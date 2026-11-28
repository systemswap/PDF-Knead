using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>The search for redacted text elsewhere in the document ends on name trees that name themselves (docs/FEHLERGEDAECHTNIS.md, No. 1264).</summary>
[Collection(nameof(UiCollection))]
public sealed class NameTreeCycleUiTests
{
    /// <summary>
    /// The trees of the destinations and of the embedded files name themselves eight times as their kids, the destination with the redacted
    /// name stands in a kid before them: the search ends and finds it.
    /// </summary>
    [Fact]
    public void FindElsewhere_NameTreesThatNameThemselves_EndsAndFindsTheName()
    {
        var self7 = string.Join(" ", Enumerable.Repeat("7 0 R", 8));
        var self9 = string.Join(" ", Enumerable.Repeat("9 0 R", 8));
        var path = TestFiles.Write(CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Names<</Dests 7 0 R/EmbeddedFiles 9 0 R>>>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
            [6] = ("", "BT /F1 12 Tf 72 700 Td (Erika Musterfrau zahlt) Tj ET"),
            [7] = $"<</Names[(Ziel) [4 0 R /Fit]]/Kids[8 0 R {self7}]>>",
            [8] = "<</Names[(Erika Musterfrau) [4 0 R /Fit]]>>",
            [9] = $"<</Kids[{self9}]>>",
        }), "namensbaum-kreis.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([path]);
                var doc = vm.SelectedDocument!;
                await doc.RedactHitsAsync(await doc.FindForRedactionAsync(["Erika Musterfrau"], RedactionPatterns.None, false));
                var search = doc.FindElsewhereAsync();
                Assert.Same(search, await Task.WhenAny(search, Task.Delay(TimeSpan.FromSeconds(30))));
                Assert.Contains(Tr.T("benannte Ziele"), (await search).Select(e => e.Where));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
