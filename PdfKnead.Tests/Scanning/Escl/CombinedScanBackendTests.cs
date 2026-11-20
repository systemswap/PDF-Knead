using System.Net;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>
/// The system's scanner interface together with the own eSCL access (KONZEPT.md §5.25, S9d): one entry per device, eSCL first, the system's
/// way as fallback when opening fails and — after a problem of the eSCL access — for the rest of the run (Festlegung).
/// </summary>
[Collection(nameof(EsclDeviceCollection))]
public sealed class CombinedScanBackendTests
{
    private static readonly EsclTimeouts Quick = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2));

    private const string Uuid = "11111111-2222-3333-4444-555555555555";

    /// <summary>The multicast search as if it had found the scanner in software under the name it announces.</summary>
    private static Func<EsclDiagnostics, CancellationToken, Task<List<EsclDevice>>> Found(FakeScanner scanner, string announced = "Beispiel Büro 1000",
        bool tls = false) =>
        (_, _) => Task.FromResult<List<EsclDevice>>([new EsclDevice(Uuid, announced, [new EsclService($"{announced}._uscan._tcp.local", tls,
            "beispiel.local", scanner.Device.Port, [IPAddress.Loopback], new Dictionary<string, string> { ["uuid"] = Uuid })])]);

    private static (CombinedScanBackend Backend, SystemScanner System) Combined(FakeScanner? scanner, EsclSettings? settings = null,
        IEsclCertificatePins? pins = null, bool tls = false, bool systemThere = true, params ScanDevice[] systemDevices)
    {
        var system = new SystemScanner(systemThere, systemDevices.Length > 0 ? systemDevices : [new ScanDevice("airscan:e1:Beispiel Büro 1000", "eSCL", "Beispiel Büro 1000", "")]);
        var escl = new EsclBackend(() => settings ?? new EsclSettings(), pins ?? new EsclMemoryPins(), null, Quick,
            scanner is null ? (_, _) => Task.FromResult<List<EsclDevice>>([]) : Found(scanner, tls: tls), () => []);
        return (new CombinedScanBackend(system, escl), system);
    }

    private static async Task<List<ScannedImage>> ScanAll(IScanSession session, string source, ScanColorMode mode = ScanColorMode.Gray)
    {
        var pages = new List<ScannedImage>();
        await foreach (var page in session.ScanAsync(new ScanSettings(source, 150, mode))) pages.Add(page);
        return pages;
    }

    [Fact]
    public void Join_ByNameOrAddress_SystemOrderKept()
    {
        var escl = new[]
        {
            new ScanDevice(EsclBackend.IdPrefix + "a", "", "Büro Links (A1B2C3)", "eSCL 192.168.1.5"),
            new ScanDevice(EsclBackend.IdPrefix + "b", "", "Etage 2", "eSCL 192.168.1.9"),
            new ScanDevice(EsclBackend.IdPrefix + "c", "", "Nur eSCL", "eSCL 192.168.1.30"),
        };
        var system = new[]
        {
            new ScanDevice("genesys:libusb:001:004", "Canoscan", "Flachbett", "flatbed scanner"),
            new ScanDevice("airscan:e1:Büro Links (A1B2C3)", "eSCL", "Büro  Links (a1b2c3)", "", Alternates: "escl:https://192.168.1.5:443"),
            new ScanDevice("escl:https://192.168.1.9:443", "eSCL", "Anderer Name", ""),
        };
        var joined = CombinedScanBackend.Join(system, escl);
        Assert.Equal(["genesys:libusb:001:004", EsclBackend.IdPrefix + "a", EsclBackend.IdPrefix + "b", EsclBackend.IdPrefix + "c"], joined.Select(d => d.Id));
        Assert.Equal(["airscan:e1:Büro Links (A1B2C3)", "escl:https://192.168.1.5:443"], joined[1].AlternateIds);   // by name
        Assert.Equal(["escl:https://192.168.1.9:443"], joined[2].AlternateIds);                                    // by address
        Assert.Empty(joined[3].AlternateIds);
    }

    [Fact]
    public async Task Find_OneEntry_OpenUsesEscl()
    {
        await using var scanner = new FakeScanner();
        scanner.Pages.Add(new FakePage(FakeScanner.Image(40, 50, false)));
        var (backend, system) = Combined(scanner);
        var device = Assert.Single(await backend.FindDevicesAsync());
        Assert.StartsWith(EsclBackend.IdPrefix, device.Id);
        // installed in the system: the name it has there, no host name before it (No. 926, design decision: too long otherwise)
        Assert.Equal("Beispiel Büro 1000", device.DisplayName);
        Assert.Equal("Beispiel Büro 1000", device.BaseName);
        // not installed: the short host name the multicast search names („beispiel.local“) before the type
        var (alone, _) = Combined(scanner, systemThere: false);
        Assert.Equal("beispiel – Beispiel Büro 1000", Assert.Single(await alone.FindDevicesAsync()).DisplayName);
        await using var session = await backend.OpenAsync(device);
        Assert.Equal(["Platen", "Feeder", "Feeder" + ScanSource.DuplexMarker], session.Capabilities.Sources.Select(s => s.Name));
        Assert.Single(await ScanAll(session, "Platen"));
        Assert.Equal(1, scanner.Creates);
        Assert.Equal(0, system.Opens);
    }

    [Fact]
    public async Task EsclUnreachable_FallsBackSilently()
    {
        var scanner = new FakeScanner();
        var (backend, system) = Combined(scanner);
        var device = Assert.Single(await backend.FindDevicesAsync());
        await scanner.DisposeAsync();   // gone after the search
        await using var session = await backend.OpenAsync(device);
        Assert.Equal(1, system.Opens);
        Assert.Equal("airscan:e1:Beispiel Büro 1000", system.LastOpened);
        Assert.Single(await ScanAll(session, "Flatbed"));
        Assert.False(backend.IsDisturbed(device));   // unreachable is no fault of eSCL: next time eSCL is tried again
        Assert.Contains(backend.Escl.Log.Entries, e => e.Contains("Rückfall"));
    }

    [Fact]
    public async Task Certificate_Asked_ThenConfirmedOrDeclined()
    {
        await using var scanner = new FakeScanner(tls: true);
        scanner.Pages.Add(new FakePage(FakeScanner.Image(40, 50, false)));
        var pins = new EsclMemoryPins();
        var (backend, system) = Combined(scanner, pins: pins, tls: true);
        var device = Assert.Single(await backend.FindDevicesAsync());
        var ask = await Assert.ThrowsAsync<EsclCertificateException>(() => backend.OpenAsync(device));
        Assert.Equal(scanner.Device.Fingerprint, ask.Fingerprint);
        Assert.Equal(0, system.Opens);   // never silently around the question
        pins.Set(ask.Key, ask.Fingerprint);
        await using (var session = await backend.OpenAsync(device)) Assert.Single(await ScanAll(session, "Platen"));
        // declined: the system's way from then on
        backend.Avoid(device, "Zertifikat abgelehnt");
        await using (await backend.OpenAsync(device)) Assert.Equal(1, system.Opens);
    }

    [Fact]
    public async Task ProtocolProblem_PagesKept_NextJobOverSystem()
    {
        await using var scanner = new FakeScanner();
        scanner.Pages.Add(new FakePage(FakeScanner.Image(40, 50, false)));
        scanner.Pages.Add(new FakePage([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]));   // a damaged page
        var (backend, system) = Combined(scanner);
        var device = Assert.Single(await backend.FindDevicesAsync());
        await using var session = await backend.OpenAsync(device);
        var pages = new List<ScannedImage>();
        await Assert.ThrowsAsync<EsclProtocolException>(async () =>
        {
            await foreach (var page in session.ScanAsync(new ScanSettings("Feeder", 150, ScanColorMode.Gray))) pages.Add(page);
        });
        Assert.Single(pages);
        Assert.True(backend.IsDisturbed(device));
        Assert.Equal(1, scanner.Deletes);
        // the same session: the next job over the system's way, the feeder of the eSCL names becomes the system's feeder
        Assert.Single(await ScanAll(session, "Feeder"));
        Assert.Equal(("ADF", 1), (system.LastSource, system.Opens));
        Assert.Equal(1, scanner.Creates);
    }

    [Fact]
    public async Task PaperProblem_NoFallback()
    {
        await using var scanner = new FakeScanner { Feeder = "ScannerAdfJam" };
        var (backend, system) = Combined(scanner);
        var device = Assert.Single(await backend.FindDevicesAsync());
        await using var session = await backend.OpenAsync(device);
        var e = await Assert.ThrowsAnyAsync<ScanException>(() => ScanAll(session, "Feeder"));
        Assert.Equal(ScanErrorKind.Jammed, e.Kind);
        Assert.False(backend.IsDisturbed(device));
        Assert.Equal(0, system.Opens);
    }

    [Fact]
    public async Task Switch_Off_OnlySystem()
    {
        await using var scanner = new FakeScanner();
        var (backend, system) = Combined(scanner, new EsclSettings(Direct: false));
        var device = Assert.Single(await backend.FindDevicesAsync());
        Assert.Equal("airscan:e1:Beispiel Büro 1000", device.Id);
        await using (await backend.OpenAsync(device)) Assert.Equal(1, system.Opens);
        Assert.Empty(scanner.Device.Requests);   // not a single request to the network scanner
    }

    [Fact]
    public async Task SystemMissing_EsclStillScans()
    {
        await using var scanner = new FakeScanner();
        scanner.Pages.Add(new FakePage(FakeScanner.Image(40, 50, true)));
        var (backend, _) = Combined(scanner, systemThere: false);
        Assert.True(backend.IsAvailable(out var hint));
        Assert.Null(hint);
        var device = Assert.Single(await backend.FindDevicesAsync());
        await using var session = await backend.OpenAsync(device);
        Assert.Single(await ScanAll(session, "Platen", ScanColorMode.Color));
        // and with the switch off it is as before: the hint of the missing system interface
        var (off, _) = Combined(scanner, new EsclSettings(Direct: false), systemThere: false);
        Assert.False(off.IsAvailable(out hint));
        Assert.NotNull(hint);
    }

    [Fact]
    public async Task OwnShare_LeftOut()
    {
        await using var scanner = new FakeScanner();
        var escl = new EsclBackend(() => new EsclSettings(), new EsclMemoryPins(), null, Quick, Found(scanner), () => [IPAddress.Loopback]);
        Assert.Empty(await escl.FindDevicesAsync());
    }

    [Fact]
    public async Task ManualAddress_Found()
    {
        await using var scanner = new FakeScanner();
        var escl = new EsclBackend(() => new EsclSettings(Addresses: [$"http://127.0.0.1:{scanner.Device.Port}"]), new EsclMemoryPins(), null, Quick,
            (_, _) => Task.FromResult<List<EsclDevice>>([]), () => []);
        var device = Assert.Single(await escl.FindDevicesAsync());
        Assert.Equal(EsclBackend.IdPrefix + Uuid, device.Id);
        Assert.Equal("Beispiel Büro 1000", device.DisplayName);
        Assert.Contains("127.0.0.1", device.Kind);
    }

    /// <summary>
    /// Fehlergedächtnis 926: the entered networks are searched only on request („Netz durchsuchen“), and what is found there does not come
    /// into the list of scanners by itself; a chosen scanner is asked at its address, and only with the same UUID.
    /// </summary>
    [Fact]
    public async Task Networks_SearchedOnRequestOnly_ChosenAskedAtTheirAddress()
    {
        await using var scanner = new FakeScanner();
        var settings = new EsclSettings(Networks: ["192.168.178.0/24"]);
        var searched = 0;
        var hit = new EsclDevice(Uuid, "Beispiel Büro 1000", [new EsclService("Beispiel Büro 1000", false, "127.0.0.1", scanner.Device.Port,
            [IPAddress.Loopback], new Dictionary<string, string> { ["uuid"] = Uuid, ["rs"] = "eSCL" })]);
        Task<List<EsclDevice>> Search(IReadOnlyList<EsclNetworkRange> ranges, IProgress<(int, int)>? progress, EsclDiagnostics log, CancellationToken cancel)
        {
            searched++;
            Assert.Equal(["192.168.178.0/24"], ranges.Select(r => r.ToString()));
            return Task.FromResult<List<EsclDevice>>([hit]);
        }
        var escl = new EsclBackend(() => settings, new EsclMemoryPins(), null, Quick, (_, _) => Task.FromResult<List<EsclDevice>>([]), () => [], Search,
            (address, _) => Task.FromResult<string?>(address.Equals(IPAddress.Loopback) ? "drucker-2og.beispiel.local." : null));

        // the list of scanners: the networks are not searched (before: at every search, also when the window opened)
        Assert.Empty(await escl.FindDevicesAsync());
        Assert.Equal(0, searched);
        // the button: the scanners found, for the list in the group
        var (found, hostName) = Assert.Single(await escl.SearchNetworksAsync());
        Assert.Equal(1, searched);
        Assert.Equal("drucker-2og.beispiel.local", hostName);   // the host name of the address (design decision), the final dot off
        var chosen = EsclChosenScanner.Of(found, hostName)!;
        Assert.Equal("drucker-2og.beispiel.local", chosen.HostName);
        Assert.Equal($"http://127.0.0.1:{scanner.Device.Port}/eSCL", chosen.Address);
        // chosen: in the list, asked at its address — without searching the network again
        settings = settings.WithChosen(chosen, true);
        var device = Assert.Single(await escl.FindDevicesAsync());
        Assert.Equal(EsclBackend.IdPrefix + Uuid, device.Id);
        Assert.Equal(1, searched);
        Assert.Null(device.Via);
        // another device answers at the address: the chosen one is not taken for it — it stays in the list, marked, the log says why
        settings = settings with { Chosen = [chosen with { Device = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" }] };
        var other = Assert.Single(await escl.FindDevicesAsync());
        Assert.Equal(EsclBackend.IdPrefix + "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", other.Id);
        Assert.Equal(Tr.T("nicht erreichbar"), other.Via);
        Assert.Contains(escl.Log.Entries, e => e.Contains("anderes Gerät", StringComparison.Ordinal));
        // not reachable at all (another network): stays in the list, marked; opening says it is not reachable
        settings = settings with { Chosen = [chosen with { Address = "http://127.0.0.1:1/eSCL" }] };
        var away = Assert.Single(await escl.FindDevicesAsync());
        Assert.Equal(Tr.T("nicht erreichbar"), away.Via);
        Assert.Contains("Beispiel Büro 1000", away.DisplayName);
        Assert.Equal(ScanErrorKind.Offline, (await Assert.ThrowsAnyAsync<ScanException>(() => escl.OpenAsync(away))).Kind);
        // back at its address: listed without the mark, without a new search of the network
        settings = settings with { Chosen = [chosen] };
        Assert.Null(Assert.Single(await escl.FindDevicesAsync()).Via);
        // the switch off or no network: no search at all
        settings = settings with { Direct = false };
        Assert.Empty(await escl.SearchNetworksAsync());
        settings = new EsclSettings();
        Assert.Empty(await escl.SearchNetworksAsync());
        Assert.Equal(1, searched);
        // a scanner this computer offers itself is left out of the result
        var own = new EsclBackend(() => new EsclSettings(Networks: ["192.168.178.0/24"]), new EsclMemoryPins(), null, Quick,
            (_, _) => Task.FromResult<List<EsclDevice>>([]), () => [IPAddress.Loopback], Search, (_, _) => Task.FromResult<string?>(null));
        Assert.Empty(await own.SearchNetworksAsync());
    }

    /// <summary>The system's scanner interface in software: one device per entry, sources „Flatbed“ and „ADF“, one grey page per job.</summary>
    private sealed class SystemScanner(bool available, IReadOnlyList<ScanDevice> devices) : IScanBackend
    {
        public int Opens;
        public string? LastOpened;
        public string? LastSource;

        public string Name => "SANE";

        public bool IsAvailable(out string? hint)
        {
            hint = available ? null : "Bitte SANE installieren.";
            return available;
        }

        public Task<IReadOnlyList<ScanDevice>> FindDevicesAsync(CancellationToken cancel = default) =>
            available ? Task.FromResult(devices) : Task.FromException<IReadOnlyList<ScanDevice>>(new ScanException("fehlt", ScanErrorKind.InterfaceMissing));

        public Task<IScanSession> OpenAsync(ScanDevice device, CancellationToken cancel = default)
        {
            Opens++;
            LastOpened = device.Id;
            return Task.FromResult<IScanSession>(new Session(this, device));
        }

        private sealed class Session(SystemScanner owner, ScanDevice device) : IScanSession
        {
            private static readonly ScanSource[] Sources = [new("Flatbed", ScanSourceKind.Flatbed), new("ADF", ScanSourceKind.Feeder)];

            public ScanDevice Device => device;

            public ScanCapabilities Capabilities { get; private set; } = new(Sources, Sources[0], [150, 300], [ScanColorMode.Gray], 216, 297);

            public Task<ScanCapabilities> SelectSourceAsync(string source, CancellationToken cancel = default) =>
                Task.FromResult(Capabilities = Capabilities with { Source = Sources.First(s => s.Name == source) });

            public async IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, IProgress<ScanProgress>? progress = null,
                [EnumeratorCancellation] CancellationToken cancel = default)
            {
                owner.LastSource = settings.Source;
                await Task.Yield();
                yield return new ScannedImage(10, 10, 1, 150, new byte[100]);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
