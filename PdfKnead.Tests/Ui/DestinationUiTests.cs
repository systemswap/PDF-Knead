using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// A bookmark leads to the place and zoom of its destination, not only to the top of the page (ISO 32000-2, 12.3.2.2; docs/ABLAUFPLAN.md,
/// 7.3 b; docs/FEHLERGEDAECHTNIS.md, No. 564).
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class DestinationUiTests
{
    /// <summary>Three A4 pages, one bookmark with <paramref name="dest"/>.</summary>
    private static string Outlined(string dest) => TestFiles.Write(CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/Outlines 6 0 R>>",
        [2] = "<</Type/Pages/Kids[3 0 R 4 0 R 5 0 R]/Count 3/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R>>",
        [4] = "<</Type/Page/Parent 2 0 R>>",
        [5] = "<</Type/Page/Parent 2 0 R>>",
        [6] = "<</Type/Outlines/First 7 0 R/Last 7 0 R/Count 1>>",
        [7] = "<</Title(Abschnitt von Max Mustermann)/Parent 6 0 R/Dest" + dest + ">>",
    }), "lesezeichen.pdf");

    /// <summary>
    /// [page 2 /XYZ 0 400 2]: zoom 200 %, the point y = 400 of page 2 at the top of the view (below the margin). Before: zoom unchanged, the
    /// top of the page at the top.
    /// </summary>
    [Fact]
    public void Outline_XyzDestination_ScrollsToThePlaceAndZoom()
    {
        var path = Outlined("[4 0 R/XYZ 0 400 2]");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([path]);
                var doc = vm.SelectedDocument!;
                vm.FitPageCommand.Execute(null);
                UiSession.Capture(window);
                var item = Assert.Single(PdfOutline.Read(doc.Document));
                vm.GoToOutlineCommand.Execute(item);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Assert.Equal(2, doc.Zoom, 3);
                var at = view.PagePointToView(1, 0, 400);
                Assert.InRange(at.Y, DocumentView.PageMargin - 2, DocumentView.PageMargin + 2);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>[page 2 /FitH 300]: page width, y = 300 of page 2 at the top (not the last page: the view cannot scroll past the end).</summary>
    [Fact]
    public void Outline_FitHDestination_PageWidthAndTop()
    {
        var path = Outlined("[4 0 R/FitH 300]");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([path]);
                var doc = vm.SelectedDocument!;
                UiSession.Capture(window);
                vm.GoToOutlineCommand.Execute(Assert.Single(PdfOutline.Read(doc.Document)));
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Assert.Equal(ZoomMode.FitWidth, doc.ZoomMode);
                Assert.InRange(view.PagePointToView(1, 0, 300).Y, DocumentView.PageMargin - 2, DocumentView.PageMargin + 2);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
