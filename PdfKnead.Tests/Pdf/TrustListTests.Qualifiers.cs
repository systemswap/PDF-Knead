using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Signatures.TrustLists;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The EU trusted lists for seals and with their qualifiers (docs/ABLAUFPLAN.md, 2.6 step 4 and 2.6c finding 4; docs/FEHLERGEDAECHTNIS.md,
/// No. 484): a service for seals confirms qualified seals; the qualifiers whose criteria select the certificate decide before its own
/// statements; qualified only if granted at issuance and at signing; a stale list is said.
/// </summary>
public sealed partial class TrustListTests
{
    private const string QcCompliance = "0.4.0.1862.1.1", QcSscd = "0.4.0.1862.1.4";

    /// <summary>A certificate from the given authority with these QC statements (and QcType eseal for a seal).</summary>
    private static SigningCertificate IssuedBy(Party ca, bool seal, X509KeyUsageFlags usage, params string[] statements)
    {
        using var key = RSA.Create(2048);
        var subject = seal ? "CN=Muster GmbH, O=Muster GmbH, 2.5.4.97=VATDE-123456789, C=DE" : "CN=Max Mustermann, C=DE";
        var request = new CertificateRequest(new X500DistinguishedName(subject), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            foreach (var id in statements)
                using (writer.PushSequence())
                    writer.WriteObjectIdentifier(id);
            if (seal)
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier("0.4.0.1862.1.6");
                    using (writer.PushSequence()) writer.WriteObjectIdentifier("0.4.0.1862.1.6.2");
                }
        }
        request.CertificateExtensions.Add(new X509Extension("1.3.6.1.5.5.7.1.3", writer.Encode(), false));
        using var issued = request.Create(ca.Certificate, Now.AddDays(-30), Now.AddYears(1), [0x61, 0x62]);
        return new SigningCertificate(issued.CopyWithPrivateKey(key), SigningCertificateSource.File, [X509CertificateLoader.LoadCertificate(ca.Certificate.RawData)]);
    }

    /// <summary>The extension „Qualifications“ with one entry: these qualifiers for certificates whose key usage has nonRepudiation (or other criteria).</summary>
    private static string Qualifications(string criteria, params string[] qualifiers) =>
        "<Extension Critical=\"true\"><Qualifications xmlns=\"http://uri.etsi.org/TrstSvc/SvcInfoExt/eSigDir-1999-93-EC-TrustedList/#\"><QualificationElement><Qualifiers>" +
        string.Concat(qualifiers.Select(q => $"<Qualifier uri=\"http://uri.etsi.org/TrstSvc/TrustedList/SvcInfoExt/{q}\"/>")) +
        $"</Qualifiers>{criteria}</QualificationElement></Qualifications></Extension>";

    private const string NonRepudiation = "<CriteriaList assert=\"atLeastOne\"><KeyUsage><KeyUsageBit name=\"nonRepudiation\">true</KeyUsageBit></KeyUsage></CriteriaList>";

    /// <summary>The German list with one qualified CA service for <paramref name="ca"/>.</summary>
    private static void ListWith(World world, Party ca, string use, string extensions = "", DateTimeOffset? from = null, string nextUpdate = "2027-04-01T00:00:00Z") =>
        world.Files[German] = Sign(List("DE", providers: Provider("Muster Trust GmbH",
            Service(TrustedList.QualifiedCa, ca, "granted", from ?? Now.AddYears(-3), use: use, extensions: extensions)), nextUpdate: nextUpdate),
            world.GermanSupervisor, "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256");

    private static SignatureCheck CheckedWith(World world, SigningCertificate signer)
    {
        var set = world.Download();
        using var saved = PdfDocument.Open(SignWith(signer, DateTimeOffset.Now));
        return Assert.Single(SignatureVerifier.Verify(saved, null, [], CancellationToken.None, () => set));
    }

    private const X509KeyUsageFlags Signing = X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.DigitalSignature;

    [Fact]
    public void QualifiedSeal_SealCaListed_ConfirmedAsQualifiedSeal()
    {
        using var world = new World();
        ListWith(world, world.Ca, "ForeSeals");
        using var signer = IssuedBy(world.Ca, seal: true, Signing, QcCompliance, QcSscd);
        var check = CheckedWith(world, signer);
        Assert.True(check.IdentityConfirmed);
        var nature = SignatureNatures.Of(check)!;
        Assert.Equal(SignatureKind.Seal, nature.Kind);
        Assert.Equal(NatureLevel.Qualified, nature.Level);
        Assert.True(nature.Listed);
        Assert.DoesNotContain(check.Warnings, w => w.Contains("nennt sich qualifiziert"));
    }

    /// <summary>A seal certificate from a service the list marks for signatures only: not qualified (the list filters by kind).</summary>
    [Fact]
    public void SealCertificate_SignatureOnlyService_NotQualified()
    {
        using var world = new World();
        ListWith(world, world.Ca, "ForeSignatures");
        using var signer = IssuedBy(world.Ca, seal: true, Signing, QcCompliance, QcSscd);
        var nature = SignatureNatures.Of(CheckedWith(world, signer))!;
        Assert.Equal(NatureLevel.Advanced, nature.Level);
        Assert.Equal(Tr.T("Die EU-Vertrauensliste führt den Aussteller zur Unterschriftszeit nicht als qualifizierten Anbieter für Siegel."), nature.Reasons[0]);
    }

    /// <summary>The certificate does not declare its device, the list says „with QSCD“ for it: qualified (the list decides).</summary>
    [Fact]
    public void QualifierQcWithQscd_OverridesMissingStatement()
    {
        using var world = new World();
        ListWith(world, world.Ca, "ForeSignatures", Qualifications(NonRepudiation, "QCWithQSCD"));
        using var signer = IssuedBy(world.Ca, seal: false, Signing, QcCompliance);
        var nature = SignatureNatures.Of(CheckedWith(world, signer))!;
        Assert.Equal(NatureLevel.Qualified, nature.Level);
        Assert.Equal(QscdState.Yes, nature.Qscd);
    }

    /// <summary>The list marks the certificate „not qualified“: at most advanced, said why — though it declares itself qualified.</summary>
    [Fact]
    public void QualifierNotQualified_Downgrades()
    {
        using var world = new World();
        ListWith(world, world.Ca, "ForeSignatures", Qualifications(NonRepudiation, "NotQualified"));
        using var signer = IssuedBy(world.Ca, seal: false, Signing, QcCompliance, QcSscd);
        var nature = SignatureNatures.Of(CheckedWith(world, signer))!;
        Assert.Equal(NatureLevel.Advanced, nature.Level);
        Assert.Equal(Tr.T("Die EU-Vertrauensliste führt dieses Zertifikat als nicht qualifiziert."), nature.Reasons[0]);
    }

    /// <summary>„QSCD managed on behalf“: qualified, the key held by the provider (remote signing) — said; never „card“ (decision E).</summary>
    [Fact]
    public void QcqscdManagedOnBehalf_SaysRemote()
    {
        using var world = new World();
        ListWith(world, world.Ca, "ForeSignatures", Qualifications(NonRepudiation, "QCQSCDManagedOnBehalf"));
        using var signer = IssuedBy(world.Ca, seal: false, Signing, QcCompliance);
        var check = CheckedWith(world, signer);
        Assert.Equal(NatureLevel.Qualified, SignatureNatures.Of(check)!.Level);
        Assert.Contains(Tr.T("Der Schlüssel wird laut EU-Vertrauensliste beim Anbieter verwaltet (Fernsignatur)."), SignatureTexts.NatureNotes(check));
    }

    /// <summary>Criteria not understood (other criteria): the qualifier is not applied — the cautious reading (docs/ABLAUFPLAN.md, 2.6).</summary>
    [Fact]
    public void UnknownCriteria_QualifierNotApplied()
    {
        using var world = new World();
        ListWith(world, world.Ca, "ForeSignatures",
            Qualifications("<CriteriaList assert=\"all\"><otherCriteriaList><CertSubjectDNAttribute/></otherCriteriaList></CriteriaList>", "NotQualified"));
        using var signer = IssuedBy(world.Ca, seal: false, Signing, QcCompliance, QcSscd);
        Assert.Equal(NatureLevel.Qualified, SignatureNatures.Of(CheckedWith(world, signer))!.Level);
    }

    /// <summary>A service granted only after the certificate was issued: not qualified (the lists at issuance and at signing, EN 319 102-1).</summary>
    [Fact]
    public void GrantedOnlyAfterIssuance_NotQualified()
    {
        using var world = new World();
        ListWith(world, world.Ca, "ForeSignatures", from: Now.AddDays(-1));
        using var signer = IssuedBy(world.Ca, seal: false, Signing, QcCompliance, QcSscd);
        var nature = SignatureNatures.Of(CheckedWith(world, signer))!;
        Assert.Equal(NatureLevel.Advanced, nature.Level);
        Assert.Equal(Tr.T("Bei der Ausstellung des Zertifikats war der Anbieter laut EU-Vertrauensliste nicht qualifiziert."), nature.Reasons[0]);
    }

    /// <summary>A list past its next update: said in the result.</summary>
    [Fact]
    public void StaleList_ReportedInResult()
    {
        using var world = new World();
        ListWith(world, world.Ca, "ForeSignatures", nextUpdate: Now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ"));
        using var signer = IssuedBy(world.Ca, seal: false, Signing, QcCompliance, QcSscd);
        var check = CheckedWith(world, signer);
        Assert.Contains(check.Warnings, w => w.StartsWith(Tr.T("Die geladene EU-Vertrauensliste ({0}, Nr. {1}) ist veraltet: die nächste Ausgabe war am {2} fällig. Laden Sie sie neu.", "DE", 7, "§").Split('§')[0], StringComparison.Ordinal));
    }
}
