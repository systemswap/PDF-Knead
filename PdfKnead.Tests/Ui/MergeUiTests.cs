using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The window „Seiten zusammenführen“ (KONZEPT.md, section 5.2): files as tabs, pages into the result, editing it with undo, „Danach
/// nicht mehr da“, closing a tab, and the result into the open document or as a new file — which depends on no original.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class MergeUiTests
{
    private static void WaitUntil(Window window, Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            UiSession.Capture(window);
            Thread.Sleep(20);
        }
        Assert.True(condition(), "Zeitüberschreitung");
    }

    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void Select(IReadOnlyList<PageViewModel> pages, params int[] indices)
    {
        for (var i = 0; i < pages.Count; i++) pages[i].IsSelected = indices.Contains(i);
    }

    /// <summary>A file with <paramref name="pages"/> pages, protected with the open password „geheim“ and printing locked.</summary>
    private static string Protected(int pages, string name)
    {
        using var plain = PdfDocument.Open(TestPdf.Simple(pages, title: null).Build());
        using var output = new MemoryStream();
        PdfWriter.Write(plain, output, new PdfSaveOptions { Protection = new PdfProtection("geheim", "Besitzer-1", PdfPermissions.Copy | PdfPermissions.Modify) });
        return TestFiles.Write(output.ToArray(), name);
    }

    [Fact]
    public void IntoTheOpenDocument_TakeEditUndo_ProtectedFileAsksAndIsListed()
    {
        var a = TestFiles.Write(TestPdf.Simple(pages: 3).Build(), "vertrag.pdf");
        var b = Protected(2, "anlage-geschuetzt.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([a]);
                var doc = vm.SelectedDocument!;
                vm.ShowMerge = async merge =>
                {
                    var dialog = new MergeWindow(merge);
                    dialog.Show(window);
                    try
                    {
                        // the result starts with the open document, which is also a tab
                        Assert.Equal(3, merge.Result.Count);
                        Assert.True(Assert.Single(merge.Sources).IsOpenDocument);
                        var asked = 0;
                        merge.AskPassword = (_, _) =>
                        {
                            asked++;
                            return Task.FromResult<string?>("geheim");
                        };
                        await merge.AddFilesAsync([b]);
                        Assert.Equal(1, asked);
                        Assert.Equal(2, merge.Sources.Count);
                        var source = merge.SelectedSource!;
                        Assert.Equal(2, source.Pages.Count);

                        // second page of the attachment behind page 1, then both at the end
                        Select(merge.Result, 0);
                        Select(source.Pages, 1);
                        merge.InsertAfterSelectionCommand.Execute(null);
                        Select(source.Pages, 0, 1);
                        merge.AppendCommand.Execute(null);
                        Assert.Equal(6, merge.Result.Count);
                        // both grids get their tiles without a click: sharing one renderer, each asks under a name of its own (under the
                        // same name the grid drawn last cancelled the other's tiles; the headless window redraws everything every frame and
                        // hides that, so the names are checked as well)
                        Assert.NotEqual(dialog.SourceGrid.Channel, dialog.ResultGrid.Channel);
                        bool Drawn(IEnumerable<PageViewModel> pages) => pages.All(p =>
                            merge.Renderer.TryGetTile(new Rendering.TileKey(p.Key, merge.Renderer.OverviewLevel(p.Key), 0, 0, 0)) is not null);
                        WaitUntil(dialog, () => Drawn(merge.Result) && Drawn(source.Pages));
                        Assert.Equal([0, 1, 1, 2, 0, 1], merge.Result.Select(p => p.Item.Page.Index));
                        Assert.Equal(6, merge.Result.Select(p => p.Item).Distinct().Count());   // the page taken twice is a copy of its own
                        Assert.All(source.Pages, p => Assert.NotNull(p.Marks));                // used pages are marked at the top

                        // „Danach nicht mehr da“ names the open password and the print lock
                        var note = Assert.Single(merge.Notes);
                        Assert.Equal("anlage-geschuetzt.pdf", note.File);
                        Assert.Contains(Tr.T("Passwort zum Öffnen → entfällt"), note.Lines);
                        Assert.Contains(Tr.T("Drucken gesperrt → gilt nicht mehr"), note.Lines);

                        // turn, remove, undo, redo
                        Select(merge.Result, 5);
                        var before = merge.Result[5].Item.Page.Rotate;
                        merge.RotateRightCommand.Execute(null);
                        Assert.Equal((before + 90) % 360, merge.Result[5].Item.Page.Rotate);
                        merge.RemoveSelectedCommand.Execute(null);
                        Assert.Equal(5, merge.Result.Count);
                        merge.UndoCommand.Execute(null);
                        merge.UndoCommand.Execute(null);
                        Assert.Equal(before, merge.Result[5].Item.Page.Rotate);
                        merge.RedoCommand.Execute(null);
                        Assert.Equal((before + 90) % 360, merge.Result[5].Item.Page.Rotate);

                        // every button and tab has a name for screen readers (icon buttons: their tooltip; tabs: the file name)
                        foreach (var button in dialog.GetVisualDescendants().OfType<Button>().Where(b => b.GetType() == typeof(Button) && b.IsEffectivelyVisible))
                            Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(button)) && button.Content is not string,
                                $"Knopf ohne Namen: {button.Content?.GetType().Name}");
                        Assert.Contains(dialog.GetVisualDescendants().OfType<ListBoxItem>(), item => item.DataContext?.ToString() == "anlage-geschuetzt.pdf");

                        // the marker for pages dragged in
                        Assert.Equal(6, dialog.ResultGrid.ShowDropAt(new Point(5000, 5000)));
                        dialog.ResultGrid.ShowDropAt(null);
                        WaitUntil(dialog, () => merge.Renderer.IsIdle);
                        Shoot(dialog, "80-zusammenfuehren");
                        return 1;
                    }
                    finally
                    {
                        dialog.Close();
                    }
                };
                Assert.True(vm.MergeFilesCommand.CanExecute(null));
                await vm.MergeFilesCommand.ExecuteAsync(null);
                Assert.True(doc.PageCount == 6, $"{doc.PageCount} Seiten, Meldung: {vm.Message}");
                Assert.Equal(Tr.T("Seiten zusammenführen"), doc.History.UndoDescription);
                doc.History.Undo();
                Assert.Equal(3, doc.PageCount);
                doc.History.Redo();

                // saved: one file with all six pages, without the protection of the attachment
                var saved = Path.Combine(Path.GetDirectoryName(a)!, "vertrag-zusammen.pdf");
                Assert.True(await vm.SaveDocumentAsync(doc, saved, doc.SaveVersion));
                using var result = PdfDocument.Open(saved);
                Assert.Equal(6, result.PageCount);
                Assert.Null(result.Security);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>The questions of the window show every button completely, also with long answers and a long file name.</summary>
    [Fact]
    public void Questions_ShowEveryButtonCompletely()
    {
        const string longName = "Quartalsbericht Vertrieb Nord mit Anhang und sehr langem Namen.pdf";
        UiSession.Run(() =>
        {
            foreach (var (heading, message, first, second) in new[]
                     {
                         (Tr.T("Reiter „{0}“ schließen", ChoiceWindow.ShortName(longName)), Tr.T("Seiten dieser Datei stehen schon im Ergebnis. Sollen sie dort bleiben?"),
                             Tr.T("Im Ergebnis behalten"), Tr.T("Auch aus dem Ergebnis entfernen")),
                         (Tr.T("Wohin mit dem Ergebnis?"), "…", Tr.T("In „{0}“ übernehmen", ChoiceWindow.ShortName(longName)), Tr.T("Als neue Datei anlegen")),
                     })
            {
                var window = new ChoiceWindow(heading, message, first, second);
                window.Show();
                try
                {
                    UiSession.Capture(window);
                    Shoot(window, "81-rueckfrage");
                    foreach (var button in window.GetVisualDescendants().OfType<Button>())
                    {
                        var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X;
                        Assert.True(right <= window.ClientSize.Width - 8, $"„{button.Content}“ endet bei {right:0}, Fenster {window.ClientSize.Width:0}");
                        Assert.True(button.DesiredSize.Width <= button.Bounds.Width + 0.5, $"„{button.Content}“ abgeschnitten");
                    }
                }
                finally
                {
                    window.Close();
                }
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void AsNewFile_WithoutOpenDocument_AndIndependentOfTheOpenOne()
    {
        var a = TestFiles.Write(TestPdf.Simple(pages: 2).Build(), "erste.pdf");
        var b = TestFiles.Write(TestPdf.Simple(pages: 3, title: null).Build(), "zweite.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                // no document open: „Zusammenführen“ is available, the result starts empty and becomes a new file
                Assert.Null(vm.SelectedDocument);
                Assert.True(vm.MergeFilesCommand.CanExecute(null));
                vm.ShowMerge = async merge =>
                {
                    Assert.False(merge.HasTarget);
                    Assert.Empty(merge.Result);
                    await merge.AddFilesAsync([a, b]);
                    foreach (var source in merge.Sources)
                    {
                        merge.SelectedSource = source;
                        Select(source.Pages, [.. Enumerable.Range(0, source.Pages.Count)]);
                        merge.AppendCommand.Execute(null);
                    }
                    // closing a tab whose pages are used: asked; „behalten“ keeps them — and the file stays open for them
                    merge.AskRemoveOnClose = _ => Task.FromResult<bool?>(false);
                    await merge.CloseSourceCommand.ExecuteAsync(merge.Sources[0]);
                    Assert.Single(merge.Sources);
                    Assert.Equal(5, merge.Result.Count);
                    Assert.Equal(2, merge.OpenFiles);
                    // a third file loaded and closed again without taking pages: freed at once (Z-4); undo does not bring it back
                    await merge.AddFilesAsync([a]);
                    Assert.Equal(3, merge.OpenFiles);
                    merge.SelectedSource = merge.Sources[^1];
                    Select(merge.SelectedSource.Pages, [0]);
                    merge.AppendCommand.Execute(null);
                    Assert.Equal(6, merge.Result.Count);
                    merge.AskRemoveOnClose = _ => Task.FromResult<bool?>(true);   // „auch aus dem Ergebnis entfernen“
                    await merge.CloseSourceCommand.ExecuteAsync(merge.Sources[^1]);
                    Assert.Equal(2, merge.OpenFiles);
                    Assert.Equal(5, merge.Result.Count);
                    while (merge.UndoCommand.CanExecute(null)) merge.UndoCommand.Execute(null);
                    Assert.Equal(2, merge.OpenFiles);
                    Assert.All(merge.Result, p => Assert.Contains(merge.Sources, s => ReferenceEquals(s.Document, p.Item.Source)) );
                    while (merge.RedoCommand.CanExecute(null)) merge.RedoCommand.Execute(null);
                    Assert.Equal(5, merge.Result.Count);
                    return 2;
                };
                await vm.MergeFilesCommand.ExecuteAsync(null);
                var merged = vm.SelectedDocument!;
                Assert.True(merged.IsUntitled);
                Assert.True(merged.IsModified);
                Assert.Equal(Tr.T("Zusammengeführt") + ".pdf", merged.FileName);
                Assert.Equal(5, merged.PageCount);

                // with an open document: its pages go into the new file as a copy — closing the original changes nothing there
                await vm.OpenAsync([a]);
                var open = vm.SelectedDocument!;
                vm.ShowMerge = merge =>
                {
                    Assert.True(merge.UsesOpenDocument);
                    return Task.FromResult<int?>(2);
                };
                await vm.MergeFilesCommand.ExecuteAsync(null);
                var copy = vm.SelectedDocument!;
                Assert.NotSame(open, copy);
                Assert.Equal(2, copy.PageCount);
                Assert.All(copy.Pages, p => Assert.NotSame(open.Document, p.Item.Source));
                // built on the copy: the open document's title (and metadata, attachments, output intent, bookmarks) stay (Z-1)
                Assert.Equal(open.Document.Info.Title, copy.Document.Info.Title);
                Assert.NotNull(copy.Document.Info.Title);
                Assert.Equal(2, open.PageCount);
                Assert.False(open.IsModified);
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                await vm.CloseDocumentCommand.ExecuteAsync(open);
                var target = Path.Combine(Path.GetDirectoryName(a)!, "kopie.pdf");
                Assert.True(await vm.SaveDocumentAsync(copy, target, copy.SaveVersion));
                using (var saved = PdfDocument.Open(target)) Assert.Equal(2, saved.PageCount);
                Assert.Equal(new FileInfo(a).Length, TestPdf.Simple(pages: 2).Build().Length);   // the original is untouched

                // a protected open document: the new file built on its copy keeps its protection and opens with its password (Z-1)
                var locked = Protected(2, "geschuetzt-offen.pdf");
                vm.AskPassword = (_, _) => Task.FromResult<string?>("geheim");
                await vm.OpenAsync([locked]);
                var lockedDoc = vm.SelectedDocument!;
                vm.ShowMerge = _ => Task.FromResult<int?>(2);
                await vm.MergeFilesCommand.ExecuteAsync(null);
                var fresh = vm.SelectedDocument!;
                Assert.NotSame(lockedDoc, fresh);
                var lockedTarget = Path.Combine(Path.GetDirectoryName(locked)!, "geschuetzt-zusammen.pdf");
                Assert.True(await vm.SaveDocumentAsync(fresh, lockedTarget, fresh.SaveVersion));
                Assert.ThrowsAny<Exception>(() => PdfDocument.Open(lockedTarget));
                using (var saved = PdfDocument.Open(lockedTarget, "geheim")) Assert.Equal(2, saved.PageCount);
                Assert.Equal(2, fresh.PageCount);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
