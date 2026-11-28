using System.Globalization;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package uni-14 (Unicode chapter 17, the scripts of Indonesia and the Philippines): the rows of the draft that still asked for a test —
/// the virama of the Philippine scripts and the pamaaeh of Sundanese make no conjuncts, the directions from the character database, the
/// optional îya ligature of Buginese with ZWJ, the pangrangkep U+A9CF beside the Arabic digit two, text stored as typed, and lines of text
/// without word spaces broken only after whole syllables. Fonts are built as in <c>M7Tests.e17b.cs</c> (Liberation Sans with a cmap for the
/// block and hand-made GSUB) and, where it knows the script, compared with the bundled shaping library. Inputs with marks are escapes.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (uni-14)

    /// <summary>Makes a built font usable as a family of its own for own text and gives back the family name.</summary>
    private static string Uni14Family(byte[] font)
    {
        var family = "Uni14 " + Guid.NewGuid().ToString("N");
        E11UseFont(family, OpenTypeFont.Parse(font), false);
        return family;
    }

    /// <summary>The layout of <paramref name="text"/> in <paramref name="family"/> (size 10) in a block of the width given.</summary>
    private static RichLayout Uni14Layout(string family, string text, double width) =>
        RichLayout.Layout(new RichText(text, new RichStyle { Family = family, Size = 10 }), width, BlockAlignment.Left, 1.15, null, 0);

    /// <summary>Whether a code point is a combining mark (Mn, Mc, Me).</summary>
    private static bool Uni14IsMark(Rune r) => Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
        or UnicodeCategory.EnclosingMark;

    /// <summary>
    /// The levels of <paramref name="word"/> after a Hebrew word (a right-to-left paragraph) must all be 2 and the word must keep its order on
    /// the line (left to right within the right-to-left line).
    /// </summary>
    private static void Uni14Level2AfterHebrew(string word)
    {
        var text = "\u05D0\u05D1 " + word;
        var levels = Bidi.Levels(text);
        Assert.All(levels.Skip(3), l => Assert.Equal(2, l));
        var order = Bidi.VisualOrder(levels);
        var positions = Enumerable.Range(3, word.Length).Select(i => Array.IndexOf(order, i)).ToArray();
        Assert.Equal(positions.Order(), positions);
    }

    // ------------------------------------------------------------ 17.1 Philippine scripts

    /// <summary>
    /// UNI 17.1.2 sentence 21: the Tagalog virama U+1714 and the pamudpod U+1715 only take away the inherent vowel, they do not join letters —
    /// Ka, virama (or pamudpod), Ka in a font whose blwf lookup would make the virama and Ka a below form stays three glyphs in text order, with
    /// the own shaper and with the shaping library.
    /// </summary>
    [Fact]
    public void Uni14_Tagalog_ViramaAndPamudpodMakeNoBelowForm()
    {
        int ka = E12G(0x1700, 0x1703), virama = E12G(0x1700, 0x1714), pamudpod = E12G(0x1700, 0x1715);
        var gsub = E17Layout("tglg", [("blwf", [0])], E12Lig(([virama, ka], E12F(0)), ([pamudpod, ka], E12F(1))));
        var font = E17Block(0x1700, 0x20, gsub);
        E17Expect(font, "\u1703\u1714\u1703", ka, virama, ka);
        E17Expect(font, "\u1703\u1715\u1703", ka, pamudpod, ka);
    }

    /// <summary>
    /// UNI 17.1.2 sentences 22–23, 17.6.1 sentence 5, 17.8.1 sentences 4–8: the Philippine scripts, Batak and Makasar run left to right — their
    /// letters (Tagalog U+1700–1711, Hanunóo U+1720–1731, Buhid U+1740–1751, Tagbanwa U+1760–1770) have the Bidi class L, all characters of
    /// Batak U+1BC0–1BF3 and Makasar U+11EE0–11EF8 L or NSM; a word of each after a Hebrew word stands at level 2 and keeps its order.
    /// </summary>
    [Fact]
    public void Uni14_ScriptsOfChapter17_LeftToRight_Level2AfterHebrew()
    {
        foreach (var (first, last) in new[] { (0x1700, 0x1711), (0x1720, 0x1731), (0x1740, 0x1751), (0x1760, 0x1770) })
            for (var c = first; c <= last; c++)
                Assert.Equal(BidiClass.L, Bidi.ClassOf(c));
        foreach (var (first, last) in new[] { (0x1BC0, 0x1BF3), (0x11EE0, 0x11EF8) })
            for (var c = first; c <= last; c++)
                Assert.Contains(Bidi.ClassOf(c), new[] { BidiClass.L, BidiClass.NSM });
        Uni14Level2AfterHebrew("\u1703\u1714\u1704");
        Uni14Level2AfterHebrew("\u1723\u1732\u1724");
        Uni14Level2AfterHebrew("\u1BC2\u1BE9\u1BC7\u1BF2");
        Uni14Level2AfterHebrew("\U00011EE0\U00011EF3\U00011EE7");
    }

    // ------------------------------------------------------------ 17.2 Buginese

    /// <summary>
    /// UNI 17.2.1 sentences 17–22: the îya ligature is optional and asked for with ZWJ — in a font whose ligature (liga) is A U+1A15, vowel sign
    /// i U+1A17, ZWJ, Ya U+1A10, the text with ZWJ gives the one ligature glyph, the text without it the three glyphs; the shaping library the
    /// same.
    /// </summary>
    [Fact]
    public void Uni14_Buginese_IyaLigatureOnlyWithZwj()
    {
        int a = E12G(0x1A00, 0x1A15), i = E12G(0x1A00, 0x1A17), ya = E12G(0x1A00, 0x1A10);
        var gsub = E17Layout("bugi", [("liga", [0])], E12Lig(([a, i, E12Zwj, ya], E12F(0))));
        var font = E17Block(0x1A00, 0x20, gsub);
        E17Expect(font, "\u1A15\u1A17\u200D\u1A10", E12F(0));
        E17Expect(font, "\u1A15\u1A17\u1A10", a, i, ya);
    }

    /// <summary>
    /// UNI 17.2.1 sentences 30–32 and 17.4.1 sentences 53–56: the pangrangkep U+A9CF is a left-to-right character (L), the Arabic-Indic digit
    /// two U+0662 an Arabic number (AN) — Buginese with U+A9CF stays at level 0, with U+0662 the digit stands at level 2; Javanese, U+A9CF,
    /// Javanese after a Hebrew word stand at level 2 in text order; own Buginese text with U+A9CF is saved and read back as typed.
    /// </summary>
    [Fact]
    public void Uni14_Pangrangkep_LeftToRight_ArabicDigitTwoAnArabicNumber()
    {
        Assert.Equal(BidiClass.L, Bidi.ClassOf(0xA9CF));
        Assert.Equal(BidiClass.AN, Bidi.ClassOf(0x0662));
        Assert.All(Bidi.Levels("\u1A00\u1A01\uA9CF"), l => Assert.Equal(0, l));
        Assert.Equal([0, 0, 2], Bidi.Levels("\u1A00\u1A01\u0662"));
        Uni14Level2AfterHebrew("\uA98F\uA9CF\uA98F");
        var font = E17Font(null, null, (0x1A00, 0x1A1F, E12Base), (0xA9CF, 0xA9CF, E12Base + 0x20));
        var (block, page) = E17RoundTrip(font, "\u1A00\u1A01\uA9CF");
        Assert.Equal("\u1A00\u1A01\uA9CF", block);
        Assert.Equal("\u1A00\u1A01\uA9CF", page);
    }

    // ------------------------------------------------------------ line breaks in scripts without word spaces

    /// <summary>
    /// Lays out the syllables (separated by „|“) five times without spaces in blocks from the width of the widest syllable to three times the
    /// width of all of them: every line must start at a syllable boundary (never before a mark, never after a virama or conjoiner), the lines
    /// follow each other without a gap and no glyph is added (no hyphen).
    /// </summary>
    private static void Uni14BreaksAfterWholeSyllables(int block, int length, string syllables)
    {
        var family = Uni14Family(E17Block(block, length, null));
        var parts = syllables.Split('|');
        var text = string.Concat(Enumerable.Repeat(string.Concat(parts), 5));
        var boundaries = new HashSet<int>();
        for (int at = 0, k = 0; at < text.Length; at += parts[k++ % parts.Length].Length) boundaries.Add(at);
        double Width(string s) => Assert.Single(Uni14Layout(family, s, 100000).Lines).Width;
        var whole = Assert.Single(Uni14Layout(family, text, 100000).Lines);
        var widest = parts.Max(Width);
        var unit = Width(string.Concat(parts));
        var breaks = 0;
        for (var width = widest + 0.01; width <= 3 * unit; width += unit / 7)
        {
            var lines = Uni14Layout(family, text, width).Lines;
            Assert.Equal(0, lines[0].Start);
            Assert.Equal(text.Length, lines[^1].End);
            for (var k = 0; k + 1 < lines.Count; k++) Assert.Equal(lines[k].End, lines[k + 1].Start);
            Assert.Equal(whole.Glyphs.Count, lines.Sum(l => l.Glyphs.Count));
            Assert.All(lines, l => Assert.Contains(l.Start, boundaries));
            breaks += lines.Count - 1;
        }
        Assert.True(breaks > 0);
    }

    /// <summary>
    /// UNI 17.3.1 sentences 96–99, 17.4.1 sentences 57–62, 17.7.1 sentence 45, 17.8.1 sentences 40–41, 17.9.1 sentences 101–103 (own texts):
    /// Balinese (Ka with adeg adeg and Ya, Ka with suku), Javanese (Ka with tarung, Ka with pangkon and Ya), Sundanese (Ka with pamaaeh, Ma
    /// with panolong, Ka with virama and Ka), Makasar (Ka with the vowel sign O) and Kawi (Ka with the conjoiner, Ka and Aa) without spaces in
    /// narrow blocks of several lines — every line ends after a whole syllable, never between a consonant and its vowel sign, killer or
    /// virama, never between a virama or conjoiner and the consonant after it; no hyphen is added. Pameneng U+1B60 stays a typed character
    /// (nothing inserts it).
    /// </summary>
    [Theory]
    [InlineData(0x1B00, 0x80, "\u1B13|\u1B13\u1B44\u1B2C|\u1B13\u1B38|\u1B13")]
    [InlineData(0xA980, 0x60, "\uA98F\uA9B4|\uA98F\uA9C0\uA9AA|\uA98F")]
    [InlineData(0x1B80, 0x40, "\u1B8A\u1BAA|\u1B99\u1BA7|\u1B8A\u1BAB\u1B8A")]
    [InlineData(0x11EE0, 0x20, "\U00011EE0\U00011EF6|\U00011EE7|\U00011EE0")]
    [InlineData(0x11F00, 0x60, "\U00011F12\U00011F42\U00011F12\U00011F34|\U00011F26")]
    public void Uni14_SyllabicScripts_HardBreakAfterWholeSyllables_NoHyphen(int block, int length, string syllables) =>
        Uni14BreaksAfterWholeSyllables(block, length, syllables);

    /// <summary>
    /// UNI 17.3.1 sentences 96–99, 17.4.1 sentences 57–62, 17.7.1 sentence 45, 17.8.1 sentences 40–41, 17.9.1 sentences 101–103 (own texts),
    /// with a vowel sign drawn before its consonant: Balinese Ka with taling, Ka with adeg adeg and Ya, Ka (the input of the draft); Javanese Ka
    /// with taling and tarung; Makasar Ka with E; Kawi Ka with E and Aa; Sundanese Ka with panaelaeng — every line must still end after a whole
    /// syllable. The shaper draws the reordered syllable as the vowel sign's glyph carrying the text of the syllable and the consonant's glyph
    /// without text; the hard break does not accept the boundary after a glyph without text, finds no other place, takes the line for one
    /// cluster and cuts where the width ends — inside the next syllable, before adeg adeg or after the consonant (docs/FEHLERGEDAECHTNIS.md,
    /// No. 1185, <c>RichLayout.BreakAllowedBefore</c>).
    /// </summary>
    [Theory]
    [InlineData(0x1B00, 0x80, "\u1B13\u1B3E|\u1B13\u1B44\u1B2C|\u1B13")]
    [InlineData(0xA980, 0x60, "\uA98F\uA9BA\uA9B4|\uA98F\uA9C0\uA9AA|\uA98F")]
    [InlineData(0x11EE0, 0x20, "\U00011EE0\U00011EF5|\U00011EE7\U00011EF6|\U00011EE0")]
    [InlineData(0x11F00, 0x60, "\U00011F12\U00011F3E\U00011F34|\U00011F12\U00011F42\U00011F12|\U00011F26")]
    [InlineData(0x1B80, 0x40, "\u1B8A\u1BA6|\u1B8A\u1BAB\u1B8A|\u1B99")]
    public void Uni14_SyllabicScripts_HardBreakAfterAReorderedSyllable(int block, int length, string syllables) =>
        Uni14BreaksAfterWholeSyllables(block, length, syllables);

    /// <summary>
    /// UNI 17.6.1 sentences 29–32 (own texts): Batak Ta, I, Pa, pangolat twice without spaces in a block too narrow for the four but wide enough
    /// for each half — the vowel sign stays on one line with the syllable it is drawn after: no line begins with Pa (the four characters run
    /// over the edge rather than break between I and Pa: UAX #14 LB28a keeps an aksara before an aksara with a final virama, also for the
    /// emergency break); the line breaks between the two groups (pangolat, class VF, before Ta: LB31). Before the line was broken between the
    /// vowel sign and Pa (docs/FEHLERGEDAECHTNIS.md, No. 1196).
    /// </summary>
    [Fact]
    public void Uni14_Batak_VowelSignStaysWithKilledFinal()
    {
        var family = Uni14Family(E17Block(Batak, 0x40, null));
        double Width(string s) => Assert.Single(Uni14Layout(family, s, 100000).Lines).Width;
        var width = Math.Max(Width("ᯖ\u1BEA"), Width("ᯇ\u1BF2")) + 0.1;
        Assert.True(Width("ᯖ\u1BEAᯇ\u1BF2") > width);
        var lines = Uni14Layout(family, "ᯖ\u1BEAᯇ\u1BF2ᯖ\u1BEAᯇ\u1BF2", width).Lines;
        Assert.Equal([0, 4], lines.Select(l => l.Start));
    }

    // ------------------------------------------------------------ 17.7 Sundanese

    /// <summary>
    /// UNI 17.7.1 sentence 17: the pamaaeh U+1BAA takes away the vowel without making a conjunct — Ka, pamaaeh, Ka in a font whose blwf lookup
    /// would make pamaaeh and Ka a below form stays three glyphs in text order, with the own shaper and with the shaping library.
    /// </summary>
    [Fact]
    public void Uni14_Sundanese_PamaaehMakesNoConjunct()
    {
        int ka = Sd(0x1B8A), pamaaeh = Sd(0x1BAA);
        var font = E17Block(Sundanese, 0x40, E17Layout("sund", [("blwf", [0])], E12Lig(([pamaaeh, ka], E12F(0)))));
        E17Expect(font, "\u1B8A\u1BAA\u1B8A", ka, pamaaeh, ka);
    }

    /// <summary>
    /// UNI 17.7.1 sentences 24–27: own text Ma U+1B99 with pamaaeh is saved and read back as typed, and a typed final m U+1BBF stays U+1BBF
    /// (nothing replaces one spelling by the other).
    /// </summary>
    [Fact]
    public void Uni14_Sundanese_MaPamaaehAndFinalM_StoredAsTyped()
    {
        var font = E17Block(Sundanese, 0x40, null);
        foreach (var typed in new[] { "\u1B99\u1BAA", "\u1B8A\u1BBF" })
        {
            var (block, page) = E17RoundTrip(font, typed);
            Assert.Equal(typed, block);
            Assert.Equal(typed, page);
        }
    }

    // ------------------------------------------------------------ 17.8 Makasar

    /// <summary>
    /// UNI 17.8.1 sentence 42: the end mark „tammat“ written with the Arabic letters teh, meem, shadda, teh — own text is saved and the block
    /// read back as typed, and the letters are drawn joined (initial teh, medial meem and final teh each differ from the glyph of the letter
    /// alone).
    /// </summary>
    [Fact]
    public void Uni14_Makasar_TammatInArabicLetters_StoredAsTypedAndJoined()
    {
        const string tammat = "\u062A\u0645\u0651\u062A";
        var element = UniTestsElement(tammat);
        using (var doc = PdfDocument.Open(UniTestsSaved(element)))
            Assert.Equal(tammat, Assert.Single(PageItem.From(doc)[0].Elements.OfType<RichTextElement>()).Content.Text);
        int Alone(string letter) => RichLayout.Of(UniTestsElement(letter)).Lines.Single().Glyphs.First(g => g.Text == letter).Glyph;
        var glyphs = RichLayout.Of(element).Lines.Single().Glyphs;
        var tehs = glyphs.Where(g => g.Text == "\u062A").OrderBy(g => g.CharStart).ToList();
        Assert.Equal(2, tehs.Count);
        Assert.All(tehs, t => Assert.NotEqual(Alone("\u062A"), t.Glyph));
        Assert.NotEqual(tehs[0].Glyph, tehs[1].Glyph);
        Assert.NotEqual(Alone("\u0645"), glyphs.Single(g => g.Text.StartsWith('\u0645')).Glyph);
    }
}
