using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Dokument bereinigen“ as an area on the right (KONZEPT.md, section 5.18): the findings to tick, a page button frames the places, the „x“ on a
/// frame keeps that place — removed is everything else of the item, also when saved later; a second click on the ribbon button closes it.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class CleanupPanelUiTests
{
    private static byte[] File() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
        [6] = ("", "BT /F1 12 Tf 72 780 Td (Sichtbar) Tj ET q BT 3 Tr /F1 12 Tf 72 700 Td (Behalten) Tj ET Q q BT 3 Tr /F1 12 Tf 72 500 Td (Entfernen) Tj ET Q"),
    });

    private static string Text(PdfDocument doc) => PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]), 0).Text;

    [Fact]
    public void KeepAPlaceWithItsX_TheRestGoes_AlsoWhenSaved()
    {
        var pdf = TestFiles.Write(File(), "bereinigen-bereich.pdf");
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
                void Click(Point p)
                {
                    window.MouseDown(p, MouseButton.Left);
                    window.MouseUp(p, MouseButton.Left);
                }
                await vm.CleanDocumentCommand.ExecuteAsync(null);
                Assert.True(doc.IsCleanupOpen);
                Assert.True(window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Single(b => b.Name == "CleanupHost").IsVisible);
                var row = Assert.Single(doc.CleanupRows, r => r.Item == CleanupItems.InvisibleText);
                Assert.False(row.IsChecked);   // OCR text is not preselected
                row.IsChecked = true;
                await doc.ShowCleanupPageCommand.ExecuteAsync(Assert.Single(row.Pages));
                UiSession.Capture(window);
                Assert.Equal(2, view.CleanupButtons.Count);
                // the „x“ on the frame of „Behalten“, clicked with the mouse
                var (button, place) = Assert.Single(view.CleanupButtons, b => b.Place.Text.Contains("Behalten"));
                Click(view.TranslatePoint(button.Center, window)!.Value);
                Assert.True(doc.IsCleanupKept(0, CleanupItems.InvisibleText, place));
                Assert.Contains("1 Stelle", doc.CleanupKeptText);
                // a second click takes it back, a third keeps it again
                UiSession.Capture(window);
                Click(view.TranslatePoint(button.Center, window)!.Value);
                Assert.False(doc.IsCleanupKept(0, CleanupItems.InvisibleText, place));
                UiSession.Capture(window);
                Click(view.TranslatePoint(button.Center, window)!.Value);

                foreach (var other in doc.CleanupRows) other.IsChecked = other.Item == CleanupItems.InvisibleText;
                await vm.ApplyCleanupCommand.ExecuteAsync(null);
                Assert.False(doc.IsCleanupOpen);
                Assert.Equal("Sichtbar\nBehalten", Text(doc.Document));
                vm.UndoCommand.Execute(null);
                Assert.Equal("Sichtbar\nBehalten\nEntfernen", Text(doc.Document));
                vm.RedoCommand.Execute(null);
                // saving cleans again what was cleaned — not the place kept
                var copy = Path.Combine(Path.GetDirectoryName(pdf)!, "bereinigen-bereich-kopie.pdf");
                await doc.SaveAsync(copy, doc.SaveOptions("1.7"));
                using (var saved = PdfDocument.Open(System.IO.File.ReadAllBytes(copy))) Assert.Equal("Sichtbar\nBehalten", Text(saved));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void SecondClickCloses_AToolWithItsOwnAreaClosesIt()
    {
        var pdf = TestFiles.Write(File(), "bereinigen-schliessen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                await vm.CleanDocumentCommand.ExecuteAsync(null);
                Assert.True(doc.IsCleanupOpen);
                await vm.CleanDocumentCommand.ExecuteAsync(null);
                Assert.False(doc.IsCleanupOpen);
                await vm.CleanDocumentCommand.ExecuteAsync(null);
                vm.SetToolCommand.Execute(ViewTool.RedactHidden);
                Assert.False(doc.IsCleanupOpen);
                Assert.Same(doc.Document, doc.History.Main ?? doc.Document);
                Assert.False(doc.CanUndo);   // closing removed nothing
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// „Titel behalten“ is preset for a tagged file only — readers for the blind announce the title there; cleaning up an untagged one removes
    /// it too, titles often name a person (decided on 5.11.2026).
    /// </summary>
    [Fact]
    public void KeepTitle_PresetForTaggedFilesOnly()
    {
        var untagged = TestFiles.Write(File(), "bereinigen-titel-ungetaggt.pdf");
        var tagged = TestFiles.Write(CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/MarkInfo<</Marked true>>/StructTreeRoot 5 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [4] = "<</Type/Page/Parent 2 0 R/Resources<<>>>>",
            [5] = "<</Type/StructTreeRoot/K[]>>",
        }), "bereinigen-titel-getaggt.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([untagged, tagged]);
            Assert.False(vm.Documents.Single(d => d.Path.EndsWith("ungetaggt.pdf", StringComparison.Ordinal)).KeepTitleOnCleanup);
            Assert.True(vm.Documents.Single(d => d.Path.EndsWith("-getaggt.pdf", StringComparison.Ordinal)).KeepTitleOnCleanup);
        });
    }
}
