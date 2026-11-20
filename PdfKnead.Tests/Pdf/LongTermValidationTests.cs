using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Long-term validation (PAdES B-LT, KONZEPT.md, section 5.17, „Langzeitprüfung“): after signing, the certificates and their revocation status
/// go into the file's /DSS; the status comes from the OCSP responder, otherwise from the CRL, and is checked — signed by the authority, for
/// exactly this certificate. A revoked certificate signs nothing; what cannot be obtained is reported, the file is saved all the same. The
/// certification authority and its services are stand-ins answering like real ones (RFC 6960, RFC 5280).
/// </summary>
public sealed class LongTermValidationTests
{
    private static readonly Uri OcspAddress = new("http://ocsp.muster-ca.example/"), CrlAddress = new("http://crl.muster-ca.example/ca.crl"),
        IssuerAddress = new("http://muster-ca.example/ca.cer");

    /// <summary>A certification authority in the test: root, a signer certificate naming its OCSP responder and CRL, and both services.</summary>
    internal sealed class TestCa : IRevocationSource, IDisposable
    {
        private readonly RSA _rootKey = RSA.Create(2048);
        private readonly RSA _otherKey = RSA.Create(2048);
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        public TestCa()
        {
            var request = new CertificateRequest("CN=Muster-Zertifizierungsstelle", _rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            Root = request.CreateSelfSigned(_now.AddYears(-2), _now.AddYears(5));

            using var key = RSA.Create(2048);
            var signer = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            signer.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
            signer.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension([OcspAddress.ToString()], [IssuerAddress.ToString()]));
            signer.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([CrlAddress.ToString()]));
            using var issued = signer.Create(Root, _now.AddDays(-30), _now.AddYears(2), [0x01, 0x23, 0x45, 0x67]);
            Signer = new SigningCertificate(issued.CopyWithPrivateKey(key), SigningCertificateSource.File, [X509CertificateLoader.LoadCertificate(Root.RawData)]);
        }

        public X509Certificate2 Root { get; }

        public SigningCertificate Signer { get; }

        /// <summary>The responder's answer: good, revoked, none (null), signed with a foreign key, for another certificate, or by a delegated responder.</summary>
        public string OcspMode { get; set; } = "gut";

        /// <summary>The CRL: empty, listing the signer, none (null).</summary>
        public string CrlMode { get; set; } = "leer";

        public int OcspRequests { get; private set; }

        public int CrlRequests { get; private set; }

        public byte[]? Ocsp(Uri responder, byte[] request, CancellationToken cancel = default)
        {
            Assert.Equal(OcspAddress, responder);
            OcspRequests++;
            if (OcspMode == "keine") return null;
            // OCSPRequest → tbsRequest → requestList → Request → reqCert (the CertID is echoed)
            var certId = new AsnReader(request, AsnEncodingRules.DER).ReadSequence().ReadSequence().ReadSequence().ReadSequence().ReadEncodedValue().ToArray();
            if (OcspMode == "anderes")
            {
                var other = new AsnReader(certId, AsnEncodingRules.DER).ReadSequence();
                var writer = new AsnWriter(AsnEncodingRules.DER);
                using (writer.PushSequence())
                {
                    writer.WriteEncodedValue(other.ReadEncodedValue().Span);
                    writer.WriteOctetString(other.ReadOctetString());
                    writer.WriteOctetString(other.ReadOctetString());
                    writer.WriteInteger(99);
                }
                certId = writer.Encode();
            }
            return OcspResponse(certId, OcspMode == "gesperrt");
        }

        private byte[] OcspResponse(byte[] certId, bool revoked)
        {
            using var delegatedKey = RSA.Create(2048);
            X509Certificate2? delegated = null;
            if (OcspMode == "beauftragt")
            {
                var request = new CertificateRequest("CN=Muster-OCSP", delegatedKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.9")], true));
                delegated = request.Create(Root, _now.AddDays(-1), _now.AddDays(30), [0x09]);
            }
            var data = new AsnWriter(AsnEncodingRules.DER);
            using (data.PushSequence())
            {
                using (data.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2, true))) data.WriteOctetString(SHA1.HashData(Root.PublicKey.EncodedKeyValue.RawData));
                data.WriteGeneralizedTime(_now, omitFractionalSeconds: true);
                // „nach-ablauf“, „nach-ablauf-archiv“: given after the signer's certificate expired (the second with archiveCutoff)
                var afterExpiry = OcspMode.StartsWith("nach-ablauf", StringComparison.Ordinal);
                var expiry = new DateTimeOffset(Signer.Certificate.NotAfter);
                using (data.PushSequence())
                using (data.PushSequence())
                {
                    data.WriteEncodedValue(certId);
                    if (revoked)
                        using (data.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true))) data.WriteGeneralizedTime(_now.AddDays(-2), omitFractionalSeconds: true);
                    else data.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 0));
                    // „ohne-ende-jetzt“: given now, after the signing; the others an hour before
                    data.WriteGeneralizedTime(afterExpiry ? expiry.AddDays(1) : OcspMode == "ohne-ende-jetzt" ? DateTimeOffset.UtcNow : _now.AddHours(-1),
                        omitFractionalSeconds: true);
                    // „ohne-ende“, „ohne-ende-jetzt“: no nextUpdate (fresh only if given after the signing, EN 319 102-1, 5.2.5)
                    if (OcspMode is not ("ohne-ende" or "ohne-ende-jetzt"))
                        using (data.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                            data.WriteGeneralizedTime(afterExpiry ? expiry.AddDays(8) : _now.AddDays(7), omitFractionalSeconds: true);
                }
                if (OcspMode == "nach-ablauf-archiv")
                    using (data.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true)))
                    using (data.PushSequence())
                    using (data.PushSequence())
                    {
                        data.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1.6");   // archiveCutoff: kept since before the signer's certificate began
                        var cutoff = new AsnWriter(AsnEncodingRules.DER);
                        cutoff.WriteGeneralizedTime(new DateTimeOffset(Signer.Certificate.NotBefore), omitFractionalSeconds: true);
                        data.WriteOctetString(cutoff.Encode());
                    }
            }
            var tbs = data.Encode();
            var key = OcspMode == "fremd" ? _otherKey : delegated is not null ? delegatedKey : _rootKey;
            var basic = new AsnWriter(AsnEncodingRules.DER);
            using (basic.PushSequence())
            {
                basic.WriteEncodedValue(tbs);
                using (basic.PushSequence())
                {
                    basic.WriteObjectIdentifier("1.2.840.113549.1.1.11");
                    basic.WriteNull();
                }
                basic.WriteBitString(key.SignData(tbs, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                if (delegated is not null)
                    using (basic.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                    using (basic.PushSequence())
                        basic.WriteEncodedValue(delegated.RawData);
            }
            delegated?.Dispose();
            var outer = new AsnWriter(AsnEncodingRules.DER);
            using (outer.PushSequence())
            {
                outer.WriteEnumeratedValue(OcspStatus.Successful);
                using (outer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                using (outer.PushSequence())
                {
                    outer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1.1");
                    outer.WriteOctetString(basic.Encode());
                }
            }
            return outer.Encode();
        }

        public byte[]? Crl(Uri address, CancellationToken cancel = default)
        {
            Assert.Equal(CrlAddress, address);
            CrlRequests++;
            if (CrlMode == "keine") return null;
            var builder = new CertificateRevocationListBuilder();
            if (CrlMode == "gesperrt") builder.AddEntry(Signer.Certificate.SerialNumberBytes.Span, _now.AddDays(-2));
            builder.AddEntry([0x55], _now.AddDays(-10));
            return builder.Build(Root, 1, _now.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, _now.AddHours(-1));
        }

        /// <summary>What the caIssuers address answers: the root (DER), in a PKCS#7 bundle, a certificate with the right name but a foreign key, nothing.</summary>
        public string IssuerMode { get; set; } = "der";

        public int IssuerRequests { get; private set; }

        public byte[]? Issuer(Uri address, CancellationToken cancel = default)
        {
            Assert.Equal(IssuerAddress, address);
            IssuerRequests++;
            switch (IssuerMode)
            {
                case "der":
                    return Root.RawData;
                case "p7c":
                    return new X509Certificate2Collection(X509CertificateLoader.LoadCertificate(Root.RawData)).Export(X509ContentType.Pkcs7);
                case "fremd":
                    var request = new CertificateRequest("CN=Muster-Zertifizierungsstelle", _otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                    using (var forged = request.CreateSelfSigned(_now.AddYears(-1), _now.AddYears(1))) return forged.RawData;
                default:
                    return null;
            }
        }

        /// <summary>The signer certificate with its key and the root as a PKCS#12 file.</summary>
        public byte[] Pkcs12(string password) =>
            new X509Certificate2Collection { Signer.Certificate, X509CertificateLoader.LoadCertificate(Root.RawData) }.Export(X509ContentType.Pkcs12, password)!;

        public void Dispose()
        {
            Signer.Dispose();
            Root.Dispose();
            _rootKey.Dispose();
            _otherKey.Dispose();
        }
    }

    private enum OcspStatus { Successful = 0 }

    private static InkElement Signature(double x = 50) => new()
    {
        Rect = new PdfRectangle(x, 100, x + 200, 180),
        Strokes = [[0, 20, 40, 70, 90, 10, 140, 60, 190, 30]],
        StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann · 31.10.2026 12:00"),
    };

    private static string TempPath() => Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdf-knead-tests")).FullName, $"langzeit-{Guid.NewGuid():N}.pdf");

    /// <summary>Signs a document with long-term validation; returns the file and the save result.</summary>
    private static (byte[] File, PdfSaveResult Result) SignLongTerm(byte[] original, TestCa ca, double x = 50, bool append = false)
    {
        using var doc = PdfDocument.Open(original);
        var pages = PageItem.From(doc);
        var edited = new[] { pages[0].WithElements([.. pages[0].Elements, Signature(x)]) };
        var path = TempPath();
        var result = SignedSaving.Save(doc, edited, path, new PdfSaveOptions(), append, ca.Signer, DateTimeOffset.Now, revocation: ca);
        var file = File.ReadAllBytes(path);
        File.Delete(path);
        return (file, result);
    }

    private static IReadOnlyList<SignatureCheck> Verify(byte[] file, TestCa ca)
    {
        using var doc = PdfDocument.Open(file);
        return SignatureVerifier.Verify(doc, null, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], CancellationToken.None);
    }

    [Fact]
    public void Ocsp_Good_EmbeddedInDss_SignatureStaysValid()
    {
        using var ca = new TestCa();
        var (file, result) = SignLongTerm(TestPdf.Simple(1).Build(), ca);
        Assert.Empty(result.LongTermMissing);
        Assert.Equal(1, ca.OcspRequests);
        Assert.Equal(0, ca.CrlRequests);   // OCSP first: the list is not needed then
        using (var doc = PdfDocument.Open(file))
        {
            var data = LongTermValidation.Read(doc)!;
            Assert.Single(data.Ocsps);
            Assert.Empty(data.Crls);
            // only the root: the signer stands in the CMS block and is not repeated (No. 534)
            Assert.Equal(X509CertificateLoader.LoadCertificate(ca.Root.RawData).Thumbprint,
                X509CertificateLoader.LoadCertificate(Assert.Single(data.Certificates)).Thumbprint);
        }
        var check = Assert.Single(Verify(file, ca));
        Assert.Equal(SignatureOutcome.Valid, check.Outcome);
        Assert.Equal(LaterChanges.ValidationData, check.Later);   // the /DSS behind the signature is validation data, no change
        Assert.Equal(RevocationInfo.Coverage.Complete, check.LongTerm);
    }

    [Fact]
    public void Ocsp_DelegatedResponder_Accepted_ItsCertificateEmbedded()
    {
        using var ca = new TestCa { OcspMode = "beauftragt" };
        var (file, result) = SignLongTerm(TestPdf.Simple(1).Build(), ca);
        Assert.Empty(result.LongTermMissing);
        using var doc = PdfDocument.Open(file);
        Assert.Equal(2, LongTermValidation.Read(doc)!.Certificates.Count);   // root and responder (the signer stands in the CMS block, No. 534)
        Assert.Equal(RevocationInfo.Coverage.Complete, Assert.Single(Verify(file, ca)).LongTerm);
    }

    [Theory]
    [InlineData("keine")]
    [InlineData("fremd")]     // signed by a key that is not the authority's
    [InlineData("anderes")]   // an answer about another certificate
    public void Ocsp_Unusable_CrlInstead(string mode)
    {
        using var ca = new TestCa { OcspMode = mode };
        var (file, result) = SignLongTerm(TestPdf.Simple(1).Build(), ca);
        Assert.Empty(result.LongTermMissing);
        Assert.Equal(1, ca.CrlRequests);
        using (var doc = PdfDocument.Open(file))
        {
            var data = LongTermValidation.Read(doc)!;
            Assert.Empty(data.Ocsps);
            Assert.Single(data.Crls);
        }
        Assert.Equal(RevocationInfo.Coverage.Complete, Assert.Single(Verify(file, ca)).LongTerm);
    }

    [Fact]
    public void NothingObtainable_Reported_SavedWithCertificatesOnly()
    {
        using var ca = new TestCa { OcspMode = "fremd", CrlMode = "keine" };
        var (file, result) = SignLongTerm(TestPdf.Simple(1).Build(), ca);
        Assert.Equal(["Max Mustermann"], result.LongTermMissing);
        var check = Assert.Single(Verify(file, ca));
        Assert.Equal(SignatureOutcome.Unconfirmed, check.Outcome);   // the status of the signer is missing (2.5)
        Assert.Equal(Tr.T("Sperrstatus nicht geprüft: nicht für jedes Zertifikat ein Sperrstatus in der Datei."), check.UnconfirmedReason);
        Assert.Equal(RevocationInfo.Coverage.Incomplete, check.LongTerm);
    }

    [Theory]
    [InlineData("gesperrt", "leer")]
    [InlineData("keine", "gesperrt")]
    public void Revoked_NothingSigned_TargetUntouched(string ocsp, string crl)
    {
        using var ca = new TestCa { OcspMode = ocsp, CrlMode = crl };
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        var path = TempPath();
        File.WriteAllBytes(path, [1, 2, 3]);
        var error = Assert.Throws<CertificateRevokedException>(() => SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(),
            false, ca.Signer, DateTimeOffset.Now, revocation: ca));
        Assert.Contains("Max Mustermann", error.Message);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        File.Delete(path);
    }

    [Fact]
    public void SecondSignature_StoreMerged_NothingTwice_FirstStaysValid()
    {
        using var ca = new TestCa();
        var (once, _) = SignLongTerm(TestPdf.Simple(1).Build(), ca);
        var (twice, _) = SignLongTerm(once, ca, x: 300, append: true);
        Assert.Equal(once, twice.AsSpan(0, once.Length).ToArray());
        using (var doc = PdfDocument.Open(twice))
        {
            var data = LongTermValidation.Read(doc)!;
            Assert.Single(data.Certificates);           // the root once (the signer stands in the CMS blocks, No. 534)
            Assert.Single(data.Ocsps);                  // the authority gave the same answer twice: stored once
        }
        var checks = Verify(twice, ca);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Equal(SignatureOutcome.Valid, c.Outcome));
        Assert.All(checks, c => Assert.Equal(RevocationInfo.Coverage.Complete, c.LongTerm));
    }

    [Fact]
    public void OwnCertificate_NothingAsked_CertificateEmbedded()
    {
        using var ca = new TestCa();
        var own = SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"langzeit-{Guid.NewGuid():N}.p12"));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        var path = TempPath();
        var result = SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(), false, own, DateTimeOffset.Now, revocation: ca);
        Assert.Empty(result.LongTermMissing);
        Assert.Equal(0, ca.OcspRequests + ca.CrlRequests);   // self-issued: there is nobody to ask
        using var saved = PdfDocument.Open(File.ReadAllBytes(path));
        Assert.Single(LongTermValidation.Read(saved)!.Certificates);
        File.Delete(path);
    }

    [Fact]
    public void WithTimestamp_AuthorityChainIncluded()
    {
        using var ca = new TestCa();
        using var authority = new SignatureTimestampTests.TestAuthority();
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        var path = TempPath();
        SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(), false, ca.Signer, DateTimeOffset.Now, timestamps: authority, revocation: ca);
        var file = File.ReadAllBytes(path);
        File.Delete(path);
        using var saved = PdfDocument.Open(file);
        Assert.Equal(2, LongTermValidation.Read(saved)!.Certificates.Count);   // root, time-stamping authority (the signer: CMS block, No. 534)
        Assert.Equal(RevocationInfo.Coverage.Complete, Assert.Single(Verify(file, ca)).LongTerm);
    }

    [Fact]
    public void WithoutLongTerm_NoStore()
    {
        using var ca = new TestCa();
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var signed = PdfSigner.Sign(doc, 0, Signature(), ca.Signer, DateTimeOffset.Now);
        Assert.Null(Assert.Single(Verify(signed, ca)).LongTerm);
        Assert.Equal(0, ca.OcspRequests);
    }

    /// <summary>
    /// An issuer that did not come along (a signature card carrying only its own certificate) is fetched from the certificate's caIssuers
    /// address — as a certificate or a PKCS#7 bundle — and taken only if it issued the certificate; then the status can be asked for.
    /// </summary>
    [Theory]
    [InlineData("der", true)]
    [InlineData("p7c", true)]
    [InlineData("fremd", false)]   // right name, other key: not taken
    [InlineData("keine", false)]
    public void IssuerMissing_FetchedFromCaIssuers_OnlyIfItIssued(string mode, bool taken)
    {
        using var ca = new TestCa { IssuerMode = mode };
        var found = RevocationInfo.Collect([(ca.Signer.Certificate, [])], ca, DateTimeOffset.UtcNow);
        Assert.Equal(1, ca.IssuerRequests);
        if (taken)
        {
            Assert.Empty(found.Missing);
            Assert.Contains(found.Certificates, c => c.AsSpan().SequenceEqual(ca.Root.RawData));
            Assert.Single(found.Ocsps);
        }
        else
        {
            Assert.Equal(["Max Mustermann"], found.Missing);
            Assert.Equal(0, ca.OcspRequests);
        }
        // with the issuer at hand nothing is fetched
        using var withRoot = new TestCa();
        RevocationInfo.Collect([(withRoot.Signer.Certificate, [X509CertificateLoader.LoadCertificate(withRoot.Root.RawData)])], withRoot, DateTimeOffset.UtcNow);
        Assert.Equal(0, withRoot.IssuerRequests);
    }

    [Fact]
    public void Addresses_OnlyHttp_DamagedExtensionsGiveNone()
    {
        using var ca = new TestCa();
        Assert.Equal([OcspAddress], RevocationInfo.Addresses(ca.Signer.Certificate, ocsp: true));
        Assert.Equal([CrlAddress], RevocationInfo.Addresses(ca.Signer.Certificate, ocsp: false));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(["ldap://ocsp.example/", "file:///etc/passwd"], null));
        request.CertificateExtensions.Add(new X509Extension("2.5.29.31", [0x30, 0x05, 0xA0, 0x7F, 0x00], false));   // damaged
        using var odd = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Empty(RevocationInfo.Addresses(odd, ocsp: true));
        Assert.Empty(RevocationInfo.Addresses(odd, ocsp: false));
    }

    [Fact]
    public void OverHttp_AsTheStandardSays_ErrorsGiveNothing()
    {
        var handler = new SignatureTimestampTests.Answer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x30, 0x00]) });
        var http = new HttpRevocationSource(handler);
        Assert.Equal([0x30, 0x00], http.Ocsp(OcspAddress, [0x30, 0x00]));
        Assert.Equal([0x30, 0x00], http.Crl(CrlAddress));
        Assert.Equal(HttpMethod.Post, handler.Seen[0].Method);
        Assert.Equal("application/ocsp-request", handler.Seen[0].Content!.Headers.ContentType!.MediaType);
        Assert.Equal(HttpMethod.Get, handler.Seen[1].Method);
        Assert.All(handler.Seen, r => Assert.DoesNotContain(r.Headers, h => h.Key is "Cookie" or "User-Agent" or "Referer"));

        foreach (var answer in new Func<HttpRequestMessage, HttpResponseMessage>[]
                 {
                     _ => throw new HttpRequestException("keine Verbindung"),
                     _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                     _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://anderswo.example/") } },
                     _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[(64 * 1024) + 1]) },
                 })
            Assert.Null(new HttpRevocationSource(new SignatureTimestampTests.Answer(answer)).Ocsp(OcspAddress, [0x30, 0x00]));
        Assert.Null(new HttpRevocationSource(new SignatureTimestampTests.Answer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[(16 << 20) + 1]) }))
            .Crl(CrlAddress));
        Assert.Equal([0x30, 0x00], http.Issuer(IssuerAddress));
        Assert.Equal(HttpMethod.Get, handler.Seen[2].Method);
        Assert.Null(new HttpRevocationSource(new SignatureTimestampTests.Answer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[(1 << 20) + 1]) }))
            .Issuer(IssuerAddress));
    }

    /// <summary>
    /// With a real time-stamping authority (docs/PRUEFUNGEN.md, „Langzeitprüfung“): only when PDFKNEAD_ZEITSTEMPEL names its address. The own
    /// certificate needs no status; the service's chain is asked about at the addresses in its certificates. With PDFKNEAD_ABLAGE=&lt;folder&gt; the
    /// file is kept for a check in other readers.
    /// </summary>
    [Fact]
    public void RealService_WhenGiven()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ZEITSTEMPEL") is not { Length: > 0 } server || !HttpTimestampAuthority.TryParse(server, out var address)) return;
        var own = SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"langzeit-{Guid.NewGuid():N}.p12"));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        var path = TempPath();
        var result = SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(), false, own, DateTimeOffset.Now,
            timestamps: new HttpTimestampAuthority(address), revocation: new HttpRevocationSource());
        var file = File.ReadAllBytes(path);
        File.Delete(path);
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "langzeit-echt.pdf"), file);
        }
        Assert.Empty(result.LongTermMissing);
        using var saved = PdfDocument.Open(file);
        var data = LongTermValidation.Read(saved)!;
        Assert.NotEmpty(data.Ocsps.Concat(data.Crls));
        var check = Assert.Single(SignatureVerifier.Verify(saved));
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        Assert.Equal(RevocationInfo.Coverage.Complete, check.LongTerm);
    }

    /// <summary>A certificate authority with an intermediate: root, intermediate, signer — the signer names the intermediate's address.</summary>
    private sealed class IntermediateCa : IRevocationSource, IDisposable
    {
        public static readonly Uri IntermediateAddress = new("http://muster-ca.example/zwischen.cer");

        public IntermediateCa()
        {
            var now = DateTimeOffset.UtcNow;
            using var rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest("CN=Muster-Wurzel", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            Root = rootRequest.CreateSelfSigned(now.AddYears(-2), now.AddYears(5));
            using var middleKey = RSA.Create(2048);
            var middleRequest = new CertificateRequest("CN=Muster-Zwischenstelle", middleKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            middleRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            middleRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using (var issued = middleRequest.Create(Root, now.AddYears(-1), now.AddYears(4), [0x51]))
                Intermediate = issued.CopyWithPrivateKey(middleKey);
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
            request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, [IntermediateAddress.ToString()]));
            using var signer = request.Create(Intermediate, now.AddDays(-30), now.AddYears(2), [0x52]);
            // as from a signature card: the own certificate only, no chain
            Signer = new SigningCertificate(signer.CopyWithPrivateKey(key), SigningCertificateSource.Token);
        }

        public X509Certificate2 Root { get; }

        public X509Certificate2 Intermediate { get; }

        public SigningCertificate Signer { get; }

        public int IssuerRequests { get; private set; }

        public byte[]? Ocsp(Uri responder, byte[] request, CancellationToken cancel = default) => null;

        public byte[]? Crl(Uri address, CancellationToken cancel = default) => null;

        public byte[]? Issuer(Uri address, CancellationToken cancel = default)
        {
            IssuerRequests++;
            return address == IntermediateAddress ? Intermediate.RawData : null;
        }

        public void Dispose()
        {
            Signer.Dispose();
            Intermediate.Dispose();
            Root.Dispose();
        }
    }

    /// <summary>
    /// A signature card that carries only its own certificate, long-term validation on: the intermediate is fetched from the address the
    /// certificate names before signing and stands in the CMS block — a checker without a connection builds the chain from it (EN 319 142-1,
    /// 6.3; docs/ABLAUFPLAN.md, 4.2, H6; docs/FEHLERGEDAECHTNIS.md, No. 533). Before it was fetched only for /DSS. Without long-term
    /// validation nothing is fetched (offline).
    /// </summary>
    [Fact]
    public void CardWithoutIssuer_LongTermOn_IntermediateInCms()
    {
        using var ca = new IntermediateCa();
        byte[] Signed(IRevocationSource? revocation)
        {
            using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
            var pages = PageItem.From(doc);
            var path = TempPath();
            SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(), false, ca.Signer, DateTimeOffset.Now,
                revocation: revocation);
            var file = File.ReadAllBytes(path);
            File.Delete(path);
            return file;
        }
        List<string> InCms(byte[] file)
        {
            using var doc = PdfDocument.Open(file);
            var block = CmsSignature.Der(Assert.Single(DigitalSignatures.Find(doc)).Signature!.GetString(PdfName.Contents)!.Bytes)!;
            return [.. LongTermValidation.CertificatesOf(block).Select(c => X509CertificateLoader.LoadCertificate(c).Thumbprint)];
        }
        Assert.Contains(ca.Intermediate.Thumbprint, InCms(Signed(ca)));
        Assert.True(ca.IssuerRequests > 0);
        var asked = ca.IssuerRequests;
        Assert.DoesNotContain(ca.Intermediate.Thumbprint, InCms(Signed(null)));
        Assert.Equal(asked, ca.IssuerRequests);
    }

    /// <summary>
    /// /DSS does not repeat what the new CMS block carries: the signer and the intermediates stand there once, /DSS holds the root and what
    /// else a checker needs (EN 319 142-1, 6.3; docs/ABLAUFPLAN.md, 4.2, H7; docs/FEHLERGEDAECHTNIS.md, No. 534). The signature stays complete
    /// for the long term.
    /// </summary>
    [Fact]
    public void Dss_DoesNotRepeatCmsCertificates()
    {
        using var ca = new TestCa();
        var (file, _) = SignLongTerm(TestPdf.Simple(1).Build(), ca);
        using var doc = PdfDocument.Open(file);
        var block = CmsSignature.Der(Assert.Single(DigitalSignatures.Find(doc)).Signature!.GetString(PdfName.Contents)!.Bytes)!;
        var inCms = LongTermValidation.CertificatesOf(block).Select(Convert.ToBase64String).ToHashSet();
        var dss = LongTermValidation.Read(doc)!.Certificates;
        Assert.NotEmpty(dss);
        Assert.DoesNotContain(dss, c => inCms.Contains(Convert.ToBase64String(c)));
        Assert.Equal(RevocationInfo.Coverage.Complete, Assert.Single(Verify(file, ca)).LongTerm);
    }

    /// <summary>
    /// An encrypted form (AES-256, empty open password, as official forms come) signed with long-term validation and an archive time stamp:
    /// signature, validation data and document time stamp each in an encrypted update, opened again after each step with the file's key —
    /// both valid, the store complete (B-LTA; docs/ABLAUFPLAN.md, 4.4 e; docs/FEHLERGEDAECHTNIS.md, No. 543).
    /// </summary>
    [Fact]
    public void EncryptedForm_LongTermAndArchive_AllValid()
    {
        using var ca = new TestCa();
        using var authority = new SignatureTimestampTests.TestAuthority();
        var original = TestPdf.Simple(1).Build(cipher: Core.Pdf.Security.PdfCipher.Aes256, user: "", permissions: Core.Pdf.Security.PdfPermissions.All);
        using var doc = PdfDocument.Open(original);
        var pages = PageItem.From(doc);
        var path = TempPath();
        var result = SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(), true, ca.Signer, DateTimeOffset.Now,
            timestamps: authority, revocation: ca, archive: true);
        var file = File.ReadAllBytes(path);
        File.Delete(path);
        Assert.Empty(result.LongTermMissing);
        Assert.Equal(original, file.AsSpan(0, original.Length).ToArray());
        using var saved = PdfDocument.Open(file);
        Assert.True(saved.IsEncrypted);
        Assert.NotNull(LongTermValidation.Read(saved));
        var checks = SignatureVerifier.Verify(saved, null, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], CancellationToken.None);
        Assert.Equal(2, checks.Count);
        Assert.Single(checks, c => c.IsDocumentTimestamp);
        Assert.All(checks, c => Assert.True(SignatureTexts.IsValid(c), c.Problem));
    }
}
