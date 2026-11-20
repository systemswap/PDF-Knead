using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>UI tests for page editing: dragging, rotating, deleting, merging, splitting and saving.</summary>
[Collection(nameof(UiCollection))]
public sealed class PageEditingUiTests
{
    private static int[] Order(DocumentViewModel doc) => [.. doc.Pages.Select(p => p.Page.Index + 1)];

    private static void Pump(Window window, int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            UiSession.Capture(window);
            Thread.Sleep(16);
        }
    }

    private static void WaitUntil(Window window, Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            UiSession.Capture(window);
            Thread.Sleep(20);
        }
        Assert.True(condition(), "Zeitüberschreitung");
    }

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>
    /// Sorting: in the tile window, select pages 5 and 6 with Ctrl+click, drag them before page 1, then move page 2 to the end;
    /// change the tile size. Applying reorders the sidebar, and Ctrl+Z reverts it in one step.
    /// </summary>
    [Fact]
    public void SortWindow_MultiSelectDragAndApply()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 6).Build(), "sortieren.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.ShowSort = async sort =>
                {
                    var dialog = new SortWindow(sort) { Width = 1180, Height = 780 };
                    var result = dialog.ShowDialog<IReadOnlyList<Core.Pdf.Editing.PageItem>?>(window);
                    UiSession.Capture(dialog);
                    var grid = dialog.Grid;
                    Point At(int index, double dx = 0) => grid.TranslatePoint(grid.CellRect(index).Center + new Point(dx, 0), dialog)!.Value;
                    void Click(int index, RawInputModifiers modifiers)
                    {
                        dialog.MouseDown(At(index), MouseButton.Left, modifiers);
                        dialog.MouseUp(At(index), MouseButton.Left, modifiers);
                        Thread.Sleep(600);
                    }

                    // tile size: only in this window
                    dialog.FindControl<Slider>("SizeSlider")!.Value = 120;
                    Assert.Equal(120, grid.TileSize);
                    UiSession.Capture(dialog);

                    // drag pages 5 and 6 (Ctrl+click) before page 1
                    Click(4, RawInputModifiers.None);
                    Click(5, RawInputModifiers.Control);
                    Assert.Equal(2, sort.Pages.Count(p => p.IsSelected));
                    var start = At(5);
                    var end = At(0, -grid.CellRect(0).Width * 0.4);   // left half of the first tile: insert before it
                    dialog.MouseDown(start, MouseButton.Left);
                    dialog.MouseMove(start + new Point(12, 0), RawInputModifiers.LeftMouseButton);
                    dialog.MouseMove(end, RawInputModifiers.LeftMouseButton);
                    UiSession.Capture(dialog);
                    Shoot(dialog, "56-seiten-sortieren");
                    dialog.MouseUp(end, MouseButton.Left);
                    Assert.Equal([5, 6, 1, 2, 3, 4], sort.Pages.Select(p => p.Number));

                    // select page 2 and move it to the end
                    Thread.Sleep(600);
                    Click(3, RawInputModifiers.None);
                    Assert.Equal(2, Assert.Single(sort.Pages, p => p.IsSelected).Number);
                    sort.ToEndCommand.Execute(null);
                    Assert.Equal([5, 6, 1, 3, 4, 2], sort.Pages.Select(p => p.Number));
                    UiSession.Capture(dialog);
                    var apply = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault);
                    apply.Command?.Execute(null);
                    apply.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    return await result;
                };
                await vm.SortPagesCommand.ExecuteAsync(null);
                Assert.Equal([5, 6, 1, 3, 4, 2], Order(doc));
                Assert.Equal(Tr.T("Seiten sortieren"), doc.History.UndoDescription);
                vm.UndoCommand.Execute(null);
                Assert.Equal([1, 2, 3, 4, 5, 6], Order(doc));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void DragFirstPagesToTheEnd_ScrollsAlong_ThenSaveAs()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 12).Build(), "ziehen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "PageList");
                UiSession.Capture(window);

                // select pages 1 and 2 (click, Ctrl+click)
                var first = (ListBoxItem)list.ContainerFromIndex(0)!;
                var second = (ListBoxItem)list.ContainerFromIndex(1)!;
                window.MouseDown(CenterOf(window, first), MouseButton.Left);
                window.MouseUp(CenterOf(window, first), MouseButton.Left);
                window.MouseDown(CenterOf(window, second), MouseButton.Left, RawInputModifiers.Control);
                window.MouseUp(CenterOf(window, second), MouseButton.Left, RawInputModifiers.Control);
                UiSession.Capture(window);
                Assert.Equal([0, 1], doc.Pages.Where(p => p.IsSelected).Select(p => p.Index));

                // drag to the bottom edge of the sidebar: it scrolls along to the end
                var start = CenterOf(window, first);
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(start + new Point(0, 20), RawInputModifiers.LeftMouseButton);
                var bottom = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height - 4), window)!.Value;
                window.MouseMove(bottom, RawInputModifiers.LeftMouseButton);
                var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
                var marker = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DropMarker");
                Pump(window, 300);
                WaitUntil(window, () => scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1);
                window.MouseMove(bottom, RawInputModifiers.LeftMouseButton);
                Pump(window, 100);
                window.MouseUp(bottom, MouseButton.Left);
                UiSession.Capture(window);

                Assert.Equal([3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 1, 2], Order(doc));
                Assert.True(doc.IsModified);
                Assert.StartsWith("● ziehen.pdf", window.Title);
                Assert.Equal([10, 11], doc.Pages.Where(p => p.IsSelected).Select(p => p.Index));
                Shoot(window, "20-seiten-gezogen");

                // undo and redo from the keyboard
                window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
                Assert.Equal(Enumerable.Range(1, 12), Order(doc));
                Assert.False(doc.IsModified);
                window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, null);
                Assert.Equal(1, Order(doc)[10]);

                // save as a new file: the original stays unchanged, the tab shows the new file
                var target = Path.Combine(Path.GetDirectoryName(pdf)!, "neu sortiert.pdf");
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                vm.ShowBackstageCommand.Execute(BackstagePage.SaveAs);
                Shoot(window, "21-speichern-unter");
                await vm.SaveAsCommand.ExecuteAsync(null);
                Assert.Equal(target, doc.Path);
                Assert.False(doc.IsModified);
                Assert.False(doc.CanUndo);
                using (var saved = PdfDocument.Open(target))
                {
                    Assert.Equal(12, saved.PageCount);
                    Assert.Equal(TestPdf.PageContent(1), saved.GetStreamData(saved.Pages[10].Dictionary.GetStream(PdfName.Contents)!));
                }
                using (var original = PdfDocument.Open(pdf))
                    Assert.Equal(TestPdf.PageContent(1), original.GetStreamData(original.Pages[0].Dictionary.GetStream(PdfName.Contents)!));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RotateDeleteAndSaveOverTheOpenFile()
    {
        // Over 4 MB, so the file is memory-mapped instead of read; saving must still be able to overwrite it
        var pdf = TestPdf.Simple(pages: 4);
        var filler = new byte[5 << 20];
        new Random(1).NextBytes(filler);
        pdf.Add(new PdfStream(new PdfDictionary(), filler));   // unused: dropped when saving
        var path = TestFiles.Write(pdf.Build(), "gross.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([path]);
                var doc = vm.SelectedDocument!;
                doc.Pages[0].IsSelected = true;
                doc.Pages[2].IsSelected = true;
                vm.RotatePagesRightCommand.Execute(null);
                Assert.Equal(90, doc.Pages[0].Page.Rotate);
                Assert.Equal(90, doc.Pages[2].Page.Rotate);
                vm.DeletePagesCommand.Execute(null);
                Assert.Equal([2, 4], Order(doc));
                UiSession.Capture(window);

                window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
                WaitUntil(window, () => !doc.IsModified && vm.Busy is null);
                Assert.Equal(path, doc.Path);
                Assert.True(new FileInfo(path).Length < 1 << 20);
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.pdfknead-*"));
                using var saved = PdfDocument.Open(File.ReadAllBytes(path));
                Assert.Equal(2, saved.PageCount);
                Assert.Equal(TestPdf.PageContent(4), saved.GetStreamData(saved.Pages[1].Dictionary.GetStream(PdfName.Contents)!));
                Shoot(window, "22-gespeichert");

                // after saving, editing continues with the new file
                vm.InsertBlankPageCommand.Execute(null);
                Assert.Equal(3, doc.PageCount);
                Assert.True(doc.IsModified);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void ClosingAModifiedDocument_Asks()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 3).Build(), "fragen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var asked = 0;
                vm.AskSaveChanges = _ =>
                {
                    asked++;
                    return Task.FromResult(asked == 1 ? SaveChoice.Cancel : SaveChoice.Discard);
                };
                vm.ReversePagesCommand.Execute(null);
                await vm.CloseDocumentCommand.ExecuteAsync(null);
                Assert.Single(vm.Documents);   // cancelled
                await vm.CloseDocumentCommand.ExecuteAsync(null);
                Assert.Empty(vm.Documents);    // discarded
                Assert.Equal(2, asked);
                using var unchanged = PdfDocument.Open(pdf);
                Assert.Equal(TestPdf.PageContent(1), unchanged.GetStreamData(unchanged.Pages[0].Dictionary.GetStream(PdfName.Contents)!));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Merge_ChoosePagesWithTicks_ThenExtract()
    {
        var a = TestFiles.Write(TestPdf.Simple(pages: 3).Build(), "haupt.pdf");
        var b = TestFiles.Write(TestPdf.Simple(pages: 30, title: null).Build(XRefStyle.Stream), "anhang.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([a]);
                var doc = vm.SelectedDocument!;
                PageSelectionWindow? chooser = null;
                vm.ChoosePages = async (source, name) =>
                {
                    chooser = new PageSelectionWindow(source, name) { Width = 980, Height = 700 };
                    var result = chooser.ShowDialog<IReadOnlyList<int>?>(window);
                    UiSession.Capture(chooser);
                    // typing a range selects exactly these pages
                    chooser.FindControl<TextBox>("RangeBox")!.Text = "28-30, 2";
                    var grid = chooser.GetVisualDescendants().OfType<Controls.PageGrid>().Single();
                    WaitUntil(chooser, () => grid.Renderer!.IsIdle);
                    Shoot(chooser, "23-seiten-auswaehlen");
                    Assert.Equal(Tr.T("{0} von {1} Seiten ausgewählt", 4, 30), chooser.FindControl<TextBlock>("CountText")!.Text);
                    chooser.FindControl<Button>("OkButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    var chosen = await result;
                    await chooser.Cleanup;
                    return chosen;
                };

                await vm.InsertFilesAsync([b], at: 1);
                Assert.NotNull(chooser);
                Assert.Equal(7, doc.PageCount);
                Assert.Equal([1, 2, 28, 29, 30, 2, 3], Order(doc));
                Assert.Same(doc.Document, doc.Pages[0].Item.Source);
                Assert.NotSame(doc.Document, doc.Pages[1].Item.Source);
                Assert.Equal([1, 2, 3, 4], doc.Pages.Where(p => p.IsSelected).Select(p => p.Index));
                UiSession.Capture(window);
                Shoot(window, "24-eingefuegt");

                // extract the inserted pages: new file, the document stays unchanged
                var target = Path.Combine(Path.GetDirectoryName(a)!, "auszug.pdf");
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.ExtractPagesCommand.ExecuteAsync(null);
                Assert.Equal(7, doc.PageCount);
                using (var extract = PdfDocument.Open(target))
                {
                    Assert.Equal(4, extract.PageCount);
                    Assert.Equal(TestPdf.PageContent(28), extract.GetStreamData(extract.Pages[1].Dictionary.GetStream(PdfName.Contents)!));
                }

                // save: merged file; afterwards the inserted file is released
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.False(doc.IsModified);
                using (var merged = PdfDocument.Open(a))
                {
                    Assert.Equal(7, merged.PageCount);
                    Assert.Equal(TestPdf.PageContent(30), merged.GetStreamData(merged.Pages[4].Dictionary.GetStream(PdfName.Contents)!));
                }
                File.Delete(b);   // succeeds only if nothing holds it open any more
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Split_DragSelectionsIntoNamedParts()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 8).Build(), "sammlung.pdf");
        var folder = Directory.CreateTempSubdirectory("pdf-knead-teile-").FullName;
        File.WriteAllBytes(Path.Combine(folder, "Teil 2.pdf"), [1, 2, 3]);   // an existing file stays untouched
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                vm.ShowSplit = async split =>
                {
                    split.PickFolder = () => Task.FromResult<string?>(folder);
                    var dialog = new SplitWindow(split) { Width = 1180, Height = 780 };
                    var result = dialog.ShowDialog<bool?>(window);
                    UiSession.Capture(dialog);
                    var grid = dialog.GetVisualDescendants().OfType<Controls.PageGrid>().Single();
                    var target = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NewPartTarget");
                    void DragTo(int fromPage, Control to)
                    {
                        UiSession.Capture(dialog);
                        var start = grid.TranslatePoint(grid.CellRect(fromPage).Center, dialog)!.Value;
                        var end = to.TranslatePoint(new Point(to.Bounds.Width / 2, to.Bounds.Height / 2), dialog)!.Value;
                        dialog.MouseDown(start, MouseButton.Left);
                        dialog.MouseMove(start + new Point(12, 0), RawInputModifiers.LeftMouseButton);
                        dialog.MouseMove(end, RawInputModifiers.LeftMouseButton);
                        dialog.MouseUp(end, MouseButton.Left);
                        UiSession.Capture(dialog);
                    }

                    // select 1-3 by range, drag onto the drop target → part 1, rename it
                    dialog.FindControl<TextBox>("RangeBox")!.Text = "1-3";
                    UiSession.Capture(dialog);
                    DragTo(0, target);
                    Assert.Single(split.Parts);
                    split.Parts[0].Name = "Angebot / Entwurf";
                    // 4-5 → part 2
                    dialog.FindControl<TextBox>("RangeBox")!.Text = "4-5";
                    DragTo(3, target);
                    Assert.Equal(2, split.Parts.Count);
                    // drag page 8 onto the first part → added to it
                    dialog.FindControl<TextBox>("RangeBox")!.Text = "8";
                    var firstPart = dialog.GetVisualDescendants().OfType<Border>().First(b => b.Tag is "part");
                    DragTo(7, firstPart);
                    Assert.Equal([0, 1, 2, 7], split.Parts[0].Pages.Select(p => p.Index));
                    Assert.Equal(2, split.Pages[7].Marks?.Count + 1);
                    split.IncludeRest = true;
                    WaitUntil(dialog, () => grid.Renderer!.IsIdle);
                    Shoot(dialog, "25-teilen");

                    await split.SaveCommand.ExecuteAsync(null);
                    return await result == true;
                };
                await vm.SplitCommand.ExecuteAsync(null);

                Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(folder, "Teil 2.pdf")));
                using (var first = PdfDocument.Open(Path.Combine(folder, "Angebot _ Entwurf.pdf")))
                {
                    Assert.Equal(4, first.PageCount);
                    Assert.Equal(TestPdf.PageContent(8), first.GetStreamData(first.Pages[3].Dictionary.GetStream(PdfName.Contents)!));
                }
                using (var second = PdfDocument.Open(Path.Combine(folder, "Teil 2 (2).pdf")))
                    Assert.Equal(TestPdf.PageContent(4), second.GetStreamData(second.Pages[0].Dictionary.GetStream(PdfName.Contents)!));
                using (var rest = PdfDocument.Open(Path.Combine(folder, Tr.T("Übrige Seiten") + ".pdf")))
                    Assert.Equal(2, rest.PageCount);   // 6 and 7
                Assert.False(vm.SelectedDocument!.IsModified);   // the document itself stays unchanged
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FitPage_WithMixedPageSizes_StillScrolls()
    {
        // An A0 drawing with inserted A4 pages: with fit-page zoom the view must still scroll. See docs/FEHLERGEDAECHTNIS.md, No. 22.
        string path;
        using (var a4 = PdfDocument.Open(TestPdf.Simple(pages: 3).Build()))
        {
            var a0 = Core.Pdf.Editing.BlankPage.Create(2383.94, 3370.39);
            var items = Core.Pdf.Editing.PageItem.From(a4);
            path = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-test-").FullName, "gemischt.pdf");
            using (var output = File.Create(path))
                Core.Pdf.Writing.PdfWriter.Write(a0.Source, [a0, items[0], items[2], items[0]], output);
            a0.Source.Dispose();
        }
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([path]);
                var doc = vm.SelectedDocument!;
                var view = window.GetVisualDescendants().OfType<Controls.DocumentView>().Single();
                doc.CurrentPage = doc.Pages[1];   // A4 page: at this zoom the A0 page is much wider than the window
                vm.FitPageCommand.Execute(null);
                Pump(window, 300);
                var before = view.Offset.Y;
                var center = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;
                for (var i = 0; i < 3; i++)
                {
                    window.MouseWheel(center, new Vector(0, -1));
                    Pump(window, 50);
                }
                Pump(window, 300);
                Assert.True(view.Offset.Y > before + 100, $"rollt nicht: {before} → {view.Offset.Y}");

                // back up into the middle of the A0 drawing (much taller than the window at this zoom)
                for (var i = 0; i < 12; i++)
                {
                    window.MouseWheel(center, new Vector(0, 1));
                    Pump(window, 30);
                }
                // the sidebar has no orphaned entries (ScrollIntoView right after opening can leave one behind)
                var pageList = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "PageList");
                Assert.DoesNotContain(pageList.GetVisualDescendants().OfType<ListBoxItem>(), li => li.IsVisible && pageList.IndexFromContainer(li) < 0);
                var inPlan = view.Offset.Y;
                Assert.InRange(inPlan, 200, before - 200);
                // the viewport changes (scroll bar on/off, window size): the position stays, no jump to the top of the page
                window.Width = 1290;
                Pump(window, 200);
                Assert.InRange(view.Offset.Y, inPlan - 40, inPlan + 40);

                // two-page layout (View tab) with fit-page zoom: both pages fully visible
                doc.CurrentPage = doc.Pages[2];
                Pump(window, 100);
                vm.SetLayoutCommand.Execute(PageLayout.TwoPages);
                vm.FitPageCommand.Execute(null);
                Pump(window, 200);
                Assert.Equal(PageLayout.TwoPages, doc.PageLayout);
                Assert.Equal(ZoomMode.FitPage, doc.ZoomMode);
                var left = view.PagePointToView(2, 0, 0);
                var right = view.PagePointToView(3, 595, 842);
                Assert.True(left.X >= 0 && right.X <= view.Bounds.Width && right.Y <= view.Bounds.Height + 1,
                    $"nicht ganz sichtbar: {left} … {right} in {view.Bounds}");
                Shoot(window, "26-doppelseite");
                vm.SetLayoutCommand.Execute(PageLayout.Continuous);
                Assert.Equal(PageLayout.Continuous, doc.PageLayout);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Search_WithSmallTextBudget_FindsEverything_AndRevealsEvictedPages()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 30).Build(), "dick.pdf");
        var budget = DocumentViewModel.TextBudget;
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                DocumentViewModel.TextBudget = 2_000;   // enough for a few pages only
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.OpenSearch();
                doc.SearchText = "seite";
                WaitUntil(window, () => !doc.IsSearching);
                Assert.Equal(30, doc.Matches.Count);   // all found, although not all page texts are kept
                Assert.True(doc.TextBytes <= 4_000, $"Text hält {doc.TextBytes} Bytes");
                // jumping to a match on a page evicted long ago: its text is read again and the match is shown
                var revealed = -1;
                doc.RevealRequested += (page, _) => revealed = page;
                doc.CurrentMatch = 2;
                doc.FindNext();   // match 3 → page 4
                WaitUntil(window, () => revealed == 3);
            }
            finally
            {
                DocumentViewModel.TextBudget = budget;
                window.Close();
            }
        });
    }

    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
