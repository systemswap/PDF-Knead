using System.Globalization;
using System.Net;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>
/// A scanner address typed by the user („Scanner über Adresse hinzufügen …“, KONZEPT.md §5.25, S9): a host name or IP address, optionally
/// with port, scheme and resource path — „192.168.1.20“, „scanner.local:8080“, „[fe80::1%2]“, „https://192.168.1.20/eSCL“. Without a scheme
/// the access tries TLS first and then plain HTTP. Whether the address lies in the own network is checked when it is used
/// (<see cref="EsclNetwork.ResolveLocalAsync"/>), because a name can only be judged by its addresses.
/// </summary>
/// <param name="Host">The host name or address text (IPv6 without brackets).</param>
/// <param name="Port">The port, or null for the default of the scheme.</param>
/// <param name="Tls">True for https, false for http, null if not given.</param>
/// <param name="Root">The resource path, or null for the default „eSCL“.</param>
public sealed record EsclAddress(string Host, int? Port, bool? Tls, string? Root)
{
    /// <summary>Reads a typed address.</summary>
    /// <param name="input">What the user typed.</param>
    /// <returns>The address, or null if the text is not one (with credentials, a query, an unknown scheme or an unsafe path).</returns>
    public static EsclAddress? Parse(string input)
    {
        var text = input.Trim();
        if (text.Length is 0 or > 512 || text.Any(char.IsWhiteSpace) || text.Any(char.IsControl)) return null;
        bool? tls = null;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme == Uri.UriSchemeHttps) tls = true;
            else if (uri.Scheme == Uri.UriSchemeHttp) tls = false;
            else return null;
            if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
            var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.Host;
            if (host.Length == 0) return null;
            string? root = null;
            var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
            if (path.Length > 0 && (root = EsclSearch.SafeRoot(path)) is null) return null;
            return new EsclAddress(host, uri.IsDefaultPort ? null : uri.Port, tls, root);
        }
        // without a scheme: host, host:port, [ipv6], [ipv6]:port, or a bare IPv6 address
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 2) return null;
            var inside = text[1..close];
            var rest = text[(close + 1)..];
            if (!IPAddress.TryParse(inside, out _)) return null;
            if (rest.Length == 0) return new EsclAddress(inside, null, null, null);
            return rest.StartsWith(':') && ParsePort(rest[1..]) is { } p ? new EsclAddress(inside, p, null, null) : null;
        }
        if (text.Count(c => c == ':') > 1) return IPAddress.TryParse(text, out _) ? new EsclAddress(text, null, null, null) : null;
        var colon = text.IndexOf(':');
        var name = colon < 0 ? text : text[..colon];
        if (!ValidHost(name)) return null;
        if (colon < 0) return new EsclAddress(name, null, null, null);
        return ParsePort(text[(colon + 1)..]) is { } port ? new EsclAddress(name, port, null, null) : null;
    }

    private static int? ParsePort(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;

    private static bool ValidHost(string name) =>
        name.Length is > 0 and <= 253 && name.Split('.').All(l => l.Length is > 0 and <= 63 && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
}
