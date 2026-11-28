using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package E29 (docs/FEHLERGEDAECHTNIS.md, No. 1220–1223): the optical bounds lfbd, rtbd and opbd at the ends of the lines PDF-Knead sets
/// (OpenType 66.2, 66.20, 67.16), the character shaping and numeric shape selectors U+206C–U+206F (The Unicode Standard 23.3.1), interlinear
/// annotations in the bidirectional algorithm (23.8.3) and rand with a repeatable choice among its alternates (OpenType 67.11). The fonts are
/// Liberation Sans with GPOS or GSUB built by hand (helpers of packages ot-02, ot-49 and E11) and the bundled Noto Sans Arabic.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (E29)

    /// <summary>The optical bounds of „T“ in the fonts below, in font units: lfbd placement and advance, rtbd placement and advance.</summary>
    private const int E29LeftPlace = -40, E29LeftAdvance = -60, E29RightPlace = 30, E29RightAdvance = -30;

    /// <summary>A single positioning lookup (type 1, format 1) with value format 5 (XPlacement, XAdvance) for the glyphs given.</summary>
    private static Action<Ot02Bytes> E29Bound(int[] glyphs, int xPlacement, int xAdvance) => Ot02Lookup(1, b =>
    {
        var start = b.Here;
        var coverage = b.NewLabel();
        b.U16(1).Off16(coverage, start).U16(5).U16(xPlacement & 0xFFFF).U16(xAdvance & 0xFFFF);
        b.Label(coverage);
        Ot02Coverage(b, glyphs);
    });

    /// <summary>Liberation Sans whose GPOS has the features given for the glyph of <paramref name="c"/> (lfbd and opbd with the left values, rtbd with the right ones).</summary>
    private static byte[] E29Font(char c, params string[] features) => Ot49Font("TaQ ", null, Ot49Layout(["latn"],
        [.. features.Select(f => (f, f == "rtbd" ? E29Bound([Ot02G(c)], E29RightPlace, E29RightAdvance) : E29Bound([Ot02G(c)], E29LeftPlace, E29LeftAdvance)))]));

    /// <summary>Points per font unit of Liberation Sans at 10 pt (the size of <see cref="Ot49LaidOut"/>).</summary>
    private static double E29Scale => 10.0 / Ot02Font.UnitsPerEm;

    /// <summary>The width of a text laid out on one line in Liberation Sans at 10 pt (no bounds).</summary>
    private static double E29Width(string text) => Ot49LaidOut("M7 E29 Plain", Ot49Font("TaQ ", null), text, 10_000, BlockAlignment.Left).Lines[0].Width;

    // ------------------------------------------------------------ question 75: lfbd, rtbd, opbd at the ends of the lines

    /// <summary>
    /// OT 66.2 sentences 7–11, OT 66.20 sentences 9–15 (left aligned, docs/FEHLERGEDAECHTNIS.md, No. 1220): the first glyph of every line takes the
    /// lfbd of the font — „TaT TaT“ broken into two lines in a font with lfbd and rtbd for „T“: the first „T“ of each line is moved by the
    /// placement and its advance changed (the text after it follows), the glyphs in the middle and the „T“ at the right end keep the advances of
    /// the same font without the features (rtbd belongs to right aligned and justified text).
    /// </summary>
    [Fact]
    public void E29_OpticalBounds_LeftAligned_FirstGlyphOfEachLine()
    {
        const string text = "TaT TaT";
        var width = E29Width("TaT") + 1;
        var with = Ot49LaidOut("M7 E29 Bounds", E29Font('T', "lfbd", "rtbd"), text, width, BlockAlignment.Left);
        var without = Ot49LaidOut("M7 E29 Plain", Ot49Font("TaQ ", null), text, width, BlockAlignment.Left);
        Assert.Equal(2, with.Lines.Count);
        Assert.Equal(2, without.Lines.Count);
        var s = E29Scale;
        for (var l = 0; l < 2; l++)
        {
            var (a, b) = (with.Lines[l].Glyphs, without.Lines[l].Glyphs);
            Assert.Equal(b[0].XOffset + E29LeftPlace * s, a[0].XOffset, 9);
            Assert.Equal(b[0].Advance + E29LeftAdvance * s, a[0].Advance, 9);
            Assert.Equal(b[1].X + E29LeftAdvance * s, a[1].X, 9);
            for (var k = 1; k < a.Count; k++)
            {
                Assert.Equal(b[k].Advance, a[k].Advance, 9);
                Assert.Equal(b[k].XOffset, a[k].XOffset, 9);
            }
            Assert.Equal(without.Lines[l].Width + E29LeftAdvance * s, with.Lines[l].Width, 9);
        }
    }

    /// <summary>
    /// OT 67.16 sentences 7–11 (right aligned, No. 1220): the last visible glyph of a line takes the rtbd of the font — „TaT“ right aligned: the
    /// last „T“ is moved by the placement and its advance changed, so the line, aligned by its new width, starts further right; the first „T“ and
    /// the „a“ keep their advances (lfbd belongs to left aligned and justified text). A space at the end of the line hangs and takes nothing.
    /// </summary>
    [Fact]
    public void E29_OpticalBounds_RightAligned_LastVisibleGlyph()
    {
        var with = Ot49LaidOut("M7 E29 Bounds", E29Font('T', "lfbd", "rtbd"), "TaT", 200, BlockAlignment.Right);
        var without = Ot49LaidOut("M7 E29 Plain", Ot49Font("TaQ ", null), "TaT", 200, BlockAlignment.Right);
        var s = E29Scale;
        var (a, b) = (with.Lines[0].Glyphs, without.Lines[0].Glyphs);
        Assert.Equal(without.Lines[0].X - E29RightAdvance * s, with.Lines[0].X, 9);
        Assert.Equal(b[2].Advance + E29RightAdvance * s, a[2].Advance, 9);
        Assert.Equal(b[2].XOffset + E29RightPlace * s, a[2].XOffset, 9);
        Assert.Equal(b[0].Advance, a[0].Advance, 9);
        Assert.Equal(b[0].XOffset, a[0].XOffset, 9);
        Assert.Equal(b[1].Advance, a[1].Advance, 9);
    }

    /// <summary>
    /// OT 66.20 sentence 11 (justified, No. 1220): the bounds change the length of a line, and line breaking and justification reckon with it —
    /// „TaT TaT“ in a width a little less than its natural width fits on one line with lfbd and rtbd (both apply in justified text) and needs two
    /// without them; „TaT TaT TaT“ in that width: the first line „TaT TaT“, justified, takes both bounds and still ends exactly at the right edge.
    /// </summary>
    [Fact]
    public void E29_OpticalBounds_Justified_BreakingAndJustificationReckonWithThem()
    {
        var s = E29Scale;
        var width = E29Width("TaT TaT") + 0.5 * (E29LeftAdvance + E29RightAdvance) * s;
        var bounds = E29Font('T', "lfbd", "rtbd");
        Assert.Single(Ot49LaidOut("M7 E29 Bounds", bounds, "TaT TaT", width, BlockAlignment.Justify).Lines);
        Assert.Equal(2, Ot49LaidOut("M7 E29 Plain", Ot49Font("TaQ ", null), "TaT TaT", width, BlockAlignment.Justify).Lines.Count);
        var laid = Ot49LaidOut("M7 E29 Bounds", bounds, "TaT TaT TaT", width, BlockAlignment.Justify);
        Assert.Equal(2, laid.Lines.Count);
        var first = laid.Lines[0].Glyphs;
        Assert.Equal("TaT TaT", string.Concat(first.Select(g => g.Text)).TrimEnd());
        Assert.Equal(E29LeftPlace * s, first[0].XOffset, 9);
        var lastVisible = first.Last(g => g.Text != " ");
        Assert.Equal(E29RightPlace * s, lastVisible.XOffset, 9);
        Assert.Equal(width, lastVisible.X + lastVisible.Advance, 6);
    }

    /// <summary>
    /// OT 66.20 sentence 10 (No. 1220): a font with the deprecated opbd but neither lfbd nor rtbd — opbd applies at the left end of a left aligned
    /// line and at the right end of a right aligned one; a font whose features cover no glyph of the text, and a centred block, are laid out as
    /// without them.
    /// </summary>
    [Fact]
    public void E29_OpticalBounds_OpbdAlone_OtherGlyphs_Centred()
    {
        var s = E29Scale;
        var plain = Ot49Font("TaQ ", null);
        var opbd = E29Font('T', "opbd");
        var left = Ot49LaidOut("M7 E29 Opbd", opbd, "TaT", 200, BlockAlignment.Left).Lines[0].Glyphs;
        var leftPlain = Ot49LaidOut("M7 E29 Plain", plain, "TaT", 200, BlockAlignment.Left).Lines[0].Glyphs;
        Assert.Equal(leftPlain[0].Advance + E29LeftAdvance * s, left[0].Advance, 9);
        Assert.Equal(leftPlain[2].Advance, left[2].Advance, 9);
        var right = Ot49LaidOut("M7 E29 Opbd", opbd, "TaT", 200, BlockAlignment.Right).Lines[0].Glyphs;
        var rightPlain = Ot49LaidOut("M7 E29 Plain", plain, "TaT", 200, BlockAlignment.Right).Lines[0].Glyphs;
        Assert.Equal(rightPlain[2].Advance + E29LeftAdvance * s, right[2].Advance, 9);
        Assert.Equal(rightPlain[0].Advance, right[0].Advance, 9);
        foreach (var (family, font, alignment) in new[] { ("M7 E29 Other", E29Font('Q', "lfbd", "rtbd"), BlockAlignment.Justify),
                     ("M7 E29 Centre", E29Font('T', "lfbd", "rtbd"), BlockAlignment.Center) })
        {
            var with = Ot49LaidOut(family, font, "TaT TaT", 30, alignment);
            var without = Ot49LaidOut("M7 E29 Plain", plain, "TaT TaT", 30, alignment);
            Assert.Equal(without.Lines.Count, with.Lines.Count);
            for (var l = 0; l < with.Lines.Count; l++)
            {
                Assert.Equal(without.Lines[l].Glyphs.Select(g => (g.Glyph, g.X, g.Advance, g.XOffset)), with.Lines[l].Glyphs.Select(g => (g.Glyph, g.X, g.Advance, g.XOffset)));
                Assert.Equal(without.Lines[l].Width, with.Lines[l].Width, 9);
            }
        }
    }

    /// <summary>
    /// OT 66.2 sentence 11, OT 67.16 sentence 11 (No. 1220): no bound where a feature of fixed widths applies — in a run of Han text, where the
    /// shaper applies chws or halt from the font, the lfbd of a glyph is not used; in Latin text it is.
    /// </summary>
    [Fact]
    public void E29_OpticalBounds_NotWithFixedWidths()
    {
        var t = Ot02G('T');
        var gpos = Ot02Layout([new Ot02Script("hani", new Ot02Lang("", [0, 1])), new Ot02Script("latn", new Ot02Lang("", [0]))],
            [new Ot02Feature("lfbd", [0]), new Ot02Feature("halt", [1])], [E29Bound([t], E29LeftPlace, E29LeftAdvance), Ot02Lookup(1, E11SingleAdvance([t], -10))]);
        var shaper = TextShaper.For(OpenTypeFont.Parse(Ot49Font("TaQ ", null, gpos)));
        Assert.Equal(((double)E29LeftPlace, (double)E29LeftAdvance), shaper.OpticalBound(t, true, "Latn", null));
        Assert.Equal((0.0, 0.0), shaper.OpticalBound(t, true, "Hani", null));
    }

    /// <summary>
    /// OT 66.2 sentences 7–11, OT 67.16 sentences 7–11 in plain multi-line text (No. 1220): a left aligned text — the line starts by the placement
    /// of its first „T“ (<see cref="ElementAppearance.TextLine.Start"/>), the advance of that glyph makes the text after it follow the bound's
    /// advance, the width shrinks by it; a right aligned text — the last „T“ moved by its placement (the glyph before it advancing by it), the width
    /// shrinks by the rtbd advance. A single-line text takes none.
    /// </summary>
    [Fact]
    public void E29_OpticalBounds_PlainMultilineText()
    {
        var s = E29Scale;
        var bounds = ElementFace.For("probe-e29-bounds", () => OpenTypeFont.Parse(E29Font('T', "lfbd", "rtbd")), false, false, false);
        var plain = ElementFace.For("probe-e29-plain", () => OpenTypeFont.Parse(Ot49Font("TaQ ", null)), false, false, false);
        var element = new TextElement { Rect = new PdfRectangle(0, 0, 200, 100), Text = "TaT", FontSize = 10, Multiline = true };
        var a = Assert.Single(ElementAppearance.Layout(element, bounds));
        var b = Assert.Single(ElementAppearance.Layout(element, plain));
        Assert.Equal(E29LeftPlace * s, a.Start, 9);
        Assert.Equal(b.Advances[0] + (E29LeftAdvance - E29LeftPlace) * s, a.Advances[0], 9);
        Assert.Equal(b.Advances[1], a.Advances[1], 9);
        Assert.Equal(b.Width + E29LeftAdvance * s, a.Width, 9);
        var right = element with { Alignment = ElementAlignment.Right };
        var c = Assert.Single(ElementAppearance.Layout(right, bounds));
        var d = Assert.Single(ElementAppearance.Layout(right, plain));
        Assert.Equal(0, c.Start, 9);
        Assert.Equal(d.Advances[0], c.Advances[0], 9);
        Assert.Equal(d.Advances[1] + E29RightPlace * s, c.Advances[1], 9);
        Assert.Equal(d.Width + E29RightAdvance * s, c.Width, 9);
        var single = Assert.Single(ElementAppearance.Layout(element with { Multiline = false }, bounds));
        Assert.Equal(0, single.Start);
        Assert.Equal(b.Width, single.Width, 9);
    }

    // ------------------------------------------------------------ question 76: U+206C/U+206D, U+206E/U+206F

    /// <summary>The glyphs of shaped text without the selectors U+206C–U+206F (drawn as nothing).</summary>
    private static ShapedGlyph[] E29WithoutSelectors(ShapedGlyph[] shaped) =>
        [.. shaped.Where(g => g.Text is not ("\u206C" or "\u206D" or "\u206E" or "\u206F"))];

    /// <summary>
    /// UNI 23.3.1 sentences 19–28 (docs/FEHLERGEDAECHTNIS.md, No. 1221): presentation forms keep their own glyphs by default (the inhibited state);
    /// after U+206D they are shaped as their nominal letters — beh initial and beh medial as „بب“ (initial, final form), beh initial and lam-alef
    /// final as „بلا“ — up to a U+206C, after which a presentation form keeps its glyph again (so the beh before it, without a joining neighbour,
    /// takes its isolated form). The glyphs carry the characters as typed; the selector is drawn as nothing.
    /// </summary>
    [Fact]
    public void E29_ShapingSelectors_PresentationFormsShapedAfterU206D()
    {
        var arabic = TextShaper.For(Uni04NotoArabic);
        int[] Glyphs(ShapedGlyph[] shaped) => [.. E29WithoutSelectors(shaped).Select(g => g.Glyph)];
        Assert.Equal([Uni04NotoArabic.GlyphForUnicode(0xFE91), Uni04NotoArabic.GlyphForUnicode(0xFE92)], Glyphs(arabic.Shape("\uFE91\uFE92")));
        var shaped = arabic.Shape("\u206D\uFE91\uFE92");
        Assert.Equal(Glyphs(arabic.Shape("\u0628\u0628")), Glyphs(shaped));
        Assert.Equal("\uFE91\uFE92", string.Concat(E29WithoutSelectors(shaped).Select(g => g.Text)));
        Assert.Equal(0, shaped.Single(g => g.Text == "\u206D").XAdvance);
        var lamAlef = arabic.Shape("\u206D\uFE91\uFEFC");
        Assert.Equal(Glyphs(arabic.Shape("\u0628\u0644\u0627")), Glyphs(lamAlef));
        Assert.Equal("\uFE91\uFEFC", string.Concat(E29WithoutSelectors(lamAlef).Select(g => g.Text)));
        Assert.Equal([.. Glyphs(arabic.Shape("\u0628")), .. Glyphs(arabic.Shape("\uFE92"))], Glyphs(arabic.Shape("\u206D\uFE91\u206C\uFE92")));
    }

    /// <summary>
    /// UNI 23.3.1 sentences 27–28 in formatted text (No. 1221): the state of U+206D reaches across runs — „abc“ before the presentation forms is a
    /// run of another font and level; the Arabic run after it is still shaped (beh initial and medial drawn as „بب“), its glyphs carrying the
    /// presentation forms as typed.
    /// </summary>
    [Fact]
    public void E29_ShapingSelectors_StateReachesAcrossRuns()
    {
        var laid = RichLayout.Layout(new RichText("\u206Dabc \uFE91\uFE92", new RichStyle { Family = "Liberation Sans", Size = 10 }), 400, BlockAlignment.Left, 1.2, 10, 0);
        var glyphs = laid.Lines.SelectMany(l => l.Glyphs).ToList();
        var font = glyphs.Single(g => g.Text == "\uFE91").Face!.Font;
        var arabic = glyphs.Where(g => g.Face?.Font == font).ToList();
        Assert.Equal("\uFE91\uFE92", string.Concat(arabic.Select(g => g.Text)));
        Assert.Equal(TextShaper.For(font).Shape("\u0628\u0628", null, null, true).Select(g => g.Glyph), arabic.Select(g => g.Glyph));
    }

    /// <summary>
    /// UNI 23.3.1 sentences 29–36 (No. 1221): after U+206E the digits are drawn in the national shapes of the script around them up to U+206F —
    /// in Arabic text as the Arabic-Indic digits (U+0661, U+0662), in Persian (language „fa“) as the extended ones (U+06F1); after U+206F and in
    /// Latin text nominal. The glyphs carry the digits as typed; a plain text in a font without the national digits keeps the nominal ones.
    /// </summary>
    [Fact]
    public void E29_DigitSelectors_NationalShapesBetweenU206EAndU206F()
    {
        List<LaidGlyph> Laid(string text, string? language) => [.. RichLayout.Layout(new RichText(text, new RichStyle { Family = "Liberation Sans", Size = 10 }),
            400, BlockAlignment.Left, 1.2, 10, 0, null, language).Lines.SelectMany(l => l.Glyphs)];
        var arabic = Laid("\u0628 \u206E12\u206F 34", null);
        var one = arabic.Single(g => g.Text == "1");
        Assert.Equal(one.Face!.Font.GlyphForUnicode(0x0661), one.Glyph);
        var two = arabic.Single(g => g.Text == "2");
        Assert.Equal(two.Face!.Font.GlyphForUnicode(0x0662), two.Glyph);
        var three = arabic.Single(g => g.Text == "3");
        Assert.Equal(three.Face!.Font.GlyphForUnicode('3'), three.Glyph);
        var persian = Laid("\u0628 \u206E1", "fa").Single(g => g.Text == "1");
        Assert.Equal(persian.Face!.Font.GlyphForUnicode(0x06F1), persian.Glyph);
        var latin = Laid("abc \u206E1", null).Single(g => g.Text == "1");
        Assert.Equal(latin.Face!.Font.GlyphForUnicode('1'), latin.Glyph);
        Assert.Equal(0x0660, FormatSelectors.NationalZero("\u0628 1", 2, null));
        Assert.Equal(0x0966, FormatSelectors.NationalZero("\u0915 1", 2, null));
        Assert.Equal(-1, FormatSelectors.NationalZero("a 1", 2, null));
        var face = ElementFace.For(ElementFont.Sans);
        var plain = Assert.Single(ElementAppearance.Layout(new TextElement { Rect = new PdfRectangle(0, 0, 300, 40), Text = "\u0628 \u206E1" }, face));
        Assert.Contains(face.Glyph('1'), plain.Glyphs);
    }

    // ------------------------------------------------------------ question 77: interlinear annotations in the bidirectional algorithm

    /// <summary>
    /// UNI 23.8.3 sentences 44–48 (docs/FEHLERGEDAECHTNIS.md, No. 1222): the main text is ordered as if the annotated text stood in an isolate and
    /// the annotation were not there — anchor, „א“, separator, „x“, terminator, „abc“: the first strong character of the main text outside the
    /// isolate is „a“, so the paragraph is left to right and „abc“ at level 0 (before, „א“ made it right to left and „abc“ stood at level 2).
    /// </summary>
    [Fact]
    public void E29_Annotations_MainTextOrderedWithoutTheAnnotation()
    {
        var levels = Bidi.Levels("\uFFF9\u05D0\uFFFAx\uFFFBabc");
        Assert.Equal([0, 0, 0], levels[^3..]);
        Assert.Equal(1, levels[1] % 2);
        Assert.Equal(Bidi.Levels("\u2068\u05D0\u2069abc")[^3..], levels[^3..]);
    }

    /// <summary>
    /// UNI 23.8.3 sentences 44–48 (No. 1222): each annotation is ordered as a paragraph of its own and stays one unit beside the annotated text —
    /// „abc “, anchor, „def“, separator, „אב xy“, terminator, „ ghi“: the annotation is a paragraph right to left, so in the line „xy“ comes before
    /// the Hebrew letters (left of them), as „אב xy“ shown on its own; before, in the one left-to-right paragraph, „xy“ followed them. The levels
    /// and the plain text line in visual order agree; without annotation characters, and with an anchor that has no terminator, nothing changes.
    /// </summary>
    [Fact]
    public void E29_Annotations_EachAnnotationOrderedOnItsOwn()
    {
        const string text = "abc \uFFF9def\uFFFA\u05D0\u05D1 xy\uFFFB ghi";
        var order = Bidi.VisualOrder(Bidi.Levels(text));
        int At(char c) => Array.IndexOf(order, text.IndexOf(c));
        Assert.True(At('x') < At('\u05D0') && At('y') < At('\u05D1') && At('\u05D1') < At('\u05D0'));
        Assert.True(At('f') < At('x') && At('\u05D0') < At('g'));
        var line = Assert.Single(ElementAppearance.Layout(new TextElement { Rect = new PdfRectangle(0, 0, 500, 40), Text = text }, ElementFace.For(ElementFont.Sans)));
        Assert.True(line.Text.IndexOf("x") < line.Text.IndexOf("\u05D0"));
        Assert.Equal(Bidi.Levels("\u05D0!b!c"), Bidi.Levels("\u05D0\uFFF9b\uFFFAc"));
    }

    // ------------------------------------------------------------ question 78: rand

    /// <summary>
    /// OT 67.11 sentences 8–12 (docs/FEHLERGEDAECHTNIS.md, No. 1223): rand is on by default where the font has it and chooses among the alternates by
    /// a fixed function of the text — Liberation Sans with a type 3 lookup offering three alternates for „a“ under rand: „aaaaaaaa“ takes
    /// alternates only, not all the same, and exactly the same ones when set again, with a font parsed anew (another shaper) and in the
    /// formatted text layout; the sequence is fixed (no time, process or random number generator takes part). With „-rand“ and in the font
    /// without rand the glyph of the cmap stays.
    /// </summary>
    [Fact]
    public void E29_Rand_OnByDefault_RepeatableChoice()
    {
        var a = Ot02G('a');
        var font = Ot49Font("a", Ot49Layout(["latn"], [("rand", Ot49Alternates(a, Ot49Form, Ot49Form2, Ot49Form3))]));
        var first = Ot49Shape(font, "aaaaaaaa");
        Assert.All(first, g => Assert.Contains(g, new[] { Ot49Form, Ot49Form2, Ot49Form3 }));
        Assert.True(first.Distinct().Count() >= 2);
        Assert.Equal(first, Ot49Shape(font, "aaaaaaaa"));
        Assert.Equal(first, Ot49Shape([.. font], "aaaaaaaa"));
        Assert.Equal(E29RandExpected, first);
        Assert.Equal(Enumerable.Repeat(a, 8), Ot49Shape(font, "aaaaaaaa", "-rand"));
        Assert.Equal(Enumerable.Repeat(a, 8), Ot49Shape(Ot49Font("a", null), "aaaaaaaa"));
        var laid = Ot49LaidOut("M7 E29 Rand", font, "aaaaaaaa", 400, BlockAlignment.Left);
        Assert.Equal(first, laid.Lines[0].Glyphs.Select(g => g.Glyph));
        Assert.Equal(first, Ot49LaidOut("M7 E29 Rand", font, "aaaaaaaa", 400, BlockAlignment.Left).Lines[0].Glyphs.Select(g => g.Glyph));
    }

    /// <summary>The alternates rand takes for „aaaaaaaa“ in the font of <see cref="E29_Rand_OnByDefault_RepeatableChoice"/> (fixed once).</summary>
    private static readonly int[] E29RandExpected = [Ot49Form, Ot49Form, Ot49Form, Ot49Form3, Ot49Form, Ot49Form2, Ot49Form, Ot49Form2];
}
