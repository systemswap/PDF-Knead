using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Suchen und schwärzen“ on a page that draws its characters out of order (each at a place of its own, from right to left): the name is
/// found and redacted as a whole (docs/FEHLERGEDAECHTNIS.md, No. 267).
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class ScrambledTextUiTests
{
    [Fact]
    public void NameDrawnFromRightToLeft_FoundAndRedacted()
    {
        // „Max Mustermann“ in Helvetica 12 pt, each character with its own matrix, the last one first
        var places = new (string C, double X)[] { ("M", 20), ("a", 29.996), ("x", 36.668), ("M", 46.004), ("u", 56), ("s", 62.672), ("t", 68.672), ("e", 72.008), ("r", 78.68), ("m", 82.676), ("a", 92.672), ("n", 99.344), ("n", 106.016) };
        var content = "BT /F1 12 Tf " + string.Join(" ", places.Reverse().Select(p => $"1 0 0 1 {p.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} 700 Tm ({p.C}) Tj")) + " ET";
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new Core.Pdf.Objects.PdfStream(new Core.Pdf.Objects.PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(content)));
        var path = TestFiles.Write(pdf.Build(), "rueckwaerts.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([path]);
                var doc = vm.SelectedDocument!;
                var hits = await doc.FindForRedactionAsync(["Max Mustermann"], RedactionPatterns.None, false);
                var hit = Assert.Single(hits);
                Assert.True(hit.InPlace);
                Assert.Equal("Max Mustermann", hit.Found);
                Assert.Equal(1, await doc.RedactHitsAsync(hits));
                var mark = Assert.Single(doc.History.Pages[0].Elements.OfType<Core.Pdf.Editing.RedactionElement>());
                Assert.True(mark.Rect.Left < 21 && mark.Rect.Right > 75.5, $"die Markierung deckt den ganzen Namen ({mark.Rect})");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
