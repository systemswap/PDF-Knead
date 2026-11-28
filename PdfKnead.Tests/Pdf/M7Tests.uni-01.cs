using System.Text.RegularExpressions;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, packages uni-01 to uni-03 (UAX #9, the Unicode Bidirectional Algorithm, revision 50 for Unicode 16.0.0) and the mirroring pairs of
/// OT 81: the explicit embeddings, overrides and isolates, the bracket pairs, the Bidi classes of the database, L1 per line, L4 before
/// shaping, every own appearance through the algorithm, formatting characters without glyph, the search over them and the hard break in an
/// Arabic word (docs/FEHLERGEDAECHTNIS.md, No. 1173). The whole conformance files are run by <c>BidiConformanceTests</c>.
/// </summary>
public sealed partial class M7NormTests
{
    private static readonly RichStyle Uax9Style = new() { Family = "Liberation Sans", Size = 12 };

    /// <summary>The glyph numbers of an appearance in drawing order (the hexadecimal strings of its TJ arrays, two bytes per glyph).</summary>
    private static List<int> Uax9Drawn(ElementAppearance appearance)
    {
        var content = System.Text.Encoding.Latin1.GetString(appearance.Form.RawData!);
        return Regex.Matches(content, @"\[([^\]]*)\] TJ").SelectMany(m =>
            Regex.Matches(m.Groups[1].Value, "<([0-9A-F]+)>").SelectMany(h =>
                Enumerable.Range(0, h.Groups[1].Value.Length / 4).Select(j => Convert.ToInt32(h.Groups[1].Value.Substring(4 * j, 4), 16)))).ToList();
    }

    /// <summary>The glyphs of Liberation Sans for the characters of a text (each must be in the font).</summary>
    private static List<int> Uax9Glyphs(string text)
    {
        var face = ElementFace.For(ElementFont.Sans);
        var result = new List<int>();
        foreach (var rune in text.EnumerateRunes())
        {
            var g = face.Glyph(rune.Value);
            Assert.NotEqual(0, g);
            result.Add(g);
        }
        return result;
    }

    // ------------------------------------------------------------ X1–X10: embeddings, overrides, isolates

    /// <summary>
    /// UAX #9, 2.1, 2.3, 3.1.2 (BD7), 3.3.2 X2, X7, 3.3.3 X9: RLE raises the level to the next odd one, a left-to-right letter in it takes the
    /// even level above (I2), PDF ends the embedding; LRE in a right-to-left paragraph raises to 2 and a Hebrew letter there stands on 3. An
    /// unmatched PDF is removed and lets W4 join the digits across it.
    /// </summary>
    [Fact]
    public void Uax9_Embeddings_RaiseTheLevelUntilPdf()
    {
        var levels = Bidi.Levels("a\u202Bb\u202Cc");
        Assert.Equal(0, levels[0]);
        Assert.Equal(2, levels[2]);
        Assert.Equal(0, levels[4]);
        var hebrew = Bidi.Levels("\u05D0\u202A\u05D1\u202C");
        Assert.Equal(1, hebrew[0]);
        Assert.Equal(3, hebrew[2]);
        var digits = Bidi.Levels("\u05D01\u202C,2");
        Assert.Equal([1, 2, 2, 2], new[] { digits[0], digits[1], digits[3], digits[4] });
        var number = Bidi.Levels("a\u202B1\u202C");
        Assert.Equal(2, number[2]);
    }

    /// <summary>
    /// UAX #9, 2.2, 3.1.1 (BD6), 3.3.2 X4, X5, X6: an override gives every character in it the direction of the override — RLO makes Latin
    /// letters R (level 1, shown reversed), LRO makes Hebrew letters L (level 2 in a right-to-left paragraph; P2 passes over the override).
    /// </summary>
    [Fact]
    public void Uax9_Overrides_SetTheDirectionOfEveryCharacter()
    {
        var rlo = Bidi.Levels("\u202Eab\u202C");
        Assert.Equal(1, rlo[1]);
        Assert.Equal(1, rlo[2]);
        Assert.Equal([2, 1], Bidi.VisualOrder([rlo[1], rlo[2]]).Select(k => k + 1).ToArray());
        var lro = Bidi.Levels("\u202D\u05D0\u05D1\u202C");
        Assert.Equal(2, lro[1]);
        Assert.Equal(2, lro[2]);
    }

    /// <summary>
    /// UAX #9, 2.4, 2.5, 3.1.2 (BD8, BD9, BD13), 3.3.1 P2, 3.3.2 X5a–X5c, X6a, X10: an isolate takes a level of its own and counts outside as
    /// a neutral character; P2 passes over it; PDI closes the isolate and every embedding opened in it; the second PDI matches the RLI; FSI
    /// takes the direction of its first strong character; a PDI without initiator does nothing. The runs before and after an isolate form one
    /// isolating run sequence, so the neutrals between two Hebrew letters around it become R.
    /// </summary>
    [Fact]
    public void Uax9_Isolates_LevelOfTheirOwnAndNeutralOutside()
    {
        Assert.Equal([1, 4, 1, 1, 1], Bidi.Levels("\u2067a\u2069 \u05D0"));
        Assert.Equal([0, 0, 1, 1, 1, 2, 1, 1, 1], Bidi.Levels("a \u05D0 \u2067c\u2069 \u05D1"));
        var nested = Bidi.Levels("a\u2067\u202Ab\u2069c");
        Assert.Equal(2, nested[3]);
        Assert.Equal(0, nested[4]);
        Assert.Equal(0, nested[5]);
        var two = Bidi.Levels("a\u2067\u2066b\u2069c\u2069d");
        Assert.Equal(2, two[5]);
        Assert.Equal(0, two[7]);
        Assert.Equal([0, 0, 1, 2, 0], Bidi.Levels("a\u2068\u05D0b\u2069"));
        Assert.Equal([1, 1, 3, 1], Bidi.Levels("\u05D0\u2066\u05D1\u2069"));
        Assert.Equal([0, 0, 0], Bidi.Levels("a\u2069b"));
        // the price after an isolated Hebrew name stays with the Latin text before it (W7 looks across the isolate)
        var price = Bidi.Levels("Name: \u2067java \u05D0\u05D1\u2069 - $19.95");
        Assert.All(price[^9..], level => Assert.Equal(0, level));
    }

    /// <summary>
    /// UAX #9, 3.1.1 (BD2), 3.3.2 X1–X5 (Security: the stack is fixed at max_depth + 2 entries with overflow counters): 200 nested RLE, LRI
    /// and RLI never give a level above 125, and the PDF after 130 nested RLE bring the paragraph level back.
    /// </summary>
    [Fact]
    public void Uax9_Nesting_NeverAbove125()
    {
        foreach (var opener in new[] { '\u202B', '\u202A', '\u2066', '\u2067' })
        {
            var levels = Bidi.Levels(new string(opener, 200) + "a\u05D0");
            Assert.All(levels, level => Assert.True(level <= 126, $"level {level}"));
            Assert.True(levels[200] <= 125 + 1);
        }
        var back = Bidi.Levels(new string('\u202B', 130) + "a" + new string('\u202C', 130) + "b");
        Assert.Equal(0, back[^1]);
        Assert.True(back[130] <= 126);
    }

    // ------------------------------------------------------------ N0: bracket pairs

    /// <summary>
    /// UAX #9, 3.1.3 (BD14–BD16), 3.3.5 N0: brackets in Hebrew text take the direction of what they enclose and of their context — L like
    /// the Latin text before them, R when they enclose a letter of the embedding direction, R from the context when they enclose a number;
    /// the pairs are resolved in the order of their opening brackets (example 1 of the standard); a mark after a changed bracket follows it.
    /// </summary>
    [Fact]
    public void Uax9_BracketPairs_ResolvedTogether()
    {
        Assert.Equal([1, 1, 1, 2, 2, 2, 2, 2], Bidi.Levels("\u05D0\u05D1 ab(c)"));
        Assert.Equal([1, 1, 2, 1, 2, 1, 1, 1, 2], Bidi.Levels("\u05D0 a(b \u05D1)c"));
        Assert.Equal([0, 0, 1, 1, 2, 1], Bidi.Levels("a \u05D0(1)"));
        Assert.Equal([1, 1, 1, 1, 1, 1, 2], Bidi.Levels("\u05D0 ( ) a"));
        Assert.Equal(2, Bidi.Levels("\u05D0\u05D1 ab(c)\u0301")[^1]);
        var example = Bidi.Levels("\u05D0\u05D1(\u05D2\u05D3[&ef]!)gh");
        Assert.Equal([1, 1, 1, 1, 1, 1, 1, 2, 2, 1, 1, 1, 2, 2], example);
        // U+2329 pairs with U+3009 (canonical equivalents)
        Assert.Equal(2, Bidi.Levels("\u05D0 b\u2329c\u3009")[^1]);
    }

    /// <summary>
    /// UAX #9, 3.1.3 BD16 (Security: a fixed stack of 63 openers): with 63 opening brackets the innermost pair is found and resolves L from
    /// the context; with 64 the stack overflows and the list of pairs is empty — the closing bracket then resolves by N1 between L and eos (R).
    /// </summary>
    [Fact]
    public void Uax9_BracketStack_63Openers_OverflowGivesNoPairs()
    {
        Assert.Equal(2, Bidi.Levels("\u05D0 b" + new string('(', 63) + "c)")[^1]);
        Assert.Equal(1, Bidi.Levels("\u05D0 b" + new string('(', 64) + "c)")[^1]);
    }

    // ------------------------------------------------------------ 3.2: Bidi classes from the database

    /// <summary>
    /// UAX #9, 3.1.1, 3.2 (Table 4) and 4.1: the classes come from DerivedBidiClass.txt — Hebrew, Samaritan, Mandaic and Adlam punctuation is
    /// R; U+0CBF and unassigned U+0378 are L; ½ is ON; U+2212 is ES (the number stays one); U+FEFF and the soft hyphen are BN and take part in
    /// nothing (W4 joins across them, W7 sees past them); U+0085 and U+001C separate paragraphs (P1).
    /// </summary>
    [Fact]
    public void Uax9_ClassesFromTheDatabase()
    {
        foreach (var c in new[] { 0x05C0, 0x05C3, 0x05C6, 0x05F3, 0x05F4, 0x0830, 0x085E, 0x1E95E })
            Assert.True(Bidi.HasRightToLeft(char.ConvertFromUtf32(c)), c.ToString("X4"));
        Assert.Equal([1, 2], Bidi.Levels("\u05D0\u0CBF"));
        Assert.Equal([1, 2], Bidi.Levels("\u05D0\u0378"));
        Assert.Equal([1, 1, 1], Bidi.Levels("\u05D0 \u00BD"));
        Assert.Equal([1, 2, 2, 2], Bidi.Levels("\u05D01\u22122"));
        Assert.Equal(0, Bidi.Levels("a\uFEFF1")[2]);
        var soft = Bidi.Levels("\u05D01\u00AD,2");
        Assert.Equal([1, 2, 2, 2], new[] { soft[0], soft[1], soft[3], soft[4] });
        var space = Bidi.Levels("\u05D01\u200B,2");
        Assert.Equal([2, 2], new[] { space[3], space[4] });
        // 5.2: W1 looks past a BN (the mark after it takes L), W5 joins a terminator across one
        Assert.Equal(2, Bidi.Levels("\u05D0a\u200B\u0301")[3]);
        Assert.Equal(2, Bidi.Levels("\u05D01\u00AD%")[3]);
        // 8: a fraction after an Arabic letter stays one Arabic number (W2, W4)
        Assert.Equal([1, 1, 2, 2, 2], Bidi.Levels("\u0628 1/2"));
        Assert.Equal([1, 1, 0], Bidi.Levels("\u05D0\u0085a"));
        Assert.Equal([1, 1, 0], Bidi.Levels("\u05D0\u001Ca"));
        // Syriac letters are AL, Samaritan and Mandaic ones R: the digits after them on level 2
        Assert.Equal([1, 1, 1, 1, 1, 2, 2], Bidi.Levels("\u072B\u0720\u0721\u0710 12"));
        Assert.Equal([1, 1, 1, 2, 2], Bidi.Levels("\u0800\u0801 12"));
        Assert.Equal([1, 1, 1, 2], Bidi.Levels("\u0840\u0841 1"));
    }

    // ------------------------------------------------------------ L1, L2: per line

    /// <summary>
    /// UAX #9, 3.4 L1: a tab and the white space before it go back to the paragraph level, so a tab separates two Hebrew words instead of
    /// joining them into one run.
    /// </summary>
    [Fact]
    public void Uax9_L1_TabsAndWhiteSpaceBeforeThem()
    {
        Assert.Equal([0, 0, 1, 0, 1], Bidi.Levels("a \u05D0\t\u05D1"));
        Assert.Equal([0, 0, 1, 0, 0, 1], Bidi.Levels("a \u05D0 \t\u05D1"));
    }

    /// <summary>
    /// UAX #9, 3.4 L1 per line: a paragraph „abc אבג דה“ broken after the first Hebrew word — the space at the end of the first line goes back
    /// to the paragraph level and stays at the visual end of the line (it is not drawn), instead of standing between „abc “ and the reversed
    /// word as when the levels of the whole paragraph were used.
    /// </summary>
    [Fact]
    public void Uax9_L1_TrailingSpaceOfAWrappedLine()
    {
        var face = ElementFace.For(ElementFont.Sans);
        const string first = "abc \u05D0\u05D1\u05D2 ";
        var width = RichLayout.Layout(new RichText(first, Uax9Style), 1000, BlockAlignment.Left, 1.15, null, 0).Lines[0].Width + 2;
        var element = new RichTextElement { Rect = new PdfRectangle(0, 0, width, 100), Content = new RichText(first + "\u05D3\u05D4", Uax9Style) };
        Assert.Equal(2, RichLayout.Of(element).Lines.Count);
        var drawn = Uax9Drawn(ElementAppearance.For(element));
        Assert.Equal(Uax9Glyphs("abc \u05D2\u05D1\u05D0\u05D4\u05D3"), drawn);
        Assert.NotEqual(0, face.Glyph(0x05D0));
    }

    // ------------------------------------------------------------ L4 and 58: own appearances

    /// <summary>
    /// UAX #9, 3.4 L4 and 7; OT 81: every pair of BidiMirroring.txt 16.0.0 mirrors into its partner (the full list, also U+FE59 … U+FF63);
    /// characters without a partner stay (U+2231, U+FD3E, U+0606: nothing is invented).
    /// </summary>
    [Fact]
    public void Uax9_Mirrored_EveryPairOfTheFile()
    {
        using var stream = typeof(ElementAppearance).Assembly.GetManifestResourceStream("Fonts/BidiMirroring.txt");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            var data = line.Split('#')[0].Split(';');
            if (data.Length < 2) continue;
            var c = Convert.ToInt32(data[0].Trim(), 16);
            Assert.Equal(Convert.ToInt32(data[1].Trim(), 16), Bidi.Mirrored(c));
            count++;
        }
        Assert.Equal(428, count);
        foreach (var (a, b) in new[] { (0xFE59, 0xFE5A), (0xFE5B, 0xFE5C), (0xFE5D, 0xFE5E), (0xFE64, 0xFE65), (0xFF08, 0xFF09), (0xFF1C, 0xFF1E),
                     (0xFF3B, 0xFF3D), (0xFF5B, 0xFF5D), (0xFF5F, 0xFF60), (0xFF62, 0xFF63), (0x2208, 0x220B), (0x3008, 0x3009), (0x22E0, 0x22E1) })
        {
            Assert.Equal(b, Bidi.Mirrored(a));
            Assert.Equal(a, Bidi.Mirrored(b));
        }
        foreach (var c in new[] { 0x2231, 0xFD3E, 0x0606, 'a' })
            Assert.Equal(c, Bidi.Mirrored(c));
    }

    /// <summary>
    /// UAX #9, 3.4 L4 (mirrored before shaping) and question 59 (measured against the three reference readers: two of them reverse a
    /// right-to-left run when extracting text and read a mirrored bracket correctly only when its text is the character stored, the third keeps
    /// the visual order): „א(ב)“ is drawn as „(“ ב „)“ א from the left, and the glyph drawn for the stored „)“ stands for „)“ in ToUnicode.
    /// </summary>
    [Fact]
    public void Uax9_MirroredGlyph_TextIsTheStoredCharacter()
    {
        var element = new RichTextElement { Rect = new PdfRectangle(0, 0, 300, 50), Content = new RichText("\u05D0(\u05D1)", Uax9Style) };
        var appearance = ElementAppearance.For(element);
        Assert.Equal(Uax9Glyphs("(\u05D1)\u05D0"), Uax9Drawn(appearance));
        var map = appearance.FaceUses.Single().Glyphs;
        Assert.Equal(")", map[Uax9Glyphs("(")[0]]);
        Assert.Equal("(", map[Uax9Glyphs(")")[0]]);
    }

    /// <summary>
    /// Question 58 (UAX #9 for every own appearance): plain text (<c>TextElement</c>, also the layout of field values) runs through the
    /// algorithm — „שלום 12“ is drawn from the left as „12 םולש“, brackets in it mirrored; free text without an appearance whose /DA font
    /// has the Hebrew letters likewise (before, all three were drawn in logical order from the left).
    /// </summary>
    [Fact]
    public void Uax9_PlainTextFieldsAndFreeText_ThroughTheAlgorithm()
    {
        var face = ElementFace.For(ElementFont.Sans);
        var plain = new TextElement { Rect = new PdfRectangle(0, 0, 300, 40), Text = "\u05E9\u05DC\u05D5\u05DD 12" };
        var line = Assert.Single(ElementAppearance.Layout(plain, face));
        Assert.Equal(Uax9Glyphs("12 \u05DD\u05D5\u05DC\u05E9"), line.Glyphs);
        Assert.Equal(Uax9Glyphs("12 \u05DD\u05D5\u05DC\u05E9"), Uax9Drawn(ElementAppearance.For(plain)));
        var brackets = Assert.Single(ElementAppearance.Layout(plain with { Text = "\u05D0(\u05D1)" }, face));
        Assert.Equal(Uax9Glyphs("(\u05D1)\u05D0"), brackets.Glyphs);
        Assert.Equal([")", "\u05D1", "(", "\u05D0"], brackets.Text);

        const string text = "\u05E9\u05DC\u05D5\u05DD";
        var hex = "<FEFF" + string.Concat(text.Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture))) + ">";
        var note = $"<</Type/Annot/Subtype/FreeText/Contents{hex}/DA(/Helv 12 Tf 0 0 0 rg)/F 4/Rect[100 600 260 630]/P 3 0 R>>";
        using var source = PdfDocument.Open(PdfARuleCases.WithAnnotation(note, "", "")());
        var annot = (PdfDictionary)source.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0);
        var shown = FieldAppearance.TextAppearance(source, annot);
        Assert.NotNull(shown);
        var drawn = Uax9Drawn(shown.Value.Appearance);
        Assert.Equal(Uax9Glyphs("\u05DD\u05D5\u05DC\u05E9"), drawn);
    }

    // ------------------------------------------------------------ 60: formatting characters

    /// <summary>
    /// Question 60 (UAX #9, 2 and 3.3.3 sentences 11–13): the directional formatting characters are drawn with neither glyph nor advance — in
    /// formatted text, plain text and field values no glyph 0 and no glyph for LRI, PDI, RLM.
    /// </summary>
    [Fact]
    public void Uax9_FormattingCharacters_NoGlyphNoAdvance()
    {
        var face = ElementFace.For(ElementFont.Sans);
        var rich = new RichTextElement { Rect = new PdfRectangle(0, 0, 300, 50), Content = new RichText("a\u2066b\u2069c\u200F", Uax9Style) };
        var glyphs = RichLayout.Of(rich).Lines.SelectMany(l => l.Glyphs).ToList();
        Assert.Equal(["a", "b", "c"], glyphs.Select(g => g.Text));
        Assert.Equal(Uax9Glyphs("abc"), Uax9Drawn(ElementAppearance.For(rich)));
        var plain = Assert.Single(ElementAppearance.Layout(new TextElement { Rect = new PdfRectangle(0, 0, 300, 40), Text = "a\u200Fb\u202Ec\u202C" }, face));
        Assert.DoesNotContain(0, plain.Glyphs);
        Assert.Equal(3, plain.Glyphs.Count);
    }

    /// <summary>
    /// UAX #9, 1 sentence 15, and question 60: searching passes over the directional formatting characters — in the page text („a LRM b“ is
    /// found as „ab“, the hit covering all three characters) and in the search term („a RLM b“ finds „ab“).
    /// </summary>
    [Fact]
    public void Uax9_Search_PassesOverFormattingCharacters()
    {
        static PageText Line(string text)
        {
            var glyphs = new List<TextGlyph>();
            float x = 0;
            foreach (var c in text)
            {
                var w = c is '\u200E' or '\u200F' ? 0 : 6;
                glyphs.Add(new TextGlyph(c.ToString(), x, 0, x + w, 0, x + w, 10, x, 10, 10, true));
                x += w;
            }
            return PageText.Create([.. glyphs]);
        }
        var hits = Line("xa\u200Eby").Find("ab");
        var hit = Assert.Single(hits);
        Assert.Equal(3, hit.Length);
        Assert.Single(Line("xaby").Find("a\u200Fb"));
        Assert.Equal("ab", PageText.NormalizeQuery("a\u2067b\u2069"));
    }

    /// <summary>
    /// UAX #9, 3.5 sentence 2: shaping stays within a level run — two Arabic behs separated by an RLE stand on levels 1 and 3 and do not join
    /// (each in its isolated form), while two behs on one level join.
    /// </summary>
    [Fact]
    public void Uax9_Shaping_StaysWithinALevelRun()
    {
        static List<int> Behs(string text)
        {
            var element = new RichTextElement { Rect = new PdfRectangle(0, 0, 300, 50), Content = new RichText(text, Uax9Style) };
            return [.. RichLayout.Of(element).Lines.SelectMany(l => l.Glyphs).Where(g => g.Text == "\u0628").Select(g => g.Glyph)];
        }
        var face = RichLayout.Of(new RichTextElement { Rect = new PdfRectangle(0, 0, 300, 50), Content = new RichText("\u0628", Uax9Style) })
            .Lines[0].Glyphs[0].Face!;
        var isolated = TextShaper.For(face.Font).Shape("\u0628")[0].Glyph;
        Assert.Equal([isolated, isolated], Behs("\u0628\u202B\u0628\u202C"));
        Assert.DoesNotContain(isolated, Behs("\u0628\u0628"));
    }

    // ------------------------------------------------------------ 62: hard break in an Arabic word

    /// <summary>
    /// UAX #9, 3.5.1 sentences 3–6, and question 62: an Arabic word too long for its line is broken hard; the letters at the break are shaped
    /// anew — each line holds the glyphs its own text shapes into (the last letter of the first line in its final form, the first of the next
    /// in its initial form), not the connecting forms of the unbroken word.
    /// </summary>
    [Fact]
    public void Uax9_HardBreakInAnArabicWord_ShapedAtTheBreak()
    {
        var word = string.Concat(Enumerable.Repeat("\u0633\u0644\u0645", 12));
        var element = new RichTextElement { Rect = new PdfRectangle(0, 0, 60, 400), Content = new RichText(word, Uax9Style with { Size = 20 }) };
        var layout = RichLayout.Of(element);
        Assert.True(layout.Lines.Count >= 2);
        foreach (var line in layout.Lines)
        {
            var piece = word[line.Start..line.End];
            var face = line.Glyphs[0].Face!;
            var expected = TextShaper.For(face.Font).Shape(piece).Select(g => g.Glyph).ToList();
            Assert.Equal(expected, line.Glyphs.Select(g => g.Glyph).ToList());
        }
    }
}
