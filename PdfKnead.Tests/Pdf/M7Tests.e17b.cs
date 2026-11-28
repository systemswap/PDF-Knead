using System.Diagnostics;
using System.Text;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, the universal shaping model (docs/FEHLERGEDAECHTNIS.md, No. 1181), second part: the scripts of the Unicode chapters 16, 17 and 18 (Tai Le,
/// Tai Tham, Tai Viet, Kayah Li, Cham, the Philippine scripts, Buginese, Balinese, Javanese, Rejang, Batak, Sundanese, Makasar, Kawi, Miao, the
/// Khitan Small Script), the choice of the model, its joining forms, its limits and the comparison of random clusters with the shaping library.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ Tai Le, Tai Tham, Tai Viet, Kayah Li, Cham (uni-13)

    /// <summary>
    /// UNI 16.5.1 sentences 10–23: a combining mark of the old orthography (U+0308) after a tall letter stands right of it — the font's anchor,
    /// whether its lookup is under mark or under abvm (the model applies abvm). Before, a lookup under abvm was not applied.
    /// </summary>
    [Fact]
    public void E17_TaiLe_CombiningMarkByMarkOrAbvm()
    {
        foreach (var feature in new[] { "mark", "abvm" })
        {
            var gpos = E17Layout("tale", [(feature, [0])], E17Attach(4, 380, E12G(0x1950, 0x1964), (0, 0), (900, 1300)));
            var font = E17Font(null, gpos, (0x1950, 0x197F, E12Base), (0x0308, 0x0308, 380));
            var drawn = E17SamePositions(font, "\u1964\u0308");
            Assert.Equal((380, 900.0, 1300.0), drawn[1]);
        }
    }

    private const int TaiTham = 0x1A20;

    private static int Tt(int cp) => E12G(TaiTham, cp);

    /// <summary>
    /// UNI 16.7.1 sentences 10–15 and 18: a consonant after SAKOT U+1A60 takes its below form — blwf of the font (SAKOT and NA → the subjoined
    /// form) applies in the cluster. Before, blwf was not applied (KA, SAKOT, NA stayed).
    /// </summary>
    [Fact]
    public void E17_TaiTham_SakotWithConsonantTakesTheBelowForm()
    {
        var font = E17Block(TaiTham, 0x80, E17Layout("lana", [("blwf", [0])], E12Lig(([Tt(0x1A60), Tt(0x1A36)], E12F(0)))));
        E17Expect(font, "\u1A20\u1A60\u1A36", Tt(0x1A20), E12F(0));
    }

    /// <summary>
    /// UNI 16.7.1 sentences 22–33: the vowel signs standing left (E U+1A6E) are drawn before their consonant, AA and tall AA as typed; the medial
    /// Ra U+1A55, also drawn left, keeps its place after the consonant as with the shaping library — the model moves only pre-base vowel signs and
    /// vowel modifiers, fonts made for it place the medial themselves (special rule). Before, E followed its consonant.
    /// </summary>
    [Fact]
    public void E17_TaiTham_PreBaseVowelReordered_MedialRaLeftToTheFont()
    {
        var font = E17Block(TaiTham, 0x80, null);
        E17Expect(font, "\u1A20\u1A6E", Tt(0x1A6E), Tt(0x1A20));
        E17Expect(font, "\u1A20\u1A63", Tt(0x1A20), Tt(0x1A63));
        E17Expect(font, "\u1A20\u1A64", Tt(0x1A20), Tt(0x1A64));
        E17Expect(font, "\u1A20\u1A55", Tt(0x1A20), Tt(0x1A55));
    }

    /// <summary>
    /// UNI 16.7.1 sentences 34–39 and 50: a tone mark sits above the vowel above its consonant, or on the consonant without one (abvm,
    /// mark-to-base and mark-to-mark), cryptogrammic dots stack below (blwm). Before, abvm and blwm were not applied.
    /// </summary>
    [Fact]
    public void E17_TaiTham_TonesAndCryptogrammicDotsByAbvmAndBlwm()
    {
        int ka = Tt(0x1A20), tone = Tt(0x1A75), dot = Tt(0x1A7F);
        var gpos = E17Layout("lana", [("abvm", [0, 1]), ("blwm", [2, 3])],
            E17Attach(4, tone, ka, (0, 0), (300, 1400)), E17Attach(6, tone, Tt(0x1A65), (0, 0), (0, 400)),
            E17Attach(4, dot, ka, (0, 0), (500, -100)), E17Attach(6, dot, dot, (0, 0), (0, -250)));
        var font = E17Block(TaiTham, 0x80, null, gpos);
        Assert.Equal((tone, 300.0, 1400.0), E17SamePositions(font, "\u1A20\u1A75")[1]);
        E17SamePositions(font, "\u1A20\u1A65\u1A75");
        var dots = E17SamePositions(font, "\u1A20\u1A7F\u1A7F");
        Assert.Equal((dot, 500.0, -350.0), dots[2]);
    }

    private const int TaiViet = 0xAA80;

    private static int Tv(int cp) => E12G(TaiViet, cp);

    /// <summary>
    /// UNI 16.8.1 sentences 19–26 and 34–40: the vowels written before their consonant (U+AAB5, visual order) stay as typed without a dotted
    /// circle; the tone marks mai ek and mai tho sit on the consonant or on a vowel above it (abvm), the tone letters of Vietnam follow with an
    /// advance. Before, abvm was not applied.
    /// </summary>
    [Fact]
    public void E17_TaiViet_VisualOrderVowelsAsTyped_TonesByAbvm()
    {
        var gpos = E17Layout("tavt", [("abvm", [0, 1])],
            E17Attach(4, Tv(0xAABF), Tv(0xAA80), (0, 0), (350, 1450)), E17Attach(6, Tv(0xAABF), Tv(0xAAB2), (0, 0), (0, 380)));
        var font = E17Block(TaiViet, 0x60, null, gpos);
        E17Expect(font, "\uAAB5\uAA80", Tv(0xAAB5), Tv(0xAA80));
        Assert.Equal((Tv(0xAABF), 350.0, 1450.0), E17SamePositions(font, "\uAA80\uAABF")[1]);
        E17SamePositions(font, "\uAA80\uAAB2\uAABF");
        var letters = E12Shape(font, "\uAA80\uAAC0");
        Assert.All(letters, g => Assert.True(g.XAdvance > 0));
    }

    /// <summary>
    /// UNI 16.9.1 sentences 12–18: the vowel marks sit above the carrier U+A922 (abvm), the tone marks below the independent vowels (blwm).
    /// Before, abvm and blwm were not applied (only a lookup under mark was).
    /// </summary>
    [Fact]
    public void E17_KayahLi_VowelAndToneMarksOnTheCarrier()
    {
        int carrier = E12G(0xA900, 0xA922);
        var gpos = E17Layout("kali", [("abvm", [0]), ("blwm", [1])],
            E17Attach(4, E12G(0xA900, 0xA926), carrier, (0, 0), (400, 1500)), E17Attach(4, E12G(0xA900, 0xA92B), carrier, (0, 0), (420, -120)));
        var font = E17Block(0xA900, 0x30, null, gpos);
        Assert.Equal((E12G(0xA900, 0xA926), 400.0, 1500.0), E17SamePositions(font, "\uA922\uA926")[1]);
        Assert.Equal((E12G(0xA900, 0xA92B), 420.0, -120.0), E17SamePositions(font, "\uA922\uA92B")[1]);
    }

    private const int Cham = 0xAA00;

    private static int Ch(int cp) => E12G(Cham, cp);

    /// <summary>
    /// UNI 16.10.1 sentences 18–25 and 32–45: the vowel signs O and AI (U+AA2F, U+AA30) stored after the consonant are drawn before it, with a
    /// further vowel only O before; -ya ligates with its consonant (pstf of the font), a vowel sign then sits on the ligature; the reordered
    /// syllable is one cluster and comes back as typed. The consonant sign -ra U+AA34, drawn left, keeps its place as with the shaping library
    /// (special rule, as Tai Tham U+1A55). Before, O followed the consonant and pstf was not applied.
    /// </summary>
    [Fact]
    public void E17_Cham_PreBaseVowelsReordered_YaLigature_MedialRaLeftToTheFont()
    {
        var font = E17Block(Cham, 0x60, E17Layout("cham", [("pstf", [0])], E12Lig(([Ch(0xAA06), Ch(0xAA33)], E12F(0)))));
        E17Expect(font, "\uAA06\uAA2F", Ch(0xAA2F), Ch(0xAA06));
        E17Expect(font, "\uAA06\uAA2F\uAA29", Ch(0xAA2F), Ch(0xAA06), Ch(0xAA29));
        E17Expect(font, "\uAA06\uAA33\uAA2D", E12F(0), Ch(0xAA2D));
        E17Expect(font, "\uAA06\uAA34", Ch(0xAA06), Ch(0xAA34));
        var (block, page) = E17RoundTrip(font, "\uAA06\uAA2F");
        Assert.Equal("\uAA06\uAA2F", block);
        Assert.Equal("\uAA06\uAA2F", page);
    }

    // ------------------------------------------------------------ Philippine scripts, Buginese, Balinese, Javanese, Rejang, Batak, Sundanese, Makasar, Kawi (uni-14)

    /// <summary>
    /// UNI 17.1.2 sentences 28–47: in Tagbanwa the vowel signs simply sit above or below the consonant (abvm), in Hanunóo and Buhid each consonant
    /// with i and with u is a ligature of the font (psts, blws) — one consonant of every row of Table 17-1. Before, abvm, psts and blws were not
    /// applied.
    /// </summary>
    [Fact]
    public void E17_PhilippineScripts_VowelSignsAboveAndConsonantLigatures()
    {
        int tka = E12G(0x1760, 0x1763), ti = E12G(0x1760, 0x1772);
        var tagbanwa = E17Block(0x1760, 0x20, null, E17Layout("tagb", [("abvm", [0])], E17Attach(4, ti, tka, (0, 0), (380, 1450))));
        Assert.Equal((ti, 380.0, 1450.0), E17SamePositions(tagbanwa, "\u1763\u1772")[1]);
        foreach (var (block, tag, first, last, i, u) in new[] { (0x1720, "hano", 0x1723, 0x1731, 0x1732, 0x1733), (0x1740, "buhd", 0x1743, 0x1751, 0x1752, 0x1753) })
        {
            var consonants = Enumerable.Range(first, last - first + 1).ToArray();
            var gsub = E17Layout(tag, [("psts", [0]), ("blws", [1])],
                E12Lig([.. consonants.Select((c, k) => (new[] { E12G(block, c), E12G(block, i) }, E12F(k)))]),
                E12Lig([.. consonants.Select((c, k) => (new[] { E12G(block, c), E12G(block, u) }, E12F(50 + k)))]));
            var font = E17Block(block, 0x20, gsub);
            for (var k = 0; k < consonants.Length; k++)
            {
                E17Expect(font, char.ConvertFromUtf32(consonants[k]) + char.ConvertFromUtf32(i), E12F(k));
                E17Expect(font, char.ConvertFromUtf32(consonants[k]) + char.ConvertFromUtf32(u), E12F(50 + k));
            }
        }
    }

    /// <summary>
    /// UNI 17.2.1 sentences 13–16: Buginese has vowel signs like the other Brahmi scripts — the vowel sign E U+1A19, standing left, is drawn before
    /// Ka (it stays after Ka in the text). Before, it followed Ka.
    /// </summary>
    [Fact]
    public void E17_Buginese_VowelSignEBeforeItsConsonant()
    {
        var font = E17Block(0x1A00, 0x20, null);
        E17Expect(font, "\u1A00\u1A19", E12G(0x1A00, 0x1A19), E12G(0x1A00, 0x1A00));
        Assert.Equal("\u1A00\u1A19", E12Shape(font, "\u1A00\u1A19")[0].Text);
    }

    private const int Balinese = 0x1B00;

    private static int Bl(int cp) => E12G(Balinese, cp);

    /// <summary>
    /// UNI 17.3.1 sentences 6–48: adeg adeg U+1B44 kills the inherent vowel and the next consonant takes its below form — every consonant
    /// U+1B13–1B33 after Ka and adeg adeg (blwf of the font; Ya, Ra and Wa by pstf), one cluster; akara U+1B05 carries adeg adeg like a consonant;
    /// taling U+1B3E is drawn before its consonant. Before, blwf and pstf were not applied and taling followed Ka.
    /// </summary>
    [Fact]
    public void E17_Balinese_AdegAdegBelowFormsTalingAkara()
    {
        int adeg = Bl(0x1B44), ka = Bl(0x1B13);
        int[] post = [0x1B2C, 0x1B2D, 0x1B2F];
        var consonants = Enumerable.Range(0x1B13, 0x1B33 - 0x1B13 + 1).ToArray();
        var gsub = E17Layout("bali", [("blwf", [0]), ("pstf", [1])],
            E12Lig([.. consonants.Where(c => !post.Contains(c)).Select(c => (new[] { adeg, Bl(c) }, E12F(c - 0x1B13)))]),
            E12Lig([.. post.Select(c => (new[] { adeg, Bl(c) }, E12F(c - 0x1B13)))]));
        var font = E17Block(Balinese, 0x80, gsub);
        foreach (var c in consonants)
            E17Expect(font, "\u1B13\u1B44" + (char)c, ka, E12F(c - 0x1B13));
        Assert.Single(E17Syllables(font, "\u1B13\u1B44\u1B13").Distinct());
        E17Expect(font, "\u1B13\u1B3E", Bl(0x1B3E), ka);
        E17Expect(font, "\u1B05\u1B44\u1B13", Bl(0x1B05), E12F(0));
    }

    /// <summary>
    /// UNI 17.3.1 sentences 54 and 105–114: surang U+1B03 beside ulu U+1B36 above the consonant, where the font's anchors put it (abvm), and ulu
    /// candra on a musical note as the shaping library places it. Before, abvm was not applied.
    /// </summary>
    [Fact]
    public void E17_Balinese_SurangBesideUlu_UluCandraOnANote()
    {
        int ka = Bl(0x1B13);
        var gpos = E17Layout("bali", [("abvm", [0, 1, 2])],
            E17Attach(4, Bl(0x1B36), ka, (0, 0), (600, 1450)), E17Attach(4, Bl(0x1B03), ka, (0, 0), (150, 1450)),
            E17Attach(4, Bl(0x1B01), Bl(0x1B62), (0, 0), (500, 1500)));
        var font = E17Block(Balinese, 0x80, null, gpos);
        var drawn = E17SamePositions(font, "\u1B13\u1B36\u1B03");
        Assert.True(drawn[2].X < drawn[1].X);
        E17SamePositions(font, "\u1B62\u1B01");
    }

    /// <summary>
    /// UNI 17.3.1 sentences 58–75: ra repa U+1B0B after Ka and adeg adeg takes its below form (blwf), the vowel sign ra repa U+1B3A sits below Ka
    /// (blwm); suku changes its form below a subjoined consonant (blws in the context of the below form) — up to three consonants. Before, blwf,
    /// blwm and blws were not applied.
    /// </summary>
    [Fact]
    public void E17_Balinese_RaRepaAndSukuForms()
    {
        int adeg = Bl(0x1B44), ka = Bl(0x1B13), sa = Bl(0x1B32), suku = Bl(0x1B38);
        var gsub = E17Layout("bali", [("blwf", [0]), ("blws", [1])],
            E12Lig(([adeg, Bl(0x1B0B)], E12F(0)), ([adeg, Bl(0x1B2C)], E12F(1)), ([adeg, ka], E12F(2)), ([adeg, Bl(0x1B2D)], E12F(3))),
            Ot02Lookup(6, Ot02ChainGlyphs([E12F(1)], [suku], [], (0, 2)), Ot02ChainGlyphs([E12F(3)], [suku], [], (0, 2))),
            E11Sub((suku, E12F(9))));
        var gpos = E17Layout("bali", [("blwm", [0])], E17Attach(4, Bl(0x1B3A), ka, (0, 0), (450, -150)));
        var font = E17Block(Balinese, 0x80, gsub, gpos);
        E17Expect(font, "\u1B13\u1B44\u1B0B", ka, E12F(0));
        Assert.Equal((Bl(0x1B3A), 450.0, -150.0), E17SamePositions(font, "\u1B13\u1B3A")[1]);
        E17Expect(font, "\u1B13\u1B44\u1B2C\u1B38", ka, E12F(1), E12F(9));
        E17Expect(font, "\u1B32\u1B44\u1B13\u1B44\u1B2D\u1B38", sa, E12F(2), E12F(3), E12F(9));
        E17Expect(font, "\u1B13\u1B38", ka, suku);
    }

    private const int Javanese = 0xA980;

    private static int Jv(int cp) => E12G(Javanese, cp);

    /// <summary>
    /// UNI 17.4.1 sentences 6–8 and 18–35: pangkon U+A9C0 makes the next consonant a pasangan (blwf), taling and tarung around Ka are drawn
    /// taling, Ka, tarung with the text as typed, the medial pengkal takes its form at the consonant (pstf) and cakra sits below it (blwm) — unlike
    /// Ka, pangkon, Ya —, and suku changes below a pasangan (blws). Before, none of these features applied and taling followed Ka.
    /// </summary>
    [Fact]
    public void E17_Javanese_PasanganTalingTarungMedialsSuku()
    {
        int ka = Jv(0xA98F), pangkon = Jv(0xA9C0), ya = Jv(0xA9AA), suku = Jv(0xA9B8);
        var gsub = E17Layout("java", [("blwf", [0]), ("pstf", [1]), ("blws", [2])],
            E12Lig(([pangkon, ka], E12F(0)), ([pangkon, ya], E12F(1))), E11Sub((Jv(0xA9BE), E12F(2))),
            Ot02Lookup(6, Ot02ChainGlyphs([E12F(1)], [suku], [], (0, 3))), E11Sub((suku, E12F(3))));
        var gpos = E17Layout("java", [("blwm", [0])], E17Attach(4, Jv(0xA9BF), ka, (0, 0), (300, -100)));
        var font = E17Block(Javanese, 0x60, gsub, gpos);
        E17Expect(font, "\uA98F\uA9C0\uA98F", ka, E12F(0));
        E17Expect(font, "\uA98F\uA9BA\uA9B4", Jv(0xA9BA), ka, Jv(0xA9B4));
        Assert.Equal("\uA98F\uA9BA\uA9B4", string.Concat(E12Shape(font, "\uA98F\uA9BA\uA9B4").Select(g => g.Text)));
        E17Expect(font, "\uA98F\uA9BE", ka, E12F(2));
        Assert.Equal((Jv(0xA9BF), 300.0, -100.0), E17SamePositions(font, "\uA98F\uA9BF")[1]);
        E17Expect(font, "\uA98F\uA9C0\uA9AA\uA9B8", ka, E12F(1), E12F(3));
    }

    /// <summary>
    /// UNI 17.5.1 sentence 13: a Rejang syllable — consonant, vowel sign, final sign — is one cluster; a vowel sign without a consonant gets the
    /// dotted circle. Before, there were no clusters and no dotted circle.
    /// </summary>
    [Fact]
    public void E17_Rejang_SyllableOneCluster_LoneVowelSignBroken()
    {
        var font = E17Block(0xA930, 0x30, null);
        Assert.Single(E17Syllables(font, "\uA930\uA947\uA94F").Distinct());
        Assert.NotEqual(0, E17Syllables(font, "\uA930\uA947\uA94F")[0]);
        E17Expect(font, "\uA947", E12Dotted, E12G(0xA930, 0xA947));
    }

    private const int Batak = 0x1BC0;

    private static int Bt(int cp) => E12G(Batak, cp);

    /// <summary>
    /// UNI 17.6.1 sentences 13–22: Ta, I, Pa, pangolat — the killer (U+1BF2, Reordering_Killer since Unicode 16.0) ends the cluster of the
    /// killed consonant; the font draws the vowel sign at it with its presentation lookups, which see across clusters (pres), the killer stays
    /// last and the text comes in the order typed; pangolat without a consonant gets the dotted circle; ee and ng sit above the consonant where
    /// the font's anchors put them (abvm), u fuses with it (psts). Before, there were no clusters, no dotted circle and pres, psts and abvm were
    /// not applied.
    /// </summary>
    [Fact]
    public void E17_Batak_KillerEndsTheClusterPresAcrossClustersSigns()
    {
        int ta = Bt(0x1BD6), i = Bt(0x1BEA), pa = Bt(0x1BC7), pangolat = Bt(0x1BF2), ka = Bt(0x1BC2);
        var gsub = E17Layout("batk", [("pres", [0]), ("psts", [1])], E12Lig(([i, pa], E12F(0))), E12Lig(([ka, Bt(0x1BEE)], E12F(1))));
        var gpos = E17Layout("batk", [("abvm", [0, 1])],
            E17Attach(4, Bt(0x1BE9), ka, (0, 0), (200, 1500)), E17Attach(4, Bt(0x1BF0), ka, (0, 0), (700, 1500)));
        var font = E17Block(Batak, 0x40, gsub, gpos);
        const string tip = "\u1BD6\u1BEA\u1BC7\u1BF2";
        var shaped = E12Shape(font, tip);
        Assert.Equal([ta, E12F(0), pangolat], shaped.Select(g => g.Glyph));
        Assert.Equal(tip, string.Concat(shaped.Select(g => g.Text)));
        Assert.Equal([E12Dotted, pangolat], E12Own(font, "\u1BF2"));
        E17Expect(font, "\u1BC2\u1BEE", E12F(1));
        var signs = E17SamePositions(font, "\u1BC2\u1BE9\u1BF0");
        Assert.True(signs[1].X < signs[2].X);
    }

    private const int Sundanese = 0x1B80;

    private static int Sd(int cp) => E12G(Sundanese, cp);

    /// <summary>
    /// UNI 17.7.1 sentences 18–23 and 49–61: the Old Sundanese virama U+1BAB makes a conjunct (blwf, no glyph of its own), the medials pamingkal
    /// and panyakra take their forms (pstf) or sit below (blwm); Ka, panyakra, panghulu, panyecek are one cluster, a medial after the vowel sign
    /// starts a broken one; panaelaeng U+1BA6 is drawn before Ka with the text as typed; several signs above sit side by side where the font's
    /// anchors put them (abvm). Before, these features were not applied, panaelaeng followed Ka and there were no clusters.
    /// </summary>
    [Fact]
    public void E17_Sundanese_ConjunctsMedialsOrderAndSigns()
    {
        int ka = Sd(0x1B8A);
        var gsub = E17Layout("sund", [("blwf", [0]), ("pstf", [1])], E12Lig(([Sd(0x1BAB), ka], E12F(0))), E11Sub((Sd(0x1BA1), E12F(1))));
        var gpos = E17Layout("sund", [("blwm", [0]), ("abvm", [1, 2])],
            E17Attach(4, Sd(0x1BA2), ka, (0, 0), (400, -100)), E17Attach(4, Sd(0x1BA4), ka, (0, 0), (250, 1450)),
            E17Attach(4, Sd(0x1B81), ka, (0, 0), (650, 1450)));
        var font = E17Block(Sundanese, 0x40, gsub, gpos);
        E17Expect(font, "\u1B8A\u1BAB\u1B8A", ka, E12F(0));
        E17Expect(font, "\u1B8A\u1BA1", ka, E12F(1));
        Assert.Equal((Sd(0x1BA2), 400.0, -100.0), E17SamePositions(font, "\u1B8A\u1BA2")[1]);
        Assert.Single(E17Syllables(font, "\u1B8A\u1BA2\u1BA4\u1B80").Distinct());
        E17Expect(font, "\u1B8A\u1BA4\u1BA2", ka, Sd(0x1BA4), E12Dotted, Sd(0x1BA2));
        E17Expect(font, "\u1B8A\u1BA6", Sd(0x1BA6), ka);
        Assert.Equal("\u1B8A\u1BA6", E12Shape(font, "\u1B8A\u1BA6")[0].Text);
        var signs = E17SamePositions(font, "\u1B8A\u1BA4\u1B81");
        Assert.True(signs[1].X < signs[2].X);
        E17Expect(font, "\u1B8A\u1BAA\u1B80", ka, Sd(0x1BAA), Sd(0x1B80));
    }

    private const int Makasar = 0x11EE0;

    private static int Mk(int cp) => E12G(Makasar, cp);

    /// <summary>
    /// UNI 17.8.1 sentences 9–21: the vowel sign E (standing left) is drawn before Ka, I and U sit above and below (abvm, blwm); two vowel signs
    /// U on Da and a vowel sign on angka U+11EF2 (which repeats the consonant before it) take no dotted circle. Before, E followed Ka and abvm,
    /// blwm were not applied.
    /// </summary>
    [Fact]
    public void E17_Makasar_VowelSignsAndAngka()
    {
        int ka = Mk(0x11EE0), da = Mk(0x11EE7), angka = Mk(0x11EF2);
        var gpos = E17Layout("maka", [("abvm", [0, 1]), ("blwm", [2, 3]), ("mkmk", [4])],
            E17Attach(4, Mk(0x11EF3), ka, (0, 0), (300, 1400)), E17Attach(4, Mk(0x11EF3), angka, (0, 0), (350, 1400)),
            E17Attach(4, Mk(0x11EF4), ka, (0, 0), (300, -100)), E17Attach(4, Mk(0x11EF4), da, (0, 0), (300, -100)),
            E17Attach(6, Mk(0x11EF4), Mk(0x11EF4), (0, 0), (0, -200)));
        var font = E17Block(Makasar, 0x20, null, gpos);
        E17Expect(font, "\U00011EE0\U00011EF5", Mk(0x11EF5), ka);
        Assert.Equal((Mk(0x11EF3), 300.0, 1400.0), E17SamePositions(font, "\U00011EE0\U00011EF3")[1]);
        Assert.Equal((Mk(0x11EF4), 300.0, -100.0), E17SamePositions(font, "\U00011EE0\U00011EF4")[1]);
        var twice = E17SamePositions(font, "\U00011EE7\U00011EF4\U00011EF4");
        Assert.Equal([da, Mk(0x11EF4), Mk(0x11EF4)], twice.Select(d => d.Glyph));
        var onAngka = E17SamePositions(font, "\U00011EE5\U00011EF2\U00011EF3");
        Assert.Equal([Mk(0x11EE5), angka, Mk(0x11EF3)], onAngka.Select(d => d.Glyph));
    }

    private const int Kawi = 0x11F00;

    private static int Kw(int cp) => E12G(Kawi, cp);

    /// <summary>
    /// UNI 17.9.1 sentences 6–53 and 67–71: the joiner U+11F42 before a consonant makes its conjunct form — every consonant U+11F12–11F32 and
    /// the syllabic r and l after Ka and the joiner (blwf of the font; Ra also by pref, drawn before the base), the joiner without a glyph of its
    /// own; four consonants with three joiners are one cluster; the repha U+11F02 typed before its consonant is drawn after it. Before, blwf and
    /// pref were not applied, the repha stayed first and there were no clusters.
    /// </summary>
    [Fact]
    public void E17_Kawi_ConjunctFormsRephaAndClusters()
    {
        int ka = Kw(0x11F12), joiner = Kw(0x11F42), ra = Kw(0x11F2C);
        var consonants = Enumerable.Range(0x11F12, 0x11F32 - 0x11F12 + 1).Concat([0x11F0A, 0x11F0C]).ToArray();
        var gsub = E17Layout("kawi", [("pref", [0]), ("blwf", [1])],
            E12Lig(([joiner, ra], E12F(99))),
            E12Lig([.. consonants.Where(c => c != 0x11F2C).Select(c => (new[] { joiner, Kw(c) }, E12F(c >= 0x11F12 ? c - 0x11F12 : 40 + c - 0x11F0A)))]));
        var font = E17Block(Kawi, 0x60, gsub);
        foreach (var c in consonants.Where(c => c != 0x11F2C))
            E17Expect(font, "\U00011F12\U00011F42" + char.ConvertFromUtf32(c), ka, E12F(c >= 0x11F12 ? c - 0x11F12 : 40 + c - 0x11F0A));
        E17Expect(font, "\U00011F12\U00011F42\U00011F2C", E12F(99), ka);
        E17Expect(font, "\U00011F02\U00011F12", ka, Kw(0x11F02));
        const string four = "\U00011F12\U00011F42\U00011F12\U00011F42\U00011F12\U00011F42\U00011F12";
        Assert.Single(E17Syllables(font, four).Distinct());
    }

    /// <summary>
    /// UNI 17.9.1 sentences 48–53, 72–84 and 92: Ra, joiner, Wa — Ra with the conjunct form of Wa; repha and Wa — the repha after Wa; jnya
    /// U+11F33 and the digit two U+11F52 (for ro) carry vowel signs (abvm, no dotted circle); E and Aa around a consonant (o) are drawn E, Ka, Aa
    /// with the text as typed, two E both before Pa, I and Aa on Pa, I and U together on La. Before, the vowel signs stayed in text order and abvm
    /// was not applied.
    /// </summary>
    [Fact]
    public void E17_Kawi_VowelSignsAroundTheConsonant()
    {
        int ka = Kw(0x11F12), joiner = Kw(0x11F42), wa = Kw(0x11F2F), i = Kw(0x11F36), e = Kw(0x11F3E), aa = Kw(0x11F34), pa = Kw(0x11F26);
        var gsub = E17Layout("kawi", [("blwf", [0])], E12Lig(([joiner, wa], E12F(0))));
        var gpos = E17Layout("kawi", [("abvm", [0, 1, 2])],
            E17Attach(4, i, Kw(0x11F33), (0, 0), (300, 1400)), E17Attach(4, i, Kw(0x11F52), (0, 0), (320, 1400)), E17Attach(4, i, pa, (0, 0), (340, 1400)));
        var font = E17Block(Kawi, 0x60, gsub, gpos);
        E17Expect(font, "\U00011F2C\U00011F42\U00011F2F", Kw(0x11F2C), E12F(0));
        E17Expect(font, "\U00011F02\U00011F2F", wa, Kw(0x11F02));
        Assert.Equal((i, 300.0, 1400.0), E17SamePositions(font, "\U00011F33\U00011F36")[1]);
        Assert.Equal((i, 320.0, 1400.0), E17SamePositions(font, "\U00011F52\U00011F36")[1]);
        E17Expect(font, "\U00011F12\U00011F3E\U00011F34", e, ka, aa);
        Assert.Equal("\U00011F12\U00011F3E\U00011F34", string.Concat(E12Shape(font, "\U00011F12\U00011F3E\U00011F34").Select(g => g.Text)));
        E17Expect(font, "\U00011F26\U00011F3E\U00011F3E", e, e, pa);
        E17SamePositions(font, "\U00011F26\U00011F36\U00011F34");
        E17Expect(font, "\U00011F2D\U00011F36\U00011F38", Kw(0x11F2D), i, Kw(0x11F38));
    }

    // ------------------------------------------------------------ Miao, Khitan Small Script (uni-16)

    private const int Miao = 0x16F00;

    private static int Mi(int cp) => E12G(Miao, cp);

    /// <summary>
    /// UNI 18.10.1 sentences 16–27: Miao is shaped by the universal model — the consonant modifier bar U+16F4F belongs to its initial (one
    /// cluster; the font's pres form and mark anchor apply), a vowel before a tone shifter U+16F8F takes the raised form of the font's pres
    /// context; glyphs and positions as with the shaping library. Before, Miao had no clusters (a lone vowel no dotted circle) and pres was not
    /// applied.
    /// </summary>
    [Fact]
    public void E17_Miao_ModifierBarAndToneShifter()
    {
        int initial = Mi(0x16F0E), bar = Mi(0x16F4F), vowel = Mi(0x16F54), shifter = Mi(0x16F8F);
        var gsub = E17Layout("plrd", [("pres", [0, 1])], E11Sub((bar, E12F(0))), Ot02Lookup(6, Ot02ChainGlyphs([], [vowel], [shifter], (0, 2))), E11Sub((vowel, E12F(1))));
        var gpos = E17Layout("plrd", [("mark", [0])], E17Attach(4, E12F(0), initial, (0, 0), (-50, -80)));
        var font = E17Font(gsub, gpos, (Miao, 0x16F9F, E12Base));
        Assert.Equal((E12F(0), -50.0, -80.0), E17SamePositions(font, "\U00016F0E\U00016F4F")[1]);
        Assert.Single(E17Syllables(font, "\U00016F0E\U00016F4F").Distinct());
        E17Expect(font, "\U00016F0E\U00016F54\U00016F8F", initial, E12F(1), shifter);
        E17Expect(font, "\U00016F54", E12Dotted, vowel);
    }

    /// <summary>
    /// UNI 18.12.1 sentences 32–35 and 43: the Khitan Small Script — every character is a cluster of its own, the font assembles the bundles with
    /// its presentation lookups: type A without, type B with the filler U+16FE4 after the first character, U+18CFF taking part like the others;
    /// glyphs as with the shaping library (which applies pres across the characters, ccmp only within one). Before, ccmp joined characters the
    /// reference shaper keeps apart.
    /// </summary>
    [Fact]
    public void E17_KhitanSmallScript_BundlesFromThePresentationLookups()
    {
        static int K(int cp) => cp >= 0x18B00 ? E12Base + cp - 0x18B00 : 700 + cp - 0x16FE0;
        var gsub = E17Layout("kits", [("pres", [0]), ("ccmp", [1])],
            E12Lig(([K(0x18B01), K(0x18B02), K(0x18B03)], E12F(0)), ([K(0x18B01), K(0x16FE4), K(0x18B02), K(0x18B03)], E12F(1)),
                ([K(0x18B01), K(0x18CFF)], E12F(2))),
            E12Lig(([K(0x18B05), K(0x18B06)], E12F(3))));
        var font = E17Font(gsub, null, (0x18B00, 0x18CFF, E12Base), (0x16FE0, 0x16FFF, 700));
        E17Expect(font, "\U00018B01\U00018B02\U00018B03", E12F(0));
        E17Expect(font, "\U00018B01\U00016FE4\U00018B02\U00018B03", E12F(1));
        Assert.Equal([E12F(2)], E12Own(font, "\U00018B01\U00018CFF"));
        E17Expect(font, "\U00018B05\U00018B06", K(0x18B05), K(0x18B06));
    }

    // ------------------------------------------------------------ the model as a whole

    /// <summary>
    /// The choice of the model: a font whose GSUB offers its features only under DFLT or latn (made for the default shaping) keeps the default
    /// shaping — no dotted circle, no reordering —, a font with the script's tag (or one of another script only) takes the model, as with the
    /// shaping library.
    /// </summary>
    [Fact]
    public void E17_FontMadeForTheDefaultShaping_KeepsIt()
    {
        var keep = E11Sub((Bl(0x1B05), Bl(0x1B05)));
        foreach (var (tag, model) in new[] { ("DFLT", false), ("latn", false), ("bali", true), ("cyrl", true) })
        {
            var font = E17Block(Balinese, 0x80, E11Layout(tag, ("ccmp", keep)));
            E17Expect(font, "\u1B36", model ? [E12Dotted, Bl(0x1B36)] : [Bl(0x1B36)]);
            E17Expect(font, "\u1B13\u1B3E", model ? [Bl(0x1B3E), Bl(0x1B13)] : [Bl(0x1B13), Bl(0x1B3E)]);
        }
    }

    /// <summary>
    /// isol, init, medi and fina in the universal model by the place of each cluster in its word (a cluster after a cluster joins it), as with the
    /// shaping library. Before, the four features were not applied outside the cursive scripts.
    /// </summary>
    [Fact]
    public void E17_TopographicalFormsByThePlaceOfTheCluster()
    {
        int ka = Bl(0x1B13);
        var font = E17Block(Balinese, 0x80, E11Layout("bali", ("isol", E11Sub((ka, E12F(4)))), ("init", E11Sub((ka, E12F(5)))), ("medi", E11Sub((ka, E12F(7)))),
            ("fina", E11Sub((ka, E12F(6))))));
        foreach (var text in new[] { "\u1B13", "\u1B13\u1B13", "\u1B13\u1B13\u1B13", "\u1B13 \u1B13\u1B13", "\u1B13\u1B36\u1B13" })
            Assert.Equal(E12Library(font, text), E12Own(font, text));
        Assert.Equal([E12F(4)], E12Own(font, "\u1B13"));
        Assert.Equal([E12F(5), E12F(7), E12F(6)], E12Own(font, "\u1B13\u1B13\u1B13"));
    }

    /// <summary>
    /// The limits of the model (as docs/FEHLERGEDAECHTNIS.md, No. 1137): a cluster of more than 64 glyphs is cut, ZWJ passed over by the grammar
    /// in any number, many broken clusters each with its dotted circle, a long text in a font with rphf and pref — all shaped in bounded time
    /// without recursion.
    /// </summary>
    [Fact]
    public void E17_Limits_LongClustersAndManyBrokenOnes()
    {
        var gsub = E17Layout("bali", [("rphf", [0]), ("pref", [1])], E12Lig(([Bl(0x1B2D), Bl(0x1B44)], E12F(1))), E12Lig(([Bl(0x1B44), Bl(0x1B2D)], E12F(2))));
        var font = E17Block(Balinese, 0x80, gsub);
        var watch = Stopwatch.StartNew();
        var marks = E12Shape(font, "\u1B13" + new string('\u1B36', 5000));
        Assert.True(marks.Length >= 5001);
        Assert.True(marks.Select(g => g.Syllable).Distinct().Count() > 5000 / 64);
        var joiners = E12Shape(font, "\u1B13" + new string('\u200D', 20000) + "\u1B3E");
        Assert.Equal(20002, joiners.Count(g => g.Glyph != E12Dotted));
        var broken = E12Shape(font, new string('\u1B44', 20000));
        Assert.Equal(20000, broken.Count(g => g.Glyph == E12Dotted));
        var sb = new StringBuilder();
        for (var k = 0; k < 4000; k++) sb.Append("\u1B2D\u1B44\u1B13\u1B44\u1B2D\u1B3E ");
        E12Shape(font, sb.ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    /// <summary>
    /// Measurement against the shaping library: random clusters of the characters with syllabic classes of each script of the model the library
    /// knows (400 each, at most six characters with ZWJ and ZWNJ among them) in a font without GSUB — the same glyphs (dotted circles and
    /// reordering included). Left out are the characters whose classes changed with Unicode 16.0 (the Batak killers, Kawi U+11F5A, Tai Tham
    /// U+1A7B and U+1A7F), the forbidden vowel sequences of Modi and Khojki (a dotted circle the library adds to an independent
    /// vowel with a vowel sign) and joiners in right-to-left Kharoshthi (the library reports them in visual order).
    /// </summary>
    [Fact]
    public void E17_RandomClusters_SameGlyphsAsTheShapingLibrary()
    {
        string[] scripts =
        [
            "ahom", "bali", "batk", "bhks", "brah", "bugi", "buhd", "cakm", "cham", "diak", "dogr", "gong", "gonm", "gran", "hano", "java",
            "kali", "kawi", "khar", "khoj", "kthi", "lana", "lepc", "limb", "mahj", "maka", "marc", "modi", "mtei", "mult", "nand", "newa",
            "rjng", "saur", "shrd", "sidd", "sind", "soyo", "sund", "sylo", "tagb", "takr", "tale", "tavt", "tglg", "tirh", "zanb",
        ];
        int[] changed = [0x1BF2, 0x1BF3, 0x11F5A, 0x1A7B, 0x1A7F];
        var failures = new List<string>();
        var total = 0;
        foreach (var tag in scripts)
        {
            var iso = char.ToUpperInvariant(tag[0]) + tag[1..];
            var points = Enumerable.Range(0x80, 0x20000)
                .Where(c => UnicodeScripts.Script(c) == iso && SyllableData.SyllabicOf(c) != SyllabicCategory.Other && !changed.Contains(c)).ToArray();
            var font = E17Font(null, null, [.. points.Select((c, k) => (c, c, E12Base + k))]);
            var random = new Random(tag.Sum(ch => ch) * 7919);
            for (var s = 0; s < 400; s++)
            {
                var sb = new StringBuilder();
                var length = random.Next(1, 7);
                for (var k = 0; k < length; k++)
                {
                    var r = random.Next(20);
                    sb.Append(r == 0 && tag != "khar" ? "\u200D" : r == 1 && tag != "khar" ? "\u200C" : char.ConvertFromUtf32(points[random.Next(points.Length)]));
                }
                var text = sb.ToString();
                if (tag is "modi" or "khoj" && E17ForbiddenVowelSequence(text)) continue;
                var library = E12Library(font, text);
                if (tag == "khar") Array.Reverse(library);
                total++;
                var own = E12Own(font, text);
                if (!own.SequenceEqual(library) && failures.Count < 20)
                    failures.Add($"{tag} {string.Join(" ", text.EnumerateRunes().Select(r => r.Value.ToString("X4")))}: {string.Join(" ", own)} / {string.Join(" ", library)}");
            }
        }
        Assert.True(total > 15_000);
        Assert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    /// <summary>An independent vowel followed by a vowel sign in Modi or Khojki (the library forbids some of these sequences).</summary>
    private static bool E17ForbiddenVowelSequence(string text)
    {
        var points = text.EnumerateRunes().Select(r => r.Value).Where(c => c is not (0x200C or 0x200D)).ToArray();
        for (var k = 0; k + 1 < points.Length; k++)
            if (SyllableData.SyllabicOf(points[k]) == SyllabicCategory.VowelIndependent && SyllableData.SyllabicOf(points[k + 1]) == SyllabicCategory.VowelDependent)
                return true;
        return false;
    }
}
