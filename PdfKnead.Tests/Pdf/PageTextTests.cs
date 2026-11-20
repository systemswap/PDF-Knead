using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>Text of a page: lines, spaces, search, quadrilaterals, selection.</summary>
public sealed class PageTextTests
{
    private static PageText Text(string content)
    {
        var (doc, list) = InterpreterTests.Page(content, _ => TestPdf.Dict(("Font", TestPdf.Dict(("F1",
            TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")),
                ("Encoding", TestPdf.N("WinAnsiEncoding"))))))));
        using (doc) return PageText.Create(list.Text);
    }

    /// <summary>
    /// A line whose words the content draws backwards (a trick against redaction): the text in content order reads them in that order, the
    /// text in the order of the places reads the line as it looks — a search there finds the name (docs/FEHLERGEDAECHTNIS.md, No. 267).
    /// </summary>
    [Fact]
    public void LineDrawnBackwards_InPlaceReadsItAsItLooks()
    {
        // Helvetica 12 pt: "Max" is 22.68 pt wide, "Mustermann" starts 6 pt after it
        var text = Text("BT /F1 12 Tf 1 0 0 1 48.68 150 Tm (Mustermann) Tj 1 0 0 1 20 150 Tm (Max) Tj ET");
        Assert.Empty(text.Find("Max Mustermann"));
        Assert.NotNull(text.InPlace);
        Assert.Equal("Max Mustermann", text.InPlace!.Text);
        Assert.Single(text.InPlace.Find("Max Mustermann"));
        Assert.Equal(13, text.InPlace.GlyphsOf(text.InPlace.Find("Max Mustermann")[0]).Count());
    }

    /// <summary>A note in the margin drawn after the line on the same baseline stays a piece of its own, also in the order of the places;
    /// an ordinary page has no such text at all.</summary>
    [Fact]
    public void NoteInTheMargin_StaysApart_OrdinaryPageHasNoInPlaceText()
    {
        var note = Text("BT /F1 12 Tf 1 0 0 1 100 150 Tm (Haupttext der Zeile) Tj 1 0 0 1 20 150 Tm (Rand) Tj ET");
        Assert.Equal(note.Text, note.InPlace?.Text ?? note.Text);
        Assert.Null(Text("BT /F1 12 Tf 20 150 Td (Hello) Tj 40 0 Td (World) Tj -40 -20 Td (Next line) Tj ET").InPlace);
    }

    [Fact]
    public void WordsPlacedApart_GetSpaces_AndLinesBreaks()
    {
        // "Hello" is 27 pt wide; "World" starts 40 pt further on, so a space belongs in between
        var text = Text("BT /F1 12 Tf 20 150 Td (Hello) Tj 40 0 Td (World) Tj -40 -20 Td (Next line) Tj ET");
        Assert.Equal("Hello World\nNext line", text.Text);
    }

    /// <summary>
    /// A page drawn as clipped tiles, each with its part of every line ("func" in the left tile, "tion ignores" in the right one, the
    /// same for the second line): before, the text followed the content order, the words were cut at the tile edge, and the search
    /// found neither "function" nor "Zweite". Now pieces that touch on the page are joined. Columns with a gap keep their order.
    /// </summary>
    [Fact]
    public void LinesCutIntoClippedTiles_AreJoinedAgain()
    {
        // Helvetica 12 pt: "func" is 22.68 pt wide, "Zwei" 25.332 pt
        var text = Text("q 0 0 42.68 300 re W n BT /F1 12 Tf 1 0 0 1 20 150 Tm (func) Tj 1 0 0 1 20 130 Tm (Zwei) Tj ET Q " +
                        "q 42.68 0 300 300 re W n BT /F1 12 Tf 1 0 0 1 42.68 150 Tm (tion ignores) Tj 1 0 0 1 45.332 130 Tm (te Zeile) Tj ET Q");
        Assert.Equal("function ignores\nZweite Zeile", text.Text);
        Assert.Single(text.Find("function"));
        Assert.Single(text.Find("Zweite Zeile"));

        var columns = Text("BT /F1 12 Tf 1 0 0 1 20 150 Tm (Links eins) Tj 1 0 0 1 20 130 Tm (Links zwei) Tj " +
                           "1 0 0 1 150 150 Tm (Rechts eins) Tj 1 0 0 1 150 130 Tm (Rechts zwei) Tj ET");
        Assert.Equal("Links eins\nLinks zwei\nRechts eins\nRechts zwei", columns.Text);
    }

    [Fact]
    public void KerningInsideAWord_DoesNotSplitIt()
    {
        var text = Text("BT /F1 12 Tf 20 150 Td [(W) 80 (ord)] TJ ET");
        Assert.Equal("Word", text.Text);
    }

    [Fact]
    public void Overprinting_CountsOnce()
    {
        // Fake bold: the same word drawn again with a slight offset
        var text = Text("BT /F1 12 Tf 20 150 Td (B) Tj ET BT /F1 12 Tf 20.3 150 Td (B) Tj ET");
        Assert.Equal("B", text.Text);
    }

    [Fact]
    public void Find_IgnoresCase_AndLineBreaks()
    {
        var text = Text("BT /F1 12 Tf 20 150 Td (Hello) Tj 40 0 Td (World) Tj -40 -20 Td (next line) Tj ET");
        var hits = text.Find("world  NEXT");
        Assert.Single(hits);
        Assert.Equal("World\nnext", text.Text.Substring(hits[0].Start, hits[0].Length));
        Assert.Empty(text.Find("world next", matchCase: true));
        Assert.Equal(2, text.Find("l").Count(h => h.Start < 5));   // two "l" in Hello
    }

    [Fact]
    public void Quads_OnePerLine_CoverTheGlyphs()
    {
        var text = Text("BT /F1 12 Tf 20 150 Td (Hello) Tj 40 0 Td (World) Tj -40 -20 Td (next) Tj ET");
        var quads = text.Quads(text.Find("World next")[0]);
        Assert.Equal(2, quads.Count);
        var first = quads[0].Bounds;
        Assert.InRange(first.Left, 59.9, 60.1);           // "World" starts at 20 + 40
        Assert.True(first.Top < 150 && first.Bottom > 150 + 6, $"{first}");
        Assert.True(quads[1].Bounds.Bottom < first.Top + 1);   // second line below
    }

    [Fact]
    public void CaretAt_SelectsBetweenPoints()
    {
        var text = Text("BT /F1 12 Tf 20 150 Td (Hello) Tj 40 0 Td (World) Tj ET");
        // From the left half of the "H" to the right half of the "W"
        var start = text.CaretAt(21, 154);
        var end = text.CaretAt(66, 154);
        var range = PageText.Between(end, start);
        Assert.Equal("Hello W", text.GetText(range));
        Assert.True(text.IsOverText(30, 154));
        Assert.False(text.IsOverText(30, 100));
        Assert.Equal(-1, PageText.Empty.CaretAt(0, 0));
    }

    [Fact]
    public void Ligatures_AreSplitIntoLetters()
    {
        var glyph = new TextGlyph("ﬂ", 0, 0, 10, 0, 10, 10, 0, 10, 10, true);
        var text = PageText.Create([glyph, glyph with { Text = "oat", X0 = 10, X1 = 30, X2 = 30, X3 = 10 }]);
        Assert.Equal("float", text.Text);
        var hit = Assert.Single(text.Find("float"));
        Assert.Single(text.Quads(hit));
    }

    /// <summary>
    /// The search finds a name however the file writes it (docs/FEHLERGEDAECHTNIS.md, No. 355): with the umlaut as u and a combining diaeresis
    /// (as some producers map it), with a soft hyphen inside, hyphenated across two lines — and the hit covers exactly its characters (the
    /// bar of „Suchen und schwärzen“ is made from them), in both searches (one term, many terms).
    /// </summary>
    [Fact]
    public void NameWrittenInAnyForm_Found_HitCoversItsCharacters()
    {
        static PageText Lines(params string[] lines)
        {
            var glyphs = new List<TextGlyph>();
            for (var l = 0; l < lines.Length; l++)
            {
                var x = 72f;
                var y = 700f - 14 * l;
                foreach (var c in lines[l])
                {
                    glyphs.Add(new TextGlyph(c.ToString(), x, y, x + 6, y, x + 6, y + 10, x, y + 10, 10, true));
                    x += 6;
                }
            }
            return PageText.Create([.. glyphs]);
        }
        var cases = new (PageText Text, string Query, string Covered)[]
        {
            (Lines("An der Mu\u0308ndung heute"), "Mündung", "Mu\u0308ndung"),
            (Lines("Herr Muster\u00ADmann kommt"), "Mustermann", "Muster\u00ADmann"),
            (Lines("Herr Muster-", "mann kommt"), "Mustermann", "Muster-\nmann"),
            (Lines("Institut Max-", "Planck heute"), "Max-Planck", "Max-\nPlanck"),
            (Lines("Die Mu\u0308ndung und Muster-", "mann"), "Herr", ""),
        };
        foreach (var (text, query, covered) in cases)
        {
            var one = text.Find(query);
            var many = text.FindAll([PageText.NormalizeQuery(query), PageText.NormalizeQuery("zzz")])[0];
            if (covered.Length == 0)
            {
                Assert.Empty(one);
                continue;
            }
            Assert.Equal(covered, text.Text.Substring(Assert.Single(one).Start, one[0].Length));
            Assert.Equal(one, many);
        }
        // a hyphen before a capital letter stays: „Max-Planck“ is not „MaxPlanck“
        Assert.Empty(Lines("Institut Max-", "Planck heute").Find("MaxPlanck"));
    }
}
