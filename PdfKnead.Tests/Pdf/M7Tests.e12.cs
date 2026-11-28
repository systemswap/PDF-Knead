using System.Diagnostics;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, the shaper of the syllabic scripts (docs/FEHLERGEDAECHTNIS.md, No. 1175): the Indic scripts of the second shaping model (and the old
/// tags), Khmer, Myanmar, Tibetan, Thai and Lao, Hangul jamo. The fonts are Liberation Sans with a cmap that maps the block of the script to
/// glyphs 100 … 227 (the dotted circle to 300, ZWNJ and ZWJ to 301 and 302) and with GSUB or GPOS built by hand; the forms the lookups make are
/// glyphs from 400 on. Where it applies, the bundled shaping library shapes the same font and must give the same glyphs.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (E12)

    private const int E12Base = 100, E12Dotted = 300, E12Zwnj = 301, E12Zwj = 302;

    /// <summary>A form made by a lookup.</summary>
    private static int E12F(int n) => 400 + n;

    /// <summary>The glyph of a character of the block starting at <paramref name="block"/>.</summary>
    private static int E12G(int block, int cp) => E12Base + cp - block;

    /// <summary>Liberation Sans with a cmap for the block (and space, ZWNJ, ZWJ, dotted circle) and the tables given.</summary>
    private static byte[] E12Font(int block, byte[]? gsub, byte[]? gpos = null, byte[]? gdef = null) =>
        ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
        {
            ["cmap"] = E11Cmap((' ', ' ', Ot02G(' ')), (block, block + 0x7F, E12Base), (0x200C, 0x200D, E12Zwnj), (0x25CC, 0x25CC, E12Dotted)),
            ["GSUB"] = gsub, ["GPOS"] = gpos, ["GDEF"] = gdef, ["kern"] = null,
        });

    private static ShapedGlyph[] E12Shape(byte[] font, string text) => TextShaper.For(OpenTypeFont.Parse(font)).Shape(text);

    private static int[] E12Own(byte[] font, string text) => [.. E12Shape(font, text).Select(g => g.Glyph)];

    /// <summary>The glyphs of the shaping library (it shows ZWNJ and ZWJ as the space glyph: given back as our glyphs of them).</summary>
    private static int[] E12Library(byte[] font, string text)
    {
        var glyphs = ShaperTests.HarfBuzz(font, 2048, text, []).Glyphs;
        var joiners = new Queue<int>(text.Where(c => c is '\u200C' or '\u200D').Select(c => c == '\u200C' ? E12Zwnj : E12Zwj));
        return [.. glyphs.Select(g => g == Ot02G(' ') && joiners.Count > 0 && !text.Contains(' ') ? joiners.Dequeue() : g)];
    }

    /// <summary>Asserts the glyphs expected and that the shaping library gives the same.</summary>
    private static void E12Expect(byte[] font, string text, params int[] expected)
    {
        Assert.Equal(expected, E12Own(font, text));
        Assert.Equal(expected, E12Library(font, text));
    }

    /// <summary>A ligature lookup.</summary>
    private static Action<Ot02Bytes> E12Lig(params (int[] Components, int Glyph)[] ligatures) => Ot02Lookup(4, Ot02Ligature(ligatures));

    /// <summary>A mark-to-base lookup (type 4, format 1) for one mark and one base with one anchor each.</summary>
    private static Action<Ot02Bytes> E12MarkToBase(int mark, int @base, (int X, int Y) markAnchor, (int X, int Y) baseAnchor) => Ot02Lookup(4, b =>
    {
        var start = b.Here;
        string mc = b.NewLabel(), bc = b.NewLabel(), ma = b.NewLabel(), ba = b.NewLabel(), mAnchor = b.NewLabel(), bAnchor = b.NewLabel();
        b.U16(1).Off16(mc, start).Off16(bc, start).U16(1).Off16(ma, start).Off16(ba, start);
        b.Label(mc);
        Ot02Coverage(b, [mark]);
        b.Label(bc);
        Ot02Coverage(b, [@base]);
        b.Label(ma);
        var markArray = b.Here;
        b.U16(1).U16(0).Off16(mAnchor, markArray);
        b.Label(mAnchor).U16(1).U16(markAnchor.X).U16(markAnchor.Y);
        b.Label(ba);
        var baseArray = b.Here;
        b.U16(1).Off16(bAnchor, baseArray);
        b.Label(bAnchor).U16(1).U16(baseAnchor.X).U16(baseAnchor.Y);
    });

    private const int Deva = 0x0900;

    private static int D(int cp) => E12G(Deva, cp);

    // Devanagari letters and signs used below
    private const char Ka = 'क', Kha = 'ख', Ta = 'त', Pa = 'प', Ra = 'र', Ssa = 'ष', Halant = '\u094D', SignI = '\u093F',
        SignU = '\u0941', SignE = '\u0947', SignAa = '\u093E', Nukta = '\u093C', Anusvara = '\u0902';

    // ------------------------------------------------------------ Indic: reordering

    /// <summary>
    /// OT 64.6, OT 65.8 (the stages and their order) and the shaping model of the Indic scripts: a dependent vowel sign that stands left of its
    /// consonant (Indic_Positional_Category Left, U+093F) is drawn before the consonant — after a half form it stays at the front of the
    /// conjunct, after a halant without a half form it follows the halant. „कU+093F“ → I K; „कU+094DकU+093F“ without a half form → K H I K, with a half form
    /// of Ka → I Ka-half K. Before, the sign was drawn after the consonant (K I), as typed.
    /// </summary>
    [Fact]
    public void E12_Devanagari_PreBaseMatra_DrawnBeforeTheBase()
    {
        var plain = E12Font(Deva, E11Layout("dev2", ("nukt", E11Sub((D(0x0905), D(0x0905))))));
        E12Expect(plain, $"{Ka}{SignI}", D(SignI), D(Ka));
        E12Expect(plain, $"{Ka}{Halant}{Ka}{SignI}", D(Ka), D(Halant), D(SignI), D(Ka));
        var half = E12Font(Deva, E11Layout("dev2", ("half", E12Lig(([D(Ka), D(Halant)], E12F(0))))));
        E12Expect(half, $"{Ka}{Halant}{Ka}{SignI}", D(SignI), E12F(0), D(Ka));
    }

    /// <summary>
    /// OT 67.15 (rphf) and OT 64.6: Ra and halant at the start of a syllable with a consonant after them form the reph (rphf only there), which
    /// then moves to the place of its script — in Devanagari to the end of the syllable before the syllable modifiers: „रU+094Dक“ → K Reph, „रU+094DकU+093F“
    /// → I K Reph, „रU+094DकU+0941“ → K U Reph; Ra and halant without a consonant after them form no reph (Ra is the base). Before, rphf was never applied
    /// and Ra stood first.
    /// </summary>
    [Fact]
    public void E12_Devanagari_Reph_FormedAndMovedBehindTheBase()
    {
        var font = E12Font(Deva, E11Layout("dev2", ("rphf", E12Lig(([D(Ra), D(Halant)], E12F(1))))));
        E12Expect(font, $"{Ra}{Halant}{Ka}", D(Ka), E12F(1));
        E12Expect(font, $"{Ra}{Halant}{Ka}{SignI}", D(SignI), D(Ka), E12F(1));
        E12Expect(font, $"{Ra}{Halant}{Ka}{SignU}", D(Ka), D(SignU), E12F(1));
        E12Expect(font, $"{Ra}{Halant}{Anusvara}", D(Ra), D(Halant), D(Anusvara));
    }

    /// <summary>
    /// OT 64.8 (blwf), OT 65.8 (half): the base consonant is the last one without a below-base form; halant and Ra after it take blwf (the
    /// Rakar), a consonant and halant before it take half — „पU+094Dर“ → Pa Rakar, „कU+094Dप“ → Ka-half Pa, „कU+094DपU+094Dर“ → Ka-half Pa Rakar, „पU+094DरU+093F“ → I Pa Rakar.
    /// Before, neither feature was applied.
    /// </summary>
    [Fact]
    public void E12_Devanagari_BelowBaseAndHalfForms_AtTheirPlaces()
    {
        var font = E12Font(Deva, E11Layout("dev2", ("blwf", E12Lig(([D(Halant), D(Ra)], E12F(2)))), ("half", E12Lig(([D(Ka), D(Halant)], E12F(0))))));
        E12Expect(font, $"{Pa}{Halant}{Ra}", D(Pa), E12F(2));
        E12Expect(font, $"{Ka}{Halant}{Pa}", E12F(0), D(Pa));
        E12Expect(font, $"{Ka}{Halant}{Pa}{Halant}{Ra}", E12F(0), D(Pa), E12F(2));
        E12Expect(font, $"{Pa}{Halant}{Ra}{SignI}", D(SignI), D(Pa), E12F(2));
    }

    /// <summary>
    /// OT 64.6 sentences 1–14 (akhn before half in the order of the basic forms): „कU+094Dष“ becomes the akhand ligature, not Ka-half Ssa; „कU+094Dत“
    /// takes the half form. Before, akhn was never applied (three glyphs).
    /// </summary>
    [Fact]
    public void E12_Devanagari_Akhand_BeforeTheHalfForm()
    {
        var font = E12Font(Deva, E11Layout("dev2", ("half", E12Lig(([D(Ka), D(Halant)], E12F(0)))), ("akhn", E12Lig(([D(Ka), D(Halant), D(Ssa)], E12F(3))))));
        E12Expect(font, $"{Ka}{Halant}{Ssa}", E12F(3));
        E12Expect(font, $"{Ka}{Halant}{Ta}", E12F(0), D(Ta));
    }

    /// <summary>
    /// OT 66.17 (nukt) and the normalization of the shaping model: Ka with nukta takes nukt; U+0958 (whose composition is excluded) is taken
    /// apart into Ka and nukta and takes nukt as well; Na and nukta typed apart become U+0929 where the font has it. Before, nukt was not
    /// applied and U+0958 kept its own glyph.
    /// </summary>
    [Fact]
    public void E12_Devanagari_Nukta_DecomposedAndComposed()
    {
        var font = E12Font(Deva, E11Layout("dev2", ("nukt", E12Lig(([D(Ka), D(Nukta)], E12F(4))))));
        E12Expect(font, $"{Ka}{Nukta}", E12F(4));
        E12Expect(font, "क़", E12F(4));
        E12Expect(font, "न\u093C", D(0x0929));
    }

    /// <summary>
    /// The joiners in a conjunct: a ZWNJ after the halant keeps the consonant from its half form (K H ZWNJ Ssa), a ZWJ asks for the half form
    /// even at the end (K H ZWJ → Ka-half ZWJ). Before, the half form was never applied.
    /// </summary>
    [Fact]
    public void E12_Devanagari_Joiners_KeepOrAskForTheHalfForm()
    {
        var font = E12Font(Deva, E11Layout("dev2", ("half", E12Lig(([D(Ka), D(Halant)], E12F(0))))));
        E12Expect(font, $"{Ka}{Halant}\u200C{Ssa}", D(Ka), D(Halant), E12Zwnj, D(Ssa));
        E12Expect(font, $"{Ka}{Halant}\u200D{Ssa}", E12F(0), E12Zwj, D(Ssa));
        E12Expect(font, $"{Ka}{Halant}\u200D", E12F(0), E12Zwj);
    }

    /// <summary>
    /// The shaping model for a sign without a base: the dotted circle (U+25CC, where the font has it) becomes its base — „U+093F“ alone → I and the
    /// dotted circle (the sign before it), also after a space. The circle has no text of its own: the text read back is the one typed. Before,
    /// the sign stood alone.
    /// </summary>
    [Fact]
    public void E12_Indic_DottedCircle_ForASignWithoutBase()
    {
        var font = E12Font(Deva, E11Layout("dev2", ("nukt", E11Sub((D(0x0905), D(0x0905))))));
        E12Expect(font, $"{SignI}", D(SignI), E12Dotted);
        E12Expect(font, $" {SignI}", Ot02G(' '), D(SignI), E12Dotted);
        Assert.Equal($" {SignI}", string.Concat(E12Shape(font, $" {SignI}").Select(g => g.Text)));
    }

    /// <summary>
    /// A reordered syllable is one cluster (as common shaping engines merge it): the first glyph stands for the whole text of the syllable, the
    /// others for none, so that the text read back comes in the order typed — „कU+093F“ → I with „कU+093F“, K with „“; „कU+093Fक“ → the second Ka keeps its
    /// own cluster. Before, each glyph kept its own character (and the glyphs stood in text order).
    /// </summary>
    [Fact]
    public void E12_Indic_ReorderedSyllable_IsOneCluster()
    {
        var font = E12Font(Deva, E11Layout("dev2", ("nukt", E11Sub((D(0x0905), D(0x0905))))));
        var shaped = E12Shape(font, $"{Ka}{SignI}{Ka}");
        Assert.Equal([D(SignI), D(Ka), D(Ka)], shaped.Select(g => g.Glyph));
        Assert.Equal([$"{Ka}{SignI}", "", $"{Ka}"], shaped.Select(g => g.Text));
        Assert.Equal([0, 0, 2], shaped.Select(g => g.Cluster));
    }

    /// <summary>
    /// The old shaping model (a font with only the old tag „deva“): the halant after the base goes behind the following consonant, so a
    /// below-base form is the ligature Ra halant — „पU+094Dर“ → Pa Rakar; the same text under „dev2“ with that ligature keeps Ra and halant. Before,
    /// neither model was followed.
    /// </summary>
    [Fact]
    public void E12_Devanagari_OldTag_HalantBehindThePostBaseConsonant()
    {
        Action<Ot02Bytes> rakar = E12Lig(([D(Ra), D(Halant)], E12F(2)));
        E12Expect(E12Font(Deva, E11Layout("deva", ("blwf", rakar))), $"{Pa}{Halant}{Ra}", D(Pa), E12F(2));
        E12Expect(E12Font(Deva, E11Layout("dev2", ("blwf", rakar))), $"{Pa}{Halant}{Ra}", D(Pa), D(Halant), D(Ra));
    }

    /// <summary>
    /// OT 66.17 sentences 6–13, OT 67.13, OT 68.4, OT 64.16 (the basic forms one after another: nukt, akhn, rphf, rkrf, pref, blwf, half, pstf,
    /// vatu, cjct): Ka nukta halant Ssa takes nukt before akhn (the nukta form joins the akhand ligature); Ka halant Ra takes rkrf before half
    /// (KRa, not Ka-half Ra); Pa halant Ra with blwf and vatu becomes Pa with the Rakar and then the vattu ligature; Da halant Ga takes cjct.
    /// Before, none of these features was applied.
    /// </summary>
    [Fact]
    public void E12_Devanagari_BasicForms_InTheOrderOfTheRegistry()
    {
        var font = E12Font(Deva, E11Layout("dev2",
            ("akhn", E12Lig(([E12F(4), D(Halant), D(Ssa)], E12F(3)))),
            ("nukt", E12Lig(([D(Ka), D(Nukta)], E12F(4)))),
            ("half", E12Lig(([D(Ka), D(Halant)], E12F(0)))),
            ("rkrf", E12Lig(([D(Ka), D(Halant), D(Ra)], E12F(20)))),
            ("vatu", E12Lig(([D(Pa), E12F(2)], E12F(21)))),
            ("blwf", E12Lig(([D(Halant), D(Ra)], E12F(2)))),
            ("cjct", E12Lig(([D(0x0926), D(Halant), D(0x0917)], E12F(22))))));
        E12Expect(font, $"{Ka}{Nukta}{Halant}{Ssa}", E12F(3));
        E12Expect(font, $"{Ka}{Halant}{Ra}", E12F(20));
        E12Expect(font, $"{Pa}{Halant}{Ra}", E12F(21));
        E12Expect(font, "द\u094Dग", E12F(22));
    }

    /// <summary>
    /// OT 67.6 (pres in Gujarati: Ka halant Ka → the conjunct), OT 67.8 (psts in Malayalam: Va with the post-base form of Va → the ligature), OT
    /// 64.4 (abvs in Kannada: Ga with vowel sign I → the ligature), OT 64.9 (blwm in Gujarati: vowel sign U placed under Ka). Before, none of them.
    /// </summary>
    [Fact]
    public void E12_Indic_PresentationFormsAndBelowMarks_OtherScripts()
    {
        const int gujr = 0x0A80, mlym = 0x0D00, knda = 0x0C80;
        var gujarati = E12Font(gujr, E11Layout("gjr2", ("pres", E12Lig(([E12G(gujr, 0x0A95), E12G(gujr, 0x0ACD), E12G(gujr, 0x0A95)], E12F(23))))),
            E11Layout("gjr2", ("blwm", E12MarkToBase(E12G(gujr, 0x0AC1), E12G(gujr, 0x0A95), (5, 0), (400, -50)))));
        E12Expect(gujarati, "ક\u0ACDક", E12F(23));
        var own = E11Drawn(E12Shape(gujarati, "ક\u0AC1"));
        var library = E11HarfBuzzDrawn(gujarati, 2048, "ક\u0AC1", false);
        Assert.Equal(library[1].X, own[1].X, 1);
        Assert.Equal(-50, own[1].Y, 1);
        var malayalam = E12Font(mlym, E11Layout("mlm2", ("pstf", E12Lig(([E12G(mlym, 0x0D4D), E12G(mlym, 0x0D35)], E12F(24)))),
            ("psts", E12Lig(([E12G(mlym, 0x0D35), E12F(24)], E12F(25))))));
        E12Expect(malayalam, "വ\u0D4Dവ", E12F(25));
        var kannada = E12Font(knda, E11Layout("knd2", ("abvs", E12Lig(([E12G(knda, 0x0C97), E12G(knda, 0x0CBF)], E12F(26))))));
        E12Expect(kannada, "ಗ\u0CBF", E12F(26));
    }

    // ------------------------------------------------------------ Indic: the stages after the reordering

    /// <summary>
    /// OT 67.6 (pres), OT 64.4 (abvs), OT 64.10 (blws), OT 67.8 (psts), OT 65.9 (haln): applied after the final reordering, each syllable on
    /// its own — Ka-half Pa → the pres ligature, Ka E → the abvs ligature, Pa U → the blws ligature, Ka AA → AA's psts form, Ka halant at the end
    /// → the haln form. Before, none of them was applied.
    /// </summary>
    [Fact]
    public void E12_Devanagari_PresentationStages_Applied()
    {
        var font = E12Font(Deva, E11Layout("dev2",
            ("half", E12Lig(([D(Ka), D(Halant)], E12F(0)))),
            ("pres", E12Lig(([E12F(0), D(Pa)], E12F(5)))),
            ("abvs", E12Lig(([D(Ka), D(SignE)], E12F(6)))),
            ("blws", E12Lig(([D(Pa), D(SignU)], E12F(7)))),
            ("psts", E11Sub((D(SignAa), E12F(8)))),
            ("haln", E12Lig(([D(Kha), D(Halant)], E12F(9))))));
        E12Expect(font, $"{Ka}{Halant}{Pa}", E12F(5));
        E12Expect(font, $"{Ka}{SignE}", E12F(6));
        E12Expect(font, $"{Pa}{SignU}", E12F(7));
        E12Expect(font, $"{Ka}{SignAa}", D(Ka), E12F(8));
        E12Expect(font, $"{Kha}{Halant}", E12F(9));
    }

    /// <summary>
    /// OT 65.17 (init): a pre-base vowel sign at the start of a word takes init — Bengali „কU+09C7“ → E-init Ka, in „কU+09C7কU+09C7“ only the first. Before,
    /// init was applied nowhere outside connected scripts.
    /// </summary>
    [Fact]
    public void E12_Bengali_Init_AtTheStartOfAWord()
    {
        const int beng = 0x0980;
        var font = E12Font(beng, E11Layout("bng2", ("init", E11Sub((E12G(beng, 0x09C7), E12F(10))))));
        E12Expect(font, "ক\u09C7", E12F(10), E12G(beng, 0x0995));
        E12Expect(font, "ক\u09C7ক\u09C7", E12F(10), E12G(beng, 0x0995), E12G(beng, 0x09C7), E12G(beng, 0x0995));
    }

    /// <summary>
    /// The shaping model for a vowel sign written in two parts: Bengali O (U+09CB) is taken apart into E and AA, E before the base — „কU+09CB“ →
    /// E Ka AA, read back as typed. Before, U+09CB kept its single glyph after Ka.
    /// </summary>
    [Fact]
    public void E12_Bengali_TwoPartVowel_TakenApart()
    {
        const int beng = 0x0980;
        var font = E12Font(beng, E11Layout("bng2", ("nukt", E11Sub((E12G(beng, 0x0985), E12G(beng, 0x0985))))));
        E12Expect(font, "ক\u09CB", E12G(beng, 0x09C7), E12G(beng, 0x0995), E12G(beng, 0x09BE));
        Assert.Equal("ক\u09CB", string.Concat(E12Shape(font, "ক\u09CB").Select(g => g.Text)));
    }

    /// <summary>
    /// OT 67.5 (pref): in Malayalam a halant and Ra after the base that pref substitutes is a pre-base reordering consonant — the form moves
    /// before the base: „കU+0D4Dര“ → Pref Ka. Before, pref was not applied.
    /// </summary>
    [Fact]
    public void E12_Malayalam_PreBaseReorderingRa_BeforeTheBase()
    {
        const int mlym = 0x0D00;
        var font = E12Font(mlym, E11Layout("mlm2", ("pref", E12Lig(([E12G(mlym, 0x0D4D), E12G(mlym, 0x0D30)], E12F(11))))));
        E12Expect(font, "ക\u0D4Dര", E12F(11), E12G(mlym, 0x0D15));
    }

    /// <summary>
    /// OT 67.7 (pstf): a consonant with a post-base form is not the base — Gurmukhi „ਕU+0A4Dਯ“ → Ka Ya-post. Before, pstf was not applied.
    /// </summary>
    [Fact]
    public void E12_Gurmukhi_PostBaseForm()
    {
        const int guru = 0x0A00;
        var font = E12Font(guru, E11Layout("gur2", ("pstf", E12Lig(([E12G(guru, 0x0A4D), E12G(guru, 0x0A2F)], E12F(12))))));
        E12Expect(font, "ਕ\u0A4Dਯ", E12G(guru, 0x0A15), E12F(12));
    }

    /// <summary>
    /// The place of the reph depends on the script: in Oriya right after the main consonant (before a below-base vowel sign), in Devanagari at
    /// the end of the syllable — „ରU+0B4DକU+0B41“ → Ka Reph U, „रU+094DकU+0941“ → Ka U Reph. Before, the reph was never formed.
    /// </summary>
    [Fact]
    public void E12_Indic_RephPlace_PerScript()
    {
        const int orya = 0x0B00;
        var oriya = E12Font(orya, E11Layout("ory2", ("rphf", E12Lig(([E12G(orya, 0x0B30), E12G(orya, 0x0B4D)], E12F(1))))));
        E12Expect(oriya, "ର\u0B4Dକ\u0B41", E12G(orya, 0x0B15), E12F(1), E12G(orya, 0x0B41));
        var deva = E12Font(Deva, E11Layout("dev2", ("rphf", E12Lig(([D(Ra), D(Halant)], E12F(1))))));
        E12Expect(deva, $"{Ra}{Halant}{Ka}{SignU}", D(Ka), D(SignU), E12F(1));
    }

    /// <summary>
    /// Kannada: Ra, halant and ZWJ at the start are Ra with a visible halant — the ZWJ goes before the halant and no reph is formed: „ರU+0CCDU+200Dಕ“ →
    /// Ra ZWJ halant Ka. Before, the glyphs stayed as typed.
    /// </summary>
    [Fact]
    public void E12_Kannada_RaHalantZwj_NoReph()
    {
        const int knda = 0x0C80;
        var font = E12Font(knda, E11Layout("knd2", ("rphf", E12Lig(([E12G(knda, 0x0CB0), E12G(knda, 0x0CCD)], E12F(1))))));
        E12Expect(font, "ರ\u0CCD\u200Dಕ", E12G(knda, 0x0CB0), E12Zwj, E12G(knda, 0x0CCD), E12G(knda, 0x0C95));
    }

    /// <summary>
    /// OT 64.3 (abvm), OT 64.9 (blwm): the mark positioning of the Indic scripts applies — an anusvara attached to Ka by a mark-to-base lookup
    /// under abvm sits where the anchors meet, as the shaping library places it. Before, abvm was not applied and the anusvara kept no offset.
    /// </summary>
    [Fact]
    public void E12_Indic_AboveBaseMarks_Abvm()
    {
        var gpos = E11Layout("dev2", ("abvm", E12MarkToBase(D(Anusvara), D(Ka), (10, 20), (300, 700))));
        var font = E12Font(Deva, E11Layout("dev2", ("nukt", E11Sub((D(0x0905), D(0x0905))))), gpos);
        var own = E11Drawn(E12Shape(font, $"{Ka}{Anusvara}"));
        var library = E11HarfBuzzDrawn(font, 2048, $"{Ka}{Anusvara}", false);
        Assert.Equal(library.Select(g => g.Glyph), own.Select(g => g.Glyph));
        Assert.Equal(library[1].X, own[1].X, 1);
        Assert.Equal(680, own[1].Y, 1);
    }

    /// <summary>
    /// The written text of a reordered syllable (ISO 32000-2, 14.9.4): formatted text „क कU+093F“ in a font for Devanagari draws Ka, space, I, Ka;
    /// the second Ka has no text of its own (its syllable's text stands on the I) and is wrapped in an empty /ActualText, so that it does not read
    /// as Ka a second time through ToUnicode (which maps its glyph to „क“ from the first word). Before, the glyphs stood in text order with a text
    /// each.
    /// </summary>
    [Fact]
    public void E12_ReorderedSyllable_WrittenWithoutDoubleText()
    {
        const string family = "M7 E12 Devanagari";
        E8UseFamily(family, OpenTypeFont.Parse(E12Font(Deva, E11Layout("dev2", ("nukt", E11Sub((D(0x0905), D(0x0905))))))));
        var content = System.Text.Encoding.ASCII.GetString(Core.Pdf.Editing.ElementAppearance.For(E8Text(family, $"{Ka} {Ka}{SignI}")).Form.RawData!);
        var empty = content.IndexOf("/Span <</ActualText <FEFF>>> BDC", StringComparison.Ordinal);
        Assert.True(empty >= 0, content);
        Assert.Contains($"<{D(Ka):X4}>", content[empty..content.IndexOf("EMC", empty, StringComparison.Ordinal)]);
        Assert.True(content.IndexOf($"<{D(SignI):X4}>", StringComparison.Ordinal) < empty, content);
    }

    // ------------------------------------------------------------ Khmer, Myanmar, Thai, Lao, Tibetan

    /// <summary>
    /// OT 64.2 (abvf), OT 67.5 (pref), OT 64.14 (cfar): in Khmer a pre-base vowel sign goes before the base, a vowel in two parts gets its
    /// pre-base part (U+17C1) before the base and abvf forms the other part, coeng and Ro go before the base and take pref, and the glyphs after
    /// them cfar — „កU+17BE“ → E Ka OE-above, „កU+17D2រ“ → Pref Ka, „កU+17D2រU+17D2ក“ → Pref Ka Coeng Ka-cfar, while „កU+17D2ក“ takes no cfar. Before, none of it.
    /// </summary>
    [Fact]
    public void E12_Khmer_PreBaseParts_CoengRo_Cfar()
    {
        const int khmr = 0x1780;
        int G(int cp) => E12G(khmr, cp);
        var font = E12Font(khmr, E11Layout("khmr",
            ("pref", E12Lig(([G(0x17D2), G(0x179A)], E12F(13)))),
            ("abvf", E11Sub((G(0x17BE), E12F(14)))),
            ("cfar", E11Sub((G(0x1780), E12F(15))))));
        E12Expect(font, "ក\u17C1", G(0x17C1), G(0x1780));
        E12Expect(font, "ក\u17BE", G(0x17C1), G(0x1780), E12F(14));
        E12Expect(font, "ក\u17D2រ", E12F(13), G(0x1780));
        E12Expect(font, "ក\u17D2រ\u17D2ក", E12F(13), G(0x1780), G(0x17D2), E12F(15));
        E12Expect(font, "ក\u17D2ក", G(0x1780), G(0x17D2), G(0x1780));
    }

    /// <summary>
    /// OT 67.5 (pref), OT 67.15 (rphf, the kinzi): in Myanmar the vowel sign E and medial Ra go before the base (E first), and the kinzi (Nga,
    /// asat, virama before a consonant) formed by rphf goes behind the base — „ကU+103CU+1031“ → E Medial-Ra Ka, „ငU+103AU+1039က“ → Ka Kinzi. Before, the glyphs
    /// stayed as typed.
    /// </summary>
    [Fact]
    public void E12_Myanmar_MedialRa_PreBaseVowel_Kinzi()
    {
        const int mymr = 0x1000;
        int G(int cp) => E12G(mymr, cp);
        var font = E12Font(mymr, E11Layout("mym2", ("rphf", E12Lig(([G(0x1004), G(0x103A), G(0x1039)], E12F(16))))));
        E12Expect(font, "က\u103C\u1031", G(0x1031), G(0x103C), G(0x1000));
        E12Expect(font, "င\u103A\u1039က", G(0x1000), E12F(16));
    }

    /// <summary>
    /// UNI 16 (Thai and Lao): SARA AM is taken apart into NIKHAHIT and SARA AA where the font has both, the nikhahit before the tone mark typed
    /// before it — Thai „กU+0E48ำ“ → Ko Nikhahit Mai-ek Sara-aa, Lao „ກU+0EC8ຳ“ likewise; the text read back is the one typed. Before, SARA AM kept its own
    /// glyph after the tone mark.
    /// </summary>
    [Fact]
    public void E12_ThaiLao_SaraAm_TakenApart()
    {
        const int thai = 0x0E00, lao = 0x0E80;
        var thaiFont = E12Font(thai, E11Layout("thai", ("ccmp", E11Sub((E12G(thai, 0x0E2E), E12G(thai, 0x0E2E))))));
        E12Expect(thaiFont, "ก\u0E48ำ", E12G(thai, 0x0E01), E12G(thai, 0x0E4D), E12G(thai, 0x0E48), E12G(thai, 0x0E32));
        Assert.Equal("ก\u0E48ำ", string.Concat(E12Shape(thaiFont, "ก\u0E48ำ").Select(g => g.Text)));
        var laoFont = E12Font(lao, E11Layout("lao ", ("ccmp", E11Sub((E12G(lao, 0x0EAE), E12G(lao, 0x0EAE))))));
        E12Expect(laoFont, "ກ\u0EC8ຳ", E12G(lao, 0x0E81), E12G(lao, 0x0ECD), E12G(lao, 0x0EC8), E12G(lao, 0x0EB2));
    }

    /// <summary>
    /// OT 64.10 (blws), OT 64.4 (abvs) for Tibetan stacks: a consonant with a subjoined consonant takes the blws ligature, and a vowel sign above
    /// the abvs ligature, each stack on its own — „ཀU+0FB2“ → the blws form, „ཀU+0F72“ → the abvs form. Before, neither feature was applied.
    /// </summary>
    [Fact]
    public void E12_Tibetan_Stacks_BlwsAbvs()
    {
        const int tibt = 0x0F40;
        int G(int cp) => E12G(tibt, cp);
        var font = E12Font(tibt, E11Layout("tibt", ("blws", E12Lig(([G(0x0F40), G(0x0FB2)], E12F(17)))), ("abvs", E12Lig(([G(0x0F40), G(0x0F72)], E12F(18))))));
        E12Expect(font, "ཀ\u0FB2", E12F(17));
        E12Expect(font, "ཀ\u0F72", E12F(18));
    }

    // ------------------------------------------------------------ Hangul

    private static readonly byte[] E12NotoCjk = File.ReadAllBytes(Path.Combine(E12Repo(), "Schriften", "CJK", "NotoSansCJK-Regular.ttc"));

    private static string E12Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>
    /// OT 66.4, 67.33, 68.8 with The Unicode Standard 3.12: conjoining jamo the font has as a precomposed syllable are composed into it (Noto Sans
    /// CJK, bundled) — U+1100 U+1161 U+11A8 → the glyph of U+AC01, U+AC00 U+11A8 likewise; a trailing consonant that does not compose
    /// (U+11C3) leaves the jamo to ljmo, vjmo and tjmo. The same glyphs as the shaping library. Before, the jamo were shaped one by one.
    /// </summary>
    [Fact]
    public void E12_Hangul_JamoComposedWhereTheFontHasTheSyllable()
    {
        var font = OpenTypeFont.Parse(E12NotoCjk);
        var shaper = TextShaper.For(font);
        foreach (var text in new[] { "각", "각", "가" })
        {
            var shaped = shaper.Shape(text);
            Assert.Equal(ShaperTests.HarfBuzz(E12NotoCjk, font.UnitsPerEm, text, []).Glyphs, shaped.Select(g => g.Glyph));
            Assert.Single(shaped);
            Assert.Equal(text, shaped[0].Text);
        }
        Assert.Equal(font.GlyphForUnicode(0xAC01), shaper.Shape("각")[0].Glyph);
        Assert.Equal(ShaperTests.HarfBuzz(E12NotoCjk, font.UnitsPerEm, "가ᇃ", []).Glyphs, shaper.Shape("가ᇃ").Select(g => g.Glyph));
    }

    // ------------------------------------------------------------ limits

    /// <summary>
    /// The work of the syllabic shaping is bounded like the rest of the shaper (No. 1137): 20 000 conjunct consonants (halant chains), and one
    /// consonant with 20 000 nuktas and vowel signs after it (a syllable is cut at 64 glyphs, so the reordering stays short), shape in bounded
    /// time and memory.
    /// </summary>
    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void E12_Syllables_LongTexts_Bounded()
    {
        var font = E12Font(Deva, E11Layout("dev2", ("rphf", E12Lig(([D(Ra), D(Halant)], E12F(1)))), ("half", E12Lig(([D(Ka), D(Halant)], E12F(0)))),
            ("blwf", E12Lig(([D(Halant), D(Ra)], E12F(2))))));
        var shaper = TextShaper.For(OpenTypeFont.Parse(font));
        var chains = string.Concat(Enumerable.Repeat($"{Ra}{Halant}{Ka}{Halant}", 10_000)) + SignI;
        var marks = Ka + string.Concat(Enumerable.Repeat($"{Nukta}{SignI}", 10_000));
        foreach (var text in new[] { chains, marks })
        {
            var watch = Stopwatch.StartNew();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var shaped = shaper.Shape(text);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 400_000_000, "Speicher");
            Assert.True(watch.ElapsedMilliseconds < 3000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
            Assert.Equal(text, string.Concat(shaped.Select(g => g.Text)));
        }
    }

    // ------------------------------------------------------------ AMTRA (UAX #53)

    /// <summary>The characters of the glyphs in drawing order (Liberation Sans with a cmap for the Arabic blocks and without layout tables, so no
    /// ligature merges them) — the order AMTRA gave the marks. Since No. 1180 a reordered cluster carries its text as typed on its first glyph,
    /// so the order is read from the glyphs (<see cref="E16Drawn"/>).</summary>
    private static string E12Amtra(string text) => E16Drawn(text);

    /// <summary>
    /// UAX53 2 sentence 18, 2 sentences 13–15, 3.2 sentence 6, 4.2, 4.3, 5.4 sentences 14–17, 5.8 sentences 9–10: the characters of class 0
    /// (CGJ U+034F, Combining Alef Overlay U+10EFC) end the sequence of non-starters, so CGJ overrides the order — beh kasra CGJ shadda, beh damma
    /// CGJ hamza above, waw damma CGJ hamza, beh kasra CGJ hamza below, beh shadda CGJ small yeh, sad sukun CGJ small seen, oe damma CGJ small
    /// noon, beh fatha CGJ dot, beh kasra alef-overlay shadda stay as typed; thal, U+08D9, CGJ, kasra, shadda only puts the shadda before the
    /// kasra. Before, the sequence was cut by general category and the shadda or modifier moved across the CGJ.
    /// </summary>
    [Fact]
    public void E12_Amtra_ClassZeroEndsTheSequence()
    {
        foreach (var text in new[]
                 {
                     "ب\u0650\u034F\u0651", "ب\u064F\u034F\u0654", "و\u064F\u034F\u0654", "ب\u0650\u034F\u0655", "ب\u0651\u034F\u06E7",
                     "ص\u0652\u034F\u06DC", "ۆ\u064F\u034F\u06E8", "ب\u064E\u034F\u08CE", "ب\u0650\U00010EFC\u0651", "ذ\u08D9\u034F\u0650",
                 })
            Assert.Equal(text, E12Amtra(text));
        Assert.Equal("ذ\u08D9\u034F\u0651\u0650", E12Amtra("ذ\u08D9\u034F\u0650\u0651"));
    }

    /// <summary>
    /// UAX53 3.2 sentences 7–8, 2 sentences 4–5, 5.8 sentences 7–8: only the modifier combining marks that lead the subsequence of class 230
    /// (or 220) move to the front — beh madda hamza-above, beh U+08D9 hamza-above, beh U+0656 hamza-below stay (the output stays canonically
    /// equivalent); beh fatha hamza-above, beh sukun small-high-noon, sad sukun small-high-seen still put the modifier first (4.3 examples 2b, 4a).
    /// Before, every modifier mark of the class moved to the front.
    /// </summary>
    [Fact]
    public void E12_Amtra_OnlyLeadingModifierMarksMove()
    {
        foreach (var text in new[] { "ب\u0653\u0654", "ب\u08D9\u0654", "ب\u0656\u0655" }) Assert.Equal(text, E12Amtra(text));
        Assert.Equal("ب\u0654\u064E", E12Amtra("ب\u064E\u0654"));
        Assert.Equal("ب\u06E8\u0652", E12Amtra("ب\u0652\u06E8"));
        Assert.Equal("ص\u06DC\u0652", E12Amtra("ص\u0652\u06DC"));
    }

    /// <summary>
    /// UAX53 3.2 sentence 5, 2 sentence 3: a precomposed base letter takes part with the mark it holds — alef-madda with fatha and hamza above:
    /// in NFD the madda leads the subsequence of class 230, so the hamza does not move before the fatha, as with alef, madda, fatha, hamza typed
    /// apart (the marks after the base in the same order) — in a font without the parts of alef-madda; with them the letter is drawn decomposed
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1180, <see cref="E16_Amtra_PrecomposedBase_DecomposedWhereTheFontHasTheParts"/>). Before, alef-madda
    /// fatha hamza drew the hamza first.
    /// </summary>
    [Fact]
    public void E12_Amtra_PrecomposedBase_MarksOrderedAsDecomposed()
    {
        Assert.Equal("آ\u064E\u0654", E16Drawn("آ\u064E\u0654", withoutAlef: true));
        Assert.Equal("ا\u064E\u0653\u0654", E12Amtra("ا\u0653\u064E\u0654"));
        Assert.Equal("أ\u0651\u064E", E12Amtra("أ\u064E\u0651"));
    }

    /// <summary>
    /// UAX53 3.2 sentences 1–4: the algorithm applies to every sequence with an Arabic mark, also after a letter that does not connect — hamza
    /// fatha shadda draws the shadda first. Before, a run without a connecting letter was not reordered.
    /// </summary>
    [Fact]
    public void E12_Amtra_RunWithoutConnectingLetter()
    {
        Assert.Equal("ء\u0651\u064E", E12Amtra("ء\u064E\u0651"));
    }

    /// <summary>
    /// The examples of UAX #53 (3.1, 4.3, 5.1, 5.4, 5.8) and the class rules of section 2: each modifier combining mark goes before the vowel of
    /// its class (above: fatha, below: kasra), U+0658, small high yeh, the small waws, small high noon and the large round dots before their
    /// vowel or shadda or sukun; U+0656 before U+08D9 (class 220 before 230); NFD order of superscript alef and madda after alef and lam; meem,
    /// the sukun-like marks and the maddas are no modifier marks (only the canonical order applies); alef overlay (class 0) keeps lam fatha
    /// overlay madda as it is. The text drawn is the order AMTRA gives; no dotted circle is ever put in (5.5).
    /// </summary>
    [Theory]
    [InlineData("ب\u064E\u0654", "ب\u0654\u064E")]
    [InlineData("ب\u064E\u0658", "ب\u0658\u064E")]
    [InlineData("ب\u064E\u06DC", "ب\u06DC\u064E")]
    [InlineData("ب\u064E\u06E7", "ب\u06E7\u064E")]
    [InlineData("ب\u064E\u06E8", "ب\u06E8\u064E")]
    [InlineData("ب\u064E\u08CA", "ب\u08CA\u064E")]
    [InlineData("ب\u064E\u08CB", "ب\u08CB\u064E")]
    [InlineData("ب\u064E\u08CD", "ب\u08CD\u064E")]
    [InlineData("ب\u064E\u08CE", "ب\u08CE\u064E")]
    [InlineData("ب\u064F\u08F3", "ب\u08F3\u064F")]
    [InlineData("ب\u0650\u0655", "ب\u0655\u0650")]
    [InlineData("ب\u0650\u06E3", "ب\u06E3\u0650")]
    [InlineData("ب\u0650\u08CF", "ب\u08CF\u0650")]
    [InlineData("ب\u0650\u08D3", "ب\u08D3\u0650")]
    [InlineData("ب\u0651\u06E7", "ب\u06E7\u0651")]
    [InlineData("ب\u0652\u06E8", "ب\u06E8\u0652")]
    [InlineData("ص\u06DC\u0652", "ص\u06DC\u0652")]
    [InlineData("ص\u0652\u06DC", "ص\u06DC\u0652")]
    [InlineData("ذ\u08D9\u0656", "ذ\u0656\u08D9")]
    [InlineData("ذ\u08D9\u034F\u0656", "ذ\u08D9\u034F\u0656")]
    [InlineData("ب\u0654\u08D9", "ب\u0654\u08D9")]
    [InlineData("ا\u0653\u0670", "ا\u0670\u0653")]
    [InlineData("ل\u0653\u0670", "ل\u0670\u0653")]
    [InlineData("ب\u064E\u06E2", "ب\u064E\u06E2")]
    [InlineData("ب\u06ED\u0650", "ب\u0650\u06ED")]
    [InlineData("ب\u06E1\u064E", "ب\u064E\u06E1")]
    [InlineData("ب\u0651\u06E1", "ب\u0651\u06E1")]
    [InlineData("ب\u0653\u064E", "ب\u064E\u0653")]
    [InlineData("ل\u064E\U00010EFC\u0653", "ل\u064E\U00010EFC\u0653")]
    public void E12_Amtra_ExamplesOfTheAnnex(string typed, string drawn)
    {
        Assert.Equal(drawn, E12Amtra(typed));
        Assert.DoesNotContain("◌", E12Amtra("ب\u0652\u0652\u064E"));
        Assert.Equal(4, TextShaper.For(OpenTypeFont.Parse(ShaperLimitTests.Liberation)).Shape("ب\u0652\u0652\u064E").Length);
    }
}
