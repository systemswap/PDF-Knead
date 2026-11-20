using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Keys that sign only once per signature (a card asks for the PIN each time, later a remote service wants a release each time;
/// docs/ABLAUFPLAN.md, 9.4.2; docs/FEHLERGEDAECHTNIS.md, No. 718): the room of the block is sized before signing, so a long chain does not
/// need a second signing; if the block does not fit after all, nothing is saved and the key is not asked again. The number of signatures is
/// told beforehand.
/// </summary>
public sealed class RemoteKeyTests
{
    /// <summary>A key that signs once per signature and counts its signings (a stand-in for a card or a remote service).</summary>
    private sealed class CountingKey : RSA, IRemoteKey
    {
        private readonly RSA _key;

        public CountingKey(RSA key)
        {
            _key = key;
            KeySizeValue = key.KeySize;
            LegalKeySizesValue = [new KeySizes(key.KeySize, key.KeySize, 0)];
        }

        public int Signings { get; private set; }

        public int? Expected { get; private set; }

        public void Expect(int signatures) => Expected = signatures;

        public override RSAParameters ExportParameters(bool includePrivateParameters) => _key.ExportParameters(false);

        public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();

        public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
        {
            Signings++;
            return _key.SignHash(hash, hashAlgorithm, padding);
        }

        public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
            _key.VerifyHash(hash, signature, hashAlgorithm, padding);
    }

    private static InkElement Signature() => new()
    {
        Rect = new PdfRectangle(50, 100, 250, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann"),
    };

    /// <summary>A signer under a chain of <paramref name="links"/> authorities with 4 096-bit keys (a long chain), the key a <see cref="CountingKey"/>.</summary>
    private static (SigningCertificate Signer, CountingKey Key, List<IDisposable> Owned) LongChain(int links)
    {
        var owned = new List<IDisposable>();
        var now = DateTimeOffset.Now;
        var issuerKey = RSA.Create(4096);
        owned.Add(issuerKey);
        var root = new CertificateRequest("CN=Muster-Wurzel Lang", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        root.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var issuer = root.CreateSelfSigned(now.AddDays(-2), now.AddYears(3));
        owned.Add(issuer);
        var chain = new List<X509Certificate2>();
        for (var i = 0; i < links; i++)
        {
            var key = RSA.Create(4096);
            owned.Add(key);
            var request = new CertificateRequest($"CN=Muster-Zwischenstelle {i + 1}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var issued = request.Create(issuer, now.AddDays(-1), now.AddYears(2), [(byte)(0x10 + i)]);
            issuer = issued.CopyWithPrivateKey(key);
            owned.Add(issuer);
            chain.Add(X509CertificateLoader.LoadCertificate(issuer.RawData));
        }
        var signerKey = RSA.Create(2048);
        owned.Add(signerKey);
        var signer = new CertificateRequest("CN=Max Mustermann", signerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        signer.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        var certificate = signer.Create(issuer, now.AddHours(-1), now.AddYears(1), [0x42]);
        var counting = new CountingKey(signerKey);
        return (new SigningCertificate(certificate, SigningCertificateSource.Token, chain, counting), counting, owned);
    }

    [Fact]
    public void RemoteKey_ReserveSizedUpfront_SignsOnce()
    {
        var (signer, key, owned) = LongChain(9);
        try
        {
            using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
            var signed = PdfSigner.SignUnchecked(doc, 0, Signature(), signer, DateTimeOffset.Now);
            Assert.Equal(1, key.Signings);
            using var saved = PdfDocument.Open(signed);
            Assert.True(SignatureTexts.IsValid(Assert.Single(SignatureVerifier.Verify(saved))));
        }
        finally
        {
            signer.Dispose();
            foreach (var item in owned) item.Dispose();
        }
    }

    /// <summary>A service that answers with a token carrying many certificates — larger than the room for a time stamp.</summary>
    private sealed class BulkyAuthority(SignatureTimestampTests.TestAuthority inner, X509Certificate2[] extra) : ITimestampAuthority
    {
        public byte[] Request(byte[] request, CancellationToken cancel = default)
        {
            var response = new AsnReader(inner.Request(request, cancel), AsnEncodingRules.DER).ReadSequence();
            var status = response.ReadEncodedValue().ToArray();
            var token = new SignedCms();
            token.Decode(response.ReadEncodedValue().Span);
            foreach (var certificate in extra) token.AddCertificate(certificate);
            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence())
            {
                writer.WriteEncodedValue(status);
                writer.WriteEncodedValue(token.Encode());
            }
            return writer.Encode();
        }
    }

    [Fact]
    public void RemoteKey_TooSmall_NothingWritten()
    {
        var (signer, key, owned) = LongChain(1);
        var extra = new List<X509Certificate2>();
        try
        {
            for (var i = 0; i < 24; i++)
            {
                using var big = RSA.Create(4096);
                extra.Add(new CertificateRequest($"CN=Muster-Beigabe {i}", big, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                    .CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1)));
            }
            using var inner = new SignatureTimestampTests.TestAuthority();
            using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
            var pages = PageItem.From(doc);
            var path = Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdf-knead-tests")).FullName, $"fern-{Guid.NewGuid():N}.pdf");
            var refused = Assert.Throws<CryptographicException>(() => SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(),
                false, signer, DateTimeOffset.Now, timestamps: new BulkyAuthority(inner, [.. extra])));
            Assert.Equal(Tr.T("Die Signatur ist größer als der vorab bestimmte Platz; es wurde nichts gespeichert und nicht ein zweites Mal unterschrieben."), refused.Message);
            Assert.Equal(1, key.Signings);
            Assert.Equal(1, key.Expected);
            Assert.False(File.Exists(path));
        }
        finally
        {
            foreach (var certificate in extra) certificate.Dispose();
            signer.Dispose();
            foreach (var item in owned) item.Dispose();
        }
    }
}
