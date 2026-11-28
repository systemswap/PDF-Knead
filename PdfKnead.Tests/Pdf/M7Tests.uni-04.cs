using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Fonts;
using JoinForm = PdfKnead.Core.Fonts.Layout.ArabicJoining.Form;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package uni-04 (The Unicode Standard 16.0.0, chapter 9: introduction, Hebrew 9.1, Arabic 9.2 and 9.2.1): the joining types of the
/// cursive and the unjoined scripts, Hebrew final forms drawn as typed, marks and signs of Hebrew and Arabic kept in the order typed when own
/// text is saved and read again, the Bidi classes of the shekel sign, the presentation forms and the digit sets, the Arabic fallback font for
/// the extended blocks and the signs spanning numbers. Test inputs with characters outside ASCII are written as escapes.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (uni-04)

    /// <summary>Liberation Sans with all its tables (Hebrew letters and points are in it).</summary>
    private static readonly OpenTypeFont Uni04Liberation = OpenTypeFont.Parse(ShaperLimitTests.Liberation);

    /// <summary>Noto Sans Arabic, the fallback for Arabic script in own text.</summary>
    private static readonly OpenTypeFont Uni04NotoArabic = EmbeddedFonts.ArabicOpenType("NotoSansArabic-Regular.ttf");

    /// <summary>The joining forms of the code points given.</summary>
    private static JoinForm[] Uni04Forms(params int[] codePoints) => ArabicJoining.Forms(codePoints);

    /// <summary>The code points of a text.</summary>
    private static int[] Uni04Points(string text) => [.. text.EnumerateRunes().Select(r => r.Value)];

    /// <summary>The text the extraction reads from the page the tests save on (before any element is added).</summary>
    private static readonly string Uni04BlankText = UniTestsPageText(TestPdf.Simple(1).Build()).Trim('\r', '\n');

    /// <summary>
    /// Own text saved on a blank page and opened again: the text of the block the page gives back, and what the extraction reads beyond the
    /// text of the blank page (line breaks at both ends removed).
    /// </summary>
    private static (string Block, string Page) Uni04RoundTrip(string text)
    {
        var saved = UniTestsSaved(UniTestsElement(text));
        string block;
        using (var doc = PdfDocument.Open(saved))
            block = Assert.Single(PageItem.From(doc)[0].Elements.OfType<RichTextElement>()).Content.Text;
        var page = UniTestsPageText(saved).Trim('\r', '\n');
        Assert.StartsWith(Uni04BlankText, page, StringComparison.Ordinal);
        return (block, page[Uni04BlankText.Length..].Trim('\r', '\n'));
    }

    /// <summary>
    /// Asserts that own text comes back as typed after saving — as the block and as the extracted page text — and that the mark given is
    /// drawn within the horizontal extent of its base, not after it as a glyph of its own (or together with it as one glyph of the font).
    /// </summary>
    private static void Uni04AssertKeptWithMarkOnBase(string text, string baseLetter, string mark)
    {
        var (block, page) = Uni04RoundTrip(text);
        Assert.Equal(text, block);
        Assert.Equal(text, page);
        var saved = UniTestsSaved(UniTestsElement(text));
        using var doc = PdfDocument.Open(saved);
        var glyphs = PageInterpreter.BuildText(doc, doc.Pages[0]);
        if (glyphs.Any(g => g.Text == baseLetter + mark)) return;
        var b = Assert.Single(glyphs, g => g.Text == baseLetter);
        var m = Assert.Single(glyphs, g => g.Text == mark);
        double left = Math.Min(b.X0, b.X1), right = Math.Max(b.X0, b.X1);
        Assert.True(right - left > 1, $"base {left}..{right}");
        Assert.InRange(m.X0, left - 0.5, right + 0.5);
    }

    // ------------------------------------------------------------ 9 introduction, 9.1 Hebrew

    /// <summary>
    /// UNI 9 sentences 24–27: Arabic, Syriac and Mandaic are cursive with forms by position, Hebrew and Samaritan stay unjoined — the joining
    /// types from ArabicShaping.txt: Syriac Beth dual-joining and Alaph right-joining, Mandaic Ab dual-joining and Halqa right-joining, Hebrew
    /// Alef and Samaritan Alaf not listed, non-joining.
    /// </summary>
    [Fact]
    public void Uni04_JoiningTypes_CursiveScriptsJoin_HebrewAndSamaritanDoNot()
    {
        Assert.Equal('D', ArabicJoining.TypeOf(0x0712));
        Assert.Equal('R', ArabicJoining.TypeOf(0x0710));
        Assert.Equal('D', ArabicJoining.TypeOf(0x0841));
        Assert.Equal('R', ArabicJoining.TypeOf(0x0840));
        Assert.Equal('U', ArabicJoining.TypeOf(0x05D0));
        Assert.Equal('U', ArabicJoining.TypeOf(0x0800));
        Assert.False(ArabicJoining.Joins(0x05D0));
        Assert.False(ArabicJoining.Joins(0x0800));
    }

    /// <summary>
    /// UNI 9.1.1 sentences 7–11: Hebrew, its cursive hand included, is never joined — every letter U+05D0 … U+05EA is non-joining, and the
    /// word „shalom“ takes no joining form at all.
    /// </summary>
    [Fact]
    public void Uni04_Hebrew_NeverJoined()
    {
        for (var c = 0x05D0; c <= 0x05EA; c++) Assert.Equal('U', ArabicJoining.TypeOf(c));
        Assert.All(Uni04Forms(Uni04Points("\u05E9\u05DC\u05D5\u05DD")), f => Assert.Equal(JoinForm.None, f));
    }

    /// <summary>
    /// UNI 9.1.1 sentences 37–40: shin is encoded as the letter U+05E9 followed by its dot U+05C1 — own text with this sequence is saved and
    /// read back as the same two characters, and the dot is drawn on its letter.
    /// </summary>
    [Fact]
    public void Uni04_ShinWithDot_SavedAndReadAsTyped()
    {
        Assert.NotEqual(0, Uni04Liberation.GlyphForUnicode(0x05C1));
        Uni04AssertKeptWithMarkOnBase("\u05E9\u05C1", "\u05E9", "\u05C1");
    }

    /// <summary>
    /// UNI 9.1.1 sentences 41–46: the five final forms are characters of their own and drawn one to one as typed, without analysing the
    /// context — „mayim“ typed with the normal mem at its end keeps the glyph of U+05DE there (not the final mem), and a final mem typed alone
    /// is drawn with the glyph of U+05DD.
    /// </summary>
    [Fact]
    public void Uni04_HebrewFinalForms_DrawnOneToOne()
    {
        var shaper = TextShaper.For(Uni04Liberation);
        int mem = Uni04Liberation.GlyphForUnicode(0x05DE), finalMem = Uni04Liberation.GlyphForUnicode(0x05DD);
        Assert.NotEqual(0, mem);
        Assert.NotEqual(mem, finalMem);
        var word = shaper.Shape("\u05DE\u05D9\u05DE");
        Assert.Equal(mem, word[^1].Glyph);
        Assert.Equal(mem, word[0].Glyph);
        Assert.Equal(finalMem, Assert.Single(shaper.Shape("\u05DD")).Glyph);
    }

    /// <summary>
    /// UNI 9.1.1 sentences 73–85: the meteg may stand left or right of the vowel and the order is kept with CGJ, since normalization would
    /// lose it — shaped with Liberation Sans, a letter with sheva and meteg, and a letter with meteg, CGJ and sheva keep their marks in the
    /// order typed, the CGJ without advance; saved and opened again both sequences come back unchanged as the block (own text is not
    /// normalized). The extracted page text is checked by <see cref="Uni04_OwnTextWithMarks_ExtractedWithEachMarkAfterItsBase"/>.
    /// </summary>
    [Fact]
    public void Uni04_Meteg_MarksInTextOrder_CgjKept()
    {
        var shaper = TextShaper.For(Uni04Liberation);
        Assert.Equal(["\u05D1", "\u05B0", "\u05BD"], shaper.Shape("\u05D1\u05B0\u05BD").Select(g => g.Text));
        var withCgj = shaper.Shape("\u05D1\u05BD\u034F\u05B0");
        Assert.Equal(["\u05D1", "\u05BD", "\u034F", "\u05B0"], withCgj.Select(g => g.Text));
        Assert.Equal(0, withCgj[2].XAdvance);
        foreach (var text in new[] { "\u05D1\u05B0\u05BD", "\u05D1\u05BD\u034F\u05B0" })
            Assert.Equal(text, Uni04RoundTrip(text).Block);
    }

    /// <summary>
    /// UNI 9.1.1 sentences 73–85, UNI 9.2.1 sentences 42–47 and UNI 9.2.5 sentences 12–16 (reading own text): the text extracted from a saved
    /// page keeps every mark after its base in the order typed — a letter with sheva and meteg, a letter with meteg, CGJ and sheva, beh with
    /// hamza above and beh with fatha come out as typed, on one line. Today the extraction puts Hebrew points before their letter and an
    /// Arabic mark on a line of its own before its letter (a letter with one Hebrew point above it, such as U+0307, comes out right).
    /// </summary>
    [Fact]
    public void Uni04_OwnTextWithMarks_ExtractedWithEachMarkAfterItsBase()
    {
        foreach (var text in new[] { "\u05D1\u05B0\u05BD", "\u05D1\u05BD\u034F\u05B0", "\u0628\u0654", "\u0628\u064E" })
            Assert.Equal(text, Uni04RoundTrip(text).Page);
    }

    /// <summary>
    /// UNI 9.1.1 sentences 123–128: numerical dots over letters are encoded with U+0307 (and U+0308), the puncta extraordinaria with U+05C4 —
    /// own text alef + U+0307 and bet + U+05C4 is saved and read back as typed, each mark drawn on its letter.
    /// </summary>
    [Fact]
    public void Uni04_DotsAboveLetters_SavedAndReadAsTyped()
    {
        Uni04AssertKeptWithMarkOnBase("\u05D0\u0307", "\u05D0", "\u0307");
        Uni04AssertKeptWithMarkOnBase("\u05D1\u05C4", "\u05D1", "\u05C4");
    }

    /// <summary>
    /// UNI 9.1.1 sentences 129–134: the inverted nun U+05C6 with the dot above it as U+0307 — own text with this sequence is saved and read
    /// back as the same two characters.
    /// </summary>
    [Fact]
    public void Uni04_NunHafukha_SavedAndReadAsTyped()
    {
        var (block, page) = Uni04RoundTrip("\u05C6\u0307");
        Assert.Equal("\u05C6\u0307", block);
        Assert.Equal("\u05C6\u0307", page);
    }

    /// <summary>
    /// UNI 9.1.1 sentences 135–136: the new shekel sign is a currency sign (Bidi class ET) — after a digit in a Hebrew paragraph it stays with
    /// the number: „shalom 5₪“ gives the digit and the sign level 2.
    /// </summary>
    [Fact]
    public void Uni04_ShekelSign_TerminatorWithTheDigit()
    {
        Assert.Equal(BidiClass.ET, Bidi.ClassOf(0x20AA));
        var levels = Bidi.Levels("\u05E9\u05DC\u05D5\u05DD 5\u20AA");
        Assert.Equal([1, 1, 1, 1, 1, 2, 2], levels);
    }

    /// <summary>
    /// UNI 9.1.2 sentences 1–5: the Hebrew presentation forms — shin with its dot as U+FB2A is right to left (level 1), and the alternative plus
    /// sign U+FB29 between two digits is a separator (ES) that takes the level of the digits around it.
    /// </summary>
    [Fact]
    public void Uni04_HebrewPresentationForms_RightToLeftAndSeparator()
    {
        Assert.Equal([1], Bidi.Levels("\uFB2A"));
        Assert.Equal(BidiClass.ES, Bidi.ClassOf(0xFB29));
        var levels = Bidi.Levels("\u05D0 1\uFB292");
        Assert.Equal([2, 2, 2], levels[2..]);
        Assert.NotEqual(0, Uni04Liberation.GlyphForUnicode(0xFB2A));
    }

    /// <summary>
    /// UNI 9.1.2 sentences 6–8: the alternative ayin U+FB20 is for data that replace the form in the text — PDF-Knead replaces nothing itself:
    /// ayin with a vowel below (U+05E2 U+05B8) keeps the glyph of U+05E2 when shaped with Liberation Sans.
    /// </summary>
    [Fact]
    public void Uni04_AlternativeAyin_NoSubstitutionByTheShaper()
    {
        var ayin = Uni04Liberation.GlyphForUnicode(0x05E2);
        Assert.NotEqual(0, ayin);
        var shaped = TextShaper.For(Uni04Liberation).Shape("\u05E2\u05B8");
        Assert.Equal(ayin, shaped[0].Glyph);
        Assert.Equal("\u05E2", shaped[0].Text);
    }

    // ------------------------------------------------------------ 9.2 Arabic, 9.2.1

    /// <summary>
    /// UNI 9.2.1 sentences 42–47 and UNI 9.2.5 sentences 12–16: hamza as a sound is encoded with the mark U+0654 after its letter — beh with
    /// hamza above, shaped with Noto Sans Arabic, gives the hamza attached to a glyph before it (no advance); own text with the sequence is
    /// saved and opened again as the same block. The extracted page text is checked by
    /// <see cref="Uni04_OwnTextWithMarks_ExtractedWithEachMarkAfterItsBase"/>.
    /// </summary>
    [Fact]
    public void Uni04_HamzaAboveAsMark_AttachedAndSavedAsTyped()
    {
        var shaped = TextShaper.For(Uni04NotoArabic).Shape("\u0628\u0654");
        var hamza = Assert.Single(shaped, g => g.Text == "\u0654");
        Assert.True(hamza.AttachBack >= 1);
        Assert.Equal(0, hamza.XAdvance);
        Assert.Equal("\u0628\u0654", Uni04RoundTrip("\u0628\u0654").Block);
    }

    /// <summary>
    /// UNI 9.2.1 sentences 97–101 (table 9-1): the Arabic-Indic digits are Arabic numbers (AN) and stand on level 2 after an Arabic word; the
    /// extended Arabic-Indic digits are European numbers (EN) and after Latin text stay on level 0.
    /// </summary>
    [Fact]
    public void Uni04_ArabicIndicDigits_ArabicNumbers_ExtendedDigitsEuropean()
    {
        Assert.Equal(BidiClass.AN, Bidi.ClassOf(0x0661));
        Assert.Equal(BidiClass.EN, Bidi.ClassOf(0x06F1));
        Assert.Equal(BidiClass.EN, Bidi.ClassOf('1'));
        var arabic = Bidi.Levels("\u0633\u0644\u0627\u0645 \u0661\u0662");
        Assert.Equal([2, 2], arabic[^2..]);
        var latin = Bidi.Levels("abc \u06F1\u06F2");
        Assert.All(latin, level => Assert.Equal(0, level));
    }

    /// <summary>
    /// UNI 9.2.1 sentences 111–113: the extended digits of Persian, Sindhi and Urdu take their direction from UAX #9 — after an Arabic word
    /// they stand on level 2 and keep their order (the first digit drawn left of the second).
    /// </summary>
    [Fact]
    public void Uni04_ExtendedArabicIndicDigits_AfterArabicLevel2InOrder()
    {
        var levels = Bidi.Levels("\u0633\u0644\u0627\u0645 \u06F1\u06F2");
        Assert.Equal([2, 2], levels[^2..]);
        var order = Bidi.VisualOrder(levels).ToList();
        Assert.True(order.IndexOf(5) < order.IndexOf(6));
    }

    /// <summary>
    /// UNI 9.2.1 sentences 114–117 and UNI 9.2.7 sentences 1–5: the extended Arabic letters of the supplement and Extended-A blocks — own text
    /// in Liberation Sans with U+0750 and U+08A0 is set with the Arabic fallback Noto Sans Arabic, and two dual-joining letters take their
    /// joining forms (the first another glyph than the letter alone).
    /// </summary>
    [Fact]
    public void Uni04_ExtendedArabicLetters_SetWithTheArabicFallback()
    {
        Assert.Equal(0, Uni04Liberation.GlyphForUnicode(0x0750));
        var glyphs = RichLayout.Of(UniTestsElement("\u0750\u0750 \u08A0")).Lines.SelectMany(l => l.Glyphs).ToList();
        foreach (var letter in new[] { "\u0750", "\u08A0" })
            Assert.All(glyphs.Where(g => g.Text == letter), g => Assert.Contains("NotoSansArabic", g.Face!.BaseName, StringComparison.Ordinal));
        Assert.Equal('D', ArabicJoining.TypeOf(0x0750));
        Assert.Equal('D', ArabicJoining.TypeOf(0x08A0));
        var face = glyphs.First(g => g.Text == "\u0750").Face!;
        var shaper = TextShaper.For(face.Font);
        var alone = shaper.Shape("\u0750").First(g => g.Text == "\u0750").Glyph;
        var joined = shaper.Shape("\u0750\u0750");
        Assert.Equal(JoinForm.Initial, joined.First(g => g.Text == "\u0750").Form);
        Assert.NotEqual(alone, joined.First(g => g.Text == "\u0750").Glyph);
    }

    /// <summary>
    /// UNI 9.2.1 sentences 118–123: the Quranic annotation marks carry meaning in their variants, so no mark is replaced by another — beh with
    /// the open fathatan U+08F0 and beh with the fathatan U+064B, shaped with Noto Sans Arabic, draw different mark glyphs.
    /// </summary>
    [Fact]
    public void Uni04_QuranicMarks_EachKeepsItsGlyph()
    {
        var shaper = TextShaper.For(Uni04NotoArabic);
        var open = Assert.Single(shaper.Shape("\u0628\u08F0"), g => g.Text == "\u08F0").Glyph;
        var plain = Assert.Single(shaper.Shape("\u0628\u064B"), g => g.Text == "\u064B").Glyph;
        Assert.NotEqual(0, open);
        Assert.NotEqual(0, plain);
        Assert.NotEqual(open, plain);
    }

    /// <summary>
    /// UNI 9.2.1 sentences 124–133: alef with wavy hamza is better the sequence alef + U+065F, but old data with U+0673 must still work — U+0673
    /// is drawn with glyphs of Noto Sans Arabic (the font splits it into alef and its hamza; together they stand for the character),
    /// alef + U+065F gives the alef and the mark without advance; both are saved and read back as typed.
    /// </summary>
    [Fact]
    public void Uni04_WavyHamzaAlef_DrawnAndReadAsTyped()
    {
        var shaper = TextShaper.For(Uni04NotoArabic);
        Assert.NotEqual(0, Uni04NotoArabic.GlyphForUnicode(0x0673));
        var old = shaper.Shape("\u0673");
        Assert.All(old, g => Assert.NotEqual(0, g.Glyph));
        Assert.Equal("\u0673", string.Concat(old.Select(g => g.Text)));
        var sequence = shaper.Shape("\u0627\u065F");
        Assert.Equal("\u0627", sequence[0].Text);
        Assert.NotEqual(0, sequence[0].Glyph);
        var mark = Assert.Single(sequence, g => g.Text == "\u065F");
        Assert.NotEqual(0, mark.Glyph);
        Assert.Equal(0, mark.XAdvance);
        foreach (var text in new[] { "\u0673", "\u0627\u065F" })
        {
            var (block, page) = Uni04RoundTrip(text);
            Assert.Equal(text, block);
            Assert.Equal(text, page);
        }
    }

    /// <summary>
    /// UNI 9.2.1 sentences 160–176: the signs spanning numbers U+0600–U+0605 are Arabic numbers (AN) in front of the digits and visible even
    /// alone — „year“ followed by the number sign and three Arabic-Indic digits gives the sign and the digits level 2; U+0600 alone has a
    /// glyph in Noto Sans Arabic.
    /// </summary>
    [Fact]
    public void Uni04_NumberSigns_ArabicNumberAndVisible()
    {
        for (var c = 0x0600; c <= 0x0605; c++) Assert.Equal(BidiClass.AN, Bidi.ClassOf(c));
        var levels = Bidi.Levels("\u0633\u0646\u0629 \u0600\u0661\u0662\u0663");
        Assert.Equal([2, 2, 2, 2], levels[^4..]);
        var alone = Assert.Single(TextShaper.For(Uni04NotoArabic).Shape("\u0600"));
        Assert.NotEqual(0, alone.Glyph);
        Assert.True(alone.XAdvance > 0);
    }

    /// <summary>
    /// UNI 9.2.1 sentences 196–209: the end of ayah U+06DD encloses the digits after it, the lookups of the font do it — shaped with Noto Sans
    /// Arabic, U+06DD with three Arabic-Indic digits draws the digits otherwise (other glyphs or other positions) than the same digits alone.
    /// </summary>
    [Fact]
    public void Uni04_EndOfAyah_DigitsShapedByTheFont()
    {
        var shaper = TextShaper.For(Uni04NotoArabic);
        var plain = shaper.Shape("\u0661\u0662\u0663");
        var enclosed = shaper.Shape("\u06DD\u0661\u0662\u0663");
        var digits = enclosed.Where(g => g.Text is "\u0661" or "\u0662" or "\u0663").ToArray();
        Assert.Equal(3, digits.Length);
        var same = plain.Zip(digits, (p, e) => p.Glyph == e.Glyph && p.XAdvance == e.XAdvance && p.XOffset == e.XOffset && p.YOffset == e.YOffset);
        Assert.Contains(false, same);
    }
}
