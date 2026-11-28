using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, decisions on own text (docs/FEHLERGEDAECHTNIS.md, No. 1162): the script taken from the text and runs split by script, language systems
/// chosen by the text's language (OpenType 2.4.1, 2.11, chapter 61); line metrics, underline, strikeout, italic angle and cap height from the
/// font (OpenType 31, 32); the legacy kern table with minimum values, cross-stream kerning, override and format 2 (OpenType 27, 3.15).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (E4b)

    /// <summary>A GSUB of single substitutions under „liga“, one script per entry: <c>(script tag, from, to)</c> — each script with its own lookup.</summary>
    private static Ot03Part E4bGsub(params (string Script, int From, int To)[] entries) =>
        Ot24Layout([.. entries.Select(e => Ot24Single((e.From, e.To)))],
            [.. entries.Select((e, i) => ("liga", new[] { i }))],
            [.. entries.Select((e, i) => (e.Script, new[] { i }))]);

    // ------------------------------------------------------------ script from the text (question 10)

    /// <summary>
    /// OT 2.4.1 sentences 10–11, OT 21.3 sentence 10, OT 61 (the script tags): the script of a run is taken from the text — a GSUB with one
    /// substitution of the run's glyph under „latn“ (→ X) and another under the tag of the text's script (→ Y): Greek under „grek“, Hebrew under
    /// „hebr“, Han under „hani“, Hiragana and Katakana under „kana“, Hangul under „hang“, Thai under „thai“, Devanagari under „dev2“, Lao under
    /// „lao “ — the substitution of the script applies, not that of „latn“. (Characters Liberation Sans lacks map to glyph 0, which the
    /// substitution covers as any other glyph.)
    /// </summary>
    [Theory]
    [InlineData("α", "grek")]
    [InlineData("א", "hebr")]
    [InlineData("漢", "hani")]
    [InlineData("か", "kana")]
    [InlineData("カ", "kana")]
    [InlineData("한", "hang")]
    [InlineData("ก", "thai")]
    [InlineData("क", "dev2")]
    [InlineData("ກ", "lao ")]
    public void E4b_ScriptFromTheText_ItsOwnTagApplies(string text, string tag)
    {
        var glyph = Ot03Font.GlyphForUnicode(char.ConvertToUtf32(text, 0));
        var gsub = E4bGsub(("latn", glyph, Ot03G('X')), (tag, glyph, Ot03G('Y')));
        Assert.Equal(Ot03Glyphs("Y"), Ot03Shape(Ot03Shaper(gsub), text));
    }

    /// <summary>
    /// OT 61 sentence 4 and OT 2.4.1 sentence 10: for Devanagari the new tag „dev2“ is tried first, then „deva“; with neither, DFLT applies.
    /// </summary>
    [Fact]
    public void E4b_Devanagari_Dev2ThenDevaThenDflt()
    {
        var ka = Ot03Font.GlyphForUnicode('क');
        Assert.Equal(Ot03Glyphs("Y"), Ot03Shape(Ot03Shaper(E4bGsub(("deva", ka, Ot03G('Y')), ("latn", ka, Ot03G('X')))), "क"));
        Assert.Equal(Ot03Glyphs("Z"), Ot03Shape(Ot03Shaper(E4bGsub(("DFLT", ka, Ot03G('Z')), ("latn", ka, Ot03G('X')))), "क"));
    }

    /// <summary>
    /// OT 2.4.1 sentences 10–11: a text without a script of its own (digits and punctuation only) is shaped with DFLT, not with „latn“ — „1“ with
    /// 1 → X under DFLT and 1 → Y under „latn“ becomes X; inside Latin text the digit belongs to the Latin run and becomes Y.
    /// </summary>
    [Fact]
    public void E4b_NeutralTextOnly_DfltApplies_InLatinTextLatn()
    {
        var shaper = Ot03Shaper(E4bGsub(("DFLT", Ot03G('1'), Ot03G('X')), ("latn", Ot03G('1'), Ot03G('Y'))));
        Assert.Equal(Ot03Glyphs("X"), Ot03Shape(shaper, "1"));
        Assert.Equal(Ot03Glyphs("aY"), Ot03Shape(shaper, "a1"));
    }

    /// <summary>
    /// OT 2.4.1 sentences 12–15 and OT 21.3 sentence 10: a mixed text is split into runs of one script, each shaped with its own lookups — „aа“
    /// (Latin a, Cyrillic a) with a → X under „latn“ and а → Y under „cyrl“ gives X Y; „aب“ (Latin a, Arabic beh) with a → X under „latn“: the
    /// Latin a is not shaped under „arab“ with the Arabic letter. The neutral space between runs goes with the run before it.
    /// </summary>
    [Fact]
    public void E4b_MixedText_SplitIntoRunsOfOneScript()
    {
        int a = Ot03G('a'), cyrillicA = Ot03Font.GlyphForUnicode('а');
        var shaper = Ot03Shaper(E4bGsub(("cyrl", cyrillicA, Ot03G('Y')), ("latn", a, Ot03G('X'))));
        Assert.Equal(Ot03Glyphs("XY"), Ot03Shape(shaper, "aа"));
        Assert.Equal(Ot03Glyphs("X Y"), Ot03Shape(shaper, "a а"));
        var shaped = Ot03Shaper(E4bGsub(("arab", a, Ot03G('Z')), ("latn", a, Ot03G('X')))).Shape("aب");
        Assert.Equal(Ot03G('X'), shaped[0].Glyph);
        Assert.Equal([0, 1], shaped.Select(g => g.Cluster));
    }

    /// <summary>
    /// OT 61 sentences 8–11, 32–40, 67–86, 91–105, 107–117, 120–139, 140–159, 160–179, OT 66.11 sentences 11–18: a cursive script other than
    /// Arabic (joining types in ArabicShaping.txt) is shaped under its own tag, with its joining forms — two joining letters of each, with a GSUB
    /// replacing them by X under „arab“ and by Y under the script's tag: Y. Before, every such text ran under „arab“.
    /// </summary>
    [Theory]
    [InlineData("ܐܒ", "syrc")]
    [InlineData("ߊߋ", "nko ")]
    [InlineData("ᠠᠡ", "mong")]
    [InlineData("ꡀꡁ", "phag")]
    [InlineData("ࡀࡁ", "mand")]
    [InlineData("𞤢𞤣", "adlm")]
    [InlineData("𐫀𐫁", "mani")]
    [InlineData("𐴀𐴁", "rohg")]
    [InlineData("𐼰𐼱", "sogd")]
    [InlineData("𐮀𐮁", "phlp")]
    [InlineData("𐾰𐾱", "chrs")]
    [InlineData("𐽰𐽱", "ougr")]
    public void E4b_CursiveScripts_TheirOwnTagNotArab(string text, string tag)
    {
        var first = char.ConvertToUtf32(text, 0);
        Assert.True(ArabicJoining.Joins(first), text);
        var glyph = Ot03Font.GlyphForUnicode(first);
        var shaped = Ot03Shape(Ot03Shaper(E4bGsub(("arab", glyph, Ot03G('X')), (tag, glyph, Ot03G('Y')))), text);
        Assert.Equal(Ot03G('Y'), shaped[0]);
        // without the script's tag DFLT applies (OpenType 2.4.1), not „arab“
        var fallback = Ot03Shape(Ot03Shaper(E4bGsub(("DFLT", glyph, Ot03G('Z')), ("arab", glyph, Ot03G('X')))), text);
        Assert.Equal(Ot03G('Z'), fallback[0]);
    }

    /// <summary>
    /// OT 66.4 sentences 7–11, OT 67.33 sentences 7–11, OT 68.8 sentences 7–11: Hangul written as conjoining jamo is shaped under „hang“, and
    /// the leading consonant takes ljmo, the vowel vjmo, the trailing consonant tjmo — a GSUB under „hang“ with the three features, each turning
    /// the jamo's glyph into its own: U+1100 U+1161 U+11A8 → X Y Z; a vowel without a leading consonant is no syllable and keeps its glyph.
    /// (Liberation Sans lacks the jamo: they are glyph 0, which each lookup covers.)
    /// </summary>
    [Fact]
    public void E4b_HangulJamo_LjmoVjmoTjmoInTheSyllable()
    {
        var gsub = Ot24Layout([Ot24Single((0, Ot03G('X'))), Ot24Single((0, Ot03G('Y'))), Ot24Single((0, Ot03G('Z')))],
            [("ljmo", [0]), ("vjmo", [1]), ("tjmo", [2])], [("hang", [0, 1, 2])]);
        var shaper = Ot03Shaper(gsub);
        Assert.Equal(Ot03Glyphs("XYZ"), Ot03Shape(shaper, "각"));
        Assert.Equal(Ot03Glyphs("XY"), Ot03Shape(shaper, "가"));
        Assert.Equal([0], Ot03Shape(shaper, "ᅡ"));
    }

    /// <summary>
    /// UNI 9.1.1 sentences 20–36 and OT 61 sentences 67–86: Hebrew typed in the bundled Liberation Sans (unchanged) is shaped under „hebr“,
    /// whose mark lookups (among them type 8) place the points as the bundled shaping library places them for this font; under „latn“ (before)
    /// they did not.
    /// </summary>
    [Fact]
    public void E4b_Hebrew_UnderHebr_MarksAsTheShapingLibrary()
    {
        var shaper = TextShaper.For(Ot20Lib);
        static (int Glyph, double X, double Y)[] Marks(ShapedGlyph[] g) => [.. g.Skip(1).Select(m => (m.Glyph, g[0].XAdvance + m.XOffset, m.YOffset)).OrderBy(m => m.Glyph)];
        foreach (var text in new[] { "ב\u05B8\u0591", "ד\u05B0\u059A" })
        {
            var own = shaper.Shape(text);
            var hb = ShaperTests.HarfBuzz(ShaperLimitTests.Liberation, Ot20Lib.UnitsPerEm, text, []);
            Assert.Equal(own.Length, hb.Glyphs.Length);
            Assert.Equal(own[0].Glyph, hb.Glyphs[^1]);
            var expected = hb.Glyphs[..^1].Select((g, i) => (g, (double)hb.XOffsets[i], (double)hb.YOffsets[i])).OrderBy(m => m.g).ToArray();
            Assert.Equal(expected, Marks(own));
        }
    }

    /// <summary>
    /// OT 31.2.30 sentences 1–3, 6–7, ISO 32000-2 9.8.1 (/CapHeight): one rule for the cap height — sCapHeight of OS/2 version 2 or later when above 0,
    /// else the top of „H“: an OS/2 with sCapHeight 0 and one of version 1 both give the height of „H“ (1409 of 2048 in Liberation Sans → 688),
    /// as the standard fonts are described.
    /// </summary>
    [Fact]
    public void E4b_CapHeight_OneRule_HeightOfHWithoutSCapHeight()
    {
        var top = Ot20Lib.Outline(Ot20Lib.GlyphForUnicode('H')).Bounds.Bottom;   // the largest y
        var expected = Math.Round(top * 1000 / Ot20Lib.UnitsPerEm);
        Assert.Equal(expected, Ot30SavedDescriptor(Ot30WithOs2(o => Ot30Set(o, 88, 0))).GetNumber(PdfName.Get("CapHeight")));
        Assert.Equal(expected, Ot30SavedDescriptor(Ot30WithOs2(null, 1)).GetNumber(PdfName.Get("CapHeight")));
    }

    // ------------------------------------------------------------ kern table (question 16)

    /// <summary>OT 27 sentences 5–9: a pair of the kern table is two glyphs in a row with marks between them passed over — „A“ + acute + „V“ is kerned as „AV“
    /// (the cmap without the composed letters, so that A and the acute stay apart, No. 1197).</summary>
    [Fact]
    public void E4b_KernTable_MarksBetweenThePairArePassedOver()
    {
        var (a, v) = (Ot27Glyph('A'), Ot27Glyph('V'));
        // without GDEF: the acute is a mark by its Unicode category (the GDEF of Liberation Sans leaves it unclassified)
        var shaper = TextShaper.For(OpenTypeFont.Parse(Ot27SansWith(("kern", Ot28Kern((0x0001, Ot28KernPair(a, v, -200)))), ("GPOS", null), ("GSUB", null),
            ("GDEF", null), ("cmap", OpenTypeFont.Parse(LiberationWithoutComposites).TableData("cmap").ToArray()))));
        var kerned = shaper.Shape("A\u0301V");
        var plain = shaper.Shape("A\u0301V", ["-kern"]);
        Assert.Equal(plain[0].XAdvance - 200, kerned[0].XAdvance);
        Assert.Equal(plain[1].XAdvance, kerned[1].XAdvance);
    }
}
