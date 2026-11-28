using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;
using JoinForm = PdfKnead.Core.Fonts.Layout.ArabicJoining.Form;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package E19 (docs/FEHLERGEDAECHTNIS.md, No. 1183): the vowel sequences not to use marked with a dotted circle, default ignorable characters
/// drawn as nothing and passed over by ligatures and positioning, ZWJ asking for a ligature, canonically equivalent spellings in Tibetan, Myanmar
/// and Thai drawn alike, digits and further syllable modifiers in the Indic syllables, Script_Extensions, fractions with U+2044. The fonts are
/// Liberation Sans with a cmap built for the block of the script (glyphs from 1100 on, the dotted circle 1050, the joiners 1060/1061) and GSUB or
/// GPOS built by hand; forms made by lookups are glyphs from 1500 on. Where it applies the bundled shaping library shapes the same font.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (E19)

    private const int E19Base = 1100, E19Dotted = 1050, E19Zwnj = 1060, E19Form = 1500;

    /// <summary>The glyph of a character of the block starting at <paramref name="block"/>.</summary>
    private static int E19G(int block, int cp) => E19Base + cp - block;

    /// <summary>A form made by a lookup.</summary>
    private static int E19F(int n) => E19Form + n;

    /// <summary>Liberation Sans with a cmap of the groups given (and the space, the dotted circle, ZWNJ and ZWJ) and the tables given.</summary>
    private static byte[] E19Font((int First, int Last, int Glyph)[] groups, byte[]? gsub, byte[]? gpos = null, bool dotted = true) =>
        ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
        {
            ["cmap"] = E11Cmap([(' ', ' ', Ot02G(' ')), (0x200C, 0x200D, E19Zwnj), .. dotted ? new[] { (0x25CC, 0x25CC, E19Dotted) } : [], .. groups]),
            ["GSUB"] = gsub, ["GPOS"] = gpos, ["GDEF"] = null, ["kern"] = null,
        });

    /// <summary>A font for the 128 characters of an Indic block (and the European digits) with a GSUB under the script's tag.</summary>
    private static byte[] E19Indic(int block, params (string Feature, Action<Ot02Bytes> Lookup)[] features) =>
        E19Font([(block, block + 0x7F, E19Base), ('0', '9', Ot02G('0'))],
            E11Layout(E19Tag(block), features.Length > 0 ? features : [("nukt", E11Sub((E19G(block, block + 5), E19G(block, block + 5))))]));

    /// <summary>The OpenType tag of the second shaping model of an Indic block (Sinhala: sinh).</summary>
    private static string E19Tag(int block) => block switch
    {
        0x0980 => "bng2", 0x0A00 => "gur2", 0x0A80 => "gjr2", 0x0B00 => "ory2", 0x0B80 => "tml2", 0x0C00 => "tel2", 0x0C80 => "knd2",
        0x0D00 => "mlm2", 0x0D80 => "sinh", _ => "dev2",
    };

    private static ShapedGlyph[] E19Shape(byte[] font, string text, params string[] features) =>
        TextShaper.For(OpenTypeFont.Parse(font)).Shape(text, features.Length == 0 ? null : features);

    /// <summary>The glyphs drawn (joiners and glyphs drawn as nothing left out).</summary>
    private static int[] E19Drawn(byte[] font, string text, params string[] features) =>
        [.. E19Shape(font, text, features).Where(g => g.Glyph is not (E19Zwnj or E19Zwnj + 1) && !(g.Glyph == Ot02G(' ') && g.XAdvance == 0)).Select(g => g.Glyph)];

    /// <summary>The same from the shaping library (it draws the joiners and default ignorable characters as the space glyph without advance).</summary>
    private static int[] E19Library(byte[] font, string text)
    {
        var hb = ShaperTests.HarfBuzz(font, 2048, text, []);
        return [.. hb.Glyphs.Where((g, i) => g is not (E19Zwnj or E19Zwnj + 1) && !(g == Ot02G(' ') && hb.Advances[i] == 0))];
    }

    private static string E19Text(ShapedGlyph[] shaped) => string.Concat(shaped.Select(g => g.Text));

    private static string E19S(params int[] cps) => string.Concat(cps.Select(char.ConvertFromUtf32));

    // ------------------------------------------------------------ 1. vowel sequences not to use

    /// <summary>The rows of the tables of the vowel letters that can be analyzed (chapters 12 and 13): the letter to use, then the sequence not to use.</summary>
    private static readonly int[][] E19Discouraged =
    [
        [0x0904, 0x0905, 0x0946],
        [0x0906, 0x0905, 0x093E],
        [0x0908, 0x0930, 0x094D, 0x0907],
        [0x090A, 0x0909, 0x0941],
        [0x090D, 0x090F, 0x0945],
        [0x090E, 0x090F, 0x0946],
        [0x0910, 0x090F, 0x0947],
        [0x0911, 0x0905, 0x0949],
        [0x0911, 0x0906, 0x0945],
        [0x0912, 0x0905, 0x094A],
        [0x0912, 0x0906, 0x0946],
        [0x0913, 0x0905, 0x094B],
        [0x0913, 0x0906, 0x0947],
        [0x0914, 0x0905, 0x094C],
        [0x0914, 0x0906, 0x0948],
        [0x0972, 0x0905, 0x0945],
        [0x0973, 0x0905, 0x093A],
        [0x0974, 0x0905, 0x093B],
        [0x0974, 0x0906, 0x093A],
        [0x0975, 0x0905, 0x094F],
        [0x0976, 0x0905, 0x0956],
        [0x0977, 0x0905, 0x0957],
        [0x0986, 0x0985, 0x09BE],
        [0x09E0, 0x098B, 0x09C3],
        [0x09E1, 0x098C, 0x09E2],
        [0x0A06, 0x0A05, 0x0A3E],
        [0x0A07, 0x0A72, 0x0A3F],
        [0x0A08, 0x0A72, 0x0A40],
        [0x0A09, 0x0A73, 0x0A41],
        [0x0A0A, 0x0A73, 0x0A42],
        [0x0A0F, 0x0A72, 0x0A47],
        [0x0A10, 0x0A05, 0x0A48],
        [0x0A13, 0x0A73, 0x0A4B],
        [0x0A14, 0x0A05, 0x0A4C],
        [0x0A86, 0x0A85, 0x0ABE],
        [0x0A8D, 0x0A85, 0x0AC5],
        [0x0A8F, 0x0A85, 0x0AC7],
        [0x0A90, 0x0A85, 0x0AC8],
        [0x0A91, 0x0A85, 0x0AC9],
        [0x0A93, 0x0A85, 0x0ACB],
        [0x0A93, 0x0A85, 0x0ABE, 0x0AC5],
        [0x0A94, 0x0A85, 0x0ACC],
        [0x0A94, 0x0A85, 0x0ABE, 0x0AC8],
        [0x0AC9, 0x0AC5, 0x0ABE],
        [0x0B06, 0x0B05, 0x0B3E],
        [0x0B10, 0x0B0F, 0x0B57],
        [0x0B14, 0x0B13, 0x0B57],
        [0x0B86, 0x0B85, 0x0BC2],
        [0x0C13, 0x0C12, 0x0C55],
        [0x0C14, 0x0C12, 0x0C4C],
        [0x0C40, 0x0C3F, 0x0C55],
        [0x0C47, 0x0C46, 0x0C55],
        [0x0C4B, 0x0C4A, 0x0C55],
        [0x0C8A, 0x0C89, 0x0CBE],
        [0x0C94, 0x0C92, 0x0CCC],
        [0x0CE0, 0x0C8B, 0x0CBE],
        [0x0D08, 0x0D07, 0x0D57],
        [0x0D0A, 0x0D09, 0x0D57],
        [0x0D10, 0x0D0E, 0x0D46],
        [0x0D13, 0x0D12, 0x0D3E],
        [0x0D14, 0x0D12, 0x0D57],
        [0x0D86, 0x0D85, 0x0DCF],
        [0x0D87, 0x0D85, 0x0DD0],
        [0x0D88, 0x0D85, 0x0DD1],
        [0x0D8C, 0x0D8B, 0x0DDF],
        [0x0D8E, 0x0D8D, 0x0DD8],
        [0x0D90, 0x0D8F, 0x0DDF],
        [0x0D92, 0x0D91, 0x0DCA],
        [0x0D93, 0x0D91, 0x0DD9],
        [0x0D96, 0x0D94, 0x0DDF],
    ];

    public static IEnumerable<object[]> E19DiscouragedRows() => Enumerable.Range(0, E19Discouraged.Length).Select(i => new object[] { i });

    /// <summary>
    /// UNI 12.1.2 sentences 38–56, 12.2.1 sentences 11–17, 12.3.1 sentences 38–50, 12.4.1 sentences 4–15, 12.5.1 sentences 7–13, 12.6.2
    /// sentences 1–5, 12.7.1 sentences 4–12, 12.8.2 sentences 10–16, 12.9.1 sentences 6–14 and 13.2.1 sentences 6–18 (the tables of the vowel
    /// letters that can be analyzed): each sequence the tables say not to use is shaped like a broken syllable — one dotted circle (without a text
    /// of its own) before the part that should not follow, the text read back as typed, the same glyphs as the bundled shaping library gives —
    /// and the vowel letter to use instead gets no circle. Before, these sequences were drawn without any mark.
    /// </summary>
    [Theory]
    [MemberData(nameof(E19DiscouragedRows))]
    public void E19_DiscouragedVowelSequences_DottedCircleBeforeTheSecondPart(int row)
    {
        var (instead, cps) = (E19Discouraged[row][0], E19Discouraged[row][1..]);
        var block = cps[0] & ~0x7F;
        var font = E19Indic(block);
        // a sequence of two vowel signs follows a consonant (Ka of the block)
        var prefix = SyllableData.SyllabicOf(cps[0]) == SyllabicCategory.VowelDependent ? char.ConvertFromUtf32(block + 0x15) : "";
        var text = E19S(cps);
        var shaped = E19Shape(font, prefix + text);
        text = prefix + text;
        Assert.Single(shaped, g => g.Glyph == E19Dotted);
        Assert.Equal(text, E19Text(shaped));
        Assert.Equal(E19Library(font, text), E19Drawn(font, text));
        Assert.DoesNotContain(E19Shape(font, prefix + E19S(instead)), g => g.Glyph == E19Dotted);
    }

    /// <summary>
    /// UNI 12.2.1 sentences 18–25 (the Kokborok vowel letters written as two characters) and 12.3.1 sentences 33–37 (a bindi before the Gurmukhi
    /// vowel sign II): allowed sequences — no dotted circle, one syllable, the bindi before II in the order typed, the same glyphs as the shaping
    /// library; a sequence of the tables in a font without the dotted circle stays unmarked. Before, the Gurmukhi bindi before II gave II a circle.
    /// </summary>
    [Fact]
    public void E19_AllowedSequences_NoDottedCircle()
    {
        foreach (var (block, cps) in new[] { (0x0980, new[] { 0x0985, 0x09D7 }), (0x0980, new[] { 0x0989, 0x09BE }), (0x0A00, new[] { 0x0A72, 0x0A02, 0x0A40 }),
                     (0x0A00, new[] { 0x0A15, 0x0A02, 0x0A40 }) })
        {
            var font = E19Indic(block);
            var text = E19S(cps);
            Assert.Equal([.. cps.Select(c => E19G(block, c))], E19Drawn(font, text));
            Assert.Equal(E19Library(font, text), E19Drawn(font, text));
        }
        var noCircle = E19Font([(0x0900, 0x097F, E19Base)], E11Layout("dev2", ("nukt", E11Sub((E19G(0x0900, 0x0905), E19G(0x0900, 0x0905))))), dotted: false);
        Assert.Equal([E19G(0x0900, 0x0905), E19G(0x0900, 0x093E)], E19Drawn(noCircle, "अ\u093E"));
    }

    /// <summary>
    /// UNI 12.9.3 sentences 53–57: digits (European and of the script) carry vowel signs and the other signs as bases — „355U+0D3EU+0D02“ without a dotted
    /// circle, the Malayalam digit five with AA as well; a Devanagari vowel sign I after a digit stands before it, as before a consonant (the same
    /// glyphs as the shaping library). Before, a digit had no class and the sign after it a dotted circle.
    /// </summary>
    [Fact]
    public void E19_Digits_CarryVowelSigns()
    {
        var malayalam = E19Indic(0x0D00);
        var text = "355\u0D3E\u0D02";
        Assert.Equal([Ot02G('3'), Ot02G('5'), Ot02G('5'), E19G(0x0D00, 0x0D3E), E19G(0x0D00, 0x0D02)], E19Drawn(malayalam, text));
        Assert.Equal(E19Library(malayalam, text), E19Drawn(malayalam, text));
        Assert.Equal([E19G(0x0D00, 0x0D6B), E19G(0x0D00, 0x0D3E)], E19Drawn(malayalam, "൫\u0D3E"));
        var devanagari = E19Indic(0x0900);
        Assert.Equal([E19G(0x0900, 0x093F), Ot02G('5')], E19Drawn(devanagari, "5\u093F"));
        Assert.Equal(E19Library(devanagari, "5\u093F"), E19Drawn(devanagari, "5\u093F"));
    }

    /// <summary>
    /// UNI 12.9.3 sentences 54 and 57: in Malayalam the anusvara may follow a vowel several times — „ഈU+0D02U+0D02U+0D02U+0D02“ is one syllable without a dotted circle
    /// (the shaping library puts one before the third anusvara: here PDF-Knead follows the standard); in Devanagari a third anusvara after Ka
    /// still gets the circle, as before and as the shaping library.
    /// </summary>
    [Fact]
    public void E19_Malayalam_SeveralAnusvaras_OneSyllable()
    {
        var malayalam = E19Indic(0x0D00);
        var shaped = E19Shape(malayalam, "ഈ\u0D02\u0D02\u0D02\u0D02");
        Assert.DoesNotContain(shaped, g => g.Glyph == E19Dotted);
        Assert.Single(shaped.Select(g => g.Syllable).Distinct());
        var devanagari = E19Indic(0x0900);
        Assert.Contains(E19Shape(devanagari, "क\u0902\u0902\u0902"), g => g.Glyph == E19Dotted);
        Assert.Equal(E19Library(devanagari, "क\u0902\u0902\u0902"), E19Drawn(devanagari, "क\u0902\u0902\u0902"));
    }

    /// <summary>
    /// The sequence Ra, virama, I of the Devanagari table: the dotted circle goes before the vowel letter I, and Ra with virama makes a reph on
    /// the circle where the font has rphf — circle, reph, I, as the shaping library; Ra and virama typed before a dotted circle likewise. Before,
    /// the sequence was drawn as Ra, virama, I and the typed circle took no reph.
    /// </summary>
    [Fact]
    public void E19_RaViramaBeforeDottedCircle_RephOnTheCircle()
    {
        var font = E19Indic(0x0900, ("rphf", E12Lig(([E19G(0x0900, 0x0930), E19G(0x0900, 0x094D)], E19F(1)))));
        Assert.Equal([E19Dotted, E19F(1), E19G(0x0900, 0x0907)], E19Drawn(font, "र\u094Dइ"));
        Assert.Equal([E19Dotted, E19F(1)], E19Drawn(font, "र\u094D◌"));
        Assert.Equal(E19Library(font, "र\u094Dइ"), E19Drawn(font, "र\u094Dइ"));
        Assert.Equal(E19Library(font, "र\u094D◌"), E19Drawn(font, "र\u094D◌"));
        Assert.Equal("र\u094Dइ", E19Text(E19Shape(font, "र\u094Dइ")));
    }

    /// <summary>
    /// UNI 12.5.1 sentences 48–54: the Oriya overline U+0B55 after the vowel sign AA stands over AA (glyph order Ka, AA, overline), after the
    /// vowel sign E it moves with E before the consonant (E, overline, Ka); right after the consonant it stays there — the same glyphs as the
    /// shaping library (with Nirmala UI as well). Before, the overline was sorted before AA and hung on the consonant.
    /// </summary>
    [Fact]
    public void E19_Oriya_OverlineStaysWithItsVowelSign()
    {
        var font = E19Indic(0x0B00);
        int ka = E19G(0x0B00, 0x0B15), aa = E19G(0x0B00, 0x0B3E), e = E19G(0x0B00, 0x0B47), line = E19G(0x0B00, 0x0B55);
        Assert.Equal([ka, aa, line], E19Drawn(font, "କ\u0B3E\u0B55"));
        Assert.Equal([e, line, ka], E19Drawn(font, "କ\u0B47\u0B55"));
        Assert.Equal([ka, line], E19Drawn(font, "କ\u0B55"));
        foreach (var text in new[] { "କ\u0B3E\u0B55", "କ\u0B47\u0B55", "କ\u0B55" }) Assert.Equal(E19Library(font, text), E19Drawn(font, text));
    }

    /// <summary>
    /// UNI 12.1.3 sentences 108–109 (R16): Ta, virama, ZWNJ, Ra, I in a font whose blwf forms the Rakar — the ZWNJ ends the syllable after the
    /// virama, so the vowel sign I goes before Ra, not before Ta: Ta, virama, ZWNJ, I, Ra, as the shaping library (and Nirmala UI) give it; the
    /// reading of the code in the draft (I before Ta) does not occur — the syllables are cut before the base is searched.
    /// </summary>
    [Fact]
    public void Uni08_ZwnjBlocksPreBaseMatra_EvenWithRakarInTheFont()
    {
        const int d = 0x0900;
        var font = E19Indic(d, ("blwf", E12Lig(([E19G(d, 0x094D), E19G(d, 0x0930)], E19F(2)))));
        var text = "त\u094D\u200Cर\u093F";
        Assert.Equal([E19G(d, 0x0924), E19G(d, 0x094D), E19G(d, 0x093F), E19G(d, 0x0930)], E19Drawn(font, text));
        Assert.Equal(E19Library(font, text), E19Drawn(font, text));
        // without the ZWNJ the Rakar joins Ta and the vowel sign goes before the conjunct
        Assert.Equal([E19G(d, 0x093F), E19G(d, 0x0924), E19F(2)], E19Drawn(font, "त\u094Dर\u093F"));
        Assert.Equal(E19Library(font, "त\u094Dर\u093F"), E19Drawn(font, "त\u094Dर\u093F"));
    }

    // ------------------------------------------------------------ 2. Thai consonant diacritics

    /// <summary>
    /// UNI 16.1.1 sentences 88–99 (must): Thai SARA U and SARA UU stand below a consonant diacritic such as U+0331 COMBINING MACRON BELOW, the
    /// tone marks above U+0303 COMBINING TILDE — in normalized text (consonant, Thai sign, combining mark) the combining mark is drawn inside:
    /// glyph order Ko, macron, SARA U and Ko, tilde, MAI EK, so that the font stacks the Thai sign outside it; the order typed inside first gives
    /// the same glyphs; the consonant and its marks are one cluster with the text as typed. Lao and other Thai marks are not reordered. Before, the
    /// macron stood under SARA U and the tilde over the tone mark (the shaping library keeps that order: here PDF-Knead follows the standard).
    /// </summary>
    [Fact]
    public void E19_Thai_ConsonantDiacriticsInsideSaraUAndToneMarks()
    {
        var font = E19Font([(0x0E00, 0x0EFF, E19Base), (0x0300, 0x036F, E19Base + 0x100)], null);
        int ko = E19G(0x0E00, 0x0E01), u = E19G(0x0E00, 0x0E38), ek = E19G(0x0E00, 0x0E48), macron = E19Base + 0x100 + 0x31, tilde = E19Base + 0x100 + 0x03;
        foreach (var text in new[] { "ก\u0E38\u0331", "ก\u0331\u0E38" })
        {
            var shaped = E19Shape(font, text);
            Assert.Equal([ko, macron, u], shaped.Select(g => g.Glyph));
            Assert.Equal(text, E19Text(shaped));
        }
        foreach (var text in new[] { "ก\u0E48\u0303", "ก\u0303\u0E48" }) Assert.Equal([ko, tilde, ek], E19Shape(font, text).Select(g => g.Glyph));
        Assert.Equal([ko, ek, E19G(0x0E00, 0x0E4D)], E19Shape(font, "ก\u0E48\u0E4D").Select(g => g.Glyph));
        Assert.Equal([E19G(0x0E00, 0x0E81), E19G(0x0E00, 0x0EB8), macron], E19Shape(font, "ກ\u0EB8\u0331").Select(g => g.Glyph));
    }

    // ------------------------------------------------------------ 3. canonical equivalence in Tibetan and Myanmar

    /// <summary>
    /// UNI 13.4.1 sentences 72–76 and 220–221: the Tibetan compound vowel U+0F73 and the aspirated letter U+0F43 are drawn like their canonical
    /// parts — „ཀ\u0F71\u0F72“ as U+0F40 U+0F73, as U+0F40 U+0F71 U+0F72 and as U+0F40 U+0F72 U+0F71 gives the same glyphs, U+0F43 the same as U+0F42 U+0FB7;
    /// the vowel signs below (A-chung, U) stand next to the stack before those above (U+0F40 U+0F72 U+0F74 like U+0F40 U+0F74 U+0F72) — each the
    /// same as the shaping library; the text read back is canonically equivalent to the one typed. Before, each spelling had its own glyphs.
    /// </summary>
    [Fact]
    public void E19_Tibetan_CompoundVowelsAndAspirates_LikeTheirParts()
    {
        var font = E19Font([(0x0F00, 0x0FFF, E19Base)], E11Layout("tibt", ("ccmp", E11Sub((E19G(0x0F00, 0x0F40), E19G(0x0F00, 0x0F40))))));
        int G(int c) => E19G(0x0F00, c);
        foreach (var text in new[] { "ཀ\u0F73", "ཀ\u0F71\u0F72", "ཀ\u0F72\u0F71" })
        {
            Assert.Equal([G(0x0F40), G(0x0F71), G(0x0F72)], E19Drawn(font, text));
            Assert.Equal(E19Library(font, text), E19Drawn(font, text));
            Assert.Equal(text.Normalize(System.Text.NormalizationForm.FormD), E19Text(E19Shape(font, text)).Normalize(System.Text.NormalizationForm.FormD));
        }
        foreach (var text in new[] { "ཀ\u0F72\u0F74", "ཀ\u0F74\u0F72" })
        {
            Assert.Equal([G(0x0F40), G(0x0F74), G(0x0F72)], E19Drawn(font, text));
            Assert.Equal(E19Library(font, text), E19Drawn(font, text));
        }
        foreach (var text in new[] { "གྷ", "ག\u0FB7" })
        {
            Assert.Equal([G(0x0F42), G(0x0FB7)], E19Drawn(font, text));
            Assert.Equal(E19Library(font, text), E19Drawn(font, text));
            Assert.Equal(text, E19Text(E19Shape(font, text)));
        }
    }

    /// <summary>
    /// UNI 16.3 (canonical equivalence, The Unicode Standard 3.11): the Myanmar vowel letter UU U+1026 and its canonical decomposition U+1025
    /// U+102E look the same — both the glyph of U+1026 where the font has it (the normalized form), the text as typed; the shaping library gives
    /// the pair the composed glyph as well. Before, the pair stayed two glyphs.
    /// </summary>
    [Fact]
    public void E19_Myanmar_VowelLetterUu_LikeItsDecomposition()
    {
        var font = E19Font([(0x1000, 0x109F, E19Base)], E11Layout("mym2", ("ccmp", E11Sub((E19G(0x1000, 0x1000), E19G(0x1000, 0x1000))))));
        foreach (var text in new[] { "ဦ", "ဥ\u102E" })
        {
            var shaped = E19Shape(font, text);
            Assert.Equal([E19G(0x1000, 0x1026)], shaped.Select(g => g.Glyph));
            Assert.Equal(text, E19Text(shaped));
        }
        Assert.Equal(E19Library(font, "ဥ\u102E"), E19Drawn(font, "ဥ\u102E"));
    }

    // ------------------------------------------------------------ 4. default ignorable characters

    /// <summary>
    /// UNI 23.2.1 sentences 10, 19–22, 23.2.4 sentences 10, 40–43, 23.2.6 sentences 14–28, 23.9.1 sentences 1–2, 16.4.2 sentences 205–209,
    /// 18.6.1 sentences 9–10 and 13.5.1 sentences 172–174: a default ignorable character the font has no glyph for (WJ, ZWSP, U+FEFF, CGJ, the
    /// Khmer inherent vowels, MVS, a musical beam control, a tag, U+206D, a Hangul filler) is drawn as nothing — the glyph of the space without
    /// an advance — and keeps its text; the soft hyphen always (it shows only at the end of a line); a character the font has a glyph for keeps
    /// it; the annotation characters U+FFF9–U+FFFB are not default ignorable and stay visible. Before, each was drawn as the box of glyph 0.
    /// </summary>
    [Fact]
    public void E19_DefaultIgnorable_WithoutGlyph_DrawnAsNothing()
    {
        var font = E19Font([('a', 'b', Ot02G('a'))], null);
        var parsed = OpenTypeFont.Parse(font);
        foreach (var c in new[] { 0x2060, 0x200B, 0xFEFF, 0x034F, 0x17B4, 0x180E, 0x1D173, 0xE0067, 0x206D, 0x3164, 0x00AD })
        {
            var text = "a" + char.ConvertFromUtf32(c) + "b";
            var shaped = E19Shape(font, text);
            Assert.Equal(3, shaped.Length);
            Assert.True(parsed.GlyphForUnicode(c) <= 0 || c == 0x00AD, $"U+{c:X4}");
            Assert.Equal(Ot02G(' '), shaped[1].Glyph);
            Assert.Equal(0, shaped[1].XAdvance);
            Assert.Equal(text, E19Text(shaped));
        }
        var annotation = E19Shape(font, "a\uFFF9b");
        Assert.Equal(0, annotation[1].Glyph);
        Assert.True(annotation[1].XAdvance > 0);
        var own = E19Font([('a', 'b', Ot02G('a')), (0x2060, 0x2060, Ot02G('x'))], null);
        Assert.Equal(Ot02G('x'), E19Shape(own, "a\u2060b")[1].Glyph);
    }

    /// <summary>
    /// UNI 23.2.1 sentences 14–15 and 23.2.4 sentences 44–48 (must): WJ, CGJ and the other format characters do not break a ligature or a pair of
    /// kerning — „f\u2060i“ and „f\u034Fi“ give the fi ligature (with the text of all three characters, the format character drawn as nothing), a kerning
    /// pair applies across WJ; the joining of Arabic letters passes over them as well (beh WJ beh: initial, final). Before, they broke both.
    /// </summary>
    [Fact]
    public void E19_FormatCharacters_DoNotBreakLigaturesOrKerning()
    {
        int f = Ot02G('f'), i = Ot02G('i'), a = Ot02G('A'), v = Ot02G('V');
        var font = Ot49Font("fiAV", Ot02Latin([new Ot02Feature("liga", [0])], Ot02Lookup(4, Ot02Ligature(([f, i], Ot49Form)))),
            Ot02Latin([new Ot02Feature("kern", [0])], Ot02Lookup(2, Ot02Pairs(0, (a, v, -200)))));
        foreach (var text in new[] { "f\u2060i", "f\u034Fi", "f\uFEFFi" })
        {
            var shaped = E19Shape(font, text);
            Assert.Equal(Ot49Form, shaped[0].Glyph);
            Assert.Equal(text, E19Text(shaped));
            Assert.All(shaped.Skip(1), g => Assert.Equal(0, g.XAdvance));
        }
        var parsed = OpenTypeFont.Parse(font);
        Assert.Equal(parsed.AdvanceWidth(a) - 200, E19Shape(font, "A\u2060V")[0].XAdvance);
        Assert.Equal([JoinForm.Initial, JoinForm.None, JoinForm.Final], ArabicJoining.Forms([0x0628, 0x2060, 0x0628]));
        Assert.Equal([JoinForm.Initial, JoinForm.None, JoinForm.Final], ArabicJoining.Forms([0x0628, 0x034F, 0x0628]));
    }

    /// <summary>
    /// UNI 23.2.2 sentences 26, 38–41, 57–58 and 71–75: a ZWJ between two characters asks for their ligature — „f\u200Di“ with liga gives the
    /// ligature, „f\u200Ci“ does not; between Arabic letters the ZWJ keeps them joined without the ligature (lam ZWJ alef in Noto Sans Arabic: no
    /// lam-alef); in the stages of the Indic syllables the joiners keep their meaning (Ka, virama, ZWJ, Ssa: the half form, not the akhand
    /// ligature) — the same glyphs as the shaping library. Before, the ZWJ broke every ligature.
    /// </summary>
    [Fact]
    public void E19_Zwj_AsksForTheLigature_ExceptInJoiningAndSyllabicStages()
    {
        int f = Ot02G('f'), i = Ot02G('i');
        var font = Ot49Font("fi", Ot02Latin([new Ot02Feature("liga", [0])], Ot02Lookup(4, Ot02Ligature(([f, i], Ot49Form)))));
        Assert.Equal([Ot49Form], E19Drawn(font, "f\u200Di"));
        Assert.Equal("f\u200Di", E19Text(E19Shape(font, "f\u200Di")));
        Assert.Contains(f, E19Shape(font, "f\u200Ci").Select(g => g.Glyph));
        var arabic = TextShaper.For(Uni04NotoArabic);
        var lamAlef = arabic.Shape("لا").Select(g => g.Glyph).ToArray();
        var joined = arabic.Shape("ل\u200Dا").Where(g => g.Text != "\u200D").Select(g => g.Glyph).ToArray();
        Assert.NotEqual(lamAlef, joined);
        Assert.Equal(arabic.Shape("لـ").First().Glyph, joined[0]);
        const int d = 0x0900;
        var indic = E19Indic(d, ("akhn", E12Lig(([E19G(d, 0x0915), E19G(d, 0x094D), E19G(d, 0x0937)], E19F(3)))),
            ("half", E12Lig(([E19G(d, 0x0915), E19G(d, 0x094D)], E19F(0)))));
        Assert.Equal([E19F(0), E19G(d, 0x0937)], E19Drawn(indic, "क\u094D\u200Dष"));
        Assert.Equal(E19Library(indic, "क\u094D\u200Dष"), E19Drawn(indic, "क\u094D\u200Dष"));
        Assert.Equal([E19F(3)], E19Drawn(indic, "क\u094Dष"));
    }

    /// <summary>
    /// UNI 23.2.3 sentences 5–7: within the unit of a prepended concatenation mark a default ignorable character breaks the unit — a ligature of
    /// the end of ayah with two digits forms for „\u06DD١٢“, with WJ between the digits it does not (the WJ is not passed over there).
    /// </summary>
    [Fact]
    public void E19_PrependedConcatenationMark_WordJoinerBreaksTheUnit()
    {
        var font = E19Font([(0x0600, 0x06FF, E19Base)], E11Layout("arab", ("rlig", E12Lig(([E19G(0x0600, 0x06DD), E19G(0x0600, 0x0661), E19G(0x0600, 0x0662)], E19F(4))))));
        Assert.Equal([E19F(4)], E19Drawn(font, "\u06DD١٢"));
        Assert.DoesNotContain(E19F(4), E19Drawn(font, "\u06DD١\u2060٢"));
    }

    /// <summary>
    /// UNI 23.4 sentences 26–29: a variation selector does not keep the font's ligature from forming — the keycap „#“ U+FE0F U+20E3 in a font
    /// with a format 14 sequence for „#“ U+FE0F gives the keycap glyph, whether the ligature names the selector or not; a selector after a
    /// character without a ligature is drawn as nothing. Before, the selector went into the text of „#“ and a ligature naming it could not form.
    /// </summary>
    [Fact]
    public void E19_Keycap_VariationSelectorDoesNotBreakTheLigature()
    {
        const int hash = 600, vs = 601, keycap = 602, smile = 603;
        var format12 = E11Cmap(('#', '#', hash), (0xFE0F, 0xFE0F, vs), (0x20E3, 0x20E3, keycap), (0x263A, 0x263A, smile), (' ', ' ', Ot02G(' ')))[12..];
        var format14 = BeOt11((14, 2), (29, 4), (1, 4), (0xFE0F, 3), (21, 4), (0, 4), (1, 4), ('#', 3), (0, 1));
        var cmap = CmapOt11((0, 5, format14), (3, 10, format12));
        foreach (var components in new[] { new[] { hash, vs, keycap }, [hash, keycap] })
        {
            var font = ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
            {
                ["cmap"] = cmap, ["GSUB"] = Ot02Latin([new Ot02Feature("liga", [0])], Ot02Lookup(4, Ot02Ligature((components, Ot49Form)))),
                ["GPOS"] = null, ["GDEF"] = null, ["kern"] = null,
            });
            Assert.Equal([Ot49Form], E19Drawn(font, "#\uFE0F\u20E3"));
            Assert.Equal("#\uFE0F\u20E3", E19Text(E19Shape(font, "#\uFE0F\u20E3")));
            var lone = E19Shape(font, "☺\uFE0F");
            Assert.Equal(smile, lone[0].Glyph);
            Assert.All(lone.Skip(1), g => Assert.Equal(0, g.XAdvance));
        }
    }

    /// <summary>
    /// UNI 23.3.1 sentences 29–36 at the level of the shaper, and U+206A: U+206E before digits leaves the glyphs of the shaper as without it —
    /// the national digit shapes come from the line layout, which knows the script of the text around the digits (docs/FEHLERGEDAECHTNIS.md,
    /// No. 1221; U+206C/U+206D are evaluated by the shaper since then) —, the format character drawn as nothing; U+206A does not change the
    /// bidirectional levels (the brackets stay at an odd level; it inhibits only their mirrored glyphs, sentences 9–18:
    /// E27_SymmetricSwapping_InhibitedFromU206AUpToU206B, No. 1215).
    /// </summary>
    [Fact]
    public void E19_DeprecatedFormatCharacters_NotEvaluated()
    {
        var arabic = TextShaper.For(Uni04NotoArabic);
        int[] Drawn(string text) => [.. arabic.Shape(text).Where(g => g.XAdvance != 0 || g.Text.Length > 0 && g.Text[0] is not ('\u206D' or '\u206E')).Select(g => g.Glyph)];
        Assert.Equal(Drawn("12"), Drawn("\u206E12"));
        var levels = Bidi.Levels("א\u206A(ב)");
        Assert.Equal(1, levels[2] % 2);
    }

    // ------------------------------------------------------------ 5. Script_Extensions, fractions

    /// <summary>
    /// UNI 12.6.3 sentences 92–97: the Grantha nukta U+1133C, whose Script_Extensions name Tamil, continues a Tamil run — one run, and the nukta
    /// attaches to Pa by the font's mark-to-base anchor under tml2. Before, it started a Grantha run of its own and was not attached.
    /// </summary>
    [Fact]
    public void E19_ScriptExtensions_GranthaNuktaInTamilText()
    {
        Assert.Equal([(0, 2, "Taml")], UnicodeScripts.Itemize([0x0BAA, 0x1133C]));
        Assert.Equal([(0, 2, "Taml"), (2, 1, "Deva")], UnicodeScripts.Itemize([0x0BAA, 0x1133C, 0x0915]));
        int pa = E19G(0x0B80, 0x0BAA), nukta = E19Base + 0x100;
        var font = E19Font([(0x0B80, 0x0BFF, E19Base), (0x1133C, 0x1133C, nukta)], E11Layout("tml2", ("nukt", E11Sub((pa, pa)))),
            E11Layout("tml2", ("mark", E12MarkToBase(nukta, pa, (0, 0), (300, -100)))));
        var shaped = E19Shape(font, "ப\U0001133C");
        Assert.Equal([pa, nukta], shaped.Select(g => g.Glyph));
        Assert.Equal(-100, shaped[1].YOffset);
    }

    /// <summary>
    /// UNI 23.2.3 sentences 12–14 and OT 65.6 (the program may apply frac itself to digits around the fraction slash, with numr and dnom): „3⁄4“
    /// takes numr at the 3, frac at the slash and dnom at the 4, each from the font; „3/4“, „3⁄x“ and „3⁄\u20604“ (WJ breaks the digits) stay as
    /// they are; with numr switched off the numerator keeps its glyph — the same glyphs as the shaping library for „3⁄4“. Before, a fraction
    /// was formed only when the caller named frac.
    /// </summary>
    [Fact]
    public void E19_Fraction_NumrDnomFracAroundTheSlash()
    {
        const string chars = "34/x⁄\u2060";
        int Gl(int c) => Ot49Glyph(chars, c);
        var font = Ot49Font(chars, Ot49Layout(["latn"], [("numr", E11Sub((Gl('3'), Ot49Form), (Gl('4'), Ot49Form))),
            ("dnom", E11Sub((Gl('3'), Ot49Form2), (Gl('4'), Ot49Form2))), ("frac", E11Sub((Gl(0x2044), Ot49Form3)))]));
        Assert.Equal([Ot49Form, Ot49Form3, Ot49Form2], Ot49Shape(font, "3⁄4"));
        Assert.Equal(E19Library(font, "3⁄4"), Ot49Shape(font, "3⁄4"));
        Assert.Equal(Ot49Glyphs(chars, "3/4"), Ot49Shape(font, "3/4"));
        Assert.Equal(Ot49Glyphs(chars, "3⁄x"), Ot49Shape(font, "3⁄x"));
        Assert.DoesNotContain(Ot49Form3, Ot49Shape(font, "3\u2060⁄4"));
        Assert.Equal([Gl('3'), Ot49Form3, Ot49Form2], Ot49Shape(font, "3⁄4", "-numr"));
    }

    /// <summary>
    /// OT 65.6 sentences 1–12 together with UNI 23.2.3: the frac ligature of a font applies of itself to digits around the fraction slash and
    /// otherwise only when named — „3⁄4“ one glyph by default, „3/4“ three glyphs by default and one with frac named.
    /// </summary>
    [Fact]
    public void Ot50_Frac_AppliedToDigitsAroundTheFractionSlash_OtherwiseOnlyWhenNamed()
    {
        foreach (var text in new[] { "3⁄4", "3/4" })
        {
            var font = Ot49Font(text, Ot49Layout(["latn"], [("frac", Ot49Ligature(Ot49Glyphs(text, text), Ot49Form))]));
            Assert.Equal(text.Contains('⁄') ? [Ot49Form] : Ot49Glyphs(text, text), Ot49Shape(font, text));
            Assert.Equal([Ot49Form], Ot49Shape(font, text, "frac"));
        }
    }

    // ------------------------------------------------------------ 6. open questions: behaviour of today

    /// <summary>
    /// UNI 9.2.5 sentences 4–6 (yeh with hamza above U+0626 and the sequence U+064A U+0654): the sequence is drawn as the composed letter where
    /// the font has it — in Noto Sans Arabic the glyph of U+0626 (yeh without its dots), the same glyphs as the shaping library — and each
    /// spelling is saved and read back as typed (docs/FEHLERGEDAECHTNIS.md, No. 1193; before, the yeh with its dots and the hamza mark).
    /// </summary>
    [Fact]
    public void Uni05_YehWithHamzaAbove_SequenceDrawnAsTheComposedLetter()
    {
        var shaper = TextShaper.For(Uni04NotoArabic);
        var composed = shaper.Shape("\u0626").Select(g => g.Glyph).ToArray();
        var sequence = shaper.Shape("\u064A\u0654").Select(g => g.Glyph).ToArray();
        Assert.Equal(composed, sequence);
        Assert.Equal(ShaperTests.HarfBuzz(Uni04NotoArabic.FileData, Uni04NotoArabic.UnitsPerEm, "\u064A\u0654", []).Glyphs.Order(), sequence.Order());
        foreach (var text in new[] { "\u0626", "\u064A\u0654" }) Assert.Equal(text, Uni04RoundTrip(text).Block);
    }
}
