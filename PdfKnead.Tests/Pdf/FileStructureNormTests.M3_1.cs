using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Parsing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>File structure, 7.5.8: cross-reference stream data (Table 18) and hybrid-reference files, sentence by sentence.</summary>
public sealed partial class FileStructureNormTests
{
    /// <summary>Files built for the tests of cross-reference streams and hybrid-reference files below.</summary>
    private static class CrossReferenceFiles
    {
        /// <summary>
        /// A hybrid-reference file after the pattern of the example in 7.5.8.4, with values of its own: the catalogue (1) names the structure
        /// tree root 3; the main table lists 1, 12 (pages) and 13 (page) in use and 2 to 11 free; object 2 is an object stream holding 3 and 4;
        /// the cross-reference stream (11) lists 2, 3, 4 and itself; the update section has no entries and names the stream in /XRefStm. Object 6
        /// stands in the body as well, but only a section reachable over nothing but the stream's own /Prev lists it in use.
        /// </summary>
        /// <param name="streamPrev">The cross-reference stream carries a /Prev to that section (meaningless in a hybrid file, 7.5.8.2).</param>
        internal static byte[] Hybrid(bool streamPrev)
        {
            var output = new MemoryStream();
            void Text(string s) => output.Write(Encoding.Latin1.GetBytes(s));
            var offsets = new Dictionary<int, long>();
            void Obj(int number, string body)
            {
                offsets[number] = output.Position;
                Text($"{number} 0 obj\n{body}\nendobj\n");
            }
            Text("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
            Obj(1, "<< /Type /Catalog /Pages 12 0 R /StructTreeRoot 3 0 R >>");
            Obj(12, "<< /Type /Pages /Kids [13 0 R] /Count 1 >>");
            Obj(13, "<< /Type /Page /Parent 12 0 R /MediaBox [0 0 200 200] >>");
            Obj(6, "<< /Verborgen true >>");

            var main = output.Position;
            Text("xref\n0 14\n0000000002 65535 f \n");
            Text($"{offsets[1]:D10} 00000 n \n");
            for (var n = 2; n <= 11; n++) Text($"{(n == 11 ? 0 : n + 1):D10} 00001 f \n");   // free list 0 → 2 → … → 11 → 0
            Text($"{offsets[12]:D10} 00000 n \n{offsets[13]:D10} 00000 n \n");
            Text($"trailer\n<< /Size 14 /Root 1 0 R >>\nstartxref\n{main}\n%%EOF\n");

            // the object stream: two dictionaries the main table hides
            string[] contained = ["<< /Type /StructTreeRoot /K 4 0 R >>", "<< /S /Document /P 3 0 R >>"];
            var header = $"3 0 4 {contained[0].Length + 1} ";
            var body = contained[0] + "\n" + contained[1];
            Obj(2, $"<< /Type /ObjStm /N 2 /First {header.Length} /Length {header.Length + body.Length} >>\nstream\n{header}{body}\nendstream");

            long orphan = -1;
            if (streamPrev)
            {
                orphan = output.Position;
                Text($"xref\n6 1\n{offsets[6]:D10} 00000 n \ntrailer\n<< /Size 14 /Root 1 0 R >>\n");
            }

            // the cross-reference stream: /W [1 2 1], entries for 2 (in the file), 3 and 4 (in object stream 2, index 0 and 1) and itself
            var stream = output.Position;
            byte[] Row(byte type, long f2, int f3) => [type, (byte)(f2 >> 8), (byte)f2, (byte)f3];
            byte[] rows = [.. Row(1, offsets[2], 0), .. Row(2, 2, 0), .. Row(2, 2, 1), .. Row(1, stream, 0)];
            Text($"11 0 obj\n<< /Type /XRef /Size 14 /W [1 2 1] /Index [2 3 11 1]{(streamPrev ? $" /Prev {orphan}" : "")} /Length {rows.Length} >>\nstream\n");
            output.Write(rows);
            Text("\nendstream\nendobj\n");

            var update = output.Position;
            Text($"xref\n0 0\ntrailer\n<< /Size 14 /Root 1 0 R /Prev {main} /XRefStm {stream} >>\nstartxref\n{update}\n%%EOF\n");
            return output.ToArray();
        }

        /// <summary>
        /// A file of catalogue (1), page tree (2) and page (3) whose only section is a cross-reference stream (4) with /W [1 w2 w3]: object 0
        /// of type 0, the others of type 1 with their offsets in <paramref name="w2"/> bytes and generation 0 in <paramref name="w3"/> bytes.
        /// </summary>
        internal static (byte[] File, long[] Offsets) WithWidths(int w2, int w3)
        {
            var output = new MemoryStream();
            void Text(string s) => output.Write(Encoding.Latin1.GetBytes(s));
            Text("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
            var offsets = new long[5];
            string[] bodies = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"];
            for (var i = 0; i < bodies.Length; i++)
            {
                offsets[i + 1] = output.Position;
                Text($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
            }
            offsets[4] = output.Position;
            var rows = new List<byte>();
            void Field(long value, int width)
            {
                for (var k = width - 1; k >= 0; k--) rows.Add((byte)(value >> (8 * k)));
            }
            for (var n = 0; n <= 4; n++)
            {
                rows.Add(n == 0 ? (byte)0 : (byte)1);
                Field(n == 0 ? 0 : offsets[n], w2);
                Field(0, w3);
            }
            Text($"4 0 obj\n<< /Type /XRef /Size 5 /W [1 {w2} {w3}] /Root 1 0 R /Length {rows.Count} >>\nstream\n");
            output.Write(rows.ToArray());
            Text($"\nendstream\nendobj\nstartxref\n{offsets[4]}\n%%EOF\n");
            return (output.ToArray(), offsets);
        }
    }

    /// <summary>
    /// 7.5.8.4, sentences 3, 8 and 9 (and the example's walk through the search, with values of its own): in a hybrid-reference file the
    /// objects the main table marks free but the cross-reference stream behind /XRefStm lists in an object stream are not hidden from a reader
    /// of PDF 1.5 and later — the structure tree root of the catalogue resolves, its kid as well, nothing is rebuilt; an object free in the main
    /// table and absent from the stream stays deleted.
    /// </summary>
    [Fact]
    public void HybridFile_ObjectsOnlyInTheXRefStm_NotHidden()
    {
        var file = CrossReferenceFiles.Hybrid(streamPrev: false);
        using (var source = PdfSource.FromBytes(file))
        {
            var table = XRefReader.Read(source, 0);
            Assert.Equal(new XRefEntry(XRefEntryType.Compressed, 2, 0), table.Entries[3]);
            Assert.Equal(new XRefEntry(XRefEntryType.Compressed, 2, 1), table.Entries[4]);
            Assert.Equal(XRefEntryType.Free, table.Entries[5].Type);
        }
        using var doc = PdfDocument.Open(file);
        Assert.False(doc.WasRepaired);
        var root = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"));
        Assert.NotNull(root);
        Assert.True(root.IsType(PdfName.Get("StructTreeRoot")));
        Assert.Equal("Document", root.GetDictionary(PdfName.Get("K"))!.GetName(PdfName.Get("S"))!.Value);
        Assert.IsType<PdfNull>(doc.GetObject(5));
        Assert.Equal(1, doc.PageCount);
    }

    /// <summary>
    /// 7.5.8.2 (Table 17, /Prev, sentence 24): in a hybrid-reference file the /Prev of the cross-reference stream behind /XRefStm means nothing —
    /// the chain goes on with the /Prev of the table's trailer. A section reachable only over the stream's /Prev that lists object 6 in use is
    /// not read: object 6 stays deleted as the main table says, the other objects are as without that /Prev, nothing is rebuilt.
    /// </summary>
    [Fact]
    public void HybridFile_PrevOfTheXRefStmStream_NotFollowed()
    {
        var file = CrossReferenceFiles.Hybrid(streamPrev: true);
        Assert.Contains("/Prev", Encoding.Latin1.GetString(file)[(Encoding.Latin1.GetString(file).IndexOf("11 0 obj", StringComparison.Ordinal))..]);
        using (var source = PdfSource.FromBytes(file))
            Assert.True(XRefReader.Read(source, 0).IsFree(6));
        using var doc = PdfDocument.Open(file);
        Assert.False(doc.WasRepaired);
        Assert.IsType<PdfNull>(doc.GetObject(6));
        Assert.True(doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.IsType(PdfName.Get("StructTreeRoot")));
        Assert.Equal(1, doc.PageCount);
    }

    /// <summary>
    /// 7.5.8.3, Table 18, type 0 as a writer: the entry of object 0 in a written cross-reference stream is of type 0 (a free entry, like „f“ of
    /// a table), field 2 the number of the next free object (0: the list ends there) and field 3 the generation for a next use (65535: never),
    /// each field in the width /W gives it, high-order byte first (sentences 4 and 5).
    /// </summary>
    [Fact]
    public void Writer_XRefStream_FreeEntryOfObjectZero_Type0_Next0_Generation65535()
    {
        using var doc = PdfDocument.Open(FileWithObjectStreams([[(10, "<< /K 1 >>")]]));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "1.7" });
        var bytes = output.ToArray();
        var (_, startxref) = Tail(bytes);
        using var source = PdfSource.FromBytes(bytes);
        var number = new PdfParser(new PdfLexer(source)).ParseIndirectObject(startxref)!.Value.Id.Number;
        using var saved = PdfDocument.Open(bytes);
        var stream = Assert.IsType<PdfStream>(saved.GetObject(number));
        var w = stream.Dictionary.GetArray(PdfName.W)!;
        int w1 = (int)w.GetNumber(0)!, w2 = (int)w.GetNumber(1)!, w3 = (int)w.GetNumber(2)!;
        Assert.Equal(1, w1);
        Assert.Equal(2, w3);
        var data = saved.DecodeStream(stream).Data;
        Assert.Equal(0, data[0]);
        Assert.All(data[1..(1 + w2)], b => Assert.Equal(0, b));
        Assert.Equal(65535, (data[1 + w2] << 8) | data[2 + w2]);
    }

    /// <summary>
    /// 7.5.8.3, Table 18: field 3 of types 0 and 1 has the default 0 — a stream with /W [1 2 0] has no generation field, and every object of
    /// type 1 gets generation 0; the objects are found where field 2 says, nothing is rebuilt.
    /// </summary>
    [Fact]
    public void XRefStream_NoGenerationField_GenerationZero()
    {
        var (file, offsets) = CrossReferenceFiles.WithWidths(2, 0);
        using (var source = PdfSource.FromBytes(file))
        {
            var table = XRefReader.Read(source, 0);
            for (var n = 1; n <= 4; n++) Assert.Equal(new XRefEntry(XRefEntryType.InFile, offsets[n], 0), table.Entries[n]);
            Assert.True(table.IsFree(0));
        }
        using var doc = PdfDocument.Open(file);
        Assert.False(doc.WasRepaired);
        Assert.Equal(1, doc.PageCount);
    }

    /// <summary>
    /// 7.5.8.3, Table 18: field 2 of type 1 has the default 0 as well — with /W [1 0 1] (which 7.5.8.2 does not allow for type 1) an entry of
    /// type 1 has offset 0, where the header stands and no object can: it counts as free, so the objects are missing and the file is rebuilt
    /// from its content (S-A A06), with its page.
    /// </summary>
    [Fact]
    public void XRefStream_NoOffsetField_TypeOneAtZero_CountsAsFree_Rebuilt()
    {
        var (file, _) = CrossReferenceFiles.WithWidths(0, 1);
        using (var source = PdfSource.FromBytes(file))
        {
            var table = XRefReader.Read(source, 0);
            for (var n = 1; n <= 4; n++) Assert.True(table.IsFree(n), $"object {n}");
        }
        using var doc = PdfDocument.Open(file);
        Assert.True(doc.WasRepaired);
        Assert.Equal(1, doc.PageCount);
    }

    /// <summary>
    /// 7.5.8.3, Table 18, type 2 as a writer: field 2 names the object stream, field 3 the index of the object in it — at that index the
    /// header of the written object stream names the same object number, and the index lies between 0 and /N − 1.
    /// </summary>
    [Fact]
    public void Writer_XRefStream_IndexOfACompressedObject_NamesItInTheHeader()
    {
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R /A 4 0 R /B 5 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>", "<< /K 1 >>", "[1 2 3]");
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "1.7" });
        var bytes = output.ToArray();
        using var source = PdfSource.FromBytes(bytes);
        var compressed = XRefReader.Read(source, 0).Entries.Where(e => e.Value.Type == XRefEntryType.Compressed).ToList();
        Assert.True(compressed.Count >= 3, $"{compressed.Count} compressed objects");
        using var saved = PdfDocument.Open(bytes);
        foreach (var (number, entry) in compressed)
        {
            var stream = Assert.IsType<PdfStream>(saved.GetObject((int)entry.Offset));
            Assert.True(stream.Dictionary.IsType(PdfName.ObjStm));
            var header = ObjectStreamReader.Header(saved.DecodeStream(stream).Data, stream.Dictionary);
            Assert.InRange(entry.Generation, 0, stream.Dictionary.GetInteger(PdfName.N)!.Value - 1);
            Assert.Equal(number, header[entry.Generation].Number);
        }
        Assert.Contains(compressed, e => e.Value.Generation > 0);
    }

    /// <summary>
    /// 7.5.8.3, Table 18, type 2 as a reader: field 3 is the index of the object within the object stream — a header naming object 10 twice
    /// (at index 0 and 1) with field 3 = 1 gives the second object, not the first with that number.
    /// </summary>
    [Fact]
    public void ObjectStream_NumberTwiceInTheHeader_IndexOfField3Counts()
    {
        var file = FileWithObjectStreams([[(10, "(erste) "), (10, "(zweite)")]]);
        using (var source = PdfSource.FromBytes(file))
            Assert.Equal(new XRefEntry(XRefEntryType.Compressed, 4, 1), XRefReader.Read(source, 0).Entries[10]);
        using var doc = PdfDocument.Open(file);
        Assert.Equal("zweite", Assert.IsType<PdfString>(doc.GetObject(10)).ToText());
        Assert.False(doc.WasRepaired);
    }
}
