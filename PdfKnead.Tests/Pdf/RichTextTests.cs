using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Formatted text: inserting, deleting and formatting runs as in office programs; typesetting with line breaks, alignment and
/// caret; appearance with several fonts, saved and read back. See KONZEPT.md, section 5.14.
/// </summary>
public sealed class RichTextTests
{
    private static readonly RichStyle Plain = new() { Family = "Liberation Sans", Size = 10 };
    private static readonly RichStyle Bold = Plain with { Bold = true };

    [Fact]
    public void Insert_TakesTheStyleBefore_DeleteAndFormatMerge()
    {
        var text = new RichText([new RichSpan("Hallo ", Plain), new RichSpan("Welt", Bold)]);
        Assert.Equal(2, text.Spans.Count);
        // Typed at the end: bold like the character before it; in the middle of normal text: normal
        var typed = text.Insert(text.Length, "!");
        Assert.Equal("Hallo Welt!", typed.Text);
        Assert.Equal(["Hallo ", "Welt!"], typed.Spans.Select(s => s.Text));
        Assert.Equal(Plain, text.Insert(2, "x").Spans[0].Style);
        // Deleted across the boundary: what remains keeps its formatting
        var deleted = text.Delete(4, 4);
        Assert.Equal("Halllt", deleted.Text);
        Assert.Equal(["Hall", "lt"], deleted.Spans.Select(s => s.Text));
        // Formatting splits runs; equal runs merge again
        var formatted = text.Format(0, 2, s => s with { Bold = true });
        Assert.Equal(["Ha", "llo ", "Welt"], formatted.Spans.Select(s => s.Text));
        Assert.Equal(["Hallo Welt"], formatted.Format(2, 4, s => s with { Bold = true }).Spans.Select(s => s.Text));
        // Everything deleted: the formatting stays for further typing
        var empty = text.Delete(0, text.Length);
        Assert.Equal("", empty.Text);
        Assert.Single(empty.Spans);
        Assert.Equal(Plain, empty.StyleAt(0));
    }

    [Fact]
    public void StyleAt_ParagraphStart_TakesTheFollowingText()
    {
        var text = new RichText([new RichSpan("Eins\n", Plain), new RichSpan("Zwei", Bold)]);
        Assert.Equal(Bold, text.StyleAt(5));    // start of the second paragraph: like "Zwei"
        Assert.Equal(Plain, text.StyleAt(4));   // end of the first: like "s"
        Assert.Equal(Plain, text.StyleAt(0));
    }

    [Fact]
    public void Layout_WrapsAtSpaces_JustifiesAllButTheLastLine_CaretRoundTrip()
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            var text = new RichText("Wir danken Ihnen fuer Ihre Anfrage und bieten Ihnen gerne an.\nNeuer Absatz", Plain);
            var layout = RichLayout.Layout(text, 120, BlockAlignment.Justify, 1.2, 10, 0);
            Assert.True(layout.Lines.Count >= 4);
            Assert.All(layout.Lines, l => Assert.True(l.X + l.Width <= 120.01, $"{l.X + l.Width}"));
            // Full lines reach the right edge; the last line of the paragraph and the second paragraph do not
            var paragraphEnd = layout.Lines.First(l => l.EndsParagraph);
            foreach (var line in layout.Lines.TakeWhile(l => !l.EndsParagraph)) Assert.Equal(120, line.X + line.Width, 1);
            Assert.True(paragraphEnd.Width < 119);
            Assert.Equal(10, layout.Lines[0].Baseline, 3);
            Assert.Equal(12, layout.Lines[1].Baseline - layout.Lines[0].Baseline, 3);
            // Every insertion point: position → hit test gives the same point (at a line break: start of the next line)
            for (var caret = 0; caret <= text.Length; caret++)
            {
                var (x, baseline, _, _) = layout.Caret(caret);
                Assert.Equal(caret, layout.HitTest(x, baseline - 2));
            }
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }

    [Fact]
    public void Layout_LongWordBreaksHard_EmptyParagraphHasALine()
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            var layout = RichLayout.Layout(new RichText("Donaudampfschifffahrtsgesellschaft\n\nEnde", Plain), 60, BlockAlignment.Left, 1.15, null, 0);
            Assert.True(layout.Lines.Count >= 4);
            Assert.Contains(layout.Lines, l => l.Start == l.End && l.EndsParagraph);   // the empty paragraph
            Assert.All(layout.Lines.Where(l => l.Glyphs.Count > 1), l => Assert.True(l.Width <= 60.01));
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }

    /// <summary>
    /// Font of the file: edited text uses the embedded subset as far as it has the characters; missing characters are drawn
    /// by the fallback font. When saved, the block refers to the same font (not copied), and after opening it uses it again.
    /// See KONZEPT.md, section 5.14.
    /// </summary>
    [Fact]
    public void OriginalFont_UsedWhereItHasTheCharacters_KeptAcrossSaving()
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            // A file with an embedded subset (only the characters of "Hallo Welt") and ToUnicode
            byte[] first;
            using (var blank = PdfDocument.Open(TestPdf.Simple(1).Build()))
            using (var output = new MemoryStream())
            {
                var hello = new RichTextElement { Rect = new PdfRectangle(72, 650, 300, 680), Content = new RichText("Hallo Welt", Plain with { Size = 14 }) };
                PdfWriter.Write(blank, [PageItem.From(blank)[0].WithElements([hello])], output, new PdfSaveOptions { Flatten = true });
                first = output.ToArray();
            }
            using var doc = PdfDocument.Open(first);
            var block = Assert.Single(TextBlocks.Find(doc, doc.Pages[0]), b => b.Text == "Hallo Welt");
            var element = EditedText.FromBlock(doc, block);
            var original = Assert.Single(element.Content.Spans).Style.Original;
            Assert.NotNull(original);
            Assert.All(RichLayout.Of(element).Lines.SelectMany(l => l.Glyphs), g => Assert.NotNull(g.Original));

            // "Quark": Q, u, r, k are missing from the subset; they come from the fallback font, a from the font of the file
            var edited = element with { Content = element.Content.Insert(6, "Quark ") };
            var glyphs = RichLayout.Of(edited).Lines.SelectMany(l => l.Glyphs).ToList();
            Assert.Equal(["Q", "u", "r", "k"], glyphs.Where(g => g.Original is null && g.Text != " ").Select(g => g.Text));
            Assert.Contains(glyphs, g => g.Text == "a" && g.Original is not null);
            var appearance = ElementAppearance.For(edited);
            Assert.Single(appearance.OriginalFonts);
            Assert.Same(doc, appearance.Source);

            // Saved (as in the user interface: the page without the old text): the same font as a reference, the text is readable; the block
            // is as wide as "Hallo Welt" and wraps; reopened, it uses the font of the file again
            var page = PageItem.From(doc)[0];
            var removed = Redactor.RemoveGlyphs(doc, page.Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
            using var output2 = new MemoryStream();
            PdfWriter.Write(doc, [page.WithEditedPage(removed, [edited])], output2);
            using var saved = PdfDocument.Open(output2.ToArray());
            var savedText = PageText.Create(PageInterpreter.BuildText(saved, saved.Pages[0])).Text.Replace('\n', ' ');
            Assert.Contains("Hallo Quark Welt", savedText);
            Assert.Equal(savedText.IndexOf("Hallo", StringComparison.Ordinal), savedText.LastIndexOf("Hallo", StringComparison.Ordinal));
            var reloaded = Assert.IsType<RichTextElement>(Assert.Single(PageItem.From(saved)[0].Elements, e => e is RichTextElement));
            Assert.Equal("Hallo Quark Welt", reloaded.Content.Text);
            Assert.Contains(reloaded.Content.Spans, s => s.Style.Original is not null);

            // Normal and fake bold of the same font: each run drawn correctly, both saved with the font of the file and read back
            // (otherwise everything draws like the first run, and the second run loses its font after reopening)
            var plainStyle = element.Content.Spans[0].Style;
            var fakeBold = plainStyle with { Bold = true, Original = plainStyle.Original! with { FakeBold = true } };
            var mixed = element with { Content = new RichText([new RichSpan("Hallo ", plainStyle), new RichSpan("Welt", fakeBold)]) };
            var mixedGlyphs = RichLayout.Of(mixed).Lines.SelectMany(l => l.Glyphs).ToList();
            Assert.False(mixedGlyphs[0].FakeBold);
            Assert.True(mixedGlyphs[^1].FakeBold);
            Assert.Equal(2, ElementAppearance.For(mixed).OriginalFonts.Count);
            using (var output3 = new MemoryStream())
            {
                PdfWriter.Write(doc, [page.WithEditedPage(removed, [mixed])], output3);
                using var saved3 = PdfDocument.Open(output3.ToArray());
                var back = Assert.IsType<RichTextElement>(Assert.Single(PageItem.From(saved3)[0].Elements, e => e is RichTextElement));
                Assert.All(back.Content.Spans, s => Assert.NotNull(s.Style.Original));
                Assert.Equal([false, true], back.Content.Spans.Select(s => s.Style.Original!.FakeBold));
            }

            // Made bold: the font of the file has only its own style, so the word is drawn with the bold family (otherwise Ctrl+B would
            // change nothing visible); changing size or colour keeps the font of the file
            var style = element.Content.Spans[0].Style;
            Assert.Null((style with { Bold = true }).Settled(style).Original);
            Assert.Null((style with { Family = "Liberation Serif" }).Settled(style).Original);
            Assert.NotNull((style with { Size = 20, Color = 0xC00000 }).Settled(style).Original);
            var bold = element with { Content = element.Content.Format(0, 5, s => (s with { Bold = true }).Settled(s)) };
            var laid = RichLayout.Of(bold).Lines.SelectMany(l => l.Glyphs).ToList();
            Assert.All(laid.Take(5), g => Assert.Null(g.Original));
            Assert.Contains(laid.Skip(6), g => g.Original is not null);
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }

    /// <summary>
    /// Right-aligned tab stops (price list): detected, typeset (all amounts end at the same place, also a new, longer one) and
    /// present again after saving.
    /// </summary>
    [Fact]
    public void RightTabStop_AmountsEndTogether_AndSurviveSaving()
    {
        // Courier: 6 pt per character; amounts end at 170. Gaps between 1.2 and 2 font sizes (wider ones the detection separates as columns)
        using var doc = TextBlockTests.Page(
            "BT /F4 10 Tf 72 700 Td (Netto) Tj ET BT /F4 10 Tf 122 700 Td (1.000,00) Tj ET " +
            "BT /F4 10 Tf 72 688 Td (MwSt 19%) Tj ET BT /F4 10 Tf 134 688 Td (190,00) Tj ET " +
            "BT /F4 10 Tf 72 676 Td (Brutto) Tj ET BT /F4 10 Tf 122 676 Td (1.190,00) Tj ET");
        var block = Assert.Single(TextBlocks.Find(doc, doc.Pages[0]));
        var stop = Assert.Single(block.TabStops);
        Assert.Equal(BlockAlignment.Right, stop.Alignment);
        Assert.Equal(170 - 72, stop.Position, 0.5);

        var element = EditedText.FromBlock(doc, block);
        // A longer amount also ends at the stop
        element = element with { Content = element.Content.Replace(element.Content.Text.IndexOf("190,00", StringComparison.Ordinal), 6, "1.190,50", element.Content.StyleAt(0)) };
        var ends = RichLayout.Of(element).Lines.Select(l => l.X + l.Width).ToList();
        Assert.Equal(3, ends.Count);
        Assert.All(ends, e => Assert.Equal(ends[0], e, 0.5));

        var items = PageItem.From(doc);
        var removed = Redactor.RemoveGlyphs(doc, items[0].Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
        items[0] = items[0].WithEditedPage(removed, [element]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output);
        using var saved = PdfDocument.Open(output.ToArray());
        var back = Assert.IsType<RichTextElement>(Assert.Single(PageItem.From(saved)[0].Elements));
        Assert.Equal(BlockAlignment.Right, Assert.Single(back.TabStops).Alignment);
    }

    /// <summary>
    /// Converting breaks lines like the original: right-aligned with a space set tighter than the font intends (otherwise 2 lines
    /// become 3), and centred with lines of different length (otherwise it gets an "indent" and wraps).
    /// </summary>
    [Theory]
    // Courier: 6 pt per character; right edges at 500, the space before "EUR" only 3 instead of 6 pt; centred around 300
    [InlineData("BT /F4 10 Tf 434 700 Td (Summe netto) Tj ET BT /F4 10 Tf 431 688 Td (1.234,56) Tj ET BT /F4 10 Tf 482 688 Td (EUR) Tj ET")]
    [InlineData("BT /F4 10 Tf 267 700 Td (Kurze Zeile) Tj ET BT /F4 10 Tf 141 688 Td (Eine deutlich laengere zweite Zeile, die mittig steht) Tj ET")]
    public void Converted_BreaksLikeTheOriginal(string content)
    {
        using var doc = TextBlockTests.Page(content);
        var block = Assert.Single(TextBlocks.Find(doc, doc.Pages[0]));
        Assert.Equal(2, block.Lines.Count);
        var element = EditedText.FromBlock(doc, block);
        var lines = RichLayout.Of(element).Lines;
        Assert.Equal(2, lines.Count);
        Assert.Equal(block.Lines[1].Text.Trim(), element.Content.Text[lines[1].Start..lines[1].End].Trim());
    }

    /// <summary>
    /// Rotated text (90°) and text on a rotated page (/Rotate 90): edited, saved and reopened, the new text stands
    /// in the same direction at the same place, and the old text is gone.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RotatedTextOrPage_EditedAndSaved_KeepsDirectionAndPlace(bool rotatedPage)
    {
        using var doc = rotatedPage
            ? PdfDocument.Open(TextBlockTests.PageBytes("BT /F1 12 Tf 72 500 Td (Querformat Beschriftung) Tj ET BT /F1 10 Tf 72 300 Td (Waagerecht) Tj ET", rotate: 90))
            : TextBlockTests.Page("BT /F1 10 Tf 0 1 -1 0 300 300 Tm (Gedrehte Beschriftung) Tj ET BT /F1 10 Tf 72 700 Td (Waagerecht) Tj ET");
        const int index = 0;
        var items = PageItem.From(doc);
        var block = TextBlocks.Find(doc, doc.Pages[index]).First(b => b.Locked is null && !b.Text.StartsWith("Waagerecht", StringComparison.Ordinal));
        var oldText = block.Text;
        var element = EditedText.FromBlock(doc, block);
        var removed = Redactor.RemoveGlyphs(doc, items[index].Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
        items[index] = items[index].WithEditedPage(removed, [RichLayout.Fitted(element with { Content = new RichText("Neu", element.Content.Spans[0].Style) })]);

        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output);
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)   // for cross-checking in other readers
            File.WriteAllBytes(Path.Combine(folder, rotatedPage ? "t5-gedrehte-seite.pdf" : "t5-gedrehter-text.pdf"), output.ToArray());
        using var saved = PdfDocument.Open(output.ToArray());
        var blocks = TextBlocks.Find(saved, saved.Pages[index]);
        Assert.DoesNotContain(blocks, b => b.Text == oldText);
        var edited = Assert.Single(blocks, b => b.Text == "Neu");
        Assert.Equal(block.Angle, edited.Angle, 1);
        // Start of the first line at the same place (page space)
        var (ox, oy) = block.FrameToPage.Transform(block.Left, block.Lines[0].Baseline);
        var (nx, ny) = edited.FrameToPage.Transform(edited.Left, edited.Lines[0].Baseline);
        Assert.InRange(Math.Abs(ox - nx) + Math.Abs(oy - ny), 0, 3);
    }

    /// <summary>
    /// A header as a shared form XObject on two pages (with the same resources as well). Edited on page 1: only page 1 changes,
    /// page 2 still shows the old header, and the form XObject stays in the file for it.
    /// </summary>
    [Fact]
    public void HeaderFormOnTwoPages_EditedOnPageOne_PageTwoKeepsIt()
    {
        var pdf = new TestPdf();
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")),
            ("Encoding", TestPdf.N("WinAnsiEncoding"))));
        var form = pdf.Add(TestPdf.FlateStream("BT /F1 12 Tf 72 780 Td (Kopfzeile alt) Tj ET"u8.ToArray(),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 595, 842)),
                ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font)))))))));
        var shared = pdf.Add(TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font)))), ("XObject", TestPdf.Dict(("Fm1", TestPdf.Ref(form))))));
        var pages = pdf.Add(new PdfDictionary());
        var kids = new PdfArray();
        foreach (var content in new[] { "/Fm1 Do BT /F1 12 Tf 72 600 Td (Seite eins) Tj ET", "/Fm1 Do BT /F1 12 Tf 72 600 Td (Seite zwei) Tj ET" })
        {
            var stream = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content)));
            kids.Add(TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("Contents", TestPdf.Ref(stream)),
                ("MediaBox", TestPdf.Box(0, 0, 595, 842)), ("Resources", TestPdf.Ref(shared))))));
        }
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", kids), ("Count", PdfInteger.Of(2))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages))));
        using var doc = PdfDocument.Open(pdf.Build());

        var items = PageItem.From(doc);
        var block = Assert.Single(TextBlocks.Find(doc, doc.Pages[0]), b => b.Text.StartsWith("Kopfzeile", StringComparison.Ordinal));
        var element = EditedText.FromBlock(doc, block);
        var removed = Redactor.RemoveGlyphs(doc, items[0].Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
        items[0] = items[0].WithEditedPage(removed, [element with { Content = new RichText("Kopfzeile neu", element.Content.Spans[0].Style) }]);

        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output);
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
            File.WriteAllBytes(Path.Combine(folder, "t5-kopfzeile.pdf"), output.ToArray());
        using var saved = PdfDocument.Open(output.ToArray());
        // Lines as spaces: "Kopfzeile neu" is wider than "Kopfzeile alt" and wraps in the old frame
        string Text(int page) => PageText.Create(PageInterpreter.BuildText(saved, saved.Pages[page])).Text.Replace('\n', ' ');
        Assert.Contains("Kopfzeile neu", Text(0));
        Assert.DoesNotContain("Kopfzeile alt", Text(0));
        Assert.Contains("Kopfzeile alt", Text(1));
        Assert.Contains("Seite zwei", Text(1));
    }

    /// <summary>
    /// The old text lies in a form XObject whose resources two pages share; only page 1 draws it. After editing
    /// (converting, then typing, <see cref="PageItem.WithElements"/>) and saving, the old form XObject must no longer be in the file.
    /// The first typed character must not forget which originals were replaced.
    /// </summary>
    [Fact]
    public void EditedThenTyped_OldTextInSharedForm_IsNotInTheFile()
    {
        var pdf = new TestPdf();
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")),
            ("Encoding", TestPdf.N("WinAnsiEncoding"))));
        var form = pdf.Add(TestPdf.FlateStream("BT /F1 12 Tf 72 700 Td (Geheimer alter Satz) Tj ET"u8.ToArray(),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 595, 842)),
                ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font)))))))));
        var shared = pdf.Add(TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font)))), ("XObject", TestPdf.Dict(("Fm1", TestPdf.Ref(form))))));
        var pages = pdf.Add(new PdfDictionary());
        var kids = new PdfArray();
        foreach (var content in new[] { "/Fm1 Do", "BT /F1 12 Tf 72 600 Td (Seite zwei) Tj ET" })
        {
            var stream = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content)));
            kids.Add(TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("Contents", TestPdf.Ref(stream)),
                ("MediaBox", TestPdf.Box(0, 0, 595, 842)), ("Resources", TestPdf.Ref(shared))))));
        }
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", kids), ("Count", PdfInteger.Of(2))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages))));
        using var doc = PdfDocument.Open(pdf.Build());

        var items = PageItem.From(doc);
        var block = Assert.Single(TextBlocks.Find(doc, doc.Pages[0]));
        var element = EditedText.FromBlock(doc, block);
        var removed = Redactor.RemoveGlyphs(doc, items[0].Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
        items[0] = items[0].WithEditedPage(removed, [element]);
        // Typed: the element is replaced
        items[0] = items[0].WithElements([element with { Content = new RichText("Neuer Satz", element.Content.Spans[0].Style) }]);
        Assert.NotEmpty(items[0].ReplacedObjects);

        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output);
        using var saved = PdfDocument.Open(output.ToArray());
        var size = (int)(saved.Trailer.GetInteger(PdfName.Size) ?? 0);
        for (var n = 1; n < size; n++)
            if (saved.GetObject(n) is PdfStream s)
                Assert.DoesNotContain("Geheimer", System.Text.Encoding.Latin1.GetString(saved.GetStreamData(s)));
        Assert.Contains("Seite zwei", PageText.Create(PageInterpreter.BuildText(saved, saved.Pages[1])).Text);
    }

    /// <summary>
    /// Saved edited text becomes a block again on opening only if the form XObject shows exactly the stored text and is
    /// only moved. If the data say something other than the content (another program changed the text, or a crafted file: "100"
    /// instead of "1000") or the page scales the form XObject from outside, it stays ordinary content: PDF Knead shows what every reader shows.
    /// </summary>
    [Fact]
    public void StoredBlock_DataDiffersOrScaled_StaysPageContent()
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            byte[] saved;
            using (var blank = PdfDocument.Open(TestPdf.Simple(1).Build()))
            using (var output = new MemoryStream())
            {
                var block = new RichTextElement { Rect = new PdfRectangle(72, 650, 300, 680), Content = new RichText("Betrag 1000 Euro", Plain with { Size = 12 }) };
                PdfWriter.Write(blank, [PageItem.From(blank)[0].WithElements([block])], output, new PdfSaveOptions { Version = "1.4" });
                saved = output.ToArray();
            }
            using (var doc = PdfDocument.Open(saved))
                Assert.Equal("Betrag 1000 Euro", Assert.Single(PageItem.From(doc)[0].Elements.OfType<RichTextElement>()).Content.Text);

            // Replace with equal length (the cross-references stay valid): the block data and the call of the form XObject are stored uncompressed in the file
            static byte[] Patched(byte[] bytes, string from, string to)
            {
                Assert.Equal(from.Length, to.Length);
                var text = System.Text.Encoding.Latin1.GetString(bytes);
                Assert.Equal(1, text.Split(from).Length - 1);
                return System.Text.Encoding.Latin1.GetBytes(text.Replace(from, to));
            }
            void StaysContent(byte[] bytes)
            {
                using var doc = PdfDocument.Open(bytes);
                var item = PageItem.From(doc)[0];
                Assert.Empty(item.Elements.OfType<RichTextElement>());
                Assert.Contains("1000", PageText.Create(PageInterpreter.BuildText(doc, item.Page)).Text);
            }

            // Data different from the content (another program changed the text, or crafted)
            StaysContent(Patched(saved, "(Betrag 1000 Euro)", "(Betrag 100 Euro) "));
            // Scaled from outside (n-up, other format)
            StaysContent(Patched(saved, "q 1 0 0 1 72 650 cm", "q 2 0 0 2 72 650 cm"));
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }

    /// <summary>
    /// The page content leaves a graphics state open ("q 0.1 0 0 0.1 0 0 cm" without Q, occurs in real files). If the writer closes
    /// only one, every reader draws all elements after it ten times too small. The block stays where it belongs
    /// and is a block at the same place again after reopening.
    /// </summary>
    [Fact]
    public void UnbalancedPageContent_ElementsStayInPlace()
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            using var doc = TextBlockTests.Page("q 0.1 0 0 0.1 0 0 cm q BT /F1 100 Tf 720 7000 Td (Kopf) Tj ET");
            var block = new RichTextElement { Rect = new PdfRectangle(72, 600, 300, 630), Content = new RichText("An seinem Platz", Plain with { Size = 12 }) };
            using var output = new MemoryStream();
            PdfWriter.Write(doc, [PageItem.From(doc)[0].WithElements([block])], output);
            using var saved = PdfDocument.Open(output.ToArray());
            var reloaded = Assert.Single(PageItem.From(saved)[0].Elements.OfType<RichTextElement>());
            Assert.Equal(72, reloaded.Rect.Left, 1);
            Assert.Equal(630, reloaded.Rect.Top, 1);
            // The text of the block stands at its place for every reader (baseline inside the block, not at a tenth)
            var glyphs = PageInterpreter.BuildText(saved, saved.Pages[0]);
            var text = PageText.Create(glyphs).Text;
            Assert.Contains("An seinem Platz", text);
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }

    /// <summary>
    /// The page content closes a state it never opened after setting the font (a Q readers pass over). With an element added, its text stays
    /// as it was: the content was put after as many q as it closed too many, so that this Q restored the initial state — no font — and the
    /// text after it was lost (docs/FEHLERGEDAECHTNIS.md, No. 383).
    /// </summary>
    [Fact]
    public void PageContentClosingMoreThanItOpens_TextStaysAsItWas()
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            using var doc = TextBlockTests.Page("BT /F1 20 Tf ET Q BT 100 500 Td (Kopf) Tj ET");
            var before = RedactionTests.Read(doc, doc.Pages[0]).Glyphs;
            var block = new RichTextElement { Rect = new PdfRectangle(72, 600, 300, 630), Content = new RichText("An seinem Platz", Plain with { Size = 12 }) };
            using var output = new MemoryStream();
            PdfWriter.Write(doc, [PageItem.From(doc)[0].WithElements([block])], output);
            using var saved = PdfDocument.Open(output.ToArray());
            var after = RedactionTests.Read(saved, saved.Pages[0]).Glyphs;
            Assert.Equal(before, after.Take(before.Count));
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }

    /// <summary>
    /// Alternate texts (for screen readers) of the edited paragraph contain the old text: they are removed, in the page content as in
    /// the structure tree. Those of the other paragraph stay (not all of the page's are removed, and those in the structure tree are removed as well).
    /// </summary>
    [Fact]
    public void EditedBlock_AlternateTextsOfThatBlockOnly_AreRemoved()
    {
        var pdf = new TestPdf();
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")),
            ("Encoding", TestPdf.N("WinAnsiEncoding"))));
        var content = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(
            "/P <</MCID 0 /ActualText (Geheimer alter Satz)>> BDC BT /F1 12 Tf 72 700 Td (Geheimer alter Satz) Tj ET EMC " +
            "/P <</MCID 1 /ActualText (Bleibender Ersatz)>> BDC BT /F1 12 Tf 72 600 Td (Anderer Absatz) Tj ET EMC")));
        var pages = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("Contents", TestPdf.Ref(content)),
            ("MediaBox", TestPdf.Box(0, 0, 595, 842)), ("StructParents", PdfInteger.Of(0)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font))))))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray { TestPdf.Ref(page) }), ("Count", PdfInteger.Of(1))));
        var root = pdf.Add(new PdfDictionary());
        var first = pdf.Add(TestPdf.Dict(("S", TestPdf.N("P")), ("P", TestPdf.Ref(root)), ("Pg", TestPdf.Ref(page)), ("K", PdfInteger.Of(0)),
            ("ActualText", new PdfString("Geheimer alter Satz"u8.ToArray()))));
        var second = pdf.Add(TestPdf.Dict(("S", TestPdf.N("P")), ("P", TestPdf.Ref(root)), ("Pg", TestPdf.Ref(page)), ("K", PdfInteger.Of(1)),
            ("Alt", new PdfString("Bleibender Ersatz"u8.ToArray()))));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", new PdfArray { TestPdf.Ref(first), TestPdf.Ref(second) })));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages)), ("StructTreeRoot", TestPdf.Ref(root)),
            ("MarkInfo", TestPdf.Dict(("Marked", PdfBoolean.Of(true))))));
        using var doc = PdfDocument.Open(pdf.Build());

        var items = PageItem.From(doc);
        var block = Assert.Single(TextBlocks.Find(doc, doc.Pages[0]), b => b.Text.StartsWith("Geheimer", StringComparison.Ordinal));
        var element = EditedText.FromBlock(doc, block);
        var removed = Redactor.RemoveGlyphs(doc, items[0].Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
        Assert.Equal([0], removed.AlternateTextMcids);
        items[0] = items[0].WithEditedPage(removed, [element with { Content = new RichText("Neuer Satz", element.Content.Spans[0].Style) }]);

        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output);
        using var saved = PdfDocument.Open(output.ToArray());
        var all = new System.Text.StringBuilder();
        var size = (int)(saved.Trailer.GetInteger(PdfName.Size) ?? 0);
        for (var n = 1; n < size; n++)
            switch (saved.GetObject(n))
            {
                case PdfStream s:
                    all.Append(System.Text.Encoding.Latin1.GetString(saved.GetStreamData(s)));
                    break;
                case var o:
                    all.Append(o);
                    break;
            }
        var text = all.ToString();
        Assert.DoesNotContain("Geheimer", text);
        Assert.Contains("Bleibender Ersatz", text);   // in the content (MCID 1) and in the structure tree
        Assert.True(text.Split("Bleibender Ersatz").Length - 1 >= 2);
    }

    /// <summary>
    /// A clicked, unchanged block is retracted without a trace: the redo history is as before. If something followed
    /// (the step of another block), nothing is retracted (otherwise Undo would take back the wrong step).
    /// </summary>
    [Fact]
    public void Retract_OnlyTheOwnLastStep_KeepsRedo()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var history = new PageHistory(PageItem.From(doc));
        var text = new TextElement { Rect = new PdfRectangle(72, 600, 200, 620), Text = "Eins" };
        history.SetElements(0, [text], "Text");
        history.Undo();
        Assert.True(history.CanRedo);

        RichTextElement Block(string s) => new() { Rect = new PdfRectangle(72, 700, 300, 720), Content = new RichText(s, Plain) };
        var a = Block("A");
        history.Apply("Text bearbeiten", [history.Pages[0].WithElements([a])], a);
        var afterA = history.Pages;
        Assert.False(history.CanRedo);
        Assert.True(history.Retract(afterA));
        Assert.Empty(history.Pages[0].Elements);
        Assert.Equal("Text", history.RedoDescription);   // the redo step is back

        // A, then B: A retracts nothing (the last step belongs to B)
        history.Apply("Text bearbeiten", [history.Pages[0].WithElements([a])], a);
        afterA = history.Pages;
        var b = Block("B");
        history.Apply("Text bearbeiten", [history.Pages[0].WithElements([a, b])], b);
        Assert.False(history.Retract(afterA));
        Assert.Equal(2, history.Pages[0].Elements.Count);
    }

    /// <summary>
    /// The glyph of a Type 3 font without its own resources paints with a pattern from the resources of the page. If text beside it
    /// is edited (the page rewritten without its characters), the pattern must stay; otherwise it is dropped and the glyph stays empty.
    /// </summary>
    [Fact]
    public void Type3GlyphUsingPagePattern_KeepsThePattern()
    {
        var pdf = new TestPdf();
        var helvetica = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")),
            ("Encoding", TestPdf.N("WinAnsiEncoding"))));
        var function = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(1, 0, 0)), ("C1", TestPdf.Box(0, 0, 1)),
            ("N", PdfInteger.Of(1)));
        var pattern = pdf.Add(TestPdf.Dict(("PatternType", PdfInteger.Of(2)), ("Shading", TestPdf.Dict(("ShadingType", PdfInteger.Of(2)),
            ("ColorSpace", TestPdf.N("DeviceRGB")), ("Coords", TestPdf.Box(0, 0, 1000, 0)), ("Function", function)))));
        var glyph = pdf.Add(TestPdf.FlateStream("1000 0 d0 /Pattern cs /P0 scn 0 0 1000 1000 re f"u8.ToArray()));
        var type3 = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("FontBBox", TestPdf.Box(0, 0, 1000, 1000)),
            ("FontMatrix", TestPdf.Box(0.001, 0, 0, 0.001, 0, 0)), ("CharProcs", TestPdf.Dict(("a", TestPdf.Ref(glyph)))),
            ("Encoding", TestPdf.Dict(("Differences", new PdfArray { PdfInteger.Of(97), TestPdf.N("a") }))),
            ("FirstChar", PdfInteger.Of(97)), ("LastChar", PdfInteger.Of(97)), ("Widths", new PdfArray { PdfInteger.Of(1000) })));
        var content = pdf.Add(TestPdf.FlateStream("BT /F3 20 Tf 72 500 Td (a) Tj ET BT /F1 12 Tf 72 700 Td (Bearbeiteter Satz) Tj ET"u8.ToArray()));
        var pages = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("Contents", TestPdf.Ref(content)),
            ("MediaBox", TestPdf.Box(0, 0, 595, 842)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(helvetica)), ("F3", TestPdf.Ref(type3)))),
                ("Pattern", TestPdf.Dict(("P0", TestPdf.Ref(pattern))))))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray { TestPdf.Ref(page) }), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages))));
        using var doc = PdfDocument.Open(pdf.Build());

        var block = Assert.Single(TextBlocks.Find(doc, doc.Pages[0]), b => b.Text.StartsWith("Bearbeiteter", StringComparison.Ordinal));
        var removed = Redactor.RemoveGlyphs(doc, doc.Pages[0], block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
        Assert.IsType<PdfDictionary>(removed.Resources.GetDictionary(PdfName.Get("Pattern"))?.Get(PdfName.Get("P0")));
    }

    [Fact]
    public void Appearance_MixedFonts_SavedAndReadBack()
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            var content = new RichText([
                new RichSpan("Normal ", Plain),
                new RichSpan("fett", Bold),
                new RichSpan(" und ", Plain),
                new RichSpan("kursiv unterstrichen", Plain with { Italic = true, Underline = true, Color = 0x1F4E9A }),
            ]);
            var element = new RichTextElement { Rect = new PdfRectangle(72, 650, 272, 700), Content = content };
            var appearance = ElementAppearance.For(element);
            Assert.Equal(3, appearance.FaceUses.Count);   // Regular, Bold, Italic
            using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
            var page = PageItem.From(original)[0].WithElements([element]);
            using var output = new MemoryStream();
            PdfWriter.Write(original, [page], output);
            using var saved = PdfDocument.Open(output.ToArray());
            var text = PageText.Create(PageInterpreter.BuildText(saved, saved.Pages[0])).Text;
            Assert.Contains("Normal fett und kursiv unterstrichen", text.Replace('\n', ' '));
            var blocks = TextBlocks.Find(saved, saved.Pages[0]);
            var block = Assert.Single(blocks, b => b.Text.StartsWith("Normal", StringComparison.Ordinal));
            var runs = block.Lines.SelectMany(l => l.Runs).ToList();
            Assert.Contains(runs, r => r.Text.Contains("fett") && r.Format.Bold);
            Assert.Contains(runs, r => r.Text.Contains("kursiv") && r.Format.Italic && r.Format.Underline && r.Format.Color == 0x1F4E9A);
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }
}
