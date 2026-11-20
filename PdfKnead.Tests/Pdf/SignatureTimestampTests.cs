using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Signature time stamps (PAdES B-T, KONZEPT.md, section 5.17, „Zeitstempel“): the signature carries the token of the service and the check
/// shows its time and name; a token that does not fit, a service that does not answer or redirects, an answer that is too large — no
/// signature then. The service is a stand-in answering like a real one (RFC 3161).
/// </summary>
public sealed class SignatureTimestampTests
{
    private static readonly Lazy<SigningCertificate> Signer = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"zeitstempel-{Guid.NewGuid():N}.p12")));

    internal static readonly DateTimeOffset StampTime = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A time-stamping authority in the test: a self-signed certificate for time-stamping, answers with tokens for what it is asked.</summary>
    internal sealed class TestAuthority : ITimestampAuthority, IDisposable
    {
        private readonly X509Certificate2 _certificate;

        /// <summary>The certificate the token's signing-certificate names; null: its own (another one: a token whose certificate was swapped).</summary>
        private readonly X509Certificate2? _boundTo;

        /// <summary>The time the service stamps; null: <see cref="StampTime"/>.</summary>
        private readonly DateTimeOffset? _at;

        /// <summary>The token is signed with SHA-1 (a service with an outdated method).</summary>
        private readonly bool _sha1;

        /// <param name="sha1">The token is signed with SHA-1.</param>
        /// <param name="expired">The service's certificate expired before the time it stamps.</param>
        /// <param name="withoutPurpose">The service's certificate does not name the purpose time stamping.</param>
        /// <param name="issuer">An authority (with its key) that issues the service's certificate; null: self-signed.</param>
        /// <param name="notAfter">The end of the service's certificate with an issuer (else five years after <see cref="StampTime"/>).</param>
        public TestAuthority(string name = "Zeitstempeldienst Test", Func<byte[], byte[]>? imprint = null, X509Certificate2? boundTo = null,
            DateTimeOffset? at = null, bool sha1 = false, bool expired = false, bool withoutPurpose = false, X509Certificate2? issuer = null,
            DateTimeOffset? notAfter = null)
        {
            _boundTo = boundTo;
            _at = at;
            _sha1 = sha1;
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!withoutPurpose) request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], critical: true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using var created = issuer is not null
                ? request.Create(issuer, DateTimeOffset.Now.AddYears(-2), notAfter ?? StampTime.AddYears(5), RandomNumberGenerator.GetBytes(8)).CopyWithPrivateKey(key)
                : expired ? request.CreateSelfSigned(StampTime.AddYears(-3), StampTime.AddDays(-1))
                : request.CreateSelfSigned(StampTime.AddYears(-1), StampTime.AddYears(5));
            _certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
            Imprint = imprint;
        }

        /// <summary>Changes the hash the token is made for (a service answering for something else).</summary>
        public Func<byte[], byte[]>? Imprint { get; }

        public int Requests { get; private set; }

        /// <summary>The service's certificate (to list it as a qualified service in tests).</summary>
        public X509Certificate2 Certificate => _certificate;

        public byte[] Request(byte[] request, CancellationToken cancel = default)
        {
            Requests++;
            Assert.True(Rfc3161TimestampRequest.TryDecode(request, out var asked, out _));
            var hash = asked.GetMessageHash().ToArray();
            var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4.5"), asked.HashAlgorithmId, Imprint?.Invoke(hash) ?? hash, new byte[] { 0x01, 0x23 },
                _at ?? StampTime, accuracyInMicroseconds: 1_000_000, isOrdering: false, nonce: asked.GetNonce());
            var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, _certificate) { IncludeOption = X509IncludeOption.EndCertOnly };
            if (_sha1) signer.DigestAlgorithm = new Oid("1.3.14.3.2.26");
            signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", CmsSignature.SigningCertificateV2Value(_boundTo ?? _certificate)));
            cms.ComputeSignature(signer);
            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence())
            {
                using (writer.PushSequence()) writer.WriteInteger(0);   // granted
                writer.WriteEncodedValue(cms.Encode());
            }
            return writer.Encode();
        }

        public void Dispose() => _certificate.Dispose();
    }

    private static InkElement Signature() => new()
    {
        Rect = new PdfRectangle(50, 100, 250, 180),
        Strokes = [[0, 20, 40, 70, 90, 10, 140, 60, 190, 30]],
        StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann · 31.10.2026 12:00"),
    };

    [Fact]
    public void Signed_WithTimestamp_CheckShowsTimeAndService()
    {
        using var authority = new TestAuthority();
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var signed = PdfSigner.Sign(doc, 0, Signature(), Signer.Value, DateTimeOffset.Now, authority);
        Assert.Equal(1, authority.Requests);
        using var saved = PdfDocument.Open(signed);
        var check = Assert.Single(SignatureVerifier.Verify(saved));
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        Assert.NotNull(check.Timestamp);
        Assert.True(check.Timestamp!.Valid);
        Assert.Equal(StampTime, check.Timestamp.Time);
        Assert.Equal("Zeitstempeldienst Test", check.Timestamp.Authority);
        Assert.False(check.Timestamp.Trusted);   // the test service is known to nobody
        Assert.Contains(check.Warnings, w => w.Contains("Zeitstempeldienst"));
    }

    /// <summary>
    /// A token whose signed attribute signing-certificate names another certificate than the service's (ESSCertID, mandatory for RFC 3161
    /// tokens): the time stamp does not fit (docs/ABLAUFPLAN.md, 2.2, step 4 — whether the platform checks it was not known before).
    /// </summary>
    [Fact]
    public void TokenWithForeignEssCertId_Rejected()
    {
        // signed with an honest service (the gap has room for a token), then the token swapped for one naming another certificate — the
        // platform refuses such an answer when signing, so a file with it is made by hand
        using var honest = new TestAuthority();
        using var other = new TestAuthority("Anderer Dienst");
        using var swapped = new TestAuthority(boundTo: other.Certificate);
        byte[] signed;
        using (var doc = PdfDocument.Open(TestPdf.Simple(1).Build()))
            signed = PdfSigner.Sign(doc, 0, Signature(), Signer.Value, DateTimeOffset.Now, honest);
        long[] range;
        using (var doc = PdfDocument.Open(signed)) range = DigitalSignatures.Find(doc).Single().ByteRange;
        var gap = (int)(range[2] - range[1] - 2);
        var block = Convert.FromHexString(System.Text.Encoding.ASCII.GetString(signed, (int)range[1] + 1, gap));
        AsnDecoder.ReadEncodedValue(block, AsnEncodingRules.BER, out _, out _, out var length);
        var cms = new SignedCms();
        cms.Decode(block[..length]);
        var signerInfo = cms.SignerInfos[0];
        signerInfo.RemoveUnsignedAttribute(signerInfo.UnsignedAttributes[0].Values[0]);
        var request = Rfc3161TimestampRequest.CreateFromSignerInfo(signerInfo, HashAlgorithmName.SHA256, requestSignerCertificates: true);
        var response = new AsnReader(swapped.Request(request.Encode()), AsnEncodingRules.DER).ReadSequence();
        response.ReadSequence();   // the status
        cms.SignerInfos[0].AddUnsignedAttribute(new AsnEncodedData("1.2.840.113549.1.9.16.2.14", response.ReadEncodedValue().ToArray()));
        var hex = System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(cms.Encode()));
        Assert.True(hex.Length <= gap);
        Array.Fill(signed, (byte)'0', (int)range[1] + 1, gap);
        hex.CopyTo(signed, (int)range[1] + 1);
        using var saved = PdfDocument.Open(signed);
        var check = Assert.Single(SignatureVerifier.Verify(saved));
        Assert.True(check.Timestamp is null or { Valid: false }, "Der Zeitstempel mit fremder Zertifikatsangabe galt als passend.");
    }

    [Fact]
    public void Signed_WithoutTimestamp_NoneShown()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        using var saved = PdfDocument.Open(PdfSigner.Sign(doc, 0, Signature(), Signer.Value, DateTimeOffset.Now));
        Assert.Null(Assert.Single(SignatureVerifier.Verify(saved)).Timestamp);
    }

    [Fact]
    public void TokenForSomethingElse_Refused_NothingSigned()
    {
        using var authority = new TestAuthority(imprint: h => SHA256.HashData(h));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        Assert.Throws<TimestampException>(() => PdfSigner.Sign(doc, 0, Signature(), Signer.Value, DateTimeOffset.Now, authority));
    }

    /// <summary>Answers HTTP requests instead of the network and keeps them.</summary>
    internal sealed class Answer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return answer(request);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    [Fact]
    public void OverHttp_RequestAsTheStandardSays_ErrorsBecomeTimestampExceptions()
    {
        using var authority = new TestAuthority();
        var handler = new Answer(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(authority.Request(r.Content!.ReadAsByteArrayAsync().Result)) });
        var http = new HttpTimestampAuthority(new Uri("https://zeitstempel.example/tsa"), handler);
        using (var doc = PdfDocument.Open(TestPdf.Simple(1).Build()))
        using (var saved = PdfDocument.Open(PdfSigner.Sign(doc, 0, Signature(), Signer.Value, DateTimeOffset.Now, http)))
            Assert.True(Assert.Single(SignatureVerifier.Verify(saved)).Timestamp!.Valid);
        var sent = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("application/timestamp-query", sent.Content!.Headers.ContentType!.MediaType);
        Assert.DoesNotContain(sent.Headers, h => h.Key is "Cookie" or "User-Agent" or "Referer");

        // not reachable, refused, redirected (not followed), too large
        HttpResponseMessage Huge(HttpRequestMessage _) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[(1 << 20) + 10]) };
        foreach (var answer in new Func<HttpRequestMessage, HttpResponseMessage>[]
                 {
                     _ => throw new HttpRequestException("keine Verbindung"),
                     _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                     _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://anderswo.example/") } },
                     Huge,
                     _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x30, 0x03, 0x02, 0x01, 0x02]) },   // rejection
                 })
        {
            using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
            var failing = new HttpTimestampAuthority(new Uri("https://zeitstempel.example/tsa"), new Answer(answer));
            Assert.Throws<TimestampException>(() => PdfSigner.Sign(doc, 0, Signature(), Signer.Value, DateTimeOffset.Now, failing));
        }
        Assert.True(HttpTimestampAuthority.TryParse(" http://timestamp.example/ ", out _));
        Assert.False(HttpTimestampAuthority.TryParse("ftp://timestamp.example/", out _));
        Assert.False(HttpTimestampAuthority.TryParse("timestamp.example", out _));
    }

    /// <summary>
    /// A service that wants a login: user name and password go as HTTP Basic, only over https; a refused login is a
    /// <see cref="TimestampLoginException"/>; a customer certificate opens with its password only.
    /// </summary>
    [Fact]
    public void Login_BasicOverHttpsOnly_RefusedIsLoginException_CertificateNeedsItsPassword()
    {
        using var authority = new TestAuthority();
        var handler = new Answer(r => r.Headers.Authorization is { Scheme: "Basic", Parameter: var p } && p == Convert.ToBase64String("max:geheim"u8.ToArray())
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(authority.Request(r.Content!.ReadAsByteArrayAsync().Result)) }
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var https = new Uri("https://zeitstempel.example/tsa");
        using (var doc = PdfDocument.Open(TestPdf.Simple(1).Build()))
        using (var saved = PdfDocument.Open(PdfSigner.Sign(doc, 0, Signature(), Signer.Value, DateTimeOffset.Now,
                   new HttpTimestampAuthority(https, handler, new TimestampLogin("max", "geheim", null)))))
            Assert.True(Assert.Single(SignatureVerifier.Verify(saved)).Timestamp!.Valid);

        using (var doc = PdfDocument.Open(TestPdf.Simple(1).Build()))
        {
            var wrong = new HttpTimestampAuthority(https, handler, new TimestampLogin("max", "falsch", null));
            Assert.Contains("abgelehnt", Assert.Throws<TimestampLoginException>(() => PdfSigner.Sign(doc, 0, Signature(), Signer.Value, DateTimeOffset.Now, wrong)).Message);
            var none = new HttpTimestampAuthority(https, handler);
            Assert.Contains("verlangt eine Anmeldung", Assert.Throws<TimestampLoginException>(() => SignatureTimestamps.Probe(none)).Message);
            var plain = new HttpTimestampAuthority(new Uri("http://zeitstempel.example/tsa"), handler, new TimestampLogin("max", "geheim", null));
            var before = handler.Seen.Count;
            Assert.Throws<TimestampException>(() => SignatureTimestamps.Probe(plain));
            Assert.Equal(before, handler.Seen.Count);   // nothing sent over http
        }

        var p12 = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"kunde-{Guid.NewGuid():N}.p12");
        Directory.CreateDirectory(Path.GetDirectoryName(p12)!);
        SigningCertificateTests.WriteOwnPfx("Max Mustermann", p12, "kunde-2026");
        Assert.Throws<CertificatePasswordException>(() => TimestampLogin.OpenCertificate(p12, "falsch"));
        using var customer = TimestampLogin.OpenCertificate(p12, "kunde-2026");
        Assert.True(customer.HasPrivateKey);
        File.Delete(p12);
    }

    /// <summary>
    /// With a real service (docs/PRUEFUNGEN.md, „Zeitstempel“): only when PDFKNEAD_ZEITSTEMPEL names its address — the tests never go to the
    /// network otherwise. With PDFKNEAD_ABLAGE=&lt;folder&gt; the signed file is kept for a check in other readers.
    /// </summary>
    [Fact]
    public void RealService_WhenGiven()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ZEITSTEMPEL") is not { Length: > 0 } server || !HttpTimestampAuthority.TryParse(server, out var address)) return;
        var authority = new HttpTimestampAuthority(address);
        var probe = SignatureTimestamps.Probe(authority);
        Assert.True(probe.Valid);
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var signed = PdfSigner.Sign(doc, 0, Signature(), Signer.Value, DateTimeOffset.Now, authority);
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "zeitstempel-echt.pdf"), signed);
        }
        using var saved = PdfDocument.Open(signed);
        var check = Assert.Single(SignatureVerifier.Verify(saved));
        Assert.True(SignatureTexts.IsValid(check), check.Problem);
        Assert.True(check.Timestamp!.Valid);
        Assert.InRange(check.Timestamp.Time, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(10));
    }

    /// <summary>
    /// A token PDF Knead does not build into a new signature: signed with SHA-1, from a service whose certificate expired before the time it
    /// stamps, or whose certificate is not meant for time stamps (RFC 3161, 2.3; EN 319 142-1, 6.2.1) — refused with the reason, nothing is
    /// signed; „Dienst prüfen“ says the same. Before a token signed with SHA-1 was taken (docs/ABLAUFPLAN.md, 4.1, H3; docs/FEHLERGEDAECHTNIS.md,
    /// No. 528).
    /// </summary>
    [Theory]
    [InlineData("sha1")]
    [InlineData("abgelaufen")]
    [InlineData("ohne Zweck")]
    public void UnfitToken_Refused(string fault)
    {
        using var authority = new TestAuthority(sha1: fault == "sha1", expired: fault == "abgelaufen", withoutPurpose: fault == "ohne Zweck");
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var refused = Assert.Throws<TimestampException>(() => PdfSigner.Sign(doc, 0, Signature(), Signer.Value, DateTimeOffset.Now, authority));
        // SHA-1 was taken before (the finding); the expired certificate and the missing purpose the platform already refuses when it reads the
        // answer — kept as a safeguard, the own check names the reason if that ever changes
        if (fault == "sha1")
            Assert.Equal(Tr.T("Der Zeitstempeldienst signiert mit einem veralteten Verfahren (SHA-1 oder MD5); sein Zeitstempel würde die Zeit nicht sicher bestätigen."),
                refused.Message);
        Assert.Throws<TimestampException>(() => SignatureTimestamps.Probe(authority));
        // the document time stamp the same way
        Assert.Throws<TimestampException>(() => SignatureTimestamps.TokenFor(new byte[32], authority));
    }
}
