using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Text in Type 3 fonts is edited like other text (phase 8.1, docs/FEHLERGEDAECHTNIS.md, No. 606): the new text uses the file's font with its
/// glyph procedures as far as it has every character of the block; otherwise the whole block takes the fallback font.
/// </summary>
public sealed class Type3EditTests
{
    /// <summary>Glyph procedure of a letter: a filled box, coloured by the text (d1).</summary>
    private const string Box = "600 0 0 0 500 700 d1 0 0 500 700 re f";

    /// <summary>Glyph procedure of a coloured letter: a red box whatever the text colour (d0).</summary>
    private const string RedBox = "600 0 d0 1 0 0 rg 0 0 500 700 re f";

    /// <summary>
    /// A page with lines in a Type 3 font „/T3“ (size 20, glyphs named after their letters, space „space“), each line <c>(x, y, text)</c>.
    /// </summary>
    private static byte[] Pixel(IEnumerable<(int Code, string Name, string Procedure)> glyphs, params (int X, int Y, string Text)[] lines) =>
        Pixel(glyphs, "", lines);

    /// <summary>As above, with <paramref name="state"/> (text state operators) at the start of each text object.</summary>
    private static byte[] Pixel(IEnumerable<(int Code, string Name, string Procedure)> glyphs, string state, params (int X, int Y, string Text)[] lines)
    {
        var content = new StringBuilder();
        foreach (var (x, y, text) in lines) content.Append($"BT {state} /T3 20 Tf {x} {y} Td ({text}) Tj ET\n");
        return PixelContent(glyphs, content.ToString());
    }

    /// <summary>A page with the Type 3 font „/T3“ (as <see cref="Pixel(IEnumerable{ValueTuple{int, string, string}}, string, ValueTuple{int, int, string}[])"/>) and the page content as it is.</summary>
    private static byte[] PixelContent(IEnumerable<(int Code, string Name, string Procedure)> glyphs, string pageContent)
    {
        var pdf = new TestPdf();
        var procs = new PdfDictionary();
        var differences = new PdfArray();
        var widths = new PdfArray();
        var list = glyphs.OrderBy(g => g.Code).ToList();
        int first = list[0].Code, last = list[^1].Code;
        for (var c = first; c <= last; c++) widths.Add(PdfInteger.Of(list.Any(g => g.Code == c) ? 600 : 0));
        foreach (var (code, name, procedure) in list)
        {
            procs.Set(PdfName.Get(name), TestPdf.Ref(pdf.Add(new PdfStream(new PdfDictionary(), Encoding.Latin1.GetBytes(procedure)))));
            differences.Add(PdfInteger.Of(code));
            differences.Add(TestPdf.N(name));
        }
        var type3 = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("Name", TestPdf.N("T3")),
            ("FontBBox", TestPdf.Box(0, 0, 500, 700)), ("FontMatrix", new PdfArray([new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0), new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0)])),
            ("CharProcs", procs), ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", differences))),
            ("FirstChar", PdfInteger.Of(first)), ("LastChar", PdfInteger.Of(last)), ("Widths", widths), ("Resources", new PdfDictionary())));
        var stream = pdf.Add(TestPdf.FlateStream(Encoding.Latin1.GetBytes(pageContent)));
        var pagesNumber = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNumber)), ("Contents", TestPdf.Ref(stream)),
            ("MediaBox", TestPdf.Box(0, 0, 595, 842)), ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("T3", TestPdf.Ref(type3))))))));
        pdf.Replace(pagesNumber, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray { TestPdf.Ref(page) }), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pagesNumber))));
        return pdf.Build();
    }

    /// <summary>Glyphs for the letters of <paramref name="letters"/> and the space, each drawn by <paramref name="procedure"/>.</summary>
    private static IEnumerable<(int, string, string)> Letters(string letters, string procedure = Box) =>
        letters.Distinct().Select(c => ((int)c, c.ToString(), procedure)).Append((32, "space", "600 0 d0"));

    private static PageTextBlock Single(IReadOnlyList<PageTextBlock> blocks, string start) =>
        Assert.Single(blocks, b => b.Text.StartsWith(start, StringComparison.Ordinal));

    /// <summary>Replaces the whole text of the block starting with <paramref name="start"/> by <paramref name="replacement"/> and saves.</summary>
    private static (byte[] Saved, RichTextElement Element) Edit(PdfDocument doc, string start, string replacement)
    {
        var block = Single(TextBlocks.Find(doc, doc.Pages[0]), start);
        var element = EditedText.FromBlock(doc, block);
        element = element with { Content = element.Content.Replace(0, element.Content.Length, replacement, element.Content.StyleAt(0)) };
        var items = PageItem.From(doc);
        var removed = Redactor.RemoveGlyphs(doc, items[0].Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
        items[0] = items[0].WithEditedPage(removed, [element]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output);
        return (output.ToArray(), element);
    }

    private static SKBitmap Render(PdfDocument doc) =>
        SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new ImageCache());

    /// <summary>The colour at a point of the page (PDF coordinates, one pixel per point).</summary>
    private static SKColor At(SKBitmap bitmap, double x, double y) => bitmap.GetPixel((int)x, (int)(842 - y));

    /// <summary>
    /// A Type 3 font without a space glyph, words set apart by TJ shifts — as TeX writes its pixel fonts. The block keeps its font (before, the
    /// space between the words was a character the font lacks, and the whole block fell back); a space of the new text becomes a shift of a
    /// third of an em, saved and read back the words stay apart.
    /// </summary>
    [Fact]
    public void Type3WithoutSpaceGlyph_KeepsItsFont_SpacesAsShifts()
    {
        var glyphs = "HaloWet".Distinct().Select(c => ((int)c, c.ToString(), Box));
        using var doc = PdfDocument.Open(PixelContent(glyphs, "BT /T3 20 Tf 72 700 Td [(Hallo) -400 (Welt)] TJ ET"));
        var block = Single(TextBlocks.Find(doc, doc.Pages[0]), "Hallo");
        Assert.Equal("Hallo Welt", block.Text);
        Assert.Null(block.Locked);
        var element = EditedText.FromBlock(doc, block);
        Assert.Empty(RichLayout.WholeBlockFallback(element.Content));
        var edited = element with { Content = element.Content.Replace(0, element.Content.Length, "Hallo Welt toll", element.Content.StyleAt(0)) };
        var laid = RichLayout.Of(edited).Lines.SelectMany(l => l.Glyphs).ToList();
        Assert.All(laid, g => Assert.NotNull(g.Original));
        // the words stay apart: a space advances about a third of the size (20 pt), not 0 (the font has no width for code 32)
        Assert.All(laid.Where(g => g.Text == " "), g => Assert.InRange(g.Advance, 5, 9));
        var items = PageItem.From(doc);
        var removed = Redactor.RemoveGlyphs(doc, items[0].Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
        items[0] = items[0].WithEditedPage(removed, [edited]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output);
        using var saved = PdfDocument.Open(output.ToArray());
        var after = Single(TextBlocks.Find(saved, saved.Pages[0]), "Hallo");
        Assert.Equal("Hallo Welt toll", after.Text.Replace('\n', ' ').Trim());
        Assert.All(after.Lines.SelectMany(l => l.Runs), r => Assert.Equal("Type3", r.Format.Font?.GetName(PdfName.Subtype)?.Value));
    }

    /// <summary>
    /// Glyph names the glyph list does not know („a72“, as TeX names its pixel glyphs) and no ToUnicode: the text is only guessed from the
    /// codes. Searching keeps the guess, editing does not trust it — the block stays locked as without readable text, and the font offers no
    /// codes for new text (before: editable, typed letters drawn with whatever glyph sat at that code).
    /// </summary>
    [Fact]
    public void Type3GuessedText_StaysLocked_NoCodesForNewText()
    {
        var glyphs = "Halo".Distinct().Select(c => ((int)c, "a" + (int)c, Box));
        using var doc = PdfDocument.Open(PixelContent(glyphs, "BT /T3 20 Tf 72 700 Td (Hallo) Tj ET"));
        var block = Single(TextBlocks.Find(doc, doc.Pages[0]), "Hallo");
        Assert.Contains("lesbaren Text", block.Locked);
        var font = Assert.IsAssignableFrom<Core.Pdf.Fonts.PdfFont>(PdfKnead.Core.Pdf.Fonts.PdfFont.Get(
            doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("T3"))!, doc));
        Assert.Empty(font.CodesByUnicode());
    }

    /// <summary>A block in a Type 3 font whose glyphs paint is editable (before: „Schrift aus gezeichneten Zeichen (Type3)“).</summary>
    [Fact]
    public void Type3Block_IsEditable()
    {
        using var doc = PdfDocument.Open(Pixel(Letters("HaloWet"), (72, 700, "Hallo Welt")));
        var block = Single(TextBlocks.Find(doc, doc.Pages[0]), "Hallo");
        Assert.Equal("Hallo Welt", block.Text);
        Assert.Null(block.Locked);
    }

    /// <summary>A glyph without readable text (no ToUnicode, an unknown name, a control code) keeps the block locked — for that reason, not as „Type3“.</summary>
    [Fact]
    public void Type3WithoutText_StaysLockedAsUnreadable()
    {
        using var doc = PdfDocument.Open(Pixel(Letters("Halo").Append((1, "g1", Box)), (72, 700, "Hallo\u0001")));
        var block = Single(TextBlocks.Find(doc, doc.Pages[0]), "Hallo");
        Assert.NotNull(block.Locked);
        Assert.Contains("lesbaren Text", block.Locked);
    }

    /// <summary>
    /// The new text uses the same Type 3 font (its dictionary from the file), where it has every character; saved and read back it is
    /// again a block in that font, editable.
    /// </summary>
    [Fact]
    public void Type3Original_NewTextUsesTheSameFont()
    {
        using var doc = PdfDocument.Open(Pixel(Letters("HaloWet"), (72, 700, "Hallo Welt")));
        var (good, goodElement) = Edit(doc, "Hallo", "Hallo Welt toll");
        var glyphs = RichLayout.Of(goodElement).Lines.SelectMany(l => l.Glyphs).ToList();
        Assert.All(glyphs, g => Assert.True(g.Original?.Font.IsType3));
        var appearance = ElementAppearance.For(goodElement);
        Assert.Contains(appearance.OriginalFonts.Values, f => f.Font.GetName(PdfName.Subtype)?.Value == "Type3");
        using var reopened = PdfDocument.Open(good);
        var after = Single(TextBlocks.Find(reopened, reopened.Pages[0]), "Hallo");
        Assert.Equal("Hallo Welt toll", after.Text.Replace('\n', ' ').Trim());
        Assert.Null(after.Locked);
        Assert.All(after.Lines.SelectMany(l => l.Runs), r => Assert.Equal("Type3", r.Format.Font?.GetName(PdfName.Subtype)?.Value));
    }

    /// <summary>
    /// The Type 3 font lacks a character of the new text: the WHOLE block takes the fallback font (no pixel glyphs beside outline glyphs);
    /// once the character is gone, the font returns. The panel „Text“ says so.
    /// </summary>
    [Fact]
    public void Type3Original_MissingCharacter_WholeBlockFallsBack()
    {
        using var doc = PdfDocument.Open(Pixel(Letters("HaloWet"), (72, 700, "Hallo Welt")));
        var block = Single(TextBlocks.Find(doc, doc.Pages[0]), "Hallo");
        var element = EditedText.FromBlock(doc, block);
        var missing = element with { Content = element.Content.Replace(0, element.Content.Length, "Hallo Xaver", element.Content.StyleAt(0)) };
        Assert.All(RichLayout.Of(missing).Lines.SelectMany(l => l.Glyphs), g => Assert.Null(g.Original));
        Assert.Single(RichLayout.WholeBlockFallback(missing.Content));
        var back = missing with { Content = missing.Content.Replace(6, 5, "Welt", missing.Content.StyleAt(0)) };
        Assert.Empty(RichLayout.WholeBlockFallback(back.Content));
        Assert.All(RichLayout.Of(back).Lines.SelectMany(l => l.Glyphs), g => Assert.NotNull(g.Original));
    }

    /// <summary>
    /// Text mode 2 with a wide line (fake bold for outline fonts) does not make Type 3 text bold — its procedures paint themselves, the
    /// outline adds nothing —, so the block keeps its font instead of falling back for „bold“.
    /// </summary>
    [Fact]
    public void Type3InFillStrokeMode_IsNotFakeBold_KeepsItsFont()
    {
        using var doc = PdfDocument.Open(Pixel(Letters("HaloWet"), "2 Tr 1 w", (72, 700, "Hallo Welt")));
        var block = Single(TextBlocks.Find(doc, doc.Pages[0]), "Hallo");
        Assert.All(block.Lines.SelectMany(l => l.Runs), r => Assert.False(r.Format.FakeBold));
        var element = EditedText.FromBlock(doc, block);
        Assert.Empty(RichLayout.WholeBlockFallback(element.Content));
        Assert.All(RichLayout.Of(element).Lines.SelectMany(l => l.Glyphs), g => Assert.NotNull(g.Original));
    }

    /// <summary>Coloured glyphs (d0, their own colour in the procedure) keep their colour after the text is replaced.</summary>
    [Fact]
    public void Type3ColouredGlyphs_d0_KeepTheirColour()
    {
        using var doc = PdfDocument.Open(Pixel(Letters("HaloWet", RedBox), (72, 700, "Hallo Welt")));
        var (saved, _) = Edit(doc, "Hallo", "Welt Hallo");
        using var reopened = PdfDocument.Open(saved);
        using var bitmap = Render(reopened);
        var inside = At(bitmap, 72 + 5, 700 + 7);   // middle of the first glyph (10 × 14 pt)
        Assert.True(inside.Red > 200 && inside.Green < 60 && inside.Blue < 60, inside.ToString());
    }

    /// <summary>
    /// A character occurs only in the replaced block and in the new text („H“; another block keeps using the font for other letters): removing
    /// the old text must not take its glyph from the font the new text uses — it is still drawn (KONZEPT.md, section 5.14; step 6 of 8.1).
    /// </summary>
    [Fact]
    public void Type3_ReplaceWord_GlyphsOnlyInTheOldWord_StillDrawn()
    {
        using var doc = PdfDocument.Open(Pixel(Letters("HaloWet"), (72, 700, "Hallo Welt"), (72, 300, "Welt")));
        var (saved, _) = Edit(doc, "Hallo", "Hot Welt");
        using var reopened = PdfDocument.Open(saved);
        using var bitmap = Render(reopened);
        var h = At(bitmap, 72 + 5, 700 + 7);
        Assert.True(h.Red < 80 && h.Green < 80 && h.Blue < 80, h.ToString());
        var text = PageText.Create(PageInterpreter.BuildText(reopened, reopened.Pages[0])).Text;
        Assert.Contains("Hot Welt", text.Replace('\n', ' '));
    }

    /// <summary>The replaced word is no longer in the file (leak check), also not as unused glyphs shown somewhere.</summary>
    [Fact]
    public void Type3_ReplaceWord_OldTextGoneFromFile()
    {
        using var doc = PdfDocument.Open(Pixel(Letters("HaloWet"), (72, 700, "Hallo Welt"), (72, 300, "Welt")));
        var (saved, _) = Edit(doc, "Hallo", "Hot Welt");
        using var reopened = PdfDocument.Open(saved);
        var text = PageText.Create(PageInterpreter.BuildText(reopened, reopened.Pages[0])).Text;
        Assert.DoesNotContain("Hallo", text);
        // „a“ occurred only in the replaced word: its glyph procedure is in no Type 3 font of the file any more (the font the new text
        // refers to keeps only what it draws; the page's copy only what the page still shows)
        var fonts = 0;
        for (var n = 1; n < reopened.ObjectCount + 64; n++)
        {
            if (!reopened.HasObject(n) || reopened.GetObject(n) is not PdfDictionary font || font.GetName(PdfName.Subtype)?.Value != "Type3") continue;
            fonts++;
            Assert.IsNotType<PdfStream>(font.GetDictionary(PdfName.Get("CharProcs"))?.Get(PdfName.Get("a")));
        }
        Assert.True(fonts > 0, "no Type 3 font found to check");
    }
}
