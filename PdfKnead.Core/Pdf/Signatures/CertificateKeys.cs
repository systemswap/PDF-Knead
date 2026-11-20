using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>The public key of a certificate for verifying, also where the platform gives none.</summary>
internal static class CertificateKeys
{
    /// <summary>id-RSASSA-PSS (RFC 4055, 3.1): an RSA key restricted to PSS signatures.</summary>
    private const string RsaPss = "1.2.840.113549.1.1.10";

    /// <summary>
    /// The RSA key of a certificate. A key marked for PSS only (id-RSASSA-PSS, as the German trusted list signer uses) is not handed out by
    /// .NET; its subjectPublicKey is the same RSAPublicKey structure as with rsaEncryption (RFC 4055, 1.2), so it is imported from there.
    /// The PSS parameters of such a key are not enforced here: they restrict signing, the signature's own algorithm is checked by the caller.
    /// </summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The key; null if it is no RSA key.</returns>
    public static RSA? Rsa(X509Certificate2 certificate)
    {
        if (certificate.GetRSAPublicKey() is { } rsa) return rsa;
        if (certificate.PublicKey.Oid.Value != RsaPss) return null;
        var imported = RSA.Create();
        try
        {
            imported.ImportRSAPublicKey(certificate.PublicKey.EncodedKeyValue.RawData, out _);
            return imported;
        }
        catch (CryptographicException)
        {
            imported.Dispose();
            return null;
        }
    }
}
