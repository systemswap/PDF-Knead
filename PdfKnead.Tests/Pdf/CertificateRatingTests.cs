using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// How certificates are offered for signing (docs/ABLAUFPLAN.md, 9.4.0, step 3; docs/FEHLERGEDAECHTNIS.md, No. 720): a log-on certificate is
/// shown with its reason but cannot be chosen; a company certificate of the Windows store gives an advanced signature only with a key that
/// cannot be exported, otherwise a simple one with the reason; several certificate files are remembered without passwords.
/// </summary>
public sealed class CertificateRatingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    /// <summary>A certificate issued by a company authority (advanced by its content), with the given key and purposes.</summary>
    private static X509Certificate2 Issued(AsymmetricAlgorithm key, params string[] purposes)
    {
        using var caKey = RSA.Create(2048);
        var ca = new CertificateRequest("CN=Muster-Firmen-CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        ca.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var authority = ca.CreateSelfSigned(Now.AddDays(-2), Now.AddYears(3));
        var request = new CertificateRequest("CN=Max Mustermann, O=Muster GmbH", (RSA)key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        if (purposes.Length > 0)
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection().With(purposes), false));
        using var issued = request.Create(authority, Now.AddDays(-1), Now.AddYears(1), [0x42]);
        return issued.CopyWithPrivateKey((RSA)key);
    }

    [Fact]
    public void LogonCertificate_ShownNotSelectable()
    {
        using var key = RSA.Create(2048);
        // client authentication and smart card log-on only: no purpose that covers documents
        using var signer = new SigningCertificate(Issued(key, "1.3.6.1.5.5.7.3.2", "1.3.6.1.4.1.311.20.2.2"), SigningCertificateSource.Store);
        var rating = SigningCertificates.Rate(signer, Now);
        Assert.False(rating.Selectable);
        Assert.Equal(Tr.T("Das Zertifikat ist für einen anderen Zweck ausgestellt (zum Beispiel für eine Webseite), nicht zum Unterschreiben."), rating.Problem);
    }

    /// <summary>
    /// A company certificate of the Windows store with an exportable key: simple, with the reason; the same certificate from a file keeps the level
    /// its content states; the decision on the key: a key that cannot be exported counts as under sole control. (A key that cannot be exported is
    /// not bound to a certificate here — that would leave a lasting key in the user's store.)
    /// </summary>
    [Fact]
    public void StoreCertificate_AdvancedOnlyWithKeyThatCannotBeExported()
    {
        if (!OperatingSystem.IsWindows()) return;
        static CngKeyCreationParameters Policy(CngExportPolicies policy) => new()
        {
            ExportPolicy = policy, Parameters = { new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None) },
        };
        using var exportable = new RSACng(CngKey.Create(CngAlgorithm.Rsa, null, Policy(CngExportPolicies.AllowPlaintextExport)));
        var certificate = Issued(exportable, "1.3.6.1.5.5.7.3.4");
        using (var signer = new SigningCertificate(certificate, SigningCertificateSource.Store))
        {
            var rating = SigningCertificates.Rate(signer, Now);
            Assert.True(rating.Selectable);
            Assert.Equal(SignatureLevel.Simple, rating.Level);
            Assert.NotNull(rating.Hint);
        }
        using (var fromFile = new SigningCertificate(Issued(exportable, "1.3.6.1.5.5.7.3.4"), SigningCertificateSource.File))
            Assert.Equal(SignatureLevel.Advanced, SigningCertificates.Rate(fromFile, Now).Level);
        using var locked = CngKey.Create(CngAlgorithm.Rsa, null, Policy(CngExportPolicies.None));
        Assert.False(SigningCertificates.Exportable(locked));
        Assert.True(SigningCertificates.Exportable(exportable.Key));
    }

    /// <summary>Several certificate files remembered, once per path, without a password; forgetting one keeps the others.</summary>
    [Fact]
    public void CertificateFiles_RememberedOncePerPath()
    {
        var settings = new AppSettings()
            .WithCertificateFile(new SigningCertificateChoice(SigningCertificateSource.File, "C:/a/muster.p12", "AA"))
            .WithCertificateFile(new SigningCertificateChoice(SigningCertificateSource.File, "C:/b/firma.pfx", "BB"))
            .WithCertificateFile(new SigningCertificateChoice(SigningCertificateSource.File, "C:/a/muster.p12", "CC"));
        Assert.Equal(["C:/b/firma.pfx", "C:/a/muster.p12"], settings.Files.Select(f => f.Path));
        Assert.Equal("CC", settings.Files[1].Fingerprint);
        Assert.Equal(["C:/a/muster.p12"], settings.WithoutCertificateFile("C:/b/firma.pfx").Files.Select(f => f.Path));
    }
}

internal static class OidCollectionExtensions
{
    public static OidCollection With(this OidCollection collection, IEnumerable<string> oids)
    {
        foreach (var oid in oids) collection.Add(new Oid(oid));
        return collection;
    }
}
