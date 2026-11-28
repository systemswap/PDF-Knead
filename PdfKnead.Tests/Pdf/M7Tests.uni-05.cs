using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;
using JoinForm = PdfKnead.Core.Fonts.Layout.ArabicJoining.Form;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package uni-05 (The Unicode Standard 16.0.0, chapter 9: Arabic 9.2.2 to 9.2.12): the joining types of table 9-3, the joining rules R2–R5
/// with tatweel, ZWNJ and the left-joining letters of other scripts, the joining types of the groups in tables 9-7 to 9-10, the hamza
/// sequences kept as typed, the lam with bar, the spacing nuktas and the word ligatures left to the font. Test inputs with characters outside
/// ASCII are written as escapes.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (uni-05)

    /// <summary>Asserts the joining type of every code point given.</summary>
    private static void Uni05AssertTypes(char type, params int[] codePoints)
    {
        foreach (var c in codePoints) Assert.True(ArabicJoining.TypeOf(c) == type, $"U+{c:X4}: {ArabicJoining.TypeOf(c)}, expected {type}");
    }

    /// <summary>Noto Sans Arabic with the GSUB given in place of its own (GPOS and GDEF kept).</summary>
    private static TextShaper Uni05NotoWithGsub(byte[] gsub) =>
        TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(Uni04NotoArabic.FileData, new Dictionary<string, byte[]?> { ["GSUB"] = gsub })));

    // ------------------------------------------------------------ 9.2.2 cursive joining

    /// <summary>
    /// UNI 9.2.2 sentences 13–24 (table 9-3): the joining types — tatweel and ZWJ join-causing (C); hamza, high hamza, ZWNJ, the signs spanning
    /// numbers and the end of ayah non-joining (U); fathatan, hamza below, superscript alef and the Syriac abbreviation mark transparent (T);
    /// the Manichaean heth left-joining (L).
    /// </summary>
    [Fact]
    public void Uni05_JoiningTypes_TableOfTheTypes()
    {
        Uni05AssertTypes('C', 0x0640, 0x200D);
        Uni05AssertTypes('U', 0x0621, 0x0674, 0x200C, 0x0600, 0x0601, 0x0602, 0x0603, 0x0604, 0x0605, 0x06DD);
        Uni05AssertTypes('T', 0x064B, 0x0655, 0x0670, 0x070F);
        Uni05AssertTypes('L', 0x10ACD);
        // C and U take no form of their own
        Assert.Equal([JoinForm.Initial, JoinForm.None], ArabicJoining.Forms([0x0628, 0x0640]));
        Assert.Equal([JoinForm.None, JoinForm.Isolated], ArabicJoining.Forms([0x0621, 0x0628]));
    }

    /// <summary>UNI 9.2.2 sentences 50–52 (R2): a right-joining letter after a join-causing character takes its final form — tatweel, alef → none, final.</summary>
    [Fact]
    public void Uni05_R2_RightJoiningAfterTatweel_Final()
    {
        Assert.Equal([JoinForm.None, JoinForm.Final], ArabicJoining.Forms([0x0640, 0x0627]));
    }

    /// <summary>
    /// UNI 9.2.2 sentence 53 (R3): a left-joining letter with a joining letter after it takes the form joined on that side — Manichaean heth
    /// (L) before aleph (D) → initial, final.
    /// </summary>
    [Fact]
    public void Uni05_R3_LeftJoiningBeforeAJoiningLetter_Initial()
    {
        Assert.Equal([JoinForm.Initial, JoinForm.Final], ArabicJoining.Forms([0x10ACD, 0x10AC0]));
    }

    /// <summary>UNI 9.2.2 sentences 54–56 (R4): a dual-joining letter between two tatweels takes its medial form — none, medial, none.</summary>
    [Fact]
    public void Uni05_R4_DualJoiningBetweenTatweels_Medial()
    {
        Assert.Equal([JoinForm.None, JoinForm.Medial, JoinForm.None], ArabicJoining.Forms([0x0640, 0x0645, 0x0640]));
    }

    /// <summary>
    /// UNI 9.2.2 sentences 57–58 (R5): a dual-joining letter with a joining neighbour only before it takes its final form — tatweel, meem →
    /// none, final; beh, beh → initial, final.
    /// </summary>
    [Fact]
    public void Uni05_R5_DualJoiningWithAJoiningLetterBeforeOnly_Final()
    {
        Assert.Equal([JoinForm.None, JoinForm.Final], ArabicJoining.Forms([0x0640, 0x0645]));
        Assert.Equal([JoinForm.Initial, JoinForm.Final], ArabicJoining.Forms([0x0628, 0x0628]));
    }

    /// <summary>
    /// UNI 9.2.2 sentences 62–64: the rules apply to other cursive scripts too, and ZWNJ prevents the joining — two Syriac beths join
    /// (initial, final); beh ZWNJ beh leaves both letters isolated.
    /// </summary>
    [Fact]
    public void Uni05_OtherCursiveScripts_AndZwnjBreaksTheJoining()
    {
        Assert.Equal([JoinForm.Initial, JoinForm.Final], ArabicJoining.Forms([0x0712, 0x0712]));
        Assert.Equal([JoinForm.Isolated, JoinForm.None, JoinForm.Isolated], ArabicJoining.Forms([0x0628, 0x200C, 0x0628]));
    }

    // ------------------------------------------------------------ 9.2.3 ligatures

    /// <summary>
    /// UNI 9.2.3 sentences 16–21: ligatures other than lam-alef are optional and a matter of the font — Noto Sans Arabic with a GSUB whose
    /// only feature is a discretionary ligature (dlig: lam meem → seen): without the feature the two letters stay two glyphs, with „dlig“
    /// switched on they become the ligature.
    /// </summary>
    [Fact]
    public void Uni05_OptionalLigatures_OnlyWhenSwitchedOn()
    {
        int lam = Uni04NotoArabic.GlyphForUnicode(0x0644), meem = Uni04NotoArabic.GlyphForUnicode(0x0645), seen = Uni04NotoArabic.GlyphForUnicode(0x0633);
        var gsub = Ot02Layout([new Ot02Script("arab", new Ot02Lang("", [0]))], [new Ot02Feature("dlig", [0])], [Ot02Lookup(4, Ot02Ligature(([lam, meem], seen)))]);
        var shaper = Uni05NotoWithGsub(gsub);
        Assert.Equal([lam, meem], shaper.Shape("\u0644\u0645").Select(g => g.Glyph));
        Assert.Equal([seen], shaper.Shape("\u0644\u0645", ["dlig"]).Select(g => g.Glyph));
    }

    // ------------------------------------------------------------ 9.2.4 joining groups

    /// <summary>
    /// UNI 9.2.4 sentences 14–33 (table 9-7, first part): the groups beh, noon, african noon, nya, yeh, farsi yeh, kashmiri yeh, thin yeh,
    /// burushaski yeh barree, sad, tah, ain, feh, african feh, qaf, african qaf are dual-joining.
    /// </summary>
    [Fact]
    public void Uni05_JoiningGroups_DualJoining_FirstPart()
    {
        Uni05AssertTypes('D', 0x0628, 0x0646, 0x08BD, 0x06BD, 0x064A, 0x06CC, 0x0620, 0x0886, 0x077A, 0x062D, 0x0633, 0x0635, 0x0637, 0x0639,
            0x0641, 0x08BB, 0x0642, 0x08BC, 0x0645);
    }

    /// <summary>UNI 9.2.4 sentences 34–40 (table 9-7, second part): heh, knotted heh, heh goal, kaf, swash kaf, gaf and lam are dual-joining.</summary>
    [Fact]
    public void Uni05_JoiningGroups_DualJoining_SecondPart()
    {
        Uni05AssertTypes('D', 0x0647, 0x06BE, 0x06C1, 0x0643, 0x06AA, 0x06AF, 0x0644);
    }

    /// <summary>
    /// UNI 9.2.4 sentences 41–54 (table 9-8): right-joining letters have two forms — alef, waw, straight waw, dal, reh, teh marbuta, teh marbuta
    /// goal, yeh with tail, yeh barree and rohingya yeh are right-joining, and such a letter is isolated or final, never initial or medial.
    /// </summary>
    [Fact]
    public void Uni05_JoiningGroups_RightJoining()
    {
        Uni05AssertTypes('R', 0x0627, 0x0648, 0x08B1, 0x062F, 0x0631, 0x0629, 0x06C3, 0x06CD, 0x06D2, 0x08AC);
        Assert.Equal([JoinForm.Isolated, JoinForm.Isolated], ArabicJoining.Forms([0x0648, 0x0648]));
        Assert.Equal([JoinForm.Initial, JoinForm.Final, JoinForm.Isolated], ArabicJoining.Forms([0x0628, 0x0648, 0x0628]));
    }

    /// <summary>
    /// UNI 9.2.4 sentences 55–59: letters only used at the end of a word, like teh marbuta, are right-joining; alef maksura stays dual-joining.
    /// </summary>
    [Fact]
    public void Uni05_TehMarbutaRightJoining_AlefMaksuraDualJoining()
    {
        Assert.Equal('R', ArabicJoining.TypeOf(0x0629));
        Assert.Equal('D', ArabicJoining.TypeOf(0x0649));
    }

    /// <summary>
    /// UNI 9.2.4 sentences 73–91 (table 9-10): the yeh forms — alef maksura, yeh, farsi yeh, thin yeh, U+0777, kashmiri yeh and U+077A are
    /// dual-joining, yeh barree and rohingya yeh right-joining (the dots of each form come from the font).
    /// </summary>
    [Fact]
    public void Uni05_YehForms_JoiningTypes()
    {
        Uni05AssertTypes('D', 0x0649, 0x064A, 0x06CC, 0x0886, 0x0777, 0x0620, 0x077A);
        Uni05AssertTypes('R', 0x06D2, 0x08AC);
    }

    /// <summary>UNI 9.2.4 sentences 99–107: yeh barree is right-joining, the Burushaski forms U+077A and U+077B dual-joining.</summary>
    [Fact]
    public void Uni05_YehBarreeRightJoining_BurushaskiDualJoining()
    {
        Assert.Equal('R', ArabicJoining.TypeOf(0x06D2));
        Assert.Equal('D', ArabicJoining.TypeOf(0x077A));
        Assert.Equal('D', ArabicJoining.TypeOf(0x077B));
    }

    /// <summary>
    /// UNI 9.2.4 sentences 121–131: noon ghunna U+06BA is shown as a dotless noon in all four forms and is dual-joining — three of them, shaped
    /// with Noto Sans Arabic, take the initial, medial and final forms, each another glyph than the isolated one.
    /// </summary>
    [Fact]
    public void Uni05_NoonGhunna_DualJoining_FourForms()
    {
        Assert.Equal('D', ArabicJoining.TypeOf(0x06BA));
        var shaper = TextShaper.For(Uni04NotoArabic);
        var isolated = shaper.Shape("\u06BA").Where(g => g.Text == "\u06BA").Select(g => g.Glyph).ToArray();
        var word = shaper.Shape("\u06BA\u06BA\u06BA").Where(g => g.Text == "\u06BA").ToArray();
        Assert.Equal(3, word.Length);
        Assert.Equal([JoinForm.Initial, JoinForm.Medial, JoinForm.Final], word.Select(g => g.Form));
        Assert.All(word, g => Assert.DoesNotContain(g.Glyph, isolated));
        Assert.Equal(3, word.Select(g => g.Glyph).Distinct().Count());
    }

    /// <summary>
    /// UNI 9.2.4 sentences 154–166: the joining types of the other cursive scripts are in ArabicShaping.txt as well — Syriac beth dual-joining,
    /// Mandaic halqa right-joining, Manichaean heth and Hanifi Rohingya a left-joining, Mongolian a dual-joining.
    /// </summary>
    [Fact]
    public void Uni05_OtherCursiveScripts_JoiningTypesFromTheFile()
    {
        Assert.Equal('D', ArabicJoining.TypeOf(0x0712));
        Assert.Equal('R', ArabicJoining.TypeOf(0x0840));
        Assert.Equal('L', ArabicJoining.TypeOf(0x10ACD));
        Assert.Equal('L', ArabicJoining.TypeOf(0x10D00));
        Assert.Equal('D', ArabicJoining.TypeOf(0x1820));
    }

    // ------------------------------------------------------------ 9.2.5 hamza

    /// <summary>
    /// UNI 9.2.5 sentences 17–29 (table 9-12): alef with hamza above typed precomposed (U+0623) and typed as alef + U+0654 — own text with each
    /// is saved and read back exactly as typed (nothing is composed or decomposed in the text).
    /// </summary>
    [Fact]
    public void Uni05_HamzaPrecomposedOrSequence_SavedAndReadAsTyped()
    {
        foreach (var text in new[] { "\u0623", "\u0627\u0654" })
        {
            var (block, page) = Uni04RoundTrip(text);
            Assert.Equal(text, block);
            Assert.Equal(text, page);
        }
    }

    /// <summary>
    /// UNI 9.2.5 sentences 30–37: the high hamza pairs are encoded with U+0674 first — high hamza and alef, shaped with Noto Sans Arabic, give two
    /// glyphs in text order (high hamza non-joining, alef isolated); own text with the pair is saved and read back as typed.
    /// </summary>
    [Fact]
    public void Uni05_HighHamzaPairs_TwoGlyphsInTextOrder_SavedAsTyped()
    {
        Assert.Equal('U', ArabicJoining.TypeOf(0x0674));
        Assert.Equal('R', ArabicJoining.TypeOf(0x0675));
        var shaped = TextShaper.For(Uni04NotoArabic).Shape("\u0674\u0627");
        Assert.Equal(["\u0674", "\u0627"], shaped.Select(g => g.Text));
        Assert.All(shaped, g => Assert.NotEqual(0, g.Glyph));
        var (block, page) = Uni04RoundTrip("\u0674\u0627");
        Assert.Equal("\u0674\u0627", block);
        Assert.Equal("\u0674\u0627", page);
    }

    /// <summary>
    /// UNI 9.2.5 sentences 46–51: the toothless yeh-hamza of Quranic spelling as tatweel + U+0654 (yasʾaluka) — seen, tatweel, hamza above, lam:
    /// the tatweel joins both neighbours and the mark is transparent → initial, none, none, final.
    /// </summary>
    [Fact]
    public void Uni05_TatweelWithHamza_JoinsBothNeighbours()
    {
        Assert.Equal([JoinForm.Initial, JoinForm.None, JoinForm.None, JoinForm.Final], ArabicJoining.Forms([0x0633, 0x0640, 0x0654, 0x0644]));
    }

    // ------------------------------------------------------------ 9.2.7 Arabic Supplement

    /// <summary>
    /// UNI 9.2.7 sentences 6–9: lam with bar U+076A is shaped like lam, including the ligature with the alef group — it is dual-joining, and
    /// lam with bar + alef shaped with Noto Sans Arabic gives other glyphs than the two letters unshaped.
    /// </summary>
    [Fact]
    public void Uni05_LamWithBar_ShapedLikeLam()
    {
        Assert.Equal('D', ArabicJoining.TypeOf(0x076A));
        var shaped = TextShaper.For(Uni04NotoArabic).Shape("\u076A\u0627").Select(g => g.Glyph).ToArray();
        int[] plain = [Uni04NotoArabic.GlyphForUnicode(0x076A), Uni04NotoArabic.GlyphForUnicode(0x0627)];
        Assert.NotEqual(0, plain[0]);
        Assert.NotEqual(plain, shaped);
    }

    // ------------------------------------------------------------ 9.2.11 presentation forms A

    /// <summary>
    /// UNI 9.2.11 sentences 13–23: the nuktas for teaching U+FBB2–U+FBC1 are spacing, right to left and not joining — U+FBB2 is non-joining
    /// and breaks the joining of two beh (isolated, none, isolated); all three characters stand on level 1.
    /// </summary>
    [Fact]
    public void Uni05_ArabicNuktas_SpacingNonJoining()
    {
        Assert.Equal('U', ArabicJoining.TypeOf(0xFBB2));
        Assert.Equal('U', ArabicJoining.TypeOf(0xFBC1));
        Assert.Equal([JoinForm.Isolated, JoinForm.None, JoinForm.Isolated], ArabicJoining.Forms([0x0628, 0xFBB2, 0x0628]));
        Assert.Equal(BidiClass.AL, Bidi.ClassOf(0xFBB2));
        Assert.Equal([1, 1, 1], Bidi.Levels("\u0628\uFBB2\u0628"));
    }

    /// <summary>
    /// UNI 9.2.11 sentences 24–30: the word ligature for Allah is better formed from alef, lam, lam, shadda, superscript alef, heh, and without
    /// shadda and superscript alef no ligature is formed — shaped with Noto Naskh Arabic, alef lam lam heh stays one glyph per letter, the
    /// spelling with U+0651 U+0670 becomes the font's ligature (one glyph for lam, lam, the marks and heh).
    /// </summary>
    [Fact]
    public void Uni05_AllahLigature_OnlyWithShaddaAndSuperscriptAlef()
    {
        var shaper = TextShaper.For(EmbeddedFonts.ArabicOpenType("NotoNaskhArabic-Regular.ttf"));
        var plain = shaper.Shape("\u0627\u0644\u0644\u0647");
        Assert.Equal(["\u0627", "\u0644", "\u0644", "\u0647"], plain.Select(g => g.Text));
        var marked = shaper.Shape("\u0627\u0644\u0644\u0651\u0670\u0647");
        Assert.Contains(marked, g => g.Text.Contains("\u0644\u0644", StringComparison.Ordinal) && g.Text.EndsWith('\u0647'));
        Assert.True(marked.Length < 6);
    }
}
