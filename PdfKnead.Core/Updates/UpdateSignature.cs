using System.Security.Cryptography;

namespace PdfKnead.Core.Updates;

/// <summary>
/// The signature of a version file (KONZEPT.md §5.26): ECDSA over P-256 with SHA-256, the signature as 64 bytes (r and s) in Base64 in
/// <c>version.json.sig</c>. Built into .NET, also in the AOT build — no cryptography of our own. The program knows the public key of the
/// release key (<see cref="ReleaseKey"/>); the secret key lies with the user, never in the repository and never online.
/// </summary>
public static class UpdateSignature
{
    /// <summary>
    /// The public key of the releases (SubjectPublicKeyInfo, Base64), written by <c>pdf-knead release-schluessel</c> on 18.11.2026 (the secret key lies with the user: local
    /// build folder and stick, docs/UEBERGABE.md). Empty would mean: no update is ever accepted.
    /// </summary>
    public const string ReleaseKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAExQiXH3xUem7N48MA3WyRnYkHT/RZmfeoJCaztMScjP54oSlYNUKexFdBi6OMPCUraWoAE0ED72zaoydyLua7TQ==";

    /// <summary>The largest signature file accepted.</summary>
    public const int MaxBytes = 1024;

    /// <summary>Whether <paramref name="signature"/> (the content of the .sig file) signs <paramref name="data"/> with the key.</summary>
    /// <param name="data">The version file as received, byte for byte.</param>
    /// <param name="signature">The signature file as received.</param>
    /// <param name="publicKey">The public key (Base64); <see cref="ReleaseKey"/> if null.</param>
    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, string? publicKey = null)
    {
        var key = publicKey ?? ReleaseKey;
        if (key.Length == 0 || signature.Length is 0 or > MaxBytes) return false;
        try
        {
            var sig = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(signature).Trim());
            if (sig.Length != 64) return false;
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            if (ecdsa.KeySize != 256) return false;
            return ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Signs a version file with the secret key (PKCS#8 in PEM): the content of the .sig file.</summary>
    public static byte[] Sign(ReadOnlySpan<byte> data, string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        if (ecdsa.KeySize != 256) throw new CryptographicException("Der Schlüssel ist kein P-256-Schlüssel.");
        var sig = ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(sig) + "\n");
    }

    /// <summary>A new key pair: the secret key as PKCS#8 PEM, the public key as Base64 for <see cref="ReleaseKey"/>.</summary>
    public static (string PrivatePem, string PublicBase64) CreateKey()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    }
}
