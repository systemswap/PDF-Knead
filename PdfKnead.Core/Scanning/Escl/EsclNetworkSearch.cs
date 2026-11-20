using System.Net;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>A place where the search of a network looks for an eSCL scanner: a port with or without TLS.</summary>
/// <param name="Port">The port.</param>
/// <param name="Tls">Whether with TLS.</param>
public readonly record struct EsclProbePort(int Port, bool Tls);

/// <summary>
/// Searches given networks for eSCL scanners address by address (KONZEPT.md §5.25, S9, „Netz der Scanner durchsuchen“, Festlegung):
/// for networks the multicast search does not reach. Only on the user's request. Every address is asked on the usual eSCL ports for the
/// device description below the usual resource path; a device counts only with a readable description that names its UUID. Reading only —
/// certificates are not checked here (<see cref="EsclTrustMode.ReadOnly"/>); the user confirms one before anything is scanned. Short time
/// limits and a bounded number of connections at a time keep the search to some seconds per network.
/// </summary>
public static class EsclNetworkSearch
{
    /// <summary>The ports asked: HTTPS first, then the usual ports without TLS.</summary>
    public static readonly IReadOnlyList<EsclProbePort> DefaultPorts = [new(443, true), new(8080, false), new(80, false)];

    /// <summary>How many connection attempts run at a time.</summary>
    public const int Parallel = 64;

    /// <summary>The time limits of the search: an address that does not accept within a second has no scanner on that port.</summary>
    public static readonly EsclTimeouts Timeouts = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));

    /// <summary>Searches networks.</summary>
    /// <param name="ranges">The networks (<see cref="EsclNetworkRange.ParseList"/>).</param>
    /// <param name="progress">Receives how many of all attempts are done.</param>
    /// <param name="log">Where hits and the summary are noted, or null.</param>
    /// <param name="cancel">Stops the search; the devices found so far are returned.</param>
    /// <returns>The scanners found, as the multicast search returns them.</returns>
    public static Task<List<EsclDevice>> SearchAsync(IReadOnlyList<EsclNetworkRange> ranges, IProgress<(int Done, int Total)>? progress = null,
        EsclDiagnostics? log = null, CancellationToken cancel = default) =>
        SearchAsync([.. ranges.SelectMany(r => r.Hosts).Distinct()], DefaultPorts, Timeouts, progress, log, cancel);

    /// <summary>Searches single addresses on the given ports (also what the tests use, with the device in software on its own port).</summary>
    /// <param name="addresses">The addresses; those outside the own network are left out.</param>
    /// <param name="ports">The ports to ask.</param>
    /// <param name="timeouts">The time limits per attempt.</param>
    /// <param name="progress">Receives how many of all attempts are done.</param>
    /// <param name="log">Where hits and the summary are noted, or null.</param>
    /// <param name="cancel">Stops the search; the devices found so far are returned.</param>
    /// <returns>The scanners found.</returns>
    public static async Task<List<EsclDevice>> SearchAsync(IReadOnlyList<IPAddress> addresses, IReadOnlyList<EsclProbePort> ports, EsclTimeouts timeouts,
        IProgress<(int Done, int Total)>? progress = null, EsclDiagnostics? log = null, CancellationToken cancel = default)
    {
        var targets = addresses.Where(EsclNetwork.IsLocal).SelectMany(a => ports.Select(p => (Address: a, Port: p))).ToList();
        var found = new List<(EsclService Service, string Uuid)>();
        var gate = new Lock();
        var done = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        log?.Add("Netzsuche", $"frage {addresses.Count} Adresse(n) auf Port {string.Join(", ", ports.Select(p => p.Port))}");
        using var slots = new SemaphoreSlim(Parallel);
        var tasks = targets.Select(async target =>
        {
            try
            {
                await slots.WaitAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                if (await ProbeAsync(target.Address, target.Port, timeouts, cancel) is { } hit)
                {
                    lock (gate) found.Add(hit);
                    log?.Add("Netzsuche", $"gefunden: {hit.Service.Name} ({hit.Uuid}) — {(target.Port.Tls ? "https" : "http")} {target.Address}:{target.Port.Port}");
                }
            }
            finally
            {
                slots.Release();
                progress?.Report((Interlocked.Increment(ref done), targets.Count));
            }
        }).ToList();
        await Task.WhenAll(tasks);
        List<EsclDevice> devices;
        lock (gate)
            devices = [.. found.GroupBy(f => f.Uuid, StringComparer.OrdinalIgnoreCase)
                .Select(g => new EsclDevice(g.Key, g.First().Service.Name, [.. g.Select(f => f.Service).OrderByDescending(s => s.Tls)]))];
        log?.Add("Netzsuche", $"{(cancel.IsCancellationRequested ? "abgebrochen" : "fertig")} nach {clock.ElapsedMilliseconds} ms: {devices.Count} Gerät(e)");
        return devices;
    }

    /// <summary>
    /// Asks an address the user typed (<see cref="EsclAddress"/>): a name is resolved first and must lie in the own network as a whole; without
    /// a port, HTTPS on 443 and the usual ports without TLS are asked, with a port both ways unless the scheme says which.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="log">Where the result is noted, or null.</param>
    /// <param name="cancel">Stops asking.</param>
    /// <returns>The scanner found there, or none.</returns>
    public static async Task<List<EsclDevice>> ProbeAddressAsync(EsclAddress address, EsclDiagnostics? log = null, CancellationToken cancel = default)
    {
        IPAddress[] resolved;
        try
        {
            resolved = await EsclNetwork.ResolveLocalAsync(address.Host, cancel);
        }
        catch (ScanException e)
        {
            log?.Add("Adresse", $"{address.Host}: {e.Message}");
            return [];
        }
        IReadOnlyList<EsclProbePort> ports = (address.Port, address.Tls) switch
        {
            (null, null) => DefaultPorts,
            (null, true) => [new(443, true)],
            (null, false) => [new(80, false)],
            ({ } p, null) => [new(p, true), new(p, false)],
            ({ } p, { } tls) => [new(p, tls)],
        };
        var found = new List<(EsclService Service, string Uuid)>();
        foreach (var ip in resolved)
            foreach (var port in ports)
                if (await ProbeAsync(ip, port, Timeouts, cancel, address.Root ?? "eSCL") is { } hit)
                    found.Add(hit);
        var devices = found.GroupBy(f => f.Uuid, StringComparer.OrdinalIgnoreCase)
            .Select(g => new EsclDevice(g.Key, g.First().Service.Name, [.. g.Select(f => f.Service).OrderByDescending(s => s.Tls)])).ToList();
        log?.Add("Adresse", $"{address.Host}: {(devices.Count == 0 ? "kein eSCL-Scanner" : string.Join(", ", devices.Select(d => d.Name)))}");
        return devices;
    }

    private static async Task<(EsclService Service, string Uuid)?> ProbeAsync(IPAddress address, EsclProbePort port, EsclTimeouts timeouts, CancellationToken cancel,
        string root = "eSCL")
    {
        try
        {
            using var client = new EsclClient(new IPEndPoint(address, port.Port), port.Tls, root, address.ToString(), null, EsclTrustMode.ReadOnly, null, timeouts);
            var answer = await client.GetAsync("ScannerCapabilities", EsclCapabilities.MaxBytes, cancel);
            if (answer.Status != 200 || EsclCapabilities.Read(answer.Body) is not { Uuid: { } uuid } caps) return null;
            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["uuid"] = uuid, ["rs"] = root };
            if (caps.MakeAndModel is { } name) texts["ty"] = name;
            return (new EsclService(caps.MakeAndModel ?? address.ToString(), port.Tls, address.ToString(), port.Port, [address], texts), uuid);
        }
        catch (Exception e) when (e is ScanException or OperationCanceledException)
        {
            return null;
        }
    }
}
