using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, the universal shaping model (docs/FEHLERGEDAECHTNIS.md, No. 1181): the Brahmi-derived and related scripts without a shaping of their own
/// (Balinese, Javanese, Sundanese, Tai Tham, Cham, Batak, Buginese, Chakma, Newa, Kawi, Limbu, Lepcha, Meetei Mayek …), Miao and the Khitan Small
/// Script. The fonts are Liberation Sans with a cmap that maps the block of the script to glyphs 100 … (the dotted circle to 300, ZWNJ and ZWJ to
/// 301 and 302) and with GSUB or GPOS built by hand; the forms the lookups make are glyphs from 400 on. Where the bundled shaping library knows
/// the script, it shapes the same font and must give the same glyphs (and positions). Test inputs with marks and joiners are written as escapes.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (E17)

    /// <summary>Liberation Sans with a cmap for the ranges given (first, last, first glyph), space, ZWNJ, ZWJ and the dotted circle, and the tables.</summary>
    private static byte[] E17Font(byte[]? gsub, byte[]? gpos, params (int First, int Last, int Glyph)[] ranges) =>
        ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
        {
            ["cmap"] = E11Cmap([(' ', ' ', Ot02G(' ')), (0x200C, 0x200D, E12Zwnj), (0x25CC, 0x25CC, E12Dotted), .. ranges]),
            ["GSUB"] = gsub, ["GPOS"] = gpos, ["GDEF"] = null, ["kern"] = null,
        });

    /// <summary>Liberation Sans with a cmap for <paramref name="length"/> code points from <paramref name="block"/> on (glyphs 100 …).</summary>
    private static byte[] E17Block(int block, int length, byte[]? gsub, byte[]? gpos = null) => E17Font(gsub, gpos, (block, block + length - 1, E12Base));

    /// <summary>A GSUB or GPOS of one script whose default language system has the features given, each with its lookups.</summary>
    private static byte[] E17Layout(string script, (string Feature, int[] Lookups)[] features, params Action<Ot02Bytes>[] lookups) =>
        Ot02Layout([new Ot02Script(script, new Ot02Lang("", [.. Enumerable.Range(0, features.Length)]))],
            [.. features.Select(f => new Ot02Feature(f.Feature, f.Lookups))], lookups);

    /// <summary>The glyphs of the own shaper and of the shaping library must both be <paramref name="expected"/>.</summary>
    private static void E17Expect(byte[] font, string text, params int[] expected) => E12Expect(font, text, expected);

    /// <summary>The drawn positions of the own shaper must equal those of the shaping library (and are given back).</summary>
    private static (int Glyph, double X, double Y)[] E17SamePositions(byte[] font, string text)
    {
        var own = E11Drawn(E12Shape(font, text));
        Assert.Equal(E11HarfBuzzDrawn(font, 2048, text, false), own);
        return own;
    }

    /// <summary>A mark attachment lookup (type 4 mark-to-base or 6 mark-to-mark, format 1) for one mark on one base with one anchor each.</summary>
    private static Action<Ot02Bytes> E17Attach(int type, int mark, int @base, (int X, int Y) markAnchor, (int X, int Y) baseAnchor) => Ot02Lookup(type, b =>
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

    /// <summary>The syllable numbers the shaper gave the glyphs of a text (0: none).</summary>
    private static int[] E17Syllables(byte[] font, string text) => [.. E12Shape(font, text).Select(g => (int)g.Syllable)];

    /// <summary>
    /// Own text in the font given (its own family) saved on a blank page and opened again: the text of the block and what the extraction reads
    /// beyond the blank page.
    /// </summary>
    private static (string Block, string Page) E17RoundTrip(byte[] font, string text)
    {
        var family = "E17 " + Guid.NewGuid().ToString("N");
        E11UseFont(family, OpenTypeFont.Parse(font), false);
        var element = new RichTextElement { Rect = new PdfRectangle(0, 0, 300, 50), Content = new RichText(text, new RichStyle { Family = family, Size = 12 }) };
        var saved = UniTestsSaved(element);
        string block;
        using (var doc = PdfDocument.Open(saved))
            block = Assert.Single(PageItem.From(doc)[0].Elements.OfType<RichTextElement>()).Content.Text;
        var page = UniTestsPageText(saved).Trim('\r', '\n');
        Assert.StartsWith(Uni04BlankText, page, StringComparison.Ordinal);
        return (block, page[Uni04BlankText.Length..].Trim('\r', '\n'));
    }

    // ------------------------------------------------------------ Newa (uni-10)

    private const int Newa = 0x11400;

    private static int Nw(int cp) => E12G(Newa, cp);

    /// <summary>
    /// UNI 13.3.1 sentences 9–11: Newa is an abugida with a virama like the other Brahmi scripts — the vowel sign I (U+11436, standing left) is
    /// drawn before its consonant, and alone it gets the dotted circle as its base (drawn after it). Before, the glyphs stayed in text order and
    /// a lone vowel sign had no base.
    /// </summary>
    [Fact]
    public void E17_Newa_PreBaseVowelSignBeforeItsConsonant_DottedCircleWithoutBase()
    {
        var font = E17Block(Newa, 0x80, null);
        E17Expect(font, "\U0001140E\U00011436", Nw(0x11436), Nw(0x1140E));
        E17Expect(font, "\U00011436", Nw(0x11436), E12Dotted);
    }

    /// <summary>
    /// UNI 13.3.1 sentences 16–21: Ra at the start of a cluster is a repha by default (rphf of the font, drawn after the base), a half form comes
    /// from half (also with ZWJ after the virama), and Ra, virama, ZWJ asks for the eyelash form instead of the repha (the font's rphf passes it
    /// over before a ZWJ, its half form of Ra applies). Before, rphf and half were never applied.
    /// </summary>
    [Fact]
    public void E17_Newa_RephaHalfFormAndEyelashRa()
    {
        int ka = Nw(0x1140E), kha = Nw(0x1140F), ra = Nw(0x1142C), virama = Nw(0x11442);
        var gsub = E17Layout("newa", [("rphf", [0]), ("half", [2])],
            Ot02Lookup(6, Ot02ChainGlyphs([], [ra, virama], [E12Zwj]), Ot02ChainGlyphs([], [ra, virama], [], (0, 1))),
            E12Lig(([ra, virama], E12F(1))),
            E12Lig(([ka, virama], E12F(0)), ([ra, virama], E12F(2))));
        var font = E17Block(Newa, 0x80, gsub);
        E17Expect(font, "\U0001142C\U00011442\U0001140E", ka, E12F(1));
        E17Expect(font, "\U0001140E\U00011442\u200D\U0001140F", E12F(0), E12Zwj, kha);
        E17Expect(font, "\U0001142C\U00011442\u200D\U0001140E", E12F(2), E12Zwj, ka);
    }

    /// <summary>
    /// UNI 13.3.1 sentences 33–36: Jihvamuliya U+11460 (a consonant with stacker) stacks with the next consonant without a virama, also with a
    /// vowel sign — one cluster, the stack from the font's cjct; the vowel sign I goes before it. Before, there was no cluster and cjct never
    /// applied.
    /// </summary>
    [Fact]
    public void E17_Newa_ConsonantWithStacker_OneCluster()
    {
        var font = E17Block(Newa, 0x80, E17Layout("newa", [("cjct", [0])], E12Lig(([Nw(0x11460), Nw(0x1140E)], E12F(3)))));
        E17Expect(font, "\U00011460\U0001140E\U00011436", Nw(0x11436), E12F(3));
        Assert.Single(E12Shape(font, "\U00011460\U0001140E\U00011436").Select(g => g.Cluster).Distinct());
    }

    /// <summary>
    /// UNI 13.3.1 sentences 37–41: the vowel signs ai, o, au take a form in two parts after Ga and some other consonants — from the font's pres
    /// (a context: AI after Ga). Before, pres was never applied.
    /// </summary>
    [Fact]
    public void E17_Newa_TwoPartVowelAfterGa_FromPres()
    {
        int ga = Nw(0x11410), ai = Nw(0x1143F);
        var font = E17Block(Newa, 0x80, E17Layout("newa", [("pres", [0])], Ot02Lookup(6, Ot02ChainGlyphs([ga], [ai], [], (0, 1))), E11Sub((ai, E12F(4)))));
        E17Expect(font, "\U00011410\U0001143F", ga, E12F(4));
        E17Expect(font, "\U0001140E\U0001143F", Nw(0x1140E), ai);
    }

    /// <summary>
    /// UNI 13.3.1 sentences 42–44: the ligatures kssa and jnya as sequences with virama (akhn of the font) and Ra with U as a ligature (blws).
    /// Before, neither feature was applied.
    /// </summary>
    [Fact]
    public void E17_Newa_AkhnAndBlwsLigatures()
    {
        int ka = Nw(0x1140E), virama = Nw(0x11442), ssa = Nw(0x11432), ra = Nw(0x1142C), u = Nw(0x11438);
        var font = E17Block(Newa, 0x80, E17Layout("newa", [("akhn", [0]), ("blws", [1])], E12Lig(([ka, virama, ssa], E12F(6))), E12Lig(([ra, u], E12F(7)))));
        E17Expect(font, "\U0001140E\U00011442\U00011432", E12F(6));
        E17Expect(font, "\U0001142C\U00011438", E12F(7));
    }

    // ------------------------------------------------------------ Limbu, Meetei Mayek, Chakma, Lepcha, Saurashtra, Gondi (uni-11)

    private const int Limbu = 0x1900;

    private static int Lb(int cp) => E12G(Limbu, cp);

    /// <summary>
    /// UNI 13.6.1 sentences 17–21 and 61–75: the subjoined ya, ra, wa (U+1929–192B) hang below their consonant — pstf and psts of the font form
    /// them; the syllables of Table 13-8 take the forms of abvs, blws and psts. Before, these features were never applied (the glyphs of the
    /// cmap stayed).
    /// </summary>
    [Fact]
    public void E17_Limbu_SubjoinedConsonantsAndPresentationForms()
    {
        var gsub = E17Layout("limb", [("pstf", [0]), ("psts", [1]), ("abvs", [2]), ("blws", [3])],
            E12Lig(([Lb(0x1901), Lb(0x1929)], E12F(0))), E11Sub((E12F(0), E12F(1)), (Lb(0x192A), E12F(4))),
            E12Lig(([Lb(0x190B), Lb(0x1927)], E12F(2))), E12Lig(([Lb(0x190B), Lb(0x1922)], E12F(3))));
        var font = E17Block(Limbu, 0x50, gsub);
        E17Expect(font, "\u1901\u1929", E12F(1));
        E17Expect(font, "\u190B\u1927", E12F(2));
        E17Expect(font, "\u190B\u1922", E12F(3));
        E17Expect(font, "\u190B\u192A", Lb(0x190B), E12F(4));
    }

    /// <summary>
    /// UNI 13.6.1 sentences 33–35, 42–44 and 76–86: vowel signs above and below (U+1927, U+1922) and kemphreng U+193A sit on their consonant
    /// without an advance (abvm and blwm of the font, kemphreng after a vowel sign by mkmk), U+1923 stands right with its advance, a small final
    /// letter (U+1930) follows with its advance; text order as typed. Before, abvm and blwm were never applied.
    /// </summary>
    [Fact]
    public void E17_Limbu_MarksPositionedByAbvmAndBlwm()
    {
        int ta = Lb(0x190B);
        var gpos = E17Layout("limb", [("abvm", [0, 1]), ("blwm", [2]), ("mkmk", [3])],
            E17Attach(4, Lb(0x1927), ta, (0, 0), (300, 1500)), E17Attach(4, Lb(0x193A), ta, (0, 0), (500, 1700)),
            E17Attach(4, Lb(0x1922), ta, (0, 0), (400, -200)), E17Attach(6, Lb(0x193A), Lb(0x1920), (0, 0), (100, 400)));
        var font = E17Block(Limbu, 0x50, null, gpos);
        foreach (var text in new[] { "\u190B\u1927", "\u190B\u1922", "\u190B\u1923", "\u190B\u1920\u193A", "\u190B\u1921\u193A", "\u190B\u1930" })
            E17SamePositions(font, text);
        var above = E11Drawn(E12Shape(font, "\u190B\u1927"));
        Assert.Equal((Lb(0x1927), 300.0, 1500.0), above[1]);
        var below = E11Drawn(E12Shape(font, "\u190B\u1922"));
        Assert.Equal((Lb(0x1922), 400.0, -200.0), below[1]);
        var final = E11Drawn(E12Shape(font, "\u190B\u1930"));
        Assert.True(final[1].X > 0);
    }

    private const int Meetei = 0xABC0, MeeteiExt = 0xAAE0;

    private static int Mm(int cp) => cp >= Meetei ? E12Base + cp - Meetei : 200 + cp - MeeteiExt;

    /// <summary>
    /// UNI 13.7.1 sentences 10–18 and 21–23, 13.7.2 sentences 3–5: vowel signs at their consonant with the forms of psts; a consonant with two
    /// vowel signs (an abbreviation) stays one cluster without a dotted circle; the killer apun iyek stays visible, without an advance, with no
    /// conjunct; the virama U+AAF6 of historic texts forms conjuncts like in other Brahmi scripts (half of the font, no glyph of its own) and the
    /// vowel sign U+AAEB stands left of its consonant. Before, psts and half were not applied and U+AAEB followed its consonant.
    /// </summary>
    [Fact]
    public void E17_MeeteiMayek_VowelSignsConjunctsAndLeftVowel()
    {
        var gsub = E17Layout("mtei", [("half", [0]), ("psts", [1])], E12Lig(([Mm(0xABC9), Mm(0xAAF6)], E12F(0))), E11Sub((Mm(0xABE3), E12F(1))));
        var font = E17Font(gsub, null, (Meetei, 0xABFF, E12Base), (MeeteiExt, 0xAAFF, 200));
        E17Expect(font, "\uABC0\uABE3", Mm(0xABC0), E12F(1));
        E17Expect(font, "\uABC0\uABE5", Mm(0xABC0), Mm(0xABE5));
        E17Expect(font, "\uABC0\uABE3\uABE4", Mm(0xABC0), E12F(1), Mm(0xABE4));
        E17Expect(font, "\uABC9\uAAF6\uABCD", E12F(0), Mm(0xABCD));
        E17Expect(font, "\uAAE2\uAAEB", Mm(0xAAEB), Mm(0xAAE2));
        // the killer apun iyek U+ABED stays a visible mark without an advance at its consonant, no conjunct
        var marked = E17Font(gsub, E17Layout("mtei", [("mark", [0])], E17Attach(4, Mm(0xABED), Mm(0xABC0), (0, 0), (700, -50))),
            (Meetei, 0xABFF, E12Base), (MeeteiExt, 0xAAFF, 200));
        var killed = E17SamePositions(marked, "\uABC0\uABED\uABD4");
        Assert.Equal([Mm(0xABC0), Mm(0xABED), Mm(0xABD4)], killed.Select(d => d.Glyph));
        Assert.Equal((Mm(0xABED), 700.0, -50.0), killed[1]);
    }

    private const int Chakma = 0x11100;

    private static int Ck(int cp) => E12G(Chakma, cp);

    /// <summary>
    /// UNI 13.13.1 sentences 5–8, 11–16 and 18–19: after the virama U+11133 the consonant takes its subjoined form (blwf of the font, the virama
    /// without a glyph of its own), the visible killer maayyaa U+11134 stays a glyph — also before a vowel sign, which then follows without a
    /// dotted circle —, candrabindu with anusvara or visarga stack on the consonant (abvm, mkmk). Before, blwf and abvm were never applied.
    /// </summary>
    [Fact]
    public void E17_Chakma_SubjoinedAfterViramaMaayyaaVisibleSignsStacked()
    {
        int ka = Ck(0x11107);
        var gsub = E17Layout("cakm", [("blwf", [0])], E12Lig(([Ck(0x11133), Ck(0x11122)], E12F(0)), ([Ck(0x11133), ka], E12F(1))));
        var gpos = E17Layout("cakm", [("abvm", [0]), ("mkmk", [1, 2])],
            E17Attach(4, Ck(0x11100), ka, (0, 0), (400, 1500)), E17Attach(6, Ck(0x11101), Ck(0x11100), (0, 0), (0, 300)),
            E17Attach(6, Ck(0x11102), Ck(0x11100), (0, 0), (100, 300)));
        var font = E17Block(Chakma, 0x50, gsub, gpos);
        E17Expect(font, "\U00011107\U00011133\U00011122", ka, E12F(0));
        E17Expect(font, "\U00011107\U00011133\U00011107", ka, E12F(1));
        E17Expect(font, "\U00011107\U00011134", ka, Ck(0x11134));
        E17Expect(font, "\U00011107\U00011134\U00011127", ka, Ck(0x11134), Ck(0x11127));
        var stacked = E17SamePositions(font, "\U00011107\U00011100\U00011101");
        Assert.Equal((Ck(0x11101), 400.0, 1800.0), stacked[2]);
        E17SamePositions(font, "\U00011107\U00011100\U00011102");
    }

    private const int Lepcha = 0x1C00;

    private static int Lp(int cp) => E12G(Lepcha, cp);

    /// <summary>
    /// UNI 13.14.1 sentences 15–20 and 37–57: the vowel signs I and O (U+1C27, U+1C28) and the signs nyin-do and kang (U+1C34, U+1C35) are drawn
    /// left of the consonant — kang also left of a vowel sign standing left —, the syllable of Table 13-9 is one cluster with its vowel sign
    /// reordered, the same characters out of order make a broken cluster (dotted circle). Before, everything stayed in text order.
    /// </summary>
    [Fact]
    public void E17_Lepcha_LeftSignsReorderedSyllableAsOneCluster()
    {
        var font = E17Block(Lepcha, 0x50, null);
        E17Expect(font, "\u1C00\u1C27", Lp(0x1C27), Lp(0x1C00));
        E17Expect(font, "\u1C00\u1C34", Lp(0x1C34), Lp(0x1C00));
        E17Expect(font, "\u1C00\u1C27\u1C35", Lp(0x1C35), Lp(0x1C27), Lp(0x1C00));
        const string syllable = "\u1C00\u1C37\u1C25\u1C24\u1C27\u1C2D\u1C36";
        E17Expect(font, syllable, Lp(0x1C27), Lp(0x1C00), Lp(0x1C37), Lp(0x1C25), Lp(0x1C24), Lp(0x1C2D), Lp(0x1C36));
        Assert.Single(E17Syllables(font, syllable).Distinct());
        E17Expect(font, "\u1C00\u1C27\u1C25", Lp(0x1C27), Lp(0x1C00), E12Dotted, Lp(0x1C25));
    }

    /// <summary>
    /// UNI 13.14.1 sentences 15–20: the reordered syllable U+1C00 U+1C27 is one cluster with the text as typed — saved and opened again, the block and the
    /// extracted page text come back in character order; the vowel sign E (U+1C2C) sits below its consonant (blwm). Before, U+1C27 followed its
    /// consonant and blwm was not applied.
    /// </summary>
    [Fact]
    public void E17_Lepcha_ReorderedSyllableSavedAndReadAsTyped_EBelow()
    {
        var gpos = E17Layout("lepc", [("blwm", [0])], E17Attach(4, Lp(0x1C2C), Lp(0x1C00), (0, 0), (500, -300)));
        var font = E17Block(Lepcha, 0x50, null, gpos);
        var shaped = E12Shape(font, "\u1C00\u1C27");
        Assert.Equal([Lp(0x1C27), Lp(0x1C00)], shaped.Select(g => g.Glyph));
        Assert.Equal("\u1C00\u1C27", shaped[0].Text);
        Assert.Equal("", shaped[1].Text);
        var (block, page) = E17RoundTrip(font, "\u1C00\u1C27");
        Assert.Equal("\u1C00\u1C27", block);
        Assert.Equal("\u1C00\u1C27", page);
        Assert.Equal((Lp(0x1C2C), 500.0, -300.0), E17SamePositions(font, "\u1C00\u1C2C")[1]);
    }

    /// <summary>
    /// UNI 13.14.1 sentences 21–29 and 48–57: the medials -ra and -ya ligate with their consonant (pstf, psts of the font), ran U+1C36 sits on a
    /// final sign (mkmk on abvm). Before, pstf, psts and abvm were not applied.
    /// </summary>
    [Fact]
    public void E17_Lepcha_MedialLigaturesAndRanOnTheFinal()
    {
        var gsub = E17Layout("lepc", [("pstf", [0]), ("psts", [1])], E12Lig(([Lp(0x1C00), Lp(0x1C25)], E12F(0))), E12Lig(([E12F(0), Lp(0x1C24)], E12F(1))));
        var gpos = E17Layout("lepc", [("abvm", [0]), ("mkmk", [1])],
            E17Attach(4, Lp(0x1C2D), Lp(0x1C00), (0, 0), (500, 1400)), E17Attach(6, Lp(0x1C36), Lp(0x1C2D), (0, 0), (0, 350)));
        var font = E17Block(Lepcha, 0x50, gsub, gpos);
        E17Expect(font, "\u1C00\u1C25", E12F(0));
        E17Expect(font, "\u1C00\u1C25\u1C24", E12F(1));
        Assert.Equal((Lp(0x1C36), 500.0, 1750.0), E17SamePositions(font, "\u1C00\u1C2D\u1C36")[2]);
    }

    private const int Saurashtra = 0xA880;

    private static int Sr(int cp) => E12G(Saurashtra, cp);

    /// <summary>
    /// UNI 13.15.1 sentences 4–16: the virama U+A8C4 stays visible (three glyphs), a ZWJ after it asks for the conjunct kssa — a ligature of the
    /// font's cjct that names the ZWJ; the vowel signs follow the consonant (and haaru U+A8B4) in text order and take their form from psts.
    /// Before, cjct and psts were never applied.
    /// </summary>
    [Fact]
    public void E17_Saurashtra_VisibleViramaKssaWithZwjVowelSignsByPsts()
    {
        int ka = Sr(0xA892), virama = Sr(0xA8C4), ssa = Sr(0xA8B0);
        var gsub = E17Layout("saur", [("cjct", [0]), ("psts", [1])], E12Lig(([ka, virama, E12Zwj, ssa], E12F(0))), E11Sub((Sr(0xA8B5), E12F(1))));
        var font = E17Block(Saurashtra, 0x60, gsub);
        E17Expect(font, "\uA892\uA8C4\uA8B0", ka, virama, ssa);
        E17Expect(font, "\uA892\uA8C4\u200D\uA8B0", E12F(0));
        E17Expect(font, "\uA892\uA8B5", ka, E12F(1));
        E17Expect(font, "\uA892\uA8B4\uA8B5", ka, Sr(0xA8B4), E12F(1));
    }

    private const int MasaramGondi = 0x11D00, GunjalaGondi = 0x11D60;

    private static int Gm(int cp) => E12G(MasaramGondi, cp);

    private static int Gg(int cp) => E12G(GunjalaGondi, cp);

    /// <summary>
    /// UNI 13.16.1 sentences 14–31: in Masaram Gondi the consonants before the last one of a cluster take their bare form (half of the font, the
    /// virama without a glyph), the repha U+11D46 (typed first) moves behind its base, ra-kara U+11D47 sits below the consonant (blwm), Ra and
    /// virama stay bare before the next consonant where the font forms nothing of them (no repha from Ra). Before, half and blwm were not
    /// applied and the repha stayed first.
    /// </summary>
    [Fact]
    public void E17_MasaramGondi_BareFormsRephaAndRakara()
    {
        int ka = Gm(0x11D0C), virama = Gm(0x11D45), ta = Gm(0x11D1B), va = Gm(0x11D2F);
        var gsub = E17Layout("gonm", [("half", [0])], E12Lig(([ka, virama], E12F(0)), ([ta, virama], E12F(1))));
        var gpos = E17Layout("gonm", [("blwm", [0])], E17Attach(4, Gm(0x11D47), ka, (0, 0), (450, -100)));
        var font = E17Block(MasaramGondi, 0x60, gsub, gpos);
        E17Expect(font, "\U00011D0C\U00011D45\U00011D1B", E12F(0), ta);
        E17Expect(font, "\U00011D0C\U00011D45\U00011D1B\U00011D45\U00011D2F", E12F(0), E12F(1), va);
        E17Expect(font, "\U00011D46\U00011D0C", ka, Gm(0x11D46));
        Assert.Equal((Gm(0x11D47), 450.0, -100.0), E17SamePositions(font, "\U00011D0C\U00011D47")[1]);
        E17Expect(font, "\U00011D26\U00011D45\U00011D0C", Gm(0x11D26), virama, ka);
    }

    /// <summary>
    /// UNI 13.17.1 sentences 14–15 and 20–23: Gunjala Gondi has no special forms of Ra — Ra at the start of a cluster takes its half form (no
    /// repha); the consonants before the last one take their half forms, a consonant with -aa its ligature from pres. Before, half and pres were
    /// not applied.
    /// </summary>
    [Fact]
    public void E17_GunjalaGondi_HalfFormsAndPresLigature()
    {
        int ra = Gg(0x11D88), virama = Gg(0x11D97), ka = Gg(0x11D71), kha = Gg(0x11D72);
        var gsub = E17Layout("gong", [("half", [0]), ("pres", [1])], E12Lig(([ra, virama], E12F(0)), ([ka, virama], E12F(1))), E12Lig(([ka, Gg(0x11D8A)], E12F(2))));
        var font = E17Block(GunjalaGondi, 0x50, gsub);
        E17Expect(font, "\U00011D88\U00011D97\U00011D71", E12F(0), ka);
        E17Expect(font, "\U00011D71\U00011D97\U00011D72", E12F(1), kha);
        E17Expect(font, "\U00011D71\U00011D8A", E12F(2));
    }

    private const int GurungKhema = 0x16100;

    private static int Gk(int cp) => E12G(GurungKhema, cp);

    /// <summary>
    /// UNI 13.22.1 sentences 12–21 (the shaper's part): the vowel sign U+16121 is canonically U+1611E U+1611E — a font with only the composed
    /// glyph draws the parts typed as it, a font with only the parts draws the composed character as them. Before, the missing spelling was
    /// drawn with the empty glyph.
    /// </summary>
    [Fact]
    public void E17_GurungKhema_BothSpellingsOfAVowelLookTheSame()
    {
        var composedOnly = E17Font(null, null, (0x16100, 0x1611D, E12Base), (0x1611F, 0x1613F, E12Base + 0x1F));
        int[] composed = [Gk(0x16101), Gk(0x16121)];
        Assert.Equal(composed, E12Own(composedOnly, "\U00016101\U00016121"));
        Assert.Equal(composed, E12Own(composedOnly, "\U00016101\U0001611E\U0001611E"));
        var partsOnly = E17Font(null, null, (0x16100, 0x16120, E12Base), (0x16122, 0x1613F, E12Base + 0x22));
        int[] parts = [Gk(0x16101), Gk(0x1611E), Gk(0x1611E)];
        Assert.Equal(parts, E12Own(partsOnly, "\U00016101\U0001611E\U0001611E"));
        Assert.Equal(parts, E12Own(partsOnly, "\U00016101\U00016121"));
    }

    /// <summary>
    /// UNI 13.22.1 sentences 22–38: medials follow the consonant directly and make one cluster with it (U+16101 U+1612A U+1612C), tholhoma
    /// U+1612F stays visible without a conjunct, also after a medial (one cluster, no dotted circle), and the anusvara sits above or below the
    /// vowel sign as the font's anchors place it (mkmk on abvm) — on the last part of a vowel sign drawn in its canonical parts (U = AA + AA).
    /// The medial ya U+1612A standing left keeps its place — the model leaves pre-base medials to the font. Before, there were no clusters and
    /// abvm was not applied.
    /// </summary>
    [Fact]
    public void E17_GurungKhema_MedialsTholhomaAndAnusvara()
    {
        int ka = Gk(0x16101);
        var gpos = E17Layout("gukh", [("abvm", [0, 1]), ("mkmk", [2, 3])],
            E17Attach(4, Gk(0x1611F), ka, (0, 0), (400, 1400)), E17Attach(4, Gk(0x1611E), ka, (0, 0), (400, 1400)),
            E17Attach(6, Gk(0x1612D), Gk(0x1611F), (0, 0), (0, 300)), E17Attach(6, Gk(0x1612D), Gk(0x1611E), (0, 0), (0, -150)));
        var font = E17Block(GurungKhema, 0x40, null, gpos);
        Assert.Equal([ka, Gk(0x1612A)], E12Own(font, "\U00016101\U0001612A"));
        Assert.Single(E17Syllables(font, "\U00016101\U0001612A\U0001612C").Distinct());
        Assert.Equal([ka, Gk(0x1612F)], E12Own(font, "\U00016101\U0001612F"));
        Assert.Equal([ka, Gk(0x1612A), Gk(0x1612F)], E12Own(font, "\U00016101\U0001612A\U0001612F"));
        Assert.Single(E17Syllables(font, "\U00016101\U0001612A\U0001612F").Distinct());
        Assert.Equal(1700.0, E11Drawn(E12Shape(font, "\U00016101\U0001611F\U0001612D"))[2].Y);
        var u = E11Drawn(E12Shape(font, "\U00016101\U00016121\U0001612D"));
        Assert.Equal([ka, Gk(0x1611E), Gk(0x1611E), Gk(0x1612D)], u.Select(d => d.Glyph));
        Assert.Equal(1250.0, u[3].Y);
    }

    private const int KiratRai = 0x16D40;

    private static int Kr(int cp) => E12G(KiratRai, cp);

    /// <summary>
    /// UNI 13.23.1 sentences 5–20 (the shaper's part): Kirat Rai runs left to right without reordering; its vowel signs, letters with an advance,
    /// follow a consonant (two glyphs) — at the start of the text a vowel sign gets the dotted circle before it (after a space it takes the space
    /// as its base, as in all scripts of the model with the reference shaper); U+16D68 is
    /// canonically U+16D67 U+16D67 and a font with only U+16D68 draws both spellings alike; own text is stored as typed. Before, a lone vowel sign
    /// had no base and the parts were drawn with the empty glyph.
    /// </summary>
    [Fact]
    public void E17_KiratRai_VowelSignsFollowAConsonant_SpellingsAlike_StoredAsTyped()
    {
        var font = E17Block(KiratRai, 0x40, null);
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0x16D44));
        var shaped = E12Shape(font, "\U00016D44\U00016D63");
        Assert.Equal([Kr(0x16D44), Kr(0x16D63)], shaped.Select(g => g.Glyph));
        Assert.All(shaped, g => Assert.True(g.XAdvance > 0));
        Assert.Equal([E12Dotted, Kr(0x16D63)], E12Own(font, "\U00016D63"));
        Assert.Equal([Ot02G(' '), Kr(0x16D63)], E12Own(font, " \U00016D63"));
        var composedOnly = E17Font(null, null, (0x16D40, 0x16D66, E12Base), (0x16D68, 0x16D7F, E12Base + 0x28));
        Assert.Equal([Kr(0x16D44), Kr(0x16D68)], E12Own(composedOnly, "\U00016D44\U00016D67\U00016D67"));
        Assert.Equal("\U00016D44\U00016D6A", E17RoundTrip(font, "\U00016D44\U00016D6A").Block);
        Assert.Equal("\U00016D44\U00016D63\U00016D67\U00016D67", E17RoundTrip(font, "\U00016D44\U00016D63\U00016D67\U00016D67").Block);
    }
}
