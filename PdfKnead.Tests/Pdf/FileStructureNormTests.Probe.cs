using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Parsing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// File structure, 7.5.4 to 7.5.8.4: one test per sentence where a row of the report named a test that checked only the behaviour next to it
/// (cross-reference sections of an update, the trailer of a file written anew, the search order of a hybrid-reference file).
/// </summary>
public sealed partial class FileStructureNormTests
{
    /// <summary>
    /// The subsections of a classic cross-reference section starting at <paramref name="at"/> („xref“): every line up to „trailer“ is either the
    /// head of a subsection — two integers separated by one space — or one of its entries of 20 bytes; anything else (a comment, a stray line)
    /// fails. Returns the object numbers in the order the section lists them.
    /// </summary>
    private static List<int> SectionNumbers(string text, int at)
    {
        Assert.StartsWith("xref\n", text[at..]);
        var end = text.IndexOf("trailer", at, StringComparison.Ordinal);
        Assert.True(end > at, "the section ends with „trailer“");
        Assert.DoesNotContain('%', text[at..end]);   // 7.5.4, sentence 12: no comment between xref and trailer
        var numbers = new List<int>();
        var pos = at + 5;
        while (pos < end)
        {
            var lineEnd = text.IndexOf('\n', pos);
            var head = Regex.Match(text[pos..lineEnd], @"^(\d+) (\d+)$");
            Assert.True(head.Success, $"head of a subsection expected: „{text[pos..lineEnd]}“");
            var first = int.Parse(head.Groups[1].Value, CultureInfo.InvariantCulture);
            var count = int.Parse(head.Groups[2].Value, CultureInfo.InvariantCulture);
            pos = lineEnd + 1;
            for (var i = 0; i < count; i++)
            {
                Assert.Matches(@"^\d{10} \d{5} [nf]( \n| \r|\r\n)$", text.Substring(pos, 20));
                numbers.Add(first + i);
                pos += 20;
            }
        }
        Assert.Equal(end, pos);
        return numbers;
    }

    /// <summary>
    /// 7.5.4, sentences 12 and 23, for the classic section of an appended update (and of a file written anew): no comment between „xref“ and
    /// „trailer“, and no object number with an entry in more than one subsection — here three runs (two replaced objects next to each other,
    /// one apart, two new ones), each number once (M3 row 13).
    /// </summary>
    [Fact]
    public void IncrementalUpdate_ClassicTable_NoCommentNoNumberTwice()
    {
        var original = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R /Extra [4 0 R 5 0 R 6 0 R] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>", "<< /A 1 >>", "<< /A 2 >>",
            "<< /A 3 >>");
        using (var doc = PdfDocument.Open(original))
        {
            using var written = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), written, new PdfSaveOptions { Version = "1.4" });
            var (writtenText, writtenXRef) = Tail(written.ToArray());
            var all = SectionNumbers(writtenText, (int)writtenXRef);
            Assert.Equal(all.Count, all.Distinct().Count());
        }

        using var source = PdfDocument.Open(original);
        var update = new IncrementalUpdate(source);
        foreach (var number in new[] { 1, 2, 4 })
            update.Replace(new PdfObjectId(number, 0), source.GetObject(number)!);
        update.Add(new PdfDictionary());
        update.Add(new PdfArray());
        using var appended = new MemoryStream();
        update.WriteTo(appended);
        var (text, startxref) = Tail(appended.ToArray());
        var numbers = SectionNumbers(text, (int)startxref);
        Assert.Equal(numbers.Count, numbers.Distinct().Count());   // sentence 23: each number once
        Assert.Equal(5, numbers.Count);
        Assert.True(Regex.Matches(text[(int)startxref..text.IndexOf("trailer", (int)startxref, StringComparison.Ordinal)], @"(?m)^\d+ \d+$").Count >= 3,
            "three subsections at least");
    }

    /// <summary>
    /// 7.5.4, sentence 57: an entry whose generation has reached 65535 is never used again. An appended update takes no such number — neither one
    /// in the middle (free with 65535 and outside the linked list, the second mechanism of sentence 52) nor the highest number of the table where
    /// /Size is one too small to include it (M3 row 17).
    /// </summary>
    [Fact]
    public void IncrementalUpdate_EntryAtGeneration65535_NeverUsedAgain()
    {
        static byte[] WithFreeEntry(int free, int head, string size)
        {
            var text = Encoding.Latin1.GetString(DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [] /Count 0 >>", "<< /Wert 3 >>", "<< /Wert 4 >>", "<< /Wert 5 >>"));
            var xref = text.IndexOf("xref\n", StringComparison.Ordinal);
            var lines = text[xref..].Split('\n');
            lines[2] = $"{head:D10} 65535 f ";
            lines[2 + free] = "0000000000 65535 f ";
            return Encoding.Latin1.GetBytes(text[..xref] + string.Join('\n', lines).Replace("/Size 6", size, StringComparison.Ordinal));
        }

        foreach (var (file, free) in new[] { (WithFreeEntry(3, 0, "/Size 6"), 3), (WithFreeEntry(5, 5, "/Size 5"), 5) })
        {
            using var source = PdfDocument.Open(file);
            Assert.False(source.WasRepaired);
            Assert.IsType<PdfNull>(source.GetObject(free) ?? PdfNull.Instance);
            var update = new IncrementalUpdate(source);
            var added = new[] { update.Add(new PdfDictionary()), update.Add(new PdfDictionary()), update.Add(new PdfDictionary()) };
            Assert.All(added, r => Assert.NotEqual(free, r.Id.Number));
            Assert.All(added, r => Assert.True(r.Id.Number >= 6, $"number {r.Id.Number}"));
            using var output = new MemoryStream();
            update.WriteTo(output);
            var bytes = output.ToArray();
            var (text, startxref) = Tail(bytes);
            Assert.DoesNotContain(free, SectionNumbers(text, (int)startxref));
            using var raw = PdfSource.FromBytes(bytes);
            Assert.True(XRefReader.Read(raw, 0).IsFree(free));
        }
    }

    /// <summary>
    /// 7.5.5, Table 15, /Prev (sentence 14: present only if the file has more than one cross-reference section): a file written anew has one
    /// section, and neither its trailer (classic table) nor its cross-reference stream carries /Prev — also where the source had been updated
    /// and had one (M3 row 23).
    /// </summary>
    [Fact]
    public void Writer_OneSection_NoPrev()
    {
        var original = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R /Probe 4 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>", "<< /Wert (alt) >>");
        var updated = TestPdf.AppendUpdate(original, 5, 1, null, (4, TestPdf.Dict(("Wert", new PdfString("neu"u8.ToArray())))));
        Assert.Contains("/Prev", Encoding.Latin1.GetString(updated)[Encoding.Latin1.GetString(updated).LastIndexOf("trailer", StringComparison.Ordinal)..]);
        using var doc = PdfDocument.Open(updated);
        foreach (var version in new[] { "1.4", "1.7" })
        {
            using var output = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = version });
            var bytes = output.ToArray();
            var (text, startxref) = Tail(bytes);
            Assert.Single(Regex.Matches(text, "startxref"));
            Assert.Single(Regex.Matches(text, "%%EOF"));
            if (version == "1.4")
            {
                Assert.Single(Regex.Matches(text, @"(?m)^xref$"));
                var trailer = text[text.IndexOf("trailer", (int)startxref, StringComparison.Ordinal)..text.LastIndexOf("startxref", StringComparison.Ordinal)];
                Assert.DoesNotContain("/Prev", trailer);
            }
            else
            {
                Assert.DoesNotMatch(@"(?m)^xref$", text);
                using var raw = PdfSource.FromBytes(bytes);
                var stream = Assert.IsType<PdfStream>(new PdfParser(new PdfLexer(raw)).ParseIndirectObject(startxref)!.Value.Value);
                Assert.True(stream.Dictionary.IsType(PdfName.XRef));
                Assert.False(stream.Dictionary.ContainsKey(PdfName.Prev));
            }
        }
    }

    /// <summary>
    /// 7.5.6, sentence 8: the cross-reference section of an incremental update holds entries only for the objects changed, replaced or added —
    /// here the catalogue replaced and one object added: the section lists exactly these (and, as a stream, the stream itself, which is new as
    /// well), after a classic table and after a cross-reference stream (M3 row 27).
    /// </summary>
    [Fact]
    public void IncrementalUpdate_SectionListsOnlyTheChangedObjects()
    {
        using var doc = PdfDocument.Open(FileWithObjectStreams([[(10, "<< /K 1 >>"), (11, "[1 2 3]")]]));
        foreach (var version in new[] { "1.4", "1.7" })
        {
            using var written = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), written, new PdfSaveOptions { Version = version });
            using var source = PdfDocument.Open(written.ToArray());
            var update = new IncrementalUpdate(source);
            var root = Assert.IsType<PdfReference>(source.Trailer.GetRaw(PdfName.Root)).Id;
            update.Replace(root, source.GetObject(root.Number)!);
            var added = update.Add(new PdfDictionary());
            using var appended = new MemoryStream();
            update.WriteTo(appended);
            var bytes = appended.ToArray();
            var (text, startxref) = Tail(bytes);
            if (version == "1.4")
            {
                Assert.Equal([root.Number, added.Id.Number], SectionNumbers(text, (int)startxref).Order());
            }
            else
            {
                using var raw = PdfSource.FromBytes(bytes);
                var section = new PdfParser(new PdfLexer(raw)).ParseIndirectObject(startxref)!.Value;
                var index = Assert.IsType<PdfStream>(section.Value).Dictionary.GetArray(PdfName.Index)!;
                var listed = new List<int>();
                for (var i = 0; i + 1 < index.Count; i += 2)
                    for (var n = 0; n < (int)index.GetNumber(i + 1)!; n++) listed.Add((int)index.GetNumber(i)! + n);
                Assert.Equal(new[] { root.Number, added.Id.Number, section.Id.Number }.Order(), listed.Order());
            }
        }
    }

    /// <summary>
    /// 7.5.8.4, sentences 10 and 11 (Table 19): in a hybrid-reference file the search goes through the standard section first, then to the
    /// stream behind /XRefStm, and only then to the section behind /Prev — with a section whose table is not empty: object 4 listed in the
    /// update's table and in its stream is taken from the table; object 5, listed in the stream and in the older section, from the stream;
    /// object 6, free in the older section and in use in the stream, is found (sentence 15). Nothing is rebuilt (M3 row 49).
    /// </summary>
    [Fact]
    public void HybridUpdate_TableFirst_ThenXRefStm_ThenPrev()
    {
        var output = new MemoryStream();
        void Text(string s) => output.Write(Encoding.Latin1.GetBytes(s));
        var offsets = new Dictionary<string, long>();
        void Obj(string key, int number, string body)
        {
            offsets[key] = output.Position;
            Text($"{number} 0 obj\n{body}\nendobj\n");
        }
        Text("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
        Obj("1", 1, "<< /Type /Catalog /Pages 2 0 R /Probe 4 0 R /Zweite 5 0 R /Dritte 6 0 R >>");
        Obj("2", 2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("3", 3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        Obj("4alt", 4, "<< /Wert (alt) >>");
        Obj("5alt", 5, "<< /Wert (alt) >>");
        var main = output.Position;
        Text("xref\n0 7\n0000000006 65535 f \n");
        foreach (var key in new[] { "1", "2", "3", "4alt", "5alt" }) Text($"{offsets[key]:D10} 00000 n \n");
        Text("0000000000 00001 f \n");
        Text($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{main}\n%%EOF\n");

        // the update: object 4 written twice (the table names one copy, the stream the other), 5 and 6 named by the stream only
        Obj("4tabelle", 4, "<< /Wert (Tabelle) >>");
        Obj("4strom", 4, "<< /Wert (Strom) >>");
        Obj("5strom", 5, "<< /Wert (Strom) >>");
        Obj("6strom", 6, "<< /Wert (Strom) >>");
        var stream = output.Position;
        byte[] Row(long at) => [1, (byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at, 0];
        byte[] rows = [.. Row(offsets["4strom"]), .. Row(offsets["5strom"]), .. Row(offsets["6strom"]), .. Row(stream)];
        Text($"7 0 obj\n<< /Type /XRef /Size 8 /W [1 4 1] /Index [4 4] /Root 1 0 R /Length {rows.Length} >>\nstream\n");
        output.Write(rows);
        Text("\nendstream\nendobj\n");
        var update = output.Position;
        Text($"xref\n4 1\n{offsets["4tabelle"]:D10} 00000 n \ntrailer\n<< /Size 8 /Root 1 0 R /Prev {main} /XRefStm {stream} >>\nstartxref\n{update}\n%%EOF\n");

        var file = output.ToArray();
        using (var raw = PdfSource.FromBytes(file))
        {
            var table = XRefReader.Read(raw, 0);
            Assert.Equal(new XRefEntry(XRefEntryType.InFile, offsets["4tabelle"], 0), table.Entries[4]);
            Assert.Equal(new XRefEntry(XRefEntryType.InFile, offsets["5strom"], 0), table.Entries[5]);
            Assert.Equal(new XRefEntry(XRefEntryType.InFile, offsets["6strom"], 0), table.Entries[6]);
        }
        using var doc = PdfDocument.Open(file);
        Assert.False(doc.WasRepaired);
        string Value(string key) => doc.Catalog!.GetDictionary(PdfName.Get(key))!.GetString(PdfName.Get("Wert"))!.ToText();
        Assert.Equal("Tabelle", Value("Probe"));
        Assert.Equal("Strom", Value("Zweite"));
        Assert.Equal("Strom", Value("Dritte"));
    }
}
