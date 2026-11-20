using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace PdfKnead.Core.Platform;

/// <summary>Where a signing certificate comes from.</summary>
public enum SigningCertificateSource
{
    /// <summary>Created by PDF-Knead for this user on this computer (self-signed).</summary>
    Own,

    /// <summary>A PKCS#12 file (.pfx, .p12) chosen by the user.</summary>
    File,

    /// <summary>The personal certificate store of Windows (also smart cards registered there).</summary>
    Store,

    /// <summary>A signature card or token through its PKCS#11 driver (Linux); the key stays on the card.</summary>
    Token,
}

/// <summary>
/// A certificate with its private key for digital signatures (KONZEPT.md, section 5.17, step S2). The caller owns it and
/// disposes of it, which releases the private key.
/// </summary>
public sealed class SigningCertificate : IDisposable
{
    internal SigningCertificate(X509Certificate2 certificate, SigningCertificateSource source, IReadOnlyList<X509Certificate2>? chain = null,
        AsymmetricAlgorithm? key = null)
    {
        Certificate = certificate;
        Source = source;
        Chain = chain ?? [];
        Key = key;
    }

    /// <summary>
    /// The private key when it is not part of <see cref="Certificate"/> (a key on a signature card, which signs on the card); null when the
    /// certificate carries it.
    /// </summary>
    internal AsymmetricAlgorithm? Key { get; }

    /// <summary>
    /// Further certificates that came with it (intermediate authorities and root of a PKCS#12 file): they form the chain embedded in the
    /// signature, so that readers can confirm the identity without fetching anything.
    /// </summary>
    public IReadOnlyList<X509Certificate2> Chain { get; }

    /// <summary>The certificate; it has a private key.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>Where it comes from.</summary>
    public SigningCertificateSource Source { get; }

    /// <summary>Name of the holder (common name of the subject).</summary>
    public string Name => Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

    /// <summary>Last day of validity, local time.</summary>
    public DateTime ValidUntil => Certificate.NotAfter;

    /// <summary>Whether subject and issuer are the same, so no third party confirmed the name.</summary>
    public bool SelfSigned => Certificate.SubjectName.RawData.AsSpan().SequenceEqual(Certificate.IssuerName.RawData);

    /// <summary>
    /// Fingerprint for passing on (SHA-256 of the certificate, in groups of four hexadecimal digits); whoever compares it with the
    /// one shown by a PDF reader knows that the signature was made with this certificate.
    /// </summary>
    public string Fingerprint => FingerprintOf(Certificate);

    /// <summary>Formats the SHA-256 fingerprint of a certificate in groups of four hexadecimal digits.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>16 groups separated by spaces.</returns>
    public static string FingerprintOf(X509Certificate2 certificate)
    {
        var hex = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>Releases the private key (for a card: logs out).</summary>
    public void Dispose()
    {
        Key?.Dispose();
        Certificate.Dispose();
        foreach (var certificate in Chain) certificate.Dispose();
    }
}

/// <summary>
/// Finds, creates and loads signing certificates: the user's own self-signed one (stored like the device identity, with an optional
/// password of its own), a PKCS#12 file with password, and on Windows the personal certificate store. No network connection.
/// </summary>
public static class SigningCertificates
{
    private static readonly byte[] Entropy = "PDF-Knead Signaturzertifikat 1"u8.ToArray();

    /// <summary>Key size of the own certificate: RSA 3072 with SHA-256 is checked by all common PDF readers.</summary>
    public const int OwnKeySize = 3072;

    /// <summary>Validity of the own certificate in years.</summary>
    public const int OwnValidityYears = 10;

    /// <summary>Gets the default path of the own certificate (signatur.p12 in <see cref="AppLocations.UserDataFolder"/>).</summary>
    public static string OwnFile => Path.Combine(AppLocations.UserDataFolder, "signatur.p12");

    /// <summary>
    /// All own certificates (decided on 11.11.2026: several, each with its own row): the first at <paramref name="first"/>, further ones
    /// beside it as „signatur-2.p12“, „signatur-3.p12“ … in the order of their numbers. Copies moved aside when one was replaced
    /// („….unlesbar-…“) are not among them.
    /// </summary>
    /// <param name="first">Path of the first one; null = <see cref="OwnFile"/>.</param>
    /// <returns>The paths of the files that exist.</returns>
    public static IReadOnlyList<string> OwnFiles(string? first = null)
    {
        first ??= OwnFile;
        var result = new List<string>();
        if (File.Exists(first)) result.Add(first);
        var folder = Path.GetDirectoryName(first);
        if (folder is null || !Directory.Exists(folder)) return result;
        var stem = Path.GetFileNameWithoutExtension(first);
        var extension = Path.GetExtension(first);
        result.AddRange(Directory.EnumerateFiles(folder, stem + "-*" + extension)
            .Select(path => (Number: int.TryParse(Path.GetFileNameWithoutExtension(path).AsSpan(stem.Length + 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0, Path: path))
            .Where(f => f.Number >= 2)
            .OrderBy(f => f.Number)
            .Select(f => f.Path));
        return result;
    }

    /// <summary>The file for one more own certificate: the first if there is none there, otherwise the next free number beside it.</summary>
    /// <param name="first">Path of the first one; null = <see cref="OwnFile"/>.</param>
    public static string NextOwnFile(string? first = null)
    {
        first ??= OwnFile;
        if (!File.Exists(first)) return first;
        var folder = Path.GetDirectoryName(first) ?? "";
        var stem = Path.GetFileNameWithoutExtension(first);
        var extension = Path.GetExtension(first);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{stem}-{n}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Shortest password of the own certificate.</summary>
    public const int MinOwnPassword = 8;

    /// <summary>
    /// Iterations of PBKDF2-SHA256 for the password of the own certificate: about a tenth of a second per attempt here, well below the limit
    /// of what loaders accept (300 000).
    /// </summary>
    private const int OwnPasswordIterations = 200_000;

    /// <summary>Whether there is an own certificate and whether it needs its password (KONZEPT.md, section 5.17, „Passwort“).</summary>
    /// <param name="file">Path of the file; null = <see cref="OwnFile"/>.</param>
    /// <returns>Missing (also unreadable: a new one can be created), ready or locked by a password.</returns>
    public static OwnCertificateState OwnState(string? file = null)
    {
        var data = ReadOwn(file);
        try
        {
            return data is null ? OwnCertificateState.Missing : Protected(data) ? OwnCertificateState.Locked
                : Open(data, null) is { } own ? Dispose(own, OwnCertificateState.Ready) : OwnCertificateState.Missing;
        }
        finally
        {
            if (data is not null) CryptographicOperations.ZeroMemory(data);
        }

        static OwnCertificateState Dispose(SigningCertificate certificate, OwnCertificateState state)
        {
            certificate.Dispose();
            return state;
        }
    }

    /// <summary>Loads the own certificate if there is one.</summary>
    /// <param name="file">Path of the file; null = <see cref="OwnFile"/>.</param>
    /// <param name="password">Its password, if it has one (<see cref="OwnState"/>); null otherwise.</param>
    /// <returns>The certificate, or null if none was created yet or it cannot be read (then a new one can be created).</returns>
    /// <exception cref="CertificatePasswordException">It has a password, and none or a wrong one was given — never create a new one then.</exception>
    public static SigningCertificate? LoadOwn(string? file = null, string? password = null)
    {
        var data = ReadOwn(file);
        if (data is null) return null;
        try
        {
            if (Protected(data) && (password is null || !Pkcs12Info.Decode(data, out _).VerifyMac(password)))
                throw new CertificatePasswordException(password is null ? Tr.T("Das eigene Zertifikat ist mit einem Passwort geschützt.") : Tr.T("Das Passwort stimmt nicht."));
            return Open(data, password);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <summary>
    /// The own certificate without its private key, to show it (name, validity, fingerprint) also while it waits for its password; null if
    /// there is none or it cannot be read.
    /// </summary>
    /// <param name="file">Path of the file; null = <see cref="OwnFile"/>.</param>
    /// <returns>The certificate (it cannot sign).</returns>
    public static SigningCertificate? OwnPublic(string? file = null)
    {
        var data = ReadOwn(file);
        if (data is null) return null;
        try
        {
            if (!Protected(data)) return Open(data, null);
            foreach (var contents in Pkcs12Info.Decode(data, out _).AuthenticatedSafe)
                if (contents.ConfidentialityMode == Pkcs12ConfidentialityMode.None)
                    foreach (var bag in contents.GetBags())
                        if (bag is Pkcs12CertBag { IsX509Certificate: true } cert)
                            return new SigningCertificate(cert.GetCertificate(), SigningCertificateSource.Own);
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <summary>
    /// Sets, changes or removes the password of the own certificate. With a password its private key is stored encrypted with it (PBKDF2
    /// with SHA-256, AES-256) — on top of what protects it anyway (Windows: bound to the user account; Linux: file permissions); the
    /// certificate itself stays readable to be shown. A forgotten password cannot be recovered: then a new certificate is needed (signatures
    /// already made stay valid).
    /// </summary>
    /// <param name="current">The current password; null if it has none.</param>
    /// <param name="next">The new password; null or empty removes it.</param>
    /// <param name="file">Path of the file; null = <see cref="OwnFile"/>.</param>
    /// <returns>false if there is no own certificate or it could not be stored.</returns>
    /// <exception cref="ArgumentException">The new password is shorter than <see cref="MinOwnPassword"/>.</exception>
    /// <exception cref="CertificatePasswordException">The current password is missing or wrong.</exception>
    public static bool SetOwnPassword(string? current, string? next, string? file = null)
    {
        if (!string.IsNullOrEmpty(next) && next.Length < MinOwnPassword)
            throw new ArgumentException(Tr.T("Das Passwort braucht mindestens {0} Zeichen.", MinOwnPassword), nameof(next));
        file ??= OwnFile;
        var data = ReadOwn(file);
        if (data is null) return false;
        try
        {
            if (Protected(data) && (current is null || !Pkcs12Info.Decode(data, out _).VerifyMac(current)))
                throw new CertificatePasswordException(Tr.T("Das bisherige Passwort stimmt nicht."));
            using var certificate = X509CertificateLoader.LoadPkcs12(data, Protected(data) ? current : null, Flags | X509KeyStorageFlags.Exportable);
            if (!certificate.HasPrivateKey) return false;
            var packed = Pack(certificate, string.IsNullOrEmpty(next) ? null : next);
            try
            {
                return UserSecretFile.TrySave(file, packed, Entropy);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(packed);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private static byte[]? ReadOwn(string? file)
    {
        try
        {
            return UserSecretFile.Read(file ?? OwnFile, Entropy) is { Length: > 0 } data ? data : null;
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether the stored PKCS#12 data needs a password (its integrity check fails without one).</summary>
    private static bool Protected(byte[] data)
    {
        try
        {
            var info = Pkcs12Info.Decode(data, out _);
            return info.IntegrityMode == Pkcs12IntegrityMode.Password && !info.VerifyMac(null);
        }
        catch (CryptographicException)
        {
            return false;   // unreadable: counts as missing, as before
        }
    }

    /// <summary>The certificate with its key from the stored data; null if it cannot be read or has no key.</summary>
    private static SigningCertificate? Open(byte[] data, string? password)
    {
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12(data, password, Flags);
            if (certificate.HasPrivateKey) return new SigningCertificate(certificate, SigningCertificateSource.Own);
            certificate.Dispose();
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// The PKCS#12 data of the own certificate: without a password as before; with one, the key encrypted with it and the certificate in
    /// the clear (to be shown before the password is given), both marked with the same local key identifier, sealed with the password.
    /// </summary>
    private static byte[] Pack(X509Certificate2 certificate, string? password)
    {
        if (password is null) return certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password: null);
        using var key = certificate.GetRSAPrivateKey() ?? throw new CryptographicException("Only RSA keys are stored with a password.");
        var id = new Pkcs9LocalKeyId(SHA256.HashData(certificate.RawData));
        var parameters = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, OwnPasswordIterations);
        var certs = new Pkcs12SafeContents();
        certs.AddCertificate(X509CertificateLoader.LoadCertificate(certificate.RawData)).Attributes.Add(id);
        var keys = new Pkcs12SafeContents();
        keys.AddShroudedKey(key, password, parameters).Attributes.Add(id);
        var builder = new Pkcs12Builder();
        builder.AddSafeContentsUnencrypted(certs);
        builder.AddSafeContentsUnencrypted(keys);
        builder.SealWithMac(password, HashAlgorithmName.SHA256, OwnPasswordIterations);
        return builder.Encode();
    }

    /// <summary>
    /// Creates the own certificate for a name and stores it (replacing an earlier one, which is moved aside). Self-signed, RSA
    /// 3072, valid for <see cref="OwnValidityYears"/> years, key usage “digital signature” and “non-repudiation”.
    /// </summary>
    /// <param name="name">Name of the holder, as it is to appear in the signature.</param>
    /// <param name="file">Path of the file; null = <see cref="OwnFile"/>.</param>
    /// <returns>The certificate; if it cannot be stored, it applies until the program ends.</returns>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    public static SigningCertificate CreateOwn(string name, string? file = null)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException(Tr.T("Für das Zertifikat fehlt der Name."), nameof(name));
        if (name.Length > 64) name = name[..64];   // the common name allows at most 64 characters (RFC 5280, Appendix A)
        file ??= OwnFile;
        using var key = RSA.Create(OwnKeySize);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(name);
        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var from = DateTimeOffset.UtcNow.AddMinutes(-5);   // tolerate a slightly wrong clock of the reader
        using var created = request.CreateSelfSigned(from, from.AddYears(OwnValidityYears));
        var data = created.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password: null);
        if (File.Exists(file)) UserSecretFile.TryBackUp(file);
        UserSecretFile.TrySave(file, data, Entropy);
        return new SigningCertificate(X509CertificateLoader.LoadPkcs12(data, password: null, Flags), SigningCertificateSource.Own);
    }

    /// <summary>Shortest password for an exported certificate.</summary>
    public const int MinExportPassword = 8;

    /// <summary>
    /// Writes the own certificate with its private key as an ordinary PKCS#12 file protected by a password (AES-256, SHA-256): to keep
    /// it as a backup or to sign with it on another computer („Zertifikat aus einer Datei“). The stored one is bound to this computer
    /// (Windows) or protected by file permissions (Linux), and by its own password if one is set (<see cref="SetOwnPassword"/>).
    /// </summary>
    /// <param name="target">Path of the file to write.</param>
    /// <param name="password">Password of the file; at least <see cref="MinExportPassword"/> characters.</param>
    /// <param name="file">Path of the own certificate; null = <see cref="OwnFile"/>.</param>
    /// <returns>false if there is no own certificate.</returns>
    /// <exception cref="ArgumentException">The password is too short.</exception>
    /// <exception cref="CryptographicException">The certificate cannot be exported.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    /// <param name="current">The password of the own certificate, if it has one.</param>
    /// <exception cref="CertificatePasswordException">The own certificate has a password, and it is missing or wrong.</exception>
    public static bool ExportOwn(string target, string password, string? file = null, string? current = null)
    {
        if (password.Length < MinExportPassword) throw new ArgumentException(Tr.T("Das Passwort ist zu kurz."), nameof(password));
        if (UserSecretFile.Read(file ?? OwnFile, Entropy) is not { Length: > 0 } data) return false;
        try
        {
            var locked = Protected(data);
            if (locked && (current is null || !Pkcs12Info.Decode(data, out _).VerifyMac(current)))
                throw new CertificatePasswordException(Tr.T("Das Passwort des Zertifikats stimmt nicht."));
            using var certificate = X509CertificateLoader.LoadPkcs12(data, locked ? current : null, Flags | X509KeyStorageFlags.Exportable);
            if (!certificate.HasPrivateKey) return false;
            var exported = certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password);
            Pdf.Writing.PdfFileSaver.SaveFile(target, output => output.Write(exported));
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <summary>
    /// Deletes the own certificate from this computer, together with the earlier ones moved aside when a new one was created (each
    /// holds a private key). Signatures already made stay valid; the next digital signature needs a new certificate.
    /// </summary>
    /// <param name="file">Path of the file; null = <see cref="OwnFile"/>.</param>
    /// <returns>true if nothing of it is left; false if a file could not be deleted.</returns>
    public static bool RemoveOwn(string? file = null)
    {
        file ??= OwnFile;
        var all = true;
        var folder = Path.GetDirectoryName(file);
        IEnumerable<string> files = [file];
        if (folder is not null && Directory.Exists(folder))
            files = files.Concat(Directory.EnumerateFiles(folder, Path.GetFileName(file) + ".unlesbar-*"));
        foreach (var path in files.ToList())
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                all = false;
            }
        }
        return all;
    }

    /// <summary>Loads a certificate with private key from a PKCS#12 file (.pfx, .p12).</summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="password">Password of the file; null or empty if it has none.</param>
    /// <returns>The certificate.</returns>
    /// <exception cref="CryptographicException">Wrong password, damaged file, or no certificate with a private key in it.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static SigningCertificate LoadFile(string path, string? password)
    {
        var collection = X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password, Flags);
        X509Certificate2? chosen = null;
        var others = new List<X509Certificate2>();
        foreach (var certificate in collection)
        {
            if (chosen is null && certificate.HasPrivateKey) chosen = certificate;
            else others.Add(certificate);   // the chain of the file: kept for the signature
        }
        if (chosen is null)
        {
            foreach (var certificate in others) certificate.Dispose();
            throw new NoPrivateKeyException(Tr.T("In der Datei ist kein Zertifikat mit privatem Schlüssel."));
        }
        return new SigningCertificate(chosen, SigningCertificateSource.File, others);
    }

    /// <summary>Lists the certificates of the Windows store that can sign: with private key, currently valid, usable for signatures.</summary>
    /// <returns>The certificates; empty on other systems.</returns>
    public static IReadOnlyList<SigningCertificate> FromStore()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var found = new List<SigningCertificate>();
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var now = DateTime.Now;
        foreach (var certificate in store.Certificates)
        {
            if (certificate.HasPrivateKey && certificate.NotBefore <= now && now <= certificate.NotAfter && CanSign(certificate))
                found.Add(new SigningCertificate(certificate, SigningCertificateSource.Store));
            else
                certificate.Dispose();
        }
        return found;
    }

    /// <summary>Finds a certificate of the Windows store by its SHA-256 fingerprint (as stored in the settings).</summary>
    /// <param name="fingerprint">The fingerprint as from <see cref="SigningCertificate.FingerprintOf"/>.</param>
    /// <returns>The certificate, or null if it is no longer there.</returns>
    public static SigningCertificate? FromStore(string fingerprint)
    {
        SigningCertificate? hit = null;
        foreach (var certificate in FromStore())
        {
            if (hit is null && certificate.Fingerprint == fingerprint) hit = certificate;
            else certificate.Dispose();
        }
        return hit;
    }

    /// <summary>
    /// Why a certificate must not make a signature at the given time — not yet valid, expired, or meant for something else (a certification
    /// authority, key usage without digital signature and non-repudiation, extended key usage without a purpose that covers documents) — or
    /// null if it may. Only for making signatures: checking those of others keeps its own rules (EN 319 122-1, 6.3; RFC 5280, 4.2.1.3 and
    /// 4.2.1.12; docs/FEHLERGEDAECHTNIS.md, No. 526).
    /// </summary>
    /// <param name="certificate">The signing certificate.</param>
    /// <param name="when">The signing time.</param>
    /// <returns>The reason (translated), or null.</returns>
    public static string? Unusable(X509Certificate2 certificate, DateTimeOffset when)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        if (when.UtcDateTime < certificate.NotBefore.ToUniversalTime())
            return Tr.T("Das Zertifikat ist erst ab {0} gültig.", certificate.NotBefore.ToString("d", culture));
        if (when.UtcDateTime > certificate.NotAfter.ToUniversalTime())
            return Tr.T("Das Zertifikat ist am {0} abgelaufen.", certificate.NotAfter.ToString("d", culture));
        return Pdf.Signatures.SignatureVerifier.SuitableForSigning(certificate, out var reason) ? null : reason;
    }

    /// <summary>
    /// How a certificate is offered for signing (docs/ABLAUFPLAN.md, 9.4.0; docs/FEHLERGEDAECHTNIS.md, No. 720): the level its signature can
    /// reach — from the certificate (<see cref="Pdf.Signatures.SignatureLevels"/>), but a certificate of the Windows store whose key can be
    /// exported gives a simple signature only (the advanced one needs a key under the signer's sole control: TPM, smart card, Windows Hello);
    /// why it cannot sign now (<see cref="Unusable"/>: expired, a log-on certificate without a purpose for documents …), then it is shown but
    /// cannot be chosen; and a hint.
    /// </summary>
    /// <param name="certificate">The certificate with its source.</param>
    /// <param name="when">The time of signing (now).</param>
    public static CertificateRating Rate(SigningCertificate certificate, DateTimeOffset when)
    {
        var level = Pdf.Signatures.SignatureLevels.Of(certificate.Certificate);
        string? hint = null;
        if (certificate.Source == SigningCertificateSource.Store && level != Pdf.Signatures.SignatureLevel.Simple && KeyExportable(certificate.Certificate) == true)
        {
            level = Pdf.Signatures.SignatureLevel.Simple;
            hint = Tr.T("Der Schlüssel lässt sich aus dem Windows-Speicher exportieren; eine fortgeschrittene Signatur verlangt einen Schlüssel, den nur Sie kontrollieren (TPM, Smartcard, Windows Hello). Darum nur eine einfache Signatur.");
        }
        return new CertificateRating(level, Unusable(certificate.Certificate, when), hint);
    }

    /// <summary>
    /// Whether the private key of a certificate of the Windows store can be exported (its CNG export policy allows it); null when not known
    /// (another system, another kind of key) — then the level stays as the certificate states it.
    /// </summary>
    internal static bool? KeyExportable(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows() || !certificate.HasPrivateKey) return null;
        try
        {
            using var rsa = certificate.GetRSAPrivateKey();
            using var ec = rsa is null ? certificate.GetECDsaPrivateKey() : null;
            var key = (rsa as RSACng)?.Key ?? (ec as ECDsaCng)?.Key;
            return key is null ? null : Exportable(key);
        }
        catch (CryptographicException)
        {
            return null;   // a card that is not inserted, a key the system will not open: nothing known
        }
    }

    /// <summary>Whether a CNG key's export policy allows taking it out (in plain or wrapped).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool Exportable(CngKey key) => (key.ExportPolicy & (CngExportPolicies.AllowExport | CngExportPolicies.AllowPlaintextExport)) != 0;

    /// <summary>Key usage allows signatures (or the certificate does not restrict it).</summary>
    private static bool CanSign(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is not { } usage
        || (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) != 0;

    /// <summary>Keys stay in memory (no temporary key files on disk) and can be used for signing.</summary>
    private static X509KeyStorageFlags Flags => OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
}

/// <summary>How a certificate is offered for signing (<see cref="SigningCertificates.Rate"/>).</summary>
/// <param name="Level">The level its signature can reach.</param>
/// <param name="Problem">Why it cannot sign now (translated); null if it can.</param>
/// <param name="Hint">What to know about the level (translated); null if nothing.</param>
public sealed record CertificateRating(Pdf.Signatures.SignatureLevel Level, string? Problem, string? Hint)
{
    /// <summary>Whether it can be chosen.</summary>
    public bool Selectable => Problem is null;
}

/// <summary>The certificate must not sign now (<see cref="SigningCertificates.Unusable"/>): nothing is written.</summary>
/// <param name="message">The localised message with the reason.</param>
public sealed class SigningCertificateException(string message) : CryptographicException(message);

/// <summary>A certificate file opened with the right password holds no certificate with a private key (no point in asking again).</summary>
/// <param name="message">The localised message.</param>
public sealed class NoPrivateKeyException(string message) : CryptographicException(message);

/// <summary>Whether there is an own certificate (<see cref="SigningCertificates.OwnState"/>).</summary>
public enum OwnCertificateState
{
    /// <summary>None yet (or unreadable): one can be created.</summary>
    Missing,

    /// <summary>There, without a password.</summary>
    Ready,

    /// <summary>There, protected by a password that must be given to sign.</summary>
    Locked,
}

/// <summary>The password of the own certificate is missing or wrong (the certificate is there — do not create a new one).</summary>
/// <param name="message">What to tell the user (translated).</param>
public sealed class CertificatePasswordException(string message) : CryptographicException(message);
