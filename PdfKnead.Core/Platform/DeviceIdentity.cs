using System.Security.Cryptography;

namespace PdfKnead.Core.Platform;

/// <summary>
/// Key of the device identity: one key pair (ECDSA P-256) per user and computer, created on first
/// use. It signs the origin of a signature (name, date, code); anyone who knows the public key
/// can later verify that it comes from this device. The private key never leaves the computer:
/// on Windows it is bound to the user with DPAPI, otherwise stored as a file that only the user may
/// read. No network connection.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private static readonly byte[] Entropy = "PDF-Knead Geräte-Kennung 1"u8.ToArray();
    private readonly ECDsa _key;

    private DeviceIdentity(ECDsa key, DeviceCode code)
    {
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        Code = code;
    }

    /// <summary>Gets the public key (SubjectPublicKeyInfo, DER).</summary>
    public byte[] PublicKey { get; }

    /// <summary>Gets the two-part code (PC-user) shown below the signature; see <see cref="DeviceCode"/>.</summary>
    public DeviceCode Code { get; }

    /// <summary>Gets the default path of the key file (geraet.key in <see cref="AppLocations.UserDataFolder"/>).</summary>
    public static string DefaultFile => Path.Combine(AppLocations.UserDataFolder, "geraet.key");

    /// <summary>Loads the key, or creates and stores a new one (the first time, or if the stored one cannot be decrypted).</summary>
    /// <param name="file">Path of the key file; null = <see cref="DefaultFile"/>.</param>
    /// <param name="code">Code; default: that of this user on this PC (tests supply their own).</param>
    /// <returns>The identity; the caller owns it and disposes of it.</returns>
    /// <remarks>
    /// If the file cannot be read at the moment, or an undecryptable file cannot be moved aside, a temporary key is used for
    /// this session and the file is left untouched. If the new key cannot be saved, it applies until the program ends.
    /// </remarks>
    public static DeviceIdentity LoadOrCreate(string? file = null, DeviceCode? code = null)
    {
        file ??= DefaultFile;
        code ??= DeviceCode.Current;
        byte[]? stored;
        var unreadable = false;
        try
        {
            stored = UserSecretFile.Read(file, Entropy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not readable at the moment (locked, e.g. by the virus scanner): a temporary key for this session;
            // the file stays untouched, and the stored key applies again at the next start.
            return new DeviceIdentity(ECDsa.Create(ECCurve.NamedCurves.nistP256), code);
        }
        catch (CryptographicException)
        {
            stored = null;
            unreadable = true;
        }
        if (stored is { Length: > 0 })
        {
            var key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(stored, out _);
                return new DeviceIdentity(key, code);
            }
            catch (CryptographicException)
            {
                key.Dispose();
                unreadable = true;
            }
        }
        // Really unreadable (other user, damaged): new key; the old one is kept with a time stamp (never overwritten, not even an
        // earlier backup copy). If it cannot be moved aside, overwrite nothing and use a temporary key.
        if (unreadable && !UserSecretFile.TryBackUp(file)) return new DeviceIdentity(ECDsa.Create(ECCurve.NamedCurves.nistP256), code);
        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        UserSecretFile.TrySave(file, created.ExportPkcs8PrivateKey(), Entropy);   // cannot be saved: the key applies until the program ends
        return new DeviceIdentity(created, code);
    }

    /// <summary>Returns the public key of this device if one has already been created (does not create one).</summary>
    /// <param name="file">Path of the key file; null = <see cref="DefaultFile"/>.</param>
    /// <returns>The public key (SubjectPublicKeyInfo, DER), or null if there is no readable key file.</returns>
    public static byte[]? ExistingPublicKey(string? file = null)
    {
        file ??= DefaultFile;
        try
        {
            if (UserSecretFile.Read(file, Entropy) is not { } stored) return null;
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(stored, out _);
            return key.ExportSubjectPublicKeyInfo();
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Returns a short fingerprint of a public key (SHA-256, first 8 bytes, in groups of four hexadecimal digits).</summary>
    /// <param name="publicKey">The public key (SubjectPublicKeyInfo, DER).</param>
    /// <returns>Four groups separated by spaces, e.g. “1A2B 3C4D 5E6F 7A8B”.</returns>
    public static string Fingerprint(byte[] publicKey)
    {
        var hex = Convert.ToHexString(SHA256.HashData(publicKey), 0, 8);
        return string.Join(' ', Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>Signs data (ECDSA P-256 with SHA-256, IEEE P1363 format: r ‖ s).</summary>
    /// <param name="data">The data to sign.</param>
    /// <returns>The signature (64 bytes).</returns>
    public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256);

    /// <summary>Verifies a signature with a public key.</summary>
    /// <param name="publicKey">The public key (SubjectPublicKeyInfo, DER).</param>
    /// <param name="data">The signed data.</param>
    /// <param name="signature">The signature in IEEE P1363 format.</param>
    /// <returns><see langword="true"/> if the signature is valid; <see langword="false"/> if not or if the key cannot be read.</returns>
    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Releases the private key.</summary>
    public void Dispose() => _key.Dispose();
}
