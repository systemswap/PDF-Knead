using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package uni-11 (The Unicode Standard 16.0.0, chapter 13, sections 13.6 to 13.23: Mro, Warang Citi, Ol Chiki, Ol Onal, Nag Mundari,
/// Wancho, Toto, Tangsa, Sunuwar): the rows of the draft that still asked for a test - the Bidi classes of the left-to-right alphabets and
/// their tone marks, the glyphs of the cmap in text order without lookups, marks attached by the GPOS of the font under the tag of the script,
/// modifier letters with their advance, a ligature across ZWJ and the forms of combining marks under the tag of the Sunuwar run. The fonts
/// are Liberation Sans with a cmap that maps the block of the script to glyphs 100 ... and with GSUB or GPOS built by hand (forms of lookups
/// from 400 on). Test inputs outside ASCII are written as escapes.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (uni-11)

    /// <summary>A font for <paramref name="length"/> code points from <paramref name="block"/> on (glyphs 100 ...) with the tables given.</summary>
    private static byte[] Uni11Font(int block, int length, byte[]? gsub = null, byte[]? gpos = null) => E17Block(block, length, gsub, gpos);

    /// <summary>A GPOS of one script whose feature "mark" has one mark-to-base lookup per (mark, base, base anchor); mark anchors at (0, 0).</summary>
    private static byte[] Uni11Marks(string script, params (int Mark, int Base, (int X, int Y) Anchor)[] attachments) =>
        E17Layout(script, [("mark", [.. Enumerable.Range(0, attachments.Length)])],
            [.. attachments.Select(a => E17Attach(4, a.Mark, a.Base, (0, 0), a.Anchor))]);

    /// <summary>Asserts the glyphs of the cmap in text order, one per code point, each with an advance.</summary>
    private static void Uni11CmapGlyphsInOrder(byte[] font, int block, params int[] codePoints)
    {
        var shaped = E12Shape(font, string.Concat(codePoints.Select(char.ConvertFromUtf32)));
        Assert.Equal(codePoints.Select(c => E12G(block, c)), shaped.Select(g => g.Glyph));
        Assert.All(shaped, g => Assert.True(g.XAdvance > 0));
    }

    /// <summary>Asserts that the second glyph of a base and a mark has no advance and is drawn at the anchor of the base.</summary>
    private static void Uni11AttachedAt(byte[] font, string text, int mark, (int X, int Y) anchor)
    {
        var shaped = E12Shape(font, text);
        Assert.Equal(2, shaped.Length);
        Assert.Equal(mark, shaped[1].Glyph);
        Assert.Equal(0, shaped[1].XAdvance);
        Assert.Equal((mark, (double)anchor.X, (double)anchor.Y), E11Drawn(shaped)[1]);
    }

    private const int Uni11Mro = 0x16A40, Uni11Warang = 0x118A0, Uni11OlChiki = 0x1C50, Uni11OlOnal = 0x1E5D0, Uni11NagMundari = 0x1E4D0,
        Uni11Wancho = 0x1E2C0, Uni11Toto = 0x1E290, Uni11Tangsa = 0x16A70, Uni11Sunuwar = 0x11BC0;

    // ------------------------------------------------------------ 13.8.1 Mro, 13.9.1 Warang Citi

    /// <summary>
    /// UNI 13.8.1 sentences 6-8: Mro runs left to right without combining marks - U+16A40, U+16A60 and U+16A6E have the Bidi class L; after a
    /// Hebrew letter and a space two Mro letters are at level 2 in text order; shaped in a font for the block without lookups, the glyphs of the
    /// cmap in text order.
    /// </summary>
    [Fact]
    public void Uni11_Mro_LeftToRight_CmapGlyphsInOrder()
    {
        foreach (var c in new[] { 0x16A40, 0x16A60, 0x16A6E })
            Assert.Equal(BidiClass.L, Bidi.ClassOf(c));
        Assert.Equal(new byte[] { 1, 1, 2, 2, 2, 2 }, Bidi.Levels("\u05D0 \U00016A40\U00016A41"));
        Uni11CmapGlyphsInOrder(Uni11Font(Uni11Mro, 0x30), Uni11Mro, 0x16A40, 0x16A41, 0x16A42);
    }

    /// <summary>
    /// UNI 13.9.1 sentences 10-14: Warang Citi is an alphabet written left to right - U+118A0, U+118C0 and U+118E0 have the Bidi class L; U+118C1
    /// U+118C2 shaped in a font for the block without lookups give the glyphs of the cmap in text order.
    /// </summary>
    [Fact]
    public void Uni11_WarangCiti_LeftToRight_CmapGlyphsInOrder()
    {
        foreach (var c in new[] { 0x118A0, 0x118C0, 0x118E0 })
            Assert.Equal(BidiClass.L, Bidi.ClassOf(c));
        Uni11CmapGlyphsInOrder(Uni11Font(Uni11Warang, 0x60), Uni11Warang, 0x118C1, 0x118C2);
    }

    /// <summary>
    /// UNI 13.9.1 sentences 15-18: the few ligatures of Warang Citi are asked for with ZWJ - a font with the liga ligature C C under "wara" (and
    /// a glyph for ZWJ): C ZWJ C gives the ligature, the ZWJ passed over in its input and its text in the ligature's text; C C alone gives the
    /// ligature as well (docs/FEHLERGEDAECHTNIS.md, No. 1183: before, the ZWJ in the input kept the ligature from forming).
    /// </summary>
    [Fact]
    public void Uni11_WarangCiti_LigatureAcrossZwj()
    {
        int c = E12G(Uni11Warang, 0x118C1);
        var font = Uni11Font(Uni11Warang, 0x60, E17Layout("wara", [("liga", [0])], E12Lig(([c, c], E12F(0)))));
        const string text = "\U000118C1\u200D\U000118C1";
        var shaped = E12Shape(font, text);
        Assert.Equal([E12F(0)], Uni10Drawn(shaped));
        Assert.Equal(text, E19Text(shaped));
        Assert.Equal([E12F(0)], E12Own(font, "\U000118C1\U000118C1"));
    }

    // ------------------------------------------------------------ 13.10.1 Ol Chiki

    /// <summary>
    /// UNI 13.10.1 sentences 8-13: Ol Chiki is a left-to-right alphabet whose modifier letters are not combining - U+1C5A and U+1C78 have the
    /// Bidi class L; U+1C5A U+1C78 shaped in a font with a GPOS under "olck" (a mark lookup for U+0301) give two glyphs, U+1C78 (Lm) with its
    /// own advance from hmtx, not drawn as a mark.
    /// </summary>
    [Fact]
    public void Uni11_OlChiki_ModifierLetterKeepsItsAdvance()
    {
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x1C5A));
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x1C78));
        int la = E12G(Uni11OlChiki, 0x1C5A), mu = E12G(Uni11OlChiki, 0x1C78);
        var gpos = Uni11Marks("olck", (500, la, (300, 1400)));
        var font = E17Font(null, gpos, (Uni11OlChiki, Uni11OlChiki + 0x2F, E12Base), (0x0301, 0x0301, 500));
        var shaped = E12Shape(font, "\u1C5A\u1C78");
        Assert.Equal([la, mu], shaped.Select(g => g.Glyph));
        Assert.Equal(OpenTypeFont.Parse(font).AdvanceWidth(mu), shaped[1].XAdvance);
        Assert.True(shaped[1].XAdvance > 0);
        Assert.Equal(0, shaped[1].XOffset);
    }

    /// <summary>
    /// UNI 13.10.1 sentences 32-34: ligatures are the font's - a font with the liga ligature U+1C5C U+1C7D under "olck": the text gives the
    /// ligature with the text of both characters; with "-liga" two glyphs.
    /// </summary>
    [Fact]
    public void Uni11_OlChiki_LigatureFromTheFont()
    {
        int a = E12G(Uni11OlChiki, 0x1C5C), b = E12G(Uni11OlChiki, 0x1C7D);
        var font = Uni11Font(Uni11OlChiki, 0x30, E17Layout("olck", [("liga", [0])], E12Lig(([a, b], E12F(0)))));
        var shaped = E12Shape(font, "\u1C5C\u1C7D");
        Assert.Equal([E12F(0)], shaped.Select(g => g.Glyph));
        Assert.Equal("\u1C5C\u1C7D", shaped[0].Text);
        Assert.Equal([a, b], E19Shape(font, "\u1C5C\u1C7D", "-liga").Select(g => g.Glyph));
    }

    // ------------------------------------------------------------ 13.11.1 Ol Onal, 13.12.1 Nag Mundari

    /// <summary>
    /// UNI 13.11.1 sentences 9-13: Ol Onal is a left-to-right alphabet with additional signs - U+1E5D0 and U+1E5F1 have the Bidi class L, the
    /// sign Mu U+1E5EE is NSM.
    /// </summary>
    [Fact]
    public void Uni11_OlOnal_BidiClasses()
    {
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x1E5D0));
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x1E5F1));
        Assert.Equal(BidiClass.NSM, Bidi.ClassOf(0x1E5EE));
    }

    /// <summary>
    /// UNI 13.11.1 sentences 14-20: Mu U+1E5EE above and Ikir U+1E5EF below the letter A, both at once - with a GPOS mark lookup under "onao"
    /// for each, A Mu Ikir gives both marks without advance at the anchors above and below A; Hoddond U+1E5F0 is a letter: after Ab it is a
    /// second glyph with an advance.
    /// </summary>
    [Fact]
    public void Uni11_OlOnal_MuAndIkirOnA_HoddondALetter()
    {
        int a = E12G(Uni11OlOnal, 0x1E5D6), mu = E12G(Uni11OlOnal, 0x1E5EE), ikir = E12G(Uni11OlOnal, 0x1E5EF);
        var font = Uni11Font(Uni11OlOnal, 0x30, null, Uni11Marks("onao", (mu, a, (300, 1400)), (ikir, a, (320, -200))));
        var shaped = E12Shape(font, "\U0001E5D6\U0001E5EE\U0001E5EF");
        Assert.Equal([a, mu, ikir], shaped.Select(g => g.Glyph));
        Assert.Equal(0, shaped[1].XAdvance);
        Assert.Equal(0, shaped[2].XAdvance);
        var drawn = E11Drawn(shaped);
        Assert.Equal((mu, 300.0, 1400.0), drawn[1]);
        Assert.Equal((ikir, 320.0, -200.0), drawn[2]);
        var hoddond = E12Shape(font, "\U0001E5D8\U0001E5F0");
        Assert.Equal([E12G(Uni11OlOnal, 0x1E5D8), E12G(Uni11OlOnal, 0x1E5F0)], hoddond.Select(g => g.Glyph));
        Assert.All(hoddond, g => Assert.True(g.XAdvance > 0));
    }

    /// <summary>
    /// UNI 13.12.1 sentences 3-7: Nag Mundari is a left-to-right alphabet - U+1E4D0 and U+1E4F0 have the Bidi class L; shaped in a font for the
    /// block without lookups, the glyphs of the cmap in text order.
    /// </summary>
    [Fact]
    public void Uni11_NagMundari_LeftToRight_CmapGlyphsInOrder()
    {
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x1E4D0));
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x1E4F0));
        Uni11CmapGlyphsInOrder(Uni11Font(Uni11NagMundari, 0x30), Uni11NagMundari, 0x1E4D0, 0x1E4D1, 0x1E4E6);
    }

    /// <summary>
    /// UNI 13.12.1 sentences 8-16: the signs U+1E4EC-U+1E4EF are marks, Ojod U+1E4EB a modifier letter - with GPOS mark lookups under "nagm",
    /// a vowel U+1E4E6 with Muhor U+1E4EC gets the mark without advance at the font's anchor above right, a consonant U+1E4D1 with Sutuh
    /// U+1E4EF the mark attached likewise; Ojod after a letter keeps its advance.
    /// </summary>
    [Fact]
    public void Uni11_NagMundari_SignsAttachedOjodWithAdvance()
    {
        int vowel = E12G(Uni11NagMundari, 0x1E4E6), muhor = E12G(Uni11NagMundari, 0x1E4EC), consonant = E12G(Uni11NagMundari, 0x1E4D1),
            sutuh = E12G(Uni11NagMundari, 0x1E4EF), ojod = E12G(Uni11NagMundari, 0x1E4EB);
        var font = Uni11Font(Uni11NagMundari, 0x30, null, Uni11Marks("nagm", (muhor, vowel, (700, 1300)), (sutuh, consonant, (400, -150))));
        Uni11AttachedAt(font, "\U0001E4E6\U0001E4EC", muhor, (700, 1300));
        Uni11AttachedAt(font, "\U0001E4D1\U0001E4EF", sutuh, (400, -150));
        var shaped = E12Shape(font, "\U0001E4D1\U0001E4EB");
        Assert.Equal([consonant, ojod], shaped.Select(g => g.Glyph));
        Assert.Equal(OpenTypeFont.Parse(font).AdvanceWidth(ojod), shaped[1].XAdvance);
    }

    // ------------------------------------------------------------ 13.18.1 Wancho, 13.19.1 Toto, 13.20.1 Tangsa

    /// <summary>
    /// UNI 13.18.1 sentences 3-5: Wancho is a left-to-right alphabet with tone marks on vowels - U+1E2C0 and U+1E2F0 have the Bidi class L, the
    /// tone mark U+1E2EC NSM; shaped in a font for the block without lookups, the glyphs of the cmap in text order.
    /// </summary>
    [Fact]
    public void Uni11_Wancho_LeftToRight_CmapGlyphsInOrder()
    {
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x1E2C0));
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x1E2F0));
        Assert.Equal(BidiClass.NSM, Bidi.ClassOf(0x1E2EC));
        Uni11CmapGlyphsInOrder(Uni11Font(Uni11Wancho, 0x40), Uni11Wancho, 0x1E2C0, 0x1E2C1, 0x1E2C2);
    }

    /// <summary>
    /// UNI 13.18.1 sentences 6-13: the four tone marks U+1E2EC-U+1E2EF sit on the vowel - with GPOS mark lookups under "wcho", the vowel U+1E2C1
    /// with U+1E2EC and with U+1E2EF gets the tone mark without advance at its anchor.
    /// </summary>
    [Fact]
    public void Uni11_Wancho_ToneMarksAttachedToTheVowel()
    {
        int vowel = E12G(Uni11Wancho, 0x1E2C1), tup = E12G(Uni11Wancho, 0x1E2EC), koini = E12G(Uni11Wancho, 0x1E2EF);
        var font = Uni11Font(Uni11Wancho, 0x40, null, Uni11Marks("wcho", (tup, vowel, (350, 1350)), (koini, vowel, (360, 1500))));
        Uni11AttachedAt(font, "\U0001E2C1\U0001E2EC", tup, (350, 1350));
        Uni11AttachedAt(font, "\U0001E2C1\U0001E2EF", koini, (360, 1500));
    }

    /// <summary>
    /// UNI 13.19.1 sentences 4-5: Toto is a left-to-right alphabet - U+1E290 and U+1E2A7 have the Bidi class L, the tone mark U+1E2AE NSM.
    /// </summary>
    [Fact]
    public void Uni11_Toto_BidiClasses()
    {
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x1E290));
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x1E2A7));
        Assert.Equal(BidiClass.NSM, Bidi.ClassOf(0x1E2AE));
    }

    /// <summary>
    /// UNI 13.19.1 sentences 11-13: the one tone mark U+1E2AE sits above vowels - with a GPOS mark lookup under "toto", the vowel U+1E2A0 with
    /// U+1E2AE gets it without advance at the anchor above; the letter U+1E297, whose similar mark is part of the letter, stays one glyph.
    /// </summary>
    [Fact]
    public void Uni11_Toto_ToneMarkAboveTheVowel_LetterNotDecomposed()
    {
        int vowel = E12G(Uni11Toto, 0x1E2A0), tone = E12G(Uni11Toto, 0x1E2AE);
        var font = Uni11Font(Uni11Toto, 0x30, null, Uni11Marks("toto", (tone, vowel, (380, 1450))));
        Uni11AttachedAt(font, "\U0001E2A0\U0001E2AE", tone, (380, 1450));
        Assert.Equal([E12G(Uni11Toto, 0x1E297)], E12Own(font, "\U0001E297"));
    }

    /// <summary>
    /// UNI 13.20.1 sentences 5-6: Tangsa runs left to right - U+16A70 and U+16AC0 have the Bidi class L; shaped in a font for the block without
    /// lookups, the glyphs of the cmap in text order.
    /// </summary>
    [Fact]
    public void Uni11_Tangsa_LeftToRight_CmapGlyphsInOrder()
    {
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x16A70));
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x16AC0));
        Uni11CmapGlyphsInOrder(Uni11Font(Uni11Tangsa, 0x60), Uni11Tangsa, 0x16A70, 0x16A71, 0x16AC0);
    }

    // ------------------------------------------------------------ 13.21.1 Sunuwar

    /// <summary>
    /// UNI 13.21.1 sentences 7-8: Sunuwar runs horizontally left to right - U+11BC0 and U+11BF0 have the Bidi class L; after a Hebrew letter and
    /// a space two Sunuwar letters are at level 2 in text order.
    /// </summary>
    [Fact]
    public void Uni11_Sunuwar_LeftToRight()
    {
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x11BC0));
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x11BF0));
        Assert.Equal(new byte[] { 1, 1, 2, 2, 2, 2 }, Bidi.Levels("\u05D0 \U00011BC0\U00011BC1"));
    }

    /// <summary>
    /// UNI 13.21.1 sentences 20-29: Sangmilu U+0331 and Sangrums U+032D go with the Sunuwar run before them - in a font whose GPOS has mark
    /// lookups only under "sunu", U+11BC6 with U+0331 and with U+032D gets the mark without advance at the anchor of the letter (the run is
    /// shaped under "sunu", not DFLT or "latn"); U+11BC6 U+0331 U+11BD4 gives three glyphs in text order, no conjunct.
    /// </summary>
    [Fact]
    public void Uni11_Sunuwar_InheritedMarksShapedUnderTheScriptTag()
    {
        int letter = E12G(Uni11Sunuwar, 0x11BC6), macronBelow = 500 + 0x31, circumflexBelow = 500 + 0x2D;
        var font = E17Font(null, Uni11Marks("sunu", (macronBelow, letter, (300, -120)), (circumflexBelow, letter, (310, -140))),
            (Uni11Sunuwar, Uni11Sunuwar + 0x3F, E12Base), (0x0300, 0x033F, 500));
        Uni11AttachedAt(font, "\U00011BC6\u0331", macronBelow, (300, -120));
        Uni11AttachedAt(font, "\U00011BC6\u032D", circumflexBelow, (310, -140));
        Assert.Equal([letter, macronBelow, E12G(Uni11Sunuwar, 0x11BD4)], E12Own(font, "\U00011BC6\u0331\U00011BD4"));
    }

    /// <summary>
    /// UNI 13.21.1 sentences 35-44: the script-specific form of the combining candrabindu U+0310 and the tone marks are the font's under the tag
    /// of the run - a font with a ccmp substitution of U+0310 only under "sunu" and a mark lookup for U+0301: U+11BC2 U+0310 gives the Sunuwar
    /// form; U+11BC2 U+0301 gets the tone mark without advance at the anchor. (The same mark after a Latin letter:
    /// Uni11_Sunuwar_CandrabinduAfterLatinLetter_GlyphOfTheCmap.)
    /// </summary>
    [Fact]
    public void Uni11_Sunuwar_ScriptFormOfCandrabindu_ToneMarkAttached()
    {
        var font = Uni11SunuwarFont();
        Assert.Equal([E12G(Uni11Sunuwar, 0x11BC2), E12F(5)], E12Own(font, "\U00011BC2\u0310"));
        Uni11AttachedAt(font, "\U00011BC2\u0301", 500 + 0x01, (330, 1400));
    }

    /// <summary>
    /// UNI 13.21.1 sentences 35-44: the Sunuwar form of U+0310 belongs to the Sunuwar run only - in the font of
    /// Uni11_Sunuwar_ScriptFormOfCandrabindu_ToneMarkAttached (GSUB and GPOS with the one script "sunu", no DFLT, no "latn"), "a" U+0310 is
    /// shaped under "latn" and gives the glyph of the cmap for the mark, as the shaping library does (a script the font lacks, with neither
    /// DFLT nor "latn", has no lookups).
    /// </summary>
    [Fact]
    public void Uni11_Sunuwar_CandrabinduAfterLatinLetter_GlyphOfTheCmap()
    {
        var font = Uni11SunuwarFont();
        Assert.Equal([Ot02G('a'), 500 + 0x10], ShaperTests.HarfBuzz(font, 2048, "a\u0310", []).Glyphs);
        Assert.Equal([Ot02G('a'), 500 + 0x10], E12Own(font, "a\u0310"));
    }

    /// <summary>
    /// A font for Sunuwar (U+11BC0-U+11BFF), the combining marks U+0300-U+033F (glyphs 500 ...) and "a": a ccmp substitution of U+0310 and a mark
    /// lookup for U+0301 on U+11BC2, both only under "sunu".
    /// </summary>
    private static byte[] Uni11SunuwarFont()
    {
        int letter = E12G(Uni11Sunuwar, 0x11BC2), candrabindu = 500 + 0x10, acute = 500 + 0x01;
        var gsub = E17Layout("sunu", [("ccmp", [0])], E11Sub((candrabindu, E12F(5))));
        return E17Font(gsub, Uni11Marks("sunu", (acute, letter, (330, 1400))),
            (Uni11Sunuwar, Uni11Sunuwar + 0x3F, E12Base), (0x0300, 0x033F, 500), ('a', 'a', Ot02G('a')));
    }
}
