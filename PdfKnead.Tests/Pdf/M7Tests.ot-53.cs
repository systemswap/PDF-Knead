using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-53 (OpenType feature registry, end of page 67: subs … twid, and page 68: the registered features u–z). The optional features
/// stay off by default and apply only when named; PDF-Knead sets its own texts horizontally only, and the features of vertical setting are never
/// applied by default. The fonts are Liberation Sans with GSUB or GPOS built by hand (helpers of packages ot-49, ot-02 and E11) and the bundled
/// fonts that carry a feature themselves (Liberation Sans: subs, sups; Amatic SC: zero; Noto Sans CJK: vkrn).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ optional substitutions: off by default

    /// <summary>
    /// The optional single substitutions of this part, each off by default and applied only when named: OT 67.29 sentences 1–5 (subs: „2“), OT
    /// 67.30 sentences 1–5 (sups: „2“), OT 67.32 sentences 1–6 and 7–10 (titl: „A“), OT 67.34 sentences 1–7 and 8–14 (tnam: a kanji; Noto Sans
    /// CJK has no tnam lookups, so the font is built), OT 67.35 sentences 1–8 (tnum: „1“), OT 67.37 sentences 1–6 (twid: „1“), OT 68.1
    /// sentences 1–8 and 9–12 (unic: „A“) and OT 68.14 sentences 1–7 (zero: „0“). A type 1 lookup to another glyph under the feature in the
    /// script of the text: the default shaping keeps the glyphs of the cmap; with the feature named, the character becomes the other glyph.
    /// </summary>
    [Theory]
    [InlineData("subs", "2", "2", "latn")]
    [InlineData("sups", "2", "2", "latn")]
    [InlineData("titl", "A", "A", "latn")]
    [InlineData("tnam", "亜", "亜", "hani")]
    [InlineData("tnum", "1", "1", "latn")]
    [InlineData("twid", "1", "1", "latn")]
    [InlineData("unic", "A", "A", "latn")]
    [InlineData("zero", "0", "0", "latn")]
    public void Ot53_OptionalSingleSubstitution_OffByDefault_AppliedOnlyWhenNamed(string feature, string text, string from, string script) =>
        Ot49_OptionalSingleSubstitution_OffByDefault_AppliedOnlyWhenNamed(feature, text, from, script);

    /// <summary>
    /// OT 67.29 sentences 6–11 (subs) and OT 67.30 sentences 6–11 (sups) with a real font: Liberation Sans has subs and sups — „2“ shaped by
    /// default keeps the glyph of its cmap; with subs named it becomes the inferior figure. Its sups lookups cover the figures from „4“ on (the
    /// shaping library finds the same): „4“ keeps the glyph of its cmap by default and becomes the superior figure with sups.
    /// </summary>
    [Fact]
    public void Ot53_Liberation_SubsAndSups_OffByDefault()
    {
        int two = Ot02G('2'), four = Ot02G('4');
        var font = ShaperLimitTests.Liberation;
        Assert.Equal([two, four], Ot49Shape(font, "24"));
        Assert.NotEqual(two, Ot49Shape(font, "2", "subs")[0]);
        Assert.NotEqual(four, Ot49Shape(font, "4", "sups")[0]);
    }

    /// <summary>
    /// OT 68.14 sentences 8–12 (zero) with a real font: Amatic SC has zero — „0“ shaped by default keeps the glyph of its cmap; with zero named it
    /// becomes the slashed zero.
    /// </summary>
    [Fact]
    public void Ot53_AmaticSc_SlashedZero_OffByDefault()
    {
        var font = OpenTypeFont.Parse(EmbeddedFonts.LoadHandwriting("AmaticSC-Regular.ttf"));
        var cmap = font.GlyphForUnicode('0');
        Assert.Equal([cmap], Ot51Glyphs(font, "0"));
        Assert.NotEqual(cmap, Ot51Glyphs(font, "0", "zero")[0]);
    }

    // ------------------------------------------------------------ vertical features

    /// <summary>
    /// The features of vertical setting are never applied to the horizontal lines PDF-Knead sets: OT 68.2 sentences 1–9 and 10–14 (valt), OT
    /// 68.3 sentences 1–9 and 10–19 (vapk), OT 68.5 sentences 1–13 and 22–30 (vchw), OT 68.7 sentences 1–7 and 8–14 (vhal), OT 68.9 sentences
    /// 1–15 (vkna), OT 68.10 sentences 1–10, 22–35 and 36–42 (vkrn), OT 68.11 sentences 1–8 and 9–17 (vpal), OT 68.12 sentences 1–8 and 16–20
    /// (vrt2) and OT 68.13 sentences 1–8 and 9–16 (vrtr). None of them is among the default features; Liberation Sans with a type 1
    /// substitution a → other glyph (vkna, vrt2, vrtr) or a single adjustment of the advance by −50 for „a“ (the positioning features) under
    /// the feature: „a“ shaped by default keeps the glyph of the cmap and the advance of the font.
    /// </summary>
    [Theory]
    [InlineData("valt", true)]
    [InlineData("vapk", true)]
    [InlineData("vchw", true)]
    [InlineData("vhal", true)]
    [InlineData("vkna", false)]
    [InlineData("vkrn", true)]
    [InlineData("vpal", true)]
    [InlineData("vrt2", false)]
    [InlineData("vrtr", false)]
    public void Ot53_VerticalFeatures_NeverAppliedByDefault(string feature, bool positioning)
    {
        Assert.DoesNotContain(feature, TextShaper.DefaultSubstitutions);
        Assert.DoesNotContain(feature, TextShaper.DefaultPositioning);
        var a = Ot02G('a');
        var layout = Ot49Layout(["latn"], [(feature, positioning ? Ot02Lookup(1, E11SingleAdvance([a], -50)) : E11Sub((a, Ot49Form)))]);
        var font = OpenTypeFont.Parse(positioning ? Ot49Font("a", null, layout) : Ot49Font("a", layout));
        var shaped = TextShaper.For(font).Shape("a");
        Assert.Equal(a, shaped[0].Glyph);
        Assert.Equal(font.AdvanceWidth(a), shaped[0].XAdvance);
        Assert.Equal(0, shaped[0].XOffset);
        Assert.Equal(0, shaped[0].YOffset);
    }

    /// <summary>
    /// OT 68.10 sentences 1–10 (vkrn) with a real font: Noto Sans CJK JP has vkrn in its GPOS — the katakana SU and TO (the pair of the
    /// registry's example) shaped horizontally take no vertical kerning: both glyphs keep their full advance and stand without offset.
    /// </summary>
    [Fact]
    public void Ot53_NotoCjk_Vkrn_NotAppliedHorizontally()
    {
        const string text = "スト";
        var font = Iso03NotoJp.Value;
        var shaped = TextShaper.For(font).Shape(text);
        Assert.Equal(Ot51Cmap(font, text), shaped.Select(g => g.Glyph));
        Assert.All(shaped, g =>
        {
            Assert.Equal(font.AdvanceWidth(g.Glyph), g.XAdvance);
            Assert.Equal(0, g.XOffset);
            Assert.Equal(0, g.YOffset);
            Assert.Equal(0, g.YAdvance);
        });
    }
}
