using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>
/// Looks for eSCL scanners in the own network (KONZEPT.md §5.25, S9, „Gerätesuche“; decided by the user on 17.11.2026). The questions go
/// from a free port to the multicast addresses of multicast DNS, as a simple querier does (RFC 6762, 6.7); the devices answer directly to
/// that port. Nothing listens on port 5353 — no conflict with the multicast DNS service of the system, no incoming firewall rule, no system
/// service needed. One socket per interface and address family, so that every network is asked through its own interface and the zone of
/// link-local IPv6 answers is known. Only answers carrying the id of an own question count.
/// </summary>
public static class EsclDiscovery
{
    /// <summary>How long a search listens.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(3);

    private static readonly IPAddress GroupV4 = IPAddress.Parse("224.0.0.251");
    private static readonly IPAddress GroupV6 = IPAddress.Parse("ff02::fb");
    private const int Port = 5353;

    /// <summary>Searches the own network for eSCL scanners.</summary>
    /// <param name="log">Where search hits and problems are noted, or null.</param>
    /// <param name="duration">How long to listen; <see cref="DefaultDuration"/> if null.</param>
    /// <param name="cancel">Stops the search; the devices found so far are returned.</param>
    /// <returns>The scanners found.</returns>
    public static async Task<List<EsclDevice>> SearchAsync(EsclDiagnostics? log = null, TimeSpan? duration = null, CancellationToken cancel = default)
    {
        var search = new EsclSearch();
        var gate = new Lock();
        var ids = new HashSet<ushort>();
        var sockets = Open(log);
        log?.Add("Suche", sockets.Count == 0 ? "keine Netzwerkschnittstelle mit Multicast gefunden"
            : $"frage über {sockets.Count} Schnittstelle(n): {string.Join(", ", sockets.Select(s => s.Description))}");
        if (sockets.Count == 0) return [];
        using var listen = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        listen.CancelAfter(duration ?? DefaultDuration);
        try
        {
            var receivers = sockets.Select(s => ReceiveAsync(s, search, gate, ids, log, listen.Token)).ToList();
            // the first question twice (a lost packet), then what is still open: location, texts and addresses of the instances found
            Send(sockets, ids, gate, [(EsclSearch.Plain, DnsType.Ptr), (EsclSearch.Secure, DnsType.Ptr)], log);
            var total = duration ?? DefaultDuration;
            foreach (var share in new[] { 0.25, 0.55 })
            {
                try
                {
                    await Task.Delay(total * share, listen.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                List<(string, DnsType)> open;
                lock (gate) open = search.OpenQuestions();
                Send(sockets, ids, gate, open.Count > 0 ? open.Take(32).ToList() : [(EsclSearch.Plain, DnsType.Ptr), (EsclSearch.Secure, DnsType.Ptr)], log);
            }
            await Task.WhenAll(receivers);
        }
        finally
        {
            foreach (var s in sockets) s.Client.Dispose();
        }
        lock (gate)
        {
            var devices = search.Devices();
            foreach (var d in devices)
                log?.Add("Suche", $"gefunden: {d.Name} ({d.Key}) — " + string.Join("; ",
                    d.Services.Select(s => $"{(s.Tls ? "https" : "http")} {s.Host}:{s.Port}/{s.Root} [{string.Join(", ", s.Addresses)}]")));
            if (devices.Count == 0) log?.Add("Suche", "kein eSCL-Scanner geantwortet");
            return devices;
        }
    }

    private sealed record Channel(UdpClient Client, IPEndPoint Target, string Description);

    private static List<Channel> Open(EsclDiagnostics? log)
    {
        var channels = new List<Channel>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException e)
        {
            log?.Add("Suche", "Schnittstellen nicht lesbar: " + e.Message);
            return channels;
        }
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            var v4 = properties.UnicastAddresses.Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && EsclNetwork.IsLocal(a) && !IPAddress.IsLoopback(a));
            if (v4 is not null && TryOpen(v4, () =>
                {
                    var client = new UdpClient(new IPEndPoint(v4, 0));
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, v4.GetAddressBytes());
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    return client;
                }, log) is { } c4)
                channels.Add(new Channel(c4, new IPEndPoint(GroupV4, Port), $"{nic.Name} {v4}"));
            if (!nic.Supports(NetworkInterfaceComponent.IPv6)) continue;
            var v6 = properties.UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6LinkLocal);
            int index;
            try
            {
                index = properties.GetIPv6Properties().Index;
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            if (v6 is not null && TryOpen(v6, () =>
                {
                    var client = new UdpClient(new IPEndPoint(v6, 0));
                    client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, index);
                    client.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
                    return client;
                }, log) is { } c6)
                channels.Add(new Channel(c6, new IPEndPoint(new IPAddress(GroupV6.GetAddressBytes(), index), Port), $"{nic.Name} {v6}"));
        }
        return channels;
    }

    private static UdpClient? TryOpen(IPAddress address, Func<UdpClient> open, EsclDiagnostics? log)
    {
        try
        {
            return open();
        }
        catch (SocketException e)
        {
            log?.Add("Suche", $"Socket an {address} nicht geöffnet: {e.SocketErrorCode}");
            return null;
        }
    }

    private static void Send(List<Channel> channels, HashSet<ushort> ids, Lock gate, IReadOnlyList<(string, DnsType)> questions, EsclDiagnostics? log)
    {
        ushort id;
        lock (gate)
        {
            do id = (ushort)Random.Shared.Next(1, ushort.MaxValue); while (!ids.Add(id));
        }
        byte[] query;
        try
        {
            query = DnsMessage.Query(id, questions);
        }
        catch (ArgumentException e)
        {
            log?.Add("Suche", "Frage nicht gebaut: " + e.Message);
            return;
        }
        foreach (var channel in channels)
        {
            try
            {
                channel.Client.Send(query, query.Length, channel.Target);
            }
            catch (SocketException e)
            {
                log?.Add("Suche", $"Senden über {channel.Description} gescheitert: {e.SocketErrorCode}");
            }
        }
    }

    private static async Task ReceiveAsync(Channel channel, EsclSearch search, Lock gate, HashSet<ushort> ids, EsclDiagnostics? log, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await channel.Client.ReceiveAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                if (!cancel.IsCancellationRequested) log?.Add("Suche", $"Empfang über {channel.Description} beendet: {e.Message}");
                return;
            }
            if (received.Buffer.Length < 2) continue;
            var id = (ushort)(received.Buffer[0] << 8 | received.Buffer[1]);
            lock (gate)
            {
                if (!ids.Contains(id)) continue;   // not an answer to an own question
                if (!search.Add(received.Buffer, received.RemoteEndPoint.Address))
                    log?.Add("Suche", $"Antwort von {received.RemoteEndPoint.Address} verworfen (außerhalb des eigenen Netzwerks oder fehlerhaft)");
            }
        }
    }
}
