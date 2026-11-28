using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-51 (OpenType feature registry, page 66: the registered features k–o, and the start of page 67: palt … pref). The optional
/// features of these pages stay off by default and apply only when a caller names them in
/// <see cref="TextShaper.Shape(string, IEnumerable{string}?)"/>; no caller does, so the text layout of the editor never applies them to a whole
/// run (the optical bounds lfbd, rtbd and opbd it applies to the glyphs at the ends of its lines, docs/FEHLERGEDAECHTNIS.md, No. 1220, package
/// E29). The fonts are
/// Liberation Sans with GSUB or GPOS built by hand (helpers of packages ot-49, ot-02, E11 and E12) and the bundled fonts that carry a feature
/// themselves (Patrick Hand, Noto Sans Arabic, Noto Sans CJK).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-51)

    /// <summary>The glyphs the shaper of a parsed font makes of <paramref name="text"/> with the features given.</summary>
    private static int[] Ot51Glyphs(OpenTypeFont font, string text, params string[] features) =>
        [.. TextShaper.For(font).Shape(text, features).Select(g => g.Glyph)];

    /// <summary>The glyphs of the cmap of a parsed font for <paramref name="text"/>, one per character.</summary>
    private static int[] Ot51Cmap(OpenTypeFont font, string text) => [.. text.EnumerateRunes().Select(r => font.GlyphForUnicode(r.Value))];

    // ------------------------------------------------------------ optional substitutions: off by default

    /// <summary>
    /// The optional single substitutions of pages 66 and 67 (first part), each off by default and applied only when named: OT 66.5 sentences
    /// 1–8 (lnum: „1“), OT 66.19 sentences 1–7 (onum: „1“), OT 66.12 sentences 1–8 and 9–12 (mgrk: the capital sigma, script grek), OT 66.16
    /// sentences 1–8 (nlck: a kanji), OT 66.18 sentences 1–6 and 7–12 (numr: the „1“ of „1/2“ — PDF-Knead applies frac and with it numr never by
    /// itself), OT 66.21 sentences 1–6 (ordn: the „o“ of „2o“), OT 66.22 sentences 10–19 (ornm: the bullet), OT 67.2 sentences 1–16 and 17–23
    /// (pcap: „a“), OT 67.3 sentences 1–6 (pkna: a katakana) and OT 67.4 sentences 1–8 (pnum: „1“). A type 1 lookup to another glyph under the
    /// feature in the script of the text: the default shaping keeps the glyphs of the cmap; with the feature named, the character becomes the
    /// other glyph.
    /// </summary>
    [Theory]
    [InlineData("lnum", "1", "1", "latn")]
    [InlineData("onum", "1", "1", "latn")]
    [InlineData("mgrk", "Σ", "Σ", "grek")]
    [InlineData("nlck", "辻", "辻", "hani")]
    [InlineData("numr", "1/2", "1", "latn")]
    [InlineData("ordn", "2o", "o", "latn")]
    [InlineData("ornm", "•", "•", "latn")]
    [InlineData("pcap", "a", "a", "latn")]
    [InlineData("pkna", "ア", "ア", "kana")]
    [InlineData("pnum", "1", "1", "latn")]
    public void Ot51_OptionalSingleSubstitution_OffByDefault_AppliedOnlyWhenNamed(string feature, string text, string from, string script) =>
        Ot49_OptionalSingleSubstitution_OffByDefault_AppliedOnlyWhenNamed(feature, text, from, script);

    /// <summary>
    /// OT 66.5 sentences 9–15 and OT 66.19 sentences 8–14 with a real font: Patrick Hand has lnum and onum — „1234567890“ shaped by default keeps
    /// the glyphs of its cmap (neither figure style is switched on); with onum named the figures change, so the default really leaves the
    /// feature off.
    /// </summary>
    [Fact]
    public void Ot51_PatrickHand_FigureStyles_OffByDefault()
    {
        const string digits = "1234567890";
        var font = OpenTypeFont.Parse(EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf"));
        Assert.Equal(Ot51Cmap(font, digits), Ot51Glyphs(font, digits));
        Assert.NotEqual(Ot51Cmap(font, digits), Ot51Glyphs(font, digits, "onum"));
    }

    /// <summary>
    /// OT 66.21 sentences 7–13 with a real font: Patrick Hand has ordn — „2o“ shaped by default keeps the glyphs of its cmap; with ordn named
    /// the „o“ after the figure takes its ordinal form (the context of the lookup is heeded).
    /// </summary>
    [Fact]
    public void Ot51_PatrickHand_Ordinals_OffByDefault()
    {
        var font = OpenTypeFont.Parse(EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf"));
        var cmap = Ot51Cmap(font, "2o");
        Assert.Equal(cmap, Ot51Glyphs(font, "2o"));
        var ordinal = Ot51Glyphs(font, "2o", "ordn");
        Assert.Equal(cmap[0], ordinal[0]);
        Assert.NotEqual(cmap[1], ordinal[^1]);
    }

    /// <summary>
    /// OT 67.4 sentences 9–13 and OT 67.35 sentences 9–13 with a real font: Noto Sans Arabic has pnum and tnum — the Arabic-Indic digit one
    /// shaped by default keeps the glyph of its cmap (neither figure width is switched on); with pnum named it takes another glyph.
    /// </summary>
    [Fact]
    public void Ot51_NotoArabic_FigureWidths_OffByDefault()
    {
        const string one = "١";
        var font = EmbeddedFonts.ArabicOpenType("NotoSansArabic-Regular.ttf");
        Assert.Equal(Ot51Cmap(font, one), Ot51Glyphs(font, one));
        Assert.NotEqual(Ot51Cmap(font, one), Ot51Glyphs(font, one, "pnum"));
    }

    // ------------------------------------------------------------ 66.2 lfbd, 66.20 opbd, 67.16 rtbd

    /// <summary>
    /// OT 66.2 sentences 1–6 and 7–11 (lfbd), OT 66.20 sentences 1–8 and 9–15 (opbd), OT 67.16 sentences 1–6 and 7–11 (rtbd): optical bounds
    /// are off by default for a run — a single adjustment with XAdvance −50 under the feature for „T“ (lfbd, opbd) or „r“ (rtbd): the default
    /// shaping keeps the advance of the font, with the feature named it is 50 smaller (at the ends of a line the layout applies them, package E29).
    /// </summary>
    [Theory]
    [InlineData("lfbd", 'T')]
    [InlineData("opbd", 'T')]
    [InlineData("rtbd", 'r')]
    public void Ot51_OpticalBounds_OffByDefault_AppliedOnlyWhenNamed(string feature, char letter)
    {
        var g = Ot02G(letter);
        var text = letter.ToString();
        var font = OpenTypeFont.Parse(Ot49Font(text, null, Ot49Layout(["latn"], [(feature, Ot02Lookup(1, E11SingleAdvance([g], -50)))])));
        var shaper = TextShaper.For(font);
        Assert.Equal(font.AdvanceWidth(g), shaper.Shape(text)[0].XAdvance);
        Assert.Equal(font.AdvanceWidth(g) - 50, shaper.Shape(text, [feature])[0].XAdvance);
    }

    // ------------------------------------------------------------ 66.14 mset

    /// <summary>
    /// OT 66.14 sentences 1–9 and 10–13 (mset, today's behaviour): PDF-Knead does not apply mset — an Arabic font with a type 1 lookup Fatha →
    /// another glyph under mset (script arab): Beh with Fatha, and two of them in one run, keep the Fatha glyph of the cmap; only with mset named
    /// does the lookup act, on every Fatha of the run.
    /// </summary>
    [Fact]
    public void Ot51_Mset_NotApplied_FathaKeepsTheCmapGlyph()
    {
        int beh = Ot50A(0x0628), fatha = Ot50A(0x064E);
        var font = OpenTypeFont.Parse(E12Font(Ot50Arabic, Ot49Layout(["arab"], [("mset", E11Sub((fatha, Ot49Form)))])));
        Assert.Equal([beh, fatha], Ot51Glyphs(font, "ب\u064E"));
        Assert.Equal([beh, fatha, beh, fatha], Ot51Glyphs(font, "ب\u064Eب\u064E"));
        Assert.Equal([beh, Ot49Form, beh, Ot49Form], Ot51Glyphs(font, "ب\u064Eب\u064E", "mset"));
    }

    // ------------------------------------------------------------ 67.1 palt, 67.9 pwid

    /// <summary>
    /// OT 67.1 sentences 1–9 (palt) and OT 67.9 sentences 1–7 (pwid) with a real font: Noto Sans CJK JP has palt in its GPOS and pwid in its
    /// GSUB — the katakana A shaped by default keeps the glyph of its cmap and the full advance of the font, with palt named its advance is
    /// smaller; the full-width Latin A (which the pwid lookups of the font cover) keeps the glyph of its cmap by default
    /// and becomes another (proportional) glyph with pwid named.
    /// </summary>
    [Fact]
    public void Ot51_NotoCjk_PaltAndPwid_OffByDefault_AppliedOnlyWhenNamed()
    {
        const string a = "ア";
        var font = Iso03NotoJp.Value;
        var shaper = TextShaper.For(font);
        var cmap = font.GlyphForUnicode(0x30A2);
        var plain = shaper.Shape(a);
        Assert.Equal(cmap, plain[0].Glyph);
        Assert.Equal(font.AdvanceWidth(cmap), plain[0].XAdvance);
        Assert.True(shaper.Shape(a, ["palt"])[0].XAdvance < font.AdvanceWidth(cmap));
        const string wide = "Ａ";
        var wideCmap = font.GlyphForUnicode(0xFF21);
        Assert.Equal(wideCmap, shaper.Shape(wide)[0].Glyph);
        Assert.NotEqual(wideCmap, shaper.Shape(wide, ["pwid"])[0].Glyph);
    }
}
