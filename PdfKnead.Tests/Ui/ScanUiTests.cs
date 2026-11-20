using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Scanning;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The window „Scannen“ (KONZEPT §5.25, step S1) with a scanner in software: settings from what the device offers (the feeder offers less
/// resolution than the glass), pages from the feeder as tiles, preview, turning, deleting, ordering, and taking over as a new document or
/// appended to the open one.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class ScanUiTests
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

    /// <summary>
    /// The scan window remembers the last device, source and resolution — and the settings are shared by all tests of the session: each test
    /// starts without them, otherwise the order of the tests decides what is selected (one test left the feeder selected, another expected
    /// the flatbed).
    /// </summary>
    private static void ForgetScanSettings() => Services.UserSettings.Update(s => s with { Scan = null, ScanProfileName = null });

    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    [Fact]
    public void Feeder_PagesAsTiles_TurnDeleteOrder_TakeOverAsNewDocument()
    {
        var backend = new FakeScanner();
        UiSession.Run(async () =>
        {
            ForgetScanSettings();
            var vm = new MainViewModel { ScanBackend = () => backend };
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                vm.ShowScan = async scan =>
                {
                    var dialog = new ScanWindow(scan) { Width = 1280, Height = 720 };
                    ScanResult? result = null;
                    scan.TakeOverRequested += r => result = r;
                    dialog.Show(window);
                    try
                    {
                        // the device is found and opened on its own; the flatbed offers up to 1200 dpi
                        WaitUntil(dialog, () => scan.SelectedResolution is not null && !scan.IsBusy);
                        Assert.Equal("Muster Scanner 3000", scan.SelectedDevice!.DisplayName);
                        Assert.Equal(ScanSourceKind.Flatbed, scan.SelectedSource!.Kind);
                        Assert.Equal(1200, scan.Resolutions[^1]);
                        scan.SelectedResolution = 600;

                        // the feeder offers only up to 300 dpi: the closest allowed value is taken
                        scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                        WaitUntil(dialog, () => scan.Resolutions.Count > 0 && scan.Resolutions[^1] == 300 && !scan.IsBusy);
                        Assert.Equal(300, scan.SelectedResolution);
                        Assert.False(scan.TakeOverNewCommand.CanExecute(null));

                        // while the second sheet is read: its progress between tiles and preview, the first page already a tile
                        backend.HoldSecondSheet = new TaskCompletionSource();
                        var scanning = scan.ScanCommand.ExecuteAsync(null);
                        WaitUntil(dialog, () => scan.ScanningPage == 2 && scan.Pages.Count == 1);
                        Assert.Equal(Tr.T("Blatt {0} wird gescannt … {1:0} %", 2, 50.0), scan.ProgressText);
                        Assert.Equal(Tr.T("Scanne Seite {0} … ({1} fertig)", 2, 1), scan.Status);
                        Assert.Contains(dialog.GetVisualDescendants().OfType<ProgressBar>(), b => b.IsEffectivelyVisible && Math.Abs(b.Value - 0.5) < 1e-9);
                        // while scanning, „Scannen“ has become „Scannen abbrechen“ at the same place
                        var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
                        Assert.Contains(buttons, x => x.Content as string == Tr.T("Scannen abbrechen") && x.IsEffectivelyVisible);
                        Assert.DoesNotContain(buttons, x => x.Content as string == Tr.T("Scannen") && x.IsEffectivelyVisible);
                        Shoot(dialog, "91-scannen-fortschritt");
                        backend.HoldSecondSheet.SetResult();
                        await scanning;
                        Assert.Equal(3, scan.Pages.Count);
                        Assert.Equal("", scan.ProgressText);
                        Assert.Equal(Tr.T("3 Seiten gescannt."), scan.Status);
                        Assert.Equal("Feeder", backend.LastSettings!.Source);
                        Assert.Equal(300, backend.LastSettings.Dpi);
                        Assert.Equal(210, backend.LastSettings.WidthMm);
                        WaitUntil(dialog, () => scan.Renderer.IsIdle);

                        // the last page read is shown; a click on a tile shows that page
                        Assert.Same(scan.ScanPages[2], scan.Current);
                        foreach (var p in scan.Pages) p.IsSelected = p.Index == 0;
                        scan.SelectionChanged();
                        Assert.Same(scan.ScanPages[0], scan.Current);
                        Assert.NotNull(scan.Preview);
                        Assert.Equal(Tr.T("Seite {0} von {1}", 1, 3), scan.CurrentText);

                        // turn the first page: width and height swap
                        var (w, h) = (scan.Current!.Image.Width, scan.Current.Image.Height);
                        await scan.RotateRightCommand.ExecuteAsync(null);
                        Assert.Equal((h, w), (scan.ScanPages[0].Image.Width, scan.ScanPages[0].Image.Height));
                        Assert.True(scan.Pages[0].IsSelected);

                        // the first page behind the last one (dragging in the tiles)
                        var first = scan.ScanPages[0];
                        scan.MoveSelected(3);
                        Assert.Same(first, scan.ScanPages[2]);

                        // delete the second page
                        foreach (var p in scan.Pages) p.IsSelected = p.Index == 1;
                        scan.SelectionChanged();
                        scan.DeleteCommand.Execute(null);
                        Assert.Equal(2, scan.Pages.Count);
                        Assert.Equal(1, backend.Marks(scan.ScanPages[0].Image));   // sheet 1 of the feeder (sheet 2 was deleted)
                        Assert.Equal(0, backend.Marks(ScanImageOps.Rotate(scan.ScanPages[1].Image, -1)));   // sheet 0, turned

                        // every button and toggle (also the headers of the groups) has a name for screen readers
                        foreach (var button in dialog.GetVisualDescendants().OfType<Button>()
                                     .Where(b => (b.GetType() == typeof(Button) || b.GetType() == typeof(ToggleButton)) && b.IsEffectivelyVisible))
                            Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(button)) && button.Content is not (string or TextBlock),
                                $"Knopf ohne Namen: {button.Content?.GetType().Name}");
                        // the fixed foot on the left stays visible in the smallest size (1280 × 720 with scaling of 14-inch laptops)
                        var scanButton = dialog.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == Tr.T("Scannen"));
                        Assert.True(scanButton.IsEffectivelyVisible && scanButton.Bounds.Bottom > 0);
                        Assert.True(scanButton.TranslatePoint(new Point(0, scanButton.Bounds.Height), dialog)!.Value.Y <= dialog.Bounds.Height);
                        // every tile is drawn, also the turned page (it has a new id, so its tile is drawn anew)
                        WaitUntil(dialog, () => scan.Pages.All(p =>
                            scan.Renderer.TryGetTile(new Rendering.TileKey(p.Key, scan.Renderer.OverviewLevel(p.Key), 0, 0, 0)) is not null));
                        Shoot(dialog, "90-scannen");

                        scan.RecognizeAfter = false;
                        await scan.TakeOverNewCommand.ExecuteAsync(null);
                        return result;
                    }
                    finally
                    {
                        dialog.Close();
                        await dialog.Cleanup;
                    }
                };
                await vm.ScanCommand.ExecuteAsync(null);
                var doc = vm.SelectedDocument!;
                Assert.True(doc.IsUntitled);
                Assert.Equal(Tr.T("Scan {0}", 1) + ".pdf", doc.FileName);
                Assert.Equal(2, doc.PageCount);
                Assert.False(backend.Open, "das Gerät ist nach dem Schließen frei");

                // saved: two image pages, as large as the scanned area (A4 at 300 dpi; the turned page lies across)
                var saved = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-scan-").FullName, "scan-neu.pdf");
                Assert.True(await vm.SaveDocumentAsync(doc, saved, doc.SaveVersion));
                using var file = PdfDocument.Open(saved);
                Assert.Equal(2, file.PageCount);
                Assert.Equal(595, file.Pages[0].MediaBox.Width, 0);
                Assert.Equal(842, file.Pages[0].MediaBox.Height, 0);
                Assert.Equal(842, file.Pages[1].MediaBox.Width, 0);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void AppendToOpenDocument_OneUndoStep_ErrorKeepsPagesAlreadyRead()
    {
        var file = TestFiles.Write(TestPdf.Simple(pages: 2).Build(), "brief.pdf");
        var backend = new FakeScanner { JamAfter = 2 };
        UiSession.Run(async () =>
        {
            ForgetScanSettings();
            var vm = new MainViewModel { ScanBackend = () => backend };
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                vm.ShowScan = async scan =>
                {
                    var dialog = new ScanWindow(scan);
                    ScanResult? result = null;
                    scan.TakeOverRequested += r => result = r;
                    dialog.Show(window);
                    try
                    {
                        WaitUntil(dialog, () => scan.SelectedResolution is not null && !scan.IsBusy);
                        Assert.True(scan.CanAppend);
                        scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                        WaitUntil(dialog, () => scan.Resolutions[^1] == 300 && !scan.IsBusy);
                        // a paper jam after two sheets: both pages stay, the message says so
                        await scan.ScanCommand.ExecuteAsync(null);
                        Assert.Equal(2, scan.Pages.Count);
                        Assert.True(scan.StatusIsError);
                        Assert.Contains(Tr.T("Papierstau im Scanner."), scan.Status);
                        await scan.TakeOverAppendCommand.ExecuteAsync(null);
                        return result;
                    }
                    finally
                    {
                        dialog.Close();
                        await dialog.Cleanup;
                    }
                };
                await vm.ScanCommand.ExecuteAsync(null);
                Assert.Same(doc, vm.SelectedDocument);
                Assert.Equal(4, doc.PageCount);
                doc.History.Undo();
                Assert.Equal(2, doc.PageCount);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void Feeder_CancelledWithTheButton_PagesAlreadyReadStay_EscDoesNothing()
    {
        var backend = new FakeScanner { HoldSecondSheet = new TaskCompletionSource() };
        UiSession.Run(async () =>
        {
            ForgetScanSettings();
            var scan = new ScanViewModel(backend, canAppend: false);
            var dialog = new ScanWindow(scan);
            dialog.Show();
            try
            {
                WaitUntil(dialog, () => scan.SelectedResolution is not null && !scan.IsBusy);
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                WaitUntil(dialog, () => scan.Resolutions[^1] == 300 && !scan.IsBusy);
                var scanning = scan.ScanCommand.ExecuteAsync(null);
                WaitUntil(dialog, () => scan.ScanningPage == 2);
                // Esc does not stop the scanner (pressed by mistake it would end a feeder job)
                dialog.Focus();
                dialog.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
                Assert.True(scan.IsScanning);
                // the button that took the place of „Scannen“ does
                var stop = dialog.GetVisualDescendants().OfType<Button>().Single(x => x.Content as string == Tr.T("Scannen abbrechen"));
                Assert.True(stop.IsEffectivelyVisible);
                stop.Command!.Execute(null);
                backend.HoldSecondSheet!.SetResult();
                await scanning;
                Assert.False(scan.IsScanning);
                Assert.Single(scan.Pages);
                Assert.Equal(Tr.T("Abgebrochen — {0} Seiten behalten.", 1), scan.Status);
            }
            finally
            {
                dialog.Close();
                await dialog.Cleanup;
            }
        });
    }

    [Fact]
    public void NetworkScanner_NoProgressWhileReading_EstimatedFromTheSheetBefore()
    {
        var backend = new FakeScanner { WholeSheetsAfter = TimeSpan.FromMilliseconds(400), HoldSecondSheet = new TaskCompletionSource() };
        UiSession.Run(async () =>
        {
            ForgetScanSettings();
            var scan = new ScanViewModel(backend, canAppend: false);
            var dialog = new ScanWindow(scan);
            dialog.Show();
            try
            {
                WaitUntil(dialog, () => scan.SelectedResolution is not null && !scan.IsBusy);
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                WaitUntil(dialog, () => scan.Resolutions[^1] == 300 && !scan.IsBusy);
                var scanning = scan.ScanCommand.ExecuteAsync(null);
                // the first sheet: nothing is known, the bar only shows that something happens
                WaitUntil(dialog, () => scan.ScanningPage == 1);
                Assert.True(scan.ProgressUnknown);
                Assert.StartsWith(Tr.T("Blatt {0} wird gescannt … {1} s", 1, 0).Split(" 0 s")[0], scan.ProgressText);   // the seconds go on
                // the second: estimated from the first (about 400 ms) and moving on; it stops below 100 % until the sheet arrives
                WaitUntil(dialog, () => scan.ScanningPage == 2 && scan.ProgressEstimated && scan.Progress > 0.5);
                Assert.StartsWith(Tr.T("Blatt {0} wird gescannt … etwa {1:0} %", 2, 0).Split("0 %")[0], scan.ProgressText);
                WaitUntil(dialog, () => scan.Progress >= 0.95);
                Thread.Sleep(300);
                UiSession.Capture(dialog);
                Assert.True(scan.Progress <= 0.95 + 1e-9);
                backend.HoldSecondSheet!.SetResult();
                await scanning;
                Assert.Equal(3, scan.Pages.Count);
                Assert.False(scan.ProgressEstimated);
            }
            finally
            {
                dialog.Close();
                await dialog.Cleanup;
            }
        });
    }

    [Fact]
    public void InterfaceMissing_SaysWhatToInstall_NoScanning()
    {
        UiSession.Run(() =>
        {
            ForgetScanSettings();
            var scan = new ScanViewModel(new UnavailableScanBackend("SANE", "Bitte SANE installieren."), canAppend: false);
            Services.UserSettings.Update(s => s with { ScanTilesShare = 0.25 });
            var dialog = new ScanWindow(scan) { Width = 1280, Height = 720 };
            dialog.Show();
            try
            {
                // the divider between tiles and preview starts where it was left, and its position is remembered on closing
                var divider = dialog.GetVisualDescendants().OfType<GridSplitter>().Single(g => g.Name == "Divider");
                var rows = ((Grid)divider.Parent!).RowDefinitions;
                Assert.Equal(0.25, rows[1].Height.Value, 3);
                Assert.Equal(0.75, rows[3].Height.Value, 3);
                UiSession.Capture(dialog);
                Assert.Equal(0.25, rows[1].ActualHeight / (rows[1].ActualHeight + rows[3].ActualHeight), 1);
                rows[1].Height = new GridLength(3, GridUnitType.Star);   // as after dragging the divider down
                rows[3].Height = new GridLength(1, GridUnitType.Star);
                UiSession.Capture(dialog);
                Assert.False(scan.IsAvailable);
                Assert.Equal("Bitte SANE installieren.", scan.InstallHint);
                Assert.False(scan.ScanCommand.CanExecute(null));
                Assert.False(scan.SearchCommand.CanExecute(null));
                UiSession.Capture(dialog);
                Assert.Contains(dialog.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text == "Bitte SANE installieren." && t.IsEffectivelyVisible);
            }
            finally
            {
                dialog.Close();
            }
            // as dragged, as far as the preview's minimum height allows
            Assert.InRange(Services.UserSettings.Current.ScanTilesShare, 0.65, 0.76);
        });
    }

    /// <summary>
    /// A scanner in software: flatbed up to 1200 dpi, feeder up to 300 dpi with three sheets; each sheet carries its number as dark squares in
    /// the top left corner (so turned and moved pages can be told apart).
    /// </summary>
    private sealed class FakeScanner : IScanBackend
    {
        public ScanSettings? LastSettings;
        public bool Open;
        public int JamAfter = -1;
        /// <summary>Set: the second sheet stops halfway until it is released (to look at the progress).</summary>
        public TaskCompletionSource? HoldSecondSheet;
        /// <summary>Like a network scanner: no progress while a sheet is read, the sheet comes as a whole after this time.</summary>
        public TimeSpan? WholeSheetsAfter;

        private static readonly ScanDevice Device = new("fake:1", "Muster", "Scanner 3000", "flatbed scanner");

        public string Name => "Test";

        public bool IsAvailable(out string? hint)
        {
            hint = null;
            return true;
        }

        public Task<IReadOnlyList<ScanDevice>> FindDevicesAsync(CancellationToken cancel = default) => Task.FromResult<IReadOnlyList<ScanDevice>>([Device]);

        public Task<IScanSession> OpenAsync(ScanDevice device, CancellationToken cancel = default)
        {
            Open = true;
            return Task.FromResult<IScanSession>(new Session(this));
        }

        /// <summary>The number of marks on a page in the top left corner.</summary>
        public int Marks(ScannedImage page)
        {
            var count = 0;
            for (var k = 0; k < 8; k++)
            {
                var (x, y) = (10 + k * 20, 10);
                if (page.Pixels[((long)y * page.Width + x) * page.Channels] < 100) count++;
            }
            return count;
        }

        private sealed class Session(FakeScanner owner) : IScanSession
        {
            private static readonly ScanSource Flatbed = new("Flatbed", ScanSourceKind.Flatbed);
            private static readonly ScanSource Feeder = new("Feeder", ScanSourceKind.Feeder);
            private ScanSource _source = Flatbed;

            public ScanDevice Device => FakeScanner.Device;

            public ScanCapabilities Capabilities => Caps();

            private ScanCapabilities Caps() => new([Flatbed, Feeder], _source,
                _source == Flatbed ? [75, 150, 300, 600, 1200] : [75, 150, 200, 300],
                [ScanColorMode.Color, ScanColorMode.Gray, ScanColorMode.BlackWhite], 216, _source == Flatbed ? 297 : 356);

            public Task<ScanCapabilities> SelectSourceAsync(string source, CancellationToken cancel = default)
            {
                _source = source == Feeder.Name ? Feeder : Flatbed;
                return Task.FromResult(Caps());
            }

            public async IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, IProgress<ScanProgress>? progress = null,
                [EnumeratorCancellation] CancellationToken cancel = default)
            {
                owner.LastSettings = settings;
                var sheets = _source == Feeder ? 3 : 1;
                for (var sheet = 0; sheet < sheets; sheet++)
                {
                    if (sheet == owner.JamAfter) throw new ScanException(Tr.T("Papierstau im Scanner."), ScanErrorKind.Jammed);
                    await Task.Yield();
                    if (owner.WholeSheetsAfter is { } after)
                    {
                        progress?.Report(new ScanProgress(sheet + 1, -1));
                        if (sheet == 1 && owner.HoldSecondSheet is { } wait) await wait.Task;
                        else await Task.Delay(after, cancel);
                        yield return Sheet(settings, sheet);
                        continue;
                    }
                    progress?.Report(new ScanProgress(sheet + 1, 0.5));
                    if (sheet == 1 && owner.HoldSecondSheet is { } hold) await hold.Task;
                    cancel.ThrowIfCancellationRequested();
                    progress?.Report(new ScanProgress(sheet + 1, 1));
                    yield return Sheet(settings, sheet);
                }
            }

            private static ScannedImage Sheet(ScanSettings settings, int number)
            {
                // A4 at the chosen resolution, but kept small for the test by scanning at a tenth and stating the real resolution
                var w = (int)Math.Round(settings.WidthMm!.Value / 25.4 * settings.Dpi / 10);
                var h = (int)Math.Round(settings.HeightMm!.Value / 25.4 * settings.Dpi / 10);
                var pixels = new byte[w * h * 3];
                Array.Fill(pixels, (byte)240);
                for (var k = 0; k < number; k++)
                    for (var y = 5; y < 15; y++)
                        for (var x = 5 + k * 20; x < 15 + k * 20; x++)
                            pixels[(y * w + x) * 3] = pixels[(y * w + x) * 3 + 1] = pixels[(y * w + x) * 3 + 2] = 20;
                return new ScannedImage(w, h, 3, settings.Dpi / 10, pixels);
            }

            public ValueTask DisposeAsync()
            {
                owner.Open = false;
                return ValueTask.CompletedTask;
            }
        }
    }
}
