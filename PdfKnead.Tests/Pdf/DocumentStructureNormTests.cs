using System.Text;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Document structure (ISO 32000-2, 7.7 to 7.12; Konformitätsabgleich M5, docs/normabgleich/M5-dokumentaufbau.md): catalogue, page tree,
/// name dictionary, data structures, functions, file specifications, extensions. Nachweise „DS.“ in the report.
/// </summary>
public sealed partial class DocumentStructureNormTests
{
    /// <summary>A file of the given objects (1 is the catalogue) with a classic table.</summary>
    internal static byte[] FileOf(params string[] objects)
    {
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (var body in objects)
        {
            offsets.Add(text.Length);
            text.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        var xref = text.Length;
        text.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) text.Append($"{o:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    internal static PdfDocument Saved(byte[] file, PdfSaveOptions? options = null)
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, options ?? new PdfSaveOptions());
        return PdfDocument.Open(output.ToArray());
    }

    /// <summary>
    /// 7.7.3.3, Table 31, as written whatever the source had (docs/FEHLERGEDAECHTNIS.md, No. 755): no empty /Contents array; /Annots with
    /// indirect references only; /LastModified where /PieceInfo stands. Also checked: inherited /Resources and /MediaBox on the page itself, /Rotate
    /// as a multiple of 90 within 0 to 270.
    /// </summary>
    [Fact]
    public void Pages_WrittenAsTable31Asks()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 300 400] /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents [] /Rotate 450 >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R /Annots [ << /Type /Annot /Subtype /Square /Rect [10 10 50 50] /C [1 0 0] >> ] /PieceInfo << /Test << /LastModified (D:20260101000000Z) /Private 1 >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length 34 >>\nstream\nBT /F1 12 Tf 20 300 Td (Hallo) Tj ET\nendstream"), new PdfSaveOptions { Version = "1.4" });
        var first = saved.Pages[0].Dictionary;
        var second = saved.Pages[1].Dictionary;
        Assert.Null(first.GetRaw(PdfName.Contents));
        Assert.Equal(90, first.GetInteger(PdfName.Rotate));
        Assert.All(new[] { first, second }, page =>
        {
            Assert.NotNull(page.GetDictionary(PdfName.Resources));
            Assert.Equal(4, page.GetArray(PdfName.MediaBox)!.Count);
        });
        var annots = second.GetArray(PdfName.Get("Annots"))!;
        Assert.All(annots, a => Assert.IsType<PdfReference>(a));
        Assert.Equal("Square", annots.Get(0).AsDictionary()!.GetName(PdfName.Subtype)!.Value);
        Assert.NotNull(second.GetDictionary(PdfName.Get("PieceInfo")));
        Assert.NotNull(second.GetString(PdfName.Get("LastModified")));
    }

    /// <summary>
    /// Security (7.10.2; docs/FEHLERGEDAECHTNIS.md, No. 756): the samples of sampled functions stay with the functions within a budget per
    /// document. Before, a file of 334 KB with twenty functions of 16 million samples each (the data a Flate bomb of zeros) took 3.5 GB and,
    /// with a 2 GB limit for the process, ended it (measured with the command line). Here: functions of a million samples, room for three.
    /// </summary>
    [Fact]
    public void SampledFunctions_WithinTheBudget()
    {
        var data = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(data, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(new byte[1 << 20]);
        var stream = Encoding.Latin1.GetString(data.ToArray());
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>" };
        for (var i = 0; i < 5; i++)
            objects.Add($"<< /FunctionType 0 /Domain [0 1 0 1] /Range [0 1] /Size [1024 1024] /BitsPerSample 8 /Filter /FlateDecode /Length {data.Length} >>\nstream\n{stream}\nendstream");
        using var doc = PdfDocument.Open(FileOf([.. objects]));
        doc.FunctionSampleBudget = 3L << 20;
        var created = Enumerable.Range(4, 5).Select(n => PdfKnead.Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(n), doc)).ToList();
        Assert.Equal(3, created.Count(f => f is not null));
        Span<double> output = stackalloc double[1];
        created[0]!.Evaluate([0.5, 0.5], output);
        Assert.Equal(0, output[0]);
    }

    /// <summary>
    /// 7.10.5.1 and 7.2.4 (M1, S-A A01 „everywhere the same“): a PostScript calculator function knows integers and reals only. „NaN“,
    /// „Infinity“ and a number beyond double were read as numbers and gave NaN or infinite colours; now the function is refused like one with
    /// an unknown operator (docs/FEHLERGEDAECHTNIS.md, No. 929). An ordinary real with an exponent stays readable.
    /// </summary>
    [Theory]
    [InlineData("{ pop NaN }", false)]
    [InlineData("{ pop Infinity }", false)]
    [InlineData("{ pop -Infinity }", false)]
    [InlineData("{ pop 1e400 }", false)]
    [InlineData("{ pop 0.5 }", true)]
    public void PostScriptFunction_NoNaNOrInfinity(string program, bool readable)
    {
        using var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /FunctionType 4 /Domain [0 1] /Range [0 1] /Length {program.Length} >>\nstream\n{program}\nendstream"));
        var function = PdfKnead.Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(4), doc);
        Assert.Equal(readable, function is not null);
    }

    /// <summary>
    /// 7.10.5.3: braces in a PostScript calculator function may nest as deep as 255 (the limit for writers). Before, more than 32 levels made the
    /// function unreadable (docs/FEHLERGEDAECHTNIS.md, No. 757); 300 levels stay refused.
    /// </summary>
    [Theory]
    [InlineData(40, true)]
    [InlineData(255, true)]
    [InlineData(300, false)]
    public void PostScriptFunction_NestingUpTo255(int levels, bool readable)
    {
        var program = "{ " + string.Concat(Enumerable.Repeat("1 1 eq { ", levels)) + "0.75 " + string.Concat(Enumerable.Repeat("} if ", levels)) + "}";
        using var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /FunctionType 4 /Domain [0 1] /Range [0 1] /Length {program.Length} >>\nstream\n{program}\nendstream"));
        var function = PdfKnead.Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(4), doc);
        Assert.Equal(readable, function is not null);
        if (function is null) return;
        Span<double> output = stackalloc double[1];
        function.Evaluate([0.2], output);
        Assert.Equal(0.75, output[0]);
    }

    /// <summary>
    /// 7.10.5.3, sentences 10 and 11: the operand stack of a PostScript calculator function holds at least 100 entries. The input and 98 or 99
    /// pushed ones (99 and 100 entries), then as many additions: the sum. Before, the program stopped as soon as 98 entries lay on the stack
    /// and gave the topmost one (docs/FEHLERGEDAECHTNIS.md, No. 924). One entry more is an overflow: the program stops, nothing breaks.
    /// </summary>
    [Theory]
    [InlineData(98, true)]
    [InlineData(99, true)]
    [InlineData(100, false)]
    public void PostScriptFunction_StackOf100Entries(int pushed, bool fits)
    {
        var program = "{ " + string.Concat(Enumerable.Repeat("1 ", pushed)) + string.Concat(Enumerable.Repeat("add ", pushed)) + "}";
        using var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /FunctionType 4 /Domain [0 1] /Range [0 1000] /Length {program.Length} >>\nstream\n{program}\nendstream"));
        var function = PdfKnead.Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(4), doc);
        Assert.NotNull(function);
        Span<double> output = stackalloc double[1];
        function.Evaluate([0.5], output);
        if (fits) Assert.Equal(pushed + 0.5, output[0], 9);
        else Assert.NotEqual(pushed + 0.5, output[0]);
    }

    /// <summary>
    /// 7.10.5.3, sentence 9, S-A A107: a program that leaves more or fewer numbers than /Range has outputs is an error by the standard. Read
    /// with tolerance: the topmost values count, missing ones are 0 — measured against the reference readers (one value too many: all three
    /// take the topmost; one too few: two draw as PDF-Knead, one draws nothing).
    /// </summary>
    [Theory]
    [InlineData("{ 1 exch 0 0.5 }", 0.25, 0.0, 0.5)]   // four numbers for three outputs: the topmost three
    [InlineData("{ 0 }", 0.0, 0.25, 0.0)]              // two numbers for three outputs: the missing first one is 0
    public void PostScriptFunction_ResultCountNotAsRange_TopmostValues(string program, double first, double second, double third)
    {
        using var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /FunctionType 4 /Domain [0 1] /Range [0 1 0 1 0 1] /Length {program.Length} >>\nstream\n{program}\nendstream"));
        var function = PdfKnead.Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(4), doc);
        Assert.NotNull(function);
        Span<double> output = stackalloc double[3];
        function.Evaluate([0.25], output);
        Assert.Equal([first, second, third], output.ToArray());
    }

    /// <summary>
    /// 7.10.4 (PDF 2.0): the intervals of a stitching function are closed on the left and open on the right, the last closed on both sides; if
    /// the first bound equals Domain0, the first interval is closed on both sides — x = Domain0 belongs to the first function, encoded as Encode0
    /// —, and if the last bound equals Domain1, x = Domain1 belongs to the last, encoded as its first Encode value. Three constant functions
    /// 0.1, 0.5, 0.9. Before, x = Domain0 went to the second function (docs/FEHLERGEDAECHTNIS.md, No. 777).
    /// </summary>
    [Theory]
    [InlineData("[0]", 0.0, 0.1)]       // first bound at Domain0: the first function at the start
    [InlineData("[0]", 0.001, 0.5)]     // … and the second just after it
    [InlineData("[0.4]", 0.4, 0.5)]     // a bound in between belongs to the interval on its right
    [InlineData("[0.4]", 0.399, 0.1)]
    [InlineData("[0.4]", 1.0, 0.5)]     // the last interval closed on the right
    public void StitchingFunction_IntervalsAndTheirEnds(string bounds, double x, double expected)
    {
        static string Constant(double v) => FormattableString.Invariant($"<< /FunctionType 2 /Domain [0 1] /C0 [{v}] /C1 [{v}] /N 1 >>");
        using var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /FunctionType 3 /Domain [0 1] /Functions [{Constant(0.1)} {Constant(0.5)}] /Bounds {bounds} /Encode [0 1 0 1] >>"));
        var function = PdfKnead.Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(4), doc)!;
        Span<double> output = stackalloc double[1];
        function.Evaluate([x], output);
        Assert.Equal(expected, output[0], 6);
        // three functions, the last bound at Domain1: x = Domain1 is the last function
        using var three = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /FunctionType 3 /Domain [0 1] /Functions [{Constant(0.1)} {Constant(0.5)} {Constant(0.9)}] /Bounds [0.5 1] /Encode [0 1 0 1 0 1] >>"));
        PdfKnead.Core.Pdf.Graphics.PdfFunction.Create(three.GetObject(4), three)!.Evaluate([1.0], output);
        Assert.Equal(0.9, output[0], 6);
    }

    /// <summary>
    /// 7.7.2, Table 29: /Dests, /Outlines and /Threads are indirect references. A source that had them direct is written with them as objects
    /// of their own, their content unchanged — before, they stayed direct (docs/FEHLERGEDAECHTNIS.md, No. 754).
    /// </summary>
    [Fact]
    public void Catalogue_DestsOutlinesThreads_WrittenIndirect()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Outlines << /Type /Outlines /First 4 0 R /Last 4 0 R /Count 1 >> /Threads [ << /I << /Title (T) >> >> ] /Dests << /Ziel [3 0 R /Fit] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Title (Kapitel) /Dest [3 0 R /Fit] >>"));
        var catalog = saved.Catalog!;
        foreach (var key in new[] { "Dests", "Outlines", "Threads" })
            Assert.IsType<PdfReference>(catalog.GetRaw(PdfName.Get(key)));
        Assert.NotNull(catalog.GetDictionary(PdfName.Get("Dests"))!.GetArray(PdfName.Get("Ziel")));
        Assert.Equal(1, catalog.GetDictionary(PdfName.Get("Outlines"))!.GetInteger(PdfName.Get("Count")));
        Assert.Single(catalog.GetArray(PdfName.Get("Threads"))!);
    }

    // ---- Nachweise added in the Nacharbeit (12.11.2026) for rows that stood as „umgesetzt“ without one

    /// <summary>
    /// 7.9.5: a rectangle is given by any two opposite corners; readers order them — page boxes and the rectangle of a link alike (M5 row 37).
    /// </summary>
    [Fact]
    public void Rectangles_AnyTwoCorners_Ordered()
    {
        using var doc = PdfDocument.Open(FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [300 400 0 0] /CropBox [250 20 50 350] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [150 150 50 60] /A << /S /URI /URI (https://example.org/) >> >>"));
        var page = doc.Pages[0];
        Assert.Equal(new PdfRectangle(0, 0, 300, 400), page.MediaBox);
        Assert.Equal(new PdfRectangle(50, 20, 250, 350), page.CropBox);
        Assert.Equal(new PdfRectangle(50, 60, 150, 150), Assert.Single(PageLinks.Of(doc, 0)).Rect);
    }

    /// <summary>
    /// 7.11.1–7.11.3: a file is named by a file specification string or a file specification dictionary, and both name the same file; in the
    /// dictionary /UF (Unicode) goes before /F (M5 row 47).
    /// </summary>
    [Theory]
    [InlineData("(Anhang.pdf)", "Anhang.pdf")]
    [InlineData("<< /Type /Filespec /F (Anhang.pdf) >>", "Anhang.pdf")]
    [InlineData("<< /Type /Filespec /F (Ubersicht.pdf) /UF <FEFF00DC00620065007200730069006300680074002E007000640066> >>", "Übersicht.pdf")]
    public void FileSpecifications_StringOrDictionary_NameTheFile(string spec, string expected)
    {
        using var doc = PdfDocument.Open(FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            $"<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /A << /S /GoToR /F {spec} /D [0 /Fit] >> >>"));
        var link = Assert.IsType<OtherLink>(Assert.Single(PageLinks.Of(doc, 0)).Target);
        Assert.Equal("GoToR", link.Kind);
        Assert.Equal(expected, link.Detail);
    }

    // ---- M5, second pass (Nacharbeit 12.11.2026)

    /// <summary>
    /// 7.7.3.2, 7.7.3.3: a page tree refers to a page object and to a node only once. The same page taken twice (a copy of a page) is written as
    /// two page objects, and /Count says two.
    /// </summary>
    [Fact]
    public void Pages_SamePageTwice_WrittenAsTwoPageObjects()
    {
        const string content = "BT /F1 12 Tf 20 20 Td (Hallo) Tj ET";
        using var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        var page = PageItem.From(doc)[0];
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [page, page], output, new PdfSaveOptions());
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.Equal(2, saved.PageCount);
        Assert.NotEqual(saved.Pages[0].Id, saved.Pages[1].Id);
        var kids = saved.Catalog!.GetDictionary(PdfName.Get("Pages"))!.GetArray(PdfName.Get("Kids"))!;
        Assert.Equal(2, Enumerable.Range(0, kids.Count).Select(i => ((PdfReference)kids[i]).Id).Distinct().Count());
        Assert.Equal(2, saved.Catalog!.GetDictionary(PdfName.Get("Pages"))!.GetInteger(PdfName.Get("Count")));
    }

    /// <summary>
    /// 7.7.3.4: an attribute a page omits comes from the nearest ancestor that has it — as it stands, without merging: a page with its own (empty)
    /// /Resources does not get the fonts of its parent. An optional attribute nobody sets takes its default (/CropBox = /MediaBox, /Rotate 0).
    /// A required one missing everywhere (/MediaBox; the file is faulty) is read as US Letter (S-A A87).
    /// </summary>
    [Fact]
    public void Pages_InheritedAttributes_AsTheyStand()
    {
        using var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 300 400] /Rotate 90 /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Resources << >> >>",
            "<< /Type /Page /Parent 2 0 R /Rotate 0 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        var own = doc.Pages[0];
        Assert.Empty(own.Resources!.Entries);   // its own, empty — not merged with the parent's
        Assert.Equal(new PdfRectangle(0, 0, 300, 400), own.MediaBox);
        Assert.Equal(own.MediaBox, own.CropBox);
        Assert.Equal(90, own.Rotate);
        var inheriting = doc.Pages[1];
        Assert.NotNull(inheriting.Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1")));
        Assert.Equal(0, inheriting.Rotate);   // its own value goes first
        using var faulty = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R >>"));
        Assert.Equal(PdfPage.DefaultMediaBox, faulty.Pages[0].MediaBox);
    }

    /// <summary>7.7.3.3: /UserUnit gives the size of a unit in multiples of 1/72 inch — a page of 100 units with /UserUnit 2 is 200 points wide.</summary>
    [Fact]
    public void Pages_UserUnit_ScalesThePage()
    {
        using var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 150] /UserUnit 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 150] /UserUnit -3 >>"));
        Assert.Equal((200.0, 300.0), (doc.Pages[0].Width, doc.Pages[0].Height));
        Assert.Equal((100.0, 150.0), (doc.Pages[1].Width, doc.Pages[1].Height));   // not positive: the default 1
    }

    /// <summary>
    /// 7.9.4: a date is „D:YYYYMMDDHHmmSSOHH'mm“; month and day default to 01, the other fields to zero; without a relation to UT the time is
    /// UT; the apostrophe follows the hour offset only, the minutes only after it. Written in PDF 2.0 without and in 1.x with the final
    /// apostrophe of ISO 32000-1.
    /// </summary>
    [Fact]
    public void Dates_FormAndDefaults()
    {
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), PdfDate.Parse("D:2026"));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), PdfDate.Parse("D:202611"));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 9, 30, 0, TimeSpan.Zero), PdfDate.Parse("D:202611120930"));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 9, 30, 5, TimeSpan.Zero), PdfDate.Parse("D:20261112093005Z"));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 9, 30, 5, TimeSpan.FromHours(1)), PdfDate.Parse("D:20261112093005+01'00"));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 9, 30, 5, TimeSpan.FromHours(1)), PdfDate.Parse("D:20261112093005+01"));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 9, 30, 5, TimeSpan.FromMinutes(-330)), PdfDate.Parse("D:20261112093005-05'30'"));
        var time = new DateTimeOffset(2026, 11, 12, 9, 30, 5, TimeSpan.FromHours(1));
        Assert.Equal("D:20261112093005+01'00", PdfDate.Format(time, pdf20: true));
        Assert.Equal("D:20261112093005+01'00'", PdfDate.Format(time));
        Assert.DoesNotContain(' ', PdfDate.Format(time, pdf20: true));
    }

    /// <summary>
    /// 7.10.2: the samples are a continuous bit stream, high-order bit first, without padding at byte boundaries; the values of one sample stand
    /// in the order of /Range. Two outputs of 12 bits, Size 2 (three bytes per sample): 0x000 FFF, then 0xFFF 000. A table of Size 1 gives its
    /// single value everywhere; data that are too short (the file is faulty) read the missing bits as 0 (S-A A88).
    /// </summary>
    [Fact]
    public void SampledFunction_BitStreamWithoutPadding_OutputsInRangeOrder()
    {
        PdfKnead.Core.Pdf.Graphics.PdfFunction Function(string dict, string data)
        {
            var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
                $"<< /FunctionType 0 {dict} /Length {data.Length} >>\nstream\n{data}\nendstream"));
            return PdfKnead.Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(3), doc)!;
        }
        Span<double> output = stackalloc double[2];
        var twelve = Function("/Domain [0 1] /Range [0 1 0 1] /Size [2] /BitsPerSample 12", "\x00\x0F\xFF\xFF\xF0\x00");
        twelve.Evaluate([0.0], output);
        Assert.Equal((0.0, 1.0), (output[0], output[1]));
        twelve.Evaluate([1.0], output);
        Assert.Equal((1.0, 0.0), (output[0], output[1]));
        twelve.Evaluate([0.5], output);
        Assert.Equal((0.5, 0.5), (output[0], output[1]));
        var four = Function("/Domain [0 1] /Range [0 1] /Size [3] /BitsPerSample 4", "\x0F\xF0");   // 0, F, F, (padding) — samples 0, 15, 15
        four.Evaluate([0.5], output);
        Assert.Equal(1.0, output[0]);
        var single = Function("/Domain [0 1 0 1] /Range [0 1] /Size [1 2] /BitsPerSample 8", "\x40\xC0");
        single.Evaluate([0.9, 0.0], output);
        Assert.Equal(0x40 / 255.0, output[0], 6);
        var shortData = Function("/Domain [0 1] /Range [0 1] /Size [4] /BitsPerSample 8", "\xFF");
        shortData.Evaluate([1.0], output);
        Assert.Equal(0.0, output[0]);
    }

    /// <summary>
    /// 7.10.2, the evaluation of a sampled function step by step (sentences 40, 47, 48, 57–59, 70): the input clipped to /Domain, encoded by
    /// /Encode (here reversed, [2 0]), clipped to the table, interpolated as a real number between the samples, decoded by /Decode (reversed,
    /// [1 0]) and clipped to /Range ([0 0.5]). Samples 0, 128, 255. Expected values worked out by hand from the equations of 7.10.2 (Bericht M5,
    /// Zeile 41 aufgeteilt nach dem Prüfer für Sammelzeilen, 20.11.2026).
    /// </summary>
    [Fact]
    public void SampledFunction_EvaluationChain_DomainEncodeTableDecodeRange()
    {
        var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /FunctionType 0 /Domain [0 2] /Range [0 0.5] /Size [3] /BitsPerSample 8 /Encode [2 0] /Decode [1 0] /Length 3 >>\nstream\n\x00\x80\xFF\nendstream"));
        var function = PdfKnead.Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(3), doc)!;
        var output = new double[1];
        double At(double x)
        {
            function.Evaluate([x], output);
            return output[0];
        }
        Assert.Equal(0, At(0), 6);                        // e = 2 → sample 255 → decoded 0
        Assert.Equal(0.5, At(2), 6);                      // e = 0 → sample 0 → decoded 1 → clipped to the range
        Assert.Equal(0.5, At(3), 6);                      // clipped to the domain first: as x = 2
        Assert.Equal(0, At(-1), 6);                       // as x = 0
        Assert.Equal(1 - 191.5 / 255, At(0.5), 6);        // e = 1.5: halfway between 128 and 255, a real number, decoded 1 − r/255
        Assert.Equal(0.5, At(1.5), 6);                    // e = 0.5: r = 64, decoded 0.749 → clipped to 0.5
    }

    /// <summary>
    /// 7.10.2, /Order 3: cubic spline interpolation in every input with at least 4 samples (with fewer, Order 3 is ignored). PDF-Knead takes
    /// Catmull-Rom splines through the samples, straight at the ends (docs/FEHLERGEDAECHTNIS.md, No. 778). Samples 0, 64, 255, 64, 0: halfway
    /// between the second and third sample the spline gives 0.688 where linear interpolation gives 0.625; at a sample both give the sample; a
    /// straight run of samples stays straight up to the ends; Size 3 stays linear; two inputs, cubic in the one with 4 samples and linear in the
    /// one with 2.
    /// </summary>
    [Fact]
    public void SampledFunction_Order3_CubicSpline()
    {
        PdfKnead.Core.Pdf.Graphics.PdfFunction Function(string dict, string data)
        {
            var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
                $"<< /FunctionType 0 {dict} /BitsPerSample 8 /Length {data.Length} >>\nstream\n{data}\nendstream"));
            return PdfKnead.Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(3), doc)!;
        }
        double At(PdfKnead.Core.Pdf.Graphics.PdfFunction f, params double[] x)
        {
            Span<double> output = stackalloc double[1];
            f.Evaluate(x, output);
            return output[0];
        }
        const string peak = "\x00\x40\xFF\x40\x00";
        var cubic = Function("/Domain [0 1] /Range [0 1] /Size [5] /Order 3", peak);
        var linear = Function("/Domain [0 1] /Range [0 1] /Size [5]", peak);
        Assert.Equal(0.5 * 64 / 255 + 0.5625, At(cubic, 0.375), 9);
        Assert.Equal(0.5 * (64 / 255.0 + 1), At(linear, 0.375), 9);
        Assert.Equal(1.0, At(cubic, 0.5), 9);
        var ramp = Function("/Domain [0 1] /Range [0 1] /Size [5] /Order 3", "\x00\x33\x66\x99\xCC");   // 0, 0.2, 0.4, 0.6, 0.8
        Assert.Equal(0.08, At(ramp, 0.1), 9);
        Assert.Equal(0.76, At(ramp, 0.95), 9);
        var three = Function("/Domain [0 1] /Range [0 1] /Size [3] /Order 3", "\x00\xFF\x00");
        Assert.Equal(0.5, At(three, 0.25), 9);
        // first input 4 samples (a ramp 0 … 0.6), second input 2 samples (the row above all 1): (0.3 + 1) / 2 in the middle
        var mixed = Function("/Domain [0 1 0 1] /Range [0 1] /Size [4 2] /Order 3", "\x00\x33\x66\x99\xFF\xFF\xFF\xFF");
        Assert.Equal(0.65, At(mixed, 0.5, 0.5), 9);
    }

    /// <summary>
    /// 7.9.7: a number tree built by PDF-Knead (the structure's parent tree) has its keys in ascending order whatever order it is given, leaves and
    /// intermediate nodes with /Limits of their least and greatest key, the root without.
    /// </summary>
    [Fact]
    public void NumberTree_Built_SortedWithLimits()
    {
        var stored = new List<PdfDictionary>();
        PdfObject Store(PdfDictionary node)
        {
            stored.Add(node);
            return new OutputReference(stored.Count - 1);
        }
        (long, PdfObject)[] entries = [(7, PdfInteger.Of(70)), (2, PdfInteger.Of(20)), (11, PdfInteger.Of(110)), (5, PdfInteger.Of(50)), (3, PdfInteger.Of(30))];
        var root = NumberTree.Build(entries, Store, pairsPerNode: 2);
        Assert.False(root.ContainsKey(PdfName.Get("Limits")));
        Assert.False(root.ContainsKey(PdfName.Get("Nums")));
        var leaves = stored.Where(s => s.ContainsKey(PdfName.Get("Nums"))).ToList();
        var keys = leaves.SelectMany(l => Enumerable.Range(0, l.GetArray(PdfName.Get("Nums"))!.Count / 2)
            .Select(i => ((PdfInteger)l.GetArray(PdfName.Get("Nums"))![2 * i]).Value)).ToList();
        Assert.Equal([2L, 3, 5, 7, 11], keys);
        foreach (var leaf in leaves)
        {
            var nums = leaf.GetArray(PdfName.Get("Nums"))!;
            var limits = leaf.GetArray(PdfName.Get("Limits"))!;
            Assert.Equal(((PdfInteger)nums[0]).Value, ((PdfInteger)limits[0]).Value);
            Assert.Equal(((PdfInteger)nums[nums.Count - 2]).Value, ((PdfInteger)limits[1]).Value);
        }
    }

    // ---- M5, writing what the norm asks of writers (Nacharbeit 12.11.2026)

    /// <summary>The values of a tree's pairs as stored (raw), for checking 7.9.6 sentence 5; kids are checked to be indirect references.</summary>
    private static List<PdfObject> RawValues(PdfDocument doc, PdfDictionary root, string entries)
    {
        var values = new List<PdfObject>();
        var stack = new Stack<PdfDictionary>([root]);
        while (stack.TryPop(out var node))
        {
            if (node.GetRaw(PdfName.Get(entries)) is { } stored && (stored is PdfReference r ? doc.Resolve(r.Id) : stored) is PdfArray pairs)
                for (var i = 1; i < pairs.Count; i += 2) values.Add(pairs[i]);
            if (node.GetArray(PdfName.Get("Kids")) is { } kids)
                for (var i = 0; i < kids.Count; i++)
                {
                    Assert.IsType<PdfReference>(kids[i]);   // kids as indirect references (Tables 36, 37)
                    stack.Push(kids.Get(i)!.AsDictionary()!);
                }
        }
        return values;
    }

    /// <summary>
    /// 7.9.6, 7.9.7: foreign trees that break the rules — keys not in order, /Limits wrong, the root with both /Kids and /Names, page labels
    /// unsorted — are written as proper trees: keys in byte order (numbers ascending), /Limits right, the root with one of the two, the values as
    /// sentence 5 of 7.9.6 recommends (arrays and dictionaries indirect). Every pair stays, also a key given twice (an attachment must not get
    /// lost); every page keeps its label. Before, such trees went out as they came (docs/FEHLERGEDAECHTNIS.md, No. 779).
    /// </summary>
    [Fact]
    public void ForeignTrees_WrittenInOrder()
    {
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 5 0 R /EmbeddedFiles << /Names [(b.txt) 8 0 R (a.txt) 9 0 R (a.txt) 10 0 R] >> >> /PageLabels << /Nums [2 << /S /r >> 0 << /S /D >>] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 6 0 R] /Count 3 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Names [(zeta) [3 0 R /Fit] (alpha) [4 0 R /Fit]] /Kids [7 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Limits [(x) (y)] /Names [(mitte) [6 0 R /Fit]] >>",
            "<< /Type /Filespec /F (b.txt) /UF (b.txt) /EF << /F 11 0 R >> >>",
            "<< /Type /Filespec /F (a.txt) /UF (a.txt) /Desc (erste) /EF << /F 11 0 R >> >>",
            "<< /Type /Filespec /F (a.txt) /UF (a.txt) /Desc (zweite) /EF << /F 11 0 R >> >>",
            "<< /Type /EmbeddedFile /Length 3 >>\nstream\nABC\nendstream");
        using var before = PdfDocument.Open(file);
        var labelsBefore = PageLabels.Of(before)!;
        using var saved = Saved(file);
        var names = saved.Catalog!.GetDictionary(PdfName.Get("Names"))!;
        var dests = names.GetDictionary(PdfName.Get("Dests"))!;
        Assert.False(dests.ContainsKey(PdfName.Get("Kids")) && dests.ContainsKey(PdfName.Get("Names")));
        Assert.False(dests.ContainsKey(PdfName.Get("Limits")));
        Assert.Equal(["alpha", "mitte", "zeta"], NameTree.Pairs(dests).Select(p => Encoding.ASCII.GetString(p.Key)));
        Assert.All(RawValues(saved, dests, "Names"), v => Assert.IsType<PdfReference>(v));
        // „alpha“ still names the second page
        var alpha = NameTree.Pairs(dests).First().Value;
        var target = ((PdfArray)(alpha is PdfReference ar ? saved.Resolve(ar.Id) : alpha))[0];
        Assert.Equal(saved.Pages[1].Id, Assert.IsType<PdfReference>(target).Id);
        var files = names.GetDictionary(PdfName.Get("EmbeddedFiles"))!;
        var attachments = NameTree.Pairs(files).Select(p => (Encoding.ASCII.GetString(p.Key), (p.Value is PdfReference fr ? saved.Resolve(fr.Id) : p.Value)
            .AsDictionary()!.GetString(PdfName.Get("Desc"))?.ToText())).ToList();
        Assert.Equal([("a.txt", "erste"), ("a.txt", "zweite"), ("b.txt", null)], attachments);
        var labels = saved.Catalog!.GetDictionary(PdfName.Get("PageLabels"))!;
        var nums = labels.GetArray(PdfName.Get("Nums"))!;
        Assert.Equal([0L, 2L], Enumerable.Range(0, nums.Count / 2).Select(i => ((PdfInteger)nums[2 * i]).Value));
        Assert.All(RawValues(saved, labels, "Nums"), v => Assert.IsType<PdfReference>(v));
        Assert.Equal(labelsBefore, PageLabels.Of(saved)!);
    }

    /// <summary>
    /// 7.9.6, 7.9.7 with keys of the wrong kind: a number tree key written as an integral real (2.0) and a name tree key written as a name
    /// (/beta) are read as the integer and as the string with the same bytes — when looking up and when listing — and written as such when the
    /// tree is built anew; the page keeps its label, the destination its name. Before, the listing passed both over and the writer lost the
    /// pairs (Bericht M5 Zeilen 38, 39; docs/FEHLERGEDAECHTNIS.md, No. 896).
    /// </summary>
    [Fact]
    public void ForeignTrees_KeysOfTheWrongKind_ReadAndWrittenAsTheirKind()
    {
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests << /Names [(zeta) [3 0 R /Fit] /beta [4 0 R /Fit]] >> >> /PageLabels << /Nums [2.0 << /S /r >> 0 << /S /D >>] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        using var before = PdfDocument.Open(file);
        Assert.NotNull(NumberTree.Value(before.Catalog!.GetDictionary(PdfName.Get("PageLabels"))!, 2));
        using var saved = Saved(file);
        var dests = saved.Catalog!.GetDictionary(PdfName.Get("Names"))!.GetDictionary(PdfName.Get("Dests"))!;
        Assert.Equal(["beta", "zeta"], NameTree.Pairs(dests).Select(p => Encoding.ASCII.GetString(p.Key)));
        Assert.All(RawKeys(saved, dests, "Names"), k => Assert.IsType<PdfString>(k));
        var nums = saved.Catalog!.GetDictionary(PdfName.Get("PageLabels"))!.GetArray(PdfName.Get("Nums"))!;
        Assert.Equal([0L, 2L], Enumerable.Range(0, nums.Count / 2).Select(i => Assert.IsType<PdfInteger>(nums[2 * i]).Value));
        Assert.Equal(PageLabels.Of(before)!, PageLabels.Of(saved)!);
        Assert.Equal("r", PageLabels.Of(saved)![2].Style);   // the third page counts in lower-case roman numerals from the pair 2.0
    }

    /// <summary>The keys of the pairs in the leaves of a name or number tree, as written.</summary>
    private static List<PdfObject> RawKeys(PdfDocument doc, PdfDictionary root, string entries)
    {
        var keys = new List<PdfObject>();
        var stack = new Stack<PdfDictionary>([root]);
        while (stack.TryPop(out var node))
        {
            if (node.GetArray(PdfName.Get(entries)) is { } pairs)
                for (var i = 0; i + 1 < pairs.Count; i += 2) keys.Add(pairs.Get(i));
            if (node.GetArray(PdfName.Get("Kids")) is { } kids)
                for (var i = 0; i < kids.Count; i++)
                    if (kids.Get(i) is PdfDictionary kid) stack.Push(kid);
        }
        return keys;
    }

    /// <summary>
    /// 7.9.7 for the parent tree of the structure: keys out of order and a key given twice are written ascending, each once — the pair this
    /// program reads (the first) —, the arrays as objects of their own (7.9.6 sentence 5). Before, the tree went out as it came
    /// (docs/FEHLERGEDAECHTNIS.md, No. 779).
    /// </summary>
    [Fact]
    public void StructureParentTree_WrittenInOrder()
    {
        const string content = "/P << /MCID 0 >> BDC BT /F1 12 Tf 20 100 Td (Hallo) Tj ET EMC";
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /StructParents 0 /Contents 4 0 R /Resources << /Font << /F1 8 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /StructTreeRoot /K 6 0 R /ParentTree << /Nums [7 [6 0 R] 0 [7 0 R] 0 [6 0 R]] >> /ParentTreeNextKey 8 >>",
            "<< /Type /StructElem /S /Document /P 5 0 R /K 7 0 R >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        var root = saved.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        var tree = root.GetDictionary(PdfName.Get("ParentTree"))!;
        var pairs = NumberTree.Pairs(tree).ToList();
        Assert.Equal([0L, 7L], pairs.Select(p => p.Key));
        Assert.All(pairs, p => Assert.IsType<PdfReference>(p.Value));
        // key 0 names the paragraph (the first pair of that key), as read before
        var first = (PdfArray)saved.Resolve(((PdfReference)pairs[0].Value).Id);
        Assert.Equal("P", first.Get(0)!.AsDictionary()!.GetName(PdfName.Get("S"))!.Value);
    }

    /// <summary>
    /// 7.9.6 sentence 5 (for number trees alike, 7.9.7) in the trees the writer builds itself: the page labels written for pages in a new order
    /// have their ranges as objects of their own (docs/FEHLERGEDAECHTNIS.md, No. 779).
    /// </summary>
    [Fact]
    public void OwnPageLabels_RangesIndirect()
    {
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [0 << /S /r >> 1 << /S /D /P (A-) >>] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [.. PageItem.From(doc).Reverse()], output, new PdfSaveOptions());
        using var saved = PdfDocument.Open(output.ToArray());
        var labels = saved.Catalog!.GetDictionary(PdfName.Get("PageLabels"))!;
        Assert.All(RawValues(saved, labels, "Nums"), v => Assert.IsType<PdfReference>(v));
        Assert.Equal([new PageLabel("D", "A-", 1), new PageLabel("r", "", 1)], PageLabels.Of(saved)!);
    }

    /// <summary>7.9.6: a key given twice stays within one leaf of a tree built — the ranges of the leaves do not overlap (No. 779).</summary>
    [Fact]
    public void NameTree_Built_KeyTwiceInOneLeaf()
    {
        var stored = new List<PdfDictionary>();
        PdfObject Store(PdfDictionary node)
        {
            stored.Add(node);
            return new OutputReference(stored.Count - 1);
        }
        (byte[], PdfObject)[] entries = [("a"u8.ToArray(), PdfInteger.Of(1)), ("b"u8.ToArray(), PdfInteger.Of(2)), ("b"u8.ToArray(), PdfInteger.Of(3)), ("c"u8.ToArray(), PdfInteger.Of(4))];
        NameTree.Build(entries, Store, pairsPerNode: 2);
        var leaves = stored.Where(s => s.ContainsKey(PdfName.Get("Names"))).Select(l => l.GetArray(PdfName.Get("Limits"))!).ToList();
        Assert.Equal(["a-b", "c-c"], leaves.Select(l => $"{((PdfString)l[0]).ToText()}-{((PdfString)l[1]).ToText()}"));
    }

    /// <summary>
    /// 7.11.3, Table 43, as a writer, the rest of the second review: a file specification that names no file at all (no /F, /UF, /DOS, /Mac,
    /// /Unix) gets /F — required then — and /UF; a private dictionary of a program that only happens to hold /F (here under a key of its own in
    /// the catalogue) stays as it is, without /Type and /UF. Before, the first went out without a name and the second was made a file
    /// specification (Bericht M5 Zeile 59; docs/FEHLERGEDAECHTNIS.md, No. 897).
    /// </summary>
    [Fact]
    public void FileSpecifications_WithoutAnyName_AndPrivateDictionaries()
    {
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R /MusterProgramm << /F (einstellungen.dat) >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /FileAttachment /Rect [10 40 30 60] /FS << /Type /Filespec /EF << /F 5 0 R >> >> >>",
            "<< /Type /EmbeddedFile /Length 3 >>\nstream\nABC\nendstream");
        using var saved = Saved(file);
        var spec = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!.GetDictionary(PdfName.Get("FS"))!;
        Assert.Equal("Anhang", spec.GetString(PdfName.Get("F"))!.ToText());
        Assert.Equal("Anhang", spec.GetString(PdfName.Get("UF"))!.ToText());
        var own = saved.Catalog!.GetDictionary(PdfName.Get("MusterProgramm"))!;
        Assert.False(own.ContainsKey(PdfName.Type) || own.ContainsKey(PdfName.Get("UF")));
        Assert.Equal("einstellungen.dat", own.GetString(PdfName.Get("F"))!.ToText());
    }

    /// <summary>
    /// 7.11.3, Table 43, as a writer: a file specification gets /Type /Filespec (required with /EF, recommended always), /UF beside /F where
    /// the bytes of /F read as text without doubt (ASCII or a byte order mark — not a platform's bytes in an unknown encoding: readers take
    /// /UF first), /F where only /UF stood; one with /EF given directly becomes an object of its own (indirectly referenced). The launch
    /// parameters of a launch action (/Win, also with /F) stay as they are; below 1.7 no /UF (a key of 1.7). Before, only the PDF/A path
    /// added /UF, and only to embedded files (docs/FEHLERGEDAECHTNIS.md, No. 780).
    /// </summary>
    [Fact]
    public void FileSpecifications_AsTable43Asks()
    {
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(a.txt) 5 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R 7 0 R 8 0 R 9 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /A << /S /GoToR /F << /F (Anhang.pdf) >> /D [0 /Fit] >> >>",
            "<< /F (a.txt) /EF << /F 6 0 R >> >>",
            "<< /Type /EmbeddedFile /Length 3 >>\nstream\nABC\nendstream",
            "<< /Type /Annot /Subtype /FileAttachment /Rect [10 40 30 60] /FS << /F (b.txt) /EF << /F 6 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 70 100 90] /A << /S /Launch /F << /UF (Bericht.pdf) >> /Win << /F (notepad.exe) >> >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 100 100 120] /A << /S /GoToR /F << /F (Grüße.pdf) >> /D [0 /Fit] >> >>");
        using var saved = Saved(file);
        var spec = NameTree.Pairs(saved.Catalog!.GetDictionary(PdfName.Get("Names"))!.GetDictionary(PdfName.Get("EmbeddedFiles"))!).Single().Value;
        var embedded = (PdfDictionary)(spec is PdfReference sr ? saved.Resolve(sr.Id) : spec);
        Assert.Equal("Filespec", embedded.GetName(PdfName.Type)?.Value);
        Assert.Equal("a.txt", embedded.GetString(PdfName.Get("UF"))!.ToText());
        PdfDictionary Annot(int i) => saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(i)!.AsDictionary()!;
        var linked = Annot(0).GetDictionary(PdfName.Get("A"))!.GetDictionary(PdfName.Get("F"))!;
        Assert.Equal("Anhang.pdf", linked.GetString(PdfName.Get("UF"))!.ToText());
        Assert.Equal("Filespec", linked.GetName(PdfName.Type)?.Value);
        // the attachment's specification with /EF: an object of its own, complete
        Assert.IsType<PdfReference>(Annot(1).GetRaw(PdfName.Get("FS")));
        var attached = Annot(1).GetDictionary(PdfName.Get("FS"))!;
        Assert.Equal("b.txt", attached.GetString(PdfName.Get("UF"))!.ToText());
        Assert.NotNull(attached.GetDictionary(PdfName.Get("EF"))!.Get(PdfName.Get("F")) as PdfStream);
        // /F from /UF; the launch parameters untouched
        var launch = Annot(2).GetDictionary(PdfName.Get("A"))!;
        Assert.Equal("Bericht.pdf", launch.GetDictionary(PdfName.Get("F"))!.GetString(PdfName.Get("F"))!.ToText());
        var win = launch.GetDictionary(PdfName.Get("Win"))!;
        Assert.False(win.ContainsKey(PdfName.Get("UF")) || win.ContainsKey(PdfName.Type));
        // a platform's bytes (here Latin-1 without a byte order mark): no /UF made from them
        Assert.False(Annot(3).GetDictionary(PdfName.Get("A"))!.GetDictionary(PdfName.Get("F"))!.ContainsKey(PdfName.Get("UF")));
        // below 1.7 no /UF
        using var old = Saved(file, new PdfSaveOptions { Version = "1.6" });
        var oldLinked = old.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!.GetDictionary(PdfName.Get("A"))!.GetDictionary(PdfName.Get("F"))!;
        Assert.False(oldLinked.ContainsKey(PdfName.Get("UF")));
        Assert.Equal("Filespec", oldLinked.GetName(PdfName.Type)?.Value);
    }

    /// <summary>
    /// 7.10.4, Table 41, as a writer: a stitching function with one bound too many (the last at the end of the domain, S-A A19) is written
    /// without it, one without /Encode with [0 1] for each function and one with a longer /Encode with its first 2 × k numbers (S-A A89) —
    /// as this program reads them, so the gradient looks the same. Before, such functions went out as they came (docs/FEHLERGEDAECHTNIS.md,
    /// No. 781).
    /// </summary>
    [Fact]
    public void StitchingFunction_WrittenAsTable41Asks()
    {
        const string content = "/Sh0 sh /Sh1 sh";
        static string Constant(double v) => FormattableString.Invariant($"<< /FunctionType 2 /Domain [0 1] /C0 [{v} 0 0] /C1 [{v} 0 0] /N 1 >>");
        using var saved = Saved(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Shading << /Sh0 5 0 R /Sh1 7 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Function 6 0 R >>",
            $"<< /FunctionType 3 /Domain [0 1] /Functions [{Constant(0.2)} {Constant(0.8)}] /Bounds [0.5 1] /Encode [0 1 0 1 0 1] >>",
            $"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Function << /FunctionType 3 /Domain [0 1] /Functions [{Constant(0.2)} {Constant(0.8)}] /Bounds [0.25] >> >>"));
        var shadings = saved.Pages[0].Resources!.GetDictionary(PdfName.Get("Shading"))!;
        var first = shadings.GetDictionary(PdfName.Get("Sh0"))!.GetDictionary(PdfName.Get("Function"))!;
        Assert.Equal([0.5], first.GetArray(PdfName.Get("Bounds"))!.Select(n => n.AsNumber()!.Value));
        Assert.Equal([0.0, 1, 0, 1], first.GetArray(PdfName.Get("Encode"))!.Select(n => n.AsNumber()!.Value));
        var second = shadings.GetDictionary(PdfName.Get("Sh1"))!.GetDictionary(PdfName.Get("Function"))!;
        Assert.Equal([0.0, 1, 0, 1], second.GetArray(PdfName.Get("Encode"))!.Select(n => n.AsNumber()!.Value));
        Assert.Equal([0.25], second.GetArray(PdfName.Get("Bounds"))!.Select(n => n.AsNumber()!.Value));
    }

    /// <summary>
    /// S-A A69 (7.3.3; 7.10.1, Tables 38, 39): a real where a function asks for an integer is read rounded — /Size [2.6] too, three samples, as
    /// the reference reader that draws such functions does (before, it was cut off to two) — and written as that integer, so the readers that
    /// draw nothing for a real show the copy as this program shows the source; /Rotate 89.6 is written as 90. Before, the reals went out as
    /// they came (docs/FEHLERGEDAECHTNIS.md, No. 905).
    /// </summary>
    [Theory]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void Function_RealsWhereIntegersBelong_ReadRoundedAndWrittenAsIntegers(string version)
    {
        const string content = "/Sh0 sh /Sh1 sh";
        var file = FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Rotate 89.6 /Resources << /Shading << /Sh0 5 0 R /Sh1 6 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 200 0] /Function 7 0 R >>",
            "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 200 0] /Function << /FunctionType 1.6 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >>",
            "<< /FunctionType 0.0 /Domain [0 1] /Range [0 1] /Size [2.6] /BitsPerSample 7.6 /Length 3 >>\nstream\n\0\xFF\0\nendstream");
        using var source = PdfDocument.Open(file);
        using var saved = Saved(file, new PdfSaveOptions { Version = version });
        PdfObject FunctionObject(PdfDocument doc, string name) =>
            doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Shading"))!.GetDictionary(PdfName.Get(name))!.Get(PdfName.Get("Function"))!;
        PdfDictionary Function(PdfDocument doc, string name) => FunctionObject(doc, name) is PdfStream s ? s.Dictionary : (PdfDictionary)FunctionObject(doc, name);
        var sampled = Function(saved, "Sh0");
        Assert.IsType<PdfInteger>(sampled.Get(PdfName.Get("FunctionType")));
        Assert.Equal(8, Assert.IsType<PdfInteger>(sampled.Get(PdfName.Get("BitsPerSample"))).Value);
        Assert.Equal([3L], sampled.GetArray(PdfName.Size)!.Select(n => Assert.IsType<PdfInteger>(n).Value));
        Assert.Equal(2, Assert.IsType<PdfInteger>(Function(saved, "Sh1").Get(PdfName.Get("FunctionType"))).Value);
        Assert.Equal(90, Assert.IsType<PdfInteger>(saved.Pages[0].Dictionary.Get(PdfName.Rotate)).Value);
        foreach (var name in new[] { "Sh0", "Sh1" })
        {
            var before = Core.Pdf.Graphics.PdfFunction.Create(FunctionObject(source, name), source)!;
            var after = Core.Pdf.Graphics.PdfFunction.Create(FunctionObject(saved, name), saved)!;
            for (var x = 0.0; x <= 1; x += 0.05)
            {
                var a = new double[1];
                var b = new double[1];
                before.Evaluate([x], a);
                after.Evaluate([x], b);
                Assert.Equal(a[0], b[0], 6);
            }
        }
        // three samples: dark again at the right end
        var right = new double[1];
        Core.Pdf.Graphics.PdfFunction.Create(FunctionObject(source, "Sh0"), source)!.Evaluate([1.0], right);
        Assert.True(right[0] < 0.05);
    }

    /// <summary>
    /// S-A A69 for the keys into the parent tree (14.7.5.4; Table 31: integer): /StructParents 0.0 of a page and /StructParent 1.0 of an
    /// annotation are those keys — read as the keys of the number tree (an integral real is that integer) and written as integers, the
    /// structure of both kept; /StructParents 2.6 of a form is no key and goes. Before, the program read such keys differently from place to
    /// place (the accessibility check rounded, the writer took only integers) and a real that is no key went out as it came
    /// (docs/FEHLERGEDAECHTNIS.md, No. 906).
    /// </summary>
    [Fact]
    public void StructureKeys_IntegralReals_ReadAndWrittenAsIntegers()
    {
        const string content = "/P << /MCID 0 >> BDC BT /F1 12 Tf 20 100 Td (Max) Tj ET EMC /Fm1 Do";
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /StructParents 0.0 /Annots [8 0 R] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm1 10 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /StructTreeRoot /K [7 0 R 11 0 R] /ParentTree 9 0 R /ParentTreeNextKey 2 >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /Border [0 0 0] /StructParent 1.0 /A << /S /URI /URI (https://example.org) >> >>",
            "<< /Nums [0 [7 0 R] 1 11 0 R] >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /StructParents 2.6 /Length 0 >>\nstream\n\nendstream",
            "<< /Type /StructElem /S /Link /P 6 0 R /Pg 3 0 R /K << /Type /OBJR /Obj 8 0 R >> >>");
        using var saved = Saved(file);
        var page = saved.Pages[0].Dictionary;
        var pageKey = Assert.IsType<PdfInteger>(page.Get(PdfName.Get("StructParents"))).Value;
        var annot = page.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!;
        var annotKey = Assert.IsType<PdfInteger>(annot.Get(PdfName.Get("StructParent"))).Value;
        var tree = saved.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.GetDictionary(PdfName.Get("ParentTree"))!;
        var marked = Assert.IsType<PdfArray>(NumberTree.Value(tree, pageKey));
        Assert.Equal("P", marked.Get(0)!.AsDictionary()!.GetName(PdfName.Get("S"))?.Value);
        Assert.Equal("Link", NumberTree.Value(tree, annotKey)!.AsDictionary()!.GetName(PdfName.Get("S"))?.Value);
        var form = (PdfStream)page.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Fm1"))!;
        Assert.False(form.Dictionary.ContainsKey(PdfName.Get("StructParents")));
    }

    /// <summary>
    /// 7.10.4, Table 41, as a writer, the rest of the second review: a stitching function whose bounds are out of order, and one with several
    /// bounds below the domain, are written with bounds in increasing order inside the domain, the functions never reached left out — as this
    /// program reads them: the function gives the same value at every point of the domain before and after. Before, they went out as they came
    /// (Bericht M5 Zeile 44; docs/FEHLERGEDAECHTNIS.md, No. 898).
    /// </summary>
    [Theory]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void StitchingFunction_BoundsOutOfOrderOrOutside_WrittenAsRead(string version)
    {
        const string content = "/Sh0 sh /Sh1 sh";
        static string Ramp(double from, double to) => FormattableString.Invariant($"<< /FunctionType 2 /Domain [0 1] /C0 [{from}] /C1 [{to}] /N 1 >>");
        var functions = $"[{Ramp(0, 0.3)} {Ramp(0.4, 0.6)} {Ramp(0.7, 1)}]";
        var file = FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Shading << /Sh0 5 0 R /Sh1 6 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 200 0] /Function << /FunctionType 3 /Domain [0 1] /Functions {functions} /Bounds [0.6 0.3] /Encode [0 1 0 1 0 1] >> >>",
            $"<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 200 0] /Function << /FunctionType 3 /Domain [0 1] /Functions {functions} /Bounds [-1 -0.5] /Encode [0 1 0 1 0 1] >> >>");
        using var source = PdfDocument.Open(file);
        using var saved = Saved(file, new PdfSaveOptions { Version = version });
        foreach (var name in new[] { "Sh0", "Sh1" })
        {
            PdfDictionary Function(PdfDocument doc) => doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Shading"))!.GetDictionary(PdfName.Get(name))!.GetDictionary(PdfName.Get("Function"))!;
            var bounds = Function(saved).GetArray(PdfName.Get("Bounds"))!.Select(n => n.AsNumber()!.Value).ToList();
            Assert.Equal(bounds.Order(), bounds);
            Assert.All(bounds, v => Assert.InRange(v, 0, 1));
            var before = Core.Pdf.Graphics.PdfFunction.Create(Function(source), source)!;
            var after = Core.Pdf.Graphics.PdfFunction.Create(Function(saved), saved)!;
            for (var x = 0.0005; x < 1; x += 0.001)
            {
                var a = new double[1];
                var b = new double[1];
                before.Evaluate([x], a);
                after.Evaluate([x], b);
                Assert.Equal(a[0], b[0], 6);
            }
        }
    }

    /// <summary>
    /// S-A A18 (7.5.2): a file without a PDF header is read when it holds objects and a catalogue — as version 1.4, with a note for the user —,
    /// as widely used readers do; bytes without objects or catalogue are no PDF. Written, the file always gets a header.
    /// </summary>
    [Fact]
    public void Reader_WithoutHeader_ReadWithNote_WrittenWithHeader()
    {
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] >>");
        var withoutHeader = file.AsSpan("%PDF-1.7\n".Length).ToArray();
        Assert.False(withoutHeader.AsSpan().IndexOf("%PDF-"u8) >= 0);
        using var doc = PdfDocument.Open(withoutHeader);
        Assert.Equal("1.4", doc.HeaderVersion);
        Assert.Equal(1, doc.PageCount);
        Assert.Equal(300, doc.Pages[0].MediaBox.Top);
        Assert.Contains(doc.Problems, p => p.Contains("Kopf", StringComparison.Ordinal));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions());
        Assert.True(output.ToArray().AsSpan().StartsWith("%PDF-"u8));
        Assert.Throws<PdfKnead.Core.Pdf.PdfException>(() => PdfDocument.Open("Kein PDF, nur Text ohne Objekte."u8.ToArray()).Dispose());
    }

    /// <summary>
    /// 7.7.4 sentence 47 (should: the key in /EmbeddedFiles matches /UF or /F), decided on 17.11.2026 (variant C): a foreign key that
    /// differs is renamed after the file, and the /GoToE link that opened it by the old key names the new one; an attachment a script names by
    /// its key keeps it (the script would not find it otherwise); one that matches already stays. Before, every foreign key stayed as it came
    /// (Bericht M5 Zeile 26; docs/FEHLERGEDAECHTNIS.md, No. 909).
    /// </summary>
    [Fact]
    public void EmbeddedFiles_KeysAfterTheirFile_LinksFollow_ScriptsKeepTheirs()
    {
        static string Spec(string name) => $"<< /Type /Filespec /F ({name}) /UF ({name}) /EF << /F 5 0 R >> >>";
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(Anlage1) 6 0 R (Bild) 7 0 R (daten.csv) 8 0 R] >> " +
            "/JavaScript << /Names [(Start) << /S /JavaScript /JS (this.exportDataObject\\({cName: \"Bild\"}\\);) >>] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /A << /S /GoToE /T << /R /C /N (Anlage1) >> >> >>",
            "<< /Type /EmbeddedFile /Length 3 >>\nstream\nABC\nendstream",
            Spec("rechnung.xml"), Spec("bild.png"), Spec("daten.csv"));
        using var saved = Saved(file);
        var tree = saved.Catalog!.GetDictionary(PdfName.Get("Names"))!.GetDictionary(PdfName.Get("EmbeddedFiles"))!;
        Assert.Equal(["Bild", "daten.csv", "rechnung.xml"], NameTree.Pairs(tree).Select(p => Encoding.Latin1.GetString(p.Key)).Order(StringComparer.Ordinal));
        var target = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!
            .GetDictionary(PdfName.Get("A"))!.GetDictionary(PdfName.Get("T"))!;
        Assert.Equal("rechnung.xml", Encoding.Latin1.GetString(target.GetString(PdfName.Get("N"))!.Bytes));
    }

    /// <summary>
    /// 7.7.4 (Table 32) as a writer: every file specification of the /EmbeddedFiles tree has /EF with /F or /UF holding the file. An entry
    /// naming none goes; one with its file stays. In a PDF/A-2b copy the PDF/A step takes out a file the part does not allow (not itself
    /// PDF/A) — its entry goes with it, and a tree left empty leaves the name dictionary (docs/FEHLERGEDAECHTNIS.md, No. 779).
    /// </summary>
    [Fact]
    public void EmbeddedFiles_OnlyEntriesWithTheirFile()
    {
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(a.txt) 4 0 R (leer.txt) 6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Filespec /F (a.txt) /UF (a.txt) /EF << /F 5 0 R >> >>",
            "<< /Type /EmbeddedFile /Length 3 >>\nstream\nABC\nendstream",
            "<< /Type /Filespec /F (leer.txt) /UF (leer.txt) >>");
        using var saved = Saved(file);
        var tree = saved.Catalog!.GetDictionary(PdfName.Get("Names"))!.GetDictionary(PdfName.Get("EmbeddedFiles"))!;
        Assert.Equal(["a.txt"], NameTree.Pairs(tree).Select(p => Encoding.ASCII.GetString(p.Key)));
        using var pdfA = Saved(file, new PdfSaveOptions { PdfA = PdfAConformance.A2b });
        Assert.False(pdfA.Catalog!.GetDictionary(PdfName.Get("Names"))?.ContainsKey(PdfName.Get("EmbeddedFiles")) == true);
    }

    /// <summary>
    /// 7.7.2 as a writer: what the catalogue says about opening — /PageLayout, /PageMode, /OpenAction (a destination, its page mapped to the
    /// page written) — is carried over. Applying it when PdfKnead opens a file comes with the viewer preferences in M10 (design decision,
    /// 12.11.2026).
    /// </summary>
    [Fact]
    public void Catalogue_OpeningEntries_Kept()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R /PageLayout /TwoColumnLeft /PageMode /UseOutlines /OpenAction [4 0 R /FitH 300] >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 400] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 400] >>"));
        var catalog = saved.Catalog!;
        Assert.Equal("TwoColumnLeft", catalog.GetName(PdfName.Get("PageLayout"))?.Value);
        Assert.Equal("UseOutlines", catalog.GetName(PdfName.Get("PageMode"))?.Value);
        var open = (PdfArray)catalog.Get(PdfName.Get("OpenAction"));
        Assert.Equal(saved.Pages[1].Id, Assert.IsType<PdfReference>(open[0]).Id);
        Assert.Equal("FitH", ((PdfName)open[1]).Value);
    }

    /// <summary>
    /// 7.8.3 sentences 12, 13 and 7.7.3.4 as a writer (S-B B49): no page inherits its resources in the copy — the /Resources of a /Pages node
    /// go onto every page, written once as one object all of them name (written onto each, they would stand a thousand times in a file of a
    /// thousand pages).
    /// </summary>
    [Fact]
    public void Pages_InheritedResources_OneObjectForAll()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length 34 >>\nstream\nBT /F1 12 Tf 20 100 Td (Max) Tj ET\nendstream"));
        var first = Assert.IsType<PdfReference>(saved.Pages[0].Dictionary.GetRaw(PdfName.Resources));
        var second = Assert.IsType<PdfReference>(saved.Pages[1].Dictionary.GetRaw(PdfName.Resources));
        Assert.Equal(first.Id, second.Id);
        Assert.NotNull(saved.Pages[1].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1")));
        Assert.Null(saved.Pages[0].Dictionary.GetRaw(PdfName.Parent) is PdfReference p && saved.Resolve(p.Id) is PdfDictionary node ? node.GetRaw(PdfName.Resources) : null);
    }

    /// <summary>
    /// 7.7.3.3 (Table 31) as a writer: /Thumb (a stream, indirect), /B (indirect references to the beads on the page), /Dur and /Trans are
    /// carried over as they stand; their content belongs to M8 (groups) and M10 (articles, presentations).
    /// </summary>
    [Fact]
    public void Pages_ThumbBeadsDurTrans_Kept()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Threads [6 0 R] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Thumb 4 0 R /B [5 0 R] /Dur 3 /Trans << /S /Dissolve /D 1 >> >>",
            "<< /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\n\0\nendstream",
            "<< /Type /Bead /T 6 0 R /N 5 0 R /V 5 0 R /P 3 0 R /R [10 10 190 190] >>",
            "<< /Type /Thread /F 5 0 R >>"));
        var page = saved.Pages[0].Dictionary;
        Assert.IsType<PdfReference>(page.GetRaw(PdfName.Get("Thumb")));
        Assert.IsType<PdfStream>(page.Get(PdfName.Get("Thumb")));
        var beads = page.GetArray(PdfName.Get("B"))!;
        Assert.IsType<PdfReference>(beads[0]);
        Assert.Equal(saved.Pages[0].Id, Assert.IsType<PdfReference>(beads.Get(0)!.AsDictionary()!.GetRaw(PdfName.Get("P"))).Id);
        Assert.Equal(3, page.GetNumber(PdfName.Get("Dur")));
        Assert.Equal("Dissolve", page.GetDictionary(PdfName.Get("Trans"))!.GetName(PdfName.Get("S"))?.Value);
    }

    /// <summary>
    /// 7.9.2.2: text strings in UTF-8 are new in 2.0. Saved below 2.0 they are written as the lower versions have text strings — PDFDocEncoding
    /// where every character has one, else UTF-16BE —, the same text; readers of 1.x showed „ï»¿“ and mojibake before. Saved as 2.0 they stay
    /// as they are (docs/FEHLERGEDAECHTNIS.md, No. 784).
    /// </summary>
    [Fact]
    public void Utf8TextStrings_SavedBelow20_AsTheLowerVersionsHaveThem()
    {
        static string Utf8(string text) => "<EFBBBF" + Convert.ToHexString(Encoding.UTF8.GetBytes(text)) + ">";
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Version /2.0 >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            $"<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /Contents {Utf8("Café für Erika")} /T {Utf8("Max Ω")} >>");
        using var old = Saved(file, new PdfSaveOptions { Version = "1.7" });
        var annot = old.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!;
        var contents = annot.GetString(PdfName.Get("Contents"))!;
        Assert.Equal("Café für Erika", contents.ToText());
        Assert.False(contents.Bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
        Assert.False(contents.Bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]));   // every character in PDFDocEncoding
        var title = annot.GetString(PdfName.Get("T"))!;
        Assert.Equal("Max Ω", title.ToText());
        Assert.True(title.Bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]));      // Ω has none: UTF-16BE
        using var current = Saved(file, new PdfSaveOptions { Version = "2.0" });
        var kept = current.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!.GetString(PdfName.Get("Contents"))!;
        Assert.True(kept.Bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
    }

    /// <summary>
    /// 7.10.4, Table 41: a bound at the edge of the domain — the first equal to Domain0 or the last equal to Domain1 (that function only at the
    /// point, valid from 2.0) — saved as 1.7 keeps the version asked for (decided on 12.11.2026; before, the file was raised to 2.0
    /// and its other objects stayed 1.x, docs/FEHLERGEDAECHTNIS.md, No. 784, 801): the bound moves a hair inside, the edge function is constant
    /// at its first Encode value, and the function gives the same colours at the edges and inside; saved as 2.0 it stays as it was. A last
    /// bound beyond Domain1 (valid in no version) belongs to a function never reached, which goes.
    /// </summary>
    [Theory]
    [InlineData("0", "1.7")]
    [InlineData("1", "1.7")]
    [InlineData("0", "2.0")]
    [InlineData("1.5", "1.7")]
    [InlineData("-0.5", "1.7")]
    public void StitchingFunction_BoundAtTheDomainEdge_KeepsTheVersionAndTheColours(string bounds, string version)
    {
        const string content = "/Sh0 sh";
        static string Ramp(double from, double to) => FormattableString.Invariant($"<< /FunctionType 2 /Domain [0 1] /C0 [{from} 0 0] /C1 [{to} 0 0] /N 1 >>");
        var file = FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Shading << /Sh0 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Extend [true true] /Function << /FunctionType 3 /Domain [0 1] /Functions [{Ramp(0.2, 0.3)} {Ramp(0.6, 0.9)}] /Bounds [{bounds}] /Encode [0 1 0 1] >> >>");
        static double[] Colours(PdfDocument doc)
        {
            var function = Core.Pdf.Graphics.PdfFunction.Create(doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Shading"))!
                .GetDictionary(PdfName.Get("Sh0"))!.Get(PdfName.Get("Function")), doc)!;
            return [.. new[] { 0.0, 1e-3, 0.25, 0.5, 0.75, 1 - 1e-3, 1.0 }.Select(x =>
            {
                var output = new double[3];
                function.Evaluate([x], output);
                return Math.Round(output[0], 4);
            })];
        }
        double[] before;
        using (var doc = PdfDocument.Open(file)) before = Colours(doc);
        using var doc2 = PdfDocument.Open(file);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc2, PageItem.From(doc2), output, new PdfSaveOptions { Version = version });
        Assert.Equal(version, result.Version);
        Assert.Null(result.RaisedFor);
        using var saved = PdfDocument.Open(output.ToArray());
        var written = saved.Pages[0].Resources!.GetDictionary(PdfName.Get("Shading"))!.GetDictionary(PdfName.Get("Sh0"))!.GetDictionary(PdfName.Get("Function"))!;
        var b = written.GetArray(PdfName.Get("Bounds"))!;
        var functions = written.GetArray(PdfName.Get("Functions"))!.Count;
        Assert.Equal(functions - 1, b.Count);
        for (var i = 0; i < b.Count; i++)
            if (version == "1.7") Assert.True(b.GetNumber(i) is > 0 and < 1, $"Grenze {b.GetNumber(i)} nicht im Inneren (1.x)");
            else Assert.True(b.GetNumber(i) is >= 0 and <= 1, $"Grenze {b.GetNumber(i)} nicht im Bereich");
        // the same colours at the edges and inside (the hair-wide strip at the edge lies between the points looked at)
        Assert.Equal(before, Colours(saved));
    }

    /// <summary>
    /// S-A A67 in the writer of M5: an entry naming nothing (a reference into the void) counts as absent — a file specification whose /UF names
    /// nothing gets /UF from /F (docs/FEHLERGEDAECHTNIS.md, No. 780).
    /// </summary>
    [Fact]
    public void FileSpecification_EntryIntoTheVoid_CountsAsAbsent()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /A << /S /GoToR /F << /Type /Filespec /F (Anhang.pdf) /UF 99 0 R >> /D [0 /Fit] >> >>"));
        var spec = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!.GetDictionary(PdfName.Get("A"))!.GetDictionary(PdfName.Get("F"))!;
        Assert.Equal("Anhang.pdf", spec.GetString(PdfName.Get("UF"))!.ToText());
    }

    /// <summary>
    /// 7.10.2, /Order 3 with nine inputs (decided on 17.11.2026: always cubic): the input with 4 samples is interpolated by the spline —
    /// halfway between 64 and 255 of 255 with 0 before and 64 after, not the linear 0.625. Before, above 8 inputs /Order 3 was linear
    /// (docs/FEHLERGEDAECHTNIS.md, No. 908).
    /// </summary>
    [Fact]
    public void SampledFunction_Order3_NineInputs_Cubic()
    {
        byte[] pattern = [0x00, 0x40, 0xFF, 0x40];
        var data = new string([.. Enumerable.Range(0, 4 * 256).Select(i => (char)pattern[i % 4])]);
        var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /FunctionType 0 /Domain [{string.Join(" ", Enumerable.Repeat("0 1", 9))}] /Range [0 1] /Size [4 {string.Join(" ", Enumerable.Repeat("2", 8))}] " +
            $"/Order 3 /BitsPerSample 8 /Length {data.Length} >>\nstream\n{data}\nendstream"));
        var f = PdfKnead.Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(3), doc)!;
        var output = new double[1];
        f.Evaluate([0.5, .. Enumerable.Repeat(0.0, 8)], output);
        Assert.Equal(0.5 * 64 / 255 + 0.5625, output[0], 9);
    }

    /// <summary>
    /// 7.11.3–7.11.5 as a writer (Bericht M5 Zeilen 51, 52, 58, 61, 66): related files (/RF, pairs of name and stream) stay with their data
    /// and the specification holding them becomes an object of its own; a URL specification keeps /FS /URL and gets /UF as the same ASCII text
    /// as /F; with a platform name (/DOS) no /F is made from /UF; /Desc stays as it is.
    /// </summary>
    [Fact]
    public void FileSpecifications_RelatedFilesUrlPlatformAndDescription_AsWritten()
    {
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R 5 0 R 6 0 R] >>",
            "<< /Type /Annot /Subtype /FileAttachment /Rect [10 10 30 30] /FS << /F (teil1.dat) /Desc (Musterbeschreibung) " +
            "/EF << /F 7 0 R >> /RF << /F 8 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [40 10 100 30] /A << /S /GoToR /F << /FS /URL /F (https://example.org/a.pdf) >> /D [0 /Fit] >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [40 40 100 60] /A << /S /GoToR /F << /DOS (MUSTER.PDF) /UF (Muster.pdf) >> /D [0 /Fit] >> >>",
            "<< /Type /EmbeddedFile /Length 3 >>\nstream\nABC\nendstream",
            "[(teil2.dat) 9 0 R]",
            "<< /Type /EmbeddedFile /Length 3 >>\nstream\nXYZ\nendstream");
        using var saved = Saved(file);
        PdfDictionary Annot(int i) => saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(i)!.AsDictionary()!;
        Assert.IsType<PdfReference>(Annot(0).GetRaw(PdfName.Get("FS")));
        var attached = Annot(0).GetDictionary(PdfName.Get("FS"))!;
        Assert.Equal("Musterbeschreibung", attached.GetString(PdfName.Get("Desc"))!.ToText());
        var related = attached.GetDictionary(PdfName.Get("RF"))!.GetArray(PdfName.Get("F"))!;
        Assert.Equal("teil2.dat", Assert.IsType<PdfString>(related.Get(0)).ToText());
        Assert.Equal("XYZ", Encoding.ASCII.GetString(saved.GetStreamData(Assert.IsType<PdfStream>(related.Get(1)))));
        var url = Annot(1).GetDictionary(PdfName.Get("A"))!.GetDictionary(PdfName.Get("F"))!;
        Assert.Equal("URL", url.GetName(PdfName.Get("FS"))?.Value);
        Assert.Equal("https://example.org/a.pdf", url.GetString(PdfName.Get("UF"))!.ToText());
        var platform = Annot(2).GetDictionary(PdfName.Get("A"))!.GetDictionary(PdfName.Get("F"))!;
        Assert.False(platform.ContainsKey(PdfName.Get("F")));
        Assert.Equal("MUSTER.PDF", Encoding.ASCII.GetString(platform.GetString(PdfName.Get("DOS"))!.Bytes));
    }

    /// <summary>
    /// 7.10.2 with more than 8 inputs (a DeviceN of many colourants): the samples are interpolated, not the nearest one taken — multilinear over
    /// the inputs between two samples (9 inputs here), linear on the simplex of the cell beyond 12 such inputs (14 here). With a table that is
    /// linear (each corner the share of inputs at 1), both give the linear value: 0.5 in the middle of 9 inputs, 0.3 at 0.3 in all of 14, the
    /// mean where the inputs alternate 0.25 and 0.75. The nearest sample gave 1, 0 and steps (docs/FEHLERGEDAECHTNIS.md, No. 785). The bound of the work per object
    /// (No. 908): FunctionBudgetTests.
    /// </summary>
    [Fact]
    public void SampledFunction_ManyInputs_Interpolated()
    {
        PdfKnead.Core.Pdf.Graphics.PdfFunction Function(string dict, string data)
        {
            var doc = PdfDocument.Open(FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
                $"<< /FunctionType 0 {dict} /BitsPerSample 8 /Length {data.Length} >>\nstream\n{data}\nendstream"));
            return PdfKnead.Core.Pdf.Graphics.PdfFunction.Create(doc.GetObject(3), doc)!;
        }
        PdfKnead.Core.Pdf.Graphics.PdfFunction Linear(int inputs) => Function(
            $"/Domain [{string.Join(" ", Enumerable.Repeat("0 1", inputs))}] /Range [0 1] /Size [{string.Join(" ", Enumerable.Repeat("2", inputs))}]",
            new string([.. Enumerable.Range(0, 1 << inputs).Select(i => (char)(int)Math.Round(System.Numerics.BitOperations.PopCount((uint)i) * 255.0 / inputs))]));
        static double At(PdfKnead.Core.Pdf.Graphics.PdfFunction f, params double[] x)
        {
            Span<double> output = stackalloc double[1];
            f.Evaluate(x, output);
            return output[0];
        }
        Assert.Equal(0.5, At(Linear(9), [.. Enumerable.Repeat(0.5, 9)]), 9);
        var fourteen = Linear(14);
        Assert.Equal(0.3, At(fourteen, [.. Enumerable.Repeat(0.3, 14)]), 9);
        Assert.Equal(0.5, At(fourteen, [.. Enumerable.Range(0, 14).Select(i => i % 2 == 0 ? 0.25 : 0.75)]), 2);
    }

    /// <summary>
    /// 7.7.2 (Table 29) as a writer: the keys of /PageLabels are page indices and index 0 has a value. A tree starting at page 3 gets a range at
    /// 0 with the numbers the pages before showed (S-A A91: read as plain numbers); a range beyond the last page (it labels none) goes. Every
    /// page keeps its label (docs/FEHLERGEDAECHTNIS.md, No. 786).
    /// </summary>
    [Fact]
    public void PageLabels_IndexZero_NoKeyBeyondTheLastPage()
    {
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [2 << /S /r >> 7 << /S /A >>] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        using var before = PdfDocument.Open(file);
        Assert.Equal([new PageLabel("D", "", 1), new PageLabel("D", "", 2), new PageLabel("r", "", 1)], PageLabels.Of(before)!);
        using var saved = Saved(file);
        var nums = saved.Catalog!.GetDictionary(PdfName.Get("PageLabels"))!.GetArray(PdfName.Get("Nums"))!;
        Assert.Equal([0L, 2L], Enumerable.Range(0, nums.Count / 2).Select(i => ((PdfInteger)nums[2 * i]).Value));
        Assert.Equal(PageLabels.Of(before)!, PageLabels.Of(saved)!);
    }

    /// <summary>
    /// 7.8.2 as a reader: operands come before their operator, as direct objects; operands left over before an operator (here three before
    /// re) are not taken, a reference (1 0 R) is no operand and an unknown operator is passed over; between BX and EX — nested too —
    /// unknown operators are passed over without an error, and drawing goes on after them (Bericht M5 Zeilen 28, 30). Every rectangle is drawn
    /// where its four operands put it.
    /// </summary>
    [Fact]
    public void ContentStream_OperandsUnknownOperatorsAndCompatibilitySections()
    {
        const string content = "5 6 7 0 0 10 10 re f 1 0 R 20 20 10 10 re f BX /X 1 foo BX bar EX EX 40 40 10 10 re f qux 60 60 10 10 re f";
        var file = FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        using var doc = PdfDocument.Open(file);
        var fills = Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Items.ToArray().Where(i => i.Kind == Core.Pdf.Graphics.DisplayItemKind.Fill).ToList();
        Assert.Equal(4, fills.Count);
        Assert.Equal([0f, 20f, 40f, 60f], fills.Select(f => MathF.Round(Math.Min(f.Left, f.Right))));
    }

    /// <summary>
    /// 7.7.2, Table 29, /Version: the version of the file is the higher of the header and /Version of the catalogue — /Version /1.7 over a 1.4
    /// header counts, /Version /1.4 under a 1.7 header does not (Bericht M5 Zeile 2).
    /// </summary>
    [Fact]
    public void Catalog_Version_CountsOnlyWhereHigherThanTheHeader()
    {
        static byte[] With(string header, string version) => Encoding.Latin1.GetBytes(
            Encoding.Latin1.GetString(FileOf($"<< /Type /Catalog /Pages 2 0 R /Version /{version} >>", "<< /Type /Pages /Kids [] /Count 0 >>"))
                .Replace("%PDF-1.7", "%PDF-" + header));
        using (var higher = PdfDocument.Open(With("1.4", "1.7"))) Assert.Equal("1.7", higher.Version);
        using (var lower = PdfDocument.Open(With("1.7", "1.4")))
        {
            Assert.Equal("1.7", lower.HeaderVersion);
            Assert.Equal("1.7", lower.Version);
        }
    }

    /// <summary>
    /// 7.7.2 Table 29 (/Pages indirect) and 7.7.3.2 Table 30, 7.7.3.3 Table 31 as a writer: the catalogue names the root of the page tree by
    /// reference; the root has /Type /Pages and no /Parent; every kid is indirect, a page or a node, with /Parent pointing at its node by
    /// reference; /Count is the number of pages below; no node or page twice — also for a source whose tree is three levels deep with a page
    /// taken twice (Bericht M5 Zeilen 4, 14).
    /// </summary>
    [Fact]
    public void PageTree_WrittenAsTables29To31Ask()
    {
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 2 >>",
            "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 300 300] >>");
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        var items = PageItem.From(doc);
        PdfWriter.Write(doc, [items[0], items[1], items[0]], output, new PdfSaveOptions());
        using var saved = PdfDocument.Open(output.ToArray());
        var rootRef = Assert.IsType<PdfReference>(saved.Catalog!.GetRaw(PdfName.Pages));
        var root = (PdfDictionary)saved.Resolve(rootRef.Id)!;
        Assert.Equal("Pages", root.GetName(PdfName.Type)?.Value);
        Assert.False(root.ContainsKey(PdfName.Parent));
        var seen = new HashSet<int>();
        int Walk(PdfReference nodeRef, int depth)
        {
            Assert.True(seen.Add(nodeRef.Id.Number), $"Objekt {nodeRef.Id.Number} zweimal im Seitenbaum");
            var node = (PdfDictionary)saved.Resolve(nodeRef.Id)!;
            if (node.GetName(PdfName.Type)?.Value == "Page") return 1;
            Assert.Equal("Pages", node.GetName(PdfName.Type)?.Value);
            var count = 0;
            foreach (var kid in node.GetArray(PdfName.Get("Kids"))!)
            {
                var kidRef = Assert.IsType<PdfReference>(kid);
                var parent = Assert.IsType<PdfReference>(((PdfDictionary)saved.Resolve(kidRef.Id)!).GetRaw(PdfName.Parent));
                Assert.Equal(nodeRef.Id.Number, parent.Id.Number);
                count += Walk(kidRef, depth + 1);
            }
            Assert.Equal(count, node.GetInteger(PdfName.Count));
            return count;
        }
        Assert.Equal(3, Walk(rootRef, 0));
        Assert.Equal(3, saved.PageCount);
    }

    /// <summary>
    /// S-A A92 (7.7.2, 7.7.3: /Type is required in the catalogue and in the nodes of the page tree): a catalogue without /Type is read as the
    /// one /Root names, a node with /Kids without /Type as an intermediate node, one without both as a page — by their place, as widely used
    /// readers do. Written, every one has its /Type.
    /// </summary>
    [Fact]
    public void Reader_CatalogueAndNodesWithoutType_ReadByTheirPlace_WrittenWithType()
    {
        var file = FileOf(
            "<< /Pages 2 0 R >>",
            "<< /Kids [3 0 R] /Count 1 >>",
            "<< /Parent 2 0 R /MediaBox [0 0 200 300] >>");
        using var doc = PdfDocument.Open(file);
        Assert.Equal(1, doc.PageCount);
        Assert.Equal(300, doc.Pages[0].MediaBox.Top);
        using var saved = Saved(file);
        Assert.Equal("Catalog", saved.Catalog!.GetName(PdfName.Type)?.Value);
        Assert.Equal("Page", saved.Pages[0].Dictionary.GetName(PdfName.Type)?.Value);
        Assert.Equal("Pages", saved.Pages[0].Dictionary.GetDictionary(PdfName.Parent)!.GetName(PdfName.Type)?.Value);
    }

    /// <summary>
    /// 7.8.3 sentences 13 and 15 as a writer (decided on 12.11.2026): each page names only the resources its content uses — also what
    /// a form without resources of its own draws with the page's (here an image) —, the default colour spaces stay, a category left empty goes.
    /// Pages with the same use share one object; a page whose content does not decode keeps all (another reader may read it). Before, the
    /// resources of the /Pages node went onto every page whole (S-B B49, given up; docs/FEHLERGEDAECHTNIS.md, No. 788).
    /// </summary>
    [Fact]
    public void Pages_OnlyTheResourcesTheyUse()
    {
        static string Stream(string content, string dict = "") => $"<< {dict} /Length {content.Length} >>\nstream\n{content}\nendstream";
        const string first = "BT /F1 12 Tf 20 100 Td (Max) Tj ET /GS1 gs /CS1 cs 0.5 sc /OC /MC1 BDC 0 0 10 10 re f EMC /Fm1 Do";
        const string second = "BT /F2 12 Tf 20 100 Td (Erika) Tj ET";
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 15 0 R 16 0 R] /Count 4 /MediaBox [0 0 200 200] /Resources << /Font << /F1 7 0 R /F2 8 0 R >> " +
            "/XObject << /Fm1 9 0 R /Im1 10 0 R >> /ExtGState << /GS1 << /CA 1 >> /GS2 << /CA 0.5 >> >> /ColorSpace << /CS1 [/CalGray << /WhitePoint [0.95 1 1.09] >>] " +
            "/DefaultRGB /DeviceRGB >> /Pattern << /P1 11 0 R >> /Shading << /Sh1 12 0 R >> /Properties << /MC1 << /Name (eins) >> /MC2 << /Name (zwei) >> >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>",
            Stream(first),
            Stream(second),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>",
            Stream("q 10 0 0 10 50 50 cm /Im1 Do Q", "/Type /XObject /Subtype /Form /BBox [0 0 200 200]"),
            Stream("\0", "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8"),
            Stream("0 0 1 1 re f", "/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 1 1] /XStep 1 /YStep 1 /Resources << >>"),
            "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >>",
            Stream("kein Flate", "/Filter /FlateDecode"),
            Stream(second),
            "<< /Type /Page /Parent 2 0 R /Contents 13 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Contents 14 0 R >>");
        using var saved = Saved(file, new PdfSaveOptions { Version = "1.7" });
        static string Names(PdfDictionary resources) => string.Join(" ", resources.Entries
            .Where(e => resources.Get(e.Key) is PdfDictionary).OrderBy(e => e.Key.Value)
            .Select(e => e.Key.Value + ":" + string.Join(",", ((PdfDictionary)resources.Get(e.Key)).Entries.Select(n => n.Key.Value).Order())));
        Assert.Equal("ColorSpace:CS1,DefaultRGB ExtGState:GS1 Font:F1 Properties:MC1 XObject:Fm1,Im1", Names(saved.Pages[0].Resources!));
        Assert.Equal("ColorSpace:DefaultRGB Font:F2", Names(saved.Pages[1].Resources!));
        // the content that does not decode: every resource stays
        Assert.Contains("Shading:Sh1", Names(saved.Pages[2].Resources!));
        // the same use: one object
        Assert.Equal(((PdfReference)saved.Pages[1].Dictionary.GetRaw(PdfName.Resources)!).Id, ((PdfReference)saved.Pages[3].Dictionary.GetRaw(PdfName.Resources)!).Id);
    }

    /// <summary>
    /// 7.8.3 as a writer, the rest of the second review: pages that keep different names of the same resources get different objects, also where
    /// a name holds a space and a solidus (#20, #2F; 7.3.5) — the name „a Font/b“ of one page and the names „a“ and „b“ of the other made the same
    /// key, the second page got the first page's resources and lost its font (Bericht M5 Zeile 41; docs/FEHLERGEDAECHTNIS.md, No. 904).
    /// </summary>
    [Fact]
    public void Pages_NamesWithSpaceAndSolidus_KeepTheirOwnResources()
    {
        static string Stream(string content) => $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 200 200] /Resources << /Font << /a 7 0 R /b 7 0 R /a#20Font#2Fb 7 0 R /c 7 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>",
            Stream("BT /a 12 Tf 20 100 Td (Max) Tj /b 12 Tf (Max) Tj ET"),
            Stream("BT /a#20Font#2Fb 12 Tf 20 100 Td (Erika) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        using var saved = Saved(file, new PdfSaveOptions { Version = "1.7" });
        static string Fonts(PdfDictionary resources) => string.Join(",", resources.GetDictionary(PdfName.Get("Font"))!.Entries.Select(n => n.Key.Value).Order());
        Assert.Equal("a,b", Fonts(saved.Pages[0].Resources!));
        Assert.Equal("a Font/b", Fonts(saved.Pages[1].Resources!));
    }

    /// <summary>
    /// A simple font whose /Widths names a number tree and whose /FontDescriptor names an outline item (test file bookmarkgetcolor): written with
    /// the widths the display sets the codes with — 256 numbers from /FirstChar 0 — and a descriptor of its substitute, as ISO 32000-2,
    /// 9.6.2.1, Table 109 asks; the text shows as before. Before, both went into the copy as they were, and a reader that read them set every
    /// character on top of the one before (docs/FEHLERGEDAECHTNIS.md, No. 886).
    /// </summary>
    [Fact]
    public void SimpleFont_WidthsAndDescriptorOfTheWrongKind_WrittenAsTheDisplayShowsThem()
    {
        static string Stream(string content, string dict = "") => $"<< {dict} /Length {content.Length} >>\nstream\n{content}\nendstream";
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /Font << /F2 5 0 R >> >> >>",
            Stream("BT /F2 20 Tf 10 100 Td (TEST) Tj ET"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /NewYork /FirstChar 0 /LastChar 255 /Widths 6 0 R /FontDescriptor 7 0 R /Encoding /MacRomanEncoding >>",
            "<< /Nums [0 << /S /D >>] /Limits [0 0] >>",
            "<< /Title (First) /Parent 1 0 R >>");
        using var original = PdfDocument.Open(file);
        var before = PdfFont.Get((PdfDictionary)original.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F2")), original);
        using var saved = Saved(file, new PdfSaveOptions { Version = "1.7" });
        var font = (PdfDictionary)saved.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F2"));
        var widths = Assert.IsType<PdfArray>(font.Get(PdfName.Get("Widths")));
        Assert.Equal(256, widths.Count);
        Assert.Equal(0, font.GetInteger(PdfName.Get("FirstChar")));
        Assert.Equal("FontDescriptor", font.GetDictionary(PdfName.Get("FontDescriptor"))!.GetName(PdfName.Type)?.Value);
        Assert.IsType<PdfReference>(font.GetRaw(PdfName.Get("FontDescriptor")));
        // the text sets its letters with the same widths as before
        var after = PdfFont.Get(font, saved);
        foreach (var c in "TEST"u8.ToArray()) Assert.Equal(before.Glyph(c, 1).Width, after.Glyph(c, 1).Width, 6);
    }

    /// <summary>
    /// An inline image naming its colour space twice, as abbreviation and full name with different values (/CS /RGB, /ColorSpace /CS0 —
    /// test file issue14256): some readers take the one, some the other, so the resource the full name gives stays when the page keeps only the
    /// resources it uses; an unused one still goes. Before, /CS0 went, and a reader taking the full name drew nothing there
    /// (docs/FEHLERGEDAECHTNIS.md, No. 885).
    /// </summary>
    [Fact]
    public void InlineImage_ColourSpaceUnderBothNames_TheResourceOfEitherStays()
    {
        static string Stream(string content, string dict = "") => $"<< {dict} /Length {content.Length} >>\nstream\n{content}\nendstream";
        const string content = "q 10 0 0 10 20 20 cm BI /W 1 /H 1 /BPC 8 /CS /RGB /ColorSpace /CS0 /F /AHx ID ff0000> EI Q";
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /ColorSpace << /CS0 [/CalRGB << /WhitePoint [0.95 1 1.09] >>] " +
            "/CS1 [/CalGray << /WhitePoint [0.95 1 1.09] >>] >> >> >>",
            Stream(content));
        using var saved = Saved(file, new PdfSaveOptions { Version = "1.7" });
        var spaces = saved.Pages[0].Resources!.GetDictionary(PdfName.Get("ColorSpace"))!;
        Assert.True(spaces.ContainsKey(PdfName.Get("CS0")));
        Assert.False(spaces.ContainsKey(PdfName.Get("CS1")));
    }

    /// <summary>
    /// 7.11.3 (Table 43) as a writer, related files and collection items: a file specification with /EF and /RF given directly (here in a file
    /// attachment annotation) becomes an object of its own with /Type /Filespec, its /CI (a collection item, required indirect) as well
    /// (docs/FEHLERGEDAECHTNIS.md, No. 780).
    /// </summary>
    [Fact]
    public void FileSpecifications_RelatedFilesAndCollectionItem_Indirect()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /FileAttachment /Rect [10 10 30 30] /FS << /F (a.txt) /EF << /F 5 0 R >> /RF << /F 6 0 R >> /CI << /Type /CollectionItem /Seite 1 >> >> >>",
            "<< /Type /EmbeddedFile /Length 3 >>\nstream\nABC\nendstream",
            "[(teil.txt) 5 0 R]"));
        var annot = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!;
        Assert.IsType<PdfReference>(annot.GetRaw(PdfName.Get("FS")));
        var spec = annot.GetDictionary(PdfName.Get("FS"))!;
        Assert.Equal("Filespec", spec.GetName(PdfName.Type)?.Value);
        Assert.IsType<PdfReference>(spec.GetRaw(PdfName.Get("CI")));
        Assert.Equal(1, spec.GetDictionary(PdfName.Get("CI"))!.GetInteger(PdfName.Get("Seite")));
        Assert.NotNull(spec.GetDictionary(PdfName.Get("RF"))?.GetArray(PdfName.Get("F")));
    }

    /// <summary>
    /// 7.7.2 (Table 29) as a writer: the further entries of the catalogue — /SpiderInfo, /OutputIntents, /PieceInfo, /OCProperties, /Legal,
    /// /Requirements, /Collection, /NeedsRendering (in 1.7), /DSS, /AF, /DPartRoot — are carried over; what they mean is checked in their
    /// modules (PLAN §5).
    /// </summary>
    [Fact]
    public void Catalogue_FurtherEntries_CarriedOver()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R /SpiderInfo << /V 1.0 >> /OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (CGATS TR 001) >>] " +
            "/PieceInfo << /PDFKnead << /LastModified (D:20261112120000Z) /Private 1 >> >> /OCProperties << /OCGs [4 0 R] /D << /Order [4 0 R] >> >> " +
            "/Legal << /JavaScriptActions 0 >> /Requirements [<< /Type /Requirement /S /EnableJavaScripts >>] /Collection << /Type /Collection >> " +
            "/NeedsRendering false /DSS << /Type /DSS >> /AF [5 0 R] /DPartRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /DPart 7 0 R >>",
            "<< /Type /OCG /Name (Ebene) >>",
            "<< /Type /Filespec /F (daten.txt) /UF (daten.txt) /AFRelationship /Data >>",
            "<< /Type /DPartRoot /DPartRootNode 7 0 R >>",
            "<< /Type /DPart /Parent 6 0 R /Start 3 0 R /End 3 0 R >>"), new PdfSaveOptions { Version = "1.7" });
        var catalog = saved.Catalog!;
        foreach (var key in (string[])["SpiderInfo", "OutputIntents", "PieceInfo", "OCProperties", "Legal", "Requirements", "Collection", "NeedsRendering", "DSS", "AF", "DPartRoot"])
            Assert.True(catalog.ContainsKey(PdfName.Get(key)), key);
        // the page stays within its document part
        Assert.NotNull(saved.Pages[0].Dictionary.GetDictionary(PdfName.Get("DPart")));
    }

    /// <summary>
    /// 7.7.3.3 and 7.11.3 (Bericht M5 Zeilen 19, 62, 63, 68, 100): what PDF-Knead neither shows nor makes itself stays as its writer wrote it —
    /// /BoxColorInfo of a page, and in the file specification of a link to another file its /ID (two strings), /V (volatile) and /Thumb.
    /// </summary>
    [Fact]
    public void ForeignFileReferencesAndBoxColours_StayAsWritten()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /BoxColorInfo << /CropBox << /C [1 0 0] /W 2 >> >> /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /A << /S /GoToR /D [0 /Fit] /F << /Type /Filespec /F (andere.pdf) " +
            "/UF (andere.pdf) /ID [<0102030405060708> <0807060504030201>] /V true /Thumb 5 0 R >> >> >>",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\nA\nendstream"));
        var page = saved.Pages[0].Dictionary;
        Assert.NotNull(page.GetDictionary(PdfName.Get("BoxColorInfo"))?.GetDictionary(PdfName.Get("CropBox")));
        var spec = page.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!.GetDictionary(PdfName.Get("A"))!.GetDictionary(PdfName.Get("F"))!;
        var id = spec.GetArray(PdfName.Get("ID"))!;
        Assert.Equal(2, id.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, Assert.IsType<PdfString>(id.Get(0)).Bytes);
        Assert.Equal(PdfBoolean.True, spec.Get(PdfName.Get("V")));
        Assert.IsType<PdfStream>(spec.Get(PdfName.Get("Thumb")));
    }

    /// <summary>
    /// 7.7.3.3 (Table 31) as a writer: the further entries of a page — /AA, /Metadata, /ID, /PZ, /SeparationInfo, /Tabs, /TemplateInstantiated,
    /// /PresSteps, /VP, /AF — are carried over; what they mean is checked in their modules (PLAN §5).
    /// </summary>
    [Fact]
    public void Pages_FurtherEntries_CarriedOver()
    {
        using var saved = Saved(FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /AA << /O << /S /GoTo /D [3 0 R /Fit] >> >> /Metadata 4 0 R /ID (seite-1) /PZ 1.5 " +
            "/SeparationInfo << /Pages [3 0 R] /DeviceColorant /Cyan >> /Tabs /R /TemplateInstantiated /Vorlage /PresSteps << /Type /NavNode >> " +
            "/VP [<< /Type /Viewport /BBox [0 0 100 100] >>] /AF [5 0 R] >>",
            "<< /Type /Metadata /Subtype /XML /Length 4 >>\nstream\n<x/>\nendstream",
            "<< /Type /Filespec /F (daten.txt) /UF (daten.txt) /AFRelationship /Data >>"));
        var page = saved.Pages[0].Dictionary;
        foreach (var key in (string[])["AA", "Metadata", "ID", "PZ", "SeparationInfo", "Tabs", "TemplateInstantiated", "PresSteps", "VP", "AF"])
            Assert.True(page.ContainsKey(PdfName.Get(key)), key);
    }

    /// <summary>
    /// 7.7.3.3 (Table 31): /DPart is required within the range of a document part and not allowed otherwise. A page of another file merged in
    /// (its document parts do not come along) loses it; before, it named a part of a hierarchy the copy does not have, and that part was written
    /// as an object nothing else names. A page of the file itself taken twice: one part over both copies (the parts are rebuilt,
    /// DocumentParts_RebuiltForPagesDeletedOrReordered; docs/FEHLERGEDAECHTNIS.md, No. 790).
    /// </summary>
    [Fact]
    public void Pages_DPartOnlyWithinADocumentPart()
    {
        var withParts = FileOf(
            "<< /Type /Catalog /Pages 2 0 R /DPartRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /DPart 5 0 R >>",
            "<< /Type /DPartRoot /DPartRootNode 5 0 R >>",
            "<< /Type /DPart /Parent 4 0 R /Start 3 0 R /End 3 0 R >>");
        var plain = FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        using var target = PdfDocument.Open(plain);
        using var other = PdfDocument.Open(withParts);
        using var output = new MemoryStream();
        PdfWriter.Write(target, [.. PageItem.From(target), .. PageItem.From(other)], output, new PdfSaveOptions());
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.Equal(2, saved.PageCount);
        Assert.False(saved.Pages[1].Dictionary.ContainsKey(PdfName.Get("DPart")));

        // the page of the file with document parts taken twice: one part over both copies (decided on 12.11.2026: rebuilt)
        using var reordered = new MemoryStream();
        PdfWriter.Write(other, [.. PageItem.From(other), .. PageItem.From(other)], reordered, new PdfSaveOptions());
        using var changed = PdfDocument.Open(reordered.ToArray());
        Assert.Equal([2], PartsInOrder(changed).Select(l => l.Pages.Count));
    }

    /// <summary>
    /// The leaves of the document parts of a file in depth-first order with the pages of their ranges (indices); asserts 14.12.2 and 14.12.3 on
    /// the way: every page in the range of exactly one leaf, the leaves following the pages, /DPart of each page naming its leaf, /Parent
    /// naming the parent, no empty /DParts, /End only for more than one page.
    /// </summary>
    private static List<(PdfDictionary Leaf, List<int> Pages)> PartsInOrder(PdfDocument doc)
    {
        var root = doc.Catalog!.GetDictionary(PdfName.Get("DPartRoot"))!;
        var indexOf = Enumerable.Range(0, doc.PageCount).ToDictionary(i => doc.Pages[i].Id!.Value.Number);
        var leaves = new List<(PdfDictionary, List<int>)>();
        void Walk(PdfDictionary node, PdfReference? self, PdfDictionary parent)
        {
            Assert.Same(parent, node.GetDictionary(PdfName.Parent));
            if (node.GetArray(PdfName.Get("DParts")) is { } groups)
            {
                Assert.NotEmpty(groups);
                foreach (var group in groups.Select(g => (PdfArray)(g is PdfReference r ? doc.Resolve(r.Id) : g)))
                {
                    Assert.NotEmpty(group);
                    foreach (var child in group) Walk((PdfDictionary)doc.Resolve(((PdfReference)child).Id), (PdfReference)child, node);
                }
                return;
            }
            var start = indexOf[((PdfReference)node.GetRaw(PdfName.Get("Start"))!).Id.Number];
            var end = node.GetRaw(PdfName.Get("End")) is PdfReference e ? indexOf[e.Id.Number] : start;
            if (node.ContainsKey(PdfName.Get("End"))) Assert.True(end > start);
            var pages = Enumerable.Range(start, end - start + 1).ToList();
            foreach (var p in pages) Assert.Equal(self!.Id, ((PdfReference)doc.Pages[p].Dictionary.GetRaw(PdfName.Get("DPart"))!).Id);
            leaves.Add((node, pages));
        }
        var top = root.GetRaw(PdfName.Get("DPartRootNode")) as PdfReference;
        Walk(root.GetDictionary(PdfName.Get("DPartRootNode"))!, top, root);
        Assert.Equal(Enumerable.Range(0, doc.PageCount), leaves.SelectMany(l => l.Item2));
        return leaves;
    }

    /// <summary>
    /// 14.12 with pages deleted or reordered (decided on 12.11.2026: rebuild rather than remove): a job of two parts under a
    /// customer node, part 1 on pages 1–2, part 2 on page 3. Page 2 deleted: part 1 on page 1 alone (without /End). Pages 3, 1, 2: part 2 first,
    /// then part 1, under the same customer. Pages 1, 3, 2: part 1 split in two — each copy with the metadata of part 1. Every page lies in
    /// exactly one part, the parts follow the pages, the names of the levels stay (docs/FEHLERGEDAECHTNIS.md, No. 790).
    /// </summary>
    [Fact]
    public void DocumentParts_RebuiltForPagesDeletedOrReordered()
    {
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R /DPartRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /DPart 9 0 R >>",
            "<< /Type /Page /Parent 2 0 R /DPart 9 0 R >>",
            "<< /Type /Page /Parent 2 0 R /DPart 10 0 R >>",
            "<< /Type /DPartRoot /DPartRootNode 7 0 R /NodeNameList [/Job /Kunde /Teil] >>",
            "<< /Type /DPart /Parent 6 0 R /DParts [[8 0 R]] >>",
            "<< /Type /DPart /Parent 7 0 R /DParts [[9 0 R 10 0 R]] /DPM << /Kunde (Max Mustermann) >> >>",
            "<< /Type /DPart /Parent 8 0 R /Start 3 0 R /End 4 0 R /DPM << /Nr 1 >> >>",
            "<< /Type /DPart /Parent 8 0 R /Start 5 0 R /DPM << /Nr 2 >> >>");
        using var doc = PdfDocument.Open(file);
        List<(PdfDictionary Leaf, List<int> Pages)> Parts(params int[] order)
        {
            using var output = new MemoryStream();
            var items = PageItem.From(doc);
            PdfWriter.Write(doc, [.. order.Select(i => items[i])], output, new PdfSaveOptions());
            using var saved = PdfDocument.Open(output.ToArray());
            Assert.Equal(3, saved.Catalog!.GetDictionary(PdfName.Get("DPartRoot"))!.GetArray(PdfName.Get("NodeNameList"))!.Count);
            return PartsInOrder(saved);
        }
        static long Nr(PdfDictionary leaf) => leaf.GetDictionary(PdfName.Get("DPM"))!.GetInteger(PdfName.Get("Nr"))!.Value;
        var deleted = Parts(0, 2);
        Assert.Equal([1L, 2L], deleted.Select(l => Nr(l.Leaf)));
        Assert.Equal([1, 1], deleted.Select(l => l.Pages.Count));
        var swapped = Parts(2, 0, 1);
        Assert.Equal([2L, 1L], swapped.Select(l => Nr(l.Leaf)));
        Assert.Equal([1, 2], swapped.Select(l => l.Pages.Count));
        var split = Parts(0, 2, 1);
        Assert.Equal([1L, 2L, 1L], split.Select(l => Nr(l.Leaf)));
    }

    /// <summary>
    /// 7.9.2.4: a byte string holds binary data, every byte any value — rewriting the UTF-8 text strings of a 2.0 file for a lower version
    /// (FG 784) must not touch it, even where it happens to begin like UTF-8 with its byte order mark: the Web Capture identifier /ID of a
    /// page and an embedded file's /CheckSum stay byte for byte, the text string beside them is rewritten (docs/FEHLERGEDAECHTNIS.md, No. 796).
    /// </summary>
    [Fact]
    public void ByteStrings_KeptByteForByte_WhenUtf8TextIsRewritten()
    {
        const string id = "EFBBBF4D61784D75737465726D616E6E";         // 16 bytes: the BOM, then valid UTF-8 ("MaxMustermann")
        const string sum = "EFBBBF4572696B614D75737465726D61";        // 16 bytes: the BOM, then "ErikaMusterma"
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Version /2.0 /Names << /EmbeddedFiles << /Names [(a.txt) 5 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /ID <{id}> /Annots [4 0 R] >>",
            $"<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /Contents <EFBBBF{Convert.ToHexString(Encoding.UTF8.GetBytes("Café"))}> >>",
            "<< /Type /Filespec /F (a.txt) /UF (a.txt) /EF << /F 6 0 R >> >>",
            $"<< /Type /EmbeddedFile /Length 3 /Params << /Size 3 /CheckSum <{sum}> >> >>\nstream\nabc\nendstream");
        using var saved = Saved(file, new PdfSaveOptions { Version = "1.7" });
        var page = saved.Pages[0].Dictionary;
        Assert.Equal(id, Convert.ToHexString(page.GetString(PdfName.Get("ID"))!.Bytes));
        var text = page.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!.GetString(PdfName.Get("Contents"))!;
        Assert.Equal("Café", text.ToText());
        Assert.False(text.Bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));   // the text string rewritten (FG 784)
        var tree = saved.Catalog!.GetDictionary(PdfName.Get("Names"))!.GetDictionary(PdfName.Get("EmbeddedFiles"))!;
        var spec = tree.GetArray(PdfName.Get("Names"))!.Get(1)!.AsDictionary()!;
        var stream = (PdfStream)spec.GetDictionary(PdfName.Get("EF"))!.Get(PdfName.Get("F"))!;
        Assert.Equal(sum, Convert.ToHexString(stream.Dictionary.GetDictionary(PdfName.Get("Params"))!.GetString(PdfName.Get("CheckSum"))!.Bytes));
    }

    /// <summary>
    /// 7.9.2.2.1 sentence 19: a reader handles characters beyond the basic plane — a surrogate pair in UTF-16BE, four bytes in UTF-8 (2.0) —, and
    /// writes them back; 7.9.2.2.2: a language escape (ESC, two characters of language, optionally two of country, ESC) is no text — removed
    /// for display, search and redaction, with and without the country.
    /// </summary>
    [Fact]
    public void TextStrings_SupplementaryCharactersAndLanguageEscapes()
    {
        const string clef = "\U0001D11E";
        Assert.Equal(clef, PdfTextEncoding.Decode([0xFE, 0xFF, 0xD8, 0x34, 0xDD, 0x1E]));
        Assert.Equal(clef, PdfTextEncoding.Decode([0xEF, 0xBB, 0xBF, 0xF0, 0x9D, 0x84, 0x9E]));
        var encoded = PdfTextEncoding.Encode("Max " + clef);
        Assert.True(encoded.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]));
        Assert.Equal("Max " + clef, PdfTextEncoding.Decode(encoded));
        Assert.Equal("Max", PdfTextEncoding.Decode([0xFE, 0xFF, 0x00, 0x1B, 0x00, 0x64, 0x00, 0x65, 0x00, 0x1B, 0x00, 0x4D, 0x00, 0x61, 0x00, 0x78]));
        Assert.Equal("Max", PdfTextEncoding.Decode([0xFE, 0xFF, 0x00, 0x1B, 0x00, 0x64, 0x00, 0x65, 0x00, 0x44, 0x00, 0x45, 0x00, 0x1B,
                                                     0x00, 0x4D, 0x00, 0x61, 0x00, 0x78]));
        Assert.Equal("Erika", PdfTextEncoding.Decode([0xEF, 0xBB, 0xBF, 0x1B, 0x64, 0x65, 0x1B, 0x45, 0x72, 0x69, 0x6B, 0x61]));
        // no language marker: text between two ESC that is not two or four letters, and a single ESC, stay text — before both hid the text
        // from search, leak search and redaction (M5, 23.11.2026)
        Assert.Equal("Ab geheim cd", PdfTextEncoding.Decode([0xEF, 0xBB, 0xBF, .. "Ab \u001Bgeheim\u001B cd"u8]));
        Assert.Equal("Ab geheim", PdfTextEncoding.Decode([0xFE, 0xFF, 0x00, 0x41, 0x00, 0x62, 0x00, 0x20, 0x00, 0x1B,
                                                         .. "geheim".SelectMany(c => (byte[])[0, (byte)c])]));
    }

    /// <summary>
    /// 7.3.10 with 7.7.3.4 (S-A A67): a page entry whose reference points into the void counts as absent — the page inherits /MediaBox,
    /// /CropBox and /Resources from its ancestor. The reader did so; the writer asked whether the key was there, took no inherited value and
    /// wrote the void (docs/FEHLERGEDAECHTNIS.md, No. 798). Saved as it is, flattened and as PDF/A-2b: the page keeps its size and its font.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("flat")]
    [InlineData("pdfa")]
    public void Pages_EntryIntoTheVoid_InheritsFromTheParent(string way)
    {
        const string content = "BT /F1 12 Tf 20 250 Td (Max Mustermann) Tj ET";
        var file = FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 300] /CropBox [10 10 290 290] /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox 98 0 R /CropBox 97 0 R /Resources 99 0 R /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        using (var doc = PdfDocument.Open(file))
        {
            Assert.Equal(300, doc.Pages[0].MediaBox.Width);
            Assert.NotNull(doc.Pages[0].Resources?.GetDictionary(PdfName.Get("Font"))?.GetDictionary(PdfName.Get("F1")));
        }
        var options = way switch
        {
            "flat" => new PdfSaveOptions { Flatten = true },
            "pdfa" => new PdfSaveOptions { PdfA = PdfAConformance.A2b },
            _ => new PdfSaveOptions(),
        };
        using var saved = Saved(file, options);
        var page = saved.Pages[0];
        Assert.Equal(300, page.MediaBox.Width);
        Assert.Equal(280, page.CropBox.Width);
        Assert.IsType<PdfArray>(page.Dictionary.Get(PdfName.MediaBox));
        var text = string.Concat(Core.Pdf.Content.PageInterpreter.Build(saved, page).Text.Select(t => t.Text));
        Assert.Contains("Max Mustermann", text);
    }

    /// <summary>
    /// 7.9.2.4 with 8.6.6.3, 7.9.6 and Table 43: bytes that only look like UTF-8 text stay bytes when a 2.0 file is saved as 1.7 — the lookup
    /// string of an /Indexed colour space (its colours), the keys of a name tree (sentence 24: any encoding; rewritten, the order of the tree and
    /// the match with what names them would go) and the /DOS name of a file specification (docs/FEHLERGEDAECHTNIS.md, No. 802).
    /// </summary>
    [Fact]
    public void ByteStrings_PaletteTreeKeysPlatformNames_KeptByteForByte()
    {
        const string lookup = "EFBBBF4D61784D7573";                       // 9 bytes: three RGB entries that look like UTF-8 with its mark
        const string key = "EFBBBF4572696B61";                             // a tree key: the mark, then "Erika"
        const string dos = "EFBBBF4D41582E504446";                         // the mark, then "MAX.PDF"
        const string content = "q 10 0 0 10 0 0 cm /Im0 Do Q";
        var file = FileOf(
            $"<< /Type /Catalog /Pages 2 0 R /Version /2.0 /Names << /Dests << /Names [<{key}> [3 0 R /Fit]] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R /Annots [6 0 R] >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /XObject /Subtype /Image /Width 3 /Height 1 /BitsPerComponent 8 /ColorSpace [/Indexed /DeviceRGB 2 <{lookup}>] /Length 3 >>\nstream\n\u0000\u0001\u0002\nendstream",
            $"<< /Type /Annot /Subtype /Link /Rect [10 10 100 30] /A << /S /GoToR /F << /Type /Filespec /F (max.pdf) /DOS <{dos}> >> /D [0 /Fit] >> >>");
        using var saved = Saved(file, new PdfSaveOptions { Version = "1.7" });
        var image = (PdfStream)saved.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im0"))!;
        Assert.Equal(lookup, Convert.ToHexString(((PdfString)image.Dictionary.GetArray(PdfName.Get("ColorSpace"))!.Get(3)!).Bytes));
        var dests = saved.Catalog!.GetDictionary(PdfName.Get("Names"))!.GetDictionary(PdfName.Get("Dests"))!;
        Assert.Equal(key, Convert.ToHexString(((PdfString)dests.GetArray(PdfName.Get("Names"))!.Get(0)!).Bytes));
        var spec = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!.AsDictionary()!.GetDictionary(PdfName.Get("A"))!.GetDictionary(PdfName.Get("F"))!;
        Assert.Equal(dos, Convert.ToHexString(spec.GetString(PdfName.Get("DOS"))!.Bytes));
    }
}
