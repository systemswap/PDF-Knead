using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, packages uni-01 to uni-03 (UAX #9, revision 50 for Unicode 16.0.0): the rows of the drafts that still asked for a test — the levels
/// of single rules and examples (W1–W7, N1, I1, I2, the higher-level protocols HL1–HL5), what own text keeps of the directional formatting
/// characters on saving and in the text read back, a mark at its base after reordering, the joiners and the line width from shaped glyphs.
/// The rules as a whole are run by <c>BidiConformanceTests</c> (BidiTest.txt and BidiCharacterTest.txt) and <c>M7Tests.uni-01.cs</c>.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>An element with text in the style of the UAX #9 tests (Liberation Sans 12, Arabic from the fallback font).</summary>
    private static RichTextElement UniTestsElement(string text, double width = 300, double height = 50) =>
        new() { Rect = new PdfRectangle(0, 0, width, height), Content = new RichText(text, Uax9Style) };

    /// <summary>A blank page with the elements, saved (own fonts only, as in the other tests that save edited text).</summary>
    private static byte[] UniTestsSaved(params PageElement[] elements)
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            using var blank = PdfDocument.Open(TestPdf.Simple(1).Build());
            using var output = new MemoryStream();
            PdfWriter.Write(blank, [PageItem.From(blank)[0].WithElements(elements)], output);
            return output.ToArray();
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }

    /// <summary>The text of the first page of a saved file as the text extraction reads it.</summary>
    private static string UniTestsPageText(byte[] saved)
    {
        using var doc = PdfDocument.Open(saved);
        return PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0])).Text;
    }

    // ------------------------------------------------------------ uni-01: 2, 2.7, 3, 3.2

    /// <summary>
    /// UAX #9, 2 sentences 16–17, and 6.5 sentences 6–8: own text keeps the directional formatting characters as they were typed — the block
    /// data written with the saved text „a RLI b PDI“ hold exactly that text, both characters unchanged and nothing inserted.
    /// </summary>
    [Fact]
    public void Uax9_FormattingCharacters_WrittenIntoTheBlockData()
    {
        var saved = UniTestsSaved(UniTestsElement("a\u2067b\u2069"));
        using var doc = PdfDocument.Open(saved);
        var xobjects = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!;
        var block = Assert.Single(xobjects.Keys.Select(k => xobjects.Get(k)).OfType<PdfStream>(), ContentElements.IsBlock);
        var parts = ElementStore.DataOf(block.Dictionary)!.GetArray(PdfName.Get("Abschnitte"))!;
        var text = string.Concat(Enumerable.Range(0, parts.Count).Select(i => (parts.Get(i) as PdfDictionary)?.GetString(PdfName.Get("Text"))?.ToText()));
        Assert.Equal("a\u2067b\u2069", text);
    }

    /// <summary>
    /// UAX #9, 2 sentences 16–17, and 6.5 sentences 6–8: own text saved and opened again is the same block with the same text — „a RLI b PDI“
    /// with both formatting characters, and „a \u05D0\u05D1 c“ with its Hebrew word in logical order.
    /// </summary>
    [Fact]
    public void Uax9_FormattingCharacters_ReopenedAsTheSameText()
    {
        foreach (var stored in new[] { "a\u2067b\u2069", "a \u05D0\u05D1 c" })
        {
            using var doc = PdfDocument.Open(UniTestsSaved(UniTestsElement(stored)));
            var element = Assert.Single(PageItem.From(doc)[0].Elements.OfType<RichTextElement>());
            Assert.Equal(stored, element.Content.Text);
        }
    }

    /// <summary>
    /// Own text whose only white space is a tab — Latin, and Hebrew — is the same block again after saving and opening; the tab glyph stands for
    /// a space in ToUnicode (docs/FEHLERGEDAECHTNIS.md, No. 1178; before it had no text, and the block stayed page content).
    /// </summary>
    [Fact]
    public void Fg1178_TabWithoutSpace_ReopenedAsTheSameText()
    {
        foreach (var stored in new[] { "a\tb", "\u05D0\u05D1\t\u05D2" })
        {
            using var doc = PdfDocument.Open(UniTestsSaved(UniTestsElement(stored)));
            var element = Assert.Single(PageItem.From(doc)[0].Elements.OfType<RichTextElement>());
            Assert.Equal(stored, element.Content.Text);
        }
    }

    /// <summary>
    /// UAX #9, 3.4 sentences 1–8 and 19–21 (L2): own right-to-left text drawn line by line in visual order is the same block again after saving and
    /// opening — Hebrew words, Arabic words, Arabic with a Latin word, a ligature and a mark, brackets, digits in a Hebrew paragraph, a Hebrew
    /// paragraph with a Latin word broken into three lines, a left-to-right paragraph whose Hebrew phrase is broken across two lines, two paragraphs,
    /// and Arabic letters whose glyphs the appearance marks with /ActualText (docs/FEHLERGEDAECHTNIS.md, No. 1177).
    /// </summary>
    [Fact]
    public void Uax9_RightToLeftBlocks_ReopenedAsTheSameText()
    {
        var cases = new (string Text, double Width, int Lines)[]
        {
            ("\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD", 300, 1),
            ("\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645", 300, 1),
            ("\u0627\u0644\u0646\u0635 abc \u0627\u0644\u0646\u0635", 300, 1),
            ("\u0644\u0627 \u0628\u064E", 300, 1),
            ("\u0628\u064E\u0651 \u0644\u0627", 300, 1),
            ("\u05D0\u05D1 (\u05D2\u05D3) \u05D4\u05D5", 300, 1),
            ("\u05D0 12 \u05D1", 300, 1),
            ("\u05D0\u05D1\u05D2 \u05D3\u05D4\u05D5 \u05D6\u05D7\u05D8 abc \u05D9\u05DB\u05DC \u05DE\u05E0\u05E1 \u05E2\u05E4\u05E6", 70, 3),
            ("abc \u05D0\u05D1\u05D2 \u05D3\u05D4\u05D5 \u05D6\u05D7\u05D8 xyz", 70, 2),
            ("\u05D0\u05D1 a\n\u05D2\u05D3", 300, 2),
        };
        foreach (var (stored, width, lines) in cases)
        {
            var original = UniTestsElement(stored, width, 100);
            Assert.True(RichLayout.Of(original).Lines.Count >= lines, stored);
            using var doc = PdfDocument.Open(UniTestsSaved(original));
            var elements = PageItem.From(doc)[0].Elements.OfType<RichTextElement>().ToList();
            Assert.True(elements.Count == 1, stored);
            Assert.Equal(stored, elements[0].Content.Text);
        }
        // beh, teh and yeh share the glyph of their body: the appearance gives teh and yeh /ActualText of their own, and the block comes back
        var shared = UniTestsElement("\u0628\u0628 \u062A\u062A \u064A\u064A");
        Assert.Contains("ActualText", System.Text.Encoding.Latin1.GetString(ElementAppearance.For(shared).Form.RawData!), StringComparison.Ordinal);
        using (var doc = PdfDocument.Open(UniTestsSaved(shared)))
            Assert.Equal(shared.Content.Text, Assert.Single(PageItem.From(doc)[0].Elements.OfType<RichTextElement>()).Content.Text);
    }

    /// <summary>
    /// UAX #9, 3.4 sentences 1–8 and 19–21: the comparison that takes a stored block back stays strict for right-to-left text (No. 1177): „a \u05D0\u05D1 c“ is
    /// shown by the drawn „a\u05D1\u05D0c“ only — not by its logical order, a missing or an added letter, or another letter; a ligature's text may stand in
    /// either order, and so may the marks on one letter (not one of them missing); a Hebrew paragraph matches as one line or as two only if the
    /// drawing changes its baseline there and each line is in its visual order; and a crafted text whose characters agree at every change of baseline
    /// but not in their order is refused within an effort budget, not in quadratic time.
    /// </summary>
    [Fact]
    public void Uax9_ShownText_StrictForRightToLeft()
    {
        static List<int> Each(string drawn) => [.. Enumerable.Range(0, drawn.Length)];
        var mixed = new ShownText("a \u05D0\u05D1 c");
        Assert.True(mixed.Matches("a\u05D1\u05D0c", Each("a\u05D1\u05D0c"), []));
        foreach (var wrong in new[] { "a\u05D0\u05D1c", "a\u05D1c", "a\u05D1\u05D0\u05D0c", "a\u05D1\u05D2c", "\u05D1\u05D0ac" })
            Assert.False(mixed.Matches(wrong, Each(wrong), []), wrong);
        // lam-alef as one glyph: its text in logical order where the visual order has the two letters reversed
        var ligature = new ShownText("\u0644\u0627");
        Assert.True(ligature.Matches("\u0644\u0627", [0], []));
        Assert.True(ligature.Matches("\u0627\u0644", [0, 1], []));
        Assert.False(ligature.Matches("\u0644\u0627", [0, 1], []));
        // beh with fatha and shadda: the shaper puts shadda first, the marks may stand in either order — but none may be missing
        var marks = new ShownText("\u0628\u064E\u0651");
        Assert.True(marks.Matches("\u064E\u0651\u0628", Each("123"), []));
        Assert.True(marks.Matches("\u0651\u064E\u0628", Each("123"), []));
        Assert.False(marks.Matches("\u064E\u064E\u0628", Each("123"), []));
        // „\u05D0\u05D1 \u05D2\u05D3“ on one baseline or on two: the visual order of each line drawn; drawn in logical order it is refused, also on two lines
        var paragraph = new ShownText("\u05D0\u05D1 \u05D2\u05D3");
        Assert.True(paragraph.Matches("\u05D3\u05D2\u05D1\u05D0", Each("1234"), []));
        Assert.True(paragraph.Matches("\u05D1\u05D0\u05D3\u05D2", Each("1234"), [2]));
        Assert.False(paragraph.Matches("\u05D1\u05D0\u05D3\u05D2", Each("1234"), []));
        Assert.False(paragraph.Matches("\u05D0\u05D1\u05D2\u05D3", Each("1234"), []));
        Assert.False(paragraph.Matches("\u05D0\u05D1\u05D2\u05D3", Each("1234"), [2]));
        // effort: 200 000 Hebrew letters drawn in logical order with a change of baseline after every second one (each line would have to
        // be reversed): refused within the budget
        var stored = string.Concat(Enumerable.Repeat("\u05D0\u05D1", 100_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(new ShownText(stored).Matches(stored, Each(stored), [.. Enumerable.Range(1, stored.Length / 2 - 1).Select(i => 2 * i)]));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    /// <summary>
    /// UAX #9, 2.7 sentences 3–6 and 7–24: the formatting offers no direction of its own and nothing is written as markup — text „ab“ has no
    /// right-to-left character, its appearance holds neither /ReversedChars nor any marked content, and the saved file is a PDF without HTML
    /// or CSS direction attributes.
    /// </summary>
    [Fact]
    public void Uax9_NoDirectionMarkupInFormattingOrOutput()
    {
        var element = UniTestsElement("ab");
        Assert.False(Bidi.HasRightToLeft(element.Content.Text));
        var content = System.Text.Encoding.Latin1.GetString(ElementAppearance.For(element).Form.RawData!);
        Assert.DoesNotContain("ReversedChars", content, StringComparison.Ordinal);
        Assert.DoesNotContain("BDC", content, StringComparison.Ordinal);
        Assert.DoesNotContain("BMC", content, StringComparison.Ordinal);
        var saved = System.Text.Encoding.Latin1.GetString(UniTestsSaved(element));
        Assert.StartsWith("%PDF-", saved, StringComparison.Ordinal);
        foreach (var markup in new[] { "dir=", "unicode-bidi", "direction:", "ReversedChars" })
            Assert.DoesNotContain(markup, saved, StringComparison.Ordinal);
    }

    /// <summary>The line of the page text that holds the element's text (the blank test page carries a line of its own).</summary>
    private static string UniTestsElementLine(string stored) =>
        Assert.Single(UniTestsPageText(UniTestsSaved(UniTestsElement(stored))).Split('\n'), l => l.Contains('a', StringComparison.Ordinal)).Trim();

    /// <summary>
    /// UAX #9, 2.7 sentences 25–26, and 6.5 sentences 1–5: no formatting character is inserted when own text is read back from the saved
    /// page — „a \u05D0\u05D1 c“ and „\u05D0 a“ come out with exactly their characters (there is no higher-level protocol whose result would have to be kept).
    /// </summary>
    [Fact]
    public void Uax9_ExtractedText_NoInsertedFormattingCharacters()
    {
        foreach (var stored in new[] { "a \u05D0\u05D1 c", "\u05D0 a" })
        {
            var read = UniTestsElementLine(stored);
            Assert.DoesNotContain(read, c => Bidi.IsControl(c));
            Assert.Equal(stored.Order(), read.Order());
        }
    }

    /// <summary>
    /// UAX #9, 2.7 sentences 25–26, and 6.5 sentences 1–5: own text read back from the saved page (copying, search) is its logical text, not
    /// the visual order of the drawn glyphs — „a \u05D0\u05D1 c“ and the right-to-left paragraph „\u05D0\u05D1 a \u05D2“ come out as stored. „\u05D0 a“ is drawn as „a \u05D0“, the
    /// same as the left-to-right text „a \u05D0“; a line beginning with a left-to-right letter is read left to right, as both reference readers that
    /// reorder do (docs/FEHLERGEDAECHTNIS.md, No. 1177).
    /// </summary>
    [Fact]
    public void Uax9_ExtractedText_InLogicalOrder()
    {
        foreach (var (stored, read) in new[] { ("a \u05D0\u05D1 c", "a \u05D0\u05D1 c"), ("\u05D0\u05D1 a \u05D2", "\u05D0\u05D1 a \u05D2"), ("\u05D0 a", "a \u05D0") })
            Assert.Equal(read, UniTestsElementLine(stored));
    }

    /// <summary>
    /// UAX #9, 3.4 sentences 19–21 (L2), read back (docs/FEHLERGEDAECHTNIS.md, No. 1177): in own text „a \u05D0\u05D1 c“ saved, the search finds „\u05D0\u05D1“ on its
    /// two glyphs, „\u05D0“ alone covers the glyph of alef only (its quadrilateral not reaching over beh, which stands left of it), and „Suchen und
    /// schwärzen“ of „\u05D0“ removes exactly that glyph.
    /// </summary>
    [Fact]
    public void Uax9_SearchAndRedact_OwnRightToLeftText()
    {
        using var doc = PdfDocument.Open(UniTestsSaved(UniTestsElement("a \u05D0\u05D1 c")));
        var page = doc.Pages[0];
        var text = PageText.Create(PageInterpreter.BuildText(doc, page));
        Assert.Equal(["\u05D0", "\u05D1"], text.GlyphsOf(Assert.Single(text.Find("\u05D0\u05D1"))).Select(g => g.Text));
        UniTestsRedactsOnly(doc, page, text, "\u05D0");
    }

    /// <summary>
    /// UAX #9, 3.4 sentences 19–21 (L2), read back from a file of another producer that writes right-to-left text in visual order (No. 1177): the
    /// line drawn „ab \u05D1\u05D0“ reads „ab \u05D0\u05D1“ (as two of the three reference readers read it, probe fremd02 of m7-e14), the search finds „\u05D0\u05D1“ on its two
    /// glyphs, and „Suchen und schwärzen“ of „\u05D1“ removes beh alone.
    /// </summary>
    [Fact]
    public void Uax9_SearchAndRedact_ForeignVisualOrder()
    {
        // codes 1–6: alef, beh, gimel, a, b, space; drawn from the left: a b space beh alef
        using var doc = PdfDocument.Open(MisreadTextTests.File(["\u05D0", "\u05D1", "\u05D2", "a", "b", " "], "0405060201"));
        var page = doc.Pages[0];
        var text = PageText.Create(PageInterpreter.BuildText(doc, page));
        Assert.Equal("ab \u05D0\u05D1", text.Text.Split('\n')[0]);
        Assert.Equal(["\u05D0", "\u05D1"], text.GlyphsOf(Assert.Single(text.Find("\u05D0\u05D1"))).Select(g => g.Text));
        UniTestsRedactsOnly(doc, page, text, "\u05D1");
    }

    /// <summary>Searches a single letter, checks that its areas cover its glyph only, and that redacting them removes that glyph alone.</summary>
    private static void UniTestsRedactsOnly(PdfDocument doc, PdfPage page, PageText text, string letter)
    {
        var hit = Assert.Single(text.Find(letter));
        var glyph = Assert.Single(text.GlyphsOf(hit));
        Assert.Equal(letter, glyph.Text);
        var quad = Assert.Single(text.Quads(hit)).Bounds;
        var own = new TextQuad(glyph.X0, glyph.Y0, glyph.X1, glyph.Y1, glyph.X2, glyph.Y2, glyph.X3, glyph.Y3).Bounds;
        Assert.Equal(own.Left, quad.Left, 3);
        Assert.Equal(own.Right, quad.Right, 3);
        var redacted = Redactor.Apply(doc, page, text.RedactionAreas(hit));
        Assert.Equal(1, redacted.RemovedGlyphs);
        Assert.Equal(letter, redacted.RemovedText);
    }

    /// <summary>
    /// UAX #9, 3 sentences 19–21, and 3.4 sentences 59–65 (L3): a combining mark stays at its base after reordering — beh with fatha
    /// saved: the fatha is drawn within the horizontal extent of the beh, not before it as a glyph of its own in the visual order. Since
    /// docs/FEHLERGEDAECHTNIS.md, No. 1182 the letter and its mark are written within one /ActualText and read back as one text on the area of
    /// both: that area is as wide as the beh alone.
    /// </summary>
    [Fact]
    public void Uax9_MarkStaysOnItsBaseAfterReordering()
    {
        static (double Left, double Right) Extent(string stored, string text)
        {
            using var doc = PdfDocument.Open(UniTestsSaved(UniTestsElement(stored)));
            var glyph = Assert.Single(PageInterpreter.BuildText(doc, doc.Pages[0]), g => g.Text == text);
            return (Math.Min(glyph.X0, glyph.X1), Math.Max(glyph.X0, glyph.X1));
        }
        var (left, right) = Extent("\u0628", "\u0628");
        Assert.True(right - left > 1, $"beh {left}..{right}");
        var (markLeft, markRight) = Extent("\u0628\u064E", "\u0628\u064E");
        Assert.InRange(markLeft, left - 0.5, left + 0.5);
        Assert.InRange(markRight, right - 0.5, right + 0.5);
    }

    /// <summary>UAX #9, 3.2 sentence 11: an object replacement character counts as a neutral — between two Hebrew letters it takes level 1.</summary>
    [Fact]
    public void Uax9_ObjectReplacementCharacter_IsNeutral()
    {
        Assert.Equal(BidiClass.ON, Bidi.ClassOf(0xFFFC));
        Assert.Equal([1, 1, 1], Bidi.Levels("\u05D0\uFFFC\u05D1"));
    }

    // ------------------------------------------------------------ uni-02: 3.3.3 to 3.4

    /// <summary>
    /// UAX #9, 3.3.3 sentences 9–10, and 6.1 sentences 1–5: the joiners act on the characters next to them in memory, before reordering —
    /// beh followed by ZWJ takes its initial form, beh ZWNJ beh leaves both isolated; the shaper, working on the stored text, gives the beh
    /// before a ZWJ the glyph of the initial form.
    /// </summary>
    [Fact]
    public void Uax9_Joiners_FormsFromTheStoredNeighbours()
    {
        Assert.Equal(ArabicJoining.Form.Initial, ArabicJoining.Forms([0x0628, 0x200D])[0]);
        var apart = ArabicJoining.Forms([0x0628, 0x200C, 0x0628]);
        Assert.Equal(ArabicJoining.Form.Isolated, apart[0]);
        Assert.Equal(ArabicJoining.Form.Isolated, apart[2]);

        var shaper = TextShaper.For(UniTestsArabicFace().Font);
        var initial = shaper.Shape("\u0628\u0628").First(g => g.Text == "\u0628").Glyph;
        var isolated = shaper.Shape("\u0628").First(g => g.Text == "\u0628").Glyph;
        Assert.NotEqual(isolated, initial);
        var joined = shaper.Shape("\u0628\u200D").First(g => g.Text == "\u0628");
        Assert.Equal(ArabicJoining.Form.Initial, joined.Form);
        Assert.Equal(initial, joined.Glyph);
    }

    /// <summary>
    /// UAX #9, 3.3.3 sentences 9–10, and 6.1 sentences 1–5: in own text „beh ZWJ“ is drawn with the initial beh — the joiner stays next to the
    /// letter when the text is shaped.
    /// </summary>
    [Fact]
    public void Uax9_Joiners_ZwjInOwnText_DrawsTheInitialForm()
    {
        var face = UniTestsArabicFace();
        var initial = TextShaper.For(face.Font).Shape("\u0628\u0628").First(g => g.Text == "\u0628").Glyph;
        var laid = RichLayout.Of(UniTestsElement("\u0628\u200D")).Lines.SelectMany(l => l.Glyphs).ToList();
        Assert.Equal(initial, Assert.Single(laid, g => g.Text == "\u0628").Glyph);
    }

    /// <summary>The face own text draws Arabic letters with (the fallback of the UAX #9 style).</summary>
    private static ElementFace UniTestsArabicFace() =>
        RichLayout.Of(UniTestsElement("\u0628")).Lines[0].Glyphs.First(g => g.Text == "\u0628").Face!;

    /// <summary>
    /// UAX #9, 3.3.4 sentences 4–9 (W1): a nonspacing mark takes the type of the character before it — after a Latin letter in a Hebrew
    /// paragraph L (levels 1, 2, 2); at the start of the paragraph it takes sos, here R (levels 1, 1).
    /// </summary>
    [Fact]
    public void Uax9_W1_MarkTakesTheTypeBeforeIt()
    {
        Assert.Equal([1, 2, 2], Bidi.Levels("\u05D0a\u0301"));
        Assert.Equal([1, 1], Bidi.Levels("\u0301\u05D0"));
    }

    /// <summary>
    /// UAX #9, 3.3.4 sentences 14–17 (W2): a European digit after an Arabic letter becomes an Arabic number (the percent sign after it then
    /// stays a terminator and resolves to R: 1, 2, 1); after a Hebrew letter it stays European and takes the percent sign along (1, 2, 2).
    /// </summary>
    [Fact]
    public void Uax9_W2_DigitAfterArabicLetter()
    {
        Assert.Equal([1, 2, 1], Bidi.Levels("\u06281%"));
        Assert.Equal([1, 2, 2], Bidi.Levels("\u05D01%"));
    }

    /// <summary>
    /// UAX #9, 3.3.4 sentences 20–23 (W4): one separator between two numbers of the same type joins them (EN CS EN, AN CS AN, EN ES EN); two
    /// commas do not and resolve to R.
    /// </summary>
    [Fact]
    public void Uax9_W4_SingleSeparatorBetweenNumbers()
    {
        Assert.Equal([1, 2, 2, 2], Bidi.Levels("\u05D01,2"));
        Assert.Equal([1, 2, 2, 2], Bidi.Levels("\u06281,2"));
        Assert.Equal([1, 2, 1, 1, 2], Bidi.Levels("\u05D01,,2"));
        Assert.Equal([1, 2, 2, 2], Bidi.Levels("\u05D01+2"));
    }

    /// <summary>UAX #9, 3.3.4 sentences 24–26 (W5): terminators next to a European number become part of it, before and after.</summary>
    [Fact]
    public void Uax9_W5_TerminatorsNextToANumber()
    {
        Assert.Equal([1, 2, 2, 2], Bidi.Levels("\u05D0%%1"));
        Assert.Equal([1, 2, 2], Bidi.Levels("\u05D01%"));
    }

    /// <summary>
    /// UAX #9, 3.3.4 sentences 27–29 (W6): a terminator next to no European number becomes neutral — after a Hebrew letter and after an Arabic
    /// number it resolves to the right-to-left level 1.
    /// </summary>
    [Fact]
    public void Uax9_W6_RemainingSeparatorsBecomeNeutral()
    {
        Assert.Equal([1, 1], Bidi.Levels("\u05D0%"));
        Assert.Equal(1, Bidi.Levels("\u06281%")[2]);
    }

    /// <summary>
    /// UAX #9, 3.3.4 sentences 30–33 (W7): a European number whose last strong type is L becomes L — in „\u05D0 a 1“ the digit and the space
    /// before it stand on level 2 with the Latin letter (without W7 the space would be R: 1, 2, 1, 2).
    /// </summary>
    [Fact]
    public void Uax9_W7_NumberAfterLeftToRightBecomesL()
    {
        Assert.Equal([1, 2, 2, 2], Bidi.Levels("\u05D0a 1"));
    }

    /// <summary>
    /// UAX #9, 3.3.5 sentences 55–59 (N1): neutrals between two characters of the same direction take it — the space between two Hebrew
    /// letters becomes R, as does the space between a Hebrew letter and a European number (the number counts as R).
    /// </summary>
    [Fact]
    public void Uax9_N1_NeutralsBetweenTheSameDirection()
    {
        Assert.Equal([0, 0, 1, 1, 1], Bidi.Levels("a \u05D0 \u05D1"));
        Assert.Equal([0, 0, 1, 1, 2], Bidi.Levels("a \u05D0 1"));
    }

    /// <summary>
    /// UAX #9, 3.3.5 sentences 68–76 (example of W4, N1, W7 together): a list of numbers after a Hebrew word stays in the right-to-left run —
    /// the comma and space between the numbers are not part of a number and resolve to R.
    /// </summary>
    [Fact]
    public void Uax9_N1_NumberListInARightToLeftRun()
    {
        Assert.Equal([0, 0, 1, 1, 2, 1, 1, 2], Bidi.Levels("a \u05D0 1, 2"));
    }

    /// <summary>UAX #9, 3.3.6 sentences 5–6 (I1): on an even level an Arabic number rises by two — level 2 after an Arabic word in a Latin paragraph.</summary>
    [Fact]
    public void Uax9_I1_NumberOnAnEvenLevelRisesByTwo()
    {
        Assert.Equal([0, 0, 1, 1, 2], Bidi.Levels("a \u0628 1"));
    }

    /// <summary>UAX #9, 3.3.6 sentences 7–8 (I2): on an odd level a Latin letter rises by one — level 2 in a Hebrew paragraph.</summary>
    [Fact]
    public void Uax9_I2_LeftToRightOnAnOddLevelRisesByOne()
    {
        Assert.Equal([1, 1, 2], Bidi.Levels("\u05D0 a"));
    }

    /// <summary>
    /// UAX #9, 3.4 sentences 30–35 (example 1): in a Latin paragraph ending in a Hebrew word with a full stop, only the word is on level 1 and
    /// the full stop stays on level 0; the visual order reverses just the word.
    /// </summary>
    [Fact]
    public void Uax9_Example1_HebrewWordWithFullStop()
    {
        var levels = Bidi.Levels("abc def \u05D0\u05D1\u05D2.");
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 0], levels);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 10, 9, 8, 11], Bidi.VisualOrder(levels));
    }

    // ------------------------------------------------------------ uni-03: 3.5.1 to 6.4

    /// <summary>
    /// UAX #9, 3.5.1 sentences 1–2: line breaking measures the shaped glyphs — two words of two lam-alef each are as wide as the advances of
    /// their shaped glyphs (not of the letters shaped one by one), and the break falls where those widths say.
    /// </summary>
    [Fact]
    public void Uax9_LineWidth_FromTheShapedGlyphs()
    {
        const string word = "\u0644\u0627\u0644\u0627";
        var one = RichLayout.Of(UniTestsElement(word, 1000));
        var face = one.Lines[0].Glyphs[0].Face!;
        var scale = Uax9Style.Size / face.Font.UnitsPerEm;
        var shaper = TextShaper.For(face.Font);
        var shaped = shaper.Shape(word).Sum(g => g.XAdvance) * scale;
        var letters = word.Sum(c => shaper.Shape(c.ToString()).Sum(g => g.XAdvance)) * scale;
        Assert.Equal(shaped, one.Lines[0].Width, 3);
        Assert.NotEqual(letters, shaped, 3);

        const string two = word + " " + word;
        var whole = shaper.Shape(two).Sum(g => g.XAdvance) * scale;
        Assert.Single(RichLayout.Of(UniTestsElement(two, whole + 1)).Lines);
        var broken = RichLayout.Of(UniTestsElement(two, whole - 1, 100));
        Assert.Equal(2, broken.Lines.Count);
        Assert.Equal(word.Length + 1, broken.Lines[1].Start);
        Assert.Equal(shaped, broken.Lines[0].Width, 3);
    }

    /// <summary>
    /// UAX #9, 4 sentences 11–15: text that looks like source code is resolved by the plain algorithm, no segment protocol — in „x + \u05EA\u05D5 == 1“
    /// the paragraph is on level 0, the Hebrew word with „ == “ on level 1, the digit on level 2, and the line is drawn as „x + 1 == \u05D5\u05EA“.
    /// </summary>
    [Fact]
    public void Uax9_SourceLikeText_ThePlainAlgorithm()
    {
        var element = UniTestsElement("x + \u05EA\u05D5 == 1");
        Assert.Equal([0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 2], Bidi.Levels(element.Content.Text));
        Assert.Equal(Uax9Glyphs("x + 1 == \u05D5\u05EA"), Uax9Drawn(ElementAppearance.For(element)));
    }

    /// <summary>
    /// UAX #9, 4.3 sentences 5–15 (HL1): the paragraph level of a paragraph without a strong character can be given — „1 2“ with level 1
    /// gives 2, 1, 2; with the default 0 everything is on level 0.
    /// </summary>
    [Fact]
    public void Uax9_HL1_DefaultLevelForAParagraphWithoutStrongCharacters()
    {
        Assert.Equal([2, 1, 2], Bidi.Levels("1 2", 1));
        Assert.Equal([0, 0, 0], Bidi.Levels("1 2"));
    }

    /// <summary>UAX #9, 4.3 sentences 16–19 (HL2): the type of a number comes from W2 alone — a digit after an Arabic letter is an Arabic number.</summary>
    [Fact]
    public void Uax9_HL2_NumberTypeFromW2Only()
    {
        Assert.Equal([1, 2], Bidi.Levels("\u06281"));
        Assert.Equal(BidiClass.EN, Bidi.ClassOf('1'));
    }

    /// <summary>
    /// UAX #9, 4.3 sentences 20–24 (HL3): formatting sets no direction — text in two spans of different colour is drawn exactly as the same
    /// text in one span, with the levels of the plain text.
    /// </summary>
    [Fact]
    public void Uax9_HL3_FormattingChangesNoDirection()
    {
        var plain = UniTestsElement("a \u05D0\u05D1 c");
        var spans = plain with
        {
            Content = new RichText([new RichSpan("a \u05D0", Uax9Style), new RichSpan("\u05D1 c", Uax9Style with { Color = 0xC00000 })]),
        };
        Assert.Equal(plain.Content.Text, spans.Content.Text);
        Assert.Equal(Uax9Glyphs("a \u05D1\u05D0 c"), Uax9Drawn(ElementAppearance.For(plain)));
        Assert.Equal(Uax9Drawn(ElementAppearance.For(plain)), Uax9Drawn(ElementAppearance.For(spans)));
        var levels = Bidi.Levels(spans.Content.Text);
        Assert.Equal(levels.Where((_, i) => spans.Content.Text[i] != ' ').ToArray(),
            RichLayout.Of(spans).Lines.SelectMany(l => l.Glyphs).Where(g => g.Text != " ").Select(g => g.Level).ToArray());
    }

    /// <summary>
    /// UAX #9, 4.3 sentences 25–28 (HL4): each element is resolved on its own — next to an Arabic free text on the same page, the Latin text
    /// „ab !“ keeps paragraph level 0 and is drawn in its order; joined to the Arabic text into one paragraph the „!“ would be on level 1.
    /// </summary>
    [Fact]
    public void Uax9_HL4_EachElementResolvedOnItsOwn()
    {
        var arabic = UniTestsElement("\u0628\u0628");
        var latin = UniTestsElement("ab !") with { Rect = new PdfRectangle(0, 100, 300, 150) };
        Assert.Equal(1, Bidi.Levels("\u0628\u0628 ab !")[^1]);
        Assert.Equal([0, 0, 0, 0], Bidi.Levels(latin.Content.Text));
        Assert.Equal(Uax9Glyphs("ab !"), Uax9Drawn(ElementAppearance.For(latin)));
        var saved = UniTestsSaved(arabic, latin);
        Assert.Contains("ab !", UniTestsPageText(saved), StringComparison.Ordinal);
    }

    /// <summary>UAX #9, 4.3 sentences 29–32 (HL5): no context is assumed around a text — a lone digit stays on level 0.</summary>
    [Fact]
    public void Uax9_HL5_NoContextAssumed()
    {
        Assert.Equal([0], Bidi.Levels("1"));
    }

    /// <summary>
    /// UAX #9, 6.2 sentences 1–14: own text is set in horizontal lines only, a rotated element turns as a whole — at 90° and 270° the lines,
    /// the levels of the glyphs and the drawn order are those of the unrotated element.
    /// </summary>
    [Fact]
    public void Uax9_RotatedElement_SameLevelsAndOrder()
    {
        var upright = UniTestsElement("a \u05D0\u05D1 c", 300, 300);
        var lines = RichLayout.Of(upright).Lines;
        var drawn = Uax9Drawn(ElementAppearance.For(upright));
        foreach (var rotation in new[] { 90, 270 })
        {
            var turned = upright with { Rotation = rotation };
            var turnedLines = RichLayout.Of(turned).Lines;
            Assert.Equal(lines.Count, turnedLines.Count);
            Assert.Equal(lines.SelectMany(l => l.Glyphs).Select(g => (g.Glyph, g.Level, g.X)),
                turnedLines.SelectMany(l => l.Glyphs).Select(g => (g.Glyph, g.Level, g.X)));
            Assert.Equal(drawn, Uax9Drawn(ElementAppearance.For(turned)));
        }
    }

    /// <summary>
    /// UAX #9, 6.3 sentences 5–18: an RLM after the punctuation of an embedded Hebrew phrase takes the punctuation into it — the exclamation
    /// mark between the Hebrew letter and the RLM is on level 1, without the RLM on level 0.
    /// </summary>
    [Fact]
    public void Uax9_RlmAfterPunctuation_TakesItIntoTheEmbedding()
    {
        Assert.Equal(1, Bidi.Levels("a \u05D0!\u200F, b")[3]);
        Assert.Equal(0, Bidi.Levels("a \u05D0!, b")[3]);
    }

    /// <summary>
    /// UAX #9, 6.4 sentences 11–21: in a Hebrew paragraph a separator between two Latin words takes their direction (level 2); an RLM before
    /// it keeps it in the right-to-left order (level 1).
    /// </summary>
    [Fact]
    public void Uax9_RlmInASeparator_KeepsTheRightToLeftOrder()
    {
        Assert.Equal(2, Bidi.Levels("\u05D0 a - b \u05D1")[4]);
        Assert.Equal(1, Bidi.Levels("\u05D0 a \u200F- b \u05D1")[5]);
    }
}
