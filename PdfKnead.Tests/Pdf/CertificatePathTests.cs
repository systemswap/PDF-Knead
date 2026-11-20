using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The certificate path checked by own rules (RFC 5280, 6.1; docs/ABLAUFPLAN.md, 9.3.2; docs/FEHLERGEDAECHTNIS.md, No. 711): a path to a
/// trusted root that breaks a rule does not confirm the identity, and the check says which certificate breaks which rule
/// (CERTIFICATE_CHAIN_GENERAL_FAILURE, EN 319 102-1, 5.2.6.4) — before it was NO_CERTIFICATE_CHAIN_FOUND without a reason, or, where the
/// platform's engine let it pass, a confirmed identity.
/// </summary>
public sealed class CertificatePathTests
{
    private static InkElement Signature() => new()
    {
        Rect = new PdfRectangle(50, 100, 250, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann"),
    };

    /// <summary>
    /// A root, the authorities below it as <paramref name="authorities"/> describes them, a signer at the end; signed and checked. Issued with a
    /// signature generator, which, unlike issuing with the certificate, does not refuse an issuer that breaks the rules.
    /// </summary>
    private static SignatureCheck Checked(params Action<CertificateRequest>[] authorities) => Checked(-1, authorities);

    /// <summary>As <see cref="Checked(Action{CertificateRequest}[])"/>, the authority number <paramref name="expired"/> (from 0) expired yesterday.</summary>
    private static SignatureCheck Checked(int expired, params Action<CertificateRequest>[] authorities)
    {
        var now = DateTimeOffset.Now;
        var keys = new List<RSA>();
        var certificates = new List<X509Certificate2>();
        try
        {
            var issuerKey = RSA.Create(2048);
            keys.Add(issuerKey);
            var rootRequest = new CertificateRequest("CN=Muster-Wurzel", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            var root = rootRequest.CreateSelfSigned(now.AddDays(-3), now.AddYears(2));
            certificates.Add(root);
            var issuerName = root.SubjectName;
            var chain = new List<X509Certificate2>();
            var serial = (byte)0x10;
            for (var k = 0; k < authorities.Length; k++)
            {
                var key = RSA.Create(2048);
                keys.Add(key);
                var request = new CertificateRequest($"CN=Muster-Zwischenstelle {k + 1}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                authorities[k](request);
                var issued = request.Create(issuerName, X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1),
                    now.AddDays(-30), k == expired ? now.AddDays(-1) : now.AddYears(1).AddDays(-k), [serial++]);
                certificates.Add(issued);
                chain.Add(issued);
                (issuerName, issuerKey) = (issued.SubjectName, key);
            }
            var signerKey = RSA.Create(2048);
            keys.Add(signerKey);
            var signerRequest = new CertificateRequest("CN=Max Mustermann", signerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            signerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
            using var signerCertificate = signerRequest.Create(issuerName, X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1),
                now.AddDays(-1), now.AddMonths(6), [serial]);
            var withKey = signerCertificate.CopyWithPrivateKey(signerKey);
            using var signer = new SigningCertificate(withKey, SigningCertificateSource.File, chain);
            using var plain = PdfDocument.Open(TestPdf.Simple(1).Build());
            var file = PdfSigner.SignUnchecked(plain, 0, Signature(), signer, now);
            using var doc = PdfDocument.Open(file);
            return Assert.Single(SignatureVerifier.Verify(doc, null, [X509CertificateLoader.LoadCertificate(root.RawData)], CancellationToken.None));
        }
        finally
        {
            foreach (var certificate in certificates) certificate.Dispose();
            foreach (var key in keys) key.Dispose();
        }
    }

    private static void Authority(CertificateRequest request) => Authority(request, null);

    private static void Authority(CertificateRequest request, int? pathLength)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, pathLength is not null, pathLength ?? 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
    }

    private static void Rejected(SignatureCheck check, string reason)
    {
        Assert.False(check.IdentityConfirmed);
        Assert.Equal(SubIndication.CertificateChainGeneralFailure, check.Cause);
        Assert.Equal($"INDETERMINATE/{SubIndication.CertificateChainGeneralFailure}", SignatureTexts.Indication(check).ToString());
        Assert.Contains(reason, check.Warnings);
    }

    /// <summary>The guard: root, two authorities, signer — every rule kept, the identity confirmed (the revocation status is not in the file).</summary>
    [Fact]
    public void ValidPath_IdentityConfirmed()
    {
        var check = Checked(Authority, r => Authority(r, 0));
        Assert.True(check.IdentityConfirmed);
        Assert.Equal(SubIndication.TryLater, check.Cause);
    }

    /// <summary>An authority in between without basicConstraints is no authority (RFC 5280, 6.1.4 k).</summary>
    [Fact]
    public void PathWithCaWithoutBasicConstraints_RejectedWithReason()
    {
        var check = Checked(r => r.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true)));
        Rejected(check, Tr.T("Das Zertifikat „{0}“ stellt Zertifikate aus, ist aber nicht als Zertifizierungsstelle ausgewiesen (basicConstraints fehlt).", "Muster-Zwischenstelle 1"));
    }

    /// <summary>An authority in between marked as „no authority“ (cA false).</summary>
    [Fact]
    public void PathWithCaMarkedNoAuthority_RejectedWithReason()
    {
        var check = Checked(r =>
        {
            r.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            r.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        });
        Rejected(check, Tr.T("Das Zertifikat „{0}“ stellt Zertifikate aus, ist laut basicConstraints aber keine Zertifizierungsstelle.", "Muster-Zwischenstelle 1"));
    }

    /// <summary>An authority whose key usage does not allow issuing certificates (RFC 5280, 6.1.4 n).</summary>
    [Fact]
    public void PathWithCaWithoutKeyCertSign_RejectedWithReason()
    {
        var check = Checked(r =>
        {
            r.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            r.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.CrlSign, true));
        });
        Rejected(check, Tr.T("Das Zertifikat „{0}“ darf laut Schlüsselverwendung keine Zertifikate ausstellen.", "Muster-Zwischenstelle 1"));
    }

    /// <summary>
    /// An authority in between that expired yesterday, the signer's certificate still valid: the path holds but for the time —
    /// OUT_OF_BOUNDS_NO_POE with the certificate named (before NO_CERTIFICATE_CHAIN_FOUND without a reason).
    /// </summary>
    [Fact]
    public void ExpiredAuthority_OutOfBoundsWithReason()
    {
        var check = Checked(0, Authority);
        Assert.False(check.IdentityConfirmed);
        Assert.Equal(SubIndication.OutOfBoundsNoPoe, check.Cause);
        var now = DateTimeOffset.Now;
        Assert.Contains(Tr.T("Das Zertifikat „{0}“ der Kette war am {1} nicht gültig (gültig vom {2} bis {3}).", "Muster-Zwischenstelle 1",
            now.LocalDateTime.ToString("d", Tr.Culture), now.AddDays(-30).LocalDateTime.ToString("d", Tr.Culture),
            now.AddDays(-1).LocalDateTime.ToString("d", Tr.Culture)), check.Warnings);
    }

    /// <summary>An authority with pathLenConstraint 0 above another authority (RFC 5280, 6.1.4 l, m).</summary>
    [Fact]
    public void PathLenExceeded_RejectedWithReason()
    {
        var check = Checked(r => Authority(r, 0), Authority);
        Rejected(check, Tr.T("Die Kette ist länger, als das Zertifikat „{0}“ erlaubt (pathLenConstraint).", "Muster-Zwischenstelle 1"));
    }
}
