using System.IO.Compression;
using System.Text;
using System.Xml;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>The outcome of searching data that is not page content for redacted text.</summary>
public enum AttachmentSearch
{
    /// <summary>Searched; the text is not there.</summary>
    NoHit,

    /// <summary>The text is there.</summary>
    Hit,

    /// <summary>The data could not be searched (unknown binary data, too large, locked, damaged): it must be reported all the same.</summary>
    NotSearchable,
}

/// <summary>
/// Searches the content of an embedded file and XMP packets for redacted text, for the question before redacting
/// (docs/FEHLERGEDAECHTNIS.md, No. 443). An attachment is often the source of the whole document (office files are ZIP archives with XML
/// inside, a sentence split into runs), an original PDF with packed content, or a table exported as UTF-16; searched as raw bytes they
/// never matched, and the question said nothing. What cannot be searched is said so, never silently counted as clean.
/// </summary>
public static class AttachmentText
{
    /// <summary>Stored (encoded) length of an embedded file that is read at all; larger ones are not searchable.</summary>
    public const long MaxStored = 32 << 20;

    // limits for archives: unpacked in all, entries, packing ratio of an entry (a bomb), and pages of an embedded PDF
    private const long MaxUnpacked = 64 << 20;
    private const int MaxEntries = 2000;
    private const int MaxRatio = 100;
    private const int MaxPages = 500;
    private const int MaxDepth = 2;
    private const int MaxObjects = 200_000;

    // entries of an archive read as text (office files, OpenDocument, exports)
    private static readonly string[] TextEntries = [".xml", ".txt", ".csv", ".tsv", ".htm", ".html", ".xhtml", ".rels", ".json", ".md", ".rtf"];

    /// <summary>Searches the data of an embedded file (decoded from its PDF filters).</summary>
    /// <param name="data">The bytes of the file.</param>
    /// <param name="has">Whether a text contains redacted text.</param>
    /// <returns>Hit, no hit, or not searchable.</returns>
    public static AttachmentSearch Search(byte[] data, Func<string?, bool> has) => Search(data, has, 0);

    private static AttachmentSearch Search(byte[] data, Func<string?, bool> has, int depth)
    {
        if (data.Length == 0) return AttachmentSearch.NoHit;
        try
        {
            if (data.AsSpan().StartsWith("PK\x03\x04"u8)) return Zip(data, has, depth);
            if (data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf("%PDF-"u8) >= 0) return Pdf(data, has, depth);
            if (Decode(data) is { } text) return has(text) || has(XmlText(data)) ? AttachmentSearch.Hit : AttachmentSearch.NoHit;
            // binary of an unknown kind: a name in plain bytes is still found, otherwise it cannot be said
            return has(Encoding.Latin1.GetString(data)) ? AttachmentSearch.Hit : AttachmentSearch.NotSearchable;
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IOException or XmlException or ArgumentException
                                      or InvalidOperationException or IndexOutOfRangeException or NotSupportedException)
        {
            return AttachmentSearch.NotSearchable;
        }
    }

    /// <summary>
    /// The text of an XMP packet: every text node and attribute value (not namespace declarations, not rdf:about), entities resolved, the
    /// encoding by its byte order mark — so „Mustermann &amp;amp; Söhne“ is found and a tag name like „title“ is no hit; null if it is not
    /// well-formed XML (then search the raw text).
    /// </summary>
    /// <param name="data">The packet as stored (decoded).</param>
    public static string? XmpText(byte[] data)
    {
        if (PdfAXmp.Read(data) is not { } xml) return null;
        var text = new StringBuilder();
        Append(xml, text);
        return text.ToString();

        static void Append(XmlNode node, StringBuilder text)
        {
            if (node is XmlCharacterData { NodeType: XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace } data)
                text.Append(data.Value).Append('\n');
            if (node.Attributes is { } attributes)
                foreach (XmlAttribute a in attributes)
                    if (a.Prefix != "xmlns" && a.Name != "xmlns" && !(a.LocalName == "about" && a.NamespaceURI.Contains("rdf-syntax", StringComparison.Ordinal)))
                        text.Append(a.Value).Append('\n');
            foreach (XmlNode child in node.ChildNodes) Append(child, text);
        }
    }

    /// <summary>Whether XMP contains redacted text: as XML if it can be read, otherwise as raw text.</summary>
    /// <param name="data">The packet as stored (decoded).</param>
    /// <param name="has">Whether a text contains redacted text.</param>
    public static bool XmpHas(byte[] data, Func<string?, bool> has) =>
        XmpText(data) is { } text ? has(text) : has(Decode(data) ?? Encoding.UTF8.GetString(data));

    /// <summary>The bytes as text if they look like text: a byte order mark (UTF-8, UTF-16), UTF-16 without one, or few control bytes.</summary>
    private static string? Decode(byte[] data)
    {
        var span = data.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return Encoding.Unicode.GetString(data, 2, (data.Length - 2) & ~1);
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Encoding.BigEndianUnicode.GetString(data, 2, (data.Length - 2) & ~1);
        var sample = span[..Math.Min(span.Length, 64 << 10)];
        int control = 0, evenZero = 0, oddZero = 0;
        for (var i = 0; i < sample.Length; i++)
        {
            var b = sample[i];
            if (b == 0)
            {
                if ((i & 1) == 0) evenZero++;
                else oddZero++;
            }
            else if (b < 0x09 || b is > 0x0D and < 0x20 and not 0x1B) control++;
        }
        var half = Math.Max(1, sample.Length / 2);
        // UTF-16 without a byte order mark: (nearly) every other byte zero, as Latin text is
        if (oddZero > half * 0.6 && evenZero < half * 0.05) return Encoding.Unicode.GetString(data, 0, data.Length & ~1);
        if (evenZero > half * 0.6 && oddZero < half * 0.05) return Encoding.BigEndianUnicode.GetString(data, 0, data.Length & ~1);
        if (evenZero + oddZero > 0 || control > sample.Length / 100) return null;
        var utf8 = new UTF8Encoding(false, true);
        try
        {
            return utf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }

    /// <summary>The text of an XML document, the runs of a paragraph joined without a gap (an office file splits a sentence into runs).</summary>
    private static string? XmlText(byte[] data)
    {
        try
        {
            StringBuilder text = new(), attributes = new();
            using var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxUnpacked, IgnoreComments = false,
            });
            while (reader.Read())
                switch (reader.NodeType)
                {
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                        text.Append(reader.Value);
                        break;
                    case XmlNodeType.Comment:
                        text.Append('\n').Append(reader.Value).Append('\n');
                        break;
                    case XmlNodeType.Element:
                        // a tab, a line break, a cell: a gap; attribute values (a field result, a title) on lines of their own after the
                        // text, so a run's attributes do not split the sentence (not namespaces, not xml:space or xml:lang)
                        if (reader.LocalName is "tab" or "br" or "cr") text.Append(' ');
                        if (reader.HasAttributes)
                        {
                            while (reader.MoveToNextAttribute())
                                if (reader.Prefix is not ("xmlns" or "xml") && reader.Name != "xmlns") attributes.Append(reader.Value).Append('\n');
                            reader.MoveToElement();
                        }
                        break;
                    case XmlNodeType.EndElement when reader.LocalName is "p" or "h" or "tr" or "row" or "si" or "c" or "td" or "th" or "li" or "div" or "table-cell":
                        text.Append('\n');
                        break;
                }
            return text.Append('\n').Append(attributes).ToString();
        }
        catch (Exception e) when (e is XmlException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A ZIP archive (office files, OpenDocument): its text entries, nested archives and PDFs one level deeper, within limits.</summary>
    private static AttachmentSearch Zip(byte[] data, Func<string?, bool> has, int depth)
    {
        using var archive = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxEntries) return AttachmentSearch.NotSearchable;
        var result = AttachmentSearch.NoHit;
        long unpacked = 0;
        foreach (var entry in archive.Entries)
        {
            if (has(entry.FullName)) return AttachmentSearch.Hit;
            var name = entry.FullName.ToLowerInvariant();
            var text = TextEntries.Any(name.EndsWith);
            var nested = name.EndsWith(".pdf") || name.EndsWith(".zip") || name.EndsWith(".docx") || name.EndsWith(".xlsx") || name.EndsWith(".pptx")
                         || name.EndsWith(".odt") || name.EndsWith(".ods") || name.EndsWith(".odp") || name.EndsWith(".bin");
            if (!text && !nested) continue;   // pictures and fonts of an office file: no text to search (as on a page)
            if (entry.Length > MaxUnpacked || entry.Length > (long)MaxRatio * Math.Max(1024, entry.CompressedLength)) return AttachmentSearch.NotSearchable;
            unpacked += entry.Length;
            if (unpacked > MaxUnpacked) return AttachmentSearch.NotSearchable;
            var bytes = Read(entry);
            if (bytes is null) return AttachmentSearch.NotSearchable;
            if (text)
            {
                if (has(Decode(bytes) ?? Encoding.UTF8.GetString(bytes)) || has(XmlText(bytes))) return AttachmentSearch.Hit;
                continue;
            }
            if (depth >= MaxDepth)
            {
                result = AttachmentSearch.NotSearchable;
                continue;
            }
            switch (Search(bytes, has, depth + 1))
            {
                case AttachmentSearch.Hit:
                    return AttachmentSearch.Hit;
                case AttachmentSearch.NotSearchable:
                    result = AttachmentSearch.NotSearchable;
                    break;
            }
        }
        return result;
    }

    /// <summary>An entry unpacked, at most as long as it says (a header that lies about its length counts as damaged).</summary>
    private static byte[]? Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var buffer = new byte[entry.Length];
        var read = 0;
        while (read < buffer.Length && stream.Read(buffer, read, buffer.Length - read) is var n and > 0) read += n;
        return read == buffer.Length && stream.ReadByte() < 0 ? buffer : null;
    }

    /// <summary>
    /// An embedded PDF: the text of its pages (at most 500), every string of its objects (properties, bookmarks, comments, fields), its
    /// XMP and its own attachments one level deeper. Encrypted with a password it is not searchable.
    /// </summary>
    private static AttachmentSearch Pdf(byte[] data, Func<string?, bool> has, int depth)
    {
        using var doc = PdfDocument.Open(data);
        var result = AttachmentSearch.NoHit;
        var pages = doc.Pages;
        for (var i = 0; i < pages.Count && i < MaxPages; i++)
        {
            // both readings of a page whose font a reference reader shows otherwise (docs/FEHLERGEDAECHTNIS.md, No. 1235)
            var text = PageText.Create(PageInterpreter.BuildText(doc, pages[i]));
            if (has(text.Text) || text.Alternate is { } alternate && has(alternate.Text)) return AttachmentSearch.Hit;
        }
        if (pages.Count > MaxPages) result = AttachmentSearch.NotSearchable;
        var highest = Math.Min(doc.HighestObjectNumber, MaxObjects);
        for (var n = 1; n <= highest; n++)
        {
            PdfObject obj;
            try
            {
                obj = doc.GetObject(n);
            }
            catch (PdfException)
            {
                continue;   // a free or unreadable entry
            }
            switch (obj)
            {
                case PdfStream stream when stream.Dictionary.IsType(PdfName.Metadata):
                    if (XmpHas(doc.GetStreamData(stream), has)) return AttachmentSearch.Hit;
                    break;
                case PdfStream stream when stream.Dictionary.IsType(PdfName.Get("EmbeddedFile")):
                    if (depth >= MaxDepth || stream.DataLength > MaxStored)
                    {
                        result = AttachmentSearch.NotSearchable;
                        break;
                    }
                    switch (Search(doc.GetStreamData(stream), has, depth + 1))
                    {
                        case AttachmentSearch.Hit:
                            return AttachmentSearch.Hit;
                        case AttachmentSearch.NotSearchable:
                            result = AttachmentSearch.NotSearchable;
                            break;
                    }
                    break;
                case PdfStream stream:
                    if (Strings(stream.Dictionary, has, 0)) return AttachmentSearch.Hit;
                    break;
                default:
                    if (Strings(obj, has, 0)) return AttachmentSearch.Hit;
                    break;
            }
        }
        return result;
    }

    // the strings of an object and its direct parts (other objects are visited on their own); names are no text and would match words
    private static bool Strings(PdfObject? value, Func<string?, bool> has, int depth) => value switch
    {
        PdfString s => has(s.ToText()),
        PdfDictionary d when depth < 4 => d.Entries.Take(1000).Any(e => Strings(d.GetRaw(e.Key) is PdfReference ? null : d.Get(e.Key), has, depth + 1)),
        PdfArray a when depth < 4 => Enumerable.Range(0, Math.Min(a.Count, 1000)).Any(i => Strings(a[i] is PdfReference ? null : a.Get(i), has, depth + 1)),
        _ => false,
    };
}
