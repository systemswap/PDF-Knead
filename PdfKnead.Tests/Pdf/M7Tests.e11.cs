using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, decisions on the shaper (docs/FEHLERGEDAECHTNIS.md, No. 1174): rvrn and rclt always (OpenType 67.20, 67.12), the features of the run's
/// direction (66.7, 66.8, 67.17, 67.18), kern not at glyphs palt covers (66.1), cpsp in words of capitals (64.19), chws or halt for CJK text
/// (64.15, 65.10), ital for italic without an italic face (65.19), the Syriac Alaph forms and stch (65.2, 65.3, 66.10, 67.28; Unicode 9.3),
/// isol and ccmp as the reference shaper has them (special rule), joiners across font runs (Unicode 9.2) and the transient reordering of
/// Arabic marks (UTR #53). The fonts are Liberation Sans with tables built by hand, or the fonts of the program where a real font shows it.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (E11)

    /// <summary>Single positioning format 1 with one value (value format 4: XAdvance) for the glyphs given.</summary>
    private static Action<Ot02Bytes> E11SingleAdvance(int[] glyphs, int advance) => b =>
    {
        var start = b.Here;
        var coverage = b.NewLabel();
        b.U16(1).Off16(coverage, start).U16(4).U16(advance & 0xFFFF);
        b.Label(coverage);
        Ot02Coverage(b, glyphs);
    };

    /// <summary>A GSUB or GPOS of one script whose default language system names every feature (one lookup per feature, in the order given).</summary>
    private static byte[] E11Layout(string script, params (string Feature, Action<Ot02Bytes> Lookup)[] entries) =>
        Ot02Layout([new Ot02Script(script, new Ot02Lang("", [.. Enumerable.Range(0, entries.Length)]))],
            [.. entries.Select((e, i) => new Ot02Feature(e.Feature, [i]))], [.. entries.Select(e => e.Lookup)]);

    /// <summary>Single substitution of type 1 as a lookup.</summary>
    private static Action<Ot02Bytes> E11Sub(params (int From, int To)[] map) => Ot02Lookup(1, Ot02Single(map));

    /// <summary>A cmap of format 12 with the groups given (code range → consecutive glyphs from the one given).</summary>
    private static byte[] E11Cmap(params (int First, int Last, int Glyph)[] groups)
    {
        var b = new Ot02Bytes();
        b.U16(0).U16(1).U16(3).U16(10).U32(12);
        b.U16(12).U16(0).U32(16 + 12 * groups.Length).U32(0).U32(groups.Length);
        foreach (var (first, last, glyph) in groups.OrderBy(g => g.First)) b.U32(first).U32(last).U32(glyph);
        return b.ToArray();
    }

    /// <summary>The glyph E11Cmap gives a Syriac character (U+0700 … U+074F → the glyphs from „A“ on, letters of Liberation Sans), the space its own.</summary>
    private static int E11Syriac(int c) => c == ' ' ? Ot02G(' ') : Ot02G('A') + c - 0x0700;

    /// <summary>Liberation Sans with a cmap for Syriac (<see cref="E11Syriac"/>) and the GSUB given.</summary>
    private static TextShaper E11SyriacShaper(byte[] gsub) =>
        TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
        {
            ["cmap"] = E11Cmap((' ', ' ', Ot02G(' ')), (0x0700, 0x074F, Ot02G('A'))), ["GSUB"] = gsub, ["GPOS"] = null, ["GDEF"] = null, ["kern"] = null,
        })));

    /// <summary>Makes RichFonts choose <paramref name="font"/> for <paramref name="family"/> in the style given, italic faked (as for a family without an italic face).</summary>
    private static void E11UseFont(string family, OpenTypeFont font, bool italic)
    {
        var key = "test:" + family;
        ElementFace.For(key, () => font, false, false, false);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var cache = (System.Collections.IDictionary)typeof(RichFonts).GetField("Cache", flags)!.GetValue(null)!;
        var gate = (Lock)typeof(RichFonts).GetField("Gate", flags)!.GetValue(null)!;
        using (gate.EnterScope()) cache[(family, false, italic)] = (key, false, italic, true);
    }

    /// <summary>The glyphs with their drawn positions (pen plus offset, y offset), for comparisons.</summary>
    private static (int Glyph, double X, double Y)[] E11Drawn(ShapedGlyph[] glyphs)
    {
        double pen = 0;
        var result = new (int, double, double)[glyphs.Length];
        for (var i = 0; i < glyphs.Length; i++)
        {
            result[i] = (glyphs[i].Glyph, pen + glyphs[i].XOffset, glyphs[i].YOffset);
            pen += glyphs[i].XAdvance;
        }
        return result;
    }

    /// <summary>HarfBuzz's glyphs with their drawn positions, in text order (a right-to-left result reversed).</summary>
    private static (int Glyph, double X, double Y)[] E11HarfBuzzDrawn(byte[] data, int unitsPerEm, string text, bool rightToLeft)
    {
        var hb = ShaperTests.HarfBuzz(data, unitsPerEm, text, []);
        double pen = 0;
        var result = new (int, double, double)[hb.Glyphs.Length];
        for (var i = 0; i < hb.Glyphs.Length; i++)
        {
            result[i] = (hb.Glyphs[i], pen + hb.XOffsets[i], hb.YOffsets[i]);
            pen += hb.Advances[i];
        }
        if (rightToLeft) Array.Reverse(result);
        return result;
    }

    // ------------------------------------------------------------ question 37: rvrn and rclt

    /// <summary>
    /// OT 67.20 sentences 23–34: rvrn is mandatory, active by default and not under the user's control, processed early — before locl and all
    /// other features — and only its single substitutions — a GSUB with liga → lookup 0 (b → c) and rvrn → lookup 1 (a → b): „a“ becomes c (rvrn
    /// in a stage of its own first, then liga sees b; in LookupList order it would stay b → never a); „-rvrn“ changes nothing; an rvrn ligature
    /// lookup (a a → X) is not applied. Before, rvrn was never applied: „a“ stayed a.
    /// </summary>
    [Fact]
    public void E11_Rvrn_FirstStageAlways_OnlySingleSubstitutions()
    {
        int a = Ot02G('a'), b = Ot02G('b'), c = Ot02G('c'), x = Ot02G('X');
        var shaper = Ot02Shaper(E11Layout("latn", ("liga", E11Sub((b, c))), ("rvrn", E11Sub((a, b)))));
        Assert.Equal([c], Ot02Shape(shaper, "a"));
        Assert.Equal([c], Ot02Shape(shaper, "a", "-rvrn"));
        var ligature = Ot02Shaper(E11Layout("latn", ("rvrn", Ot02Lookup(4, Ot02Ligature(([a, a], x))))));
        Assert.Equal([a, a], Ot02Shape(ligature, "aa"));
    }

    /// <summary>
    /// OT 67.20 sentences 14–22 and 26–34 in a variable font (fvar with one axis): the feature table of rvrn has no lookups and FeatureVariations
    /// (an empty ConditionSet, which the default instance meets) substitute it by one with lookup 1 (a → x); locl → lookup 0 (x → y) comes after
    /// rvrn although its lookup stands first: „a“ becomes y. Before, the substituted table was read but rvrn never asked for: „a“ stayed a.
    /// </summary>
    [Fact]
    public void E11_Rvrn_ThroughFeatureVariations_BeforeLocl()
    {
        var fvar = new Ot03Part().U16(1, 0, 16, 2, 1, 20, 0, 8).Tag("wght").U32(0x00640000).U32(0x01900000).U32(0x03840000).U16(0, 256).Build();
        var gsub = Ot03Header([Ot03Single(('x', 'y')), Ot03Single(('a', 'x'))], [("locl", [0]), ("rvrn", [])],
            featureVariations: Ot03FeatureVariations(new Ot03Part().U16(0), 1));
        var font = ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
        {
            ["GSUB"] = gsub.Build(), ["GPOS"] = null, ["GDEF"] = null, ["fvar"] = fvar,
        });
        Assert.Equal(Ot03Glyphs("y"), Ot03Shape(TextShaper.For(OpenTypeFont.Parse(font)), "a"));
    }

    /// <summary>
    /// OT 67.12 sentences 5, 8–11, 14: rclt is applied in its contexts and cannot be switched off — rclt → a → b: „a“ becomes b, also with
    /// „-rclt“. Before, rclt was not among the default features: „a“ stayed a.
    /// </summary>
    [Fact]
    public void E11_Rclt_AlwaysApplied_CannotBeSwitchedOff()
    {
        int a = Ot02G('a'), b = Ot02G('b');
        var shaper = Ot02Shaper(E11Layout("latn", ("rclt", E11Sub((a, b)))));
        Assert.Equal([b], Ot02Shape(shaper, "a"));
        Assert.Equal([b], Ot02Shape(shaper, "a", "-rclt"));
        Assert.Contains("rclt", TextShaper.DefaultSubstitutions);
        // in Arabic shaping among the stages after the joining forms: rclt → alef → beh under arab in Noto Sans Arabic
        int G(char ch) => Ot02ArabicFont.GlyphForUnicode(ch);
        var arabic = Ot02Shaper(E11Layout("arab", ("rclt", E11Sub((G('ا'), G('ب'))))), font: Ot02Arabic);
        Assert.Equal([G('ب')], Ot02Shape(arabic, "ا"));
    }

    // ------------------------------------------------------------ question 38: direction features

    /// <summary>
    /// OT 66.7 sentences 1–8, OT 66.8 sentences 1–10, OT 67.17 sentences 1–9: ltra and ltrm apply to text set left to right, rtla to text set right
    /// to left, together with the other features — under latn ltra → a → L, ltrm → b → M, rtla → a → R: „ab“ (left to right, its first strong
    /// character) becomes L M; under hebr rtla → א → R, ltra → א → L: „א“ (right to left) becomes R. Before, none of them was applied.
    /// </summary>
    [Fact]
    public void E11_DirectionFeatures_LtraLtrmLeftToRight_RtlaRightToLeft()
    {
        int a = Ot02G('a'), b = Ot02G('b'), l = Ot02G('L'), m = Ot02G('M'), r = Ot02G('R'), alef = Ot02G('א');
        var shaper = Ot02Shaper(E11Layout("latn", ("ltra", E11Sub((a, l))), ("ltrm", E11Sub((b, m))), ("rtla", E11Sub((a, r)))));
        Assert.Equal([l, m], Ot02Shape(shaper, "ab"));
        var hebrew = Ot02Shaper(E11Layout("hebr", ("rtla", E11Sub((alef, r))), ("ltra", E11Sub((alef, l)))));
        Assert.Equal([r], Ot02Shape(hebrew, "א"));
        // OT 66.7 sentence 8, OT 67.17 sentences 6–9: together with ccmp and locl — ccmp → a → b (lookup 0), ltra → b → c (lookup 1): „a“
        // becomes c; in Arabic shaping (Noto Sans Arabic) ccmp → alef → beh, rtla → beh → teh in the first stage: „ا“ becomes teh
        var stage = Ot02Shaper(E11Layout("latn", ("ccmp", E11Sub((a, b))), ("ltra", E11Sub((b, Ot02G('c'))))));
        Assert.Equal([Ot02G('c')], Ot02Shape(stage, "a"));
        int G(char ch) => Ot02ArabicFont.GlyphForUnicode(ch);
        var arabic = Ot02Shaper(E11Layout("arab", ("ccmp", E11Sub((G('ا'), G('ب')))), ("rtla", E11Sub((G('ب'), G('ت'))))), font: Ot02Arabic);
        Assert.Equal([G('ت')], Ot02Shape(arabic, "ا"));
    }

    /// <summary>
    /// OT 66.7, 67.17: the direction the caller gives (its embedding level, UAX #9) decides, not the text — the same „ab“ under latn (ltra → a → L,
    /// ltrm → b → M, rtla → a → R) set left to right is L M, set right to left (as in a right-to-left embedding) R b; „-ltra“ switches ltra off.
    /// (A new parameter of No. 1174; the rule itself is shown at the old interface by the test before.)
    /// </summary>
    [Fact]
    public void E11_DirectionFeatures_TheCallersDirection()
    {
        int a = Ot02G('a'), b = Ot02G('b'), l = Ot02G('L'), m = Ot02G('M'), r = Ot02G('R');
        var shaper = Ot02Shaper(E11Layout("latn", ("ltra", E11Sub((a, l))), ("ltrm", E11Sub((b, m))), ("rtla", E11Sub((a, r)))));
        Assert.Equal([l, m], shaper.Shape("ab", null, null, false).Select(g => g.Glyph));
        Assert.Equal([r, b], shaper.Shape("ab", null, null, true).Select(g => g.Glyph));
        Assert.Equal([a, m], shaper.Shape("ab", ["-ltra"], null, false).Select(g => g.Glyph));
    }

    /// <summary>
    /// OT 67.18 sentences 1–10: rtlm gives mirrored forms in right-to-left text for characters that are not mirrored through their counterpart —
    /// under hebr rtlm → ∂ → X, א → Y, ( → Z: „א∂(“ (right to left) becomes א X ( — ∂ (U+2202, Bidi_Mirrored without a counterpart in
    /// BidiMirroring.txt) takes rtlm, א is no mirrored character, and ( has its counterpart in the font (the caller mirrors such a character, UAX
    /// #9 L4); in a left-to-right run nothing changes. Before, rtlm was never applied.
    /// </summary>
    [Fact]
    public void E11_Rtlm_OnlyMirroredCharactersWithoutCounterpart()
    {
        int partial = Ot02G('∂'), alef = Ot02G('א'), open = Ot02G('('), x = Ot02G('X'), y = Ot02G('Y'), z = Ot02G('Z'), a = Ot02G('a');
        Assert.True(partial > 0);
        var shaper = Ot02Shaper(E11Layout("hebr", ("rtlm", E11Sub((partial, x), (alef, y), (open, z)))));
        Assert.Equal([alef, x, open], Ot02Shape(shaper, "א∂("));
        var latin = Ot02Shaper(E11Layout("latn", ("rtlm", E11Sub((partial, x)))));
        Assert.Equal([a, partial], Ot02Shape(latin, "a∂"));
    }

    // ------------------------------------------------------------ question 39: kern and palt

    /// <summary>
    /// OT 66.1 sentences 27–35, OT 67.1 sentences 10–18: while palt is off and the font has no apkn, kern does not apply to glyphs palt lookups
    /// cover — kern A–V −100, palt V +0: „AV“ is not kerned; with palt switched on, or with an apkn in the font, it is (−100). Before, kern
    /// applied to every glyph.
    /// </summary>
    [Fact]
    public void E11_Kern_NotOnGlyphsPaltCovers_UnlessPaltOrApkn()
    {
        int a = Ot02G('A'), v = Ot02G('V');
        double Width(TextShaper s, params string[] f) => s.Shape("AV", f).Sum(g => g.XAdvance);
        var plain = Ot02Font.AdvanceWidth(a) + Ot02Font.AdvanceWidth(v);
        var kern = Ot02Lookup(2, Ot02Pairs(0, (a, v, -100)));
        var shaper = Ot02Shaper(gpos: E11Layout("latn", ("kern", kern), ("palt", Ot02Lookup(1, E11SingleAdvance([v], 0)))));
        Assert.Equal(plain, Width(shaper));
        Assert.Equal(plain - 100, Width(shaper, "palt"));
        var withApkn = Ot02Shaper(gpos: E11Layout("latn", ("kern", kern), ("palt", Ot02Lookup(1, E11SingleAdvance([v], 0))),
            ("apkn", Ot02Lookup(1, E11SingleAdvance([a], 0)))));
        Assert.Equal(plain - 100, Width(withApkn));
        var withoutPalt = Ot02Shaper(gpos: E11Layout("latn", ("kern", kern)));
        Assert.Equal(plain - 100, Width(withoutPalt));
    }

    /// <summary>
    /// OT 66.1 sentences 38–39: kern is not used together with features of fixed widths (chws, fwid, halt, hwid, qwid, twid, tnum) — kern A–V −100:
    /// „AV“ with tnum or fwid switched on is not kerned, with pnum (proportional) it is. Before, kern stayed on.
    /// </summary>
    [Fact]
    public void E11_Kern_OffWithFixedWidthFeatures()
    {
        int a = Ot02G('A'), v = Ot02G('V');
        var plain = Ot02Font.AdvanceWidth(a) + Ot02Font.AdvanceWidth(v);
        var shaper = Ot02Shaper(gpos: E11Layout("latn", ("kern", Ot02Lookup(2, Ot02Pairs(0, (a, v, -100))))));
        Assert.Equal(plain, shaper.Shape("AV", ["tnum"]).Sum(g => g.XAdvance));
        Assert.Equal(plain, shaper.Shape("AV", ["fwid"]).Sum(g => g.XAdvance));
        Assert.Equal(plain - 100, shaper.Shape("AV", ["pnum"]).Sum(g => g.XAdvance));
    }

    /// <summary>
    /// OT 66.1 sentences 27–35 with a real font: Noto Sans CJK JP kerns ア–イ by −50 in its kern lookup, and its palt covers the kana — with halt
    /// switched off (question 40) „アイ“ keeps the full widths 1000 + 1000. Before, ア was kerned to 950.
    /// </summary>
    [Fact]
    public void E11_Kern_NotoCjk_KanaCoveredByPalt_NotKerned()
    {
        var shaper = TextShaper.For(Iso03NotoJp.Value);
        Assert.Equal([1000.0, 1000.0], shaper.Shape("アイ", ["-halt"]).Select(g => g.XAdvance));
    }

    // ------------------------------------------------------------ question 40: cpsp, chws/halt, ital

    /// <summary>
    /// OT 64.19 sentences 3–15: cpsp on by default, by the program's own recognition of words of capitals, not in connecting scripts — cpsp →
    /// A +50: „AA“ widens by 100, „Aa“ not (no word of capitals), „A AA“ all three A (a single capital next to such a word); „-cpsp“ switches it
    /// off. With Patrick Hand (cpsp in its GPOS): „AVATAR“ is spaced out (its first glyph moved by 5 units), the T of „Toyota“ is not. Before,
    /// cpsp was never applied.
    /// </summary>
    [Fact]
    public void E11_Cpsp_OnByDefault_InWordsOfCapitals()
    {
        var a = Ot02G('A');
        var shaper = Ot02Shaper(gpos: E11Layout("latn", ("cpsp", Ot02Lookup(1, E11SingleAdvance([a], 50)))));
        double Extra(string text, params string[] f) => shaper.Shape(text, f).Sum(g => g.XAdvance) - text.Sum(c => Ot02Font.AdvanceWidth(Ot02G(c)));
        Assert.Equal(100, Extra("AA"));
        Assert.Equal(0, Extra("Aa"));
        Assert.Equal(150, Extra("A AA"));
        Assert.Equal(0, Extra("AA", "-cpsp"));
        Assert.Contains("cpsp", TextShaper.DefaultPositioning);

        var patrick = TextShaper.For(EmbeddedFonts.HandwritingOpenType("PatrickHand-Regular.ttf"));
        var shaped = patrick.Shape("AVATAR Toyota");
        var plain = patrick.Shape("AVATAR Toyota", ["-cpsp"]);
        Assert.NotEqual(E11Drawn(plain)[0], E11Drawn(shaped)[0]);
        Assert.Equal(plain[7].XOffset, shaped[7].XOffset);
        Assert.Equal(plain[7].XAdvance, shaped[7].XAdvance);
    }

    /// <summary>
    /// OT 64.15 sentences 20–28, OT 65.10 sentences 11–19, OT 66.1 sentences 38–39: PDF-Knead has no layout after CLREQ, JLREQ or KLREQ, so a run of
    /// horizontal CJK text takes chws where the font has it, else halt — mutually exclusive — and then neither kern nor apkn. Han „漢漢“ (glyph 0
    /// in Liberation Sans) with chws −300, halt −500 and kern (pair −100): each −300 (chws); without chws −500 each (halt); with both switched off
    /// −100 (kern); Latin text keeps kern. Before, only kern applied.
    /// </summary>
    [Fact]
    public void E11_ChwsOrHalt_ForCjkText_KernOff()
    {
        var full = 2 * Ot02Font.AdvanceWidth(0);
        Action<Ot02Bytes> Chws() => Ot02Lookup(1, E11SingleAdvance([0], -300));
        Action<Ot02Bytes> Halt() => Ot02Lookup(1, E11SingleAdvance([0], -500));
        Action<Ot02Bytes> Kern() => Ot02Lookup(2, Ot02Pairs(0, (0, 0, -100)));
        double Width(TextShaper s, params string[] f) => s.Shape("漢漢", f).Sum(g => g.XAdvance);
        var both = Ot02Shaper(gpos: E11Layout("hani", ("chws", Chws()), ("halt", Halt()), ("kern", Kern())));
        Assert.Equal(full - 600, Width(both));
        var haltOnly = Ot02Shaper(gpos: E11Layout("hani", ("halt", Halt()), ("kern", Kern())));
        Assert.Equal(full - 1000, Width(haltOnly));
        Assert.Equal(full - 100, Width(both, "-chws", "-halt"));
        // a width feature asked for by the caller excludes chws and halt (and, being one of fixed widths, kern)
        Assert.Equal(full, Width(both, "tnum"));
        Assert.Equal(full - 100, Width(both, "pnum"));
    }

    /// <summary>
    /// OT 65.10 sentences 12 and 15 with a real font: Noto Sans CJK JP has halt (no chws) — in „漢字。“ the ideographic full stop takes its half width
    /// 500; the same stop alone (no CJK letter: no CJK run) keeps 1000. Before, 1000 in both.
    /// </summary>
    [Fact]
    public void E11_Halt_NotoCjk_FullStopHalfWidthInCjkText()
    {
        var shaper = TextShaper.For(Iso03NotoJp.Value);
        Assert.Equal(500.0, shaper.Shape("漢字。")[2].XAdvance);
        Assert.Equal(1000.0, shaper.Shape("。")[0].XAdvance);
    }

    /// <summary>
    /// OT 65.19 sentences 7–8 and 11: for italic formatting the program looks for ital and uses it — a font without an italic face whose ital
    /// replaces a by X: typed „ab“ in italic is laid out with X, which is not slanted again, and b slanted (the font has no italic form of it);
    /// the shaper marks the glyph it replaced. Before, ital was never asked for: a slanted a.
    /// </summary>
    [Fact]
    public void E11_Ital_ItalicWithoutItalicFace_UsesTheFontsItalicForms()
    {
        int a = Ot02G('a'), b = Ot02G('b'), x = Ot02G('X');
        var gsub = E11Layout("latn", ("ital", E11Sub((a, x))));
        Assert.Equal([x, b], Ot02Shape(Ot02Shaper(gsub), "ab", "ital"));

        const string family = "M7 E11 Ital";
        E11UseFont(family, OpenTypeFont.Parse(ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?> { ["GSUB"] = gsub })), true);
        var layout = RichLayout.Layout(new RichText("ab", new RichStyle { Family = family, Size = 10, Italic = true }), 500, BlockAlignment.Left, 1.2, 10, 0);
        var glyphs = layout.Lines[0].Glyphs;
        Assert.Equal([x, b], glyphs.Select(g => g.Glyph));
        Assert.Equal([false, true], glyphs.Select(g => g.FakeItalic));
    }

    // ------------------------------------------------------------ question 42: Syriac Alaph, stch

    /// <summary>
    /// Unicode 9.3.2 sentences 8–21 (rules S1–S3), OT 65.2, 65.3, 66.10: the forms of Alaph from the joining types and groups of ArabicShaping.txt
    /// — after a joining Beth at the end of the word final (Afj), inside the word med2, after He (right-joining, not Dalath/Rish) at the end fin2
    /// (Afn), after Dalath or Rish fin3 (Afx), alone or after a space isolated, after He inside the word isolated; a mark between is skipped.
    /// Before, Alaph took only isolated or final.
    /// </summary>
    [Theory]
    [InlineData("ܒܐ", 1, "Final")]
    [InlineData("ܒܐ ", 1, "Final")]
    [InlineData("ܒܐܒ", 1, "Medial2")]
    [InlineData("ܗܐ", 1, "Final2")]
    [InlineData("ܗ\u0730ܐ", 2, "Final2")]
    [InlineData("ܐܐ", 1, "Final2")]
    [InlineData("ܕܐ", 1, "Final3")]
    [InlineData("ܪܐ", 1, "Final3")]
    [InlineData("ܐ", 0, "Isolated")]
    [InlineData(" ܐ", 1, "Isolated")]
    [InlineData("ܗܐܒ", 1, "Isolated")]
    public void E11_SyriacAlaph_FormsFromJoiningGroups(string text, int index, string expected)
    {
        Assert.Equal(Enum.Parse<ArabicJoining.Form>(expected), ArabicJoining.Forms([.. text.EnumerateRunes().Select(r => r.Value)])[index]);
    }

    /// <summary>
    /// OT 65.2 sentences 7–8, OT 65.3 sentences 7–8, OT 66.10 sentences 7–8 (and Unicode 9.3.2 sentences 76–82): the shaper applies fin2, fin3 and
    /// med2 to Alaph where it takes that form, each in its stage between fina and init — a GSUB under syrc with a lookup per form (Alaph → its own
    /// glyph): Beth Alaph → fina, Beth Alaph Beth → med2, He Alaph → fin2, Dalath Alaph → fin3, Alaph alone → isol. Before, fin2, fin3 and med2
    /// were never applied (He Alaph took isol, Beth Alaph Beth fina).
    /// </summary>
    [Fact]
    public void E11_SyriacAlaph_Fin2Fin3Med2Applied()
    {
        var alaph = E11Syriac(0x0710);
        string[] features = ["isol", "fina", "fin2", "fin3", "medi", "med2", "init"];
        var shaper = E11SyriacShaper(E11Layout("syrc", [.. features.Select((f, k) => (f, E11Sub((alaph, 500 + k))))]));
        int Form(string text, int index) => shaper.Shape(text)[index].Glyph - 500;
        Assert.Equal(1, Form("ܒܐ", 1));
        Assert.Equal(5, Form("ܒܐܒ", 1));
        Assert.Equal(2, Form("ܗܐ", 1));
        Assert.Equal(3, Form("ܕܐ", 1));
        Assert.Equal(0, Form("ܐ", 0));
    }

    /// <summary>
    /// OT 67.28 sentences 1–22, Unicode 9.3.1 sentences 59–69: stch decomposes the Syriac abbreviation mark into an odd number of glyphs — F1 R F2
    /// — whose fixed glyphs spread over the text after it up to the end of the word and whose middle glyph is repeated in between: in „ܒ SAM ܒܓ ܒ“ the
    /// mark becomes F1 (no advance, at the start of „ܒܓ“), F2 and the copies of R follow „ܓ“ (attached to it, no advance), F2 ending where the word
    /// ends — right to left (the caller reverses the line: F1 at −w(F1) from the start at the right edge of „ܒܓ“, F2 at −w(ܓ) from the pen after ܓ,
    /// which is the left edge); left to right (a direction the caller can give, No. 1174) F1 at offset 0 and F2 at −w(F2) from the end. The width
    /// of the line does not change. Before, the mark kept its single glyph.
    /// </summary>
    [Fact]
    public void E11_Stch_SpansTheWordAfterTheMark()
    {
        int sam = E11Syriac(0x070F), f1 = Ot02G('.'), r = Ot02G('-'), f2 = Ot02G(',');
        var shaper = E11SyriacShaper(E11Layout("syrc", ("stch", Ot02Lookup(2, Ot02Multiple([sam], [f1, r, f2])))));
        var font = Ot02Font;
        double wB = font.AdvanceWidth(E11Syriac(0x0712)), wG = font.AdvanceWidth(E11Syriac(0x0713));
        double w1 = font.AdvanceWidth(f1), wr = font.AdvanceWidth(r), w2 = font.AdvanceWidth(f2);
        var width = wB + wG;
        var copies = (int)Math.Ceiling((width - w1 - w2) / wr);
        Assert.True(copies >= 1);
        foreach (var rightToLeft in new[] { true, false })
        {
            var shaped = rightToLeft ? shaper.Shape("ܒ\u070Fܒܓ ܒ") : StchLeftToRight(shaper, "ܒ\u070Fܒܓ ܒ");
            Assert.Equal(f1, shaped[1].Glyph);
            Assert.Equal(0, shaped[1].XAdvance);
            Assert.Equal(rightToLeft ? -w1 : 0, shaped[1].XOffset, 6);
            var tail = shaped.Skip(4).TakeWhile(g => g.Glyph == r || g.Glyph == f2).ToArray();
            Assert.Equal(copies + 1, tail.Length);
            Assert.All(tail, g => Assert.True(g.AttachBack > 0 && g.XAdvance == 0));
            var end = tail.Single(g => g.Glyph == f2);
            Assert.Equal(rightToLeft ? -wG : -w2, end.XOffset, 6);
            Assert.Equal(E11Syriac(' '), shaped[4 + tail.Length].Glyph);
            Assert.Equal(3 * wB + wG + font.AdvanceWidth(E11Syriac(' ')), shaped.Sum(g => g.XAdvance), 6);
        }

        // the four-parameter Shape exists since No. 1174: called by reflection, so that the right-to-left part runs on the interface before it
        static ShapedGlyph[] StchLeftToRight(TextShaper shaper, string text) =>
            (ShapedGlyph[])typeof(TextShaper).GetMethod("Shape", [typeof(string), typeof(IEnumerable<string>), typeof(string), typeof(bool?)])!
                .Invoke(shaper, [text, null, null, false])!;
    }

    // ------------------------------------------------------------ question 43: isol and ccmp (special rule)

    /// <summary>
    /// OT 65.18 sentence 9 and OT 64.13 sentences 6, 16 — special rule (as the reference shaper): a Non_Joining character of a joining script gets
    /// no isol, and ccmp is a stage of its own only in Arabic shaping — isol → Hamza → X under arab: „ء“ stays Hamza; liga → lookup 0 (a → b),
    /// ccmp → lookup 1 (a → c) under latn: „a“ becomes b (LookupList order). HarfBuzz gives the same glyphs for both fonts.
    /// </summary>
    [Fact]
    public void E11_IsolAndCcmp_AsTheReferenceShaper()
    {
        int G(char ch) => Ot02ArabicFont.GlyphForUnicode(ch);
        int hamza = G('ء'), x = G('ب');
        var arabicData = ColorFontBuilder.WithTables(Ot02Arabic, new Dictionary<string, byte[]?> { ["GSUB"] = E11Layout("arab", ("isol", E11Sub((hamza, x)))) });
        var arabic = TextShaper.For(OpenTypeFont.Parse(arabicData));
        Assert.Equal([hamza], arabic.Shape("ء").Select(g => g.Glyph));
        Assert.Equal([hamza], ShaperTests.HarfBuzz(arabicData, Ot02ArabicFont.UnitsPerEm, "ء", []).Glyphs);

        int a = Ot02G('a'), b = Ot02G('b'), c = Ot02G('c');
        var latinData = ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
        {
            ["GSUB"] = E11Layout("latn", ("liga", E11Sub((a, b))), ("ccmp", E11Sub((a, c)))), ["GPOS"] = null, ["kern"] = null,
        });
        Assert.Equal([b], TextShaper.For(OpenTypeFont.Parse(latinData)).Shape("a").Select(g => g.Glyph));
        Assert.Equal([b], ShaperTests.HarfBuzz(latinData, Ot02Font.UnitsPerEm, "a", []).Glyphs);
    }

    // ------------------------------------------------------------ question 45: joiners across font runs

    /// <summary>
    /// Unicode 9.2.1 sentences 55–65: ZERO WIDTH JOINER after Heh makes it join forward (the Hijri sign, initial form) — typed in Liberation Sans,
    /// which lacks Arabic but has the joiner: the joiner goes with the Heh into the run of the fallback font, and Heh takes the initial form of
    /// Noto Sans Arabic, also in a paragraph that begins with Latin text (the joiner's own embedding level is that of the paragraph). Before, the
    /// joiner stayed in a run of its own and Heh stood isolated.
    /// </summary>
    [Theory]
    [InlineData("ه\u200D")]
    [InlineData("ab ه\u200D")]
    public void E11_Joiner_GoesWithTheLetterItBelongsTo(string text)
    {
        var noto = TextShaper.For(Ot02ArabicFont);
        var initial = noto.Shape("ه\u200D")[0].Glyph;
        Assert.NotEqual(noto.Shape("ه")[0].Glyph, initial);
        var layout = RichLayout.Layout(new RichText(text, new RichStyle { Family = "Liberation Sans", Size = 10 }), 500, BlockAlignment.Left, 1.2, 10, 0);
        var heh = layout.Lines[0].Glyphs.Single(g => g.Text == "ه");
        Assert.Equal(initial, heh.Glyph);
    }

    // ------------------------------------------------------------ security (E11)

    /// <summary>
    /// Bounds of No. 1174: a base with 20 000 combining marks (the transient reordering leaves a sequence longer than the stream-safe 32 as it
    /// is), 20 000 joiners in a row (each looks at most 8 characters far for its letter) and 20 000 capitals and mirrored characters shape in
    /// linear time — a quadratic search would take minutes.
    /// </summary>
    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void E11_LongRowsOfMarksJoinersAndCapitals_StayLinear()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var noto = TextShaper.For(Ot02ArabicFont);
        var marks = noto.Shape("\u0628" + string.Concat(Enumerable.Repeat("\u0650\u0651", 10_000)));
        Assert.Equal(20_001, marks.Sum(g => g.Text.Length));
        var layout = RichLayout.Layout(new RichText("\u0647" + new string('\u200D', 20_000), new RichStyle { Family = "Liberation Sans", Size = 10 }), 1e6,
            BlockAlignment.Left, 1.2, 10, 0);
        Assert.NotEmpty(layout.Lines);
        Ot02Shaper(gpos: E11Layout("latn", ("cpsp", Ot02Lookup(1, E11SingleAdvance([Ot02G('A')], 50))))).Shape(new string('A', 20_000));
        noto.Shape("\u0628" + new string('∂', 20_000));
        Assert.True(watch.ElapsedMilliseconds < 20_000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    // ------------------------------------------------------------ question 46: AMTRA

    /// <summary>
    /// Unicode 9.2.1 sentences 73–75 (UTR #53, measured against Noto on 26.11.2026): canonically equivalent mark sequences look the same —
    /// Beh with fatha/kasra/dammatan/fathatan/sukun and shadda, Waw with damma and hamza above, Beh with hamza above and below, Lam with shadda and
    /// superscript alef, each in both orders, in Noto Sans Arabic and Noto Naskh Arabic: the same glyphs at the same places in both orders, and
    /// those of the reference shaper; the glyphs keep their characters. Before, 17 of the 20 pairs gave different pictures, e.g. kasra–shadda
    /// without the shadda-kasra ligature.
    /// </summary>
    [Theory]
    [InlineData("NotoSansArabic-Regular.ttf")]
    [InlineData("NotoNaskhArabic-Regular.ttf")]
    public void E11_Amtra_CanonicallyEquivalentMarksLookTheSame(string file)
    {
        string[][] pairs =
        [
            ["ب\u064E\u0651", "ب\u0651\u064E"], ["ب\u0650\u0651", "ب\u0651\u0650"], ["ب\u064C\u0651", "ب\u0651\u064C"],
            ["ب\u064B\u0651", "ب\u0651\u064B"], ["و\u064F\u0654", "و\u0654\u064F"], ["ب\u0651\u0652", "ب\u0652\u0651"],
            ["ب\u0654\u0655", "ب\u0655\u0654"], ["ل\u0651\u0670", "ل\u0670\u0651"],
        ];
        var font = EmbeddedFonts.ArabicOpenType(file);
        var shaper = TextShaper.For(font);
        foreach (var pair in pairs)
        {
            var first = shaper.Shape(pair[0]);
            var second = shaper.Shape(pair[1]);
            Assert.Equal(E11Drawn(first), E11Drawn(second));
            var reference = E11HarfBuzzDrawn(font.FileData, font.UnitsPerEm, pair[0], true);
            Assert.Equal(reference.Select(g => g.Glyph), E11Drawn(first).Select(g => g.Glyph));
            Assert.All(reference.Zip(E11Drawn(first)), p => Assert.True(Math.Abs(p.First.X - p.Second.X) <= 1 && Math.Abs(p.First.Y - p.Second.Y) <= 1, pair[0]));
            Assert.Equal(pair[0].Order(), string.Concat(first.Select(g => g.Text)).Order());
        }
        // kasra (class 32) typed before fatha (class 30) is drawn after it, in canonical order — the glyphs of beh, fatha, kasra typed in that
        // order; the cluster carries the text as typed (docs/FEHLERGEDAECHTNIS.md, No. 1180)
        Assert.Equal(shaper.Shape("ب\u064E\u0650").Select(g => g.Glyph), shaper.Shape("ب\u0650\u064E").Select(g => g.Glyph));
        Assert.Equal(["ب\u0650\u064E"], shaper.Shape("ب\u0650\u064E").Select(g => g.Text).Where(t => t.Length > 0));
    }
}
