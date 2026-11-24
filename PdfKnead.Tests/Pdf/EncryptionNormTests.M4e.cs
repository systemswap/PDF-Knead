using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ISO/TS 32004 (MAC per revision) and RFC 6211 (algorithm protection) from the reader's side: tokens built in the test with the building
/// blocks of the runtime (ASN.1 writer, HMAC-SHA-256, AES, HKDF, the hash functions) and a file key computed in the test from /UE
/// (ISO 32000-2, algorithms 2.A and 2.B) — one field changed at a time, the HMAC computed anew over the changed attributes, so that only the
/// condition under test can reject the token (Konformitätsabgleich M4, packages j-ts32004 and l-rfc6211).
/// </summary>
public sealed partial class EncryptionNormTests
{
    // ------------------------------------------------------------ object identifiers (RFC 5652, RFC 6211, ISO/TS 32004, NIST register)

    private const string M4eAuthData = "1.2.840.113549.1.9.16.1.2", M4eSignedData = "1.2.840.113549.1.7.2", M4eIdData = "1.2.840.113549.1.7.1",
        M4eIntegrityInfo = "1.0.32004.1.0", M4eWrapKdf = "1.0.32004.1.1", M4eAes256Wrap = "2.16.840.1.101.3.4.1.45",
        M4eHmacSha256 = "1.2.840.113549.2.9", M4eHmacSha384 = "1.2.840.113549.2.10", M4eSha256 = "2.16.840.1.101.3.4.2.1",
        M4eSha384 = "2.16.840.1.101.3.4.2.2", M4eSha512 = "2.16.840.1.101.3.4.2.3", M4eSha3_256 = "2.16.840.1.101.3.4.2.8",
        M4eSha1 = "1.3.14.3.2.26", M4eContentType = "1.2.840.113549.1.9.3", M4eMessageDigest = "1.2.840.113549.1.9.4",
        M4eSigningTime = "1.2.840.113549.1.9.5", M4eAlgorithmProtection = "1.2.840.113549.1.9.52";

    private const string M4ePassword = "geheim";

    // ------------------------------------------------------------ independent building blocks

    /// <summary>ISO 32000-2, algorithm 2.B, written anew from the steps: SHA-256 start, AES-128-CBC over 64 repetitions, hash chosen by the
    /// first 16 bytes modulo 3, at least 64 rounds, then until the last byte is not above the round count minus 32.</summary>
    private static byte[] M4eHash2B(byte[] password, byte[] salt, byte[] udata)
    {
        var k = SHA256.HashData([.. password, .. salt, .. udata]);
        using var aes = Aes.Create();
        byte[] e = [];
        var rounds = 0;
        while (rounds < 64 || e[^1] > rounds - 32)
        {
            byte[] unit = [.. password, .. k, .. udata];
            var k1 = new byte[unit.Length * 64];
            for (var i = 0; i < 64; i++) unit.CopyTo(k1, i * unit.Length);
            aes.Key = k[..16];
            e = aes.EncryptCbc(k1, k[16..32], PaddingMode.None);
            var mod = 0;
            for (var i = 0; i < 16; i++) mod = (mod * 256 + e[i]) % 3;
            k = mod switch { 0 => SHA256.HashData(e), 1 => SHA384.HashData(e), _ => SHA512.HashData(e) };
            rounds++;
        }
        return k[..32];
    }

    /// <summary>The file key of a revision 6 file from the user password (algorithm 2.A, steps for the user): /U checked with its validation
    /// salt, the intermediate key from the key salt, /UE decrypted with AES-256 (CBC, zero IV, no padding).</summary>
    private static byte[] M4eFileKey(PdfDictionary encrypt, string password = M4ePassword)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        var u = encrypt.GetString(PdfName.Get("U"))!.Bytes;
        var ue = encrypt.GetString(PdfName.Get("UE"))!.Bytes;
        Assert.Equal(u[..32], M4eHash2B(pw, u[32..40], []));
        using var aes = Aes.Create();
        aes.Key = M4eHash2B(pw, u[40..48], []);
        return aes.DecryptCbc(ue, new byte[16], PaddingMode.None);
    }

    /// <summary>pdfMacWrapKdf (ISO/TS 32004, 6.4) with the HKDF of the runtime.</summary>
    private static byte[] M4eKek(byte[] fileKey, byte[] salt) => HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey, 32, salt, "PDFMAC"u8.ToArray());

    /// <summary>AES key wrap (RFC 3394, 2.2.1), written anew for the tests.</summary>
    private static byte[] M4eWrap(byte[] kek, byte[] key)
    {
        var n = key.Length / 8;
        var a = new byte[] { 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6 };
        var r = key.ToArray();
        using var aes = Aes.Create();
        aes.Key = kek;
        for (var j = 0; j < 6; j++)
            for (var i = 0; i < n; i++)
            {
                var b = aes.EncryptEcb([.. a, .. r.AsSpan(i * 8, 8)], PaddingMode.None);
                BinaryPrimitives.WriteUInt64BigEndian(a, BinaryPrimitives.ReadUInt64BigEndian(b) ^ (ulong)(n * j + i + 1));
                b.AsSpan(8, 8).CopyTo(r.AsSpan(i * 8));
            }
        return [.. a, .. r];
    }

    /// <summary>AES key unwrap (RFC 3394, 2.2.2), written anew; the integrity value is returned with the key.</summary>
    private static (byte[] Iv, byte[] Key) M4eUnwrap(byte[] kek, byte[] wrapped)
    {
        var n = wrapped.Length / 8 - 1;
        var a = wrapped[..8];
        var r = wrapped[8..];
        using var aes = Aes.Create();
        aes.Key = kek;
        for (var j = 5; j >= 0; j--)
            for (var i = n - 1; i >= 0; i--)
            {
                var x = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(x, BinaryPrimitives.ReadUInt64BigEndian(a) ^ (ulong)(n * j + i + 1));
                var b = aes.DecryptEcb([.. x, .. r.AsSpan(i * 8, 8)], PaddingMode.None);
                a = b[..8];
                b.AsSpan(8, 8).CopyTo(r.AsSpan(i * 8));
            }
        return (a, r);
    }

    // ------------------------------------------------------------ DER pieces

    private static byte[] M4eDer(Action<AsnWriter> write)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        write(w);
        return w.Encode();
    }

    /// <summary>An AlgorithmIdentifier (a SEQUENCE, or implicitly tagged with <paramref name="tag"/>), parameters absent or as given.</summary>
    private static byte[] M4eAlg(string oid, int? tag = null, byte[]? parameters = null) => M4eDer(w =>
    {
        using (tag is { } t ? w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, t)) : w.PushSequence())
        {
            w.WriteObjectIdentifier(oid);
            if (parameters is not null) w.WriteEncodedValue(parameters);
        }
    });

    private static readonly byte[] M4eNull = [0x05, 0x00];

    private static byte[] M4eOid(string oid) => M4eDer(w => w.WriteObjectIdentifier(oid));

    private static byte[] M4eOctets(byte[] value) => M4eDer(w => w.WriteOctetString(value));

    /// <summary>An Attribute: type and the SET OF its encoded values.</summary>
    private static byte[] M4eAttr(string type, params byte[][] values) => M4eDer(w =>
    {
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(type);
            using (w.PushSetOf()) foreach (var v in values) w.WriteEncodedValue(v);
        }
    });

    /// <summary>A CMSAlgorithmProtection value (RFC 6211, 2): a SEQUENCE of the encoded fields.</summary>
    private static byte[] M4eProtection(params byte[][] fields) => M4eDer(w =>
    {
        using (w.PushSequence()) foreach (var f in fields) w.WriteEncodedValue(f);
    });

    /// <summary>The fields of the token that the tests change, one at a time; the defaults give a token as ISO/TS 32004, 6.3 asks.</summary>
    private sealed record M4eSpec
    {
        public string OuterType { get; init; } = M4eAuthData;
        public int Version { get; init; }
        /// <summary>The recipientInfos (SET OF) from the wrapped key; null: one PasswordRecipientInfo with pdfMacWrapKdf and the AES-256 wrap.</summary>
        public Func<byte[], byte[][]>? Recipients { get; init; }
        public string DigestOid { get; init; } = M4eSha256;
        public HashAlgorithmName Digest { get; init; } = HashAlgorithmName.SHA256;
        public string MacOid { get; init; } = M4eHmacSha256;
        public string EncapType { get; init; } = M4eIntegrityInfo;
        public int InfoVersion { get; init; }
        public bool SignatureDigest { get; init; }
        /// <summary>The authenticated attributes from the message digest; null: content type, message digest, algorithm protection.</summary>
        public Func<byte[], byte[][]>? Attributes { get; init; }
        public bool WithoutAuthAttrs { get; init; }
        /// <summary>Attributes of an unauthAttrs [3] after the MAC value; null: none.</summary>
        public byte[][]? UnauthAttrs { get; init; }
        /// <summary>Zero bytes after the DER value, inside the gap.</summary>
        public int Trailing { get; init; }
        /// <summary>The salt the key encryption key is derived with; null: the /KDFSalt of the file.</summary>
        public byte[]? KekSalt { get; init; }
    }

    private static byte[] M4eDefaultProtection(M4eSpec spec) =>
        M4eAttr(M4eAlgorithmProtection, M4eProtection(M4eAlg(spec.DigestOid), M4eAlg(M4eHmacSha256, tag: 2)));

    private static byte[] M4eHash(HashAlgorithmName name, byte[] data)
    {
        if (name == HashAlgorithmName.SHA3_256 && !SHA3_256.IsSupported) return new byte[32];
        using var hash = IncrementalHash.CreateHash(name);
        hash.AppendData(data);
        return hash.GetHashAndReset();
    }

    /// <summary>Builds an AuthenticatedData token (RFC 5652, 9.1) as <paramref name="spec"/> says; the MAC is HMAC-SHA-256 over the
    /// authenticated attributes as SET OF (RFC 5652, 9.2).</summary>
    private static byte[] M4eToken(M4eSpec spec, byte[] kek, byte[] macKey, byte[] dataDigest)
    {
        var eContent = M4eDer(w =>
        {
            using (w.PushSequence())
            {
                w.WriteInteger(spec.InfoVersion);
                w.WriteOctetString(dataDigest);
                if (spec.SignatureDigest) w.WriteOctetString(new byte[dataDigest.Length], new Asn1Tag(TagClass.ContextSpecific, 0));
            }
        });
        var messageDigest = M4eHash(spec.Digest, eContent);
        var attributes = spec.Attributes?.Invoke(messageDigest)
                         ?? [M4eAttr(M4eContentType, M4eOid(M4eIntegrityInfo)), M4eAttr(M4eMessageDigest, M4eOctets(messageDigest)), M4eDefaultProtection(spec)];
        var attrSet = M4eDer(w =>
        {
            using (w.PushSetOf()) foreach (var a in attributes) w.WriteEncodedValue(a);
        });
        var mac = HMACSHA256.HashData(macKey, attrSet);
        var stored = attrSet.ToArray();
        stored[0] = 0xA2;
        var wrapped = M4eWrap(kek, macKey);
        var recipients = spec.Recipients?.Invoke(wrapped) ?? [M4ePwri(wrapped)];

        var token = M4eDer(w =>
        {
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(spec.OuterType);
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                using (w.PushSequence())
                {
                    w.WriteInteger(spec.Version);
                    using (w.PushSetOf()) foreach (var r in recipients) w.WriteEncodedValue(r);
                    w.WriteEncodedValue(M4eAlg(spec.MacOid));
                    w.WriteEncodedValue(M4eAlg(spec.DigestOid, tag: 1));
                    using (w.PushSequence())
                    {
                        w.WriteObjectIdentifier(spec.EncapType);
                        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) w.WriteOctetString(eContent);
                    }
                    if (!spec.WithoutAuthAttrs) w.WriteEncodedValue(stored);
                    w.WriteOctetString(mac);
                    if (spec.UnauthAttrs is { } unauth)
                        using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 3))) foreach (var a in unauth) w.WriteEncodedValue(a);
                }
            }
        });
        return [.. token, .. new byte[spec.Trailing]];
    }

    /// <summary>A PasswordRecipientInfo [3] with pdfMacWrapKdf (no parameters) and the AES-256 key wrap (ISO/TS 32004, 6.3.3).</summary>
    private static byte[] M4ePwri(byte[] wrapped) => M4eDer(w =>
    {
        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3)))
        {
            w.WriteInteger(0);
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) w.WriteObjectIdentifier(M4eWrapKdf);
            w.WriteEncodedValue(M4eAlg(M4eAes256Wrap));
            w.WriteOctetString(wrapped);
        }
    });

    // ------------------------------------------------------------ files

    private static readonly PdfName M4eAuthCodeKey = PdfName.Get("AuthCode"), M4eKdfSaltKey = PdfName.Get("KDFSalt");

    /// <summary>
    /// An AES-256 file (revision 6, PDF 2.0, MAC required) with a standalone /AuthCode whose token <paramref name="spec"/> describes: /ByteRange
    /// from 0 to the end around the gap (or as <paramref name="range"/> writes it from L1, S and L2), the digest over those bytes, the token as
    /// hexadecimal digits in the gap — the key encryption key from the file key the test computes itself.
    /// </summary>
    /// <param name="oddDigits">The gap ends one digit early: an odd number of hexadecimal digits, /ByteRange fitting the shorter gap.</param>
    private static byte[] M4eMacFile(M4eSpec? spec = null, Func<long, long, long, string>? range = null, Action<PdfDictionary>? patchEncrypt = null,
        Action<PdfDictionary>? patchAuthCode = null, bool oddDigits = false, Pdf.TestPdf? pdf = null, string gapKey = "MAC")
    {
        spec ??= new M4eSpec();
        var length = M4eToken(spec, new byte[32], new byte[32], M4eHash(spec.Digest, [])).Length;
        PdfDictionary? encrypt = null;
        var file = (pdf ?? Pdf.TestPdf.Simple()).Build(cipher: PdfCipher.Aes256, user: M4ePassword, owner: "chef", permissions: PdfPermissions.Print,
            version: "2.0", macRequired: true, mac: TestMac.None,
            patchEncrypt: e =>
            {
                encrypt = e;
                patchEncrypt?.Invoke(e);
            },
            patchTrailer: t =>
            {
                var authCode = Pdf.TestPdf.Dict(("MACLocation", Pdf.TestPdf.N("Standalone")),
                    ("ByteRange", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(1_000_000_000), PdfInteger.Of(1_000_000_000), PdfInteger.Of(1_000_000_000)])),
                    (gapKey, new PdfString(new byte[length], true)));
                patchAuthCode?.Invoke(authCode);
                t.Set(M4eAuthCodeKey, authCode);
            });

        var text = Encoding.Latin1.GetString(file);
        var macAt = Regex.Matches(text, "/" + gapKey + @"\s*<").Last();
        var gapStart = macAt.Index + macAt.Length - 1;
        var gapEnd = text.IndexOf('>', gapStart) + 1;
        if (oddDigits)
        {
            file[gapEnd - 2] = (byte)'>';
            file[gapEnd - 1] = (byte)' ';
            gapEnd--;
        }
        var rangeAt = text.IndexOf('[', text.LastIndexOf("/ByteRange", StringComparison.Ordinal));
        var rangeEnd = text.IndexOf(']', rangeAt);
        var written = range?.Invoke(gapStart, gapEnd, file.Length - gapEnd) ?? $"[0 {gapStart} {gapEnd} {file.Length - gapEnd}";
        Encoding.ASCII.GetBytes(written.PadRight(rangeEnd - rangeAt)).CopyTo(file, rangeAt);

        byte[] digest;
        if (spec.Digest == HashAlgorithmName.SHA3_256 && !SHA3_256.IsSupported) digest = new byte[32];
        else
        {
            using var hash = IncrementalHash.CreateHash(spec.Digest);
            hash.AppendData(file, 0, gapStart);
            hash.AppendData(file, gapEnd, file.Length - gapEnd);
            digest = hash.GetHashAndReset();
        }
        var kek = M4eKek(M4eFileKey(encrypt!), spec.KekSalt ?? encrypt!.GetString(M4eKdfSaltKey)!.Bytes);
        var hex = Convert.ToHexStringLower(M4eToken(spec, kek, RandomNumberGenerator.GetBytes(32), digest));
        Encoding.ASCII.GetBytes(oddDigits ? hex[..^1] : hex).CopyTo(file, gapStart + 1);
        return file;
    }

    private static MacCheck M4eCheck(byte[] file)
    {
        using var doc = PdfDocument.Open(file, M4ePassword);
        return doc.Integrity;
    }

    private static void M4eInvalid(byte[] file, string? detail = null)
    {
        var check = M4eCheck(file);
        Assert.Equal(MacState.Invalid, check.State);
        if (detail is not null) Assert.Equal(detail, check.Detail);
    }

    private const string M4eNotTs32004 = "Der Integritätsschutz (MAC) entspricht nicht ISO/TS 32004.",
        M4eMismatch = "Die Angabe der Verfahren im MAC passt nicht zu den verwendeten.", M4eBadRange = "Die Byte-Bereiche des Integritätsschutzes sind ungültig.";

    // ------------------------------------------------------------ the token builder itself

    /// <summary>
    /// ISO/TS 32004, 6.3 and 6.4: the token as the test builds it — file key from /UE by the test's own algorithm 2.A/2.B, key encryption key
    /// by the HKDF of the runtime, own key wrap, HMAC over the attributes — is accepted as valid. All other tests in this file change one
    /// field of exactly this token.
    /// </summary>
    [Fact]
    public void Mac_TokenBuiltIndependently_Valid()
    {
        var check = M4eCheck(M4eMacFile());
        Assert.Equal(MacState.Valid, check.State);
        Assert.Null(check.Detail);
    }

    // ------------------------------------------------------------ ISO/TS 32004, 4: the extension declaration

    /// <summary>A MAC file whose catalog does not declare ISO/TS 32004 (the declaration taken out of the source), saved with a fresh MAC as 2.0.</summary>
    private static PdfDictionary M4eSavedDeclaration()
    {
        var pdf = Pdf.TestPdf.Simple();
        var source = M4eMacFile(pdf: pdf, patchEncrypt: _ => ((PdfDictionary)pdf.Get(pdf.RootNumber)).Remove(PdfName.Get("Extensions")));
        using (var doc = PdfDocument.Open(source, M4ePassword))
        {
            Assert.Null(doc.Catalog!.Get(PdfName.Get("Extensions")) as PdfDictionary);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "2.0", WriteMac = true });
            source = output.ToArray();
        }
        using var copy = PdfDocument.Open(source, M4ePassword);
        Assert.Equal(MacState.Valid, copy.Integrity.State);
        var iso = copy.Catalog!.GetDictionary(PdfName.Get("Extensions"))!.GetArray(PdfName.Get("ISO_"))!;
        return Enumerable.Range(0, iso.Count).Select(i => iso.Get(i) as PdfDictionary).Single(d => d?.GetInteger(PdfName.Get("ExtensionLevel")) == 32004)!;
    }

    /// <summary>
    /// ISO/TS 32004, 4, Table 1: the writer declares the extension with its own values, not those of the source — /BaseVersion the name 2.0,
    /// /ExtensionLevel 32004, /ExtensionRevision the text „:2024“ with its colon, /URL the page of the document (read back decrypted).
    /// </summary>
    [Fact]
    public void MacDeclaration_WrittenWithoutOneInTheSource_HasTheValuesOfTable1()
    {
        var entry = M4eSavedDeclaration();
        Assert.Equal("2.0", entry.GetName(PdfName.Get("BaseVersion"))?.Value);
        Assert.Equal(32004, entry.GetRaw(PdfName.Get("ExtensionLevel")) is PdfInteger level ? level.Value : -1);
        Assert.Equal(":2024", Encoding.ASCII.GetString(entry.GetString(PdfName.Get("ExtensionRevision"))!.Bytes));
        Assert.Equal("https://www.iso.org/standard/45877.html", Encoding.ASCII.GetString(entry.GetString(PdfName.Get("URL"))!.Bytes));
    }

    /// <summary>ISO/TS 32004, 4, Table 1: the declaration also carries /Type /DeveloperExtensions.</summary>
    [Fact]
    public void MacDeclaration_WrittenWithoutOneInTheSource_HasTypeDeveloperExtensions() =>
        Assert.Equal("DeveloperExtensions", M4eSavedDeclaration().GetName(PdfName.Type)?.Value);

    // ------------------------------------------------------------ ISO/TS 32004, 5.1: /KDFSalt

    /// <summary>ISO/TS 32004, 5.1: /KDFSalt has 32 bytes — one of 16 bytes (the token derived with exactly that salt) is rejected with its name.</summary>
    [Fact]
    public void Mac_KdfSaltOf16Bytes_Invalid() =>
        M4eInvalid(M4eMacFile(patchEncrypt: e => e.Set(M4eKdfSaltKey, new PdfString(RandomNumberGenerator.GetBytes(16)))),
            "Der Schlüsselzusatz für den Integritätsschutz (/KDFSalt) fehlt oder hat nicht 32 Byte.");

    /// <summary>
    /// ISO/TS 32004, 5.1: /KDFSalt is a direct object — a source that gives it as an indirect object (tolerated when reading) is saved with
    /// the salt written directly in /Encrypt, the same 32 bytes, the fresh MAC valid.
    /// </summary>
    [Fact]
    public void MacFile_SavedFromAnIndirectSalt_WritesTheSaltDirect()
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var pdf = Pdf.TestPdf.Simple();
        var saltObject = pdf.Add(new PdfString(salt));
        var source = M4eMacFile(pdf: pdf, spec: new M4eSpec { KekSalt = salt }, patchEncrypt: e => e.Set(M4eKdfSaltKey, Pdf.TestPdf.Ref(saltObject)));
        using var doc = PdfDocument.Open(source, M4ePassword);
        Assert.IsType<PdfReference>(doc.EncryptDictionary!.GetRaw(M4eKdfSaltKey));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "2.0", WriteMac = true });
        using var copy = PdfDocument.Open(output.ToArray(), M4ePassword);
        Assert.Equal(salt, Assert.IsType<PdfString>(copy.EncryptDictionary!.GetRaw(M4eKdfSaltKey)).Bytes);
        Assert.Equal(MacState.Valid, copy.Integrity.State);
    }

    // ------------------------------------------------------------ ISO/TS 32004, 5.2: /AuthCode

    private static (byte[] Bytes, PdfDocument Copy) M4eSaved(bool objectStreams)
    {
        using var doc = PdfDocument.Open(M4eMacFile(), M4ePassword);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "2.0", WriteMac = true, ObjectStreams = objectStreams });
        return (output.ToArray(), PdfDocument.Open(output.ToArray(), M4ePassword));
    }

    /// <summary>
    /// ISO/TS 32004, 5.2 (Table 5 and Table 6): the saved file has /AuthCode as a direct dictionary in its newest trailer (both cross-reference
    /// forms), and in the trailer text /MACLocation, /ByteRange and /MAC follow as direct values — a name, an array, a hexadecimal string —
    /// with no object reference; no /SigObjRef in a standalone MAC.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MacFile_Saved_AuthCodeAndItsEntriesDirect(bool objectStreams)
    {
        var (bytes, copy) = M4eSaved(objectStreams);
        using (copy)
        {
            var authCode = Assert.IsType<PdfDictionary>(copy.NewestTrailer!.GetRaw(M4eAuthCodeKey));
            Assert.IsType<PdfName>(authCode.GetRaw(PdfName.Get("MACLocation")));
            Assert.IsType<PdfArray>(authCode.GetRaw(PdfName.Get("ByteRange")));
            Assert.IsType<PdfString>(authCode.GetRaw(PdfName.Get("MAC")));
            Assert.Null(authCode.GetRaw(PdfName.Get("SigObjRef")));
            Assert.Equal(MacState.Valid, copy.Integrity.State);
        }
        var text = Encoding.Latin1.GetString(bytes);
        var at = text.LastIndexOf("/AuthCode", StringComparison.Ordinal);
        Assert.Matches(@"^/AuthCode\s*<<", text[at..]);
        var authCodeText = text[at..(text.IndexOf(">>", at, StringComparison.Ordinal) + 2)];
        Assert.Matches(@"/MACLocation\s*/Standalone\b", authCodeText);
        Assert.Matches(@"/ByteRange\s*\[\s*0\s+\d+\s+\d+\s+\d+\s*\]", authCodeText);
        Assert.Matches(@"/MAC\s*<[0-9A-Fa-f]+>", authCodeText);
        Assert.DoesNotMatch(@"\d+\s+\d+\s+R\b", authCodeText);
    }

    /// <summary>
    /// ISO/TS 32004, 5.2: /AuthCode belongs to /V 5 and above — an AES-128 file (revision 4) with an /AuthCode in its trailer is not checked
    /// (state none, no note), and its copy (even with WriteMac asked for 2.0) carries no /AuthCode.
    /// </summary>
    [Fact]
    public void Mac_AuthCodeInARevision4File_IgnoredAndNotWritten()
    {
        var file = Pdf.TestPdf.Simple().Build(cipher: PdfCipher.Aes128, user: M4ePassword, owner: "chef", permissions: PdfPermissions.Print,
            patchEncrypt: e => e.Set(M4eKdfSaltKey, new PdfString(RandomNumberGenerator.GetBytes(32))),
            patchTrailer: t => t.Set(M4eAuthCodeKey, Pdf.TestPdf.Dict(("MACLocation", Pdf.TestPdf.N("Standalone")),
                ("ByteRange", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(10), PdfInteger.Of(20), PdfInteger.Of(30)])), ("MAC", new PdfString(new byte[8], true)))));
        using var doc = PdfDocument.Open(file, M4ePassword);
        Assert.Equal(4, doc.Security!.Revision);
        Assert.Equal(MacState.None, doc.Integrity.State);
        Assert.DoesNotContain(doc.Problems, p => p.Contains("Integrität", StringComparison.Ordinal) || p.Contains("MAC", StringComparison.Ordinal));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "2.0", WriteMac = true });
        using var copy = PdfDocument.Open(output.ToArray(), M4ePassword);
        Assert.False(copy.Trailer.ContainsKey(M4eAuthCodeKey));
        Assert.False(copy.NewestTrailer?.ContainsKey(M4eAuthCodeKey) ?? false);
    }

    /// <summary>
    /// ISO/TS 32004, 5.2, Table 6: /MACLocation /AttachedToSig (here with a /SigObjRef) and a value PDF-Knead does not know: not checkable,
    /// never „violated“.
    /// </summary>
    [Theory]
    [InlineData("AttachedToSig", "Der MAC hängt an einer Signatur; das prüft PDF-Knead noch nicht.")]
    [InlineData("PDFK_Woanders", "Unbekannte Lage des MAC („PDFK_Woanders“).")]
    public void Mac_OtherMacLocation_NotCheckable(string location, string detail)
    {
        var check = M4eCheck(M4eMacFile(patchAuthCode: a =>
        {
            a.Set(PdfName.Get("MACLocation"), PdfName.Get(location));
            if (location == "AttachedToSig") a.Set(PdfName.Get("SigObjRef"), Pdf.TestPdf.Ref(1));
        }));
        Assert.Equal(MacState.NotCheckable, check.State);
        Assert.Equal(detail, check.Detail);
    }

    /// <summary>
    /// ISO/TS 32004, 5.2, Table 6: /ByteRange is four non-negative integers [0 L1 S L2] with S greater than L1 — a first element 1, S equal to
    /// L1, three or five values: each rejected as an invalid byte range.
    /// </summary>
    [Theory]
    [InlineData("first1")]
    [InlineData("sNotAboveL1")]
    [InlineData("three")]
    [InlineData("five")]
    public void Mac_ByteRangeMalformed_Invalid(string variant) =>
        M4eInvalid(M4eMacFile(range: (l1, s, l2) => variant switch
        {
            "first1" => $"[1 {l1} {s} {l2}",
            "sNotAboveL1" => $"[0 {l1} {l1} {l2 + s - l1}",
            "three" => $"[0 {l1} {s}",
            _ => $"[0 {l1} {s} {l2} 0",
        }), M4eBadRange);

    /// <summary>ISO/TS 32004, 5.2, Table 6: the /ByteRange values are integers — L1 written as a real (the gap where it is) is rejected.</summary>
    [Fact]
    public void Mac_ByteRangeWithAReal_Invalid() =>
        M4eInvalid(M4eMacFile(range: (l1, s, l2) => $"[0 {l1}.5 {s} {l2}"), M4eBadRange);

    /// <summary>
    /// ISO/TS 32004, 5.2, Table 6: between „&lt;“ and „&gt;“ only the hexadecimal digits of the token — a zero byte after the DER value (in a
    /// gap made larger for it) is rejected, as is an odd number of digits.
    /// </summary>
    [Fact]
    public void Mac_GapWithTrailingDataOrOddDigits_Invalid()
    {
        M4eInvalid(M4eMacFile(new M4eSpec { Trailing = 1 }), "Nach dem MAC stehen weitere Daten.");
        M4eInvalid(M4eMacFile(oddDigits: true), "Der MAC-Wert steht nicht genau in der ausgesparten Lücke.");
    }

    // ------------------------------------------------------------ ISO/TS 32004, 6.2, 6.3, 6.6: the token

    /// <summary>ISO/TS 32004, 6.2 and 6.6.1: PdfMacIntegrityInfo has version 0 — version 1 (HMAC and message digest computed anew) is violated.</summary>
    [Fact]
    public void Mac_IntegrityInfoVersion1_Invalid() => M4eInvalid(M4eMacFile(new M4eSpec { InfoVersion = 1 }), M4eNotTs32004);

    /// <summary>ISO/TS 32004, 6.6.1 (6.6.2): a standalone token has no signatureDigest — one present is violated.</summary>
    [Fact]
    public void Mac_StandaloneWithSignatureDigest_Invalid() => M4eInvalid(M4eMacFile(new M4eSpec { SignatureDigest = true }), M4eNotTs32004);

    /// <summary>ISO/TS 32004, 6.3.1: the ContentInfo has the type id-ct-authData — SignedData is violated; an AuthenticatedData of another
    /// version (2) is not checkable.</summary>
    [Fact]
    public void Mac_OuterTypeOrVersion_InvalidOrNotCheckable()
    {
        M4eInvalid(M4eMacFile(new M4eSpec { OuterType = M4eSignedData }), M4eNotTs32004);
        Assert.Equal(MacState.NotCheckable, M4eCheck(M4eMacFile(new M4eSpec { Version = 2 })).State);
    }

    /// <summary>ISO/TS 32004, 6.3.2: encapContentInfo has the type id-ct-pdfMacIntegrityInfo — id-data is violated.</summary>
    [Fact]
    public void Mac_EncapsulatedTypeData_Invalid() => M4eInvalid(M4eMacFile(new M4eSpec { EncapType = M4eIdData }), M4eNotTs32004);

    /// <summary>ISO/TS 32004, 6.3.3: exactly one recipientInfo, a PasswordRecipientInfo — two are violated; a KEKRecipientInfo is not checkable.</summary>
    [Fact]
    public void Mac_RecipientsTwoOrKek_InvalidOrNotCheckable()
    {
        M4eInvalid(M4eMacFile(new M4eSpec { Recipients = w => [M4ePwri(w), M4ePwri(w)] }), M4eNotTs32004);
        var kekRecipient = new M4eSpec
        {
            Recipients = w => [M4eDer(x =>
            {
                using (x.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                {
                    x.WriteInteger(4);
                    using (x.PushSequence()) x.WriteOctetString([1, 2, 3, 4]);
                    x.WriteEncodedValue(M4eAlg(M4eAes256Wrap));
                    x.WriteOctetString(w);
                }
            })],
        };
        Assert.Equal(MacState.NotCheckable, M4eCheck(M4eMacFile(kekRecipient)).State);
    }

    /// <summary>ISO/TS 32004, 6.3.3 and 6.4: the MAC key unwraps only with the key encryption key from this file's /KDFSalt — a token
    /// wrapped with another 32-byte salt is violated with the key named.</summary>
    [Fact]
    public void Mac_WrappedWithAnotherSalt_KeyDoesNotFit() =>
        M4eInvalid(M4eMacFile(new M4eSpec { KekSalt = RandomNumberGenerator.GetBytes(32) }), "Der Schlüssel des MAC passt nicht zur Datei.");

    /// <summary>
    /// ISO/TS 32004, 6.3.3: the MAC key comes from strong randomness and has the length of HMAC-SHA-256 — two saves of the same file at the
    /// same time give different keys, each of 32 bytes, unwrapped from the saved token by the test's own key wrap (integrity value A6…A6)
    /// with the key encryption key from the file key, and the MAC value of the token recomputed with it.
    /// </summary>
    [Fact]
    public void MacFile_Saved_MacKeyRandomOf32Bytes()
    {
        var source = M4eMacFile();
        var at = new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.FromHours(1));
        byte[] KeyOfASave()
        {
            using var doc = PdfDocument.Open(source, M4ePassword);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "2.0", WriteMac = true, ModificationTime = at });
            var bytes = output.ToArray();
            using var copy = PdfDocument.Open(bytes, M4ePassword);
            var text = Encoding.Latin1.GetString(bytes);
            var macAt = Regex.Matches(text, @"/MAC\s*<").Last();
            var gap = macAt.Index + macAt.Length;
            var token = Convert.FromHexString(text[gap..text.IndexOf('>', gap)]);

            var data = new AsnReader(token, AsnEncodingRules.DER).ReadSequence();
            data.ReadObjectIdentifier();
            var authData = data.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            authData.ReadInteger();
            var pwri = authData.ReadSetOf().ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 3));
            pwri.ReadInteger();
            pwri.ReadEncodedValue();
            pwri.ReadEncodedValue();
            var wrapped = pwri.ReadOctetString();
            authData.ReadEncodedValue();
            authData.ReadEncodedValue();
            authData.ReadEncodedValue();
            var attrs = authData.ReadEncodedValue().ToArray();
            var mac = authData.ReadOctetString();

            var kek = M4eKek(M4eFileKey(copy.EncryptDictionary!), copy.EncryptDictionary!.GetString(M4eKdfSaltKey)!.Bytes);
            var (iv, key) = M4eUnwrap(kek, wrapped);
            Assert.Equal(Enumerable.Repeat((byte)0xA6, 8), iv);
            Assert.Equal(32, key.Length);
            attrs[0] = 0x31;
            Assert.Equal(mac, HMACSHA256.HashData(key, attrs));
            return key;
        }
        Assert.NotEqual(KeyOfASave(), KeyOfASave());
    }

    /// <summary>
    /// ISO/TS 32004, 6.3.4 and 6.6.1: digestAlgorithm from Table 8 — with SHA-384, SHA-512 and SHA3-256 (where the system has it) for the
    /// byte range, the message digest and the algorithm protection the token is valid; SHA-1 is not in the table: not checkable.
    /// </summary>
    [Theory]
    [InlineData("SHA384", M4eSha384)]
    [InlineData("SHA512", M4eSha512)]
    [InlineData("SHA3-256", M4eSha3_256)]
    [InlineData("SHA1", M4eSha1)]
    public void Mac_DigestAlgorithmOfTable8_ValidOrNotCheckable(string name, string oid)
    {
        var check = M4eCheck(M4eMacFile(new M4eSpec { Digest = new HashAlgorithmName(name), DigestOid = oid }));
        var expected = name == "SHA1" || (name == "SHA3-256" && !SHA3_256.IsSupported) ? MacState.NotCheckable : MacState.Valid;
        Assert.Equal(expected, check.State);
    }

    /// <summary>ISO/TS 32004, 6.3.6.1: authAttrs must be there — a token without them is violated; another attribute beside the three
    /// (a signing time, HMAC over all four) is allowed: valid.</summary>
    [Fact]
    public void Mac_AuthAttrsMissingOrWithAnotherAttribute_InvalidOrValid()
    {
        M4eInvalid(M4eMacFile(new M4eSpec { WithoutAuthAttrs = true }), M4eNotTs32004);
        var extra = new M4eSpec();
        extra = extra with
        {
            Attributes = md => [M4eAttr(M4eContentType, M4eOid(M4eIntegrityInfo)), M4eAttr(M4eMessageDigest, M4eOctets(md)), M4eDefaultProtection(extra),
                M4eAttr(M4eSigningTime, M4eDer(w => w.WriteUtcTime(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero))))],
        };
        Assert.Equal(MacState.Valid, M4eCheck(M4eMacFile(extra)).State);
    }

    /// <summary>ISO/TS 32004, 6.3.6.2: exactly one contentType attribute with id-ct-pdfMacIntegrityInfo — two of them, or the value id-data,
    /// are violated.</summary>
    [Fact]
    public void Mac_ContentTypeAttributeTwiceOrData_Invalid()
    {
        var spec = new M4eSpec();
        M4eInvalid(M4eMacFile(spec with
        {
            Attributes = md => [M4eAttr(M4eContentType, M4eOid(M4eIntegrityInfo)), M4eAttr(M4eContentType, M4eOid(M4eIntegrityInfo)),
                M4eAttr(M4eMessageDigest, M4eOctets(md)), M4eDefaultProtection(spec)],
        }), M4eNotTs32004);
        M4eInvalid(M4eMacFile(spec with
        {
            Attributes = md => [M4eAttr(M4eContentType, M4eOid(M4eIdData)), M4eAttr(M4eMessageDigest, M4eOctets(md)), M4eDefaultProtection(spec)],
        }), M4eNotTs32004);
    }

    /// <summary>ISO/TS 32004, 6.3.6.3: exactly one messageDigest, the digest of eContent — one bit changed (HMAC over the changed attribute)
    /// does not match; two messageDigest attributes are violated.</summary>
    [Fact]
    public void Mac_MessageDigestChangedOrTwice_Invalid()
    {
        var spec = new M4eSpec();
        M4eInvalid(M4eMacFile(spec with
        {
            Attributes = md =>
            {
                var changed = md.ToArray();
                changed[0] ^= 0x01;
                return [M4eAttr(M4eContentType, M4eOid(M4eIntegrityInfo)), M4eAttr(M4eMessageDigest, M4eOctets(changed)), M4eDefaultProtection(spec)];
            },
        }), "Der MAC stimmt nicht.");
        M4eInvalid(M4eMacFile(spec with
        {
            Attributes = md => [M4eAttr(M4eContentType, M4eOid(M4eIntegrityInfo)), M4eAttr(M4eMessageDigest, M4eOctets(md)),
                M4eAttr(M4eMessageDigest, M4eOctets(md)), M4eDefaultProtection(spec)],
        }), M4eNotTs32004);
    }

    /// <summary>ISO/TS 32004, 6.3.7: no unauthAttrs — a token with an unauthAttrs [3] after the MAC value is violated.</summary>
    [Fact]
    public void Mac_UnauthAttrs_Invalid() =>
        M4eInvalid(M4eMacFile(new M4eSpec { UnauthAttrs = [M4eAttr(M4eSigningTime, M4eDer(w => w.WriteUtcTime(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero))))] }),
            M4eNotTs32004);

    /// <summary>
    /// ISO/TS 32004, 6.4: the input of pdfMacWrapKdf is the file key — the key encryption key of the handler equals HKDF (runtime) of the
    /// file key the test decrypts itself from /UE (algorithm 2.A with its own 2.B), with /KDFSalt and „PDFMAC“.
    /// </summary>
    [Fact]
    public void MacKek_IsHkdfOfTheFileKeyFromUe()
    {
        var file = Pdf.TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: M4ePassword, owner: "chef", version: "2.0", macRequired: true, mac: TestMac.None);
        using var doc = PdfDocument.Open(file, M4ePassword);
        var salt = doc.EncryptDictionary!.GetString(M4eKdfSaltKey)!.Bytes;
        Assert.Equal(32, salt.Length);
        Assert.Equal(M4eKek(M4eFileKey(doc.EncryptDictionary!), salt), doc.Security!.MacKek(salt));
    }

    /// <summary>ISO/TS 32004, 6.5.1: in an unsigned newest revision /ByteRange covers the whole file — a valid file with a comment appended
    /// after %%EOF is violated (not covered to the end).</summary>
    [Fact]
    public void Mac_CommentAppendedAfterEof_NotTheWholeFile() =>
        M4eInvalid([.. M4eMacFile(), .. "% angehaengt\n"u8.ToArray()],
            "Der Integritätsschutz deckt nicht die ganze Datei ab — sie wurde danach verändert, oder ein anderes Programm hat ihn beim Anhängen nicht erneuert.");

    // ------------------------------------------------------------ RFC 6211 (and ISO/TS 32004, 6.3.6.4): CMSAlgorithmProtection

    private static byte[] M4eWithProtection(params byte[][] protectionAttributes) => M4eMacFile(new M4eSpec
    {
        Attributes = md => [M4eAttr(M4eContentType, M4eOid(M4eIntegrityInfo)), M4eAttr(M4eMessageDigest, M4eOctets(md)), .. protectionAttributes],
    });

    /// <summary>ISO/TS 32004, 6.3.6.4 and RFC 6211, 2: the attribute is recommended, not required — a token without it is valid.</summary>
    [Fact]
    public void AlgorithmProtection_Absent_Valid() => Assert.Equal(MacState.Valid, M4eCheck(M4eWithProtection()).State);

    /// <summary>
    /// RFC 6211, 2: for AuthenticatedData the value holds digestAlgorithm and macAlgorithm [2], exactly one of [1] and [2] — signatureAlgorithm
    /// [1] instead, [2] followed by another field, or digestAlgorithm alone: each violated (HMAC computed anew).
    /// </summary>
    [Theory]
    [InlineData("signatureInsteadOfMac")]
    [InlineData("macAndAnother")]
    [InlineData("digestOnly")]
    public void AlgorithmProtection_FieldsNotDigestAndMac_Invalid(string variant)
    {
        var digest = M4eAlg(M4eSha256);
        var value = variant switch
        {
            "signatureInsteadOfMac" => M4eProtection(digest, M4eAlg(M4eHmacSha256, tag: 1)),
            "macAndAnother" => M4eProtection(digest, M4eAlg(M4eHmacSha256, tag: 2), M4eAlg(M4eHmacSha256, tag: 3)),
            _ => M4eProtection(digest),
        };
        M4eInvalid(M4eWithProtection(M4eAttr(M4eAlgorithmProtection, value)), M4eMismatch);
    }

    private static byte[] M4eGoodProtectionValue => M4eProtection(M4eAlg(M4eSha256), M4eAlg(M4eHmacSha256, tag: 2));

    /// <summary>
    /// RFC 6211, 2: exactly one attribute value, and the attribute only among the authenticated attributes — two values, an empty value set
    /// (not readable), or the attribute only in unauthAttrs: each violated.
    /// </summary>
    [Fact]
    public void AlgorithmProtection_ValuesNotExactlyOneOrUnauthenticated_Invalid()
    {
        var good = M4eGoodProtectionValue;
        M4eInvalid(M4eWithProtection(M4eAttr(M4eAlgorithmProtection, good, M4eProtection(M4eAlg(M4eSha256, parameters: M4eNull), M4eAlg(M4eHmacSha256, tag: 2)))),
            M4eMismatch);
        M4eInvalid(M4eWithProtection(M4eAttr(M4eAlgorithmProtection)), "Der Integritätsschutz (MAC) ist nicht lesbar.");
        M4eInvalid(M4eMacFile(new M4eSpec
        {
            Attributes = md => [M4eAttr(M4eContentType, M4eOid(M4eIntegrityInfo)), M4eAttr(M4eMessageDigest, M4eOctets(md))],
            UnauthAttrs = [M4eAttr(M4eAlgorithmProtection, good)],
        }), M4eNotTs32004);
    }

    /// <summary>RFC 6211, 2: the authenticated attributes of an AuthenticatedData hold the attribute at most once — twice is violated.</summary>
    [Fact]
    public void AlgorithmProtection_Twice_Invalid()
    {
        var good = M4eGoodProtectionValue;
        M4eInvalid(M4eWithProtection(M4eAttr(M4eAlgorithmProtection, good), M4eAttr(M4eAlgorithmProtection, good)), M4eNotTs32004);
    }

    /// <summary>
    /// RFC 6211, 3: compared as algorithms, not as bytes — digestAlgorithm in the attribute with explicit NULL parameters, in the
    /// AuthenticatedData without, is the same (valid); other parameters (an integer) are not (violated, not readable as SHA-256).
    /// </summary>
    [Fact]
    public void AlgorithmProtection_NullParametersEqualAbsent_OthersDiffer()
    {
        var mac = M4eAlg(M4eHmacSha256, tag: 2);
        Assert.Equal(MacState.Valid, M4eCheck(M4eWithProtection(M4eAttr(M4eAlgorithmProtection, M4eProtection(M4eAlg(M4eSha256, parameters: M4eNull), mac)))).State);
        Assert.Equal(MacState.Invalid,
            M4eCheck(M4eWithProtection(M4eAttr(M4eAlgorithmProtection, M4eProtection(M4eAlg(M4eSha256, parameters: M4eDer(w => w.WriteInteger(5))), mac)))).State);
    }

    /// <summary>
    /// RFC 6211, 3.2 (ISO/TS 32004, 6.3.6.4): the verifier compares digestAlgorithm and macAlgorithm with the attribute — SHA-384 or SHA-512
    /// beside SHA-256, or HMAC-SHA-384 beside HMAC-SHA-256: authentication fails.
    /// </summary>
    [Theory]
    [InlineData(M4eSha384, M4eHmacSha256)]
    [InlineData(M4eSha512, M4eHmacSha256)]
    [InlineData(M4eSha256, M4eHmacSha384)]
    public void AlgorithmProtection_OtherAlgorithms_Invalid(string digestOid, string macOid) =>
        M4eInvalid(M4eWithProtection(M4eAttr(M4eAlgorithmProtection, M4eProtection(M4eAlg(digestOid), M4eAlg(macOid, tag: 2)))), M4eMismatch);
    /// <summary>
    /// ISO/TS 32004, 5.2, Table 6: with Standalone /MAC is required and its value is what stands in the gap of /ByteRange. A token in the gap
    /// under another key and no /MAC, or a /MAC beside it with another value, is violated — before, only the gap was read and either was valid
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1110). The token itself is valid in both files.
    /// </summary>
    [Theory]
    [InlineData(false, "Im Integritätsschutz fehlt der MAC-Wert (/MAC).")]
    [InlineData(true, "Der MAC-Wert (/MAC) steht nicht in der ausgesparten Lücke.")]
    public void Mac_ValueOfMacNotTheGap_Invalid(bool otherMac, string detail)
    {
        var length = M4eToken(new M4eSpec(), new byte[32], new byte[32], M4eHash(HashAlgorithmName.SHA256, [])).Length;
        var file = M4eMacFile(gapKey: "MAX", patchAuthCode: a =>
        {
            if (otherMac) a.Set(PdfName.Get("MAC"), new PdfString(new byte[length], true));
        });
        M4eInvalid(file, detail);
    }
}
