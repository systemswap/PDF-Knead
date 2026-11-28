using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-52 (OpenType feature registry, page 67: the registered features pres … stch). The optional features of this part stay off by
/// default and apply only when named; kern is switched off for a run with a feature of fixed widths. rand, on by default since
/// docs/FEHLERGEDAECHTNIS.md, No. 1223, is tested in package E29. The fonts are Liberation Sans with GSUB or GPOS built by hand (helpers of
/// packages ot-49, ot-02 and E11).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ optional substitutions: off by default

    /// <summary>
    /// The optional single substitutions of this part of page 67, each off by default and applied only when named: OT 67.10 sentences 1–6 (qwid:
    /// „1“), OT 67.19 sentences 1–11 and 12–15 (ruby: a hiragana), OT 67.22 sentences 1–6 and 7–10 (sinf: „2“), OT 67.25 sentences 1–9 and 10–14
    /// (smpl: a traditional hanzi), OT 67.27 sentences 1–14 and 15–21 (ssty: „a“ — PDF-Knead sets no formulas). The ruby lookups of Noto Sans CJK
    /// JP leave the kana unchanged even when named (also in the shaping library), and the font has no smpl, so the fonts are built: a type 1
    /// lookup to another glyph under the feature in the script of the text — the default shaping keeps the glyphs of the cmap; with the feature
    /// named, the character becomes the other glyph.
    /// </summary>
    [Theory]
    [InlineData("qwid", "1", "1", "latn")]
    [InlineData("ruby", "あ", "あ", "kana")]
    [InlineData("sinf", "2", "2", "latn")]
    [InlineData("smpl", "臺", "臺", "hani")]
    [InlineData("ssty", "a", "a", "latn")]
    public void Ot52_OptionalSingleSubstitution_OffByDefault_AppliedOnlyWhenNamed(string feature, string text, string from, string script) =>
        Ot49_OptionalSingleSubstitution_OffByDefault_AppliedOnlyWhenNamed(feature, text, from, script);

    // ------------------------------------------------------------ 67.10 qwid, 67.37 twid: kern off

    /// <summary>
    /// OT 67.10 sentences 7–12 (qwid) and OT 67.37 sentences 7–12 (twid): with a feature of fixed widths kern is off for the run — Liberation Sans
    /// with the feature in its GSUB (a type 1 lookup for „1“) and a kern pair A–V of −100 in its GPOS: „AV“ is kerned by default and not with
    /// the feature named. Before docs/FEHLERGEDAECHTNIS.md, No. 1174, kern stayed on.
    /// </summary>
    [Theory]
    [InlineData("qwid")]
    [InlineData("twid")]
    public void Ot52_FixedWidthFeature_SwitchesKernOff(string feature)
    {
        int a = Ot02G('A'), v = Ot02G('V'), one = Ot02G('1');
        var font = OpenTypeFont.Parse(Ot49Font("AV1", Ot49Layout(["latn"], [(feature, E11Sub((one, Ot49Form)))]),
            Ot49Layout(["latn"], [("kern", Ot02Lookup(2, Ot02Pairs(0, (a, v, -100))))])));
        var plain = font.AdvanceWidth(a) + font.AdvanceWidth(v);
        var shaper = TextShaper.For(font);
        Assert.Equal(plain - 100, shaper.Shape("AV").Sum(g => g.XAdvance));
        Assert.Equal(plain, shaper.Shape("AV", [feature]).Sum(g => g.XAdvance));
    }

    // ------------------------------------------------------------ 67.26 ss01 … ss20

    /// <summary>
    /// OT 67.26 sentences 1–13 and 22–31 (stylistic sets): off by default, each applied when named and freely combinable — Liberation Sans with
    /// type 1 lookups under ss01 (a → x), ss02 (b → y) and ss20 (c → z): „abc“ keeps the glyphs of the cmap by default; with ss20 named only c is
    /// replaced, with ss01 and ss02 named together both a and b.
    /// </summary>
    [Fact]
    public void Ot52_StylisticSets_OffByDefault_CombineWhenNamed()
    {
        int a = Ot02G('a'), b = Ot02G('b'), c = Ot02G('c');
        var font = Ot49Font("abc", Ot49Layout(["latn"], [("ss01", E11Sub((a, Ot49Form))), ("ss02", E11Sub((b, Ot49Form2))), ("ss20", E11Sub((c, Ot49Form3)))]));
        Assert.Equal([a, b, c], Ot49Shape(font, "abc"));
        Assert.Equal([a, b, Ot49Form3], Ot49Shape(font, "abc", "ss20"));
        Assert.Equal([Ot49Form, Ot49Form2, c], Ot49Shape(font, "abc", "ss01", "ss02"));
    }
}
