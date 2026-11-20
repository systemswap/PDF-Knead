using System.Security.Cryptography.X509Certificates;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>
/// Until when a method protects (ETSI TS 119 312 V2.1.1: the hash functions, elliptic curves and RSA key sizes with their end dates; the
/// basis of „erneuern bis“, docs/ABLAUFPLAN.md, 9.3.4): <c>null</c> — no end date given (recommended), <see cref="DateTimeOffset.MinValue"/> —
/// no longer suitable, otherwise the last day. SHA-224 is legacy until the end of 2028; RSA keys of 1 900 to below 3 000 bits are legacy and
/// their certificates end by the end of 2028 at the latest; below 1 900 bits, SHA-1 and MD5 are no longer suitable. Unknown methods: no
/// statement (<c>null</c>), the check of the method itself says what it knows (<see cref="SignatureVerifier"/>).
/// </summary>
internal static class AlgorithmSuitability
{
    // the last day, at noon UTC: the same calendar day in every time zone it is shown in
    private static readonly DateTimeOffset EndOf2028 = new(2028, 12, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A hash function by its object identifier.</summary>
    internal static DateTimeOffset? Hash(string? oid) => oid switch
    {
        "2.16.840.1.101.3.4.2.4" => EndOf2028,   // SHA-224
        "2.16.840.1.101.3.4.2.1" or "2.16.840.1.101.3.4.2.2" or "2.16.840.1.101.3.4.2.3"   // SHA-256, -384, -512
            or "2.16.840.1.101.3.4.2.8" or "2.16.840.1.101.3.4.2.9" or "2.16.840.1.101.3.4.2.10" => null,   // SHA3-256, -384, -512
        "1.3.14.3.2.26" or "1.2.840.113549.2.5" or "1.2.840.113549.2.2" => DateTimeOffset.MinValue,   // SHA-1, MD5, MD2
        _ => null,
    };

    /// <summary>The key of a certificate: RSA by its length, elliptic curves of at least 256 bits and EdDSA without an end date.</summary>
    internal static DateTimeOffset? Key(X509Certificate2 certificate)
    {
        using (var rsa = certificate.GetRSAPublicKey())
            if (rsa is not null)
                return rsa.KeySize < 1900 ? DateTimeOffset.MinValue : rsa.KeySize < 3000 ? EndOf2028 : null;
        using (var ec = certificate.GetECDsaPublicKey())
            if (ec is not null)
                return ec.KeySize < 256 ? DateTimeOffset.MinValue : null;
        return null;
    }

    /// <summary>The earliest of the given dates; null if none is given.</summary>
    internal static DateTimeOffset? Earliest(params DateTimeOffset?[] dates) =>
        dates.Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty(DateTimeOffset.MaxValue).Min() is var min && min != DateTimeOffset.MaxValue ? min : null;
}
