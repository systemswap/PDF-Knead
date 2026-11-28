using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;
using JoinForm = PdfKnead.Core.Fonts.Layout.ArabicJoining.Form;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package E19 (docs/FEHLERGEDAECHTNIS.md, No. 1183): the rows of the drafts uni-08 (Devanagari), uni-09 (the other Indic scripts of chapter
/// 12), uni-10 (Thaana, Sinhala, Tibetan, Mongolian), uni-12 (Thai, Lao, Myanmar, Khmer), uni-16 (kana, Hangul) and uni-17 (chapter 23) that
/// concern shaping, tested with the fonts of <see cref="E19Font"/> and, where it applies, against the bundled shaping library.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers

    /// <summary>Asserts the glyphs drawn (joiners and glyphs drawn as nothing left out) and that the shaping library gives the same.</summary>
    private static void E19Expect(byte[] font, string text, params int[] expected)
    {
        Assert.Equal(expected, E19Drawn(font, text));
        Assert.Equal(expected, E19Library(font, text));
    }

    /// <summary>Asserts one syllable without a dotted circle, the text read back as typed.</summary>
    private static void E19OneSyllable(byte[] font, string text)
    {
        var shaped = E19Shape(font, text);
        Assert.DoesNotContain(shaped, g => g.Glyph == E19Dotted);
        Assert.Single(shaped.Select(g => g.Syllable).Distinct());
        Assert.Equal(text, E19Text(shaped));
    }

    /// <summary>Asserts a dotted circle (one) and the text read back as typed.</summary>
    private static void E19Circle(byte[] font, string text)
    {
        var shaped = E19Shape(font, text);
        Assert.Single(shaped, g => g.Glyph == E19Dotted);
        Assert.Equal(text, E19Text(shaped));
        Assert.Equal(E19Library(font, text), E19Drawn(font, text));
    }

    /// <summary>A layout of one script with lookups of the features given and further lookups after them (called by contexts).</summary>
    private static byte[] E19Layout(string script, (string Feature, Action<Ot02Bytes> Lookup)[] entries, params Action<Ot02Bytes>[] extra) =>
        Ot49Layout([script], entries, extra);

    /// <summary>A mark-to-mark lookup (type 6, format 1) for one mark on one mark, one anchor each.</summary>
    private static Action<Ot02Bytes> E19MarkToMark(int mark, int mark2, (int X, int Y) markAnchor, (int X, int Y) mark2Anchor) => Ot02Lookup(6, b =>
    {
        var start = b.Here;
        string mc = b.NewLabel(), bc = b.NewLabel(), ma = b.NewLabel(), ba = b.NewLabel(), mAnchor = b.NewLabel(), bAnchor = b.NewLabel();
        b.U16(1).Off16(mc, start).Off16(bc, start).U16(1).Off16(ma, start).Off16(ba, start);
        b.Label(mc);
        Ot02Coverage(b, [mark]);
        b.Label(bc);
        Ot02Coverage(b, [mark2]);
        b.Label(ma);
        var markArray = b.Here;
        b.U16(1).U16(0).Off16(mAnchor, markArray);
        b.Label(mAnchor).U16(1).U16(markAnchor.X).U16(markAnchor.Y);
        b.Label(ba);
        var baseArray = b.Here;
        b.U16(1).Off16(bAnchor, baseArray);
        b.Label(bAnchor).U16(1).U16(mark2Anchor.X).U16(mark2Anchor.Y);
    });

    /// <summary>Own text saved on a blank page: what the extraction reads beyond the text of the blank page.</summary>
    private static string E19PageText(string text)
    {
        var page = UniTestsPageText(UniTestsSaved(UniTestsElement(text))).Trim('\r', '\n');
        Assert.StartsWith(Uni04BlankText, page, StringComparison.Ordinal);
        return page[Uni04BlankText.Length..].Trim('\r', '\n');
    }

    private const int Dv = 0x0900;

    private static int Dg(int cp) => E19G(Dv, cp);

    private static (string, Action<Ot02Bytes>) E19Rphf(int block, int ra, int virama, int form) => ("rphf", E12Lig(([E19G(block, ra), E19G(block, virama)], form)));

    // ------------------------------------------------------------ uni-08: Devanagari

    /// <summary>
    /// UNI 12.1.2 sentence 34 and 12.1.5 sentences 43–48: the historic prishthamatra E U+094E stands left — „कU+094E“ gives U+094E before Ka, „कU+094E\u0947“
    /// and „कU+094EU+093E“ keep the other sign after Ka; the text read back as typed, the same glyphs as the shaping library.
    /// </summary>
    [Fact]
    public void Uni08_PrishthamatraE_DrawnBeforeTheBase()
    {
        var font = E19Indic(Dv);
        E19Expect(font, "क\u094E", Dg(0x094E), Dg(0x0915));
        E19Expect(font, "क\u094E\u0947", Dg(0x094E), Dg(0x0915), Dg(0x0947));
        E19Expect(font, "क\u094E\u093E", Dg(0x094E), Dg(0x0915), Dg(0x093E));
        Assert.Equal("क\u094E\u0947", E19Text(E19Shape(font, "क\u094E\u0947")));
    }

    /// <summary>UNI 12.1.2 sentences 38–56 (table 12-1): „अU+093E“ and „ए\u0947“ get a dotted circle before the vowel sign, „आ“ none.</summary>
    [Fact]
    public void Uni08_DoNotUseVowelSequences_DottedCircle()
    {
        var font = E19Indic(Dv);
        E19Circle(font, "अ\u093E");
        E19Circle(font, "ए\u0947");
        Assert.DoesNotContain(E19Shape(font, "आ"), g => g.Glyph == E19Dotted);
    }

    /// <summary>
    /// UNI 12.1.2 sentences 68–71: a vowel sign after a dead consonant has no base — „क\u094DU+093F“ gives Ka, virama, then I before a dotted circle; a vowel
    /// letter after it („क\u094Dअ“) is a syllable of its own without a circle; the text read back as typed.
    /// </summary>
    [Fact]
    public void Uni08_DeadConsonantThenVowelSign_DottedCircle()
    {
        var font = E19Indic(Dv);
        E19Expect(font, "क\u094D\u093F", Dg(0x0915), Dg(0x094D), Dg(0x093F), E19Dotted);
        E19Expect(font, "क\u094Dअ", Dg(0x0915), Dg(0x094D), Dg(0x0905));
        Assert.Equal("क\u094D\u093F", E19Text(E19Shape(font, "क\u094D\u093F")));
    }

    /// <summary>
    /// UNI 12.1.2 sentences 76–95, 96–110, 111–112, 134–147 and 12.1.5 sentence 3: the discouraged spellings of the dead-consonant forms are
    /// taken as typed — no conversion into the atomic character, the shaped text and the characters of the saved page exactly as typed. Where
    /// the vowel sign after the dead consonant gets a dotted circle (no base, as the shaping library), the page text of today reads a space at
    /// the circle (a glyph with an advance and without a text; finding of docs/FEHLERGEDAECHTNIS.md, No. 1183, for the text extraction).
    /// </summary>
    [Fact]
    public void Uni08_DiscouragedSequences_KeptAsTyped()
    {
        var font = E19Indic(Dv);
        foreach (var text in new[] { "ख\u094D\u093E", "ख\u094D\u200D\u093E", "क\u094Dष\u094D\u093E", "ज़\u094D\u093E",
                     "ज\u093C\u094D\u093E", "ग\u093C\u0952" })
        {
            Assert.Equal(text, E19Text(E19Shape(font, text)));
            var circle = E19Shape(font, text).Any(g => g.Glyph == E19Dotted);
            Assert.Equal(text, circle ? E19PageText(text).Replace(" ", "") : E19PageText(text));
        }
    }

    /// <summary>
    /// UNI 12.1.2 sentence 191: in a font without half forms Ka, virama, ZWJ, Ssa keeps the visible virama (Ka, virama, Ssa drawn, the ZWJ
    /// without width); a ZWJ the font has no glyph for is drawn as nothing.
    /// </summary>
    [Fact]
    public void Uni08_ZwjWithoutHalfForms_VisibleVirama()
    {
        var font = E19Indic(Dv);
        E19Expect(font, "क\u094D\u200Dष", Dg(0x0915), Dg(0x094D), Dg(0x0937));
        var noJoiner = ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
        {
            ["cmap"] = E11Cmap((' ', ' ', Ot02G(' ')), (Dv, Dv + 0x7F, E19Base)), ["GSUB"] = E11Layout("dev2", ("nukt", E11Sub((Dg(0x0905), Dg(0x0905))))),
            ["GPOS"] = null, ["GDEF"] = null, ["kern"] = null,
        });
        var shaped = E19Shape(noJoiner, "क\u094D\u200Dष");
        Assert.Equal([Dg(0x0915), Dg(0x094D), Ot02G(' '), Dg(0x0937)], shaped.Select(g => g.Glyph));
        Assert.Equal(0, shaped[2].XAdvance);
    }

    /// <summary>UNI 12.1.3 sentences 37–38: a reph on a dead Ra — „र\u094Dर\u094D“ gives Ra, virama, reph; the text read back as typed.</summary>
    [Fact]
    public void Uni08_RephOnDeadRa()
    {
        var font = E19Indic(Dv, E19Rphf(Dv, 0x0930, 0x094D, E19F(1)));
        E19Expect(font, "र\u094Dर\u094D", Dg(0x0930), Dg(0x094D), E19F(1));
        Assert.Equal("र\u094Dर\u094D", E19Text(E19Shape(font, "र\u094Dर\u094D")));
    }

    /// <summary>
    /// UNI 12.1.3 sentences 39–44: the reph goes behind the base and its forms — „र\u094Dज\u094Dञ“ with the conjunct J.NYA gives J.NYA, reph; „र\u094Dग\u094Dघ“ with
    /// the half form of Ga gives the half form, Gha, reph.
    /// </summary>
    [Fact]
    public void Uni08_Reph_OnConjunctAndAfterHalfForm()
    {
        var font = E19Indic(Dv, E19Rphf(Dv, 0x0930, 0x094D, E19F(1)), ("akhn", E12Lig(([Dg(0x091C), Dg(0x094D), Dg(0x091E)], E19F(2)))),
            ("half", E12Lig(([Dg(0x0917), Dg(0x094D)], E19F(3)))));
        E19Expect(font, "र\u094Dज\u094Dञ", E19F(2), E19F(1));
        E19Expect(font, "र\u094Dग\u094Dघ", E19F(3), Dg(0x0918), E19F(1));
    }

    /// <summary>UNI 12.1.3 sentences 45–48: U+0931 never forms a reph; its half form (the eyelash Ra) comes from the font — „ऱ\u094Dक“ gives it, Ka.</summary>
    [Fact]
    public void Uni08_Rra_HalfFormIsEyelash()
    {
        var font = E19Indic(Dv, E19Rphf(Dv, 0x0930, 0x094D, E19F(1)), ("half", E12Lig(([Dg(0x0931), Dg(0x094D)], E19F(4)))));
        E19Expect(font, "ऱ\u094Dक", E19F(4), Dg(0x0915));
    }

    /// <summary>UNI 12.1.3 sentences 49–51: Ra, virama, ZWJ before a consonant gives the eyelash Ra (the half form), without the ZWJ the reph.</summary>
    [Fact]
    public void Uni08_RaVirama_Zwj_Eyelash()
    {
        var font = E19Indic(Dv, E19Rphf(Dv, 0x0930, 0x094D, E19F(1)), ("half", E12Lig(([Dg(0x0930), Dg(0x094D)], E19F(4)))));
        E19Expect(font, "र\u094D\u200Dक", E19F(4), Dg(0x0915));
        E19Expect(font, "र\u094Dक", Dg(0x0915), E19F(1));
    }

    /// <summary>
    /// UNI 12.1.3 sentences 59–64: a dead Ra after a dead consonant takes the Rakar and keeps the virama — „त\u094Dर\u094Dय“ forms the half form of T.RA and
    /// with Ya the conjunct T.R.YA (blwf before half, pres after); „त\u094Dर\u094D“ gives Ta, Rakar, virama.
    /// </summary>
    [Fact]
    public void Uni08_DeadRaAfterDeadConsonant_RakarKeepsVirama()
    {
        var font = E19Indic(Dv, ("blwf", E12Lig(([Dg(0x094D), Dg(0x0930)], E19F(5)))), ("half", E12Lig(([Dg(0x0924), E19F(5), Dg(0x094D)], E19F(6)))),
            ("pres", E12Lig(([E19F(6), Dg(0x092F)], E19F(7)))));
        E19Expect(font, "त\u094Dर\u094Dय", E19F(7));
        E19Expect(font, "त\u094Dर\u094D", Dg(0x0924), E19F(5), Dg(0x094D));
    }

    /// <summary>UNI 12.1.3 sentences 71–72: a vowel sign after the bindu has no base — „क\u0902U+093E“ gives Ka, anusvara, circle, AA; „कU+093E\u0902“ is one syllable.</summary>
    [Fact]
    public void Uni08_BinduBeforeVowelSign_DottedCircle()
    {
        var font = E19Indic(Dv);
        E19Expect(font, "क\u0902\u093E", Dg(0x0915), Dg(0x0902), E19Dotted, Dg(0x093E));
        E19OneSyllable(font, "क\u093E\u0902");
    }

    /// <summary>
    /// UNI 12.1.3 sentences 82–87: each basic form acts on the result of the one before — K.SSA from akhn takes its half form („क\u094Dष\u094Dय“: the half
    /// form of K.SSA, Ya); S.T.RA from the half form of Sa, Ta and the Rakar by pres.
    /// </summary>
    [Fact]
    public void Uni08_ConjunctAsDeadConsonant()
    {
        var font = E19Indic(Dv, ("akhn", E12Lig(([Dg(0x0915), Dg(0x094D), Dg(0x0937)], E19F(8)))), ("blwf", E12Lig(([Dg(0x094D), Dg(0x0930)], E19F(5)))),
            ("half", E12Lig(([E19F(8), Dg(0x094D)], E19F(9)), ([Dg(0x0938), Dg(0x094D)], E19F(10)))), ("pres", E12Lig(([E19F(10), Dg(0x0924), E19F(5)], E19F(11)))));
        E19Expect(font, "क\u094Dष\u094Dय", E19F(9), Dg(0x092F));
        E19Expect(font, "स\u094Dत\u094Dर", E19F(11));
    }

    /// <summary>
    /// UNI 12.1.3 sentences 110–116: Ra at the start of a cluster in three forms — „र\u094Dय“ the reph, „र\u094D\u200Dय“ the eyelash Ra (half form), „र\u200D\u094Dय“ Ra with
    /// the shortened Ya of blwf.
    /// </summary>
    [Fact]
    public void Uni08_ClusterInitialRa_ThreeForms()
    {
        var font = E19Indic(Dv, E19Rphf(Dv, 0x0930, 0x094D, E19F(1)), ("blwf", E12Lig(([Dg(0x094D), Dg(0x092F)], E19F(12)))),
            ("half", E12Lig(([Dg(0x0930), Dg(0x094D)], E19F(4)))));
        E19Expect(font, "र\u094Dय", Dg(0x092F), E19F(1));
        E19Expect(font, "र\u094D\u200Dय", E19F(4), Dg(0x092F));
        E19Expect(font, "र\u200D\u094Dय", Dg(0x0930), E19F(12));
    }

    /// <summary>UNI 12.1.3 sentences 199–201: Ra, virama and the vowel letter vocalic R are drawn tolerantly with the reph on the vowel; the text as typed.</summary>
    [Fact]
    public void Uni08_RaViramaIndependentVowel_KeptAsTyped()
    {
        var font = E19Indic(Dv, E19Rphf(Dv, 0x0930, 0x094D, E19F(1)));
        E19Expect(font, "र\u094Dऋ", Dg(0x090B), E19F(1));
        Assert.Equal("र\u094Dऋ", E19Text(E19Shape(font, "र\u094Dऋ")));
        E19Expect(font, "र\u0943", Dg(0x0930), Dg(0x0943));
    }

    /// <summary>UNI 12.1.3 sentences 202–212: the half form of a conjunct before a ZWJ — „क\u094Dष\u094D\u200D“ gives the half form of K.SSA.</summary>
    [Fact]
    public void Uni08_ConjunctHalfForm_WithZwj()
    {
        var font = E19Indic(Dv, ("akhn", E12Lig(([Dg(0x0915), Dg(0x094D), Dg(0x0937)], E19F(8)))), ("half", E12Lig(([E19F(8), Dg(0x094D)], E19F(9)))));
        E19Expect(font, "क\u094Dष\u094D\u200D", E19F(9));
    }

    /// <summary>
    /// UNI 12.1.3 sentences 213–225: Marathi forms come from locl of the language system MAR — with the language „mr“ JHA and the digit five
    /// take the variants of the font, without a language the default forms.
    /// </summary>
    [Fact]
    public void Uni08_MarathiAllographs_FromLocl()
    {
        var gsub = Ot02Layout([new Ot02Script("dev2", new Ot02Lang("", [0]), new Ot02Lang("MAR ", [0, 1]))],
            [new Ot02Feature("nukt", [0]), new Ot02Feature("locl", [1])], [E11Sub((Dg(0x0905), Dg(0x0905))), E11Sub((Dg(0x091D), E19F(13)), (Dg(0x096B), E19F(14)))]);
        var font = E19Font([(Dv, Dv + 0x7F, E19Base)], gsub);
        var shaper = TextShaper.For(OpenTypeFont.Parse(font));
        Assert.Equal([E19F(13), E19F(14)], shaper.Shape("झ५", null, "mr").Select(g => g.Glyph));
        Assert.Equal([Dg(0x091D), Dg(0x096B)], shaper.Shape("झ५").Select(g => g.Glyph));
    }

    /// <summary>
    /// UNI 12.1.3 sentences 236–237: the grave accent U+0953 has no Indic class — after a Latin letter it stays with it in the Latin run, after Ka
    /// it follows it without a dotted circle; the text as typed.
    /// </summary>
    [Fact]
    public void Uni08_GraveAccent0953_NoIndicClass()
    {
        var font = E19Font([(Dv, Dv + 0x7F, E19Base), ('a', 'a', Ot02G('a'))], E11Layout("dev2", ("nukt", E11Sub((Dg(0x0905), Dg(0x0905))))));
        Assert.Equal([Ot02G('a'), Dg(0x0953)], E19Shape(font, "a\u0953").Select(g => g.Glyph));
        Assert.Single(UnicodeScripts.Itemize([(int)'a', 0x0953]));
        E19Expect(font, "क\u0953", Dg(0x0915), Dg(0x0953));
        Assert.Equal("क\u0953", E19Text(E19Shape(font, "क\u0953")));
    }

    /// <summary>
    /// UNI 12.1.6 sentences 18–20 and 12.1.8 sentences 8–13: Samaveda and Vedic marks belong to the syllable — „कꣳ\uA8EC“ and „क\u1CD8\u1CD5“ are one syllable
    /// without a dotted circle, the marks in the order typed; the same glyphs as the shaping library.
    /// </summary>
    [Fact]
    public void Uni08_VedicMarks_InTheSyllable()
    {
        var font = E19Font([(Dv, Dv + 0x7F, E19Base), (0xA8E0, 0xA8FF, E19Base + 0x80), (0x1CD0, 0x1CFF, E19Base + 0xA0)],
            E11Layout("dev2", ("nukt", E11Sub((Dg(0x0905), Dg(0x0905))))));
        foreach (var (text, marks) in new[] { ("क\uA8E3\uA8EC", new[] { E19Base + 0x83, E19Base + 0x8C }), ("क\u1CD8\u1CD5", [E19Base + 0xA8, E19Base + 0xA5]) })
        {
            E19OneSyllable(font, text);
            E19Expect(font, text, [Dg(0x0915), .. marks]);
        }
    }

    // ------------------------------------------------------------ uni-09: Bengali … Malayalam

    private const int Bn = 0x0980, Gu = 0x0A00, Gj = 0x0A80, Or = 0x0B00, Tm = 0x0B80, Te = 0x0C00, Kn = 0x0C80, Ml = 0x0D00;

    /// <summary>
    /// UNI 12.2.1 sentences 11–17 and 18–25: „অU+09BE“ (U+0985 U+09BE) gets a dotted circle before the vowel sign; the Kokborok vowel letters U+0985
    /// U+09D7 and U+0989 U+09BE are one syllable each without a circle — as the shaping library.
    /// </summary>
    [Fact]
    public void Uni09_Bengali_DoNotUseAndKokborok()
    {
        var font = E19Indic(Bn);
        E19Circle(font, "অ\u09BE");
        E19OneSyllable(font, "অ\u09D7");
        E19OneSyllable(font, "উ\u09BE");
        E19Expect(font, "উ\u09BE", E19G(Bn, 0x0989), E19G(Bn, 0x09BE));
    }

    /// <summary>
    /// UNI 12.2.1 sentences 60–65: a ligature of Ka and U forms in „ক\u09C1“; a ZWNJ between them keeps the three glyphs; a ligature of the font that
    /// names the ZWJ forms in „ক\u200D\u09C1“ (in the stages of the syllable the joiners stay glyphs of their own, as the shaping library keeps them).
    /// </summary>
    [Fact]
    public void Uni09_Bengali_KaU_LigatureWithJoiners()
    {
        int ka = E19G(Bn, 0x0995), u = E19G(Bn, 0x09C1);
        var font = E19Indic(Bn, ("blws", E12Lig(([ka, u], E19F(1)), ([ka, E19Zwnj + 1, u], E19F(2)))));
        E19Expect(font, "ক\u09C1", E19F(1));
        E19Expect(font, "ক\u200C\u09C1", ka, u);
        E19Expect(font, "ক\u200D\u09C1", E19F(2));
    }

    /// <summary>UNI 12.2.1 sentences 73–74: Ra and hasant before khanda ta form the reph on it — „র\u09CDৎ“ gives khanda ta, reph.</summary>
    [Fact]
    public void Uni09_Bengali_RephOnKhandaTa()
    {
        var font = E19Indic(Bn, E19Rphf(Bn, 0x09B0, 0x09CD, E19F(1)));
        E19Expect(font, "র\u09CDৎ", E19G(Bn, 0x09CE), E19F(1));
    }

    /// <summary>
    /// UNI 12.2.1 sentences 91–112: the ya-phalaa of pstf on the vowel letter A („অ\u09CDযU+09BE\u0981“ one syllable: A, ya-phalaa, AA, candrabindu, no circle);
    /// „র\u09CDয“ the reph on Ya, „র\u200D\u09CDয“ Ra with the ya-phalaa, „ক\u200D\u09CDয“ Ka with the ya-phalaa — as the shaping library.
    /// </summary>
    [Fact]
    public void Uni09_Bengali_YaPhalaaAndReph()
    {
        var font = E19Indic(Bn, E19Rphf(Bn, 0x09B0, 0x09CD, E19F(1)), ("pstf", E12Lig(([E19G(Bn, 0x09CD), E19G(Bn, 0x09AF)], E19F(3)))));
        E19OneSyllable(font, "অ\u09CDয\u09BE\u0981");
        E19Expect(font, "অ\u09CDয\u09BE\u0981", E19G(Bn, 0x0985), E19F(3), E19G(Bn, 0x09BE), E19G(Bn, 0x0981));
        E19Expect(font, "র\u09CDয", E19G(Bn, 0x09AF), E19F(1));
        E19Expect(font, "র\u200D\u09CDয", E19G(Bn, 0x09B0), E19F(3));
        E19Expect(font, "ক\u200D\u09CDয", E19G(Bn, 0x0995), E19F(3));
    }

    /// <summary>
    /// UNI 12.2.1 sentences 113–118: U+1CF5 (Consonant_With_Stacker) and Ka are one syllable, so a ligature of the font forms; with a ZWNJ between
    /// them three glyphs and no dotted circle (the shaping library keeps U+1CF5 apart and forms no ligature: here PDF-Knead follows the standard).
    /// </summary>
    [Fact]
    public void Uni09_Bengali_Jihvamuliya_LigatureAndZwnj()
    {
        int sign = E19Base + 0x100 + 0x05, ka = E19G(Bn, 0x0995);
        var font = E19Font([(Bn, Bn + 0x7F, E19Base), (0x1CF0, 0x1CFF, E19Base + 0x100)], E11Layout("bng2", ("pres", E12Lig(([sign, ka], E19F(4))))));
        Assert.Equal([E19F(4)], E19Drawn(font, "ᳵক"));
        Assert.Equal([sign, ka], E19Drawn(font, "ᳵ\u200Cক"));
        Assert.DoesNotContain(E19Shape(font, "ᳵ\u200Cক"), g => g.Glyph == E19Dotted);
    }

    /// <summary>
    /// UNI 12.3.1 sentences 20–32: yakash and udaat before the vowel sign I, the vowel sign U after OO or after the vowel letter OO belong to the
    /// syllable — one syllable each without a circle, Ga, OO, U in the order typed.
    /// </summary>
    [Fact]
    public void Uni09_Gurmukhi_SignsInOneSyllable()
    {
        var font = E19Indic(Gu);
        foreach (var text in new[] { "ਕ\u0A75\u0A3F", "ਕ\u0A51\u0A3F", "ਓ\u0A41", "ਗ\u0A4B\u0A41" })
        {
            E19OneSyllable(font, text);
            Assert.Equal(E19Library(font, text), E19Drawn(font, text));
        }
        E19Expect(font, "ਗ\u0A4B\u0A41", E19G(Gu, 0x0A17), E19G(Gu, 0x0A4B), E19G(Gu, 0x0A41));
    }

    /// <summary>
    /// UNI 12.3.1 sentences 33–50: the bindi before II is one syllable with iri and with Ka (in the order typed); iri with II (the letter II
    /// to use instead) gets a dotted circle.
    /// </summary>
    [Fact]
    public void Uni09_Gurmukhi_BindiBeforeII_IriII()
    {
        var font = E19Indic(Gu);
        E19OneSyllable(font, "ੲ\u0A02\u0A40");
        E19Expect(font, "ੲ\u0A02\u0A40", E19G(Gu, 0x0A72), E19G(Gu, 0x0A02), E19G(Gu, 0x0A40));
        E19OneSyllable(font, "ਕ\u0A02\u0A40");
        E19Circle(font, "ੲ\u0A40");
    }

    /// <summary>UNI 12.4.1 sentences 4–39: „અU+0ABE“ gets a circle; reph and Rakar of the font; U+0AFF and U+0AFE U+0AFA after Jha in one syllable.</summary>
    [Fact]
    public void Uni09_Gujarati_DoNotUseRephRakarAndMarks()
    {
        var plain = E19Indic(Gj);
        E19Circle(plain, "અ\u0ABE");
        foreach (var text in new[] { "ઝ\u0AFF", "ઝ\u0AFE\u0AFA" })
        {
            E19OneSyllable(plain, text);
            Assert.Equal(E19Library(plain, text), E19Drawn(plain, text));
        }
        var font = E19Indic(Gj, E19Rphf(Gj, 0x0AB0, 0x0ACD, E19F(1)), ("blwf", E12Lig(([E19G(Gj, 0x0ACD), E19G(Gj, 0x0AB0)], E19F(2)))));
        E19Expect(font, "ર\u0ACDક", E19G(Gj, 0x0A95), E19F(1));
        E19Expect(font, "ક\u0ACDર", E19G(Gj, 0x0A95), E19F(2));
    }

    /// <summary>UNI 12.5.1 sentences 7–13 and 71–75: „ଏU+0B57“ gets a circle; a ligature of O and candrabindu forms, a ZWNJ keeps three glyphs, no circle.</summary>
    [Fact]
    public void Uni09_Oriya_DoNotUseAndOCandrabindu()
    {
        var plain = E19Indic(Or);
        E19Circle(plain, "ଏ\u0B57");
        int o = E19G(Or, 0x0B13), candra = E19G(Or, 0x0B01);
        var font = E19Indic(Or, ("pres", E12Lig(([o, candra], E19F(3)))));
        E19Expect(font, "ଓ\u0B01", E19F(3));
        E19Expect(font, "ଓ\u200C\u0B01", o, candra);
        Assert.DoesNotContain(E19Shape(font, "ଓ\u200C\u0B01"), g => g.Glyph == E19Dotted);
    }

    /// <summary>UNI 12.5.1 sentences 48–54: the overline after AA stands over it (Ka, AA, overline) — see E19_Oriya_OverlineStaysWithItsVowelSign.</summary>
    [Fact]
    public void Uni09_Oriya_OverlineAfterAa() =>
        E19Expect(E19Indic(Or), "କ\u0B3E\u0B55", E19G(Or, 0x0B15), E19G(Or, 0x0B3E), E19G(Or, 0x0B55));

    /// <summary>
    /// UNI 12.6.2 sentences 1–5 and 30–32: „அU+0BC2“ gets a circle; „க\u0BCDஷU+0BCB“ with akhn gives EE, the Kssa ligature, AA, without akhn Ka, pulli, EE, Ssa, AA.
    /// </summary>
    [Fact]
    public void Uni09_Tamil_DoNotUseAndKssa()
    {
        var plain = E19Indic(Tm);
        E19Circle(plain, "அ\u0BC2");
        int ka = E19G(Tm, 0x0B95), pulli = E19G(Tm, 0x0BCD), ssa = E19G(Tm, 0x0BB7), ee = E19G(Tm, 0x0BC7), aa = E19G(Tm, 0x0BBE);
        E19Expect(plain, "க\u0BCDஷ\u0BCB", ka, pulli, ee, ssa, aa);
        var font = E19Indic(Tm, ("akhn", E12Lig(([ka, pulli, ssa], E19F(5)))));
        E19Expect(font, "க\u0BCDஷ\u0BCB", ee, E19F(5), aa);
    }

    /// <summary>UNI 12.6.3 sentences 36–45: Ra with I keeps the glyphs of the cmap without a lookup; a contextual form of Ra before I comes from the font.</summary>
    [Fact]
    public void Uni09_Tamil_RaI_FormFromTheFont()
    {
        int ra = E19G(Tm, 0x0BB0), i = E19G(Tm, 0x0BBF);
        E19Expect(E19Indic(Tm), "ர\u0BBF", ra, i);
        var font = E19Font([(Tm, Tm + 0x7F, E19Base)], E19Layout("tml2", [("pres", Ot02Lookup(6, Ot02Chain([], [[ra]], [[i]], (0, 1))))], E11Sub((ra, E19F(6)))));
        E19Expect(font, "ர\u0BBF", E19F(6), i);
    }

    /// <summary>UNI 12.6.3 sentences 92–97: the Grantha nukta in Tamil text is in the Tamil run and attaches to Pa (E19_ScriptExtensions_GranthaNuktaInTamilText).</summary>
    [Fact]
    public void Uni09_Tamil_GranthaNukta_InTheTamilRun() => E19_ScriptExtensions_GranthaNuktaInTamilText();

    /// <summary>
    /// UNI 12.7.1 sentences 4–12, 31–40 and 47–48: „ఒ\u0C55“ gets a circle; the Telugu reph is written with ZWJ — „ర\u0C4Dమ“ no reph, „ర\u0C4D\u200Dమ“ Ma with the reph,
    /// „ర\u200D\u0C4Dమ“ no reph; Ka with the anudatta U+0952 is one syllable without a circle.
    /// </summary>
    [Fact]
    public void Uni09_Telugu_DoNotUseRephAndAnudatta()
    {
        var plain = E19Indic(Te);
        E19Circle(plain, "ఒ\u0C55");
        E19OneSyllable(plain, "క\u0952");
        int ra = E19G(Te, 0x0C30), virama = E19G(Te, 0x0C4D), ma = E19G(Te, 0x0C2E);
        var font = E19Indic(Te, ("rphf", E12Lig(([ra, virama, E19Zwnj + 1], E19F(1)), ([ra, virama], E19F(1)))));
        E19Expect(font, "ర\u0C4Dమ", ra, virama, ma);
        E19Expect(font, "ర\u0C4D\u200Dమ", ma, E19F(1));
        Assert.DoesNotContain(E19F(1), E19Drawn(font, "ర\u200D\u0C4Dమ"));
    }

    /// <summary>
    /// UNI 12.8.2 sentences 10–16, 43–46 and 12.8.3 sentences 14–27: „ಉU+0CBE“ gets a circle; Llla below A; reph and Ra below; the ligature of U+0CF1
    /// and Ka (also before I, one syllable); nukta and virama one syllable in either order (canonically equivalent: the nukta goes before the
    /// virama, as the shaping library puts it; before, the order virama, nukta gave the nukta a dotted circle).
    /// </summary>
    [Fact]
    public void Uni09_Kannada_DoNotUseAndForms()
    {
        var plain = E19Indic(Kn);
        E19Circle(plain, "ಉ\u0CBE");
        E19OneSyllable(plain, "ಜ\u0CBC\u0CCD");
        E19Expect(plain, "ಜ\u0CCD\u0CBC", E19G(Kn, 0x0C9C), E19G(Kn, 0x0CBC), E19G(Kn, 0x0CCD));
        int a = E19G(Kn, 0x0C85), virama = E19G(Kn, 0x0CCD), llla = E19G(Kn, 0x0CDE), ra = E19G(Kn, 0x0CB0), ka = E19G(Kn, 0x0C95), sign = E19G(Kn, 0x0CF1);
        var below = E19Indic(Kn, ("blwf", E12Lig(([virama, llla], E19F(2)), ([virama, ra], E19F(3)))), E19Rphf(Kn, 0x0CB0, 0x0CCD, E19F(1)), ("pres", E12Lig(([sign, ka], E19F(4)))));
        E19Expect(below, "ಅ\u0CCDೞ", a, E19F(2));
        E19Expect(below, "ರ\u0CCDಕ", ka, E19F(1));
        E19Expect(below, "ಕ\u0CCDರ", ka, E19F(3));
        E19Expect(below, "ೱಕ", E19F(4));
        E19OneSyllable(below, "ೱಕ\u0CBF");
    }

    /// <summary>
    /// UNI 12.9.1 sentences 6–14 and 12.9.3 sentences 16–25, 53–57: „ഇU+0D57“ gets a circle; the sequences of table 12-35, digits with vowel signs,
    /// several anusvaras, hyphen-minus and no-break space with AA — no circle.
    /// </summary>
    [Fact]
    public void Uni09_Malayalam_DoNotUseAndBases()
    {
        var font = E19Font([(Ml, Ml + 0x7F, E19Base), ('0', '9', Ot02G('0')), ('-', '-', Ot02G('-')), (0xA0, 0xA0, Ot02G(' '))],
            E11Layout("mlm2", ("nukt", E11Sub((E19G(Ml, 0x0D15), E19G(Ml, 0x0D15))))));
        E19Circle(font, "ഇ\u0D57");
        foreach (var text in new[] { "പ\u0D3Eല\u0D41\u0D4D", "എ\u0D4Dന\u0D4Dന\u0D3E", "ഐശ\u0D40ല\u0D4D\u0D02",
                     "355\u0D3E\u0D02", "ഈ\u0D02\u0D02\u0D02\u0D02", "-\u0D3E", " \u0D3E" })
        {
            Assert.DoesNotContain(E19Shape(font, text), g => g.Glyph == E19Dotted);
            Assert.Equal(text, E19Text(E19Shape(font, text)));
        }
    }

    /// <summary>
    /// UNI 12.9.3 sentences 30–39: a ligature of the font that names ZWJ („ക\u200D\u0D4Dര“) forms, without the ZWJ the standard form; one that names ZWNJ
    /// („ക\u200C\u0D4Dര“, the open form) forms in one syllable without a circle.
    /// </summary>
    [Fact]
    public void Uni09_Malayalam_JoinersInConjuncts()
    {
        int ka = E19G(Ml, 0x0D15), virama = E19G(Ml, 0x0D4D), ra = E19G(Ml, 0x0D30);
        var font = E19Indic(Ml, ("pres", E12Lig(([ka, E19Zwnj + 1, virama, ra], E19F(1)), ([ka, E19Zwnj, virama, ra], E19F(2)))));
        E19Expect(font, "ക\u200D\u0D4Dര", E19F(1));
        Assert.DoesNotContain(E19F(1), E19Drawn(font, "ക\u0D4Dര"));
        E19Expect(font, "ക\u200C\u0D4Dര", E19F(2));
        E19OneSyllable(font, "ക\u200C\u0D4Dര");
    }

    /// <summary>
    /// UNI 12.9.3 sentences 58–64: the dot reph U+0D4E goes behind the consonant (rphf of the font); Ra, virama, Ya takes the post-base Ya and no
    /// reph; Ra, virama, ZWJ no reph either.
    /// </summary>
    [Fact]
    public void Uni09_Malayalam_DotRephAndPostBaseYa()
    {
        int ka = E19G(Ml, 0x0D15), dot = E19G(Ml, 0x0D4E), ra = E19G(Ml, 0x0D30), virama = E19G(Ml, 0x0D4D), ya = E19G(Ml, 0x0D2F);
        var font = E19Indic(Ml, ("rphf", E11Sub((dot, E19F(1)))), ("pstf", E12Lig(([virama, ya], E19F(2)))));
        E19Expect(font, "ൎക", ka, E19F(1));
        E19Expect(font, "ര\u0D4Dയ", ra, E19F(2));
        E19Expect(font, "ര\u0D4D\u200D", ra, virama);
    }

    /// <summary>
    /// UNI 12.9.3 sentences 68–78 and 101–121, 129–131: chillu N, virama, Rra one syllable (with the ligature of the font), also with AA; two Rra
    /// two syllables, the stacked Rra with O (E before, AA after); chillu N and Rra two syllables, chillu N, virama, Rra, OO one syllable with EE
    /// before; Na, virama, ZWJ the glyph of chillu N where the font forms it — as the shaping library.
    /// </summary>
    [Fact]
    public void Uni09_Malayalam_ChilluAndRra()
    {
        int chillu = E19G(Ml, 0x0D7B), virama = E19G(Ml, 0x0D4D), rra = E19G(Ml, 0x0D31), na = E19G(Ml, 0x0D28);
        var font = E19Indic(Ml, ("blwf", E12Lig(([virama, rra], E19F(3)))), ("haln", E12Lig(([na, virama, E19Zwnj + 1], chillu))));
        foreach (var text in new[] { "ൻ\u0D4Dറ", "ൻ\u0D4Dറ\u0D3E", "റ\u0D4Dറ\u0D4A", "ൻ\u0D4Dറ\u0D4B" })
        {
            E19OneSyllable(font, text);
            Assert.Equal(E19Library(font, text), E19Drawn(font, text));
        }
        foreach (var text in new[] { "ററ", "ൻറ", "റ\u0D46റ" })
        {
            Assert.Equal(2, E19Shape(font, text).Select(g => g.Syllable).Distinct().Count());
            Assert.Equal(E19Library(font, text), E19Drawn(font, text));
        }
        E19Expect(font, "റ\u0D4Dറ\u0D4A", E19G(Ml, 0x0D46), rra, E19F(3), E19G(Ml, 0x0D3E));
        E19Expect(font, "ന\u0D4D\u200D", chillu);
    }

    // ------------------------------------------------------------ uni-10: Thaana, Sinhala, Tibetan, Mongolian

    private const int Th = 0x0780, Si19 = 0x0D80, Tb = 0x0F00, Mo = 0x1800;

    /// <summary>
    /// UNI 13.1.1 sentences 1–4 and 14–26: Thaana does not join — the letters U+0780, U+0787, U+07A5, U+07B1 are non-joining, the vowel sign U+07A6
    /// transparent, „ތ\u07A6ތ“ takes no joining form and init/medi/fina of a font under thaa are not applied; the vowel signs sit at the font's anchors
    /// without an advance.
    /// </summary>
    [Fact]
    public void Uni10_Thaana_DoesNotJoin_MarksAtTheirAnchors()
    {
        foreach (var c in new[] { 0x0780, 0x0787, 0x07A5, 0x07B1 }) Assert.Equal('U', ArabicJoining.TypeOf(c));
        Assert.Equal('T', ArabicJoining.TypeOf(0x07A6));
        Assert.All(ArabicJoining.Forms([0x078C, 0x07A6, 0x078C]), f => Assert.Equal(JoinForm.None, f));
        int ta = E19G(Th, 0x078C), fili = E19G(Th, 0x07A6);
        var font = E19Font([(Th, Th + 0x3F, E19Base)], E11Layout("thaa", ("init", E11Sub((ta, E19F(1)))), ("fina", E11Sub((ta, E19F(2))))),
            E11Layout("thaa", ("mark", E12MarkToBase(fili, ta, (0, 0), (200, 900)))));
        var shaped = E19Shape(font, "ތ\u07A6ތ");
        Assert.Equal([ta, fili, ta], shaped.Select(g => g.Glyph));
        Assert.Equal(0, shaped[1].XAdvance);
        Assert.Equal(900, shaped[1].YOffset);
    }

    /// <summary>UNI 13.2.1 sentences 6–18 (table 13-2): each of the nine vowel sequences gets a dotted circle before the vowel sign, the text as typed.</summary>
    [Fact]
    public void Uni10_Sinhala_DoNotUseVowelSequences_DottedCircle()
    {
        var font = E19Indic(Si19);
        foreach (var (a, b) in new[] { (0x0D85, 0x0DCF), (0x0D85, 0x0DD0), (0x0D85, 0x0DD1), (0x0D8B, 0x0DDF), (0x0D8D, 0x0DD8), (0x0D8F, 0x0DDF), (0x0D91, 0x0DCA),
                     (0x0D91, 0x0DD9), (0x0D94, 0x0DDF) })
            E19Circle(font, E19S(a, b));
    }

    /// <summary>
    /// UNI 13.2.1 sentences 36–37, 40, 50–65, 66–85: the Sinhala forms come from the font's lookups — a half form of consonant, ZWJ, al-lakuna
    /// for any consonant (no list of pairs in the code; al-lakuna, ZWJ does not form it), the ligature of Ka, al-lakuna, ZWJ, Ssa with the text of
    /// all four (without the ZWJ three glyphs), Ra with U and with AE as ligatures of blws and psts — as the shaping library.
    /// </summary>
    [Fact]
    public void Uni10_Sinhala_FormsFromTheFont()
    {
        int G(int c) => E19G(Si19, c);
        int zwj = E19Zwnj + 1, da = G(0x0DAF), dha = G(0x0DB0), ka = G(0x0D9A), lakuna = G(0x0DCA), ssa = G(0x0DC2), ra = G(0x0DBB);
        var font = E19Indic(Si19, ("akhn", E12Lig(([ka, lakuna, zwj, ssa], E19F(3)))), ("half", E12Lig(([da, zwj, lakuna], E19F(1)), ([ka, zwj, lakuna], E19F(2)))),
            ("blws", E12Lig(([ra, G(0x0DD4)], E19F(4)))), ("psts", E12Lig(([ra, G(0x0DD0)], E19F(5)))));
        E19Expect(font, "ද\u200D\u0DCAධ", E19F(1), dha);
        Assert.DoesNotContain(E19F(1), E19Drawn(font, "ද\u0DCA\u200Dධ"));
        var kssa = E19Shape(font, "ක\u0DCA\u200Dෂ");
        Assert.Equal([E19F(3)], kssa.Where(g => g.Glyph != zwj).Select(g => g.Glyph));
        Assert.Equal("ක\u0DCA\u200Dෂ", E19Text(kssa));
        E19Expect(font, "ක\u0DCAෂ", ka, lakuna, ssa);
        E19Expect(font, "ර\u0DD4", E19F(4));
        E19Expect(font, "ර\u0DD0", E19F(5));
    }

    /// <summary>
    /// UNI 13.4.1 sentences 32–33, 84–94, 116–127, 226–227, 231–234 and 274–275: Tibetan stacks — Ka with three subjoined letters, A-chung and I,
    /// the unusual stack Nya over Ka, Sa with Pa, Ya and I (Ra a syllable of its own), the subjoined forms of Ka, Ka with A-chung, Pha with
    /// tsa-phru, Ka with three subjoined Ka and two vowel signs are one syllable each without a dotted circle, the text as typed; tsa-phru at the
    /// start gets the circle; a hundred subjoined letters are cut into pieces of 64.
    /// </summary>
    [Fact]
    public void Uni10_Tibetan_Stacks_OneSyllable()
    {
        var font = E19Font([(Tb, Tb + 0xFF, E19Base)], E11Layout("tibt", ("ccmp", E11Sub((E19G(Tb, 0x0F40), E19G(Tb, 0x0F40))))));
        foreach (var text in new[] { "ཀ\u0FB7\u0F99\u0FAD\u0F71\u0F72", "ཉ\u0F90", "ས\u0FA4\u0FB1\u0F72", "ཀ\u0FAD", "ཀ\u0FB1",
                     "ཀ\u0FB2", "ཀ\u0FBA", "ཀ\u0FBB", "ཀ\u0FBC", "ཀ\u0F71", "ཀ\u0FB0", "ཕ\u0F39", "ཀ\u0F90\u0F90\u0F90\u0F72\u0F74" })
        {
            var shaped = E19Shape(font, text);
            Assert.DoesNotContain(shaped, g => g.Glyph == E19Dotted);
            Assert.Single(shaped.Select(g => g.Syllable).Distinct());
            Assert.Equal(text.Normalize(System.Text.NormalizationForm.FormD), E19Text(shaped).Normalize(System.Text.NormalizationForm.FormD));
        }
        Assert.Equal(2, E19Shape(font, "ས\u0FA4\u0FB1\u0F72ར").Select(g => g.Syllable).Distinct().Count());
        Assert.Equal([E19Dotted, E19G(Tb, 0x0F39)], E19Shape(font, "\u0F39").Select(g => g.Glyph));
        var long100 = "ཀ" + new string('\u0F90', 100);
        var pieces = E19Shape(font, long100);
        Assert.Equal(long100, E19Text(pieces));
        Assert.True(pieces.Select(g => g.Syllable).Distinct().Count() >= 2);
    }

    /// <summary>UNI 13.4.1 sentences 74–76 and 220–221: the compound vowels and aspirated letters like their parts (E19_Tibetan_CompoundVowelsAndAspirates_LikeTheirParts).</summary>
    [Fact]
    public void Uni10_Tibetan_CompoundVowelsAndAspirates() => E19_Tibetan_CompoundVowelsAndAspirates_LikeTheirParts();

    /// <summary>
    /// UNI 13.4.1 sentences 100–106, 128–132, 169–181: the forms are the font's — a short Ra by a ccmp context before subjoined Ka, the full Ra
    /// before subjoined Nya; a blws ligature of Ka and subjoined Ra, not formed across the halanta U+0F84; the liga ligature of U+0F04 U+0F05
    /// U+0F05 with the text of all three; a kerning pair of U+0F3C U+0F22.
    /// </summary>
    [Fact]
    public void Uni10_Tibetan_FormsFromTheFont()
    {
        int G(int c) => E19G(Tb, c);
        var gsub = E19Layout("tibt", [("ccmp", Ot02Lookup(6, Ot02Chain([], [[G(0x0F62)]], [[G(0x0F90)]], (0, 3)))), ("blws", E12Lig(([G(0x0F40), G(0x0FB2)], E19F(2)))),
            ("liga", E12Lig(([G(0x0F04), G(0x0F05), G(0x0F05)], E19F(3))))], E11Sub((G(0x0F62), E19F(1))));
        var gpos = E11Layout("tibt", ("kern", Ot02Lookup(2, Ot02Pairs(0, (G(0x0F3C), G(0x0F22), -150)))));
        var font = E19Font([(Tb, Tb + 0xFF, E19Base)], gsub, gpos);
        E19Expect(font, "ར\u0F90", E19F(1), G(0x0F90));
        E19Expect(font, "ར\u0F99", G(0x0F62), G(0x0F99));
        E19Expect(font, "ཀ\u0FB2", E19F(2));
        Assert.DoesNotContain(E19F(2), E19Drawn(font, "ཀ\u0F84\u0FB2"));
        var mark = E19Shape(font, "༄༅༅");
        Assert.Equal([E19F(3)], mark.Select(g => g.Glyph));
        Assert.Equal("༄༅༅", mark[0].Text);
        var parsed = OpenTypeFont.Parse(font);
        Assert.Equal(parsed.AdvanceWidth(G(0x0F3C)) - 150, E19Shape(font, "༼༢༽")[0].XAdvance);
    }

    /// <summary>
    /// UNI 13.5.1 sentences 80–104, 130–159, 193–199: Mongolian joining — positional forms of the font (initial, medial, final), forms with ZWJ and
    /// ZWNJ, MVS and nirugu as by the joining types; a calt context of the font (Ga before A, a masculine Ga at the end of a word after A, A after
    /// MVS); a free variation selector the font maps but no format 14 subtable names chooses by the font's context and is drawn as nothing;
    /// with a format 14 sequence the variant for Ga FVS1 ZWJ, not for Ga ZWJ FVS1 (the older reading is not taken over).
    /// </summary>
    [Fact]
    public void Uni10_Mongolian_FormsFromJoiningAndTheFont()
    {
        Assert.Equal([JoinForm.Initial, JoinForm.Medial, JoinForm.Final], ArabicJoining.Forms([0x1823, 0x1833, 0x1823]));
        Assert.Equal([JoinForm.Isolated], ArabicJoining.Forms([0x1820]));
        Assert.Equal(JoinForm.Initial, ArabicJoining.Forms([0x1820, 0x200D])[0]);
        Assert.Equal(JoinForm.Final, ArabicJoining.Forms([0x200D, 0x1820])[1]);
        Assert.Equal(JoinForm.Medial, ArabicJoining.Forms([0x200D, 0x1820, 0x200D])[1]);
        Assert.Equal([JoinForm.Isolated, JoinForm.None, JoinForm.Isolated], ArabicJoining.Forms([0x1820, 0x200C, 0x1820]));
        Assert.Equal([JoinForm.Initial, JoinForm.Medial, JoinForm.Final, JoinForm.None, JoinForm.Isolated], ArabicJoining.Forms([0x182C, 0x1820, 0x1828, 0x180E, 0x1820]));
        Assert.Equal([JoinForm.Initial, JoinForm.None, JoinForm.Final], ArabicJoining.Forms([0x1820, 0x180A, 0x1820]));
        int G(int c) => E19G(Mo, c);
        int ga = G(0x182D), a = G(0x1820), e = G(0x1821), fvs1 = G(0x180B), mvs = G(0x180E);
        // the three contexts are lookups 0–2, the substitutions they call 3–5
        var gsub = E19Layout("mong", [("calt", Ot02Lookup(6, Ot02Chain([], [[ga]], [[a]], (0, 3)))), ("calt", Ot02Lookup(6, Ot02Chain([], [[ga]], [[fvs1]], (0, 4)))),
            ("calt", Ot02Lookup(6, Ot02Chain([[mvs]], [[a]], [], (0, 5))))], E11Sub((ga, E19F(1))), E11Sub((ga, E19F(2))), E11Sub((a, E19F(3))));
        var font = E19Font([(Mo, Mo + 0xAF, E19Base)], gsub);
        Assert.Equal(E19F(1), E19Shape(font, "ᠭᠠ")[0].Glyph);
        Assert.Equal(ga, E19Shape(font, "ᠭᠡ")[0].Glyph);
        var fvs = E19Shape(font, "ᠭ\u180Bᠠᠯ");
        Assert.Equal(E19F(2), fvs[0].Glyph);
        Assert.Equal(0, fvs[1].XAdvance);
        Assert.Equal(E19F(3), E19Shape(font, "ᠭ\u180Eᠠ")[2].Glyph);
        Assert.Equal(e, E19Shape(font, "ᠭᠡ")[1].Glyph);
    }

    // ------------------------------------------------------------ uni-12: Thai, Lao, Myanmar, Khmer

    private const int Ta19 = 0x0E00, La = 0x0E80, My = 0x1000, Km = 0x1780;

    /// <summary>A font for Thai and Lao (U+0E00–U+0EFF) without GSUB, with the GPOS given.</summary>
    private static byte[] E19Thai(byte[]? gpos = null) => E19Font([(Ta19, Ta19 + 0xFF, E19Base)], null, gpos);

    /// <summary>
    /// UNI 16 sentences 7–9, 16.1.1 sentences 4, 15–47, 76–77 and 16.2.1 sentences 9–36, 72: Thai and Lao are stored in visual order — the
    /// vowels written left of the consonant come first in the text and stay first (no reordering), the marks follow the consonant in the order
    /// typed, PHINTHU and the Lao U+0EBA are glyphs of their own without a dotted circle; each glyph with its own text — as the shaping library.
    /// </summary>
    [Fact]
    public void Uni12_ThaiLao_VisualOrder_GlyphsOfTheCmap()
    {
        var font = E19Thai();
        foreach (var text in new[] { "เก", "ເກ", "แก", "ไก", "ก\u0E3Aข", "กะ", "กา",
                     "ก\u0E34", "ก\u0E35", "ก\u0E36", "ก\u0E37", "ก\u0E38", "ก\u0E39", "เกะ", "เกาะ",
                     "เกอะ", "เกอ", "เก\u0E35ย", "เก\u0E37อ", "ก\u0E31ว", "เกา",
                     "เกย", "ใก", "กฤ", "ກະ", "ກາ", "ເກ\u0EB4", "ເກ\u0EB5",
                     "ເກ\u0EB1ຽ", "ເກ\u0EB7ອ", "ກ\u0EBBວ", "ເກ\u0EBBາ", "ໄກ", "ໃກ", "ກ\u0EBAກ" })
        {
            var shaped = E19Shape(font, text);
            Assert.Equal([.. text.Select(c => E19G(Ta19, c))], shaped.Select(g => g.Glyph));
            Assert.Equal([.. text.Select(c => c.ToString())], shaped.Select(g => g.Text));
            Assert.Equal(E19Library(font, text), E19Drawn(font, text));
        }
    }

    /// <summary>
    /// UNI 16.1.1 sentences 49–51 and 16.2.1 sentences 47–50: the marks above stack outward in the order typed — Ko, MAI EK, NIKHAHIT puts the
    /// nikhahit over the tone mark, the reverse order the tone mark over the nikhahit (mark and mkmk of the font); likewise in Lao.
    /// </summary>
    [Fact]
    public void Uni12_ThaiLao_MarksAboveStackInTheOrderTyped()
    {
        int ko = E19G(Ta19, 0x0E01), ek = E19G(Ta19, 0x0E48), nik = E19G(Ta19, 0x0E4D);
        var gpos = E19Layout("thai", [("mark", E12MarkToBase(ek, ko, (0, 0), (300, 1000))), ("mark", E12MarkToBase(nik, ko, (0, 0), (300, 1000))),
            ("mkmk", E19MarkToMark(nik, ek, (0, 0), (0, 300))), ("mkmk", E19MarkToMark(ek, nik, (0, 0), (0, 300)))]);
        var font = E19Thai(gpos);
        var first = E19Shape(font, "ก\u0E48\u0E4D");
        Assert.Equal([ko, ek, nik], first.Select(g => g.Glyph));
        Assert.True(first[2].YOffset > first[1].YOffset);
        var second = E19Shape(font, "ก\u0E4D\u0E48");
        Assert.Equal([ko, nik, ek], second.Select(g => g.Glyph));
        Assert.True(second[2].YOffset > second[1].YOffset);
        Assert.Equal([E19G(Ta19, 0x0E81), E19G(Ta19, 0x0EC8), E19G(Ta19, 0x0ECD)], E19Shape(font, "ກ\u0EC8\u0ECD").Select(g => g.Glyph));
        Assert.Equal([E19G(Ta19, 0x0E81), E19G(Ta19, 0x0ECD), E19G(Ta19, 0x0EC8)], E19Shape(font, "ກ\u0ECD\u0EC8").Select(g => g.Glyph));
    }

    /// <summary>UNI 16.1.1 sentences 88–99: the consonant diacritics inside SARA U and the tone marks (E19_Thai_ConsonantDiacriticsInsideSaraUAndToneMarks).</summary>
    [Fact]
    public void Uni12_Thai_MacronBelowAndTilde() => E19_Thai_ConsonantDiacriticsInsideSaraUAndToneMarks();

    /// <summary>
    /// UNI 16.3.1 sentences 28–30, 58–85 and 16.3.3 sentences 32–34, 16.3.4 sentences 6–10: Myanmar — Ka, virama, Ka with the subjoined form of
    /// blwf in one syllable (also with U+AA61); a syllable with every class in the order of the table is one syllable without a circle, the
    /// kinzi behind the base, E and medial Ra before it; U+103F, U+102B and U+102C, U+109A, U+108B and U+1089 keep the glyphs of the cmap.
    /// </summary>
    [Fact]
    public void Uni12_Myanmar_Syllables()
    {
        int G(int c) => E19G(My, c);
        int ka = G(0x1000), aa61 = E19Base + 0x100 + 1;
        var font = E19Font([(My, My + 0x9F, E19Base), (0xAA60, 0xAA7F, E19Base + 0x100)],
            E11Layout("mym2", ("blwf", E12Lig(([G(0x1039), ka], E19F(1)), ([G(0x1039), aa61], E19F(2))))));
        E19Expect(font, "က\u1039က", ka, E19F(1));
        E19OneSyllable(font, "က\u1039က");
        E19Expect(font, "က\u1039ꩡ", ka, E19F(2));
        var all = "င\u103A\u1039က\u1039သ\u103B\u103C\u103D\u103E\u1031\u102D\u102F\u102C\u1036\u1037\u1038";
        var shaped = E19Shape(font, all);
        Assert.DoesNotContain(shaped, g => g.Glyph == E19Dotted);
        Assert.Single(shaped.Select(g => g.Syllable).Distinct());
        var glyphs = shaped.Select(g => g.Glyph).ToList();
        Assert.True(glyphs.IndexOf(G(0x1031)) < glyphs.IndexOf(ka) && glyphs.IndexOf(G(0x103C)) < glyphs.IndexOf(ka));
        Assert.True(glyphs.IndexOf(G(0x1004)) > glyphs.IndexOf(ka));
        foreach (var c in new[] { 0x103F, 0x102B, 0x102C, 0x109A, 0x108B, 0x1089 })
            Assert.Equal(G(c), E19Shape(font, E19S(ka, c)).Last().Glyph);
    }

    /// <summary>
    /// UNI 16.4.2 sentences 28, 40–53, 157–171, 220–229, 248–251: Khmer syllables — coeng and Ka at the start get a dotted circle (without a text);
    /// the examples of the standard are one syllable each (or two where the standard divides them) without a circle, the pre-base part of the
    /// split vowel U+17BE before Ha; robat, bantoc and the signs in their syllable; ZWNJ inside a syllable.
    /// </summary>
    [Fact]
    public void Uni12_Khmer_Syllables()
    {
        var font = E19Font([(Km, Km + 0x7F, E19Base)], E11Layout("khmr", ("blwf", E11Sub((E19G(Km, 0x17D2), E19G(Km, 0x17D2))))));
        var start = E19Shape(font, "\u17D2ក");
        Assert.Equal([E19Dotted, E19G(Km, 0x17D2), E19G(Km, 0x1780)], start.Select(g => g.Glyph));
        Assert.Equal("\u17D2ក", E19Text(start));
        foreach (var text in new[] { "ទ\u17B6\u17C6\u17D2ង", "ហ\u17BE\u17D2យ", "ឱ\u17D2យ", "ឱ\u17D2ម", "អ\u17B6យ\u17CC",
                     "ក\u17DD", "ក\u17D1", "ខ\u17D2ញ\u17BB\u17C6", "ម\u17C9\u17D2ង\u17C3", "ក\u200C\u17B7", "ម\u17D2ង\u17C9\u17C3" })
        {
            var shaped = E19Shape(font, text);
            Assert.DoesNotContain(shaped, g => g.Glyph == E19Dotted);
            Assert.Equal(text, E19Text(shaped));
        }
        Assert.Equal(E19G(Km, 0x17C1), E19Shape(font, "ហ\u17BE\u17D2យ")[0].Glyph);
        Assert.Equal(2, E19Shape(font, "ផ\u17D2ឯម").Select(g => g.Syllable).Distinct().Count());
        Assert.Equal(2, E19Shape(font, "ស\u17D2ញប\u17CB").Select(g => g.Syllable).Distinct().Count());
        Assert.Single(E19Shape(font, "អ\u17B6យ\u17CC").Select(g => g.Syllable).Skip(2).Distinct());
    }

    /// <summary>
    /// UNI 16.4.2 sentences 87–124, 149–156: the subscript forms of every consonant and of the independent vowels U+17A7, U+17AB, U+17AC, U+17AF
    /// come from blwf of the font — Ka, coeng, X gives Ka and the form of X (Ro through pref, E12_Khmer_PreBaseParts_CoengRo_Cfar).
    /// </summary>
    [Fact]
    public void Uni12_Khmer_SubscriptFormsFromTheFont()
    {
        int G(int c) => E19G(Km, c);
        var subscripts = Enumerable.Range(0x1780, 0x17A2 - 0x1780 + 1).Where(c => c != 0x179A).Concat([0x17A7, 0x17AB, 0x17AC, 0x17AF]).ToArray();
        var font = E19Font([(Km, Km + 0x7F, E19Base)], E11Layout("khmr", ("blwf", E12Lig([.. subscripts.Select((c, i) => (new[] { G(0x17D2), G(c) }, E19F(i)))]))));
        for (var i = 0; i < subscripts.Length; i++) E19Expect(font, E19S(0x1780, 0x17D2, subscripts[i]), G(0x1780), E19F(i));
    }

    /// <summary>
    /// UNI 16.4.2 sentences 172–187, 245–247, 252–264: the forms of the font — the ligature of Ba and AA; the shortened Nyo before coeng Nyo by a
    /// context (two Nyo without the coeng stay); TRIISAP before II in its U form, alone the ordinary one, with a ZWNJ before it the context of the
    /// consonant does not match; the ligature of Vo and I forms, not with a ZWNJ between them, and with a ZWJ between them (the ZWJ asks for it;
    /// the presentation forms pass over it) — as the shaping library, except the last (it keeps the ZWJ there: PDF-Knead follows the standard).
    /// </summary>
    [Fact]
    public void Uni12_Khmer_FormsFromTheFont()
    {
        int G(int c) => E19G(Km, c);
        int ba = G(0x1794), aa = G(0x17B6), nyo = G(0x1789), coeng = G(0x17D2), tri = G(0x17CA), ii = G(0x17B8), sa = G(0x179F), vo = G(0x179C), i = G(0x17B7);
        // one lookup per feature (the substitutions the contexts call are lookups 5 and 6)
        var gsub = E19Layout("khmr", [("blwf", E12Lig(([coeng, nyo], E19F(1)))), ("psts", E12Lig(([ba, aa], E19F(2)))), ("pres", Ot02Lookup(6, Ot02Chain([], [[nyo]], [[E19F(1)]], (0, 5)))),
            ("blws", Ot02Lookup(6, Ot02Chain([[sa, ba]], [[tri]], [[ii]], (0, 6)))), ("abvs", E12Lig(([vo, i], E19F(5))))], E11Sub((nyo, E19F(3))), E11Sub((tri, E19F(4))));
        var font = E19Font([(Km, Km + 0x7F, E19Base)], gsub);
        E19Expect(font, "ប\u17B6", E19F(2));
        E19Expect(font, "ញ\u17D2ញ", E19F(3), E19F(1));
        E19Expect(font, "ញញ", nyo, nyo);
        E19Expect(font, "ស\u17CA\u17B8", sa, E19F(4), ii);
        E19Expect(font, "ស\u17CA", sa, tri);
        E19Expect(font, "ប\u200C\u17CA\u17B8", ba, tri, ii);
        E19Expect(font, "វ\u17B7ទ\u17BC", E19F(5), G(0x1791), G(0x17BC));
        E19Expect(font, "វ\u200C\u17B7ទ\u17BC", vo, i, G(0x1791), G(0x17BC));
        Assert.Equal([E19F(5), G(0x1791), G(0x17BC)], E19Drawn(font, "វ\u200D\u17B7ទ\u17BC"));
    }

    /// <summary>UNI 16.4.2 sentences 205–209: the Khmer inherent vowels U+17B4 and U+17B5 without a glyph in the font are drawn as nothing, the text as typed.</summary>
    [Fact]
    public void Uni12_Khmer_InherentVowels_DrawnAsNothing()
    {
        var font = E19Font([(Km, Km + 0x33, E19Base)], null);
        var shaped = E19Shape(font, "ក\u17B4ក");
        Assert.Equal([E19G(Km, 0x1780), Ot02G(' '), E19G(Km, 0x1780)], shaped.Select(g => g.Glyph));
        Assert.Equal(0, shaped[1].XAdvance);
        Assert.Equal("ក\u17B4ក", E19Text(shaped));
    }

    // ------------------------------------------------------------ uni-16: kana, Hangul

    /// <summary>
    /// UNI 18.4.1 sentences 7–11: the syllable KA with U+3099 in Noto Sans CJK JP is drawn with the glyph of GA, which the font has — one glyph
    /// with the text as typed, as the shaping library composes it (docs/FEHLERGEDAECHTNIS.md, No. 1193; before, the syllable and the voiced mark
    /// after it); all kana in <c>E21_KanaWithVoicedMark_ComposedAsTheShapingLibrary</c>.
    /// </summary>
    [Fact]
    public void Uni16_Kana_VoicedSoundMark_ComposedWhereTheFontHasTheSyllable()
    {
        var noto = Iso03NotoJp.Value;
        var shaped = TextShaper.For(noto).Shape("\u304B\u3099");
        Assert.Equal([noto.GlyphForUnicode(0x304C)], shaped.Select(g => g.Glyph));
        Assert.Equal("\u304B\u3099", E19Text(shaped));
        var hb = ShaperTests.HarfBuzz(noto.FileData, noto.UnitsPerEm, "\u304B\u3099", []);
        Assert.Equal([noto.GlyphForUnicode(0x304C)], hb.Glyphs);
    }

    /// <summary>UNI 18.4.6 sentence 23: チァム and the tone mark U+1AFF5 give four glyphs in text order, the tone mark after the syllable with an advance.</summary>
    [Fact]
    public void Uni16_Kana_ToneMarkAfterTheSyllable()
    {
        var font = E19Font([(0x30A1, 0x30A1, 1101), (0x30C1, 0x30C1, 1102), (0x30E0, 0x30E0, 1103), (0x1AFF5, 0x1AFF5, Ot02G('x'))], null);
        var shaped = E19Shape(font, "\u30C1\u30A1\u30E0\U0001AFF5");
        Assert.Equal([1102, 1101, 1103, Ot02G('x')], shaped.Select(g => g.Glyph));
        Assert.True(shaped[3].XAdvance > 0);
        Assert.Equal("\u30C1\u30A1\u30E0\U0001AFF5", E19Text(shaped));
    }

    /// <summary>
    /// UNI 18.6.1 sentences 9–10: the Hangul fillers are default ignorable — without a glyph in the font drawn as nothing; with a glyph of the
    /// font (Malgun Gothic) the font's blank glyph, as the shaping library; the choseong filler with a vowel forms the font's syllable.
    /// </summary>
    [Fact]
    public void Uni16_HangulFillers()
    {
        var bare = E19Font([(0x1161, 0x1161, Ot02G('a'))], null);
        foreach (var filler in new[] { "\u115F", "\u1160", "\u3164", "\uFFA0" })
        {
            var shaped = E19Shape(bare, filler + "\u1161");
            Assert.Equal(Ot02G(' '), shaped[0].Glyph);
            Assert.Equal(0, shaped[0].XAdvance);
        }
        var malgun = @"C:\Windows\Fonts\malgun.ttf";
        if (!File.Exists(malgun)) return;
        var data = File.ReadAllBytes(malgun);
        var font = OpenTypeFont.Parse(data);
        foreach (var text in new[] { "\u115F\u1161", "\u1160" })
            Assert.Equal(ShaperTests.HarfBuzz(data, font.UnitsPerEm, text, []).Glyphs, TextShaper.For(font).Shape(text).Select(g => g.Glyph));
    }

    /// <summary>
    /// UNI 18.6.2, 18.6.3 sentences 1–2, 18.6.4 sentences 1–4 and 18.6.5 sentences 13–18: the jamo of the extended blocks take ljmo, vjmo, tjmo;
    /// the compatibility jamo ㄱㅏ keep the glyphs of the cmap and do not join; a precomposed syllable the font lacks („각“ with only 가 and the jamo)
    /// is taken apart into the jamo of the features, the text as typed — as the shaping library.
    /// </summary>
    [Fact]
    public void Uni16_Hangul_JamoFeaturesAndDecomposition()
    {
        int l = 1101, v = 1102, t = 1103, ga = 1104, lx = 1105, vx = 1106, tx = 1107, k1 = 1108, k2 = 1109;
        var gsub = E19Layout("hang", [("ljmo", E11Sub((l, E19F(1)), (lx, E19F(4)))), ("vjmo", E11Sub((v, E19F(2)), (vx, E19F(5)))), ("tjmo", E11Sub((t, E19F(3)), (tx, E19F(6))))]);
        var font = E19Font([(0x1100, 0x1100, l), (0x1161, 0x1161, v), (0x11A8, 0x11A8, t), (0xAC00, 0xAC00, ga), (0xA960, 0xA960, lx), (0xD7B0, 0xD7B0, vx),
            (0xD7CB, 0xD7CB, tx), (0x3131, 0x3131, k1), (0x314F, 0x314F, k2)], gsub);
        E19Expect(font, "\uA960\uD7B0\uD7CB", E19F(4), E19F(5), E19F(6));
        E19Expect(font, "\u3131\u314F", k1, k2);
        var shaped = E19Shape(font, "\uAC01");
        Assert.Equal([E19F(1), E19F(2), E19F(3)], shaped.Select(g => g.Glyph));
        Assert.Equal("\uAC01", E19Text(shaped));
        Assert.Equal(E19Library(font, "\uAC01"), E19Drawn(font, "\uAC01"));
        E19Expect(font, "\uAC00", ga);
    }

    // ------------------------------------------------------------ uni-17: chapter 23

    /// <summary>
    /// UNI 23.2.1 sentences 10, 14–15, 19–22, 33–48 and 23.2.4 sentences 10, 40–48: WJ, U+FEFF, CGJ and the soft hyphen without a glyph are drawn
    /// as nothing and do not break the fi ligature (E19_DefaultIgnorable_WithoutGlyph_DrawnAsNothing, E19_FormatCharacters_DoNotBreakLigaturesOrKerning);
    /// ZWSP and U+FEFF between Arabic letters keep the joining of beh (initial, final).
    /// </summary>
    [Fact]
    public void Uni17_FormatCharacters_InvisibleAndTransparent()
    {
        E19_DefaultIgnorable_WithoutGlyph_DrawnAsNothing();
        E19_FormatCharacters_DoNotBreakLigaturesOrKerning();
        Assert.Equal([JoinForm.Initial, JoinForm.None, JoinForm.Final], ArabicJoining.Forms([0x0628, 0x200B, 0x0628]));
        Assert.Equal([JoinForm.Initial, JoinForm.None, JoinForm.Final], ArabicJoining.Forms([0x0628, 0xFEFF, 0x0628]));
    }

    /// <summary>
    /// UNI 23.2.2 sentences 26, 38–41, 57–64, 71–75: ZWJ asks for a ligature outside the joining scripts and keeps lam joined without lam-alef
    /// (E19_Zwj_AsksForTheLigature_ExceptInJoiningAndSyllabicStages); lam, ZWJ, ZWNJ, ZWJ, alef — lam initial, alef final, no lam-alef in Noto Sans
    /// Arabic; beh, ZWJ, ZWNJ, beh — initial and isolated; a combining enclosing circle between two beh leaves them joined; a font ligature that
    /// names the ZWJ forms.
    /// </summary>
    [Fact]
    public void Uni17_Joiners_LigaturesAndJoining()
    {
        E19_Zwj_AsksForTheLigature_ExceptInJoiningAndSyllabicStages();
        var forms = ArabicJoining.Forms([0x0644, 0x200D, 0x200C, 0x200D, 0x0627]);
        Assert.Equal(JoinForm.Initial, forms[0]);
        Assert.Equal(JoinForm.Final, forms[4]);
        var arabic = TextShaper.For(Uni04NotoArabic);
        var lamAlef = arabic.Shape("\u0644\u0627").Select(g => g.Glyph).ToArray();
        Assert.NotEqual(lamAlef, arabic.Shape("\u0644\u200D\u200C\u200D\u0627").Where(g => g.Text.Length > 0 && g.Text[0] is '\u0644' or '\u0627').Select(g => g.Glyph).ToArray());
        var beh = ArabicJoining.Forms([0x0628, 0x200D, 0x200C, 0x0628]);
        Assert.Equal(JoinForm.Initial, beh[0]);
        Assert.Equal(JoinForm.Isolated, beh[3]);
        Assert.Equal([JoinForm.Initial, JoinForm.None, JoinForm.Final], ArabicJoining.Forms([0x0628, 0x20DD, 0x0628]));
        int f = Ot02G('f'), i = Ot02G('i'), zwj = 1061;
        var named = E19Font([('f', 'f', f), ('i', 'i', i)], Ot02Latin([new Ot02Feature("liga", [0])], Ot02Lookup(4, Ot02Ligature(([f, zwj, i], Ot49Form)))));
        Assert.Equal([Ot49Form], E19Shape(named, "f\u200Di").Select(g => g.Glyph));
    }

    /// <summary>
    /// UNI 23.2.3 sentences 5–7 in Noto Sans Arabic: with WJ between the digits after the end of ayah only the first digit is enclosed — the
    /// second has the glyph it has alone (the WJ breaks the unit, E19_PrependedConcatenationMark_WordJoinerBreaksTheUnit).
    /// </summary>
    [Fact]
    public void Uni17_EndOfAyah_WordJoinerEndsTheUnit()
    {
        var arabic = TextShaper.For(Uni04NotoArabic);
        var two = arabic.Shape("\u0662").Single().Glyph;
        var broken = arabic.Shape("\u06DD\u0661\u2060\u0662");
        Assert.Equal(two, broken.Single(g => g.Text == "\u0662").Glyph);
        E19_PrependedConcatenationMark_WordJoinerBreaksTheUnit();
    }

    /// <summary>
    /// UNI 23.2.6 sentences 14–21: a musical beam control is not interpreted and drawn as nothing without a glyph of the font; the annotation
    /// characters U+FFF9–U+FFFB are not default ignorable and stay visible with an advance.
    /// </summary>
    [Fact]
    public void Uni17_BeamControlsInvisible_AnnotationsVisible()
    {
        var font = E19Font([('a', 'c', Ot02G('a'))], null);
        var beam = E19Shape(font, "a\U0001D173b");
        Assert.Equal([Ot02G('a'), Ot02G(' '), Ot02G('b')], beam.Select(g => g.Glyph));
        Assert.Equal(0, beam[1].XAdvance);
        var annotated = E19Shape(font, "a\uFFF9b\uFFFAc\uFFFB");
        Assert.All(annotated.Where(g => g.Text.Length > 0 && g.Text[0] >= '\uFFF9'), g => Assert.True(g.XAdvance > 0));
    }

    /// <summary>UNI 23.3.1 sentences 19–36: the deprecated format characters U+206C–U+206F are not evaluated (E19_DeprecatedFormatCharacters_NotEvaluated).</summary>
    [Fact]
    public void Uni17_DeprecatedFormatCharacters_NotEvaluated() => E19_DeprecatedFormatCharacters_NotEvaluated();

    /// <summary>
    /// UNI 23.4 sentences 21–29: a variant of a format 14 sequence carries the mark at its own anchor and with its own advance; the keycap ligature
    /// forms with the variation selector (E19_Keycap_VariationSelectorDoesNotBreakTheLigature).
    /// </summary>
    [Fact]
    public void Uni17_VariationSequences()
    {
        const int x = 600, acute = 601, variant = 602;
        var format12 = E11Cmap(('X', 'X', x), (0x0301, 0x0301, acute), (' ', ' ', Ot02G(' ')))[12..];
        var format14 = BeOt11((14, 2), (31, 4), (1, 4), (0xFE00, 3), (0, 4), (21, 4), (1, 4), ('X', 3), (variant, 2));
        var gpos = E19Layout("latn", [("mark", E12MarkToBase(acute, x, (0, 0), (100, 900))), ("mark", E12MarkToBase(acute, variant, (0, 0), (500, 1200)))]);
        var font = ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
        {
            ["cmap"] = CmapOt11((0, 5, format14), (3, 10, format12)), ["GSUB"] = null, ["GPOS"] = gpos, ["GDEF"] = null, ["kern"] = null,
        });
        var shaped = E19Shape(font, "X\uFE00\u0301");
        Assert.Equal([variant, acute], shaped.Select(g => g.Glyph));
        Assert.Equal(1200, shaped[1].YOffset);
        Assert.Equal(OpenTypeFont.Parse(font).AdvanceWidth(variant), shaped[0].XAdvance);
        E19_Keycap_VariationSelectorDoesNotBreakTheLigature();
    }

    /// <summary>UNI 23.5 sentences 10–23: a private-use character has the default joining type (non-joining): beh before it isolated.</summary>
    [Fact]
    public void Uni17_PrivateUse_DefaultProperties() =>
        Assert.Equal([JoinForm.Isolated, JoinForm.None], ArabicJoining.Forms([0x0628, 0xE000]));

    // ------------------------------------------------------------ uni-04, uni-05: Hebrew and Arabic

    /// <summary>
    /// UNI 9.1.1 sentences 86–101: between Hebrew marks a ZWJ asks for the font's ligature (hataf patah, ZWJ, meteg as one glyph where the font
    /// has it, or the ligature of the two marks), a ZWNJ keeps three glyphs.
    /// </summary>
    [Fact]
    public void Uni04_HebrewMarks_ZwjAsksZwnjKeepsApart()
    {
        int bet = 1101, patah = 1102, meteg = 1103, zwj = 1061;
        foreach (var components in new[] { new[] { patah, zwj, meteg }, [patah, meteg] })
        {
            var font = E19Font([(0x05D1, 0x05D1, bet), (0x05B2, 0x05B2, patah), (0x05BD, 0x05BD, meteg)],
                E19Layout("hebr", [("liga", E12Lig((components, E19F(1))))]));
            Assert.Equal([bet, E19F(1)], E19Drawn(font, "\u05D1\u05B2\u200D\u05BD"));
            Assert.Equal([bet, patah, meteg], E19Drawn(font, "\u05D1\u05B2\u200C\u05BD"));
        }
    }

    /// <summary>
    /// UNI 9.2.1 sentences 35–41: alef with madda U+0622 in Noto Sans Arabic is drawn as the font draws it (the alef form of its ccmp and the
    /// madda attached), the same glyphs as the shaping library; alef with U+0653 typed apart is composed for display into U+0622 and so gets the
    /// same glyphs at the same places, as the shaping library (docs/FEHLERGEDAECHTNIS.md, No. 1193; before, the plain alef with the madda);
    /// both saved and read back as typed.
    /// </summary>
    [Fact]
    public void Uni04_Madda_PrecomposedAndSequence_DrawnAlikeAsTheLibrary()
    {
        var arabic = TextShaper.For(Uni04NotoArabic);
        var precomposed = arabic.Shape("\u0622");
        var hb = ShaperTests.HarfBuzz(Uni04NotoArabic.FileData, Uni04NotoArabic.UnitsPerEm, "\u0622", []);
        Assert.Equal(hb.Glyphs.Order(), precomposed.Select(g => g.Glyph).Order());
        var sequence = arabic.Shape("\u0627\u0653");
        Assert.Equal(E11Drawn(precomposed), E11Drawn(sequence));
        Assert.Equal(ShaperTests.HarfBuzz(Uni04NotoArabic.FileData, Uni04NotoArabic.UnitsPerEm, "\u0627\u0653", []).Glyphs.Order(), sequence.Select(g => g.Glyph).Order());
        Assert.Equal("\u0627\u0653", E19Text(sequence));
        foreach (var text in new[] { "\u0622", "\u0627\u0653" }) Assert.Equal(text, Uni04RoundTrip(text).Block);
    }
}
