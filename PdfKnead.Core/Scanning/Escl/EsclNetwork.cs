using System.Net;
using System.Net.Sockets;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>
/// The one exception to „the program never connects anywhere“ (KONZEPT.md §5.25, S9): scanners are reached only in the own network. Every
/// address the eSCL access uses — found by the search, typed by the user, or named by a scanner in a redirect — passes this check first.
/// </summary>
public static class EsclNetwork
{
    /// <summary>
    /// Whether an address belongs to the own network: this computer (loopback — the USB bridge of the system offers USB scanners there), the
    /// private IPv4 ranges (RFC 1918), IPv4 link-local (RFC 3927), IPv6 link-local and unique local addresses (RFC 4193). Everything else —
    /// public addresses, the shared range of carriers (RFC 6598), multicast, unspecified — is not.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>Whether the eSCL access may connect to it.</returns>
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254 && b[2] != 0 && b[2] != 255);   // the first and last /24 are reserved (RFC 3927, 2.1)
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal) return true;
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC;   // fc00::/7
        }
        return false;
    }

    /// <summary>
    /// Resolves a host name or address text to addresses in the own network. A name whose addresses lie partly outside is refused as a
    /// whole: a name that also points outside is not a scanner in the own network.
    /// </summary>
    /// <param name="host">A host name or an IPv4/IPv6 address (IPv6 with or without brackets, link-local with a zone such as <c>%2</c>).</param>
    /// <param name="cancel">Stops the name lookup.</param>
    /// <returns>The addresses; never empty.</returns>
    /// <exception cref="ScanException">The name cannot be resolved, or an address lies outside the own network.</exception>
    public static async Task<IPAddress[]> ResolveLocalAsync(string host, CancellationToken cancel = default)
    {
        var text = host.Trim().TrimStart('[').TrimEnd(']');
        IPAddress[] addresses;
        if (IPAddress.TryParse(text, out var literal)) addresses = [literal];
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(text, cancel);
            }
            catch (Exception e) when (e is SocketException or ArgumentException)
            {
                throw new ScanException(Tr.T("Der Name „{0}“ lässt sich im Netzwerk nicht auflösen.", host), ScanErrorKind.Offline);
            }
        }
        if (addresses.Length == 0)
            throw new ScanException(Tr.T("Der Name „{0}“ lässt sich im Netzwerk nicht auflösen.", host), ScanErrorKind.Offline);
        if (addresses.Any(a => !IsLocal(a)))
            throw new ScanException(Tr.T("„{0}“ liegt nicht im eigenen Netzwerk. Scanner werden nur im eigenen Netzwerk angesprochen.", host));
        return addresses;
    }
}
