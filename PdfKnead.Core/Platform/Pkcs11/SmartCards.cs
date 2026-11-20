using System.Security.Cryptography.X509Certificates;

namespace PdfKnead.Core.Platform.Pkcs11;

/// <summary>A certificate found on a token.</summary>
/// <param name="Module">The driver library.</param>
/// <param name="Token">Name of the token (card).</param>
/// <param name="Certificate">The certificate (public).</param>
/// <param name="PinPad">The reader has its own PIN pad: the PIN is entered there, not in the program.</param>
public sealed record TokenCertificate(string Module, string Token, X509Certificate2 Certificate, bool PinPad)
{
    /// <summary>Name of the holder.</summary>
    public string Name => Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

    /// <summary>What a list shows: holder, card, valid until.</summary>
    public string Display => Tr.T("{0} — {1}, gültig bis {2}", Name, Token, Certificate.NotAfter.ToString("d"));
}

/// <summary>What a signature card asks before signing.</summary>
/// <param name="Token">Name of the card.</param>
/// <param name="PinPad">The PIN is entered at the reader.</param>
/// <param name="FinalTry">One wrong PIN more locks the card.</param>
/// <param name="Locked">The PIN is locked already.</param>
public sealed record TokenPinInfo(string Token, bool PinPad, bool FinalTry, bool Locked);

/// <summary>
/// Signature cards and tokens through their PKCS#11 driver (KONZEPT.md, section 5.17, „Signaturkarte“): which drivers are installed, which
/// certificates the cards hold, and a <see cref="SigningCertificate"/> whose key stays on the card. Linux and other Unix systems; Windows
/// reaches cards through its certificate store.
/// </summary>
public static class SmartCards
{
    /// <summary>Whether cards are reached this way on this system.</summary>
    public static bool Available => !OperatingSystem.IsWindows();

    /// <summary>
    /// Where the distributions put OpenSC, the driver for most cards. Test tokens in software (SoftHSM) are not offered: they are no card, and
    /// for a normal user they cannot even start; whoever tests with them chooses the file.
    /// </summary>
    private static readonly string[] KnownPlaces =
    [
        "/usr/lib/x86_64-linux-gnu/opensc-pkcs11.so", "/usr/lib/aarch64-linux-gnu/opensc-pkcs11.so", "/usr/lib64/opensc-pkcs11.so",
        "/usr/lib/opensc-pkcs11.so", "/usr/lib64/pkcs11/opensc-pkcs11.so", "/usr/lib/pkcs11/opensc-pkcs11.so",
    ];

    /// <summary>Why a driver shows no certificate: no reader, no card in a reader, or no certificate on the card.</summary>
    /// <param name="module">The driver library.</param>
    /// <returns>The reason to tell the user.</returns>
    /// <exception cref="Pkcs11Exception">The driver cannot be loaded or fails.</exception>
    public static string WhyNothing(string module)
    {
        var driver = Pkcs11Module.Open(module);
        if (driver.Slots(withToken: false).Count == 0)
            return Tr.T("Kein Kartenleser gefunden. Ist er angeschlossen und läuft der Kartendienst? Debian/Ubuntu: sudo apt install pcscd — Fedora: sudo dnf install pcsc-lite");
        return driver.Slots().Count == 0 ? Tr.T("Keine Karte im Leser.") : Tr.T("Auf der Karte ist kein Zertifikat zu finden.");
    }

    /// <summary>The drivers installed in the known places (each file once, symbolic links resolved).</summary>
    public static IReadOnlyList<string> KnownModules() => !Available ? []
        : [.. KnownPlaces.Where(File.Exists).DistinctBy(p => new FileInfo(p).ResolveLinkTarget(true)?.FullName ?? p)];

    /// <summary>The certificates on the cards a driver reaches (no PIN needed; private keys are looked up when signing).</summary>
    /// <param name="module">The driver library.</param>
    /// <returns>The certificates.</returns>
    /// <exception cref="Pkcs11Exception">The driver cannot be loaded or fails.</exception>
    public static List<TokenCertificate> Certificates(string module)
    {
        var driver = Pkcs11Module.Open(module);
        var found = new List<TokenCertificate>();
        foreach (var slot in driver.Slots())
        {
            var (label, _, flags) = driver.TokenInfo(slot);
            if ((flags & Pkcs11Module.CkfTokenInitialized) == 0) continue;
            var session = driver.OpenSession(slot);
            try
            {
                foreach (var handle in driver.Find(session, (Pkcs11Module.CkaClass, Pkcs11Module.CkoCertificate), (Pkcs11Module.CkaCertificateType, Pkcs11Module.CkcX509)))
                {
                    if (driver.Attribute(session, handle, Pkcs11Module.CkaValue) is not { } der) continue;
                    try
                    {
                        found.Add(new TokenCertificate(module, label, X509CertificateLoader.LoadCertificate(der), (flags & Pkcs11Module.CkfProtectedAuthenticationPath) != 0));
                    }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                        // an unreadable certificate object: left out
                    }
                }
            }
            finally
            {
                driver.CloseSession(session);
            }
        }
        return found;
    }

    /// <summary>A card's certificate to show (without its key, which stays on the card until signing).</summary>
    /// <param name="card">The certificate found on the card.</param>
    /// <returns>A copy the caller owns.</returns>
    public static SigningCertificate Describe(TokenCertificate card) =>
        new(X509CertificateLoader.LoadCertificate(card.Certificate.RawData), SigningCertificateSource.Token);

    /// <summary>What the card holding a certificate asks before signing (PIN pad, last try, locked).</summary>
    /// <param name="module">The driver library.</param>
    /// <param name="fingerprint">The certificate (<see cref="SigningCertificate.FingerprintOf"/>).</param>
    /// <returns>The information; null if the certificate is on no card that is there.</returns>
    public static TokenPinInfo? PinInfo(string module, string fingerprint)
    {
        var driver = Pkcs11Module.Open(module);
        return Locate(driver, fingerprint) is { } at ? Info(driver, at.Slot) : null;
    }

    private static TokenPinInfo Info(Pkcs11Module driver, ulong slot)
    {
        const ulong finalTry = 0x20000;
        var (label, _, flags) = driver.TokenInfo(slot);
        return new TokenPinInfo(label, (flags & Pkcs11Module.CkfProtectedAuthenticationPath) != 0, (flags & finalTry) != 0, (flags & Pkcs11Module.CkfUserPinLocked) != 0);
    }

    /// <summary>
    /// Opens the certificate for signing: logs in on its card with the PIN (or at the reader's PIN pad) and finds its private key by the
    /// object ID it shares with the certificate. The key stays on the card; disposing logs out.
    /// </summary>
    /// <param name="module">The driver library.</param>
    /// <param name="fingerprint">The certificate (<see cref="SigningCertificate.FingerprintOf"/>).</param>
    /// <param name="pin">The PIN; null for a PIN pad.</param>
    /// <returns>The certificate with its key on the card; the other certificates of the card come along for the chain.</returns>
    /// <exception cref="Pkcs11Exception">The card is not there, the PIN is wrong or locked, or the card has no key for the certificate.</exception>
    public static SigningCertificate Open(string module, string fingerprint, string? pin)
    {
        var driver = Pkcs11Module.Open(module);
        var at = Locate(driver, fingerprint) ?? throw new Pkcs11Exception(Tr.T("Die Karte mit diesem Zertifikat ist nicht im Leser."));
        var session = driver.OpenSession(at.Slot);
        try
        {
            driver.Login(session, pin);
            var keys = driver.Find(session, (Pkcs11Module.CkaClass, Pkcs11Module.CkoPrivateKey), (Pkcs11Module.CkaId, at.Id));
            if (keys.Count == 0) throw new Pkcs11Exception(Tr.T("Auf der Karte gibt es zu diesem Zertifikat keinen Schlüssel."));
            var key = keys[0];
            var always = driver.Attribute(session, key, Pkcs11Module.CkaAlwaysAuthenticate) is [not 0, ..];
            var tokenSession = new TokenSession(driver, session, key, always, pin);
            var certificate = at.Certificate;
            System.Security.Cryptography.AsymmetricAlgorithm signer = driver.Number(session, key, Pkcs11Module.CkaKeyType) switch
            {
                Pkcs11Module.CkkRsa when certificate.GetRSAPublicKey() is { } rsa => new TokenRsa(tokenSession, rsa),
                Pkcs11Module.CkkEc when certificate.GetECDsaPublicKey() is { } ec => new TokenEcdsa(tokenSession, ec),
                _ => throw new Pkcs11Exception(Tr.T("Die Art des Schlüssels auf der Karte wird nicht unterstützt.")),
            };
            return new SigningCertificate(certificate, SigningCertificateSource.Token, at.Others, signer);
        }
        catch
        {
            driver.Logout(session);
            driver.CloseSession(session);
            throw;
        }
    }

    /// <summary>The slot, object ID and certificate on the card that holds a certificate; the other certificates of that card for the chain.</summary>
    private static (ulong Slot, byte[] Id, X509Certificate2 Certificate, List<X509Certificate2> Others)? Locate(Pkcs11Module driver, string fingerprint)
    {
        foreach (var slot in driver.Slots())
        {
            // an empty, not yet set up token (SoftHSM always offers one) has nothing to open
            if ((driver.TokenInfo(slot).Flags & Pkcs11Module.CkfTokenInitialized) == 0) continue;
            var session = driver.OpenSession(slot);
            try
            {
                (byte[] Id, X509Certificate2 Certificate)? hit = null;
                var others = new List<X509Certificate2>();
                foreach (var handle in driver.Find(session, (Pkcs11Module.CkaClass, Pkcs11Module.CkoCertificate), (Pkcs11Module.CkaCertificateType, Pkcs11Module.CkcX509)))
                {
                    if (driver.Attribute(session, handle, Pkcs11Module.CkaValue) is not { } der) continue;
                    X509Certificate2 certificate;
                    try
                    {
                        certificate = X509CertificateLoader.LoadCertificate(der);
                    }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                        continue;
                    }
                    if (hit is null && SigningCertificate.FingerprintOf(certificate) == fingerprint && driver.Attribute(session, handle, Pkcs11Module.CkaId) is { } id)
                        hit = (id, certificate);
                    else others.Add(certificate);
                }
                if (hit is { } found) return (slot, found.Id, found.Certificate, others);
                foreach (var other in others) other.Dispose();
            }
            finally
            {
                driver.CloseSession(session);
            }
        }
        return null;
    }
}
