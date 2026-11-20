using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>Own, file and store certificates for digital signatures (KONZEPT.md, section 5.17, step S2).</summary>
public sealed class SigningCertificateTests
{
    private static string TempFile(string extension = "p12") =>
        Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"signatur-{Guid.NewGuid():N}.{extension}");

    /// <summary>
    /// Writes a new own certificate as a PFX file with the given password (also shorter than the export minimum); returns the certificate
    /// without key. On Windows a key loaded without Exportable cannot be exported directly (Linux allows it), so this goes through
    /// <see cref="SigningCertificates.ExportOwn"/> like the program and re-encrypts the result.
    /// </summary>
    internal static byte[] WriteOwnPfx(string name, string pfx, string password)
    {
        var own = pfx + ".eigen.p12";
        var step = pfx + ".zwischen.pfx";
        const string stepPassword = "zwischen-123";
        try
        {
            using (SigningCertificates.CreateOwn(name, own)) { }
            Assert.True(SigningCertificates.ExportOwn(step, stepPassword, own));
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(step, stepPassword,
                X509KeyStorageFlags.Exportable | (OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet));
            File.WriteAllBytes(pfx, certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password));
            return certificate.RawData;
        }
        finally
        {
            File.Delete(own);
            File.Delete(step);
        }
    }

    [Fact]
    public void Own_CreateThenLoad_SameCertificateWithKey()
    {
        var file = TempFile();
        Assert.Null(SigningCertificates.LoadOwn(file));
        string fingerprint;
        using (var created = SigningCertificates.CreateOwn("  Max Mustermann ", file))
        {
            Assert.Equal("Max Mustermann", created.Name);
            Assert.True(created.SelfSigned);
            Assert.True(created.Certificate.HasPrivateKey);
            Assert.Equal(SigningCertificateSource.Own, created.Source);
            Assert.Equal(SigningCertificates.OwnKeySize, created.Certificate.GetRSAPublicKey()!.KeySize);
            var usage = created.Certificate.Extensions.OfType<X509KeyUsageExtension>().Single();
            Assert.Equal(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, usage.KeyUsages);
            Assert.InRange((created.ValidUntil - DateTime.Now).TotalDays, 365 * 9.9, 365 * 10.1);
            Assert.Equal(16, created.Fingerprint.Split(' ').Length);
            fingerprint = created.Fingerprint;
        }
        using var loaded = SigningCertificates.LoadOwn(file);
        Assert.NotNull(loaded);
        Assert.Equal(fingerprint, loaded.Fingerprint);
        using var rsa = loaded.Certificate.GetRSAPrivateKey()!;
        var data = "Prüfung"u8.ToArray();
        var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(loaded.Certificate.GetRSAPublicKey()!.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    /// <summary>
    /// The own certificate exported with a password (to keep or to sign on another computer): an ordinary PKCS#12 file that opens
    /// with this password only, with the same certificate and its key, usable as „Zertifikat aus einer Datei“.
    /// </summary>
    [Fact]
    public void Own_Exported_WithPassword_LoadsAsFile()
    {
        var file = TempFile();
        var target = TempFile();
        Assert.False(SigningCertificates.ExportOwn(target, "geheim-123", file));   // none yet
        Assert.Throws<ArgumentException>(() => SigningCertificates.ExportOwn(target, "kurz", file));
        string fingerprint;
        using (var created = SigningCertificates.CreateOwn("Max Mustermann", file)) fingerprint = created.Fingerprint;

        Assert.True(SigningCertificates.ExportOwn(target, "geheim-123", file));
        Assert.ThrowsAny<CryptographicException>(() => SigningCertificates.LoadFile(target, "falsch"));
        Assert.ThrowsAny<CryptographicException>(() => SigningCertificates.LoadFile(target, null));
        using var loaded = SigningCertificates.LoadFile(target, "geheim-123");
        Assert.Equal(fingerprint, loaded.Fingerprint);
        Assert.Equal(SigningCertificateSource.File, loaded.Source);
        using var rsa = loaded.Certificate.GetRSAPrivateKey()!;
        var signature = rsa.SignData("Prüfung"u8.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(loaded.Certificate.GetRSAPublicKey()!.VerifyData("Prüfung"u8.ToArray(), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Own_NewName_ReplacesAndKeepsTheOldFileAside()
    {
        var file = TempFile();
        using (SigningCertificates.CreateOwn("Max Mustermann", file)) { }
        using (var renamed = SigningCertificates.CreateOwn("Erika Mustermann", file))
            Assert.Equal("Erika Mustermann", renamed.Name);
        using (var loaded = SigningCertificates.LoadOwn(file))
            Assert.Equal("Erika Mustermann", loaded!.Name);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file) + ".unlesbar-*"));
    }

    /// <summary>
    /// Several own certificates (decided on 11.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 733): the first at its place, further ones beside it
    /// numbered from 2 and listed by number (10 after 9, not after 1); files set aside on replacing and other names are not among them.
    /// </summary>
    [Fact]
    public void Own_Several_NumberedBesideTheFirst()
    {
        var first = TempFile();
        var stem = Path.ChangeExtension(first, null);
        Assert.Equal(first, SigningCertificates.NextOwnFile(first));
        Assert.Empty(SigningCertificates.OwnFiles(first));
        using (SigningCertificates.CreateOwn("Max Mustermann", first)) { }
        var second = SigningCertificates.NextOwnFile(first);
        Assert.Equal(stem + "-2.p12", second);
        using (SigningCertificates.CreateOwn("Erika Mustermann", second)) { }
        var tenth = stem + "-10.p12";
        File.Copy(second, tenth);
        File.WriteAllBytes(stem + "-alt.p12", [1]);                    // not a number
        File.WriteAllBytes(second + ".unlesbar-20261111", [1]);       // set aside
        Assert.Equal([first, second, tenth], SigningCertificates.OwnFiles(first));
        Assert.Equal(stem + "-3.p12", SigningCertificates.NextOwnFile(first));

        // the first one deleted: the others stay listed, a new one takes the first place
        File.Delete(first);
        Assert.Equal([second, tenth], SigningCertificates.OwnFiles(first));
        Assert.Equal(first, SigningCertificates.NextOwnFile(first));
    }

    [Fact]
    public void Own_EmptyName_Throws_LongName_Shortened()
    {
        Assert.Throws<ArgumentException>(() => SigningCertificates.CreateOwn("   ", TempFile()));
        using var long_ = SigningCertificates.CreateOwn(new string('A', 100), TempFile());
        Assert.Equal(64, long_.Name.Length);
    }

    [Fact]
    public void Own_DamagedFile_LoadsNothing()
    {
        var file = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        Assert.Null(SigningCertificates.LoadOwn(file));
    }

    [Fact]
    public void File_WithPassword_LoadsOnlyWithTheRightOne()
    {
        // Through ExportOwn: on Windows a key loaded without Exportable cannot be exported directly (Linux allows it)
        var file = TempFile("pfx");
        var ownFile = TempFile();
        using (SigningCertificates.CreateOwn("Max Mustermann", ownFile)) { }
        Assert.True(SigningCertificates.ExportOwn(file, "geheim-123", ownFile));
        using (var loaded = SigningCertificates.LoadFile(file, "geheim-123"))
        {
            Assert.Equal("Max Mustermann", loaded.Name);
            Assert.Equal(SigningCertificateSource.File, loaded.Source);
            Assert.True(loaded.Certificate.HasPrivateKey);
        }
        Assert.ThrowsAny<CryptographicException>(() => SigningCertificates.LoadFile(file, "falsch"));
    }

    [Fact]
    public void File_WithoutPrivateKey_Rejected()
    {
        var file = TempFile("pfx");
        using (var own = SigningCertificates.CreateOwn("Max Mustermann", TempFile()))
        using (var publicOnly = X509CertificateLoader.LoadCertificate(own.Certificate.RawData))
            File.WriteAllBytes(file, publicOnly.Export(X509ContentType.Pkcs12, "geheim"));
        Assert.ThrowsAny<CryptographicException>(() => SigningCertificates.LoadFile(file, "geheim"));
    }

    [Fact]
    public void Store_OnlyOnWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Empty(SigningCertificates.FromStore());
    }

    /// <summary>
    /// Replacing an own certificate whose file had wider permissions (S7-12): the new file and the one set aside are readable for the
    /// user only, and no half-written file is left (written to a new file first, then moved).
    /// </summary>
    [Fact]
    public void Own_ReplacedFile_UserOnlyPermissions()
    {
        if (OperatingSystem.IsWindows()) return;   // protected by DPAPI there, no file modes
        var file = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"rechte-{Guid.NewGuid():N}", "signatur.p12");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, [1, 2, 3]);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        using (SigningCertificates.CreateOwn("Max Mustermann", file)) { }
        var userOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(userOnly, File.GetUnixFileMode(file));
        var aside = Directory.GetFiles(Path.GetDirectoryName(file)!, "signatur.p12.unlesbar-*");
        Assert.Single(aside);
        Assert.Equal(userOnly, File.GetUnixFileMode(aside[0]));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.neu-*"));
    }

    /// <summary>
    /// The optional password of the own certificate (KONZEPT.md, section 5.17, „Passwort“): set, the key needs it, the certificate is still
    /// shown; without or with a wrong one there is a clear error (never „missing“, which would create a new one); changed and removed again;
    /// the key loaded with it signs.
    /// </summary>
    [Fact]
    public void Own_Password_SetChangedRemoved_KeySignsWithIt()
    {
        var file = TempFile();
        using (var created = SigningCertificates.CreateOwn("Max Mustermann", file))
        {
            Assert.Equal(OwnCertificateState.Ready, SigningCertificates.OwnState(file));
            var fingerprint = created.Fingerprint;

            Assert.Throws<ArgumentException>(() => SigningCertificates.SetOwnPassword(null, "kurz", file));
            Assert.True(SigningCertificates.SetOwnPassword(null, "geheim-123", file));
            Assert.Equal(OwnCertificateState.Locked, SigningCertificates.OwnState(file));
            Assert.Throws<CertificatePasswordException>(() => SigningCertificates.LoadOwn(file));
            Assert.Throws<CertificatePasswordException>(() => SigningCertificates.LoadOwn(file, "falsch-123"));
            using (var shown = SigningCertificates.OwnPublic(file))
            {
                Assert.NotNull(shown);
                Assert.Equal("Max Mustermann", shown!.Name);
                Assert.Equal(fingerprint, shown.Fingerprint);
                Assert.False(shown.Certificate.HasPrivateKey);
            }
            using (var opened = SigningCertificates.LoadOwn(file, "geheim-123"))
            {
                Assert.NotNull(opened);
                Assert.Equal(fingerprint, opened!.Fingerprint);
                using var key = opened.Certificate.GetRSAPrivateKey()!;
                var data = "zu unterschreiben"u8.ToArray();
                var signature = key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var check = created.Certificate.GetRSAPublicKey()!;
                Assert.True(check.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            }

            // the export needs it as well
            var export = TempFile("pfx");
            Assert.Throws<CertificatePasswordException>(() => SigningCertificates.ExportOwn(export, "export-123", file));
            Assert.True(SigningCertificates.ExportOwn(export, "export-123", file, "geheim-123"));
            using (var exported = SigningCertificates.LoadFile(export, "export-123")) Assert.Equal(fingerprint, exported.Fingerprint);

            // changed: only with the old one; then removed
            Assert.Throws<CertificatePasswordException>(() => SigningCertificates.SetOwnPassword("falsch-123", "neu-geheim-1", file));
            Assert.True(SigningCertificates.SetOwnPassword("geheim-123", "neu-geheim-1", file));
            Assert.Throws<CertificatePasswordException>(() => SigningCertificates.LoadOwn(file, "geheim-123"));
            Assert.True(SigningCertificates.SetOwnPassword("neu-geheim-1", null, file));
            Assert.Equal(OwnCertificateState.Ready, SigningCertificates.OwnState(file));
            using var again = SigningCertificates.LoadOwn(file);
            Assert.Equal(fingerprint, again!.Fingerprint);
        }
        Assert.Equal(OwnCertificateState.Missing, SigningCertificates.OwnState(TempFile()));
    }
}
