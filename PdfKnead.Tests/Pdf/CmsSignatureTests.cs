using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Signatures;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The CMS block built and checked from the digest streamed over the signed bytes (S7, memory with large files): the same block as
/// <see cref="SignedCms"/> writes, and the same verdict as <see cref="SignedCms.CheckSignature(bool)"/>, for the methods handled; the
/// others are left to <see cref="SignedCms"/>.
/// </summary>
public sealed class CmsSignatureTests
{
    private static readonly byte[] Content = RandomNumberGenerator.GetBytes(100_000);

    private static X509Certificate2 Rsa()
    {
        using var key = RSA.Create(2048);
        return new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 Ecdsa()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static byte[] DotNet(X509Certificate2 certificate, string digest, RSASignaturePadding? padding = null, bool attributes = false)
    {
        var cms = new SignedCms(new ContentInfo(Content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate) { DigestAlgorithm = new Oid(digest), SignaturePadding = padding };
        if (attributes) signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
        cms.ComputeSignature(signer, silent: true);
        return cms.Encode();
    }

    private static CmsSignature.DigestCheck Check(byte[] der, X509Certificate2 certificate, byte[] content) =>
        CmsSignature.CheckDigest(der, certificate, hash => hash == HashAlgorithmName.SHA1 ? SHA1.HashData(content)
            : hash == HashAlgorithmName.SHA256 ? SHA256.HashData(content) : hash == HashAlgorithmName.SHA384 ? SHA384.HashData(content)
            : SHA512.HashData(content));

    [Fact]
    public void Rsa_SameBlockAsDotNet()
    {
        using var certificate = Rsa();
        Assert.Equal(CmsSignature.Create(Content, certificate), CmsSignature.CreateFromDigest(SHA256.HashData(Content), certificate));
    }

    [Fact]
    public void Ecdsa_AcceptedByDotNet_RejectedForOtherBytes()
    {
        using var certificate = Ecdsa();
        var der = CmsSignature.CreateFromDigest(SHA256.HashData(Content), certificate)!;
        var cms = new SignedCms(new ContentInfo(Content), detached: true);
        cms.Decode(der);
        cms.CheckSignature(verifySignatureOnly: true);
        Assert.Equal(3, cms.SignerInfos[0].SignedAttributes.Count);

        var other = (byte[])Content.Clone();
        other[500] ^= 1;
        var changed = new SignedCms(new ContentInfo(other), detached: true);
        changed.Decode(der);
        Assert.ThrowsAny<CryptographicException>(() => changed.CheckSignature(verifySignatureOnly: true));
    }

    /// <summary>Blocks from .NET with and without signed attributes, several digests, RSA and ECDSA: valid for the bytes, invalid for others.</summary>
    [Theory]
    [InlineData("2.16.840.1.101.3.4.2.1", false, false)]
    [InlineData("2.16.840.1.101.3.4.2.1", true, false)]
    [InlineData("1.3.14.3.2.26", true, false)]
    [InlineData("2.16.840.1.101.3.4.2.3", false, false)]
    [InlineData("2.16.840.1.101.3.4.2.2", true, true)]
    [InlineData("2.16.840.1.101.3.4.2.1", false, true)]
    public void Check_SameVerdictAsDotNet(string digest, bool attributes, bool ecdsa)
    {
        using var certificate = ecdsa ? Ecdsa() : Rsa();
        var der = DotNet(certificate, digest, attributes: attributes);
        Assert.Equal(CmsSignature.DigestCheck.Valid, Check(der, certificate, Content));
        var other = (byte[])Content.Clone();
        other[0] ^= 1;
        Assert.Equal(CmsSignature.DigestCheck.Invalid, Check(der, certificate, other));
    }

    [Fact]
    public void Check_TamperedSignatureOrDigestAttribute_Invalid()
    {
        using var certificate = Rsa();
        var der = CmsSignature.CreateFromDigest(SHA256.HashData(Content), certificate)!;
        var signature = (byte[])der.Clone();
        signature[^10] ^= 1;   // inside the signature value at the end: the bytes are those signed, the value is not the signer's
        Assert.Equal(CmsSignature.DigestCheck.ValueMismatch, Check(signature, certificate, Content));

        var digest = SHA256.HashData(Content);
        var attribute = (byte[])der.Clone();
        var at = attribute.AsSpan().IndexOf(digest);
        attribute[at] ^= 1;   // the stated message digest
        Assert.Equal(CmsSignature.DigestCheck.Invalid, Check(attribute, certificate, Content));
    }

    /// <summary>Methods not handled here (MD5) are left to <see cref="SignedCms"/>; RSA-PSS is checked here since No. 537.</summary>
    [Fact]
    public void Check_OtherMethods_Unsupported()
    {
        using var certificate = Rsa();
        Assert.Equal(CmsSignature.DigestCheck.Valid, Check(DotNet(certificate, "2.16.840.1.101.3.4.2.1", RSASignaturePadding.Pss), certificate, Content));
        Assert.Equal(CmsSignature.DigestCheck.Unsupported, Check(DotNet(certificate, "1.2.840.113549.2.5"), certificate, Content));
        Assert.Equal(CmsSignature.DigestCheck.Unsupported, Check([0x30, 0x03, 0x02, 0x01, 0x01], certificate, Content));
    }

    /// <summary>
    /// The fallback path (a key that is neither RSA nor ECDSA signs through the platform) writes exactly the three signed attributes PAdES
    /// allows — content-type, message-digest, signing-certificate-v2 — and no signing-time beside /M (EN 319 142-1, Table 1 and 6.3;
    /// docs/ABLAUFPLAN.md, 4.2, H8). A safeguard: a change of the platform that added one would be noticed here.
    /// </summary>
    [Fact]
    public void Create_FallbackPath_OnlyPadesAttributes()
    {
        using var certificate = Rsa();
        var cms = new SignedCms(new ContentInfo(Content), detached: true);
        cms.Decode(CmsSignature.Create(Content, certificate));
        var oids = cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid.Value).Order().ToList();
        Assert.Equal(["1.2.840.113549.1.9.16.2.47", "1.2.840.113549.1.9.3", "1.2.840.113549.1.9.4"], oids);
    }

    /// <summary>The salt length in the PSS parameters of a block made here (32 bytes for SHA-256) changed to another.</summary>
    internal static byte[] WithPssSalt(byte[] der, byte salt)
    {
        var at = der.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xA2, 0x03, 0x02, 0x01, 0x20]);
        Assert.True(at > 0);
        var changed = (byte[])der.Clone();
        changed[at + 4] = salt;
        return changed;
    }

    /// <summary>
    /// RSASSA-PSS (ISO 32000-2, Table 260; RFC 4055): a block signed with PSS names id-RSASSA-PSS with its parameters — SHA-256, MGF1 with
    /// SHA-256, salt 32 — and is checked streamed, like one the platform makes, and by the platform itself; a changed byte fails. Another
    /// salt length is not checked here: „unsupported parameters“, never „changed“ (docs/ABLAUFPLAN.md, 4.3, H9; docs/FEHLERGEDAECHTNIS.md,
    /// No. 537). Before a PSS block was only checked with a copy of the whole content, and PDF Knead could not sign with PSS.
    /// </summary>
    [Fact]
    public void Pss_SignedAndVerifiedStreamed()
    {
        using var certificate = Rsa();
        var der = CmsSignature.CreateFromDigest(SHA256.HashData(Content), certificate, pss: true)!;
        var cms = new SignedCms(new ContentInfo(Content), detached: true);
        cms.Decode(der);
        Assert.Equal("1.2.840.113549.1.1.10", cms.SignerInfos[0].SignatureAlgorithm.Value);
        cms.CheckSignature(verifySignatureOnly: true);   // the platform accepts it
        Assert.Equal(CmsSignature.DigestCheck.Valid, Check(der, certificate, Content));
        var other = (byte[])Content.Clone();
        other[0] ^= 1;
        Assert.Equal(CmsSignature.DigestCheck.Invalid, Check(der, certificate, other));
        // one the platform made, with signed attributes: checked streamed the same way
        Assert.Equal(CmsSignature.DigestCheck.Valid, Check(DotNet(certificate, "2.16.840.1.101.3.4.2.1", RSASignaturePadding.Pss, attributes: true), certificate, Content));
        Assert.Equal(CmsSignature.DigestCheck.UnsupportedParameters, Check(WithPssSalt(der, 20), certificate, Content));
    }

    /// <summary>A key like a card that offers RSA only with PSS: PKCS #1 refused as „mechanism not offered“.</summary>
    private sealed class PssOnlyCard(RSA inner) : RSA
    {
        public override RSAParameters ExportParameters(bool includePrivateParameters) => inner.ExportParameters(false);
        public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();
        public override int KeySize { get => inner.KeySize; set { } }
        public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
            padding == RSASignaturePadding.Pkcs1
                ? throw new PdfKnead.Core.Platform.Pkcs11.MechanismUnavailableException("CKR_MECHANISM_INVALID", new Exception())
                : inner.SignHash(hash, hashAlgorithm, padding);
        public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
            inner.VerifyHash(hash, signature, hashAlgorithm, padding);
    }

    /// <summary>
    /// A card that refuses PKCS #1 v1.5 but offers PSS: signed with PSS instead (docs/ABLAUFPLAN.md, 4.3, steps 6 and 7;
    /// docs/FEHLERGEDAECHTNIS.md, No. 537). Before signing stopped with „Dieses Signaturverfahren unterstützt die Karte hier nicht.“
    /// </summary>
    [Fact]
    public void CardWithoutPkcs1_SignsWithPss()
    {
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=Max Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        using var card = new PssOnlyCard(key);
        var der = CmsSignature.CreateFromDigest(SHA256.HashData(Content), publicOnly, key: card)!;
        var cms = new SignedCms(new ContentInfo(Content), detached: true);
        cms.Decode(der);
        Assert.Equal("1.2.840.113549.1.1.10", cms.SignerInfos[0].SignatureAlgorithm.Value);
        Assert.Equal(CmsSignature.DigestCheck.Valid, Check(der, publicOnly, Content));
    }

    /// <summary>
    /// SHA-3 (ISO/TS 32001, 5.1.4): where the system offers it, a block with SHA3-256, -384 or -512 is checked streamed like SHA-2, and a
    /// changed byte fails; where it does not, „unsupported“ and the method is named for „nicht geprüft“ (docs/ABLAUFPLAN.md, 4.4 c;
    /// docs/FEHLERGEDAECHTNIS.md, No. 540). Before every SHA-3 block went to the copy of the whole content.
    /// </summary>
    [Theory]
    [InlineData("2.16.840.1.101.3.4.2.8", "SHA3-256")]
    [InlineData("2.16.840.1.101.3.4.2.9", "SHA3-384")]
    [InlineData("2.16.840.1.101.3.4.2.10", "SHA3-512")]
    public void Sha3Digest_StreamedOrNotChecked(string oid, string name)
    {
        var supported = name switch { "SHA3-256" => SHA3_256.IsSupported, "SHA3-384" => SHA3_384.IsSupported, _ => SHA3_512.IsSupported };
        // without SHA-3 on this system the platform cannot make such a block either (then CmsSignature.NotComputable names the method)
        if (!supported) return;
        using var certificate = Rsa();
        var der = DotNet(certificate, oid, attributes: true);
        Assert.Equal(CmsSignature.DigestCheck.Valid, CmsSignature.CheckDigest(der, certificate, h => CryptographicOperations.HashData(h, Content)));
        var other = (byte[])Content.Clone();
        other[1] ^= 1;
        Assert.Equal(CmsSignature.DigestCheck.Invalid, CmsSignature.CheckDigest(der, certificate, h => CryptographicOperations.HashData(h, other)));
    }
}
