using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Parsing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// File structure (ISO 32000-2, 7.5; Konformitätsabgleich M3, docs/normabgleich/M3-dateiaufbau.md): header, body, cross-reference tables and
/// streams, trailer, incremental updates, object streams. Nachweise „FS.“ in the report.
/// </summary>
public sealed class FileStructureNormTests
{
    /// <summary>A file of catalogue and page tree with a cross-reference stream; <paramref name="entries"/> entries in all.</summary>
    /// <param name="entries">Number of entries: the first four real (0 free, 1 catalogue, 2 pages, 3 the stream itself), the rest free.</param>
    /// <param name="index">/Index of the stream; null: none (0 to /Size).</param>
    /// <param name="prev">Whether the stream names an older section (/Prev, here itself: the chain stops at the loop).</param>
    private static byte[] XRefStreamFile(long entries, string? index = null, bool prev = false)
    {
        var output = new MemoryStream();
        output.Write("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"u8);
        var offsets = new List<long>();
        foreach (var body in new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>" })
        {
            offsets.Add(output.Position);
            output.Write(Encoding.ASCII.GetBytes($"{offsets.Count} 0 obj\n{body}\nendobj\n"));
        }
        var at = output.Position;
        // W [1 1 0]: two bytes per entry; type 0 free; type 1 with offsets below 256 does not fit — the real ones get W [1 4 1] below
        var data = new MemoryStream();
        using (var z = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write([0, 0, 0, 0, 0, 0xFF]);
            foreach (var o in offsets) z.Write([1, (byte)(o >> 24), (byte)(o >> 16), (byte)(o >> 8), (byte)o, 0]);
            z.Write([1, (byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at, 0]);
            var zeros = new byte[1 << 20];
            for (var left = (entries - 4) * 6; left > 0; left -= zeros.Length) z.Write(zeros, 0, (int)Math.Min(zeros.Length, left));
        }
        var dict = $"<< /Type /XRef /Size {entries} /W [1 4 1] /Root 1 0 R /Filter /FlateDecode /Length {data.Length}" +
                   (index is null ? "" : $" /Index [{index}]") + (prev ? $" /Prev {at}" : "") + " >>";
        output.Write(Encoding.ASCII.GetBytes($"3 0 obj\n{dict}\nstream\n"));
        output.Write(data.ToArray());
        output.Write(Encoding.ASCII.GetBytes($"\nendstream\nendobj\nstartxref\n{at}\n%%EOF\n"));
        return output.ToArray();
    }

    /// <summary>
    /// A file of catalogue (1), page tree (2) and one page (3), plus object streams (4, 5, …) holding the given objects as they stand — the
    /// caller decides about white space between them — followed by <paramref name="padding"/> spaces; cross-reference stream last.
    /// </summary>
    /// <param name="streams">Per object stream its objects: number and text.</param>
    /// <param name="padding">Spaces after the objects of each stream (for its decoded size).</param>
    /// <param name="preamble">Bytes before "%PDF-"; offsets count from the header (ISO 32000-2, 7.5.2).</param>
    /// <param name="trailerExtra">Further entries of the cross-reference stream dictionary.</param>
    private static byte[] FileWithObjectStreams(IReadOnlyList<(int Number, string Text)[]> streams, int padding = 0, string preamble = "",
        string trailerExtra = "")
    {
        var output = new MemoryStream();
        void Text(string s) => output.Write(Encoding.Latin1.GetBytes(s));
        Text("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
        var offsets = new SortedDictionary<int, long>();
        var compressed = new SortedDictionary<int, (int Stream, int Index)>();
        void Obj(int number, string body)
        {
            offsets[number] = output.Position;
            Text($"{number} 0 obj\n{body}\nendobj\n");
        }
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        for (var s = 0; s < streams.Count; s++)
        {
            var header = new StringBuilder();
            var body = new StringBuilder();
            for (var i = 0; i < streams[s].Length; i++)
            {
                header.Append($"{streams[s][i].Number} {body.Length} ");
                body.Append(streams[s][i].Text);
                compressed[streams[s][i].Number] = (4 + s, i);
            }
            var data = new MemoryStream();
            using (var z = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(Encoding.Latin1.GetBytes(header.ToString() + body));
                var spaces = new byte[1 << 16];
                Array.Fill(spaces, (byte)' ');
                for (var left = padding; left > 0; left -= spaces.Length) z.Write(spaces, 0, Math.Min(left, spaces.Length));
            }
            offsets[4 + s] = output.Position;
            Text($"{4 + s} 0 obj\n<< /Type /ObjStm /N {streams[s].Length} /First {header.Length} /Filter /FlateDecode /Length {data.Length} >>\nstream\n");
            output.Write(data.ToArray());
            Text("\nendstream\nendobj\n");
        }
        var xref = 4 + streams.Count;
        var size = Math.Max(xref, compressed.Count == 0 ? 0 : compressed.Keys.Max()) + 1;
        offsets[xref] = output.Position;
        var rows = new MemoryStream();
        for (var n = 0; n < size; n++)
        {
            if (offsets.TryGetValue(n, out var at)) rows.Write([1, (byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at, 0, 0]);
            else if (compressed.TryGetValue(n, out var c)) rows.Write([2, 0, 0, (byte)(c.Stream >> 8), (byte)c.Stream, (byte)(c.Index >> 8), (byte)c.Index]);
            else rows.Write([0, 0, 0, 0, 0, 0xFF, 0xFF]);
        }
        Text($"{xref} 0 obj\n<< /Type /XRef /Size {size} /W [1 4 2] /Root 1 0 R{trailerExtra} /Length {rows.Length} >>\nstream\n");
        output.Write(rows.ToArray());
        Text($"\nendstream\nendobj\nstartxref\n{offsets[xref]}\n%%EOF\n");
        return [.. Encoding.Latin1.GetBytes(preamble), .. output.ToArray()];
    }

    // ---- Writer: 7.5.2 to 7.5.8 as written by PDF-Knead

    /// <summary>The trailer of a written file as the reader sees it, and the offset after „startxref“.</summary>
    private static (string Text, long StartXRef) Tail(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var match = System.Text.RegularExpressions.Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$");
        Assert.True(match.Success, "the file ends with startxref, its offset and %%EOF, one per line (7.5.5)");
        return (text, long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A file written with a classic table (PDF 1.4): header and binary comment (7.5.2), one subsection from 0, every entry 20 bytes with the
    /// offset of its object, object 0 head of the free list with 65535 (7.5.4), trailer with direct /Size one above the highest number, /Root
    /// indirect and an /ID of two strings of at least 16 bytes (7.5.5).
    /// </summary>
    [Fact]
    public void Writer_ClassicTable_HeaderEntriesTrailer()
    {
        using var doc = PdfDocument.Open(FileWithObjectStreams([[(10, "<< /K 1 >>")]]));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "1.4" });
        var bytes = output.ToArray();
        var (text, startxref) = Tail(bytes);
        Assert.StartsWith("%PDF-1.4\n%", text);
        Assert.All(bytes[10..14], b => Assert.True(b >= 128));
        Assert.StartsWith("xref\n0 ", text[(int)startxref..]);
        var head = System.Text.RegularExpressions.Regex.Match(text[(int)startxref..], @"^xref\n0 (\d+)\n");
        var count = int.Parse(head.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var entries = text.Substring((int)startxref + head.Length, count * 20);
        Assert.StartsWith("0000000000 65535 f", entries);
        for (var i = 1; i < count; i++)
        {
            var entry = entries.Substring(i * 20, 20);
            Assert.Matches(@"^\d{10} 00000 n( \n| \r|\r\n)$", entry);
            Assert.StartsWith($"{i} 0 obj", text[int.Parse(entry[..10], System.Globalization.CultureInfo.InvariantCulture)..]);
        }
        Assert.StartsWith("trailer\n", text[((int)startxref + head.Length + count * 20)..]);
        using var saved = PdfDocument.Open(bytes);
        Assert.Equal(count, Assert.IsType<PdfInteger>(saved.Trailer.GetRaw(PdfName.Size)).Value);
        Assert.IsType<PdfReference>(saved.Trailer.GetRaw(PdfName.Root));
        var id = Assert.IsType<PdfArray>(saved.Trailer.GetRaw(PdfName.ID));
        Assert.Equal(2, id.Count);
        Assert.All(id, s => Assert.True(Assert.IsType<PdfString>(s).Bytes.Length >= 16));
    }

    /// <summary>
    /// A file written with object streams (PDF 1.7): startxref names the cross-reference stream (7.5.8.1), its dictionary has /Type /XRef,
    /// /Size, a direct /W of three, no crypt filter, and an entry for itself (7.5.8.2, 7.5.8.3); every object stream has /Type /ObjStm, /N
    /// and /First, its header N pairs with rising offsets, the first object right after the header (7.5.7), and holds no stream, no object of
    /// another generation and not the encryption dictionary.
    /// </summary>
    [Fact]
    public void Writer_XRefStreamAndObjectStreams()
    {
        using var doc = PdfDocument.Open(FileWithObjectStreams([[(10, "<< /K 1 >>"), (11, "[1 2 3]")]]));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "1.7" });
        var bytes = output.ToArray();
        var (text, startxref) = Tail(bytes);
        Assert.Matches(@"^\d+ 0 obj", text[(int)startxref..]);
        Assert.DoesNotContain("\nxref\n", text);
        Assert.DoesNotContain("\ntrailer", text);

        using var source = PdfSource.FromBytes(bytes);
        var xref = new PdfParser(new PdfLexer(source)).ParseIndirectObject(startxref)!.Value;
        var dict = Assert.IsType<PdfStream>(xref.Value).Dictionary;
        Assert.True(dict.IsType(PdfName.XRef));
        Assert.IsType<PdfInteger>(dict.GetRaw(PdfName.Size));
        var w = Assert.IsType<PdfArray>(dict.GetRaw(PdfName.W));
        Assert.Equal(3, w.Count);
        Assert.All(w, e => Assert.IsType<PdfInteger>(e));
        Assert.NotEqual(0d, w.GetNumber(1) ?? 0);
        Assert.DoesNotContain("Crypt", dict.GetRaw(PdfName.Filter)?.ToString() ?? "");
        var table = XRefReader.Read(source, 0);
        Assert.Equal(new XRefEntry(XRefEntryType.InFile, startxref, 0), table.Entries[xref.Id.Number]);

        using var saved = PdfDocument.Open(bytes);
        var streams = table.Entries.Values.Where(e => e.Type == XRefEntryType.Compressed).Select(e => (int)e.Offset).Distinct().ToList();
        Assert.NotEmpty(streams);
        foreach (var number in streams)
        {
            var stm = Assert.IsType<PdfStream>(saved.GetObject(number));
            Assert.True(stm.Dictionary.IsType(PdfName.ObjStm));
            var n = (int)stm.Dictionary.GetInteger(PdfName.N)!;
            var first = (int)stm.Dictionary.GetInteger(PdfName.First)!;
            var data = Encoding.Latin1.GetString(saved.DecodeStream(stm).Data);
            var pairs = data[..first].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToList();
            Assert.Equal(2 * n, pairs.Count);
            var offsets = pairs.Where((_, i) => i % 2 == 1).ToList();
            Assert.Equal(0, offsets[0]);   // the first object right after the header
            Assert.Equal(offsets.Order(), offsets);
            Assert.True(char.IsWhiteSpace(data[first - 1]));
            foreach (var objectNumber in pairs.Where((_, i) => i % 2 == 0))
                Assert.IsNotType<PdfStream>(saved.GetObject((int)objectNumber));
        }
    }

    /// <summary>
    /// An appended update (7.5.6): the original bytes unchanged, a section only for what changed, the trailer with /Prev to the previous
    /// section and its own %%EOF — once after a classic table (entries of 20 bytes), once after a cross-reference stream (a stream again).
    /// </summary>
    [Fact]
    public void IncrementalUpdate_AppendedSection()
    {
        using var doc = PdfDocument.Open(FileWithObjectStreams([[(10, "<< /K 1 >>")]]));
        foreach (var version in new[] { "1.4", "1.7" })
        {
            using var written = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), written, new PdfSaveOptions { Version = version });
            var original = written.ToArray();
            var (_, before) = Tail(original);
            using var source = PdfDocument.Open(original);
            var update = new IncrementalUpdate(source);
            var added = update.Add(new PdfDictionary());
            using var appended = new MemoryStream();
            update.WriteTo(appended);
            var bytes = appended.ToArray();
            Assert.Equal(original, bytes[..original.Length]);
            var (text, startxref) = Tail(bytes);
            Assert.True(startxref > original.Length);
            using var after = PdfDocument.Open(bytes);
            Assert.Equal(before, Assert.IsType<PdfInteger>(after.Trailer.GetRaw(PdfName.Prev)).Value);
            Assert.IsType<PdfDictionary>(after.GetObject(added.Id.Number));
            if (version == "1.4")
            {
                var section = text[(int)startxref..];
                Assert.Matches(@"^xref\n\d+ \d+\n\d{10} 00000 n\r\n", section);
            }
            else
            {
                Assert.Matches(@"^\d+ 0 obj", text[(int)startxref..]);
            }
        }
    }

    /// <summary>A reader accepts the headers %PDF-1.0 to %PDF-1.7 and %PDF-2.0 (ISO 32000-2, 7.5.2).</summary>
    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    [InlineData("1.2")]
    [InlineData("1.3")]
    [InlineData("1.4")]
    [InlineData("1.5")]
    [InlineData("1.6")]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void Header_VersionsOneZeroToTwoZero_Accepted(string version)
    {
        var bytes = FileWithObjectStreams([]);
        Encoding.ASCII.GetBytes(version).CopyTo(bytes, 5);
        using var doc = PdfDocument.Open(bytes);
        Assert.Equal(version, doc.HeaderVersion);
        Assert.Equal(1, doc.PageCount);
    }

    // ---- 7.5.7 Object streams

    /// <summary>
    /// Objects without white space between them (ISO 32000-2, 7.5.7, corrected in 2020): each ends before the next one begins. Before, a number,
    /// name or true/false ran into the following object — „12“ and „34“ were read as 1234.
    /// </summary>
    [Fact]
    public void ObjectStream_NoWhiteSpaceBetweenObjects_EachEndsAtTheNext()
    {
        using var doc = PdfDocument.Open(FileWithObjectStreams([[(10, "12"), (11, "34"), (12, "/A"), (13, "true"), (14, "false"), (15, "<</K 1>>"), (16, "null")]]));
        Assert.Equal(12, Assert.IsType<PdfKnead.Core.Pdf.Objects.PdfInteger>(doc.GetObject(10)).Value);
        Assert.Equal(34, Assert.IsType<PdfKnead.Core.Pdf.Objects.PdfInteger>(doc.GetObject(11)).Value);
        Assert.Equal("A", Assert.IsType<PdfKnead.Core.Pdf.Objects.PdfName>(doc.GetObject(12)).Value);
        Assert.True(Assert.IsType<PdfKnead.Core.Pdf.Objects.PdfBoolean>(doc.GetObject(13)).Value);
        Assert.False(Assert.IsType<PdfKnead.Core.Pdf.Objects.PdfBoolean>(doc.GetObject(14)).Value);
        Assert.Equal(1, Assert.IsType<PdfKnead.Core.Pdf.Objects.PdfDictionary>(doc.GetObject(15)).GetInteger(PdfKnead.Core.Pdf.Objects.PdfName.Get("K")));
        Assert.IsType<PdfKnead.Core.Pdf.Objects.PdfNull>(doc.GetObject(16));
    }

    /// <summary>
    /// Security (docs/FEHLERGEDAECHTNIS.md, No. 745): decoded object streams are kept within a budget; the least recently used are dropped and
    /// decoded again when one of their objects is needed. Before, every decoded stream stayed until the document was closed — a crafted file of
    /// 2.7 MB with six streams just under the decoding limit took 6.9 GB (measured with the command line).
    /// </summary>
    [Fact]
    public void ObjectStreams_KeptWithinTheBudget_DroppedOnesDecodedAgain()
    {
        const int mb = 1 << 20;
        var streams = Enumerable.Range(0, 6).Select(s => Enumerable.Range(0, 4).Select(i => (100 + 10 * s + i, $"<< /S {s} /I {i} >>\n")).ToArray()).ToList();
        using var doc = PdfDocument.Open(FileWithObjectStreams(streams, padding: 4 * mb));
        doc.ObjectStreamBudget = 10 * mb;
        for (var s = 0; s < 6; s++)
        {
            Assert.Equal(s, doc.GetObject(100 + 10 * s).AsDictionary()!.GetInteger(PdfKnead.Core.Pdf.Objects.PdfName.Get("S")));
            Assert.True(doc.ObjectStreamBytesKept <= 10 * mb, $"{doc.ObjectStreamBytesKept >> 20} MB kept");
        }
        // the first stream was dropped: its next object comes from decoding it again
        var decoded = doc.ObjectStreamBytesDecoded;
        Assert.Equal(1, doc.GetObject(101).AsDictionary()!.GetInteger(PdfKnead.Core.Pdf.Objects.PdfName.Get("I")));
        Assert.True(doc.ObjectStreamBytesDecoded > decoded);
        Assert.NotNull(doc.NewestXRefSection);   // no repair
    }

    /// <summary>
    /// Security (No. 745): a file whose object streams must be decoded over and over (they do not fit the budget, and their objects are asked
    /// for in turn) gets no more than eight budgets of decoding work; objects beyond that are missing instead of the program hanging.
    /// </summary>
    [Fact]
    public void ObjectStreams_DecodedOverAndOver_StopAtTheWorkLimit()
    {
        const int mb = 1 << 20;
        var streams = Enumerable.Range(0, 6).Select(s => Enumerable.Range(0, 20).Select(i => (100 + 100 * s + i, $"{s * 100 + i}\n")).ToArray()).ToList();
        using var doc = PdfDocument.Open(FileWithObjectStreams(streams, padding: 2 * mb));
        doc.ObjectStreamBudget = 3 * mb;   // one stream at a time; the work limit is 24 MB
        var missing = 0;
        for (var i = 0; i < 20; i++)
            for (var s = 0; s < 6; s++)
                if (doc.GetObject(100 + 100 * s + i) is not PdfKnead.Core.Pdf.Objects.PdfInteger) missing++;
        Assert.True(missing > 0);
        Assert.True(doc.ObjectStreamBytesDecoded <= 27 * mb, $"{doc.ObjectStreamBytesDecoded >> 20} MB decoded");   // 24 MB and one stream
    }

    // ---- 7.5.2 Header, 7.5.5 Trailer

    /// <summary>
    /// Bytes before "%PDF-" (ISO 32000-2, 7.5.2): offsets count from the "%". Here the bytes before are exactly as long as object 1, so object
    /// 2, looked up from the start of the file, finds object 1 — before, that made object 2 count as missing and the file was rebuilt.
    /// </summary>
    [Fact]
    public void Preamble_OffsetsCountFromTheHeader()
    {
        var plain = FileWithObjectStreams([]);
        var text = Encoding.Latin1.GetString(plain);
        var first = text.IndexOf("1 0 obj", StringComparison.Ordinal);
        var length = text.IndexOf("2 0 obj", StringComparison.Ordinal) - first;
        using var doc = PdfDocument.Open(FileWithObjectStreams([], preamble: new string('#', length)));
        Assert.Equal(1, doc.PageCount);
        Assert.DoesNotContain(doc.Problems, p => p.Contains("rekonstruiert", StringComparison.Ordinal));
    }

    /// <summary>
    /// ISO 32000-2, Table 15: each string of /ID has at least 16 bytes. A shorter first string of the source is not carried on — neither when the
    /// file is written anew nor in an appended update (docs/FEHLERGEDAECHTNIS.md, No. 746).
    /// </summary>
    [Fact]
    public void ShortSourceId_NotCarriedOn()
    {
        var file = FileWithObjectStreams([], trailerExtra: " /ID [<0102> <0102>]");
        using var doc = PdfDocument.Open(file);
        using (var output = new MemoryStream())
        {
            PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions());
            using var saved = PdfDocument.Open(output.ToArray());
            var id = saved.Trailer.GetArray(PdfKnead.Core.Pdf.Objects.PdfName.ID)!;
            Assert.All(id, s => Assert.True(((PdfKnead.Core.Pdf.Objects.PdfString)s).Bytes.Length >= 16));
        }
        var update = new IncrementalUpdate(doc);
        update.Add(new PdfKnead.Core.Pdf.Objects.PdfDictionary());
        using (var output = new MemoryStream())
        {
            update.WriteTo(output);
            using var appended = PdfDocument.Open(output.ToArray());
            var id = appended.Trailer.GetArray(PdfKnead.Core.Pdf.Objects.PdfName.ID)!;
            Assert.All(id, s => Assert.True(((PdfKnead.Core.Pdf.Objects.PdfString)s).Bytes.Length >= 16));
        }
    }

    // ---- Security (docs/FEHLERGEDAECHTNIS.md, No. 744)

    /// <summary>
    /// A cross-reference stream with millions of free entries out of a decompression bomb (here 3 million from a few kilobytes): without /Prev
    /// a free entry hides nothing and is not kept. Before every entry went into the table — a file of 114 KB with 20 million took 2.1 GB.
    /// </summary>
    [Fact]
    public void XRefStream_FreeEntriesWithoutOlderSection_NotKept()
    {
        var file = XRefStreamFile(3_000_000);
        var before = GC.GetAllocatedBytesForCurrentThread();
        using var source = PdfSource.FromBytes(file);
        var table = XRefReader.Read(source, 0);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // the decoded stream itself (18 MB, with the copies while it grows) is the most it may take
        Assert.True(allocated < 200L << 20, $"{allocated >> 20} MB");   // before: 571 MB
        Assert.Equal([1, 2, 3], table.Entries.Keys.Order());
        // still deleted, not missing (7.5.4): null without repairing the file
        Assert.True(table.IsFree(5) && table.IsFree(2_999_999) && !table.IsFree(3_000_000));
        using var doc = PdfDocument.Open(file);
        Assert.NotNull(doc.Catalog);
        Assert.IsType<PdfKnead.Core.Pdf.Objects.PdfNull>(doc.GetObject(5));
        Assert.NotNull(doc.NewestXRefSection);   // not rebuilt
        Assert.Equal(2_999_999, doc.HighestObjectNumber);
    }

    /// <summary>
    /// Hybrid file (7.5.8.4): an update whose table is empty and whose /XRefStm stream marks object 4 deleted. The stream has no /Prev of its own
    /// (that stands in the trailer of the table), yet its free entry hides object 4 of the older section — the rule of No. 744 must not lose it.
    /// </summary>
    [Fact]
    public void HybridUpdate_FreeEntryInXRefStm_HidesTheOlderObject()
    {
        var output = new MemoryStream();
        void Text(string s) => output.Write(Encoding.Latin1.GetBytes(s));
        Text("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
        var offsets = new List<long>();
        foreach (var body in new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                     "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>", "<< /Old true >>" })
        {
            offsets.Add(output.Position);
            Text($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        var main = output.Position;
        Text("xref\n0 5\n0000000000 65535 f \n");
        foreach (var o in offsets) Text($"{o:D10} 00000 n \n");
        Text($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{main}\n%%EOF\n");

        // the update: object 4 deleted (type 0, next generation 1), the stream itself as object 5
        var stream = output.Position;
        byte[] data = [0, 0, 0, 0, 0, 1, 1, (byte)(stream >> 24), (byte)(stream >> 16), (byte)(stream >> 8), (byte)stream, 0];
        Text($"5 0 obj\n<< /Type /XRef /Size 6 /W [1 4 1] /Index [4 2] /Root 1 0 R /Length {data.Length} >>\nstream\n");
        output.Write(data);
        Text("\nendstream\nendobj\n");
        var update = output.Position;
        Text($"xref\n0 0\ntrailer\n<< /Size 6 /Root 1 0 R /Prev {main} /XRefStm {stream} >>\nstartxref\n{update}\n%%EOF\n");

        using var source = PdfSource.FromBytes(output.ToArray());
        var table = XRefReader.Read(source, 0);
        Assert.Equal(XRefEntryType.Free, table.Entries[4].Type);
        using var doc = PdfDocument.Open(output.ToArray());
        Assert.IsType<PdfKnead.Core.Pdf.Objects.PdfNull>(doc.GetObject(4));
        Assert.Equal(1, doc.PageCount);
    }

    /// <summary>
    /// Object numbers beyond ISO 32000-2, Annex C (8,388,607) do not exist: their entries are passed over without being read one by one, in a
    /// stream and in a classic table; with /Prev the free entries below the limit are kept (they may hide older ones).
    /// </summary>
    [Fact]
    public void XRefNumbersBeyondAnnexC_AreMissing()
    {
        using (var source = PdfSource.FromBytes(XRefStreamFile(4 + 1_000_000, index: "0 4 9000000 1000000", prev: true)))
        {
            var table = XRefReader.Read(source, 0);
            Assert.Equal([0, 1, 2, 3], table.Entries.Keys.Order());
        }
        using (var source = PdfSource.FromBytes(XRefStreamFile(4 + 1000, index: "0 4 100 1000", prev: true)))
            Assert.Equal(1004, XRefReader.Read(source, 0).Entries.Count);

        var classic = Encoding.ASCII.GetBytes(
            "%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var xref = classic.Length;
        var text = new StringBuilder("xref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n");
        text.Append($"{Encoding.ASCII.GetString(classic).IndexOf("2 0 obj", StringComparison.Ordinal):D10} 00000 n \n");
        text.Append("8388608 2\n0000000009 00000 n \n0000000009 00000 n \n");
        text.Append($"trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        using (var source = PdfSource.FromBytes([.. classic, .. Encoding.ASCII.GetBytes(text.ToString())]))
            Assert.Equal([0, 1, 2], XRefReader.Read(source, 0).Entries.Keys.Order());
    }

    // ---- Nachweise added in the Nacharbeit (12.11.2026) for rows that stood as „umgesetzt“ without one

    /// <summary>
    /// 7.5.5: a reader reads a file from its end — the last startxref leads to the newest section, which wins over the original part, although
    /// that part is a complete file of its own with its own startxref and %%EOF (M3 row 18).
    /// </summary>
    [Fact]
    public void Reader_StartsAtTheEnd_NewestSectionWins()
    {
        var original = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>", "<< /Wert (alt) >>");
        using (var before = PdfDocument.Open(original))
            Assert.Equal("alt", before.GetObject(3)!.AsDictionary()!.GetString(PdfName.Get("Wert"))!.ToText());
        var updated = TestPdf.AppendUpdate(original, 4, 1, null, (3, TestPdf.Dict(("Wert", new PdfString("neu"u8.ToArray())))));
        using var after = PdfDocument.Open(updated);
        Assert.Equal("neu", after.GetObject(3)!.AsDictionary()!.GetString(PdfName.Get("Wert"))!.ToText());
    }

    /// <summary>
    /// 7.5.7: freed numbers may be used again only for ordinary objects, new object streams get new numbers. An appended update of PDF-Knead
    /// never uses a freed number again (here number 3, free with generation 1) and writes no object streams (M3 row 38).
    /// </summary>
    [Fact]
    public void IncrementalUpdate_NoFreedNumberUsedAgain_NoObjectStreams()
    {
        var text = Encoding.Latin1.GetString(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>", "<< /Frei true >>", "<< /Wert 1 >>"));
        // object 3 free (generation 1), the head of the free list pointing to it
        var xref = text.IndexOf("xref\n", StringComparison.Ordinal);
        var entries = text[xref..].Split('\n');
        entries[2] = "0000000003 65535 f ";
        entries[5] = "0000000000 00001 f ";
        var file = Encoding.Latin1.GetBytes(text[..xref] + string.Join('\n', entries));
        using var source = PdfDocument.Open(file);
        Assert.IsType<PdfNull>(source.GetObject(3) ?? PdfNull.Instance);
        var update = new IncrementalUpdate(source);
        var added = new[] { update.Add(new PdfDictionary()), update.Add(new PdfDictionary()) };
        Assert.All(added, r => Assert.True(r.Id.Number >= 5, $"number {r.Id.Number}"));
        using var output = new MemoryStream();
        update.WriteTo(output);
        var appended = Encoding.Latin1.GetString(output.ToArray()[file.Length..]);
        Assert.DoesNotContain("/ObjStm", appended);
        using var after = PdfDocument.Open(output.ToArray());
        Assert.True(after.Trailer.GetInteger(PdfName.Get("Size")) >= 7);
    }

    /// <summary>
    /// 7.5.5 with S-A A80: a /Size below the numbers the table lists (a common writer's fault) — the entries above it are still read, as the
    /// safe direction for redaction and cleanup (they see more than a strict reader); the written file states the right /Size (M3 row 21).
    /// </summary>
    [Fact]
    public void SizeTooSmall_EntriesAboveRead_WrittenSizeRight()
    {
        var text = Encoding.Latin1.GetString(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Extra 4 0 R >>", "<< /Type /Pages /Kids [5 0 R] /Count 1 >>", "<< /Wert 3 >>", "<< /Wert 4 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"));
        var file = Encoding.Latin1.GetBytes(text.Replace("/Size 6 /Root", "/Size 3 /Root"));
        using var doc = PdfDocument.Open(file);
        Assert.Equal(3, doc.Trailer.GetInteger(PdfName.Get("Size")));
        Assert.Equal(4, doc.GetObject(4)!.AsDictionary()!.GetInteger(PdfName.Get("Wert")));
        Assert.Equal(1, doc.PageCount);
        using var saved = DocumentStructureNormTests.Saved(file);
        var size = saved.Trailer.GetInteger(PdfName.Get("Size"))!.Value;
        Assert.True(size > saved.HighestObjectNumber, $"/Size {size}, highest number {saved.HighestObjectNumber}");
        Assert.Equal(4, saved.Catalog!.GetDictionary(PdfName.Get("Extra"))!.GetInteger(PdfName.Get("Wert")));
    }

    /// <summary>
    /// 7.5.8 with S-A A06: a cross-reference stream whose data is damaged — here one character of its ASCII85 data replaced by one outside the
    /// alphabet, so that every group after it shifts — is no readable section; the cross-references are rebuilt from the file and the page is
    /// there. Before, the shifted entries were taken as they came: the catalogue was found, the page tree not, and the file showed no page
    /// (as PDFBOX-3148-2-fuzzed.pdf of a public test collection; S-C C01; docs/FEHLERGEDAECHTNIS.md, No. 797).
    /// </summary>
    [Fact]
    public void XRefStream_Damaged_IsRebuiltFromTheFile()
    {
        var output = new MemoryStream();
        output.Write("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"u8);
        var offsets = new List<long>();
        foreach (var body in new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                                     "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>" })
        {
            offsets.Add(output.Position);
            output.Write(Encoding.ASCII.GetBytes($"{offsets.Count} 0 obj\n{body}\nendobj\n"));
        }
        var at = output.Position;
        var entries = new List<byte> { 0, 0, 0, 0, 0, 0xFF };
        foreach (var o in offsets.Append(at)) entries.AddRange([1, (byte)(o >> 24), (byte)(o >> 16), (byte)(o >> 8), (byte)o, 0]);
        // ASCII85 by hand, five characters for every group (also „!!!!!“ for four zero bytes, valid beside „z“)
        var text = new StringBuilder();
        var bytes = entries.ToArray();
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var group = new byte[4];
            var n = Math.Min(4, bytes.Length - i);
            Array.Copy(bytes, i, group, 0, n);
            var v = (uint)(group[0] << 24 | group[1] << 16 | group[2] << 8 | group[3]);
            var chars = new char[5];
            for (var k = 4; k >= 0; k--, v /= 85) chars[k] = (char)(v % 85 + 33);
            text.Append(chars, 0, n + 1);
        }
        var data = text.ToString();
        data = data[..17] + "x" + data[18..] + "~>";   // the 18th character (inside the fourth group, the page tree's entry) damaged
        var dict = $"<< /Type /XRef /Size 5 /W [1 4 1] /Root 1 0 R /Filter /ASCII85Decode /Length {data.Length} >>";
        output.Write(Encoding.ASCII.GetBytes($"4 0 obj\n{dict}\nstream\n{data}\nendstream\nendobj\nstartxref\n{at}\n%%EOF\n"));

        using var doc = PdfDocument.Open(output.ToArray());
        Assert.Equal(1, doc.PageCount);
        Assert.True(doc.WasRepaired);
    }

    /// <summary>
    /// 7.5.6, 7.5.7 with S-A A06: an incrementally updated file whose cross-reference stream names, for an object kept only in the newer object
    /// stream, an offset where another object stands (as test file FRC_3.5_V_5_CFM_AESV3). Looking it up rebuilds the cross-references from the
    /// file; the object is then found in the object stream at once, and of two definitions the one further back in the file counts — also
    /// against an object written in the file before the stream. Before, the object that caused the rebuild stayed missing (a note lost its
    /// annotation), and the older object stream won (docs/FEHLERGEDAECHTNIS.md, No. 887).
    /// </summary>
    [Fact]
    public void Rebuilt_ObjectInTheNewerObjectStream_FoundAtOnce_LaterDefinitionCounts()
    {
        var bytes = FileWithObjectStreams([
            [(10, "(alt)"), (12, "(alt)")],
            [(10, "(neu)"), (11, "(nur neu)"), (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] >>")],
        ]);
        var text = Encoding.Latin1.GetString(bytes);
        // the entry of object 11 (type 2) made to point at object 1 in the file, as a writer that took the wrong type
        var rows = text.LastIndexOf(">>\nstream\n", StringComparison.Ordinal) + ">>\nstream\n".Length;   // the cross-reference stream is the last
        var one = text.IndexOf("1 0 obj", StringComparison.Ordinal);
        bytes[rows + 7 * 11] = 1;
        bytes[rows + 7 * 11 + 1] = (byte)(one >> 24);
        bytes[rows + 7 * 11 + 2] = (byte)(one >> 16);
        bytes[rows + 7 * 11 + 3] = (byte)(one >> 8);
        bytes[rows + 7 * 11 + 4] = (byte)one;
        bytes[rows + 7 * 11 + 5] = 0;
        bytes[rows + 7 * 11 + 6] = 0;

        using var doc = PdfDocument.Open(bytes);
        Assert.False(doc.WasRepaired);
        Assert.Equal("nur neu", Assert.IsType<PdfString>(doc.GetObject(11)).ToText());
        Assert.True(doc.WasRepaired);
        Assert.Equal("neu", Assert.IsType<PdfString>(doc.GetObject(10)).ToText());
        Assert.Equal("alt", Assert.IsType<PdfString>(doc.GetObject(12)).ToText());
        Assert.Equal(300, doc.Pages[0].MediaBox.Width);
    }

    /// <summary>
    /// 7.5.7 with S-A A06: the cross-reference stream names a wrong offset for the object stream itself (as test file
    /// FRC_8.4.2_Annotation_Flags_F_Hidden, whose page found its font and annotations in it). Looking up the first object in it reads the
    /// stream, which rebuilds the cross-references; the stream being read is registered at the next lookup, and every other object in it is
    /// found. Before, the stream was skipped while it was being read and its other objects counted as missing — a copy lost the font and the
    /// annotations of the page (docs/FEHLERGEDAECHTNIS.md, No. 923).
    /// </summary>
    [Fact]
    public void Rebuilt_WhileTheObjectStreamIsRead_ItsOtherObjectsAreFound()
    {
        var bytes = FileWithObjectStreams([[(10, "(eins)"), (11, "(zwei)"), (12, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")]]);
        var text = Encoding.Latin1.GetString(bytes);
        // the entry of the object stream (object 4) made to point at object 2
        var rows = text.LastIndexOf(">>\nstream\n", StringComparison.Ordinal) + ">>\nstream\n".Length;
        var two = text.IndexOf("2 0 obj", StringComparison.Ordinal);
        bytes[rows + 7 * 4 + 1] = (byte)(two >> 24);
        bytes[rows + 7 * 4 + 2] = (byte)(two >> 16);
        bytes[rows + 7 * 4 + 3] = (byte)(two >> 8);
        bytes[rows + 7 * 4 + 4] = (byte)two;

        using var doc = PdfDocument.Open(bytes);
        Assert.False(doc.WasRepaired);
        Assert.Equal("eins", Assert.IsType<PdfString>(doc.GetObject(10)).ToText());
        Assert.True(doc.WasRepaired);
        Assert.Equal("zwei", Assert.IsType<PdfString>(doc.GetObject(11)).ToText());
        Assert.True(Assert.IsType<PdfDictionary>(doc.GetObject(12)).IsType(PdfName.Get("Font")));
    }

    /// <summary>
    /// S-A A106 (with A06): one object written twice in the same body, without an update, and no usable cross-references (as test file
    /// 1m_diff_lsjdf, 1m_same_xxxx). Rebuilt from the file, the later definition counts — as 7.5.6 orders it for updates; the reference readers
    /// are divided. A copy holds that definition and needs no repair (Nacharbeit M5, A.9 (4), 17.11.2026).
    /// </summary>
    [Fact]
    public void Rebuilt_ObjectWrittenTwice_LaterDefinitionCounts_CopyHoldsIt()
    {
        var text = new StringBuilder("%PDF-1.7\n");
        foreach (var (number, body) in new[] { (1, "<< /Type /Catalog /Pages 2 0 R /Mark 4 0 R >>"), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                     (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"), (4, "(zuerst)"), (4, "(danach)") })
            text.Append($"{number} 0 obj\n{body}\nendobj\n");
        text.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n999999\n%%EOF\n");   // no cross-reference table: rebuilt
        using var doc = PdfDocument.Open(Encoding.Latin1.GetBytes(text.ToString()));
        Assert.True(doc.WasRepaired);
        Assert.Equal("danach", Assert.IsType<PdfString>(doc.GetObject(4)).ToText());
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions());
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.False(saved.WasRepaired);
        Assert.Equal("danach", Assert.IsType<PdfString>(saved.Catalog!.Get(PdfName.Get("Mark"))).ToText());
    }

    /// <summary>
    /// 7.5.6 with S-A A06: a damaged later update whose trailer names as /Root a number that, in the file, belongs to the information
    /// dictionary of the older revision (as issue9418 of a public test collection). Rebuilt from the file, the newest trailer whose /Root is a
    /// catalogue wins, with its /Info — as reference readers choose. Before, the information dictionary became the catalogue: a copy carried
    /// /Creator in its catalogue and had lost the outline (docs/FEHLERGEDAECHTNIS.md, No. 813).
    /// </summary>
    [Fact]
    public void Rebuilt_RootNamingNoCatalogue_OlderTrailerWins()
    {
        var text = new StringBuilder("%PDF-1.5\n");
        var offsets = new List<int>();
        foreach (var body in new[] { "<< /Producer (Muster) /Title (Erstausgabe) >>", "<< /Type /Catalog /Pages 3 0 R /Outlines 4 0 R >>",
                     "<< /Type /Pages /Kids [5 0 R] /Count 1 >>", "<< /Type /Outlines /Count 0 >>", "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>" })
        {
            offsets.Add(text.Length);
            text.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        var xref = text.Length;
        text.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) text.Append($"{o:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {offsets.Count + 1} /Info 1 0 R /Root 2 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        // the later update: its objects lost, its trailer naming object 1 as the catalogue and pointing nowhere
        text.Append("xref\n0 0\ntrailer\n<< /Size 9 /Root 1 0 R /Info 8 0 R >>\nstartxref\n3\n%%EOF\n");

        using var doc = PdfDocument.Open(Encoding.Latin1.GetBytes(text.ToString()));
        Assert.True(doc.WasRepaired);
        Assert.True(doc.Catalog!.IsType(PdfName.Catalog));
        Assert.True(doc.Catalog.ContainsKey(PdfName.Get("Outlines")));
        Assert.Equal("Erstausgabe", doc.Info.Title);
        Assert.Equal(1, doc.PageCount);
    }
}
