using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace PdfKnead.Core.Pdf.Signatures;

/// <summary>
/// The CMS block (RFC 5652) inside a PDF signature, as PAdES baseline B-B requires it (ETSI EN 319 142-1, section 5.1; ETSI EN
/// 319 122-1 for the CAdES attributes): detached SignedData over the signed byte ranges, SHA-256, signed attributes content-type
/// and message-digest (added by <see cref="SignedCms"/>) and signing-certificate-v2 (RFC 5035), and no signing-time attribute,
/// because PAdES takes the time from <c>/M</c> of the signature dictionary.
/// </summary>
/// <remarks>
/// The digest of the signed bytes is streamed over the byte ranges of the file (<see cref="CreateFromDigest"/>, <see cref="CheckDigest"/>):
/// <see cref="SignedCms"/> needs the whole content as one array and copies it once more when reading, two full copies of the file
/// per signature. The CMS structure around the digest is written and read here for RSA (PKCS #1 v1.5) and ECDSA keys, the same as
/// <see cref="SignedCms"/> writes it; other methods fall back to <see cref="SignedCms"/> with the content.
/// </remarks>
public static class CmsSignature
{
    private const string Sha256 = "2.16.840.1.101.3.4.2.1";
    private const string SigningCertificateV2 = "1.2.840.113549.1.9.16.2.47";
    private const string AlgorithmProtectionOid = "1.2.840.113549.1.9.52";   // CMSAlgorithmProtection (RFC 6211)
    private const string SignedDataOid = "1.2.840.113549.1.7.2", DataOid = "1.2.840.113549.1.7.1";
    private const string ContentTypeOid = "1.2.840.113549.1.9.3", MessageDigestOid = "1.2.840.113549.1.9.4";
    private const string RsaOid = "1.2.840.113549.1.1.1", EcPublicKeyOid = "1.2.840.10045.2.1", EcdsaSha256Oid = "1.2.840.10045.4.3.2";

    private static readonly Asn1Tag Context0 = new(TagClass.ContextSpecific, 0, isConstructed: true),
        Context1 = new(TagClass.ContextSpecific, 1, isConstructed: true), Context2 = new(TagClass.ContextSpecific, 2, isConstructed: true),
        Context3 = new(TagClass.ContextSpecific, 3, isConstructed: true);

    /// <summary>The CMS block of a signature value without the padding after it (the /Contents of a signature are reserved larger); null if unreadable.</summary>
    internal static byte[]? Der(byte[] block)
    {
        try
        {
            return AsnDecoder.TryReadEncodedValue(block, AsnEncodingRules.BER, out _, out _, out _, out var consumed)
                ? block.AsSpan(0, consumed).ToArray() : null;
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The signers of a CMS block and whether it carries the certificate of its (first) signer (what PDF/A asks of a signature, ISO 19005-2,
    /// 6.4.3); null if it cannot be read.
    /// </summary>
    internal static (int Signers, bool Certificate)? Signers(byte[] block)
    {
        if (Der(block) is not { } der) return null;
        try
        {
            var cms = new SignedCms();
            cms.Decode(der);
            return (cms.SignerInfos.Count, cms.SignerInfos.Count > 0 && cms.SignerInfos[0].Certificate is not null);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Signs the content (the signed byte ranges of the file, one after the other).</summary>
    /// <param name="content">The content.</param>
    /// <param name="certificate">The certificate with its private key.</param>
    /// <param name="chain">Further certificates to build the chain from (those of a PKCS#12 file); the chain up to, but without, the root
    /// is embedded. Empty for a self-signed certificate.</param>
    /// <param name="key">The private key when the certificate does not carry it (a signature card); null otherwise.</param>
    /// <returns>The DER-encoded CMS block.</returns>
    /// <exception cref="CryptographicException">The key cannot sign (for example a smart card that refused).</exception>
    public static byte[] Create(byte[] content, X509Certificate2 certificate, IReadOnlyList<X509Certificate2>? chain = null, AsymmetricAlgorithm? key = null)
    {
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        // EndCertOnly: .NET would otherwise build the chain itself with downloads allowed (from the address in the certificate); the
        // chain is built here, offline, and its certificates are added by hand
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate, key)
        {
            DigestAlgorithm = new Oid(DigestOid(HashFor(certificate))),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        var links = ChainOf(certificate, chain ?? []);
        try
        {
            foreach (var link in links) signer.Certificates.Add(link);
            signer.SignedAttributes.Add(new AsnEncodedData(SigningCertificateV2, SigningCertificateV2Value(certificate)));
            // CMSAlgorithmProtection where the signature algorithm SignedCms writes is known (RSA PKCS #1, ECDSA; No. 1113)
            if (AlgorithmProtectionFor(certificate) is { } protection) signer.SignedAttributes.Add(new AsnEncodedData(AlgorithmProtectionOid, protection));
            cms.ComputeSignature(signer, silent: true);
            return cms.Encode();
        }
        finally
        {
            foreach (var link in links) link.Dispose();
        }
    }

    /// <summary>
    /// Signs the SHA-256 digest of the content (streamed over the signed byte ranges by the caller), building the same CMS block as
    /// <see cref="Create"/> without needing the content itself.
    /// </summary>
    /// <param name="digest">SHA-256 of the content.</param>
    /// <param name="certificate">The certificate with its private key.</param>
    /// <param name="chain">As for <see cref="Create"/>.</param>
    /// <param name="key">The private key when the certificate does not carry it (a signature card; not disposed here); null otherwise.</param>
    /// <returns>The DER-encoded CMS block; null if the key is neither RSA nor ECDSA (then <see cref="Create"/> with the content).</returns>
    /// <exception cref="CryptographicException">The key cannot sign.</exception>
    /// <param name="pss">RSA with RSASSA-PSS instead of PKCS #1 v1.5 (the tests; otherwise only when the key cannot do PKCS #1).</param>
    internal static byte[]? CreateFromDigest(byte[] digest, X509Certificate2 certificate, IReadOnlyList<X509Certificate2>? chain = null, AsymmetricAlgorithm? key = null,
        bool pss = false)
    {
        return CreateFromDigestCore(digest, certificate, chain, key, pss);
    }

    /// <summary>
    /// The size of the block <see cref="CreateFromDigest"/> builds for this certificate and chain, without the real key (docs/ABLAUFPLAN.md, 9.4.2):
    /// a trial run with a stand-in key of the same kind and size — the signature value has the same length (ECDSA: at most the longest DER
    /// form, a margin covers it). For a key that may sign only once (<see cref="Platform.IRemoteKey"/>).
    /// </summary>
    /// <param name="certificate">The signer's certificate.</param>
    /// <param name="chain">As for <see cref="Create"/>.</param>
    /// <returns>The size in bytes, with a small margin.</returns>
    internal static int SizeFor(X509Certificate2 certificate, IReadOnlyList<X509Certificate2>? chain)
    {
        using var rsaPublic = certificate.GetRSAPublicKey();
        using var ecPublic = rsaPublic is null ? certificate.GetECDsaPublicKey() : null;
        using AsymmetricAlgorithm standIn = rsaPublic is not null ? RSA.Create(rsaPublic.KeySize)
            : ecPublic is not null ? ECDsa.Create(ecPublic.ExportParameters(false).Curve)
            : throw new CryptographicException(Tr.T("Dieses Schlüsselverfahren wird zum Unterschreiben nicht unterstützt."));
        var block = CreateFromDigestCore(new byte[HashLength(HashFor(certificate))], certificate, chain, standIn, pss: false)!;
        return block.Length + 64;
    }

    private static byte[]? CreateFromDigestCore(byte[] digest, X509Certificate2 certificate, IReadOnlyList<X509Certificate2>? chain, AsymmetricAlgorithm? key,
        bool pss)
    {
        using var own = key is null ? certificate.GetRSAPrivateKey() : null;
        using var ownEcdsa = key is null && own is null ? certificate.GetECDsaPrivateKey() : null;
        var rsa = key as RSA ?? own;
        var ecdsa = key as ECDsa ?? ownEcdsa;
        if (rsa is null && ecdsa is null) return null;
        // the digest the curve asks for (ISO/TS 32002, Table 3): the same for the content and the signed attributes (5.1.4)
        var hash = HashFor(certificate);
        var hashOid = DigestOid(hash);
        if (digest.Length != HashLength(hash))
            throw new ArgumentException(Tr.T("Die Prüfsumme passt nicht zum Verfahren des Schlüssels."), nameof(digest));

        // signedAttrs as a SET OF for the signature (RFC 5652, 5.4), sorted as DER requires; with CMSAlgorithmProtection (RFC 6211, 2: the
        // digest and the signature algorithm of the SignerInfo, protected by the signature — ETSI EN 319 122-1, 5.2.13; decision of the
        // user, 24.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 1113)
        var signatureAlgorithm = new AsnWriter(AsnEncodingRules.DER);
        if (rsa is not null && pss) PssAlgorithm(signatureAlgorithm, hash);
        else AlgorithmIdentifier(signatureAlgorithm, rsa is not null ? RsaOid : EcdsaOid(hash));
        var signatureAlgorithmDer = signatureAlgorithm.Encode();
        var attributes = new AsnWriter(AsnEncodingRules.DER);
        using (attributes.PushSetOf())
        {
            Attribute(attributes, ContentTypeOid, w => w.WriteObjectIdentifier(DataOid));
            Attribute(attributes, MessageDigestOid, w => w.WriteOctetString(digest));
            Attribute(attributes, SigningCertificateV2, w => w.WriteEncodedValue(SigningCertificateV2Value(certificate)));
            Attribute(attributes, AlgorithmProtectionOid, w => w.WriteEncodedValue(AlgorithmProtectionValue(hashOid, signatureAlgorithmDer)));
        }
        var signed = attributes.Encode();
        byte[] signature;
        if (rsa is null) signature = ecdsa!.SignData(signed, hash, DSASignatureFormat.Rfc3279DerSequence);
        else
        {
            try
            {
                signature = rsa.SignData(signed, hash, pss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
            }
            catch (Platform.Pkcs11.MechanismUnavailableException) when (!pss)
            {
                // a card that offers RSA only with PSS (ISO 32000-2, Table 260; docs/FEHLERGEDAECHTNIS.md, No. 537), found out only now: the
                // attributes named PKCS #1 — built anew with PSS
                return CreateFromDigestCore(digest, certificate, chain, key, pss: true);
            }
        }

        var links = ChainOf(certificate, chain ?? []);
        try
        {
            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(SignedDataOid);
                using (writer.PushSequence(Context0))
                using (writer.PushSequence())
                {
                    writer.WriteInteger(1);
                    using (writer.PushSetOf()) AlgorithmIdentifier(writer, hashOid);
                    using (writer.PushSequence()) writer.WriteObjectIdentifier(DataOid);   // detached: no content
                    using (writer.PushSetOf(Context0))
                    {
                        writer.WriteEncodedValue(certificate.RawData);
                        foreach (var link in links) writer.WriteEncodedValue(link.RawData);
                    }
                    using (writer.PushSetOf())
                    using (writer.PushSequence())
                    {
                        writer.WriteInteger(1);
                        using (writer.PushSequence())   // issuerAndSerialNumber
                        {
                            writer.WriteEncodedValue(certificate.IssuerName.RawData);
                            writer.WriteInteger(certificate.SerialNumberBytes.Span);
                        }
                        AlgorithmIdentifier(writer, hashOid);
                        var implicitAttributes = signed.ToArray();
                        implicitAttributes[0] = 0xA0;   // [0] IMPLICIT in the SignerInfo
                        writer.WriteEncodedValue(implicitAttributes);
                        writer.WriteEncodedValue(signatureAlgorithmDer);   // without parameters for PKCS #1 and ECDSA, as SignedCms writes it
                        writer.WriteOctetString(signature);
                    }
                }
            }
            return writer.Encode();
        }
        finally
        {
            foreach (var link in links) link.Dispose();
        }
    }

    /// <summary>
    /// The digest a signature with this certificate's key uses: for ECDSA after the size of the curve — SHA-512 from 512 bits (P-521,
    /// brainpoolP512r1), SHA-384 from 384 bits (P-384, brainpoolP384r1), else SHA-256 (ISO/TS 32002, Table 3) —, for RSA SHA-256. Decided by
    /// the size of the public key, not by the curve's name, which differs between platforms (docs/FEHLERGEDAECHTNIS.md, No. 535).
    /// </summary>
    /// <param name="certificate">The signing certificate.</param>
    internal static HashAlgorithmName HashFor(X509Certificate2 certificate)
    {
        using var ecdsa = certificate.GetECDsaPublicKey();
        return ecdsa?.KeySize switch
        {
            >= 512 => HashAlgorithmName.SHA512,
            >= 384 => HashAlgorithmName.SHA384,
            _ => HashAlgorithmName.SHA256,
        };
    }

    /// <summary>Whether the certificate's key is an ECDSA key (ISO/TS 32002 declares its use in a PDF 2.0 file).</summary>
    internal static bool IsEcdsa(X509Certificate2 certificate)
    {
        using var ecdsa = certificate.GetECDsaPublicKey();
        return ecdsa is not null;
    }

    private static string DigestOid(HashAlgorithmName hash) =>
        hash == HashAlgorithmName.SHA512 ? "2.16.840.1.101.3.4.2.3" : hash == HashAlgorithmName.SHA384 ? "2.16.840.1.101.3.4.2.2" : Sha256;

    private static string EcdsaOid(HashAlgorithmName hash) =>
        hash == HashAlgorithmName.SHA512 ? "1.2.840.10045.4.3.4" : hash == HashAlgorithmName.SHA384 ? "1.2.840.10045.4.3.3" : EcdsaSha256Oid;

    private static int HashLength(HashAlgorithmName hash) =>
        hash == HashAlgorithmName.SHA512 || hash == HashAlgorithmName.SHA3_512 ? 64 : hash == HashAlgorithmName.SHA384 || hash == HashAlgorithmName.SHA3_384 ? 48 : 32;

    /// <summary>
    /// id-RSASSA-PSS with its parameters (RFC 4055, 3.1; explicit tags): the hash, MGF1 with the same hash, salt as long as the hash; the
    /// trailer field keeps its default (1). The hash identifiers without parameters (RFC 5754).
    /// </summary>
    private static void PssAlgorithm(AsnWriter writer, HashAlgorithmName hash)
    {
        var hashOid = DigestOid(hash);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(RsaPssOid);
            using (writer.PushSequence())
            {
                using (writer.PushSequence(Context0)) AlgorithmIdentifier(writer, hashOid);
                using (writer.PushSequence(Context1))
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(Mgf1Oid);
                    AlgorithmIdentifier(writer, hashOid);
                }
                using (writer.PushSequence(Context2)) writer.WriteInteger(HashLength(hash));
            }
        }
    }

    /// <summary>
    /// The parameters of an RSASSA-PSS signature this program checks: hash and MGF1 hash equal to the digest algorithm, salt as long as the
    /// hash, trailer field 1 — what the platform verifies. Null for other parameters (then not checked, never „changed“).
    /// </summary>
    private static HashAlgorithmName? PssHash(ReadOnlyMemory<byte>? parameters, HashAlgorithmName digestHash)
    {
        if (parameters is not { } encoded) return null;
        try
        {
            var reader = new AsnReader(encoded, AsnEncodingRules.BER).ReadSequence();
            string hashOid = "1.3.14.3.2.26", mgfHashOid = "1.3.14.3.2.26";   // the defaults: SHA-1 (RFC 4055, 3.1)
            var salt = 20;
            if (reader.HasData && reader.PeekTag().HasSameClassAndValue(Context0)) hashOid = reader.ReadSequence(Context0).ReadSequence().ReadObjectIdentifier();
            if (reader.HasData && reader.PeekTag().HasSameClassAndValue(Context1))
            {
                var mgf = reader.ReadSequence(Context1).ReadSequence();
                if (mgf.ReadObjectIdentifier() != Mgf1Oid) return null;
                mgfHashOid = mgf.ReadSequence().ReadObjectIdentifier();
            }
            if (reader.HasData && reader.PeekTag().HasSameClassAndValue(Context2))
                salt = reader.ReadSequence(Context2).ReadInteger() is var s && s >= 0 && s <= 1024 ? (int)s : -1;
            if (reader.HasData && reader.PeekTag().HasSameClassAndValue(Context3) && reader.ReadSequence(Context3).ReadInteger() != 1) return null;
            var hash = HashOf(hashOid);
            return hash == digestHash && HashOf(mgfHashOid) == digestHash && salt == HashLength(digestHash) ? hash : null;
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    private const string RsaPssOid = "1.2.840.113549.1.1.10", Mgf1Oid = "1.2.840.113549.1.1.8";

    private static void Attribute(AsnWriter writer, string oid, Action<AsnWriter> value)
    {
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(oid);
            using (writer.PushSetOf()) value(writer);
        }
    }

    private static void AlgorithmIdentifier(AsnWriter writer, string oid)
    {
        using (writer.PushSequence()) writer.WriteObjectIdentifier(oid);
    }

    /// <summary>The CMSAlgorithmProtection value for the signature SignedCms makes with this certificate (RSA PKCS #1, ECDSA); null for other keys.</summary>
    private static byte[]? AlgorithmProtectionFor(X509Certificate2 certificate)
    {
        var hash = HashFor(certificate);
        using var rsa = certificate.GetRSAPublicKey();
        using var ecdsa = rsa is null ? certificate.GetECDsaPublicKey() : null;
        if (rsa is null && ecdsa is null) return null;   // EdDSA and others: SignedCms decides the identifier, not named here
        var writer = new AsnWriter(AsnEncodingRules.DER);
        AlgorithmIdentifier(writer, rsa is not null ? RsaOid : EcdsaOid(hash));
        return AlgorithmProtectionValue(DigestOid(hash), writer.Encode());
    }

    /// <summary>
    /// The value of CMSAlgorithmProtection (RFC 6211, 2): SEQUENCE { digestAlgorithm, signatureAlgorithm [1] IMPLICIT } — the identifiers as
    /// the SignerInfo carries them.
    /// </summary>
    private static byte[] AlgorithmProtectionValue(string digestOid, byte[] signatureAlgorithmDer)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            AlgorithmIdentifier(writer, digestOid);
            var implicitSignature = signatureAlgorithmDer.ToArray();
            implicitSignature[0] = 0xA1;   // [1] IMPLICIT AlgorithmIdentifier
            writer.WriteEncodedValue(implicitSignature);
        }
        return writer.Encode();
    }

    /// <summary>Result of <see cref="CheckDigest"/>.</summary>
    internal enum DigestCheck
    {
        /// <summary>The signature value is valid for the digest of the signed bytes.</summary>
        Valid,
        /// <summary>It is not: the bytes were changed, or the block was tampered with.</summary>
        Invalid,
        /// <summary>The block's digest is that of the bytes, but its signature value does not fit the signer's key: forged or damaged.</summary>
        ValueMismatch,
        /// <summary>A method not checked here (then <see cref="SignedCms.CheckSignature(bool)"/> with the content).</summary>
        Unsupported,
        /// <summary>Signed attributes without a message digest (RFC 5652, 5.3: required): a block not made to the norm, no proof of a change.</summary>
        NoMessageDigest,

        /// <summary>RSASSA-PSS with parameters not checked here (another salt length or mask hash): not checked, no proof of a change.</summary>
        UnsupportedParameters,

        /// <summary>CMSAlgorithmProtection (RFC 6211) missing in form, repeated or naming other algorithms than the SignerInfo: broken.</summary>
        AlgorithmMismatch,
    }

    /// <summary>
    /// Checks the signature value of a detached CMS block with one signer against the digest of the signed bytes, streamed by the
    /// caller: with signed attributes, their message digest must be that digest and the signature must cover them (RFC 5652, 5.4 and
    /// 5.6); without, the signature covers the digest itself. Only the signature value is checked, as with
    /// <see cref="SignedCms.CheckSignature(bool)"/> and <c>verifySignatureOnly</c>; the certificate is judged elsewhere.
    /// </summary>
    /// <param name="der">The CMS block.</param>
    /// <param name="signer">The signer's certificate as found in the block.</param>
    /// <param name="digestOf">The digest of the signed bytes with the given algorithm.</param>
    /// <returns>The result.</returns>
    internal static DigestCheck CheckDigest(byte[] der, X509Certificate2 signer, Func<HashAlgorithmName, byte[]> digestOf)
    {
        try
        {
            var info = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            if (info.ReadObjectIdentifier() != SignedDataOid) return DigestCheck.Unsupported;
            var data = info.ReadSequence(Context0).ReadSequence();
            data.ReadInteger();
            data.ReadSetOf();
            data.ReadSequence();
            if (data.HasData && data.PeekTag().HasSameClassAndValue(Context0)) data.ReadEncodedValue();   // certificates
            if (data.HasData && data.PeekTag().HasSameClassAndValue(Context1)) data.ReadEncodedValue();   // revocation lists
            var signers = data.ReadSetOf();
            var signerInfo = signers.ReadSequence();
            if (signers.HasData) return DigestCheck.Unsupported;
            signerInfo.ReadInteger();
            signerInfo.ReadEncodedValue();   // sid: the caller found the certificate through it
            var signerInfoDigestSequence = signerInfo.ReadEncodedValue();
            var signerInfoDigest = new AsnReader(signerInfoDigestSequence, AsnEncodingRules.BER).ReadSequence();
            if (HashOf(signerInfoDigest.ReadObjectIdentifier()) is not { } digestHash) return DigestCheck.Unsupported;
            var attributes = signerInfo.PeekTag().HasSameClassAndValue(Context0) ? signerInfo.ReadEncodedValue() : (ReadOnlyMemory<byte>?)null;
            var algorithmSequence = signerInfo.ReadSequence();
            var algorithm = algorithmSequence.ReadObjectIdentifier();
            var algorithmParameters = algorithmSequence.HasData ? algorithmSequence.ReadEncodedValue() : (ReadOnlyMemory<byte>?)null;
            var signature = signerInfo.ReadOctetString();
            // RSASSA-PSS: checked here with the parameters the platform verifies; others are not checked (No. 537)
            HashAlgorithmName? pss = null;
            if (algorithm == RsaPssOid && (pss = PssHash(algorithmParameters, digestHash)) is null) return DigestCheck.UnsupportedParameters;

            var digest = digestOf(digestHash);
            if (attributes is not { } signedAttributes)
                return pss is { } h ? VerifyPss(signer, h, digest, signature, isDigest: true) : Verify(signer, algorithm, digestHash, digest, signature, isDigest: true);
            if (MessageDigest(signedAttributes) is not { } stated) return DigestCheck.NoMessageDigest;
            if (!stated.AsSpan().SequenceEqual(digest)) return DigestCheck.Invalid;
            // CMSAlgorithmProtection (RFC 6211): where present, exactly one with one value, naming the digest and the signature algorithm of
            // the SignerInfo — a substituted algorithm is a broken signature (docs/FEHLERGEDAECHTNIS.md, No. 1113)
            if (!AlgorithmProtectionFits(signedAttributes, new AsnReader(signerInfoDigestSequence, AsnEncodingRules.BER).ReadSequence(), algorithm, algorithmParameters))
                return DigestCheck.AlgorithmMismatch;
            var covered = signedAttributes.ToArray();
            covered[0] = 0x31;   // the signature covers the attributes as a SET OF (RFC 5652, 5.4)
            var verified = pss is { } ph ? VerifyPss(signer, ph, covered, signature, isDigest: false)
                : Verify(signer, algorithm, digestHash, covered, signature, isDigest: false);
            return verified == DigestCheck.Invalid ? DigestCheck.ValueMismatch : verified;
        }
        catch (AsnContentException)
        {
            return DigestCheck.Unsupported;
        }
    }

    /// <summary>
    /// Whether the CMSAlgorithmProtection attribute fits the SignerInfo (RFC 6211, 2 and 3): absent, or exactly one attribute with exactly one
    /// value; its digest algorithm the SignerInfo's (parameters absent and NULL alike, 3), its signature algorithm [1] the SignerInfo's, no MAC
    /// algorithm [2] in a signature.
    /// </summary>
    private static bool AlgorithmProtectionFits(ReadOnlyMemory<byte> attributes, AsnReader signerInfoDigest, string signatureOid, ReadOnlyMemory<byte>? signatureParameters)
    {
        var digestOid = signerInfoDigest.ReadObjectIdentifier();
        var digestParameters = signerInfoDigest.HasData ? signerInfoDigest.ReadEncodedValue() : (ReadOnlyMemory<byte>?)null;
        var set = new AsnReader(attributes, AsnEncodingRules.BER).ReadSetOf(Context0);
        var found = 0;
        while (set.HasData)
        {
            var attribute = set.ReadSequence();
            if (attribute.ReadObjectIdentifier() != AlgorithmProtectionOid) continue;
            if (++found > 1) return false;
            var values = attribute.ReadSetOf();
            if (!values.HasData) return false;
            var protection = values.ReadSequence();
            if (values.HasData) return false;
            var digest = protection.ReadSequence();
            if (digest.ReadObjectIdentifier() != digestOid || !SameParameters(digest.HasData ? digest.ReadEncodedValue() : (ReadOnlyMemory<byte>?)null, digestParameters)) return false;
            if (!protection.HasData || !protection.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true))) return false;
            var signature = protection.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true));
            if (signature.ReadObjectIdentifier() != signatureOid) return false;
            var parameters = signature.HasData ? signature.ReadEncodedValue() : (ReadOnlyMemory<byte>?)null;
            if (signature.HasData || !SameParameters(parameters, signatureParameters)) return false;
            if (protection.HasData) return false;   // a MAC algorithm [2] does not belong to a signature
        }
        return true;
    }

    /// <summary>Two algorithm parameters alike: the same bytes, or one absent and the other NULL (RFC 6211, 3).</summary>
    private static bool SameParameters(ReadOnlyMemory<byte>? a, ReadOnlyMemory<byte>? b)
    {
        static bool AbsentOrNull(ReadOnlyMemory<byte>? p) => p is null || p.Value.IsEmpty || p.Value.Span.SequenceEqual(new byte[] { 0x05, 0x00 });
        if (AbsentOrNull(a) && AbsentOrNull(b)) return true;
        return a is { } x && b is { } y && x.Span.SequenceEqual(y.Span);
    }

    /// <summary>The one value of the message-digest attribute; null if it is missing, repeated or has several values.</summary>
    private static byte[]? MessageDigest(ReadOnlyMemory<byte> attributes)
    {
        var set = new AsnReader(attributes, AsnEncodingRules.BER).ReadSetOf(Context0);
        byte[]? found = null;
        while (set.HasData)
        {
            var attribute = set.ReadSequence();
            if (attribute.ReadObjectIdentifier() != MessageDigestOid) continue;
            if (found is not null) return null;
            var values = attribute.ReadSetOf();
            found = values.ReadOctetString();
            if (values.HasData) return null;
        }
        return found;
    }

    private static DigestCheck VerifyPss(X509Certificate2 signer, HashAlgorithmName hash, byte[] data, byte[] signature, bool isDigest)
    {
        using var rsa = signer.GetRSAPublicKey();
        if (rsa is null) return DigestCheck.Unsupported;
        var valid = isDigest ? rsa.VerifyHash(data, signature, hash, RSASignaturePadding.Pss) : rsa.VerifyData(data, signature, hash, RSASignaturePadding.Pss);
        return valid ? DigestCheck.Valid : DigestCheck.Invalid;
    }

    private static DigestCheck Verify(X509Certificate2 signer, string algorithm, HashAlgorithmName digestHash, byte[] data, byte[] signature, bool isDigest)
    {
        HashAlgorithmName? rsaHash = algorithm switch
        {
            RsaOid => digestHash,
            "1.2.840.113549.1.1.5" => HashAlgorithmName.SHA1,
            "1.2.840.113549.1.1.11" => HashAlgorithmName.SHA256,
            "1.2.840.113549.1.1.12" => HashAlgorithmName.SHA384,
            "1.2.840.113549.1.1.13" => HashAlgorithmName.SHA512,
            // RSA PKCS #1 v1.5 with SHA3-256/384/512 (NIST register 2.16.840.1.101.3.4.3.14–16; No. 540)
            "2.16.840.1.101.3.4.3.14" when SHA3_256.IsSupported => HashAlgorithmName.SHA3_256,
            "2.16.840.1.101.3.4.3.15" when SHA3_384.IsSupported => HashAlgorithmName.SHA3_384,
            "2.16.840.1.101.3.4.3.16" when SHA3_512.IsSupported => HashAlgorithmName.SHA3_512,
            _ => null,
        };
        HashAlgorithmName? ecdsaHash = algorithm switch
        {
            EcPublicKeyOid => digestHash,
            "1.2.840.10045.4.1" => HashAlgorithmName.SHA1,
            EcdsaSha256Oid => HashAlgorithmName.SHA256,
            "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
            "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
            // ECDSA with SHA3-256/384/512 (2.16.840.1.101.3.4.3.10–12)
            "2.16.840.1.101.3.4.3.10" when SHA3_256.IsSupported => HashAlgorithmName.SHA3_256,
            "2.16.840.1.101.3.4.3.11" when SHA3_384.IsSupported => HashAlgorithmName.SHA3_384,
            "2.16.840.1.101.3.4.3.12" when SHA3_512.IsSupported => HashAlgorithmName.SHA3_512,
            _ => null,
        };
        // a digest signed directly must have been made with the same algorithm
        if (isDigest && (rsaHash ?? ecdsaHash) != digestHash) return DigestCheck.Unsupported;
        if (rsaHash is { } rh)
        {
            using var rsa = signer.GetRSAPublicKey();
            if (rsa is null) return DigestCheck.Unsupported;
            var valid = isDigest ? rsa.VerifyHash(data, signature, rh, RSASignaturePadding.Pkcs1) : rsa.VerifyData(data, signature, rh, RSASignaturePadding.Pkcs1);
            return valid ? DigestCheck.Valid : DigestCheck.Invalid;
        }
        if (ecdsaHash is { } eh)
        {
            using var ecdsa = signer.GetECDsaPublicKey();
            if (ecdsa is null) return DigestCheck.Unsupported;
            var valid = isDigest ? ecdsa.VerifyHash(data, signature, DSASignatureFormat.Rfc3279DerSequence)
                : ecdsa.VerifyData(data, signature, eh, DSASignatureFormat.Rfc3279DerSequence);
            return valid ? DigestCheck.Valid : DigestCheck.Invalid;
        }
        return DigestCheck.Unsupported;
    }

    /// <summary>Digest algorithms streamed over the signed bytes; null for others (they are checked with the content).</summary>
    /// <summary>What a detached CMS block must not have (RFC 5652, 5.2 and 5.3; ETSI EN 319 142-1, 6.3).</summary>
    internal enum Flaw
    {
        /// <summary>None of the flaws below.</summary>
        None,

        /// <summary>The block carries content of its own (eContent): it signs that, not the bytes of the file.</summary>
        CarriesContent,

        /// <summary>A digest algorithm named by a signature algorithm (sha256WithRSAEncryption and the like): not a digest algorithm.</summary>
        SignatureAsDigest,
    }

    /// <summary>
    /// A flaw of a block for the detached methods (adbe.pkcs7.detached, ETSI.CAdES.detached) that the platform's check passes over: it takes
    /// content carried in the block in place of the bytes of the file, and accepts a signature algorithm where a digest algorithm belongs
    /// (docs/FEHLERGEDAECHTNIS.md, No. 344). Unreadable blocks give <see cref="Flaw.None"/> — the check of the signature finds them.
    /// </summary>
    /// <param name="der">The block.</param>
    internal static Flaw FlawOf(byte[] der)
    {
        try
        {
            var info = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            if (info.ReadObjectIdentifier() != SignedDataOid) return Flaw.None;
            var data = info.ReadSequence(Context0).ReadSequence();
            data.ReadInteger();
            var digests = data.ReadSetOf();
            while (digests.HasData)
                if (SignatureAlgorithm(digests.ReadSequence().ReadObjectIdentifier())) return Flaw.SignatureAsDigest;
            var content = data.ReadSequence();
            content.ReadObjectIdentifier();
            if (content.HasData) return Flaw.CarriesContent;
            if (data.HasData && data.PeekTag().HasSameClassAndValue(Context0)) data.ReadEncodedValue();   // certificates
            if (data.HasData && data.PeekTag().HasSameClassAndValue(Context1)) data.ReadEncodedValue();   // revocation lists
            var signers = data.ReadSetOf();
            while (signers.HasData)
            {
                var signer = signers.ReadSequence();
                signer.ReadInteger();
                signer.ReadEncodedValue();
                if (SignatureAlgorithm(signer.ReadSequence().ReadObjectIdentifier())) return Flaw.SignatureAsDigest;
            }
            return Flaw.None;
        }
        catch (AsnContentException)
        {
            return Flaw.None;
        }
    }

    /// <summary>
    /// What the signers of a block declare that the platform's check passes over, for every method (also adbe.pkcs7.sha1, whose block carries
    /// content by design): a signature algorithm where a digest algorithm belongs (RFC 5652, 5.3), and signed attributes not in DER order
    /// (RFC 5652, 5.3: the signed attributes MUST be DER encoded; X.690, 11.6: the elements of a SET OF sorted) — the platform checks the bytes
    /// as they stand, a check by the norm re-encodes them and finds another value (docs/FEHLERGEDAECHTNIS.md, No. 476). Unreadable blocks give
    /// (false, false): the check of the signature finds them.
    /// </summary>
    /// <param name="der">The block.</param>
    internal static (bool SignatureAsDigest, bool UnsortedAttributes) SignerFlaws(byte[] der)
    {
        bool asDigest = false, unsorted = false;
        try
        {
            var info = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            if (info.ReadObjectIdentifier() != SignedDataOid) return (false, false);
            var data = info.ReadSequence(Context0).ReadSequence();
            data.ReadInteger();
            var digests = data.ReadSetOf();
            while (digests.HasData) asDigest |= SignatureAlgorithm(digests.ReadSequence().ReadObjectIdentifier());
            data.ReadEncodedValue();   // encapContentInfo
            if (data.HasData && data.PeekTag().HasSameClassAndValue(Context0)) data.ReadEncodedValue();   // certificates
            if (data.HasData && data.PeekTag().HasSameClassAndValue(Context1)) data.ReadEncodedValue();   // revocation lists
            var signers = data.ReadSetOf();
            for (var i = 0; signers.HasData && i < 16; i++)
            {
                var signer = signers.ReadSequence();
                signer.ReadInteger();
                signer.ReadEncodedValue();   // sid
                asDigest |= SignatureAlgorithm(signer.ReadSequence().ReadObjectIdentifier());
                if (!signer.HasData || !signer.PeekTag().HasSameClassAndValue(Context0)) continue;
                var attributes = signer.ReadSetOf(true, Context0);
                byte[]? previous = null;
                for (var j = 0; attributes.HasData && j < 256; j++)
                {
                    var current = attributes.ReadEncodedValue().ToArray();
                    if (previous is not null && DerSetOrder(previous, current) > 0) unsorted = true;
                    previous = current;
                }
            }
        }
        catch (AsnContentException)
        {
            // what was read counts
        }
        return (asDigest, unsorted);
    }

    /// <summary>
    /// The revocation values a block carries itself (docs/FEHLERGEDAECHTNIS.md, No. 481): the CRLs and OCSP responses of SignedData.crls
    /// (RFC 5652, 10.2.1; OCSP as id-ri-ocsp-response, RFC 5940) and those of the signed attribute adbe-revocationInfoArchival
    /// (1.2.840.113583.1.1.8, ISO 32000-1, 12.8.3.3.2: SEQUENCE { crl [0] EXPLICIT SEQUENCE OF CRL, ocsp [1] EXPLICIT SEQUENCE OF
    /// OCSPResponse, … }) — where signing programs such as the widespread one put them instead of the security store. Bounded; an unreadable
    /// part gives what was read before it.
    /// </summary>
    /// <param name="der">The block.</param>
    internal static (List<byte[]> Ocsps, List<byte[]> Crls) RevocationValues(byte[] der)
    {
        List<byte[]> ocsps = [], crls = [];
        try
        {
            var info = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            if (info.ReadObjectIdentifier() != SignedDataOid) return (ocsps, crls);
            var data = info.ReadSequence(Context0).ReadSequence();
            data.ReadInteger();
            data.ReadSetOf(true);       // digest algorithms
            data.ReadEncodedValue();    // encapContentInfo
            if (data.HasData && data.PeekTag().HasSameClassAndValue(Context0)) data.ReadEncodedValue();   // certificates
            if (data.HasData && data.PeekTag().HasSameClassAndValue(Context1))
            {
                var list = data.ReadSetOf(true, new Asn1Tag(TagClass.ContextSpecific, 1, true));
                for (var i = 0; list.HasData && i < MaxRevocationValues; i++)
                {
                    var tag = list.PeekTag();
                    if (tag.HasSameClassAndValue(Asn1Tag.Sequence)) crls.Add(list.ReadEncodedValue().ToArray());
                    else if (tag.HasSameClassAndValue(Context1))
                    {
                        var other = list.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true));
                        if (other.ReadObjectIdentifier() == "1.3.6.1.5.5.7.16.2") ocsps.Add(other.ReadEncodedValue().ToArray());
                    }
                    else list.ReadEncodedValue();
                }
            }
            var signers = data.ReadSetOf(true);
            if (!signers.HasData) return (ocsps, crls);
            var signer = signers.ReadSequence();
            signer.ReadInteger();
            signer.ReadEncodedValue();   // sid
            signer.ReadSequence();       // digest algorithm
            if (!signer.HasData || !signer.PeekTag().HasSameClassAndValue(Context0)) return (ocsps, crls);
            var attributes = signer.ReadSetOf(true, Context0);
            for (var i = 0; attributes.HasData && i < 256; i++)
            {
                var attribute = attributes.ReadSequence();
                if (attribute.ReadObjectIdentifier() != "1.2.840.113583.1.1.8") continue;
                var archival = attribute.ReadSetOf(true).ReadSequence();
                while (archival.HasData)
                {
                    var tag = archival.PeekTag();
                    var into = tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 0 ? crls
                        : tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 1 ? ocsps : null;
                    if (into is null)
                    {
                        archival.ReadEncodedValue();
                        continue;
                    }
                    var values = archival.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, tag.TagValue, true)).ReadSequence();
                    for (var j = 0; values.HasData && into.Count < MaxRevocationValues; j++) into.Add(values.ReadEncodedValue().ToArray());
                }
                break;
            }
        }
        catch (AsnContentException)
        {
            // what was read counts
        }
        return (ocsps, crls);
    }

    /// <summary>At most this many revocation values of each kind are read from a block (prepared files).</summary>
    private const int MaxRevocationValues = 100;

    /// <summary>The order of the elements of a DER SET OF (X.690, 11.6): as octet strings, the shorter one padded with zeros at its end.</summary>
    private static int DerSetOrder(byte[] a, byte[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x - y;
        }
        return 0;
    }

    /// <summary>The content a block carries (eContent); null if it carries none or cannot be read.</summary>
    /// <param name="der">The block.</param>
    internal static byte[]? Carried(byte[] der)
    {
        try
        {
            var info = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            if (info.ReadObjectIdentifier() != SignedDataOid) return null;
            var data = info.ReadSequence(Context0).ReadSequence();
            data.ReadInteger();
            data.ReadSetOf();
            var content = data.ReadSequence();
            content.ReadObjectIdentifier();
            return content.HasData ? content.ReadSequence(Context0).ReadOctetString() : null;
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    /// <summary>Signature algorithms the platform can verify (RSA PKCS #1 and PSS, ECDSA, DSA); with SHA-3 only where the system offers it.</summary>
    private static readonly HashSet<string> ComputableSignatures =
    [
        "1.2.840.113549.1.1.1", "1.2.840.113549.1.1.4", "1.2.840.113549.1.1.5", "1.2.840.113549.1.1.10", "1.2.840.113549.1.1.11",
        "1.2.840.113549.1.1.12", "1.2.840.113549.1.1.13", "1.2.840.113549.1.1.14",
        "1.2.840.10045.2.1", "1.2.840.10045.4.1", "1.2.840.10045.4.3.1", "1.2.840.10045.4.3.2", "1.2.840.10045.4.3.3", "1.2.840.10045.4.3.4",
        "1.2.840.10040.4.1", "1.2.840.10040.4.3", "2.16.840.1.101.3.4.3.1", "2.16.840.1.101.3.4.3.2",
    ];

    /// <summary>Digest algorithms the platform can compute: SHA-1, SHA-2 and MD5 (weak, <c>Weakness</c> says so); SHA-3 only where offered.</summary>
    private static readonly HashSet<string> ComputableDigests =
    [
        "1.3.14.3.2.26", "2.16.840.1.101.3.4.2.4", Sha256, "2.16.840.1.101.3.4.2.2", "2.16.840.1.101.3.4.2.3", "1.2.840.113549.2.5",
    ];

    /// <summary>
    /// The method of a signer the platform cannot verify (Ed25519, Ed448 with SHAKE256 — fixed or variable length —, SHA-3 without the system
    /// offering it, MD2 …), in readable form; null if it can. Checked before the platform's own check: that throws for an unknown method,
    /// and the throw read as „the document was changed“ — a false charge against the signer (docs/ABLAUFPLAN.md, 2.4; ISO/TS 32002, 5.1.3
    /// allows to leave such signatures unchecked).
    /// </summary>
    internal static string? NotComputable(SignerInfo signer)
    {
        var signature = signer.SignatureAlgorithm.Value ?? "";
        var digest = signer.DigestAlgorithm.Value ?? "";
        var sha3 = SHA3_256.IsSupported;
        var signatureOk = ComputableSignatures.Contains(signature)
                          || sha3 && signature.StartsWith("2.16.840.1.101.3.4.3.", StringComparison.Ordinal)
                          && int.TryParse(signature.AsSpan("2.16.840.1.101.3.4.3.".Length), out var n) && n is >= 9 and <= 16;
        // a signature algorithm named as the digest (not RFC 5652, files exist): the platform computes it from the hash inside — judged as
        // before, not „not checked“ (only judgements resting on the platform's failure change here)
        var digestOk = ComputableDigests.Contains(digest) || ComputableSignatures.Contains(digest) && digest != "1.2.840.10045.2.1"
            || sha3 && digest is "2.16.840.1.101.3.4.2.7" or "2.16.840.1.101.3.4.2.8" or "2.16.840.1.101.3.4.2.9" or "2.16.840.1.101.3.4.2.10";
        if (signatureOk && digestOk) return null;
        static string Name(string oid) => oid switch
        {
            "1.3.101.112" => "Ed25519",
            "1.3.101.113" => "Ed448",
            "2.16.840.1.101.3.4.2.12" => "SHAKE256",
            "2.16.840.1.101.3.4.2.18" => Tr.T("SHAKE256 mit variabler Länge"),
            "1.2.840.113549.2.2" => "MD2",
            "2.16.840.1.101.3.4.2.7" => "SHA3-224",
            "2.16.840.1.101.3.4.2.8" => "SHA3-256",
            "2.16.840.1.101.3.4.2.9" => "SHA3-384",
            "2.16.840.1.101.3.4.2.10" => "SHA3-512",
            _ => oid,
        };
        return signatureOk ? Name(digest) : digestOk ? Name(signature) : $"{Name(signature)}/{Name(digest)}";
    }

    /// <summary>An identifier of a signature algorithm (RSA PKCS #1, ECDSA, DSA), not of a digest algorithm.</summary>
    private static bool SignatureAlgorithm(string oid) =>
        oid.StartsWith("1.2.840.113549.1.1.", StringComparison.Ordinal) || oid.StartsWith("1.2.840.10045.4.", StringComparison.Ordinal) ||
        oid.StartsWith("1.2.840.10040.4.", StringComparison.Ordinal) || oid.StartsWith("2.16.840.1.101.3.4.3.", StringComparison.Ordinal);

    private static HashAlgorithmName? HashOf(string oid) => oid switch
    {
        "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
        Sha256 => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        // SHA-3 (ISO/TS 32001, 5.1.4), only where the system offers it — otherwise „not checked“ with the reason (No. 540)
        "2.16.840.1.101.3.4.2.8" when SHA3_256.IsSupported => HashAlgorithmName.SHA3_256,
        "2.16.840.1.101.3.4.2.9" when SHA3_384.IsSupported => HashAlgorithmName.SHA3_384,
        "2.16.840.1.101.3.4.2.10" when SHA3_512.IsSupported => HashAlgorithmName.SHA3_512,
        _ => null,
    };

    /// <summary>
    /// The intermediate certificates between the certificate and its root, built from the given certificates and the system's stores,
    /// never from the network; the root itself is left out (a reader has to trust it on its own anyway).
    /// </summary>
    private static List<X509Certificate2> ChainOf(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> given)
    {
        var links = new List<X509Certificate2>();
        if (certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)) return links;
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority | X509VerificationFlags.IgnoreNotTimeValid;
        foreach (var other in given) chain.ChainPolicy.ExtraStore.Add(other);
        try
        {
            chain.Build(certificate);
        }
        catch (CryptographicException)
        {
            return links;   // no chain: the certificate alone
        }
        for (var i = 1; i < chain.ChainElements.Count; i++)
        {
            var element = chain.ChainElements[i].Certificate;
            if (element.SubjectName.RawData.AsSpan().SequenceEqual(element.IssuerName.RawData)) continue;   // the root
            links.Add(element);
        }
        return links;
    }

    private const string SigningCertificateV1 = "1.2.840.113549.1.9.16.2.12";

    /// <summary>How the signed attribute signing-certificate (v1 or v2) binds the signer's certificate.</summary>
    internal enum CertificateBinding
    {
        /// <summary>It names exactly this certificate (hash, and issuer and serial number if given).</summary>
        Matches,
        /// <summary>Neither v1 nor v2 is there.</summary>
        Missing,
        /// <summary>It names another certificate: the one in the block was swapped (the certificates of a block are not signed).</summary>
        Mismatch,
        /// <summary>Its hash is one this system cannot compute: the binding cannot be checked.</summary>
        UnknownHash,
        /// <summary>Twice, with several values, or not readable.</summary>
        Malformed,

        /// <summary>
        /// The hash names exactly this certificate, the issuer and serial number given beside it do not: the hash binds it all the same — a
        /// flaw of the signing program, said (the reference warns only; docs/FEHLERGEDAECHTNIS.md, No. 483).
        /// </summary>
        IssuerSerialDiffers,
    }

    /// <summary>
    /// Checks the signed attributes signing-certificate-v2 (RFC 5035, section 3) and signing-certificate (RFC 2634, 5.4) against the signer's
    /// certificate (ISO 32000-2:2020, 12.8.3.4.5 a): the first entry of the list names the certificate the signature is to be verified with —
    /// its hash over the whole certificate and, if given, issuer (directoryName, compared by bytes, else by the decoded name) and serial
    /// number (leading zeros aside). Both present: both must fit; one fitting and one of a hash this system lacks is enough.
    /// </summary>
    internal static CertificateBinding BindingOf(SignerInfo signer, X509Certificate2 certificate)
    {
        var results = new List<CertificateBinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in signer.SignedAttributes)
        {
            if (attribute.Oid?.Value is not ({ } oid and (SigningCertificateV2 or SigningCertificateV1))) continue;
            if (!seen.Add(oid) || attribute.Values.Count != 1) return CertificateBinding.Malformed;
            results.Add(BindingOf(attribute.Values[0].RawData, oid == SigningCertificateV2, certificate));
        }
        if (results.Count == 0) return CertificateBinding.Missing;
        if (results.Contains(CertificateBinding.Malformed)) return CertificateBinding.Malformed;
        if (results.Contains(CertificateBinding.Mismatch)) return CertificateBinding.Mismatch;
        if (results.Contains(CertificateBinding.IssuerSerialDiffers)) return CertificateBinding.IssuerSerialDiffers;
        return results.Contains(CertificateBinding.Matches) ? CertificateBinding.Matches : CertificateBinding.UnknownHash;
    }

    private static CertificateBinding BindingOf(byte[] value, bool v2, X509Certificate2 certificate)
    {
        try
        {
            var reader = new AsnReader(value, AsnEncodingRules.BER);
            var outer = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            var certs = outer.ReadSequence();
            var first = certs.ReadSequence();
            var hashOid = v2 ? Sha256 : "1.3.14.3.2.26";
            if (v2 && first.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence)) hashOid = first.ReadSequence().ReadObjectIdentifier();
            var hash = first.ReadOctetString();
            HashAlgorithmName? name = hashOid switch
            {
                "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
                Sha256 => HashAlgorithmName.SHA256,
                "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
                "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
                "2.16.840.1.101.3.4.2.8" when SHA3_256.IsSupported => HashAlgorithmName.SHA3_256,
                "2.16.840.1.101.3.4.2.9" when SHA3_384.IsSupported => HashAlgorithmName.SHA3_384,
                "2.16.840.1.101.3.4.2.10" when SHA3_512.IsSupported => HashAlgorithmName.SHA3_512,
                _ => null,
            };
            if (name is not { } algorithm) return CertificateBinding.UnknownHash;
            if (!CryptographicOperations.FixedTimeEquals(hash, CryptographicOperations.HashData(algorithm, certificate.RawData))) return CertificateBinding.Mismatch;
            if (!first.HasData) return CertificateBinding.Matches;
            var issuerSerial = first.ReadSequence();
            var names = issuerSerial.ReadSequence();
            bool anyDirectory = false, issuerFits = false;
            while (names.HasData)
            {
                var tag = names.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 4)
                {
                    anyDirectory = true;
                    var issuer = names.ReadSequence(tag).ReadEncodedValue().ToArray();
                    issuerFits |= issuer.AsSpan().SequenceEqual(certificate.IssuerName.RawData)
                                  || string.Equals(new X500DistinguishedName(issuer).Name, certificate.IssuerName.Name, StringComparison.Ordinal);
                }
                else names.ReadEncodedValue();
            }
            var serial = issuerSerial.ReadIntegerBytes().Span.TrimStart((byte)0);
            var own = certificate.SerialNumberBytes.Span.TrimStart((byte)0);
            return anyDirectory && !issuerFits || !serial.SequenceEqual(own) ? CertificateBinding.IssuerSerialDiffers : CertificateBinding.Matches;
        }
        catch (Exception e) when (e is AsnContentException or CryptographicException)
        {
            return CertificateBinding.Malformed;
        }
    }

    /// <summary>
    /// SigningCertificateV2 ::= SEQUENCE { certs SEQUENCE OF ESSCertIDv2 }, ESSCertIDv2 ::= SEQUENCE { hashAlgorithm DEFAULT sha256,
    /// certHash OCTET STRING, issuerSerial IssuerSerial } (RFC 5035, section 3): binds the signature to exactly this certificate.
    /// </summary>
    internal static byte[] SigningCertificateV2Value(X509Certificate2 certificate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            writer.WriteOctetString(SHA256.HashData(certificate.RawData));
            using (writer.PushSequence())
            {
                using (writer.PushSequence())   // GeneralNames with the issuer as directoryName [4]
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true)))
                    writer.WriteEncodedValue(certificate.IssuerName.RawData);
                writer.WriteInteger(certificate.SerialNumberBytes.Span);
            }
        }
        return writer.Encode();
    }
}
