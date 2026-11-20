using System.Globalization;
using System.Net;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>
/// A network whose addresses are tried one by one when the multicast search cannot reach the scanners — in companies they often sit in a
/// network of their own behind a router (KONZEPT.md §5.25, S9, „Netz der Scanner durchsuchen“, Festlegung). Only private IPv4
/// networks of at most 254 hosts: anything larger would take long and look like an attack to the monitoring of a company network.
/// </summary>
/// <param name="Network">The network address (host part zero).</param>
/// <param name="PrefixLength">The prefix length, 24 … 30.</param>
public sealed record EsclNetworkRange(IPAddress Network, int PrefixLength)
{
    /// <summary>The shortest prefix accepted (254 hosts).</summary>
    public const int ShortestPrefix = 24;

    /// <summary>The longest prefix accepted (2 hosts).</summary>
    public const int LongestPrefix = 30;

    /// <summary>The host addresses: all but the network and the broadcast address.</summary>
    public IEnumerable<IPAddress> Hosts
    {
        get
        {
            var first = ToNumber(Network);
            var count = 1u << (32 - PrefixLength);
            for (var i = 1u; i < count - 1; i++) yield return FromNumber(first + i);
        }
    }

    /// <summary>How many host addresses.</summary>
    public int HostCount => (1 << (32 - PrefixLength)) - 2;

    /// <summary>The network as the user reads it: „192.168.10.1–254“.</summary>
    public string Display
    {
        get
        {
            var hosts = Hosts.ToList();
            var last = hosts[^1].GetAddressBytes()[3];
            return $"{hosts[0]}–{last.ToString(CultureInfo.InvariantCulture)}";
        }
    }

    /// <summary>The network as it is stored: „192.168.10.0/24“.</summary>
    public override string ToString() => $"{Network}/{PrefixLength.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Reads one network as typed: „192.168.10“, „192.168.10.0“, „192.168.10.x“, „192.168.10.*“, „192.168.10.0/24“; any host part is replaced
    /// by the network (with the last part given, the /24 around that address).
    /// </summary>
    /// <param name="text">What the user typed.</param>
    /// <param name="error">Why the text is not accepted (translated), or null.</param>
    /// <returns>The network, or null.</returns>
    public static EsclNetworkRange? Parse(string text, out string? error)
    {
        var t = text.Trim();
        error = null;
        var prefix = ShortestPrefix;
        var slash = t.IndexOf('/');
        if (slash >= 0)
        {
            if (!int.TryParse(t[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix is < 0 or > 32)
                return Fail(text, out error);
            t = t[..slash];
        }
        var parts = t.Split('.');
        if (parts.Length == 4 && parts[3] is "x" or "X" or "*") parts = parts[..3];
        if (parts.Length is not (3 or 4) || parts.Any(p => !byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _) || p.Length > 3))
            return Fail(text, out error);
        if (parts.Length == 3) parts = [.. parts, "0"];
        var address = IPAddress.Parse(string.Join('.', parts));
        if (prefix < ShortestPrefix)
        {
            error = Tr.T("„{0}“ ist zu groß — bitte ein Netz mit höchstens 254 Adressen, zum Beispiel 192.168.178.0.", text.Trim());
            return null;
        }
        if (prefix > LongestPrefix) return Fail(text, out error);
        if (IPAddress.IsLoopback(address) || !EsclNetwork.IsLocal(address))
        {
            error = Tr.T("„{0}“ liegt nicht im eigenen Netzwerk. Scanner werden nur im eigenen Netzwerk angesprochen.", text.Trim());
            return null;
        }
        var mask = uint.MaxValue << (32 - prefix);
        return new EsclNetworkRange(FromNumber(ToNumber(address) & mask), prefix);
    }

    /// <summary>
    /// Reads the networks of the text field: separated by commas, semicolons, blanks or lines; repeated ones once.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="errors">What was not accepted (translated), in the order typed.</param>
    /// <returns>The networks accepted, in the order typed.</returns>
    public static List<EsclNetworkRange> ParseList(string text, out List<string> errors)
    {
        errors = [];
        var ranges = new List<EsclNetworkRange>();
        foreach (var piece in text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(64))
        {
            if (Parse(piece, out var error) is { } range)
            {
                if (!ranges.Contains(range)) ranges.Add(range);
            }
            else errors.Add(error!);
        }
        return ranges;
    }

    private static EsclNetworkRange? Fail(string text, out string? error)
    {
        error = Tr.T("„{0}“ ist kein Netz. Beispiel: 192.168.178.0", text.Trim());
        return null;
    }

    private static uint ToNumber(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress FromNumber(uint n) => new([(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]);
}
