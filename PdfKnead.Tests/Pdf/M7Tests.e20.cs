using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package E20: the search over canonically equivalent text, format characters and the spellings the Unicode Standard treats as the same
/// (docs/FEHLERGEDAECHTNIS.md, No. 1184), line breaking, letter spacing and the editor in the scripts of the Unicode chapters 13–23 (No. 1185),
/// the Han fallback face after the language of the block (No. 1186) and the text of fonts without /ToUnicode beyond U+3000 (No. 1187).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (E20)

    /// <summary>A page text of one glyph per element of <paramref name="glyphs"/>, side by side on one line (each 6 units wide).</summary>
    private static PageText E20Line(params string[] glyphs)
    {
        var list = new List<TextGlyph>();
        float x = 0;
        foreach (var g in glyphs)
        {
            list.Add(new TextGlyph(g, x, 0, x + 6, 0, x + 6, 10, x, 10, 10, true));
            x += 6;
        }
        return PageText.Create([.. list]);
    }

    /// <summary>A page text of one glyph per code point of <paramref name="text"/>.</summary>
    private static PageText E20Text(string text) => E20Line([.. text.EnumerateRunes().Select(r => r.ToString())]);

    /// <summary>The texts of the glyphs a hit marks.</summary>
    private static string[] E20Marked(PageText text, TextRange hit) => [.. text.GlyphsOf(hit).Select(g => g.Text)];

    /// <summary>The single hit of <paramref name="query"/> with <see cref="PageText.Find"/>, the same with <see cref="PageText.FindAll"/>.</summary>
    private static TextRange E20Hit(PageText text, string query)
    {
        var hit = Assert.Single(text.Find(query));
        Assert.Equal([hit], text.FindAll([PageText.NormalizeQuery(query)])[0]);
        return hit;
    }

    /// <summary>No hit of <paramref name="query"/>, neither with Find nor with FindAll.</summary>
    private static void E20NoHit(PageText text, string query)
    {
        Assert.Empty(text.Find(query));
        Assert.Empty(text.FindAll([PageText.NormalizeQuery(query)])[0]);
    }

    // ------------------------------------------------------------ 1. canonical folding of the page text (FG 1184)

    /// <summary>
    /// UNI 18.1.10 sentences 1–5, 18.1.2 sentences 15–18 and 83 (reader, search; privacy): a hanja doublet in the page text (U+F900 from
    /// /ToUnicode) is found with the same character copied from the page and with the unified ideograph U+8C48 it is canonically equivalent
    /// to — and the unified ideograph on the page with the doublet (before only the term was normalised, so neither was found); the text read
    /// from the page stays U+F900.
    /// </summary>
    [Fact]
    public void E20_Search_CompatibilityIdeograph_FoundInBothDirections()
    {
        var doublet = E20Text("x豈y");
        Assert.Equal(["豈"], E20Marked(doublet, E20Hit(doublet, "豈")));
        Assert.Equal(["豈"], E20Marked(doublet, E20Hit(doublet, "豈")));
        Assert.Equal("x豈y", doublet.Text);
        var unified = E20Text("x豈y");
        Assert.Equal(["豈"], E20Marked(unified, E20Hit(unified, "豈")));
    }

    /// <summary>
    /// UNI 18.1.2 sentences 81–83, 18.1.10 sentences 6–8 (reader, search): the twelve unified ideographs in the compatibility block (U+FA0E …)
    /// have no decomposition and stay themselves in the term and in the page text — a page with U+FA0E is found with U+FA0E, the term stays
    /// U+FA0E; all twelve unchanged.
    /// </summary>
    [Fact]
    public void E20_Search_TwelveUnifiedIdeographsOfTheCompatibilityBlock_StayThemselves()
    {
        foreach (var c in "﨎﨏﨑﨓﨔﨟﨡﨣﨤﨧﨨﨩")
            Assert.Equal(c.ToString(), PageText.NormalizeQuery(c.ToString()));
        var page = E20Text("x﨎y");
        Assert.Equal(["﨎"], E20Marked(page, E20Hit(page, "﨎")));
    }

    /// <summary>
    /// UNI 23.4 sentences 45–56 (reader, search): characters with a singleton decomposition or excluded from composition are found by what
    /// they are canonically equivalent to and by themselves — U+F914, U+2126 OHM (as omega), U+212B ANGSTROM (as A with ring), U+0958 (as ka
    /// with nukta), U+FB2A (as shin with shin dot), each in both directions (before only the term was normalised).
    /// </summary>
    [Fact]
    public void E20_Search_SingletonsAndCompositionExclusions_FoundInBothDirections()
    {
        foreach (var (single, equivalent) in new[]
                 {
                     ("樂", "樂"), ("Ω", "Ω"), ("Å", "Å"), ("क़", "क\u093C"), ("שׁ", "ש\u05C1"),
                 })
        {
            var page = E20Text("x" + single + "y");
            Assert.Equal([single], E20Marked(page, E20Hit(page, single)));
            Assert.Equal([single], E20Marked(page, E20Hit(page, equivalent)));
            // (a right-to-left line is drawn in visual order, as files write it, and read back in logical order: No. 1177)
            var rightToLeft = equivalent[0] is >= '֐' and <= '׿';
            var other = rightToLeft ? E20Line([.. equivalent.Reverse().Select(c => c.ToString())]) : E20Text("x" + equivalent + "y");
            Assert.Equal(equivalent.EnumerateRunes().Select(r => r.ToString()).Order(), E20Marked(other, E20Hit(other, single)).Order());
        }
    }

    /// <summary>
    /// UNI 18.1.10 sentences 1–5 („Suchen und schwärzen“; privacy): a page whose font reads U+F900 through /ToUnicode (a Type 3 font) — the
    /// term U+F900 copied from the page and the unified U+8C48 both find it, the quadrilateral covers exactly that glyph, and redacting it
    /// removes this glyph and no other (before neither term found anything and the name stayed).
    /// </summary>
    [Fact]
    public void E20_SearchAndRedact_CompatibilityIdeographFromToUnicode()
    {
        foreach (var term in new[] { "豈", "豈" })
        {
            var file = MisreadTextTests.File(["a", "豈", "b", "x", "y", "z"], "010203");
            using var doc = PdfDocument.Open(file);
            var page = doc.Pages[0];
            var (glyphs, unreadable, misread) = PageInterpreter.BuildTextAndUnreadable(doc, page);
            var text = PageText.Create(glyphs, unreadable, misread);
            var hit = Assert.Single(text.FindAll([PageText.NormalizeQuery(term)])[0]);
            Assert.Equal(["豈"], E20Marked(text, hit));
            var areas = text.Quads(hit).Select(q => q.Bounds).Select(b => new PdfRectangle(b.Left, b.Top, b.Right, b.Bottom)).ToList();
            var redacted = Redactor.Apply(doc, page, areas);
            Assert.Equal(1, redacted.RemovedGlyphs);
            Assert.Equal("豈", redacted.RemovedText.Trim());
        }
    }

    /// <summary>
    /// UNI 18.6 sentences 8–9 (reader, search): Hangul written as conjoining jamo on the page (U+1100 U+1161 U+11A8, three glyphs) is found
    /// with the syllable „각“ U+AC01 and with the same jamo sequence, the hit marking all three glyphs; the syllable on the page is found with
    /// the jamo (before neither direction found anything).
    /// </summary>
    [Fact]
    public void E20_Search_HangulJamoAndSyllable_FoundInBothDirections()
    {
        var jamo = E20Text("x각y");
        Assert.Equal(["ᄀ", "ᅡ", "ᆨ"], E20Marked(jamo, E20Hit(jamo, "각")));
        Assert.Equal(["ᄀ", "ᅡ", "ᆨ"], E20Marked(jamo, E20Hit(jamo, "각")));
        var syllable = E20Text("x각y");
        Assert.Equal(["각"], E20Marked(syllable, E20Hit(syllable, "각")));
    }

    /// <summary>
    /// UNI 18.6 sentences 8–9 („Suchen und schwärzen“): the jamo of a Type 3 font from /ToUnicode are found with the syllable and redacting
    /// the hit removes exactly the three glyphs.
    /// </summary>
    [Fact]
    public void E20_SearchAndRedact_HangulJamoFromToUnicode()
    {
        var file = MisreadTextTests.File(["ᄀ", "ᅡ", "ᆨ", "x", "y", "z"], "04010203050401");
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        var (glyphs, unreadable, misread) = PageInterpreter.BuildTextAndUnreadable(doc, page);
        var text = PageText.Create(glyphs, unreadable, misread);
        var hit = Assert.Single(text.FindAll([PageText.NormalizeQuery("각")])[0]);
        var areas = text.Quads(hit).Select(q => q.Bounds).Select(b => new PdfRectangle(b.Left, b.Top, b.Right, b.Bottom)).ToList();
        var redacted = Redactor.Apply(doc, page, areas);
        Assert.Equal(3, redacted.RemovedGlyphs);
        Assert.Equal("각", redacted.RemovedText.Replace(" ", ""));
    }

    /// <summary>
    /// UNI 13.22.1 sentences 12–21, 13.23.1 sentences 12–20, 13.21.1 (reader, search): marks outside the Basic Multilingual Plane are composed
    /// in the page text as in the term — Gurung Khema U+16101 U+16121 and U+16101 U+1611E U+1611E, Kirat Rai U+16D44 U+16D6A and U+16D44
    /// U+16D63 U+16D67 U+16D67 (the vowel signs of Kirat Rai are letters), Chakma U+11107 U+1112E and U+11107 U+11131 U+11127 — each found with
    /// the other (before the page text folded only characters before a BMP mark, and the platform does not know the Unicode 16.0.0
    /// decompositions).
    /// </summary>
    [Fact]
    public void E20_Search_MarksOutsideTheBmp_ComposedAndDecomposedFindEachOther()
    {
        foreach (var (composed, decomposed) in new[]
                 {
                     ("\U00016101\U00016121", "\U00016101\U0001611E\U0001611E"),
                     ("\U00016101\U00016126", "\U00016101\U0001611E\U0001611E\U0001611F"),
                     ("\U00016D44\U00016D6A", "\U00016D44\U00016D63\U00016D67\U00016D67"),
                     ("\U00016D44\U00016D68", "\U00016D44\U00016D67\U00016D67"),
                     ("\U00011107\U0001112E", "\U00011107\U00011131\U00011127"),
                 })
        {
            var a = E20Text("x" + composed + "y");
            Assert.Equal(composed.EnumerateRunes().Select(r => r.ToString()), E20Marked(a, E20Hit(a, decomposed)));
            var b = E20Text("x" + decomposed + "y");
            Assert.Equal(decomposed.EnumerateRunes().Select(r => r.ToString()), E20Marked(b, E20Hit(b, composed)));
        }
    }

    /// <summary>
    /// UNI 13.9.1 sentence 19 (reader, search; privacy): upper and lower case are folded per code point also outside the Basic Multilingual
    /// Plane — a capitalised Warang Citi word on the page (U+118A0 U+118C1) is found by „Suchen und schwärzen“ with many terms when searched in
    /// small letters (U+118C0 U+118C1), as by the single search (before the candidates of the many-term search were keyed per UTF-16 unit).
    /// </summary>
    [Fact]
    public void E20_FindAll_CaseFoldedPerCodePoint_OutsideTheBmp()
    {
        var page = E20Text("x\U000118A0\U000118C1y");
        var small = "\U000118C0\U000118C1";
        Assert.Single(page.Find(small));
        var all = page.FindAll([PageText.NormalizeQuery(small), "zz", "qq"]);
        Assert.Equal(["\U000118A0", "\U000118C1"], E20Marked(page, Assert.Single(all[0])));
        Assert.Empty(page.FindAll([PageText.NormalizeQuery(small)], matchCase: true)[0]);
        // the prefix of three units cuts the second surrogate pair: still found
        var cut = E20Text("xä\U000118A0y");
        Assert.Single(cut.FindAll(["Ä\U000118C0"])[0]);
    }

    /// <summary>
    /// UNI 23.2.1 sentences 16–18, 23.2.2 sentences 76–80, 23.2.4 sentence 35, 23.9.1 sentences 1–2 (reader, search): format characters are
    /// passed over in the page text and in the term like the directional formatting characters — WJ, ZWJ, ZWNJ, CGJ, ZWNBSP and the tags
    /// (before none of them was, so „ab“ did not find „a\u2060b“); the hit covers the format character between the letters.
    /// </summary>
    [Fact]
    public void E20_Search_FormatCharacters_PassedOver()
    {
        foreach (var format in new[] { "\u2060", "\u200D", "\u200C", "\u034F", "\uFEFF", "\U000E0067", "\u200B" })
        {
            var page = E20Text("xa" + format + "by");
            Assert.Equal(["a", format, "b"], E20Marked(page, E20Hit(page, "ab")));
            var plain = E20Text("xaby");
            Assert.Equal(["a", "b"], E20Marked(plain, E20Hit(plain, "a" + format + "b")));
        }
        var ch = E20Text("c\u034Fh");
        E20Hit(ch, "ch");
    }

    /// <summary>
    /// UNI 23.2.2 sentences 81–83 (reader, search): after a virama ZWNJ and ZWJ keep their distinction in the Indic scripts — the Devanagari
    /// page text ka, virama, ZWNJ, ssa is found with the term written the same way, not with ka, virama, ssa (the conjunct), and the conjunct
    /// on the page is not found with the ZWNJ spelling; the half form with ZWJ likewise; a ZWJ between two letters is passed over.
    /// </summary>
    [Fact]
    public void E20_Search_JoinersAfterAVirama_KeepTheirDistinction()
    {
        foreach (var joiner in new[] { "\u200C", "\u200D" })
        {
            var explicitForm = E20Text("xक\u094D" + joiner + "षy");
            E20Hit(explicitForm, "क\u094D" + joiner + "ष");
            E20NoHit(explicitForm, "क\u094Dष");
            var conjunct = E20Text("xक\u094Dषy");
            E20NoHit(conjunct, "क\u094D" + joiner + "ष");
            E20Hit(conjunct, "क\u094Dष");
        }
        var khmer = E20Text("xក\u17D2\u200Cកy");   // after the invisible stacker as well
        E20NoHit(khmer, "ក\u17D2ក");
        var between = E20Text("xक\u200Dषy");
        E20Hit(between, "कष");
    }

    /// <summary>
    /// UNI 23.5 (reader, search): a private-use character followed by a mark is found with the same sequence and the mark stays with it.
    /// </summary>
    [Fact]
    public void E20_Search_PrivateUseCharacterWithAMark_Found()
    {
        var page = E20Text("x\u0301y");
        Assert.Equal(["", "\u0301"], E20Marked(page, E20Hit(page, "\u0301")));
    }

    /// <summary>
    /// Security (reader, search): a page text with an unpaired surrogate (from a crafted /ToUnicode) before a mark, and a term with one, are
    /// searched without an exception — before the normalisation of the unit threw and the search of the page ended.
    /// </summary>
    [Fact]
    public void E20_Search_UnpairedSurrogateBeforeAMark_NoException()
    {
        var page = E20Line("a", "\uD800", "\u0301", "b", "c");
        Assert.Single(page.Find("bc"));
        Assert.Single(page.FindAll(["bc"])[0]);
        Assert.Empty(page.Find("\uDC00\u0301"));
        _ = PageText.NormalizeQuery("\uD800\u0301x");
    }

    /// <summary>
    /// UNI 13.4.1 sentences 202–203 (reader, search): the Tibetan marks of emphasis U+0F35 and U+0F37 are passed over in comparisons — „ཀ\u0F72ས“
    /// finds „ཀ\u0F72\u0F35ས“, the hit marking all four glyphs with the mark; and the term with U+0F37 finds the text without it.
    /// </summary>
    [Fact]
    public void E20_Search_TibetanMarksOfEmphasis_PassedOver()
    {
        var page = E20Text("xཀ\u0F72\u0F35སy");
        Assert.Equal(["ཀ", "\u0F72", "\u0F35", "ས"], E20Marked(page, E20Hit(page, "ཀ\u0F72ས")));
        var plain = E20Text("xཀ\u0F72སy");
        E20Hit(plain, "ཀ\u0F37\u0F72ས");
    }

    /// <summary>
    /// UNI 12.6.3 sentence 50, 12.9.3 sentences 125–132 (reader, search): the spellings the standard asks to treat as the same — Tamil shri
    /// with sha and with sa; Malayalam ṉṯa written na, virama, rra and chillu-n, virama, rra; the chillus of Unicode 5.0 (consonant, virama, ZWJ)
    /// and the atomic chillus U+0D7A–U+0D7E — find each other, the hit marking all glyphs of the spelling on the page; the ambiguous na, virama,
    /// ZWJ, rra (sentence 128) is not folded. The text read from the page stays as it is.
    /// </summary>
    [Fact]
    public void E20_Search_EquivalentSpellings_TamilAndMalayalam()
    {
        var sha = "ஶ\u0BCDர\u0BC0";
        var sa = "ஸ\u0BCDர\u0BC0";
        Assert.Equal(4, E20Marked(E20Text("x" + sha + "y"), E20Hit(E20Text("x" + sha + "y"), sa)).Length);
        E20Hit(E20Text("x" + sa + "y"), sha);
        E20NoHit(E20Text("xஶy"), "ஸ");   // only within shri
        var legacy = "ന\u0D4Dറ";
        var recommended = "ൻ\u0D4Dറ";
        E20Hit(E20Text("x" + legacy + "y"), recommended);
        E20Hit(E20Text("x" + recommended + "y"), legacy);
        foreach (var (consonant, chillu) in new[] { ('ണ', 'ൺ'), ('ന', 'ൻ'), ('ര', 'ർ'), ('ല', 'ൽ'), ('ള', 'ൾ') })
        {
            var old = E20Text($"x{consonant}\u0D4D\u200Dy");
            Assert.Equal([consonant.ToString(), "\u0D4D", "\u200D"], E20Marked(old, E20Hit(old, chillu.ToString())));
            E20Hit(E20Text($"x{chillu}y"), $"{consonant}\u0D4D\u200D");
            Assert.Equal($"x{consonant}\u0D4D\u200Dy", old.Text);
        }
        var ambiguous = E20Text("xന\u0D4D\u200Dറy");
        E20NoHit(ambiguous, recommended);
        E20NoHit(ambiguous, "ൻറ");
        E20Hit(ambiguous, "ന\u0D4D\u200Dറ");
    }

    /// <summary>
    /// UNI 17.3.1 sentences 55–57 (reader, search): Balinese surang is found as ra, cecek as nga and bisah as ha, and the reverse; the hit marks
    /// the glyph of the sign.
    /// </summary>
    [Fact]
    public void E20_Search_Balinese_SignsAsTheirLetters()
    {
        foreach (var (sign, letter) in new[] { ("\u1B03", "ᬭ"), ("\u1B02", "ᬗ"), ("\u1B04", "ᬳ") })
        {
            var page = E20Text("xᬓ" + sign + "y");
            Assert.Equal(["ᬓ", sign], E20Marked(page, E20Hit(page, "ᬓ" + letter)));
            E20Hit(E20Text("xᬓ" + letter + "y"), "ᬓ" + sign);
        }
    }

    /// <summary>
    /// UNI 18.4.1 sentences 7–11, 18.6.4 sentences 7–10 (reader, search): „が“ written with U+3099 is found with the precomposed U+304C and the
    /// reverse; compatibility jamo U+3131 U+314F stay as they are (no NFKC) — „ㄱㅏ“ is found with itself, not with „가“.
    /// </summary>
    [Fact]
    public void E20_Search_KanaVoicedMark_And_CompatibilityJamoStay()
    {
        var voiced = E20Text("xか\u3099y");
        Assert.Equal(["か", "\u3099"], E20Marked(voiced, E20Hit(voiced, "が")));
        E20Hit(E20Text("xがy"), "か\u3099");
        var jamo = E20Text("xㄱㅏy");
        E20Hit(jamo, "ㄱㅏ");
        E20NoHit(jamo, "가");
        Assert.Equal("xㄱㅏy", jamo.Text);
    }

    /// <summary>
    /// UNI 18.1.12 sentences 5–9, 18.1.10 sentences 10–16, 18.1.11 sentence 2 (reader, search): only canonical equivalence is folded — the
    /// kanbun U+3192 is not found as U+4E00 (its decomposition is a compatibility one), the term U+3192 stays and finds it; U+FA30 and U+2F800
    /// read from a page stay in the text.
    /// </summary>
    [Fact]
    public void E20_Search_OnlyCanonicalEquivalence_KanbunStays()
    {
        var kanbun = E20Text("x㆒y");
        E20NoHit(kanbun, "一");
        E20Hit(kanbun, "㆒");
        Assert.Equal("㆒", PageText.NormalizeQuery("㆒"));
        Assert.Equal("x侮\U0002F800y", E20Text("x侮\U0002F800y").Text);
    }

    /// <summary>
    /// UNI 16.2.1 sentence 74, 16.3.1 sentences 88–89 (reader, search): no characters are mapped between Thai and Lao — „ກ“ is not found with
    /// „ก“; Myanmar marks in either order (U+1037 U+103A and U+103A U+1037) are canonically ordered and find each other.
    /// </summary>
    [Fact]
    public void E20_Search_ThaiAndLaoApart_MyanmarMarksInEitherOrder()
    {
        E20NoHit(E20Text("xກy"), "ก");
        E20NoHit(E20Text("xกy"), "ກ");
        var page = E20Text("xက\u1037\u103Ay");
        Assert.Equal(["က", "\u1037", "\u103A"], E20Marked(page, E20Hit(page, "က\u103A\u1037")));
    }

    /// <summary>
    /// UNI 18.2.1 sentences 13–15 and 67–68 (reader, search): an ideographic description sequence read from the page stays as it is — the
    /// text is „⿱井蛙“ and „蛙“ finds only its third character.
    /// </summary>
    [Fact]
    public void E20_Search_IdeographicDescriptionSequence_AsWritten()
    {
        var page = E20Text("⿱井蛙");
        Assert.Equal("⿱井蛙", page.Text);
        Assert.Equal(["蛙"], E20Marked(page, E20Hit(page, "蛙")));
    }


    // ------------------------------------------------------------ 12. vowel signs drawn left of their consonant (reader)

    /// <summary>
    /// UNI 17.7.1 sentences 57–59, 12.1 (reader, text of foreign pages without /ActualText): a vowel sign drawn before its consonant (Devanagari
    /// I, Bengali E, Javanese taling; glyph of the sign first in the content, then the consonant, each with its character in /ToUnicode) is read
    /// in the order of the content, as the reference readers read it (measured m7-e20; special rule) — the text is not reordered by guessing.
    /// </summary>
    [Fact]
    public void E20_LeftDrawnVowelSign_ReadInTheOrderOfTheContent_AsTheReferenceReaders()
    {
        foreach (var (name, sign, consonant) in new[] { ("devanagari-i", "\u093F", "क"), ("bengali-e", "\u09C7", "ক"), ("javanisch-taling", "\uA9BA", "ꦏ") })
        {
            var file = MisreadTextTests.File([sign, consonant, "x", "y", "z", "w"], "0102");
            if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
                File.WriteAllBytes(Path.Combine(folder, $"e20-vokal-links-{name}.pdf"), file);
            using var doc = PdfDocument.Open(file);
            var text = PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0])).Text;
            Assert.StartsWith(sign + consonant, text);
        }
    }

    // ------------------------------------------------------------ 4, 5, 15. line breaking, letter spacing, separators (FG 1185)

    private static readonly RichStyle E20Style = new() { Family = "Liberation Sans", Size = 10 };

    private static RichLayout E20Layout(string text, double width, double hangingIndent = 0, RichStyle? style = null) =>
        RichLayout.Layout(new RichText(text, style ?? E20Style), width, BlockAlignment.Left, 1.15, null, hangingIndent);

    private static string[] E20LineTexts(RichLayout layout) => [.. layout.Lines.Select(l => string.Concat(l.Glyphs.Select(g => g.Text)))];

    private static double E20Width(string text) => E20Layout(text, 10000).Lines[0].Width;

    /// <summary>
    /// UNI 16.1.1 sentence 71, 16.2.1 sentence 58, 16.3.1 sentence 94, 16.4.2 sentence 267, 18.6.4 sentences 11–13, 23.2.1 sentences 23–25 (own
    /// texts): ZERO WIDTH SPACE is a break opportunity — „aaaa“ ZWSP „bbbb“ in a width for five letters breaks after the ZWSP (before the line
    /// broke hard after the fifth letter); likewise Thai and Khmer without spaces (class SA, no break inside a word without a dictionary, UAX #14
    /// LB1); WJ in its place gives no opportunity. Compatibility jamo are ideographic (class ID) and may break anywhere, see
    /// M7NormTests.E23_Korean_BreaksBetweenSyllables_AsTheStandard.
    /// </summary>
    [Fact]
    public void E20_LineBreak_AfterZeroWidthSpace()
    {
        foreach (var (left, right) in new[] { ("aaaa", "bbbb"), ("กขคง", "จฉชซ"), ("កខគឃ", "ងចឆជ") })
        {
            var layout = E20Layout(left + "\u200B" + right, E20Width(left) * 1.6);
            Assert.Equal([left + "\u200B", right], E20LineTexts(layout));
        }
        var joined = E20LineTexts(E20Layout("aaaa\u2060bbbb", E20Width("aaaa") * 1.6));
        Assert.NotEqual("aaaa\u2060", joined[0]);
    }

    /// <summary>
    /// UNI 23.2.1 sentences 6–9, 11–13, 19–22, 33–37, 13.4.1 sentences 164–168 (own texts): no line breaks at a no-break space, WJ, ZWNBSP or a
    /// joiner — „aaaa“ X „bbbb cc“ in a width for ten letters breaks after the ordinary space, never at X.
    /// </summary>
    [Fact]
    public void E20_LineBreak_NotAtNoBreakSpaceWordJoinerOrJoiners()
    {
        foreach (var x in new[] { " ", "\u2060", "\uFEFF", "\u200D", " " })
        {
            var lines = E20LineTexts(E20Layout("aaaa" + x + "bbbb cc", E20Width("aaaabbbbcc")));
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("aaaa" + x + "bbbb", lines[0]);
            Assert.Equal("cc", lines[1]);
        }
    }

    /// <summary>
    /// UNI 16.7.1 sentence 67, 16.13.1 sentence 32, 16.4.2 sentences 85–86 and 265–269 (own texts): a word too long for its line is broken
    /// between clusters only — Tai Tham „ᨠ\u1A63“ repeated without spaces in a width where U+1A63 (a spacing mark with an advance of its own) no
    /// longer fits: the line ends before U+1A20, no line starts with the vowel sign (before it did); Khmer ka, coeng, ka repeated: no line starts
    /// between the coeng and its consonant.
    /// </summary>
    [Fact]
    public void E20_HardBreak_BetweenClustersOnly()
    {
        var taiTham = string.Concat(Enumerable.Repeat("ᨠ\u1A63", 6));
        var layout = E20Layout(taiTham, E20Width("ᨠ") * 3.5);
        Assert.True(layout.Lines.Count > 1);
        Assert.All(layout.Lines, l => Assert.Equal("ᨠ", l.Glyphs[0].Text));
        var khmer = string.Concat(Enumerable.Repeat("ក\u17D2ក", 4));
        foreach (var room in new[] { 4.5, 5.5 })
        {
            var lines = E20Layout(khmer, E20Width("ក") * room).Lines;
            Assert.True(lines.Count > 1);
            Assert.All(lines.Skip(1), l => Assert.NotEqual('\u17D2', khmer[l.Start - 1]));
            Assert.All(lines, l => Assert.NotEqual("\u17D2", l.Glyphs[0].Text));
        }
    }

    /// <summary>
    /// UNI 23.2.1 sentences 38–48 (own texts): the soft hyphen is a break opportunity, shown as a hyphen where the line breaks after it and
    /// invisible otherwise — „Donau“ SHY „dampf“ in a width for „Donau-“ gives „Donau-“ (the glyph of the hyphen, with its advance) and „dampf“;
    /// unbroken it has neither glyph nor advance (before it stood visibly within the word and the line broke hard). The expected text of the
    /// block leaves it out like white space; saved, the word is found as „Donaudampf“.
    /// </summary>
    [Fact]
    public void E20_SoftHyphen_BreakWithHyphen_OtherwiseInvisible()
    {
        const string word = "Donau\u00ADdampf";
        var layout = E20Layout(word, E20Width("Donau-") + 1);
        Assert.Equal(2, layout.Lines.Count);
        var hyphen = layout.Lines[0].Glyphs[^1];
        Assert.Equal("\u00AD", hyphen.Text);
        Assert.Equal(hyphen.Face!.Glyph('-'), hyphen.Glyph);
        Assert.True(hyphen.Advance > 0);
        Assert.Equal("dampf", E20LineTexts(layout)[1]);
        var unbroken = E20Layout(word, 10000).Lines[0];
        Assert.DoesNotContain(unbroken.Glyphs, g => g.Text == "\u00AD");
        Assert.Equal(E20Width("Donaudampf"), unbroken.Width, 3);
        Assert.Equal(10, new ShownText(word).Length);
        // saved: the hyphen at the end of the line, the word found whole
        var element = new RichTextElement { Rect = new PdfRectangle(72, 600, 72 + E20Width("Donau-") + 1, 700), Content = new RichText(word, E20Style) };
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        using var output = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(original, [PageItem.From(original)[0].WithElements([element])], output);
        using var saved = PdfDocument.Open(output.ToArray());
        var text = PageText.Create(PageInterpreter.BuildText(saved, saved.Pages[0]));
        Assert.Single(text.Find("Donaudampf"));
    }

    /// <summary>
    /// UNI 16.1.1 sentences 72–73, 23.2.1 sentences 26–32 (own texts): letter spacing passes over ZWSP and the other format characters and does
    /// not widen the spaces of fixed width (U+2002, U+2003, U+2009, U+202F); the letters on either side of a ZWSP keep their spacing (before
    /// every glyph took it).
    /// </summary>
    [Fact]
    public void E20_LetterSpacing_NotOnFormatCharactersAndFixedSpaces()
    {
        var spaced = E20Style with { CharSpacing = 2 };
        foreach (var between in new[] { "\u200B", "\u2060", "\u200D", " ", " ", " ", " " })
        {
            var spacedGlyphs = E20Layout("a" + between + "b", 1000, style: spaced).Lines[0].Glyphs;
            var plainGlyphs = E20Layout("a" + between + "b", 1000).Lines[0].Glyphs;
            Assert.Equal(3, spacedGlyphs.Count);
            Assert.Equal(plainGlyphs[0].Advance + 2, spacedGlyphs[0].Advance, 3);
            Assert.Equal(plainGlyphs[1].Advance, spacedGlyphs[1].Advance, 3);
            Assert.Equal(plainGlyphs[2].Advance + 2, spacedGlyphs[2].Advance, 3);
        }
    }

    /// <summary>
    /// UNI 23.2.1 sentences 49–63 (own texts): U+2029 starts a new paragraph (with the first-line indent), U+2028 a new line of the same paragraph
    /// (without it); neither is drawn (before both stood in the line as glyphs and nothing broke).
    /// </summary>
    [Fact]
    public void E20_LineAndParagraphSeparator()
    {
        var paragraph = E20Layout("a\u2029b", 1000, hangingIndent: -10);
        Assert.Equal(2, paragraph.Lines.Count);
        Assert.Equal(10, paragraph.Lines[0].X, 3);
        Assert.Equal(10, paragraph.Lines[1].X, 3);
        var line = E20Layout("a\u2028b", 1000, hangingIndent: -10);
        Assert.Equal(2, line.Lines.Count);
        Assert.Equal(10, line.Lines[0].X, 3);
        Assert.Equal(0, line.Lines[1].X, 3);
        Assert.True(line.Lines[0].EndsParagraph);
        Assert.Equal(["a", "b"], E20LineTexts(line));
        Assert.Equal(["a", "b"], E20LineTexts(paragraph));
    }

    /// <summary>
    /// UNI 23.1.2 sentences 1–15, 23–28, 23.2.1 sentences 49–58, UAX #14 LB4–LB6 and 5.1 (plain text, field values, free text): CR alone (a
    /// field value of another program), NEL, VT, FF, U+2028 and U+2029 each end a line (before a lone CR, NEL and the separators were drawn
    /// within the line); the information separators FS, GS, RS do not — LineBreak.txt gives them the class CM, and the class BK may not be
    /// extended by tailoring (before they ended a line, docs/FEHLERGEDAECHTNIS.md, No. 1196).
    /// </summary>
    [Fact]
    public void E20_PlainText_LineSeparators()
    {
        var face = ElementFace.For(ElementFont.Sans);
        foreach (var separator in new[] { "\r", "\r\n", "\n", "\u0085", "\u000B", "\u000C", "\u2028", "\u2029" })
        {
            var element = new TextElement { Rect = new PdfRectangle(0, 0, 300, 80), Text = "a" + separator + "b", Multiline = true };
            Assert.Equal(2, ElementAppearance.Layout(element, face).Count);
        }
        foreach (var separator in new[] { "\u001C", "\u001D", "\u001E" })
        {
            var element = new TextElement { Rect = new PdfRectangle(0, 0, 300, 80), Text = "a" + separator + "b", Multiline = true };
            Assert.Single(ElementAppearance.Layout(element, face));
        }
    }

    /// <summary>
    /// UNI 23.8.3 sentences 20–25 (copying): text copied from a page as plain text loses the interlinear annotation characters with the
    /// annotating text — „a\uFFF9b\uFFFAc\uFFFBd“ gives „abd“; the page text keeps all seven characters (sentences 1–19).
    /// </summary>
    [Fact]
    public void E20_Copy_InterlinearAnnotationRemoved_PageTextKeepsIt()
    {
        Assert.Equal("abd", PageText.ForPlainText("a\uFFF9b\uFFFAc\uFFFBd"));
        Assert.Equal("ab", PageText.ForPlainText("a\uFFF9b\uFFFAc"));
        Assert.Equal("plain", PageText.ForPlainText("plain"));
        Assert.Equal("a\uFFF9b\uFFFAc\uFFFBd", E20Text("a\uFFF9b\uFFFAc\uFFFBd").Text);
    }

    /// <summary>UNI 23.8.1 (word lists): every leading ZWNBSP is taken away, not only one byte order mark.</summary>
    [Fact]
    public void E20_WordList_AllLeadingZwnbspRemoved()
    {
        Assert.Equal(["Muster", "Erika"], WordList.Parse("\uFEFF\uFEFFMuster;Erika"));
    }

    // ------------------------------------------------------------ 6, 13. units of the editor (FG 1185)

    /// <summary>
    /// UNI 16.4.2 sentences 85–86 (editor): coeng and consonant are one unit — in „ក\u17D2ក“ the stops are 0 and 3 (not between the coeng and its
    /// ka; since No. 1195 also not between the first ka and the coeng, a grapheme cluster: the larger unit counts); a vowel sign stays at its syllable (Devanagari „क\u093F“: 0 and 2); a virama with ZWJ and consonant is one unit with the consonant before (No. 1195); the backspace key deletes a consonant after the coeng
    /// together with it and a mark alone; surrogate pairs
    /// stay whole.
    /// </summary>
    [Fact]
    public void E20_EditUnits_CoengAndConsonantOneUnit_VowelSignsAtTheirSyllable()
    {
        static int[] Stops(string text) => [.. Enumerable.Range(0, text.Length + 1).Where(i => EditUnits.IsStop(text, i))];
        Assert.Equal([0, 3], Stops("ក\u17D2ក"));
        Assert.Equal(0, EditUnits.Previous("ក\u17D2ក", 3));
        Assert.Equal(3, EditUnits.Next("ក\u17D2ក", 1));
        Assert.Equal([0, 2], Stops("क\u093F"));
        Assert.Equal([0, 4], Stops("क\u094D\u200Dष"));
        Assert.Equal([0, 2, 4], Stops("\U00016A40\U00016A41"));
        // the backspace key: a consonant after the coeng together with it, a mark alone
        Assert.Equal(1, EditUnits.PreviousForDeletion("\u1780\u17D2\u1780", 3));
        Assert.Equal(1, EditUnits.PreviousForDeletion("\u0915\u093F", 2));
        Assert.Equal(2, EditUnits.PreviousForDeletion("\u0628\u0651\u0650", 3));
        Assert.Equal(1, EditUnits.PreviousForDeletion("\u0915\u094D\u200D\u0937", 4));
    }

    /// <summary>
    /// UNI 13.8.1 sentences 13–15, 13.21.1 sentence 9 (editor): words outside the Basic Multilingual Plane (Mro, Chakma, Wancho) and vowel signs
    /// count as word characters per code point (before each UTF-16 unit was tested, so Ctrl+arrow stepped one unit or ran to the line start).
    /// </summary>
    [Fact]
    public void E20_EditUnits_WordsPerCodePoint_MarksInWords()
    {
        Assert.True(EditUnits.IsWordAt("\U00016A40", 0));
        Assert.True(EditUnits.IsWordBefore("x\U00011103\U00011127", 5, out var length) && length == 2);
        Assert.True(EditUnits.IsWordAt("क\u093F", 1));
        Assert.True(EditUnits.IsWordAt("\U0001E2C0", 0));
        Assert.False(EditUnits.IsWordAt(" ", 0));
    }

    /// <summary>
    /// UNI 23.1 sentences 6–9, 23.1.2 sentences 1–28, UAX #14 LB4 (editor): typed control codes are taken as separators — FS, GS, RS, NEL, CR as
    /// a paragraph break, VT and FF as the line separator (FF is a hard line break, class BK, but no new paragraph; before FF began a paragraph,
    /// docs/FEHLERGEDAECHTNIS.md, No. 1196), US as a tab; ESC goes without its control sequence being interpreted (before all were dropped and
    /// the words ran together).
    /// </summary>
    [Fact]
    public void E20_EditUnits_ControlCodesAsSeparators()
    {
        Assert.Equal("eins\u2028zwei", EditUnits.Clean("eins\u000Czwei"));
        Assert.Equal("eins\nzwei", EditUnits.Clean("eins\u001Ezwei"));
        Assert.Equal("a\nb", EditUnits.Clean("a\u0085b"));
        Assert.Equal("a\nb\nc", EditUnits.Clean("a\r\nb\rc"));
        Assert.Equal("a\u2028b", EditUnits.Clean("a\u000Bb"));
        Assert.Equal("a\tb", EditUnits.Clean("a\u001Fb"));
        Assert.Equal("a\nb", EditUnits.Clean("a\u2029b"));
        Assert.Equal("a[1mb", EditUnits.Clean("a\u001B[1mb"));
    }

    /// <summary>
    /// UNI 23.2.5 sentences 16–19, 23.2.6 sentences 1–6 (editor; must): deleting one of a pair of directional formatting characters deletes its
    /// partner — „a RLE b PDF c“ without „RLE b“ is „ac“, „a RLI b PDI“ without „RLI b“ is „a“; inserted text is paired on its own (an opening one
    /// is closed at its end, a lone closing one dropped).
    /// </summary>
    [Fact]
    public void E20_EditUnits_DirectionalPairsKept()
    {
        Assert.Equal([3], EditUnits.OrphanedPartners("a\u202Bb\u202Cc", 1, 2));
        Assert.Equal([1], EditUnits.OrphanedPartners("a\u202Bb\u202Cc", 2, 2));
        Assert.Equal([3], EditUnits.OrphanedPartners("a\u2067b\u2069", 1, 2));
        Assert.Empty(EditUnits.OrphanedPartners("a\u202Bb\u202Cc", 1, 3));
        Assert.Empty(EditUnits.OrphanedPartners("a\u200Fb", 1, 1));   // marks are not paired
        Assert.Equal("\u202Bx\u202C", EditUnits.Balanced("\u202Bx"));
        Assert.Equal("\u2067\u202Ax\u202C\u2069", EditUnits.Balanced("\u2067\u202Ax"));
        Assert.Equal("x", EditUnits.Balanced("x\u2069"));
        Assert.Equal("\u202Bx\u202C\ny", EditUnits.Balanced("\u202Bx\ny"));
    }

    // ------------------------------------------------------------ 16. open rows of uni-10, uni-16, uni-17

    /// <summary>
    /// UNI 13.1.1 sentences 8–10 and 27–30, 13.5.1 sentences 56–60 (own texts, text order): Thaana letters and their marks take level 1, the
    /// digits after them level 2, the number with U+066B as a whole level 2; Mongolian stays at level 0 (left to right).
    /// </summary>
    [Fact]
    public void E20_BidiLevels_ThaanaAndMongolian()
    {
        Assert.Equal([1, 1, 1, 1, 1, 2, 2], Core.Fonts.Layout.Bidi.Levels("ތ\u07A6ތ\u07B0 12"));
        Assert.Equal([1, 1, 2, 2], Core.Fonts.Layout.Bidi.Levels("ތ 12"));
        Assert.Equal([1, 1, 2, 2, 2], Core.Fonts.Layout.Bidi.Levels("ތ 1٫5"));
        Assert.True(Core.Fonts.Layout.Bidi.HasRightToLeft("ތ"));
        Assert.All(Core.Fonts.Layout.Bidi.Levels("ᠮᠣᠩᠭᠣᠯ 12"), l => Assert.Equal(0, l));
    }

    /// <summary>
    /// UNI 23.2.1 sentences 33–37 (own texts): ZWSP and ZWNBSP do not break the joining of Arabic letters (beh, ZWSP, beh: initial and final);
    /// a ZWJ in a word gives no break opportunity (shown in E20_LineBreak_NotAtNoBreakSpaceWordJoinerOrJoiners).
    /// </summary>
    [Fact]
    public void E20_ZeroWidthSpaces_DoNotBreakTheJoining()
    {
        foreach (var between in new[] { 0x200B, 0xFEFF })
        {
            var forms = Core.Fonts.Layout.ArabicJoining.Forms([0x0628, between, 0x0628]);
            Assert.Equal(Core.Fonts.Layout.ArabicJoining.Form.Initial, forms[0]);
            Assert.Equal(Core.Fonts.Layout.ArabicJoining.Form.Final, forms[2]);
        }
    }

    /// <summary>
    /// UNI 18.4.6 sentences 15 and 23 (own texts): a furigana sequence after its kanji stays one line in text order — no second line of
    /// annotation; kana with the tone mark U+1AFF5 are glyphs in text order on one line.
    /// </summary>
    [Fact]
    public void E20_FuriganaSequence_OneLineInTextOrder()
    {
        const string text = "恬恬聽チ\u0305ァム\U0001AFF5";
        var layout = E20Layout(text, 10000);
        Assert.Single(layout.Lines);
        var glyphs = layout.Lines[0].Glyphs;
        Assert.Equal(glyphs.OrderBy(g => g.CharStart).Select(g => g.CharStart), glyphs.Select(g => g.CharStart));
        Assert.Equal(text, string.Concat(glyphs.Select(g => g.Text)));
    }

    /// <summary>
    /// Security (FG 1184): a crafted run of 400 000 marks after one letter (alternating combining classes, which canonical ordering sorts) is
    /// folded in pieces of at most 32 code points — in linear time, under five seconds, for the page text and for a term.
    /// </summary>
    [Fact]
    public void E20_SearchFolding_LongRunOfMarks_Bounded()
    {
        var text = "a" + string.Concat(Enumerable.Repeat("\u0301\u0316", 200_000)) + "b";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (folded, map) = SearchFolding.Fold(text, page: true);
        _ = PageText.NormalizeQuery(text);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.Equal(folded.Length + 1, map.Length);
        Assert.EndsWith("b", folded);
    }
    // ------------------------------------------------------------ 9. text of fonts without /ToUnicode beyond U+3000 (FG 1187)

    /// <summary>A page whose font F1 is <paramref name="font"/> embedded as CIDFontType2 (Identity-H, CIDToGIDMap Identity, no /ToUnicode).</summary>
    internal static byte[] E20CidTrueTypePage(byte[] font, string content)
    {
        var pdf = new TestPdf();
        var cat = TestPdf.Dict(("Type", TestPdf.N("Catalog")));
        var catNumber = pdf.Add(cat);
        var pagesNumber = pdf.Add(new PdfDictionary());
        var program = pdf.Add(TestPdf.FlateStream(font, TestPdf.Dict(("Length1", PdfInteger.Of(font.Length)))));
        var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ProbeSans")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(-203, -303, 1050, 910)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(905)), ("Descent", PdfInteger.Of(-212)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(program)));
        var cid = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N("ProbeSans")),
            ("CIDSystemInfo", TestPdf.Dict(("Registry", new PdfString(Encoding.ASCII.GetBytes("Adobe"))), ("Ordering", new PdfString(Encoding.ASCII.GetBytes("Identity"))),
                ("Supplement", PdfInteger.Of(0)))), ("FontDescriptor", descriptor), ("CIDToGIDMap", TestPdf.N("Identity")));
        var type0 = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("ProbeSans")),
            ("Encoding", TestPdf.N("Identity-H")), ("DescendantFonts", new PdfArray([cid]))));
        var contentNumber = pdf.Add(TestPdf.FlateStream(Encoding.Latin1.GetBytes(content)));
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNumber)), ("MediaBox", TestPdf.Box(0, 0, 400, 200)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(type0)))))), ("Contents", TestPdf.Ref(contentNumber))));
        pdf.Replace(pagesNumber, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        cat.Set(PdfName.Pages, TestPdf.Ref(pagesNumber));
        pdf.RootNumber = catNumber;
        return pdf.Build();
    }

    /// <summary>The probe of FG 1187: Liberation Sans with a cmap of kana, Hangul, Han, a halfwidth form and a character beyond the BMP.</summary>
    internal static (byte[] Pdf, string Expected) E20KanaProbe()
    {
        int[] codes = [0x30A2, 0xAC00, 0x4E00, 0xFF71, 0x3105, 0xA000, 0x20000];
        var letters = "ABCDEFG";
        var font = E12bFont([.. codes.Select((c, i) => (c, c, Ot02G(letters[i])))], null);
        var hex = string.Concat(letters.Select(l => Ot02G(l).ToString("X4")));
        return (E20CidTrueTypePage(font, $"BT /F1 20 Tf 20 100 Td <{hex}> Tj ET"), string.Concat(codes.Select(char.ConvertFromUtf32)));
    }

    /// <summary>
    /// UNI 18 (reader, side finding of uni-16): an embedded TrueType CIDFont with Identity-H and without /ToUnicode reads its text from the
    /// inverted cmap — also beyond U+2FFF: katakana „ア“, Hangul „가“, Han „一“, halfwidth „ｱ“, Bopomofo, Yi and an ideograph of extension B
    /// (before the inversion stopped at U+2FFF, so these glyphs read as nothing). Two of the three reference readers read the same (measured
    /// m7-e20).
    /// </summary>
    [Fact]
    public void E20_TrueTypeWithoutToUnicode_TextFromTheWholeCmap()
    {
        var (file, expected) = E20KanaProbe();
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)   // for the measurement in the reference readers
            File.WriteAllBytes(Path.Combine(folder, "e20-kana-ohne-tounicode.pdf"), file);
        using var doc = PdfDocument.Open(file);
        var text = PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0])).Text;
        Assert.Equal(expected, text.Trim());
    }

    /// <summary>
    /// Security (FG 1187): a cmap claiming every code point (a format 12 group U+0000–U+10FFFF) is inverted within the bound of
    /// <c>GlyphTexts.MaxCodes</c> and quickly; the lowest code point keeps the glyph.
    /// </summary>
    [Fact]
    public void E20_WholeCmapInversion_Bounded()
    {
        var font = OpenTypeFont.Parse(E12bFont([(0x21, 0x10FFFF, Ot02G('A'))], null));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var texts = Core.Fonts.Layout.GlyphTexts.Of(font);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.True(texts.Count <= font.GlyphCount);
    }
}

/// <summary>
/// M7, package E20: the fallback face for Han characters after the language of the block (docs/FEHLERGEDAECHTNIS.md, No. 1186). In the
/// collection of the CJK fonts: the folder is shared by all tests (No. 774).
/// </summary>
[Collection(CjkFontsCollection.Name)]
public sealed class E20CjkFallbackTests
{
    private static string Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));


    /// <summary>
    /// UNI 18.1.10 sentences 10–16, 18.1.11 sentence 2, 18.2.1 sentences 72–77 (own texts): typed compatibility ideographs (U+FA30, U+2F800)
    /// stay themselves in the text, in the saved page and in its text (not made unified ideographs); an ideographic description sequence is
    /// three glyphs with their own texts and carets, not drawn as one character.
    /// </summary>
    [Fact]
    public void E20_TypedCompatibilityIdeographsAndDescriptionSequence_AsTyped()
    {
        CjkFonts.FolderForTests = Path.Combine(Repo(), "Schriften", "CJK");
        try
        {
            const string typed = "x侮\U0002F800 ⿱井蛙";
            var element = new RichTextElement
            {
                Rect = new PdfRectangle(72, 600, 500, 700),
                Content = new RichText(typed, new RichStyle { Family = "Liberation Sans", Size = 14 }),
                Language = "ja",
            };
            var glyphs = RichLayout.Of(element).Lines.SelectMany(l => l.Glyphs).ToList();
            var description = glyphs.TakeLast(3).ToList();
            Assert.Equal(["⿱", "井", "蛙"], description.Select(g => g.Text));
            Assert.All(description, g => Assert.NotEqual(0, g.Glyph));
            Assert.Equal(3, description.Select(g => g.CharStart).Distinct().Count());
            using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
            using var output = new MemoryStream();
            Core.Pdf.Writing.PdfWriter.Write(original, [PageItem.From(original)[0].WithElements([element])], output);
            using var saved = PdfDocument.Open(output.ToArray());
            var text = PageText.Create(PageInterpreter.BuildText(saved, saved.Pages[0])).Text;
            Assert.Contains("侮", text);
            Assert.Contains("\U0002F800", text);
            Assert.Contains("⿱井蛙", text);
        }
        finally
        {
            CjkFonts.FolderForTests = null;
        }
    }

    /// <summary>
    /// UNI 18.1.12 sentence 10 (own texts): the kanbun U+3192 is drawn with the glyph of Noto Sans CJK JP in full size on the baseline, its
    /// advance from hmtx — no reduction (PDF-Knead has no kanbun setting).
    /// </summary>
    [Fact]
    public void E20_Kanbun_GlyphOfTheFontInFullSize()
    {
        CjkFonts.FolderForTests = Path.Combine(Repo(), "Schriften", "CJK");
        try
        {
            var element = new RichTextElement
            {
                Rect = new PdfRectangle(72, 600, 500, 700),
                Content = new RichText("㆒", new RichStyle { Family = "Liberation Sans", Size = 20 }),
                Language = "ja",
            };
            var glyph = RichLayout.Of(element).Lines.Single().Glyphs.Single();
            Assert.NotEqual(0, glyph.Glyph);
            Assert.Equal(0, glyph.YOffset);
            Assert.Equal(glyph.Face!.Font.AdvanceWidth(glyph.Glyph) * 20.0 / glyph.Face.Font.UnitsPerEm, glyph.Advance, 3);
        }
        finally
        {
            CjkFonts.FolderForTests = null;
        }
    }

    /// <summary>
    /// UNI 18.1.10 sentences 20–21, 18.1.11 sentences 3–4 (own texts): a standardized variation sequence of a compatibility ideograph (U+4FAE
    /// U+FE00 for U+FA30; U+4E3D U+FE00 for U+2F800) is one glyph with the text of both characters: the glyph cmap format 14 of Noto Sans CJK JP
    /// gives the sequence (default sequences: that of the base character), where the font does not list it the glyph of the base character.
    /// </summary>
    [Fact]
    public void E20_StandardizedVariationSequence_OneGlyphAfterCmapFormat14()
    {
        CjkFonts.FolderForTests = Path.Combine(Repo(), "Schriften", "CJK");
        try
        {
            foreach (var (sequence, compatibility) in new[] { ("侮\uFE00", 0xFA30), ("丽\uFE00", 0x2F800) })
            {
                var element = new RichTextElement
                {
                    Rect = new PdfRectangle(72, 600, 500, 700),
                    Content = new RichText(sequence, new RichStyle { Family = "Liberation Sans", Size = 20 }),
                    Language = "ja",
                };
                var glyph = RichLayout.Of(element).Lines.Single().Glyphs.Single();
                Assert.Equal(sequence, glyph.Text);
                // the glyph cmap format 14 gives the sequence, otherwise the base character's (mechanism: M7NormTests.Format14VariationSequences_AreRead)
                var variant = glyph.Face!.Font.GlyphForVariation(char.ConvertToUtf32(sequence, 0), 0xFE00);
                Assert.Equal(variant > 0 ? variant : glyph.Face.Glyph(char.ConvertToUtf32(sequence, 0)), glyph.Glyph);
                Assert.NotEqual(0, glyph.Glyph);
            }
        }
        finally
        {
            CjkFonts.FolderForTests = null;
        }
    }
    /// <summary>The PostScript name of the face that draws <paramref name="text"/> in a block of <paramref name="language"/>.</summary>
    private static string Face(string text, string? language)
    {
        var element = new RichTextElement
        {
            Rect = new PdfRectangle(72, 600, 500, 700),
            Content = new RichText(text, new RichStyle { Family = "Liberation Sans", Size = 14 }),
            Language = language,
        };
        return RichLayout.Of(element).Lines.SelectMany(l => l.Glyphs).Single().Face!.BaseName;
    }

    /// <summary>
    /// UNI 18.1.3 sentences 76–80 (own texts): the glyphs of Han characters differ per region and the user chooses the font for a region — the
    /// fallback face for „骨“ U+9AA8 follows the language of the block: zh-Hans, zh-CN and zh simplified Chinese, zh-Hant, zh-TW and zh-HK
    /// traditional Chinese, ja Japanese, ko Korean; without a language, and with a language of no CJK script, the UI language decides as
    /// before (German: Japanese). Before the UI language decided always.
    /// </summary>
    [Fact]
    public void E20_HanFallbackFace_FollowsTheLanguageOfTheBlock()
    {
        CjkFonts.FolderForTests = Path.Combine(Repo(), "Schriften", "CJK");
        var ui = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
        try
        {
            foreach (var (language, face) in new (string?, string)[]
                     {
                         ("zh-Hans", "SC"), ("zh-CN", "SC"), ("zh", "SC"), ("zh-Hant", "TC"), ("zh-TW", "TC"), ("zh-HK", "TC"), ("ja", "JP"), ("ko", "KR"),
                         (null, "JP"), ("de", "JP"),
                     })
                Assert.Contains("CJK" + face, Face("骨", language), StringComparison.OrdinalIgnoreCase);
            // kana and Hangul keep the face of their script whatever the language
            Assert.Contains("CJKJP", Face("ア", "zh-Hans"), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CJKKR", Face("가", "ja"), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CultureInfo.CurrentUICulture = ui;
            CjkFonts.FolderForTests = null;
        }
    }
}
