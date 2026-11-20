using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Security;

/// <summary>
/// Permissions from /P (ISO 32000-2, Table 22). Without an open password they are only a request to the viewer;
/// only encryption with a password actually protects the content.
/// </summary>
/// <remarks>The values are the bit masks of /P (bit 3 = 1 &lt;&lt; 2 …).</remarks>
[Flags]
public enum PdfPermissions
{
    /// <summary>No permissions.</summary>
    None = 0,
    /// <summary>Bit 3: print.</summary>
    Print = 1 << 2,
    /// <summary>Bit 4: modify the content.</summary>
    Modify = 1 << 3,
    /// <summary>Bit 5: copy or extract text and graphics.</summary>
    Copy = 1 << 4,
    /// <summary>Bit 6: add or modify annotations, fill in form fields.</summary>
    Annotate = 1 << 5,
    /// <summary>Bit 9: fill in form fields, even without <see cref="Annotate"/>.</summary>
    FillForms = 1 << 8,
    /// <summary>Bit 10: extract text and graphics for accessibility.</summary>
    Accessibility = 1 << 9,
    /// <summary>Bit 11: assemble the document (insert, rotate, delete pages, bookmarks, thumbnails).</summary>
    Assemble = 1 << 10,
    /// <summary>Bit 12: print in high quality.</summary>
    PrintHighQuality = 1 << 11,
    /// <summary>All permissions.</summary>
    All = Print | Modify | Copy | Annotate | FillForms | Accessibility | Assemble | PrintHighQuality,
}

/// <summary>Method with which strings and streams are encrypted.</summary>
public enum PdfCipher
{
    /// <summary>Not encrypted (Identity crypt filter).</summary>
    None,
    /// <summary>RC4.</summary>
    Rc4,
    /// <summary>AES with a 128-bit key (AESV2).</summary>
    Aes128,
    /// <summary>AES with a 256-bit key (AESV3).</summary>
    Aes256,
    /// <summary>AES-GCM with a 256-bit key (AESV4, ISO/TS 32003): the file key for every object, 12-byte nonce in front, 16-byte tag behind.</summary>
    Aes256Gcm,
}

/// <summary>
/// Standard security handler (ISO 32000-2, 7.6.4): RC4 40–128 bit (R2–R4), AES-128 (R4, AESV2), AES-256 (R5 Adobe
/// extension, R6 PDF 2.0, AESV3). Checks the user and owner passwords, derives the file key and decrypts and
/// encrypts strings and streams. Own implementation following the standard; MD5, SHA-2 and AES come from .NET.
/// </summary>
/// <remarks>Immutable after creation; decryption and encryption may be called from several threads.</remarks>
public sealed class StandardSecurityHandler
{
    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private static readonly byte[] SaltAes = "sAlT"u8.ToArray();

    private readonly byte[] _key;

    /// <summary>The key the data of the file is read with: the file key, or that padded for reading (<see cref="PaddedForReading"/>).</summary>
    private byte[] _readKey;

    private StandardSecurityHandler(byte[] key, int revision, PdfCipher stringCipher, PdfCipher streamCipher,
        bool encryptMetadata, PdfPermissions permissions, bool isOwner)
    {
        _key = key;
        _readKey = key;
        Revision = revision;
        StringCipher = stringCipher;
        StreamCipher = streamCipher;
        EncryptMetadata = encryptMetadata;
        // bit 10 (text for assistive technology) is always granted: readers ignore it (ISO 32000-2, 7.6.4.2, Table 22) — a file without it
        // is not shown as restricted (docs/FEHLERGEDAECHTNIS.md, No. 490)
        Permissions = permissions | PdfPermissions.Accessibility;
        IsOwner = isOwner;
        // the reserved bits set as ISO 32000-2, Table 22 asks, bit 13 with them (no MAC required); the opening code sets the bits it read
        RawPermissions = unchecked((int)0xFFFFF0C0) | (int)permissions;
    }

    /// <summary>The revision of the handler (/R).</summary>
    public int Revision { get; }

    /// <summary>The method for strings (/StrF).</summary>
    public PdfCipher StringCipher { get; }

    /// <summary>The method for streams (/StmF).</summary>
    public PdfCipher StreamCipher { get; }

    /// <summary>
    /// The method for embedded files without a crypt filter of their own (/EFF; without it the method for streams, ISO 32000-2, 7.6.6,
    /// Table 20). Files that encrypt only their attachments have /StmF Identity and /EFF naming a filter (docs/FEHLERGEDAECHTNIS.md, No. 442).
    /// </summary>
    public PdfCipher EmbeddedFileCipher { get; private set; }

    // the crypt filters of /CF by name (V 4 and 5); a stream naming one in its /Crypt filter uses it (ISO 32000-2, 7.4.10)
    private IReadOnlyDictionary<string, PdfCipher> _named = new Dictionary<string, PdfCipher>();

    /// <summary>The method of a crypt filter named by a stream (/Crypt with /DecodeParms /Name; Identity or no name: none).</summary>
    /// <param name="name">The name; null for the default /Identity.</param>
    /// <returns>The method; null if /CF does not name it (every filter used must be there, Table 20).</returns>
    internal PdfCipher? CipherNamed(PdfName? name) =>
        name is null || name.Value == "Identity" ? PdfCipher.None : _named.TryGetValue(name.Value, out var cipher) ? cipher : null;

    // new protection: one filter for everything, /StdCF (embedded files as all streams)
    private StandardSecurityHandler WithStandardFilter(PdfCipher cipher)
    {
        _named = new Dictionary<string, PdfCipher> { ["StdCF"] = cipher };
        EmbeddedFileCipher = cipher;
        return this;
    }

    // the filters of the /Encrypt dictionary taken over by a copy of this handler (other permissions, the same keys and methods)
    private StandardSecurityHandler WithFiltersOf(StandardSecurityHandler other)
    {
        _named = other._named;
        EmbeddedFileCipher = other.EmbeddedFileCipher;
        return this;
    }

    /// <summary>True if metadata streams are encrypted as well (/EncryptMetadata, default true).</summary>
    public bool EncryptMetadata { get; }

    /// <summary>The permissions; <see cref="PdfPermissions.All"/> when opened with the owner password.</summary>
    public PdfPermissions Permissions { get; }

    /// <summary>True if opened with the owner password, which grants all permissions.</summary>
    public bool IsOwner { get; }

    /// <summary>Key length in bits, for display.</summary>
    public int KeyBits => _key.Length * 8;

    /// <summary>Up to revision 4: the padded user password (derived from /O when the owner password was given), for <see cref="WithAllPermissions"/>.</summary>
    private byte[]? PaddedUser { get; init; }

    /// <summary>
    /// First part of /ID with which the key was derived (revisions 2–4; empty otherwise). Code that keeps the encryption
    /// must write exactly this one, not the one from the trailer: after a repair, a different (damaged) one can be there,
    /// and the copy would then ask for a password. See docs/FEHLERGEDAECHTNIS.md, No. 89.
    /// </summary>
    public byte[] FileId { get; private init; } = [];

    // ------------------------------------------------------------ Opening

    /// <summary>
    /// Checks the password and sets up decryption. Without a password, the empty user password is tried (most
    /// "protected" PDFs only restrict permissions and have no open password).
    /// </summary>
    /// <param name="encrypt">The encryption dictionary.</param>
    /// <param name="id">The /ID array of the trailer; its first part enters the key up to revision 4.</param>
    /// <param name="password">The password (user or owner); null or empty for none.</param>
    /// <returns>The handler. If only embedded files are encrypted and the password does not match, a handler with
    /// <see cref="AttachmentsLocked"/> set and no encryption.</returns>
    /// <exception cref="PdfPasswordException">A password is required or the given one is wrong.</exception>
    /// <exception cref="PdfException">Unsupported method or incomplete encryption data.</exception>
    public static StandardSecurityHandler Open(PdfDictionary encrypt, PdfArray? id, string? password)
    {
        var filter = encrypt.GetName(PdfName.Filter)?.Value;
        if (filter != "Standard")
            throw new PdfException(Tr.T("Verschlüsselung „{0}“ wird nicht unterstützt (nur Passwortschutz).", filter ?? "?"));

        var v = (int)(encrypt.GetInteger(PdfName.Get("V")) ?? 0);
        var r = (int)(encrypt.GetInteger(PdfName.Get("R")) ?? 0);
        var o = encrypt.GetString(PdfName.Get("O"))?.Bytes ?? [];
        var u = encrypt.GetString(PdfName.Get("U"))?.Bytes ?? [];
        var p = (int)(encrypt.GetInteger(PdfName.Get("P")) ?? -1);
        var encryptMetadata = encrypt.GetBoolean(PdfName.Get("EncryptMetadata")) ?? true;
        var permissions = (PdfPermissions)p & PdfPermissions.All;

        var notes = new List<string>();
        var (stringCipher, streamCipher) = Ciphers(encrypt, v, notes);
        var (named, embedded) = Filters(encrypt, v, streamCipher, notes);
        StandardSecurityHandler handler;
        try
        {
            handler = OpenWithPassword(encrypt, id, password, r, o, u, p, encryptMetadata, permissions, stringCipher, streamCipher);
        }
        catch (PdfPasswordException) when (stringCipher == PdfCipher.None && streamCipher == PdfCipher.None)
        {
            // Only embedded files are encrypted (/EFF, AuthEvent EFOpen): the document itself is readable;
            // the password is needed only to open an attachment.
            handler = new StandardSecurityHandler([], r, PdfCipher.None, PdfCipher.None, encryptMetadata, permissions, false)
            {
                AttachmentsLocked = true,
            };
        }
        handler._named = named;
        handler.EmbeddedFileCipher = embedded;
        handler.Notes = notes.Distinct().ToList();
        return handler;
    }

    /// <summary>
    /// What was noticed about the encryption data while opening (localised): an incomplete crypt filter whose method was assumed, a filter
    /// that does not encrypt (docs/FEHLERGEDAECHTNIS.md, No. 544). Empty if nothing.
    /// </summary>
    public IReadOnlyList<string> Notes { get; private set; } = [];

    /// <summary>The state of the sealed permissions (/Perms, revision 5 and 6), checked with both passwords.</summary>
    public PermsState Perms { get; private init; } = PermsState.NotApplicable;

    /// <summary>The permissions sealed in /Perms (all 32 bits); null if there is none or it does not check out.</summary>
    internal int? SealedP { get; private init; }

    /// <summary>
    /// The permission bits as they stand — sealed where /Perms is valid, else /P — with bit 13 (ISO/TS 32004: cleared = a MAC is required)
    /// and the other bits <see cref="Permissions"/> leaves out.
    /// </summary>
    internal int RawPermissions { get; private init; }

    /// <summary>The crypt filters of /CF by name and the method for embedded files (/EFF, without it that for streams).</summary>
    private static (IReadOnlyDictionary<string, PdfCipher> Named, PdfCipher Embedded) Filters(PdfDictionary encrypt, int v, PdfCipher streams,
        List<string> notes)
    {
        var named = new Dictionary<string, PdfCipher>();
        if (v is not (4 or 5 or 6)) return (named, streams);
        if (encrypt.GetDictionary(PdfName.Get("CF")) is { } cf)
            foreach (var (key, _) in cf.Entries)
                named[key.Value] = Method(cf.GetDictionary(key)?.GetName(PdfName.Get("CFM"))?.Value, v, notes);
        // V 6 is V 5 with at least one AES-GCM filter (ISO/TS 32003, 5.1, Table 2); others (AESV3, Identity) may stand beside it
        if (v == 6 && !named.ContainsValue(PdfCipher.Aes256Gcm))
            throw new PdfException(Tr.T("Verschlüsselung Version 6 ohne AES-GCM-Filter (AESV4)."));
        var eff = encrypt.GetName(PdfName.Get("EFF"));
        var embedded = eff is null ? streams : eff.Value == "Identity" ? PdfCipher.None : named.TryGetValue(eff.Value, out var c) ? c : streams;
        return (named, embedded);
    }

    /// <summary>
    /// The method of a crypt filter (ISO 32000-2, 7.6.6, Table 25): an unknown one is reported, not guessed (AESV4 read as CBC gave garbage),
    /// AESV3 only with the 256-bit key of V 5; a missing /CFM is assumed after V (V 5: AES-256, else RC4) with a note — a deliberate
    /// tolerance against the default None, as widespread files leave it out (No. 242); None is noted (docs/FEHLERGEDAECHTNIS.md, No. 544).
    /// </summary>
    private static PdfCipher Method(string? method, int v, List<string> notes)
    {
        switch (method)
        {
            case "V2":
                return PdfCipher.Rc4;
            case "AESV2":
                return PdfCipher.Aes128;
            case "AESV3" when v is 5 or 6:
                return PdfCipher.Aes256;
            // AES-GCM (ISO/TS 32003): only under V 6 (docs/ABLAUFPLAN.md, 5.4; docs/FEHLERGEDAECHTNIS.md, No. 555)
            case "AESV4" when v == 6:
                return GcmSupported() ? PdfCipher.Aes256Gcm : throw new PdfException(Tr.T("AES-GCM wird auf diesem System nicht unterstützt."));
            case "AESV4":
                throw new PdfException(Tr.T("Verschlüsselungsverfahren „{0}“ passt nicht zu Version {1}.", "AESV4", v));
            case "AESV3":
                throw new PdfException(Tr.T("Verschlüsselungsverfahren „{0}“ passt nicht zu Version {1}.", "AESV3", v));
            case "None":
                notes.Add(Tr.T("Ein Kryptfilter der Datei verschlüsselt nicht (None)."));
                return PdfCipher.None;
            case null:
                notes.Add(Tr.T("Verschlüsselungsangaben unvollständig — Verfahren angenommen."));
                return v is 5 or 6 ? PdfCipher.Aes256 : PdfCipher.Rc4;
            default:
                throw new PdfException(Tr.T("Verschlüsselungsverfahren „{0}“ wird nicht unterstützt.", method));
        }
    }

    /// <summary>True if only embedded files are encrypted and have not been unlocked (the document itself is open).</summary>
    public bool AttachmentsLocked { get; private init; }

    private static StandardSecurityHandler OpenWithPassword(PdfDictionary encrypt, PdfArray? id, string? password, int r,
        byte[] o, byte[] u, int p, bool encryptMetadata, PdfPermissions permissions, PdfCipher stringCipher, PdfCipher streamCipher)
    {
        // revision 5 (a deprecated vendor extension, still read), 6, and 7 (AES-GCM, ISO/TS 32003: only with V 6, passwords as R 6); beyond
        // is reported (No. 544, 555)
        if (r == 7 && encrypt.GetInteger(PdfName.Get("V")) != 6)
            throw new PdfException(Tr.T("Verschlüsselung Revision {0} wird nicht unterstützt.", r));
        if (r is 5 or 6 or 7)
        {
            var oe = encrypt.GetString(PdfName.Get("OE"))?.Bytes ?? [];
            var ue = encrypt.GetString(PdfName.Get("UE"))?.Bytes ?? [];
            if (o.Length < 48 || u.Length < 48 || oe.Length < 32 || ue.Length < 32)
                throw new PdfException(Tr.T("Verschlüsselungsdaten unvollständig."));
            var pwd = PrepareAesPassword(password ?? "");
            var owner = OpenAes256(r, pwd, o, u, oe, ue, owner: true);
            var key = owner ?? OpenAes256(r, pwd, o, u, oe, ue, owner: false) ?? throw PasswordError(password);
            // /Perms with both passwords (ISO 32000-2, 7.6.4.3.3, algorithm 2.A f): the sealed bits count, byte 8 says /EncryptMetadata
            var (state, sealedP, sealedMetadata) = PermsOf(encrypt, key, p, encryptMetadata);
            var effectiveMetadata = sealedMetadata ?? encryptMetadata;
            var granted = owner is not null ? PdfPermissions.All : (PdfPermissions)(sealedP ?? p) & PdfPermissions.All;
            return new StandardSecurityHandler(key, r, stringCipher, streamCipher, effectiveMetadata, granted, owner is not null)
            {
                Perms = state,
                SealedP = sealedP,
                RawPermissions = sealedP ?? p,
            };
        }
        if (r > 7) throw new PdfException(Tr.T("Verschlüsselung Revision {0} wird nicht unterstützt.", r));

        if (r is < 2 or > 4) throw new PdfException(Tr.T("Verschlüsselung Revision {0} wird nicht unterstützt.", r));
        var lengthBits = (int)(encrypt.GetInteger(PdfName.Length) ?? 40);
        var keyLength = r == 2 ? 5 : Math.Clamp(lengthBits / 8, 5, 16);
        var firstId = id is { Count: > 0 } && id.Get(0) is PdfString s ? s.Bytes : [];
        if (o.Length < 32 || u.Length < 32) throw new PdfException(Tr.T("Verschlüsselungsdaten unvollständig."));

        // PDFDocEncoding (as common PDF editors do), and Latin-1 as well (as written by older versions of this program and by other programs)
        foreach (var bytes in PasswordCandidates(password ?? ""))
        {
            // Owner password first: it decrypts /O to the user password
            var asUser = UserPasswordFromOwner(bytes, o, r, keyLength);
            var ownerFileKey = ComputeKey(asUser, o, p, firstId, r, keyLength, encryptMetadata);
            if (CheckUser(ownerFileKey, u, firstId, r))
                return new StandardSecurityHandler(ownerFileKey, r, stringCipher, streamCipher, encryptMetadata, PdfPermissions.All, true)
                {
                    PaddedUser = asUser,
                    FileId = firstId,
                    RawPermissions = p,
                };

            var userKey2 = ComputeKey(bytes, o, p, firstId, r, keyLength, encryptMetadata);
            if (CheckUser(userKey2, u, firstId, r))
                return new StandardSecurityHandler(userKey2, r, stringCipher, streamCipher, encryptMetadata, permissions, false)
                {
                    PaddedUser = bytes,
                    FileId = firstId,
                    RawPermissions = p,
                };
        }

        throw PasswordError(password);
    }

    private static PdfPasswordException PasswordError(string? password) =>
        string.IsNullOrEmpty(password)
            ? new PdfPasswordException(Tr.T("Die Datei ist mit einem Passwort geschützt."), wrongPassword: false)
            : new PdfPasswordException(Tr.T("Das Passwort ist falsch."), wrongPassword: true);

    private static (PdfCipher Strings, PdfCipher Streams) Ciphers(PdfDictionary encrypt, int v, List<string> notes)
    {
        switch (v)
        {
            case 1 or 2 or 3:
                return (PdfCipher.Rc4, PdfCipher.Rc4);
            case 4 or 5 or 6:
                var cf = encrypt.GetDictionary(PdfName.Get("CF"));
                PdfCipher Of(PdfName? name) =>
                    name is null || name.Value == "Identity" ? PdfCipher.None : Method(cf?.GetDictionary(name)?.GetName(PdfName.Get("CFM"))?.Value, v, notes);
                return (Of(encrypt.GetName(PdfName.Get("StrF")) ?? PdfName.Identity), Of(encrypt.GetName(PdfName.Get("StmF")) ?? PdfName.Identity));
            default:
                throw new PdfException(Tr.T("Verschlüsselung Version {0} wird nicht unterstützt.", v));
        }
    }

    // ------------------------------------------------------------ R2–R4 (RC4/AES-128)

    /// <summary>Checks whether a password can be represented in PDFDocEncoding, as RC4 and AES-128 require.</summary>
    /// <param name="password">The password.</param>
    /// <returns>True if it fits; otherwise only AES-256 can be used. A lossy conversion would turn "Пароль€" into
    /// "???????", which any password of question marks would then open. See docs/FEHLERGEDAECHTNIS.md, No. 37.</returns>
    public static bool FitsLegacyPassword(string password) => PdfTextEncoding.EncodeDocEncoding(password) is not null;

    private static IEnumerable<byte[]> PasswordCandidates(string password)
    {
        var doc = PdfTextEncoding.EncodeDocEncoding(password);
        var latin1 = Encoding.Latin1.GetBytes(password);
        if (doc is not null) yield return Pad(doc);
        if (doc is null || !doc.AsSpan().SequenceEqual(latin1)) yield return Pad(latin1);
    }

    private static byte[] PasswordBytes(string password) => Pad(PdfTextEncoding.EncodeDocEncoding(password) ?? Encoding.Latin1.GetBytes(password));

    /// <summary>Password to 32 bytes: truncated and filled up with the fixed padding.</summary>
    private static byte[] Pad(byte[] raw)
    {
        var padded = new byte[32];
        var n = Math.Min(32, raw.Length);
        raw.AsSpan(0, n).CopyTo(padded);
        Padding.AsSpan(0, 32 - n).CopyTo(padded.AsSpan(n));
        return padded;
    }

    /// <summary>Algorithm 2: file key from the (padded) user password.</summary>
    private static byte[] ComputeKey(byte[] paddedPassword, byte[] o, int p, byte[] firstId, int r, int keyLength, bool encryptMetadata)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(paddedPassword);
        md5.AppendData(o.AsSpan(0, 32));
        Span<byte> pBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(pBytes, p);
        md5.AppendData(pBytes);
        md5.AppendData(firstId);
        if (r >= 4 && !encryptMetadata) md5.AppendData([0xFF, 0xFF, 0xFF, 0xFF]);
        var hash = md5.GetHashAndReset();
        if (r >= 3)
            for (var i = 0; i < 50; i++) hash = MD5.HashData(hash.AsSpan(0, keyLength));
        return hash[..keyLength];
    }

    /// <summary>Algorithms 4/5: computes the expected /U value and compares it.</summary>
    private static bool CheckUser(byte[] key, byte[] u, byte[] firstId, int r)
    {
        var expected = ComputeU(key, firstId, r);
        var compare = r == 2 ? 32 : 16;
        return expected.AsSpan(0, compare).SequenceEqual(u.AsSpan(0, compare));
    }

    private static byte[] ComputeU(byte[] key, byte[] firstId, int r)
    {
        if (r == 2) return Rc4.Apply(key, Padding);
        var hash = MD5.HashData([.. Padding, .. firstId]);
        var value = Rc4.Apply(key, hash);
        for (var i = 1; i <= 19; i++) value = Rc4.Apply(XorKey(key, i), value);
        // Fill up to 32 bytes; the rest is arbitrary (zeros here)
        return [.. value, .. new byte[16]];
    }

    /// <summary>Algorithm 7: derives the (padded) user password from the owner password.</summary>
    private static byte[] UserPasswordFromOwner(byte[] paddedOwner, byte[] o, int r, int keyLength)
    {
        var key = OwnerKey(paddedOwner, r, keyLength);
        var value = o[..32];
        if (r == 2) return Rc4.Apply(key, value);
        for (var i = 19; i >= 0; i--) value = Rc4.Apply(XorKey(key, i), value);
        return value;
    }

    private static byte[] OwnerKey(byte[] paddedOwner, int r, int keyLength)
    {
        var hash = MD5.HashData(paddedOwner);
        if (r >= 3)
            for (var i = 0; i < 50; i++) hash = MD5.HashData(hash);
        return hash[..keyLength];
    }

    private static byte[] XorKey(byte[] key, int x)
    {
        var result = new byte[key.Length];
        for (var i = 0; i < key.Length; i++) result[i] = (byte)(key[i] ^ x);
        return result;
    }

    // ------------------------------------------------------------ R5/R6 (AES-256)

    /// <summary>
    /// SASLprep (RFC 4013) in the form needed for passwords: removes characters that are "mapped to nothing" (soft
    /// hyphen, zero-width characters …), maps space characters to a plain space, normalises to NFKC; then UTF-8,
    /// at most 127 bytes.
    /// </summary>
    internal static byte[] PrepareAesPassword(string password)
    {
        var mapped = SaslMap(password);
        // a lone surrogate cannot be normalised: when opening, taken as it is (setting refuses it, see SaslRefusal; No. 558)
        string normalized;
        try
        {
            normalized = mapped.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            normalized = mapped;
        }
        var bytes = Encoding.UTF8.GetBytes(normalized);
        return bytes.Length <= 127 ? bytes : bytes[..127];
    }

    /// <summary>
    /// SASLprep mapping (RFC 4013, 2.1 before 2.2): non-ASCII spaces (RFC 3454, C.1.2) become a space, characters of B.1 go. U+200B stands in
    /// both tables and becomes a space, as in the order of RFC 4013 and in Unicode 3.2 (docs/FEHLERGEDAECHTNIS.md, No. 752).
    /// </summary>
    private static string SaslMap(string password)
    {
        var sb = new StringBuilder(password.Length);
        foreach (var c in password)
        {
            if (c is '\u00A0' or '\u1680' or (>= '\u2000' and <= '\u200B') or '\u202F' or '\u205F' or '\u3000')
                sb.Append(' ');
            else if (c is not ('\u00AD' or '\u034F' or '\u1806' or (>= '\u180B' and <= '\u180D') or '\u200C' or '\u200D'
                     or '\u2060' or (>= '\uFE00' and <= '\uFE0F') or '\uFEFF'))
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Why a password cannot be set for AES-256 (ISO 32000-2, 7.6.4.3.3, algorithm 2.A: SASLprep, RFC 4013 with the prohibitions and the
    /// bidirectional check of RFC 3454, and no unassigned code points for stored strings); null if it can. A password a conforming reader
    /// refuses would leave a file only PDF-Knead opens (docs/FEHLERGEDAECHTNIS.md, No. 558). Opening stays tolerant.
    /// </summary>
    public static string? SaslRefusal(string password)
    {
        var mapped = SaslMap(password);
        for (var i = 0; i < mapped.Length; i++)
            if (char.IsSurrogate(mapped[i]) && !(char.IsHighSurrogate(mapped[i]) && i + 1 < mapped.Length && char.IsLowSurrogate(mapped[++i])))
                return Tr.T("Das Passwort enthält ein ungültiges Zeichen.");
        var normalized = mapped.Normalize(NormalizationForm.FormKC);
        bool randAl = false, left = false;
        Rune? first = null, last = null;
        foreach (var rune in normalized.EnumerateRunes())
        {
            var v = rune.Value;
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control || v is 0x06DD or 0x070F or 0x180E or 0x200C or 0x200D or 0x2028 or 0x2029
                or (>= 0x2060 and <= 0x2063) or (>= 0x1D173 and <= 0x1D17A))
                return Tr.T("Das Passwort enthält ein Steuerzeichen.");
            if (category == UnicodeCategory.PrivateUse) return Tr.T("Das Passwort enthält ein Zeichen aus einem privaten Bereich.");
            if (v is (>= 0xFDD0 and <= 0xFDEF) || (v & 0xFFFE) == 0xFFFE) return Tr.T("Das Passwort enthält ein Nichtzeichen.");
            // C.6 to C.9: inappropriate for plain text or canonical representation, changing display or direction, tags
            if (v is (>= 0xFFF9 and <= 0xFFFD) or (>= 0x2FF0 and <= 0x2FFB) or 0x0340 or 0x0341 or 0x200E or 0x200F or (>= 0x202A and <= 0x202E)
                or (>= 0x206A and <= 0x206F) or 0xE0001 or (>= 0xE0020 and <= 0xE007F))
                return Tr.T("Das Passwort enthält ein Zeichen, das in Passwörtern nicht erlaubt ist (Steuerung der Darstellung oder Schreibrichtung).");
            // unassigned in Unicode 3.2, the version of the tables (RFC 3454, A.1): a later character could be normalised differently by
            // another program, and the password would not open the file there (No. 752)
            if (category == UnicodeCategory.OtherNotAssigned || SaslPrepTables.Unassigned(v))
                return Tr.T("Das Passwort enthält ein nicht zugewiesenes Zeichen.");
            var rtl = SaslPrepTables.RandAL(v);
            randAl |= rtl;
            left |= SaslPrepTables.LeftToRight(v);
            first ??= rune;
            last = rune;
        }
        // RFC 3454, 6: with right-to-left characters no left-to-right ones, and the first and last right-to-left
        if (randAl && (left || !SaslPrepTables.RandAL(first!.Value.Value) || !SaslPrepTables.RandAL(last!.Value.Value)))
            return Tr.T("Das Passwort mischt Schreibrichtungen (zum Beispiel hebräische oder arabische mit lateinischen Buchstaben) — das ist in Passwörtern nicht erlaubt.");
        return null;
    }

    /// <summary>
    /// The permissions of an AES-256 file as the owner sealed them (ISO 32000-2, 7.6.4.4.12, algorithm 13): /Perms decrypted with the file
    /// key holds "adb" and the permissions. They count instead of the plain /P, which anyone can edit without a password — a loosened /P
    /// was taken as a real permission (docs/FEHLERGEDAECHTNIS.md, No. 432). Without a readable /Perms (a damaged or old file) /P stays.
    /// </summary>
    private static (PermsState State, int? SealedP, bool? SealedMetadata) PermsOf(PdfDictionary encrypt, byte[] fileKey, int p, bool encryptMetadata)
    {
        if (encrypt.GetString(PdfName.Get("Perms"))?.Bytes is not { Length: 16 } sealedPerms) return (PermsState.Missing, null, null);
        try
        {
            using var aes = Aes.Create();
            aes.Key = fileKey;
            var perms = aes.DecryptEcb(sealedPerms, PaddingMode.None);
            if (!perms.AsSpan(9, 3).SequenceEqual("adb"u8)) return (PermsState.Invalid, null, null);
            var sealedP = BinaryPrimitives.ReadInt32LittleEndian(perms);
            bool? metadata = perms[8] switch { (byte)'T' => true, (byte)'F' => false, _ => null };
            var state = sealedP != p ? PermsState.DiffersFromP
                : metadata is { } m && m != encryptMetadata ? PermsState.EncryptMetadataDiffers : PermsState.Valid;
            return (state, sealedP, metadata);
        }
        catch (CryptographicException)
        {
            return (PermsState.Invalid, null, null);
        }
    }

    private static byte[]? OpenAes256(int r, byte[] pwd, byte[] o, byte[] u, byte[] oe, byte[] ue, bool owner)
    {
        var hashOf = owner ? o : u;
        var udata = owner ? u[..48] : [];
        var validationSalt = hashOf.AsSpan(32, 8).ToArray();
        var keySalt = hashOf.AsSpan(40, 8).ToArray();
        if (!Hash(r, pwd, validationSalt, udata).AsSpan().SequenceEqual(hashOf.AsSpan(0, 32))) return null;
        var intermediate = Hash(r, pwd, keySalt, udata);
        return AesCbcNoPadding(intermediate, new byte[16], (owner ? oe : ue)[..32], encrypt: false);
    }

    /// <summary>R5: SHA-256; R6: algorithm 2.B (ISO 32000-2, 7.6.4.3.4).</summary>
    private static byte[] Hash(int r, byte[] password, byte[] salt, byte[] udata)
    {
        var k = SHA256.HashData([.. password, .. salt, .. udata]);
        if (r == 5) return k;

        for (var round = 0; ; round++)
        {
            var block = (byte[])[.. password, .. k, .. udata];
            var k1 = new byte[block.Length * 64];
            for (var i = 0; i < 64; i++) block.CopyTo(k1, i * block.Length);
            var e = AesCbcNoPadding(k[..16], k[16..32], k1, encrypt: true);
            var sum = 0;
            for (var i = 0; i < 16; i++) sum += e[i];
            k = (sum % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
            if (round >= 63 && e[^1] <= round - 31) break;
        }
        return k[..32];
    }

    private static byte[] AesCbcNoPadding(byte[] key, byte[] iv, byte[] data, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return encrypt
            ? aes.EncryptCbc(data, iv, PaddingMode.None)
            : aes.DecryptCbc(data, iv, PaddingMode.None);
    }

    // ------------------------------------------------------------ Decryption

    /// <summary>Decrypts a string of an indirect object.</summary>
    /// <param name="id">The object the string belongs to.</param>
    /// <param name="data">The encrypted bytes.</param>
    /// <returns>The decrypted bytes; for AES, empty if the data is shorter than one block.</returns>
    public byte[] DecryptString(PdfObjectId id, byte[] data) => Decrypt(id, data, StringCipher);

    /// <summary>Decrypts the data of a stream.</summary>
    /// <param name="id">The object the stream belongs to.</param>
    /// <param name="data">The encrypted bytes.</param>
    /// <returns>The decrypted bytes; for AES, empty if the data is shorter than one block.</returns>
    public byte[] DecryptStream(PdfObjectId id, byte[] data) => Decrypt(id, data, StreamCipher);

    /// <summary>Decrypts the data of a stream with a given method (its own crypt filter, or that for embedded files).</summary>
    /// <param name="id">The object the stream belongs to.</param>
    /// <param name="data">The encrypted bytes.</param>
    /// <param name="cipher">The method.</param>
    /// <returns>The decrypted bytes.</returns>
    /// <exception cref="PdfException">The attachments are locked (no key): access must fail, not yield the encrypted bytes (7.6.6).</exception>
    public byte[] DecryptStream(PdfObjectId id, byte[] data, PdfCipher cipher)
    {
        if (cipher != PdfCipher.None && AttachmentsLocked)
            throw new PdfException(Tr.T("Der Anhang ist verschlüsselt und lässt sich ohne sein Passwort nicht lesen."));
        return Decrypt(id, data, cipher);
    }

    /// <summary>
    /// Whether data encrypted with AES decrypts to a whole padding under this key (the last block ends in n bytes of value n, 1 ≤ n ≤ 16).
    /// Reading accepts a damaged padding (it occurs); unlocking attachments checks it, so that a key that opens /U but not the attachment
    /// is not taken — the bytes would otherwise be written anew as they came out, and the attachment would be destroyed without a word
    /// (docs/FEHLERGEDAECHTNIS.md, No. 459). RC4 and no encryption: true.
    /// </summary>
    /// <param name="id">The object the stream belongs to.</param>
    /// <param name="data">The encrypted bytes (initialisation vector in front).</param>
    /// <param name="cipher">The method.</param>
    /// <returns>Whether the padding is whole.</returns>
    public bool DecryptsWhole(PdfObjectId id, byte[] data, PdfCipher cipher)
    {
        if (cipher == PdfCipher.Aes256Gcm) return GcmOpen(data) is not null;
        if (cipher is not (PdfCipher.Aes128 or PdfCipher.Aes256)) return true;
        if (data.Length < 32 || (data.Length - 16) % 16 != 0) return false;
        var key = cipher == PdfCipher.Aes256 ? _key : ObjectKey(id, aes: true, _readKey);
        if (key.Length is not (16 or 24 or 32)) return false;
        using var aes = Aes.Create();
        aes.Key = key;
        var plain = aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.None);
        var pad = plain[^1];
        if (pad is < 1 or > 16) return false;
        for (var i = plain.Length - pad; i < plain.Length; i++)
            if (plain[i] != pad) return false;
        return true;
    }

    private byte[] Decrypt(PdfObjectId id, byte[] data, PdfCipher cipher)
    {
        switch (cipher)
        {
            case PdfCipher.None:
                return data;
            case PdfCipher.Rc4:
                return Rc4.Apply(ObjectKey(id, aes: false, _readKey), data);
            case PdfCipher.Aes256Gcm:
                if (GcmOpen(data) is { } opened) return opened;
                // too short or the tag does not match: nothing rather than garbage, and that is said (ISO/TS 32003, 5.2; No. 555)
                AuthenticationFailed?.Invoke();
                return [];
            default:
                if (data.Length < 16) return [];
                var key = cipher == PdfCipher.Aes256 ? _key : ObjectKey(id, aes: true, _readKey);
                if (key.Length is not (16 or 24 or 32)) throw new PdfException(Tr.T("Verschlüsselungsdaten unvollständig."));
                var body = data.AsSpan(16, (data.Length - 16) / 16 * 16);
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    var plain = aes.DecryptCbc(body, data.AsSpan(0, 16), PaddingMode.None);
                    // Remove the PKCS#7 padding; leave damaged padding (it occurs) as it is
                    var pad = plain.Length > 0 ? plain[^1] : 0;
                    return pad is >= 1 and <= 16 && pad <= plain.Length ? plain[..^pad] : plain;
                }
        }
    }

    /// <summary>Encrypts a string of an indirect object.</summary>
    /// <param name="id">The object the string belongs to.</param>
    /// <param name="data">The plain bytes.</param>
    /// <returns>The encrypted bytes; for AES with a random 16-byte initialisation vector in front.</returns>
    public byte[] EncryptString(PdfObjectId id, byte[] data) => Encrypt(id, data, StringCipher);

    /// <summary>Encrypts the data of a stream.</summary>
    /// <param name="id">The object the stream belongs to.</param>
    /// <param name="data">The plain bytes.</param>
    /// <returns>The encrypted bytes; for AES with a random 16-byte initialisation vector in front.</returns>
    public byte[] EncryptStream(PdfObjectId id, byte[] data) => Encrypt(id, data, StreamCipher);

    /// <summary>Encrypts the data of a stream with a given method (its own crypt filter, or that for embedded files).</summary>
    /// <param name="id">The object the stream belongs to.</param>
    /// <param name="data">The plain bytes.</param>
    /// <param name="cipher">The method.</param>
    /// <returns>The encrypted bytes.</returns>
    public byte[] EncryptStream(PdfObjectId id, byte[] data, PdfCipher cipher) => Encrypt(id, data, cipher);

    private byte[] Encrypt(PdfObjectId id, byte[] data, PdfCipher cipher)
    {
        switch (cipher)
        {
            case PdfCipher.None:
                return data;
            case PdfCipher.Rc4:
                return Rc4.Apply(ObjectKey(id, aes: false, _key), data);
            case PdfCipher.Aes256Gcm:
                return GcmSeal(data);
            default:
                var key = cipher == PdfCipher.Aes256 ? _key : ObjectKey(id, aes: true, _key);
                // no key of a size AES takes (attachments with a password of their own, not given: the key is empty) — a message, as when
                // decrypting, not an exception of the runtime (docs/FEHLERGEDAECHTNIS.md, No. 521)
                if (key.Length is not (16 or 24 or 32)) throw new PdfException(Tr.T("Verschlüsselungsdaten unvollständig."));
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    var iv = RandomNumberGenerator.GetBytes(16);
                    return [.. iv, .. aes.EncryptCbc(data, iv, PaddingMode.PKCS7)];
                }
        }
    }

    /// <summary>
    /// Data of a stream or string that did not decrypt under AES-GCM (too short, or the tag does not match: changed after encrypting). The
    /// document turns it into a problem shown to the user (docs/FEHLERGEDAECHTNIS.md, No. 555).
    /// </summary>
    internal event Action? AuthenticationFailed;

    private const int GcmNonce = 12, GcmTag = 16;

    /// <summary>For the tests: an owner handler with a given 32-byte file key and one method for strings and streams (revision 7 for GCM).</summary>
    internal static StandardSecurityHandler WithKey(byte[] key, PdfCipher cipher) =>
        new StandardSecurityHandler(key, cipher == PdfCipher.Aes256Gcm ? 7 : 6, cipher, cipher, true, PdfPermissions.All, true).WithStandardFilter(cipher);

    private static bool GcmSupported() => AesGcm.IsSupported;

    /// <summary>
    /// AES-GCM decryption (ISO/TS 32003, 5.2): the file key, 12-byte nonce in front, 16-byte tag behind, no associated data; null if the data
    /// is too short or the tag does not match. A new <see cref="AesGcm"/> per call — the handler is used from several threads.
    /// </summary>
    private byte[]? GcmOpen(ReadOnlySpan<byte> data)
    {
        if (data.Length < GcmNonce + GcmTag || _key.Length != 32) return null;
        var plain = new byte[data.Length - GcmNonce - GcmTag];
        try
        {
            using var gcm = new AesGcm(_key, GcmTag);
            gcm.Decrypt(data[..GcmNonce], data[GcmNonce..^GcmTag], data[^GcmTag..], plain);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    /// <summary>AES-GCM encryption: a random nonce (never twice in practice — 96 random bits), nonce ‖ ciphertext ‖ tag.</summary>
    private byte[] GcmSeal(byte[] data)
    {
        if (_key.Length != 32) throw new PdfException(Tr.T("Verschlüsselungsdaten unvollständig."));
        var result = new byte[GcmNonce + data.Length + GcmTag];
        RandomNumberGenerator.Fill(result.AsSpan(0, GcmNonce));
        using var gcm = new AesGcm(_key, GcmTag);
        gcm.Encrypt(result.AsSpan(0, GcmNonce), data, result.AsSpan(GcmNonce, data.Length), result.AsSpan(GcmNonce + data.Length));
        return result;
    }

    /// <summary>Algorithm 1: key per object from the file key, object number and generation.</summary>
    private static byte[] ObjectKey(PdfObjectId id, bool aes, byte[] fileKey)
    {
        var input = new byte[fileKey.Length + 5 + (aes ? 4 : 0)];
        fileKey.CopyTo(input, 0);
        input[fileKey.Length] = (byte)id.Number;
        input[fileKey.Length + 1] = (byte)(id.Number >> 8);
        input[fileKey.Length + 2] = (byte)(id.Number >> 16);
        input[fileKey.Length + 3] = (byte)id.Generation;
        input[fileKey.Length + 4] = (byte)(id.Generation >> 8);
        if (aes) SaltAes.CopyTo(input, fileKey.Length + 5);
        // AES accepts only 16 bytes here: with a short file key (crafted file: AESV2 with /Length 40) the whole hash is used
        return aes ? MD5.HashData(input) : MD5.HashData(input)[..Math.Min(fileKey.Length + 5, 16)];
    }

    /// <summary>True if the data of the file is read with the file key padded to 16 bytes (<see cref="PaddedForReading"/>).</summary>
    public bool ReadsWithPaddedKey { get; private set; }

    /// <summary>
    /// A copy that reads the data with the file key padded with zero bytes to 16 bytes — so that Algorithm 1 (ISO 32000-2, 7.6.3.3) takes n = 16
    /// — as a widely used reader opens revision-4 files whose RC4 key is shorter: test file issue19484_1, _2 (/Length 40, the data encrypted with
    /// the padded key, /U computed with the key as the norm derives it; docs/FEHLERGEDAECHTNIS.md, No. 894). The norm does not describe this; the
    /// document uses the copy only where a stream of the file decodes with it and not with the key of the norm (a read tolerance, sonderregeln.md
    /// A105). Writing keeps the key of the norm, so a copy is encrypted as the norm asks. Null where it does not apply: not revision 4, no RC4,
    /// or a key of 16 bytes already.
    /// </summary>
    /// <returns>The copy, or null.</returns>
    internal StandardSecurityHandler? PaddedForReading()
    {
        if (Revision != 4 || _key.Length is 0 or >= 16 || StreamCipher != PdfCipher.Rc4 && StringCipher != PdfCipher.Rc4) return null;
        var copy = (StandardSecurityHandler)MemberwiseClone();
        copy._readKey = new byte[16];
        _key.CopyTo(copy._readKey, 0);
        copy.ReadsWithPaddedKey = true;
        copy.AuthenticationFailed = null;
        return copy;
    }

    // ------------------------------------------------------------ New encryption (saving)

    /// <summary>
    /// Creates a new /Encrypt dictionary and the matching handler.
    /// </summary>
    /// <param name="cipher">RC4 (128 bit, R3), AES-128 (R4) or AES-256 (R6).</param>
    /// <param name="userPassword">The open password; empty for none.</param>
    /// <param name="ownerPassword">The permissions password; if empty, the user password is used.</param>
    /// <param name="permissions">The permissions granted without the owner password.</param>
    /// <param name="firstId">The first part of /ID of the new file; it must already be fixed (used up to R4).</param>
    /// <param name="encryptMetadata">False to leave metadata streams unencrypted.</param>
    /// <returns>The dictionary and a handler with <see cref="IsOwner"/> set, for encrypting the new file.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cipher"/> is none of the three (without one, an R3 dictionary would be
    /// written for a handler that encrypts nothing).</exception>
    /// <remarks>R3 and R4 encode the passwords in PDFDocEncoding, or Latin-1 if that is not possible; check with
    /// <see cref="FitsLegacyPassword"/> first. Uses <see cref="RandomNumberGenerator"/> for keys and salts.</remarks>
    public static (PdfDictionary Encrypt, StandardSecurityHandler Handler) Create(
        PdfCipher cipher, string userPassword, string ownerPassword, PdfPermissions permissions, byte[] firstId, bool encryptMetadata = true) =>
        Create(cipher, userPassword, ownerPassword, permissions, firstId, encryptMetadata, requireMac: false);

    /// <inheritdoc cref="Create(PdfCipher, string, string, PdfPermissions, byte[], bool)"/>
    /// <param name="requireMac">AES-256 only, for the tests: bit 13 cleared and a /KDFSalt, as a file asking for a MAC per revision
    /// (ISO/TS 32004, 5.1) is written.</param>
    internal static (PdfDictionary Encrypt, StandardSecurityHandler Handler) Create(
        PdfCipher cipher, string userPassword, string ownerPassword, PdfPermissions permissions, byte[] firstId, bool encryptMetadata, bool requireMac)
    {
        // AES-GCM (V 6, R 7, ISO/TS 32003) only on this internal way, for the tests: as new protection it is a later choice (docs/ABLAUFPLAN.md, 5.4)
        if (cipher is not (PdfCipher.Rc4 or PdfCipher.Aes128 or PdfCipher.Aes256 or PdfCipher.Aes256Gcm)) throw new ArgumentOutOfRangeException(nameof(cipher));
        if (string.IsNullOrEmpty(ownerPassword)) ownerPassword = userPassword;
        // Unused bits are 1 by the standard; bits 1–2 are 0; bit 10 always set (ISO 32000-2, Table 22; ISO 14289-1, 7.16; No. 490)
        var p = unchecked((int)0xFFFFF0C0) | (int)permissions | (int)PdfPermissions.Accessibility;
        if (requireMac && cipher is PdfCipher.Aes256 or PdfCipher.Aes256Gcm) p &= ~MacBit;
        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("Standard"));
        dict.Set(PdfName.Get("P"), PdfInteger.Of(p));

        if (cipher is PdfCipher.Aes256 or PdfCipher.Aes256Gcm)
        {
            var gcm = cipher == PdfCipher.Aes256Gcm;
            var fileKey = RandomNumberGenerator.GetBytes(32);
            var user = PrepareAesPassword(userPassword);
            var owner = PrepareAesPassword(ownerPassword);

            var uSalts = RandomNumberGenerator.GetBytes(16);
            byte[] u = [.. Hash(6, user, uSalts[..8], []), .. uSalts];
            var ue = AesCbcNoPadding(Hash(6, user, uSalts[8..], []), new byte[16], fileKey, encrypt: true);

            var oSalts = RandomNumberGenerator.GetBytes(16);
            byte[] o = [.. Hash(6, owner, oSalts[..8], u), .. oSalts];
            var oe = AesCbcNoPadding(Hash(6, owner, oSalts[8..], u), new byte[16], fileKey, encrypt: true);

            var permsEncrypted = SealPerms(p, fileKey, encryptMetadata);
            if (requireMac) dict.Set(PdfName.Get("KDFSalt"), new PdfString(RandomNumberGenerator.GetBytes(32)));

            dict.Set(PdfName.Get("V"), PdfInteger.Of(gcm ? 6 : 5));
            dict.Set(PdfName.Get("R"), PdfInteger.Of(gcm ? 7 : 6));
            if (!gcm) dict.Set(PdfName.Length, PdfInteger.Of(256));
            dict.Set(PdfName.Get("O"), new PdfString(o));
            dict.Set(PdfName.Get("U"), new PdfString(u));
            dict.Set(PdfName.Get("OE"), new PdfString(oe));
            dict.Set(PdfName.Get("UE"), new PdfString(ue));
            dict.Set(PdfName.Get("Perms"), new PdfString(permsEncrypted));
            SetCryptFilter(dict, gcm ? "AESV4" : "AESV3", 32, encryptMetadata);
            return (dict, new StandardSecurityHandler(fileKey, gcm ? 7 : 6, cipher, cipher, encryptMetadata, permissions, true)
            {
                RawPermissions = p,
            }.WithStandardFilter(cipher));
        }

        var r = cipher == PdfCipher.Aes128 ? 4 : 3;
        const int keyLength = 16;
        var paddedUser = PasswordBytes(userPassword);
        var paddedOwner = PasswordBytes(ownerPassword);

        // Algorithm 3: /O
        var ownerKey = OwnerKey(paddedOwner, r, keyLength);
        var oValue = Rc4.Apply(ownerKey, paddedUser);
        for (var i = 1; i <= 19; i++) oValue = Rc4.Apply(XorKey(ownerKey, i), oValue);

        var key = ComputeKey(paddedUser, oValue, p, firstId, r, keyLength, encryptMetadata);
        var uValue = ComputeU(key, firstId, r);

        dict.Set(PdfName.Get("V"), PdfInteger.Of(r == 4 ? 4 : 2));
        dict.Set(PdfName.Get("R"), PdfInteger.Of(r));
        dict.Set(PdfName.Length, PdfInteger.Of(128));
        dict.Set(PdfName.Get("O"), new PdfString(oValue));
        dict.Set(PdfName.Get("U"), new PdfString(uValue));
        if (r == 4) SetCryptFilter(dict, "AESV2", 16, encryptMetadata);   // R4 is AES-128 here; RC4 is written as R3
        return (dict, new StandardSecurityHandler(key, r, cipher, cipher, encryptMetadata, permissions, true) { PaddedUser = paddedUser }.WithStandardFilter(cipher));
    }

    // ------------------------------------------------------------ Removing restrictions (see KONZEPT.md, section 5.11)

    /// <summary>Checks whether the file opens without a password (permissions only, no open password).</summary>
    /// <param name="encrypt">The encryption dictionary.</param>
    /// <param name="id">The /ID array of the trailer.</param>
    /// <returns>True if the empty user password opens the document and embedded files are not locked.</returns>
    /// <exception cref="PdfException">Unsupported method or incomplete encryption data.</exception>
    public static bool OpensWithoutPassword(PdfDictionary encrypt, PdfArray? id)
    {
        try
        {
            return !Open(encrypt, id, null).AttachmentsLocked;
        }
        catch (PdfPasswordException)
        {
            return false;
        }
    }

    /// <summary>Checks whether <paramref name="password"/> is the owner (permissions) password of this encryption.</summary>
    /// <param name="encrypt">The encryption dictionary.</param>
    /// <param name="id">The /ID array of the trailer.</param>
    /// <param name="password">The password to check.</param>
    /// <returns>True if it is the owner password.</returns>
    /// <exception cref="PdfException">Unsupported method or incomplete encryption data.</exception>
    public static bool IsOwnerPassword(PdfDictionary encrypt, PdfArray? id, string password)
    {
        try
        {
            return Open(encrypt, id, password) is { IsOwner: true, AttachmentsLocked: false };
        }
        catch (PdfPasswordException)
        {
            return false;
        }
    }

    /// <summary>True if all permissions can be granted without knowing the passwords; false while only attachments are encrypted and locked.</summary>
    public bool CanGrantAllPermissions => !AttachmentsLocked && (Revision >= 5 ? _key.Length == 32 : PaddedUser is not null);

    /// <summary>
    /// Same passwords, but all permissions (removing restrictions). Up to revision 4, /P is part of the file key: it is
    /// recomputed with the known user password, /O stays valid (it depends only on the two passwords) and /U is
    /// rewritten; for this, <paramref name="firstId"/> must be the first part of /ID of the new file. From revision 5
    /// the key stays, and only /P and the encrypted /Perms change. All other entries (methods, crypt filters) stay.
    /// </summary>
    /// <param name="encrypt">The current encryption dictionary; it is copied, not modified.</param>
    /// <param name="firstId">The first part of /ID of the new file.</param>
    /// <returns>The new dictionary and the matching handler.</returns>
    /// <exception cref="PdfException"><see cref="CanGrantAllPermissions"/> is false, or /O is incomplete.</exception>
    public (PdfDictionary Encrypt, StandardSecurityHandler Handler) WithAllPermissions(PdfDictionary encrypt, byte[] firstId)
    {
        if (!CanGrantAllPermissions) throw new PdfException(Tr.T("Die Rechte dieser Datei lassen sich nicht ändern."));
        var p = unchecked((int)0xFFFFF0C0) | (int)PdfPermissions.All;
        var dict = new PdfDictionary(encrypt.Count);
        foreach (var (name, value) in encrypt.Entries) dict.Set(name, value);
        dict.Set(PdfName.Get("P"), PdfInteger.Of(p));
        // no MAC is written: its salt goes with the requirement (ISO/TS 32004, 5.1.1; docs/FEHLERGEDAECHTNIS.md, No. 546)
        dict.Remove(PdfName.Get("KDFSalt"));

        if (Revision >= 5)
        {
            dict.Set(PdfName.Get("Perms"), new PdfString(SealPerms(p, _key, EncryptMetadata)));
            return (dict, new StandardSecurityHandler(_key, Revision, StringCipher, StreamCipher, EncryptMetadata, PdfPermissions.All, IsOwner).WithFiltersOf(this));
        }

        var o = encrypt.GetString(PdfName.Get("O"))?.Bytes ?? [];
        if (o.Length < 32) throw new PdfException(Tr.T("Verschlüsselungsdaten unvollständig."));
        var key = ComputeKey(PaddedUser!, o, p, firstId, Revision, _key.Length, EncryptMetadata);
        dict.Set(PdfName.Get("U"), new PdfString(ComputeU(key, firstId, Revision)));
        return (dict, new StandardSecurityHandler(key, Revision, StringCipher, StreamCipher, EncryptMetadata, PdfPermissions.All, IsOwner)
        {
            PaddedUser = PaddedUser,
        }.WithFiltersOf(this));
    }

    /// <summary>
    /// Whether the file key is there (not when only the attachments are encrypted and stay locked): without it nothing can be sealed anew
    /// (/Perms) and no MAC computed (docs/FEHLERGEDAECHTNIS.md, No. 560).
    /// </summary>
    internal bool HasFileKey => Revision >= 5 ? _key.Length == 32 : _key.Length > 0;

    /// <summary>The key encryption key of a PDF MAC (ISO/TS 32004, 6.4), derived from the file key, which stays private here.</summary>
    internal byte[] MacKek(byte[] kdfSalt) => PdfMac.Kek(_key, kdfSalt);

    /// <summary>Bit 13 of the permissions: cleared, every revision must carry a MAC (ISO/TS 32004, 5.1.2, Table 3).</summary>
    internal const int MacBit = 0x1000;

    /// <summary>Whether a file asks for a MAC per revision: from revision 6 with bit 13 cleared — judged by the raw bits (sealed where /Perms
    /// is valid), never by <see cref="Permissions"/>, which is „all“ for the owner (docs/FEHLERGEDAECHTNIS.md, No. 546).</summary>
    internal static bool RequiresMac(int rawPermissions, int revision) => revision >= 6 && (rawPermissions & MacBit) == 0;

    /// <summary>/Perms (ISO 32000-2, 7.6.4.4.9, algorithm 10): P, four FF, T/F for /EncryptMetadata, „adb“, four random bytes, AES-256-ECB.</summary>
    private static byte[] SealPerms(int p, byte[] key, bool encryptMetadata)
    {
        var perms = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(perms, p);
        perms[4] = perms[5] = perms[6] = perms[7] = 0xFF;
        perms[8] = (byte)(encryptMetadata ? 'T' : 'F');
        "adb"u8.CopyTo(perms.AsSpan(9));
        RandomNumberGenerator.Fill(perms.AsSpan(12));
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(perms, PaddingMode.None);
    }

    /// <summary>
    /// The encryption without the MAC requirement (ISO/TS 32004), for a copy PDF Knead writes without a MAC: bit 13 set, /P and /Perms both
    /// (algorithm 2.A f), /KDFSalt gone; the rights stay exactly as sealed (never „all“ for an owner), key and passwords stay — not unlocked
    /// (docs/ABLAUFPLAN.md, 5.2; docs/FEHLERGEDAECHTNIS.md, No. 546). Revision 6 only.
    /// </summary>
    /// <param name="encrypt">The current encryption dictionary; it is copied, not modified.</param>
    internal (PdfDictionary Encrypt, StandardSecurityHandler Handler) WithoutMacRequirement(PdfDictionary encrypt)
    {
        var p = RawPermissions | MacBit;
        var dict = new PdfDictionary(encrypt.Count);
        foreach (var (name, value) in encrypt.Entries) dict.Set(name, value);
        dict.Set(PdfName.Get("P"), PdfInteger.Of(p));
        dict.Remove(PdfName.Get("KDFSalt"));
        if (Revision >= 5) dict.Set(PdfName.Get("Perms"), new PdfString(SealPerms(p, _key, EncryptMetadata)));
        return (dict, new StandardSecurityHandler(_key, Revision, StringCipher, StreamCipher, EncryptMetadata, Permissions, IsOwner)
        {
            PaddedUser = PaddedUser,
            FileId = FileId,
            RawPermissions = p,
            Perms = Revision >= 5 ? PermsState.Valid : PermsState.NotApplicable,
        }.WithFiltersOf(this));
    }

    private static void SetCryptFilter(PdfDictionary dict, string method, int length, bool encryptMetadata)
    {
        var std = new PdfDictionary();
        std.Set(PdfName.Type, PdfName.Get("CryptFilter"));
        std.Set(PdfName.Get("CFM"), PdfName.Get(method));
        std.Set(PdfName.Get("AuthEvent"), PdfName.Get("DocOpen"));
        std.Set(PdfName.Length, PdfInteger.Of(length));
        var cf = new PdfDictionary();
        cf.Set(PdfName.Get("StdCF"), std);
        dict.Set(PdfName.Get("CF"), cf);
        dict.Set(PdfName.Get("StmF"), PdfName.Get("StdCF"));
        dict.Set(PdfName.Get("StrF"), PdfName.Get("StdCF"));
        if (!encryptMetadata) dict.Set(PdfName.Get("EncryptMetadata"), PdfBoolean.False);
    }
}

/// <summary>RC4 (ARCFOUR); not included in .NET, but short.</summary>
public static class Rc4
{
    /// <summary>Encrypts or decrypts data (the operation is symmetric).</summary>
    /// <param name="key">The key; must not be empty.</param>
    /// <param name="data">The data.</param>
    /// <returns>A new array with the result.</returns>
    public static byte[] Apply(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        Span<byte> s = stackalloc byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var result = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            result[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return result;
    }
}

/// <summary>The state of the sealed permissions /Perms (ISO 32000-2, 7.6.4.3.3 and 7.6.4.4.12; docs/FEHLERGEDAECHTNIS.md, No. 544).</summary>
public enum PermsState
{
    /// <summary>Not a revision with /Perms (up to 4).</summary>
    NotApplicable,

    /// <summary>Present, „adb“ checks out, the same as /P and /EncryptMetadata.</summary>
    Valid,

    /// <summary>Missing although revision 5 or 6 requires it: the rights could have been changed afterwards.</summary>
    Missing,

    /// <summary>Present, but „adb“ does not check out.</summary>
    Invalid,

    /// <summary>Valid, but /P says something else: the sealed rights count.</summary>
    DiffersFromP,

    /// <summary>Valid, but byte 8 says something other than /EncryptMetadata: the sealed value counts.</summary>
    EncryptMetadataDiffers,
}
