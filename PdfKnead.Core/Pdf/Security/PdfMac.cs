using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Security;

/// <summary>The result of checking the integrity protection of an encrypted file (ISO/TS 32004).</summary>
public enum MacState
{
    /// <summary>The file has no MAC and does not ask for one.</summary>
    None,
    /// <summary>The MAC covers the whole file and is right.</summary>
    Valid,
    /// <summary>The MAC does not fit the file: changed after it was protected (or a damaged, stale or wrong token).</summary>
    Invalid,
    /// <summary>The file asks for a MAC per revision (bit 13 cleared) but has none: not conforming, the integrity cannot be shown.</summary>
    MissingButRequired,
    /// <summary>A MAC whose method or place PDF-Knead does not check (another key derivation, attached to a signature, a repaired file …).</summary>
    NotCheckable,
}

/// <summary>What <see cref="PdfMac.Verify"/> found.</summary>
/// <param name="State">The result.</param>
/// <param name="Detail">Why, where it is not simply valid or absent (localised); null otherwise.</param>
/// <param name="EmptyUserPassword">The file opens without a password: anyone can derive the key, the MAC protects nothing (ISO/TS 32004, 6.4, note 2).</param>
public sealed record MacCheck(MacState State, string? Detail = null, bool EmptyUserPassword = false);

/// <summary>
/// PDF MAC tokens (ISO/TS 32004): CMS AuthenticatedData with one PasswordRecipientInfo whose key encryption key comes from the file key
/// (HKDF-SHA-256 with /KDFSalt and „PDFMAC“), AES-256 key wrap of a random MAC key (RFC 3394), HMAC-SHA-256 over the authenticated
/// attributes, a PdfMacIntegrityInfo with the digest of the byte range as content. Building and checking share these building blocks;
/// the tests hold them against published vectors and against a token computed independently (docs/ABLAUFPLAN.md, 5.5;
/// docs/FEHLERGEDAECHTNIS.md, No. 556).
/// </summary>
public static class PdfMac
{
    internal const string AuthData = "1.2.840.113549.1.9.16.1.2", IntegrityInfo = "1.0.32004.1.0", WrapKdf = "1.0.32004.1.1",
        Aes256Wrap = "2.16.840.1.101.3.4.1.45", HmacSha256 = "1.2.840.113549.2.9", Sha256 = "2.16.840.1.101.3.4.2.1",
        ContentType = "1.2.840.113549.1.9.3", MessageDigest = "1.2.840.113549.1.9.4", AlgorithmProtection = "1.2.840.113549.1.9.52",
        MacData = "1.0.32004.1.2", SignedData = "1.2.840.113549.1.7.2";

    /// <summary>
    /// Whether PDF-Knead writes MACs (ISO/TS 32004): off until an outside check with a tool that checks TS 32004 has been passed (a wrong MAC is
    /// worse than none); PDFKNEAD_MAC_SCHREIBEN=1 switches it on for trying it by hand, the tests switch it on per call.
    /// </summary>
    internal static bool WritingOn => Environment.GetEnvironmentVariable("PDFKNEAD_MAC_SCHREIBEN") == "1";

    private static readonly Dictionary<string, HashAlgorithmName> Digests = new()
    {
        [Sha256] = HashAlgorithmName.SHA256,
        ["2.16.840.1.101.3.4.2.2"] = HashAlgorithmName.SHA384,
        ["2.16.840.1.101.3.4.2.3"] = HashAlgorithmName.SHA512,
        ["2.16.840.1.101.3.4.2.8"] = HashAlgorithmName.SHA3_256,
        ["2.16.840.1.101.3.4.2.9"] = HashAlgorithmName.SHA3_384,
        ["2.16.840.1.101.3.4.2.10"] = HashAlgorithmName.SHA3_512,
    };

    private static readonly byte[] KeyWrapIv = [0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6];

    // ------------------------------------------------------------ building blocks

    /// <summary>AES key wrap without padding (RFC 3394, 2.2.1): the key in 64-bit blocks, six rounds, the integrity value A6…A6 in front.</summary>
    public static byte[] Wrap(byte[] kek, byte[] key) => Wrap(kek, key, null);

    /// <summary>
    /// For the tests: called at every step t of <see cref="Wrap(byte[], byte[], KeyWrapStep?)"/> and
    /// <see cref="Unwrap(byte[], byte[], KeyWrapStep?)"/> with copies of A and R, under the names of the tables of RFC 3394, section 4
    /// („In“, „Enc“, „XorT“ when wrapping; „In“, „XorT“, „Dec“ when unwrapping). Without one nothing happens.
    /// </summary>
    internal delegate void KeyWrapStep(int t, string phase, byte[] a, byte[] r);

    /// <inheritdoc cref="Wrap(byte[], byte[])"/>
    /// <param name="step">Observes every step (tests only); null in every other caller.</param>
    internal static byte[] Wrap(byte[] kek, byte[] key, KeyWrapStep? step)
    {
        if (key.Length < 16 || key.Length % 8 != 0) throw new ArgumentException("RFC 3394 wraps 64-bit blocks, at least two.", nameof(key));
        var n = key.Length / 8;
        var a = KeyWrapIv.ToArray();
        var r = key.ToArray();
        using var aes = Aes.Create();
        aes.Key = kek;
        var block = new byte[16];
        for (var j = 0; j <= 5; j++)
            for (var i = 1; i <= n; i++)
            {
                step?.Invoke(n * j + i, "In", a.ToArray(), r.ToArray());
                a.CopyTo(block, 0);
                Array.Copy(r, (i - 1) * 8, block, 8, 8);
                var b = aes.EncryptEcb(block, PaddingMode.None);
                var t = (ulong)(n * j + i);
                BinaryPrimitives.WriteUInt64BigEndian(a, BinaryPrimitives.ReadUInt64BigEndian(b) ^ t);
                Array.Copy(b, 8, r, (i - 1) * 8, 8);
                if (step is not null)
                {
                    step((int)t, "Enc", b[..8], r.ToArray());
                    step((int)t, "XorT", a.ToArray(), r.ToArray());
                }
            }
        return [.. a, .. r];
    }

    /// <summary>Unwraps (RFC 3394, 2.2.2); null if the integrity value does not come out (another key, or changed).</summary>
    public static byte[]? Unwrap(byte[] kek, byte[] wrapped) => Unwrap(kek, wrapped, null);

    /// <inheritdoc cref="Unwrap(byte[], byte[])"/>
    /// <param name="step">Observes every step (tests only); null in every other caller.</param>
    internal static byte[]? Unwrap(byte[] kek, byte[] wrapped, KeyWrapStep? step)
    {
        if (wrapped.Length < 24 || wrapped.Length % 8 != 0) return null;
        var n = wrapped.Length / 8 - 1;
        var a = wrapped[..8];
        var r = wrapped[8..];
        using var aes = Aes.Create();
        aes.Key = kek;
        var block = new byte[16];
        for (var j = 5; j >= 0; j--)
            for (var i = n; i >= 1; i--)
            {
                var t = (ulong)(n * j + i);
                step?.Invoke((int)t, "In", a.ToArray(), r.ToArray());
                BinaryPrimitives.WriteUInt64BigEndian(block, BinaryPrimitives.ReadUInt64BigEndian(a) ^ t);
                Array.Copy(r, (i - 1) * 8, block, 8, 8);
                step?.Invoke((int)t, "XorT", block[..8], r.ToArray());
                var b = aes.DecryptEcb(block, PaddingMode.None);
                Array.Copy(b, 0, a, 0, 8);
                Array.Copy(b, 8, r, (i - 1) * 8, 8);
                step?.Invoke((int)t, "Dec", a.ToArray(), r.ToArray());
            }
        return CryptographicOperations.FixedTimeEquals(a, KeyWrapIv) ? r : null;
    }

    /// <summary>pdfMacWrapKdf (ISO/TS 32004, 6.4): HKDF-SHA-256 of the file key with /KDFSalt as salt and „PDFMAC“ as info, 32 bytes.</summary>
    public static byte[] Kek(byte[] fileKey, byte[] kdfSalt) => HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey, 32, kdfSalt, "PDFMAC"u8.ToArray());

    /// <summary>
    /// A PDF MAC token for an unsigned revision (ISO/TS 32004, 6.3): SHA-256, HMAC-SHA-256, the MAC key wrapped with <paramref name="kek"/>,
    /// content type, message digest and algorithm protection as authenticated attributes, no unauthenticated ones. Every field has a fixed
    /// length, so does the token (<see cref="TokenLength"/>).
    /// </summary>
    /// <param name="kek">The key encryption key (<see cref="Kek"/>).</param>
    /// <param name="macKey">The MAC key, 32 random bytes.</param>
    /// <param name="dataDigest">SHA-256 of the byte range.</param>
    public static byte[] Token(byte[] kek, byte[] macKey, byte[] dataDigest) => Token(kek, macKey, dataDigest, null);

    /// <inheritdoc cref="Token(byte[], byte[], byte[])"/>
    /// <param name="signatureDigest">In a signed revision: SHA-256 of the signature value the token is attached to (6.6.3); null in an unsigned
    /// one, where the field is absent (6.6.2).</param>
    internal static byte[] Token(byte[] kek, byte[] macKey, byte[] dataDigest, byte[]? signatureDigest)
    {
        var info = new AsnWriter(AsnEncodingRules.DER);
        using (info.PushSequence())
        {
            info.WriteInteger(0);
            info.WriteOctetString(dataDigest);
            if (signatureDigest is not null) info.WriteOctetString(signatureDigest, new Asn1Tag(TagClass.ContextSpecific, 0));
        }
        var eContent = info.Encode();

        // the authenticated attributes as SET OF (sorted by DER) — the MAC is computed over this form, stored with the tag [2] (RFC 5652, 9.2)
        var attrs = new AsnWriter(AsnEncodingRules.DER);
        using (attrs.PushSetOf())
        {
            Attribute(attrs, ContentType, w => w.WriteObjectIdentifier(IntegrityInfo));
            Attribute(attrs, MessageDigest, w => w.WriteOctetString(SHA256.HashData(eContent)));
            Attribute(attrs, AlgorithmProtection, w =>
            {
                using (w.PushSequence())
                {
                    using (w.PushSequence()) w.WriteObjectIdentifier(Sha256);
                    using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2))) w.WriteObjectIdentifier(HmacSha256);
                }
            });
        }
        var attrSet = attrs.Encode();
        var mac = HMACSHA256.HashData(macKey, attrSet);
        var stored = attrSet.ToArray();
        stored[0] = 0xA2;

        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(AuthData);
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (w.PushSequence())
            {
                w.WriteInteger(0);
                using (w.PushSetOf())
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3)))
                {
                    w.WriteInteger(0);
                    using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) w.WriteObjectIdentifier(WrapKdf);
                    using (w.PushSequence()) w.WriteObjectIdentifier(Aes256Wrap);
                    w.WriteOctetString(Wrap(kek, macKey));
                }
                using (w.PushSequence()) w.WriteObjectIdentifier(HmacSha256);
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1))) w.WriteObjectIdentifier(Sha256);
                using (w.PushSequence())
                {
                    w.WriteObjectIdentifier(IntegrityInfo);
                    using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) w.WriteOctetString(eContent);
                }
                w.WriteEncodedValue(stored);
                w.WriteOctetString(mac);
            }
        }
        return w.Encode();

        static void Attribute(AsnWriter writer, string type, Action<AsnWriter> value)
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(type);
                using (writer.PushSetOf()) value(writer);
            }
        }
    }

    /// <summary>The length of every token <see cref="Token(byte[], byte[], byte[])"/> builds (measured once, not counted by hand).</summary>
    public static int TokenLength { get; } = Token(new byte[32], new byte[32], new byte[32]).Length;

    /// <summary>What a token attached to a signature adds to its CMS block at most: the token with its signature digest and the attribute
    /// around it (measured once, with room for longer length fields).</summary>
    internal static int AttachedLength { get; } = Token(new byte[32], new byte[32], new byte[32], new byte[32]).Length + 32;

    // ------------------------------------------------------------ writing into files

    /// <summary>The key encryption key of a document that asks for a MAC: from its file key and its /KDFSalt (6.4).</summary>
    internal static byte[] KekOf(PdfDocument doc) =>
        doc.Security!.MacKek(doc.EncryptDictionary!.GetString(PdfName.Get("KDFSalt"))!.Bytes);

    /// <summary>The placeholders of a standalone /AuthCode (Table 6): a fixed-width /ByteRange and the /MAC gap of exactly the token's length.</summary>
    internal static PdfDictionary StandalonePlaceholder()
    {
        var authCode = new PdfDictionary();
        authCode.Set(PdfName.Get("MACLocation"), PdfName.Get("Standalone"));
        authCode.Set(PdfName.Get("ByteRange"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(1_000_000_000_000), PdfInteger.Of(1_000_000_000_000), PdfInteger.Of(1_000_000_000_000)]));
        authCode.Set(PdfName.Get("MAC"), new PdfString(new byte[TokenLength], isHex: true));
        return authCode;
    }

    /// <summary>The /AuthCode of a signed revision (6.5.2, Table 6): /MACLocation /AttachedToSig and the signature dictionary, nothing else.</summary>
    /// <param name="signature">The signature dictionary the token is attached to.</param>
    internal static PdfDictionary AttachedAuthCode(PdfReference signature)
    {
        var authCode = new PdfDictionary();
        authCode.Set(PdfName.Get("MACLocation"), PdfName.Get("AttachedToSig"));
        authCode.Set(PdfName.Get("SigObjRef"), signature);
        return authCode;
    }

    /// <summary>
    /// Completes a standalone MAC in the last part of a file (6.5.1, 6.6.2): /ByteRange from 0 to the end of the file around the /MAC gap (padded
    /// with spaces, the same width), the digest of those bytes, the token as hexadecimal digits exactly in the gap.
    /// </summary>
    /// <param name="tail">The last part of the file, holding the trailer with the placeholders; filled in place.</param>
    /// <param name="tailStart">Where <paramref name="tail"/> starts in the file.</param>
    /// <param name="prefix">SHA-256 over every byte of the file before <paramref name="tail"/>; used up.</param>
    /// <param name="kek">The key encryption key (<see cref="Kek"/>).</param>
    internal static void FillStandalone(byte[] tail, long tailStart, IncrementalHash prefix, byte[] kek)
    {
        var text = System.Text.Encoding.Latin1.GetString(tail);
        var auth = text.LastIndexOf("/AuthCode", StringComparison.Ordinal);
        var gapStart = auth < 0 ? -1 : text.IndexOf('<', text.IndexOf("/MAC", auth, StringComparison.Ordinal));
        var gapEnd = gapStart < 0 ? -1 : text.IndexOf('>', gapStart) + 1;
        var rangeStart = auth < 0 ? -1 : text.IndexOf('[', text.IndexOf("/ByteRange", auth, StringComparison.Ordinal));
        var rangeEnd = rangeStart < 0 ? -1 : text.IndexOf(']', rangeStart);
        if (auth < 0 || gapStart < 0 || gapEnd - gapStart != 2 * TokenLength + 2 || rangeStart < 0 || rangeEnd < 0)
            throw new InvalidOperationException("The placeholders of the MAC were not found.");
        var end = tailStart + tail.Length;
        var range = $"[0 {tailStart + gapStart} {tailStart + gapEnd} {end - (tailStart + gapEnd)}";
        System.Text.Encoding.ASCII.GetBytes(range.PadRight(rangeEnd - rangeStart)).CopyTo(tail, rangeStart);
        prefix.AppendData(tail, 0, gapStart);
        prefix.AppendData(tail, gapEnd, tail.Length - gapEnd);
        var token = Token(kek, RandomNumberGenerator.GetBytes(32), prefix.GetHashAndReset());
        if (token.Length != TokenLength) throw new InvalidOperationException("The MAC token has not the length reserved for it.");
        System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(token)).CopyTo(tail, gapStart + 1);
    }

    /// <summary>
    /// Attaches the token of a signed revision to the single signer of a CMS block (6.5.2, 6.6.3): the one value of the unsigned attribute
    /// id-attr-pdfMacData; its dataDigest is the digest of the signature's byte range, its signatureDigest that of the signature value. The
    /// signature stays as it is — unsigned attributes lie outside what it signs.
    /// </summary>
    /// <param name="cms">The CMS block of a signature or a document time stamp (SignedData).</param>
    /// <param name="kek">The key encryption key (<see cref="KekOf"/>).</param>
    /// <param name="dataDigest">SHA-256 of the signature's byte range.</param>
    internal static byte[] AttachTo(byte[] cms, byte[] kek, byte[] dataDigest)
    {
        var signed = new SignedCms();
        signed.Decode(cms);
        var signer = signed.SignerInfos[0];
        var token = Token(kek, RandomNumberGenerator.GetBytes(32), dataDigest, SHA256.HashData(signer.GetSignature()));
        signer.AddUnsignedAttribute(new AsnEncodedData(new System.Security.Cryptography.Oid(MacData), token));
        return signed.Encode();
    }

    // ------------------------------------------------------------ checking (ISO/TS 32004, Annex B)

    /// <summary>
    /// Checks the MAC of the newest revision (Annex B): only /AuthCode of the newest trailer counts; standalone tokens are bound to the file
    /// by /ByteRange (from 0 to the end, the gap exactly the hexadecimal /MAC value) and checked as CMS (RFC 5652, 9.3) with the key derived
    /// from the file key. Where in doubt „not checkable“, never wrongly „violated“.
    /// </summary>
    public static MacCheck Verify(PdfDocument doc)
    {
        if (doc.Security is not { } security || security.Revision < 6 || doc.EncryptDictionary is not { } encrypt) return new MacCheck(MacState.None);
        var empty = StandardSecurityHandler.OpensWithoutPassword(encrypt, doc.Trailer.Get(PdfName.ID) as PdfArray);
        var required = StandardSecurityHandler.RequiresMac(security.RawPermissions, security.Revision);
        if (doc.NewestTrailer is not { } newest)
            return doc.Trailer.HasValue(AuthCodeKey) || required
                ? new MacCheck(MacState.NotCheckable, Tr.T("Die Querverweise der Datei mussten repariert werden; die neueste Fassung ist nicht sicher bestimmt."), empty)
                : new MacCheck(MacState.None);
        if (newest.GetDictionary(AuthCodeKey) is not { } authCode)
            return required ? new MacCheck(MacState.MissingButRequired, null, empty) : new MacCheck(MacState.None);
        // only the attachments encrypted and not unlocked: without the file key the MAC key cannot be unwrapped — not checkable, not
        // „violated“ (docs/FEHLERGEDAECHTNIS.md, No. 1099)
        if (!security.HasFileKey)
            return new MacCheck(MacState.NotCheckable, Tr.T("Den Integritätsschutz kann PDF-Knead erst prüfen, wenn die Anhänge mit ihrem Passwort entsperrt sind."), empty);
        try
        {
            var result = Bound(doc, security, encrypt, authCode);
            return result with { EmptyUserPassword = empty };
        }
        catch (Exception e) when (e is AsnContentException or CryptographicException or PdfException or ArgumentException or InvalidOperationException)
        {
            return new MacCheck(MacState.Invalid, Tr.T("Der Integritätsschutz (MAC) ist nicht lesbar."), empty);
        }
    }

    private static readonly PdfName AuthCodeKey = PdfName.Get("AuthCode");

    /// <summary>Where the token stands (B.2.1): in /AuthCode itself or attached to a signature; an unknown place is not checkable.</summary>
    private static MacCheck Bound(PdfDocument doc, StandardSecurityHandler security, PdfDictionary encrypt, PdfDictionary authCode)
    {
        var location = authCode.GetName(PdfName.Get("MACLocation"))?.Value;
        if (location is null) return new MacCheck(MacState.Invalid, Tr.T("Im Integritätsschutz fehlt die Angabe, wo der MAC steht."));
        return location switch
        {
            "Standalone" => Standalone(doc, security, encrypt, authCode),
            "AttachedToSig" => Attached(doc, security, encrypt, authCode),
            _ => new MacCheck(MacState.NotCheckable, Tr.T("Unbekannte Lage des MAC („{0}“).", location)),
        };
    }

    private static MacCheck Standalone(PdfDocument doc, StandardSecurityHandler security, PdfDictionary encrypt, PdfDictionary authCode)
    {
        // /SigObjRef belongs to a token attached to a signature (Table 6)
        if (authCode.HasValue(PdfName.Get("SigObjRef")))
            return new MacCheck(MacState.Invalid, Tr.T("Ein eigenständiger MAC nennt keine Signatur (/SigObjRef)."));

        // the byte range: [0 L1 S L2], to the end of the file, the gap exactly „<“ hexadecimal digits „>“ (5.2.3, Table 6)
        // four integers (Table 6) — a real was truncated and taken (docs/FEHLERGEDAECHTNIS.md, No. 1109)
        if (authCode.GetArray(PdfName.Get("ByteRange")) is not { Count: 4 } range
            || Enumerable.Range(0, 4).Any(i => range.Get(i) is not PdfInteger)
            || range.GetNumber(0) is not 0.0 || range.GetNumber(1) is not { } l1 || range.GetNumber(2) is not { } s || range.GetNumber(3) is not { } l2
            || l1 < 1 || s <= l1 || l2 < 0)
            return new MacCheck(MacState.Invalid, Tr.T("Die Byte-Bereiche des Integritätsschutzes sind ungültig."));
        long start = (long)l1, gapEnd = (long)s, tail = (long)l2;
        if (gapEnd + tail != doc.FileSize)
            return new MacCheck(MacState.Invalid, Tr.T("Der Integritätsschutz deckt nicht die ganze Datei ab — sie wurde danach verändert, oder ein anderes Programm hat ihn beim Anhängen nicht erneuert."));
        var gap = doc.FileBytes(start, Math.Min(gapEnd - start, 1 << 20)).ToArray();
        if (gap.Length != gapEnd - start || gap.Length < 4 || gap[0] != '<' || gap[^1] != '>' || (gap.Length - 2) % 2 != 0
            || gap.AsSpan(1, gap.Length - 2).ContainsAnyExcept("0123456789abcdefABCDEF"u8))
            return new MacCheck(MacState.Invalid, Tr.T("Der MAC-Wert steht nicht genau in der ausgesparten Lücke."));
        var token = Convert.FromHexString(System.Text.Encoding.ASCII.GetString(gap, 1, gap.Length - 2));
        // the gap is the value of /MAC (5.2, Table 6: required with Standalone, the token stands between L1 and S) — a /AuthCode without
        // /MAC, or one whose /MAC is not what the gap holds, was taken (docs/FEHLERGEDAECHTNIS.md, No. 1110)
        if (authCode.GetString(PdfName.Get("MAC"))?.Bytes is not { } macValue)
            return new MacCheck(MacState.Invalid, Tr.T("Im Integritätsschutz fehlt der MAC-Wert (/MAC)."));
        if (!macValue.AsSpan().SequenceEqual(token))
            return new MacCheck(MacState.Invalid, Tr.T("Der MAC-Wert (/MAC) steht nicht in der ausgesparten Lücke."));

        if (Read(token) is { } problem) return problem.Check;
        var parsed = Parse(token, signed: false);
        if (parsed.Problem is { } bad) return bad;
        if (CmsCheck(parsed, security, encrypt) is { } failed) return failed;
        // the binding to the file: the digest of the byte range
        using var hash = IncrementalHash.CreateHash(parsed.Digest);
        Feed(doc, hash, 0, start);
        Feed(doc, hash, gapEnd, tail);
        return hash.GetHashAndReset().AsSpan().SequenceEqual(parsed.DataDigest)
            ? new MacCheck(MacState.Valid)
            : new MacCheck(MacState.Invalid, Tr.T("Die Datei wurde nach dem Schützen verändert."));
    }

    /// <summary>
    /// A token attached to a signature (6.5.2, B.2.3): /AuthCode names the signature dictionary by an indirect reference (/SigObjRef) and has
    /// neither /ByteRange nor /MAC; the signature's /ByteRange covers the whole file but the value of its /Contents; its CMS block has one signer
    /// whose unsigned attributes carry the token as the one value of id-attr-pdfMacData; the token's dataDigest is the digest of that byte range
    /// and its signatureDigest the digest of the signature value (6.6.3). The signature itself is checked elsewhere; the MAC stands on its own.
    /// </summary>
    private static MacCheck Attached(PdfDocument doc, StandardSecurityHandler security, PdfDictionary encrypt, PdfDictionary authCode)
    {
        if (authCode.HasValue(PdfName.Get("ByteRange")) || authCode.HasValue(PdfName.Get("MAC")))
            return new MacCheck(MacState.Invalid, Tr.T("Ein MAC an einer Signatur hat im Integritätsschutz weder /ByteRange noch /MAC."));
        if (authCode.GetRaw(PdfName.Get("SigObjRef")) is not PdfReference reference || doc.Resolve(reference.Id) is not PdfDictionary signature)
            return new MacCheck(MacState.Invalid, Tr.T("Der Integritätsschutz nennt nicht die Signatur, an der der MAC hängt (/SigObjRef)."));
        if (signature.GetArray(PdfName.Get("ByteRange")) is not { Count: 4 } range
            || Enumerable.Range(0, 4).Any(i => range.Get(i) is not PdfInteger)
            || range.GetNumber(0) is not 0.0 || range.GetNumber(1) is not { } l1 || range.GetNumber(2) is not { } s || range.GetNumber(3) is not { } l2
            || l1 < 1 || s <= l1 || l2 < 0)
            return new MacCheck(MacState.Invalid, Tr.T("Die Byte-Bereiche der Signatur mit dem MAC sind ungültig."));
        long start = (long)l1, gapEnd = (long)s, tail = (long)l2;
        if (gapEnd + tail != doc.FileSize)
            return new MacCheck(MacState.Invalid, Tr.T("Der Integritätsschutz deckt nicht die ganze Datei ab — sie wurde danach verändert, oder ein anderes Programm hat ihn beim Anhängen nicht erneuert."));
        // the gap is the value of /Contents, nothing else (B.2.3.2)
        var gap = doc.FileBytes(start, Math.Min(gapEnd - start, 1 << 22)).ToArray();
        if (gap.Length != gapEnd - start || gap.Length < 4 || gap[0] != '<' || gap[^1] != '>'
            || gap.AsSpan(1, gap.Length - 2).ContainsAnyExcept("0123456789abcdefABCDEF"u8))
            return new MacCheck(MacState.Invalid, Tr.T("Zwischen den Byte-Bereichen der Signatur mit dem MAC steht mehr als ihr Signaturblock."));
        if (signature.GetString(PdfName.Contents)?.Bytes is not { Length: > 0 } block)
            return new MacCheck(MacState.Invalid, Tr.T("Die Signatur mit dem MAC hat keinen Signaturblock."));
        var (token, signatureValue, located) = AttachedToken(block);
        if (located is not null) return located;
        if (Read(token!) is { } problem) return problem.Check;
        var parsed = Parse(token!, signed: true);
        if (parsed.Problem is { } bad) return bad;
        if (CmsCheck(parsed, security, encrypt) is { } failed) return failed;
        using var hash = IncrementalHash.CreateHash(parsed.Digest);
        Feed(doc, hash, 0, start);
        Feed(doc, hash, gapEnd, tail);
        if (!hash.GetHashAndReset().AsSpan().SequenceEqual(parsed.DataDigest))
            return new MacCheck(MacState.Invalid, Tr.T("Die Datei wurde nach dem Schützen verändert."));
        return Hash(parsed.Digest, signatureValue).AsSpan().SequenceEqual(parsed.SignatureDigest)
            ? new MacCheck(MacState.Valid)
            : new MacCheck(MacState.Invalid, Tr.T("Der MAC passt nicht zu der Signatur, an der er hängt."));
    }

    /// <summary>
    /// The token among the unsigned attributes of the one signer of a signature's CMS block, and the signature value (6.5.2: the attribute
    /// id-attr-pdfMacData with exactly one value); read as BER, which a signature block may use.
    /// </summary>
    private static (byte[]? Token, byte[] SignatureValue, MacCheck? Problem) AttachedToken(byte[] block)
    {
        var unreadable = new MacCheck(MacState.Invalid, Tr.T("Der Signaturblock mit dem MAC ist nicht lesbar."));
        if (Signatures.CmsSignature.Der(block) is not { } der) return (null, [], unreadable);
        var content = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
        if (content.ReadObjectIdentifier() != SignedData) return (null, [], unreadable);
        var signed = content.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        signed.ReadInteger();
        signed.ReadSetOf();
        signed.ReadSequence();
        if (signed.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) signed.ReadEncodedValue();   // certificates
        if (signed.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1))) signed.ReadEncodedValue();   // revocation data
        var signers = signed.ReadSetOf();
        var signer = signers.ReadSequence();
        if (signers.HasData) return (null, [], new MacCheck(MacState.NotCheckable, Tr.T("Der Signaturblock mit dem MAC hat mehr als einen Unterzeichner.")));
        signer.ReadInteger();
        signer.ReadEncodedValue();   // sid
        signer.ReadSequence();   // digestAlgorithm
        if (signer.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) signer.ReadEncodedValue();   // signedAttrs
        signer.ReadSequence();   // signatureAlgorithm
        var signatureValue = signer.ReadOctetString();
        byte[]? token = null;
        var found = 0;
        if (signer.HasData)
        {
            var unsigned = signer.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 1));
            while (unsigned.HasData)
            {
                var attribute = unsigned.ReadSequence();
                if (attribute.ReadObjectIdentifier() != MacData) continue;
                var values = attribute.ReadSetOf();
                found++;
                token = values.ReadEncodedValue().ToArray();
                if (values.HasData) found++;   // exactly one value
            }
        }
        return found switch
        {
            0 => (null, [], new MacCheck(MacState.Invalid, Tr.T("An der Signatur hängt kein MAC, obwohl der Integritätsschutz es sagt."))),
            1 => (token, signatureValue, null),
            _ => (null, [], new MacCheck(MacState.Invalid, Tr.T("An der Signatur hängt mehr als ein MAC."))),
        };
    }

    /// <summary>
    /// What every token is checked for (B.3; RFC 5652, 9.3): its key unwrapped with the key derived from the file key and /KDFSalt, the MAC over
    /// the authenticated attributes, the message digest of the content; null if they hold.
    /// </summary>
    private static MacCheck? CmsCheck(Token_ parsed, StandardSecurityHandler security, PdfDictionary encrypt)
    {
        if (encrypt.GetString(PdfName.Get("KDFSalt"))?.Bytes is not { Length: 32 } salt)
            return new MacCheck(MacState.Invalid, Tr.T("Der Schlüsselzusatz für den Integritätsschutz (/KDFSalt) fehlt oder hat nicht 32 Byte."));
        if (Unwrap(security.MacKek(salt), parsed.EncryptedKey) is not { } macKey)
            return new MacCheck(MacState.Invalid, Tr.T("Der Schlüssel des MAC passt nicht zur Datei."));
        // the MAC over the authenticated attributes as SET OF (tag 31 instead of A2)
        var attrSet = parsed.AuthAttrs.ToArray();
        attrSet[0] = 0x31;
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(macKey, attrSet), parsed.Mac))
            return new MacCheck(MacState.Invalid, Tr.T("Der MAC stimmt nicht."));
        if (!Hash(parsed.Digest, parsed.EContent).AsSpan().SequenceEqual(parsed.MessageDigest))
            return new MacCheck(MacState.Invalid, Tr.T("Der MAC stimmt nicht."));
        return null;
    }

    private static void Feed(PdfDocument doc, IncrementalHash hash, long at, long length)
    {
        const int chunk = 1 << 20;
        for (var done = 0L; done < length; done += chunk) hash.AppendData(doc.FileBytes(at + done, (int)Math.Min(chunk, length - done)));
    }

    private static byte[] Hash(HashAlgorithmName name, byte[] data)
    {
        using var hash = IncrementalHash.CreateHash(name);
        hash.AppendData(data);
        return hash.GetHashAndReset();
    }

    /// <summary>The DER form: exactly one value, nothing after it (the gap is free of trailing data).</summary>
    private static (MacCheck Check, bool)? Read(byte[] token)
    {
        var reader = new AsnReader(token, AsnEncodingRules.DER);
        reader.ReadEncodedValue();
        return reader.HasData ? (new MacCheck(MacState.Invalid, Tr.T("Nach dem MAC stehen weitere Daten.")), true) : null;
    }

    private sealed record Token_(HashAlgorithmName Digest, byte[] EncryptedKey, byte[] EContent, byte[] DataDigest, byte[] MessageDigest,
        ReadOnlyMemory<byte> AuthAttrs, byte[] Mac, MacCheck? Problem, byte[]? SignatureDigest = null);

    private static Token_ Failed(MacState state, string detail) => new(default, [], [], [], [], default, [], new MacCheck(state, detail));

    /// <summary>Reads the token as AuthenticatedData (RFC 5652, 9.1) with the conditions of ISO/TS 32004, 6.3.</summary>
    /// <param name="token">The token.</param>
    /// <param name="signed">Attached to a signature: the signature digest is required (6.6.3); standalone it must be absent (6.6.2).</param>
    private static Token_ Parse(byte[] token, bool signed)
    {
        var invalid = Tr.T("Der Integritätsschutz (MAC) entspricht nicht ISO/TS 32004.");
        var outer = new AsnReader(token, AsnEncodingRules.DER).ReadSequence();
        if (outer.ReadObjectIdentifier() != AuthData) return Failed(MacState.Invalid, invalid);
        var content = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        var data = content.ReadSequence();
        if (data.ReadInteger() is var version && version != 0) return Failed(MacState.NotCheckable, invalid);
        if (data.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) data.ReadEncodedValue();   // originatorInfo: not used
        var recipients = data.ReadSetOf();
        if (!recipients.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 3))) return Failed(MacState.NotCheckable, invalid);
        var pwri = recipients.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 3));
        if (recipients.HasData) return Failed(MacState.Invalid, invalid);   // exactly one recipient (6.3.3)
        pwri.ReadInteger();
        if (!pwri.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) return Failed(MacState.NotCheckable, invalid);
        var kdf = pwri.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        if (kdf.ReadObjectIdentifier() != WrapKdf || kdf.HasData) return Failed(MacState.NotCheckable, Tr.T("Der MAC nutzt eine andere Schlüsselableitung."));
        var wrap = pwri.ReadSequence();
        if (wrap.ReadObjectIdentifier() != Aes256Wrap) return Failed(MacState.NotCheckable, Tr.T("Der MAC nutzt ein anderes Schlüssel-Verpackungsverfahren."));
        var encryptedKey = pwri.ReadOctetString();
        var macAlgorithm = data.ReadEncodedValue().ToArray();
        if (OidOf(macAlgorithm) != HmacSha256) return Failed(MacState.NotCheckable, Tr.T("Der MAC nutzt ein anderes Verfahren als HMAC-SHA-256."));
        if (!data.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1))) return Failed(MacState.Invalid, invalid);
        var digestAlgorithm = data.ReadEncodedValue().ToArray();
        var digestOid = OidOf(digestAlgorithm, implicitTag: true);
        if (digestOid is null || !Digests.TryGetValue(digestOid, out var digest)) return Failed(MacState.NotCheckable, Tr.T("Der MAC nutzt ein unbekanntes Prüfsummenverfahren."));
        if ((digest == HashAlgorithmName.SHA3_256 && !SHA3_256.IsSupported) || (digest == HashAlgorithmName.SHA3_384 && !SHA3_384.IsSupported)
            || (digest == HashAlgorithmName.SHA3_512 && !SHA3_512.IsSupported))
            return Failed(MacState.NotCheckable, Tr.T("Das Prüfsummenverfahren des MAC wird auf diesem System nicht unterstützt."));
        var encap = data.ReadSequence();
        if (encap.ReadObjectIdentifier() != IntegrityInfo) return Failed(MacState.Invalid, invalid);
        var eContent = encap.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadOctetString();
        var info = new AsnReader(eContent, AsnEncodingRules.DER).ReadSequence();
        if (info.ReadInteger() != 0) return Failed(MacState.Invalid, invalid);
        var dataDigest = info.ReadOctetString();
        byte[]? signatureDigest = null;
        if (signed)
        {
            if (!info.HasData || !info.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
                return Failed(MacState.Invalid, Tr.T("Im MAC an der Signatur fehlt die Prüfsumme der Signatur (signatureDigest)."));
            signatureDigest = info.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0));
        }
        if (info.HasData) return Failed(MacState.Invalid, invalid);   // a signature digest has no place in a standalone token (6.6.2)
        if (!data.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2))) return Failed(MacState.Invalid, invalid);
        var authAttrs = data.ReadEncodedValue();
        var mac = data.ReadOctetString();
        if (data.HasData) return Failed(MacState.Invalid, invalid);   // no unauthenticated attributes (6.3.7)

        // exactly one content type (the PdfMacIntegrityInfo) and one message digest; an algorithm protection must match (6.3.6)
        byte[]? messageDigest = null;
        int contentTypes = 0, digests = 0, protections = 0;
        var set = new AsnReader(authAttrs, AsnEncodingRules.DER).ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 2));
        while (set.HasData)
        {
            var attribute = set.ReadSequence();
            var type = attribute.ReadObjectIdentifier();
            var values = attribute.ReadSetOf();
            switch (type)
            {
                case ContentType:
                    contentTypes++;
                    if (values.ReadObjectIdentifier() != IntegrityInfo || values.HasData) return Failed(MacState.Invalid, invalid);
                    break;
                case MessageDigest:
                    digests++;
                    messageDigest = values.ReadOctetString();
                    if (values.HasData) return Failed(MacState.Invalid, invalid);
                    break;
                case AlgorithmProtection:
                    protections++;
                    var protection = values.ReadSequence();
                    var protectedDigest = protection.ReadEncodedValue().ToArray();
                    var protectedMac = protection.HasData ? protection.ReadEncodedValue().ToArray() : null;
                    if (values.HasData || protection.HasData || OidOf(protectedDigest) != digestOid || protectedMac is null
                        || !new Asn1Tag(TagClass.ContextSpecific, 2).HasSameClassAndValue(Asn1Tag.Decode(protectedMac, out _))
                        || OidOf(protectedMac, implicitTag: true) != HmacSha256)
                        return Failed(MacState.Invalid, Tr.T("Die Angabe der Verfahren im MAC passt nicht zu den verwendeten."));
                    break;
            }
        }
        if (contentTypes != 1 || digests != 1 || protections > 1) return Failed(MacState.Invalid, invalid);
        return new Token_(digest, encryptedKey, eContent, dataDigest, messageDigest!, authAttrs, mac, null, signatureDigest);
    }

    /// <summary>The algorithm of an AlgorithmIdentifier (SEQUENCE, or implicitly tagged), parameters absent or NULL; null otherwise.</summary>
    private static string? OidOf(byte[] encoded, bool implicitTag = false)
    {
        var reader = new AsnReader(encoded, AsnEncodingRules.DER);
        var sequence = implicitTag ? reader.ReadSequence(Asn1Tag.Decode(encoded, out _)) : reader.ReadSequence();
        var oid = sequence.ReadObjectIdentifier();
        if (sequence.HasData) sequence.ReadNull();
        return sequence.HasData ? null : oid;
    }
}
