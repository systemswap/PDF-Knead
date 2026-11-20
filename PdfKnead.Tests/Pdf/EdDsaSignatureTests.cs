using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Signatures with methods the platform cannot verify — Ed25519, Ed448 with SHAKE256 (fixed and the non-conforming variable length), SHA-3
/// where the system lacks it — are „not checked“, never „the document was changed“ (docs/ABLAUFPLAN.md, 2.4; ISO/TS 32002, 5.1.2 with Table 4
/// and 5.1.3). The blocks are built by hand (the platform signs neither): an Ed25519 or Ed448 key in a certificate a test RSA key issued, a
/// random signature value — what is tested is the judgement, not EdDSA.
/// </summary>
public sealed class EdDsaSignatureTests
{
    private static readonly Lazy<SigningCertificate> Own = new(() =>
        SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"eddsa-{Guid.NewGuid():N}.p12")));

    private const string Ed25519 = "1.3.101.112", Ed448 = "1.3.101.113", Sha512 = "2.16.840.1.101.3.4.2.3", Shake256 = "2.16.840.1.101.3.4.2.12",
        Shake256Len = "2.16.840.1.101.3.4.2.18", Data = "1.2.840.113549.1.7.1", SignedData = "1.2.840.113549.1.7.2";

    private static byte[] Signed()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        return PdfSigner.Sign(doc, 0, new InkElement
        {
            Rect = new PdfRectangle(50, 100, 250, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
            Digital = new DigitalSigning("Max Mustermann"),
        }, Own.Value, DateTimeOffset.Now);
    }

    /// <summary>A certificate for „Erika Mustermann“ with an EdDSA public key of random bytes, issued by a test RSA key.</summary>
    private static X509Certificate2 EdCertificate(string keyOid, int keyLength)
    {
        using var issuerKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest("CN=Muster-CA", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var issuer = issuerRequest.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        var key = new PublicKey(new Oid(keyOid), null, new AsnEncodedData(RandomNumberGenerator.GetBytes(keyLength)));
        var request = new CertificateRequest(new X500DistinguishedName("CN=Erika Mustermann"), key, HashAlgorithmName.SHA256);
        return request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1),
            DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddMonths(6), [1, 2, 3, 4]);
    }

    /// <summary>A detached SignedData block in the shape PdfSigner writes, with the given digest and signature algorithms and a random value.</summary>
    private static byte[] Block(X509Certificate2 certificate, string digestOid, byte[] digest, string signatureOid, int valueLength)
    {
        var attributes = new AsnWriter(AsnEncodingRules.DER);
        using (attributes.PushSetOf())
        {
            using (attributes.PushSequence())
            {
                attributes.WriteObjectIdentifier("1.2.840.113549.1.9.3");
                using (attributes.PushSetOf()) attributes.WriteObjectIdentifier(Data);
            }
            using (attributes.PushSequence())
            {
                attributes.WriteObjectIdentifier("1.2.840.113549.1.9.4");
                using (attributes.PushSetOf()) attributes.WriteOctetString(digest);
            }
        }
        var signedAttributes = attributes.Encode();
        signedAttributes[0] = 0xA0;   // [0] IMPLICIT in the SignerInfo
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(SignedData);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence())
            {
                writer.WriteInteger(1);
                using (writer.PushSetOf())
                using (writer.PushSequence()) writer.WriteObjectIdentifier(digestOid);
                using (writer.PushSequence()) writer.WriteObjectIdentifier(Data);
                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0))) writer.WriteEncodedValue(certificate.RawData);
                using (writer.PushSetOf())
                using (writer.PushSequence())
                {
                    writer.WriteInteger(1);
                    using (writer.PushSequence())
                    {
                        writer.WriteEncodedValue(certificate.IssuerName.RawData);
                        writer.WriteInteger(certificate.SerialNumberBytes.Span);
                    }
                    using (writer.PushSequence()) writer.WriteObjectIdentifier(digestOid);
                    writer.WriteEncodedValue(signedAttributes);
                    using (writer.PushSequence()) writer.WriteObjectIdentifier(signatureOid);
                    writer.WriteOctetString(RandomNumberGenerator.GetBytes(valueLength));
                }
            }
        }
        return writer.Encode();
    }

    /// <summary>The signed file with the block in the gap of its signature (hexadecimal, padded with zeros).</summary>
    private static byte[] WithBlock(byte[] signed, byte[] der)
    {
        using var doc = PdfDocument.Open(signed);
        var range = DigitalSignatures.Find(doc).Single().ByteRange;
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(der));
        var room = (int)(range[2] - range[1] - 2);
        Assert.True(hex.Length <= room);
        var copy = (byte[])signed.Clone();
        Array.Fill(copy, (byte)'0', (int)range[1] + 1, room);
        hex.CopyTo(copy, (int)range[1] + 1);
        return copy;
    }

    private static byte[] Covered(byte[] signed)
    {
        using var doc = PdfDocument.Open(signed);
        var r = DigitalSignatures.Find(doc).Single().ByteRange;
        return [.. signed.AsSpan((int)r[0], (int)r[1]), .. signed.AsSpan((int)r[2], (int)r[3])];
    }

    private static SignatureCheck Check(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        return Assert.Single(SignatureVerifier.Verify(doc, null, [], CancellationToken.None));
    }

    [Fact]
    public void Ed25519Block_NotChecked()
    {
        var signed = Signed();
        using var certificate = EdCertificate(Ed25519, 32);
        var check = Check(WithBlock(signed, Block(certificate, Sha512, SHA512.HashData(Covered(signed)), Ed25519, 64)));
        Assert.Equal(SignatureOutcome.NotChecked, check.Outcome);
        Assert.Equal(Tr.T("Das Signaturverfahren {0} wird nicht geprüft.", "Ed25519"), check.Problem);
    }

    [Theory]
    [InlineData(Shake256, "Ed448/SHAKE256")]
    [InlineData(Shake256Len, null)]
    public void Ed448Block_NotChecked(string digestOid, string? name)
    {
        var signed = Signed();
        using var certificate = EdCertificate(Ed448, 57);
        var check = Check(WithBlock(signed, Block(certificate, digestOid, RandomNumberGenerator.GetBytes(64), Ed448, 114)));
        Assert.Equal(SignatureOutcome.NotChecked, check.Outcome);   // never valid, also with the non-conforming variable length
        Assert.Equal(Tr.T("Das Signaturverfahren {0} wird nicht geprüft.", name ?? "Ed448/" + Tr.T("SHAKE256 mit variabler Länge")), check.Problem);
    }

    /// <summary>SHA-3 where the system does not offer it: not checked. Where it does (Windows 11, recent Linux), there is nothing to test here.</summary>
    [Fact]
    public void Sha3Digest_WithoutPlatformSupport_NotChecked()
    {
        if (SHA3_256.IsSupported) return;   // the platform computes SHA-3: then it is checked like any other digest
        var signed = Signed();
        using var certificate = EdCertificate(Ed25519, 32);
        var check = Check(WithBlock(signed, Block(certificate, "2.16.840.1.101.3.4.2.8", RandomNumberGenerator.GetBytes(32), "1.2.840.113549.1.1.1", 256)));
        Assert.Equal(SignatureOutcome.NotChecked, check.Outcome);
    }

    /// <summary>
    /// An Ed25519 block over a document changed afterwards is never „valid“ (counter-check): its digest (SHA-512) is computed and no longer
    /// fits the bytes — that proves the change, whatever the signature value; so „changed“, not merely „not checked“.
    /// </summary>
    [Fact]
    public void Ed25519Block_DocumentChanged_NeverValid()
    {
        var signed = Signed();
        using var certificate = EdCertificate(Ed25519, 32);
        var file = WithBlock(signed, Block(certificate, Sha512, SHA512.HashData(Covered(signed)), Ed25519, 64));
        var at = Encoding.ASCII.GetString(file).IndexOf("stream", StringComparison.Ordinal) + 12;
        file[at] ^= 0x01;
        var check = Check(file);
        Assert.False(SignatureTexts.IsValid(check));
        Assert.Equal(SignatureOutcome.Invalid, check.Outcome);
    }
}
