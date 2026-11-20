using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Signature or seal, and at which level (docs/ABLAUFPLAN.md, 2.6, I-7): read from the certificate's statements with their content
/// (EN 319 412-5: QcType, QcCClegislation, QcSSCD), the semantics identifier (EN 319 412-1), the subject and the key usage
/// (EN 319 412-2/-3) — offline, each case from a signed file checked with the issuing authority as the trusted root.
/// </summary>
public sealed class SignatureNatureTests
{
    /// <summary>One QC statement: its identifier and, optionally, its encoded content.</summary>
    private sealed record Statement(string Id, Action<AsnWriter>? Info = null);

    private static Statement Compliance => new("0.4.0.1862.1.1");

    private static Statement Sscd => new("0.4.0.1862.1.4");

    private static Statement Type(string type) => new("0.4.0.1862.1.6", w =>
    {
        using (w.PushSequence()) w.WriteObjectIdentifier(type);
    });

    private static Statement Legislation(string country) => new("0.4.0.1862.1.7", w =>
    {
        using (w.PushSequence()) w.WriteCharacterString(UniversalTagNumber.PrintableString, country);
    });

    private static Statement Semantics(string id) => new("1.3.6.1.5.5.7.11.2", w =>
    {
        using (w.PushSequence()) w.WriteObjectIdentifier(id);
    });

    private static byte[] Encode(Statement[] statements)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
            foreach (var statement in statements)
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(statement.Id);
                    statement.Info?.Invoke(writer);
                }
        return writer.Encode();
    }

    /// <summary>A file signed with a certificate an authority issued with these statements, checked with that authority trusted.</summary>
    private static SignatureCheck Checked(Statement[] statements, X509KeyUsageFlags usage = X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.DigitalSignature,
        string subject = "CN=Max Mustermann")
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Muster-Anbieter", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddYears(1));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName(subject), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
        if (statements.Length > 0) request.CertificateExtensions.Add(new X509Extension("1.3.6.1.5.5.7.1.3", Encode(statements), false));
        using var issued = request.Create(root, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddMonths(6), [0x51]);
        using var signer = new SigningCertificate(issued.CopyWithPrivateKey(key), SigningCertificateSource.File);
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var signed = PdfSigner.Sign(doc, 0, new InkElement
        {
            Rect = new PdfRectangle(50, 100, 250, 180),
            Strokes = [[0, 20, 40, 70, 90, 10]],
            StrokeWidth = 3,
            Digital = new DigitalSigning("Max Mustermann"),
        }, signer, DateTimeOffset.Now);
        using var saved = PdfDocument.Open(signed);
        var check = Assert.Single(SignatureVerifier.Verify(saved, null, [X509CertificateLoader.LoadCertificate(root.RawData)], CancellationToken.None));
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        return check;
    }

    private static SignatureNature Nature(SignatureCheck check) => SignatureNatures.Of(check)!;

    [Fact]
    public void QcTypeEseal_IsSeal_NotSignature()
    {
        var check = Checked([Compliance, Sscd, Type("0.4.0.1862.1.6.2")], subject: "CN=Muster GmbH, O=Muster GmbH, 2.5.4.97=VATDE-123456789, C=DE");
        var nature = Nature(check);
        Assert.Equal(SignatureKind.Seal, nature.Kind);
        Assert.Equal(NatureLevel.Qualified, nature.Level);
        Assert.Equal(Tr.T("{0} (laut Zertifikat)", Tr.T("qualifiziertes elektronisches Siegel")), SignatureTexts.Level(check));
        Assert.DoesNotContain(Tr.T("Signatur"), SignatureTexts.Level(check));
        Assert.Contains(Tr.T("Organisation"), SignatureTexts.LevelMeaning(SignatureLevel.Qualified, SignatureKind.Seal));
    }

    [Fact]
    public void QcCClegislationCH_NotEuQualified()
    {
        var check = Checked([Compliance, Sscd, Legislation("CH")]);
        var nature = Nature(check);
        Assert.Equal(NatureLevel.Advanced, nature.Level);
        Assert.Equal(["CH"], nature.OtherLaw);
        Assert.Equal(Tr.T("Das Zertifikat ist nach dem Recht von {0} qualifiziert, nicht nach EU-Recht.", "CH"), nature.Reasons[0]);
        Assert.Equal(SignatureLevel.Advanced, check.Certificate!.Level);
    }

    [Fact]
    public void QcComplianceWithoutSscd_AdvancedQc()
    {
        var check = Checked([Compliance]);
        Assert.Equal(NatureLevel.AdvancedQc, Nature(check).Level);
        Assert.Equal(Tr.T("{0} (laut Zertifikat)", Tr.T("fortgeschrittene elektronische Signatur mit qualifiziertem Zertifikat")), SignatureTexts.Level(check));
    }

    [Fact]
    public void KeyUsageDigitalSignatureOnly_NotCommitment()
    {
        var check = Checked([Compliance, Sscd], X509KeyUsageFlags.DigitalSignature);
        var nature = Nature(check);
        Assert.Equal(NatureLevel.Advanced, nature.Level);
        Assert.Equal(Tr.T("Das Zertifikat ist nur zur Anmeldung bestimmt, nicht für Willenserklärungen (Schlüsselverwendung)."), nature.Reasons[0]);
        Assert.Equal(SignatureLevel.Advanced, check.Certificate!.Level);
    }

    [Fact]
    public void PseudonymAttribute_Reported()
    {
        var check = Checked([Compliance, Sscd], subject: "CN=Musterpseudonym, 2.5.4.65=Musterpseudonym, C=DE");
        Assert.True(Nature(check).Pseudonym);
        Assert.Contains(Tr.T("Das Zertifikat nennt ein Pseudonym, nicht den Namen der Person."), SignatureTexts.NatureNotes(check));
    }

    [Fact]
    public void LegalPersonSemanticsId_Seal()
    {
        // no QcType, no QcCompliance: the semantics identifier „legal person“ points to a seal
        var check = Checked([Semantics("0.4.0.194121.1.2")], subject: "CN=Muster GmbH, O=Muster GmbH, C=DE");
        Assert.Equal(SignatureKind.Seal, Nature(check).Kind);
        Assert.Equal(Tr.T("fortgeschrittenes elektronisches Siegel (höchstens)"), SignatureTexts.Level(check));
        // with QcCompliance and no QcType it is a signature (EN 319 412-5, Tab. 1)
        Assert.Equal(SignatureKind.Signature, Nature(Checked([Compliance, Semantics("0.4.0.194121.1.2")])).Kind);
    }

    [Fact]
    public void QscdText_YesOrUnknown()
    {
        Assert.Equal(Tr.T("ja"), SignatureTexts.Qscd(Nature(Checked([Compliance, Sscd])))!);
        Assert.Equal(Tr.T("unbekannt"), SignatureTexts.Qscd(Nature(Checked([Compliance])))!);
        foreach (var check in new[] { Checked([Compliance, Sscd]), Checked([Compliance]) })
            Assert.All(SignatureTexts.NatureNotes(check).Append(SignatureTexts.Qscd(Nature(check))!),
                text => Assert.DoesNotMatch("(?i)karte|software|token", text));
        // nothing declared qualified: nothing said about the device
        Assert.Null(SignatureTexts.Qscd(Nature(Checked([]))));
    }

    /// <summary>QcType with two values (EN 319 412-5, 4.2.3: exactly one): the kind stays open, never qualified (No. 483).</summary>
    [Fact]
    public void QcTypeWithTwoValues_NeverQualified()
    {
        var both = new Statement("0.4.0.1862.1.6", w =>
        {
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier("0.4.0.1862.1.6.1");
                w.WriteObjectIdentifier("0.4.0.1862.1.6.2");
            }
        });
        var check = Checked([Compliance, Sscd, both]);
        var nature = Nature(check);
        Assert.Equal(NatureLevel.Advanced, nature.Level);
        Assert.Equal(Tr.T("Das Zertifikat nennt keine eindeutige Art (Signatur, Siegel oder Webseite)."), nature.Reasons[0]);
        Assert.Equal(SignatureLevel.Advanced, check.Certificate!.Level);
    }

    /// <summary>A document time stamp of a qualified service the check does not trust (revoked): no „qualified time stamp“ (No. 483).</summary>
    [Fact]
    public void DocumentTimestamp_QualifiedOnlyFromTrustedService()
    {
        var service = new Core.Pdf.Signatures.TrustLists.TrustService("DE", "Muster Trust GmbH", "TSU", Core.Pdf.Signatures.TrustLists.TrustedList.QualifiedTimestamps, [],
            [new Core.Pdf.Signatures.TrustLists.TrustServicePeriod(DateTimeOffset.UtcNow.AddYears(-1), "http://uri.etsi.org/TrstSvc/TrustedList/Svcstatus/granted", true)]);
        var match = new Core.Pdf.Signatures.TrustLists.TrustListMatch(service, service.Periods[0], true);
        var stamp = Checked([]) with { IsDocumentTimestamp = true, TimestampService = match };
        Assert.Equal(NatureLevel.Qualified, Nature(stamp with { IdentityConfirmed = true }).Level);
        Assert.Equal(NatureLevel.Simple, Nature(stamp with { IdentityConfirmed = false }).Level);
    }

    /// <summary>A damaged statement content declares nothing; the identifiers around it still count (counter-check, green before and after).</summary>
    [Fact]
    public void DamagedStatementInfo_DeclaresNothing()
    {
        // QcType whose content is an INTEGER instead of a list of identifiers: no type, the statements after it still read
        var damaged = new Statement("0.4.0.1862.1.6", w => w.WriteEncodedValue([0x30, 0x03, 0x02, 0x01, 0x05]));
        var profile = CertificateProfiles.Statements(Encode([Compliance, damaged, Sscd]), CertificateProfile.None);
        Assert.Null(profile.QcType);
        Assert.True(profile.EuQualified && profile.QcSscd);
        Assert.Null(CertificateProfiles.Statements(Encode([Compliance, Type("1.2.3.4")]), CertificateProfile.None).QcType);
        Assert.False(CertificateProfiles.Statements(new byte[] { 0x30, 0x05, 0x30, 0x03, 0x06 }, CertificateProfile.None).QcCompliance);
    }

    /// <summary>A self-issued certificate stays a simple signature, said why.</summary>
    [Fact]
    public void SelfIssued_StaysSimple()
    {
        using var own = SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"art-{Guid.NewGuid():N}.p12"));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var signed = PdfSigner.Sign(doc, 0, new InkElement
        {
            Rect = new PdfRectangle(50, 100, 250, 180),
            Strokes = [[0, 20, 40, 70, 90, 10]],
            StrokeWidth = 3,
            Digital = new DigitalSigning("Max Mustermann"),
        }, own, DateTimeOffset.Now);
        using var saved = PdfDocument.Open(signed);
        var check = Assert.Single(SignatureVerifier.Verify(saved, null, [], CancellationToken.None));
        Assert.Equal(NatureLevel.Simple, Nature(check).Level);
        Assert.Equal(Tr.T("einfache elektronische Signatur"), SignatureTexts.Level(check));
    }
}
