using System.Net;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Escl;
using PdfKnead.ViewModels;
using PdfKnead.Views;
using EsclScanner = PdfKnead.Tests.Scanning.Escl.FakeScanner;
using EsclPage = PdfKnead.Tests.Scanning.Escl.FakePage;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The scan window with the own eSCL access (KONZEPT §5.25, S9d): the group „Netzwerk-Scanner (erweitert)“ with its network list, the
/// question about a scanner's certificate (trusted: scanned over eSCL; not trusted: the system's way), the feeder state next to the source.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class NetworkScannerUiTests
{
    private const string Uuid = "11111111-2222-3333-4444-555555555555";

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

    [Fact]
    public void NetworkList_PastedSplit_RefusedStayWithReason_Saved()
    {
        var saved = new EsclSettings();
        var changes = new List<bool>();
        var vm = new NetworkScannersViewModel(() => saved, change => saved = change(saved), new EsclDiagnostics());
        vm.Changed += changes.Add;
        vm.NetworkInput = "192.168.178.0, 10.1.2\n8.8.8 kaputt 192.168.178.77";
        vm.AddNetwork();
        Assert.Equal(["192.168.178.1 – 254", "10.1.2.1 – 254"], vm.Networks.Select(n => n.Display));
        Assert.Equal("8.8.8, kaputt", vm.NetworkInput);   // the duplicate network is dropped, the refused stay
        Assert.Contains("nicht im eigenen Netzwerk", vm.NetworkError);
        Assert.Equal(["192.168.178.0/24", "10.1.2.0/24"], saved.Networks);
        // leaving the field: only the reason, nothing taken
        vm.NetworkInput = "10.9.9.x";
        vm.CheckNetworkInput();
        Assert.Null(vm.NetworkError);
        vm.NetworkInput = "192.168.0.0/16";
        vm.CheckNetworkInput();
        Assert.Contains("zu groß", vm.NetworkError);
        Assert.Equal(2, vm.Networks.Count);
        vm.RemoveNetwork(vm.Networks[0]);
        Assert.Equal(["10.1.2.0/24"], saved.Networks);
        // addresses
        vm.AddressInput = "nicht gültig/";
        vm.AddAddress();
        Assert.NotNull(vm.AddressError);
        vm.AddressInput = "https://192.168.178.20";
        vm.AddAddress();
        Assert.Null(vm.AddressError);
        Assert.Equal(["https://192.168.178.20"], saved.Addresses);
        vm.RemoveAddress(vm.Addresses[0]);
        Assert.Equal([], saved.Addresses);
        // the switch searches at once, lists only at the next search
        vm.Direct = false;
        Assert.False(saved.Direct);
        Assert.Equal([false, false, false, false, true], changes);
        // a new window shows what was saved
        var again = new NetworkScannersViewModel(() => saved, change => saved = change(saved), new EsclDiagnostics());
        Assert.Equal(["10.1.2.1 – 254"], again.Networks.Select(n => n.Display));
        Assert.False(again.Direct);
    }

    private static EsclFoundScanner Found(string uuid, string name, string address, string? hostName = null) =>
        new(new EsclDevice(uuid, name, [new EsclService(name, true, address, 443, [IPAddress.Parse(address)], new Dictionary<string, string> { ["uuid"] = uuid })]),
            hostName);

    /// <summary>
    /// Fehlergedächtnis 926 (design decision): the scanners „Netz durchsuchen“ finds stand in the group, sorted, without the check; the green
    /// check takes one into the list of scanners and saves it with its address; a new window shows it before any search; found at a new
    /// address it is saved with that one; not found it stays with a note until the check is taken off.
    /// </summary>
    [Fact]
    public void FoundScanners_GreenCheckTakesIntoTheList_Saved()
    {
        const string first = "aaaaaaaa-1111-2222-3333-444444444444", second = "bbbbbbbb-1111-2222-3333-444444444444";
        var saved = new EsclSettings(Networks: ["10.242.66.0/24"]);
        var changes = new List<bool>();
        var vm = new NetworkScannersViewModel(() => saved, change => saved = change(saved), new EsclDiagnostics());
        vm.Changed += changes.Add;
        Assert.Empty(vm.Found);
        vm.ShowSearchResult([Found(first, "Beispiel Etage 2", "10.242.66.35", "PRN-2OG-01.beispiel.local"), Found(second, "Beispiel Empfang", "10.242.66.21")]);
        Assert.Equal(["Beispiel Empfang", "Beispiel Etage 2"], vm.Found.Select(r => r.Name));
        Assert.Equal(["10.242.66.21", "10.242.66.35 · PRN-2OG-01"], vm.Found.Select(r => r.Address));   // the host name behind the address, without domain
        Assert.Equal("10.242.66.35 · PRN-2OG-01.beispiel.local", vm.Found[1].AddressFull);                // in full in the tooltip
        Assert.All(vm.Found, r => Assert.False(r.IsChosen));
        Assert.Contains("2 Scanner gefunden", vm.SearchStatus);
        Assert.Empty(changes);   // nothing in the list of scanners yet
        Assert.Empty(saved.ChosenScanners);

        vm.Found[1].IsChosen = true;
        Assert.Equal([true], changes);   // the list of scanners is searched again (cheap: the chosen ones are asked directly)
        Assert.Equal("https://10.242.66.35:443/eSCL", Assert.Single(saved.ChosenScanners).Scanner.Address);
        Assert.Equal("PRN-2OG-01.beispiel.local", saved.ChosenScanners[0].Scanner.HostName);   // shown also where the network gives no name

        // a new window: the chosen one stands there before any search
        var again = new NetworkScannersViewModel(() => saved, change => saved = change(saved), new EsclDiagnostics());
        again.Changed += changes.Add;
        var row = Assert.Single(again.Found);
        Assert.True(row.IsChosen);
        Assert.Equal("Beispiel Etage 2", row.Name);
        Assert.Contains("Beispiel Etage 2", row.CheckName);
        Assert.Equal("10.242.66.35 · PRN-2OG-01", row.Address);
        // the next search: found at a new address — saved with it; the other one is new again, without the check
        again.ShowSearchResult([Found(first, "Beispiel Etage 2", "10.242.66.40"), Found(second, "Beispiel Empfang", "10.242.66.21")]);
        Assert.Equal("https://10.242.66.40:443/eSCL", Assert.Single(saved.ChosenScanners).Scanner.Address);
        Assert.Equal("10.242.66.40", again.Found.Single(r => r.IsChosen).Address);
        Assert.Equal([true, true], changes);
        // not found: the chosen one stays with a note, the other goes; a stopped search removes nothing
        again.ShowSearchResult([], cancelled: true);
        Assert.Equal(2, again.Found.Count);
        again.ShowSearchResult([]);
        row = Assert.Single(again.Found);
        Assert.NotNull(row.Note);
        Assert.Contains("Kein Scanner gefunden", again.SearchStatus);
        // the check taken off: out of the list and out of the settings
        row.IsChosen = false;
        Assert.Empty(again.Found);
        Assert.Empty(saved.ChosenScanners);
        Assert.Equal([true, true, true], changes);
    }

    /// <summary>
    /// Fehlergedächtnis 926: the scanners show at once, their host names come afterwards (a list at 100 % without scanners looked like a
    /// hang); a chosen scanner is saved with its name; a search without names keeps the name known at the same address.
    /// </summary>
    [Fact]
    public void FoundScanners_NamesAfterTheScanners_KnownNameStays()
    {
        const string first = "aaaaaaaa-1111-2222-3333-444444444444";
        var saved = new EsclSettings(Networks: ["10.242.66.0/24"]);
        var vm = new NetworkScannersViewModel(() => saved, change => saved = change(saved), new EsclDiagnostics());
        vm.ShowSearchResult([Found(first, "Beispiel Etage 2", "10.242.66.35")]);
        var row = Assert.Single(vm.Found);
        Assert.Equal("10.242.66.35", row.Address);
        row.IsChosen = true;
        Assert.Null(saved.ChosenScanners[0].Scanner.HostName);
        vm.ShowHostNames([Found(first, "Beispiel Etage 2", "10.242.66.35", "PRN-2OG-01.beispiel.local")]);
        Assert.Equal("10.242.66.35 · PRN-2OG-01", row.Address);
        Assert.Equal("PRN-2OG-01.beispiel.local", saved.ChosenScanners[0].Scanner.HostName);
        // the next search shows the scanners before their names: the known name stays meanwhile
        vm.ShowSearchResult([Found(first, "Beispiel Etage 2", "10.242.66.35")]);
        Assert.Equal("10.242.66.35 · PRN-2OG-01", Assert.Single(vm.Found).Address);
        Assert.Equal("PRN-2OG-01.beispiel.local", saved.ChosenScanners[0].Scanner.HostName);
        // at another address the old name does not count
        vm.ShowSearchResult([Found(first, "Beispiel Etage 2", "10.242.66.40")]);
        Assert.Equal("10.242.66.40", Assert.Single(vm.Found).Address);
    }

    /// <summary>
    /// Fehlergedächtnis 926 in the window: „Netz durchsuchen“ fills the list in the group, the device list below stays as it was; the green
    /// check (a toggle button in the row, named for screen readers) brings the scanner into the device list.
    /// </summary>
    [Fact]
    public void Window_SearchNetworks_ListInTheGroup_CheckBringsItIntoTheDeviceList()
    {
        var scanner = new EsclScanner();
        var system = new SystemScanner();
        var hit = new EsclDevice(Uuid, "Beispiel Büro 1000", [new EsclService("Beispiel Büro 1000", false, "127.0.0.1", scanner.Device.Port,
            [IPAddress.Loopback], new Dictionary<string, string> { ["uuid"] = Uuid })]);
        var backend = new CombinedScanBackend(system, new EsclBackend(() => Services.NetworkScanning.Settings, new EsclMemoryPins(), null,
            new EsclTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2)),
            (_, _) => Task.FromResult<List<EsclDevice>>([]), () => [],
            (_, _, _, _) => Task.FromResult<List<EsclDevice>>([hit, Found("bbbbbbbb-1111-2222-3333-444444444444", "Beispiel Empfang", "192.168.178.21").Device]),
            (_, _) => Task.FromResult<string?>(null)));   // no name lookups from a test
        UiSession.Run(async () =>
        {
            var before = Services.UserSettings.Current;
            Services.UserSettings.Update(s => s with { Scan = null, ScanProfileName = null, ScanNetwork = new EsclSettings(Networks: ["192.168.178.0/24"]) });
            var scan = new ScanViewModel(backend, canAppend: false);
            var dialog = new ScanWindow(scan);
            dialog.Show();
            try
            {
                WaitUntil(dialog, () => !scan.IsBusy);
                Assert.DoesNotContain(scan.Devices, d => d.AllIds.Any(EsclBackend.Owns));
                var group = dialog.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "NetworkGroup");
                group.IsExpanded = true;
                UiSession.Capture(dialog);
                Assert.True(scan.SearchNetworksCommand.CanExecute(null));
                await scan.SearchNetworksAsync();
                UiSession.Capture(dialog);
                Assert.Equal(2, scan.Network!.Found.Count);
                var row = scan.Network.Found.Single(r => r.Scanner.Device == Uuid);
                Assert.All(scan.Network.Found, r => Assert.False(r.IsChosen));
                Assert.DoesNotContain(scan.Devices, d => d.AllIds.Any(EsclBackend.Owns));   // the device list is not touched
                var check = dialog.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
                    .Single(t => t.Classes.Contains("foundCheck") && Avalonia.Automation.AutomationProperties.GetName(t)?.Contains("Beispiel Büro 1000") == true);
                check.IsChecked = true;
                Assert.True(row.IsChosen);
                // at once in the list (design decision), not only after the search that follows
                Assert.Contains(scan.Devices, d => d.AllIds.Any(EsclBackend.Owns));
                WaitUntil(dialog, () => !scan.IsBusy && scan.Devices.Any(d => d.AllIds.Any(EsclBackend.Owns)));
                Assert.Single(Services.NetworkScanning.Settings.ChosenScanners);
                if (UiSession.ScreenshotFolder() is { } folder)
                    UiSession.Capture(dialog).Save(Path.Combine(folder, "scan-netzwerk-gefunden.png"), PngBitmapEncoderOptions.Default);

                // the „×“ in the list of scanners (design decision): the chosen way is taken out of the choice (the check goes off), the way of
                // the system is hidden — it stays away after the next search, until „Ausgeblendete wieder zeigen“
                var joined = scan.Devices.Single(d => d.AllIds.Any(EsclBackend.Owns));
                Assert.Contains("airscan:e1:Beispiel Büro 1000", joined.AllIds);
                scan.RemoveDevice(joined);
                WaitUntil(dialog, () => !scan.IsBusy && !scan.Network.Found.Any(r => r.IsChosen));
                Assert.Empty(Services.NetworkScanning.Settings.ChosenScanners);
                Assert.Equal(["airscan:e1:Beispiel Büro 1000"], Services.UserSettings.Current.ScanHidden);
                WaitUntil(dialog, () => !scan.IsBusy && scan.Devices.Count == 0);
                Assert.Equal(1, scan.HiddenCount);
                Assert.True(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ShowHidden").IsVisible);
                await scan.ShowHiddenDevicesAsync();
                WaitUntil(dialog, () => !scan.IsBusy && scan.Devices.Count == 1);
                Assert.Equal(0, scan.HiddenCount);
                Assert.Null(Services.UserSettings.Current.ScanHidden);
                // the „×“ stands in the open list only, not in the closed box
                var choice = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "DeviceChoice");
                bool RemoveVisible(Avalonia.Visual root) => root.GetVisualDescendants().OfType<Button>()
                    .Any(b => b.IsVisible && Avalonia.Automation.AutomationProperties.GetName(b) == Tr.T("Scanner aus der Liste entfernen"));
                Assert.False(RemoveVisible(choice));
                choice.IsDropDownOpen = true;
                UiSession.Capture(dialog);
                Assert.True(choice.ContainerFromIndex(0) is { } item && RemoveVisible(item));
                choice.IsDropDownOpen = false;
            }
            finally
            {
                dialog.Close();
                Services.UserSettings.Update(_ => before);
                await scanner.DisposeAsync();
            }
        });
    }

    private static CombinedScanBackend Backend(EsclScanner scanner, SystemScanner system) =>
        new(system, new EsclBackend(() => new EsclSettings(), new EsclMemoryPins(), null,
            new EsclTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2)),
            (_, _) => Task.FromResult<List<EsclDevice>>([new EsclDevice(Uuid, "Beispiel Büro 1000", [new EsclService("Beispiel Büro 1000._uscans._tcp.local",
                true, "beispiel.local", scanner.Device.Port, [IPAddress.Loopback], new Dictionary<string, string> { ["uuid"] = Uuid })])]), () => []));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Certificate_TrustedOverEscl_DeclinedOverSystem(bool trust)
    {
        var scanner = new EsclScanner(tls: true);
        scanner.Pages.Add(new EsclPage(EsclScanner.Image(60, 80, true)));
        var system = new SystemScanner();
        var backend = Backend(scanner, system);
        var asked = new List<EsclCertificateException>();
        var previous = ScanViewModel.FeederInterval;
        ScanViewModel.FeederInterval = TimeSpan.FromMilliseconds(100);
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Scan = null, ScanProfileName = null });
            var scan = new ScanViewModel(backend, canAppend: false);
            var dialog = new ScanWindow(scan);
            scan.ConfirmCertificate = (_, question) =>
            {
                asked.Add(question);
                return Task.FromResult(trust);
            };
            dialog.Show();
            try
            {
                WaitUntil(dialog, () => scan.SelectedResolution is not null && !scan.IsBusy);
                var question = Assert.Single(asked);
                Assert.Equal(scanner.Device.Fingerprint, question.Fingerprint);
                Assert.Null(question.Previous);
                var group = dialog.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "NetworkGroup");
                Assert.True(group.IsVisible);
                Assert.False(group.IsExpanded);
                if (trust)
                {
                    Assert.Equal(0, system.Opens);
                    Assert.Equal(["Platen", "Feeder", "Feeder" + ScanSource.DuplexMarker], scan.Sources.Select(s => s.Name));
                    // the feeder state shows next to the source
                    scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Feeder);
                    WaitUntil(dialog, () => scan.FeederState == Tr.T("Papier im Einzug"));
                    Assert.False(scan.FeederProblem);
                    scanner.Feeder = "ScannerAdfJam";
                    WaitUntil(dialog, () => scan.FeederState == Tr.T("Papierstau im Einzug") && scan.FeederProblem);
                    scanner.Feeder = "ScannerAdfLoaded";
                    group.IsExpanded = true;
                    // entries only for the picture; the scanner interface of this test does not read them (no search of a real network)
                    scan.Network!.NetworkInput = "192.168.178.0, 10.20.5 8.8.8";
                    scan.Network.AddNetwork();
                    scan.Network.AddressInput = "https://192.168.178.20";
                    scan.Network.AddAddress();
                    WaitUntil(dialog, () => scan.FeederState == Tr.T("Papier im Einzug"));
                    if (UiSession.ScreenshotFolder() is { } folder)
                        UiSession.Capture(dialog).Save(Path.Combine(folder, "scan-netzwerk.png"), PngBitmapEncoderOptions.Default);
                    foreach (var row in scan.Network.Networks.ToList()) scan.Network.RemoveNetwork(row);
                    foreach (var row in scan.Network.Addresses.ToList()) scan.Network.RemoveAddress(row);
                    scan.SelectedSource = scan.Sources.First(s => s.Kind == ScanSourceKind.Flatbed);
                    WaitUntil(dialog, () => scan.FeederState is null && !scan.IsBusy);
                    await scan.ScanCommand.ExecuteAsync(null);
                    Assert.Single(scan.Pages);
                    Assert.Equal(1, scanner.Creates);
                }
                else
                {
                    Assert.Equal(1, system.Opens);
                    Assert.Equal(["Flatbed", "ADF"], scan.Sources.Select(s => s.Name));
                    Assert.True(backend.IsDisturbed(scan.SelectedDevice!));
                    Assert.Null(scan.FeederState);
                    Assert.Equal(0, scanner.Creates);
                }
            }
            finally
            {
                dialog.Close();
                await dialog.Cleanup;
                ScanViewModel.FeederInterval = previous;
                await scanner.DisposeAsync();
            }
        });
    }

    /// <summary>The system's scanner interface in software, with the same device under its network back end's name.</summary>
    private sealed class SystemScanner : IScanBackend
    {
        public int Opens;

        public string Name => "SANE";

        public bool IsAvailable(out string? hint)
        {
            hint = null;
            return true;
        }

        public Task<IReadOnlyList<ScanDevice>> FindDevicesAsync(CancellationToken cancel = default) =>
            Task.FromResult<IReadOnlyList<ScanDevice>>([new ScanDevice("airscan:e1:Beispiel Büro 1000", "eSCL", "Beispiel Büro 1000", "")]);

        public Task<IScanSession> OpenAsync(ScanDevice device, CancellationToken cancel = default)
        {
            Opens++;
            return Task.FromResult<IScanSession>(new Session(device));
        }

        private sealed class Session(ScanDevice device) : IScanSession
        {
            private static readonly ScanSource[] Sources = [new("Flatbed", ScanSourceKind.Flatbed), new("ADF", ScanSourceKind.Feeder)];

            public ScanDevice Device => device;

            public ScanCapabilities Capabilities { get; private set; } = new(Sources, Sources[0], [150, 300], [ScanColorMode.Gray], 216, 297);

            public Task<ScanCapabilities> SelectSourceAsync(string source, CancellationToken cancel = default) =>
                Task.FromResult(Capabilities = Capabilities with { Source = Sources.First(s => s.Name == source) });

            public async IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, IProgress<ScanProgress>? progress = null,
                [EnumeratorCancellation] CancellationToken cancel = default)
            {
                await Task.Yield();
                yield return new ScannedImage(10, 10, 1, 150, new byte[100]);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
