using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;
using JoinForm = PdfKnead.Core.Fonts.Layout.ArabicJoining.Form;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package uni-06 (The Unicode Standard 16.0.0, chapter 9: Syriac 9.3.1–9.3.3, Samaritan 9.4, Mandaic 9.5, Yezidi 9.6): the joining types
/// and forms of Syriac, Garshuni Malayalam and Mandaic from ArabicShaping.txt, ZWNJ for the right-joining forms, marks and ligatures of a
/// Syriac or Samaritan font applied under the script's own tag, own text with the marks kept as typed, Samaritan unjoined and Yezidi right to
/// left. No font for these scripts is shipped: the shaping tests use Liberation Sans with a cmap that maps the block to its Latin letters and
/// tables built by hand. Test inputs with characters outside ASCII are written as escapes.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (uni-06)

    /// <summary>The glyph the test font gives a character of the block starting at <paramref name="block"/> (the glyphs from „A“ on).</summary>
    private static int Uni06G(int block, int c) => Ot02G('A') + c - block;

    /// <summary>Liberation Sans with a cmap for the block (128 characters from <paramref name="block"/>, see <see cref="Uni06G"/>) and the GSUB and GPOS given.</summary>
    private static TextShaper Uni06Shaper(int block, byte[]? gsub, byte[]? gpos)
    {
        var cmap = new Ot02Bytes();
        cmap.U16(0).U16(1).U16(3).U16(10).U32(12);
        cmap.U16(12).U16(0).U32(16 + 12 * 2).U32(0).U32(2);
        cmap.U32(' ').U32(' ').U32(Ot02G(' '));
        cmap.U32(block).U32(block + 0x7F).U32(Ot02G('A'));
        return TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
        {
            ["cmap"] = cmap.ToArray(), ["GSUB"] = gsub, ["GPOS"] = gpos, ["GDEF"] = null, ["kern"] = null,
        })));
    }

    /// <summary>A GSUB or GPOS of one script whose default language system names the features given, one lookup each.</summary>
    private static byte[] Uni06Layout(string script, params (string Feature, Action<Ot02Bytes> Lookup)[] entries) =>
        Ot02Layout([new Ot02Script(script, new Ot02Lang("", [.. Enumerable.Range(0, entries.Length)]))],
            [.. entries.Select((e, i) => new Ot02Feature(e.Feature, [i]))], [.. entries.Select(e => e.Lookup)]);

    /// <summary>A mark-to-base lookup (type 4, format 1): the marks given (one class) on the base given, the mark anchor at (0, 0), the base anchor above.</summary>
    private static Action<Ot02Bytes> Uni06MarkToBase(int @base, params int[] marks) => Ot02Lookup(4, b =>
    {
        var start = b.Here;
        string mc = b.NewLabel(), bc = b.NewLabel(), ma = b.NewLabel(), ba = b.NewLabel(), mAnchor = b.NewLabel(), bAnchor = b.NewLabel();
        b.U16(1).Off16(mc, start).Off16(bc, start).U16(1).Off16(ma, start).Off16(ba, start);
        b.Label(mc);
        Ot02Coverage(b, marks);
        b.Label(bc);
        Ot02Coverage(b, [@base]);
        b.Label(ma);
        var markArray = b.Here;
        b.U16(marks.Length);
        foreach (var _ in marks) b.U16(0).Off16(mAnchor, markArray);
        b.Label(mAnchor).U16(1).U16(0).U16(0);
        b.Label(ba);
        var baseArray = b.Here;
        b.U16(1).Off16(bAnchor, baseArray);
        b.Label(bAnchor).U16(1).U16(300).U16(1400);
    });

    /// <summary>Asserts the joining type of every code point given.</summary>
    private static void Uni06AssertTypes(char type, params int[] codePoints)
    {
        foreach (var c in codePoints) Assert.True(ArabicJoining.TypeOf(c) == type, $"U+{c:X4}: {ArabicJoining.TypeOf(c)}, expected {type}");
    }

    /// <summary>Asserts that a mark shaped after its base is attached to it (base one glyph back, no advance, raised).</summary>
    private static void Uni06AssertAttached(ShapedGlyph[] shaped, int index)
    {
        Assert.True(shaped[index].AttachBack >= 1, $"AttachBack {shaped[index].AttachBack}");
        Assert.Equal(0, shaped[index].XAdvance);
        Assert.NotEqual(0, shaped[index].YOffset);
    }

    // ------------------------------------------------------------ 9.3 Syriac

    /// <summary>
    /// UNI 9.3.1 sentences 28–30 and UNI 9.3.2 sentences 4–7: Syriac is cursive with rules like those of Arabic, and every letter appears in
    /// one of its forms — beth, beth, alaph → initial, medial, final; shaped with a Syriac test font whose GSUB under „syrc“ has init, medi and
    /// fina (beth → 1, 2; alaph → 3), the word takes these glyphs.
    /// </summary>
    [Fact]
    public void Uni06_Syriac_EveryLetterInItsJoiningForm()
    {
        Assert.Equal([JoinForm.Initial, JoinForm.Medial, JoinForm.Final], ArabicJoining.Forms([0x0712, 0x0712, 0x0710]));
        int beth = Uni06G(0x0700, 0x0712), alaph = Uni06G(0x0700, 0x0710);
        var gsub = Uni06Layout("syrc", ("fina", Ot02Lookup(1, Ot02Single((alaph, Ot02G('3'))))), ("medi", Ot02Lookup(1, Ot02Single((beth, Ot02G('2'))))),
            ("init", Ot02Lookup(1, Ot02Single((beth, Ot02G('1'))))));
        var shaped = Uni06Shaper(0x0700, gsub, null).Shape("\u0712\u0712\u0710");
        Assert.Equal([Ot02G('1'), Ot02G('2'), Ot02G('3')], shaped.Select(g => g.Glyph));
    }

    /// <summary>
    /// UNI 9.3.1 sentences 85–92: Syriac marks have no width and sit over or under their letter, as the font places them — beth with the
    /// dot above U+0741, shaped with a Syriac test font whose GPOS under „syrc“ attaches the mark to beth: the mark is attached to the beth
    /// (one glyph back), without advance and raised.
    /// </summary>
    [Fact]
    public void Uni06_SyriacMark_AttachedWithoutAdvance()
    {
        var gpos = Uni06Layout("syrc", ("mark", Uni06MarkToBase(Uni06G(0x0700, 0x0712), Uni06G(0x0700, 0x0741))));
        var shaped = Uni06Shaper(0x0700, null, gpos).Shape("\u0712\u0741");
        Assert.Equal(2, shaped.Length);
        Uni06AssertAttached(shaped, 1);
    }

    /// <summary>
    /// UNI 9.3.1 sentences 106–110: for the right-joining form of semkath a ZWNJ follows it — beth, semkath, ZWNJ → initial, final, none
    /// (semkath and final semkath are dual-joining).
    /// </summary>
    [Fact]
    public void Uni06_Semkath_ZwnjForTheRightJoiningForm()
    {
        Uni06AssertTypes('D', 0x0723, 0x0724);
        Assert.Equal([JoinForm.Initial, JoinForm.Final, JoinForm.None], ArabicJoining.Forms([0x0712, 0x0723, 0x200C]));
    }

    /// <summary>
    /// UNI 9.3.1 sentences 114–131 (table 9-14): a dot between two letters is encoded with U+1DF8 — own text beth, U+1DF8, beth is saved and
    /// opened again as the same block (no Syriac font is shipped; the characters are drawn with the placeholder glyph). The extracted page
    /// text is checked by <see cref="Uni06_OwnTextWithoutAFont_ExtractedAsTyped"/>.
    /// </summary>
    [Fact]
    public void Uni06_SyriacDotBetweenLetters_SavedAndReadAsTyped()
    {
        Assert.Equal("\u0712\u1DF8\u0712", Uni04RoundTrip("\u0712\u1DF8\u0712").Block);
    }

    /// <summary>
    /// UNI 9.3.1 sentences 114–131, UNI 9.4.1 sentences 10–17 and 18–29 (reading own text): own text in a script without a shipped font —
    /// Syriac beth, U+1DF8, beth; Samaritan U+0828 alaf; alaf with U+0818 U+0819 — is extracted from the saved page as typed. Today every
    /// character drawn with the placeholder glyph reads as the text of the last of them (one ToUnicode entry for glyph 0, no /ActualText).
    /// </summary>
    [Fact]
    public void Uni06_OwnTextWithoutAFont_ExtractedAsTyped()
    {
        foreach (var text in new[] { "\u0712\u1DF8\u0712", "\u0828\u0800", "\u0800\u0818\u0819" })
            Assert.Equal(text, Uni04RoundTrip(text).Page);
    }

    /// <summary>
    /// UNI 9.3.1 sentences 132–136: Syriac uses characters of the Arabic block such as tatweel — beth before tatweel joins it (initial, none);
    /// the Arabic comma is a common separator, the Arabic semicolon and question mark are Arabic letters to UAX #9.
    /// </summary>
    [Fact]
    public void Uni06_SyriacWithTatweel_JoinsIt()
    {
        Assert.Equal([JoinForm.Initial, JoinForm.None], ArabicJoining.Forms([0x0712, 0x0640]));
        Assert.Equal(BidiClass.CS, Bidi.ClassOf(0x060C));
        Assert.Equal(BidiClass.AL, Bidi.ClassOf(0x061B));
        Assert.Equal(BidiClass.AL, Bidi.ClassOf(0x061F));
    }

    /// <summary>
    /// UNI 9.3.2 sentences 22–24: Malayalam lla U+0868 is dual-joining; a ZWNJ after it gives its right-joining form — nga, lla, ZWNJ, nga →
    /// initial, final, none, isolated.
    /// </summary>
    [Fact]
    public void Uni06_MalayalamLla_ZwnjForTheRightJoiningForm()
    {
        Assert.Equal('D', ArabicJoining.TypeOf(0x0868));
        Assert.Equal([JoinForm.Initial, JoinForm.Final, JoinForm.None, JoinForm.Isolated], ArabicJoining.Forms([0x0860, 0x0868, 0x200C, 0x0860]));
    }

    /// <summary>
    /// UNI 9.3.2 sentences 31–50 (table 9-16): beth, gamal, heth, teth, yudh, kaph, khaph, lamadh, mim, nun, semkath, final semkath, e, pe,
    /// reversed pe, fe, qaph, shin and Malayalam nga (with the Persian and Sogdian forms) are dual-joining.
    /// </summary>
    [Fact]
    public void Uni06_SyriacDualJoining_Table()
    {
        Uni06AssertTypes('D', 0x0712, 0x072D, 0x0713, 0x0714, 0x072E, 0x071A, 0x071B, 0x071C, 0x071D, 0x071F, 0x074E, 0x0720, 0x0721, 0x0722,
            0x0723, 0x0724, 0x0725, 0x0726, 0x0727, 0x074F, 0x0729, 0x072B, 0x0860);
    }

    /// <summary>UNI 9.3.2 sentences 51–59 (table 9-16, rest): the Garshuni Malayalam nya, tta, nna, nnna and lla are dual-joining.</summary>
    [Fact]
    public void Uni06_GarshuniMalayalam_DualJoining()
    {
        Uni06AssertTypes('D', 0x0862, 0x0863, 0x0864, 0x0865, 0x0868);
    }

    /// <summary>
    /// UNI 9.3.2 sentences 60–75 (table 9-17): dalath, rish, he, waw, zain, zhain, yudh he, sadhe, taw, Malayalam ra, llla and ssa are
    /// right-joining (with the Persian dhalath); Malayalam ja and bha do not join.
    /// </summary>
    [Fact]
    public void Uni06_SyriacRightJoiningAndNonJoining_Table()
    {
        Uni06AssertTypes('R', 0x0715, 0x072A, 0x0716, 0x072F, 0x0717, 0x0718, 0x0719, 0x074D, 0x071E, 0x0728, 0x072C, 0x0867, 0x0869, 0x086A);
        Uni06AssertTypes('U', 0x0861, 0x0866);
    }

    /// <summary>
    /// UNI 9.3.2 sentences 83–103 (table 9-19): the Syriac ligatures are the font's — a Syriac test font with rlig under „syrc“ (lamadh alaph →
    /// 1, taw alaph → 2): both pairs become the ligature glyph, with the text of both letters.
    /// </summary>
    [Fact]
    public void Uni06_SyriacLigatures_FromTheFontsRlig()
    {
        int lamadh = Uni06G(0x0700, 0x0720), taw = Uni06G(0x0700, 0x072C), alaph = Uni06G(0x0700, 0x0710);
        var gsub = Uni06Layout("syrc", ("rlig", Ot02Lookup(4, Ot02Ligature(([lamadh, alaph], Ot02G('1')), ([taw, alaph], Ot02G('2'))))));
        var shaper = Uni06Shaper(0x0700, gsub, null);
        var lamadhAlaph = Assert.Single(shaper.Shape("\u0720\u0710"));
        Assert.Equal(Ot02G('1'), lamadhAlaph.Glyph);
        Assert.Equal("\u0720\u0710", lamadhAlaph.Text);
        var tawAlaph = Assert.Single(shaper.Shape("\u072C\u0710"));
        Assert.Equal(Ot02G('2'), tawAlaph.Glyph);
        Assert.Equal("\u072C\u0710", tawAlaph.Text);
    }

    // ------------------------------------------------------------ 9.4 Samaritan

    /// <summary>
    /// UNI 9.4.1 sentence 5: Samaritan consonants form no ligatures and have no final forms — every letter U+0800 … U+0815 is non-joining,
    /// and a word of them takes no joining form.
    /// </summary>
    [Fact]
    public void Uni06_Samaritan_NeverJoined()
    {
        for (var c = 0x0800; c <= 0x0815; c++) Assert.Equal('U', ArabicJoining.TypeOf(c));
        Assert.All(ArabicJoining.Forms([0x0800, 0x0801, 0x0802]), f => Assert.Equal(JoinForm.None, f));
    }

    /// <summary>
    /// UNI 9.4.1 sentences 10–17: the vowel signs are combining and placed by the font, at the start of a word the spacing forms are used —
    /// alaf with the vowel sign U+081C, shaped with a Samaritan test font whose GPOS under „samr“ attaches it: attached, without advance; own
    /// text with the spacing U+0828 before alaf is saved and opened again as the same block (extracted text: see
    /// <see cref="Uni06_OwnTextWithoutAFont_ExtractedAsTyped"/>).
    /// </summary>
    [Fact]
    public void Uni06_SamaritanVowel_AttachedAndSpacingFormSavedAsTyped()
    {
        var gpos = Uni06Layout("samr", ("mark", Uni06MarkToBase(Uni06G(0x0800, 0x0800), Uni06G(0x0800, 0x081C))));
        var shaped = Uni06Shaper(0x0800, null, gpos).Shape("\u0800\u081C");
        Assert.Equal(2, shaped.Length);
        Uni06AssertAttached(shaped, 1);
        Assert.Equal("\u0828\u0800", Uni04RoundTrip("\u0828\u0800").Block);
    }

    /// <summary>
    /// UNI 9.4.1 sentences 18–29: the consonant modifiers — occlusion first, dagesh after it — are kept in the order typed: alaf with U+0818 and
    /// U+0819, shaped with a Samaritan test font that attaches both, keeps the order with both marks attached; own text with the sequence is
    /// saved and opened again as the same block (extracted text: see <see cref="Uni06_OwnTextWithoutAFont_ExtractedAsTyped"/>).
    /// </summary>
    [Fact]
    public void Uni06_SamaritanConsonantMarks_KeptInOrderAndAttached()
    {
        var gpos = Uni06Layout("samr", ("mark", Uni06MarkToBase(Uni06G(0x0800, 0x0800), Uni06G(0x0800, 0x0818), Uni06G(0x0800, 0x0819))));
        var shaped = Uni06Shaper(0x0800, null, gpos).Shape("\u0800\u0818\u0819");
        Assert.Equal(["\u0800", "\u0818", "\u0819"], shaped.Select(g => g.Text));
        Uni06AssertAttached(shaped, 1);
        Uni06AssertAttached(shaped, 2);
        Assert.Equal("\u0800\u0818\u0819", Uni04RoundTrip("\u0800\u0818\u0819").Block);
    }

    // ------------------------------------------------------------ 9.5 Mandaic

    /// <summary>
    /// UNI 9.5.1 sentences 20–23: dushenna, kad and ain are right-joining; ZWNJ before one of them gives its isolated form after a joining
    /// letter — ab, ZWNJ, dushenna → isolated, none, isolated.
    /// </summary>
    [Fact]
    public void Uni06_Mandaic_RightJoiningLetters_ZwnjBefore()
    {
        Uni06AssertTypes('R', 0x0856, 0x0857, 0x0858);
        Assert.Equal([JoinForm.Isolated, JoinForm.None, JoinForm.Isolated], ArabicJoining.Forms([0x0841, 0x200C, 0x0856]));
    }

    /// <summary>UNI 9.5.1 sentences 35–37: Mandaic joins fully — ab, ab, halqa → initial, medial, final.</summary>
    [Fact]
    public void Uni06_Mandaic_FullJoining()
    {
        Assert.Equal([JoinForm.Initial, JoinForm.Medial, JoinForm.Final], ArabicJoining.Forms([0x0841, 0x0841, 0x0840]));
    }

    /// <summary>UNI 9.5.1 sentences 38–55 (table 9-21): the 17 Mandaic letters U+0841–U+0845, U+0848, U+084A–U+0853 and U+0855 are dual-joining.</summary>
    [Fact]
    public void Uni06_MandaicDualJoining_Table()
    {
        Uni06AssertTypes('D', 0x0841, 0x0842, 0x0843, 0x0844, 0x0845, 0x0848, 0x084A, 0x084B, 0x084C, 0x084D, 0x084E, 0x084F, 0x0850, 0x0851,
            0x0852, 0x0853, 0x0855);
    }

    /// <summary>UNI 9.5.1 sentences 56–64 (table 9-22): halqa, az, it, aksa, ash, dushenna, kad and ain are right-joining.</summary>
    [Fact]
    public void Uni06_MandaicRightJoining_Table()
    {
        Uni06AssertTypes('R', 0x0840, 0x0846, 0x0847, 0x0849, 0x0854, 0x0856, 0x0857, 0x0858);
    }

    // ------------------------------------------------------------ 9.6 Yezidi

    /// <summary>
    /// UNI 9.6.1 sentences 6–8: Yezidi is a right-to-left alphabet without joining — two letters outside the BMP stand on level 1 (each of
    /// their two UTF-16 units) and are non-joining.
    /// </summary>
    [Fact]
    public void Uni06_Yezidi_RightToLeftNotJoined()
    {
        Assert.Equal([1, 1, 1, 1], Bidi.Levels("\U00010E80\U00010E81"));
        Assert.Equal('U', ArabicJoining.TypeOf(0x10E80));
        Assert.Equal('U', ArabicJoining.TypeOf(0x10E81));
    }
}
