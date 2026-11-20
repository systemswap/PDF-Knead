namespace PdfKnead.Core.Scanning.Escl;

/// <summary>A scanner certificate the user confirmed.</summary>
/// <param name="Device">The device: its UUID, else host and port.</param>
/// <param name="Fingerprint">The SHA-256 fingerprint (<see cref="EsclTrust.Fingerprint"/>).</param>
public sealed record EsclPinnedCertificate(string Device, string Fingerprint);

/// <summary>A scanner found by „Netz durchsuchen“, with the host name of its address (null if the network gave none).</summary>
public sealed record EsclFoundScanner(EsclDevice Device, string? HostName);

/// <summary>
/// A scanner the user took from the result of „Netz durchsuchen“ into the list of scanners (green check; docs/FEHLERGEDAECHTNIS.md, No. 926):
/// asked directly at its address at every search, instead of the whole network.
/// </summary>
/// <param name="Device">The device's UUID (<see cref="EsclDevice.Key"/>); an answer at the address with another UUID does not count.</param>
/// <param name="Name">The make and model as found, for the list before the next search.</param>
/// <param name="Address">Where it was found, as <see cref="EsclAddress.Parse"/> reads it (scheme, address, port, resource path).</param>
/// <param name="HostName">The host name of the address when it was found (<see cref="EsclHostNames"/>), for the display; null if none.</param>
public sealed record EsclChosenScanner(string Device, string Name, string Address, string? HostName = null)
{
    /// <summary>The entry for a scanner found by the network search: its first service (TLS first) at its first address.</summary>
    /// <param name="device">The scanner.</param>
    /// <param name="hostName">The host name of that address, or null.</param>
    /// <returns>The entry, or null if the device has no service with an address.</returns>
    public static EsclChosenScanner? Of(EsclDevice device, string? hostName = null)
    {
        foreach (var service in device.Services)
            if (service.Addresses.FirstOrDefault() is { } address)
            {
                var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
                var text = $"{(service.Tls ? "https" : "http")}://{host}:{service.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{service.Root}";
                return EsclAddress.Parse(text) is null ? null : new EsclChosenScanner(device.Key, EsclXml.Clean(device.Name), text, EsclHostNames.Clean(hostName));
            }
        return null;
    }
}

/// <summary>
/// The settings of the eSCL access, kept with the computer's settings (not in a scan profile: networks and addresses belong to the place,
/// KONZEPT.md §5.25, S9).
/// </summary>
/// <param name="Direct">„Netzwerk-Scanner direkt ansprechen“ — off: only SANE or WIA, exactly as without the eSCL access.</param>
/// <param name="Networks">Networks searched address by address, as stored by <see cref="EsclNetworkRange.ToString"/>.</param>
/// <param name="Addresses">Single scanner addresses typed by the user (<see cref="EsclAddress.Parse"/>).</param>
/// <param name="Certificates">The confirmed certificates.</param>
/// <param name="Chosen">The scanners taken from the network search into the list of scanners (No. 926).</param>
public sealed record EsclSettings(bool Direct = true, IReadOnlyList<string>? Networks = null, IReadOnlyList<string>? Addresses = null,
    IReadOnlyList<EsclPinnedCertificate>? Certificates = null, IReadOnlyList<EsclChosenScanner>? Chosen = null)
{
    /// <summary>The most networks, addresses and certificates used — also from a settings file edited by hand.</summary>
    public const int MaxEntries = 64;

    /// <summary>The networks that are still valid (derived, not saved).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<EsclNetworkRange> Ranges =>
        [.. (Networks ?? []).Take(MaxEntries).Select(n => EsclNetworkRange.Parse(n, out _)).OfType<EsclNetworkRange>().Distinct()];

    /// <summary>The addresses that are still valid (derived, not saved).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<EsclAddress> ManualAddresses => [.. (Addresses ?? []).Take(MaxEntries).Select(EsclAddress.Parse).OfType<EsclAddress>().Distinct()];

    /// <summary>The chosen scanners that are still valid, each device once (derived, not saved).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<(EsclChosenScanner Scanner, EsclAddress Address)> ChosenScanners =>
        [.. (Chosen ?? []).Take(MaxEntries)
            .Where(c => c is { Device.Length: > 0 and <= 128, Name: not null, Address: not null })
            .Select(c => (Scanner: c, Address: EsclAddress.Parse(c.Address)))
            .Where(c => c.Address is not null)
            .DistinctBy(c => c.Scanner.Device, StringComparer.OrdinalIgnoreCase)
            .Select(c => (c.Scanner, c.Address!))];

    /// <summary>The settings with a scanner taken into the list (replacing an older entry of the same device) or taken out.</summary>
    public EsclSettings WithChosen(EsclChosenScanner scanner, bool chosen) => this with
    {
        Chosen = [.. (Chosen ?? []).Where(c => !string.Equals(c.Device, scanner.Device, StringComparison.OrdinalIgnoreCase))
            .TakeLast(chosen ? MaxEntries - 1 : MaxEntries), .. chosen ? new[] { scanner } : []],
    };

    /// <summary>The settings with a confirmed certificate of a device replaced.</summary>
    public EsclSettings WithCertificate(string device, string fingerprint) => this with
    {
        Certificates = [.. (Certificates ?? []).Where(c => !string.Equals(c.Device, device, StringComparison.OrdinalIgnoreCase))
            .TakeLast(MaxEntries - 1), new EsclPinnedCertificate(device, fingerprint)],
    };

    /// <summary>The confirmed fingerprint of a device, or null.</summary>
    public string? CertificateOf(string device) =>
        Certificates?.LastOrDefault(c => string.Equals(c.Device, device, StringComparison.OrdinalIgnoreCase))?.Fingerprint;
}
