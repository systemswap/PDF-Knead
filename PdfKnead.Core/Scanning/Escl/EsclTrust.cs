using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>
/// The certificates the user has confirmed for scanners (KONZEPT.md §5.25, S9, „Übertragung“). Scanners mostly present certificates they
/// issued themselves, which no authority vouches for: the program shows the fingerprint at the first contact, remembers it after the user
/// confirms, and from then on accepts only this one for the device — never „accept everything“.
/// </summary>
public interface IEsclCertificatePins
{
    /// <summary>The confirmed fingerprint for a device, or null.</summary>
    /// <param name="key">The device: its UUID, else host and port.</param>
    string? Get(string key);

    /// <summary>Remembers the confirmed fingerprint for a device.</summary>
    /// <param name="key">The device.</param>
    /// <param name="fingerprint">The SHA-256 fingerprint as hexadecimal text (<see cref="EsclTrust.Fingerprint"/>).</param>
    void Set(string key, string fingerprint);
}

/// <summary>Confirmed certificates kept in memory only (tests, and until the settings keep them).</summary>
public sealed class EsclMemoryPins : IEsclCertificatePins
{
    private readonly ConcurrentDictionary<string, string> _pins = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public string? Get(string key) => _pins.TryGetValue(key, out var fingerprint) ? fingerprint : null;

    /// <inheritdoc/>
    public void Set(string key, string fingerprint) => _pins[key] = fingerprint;
}

/// <summary>How a connection treats the scanner's certificate.</summary>
public enum EsclTrustMode
{
    /// <summary>Only the confirmed certificate counts — for everything that sends settings or fetches pages.</summary>
    Pinned,
    /// <summary>
    /// Any certificate, reading only: to name a device found by searching a network before the user has confirmed its certificate. Nothing
    /// is sent but requests for its description; the client refuses anything but reading in this mode.
    /// </summary>
    ReadOnly,
}

/// <summary>Fingerprints of certificates.</summary>
public static class EsclTrust
{
    /// <summary>The SHA-256 fingerprint of a certificate as upper-case hexadecimal text without separators.</summary>
    /// <param name="certificate">The certificate's encoded form.</param>
    public static string Fingerprint(ReadOnlySpan<byte> certificate) => Convert.ToHexString(SHA256.HashData(certificate));

    /// <summary>A fingerprint for the user: pairs of hexadecimal digits separated by colons.</summary>
    public static string Display(string fingerprint) =>
        string.Join(':', Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2)));
}

/// <summary>
/// The scanner presents a certificate the user has not confirmed — at the first contact, or because it changed. The program asks the user
/// (showing <see cref="Fingerprint"/> and, if any, <see cref="Previous"/>), remembers the answer and tries again.
/// </summary>
public sealed class EsclCertificateException : ScanException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="key">The device the certificate is for.</param>
    /// <param name="fingerprint">The fingerprint presented.</param>
    /// <param name="previous">The fingerprint confirmed before, or null at the first contact.</param>
    public EsclCertificateException(string key, string fingerprint, string? previous)
        : base(previous is null
            ? Tr.T("Der Scanner weist sich mit einem Zertifikat aus, das noch nicht bestätigt ist.")
            : Tr.T("Das Zertifikat des Scanners hat sich geändert. Wurde das Gerät zurückgesetzt oder ersetzt? Sonst könnte sich ein anderes Gerät als dieser Scanner ausgeben."))
    {
        Key = key;
        Fingerprint = fingerprint;
        Previous = previous;
    }

    /// <summary>The device.</summary>
    public string Key { get; }

    /// <summary>The fingerprint presented.</summary>
    public string Fingerprint { get; }

    /// <summary>The fingerprint confirmed before, or null.</summary>
    public string? Previous { get; }
}
