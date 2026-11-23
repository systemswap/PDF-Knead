using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Marking redactions, saving them and reopening.</summary>
public sealed class RedactionTests
{
    /// <summary>Builds a page with lines of text (Helvetica 12, from y = 700 downwards, 20 points apart) and a rectangle.</summary>
    internal static byte[] TextPage(params string[] lines) => TitledTextPage("Testdokument", lines);

    /// <summary>Like <see cref="TextPage"/>, with its own title in the document properties.</summary>
    internal static byte[] TitledTextPage(string title, params string[] lines)
    {
        var pdf = TestPdf.Simple(1, title);
        var content = new System.Text.StringBuilder("0.31 0.27 0.9 rg 72 400 200 100 re f BT /F1 12 Tf 72 700 Td 20 TL ");
        foreach (var line in lines) content.Append('(').Append(line.Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj T* ");
        content.Append("ET");
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.Latin1.GetBytes(content.ToString())));
        return pdf.Build();
    }

    /// <summary>Reads the text of a page from the display list, with the characters and their left edge.</summary>
    internal static (string Text, List<(string Char, double X)> Glyphs) Read(PdfDocument doc, PdfPage page)
    {
        var glyphs = PageInterpreter.Build(doc, page).Text.Select(g => (g.Text, (double)Math.Min(g.X0, g.X3))).ToList();
        return (string.Concat(glyphs.Select(g => g.Item1)), glyphs);
    }

    private static PdfPage PageOf(PdfPage page, RedactionResult result) =>
        new(page.Index, result.Page, null, result.Resources, page.MediaBox, page.CropBox, page.Rotate);

    private static PdfRectangle AreaOf(PdfDocument doc, PdfPage page, string word)
    {
        var text = Core.Pdf.Text.PageText.Create(PageInterpreter.Build(doc, page).Text);
        return Assert.Single(ViewModelsShim.Areas(text, Assert.Single(text.Find(word))));
    }

    /// <summary>Line rectangles of a text range (as with "Redact text" in the user interface).</summary>
    private static class ViewModelsShim
    {
        public static List<PdfRectangle> Areas(Core.Pdf.Text.PageText text, Core.Pdf.Text.TextRange range) =>
            [.. text.Quads(range).Select(q => q.Bounds).Select(b => new PdfRectangle(b.Left - 0.5, b.Top - 0.5, b.Right + 0.5, b.Bottom + 0.5))];
    }

    /// <summary>
    /// The rewritten content keeps every number exactly: a matrix with a tiny scale (0.00001234567 × font size 1 000 000 = 12.3 pt) was
    /// rounded to five decimals and the text drawn 19 % smaller (docs/FEHLERGEDAECHTNIS.md, No. 132).
    /// </summary>
    [Fact]
    public void Rewritten_NumbersExact_RestUnchanged()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.Latin1.GetBytes(
            "BT /F1 12 Tf 72 700 Td (Geheim) Tj ET q 0.00001234567 0 0 0.00001234567 72 500 cm BT /F1 1000000 Tf 0 0 Td (Bleibt) Tj ET Q")));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var result = Redactor.Apply(doc, page, [AreaOf(doc, page, "Geheim")]);
        var before = Read(doc, page).Glyphs.Skip("Geheim".Length).Select(g => g.X).ToList();
        var after = Read(doc, PageOf(page, result));
        Assert.Contains("Bleibt", after.Text);
        var x = after.Glyphs.Select(g => g.X).ToList();
        Assert.Equal(before.Count, x.Count);
        for (var i = 0; i < x.Count; i++) Assert.Equal(before[i], x[i], 3);
    }

    /// <summary>
    /// After the bar, fonts whose characters are all gone leave the page, and a Type 3 font keeps only the glyph contents still shown —
    /// a glyph that itself draws text kept it in the file (docs/FEHLERGEDAECHTNIS.md, No. 135).
    /// </summary>
    [Fact]
    public void Fonts_OnlyWhatIsStillShownStays()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.Latin1.GetBytes(
            "BT /F1 12 Tf 72 700 Td (Bleibt) Tj ET BT /T3 40 Tf 72 600 Td (a) Tj ET BT /T3 40 Tf 300 600 Td (b) Tj ET " +
            "BT /G 12 Tf 72 500 Td (Weg) Tj ET")));
        var procA = pdf.Add(TestPdf.FlateStream("1000 0 d0 BT /F1 12 Tf 0 0 Td (GLYPHGEHEIM) Tj ET"u8.ToArray()));
        var procB = pdf.Add(TestPdf.FlateStream("1000 0 d0 0 0 500 500 re f"u8.ToArray()));
        var t3 = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("FontBBox", TestPdf.Box(0, 0, 1000, 1000)),
            ("FontMatrix", TestPdf.Box(0.001, 0, 0, 0.001, 0, 0)), ("CharProcs", TestPdf.Dict(("a", TestPdf.Ref(procA)), ("b", TestPdf.Ref(procB)))),
            ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", new PdfArray([PdfInteger.Of(97), TestPdf.N("a"), TestPdf.N("b")])))),
            ("FirstChar", PdfInteger.Of(97)), ("LastChar", PdfInteger.Of(98)), ("Widths", new PdfArray([PdfInteger.Of(1000), PdfInteger.Of(1000)])),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))))));
        var gone = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("SchriftNurHier"))));
        var fonts = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!;
        fonts.Set(PdfName.Get("T3"), TestPdf.Ref(t3));
        fonts.Set(PdfName.Get("G"), TestPdf.Ref(gone));
        using var doc = PdfDocument.Open(pdf.Build());
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new RedactionElement { Rect = new PdfRectangle(60, 590, 120, 650) }, new RedactionElement { Rect = new PdfRectangle(60, 490, 200, 520) }]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, CompressStreams = false, ObjectStreams = false });
        var data = output.ToArray();
        static bool Has(byte[] d, string word) => PdfKnead.Cli.LeakSearch.Find(d, [word], null, out _).Count > 0;
        Assert.False(Has(data, "GLYPHGEHEIM"));
        Assert.False(Has(data, "SchriftNurHier"));
        using var after = PdfDocument.Open(data);
        var text = Read(after, after.Pages[0]).Text;
        Assert.Contains("Bleibt", text);
        Assert.Contains("b", text);
    }

    /// <summary>
    /// Several gaps in one text operator: each is rounded to the grid, but the text after them stays within half the grid of where it
    /// was — the errors added up before, and the rest of a line of tiny text moved by 2 pt (docs/FEHLERGEDAECHTNIS.md, No. 138).
    /// </summary>
    [Fact]
    public void ManyGaps_RestStaysWithinHalfTheGrid()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 3 Tf 72 700 Td (AbCdEfGhOjKlMn) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var before = Read(doc, page).Glyphs.Where(g => char.IsUpper(g.Char[0])).ToList();
        var areas = "bdfhjln".Select(c => AreaOf(doc, page, c.ToString())).ToList();
        var result = Redactor.Apply(doc, page, areas);
        var after = Read(doc, PageOf(page, result)).Glyphs.Where(g => char.IsUpper(g.Char[0])).ToList();
        Assert.Equal(before.Select(g => g.Char), after.Select(g => g.Char));
        for (var i = 0; i < after.Count; i++) Assert.True(Math.Abs(after[i].X - before[i].X) <= Redactor.PositionGrid / 2 + 0.01, $"{after[i].Char}: {before[i].X} → {after[i].X}");
    }

    /// <summary>
    /// Gaps in one character per text operator: the rounding is carried across the operators of a line, so the text after them stays
    /// within half the grid — rounded per operator, the rest of a line moved by 1.6 pt and the cross-check refused to save
    /// (docs/FEHLERGEDAECHTNIS.md, No. 154).
    /// </summary>
    [Fact]
    public void OneCharacterPerOperator_RestStaysWithinHalfTheGrid()
    {
        var pdf = TestPdf.Simple(1);
        var text = "AbCdEfGhOjKlMn";
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("BT 72 700 Td " + string.Concat(text.Select(c => $"/F1 3 Tf ({c}) Tj ")) + "ET")));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var before = Read(doc, page).Glyphs.Where(g => char.IsUpper(g.Char[0])).ToList();
        var areas = "bdfhjln".Select(c => AreaOf(doc, page, c.ToString())).ToList();
        var result = Redactor.Apply(doc, page, areas);
        var after = Read(doc, PageOf(page, result)).Glyphs.Where(g => char.IsUpper(g.Char[0])).ToList();
        Assert.Equal(before.Select(g => g.Char), after.Select(g => g.Char));
        for (var i = 0; i < after.Count; i++) Assert.True(Math.Abs(after[i].X - before[i].X) <= Redactor.PositionGrid / 2 + 0.01, $"{after[i].Char}: {before[i].X} → {after[i].X}");
    }

    /// <summary>
    /// A Type 3 font with small glyphs (boxes 0.17 of the size high) set large: „Text schwärzen“ removes the word — its cells were at least 0.9
    /// of the size high, the bar over the glyphs covered a fifth of them, and the word stayed in the file under the bar
    /// (docs/FEHLERGEDAECHTNIS.md, No. 159).
    /// </summary>
    [Fact]
    public void Type3SmallGlyphsSetLarge_WordRemoved()
    {
        var pdf = TestPdf.Simple(1);
        var proc = pdf.Add(TestPdf.FlateStream("600 0 0 0 600 170 d1 0 0 600 170 re f"u8.ToArray()));
        var names = new PdfArray([PdfInteger.Of(65)]);
        var procs = new PdfDictionary();
        foreach (var c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            names.Add(TestPdf.N(c.ToString()));
            procs.Set(PdfName.Get(c.ToString()), TestPdf.Ref(proc));
        }
        var t3 = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("FontBBox", TestPdf.Box(0, 0, 600, 170)),
            ("FontMatrix", TestPdf.Box(0.001, 0, 0, 0.001, 0, 0)), ("CharProcs", procs),
            ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", names))),
            ("FirstChar", PdfInteger.Of(65)), ("LastChar", PdfInteger.Of(90)), ("Widths", new PdfArray([.. Enumerable.Repeat<PdfObject>(PdfInteger.Of(600), 26)]))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("T3"), TestPdf.Ref(t3));
        pdf.Replace(4, TestPdf.FlateStream("BT /T3 58 Tf 72 400 Td (BLEIBT GEHEIM BLEIBT) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var text = Core.Pdf.Text.PageText.Create(PageInterpreter.Build(doc, page).Text);
        var areas = text.RedactionAreas(Assert.Single(text.Find("GEHEIM")));
        var result = Redactor.Apply(doc, page, areas);
        var after = Read(doc, PageOf(page, result)).Text;
        Assert.DoesNotContain("GEHEIM", after);
        Assert.Equal(2, after.Split("BLEIBT").Length - 1);
    }

    /// <summary>
    /// A Type 3 font whose height comes from its glyphs' boxes: redacting its tallest glyph leaves the boxes of the other characters where
    /// they were — the copy without the glyph was lower, and the text left moved for selection and later bars (docs/FEHLERGEDAECHTNIS.md, No. 161).
    /// </summary>
    [Fact]
    public void Type3TallestGlyphRemoved_OtherBoxesStay()
    {
        var pdf = TestPdf.Simple(1);
        var tall = pdf.Add(TestPdf.FlateStream("600 0 0 -200 600 900 d1 0 -200 600 1100 re f"u8.ToArray()));
        var low = pdf.Add(TestPdf.FlateStream("600 0 0 0 600 400 d1 0 0 600 400 re f"u8.ToArray()));
        var t3 = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("FontBBox", TestPdf.Box(0, -200, 600, 900)),
            ("FontMatrix", TestPdf.Box(0.001, 0, 0, 0.001, 0, 0)), ("CharProcs", TestPdf.Dict(("H", TestPdf.Ref(tall)), ("x", TestPdf.Ref(low)))),
            ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", new PdfArray([PdfInteger.Of(72), TestPdf.N("H"), PdfInteger.Of(120), TestPdf.N("x")])))),
            ("FirstChar", PdfInteger.Of(72)), ("LastChar", PdfInteger.Of(120)), ("Widths", new PdfArray([.. Enumerable.Repeat<PdfObject>(PdfInteger.Of(600), 49)]))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("T3"), TestPdf.Ref(t3));
        pdf.Replace(4, TestPdf.FlateStream("BT /T3 20 Tf 72 400 Td (Hxxx) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var before = PageInterpreter.Build(doc, page).Text.Where(g => g.Text == "x").ToList();
        var result = Redactor.Apply(doc, page, [AreaOf(doc, page, "H")]);
        var after = PageInterpreter.Build(doc, PageOf(page, result)).Text.Where(g => g.Text == "x").ToList();
        Assert.Equal(3, after.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(before[i].Y0, after[i].Y0, 2);
            Assert.Equal(before[i].Y3, after[i].Y3, 2);
        }
    }

    /// <summary>Type 3 font /T3 with glyph „a“ (a square) and „b“ (draws the text GLYPHGEHEIM itself), on the pages node.</summary>
    private static int AddType3(TestPdf pdf)
    {
        var procA = pdf.Add(TestPdf.FlateStream("1000 0 d0 0 0 800 800 re f"u8.ToArray()));
        var procB = pdf.Add(TestPdf.FlateStream("1000 0 d0 BT /F1 12 Tf 0 0 Td (GLYPHGEHEIM) Tj ET 0 0 300 800 re f"u8.ToArray()));
        var t3 = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("FontBBox", TestPdf.Box(0, 0, 1000, 1000)),
            ("FontMatrix", TestPdf.Box(0.001, 0, 0, 0.001, 0, 0)), ("CharProcs", TestPdf.Dict(("a", TestPdf.Ref(procA)), ("b", TestPdf.Ref(procB)))),
            ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", new PdfArray([PdfInteger.Of(97), TestPdf.N("a"), TestPdf.N("b")])))),
            ("FirstChar", PdfInteger.Of(97)), ("LastChar", PdfInteger.Of(98)), ("Widths", new PdfArray([PdfInteger.Of(1000), PdfInteger.Of(1000)])),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("T3"), TestPdf.Ref(t3));
        return t3;
    }

    internal static byte[] WithBars(PdfDocument doc, params (int Page, PdfRectangle Area)[] bars)
    {
        var items = PageItem.From(doc);
        foreach (var group in bars.GroupBy(b => b.Page))
            items[group.Key] = items[group.Key].WithElements([.. group.Select(b => new RedactionElement { Rect = b.Area })]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, CompressStreams = false, ObjectStreams = false });
        return output.ToArray();
    }

    private static int Dark(PdfDocument doc, int page)
    {
        using var b = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[page]), doc, doc.Pages[page], 1, new Rendering.ImageCache());
        var n = 0;
        for (var y = 0; y < b.Height; y++)
            for (var x = 0; x < b.Width; x++)
                if (b.GetPixel(x, y).Red < 128) n++;
        return n;
    }

    /// <summary>
    /// A Type 3 font shared by two pages: redacting something else on page 1 leaves both its glyphs to page 2 (they were dropped from the
    /// whole file, docs/FEHLERGEDAECHTNIS.md, No. 140); and a glyph removed on a page together with a word in a form without resources of
    /// its own leaves nothing of the glyph's content in the file (the copy of the form kept the whole font).
    /// </summary>
    [Fact]
    public void Type3_SharedFont_AndFormCopy()
    {
        var pdf = TestPdf.Simple(2);
        AddType3(pdf);
        pdf.Replace(4, TestPdf.FlateStream("BT /T3 20 Tf 72 600 Td (b) Tj ET BT /F1 12 Tf 72 400 Td (Wegdamit) Tj ET"u8.ToArray()));
        var second = (PdfDictionary)pdf.Get(7);
        pdf.Replace(((PdfReference)second.GetRaw(PdfName.Contents)!).Id.Number, TestPdf.FlateStream("BT /T3 20 Tf 72 600 Td (ab) Tj ET"u8.ToArray()));
        using (var doc = PdfDocument.Open(pdf.Build()))
        {
            var before = Dark(doc, 1);
            using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(60, 390, 200, 420))));
            Assert.Equal(before, Dark(after, 1));
        }

        var withForm = TestPdf.Simple(1);
        AddType3(withForm);
        var form = withForm.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 600, 800))),
            "BT /F1 12 Tf 72 400 Td (Formwort) Tj ET"u8.ToArray()));
        ((PdfDictionary)withForm.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("XObject"), TestPdf.Dict(("Fm", TestPdf.Ref(form))));
        withForm.Replace(4, TestPdf.FlateStream("BT /T3 20 Tf 72 600 Td (b) Tj ET /Fm Do"u8.ToArray()));
        using var doc2 = PdfDocument.Open(withForm.Build());
        var data = WithBars(doc2, (0, new PdfRectangle(60, 590, 120, 630)), (0, new PdfRectangle(60, 390, 200, 420)));
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(data, ["GLYPHGEHEIM"], null, out _));
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(data, ["Formwort"], null, out _));
    }

    /// <summary>
    /// Invisible text (mode 3, an OCR layer) goes under a bar by its text box, the only thing of it anyone finds (by selecting): a dot
    /// whose box lay mostly under the bar stayed, because its shape — which nobody sees — lay below the bar (docs/FEHLERGEDAECHTNIS.md,
    /// No. 196). Visible text keeps the rule by its shape.
    /// </summary>
    [Fact]
    public void InvisibleCharacter_GoesByItsTextBox()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 12 Tf 72 700 Td (Sichtbar) Tj ET BT 3 Tr /F1 20 Tf 72 600 Td (.) Tj ET BT 0 Tr /F1 20 Tf 200 600 Td (.) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        // the boxes of the dots reach from about 596 to 614 pt, their shapes from 600 to 602: bars over the upper part of both
        using var redacted = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(70, 603, 80, 620)), (0, new PdfRectangle(198, 603, 208, 620))));
        var glyphs = PageInterpreter.Build(redacted, redacted.Pages[0]).Text.Where(t => t.Text == ".").ToList();
        // the invisible dot went, the visible one (its shape beside the bar, seen there) stays
        Assert.DoesNotContain(glyphs, g => !g.Visible);
        Assert.Contains(glyphs, g => g.Visible);
    }

    /// <summary>
    /// A Type 3 font shared by two pages, its glyph with text redacted on page 1 while page 2 shows only its other glyph: the glyph's
    /// content leaves the file — page 2 kept every glyph of the font alive (docs/FEHLERGEDAECHTNIS.md, No. 155). Page 2 still shows its
    /// glyph.
    /// </summary>
    [Fact]
    public void Type3_SharedFont_GlyphShownNowhereLeavesTheFile()
    {
        var pdf = TestPdf.Simple(2);
        AddType3(pdf);
        pdf.Replace(4, TestPdf.FlateStream("BT /T3 20 Tf 72 600 Td (b) Tj ET"u8.ToArray()));
        var second = (PdfDictionary)pdf.Get(7);
        pdf.Replace(((PdfReference)second.GetRaw(PdfName.Contents)!).Id.Number, TestPdf.FlateStream("BT /T3 20 Tf 72 600 Td (a) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var before = Dark(doc, 1);
        var data = WithBars(doc, (0, new PdfRectangle(60, 590, 120, 630)));
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(data, ["GLYPHGEHEIM"], null, out _));
        using var after = PdfDocument.Open(data);
        Assert.Equal(before, Dark(after, 1));
    }

    /// <summary>
    /// All characters of a vertical font removed: what follows in the same text object stays where it was — the bare replacement font was
    /// horizontal, and the remaining shift moved the text sideways (docs/FEHLERGEDAECHTNIS.md, No. 140).
    /// </summary>
    [Fact]
    public void VerticalFont_Removed_FollowingTextStays()
    {
        var pdf = TestPdf.Simple(1);
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Senkrecht")), ("Flags", PdfInteger.Of(4)),
            ("FontBBox", TestPdf.Box(0, -200, 1000, 800)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(800)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80))));
        var cid = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N("Senkrecht")),
            ("CIDSystemInfo", TestPdf.Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())), ("Supplement", PdfInteger.Of(0)))),
            ("FontDescriptor", TestPdf.Ref(descriptor)), ("DW", PdfInteger.Of(1000))));
        var vertical = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("Senkrecht")),
            ("Encoding", TestPdf.N("Identity-V")), ("DescendantFonts", new PdfArray([TestPdf.Ref(cid)]))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("V"), TestPdf.Ref(vertical));
        pdf.Replace(4, TestPdf.FlateStream("BT /V 20 Tf 300 500 Td <0024002500260027> Tj [-5000] TJ /F1 12 Tf (SICHTBAR) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var before = Read(doc, page).Glyphs.Where(g => g.Char == "S").Select(g => g.X).Single();
        var bar = new PdfRectangle(270, 410, 330, 510);
        using var after = PdfDocument.Open(WithBars(doc, (0, bar)));
        var glyphs = Read(after, after.Pages[0]);
        Assert.Contains("SICHTBAR", glyphs.Text);
        Assert.Equal(before, glyphs.Glyphs.Where(g => g.Char == "S").Select(g => g.X).Single(), 0.3);
        // the vertical stand-in has a font program of its own, embedded (No. 691: before a Helvetica without one, which PDF/A and PDF/UA
        // forbid while a „Tf“ names it)
        var standIn = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("V"))!;
        Assert.Equal("Identity-V", standIn.GetName(PdfName.Get("Encoding"))?.Value);
        var descendant = (PdfDictionary)standIn.GetArray(PdfName.Get("DescendantFonts"))!.Get(0)!;
        Assert.Equal("CIDFontType2", descendant.GetName(PdfName.Subtype)?.Value);
        Assert.NotNull(descendant.GetDictionary(PdfName.Get("FontDescriptor"))!.Get(PdfName.Get("FontFile2")) as PdfStream);
        // (the page's own Helvetica, /F1, is not embedded in this test file and is reported — the stand-in is not)
        Assert.DoesNotContain(Core.Pdf.Accessibility.AccessibilityCheck.Check(after, Core.Pdf.Accessibility.AccessibilityProfile.PdfUa1).Findings,
            f => f.Id.StartsWith("7.21.4.1", StringComparison.Ordinal) && f.Text.Contains("LiberationSans", StringComparison.Ordinal));
    }

    /// <summary>
    /// A soft mask set within a text object, whose content moves its own text matrix: the text after it is still found where it is
    /// drawn — the bar left the word in the file, and „Verborgenes schwärzen“ took it for text beside the page (docs/FEHLERGEDAECHTNIS.md,
    /// No. 141).
    /// </summary>
    [Fact]
    public void SoftMaskInsideText_TextStaysWhereItIs()
    {
        var pdf = TestPdf.Simple(1);
        var group = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 612, 792)),
            ("Group", TestPdf.Dict(("S", TestPdf.N("Transparency")), ("CS", TestPdf.N("DeviceGray"))))), "1 g 0 0 612 792 re f BT 5000 5000 Td ET"u8.ToArray()));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("ExtGState"),
            TestPdf.Dict(("SM", TestPdf.Dict(("SMask", TestPdf.Dict(("Type", TestPdf.N("Mask")), ("S", TestPdf.N("Luminosity")), ("G", TestPdf.Ref(group))))))));
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 20 Tf 72 600 Td /SM gs (KONTONUMMER) Tj ET BT /F1 12 Tf 72 700 Td (Sichtbar) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.DoesNotContain(HiddenText.Find(doc, doc.Pages[0]), s => s.Text.Contains("KONTO"));
        var data = WithBars(doc, (0, new PdfRectangle(60, 590, 300, 625)));
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(data, ["KONTONUMMER"], null, out _));
        using var after = PdfDocument.Open(data);
        Assert.Contains("Sichtbar", Read(after, after.Pages[0]).Text);
    }

    /// <summary>
    /// A Type 3 font drawing in text space directly (identity matrix, a TeX font at 0.24 Tf): the boxes of its characters — and so the bars
    /// of „Text schwärzen“ — are as high as the glyphs, not a fifth of a point (docs/FEHLERGEDAECHTNIS.md, No. 149).
    /// </summary>
    [Fact]
    public void Type3IdentityMatrix_BoxesAsHighAsTheGlyphs()
    {
        var pdf = TestPdf.Simple(1);
        var proc = pdf.Add(TestPdf.FlateStream("40 0 d0 2 0 36 40 re f"u8.ToArray()));
        var t3 = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("FontBBox", TestPdf.Box(-2, -11, 44, 43)),
            ("FontMatrix", TestPdf.Box(1, 0, 0, 1, 0, 0)), ("CharProcs", TestPdf.Dict(("a", TestPdf.Ref(proc)))),
            ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", new PdfArray([PdfInteger.Of(97), TestPdf.N("a")])))),
            ("FirstChar", PdfInteger.Of(97)), ("LastChar", PdfInteger.Of(97)), ("Widths", new PdfArray([PdfInteger.Of(40)]))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("T3"), TestPdf.Ref(t3));
        pdf.Replace(4, TestPdf.FlateStream("BT /T3 0.24 Tf 72 600 Td (aaaa) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var area = AreaOf(doc, doc.Pages[0], "aaaa");
        Assert.True(area.Height > 11, $"{area.Height} pt");
        Assert.True(area.Bottom < 600 - 2 && area.Top > 600 + 9, $"{area}");
    }

    /// <summary>
    /// Content that closes more states than it opens and leaves a scale behind: the bars are still drawn where they belong, black and
    /// full size — they were drawn in the leftover state, as thin lines (docs/FEHLERGEDAECHTNIS.md, No. 148).
    /// </summary>
    [Fact]
    public void UnbalancedContent_BarsStillFullSize()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("Q BT /F1 12 Tf 72 600 Td (Geheim) Tj ET 0.1 0 0 0.1 0 0 cm"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var area = AreaOf(doc, doc.Pages[0], "Geheim");
        using var after = PdfDocument.Open(WithBars(doc, (0, area)));
        using var image = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        var x = (int)((area.Left + area.Right) / 2);
        var y = image.Height - (int)((area.Bottom + area.Top) / 2);
        Assert.True(image.GetPixel(x, y).Red < 40, $"{image.GetPixel(x, y)}");
    }

    /// <summary>A box half under a bar stays visible beside it (it was removed entirely, docs/FEHLERGEDAECHTNIS.md, No. 150).</summary>
    [Fact]
    public void PathHalfUnderBar_RestStaysVisible()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("1 0 0 rg 60 590 200 30 re f 0 g BT /F1 12 Tf 72 600 Td (Geheim) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(55, 585, 140, 625))));
        using var image = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        var right = image.GetPixel(220, image.Height - 605);
        Assert.True(right.Red > 200 && right.Green < 60, $"{right}");
        Assert.True(image.GetPixel(100, image.Height - 605).Red < 40);   // the bar
    }

    /// <summary>
    /// Lines with tight leading (their boxes overlap): the bar over a word of one line does not reach into the next — it removed and
    /// covered characters there (docs/FEHLERGEDAECHTNIS.md, No. 151).
    /// </summary>
    [Fact]
    public void TightLines_BarStaysInItsLine()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 24 Tf 72 600 Td 17 TL (GEHEIMWORT) Tj T* (Unten bleibt) Tj T* (Auch das) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var text = Core.Pdf.Text.PageText.Create(PageInterpreter.Build(doc, page).Text);
        var areas = text.RedactionAreas(Assert.Single(text.Find("GEHEIMWORT")));
        var result = Redactor.Apply(doc, page, areas);
        var after = Read(doc, PageOf(page, result)).Text;
        Assert.DoesNotContain("GEHEIM", after);
        Assert.Contains("Unten bleibt", after);
        Assert.True(areas.Min(a => a.Bottom) > 600 - 24 * 0.3, $"{areas[0]}");   // not down into the next line's capitals
    }

    /// <summary>
    /// A slanted text block: the bar over a word of the middle line covers all of the word and none of the lines above and below — one
    /// rectangle around the slanted line, cut back to its middle, left the ends of the word (docs/FEHLERGEDAECHTNIS.md, No. 156).
    /// </summary>
    [Fact]
    public void SlantedLines_BarCoversTheWholeWordOnly()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(("BT /F1 14 Tf 0.866 0.5 -0.5 0.866 150 400 Tm 16 TL (Oben Alpha Bravo Charlie) Tj T* (Mitte GEHEIMWORT Delta) Tj " +
            "T* (Unten Echo Foxtrot Golf) Tj ET").Select(c => (byte)c).ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var text = Core.Pdf.Text.PageText.Create(PageInterpreter.Build(doc, page).Text);
        var areas = text.RedactionAreas(Assert.Single(text.Find("GEHEIMWORT")));
        var result = Redactor.Apply(doc, page, areas);
        var after = Read(doc, PageOf(page, result)).Text;
        foreach (var stays in new[] { "Oben Alpha Bravo Charlie", "Mitte", "Delta", "Unten Echo Foxtrot Golf" }) Assert.Contains(stays, after);
        var middle = after[(after.IndexOf("Mitte", StringComparison.Ordinal) + 5)..after.IndexOf("Delta", StringComparison.Ordinal)];
        Assert.True(middle.All(c => !char.IsLetter(c)), $"„{middle}“");
    }

    /// <summary>
    /// A word of very large letters joined with small ones (one word for the selection): the bar covers the large letters entirely — the
    /// quadrilateral over glyphs of different size is no rectangle, it counted as slanted text and got strips that missed most of the
    /// large letters (docs/FEHLERGEDAECHTNIS.md, No. 160).
    /// </summary>
    [Fact]
    public void LargeAndSmallGlyphsInOneWord_AllCovered()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 150 Tf 50 400 Td (GEH) Tj /F1 10 Tf (klein) Tj ET BT /F1 12 Tf 72 100 Td (Bleibt stehen) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var text = Core.Pdf.Text.PageText.Create(PageInterpreter.Build(doc, page).Text);
        var areas = text.RedactionAreas(Assert.Single(text.Find("GEHklein")));
        var result = Redactor.Apply(doc, page, areas);
        var after = Read(doc, PageOf(page, result)).Text;
        Assert.DoesNotContain("G", after);
        Assert.DoesNotContain("H", after);
        Assert.Contains("Bleibt stehen", after);
    }

    /// <summary>
    /// A large rectangle filled with a pattern (an image in it) and clipped to a thin strip, a bar touching the rectangle but not the strip:
    /// the strip keeps its pattern — the path counted as touched, its pattern was emptied for the page and the strip vanished
    /// (docs/FEHLERGEDAECHTNIS.md, No. 162).
    /// </summary>
    [Fact]
    public void PatternPathTouchedOnlyWhereClippedAway_StaysPainted()
    {
        var pdf = TestPdf.Simple(1);
        var cell = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Pattern")), ("PatternType", PdfInteger.Of(1)), ("PaintType", PdfInteger.Of(1)), ("TilingType", PdfInteger.Of(1)),
            ("BBox", TestPdf.Box(0, 0, 10, 10)), ("XStep", PdfInteger.Of(10)), ("YStep", PdfInteger.Of(10)), ("Resources", new PdfDictionary())),
            [.. "q 10 0 0 10 0 0 cm BI /W 1 /H 1 /CS /G /BPC 8 ID "u8, 0x40, .. " EI Q"u8]);
        var pattern = pdf.Add(cell);
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("Pattern"), TestPdf.Dict(("P1", TestPdf.Ref(pattern))));
        pdf.Replace(4, TestPdf.FlateStream("q 36 392 270 4 re W n /Pattern cs /P1 scn 34 392 272 137 re f* Q BT /F1 12 Tf 100 470 Td (Geheim) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim"))));
        using var image = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        var strip = image.GetPixel(150, image.Height - 394);
        Assert.True(strip.Red < 120, $"Streifen {strip}");
    }

    /// <summary>
    /// A rectangle filled with a pattern whose cell carries text, the bar over the whole rectangle: the fill goes and the pattern with its
    /// text leaves the file, but the colour choice „/P1 scn“ stays in the content — so its name must still be found in the resources
    /// (docs/FEHLERGEDAECHTNIS.md, No. 406). The pattern was dropped from the resources, and readers that check names refused the page
    /// ("cannot find Pattern resource").
    /// </summary>
    [Fact]
    public void PatternFillUnderTheBar_NameStaysValid()
    {
        var pdf = TestPdf.Simple(1);
        var font = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.GetRaw(PdfName.Get("F1"))!;
        var cell = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Pattern")), ("PatternType", PdfInteger.Of(1)), ("PaintType", PdfInteger.Of(1)), ("TilingType", PdfInteger.Of(1)),
            ("BBox", TestPdf.Box(0, 0, 600, 840)), ("XStep", PdfInteger.Of(600)), ("YStep", PdfInteger.Of(840)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", font)))))),
            "BT /F1 12 Tf 102 704 Td (MUSTERGEHEIM) Tj ET"u8.ToArray());
        var pattern = pdf.Add(cell);
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("Pattern"), TestPdf.Dict(("P1", TestPdf.Ref(pattern))));
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 12 Tf 100 760 Td (Sichtbar) Tj ET q /Pattern cs /P1 scn 100 700 120 20 re f Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var saved = WithBars(doc, (0, new PdfRectangle(96, 696, 224, 724)));
        Assert.False(System.Text.Encoding.Latin1.GetString(saved).Contains("MUSTERGEHEIM", StringComparison.Ordinal));
        using var after = PdfDocument.Open(saved);
        var content = PageContent(saved);
        var patterns = after.Pages[0].Resources?.GetDictionary(PdfName.Get("Pattern"));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(content, @"/(\w+)\s+scn"))
            Assert.True(patterns?.Get(PdfName.Get(m.Groups[1].Value)) is PdfStream, $"„/{m.Groups[1].Value} scn“ ohne Muster in den Ressourcen");
        Assert.Contains("Sichtbar", Core.Pdf.Text.PageText.Create(PageInterpreter.Build(after, after.Pages[0]).Text).Text);
    }

    /// <summary>
    /// A pattern chosen with "sc" (as "scn" in a pattern colour space; the display paints with it), a bar elsewhere on the page: the fill
    /// keeps its pattern — "sc" counted as clearing the pattern, it left the resources and the fill painted nothing
    /// (docs/FEHLERGEDAECHTNIS.md, No. 412).
    /// </summary>
    [Fact]
    public void PatternChosenWithSc_StaysWhereItPaints()
    {
        var pdf = TestPdf.Simple(1);
        var cell = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Pattern")), ("PatternType", PdfInteger.Of(1)), ("PaintType", PdfInteger.Of(1)), ("TilingType", PdfInteger.Of(1)),
            ("BBox", TestPdf.Box(0, 0, 10, 10)), ("XStep", PdfInteger.Of(10)), ("YStep", PdfInteger.Of(10)), ("Resources", new PdfDictionary())),
            "1 0 0 rg 0 0 10 10 re f"u8.ToArray());
        var pattern = pdf.Add(cell);
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("Pattern"), TestPdf.Dict(("P1", TestPdf.Ref(pattern))));
        pdf.Replace(4, TestPdf.FlateStream("q /Pattern cs /P1 sc 100 500 100 100 re f Q BT /F1 12 Tf 100 700 Td (Geheim) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim"))));
        Assert.DoesNotContain("Geheim", PageContent(WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim")))));
        using var image = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        var fill = image.GetPixel(150, image.Height - 550);
        Assert.True(fill.Red > 200 && fill.Green < 60, $"Fläche {fill}");
    }

    /// <summary>
    /// "/F2 Tf" with a single operand, which the display ignores: the next line is still shown in F1, so after the bar over the first word F1
    /// stays as it is — counted as switching to F2, F1 seemed to show nothing more and became the bare stand-in, and the line changed its font
    /// (docs/FEHLERGEDAECHTNIS.md, No. 413).
    /// </summary>
    [Fact]
    public void TfWithOneOperand_FontStillShowingStays()
    {
        var pdf = TestPdf.Simple(1);
        var resources = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!;
        resources.Set(PdfName.Get("Font"), TestPdf.Dict(
            ("F1", TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Courier")))),
            ("F2", TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica"))))));
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 12 Tf 100 700 Td (Geheim) Tj /F2 Tf 0 -20 Td (Bleibt) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var saved = WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim")));
        Assert.DoesNotContain("Geheim", PageContent(saved));
        using var after = PdfDocument.Open(saved);
        var f1 = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
        Assert.Equal("Courier", f1.GetName(PdfName.Get("BaseFont"))?.Value);
    }

    /// <summary>
    /// The last characters of an embedded font removed (docs/FEHLERGEDAECHTNIS.md, No. 674; found by the round trip of 6.8, through „Text
    /// bearbeiten“): its stand-in is a Type 3 font without glyphs, needing no font program — the standard Helvetica before was a font not
    /// embedded while the „Tf“ still named it (ISO 14289-1, 7.21.4.1; ISO 19005-2, 6.2.11.4.1), and a PDF/UA file kept its identification.
    /// </summary>
    /// <remarks>
    /// Tagged, the stand-in has a font descriptor (No. 687; found by the run of all ways to write: ISO 32000-2, Table 110 asks for one in a
    /// tagged file); untagged it needs none.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LastCharactersOfAnEmbeddedFont_StandInNeedsNoFontProgram(bool tagged)
    {
        var ttf = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var pdf = TestPdf.Simple(1);
        var file = pdf.Add(TestPdf.FlateStream(ttf, TestPdf.Dict(("Length1", PdfInteger.Of(ttf.Length)))));
        var resources = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!;
        resources.Set(PdfName.Get("Font"), TestPdf.Dict(("F1", TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")),
            ("BaseFont", TestPdf.N("LiberationSans")), ("Encoding", TestPdf.N("WinAnsiEncoding")), ("FirstChar", PdfInteger.Of(32)), ("LastChar", PdfInteger.Of(126)),
            ("Widths", new PdfArray([.. Enumerable.Repeat<PdfObject>(PdfInteger.Of(600), 95)])),
            ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("LiberationSans")), ("Flags", PdfInteger.Of(32)),
                ("FontBBox", TestPdf.Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
                ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file))))))));
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 12 Tf 100 700 Td (Max Mustermann) Tj ET"u8.ToArray()));
        if (tagged) ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("MarkInfo"), TestPdf.Dict(("Marked", PdfBoolean.True)));
        using var doc = PdfDocument.Open(pdf.Build());
        var saved = WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Max Mustermann")));
        using var after = PdfDocument.Open(saved);
        Assert.Contains("/F1 12 Tf", PageContent(saved));
        var f1 = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
        Assert.Equal("Type3", f1.GetName(PdfName.Subtype)?.Value);
        Assert.Empty(f1.GetDictionary(PdfName.Get("CharProcs"))!.Entries);
        var descriptor = f1.GetDictionary(PdfName.Get("FontDescriptor"));
        if (tagged)
        {
            Assert.NotNull(descriptor);
            Assert.Equal(4, descriptor.GetInteger(PdfName.Get("Flags")));
            Assert.Equal(0, descriptor.GetInteger(PdfName.Get("ItalicAngle")));
            Assert.False(descriptor.ContainsKey(PdfName.Get("FontFile2")));
        }
        else Assert.Null(descriptor);
        // no font without its program (the check of PDF/UA-1, which takes the font rules of PDF/A level u)
        Assert.DoesNotContain(Core.Pdf.Accessibility.AccessibilityCheck.Check(after, Core.Pdf.Accessibility.AccessibilityProfile.PdfUa1).Findings,
            f => f.Id.StartsWith("7.21.4.1", StringComparison.Ordinal));
    }

    /// <summary>
    /// A rectangle whose painting comes after a "cm" (and one after marked content), a bar over its left part: the display paints the
    /// construction with the matrix in effect at the painting, so the redacted rectangle lies where the display drew it, beside the bar
    /// still painted and nothing of it under the bar — taken as a broken path, the construction stayed in the file unchanged and its
    /// painting was dropped (docs/FEHLERGEDAECHTNIS.md, No. 414).
    /// </summary>
    [Theory]
    [InlineData("100 300 200 100 re 1 0 0 1 50 0 cm f", 150)]
    [InlineData("150 300 200 100 re /Artifact BMC f EMC", 150)]
    public void PaintingAfterCmOrMarkedContent_CutWhereTheDisplayDrawsIt(string drawing, int left)
    {
        using var doc = LinePage("q 1 0 0 rg " + drawing + " Q BT /F1 12 Tf 400 700 Td (Quark) Tj ET");
        using (var image = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new Rendering.ImageCache()))
            Assert.True(image.GetPixel(left + 150, image.Height - 350).Red > 200 && image.GetPixel(left + 150, image.Height - 350).Green < 60);
        var saved = WithBars(doc, (0, new PdfRectangle(left, 290, left + 60, 410)));
        using var after = PdfDocument.Open(saved);
        using var drawn = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        var beside = drawn.GetPixel(left + 150, drawn.Height - 350);
        Assert.True(beside.Red > 200 && beside.Green < 60, $"neben dem Balken {beside}");
        // nothing of the rectangle under the bar: its original corner is no longer in the content
        Assert.DoesNotContain(drawing.Split(" re ")[0] + " re", PageContent(saved));
    }

    /// <summary>
    /// A line that loses a word, then "T*" with the leading of the graphics state — set in an earlier block that changed it to 30 only inside
    /// (and, in a form, inherited from the page): the next line stays on its baseline — the rewriter followed the leading on its own, without
    /// q/Q and without inheritance, and moved it (docs/FEHLERGEDAECHTNIS.md, No. 415).
    /// </summary>
    [Theory]
    [InlineData("12 TL q BT 30 TL ET Q BT /F1 12 Tf 72.3 700 Td (Geheim und mehr) Tj T* (Naechste Zeile) Tj ET", false)]
    [InlineData("12 TL /Fx Do", true)]
    public void LineAfterAnEvenedOne_KeepsTheLeadingOfTheDisplay(string content, bool inForm)
    {
        var pdf = TestPdf.Simple(1);
        if (inForm)
        {
            var font = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.GetRaw(PdfName.Get("F1"))!;
            var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 612, 792)),
                ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", font)))))),
                "BT /F1 12 Tf 72.3 700 Td (Geheim und mehr) Tj T* (Naechste Zeile) Tj ET"u8.ToArray()));
            ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("XObject"), TestPdf.Dict(("Fx", TestPdf.Ref(form))));
        }
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content)));
        using var doc = PdfDocument.Open(pdf.Build());
        double Baseline(PdfDocument d, string word)
        {
            var text = Core.Pdf.Text.PageText.Create(PageInterpreter.Build(d, d.Pages[0]).Text);
            var q = text.Quads(Assert.Single(text.Find(word, matchCase: true)))[0];
            return Math.Min(q.Y0, q.Y3);
        }
        var before = Baseline(doc, "Naechste");
        using var after = PdfDocument.Open(WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim"))));
        Assert.Equal(before, Baseline(after, "Naechste"), 2);
    }

    /// <summary>
    /// Operators with too few operands (a font without its size, a rectangle with two numbers), which the display ignores, are not written
    /// again with the redacted page: a reference reader stops drawing a page at the first of them, and the bars written after it were
    /// missing there (docs/FEHLERGEDAECHTNIS.md, No. 416).
    /// </summary>
    [Fact]
    public void OperatorsLackingOperands_NotWrittenAgain()
    {
        using var doc = LinePage("BT /F1 12 Tf 100 700 Td (Geheim) Tj /F1 Tf ET 7 8 re 1 0 0 rg 100 500 50 50 re f");
        var saved = WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim")));
        var content = PageContent(saved);
        Assert.DoesNotContain("Geheim", content);
        Assert.DoesNotContain("7 8 re", content);
        Assert.DoesNotMatch(@"(^|\s)/F1\s+Tf", content);
        using var after = PdfDocument.Open(saved);
        using var image = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        var square = image.GetPixel(125, image.Height - 525);
        Assert.True(square.Red > 200 && square.Green < 60, $"Quadrat {square}");
    }

    /// <summary>
    /// A half-transparent rectangle in a non-isolated knockout group, a bar across it: the cut rectangle is written without a clip around it —
    /// a reference reader blended a clipped element of a knockout group with the group's content beneath instead of knocking it out
    /// (docs/FEHLERGEDAECHTNIS.md, No. 417); it is cut at the bar all the same, and our display shows the knockout as before.
    /// </summary>
    [Fact]
    public void ElementOfAKnockoutGroup_CutWithoutAClip()
    {
        var pdf = TestPdf.Simple(1);
        var half = TestPdf.Dict(("ca", new PdfReal(0.5)));
        var group = TestPdf.Dict(("S", TestPdf.N("Transparency")), ("K", PdfBoolean.True), ("I", PdfBoolean.False));
        var formDict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 612, 792)), ("Group", group),
            ("Resources", TestPdf.Dict(("ExtGState", TestPdf.Dict(("H", half))))));
        var form = pdf.Add(new PdfStream(formDict, "0.9 0.85 0.7 rg 0 0 612 792 re f /H gs 1 0 0 rg 100 300 200 200 re f"u8.ToArray()));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("XObject"), TestPdf.Dict(("Fk", TestPdf.Ref(form))));
        pdf.Replace(4, TestPdf.FlateStream("0 0 1 rg 0 380 612 40 re f /Fk Do"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var before = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new Rendering.ImageCache());
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(180, 250, 220, 550))));
        var forms = after.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!;
        var copy = forms.Entries.Select(e => forms.Get(e.Key)).OfType<PdfStream>().Single(f => f.Dictionary.GetDictionary(PdfName.Get("Group")) is not null);
        var content = System.Text.Encoding.ASCII.GetString(after.GetStreamData(copy));
        Assert.DoesNotContain("W*", content);
        Assert.DoesNotContain("100 300 200 200 re", content);
        using var drawn = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        // beside the bar the knockout shows as before: over the blue band the red knocks the beige out down to the band
        var top = (int)doc.Pages[0].MediaBox.Top;
        foreach (var (x, y) in (ReadOnlySpan<(int, int)>)[(130, 400), (270, 400), (130, 330)])
        {
            var (a, b) = (before.GetPixel(x, top - y), drawn.GetPixel(x, top - y));
            Assert.True(Math.Abs(a.Red - b.Red) <= 2 && Math.Abs(a.Green - b.Green) <= 2 && Math.Abs(a.Blue - b.Blue) <= 2, $"({x}, {y}): {a} → {b}");
        }
    }

    /// <summary>
    /// A line whose characters are set one by one (each by its own Tm, left to right and right to left), a bar over its middle word: the
    /// characters of the words after it keep their distances — only a word's start goes onto the grid. Each character went onto the grid by
    /// itself, the distances inside the words changed by up to half a point, and a reference reader split them (docs/FEHLERGEDAECHTNIS.md,
    /// No. 418).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharactersSetOneByOne_KeepTheirDistancesInWords(bool backwards)
    {
        // where the characters stand when the line is set as one string: then each is placed there by itself
        using var plain = LinePage("BT /F1 12 Tf 72.37 700 Td (Herr Geheim Bleibtdort) Tj ET");
        var glyphs = PageInterpreter.Build(plain, plain.Pages[0]).Text.Where(g => g.Text != " ").ToList();
        var order = backwards ? Enumerable.Reverse(glyphs) : glyphs;
        var content = new System.Text.StringBuilder("BT /F1 12 Tf ");
        foreach (var g in order)
            content.Append(FormattableString.Invariant($"1 0 0 1 {g.X0:0.###} 700 Tm ({g.Text}) Tj "));
        using var doc = LinePage(content.Append("ET").ToString());
        double[] Starts(PdfDocument d) => [.. PageInterpreter.Build(d, d.Pages[0]).Text.Where(g => "Bleibtdort".Contains(g.Text) && g.X0 > 140).OrderBy(g => g.X0).Select(g => g.X0)];
        var before = Starts(doc);
        using var after = PdfDocument.Open(WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim"))));
        var now = Starts(after);
        Assert.Equal(before.Length, now.Length);
        // within a hundredth of a point (the content here gives the places to a thousandth; the grid moved them by up to half a point)
        for (var i = 1; i < before.Length; i++) Assert.InRange(Math.Abs(before[i] - before[i - 1] - (now[i] - now[i - 1])), 0, 0.01);
    }

    /// <summary>
    /// A character flattened to a slanted line (text matrix 1 10 0 0: no area, it draws nothing), a small bar over the middle of that line
    /// covering a little less than half of it: the character goes — it stayed, and its text was in the file at the bar
    /// (docs/FEHLERGEDAECHTNIS.md, No. 419).
    /// </summary>
    [Fact]
    public void FlattenedCharacter_MiddleUnderTheBar_Goes()
    {
        using var doc = LinePage("BT /F1 12 Tf 100 700 Td (Sichtbar) Tj ET BT /F1 1 Tf 1 10 0 0 200 500 Tm (Z) Tj ET");
        var saved = WithBars(doc, (0, new PdfRectangle(200.05, 501.55, 200.55, 504.47)));
        Assert.DoesNotContain("(Z)", PageContent(saved));
        Assert.Contains("Sichtbar", PageContent(saved));
    }

    /// <summary>
    /// The same with a matrix that stretches the character to 10^296 points (both rows alike: it lies on a line): the squares of its
    /// distances were infinite, the middle of the line was taken from the wrong corners, and the character stayed under the bar
    /// (docs/FEHLERGEDAECHTNIS.md, No. 419).
    /// </summary>
    [Fact]
    public void FlattenedCharacterOfHugeSize_MiddleUnderTheBar_Goes()
    {
        string Big(int zeros) => "1" + new string('0', zeros);
        using var doc = LinePage($"BT /F1 12 Tf 100 700 Td (Sichtbar) Tj ET BT /F1 1 Tf {Big(225)} {Big(296)} {Big(225)} {Big(296)} 0 0 Tm (Z) Tj ET");
        var saved = WithBars(doc, (0, new PdfRectangle(3e224, 3e295, 1e225, 1e296)));
        Assert.DoesNotContain("(Z)", PageContent(saved));
    }

    /// <summary>
    /// A path whose painting comes in a block of its own after its construction („path … 1 w 2 j q 2 w S Q“, as plotting programs write
    /// curves), with a bar beside the line but within the box around it: the curve stays painted (docs/FEHLERGEDAECHTNIS.md, No. 262) — the
    /// painting went lost, the curve vanished, and a dashed one turned into a frame.
    /// </summary>
    [Fact]
    public void PaintingInABlockAfterTheConstruction_CurveStays()
    {
        using var doc = LinePage("q 1 0 0 RG 100 650 m 150 680 l 180 720 l 200 760 l 1 w 2 j q 3 w S Q Q\n" +
                                 "BT /F1 12 Tf 110 735 Td (Quark) Tj ET");
        var saved = WithBars(doc, (0, new PdfRectangle(109, 732, 146, 746)));
        using var after = PdfDocument.Open(saved);
        using var image = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        var p = Rendering.PageGeometry.PageToDevice(after.Pages[0], 1).Transform(165, 700);   // on the segment (150, 680) – (180, 720)
        var on = image.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y));
        Assert.True(on.Red > 180 && on.Green < 100, $"die Kurve ist noch da ({on})");
        Assert.DoesNotContain("Quark", PageContent(saved));
    }

    /// <summary>
    /// A small character just right of a bar, kept (a fifth of its shape under the bar), moves a quarter point towards the bar when its line is
    /// written anew on the grid: the cross-check measured it narrowed by half the grid on both sides — then three fifths of what was left lay
    /// under the bar, and the page was refused (DocLayNet, a comma after a redacted word; docs/FEHLERGEDAECHTNIS.md, No. 266).
    /// </summary>
    [Fact]
    public void SmallCharacterBesideABar_MovedByTheGrid_PageIsRedacted()
    {
        // a comma at 7 pt (its shape about 0.63 to 1.32 pt from its start), a fifth of its shape under the bar; it starts 0.249 pt beside
        // the grid and moves that far towards the bar
        using var doc = LinePage("BT /F1 10 Tf 1 0 0 1 200 630 Tm (Wort) Tj /F1 7 Tf 1 0 0 1 226.749 630 Tm (,) Tj " +
                                 "/F1 10 Tf 1 0 0 1 232 630 Tm (bleibt) Tj ET");
        var saved = WithBars(doc, (0, new PdfRectangle(199, 627.5, 227.565, 637.8)));
        var content = PageContent(saved);
        Assert.DoesNotContain("Wort", content);
        Assert.Contains("(,)", content);
        Assert.Contains("bleibt", content);
    }

    /// <summary>
    /// A word set in several text operators (a larger capital inside it, a font change) in a line that loses a word: it stays one word —
    /// before, every operator began a word on the grid and the word read „Pro Perties“ afterwards (DocLayNet; docs/FEHLERGEDAECHTNIS.md,
    /// No. 275).
    /// </summary>
    [Fact]
    public void WordInSeveralTextOperators_StaysOneWord()
    {
        foreach (var x in new[] { 72.0, 72.13, 72.27, 72.41 })
        {
            var start = x.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // 6 pt, the capitals 7.5 pt, each a little apart from the letter before it (0.13 em, below the 0.15 em of a space)
            using var doc = LinePage($"BT /F1 6 Tf {start} 700 Td (Killam Pro) Tj /F1 7.5 Tf [-130 (P)] TJ /F1 6 Tf [-130 (erties inc )] TJ /F1 7.5 Tf (A) Tj /F1 6 Tf [-130 (nnual)] TJ ET");
            var before = Core.Pdf.Text.PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]));
            Assert.Single(before.Find("ProPerties"));
            var saved = WithBars(doc, [.. before.RedactionAreas(before.Find("Killam")[0]).Select(a => (0, a))]);
            using var after = PdfDocument.Open(saved);
            var text = Core.Pdf.Text.PageText.Create(PageInterpreter.BuildText(after, after.Pages[0]));
            Assert.True(text.Find("ProPerties").Count == 1 && text.Find("inc Annual").Count == 1 && !text.Text.Contains("Killam"), $"{start}: „{text.Text}“");
        }
    }

    /// <summary>
    /// „Text schwärzen“ on a line turned by 90° (a heading in the margin) and on vertical writing: the bar covers the whole word, not only up
    /// to its middle — before, the line itself counted as its neighbour and cut the bar, the last letters stayed (docs/FEHLERGEDAECHTNIS.md,
    /// No. 274).
    /// </summary>
    [Fact]
    public void TurnedLine_BarCoversTheWholeWord()
    {
        foreach (var matrix in new[] { "0 6 -6 0 11.3385 142.813", "0 -6 6 0 30 700" })
        {
            using var doc = LinePage($"BT /F1 1 Tf {matrix} Tm (FINANCIAL SECTION ANALYSIS) Tj ET");
            var text = Core.Pdf.Text.PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]));
            var hit = text.Find("FINANCIAL")[0];
            var areas = text.RedactionAreas(hit);
            foreach (var quad in text.Quads(hit))
            {
                var b = quad.Bounds;
                Assert.True(areas.Any(a => a.Left <= b.Left + 0.01 && a.Right >= b.Right - 0.01 && a.Bottom <= b.Top + 0.01 && a.Top >= b.Bottom - 0.01),
                    $"{matrix}: {string.Join("; ", areas)} deckt {b} nicht");
            }
            var saved = WithBars(doc, [.. areas.Select(a => (0, a))]);
            using var after = PdfDocument.Open(saved);
            var rest = Core.Pdf.Text.PageText.Create(PageInterpreter.BuildText(after, after.Pages[0])).Text;
            Assert.True(rest.TrimStart().StartsWith("SECTION", StringComparison.Ordinal), $"{matrix}: „{rest}“");
        }
    }

    /// <summary>
    /// A letter-spaced word just below the gap a reader takes for a space (Tc 0.1499 em), in a line that loses a word: rounding the
    /// spacing inside the word must not open such a gap — before, the turned line read „S ECTI ON“ afterwards, and no search found it
    /// (DocLayNet; docs/FEHLERGEDAECHTNIS.md, No. 275).
    /// </summary>
    [Fact]
    public void LetterSpacedWordBesideARedaction_StaysOneWord()
    {
        // as in a margin: turned by 90°, 6 pt, Tc just below 0.15 em; and upright
        foreach (var matrix in new[] { "0 6 -6 0 11.3385 142.813", "6 0 0 6 72 700" })
        {
            var content = $"BT /F1 1 Tf 0.1499 Tc {matrix} Tm (FINANCIAL SECTION ANALYSIS WORKINGS) Tj ET";
            using var doc = LinePage(content);
            var before = Core.Pdf.Text.PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]));
            Assert.Single(before.Find("WORKINGS"));
            var saved = WithBars(doc, [.. before.RedactionAreas(before.Find("FINANCIAL")[0]).Select(a => (0, a))]);
            using var after = PdfDocument.Open(saved);
            var text = Core.Pdf.Text.PageText.Create(PageInterpreter.BuildText(after, after.Pages[0]));
            Assert.True(!text.Text.Contains("FINANCIAL") && !text.Text.Contains(" L"), $"{matrix}: „{text.Text}“");
            foreach (var word in new[] { "SECTION", "ANALYSIS", "WORKINGS" })
                Assert.True(text.Find(word).Count == 1, $"{matrix}: „{text.Text}“");
        }
    }

    // ------------------------------------------------------------ Lines that lose characters (Edact-Ray, B-12)

    /// <summary>A page whose content is <paramref name="content"/> (font /F1: Helvetica, 12 pt in the tests).</summary>
    private static PdfDocument LinePage(string content)
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(content)));
        return PdfDocument.Open(pdf.Build());
    }

    private static string PageContent(byte[] data)
    {
        using var doc = PdfDocument.Open(data);
        return System.Text.Encoding.Latin1.GetString(doc.GetStreamData((PdfStream)doc.Pages[0].Dictionary.Get(PdfName.Contents)!));
    }

    /// <summary>Where a word starts on the page (left edge of its first character).</summary>
    private static double StartOf(byte[] data, string word)
    {
        using var doc = PdfDocument.Open(data);
        var text = Core.Pdf.Text.PageText.Create(PageInterpreter.Build(doc, doc.Pages[0]).Text);
        return text.Quads(Assert.Single(text.Find(word, matchCase: true)))[0].X0;
    }

    /// <summary>
    /// Two lines that show the same — „sagt“ and „heute“ within a grid step of each other — and differ only in the redacted name
    /// („Mustermann“, 66.7 pt, or „Mustermanns“, 72.7 pt) and so in the numbers after it in TJ. Before, only the gap was rounded; the numbers after it
    /// stayed exact, and with the widths of the font they gave the width of the name away (Edact-Ray, Bland, Iyer, Levchenko, PoPETs 2023;
    /// docs/RECHERCHE-VERSTECKEN.md, B-12). Now every word of the line starts on the grid: both files have the same content, and the rest
    /// of the line moves by at most a quarter point.
    /// </summary>
    [Fact]
    public void RedactedLine_NumbersAfterTheWord_TellNothing()
    {
        static byte[] Redacted(string word, double width, double sagt, double heute)
        {
            var afterWord = -(sagt - (98.328 + width)) / 12 * 1000;
            var afterSagt = -(heute - (sagt + 22.68)) / 12 * 1000;
            using var doc = LinePage(FormattableString.Invariant(
                $"BT /F1 12 Tf 72 700 Td [(Herr) -250 ({word}) {afterWord:0.####} (sagt) {afterSagt:0.####} (heute)] TJ ET"));
            return WithBars(doc, (0, new PdfRectangle(97, 696, 174, 710)));
        }
        var a = Redacted("Mustermann", 66.684, 190.05, 230.30);
        var b = Redacted("Mustermanns", 72.684, 190.20, 230.40);
        Assert.Equal(PageContent(a), PageContent(b));
        Assert.InRange(StartOf(a, "sagt"), 189.8, 190.3);
        Assert.InRange(StartOf(a, "heute"), 230.05, 230.55);
        Assert.InRange(StartOf(a, "Herr"), 71.99, 72.01);
    }

    /// <summary>
    /// Justified with word spacing: Tw 9.05 sets the spaces of a line with the name in it, and also of the next line (Tw outlives
    /// ET). Before, „9.05 Tw“ stayed in the file and gave the width of the name away. Now the redacted line is written without word
    /// spacing, its words start on the grid; the other line keeps exactly its positions — written as it was, byte for byte, with the
    /// value set again just before it, which its spaces show anyway (before No. 804 it was written out character by character, the same
    /// value in numbers; untouched lines fell into other sub-pixel steps of a renderer).
    /// </summary>
    [Fact]
    public void RedactedLine_WordSpacing_WrittenAsZero_OtherLineStays()
    {
        const string content = "BT /F1 12 Tf 72 700 Td 9.05 Tw (Herr Mustermann sagt heute) Tj ET BT /F1 12 Tf 72 680 Td (Zweite Zeile bleibt) Tj ET";
        using var doc = LinePage(content);
        var before = WithBars(doc);
        var after = WithBars(doc, (0, new PdfRectangle(106, 696, 176, 710)));
        var written = PageContent(after);
        Assert.DoesNotContain("9.05", written[..written.IndexOf("ET", StringComparison.Ordinal)]);   // not with the redacted line
        Assert.Contains("(Zweite Zeile bleibt) Tj", written);                                         // the other line as it was
        Assert.DoesNotContain("Mustermann", written);
        var sagt = StartOf(after, "sagt");
        Assert.InRange(sagt, StartOf(before, "sagt") - 0.26, StartOf(before, "sagt") + 0.26);
        Assert.Equal(0, Math.Abs(sagt - Math.Round(sagt / 0.5) * 0.5), 2);
        foreach (var word in new[] { "Zweite", "Zeile", "bleibt" }) Assert.Equal(StartOf(before, word), StartOf(after, word), 3);
    }

    /// <summary>
    /// One character spacing for a whole paragraph (0.5 Tc set once, as in many DocLayNet pages): the line with the name is written with
    /// Tc 0, the others stay byte for byte as they were, at the same places, with the value set again just before the next one — before,
    /// they were written out character by character, the same positions in numbers, and single characters fell into another sub-pixel
    /// step of a renderer (39 of 4 981 DocLayNet cases with „Länge verbergen“; docs/FEHLERGEDAECHTNIS.md, No. 804).
    /// </summary>
    [Fact]
    public void RedactedLine_SharedCharSpacing_OtherLinesStayByteForByte()
    {
        const string content = "BT /F1 12 Tf 72 700 Td 0.5 Tc (Herr Mustermann sagt) Tj 0 -14 TD (Zweite Zeile bleibt) Tj T* (Dritte Zeile auch) Tj ET";
        using var doc = LinePage(content);
        var before = WithBars(doc);
        var after = WithBars(doc, (0, new PdfRectangle(106, 696, 196, 710)));
        var written = PageContent(after);
        Assert.DoesNotContain("Mustermann", written);
        Assert.Contains("(Zweite Zeile bleibt) Tj", written);
        Assert.Contains("(Dritte Zeile auch) Tj", written);
        // the redacted line without the spacing it was laid out with; the value comes back once, just before the next line
        Assert.DoesNotContain("0.5 Tc", written[..written.IndexOf(" TJ", StringComparison.Ordinal)]);   // up to the redacted line, written anew as TJ
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(written, @"(?<![\d.])0\.5 Tc"));
        foreach (var word in new[] { "Zweite", "bleibt", "Dritte", "auch" }) Assert.Equal(StartOf(before, word), StartOf(after, word), 3);
    }

    /// <summary>
    /// Each word placed with its own Td: the move to the word after the name is the name's width plus the space. Two files that show
    /// the same differ only there. Now the line is set with absolute positions on the grid: the files are the same.
    /// </summary>
    [Fact]
    public void RedactedLine_MovesPerWord_TellNothing()
    {
        static byte[] Redacted(string word, double sagt, double heute)
        {
            using var doc = LinePage(FormattableString.Invariant(
                $"BT /F1 12 Tf 72 700 Td (Herr) Tj 26.328 0 Td ({word}) Tj {sagt - 98.328:0.####} 0 Td (sagt) Tj {heute - sagt:0.####} 0 Td (heute) Tj ET ") +
                "BT /F1 12 Tf 72 680 Td (Zweite Zeile) Tj ET");
            return WithBars(doc, (0, new PdfRectangle(97, 696, 174, 710)));
        }
        var a = Redacted("Mustermann", 190.05, 230.30);
        var b = Redacted("Mustermanns", 190.20, 230.40);
        Assert.Equal(PageContent(a), PageContent(b));
        Assert.InRange(StartOf(a, "heute"), 230.05, 230.55);
        Assert.InRange(StartOf(a, "Zweite"), 71.99, 72.01);
    }

    /// <summary>
    /// A long word after the redacted one, set with character spacing (Tc 0.65 at 12 pt): the line is set anew with Tc 0 and the spacing
    /// inside the word as rounded numbers. Before, each character's spacing was rounded on top of the one before, and the rounding errors
    /// added up — the end of the word moved by about 0.7 pt (docs/FEHLERGEDAECHTNIS.md, No. 241). Now every character stays within a quarter
    /// point of where it was.
    /// </summary>
    [Fact]
    public void RedactedLine_CharacterSpacing_LongWordDoesNotDrift()
    {
        using var doc = LinePage("BT /F1 12 Tf 0.65 Tc 72 700 Td (Geheim Zusammenfassungen) Tj ET ");
        static List<(string T, float X)> Glyphs(PdfDocument d) => [.. PageInterpreter.BuildText(d, d.Pages[0]).Select(g => (g.Text, g.X0))];
        var before = Glyphs(doc).Skip(7).ToList();   // „Zusammenfassungen“
        var data = WithBars(doc, (0, new PdfRectangle(70, 696, 118, 712)));
        using var after = PdfDocument.Open(data);
        var now = Glyphs(after).Where(g => g.T != " ").ToList();
        Assert.Equal(string.Concat(before.Select(g => g.T)), string.Concat(now.Select(g => g.T)));
        for (var i = 0; i < before.Count; i++)
            Assert.True(Math.Abs(now[i].X - before[i].X) <= 0.25, $"„{before[i].T}“ ({i}) um {now[i].X - before[i].X:0.00} pt verschoben");
    }

    /// <summary>
    /// Text inside the cell of a tiling pattern, a bar over part of it: what lies under the bar leaves the file, the rest still shows —
    /// the whole pattern was emptied for the page (docs/FEHLERGEDAECHTNIS.md, No. 163).
    /// </summary>
    [Fact]
    public void PatternCellText_HalfUnderBar_RestStays()
    {
        var pdf = TestPdf.Simple(1);
        var font = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.GetRaw(PdfName.Get("F1"))!;
        var cell = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Pattern")), ("PatternType", PdfInteger.Of(1)), ("PaintType", PdfInteger.Of(1)), ("TilingType", PdfInteger.Of(1)),
            ("BBox", TestPdf.Box(0, 0, 600, 840)), ("XStep", PdfInteger.Of(600)), ("YStep", PdfInteger.Of(840)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", font)))))), "BT /F1 20 Tf 100 500 Td (Geheimnis Bleibt) Tj ET"u8.ToArray());
        var pattern = pdf.Add(cell);
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("Pattern"), TestPdf.Dict(("P1", TestPdf.Ref(pattern))));
        pdf.Replace(4, TestPdf.FlateStream("q /Pattern cs /P1 scn 90 490 220 30 re f Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var bar = new PdfRectangle(95, 492, 190, 518);   // over „Geheimnis“
        var data = WithBars(doc, (0, bar));
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(data, ["Geheimnis"], null, out _));
        Assert.NotEmpty(PdfKnead.Cli.LeakSearch.Find(data, ["Bleibt"], null, out _));
        using var after = PdfDocument.Open(data);
        using var image = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        var dark = 0;
        for (var x = 200; x < 270; x++)
            for (var y = image.Height - 518; y < image.Height - 495; y++)
                if (image.GetPixel(x, y).Red < 100) dark++;
        Assert.True(dark > 50, $"„Bleibt“ fehlt ({dark} dunkle Pixel)");
    }

    /// <summary>
    /// Text as a clip with a colour painted through it: all of it under the bar — the colour must not appear around it afterwards (an empty
    /// text clip clips nothing for some readers, docs/FEHLERGEDAECHTNIS.md, No. 152).
    /// </summary>
    [Fact]
    public void ClipTextAllRemoved_NothingShowsAroundIt()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("q BT 7 Tr /F1 40 Tf 72 600 Td (WAVES) Tj ET 0 0 1 rg 0 0 612 792 re f Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(60, 580, 260, 650))));
        using var image = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        var outside = image.GetPixel(300, image.Height - 300);
        Assert.True(outside.Red > 200 && outside.Blue > 200, $"{outside}");
    }

    /// <summary>
    /// Text as a clip ending in a space, all letters under the bar, the space beside it: the text object leaves no outline, so the clip must
    /// become empty and the blue must not appear around it. The rewriter judged by the bytes left (the space) and wrote no empty clip; the
    /// display, since No. 433, sets no clip for a text object without outlines — the blue covered the page (docs/FEHLERGEDAECHTNIS.md,
    /// No. 441).
    /// </summary>
    [Fact]
    public void ClipTextWithTrailingSpace_AllLettersRemoved_ClipBecomesEmpty()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("q BT 7 Tr /F1 40 Tf 72 600 Td (WAVES ) Tj ET 0 0 1 rg 0 0 612 792 re f Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "WAVES"))));
        Assert.DoesNotContain("WAVES", Read(after, after.Pages[0]).Text);
        using var image = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        var outside = image.GetPixel(300, image.Height - 300);
        Assert.True(outside.Red > 200 && outside.Blue > 200, $"{outside}");
    }

    /// <summary>
    /// The same in a form that inherits the clip mode from the page: the rewriter followed Tr on its own, from 0 in every stream, and wrote no
    /// empty clip in the form — readers that clip nothing with an empty text clip painted the blue over the whole page
    /// (docs/FEHLERGEDAECHTNIS.md, No. 426).
    /// </summary>
    [Fact]
    public void ClipTextAllRemovedInAFormWithTheModeOfThePage_ClipBecomesEmpty()
    {
        var pdf = TestPdf.Simple(1);
        var form = pdf.Add(TestPdf.FlateStream("BT /F1 40 Tf 72 600 Td (WAVES) Tj ET 0 0 1 rg 0 0 612 792 re f"u8.ToArray(), TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 612, 792)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))))))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("XObject"), TestPdf.Dict(("Fm0", TestPdf.Ref(form))));
        pdf.Replace(4, TestPdf.FlateStream("q 7 Tr /Fm0 Do Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(60, 580, 260, 650))));
        var all = Everything(after);
        Assert.DoesNotContain("WAVES", all);
        Assert.Contains("0 0 0 0 re W n", all);
    }

    /// <summary>
    /// A Type 3 font with a placeholder box (/FontBBox [0 0 1 -1]), a flipped matrix and the real boxes in d1 of its glyphs: the boxes of
    /// its characters come from d1 (docs/FEHLERGEDAECHTNIS.md, No. 149).
    /// </summary>
    [Fact]
    public void Type3PlaceholderBox_BoxesFromTheGlyphs()
    {
        var pdf = TestPdf.Simple(1);
        var proc = pdf.Add(TestPdf.FlateStream("117 0 5 0 111 100 d1 5 0 106 100 re f"u8.ToArray()));
        var t3 = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("FontBBox", TestPdf.Box(0, 0, 1, -1)),
            ("FontMatrix", TestPdf.Box(1, 0, 0, -1, 0, 0)), ("CharProcs", TestPdf.Dict(("a", TestPdf.Ref(proc)))),
            ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", new PdfArray([PdfInteger.Of(97), TestPdf.N("a")])))),
            ("FirstChar", PdfInteger.Of(97)), ("LastChar", PdfInteger.Of(97)), ("Widths", new PdfArray([PdfInteger.Of(117)]))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("T3"), TestPdf.Ref(t3));
        pdf.Replace(4, TestPdf.FlateStream("q 1 0 0 -1 0 792 cm BT /T3 0.12 Tf 1 0 0 -1 72 200 Tm (aaaa) Tj ET Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var area = AreaOf(doc, doc.Pages[0], "aaaa");
        Assert.True(area.Height > 11, $"{area.Height} pt");
    }

    /// <summary>
    /// A text object inside the construction of a rectangle the bar touches (its stroke follows the text): redacting the text keeps the
    /// stroke with its rectangle and in its colour — it went with the next rectangle, in the next colour (docs/FEHLERGEDAECHTNIS.md, No. 153).
    /// </summary>
    [Fact]
    public void TextInsidePathConstruction_StrokesStayWithTheirPaths()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("4 w 100 560 300 100 re BT /F1 12 Tf 72 600 Td (Geheim) Tj ET 1 0 0 RG S 100 300 300 100 re 0 0 1 RG S"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim"))));
        using var image = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        var upper = image.GetPixel(250, image.Height - 660);
        var lower = image.GetPixel(250, image.Height - 400);
        Assert.True(upper.Red > 200 && upper.Blue < 60, $"oben {upper}");
        Assert.True(lower.Blue > 200 && lower.Red < 60, $"unten {lower}");
    }

    /// <summary>
    /// Slanted characters (a diagonal stamp) count by the covered fraction of their rotated cell (from 25 %): two bars over the
    /// same strip count once (18 %, stays), two bars over different strips together (36 %, goes). Added up, the overlap counted
    /// twice and the character went although only 18 % of it was under the bars (No. 165, there for upright characters and paths).
    /// </summary>
    [Fact]
    public void SlantedCharacter_OverlappingBarsCountOnce()
    {
        (double X, double Y)[] diamond = [(50, 0), (100, 50), (50, 100), (0, 50)];   // a square turned by 45°, area 5000
        var top = Core.Pdf.Graphics.RectD.FromLtrb(0, 0, 100, 30);   // the tip down to 30: 900 = 18 %
        var bottom = Core.Pdf.Graphics.RectD.FromLtrb(0, 70, 100, 100);
        Assert.Equal(0.18, Redactor.QuadCoverage([top], diamond), 6);
        Assert.Equal(0.18, Redactor.QuadCoverage([top, top], diamond), 6);
        Assert.Equal(0.18, Redactor.QuadCoverage([top, Core.Pdf.Graphics.RectD.FromLtrb(10, 0, 90, 30)], diamond), 6);
        Assert.Equal(0.36, Redactor.QuadCoverage([top, bottom], diamond), 6);
    }

    /// <summary>
    /// Bars side by side that cover a letter only together (each a quarter of it): the letter goes — each bar alone covered too little,
    /// and the text stayed under the bars. Overlapping bars over a background rectangle leave it where they do not cover it — their
    /// areas were added up, overlaps twice, and the rectangle counted as covered (docs/FEHLERGEDAECHTNIS.md, No. 165).
    /// </summary>
    [Fact]
    public void BarsCoverOnlyTogether_CountedAsTheirUnion()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("1 0 0 rg 60 590 200 40 re f 0 g BT /F1 40 Tf 72 600 Td (GEHEIM) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var word = AreaOf(doc, page, "GEHEIM");
        // four bars side by side over the „H“, each a quarter of it wide
        var h = PageInterpreter.Build(doc, page).Text.First(g => g.Text == "H");
        var quarter = (h.X1 - h.X0) / 4;
        var strips = Enumerable.Range(0, 4).Select(i => new PdfRectangle(h.X0 + i * quarter, word.Bottom, h.X0 + (i + 1) * quarter, word.Top)).ToList();
        var result = Redactor.Apply(doc, page, strips);
        Assert.DoesNotContain("H", Read(doc, PageOf(page, result)).Text);
        // the same word under three overlapping bars: the red background around them stays
        using var after = PdfDocument.Open(WithBars(doc, (0, word), (0, word), (0, word)));
        using var image = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        var beside = image.GetPixel(250, image.Height - 625);
        Assert.True(beside.Red > 200 && beside.Green < 60, $"Hintergrund {beside}");
    }

    [Fact]
    public void Text_WordRemoved_RestOfLineStays()
    {
        using var doc = PdfDocument.Open(TextPage("Max Mustermann, Musterstrasse 12, 12345 Musterstadt", "Zweite Zeile bleibt"));
        var page = doc.Pages[0];
        var before = Read(doc, page);
        var area = AreaOf(doc, page, "Musterstrasse");

        var result = Redactor.Apply(doc, page, [area]);
        Assert.Equal("Musterstrasse", result.RemovedText);
        var after = Read(doc, PageOf(page, result));
        Assert.DoesNotContain("Musterstrasse", after.Text);
        Assert.Contains("Max Mustermann,", after.Text);
        Assert.Contains("12, 12345 Musterstadt", after.Text);
        Assert.Contains("Zweite Zeile bleibt", after.Text);
        // The characters after the gap stay almost where they were (coarsened: at most half the grid)
        var x12Before = before.Glyphs.First(g => g.Char == "1").X;
        var x12After = after.Glyphs.First(g => g.Char == "1").X;
        Assert.InRange(Math.Abs(x12After - x12Before), 0, Redactor.PositionGrid / 2 + 0.01);
        // The word no longer appears anywhere in the content
        var content = System.Text.Encoding.Latin1.GetString(doc.GetStreamData((PdfStream)result.Page.Get(PdfName.Contents)));
        Assert.DoesNotContain("Musterstrasse", content);
    }

    [Fact]
    public void Path_LargeShapeIsOnlyCutOut_SmallShapeRemoved()
    {
        using var doc = PdfDocument.Open(TextPage("x"));
        var page = doc.Pages[0];
        // The rectangle 72/400–272/500: a small bar inside it only cuts a hole
        var result = Redactor.Apply(doc, page, [new PdfRectangle(100, 430, 140, 460)]);
        Assert.Equal(1, result.RemovedPaths);
        using var bitmap = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, PageOf(page, result)), doc, PageOf(page, result), 1, new Rendering.ImageCache());
        var inside = bitmap.GetPixel(120, (int)(842 - 445));
        var outside = bitmap.GetPixel(200, (int)(842 - 445));
        Assert.True(inside.Red < 20 && inside.Green < 20 && inside.Blue < 20, $"innen {inside}");   // black bar
        Assert.True(outside.Blue > 150 && outside.Red < 120, $"außen {outside}");                    // the rectangle stays blue
        var content = System.Text.Encoding.Latin1.GetString(doc.GetStreamData((PdfStream)result.Page.Get(PdfName.Contents)));
        Assert.Contains("W*", content);   // cut out, not removed

        // A bar over almost the whole rectangle: removed, and its coordinates no longer appear in the content
        var removed = Redactor.Apply(doc, page, [new PdfRectangle(70, 398, 274, 502)]);
        var removedContent = System.Text.Encoding.Latin1.GetString(doc.GetStreamData((PdfStream)removed.Page.Get(PdfName.Contents)));
        Assert.DoesNotContain("72 400 200 100 re", removedContent);
    }

    /// <summary>Page with an image (20 × 10, stripes, drawn at 100/300 as 200 × 100) and a form XObject with text, drawn twice.</summary>
    private static byte[] ImageAndFormPage() => ImageAndFormPdf().Build();

    private static TestPdf ImageAndFormPdf()
    {
        var pdf = TestPdf.Simple(1);
        var pixels = new byte[20 * 10 * 3];
        for (var i = 0; i < 200; i++) (pixels[i * 3], pixels[i * 3 + 1], pixels[i * 3 + 2]) = ((byte)(i % 20 * 12), 200, 40);
        var image = pdf.Add(TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(20)), ("Height", PdfInteger.Of(10)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)))));
        var form = pdf.Add(TestPdf.FlateStream("BT /F1 12 Tf 10 20 Td (Geheimer Formtext) Tj ET"u8.ToArray(), TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 300, 50)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))))))));
        pdf.Replace(4, TestPdf.FlateStream("q 200 0 0 100 100 300 cm /Im0 Do Q q 1 0 0 1 72 600 cm /Fm0 Do Q q 1 0 0 1 72 200 cm /Fm0 Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image)), ("Fm0", TestPdf.Ref(form)))),
            ("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))));
        return pdf;
    }

    /// <summary>
    /// Two JBIG2 images on two pages share one symbol dictionary (/JBIG2Globals): after redacting on page 1 the shared dictionary is gone
    /// from the file — it holds the shapes of the redacted page's characters — and page 2 looks as before, its image written without it
    /// (docs/FEHLERGEDAECHTNIS.md, No. 353).
    /// </summary>
    [Fact]
    public void SharedJbig2SymbolDictionary_LeavesWithTheRedaction()
    {
        using var source = PdfDocument.Open(Path.Combine(Root(), "TestPDFs", "pdfjs", "bitmap-p32-eof.pdf"));
        var globals = System.Text.Encoding.Latin1.GetString(source.GetRawStreamData((PdfStream)source.GetObject(3)));
        var image = System.Text.Encoding.Latin1.GetString(source.GetRawStreamData((PdfStream)source.GetObject(6)));
        const string imageDict = "/Type/XObject/Subtype/Image/Width 399/Height 400/ColorSpace/DeviceGray/BitsPerComponent 1/Filter/JBIG2Decode/DecodeParms<</JBIG2Globals 3 0 R>>";
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R 5 0 R]/Count 2/MediaBox[0 0 612 792]>>",
            [3] = ("", globals),
            [4] = "<</Type/Page/Parent 2 0 R/Contents 8 0 R/Resources<</XObject<</Im0 6 0 R>>>>>>",
            [5] = "<</Type/Page/Parent 2 0 R/Contents 9 0 R/Resources<</XObject<</Im0 7 0 R>>>>>>",
            [6] = (imageDict, image),
            [7] = (imageDict, image),
            [8] = ("", "q 399 0 0 400 100 200 cm /Im0 Do Q"),
            [9] = ("", "q 399 0 0 400 100 200 cm /Im0 Do Q"),
        });
        using var doc = PdfDocument.Open(file);
        var saved = WithBars(doc, (0, new PdfRectangle(150, 300, 400, 500)));
        Assert.True(saved.AsSpan().IndexOf(System.Text.Encoding.Latin1.GetBytes(globals)) < 0, "die gemeinsame Symboltabelle steht noch in der Datei");
        using var after = PdfDocument.Open(saved);
        using var before2 = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[1]), doc, doc.Pages[1], 1, new Rendering.ImageCache());
        using var after2 = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[1]), after, after.Pages[1], 1, new Rendering.ImageCache());
        Assert.Equal(before2.GetPixelSpan().ToArray(), after2.GetPixelSpan().ToArray());
        Assert.True(Dark(doc, 1) > 1000, "Seite 2 zeigt das Bild");
    }

    /// <summary>
    /// The redacted name is set in an embedded font nothing else on the page uses: after redacting, that font is gone from the file — its
    /// program, its widths and its character map would tell which characters the name had (a font shared with other text keeps only what
    /// they show, docs/FEHLERGEDAECHTNIS.md, No. 169).
    /// </summary>
    [Fact]
    public void FontUsedOnlyByTheRedactedWord_LeavesTheFile()
    {
        var ttf = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var pdf = TestPdf.Simple(1, null);
        var file = pdf.Add(TestPdf.FlateStream(ttf, TestPdf.Dict(("Length1", PdfInteger.Of(ttf.Length)))));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("AAAAAA+LiberationSans")),
            ("Encoding", TestPdf.N("WinAnsiEncoding")), ("FirstChar", PdfInteger.Of(32)), ("LastChar", PdfInteger.Of(122)),
            ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("AAAAAA+LiberationSans")), ("Flags", PdfInteger.Of(32)),
                ("FontFile2", TestPdf.Ref(file))))));
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 12 Tf 72 700 Td (Herr ) Tj /TT 12 Tf (Zygmunt) Tj /F1 12 Tf ( sagt heute) Tj ET"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)), ("TT", TestPdf.Ref(font))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var saved = WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Zygmunt")));
        using var after = PdfDocument.Open(saved);
        // the name may stay in the resources, standing for a bare font (the operators stay valid) — without program, name or widths that say
        // anything (the Type 3 stand-in has to have widths: a single zero; No. 674)
        var tt = after.Pages[0].Resources?.GetDictionary(PdfName.Get("Font"))?.GetDictionary(PdfName.Get("TT"));
        var widths = tt?.GetArray(PdfName.Get("Widths"));
        Assert.True(tt is null || !tt.ContainsKey(PdfName.Get("FontDescriptor")) &&
                    (widths is null || Enumerable.Range(0, widths.Count).All(i => widths.Get(i) is PdfInteger { Value: 0 })), "die Schrift hat noch Programm oder Breiten");
        Assert.True(saved.AsSpan().IndexOf("LiberationSans"u8) < 0, "die Schrift steht noch in der Datei");
        Assert.Contains("sagt heute", Core.Pdf.Text.PageText.Create(PageInterpreter.Build(after, after.Pages[0]).Text).Text);
    }

    /// <summary>
    /// Edited text and a redaction in the same embedded font (KONZEPT.md, sections 5.14 and 5.6): the font that lost characters keeps what
    /// the file still shows — also what the edited paragraph shows, which is written as a form of its own (a paragraph set anew when
    /// redacting relies on the same).
    /// </summary>
    [Fact]
    public void EditedTextAndRedactionInOneFont_EditedTextKeepsItsLetters()
    {
        var ttf = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var pdf = TestPdf.Simple(1, null);
        var file = pdf.Add(TestPdf.FlateStream(ttf, TestPdf.Dict(("Length1", PdfInteger.Of(ttf.Length)))));
        var widths = new PdfArray();
        var metrics = Core.Fonts.OpenTypeFont.Parse(ttf);
        for (var c = 32; c <= 122; c++) widths.Add(PdfInteger.Of((int)Math.Round(metrics.AdvanceWidth(metrics.GlyphForUnicode(c)) * 1000.0 / metrics.UnitsPerEm)));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("AAAAAA+LiberationSans")),
            ("Encoding", TestPdf.N("WinAnsiEncoding")), ("FirstChar", PdfInteger.Of(32)), ("LastChar", PdfInteger.Of(122)), ("Widths", widths),
            ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("AAAAAA+LiberationSans")), ("Flags", PdfInteger.Of(32)),
                ("FontBBox", TestPdf.Box(-200, -300, 1200, 1000)), ("Ascent", PdfInteger.Of(905)), ("Descent", PdfInteger.Of(-212)), ("CapHeight", PdfInteger.Of(700)),
                ("ItalicAngle", PdfInteger.Of(0)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file))))));
        pdf.Replace(4, TestPdf.FlateStream("BT /TT 12 Tf 72 700 Td (Herr Mustermann sagt) Tj ET BT /TT 12 Tf 72 600 Td (Zebra quakt) Tj ET"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("TT", TestPdf.Ref(font))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var block = Core.Pdf.Text.TextBlocks.Find(doc, doc.Pages[0]).Single(b => b.Text.StartsWith("Zebra", StringComparison.Ordinal));
        var edited = EditedText.FromBlock(doc, block);
        var removed = Redactor.RemoveGlyphs(doc, doc.Pages[0], block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
        var items = PageItem.From(doc);
        items[0] = items[0].WithEditedPage(removed, [edited, new RedactionElement { Rect = AreaOf(doc, doc.Pages[0], "Mustermann") }]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, CompressStreams = false });
        using var after = PdfDocument.Open(output.ToArray());
        var text = Core.Pdf.Text.PageText.Create(PageInterpreter.Build(after, after.Pages[0]).Text).Text;
        Assert.Contains("Zebra quakt", text);
        Assert.DoesNotContain("Mustermann", text);
        // drawn: every letter of the edited paragraph has a shape
        Assert.True(Dark(after, 0) > Dark(doc, 0) / 2, "der bearbeitete Absatz ist zu hell — Buchstaben fehlen");
    }

    /// <summary>
    /// A tagged page whose widget annotation is referenced from the structure (/OBJR): the bar over the widget takes the annotation, and
    /// the object reference goes with it — it pointed at nothing (ISO 32000-2, 14.7.5.3: /Obj is required; docs/FEHLERGEDAECHTNIS.md,
    /// No. 363; found with the reference model of ISO 32000-2).
    /// </summary>
    [Fact]
    public void RedactedWidgetReferencedFromTheStructure_ObjectReferenceGoes()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 7 0 R/MarkInfo<</Marked true>>>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 612 792]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Annots[5 0 R]/StructParents 0>>",
            [4] = ("", "0 0 1 rg 300 300 50 50 re f"),
            [5] = "<</Type/Annot/Subtype/Widget/FT/Tx/T(Name)/V(Max)/Rect[100 100 200 120]/P 3 0 R/StructParent 1/DA(/Helv 10 Tf 0 g)/AP<</N 10 0 R>>>>",
            [6] = "<</Nums[0[] 1 8 0 R]>>",
            [7] = "<</Type/StructTreeRoot/K[8 0 R 9 0 R]/ParentTree 6 0 R>>",
            [8] = "<</Type/StructElem/S/Form/P 7 0 R/Pg 3 0 R/K<</Type/OBJR/Obj 5 0 R/Pg 3 0 R>>>>",
            [9] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0>>",
            [10] = ("/Type/XObject/Subtype/Form/BBox[0 0 100 20]", "0 0 1 rg 0 0 100 20 re f"),
        });
        using var doc = PdfDocument.Open(file);
        var saved = WithBars(doc, (0, new PdfRectangle(90, 90, 210, 130)));
        using var after = PdfDocument.Open(saved);
        static bool Nothing(PdfObject? o) => o is null or PdfNull;
        for (var n = 1; n <= after.HighestObjectNumber; n++)
        {
            if (!after.HasObject(n) || after.GetObject(n) is not PdfDictionary dict) continue;
            Assert.False(dict.GetName(PdfName.Type)?.Value == "OBJR" && Nothing(dict.Get(PdfName.Get("Obj"))), $"OBJR ohne Ziel in Objekt {n}");
            if (dict.Get(PdfName.Get("K")) is PdfDictionary k)
                Assert.False(k.GetName(PdfName.Type)?.Value == "OBJR" && Nothing(k.Get(PdfName.Get("Obj"))), $"OBJR ohne Ziel in Objekt {n}");
            // the parent tree names something for every number it lists
            if (dict.GetArray(PdfName.Get("Nums")) is { } nums)
                for (var i = 1; i < nums.Count; i += 2) Assert.False(Nothing(nums.Get(i)), $"Eintrag {nums.Get(i - 1)} des Elternbaums ohne Ziel");
        }
        var annots = after.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"));
        Assert.True(annots is null || annots.Count == 0, $"Anmerkungen: {annots?.Count} — {string.Join(", ", Enumerable.Range(0, annots?.Count ?? 0).Select(i => annots!.Get(i)?.ToString()))}");
    }

    /// <summary>
    /// The only structure element of the page refers to the widget a bar takes: the element goes — and with it, nothing tagged being left, the
    /// structure tree, /MarkInfo and the /StructParents of the page (docs/FEHLERGEDAECHTNIS.md, No. 626). Before 523 the root's /K stayed, the
    /// reference to the dropped element written as null (found with the reference model of ISO 32000-2 on a public test file); from 523 to 626
    /// a root without /K stayed, and the file still claimed to be tagged.
    /// </summary>
    [Fact]
    public void OnlyStructureElementRedacted_NoStructureLeft()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 7 0 R/MarkInfo<</Marked true>>>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 612 792]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Annots[5 0 R]/StructParents 0>>",
            [4] = ("", "0 0 1 rg 300 300 50 50 re f"),
            [5] = "<</Type/Annot/Subtype/Widget/FT/Tx/T(Name)/V(Max)/Rect[100 100 200 120]/P 3 0 R/StructParent 1/DA(/Helv 10 Tf 0 g)/AP<</N 10 0 R>>>>",
            [6] = "<</Nums[1 8 0 R]>>",
            [7] = "<</Type/StructTreeRoot/K[8 0 R]/ParentTree 6 0 R>>",
            [8] = "<</Type/StructElem/S/Form/P 7 0 R/Pg 3 0 R/K<</Type/OBJR/Obj 5 0 R/Pg 3 0 R>>>>",
            [10] = ("/Type/XObject/Subtype/Form/BBox[0 0 100 20]", "0 0 1 rg 0 0 100 20 re f"),
        });
        using var doc = PdfDocument.Open(file);
        var saved = WithBars(doc, (0, new PdfRectangle(90, 90, 210, 130)));
        using var after = PdfDocument.Open(saved);
        Assert.False(after.Catalog!.ContainsKey(PdfName.Get("StructTreeRoot")), $"Strukturbaum: {after.Catalog.GetRaw(PdfName.Get("StructTreeRoot"))}");
        Assert.False(after.Catalog.ContainsKey(PdfName.Get("MarkInfo")));
        Assert.False(after.Pages[0].Dictionary.ContainsKey(PdfName.Get("StructParents")));
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }

    /// <summary>
    /// The redacted image has an unused copy in the resources (another name, never drawn): after redacting, no unblacked picture is left in the
    /// file — the copy survived when „Was niemand sieht“ was not chosen (docs/FEHLERGEDAECHTNIS.md, No. 350).
    /// </summary>
    [Fact]
    public void UnusedCopyOfARedactedImage_LeavesTheFile()
    {
        var pdf = ImageAndFormPdf();
        var pixels = new byte[20 * 10 * 3];
        for (var i = 0; i < 200; i++) (pixels[i * 3], pixels[i * 3 + 1], pixels[i * 3 + 2]) = ((byte)(i % 20 * 12), 200, 40);
        var copy = pdf.Add(TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(20)), ("Height", PdfInteger.Of(10)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)))));
        var xobjects = ((PdfDictionary)pdf.Get(5)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!;
        xobjects.Set(PdfName.Get("Spare"), TestPdf.Ref(copy));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(90, 290, 310, 410))));
        var images = Enumerable.Range(1, saved.HighestObjectNumber).Where(saved.HasObject).Select(saved.GetObject).OfType<PdfStream>()
            .Where(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image").ToList();
        // every image left is black: none still shows the stripes
        Assert.All(images, image => Assert.All(saved.GetStreamData(image), b => Assert.True(b == 0, "ungeschwärztes Bild in der Datei")));
    }

    /// <summary>
    /// The same image drawn a thousand times at the same place under a bar: one blacked-out copy in the file — every drawing made its own
    /// copy, a crafted file took seconds and the result grew with every repetition (docs/FEHLERGEDAECHTNIS.md, No. 164).
    /// </summary>
    [Fact]
    public void ImageDrawnOftenAtOnePlace_BlankedOnce()
    {
        var pdf = ImageAndFormPdf();
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("q 200 0 0 100 100 300 cm " + string.Concat(Enumerable.Repeat("/Im0 Do ", 1000)) + "Q")));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(100, 300, 200, 400))));
        var images = Enumerable.Range(1, saved.ObjectCount + 8).Select(saved.GetObject).OfType<PdfStream>()
            .Where(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image").ToList();
        var data = saved.GetStreamData(Assert.Single(images));
        Assert.Equal((0, 0, 0), (data[0], data[1], data[2]));
    }

    /// <summary>
    /// A coarse image (2 × 1 pixels over 200 × 100 pt, also interpolated) half under a small bar: it goes black under the bar only, beside
    /// it the page looks as before — the whole touched image pixel went black, a hundred points beyond the bar (docs/FEHLERGEDAECHTNIS.md,
    /// No. 170).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoarseImage_BlackOnlyUnderTheBar(bool interpolate)
    {
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(TestPdf.FlateStream([220, 40, 40, 40, 200, 60], TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(1)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)),
            ("Interpolate", interpolate ? PdfBoolean.True : PdfBoolean.False))));
        pdf.Replace(4, TestPdf.FlateStream("q 200 0 0 100 100 300 cm /Im0 Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var bar = new PdfRectangle(120, 320, 150, 350);
        using var saved = PdfDocument.Open(WithBars(doc, (0, bar)));
        using var before = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new Rendering.ImageCache());
        using var after = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0]), saved, saved.Pages[0], 1, new Rendering.ImageCache());
        var (changed, top) = (0, doc.Pages[0].MediaBox.Top);
        for (var y = 0; y < before.Height; y++)
            for (var x = 0; x < before.Width; x++)
            {
                // a point of margin around the bar (anti-aliasing at its edge)
                if (x >= bar.Left - 1 && x <= bar.Right + 1 && top - y >= bar.Bottom - 1 && top - y <= bar.Top + 1) continue;
                var (a, b) = (before.GetPixel(x, y), after.GetPixel(x, y));
                if (Math.Abs(a.Red - b.Red) > 24 || Math.Abs(a.Green - b.Green) > 24 || Math.Abs(a.Blue - b.Blue) > 24) changed++;
            }
        Assert.Equal(0, changed);
        // the image itself is black under the bar
        var copy = Enumerable.Range(1, saved.ObjectCount + 8).Select(saved.GetObject).OfType<PdfStream>()
            .Single(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image");
        var (w, h) = ((int)copy.Dictionary.GetInteger(PdfName.Get("Width"))!, (int)copy.Dictionary.GetInteger(PdfName.Get("Height"))!);
        var pixels = saved.GetStreamData(copy);
        var (cx, cy) = ((int)((135 - 100) / 200.0 * w), (int)((1 - (335 - 300) / 100.0) * h));
        Assert.Equal((0, 0, 0), (pixels[(cy * w + cx) * 3], pixels[(cy * w + cx) * 3 + 1], pixels[(cy * w + cx) * 3 + 2]));
    }

    /// <summary>
    /// A Flate image whose zlib header is damaged: before, the display read it from byte 2 on and the blacking out in its own samples read zeros —
    /// the image half under a bar came back black beside it as well; the result is now read through the display's decoder and compared with the
    /// original (docs/FEHLERGEDAECHTNIS.md, No. 431). Since No. 937 a header RFC 1950 does not allow gives no data at all, and every reader
    /// takes the header by the same rule; the damaged header that is still read — a window above 32 KB, CINFO 8 — stands in for it.
    /// </summary>
    [Fact]
    public void ImageTheBlankerReadsOtherwise_GoesTheWayOfTheDisplay()
    {
        var samples = new byte[16 * 16];
        for (var i = 0; i < samples.Length; i++) samples[i] = (byte)(60 + i % 16 * 10);
        var zlib = Core.Pdf.Filters.Flate.Encode(samples);
        // CINFO 8 with a right FCHECK: read, and the stream counts as damaged (No. 937)
        zlib[0] = 0x88;
        zlib[1] = (byte)((zlib[1] & 0xE0) + (31 - (0x88 * 256 + (zlib[1] & 0xE0)) % 31) % 31);
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(16)),
            ("Height", PdfInteger.Of(16)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N("FlateDecode"))), zlib));
        pdf.Replace(4, TestPdf.FlateStream("q 160 0 0 160 100 300 cm /Im0 Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var bar = new PdfRectangle(100, 300, 170, 460);
        using var saved = PdfDocument.Open(WithBars(doc, (0, bar)));
        using var before = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new Rendering.ImageCache());
        using var after = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0]), saved, saved.Pages[0], 1, new Rendering.ImageCache());
        var top = doc.Pages[0].MediaBox.Top;
        Assert.True(before.GetPixel(240, (int)(top - 380)).Red > 100, "das Bild fehlt schon vorher");
        var changed = 0;
        for (var y = (int)(top - 455); y < top - 305; y++)
            for (var x = 185; x < 255; x++)
                if (Math.Abs(before.GetPixel(x, y).Red - after.GetPixel(x, y).Red) > 24) changed++;
        Assert.Equal(0, changed);
    }

    /// <summary>
    /// Files associated with the page, a form and an image under the bar (/AF, ISO 32000-2 14.13: the data of a chart, the original of a
    /// picture) and the page's own XMP metadata leave the redacted copy — they stayed and could hold the redacted content in another form
    /// (docs/FEHLERGEDAECHTNIS.md, No. 434).
    /// </summary>
    [Fact]
    public void AssociatedFilesAndMetadataOfRedactedContent_Leave()
    {
        var pdf = TestPdf.Simple(1);
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), "Geheim 4711"u8.ToArray()));
        var spec = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", new PdfString("daten.csv"u8.ToArray())), ("AFRelationship", TestPdf.N("Data")),
            ("EF", TestPdf.Dict(("F", TestPdf.Ref(file))))));
        var af = new PdfArray([TestPdf.Ref(spec)]);
        var form = pdf.Add(TestPdf.FlateStream("BT /F1 20 Tf 300 650 Td (Geheim) Tj ET"u8.ToArray(), TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 612, 792)), ("AF", af),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))))))));
        var image = pdf.Add(TestPdf.FlateStream([220, 40, 40, 40, 200, 60], TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(1)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)), ("AF", af))));
        var xmp = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))), "<x:xmpmeta>Geheim</x:xmpmeta>"u8.ToArray()));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Get("AF"), af);
        page.Set(PdfName.Get("Metadata"), TestPdf.Ref(xmp));
        page.Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Fm0", TestPdf.Ref(form)), ("Im0", TestPdf.Ref(image)))), ("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))));
        pdf.Replace(4, TestPdf.FlateStream("q 100 0 0 50 100 600 cm /Im0 Do Q /Fm0 Do"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(90, 590, 420, 680))));
        var written = after.Pages[0].Dictionary;
        Assert.False(written.ContainsKey(PdfName.Get("AF")));
        Assert.False(written.ContainsKey(PdfName.Get("Metadata")));
        var xobjects = after.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!;
        foreach (var (name, _) in xobjects.Entries)
            Assert.False(xobjects.GetStream(name)!.Dictionary.ContainsKey(PdfName.Get("AF")), name.Value);
        Assert.DoesNotContain("Geheim", Everything(after));
    }

    /// <summary>A file specification with an embedded file „Geheim 4711“ and an XMP stream „Geheim“ (No. 444).</summary>
    private static (int Spec, int Xmp) DescribingData(TestPdf pdf, string text = "Geheim")
    {
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), System.Text.Encoding.ASCII.GetBytes(text + " 4711")));
        var spec = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", new PdfString("quelle.tex"u8.ToArray())), ("AFRelationship", TestPdf.N("Source")),
            ("EF", TestPdf.Dict(("F", TestPdf.Ref(file))))));
        var xmp = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))),
            System.Text.Encoding.ASCII.GetBytes($"<x:xmpmeta>{text}</x:xmpmeta>")));
        return (spec, xmp);
    }

    /// <summary>
    /// Marked content under the bar with files associated through its property list („/AF /MC0 BDC“, ISO 32000-2 14.13.5) and with XMP
    /// in a named property list (14.3.2): both leave the redacted copy; „/AF /MC0 BDC“ stays paired with an empty array
    /// (docs/FEHLERGEDAECHTNIS.md, No. 444, Analyse K-04).
    /// </summary>
    [Fact]
    public void MarkedContentAssociatedFileAndMetadata_UnderABar_Leave()
    {
        var pdf = TestPdf.Simple(1);
        var (spec, xmp) = DescribingData(pdf);
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))),
            ("Properties", TestPdf.Dict(("MC0", new PdfArray([TestPdf.Ref(spec)])), ("MC1", TestPdf.Dict(("MCID", PdfInteger.Of(0)), ("Metadata", TestPdf.Ref(xmp))))))));
        pdf.Replace(4, TestPdf.FlateStream("/AF /MC0 BDC BT /F1 20 Tf 100 600 Td (Geheim) Tj ET EMC /Span /MC1 BDC BT /F1 20 Tf 100 500 Td (Geheim) Tj ET EMC"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(90, 490, 260, 630))));
        Assert.DoesNotContain("Geheim", Everything(after));
        var properties = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Properties"))!;
        Assert.Empty(properties.GetArray(PdfName.Get("MC0"))!);
        Assert.Equal(0, properties.GetDictionary(PdfName.Get("MC1"))!.GetInteger(PdfName.Get("MCID")));
    }

    /// <summary>
    /// A formula on a redacted page with its LaTeX source associated (/AF, ISO 32000-2 14.13.6) and its own XMP: the structure element
    /// loses both (docs/FEHLERGEDAECHTNIS.md, No. 444, Analyse K-05).
    /// </summary>
    [Fact]
    public void RedactedPage_StructureElementLosesMetadataAndAssociatedFiles()
    {
        var pdf = TestPdf.Simple(1);
        var (spec, xmp) = DescribingData(pdf);
        var root = pdf.Add(new PdfDictionary());
        var formula = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Formula")), ("P", TestPdf.Ref(root)), ("Pg", TestPdf.Ref(5)),
            ("K", PdfInteger.Of(0)), ("AF", new PdfArray([TestPdf.Ref(spec)])), ("Metadata", TestPdf.Ref(xmp))));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", new PdfArray([TestPdf.Ref(formula)]))));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("StructTreeRoot"), TestPdf.Ref(root));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("MarkInfo"), TestPdf.Dict(("Marked", PdfBoolean.True)));
        pdf.Replace(4, TestPdf.FlateStream("/Formula <</MCID 0>> BDC BT /F1 20 Tf 100 600 Td (Geheim) Tj ET EMC"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(90, 590, 260, 630))));
        Assert.DoesNotContain("Geheim", Everything(after));
        var tree = after.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        var element = (PdfDictionary)tree.GetArray(PdfName.Get("K"))!.Get(0)!;
        Assert.Equal("Formula", element.GetName(PdfName.Get("S"))!.Value);
        Assert.False(element.ContainsKey(PdfName.Get("AF")));
        Assert.False(element.ContainsKey(PdfName.Get("Metadata")));
        // the formula keeps a neutral description (no document language: in English, marked as such; 6.3 step 4, No. 617)
        Assert.Equal("Redacted content", element.GetString(PdfName.Get("Alt"))?.ToText());
        Assert.Equal("en", element.GetString(PdfName.Get("Lang"))?.ToText());
    }

    /// <summary>
    /// The redacted copy of a pattern cell (No. 163) with XMP and an associated file at the pattern: both leave (docs/FEHLERGEDAECHTNIS.md,
    /// No. 444, Analyse K-06).
    /// </summary>
    [Fact]
    public void PatternCellCopy_LosesMetadataAndAssociatedFiles()
    {
        var pdf = TestPdf.Simple(1);
        var (spec, xmp) = DescribingData(pdf, "Geheimnis");
        var font = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.GetRaw(PdfName.Get("F1"))!;
        var cell = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Pattern")), ("PatternType", PdfInteger.Of(1)), ("PaintType", PdfInteger.Of(1)), ("TilingType", PdfInteger.Of(1)),
            ("BBox", TestPdf.Box(0, 0, 600, 840)), ("XStep", PdfInteger.Of(600)), ("YStep", PdfInteger.Of(840)), ("Metadata", TestPdf.Ref(xmp)), ("AF", new PdfArray([TestPdf.Ref(spec)])),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", font)))))), "BT /F1 20 Tf 100 500 Td (Geheimnis Bleibt) Tj ET"u8.ToArray());
        var pattern = pdf.Add(cell);
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("Pattern"), TestPdf.Dict(("P1", TestPdf.Ref(pattern))));
        pdf.Replace(4, TestPdf.FlateStream("q /Pattern cs /P1 scn 90 490 220 30 re f Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var data = WithBars(doc, (0, new PdfRectangle(95, 492, 190, 518)));
        using var after = PdfDocument.Open(data);
        Assert.DoesNotContain("Geheimnis", Everything(after));
        Assert.Contains("Bleibt", Everything(after));
    }

    /// <summary>
    /// An uncoloured pattern with text, emptied for the page: without its XMP, its associated file and its old resources
    /// (docs/FEHLERGEDAECHTNIS.md, No. 444, Analyse K-06).
    /// </summary>
    [Fact]
    public void BlankedPattern_LosesMetadataAndAssociatedFiles()
    {
        var pdf = TestPdf.Simple(1);
        var (spec, xmp) = DescribingData(pdf, "Geheimnis");
        var font = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.GetRaw(PdfName.Get("F1"))!;
        var cell = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Pattern")), ("PatternType", PdfInteger.Of(1)), ("PaintType", PdfInteger.Of(2)), ("TilingType", PdfInteger.Of(1)),
            ("BBox", TestPdf.Box(0, 0, 600, 840)), ("XStep", PdfInteger.Of(600)), ("YStep", PdfInteger.Of(840)), ("Metadata", TestPdf.Ref(xmp)), ("AF", new PdfArray([TestPdf.Ref(spec)])),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", font)))))), "BT /F1 20 Tf 100 500 Td (Geheimnis Bleibt) Tj ET"u8.ToArray());
        var pattern = pdf.Add(cell);
        var resources = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!;
        resources.Set(PdfName.Get("Pattern"), TestPdf.Dict(("P1", TestPdf.Ref(pattern))));
        resources.Set(PdfName.Get("ColorSpace"), TestPdf.Dict(("CS0", new PdfArray([TestPdf.N("Pattern"), TestPdf.N("DeviceRGB")]))));
        pdf.Replace(4, TestPdf.FlateStream("q /CS0 cs 0 0 0 /P1 scn 90 490 220 30 re f Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(95, 492, 190, 518))));
        Assert.DoesNotContain("Geheimnis", Everything(after));
        var patterns = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Pattern"))!;
        foreach (var (name, _) in patterns.Entries)
        {
            var written = patterns.GetStream(name)!.Dictionary;
            Assert.False(written.ContainsKey(PdfName.Get("AF")), name.Value);
            Assert.False(written.ContainsKey(PdfName.Get("Metadata")), name.Value);
        }
    }

    /// <summary>
    /// A 1-bit image whose rows do not end on a byte, a bar from inside one byte to inside another: exactly the samples the bar touches go
    /// black, the bits beside them in the same bytes stay — whole bytes are set at once, the ends sample by sample (a mask of 151 megapixels
    /// under a wide bar took seconds sample by sample).
    /// </summary>
    [Fact]
    public void OneBitImage_BlankedBitExactly()
    {
        const int W = 37, H = 9, Stride = (W + 7) / 8;
        var bits = new byte[Stride * H];
        for (var i = 0; i < bits.Length; i++) bits[i] = 0xFF;   // all white
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(TestPdf.FlateStream(bits, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(W)), ("Height", PdfInteger.Of(H)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(1)))));
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes($"q {W} 0 0 {H} 100 300 cm /Im0 Do Q")));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(103.5, 302.5, 130.5, 306.5))));
        var copy = Enumerable.Range(1, saved.ObjectCount + 8).Select(saved.GetObject).OfType<PdfStream>()
            .Single(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image");
        Assert.Equal((W, 1), ((int)copy.Dictionary.GetInteger(PdfName.Get("Width"))!, (int)copy.Dictionary.GetInteger(PdfName.Get("BitsPerComponent"))!));
        var data = saved.GetStreamData(copy);
        // touched: columns 3 … 30, rows 2 … 6 from the top (y 302.5 … 306.5 of 300 … 309)
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var bit = data[y * Stride + x / 8] >> (7 - x % 8) & 1;
                Assert.True(bit == (x is >= 3 and <= 30 && y is >= 2 and <= 6 ? 0 : 1), $"({x}, {y})");
            }
    }

    /// <summary>
    /// The same as an inline image (ASCIIHex, abbreviated keys): blacked out in its own samples, written back as an XObject with 1 bit per
    /// sample and exactly the touched bits black — before, it went the way of the display and came back as 8-bit RGB
    /// (docs/FEHLERGEDAECHTNIS.md, No. 279).
    /// </summary>
    [Fact]
    public void InlineOneBitImage_BlankedInItsOwnSamples()
    {
        const int W = 37, H = 9, Stride = (W + 7) / 8;
        var hex = string.Concat(Enumerable.Repeat("FF", Stride * H)) + ">";
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes($"q {W} 0 0 {H} 100 300 cm BI /W {W} /H {H} /CS /G /BPC 1 /F /AHx ID {hex} EI Q")));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(103.5, 302.5, 130.5, 306.5))));
        var copy = Enumerable.Range(1, saved.ObjectCount + 8).Select(saved.GetObject).OfType<PdfStream>()
            .Single(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image");
        Assert.Equal((W, 1), ((int)copy.Dictionary.GetInteger(PdfName.Get("Width"))!, (int)copy.Dictionary.GetInteger(PdfName.Get("BitsPerComponent"))!));
        var data = saved.GetStreamData(copy);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var bit = data[y * Stride + x / 8] >> (7 - x % 8) & 1;
                Assert.True(bit == (x is >= 3 and <= 30 && y is >= 2 and <= 6 ? 0 : 1), $"({x}, {y})");
            }
    }

    /// <summary>
    /// An inline image mask that also names a colour space (readers ignore it there), partly under a bar: written back as an XObject without
    /// /ColorSpace — an image mask shall have none (ISO 32000-2, 8.9.6.2; docs/FEHLERGEDAECHTNIS.md, No. 374).
    /// </summary>
    [Fact]
    public void InlineImageMaskWithAColourSpace_WrittenWithout()
    {
        const int W = 37, H = 9, Stride = (W + 7) / 8;
        var hex = string.Concat(Enumerable.Repeat("00", Stride * H)) + ">";
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes($"q 0 0 1 rg {W} 0 0 {H} 100 300 cm BI /W {W} /H {H} /IM true /CS /G /BPC 1 /F /AHx ID {hex} EI Q")));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(103.5, 302.5, 130.5, 306.5))));
        var copy = Enumerable.Range(1, saved.ObjectCount + 8).Select(saved.GetObject).OfType<PdfStream>()
            .Single(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image");
        Assert.Equal(true, copy.Dictionary.GetBoolean(PdfName.Get("ImageMask")));
        Assert.False(copy.Dictionary.ContainsKey(PdfName.Get("ColorSpace")));
    }

    /// <summary>
    /// A coarse inline image with a predictor and abbreviations (/CS /RGB, /F /Fl, /DP with /Columns 4), partly under a bar: written back as an
    /// XObject only with full keys and values — the kept /DP made a reader unpack the refined data with the old predictor, the image beside
    /// the bar turned to stripes (docs/FEHLERGEDAECHTNIS.md, No. 289). Also a colour space named in the resources becomes the space itself.
    /// </summary>
    [Theory]
    [InlineData("/RGB")]
    [InlineData("/Farbe")]
    public void InlineImageWithPredictor_AbbreviationsGone_RestKeepsItsColours(string space)
    {
        // four pixels red, green, blue, blue; predictor rows (filter byte 0) packed with Flate
        byte[] row = [0, 220, 30, 30, 30, 200, 30, 30, 30, 220, 30, 30, 220];
        using var packed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(row);
        var hex = Convert.ToHexString(packed.ToArray()) + ">";
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(
            $"q 200 0 0 20 100 300 cm BI /W 4 /H 1 /CS {space} /BPC 8 /F [/AHx /Fl] /DP [null << /Predictor 15 /Columns 4 /Colors 3 >>] ID {hex} EI Q")));
        var resources = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!;
        resources.Set(PdfName.Get("ColorSpace"), TestPdf.Dict(("Farbe", TestPdf.N("DeviceRGB"))));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(110, 295, 140, 325))));
        var copy = Enumerable.Range(1, saved.ObjectCount + 8).Select(saved.GetObject).OfType<PdfStream>()
            .Single(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image");
        foreach (var abbreviation in (string[])["W", "H", "CS", "BPC", "F", "DP", "D", "IM", "I"])
            Assert.False(copy.Dictionary.ContainsKey(PdfName.Get(abbreviation)), abbreviation);
        Assert.Equal("DeviceRGB", copy.Dictionary.GetName(PdfName.Get("ColorSpace"))?.Value);
        using var bitmap = ContentImageTests.Render(saved, saved.Pages[0]);
        var green = ContentImageTests.At(bitmap, saved.Pages[0], 175, 310);
        var blue = ContentImageTests.At(bitmap, saved.Pages[0], 275, 310);
        Assert.True(green.Green > 150 && green.Red < 80, $"grün: {green}");
        Assert.True(blue.Blue > 150 && blue.Red < 80, $"blau: {blue}");
    }

    /// <summary>
    /// An image with a colour space that names no ranges for its samples (/Pattern, as a damaged file had it): blacking it out under a bar
    /// does not break off saving — an IndexOutOfRangeException did (found by fuzzing, docs/FEHLERGEDAECHTNIS.md, No. 186).
    /// </summary>
    [Fact]
    public void ImageWithoutRanges_BlankedWithoutFailing()
    {
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(TestPdf.FlateStream(new byte[50 * 20], TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(50)), ("Height", PdfInteger.Of(20)), ("ColorSpace", TestPdf.N("Pattern")), ("BitsPerComponent", PdfInteger.Of(8)))));
        pdf.Replace(4, TestPdf.FlateStream("q 50 0 0 20 100 300 cm /Im0 Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(110, 305, 130, 315))));
        Assert.Equal(1, saved.PageCount);
    }

    /// <summary>
    /// A coarse CMYK image with /Interpolate (pixels of 15 pt) under a bar: blacked out in its own samples, refined and smoothed like a reader
    /// enlarging it — it stays CMYK. The check of the copy compared each pixel with the nearest pixel of the original only, found half of them
    /// different and sent the image the display's way, which writes RGB (docs/FEHLERGEDAECHTNIS.md, No. 809).
    /// </summary>
    [Fact]
    public void CoarseInterpolatedCmykImage_StaysCmyk()
    {
        var pdf = TestPdf.Simple(1);
        var pixels = new byte[40 * 30 * 4];
        new Random(7).NextBytes(pixels);
        var image = pdf.Add(TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(40)), ("Height", PdfInteger.Of(30)), ("ColorSpace", TestPdf.N("DeviceCMYK")), ("BitsPerComponent", PdfInteger.Of(8)),
            ("Interpolate", PdfBoolean.True))));
        pdf.Replace(4, TestPdf.FlateStream("q 600 0 0 450 0 100 cm /Im0 Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(100, 200, 160, 240))));
        var copy = Enumerable.Range(1, saved.ObjectCount + 8).Select(saved.GetObject).OfType<PdfStream>()
            .Single(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image");
        Assert.Equal("DeviceCMYK", copy.Dictionary.GetName(PdfName.Get("ColorSpace"))?.Value);
        Assert.True(copy.Dictionary.GetInteger(PdfName.Get("Width")) > 40);
    }

    /// <summary>
    /// A fine image (pixels of 0.8 pt) under a bar keeps its pixels: refined, a reader averages it instead of interpolating it and it looks
    /// slightly different everywhere; the black reaches at most one such pixel beyond the bar (docs/FEHLERGEDAECHTNIS.md, No. 177).
    /// </summary>
    [Fact]
    public void FineImage_NotRefined()
    {
        var pdf = TestPdf.Simple(1);
        var pixels = new byte[100 * 100 * 3];
        new Random(5).NextBytes(pixels);
        var image = pdf.Add(TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(100)), ("Height", PdfInteger.Of(100)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)))));
        pdf.Replace(4, TestPdf.FlateStream("q 80 0 0 80 100 300 cm /Im0 Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(120, 320, 140.3, 340.5))));
        var copy = Enumerable.Range(1, saved.ObjectCount + 8).Select(saved.GetObject).OfType<PdfStream>()
            .Single(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image");
        Assert.Equal((100, 100), ((int)copy.Dictionary.GetInteger(PdfName.Get("Width"))!, (int)copy.Dictionary.GetInteger(PdfName.Get("Height"))!));
        // the pixels the bar touches (x 25 … 50, rows 49 … 74 from the top) are black, the others as before
        var data = saved.GetStreamData(copy);
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
            {
                var at = (y * 100 + x) * 3;
                var touched = x >= 25 && x <= 50 && y >= 49 && y <= 74;
                Assert.Equal(touched ? (0, 0, 0) : (pixels[at], pixels[at + 1], pixels[at + 2]), (data[at], data[at + 1], data[at + 2]));
            }
    }

    /// <summary>
    /// A large indexed image (over 16 megapixels) under a bar: blacked out in its own samples — same size, colour space and palette,
    /// every sample beside the bar as before, under it the darkest entry of the palette. The display reduces such images while decoding;
    /// written back from there, the whole image lost resolution and became RGB (docs/FEHLERGEDAECHTNIS.md, No. 170).
    /// </summary>
    [Fact]
    public void LargeIndexedImage_KeepsItsSamplesAndResolution()
    {
        const int W = 4100, H = 4000;
        var samples = new byte[W * H];
        for (var i = 0; i < samples.Length; i++) samples[i] = (byte)(i % W * 2 / W);   // left half palette entry 0, right half entry 1
        var pdf = TestPdf.Simple(1);
        var palette = new PdfArray([TestPdf.N("Indexed"), TestPdf.N("DeviceRGB"), PdfInteger.Of(2), new PdfString([255, 255, 255, 128, 128, 128, 0, 0, 0])]);
        var image = pdf.Add(TestPdf.FlateStream(samples, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(W)), ("Height", PdfInteger.Of(H)), ("ColorSpace", palette), ("BitsPerComponent", PdfInteger.Of(8)))));
        pdf.Replace(4, TestPdf.FlateStream("q 410 0 0 400 100 300 cm /Im0 Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(200, 400, 300, 500))));
        var copy = Enumerable.Range(1, saved.ObjectCount + 8).Select(saved.GetObject).OfType<PdfStream>()
            .Single(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image");
        Assert.Equal(W, copy.Dictionary.GetInteger(PdfName.Get("Width")));
        Assert.Equal(H, copy.Dictionary.GetInteger(PdfName.Get("Height")));
        Assert.Equal("Indexed", (copy.Dictionary.Get(PdfName.Get("ColorSpace")) as PdfArray)?.Get(0) is PdfName { } n ? n.Value : null);
        var data = saved.GetStreamData(copy);
        // the bar covers x 1000 … 2000 and rows 2000 … 3000 from the top; one image pixel is 0.1 pt
        int outside = 0, inside = 0;
        for (var y = 0; y < H; y += 7)
            for (var x = 0; x < W; x += 7)
            {
                var under = x >= 1000 && x < 2000 && y >= 2000 && y < 3000;
                var border = x >= 995 && x < 2005 && y >= 1995 && y < 3005 && !under;
                if (border) continue;
                if (under ? data[y * W + x] != 2 : data[y * W + x] != samples[y * W + x])
                {
                    if (under) inside++;
                    else outside++;
                }
            }
        Assert.Equal((0, 0), (outside, inside));
    }

    /// <summary>
    /// A thin sickle of two curves (a slur in sheet music, filled even-odd), a narrow bar over its lower edge near one end: beside the bar
    /// the slur keeps its shape. Its upper curve does not touch the bar and stays as written; split also where it merely crossed the line
    /// of an edge of the bar, it was approximated differently and its edges moved by a fraction of a pixel along its whole length
    /// (docs/FEHLERGEDAECHTNIS.md, No. 179). The lower curve enters the bar and must be split, so a little smoothing may differ.
    /// </summary>
    [Fact]
    public void CurveCutNearItsEnd_KeepsItsShapeBeside()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("q 3 0 0 3 0 0 cm 1 0 0 1 86.108 129.572 cm 0 0 m 13.986 13.284 55.242 19.296 " +
            "72.594 10.44 c 72.522 10.296 l 55.404 18.252 14.13 12.222 0.108 -0.126 c h f* Q")));
        using var doc = PdfDocument.Open(pdf.Build());
        // the bar in the same space as the slur, scaled by 3: x 101.88 … 108.11, y 99.56 … 138.06 before scaling
        var bar = new PdfRectangle(3 * 101.88, 3 * 99.56, 3 * 108.109, 3 * 138.059);
        using var saved = PdfDocument.Open(WithBars(doc, (0, bar)));
        using var before = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new Rendering.ImageCache());
        using var after = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0]), saved, saved.Pages[0], 1, new Rendering.ImageCache());
        var (changed, top) = (0, doc.Pages[0].MediaBox.Top);
        for (var y = 0; y < before.Height; y++)
            for (var x = 0; x < before.Width; x++)
            {
                if (x >= bar.Left - 1 && x <= bar.Right + 1 && top - y >= bar.Bottom - 1 && top - y <= bar.Top + 1) continue;
                if (Math.Abs(before.GetPixel(x, y).Red - after.GetPixel(x, y).Red) > 24) changed++;
            }
        Assert.True(changed <= 30, $"{changed} Pixel");
        // the upper curve as written
        var content = System.Text.Encoding.ASCII.GetString(saved.GetStreamData((PdfStream)saved.Pages[0].Dictionary.Get(PdfName.Contents)!));
        Assert.Contains("13.986 13.284 55.242 19.296 72.594 10.44 c", content);
    }

    /// <summary>
    /// A half-transparent box filled and stroked in one operator (B), a bar across it: beside the bar its edge keeps its colour — cut into
    /// a fill and a stroke, the stroke blended with the fill beneath it, while with B it replaces it (docs/FEHLERGEDAECHTNIS.md, No. 176).
    /// </summary>
    [Fact]
    public void TransparentFillAndStroke_CutKeepsItsEdge()
    {
        var pdf = TestPdf.Simple(1);
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("ExtGState", TestPdf.Dict(("GS0", TestPdf.Dict(("ca", new PdfReal(0.5)), ("CA", new PdfReal(0.5))))))));
        pdf.Replace(4, TestPdf.FlateStream("q /GS0 gs 1 0 0 rg 0 0 1 RG 12 w 100 300 200 200 re B Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(170, 250, 230, 560))));
        using var before = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new Rendering.ImageCache());
        using var after = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0]), saved, saved.Pages[0], 1, new Rendering.ImageCache());
        var top = (int)doc.Pages[0].MediaBox.Top;
        // on the edges beside the bar (left and top), and inside the box
        foreach (var (x, y) in (ReadOnlySpan<(int, int)>)[(100, 400), (130, 500), (270, 500), (140, 400)])
        {
            var (a, b) = (before.GetPixel(x, top - y), after.GetPixel(x, top - y));
            Assert.True(Math.Abs(a.Red - b.Red) <= 2 && Math.Abs(a.Green - b.Green) <= 2 && Math.Abs(a.Blue - b.Blue) <= 2, $"({x}, {y}): {a} → {b}");
        }
    }

    /// <summary>
    /// The same box with its stroke parameters set before (width 12, round caps and joins, dashed): the knockout group the cut goes into
    /// states them itself, as the display uses them (docs/FEHLERGEDAECHTNIS.md, No. 410) — it relied on what a group inherits, and a
    /// reference reader drew the edge as a hairline.
    /// </summary>
    [Fact]
    public void TransparentFillAndStroke_GroupStatesTheStroke()
    {
        var pdf = TestPdf.Simple(1);
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("ExtGState", TestPdf.Dict(("GS0", TestPdf.Dict(("ca", new PdfReal(0.5)), ("CA", new PdfReal(0.5))))))));
        pdf.Replace(4, TestPdf.FlateStream("12 w 1 J 1 j 7 M [6 3] 1 d q /GS0 gs 1 0 0 rg 0 0 1 RG 100 300 200 200 re B Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(170, 250, 230, 560))));
        var group = saved.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Entries
            .Select(e => saved.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(e.Key)).OfType<PdfStream>()
            .Single(f => f.Dictionary.GetDictionary(PdfName.Get("Group")) is not null);
        var content = System.Text.Encoding.ASCII.GetString(saved.GetStreamData(group));
        Assert.Contains("12 w 1 J 1 j 7 M [6 3] 1 d", content);
    }

    /// <summary>
    /// A clip entirely under a bar, a large area filled through it (a shading in its own clip): beside the bar nothing is painted — the
    /// clip was cut to nothing, "W n" without a path clipped nothing, and the area covered the whole page (docs/FEHLERGEDAECHTNIS.md,
    /// No. 175).
    /// </summary>
    [Theory]
    [InlineData("0 0 1 rg 0 0 612 842 re f")]
    [InlineData("/Sh0 sh")]
    public void ClipEntirelyUnderTheBar_PaintsNothingBeside(string paint)
    {
        var pdf = TestPdf.Simple(1);
        var shading = TestPdf.Dict(("ShadingType", PdfInteger.Of(2)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Coords", TestPdf.Box(0, 0, 612, 0)),
            ("Function", TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(0, 0, 1)), ("C1", TestPdf.Box(0, 0, 1)),
                ("N", PdfInteger.Of(1)))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Shading", TestPdf.Dict(("Sh0", shading)))));
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes($"q 210 310 30 30 re W n {paint} Q")));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.True(Blue(doc) > 0);
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(200, 300, 250, 350))));
        Assert.Equal(0, Blue(saved));

        static int Blue(PdfDocument d)
        {
            using var b = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(d, d.Pages[0]), d, d.Pages[0], 1, new Rendering.ImageCache());
            var n = 0;
            for (var y = 0; y < b.Height; y++)
                for (var x = 0; x < b.Width; x++)
                    if (b.GetPixel(x, y) is var c && c.Blue > 200 && c.Red < 100) n++;
            return n;
        }
    }

    /// <summary>
    /// A red box under a white one, a bar across both: along the edges of the bar nothing red shows through — both were clipped exactly at
    /// the edge of the bar, and where its smoothed edge met the smoothed edge of the clip, the red showed as a fine line
    /// (docs/FEHLERGEDAECHTNIS.md, No. 172).
    /// </summary>
    [Fact]
    public void CutFills_NoSeamAlongTheBar()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("1 0 0 rg 100 500 200 50 re f 1 1 1 rg 90 490 300 80 re f"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(150.3, 480.3, 200.7, 520.6))));
        using var bitmap = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0]), saved, saved.Pages[0], 4, new Rendering.ImageCache());
        var red = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y) is var c && c.Red - c.Green > 24) red++;
        Assert.Equal(0, red);
    }

    /// <summary>
    /// A word covered by a white box except for a strip at its foot, a bar over just that strip: too little of each letter lies under the
    /// bar for it to go, but nobody sees the letters any more — they stayed in the file (docs/FEHLERGEDAECHTNIS.md, No. 172).
    /// </summary>
    [Fact]
    public void BarOverTheOnlyVisibleStrip_TheCoveredWordGoes()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 40 Tf 100 500 Td (HIDE) Tj ET 1 1 1 rg 90 504 200 60 re f 0 g BT /F1 12 Tf 72 700 Td (Sichtbar) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Contains("HIDE", Read(doc, doc.Pages[0]).Text);
        var data = WithBars(doc, (0, new PdfRectangle(95, 495, 210, 507)));
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(data, ["HIDE"], null, out _));
        using var after = PdfDocument.Open(data);
        Assert.Contains("Sichtbar", Read(after, after.Pages[0]).Text);
    }

    /// <summary>
    /// A slanted line whose first word lies under a white box, a bar that grazes it at the box's edge: cleaning up after the bars removes the
    /// letters nobody sees any more and leaves every other one exactly where it was — evening out the gap there moved the rest of the line
    /// (in a damaged file into a bar, with nothing checking it any more; docs/FEHLERGEDAECHTNIS.md, No. 377).
    /// </summary>
    [Fact]
    public void GrazedLettersOfASlantedLine_GoWithoutMovingTheRest()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 40 Tf 1.4 -0.7 0.7 0.7 100 500 Tm (HIDE STAYS here) Tj ET 1 1 1 rg 90 300 120 400 re f"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var bar = new PdfRectangle(205, 300, 215, 700);
        var redacted = Redactor.Apply(doc, doc.Pages[0], [bar]);
        var first = new PdfPage(0, redacted.Page, doc.Pages[0].Id, redacted.Resources, doc.Pages[0].MediaBox, doc.Pages[0].CropBox, doc.Pages[0].Rotate);
        var grazed = Redactor.RemoveHiddenIn(doc, first, [bar], grazing: true);
        Assert.NotNull(grazed);
        var before = PageInterpreter.Build(doc, first).Text.Where(g => g.Text.Trim().Length > 0).ToList();
        var after = PageInterpreter.Build(doc, new PdfPage(0, grazed.Page, null, grazed.Resources, doc.Pages[0].MediaBox, doc.Pages[0].CropBox, doc.Pages[0].Rotate))
            .Text.Where(g => g.Text.Trim().Length > 0).ToList();
        Assert.True(after.Count < before.Count);
        Assert.All(after, g => Assert.Contains(before, b => b.Text == g.Text && Math.Abs(b.X0 - g.X0) < 0.01 && Math.Abs(b.Y0 - g.Y0) < 0.01));
    }

    /// <summary>
    /// A character whose shape is empty (an outline without area, as a damaged font program gives it) under a bar over most of its cell but
    /// not its origin: it goes, judged by its cell as invisible text is — its empty shape counted as the point of its origin, which lay beside
    /// the bar, and its text stayed in the file (docs/FEHLERGEDAECHTNIS.md, No. 377; found by the redaction focus of the fuzzer). Upright and
    /// slanted; also the final check finds it.
    /// </summary>
    [Theory]
    [InlineData(20, 0, 0, 20)]
    [InlineData(18, -9, 9, 18)]
    public void CharacterWithAnEmptyShape_UnderABar_Goes(double a, double b, double c, double d)
    {
        var cellToPage = new Core.Pdf.Graphics.Matrix(a, b, c, d, 100, 500);
        var cell = Core.Pdf.Graphics.RectD.FromLtrb(0, -0.2, 0.5, 0.8);
        var page = cellToPage.TransformBounds(cell);
        // the bar leaves out a strip at the origin's height and covers the rest of the cell and beyond
        List<Core.Pdf.Graphics.RectD> bars = [Core.Pdf.Graphics.RectD.FromLtrb(page.Left - 5, 500.5, page.Right + 5, page.Bottom + 5)];
        Assert.True(Redactor.GlyphRemoved(bars, cell, Core.Pdf.Graphics.RectD.Empty, cellToPage));
        Assert.True(Redactor.GlyphRemoved(bars, cell, Core.Pdf.Graphics.RectD.Empty, cellToPage, verify: true));
    }

    /// <summary>
    /// A tiny image drawn ten times large, a bar across all of them: the images are refined for the bar's edge together within the budget of
    /// the page — each one refined up to its own limit, a damaged file with many uses took 4 GB to draw (docs/FEHLERGEDAECHTNIS.md, No. 378).
    /// Under the bar it stays black.
    /// </summary>
    [Fact]
    public void TinyImageDrawnManyTimesLarge_RefinedWithinThePagesBudget()
    {
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(2)),
            ("Height", PdfInteger.Of(2)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8))), [200, 220, 240, 255]));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(image))))));
        var content = string.Concat(Enumerable.Range(0, 10).Select(i => $"q 1000 0 0 1000 {-400 + i * 3} -100 cm /Im Do Q "));
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content)));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(100, 400, 300, 420))));
        var pixels = Enumerable.Range(1, saved.ObjectCount + 8).Select(saved.GetObject).OfType<PdfStream>()
            .Where(st => st.Dictionary.GetName(PdfName.Subtype)?.Value == "Image")
            .Sum(st => (long)(st.Dictionary.GetInteger(PdfName.Get("Width")) ?? 0) * (st.Dictionary.GetInteger(PdfName.Get("Height")) ?? 0));
        Assert.InRange(pixels, 1, ImageBlanker.PageRefineBudget + 100);
        using var bitmap = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0]), saved, saved.Pages[0], 1, new Rendering.ImageCache());
        var c = bitmap.GetPixel(200, bitmap.Height - 410);
        Assert.True(c.Red < 40 && c.Green < 40 && c.Blue < 40, c.ToString());
    }

    /// <summary>
    /// After a Tf with one operand — which the display ignores, keeping its font — the rest of the line counts for the font before: the
    /// count of shown codes (what redaction lets a font keep) read every text operator as the display does. It switched to no font there, and
    /// those codes counted as not shown (docs/FEHLERGEDAECHTNIS.md, No. 379); also Tj with a surplus operand — the one directly before it
    /// counts (7.8.2, No. 912: „ij“, not „gh“) —, and q nested deeper than the display saves.
    /// </summary>
    [Fact]
    public void FontUsage_ReadsTextOperatorsAsTheDisplay()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(
            "BT /F1 20 Tf 100 500 Td (abc) Tj /F9 Tf (def) Tj (gh) (ij) Tj " + string.Concat(Enumerable.Repeat("q ", 600)) + "/F9 9 Tf " +
            string.Concat(Enumerable.Repeat("Q ", 600)) + "(kl) Tj ET")));
        using var doc = PdfDocument.Open(pdf.Build());
        var usage = new FontUsage(doc).Collect([doc.Pages[0]], default);
        var font = (PdfDictionary)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1"));
        var codes = usage.Codes[font].Select(c => (char)c.Code).ToHashSet();
        // as the display shows them: all with F1 — the q beyond 512 are not saved, so the Q after them restore the state with F1
        var shown = Read(doc, doc.Pages[0]).Text.Where(char.IsLetter).ToHashSet();
        Assert.Superset(shown, codes);
        Assert.Contains('d', codes);
        Assert.Contains('i', codes);
        Assert.DoesNotContain('g', codes);
        Assert.Contains('k', codes);
    }

    /// <summary>
    /// A graphics state with a font but no size, and a tiling pattern whose text has no Tf of its own: the display keeps the font before the
    /// gs and draws the pattern's text with the font of the state it paints in. The count switched to no font at such a gs and read patterns
    /// without a font, so those characters counted as not shown — redacting elsewhere emptied their glyphs (docs/FEHLERGEDAECHTNIS.md, No. 427).
    /// </summary>
    [Fact]
    public void FontUsage_GraphicsStateFontAndPatternTextAsTheDisplay()
    {
        var pdf = TestPdf.Simple(1);
        var other = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Courier"))));
        var cell = pdf.Add(TestPdf.FlateStream("BT 2 10 Td (xyz) Tj ET"u8.ToArray(), TestPdf.Dict(("PatternType", PdfInteger.Of(1)), ("PaintType", PdfInteger.Of(1)),
            ("TilingType", PdfInteger.Of(1)), ("BBox", TestPdf.Box(0, 0, 60, 30)), ("XStep", PdfInteger.Of(60)), ("YStep", PdfInteger.Of(30)),
            ("Resources", new PdfDictionary()))));
        var resources = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!;
        resources.Set(PdfName.Get("ExtGState"), TestPdf.Dict(("GS1", TestPdf.Dict(("Font", new PdfArray([TestPdf.Ref(other)]))))));
        resources.Set(PdfName.Get("Pattern"), TestPdf.Dict(("P1", TestPdf.Ref(cell))));
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 20 Tf 100 500 Td (abc) Tj /GS1 gs (def) Tj ET /Pattern cs /P1 scn 100 300 120 60 re f"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Contains("def", Read(doc, doc.Pages[0]).Text);
        var usage = new FontUsage(doc).Collect([doc.Pages[0]], default);
        var font = (PdfDictionary)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1"));
        var codes = usage.Codes[font].Select(c => (char)c.Code).ToHashSet();
        Assert.Contains('d', codes);
        Assert.Contains('x', codes);
    }

    /// <summary>
    /// A TrueType font whose only cmap the display reaches through the raw code (a (3,2) table, found as a last resort as in common font
    /// engines): after one of its letters is redacted, the other keeps its glyph. The glyphs a font keeps were found by a list of its own that
    /// missed this way, and the shown letter was emptied out of the program (found by the redaction focus of the fuzzer;
    /// docs/FEHLERGEDAECHTNIS.md, No. 382).
    /// </summary>
    [Fact]
    public void LetterReachedThroughTheRawCode_KeepsItsGlyph()
    {
        var sans = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var ot = Core.Fonts.OpenTypeFont.Parse(sans);
        int Gid(char c) => ot.GlyphForUnicode(c);
        var advances = new Dictionary<int, int> { [0] = 0, [Gid('A')] = (int)ot.AdvanceWidth(Gid('A')), [Gid('B')] = (int)ot.AdvanceWidth(Gid('B')) };
        var program = Core.Fonts.TrueTypeSubsetter.Rebuild(sans, advances, [], [('A', Gid('A')), ('B', Gid('B'))]);
        // the (3,0) table the rebuild writes becomes a (3,2) table: no Unicode, no symbol codes
        var tables = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(4));
        for (var i = 0; i < tables; i++)
            if (System.Text.Encoding.ASCII.GetString(program, 12 + i * 16, 4) == "cmap")
                program[(int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(program.AsSpan(12 + i * 16 + 8)) + 7] = 2;
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(program: () => program)());
        Assert.Contains("AB", Read(doc, doc.Pages[0]).Text);
        var source = Core.Pdf.Fonts.PdfFont.Get((PdfDictionary)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1")), doc);
        var (a, b) = (source.Glyph('A', 1).Outline, source.Glyph('B', 1).Outline);
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(89, 690, 108, 730))));   // over the B (A: 72 … 88)
        var font = (PdfDictionary)after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1"));
        // the A as before, not the placeholder box of glyph 0 the display draws for a code with a width but no glyph
        var kept = Core.Pdf.Fonts.PdfFont.Get(font, after).Glyph('A', 1).Outline;
        Assert.Equal(a.Points.ToArray(), kept.Points.ToArray());
        Assert.NotEqual(b.Points.ToArray(), Core.Pdf.Fonts.PdfFont.Get(font, after).Glyph('B', 1).Outline.Points.ToArray());
    }

    /// <summary>
    /// Content that closes a state it never opened (a Q readers pass over) after setting the font: the rest of the line keeps its font and its
    /// place after a redaction. The content was put after as many q as it closed too many, so that this Q restored the page's initial state —
    /// no font any more — and a line in a damaged file slid under a bar in size 1 (found by the redaction focus of the fuzzer;
    /// docs/FEHLERGEDAECHTNIS.md, No. 383).
    /// </summary>
    [Fact]
    public void RestoreWithoutSave_RestOfTheLineKeepsItsFont()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 20 Tf ET Q BT 100 500 Td (abcdef) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var before = Read(doc, doc.Pages[0]).Glyphs.Where(g => g.Char is "d" or "e" or "f").ToList();
        Assert.Equal(3, before.Count);
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(99, 490, 131, 520))));   // over „abc“ (a, b, c: 100 … 132)
        var kept = Read(after, after.Pages[0]).Glyphs.Where(g => g.Char is "d" or "e" or "f").ToList();
        Assert.Equal(before.Select(g => g.Char), kept.Select(g => g.Char));
        foreach (var (was, now) in before.Zip(kept)) Assert.Equal(was.X, now.X, 0.3);   // within the grid the gap is rounded to
    }

    /// <summary>
    /// A text operator that shows nothing and moves nothing (a Tj without a string in a damaged file) between the part of a line that loses its
    /// end and the rest of it: the rest stays where it was. It was placed from where its first character stood before — as if the removed
    /// end were still there — and slid under the bar (found by the redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 383). Also a
    /// Td with one operand, which the display passes over as well.
    /// </summary>
    [Theory]
    [InlineData("Tj")]
    [InlineData("0 Td")]
    [InlineData("[] TJ")]
    public void OperatorThatShowsNothing_RestOfTheLineStays(string between)
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes($"BT /F1 20 Tf 100 500 Td (abcdef) Tj {between} (xyz) Tj ET")));
        using var doc = PdfDocument.Open(pdf.Build());
        var x = Read(doc, doc.Pages[0]).Glyphs.Single(g => g.Char == "x").X;
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(132, 490, 159.5, 520))));   // over „def“ (x from 160 on)
        Assert.Equal(x, Read(after, after.Pages[0]).Glyphs.Single(g => g.Char == "x").X, 0.3);
    }

    /// <summary>
    /// A text operator whose end is redacted, then one written as it is (turned by a matrix, so on no line that loses characters), then the rest
    /// of the line: both stay where they were. The first ended at its last kept character, the raised one went on from there, and the rest of the line was placed as if
    /// the removed end were still there — the pen was forgotten at the operator written as it is (found by the redaction focus of the fuzzer;
    /// docs/FEHLERGEDAECHTNIS.md, No. 383).
    /// </summary>
    [Fact]
    public void RedactedEndOfAnOperator_WhatFollowsStaysWhereItWas()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 20 Tf 100 500 Td (abcdef) Tj q 0 1 -1 0 800 300 cm (Q) Tj Q (xyz) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var before = Read(doc, doc.Pages[0]).Glyphs;
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(132, 490, 159.5, 520))));   // over „def“
        var glyphs = Read(after, after.Pages[0]).Glyphs;
        foreach (var c in new[] { "Q", "x", "y", "z" })
            Assert.Equal(before.Single(g => g.Char == c).X, glyphs.Single(g => g.Char == c).X, 0.3);
    }

    /// <summary>
    /// A page of 15,000 short lines under a bar, with the lengths hidden: the lines are evened out in time growing with the number of characters.
    /// For every line all characters of the page were searched for text reaching into it — two seconds per pass for a damaged page, which the
    /// check after hiding lengths runs up to five times (found by the redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 384).
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void ManyLines_LengthsHiddenWithoutSearchingThePagePerLine()
    {
        // lines 0.05 pt apart, each with „ab“ in 0.04 pt, stretched to half a point per character
        var content = new System.Text.StringBuilder("BT /F1 0.04 Tf 2500 Tz 1 0 0 1 100 790 Tm 0.05 TL ");
        for (var i = 0; i < 15_000; i++) content.Append("(ab) Tj T* ");
        content.Append("ET");
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content.ToString())));
        using var doc = PdfDocument.Open(pdf.Build());
        Redactor.Apply(doc, doc.Pages[0], [new PdfRectangle(99.9, 0, 100.55, 800)], hideLengths: true);   // warm up
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = Redactor.Apply(doc, doc.Pages[0], [new PdfRectangle(99.9, 0, 100.55, 800)], hideLengths: true);   // the „a“ of every line
        Assert.DoesNotContain("a", Read(doc, PageOf(doc.Pages[0], result)).Text);
        Assert.True(watch.ElapsedMilliseconds < 1_500 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");   // measured 0.4 s, before 3.4 s (debug build)
    }

    /// <summary>
    /// A letter squeezed by its matrix to a ten-millionth of a point in width, wholly under a bar: it goes. Coverage was measured against at
    /// least a millionth of a point, which such a letter never reaches — it stayed, and the check before saving, measuring the same way, let
    /// it pass (found by the redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 385).
    /// </summary>
    [Fact]
    public void LetterNarrowerThanAMillionthOfAPoint_UnderABar_Goes()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 1 Tf 0.0000001 0 0 500 0.000001 100 Tm (I) Tj ET BT /F1 12 Tf 72 700 Td (Rest) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Contains("I", Read(doc, doc.Pages[0]).Text);
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(-1, 50, 1, 600))));
        var text = Read(after, after.Pages[0]).Text;
        Assert.DoesNotContain("I", text);
        Assert.Contains("Rest", text);
    }

    /// <summary>
    /// A code with a width but no glyph in its TrueType font is drawn with glyph 0, as common readers do: after another letter of the font is
    /// redacted, it keeps that glyph as drawn. Here loca ends glyph 0 too early — the display reads its outline on, clearing the font copies it
    /// only as far as loca says. The glyphs a font keeps never included glyph 0, so the check after clearing did not compare it, and in a
    /// damaged font a huge glyph 0 became small and its character slid under a bar (found by the redaction focus of the fuzzer;
    /// docs/FEHLERGEDAECHTNIS.md, No. 386).
    /// </summary>
    [Fact]
    public void CodeDrawnWithGlyphZero_KeepsIt()
    {
        var sans = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var ot = Core.Fonts.OpenTypeFont.Parse(sans);
        int Gid(char c) => ot.GlyphForUnicode(c);
        var advances = new Dictionary<int, int> { [0] = (int)ot.AdvanceWidth(0), [Gid('A')] = (int)ot.AdvanceWidth(Gid('A')) };
        var program = Core.Fonts.TrueTypeSubsetter.Rebuild(sans, advances, [], [('A', Gid('A'))]);   // „B“ has a width, but no glyph
        // glyph 0 ends after 12 bytes in loca (its header and the end of its first contour); the rebuilt font has long offsets
        var tables = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(4));
        for (var i = 0; i < tables; i++)
            if (System.Text.Encoding.ASCII.GetString(program, 12 + i * 16, 4) == "loca")
            {
                var loca = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(program.AsSpan(12 + i * 16 + 8));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(program.AsSpan(loca + 4),
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(program.AsSpan(loca)) + 12);
            }
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(program: () => program)());
        var page = doc.Pages[0];
        var dict = (PdfDictionary)page.Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1"));
        var before = Core.Pdf.Fonts.PdfFont.Get(dict, doc).Glyph('B', 1).Outline;
        Assert.False(before.IsEmpty);   // glyph 0, the box of a missing character
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(70, 690, 87, 730))));   // over the A (72 … 88)
        var font = (PdfDictionary)after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1"));
        Assert.Equal(before.Points.ToArray(), Core.Pdf.Fonts.PdfFont.Get(font, after).Glyph('B', 1).Outline.Points.ToArray());
    }

    /// <summary>
    /// A code whose cmap entry points beyond the glyphs of its TrueType font draws nothing; after another letter of the font is redacted it still
    /// draws nothing. Clearing the font emptied that cmap entry — then the code fell to glyph 0 (it has a width) and a box appeared, under a bar
    /// (found by the redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 402).
    /// </summary>
    [Fact]
    public void CodeMappedBeyondTheGlyphs_StillDrawsNothing()
    {
        var sans = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var ot = Core.Fonts.OpenTypeFont.Parse(sans);
        int Gid(char c) => ot.GlyphForUnicode(c);
        var advances = new Dictionary<int, int> { [0] = (int)ot.AdvanceWidth(0), [Gid('A')] = (int)ot.AdvanceWidth(Gid('A')) };
        var program = Core.Fonts.TrueTypeSubsetter.Rebuild(sans, advances, [], [('A', Gid('A')), ('B', 60_000)]);   // „B“ beyond the glyphs
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(program: () => program)());
        var dict = (PdfDictionary)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1"));
        Assert.True(Core.Pdf.Fonts.PdfFont.Get(dict, doc).Glyph('B', 1).Outline.IsEmpty);
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(70, 690, 87, 730))));   // over the A (72 … 88)
        var font = (PdfDictionary)after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1"));
        Assert.True(Core.Pdf.Fonts.PdfFont.Get(font, after).Glyph('B', 1).Outline.IsEmpty);
    }

    /// <summary>
    /// A slanted character set a hundred billion points from the origin, its corners a thin parallelogram, wholly under a bar: the covered part
    /// is all of it. The area of its corners was computed from the coordinates as they are; their products were so large that the area — tiny
    /// beside them — was lost in rounding, and such a letter counted as hardly covered and stayed (found by the redaction focus of the fuzzer;
    /// docs/FEHLERGEDAECHTNIS.md, No. 387).
    /// </summary>
    [Fact]
    public void SlantedCharacterFarFromTheOrigin_CountsAsCovered()
    {
        var m = new Core.Pdf.Graphics.Matrix(635289.834, 128818.2157, -757108.877, -153519.5582, 15587234970.49, -127170661743.79);
        (double X, double Y)[] quad = [m.Transform(0, -0.2), m.Transform(0.667, -0.2), m.Transform(0.667, 0.8), m.Transform(0, 0.8)];
        var bar = Core.Pdf.Graphics.RectD.FromLtrb(quad.Min(p => p.X) - 1000, quad.Min(p => p.Y) - 1000, quad.Max(p => p.X) + 1000, quad.Max(p => p.Y) + 1000);
        Assert.Equal(1, Redactor.QuadCoverage([bar], quad), 6);
        // a bar beside it covers nothing
        var beside = Core.Pdf.Graphics.RectD.FromLtrb(bar.Right + 10, bar.Top, bar.Right + 2_000_000, bar.Bottom);
        Assert.Equal(0, Redactor.QuadCoverage([beside], quad), 6);
        // the ink of a letter at 10^69 points whose axes are all but parallel: a sliver whose area — large by itself — is made of rounding;
        // measured by the line it lies on (docs/FEHLERGEDAECHTNIS.md, No. 395)
        var far = new Core.Pdf.Graphics.Matrix(-9.191412459430877E+66, 8.267752990476749E+66, 3.323800486954946E+69, -2.9897865575136545E+69,
            1.184678080243416E+69, -1.0656279199651165E+69);
        (double X, double Y)[] sliver = [far.Transform(0.08203125, 0), far.Transform(0.6142578125, 0), far.Transform(0.6142578125, 0.68798828125), far.Transform(0.08203125, 0.68798828125)];
        var around = Core.Pdf.Graphics.RectD.FromLtrb(1.129439701983712E+69, -3.097539288303836E+69, 3.4435911717351253E+69, -1.015940533063318E+69);
        Assert.True(Redactor.QuadCoverage([around], sliver) >= 0.9, $"{Redactor.QuadCoverage([around], sliver)}");
    }

    /// <summary>
    /// A TrueType font whose maxp counts no glyphs (readers then count them in loca, as common font engines do): after one of its letters is
    /// redacted, the other keeps its glyph. Clearing the font took maxp's 0 and wrote a font without glyphs; the check after clearing compared
    /// outlines and advances, which read the same, but not the count — and the display found no glyph any more (found by the redaction focus of
    /// the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 389).
    /// </summary>
    [Fact]
    public void FontWithoutGlyphCountInMaxp_KeepsItsGlyphs()
    {
        var sans = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var ot = Core.Fonts.OpenTypeFont.Parse(sans);
        int Gid(char c) => ot.GlyphForUnicode(c);
        // „A“ shows a glyph without outline (the space's), the widths come from the program alone (no /Widths)
        var advances = new Dictionary<int, int> { [0] = (int)ot.AdvanceWidth(0), [Gid(' ')] = (int)ot.AdvanceWidth(Gid(' ')), [Gid('B')] = (int)ot.AdvanceWidth(Gid('B')) };
        var program = Core.Fonts.TrueTypeSubsetter.Rebuild(sans, advances, [], [(0xF041, Gid(' ')), (0xF042, Gid('B'))]);
        var tables = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(4));
        for (var i = 0; i < tables; i++)
            if (System.Text.Encoding.ASCII.GetString(program, 12 + i * 16, 4) == "maxp")
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(
                    program.AsSpan((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(program.AsSpan(12 + i * 16 + 8)) + 4), 0);
        Assert.True(Core.Fonts.OpenTypeFont.Parse(program).GlyphCount > Gid('B'));   // counted in loca
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(font: f => f.Replace("/FirstChar 65/LastChar 66/Widths[667 667]", ""),
            descriptor: d => d.Replace("/Flags 32", "/Flags 4"), program: () => program)());
        var dict = (PdfDictionary)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1"));
        var before = Core.Pdf.Fonts.PdfFont.Get(dict, doc).Glyph('A', 1).Width;
        Assert.True(before > 0.2);
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(80, 690, 108, 730))));   // over the B (A: 72 … 78.7)
        var font = (PdfDictionary)after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1"));
        Assert.Equal(before, Core.Pdf.Fonts.PdfFont.Get(font, after).Glyph('A', 1).Width, 6);
    }

    /// <summary>
    /// A mark over several areas, one of them far away (a character of a damaged file at 10^19 points): the others still cover their text.
    /// The areas were kept relative to their bounding rectangle, which became so large that the near ones were lost in rounding — the mark
    /// covered nothing, and the check before saving measured against the same lost areas (found by the redaction focus of the fuzzer;
    /// docs/FEHLERGEDAECHTNIS.md, No. 390).
    /// </summary>
    [Fact]
    public void MarkWithAFarArea_StillCoversTheNearOnes()
    {
        PdfRectangle line1 = new(70, 695, 150, 715), line2 = new(70, 675, 120, 695);
        // the far one without height covers nothing and is left out
        var flat = RedactionElement.FromPageAreas([line1, line2, new PdfRectangle(14, -2.4e19, 30, -2.4e19)]);
        Assert.Equal([line1, line2], flat.PageAreas.Select(a => new PdfRectangle(Math.Round(a.Left, 6), Math.Round(a.Bottom, 6), Math.Round(a.Right, 6), Math.Round(a.Top, 6))));
        // one with an area: the whole rectangle — more, never less
        var far = RedactionElement.FromPageAreas([line1, line2, new PdfRectangle(14, -2.4e19, 30, -2.3e19)]);
        foreach (var line in new[] { line1, line2 })
            Assert.Contains(far.PageAreas, a => a.Left <= line.Left && a.Bottom <= line.Bottom && a.Right >= line.Right && a.Top >= line.Top);
        // end to end: the text under the near areas goes
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 12 Tf 72 700 Td (Geheim) Tj ET BT /F1 12 Tf 300 400 Td (Rest) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([flat]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true });
        using var after = PdfDocument.Open(output.ToArray());
        var text = Read(after, after.Pages[0]).Text;
        Assert.DoesNotContain("Geheim", text);
        Assert.Contains("Rest", text);
    }

    /// <summary>
    /// A letter its matrix shears to a sliver (almost no area, but not none), wholly under a bar: it goes. The covered share of a slanted
    /// character is measured by area, and below a billionth of a square point it counted as not covered at all — such a letter stayed under a bar
    /// over the whole page. Now it is measured by the line it lies on, as one without area (found by the redaction focus of the fuzzer;
    /// docs/FEHLERGEDAECHTNIS.md, No. 391).
    /// </summary>
    [Fact]
    public void LetterShearedToASliver_UnderABar_Goes()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 1 Tf 0.01071 0.56253 0 0.000000255 121 625.19 Tm (F) Tj ET BT /F1 12 Tf 72 700 Td (Rest) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Contains("F", Read(doc, doc.Pages[0]).Text);
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(120, 620, 122, 630))));
        var text = Read(after, after.Pages[0]).Text;
        Assert.DoesNotContain("F", text);
        Assert.Contains("Rest", text);
    }

    /// <summary>
    /// Two composite fonts with different CMaps sharing one descendant (its /W), one on each page; a character of the second page's font is
    /// redacted: the first page keeps its widths. The CIDs the descendant keeps were read through the font being cleared — the codes of the
    /// other page came out as other CIDs, their widths went, and the rest of a line slid under a bar (found by the redaction focus of the
    /// fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 392).
    /// </summary>
    [Fact]
    public void SharedDescendantFont_OtherPageKeepsItsWidths()
    {
        var pdf = TestPdf.Simple(2);
        var widths = pdf.Add(new PdfArray([PdfInteger.Of(65), new PdfArray([PdfInteger.Of(500)]), PdfInteger.Of(165), new PdfArray([PdfInteger.Of(700), PdfInteger.Of(700)])]));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Muster")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(-200), PdfInteger.Of(1000), PdfInteger.Of(800)])), ("ItalicAngle", PdfInteger.Of(0)),
            ("Ascent", PdfInteger.Of(800)), ("Descent", PdfInteger.Of(-200)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80))));
        var descendant = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType0")), ("BaseFont", TestPdf.N("Muster")),
            ("CIDSystemInfo", TestPdf.Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())), ("Supplement", PdfInteger.Of(0)))),
            ("FontDescriptor", TestPdf.Ref(descriptor)), ("DW", PdfInteger.Of(1000)), ("W", TestPdf.Ref(widths))));
        // the second font reads one byte per code, CID = 100 + code
        var shift = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Verschoben def " +
            "1 begincodespacerange <00> <FF> endcodespacerange 1 begincidrange <00> <FF> 100 endcidrange endcmap CMapName currentdict /CMap defineresource pop end end"),
            TestPdf.Dict(("Type", TestPdf.N("CMap")), ("CMapName", TestPdf.N("Verschoben")),
                ("CIDSystemInfo", TestPdf.Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())), ("Supplement", PdfInteger.Of(0)))))));
        PdfDictionary Type0(PdfObject encoding) => TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("Muster")),
            ("Encoding", encoding), ("DescendantFonts", new PdfArray([TestPdf.Ref(descendant)])));
        var first = pdf.Add(Type0(TestPdf.N("Identity-H")));
        var second = pdf.Add(Type0(TestPdf.Ref(shift)));
        PdfDictionary Page(int content, int font) => TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(2)), ("Contents", TestPdf.Ref(content)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F", TestPdf.Ref(font)))))));
        pdf.Replace(4, TestPdf.FlateStream("BT /F 20 Tf 100 700 Td <00410041> Tj ET"u8.ToArray()));
        pdf.Replace(5, Page(4, first));
        pdf.Replace(6, TestPdf.FlateStream("BT /F 20 Tf 100 700 Td <4142> Tj ET"u8.ToArray()));
        pdf.Replace(7, Page(6, second));
        using var doc = PdfDocument.Open(pdf.Build());
        double WidthOnPageOne(PdfDocument d) =>
            Core.Pdf.Fonts.PdfFont.Get((PdfDictionary)d.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F")), d).Glyph(0x0041, 2).Width;
        Assert.Equal(0.5, WidthOnPageOne(doc), 6);
        using var after = PdfDocument.Open(WithBars(doc, (1, new PdfRectangle(95, 650, 114.5, 750))));   // the first character of page 2 (100 … 114)
        Assert.Equal(0.5, WidthOnPageOne(after), 6);
    }

    /// <summary>
    /// A composite font whose /W names a CID twice (in a list and again in a range, as damaged or sloppily written files do): after a redaction
    /// on the same line the kept characters keep the width the display reads — the entry of the list. The clearing of the font read /W on its own
    /// and took the last one; the space before the bar became ten times wider and the text in front of the bar slid under it (found by the
    /// redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 401).
    /// </summary>
    [Fact]
    public void CidNamedTwiceInW_KeptCharactersKeepTheirWidth()
    {
        var pdf = TestPdf.Simple(1);
        var widths = pdf.Add(new PdfArray([PdfInteger.Of(3), new PdfArray([PdfInteger.Of(278)]), PdfInteger.Of(3), PdfInteger.Of(3), PdfInteger.Of(2760),
            PdfInteger.Of(65), PdfInteger.Of(67), PdfInteger.Of(600)]));
        // an embedded TrueType program (the glyphs of CIDs 3, 65 and 66 through the identity map); the widths come from /W alone
        var file = pdf.Add(TestPdf.FlateStream(Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf")));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Muster")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(-200), PdfInteger.Of(1000), PdfInteger.Of(800)])), ("ItalicAngle", PdfInteger.Of(0)),
            ("Ascent", PdfInteger.Of(800)), ("Descent", PdfInteger.Of(-200)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)),
            ("FontFile2", TestPdf.Ref(file))));
        var descendant = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N("Muster")),
            ("CIDSystemInfo", TestPdf.Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())), ("Supplement", PdfInteger.Of(0)))),
            ("FontDescriptor", TestPdf.Ref(descriptor)), ("DW", PdfInteger.Of(1000)), ("W", TestPdf.Ref(widths)), ("CIDToGIDMap", TestPdf.N("Identity"))));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("Muster")),
            ("Encoding", TestPdf.N("Identity-H")), ("DescendantFonts", new PdfArray([TestPdf.Ref(descendant)]))));
        // A, space, B, C: the C under a bar (a character the page shows nowhere else, so the font is cleared)
        pdf.Replace(4, TestPdf.FlateStream("BT /F 20 Tf 100 700 Td <0041000300420043> Tj ET"u8.ToArray()));
        pdf.Replace(5, TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(2)), ("Contents", TestPdf.Ref(4)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F", TestPdf.Ref(font))))))));
        using var doc = PdfDocument.Open(pdf.Build());
        double Space(PdfDocument d) =>
            Core.Pdf.Fonts.PdfFont.Get((PdfDictionary)d.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F")), d).Glyph(0x0003, 2).Width;
        Assert.Equal(0.278, Space(doc), 6);
        // A 100 … 112, space … 117.56, B … 129.56, C … 141.56: the bar over the C
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(130.5, 650, 145, 750))));
        Assert.Equal(0.278, Space(after), 6);
    }

    /// <summary>
    /// A string of a million characters beside the page, removed as hidden content: what is removed is held as one run, not one entry per
    /// character, and a page with that many characters is not snapshot character by character for hiding lengths — together they took gigabytes
    /// for a damaged page of 20 million characters (found by the redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 384).
    /// </summary>
    [Fact]
    public void MillionCharacters_RemovedWithoutAnEntryPerCharacter()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(
            "BT /F1 12 Tf 72 700 Td (Geheim Rest) Tj ET BT /F1 1 Tf -5000 -5000 Td (" + new string('a', 1_100_000) + ") Tj ET")));
        using var doc = PdfDocument.Open(pdf.Build());
        Redactor.RemoveHidden(doc, doc.Pages[0], false, false, default, outside: true);   // warm up (fonts, caches)
        var before = GC.GetAllocatedBytesForCurrentThread();
        var cleaned = Redactor.RemoveHidden(doc, doc.Pages[0], false, false, default, outside: true);
        var hidden = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(cleaned);
        before = GC.GetAllocatedBytesForCurrentThread();
        var redacted = Redactor.Apply(doc, doc.Pages[0], [AreaOf(doc, doc.Pages[0], "Geheim")], hideLengths: true);
        var applied = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.DoesNotContain("Geheim", Read(doc, PageOf(doc.Pages[0], redacted)).Text);
        // reading the page already allocates about 50 bytes per character (measured: 118 and 463 MB; with an entry per removed character and
        // a snapshot of every character 588 and 688 MB). Since the last check of what stays covers pages beyond the limit for hiding lengths
        // (decided on 4.11.2026: safety before speed; No. 460), every character is taken before and after redacting and compared:
        // 1 713 MB allocated for the 1.1 million characters (short-lived; 2 398 MB with a list per character and copies of both snapshots);
        // with lines checked in their own direction (No. 485) 1 845 MB — a direction stored as two doubles took 2 032 MB; with the index of
        // that check (No. 999) 1 856 MB, with a closure per character there 2 005 MB
        Assert.True(hidden < 250_000_000, $"Bereinigen: {hidden / 1_000_000} MB");
        Assert.True(applied < 1_900_000_000, $"Schwärzen: {applied / 1_000_000} MB");
    }

    /// <summary>
    /// Text set with a matrix that flattens it to a line (no area, nothing drawn, its text still in the file), a bar over most of the line of the
    /// first character but not its origin: the character goes — only its origin counted, which lay just beside the bar; the next one, mostly
    /// beside the bar, stays (found by the redaction focus of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 381).
    /// </summary>
    [Fact]
    public void CharacterFlattenedToALine_MostlyUnderABar_Goes()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 1 Tf 10 100 0 0 100 400 Tm (ab) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Contains("ab", Read(doc, doc.Pages[0]).Text);
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(99, 402, 107, 455))));   // the origin of b at 455.6 stays beside it
        var text = Read(after, after.Pages[0]).Text;
        Assert.DoesNotContain("a", text);
        Assert.Contains("b", text);
    }

    /// <summary>
    /// A bar that grazes a neighbouring letter of plain text on white: that letter shows beside the bar, so the redacted page is not measured
    /// once more for letters the bar made invisible; with a white box over the rest of a word it is (docs/FEHLERGEDAECHTNIS.md, No. 188 —
    /// measuring every redacted page cost the save a third more time).
    /// </summary>
    [Fact]
    public void GrazedLetters_MeasuredOnlyWhenSomethingMayCoverThem()
    {
        var plain = TestPdf.Simple(1);
        plain.Replace(4, TestPdf.FlateStream("BT /F1 40 Tf 100 500 Td (HIDE) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(plain.Build());
        Assert.Equal(0, Redactor.Apply(doc, doc.Pages[0], [new PdfRectangle(95, 495, 210, 507)]).GrazedGlyphs);

        var covered = TestPdf.Simple(1);
        covered.Replace(4, TestPdf.FlateStream("BT /F1 40 Tf 100 500 Td (HIDE) Tj ET 1 1 1 rg 90 504 200 60 re f"u8.ToArray()));
        using var second = PdfDocument.Open(covered.Build());
        Assert.True(Redactor.Apply(second, second.Pages[0], [new PdfRectangle(95, 495, 210, 507)]).GrazedGlyphs > 0);
    }

    /// <summary>
    /// Two soft masks whose groups draw the same image (shadows of two symbols), a bar over one of them: the other keeps the image — it
    /// was taken from the whole file because the quick check of what a page can reach did not look into soft masks, and the second
    /// shadow became a box (docs/FEHLERGEDAECHTNIS.md, No. 171).
    /// </summary>
    [Fact]
    public void SoftMasksSharingAnImage_TheOtherKeepsIt()
    {
        var pdf = TestPdf.Simple(1);
        var pixels = new byte[16 * 16];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 16 * 16);
        var image = pdf.Add(TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(16)), ("Height", PdfInteger.Of(16)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)))));
        int Group(int x) => pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes($"q 100 0 0 100 {x} 500 cm /Im0 Do Q"), TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(x, 500, x + 100, 600)),
            ("Group", TestPdf.Dict(("S", TestPdf.N("Transparency")), ("CS", TestPdf.N("DeviceGray")))),
            ("Resources", TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image)))))))));
        PdfDictionary State(int group) => TestPdf.Dict(("SMask", TestPdf.Dict(("Type", TestPdf.N("Mask")), ("S", TestPdf.N("Luminosity")), ("G", TestPdf.Ref(group)))));
        var (left, right) = (Group(100), Group(300));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("ExtGState", TestPdf.Dict(("SM1", State(left)), ("SM2", State(right))))));
        pdf.Replace(4, TestPdf.FlateStream("q /SM1 gs 0 0 1 rg 100 500 100 100 re f Q q /SM2 gs 0 0 1 rg 300 500 100 100 re f Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(110, 510, 150, 550))));
        using var before = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new Rendering.ImageCache());
        using var after = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0]), saved, saved.Pages[0], 1, new Rendering.ImageCache());
        var (changed, top) = (0, (int)doc.Pages[0].MediaBox.Top);
        for (var y = top - 600; y < top - 500; y++)
            for (var x = 300; x < 400; x++)
                if (Math.Abs(before.GetPixel(x, y).Blue - after.GetPixel(x, y).Blue) > 24 || Math.Abs(before.GetPixel(x, y).Red - after.GetPixel(x, y).Red) > 24) changed++;
        Assert.Equal(0, changed);
    }

    /// <summary>
    /// An ink annotation without an appearance whose line runs beyond its /Rect (a /Rect written for a turned page): a bar over the part
    /// beyond /Rect removes it — by /Rect alone it stayed and its line showed over the bar (docs/FEHLERGEDAECHTNIS.md, No. 167). A note
    /// with an empty /Rect, whose icon lies at its top-left corner, goes under a bar over the icon as well.
    /// </summary>
    [Fact]
    public void AnnotationDrawnBeyondItsRect_DroppedWhereItDraws()
    {
        var pdf = TestPdf.Simple(1);
        var ink = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Ink")), ("Rect", TestPdf.Box(100, 100, 150, 150)),
            ("C", new PdfArray([PdfInteger.Of(1), PdfInteger.Of(0), PdfInteger.Of(0)])),
            ("InkList", new PdfArray([new PdfArray([PdfInteger.Of(120), PdfInteger.Of(120), PdfInteger.Of(400), PdfInteger.Of(500)])]))));
        var note = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Text")), ("Rect", TestPdf.Box(300, 300, 300, 300))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(ink), TestPdf.Ref(note)]));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        // over the line near (360, 443), beyond /Rect; over the middle of the icon (300 … 320, 280 … 300)
        var result = Redactor.Apply(doc, page, [new PdfRectangle(340, 420, 380, 470), new PdfRectangle(305, 285, 315, 295)]);
        Assert.Equal(2, result.DroppedAnnotations.Count);
    }

    /// <summary>
    /// Paths half under a bar — a filled circle of curves, a stroked curve like a signature (also dashed), a rectangle filled and stroked,
    /// a circle as a clip, a line filled (no area, paints nothing; it was not seen to lie under the bar, docs/FEHLERGEDAECHTNIS.md, No. 178):
    /// none of their points remains under the bar, and beside it nothing changes, the dashes lie where they were — they were only painted outside the bar, their coordinates under it
    /// stayed in the file (docs/FEHLERGEDAECHTNIS.md, No. 168).
    /// </summary>
    [Theory]
    [InlineData("0 0 1 rg 200 450 m 255.2 450 300 494.8 300 550 c 300 605.2 255.2 650 200 650 c 144.8 650 100 605.2 100 550 c 100 494.8 144.8 450 200 450 c f")]
    [InlineData("3 w 1 J 1 j 0 0 0 RG 100 300 m 150 420 180 200 230 330 c 260 410 290 250 330 320 c 380 400 400 280 450 340 c S")]
    [InlineData("4 w 0 0 1 RG 1 0 0 rg 120 300 300 200 re B")]
    [InlineData("2 w 0 0 0 RG 100 500 m 230 500 l S 230 400 m 230 400 l S")]
    [InlineData("q 200 450 m 255.2 450 300 494.8 300 550 c 300 605.2 255.2 650 200 650 c 144.8 650 100 605.2 100 550 c 100 494.8 144.8 450 200 450 c W n 0 0.6 0 rg 50 300 400 400 re f Q")]
    [InlineData("2 w [7 4 2 4] 3 d 0 0 0 RG 100 300 m 150 420 180 200 230 330 c 260 410 290 250 330 320 c S 120 560 m 420 560 l S")]
    [InlineData("0 0 0 rg 200 450 m 220.42 450 l 199.88 450 l f 0 0 1 rg 100 300 50 50 re f")]
    // a path begun without m starts where the one before ended, not at the origin; under a matrix scaling by a million the pieces stay
    // where they were (docs/FEHLERGEDAECHTNIS.md, No. 428)
    [InlineData("2 w 0 0 0 RG 100 500 m 150 500 l S 250 500 l S")]
    [InlineData("q 1000000 0 0 1000000 0 0 cm 0.000002 w 0 0 0 RG 0.000123456 0.000400789 m 0.000377777 0.000400789 l S Q")]
    public void PathHalfUnderBar_ItsPointsLeaveTheFile(string content)
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content)));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var bar = new PdfRectangle(180, 280, 280, 620);
        var result = Redactor.Apply(doc, page, [bar]);
        // the points of all path operators in the new content (no cm in it: user space is page space); the cut ends of a stroke lie a
        // stroke's reach inside the bar, so that its edge stays as it was
        const double Seam = 4;
        var written = doc.GetStreamData((PdfStream)result.Page.Get(PdfName.Contents)!);
        var parser = new ContentParser(written);
        var inside = 0;
        while (parser.Next() is { } op)
        {
            if (op is not ("m" or "l" or "c" or "v" or "y")) continue;
            var o = parser.Operands;
            for (var k = 0; k + 1 < o.Count; k += 2)
                if (o[k].Number > bar.Left + Seam && o[k].Number < bar.Right - Seam && o[k + 1].Number > bar.Bottom + Seam && o[k + 1].Number < bar.Top - Seam) inside++;
        }
        Assert.Equal(0, inside);
        // beside the bar the same pixels (up to the smoothing of edges: a value within the range of its neighbours in the other picture)
        using var before = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 2, new Rendering.ImageCache());
        using var after = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, PageOf(page, result)), doc, PageOf(page, result), 2, new Rendering.ImageCache());
        var differing = 0;
        for (var y = 0; y < before.Height; y++)
            for (var x = 0; x < before.Width; x++)
            {
                double px = x / 2.0, py = (before.Height - y) / 2.0;
                if (px > bar.Left - 1.5 && px < bar.Right + 1.5 && py > bar.Bottom - 1.5 && py < bar.Top + 1.5) continue;
                SkiaSharp.SKColor a = before.GetPixel(x, y), c = after.GetPixel(x, y);
                if (Math.Max(Math.Abs(a.Red - c.Red), Math.Max(Math.Abs(a.Green - c.Green), Math.Abs(a.Blue - c.Blue))) <= 40) continue;
                if (!WithinNeighbours(before, x, y, c) || !WithinNeighbours(after, x, y, a)) differing++;
            }
        Assert.True(differing <= 3, $"{differing} Pixel neben dem Balken verändert");
    }

    /// <summary>
    /// A clip rectangle around a filled area whose upper edge passes through a small bar, no corner under it: the clip stays as written —
    /// cut, it came back as the same shape with two more corners on its edge (a path instead of re), and the renderer smoothed its left
    /// edge otherwise: a column of an image beside the bar changed by 26 levels (DocLayNet 19ee9956; docs/FEHLERGEDAECHTNIS.md, No. 873).
    /// </summary>
    [Fact]
    public void ClipRectanglePassingABar_NoCornerUnderIt_StaysAsWritten()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(
            "q 100.3 100 300 200 re W n 0.85 0.85 0.85 rg 99 99 303 203 re f Q 0 0 0 rg 210 310 20 10 re f")));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var result = Redactor.Apply(doc, page, [new PdfRectangle(200, 290, 212, 305)]);
        var parser = new ContentParser(doc.GetStreamData((PdfStream)result.Page.Get(PdfName.Contents)!));
        var clips = new List<string>();
        var last = "";
        while (parser.Next() is { } op)
        {
            if (op is "W" or "W*") clips.Add(last);
            last = op == "re" ? string.Join(" ", parser.Operands.Select(o => o.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))) + " re" : op;
        }
        Assert.Contains("100.3 100 300 200 re", clips);
        // the left edge of the clip renders as before (the bar lies far from it)
        using var before = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1.5, new Rendering.ImageCache());
        using var after = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, PageOf(page, result)), doc, PageOf(page, result), 1.5, new Rendering.ImageCache());
        var toDevice = Rendering.PageGeometry.PageToDevice(page, 1.5);
        for (var py = 110.0; py < 290; py += 0.5)
        {
            var (dx, dy) = toDevice.Transform(100.3, py);
            Assert.Equal(before.GetPixel((int)dx, (int)dy), after.GetPixel((int)dx, (int)dy));
        }
    }

    /// <summary>
    /// A dotted leader (dashes of length 0 with round caps, as a table of contents draws it) cut by a bar: each piece keeps the pattern as
    /// it was read and starts into it by its length along the line, so that every dot lies where it lay. Rounded to 1/1000, the gap 1.99945
    /// moved the dots right of a bar by 0.1 pt after 217 periods — in a DocLayNet case visible outside the bar (docs/FEHLERGEDAECHTNIS.md,
    /// No. 871).
    /// </summary>
    [Fact]
    public void DottedLeader_CutByABar_PiecesKeepTheExactPattern()
    {
        const double Start = 41.12445, Gap = 1.99945;
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(
            FormattableString.Invariant($"1 w 1 J 1 j 0 0 0 RG [0 {Gap}] 0 d {Start} 500 m 506.875 500 l S"))));
        using var doc = PdfDocument.Open(pdf.Build());
        var result = Redactor.Apply(doc, doc.Pages[0], [new PdfRectangle(277.66, 480, 491.545, 520)]);
        var parser = new ContentParser(doc.GetStreamData((PdfStream)result.Page.Get(PdfName.Contents)!));
        // the subpaths that are stroked (not the clip around the bar), each with the pattern in force
        var pieces = new List<(double[] Lengths, double Phase, double X)>();
        var starts = new List<double>();
        (double[] Lengths, double Phase) pattern = ([], 0);
        while (parser.Next() is { } op)
        {
            var o = parser.Operands;
            if (op == "d" && o.Count == 2 && o[0].Object is PdfArray array)
                pattern = ([.. Enumerable.Range(0, array.Count).Select(i => array.GetNumber(i) ?? double.NaN)], o[1].Number);
            else if (op == "m")
                starts.Add(o[0].Number);
            else if (op == "S")
            {
                pieces.AddRange(starts.Select(x => (pattern.Lengths, pattern.Phase, x)));
                starts.Clear();
            }
            else if (op is "n" or "f" or "f*" or "W" or "W*" or "B" or "b")
                starts.Clear();
        }
        Assert.Equal(2, pieces.Count);
        foreach (var (lengths, phase, x) in pieces)
        {
            Assert.Equal([0, Gap], lengths);
            // the pattern runs on from the start of the line: the piece begins that far into it
            Assert.Equal(x - Start, phase, 6);
        }
    }

    /// <summary>Whether each channel of <paramref name="value"/> lies within the range of the 3 × 3 pixels around (x, y), give or take 40.</summary>
    private static bool WithinNeighbours(SkiaSharp.SKBitmap image, int x, int y, SkiaSharp.SKColor value)
    {
        int[] low = [255, 255, 255], high = [0, 0, 0];
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (x + dx < 0 || y + dy < 0 || x + dx >= image.Width || y + dy >= image.Height) continue;
                var p = image.GetPixel(x + dx, y + dy);
                byte[] c = [p.Red, p.Green, p.Blue];
                for (var k = 0; k < 3; k++) (low[k], high[k]) = (Math.Min(low[k], c[k]), Math.Max(high[k], c[k]));
            }
        byte[] v = [value.Red, value.Green, value.Blue];
        return Enumerable.Range(0, 3).All(k => v[k] >= low[k] - 40 && v[k] <= high[k] + 40);
    }

    /// <summary>
    /// An embedded CFF font (FontFile3 /Type1C, glyph names through /Differences), a word redacted whose glyphs (gamma, Omega) occur nowhere else:
    /// afterwards the font program has neither their outlines nor their names, while alpha, beta and Sigma stay as they were
    /// (docs/FEHLERGEDAECHTNIS.md, No. 174).
    /// </summary>
    [Fact]
    public void CffFont_GlyphsOnlyInRedactedText_LeaveTheFont()
    {
        var pdf = TestPdf.Simple(1);
        var cff = Core.Fonts.EmbeddedFonts.LoadStandard("FoxitSymbol.cff");
        var original = Core.Fonts.CffFont.Parse(cff);
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Subtype", TestPdf.N("Type1C"))), cff));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Symbol")), ("Flags", PdfInteger.Of(4)),
            ("FontBBox", TestPdf.Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile3", TestPdf.Ref(file))));
        var differences = new PdfArray([PdfInteger.Of('S'), TestPdf.N("Sigma"), PdfInteger.Of('W'), TestPdf.N("Omega"),
            PdfInteger.Of('a'), TestPdf.N("alpha"), TestPdf.N("beta"), PdfInteger.Of('g'), TestPdf.N("gamma")]);
        var symbol = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("ABCDEF+Symbol")),
            ("FontDescriptor", TestPdf.Ref(descriptor)), ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", differences)))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("SY"), TestPdf.Ref(symbol));
        pdf.Replace(4, TestPdf.FlateStream("BT /SY 24 Tf 72 600 Td (gW) Tj 100 0 Td (abS) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var data = WithBars(doc, (0, new PdfRectangle(66, 590, 140, 630)));
        using var after = PdfDocument.Open(data);
        var dict = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("SY"))!;
        var program = Core.Fonts.CffFont.Parse(after.GetStreamData(dict.GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile3"))!));
        foreach (var name in (string[])["gamma", "Omega"])
        {
            Assert.Equal(-1, program.GlyphByName(name));
            Assert.True(program.Outline(original.GlyphByName(name)).IsEmpty, name);
        }
        foreach (var name in (string[])["alpha", "beta", "Sigma"])
        {
            Assert.Equal(original.GlyphByName(name), program.GlyphByName(name));
            Assert.Equal(original.Outline(original.GlyphByName(name)).Bounds, program.Outline(program.GlyphByName(name)).Bounds);
        }
    }

    /// <summary>
    /// A CFF font with its own encoding that names two glyphs alike, both shown: after a third letter is redacted, each code draws its glyph
    /// as before. The scrubbed program turns the encoding into code → name, and the second code reached the first glyph of that name;
    /// only each glyph by its number was compared (docs/FEHLERGEDAECHTNIS.md, No. 430).
    /// </summary>
    [Fact]
    public void CffFontWithTwoGlyphsOfOneName_EachCodeKeepsItsGlyph()
    {
        var sans = Core.Fonts.OpenTypeFont.Parse(Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"));
        (string, Core.Fonts.GlyphOutline, double) Glyph(string name, char c) => (name, sans.Outline(sans.GlyphForUnicode(c)), sans.AdvanceWidth(sans.GlyphForUnicode(c)));
        var cff = Core.Fonts.CffBuilder.Build("ABCDEF+Test", new Core.Pdf.Graphics.Matrix(1.0 / sans.UnitsPerEm, 0, 0, 1.0 / sans.UnitsPerEm, 0, 0),
            [Glyph("A", 'A'), Glyph("A", 'B'), Glyph("C", 'C')], [(byte)'a', (byte)'b', (byte)'c']);
        var pdf = TestPdf.Simple(1);
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Subtype", TestPdf.N("Type1C"))), cff));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Test")), ("Flags", PdfInteger.Of(4)),
            ("FontBBox", TestPdf.Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile3", TestPdf.Ref(file))));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("ABCDEF+Test")),
            ("FontDescriptor", TestPdf.Ref(descriptor))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("T1"), TestPdf.Ref(font));
        pdf.Replace(4, TestPdf.FlateStream("BT /T1 24 Tf 72 600 Td (ab) Tj 200 0 Td (c) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        Core.Fonts.GlyphOutline Shape(PdfDocument d, char code) =>
            Core.Pdf.Fonts.PdfFont.Get(d.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("T1"))!, d).Glyph(code, 1).Outline;
        var b = Shape(doc, 'b');
        Assert.Equal(sans.Outline(sans.GlyphForUnicode('B')).Points.ToArray(), b.Points.ToArray());
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(265, 590, 300, 630))));
        Assert.Equal(b.Points.ToArray(), Shape(after, 'b').Points.ToArray());
        Assert.Equal(Shape(doc, 'a').Points.ToArray(), Shape(after, 'a').Points.ToArray());
    }

    /// <summary>
    /// /Differences beginning outside the codes (-1 /a /b): the display names no code with them, and a shown code 0 draws nothing. Scrubbed,
    /// the encoding counted on from -1 and named code 0 /b — a letter appeared that was not to be seen (docs/FEHLERGEDAECHTNIS.md, No. 429).
    /// </summary>
    [Fact]
    public void DifferencesOutsideTheCodes_ScrubbedEncodingNamesNoShownCode()
    {
        var pdf = TestPdf.Simple(1);
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")),
            ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", new PdfArray([PdfInteger.Of(-1), TestPdf.N("a"), TestPdf.N("b")]))))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("H1"), TestPdf.Ref(font));
        pdf.Replace(4, TestPdf.FlateStream("BT /H1 24 Tf 72 600 Td (\\000) Tj 200 0 Td (Geheim) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.DoesNotContain("b", Read(doc, doc.Pages[0]).Text);
        using var after = PdfDocument.Open(WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim"))));
        Assert.DoesNotContain("Geheim", Read(after, after.Pages[0]).Text);
        Assert.DoesNotContain("b", Read(after, after.Pages[0]).Text);
        var glyph = Core.Pdf.Fonts.PdfFont.Get(after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("H1"))!, after).Glyph(0, 1);
        Assert.True(glyph.Outline.IsEmpty);
    }

    /// <summary>
    /// An embedded Type 1 font (FontFile), the letter A redacted, which occurs nowhere else: afterwards the font program has neither its
    /// charstring nor its name nor its code, and B stays as it was (docs/FEHLERGEDAECHTNIS.md, No. 180) — also if the font is embedded as the
    /// segments of a PFB file, which stayed whole (No. 286).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Type1Font_GlyphsOnlyInRedactedText_LeaveTheFont(bool pfb)
    {
        var pdf = TestPdf.Simple(1);
        var sample = Fonts.Type1Sample.Build();
        static byte[] Segment(byte type, ReadOnlySpan<byte> payload) =>
            [0x80, type, (byte)payload.Length, (byte)(payload.Length >> 8), (byte)(payload.Length >> 16), (byte)(payload.Length >> 24), .. payload];
        var embedded = pfb
            ? [.. Segment(1, sample.Data.AsSpan(0, sample.Clear)), .. Segment(2, sample.Data.AsSpan(sample.Clear, sample.Encrypted)),
               .. Segment(1, sample.Data.AsSpan(sample.Clear + sample.Encrypted)), 0x80, 3]
            : sample.Data;
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Length1", PdfInteger.Of(sample.Clear)), ("Length2", PdfInteger.Of(sample.Encrypted)),
            ("Length3", PdfInteger.Of(sample.Data.Length - sample.Clear - sample.Encrypted))), embedded));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Probe")), ("Flags", PdfInteger.Of(4)),
            ("FontBBox", TestPdf.Box(0, 0, 1000, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(800)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile", TestPdf.Ref(file))));
        var probe = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("ABCDEF+Probe")),
            ("FirstChar", PdfInteger.Of(65)), ("LastChar", PdfInteger.Of(67)), ("Widths", new PdfArray([PdfInteger.Of(600), PdfInteger.Of(600), PdfInteger.Of(600)])),
            ("FontDescriptor", TestPdf.Ref(descriptor))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("PR"), TestPdf.Ref(probe));
        pdf.Replace(4, TestPdf.FlateStream("BT /PR 40 Tf 72 600 Td (A) Tj 100 0 Td (B) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(66, 590, 120, 650))));
        var dict = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("PR"))!;
        var stream = dict.GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile"))!;
        var program = Core.Fonts.Type1Font.Parse(after.GetStreamData(stream));
        Assert.False(program.HasGlyph("A"));
        Assert.True(program.HasGlyph("B"));
        Assert.True(program.Encoding['A'] is "" or ".notdef");
        Assert.Equal(after.GetStreamData(stream).Length, (int)stream.Dictionary.GetInteger(PdfName.Get("Length1"))! +
            (int)stream.Dictionary.GetInteger(PdfName.Get("Length2"))! + (int)stream.Dictionary.GetInteger(PdfName.Get("Length3"))!);
    }

    /// <summary>
    /// An embedded TrueType font, a word redacted whose letters G, h, i, m occur nowhere else: afterwards the font tells nothing of them — no
    /// outline, no cmap entry, no ToUnicode entry, no name in /Differences, no width — while T, e, x, t stay as they were
    /// (docs/FEHLERGEDAECHTNIS.md, No. 169).
    /// </summary>
    [Fact]
    public void TrueTypeFont_LettersOnlyInRedactedText_LeaveTheFont()
    {
        var pdf = TestPdf.Simple(1);
        var ttf = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Length1", PdfInteger.Of(ttf.Length))), ttf));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+LiberationSans")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file))));
        const string letters = " GTehimtx";
        var font = Core.Fonts.OpenTypeFont.Parse(ttf);
        var widths = new PdfArray([.. Enumerable.Range(32, 91).Select(c => (PdfObject)PdfInteger.Of(letters.Contains((char)c) ? 600 : 0))]);
        var differences = new PdfArray();
        foreach (var c in letters.Where(c => c != ' '))
        {
            differences.Add(PdfInteger.Of(c));
            differences.Add(TestPdf.N(c.ToString()));
        }
        var unicode = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange " +
            letters.Length + " beginbfchar " + string.Concat(letters.Select(c => $"<{(int)c:X2}> <{(int)c:X4}> ")) + "endbfchar endcmap CMapName currentdict /CMap defineresource pop end end")));
        var tt = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("ABCDEF+LiberationSans")),
            ("FirstChar", PdfInteger.Of(32)), ("LastChar", PdfInteger.Of(122)), ("Widths", widths), ("FontDescriptor", TestPdf.Ref(descriptor)),
            ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("BaseEncoding", TestPdf.N("WinAnsiEncoding")), ("Differences", differences))),
            ("ToUnicode", TestPdf.Ref(unicode))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("TT"), TestPdf.Ref(tt));
        pdf.Replace(4, TestPdf.FlateStream("BT /TT 24 Tf 72 600 Td (Geheim Text) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var data = WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim")));
        using var after = PdfDocument.Open(data);
        var dict = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("TT"))!;
        var program = Core.Fonts.OpenTypeFont.Parse(after.GetStreamData(dict.GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile2"))!));
        var map = Core.Pdf.Fonts.PdfFont.ReadToUnicode(dict, after)!;
        var names = dict.GetDictionary(PdfName.Get("Encoding"))!.GetArray(PdfName.Get("Differences"))!;
        var nameList = Enumerable.Range(0, names.Count).Select(i => names.Get(i)).OfType<PdfName>().Select(n => n.Value).ToList();
        foreach (var c in "Ghim")
        {
            Assert.Equal(0, program.GlyphForUnicode(c));
            Assert.True(program.Outline(font.GlyphForUnicode(c)).IsEmpty, $"Umriss {c}");
            Assert.Null(map.ToUnicode(c, 1));
            Assert.DoesNotContain(c.ToString(), nameList);
            Assert.Equal(0, dict.GetArray(PdfName.Get("Widths"))!.GetNumber(c - 32));
        }
        foreach (var c in "Text")
        {
            Assert.Equal(font.GlyphForUnicode(c), program.GlyphForUnicode(c));
            Assert.Equal(c.ToString(), map.ToUnicode(c, 1));
            Assert.Contains(c.ToString(), nameList);
        }
        Assert.Contains("Text", Read(after, after.Pages[0]).Text);
    }

    /// <summary>
    /// The embedded subset of Liberation Sans with the letters " GTehimtx" (ToUnicode, /Differences, widths), as an object of
    /// <paramref name="pdf"/>; returns its number (for the tests of No. 700).
    /// </summary>
    private static int LiberationSubset(TestPdf pdf)
    {
        var ttf = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Length1", PdfInteger.Of(ttf.Length))), ttf));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+LiberationSans")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file))));
        const string letters = " GTehimtx";
        var widths = new PdfArray([.. Enumerable.Range(32, 91).Select(c => (PdfObject)PdfInteger.Of(letters.Contains((char)c) ? 600 : 0))]);
        var differences = new PdfArray();
        foreach (var c in letters.Where(c => c != ' '))
        {
            differences.Add(PdfInteger.Of(c));
            differences.Add(TestPdf.N(c.ToString()));
        }
        var unicode = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange " +
            letters.Length + " beginbfchar " + string.Concat(letters.Select(c => $"<{(int)c:X2}> <{(int)c:X4}> ")) + "endbfchar endcmap CMapName currentdict /CMap defineresource pop end end")));
        return pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("ABCDEF+LiberationSans")),
            ("FirstChar", PdfInteger.Of(32)), ("LastChar", PdfInteger.Of(122)), ("Widths", widths), ("FontDescriptor", TestPdf.Ref(descriptor)),
            ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("BaseEncoding", TestPdf.N("WinAnsiEncoding")), ("Differences", differences))),
            ("ToUnicode", TestPdf.Ref(unicode))));
    }

    /// <summary>
    /// The same subset set by a graphics state (/Font in an ExtGState, ISO 32000-2, 8.4.5) instead of Tf: the letters only in the redacted
    /// word leave it as well (docs/FEHLERGEDAECHTNIS.md, No. 700; before, such a font stayed whole — what codes it showed was not told).
    /// </summary>
    [Fact]
    public void FontSetByGraphicsState_LettersOnlyInRedactedText_LeaveTheFont()
    {
        var pdf = TestPdf.Simple(1);
        var tt = LiberationSubset(pdf);
        var resources = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!;
        resources.Set(PdfName.Get("ExtGState"), TestPdf.Dict(("GS0", TestPdf.Dict(("Type", TestPdf.N("ExtGState")),
            ("Font", new PdfArray([TestPdf.Ref(tt), PdfInteger.Of(24)]))))));
        pdf.Replace(4, TestPdf.FlateStream("/GS0 gs BT 72 600 Td (Geheim Text) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var data = WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim")));
        using var after = PdfDocument.Open(data);
        var state = after.Pages[0].Resources!.GetDictionary(PdfName.Get("ExtGState"))!.GetDictionary(PdfName.Get("GS0"))!;
        var dict = (PdfDictionary)state.GetArray(PdfName.Get("Font"))!.Get(0)!;
        var map = Core.Pdf.Fonts.PdfFont.ReadToUnicode(dict, after)!;
        foreach (var c in "Ghim") Assert.Null(map.ToUnicode(c, 1));
        foreach (var c in "Text") Assert.Equal(c.ToString(), map.ToUnicode(c, 1));
        Assert.Contains("Text", Read(after, after.Pages[0]).Text);
    }

    /// <summary>
    /// The same subset is also a font of the form (/DR), and a field's value is „Tim“: letters only in the redacted word leave it, but those
    /// of the value stay — a program writing the value anew with the font still finds them (No. 700; before, every form font stayed whole,
    /// with the letters of the redacted word).
    /// </summary>
    [Fact]
    public void FormFontSubset_LettersOnlyInRedactedText_LeaveIt_ThoseOfFieldValuesStay()
    {
        var pdf = TestPdf.Simple(1);
        var tt = LiberationSubset(pdf);
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("TT"), TestPdf.Ref(tt));
        var field = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Tx")), ("T", new PdfString("Name"u8.ToArray())), ("V", new PdfString("Tim"u8.ToArray())),
            ("DA", new PdfString("/TT 12 Tf 0 g"u8.ToArray()))));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(field)])),
            ("DR", TestPdf.Dict(("Font", TestPdf.Dict(("TT", TestPdf.Ref(tt))))))));
        pdf.Replace(4, TestPdf.FlateStream("BT /TT 24 Tf 72 600 Td (Geheim Text) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var data = WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim")));
        using var after = PdfDocument.Open(data);
        var dict = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("TT"))!;
        var map = Core.Pdf.Fonts.PdfFont.ReadToUnicode(dict, after)!;
        foreach (var c in "Gh") Assert.Null(map.ToUnicode(c, 1));       // only in the redacted word
        foreach (var c in "im") Assert.Equal(c.ToString(), map.ToUnicode(c, 1));   // in the value „Tim“ as well
        foreach (var c in "Text") Assert.Equal(c.ToString(), map.ToUnicode(c, 1));
    }

    /// <summary>
    /// A font that is not embedded, with widths for every code: they are the metrics of the system font and tell nothing of the letters
    /// redacted. They stay — cleared, the widths left were all equal and other readers drew the page in a fixed-width substitute
    /// (docs/FEHLERGEDAECHTNIS.md, No. 243).
    /// </summary>
    [Fact]
    public void NotEmbeddedFont_CompleteWidths_Stay()
    {
        var pdf = TestPdf.Simple(1);
        // one code without a width, as the encoding leaves it empty
        var widths = new PdfArray([.. Enumerable.Range(32, 91).Select(c => (PdfObject)PdfInteger.Of(c == 96 ? 0 : c is >= 'a' and <= 'z' ? 500 : 650))]);
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("SystemSans")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80))));
        var tt = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("SystemSans")),
            ("FirstChar", PdfInteger.Of(32)), ("LastChar", PdfInteger.Of(122)), ("Widths", widths), ("FontDescriptor", TestPdf.Ref(descriptor)),
            ("Encoding", TestPdf.N("WinAnsiEncoding"))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("TT"), TestPdf.Ref(tt));
        pdf.Replace(4, TestPdf.FlateStream("BT /TT 24 Tf 72 600 Td (Geheim Text) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim"))));
        var written = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("TT"))!.GetArray(PdfName.Get("Widths"))!;
        Assert.Equal(91, written.Count);
        for (var i = 0; i < 91; i++) Assert.Equal(widths.GetNumber(i), written.GetNumber(i));
        Assert.DoesNotContain("Geheim", Read(after, after.Pages[0]).Text);
        Assert.Contains("Text", Read(after, after.Pages[0]).Text);
    }

    /// <summary>
    /// A Type 3 font whose widths are an object of their own: after a glyph is redacted the page uses a copy of the font that shares them —
    /// the widths of the characters left stay (they counted for the original font only, which shows nothing any more, and were cleared;
    /// the rest of the line moved; docs/FEHLERGEDAECHTNIS.md, No. 169).
    /// </summary>
    [Fact]
    public void Type3SharedWidths_StayForTheCopy()
    {
        var pdf = TestPdf.Simple(1);
        var tall = pdf.Add(TestPdf.FlateStream("600 0 0 0 600 700 d1 0 0 600 700 re f"u8.ToArray()));
        var widths = pdf.Add(new PdfArray([.. Enumerable.Repeat<PdfObject>(PdfInteger.Of(600), 49)]));
        var t3 = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("FontBBox", TestPdf.Box(0, 0, 600, 700)),
            ("FontMatrix", TestPdf.Box(0.001, 0, 0, 0.001, 0, 0)), ("CharProcs", TestPdf.Dict(("H", TestPdf.Ref(tall)), ("x", TestPdf.Ref(tall)))),
            ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", new PdfArray([PdfInteger.Of(72), TestPdf.N("H"), PdfInteger.Of(120), TestPdf.N("x")])))),
            ("FirstChar", PdfInteger.Of(72)), ("LastChar", PdfInteger.Of(120)), ("Widths", TestPdf.Ref(widths))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("T3"), TestPdf.Ref(t3));
        pdf.Replace(4, TestPdf.FlateStream("BT /T3 20 Tf 72 400 Td (Hxxx) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var before = Read(doc, doc.Pages[0]).Glyphs.Where(g => g.Char == "x").Select(g => g.X).ToList();
        using var after = PdfDocument.Open(WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "H"))));
        var now = Read(after, after.Pages[0]).Glyphs.Where(g => g.Char == "x").Select(g => g.X).ToList();
        Assert.Equal(3, now.Count);
        for (var i = 0; i < 3; i++) Assert.Equal(before[i], now[i], 1);
    }

    /// <summary>
    /// A Type 3 font with its encoding, widths and ToUnicode written into the font dictionary itself, one letter redacted that occurs nowhere
    /// else: the copy the page uses names it nowhere any more — the copy shared the direct /Differences of the original, which only the
    /// original's scrubbing changed (docs/FEHLERGEDAECHTNIS.md, No. 181).
    /// </summary>
    [Fact]
    public void Type3Copy_DirectEncoding_NamesNothingRemoved()
    {
        var pdf = TestPdf.Simple(1);
        var box = pdf.Add(TestPdf.FlateStream("600 0 0 0 600 700 d1 0 0 600 700 re f"u8.ToArray()));
        var unicode = new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 12 dict begin begincmap " +
            "1 begincodespacerange <00> <FF> endcodespacerange 2 beginbfchar <48> <0048> <51> <0051> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end"));
        var t3 = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("FontBBox", TestPdf.Box(0, 0, 600, 700)),
            ("FontMatrix", TestPdf.Box(0.001, 0, 0, 0.001, 0, 0)), ("CharProcs", TestPdf.Dict(("H", TestPdf.Ref(box)), ("Q", TestPdf.Ref(box)))),
            ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", new PdfArray([PdfInteger.Of(72), TestPdf.N("H"), PdfInteger.Of(81), TestPdf.N("Q")])))),
            ("FirstChar", PdfInteger.Of(72)), ("LastChar", PdfInteger.Of(81)), ("Widths", new PdfArray([.. Enumerable.Repeat<PdfObject>(PdfInteger.Of(600), 10)])),
            ("ToUnicode", unicode)));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("T3"), TestPdf.Ref(t3));
        pdf.Replace(4, TestPdf.FlateStream("BT /T3 20 Tf 72 400 Td (HHQ) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Q"))));
        var font = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("T3"))!;
        var differences = font.GetDictionary(PdfName.Get("Encoding"))!.GetArray(PdfName.Get("Differences"))!;
        Assert.DoesNotContain("Q", Enumerable.Range(0, differences.Count).Select(i => differences.Get(i)).OfType<PdfName>().Select(n => n.Value));
        Assert.Null(Core.Pdf.Fonts.PdfFont.ReadToUnicode(font, after)?.ToUnicode('Q', 1));
        Assert.Equal(0, font.GetArray(PdfName.Get("Widths"))!.GetNumber('Q' - 72));
        Assert.Equal("HH", Read(after, after.Pages[0]).Text.Trim());
    }

    [Fact]
    public void Image_PixelsBlanked_OnlyUnderTheArea()
    {
        using var doc = PdfDocument.Open(ImageAndFormPage());
        var page = doc.Pages[0];
        var result = Redactor.Apply(doc, page, [new PdfRectangle(100, 300, 200, 400)]);   // left half of the image
        Assert.Equal(1, result.BlankedImages);
        var xobjects = result.Resources.GetDictionary(PdfName.Get("XObject"))!;
        var blanked = xobjects.Entries.Select(e => xobjects.Get(e.Key)).OfType<PdfStream>()
            .Single(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image" && s.RawData is not null);
        var data = doc.GetStreamData(blanked);
        // a pixel of 10 pt is refined by whole factors to at most 0.25 pt, so that the black ends at the bar
        var (w, h) = ((int)blanked.Dictionary.GetInteger(PdfName.Get("Width"))!, (int)blanked.Dictionary.GetInteger(PdfName.Get("Height"))!);
        var (kx, ky) = (w / 20, h / 10);
        Assert.Equal((20 * kx, 10 * ky, w * h * 3), (w, h, data.Length));
        Assert.True(kx >= 40 && ky >= 40);
        for (var x = 0; x < 20; x++)
        {
            var at = ((5 * ky + ky / 2) * w + x * kx + kx / 2) * 3;
            if (x < 10) Assert.Equal((0, 0, 0), (data[at], data[at + 1], data[at + 2]));        // redacted
            else Assert.Equal((x * 12, 200, 40), (data[at], data[at + 1], data[at + 2]));        // unchanged
        }
        // The original image in the file is untouched (other pages may keep using it)
        Assert.Equal(200, doc.GetStreamData((PdfStream)page.Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im0")))[1]);
    }

    [Fact]
    public void Form_OnlyThisCallChanges()
    {
        using var doc = PdfDocument.Open(ImageAndFormPage());
        var page = doc.Pages[0];
        var text = Core.Pdf.Text.PageText.Create(PageInterpreter.Build(doc, page).Text);
        var area = text.Find("Geheimer").Select(r => Assert.Single(ViewModelsShim.Areas(text, r))).OrderByDescending(a => a.Bottom).First();
        Assert.True(area.Bottom > 500);
        var result = Redactor.Apply(doc, page, [area]);
        Assert.Equal("Geheimer", result.RemovedText);
        var after = Read(doc, PageOf(page, result)).Text;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(after, "Geheimer"));   // only the lower occurrence stays
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(after, "Formtext").Count);
    }

    [Fact]
    public void Areas_FollowTheRectangle()
    {
        var element = RedactionElement.FromPageAreas([new PdfRectangle(100, 700, 300, 714), new PdfRectangle(100, 680, 180, 694)]);
        Assert.Equal(new PdfRectangle(100, 680, 300, 714), element.Rect);
        Assert.Equal([new PdfRectangle(100, 700, 300, 714), new PdfRectangle(100, 680, 180, 694)], element.PageAreas.Select(Round));
        var moved = ElementAppearance.Moved(element, element.Rect with { Left = 150, Right = 350 });
        Assert.Equal(new PdfRectangle(150, 700, 350, 714), Round(((RedactionElement)moved).PageAreas.First()));

        static PdfRectangle Round(PdfRectangle r) => new(Math.Round(r.Left, 6), Math.Round(r.Bottom, 6), Math.Round(r.Right, 6), Math.Round(r.Top, 6));
    }

    [Fact]
    public void Saved_AsRedactAnnotation_ComesBack_FlattenKeepsItAMark()
    {
        using var original = PdfDocument.Open(TextPage("Max Mustermann, Musterstadt"));
        var element = RedactionElement.FromPageAreas([new PdfRectangle(70, 696, 160, 712), new PdfRectangle(70, 676, 120, 692)]);
        var item = PageItem.From(original)[0].WithElements([element]);
        foreach (var flatten in (ReadOnlySpan<bool>)[false, true])
        {
            using var output = new MemoryStream();
            PdfWriter.Write(original, [item], output, new PdfSaveOptions { Flatten = flatten });
            using var saved = PdfDocument.Open(output.ToArray());
            var annot = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0).AsDictionary()!;
            Assert.Equal("Redact", annot.GetName(PdfName.Subtype)!.Value);
            Assert.Equal(16, annot.GetArray(PdfName.Get("QuadPoints"))!.Count);   // two areas
            var back = Assert.IsType<RedactionElement>(Assert.Single(PageItem.From(saved)[0].Elements));
            Assert.Equal(2, back.Areas.Count);
            Assert.Equal(element.Rect.Left, back.Rect.Left, 3);
            // Only marked: the text is still there (only applying removes it), also after flattening
            Assert.Contains("Mustermann", string.Concat(PageInterpreter.Build(saved, saved.Pages[0], withoutOwnElements: true).Text.Select(t => t.Text)));
        }
    }

    /// <summary>
    /// <see cref="ImageAndFormPage"/> with a comment (including popup), two form fields (one under the area) and
    /// a structure element with alternative text for the page.
    /// </summary>
    private static byte[] AnnotatedPage()
    {
        var pdf = ImageAndFormPdf();
        const int page = 5;
        var popup = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Popup")), ("Rect", TestPdf.Box(400, 600, 550, 700))));
        var note = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Text")), ("Rect", TestPdf.Box(300, 610, 320, 630)),
            ("Contents", new PdfString("Geheime Notiz"u8.ToArray())), ("Popup", TestPdf.Ref(popup))));
        ((PdfDictionary)pdf.Get(popup)).Set(PdfName.Parent, TestPdf.Ref(note));
        var secret = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Tx")),
            ("T", new PdfString("Konto"u8.ToArray())), ("V", new PdfString("DE12 3456"u8.ToArray())), ("Rect", TestPdf.Box(330, 610, 370, 630))));
        var open = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Tx")),
            ("T", new PdfString("Ort"u8.ToArray())), ("V", new PdfString("Musterstadt"u8.ToArray())), ("Rect", TestPdf.Box(72, 100, 200, 120))));
        ((PdfDictionary)pdf.Get(page)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(note), TestPdf.Ref(popup), TestPdf.Ref(secret), TestPdf.Ref(open)]));
        var element = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Figure")), ("Pg", TestPdf.Ref(page)),
            ("Alt", new PdfString("Vertraulicher Ersatztext"u8.ToArray()))));
        var tree = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", TestPdf.Ref(element))));
        ((PdfDictionary)pdf.Get(element)).Set(PdfName.Get("P"), TestPdf.Ref(tree));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(secret), TestPdf.Ref(open)]))));
        catalog.Set(PdfName.Get("StructTreeRoot"), TestPdf.Ref(tree));
        return pdf.Build();
    }

    /// <summary>Collects everything stored in the file: strings and decoded streams (Latin-1).</summary>
    internal static string Everything(PdfDocument doc)
    {
        var all = new System.Text.StringBuilder();
        void Walk(PdfObject obj, int depth)
        {
            if (depth > 32) return;
            switch (obj)
            {
                case PdfString s: all.Append(System.Text.Encoding.Latin1.GetString(s.Bytes)).Append('\n'); break;
                case PdfArray a: for (var i = 0; i < a.Count; i++) Walk(a[i], depth + 1); break;
                case PdfDictionary d: foreach (var (_, v) in d.Entries) Walk(v, depth + 1); break;
                case PdfStream st:
                    Walk(st.Dictionary, depth + 1);
                    all.Append(System.Text.Encoding.Latin1.GetString(doc.GetStreamData(st))).Append('\n');
                    break;
            }
        }
        for (var n = 1; n <= doc.ObjectCount + 8; n++) Walk(doc.GetObject(n), 0);
        return all.ToString();
    }

    [Fact]
    public void Applied_OnSave_NothingUnderTheAreaStaysInTheFile()
    {
        using var original = PdfDocument.Open(AnnotatedPage());
        Assert.Contains("Geheime Notiz", Everything(original));
        var upper = new PdfRectangle(70, 600, 380, 640);   // form text at the top, comment, field "Konto"
        var image = new PdfRectangle(100, 300, 200, 400);   // left half of the image
        var item = PageItem.From(original)[0];
        item = item.WithElements([.. item.Elements, RedactionElement.FromPageAreas([upper, image])]);

        using var output = new MemoryStream();
        PdfWriter.Write(original, [item], output, new PdfSaveOptions { ApplyRedactions = true });
        using var saved = PdfDocument.Open(output.ToArray());
        var everything = Everything(saved);
        foreach (var secret in (string[])["Geheime Notiz", "DE12 3456", "Vertraulicher Ersatztext"])
            Assert.DoesNotContain(secret, everything);

        var page = saved.Pages[0];
        var text = Read(saved, page).Text;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "Geheimer Formtext"));   // the lower occurrence stays
        // Consumed: no mark left; the field outside stays and is the only one left in the form
        Assert.Equal("Ort", FormField.Of(Assert.IsType<ForeignAnnotation>(Assert.Single(PageItem.From(saved)[0].Elements)))!.Name);   // only the field outside
        var annot = Assert.Single(Enumerable.Range(0, page.Dictionary.GetArray(PdfName.Get("Annots"))!.Count)
            .Select(i => page.Dictionary.GetArray(PdfName.Get("Annots"))!.Get(i).AsDictionary()!));
        Assert.Equal("Ort", annot.GetString(PdfName.Get("T"))!.ToText());
        var fields = saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!;
        Assert.Equal("Ort", Assert.Single(Enumerable.Range(0, fields.Count).Select(i => fields.Get(i).AsDictionary()!)).GetString(PdfName.Get("T"))!.ToText());
        Assert.Contains("Musterstadt", everything);
        // Only one image in the file: the redacted one (otherwise the original would remain unused beside it)
        var images = Enumerable.Range(1, saved.ObjectCount + 8).Select(saved.GetObject).OfType<PdfStream>()
            .Where(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image").ToList();
        var single = Assert.Single(images);
        var data = saved.GetStreamData(single);
        var w = (int)single.Dictionary.GetInteger(PdfName.Get("Width"))!;
        Assert.Equal((0, 0, 0), (data[0], data[1], data[2]));
        Assert.Equal(200, data[(w - 1) * 3 + 1]);
        // Black bars in the content
        Assert.Contains("q 0 g\n70 600 310 40 re\n", System.Text.Encoding.Latin1.GetString(saved.GetStreamData((PdfStream)page.Dictionary.Get(PdfName.Contents))));
    }

    /// <summary>
    /// Placed tightly at high zoom: a bar only around the point in "1.234,56" (covering barely a sixth of the line height)
    /// removes exactly the point; a bar only over the upper half of the digits leaves the neighbours in place.
    /// </summary>
    [Fact]
    public void TightBar_AroundSmallCharacter_RemovesExactlyIt()
    {
        using var doc = PdfDocument.Open(TextPage("Betrag 1.234,56 EUR"));
        var page = doc.Pages[0];
        var text = Core.Pdf.Text.PageText.Create(PageInterpreter.Build(doc, page).Text);
        var range = Assert.Single(text.Find("."));
        var cell = Assert.Single(text.Quads(range)).Bounds;   // cell of the point (full line height)
        // The point in Helvetica 12: about 1.3 pt high, on the baseline (700)
        var bar = new PdfRectangle(cell.Left + 0.3, 699.6, cell.Right - 0.3, 701.8);
        Assert.True(bar.Height < (cell.Bottom - cell.Top) / 4);
        var result = Redactor.Apply(doc, page, [bar]);
        Assert.Equal(".", result.RemovedText);
        Assert.Contains("1234,56 EUR", Read(doc, PageOf(page, result)).Text);
    }

    [Fact]
    public void RotatedText_OnlyLettersUnderTheBarGo()
    {
        // Diagonal lettering (45°): the bounding box of each letter would be almost twice as large as the letter itself
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 40 Tf 0.7071 0.7071 -0.7071 0.7071 100 300 Tm (ENTWURF) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var result = Redactor.Apply(doc, page, [new PdfRectangle(50, 345, 400, 360)]);
        Assert.InRange(result.RemovedGlyphs, 1, 2);
        Assert.Equal(7 - result.RemovedGlyphs, Read(doc, PageOf(page, result)).Text.Length);
    }

    [Fact]
    public void Applied_PageWithoutRedaction_StaysAsItWas()
    {
        using var original = PdfDocument.Open(TextPage("Max Mustermann"));
        using var plain = new MemoryStream();
        using var applied = new MemoryStream();
        PdfWriter.Write(original, plain);
        PdfWriter.Write(original, applied, new PdfSaveOptions { ApplyRedactions = true });
        using var a = PdfDocument.Open(plain.ToArray());
        using var b = PdfDocument.Open(applied.ToArray());
        Assert.Equal(a.ObjectCount, b.ObjectCount);
        Assert.Equal(a.GetStreamData((PdfStream)a.Pages[0].Dictionary.Get(PdfName.Contents)), b.GetStreamData((PdfStream)b.Pages[0].Dictionary.Get(PdfName.Contents)));
    }

    [Fact]
    public void SyncFolder_Recognized()
    {
        var root = Path.GetTempPath();
        Assert.True(Core.Platform.SyncFolder.Contains(Path.Combine(root, "OneDrive - Musterfirma", "Desktop", "a.pdf")));
        Assert.True(Core.Platform.SyncFolder.Contains(Path.Combine(root, "Dropbox", "a.pdf")));
        Assert.False(Core.Platform.SyncFolder.Contains(Path.Combine(root, "Daten", "Dropboxen-Liste.pdf")));
    }

    /// <summary>
    /// A frame (a stroked rectangle) and a large box, each 96 % of their bounds under a bar: what shows of them beside the bar stays — removed
    /// whole, the frame lost its side beside the bar (docs/FEHLERGEDAECHTNIS.md, No. 261). A small square 1 pt beside a bar keeps that strip
    /// (20 pt long: visible, No. 290) and loses the rest from the file; a short stroke wholly under a bar still goes whole.
    /// </summary>
    [Fact]
    public void FrameAndBoxMostlyUnderABar_TheirRestBesideItStays()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(
            "1 0 0 RG 4 w 100 600 100 80 re S 1 0 0 rg 100 300 100 200 re f 0 0 1 rg 300 600 20 20 re f 0 0 1 RG 0.5 w 320 300 m 350 304 l S"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var redacted = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(90, 590, 196, 690)), (0, new PdfRectangle(90, 290, 196, 510)),
            (0, new PdfRectangle(301, 590, 400, 630)), (0, new PdfRectangle(310, 290, 360, 310))));
        using var bitmap = ContentImageTests.Render(redacted, redacted.Pages[0]);
        Assert.True(ContentImageTests.Red(ContentImageTests.At(bitmap, redacted.Pages[0], 200, 640)), "die rechte Seite des Rahmens");
        Assert.True(ContentImageTests.Red(ContentImageTests.At(bitmap, redacted.Pages[0], 198, 400)), "der Rest der Fläche");
        // the small square: 95 % under the bar — its strip beside it (1 × 20 pt) stays, its square under the bar leaves the file
        var strip = ContentImageTests.At(bitmap, redacted.Pages[0], 300.5, 610);
        Assert.True(strip.Blue > 150 && strip.Red < 150, $"Streifen neben dem Balken: {strip}");
        // a short stroke wholly under a bar: gone from the file, not a point of it left under the bar (cut, it kept some)
        var content = System.Text.Encoding.Latin1.GetString(redacted.GetStreamData((PdfStream)redacted.Pages[0].Dictionary.Get(PdfName.Contents)!));
        Assert.DoesNotContain("300 600 20 20 re", content);
        Assert.DoesNotContain("320 300 m", content);
        Assert.DoesNotContain("350 304", content);
    }

    /// <summary>
    /// A thin rule 95 % under a bar, 10 pt of it beside it: that piece stays — it has almost no area (1 pt²), and the rule went whole
    /// (docs/FEHLERGEDAECHTNIS.md, No. 290); its part under the bar leaves the file. A short stroke sticking out 1 pt still goes whole.
    /// </summary>
    [Fact]
    public void ThinRuleMostlyUnderABar_ItsPieceBesideItStays()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("0 0 1 RG 0.5 w 100 200 m 300 200 l S 0 0 1 RG 0.5 w 100 400 m 191 400 l S"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var redacted = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(90, 190, 290, 210)), (0, new PdfRectangle(90, 390, 190, 410))));
        using var bitmap = ContentImageTests.Render(redacted, redacted.Pages[0]);
        var beside = ContentImageTests.At(bitmap, redacted.Pages[0], 296, 200);
        Assert.True(beside.Blue > 150 && beside.Red < 150, $"das Stück neben dem Balken: {beside}");
        var content = System.Text.Encoding.Latin1.GetString(redacted.GetStreamData((PdfStream)redacted.Pages[0].Dictionary.Get(PdfName.Contents)!));
        Assert.DoesNotContain("100 200 m", content);   // the part under the bar is cut away
        Assert.DoesNotContain("100 400 m", content);   // the short one: gone whole
        Assert.DoesNotContain("191 400", content);
    }

    /// <summary>
    /// A stencil mask whose data ends inside a row (its predictor counts 8-bit columns, the image 1-bit rows), a bar over one corner: the rows
    /// the data lacks stay unpainted as the display shows them — filled with zeros they painted a black line across a barcode
    /// (docs/FEHLERGEDAECHTNIS.md, No. 259).
    /// </summary>
    [Fact]
    public void StencilMaskWithShortData_BarOverACorner_NoLineWhereDataEnds()
    {
        var pdf = TestPdf.Simple(1);
        // 16 × 8 pixels = 2 bytes a row; the predictor gives 13 bytes (all set: nothing painted) — row 6 half, row 7 not at all
        var mask = pdf.Add(TestPdf.FlateStream([0, .. Enumerable.Repeat((byte)0xFF, 13)], TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(16)), ("Height", PdfInteger.Of(8)), ("ImageMask", PdfBoolean.True),
            ("DecodeParms", TestPdf.Dict(("Predictor", PdfInteger.Of(15)), ("Columns", PdfInteger.Of(13)))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("M", TestPdf.Ref(mask))))));
        pdf.Replace(4, TestPdf.FlateStream("0 g q 160 0 0 80 100 500 cm /M Do Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var before = ContentImageTests.Render(doc, doc.Pages[0]);
        Assert.Equal(SKColors.White, ContentImageTests.At(before, doc.Pages[0], 200, 505));
        using var redacted = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(100, 560, 140, 580))));
        using var after = ContentImageTests.Render(redacted, redacted.Pages[0]);
        Assert.Equal(SKColors.White, ContentImageTests.At(after, redacted.Pages[0], 200, 505));   // the row the data lacks
        Assert.Equal(SKColors.White, ContentImageTests.At(after, redacted.Pages[0], 230, 515));   // the half row (its second byte is missing)
    }

    /// <summary>
    /// Text reaching beyond the bottom of the page, a bar over the sliver that shows: all anybody sees of it lies under the bar, so it goes —
    /// it stayed because most of it lay beside the page (docs/FEHLERGEDAECHTNIS.md, No. 258). The same text beside the bar stays.
    /// </summary>
    [Fact]
    public void TextBesideThePage_BarOverWhatShows_Removed()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 12 Tf 72 -8 Td (Unten) Tj ET BT /F1 12 Tf 300 -8 Td (Daneben) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Contains("Unten", Read(doc, doc.Pages[0]).Text);
        using var redacted = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(60, 0, 150, 2))));
        var text = Read(redacted, redacted.Pages[0]).Text;
        Assert.DoesNotContain("U", text);
        Assert.Contains("Daneben", text);
    }

    /// <summary>
    /// The check after redacting decides by the drawn shape, not by the text alone: a glyph that a ToUnicode entry reads as a space but that
    /// draws a letter, left under a bar, makes the check refuse the page; a real space under a bar does not (docs/FEHLERGEDAECHTNIS.md,
    /// No. 438). Before, every character read as a space was passed over, and a letter under a bar would have been saved unnoticed.
    /// </summary>
    [Fact]
    public void SelfCheck_GlyphReadAsSpaceThatDrawsALetter_UnderTheBar_NothingSaved()
    {
        static PdfDocument Page(string content)
        {
            var pdf = TestPdf.Simple(1);
            var unicode = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 12 dict begin begincmap " +
                "1 begincodespacerange <00> <FF> endcodespacerange 2 beginbfchar <41> <0020> <20> <0020> endbfchar endcmap CMapName currentdict " +
                "/CMap defineresource pop end end")));
            ((PdfDictionary)pdf.Get(3)).Set(PdfName.Get("ToUnicode"), TestPdf.Ref(unicode));
            pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content)));
            return PdfDocument.Open(pdf.Build());
        }
        // the bar over the place of the character, as if the page had come out of the rewriter with it still there
        List<Core.Pdf.Graphics.RectD> bar = [Core.Pdf.Graphics.RectD.FromLtrb(90, 580, 160, 680)];

        using var letter = Page("BT /F1 48 Tf 100 600 Td (A) Tj ET");
        Assert.Equal(" ", Assert.Single(Read(letter, letter.Pages[0]).Glyphs).Char);   // the display reads it as a space
        Assert.Throws<Core.Pdf.PdfException>(() => Redactor.Verify(letter, letter.Pages[0], letter.Pages[0].Dictionary, letter.Pages[0].Resources!, bar));

        using var space = Page("BT /F1 48 Tf 100 600 Td ( ) Tj ET");
        Redactor.Verify(space, space.Pages[0], space.Pages[0].Dictionary, space.Pages[0].Resources!, bar);
    }

    private static PdfDocument PathPage(string content, Action<TestPdf>? more = null)
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content)));
        more?.Invoke(pdf);
        return PdfDocument.Open(pdf.Build());
    }

    /// <summary>Runs the check after redacting over the page as it is, the bar planned at (90, 580) – (160, 680).</summary>
    private static void CheckPaths(PdfDocument doc, List<Core.Pdf.Graphics.RectD>? edges = null)
    {
        List<Core.Pdf.Graphics.RectD> bar = [Core.Pdf.Graphics.RectD.FromLtrb(90, 580, 160, 680)];
        Redactor.Verify(doc, doc.Pages[0], doc.Pages[0].Dictionary, doc.Pages[0].Resources!, bar, bar, edges ?? bar);
    }

    /// <summary>
    /// The check after redacting finds the coordinates of a path left under the bar — a letter converted to curves, filled; a stroke; the
    /// same on a hidden layer; a lone „m“ at the end of a path; a path ended by „n“ that paints nothing. Before, the check looked at
    /// characters only: a fault in cutting paths would have left the shape of what was redacted in the file without a word
    /// (docs/FEHLERGEDAECHTNIS.md, No. 449, Analyse F 3a with G2).
    /// </summary>
    [Theory]
    [InlineData("100 600 m 120 660 l 140 600 l 130 600 l 120 630 l 110 600 l h f")]
    [InlineData("100 600 m 100 620 120 650 150 660 c f")]
    [InlineData("2 w 100 640 m 150 645 l S")]
    [InlineData("/OC /Aus BDC 100 600 m 120 660 l 140 600 l f EMC")]
    [InlineData("10 10 m 20 20 l 30 10 l 125 630 m f")]
    [InlineData("100 600 m 120 660 l 140 600 l h n")]
    public void SelfCheck_PathPointsLeftUnderTheBar_NothingSaved(string content)
    {
        using var doc = PathPage(content, pdf =>
        {
            var layer = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Aus"u8.ToArray()))));
            ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(layer)])),
                ("D", TestPdf.Dict(("OFF", new PdfArray([TestPdf.Ref(layer)]))))));
            ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("Properties"), TestPdf.Dict(("Aus", TestPdf.Ref(layer))));
        });
        Assert.Throws<Core.Pdf.PdfException>(() => CheckPaths(doc));
    }

    /// <summary>
    /// What cutting leaves is accepted: a fill cut along the edge (its points on the edge), a background larger than the bar (its corners
    /// beside it), a stroke ending its width short of the bar, a thick stroke over the narrow bar (not cut), points on the edge of a piece
    /// the bar grew by, and a fill cut under a huge matrix (No. 449).
    /// </summary>
    [Theory]
    [InlineData("50 600 m 90 600 l 90 650 l 90.0 650 l 50 650 l h f", false)]
    [InlineData("0 0 m 600 0 l 600 800 l 0 800 l h f", false)]
    [InlineData("2 w 40 640 m 87 640 l S", false)]
    [InlineData("200 w 120 500 m 120 750 l S", false)]
    [InlineData("150 600 m 160 600 l 170 600 l 170 650 l 160 650 l h f", true)]
    [InlineData("1000000 0 0 1000000 0 0 cm 0.00005 0.0006 m 0.00009 0.0006 l 0.00009 0.00065 l 0.00005 0.00065 l h f", false)]
    public void SelfCheck_WhatCuttingLeaves_Accepted(string content, bool piece)
    {
        using var doc = PathPage(content);
        List<Core.Pdf.Graphics.RectD> bar = [Core.Pdf.Graphics.RectD.FromLtrb(90, 580, 160, 680)];
        CheckPaths(doc, piece ? [.. bar, Core.Pdf.Graphics.RectD.FromLtrb(150, 580, 160, 680)] : null);
    }

    /// <summary>A page with an axial shading „Sh0“ over all of it (extended at both ends).</summary>
    private static PdfDocument ShadingPage(string content) => PathPage(content, pdf =>
    {
        var function = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(1, 0, 0)),
            ("C1", TestPdf.Box(0, 0, 1)), ("N", PdfInteger.Of(1)));
        var shading = pdf.Add(TestPdf.Dict(("ShadingType", PdfInteger.Of(2)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Coords", TestPdf.Box(0, 0, 600, 0)),
            ("Function", function), ("Extend", new PdfArray([PdfBoolean.True, PdfBoolean.True]))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("Shading"), TestPdf.Dict(("Sh0", TestPdf.Ref(shading))));
    });

    /// <summary>
    /// A path that only clips („W n“) left under the bar carries the shape of what was redacted as well — letters converted to curves and
    /// used as a clip, by either rule, also with nothing painted in it; and a shading painted under the bar because its clip does not cut
    /// the bar out. The check saw neither: it was given the points of painted and empty paths only, and shadings not at all
    /// (docs/FEHLERGEDAECHTNIS.md, No. 455, Analyse F 3a stage 2 with G2).
    /// </summary>
    [Theory]
    [InlineData("q 100 600 m 120 660 l 140 600 l 130 600 l 120 630 l 110 600 l h W n Q")]
    [InlineData("q 100 600 m 100 620 120 650 150 660 c h W* n 0 0 1 rg 0 0 595 842 re f Q")]
    [InlineData("q /Sh0 sh Q")]
    [InlineData("q 0 0 595 842 re W n /Sh0 sh Q")]
    public void SelfCheck_ClipOnlyPathShapedLikeLettersOrShadingUnderTheBar_NothingSaved(string content)
    {
        using var doc = ShadingPage(content);
        Assert.Throws<Core.Pdf.PdfException>(() => CheckPaths(doc));
    }

    /// <summary>
    /// What redacting writes around a shading or a clip passes the check: the bar cut out of the clip of a shading, 0.25 pt inside its edge
    /// (the seam that keeps a fine line of what lies beneath from showing, No. 172); a clip cut along the edge; a shading clipped to
    /// beside the bar (No. 455).
    /// </summary>
    [Theory]
    [InlineData("q -10000 -10000 m 10600 -10000 l 10600 10800 l -10000 10800 l h 90.25 580.25 m 159.75 580.25 l 159.75 679.75 l 90.25 679.75 l h W* n /Sh0 sh Q")]
    [InlineData("q 50 600 m 90 600 l 90 650 l 50 650 l h W n /Sh0 sh Q")]
    [InlineData("q 300 300 100 100 re W n /Sh0 sh Q")]
    public void SelfCheck_ShadingAndClipAsRedactingWritesThem_Accepted(string content)
    {
        using var doc = ShadingPage(content);
        CheckPaths(doc);
    }

    /// <summary>
    /// A path closed and followed by a lone „m“ at its start, as an office program writes a round clip (test file test file bug1771477:
    /// „… c h 225.2266 343.25 m W n“), under the bar: the page is saved and the point is gone. Cutting the clip removed the circle but
    /// wrote the lone „m“ again — the start of the redacted shape stayed in the file, and the check of stage 2 refused the page
    /// (docs/FEHLERGEDAECHTNIS.md, No. 455).
    /// </summary>
    [Theory]
    [InlineData("q 100 600 m 120 660 l 140 600 l h 100 600 m W n 0 g 0 0 595 842 re f Q")]
    [InlineData("q 0 g 100 600 m 120 660 l 140 600 l h 100 600 m f Q")]
    public void LoneMoveAfterAShapeUnderTheBar_Leaves(string content)
    {
        using var doc = PathPage(content + " BT /F1 12 Tf 100 300 Td (Bleibt) Tj ET");
        var saved = WithBars(doc, (0, new PdfRectangle(90, 590, 200, 680)));
        Assert.DoesNotContain("100 600", PageContent(saved));
    }

    /// <summary>
    /// A table cell's frame, only stroked, with a bar inside it: the frame stays as it was, without a clip around it — clipped, it was drawn
    /// on its own, and a reader that draws like lines together shaded the pixel where two frames meet differently (public test files
    /// example_006, example_048; docs/FEHLERGEDAECHTNIS.md, No. 464). A frame whose line the bar touches, and a filled cell, are still cut.
    /// </summary>
    [Fact]
    public void FrameOfACellWithABarInside_StaysUnclipped_OneTheBarTouchesIsCut()
    {
        using var doc = PathPage("0.8 w 2 J 0 0 1 RG 50 550 300 100 re S 1 0 0 RG 50 400 300 100 re S 0 1 0 rg 50 250 300 100 re f " +
                                 "BT /F1 12 Tf 100 590 Td (Geheim) Tj ET BT /F1 12 Tf 100 440 Td (Geheim) Tj ET BT /F1 12 Tf 100 290 Td (Geheim) Tj ET");
        var saved = WithBars(doc, (0, new PdfRectangle(95, 585, 160, 605)), (0, new PdfRectangle(49.5, 435, 160, 455)),
            (0, new PdfRectangle(95, 285, 160, 305)));
        var content = PageContent(saved);
        // the blue frame: the bar 45 pt from its line — written as it was, with no clip before it
        var blue = content.IndexOf("0 0 1 RG", StringComparison.Ordinal);
        var red = content.IndexOf("1 0 0 RG", StringComparison.Ordinal);
        Assert.True(blue >= 0 && red > blue, content);
        Assert.DoesNotContain("W", content[blue..red]);
        // the red frame: its left edge runs under the bar — clipped; the green cell, filled, holds its bar — clipped
        var green = content.IndexOf("0 1 0 rg", StringComparison.Ordinal);
        Assert.Contains("W", content[red..green]);
        Assert.Contains("W", content[green..]);
        using var after = PdfDocument.Open(saved);
        Assert.DoesNotContain("Geheim", Read(after, after.Pages[0]).Text);
    }

    /// <summary>
    /// Redacting over a shading and a clip shaped like letters, with two bars that overlap: the page is saved, the shading shows beside the
    /// bars, and no point of the letters stays. The clip that cuts the bars out of the shading took them by the even-odd rule in one path —
    /// where the bars overlap, the shading was inside the clip again and painted under both; the new check refused the page
    /// (docs/FEHLERGEDAECHTNIS.md, No. 455).
    /// </summary>
    [Fact]
    public void ShadingUnderTwoOverlappingBars_CutOutOfBoth()
    {
        using var doc = ShadingPage("q /Sh0 sh Q q 100 600 m 120 660 l 140 600 l h W n 0 g 0 0 595 842 re f Q BT /F1 12 Tf 100 300 Td (Geheim) Tj ET");
        var saved = WithBars(doc, (0, new PdfRectangle(90, 590, 200, 680)), (0, new PdfRectangle(150, 560, 260, 650)),
            (0, new PdfRectangle(90, 290, 160, 320)));
        using var after = PdfDocument.Open(saved);
        Assert.DoesNotContain("Geheim", Read(after, after.Pages[0]).Text);
        var content = PageContent(saved);
        Assert.DoesNotContain("120 660", content);
        using var bitmap = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        Assert.True(bitmap.GetPixel(400, 100).Red + bitmap.GetPixel(400, 100).Blue > 200);   // the shading beside the bars
    }

    /// <summary>
    /// The last check of what stays (docs/FEHLERGEDAECHTNIS.md, No. 450, Analyse F 3b): a page with „Geheim 4711“ under the bar and two lines
    /// beside it, against pages „after redacting“ made by hand. As the rewriter writes it, it passes; a word missing on another line, a
    /// word in another font in the same place, the next line moved down by 3 pt and the rest of the redacted line moved by 4 pt along it are each
    /// refused — before, only hiding lengths compared what stays, and a character missing outside was accepted in its last round.
    /// </summary>
    [Theory]
    [InlineData("BT /F1 12 Tf 100 500 Td (Bleibt hier) Tj ET BT /F1 12 Tf 100 480 Td (Zweite Zeile) Tj ET BT /F1 12 Tf 175.372 520 Td (danach) Tj ET", true)]
    [InlineData("BT /F1 12 Tf 100 500 Td (Bleibt) Tj ET BT /F1 12 Tf 100 480 Td (Zweite Zeile) Tj ET BT /F1 12 Tf 175.372 520 Td (danach) Tj ET", false)]
    [InlineData("BT /F1 12 Tf 100 500 Td (Bleibt hier) Tj ET BT /F2 12 Tf 100 480 Td (Zweite Zeile) Tj ET BT /F1 12 Tf 175.372 520 Td (danach) Tj ET", false)]
    [InlineData("BT /F1 12 Tf 100 500 Td (Bleibt hier) Tj ET BT /F1 12 Tf 100 477 Td (Zweite Zeile) Tj ET BT /F1 12 Tf 175.372 520 Td (danach) Tj ET", false)]
    [InlineData("BT /F1 12 Tf 100 500 Td (Bleibt hier) Tj ET BT /F1 12 Tf 100 480 Td (Zweite Zeile) Tj ET BT /F1 12 Tf 179.372 520 Td (danach) Tj ET", false)]
    public void SelfCheck_WhatStays_ComparedWithThePageBefore(string after, bool accepted)
    {
        var pdf = TestPdf.Simple(1);
        var courier = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Courier"))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("F2"), TestPdf.Ref(courier));
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(
            "BT /F1 12 Tf 100 520 Td (Geheim 4711) Tj ( danach) Tj ET BT /F1 12 Tf 100 500 Td (Bleibt hier) Tj ET BT /F1 12 Tf 100 480 Td (Zweite Zeile) Tj ET")));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var newPage = new PdfDictionary();
        foreach (var (key, value) in page.Dictionary.Entries) newPage.Set(key, value);
        newPage.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(after)));
        // the bar over „Geheim 4711“ (the rest of its line, „ danach“, starts at 175.37 pt)
        List<Core.Pdf.Graphics.RectD> bar = [Core.Pdf.Graphics.RectD.FromLtrb(99, 516, 173, 530)];
        Assert.Equal(accepted, Redactor.StaysChecked(doc, page, newPage, page.Resources!, bar));
    }

    /// <summary>
    /// The invisible text layer of a scan 2 pt above a line whose word went with its length hidden, the bar over both: the rest of the line,
    /// moved along it, and the text layer as it was pass the last check. It took the characters after within a third of their size around
    /// the line — the text layer above fell into the line below, the counts did not match, and nothing was saved (test file test file
    /// issue12963; docs/FEHLERGEDAECHTNIS.md, No. 462).
    /// </summary>
    [Fact]
    public void SelfCheck_TextLayerJustAboveAMovedLine_Accepted()
    {
        const string layer = "BT /F1 9 Tf 3 Tr 22 590.5 Td (Texterkennung einer Zeile) Tj ET ";
        using var doc = PathPage(layer + "BT /F1 9 Tf 22 588.5 Td (Geheim 020) Tj ET");
        var page = doc.Pages[0];
        var newPage = new PdfDictionary();
        foreach (var (key, value) in page.Dictionary.Entries) newPage.Set(key, value);
        newPage.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(layer + "BT /F1 9 Tf 70 588.5 Td ( 020) Tj ET")));
        List<Core.Pdf.Graphics.RectD> bar = [Core.Pdf.Graphics.RectD.FromLtrb(21, 586, 52, 592)];
        Assert.True(Redactor.StaysChecked(doc, page, newPage, page.Resources!, bar, moved: true));
    }

    /// <summary>
    /// The last check with lines the writer moves along with a redacted one, as DocLayNet pages have them (docs/FEHLERGEDAECHTNIS.md,
    /// No. 485): a line 0.48 pt below the redacted one (the same rounded bucket), a raised index after the redacted word, a line running up the
    /// page, two runs of dots and colons laid over each other. Each passes as the writer leaves it; each with a character missing or moved
    /// across its line is refused. Before, all four were refused: the check knew only level lines within 0.3 pt and kept their order.
    /// </summary>
    [Theory]
    // a line 0.48 pt below the redacted one, in the same rounded bucket
    [InlineData("BT /F1 9 Tf 22 84.24 Td (Geheim 020) Tj ET BT /F1 9 Tf 300 83.76 Td (Andere Zeile) Tj ET", "BT /F1 9 Tf 300 83.76 Td (Andere Zeile) Tj ET", 21, 80, 82, 94, true)]
    [InlineData("BT /F1 9 Tf 22 84.24 Td (Geheim 020) Tj ET BT /F1 9 Tf 300 83.76 Td (Andere Zeile) Tj ET", "BT /F1 9 Tf 300 83.76 Td (Andere Zeil) Tj ET", 21, 80, 82, 94, false)]
    // a raised index after the redacted word moves with its line
    [InlineData("BT /F1 9 Tf 22 588.5 Td (Geheim) Tj 3 Ts (2) Tj 0 Ts ( rest) Tj ET", "BT /F1 9 Tf 40 588.5 Td 3 Ts (2) Tj 0 Ts ( rest) Tj ET", 21, 53, 586, 597, true)]
    [InlineData("BT /F1 9 Tf 22 588.5 Td (Geheim) Tj 3 Ts (2) Tj 0 Ts ( rest) Tj ET", "BT /F1 9 Tf 40 588.5 Td ( rest) Tj ET", 21, 53, 586, 597, false)]
    // a line running up the page moves along itself, not across
    [InlineData("BT 0 1 -1 0 300 400 Tm /F1 9 Tf (Geheim) Tj ( rest) Tj ET", "BT 0 1 -1 0 300 440 Tm /F1 9 Tf ( rest) Tj ET", 290, 302, 399, 431, true)]
    [InlineData("BT 0 1 -1 0 300 400 Tm /F1 9 Tf (Geheim) Tj ( rest) Tj ET", "BT 0 1 -1 0 305 440 Tm /F1 9 Tf ( rest) Tj ET", 290, 302, 399, 431, false)]
    // dots and colons laid over each other, each run moved on its own
    [InlineData("BT /F1 9 Tf 22 588.5 Td (Geheim) Tj ET BT /F1 9 Tf 60 588.5 Td (. . . .) Tj ET BT /F1 9 Tf 60.5 588.5 Td (: : : :) Tj ET",
        "BT /F1 9 Tf 62 588.5 Td (. . . .) Tj ET BT /F1 9 Tf 61.5 588.5 Td (: : : :) Tj ET", 21, 54, 586, 597, true)]
    [InlineData("BT /F1 9 Tf 22 588.5 Td (Geheim) Tj ET BT /F1 9 Tf 60 588.5 Td (. . . .) Tj ET BT /F1 9 Tf 60.5 588.5 Td (: : : :) Tj ET",
        "BT /F1 9 Tf 62 588.5 Td (. . . .) Tj ET BT /F1 9 Tf 61.5 588.5 Td (: : :) Tj ET", 21, 54, 586, 597, false)]
    public void SelfCheck_LinesMovedWithTheRedactedOne(string before, string after, double left, double right, double bottom, double top, bool accepted)
    {
        using var doc = PathPage(before);
        var page = doc.Pages[0];
        var newPage = new PdfDictionary();
        foreach (var (key, value) in page.Dictionary.Entries) newPage.Set(key, value);
        newPage.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(after)));
        List<Core.Pdf.Graphics.RectD> bar = [Core.Pdf.Graphics.RectD.FromLtrb(left, bottom, right, top)];
        Assert.Equal(accepted, Redactor.StaysChecked(doc, page, newPage, page.Resources!, bar, moved: true));
    }

    /// <summary>
    /// The words before the first redacted one of a line stay exactly where they were when lengths are hidden: their place tells nothing of
    /// the removed text. Before, every word start of the line went to the grid — and to keep a reader's gap behind a word moved right, the
    /// next one moved further: a letter just before the bar was pushed under it, and the check refused the page (DocLayNet;
    /// docs/FEHLERGEDAECHTNIS.md, No. 486). The words after the bar may move.
    /// </summary>
    [Fact]
    public void HideLengths_WordsBeforeTheBar_StayExactly()
    {
        using var doc = PathPage("BT /F1 12 Tf 100.13 500 Td (Davor steht Text) Tj ( Geheim) Tj ( danach) Tj ET");
        var page = doc.Pages[0];
        var before = Read(doc, page).Glyphs;
        var redacted = Redactor.Apply(doc, page, [AreaOf(doc, page, "Geheim")], hideLengths: true);
        var (text, after) = Read(doc, PageOf(page, redacted));
        Assert.DoesNotContain("Geheim", text);
        // „Davor steht Text“: 16 characters, each where it was
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(before[i].Char, after[i].Char);
            Assert.Equal(before[i].X, after[i].X, 3);
        }
    }

    /// <summary>A stream that can only be written (as the file the saver writes to): the writer cannot read it back.</summary>
    private sealed class WriteOnly(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    /// <summary>
    /// The second check, on the file as written (Analyse F3b-3; decided on 4.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 461): a
    /// written file whose page lacks a word outside the bar, or still has the redacted word under it, is refused; the file as the writer
    /// writes it passes. The writer checks what it can read back itself and hands the check on otherwise (to the saver, before the file
    /// replaces the target). Before, the pages were checked only before writing — what writing did afterwards (scrubbing fonts, packing,
    /// renumbering) was not looked at again.
    /// </summary>
    [Fact]
    public void SelfCheck_WrittenFile_CheckedAgain()
    {
        const string original = "BT /F1 12 Tf 100 520 Td (Geheim 4711) Tj ( danach) Tj ET BT /F1 12 Tf 100 500 Td (Bleibt hier) Tj ET";
        using var doc = PathPage(original);
        // the bar over „Geheim 4711“ (the rest of its line, „ danach“, starts at 175.37 pt), as in SelfCheck_WhatStays_ComparedWithThePageBefore
        var area = new PdfRectangle(99, 516, 173, 530);
        var check = Assert.IsType<Redactor.WrittenCheck>(Redactor.Apply(doc, doc.Pages[0], [area]).Check);
        void Written(string content, bool accepted)
        {
            using var written = PathPage(content);
            if (accepted) Redactor.CheckWritten(written, [(0, check)], contentAsRedacted: true);
            else Assert.Throws<Core.Pdf.PdfException>(() => Redactor.CheckWritten(written, [(0, check)], contentAsRedacted: true));
        }
        Written("BT /F1 12 Tf 100 500 Td (Bleibt hier) Tj ET BT /F1 12 Tf 175.372 520 Td (danach) Tj ET", true);
        Written("BT /F1 12 Tf 100 500 Td (Bleibt) Tj ET BT /F1 12 Tf 175.372 520 Td (danach) Tj ET", false);
        Written(original, false);

        // the writer: checked itself in memory, handed on where it cannot read its output back
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new RedactionElement { Rect = area }]);
        using (var memory = new MemoryStream())
            Assert.Null(PdfWriter.Write(doc, items, memory, new PdfSaveOptions { ApplyRedactions = true }).RedactionChecks);
        using (var file = new MemoryStream())
            Assert.NotNull(PdfWriter.Write(doc, items, new WriteOnly(file), new PdfSaveOptions { ApplyRedactions = true }).RedactionChecks);
    }

    /// <summary>A page of 1 000 lines of 310 characters each (310 000, beyond the limit for hiding lengths), 1 pt high; <paramref name="change"/>
    /// alters the text of a line by its index.</summary>
    private static string ManyCharacters(Func<int, string, string>? change = null)
    {
        var text = new System.Text.StringBuilder();
        var line = new string('a', 310);
        for (var i = 0; i < 1000; i++)
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"BT /F1 1 Tf 10 {10 + i * 0.75:0.##} Td ({change?.Invoke(i, line) ?? line}) Tj ET\n");
        return text.ToString();
    }

    /// <summary>
    /// The last check of what stays on a page beyond the limit for hiding lengths (310 000 characters): a character missing outside the bar
    /// is found, and redacting such a page goes through. Before, the characters of such a page were not taken at all, and the check let it
    /// pass unchecked (Analyse F3b-2; decided on 4.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 460).
    /// </summary>
    [Fact]
    public void SelfCheck_PageBeyondTheLimitForHidingLengths_StillChecked()
    {
        using var doc = PathPage(ManyCharacters());
        var page = doc.Pages[0];
        List<Core.Pdf.Graphics.RectD> bar = [Core.Pdf.Graphics.RectD.FromLtrb(400, 400, 500, 500)];
        var newPage = new PdfDictionary();
        foreach (var (key, value) in page.Dictionary.Entries) newPage.Set(key, value);
        newPage.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(ManyCharacters((i, l) => i == 500 ? l[..^1] : l))));
        Assert.False(Redactor.StaysChecked(doc, page, newPage, page.Resources!, bar));
        newPage.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(ManyCharacters())));
        Assert.True(Redactor.StaysChecked(doc, page, newPage, page.Resources!, bar));

        var result = Redactor.Apply(doc, page, [new PdfRectangle(10, 10, 60, 20)]);
        Assert.NotNull(result.Page);
    }

    /// <summary>
    /// A grey background painted with an operator nobody knows between its rectangle and the fill (a damaged file from a public test
    /// collection, bug_481): the display ignores the operator and fills the rectangle; after redacting a word the background is still grey.
    /// The rewriter wrote the path out at the unknown operator, the fill found no path, and the whole page went white
    /// (docs/FEHLERGEDAECHTNIS.md, No. 454).
    /// </summary>
    [Fact]
    public void UnknownOperatorInsideAPath_PaintingStays()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("q 0.5 g 0 0 595 842 re /R11 36 0266 0 end00 000000 5 f Q BT /F1 12 Tf 100 600 Td (Geheim) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using (var before = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new Rendering.ImageCache()))
            Assert.InRange((int)before.GetPixel(300, 200).Red, 100, 160);   // the display fills the rectangle
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(90, 590, 200, 620))));
        Assert.DoesNotContain("Geheim", Read(after, after.Pages[0]).Text);
        using var bitmap = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
        Assert.InRange((int)bitmap.GetPixel(300, 200).Red, 100, 160);   // grey far from the bar
    }

    /// <summary>
    /// A path under construction across a shading or an inline image (out of order, but the display keeps the construction past them and
    /// paints it with the operator after them): redacting cuts what lies under the bar and keeps a stroke beside it; the inline image is
    /// written with its abbreviated keys only. The rewriter wrote the construction out before the shading or image — the clip it writes
    /// around the shading took the construction in (thousands of subpaths: minutes for the check, a hang in the fuzz run), the painting after
    /// it found no path and the redaction failed; the inline image came back with /W and /Width (docs/FEHLERGEDAECHTNIS.md, No. 913).
    /// </summary>
    [Theory]
    [InlineData("q 1 0 0 rg 100 600 40 40 re /Sh0 sh f Q")]
    [InlineData("q 1 0 0 rg 100 600 40 40 re BI /W 1 /H 1 /BPC 8 /CS /G ID \u0080 EI f Q")]
    [InlineData("q 0 0 1 RG 10 w 300 300 m 400 300 l 100 600 m 140 640 l /Sh0 sh S Q")]
    public void PathAcrossShadingOrInlineImage_CutAndKept(string content)
    {
        using var doc = ShadingPage(content);
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(90, 580, 160, 680))));
        var written = Everything(after);
        Assert.DoesNotContain("/Width", written);
        if (content.Contains(" S "))
        {
            // the stroke beside the bar is still drawn, blue
            using var bitmap = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 1, new Rendering.ImageCache());
            var pixel = bitmap.GetPixel(350, 842 - 300);
            Assert.True(pixel.Blue > 200 && pixel.Red < 60, $"Strich neben dem Balken fehlt: {pixel}");
        }
    }

    /// <summary>Thousands of subpaths before a shading under the bar: redacting takes well under a second, not minutes (No. 913).</summary>
    [Fact]
    public void ManySubpathsBeforeAShading_RedactedQuickly()
    {
        using var doc = ShadingPage(string.Concat(Enumerable.Repeat("0 0 m 1 1 l ", 20_000)) + "/Sh0 sh");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = Redactor.Apply(doc, doc.Pages[0], [new PdfRectangle(90, 580, 160, 680)]);
        Assert.NotNull(result.Page);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.Elapsed}");
    }

    /// <summary>
    /// A page in two parts, the second with a filter nobody knows (the display shows it empty): redaction reads the content as the display
    /// does and goes through; the unreadable part leaves with the rewritten page. The rewriter read the parts with a function of its own and
    /// broke off at the part the display had shown empty — two ways to read the same bytes, whose operator numbers must agree
    /// (docs/FEHLERGEDAECHTNIS.md, No. 452, Analyse F 3e).
    /// </summary>
    [Fact]
    public void PagePartTheDisplayCannotDecode_ReadTheSameWay_RedactionGoesThrough()
    {
        var pdf = TestPdf.Simple(1);
        var first = pdf.Add(TestPdf.FlateStream("BT /F1 12 Tf 100 600 Td (Geheim) Tj ET BT /F1 12 Tf 100 500 Td (Bleibt) Tj ET"u8.ToArray()));
        var second = pdf.Add(new PdfStream(TestPdf.Dict(("Filter", TestPdf.N("UnbekannterFilter"))), "WIRRWARR"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Contents, new PdfArray([TestPdf.Ref(first), TestPdf.Ref(second)]));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(90, 590, 200, 620))));
        var text = Read(after, after.Pages[0]).Text;
        Assert.DoesNotContain("Geheim", text);
        Assert.Contains("Bleibt", text);
        Assert.DoesNotContain("WIRRWARR", Everything(after));
    }

    /// <summary>
    /// A path ended by „n“ without a clip, its coordinates under the bar (a shape of the redacted content that paints nothing): it leaves the
    /// file. It was written back as it was (docs/FEHLERGEDAECHTNIS.md, No. 449, Analyse G2 to F 3a).
    /// </summary>
    [Fact]
    public void EmptyPathUnderTheBar_Leaves()
    {
        using var doc = PathPage("q 1 0 0 1 0 0 cm 101.25 612.5 m 123.75 655.5 l 141.25 612.5 l h n Q 300 300 m 400 400 l S");
        using var after = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(90, 580, 160, 680))));
        var content = System.Text.Encoding.Latin1.GetString(after.GetStreamData(after.Pages[0].Dictionary.Get(PdfName.Contents) as PdfStream
            ?? throw new InvalidOperationException()));
        Assert.DoesNotContain("123.75", content);
        Assert.Contains("400 400", content);
    }

    /// <summary>
    /// A scan of 5 004 × 6 000 pixels (30 million), a bar over a line of text, the copy blacked out but for two columns of pixels under the
    /// bar: the check of the blacked-out image finds them at full size. Reduced to a million pixels (blocks of 6 × 6), a rest of two columns
    /// on a block edge averaged to at most 42 levels, in a 1-bit scan to about 25 — below the 48 within which samples count as alike, and the
    /// rest went through (docs/FEHLERGEDAECHTNIS.md, No. 439). Fully blacked out, the check passes.
    /// </summary>
    [Theory]
    [InlineData(false, 1505)]
    [InlineData(false, 1501)]
    [InlineData(true, 1505)]
    [InlineData(true, 1501)]
    public void LargeScanWithANarrowRestUnderTheBar_CheckFindsIt(bool oneBit, int rest)
    {
        const int width = 5004, height = 6000;
        // the bar in pixels: columns 1200–2399, rows 2994–3053 (from the top); the line of text in rows 3000–3047, ink in 40 % of the columns
        static bool Ink(int x, int y) => y is >= 3000 and < 3048 && x % 5 < 2;
        static bool Bar(int x, int y) => x is >= 1200 and < 2400 && y is >= 2994 and < 3054;
        PdfStream Image(bool blacked, int keep)
        {
            var rowBytes = oneBit ? (width + 7) / 8 : width;
            var data = new byte[(long)rowBytes * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    // blacked out: under the bar nothing (grey 0, a stencil paints nothing there), but the columns kept as they were
                    var ink = Ink(x, y) && !(blacked && Bar(x, y) && x != keep && x != keep + 1);
                    var black = blacked && Bar(x, y) && x != keep && x != keep + 1;
                    if (oneBit)
                    {
                        // a stencil mask: bit 0 paints, so every bit is 1 except where there is ink
                        if (!ink) data[(long)y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                    }
                    else data[(long)y * rowBytes + x] = black ? (byte)0 : ink ? (byte)30 : (byte)255;
                }
            var dict = oneBit
                ? TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(width)), ("Height", PdfInteger.Of(height)),
                    ("ImageMask", PdfBoolean.True), ("Filter", TestPdf.N("FlateDecode")))
                : TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(width)), ("Height", PdfInteger.Of(height)),
                    ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N("FlateDecode")));
            return new PdfStream(dict, Core.Pdf.Filters.Flate.Encode(data));
        }
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        // 0.1 pt per pixel, the image at (50, 100): the bar exactly on pixel edges
        var imageToPage = new Core.Pdf.Graphics.Matrix(width * 0.1, 0, 0, height * 0.1, 50, 100);
        List<Core.Pdf.Graphics.RectD> bar = [Core.Pdf.Graphics.RectD.FromLtrb(50 + 120, 100 + (height - 3054) * 0.1, 50 + 240, 100 + (height - 2994) * 0.1)];

        var withRest = Image(true, rest);
        Assert.False(Redactor.UniformUnderAreas(doc, withRest, null, oneBit, imageToPage, bar));
        Assert.True(Redactor.UniformUnderAreas(doc, Image(true, -10), null, oneBit, imageToPage, bar));
        // the check of the command line tells an image it cannot read row by row apart (null), and falls back to the display's decoding
        // there (docs/FEHLERGEDAECHTNIS.md, No. 448)
        Assert.False(Redactor.Uniformity(doc, withRest, null, oneBit, imageToPage, bar));
        var unreadable = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(0)),
            ("Height", PdfInteger.Of(height)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8))), []);
        Assert.Null(Redactor.Uniformity(doc, unreadable, null, oneBit, imageToPage, bar));
        Assert.False(Redactor.UniformUnderAreas(doc, unreadable, null, oneBit, imageToPage, bar));

        // the reduced reading the check used before: the rest lies within the levels of „alike“ — except a grey rest inside one block
        var reduced = Core.Pdf.Graphics.PdfImageDecoder.DecodeReduced(new Core.Pdf.Graphics.ImageSource(withRest, null, null, oneBit, 0xFF000000), doc,
            1_000_000)!;
        int low = 255, high = 0;
        for (var y = 0; y < reduced.Height; y++)
            for (var x = 0; x < reduced.Width; x++)
            {
                var (u, v) = ((x + 0.5) / reduced.Width, 1 - (y + 0.5) / reduced.Height);
                if (u <= 1200.0 / width || u >= 2400.0 / width || v <= 1 - 3054.0 / height || v >= 1 - 2994.0 / height) continue;
                var value = reduced.Pixels[(y * reduced.Width + x) * reduced.BytesPerPixel];
                (low, high) = (Math.Min(low, value), Math.Max(high, value));
            }
        if (oneBit || rest == 1505) Assert.True(high - low <= Redactor.ImageSameLevels, $"verkleinert {high - low} Stufen");
    }

    /// <summary>
    /// A grey scan of 5 000 × 6 000 pixels, a bar whose edges lie inside the blocks of the reduced check: the image is blacked out in its own
    /// samples and keeps its size and colour space, also with the check at full size under the bar (docs/FEHLERGEDAECHTNIS.md, No. 439). The
    /// analysis suspected the reduced check sent such scans the way of the display; this case passed with the old state too — kept as a guard.
    /// </summary>
    [Fact]
    public void LargeScanUnderABar_KeepsItsResolutionAndColourSpace()
    {
        const int width = 5000, height = 6000;
        var data = new byte[(long)width * height];
        Array.Fill(data, (byte)255);
        for (var y = 3000; y < 3048; y++)
            for (var x = 0; x < width; x++)
                if (x % 5 < 2) data[(long)y * width + x] = 30;
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(width)),
            ("Height", PdfInteger.Of(height)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N("FlateDecode"))),
            Core.Pdf.Filters.Flate.Encode(data)));
        pdf.Replace(4, TestPdf.FlateStream("q 500 0 0 600 50 100 cm /Im0 Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        // edges 2.5 pixels into a block of six: columns 1202.5–2398.5, rows 2994.5–3053.5 from the top
        var bar = new PdfRectangle(50 + 120.25, 100 + (height - 3053.5) * 0.1, 50 + 239.85, 100 + (height - 2994.5) * 0.1);
        using var saved = PdfDocument.Open(WithBars(doc, (0, bar)));
        var xobjects = saved.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!;
        var blanked = xobjects.GetStream(xobjects.Keys.Single())!;
        Assert.Equal(width, blanked.Dictionary.GetInteger(PdfName.Get("Width")));
        Assert.Equal(height, blanked.Dictionary.GetInteger(PdfName.Get("Height")));
        Assert.Equal("DeviceGray", blanked.Dictionary.GetName(PdfName.Get("ColorSpace"))?.Value);
    }

    /// <summary>
    /// An image with a pre-multiplied soft mask (/Matte) under a bar, after three coarse images that used up most of the page's budget for
    /// refining: the blacked-out image and its mask keep the same size, as /Matte requires (ISO 32000-2, 11.6.5.2). The mask was refined
    /// first and took the rest of the budget, and the image stayed coarse — mask and image of different sizes (docs/FEHLERGEDAECHTNIS.md,
    /// No. 440).
    /// </summary>
    [Fact]
    public void ImageWithMatteMask_MaskAndImageKeepTheSameSize()
    {
        var pdf = TestPdf.Simple(1);
        PdfDictionary Gray(int size) => TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(size)),
            ("Height", PdfInteger.Of(size)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)));
        var samples = new byte[100 * 100];
        for (var i = 0; i < samples.Length; i++) samples[i] = (byte)(i % 100 * 2);
        var xobjects = new PdfDictionary();
        var content = new System.Text.StringBuilder();
        for (var k = 0; k < 3; k++)
        {
            xobjects.Set(PdfName.Get("Im" + k), TestPdf.Ref(pdf.Add(new PdfStream(Gray(100), samples))));
            content.Append($"q 1000 0 0 1000 0 0 cm /Im{k} Do Q ");
        }
        var maskDict = Gray(100);
        maskDict.Set(PdfName.Get("Matte"), new PdfArray([PdfInteger.Of(0)]));
        var mask = pdf.Add(new PdfStream(maskDict, samples));
        var imageDict = Gray(100);
        imageDict.Set(PdfName.Get("SMask"), TestPdf.Ref(mask));
        xobjects.Set(PdfName.Get("ImM"), TestPdf.Ref(pdf.Add(new PdfStream(imageDict, samples))));
        content.Append("q 1000 0 0 1000 0 0 cm /ImM Do Q");
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content.ToString())));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", xobjects)));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(WithBars(doc, (0, new PdfRectangle(400, 400, 600, 600))));
        // the image with the mask (the names may change when the page is written anew)
        var images = saved.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!;
        var blanked = images.Keys.Select(images.GetStream).Single(i => i?.Dictionary.GetStream(PdfName.Get("SMask")) is not null)!;
        var blankedMask = blanked.Dictionary.GetStream(PdfName.Get("SMask"))!;
        Assert.Equal(blankedMask.Dictionary.GetInteger(PdfName.Get("Width")), blanked.Dictionary.GetInteger(PdfName.Get("Width")));
        Assert.Equal(blankedMask.Dictionary.GetInteger(PdfName.Get("Height")), blanked.Dictionary.GetInteger(PdfName.Get("Height")));
        Assert.NotNull(blankedMask.Dictionary.Get(PdfName.Get("Matte")) as PdfArray);
    }

    /// <summary>
    /// An embedded TrueType font with both Symbolic and Nonsymbolic set (Flags 36), no /Encoding: its (3,0) cmap leads to the right glyphs, its
    /// (3,1) cmap to wrong ones. Redacting „Geheim“ keeps exactly the glyphs the display draws for „Text“ — display and font cleaning decide the
    /// same way (the Symbolic bit, ISO 32000-2, 9.8.2; docs/FEHLERGEDAECHTNIS.md, No. 571) — and the outlines of the letters only in the
    /// redacted word are gone.
    /// </summary>
    [Fact]
    public void FontWithBothSymbolFlags_RedactedCodesMatch()
    {
        const string letters = "GTehimtx";
        var original = Core.Fonts.OpenTypeFont.Parse(Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"));
        var wrong = original.GlyphForUnicode('o');
        var ttf = TextTests.TwoCmaps([.. letters.Select(c => (0xF000 + c, original.GlyphForUnicode(c)))], [.. letters.Select(c => ((int)c, wrong))]);
        var pdf = TestPdf.Simple(1);
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Length1", PdfInteger.Of(ttf.Length))), ttf));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Musterschrift")), ("Flags", PdfInteger.Of(36)),
            ("FontBBox", TestPdf.Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file))));
        const string shown = " " + letters;
        var widths = new PdfArray([.. Enumerable.Range(32, 91).Select(c => (PdfObject)PdfInteger.Of(shown.Contains((char)c) ? 600 : 0))]);
        var unicode = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange " +
            shown.Length + " beginbfchar " + string.Concat(shown.Select(c => $"<{(int)c:X2}> <{(int)c:X4}> ")) + "endbfchar endcmap CMapName currentdict /CMap defineresource pop end end")));
        var tt = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("ABCDEF+Musterschrift")),
            ("FirstChar", PdfInteger.Of(32)), ("LastChar", PdfInteger.Of(122)), ("Widths", widths), ("FontDescriptor", TestPdf.Ref(descriptor)),
            ("ToUnicode", TestPdf.Ref(unicode))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("TT"), TestPdf.Ref(tt));
        pdf.Replace(4, TestPdf.FlateStream("BT /TT 24 Tf 72 600 Td (Geheim Text) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var before = Core.Pdf.Fonts.PdfFont.Get(doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("TT"))!, doc);
        foreach (var c in letters) Assert.Equal(original.Outline(original.GlyphForUnicode(c)).Bounds, before.Glyph(c, 1).Outline.Bounds);   // the (3,0) way
        var data = WithBars(doc, (0, AreaOf(doc, doc.Pages[0], "Geheim")));
        using var after = PdfDocument.Open(data);
        var font = Core.Pdf.Fonts.PdfFont.Get(after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("TT"))!, after);
        foreach (var c in "Text")
        {
            Assert.False(font.Glyph(c, 1).Outline.IsEmpty, $"„{c}“");
            Assert.Equal(before.Glyph(c, 1).Outline.Bounds, font.Glyph(c, 1).Outline.Bounds);
        }
        var program = Core.Fonts.OpenTypeFont.Parse(after.GetStreamData(after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("TT"))!
            .GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile2"))!));
        // the letters only in the redacted word: their own outlines and their (3,0) entries are gone (the code may still find the unrelated
        // glyph of the (3,1) cmap, which the kept letters reach as well and which tells nothing about them)
        foreach (var c in "Ghim")
        {
            Assert.True(program.Outline(original.GlyphForUnicode(c)).IsEmpty, $"Umriss „{c}“");
            Assert.Equal(0, program.Lookup(3, 0, 0xF000 + c));
        }
        Assert.Contains("Text", Read(after, after.Pages[0]).Text);
        Assert.DoesNotContain("Geheim", Read(after, after.Pages[0]).Text);
    }
}

/// <summary>Redaction tests with a time limit: alone, not in parallel — under the load of the other tests the limit failed (29.10.2026).</summary>
[Collection(nameof(LimitCollection))]
public sealed class RedactionTimingTests
{
    /// <summary>
    /// A tiny image with a soft mask of 96 megapixels, drawn forty times under a bar (as a damaged file of the fuzzer, test file issue16263):
    /// redacting takes a few seconds. The check of each blacked-out copy read its whole mask anew — about a second each, and the redaction of
    /// such a page took over a minute; beyond a budget of samples per page the copies now go the display's way, read once
    /// (docs/FEHLERGEDAECHTNIS.md, No. 463).
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void TinyImageWithAHugeMask_DrawnOftenUnderABar_RedactedQuickly()
    {
        var pdf = TestPdf.Simple(1);
        var mask = pdf.Add(TestPdf.FlateStream(new byte[12_000 / 8 * 8_000], TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(12_000)), ("Height", PdfInteger.Of(8_000)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(1)))));
        var image = pdf.Add(TestPdf.FlateStream([0, 80, 160, 240], TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(2)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)),
            ("SMask", TestPdf.Ref(mask)))));
        var content = new System.Text.StringBuilder();
        for (var i = 0; i < 40; i++)
            content.Append(System.FormattableString.Invariant($"q 140 0 0 18 {60 + i % 2 * 160} {100 + i / 2 * 32} cm /Im1 Do Q "));
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content.ToString())));
        pdf.Replace(5, TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(2)), ("Contents", TestPdf.Ref(4)), ("MediaBox", TestPdf.Box(0, 0, 595, 842)),
            ("Resources", TestPdf.Dict(("XObject", TestPdf.Dict(("Im1", TestPdf.Ref(image))))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = Redactor.Apply(doc, doc.Pages[0], [new PdfRectangle(100, 90, 300, 760)]);
        Assert.True(watch.Elapsed.TotalSeconds < 15 * TestKind.Factor, $"{watch.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal(40, result.BlankedImages);
    }

    /// <summary>
    /// Many short lines, each with a positioning of its own and two text operators, all cut by the bar (docs/FEHLERGEDAECHTNIS.md, No. 245):
    /// finding the first text operator after each positioning searched all operators once per line — 18 s for this page in a release
    /// build; and every character was compared with every line that loses characters.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void ManyCutLines_AreWrittenAnewQuickly()
    {
        var pdf = TestPdf.Simple(1);
        var content = new System.Text.StringBuilder("BT /F1 12 Tf 5 337 Td\n");
        for (var i = 0; i < 40_000; i++) content.Append("0 0.003 Td (W) Tj ( ghijk) Tj\n");
        content.Append("ET");
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content.ToString())));
        using var doc = PdfDocument.Open(pdf.Build());
        var box = doc.Pages[0].CropBox;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(box.Left + 40, 330, box.Right - 40, 470))));
        Assert.True(watch.Elapsed.TotalSeconds < 10 * TestKind.Factor, $"{watch.Elapsed.TotalSeconds:0.0} s");
        var text = RedactionTests.Read(after, after.Pages[0]).Text;
        Assert.Contains("W", text);
        Assert.DoesNotContain("ghijk", text);
    }

    /// <summary>
    /// One TJ of 120,000 elements beside the page, removed by „Was niemand sieht“ — also when redacting with hidden content removed along
    /// (docs/FEHLERGEDAECHTNIS.md, No. 283): the removed characters were picked out of all of them once per element, 16 s for this page in a
    /// release build.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void LongTJ_BesideThePage_RemovedQuickly()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(
            "BT /F1 12 Tf 72 700 Td (Sichtbar) Tj ET BT /F1 12 Tf 900 700 Td [" + string.Concat(Enumerable.Repeat("(x) 10 ", 120_000)) + "] TJ ET")));
        using var doc = PdfDocument.Open(pdf.Build());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cleaned = Redactor.RemoveHidden(doc, doc.Pages[0], false, false, default, outside: true);
        Assert.True(watch.Elapsed.TotalSeconds < 5 * TestKind.Factor, $"{watch.Elapsed.TotalSeconds:0.0} s");
        Assert.NotNull(cleaned);
    }

    /// <summary>
    /// „Verborgenes schwärzen“ over a spot of 150,000 lines (rotated text beside the page, one line per character — docs/FEHLERGEDAECHTNIS.md,
    /// No. 285): every character was tested against every marked area, 61 s for a crafted page of 335,000 such lines.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void HiddenSpotOfManyLines_MarksAppliedQuickly()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(
            "BT /F1 12 Tf 72 700 Td (Sichtbar) Tj ET BT /F1 1 Tf 0 24 -24 0 30 30 Tm (" + new string('x', 150_000) + ") Tj ET")));
        using var doc = PdfDocument.Open(pdf.Build());
        var spots = HiddenText.Scan(doc, doc.Pages[0]).Spots;
        var areas = spots.SelectMany(HiddenText.MarkAreas).ToList();
        Assert.True(areas.Count > 100_000, $"{areas.Count} Flächen");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cleaned = Redactor.RemoveHiddenIn(doc, doc.Pages[0], areas);
        Assert.True(watch.Elapsed.TotalSeconds < 5 * TestKind.Factor, $"{watch.Elapsed.TotalSeconds:0.0} s");
        Assert.NotNull(cleaned);
        Assert.DoesNotContain("Sichtbar", cleaned.RemovedText);   // only the hidden text goes
    }

    /// <summary>
    /// A character of 10^11 pt (a damaged size, found by fuzzing): redacting a band over the page ends at once — the last check searched for
    /// the character in a window of grid cells as wide as 0.006 em, 600 million cells, and no cancelling stopped it (docs/FEHLERGEDAECHTNIS.md,
    /// No. 545; after 506, which narrowed the window across the line only). Now a window wider than the characters of that text searches
    /// through them.
    /// </summary>
    [Theory]
    [InlineData("99999999999")]
    [InlineData("-99999999999")]
    public async Task HugeFontSize_RedactedWithoutHanging(string size)
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes($"BT /F1 {size} Tf 72 720 Td (Example) Tj ET")));
        var work = Task.Run(() =>
        {
            using var doc = PdfDocument.Open(pdf.Build());
            var items = PageItem.From(doc);
            items[0] = items[0].WithElements([new RedactionElement { Rect = new PdfRectangle(61, 237, 551, 554) }]);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true });
        });
        // awaited, not waited for (a blocking wait in a test can deadlock, xUnit1031)
        var first = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(ReferenceEquals(first, work), "das Schwärzen hängt");
        await work;
    }

    /// <summary>
    /// Many huge characters on one line (a font size of 4 294 967 295, the sample of the fuzzing run over the file structure, M3): the last
    /// check of what stays went through every character of the page for each one — 4 173 repetitions (the sample) took 9 s in a release build,
    /// these 12 000 about 75 s (docs/FEHLERGEDAECHTNIS.md, No. 999). Now one or two seconds in a debug build.
    /// </summary>
    [Fact]
    public async Task HugeFontSize_ManyCharacters_CheckNotSquare()
    {
        var content = new System.Text.StringBuilder("BT /F1 4294967295 Tf 72 700 Td (Example");
        for (var i = 0; i < 12_000; i++) content.Append("(a) Tj ");
        content.Append(") Tj ET");
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content.ToString())));
        var work = Task.Run(() =>
        {
            using var doc = PdfDocument.Open(pdf.Build());
            var items = PageItem.From(doc);
            items[0] = items[0].WithElements([new RedactionElement { Rect = new PdfRectangle(102, 300, 582, 630) }]);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true });
        });
        var first = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(ReferenceEquals(first, work), "die Prüfung nach dem Schwärzen wächst mit dem Quadrat der Zeichen");
        await work;
    }
}
