using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Signatures;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// RFC 6211 in signatures (design decision, 24.11.2026; ETSI EN 319 122-1, 5.2.13; docs/FEHLERGEDAECHTNIS.md, No. 1113): PDF-Knead's own
/// signatures carry CMSAlgorithmProtection naming exactly the digest and signature algorithm of the SignerInfo; a foreign block whose
/// attribute names other algorithms, or carries it twice, is broken.
/// </summary>
public sealed class CmsAlgorithmProtectionTests
{
    private const string AlgorithmProtection = "1.2.840.113549.1.9.52";
    private static readonly byte[] Content = RandomNumberGenerator.GetBytes(10_000);

    private static X509Certificate2 Rsa()
    {
        using var key = RSA.Create(2048);
        return new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 Ecdsa(ECCurve curve)
    {
        using var key = ECDsa.Create(curve);
        return new CertificateRequest("CN=Erika Musterfrau", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>The value of the attribute and the identifiers of the SignerInfo, read here with the ASN.1 reader of the runtime.</summary>
    private static (byte[] Value, byte[] Digest, byte[] Signature) Read(byte[] der)
    {
        var cms = new SignedCms();
        cms.Decode(der);
        var signer = cms.SignerInfos[0];
        var values = signer.SignedAttributes.Cast<CryptographicAttributeObject>().Where(a => a.Oid.Value == AlgorithmProtection).ToList();
        var value = Assert.Single(Assert.Single(values).Values.Cast<AsnEncodedData>()).RawData;
        // the identifiers as they stand in the SignerInfo
        var info = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
        info.ReadObjectIdentifier();
        var data = info.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        data.ReadInteger();
        data.ReadSetOf();
        data.ReadSequence();
        if (data.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) data.ReadEncodedValue();
        var signerInfo = data.ReadSetOf().ReadSequence();
        signerInfo.ReadInteger();
        signerInfo.ReadEncodedValue();
        var digest = signerInfo.ReadEncodedValue().ToArray();
        signerInfo.ReadEncodedValue();   // signed attributes
        var signature = signerInfo.ReadEncodedValue().ToArray();
        return (value, digest, signature);
    }

    /// <summary>
    /// RFC 6211, 2: the own block (built from the digest, and the same through SignedCms) carries exactly one CMSAlgorithmProtection, its
    /// digestAlgorithm the SignerInfo's, its signatureAlgorithm [1] the SignerInfo's — RSA and ECDSA on P-256 and P-384 (SHA-384).
    /// </summary>
    [Theory]
    [InlineData("rsa")]
    [InlineData("p256")]
    [InlineData("p384")]
    public void OwnSignature_NamesItsAlgorithms(string kind)
    {
        using var certificate = kind == "rsa" ? Rsa() : Ecdsa(kind == "p256" ? ECCurve.NamedCurves.nistP256 : ECCurve.NamedCurves.nistP384);
        var hash = CmsSignature.HashFor(certificate);
        foreach (var der in new[] { CmsSignature.CreateFromDigest(CryptographicOperations.HashData(hash, Content), certificate)!, CmsSignature.Create(Content, certificate) })
        {
            var (value, digest, signature) = Read(der);
            var protection = new AsnReader(value, AsnEncodingRules.DER).ReadSequence();
            Assert.Equal(digest, protection.ReadEncodedValue().ToArray());
            var signatureInAttribute = protection.ReadEncodedValue().ToArray();
            Assert.Equal(0xA1, signatureInAttribute[0]);
            signatureInAttribute[0] = 0x30;
            Assert.Equal(signature, signatureInAttribute);
            Assert.False(protection.HasData);
            Assert.Equal(CmsSignature.DigestCheck.Valid, CmsSignature.CheckDigest(der, certificate, h => CryptographicOperations.HashData(h, Content)));
        }
    }

    /// <summary>
    /// RFC 6211, 2 and 3.2: a foreign block whose CMSAlgorithmProtection names another digest (SHA-512 beside SHA-256) or another signature
    /// algorithm (ECDSA beside RSA), or carries the attribute twice, is broken — even though its signature value is right (the attribute is
    /// signed). A block with the matching attribute, with NULL parameters where the SignerInfo has none (3), is valid.
    /// </summary>
    [Theory]
    [InlineData("passend", true)]
    [InlineData("passend-null", true)]
    [InlineData("anderer-digest", false)]
    [InlineData("andere-signatur", false)]
    [InlineData("doppelt", false)]
    [InlineData("mit-mac", false)]
    public void ForeignBlock_AttributeChecked(string variant, bool valid)
    {
        using var certificate = Rsa();
        static byte[] Alg(string oid, bool nullParameters = false)
        {
            var w = new AsnWriter(AsnEncodingRules.DER);
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(oid);
                if (nullParameters) w.WriteNull();
            }
            return w.Encode();
        }
        byte[] Protection(string digestOid, string signatureOid, bool nullParameters = false, bool mac = false)
        {
            var w = new AsnWriter(AsnEncodingRules.DER);
            using (w.PushSequence())
            {
                w.WriteEncodedValue(Alg(digestOid, nullParameters));
                var s = Alg(signatureOid, nullParameters);
                s[0] = 0xA1;
                w.WriteEncodedValue(s);
                if (mac)
                {
                    var m = Alg("1.2.840.113549.2.9");
                    m[0] = 0xA2;
                    w.WriteEncodedValue(m);
                }
            }
            return w.Encode();
        }
        const string sha256 = "2.16.840.1.101.3.4.2.1", rsa = "1.2.840.113549.1.1.1";
        var cms = new SignedCms(new ContentInfo(Content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate) { DigestAlgorithm = new Oid(sha256) };
        var values = variant switch
        {
            "passend" => [Protection(sha256, rsa)],
            "passend-null" => [Protection(sha256, rsa, nullParameters: true)],
            "anderer-digest" => [Protection("2.16.840.1.101.3.4.2.3", rsa)],
            "andere-signatur" => [Protection(sha256, "1.2.840.10045.4.3.2")],
            "mit-mac" => [Protection(sha256, rsa, mac: true)],
            _ => new[] { Protection(sha256, rsa), Protection(sha256, rsa) },
        };
        foreach (var v in values) signer.SignedAttributes.Add(new CryptographicAttributeObject(new Oid(AlgorithmProtection), new AsnEncodedDataCollection(new AsnEncodedData(AlgorithmProtection, v))));
        cms.ComputeSignature(signer, silent: true);
        Assert.Equal(valid ? CmsSignature.DigestCheck.Valid : CmsSignature.DigestCheck.AlgorithmMismatch,
            CmsSignature.CheckDigest(cms.Encode(), certificate, h => CryptographicOperations.HashData(h, Content)));
        // the same judgement on the ways without the digest check (adbe.pkcs7.sha1, the platform's check, document time stamps; No. 1119)
        Assert.Equal(valid, CmsSignature.AlgorithmProtectionHolds(cms.Encode()));
    }

    /// <summary>
    /// RSASSA-PSS parameters compared by meaning (RFC 4055, 3.1; second M4 probe, A5): the defaults written out equal them left away, a salt of
    /// another length does not; unreadable parameters never match.
    /// </summary>
    [Fact]
    public void PssParameters_DefaultsWrittenOrLeftAway_Alike()
    {
        static byte[] Pss(bool explicitDefaults, int salt)
        {
            var w = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
            using (w.PushSequence())
            {
                if (explicitDefaults)
                {
                    using (w.PushSequence(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 0)))
                    using (w.PushSequence())
                    {
                        w.WriteObjectIdentifier("1.3.14.3.2.26");
                        w.WriteNull();
                    }
                    using (w.PushSequence(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 1)))
                    using (w.PushSequence())
                    {
                        w.WriteObjectIdentifier("1.2.840.113549.1.1.8");
                        using (w.PushSequence())
                        {
                            w.WriteObjectIdentifier("1.3.14.3.2.26");
                            w.WriteNull();
                        }
                    }
                }
                // the fields in their order: [2] saltLength before [3] trailerField
                if (explicitDefaults || salt != 20)
                    using (w.PushSequence(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 2)))
                        w.WriteInteger(salt);
                if (explicitDefaults)
                    using (w.PushSequence(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 3)))
                        w.WriteInteger(1);
            }
            return w.Encode();
        }
        Assert.True(PdfKnead.Core.Pdf.Signatures.CmsSignature.SamePssParameters(Pss(true, 20), Pss(false, 20)));
        Assert.True(PdfKnead.Core.Pdf.Signatures.CmsSignature.SamePssParameters(Pss(false, 20), null));
        Assert.False(PdfKnead.Core.Pdf.Signatures.CmsSignature.SamePssParameters(Pss(true, 20), Pss(false, 32)));
        Assert.False(PdfKnead.Core.Pdf.Signatures.CmsSignature.SamePssParameters(new byte[] { 0x30, 0x03, 0x02, 0x01 }, null));
    }
}
