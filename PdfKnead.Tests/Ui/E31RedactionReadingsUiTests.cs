using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// M7, package E31 (docs/FEHLERGEDAECHTNIS.md, No. 1235): „Suchen und schwärzen“ and „Jetzt schwärzen?“ on a page with a CID TrueType font that
/// is not embedded and has no /ToUnicode, drawn through the glyph order of Arial (A210): a reference reader shows its CIDs as Unicode, so the
/// words, the patterns and the other places of what was redacted count in that reading as well.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class E31RedactionReadingsUiTests
{
    /// <summary>
    /// One page: „ABCD“ and „info@beispiel.de“ as CIDs in the font Arial (drawn as glyph numbers, read „^_`a“ and so on), „ABCD“ in Helvetica
    /// below.
    /// </summary>
    private static byte[] File() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 5 0 R/Resources<</Font<</F1 3 0 R/F2 6 0 R>>>>>>",
        [5] = ("", "BT /F2 20 Tf 72 700 Td <0041004200430044> Tj ET " +
                   "BT /F2 20 Tf 72 650 Td <0069006E0066006F00400062006500690073007000690065006C002E00640065> Tj ET " +
                   "BT /F1 20 Tf 72 600 Td (ABCD) Tj ET"),
        [6] = "<</Type/Font/Subtype/Type0/BaseFont/Arial/Encoding/Identity-H/DescendantFonts[7 0 R]>>",
        [7] = "<</Type/Font/Subtype/CIDFontType2/BaseFont/Arial/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/FontDescriptor 8 0 R/DW 600>>",
        [8] = "<</Type/FontDescriptor/FontName/Arial/Flags 32/FontBBox[-665 -325 2000 1006]/ItalicAngle 0/Ascent 905/Descent -212/CapHeight 716/StemV 80>>",
    });

    /// <summary>
    /// „Suchen und schwärzen“ with the word „ABCD“ and the pattern for e-mail addresses: the word in both fonts and the address drawn as glyph
    /// numbers are found — the address only in the second reading. Marking the place in Arial, „Jetzt schwärzen?“ offers the „ABCD“ in Helvetica
    /// as another place of the redacted text. Before, only the Helvetica word was found, and nothing was offered.
    /// </summary>
    [Fact]
    public void SearchAndRedact_SecondReading_WordsPatternsAndOtherPlaces()
    {
        var pdf = TestFiles.Write(File(), "e31-lesarten.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var text = await doc.GetTextAsync(0);
                Assert.DoesNotContain("ABCD\n", text.Text);   // copying keeps the text of the drawn glyphs
                var hits = await doc.FindForRedactionAsync(["ABCD"], RedactionPatterns.Email, matchCase: false);
                Assert.Equal(3, hits.Count);
                Assert.Single(hits, h => h.Kind == RedactionFinds.Email);

                var arial = hits.OrderBy(h => h.Range.Start).First();
                doc.AddElement(0, RedactionElement.FromPageAreas(DocumentViewModel.AreasOf(text, arial.Range)), "Test");
                var other = Assert.Single(await doc.FindUnredactedAsync());
                Assert.Equal((0, "ABCD"), (other.Page, other.Found));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The page of <see cref="File"/> with a line in a font whose CIDs select no glyph (A258: no installed font, no glyph order) below: codes
    /// 0041 0042 read „AB“ and draw „^_“ (standard Macintosh order, No. 1245).
    /// </summary>
    private static byte[] FileWithoutGlyphs() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 5 0 R/Resources<</Font<</F3 6 0 R>>>>>>",
        [5] = ("", "BT /F3 20 Tf 72 700 Td <00410042> Tj ET"),
        [6] = "<</Type/Font/Subtype/Type0/BaseFont/ProbeSans/Encoding/Identity-H/DescendantFonts[7 0 R]>>",
        [7] = "<</Type/Font/Subtype/CIDFontType2/BaseFont/ProbeSans/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/FontDescriptor 8 0 R/DW 600>>",
        [8] = "<</Type/FontDescriptor/FontName/ProbeSans/Flags 32/FontBBox[-665 -325 2000 1006]/ItalicAngle 0/Ascent 905/Descent -212/CapHeight 716/StemV 80>>",
    });

    /// <summary>
    /// The window of „Suchen und schwärzen“ shows a hit in the reading it was found in (No. 1246): „ABCD“ in Arial, found only in the second
    /// reading, as „ABCD“ with the note „zweite Lesart der Schrift“ and „ABCD“ in its context (not as „^_`a“, the text of the glyphs drawn), the same
    /// word in Helvetica and „^_`a“ searched for as they read, without the note; the e-mail address drawn as glyph numbers as the address. The
    /// range stays the one of the text, so the bar covers the same glyphs. „Jetzt schwärzen?“ counts the Arial place as the whole redacted piece
    /// after the Helvetica word was marked (before: „^_`a“, only a word of it). And where the CIDs select no glyph (A258), „^_“ searched for is
    /// shown as „^_“ with the note, „AB“ as „AB“ without it. Before, „Gefunden“ showed „^_`a“ and „AB“ for every hit there.
    /// </summary>
    [Fact]
    public void SearchAndRedact_HitShownInTheReadingItWasFoundIn()
    {
        var pdf = TestFiles.Write(File(), "e31-lesarten-anzeige.pdf");
        var none = TestFiles.Write(FileWithoutGlyphs(), "e33-ohne-glyphen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var text = await doc.GetTextAsync(0);
                var hits = (await doc.FindForRedactionAsync(["ABCD"], RedactionPatterns.Email, matchCase: false)).OrderBy(h => h.Range.Start).ToList();
                Assert.Equal(3, hits.Count);
                var (arial, mail, helvetica) = (hits[0], hits[1], hits[2]);
                Assert.Equal(("ABCD", true), (arial.Found, arial.SecondReading));
                Assert.Equal("ABCD", arial.Context.Substring(arial.InContext.Start, arial.InContext.Length));
                Assert.Equal("^_`a", text.Text.Substring(arial.Range.Start, arial.Range.Length));   // the range stays one of the text
                Assert.Equal(("info@beispiel.de", true), (mail.Found, mail.SecondReading));
                Assert.Equal(("ABCD", false), (helvetica.Found, helvetica.SecondReading));
                var drawn = Assert.Single(await doc.FindForRedactionAsync(["^_`a"], RedactionPatterns.None, matchCase: false));
                Assert.Equal(("^_`a", false, arial.Range), (drawn.Found, drawn.SecondReading, drawn.Range));

                var dialog = new SearchRedactWindow((w, p, m) => doc.FindForRedactionAsync(w, p, m));
                dialog.Show();
                dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "WordsBox").Text = "ABCD";
                await dialog.SearchAsync();
                var titles = dialog.Choices.Select(c => c.Title).ToList();
                var note = " (" + Tr.T("zweite Lesart der Schrift") + ")";
                Assert.Contains(Tr.T("Seite {0}: {1}", 1, "ABCD") + note, titles);
                Assert.Contains(Tr.T("Seite {0}: {1}", 1, "ABCD"), titles);
                Assert.DoesNotContain(titles, t => t.Contains("^_", StringComparison.Ordinal));
                dialog.Close();

                doc.AddElement(0, RedactionElement.FromPageAreas(DocumentViewModel.AreasOf(text, helvetica.Range)), "Test");
                var other = Assert.Single(await doc.FindUnredactedAsync());
                Assert.Equal((0, "ABCD", true, true), (other.Page, other.Found, other.SecondReading, other.Whole));

                await vm.OpenAsync([none]);
                var plain = vm.SelectedDocument!;
                var standard = Assert.Single(await plain.FindForRedactionAsync(["^_"], RedactionPatterns.None, matchCase: false));
                Assert.Equal(("^_", true), (standard.Found, standard.SecondReading));
                var own = Assert.Single(await plain.FindForRedactionAsync(["AB"], RedactionPatterns.None, matchCase: false));
                Assert.Equal(("AB", false, standard.Range), (own.Found, own.SecondReading, own.Range));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
