using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>
/// The own eSCL access behind the program's scanner interface (KONZEPT.md §5.25, S9d): finds scanners by the multicast search, in the
/// networks and at the addresses the user entered, and opens them over HTTPS (with the confirmed certificate) or HTTP. Runs in the program's
/// process — there is no foreign driver code that could crash. Normally used inside <see cref="CombinedScanBackend"/>, which joins its
/// devices with those of SANE or WIA.
/// </summary>
public sealed class EsclBackend : IScanBackend
{
    /// <summary>The start of the ids of devices of this access (never the same as a SANE back end's).</summary>
    public const string IdPrefix = "escl-direkt:";

    private readonly Func<EsclSettings> _settings;
    private readonly IEsclCertificatePins _pins;
    private readonly Func<EsclDiagnostics, CancellationToken, Task<List<EsclDevice>>> _discover;
    private readonly Func<IReadOnlyCollection<IPAddress>> _ownAddresses;
    private readonly Func<IReadOnlyList<EsclNetworkRange>, IProgress<(int Done, int Total)>?, EsclDiagnostics, CancellationToken, Task<List<EsclDevice>>> _searchNetworks;
    private readonly Func<IPAddress, CancellationToken, Task<string?>>? _hostName;
    private readonly ConcurrentDictionary<string, EsclDevice> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Prepares the access; nothing is sent until a search.</summary>
    /// <param name="settings">The current settings (asked at every search).</param>
    /// <param name="pins">The confirmed certificates.</param>
    /// <param name="log">The diagnosis log; a new one if null.</param>
    /// <param name="timeouts">The time limits of the connections; <see cref="EsclTimeouts.Default"/> if null.</param>
    /// <param name="discover">The multicast search; <see cref="EsclDiscovery.SearchAsync"/> if null (tests put a fixed list here).</param>
    /// <param name="ownAddresses">The addresses of this computer; read from the interfaces if null.</param>
    /// <param name="searchNetworks">The search of the entered networks; <see cref="EsclNetworkSearch.SearchAsync(IReadOnlyList{EsclNetworkRange}, IProgress{ValueTuple{int, int}}?, EsclDiagnostics?, CancellationToken)"/>
    /// if null (tests put a device in software here: the real search asks the usual ports, which a test cannot open).</param>
    public EsclBackend(Func<EsclSettings> settings, IEsclCertificatePins pins, EsclDiagnostics? log = null, EsclTimeouts? timeouts = null,
        Func<EsclDiagnostics, CancellationToken, Task<List<EsclDevice>>>? discover = null, Func<IReadOnlyCollection<IPAddress>>? ownAddresses = null,
        Func<IReadOnlyList<EsclNetworkRange>, IProgress<(int Done, int Total)>?, EsclDiagnostics, CancellationToken, Task<List<EsclDevice>>>? searchNetworks = null,
        Func<IPAddress, CancellationToken, Task<string?>>? hostName = null)
    {
        _settings = settings;
        _pins = pins;
        Log = log ?? new EsclDiagnostics();
        Timeouts = timeouts ?? EsclTimeouts.Default;
        _discover = discover ?? ((l, c) => EsclDiscovery.SearchAsync(l, null, c));
        _ownAddresses = ownAddresses ?? OwnAddresses;
        _searchNetworks = searchNetworks ?? ((r, p, l, c) => EsclNetworkSearch.SearchAsync(r, p, l, c));
        _hostName = hostName;   // null: EsclHostNames.NameOfAsync (the name service of the system, then NetBIOS at the device)
    }

    /// <summary>What the access did lately, for „Diagnose speichern …“.</summary>
    public EsclDiagnostics Log { get; }

    /// <summary>The time limits of the connections.</summary>
    public EsclTimeouts Timeouts { get; }

    /// <summary>Whether „Netzwerk-Scanner direkt ansprechen“ is on.</summary>
    public bool Enabled => _settings().Direct;

    /// <inheritdoc/>
    public string Name => "eSCL";

    /// <inheritdoc/>
    public bool IsAvailable(out string? hint)
    {
        hint = null;
        return true;
    }

    /// <summary>Remembers a certificate the user confirmed (<see cref="EsclCertificateException"/>).</summary>
    /// <param name="device">The device (<see cref="EsclCertificateException.Key"/>).</param>
    /// <param name="fingerprint">The fingerprint (<see cref="EsclCertificateException.Fingerprint"/>).</param>
    public void Confirm(string device, string fingerprint)
    {
        _pins.Set(device, fingerprint);
        Log.Add("Zertifikat", $"bestätigt: {EsclTrust.Display(fingerprint)}");
    }

    /// <summary>Whether an id belongs to this access.</summary>
    public static bool Owns(string id) => id.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The list of scanners: the multicast search, the typed addresses and the scanners chosen from the network search, each asked at its
    /// address (only an answer with the same UUID counts). The entered networks are not searched here — before No. 926 every search (also the
    /// one when the window opens) asked all their addresses, and every scanner found there came into the list unasked; now
    /// <see cref="SearchNetworksAsync"/> does that on the user's request only, and the user chooses what comes into the list.
    /// </summary>
    public async Task<IReadOnlyList<ScanDevice>> FindDevicesAsync(CancellationToken cancel = default)
    {
        var settings = _settings();
        if (!settings.Direct) return [];
        var searches = new List<Task<List<EsclDevice>>> { _discover(Log, cancel) };
        searches.AddRange(settings.ManualAddresses.Select(a => EsclNetworkSearch.ProbeAddressAsync(a, Log, cancel)));
        var chosen = settings.ChosenScanners.Select(c => ChosenAsync(c.Scanner, c.Address, cancel)).ToList();
        var hosts = settings.ChosenScanners.Where(c => c.Scanner.HostName is not null)
            .ToDictionary(c => c.Scanner.Device, c => c.Scanner.HostName, StringComparer.OrdinalIgnoreCase);
        var found = new List<EsclDevice>();
        var offline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in chosen)
        {
            try
            {
                var (answered, stored) = await task;
                if (answered.Count > 0) found.AddRange(answered);
                else if (stored is not null)
                {
                    found.Add(stored);
                    offline.Add(stored.Key);
                }
            }
            catch (Exception e) when (e is ScanException or SocketException or OperationCanceledException && !cancel.IsCancellationRequested)
            {
                Log.Add("Suche", "gescheitert: " + e.Message);
            }
        }
        foreach (var search in searches)
        {
            try
            {
                found.AddRange(await search);
            }
            catch (Exception e) when (e is ScanException or SocketException or OperationCanceledException && !cancel.IsCancellationRequested)
            {
                Log.Add("Suche", "gescheitert: " + e.Message);
            }
        }
        cancel.ThrowIfCancellationRequested();
        var own = _ownAddresses();
        var devices = new List<ScanDevice>();
        foreach (var group in found.GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            // the services of all searches, each once; a scanner this computer itself offers (its address is an own one) is left out — it is
            // already there as a local device
            var services = group.SelectMany(d => d.Services)
                .GroupBy(s => (s.Tls, s.Port, string.Join(",", s.Addresses.Select(a => a.ToString()).Order())))
                .Select(g => g.First()).OrderByDescending(s => s.Tls).ToList();
            if (services.All(s => s.Addresses.Count > 0 && s.Addresses.All(a => own.Any(o => o.Equals(a))))) continue;
            var device = new EsclDevice(group.Key, group.First().Name, services);
            _known[device.Key] = device;
            // the host name before the type: the one saved with the choice, else the one the multicast search names (not an address)
            var host = EsclHostNames.Short(hosts.GetValueOrDefault(group.Key))
                ?? services.Select(s => EsclHostNames.Short(s.Host)).FirstOrDefault(h => h is not null);
            var listed = ToScanDevice(device) with { Host = host };
            // a chosen scanner that did not answer (another network, switched off) stays in the list, marked — the user removes it, not the search
            devices.Add(offline.Contains(group.Key) && group.Count() == 1 ? listed with { Via = Tr.T("nicht erreichbar") } : listed);
        }
        return devices;
    }

    /// <summary>
    /// Asks a chosen scanner at its address. Answered with its UUID: the device as found. Otherwise the device as saved (its address, port and
    /// resource path) — it stays in the list as „nicht erreichbar“ and is opened at that address once it is back (e.g. in its network again).
    /// </summary>
    private async Task<(List<EsclDevice> Answered, EsclDevice? Stored)> ChosenAsync(EsclChosenScanner scanner, EsclAddress address, CancellationToken cancel)
    {
        var found = await EsclNetworkSearch.ProbeAddressAsync(address, null, cancel);
        var same = found.Where(d => string.Equals(d.Key, scanner.Device, StringComparison.OrdinalIgnoreCase)).ToList();
        Log.Add("Gewählt", same.Count > 0 ? $"{scanner.Name}: erreichbar unter {address.Host}"
            : found.Count > 0 ? $"{scanner.Name}: unter {address.Host} antwortet ein anderes Gerät — „Netz durchsuchen“ findet es neu"
            : $"{scanner.Name}: unter {address.Host} nicht erreichbar");
        return same.Count > 0 ? (same, null) : ([], Stored(scanner, address));
    }

    /// <summary>The device as saved with the choice: its address, port and resource path; null for an address that is not one in the own network.</summary>
    private static EsclDevice? Stored(EsclChosenScanner scanner, EsclAddress address)
    {
        if (!IPAddress.TryParse(address.Host, out var ip) || !EsclNetwork.IsLocal(ip)) return null;
        var tls = address.Tls ?? true;
        var root = address.Root ?? "eSCL";
        var service = new EsclService(scanner.Name, tls, address.Host, address.Port ?? (tls ? 443 : 80), [ip],
            new Dictionary<string, string> { ["uuid"] = scanner.Device, ["rs"] = root, ["ty"] = scanner.Name });
        return new EsclDevice(scanner.Device, scanner.Name, [service]);
    }

    /// <summary>
    /// The entry of a scanner just chosen with the green check, at once — without waiting for the next search (design decision: it should
    /// stand in the list at once). It is opened at its saved address; the next search joins it with the other ways of the device.
    /// </summary>
    /// <returns>The entry, or null if the saved address is not one in the own network.</returns>
    public ScanDevice? Remember(EsclChosenScanner scanner)
    {
        if (EsclAddress.Parse(scanner.Address) is not { } address || Stored(scanner, address) is not { } device) return null;
        _known[device.Key] = device;
        return ToScanDevice(device) with { Host = EsclHostNames.Short(scanner.HostName) };
    }

    /// <summary>
    /// Searches the entered networks address by address (the button „Netz durchsuchen“; No. 926): the result goes to the list in the group
    /// „Netzwerk-Scanner (erweitert)“, where the user chooses what comes into the list of scanners — not into that list itself.
    /// </summary>
    /// <param name="progress">Receives how many of all attempts are done.</param>
    /// <param name="cancel">Stops the search; the devices found so far are returned.</param>
    /// <returns>The scanners found with the host names of their addresses (design decision: then one knows which device it is), without
    /// those this computer offers itself; empty with the switch off or without networks.</returns>
    /// <param name="names">Whether to look up the host names too; the window shows the scanners first and asks the names afterwards
    /// (<see cref="HostNamesAsync"/>) — they take some seconds more, and a list at 100 % without scanners looked like a hang.</param>
    public async Task<IReadOnlyList<EsclFoundScanner>> SearchNetworksAsync(IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default,
        bool names = true)
    {
        var settings = _settings();
        if (!settings.Direct || settings.Ranges is not { Count: > 0 } ranges) return [];
        var found = await _searchNetworks(ranges, progress, Log, cancel);
        var own = _ownAddresses();
        IReadOnlyList<EsclFoundScanner> devices = [.. found.Where(d => !d.Services.All(s => s.Addresses.Count > 0 && s.Addresses.All(a => own.Any(o => o.Equals(a)))))
            .Select(d => new EsclFoundScanner(d, null))];
        return names ? await HostNamesAsync(devices, cancel) : devices;
    }

    /// <summary>The scanners with the host names of their first addresses (<see cref="EsclHostNames"/>); one without a name stays without.</summary>
    public async Task<IReadOnlyList<EsclFoundScanner>> HostNamesAsync(IReadOnlyList<EsclFoundScanner> found, CancellationToken cancel = default)
    {
        static IPAddress? First(EsclDevice d) => d.Services.SelectMany(s => s.Addresses).FirstOrDefault();
        var names = await EsclHostNames.NamesOfAsync(found.Select(f => First(f.Device)).OfType<IPAddress>(), _hostName, cancel);
        foreach (var (address, name) in names) Log.Add("Netzsuche", $"Name von {address}: {name}");
        return [.. found.Select(f => f with { HostName = First(f.Device) is { } a && names.TryGetValue(a, out var n) ? n : f.HostName })];
    }

    /// <summary>The device as the program lists it: the name under which the device announces itself, its addresses in the type.</summary>
    internal static ScanDevice ToScanDevice(EsclDevice device)
    {
        var announced = device.Services.Select(s => s.Instance).FirstOrDefault(i => i.Contains("._uscan", StringComparison.OrdinalIgnoreCase));
        var name = announced is not null && EsclXml.Clean(DnsMessage.Labels(announced).FirstOrDefault() ?? "") is { Length: > 0 } label ? label
            : EsclXml.Clean(device.Name);
        if (name.Length == 0) name = device.Key;
        var addresses = device.Services.SelectMany(s => s.Addresses).Distinct()
            .Select(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{new IPAddress(a.GetAddressBytes())}]" : a.ToString());
        return new ScanDevice(IdPrefix + device.Key, "", name, "eSCL " + string.Join(" ", addresses));
    }

    /// <inheritdoc/>
    /// <exception cref="EsclCertificateException">The device presents a certificate the user has not confirmed.</exception>
    public async Task<IScanSession> OpenAsync(ScanDevice device, CancellationToken cancel = default)
    {
        var id = device.AllIds.FirstOrDefault(Owns) ?? throw new ScanException(Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline);
        var key = id[IdPrefix.Length..];
        if (!_known.ContainsKey(key)) await FindDevicesAsync(cancel);
        if (!_known.TryGetValue(key, out var found))
            throw new ScanException(Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline);
        ScanException? failed = null;
        foreach (var service in found.Services)
            foreach (var address in service.Addresses.Where(EsclNetwork.IsLocal).OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6))
            {
                var client = new EsclClient(new IPEndPoint(address, service.Port), service.Tls, service.Root, key, _pins, EsclTrustMode.Pinned, Log, Timeouts);
                try
                {
                    var caps = await client.GetCapabilitiesAsync(cancel);
                    Log.Add("Öffnen", $"{device.DisplayName} über {(service.Tls ? "https" : "http")} {address}:{service.Port}");
                    return new EsclSession(device, client, caps, Log);
                }
                catch (EsclCertificateException)
                {
                    client.Dispose();
                    throw;
                }
                catch (ScanException e)
                {
                    client.Dispose();
                    failed ??= e;
                }
            }
        throw failed ?? new ScanException(Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline);
    }

    private static IReadOnlyCollection<IPAddress> OwnAddresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address)];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}

/// <summary>An eSCL scanner opened for scanning (KONZEPT.md §5.25, S9d).</summary>
public sealed class EsclSession : IScanSession
{
    private readonly EsclClient _client;
    private readonly EsclDiagnostics _log;

    internal EsclSession(ScanDevice device, EsclClient client, EsclCapabilities caps, EsclDiagnostics log)
    {
        Device = device;
        _client = client;
        Description = caps;
        _log = log;
        Capabilities = caps.ToScanCapabilities(null);
    }

    /// <inheritdoc/>
    public ScanDevice Device { get; }

    /// <inheritdoc/>
    public ScanCapabilities Capabilities { get; private set; }

    /// <summary>The device's own description.</summary>
    public EsclCapabilities Description { get; }

    /// <inheritdoc/>
    public Task<ScanCapabilities> SelectSourceAsync(string source, CancellationToken cancel = default)
    {
        Capabilities = Description.ToScanCapabilities(EsclCapabilities.InputOf(source));
        return Task.FromResult(Capabilities);
    }

    /// <summary>Reads the device's state now — for the feeder state shown next to the source.</summary>
    /// <exception cref="ScanException">Not reachable or no readable answer.</exception>
    public Task<EsclStatus> StatusAsync(CancellationToken cancel = default) => _client.GetStatusAsync(cancel);

    /// <inheritdoc/>
    public IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, IProgress<ScanProgress>? progress = null, CancellationToken cancel = default) =>
        EsclScanJob.RunAsync(_client, Description, settings, progress, _log, cancel);

    /// <inheritdoc/>
    /// <remarks>Lets a job that is closing in the background end first (at most half a minute), so that the device is not left busy.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (_client.Closing is { IsCompleted: false } closing)
        {
            try
            {
                await closing.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.Add("Auftrag", "Abschluss dauert zu lange — Verbindung beendet");
            }
        }
        _client.Dispose();
    }
}
