using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;
using PdfKnead.Core.Platform.Pkcs11;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Signature cards through PKCS#11 (KONZEPT.md, section 5.17, „Signaturkarte“) with SoftHSM, a token in software: tokens of its own in the
/// test folder (never the system's), one with an RSA and one with an ECDSA key, each with its certificate. Skipped where SoftHSM and the
/// OpenSC tools are not installed (Debian: softhsm2, opensc).
/// </summary>
public sealed class SmartCardTests
{
    internal const string Pin = "123456";
    private const string SoftHsm = "/usr/lib/softhsm/libsofthsm2.so";

    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetNativeEnvironment(string name, string value, int overwrite);

    /// <summary>The tokens of this test run: created once, before the module is loaded into the process (it reads its configuration then).</summary>
    internal sealed class Tokens
    {
        public required string Module { get; init; }
        public required X509Certificate2 Rsa { get; init; }
        public required X509Certificate2 Ecdsa { get; init; }
        public required X509Certificate2 Authority { get; init; }
    }

    private static readonly Lazy<Tokens?> Shared = new(Create);

    internal static Tokens? Available => Shared.Value;

    private static Tokens? Create()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(SoftHsm) || !File.Exists("/usr/bin/softhsm2-util") || !File.Exists("/usr/bin/pkcs11-tool")) return null;
        var folder = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"softhsm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "tokens"));
        var config = Path.Combine(folder, "softhsm2.conf");
        File.WriteAllText(config, $"directories.tokendir = {Path.Combine(folder, "tokens")}\nobjectstore.backend = file\nlog.level = ERROR\n");
        // the module reads SOFTHSM2_CONF from the process environment of C: set it there as well, not only for .NET
        Environment.SetEnvironmentVariable("SOFTHSM2_CONF", config);
        SetNativeEnvironment("SOFTHSM2_CONF", config, 1);

        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Muster-Kartenstelle", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var authority = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(5));

        Run("softhsm2-util", config, "--init-token", "--free", "--label", "Muster-Karte RSA", "--pin", Pin, "--so-pin", "87654321");
        Run("softhsm2-util", config, "--init-token", "--free", "--label", "Muster-Karte EC", "--pin", Pin, "--so-pin", "87654321");

        using var rsaKey = RSA.Create(2048);
        var rsa = Issue(new CertificateRequest("CN=Max Mustermann", rsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), authority, caKey, 1);
        Put(folder, config, "Muster-Karte RSA", "01", rsaKey.ExportPkcs8PrivateKeyPem(), rsa);
        Put(folder, config, "Muster-Karte RSA", "10", null, authority);
        using var ecKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ecdsa = Issue(new CertificateRequest("CN=Erika Mustermann", ecKey, HashAlgorithmName.SHA256), authority, caKey, 2);
        Put(folder, config, "Muster-Karte EC", "02", ecKey.ExportPkcs8PrivateKeyPem(), ecdsa);
        return new Tokens { Module = SoftHsm, Rsa = rsa, Ecdsa = ecdsa, Authority = X509CertificateLoader.LoadCertificate(authority.RawData) };
    }

    private static X509Certificate2 Issue(CertificateRequest request, X509Certificate2 authority, RSA caKey, byte serial)
    {
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.DigitalSignature, true));
        // the authority signs with RSA also for the EC key
        using var issued = request.Create(authority.SubjectName, X509SignatureGenerator.CreateForRSA(caKey, RSASignaturePadding.Pkcs1),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2), [serial]);
        return X509CertificateLoader.LoadCertificate(issued.RawData);
    }

    /// <summary>Puts a key (PKCS#8, through softhsm2-util) and a certificate (through pkcs11-tool) on a token under one object ID.</summary>
    private static void Put(string folder, string config, string token, string id, string? keyPem, X509Certificate2 certificate)
    {
        if (keyPem is not null)
        {
            var key = Path.Combine(folder, $"key-{id}.pem");
            File.WriteAllText(key, keyPem);
            Run("softhsm2-util", config, "--import", key, "--token", token, "--label", "Schlüssel", "--id", id, "--pin", Pin);
            File.Delete(key);
        }
        var der = Path.Combine(folder, $"cert-{id}.der");
        File.WriteAllBytes(der, certificate.RawData);
        Run("pkcs11-tool", config, "--module", SoftHsm, "--token-label", token, "--login", "--pin", Pin, "--write-object", der, "--type", "cert", "--id", id,
            "--label", "Zertifikat");
    }

    private static void Run(string tool, string config, params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/" + tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["SOFTHSM2_CONF"] = config;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{tool}: {output}");
    }

    private static InkElement Signature() => new()
    {
        Rect = new PdfRectangle(50, 100, 250, 180),
        Strokes = [[0, 20, 40, 70, 90, 10, 140, 60]],
        StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann"),
    };

    [Fact]
    public void Certificates_Listed_WithoutPin()
    {
        if (Available is not { } tokens) return;
        // SoftHSM is no card: not offered in the list of drivers (chosen as a file for tests)
        Assert.DoesNotContain(SmartCards.KnownModules(), m => m.Contains("softhsm"));
        var found = SmartCards.Certificates(tokens.Module);
        Assert.Contains(found, c => c.Certificate.Thumbprint == tokens.Rsa.Thumbprint && c.Token == "Muster-Karte RSA" && !c.PinPad);
        Assert.Contains(found, c => c.Certificate.Thumbprint == tokens.Ecdsa.Thumbprint && c.Token == "Muster-Karte EC");
        Assert.Contains("Max Mustermann", found.First(c => c.Certificate.Thumbprint == tokens.Rsa.Thumbprint).Display);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Signed_OnTheCard_ChainFromTheCard_OnlyRevocationUnchecked(bool ecdsa)
    {
        if (Available is not { } tokens) return;
        var certificate = ecdsa ? tokens.Ecdsa : tokens.Rsa;
        using var signer = SmartCards.Open(tokens.Module, SigningCertificate.FingerprintOf(certificate), Pin);
        Assert.Equal(SigningCertificateSource.Token, signer.Source);
        Assert.False(signer.Certificate.HasPrivateKey);   // the key stays on the card
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var signed = PdfSigner.Sign(doc, 0, Signature(), signer, DateTimeOffset.Now);
        using var saved = PdfDocument.Open(signed);
        var check = Assert.Single(SignatureVerifier.Verify(saved, null, [X509CertificateLoader.LoadCertificate(tokens.Authority.RawData)], CancellationToken.None));
        // the chain to the card's authority came along from the card (RSA token): the identity is confirmed; without revocation data in the
        // file the result is „not confirmed“ since phase 2.5 (docs/FEHLERGEDAECHTNIS.md, No. 472) — the test ran only under Linux (SoftHSM)
        // and was not adjusted then (found by the Linux publish of 1.1.542)
        Assert.True(check.IdentityConfirmed, check.Problem);
        Assert.Equal(SignatureOutcome.Unconfirmed, check.Outcome);
        Assert.Equal(Tr.T("Sperrstatus nicht geprüft (keine Prüfdaten in der Datei)."), check.UnconfirmedReason);
        Assert.Equal(SigningCertificate.FingerprintOf(certificate).Replace(" ", ""), check.Certificate!.Fingerprint.Replace(" ", ""));
    }

    [Fact]
    public void WrongPin_Said_NothingSigned()
    {
        if (Available is not { } tokens) return;
        var error = Assert.Throws<Pkcs11Exception>(() => SmartCards.Open(tokens.Module, SigningCertificate.FingerprintOf(tokens.Rsa), "000000"));
        Assert.True(error.WrongPin);
        Assert.Equal(Tr.T("Die PIN ist falsch."), error.Message);
        var info = SmartCards.PinInfo(tokens.Module, SigningCertificate.FingerprintOf(tokens.Rsa));
        Assert.Equal("Muster-Karte RSA", info?.Token);
        Assert.False(info!.Locked);
    }

    [Fact]
    public void CertificateNotOnACard_Said()
    {
        if (Available is not { } tokens) return;
        using var other = RSA.Create(2048);
        using var stranger = new CertificateRequest("CN=Fremd", other, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Throws<Pkcs11Exception>(() => SmartCards.Open(tokens.Module, SigningCertificate.FingerprintOf(stranger), Pin));
        Assert.Null(SmartCards.PinInfo(tokens.Module, SigningCertificate.FingerprintOf(stranger)));
        Assert.Throws<Pkcs11Exception>(() => SmartCards.Certificates("/usr/lib/kein-treiber.so"));
        Assert.Equal(Tr.T("Auf der Karte ist kein Zertifikat zu finden."), SmartCards.WhyNothing(tokens.Module));   // reader and card there: the reason is the certificate
    }
}
