using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Security;

/// <summary>
/// Public-key security handlers (ISO 32000-2, 7.6.5): the document is encrypted for recipients — each list of recipients is a CMS
/// EnvelopedData object (RFC 5652) holding a 20-byte seed and 4 bytes of permissions, encrypted for the certificates of that list. The file key
/// is the digest of the seed and all objects of the list (SHA-1, or SHA-256 for 256-bit AES; 7.6.5.3); strings and streams are then encrypted
/// as with the standard handler (algorithm 1 or 1.A), so the handler is the same class with another way to its key (design decision,
/// 24.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 1117).
/// </summary>
public sealed partial class StandardSecurityHandler
{
    private const string S3 = "adbe.pkcs7.s3", S4 = "adbe.pkcs7.s4", S5 = "adbe.pkcs7.s5";

    /// <summary>The OID of 256-bit AES in CBC mode (the only method for the enveloped data that is not deprecated in PDF 2.0, 7.6.5.3).</summary>
    private const string Aes256CbcOid = "2.16.840.1.101.3.4.1.42";

    /// <summary>True if the document is encrypted for recipients (a public-key handler) rather than with passwords.</summary>
    public bool IsPublicKey { get; private init; }

    /// <summary>The name of the public-key handler as the file gives it (/Filter, e.g. Adobe.PubSec); null for the standard handler.</summary>
    public string? PublicKeyFilter { get; private init; }

    /// <summary>
    /// Whether an encryption dictionary belongs to a public-key handler: another /Filter than Standard with a /SubFilter of 7.6.5.2
    /// (adbe.pkcs7.s3, s4, s5) — or, without /SubFilter, with recipients (/Recipients, or crypt filters naming them).
    /// </summary>
    /// <param name="encrypt">The encryption dictionary.</param>
    public static bool IsPublicKeyEncryption(PdfDictionary encrypt) =>
        encrypt.GetName(PdfName.Filter)?.Value is { } filter && filter != "Standard" && PublicKeySubFilter(encrypt) is not null;

    private static string? PublicKeySubFilter(PdfDictionary encrypt)
    {
        if (encrypt.GetName(PdfName.Get("SubFilter"))?.Value is (S3 or S4 or S5) and var named) return named;
        if (encrypt.HasValue(PdfName.Get("SubFilter"))) return null;
        // no /SubFilter (files of that kind exist): recipients in crypt filters are s5, in the dictionary itself s4 (s3 only with V 1)
        if ((encrypt.GetInteger(PdfName.Get("V")) ?? 0) >= 4 && encrypt.GetDictionary(PdfName.Get("CF")) is { } cf
            && cf.Entries.Any(e => cf.GetDictionary(e.Key)?.HasValue(PdfName.Get("Recipients")) == true))
            return S5;
        return encrypt.HasValue(PdfName.Get("Recipients")) ? (encrypt.GetInteger(PdfName.Get("V")) ?? 0) <= 1 ? S3 : S4 : null;
    }

    /// <summary>
    /// Opens a document encrypted for recipients (7.6.5.1): the recipient lists are tried in their order — the first one a certificate with
    /// its private key opens counts (Table 23: the permissions of the first matching list) — with the certificates given and those of the
    /// user's certificate store. The seed and the permissions come from the enveloped data, the file key from the seed and all objects of
    /// that list (7.6.5.3).
    /// </summary>
    /// <param name="encrypt">The encryption dictionary.</param>
    /// <param name="id">The /ID of the trailer (kept for updates; the key does not depend on it).</param>
    /// <param name="certificates">Certificates with private keys to try besides the store (e.g. from a .p12 file); null for none.</param>
    /// <exception cref="PdfRecipientException">No certificate at hand opens any list.</exception>
    /// <exception cref="PdfException">Incomplete or unsupported encryption data.</exception>
    private static StandardSecurityHandler OpenPublicKey(PdfDictionary encrypt, PdfArray? id, IReadOnlyList<X509Certificate2>? certificates)
    {
        var filter = encrypt.GetName(PdfName.Filter)!.Value;
        var subFilter = PublicKeySubFilter(encrypt)!;
        var v = (int)(encrypt.GetInteger(PdfName.Get("V")) ?? 0);
        var notes = new List<string>();
        if (!encrypt.HasValue(PdfName.Get("SubFilter")))
            notes.Add(Tr.T("In der Verschlüsselung für Empfänger fehlt /SubFilter — gelesen als {0}.", subFilter));
        // s3 and s4 without crypt filters, s5 with them (7.6.5.2)
        if (subFilter == S5 ? v is not (4 or 5) : v is not (1 or 2 or 3))
            throw new PdfException(Tr.T("Verschlüsselung für Empfänger: {0} passt nicht zu Version {1}.", subFilter, v));
        var encryptMetadata = v < 4 || (encrypt.GetBoolean(PdfName.Get("EncryptMetadata")) ?? true);
        var (stringCipher, streamCipher) = Ciphers(encrypt, v, notes);
        var (filters, embedded) = Filters(encrypt, v, streamCipher, notes);
        var named = new Dictionary<string, PdfCipher>(filters);

        PdfArray? lists;
        int keyBits;
        if (subFilter == S5)
        {
            // the recipients stand in the crypt filters (Table 27): that of the streams, else of the strings, else of the attachments
            var cf = encrypt.GetDictionary(PdfName.Get("CF"));
            var used = new[] { encrypt.GetName(PdfName.Get("StmF")), encrypt.GetName(PdfName.Get("StrF")), encrypt.GetName(PdfName.Get("EFF")) }
                .Where(n => n is not null && n.Value != "Identity").Select(n => cf?.GetDictionary(n!)).OfType<PdfDictionary>().ToList();
            var first = used.FirstOrDefault(d => d.HasValue(PdfName.Get("Recipients")));
            lists = RecipientsOf(first?.Get(PdfName.Get("Recipients")));
            // recipients in the dictionary itself instead of the crypt filter (s4 style, Table 23 asks for the filter with s5): read, and said
            if (lists is null && RecipientsOf(encrypt.Get(PdfName.Get("Recipients"))) is { } outside)
            {
                lists = outside;
                notes.Add(Tr.T("Die Empfänger stehen nicht im Kryptfilter, sondern im Verschlüsselungswörterbuch — so gelesen."));
            }
            // one key for the document: filters with other recipients would need keys of their own — said, not guessed
            if (used.Any(d => d != first && d.HasValue(PdfName.Get("Recipients")) && !SameRecipients(lists, RecipientsOf(d.Get(PdfName.Get("Recipients"))))))
                throw new PdfException(Tr.T("Die Kryptfilter dieser Datei nennen verschiedene Empfängerlisten; das wird nicht unterstützt."));
            // a filter only single streams name (/Crypt) with recipients of its own: its data would decrypt to garbage with the document's key
            // — the filter is left out, such a stream is reported as not decryptable (second M4 probe, A3; docs/FEHLERGEDAECHTNIS.md, No. 1119)
            if (cf is not null)
                foreach (var (name, _) in cf.Entries)
                    if (cf.GetDictionary(name) is { } other && !used.Contains(other) && other.HasValue(PdfName.Get("Recipients"))
                        && !SameRecipients(lists, RecipientsOf(other.Get(PdfName.Get("Recipients")))))
                    {
                        named.Remove(name.Value);
                        notes.Add(Tr.T("Der Kryptfilter „{0}“ nennt eine eigene Empfängerliste; das wird nicht unterstützt, Datenströme mit ihm sind nicht lesbar.", name.Value));
                    }
            // the crypt filter's /Length counts bits for public-key handlers (Table 25 with pdf-issues 184); a small value is taken as bytes
            var length = first?.GetInteger(PdfName.Length) ?? 128;
            keyBits = (int)(length < 40 ? length * 8 : length);
            encryptMetadata = first?.GetBoolean(PdfName.Get("EncryptMetadata")) ?? encryptMetadata;
        }
        else
        {
            lists = RecipientsOf(encrypt.Get(PdfName.Get("Recipients")));
            keyBits = (int)(encrypt.GetInteger(PdfName.Length) ?? 40);
        }
        if (lists is null || lists.Count == 0)
            throw new PdfException(Tr.T("In der Verschlüsselung für Empfänger ({0}) fehlt die Liste der Empfänger (/Recipients).", filter));

        // the first list a certificate at hand opens (the store of the user and the certificates given)
        var extra = new X509Certificate2Collection();
        foreach (var certificate in certificates ?? []) extra.Add(certificate);
        byte[]? content = null;
        for (var i = 0; i < Math.Min(lists.Count, 256) && content is null; i++)
        {
            if (lists.Get(i) is not PdfString cms) continue;
            try
            {
                var envelope = new EnvelopedCms();
                envelope.Decode(cms.Bytes);
                envelope.Decrypt(extra);
                content = envelope.ContentInfo.Content;
            }
            catch (CryptographicException)
            {
                // not for this user, or not readable: the next list
            }
        }
        if (content is null)
        {
            // only the attachments encrypted (/StmF and /StrF Identity, /EFF a filter, authorised when an attachment opens): the document opens
            // without a certificate, the attachments stay locked — as with the standard handler (7.6.6, Table 25; second M4 probe, A4;
            // docs/FEHLERGEDAECHTNIS.md, No. 1119)
            if (stringCipher == PdfCipher.None && streamCipher == PdfCipher.None && embedded != PdfCipher.None
                && encrypt.GetName(PdfName.Get("EFF")) is { Value: not "Identity" } && AuthorisedWhenAttachmentOpens(encrypt))
            {
                var locked = new StandardSecurityHandler([], subFilter == S3 ? 2 : 4, PdfCipher.None, PdfCipher.None, encryptMetadata, PdfPermissions.All, false)
                {
                    AttachmentsLocked = true,
                    IsPublicKey = true,
                    PublicKeyFilter = filter,
                    RawPermissions = -1,
                };
                locked._named = named;
                locked.EmbeddedFileCipher = embedded;
                locked.Notes = notes.Distinct().ToList();
                return locked;
            }
            throw new PdfRecipientException(filter);
        }
        if (content.Length < 20) throw new PdfException(Tr.T("Die Daten für den Empfänger sind unvollständig."));

        var aes256 = streamCipher == PdfCipher.Aes256 || stringCipher == PdfCipher.Aes256 || embedded == PdfCipher.Aes256;
        var key = PublicKeyFileKey(content.AsSpan(0, 20), lists, encryptMetadata, aes256, keyBits);
        // 4 bytes of permissions, most significant first (absent only in the data of a stream's own crypt filter, 7.6.5.3) — absent here, at the
        // level of the document, the data are damaged: no permission beyond viewing rather than all (second M4 probe, A3; No. 1119)
        int? permissions = content.Length >= 24 ? BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(20, 4)) : null;
        if (permissions is null)
        {
            notes.Add(Tr.T("In den Daten für den Empfänger fehlen die Rechte — die Datei wird nur zum Ansehen geöffnet."));
            permissions = 0;
        }
        // the seed is key material: cleared once the key is derived (docs/FEHLERGEDAECHTNIS.md, No. 1118)
        CryptographicOperations.ZeroMemory(content);
        // bit 2: change of the encryption and every other permission (Table 24); only bits 2–6 and 9–12 count
        var full = (permissions.Value & 2) != 0;
        var granted = full ? PdfPermissions.All : (PdfPermissions)permissions.Value & PdfPermissions.All;
        // s3 has the permissions of revision 2 (7.6.5.3); the others those of revisions 3 and up — the revision stands for that and for the
        // key handling (256-bit AES as revision 6)
        var revision = subFilter == S3 ? 2 : aes256 ? 6 : subFilter == S4 ? 3 : 4;
        var handler = new StandardSecurityHandler(key, revision, stringCipher, streamCipher, encryptMetadata, granted, full)
        {
            IsPublicKey = true,
            PublicKeyFilter = filter,
            FileId = id is { Count: > 0 } && id.Get(0) is PdfString first0 ? first0.Bytes : [],
            // bit 13 (ISO/TS 32004: a MAC required) does not belong to these permissions — set, so none is asked for
            RawPermissions = permissions.Value | (1 << 12),
        };
        handler._named = named;
        handler.EmbeddedFileCipher = embedded;
        handler.Notes = notes.Distinct().ToList();
        return handler;
    }

    /// <summary>The file key (7.6.5.3): the digest of the seed, every object of the recipient list in its order and, where the metadata stay
    /// plain, four bytes 0xFF — SHA-256 for 256-bit AES, else SHA-1 cut to the key length.</summary>
    internal static byte[] PublicKeyFileKey(ReadOnlySpan<byte> seed, PdfArray lists, bool encryptMetadata, bool aes256, int keyBits)
    {
        using var hash = IncrementalHash.CreateHash(aes256 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        hash.AppendData(seed);
        for (var i = 0; i < lists.Count; i++)
            if (lists.Get(i) is PdfString cms) hash.AppendData(cms.Bytes);
        if (!encryptMetadata) hash.AppendData([0xFF, 0xFF, 0xFF, 0xFF]);
        var digest = hash.GetHashAndReset();
        if (aes256) return digest;
        var key = digest[..Math.Clamp(keyBits / 8, 5, 16)];
        CryptographicOperations.ZeroMemory(digest);
        return key;
    }

    /// <summary>/Recipients: an array of byte strings, or one byte string (Table 27).</summary>
    private static PdfArray? RecipientsOf(PdfObject? value) => value switch
    {
        PdfArray array => array,
        PdfString one => new PdfArray([one]),
        _ => null,
    };

    private static bool SameRecipients(PdfArray? a, PdfArray? b) =>
        a is not null && b is not null && a.Count == b.Count
        && Enumerable.Range(0, a.Count).All(i => a.Get(i) is PdfString x && b.Get(i) is PdfString y && x.Bytes.AsSpan().SequenceEqual(y.Bytes));

    /// <summary>
    /// A new encryption for recipients (7.6.5): adbe.pkcs7.s5 with one crypt filter of 256-bit AES (V 5, AESV3; the only method not deprecated
    /// in PDF 2.0) for strings, streams and attachments; per group one CMS object with the same random seed and the group's permissions,
    /// its content encrypted with 256-bit AES for the certificates of the group (RSA key transport). The file key is SHA-256 of the seed and
    /// all objects (7.6.5.3). Bit 10 is always set (Table 24: writers shall), bit 2 for a group with full access.
    /// </summary>
    /// <param name="groups">The recipient groups: their certificates (public keys suffice) and permissions; at least one.</param>
    /// <param name="fileId">The first part of /ID of the new file (kept for updates).</param>
    /// <exception cref="PdfException">No group, a group without certificates, or a certificate without an RSA key.</exception>
    public static (PdfDictionary Encrypt, StandardSecurityHandler Handler) CreateForRecipients(IReadOnlyList<PdfRecipientGroup> groups, byte[] fileId)
    {
        if (groups.Count == 0) throw new PdfException(Tr.T("Für die Verschlüsselung für Empfänger fehlt ein Empfänger."));
        // one CMS object per set of permissions (Table 23): groups with the same permissions become one, in the order they first appear
        // (second M4 probe; docs/FEHLERGEDAECHTNIS.md, No. 1119)
        groups = groups.GroupBy(g => (Permissions: (g.Permissions & PdfPermissions.All) | PdfPermissions.Accessibility, g.FullAccess))
            .Select(same => new PdfRecipientGroup(same.SelectMany(g => g.Certificates).ToList(), same.Key.Permissions, same.Key.FullAccess)).ToList();
        var seed = RandomNumberGenerator.GetBytes(20);
        var lists = new PdfArray();
        foreach (var group in groups)
        {
            if (group.Certificates.Count == 0) throw new PdfException(Tr.T("Eine Gruppe von Empfängern hat kein Zertifikat."));
            // bits 2–6 and 9–12 as Table 24 defines them, bit 10 always; the rest set as the standard handler does (they are ignored)
            var bits = unchecked((int)0xFFFFF0C0) | (int)(group.Permissions & PdfPermissions.All) | (int)PdfPermissions.Accessibility | (group.FullAccess ? 2 : 0);
            var data = new byte[24];
            seed.CopyTo(data, 0);
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(20), bits);
            var envelope = new EnvelopedCms(new ContentInfo(data), new AlgorithmIdentifier(new Oid(Aes256CbcOid)));
            var recipients = new CmsRecipientCollection();
            foreach (var certificate in group.Certificates)
            {
                if (certificate.GetRSAPublicKey() is null)
                    throw new PdfException(Tr.T("Das Zertifikat „{0}“ hat keinen RSA-Schlüssel; für Empfänger wird nur mit RSA verschlüsselt.",
                        certificate.GetNameInfo(X509NameType.SimpleName, false)));
                recipients.Add(new CmsRecipient(SubjectIdentifierType.IssuerAndSerialNumber, certificate, RSAEncryptionPadding.Pkcs1));
            }
            envelope.Encrypt(recipients);
            lists.Add(new PdfString(envelope.Encode(), isHex: true));
            CryptographicOperations.ZeroMemory(data);
        }
        var key = PublicKeyFileKey(seed, lists, encryptMetadata: true, aes256: true, 256);
        CryptographicOperations.ZeroMemory(seed);

        var filter = new PdfDictionary();
        filter.Set(PdfName.Get("CFM"), PdfName.Get("AESV3"));
        filter.Set(PdfName.Get("AuthEvent"), PdfName.Get("DocOpen"));
        // in bits for public-key handlers (Table 25 with pdf-issues 184)
        filter.Set(PdfName.Length, PdfInteger.Of(256));
        filter.Set(PdfName.Get("Recipients"), lists);
        filter.Set(PdfName.Get("EncryptMetadata"), PdfBoolean.True);
        var cf = new PdfDictionary();
        cf.Set(PdfName.Get("DefaultCryptFilter"), filter);
        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("Adobe.PubSec"));
        dict.Set(PdfName.Get("SubFilter"), PdfName.Get(S5));
        dict.Set(PdfName.Get("V"), PdfInteger.Of(5));
        dict.Set(PdfName.Get("CF"), cf);
        dict.Set(PdfName.Get("StmF"), PdfName.Get("DefaultCryptFilter"));
        dict.Set(PdfName.Get("StrF"), PdfName.Get("DefaultCryptFilter"));

        var handler = new StandardSecurityHandler(key, 6, PdfCipher.Aes256, PdfCipher.Aes256, true, PdfPermissions.All, true)
        {
            IsPublicKey = true,
            PublicKeyFilter = "Adobe.PubSec",
            FileId = fileId,
            RawPermissions = -1,
        };
        handler._named = new Dictionary<string, PdfCipher> { ["DefaultCryptFilter"] = PdfCipher.Aes256 };
        handler.EmbeddedFileCipher = PdfCipher.Aes256;
        return (dict, handler);
    }
}

/// <summary>A group of recipients of a document encrypted with certificates (ISO 32000-2, 7.6.5): the same permissions for all of them.</summary>
/// <param name="Certificates">The recipients' certificates (public keys suffice).</param>
/// <param name="Permissions">What they may do.</param>
/// <param name="FullAccess">Every permission and the change of the encryption (Table 24, bit 2).</param>
public sealed record PdfRecipientGroup(IReadOnlyList<X509Certificate2> Certificates, PdfPermissions Permissions, bool FullAccess = false);
