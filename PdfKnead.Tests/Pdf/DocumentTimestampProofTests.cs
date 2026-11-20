using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Document time stamps as proofs of existence (EN 319 102-1, 5.6.2.3 and 5.6.3.4; docs/ABLAUFPLAN.md, 9.3.3; docs/FEHLERGEDAECHTNIS.md,
/// No. 712): the services' certificates expire after a few years; a later document time stamp that holds proves the signature and its time
/// stamp existed at its time, so both are checked then, not today. Two stand-in services under one test root: A, whose certificate expired
/// yesterday, stamped the signature a month ago; B stamped the document ten days ago. The signer's certificate expired yesterday as well
/// (short-term, so no revocation status is needed).
/// </summary>
public sealed class DocumentTimestampProofTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static InkElement Signature() => new()
    {
        Rect = new PdfRectangle(50, 100, 250, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann"),
    };

    /// <summary>The test root with its key, a signer under it whose certificate ends <paramref name="signerUntil"/>, and the checks of a file.</summary>
    private sealed class Setup : IDisposable
    {
        private readonly RSA _rootKey = RSA.Create(2048);
        private readonly RSA _signerKey = RSA.Create(2048);

        public Setup(DateTimeOffset signerUntil)
        {
            var request = new CertificateRequest("CN=Muster-Wurzel Zeit", _rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            Root = request.CreateSelfSigned(Now.AddYears(-3), Now.AddYears(5));
            var signer = new CertificateRequest("CN=Max Mustermann", _signerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            signer.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
            signer.CertificateExtensions.Add(new X509Extension("0.4.0.194121.2.1", [0x05, 0x00], false));   // short-term certificate
            using var issued = signer.Create(Root, Now.AddYears(-1), signerUntil, [0x42]);
            Signer = new SigningCertificate(issued.CopyWithPrivateKey(_signerKey), SigningCertificateSource.File);
        }

        public X509Certificate2 Root { get; }

        public SigningCertificate Signer { get; }

        /// <summary>A service under the root, stamping <paramref name="at"/>, its certificate ending <paramref name="until"/>.</summary>
        public SignatureTimestampTests.TestAuthority Service(string name, DateTimeOffset at, DateTimeOffset until) =>
            new(name, at: at, issuer: Root, notAfter: until);

        public IReadOnlyList<SignatureCheck> Verify(byte[] file)
        {
            using var doc = PdfDocument.Open(file);
            return SignatureVerifier.Verify(doc, null, [X509CertificateLoader.LoadCertificate(Root.RawData)], CancellationToken.None);
        }

        public void Dispose()
        {
            Signer.Dispose();
            Root.Dispose();
            _rootKey.Dispose();
            _signerKey.Dispose();
        }
    }

    /// <summary>Signed with a time stamp of <paramref name="signatureService"/>, then a document time stamp of each of <paramref name="documentServices"/>.</summary>
    private static byte[] Build(Setup setup, SignatureTimestampTests.TestAuthority signatureService, params SignatureTimestampTests.TestAuthority[] documentServices)
    {
        byte[] file;
        using (var plain = PdfDocument.Open(TestPdf.Simple(1).Build()))
            file = PdfSigner.SignUnchecked(plain, 0, Signature(), setup.Signer, Now.AddDays(-30), signatureService);
        foreach (var service in documentServices)
        {
            var path = Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdf-knead-tests")).FullName, $"nachweis-{Guid.NewGuid():N}.pdf");
            using (var doc = PdfDocument.Open(file)) SignedSaving.AddArchiveTimestamp(doc, path, service, null);
            file = File.ReadAllBytes(path);
            File.Delete(path);
        }
        return file;
    }

    [Fact]
    public void ExpiredTsaOfSignatureStamp_CoveredByLaterDocumentStamp_IdentityConfirmed()
    {
        using var setup = new Setup(Now.AddDays(-1));
        using var a = setup.Service("Zeitstempeldienst A", Now.AddDays(-30), Now.AddDays(-1));
        using var b = setup.Service("Zeitstempeldienst B", Now.AddDays(-10), Now.AddYears(3));
        var checks = setup.Verify(Build(setup, a, b));
        var signature = Assert.Single(checks, c => !c.IsDocumentTimestamp);
        Assert.True(signature.IdentityConfirmed, string.Join(" | ", signature.Warnings));
        Assert.Equal(SignatureOutcome.Valid, signature.Outcome);
        Assert.True(signature.Timestamp is { Trusted: true, ThroughProof: true });
        Assert.Contains(signature.Warnings, w => w.StartsWith(Tr.T("Ein späterer Dokument-Zeitstempel belegt, dass die Signatur am {0} schon bestand; geprüft zu diesem Zeitpunkt.", "§").Split('§')[0], StringComparison.Ordinal));
        Assert.True(Assert.Single(checks, c => c.IsDocumentTimestamp).IdentityConfirmed);
    }

    /// <summary>
    /// Two document time stamps: the older one's service (B) expired yesterday as well, the newer one (C) holds — C proves B, B proves the
    /// signature and its stamp.
    /// </summary>
    [Fact]
    public void ExpiredTsaOfSignatureStamp_ChainOfTwoDocumentStamps_OlderTsaExpired_StillCounts()
    {
        using var setup = new Setup(Now.AddDays(-1));
        using var a = setup.Service("Zeitstempeldienst A", Now.AddDays(-30), Now.AddDays(-1));
        using var b = setup.Service("Zeitstempeldienst B", Now.AddDays(-20), Now.AddDays(-1));
        using var c = setup.Service("Zeitstempeldienst C", Now.AddDays(-10), Now.AddYears(3));
        var checks = setup.Verify(Build(setup, a, b, c));
        var signature = Assert.Single(checks, x => !x.IsDocumentTimestamp);
        Assert.True(signature.IdentityConfirmed, string.Join(" | ", signature.Warnings));
        var stamps = checks.Where(x => x.IsDocumentTimestamp).OrderBy(x => x.SigningTime).ToList();
        Assert.Equal(2, stamps.Count);
        Assert.True(stamps[0].IdentityConfirmed, string.Join(" | ", stamps[0].Warnings));   // B, through C
        Assert.True(stamps[1].IdentityConfirmed);
    }

    /// <summary>
    /// The guard: a later document time stamp whose own service nobody trusts proves nothing — the signature stays „identity not confirmed“
    /// (a forged stamp must not stand in as a proof).
    /// </summary>
    [Fact]
    public void ForgedLaterStamp_NotUsedAsPoe()
    {
        using var setup = new Setup(Now.AddDays(-1));
        using var a = setup.Service("Zeitstempeldienst A", Now.AddDays(-30), Now.AddDays(-1));
        using var forged = new SignatureTimestampTests.TestAuthority("Zeitstempeldienst Fremd", at: Now.AddDays(-10));   // self-signed, not under the root
        var checks = setup.Verify(Build(setup, a, forged));
        var signature = Assert.Single(checks, c => !c.IsDocumentTimestamp);
        Assert.False(signature.IdentityConfirmed);
        Assert.False(signature.Timestamp!.Trusted);
    }
}
