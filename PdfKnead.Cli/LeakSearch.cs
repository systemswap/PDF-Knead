using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Cli;

/// <summary>
/// <c>pdf-knead lecksuche</c>: does a word still appear anywhere in a file — after redacting, cleaning up, compressing? The search
/// covers
/// <list type="bullet">
/// <item>all objects of the cross-reference table: strings (PDFDocEncoding, UTF-16) and decoded streams (including object streams,
/// Brotli), there also as hex text (<c>&lt;4D61…&gt;</c>) — the samples of images not: they are pixel values, in which a word turns up
/// by chance;</item>
/// <item>the raw file and all Flate streams in it, including those without a cross-reference entry (old revisions, leftovers), images
/// again without their samples;</item>
/// <item>the page text via the fonts (ToUnicode, custom encodings) and the text of the annotations;</item>
/// <item>the content of attachments as what it is (office files, PDF, UTF-16 text; <see cref="AttachmentText"/>) and XMP as XML — an
/// attachment that cannot be searched is named in the report (docs/FEHLERGEDAECHTNIS.md, No. 699).</item>
/// </list>
/// Also reported: objects that are not reachable from anywhere, and the file identifier /ID. Returns 1 if anything was found.
/// </summary>
internal static class LeakSearch
{
    public static int Run(string path, IReadOnlyList<string> words, string? password, bool quiet = false)
    {
        var hits = Find(File.ReadAllBytes(path), words, password, out var report);
        if (!quiet || hits.Count > 0)
        {
            Console.WriteLine($"{Path.GetFileName(path)}: {report}");
            foreach (var hit in hits) Console.WriteLine("  FUND " + hit);
        }
        return hits.Count > 0 ? 1 : 0;
    }

    /// <summary>Finds the occurrences of <paramref name="words"/> in a file.</summary>
    /// <param name="data">The file content.</param>
    /// <param name="words">Words to search for (case-insensitive; empty ones are ignored).</param>
    /// <param name="password">Password for encrypted files, or null.</param>
    /// <param name="report">Short report (unreachable objects, /ID).</param>
    /// <param name="moreSyntaxNames">Further names that are syntax in this file (the names its content uses, the standard vocabulary of its
    /// structure): a word met only inside one of them as a whole name token is not counted — never inside a string (docs/FEHLERGEDAECHTNIS.md,
    /// No. 803; before, the redaction check excused a word found as part of any such name, also where it stood in /Alt).</param>
    /// <returns>The occurrences, described in readable form.</returns>
    public static List<string> Find(byte[] data, IReadOnlyList<string> words, string? password, out string report, IReadOnlySet<string>? moreSyntaxNames = null)
    {
        var hits = new List<string>();
        var needles = words.Where(w => w.Length > 0).Select(w => (Word: w, Forms: Forms(w))).ToList();
        void Search(string where, ReadOnlySpan<byte> bytes, bool names = true)
        {
            foreach (var (word, forms) in needles)
                foreach (var (name, form) in forms)
                    if (Occurs(bytes, form, names ? moreSyntaxNames ?? Empty : null))
                    {
                        hits.Add($"„{word}“ ({name}) in {where}");
                        break;
                    }
        }
        void SearchText(string where, string text)
        {
            foreach (var (word, _) in needles)
                if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) hits.Add($"„{word}“ in {where}");
        }

        // 1. Raw file and every Flate stream in it, whether in the cross-reference table or not — except the samples of images: pixel
        // values, in which a word turns up by chance (a photo blacked out losslessly held "funds" among 2 million bytes)
        Search("der rohen Datei", data);
        foreach (var (offset, inflated, image) in RawStreams(data))
            if (!image) Search($"rohem Stream bei Byte {offset}", inflated);

        using var doc = PdfDocument.Open(data, password);
        // 2. All objects of the cross-reference table
        var size = (int)Math.Clamp(doc.Trailer.GetInteger(PdfName.Size) ?? 0, 0, 2_000_000);
        var present = new HashSet<int>();
        var unsearchable = new SortedSet<int>();   // attachments whose content could not be searched: said so, never counted as clean
        var embedded = new SortedSet<int>();
        for (var number = 1; number < size; number++)
        {
            PdfObject obj;
            try
            {
                obj = doc.GetObject(number);
            }
            catch (PdfException)
            {
                continue;
            }
            if (obj is PdfNull) continue;
            present.Add(number);
            // the files a file specification embeds (/Type /EmbeddedFile is optional, ISO 32000-2, 7.11.4)
            if (obj is PdfDictionary spec && spec.GetDictionary(PdfName.Get("EF")) is { } files)
                foreach (var (_, value) in files.Entries)
                    if (value is PdfReference file) embedded.Add(file.Id.Number);
            foreach (var s in Strings(obj)) SearchText($"Objekt {number} (String)", s.ToText());
            foreach (var s in Strings(obj)) Search($"Objekt {number} (String-Bytes)", s.Bytes, names: false);   // a string is text, never syntax
            if (obj is PdfStream stream)
            {
                byte[] decoded;
                try
                {
                    decoded = doc.DecodeStream(stream).Data;
                }
                catch (Exception e) when (e is PdfException or InvalidDataException)
                {
                    continue;
                }
                if (stream.Dictionary.GetName(PdfName.Subtype)?.Value != "Image") Search($"Objekt {number} (Stream {Describe(stream.Dictionary)})", decoded);
                // the content of an attachment as what it is — an office file (ZIP with XML, a sentence split into runs), a PDF with packed
                // content, UTF-16 text — and XMP as XML (entities resolved): searched as bytes they never matched (docs/FEHLERGEDAECHTNIS.md,
                // No. 443 for the question before redacting, No. 699 here)
                var type = stream.Dictionary.GetName(PdfName.Type)?.Value;
                if (type == "EmbeddedFile") embedded.Add(number);
                else if (type == "Metadata")
                    foreach (var (word, _) in needles)
                        if (AttachmentText.XmpHas(decoded, text => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true))
                            hits.Add($"„{word}“ im XMP (Objekt {number})");
            }
        }
        foreach (var s in Strings(doc.Trailer)) SearchText("Trailer", s.ToText());
        foreach (var number in embedded)
        {
            if (doc.GetObject(number) is not PdfStream file) continue;
            byte[] content;
            try
            {
                content = doc.DecodeStream(file).Data;
            }
            catch (Exception e) when (e is PdfException or InvalidDataException)
            {
                unsearchable.Add(number);
                continue;
            }
            foreach (var (word, _) in needles)
                switch (AttachmentText.Search(content, text => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true))
                {
                    case AttachmentSearch.Hit:
                        hits.Add($"„{word}“ im Inhalt des Anhangs (Objekt {number})");
                        break;
                    case AttachmentSearch.NotSearchable:
                        unsearchable.Add(number);
                        break;
                }
        }

        // 3. Page text (via the fonts) and annotations
        for (var i = 0; i < doc.PageCount; i++)
        {
            try
            {
                SearchText($"Text von Seite {i + 1}", PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[i])).Text);
                var item = new PageItem(doc, doc.Pages[i]);
                if (item.Elements.Count > 0)
                    SearchText($"Anmerkungen von Seite {i + 1}", PageText.Create(ForeignAnnotations.Text(item, CancellationToken.None)).Text);
            }
            catch (PdfException)
            {
                // unreadable page: the raw search above has still covered it
            }
        }

        // 4. Unreachable objects and /ID
        var reachable = Reachable(doc);
        var orphans = present.Where(n => !reachable.Contains(n)).ToList();
        var id = doc.Trailer.Get(PdfName.Get("ID")) is PdfArray { Count: >= 1 } ids && ids[0] is PdfString first ? Convert.ToHexString(first.Bytes) : "keine";
        report = $"{present.Count} Objekte, {orphans.Count} unverknüpft{(orphans.Count > 0 ? $" ({string.Join(", ", orphans.Take(10))}{(orphans.Count > 10 ? " …" : "")})" : "")}, " +
                 $"{doc.PageCount} Seiten, /ID {id}{(doc.Security is not null ? ", verschlüsselt (Rohsuche nur unverschlüsselte Teile)" : "")}" +
                 (unsearchable.Count > 0 ? $", Anhänge nicht durchsuchbar: {string.Join(", ", unsearchable.Take(10))}" : "");
        return [.. hits.Distinct()];
    }

    /// <summary>The byte forms a word can take: Latin-1/PDFDocEncoding, UTF-8, UTF-16 BE/LE, hex text.</summary>
    private static List<(string, byte[])> Forms(string word)
    {
        var forms = new List<(string, byte[])>
        {
            ("Latin-1", Encoding.Latin1.GetBytes(word)),
            ("UTF-16BE", Encoding.BigEndianUnicode.GetBytes(word)),
            ("UTF-16LE", Encoding.Unicode.GetBytes(word)),
            ("Hex", Encoding.ASCII.GetBytes(Convert.ToHexString(Encoding.Latin1.GetBytes(word)))),
            ("Hex UTF-16", Encoding.ASCII.GetBytes(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(word)))),
        };
        var utf8 = Encoding.UTF8.GetBytes(word);
        if (!utf8.AsSpan().SequenceEqual(forms[0].Item2)) forms.Add(("UTF-8", utf8));
        return forms;
    }

    /// <summary>
    /// Names of the PDF syntax the program writes itself (keys and values of ISO 32000), as whole name tokens: a word met only inside one of
    /// them („ActualText“ in <c>/ActualText</c>, „Object“ in <c>/XObject</c>) is syntax, not content of the file. Any other name still counts —
    /// a named destination such as <c>/Kapitel_Mustermann</c> can hold text (docs/FEHLERGEDAECHTNIS.md, No. 617).
    /// </summary>
    private static readonly HashSet<string> SyntaxNames = new(["ActualText", "Alt", "Artifact", "Pagination", "Redaction", "Subtype", "Type",
        "XObject", "Form", "BBox", "Resources", "Annot", "Contents", "Font", "Image", "Pattern", "Properties", "Metadata", "Page", "Pages",
        "Catalog", "StructElem", "StructTreeRoot", "ObjStm", "XRef", "ExtGState", "ColorSpace", "Shading", "Filter", "FlateDecode",
        // the empty Type 3 font that stands in for a font whose text all went (its keys, Table 110; No. 803: „difference“ in /Differences)
        "Type3", "FontBBox", "FontMatrix", "CharProcs", "Encoding", "Differences", "FirstChar", "LastChar", "Widths", "ProcSet"], StringComparer.Ordinal);

    private static readonly HashSet<string> Empty = [];

    /// <summary>Whether <paramref name="form"/> occurs in <paramref name="bytes"/> other than inside a name of <see cref="SyntaxNames"/>.</summary>
    /// <param name="more">Further syntax names of the file; null: no name is excused (the bytes are a string's).</param>
    private static bool Occurs(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> form, IReadOnlySet<string>? more)
    {
        for (var from = 0; from <= bytes.Length - form.Length;)
        {
            var at = IndexOfIgnoreCase(bytes[from..], form);
            if (at < 0) return false;
            at += from;
            if (more is null || !InSyntaxName(bytes, at, form.Length, more)) return true;
            from = at + 1;
        }
        return false;
    }

    /// <summary>Whether the bytes at <paramref name="at"/> lie inside a name token (/…) that is one of <see cref="SyntaxNames"/>.</summary>
    private static bool InSyntaxName(ReadOnlySpan<byte> bytes, int at, int length, IReadOnlySet<string> more)
    {
        static bool Regular(byte b) => b > 32 && b < 127 && "()<>[]{}/%".IndexOf((char)b) < 0;
        var start = at;
        while (start > 0 && Regular(bytes[start - 1]) && at - start < 64) start--;
        if (start == 0 || bytes[start - 1] != (byte)'/') return false;
        var end = at + length;
        while (end < bytes.Length && Regular(bytes[end]) && end - start < 64) end++;
        if (end - start > 64) return false;
        var token = Encoding.ASCII.GetString(bytes[start..end]);
        return SyntaxNames.Contains(token) || more.Contains(token);
    }

    /// <summary>Byte search, case-insensitive for ASCII letters.</summary>
    private static int IndexOfIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        static byte Lower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
        var first = Lower(needle[0]);
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (Lower(haystack[i]) != first) continue;
            var k = 1;
            while (k < needle.Length && Lower(haystack[i + k]) == Lower(needle[k])) k++;
            if (k == needle.Length) return i;
        }
        return -1;
    }

    /// <summary>All strings of an object (recursively in dictionaries and arrays, not following references).</summary>
    private static IEnumerable<PdfString> Strings(PdfObject obj)
    {
        switch (obj)
        {
            case PdfString s:
                yield return s;
                break;
            case PdfStream stream:
                foreach (var s in Strings(stream.Dictionary)) yield return s;
                break;
            case PdfDictionary dict:
                foreach (var (_, value) in dict.Entries)
                    foreach (var s in Strings(value)) yield return s;
                break;
            case PdfArray array:
                foreach (var item in array)
                    foreach (var s in Strings(item)) yield return s;
                break;
        }
    }

    private static string Describe(PdfDictionary dict) =>
        string.Join(" ", new[] { dict.GetName(PdfName.Type)?.Value, dict.GetName(PdfName.Subtype)?.Value }.Where(n => n is not null)) is { Length: > 0 } s ? s : "ohne Typ";

    /// <summary>Every "stream … endstream" block of the raw file, decompressed with zlib (at most about 64 MB per stream); blocks that do not decompress are skipped.</summary>
    /// <summary>Every Flate stream of the raw file: where its data begins, the inflated data, and whether its dictionary says it is an image.</summary>
    private static IEnumerable<(long, byte[], bool)> RawStreams(byte[] data)
    {
        var marker = "stream"u8.ToArray();
        var end = "endstream"u8.ToArray();
        for (var i = data.AsSpan().IndexOf(marker); i >= 0;)
        {
            var start = i + marker.Length;
            if (start < data.Length && data[start] == '\r') start++;
            if (start < data.Length && data[start] == '\n') start++;
            var stop = data.AsSpan(start).IndexOf(end);
            if (stop < 0) yield break;
            // "endstream" itself contains "stream": do not take it as a start
            if (i < marker.Length + 3 || !data.AsSpan(i - 3, 3).SequenceEqual("end"u8))
            {
                var inflated = Inflate(data, start, stop);
                if (inflated is not null) yield return (start, inflated, IsImage(data, i));
            }
            var next = data.AsSpan(start + stop + end.Length).IndexOf(marker);
            i = next < 0 ? -1 : start + stop + end.Length + next;
        }
    }

    /// <summary>Whether the dictionary before the stream keyword at <paramref name="at"/> (back to its "obj", at most 4 KB) names an image.</summary>
    private static bool IsImage(byte[] data, int at)
    {
        var from = Math.Max(0, at - 4096);
        var head = data.AsSpan(from, at - from);
        var obj = head.LastIndexOf(" obj"u8);
        if (obj >= 0) head = head[obj..];
        var subtype = head.IndexOf("/Subtype"u8);
        if (subtype < 0) return false;
        var rest = head[(subtype + 8)..];
        while (rest.Length > 0 && rest[0] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') rest = rest[1..];
        return rest.StartsWith("/Image"u8);
    }

    /// <summary>
    /// The stream inflated: as zlib, and where that gives nothing (a header RFC 1950 does not allow, or none) as raw Deflate from its first
    /// and from its third byte. No reader shows such a stream, and PDF-Knead does not either (docs/FEHLERGEDAECHTNIS.md, No. 937) — but its
    /// words can be recovered from the file, and the leak search looks for what is in the file, not only for what is shown (No. 947).
    /// </summary>
    private static byte[]? Inflate(byte[] data, int start, int length)
    {
        if (length < 3) return null;
        return Read(new ZLibStream(new MemoryStream(data, start, length), CompressionMode.Decompress))
            ?? Read(new DeflateStream(new MemoryStream(data, start, length), CompressionMode.Decompress))
            ?? Read(new DeflateStream(new MemoryStream(data, start + 2, length - 2), CompressionMode.Decompress));
    }

    /// <summary>Reads an inflater to its end (at most about 64 MB); what came before damaged data is kept. Null if nothing came.</summary>
    private static byte[]? Read(Stream inflater)
    {
        using var output = new MemoryStream();
        try
        {
            using (inflater)
            {
                var buffer = new byte[81920];
                int n;
                while ((n = inflater.Read(buffer)) > 0)
                {
                    output.Write(buffer, 0, n);
                    if (output.Length > 64 << 20) break;
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            // not deflated (CCITT, JPEG …), or damaged further on: depending on its bytes the inflater reports invalid data or an error of its
            // own — what it gave before stays searchable
        }
        return output.Length > 0 ? output.ToArray() : null;
    }

    /// <summary>Object numbers reachable from the trailer via references (object streams and cross-reference streams count as reachable).</summary>
    private static HashSet<int> Reachable(PdfDocument doc)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<PdfObject>();
        stack.Push(doc.Trailer);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case PdfReference r:
                    if (!seen.Add(r.Id.Number)) break;
                    try
                    {
                        stack.Push(doc.GetObject(r.Id.Number));
                    }
                    catch (PdfException)
                    {
                    }
                    break;
                case PdfStream s:
                    stack.Push(s.Dictionary);
                    break;
                case PdfDictionary d:
                    foreach (var (_, v) in d.Entries) stack.Push(v);
                    break;
                case PdfArray a:
                    foreach (var v in a) stack.Push(v);
                    break;
            }
        }
        // Structural objects of the file itself (object streams, cross-reference streams) are not "unreachable"
        var size = (int)Math.Clamp(doc.Trailer.GetInteger(PdfName.Size) ?? 0, 0, 2_000_000);
        for (var n = 1; n < size; n++)
        {
            if (seen.Contains(n)) continue;
            try
            {
                if (doc.GetObject(n) is PdfStream s && (s.Dictionary.IsType(PdfName.ObjStm) || s.Dictionary.IsType(PdfName.XRef))) seen.Add(n);
            }
            catch (PdfException)
            {
            }
        }
        return seen;
    }
}
