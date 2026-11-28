using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Scanning;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The scan window with profiles, both sides by turning the stack and leaving out blank pages (KONZEPT §5.25, step S2), with a feeder in
/// software whose sheets carry their number as marks; and the page „Scanprofile“ of the file area.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class ScanProfileUiTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pdfknead-scanprofile-ui-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

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

    /// <summary>Opens the scan window with the feeder and waits until the device is ready.</summary>
    private (ScanWindow Window, ScanViewModel Scan) Open(Feeder feeder, ScanProfileStore store)
    {
        Services.UserSettings.Update(s => s with { Scan = null, ScanProfileName = null });
        var scan = new ScanViewModel(feeder, false, store);
        var window = new ScanWindow(scan) { Width = 1280, Height = 720 };
        window.Show();
        WaitUntil(window, () => scan.SelectedResolution is not null && !scan.IsBusy);
        return (window, scan);
    }

    private static async Task Close(ScanWindow window)
    {
        window.Close();
        await window.Cleanup;
    }

    /// <summary>The filters of the images on a page (also inside forms).</summary>
    private static List<string> ImageFilters(Core.Pdf.Document.PdfDocument doc, int page)
    {
        var found = new List<string>();
        void Walk(Core.Pdf.Objects.PdfDictionary? resources, int depth)
        {
            if (resources?.GetDictionary(Core.Pdf.Objects.PdfName.Get("XObject")) is not { } xobjects || depth > 4) return;
            foreach (var key in xobjects.Keys.ToList())
            {
                if (xobjects.Get(key) is not Core.Pdf.Objects.PdfStream stream) continue;
                if (stream.Dictionary.GetName(Core.Pdf.Objects.PdfName.Subtype)?.Value == "Image")
                    found.Add(stream.Dictionary.GetName(Core.Pdf.Objects.PdfName.Filter)?.Value ?? "");
                else Walk(stream.Dictionary.GetDictionary(Core.Pdf.Objects.PdfName.Resources), depth + 1);
            }
        }
        Walk(doc.Pages[page].Resources, 0);
        return found;
    }

    private static List<int> Numbers(ScanViewModel scan) => [.. scan.ScanPages.Select(p => Feeder.Number(p.Image))];

    [Fact]
    public void Profiles_SaveReplaceChooseAdjust()
    {
        var store = new ScanProfileStore(_folder);
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(new Feeder(), store);
            try
            {
                // settings: feeder, 200 dpi, grey, both sides, without blank pages
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                WaitUntil(window, () => !scan.IsBusy && scan.Resolutions[^1] == 300);
                scan.SelectedResolution = 200;
                scan.SelectedColorMode = scan.ColorModes.First(c => c.Mode == ScanColorMode.Gray);
                scan.ManualDuplex = true;
                scan.SkipBlank = true;
                scan.BlankLimit = 0.4;

                // „Profil speichern“ opens the box for the name, focused
                scan.SaveProfileCommand.Execute(null);
                WaitUntil(window, () => window.FindControl<TextBox>("ProfileName")!.IsFocused);
                Assert.True(scan.IsNamingProfile);
                Assert.False(scan.ConfirmProfileCommand.CanExecute(null));   // no name yet
                scan.NewProfileName = "  Briefe  ";
                scan.ConfirmProfileCommand.Execute(null);
                Assert.False(scan.IsNamingProfile);
                Assert.Equal(Tr.T("Profil „{0}“ gespeichert.", "Briefe"), scan.Status);
                var saved = Assert.Single(store.Load());
                Assert.Equal(("Briefe", "Feeder", 200, ScanColorMode.Gray, true, true, 0.4),
                    (saved.Name, saved.Source, saved.Dpi, saved.Mode, saved.ManualDuplex, saved.SkipBlank, saved.BlankLimit));
                Assert.Equal("Muster Einzug 1", saved.DeviceName);
                Assert.Same(scan.Profiles[0], scan.SelectedProfile);
                Assert.Equal("Briefe", Services.UserSettings.Current.ScanProfileName);

                // the same name again: replaced only on the second press
                scan.SelectedResolution = 300;
                scan.SaveProfileCommand.Execute(null);
                Assert.Equal("Briefe", scan.NewProfileName);   // the chosen profile's name to begin with
                scan.ConfirmProfileCommand.Execute(null);
                Assert.True(scan.OverwritePending);
                Assert.Equal(Tr.T("Ersetzen"), scan.ConfirmProfileText);
                Assert.Equal(200, store.Load()[0].Dpi);
                scan.ConfirmProfileCommand.Execute(null);
                Assert.Equal(300, store.Load()[0].Dpi);
                Assert.False(scan.OverwritePending);

                // another profile: flatbed in colour at 1200 dpi — choosing it switches source, resolution and colour
                store.Save(new ScanProfile("Fotos", Feeder.DeviceId, "Muster Einzug 1", "Flatbed", 1200, ScanColorMode.Color));
                // and one for the feeder at 1200 dpi, which the feeder does not offer
                store.Save(new ScanProfile("Zu fein", Feeder.DeviceId, "Muster Einzug 1", "Feeder", 1200, ScanColorMode.Gray));
                scan.SaveProfileCommand.Execute(null);
                scan.CancelProfileCommand.Execute(null);   // closing the box without saving changes nothing
                Assert.Single(store.Load(), p => p.Name == "Briefe");

                var reload = new ScanViewModel(new Feeder(), false, store);   // a new window reads the profiles again
                Assert.Equal(["Briefe", "Fotos", "Zu fein"], reload.Profiles.Select(p => p.Name));
                Assert.Equal("Briefe", reload.SelectedProfile?.Name);   // the profile last chosen
                Assert.True(reload.ManualDuplex);
                await reload.ReleaseAsync();

                scan.Profiles = store.Load();
                scan.SelectedProfile = scan.Profiles.First(p => p.Name == "Fotos");
                WaitUntil(window, () => !scan.IsBusy && scan.SelectedSource?.Kind == ScanSourceKind.Flatbed && scan.SelectedResolution == 1200);
                Assert.Equal(ScanColorMode.Color, scan.SelectedColorMode!.Mode);
                Assert.False(scan.ManualDuplex);
                Assert.False(scan.SkipBlank);

                scan.SelectedProfile = scan.Profiles.First(p => p.Name == "Zu fein");
                WaitUntil(window, () => !scan.IsBusy && scan.SelectedSource?.Kind == ScanSourceKind.Feeder);
                Assert.Equal(300, scan.SelectedResolution);
                Assert.Contains(Tr.T("{0} dpi bietet diese Quelle nicht an, {1} dpi gewählt.", 1200, 300), scan.Status);

                // a profile of a scanner that is not there: the rest applies, the device stays
                store.Save(new ScanProfile("Anderswo", "other:9", "Anderer Scanner", "Flatbed", 150, ScanColorMode.BlackWhite));
                var others = new ScanViewModel(new Feeder(), false, store);
                await others.ReleaseAsync();
                scan.Profiles = store.Load();
                scan.SelectedProfile = scan.Profiles.First(p => p.Name == "Anderswo");
                WaitUntil(window, () => !scan.IsBusy && scan.SelectedResolution == 150);
                Assert.Equal(Feeder.DeviceId, scan.SelectedDevice!.Id);
                Assert.Equal(ScanSourceKind.Flatbed, scan.SelectedSource!.Kind);
                Assert.Equal(ScanColorMode.BlackWhite, scan.SelectedColorMode!.Mode);
                Assert.Contains(Tr.T("Der Scanner des Profils ist nicht da ({0}) — die übrigen Einstellungen gelten.", "Anderer Scanner"), scan.Status);
                Assert.True(scan.StatusIsError);
            }
            finally
            {
                await Close(window);
            }
        });
    }

    [Fact]
    public void BothSides_FrontsThenBacks_Paired_BlankBackLeftOut()
    {
        var store = new ScanProfileStore(_folder);
        // fronts 1, 2, 3; the backs come in reverse: back of sheet 3 (6), of sheet 2 (blank), of sheet 1 (4)
        var feeder = new Feeder([[1, 2, 3], [6, null, 4]]);
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(feeder, store);
            try
            {
                Assert.False(scan.ManualDuplexPossible);   // the flatbed reads one page at a time
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                WaitUntil(window, () => !scan.IsBusy && scan.ManualDuplexPossible);
                scan.ManualDuplex = true;
                scan.SkipBlank = true;

                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal([1, 2, 3], Numbers(scan));
                Assert.True(scan.AwaitingBacks);
                Assert.Equal(Tr.T("Rückseiten"), scan.ScanButtonText);
                Assert.Equal(Tr.T("Stapel {0}: {1} Vorderseiten gescannt. Jetzt die Rückseiten: den Stapel aus der Ablage nehmen, ohne die Reihenfolge zu ändern, wie eine Buchseite umdrehen und so in den Einzug legen wie vorher — Oberkante voran. Dann „Rückseiten scannen“ drücken.", 1, 3), scan.Status);
                WaitUntil(window, () => window.GetVisualDescendants().OfType<Button>()
                    .Any(b => b.Content as string == Tr.T("Ohne Rückseiten") && b.IsEffectivelyVisible));
                WaitUntil(window, () => scan.Renderer.IsIdle);
                if (UiSession.ScreenshotFolder() is { } folder)
                    UiSession.Capture(window).Save(Path.Combine(folder, "92-scannen-rueckseiten.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal([1, 4, 2, 3, 6], Numbers(scan));
                // the sides of a sheet know each other (the blank back of sheet 2 left out: its front has no other side)
                Assert.Same(scan.ScanPages[1], scan.ScanPages[0].Partner);
                Assert.Same(scan.ScanPages[0], scan.ScanPages[1].Partner);
                Assert.Null(scan.ScanPages[2].Partner);
                Assert.Same(scan.ScanPages[4], scan.ScanPages[3].Partner);
                Assert.False(scan.AwaitingBacks);
                Assert.Equal(Tr.T("Scannen"), scan.ScanButtonText);
                Assert.Equal(Tr.T("Stapel {0}: {1} Blätter beidseitig gescannt.", 1, 3) + " " + Tr.T("1 leere Seite weggelassen."), scan.Status);

                // a third run starts with fronts again; „Ohne Rückseiten“ keeps them as they are
                feeder.Runs.Enqueue([7]);
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.True(scan.AwaitingBacks);
                scan.EndDuplexCommand.Execute(null);
                Assert.False(scan.AwaitingBacks);
                Assert.Equal([1, 4, 2, 3, 6, 7], Numbers(scan));
            }
            finally
            {
                await Close(window);
            }
        });
    }

    [Fact]
    public void BothSides_PositionCorrection_TurnAndSwapBacks_RememberedInTheProfile()
    {
        // a feeder that lays the sheets face up: turned over, the backs come in the order of the sheets (4 5 6 for the fronts 1 2 3) — paired
        // the usual way round at first; the bar „Lage-Korrektur manueller Duplex-Scan“ puts them right, turns them, and the profile keeps it
        var store = new ScanProfileStore(_folder);
        store.Save(new ScanProfile("Wenden", Feeder.DeviceId, "Muster Einzug 1", "Feeder", 150, ScanColorMode.Gray, "A4", ManualDuplex: true));
        var feeder = new Feeder([[1, 2, 3], [4, 5, 6], [1, 2], [3, 4]]);
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(feeder, store);
            try
            {
                scan.SelectedProfile = scan.Profiles.First(p => p.Name == "Wenden");
                WaitUntil(window, () => !scan.IsBusy && scan.ManualDuplexPossible && scan.ManualDuplex);
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.False(scan.CanCorrectDuplex);   // not before the backs are in
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal([1, 6, 2, 5, 3, 4], Numbers(scan));
                Assert.True(scan.CanCorrectDuplex);
                WaitUntil(window, () => window.FindControl<Border>("DuplexCorrection")?.IsEffectivelyVisible == true);
                WaitUntil(window, () => scan.Renderer.IsIdle);
                if (UiSession.ScreenshotFolder() is { } folder)
                    UiSession.Capture(window).Save(Path.Combine(folder, "93-scannen-lage-korrektur.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

                // the other way round: every back with its own front, the sides of each sheet linked anew (their tiles computed anew)
                var paired = scan.ScanPages.ToList();
                var tilesBefore = paired.Select(p => p.Item).ToList();
                scan.SwapBacksCommand.Execute(null);
                Assert.Equal([1, 4, 2, 5, 3, 6], Numbers(scan));
                WaitUntil(window, () => paired.Select((p, k) => !ReferenceEquals(p.Item, tilesBefore[k])).All(x => x));
                Assert.Same(scan.ScanPages[1], scan.ScanPages[0].Partner);
                Assert.Same(scan.ScanPages[5], scan.ScanPages[4].Partner);
                Assert.True(store.Load().Single(p => p.Name == "Wenden").BacksInFeedOrder);
                Assert.True(Services.UserSettings.Current.Scan?.BacksInFeedOrder);

                // turned by half a turn: the backs upside down, the fronts as they were; once more turns them back. The fronts get new tiles
                // too — their show-through comes from the turned backs (Fehlergedächtnis 858)
                var frontTiles = new[] { 0, 2, 4 }.Select(i => scan.ScanPages[i].Item).ToList();
                await scan.TurnBacksCommand.ExecuteAsync(null);
                WaitUntil(window, () => new[] { 0, 2, 4 }.Select((p, k) => !ReferenceEquals(scan.ScanPages[p].Item, frontTiles[k])).All(x => x));
                Assert.Equal([1, 0, 2, 0, 3, 0], Numbers(scan));
                Assert.Equal([4, 5, 6], new[] { 1, 3, 5 }.Select(i => Feeder.NumberUpsideDown(scan.ScanPages[i].Image)));
                Assert.Equal(Tr.T("Rückseiten um 180° drehen"), scan.TurnBacksText);
                Assert.True(store.Load().Single(p => p.Name == "Wenden").BacksTurned);

                // the next scan by turning: the bar goes, both corrections come by themselves (backs in the order of the sheets, turned)
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.False(scan.CanCorrectDuplex);
                await scan.ScanCommand.ExecuteAsync(null);
                var last = scan.ScanPages.Skip(6).ToList();
                Assert.Equal([1, 0, 2, 0], last.Select(p => Feeder.Number(p.Image)));
                Assert.Equal([3, 4], new[] { 1, 3 }.Select(i => Feeder.NumberUpsideDown(last[i].Image)));
                Assert.True(scan.CanCorrectDuplex);
                scan.HideDuplexCorrectionCommand.Execute(null);
                Assert.False(scan.CanCorrectDuplex);
            }
            finally
            {
                await Close(window);
            }
        });
    }

    /// <summary>The numbers of the pages, an upside-down page with „↓“ (as the correction leaves it).</summary>
    private static List<string> Lying(ScanViewModel scan) =>
        [.. scan.ScanPages.Select(p => Feeder.Number(p.Image) is var n and > 0 ? n.ToString() : Feeder.NumberUpsideDown(p.Image) + "↓")];

    private static List<string?> Headers(ScanViewModel scan) => [.. scan.Pages.Select(p => p.GroupHeader)];

    [Fact]
    public void SeveralStacks_EachAfterTheSelectedStack_Grouped_FlatbedPageJoins()
    {
        // two stacks by turning (2 sheets each); between them page 1 is clicked to look at it: the second stack still comes after the first
        // (Fehlergedächtnis 859: it went right behind page 1, into the first sheet)
        var store = new ScanProfileStore(_folder);
        var feeder = new Feeder([[1, 3], [4, 2], [5, 7], [8, 6]]);
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(feeder, store);
            try
            {
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                WaitUntil(window, () => !scan.IsBusy && scan.ManualDuplexPossible);
                scan.ManualDuplex = true;
                await scan.ScanCommand.ExecuteAsync(null);
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal([1, 2, 3, 4], Numbers(scan));
                Assert.All(Headers(scan), Assert.Null);   // one stack: no groups

                foreach (var tile in scan.Pages) tile.IsSelected = tile.Index == 0;
                scan.SelectionChanged();
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal(Tr.T("Stapel {0}: {1} Vorderseiten gescannt. Jetzt die Rückseiten: den Stapel aus der Ablage nehmen, ohne die Reihenfolge zu ändern, wie eine Buchseite umdrehen und so in den Einzug legen wie vorher — Oberkante voran. Dann „Rückseiten scannen“ drücken.", 2, 2), scan.BacksText);
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], Numbers(scan));
                Assert.Equal([Tr.T("Stapel {0} · beidseitig · {1} Seiten", 1, 4), null, null, null, Tr.T("Stapel {0} · beidseitig · {1} Seiten", 2, 4), null, null, null],
                    Headers(scan));
                Assert.Equal(Tr.T("Stapel {0}: {1} Blätter beidseitig gescannt.", 2, 2), scan.Status);

                // the tiles: the second stack starts a row of its own, below a band with its hint
                var grid = window.FindControl<PageGridView>("GridView")!.Grid;
                WaitUntil(window, () => grid.Bounds.Width > 0);
                Assert.Equal(grid.CellRect(0).X, grid.CellRect(4).X);
                Assert.True(grid.CellRect(4).Top >= grid.CellRect(3).Bottom + 20);
                Assert.Equal(-1, grid.IndexAt(new Avalonia.Point(grid.CellRect(4).Center.X, grid.CellRect(4).Top - 10)));   // the band is no tile
                WaitUntil(window, () => scan.Renderer.IsIdle);
                if (UiSession.ScreenshotFolder() is { } folder)
                    UiSession.Capture(window).Save(Path.Combine(folder, "94-scannen-stapel.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

                // a page from the flatbed after the selected page 1 joins its stack — after the back of the sheet, never between its sides
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Flatbed);
                WaitUntil(window, () => !scan.IsBusy && scan.SelectedSource?.Kind == ScanSourceKind.Flatbed);
                foreach (var tile in scan.Pages) tile.IsSelected = tile.Index == 0;
                scan.SelectionChanged();
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal([1, 2, 1, 3, 4, 5, 6, 7, 8], Numbers(scan));
                Assert.Equal(Tr.T("Stapel {0} · beidseitig · {1} Seiten", 1, 5), scan.Pages[0].GroupHeader);
                Assert.Equal(Tr.T("Stapel {0} · beidseitig · {1} Seiten", 2, 4), scan.Pages[5].GroupHeader);

                // reordering within a stack keeps the groups; a page moved into another stack mixes them: no groups, until it is back
                foreach (var tile in scan.Pages) tile.IsSelected = tile.Index == 6;
                scan.MoveSelected(5);
                Assert.Equal([1, 2, 1, 3, 4, 6, 5, 7, 8], Numbers(scan));
                Assert.NotNull(scan.Pages[5].GroupHeader);
                foreach (var tile in scan.Pages) tile.IsSelected = tile.Index == 0;
                scan.MoveSelected(9);
                Assert.All(Headers(scan), Assert.Null);
                foreach (var tile in scan.Pages) tile.IsSelected = tile.Index == 8;
                scan.MoveSelected(0);
                Assert.Equal([Tr.T("Stapel {0} · beidseitig · {1} Seiten", 1, 5), null, null, null, null, Tr.T("Stapel {0} · beidseitig · {1} Seiten", 2, 4), null, null, null],
                    Headers(scan));
            }
            finally
            {
                await Close(window);
            }
        });
    }

    [Fact]
    public void SeveralStacks_TurnedBacks_BlankLastBack_FinishedStackNeverTurnsAgain()
    {
        // the back of the last sheet of the first stack is blank (left out) and the backs were turned: the second stack still comes after the
        // whole first one (Fehlergedächtnis 859: it went in before the last front); the turning of the first stays as it is
        var store = new ScanProfileStore(_folder);
        var feeder = new Feeder([[1, 3], [null, 2], [5, 7], [8, 6]]);
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(feeder, store);
            try
            {
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                WaitUntil(window, () => !scan.IsBusy && scan.ManualDuplexPossible);
                scan.ManualDuplex = true;
                scan.SkipBlank = true;
                await scan.ScanCommand.ExecuteAsync(null);
                await scan.ScanCommand.ExecuteAsync(null);
                await scan.TurnBacksCommand.ExecuteAsync(null);
                Assert.Equal(["1", "2↓", "3"], Lying(scan));

                // the next stack: the bar goes, the first stack is done with; its backs come turned as remembered
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.False(scan.CanCorrectDuplex);
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal(["1", "2↓", "3", "5", "6↓", "7", "8↓"], Lying(scan));
                Assert.Equal(Tr.T("Rückseiten um 180° drehen"), scan.TurnBacksText);

                // correcting the second stack turns only its pages
                await scan.TurnBacksCommand.ExecuteAsync(null);
                Assert.Equal(["1", "2↓", "3", "5", "6", "7", "8"], Lying(scan));
                await scan.TurnFrontsCommand.ExecuteAsync(null);
                Assert.Equal(["1", "2↓", "3", "5↓", "6", "7↓", "8"], Lying(scan));
                await scan.TurnFrontsCommand.ExecuteAsync(null);
                Assert.Equal(["1", "2↓", "3", "5", "6", "7", "8"], Lying(scan));
            }
            finally
            {
                await Close(window);
            }
        });
    }

    [Fact]
    public void BothSides_StackLaidUpsideDown_FrontsAndBacksTurned_ProfileUnchanged()
    {
        // the stack was laid upside down: fronts and backs come upside down; turning both puts it right and leaves the profile as it was
        // (remembered is only how the backs lie against the fronts), so the next stack, laid right, comes right
        var store = new ScanProfileStore(_folder);
        store.Save(new ScanProfile("Wenden", Feeder.DeviceId, "Muster Einzug 1", "Feeder", 150, ScanColorMode.Gray, "A4", ManualDuplex: true));
        var feeder = new Feeder([[5, 7], [8, 6]]);
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(feeder, store);
            try
            {
                scan.SelectedProfile = scan.Profiles.First(p => p.Name == "Wenden");
                WaitUntil(window, () => !scan.IsBusy && scan.ManualDuplexPossible && scan.ManualDuplex);
                ScannedImage UpsideDown(int number) => ScanImageOps.Rotate(Feeder.Sheet(number, 150), 2);
                feeder.Images.Enqueue(UpsideDown(1));
                feeder.Images.Enqueue(UpsideDown(3));
                await scan.ScanCommand.ExecuteAsync(null);
                feeder.Images.Enqueue(UpsideDown(4));
                feeder.Images.Enqueue(UpsideDown(2));
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal(["1↓", "2↓", "3↓", "4↓"], Lying(scan));
                Assert.Equal(Tr.T("Vorderseiten um 180° drehen"), scan.TurnFrontsText);

                await scan.TurnFrontsCommand.ExecuteAsync(null);
                Assert.Equal(["1", "2↓", "3", "4↓"], Lying(scan));
                Assert.Equal(Tr.T("Vorderseiten um 180° drehen"), scan.TurnFrontsText);
                Assert.True(store.Load().Single(p => p.Name == "Wenden").BacksTurned);   // so far: the backs lie other than the fronts
                await scan.TurnBacksCommand.ExecuteAsync(null);
                Assert.Equal(["1", "2", "3", "4"], Lying(scan));
                Assert.False(store.Load().Single(p => p.Name == "Wenden").BacksTurned);
                Assert.False(scan.BacksTurned);

                // the next stack, laid right: nothing turned by itself
                await scan.ScanCommand.ExecuteAsync(null);
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8"], Lying(scan));
                Assert.Equal(Tr.T("Vorderseiten um 180° drehen"), scan.TurnFrontsText);
                Assert.Equal(Tr.T("Rückseiten um 180° drehen"), scan.TurnBacksText);
            }
            finally
            {
                await Close(window);
            }
        });
    }

    [Fact]
    public void BothSides_FrontsTurnedWhileWaiting_BacksComeRight_AnOldTileIsNeverShown()
    {
        // a stack laid upside down: its fronts are turned while the backs are awaited (Festlegung), the backs, laid the same way,
        // come right by themselves; a hint shows while the pages are sorted in
        var store = new ScanProfileStore(_folder);
        var feeder = new Feeder();
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(feeder, store);
            try
            {
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                WaitUntil(window, () => !scan.IsBusy && scan.ManualDuplexPossible);
                scan.ManualDuplex = true;
                ScannedImage UpsideDown(int number) => ScanImageOps.Rotate(Feeder.Sheet(number, 150), 2);
                feeder.Images.Enqueue(UpsideDown(1));
                feeder.Images.Enqueue(UpsideDown(3));
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.True(scan.AwaitingBacks);
                await scan.TurnFrontsCommand.ExecuteAsync(null);
                Assert.Equal(["1", "3"], Lying(scan));
                Assert.Equal(Tr.T("Vorderseiten um 180° drehen"), scan.TurnFrontsText);
                Assert.False(scan.BacksTurned);   // nothing remembered before the backs are in
                feeder.Images.Enqueue(UpsideDown(4));
                feeder.Images.Enqueue(UpsideDown(2));
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal(["1", "2", "3", "4"], Lying(scan));
                Assert.True(scan.IsSorting);
                WaitUntil(window, () => window.FindControl<TextBlock>("SortingHint")?.IsEffectivelyVisible == true);
                WaitUntil(window, () => !scan.IsSorting);
                Assert.False(scan.BacksTurned);   // the backs lie as the fronts: the stack as a whole was upside down

                // the fronts turned back: the tiles of the backs are computed anew (show-through) — paused there, the backs are turned
                // meanwhile; the old tiles must not replace the new ones (Fehlergedächtnis 860: page 10 kept its old tile)
                var reached = new TaskCompletionSource();
                var gate = new TaskCompletionSource();
                ScanViewModel.TilesBuiltForTests = () =>
                {
                    reached.TrySetResult();
                    return gate.Task;
                };
                try
                {
                    await scan.TurnFrontsCommand.ExecuteAsync(null);
                    await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    ScanViewModel.TilesBuiltForTests = null;
                    await scan.TurnBacksCommand.ExecuteAsync(null);
                    gate.SetResult();
                }
                finally
                {
                    ScanViewModel.TilesBuiltForTests = null;
                    gate.TrySetResult();
                }
                Assert.Equal(["1↓", "2↓", "3↓", "4↓"], Lying(scan));
                WaitUntil(window, () => scan.ScanPages.All(p => p.PreviewState == p.TileState));
            }
            finally
            {
                ScanViewModel.TilesBuiltForTests = null;
                await Close(window);
            }
        });
    }

    [Fact]
    public void DuplexFeeder_SidesPaired_BlankBackLeftOutAfterPairing()
    {
        var store = new ScanProfileStore(_folder);
        // a profile with „Manueller Duplex“ from a device without duplex: here the box is not offered and the profile's wish not followed
        store.Save(new ScanProfile("Wenden", Feeder.DeviceId, "Muster Einzug 1", "Feeder", 150, ScanColorMode.Gray, "A4", ManualDuplex: true));
        var feeder = new Feeder([[1, 2, 3, null, 4, 5]]) { HasDuplex = true };
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(feeder, store);
            try
            {
                // the one-sided feeder of a device that reads both sides itself: no „Manueller Duplex“ (decided on 15.11.2026)
                scan.SelectedProfile = scan.Profiles.First(p => p.Name == "Wenden");
                WaitUntil(window, () => !scan.IsBusy && scan.SelectedSource?.Kind == ScanSourceKind.Feeder);
                Assert.True(scan.ManualDuplex);              // the wish of the profile is kept …
                Assert.False(scan.ManualDuplexPossible);     // … but not offered, so not followed
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.FeederDuplex);
                WaitUntil(window, () => !scan.IsBusy && scan.SelectedSource?.Kind == ScanSourceKind.FeederDuplex);
                Assert.False(scan.ManualDuplexPossible);   // the feeder reads both sides itself
                scan.SkipBlank = true;
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal([1, 2, 3, 4, 5], Numbers(scan));
                Assert.Same(scan.ScanPages[1], scan.ScanPages[0].Partner);
                Assert.Null(scan.ScanPages[2].Partner);              // its back was blank
                Assert.Same(scan.ScanPages[4], scan.ScanPages[3].Partner);
                // deleting a side leaves its sheet
                foreach (var tile in scan.Pages) tile.IsSelected = tile.Index == 4;
                scan.SelectionChanged();
                scan.DeleteCommand.Execute(null);
                Assert.Null(scan.ScanPages[3].Partner);
            }
            finally
            {
                await Close(window);
            }
        });
    }

    [Fact]
    public void BothSides_FewerBacks_PairedWithTheLastSheets_Warned_BacksScannedAnew()
    {
        var store = new ScanProfileStore(_folder);
        // the backs were stopped after two: they belong to sheets 3 and 2; scanned anew, all three come
        var feeder = new Feeder([[1, 2, 3], [6, 5], [6, 5, 4]]);
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(feeder, store);
            try
            {
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                WaitUntil(window, () => !scan.IsBusy && scan.ManualDuplexPossible);
                scan.ManualDuplex = true;
                await scan.ScanCommand.ExecuteAsync(null);
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal([1, 2, 5, 3, 6], Numbers(scan));
                Assert.Equal(Tr.T("Stapel {0}: {1} Vorderseiten, aber {2} Rückseiten — bitte die Reihenfolge prüfen oder „Rückseiten neu scannen“.", 1, 3, 2), scan.Status);
                Assert.True(scan.StatusIsError);

                // „Rückseiten neu scannen“: waiting again, the backs scanned before stay meanwhile; „Bisherige behalten“ keeps them
                scan.RescanBacksCommand.Execute(null);
                Assert.True(scan.AwaitingBacks);
                Assert.False(scan.CanCorrectDuplex);
                Assert.Equal(Tr.T("Bisherige behalten"), scan.EndDuplexText);
                Assert.Equal([1, 2, 5, 3, 6], Numbers(scan));
                scan.EndDuplexCommand.Execute(null);
                Assert.False(scan.AwaitingBacks);
                Assert.True(scan.CanCorrectDuplex);
                Assert.Equal([1, 2, 5, 3, 6], Numbers(scan));

                // again, now scanned: the new backs replace the old ones, every sheet complete
                scan.RescanBacksCommand.Execute(null);
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal([1, 4, 2, 5, 3, 6], Numbers(scan));
                Assert.Equal(Tr.T("Stapel {0}: {1} Blätter beidseitig gescannt.", 1, 3), scan.Status);
                Assert.False(scan.StatusIsError);
                Assert.Same(scan.ScanPages[1], scan.ScanPages[0].Partner);
                Assert.Same(scan.ScanPages[5], scan.ScanPages[4].Partner);
                Assert.Equal(Tr.T("Ohne Rückseiten"), scan.EndDuplexText);
            }
            finally
            {
                await Close(window);
            }
        });
    }

    [Fact]
    public void BlankPages_LeftOutWhileScanning()
    {
        var store = new ScanProfileStore(_folder);
        var feeder = new Feeder([[1, null, 2, null]]);
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(feeder, store);
            try
            {
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                WaitUntil(window, () => !scan.IsBusy && scan.ManualDuplexPossible);
                scan.SkipBlank = true;
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal([1, 2], Numbers(scan));
                Assert.Equal(Tr.T("{0} Seiten gescannt.", 4) + " " + Tr.T("{0} leere Seiten weggelassen.", 2), scan.Status);

                // without the option every page stays
                scan.SkipBlank = false;
                feeder.Runs.Enqueue([null, 3]);
                foreach (var p in scan.Pages) p.IsSelected = false;
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal(4, scan.ScanPages.Count);
            }
            finally
            {
                await Close(window);
            }
        });
    }

    [Fact]
    public void Processing_PerPage_ForAll_Reset_NextScans_Profile_TakeOver()
    {
        var store = new ScanProfileStore(_folder);
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(new Feeder([[1, 2, 3]]), store);
            try
            {
                // without pages the sliders set what new scans get: the raw scan by default
                Assert.False(scan.AutoEnhance);
                Assert.Equal(Tr.T("für neue Scans"), scan.AdjustmentsTarget);
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                WaitUntil(window, () => !scan.IsBusy && scan.ManualDuplexPossible);
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.All(scan.ScanPages, p => Assert.Equal(new Core.Scanning.Processing.ScanAdjustments(), p.Adjustments));

                // one page: only it changes, only that value
                void Select(params int[] indices)
                {
                    foreach (var tile in scan.Pages) tile.IsSelected = indices.Contains(tile.Index);
                    scan.SelectionChanged();
                }
                // „Für alle übernehmen“ is on by default: then every change applies to all pages; switched off, to the selected ones
                Assert.True(scan.ApplyToAll);
                Select(0);
                Assert.Equal(Tr.T("Alle Seiten"), scan.AdjustmentsTarget);
                scan.ApplyToAll = false;
                Assert.Equal(Tr.T("Seite {0}", 1), scan.AdjustmentsTarget);
                var tileBefore = scan.ScanPages[0].Item.Id;
                scan.Brightness = 40;
                Assert.Equal(40, scan.ScanPages[0].Adjustments.Brightness);
                Assert.Equal(0, scan.ScanPages[1].Adjustments.Brightness);
                WaitUntil(window, () => scan.ScanPages[0].Item.Id != tileBefore);   // the tile is computed anew
                Assert.True(scan.Pages[0].IsSelected);                                // and stays selected

                // two pages: black and white for both, page 1 keeps its brightness, page 3 is untouched
                Select(0, 1);
                Assert.Equal(Tr.T("{0} Seiten gewählt", 2), scan.AdjustmentsTarget);
                scan.BlackWhite = true;
                Assert.True(scan.ScanPages[0].Adjustments.BlackWhite && scan.ScanPages[1].Adjustments.BlackWhite);
                Assert.Equal(40, scan.ScanPages[0].Adjustments.Brightness);
                Assert.Equal(0, scan.ScanPages[1].Adjustments.Brightness);
                Assert.False(scan.ScanPages[2].Adjustments.BlackWhite);

                // showing a page shows its values
                Select(2);
                Assert.False(scan.BlackWhite);
                Assert.Equal(0, scan.Brightness);
                Select(0);
                Assert.True(scan.BlackWhite);
                Assert.Equal(40, scan.Brightness);

                // switched on: all pages get the processing shown; then back to the raw scan for the selected page only (switched off again)
                scan.ApplyToAll = true;
                Assert.All(scan.ScanPages, p => Assert.Equal(scan.ScanPages[0].Adjustments, p.Adjustments));
                scan.ApplyToAll = false;
                scan.ResetAdjustmentsCommand.Execute(null);
                Assert.Equal(Core.Scanning.Processing.ScanAdjustments.None, scan.ScanPages[0].Adjustments);
                Assert.False(scan.AutoEnhance);
                Assert.True(scan.ScanPages[1].Adjustments.BlackWhite);

                // new pages get the values last set (the shown page, reset just now, gets the sharpening too)
                scan.Sharpen = 30;
                Assert.Equal(30, scan.ScanPages[0].Adjustments.Sharpen);
                Assert.False(scan.AutoEnhance);
                foreach (var tile in scan.Pages) tile.IsSelected = false;
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal(30, scan.ScanPages[^1].Adjustments.Sharpen);
                Assert.False(scan.ScanPages[^1].Adjustments.AutoEnhance);

                // the profile keeps them
                scan.SaveProfileCommand.Execute(null);
                scan.NewProfileName = "Mit Nachbearbeitung";
                scan.ConfirmProfileCommand.Execute(null);
                Assert.Equal(30, store.Load().Single(p => p.Name == "Mit Nachbearbeitung").Adjustments!.Sharpen);

                if (UiSession.ScreenshotFolder() is { } folder)
                    UiSession.Capture(window).Save(Path.Combine(folder, "94-scannen-nachbearbeitung.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

                // taking over hands over the processed pages packed as PDF pages: black and white as a JBIG2 mask, the others as JPEG
                ScanResult? result = null;
                scan.TakeOverRequested += r => result = r;
                await scan.TakeOverNewCommand.ExecuteAsync(null);
                Assert.NotNull(result);
                using var taken = result.Pages;
                Assert.Equal(4, taken.PageCount);
                Assert.Contains("JBIG2Decode", ImageFilters(taken, 1));
                Assert.Equal(["DCTDecode"], ImageFilters(taken, 0));   // reset, then only sharpened
            }
            finally
            {
                await Close(window);
            }
        });
    }

    /// <summary>A page of text lines (A4 at 150 dpi), turned by an angle — as a sheet that went in askew.</summary>
    private static ScannedImage Askew(double degrees)
    {
        const int w = 1240, h = 1754;
        var px = new byte[w * h];
        Array.Fill(px, (byte)235);
        var random = new Random(5);
        for (var y = 180; y < h - 200; y += 36)
            for (var x = 150; x < w - 150;)
            {
                var word = random.Next(20, 90);
                for (var yy = y; yy < y + 12; yy++)
                    for (var xx = x; xx < Math.Min(w - 150, x + word); xx++) px[yy * w + xx] = 30;
                x += word + random.Next(10, 18);
            }
        return Core.Scanning.Processing.ImageGeometry.Rotate(new ScannedImage(w, h, 1, 150, px), degrees);
    }

    [Fact]
    public void Straighten_AskewSheet_Badge_FreeRotation()
    {
        var store = new ScanProfileStore(_folder);
        var feeder = new Feeder();
        feeder.Images.Enqueue(Askew(3));
        feeder.Images.Enqueue(Askew(0));
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(feeder, store);
            try
            {
                Assert.Equal(Core.Scanning.Processing.Straighteners.Skew, scan.SelectedStraightener.Key);   // on by default
                Assert.True(scan.CropBorders);
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                WaitUntil(window, () => !scan.IsBusy && scan.ManualDuplexPossible);
                await scan.ScanCommand.ExecuteAsync(null);
                var (askew, straight) = (scan.ScanPages[0], scan.ScanPages[1]);
                Assert.True(askew.IsStraightened);
                Assert.InRange(askew.Geometry().Plan!.Degrees, -3.1, -2.9);
                Assert.False(straight.IsStraightened);   // a straight sheet stays as it is
                Assert.Equal(PageBadge.Applied, scan.Pages[0].Badge);
                Assert.Equal(PageBadge.None, scan.Pages[1].Badge);
                // the processed page has level lines
                Assert.InRange(Core.Scanning.Processing.SkewDetector.Detect(askew.Processed()).Degrees, -0.15, 0.15);

                // the symbol at the tile discards the straightening; again applies it
                var before = askew.Item.Id;
                scan.BadgeClicked(0);
                Assert.True(askew.StraightenOff);
                WaitUntil(window, () => askew.Item.Id != before);
                Assert.Equal(PageBadge.Discarded, scan.Pages[0].Badge);
                Assert.InRange(Core.Scanning.Processing.SkewDetector.Detect(askew.Processed()).Degrees, 2.9, 3.1);
                scan.BadgeClicked(0);
                Assert.False(askew.StraightenOff);

                // turning by hand: starts at the angle found, Esc leaves without change, Enter keeps
                foreach (var tile in scan.Pages) tile.IsSelected = tile.Index == 0;
                scan.SelectionChanged();
                await scan.StartFreeRotateCommand.ExecuteAsync(null);
                Assert.True(scan.IsFreeRotating);
                Assert.InRange(scan.FreeAngle, -3.1, -2.9);
                WaitUntil(window, () => window.FindControl<Controls.GridOverlay>("RotateGrid")!.IsEffectivelyVisible);
                var preview = window.FindControl<Controls.ScanPreview>("PreviewView")!;
                Assert.Equal(scan.FreeAngle, preview.Rotation, 3);
                window.FindControl<Controls.PageGridView>("GridView")!.Grid.Focus();
                window.KeyPress(Avalonia.Input.Key.Right, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowRight, null);
                Assert.InRange(scan.FreeAngle, -3.0, -2.8);
                window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
                Assert.False(scan.IsFreeRotating);
                Assert.Null(askew.ManualAngle);
                Assert.Equal(0, preview.Rotation);
                await scan.StartFreeRotateCommand.ExecuteAsync(null);
                scan.FreeAngle = -2.5;
                window.KeyPress(Avalonia.Input.Key.PageUp, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.PageUp, null);
                Assert.Equal(-1.5, scan.FreeAngle, 3);
                window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
                Assert.Equal(-1.5, askew.ManualAngle!.Value, 3);
                Assert.Equal(-1.5, askew.Geometry().Plan!.Degrees, 3);
                scan.AutoAngleCommand.Execute(null);
                Assert.Null(askew.ManualAngle);

                // „Nicht begradigen“ for the shown page
                scan.SelectedStraightener = scan.StraightenMethods.First(m => m.Key == Core.Scanning.Processing.Straighteners.None);
                Assert.False(askew.Geometry().ChangesPage);
                if (UiSession.ScreenshotFolder() is { } folder)
                {
                    scan.SelectedStraightener = scan.StraightenMethods.First(m => m.Key == Core.Scanning.Processing.Straighteners.Skew);
                    await scan.StartFreeRotateCommand.ExecuteAsync(null);
                    WaitUntil(window, () => scan.Renderer.IsIdle);
                    UiSession.Capture(window).Save(Path.Combine(folder, "95-scannen-frei-drehen.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
            finally
            {
                await Close(window);
            }
        });
    }

    [Fact]
    public void BookPage_Straightened_DoublePage_SplitAtSpine()
    {
        var store = new ScanProfileStore(_folder);
        var feeder = new Feeder();
        var lie = new Scanning.BookLie(14, 60, 320, true, BlurPerMm: 0);
        feeder.Images.Enqueue(Scanning.BookScanSimulator.Page(Askew(0), lie, spineRight: true));
        feeder.Images.Enqueue(Scanning.BookScanSimulator.Spread(Askew(0), Askew(0), lie, turned: false));
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(feeder, store);
            try
            {
                // „Buchseite entzerren“ is offered and used for new pages
                var book = scan.StraightenMethods.First(m => m.Key == Core.Scanning.Processing.Straighteners.Book);
                Assert.Equal(Tr.T("Buchseite entzerren"), book.DisplayName);
                scan.SelectedStraightener = book;
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                WaitUntil(window, () => !scan.IsBusy && scan.ManualDuplexPossible);
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal(2, scan.ScanPages.Count);
                var plan = scan.ScanPages[0].Geometry().Plan;
                Assert.IsType<Core.Scanning.Processing.BookPlan>(plan is Core.Scanning.Processing.CombinedPlan c ? c.Second : plan);
                Assert.True(scan.ScanPages[0].IsStraightened);

                // the double page: split at the spine into two pages of half the width, both with the processing of the page
                foreach (var tile in scan.Pages) tile.IsSelected = tile.Index == 1;
                scan.SelectionChanged();
                var spreadWidth = scan.ScanPages[1].Image.Width;
                await scan.SplitAtSpineCommand.ExecuteAsync(null);
                Assert.Equal(3, scan.ScanPages.Count);
                Assert.InRange(scan.ScanPages[1].Image.Width, spreadWidth * 0.45, spreadWidth * 0.55);
                Assert.Equal(spreadWidth, scan.ScanPages[1].Image.Width + scan.ScanPages[2].Image.Width);
                Assert.Equal(Core.Scanning.Processing.Straighteners.Book, scan.ScanPages[2].Adjustments.Straighten);
                Assert.Equal(Tr.T("Am Bund geteilt: {0} Seiten.", 2), scan.Status);
            }
            finally
            {
                await Close(window);
            }
        });
    }

    [Fact]
    public void Preview_Zoom_FreePanning_BeforeAfter()
    {
        var store = new ScanProfileStore(_folder);
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(new Feeder(), store);
            try
            {
                scan.AutoEnhance = true;   // something to compare
                await scan.ScanCommand.ExecuteAsync(null);
                var view = window.FindControl<Controls.ScanPreview>("PreviewView")!;
                var zoom = window.FindControl<Slider>("PreviewZoom")!;
                WaitUntil(window, () => view.After is not null && view.PageRect.Height > 50);
                var fitted = view.PageRect.Size;

                // the slider zooms the preview, the text shows the zoom
                zoom.Value = 3;
                WaitUntil(window, () => Math.Abs(view.PageRect.Height - fitted.Height * 3) < 1);
                Assert.Contains("300", window.FindControl<TextBlock>("PreviewZoomText")!.Text);
                // free panning: as far as the edge reaching the middle, not further
                view.PanBy(new Avalonia.Vector(100000, 0));
                Assert.Equal(view.Bounds.Width / 2, view.PageRect.Left, 1);
                view.PanBy(new Avalonia.Vector(0, -100000));
                Assert.Equal(view.Bounds.Height / 2, view.PageRect.Bottom, 1);
                // also at the whole page the page can be moved to look at an edge
                window.FindControl<Button>("PreviewFit")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, view.Zoom);
                Assert.Equal(default, view.Pan);
                view.PanBy(new Avalonia.Vector(-100000, 0));
                Assert.Equal(view.Bounds.Width / 2, view.PageRect.Right, 1);
                // zooming around a point keeps it in place
                view.Fit();
                var at = new Avalonia.Point(view.PageRect.Left + 20, view.PageRect.Top + 30);
                view.ZoomAt(4, at);
                Assert.Equal(4, view.Zoom, 3);
                Assert.Equal(at.X, view.PageRect.Left + 20 * 4, 1);

                // before/after: the toggle in the bar, the processed page right, the page as scanned left
                Assert.False(scan.Compare);
                window.FindControl<ToggleButton>("CompareToggle")!.IsChecked = true;
                Assert.True(scan.Compare);
                WaitUntil(window, () => scan.PreviewBefore is not null && view.Before is not null);
                Assert.True(view.Compare);
                view.Split = 0.3;
                if (UiSession.ScreenshotFolder() is { } folder)
                    UiSession.Capture(window).Save(Path.Combine(folder, "96-scannen-vorher-nachher.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                scan.Compare = false;
                WaitUntil(window, () => scan.PreviewBefore is null);
            }
            finally
            {
                await Close(window);
            }
        });
    }

    [Fact]
    public void SaveAsImages_AllOrSelected_Processed_SettingsRemembered()
    {
        var store = new ScanProfileStore(_folder);
        var feeder = new Feeder([[1, 2, 3]]);
        var target = Path.Combine(_folder, "bilder");
        Directory.CreateDirectory(target);
        UiSession.Run(async () =>
        {
            var (window, scan) = Open(feeder, store);
            try
            {
                Services.UserSettings.Update(s => s with { PageImages = null });
                scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                WaitUntil(window, () => !scan.IsBusy && scan.ManualDuplexPossible);
                await scan.ScanCommand.ExecuteAsync(null);
                Assert.Equal(3, scan.ScanPages.Count);
                foreach (var p in scan.ScanPages) p.Adjustments = p.Adjustments with { BlackWhite = true };

                // the dialog: all three pages, the selection of one page offered, no resolution (scans keep theirs)
                var offered = new List<(int All, int Selected)>();
                Core.Imaging.PageImageSettings? answer = null;
                scan.PickPageImages = (start, all, selected) =>
                {
                    offered.Add((all, selected));
                    var dialog = new PageImagesWindow(start, all, selected, document: false);
                    dialog.Show();
                    UiSession.Capture(dialog);
                    Assert.False(dialog.FindControl<ComboBox>("DpiChoice")!.IsVisible);
                    if (UiSession.ScreenshotFolder() is { } shots)
                        UiSession.Capture(dialog).Save(Path.Combine(shots, "97-scannen-als-bild.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    dialog.FindControl<RadioButton>("TiffChoice")!.IsChecked = true;
                    var settings = dialog.Settings();
                    dialog.Close();
                    return Task.FromResult<Core.Imaging.PageImageSettings?>(answer ?? settings);
                };
                scan.PickFolder = _ => Task.FromResult<string?>(target);
                foreach (var tile in scan.Pages) tile.IsSelected = tile.Index == 1;
                scan.SelectionChanged();
                Assert.True(scan.SaveImagesCommand.CanExecute(null));
                await scan.SaveImagesCommand.ExecuteAsync(null);
                Assert.Equal((3, 1), offered[0]);
                var files = Directory.GetFiles(target).Order().ToList();
                Assert.Equal(3, files.Count);   // one selected page: „Alle“ is the default
                Assert.All(files, f => Assert.Matches(@"^Scan_\d{4}-\d\d-\d\d_\d\d-\d\d_00[123]\.tif$", Path.GetFileName(f)));
                Assert.Equal(Tr.T("{0} Bilder gespeichert in {1}", 3, target), scan.Status);
                // processed as taken over: black and white → Group 4 (compression 4 in the directory)
                var tiff = File.ReadAllBytes(files[1]);
                Assert.Contains(Enumerable.Range(0, 30), i => BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(10 + i * 12)) == 259
                    && BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(10 + i * 12 + 8)) == 4);
                Assert.Equal(Core.Imaging.PageImageFormat.Tiff, Services.UserSettings.Current.PageImages!.Format);

                // only the selected page, as PNG
                foreach (var f in files) File.Delete(f);
                answer = new Core.Imaging.PageImageSettings(Core.Imaging.PageImageFormat.Png, SelectedOnly: true);
                await scan.SaveImagesCommand.ExecuteAsync(null);
                var one = Assert.Single(Directory.GetFiles(target));
                Assert.EndsWith("_001.png", one);
                using var bitmap = SkiaSharp.SKBitmap.Decode(one);
                Assert.Equal(2, Feeder.Number(new ScannedImage(bitmap.Width, bitmap.Height, 1, 150,
                    [.. Enumerable.Range(0, bitmap.Width * bitmap.Height).Select(i => bitmap.GetPixel(i % bitmap.Width, i / bitmap.Width).Red)])));

                // cancelled in the dialog: nothing written
                foreach (var f in Directory.GetFiles(target)) File.Delete(f);
                scan.PickPageImages = (_, _, _) => Task.FromResult<Core.Imaging.PageImageSettings?>(null);
                await scan.SaveImagesCommand.ExecuteAsync(null);
                Assert.Empty(Directory.GetFiles(target));
            }
            finally
            {
                await Close(window);
            }
        });
    }

    [Fact]
    public void FileArea_ExportImportDelete()
    {
        var mine = new ScanProfileStore(Path.Combine(_folder, "hier"));
        var other = new ScanProfileStore(Path.Combine(_folder, "dort"));
        mine.Save(new ScanProfile("Briefe", Feeder.DeviceId, "Muster Einzug 1", "Feeder", 200, ScanColorMode.Gray));
        mine.Save(new ScanProfile("Fotos", Feeder.DeviceId, "Muster Einzug 1", "Flatbed", 600));
        other.Save(new ScanProfile("Fotos", "x:1", "Anderer Scanner", "Flatbed", 300));
        var exported = Path.Combine(_folder, "export.json");
        UiSession.Run(async () =>
        {
            var page = new ScanProfilesViewModel(() => mine) { PickExportPath = (_, _) => Task.FromResult<string?>(exported) };
            page.Reload();
            Assert.Equal(["Briefe", "Fotos"], page.Rows.Select(r => r.Name));
            Assert.Equal("Muster Einzug 1 · " + Tr.T("Einzug") + " · 200 dpi · " + Tr.T("Graustufen") + " · A4", page.Rows[0].Summary);

            // nothing ticked: all are exported
            await page.ExportCommand.ExecuteAsync(null);
            Assert.Equal(Tr.T("{0} Scanprofile exportiert: {1}", 2, "export.json"), page.Info);
            Assert.False(page.InfoIsError);

            // imported elsewhere: „Fotos“ is there with other values and gets a number, nothing is replaced
            var there = new ScanProfilesViewModel(() => other) { PickImportPath = _ => Task.FromResult<string?>(exported) };
            await there.ImportCommand.ExecuteAsync(null);
            Assert.Equal(["Briefe", "Fotos", "Fotos (2)"], there.Rows.Select(r => r.Name));
            Assert.Equal(300, other.Load().First(p => p.Name == "Fotos").Dpi);
            Assert.Equal(Tr.T("{0} Scanprofile importiert.", 2) + " " + Tr.T("Profile mit schon vergebenem Namen haben eine Nummer bekommen."), there.Info);
            // the same file again brings nothing new
            await there.ImportCommand.ExecuteAsync(null);
            Assert.Equal(Tr.T("Keine neuen Scanprofile — alle gibt es schon."), there.Info);

            // a file that is not a profile file: told, nothing changes
            var foreign = Path.Combine(_folder, "fremd.json");
            await File.WriteAllTextAsync(foreign, "{\"Format\":\"etwas anderes\"}");
            there.PickImportPath = _ => Task.FromResult<string?>(foreign);
            await there.ImportCommand.ExecuteAsync(null);
            Assert.True(there.InfoIsError);
            Assert.Equal(Tr.T("Die Datei enthält keine Scanprofile von PDF-Knead."), there.Info);
            Assert.Equal(3, there.Rows.Count);

            // only the ticked one is exported
            page.Rows[1].IsChecked = true;
            await page.ExportCommand.ExecuteAsync(null);
            Assert.Equal(Tr.T("1 Scanprofil exportiert: {0}", "export.json"), page.Info);
            await using (var file = File.OpenRead(exported)) Assert.Equal(["Fotos"], ScanProfileStore.Import(file).Select(p => p.Name));

            // deleting asks; „Abbrechen“ keeps the profiles
            var asked = 0;
            var answer = false;
            there.Confirm = (_, _, _) =>
            {
                asked++;
                return Task.FromResult(answer);
            };
            await there.DeleteCommand.ExecuteAsync(null);
            Assert.Equal(0, asked);   // nothing ticked: told so
            Assert.True(there.InfoIsError);
            there.Rows[0].IsChecked = true;
            there.Rows[2].IsChecked = true;
            await there.DeleteCommand.ExecuteAsync(null);
            Assert.Equal(3, other.Load().Count);
            answer = true;
            await there.DeleteCommand.ExecuteAsync(null);
            Assert.Equal(2, asked);
            Assert.Equal(["Fotos"], other.Load().Select(p => p.Name));
            Assert.Equal(Tr.T("{0} Scanprofile gelöscht.", 2), there.Info);
        });
    }

    [Fact]
    public void FileArea_PageShowsTheProfiles()
    {
        UiSession.Run(() =>
        {
            var vm = new MainViewModel();
            var store = ScanProfileStore.Beside(Services.UserSettings.File);
            store.Save(new ScanProfile("Testprofil Datei-Bereich", Feeder.DeviceId, "Muster Einzug 1", "Feeder", 200, ScanColorMode.Gray));
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                vm.ShowBackstageCommand.Execute(BackstagePage.ScanProfiles);
                WaitUntil(window, () => window.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Text == "Testprofil Datei-Bereich" && t.IsEffectivelyVisible));
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Tr.T("GESPEICHERTE PROFILE") && t.IsEffectivelyVisible);
                if (UiSession.ScreenshotFolder() is { } folder)
                    UiSession.Capture(window).Save(Path.Combine(folder, "93-scanprofile.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            finally
            {
                store.Delete("Testprofil Datei-Bereich");
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// A scanner in software with a flatbed and a feeder. Each scan from the feeder takes the next run of sheets: a number gives a sheet with
    /// that many marks in the top left corner and a block of text, null a blank sheet. Without runs left, the feeder gives one sheet.
    /// </summary>
    private sealed class Feeder(IEnumerable<int?[]>? runs = null) : IScanBackend
    {
        public const string DeviceId = "einzug:1";
        private static readonly ScanDevice Device = new(DeviceId, "Muster", "Einzug 1", "sheetfed scanner");

        public Queue<int?[]> Runs { get; } = new(runs ?? []);

        /// <summary>Whether the feeder reads both sides itself (the source „Einzug beidseitig“).</summary>
        public bool HasDuplex { get; init; }

        /// <summary>Whole pages to deliver from the feeder before the runs (one scan delivers all of them).</summary>
        public Queue<ScannedImage> Images { get; } = new();

        // a name of its own: the device lists found are kept per scanner interface for the run of the program
        public string Name => "Test-Einzug";

        public bool IsAvailable(out string? hint)
        {
            hint = null;
            return true;
        }

        public Task<IReadOnlyList<ScanDevice>> FindDevicesAsync(CancellationToken cancel = default) => Task.FromResult<IReadOnlyList<ScanDevice>>([Device]);

        public Task<IScanSession> OpenAsync(ScanDevice device, CancellationToken cancel = default) => Task.FromResult<IScanSession>(new Session(this));

        public static int Number(ScannedImage page)
        {
            var count = 0;
            for (var k = 0; k < 8; k++)
                if (page.Pixels[((long)10 * page.Width + 10 + k * 20) * page.Channels] < 100) count++;
            return count;
        }

        /// <summary>The number of a sheet turned by half a turn: the marks are then at the bottom right.</summary>
        public static int NumberUpsideDown(ScannedImage page)
        {
            var count = 0;
            for (var k = 0; k < 8; k++)
                if (page.Pixels[((long)(page.Height - 1 - 10) * page.Width + page.Width - 1 - (10 + k * 20)) * page.Channels] < 100) count++;
            return count;
        }

        public static ScannedImage Sheet(int? number, int dpi)
        {
            var (w, h) = (200, 280);
            var pixels = new byte[w * h];
            Array.Fill(pixels, (byte)240);
            if (number is { } n)
            {
                for (var k = 0; k < n; k++)
                    for (var y = 5; y < 15; y++)
                        for (var x = 5 + k * 20; x < 15 + k * 20; x++)
                            pixels[y * w + x] = 20;
                for (var y = 100; y < 130; y++)
                    for (var x = 20; x < 180; x++)
                        pixels[y * w + x] = (x / 4 % 2 == 0) ? (byte)30 : (byte)240;   // a block of „text“
            }
            return new ScannedImage(w, h, 1, dpi, pixels);
        }

        private sealed class Session(Feeder owner) : IScanSession
        {
            private static readonly ScanSource Flatbed = new("Flatbed", ScanSourceKind.Flatbed);
            private static readonly ScanSource Feed = new("Feeder", ScanSourceKind.Feeder);
            private static readonly ScanSource Duplex = new("Duplex", ScanSourceKind.FeederDuplex);
            private ScanSource _source = Flatbed;

            public ScanDevice Device => Feeder.Device;

            public ScanCapabilities Capabilities => Caps();

            private ScanCapabilities Caps() => new(owner.HasDuplex ? [Flatbed, Feed, Duplex] : [Flatbed, Feed], _source,
                _source == Flatbed ? [75, 150, 300, 600, 1200] : [75, 150, 200, 300],
                [ScanColorMode.Color, ScanColorMode.Gray, ScanColorMode.BlackWhite], 216, 356);

            public Task<ScanCapabilities> SelectSourceAsync(string source, CancellationToken cancel = default)
            {
                _source = source == Feed.Name ? Feed : source == Duplex.Name ? Duplex : Flatbed;
                return Task.FromResult(Caps());
            }

            public async IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, IProgress<ScanProgress>? progress = null,
                [EnumeratorCancellation] CancellationToken cancel = default)
            {
                if (_source != Flatbed && owner.Images.Count > 0)
                {
                    while (owner.Images.TryDequeue(out var image))
                    {
                        await Task.Yield();
                        yield return image;
                    }
                    yield break;
                }
                var sheets = _source != Flatbed && owner.Runs.TryDequeue(out var run) ? run : [1];
                for (var i = 0; i < sheets.Length; i++)
                {
                    await Task.Yield();
                    cancel.ThrowIfCancellationRequested();
                    progress?.Report(new ScanProgress(i + 1, 1));
                    yield return Sheet(sheets[i], settings.Dpi);
                }
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
