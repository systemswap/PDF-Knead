using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfKnead.Core.Fonts;
using PdfKnead.Services;
using PdfKnead.ViewModels;
using PdfKnead.Tests.Pdf;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Headless tests of the main window: builds it and shows every ribbon tab, the File area,
/// an open document, light and dark, German and English. With PDFKNEAD_SCREENSHOTS set,
/// the tests save screenshots for visual inspection.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class MainWindowTests
{
    /// <summary>
    /// Every ribbon tab fits the window at its start size, with room for further groups: at 1,280 px the last groups of „Formular“ were
    /// cut off. (A narrower window scrolls the ribbon sideways.)
    /// </summary>
    [Fact]
    public void StartSize_EveryRibbonTabFits()
    {
        UiSession.Run(() =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                // the tabs of modules are chosen with their own command and covered by their module's tests
                foreach (var tab in Enum.GetValues<RibbonTab>().Where(t => t != RibbonTab.Module))
                {
                    vm.SelectRibbonTabCommand.Execute(tab);
                    window.UpdateLayout();
                    var content = window.GetVisualDescendants().OfType<StackPanel>()
                        .Single(p => p.IsVisible && p.Orientation == Avalonia.Layout.Orientation.Horizontal && p.Parent is Panel { Parent: ScrollViewer });
                    Assert.True(content.DesiredSize.Width + 40 <= window.Width, $"{tab}: {content.DesiredSize.Width:F0} px bei {window.Width:F0} px Fensterbreite");
                }
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void AllStates_RenderWithoutErrors()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 5).Build(), "bericht.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                Assert.Equal($"PDF-Knead {AppVersion.Display}", window.Title);
                Shoot(window, "01-start-hell");

                // the tabs of modules are chosen with their own command and covered by their module's tests
                foreach (var tab in Enum.GetValues<RibbonTab>().Where(t => t != RibbonTab.Module))
                {
                    vm.SelectRibbonTabCommand.Execute(tab);
                    Shoot(window, $"02-ribbon-{tab}");
                }

                await vm.OpenAsync([pdf]);
                Assert.Single(vm.Documents);
                Assert.Equal("1.7", vm.SelectedDocument!.PdfVersion);
                Assert.Equal(5, vm.SelectedDocument.PageCount);
                Assert.Equal(ZoomMode.FitPage, vm.SelectedDocument.ZoomMode);   // a document opens at full-page zoom
                Assert.StartsWith("bericht.pdf – PDF-Knead", window.Title);
                Assert.Equal(Tr.T("Seite {0} von {1}", 1, 5), vm.PageStatus);
                vm.RibbonTab = RibbonTab.Start;
                WaitForTiles(window, vm.SelectedDocument);
                Shoot(window, "03-dokument");
                vm.SelectedDocument.CurrentPage = vm.SelectedDocument.Pages[1];   // Letter landscape, rotated
                Assert.Equal(Tr.T("Seite {0} von {1}", 2, 5), vm.PageStatus);
                WaitForTiles(window, vm.SelectedDocument);
                Shoot(window, "03b-seite-2");
                vm.FitPageCommand.Execute(null);
                WaitForTiles(window, vm.SelectedDocument);
                Shoot(window, "03c-ganze-seite");
                vm.ZoomInCommand.Execute(null);
                vm.ZoomInCommand.Execute(null);
                vm.ZoomInCommand.Execute(null);
                WaitForTiles(window, vm.SelectedDocument);
                Shoot(window, "03d-zoom");
                vm.RotateRightCommand.Execute(null);
                WaitForTiles(window, vm.SelectedDocument);
                Shoot(window, "03e-gedreht");
                vm.RotateLeftCommand.Execute(null);
                vm.FitPageCommand.Execute(null);
                vm.SetLayoutCommand.Execute(PageLayout.TwoPages);
                WaitForTiles(window, vm.SelectedDocument);
                Shoot(window, "03f-doppelseite");
                vm.SetLayoutCommand.Execute(PageLayout.Single);
                WaitForTiles(window, vm.SelectedDocument);
                Shoot(window, "03g-einzelseite");
                vm.SetLayoutCommand.Execute(PageLayout.Continuous);
                vm.SidebarTab = SidebarTab.Bookmarks;
                Shoot(window, "03h-lesezeichen-leer");
                vm.SidebarTab = SidebarTab.Pages;
                vm.FitWidthCommand.Execute(null);
                vm.ShowBackstageCommand.Execute(BackstagePage.Properties);
                Shoot(window, "04b-eigenschaften");

                vm.ShowBackstageCommand.Execute(BackstagePage.Open);
                Shoot(window, "04-datei-oeffnen");
                vm.ShowBackstageCommand.Execute(BackstagePage.Settings);
                Shoot(window, "05-einstellungen");
                vm.ShowBackstageCommand.Execute(BackstagePage.Info);
                Shoot(window, "05b-info");
                vm.CloseBackstageCommand.Execute(null);

                // Handwriting fonts in a window of their own: added ones first (removable), then those of the program
                var fonts = new SignatureFontsViewModel(null);
                fonts.Load();
                Assert.Equal(EmbeddedFonts.Handwriting.Count, fonts.Added.Count + fonts.BuiltIn.Count);
                Assert.All(fonts.Added, r => Assert.True(r.Removable));
                Assert.All(fonts.BuiltIn, r => Assert.False(r.Removable));
                var fontsWindow = new SignatureFontsWindow(fonts);
                fontsWindow.Show(window);
                Assert.NotNull(fonts.PickFontFile);
                Shoot(fontsWindow, "05c-schriften");
                var removable = fonts.Added.First();
                removable.AskCommand.Execute(null);
                Shoot(fontsWindow, "05d-schriften-rueckfrage");
                removable.KeepCommand.Execute(null);
                Assert.False(removable.Asking);
                fontsWindow.Close();

                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                Shoot(window, "06-dokument-dunkel");
                vm.CloseDocumentCommand.Execute(null);
                Shoot(window, "07-start-dunkel");

                Tr.Use(English.Language);
                vm.ShowBackstageCommand.Execute(BackstagePage.Settings);
                Shoot(window, "08-settings-english");
                vm.CloseBackstageCommand.Execute(null);
                Application.Current.RequestedThemeVariant = ThemeVariant.Light;
                vm.RibbonTab = RibbonTab.Secure;
                Shoot(window, "09-secure-english");
            }
            finally
            {
                Tr.Use(Language.German);
                UserSettings.ApplyTheme();   // restore the saved theme (ThemeVariant.Default would leave the scheme unresolved; see docs/FEHLERGEDAECHTNIS.md, No. 70)
                window.Close();
            }
        });
    }

    [Fact]
    public void Keys_ToggleSidebarAndRibbon_AndSettingsAreSaved()
    {
        UiSession.Run(() =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                Assert.True(vm.SidebarVisible);
                window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
                Assert.False(vm.SidebarVisible);
                Assert.Equal(0, window.FindControl<Grid>("BodyGrid")!.ColumnDefinitions[0].Width.Value);
                window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
                Assert.True(vm.SidebarVisible);

                window.KeyPress(Key.F1, RawInputModifiers.Control, PhysicalKey.F1, null);
                Assert.True(vm.RibbonCollapsed);
                Assert.False(vm.RibbonContentVisible);
                Assert.True(AppSettings.Load(UserSettings.File).RibbonCollapsed);

                // collapsed: a tab opens temporarily, clicking the same tab again closes it
                vm.SelectRibbonTabCommand.Execute(RibbonTab.Pages);
                Assert.True(vm.RibbonContentVisible);
                vm.SelectRibbonTabCommand.Execute(RibbonTab.Pages);
                Assert.False(vm.RibbonContentVisible);

                window.KeyPress(Key.F1, RawInputModifiers.Control, PhysicalKey.F1, null);
                Assert.False(vm.RibbonCollapsed);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Open_RejectsNonPdf_AndKeepsRecentList()
    {
        var pdf = TestFiles.MinimalPdf();
        var text = Path.Combine(Path.GetDirectoryName(pdf)!, "kein.pdf");
        File.WriteAllText(text, "Hallo");

        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([text, pdf, pdf]);

            Assert.Single(vm.Documents);                       // opening the same file twice opens it once
            Assert.Contains("kein.pdf", vm.Message);
            Assert.Equal(Path.GetFullPath(pdf), vm.RecentFiles[0].Path);

            vm.CloseDocumentCommand.Execute(vm.Documents[0]);
            Assert.Null(vm.SelectedDocument);
            Assert.True(vm.HasNoDocuments);
        });
    }

    [Fact]
    public void History_RemoveAndClear_LeaveFilesAlone_AndMissingFilesAreMarked()
    {
        var a = TestFiles.Write(TestPdf.Simple(1).Build(), "a.pdf");
        var b = TestFiles.Write(TestPdf.Simple(1).Build(), "b.pdf");
        var gone = TestFiles.Write(TestPdf.Simple(1).Build(), "weg.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            vm.ClearRecentCommand.Execute(null);
            await vm.OpenAsync([gone, a, b]);
            foreach (var d in vm.Documents.ToList()) vm.CloseDocumentCommand.Execute(d);
            File.Delete(gone);

            var fresh = new MainViewModel();   // the recent list is checked again at start-up
            Assert.Equal(["b.pdf", "a.pdf", "weg.pdf"], fresh.RecentFiles.Select(r => r.FileName));
            Assert.True(fresh.RecentFiles[2].Missing);

            // missing file: a message, and the entry stays (only the × removes it)
            await fresh.OpenRecentCommand.ExecuteAsync(fresh.RecentFiles[2]);
            Assert.Contains("weg.pdf", fresh.Message);
            Assert.Equal(3, fresh.RecentFiles.Count);

            fresh.RemoveRecentCommand.Execute(fresh.RecentFiles[0]);
            Assert.Equal(["a.pdf", "weg.pdf"], fresh.RecentFiles.Select(r => r.FileName));
            Assert.True(File.Exists(b));   // removed from the list only, not from the disk

            fresh.ClearRecentCommand.Execute(null);
            Assert.Empty(fresh.RecentFiles);
            Assert.True(File.Exists(a));
        });
    }

    [Fact]
    public void Open_Encrypted_AsksUntilThePasswordFits()
    {
        var pdf = TestFiles.Write(TestPdf.Simple().Build(cipher: Core.Pdf.Security.PdfCipher.Aes256, user: "geheim", owner: "chef"), "geschuetzt.pdf");
        UiSession.Run(async () =>
        {
            var asked = new List<bool>();
            var answers = new Queue<string?>(["falsch", "geheim"]);
            var vm = new MainViewModel
            {
                AskPassword = (_, wrong) =>
                {
                    asked.Add(wrong);
                    return Task.FromResult(answers.Dequeue());
                },
            };
            await vm.OpenAsync([pdf]);

            Assert.Equal([false, true], asked);   // first without, then after a wrong password
            Assert.Single(vm.Documents);
            Assert.Contains(vm.SelectedDocument!.Properties, r => r.Label == "Schutz");

            // cancel: no document, no error message
            var cancelled = new MainViewModel { AskPassword = (_, _) => Task.FromResult<string?>(null) };
            await cancelled.OpenAsync([pdf]);
            Assert.Empty(cancelled.Documents);
            Assert.False(cancelled.HasMessage);
        });
    }

    [Fact]
    public void Text_SearchSelectAndCopy()
    {
        // every page has „Seite n“ at the top (Helvetica 24 pt at x 72, baseline 760)
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 5).Build(), "suche.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            string? copied = null;
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.SetClipboardText = text =>
                {
                    copied = text;
                    return Task.CompletedTask;
                };
                vm.RibbonTab = RibbonTab.Start;
                WaitForTiles(window, doc);

                // search: Ctrl+F, all matches, F3 jumps to the next one
                window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, null);
                Assert.True(doc.IsSearchOpen);
                doc.SearchText = "seite";
                WaitUntil(window, () => !doc.IsSearching);
                Assert.Equal(5, doc.Matches.Count);
                Assert.Equal(0, doc.CurrentMatch);
                Assert.Equal(Tr.T("{0} von {1}", 1, 5), doc.SearchStatus);
                Shoot(window, "11-suche");
                window.KeyPress(Key.F3, RawInputModifiers.None, PhysicalKey.F3, null);
                Assert.Equal(1, doc.Current!.Value.Page);
                // next after the last match wraps to the first; previous from the first wraps to the last
                for (var i = 0; i < 3; i++) window.KeyPress(Key.F3, RawInputModifiers.None, PhysicalKey.F3, null);
                Assert.Equal(4, doc.CurrentMatch);
                window.KeyPress(Key.F3, RawInputModifiers.None, PhysicalKey.F3, null);
                Assert.Equal(0, doc.CurrentMatch);
                window.KeyPress(Key.F3, RawInputModifiers.Shift, PhysicalKey.F3, null);
                Assert.Equal(4, doc.CurrentMatch);
                // case: ignored without the check box, exact with it
                doc.MatchCase = true;
                WaitUntil(window, () => !doc.IsSearching);
                Assert.Empty(doc.Matches);
                doc.SearchText = "Seite";
                WaitUntil(window, () => !doc.IsSearching);
                Assert.Equal(5, doc.Matches.Count);
                doc.MatchCase = false;
                doc.SearchText = "gibt es nicht";
                WaitUntil(window, () => !doc.IsSearching);
                Assert.Equal(Tr.T("Keine Treffer"), doc.SearchStatus);
                doc.CloseSearch();
                Assert.Empty(doc.Matches);

                // selecting with the mouse: from the left half of the „S“ into the right half of the „1“
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                await doc.GetTextAsync(0);
                WaitForTiles(window, doc);
                var view = window.GetVisualDescendants().OfType<Controls.DocumentView>().Single();
                var from = view.TranslatePoint(view.PagePointToView(0, 73, 766), window)!.Value;
                var to = view.TranslatePoint(view.PagePointToView(0, 146, 766), window)!.Value;
                window.MouseDown(from, MouseButton.Left);
                window.MouseMove(to);
                window.MouseUp(to, MouseButton.Left);
                Assert.Equal(0, doc.SelectionPage);
                Assert.Equal("Seite 1", doc.SelectedText);
                Shoot(window, "12-auswahl");

                // copy with Ctrl+C
                Assert.True(vm.CopyTextCommand.CanExecute(null));
                window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
                WaitUntil(window, () => copied is not null);
                Assert.Equal("Seite 1", copied);

                // a click beside the text clears the selection; Ctrl+A selects the whole page
                var beside = view.TranslatePoint(view.PagePointToView(0, 450, 700), window)!.Value;
                window.MouseDown(beside, MouseButton.Left);
                window.MouseUp(beside, MouseButton.Left);
                Assert.False(doc.HasTextSelection);
                view.Focus();
                window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
                WaitUntil(window, () => doc.HasTextSelection);
                Assert.Contains("Seite 1", doc.SelectedText);

                // pan tool: dragging over text does not select
                vm.SetToolCommand.Execute(ViewTool.Pan);
                doc.ClearSelection();
                window.MouseDown(from, MouseButton.Left);
                window.MouseMove(to);
                window.MouseUp(to, MouseButton.Left);
                Assert.False(doc.HasTextSelection);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Waits until the condition holds (processing queued jobs, pausing briefly), for at most 10 seconds.</summary>
    private static void WaitUntil(Window window, Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            UiSession.Capture(window);
            Thread.Sleep(20);
        }
        Assert.True(condition(), "Zeitüberschreitung");
    }

    [Fact]
    public void PasswordWindow_Renders()
    {
        UiSession.Run(() =>
        {
            var window = new PasswordWindow("vertrag.pdf", wrongPassword: true);
            window.Show();
            UiSession.Capture(window);   // the first frame is drawn before the size adjustment (SizeToContent)
            Shoot(window, "10-passwort");
            window.Close();
        });
    }

    /// <summary>
    /// Waits until the overview tile of the current page exists and the renderer is idle (tiles are rendered on background
    /// threads): draws, processes queued jobs and pauses briefly, for at most 10 seconds.
    /// </summary>
    private static void WaitForTiles(Window window, DocumentViewModel doc)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        var renderer = doc.Renderer;
        var key = doc.CurrentPage?.Key ?? doc.Pages[0].Key;
        var overview = new Rendering.TileKey(key, renderer.OverviewLevel(key), 0, 0, doc.Rotation);
        while (DateTime.UtcNow < until)
        {
            UiSession.Capture(window);
            if (renderer.TryGetTile(overview) is not null && renderer.IsIdle) break;
            Thread.Sleep(30);
        }
        UiSession.Capture(window);
    }

    internal static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        Assert.True(frame.PixelSize.Width > 0);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}

[CollectionDefinition(nameof(UiCollection), DisableParallelization = true)]
public sealed class UiCollection;
