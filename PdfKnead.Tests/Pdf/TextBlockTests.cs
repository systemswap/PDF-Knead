using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Text block detection: paragraphs, columns and table cells, alignment, bullet lists, runs with their own
/// formatting (including fake bold), rotated text, underlines, hard line breaks, locked blocks. See KONZEPT.md, section 5.14.
/// </summary>
public sealed class TextBlockTests
{
    /// <summary>Opens an A4 page with <paramref name="content"/>; fonts F1 Helvetica, F2 Helvetica-Bold, F3 Times-Italic, F4 Courier, F5 a barcode font, F6 Symbol.</summary>
    internal static PdfDocument Page(string content) => PdfDocument.Open(PageBytes(content));

    /// <summary>Builds the file for <see cref="Page"/>.</summary>
    internal static byte[] PageBytes(string content, int rotate = 0)
    {
        var pdf = new TestPdf();
        PdfDictionary Font(string name) => TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N(name)),
            ("Encoding", TestPdf.N("WinAnsiEncoding")));
        var fonts = TestPdf.Dict(("F1", Font("Helvetica")), ("F2", Font("Helvetica-Bold")), ("F3", Font("Times-Italic")), ("F4", Font("Courier")),
            ("F5", Font("Barcode3of9")), ("F6", TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Symbol")))));
        var stream = pdf.Add(TestPdf.FlateStream(Encoding.Latin1.GetBytes(content)));
        var pagesNumber = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNumber)), ("Contents", TestPdf.Ref(stream)),
            ("MediaBox", TestPdf.Box(0, 0, 595, 842)), ("Resources", TestPdf.Dict(("Font", fonts)))));
        // Rotated: a landscape page (792 × 612) that the reader rotates by "rotate" degrees
        if (rotate != 0)
        {
            var pageDict = (PdfDictionary)pdf.Get(page);
            pageDict.Set(PdfName.MediaBox, TestPdf.Box(0, 0, 792, 612));
            pageDict.Set(PdfName.Rotate, PdfInteger.Of(rotate));
        }
        pdf.Replace(pagesNumber, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray { TestPdf.Ref(page) }), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pagesNumber))));
        return pdf.Build();
    }

    private static IReadOnlyList<PageTextBlock> Blocks(string content)
    {
        using var doc = Page(content);
        return TextBlocks.Find(doc, doc.Pages[0]);
    }

    private static PageTextBlock Single(IReadOnlyList<PageTextBlock> blocks, string startsWith) => Assert.Single(blocks, b => b.Text.StartsWith(startsWith, StringComparison.Ordinal));

    [Fact]
    public void Paragraph_HeadingAndColumn_AreSeparateBlocks()
    {
        var blocks = Blocks(
            "BT /F2 16 Tf 72 760 Td (Angebot) Tj ET " +
            "BT /F1 10 Tf 12 TL 72 730 Td (Wir danken Ihnen fuer Ihre Anfrage und bieten) Tj T* (Ihnen die folgenden Leistungen gerne an. Die) Tj T* (Preise gelten bis Ende des Monats.) Tj ET " +
            "BT /F1 10 Tf 12 TL 400 730 Td (Kundennummer) Tj T* (12345) Tj ET");
        Assert.Equal(3, blocks.Count);
        var heading = Single(blocks, "Angebot");
        Assert.True(heading.Lines[0].Runs[0].Format.Bold);
        Assert.Equal(16, heading.Lines[0].Size, 1);
        var paragraph = Single(blocks, "Wir danken");
        Assert.Equal(3, paragraph.Lines.Count);
        Assert.Equal(BlockAlignment.Left, paragraph.Alignment);
        Assert.Equal(12, paragraph.LineSpacing, 1);
        Assert.Equal(0, paragraph.Angle);
        Assert.Null(paragraph.Locked);
        // Full lines only wrap; the last one is the end of the paragraph
        Assert.False(paragraph.Lines[0].HardBreak);
        Assert.False(paragraph.Lines[1].HardBreak);
        Assert.True(paragraph.Lines[2].HardBreak);
        var column = Single(blocks, "Kundennummer");
        Assert.Equal("Kundennummer\n12345", column.Text);
        Assert.True(column.Lines[0].HardBreak);   // "12345" would still have fitted after it: a line of its own
    }

    [Fact]
    public void Letterhead_SmallLinesBesideLargeName_StayApart_SuperscriptJoins()
    {
        // Two address lines (8 pt) to the right of a large company name (22 pt), with baselines above and below it. Merged into one
        // line (with the measure of the large font), the characters of both address lines would be mixed by position
        var blocks = Blocks(
            "BT /F2 22 Tf 72 700 Td (Muster GmbH) Tj ET " +
            "BT /F1 8 Tf 400 704 Td (Musterstrasse 1) Tj ET " +
            "BT /F1 8 Tf 400 694.5 Td (12345 Musterstadt) Tj ET " +
            // Superscript (footnote): belongs to its line
            "BT /F1 10 Tf 72 600 Td (Preis inklusive Lieferung) Tj /F1 6 Tf 3.5 Ts (1) Tj 0 Ts /F1 10 Tf ( und Montage) Tj ET");
        Assert.Equal("Muster GmbH", Single(blocks, "Muster GmbH").Text);
        Assert.Equal("Musterstrasse 1\n12345 Musterstadt", Single(blocks, "Musterstrasse").Text);
        var footnote = Single(blocks, "Preis");
        Assert.Single(footnote.Lines);
        Assert.Equal("Preis inklusive Lieferung1 und Montage", footnote.Text);
    }

    /// <summary>
    /// A crafted file with tens of thousands of provenance records in the metadata: locking the provenance line must not slow down
    /// block detection, as happens when the records are read anew for every page and every block is compared with every identifier.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void ManyFakeSealRecords_DoNotSlowBlockDetection()
    {
        var content = new System.Text.StringBuilder();
        for (var i = 0; i < 200; i++) content.Append($"BT /F1 9 Tf 72 {800 - i * 3.6:0.0} Td (Zeile {i} mit etwas Text darin) Tj ET ");
        var json = new System.Text.StringBuilder("[");
        for (var i = 0; i < 20_000; i++)
            json.Append(i == 0 ? "" : ",").Append($"{{\"seite\":1,\"herkunft\":\"Herkunft {i:D8} xyz\",\"kennung\":\"K{i:D9}\",\"schluessel\":\"AA==\",\"inhalt\":\"AA==\",\"signatur\":\"AA==\"}}");
        json.Append(']');
        var bytes = PageBytes(content.ToString());
        using var plain = PdfDocument.Open(bytes);
        using var sealed_ = PdfDocument.Open(bytes);
        // The same page plus the records in the Info dictionary (the writer stores them as when saving)
        var info = new PdfDictionary();
        info.Set(Core.Pdf.Writing.PdfWriter.SignaturesKey, new PdfString(System.Text.Encoding.UTF8.GetBytes(json.ToString())));
        sealed_.Trailer.Set(PdfName.Info, info);
        TextBlocks.Find(plain, plain.Pages[0]);   // warm
        TextBlocks.Find(sealed_, sealed_.Pages[0]);
        // Measured alternately and compared instead of a fixed limit (the full test run computes in parallel): with the records almost equal, not a multiple
        long withSeals = 0, without = 0;
        for (var round = 0; round < 3; round++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            TextBlocks.Find(plain, plain.Pages[0]);
            without += watch.ElapsedTicks;
            watch.Restart();
            TextBlocks.Find(sealed_, sealed_.Pages[0]);
            withSeals += watch.ElapsedTicks;
        }
        Assert.True(withSeals < 2 * without + System.Diagnostics.Stopwatch.Frequency / 20,
            $"mit Nachweisen {withSeals * 1000 / System.Diagnostics.Stopwatch.Frequency} ms, ohne {without * 1000 / System.Diagnostics.Stopwatch.Frequency} ms");
    }

    /// <summary>
    /// Found by fuzzing: glyphs outside the page are discarded, but they must still count against a limit; otherwise a page with
    /// millions of glyphs beside it is processed completely (the limit for remembered glyphs never applies there).
    /// </summary>
    [Fact]
    public void MillionsOfGlyphsOutsideThePage_HitTheLimit()
    {
        var outside = new string('a', 500_000);
        using var doc = Page($"BT /F1 1 Tf 5000 5000 Td ({outside}) Tj ET BT /F1 10 Tf 72 700 Td (Sichtbar) Tj ET");
        Assert.Null(TextBlocks.Collect(doc, doc.Pages[0]));
    }

    [Fact]
    public void Justified_RightAligned_Centered()
    {
        // Courier: all characters equally wide, so lines of equal length end at the same edge
        var justified = Single(Blocks("BT /F4 10 Tf 12 TL 72 700 Td (aaaa bbbb cccc dddd) Tj T* (eeee ffff gggg hhhh) Tj T* (iiii jjjj) Tj ET"), "aaaa");
        Assert.Equal(BlockAlignment.Justify, justified.Alignment);
        var right = Single(Blocks("BT /F4 10 Tf 300 700 Td (Datum) Tj ET BT /F4 10 Tf 282 688 Td (Ort, Tag) Tj ET BT /F4 10 Tf 234 676 Td (Musterstadt, den) Tj ET"), "Datum");
        Assert.Equal(BlockAlignment.Right, right.Alignment);
        var center = Single(Blocks("BT /F4 10 Tf 285 700 Td (Titel) Tj ET BT /F4 10 Tf 264 688 Td (Untertitel 1) Tj ET BT /F4 10 Tf 285 676 Td (Zeile) Tj ET"), "Titel");
        Assert.Equal(BlockAlignment.Center, center.Alignment);
        Assert.Equal(3, center.Lines.Count);
    }

    [Fact]
    public void Bullets_EachItemABlock_WithHangingIndent()
    {
        var blocks = Blocks(
            "BT /F1 10 Tf 72 700 Td (\x95 Erster Punkt mit einem etwas laengeren Text) Tj ET " +
            "BT /F1 10 Tf 81.2 688 Td (der umbricht) Tj ET " +
            "BT /F1 10 Tf 72 676 Td (\x95 Zweiter Punkt) Tj ET " +
            "BT /F1 10 Tf 72 664 Td (- Dritter Punkt) Tj ET");
        Assert.Equal(3, blocks.Count);
        Assert.Equal(2, Single(blocks, "• Erster").Lines.Count);   // the indented continuation belongs to it
        Single(blocks, "• Zweiter");
        Single(blocks, "- Dritter");
    }

    [Fact]
    public void Runs_MixedFormatting_FakeBoldAndUnderline()
    {
        // Lines that are entirely bold or in another colour are separate blocks (heading, "appearance" rule)
        var blocks = Blocks(
            "BT /F1 12 Tf 72 700 Td (Normal ) Tj /F2 12 Tf (fett) Tj /F3 12 Tf ( kursiv) Tj ET " +
            "0 0 0 RG 0.3 w BT /F1 12 Tf 2 Tr 72 686 Td (nachgemacht) Tj ET " +
            "BT 0 Tr /F1 12 Tf 0 0 1 rg 72 672 Td (unterstrichen) Tj ET 0 0 1 RG 0.8 w 72 670 m 140 670 l S");
        Assert.Equal(3, blocks.Count);
        var runs = Single(blocks, "Normal").Lines[0].Runs;
        Assert.Equal(["Normal ", "fett", " kursiv"], runs.Select(r => r.Text));
        Assert.Equal((false, false), (runs[0].Format.Bold, runs[0].Format.Italic));
        Assert.Equal((true, false), (runs[1].Format.Bold, runs[1].Format.Italic));
        Assert.Equal((false, true), (runs[2].Format.Bold, runs[2].Format.Italic));
        Assert.Equal("Helvetica", runs[0].Format.Family);
        var fake = Assert.Single(Single(blocks, "nachgemacht").Lines[0].Runs).Format;
        Assert.True(fake.Bold && fake.FakeBold);
        var underlinedBlock = Single(blocks, "unterstrichen");
        var underlined = Assert.Single(underlinedBlock.Lines[0].Runs).Format;
        Assert.True(underlined.Underline);
        Assert.Equal(0x0000FF, underlined.Color);
        Assert.Single(underlinedBlock.Paths);
    }

    [Fact]
    public void RotatedText_HasItsOwnFrame()
    {
        var blocks = Blocks("BT /F1 10 Tf 0 1 -1 0 300 300 Tm 12 TL (Gedreht und) Tj T* (zweite Zeile) Tj ET BT /F1 10 Tf 72 700 Td (Waagerecht) Tj ET");
        var rotated = Single(blocks, "Gedreht");
        Assert.Equal(90, rotated.Angle);
        Assert.Equal(2, rotated.Lines.Count);
        Assert.True(rotated.Contains(295, 330));   // left of the baseline (top of the frame)
        Assert.Equal(0, Single(blocks, "Waagerecht").Angle);
    }

    [Fact]
    public void TableCells_OnOneBaseline_AreSeparate()
    {
        var blocks = Blocks("BT /F1 10 Tf 72 700 Td (Pos.) Tj 100 0 Td (Bezeichnung) Tj 250 0 Td (Preis) Tj ET");
        Assert.Equal(["Pos.", "Bezeichnung", "Preis"], blocks.Select(b => b.Text).OrderBy(t => t switch { "Pos." => 0, "Bezeichnung" => 1, _ => 2 }));
    }

    [Fact]
    public void Locked_BarcodeInvisibleAndPattern()
    {
        var blocks = Blocks(
            "BT /F5 24 Tf 72 760 Td (*123*) Tj ET " +
            "BT /F1 10 Tf 3 Tr 72 700 Td (Texterkennung) Tj ET " +
            "BT /F1 10 Tf 0 Tr 72 650 Td (Frei) Tj ET");
        Assert.Contains("Code-Schrift", Single(blocks, "*123*").Locked);   // to the code tool (phase 8.2)
        Assert.Contains("Unsichtbar", Single(blocks, "Texterkennung").Locked);
        Assert.Null(Single(blocks, "Frei").Locked);
    }

    /// <summary>
    /// A line under text is an underline only if it is thin and not wider than the text: the signature line under
    /// "Ort, Datum" and a table line under the cell stay in place (otherwise editing would remove them completely). A line belongs
    /// to at most one block.
    /// </summary>
    [Fact]
    public void LongLineUnderText_IsNoUnderline_RealUnderlineIsClaimedOnce()
    {
        var blocks = Blocks(
            "BT /F1 10 Tf 72 700 Td (Ort, Datum) Tj ET 0.5 w 72 697 m 400 697 l S " +
            "BT /F1 10 Tf 72 640 Td (Wichtig) Tj ET 0.6 w 72 638 m 106 638 l S " +
            "BT /F1 10 Tf 72 600 Td (Zelle) Tj ET 72 595.5 26 3 re f");
        var signature = Single(blocks, "Ort, Datum");
        Assert.Empty(signature.Paths);
        Assert.False(signature.Lines[0].Runs[0].Format.Underline);
        var important = Single(blocks, "Wichtig");
        Assert.Single(important.Paths);
        Assert.True(important.Lines[0].Runs[0].Format.Underline);
        Assert.Empty(Single(blocks, "Zelle").Paths);   // 3 pt high area: a bar, not an underline
    }

    /// <summary>
    /// Office suites put bullets in the Symbol font: as a marker it does not lock the paragraph, and the bullet stays in Symbol when
    /// editing. Symbol characters within the text itself still lock it (typed letters would turn into Greek ones).
    /// </summary>
    [Fact]
    public void SymbolBullet_DoesNotLock_SymbolInTextDoes()
    {
        using var doc = Page(
            "BT /F6 10 Tf 72 700 Td (\xB7) Tj /F1 10 Tf 18 0 Td (Punkt in Symbol) Tj ET " +
            "BT /F1 10 Tf 72 640 Td (Winkel ) Tj /F6 10 Tf (a) Tj ET");
        var blocks = TextBlocks.Find(doc, doc.Pages[0]);
        var bullet = Single(blocks, "\u2022");
        Assert.Null(bullet.Locked);
        Assert.Contains("Symbol", Single(blocks, "Winkel").Locked);
        // Edited: the bullet keeps drawing with the Symbol font of the file
        var element = Core.Pdf.Editing.EditedText.FromBlock(doc, bullet);
        var first = Core.Pdf.Editing.RichLayout.Of(element).Lines[0].Glyphs[0];
        Assert.Equal("\u2022", first.Text);
        Assert.NotNull(first.Original);
    }

    [Fact]
    public void Glyphs_NumberedInContentOrder_IncludingOverprintedBold()
    {
        // "Bold" by printing twice with a small offset: once in the text, but both must be removed
        var blocks = Blocks("BT /F1 10 Tf 72 700 Td (AB) Tj ET BT /F1 10 Tf 300 700 Td (Fett) Tj ET BT /F1 10 Tf 300.2 700 Td (Fett) Tj ET");
        var first = Single(blocks, "AB");
        Assert.Equal([0, 1], first.Glyphs);
        var bold = Single(blocks, "Fett");
        Assert.Equal("Fett", bold.Text);
        Assert.Equal(8, bold.Glyphs.Count);
        Assert.Equal(Enumerable.Range(2, 8), bold.Glyphs.Order());
    }
    /// <summary>
    /// Joining blocks for one edit (KONZEPT.md, section 5.14, „Blöcke zusammenfassen“): the heading and the paragraph of the test page become
    /// one block — lines in reading order, all characters, the paragraph's lines after the heading's; a locked block (the barcode) cannot be
    /// joined, and the reason says why.
    /// </summary>
    [Fact]
    public void Joined_HeadingAndParagraph_OneBlockInReadingOrder_LockedRefused()
    {
        using var doc = PdfDocument.Open(PageBytes(Ui.TextEditUiTests.Content));
        var blocks = TextBlocks.Find(doc, doc.Pages[0]);
        var heading = blocks.Single(b => b.Text == "Angebot");
        var paragraph = blocks.Single(b => b.Lines.Count == 3);
        var joined = PageTextBlock.Joined([paragraph, heading], out var reason);
        Assert.NotNull(joined);
        Assert.Null(reason);
        Assert.Equal(4, joined!.Lines.Count);
        Assert.Equal("Angebot", joined.Lines[0].Text);
        Assert.StartsWith("Wir danken", joined.Lines[1].Text);
        Assert.Equal(heading.Glyphs.Count + paragraph.Glyphs.Count, joined.Glyphs.Count);
        Assert.Equal(Math.Min(heading.Left, paragraph.Left), joined.Left, 3);
        Assert.Equal(heading.Top, joined.Top, 3);
        Assert.Equal(paragraph.Bottom, joined.Bottom, 3);
        var locked = blocks.Single(b => b.Locked is not null);
        Assert.Null(PageTextBlock.Joined([heading, locked], out var why));
        Assert.Equal(locked.Locked, why);
        Assert.Null(PageTextBlock.Joined([heading], out _));
    }

    /// <summary>
    /// A line of Times with one character of a Type 3 font „T7“ after it, as a word processor exports a line end (docs/FEHLERGEDAECHTNIS.md,
    /// No. 489): the glyph is named <paramref name="glyph"/> and drawn by <paramref name="procedure"/>.
    /// </summary>
    private static byte[] Type3LineEnd(string procedure, string glyph = "space")
    {
        var pdf = new TestPdf();
        var proc = pdf.Add(new PdfStream(new PdfDictionary(), Encoding.Latin1.GetBytes(procedure)));
        var type3 = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("Name", TestPdf.N("T7")),
            ("FontBBox", TestPdf.Box(0, 0, 1, 1)), ("FontMatrix", new PdfArray([new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0), new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0)])),
            ("CharProcs", TestPdf.Dict((glyph, TestPdf.Ref(proc)))), ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", new PdfArray([PdfInteger.Of(32), TestPdf.N(glyph)])))),
            ("FirstChar", PdfInteger.Of(32)), ("LastChar", PdfInteger.Of(32)), ("Widths", new PdfArray([PdfInteger.Of(250)])), ("Resources", new PdfDictionary())));
        var times = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Times-Roman")), ("Encoding", TestPdf.N("WinAnsiEncoding")));
        var stream = pdf.Add(TestPdf.FlateStream("BT /F1 11 Tf 72 700 Td (Inhalt der Seite) Tj /T7 11 Tf ( ) Tj ET"u8.ToArray()));
        var pagesNumber = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNumber)), ("Contents", TestPdf.Ref(stream)),
            ("MediaBox", TestPdf.Box(0, 0, 595, 842)), ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", times), ("T7", TestPdf.Ref(type3))))))));
        pdf.Replace(pagesNumber, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray { TestPdf.Ref(page) }), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pagesNumber))));
        return pdf.Build();
    }

    /// <summary>The one-pixel image mask the word processor draws for the space, every sample leaving the page as it is (0xFF; 0x00 paints).</summary>
    private const string BlankPixel = "21 0 0 0 1 1 d1 q 1 0 0 -1 0 1 cm BI /W 1 /H 1 /BPC 1 /IM true ID ÿ EI Q";

    /// <summary>
    /// A space of a Type 3 font that paints nothing — only d0, or a one-pixel image mask that masks everything — does not lock the block:
    /// the line is ordinary Times (docs/FEHLERGEDAECHTNIS.md, No. 489; before: „Schrift aus gezeichneten Zeichen“).
    /// </summary>
    [Theory]
    [InlineData("250 0 d0")]
    [InlineData(BlankPixel)]
    public void Type3BlankSpace_AtLineEnd_DoesNotLockTheBlock(string procedure)
    {
        using var doc = PdfDocument.Open(Type3LineEnd(procedure));
        var block = Single(TextBlocks.Find(doc, doc.Pages[0]), "Inhalt");
        Assert.Null(block.Locked);
    }

    /// <summary>
    /// A space whose procedure paints (a path, a pixel of the mask) no longer locks the block — Type 3 fonts are editable (phase 8.1,
    /// No. 606) —, but a letter that paints nothing still does (invisible text must not be slipped in).
    /// </summary>
    [Theory]
    [InlineData("250 0 d0 0 0 100 100 re f", "space", false)]
    [InlineData("21 0 0 0 1 1 d1 q 1 0 0 -1 0 1 cm BI /W 1 /H 1 /BPC 1 /IM true ID   EI Q", "space", false)]
    [InlineData("250 0 d0", "A", true)]
    public void Type3_ThatPaints_Editable_LetterThatPaintsNothing_StillLocks(string procedure, string glyph, bool locked)
    {
        using var doc = PdfDocument.Open(Type3LineEnd(procedure, glyph));
        var block = Single(TextBlocks.Find(doc, doc.Pages[0]), "Inhalt");
        if (locked) Assert.Contains("Type-3", block.Locked);
        else Assert.Null(block.Locked);
    }

    /// <summary>The block with the blank Type 3 space edited and saved: the new text in the block's own font, the block editable again.</summary>
    [Fact]
    public void Type3BlankSpace_BlockEditedAndSaved()
    {
        using var doc = PdfDocument.Open(Type3LineEnd(BlankPixel));
        var block = Single(TextBlocks.Find(doc, doc.Pages[0]), "Inhalt");
        var element = EditedText.FromBlock(doc, block);
        element = element with { Content = element.Content.Replace(0, "Inhalt".Length, "Neuer Inhalt", element.Content.StyleAt(0)) };
        var items = PageItem.From(doc);
        var removed = Redactor.RemoveGlyphs(doc, items[0].Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
        items[0] = items[0].WithEditedPage(removed, [element]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output);
        using var saved = PdfDocument.Open(output.ToArray());
        // (the block keeps its width: the longer text wraps)
        var after = Single(TextBlocks.Find(saved, saved.Pages[0]), "Neuer Inhalt");
        Assert.Equal("Neuer Inhalt der Seite", after.Text.Replace('\n', ' ').Trim());
        Assert.Null(after.Locked);
        Assert.DoesNotContain(after.Lines.SelectMany(l => l.Runs), r => r.Format.Font?.GetName(PdfName.Subtype)?.Value == "Type3");
    }
}
