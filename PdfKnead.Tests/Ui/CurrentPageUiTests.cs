using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>The current page in the status bar and for the single-page view (docs/FEHLERGEDAECHTNIS.md, No. 321).</summary>
[Collection(nameof(UiCollection))]
public sealed class CurrentPageUiTests
{
    /// <summary>
    /// Zoomed out until all pages fit into the view: the centre of the view lies below the last page. The view stays at the top and the current
    /// page is the first one, not the last — otherwise the view scrolls to the end, the status bar says „Seite 3 von 3“ and „Einzelseite“ jumps
    /// to the last page.
    /// </summary>
    private static DocumentView View(MainWindow window) => window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;

    [Fact]
    public void AllPagesInView_TheCurrentPageIsTheFirst()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(3).Build(), "drei-seiten-klein.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 2400 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                // zoomed out until all pages fit and the centre of the view lies below the last page (the buttons step by 5 % now: as many steps
                // as it takes)
                for (var k = 0; k < 60 && (k < 6 || View(window).Extent.Height > View(window).Bounds.Height / 2); k++)
                {
                    vm.ZoomOutCommand.Execute(null);
                    for (var i = 0; i < 10; i++) await Task.Delay(10);
                }
                UiSession.Capture(window);
                Assert.Equal(0, View(window).Offset.Y);
                Assert.True(ReferenceEquals(doc.Pages[0], doc.CurrentPage), $"aktuell: Seite {doc.CurrentPage?.Index + 1}, Versatz {View(window).Offset}, Größe {View(window).Bounds}, Umfang {View(window).Extent}");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
