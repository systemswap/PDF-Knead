using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Verdeckte Teile von Bildern“ in „Dokument bereinigen“ (docs/RECHERCHE-VERSTECKEN.md, B-11): listed with the number of images, not preselected
/// (JPEG images are written anew); its page button frames the images on the page; chosen, the open document no longer has any.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class HiddenImagePartsUiTests
{
    [Fact]
    public void Listed_NotPreselected_ChosenCutsInTheDocument()
    {
        var pdf = TestFiles.Write(HiddenImagePartsTests.File(), "verdeckte-bildteile.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var view = window.GetVisualDescendants().OfType<PdfKnead.Controls.DocumentView>().Single();
                // the area on the right with the findings; the page of a finding as a button frames the places in red
                await vm.CleanDocumentCommand.ExecuteAsync(null);
                Assert.True(doc.IsCleanupOpen);
                var row = Assert.Single(doc.CleanupRows, r => r.Item == CleanupItems.HiddenImageParts);
                var label = row.Title;
                Assert.False(row.IsChecked, "nicht vorgewählt");
                await doc.ShowCleanupPageCommand.ExecuteAsync(Assert.Single(row.Pages));
                Assert.Equal(0, doc.CleanupPlaces?.Page);
                Assert.Equal(3, doc.CleanupPlaces?.Places.Count);
                UiSession.Capture(window);
                Assert.Equal(3, view.CleanupFrames.Count);
                Assert.Equal(3, view.CleanupButtons.Count);   // each with its „x“
                Assert.Equal(Tr.T("Seite {0}: {1} Stellen rot umrandet — „x“ am Rahmen: nicht löschen.", 1, 3), row.Result);
                foreach (var other in doc.CleanupRows) other.IsChecked = false;
                row.IsChecked = true;
                await vm.ApplyCleanupCommand.ExecuteAsync(null);
                Assert.False(doc.IsCleanupOpen);
                UiSession.Capture(window);
                Assert.Empty(view.CleanupFrames);   // the frames go with the area
                Assert.Contains("3", label);
                Assert.DoesNotContain(DocumentCleanup.Inspect(doc.Document, PageItem.From(doc.Document)), f => f.Item == CleanupItems.HiddenImageParts);
                Assert.True(doc.IsModified);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
