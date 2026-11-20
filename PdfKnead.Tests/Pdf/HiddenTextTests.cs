using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Verborgenes schwärzen“ (KONZEPT.md, section 5.18): hidden text is found by kind, and a mark of hidden content removes only the
/// characters nobody sees in its areas — visible text at the same spot stays, there is no bar, unmarked hidden text stays.
/// </summary>
[Collection(nameof(LimitCollection))]   // measures time (…_StaysFast): in parallel with the other tests the limits failed under load
public sealed class HiddenTextTests
{
    // Visible text, white text on empty paper, invisible text (mode 3) over visible text, text beside the page, text of size 0,
    // text under an opaque black box
    private const string Content =
        "BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET " +
        "1 1 1 rg BT /F1 10 Tf 300 700 Td (WEISSGEHEIM) Tj ET 0 g " +
        "BT /F1 10 Tf 72 600 Td (SICHTBARDA) Tj ET q BT 3 Tr /F1 10 Tf 72 600 Td (OCRTEXT) Tj ET Q " +
        "BT /F1 10 Tf -500 -500 Td (DRAUSSEN) Tj ET " +
        "BT /F1 0 Tf 72 500 Td (WINZIG) Tj ET " +
        "BT /F1 10 Tf 72 400 Td (VERDECKT) Tj ET 60 390 120 25 re f";

    internal static byte[] Build(string content = Content)
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content)));
        return pdf.Build();
    }

    private static bool Contains(byte[] data, string word) => PdfKnead.Cli.LeakSearch.Find(data, [word], null, out _).Count > 0;

    private static byte[] Write(PdfDocument doc, IReadOnlyList<PageItem> pages, PdfSaveOptions options)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, pages, output, options);
        return output.ToArray();
    }

    private static string Text(byte[] data)
    {
        using var doc = PdfDocument.Open(data);
        return PageText.Create(PageInterpreter.Build(doc, doc.Pages[0]).Text).Text;
    }

    /// <summary>A probe that counts its calls and the characters it was asked about, and measures with the real one.</summary>
    private sealed class CountingProbe : PdfKnead.Core.Pdf.Graphics.IVisibilityProbe
    {
        public int Calls, Candidates;

        public PdfKnead.Core.Pdf.Graphics.VisibilityMeasure Measure(PdfKnead.Core.Pdf.Graphics.DisplayList list, PdfDocument doc, PdfPage page,
            IReadOnlyList<PdfKnead.Core.Pdf.Graphics.VisibilityCandidate> candidates, CancellationToken cancel)
        {
            Calls++;
            Candidates += candidates.Count;
            return PdfKnead.Rendering.VisibilityProbe.Instance.Measure(list, doc, page, candidates, cancel);
        }
    }

    /// <summary>
    /// A page whose text is all decided without drawing — the text layer of a scan in mode 3, a word beside the page — is not drawn at all,
    /// not even its display list built (docs/FEHLERGEDAECHTNIS.md, No. 704); a page with text to measure finds what it found before, the same
    /// characters measured.
    /// </summary>
    [Fact]
    public void Scan_DrawsOnlyWhatIsNotDecidedWithout()
    {
        using var decided = PdfDocument.Open(Build("q 0.5 g 0 0 595 842 re f Q q BT 3 Tr /F1 10 Tf 72 600 Td (OCRTEXT EINS ZWEI) Tj ET Q BT /F1 10 Tf -500 -500 Td (DRAUSSEN) Tj ET"));
        var probe = new CountingProbe();
        var spots = HiddenText.Find(decided, decided.Pages[0], probe: probe);
        Assert.Equal(0, probe.Calls);
        Assert.Contains(spots, s => s.Kind == HiddenTextKind.Invisible);
        Assert.Contains(spots, s => s.Kind == HiddenTextKind.Outside);

        using var mixed = PdfDocument.Open(Build());
        var measuring = new CountingProbe();
        var found = HiddenText.Find(mixed, mixed.Pages[0], probe: measuring);
        Assert.Equal(HiddenTextKind.Suspicious, Assert.Single(found, s => s.Text.Replace(" ", "") == "WEISSGEHEIM").Kind);
        Assert.Equal(HiddenTextKind.Suspicious, Assert.Single(found, s => s.Text.Replace(" ", "") == "VERDECKT").Kind);
        // drawn are only the doubtful ones among those left: „WEISSGEHEIM“ and „VERDECKT“ (the invisible, outside and tiny ones are decided
        // before, the plainly visible ones need no drawing)
        Assert.Equal(19, measuring.Candidates);
    }

    [Fact]
    public void Scan_OnlyInAreas_DrawsOnlyThere()
    {
        // a question about a few places (the e-invoice checker: does this value show?): only the characters touching them are drawn
        using var doc = PdfDocument.Open(Build());
        var all = new CountingProbe();
        var everything = HiddenText.Scan(doc, doc.Pages[0], probe: all);
        var some = new CountingProbe();
        var areas = HiddenText.Scan(doc, doc.Pages[0], [new PdfRectangle(290, 690, 420, 715)], probe: some);
        Assert.Contains(areas.Spots, s => s.Text.Replace(" ", "") == "WEISSGEHEIM");
        Assert.DoesNotContain(areas.Spots, s => s.Text.Replace(" ", "") == "VERDECKT");     // elsewhere: not drawn, not found
        Assert.Contains(everything.Spots, s => s.Text.Replace(" ", "") == "VERDECKT");
        Assert.True(some.Candidates < all.Candidates, $"{some.Candidates} / {all.Candidates}");
        Assert.True(areas.Complete);
        // areas without anything doubtful: nothing drawn at all
        var none = new CountingProbe();
        _ = HiddenText.Scan(doc, doc.Pages[0], [new PdfRectangle(500, 50, 520, 60)], probe: none);
        Assert.Equal(0, none.Calls);
    }

    [Fact]
    public void Find_EveryKind_WithItsText()
    {
        using var doc = PdfDocument.Open(Build());
        var spots = HiddenText.Find(doc, doc.Pages[0]);
        HiddenTextSpot Spot(string word) => Assert.Single(spots, s => s.Text.Replace(" ", "") == word);
        Assert.Equal(HiddenTextKind.Suspicious, Spot("WEISSGEHEIM").Kind);
        Assert.Equal(HiddenTextKind.Invisible, Spot("OCRTEXT").Kind);
        Assert.Equal(HiddenTextKind.Outside, Spot("DRAUSSEN").Kind);
        Assert.True(Spot("DRAUSSEN").OutsidePage);
        Assert.False(Spot("WEISSGEHEIM").OutsidePage);
        Assert.Equal(HiddenTextKind.Tiny, Spot("WINZIG").Kind);
        Assert.Equal(HiddenTextKind.Suspicious, Spot("VERDECKT").Kind);
        Assert.Equal(5, spots.Count);   // the visible text is not among them
        // even tiny text gets an area that can be marked
        Assert.All(HiddenText.MarkAreas(Spot("WINZIG")), a => Assert.True(a.Width >= HiddenText.MinMarkSize && a.Height >= HiddenText.MinMarkSize));
    }

    // Invisible text (mode 3) of 60 lines with 56 characters each: 3 400 characters with the line breaks
    internal static string LongInvisible()
    {
        var content = new StringBuilder("BT 3 Tr /F1 8 Tf 10 TL 40 800 Td ");
        for (var i = 0; i < 60; i++) content.Append($"(Z{i:00}{new string((char)('A' + i % 26), 53)}) Tj T* ");
        return content.Append("ET").ToString();
    }

    [Fact]
    public void Find_KeepsTheWholeText_OfALongSpot()
    {
        // the window of the panel shows the whole text of a spot, without searching again: the spot keeps it all
        using var doc = PdfDocument.Open(Build(LongInvisible()));
        var spot = Assert.Single(HiddenText.Find(doc, doc.Pages[0]));
        var lines = spot.Text.Split('\n');
        Assert.Equal(60, lines.Length);
        Assert.Equal(spot.Lines.Count, lines.Length);
        Assert.True(spot.Text.Length > 3000);
        Assert.Equal("Z59" + new string((char)('A' + 59 % 26), 53), lines[^1].Replace(" ", ""));
    }

    [Fact]
    public void Find_CutsTheText_AtThePageBudget()
    {
        // a crafted page cannot fill the memory: beyond the budget the text ends in „…“, the lines and characters are all still there
        using var doc = PdfDocument.Open(Build(LongInvisible()));
        var spot = Assert.Single(HiddenText.Scan(doc, doc.Pages[0], 500).Spots);
        Assert.EndsWith(" …", spot.Text);
        Assert.Equal(502, spot.Text.Length);
        Assert.Equal(60, spot.Lines.Count);
        Assert.Equal(60 * 56, spot.Glyphs);
    }

    [Fact]
    public void HiddenMark_RemovesOnlyHiddenCharacters_VisibleStays_NoBar()
    {
        var data = Build();
        using var doc = PdfDocument.Open(data);
        var spots = HiddenText.Find(doc, doc.Pages[0]);
        var marked = spots.Where(s => s.Text.Replace(" ", "") is "WEISSGEHEIM" or "OCRTEXT" or "DRAUSSEN" or "WINZIG").ToList();
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([.. marked.Select(s => RedactionElement.FromPageAreas(HiddenText.MarkAreas(s)) with { Hidden = true })]);
        var redacted = Write(doc, items, new PdfSaveOptions { ApplyRedactions = true, CompressStreams = false, ObjectStreams = false });

        foreach (var word in new[] { "WEISSGEHEIM", "OCRTEXT", "DRAUSSEN", "WINZIG" })
            Assert.False(Contains(redacted, word), $"{word}: noch in der Datei");
        Assert.True(Contains(redacted, "VERDECKT"));   // not marked: stays
        var text = Text(redacted);
        Assert.Contains("Sichtbar", text);
        Assert.Contains("SICHTBARDA", text);   // visible text under the mark of the invisible text stays
        // no bar: the only filled rectangle is the box that was there before
        using var after = PdfDocument.Open(redacted);
        var list = PageInterpreter.Build(after, after.Pages[0]);
        Assert.Single(list.Items.ToArray(), i => !i.Glyph);
        Assert.Equal("VERDECKT", Assert.Single(HiddenText.Find(after, after.Pages[0])).Text.Replace(" ", ""));
    }

    [Fact]
    public void HiddenMark_SavedAndReadBack_WithoutTheHiddenText()
    {
        var data = Build();
        using var doc = PdfDocument.Open(data);
        var spot = Assert.Single(HiddenText.Find(doc, doc.Pages[0]), s => s.Kind == HiddenTextKind.Invisible);
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([RedactionElement.FromPageAreas(HiddenText.MarkAreas(spot)) with { Hidden = true }]);
        var saved = Write(doc, items, new PdfSaveOptions());
        using var again = PdfDocument.Open(saved);
        var mark = Assert.Single(PageItem.From(again)[0].Elements.OfType<RedactionElement>());
        Assert.True(mark.Hidden);
        Assert.Equal(spot.Bounds.Left, mark.Rect.Left, 0.01);

        // applying the read mark removes the invisible text and leaves the visible text of the same line
        var applied = Write(again, PageItem.From(again), new PdfSaveOptions { ApplyRedactions = true });
        Assert.False(Contains(applied, "OCRTEXT"));
        Assert.Contains("SICHTBARDA", Text(applied));
    }

    [Fact]
    public void HiddenAndBlackMarks_OnOnePage_BothApply()
    {
        var data = Build();
        using var doc = PdfDocument.Open(data);
        var spot = Assert.Single(HiddenText.Find(doc, doc.Pages[0]), s => s.Kind == HiddenTextKind.Invisible);
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements(
        [
            RedactionElement.FromPageAreas(HiddenText.MarkAreas(spot)) with { Hidden = true },
            new RedactionElement { Rect = new PdfRectangle(60, 750, 300, 800) },   // "Sichtbar"
        ]);
        var redacted = Write(doc, items, new PdfSaveOptions { ApplyRedactions = true });
        Assert.False(Contains(redacted, "OCRTEXT"));
        var text = Text(redacted);
        Assert.DoesNotContain("Sichtbar", text);
        Assert.Contains("SICHTBARDA", text);
    }

    /// <summary>Marks the hidden spots whose text (without spaces) is one of <paramref name="words"/> and applies them; uncompressed.</summary>
    internal static byte[] RedactHidden(string content, params string[] words) => RedactHidden(Build(content), words);

    /// <summary>As <see cref="RedactHidden(string, string[])"/> for a whole file.</summary>
    internal static byte[] RedactHidden(byte[] data, params string[] words)
    {
        using var doc = PdfDocument.Open(data);
        var spots = HiddenText.Find(doc, doc.Pages[0]).Where(s => words.Contains(s.Text.Replace(" ", ""))).ToList();
        Assert.Equal(words.Length, spots.Count);
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([.. spots.Select(s => RedactionElement.FromPageAreas(HiddenText.MarkAreas(s)) with { Hidden = true })]);
        return Write(doc, items, new PdfSaveOptions { ApplyRedactions = true, CompressStreams = false, ObjectStreams = false });
    }

    /// <summary>
    /// Hidden text within a visible line: the gap left behind is coarsened to the grid as under a bar — exact to four decimals, its
    /// width gave the length of the removed word (docs/FEHLERGEDAECHTNIS.md, No. 130).
    /// </summary>
    [Fact]
    public void HiddenMark_GapCoarsened_WidthRevealsNothing()
    {
        var redacted = RedactHidden("BT /F1 10 Tf 72 600 Td (Herr ) Tj 3 Tr (GEHEIMNAME) Tj 0 Tr ( sagt) Tj ET", "GEHEIMNAME");
        Assert.False(Contains(redacted, "GEHEIMNAME"));
        var content = Encoding.Latin1.GetString(redacted);
        var shift = System.Text.RegularExpressions.Regex.Match(content, @"\[(-?[0-9.]+)\] TJ");
        Assert.True(shift.Success, "keine Lücke im Inhalt");
        var points = Math.Abs(double.Parse(shift.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)) / 1000 * 10;
        Assert.Equal(0, Math.IEEERemainder(points, Redactor.PositionGrid), 6);
        Assert.Contains("Herr", Text(redacted));
        Assert.Contains("sagt", Text(redacted));
    }

    // ------------------------------------------------------------ Visibility measured by drawing

    /// <summary>Opacity 0 (/Null), a separation colour space /None (/None), a grey image (/Im), an optional content group switched off (/Off).</summary>
    private static byte[] BuildWith(string content)
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content)));
        var ocg = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Aus"u8.ToArray()))));
        var catalog = (PdfDictionary)pdf.Get(1);
        catalog.Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(ocg)])),
            ("D", TestPdf.Dict(("OFF", new PdfArray([TestPdf.Ref(ocg)]))))));
        var image = pdf.Add(TestPdf.FlateStream(Enumerable.Repeat((byte)0x80, 4).ToArray(), TestPdf.Dict(("Type", TestPdf.N("XObject")),
            ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(2)), ("BitsPerComponent", PdfInteger.Of(8)),
            ("ColorSpace", TestPdf.N("DeviceGray")))));
        var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 600, 800)),
            ("OC", TestPdf.Ref(ocg))), "BT /F1 12 Tf 72 300 Td (FORMAUS) Tj ET"u8.ToArray()));
        var none = new PdfArray([TestPdf.N("Separation"), TestPdf.N("None"), TestPdf.N("DeviceGray"), TestPdf.Dict(("FunctionType", PdfInteger.Of(2)),
            ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(1)), ("C1", TestPdf.Box(0)), ("N", PdfInteger.Of(1)))]);
        var resources = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!;
        resources.Set(PdfName.Get("ExtGState"), TestPdf.Dict(("Null", TestPdf.Dict(("ca", new PdfReal(0)))), ("Half", TestPdf.Dict(("ca", new PdfReal(0.5))))));
        resources.Set(PdfName.Get("ColorSpace"), TestPdf.Dict(("None", none)));
        resources.Set(PdfName.Get("XObject"), TestPdf.Dict(("Im", TestPdf.Ref(image)), ("Fm", TestPdf.Ref(form))));
        return pdf.Build();
    }

    private static SkiaSharp.SKBitmap Render(byte[] data)
    {
        using var doc = PdfDocument.Open(data);
        return PdfKnead.Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 2, new PdfKnead.Rendering.ImageCache());
    }

    /// <summary>Largest change of a colour channel between two renderings of the same page.</summary>
    private static int Change(byte[] before, byte[] after)
    {
        using var a = Render(before);
        using var b = Render(after);
        var most = 0;
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
        {
            var p = a.GetPixel(x, y);
            var q = b.GetPixel(x, y);
            most = Math.Max(most, Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue))));
        }
        return most;
    }

    /// <summary>
    /// Painted but not to be seen — found by drawing, removed, and the page looks exactly as before. Each of these stayed in the file
    /// with the guess from colours and rectangles (docs/FEHLERGEDAECHTNIS.md, No. 131).
    /// </summary>
    [Theory]
    [InlineData("q /Null gs BT /F1 12 Tf 72 600 Td (DURCHSICHTIG) Tj ET Q")]                                        // opacity 0
    [InlineData("0 0 1 rg 50 580 300 40 re f BT /F1 12 Tf 72 600 Td (BLAUAUFBLAU) Tj ET 0 g")]                       // colour of the background
    [InlineData("BT /F1 12 Tf 72 600 Td (UNTERBILD) Tj ET q 200 0 0 40 60 590 cm /Im Do Q")]                          // under an image
    [InlineData("1 g 50 580 300 40 re f BT /F1 12 Tf 72 600 Td (WEISSAUFWEISS) Tj ET 0 g")]                          // white on a white area
    [InlineData("q 0 0 m 300 0 l 0 300 l h W n BT /F1 12 Tf 250 250 Td (DREIECK) Tj ET Q")]                          // outside a clip of another shape
    [InlineData("/None cs 1 scn BT /F1 12 Tf 72 600 Td (FARBRAUMNONE) Tj ET 0 g")]                                   // colour space /None paints nothing
    [InlineData("0 g 50 590 300 30 re f 0.02 g BT /F1 12 Tf 72 600 Td (FASTSCHWARZ) Tj ET")]                        // almost the colour of the background
    public void Measured_FoundRemoved_PageUnchanged(string hiddenPart)
    {
        var word = System.Text.RegularExpressions.Regex.Match(hiddenPart, @"\(([A-Z]+)\)").Groups[1].Value;
        var data = BuildWith("BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET " + hiddenPart);
        using (var doc = PdfDocument.Open(data))
            Assert.Equal(HiddenTextKind.Suspicious, Assert.Single(HiddenText.Find(doc, doc.Pages[0]), s => s.Text.Replace(" ", "") == word).Kind);
        var redacted = RedactHidden(data, word);
        Assert.False(Contains(redacted, word), $"{word}: noch in der Datei");
        Assert.Contains("Sichtbar", Text(redacted));
        Assert.True(Change(data, redacted) < 8, "die Seite sieht anders aus");
    }

    /// <summary>Visible text is never hidden: half transparent, white over black text (it cuts into it), a visible character exactly over a hidden one.</summary>
    [Fact]
    public void Measured_VisibleStays()
    {
        var data = BuildWith(
            "BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET " +
            "q /Half gs BT /F1 12 Tf 72 700 Td (HALBDURCHSICHTIG) Tj ET Q " +
            "BT /F1 30 Tf 72 600 Td (SCHWARZ) Tj ET 1 g BT /F1 30 Tf 74 600 Td (WEISS) Tj ET 0 g " +
            "BT /F1 30 Tf 100 400 Td (QQQ) Tj ET 1 g 90 390 80 40 re f 0 g BT /F1 30 Tf 100 400 Td (QQQ) Tj ET");
        using var doc = PdfDocument.Open(data);
        var spots = HiddenText.Find(doc, doc.Pages[0]);
        Assert.DoesNotContain(spots, s => s.Text.Replace(" ", "") is "HALBDURCHSICHTIG" or "WEISS" or "SCHWARZ" or "Sichtbar");
        // of the two QQQ, only the one under the white box
        Assert.True(spots.Count == 1, string.Join(" | ", spots.Select(s => $"{s.Kind} {s.Text} {s.Bounds}")));
        Assert.Equal("QQQ", Assert.Single(spots).Text.Replace(" ", ""));
        var redacted = RedactHiddenAll(data);
        Assert.True(Change(data, redacted) < 8, "die Seite sieht anders aus");
        Assert.Contains("QQQ", Text(redacted));
    }

    /// <summary>A form on a hidden layer by its own /OC: its text is hidden too (it was never found: only marked content counted).</summary>
    [Fact]
    public void FormOnHiddenLayer_Found()
    {
        var data = BuildWith("BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET /Fm Do");
        using var doc = PdfDocument.Open(data);
        Assert.Equal(HiddenTextKind.HiddenLayer, Assert.Single(HiddenText.Find(doc, doc.Pages[0])).Kind);
        Assert.False(Contains(RedactHiddenAll(data), "FORMAUS"));
    }

    /// <summary>Marks and applies every hidden spot of the first page; uncompressed.</summary>
    private static byte[] RedactHiddenAll(byte[] data)
    {
        using var doc = PdfDocument.Open(data);
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([.. HiddenText.Find(doc, doc.Pages[0]).Select(s => RedactionElement.FromPageAreas(HiddenText.MarkAreas(s)) with { Hidden = true })]);
        return Write(doc, items, new PdfSaveOptions { ApplyRedactions = true, CompressStreams = false, ObjectStreams = false });
    }

    /// <summary>
    /// Text in the cell of a tiling pattern (offene Punkte 2.3, docs/FEHLERGEDAECHTNIS.md, No. 233): red text on the cell's red background,
    /// black text beside it, a line in mode 3, and white text where the pattern paints nothing. Before, „Verborgenes schwärzen“ never
    /// looked into pattern cells, and all of it stayed. Now the hidden ones are found — the painted ones by drawing: the page is built
    /// without them and compared — and removed in a copy of the pattern; the black text and what the page shows stay.
    /// </summary>
    [Fact]
    public void PatternCellText_HiddenFoundAndRemoved()
    {
        var pdf = TestPdf.Simple(1);
        var font = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.GetRaw(PdfName.Get("F1"))!;
        var cell = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Pattern")), ("PatternType", PdfInteger.Of(1)), ("PaintType", PdfInteger.Of(1)),
            ("TilingType", PdfInteger.Of(1)), ("BBox", TestPdf.Box(0, 0, 600, 840)), ("XStep", PdfInteger.Of(600)), ("YStep", PdfInteger.Of(840)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", font)))))),
            Encoding.ASCII.GetBytes("1 0 0 rg 0 0 600 840 re f BT /F1 14 Tf 100 500 Td (ROTAUFROT) Tj ET " +
                                    "0 g BT /F1 14 Tf 100 520 Td (Schwarz sichtbar) Tj ET BT 3 Tr /F1 14 Tf 100 540 Td (UNSICHTBARZELLE) Tj ET " +
                                    "1 1 1 rg BT 0 Tr /F1 14 Tf 400 100 Td (AUSSERHALB) Tj ET"));
        var pattern = pdf.Add(cell);
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("Pattern"), TestPdf.Dict(("P1", TestPdf.Ref(pattern))));
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 20 Tf 72 760 Td (Seite) Tj ET q /Pattern cs /P1 scn 90 490 230 70 re f Q"u8.ToArray()));
        var data = pdf.Build();

        using (var doc = PdfDocument.Open(data))
        {
            var spots = HiddenText.Find(doc, doc.Pages[0]);
            HiddenTextSpot Spot(string word) => Assert.Single(spots, s => s.Text.Replace(" ", "") == word);
            Assert.Equal(HiddenTextKind.Suspicious, Spot("ROTAUFROT").Kind);
            Assert.Equal(HiddenTextKind.Invisible, Spot("UNSICHTBARZELLE").Kind);
            Assert.Equal(HiddenTextKind.Suspicious, Spot("AUSSERHALB").Kind);
            Assert.DoesNotContain(spots, s => s.Text.Contains("Schwarz", StringComparison.Ordinal) || s.Text.Contains("Seite", StringComparison.Ordinal));
        }

        var after = RedactHiddenAll(data);
        Assert.False(Contains(after, "ROTAUFROT"));
        Assert.False(Contains(after, "UNSICHTBARZELLE"));
        Assert.False(Contains(after, "AUSSERHALB"));
        Assert.True(Contains(after, "Schwarz sichtbar"));
        Assert.InRange(Change(data, after), 0, 23);
    }

    /// <summary>
    /// A layer that is off on the screen but printed (a watermark for paper) is not hidden: removing it changed the printout
    /// (docs/FEHLERGEDAECHTNIS.md, No. 133). A layer that is off for both stays hidden.
    /// </summary>
    [Fact]
    public void PrintOnlyLayer_NotHidden()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(
            "BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET /OC /Druck BDC BT /F1 12 Tf 72 600 Td (NURDRUCK) Tj ET EMC " +
            "/OC /Aus BDC BT /F1 12 Tf 72 500 Td (GANZAUS) Tj ET EMC /Fd Do")));
        var print = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Druck"u8.ToArray())),
            ("Usage", TestPdf.Dict(("Print", TestPdf.Dict(("PrintState", TestPdf.N("ON"))))))));
        var off = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Aus"u8.ToArray()))));
        var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 600, 800)),
            ("OC", TestPdf.Ref(print))), "BT /F1 12 Tf 72 300 Td (FORMDRUCK) Tj ET"u8.ToArray()));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(print), TestPdf.Ref(off)])),
            ("D", TestPdf.Dict(("OFF", new PdfArray([TestPdf.Ref(print), TestPdf.Ref(off)])),
                ("AS", new PdfArray([TestPdf.Dict(("Event", TestPdf.N("Print")), ("Category", new PdfArray([TestPdf.N("Print")])),
                    ("OCGs", new PdfArray([TestPdf.Ref(print), TestPdf.Ref(off)])))]))))));
        var resources = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!;
        resources.Set(PdfName.Get("Properties"), TestPdf.Dict(("Druck", TestPdf.Ref(print)), ("Aus", TestPdf.Ref(off))));
        resources.Set(PdfName.Get("XObject"), TestPdf.Dict(("Fd", TestPdf.Ref(form))));
        var data = pdf.Build();
        using (var doc = PdfDocument.Open(data))
            Assert.Equal("GANZAUS", Assert.Single(HiddenText.Find(doc, doc.Pages[0])).Text.Replace(" ", ""));
        var redacted = RedactHiddenAll(data);
        Assert.False(Contains(redacted, "GANZAUS"));
        Assert.True(Contains(redacted, "NURDRUCK"));
        Assert.True(Contains(redacted, "FORMDRUCK"));
    }

    /// <summary>
    /// Removing hidden text takes only the alternate texts of the marked content it came from — in the content and in the structure
    /// tree; those of visible content stay (all alternate texts of the page went, docs/FEHLERGEDAECHTNIS.md, No. 134).
    /// </summary>
    [Fact]
    public void HiddenMark_OnlyItsAlternateTextsGo()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(
            "/Figure << /MCID 0 /Alt (BLEIBTALT) >> BDC BT /F1 20 Tf 72 700 Td (Sichtbar) Tj ET EMC " +
            "/Span << /MCID 1 /ActualText (GEHEIMACTUAL) >> BDC BT 3 Tr /F1 12 Tf 72 600 Td (OCRTEXT) Tj ET EMC")));
        static PdfString S(string text) => new(Encoding.ASCII.GetBytes(text));
        var root = pdf.Add(new PdfDictionary());
        var figure = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Figure")), ("P", TestPdf.Ref(root)),
            ("Pg", TestPdf.Ref(5)), ("K", PdfInteger.Of(0)), ("Alt", S("STRUKTURBLEIBT"))));
        var span = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Span")), ("P", TestPdf.Ref(root)),
            ("Pg", TestPdf.Ref(5)), ("K", PdfInteger.Of(1)), ("Alt", S("STRUKTURGEHEIM"))));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", new PdfArray([TestPdf.Ref(figure), TestPdf.Ref(span)]))));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("StructTreeRoot"), TestPdf.Ref(root));
        var redacted = RedactHiddenAll(pdf.Build());
        Assert.False(Contains(redacted, "OCRTEXT"));
        Assert.False(Contains(redacted, "GEHEIMACTUAL"));
        Assert.False(Contains(redacted, "STRUKTURGEHEIM"));
        Assert.True(Contains(redacted, "BLEIBTALT"));
        Assert.True(Contains(redacted, "STRUKTURBLEIBT"));
    }

    /// <summary>
    /// 30 000 characters drawn on top of each other: the search stays within its work limit — every query for what overlaps a character
    /// collected all 30 000 entries, the limit counted only those looked at, and the time grew with the square of the characters (a damaged
    /// file with 159 000 took 35 s; docs/FEHLERGEDAECHTNIS.md, No. 187).
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void ManyCharactersOnTopOfEachOther_SearchStaysFast()
    {
        var content = "BT /F1 12 Tf 72 700 Td [" + string.Concat(Enumerable.Repeat("(x) 500 ", 30_000)) + "] TJ ET";
        using var doc = PdfDocument.Open(Build(content));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _ = HiddenText.Scan(doc, doc.Pages[0]);
        Assert.True(watch.Elapsed.TotalSeconds < 5 * TestKind.Factor, $"{watch.Elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>
    /// 100 000 characters side by side in one string on a page wide enough for all of them: the search stays fast — the characters left
    /// to measure were handed on as one area each, and each character was tested against all of them (112 000 characters in a fuzz sample
    /// took 36 s; docs/FEHLERGEDAECHTNIS.md, No. 914).
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void ManyCharactersSideBySide_SearchStaysFast()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("BT /F1 18 Tf 20 250 Td (" + new string('x', 100_000) + ") Tj ET")));
        ((PdfDictionary)pdf.Get(2)).Set(PdfName.MediaBox, TestPdf.Box(0, 0, 1_000_000, 300));
        using var doc = PdfDocument.Open(pdf.Build());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var scan = HiddenText.Scan(doc, doc.Pages[0]);
        Assert.True(watch.Elapsed.TotalSeconds < 5 * TestKind.Factor, $"{watch.Elapsed.TotalSeconds:0.0} s");
        Assert.Empty(scan.Spots);
    }

    /// <summary>
    /// A slanted character at the edge of the page of which only a corner too small to be seen lies on the page: it is measured and counts
    /// as hidden — counted as visible without measuring, it was judged hidden only once something was redacted next to it, and remained
    /// after „Verborgenes schwärzen“ (docs/FEHLERGEDAECHTNIS.md, No. 157).
    /// </summary>
    [TestPdfsFact]
    public void SliverAtThePageEdge_IsMeasured()
    {
        using var doc = PdfDocument.Open(Path.Combine(Root(), "TestPDFs", "pdfjs", "bug946506.pdf"));
        var scan = HiddenText.Scan(doc, doc.Pages[0]);
        Assert.Contains(scan.Spots, s => s.Text.Contains('a') && s.Bounds.Bottom < 0 && s.Bounds.Top > 0 && s.Bounds.Left > 350 && s.Bounds.Right < 400);
    }

    /// <summary>
    /// A character drawn just beside the page whose cell reaches onto it (an „a“ below the bottom edge, its ascender box above it) is
    /// hidden; filled or stroked a little further up it is seen — it counted as visible because of its cell (docs/FEHLERGEDAECHTNIS.md, No. 158).
    /// </summary>
    [Fact]
    public void DrawnBesideThePage_CellOnIt_IsHidden()
    {
        bool Found(string content)
        {
            using var doc = PdfDocument.Open(Build("BT /F1 20 Tf 72 700 Td (Sichtbar) Tj ET " + content));
            return HiddenText.Scan(doc, doc.Pages[0]).Spots.Any(s => s.Text.Contains('a'));
        }
        Assert.True(Found("BT /F1 20 Tf 300 -11 Td (a) Tj ET"));
        Assert.False(Found("BT /F1 20 Tf 300 -9 Td (a) Tj ET"));
        Assert.False(Found("BT /F1 20 Tf 1 Tr 3 w 300 -11.5 Td (a) Tj ET"));   // the stroke reaches onto the page
    }

    /// <summary>
    /// A further pass of „Verborgenes schwärzen“ that cannot measure completely: nothing is saved — the passes stopped and left hidden
    /// text in the marked areas without telling anyone (docs/FEHLERGEDAECHTNIS.md, No. 166).
    /// </summary>
    [Fact]
    public void FurtherPassIncomplete_NothingSaved()
    {
        using var doc = PdfDocument.Open(Build("1 1 1 rg BT /F1 20 Tf 72 700 Td (Erstens) Tj ET BT /F1 20 Tf 72 500 Td (Zweitens) Tj ET 0 g"));
        var page = doc.Pages[0];
        var areas = HiddenText.Scan(doc, page).Spots.SelectMany(HiddenText.MarkAreas).ToList();
        Assert.Equal(2, areas.Count);
        // first measurement: the second word counts as visible (as if something over it had hidden it); second: out of time
        var probe = new SteppingProbe();
        Assert.Throws<PdfKnead.Core.Pdf.PdfException>(() => Redactor.RemoveHiddenIn(doc, page, areas, probe: probe));
        Assert.Equal(2, probe.Calls);
    }

    private sealed class SteppingProbe : PdfKnead.Core.Pdf.Graphics.IVisibilityProbe
    {
        public int Calls;

        public PdfKnead.Core.Pdf.Graphics.VisibilityMeasure Measure(PdfKnead.Core.Pdf.Graphics.DisplayList list, PdfDocument doc, PdfPage page,
            IReadOnlyList<PdfKnead.Core.Pdf.Graphics.VisibilityCandidate> candidates, CancellationToken cancel)
        {
            var real = PdfKnead.Rendering.VisibilityProbe.Instance.Measure(list, doc, page, candidates, cancel);
            if (++Calls == 1)
                return real with { Hidden = [.. real.Hidden.Select((h, k) => h && candidates[k].Bounds.Top > 600)] };
            return real with { Complete = false };
        }
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }

    /// <summary>
    /// No silent limits (docs/FEHLERGEDAECHTNIS.md, No. 136): more spots than <see cref="HiddenText.MaxSpots"/> make the search incomplete,
    /// and a mark of hidden content that finds nothing left to remove is reported by the writer.
    /// </summary>
    [Fact]
    public void Limits_AndIdleMarks_AreReported()
    {
        var many = new StringBuilder("BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET ");
        for (var i = 0; i <= HiddenText.MaxSpots; i++) many.Append("BT /F1 10 Tf ").Append(-1000 - i * 50).Append(" -500 Td (X) Tj ET ");
        using (var doc = PdfDocument.Open(Build(many.ToString())))
        {
            var scan = HiddenText.Scan(doc, doc.Pages[0]);
            Assert.False(scan.Complete);
            Assert.Equal(HiddenText.MaxSpots, scan.Spots.Count);
        }
        using (var doc = PdfDocument.Open(Build()))
        {
            Assert.True(HiddenText.Scan(doc, doc.Pages[0]).Complete);
            var items = PageItem.From(doc);
            items[0] = items[0].WithElements([RedactionElement.FromPageAreas([new PdfRectangle(400, 100, 500, 150)]) with { Hidden = true }]);   // nothing hidden there
            using var output = new MemoryStream();
            var result = PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true });
            Assert.Equal([0], result.IdleHiddenMarks);
        }
    }

    /// <summary>
    /// Many large characters lying exactly on each other (crafted or broken files: a fuzzed file had hundreds of glyphs a page in size):
    /// the measurement stays fast — drawing each of them on its own took 21 s here and 30 s for one page of the fuzzed file — and what it
    /// finds can be removed without changing the page (docs/FEHLERGEDAECHTNIS.md, No. 139).
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void ManyOverlapping_StaysFast()
    {
        var content = new StringBuilder("BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET ");
        for (var i = 0; i < 300; i++) content.Append("BT /F1 400 Tf 50 200 Td (W) Tj ET ");
        var data = Build(content.ToString());
        using (var doc = PdfDocument.Open(data))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var scan = HiddenText.Scan(doc, doc.Pages[0]);
            Assert.True(watch.ElapsedMilliseconds < 4000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
            Assert.True(scan.Complete);
        }
        Assert.True(Change(data, RedactHiddenAll(data)) < 8, "die Seite sieht anders aus");
    }

    /// <summary>
    /// The text layer of a scan as OCR programs often add it: in a form XObject of its own over the page image, its words marked and
    /// described in the structure tree (an MCR into the form, /ActualText). „Dokument bereinigen“ must find it and remove the words
    /// everywhere: from the form, and from the structure element that repeats them.
    /// </summary>
    [Fact]
    public void OcrLayerInAForm_FoundAndRemoved_WithItsAlternateText()
    {
        var pdf = TestPdf.Simple(1);
        var layer = pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes(
                "/P << /MCID 0 >> BDC BT 3 Tr /F1 10 Tf 72 600 Td (OCRFORMWORT) Tj ET EMC"),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 595, 842)),
                ("StructParents", PdfInteger.Of(0)), ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))))))));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET q /OCR Do Q")));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))),
            ("XObject", TestPdf.Dict(("OCR", TestPdf.Ref(layer))))));
        var root = pdf.Add(new PdfDictionary());
        var paragraph = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("P")), ("P", TestPdf.Ref(root)), ("Pg", TestPdf.Ref(5)),
            ("ActualText", new PdfString("OCRFORMWORT"u8.ToArray())),
            ("K", TestPdf.Dict(("Type", TestPdf.N("MCR")), ("Stm", TestPdf.Ref(layer)), ("MCID", PdfInteger.Of(0))))));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", TestPdf.Ref(paragraph))));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("StructTreeRoot"), TestPdf.Ref(root));
        var data = pdf.Build();
        // „Verborgenes schwärzen“ takes the same way through the writer
        Assert.False(Contains(RedactHiddenAll(data), "OCRFORMWORT"));
        using var doc = PdfDocument.Open(data);

        Assert.Equal(1, Assert.Single(DocumentCleanup.Inspect(doc, PageItem.From(doc)), f => f.Item == CleanupItems.InvisibleText).Count);
        var saved = Write(doc, PageItem.From(doc), new PdfSaveOptions { Cleanup = CleanupItems.InvisibleText });
        Assert.False(Contains(saved, "OCRFORMWORT"));
        Assert.Contains("Sichtbar", Text(saved));
    }

    /// <summary>A parent structure element describes its children: its /ActualText goes with removed hidden text below it (docs/FEHLERGEDAECHTNIS.md, No. 142).</summary>
    [Fact]
    public void HiddenMark_ParentAlternateTextGoesToo()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(
            "BT /F1 20 Tf 72 700 Td (Sichtbar) Tj ET /Span << /MCID 0 >> BDC BT 3 Tr /F1 12 Tf 72 600 Td (VERSTECKTWORT) Tj ET EMC")));
        var root = pdf.Add(new PdfDictionary());
        var paragraph = pdf.Add(new PdfDictionary());
        var span = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Span")), ("P", TestPdf.Ref(paragraph)), ("K", PdfInteger.Of(0))));
        pdf.Replace(paragraph, TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("P")), ("P", TestPdf.Ref(root)), ("Pg", TestPdf.Ref(5)),
            ("ActualText", new PdfString("VERSTECKTWORT"u8.ToArray())), ("K", new PdfArray([TestPdf.Ref(span)]))));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", TestPdf.Ref(paragraph))));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("StructTreeRoot"), TestPdf.Ref(root));
        var data = pdf.Build();
        Assert.False(Contains(RedactHiddenAll(data), "VERSTECKTWORT"));
        // „Dokument bereinigen“ → unsichtbaren Text entfernen: ebenso
        using var doc = PdfDocument.Open(data);
        Assert.False(Contains(Write(doc, PageItem.From(doc), new PdfSaveOptions { Cleanup = CleanupItems.InvisibleText }), "VERSTECKTWORT"));
    }

    /// <summary>
    /// Text as a clip (modes 4–6) is visible through what is painted inside it, even with a white fill: it is never hidden — removing it
    /// took the clip and everything shown through it (docs/FEHLERGEDAECHTNIS.md, No. 143). And a word covered by rows of later small
    /// characters is measured and found (it was never looked at).
    /// </summary>
    [Fact]
    public void ClipText_Visible_CoveredByCharacters_Found()
    {
        var clip = BuildWith("BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET q BT 4 Tr 1 g /F1 60 Tf 72 600 Td (CLIPWORT) Tj ET 0 g 0 0 612 792 re f Q");
        using (var doc = PdfDocument.Open(clip))
            Assert.DoesNotContain(HiddenText.Find(doc, doc.Pages[0]), s => s.Text.Contains("CLIP"));
        // rows of filled squares (ZapfDingbats „n“) drawn after the word, overlapping each other
        var squares = new StringBuilder("BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET BT /F1 14 Tf 100 600 Td (UNTERQUADRATEN) Tj ET");
        for (var row = 0; row < 5; row++)
            squares.Append($" BT /Z 7 Tf -1.5 Tc 96 {595 + row * 4} Td ({new string('n', 60)}) Tj ET");
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(squares.ToString())));
        var dingbats = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("ZapfDingbats"))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("Z"), TestPdf.Ref(dingbats));
        var data = pdf.Build();
        using (var doc = PdfDocument.Open(data))
            Assert.Contains(HiddenText.Find(doc, doc.Pages[0]), s => s.Text.Replace(" ", "").Contains("QUADRAT"));
        Assert.False(Contains(RedactHiddenAll(data), "UNTERQUADRATEN"));
        Assert.True(Change(data, RedactHiddenAll(data)) < 8, "die Seite sieht anders aus");
    }

    /// <summary>
    /// Transparent text over visible text: the joint check reveals the visible characters, not the transparent ones whose rectangle lies
    /// over them — „KONTO 47“ stayed in the file while the page counted as searched completely (docs/FEHLERGEDAECHTNIS.md, No. 144).
    /// </summary>
    [Fact]
    public void TransparentOverVisibleText_FoundCompletely()
    {
        var content = new StringBuilder("BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET ");
        for (var line = 0; line < 8; line++) content.Append($"BT /F1 8 Tf 60 {585 + line * 7} Td ({new string('o', 90)}) Tj ET ");
        content.Append("q /Null gs BT /F1 60 Tf 70 580 Td (KONTO 4711) Tj ET Q");
        var data = BuildWith(content.ToString());
        using (var doc = PdfDocument.Open(data))
        {
            var scan = HiddenText.Scan(doc, doc.Pages[0]);
            Assert.Contains(scan.Spots, s => s.Text.Replace(" ", "").Contains("KONTO4711"));
        }
        var redacted = RedactHiddenAll(data);
        Assert.False(Contains(redacted, "KONTO"));
        Assert.True(Change(data, redacted) < 8, "die Seite sieht anders aus");
    }

    /// <summary>
    /// A white box on a layer that is not printed covers a word on the screen only: on paper the word shows, so it is not hidden — it was
    /// removed and the printout changed (docs/FEHLERGEDAECHTNIS.md, No. 145). A box that is printed too still hides.
    /// </summary>
    [Fact]
    public void CoverOnlyOnScreen_NotHidden()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(
            "BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET BT /F1 14 Tf 72 600 Td (AUFPAPIER) Tj ET /OC /Bild BDC 1 g 60 590 200 30 re f 0 g EMC " +
            "BT /F1 14 Tf 72 500 Td (UEBERALLWEG) Tj ET 1 g 60 490 200 30 re f 0 g")));
        var screen = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Bild"u8.ToArray())),
            ("Usage", TestPdf.Dict(("Print", TestPdf.Dict(("PrintState", TestPdf.N("OFF"))))))));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(screen)])),
            ("D", TestPdf.Dict(("AS", new PdfArray([TestPdf.Dict(("Event", TestPdf.N("Print")), ("Category", new PdfArray([TestPdf.N("Print")])),
                ("OCGs", new PdfArray([TestPdf.Ref(screen)])))]))))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("Properties"), TestPdf.Dict(("Bild", TestPdf.Ref(screen))));
        using var doc = PdfDocument.Open(pdf.Build());
        var spots = HiddenText.Find(doc, doc.Pages[0]).Select(s => s.Text.Replace(" ", "")).ToList();
        Assert.DoesNotContain("AUFPAPIER", spots);
        Assert.Contains("UEBERALLWEG", spots);
    }
}
