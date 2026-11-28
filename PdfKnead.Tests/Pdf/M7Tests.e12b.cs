using System.Buffers.Binary;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using PdfKnead.Tests.Fonts;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, the rest of the shaper of the syllabic scripts and the differences of position measured against the bundled shaping library
/// (docs/FEHLERGEDAECHTNIS.md, No. 1179): Sinhala, the Hangul tone marks, Bengali Ya with nukta, Thai and Lao in fonts without GSUB, the joiners
/// in GPOS, the place of attached marks after all GPOS lookups and the advance of a composite glyph with USE_MY_METRICS. The fonts are built
/// as in <c>M7Tests.e12.cs</c> (Liberation Sans with a cmap for the block, GSUB and GPOS by hand); where it applies the shaping library shapes
/// the same font and must give the same glyphs and places.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (E12b)

    private const int Sinh = 0x0D80;

    private static int Si(int cp) => E12G(Sinh, cp);

    // Sinhala letters and signs used below
    private const int SiKa = 0x0D9A, SiTa = 0x0DAD, SiYa = 0x0DBA, SiRa = 0x0DBB, SiLakuna = 0x0DCA, SiAa = 0x0DCF, SiI = 0x0DD2, SiE = 0x0DD9,
        SiEe = 0x0DDA, SiO = 0x0DDC, SiOo = 0x0DDD, SiAu = 0x0DDE, SiAuPart = 0x0DDF;

    private static string Cps(params int[] cps) => string.Concat(cps.Select(char.ConvertFromUtf32));

    /// <summary>A glyph of Liberation Sans without advance (for joiners and a tone mark of zero width).</summary>
    private static readonly Lazy<int> E12bZeroGlyph = new(() =>
    {
        var font = OpenTypeFont.Parse(ShaperLimitTests.Liberation);
        return Enumerable.Range(3, 2000).First(g => font.AdvanceWidth(g) == 0);
    });

    /// <summary>Liberation Sans with the cmap groups given (the space and the joiners → a glyph without advance always) and the tables given.</summary>
    private static byte[] E12bFont((int First, int Last, int Glyph)[] groups, byte[]? gsub, byte[]? gpos = null, byte[]? gdef = null) =>
        ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
        {
            ["cmap"] = E11Cmap([(' ', ' ', Ot02G(' ')), (0x200C, 0x200D, E12bZeroGlyph.Value), .. groups]),
            ["GSUB"] = gsub, ["GPOS"] = gpos, ["GDEF"] = gdef, ["kern"] = null,
        });

    /// <summary>The glyphs other than joiners with their drawn places (pen plus offset, y offset) and the width of the whole text.</summary>
    private static ((int Glyph, double X, double Y)[] Drawn, double Width) E12bOwnDrawn(byte[] font, string text)
    {
        var shaped = E12Shape(font, text);
        var drawn = new List<(int, double, double)>();
        double pen = 0;
        foreach (var g in shaped)
        {
            if (g.Text is not ("\u200C" or "\u200D")) drawn.Add((g.Glyph, pen + g.XOffset, g.YOffset));
            pen += g.XAdvance;
        }
        return ([.. drawn], pen);
    }

    /// <summary>The same from the shaping library (which shows a joiner as the space glyph without advance).</summary>
    private static ((int Glyph, double X, double Y)[] Drawn, double Width) E12bLibraryDrawn(byte[] font, string text)
    {
        var hb = ShaperTests.HarfBuzz(font, 2048, text, []);
        var joiners = text.Count(c => c is '\u200C' or '\u200D');
        var drawn = new List<(int, double, double)>();
        double pen = 0;
        for (var i = 0; i < hb.Glyphs.Length; i++)
        {
            if (joiners > 0 && hb.Advances[i] == 0 && (hb.Glyphs[i] == Ot02G(' ') || hb.Glyphs[i] == E12bZeroGlyph.Value)) joiners--;
            else drawn.Add((hb.Glyphs[i], pen + hb.XOffsets[i], hb.YOffsets[i]));
            pen += hb.Advances[i];
        }
        return ([.. drawn], pen);
    }

    // ------------------------------------------------------------ Sinhala (question 41)

    /// <summary>
    /// OT 61 (sinh) and the shaping model of the Indic scripts for Sinhala: a run of Sinhala text is cut into syllables and reordered — the vowel
    /// sign kombuva (U+0DD9, standing left of its consonant) is drawn before the consonant: „කU+0DD9“ → E Ka, „තU+0DD9“ → E Ta; the shaping library gives the
    /// same. Before, Sinhala was shaped without syllables and the sign stood after the consonant, as typed.
    /// </summary>
    [Fact]
    public void E12b_Sinhala_PreBaseVowel_DrawnBeforeTheBase()
    {
        var font = E12Font(Sinh, E11Layout("sinh", ("blws", E11Sub((Si(0x0D85), Si(0x0D85))))));
        E12Expect(font, Cps(SiKa, SiE), Si(SiE), Si(SiKa));
        E12Expect(font, Cps(SiTa, SiE), Si(SiE), Si(SiTa));
        Assert.Equal(Cps(SiKa, SiE), string.Concat(E12Shape(font, Cps(SiKa, SiE)).Select(g => g.Text)));
    }

    /// <summary>
    /// The vowel signs of Sinhala written in two and three parts are taken apart canonically, the kombuva (U+0DD9) before the base — „කU+0DDC“ → E Ka
    /// Aa, „කU+0DDD“ → E Ka Aa Al-lakuna (three parts), „කU+0DDA“ → E Ka Al-lakuna, „කU+0DDE“ → E Ka the second part of AU — also where pstf of the font could form
    /// a second half from the sign itself (as the shaping library does, measured with Nirmala UI: it does not keep the sign then). The parts after
    /// the first have no text of their own. Before, every sign kept its own glyph after the consonant.
    /// </summary>
    [Fact]
    public void E12b_Sinhala_SplitVowels_TakenApartCanonically()
    {
        var plain = E12Font(Sinh, E11Layout("sinh", ("blws", E11Sub((Si(0x0D85), Si(0x0D85))))));
        E12Expect(plain, Cps(SiKa, SiO), Si(SiE), Si(SiKa), Si(SiAa));
        E12Expect(plain, Cps(SiKa, SiOo), Si(SiE), Si(SiKa), Si(SiAa), Si(SiLakuna));
        E12Expect(plain, Cps(SiKa, SiEe), Si(SiE), Si(SiKa), Si(SiLakuna));
        E12Expect(plain, Cps(SiKa, SiAu), Si(SiE), Si(SiKa), Si(SiAuPart));
        Assert.Equal(Cps(SiKa, SiOo), string.Concat(E12Shape(plain, Cps(SiKa, SiOo)).Select(g => g.Text)));

        var halves = E12Font(Sinh, E11Layout("sinh", ("pstf", E11Sub((Si(SiO), E12F(30)), (Si(SiOo), E12F(31))))));
        E12Expect(halves, Cps(SiKa, SiO), Si(SiE), Si(SiKa), Si(SiAa));
        E12Expect(halves, Cps(SiKa, SiOo), Si(SiE), Si(SiKa), Si(SiAa), Si(SiLakuna));
        E12Expect(halves, Cps(SiKa, SiEe), Si(SiE), Si(SiKa), Si(SiLakuna));
    }

    /// <summary>
    /// Sinhala reph, yansaya and rakaaraansaya through the joiner: Ra, al-lakuna and ZWJ before a consonant form the reph (rphf, explicit as in
    /// Telugu), which goes behind the main consonant — „රU+0DCAU+200Dක“ → Ka Reph, „රU+0DCAU+200DකU+0DCF“ → Ka Reph Aa; a consonant after al-lakuna and ZWJ is not the base
    /// (the base is the last consonant without a ZWJ before it), so the yansaya of pstf and the rakaaraansaya of vatu form after the base — „කU+0DCAU+200Dය“ →
    /// Ka Yansaya, „කU+0DCAU+200DයU+0DD9“ → E Ka Yansaya, „කU+0DCAU+200Dර“ → Ka Rakaaraansaya. Before, none of these forms was made.
    /// </summary>
    [Fact]
    public void E12b_Sinhala_RephYansayaRakaaraansaya_ThroughTheJoiner()
    {
        var font = E12Font(Sinh, E11Layout("sinh",
            ("rphf", E12Lig(([Si(SiRa), Si(SiLakuna), E12Zwj], E12F(32)))),
            ("pstf", E12Lig(([Si(SiLakuna), E12Zwj, Si(SiYa)], E12F(33)))),
            ("vatu", E12Lig(([Si(SiLakuna), E12Zwj, Si(SiRa)], E12F(34))))));
        E12Expect(font, Cps(SiRa, SiLakuna, 0x200D, SiKa), Si(SiKa), E12F(32));
        E12Expect(font, Cps(SiRa, SiLakuna, 0x200D, SiKa, SiAa), Si(SiKa), E12F(32), Si(SiAa));
        E12Expect(font, Cps(SiKa, SiLakuna, 0x200D, SiYa), Si(SiKa), E12F(33));
        E12Expect(font, Cps(SiKa, SiLakuna, 0x200D, SiYa, SiE), Si(SiE), Si(SiKa), E12F(33));
        E12Expect(font, Cps(SiKa, SiLakuna, 0x200D, SiRa), Si(SiKa), E12F(34));
        // the reph does not pass the al-lakuna of E-E after the base
        E12Expect(font, Cps(SiRa, SiLakuna, 0x200D, SiKa, SiEe), Si(SiE), Si(SiKa), E12F(32), Si(SiLakuna));
    }

    /// <summary>
    /// The kombuva in a Sinhala conjunct the font does not form: it goes behind the last al-lakuna that no form took before the last consonant,
    /// so it stands at the consonant it belongs to — „කU+0DCAU+200DතU+0DD9“ without a conjunct → Ka Al-lakuna E ZWJ Ta, „කU+0DCAතU+0DD9“ → Ka Al-lakuna E Ta; with a
    /// yansaya formed it stays before the base — „කU+0DCAU+200DයU+0DD9“ → E Ka Yansaya. As the shaping library places it (measured with Nirmala UI: 815
    /// samples with a conjunct the font lacks). Before, Sinhala was not reordered.
    /// </summary>
    [Fact]
    public void E12b_Sinhala_PreBaseVowel_AtTheConsonantItBelongsTo()
    {
        var font = E12Font(Sinh, E11Layout("sinh", ("pstf", E12Lig(([Si(SiLakuna), E12Zwj, Si(SiYa)], E12F(33))))));
        E12Expect(font, Cps(SiKa, SiLakuna, 0x200D, SiTa, SiE), Si(SiKa), Si(SiLakuna), Si(SiE), E12Zwj, Si(SiTa));
        E12Expect(font, Cps(SiKa, SiLakuna, SiTa, SiE), Si(SiKa), Si(SiLakuna), Si(SiE), Si(SiTa));
        E12Expect(font, Cps(SiKa, SiLakuna, 0x200D, SiYa, SiE), Si(SiE), Si(SiKa), E12F(33));
    }

    // ------------------------------------------------------------ canonical order of the marks (question 41)

    /// <summary>
    /// The Unicode Standard 3.11 (canonical ordering) for the syllabic scripts and Thai and Lao: a sequence of marks of combining classes other
    /// than 0 is put into canonical order before shaping, so that canonically equivalent texts look the same — Myanmar asat (class 9) and dot
    /// below (7) typed in either order → dot below, asat; Lao U+0EB8 (118) and U+0EBA (9) → U+0EBA, U+0EB8. The glyphs keep their texts. The same
    /// glyphs as the shaping library. Before, the marks stayed as typed (measured: 33 Myanmar samples with Myanmar Text, 55 Lao samples with
    /// Leelawadee UI otherwise than the shaping library).
    /// </summary>
    [Fact]
    public void E12b_CanonicalOrder_MarksOfTheSyllabicScripts()
    {
        const int mymr = 0x1000, lao = 0x0E80;
        var myanmar = E12Font(mymr, E11Layout("mym2", ("blws", E11Sub((E12G(mymr, 0x1021), E12G(mymr, 0x1021))))));
        E12Expect(myanmar, Cps(0x1000, 0x103A, 0x1037), E12G(mymr, 0x1000), E12G(mymr, 0x1037), E12G(mymr, 0x103A));
        E12Expect(myanmar, Cps(0x1000, 0x1037, 0x103A), E12G(mymr, 0x1000), E12G(mymr, 0x1037), E12G(mymr, 0x103A));
        var laoFont = E12Font(lao, E11Layout("lao ", ("ccmp", E11Sub((E12G(lao, 0x0EAE), E12G(lao, 0x0EAE))))));
        E12Expect(laoFont, Cps(0x0E81, 0x0EB8, 0x0EBA), E12G(lao, 0x0E81), E12G(lao, 0x0EBA), E12G(lao, 0x0EB8));
        Assert.Equal(Cps(0x0E81, 0x0EBA, 0x0EB8), string.Concat(E12Shape(laoFont, Cps(0x0E81, 0x0EB8, 0x0EBA)).Select(g => g.Text)));
    }

    // ------------------------------------------------------------ ZWJ in the context of a substitution

    /// <summary>
    /// A ZWJ asks for a joining or ligature of its neighbours (The Unicode Standard, 23.2) and does not stand between them for the context of a
    /// substitution: a Gujarati half form that a contextual lookup (calt) changes before Kha takes that form also across the ZWJ that asked for
    /// it — „કU+0ACDU+200Dખ“ → the changed half form, ZWJ, Kha; a context that names the ZWJ itself (as fonts do for the eyelash Ra) still matches it. The
    /// same glyphs as the shaping library (measured with Nirmala UI: 30 Gujarati samples otherwise). Before, the ZWJ broke the lookahead.
    /// </summary>
    [Fact]
    public void E12b_Gsub_ZwjPassedInTheContext()
    {
        const int gujr = 0x0A80;
        int G(int cp) => E12G(gujr, cp);
        var gsub = Ot02Layout([new Ot02Script("gjr2", new Ot02Lang("", [0, 1]))], [new Ot02Feature("half", [0]), new Ot02Feature("calt", [1, 2])],
        [
            E12Lig(([G(0x0A95), G(0x0ACD)], E12F(40))),
            Ot02Lookup(6, Ot02ChainGlyphs([], [E12F(40)], [G(0x0A96)], (0, 3))),
            Ot02Lookup(6, Ot02ChainGlyphs([], [G(0x0AB0)], [E12Zwj], (0, 4))),
            Ot02Lookup(1, Ot02Single((E12F(40), E12F(41)))),
            Ot02Lookup(1, Ot02Single((G(0x0AB0), E12F(42)))),
        ]);
        var font = E12Font(gujr, gsub);
        E12Expect(font, "ક\u0ACD\u200Dખ", E12F(41), E12Zwj, G(0x0A96));
        E12Expect(font, "ક\u0ACD\u200Dગ", E12F(40), E12Zwj, G(0x0A97));
        E12Expect(font, "ર\u200Dખ", E12F(42), E12Zwj, G(0x0A96));
    }

    /// <summary>
    /// Sinhala has no old shaping model (only the tag sinh): below-base forms also apply before the base — Ka al-lakuna Ta with a blwf ligature
    /// of Ka and al-lakuna → the form and Ta, as the shaping library gives it; under the old model of the other scripts blwf would not apply before
    /// the base. Before, blwf was not applied at all.
    /// </summary>
    [Fact]
    public void E12b_Sinhala_NoOldModel_BelowBaseFormsBeforeTheBase()
    {
        var font = E12Font(Sinh, E11Layout("sinh", ("blwf", E12Lig(([Si(SiKa), Si(SiLakuna)], E12F(35))))));
        E12Expect(font, Cps(SiKa, SiLakuna, SiTa), E12F(35), Si(SiTa));
    }

    // ------------------------------------------------------------ Hangul tone marks (question 41)

    /// <summary>The Hangul test font: syllables U+AC00 … from glyph 100, leading jamo from 400, vowels from 500, trailing ones from 600, the
    /// single dot tone mark → „A“ (with advance), the double dot → a glyph without advance, the dotted circle → 300.</summary>
    private static byte[] E12bHangul() => E12bFont(
        [(0xAC00, 0xAC7F, 100), (0x1100, 0x1112, 400), (0x1161, 0x1175, 500), (0x11A8, 0x11C2, 600), (0x302E, 0x302E, Ot02G('A')),
            (0x302F, 0x302F, E12bZeroGlyph.Value), (0x25CC, 0x25CC, E12Dotted)], null);

    /// <summary>
    /// The Hangul tone marks U+302E and U+302F belong to the syllable before them (precomposed, or conjoining jamo L V T): a tone mark with an
    /// advance is drawn before its syllable, both one cluster — „가U+302E“ → the tone mark, Ga (the tone mark's glyph standing for „가U+302E“), „각“ written
    /// as jamo with the tone mark likewise; a tone mark without an advance stays after the syllable (the font places it). Without a syllable
    /// before it — at the start, after a lone leading jamo, after a second tone mark — the dotted circle becomes its base: „U+302E“ → tone mark,
    /// circle; „U+302F“ → circle, tone mark. The same glyphs as the shaping library. Before, the tone mark always stood after its syllable and never
    /// had a base of its own.
    /// </summary>
    [Fact]
    public void E12b_Hangul_ToneMarks_WithTheirSyllable_DottedCircleOnlyWithoutOne()
    {
        var font = E12bHangul();
        int tone = Ot02G('A'), zero = E12bZeroGlyph.Value;
        void Expect(string text, params int[] glyphs)
        {
            Assert.Equal(glyphs, E12Own(font, text));
            Assert.Equal(glyphs, ShaperTests.HarfBuzz(font, 2048, text, []).Glyphs);
            Assert.Equal(text, string.Concat(E12Shape(font, text).Select(g => g.Text)));
        }
        Expect("가\u302E", tone, 100);
        Expect("각\u302E", tone, 101);
        Expect("가\u302F", 100, zero);
        Expect("\u302E", tone, E12Dotted);
        Expect("\u302F", E12Dotted, zero);
        Expect("ᄀ\u302E", 400, tone, E12Dotted);
        Expect("가\u302E\u302E", tone, 100, tone, E12Dotted);
        Expect("가\u302E가", tone, 100, 100);
        var shaped = E12Shape(font, "가\u302E");
        Assert.Equal(["가\u302E", ""], shaped.Select(g => g.Text));
        Assert.Equal([0, 0], shaped.Select(g => g.Cluster));
    }

    // ------------------------------------------------------------ Bengali Ya with nukta (question 41)

    /// <summary>
    /// Bengali YYA: U+09DF is canonically Ya with nukta (U+09AF U+09BC) and excluded from composition — like the other nukta pairs excluded
    /// from composition (U+0958 … U+095F, U+09DC, U+09DD, U+0A33 …) it is taken apart, also where the font has a glyph for it, and the pair typed
    /// apart stays apart: the font forms the nukta form with nukt, from either spelling the same glyphs (canonically equivalent texts look the
    /// same); without nukt Ya and nukta. The shaping library takes U+09DF apart as well, but composes the pair typed apart into U+09DF where the
    /// font has it, and keeps U+09DC whole (exceptions of its own, not of the Unicode Standard: two spellings of one text, two looks) —
    /// PDF-Knead keeps both spellings alike (measured with Nirmala UI: no difference, its nukt forms the same glyphs). Today's behaviour, kept.
    /// </summary>
    [Fact]
    public void E12b_Bengali_YaWithNukta_DecomposedLikeTheOtherNuktaPairs()
    {
        const int beng = 0x0980;
        int G(int cp) => E12G(beng, cp);
        var plain = E12Font(beng, null);
        E12Expect(plain, Cps(0x09DF), G(0x09AF), G(0x09BC));
        Assert.Equal([G(0x09AF), G(0x09BC)], E12Own(plain, Cps(0x09AF, 0x09BC)));
        Assert.Equal([G(0x09DF)], E12Library(plain, Cps(0x09AF, 0x09BC)));
        Assert.Equal([G(0x09A1), G(0x09BC)], E12Own(plain, Cps(0x09DC)));
        Assert.Equal([G(0x09DC)], E12Library(plain, Cps(0x09DC)));
        var nukt = E12Font(beng, E11Layout("bng2", ("nukt", E12Lig(([G(0x09AF), G(0x09BC)], E12F(36)), ([G(0x09A1), G(0x09BC)], E12F(37))))));
        E12Expect(nukt, Cps(0x09DF), E12F(36));
        Assert.Equal([E12F(36)], E12Own(nukt, Cps(0x09AF, 0x09BC)));
        Assert.Equal([E12F(36), G(0x09BE)], E12Own(nukt, Cps(0x09AF, 0x09BC, 0x09BE)));
        Assert.Equal([E12F(37)], E12Own(nukt, Cps(0x09DC)));
        Assert.Equal(Cps(0x09DF), string.Concat(E12Shape(nukt, Cps(0x09DF)).Select(g => g.Text)));
    }

    // ------------------------------------------------------------ Thai and Lao without GSUB (question 41)

    /// <summary>
    /// OT 64–68 (the registered features name no substitute for fonts without the features) and the shaping of Thai and Lao: in a font without
    /// GSUB the characters keep the glyphs of the cmap, in the order typed, and the marks keep their places; only SARA AM is taken apart (into the
    /// glyphs of the cmap). No substitute glyphs are looked up elsewhere (the private use area some older fonts carry is a convention of other
    /// programs, not of the specification): „กU+0E34U+0E48“ → Ko, Sara i, Mai ek; „ກU+0EB4U+0EC8“ likewise. Today's behaviour, kept.
    /// </summary>
    [Fact]
    public void E12b_ThaiAndLao_WithoutGsub_GlyphsOfTheCmap()
    {
        const int thai = 0x0E00, lao = 0x0E80;
        var thaiFont = E12Font(thai, null);
        E12Expect(thaiFont, "ก\u0E34\u0E48", E12G(thai, 0x0E01), E12G(thai, 0x0E34), E12G(thai, 0x0E48));
        E12Expect(thaiFont, "ป\u0E31\u0E49น", E12G(thai, 0x0E1B), E12G(thai, 0x0E31), E12G(thai, 0x0E49), E12G(thai, 0x0E19));
        E12Expect(thaiFont, "ก\u0E48ำ", E12G(thai, 0x0E01), E12G(thai, 0x0E4D), E12G(thai, 0x0E48), E12G(thai, 0x0E32));
        var laoFont = E12Font(lao, null);
        E12Expect(laoFont, "ກ\u0EB4\u0EC8", E12G(lao, 0x0E81), E12G(lao, 0x0EB4), E12G(lao, 0x0EC8));
    }

    // ------------------------------------------------------------ joiners in GPOS

    /// <summary>
    /// The joiners in GPOS: ZWJ and ZWNJ only ask for or prevent joining and ligatures between their neighbours (The Unicode Standard, 23.2;
    /// OpenType says nothing else about them), so positioning passes over them — the kerning pair A V applies across „AU+200DV“ and „AU+200CV“ as across
    /// „AV“, the pair of a Gujarati half form and Kha across the ZWJ that asked for the half form, and a mark after a joiner attaches to the base
    /// before it. The same places as the shaping library. Before, a joiner between the glyphs broke the pair and the attachment.
    /// </summary>
    [Fact]
    public void E12b_Gpos_JoinersPassedOver()
    {
        int a = Ot02G('A'), v = Ot02G('V'), acute = Ot02Font.GlyphForUnicode(0x0301);
        var latin = E12bFont([('A', 'A', a), ('V', 'V', v), (0x0301, 0x0301, acute)], null,
            Ot02Latin([new Ot02Feature("kern", [0]), new Ot02Feature("mark", [1])], Ot02Lookup(2, Ot02Pairs(0, (a, v, -150))),
                E12MarkToBase(acute, a, (10, 20), (600, 1400))),
            Ot02Gdef(new Dictionary<int, int> { [a] = 1, [v] = 1, [acute] = 3 }));
        var plain = E12bOwnDrawn(latin, "AV");
        foreach (var text in new[] { "A\u200DV", "A\u200CV" })
        {
            var own = E12bOwnDrawn(latin, text);
            Assert.Equal(plain.Drawn, own.Drawn);
            Assert.Equal(E12bLibraryDrawn(latin, text).Drawn, own.Drawn);
        }
        var mark = E12bOwnDrawn(latin, "A\u200D\u0301");
        Assert.Equal((acute, 590.0, 1380.0), mark.Drawn[1]);
        Assert.Equal(E12bLibraryDrawn(latin, "A\u200D\u0301").Drawn, mark.Drawn);

        const int gujr = 0x0A80;
        int G(int cp) => E12G(gujr, cp);
        var gujarati = E12bFont([(gujr, gujr + 0x7F, E12Base)], E11Layout("gjr2", ("half", E12Lig(([G(0x0A95), G(0x0ACD)], E12F(40))))),
            E11Layout("gjr2", ("kern", Ot02Lookup(2, Ot02Pairs(0, (E12F(40), G(0x0A96), -120))))));
        var joined = E12bOwnDrawn(gujarati, "ક\u0ACD\u200Dખ");
        Assert.Equal([E12F(40), G(0x0A96)], joined.Drawn.Select(d => d.Glyph));
        Assert.Equal(Ot02Font.AdvanceWidth(E12F(40)) - 120, joined.Drawn[1].X);
        Assert.Equal(E12bLibraryDrawn(gujarati, "ક\u0ACD\u200Dખ").Drawn, joined.Drawn);
    }

    // ------------------------------------------------------------ attached marks after all GPOS lookups

    /// <summary>Single positioning format 1 with XPlacement and XAdvance (value format 5) for the glyphs given.</summary>
    private static Action<Ot02Bytes> E12bSinglePlacementAdvance(int[] glyphs, int placement, int advance) => b =>
    {
        var start = b.Here;
        var coverage = b.NewLabel();
        b.U16(1).Off16(coverage, start).U16(5).U16(placement & 0xFFFF).U16(advance & 0xFFFF);
        b.Label(coverage);
        Ot02Coverage(b, glyphs);
    };

    /// <summary>
    /// OT 20.4.5 sentences 5, 10 (the mark is placed with respect to the final position of its base) and OT 20.4.7 sentences 9–11: the place of an
    /// attached mark follows its base to the end of GPOS — an anusvara attached to Ka by abvm, after which a later lookup (dist) moves Ka by 50
    /// and widens it by 200, sits on Ka where the anchors meet (x = 50 + 300 − 10, y = 700 − 20); a mark attached to that mark (mkmk) moves with
    /// both. The same places as the shaping library. Before, the mark was placed when it was attached and stayed there (200 units right of its
    /// base, as measured with Myanmar Text).
    /// </summary>
    [Fact]
    public void E12b_Gpos_AttachedMarks_FollowTheirBaseToTheEnd()
    {
        const int udatta = 0x0951;
        // one feature abvm with the mark-to-base and the mark-to-mark lookup, then dist
        var gpos = Ot02Layout([new Ot02Script("dev2", new Ot02Lang("", [0, 1]))], [new Ot02Feature("abvm", [0, 1]), new Ot02Feature("dist", [2])], [
            E12MarkToBase(D(Anusvara), D(Ka), (10, 20), (300, 700)),
            Ot02Lookup(6, b =>
            {
                var start = b.Here;
                string mc = b.NewLabel(), bc = b.NewLabel(), ma = b.NewLabel(), ba = b.NewLabel(), mAnchor = b.NewLabel(), bAnchor = b.NewLabel();
                b.U16(1).Off16(mc, start).Off16(bc, start).U16(1).Off16(ma, start).Off16(ba, start);
                b.Label(mc);
                Ot02Coverage(b, [D(udatta)]);
                b.Label(bc);
                Ot02Coverage(b, [D(Anusvara)]);
                b.Label(ma);
                var markArray = b.Here;
                b.U16(1).U16(0).Off16(mAnchor, markArray);
                b.Label(mAnchor).U16(1).U16(0).U16(0);
                b.Label(ba);
                var baseArray = b.Here;
                b.U16(1).Off16(bAnchor, baseArray);
                b.Label(bAnchor).U16(1).U16(5).U16(100);
            }),
            Ot02Lookup(1, E12bSinglePlacementAdvance([D(Ka)], 50, 200))]);
        var gdef = Ot02Gdef(new Dictionary<int, int> { [D(Ka)] = 1, [D(Anusvara)] = 3, [D(udatta)] = 3 });
        var font = E12Font(Deva, null, gpos, gdef);
        var text = $"{Ka}{Anusvara}\u0951";
        var own = E12bOwnDrawn(font, text);
        Assert.Equal((D(Ka), 50.0, 0.0), own.Drawn[0]);
        Assert.Equal((D(Anusvara), 340.0, 680.0), own.Drawn[1]);
        Assert.Equal((D(udatta), 345.0, 780.0), own.Drawn[2]);
        Assert.Equal(E12bLibraryDrawn(font, text).Drawn, own.Drawn);
        Assert.Equal(E12bLibraryDrawn(font, text).Width, own.Width);
    }

    // ------------------------------------------------------------ limits

    /// <summary>
    /// The work of the new parts is bounded like the rest of the shaper (No. 1137): a syllable with 20 000 tone marks (each further one gets a
    /// dotted circle, built in one pass), 20 000 joiners between a kerning pair and before the lookahead of a context, Ka with 20 000 anusvaras
    /// attached under abvm and moved by dist, Myanmar with 20 000 marks (longer than the canonical ordering takes) shape in bounded time and
    /// memory, the text read back as typed.
    /// </summary>
    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void E12b_LongTexts_Bounded()
    {
        var latin = E12bFont([('A', 'A', Ot02G('A')), ('V', 'V', Ot02G('V'))], null,
            Ot02Latin([new Ot02Feature("kern", [0])], Ot02Lookup(2, Ot02Pairs(0, (Ot02G('A'), Ot02G('V'), -150)))));
        const int gujr = 0x0A80;
        var context = E12Font(gujr, Ot02Layout([new Ot02Script("gjr2", new Ot02Lang("", [0]))], [new Ot02Feature("calt", [0])],
            [Ot02Lookup(6, Ot02ChainGlyphs([], [E12G(gujr, 0x0A95)], [E12G(gujr, 0x0A96)], (0, 1))), Ot02Lookup(1, Ot02Single((E12G(gujr, 0x0A95), E12F(41))))]));
        var marks = E12Font(Deva, null, E11Layout("dev2", ("abvm", E12MarkToBase(D(Anusvara), D(Ka), (10, 20), (300, 700))),
            ("dist", Ot02Lookup(1, E12bSinglePlacementAdvance([D(Ka)], 50, 200)))), Ot02Gdef(new Dictionary<int, int> { [D(Ka)] = 1, [D(Anusvara)] = 3 }));
        var myanmar = E12Font(0x1000, E11Layout("mym2", ("blws", E11Sub((E12G(0x1000, 0x1021), E12G(0x1000, 0x1021))))));
        (byte[] Font, string Text)[] cases =
        [
            (E12bHangul(), "가" + new string('\u302E', 20_000)),
            (latin, "A" + new string('\u200D', 20_000) + "V"),
            (context, "ક" + new string('\u200D', 20_000) + "ખ"),
            (marks, Ka + new string(Anusvara, 20_000)),
            (myanmar, "က" + string.Concat(Enumerable.Repeat("\u103A\u1037", 10_000))),
        ];
        foreach (var (font, text) in cases)
        {
            var shaper = TextShaper.For(OpenTypeFont.Parse(font));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var shaped = shaper.Shape(text);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 400_000_000, "Speicher");
            Assert.True(watch.ElapsedMilliseconds < 3000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
            Assert.Equal(text.Length, string.Concat(shaped.Select(g => g.Text)).Length);
        }
    }

    // ------------------------------------------------------------ USE_MY_METRICS and the advance

    /// <summary>
    /// Liberation Sans whose „A“ is a composite of a triangle (glyph 10, advance 1000) and a box (glyph 11, advance 300) at x 500 with
    /// USE_MY_METRICS on the box, its own hmtx advance 1100, and whose „B“ is a tall box (0 … 100 × 0 … 1200, advance 300).
    /// </summary>
    private static byte[] E12bUseMyMetricsFont()
    {
        var font = Ot27Sans.Value;
        int ga = font.GlyphForUnicode('A'), gb = font.GlyphForUnicode('B');
        var glyphs = new Dictionary<int, byte[]>
        {
            [10] = Ot20Simple([[(0, 0, true), (450, 300, true), (900, 0, true)]]), [11] = Ot20Simple([Ot20Rect(0, 0, 100, 400)]),
            [ga] = Ot20Composite([new Ot20Part(Ot20Xy, 10, 0, 0), new Ot20Part(Ot20Xy | 0x0200, 11, 500, 0)]), [gb] = Ot20Simple([Ot20Rect(0, 0, 100, 1200)]),
        };
        var metrics = new Dictionary<int, int> { [10] = 1000, [11] = 300, [ga] = 1100, [gb] = 300 };
        var glyf = new List<byte>();
        var loca = new byte[(font.GlyphCount + 1) * 4];
        for (var g = 0; g < font.GlyphCount; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(g * 4), (uint)glyf.Count);
            glyf.AddRange(glyphs.TryGetValue(g, out var d) ? d : Ot27GlyphData(font, g));
            while (glyf.Count % 4 != 0) glyf.Add(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(font.GlyphCount * 4), (uint)glyf.Count);
        var head = Ot27Table(Ot27SansFile.Value, "head");
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        var hmtx = Ot27Table(Ot27SansFile.Value, "hmtx");
        foreach (var (g, advance) in metrics)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(g * 4), (ushort)advance);
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(g * 4 + 2), 0);
        }
        return Ot27SansWith(("glyf", [.. glyf]), ("loca", loca), ("head", head), ("hmtx", hmtx));
    }

    /// <summary>
    /// OT 25 sentences 5 and 15 (the advance always from hmtx, which must be used for advance widths) with OT 19.4 sentences 56–57 and 97–100
    /// (USE_MY_METRICS gives the composite the side bearings and phantom points of the component; hmtx should agree): the shaper sets a composite
    /// with USE_MY_METRICS by its hmtx advance — „AB“ → A advances 1100, not the component's 300 — as the shaping library does (measured with
    /// Nirmala UI: 65 Gurmukhi samples, component 1675, hmtx 1672); the width written for new text comes from the same advance (the shaper does
    /// not contradict /Widths). Where a PDF gives no width for an embedded TrueType font the program's hmtx advance counts — B drawn at 20 + 55 pt,
    /// as two of the three reference readers draw it (the third takes a width of a standard font; probes u1–u3, none takes the component's).
    /// Before, the component's advance counted everywhere (B at 20 + 15 pt; docs/FEHLERGEDAECHTNIS.md, No. 1163, No. 1179).
    /// </summary>
    [Fact]
    public void E12b_UseMyMetrics_AdvanceFromHmtx_InTheShaperAndWithoutWidths()
    {
        var data = E12bUseMyMetricsFont();
        var font = OpenTypeFont.Parse(data);
        var shaped = TextShaper.For(font).Shape("AB", ["-kern"]);
        Assert.Equal(1100, shaped[0].XAdvance);
        Assert.Equal(ShaperTests.HarfBuzz(data, font.UnitsPerEm, "AB", ["-kern"]).Advances, shaped.Select(g => (int)g.XAdvance));
        Assert.Equal(1100, font.AdvanceWidth(font.GlyphForUnicode('A')));

        var (doc, list) = InterpreterTests.Page("BT /F1 102.4 Tf 20 30 Td (AB) Tj ET", pdf =>
        {
            var file = pdf.Add(TestPdf.FlateStream(data, TestPdf.Dict(("Length1", PdfInteger.Of(data.Length)))));
            var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Probe")), ("Flags", PdfInteger.Of(32)),
                ("FontBBox", TestPdf.Box(0, 0, 1000, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(800)), ("Descent", PdfInteger.Of(-200)),
                ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file)));
            var dict = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("ABCDEF+Probe")),
                ("Encoding", TestPdf.N("WinAnsiEncoding")), ("FontDescriptor", TestPdf.Ref(pdf.Add(descriptor))));
            return TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(pdf.Add(dict))))));
        });
        using var bitmap = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
        SKColor Pixel(double x, double y) => bitmap.GetPixel((int)x, bitmap.Height - 1 - (int)y);
        Assert.True(Pixel(77, 75).Red < 100, $"B nicht bei 75–80 pt: {Pixel(77, 75)}");
        Assert.True(Pixel(37, 75).Red > 200, $"B bei 35–40 pt: {Pixel(37, 75)}");
    }
}
