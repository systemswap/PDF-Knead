using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Valid“ only with the revocation status known (docs/ABLAUFPLAN.md, 2.5, I-2; design decision: without a revocation check yellow): a
/// certificate a trusted authority issued, without validation data in the file, is „not confirmed“ — the reason first; with complete data
/// it is valid; a short-term certificate or one with noRevAvail needs no check. A block without the signer's certificate is not confirmed
/// and proves nothing (EN 319 102-1: NO_SIGNING_CERTIFICATE_FOUND). Shown yellow with the reason first.
/// </summary>
public sealed class RevocationNotCheckedTests
{
    private static InkElement Signature() => new()
    {
        Rect = new PdfRectangle(50, 100, 250, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann"),
    };

    private static byte[] Signed(SigningCertificate certificate, IRevocationSource? revocation = null)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdf-knead-tests")).FullName, $"sperrstatus-{Guid.NewGuid():N}.pdf");
        SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(), false, certificate, DateTimeOffset.Now, revocation: revocation);
        var file = File.ReadAllBytes(path);
        File.Delete(path);
        return file;
    }

    private static SignatureCheck Check(byte[] file, X509Certificate2 root)
    {
        using var doc = PdfDocument.Open(file);
        return Assert.Single(SignatureVerifier.Verify(doc, null, [X509CertificateLoader.LoadCertificate(root.RawData)], CancellationToken.None));
    }

    [Fact]
    public void IssuedCertificate_NoRevocationData_Unconfirmed_WithDataValid()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var without = Check(Signed(ca.Signer), ca.Root);
        Assert.Equal(SignatureOutcome.Unconfirmed, without.Outcome);
        Assert.Equal(Tr.T("Sperrstatus nicht geprüft (keine Prüfdaten in der Datei)."), without.UnconfirmedReason);
        Assert.True(without.IdentityConfirmed);
        Assert.True(SignatureTexts.IsValid(without));   // the integrity is proven
        // shown yellow, the reason first
        var row = new SignatureRow(without);
        Assert.Equal(SignatureTone.Warn, row.Tone);
        Assert.Equal(without.UnconfirmedReason, row.Notes[0]);
        Assert.Equal(Tr.T("Nicht bestätigt"), SignatureTexts.Outcome(without));
        Assert.Equal(new ValidationIndication(ValidationMain.Indeterminate, SubIndication.TryLater), SignatureTexts.Indication(without));
        // with the revocation status in the file (B-LT): valid, green
        var with = Check(Signed(ca.Signer, ca), ca.Root);
        Assert.Equal(SignatureOutcome.Valid, with.Outcome);
        Assert.Equal(SignatureTone.Good, new SignatureRow(with).Tone);
        Assert.Equal("TOTAL-PASSED", SignatureTexts.Indication(with).ToString());
    }

    /// <summary>A short-term certificate (EN 319 412-1, 5.2) needs no revocation check: valid, said so.</summary>
    [Fact]
    public void ShortTermCertificate_NoRevocationNeeded()
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Muster-Kurzzeit-CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddYears(1));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        request.CertificateExtensions.Add(new X509Extension("0.4.0.194121.2.1", [0x05, 0x00], false));   // short-term certificate, value NULL
        using var issued = request.Create(root, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(3), [0x42]);
        using var signer = new SigningCertificate(issued.CopyWithPrivateKey(key), SigningCertificateSource.File);
        var check = Check(Signed(signer), root);
        Assert.Equal(SignatureOutcome.Valid, check.Outcome);
        Assert.Contains(Tr.T("Sperrprüfung nicht nötig (laut Zertifikat)."), check.Warnings);
    }

    /// <summary>A block without the signer's certificate: the value cannot be checked — not confirmed, nothing proven.</summary>
    [Fact]
    public void BlockWithoutSignerCertificate_UnconfirmedNotValid()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var check = Check(Resigned(ca, X509IncludeOption.None, signers: 1), ca.Root);
        Assert.Equal(SignatureOutcome.Unconfirmed, check.Outcome);
        Assert.False(SignatureTexts.IsValid(check));
        Assert.Equal(Tr.T("Der Signaturblock enthält kein Zertifikat des Unterzeichners."), check.UnconfirmedReason);
        Assert.Equal("INDETERMINATE/NO_SIGNING_CERTIFICATE_FOUND", SignatureTexts.Indication(check).ToString());
    }

    /// <summary>Two signers in one block (ISO 32000-2, 12.8.3.3 allows one): which of them signed stays open — not confirmed, not valid.</summary>
    [Fact]
    public void BlockWithTwoSigners_UnconfirmedNotValid()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var check = Check(Resigned(ca, X509IncludeOption.EndCertOnly, signers: 2), ca.Root);
        Assert.Equal(SignatureOutcome.Unconfirmed, check.Outcome);
        Assert.False(SignatureTexts.IsValid(check));
        Assert.Equal(Tr.T("Der Signaturblock enthält mehr als einen Unterzeichner; vorgesehen ist genau einer."), check.UnconfirmedReason);
    }

    /// <summary>
    /// The results in the terms of EN 319 102-1 (docs/ABLAUFPLAN.md, 2.5, step 11), each from a file: an own certificate nobody vouches for,
    /// a block not meant for signing, the document changed, the signature value not the certificate's. The other rows of the table are
    /// checked where their cases are built (revoked, time stamps, MD5, no certificate, revocation status).
    /// </summary>
    [Fact]
    public void Indication_FollowsTable()
    {
        using var ca = new LongTermValidationTests.TestCa();
        // issued by an authority the check does not know: no chain
        Assert.Equal("INDETERMINATE/NO_CERTIFICATE_CHAIN_FOUND", SignatureTexts.Indication(Check(Signed(ca.Signer), RsaRoot())).ToString());
        // a certificate for encryption only: chain constraints
        using var root = RsaRoot();
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, true));
            using var issued = request.Create(root, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddMonths(6), [0x43]);
            using var signer = new SigningCertificate(issued.CopyWithPrivateKey(key), SigningCertificateSource.File);
            // signed as another program would: PDF Knead itself refuses such a certificate (No. 526)
            using var plain = PdfDocument.Open(TestPdf.Simple(1).Build());
            var foreign = PdfSigner.SignUnchecked(plain, 0, Signature(), signer, DateTimeOffset.Now);
            Assert.Equal("INDETERMINATE/CHAIN_CONSTRAINTS_FAILURE", SignatureTexts.Indication(Check(foreign, root)).ToString());
        }
        // a byte of the signed content changed: hash failure
        var changed = Signed(ca.Signer);
        var at = Encoding.ASCII.GetString(changed).IndexOf("stream", StringComparison.Ordinal) + 12;
        changed[at] ^= 0x01;
        Assert.Equal("TOTAL-FAILED/HASH_FAILURE", SignatureTexts.Indication(Check(changed, ca.Root)).ToString());
        // the value of another key: signature value failure
        using var otherKey = RSA.Create(2048);
        var forged = Signed(ca.Signer);
        long[] range;
        using (var doc = PdfDocument.Open(forged)) range = DigitalSignatures.Find(doc).Single().ByteRange;
        var block = Convert.FromHexString(Encoding.ASCII.GetString(forged, (int)range[1] + 1, (int)(range[2] - range[1] - 2)));
        var value = otherKey.SignData([1, 2, 3], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cms = new SignedCms();
        cms.Decode(Core.Pdf.Signatures.CmsSignature.Der(block)!);
        var encoded = cms.Encode();
        var old = cms.SignerInfos[0].GetSignature();
        var where = encoded.AsSpan().IndexOf(old);
        Assert.True(where > 0);
        value.CopyTo(encoded, where);
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(encoded));
        Array.Fill(forged, (byte)'0', (int)range[1] + 1, (int)(range[2] - range[1] - 2));
        hex.CopyTo(forged, (int)range[1] + 1);
        Assert.Equal("TOTAL-FAILED/SIG_CRYPTO_FAILURE", SignatureTexts.Indication(Check(forged, ca.Root)).ToString());
    }

    private static X509Certificate2 RsaRoot()
    {
        using var rootKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=Muster-Wurzel", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddYears(1));
    }

    /// <summary>A signed file whose block is replaced by one made by hand: the given number of signers, certificates as given.</summary>
    private static byte[] Resigned(LongTermValidationTests.TestCa ca, X509IncludeOption include, int signers)
    {
        var signed = Signed(ca.Signer);
        long[] range;
        using (var doc = PdfDocument.Open(signed)) range = DigitalSignatures.Find(doc).Single().ByteRange;
        byte[] content = [.. signed.AsSpan(0, (int)range[1]), .. signed.AsSpan((int)range[2], (int)range[3])];
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        for (var i = 0; i < signers; i++)
            cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, ca.Signer.Certificate)
                { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), IncludeOption = include }, silent: true);
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(cms.Encode()));
        var gap = (int)(range[2] - range[1] - 2);
        Array.Fill(signed, (byte)'0', (int)range[1] + 1, gap);
        hex.CopyTo(signed, (int)range[1] + 1);
        return signed;
    }
}
