using System.Diagnostics;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package E21: a letter and the mark immediately after it that compose canonically are drawn as the composed letter where the font has it
/// (UAX #53, 3.2; Unicode 18.4.1; docs/FEHLERGEDAECHTNIS.md, No. 1193) — in every script without syllabic shaping as canonical composition
/// does it (Unicode 3.11; No. 1197) —, the Kangxi and CJK radicals are folded to their equivalent ideographs in
/// the search (Unicode 18.1.14, Equivalent_Unified_Ideograph; No. 1194), and the caret moves by extended grapheme clusters (UAX #29 as the
/// runtime gives them; Unicode 13.23.1; No. 1195).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ 1. composing for display (FG 1193)

    /// <summary>The Arabic sequences of UnicodeData.txt 16.0.0 that are canonically equivalent to a precomposed letter: letter, mark, composed letter.</summary>
    private static readonly (int Letter, int Mark, int Composed)[] E21ArabicCompositions =
    [
        (0x0627, 0x0653, 0x0622), (0x0627, 0x0654, 0x0623), (0x0648, 0x0654, 0x0624), (0x0627, 0x0655, 0x0625),
        (0x064A, 0x0654, 0x0626), (0x06D5, 0x0654, 0x06C0), (0x06C1, 0x0654, 0x06C2), (0x06D2, 0x0654, 0x06D3),
    ];

    /// <summary>
    /// Liberation Sans with the cmap of <c>E16Font</c> (U+0600 … U+06FF to glyphs 100 …) but without U+0626 — a font without the glyph of yeh
    /// with hamza above —, without layout tables.
    /// </summary>
    private static readonly Lazy<OpenTypeFont> E21ArabicWithoutYehHamza = new(() => OpenTypeFont.Parse(ColorFontBuilder.WithTables(ShaperLimitTests.Liberation,
        new Dictionary<string, byte[]?>
        {
            ["cmap"] = E11Cmap((' ', ' ', Ot02G(' ')), (0x0600, 0x0625, 100), (0x0627, 0x06FF, 100 + 0x27)),
            ["GSUB"] = null, ["GPOS"] = null, ["GDEF"] = null, ["kern"] = null,
        })));

    /// <summary>The characters of the glyphs <paramref name="font"/> gives in drawing order (glyph numbers as in <c>E16Font</c>).</summary>
    private static string E21Drawn(OpenTypeFont font, string text) =>
        string.Concat(TextShaper.For(font).Shape(text).Select(g => g.Glyph is >= 100 and < 356 ? char.ConvertFromUtf32(0x0600 + g.Glyph - 100) : "?"));

    /// <summary>
    /// UAX53 3.2 sentences 9–11 (Kann) and UNI 9.2.5 sentences 4–6: each of the eight Arabic letters with the mark immediately after it that
    /// compose canonically (UnicodeData.txt 16.0.0) is drawn with the glyph of the composed letter where the font has it — yeh with hamza above
    /// as U+0626, without the dots of yeh —, one glyph that carries the text as typed; also when AMTRA puts the hamza first (yeh, fatha, hamza:
    /// the hamza is a modifier mark and moves before the fatha); in a font without U+0626 the sequence keeps its glyphs. Before, every sequence
    /// kept the glyphs of its characters.
    /// </summary>
    [Fact]
    public void E21_ArabicSequenceEquivalentToALetter_DrawnAsTheComposedLetter()
    {
        foreach (var (letter, mark, composed) in E21ArabicCompositions)
        {
            var typed = char.ConvertFromUtf32(letter) + char.ConvertFromUtf32(mark);
            Assert.Equal(char.ConvertFromUtf32(composed), E16Drawn(typed));
            Assert.Equal([typed], TextShaper.For(E16Arabic.Value).Shape(typed).Select(g => g.Text));
            Assert.Equal(char.ConvertFromUtf32(composed), typed.Normalize(NormalizationForm.FormC));
        }
        Assert.Equal("\u0626\u064E", E16Drawn("\u064A\u0654\u064E"));
        Assert.Equal("\u0626\u064E", E16Drawn("\u064A\u064E\u0654"));
        Assert.Equal("\u0628\u0626\u0628", E16Drawn("\u0628\u064A\u0654\u0628"));
        Assert.Equal("\u064A\u0654", E21Drawn(E21ArabicWithoutYehHamza.Value, "\u064A\u0654"));
        Assert.Equal("\u0623", E21Drawn(E21ArabicWithoutYehHamza.Value, "\u0627\u0654"));
    }

    /// <summary>
    /// UAX53 3.2 sentences 12–13: only the mark immediately after the letter is composed with it, no mark is skipped — alef, superscript alef,
    /// madda stays three glyphs (not alef-madda with superscript alef); alef, madda, fatha is ordered alef, fatha, madda (fatha of class 30
    /// first) and not composed, the same picture as alef-madda with fatha (drawn decomposed, No. 1180); a letter that is composed already keeps
    /// its glyph.
    /// </summary>
    [Fact]
    public void E21_Composition_OnlyWithTheMarkImmediatelyAfter()
    {
        Assert.Equal("\u0627\u0670\u0653", E16Drawn("\u0627\u0670\u0653"));
        Assert.Equal("\u0627\u064E\u0653", E16Drawn("\u0627\u0653\u064E"));
        Assert.Equal(E16Drawn("\u0622\u064E"), E16Drawn("\u0627\u0653\u064E"));
        Assert.Equal("\u0626\u064E", E16Drawn("\u0626\u064E"));
        Assert.Equal("\u0622\u0654", E16Drawn("\u0627\u0653\u0654"));
    }

    /// <summary>
    /// UAX53 0 sentences 13–16 (the stored text is not changed by the display) with ISO 32000-2, 14.9.4: own text with yeh and hamza above typed
    /// apart is drawn with the glyph of U+0626 within one /ActualText with the text as typed; saved and opened again the block holds the typed
    /// text, the page text reads it as typed, and the search finds it typed and as U+0626 (canonical folding, No. 1184). Before, it was drawn
    /// with the glyphs of yeh and hamza.
    /// </summary>
    [Fact]
    public void E21_ArabicComposedForDisplay_TextAsTyped()
    {
        var content = Encoding.Latin1.GetString(ElementAppearance.For(UniTestsElement("a \u064A\u0654 c")).Form.RawData!);
        Assert.Contains("/Span <</ActualText <FEFF064A0654>>> BDC", content, StringComparison.Ordinal);
        foreach (var stored in new[] { "a \u064A\u0654 c", "a \u0628\u064A\u0654\u0628 c", "a \u0627\u0653 \u0622 c", "a \u0626 \u064A\u0654 c" })
        {
            Assert.Equal(stored, UniTestsElementLine(stored));
            var (block, _) = Uni04RoundTrip(stored);
            Assert.Equal(stored, block);
        }
        using var doc = PdfDocument.Open(UniTestsSaved(UniTestsElement("a \u064A\u0654 c")));
        var text = PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]));
        Assert.Single(text.Find("\u064A\u0654"));
        Assert.Single(text.Find("\u0626"));
    }

    /// <summary>
    /// UAX53 3.2 sentences 9–11 and UNI 9.2.1 sentences 35–41, measured against the shaping library: in Noto Sans Arabic and Noto Naskh Arabic
    /// each of the eight sequences gives the glyphs of the composed letter at the same places (the font's ccmp may take it apart again, as for
    /// alef-madda) and the same glyphs as the shaping library, alone and between two behs. Before, the sequences with a mark the font draws apart
    /// from the letter (yeh with the dots, alef with madda) differed.
    /// </summary>
    [Fact]
    public void E21_ArabicSequences_SameGlyphsAsTheComposedLetterAndTheShapingLibrary()
    {
        foreach (var file in new[] { "NotoSansArabic-Regular.ttf", "NotoNaskhArabic-Regular.ttf" })
        {
            var font = EmbeddedFonts.ArabicOpenType(file);
            var shaper = TextShaper.For(font);
            foreach (var (letter, mark, composed) in E21ArabicCompositions)
            {
                if (font.GlyphForUnicode(composed) <= 0) continue;
                foreach (var (before, after) in new[] { ("", ""), ("\u0628", "\u0628") })
                {
                    var typed = before + char.ConvertFromUtf32(letter) + char.ConvertFromUtf32(mark) + after;
                    var precomposed = before + char.ConvertFromUtf32(composed) + after;
                    var shaped = shaper.Shape(typed);
                    Assert.Equal(E11Drawn(shaper.Shape(precomposed)), E11Drawn(shaped));
                    Assert.Equal(ShaperTests.HarfBuzz(font.FileData, font.UnitsPerEm, typed, []).Glyphs.Order(), shaped.Select(g => g.Glyph).Order());
                    Assert.Equal(typed, string.Concat(shaped.Select(g => g.Text)));
                }
            }
        }
    }

    /// <summary>
    /// UNI 18.4.1 sentences 7–11 (the same rule for kana): every kana that composes canonically with U+3099 or U+309A (NFC; が, ぱ, ヴ …) is drawn
    /// with the glyph of the composed syllable in Noto Sans CJK JP, one glyph with the text as typed — the same glyph as the shaping library; a
    /// kana without a composed character (ア U+3099) is left to the font's own ligatures, as the shaping library. Before, the syllable and the
    /// mark were drawn apart.
    /// </summary>
    [Fact]
    public void E21_KanaWithVoicedMark_ComposedAsTheShapingLibrary()
    {
        var noto = Iso03NotoJp.Value;
        var shaper = TextShaper.For(noto);
        var count = 0;
        for (var kana = 0x3041; kana <= 0x30FF; kana++)
            foreach (var mark in new[] { 0x3099, 0x309A })
            {
                var typed = char.ConvertFromUtf32(kana) + char.ConvertFromUtf32(mark);
                var composed = typed.Normalize(NormalizationForm.FormC);
                if (composed.Length != 1 || noto.GlyphForUnicode(composed[0]) <= 0) continue;
                var shaped = shaper.Shape(typed);
                Assert.Equal([noto.GlyphForUnicode(composed[0])], shaped.Select(g => g.Glyph));
                Assert.Equal([typed], shaped.Select(g => g.Text));
                Assert.Equal(ShaperTests.HarfBuzz(noto.FileData, noto.UnitsPerEm, typed, []).Glyphs, shaped.Select(g => g.Glyph));
                count++;
            }
        Assert.True(count >= 40, count.ToString());
        // a kana without a composed character: what the font's own ligatures make of it, as the shaping library
        Assert.Equal(ShaperTests.HarfBuzz(noto.FileData, noto.UnitsPerEm, "\u30A2\u3099", []).Glyphs, shaper.Shape("\u30A2\u3099").Select(g => g.Glyph));
        Assert.Equal("\u30A2\u3099", E19Text(shaper.Shape("\u30A2\u3099")));
    }

    // ------------------------------------------------------------ 2. radicals in the search (FG 1194)

    /// <summary>
    /// UNI 18.1.14 sentences 4–5 and 25–29 (reader, search; „Suchen und schwärzen“): a Kangxi radical a producer wrote into /ToUnicode instead
    /// of the ideograph it looks like — the page text „x⼀y“ with U+2F00 — is found with U+4E00 and with U+2F00, the hit marking the radical's
    /// glyph; the ideograph on a page is found with the radical; the CJK radicals of the supplement fold to the ideograph the UCD names
    /// (U+2E8C → U+5C0F, U+2EAA → U+24D14 outside the Basic Multilingual Plane, U+2E9F → U+6BCD from its compatibility decomposition); the text
    /// read from the page stays the radical. U+2E80 (no equivalent ideograph), the CJK strokes (U+31D2) and the kanbun U+3192 are not folded.
    /// Before, the radical was found only with itself.
    /// </summary>
    [Fact]
    public void E21_Search_RadicalsFoundWithTheEquivalentIdeograph()
    {
        var page = E20Text("x\u2F00y");
        Assert.Equal(["\u2F00"], E20Marked(page, E20Hit(page, "\u4E00")));
        Assert.Equal(["\u2F00"], E20Marked(page, E20Hit(page, "\u2F00")));
        Assert.Equal("x\u2F00y", page.Text);
        var ideograph = E20Text("x\u4E00y");
        Assert.Equal(["\u4E00"], E20Marked(ideograph, E20Hit(ideograph, "\u2F00")));
        foreach (var (radical, equivalent) in new[] { ("\u2E8C", "\u5C0F"), ("\u2EAA", "\U00024D14"), ("\u2E9F", "\u6BCD"), ("\u2FD5", "\u9FA0") })
        {
            var supplement = E20Text("x" + radical + "y");
            Assert.Equal([radical], E20Marked(supplement, E20Hit(supplement, equivalent)));
        }
        E20NoHit(E20Text("x\u2E80y"), "\u4E00");
        E20NoHit(E20Text("x\u31D2y"), "\u4E3F");
        E20NoHit(E20Text("x\u3192y"), "\u4E00");
        // a name of two ideographs, one of them written as a radical
        var name = E20Text("ab \u2F49\u7530 cd");
        Assert.Equal(["\u2F49", "\u7530"], E20Marked(name, E20Hit(name, "\u6708\u7530")));
    }

    /// <summary>
    /// UNI 18.1.14 sentences 4–5 („Suchen und schwärzen“; privacy): on a page whose font reads U+2F00 through /ToUnicode, the term U+4E00 finds
    /// it, and redacting the hit removes exactly that glyph (before nothing was found and the character stayed).
    /// </summary>
    [Fact]
    public void E21_SearchAndRedact_KangxiRadicalFromToUnicode()
    {
        var file = MisreadTextTests.File(["a", "\u2F00", "b", "x", "y", "z"], "010203");
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        var (glyphs, unreadable, misread) = PageInterpreter.BuildTextAndUnreadable(doc, page);
        var text = PageText.Create(glyphs, unreadable, misread);
        var hit = Assert.Single(text.FindAll([PageText.NormalizeQuery("\u4E00")])[0]);
        Assert.Equal(["\u2F00"], E20Marked(text, hit));
        var areas = text.Quads(hit).Select(q => q.Bounds).Select(b => new PdfRectangle(b.Left, b.Top, b.Right, b.Bottom)).ToList();
        var redacted = Redactor.Apply(doc, page, areas);
        Assert.Equal(1, redacted.RemovedGlyphs);
        Assert.Equal("\u2F00", redacted.RemovedText.Trim());
    }

    /// <summary>
    /// UNI 18.1.14 sentences 4–5 with EquivalentUnifiedIdeograph.txt 16.0.0: all 214 Kangxi radicals fold to the ideograph of their compatibility
    /// decomposition (as NFKC of the runtime gives it), 114 of the 115 CJK radicals of the supplement to an ideograph (U+2E80 has none; values
    /// from the file noted here), the unassigned U+2E9A and U+2FD6 stay.
    /// </summary>
    [Fact]
    public void E21_RadicalFolding_AllRadicalsOfTheUcd()
    {
        for (var cp = 0x2F00; cp <= 0x2FD5; cp++)
        {
            var radical = char.ConvertFromUtf32(cp);
            Assert.Equal(radical.Normalize(NormalizationForm.FormKC), PageText.NormalizeQuery(radical));
        }
        var folded = 0;
        for (var cp = 0x2E80; cp <= 0x2EF3; cp++)
        {
            var radical = char.ConvertFromUtf32(cp);
            if (PageText.NormalizeQuery(radical) != radical) folded++;
        }
        Assert.Equal(114, folded);
        foreach (var (radical, ideograph) in new[] { (0x2E81, 0x5382), (0x2E87, 0x20628), (0x2E95, 0x2B739), (0x2EC7, 0x278B2), (0x2EF3, 0x9F9F) })
            Assert.Equal(char.ConvertFromUtf32(ideograph), PageText.NormalizeQuery(char.ConvertFromUtf32(radical)));
        Assert.Equal("\u2E9A", PageText.NormalizeQuery("\u2E9A"));
        Assert.Equal("\u2FD6", PageText.NormalizeQuery("\u2FD6"));
    }

    // ------------------------------------------------------------ 3. caret by grapheme clusters (FG 1195)

    private static int[] E21Stops(string text) => [.. Enumerable.Range(0, text.Length + 1).Where(i => EditUnits.IsStop(text, i))];

    /// <summary>
    /// UNI 13.23.1 sentences 21–24 with UAX #29 (editor): the caret stops at the boundaries of extended grapheme clusters as the runtime gives
    /// them, together with the units of the syllabic scripts (the larger unit counts) — a Kirat Rai vowel in two parts is not split (consonant,
    /// then the vowel: stops 0, 2, 6), e with a combining acute is one unit, Hangul jamo of one syllable, an emoji ZWJ sequence and a pair of
    /// regional indicators too; Khmer ka, coeng, ka is one unit (grapheme cluster before, coeng and consonant after), so is Devanagari ka,
    /// virama, ZWJ, ssa; an ideographic description sequence keeps a stop after every character; a prepended concatenation mark with the
    /// digit after it is one unit, CR LF too. The backspace key keeps its rules: one code
    /// point (the second part of the Kirat Rai vowel, the last emoji of the sequence, the acute). Before, the caret stopped at every code point
    /// that is no mark.
    /// </summary>
    [Fact]
    public void E21_EditUnits_CaretByGraphemeClusters()
    {
        const string kiratRai = "\U00016D44\U00016D63\U00016D67";
        Assert.Equal([0, 2, 6], E21Stops(kiratRai));
        Assert.Equal(2, EditUnits.Previous(kiratRai, 6));
        Assert.Equal(2, EditUnits.Next(kiratRai, 0));
        Assert.Equal(6, EditUnits.Next(kiratRai, 2));
        Assert.Equal([0, 2, 3], E21Stops("e\u0301x"));
        Assert.Equal([0, 3], E21Stops("\u1100\u1161\u11A8"));
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        Assert.Equal([0, 8], E21Stops(family));
        Assert.Equal([0, 4, 8], E21Stops("\U0001F1E9\U0001F1EA\U0001F1EB\U0001F1F7"));
        Assert.Equal([0, 3], E21Stops("\u1780\u17D2\u1780"));
        Assert.Equal([0, 4], E21Stops("\u0915\u094D\u200D\u0937"));
        Assert.Equal([0, 1, 2, 3], E21Stops("\u2FF1\u4E95\u86D9"));
        Assert.Equal([0, 2], E21Stops("\u0600" + "1"));
        Assert.Equal([0, 1, 3, 4], E21Stops("a\r\nb"));
        // the backspace key: one code point
        Assert.Equal(4, EditUnits.PreviousForDeletion(kiratRai, 6));
        Assert.Equal(6, EditUnits.PreviousForDeletion(family, 8));
        Assert.Equal(1, EditUnits.PreviousForDeletion("e\u0301", 2));
    }

    /// <summary>
    /// UAX #29 with the limits of No. 1137 (editor): in long texts the grapheme boundaries are found once per state of the text, from its start
    /// (No. 1210) — 200 000 Thai consonants without a space, 100 000 ideographs and 20 000 regional indicators (pairs counted from the start of the
    /// text) — each arrow step within a few milliseconds; the regional indicators stay paired at the start.
    /// </summary>
    [Fact]
    public void E21_EditUnits_LongTexts_Bounded()
    {
        var thai = new string('\u0E01', 200_000);
        var cjk = new string('\u4E00', 100_000);
        var flags = string.Concat(Enumerable.Repeat("\U0001F1E9\U0001F1EA", 10_000));
        var watch = Stopwatch.StartNew();
        for (var k = 0; k < 200; k++)
        {
            Assert.Equal(100_001, EditUnits.Next(thai, 100_000));
            Assert.Equal(49_999, EditUnits.Previous(cjk, 50_000));
        }
        Assert.Equal(4, EditUnits.Next(flags, 0));
        Assert.Equal(8, EditUnits.Next(flags, 4));
        Assert.Equal(4, EditUnits.Previous(flags, 8));
        Assert.True(watch.ElapsedMilliseconds < 3000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// UNI 13.23.1 sentences 21–24 with UAX #29 (click into own text): the insertion point for a click lies at a grapheme boundary — in „e\u0301e\u0301“
    /// a click on the first acute goes before or after the first e with its acute, never between them. Before, every character boundary of
    /// the line could be hit.
    /// </summary>
    [Fact]
    public void E21_Click_AtGraphemeBoundaries()
    {
        const string text = "e\u0301e\u0301";
        var layout = E20Layout(text, 400);
        var line = layout.Lines[0];
        foreach (var g in line.Glyphs)
            foreach (var dx in new[] { -0.5, 0, 0.5, g.Advance / 2, g.Advance })
                Assert.Contains(layout.HitTest(g.X + dx, line.Baseline - 2), new[] { 0, 2, 4 });
        Assert.Contains(layout.CaretInLine(line, RichLayout.XAt(line, 1)), new[] { 0, 2 });
    }

    // ------------------------------------------------------------ 4. composing for display in every script (question 72, FG 1197)

    /// <summary>The sequences of question 72 measured: typed with combining marks, Latin, Greek, Cyrillic, Vietnamese.</summary>
    private static readonly string[] E21CombiningSamples =
    [
        "e\u0301", "A\u030A", "a\u0308", "n\u0303", "c\u0327", "o\u0302", "u\u030B", "z\u030C", "g\u0306", "I\u0307",
        "\u03A9\u0301", "\u03B1\u0301", "\u03B9\u0308", "\u03C5\u0308\u0301", "\u0438\u0306", "\u0435\u0308", "\u0443\u0306", "\u0433\u0301",
        "a\u0323\u0302", "a\u0302\u0323", "o\u031B\u0301", "e\u0302\u0303", "\u00E9\u0323", "u\u031B\u0300", "y\u0309",
        "pe\u0301t", "na\u0308me", "\u0412\u0438\u0306",
    ];

    /// <summary>
    /// UNI 3.11 (canonical composition with blocking) for display, question 72: in Liberation Sans every sequence a starter forms with its marks
    /// is drawn with the glyph of the composed character the font has — e U+0301 as é, A U+030A as Å, Ω U+0301 as Ώ, и U+0306 as й, a U+0323
    /// U+0302 and a U+0302 U+0323 both as ậ (canonical order) —, one glyph with the text as typed; é U+0323 is drawn ẹ and the acute (the
    /// starter taken apart and composed again); a second acute after a composed one stays (blocked by nothing, but no composite); shin with shin
    /// dot stays two glyphs (U+FB2A is excluded from composition); a font without the composed letter keeps the sequence. Before, each sequence
    /// kept the glyphs of its characters.
    /// </summary>
    [Fact]
    public void E21_AllScripts_SequenceComposedWhereTheFontHasTheLetter()
    {
        var font = OpenTypeFont.Parse(ShaperLimitTests.Liberation);
        var shaper = TextShaper.For(font);
        foreach (var (typed, composed) in new[]
                 {
                     ("e\u0301", "\u00E9"), ("A\u030A", "\u00C5"), ("\u03A9\u0301", "\u038F"), ("\u0438\u0306", "\u0439"),
                     ("a\u0323\u0302", "\u1EAD"), ("a\u0302\u0323", "\u1EAD"), ("\u00E9\u0323", "\u1EB9\u0301"), ("a\u0301\u0301", "\u00E1\u0301"),
                 })
        {
            Assert.All(composed, c => Assert.True(font.GlyphForUnicode(c) > 0, ((int)c).ToString("X4")));
            var shaped = shaper.Shape(typed);
            Assert.Equal(composed.Select(c => font.GlyphForUnicode(c)), shaped.Select(g => g.Glyph));
            Assert.Equal(typed, shaped[0].Text);
            Assert.All(shaped.Skip(1), g => Assert.Equal("", g.Text));
        }
        var hebrew = E19Font([(0x05E9, 0x05E9, 1101), (0x05C1, 0x05C1, 1102), (0xFB2A, 0xFB2A, 1103)], null);
        Assert.Equal([1101, 1102], E19Shape(hebrew, "\u05E9\u05C1").Select(g => g.Glyph));
        var bare = E19Font([('e', 'e', Ot02G('e')), (0x0301, 0x0301, Ot02G('a'))], null);
        Assert.Equal([Ot02G('e'), Ot02G('a')], E19Shape(bare, "e\u0301").Select(g => g.Glyph));
    }

    /// <summary>
    /// UNI 3.11 for display, question 72, measured against the shaping library: Latin, Greek, Cyrillic and Vietnamese sequences with combining
    /// marks in Liberation Sans, Arial, Times New Roman, Segoe UI and Calibri give the same glyphs as the library (which composes before
    /// shaping where the font has the character). Before, the sequences with a composed character in the font differed.
    /// </summary>
    [Fact]
    public void E21_AllScripts_SameGlyphsAsTheShapingLibrary()
    {
        var fonts = new List<OpenTypeFont> { OpenTypeFont.Parse(ShaperLimitTests.Liberation) };
        foreach (var file in new[] { "arial.ttf", "times.ttf", "segoeui.ttf", "calibri.ttf" })
            if (File.Exists(Path.Combine(@"C:\Windows\Fonts", file))) fonts.Add(OpenTypeFont.Parse(File.ReadAllBytes(Path.Combine(@"C:\Windows\Fonts", file))));
        foreach (var font in fonts)
            foreach (var sample in E21CombiningSamples)
            {
                if (sample.EnumerateRunes().Any(r => font.GlyphForUnicode(r.Value) <= 0)) continue;
                var ours = TextShaper.For(font).Shape(sample).Select(g => g.Glyph).Order();
                Assert.Equal(ShaperTests.HarfBuzz(font.FileData, font.UnitsPerEm, sample, []).Glyphs.Order(), ours);
            }
    }

    /// <summary>
    /// ISO 32000-2, 14.9.4 with question 72 (the stored text is not changed by the display): own text with „e U+0301“ next to the precomposed
    /// „é“ — both drawn with the glyph of é — is read back from the saved page as typed, the block holds the typed text, and the search finds
    /// both spellings with either term.
    /// </summary>
    [Fact]
    public void E21_AllScripts_ComposedForDisplay_TextAsTyped()
    {
        foreach (var stored in new[] { "a pe\u0301t c", "a \u00E9 e\u0301 c", "a \u0438\u0306 c", "a Vi\u0323\u0302t c" })
        {
            Assert.Equal(stored, UniTestsElementLine(stored));
            Assert.Equal(stored, Uni04RoundTrip(stored).Block);
        }
        using var doc = PdfDocument.Open(UniTestsSaved(UniTestsElement("a \u00E9 e\u0301 c")));
        var text = PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]));
        Assert.Equal(2, text.Find("\u00E9").Count);
        Assert.Equal(2, text.Find("e\u0301").Count);
    }

    // ------------------------------------------------------------ helper for the positioning tests (No. 1197)

    /// <summary>
    /// Liberation Sans whose cmap lacks every character with a canonical decomposition (é, Å, ậ …): a base letter and a combining mark stay two
    /// glyphs, as the tests of mark positioning need them — with the composed letter in the font the shaper draws it instead (No. 1197).
    /// </summary>
    internal static byte[] LiberationWithoutComposites => E21LiberationWithoutComposites.Value;

    private static readonly Lazy<byte[]> E21LiberationWithoutComposites = new(() =>
    {
        var font = OpenTypeFont.Parse(ShaperLimitTests.Liberation);
        var groups = new List<(int First, int Last, int Glyph)>();
        for (var c = 0; c <= 0xFFFF; c++)
        {
            if (c is >= 0xD800 and <= 0xDFFF || font.GlyphForUnicode(c) is var glyph && glyph <= 0) continue;
            var single = ((char)c).ToString();
            if (single.Normalize(NormalizationForm.FormD) != single) continue;
            if (groups.Count > 0 && groups[^1].Last == c - 1 && groups[^1].Glyph + (c - groups[^1].First) == glyph)
                groups[^1] = (groups[^1].First, c, groups[^1].Glyph);
            else
                groups.Add((c, c, glyph));
        }
        return ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?> { ["cmap"] = E11Cmap([.. groups]) });
    });
}
