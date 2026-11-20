using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Signatures.TrustLists;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Document time stamps and PAdES B-LTA (KONZEPT.md, section 5.17, „Archiv-Zeitstempel“): signing with time stamp, validation data and a
/// document time stamp keeps the signature valid and the document time stamp checks out; a later change breaks it; renewing appends a second
/// one and the validation data, every earlier signature staying valid. Stand-ins for the services (no network).
/// </summary>
public sealed class DocumentTimestampTests
{
    private static InkElement Signature(double x = 50) => new()
    {
        Rect = new PdfRectangle(x, 100, x + 200, 180),
        Strokes = [[0, 20, 40, 70, 90, 10, 140, 60]],
        StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann"),
    };

    private static string TempPath() => Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdf-knead-tests")).FullName, $"archiv-{Guid.NewGuid():N}.pdf");

    private static IReadOnlyList<SignatureCheck> Verify(byte[] file, LongTermValidationTests.TestCa ca)
    {
        using var doc = PdfDocument.Open(file);
        return SignatureVerifier.Verify(doc, null, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], CancellationToken.None);
    }

    private static byte[] SignArchived(LongTermValidationTests.TestCa ca, SignatureTimestampTests.TestAuthority authority, byte[]? source = null,
        InkElement? signature = null)
    {
        using var doc = PdfDocument.Open(source ?? TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        var path = TempPath();
        SignedSaving.Save(doc, [pages[0].WithElements([.. pages[0].Elements, signature ?? Signature()]), .. pages.Skip(1)], path, new PdfSaveOptions(), false, ca.Signer, DateTimeOffset.Now,
            timestamps: authority, revocation: ca, archive: true);
        var file = File.ReadAllBytes(path);
        File.Delete(path);
        return file;
    }

    [Fact]
    public void Archived_SignatureValid_DocumentTimestampChecked()
    {
        using var ca = new LongTermValidationTests.TestCa();
        using var authority = new SignatureTimestampTests.TestAuthority();
        var file = SignArchived(ca, authority);
        Assert.Equal(2, authority.Requests);   // one for the signature, one for the document
        var checks = Verify(file, ca);
        Assert.Equal(2, checks.Count);
        var signature = Assert.Single(checks, c => !c.IsDocumentTimestamp);
        Assert.True(SignatureTexts.IsValid(signature), signature.Problem);
        Assert.Equal(LaterChanges.ValidationData, signature.Later);   // validation data and the time stamp after it: no change at all
        var stamp = Assert.Single(checks, c => c.IsDocumentTimestamp);
        Assert.True(SignatureTexts.IsValid(stamp), stamp.Problem);
        Assert.Equal("Zeitstempeldienst Test", stamp.SignerName);
        Assert.Equal(SignatureTimestampTests.StampTime, stamp.SigningTime);
        Assert.Equal("ETSI.RFC3161", stamp.Method);
        Assert.False(stamp.IdentityConfirmed);   // the test service is known to nobody
    }

    /// <summary>
    /// A certified form with all fields locked, then a document time stamp: the stamp's new field is no change to a locked field — the lock
    /// lists the fields there were at signing (docs/FEHLERGEDAECHTNIS.md, „Feldsperre und Dokument-Zeitstempel“).
    /// </summary>
    [Fact]
    public void CertifiedAndLocked_Archived_StaysValid_StampFieldNotLocked()
    {
        using var ca = new LongTermValidationTests.TestCa();
        using var authority = new SignatureTimestampTests.TestAuthority();
        var locking = Signature() with { Digital = new DigitalSigning("Max Mustermann", Certify: CertificationLevel.FormsAndSigning, LockFields: true) };
        var checks = Verify(SignArchived(ca, authority, FormFillTests.AllKinds(), locking), ca);
        var signature = Assert.Single(checks, c => !c.IsDocumentTimestamp);
        Assert.True(SignatureTexts.IsValid(signature), signature.Problem);
        Assert.Equal(CertificationLevel.FormsAndSigning, signature.Certification);
        var stamp = Assert.Single(checks, c => c.IsDocumentTimestamp);
        Assert.True(SignatureTexts.IsValid(stamp), stamp.Problem);
        Assert.Contains("Name", signature.Lock!.Fields);
        Assert.False(signature.Lock.Locks(stamp.FieldName));
    }

    [Fact]
    public void ChangedAfterTheStamp_Detected()
    {
        using var ca = new LongTermValidationTests.TestCa();
        using var authority = new SignatureTimestampTests.TestAuthority();
        var file = SignArchived(ca, authority);
        // a byte of the first page's content (the structure of the file stays intact): covered by the signature and the document time stamp
        var at = System.Text.Encoding.ASCII.GetString(file).IndexOf("stream", StringComparison.Ordinal) + 12;
        file[at] ^= 0x01;
        var checks = Verify(file, ca);
        Assert.False(SignatureTexts.IsValid(checks.Single(c => c.IsDocumentTimestamp)));
        Assert.False(SignatureTexts.IsValid(checks.Single(c => !c.IsDocumentTimestamp)));
    }

    [Fact]
    public void Renewed_SecondStampAppended_AllStayValid()
    {
        using var ca = new LongTermValidationTests.TestCa();
        using var authority = new SignatureTimestampTests.TestAuthority();
        var once = SignArchived(ca, authority);
        using var doc = PdfDocument.Open(once);
        var path = TempPath();
        var result = SignedSaving.AddArchiveTimestamp(doc, path, authority, ca);
        var twice = File.ReadAllBytes(path);
        File.Delete(path);
        Assert.Empty(result.LongTermMissing);
        Assert.Equal(once, twice.AsSpan(0, once.Length).ToArray());   // only appended
        var checks = Verify(twice, ca);
        Assert.Equal(3, checks.Count);
        Assert.Equal(2, checks.Count(c => c.IsDocumentTimestamp));
        // the second one named as what it is (it was „Unterschrift2“)
        Assert.Equal([Tr.T("Dokument-Zeitstempel"), Tr.T("Dokument-Zeitstempel") + " 2"], checks.Where(c => c.IsDocumentTimestamp).Select(c => c.FieldName).Order());
        Assert.All(checks, c => Assert.True(SignatureTexts.IsValid(c), c.Problem));
        Assert.All(checks.Where(c => !c.CoversWholeFile), c => Assert.Equal(LaterChanges.ValidationData, c.Later));
    }

    /// <summary>
    /// A document time stamp whose time lies before the signature's own stamp, though it covers it (EN 319 102-1, 5.5.4, step 4e:
    /// TIMESTAMP_ORDER_FAILURE): the file contradicts itself about time — the signature and the first document stamp are not confirmed, the
    /// reason first. The same time is no contradiction (<see cref="Renewed_SecondStampAppended_AllStayValid"/>).
    /// </summary>
    [Fact]
    public void DocumentStampOlderThanSignatureStamp_OrderReported()
    {
        using var ca = new LongTermValidationTests.TestCa();
        using var authority = new SignatureTimestampTests.TestAuthority();
        using var backdated = new SignatureTimestampTests.TestAuthority(at: SignatureTimestampTests.StampTime.AddDays(-1));
        using var doc = PdfDocument.Open(SignArchived(ca, authority));
        var path = TempPath();
        SignedSaving.AddArchiveTimestamp(doc, path, backdated, ca);
        var file = File.ReadAllBytes(path);
        File.Delete(path);
        var checks = Verify(file, ca);
        var reason = Tr.T("Die Zeitstempel widersprechen sich: der spätere Dokument-Zeitstempel „{0}“ trägt eine frühere Zeit.", Tr.T("Dokument-Zeitstempel") + " 2");
        var signature = Assert.Single(checks, c => !c.IsDocumentTimestamp);
        Assert.Equal(SignatureOutcome.Unconfirmed, signature.Outcome);
        Assert.Equal(reason, signature.UnconfirmedReason);
        Assert.True(SignatureTexts.IsValid(signature));   // the bytes are still the signed ones
        Assert.Equal("INDETERMINATE/TIMESTAMP_ORDER_FAILURE", SignatureTexts.Indication(signature).ToString());
        var first = Assert.Single(checks, c => c.FieldName == Tr.T("Dokument-Zeitstempel"));
        Assert.Equal(SignatureOutcome.Unconfirmed, first.Outcome);
        Assert.Equal(reason, first.UnconfirmedReason);
        var last = Assert.Single(checks, c => c.FieldName == Tr.T("Dokument-Zeitstempel") + " 2");
        Assert.NotEqual(SignatureOutcome.Unconfirmed, last.Outcome);   // nothing covers the last one
    }

    /// <summary>
    /// With a real time-stamping authority (docs/PRUEFUNGEN.md, „Archiv-Zeitstempel“): only when PDFKNEAD_ZEITSTEMPEL names its address. Own
    /// certificate, signature time stamp, validation data and document time stamp; with PDFKNEAD_ABLAGE=&lt;folder&gt; the file is kept.
    /// </summary>
    [Fact]
    public void RealService_WhenGiven()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ZEITSTEMPEL") is not { Length: > 0 } server || !Core.Platform.HttpTimestampAuthority.TryParse(server, out var address)) return;
        var own = Core.Platform.SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"archiv-{Guid.NewGuid():N}.p12"));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        var path = TempPath();
        var result = SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(), false, own, DateTimeOffset.Now,
            timestamps: new Core.Platform.HttpTimestampAuthority(address), revocation: new Core.Platform.HttpRevocationSource(), archive: true);
        var file = File.ReadAllBytes(path);
        File.Delete(path);
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "archiv-echt.pdf"), file);
        }
        Assert.Empty(result.LongTermMissing);
        using var saved = PdfDocument.Open(file);
        var checks = SignatureVerifier.Verify(saved);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.True(SignatureTexts.IsValid(c), c.Problem));
        var stamp = Assert.Single(checks, c => c.IsDocumentTimestamp);
        Assert.True(stamp.IdentityConfirmed);   // a service the system trusts
        Assert.Equal(RevocationInfo.Coverage.Complete, stamp.LongTerm);
    }

    /// <summary>
    /// „Schutz erneuern bis spätestens“ of a document time stamp (docs/ABLAUFPLAN.md, 9.3.4, step 3; docs/FEHLERGEDAECHTNIS.md, No. 714): the
    /// end of the service's certificate when that comes first; with a service whose RSA key is of legacy length (2 048 bits) and whose
    /// certificate runs longer, the end of 2028 (ETSI TS 119 312).
    /// </summary>
    [Fact]
    public void RenewBefore_IsTsaNotAfter()
    {
        using var ca = new LongTermValidationTests.TestCa();
        using var shortLived = new SignatureTimestampTests.TestAuthority(issuer: ca.Root, notAfter: DateTimeOffset.Now.AddMonths(8));
        var stamp = Assert.Single(Verify(SignArchived(ca, shortLived), ca), c => c.IsDocumentTimestamp);
        Assert.Equal(new DateTimeOffset(shortLived.Certificate.NotAfter), stamp.RenewBefore);
        using var longLived = new SignatureTimestampTests.TestAuthority();   // valid until 2031, RSA 2 048
        var legacy = Assert.Single(Verify(SignArchived(ca, longLived), ca), c => c.IsDocumentTimestamp);
        Assert.Equal(new DateTimeOffset(2028, 12, 31, 12, 0, 0, TimeSpan.Zero), legacy.RenewBefore);
        Assert.Null(Assert.Single(Verify(SignArchived(ca, longLived), ca), c => !c.IsDocumentTimestamp).RenewBefore);
    }

    /// <summary>
    /// The PAdES level of own files (ETSI EN 319 142-1; I-12, docs/ABLAUFPLAN.md, 9.3.4, step 4): signed plainly B-B, with a time stamp B-T,
    /// with validation data B-LT, with a document time stamp after them B-LTA; the document time stamp itself has none.
    /// </summary>
    [Fact]
    public void PadesLevel_FromOwnFiles()
    {
        using var ca = new LongTermValidationTests.TestCa();
        using var authority = new SignatureTimestampTests.TestAuthority();
        byte[] plain, stamped;
        using (var doc = PdfDocument.Open(TestPdf.Simple(1).Build())) plain = PdfSigner.Sign(doc, 0, Signature(), ca.Signer, DateTimeOffset.Now);
        using (var doc = PdfDocument.Open(TestPdf.Simple(1).Build())) stamped = PdfSigner.Sign(doc, 0, Signature(), ca.Signer, DateTimeOffset.Now, authority);
        byte[] longTerm;
        using (var doc = PdfDocument.Open(TestPdf.Simple(1).Build()))
        {
            var pages = PageItem.From(doc);
            var path = TempPath();
            SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(), false, ca.Signer, DateTimeOffset.Now,
                timestamps: authority, revocation: ca);
            longTerm = File.ReadAllBytes(path);
            File.Delete(path);
        }
        string? Level(byte[] file) => SignatureTexts.PadesLevel(Assert.Single(Verify(file, ca), c => !c.IsDocumentTimestamp));
        Assert.Equal("B-B", Level(plain));
        Assert.Equal("B-T", Level(stamped));
        Assert.Equal("B-LT", Level(longTerm));
        var archived = Verify(SignArchived(ca, authority), ca);
        Assert.Equal("B-LTA", SignatureTexts.PadesLevel(Assert.Single(archived, c => !c.IsDocumentTimestamp)));
        Assert.Null(SignatureTexts.PadesLevel(Assert.Single(archived, c => c.IsDocumentTimestamp)));
    }

    /// <summary>
    /// Qualified signatures (signer of the listed CA, declaring itself qualified) and an archive time stamp of a service the EU list does not
    /// name: the hint (docs/ABLAUFPLAN.md, 9.3.4, step 2); with the listed qualified service none, and without the lists none.
    /// </summary>
    [Fact]
    public void Archive_QualifiedSignature_NonQualifiedService_Hint()
    {
        using var world = new TrustListTests.World();
        var set = world.Download();
        using var signer = world.Signer();
        using var unlisted = new SignatureTimestampTests.TestAuthority("Zeitstempeldienst Unbekannt", at: DateTimeOffset.Now.AddMinutes(-5));
        byte[] signed;
        using (var doc = PdfDocument.Open(TestPdf.Simple(1).Build())) signed = PdfSigner.Sign(doc, 0, Signature(), signer, DateTimeOffset.Now);
        var path = TempPath();
        using (var doc = PdfDocument.Open(signed)) SignedSaving.AddArchiveTimestamp(doc, path, unlisted, null);
        var archived = File.ReadAllBytes(path);
        File.Delete(path);
        IReadOnlyList<SignatureCheck> Checks(TrustListSet? lists)
        {
            using var doc = PdfDocument.Open(archived);
            return SignatureVerifier.Verify(doc, null, [X509CertificateLoader.LoadCertificate(world.Ca.Certificate.RawData)], CancellationToken.None, () => lists);
        }
        var withLists = Checks(set);
        Assert.True(Assert.Single(withLists, c => !c.IsDocumentTimestamp).Listed is { Qualified: true });
        Assert.Equal(Tr.T("Die Datei trägt qualifizierte Signaturen, der Dienst des Archiv-Zeitstempels ist laut EU-Vertrauensliste aber nicht qualifiziert; für den Beweiswert wäre ein qualifizierter Dienst besser."),
            SignatureTexts.ArchiveServiceHint(withLists));
        Assert.Null(SignatureTexts.ArchiveServiceHint(Checks(null)));
    }

    /// <summary>
    /// A PDF/A-2b file signed with a time stamp, validation data and a document time stamp (B-LTA) stays PDF/A-2b: the block of a signature
    /// with a time stamp got 36 KB of room, more than the 32 767 bytes a string may have (ISO 19005-2, 6.1.13; the PDF/A reference validator
    /// refused every file from B-T on — docs/ABLAUFPLAN.md, 9.3.4, step 5; docs/FEHLERGEDAECHTNIS.md, No. 715).
    /// </summary>
    [Fact]
    public void PdfA2b_SignedUpToBLta_StaysConforming()
    {
        byte[] pdfa;
        using (var plain = PdfDocument.Open(TestPdf.Simple(1).Build()))
        using (var output = new MemoryStream())
        {
            PdfWriter.Write(plain, PageItem.From(plain), output, new PdfSaveOptions { PdfA = PdfAConformance.A2b });
            pdfa = output.ToArray();
        }
        using var ca = new LongTermValidationTests.TestCa();
        using var authority = new SignatureTimestampTests.TestAuthority();
        var file = SignArchived(ca, authority, pdfa);
        Assert.Equal("B-LTA", SignatureTexts.PadesLevel(Assert.Single(Verify(file, ca), c => !c.IsDocumentTimestamp)));
        using var doc = PdfDocument.Open(file);
        Assert.Empty(PdfACheck.Check(doc, PdfAConformance.A2b).Select(v => v.ToString()));
    }
}
