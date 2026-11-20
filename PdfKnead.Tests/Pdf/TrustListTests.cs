using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Signatures.TrustLists;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The EU trusted lists (KONZEPT.md, section 5.17, „EU-Vertrauensliste“) with stand-ins: a list of lists and national lists in the published
/// format, signed with the XML signature code of the .NET libraries (test project only) and checked with the own code. A list signed by
/// someone else, changed afterwards, wrapped or with a DTD is refused; a key change of the Commission is followed through a pivot list; a
/// signature whose issuer is listed as qualified says so.
/// </summary>
public sealed partial class TrustListTests
{
    internal const string Lotl = "https://ec.europa.eu/tools/lotl/eu-lotl.xml", German = "https://tl.example/DE.xml", Austrian = "https://tl.example/AT.xml";

    static TrustListTests() => XmlSignatureMethods.Register();

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    /// <summary>A key with a self-signed certificate (Commission, national supervisor, certification authority, time-stamping unit).</summary>
    internal sealed class Party : IDisposable
    {
        public Party(string name, bool ecdsa = false, bool authority = false)
        {
            Key = ecdsa ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : RSA.Create(2048);
            var request = Key is RSA rsa
                ? new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                : new CertificateRequest($"CN={name}", (ECDsa)Key, HashAlgorithmName.SHA256);
            if (authority)
            {
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            }
            Certificate = request.CreateSelfSigned(Now.AddYears(-5), Now.AddYears(5));
        }

        public AsymmetricAlgorithm Key { get; }

        public X509Certificate2 Certificate { get; }

        public string Base64 => Convert.ToBase64String(Certificate.RawData);

        public void Dispose()
        {
            Certificate.Dispose();
            Key.Dispose();
        }
    }

    private static string Identity(params Party[] parties) =>
        string.Concat(parties.Select(p => $"<ServiceDigitalIdentity><DigitalId><X509Certificate>{p.Base64}</X509Certificate></DigitalId></ServiceDigitalIdentity>"));

    private static string Pointer(string territory, string location, params Party[] signers) =>
        $"<OtherTSLPointer><ServiceDigitalIdentities>{Identity(signers)}</ServiceDigitalIdentities><TSLLocation>{location}</TSLLocation>" +
        $"<AdditionalInformation><OtherInformation><SchemeTerritory>{territory}</SchemeTerritory></OtherInformation>" +
        "<OtherInformation><ns3:MimeType>application/vnd.etsi.tsl+xml</ns3:MimeType></OtherInformation></AdditionalInformation></OtherTSLPointer>";

    private static string List(string territory, string pointers = "", string providers = "", string uris = "", string nextUpdate = "2027-04-01T00:00:00Z") =>
        "<TrustServiceStatusList xmlns=\"http://uri.etsi.org/02231/v2#\" xmlns:ns3=\"http://uri.etsi.org/02231/v2/additionaltypes#\" Id=\"tsl\">" +
        $"<SchemeInformation><TSLVersionIdentifier>6</TSLVersionIdentifier><TSLSequenceNumber>7</TSLSequenceNumber>" +
        (pointers.Length > 0 ? $"<PointersToOtherTSL>{pointers}</PointersToOtherTSL>" : "") +
        $"<SchemeTerritory>{territory}</SchemeTerritory><SchemeInformationURI><URI xml:lang=\"en\">https://ec.europa.eu/tools/lotl/legal.html</URI>{uris}</SchemeInformationURI>" +
        $"<ListIssueDateTime>2026-10-15T00:00:00Z</ListIssueDateTime><NextUpdate><dateTime>{nextUpdate}</dateTime></NextUpdate></SchemeInformation>" +
        (providers.Length > 0 ? $"<TrustServiceProviderList>{providers}</TrustServiceProviderList>" : "") + "</TrustServiceStatusList>";

    private static string Service(string type, Party party, string status, DateTimeOffset from, string? use = "ForeSignatures", string history = "",
        string extensions = "") =>
        $"<TSPService><ServiceInformation><ServiceTypeIdentifier>{type}</ServiceTypeIdentifier><ServiceName><Name xml:lang=\"en\">{party.Certificate.GetNameInfo(X509NameType.SimpleName, false)}</Name></ServiceName>" +
        $"<ServiceDigitalIdentity><DigitalId><X509Certificate>{party.Base64}</X509Certificate></DigitalId></ServiceDigitalIdentity>" +
        $"<ServiceStatus>http://uri.etsi.org/TrstSvc/TrustedList/Svcstatus/{status}</ServiceStatus><StatusStartingTime>{from:yyyy-MM-ddTHH:mm:ssZ}</StatusStartingTime>" +
        (use is null && extensions.Length == 0 ? "" : "<ServiceInformationExtensions>" + (use is null ? "" : $"<Extension Critical=\"true\"><AdditionalServiceInformation><URI xml:lang=\"en\">http://uri.etsi.org/TrstSvc/TrustedList/SvcInfoExt/{use}</URI></AdditionalServiceInformation></Extension>") + extensions + "</ServiceInformationExtensions>") +
        $"</ServiceInformation>{(history.Length > 0 ? $"<ServiceHistory>{history}</ServiceHistory>" : "")}</TSPService>";

    private static string Provider(string name, params string[] services) =>
        $"<TrustServiceProvider><TSPInformation><TSPName><Name xml:lang=\"en\">{name}</Name></TSPName></TSPInformation><TSPServices>{string.Concat(services)}</TSPServices></TrustServiceProvider>";

    /// <summary>Signs a list as the lists are signed: enveloped, Exclusive C14N, the signer's certificate in KeyInfo.</summary>
    private static byte[] Sign(string xml, Party signer, string method = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256", bool whole = true)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);
        var signed = new SignedXml(document) { SigningKey = signer.Key };
        signed.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signed.SignedInfo.SignatureMethod = method;
        var reference = new Reference(whole ? "" : "#tsl") { DigestMethod = "http://www.w3.org/2001/04/xmlenc#sha256" };
        if (whole) reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signed.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(signer.Certificate));
        signed.KeyInfo = keyInfo;
        signed.ComputeSignature();
        document.DocumentElement!.AppendChild(document.ImportNode(signed.GetXml(), true));
        return Encoding.UTF8.GetBytes(document.OuterXml);
    }

    internal sealed class Source(Dictionary<string, byte[]> files) : ITrustListSource
    {
        public List<string> Asked { get; } = [];

        public byte[] Fetch(Uri address, CancellationToken cancel = default)
        {
            Asked.Add(address.ToString());
            return files.TryGetValue(address.ToString(), out var data) ? data : throw new TrustListException("nicht erreichbar");
        }
    }

    /// <summary>The Commission, two supervisors, a qualified CA and a qualified time-stamping unit, with their lists.</summary>
    internal sealed class World : IDisposable
    {
        public Party Commission { get; } = new("Muster-Kommission");
        public Party GermanSupervisor { get; } = new("Muster-Aufsicht DE", ecdsa: true);
        public Party AustrianSupervisor { get; } = new("Muster-Aufsicht AT");
        public Party Ca { get; } = new("Muster Trust CA", authority: true);
        public Party Tsu { get; } = new("Muster Trust Zeitstempel");

        public Dictionary<string, byte[]> Files { get; } = [];

        public World()
        {
            Files[Lotl] = Sign(List("EU", Pointer("EU", Lotl, Commission) + Pointer("DE", German, GermanSupervisor) + Pointer("AT", Austrian, AustrianSupervisor)), Commission);
            Files[German] = Sign(List("DE", providers: Provider("Muster Trust GmbH",
                Service(TrustedList.QualifiedCa, Ca, "granted", Now.AddYears(-3)),
                Service(TrustedList.QualifiedTimestamps, Tsu, "granted", Now.AddYears(-3), use: null))), GermanSupervisor,
                "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256");
            Files[Austrian] = Sign(List("AT", providers: Provider("Muster AT", Service(TrustedList.QualifiedCa, Ca, "withdrawn", Now.AddYears(-1)))), AustrianSupervisor,
                "http://www.w3.org/2007/05/xmldsig-more#sha256-rsa-MGF1");
        }

        public IReadOnlyList<X509Certificate2> Anchors => [Commission.Certificate];

        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"eu-liste-{Guid.NewGuid():N}");

        public TrustListSet Download(Source? source = null) =>
            TrustListStore.Download(source ?? new Source(Files), Folder, anchors: Anchors);

        public void Dispose()
        {
            foreach (var party in new[] { Commission, GermanSupervisor, AustrianSupervisor, Ca, Tsu }) party.Dispose();
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }

        /// <summary>A signer certificate issued by the listed CA, declaring itself qualified (QcCompliance, QcSSCD).</summary>
        public SigningCertificate Signer()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.DigitalSignature, true));
            var statements = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
            using (statements.PushSequence())
            {
                using (statements.PushSequence()) statements.WriteObjectIdentifier("0.4.0.1862.1.1");
                using (statements.PushSequence()) statements.WriteObjectIdentifier("0.4.0.1862.1.4");
            }
            request.CertificateExtensions.Add(new X509Extension("1.3.6.1.5.5.7.1.3", statements.Encode(), false));
            using var issued = request.Create(Ca.Certificate, Now.AddDays(-30), Now.AddYears(2), [0x42, 0x17]);
            return new SigningCertificate(issued.CopyWithPrivateKey(key), SigningCertificateSource.File, [X509CertificateLoader.LoadCertificate(Ca.Certificate.RawData)]);
        }
    }

    [Fact]
    public void Download_Checked_Kept_LoadedAgain_IssuerFound()
    {
        using var world = new World();
        var set = world.Download();
        Assert.Equal(["AT", "DE"], set.Lists.Select(l => l.Territory).Order());
        Assert.Empty(set.Failures);
        Assert.True(File.Exists(Path.Combine(world.Folder, "eu-lotl.xml")));
        Assert.True(File.Exists(Path.Combine(world.Folder, "DE.xml")));

        var again = TrustListStore.Load(world.Folder, world.Anchors);
        using var signer = world.Signer();
        var match = again.Find(signer.Certificate, [], TrustedList.QualifiedCa, Now);
        Assert.NotNull(match);
        Assert.True(match.Qualified);
        Assert.Equal("Muster Trust GmbH", match.Service.Provider);
        Assert.Equal("DE", match.Service.Territory);
        // the time-stamping unit is listed itself
        var unit = again.Find(world.Tsu.Certificate, [], TrustedList.QualifiedTimestamps, Now);
        Assert.True(unit?.Qualified);
        // before the service was listed: not qualified
        Assert.Null(again.Find(signer.Certificate, [], TrustedList.QualifiedCa, Now.AddYears(-4))?.Period);
    }

    [Fact]
    public void ChangedNationalList_Reported_OthersKept()
    {
        using var world = new World();
        var text = Encoding.UTF8.GetString(world.Files[German]).Replace("Muster Trust GmbH", "Fremde Trust GmbH");
        world.Files[German] = Encoding.UTF8.GetBytes(text);
        var set = world.Download();
        Assert.Equal(["AT"], set.Lists.Select(l => l.Territory));
        Assert.Contains("verändert", set.Failures["DE"]);
        Assert.False(File.Exists(Path.Combine(world.Folder, "DE.xml")));

        // a kept file changed on disk afterwards is not used either
        world.Files[German] = Sign(List("DE", providers: Provider("Muster Trust GmbH", Service(TrustedList.QualifiedCa, world.Ca, "granted", Now.AddYears(-3)))),
            world.GermanSupervisor, "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256");
        world.Download();
        var kept = Path.Combine(world.Folder, "DE.xml");
        File.WriteAllText(kept, File.ReadAllText(kept).Replace("granted", "grantex"));
        Assert.Contains("DE", TrustListStore.Load(world.Folder, world.Anchors).Failures.Keys);
    }

    [Fact]
    public void NationalList_SignedBySomeoneElse_Refused()
    {
        using var world = new World();
        using var stranger = new Party("Fremder");
        world.Files[German] = Sign(List("DE", providers: Provider("Muster Trust GmbH", Service(TrustedList.QualifiedCa, world.Ca, "granted", Now.AddYears(-3)))), stranger);
        var set = world.Download();
        Assert.Contains("berechtigten", set.Failures["DE"]);
    }

    [Fact]
    public void ListOfLists_WrongKey_Refused_KeptListsStay()
    {
        using var world = new World();
        world.Download();
        using var stranger = new Party("Fremder");
        var good = world.Files[Lotl];
        world.Files[Lotl] = Sign(List("EU", Pointer("DE", German, world.GermanSupervisor)), stranger);
        Assert.Throws<TrustListException>(() => world.Download());
        // what was kept stays usable
        Assert.Equal(2, TrustListStore.Load(world.Folder, world.Anchors).Lists.Count);
        Assert.Equal(good, File.ReadAllBytes(Path.Combine(world.Folder, "eu-lotl.xml")));
    }

    [Fact]
    public void KeyChange_FollowedThroughPivot_OnlySignedByTheKeyBefore()
    {
        using var world = new World();
        using var next = new Party("Muster-Kommission neu");
        using var stranger = new Party("Fremder");
        const string pivot = "https://ec.europa.eu/tools/lotl/eu-lotl-pivot-12.xml", fake = "https://ec.europa.eu/tools/lotl/eu-lotl-pivot-13.xml";
        // the pivot, signed with the old key, names the new one; a later „pivot“ by a stranger must not hand on anything
        world.Files[pivot] = Sign(List("EU", Pointer("EU", Lotl, next)), world.Commission);
        world.Files[fake] = Sign(List("EU", Pointer("EU", Lotl, stranger)), stranger);
        world.Files[Lotl] = Sign(List("EU", Pointer("EU", Lotl, next) + Pointer("DE", German, world.GermanSupervisor),
            uris: $"<URI xml:lang=\"en\">{fake}</URI><URI xml:lang=\"en\">{pivot}</URI><URI xml:lang=\"en\">https://anderswo.example/tools/lotl/eu-lotl-pivot-1.xml</URI>"), next);
        var source = new Source(world.Files);
        var set = world.Download(source);
        Assert.Equal(["DE"], set.Lists.Select(l => l.Territory));
        Assert.DoesNotContain(source.Asked, a => a.Contains("anderswo"));   // pivots only from the Commission's site
        Assert.True(File.Exists(Path.Combine(world.Folder, "eu-lotl-pivot-12.xml")));
        Assert.Single(TrustListStore.Load(world.Folder, world.Anchors).Lists);

        // signed with the new key but without a pivot from the old one: refused
        world.Files.Remove(pivot);
        Assert.Throws<TrustListException>(() => world.Download());
    }

    [Fact]
    public void Wrapping_PartialReference_Dtd_Refused()
    {
        using var world = new World();
        var supervisor = world.AustrianSupervisor;
        // a signature over part of the list only (no reference to the whole document): refused
        var partial = XmlSignature.Load(Sign(List("AT"), supervisor, "http://www.w3.org/2007/05/xmldsig-more#sha256-rsa-MGF1", whole: false));
        Assert.Throws<TrustListException>(() => XmlSignature.Verify(partial, [supervisor.Certificate]));
        // a second element with the signed Id (a wrapped copy): refused
        var signedOnce = Encoding.UTF8.GetString(Sign(List("AT"), supervisor, "http://www.w3.org/2007/05/xmldsig-more#sha256-rsa-MGF1", whole: false));
        var twice = XmlSignature.Load(Encoding.UTF8.GetBytes(signedOnce.Replace("</TrustServiceStatusList>", "<Kopie Id=\"tsl\" xmlns=\"\"/></TrustServiceStatusList>")));
        Assert.Contains("doppelt", Assert.Throws<TrustListException>(() => XmlSignature.Verify(twice, [supervisor.Certificate])).Message);
        // an element slipped into the signed list afterwards: the digest no longer fits
        var slipped = Encoding.UTF8.GetString(world.Files[German]).Replace("<TrustServiceProviderList>", "<TrustServiceProviderList><Kopie/>");
        Assert.Throws<TrustListException>(() => XmlSignature.Verify(XmlSignature.Load(Encoding.UTF8.GetBytes(slipped)), [world.GermanSupervisor.Certificate]));
        // DTDs (entity expansion, external files) are refused when reading
        Assert.Throws<TrustListException>(() => XmlSignature.Load(Encoding.UTF8.GetBytes("<!DOCTYPE a [<!ENTITY x \"y\">]><a>&x;</a>")));
        // nesting beyond the limit: refused before a tree is built — 100 000 levels would overflow the stack later (No. 869); a shallow one reads
        static byte[] Nested(int depth) => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth)));
        Assert.Contains("64", Assert.Throws<TrustListException>(() => XmlSignature.Load(Nested(100_000))).Message);
        Assert.NotNull(XmlSignature.Load(Nested(60)));
    }

    [Fact]
    public void Status_Withdrawn_SealsOnly_History()
    {
        using var world = new World();
        using var sealCa = new Party("Muster Siegel CA", authority: true);
        using var oldCa = new Party("Muster Alt CA", authority: true);
        var history = $"<ServiceHistoryInstance><ServiceTypeIdentifier>{TrustedList.QualifiedCa}</ServiceTypeIdentifier><ServiceName><Name xml:lang=\"en\">alt</Name></ServiceName>" +
                      $"<ServiceDigitalIdentity><DigitalId><X509Certificate>{oldCa.Base64}</X509Certificate></DigitalId></ServiceDigitalIdentity>" +
                      $"<ServiceStatus>http://uri.etsi.org/TrstSvc/TrustedList/Svcstatus/granted</ServiceStatus><StatusStartingTime>{Now.AddYears(-4):yyyy-MM-ddTHH:mm:ssZ}</StatusStartingTime></ServiceHistoryInstance>";
        world.Files[German] = Sign(List("DE", providers: Provider("Muster Trust GmbH",
            Service(TrustedList.QualifiedCa, sealCa, "granted", Now.AddYears(-3), use: "ForeSeals"),
            Service(TrustedList.QualifiedCa, oldCa, "withdrawn", Now.AddYears(-1), history: history))), world.GermanSupervisor,
            "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256");
        var set = world.Download();
        Certificate(sealCa, out var forSeal);
        Assert.False(set.Find(forSeal, [], TrustedList.QualifiedCa, Now)!.Qualified);   // seals only
        Certificate(oldCa, out var old);
        Assert.False(set.Find(old, [], TrustedList.QualifiedCa, Now)!.Qualified);     // withdrawn now
        Assert.True(set.Find(old, [], TrustedList.QualifiedCa, Now.AddYears(-2))!.Qualified);   // granted then
        forSeal.Dispose();
        old.Dispose();

        static void Certificate(Party ca, out X509Certificate2 issued)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            issued = request.Create(ca.Certificate, Now.AddYears(-3), Now.AddYears(2), [0x11]);
        }
    }

    [Fact]
    public void Verifier_ShowsListedIssuer_WarnsWhenNotListed()
    {
        using var world = new World();
        var set = world.Download();
        using var signer = world.Signer();
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var signed = PdfSigner.Sign(doc, 0, new InkElement
        {
            Rect = new PdfRectangle(50, 100, 250, 180),
            Strokes = [[0, 20, 40, 70, 90, 10]],
            StrokeWidth = 3,
            Digital = new DigitalSigning("Max Mustermann"),
        }, signer, DateTimeOffset.Now);
        using var saved = PdfDocument.Open(signed);
        var check = Assert.Single(SignatureVerifier.Verify(saved, null, [], CancellationToken.None, () => set));
        Assert.True(check.TrustListsLoaded);
        Assert.True(check.Issuer?.Qualified);
        Assert.Contains("Muster Trust GmbH", SignatureTexts.TrustList(check));
        Assert.DoesNotContain(check.Warnings, w => w.Contains("Vertrauensliste"));

        // the lists without that CA: the certificate calls itself qualified, the list does not confirm it
        world.Files[German] = Sign(List("DE", providers: Provider("Muster Trust GmbH", Service(TrustedList.QualifiedTimestamps, world.Tsu, "granted", Now.AddYears(-3), use: null))),
            world.GermanSupervisor, "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256");
        var without = world.Download();
        var unlisted = Assert.Single(SignatureVerifier.Verify(saved, null, [], CancellationToken.None, () => without));
        Assert.False(unlisted.Issuer?.Qualified);   // only the Austrian entry, withdrawn
        Assert.Contains("nicht als qualifiziert", SignatureTexts.TrustList(unlisted));
        Assert.Contains(unlisted.Warnings, w => w.Contains("Vertrauensliste"));
        // not loaded: nothing said
        Assert.Null(SignatureTexts.TrustList(Assert.Single(SignatureVerifier.Verify(saved, null, [], CancellationToken.None))));
    }

    /// <summary>
    /// An expired certificate with a time stamp from its validity: the stamp's time counts only if the service is trusted — here as a
    /// qualified service of the EU list; a service nobody knows confirms nothing (Fehlergedächtnis 326).
    /// </summary>
    [Fact]
    public void ExpiredCertificate_StampCountsOnlyFromTrustedService()
    {
        using var world = new World();
        using var authority = new SignatureTimestampTests.TestAuthority();
        var stampTime = SignatureTimestampTests.StampTime;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.DigitalSignature, true));
        using var issued = request.Create(world.Ca.Certificate, stampTime.AddYears(-1), stampTime.AddHours(1), [0x33]);
        using var signer = new SigningCertificate(issued.CopyWithPrivateKey(key), SigningCertificateSource.File, [X509CertificateLoader.LoadCertificate(world.Ca.Certificate.RawData)]);
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var signed = PdfSigner.SignUnchecked(doc, 0, new InkElement
        {
            Rect = new PdfRectangle(50, 100, 250, 180),
            Strokes = [[0, 20, 40, 70, 90, 10]],
            StrokeWidth = 3,
            Digital = new DigitalSigning("Max Mustermann"),
        }, signer, stampTime, authority);
        using var saved = PdfDocument.Open(signed);

        var unknown = Assert.Single(SignatureVerifier.Verify(saved, null, [], CancellationToken.None));
        Assert.Contains(Tr.T("Das Zertifikat ist abgelaufen; der Zeitstempel stammt von einem Dienst, dem dieser Rechner nicht vertraut, und bestätigt deshalb nichts."), unknown.Warnings);

        world.Files[German] = Sign(List("DE", providers: Provider("Muster Trust GmbH", Service(TrustedList.QualifiedCa, world.Ca, "granted", Now.AddYears(-3)),
            $"<TSPService><ServiceInformation><ServiceTypeIdentifier>{TrustedList.QualifiedTimestamps}</ServiceTypeIdentifier><ServiceName><Name xml:lang=\"en\">TSU</Name></ServiceName>" +
            $"<ServiceDigitalIdentity><DigitalId><X509Certificate>{Convert.ToBase64String(authority.Certificate.RawData)}</X509Certificate></DigitalId></ServiceDigitalIdentity>" +
            $"<ServiceStatus>http://uri.etsi.org/TrstSvc/TrustedList/Svcstatus/granted</ServiceStatus><StatusStartingTime>{stampTime.AddYears(-1):yyyy-MM-ddTHH:mm:ssZ}</StatusStartingTime>" +
            "</ServiceInformation></TSPService>")), world.GermanSupervisor, "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256");
        var set = world.Download();
        var listed = Assert.Single(SignatureVerifier.Verify(saved, null, [], CancellationToken.None, () => set));
        Assert.True(listed.TimestampService?.Qualified);
        Assert.DoesNotContain(listed.Warnings, w => w.Contains(Tr.T("abgelaufen")) || w.Contains(Tr.T("Den Zeitstempeldienst kennt")));
    }

    /// <summary>
    /// A trusted time stamp from before the certificate began (EN 319 102-1, Tab. 6: NOT_YET_VALID): proven signed before the certificate was
    /// valid — invalid. The same file with a service nobody trusts proves nothing of the kind; a stated time after the stamp is mentioned.
    /// </summary>
    [Fact]
    public void StampBeforeCertificateStart_Invalid()
    {
        using var world = new World();
        using var authority = new SignatureTimestampTests.TestAuthority();
        var stampTime = SignatureTimestampTests.StampTime;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.DigitalSignature, true));
        using var issued = request.Create(world.Ca.Certificate, stampTime.AddDays(1), stampTime.AddYears(1), [0x35]);
        using var signer = new SigningCertificate(issued.CopyWithPrivateKey(key), SigningCertificateSource.File, [X509CertificateLoader.LoadCertificate(world.Ca.Certificate.RawData)]);
        using var saved = PdfDocument.Open(SignWith(signer, stampTime.AddDays(2), authority));
        var unknown = Assert.Single(SignatureVerifier.Verify(saved, null, [X509CertificateLoader.LoadCertificate(world.Ca.Certificate.RawData)], CancellationToken.None));
        Assert.NotEqual(SignatureOutcome.Invalid, unknown.Outcome);
        Assert.Contains(Tr.T("Die angegebene Unterschriftszeit liegt nach der Zeit des Zeitstempels."), unknown.Warnings);
        world.Files[German] = Sign(List("DE", providers: Provider("Muster Trust GmbH",
            $"<TSPService><ServiceInformation><ServiceTypeIdentifier>{TrustedList.QualifiedTimestamps}</ServiceTypeIdentifier><ServiceName><Name xml:lang=\"en\">TSU</Name></ServiceName>" +
            $"<ServiceDigitalIdentity><DigitalId><X509Certificate>{Convert.ToBase64String(authority.Certificate.RawData)}</X509Certificate></DigitalId></ServiceDigitalIdentity>" +
            $"<ServiceStatus>http://uri.etsi.org/TrstSvc/TrustedList/Svcstatus/granted</ServiceStatus><StatusStartingTime>{stampTime.AddYears(-1):yyyy-MM-ddTHH:mm:ssZ}</StatusStartingTime>" +
            "</ServiceInformation></TSPService>")), world.GermanSupervisor, "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256");
        var set = world.Download();
        var check = Assert.Single(SignatureVerifier.Verify(saved, null, [X509CertificateLoader.LoadCertificate(world.Ca.Certificate.RawData)], CancellationToken.None, () => set));
        Assert.Equal(SignatureOutcome.Invalid, check.Outcome);
        Assert.Equal(Tr.T("Ein vertrauenswürdiger Zeitstempel belegt die Signatur vor Beginn der Gültigkeit des Zertifikats."), check.Problem);
        Assert.Equal("TOTAL-FAILED/NOT_YET_VALID", SignatureTexts.Indication(check).ToString());
    }

    // files as another program writes them: also with a certificate PDF Knead itself would not sign with (expired, not yet valid; No. 526)
    private static byte[] SignWith(SigningCertificate signer, DateTimeOffset when, ITimestampAuthority? stamps = null)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        return PdfSigner.SignUnchecked(doc, 0, new InkElement
        {
            Rect = new PdfRectangle(50, 100, 250, 180),
            Strokes = [[0, 20, 40, 70, 90, 10]],
            StrokeWidth = 3,
            Digital = new DigitalSigning("Max Mustermann"),
        }, signer, when, stamps);
    }

    /// <summary>
    /// A certificate that only names a listed authority as its issuer (signed with another key) and brings that authority's certificate along:
    /// the chain built leniently links them by name, but the authority did not issue it — not „qualified provider“, no identity
    /// (docs/FEHLERGEDAECHTNIS.md, No. 335).
    /// </summary>
    [Fact]
    public void ForgedIssuerName_NotListed_NoIdentity()
    {
        using var world = new World();
        var set = world.Download();
        using var key = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.DigitalSignature, true));
        using var forged = request.Create(world.Ca.Certificate.SubjectName, X509SignatureGenerator.CreateForRSA(other, RSASignaturePadding.Pkcs1),
            Now.AddDays(-1), Now.AddYears(1), [0x66]);
        using var signer = new SigningCertificate(forged.CopyWithPrivateKey(key), SigningCertificateSource.File, [X509CertificateLoader.LoadCertificate(world.Ca.Certificate.RawData)]);
        using var saved = PdfDocument.Open(SignWith(signer, DateTimeOffset.Now));
        var check = Assert.Single(SignatureVerifier.Verify(saved, null, [X509CertificateLoader.LoadCertificate(world.Ca.Certificate.RawData)], CancellationToken.None, () => set));
        Assert.NotEqual(true, check.Issuer?.Qualified);
        Assert.False(check.IdentityConfirmed);
    }

    /// <summary>
    /// The EU trusted lists as trust anchor (ETSI EN 319 102-1, 5.2.4; eIDAS Art. 22): a certificate of a qualified authority listed as granted
    /// at signing time confirms the identity without the authority's root among the system's — as long as the chain verifies.
    /// </summary>
    [Fact]
    public void ListedQualifiedAuthority_ConfirmsIdentity()
    {
        using var world = new World();
        var set = world.Download();
        using var signer = world.Signer();
        using var saved = PdfDocument.Open(SignWith(signer, DateTimeOffset.Now));
        Assert.False(Assert.Single(SignatureVerifier.Verify(saved, null, [], CancellationToken.None)).IdentityConfirmed);   // lists not loaded
        var check = Assert.Single(SignatureVerifier.Verify(saved, null, [], CancellationToken.None, () => set));
        Assert.True(check.Issuer?.Qualified);
        Assert.True(check.IdentityConfirmed);
        Assert.Equal(SignatureOutcome.Unconfirmed, check.Outcome);   // no validation data in the file (2.5)
        Assert.Equal(Tr.T("qualifizierte elektronische Signatur (Anbieter laut EU-Vertrauensliste qualifiziert)"), SignatureTexts.Level(check));
    }

    /// <summary>
    /// An expired certificate with a time stamp of a trusted (here: qualified) service from its validity: the chain is checked at the time the
    /// service confirms, so the identity stays confirmed after expiry — the point of a time stamp (PAdES B-T, ETSI EN 319 102-1, 5.5).
    /// </summary>
    [Fact]
    public void ExpiredCertificate_TrustedStamp_IdentityAtStampTime()
    {
        using var world = new World();
        using var authority = new SignatureTimestampTests.TestAuthority();
        var stampTime = SignatureTimestampTests.StampTime;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.DigitalSignature, true));
        using var issued = request.Create(world.Ca.Certificate, stampTime.AddYears(-1), stampTime.AddHours(1), [0x34]);
        using var signer = new SigningCertificate(issued.CopyWithPrivateKey(key), SigningCertificateSource.File, [X509CertificateLoader.LoadCertificate(world.Ca.Certificate.RawData)]);
        using var saved = PdfDocument.Open(SignWith(signer, stampTime, authority));
        world.Files[German] = Sign(List("DE", providers: Provider("Muster Trust GmbH",
            $"<TSPService><ServiceInformation><ServiceTypeIdentifier>{TrustedList.QualifiedTimestamps}</ServiceTypeIdentifier><ServiceName><Name xml:lang=\"en\">TSU</Name></ServiceName>" +
            $"<ServiceDigitalIdentity><DigitalId><X509Certificate>{Convert.ToBase64String(authority.Certificate.RawData)}</X509Certificate></DigitalId></ServiceDigitalIdentity>" +
            $"<ServiceStatus>http://uri.etsi.org/TrstSvc/TrustedList/Svcstatus/granted</ServiceStatus><StatusStartingTime>{stampTime.AddYears(-1):yyyy-MM-ddTHH:mm:ssZ}</StatusStartingTime>" +
            "</ServiceInformation></TSPService>")), world.GermanSupervisor, "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256");
        var set = world.Download();
        // the authority's root trusted by the computer (not via the list): the chain counts at the time of the stamp
        var check = Assert.Single(SignatureVerifier.Verify(saved, null, [X509CertificateLoader.LoadCertificate(world.Ca.Certificate.RawData)], CancellationToken.None, () => set));
        Assert.True(check.TimestampService?.Qualified);
        Assert.True(check.IdentityConfirmed);
        // without a trusted stamp the expired certificate confirms nothing
        var unstamped = Assert.Single(SignatureVerifier.Verify(saved, null, [X509CertificateLoader.LoadCertificate(world.Ca.Certificate.RawData)], CancellationToken.None));
        Assert.False(unstamped.IdentityConfirmed);
        // the chain held while the certificate was valid, nothing proves signing then (EN 319 102-1, docs/ABLAUFPLAN.md, 2.5, step 11)
        Assert.Equal("INDETERMINATE/OUT_OF_BOUNDS_NO_POE", SignatureTexts.Indication(unstamped).ToString());
    }

    [Fact]
    public void OverHttp_GetOnly_ErrorsAreTrustListExceptions()
    {
        var handler = new SignatureTimestampTests.Answer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x3C, 0x61, 0x2F, 0x3E]) });
        Assert.Equal([0x3C, 0x61, 0x2F, 0x3E], new HttpTrustListSource(handler).Fetch(new Uri(German)));
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Seen).Method);
        // within the same domain to https: followed (www. to the bare domain)
        var moved = new SignatureTimestampTests.Answer(r => r.RequestUri!.Host == "www.tl.example"
            ? new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://tl.example/DE.xml") } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x3C, 0x61, 0x2F, 0x3E]) });
        Assert.Equal(4, new HttpTrustListSource(moved).Fetch(new Uri("http://www.tl.example/DE.xml")).Length);
        Assert.Equal(["www.tl.example", "tl.example"], moved.Seen.Select(r => r.RequestUri!.Host));
        foreach (var answer in new Func<HttpRequestMessage, HttpResponseMessage>[]
                 {
                     _ => throw new HttpRequestException("keine Verbindung"),
                     _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                     _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://anderswo.example/") } },
                     _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://tl.example/DE.xml") } },   // not https
                     _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://tl.example/DE.xml") } },   // endless
                 })
            Assert.Throws<TrustListException>(() => new HttpTrustListSource(new SignatureTimestampTests.Answer(answer)).Fetch(new Uri(German)));
    }
}
