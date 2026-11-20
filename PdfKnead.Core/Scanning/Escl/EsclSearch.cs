using System.Net;
using System.Net.Sockets;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>One eSCL service of a scanner as the network search found it (KONZEPT.md §5.25, S9).</summary>
/// <param name="Instance">The instance name (labels escaped as in <see cref="DnsMessage.Labels"/>), e.g. „Scanner im Flur._uscan._tcp.local“.</param>
/// <param name="Tls">Whether the service is the one with TLS.</param>
/// <param name="Host">The host name from the service location.</param>
/// <param name="Port">The port.</param>
/// <param name="Addresses">The addresses in the own network, the most trusted first (the address the answer came from).</param>
/// <param name="Texts">The key=value texts, keys in lower case; the first value of a key wins.</param>
public sealed record EsclService(string Instance, bool Tls, string Host, int Port, IReadOnlyList<IPAddress> Addresses,
    IReadOnlyDictionary<string, string> Texts)
{
    /// <summary>The device's UUID in lower case without braces, or null if the service names none.</summary>
    public string? Uuid => Texts.TryGetValue("uuid", out var u) && EsclSearch.NormalizeUuid(u) is { } n ? n : null;

    /// <summary>The make and model for display: the „ty“ text, else the first label of the instance name.</summary>
    public string Name => EsclXml.Clean(Texts.TryGetValue("ty", out var ty) && EsclXml.Clean(ty).Length > 0 ? ty
        : DnsMessage.Labels(Instance).FirstOrDefault() ?? Host);

    /// <summary>
    /// The path of the eSCL resources below the host (the „rs“ text, default „eSCL“); only letters, digits and „-_./“ in segments, never „..“
    /// — a device cannot point the program at another place with it.
    /// </summary>
    public string Root => Texts.TryGetValue("rs", out var rs) && EsclSearch.SafeRoot(rs) is { } r ? r : "eSCL";
}

/// <summary>A scanner found by the search: its services (TLS first), joined by the UUID of the device.</summary>
/// <param name="Key">The identity: the UUID, or host and port when the device names no UUID.</param>
/// <param name="Name">The make and model for display.</param>
/// <param name="Services">The services, the one with TLS first.</param>
public sealed record EsclDevice(string Key, string Name, IReadOnlyList<EsclService> Services);

/// <summary>
/// Collects the answers of a search and links them by name, not by the address an answer came from: the service type points to instances,
/// an instance has its location (host and port) and texts, a host its addresses. Rules from experience with devices: only answers count
/// (questions mirrored back are dropped by <see cref="DnsMessage.Read"/>), data in an answer never replaces the address the answer came from
/// (that address is kept as the first hint), only addresses in the own network are used, a record with time to live 0 withdraws an entry,
/// and the amounts are limited so that a flood of answers cannot grow the search without end.
/// </summary>
public sealed class EsclSearch
{
    /// <summary>The service type of eSCL without TLS.</summary>
    public const string Plain = "_uscan._tcp.local";

    /// <summary>The service type of eSCL with TLS.</summary>
    public const string Secure = "_uscans._tcp.local";

    /// <summary>The most instances one search keeps.</summary>
    public const int MaxInstances = 64;

    /// <summary>The most hosts (with addresses) one search keeps.</summary>
    public const int MaxHosts = 128;

    private const int MaxTextLength = 255, MaxAddressesPerHost = 8;

    private readonly Dictionary<string, bool> _instances = new(StringComparer.OrdinalIgnoreCase);   // instance → TLS
    private readonly Dictionary<string, (string Host, int Port)> _locations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _texts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IPAddress>> _hostAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IPAddress>> _senders = new(StringComparer.OrdinalIgnoreCase);   // instance → where its answers came from

    /// <summary>
    /// Adds one received message. Messages from outside the own network and malformed messages are ignored.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="sender">The address it came from (with the zone of the receiving interface for IPv6 link-local).</param>
    /// <returns>Whether the message was used.</returns>
    public bool Add(ReadOnlySpan<byte> message, IPAddress sender)
    {
        if (!EsclNetwork.IsLocal(sender)) return false;
        if (DnsMessage.Read(message) is not { } read) return false;
        var touched = new List<string>();
        foreach (var record in read.Records)
        {
            var withdrawn = record.Ttl == 0;
            switch ((DnsType)record.Type)
            {
                case DnsType.Ptr when record.Target is { } instance && (Same(record.Name, Plain) || Same(record.Name, Secure)) && IsService(instance):
                    if (withdrawn) Forget(instance);
                    else if (_instances.ContainsKey(instance) || _instances.Count < MaxInstances)
                    {
                        _instances[instance] = Same(record.Name, Secure);
                        touched.Add(instance);
                    }
                    break;
                case DnsType.Srv when record.Target is { } host && IsService(record.Name):
                    if (withdrawn) _locations.Remove(record.Name);
                    else if (_locations.ContainsKey(record.Name) || _locations.Count < MaxInstances)
                    {
                        _locations[record.Name] = (host, record.Port);
                        touched.Add(record.Name);
                    }
                    break;
                case DnsType.Txt when record.Texts is { } texts && IsService(record.Name):
                    if (withdrawn) _texts.Remove(record.Name);
                    else if (_texts.ContainsKey(record.Name) || _texts.Count < MaxInstances)
                    {
                        _texts[record.Name] = Texts(texts);
                        touched.Add(record.Name);
                    }
                    break;
                case DnsType.A or DnsType.Aaaa when record.Address is { } address:
                    AddAddress(record.Name, WithZone(address, sender), withdrawn);
                    break;
            }
        }
        foreach (var instance in touched.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_senders.TryGetValue(instance, out var list)) _senders[instance] = list = [];
            if (!list.Contains(sender) && list.Count < MaxAddressesPerHost) list.Add(sender);
        }
        return true;
    }

    /// <summary>The questions still open: location and texts of known instances, addresses of known hosts — asked for once more.</summary>
    public List<(string Name, DnsType Type)> OpenQuestions()
    {
        var questions = new List<(string, DnsType)>();
        foreach (var instance in _instances.Keys)
        {
            if (!_locations.ContainsKey(instance)) questions.Add((instance, DnsType.Srv));
            if (!_texts.ContainsKey(instance)) questions.Add((instance, DnsType.Txt));
        }
        foreach (var (host, _) in _locations.Values.Distinct())
            if (!_hostAddresses.ContainsKey(host))
            {
                questions.Add((host, DnsType.A));
                questions.Add((host, DnsType.Aaaa));
            }
        return questions;
    }

    /// <summary>The scanners found so far: instances with a location; services of the same device joined by its UUID.</summary>
    public List<EsclDevice> Devices()
    {
        var services = new List<EsclService>();
        foreach (var (instance, tls) in _instances)
        {
            if (!_locations.TryGetValue(instance, out var location)) continue;
            var texts = _texts.TryGetValue(instance, out var t) ? t : new Dictionary<string, string>();
            var addresses = new List<IPAddress>();
            // the address the answer came from first, then the addresses of the host — the host's own records never displace it
            if (_senders.TryGetValue(instance, out var senders)) addresses.AddRange(senders);
            if (_hostAddresses.TryGetValue(location.Host, out var own))
                addresses.AddRange(own.Where(a => !addresses.Contains(a)));
            if (addresses.Count == 0) continue;
            services.Add(new EsclService(instance, tls, location.Host, location.Port, addresses, texts));
        }
        return [.. services.GroupBy(s => s.Uuid ?? $"{s.Host}:{s.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(g => new EsclDevice(g.Key, g.First().Name, [.. g.OrderByDescending(s => s.Tls)]))];
    }

    private void Forget(string instance)
    {
        _instances.Remove(instance);
        _locations.Remove(instance);
        _texts.Remove(instance);
        _senders.Remove(instance);
    }

    private void AddAddress(string host, IPAddress address, bool withdrawn)
    {
        if (!EsclNetwork.IsLocal(address)) return;   // a device names an address outside: never used
        if (!_hostAddresses.TryGetValue(host, out var list))
        {
            if (withdrawn || _hostAddresses.Count >= MaxHosts) return;
            _hostAddresses[host] = list = [];
        }
        if (withdrawn) list.Remove(address);
        else if (!list.Contains(address) && list.Count < MaxAddressesPerHost) list.Add(address);
    }

    /// <summary>Link-local IPv6 addresses in an answer get the zone of the interface the answer arrived on — without it they cannot be reached.</summary>
    private static IPAddress WithZone(IPAddress address, IPAddress sender)
    {
        // ScopeId exists only for IPv6 (reading it from an IPv4 address throws): an IPv4 sender has no zone to give
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || !address.IsIPv6LinkLocal || address.ScopeId != 0
            || sender.AddressFamily != AddressFamily.InterNetworkV6 || sender.ScopeId == 0)
            return address;
        return new IPAddress(address.GetAddressBytes(), sender.ScopeId);
    }

    private static Dictionary<string, string> Texts(IReadOnlyList<string> texts)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            var equals = text.IndexOf('=');
            var key = (equals < 0 ? text : text[..equals]).Trim().ToLowerInvariant();
            if (key.Length == 0 || result.ContainsKey(key)) continue;   // RFC 6763, 6.4: the first occurrence counts
            var value = equals < 0 ? "" : text[(equals + 1)..];
            result[key] = value.Length > MaxTextLength ? value[..MaxTextLength] : value;
        }
        return result;
    }

    private static bool IsService(string name) =>
        name.EndsWith("." + Plain, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + Secure, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A UUID text in lower case without braces or the „urn:uuid:“ prefix; null if it is not a UUID.</summary>
    public static string? NormalizeUuid(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)) t = t[9..];
        return Guid.TryParse(t, out var guid) ? guid.ToString("D") : null;
    }

    /// <summary>The resource path of a device if it is harmless: segments of letters, digits and „-_.“, never „.“ or „..“, at most 64 characters.</summary>
    public static string? SafeRoot(string text)
    {
        var t = text.Trim().Trim('/');
        if (t.Length is 0 or > 64) return null;
        foreach (var segment in t.Split('/'))
            if (segment is "" or "." or ".." || !segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) return null;
        return t;
    }
}
