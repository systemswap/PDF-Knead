using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// A revoked certificate is never „valid“ without proof it was signed before (docs/ABLAUFPLAN.md, 2.5, I-1; EN 319 102-1, 5.2.6.4, 5.3.4,
/// 5.5.4): the answers of the security store are read with time and reason of the revocation; the newest answer counts; an answer without
/// nextUpdate is not fresh. Revoked after a proven time — valid with a hint; before it — invalid; without proof — not confirmed (yellow).
/// </summary>
public sealed class RevocationStatusTests
{
    private static InkElement Signature() => new()
    {
        Rect = new PdfRectangle(50, 100, 250, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann"),
    };

    private static string TempPath() => Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdf-knead-tests")).FullName, $"sperre-{Guid.NewGuid():N}.pdf");

    /// <summary>Signed with the test authority's certificate; with <paramref name="longTerm"/> its validation data appended (B-LT).</summary>
    private static byte[] Signed(LongTermValidationTests.TestCa ca, bool longTerm)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        var path = TempPath();
        SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(), false, ca.Signer, DateTimeOffset.Now,
            revocation: longTerm ? ca : null);
        var file = File.ReadAllBytes(path);
        File.Delete(path);
        return file;
    }

    /// <summary>The file with validation data appended (merged with the store it has).</summary>
    private static byte[] WithData(byte[] file, RevocationInfo.Found found)
    {
        using var doc = PdfDocument.Open(file);
        return [.. doc.FileBytes(0, doc.FileSize), .. LongTermValidation.Update(doc, found)];
    }

    private static SignatureCheck Check(byte[] file, LongTermValidationTests.TestCa ca)
    {
        using var doc = PdfDocument.Open(file);
        return SignatureVerifier.Verify(doc, null, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], CancellationToken.None)
            .Single(c => !c.IsDocumentTimestamp);
    }

    /// <summary>A CRL of the test authority, issued at <paramref name="thisUpdate"/>, listing the signer revoked at <paramref name="revokedAt"/> if given.</summary>
    private static byte[] Crl(LongTermValidationTests.TestCa ca, DateTimeOffset thisUpdate, DateTimeOffset? revokedAt, X509RevocationReason? reason = null, int number = 1)
    {
        var builder = new CertificateRevocationListBuilder();
        if (revokedAt is { } at) builder.AddEntry(ca.Signer.Certificate.SerialNumberBytes.Span, at, reason);
        builder.AddEntry([0x55], thisUpdate.AddDays(-10));
        return builder.Build(ca.Root, number, thisUpdate.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, thisUpdate);
    }

    [Fact]
    public void ForeignFile_SignerRevokedInDss_NotValid()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var good = Signed(ca, longTerm: true);
        Assert.Equal(SignatureOutcome.Valid, Check(good, ca).Outcome);
        // another program added an answer „revoked“ (as new as the good one: revoked wins at the same time)
        ca.OcspMode = "gesperrt";
        var responder = RevocationInfo.Addresses(ca.Signer.Certificate, ocsp: true).Single();
        var revoked = ca.Ocsp(responder, RevocationInfo.OcspRequest(ca.Signer.Certificate, ca.Root))!;
        var check = Check(WithData(good, new RevocationInfo.Found([], [revoked], [], [])), ca);
        Assert.NotEqual(SignatureOutcome.Valid, check.Outcome);
        Assert.Equal(SignatureOutcome.Unconfirmed, check.Outcome);   // nothing proves it was signed before
        Assert.Contains("gesperrt", check.UnconfirmedReason);
        Assert.False(check.IdentityConfirmed);
        Assert.Equal("INDETERMINATE/REVOKED_NO_POE", SignatureTexts.Indication(check).ToString());
    }

    [Fact]
    public void OlderRevokedNewerGood_Good()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var now = DateTimeOffset.UtcNow;
        var file = WithData(Signed(ca, longTerm: false), new RevocationInfo.Found([ca.Root.RawData], [],
            [Crl(ca, now.AddDays(-3), now.AddDays(-4), number: 1), Crl(ca, now.AddDays(-1), null, number: 2)], []));
        var check = Check(file, ca);
        Assert.Equal(SignatureOutcome.Valid, check.Outcome);
        Assert.Equal(RevocationInfo.Coverage.Complete, check.LongTerm);
        Assert.DoesNotContain(check.Warnings, w => w.Contains("gesperrt"));
    }

    [Fact]
    public void OlderGoodNewerRevoked_Revoked()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var now = DateTimeOffset.UtcNow;
        var file = WithData(Signed(ca, longTerm: false), new RevocationInfo.Found([ca.Root.RawData], [],
            [Crl(ca, now.AddDays(-3), null, number: 1), Crl(ca, now.AddDays(-1), now.AddDays(-2), number: 2)], []));
        var check = Check(file, ca);
        Assert.Equal(SignatureOutcome.Unconfirmed, check.Outcome);
        Assert.Equal(RevocationInfo.Coverage.Revoked, check.LongTerm);
    }

    [Fact]
    public void CrlEntryDateAndReasonRead()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var now = DateTimeOffset.UtcNow;
        var revokedAt = new DateTimeOffset(now.Year, now.Month, now.Day, 8, 30, 0, TimeSpan.Zero).AddDays(-2);
        var status = RevocationInfo.Embedded(ca.Signer.Certificate, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], [],
            [Crl(ca, now.AddDays(-1), revokedAt, X509RevocationReason.KeyCompromise)], now);
        Assert.Equal(RevocationInfo.Coverage.Revoked, status.Coverage);
        Assert.Equal(revokedAt, status.RevokedAt);
        Assert.Equal(1, status.Reason);
        Assert.True(status.EndRevoked);
        Assert.False(status.OnHold);
        // certificateHold: on hold, not revoked for good
        var held = RevocationInfo.Embedded(ca.Signer.Certificate, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], [],
            [Crl(ca, now.AddDays(-1), revokedAt, X509RevocationReason.CertificateHold)], now);
        Assert.True(held.OnHold);
    }

    [Fact]
    public void OcspRevokedTimeRead()
    {
        using var ca = new LongTermValidationTests.TestCa { OcspMode = "gesperrt" };
        var responder = RevocationInfo.Addresses(ca.Signer.Certificate, ocsp: true).Single();
        var revoked = ca.Ocsp(responder, RevocationInfo.OcspRequest(ca.Signer.Certificate, ca.Root))!;
        var status = RevocationInfo.Embedded(ca.Signer.Certificate, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], [revoked], [], DateTimeOffset.UtcNow);
        Assert.Equal(RevocationInfo.Coverage.Revoked, status.Coverage);
        Assert.InRange(status.RevokedAt!.Value, DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(5));
        Assert.Null(status.Reason);   // the test authority gives none
    }

    /// <summary>
    /// An OCSP answer without nextUpdate given before the signing: not fresh (EN 319 102-1, 5.2.5) — so it is not embedded either; the CRL
    /// takes its place, the data is complete. Before, it was embedded and then not counted: PDF-Knead's own file turned yellow at once
    /// (docs/FEHLERGEDAECHTNIS.md, No. 478). Without a CRL, the data stays incomplete.
    /// </summary>
    [Fact]
    public void OcspWithoutNextUpdate_BeforeSigning_NotEmbedded_CrlInstead()
    {
        using (var ca = new LongTermValidationTests.TestCa { OcspMode = "ohne-ende" })
        {
            var check = Check(Signed(ca, longTerm: true), ca);
            Assert.Equal(RevocationInfo.Coverage.Complete, check.LongTerm);
            Assert.Equal(1, ca.CrlRequests);
        }
        using (var ca = new LongTermValidationTests.TestCa { OcspMode = "ohne-ende", CrlMode = "keine" })
            Assert.Equal(RevocationInfo.Coverage.Incomplete, Check(Signed(ca, longTerm: true), ca).LongTerm);
    }

    /// <summary>An OCSP answer without nextUpdate given after the signing: it proves the status at that time — fresh, complete (No. 478).</summary>
    [Fact]
    public void OcspWithoutNextUpdate_AfterSigning_Fresh()
    {
        using var ca = new LongTermValidationTests.TestCa { OcspMode = "ohne-ende-jetzt", CrlMode = "keine" };
        var check = Check(Signed(ca, longTerm: true), ca);
        Assert.Equal(RevocationInfo.Coverage.Complete, check.LongTerm);
        Assert.Equal(SignatureOutcome.Valid, check.Outcome);
    }

    // ------------------------------------------------------------ the verdict on a revoked certificate (the proven time from a trusted time stamp)

    [Fact]
    public void Verdict_RevokedAfterProvenTime_ValidWithHint()
    {
        var revoked = new RevocationInfo.EmbeddedStatus(RevocationInfo.Coverage.Revoked, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), 1, EndRevoked: true);
        var verdict = SignatureVerifier.RevokedVerdict(revoked, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Null(verdict.Problem);
        Assert.Null(verdict.Unconfirmed);
        Assert.Contains("früher belegt", verdict.Hint);
    }

    [Fact]
    public void Verdict_RevokedBeforeProvenTime_Invalid()
    {
        var revoked = new RevocationInfo.EmbeddedStatus(RevocationInfo.Coverage.Revoked, new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero), 1, EndRevoked: true);
        var verdict = SignatureVerifier.RevokedVerdict(revoked, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains(SignatureTexts.RevocationReason(1), verdict.Problem);
    }

    [Theory]
    [InlineData(true, false, null)]    // the signer's certificate, no proof
    [InlineData(false, false, null)]   // an issuer
    [InlineData(true, true, null)]     // on hold
    public void Verdict_WithoutProofOrIssuerOrHold_Unconfirmed(bool end, bool onHold, string? _)
    {
        var revoked = new RevocationInfo.EmbeddedStatus(RevocationInfo.Coverage.Revoked, DateTimeOffset.UtcNow.AddDays(-1), null, end, onHold);
        var verdict = SignatureVerifier.RevokedVerdict(revoked, null);
        Assert.Null(verdict.Problem);
        Assert.NotNull(verdict.Unconfirmed);
    }
}
