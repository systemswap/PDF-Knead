using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The algorithms 2 to 13 of the standard security handler (ISO 32000-2, 7.6.4.3 and 7.6.4.4), step by step: each step against an independent
/// computation written out in this file (MD5, SHA-2 and AES of the runtime; RC4 and algorithm 2.B written anew here, not taken from PDF-Knead) and
/// against files of a public test collection with known passwords (TestPDFs/testsammlungen). Konformitätsabgleich M4.
/// </summary>
public sealed partial class EncryptionNormTests
{
    /// <summary>The folder of the collection files with known passwords (user „view“, owner „master“; V4*.pdf with empty passwords).</summary>
    private const string M4cCollection = "testsammlungen/qpdf/qpdf/qtest/qpdf";

    private const string M4cR2 = "enc-R2,V1,U=view,O=master.pdf", M4cR3 = "enc-R3,V2,U=view,O=master.pdf", M4cR4 = "V4.pdf",
        M4cR4ClearMeta = "V4-clearmeta.pdf", M4cR6 = "enc-XI-R6,V5,U=view,O=master.pdf";

    /// <summary>A first part of /ID for the dictionaries built in these tests.</summary>
    private static readonly byte[] M4cId = [.. Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i))];

    /// <summary>Independent computations of the algorithms, written from the standard here and not taken from PDF-Knead.</summary>
    private static class M4cRef
    {
        /// <summary>The fixed padding string of algorithm 2, step (a).</summary>
        public static readonly byte[] Padding = Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");

        /// <summary>RC4 (key scheduling, then the keystream XOR the data).</summary>
        public static byte[] Rc4(byte[] key, byte[] data)
        {
            var s = new int[256];
            for (var i = 0; i < 256; i++) s[i] = i;
            for (int i = 0, j = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) % 256;
                (s[i], s[j]) = (s[j], s[i]);
            }
            var output = new byte[data.Length];
            for (int n = 0, i = 0, j = 0; n < data.Length; n++)
            {
                i = (i + 1) % 256;
                j = (j + s[i]) % 256;
                (s[i], s[j]) = (s[j], s[i]);
                output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) % 256]);
            }
            return output;
        }

        /// <summary>Algorithm 2, step (a): the first 32 bytes, filled up with the first 32 − n bytes of the padding.</summary>
        public static byte[] Pad(byte[] raw) => [.. raw.Take(32), .. Padding.Take(32 - Math.Min(32, raw.Length))];

        /// <summary>The 32 bits of /P, low-order byte first.</summary>
        public static byte[] PBytes(long p) => [(byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24)];

        /// <summary>Each byte of the key XOR a counter.</summary>
        public static byte[] Xor(byte[] key, int x) => [.. key.Select(b => (byte)(b ^ x))];

        /// <summary>Algorithm 2: MD5 over padded password, /O, /P, /ID[0] (and FF FF FF FF from revision 4 with plain metadata); from revision 3
        /// <paramref name="rounds"/> times MD5 over the first <paramref name="roundBytes"/> (n) bytes; the first n bytes.</summary>
        public static byte[] FileKey(byte[] padded, byte[] o, byte[] pBytes, byte[] id0, int r, int n, bool encryptMetadata = true, int rounds = 50,
            int? roundBytes = null)
        {
            byte[] input = [.. padded, .. o.Take(32), .. pBytes, .. id0, .. (r >= 4 && !encryptMetadata ? new byte[] { 0xFF, 0xFF, 0xFF, 0xFF } : [])];
            var hash = MD5.HashData(input);
            if (r >= 3)
                for (var i = 0; i < rounds; i++) hash = MD5.HashData(hash.Take(roundBytes ?? n).ToArray());
            return [.. hash.Take(n)];
        }

        /// <summary>Algorithm 3, steps (a) to (d): MD5 of the padded owner password, from revision 3 fifty times MD5 over the whole hash; n bytes.</summary>
        public static byte[] OwnerKey(byte[] paddedOwner, int r, int n)
        {
            var hash = MD5.HashData(paddedOwner);
            if (r >= 3)
                for (var i = 0; i < 50; i++) hash = MD5.HashData(hash);
            return [.. hash.Take(n)];
        }

        /// <summary>Algorithm 3, steps (e) to (h): the padded user password with RC4, from revision 3 <paramref name="rounds"/> more times with
        /// the key XOR 1, 2, ….</summary>
        public static byte[] O(byte[] paddedOwner, byte[] paddedUser, int r, int n, int rounds = 19)
        {
            var key = OwnerKey(paddedOwner, r, n);
            var value = Rc4(key, paddedUser);
            if (r >= 3)
                for (var i = 1; i <= rounds; i++) value = Rc4(Xor(key, i), value);
            return value;
        }

        /// <summary>Algorithm 4: the padding encrypted with RC4 and the file key.</summary>
        public static byte[] U2(byte[] key) => Rc4(key, Padding);

        /// <summary>Algorithm 5, steps (b) to (e): MD5 of padding and /ID[0], RC4, then <paramref name="rounds"/> times with the key XOR 1, 2, …
        /// (the 16 bytes before the arbitrary rest).</summary>
        public static byte[] U3(byte[] key, byte[] id0, int rounds = 19)
        {
            var value = Rc4(key, MD5.HashData([.. Padding, .. id0]));
            for (var i = 1; i <= rounds; i++) value = Rc4(Xor(key, i), value);
            return value;
        }

        /// <summary>Algorithm 7, step (b): /O decrypted with the owner key — once (revision 2), or twenty times with the key XOR 19 down to 0.</summary>
        public static byte[] UserFromOwner(byte[] paddedOwner, byte[] o, int r, int n, int lowest = 0)
        {
            var key = OwnerKey(paddedOwner, r, n);
            var value = o.Take(32).ToArray();
            if (r == 2) return Rc4(key, value);
            for (var x = 19; x >= lowest; x--) value = Rc4(Xor(key, x), value);
            return value;
        }

        /// <summary>One round of algorithm 2.B as logged by <see cref="Hash"/>.</summary>
        public sealed record Round(int Choice, int K1Length, byte LastOfE, int KLength);

        /// <summary>
        /// Algorithm 2.B: K = SHA-256(input); per round K1 = 64 × (password ‖ K ‖ udata), E = AES-128-CBC(key K[0..16], vector K[16..32]) without
        /// padding, the first 16 bytes of E as a big-endian number modulo 3 choose SHA-256/384/512 of E as the new K; at least 64 rounds, then on
        /// while the last byte of E is greater than the number of rounds done − 32; the first 32 bytes of K.
        /// </summary>
        public static byte[] Hash(byte[] password, byte[] salt, byte[] udata, List<Round>? log = null, bool swapKeyAndVector = false)
        {
            var k = SHA256.HashData([.. password, .. salt, .. udata]);
            var e = Array.Empty<byte>();
            var done = 0;
            while (done < 64 || e[^1] > done - 32)
            {
                byte[] unit = [.. password, .. k, .. udata];
                var k1 = Enumerable.Repeat(unit, 64).SelectMany(x => x).ToArray();
                using (var aes = Aes.Create())
                {
                    aes.Key = swapKeyAndVector ? k[16..32] : k[..16];
                    e = aes.EncryptCbc(k1, swapKeyAndVector ? k[..16] : k[16..32], PaddingMode.None);
                }
                var choice = (int)(new BigInteger(e.AsSpan(0, 16), isUnsigned: true, isBigEndian: true) % 3);
                k = choice switch { 0 => SHA256.HashData(e), 1 => SHA384.HashData(e), _ => SHA512.HashData(e) };
                log?.Add(new Round(choice, k1.Length, e[^1], k.Length));
                done++;
            }
            return k[..32];
        }

        /// <summary>AES-256-CBC without padding and with a zero vector, decrypting (/OE, /UE).</summary>
        public static byte[] CbcDecrypt(byte[] key, byte[] data)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            return aes.DecryptCbc(data, new byte[16], PaddingMode.None);
        }

        /// <summary>AES-256-ECB without padding (/Perms).</summary>
        public static byte[] Ecb(byte[] key, byte[] data, bool encrypt)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            return encrypt ? aes.EncryptEcb(data, PaddingMode.None) : aes.DecryptEcb(data, PaddingMode.None);
        }
    }

    // ------------------------------------------------------------ helpers

    private static string M4cFile(string name) => TestPdfs.File(M4cCollection, name);

    /// <summary>The /Encrypt dictionary and /ID of a collection file, opened with its password.</summary>
    private static (PdfDictionary Encrypt, PdfArray Id) M4cEncryptOf(string name, string password)
    {
        using var doc = PdfDocument.Open(M4cFile(name), password);
        return (M4cCopy(doc.EncryptDictionary!), (PdfArray)doc.Trailer.Get(PdfName.ID));
    }

    /// <summary>A copy of a dictionary with entries replaced (null: removed).</summary>
    private static PdfDictionary M4cCopy(PdfDictionary source, params (string Key, PdfObject? Value)[] changes)
    {
        var copy = new PdfDictionary();
        foreach (var (key, value) in source.Entries) copy.Set(key, value);
        foreach (var (key, value) in changes)
            if (value is null) copy.Remove(PdfName.Get(key));
            else copy.Set(PdfName.Get(key), value);
        return copy;
    }

    private static byte[] M4cBytes(PdfDictionary d, string key) => d.GetString(PdfName.Get(key))!.Bytes;

    private static byte[] M4cFlip(byte[] bytes, int index)
    {
        var copy = (byte[])bytes.Clone();
        copy[index] ^= 0x01;
        return copy;
    }

    private static PdfArray M4cIds(byte[] first, byte[]? second = null) => new([new PdfString(first), new PdfString(second ?? first)]);

    private static byte[] M4cId0(PdfArray id) => ((PdfString)id.Get(0)).Bytes;

    /// <summary>The key length n in bytes of a file of revision 2 to 4 as algorithm 2 takes it (revision 2: 5; else /Length, default 128 with V 4).</summary>
    private static int M4cKeyLength(PdfDictionary encrypt, int r) =>
        r == 2 ? 5 : (int)(encrypt.GetInteger(PdfName.Length) ?? (encrypt.GetInteger(PdfName.Get("V")) >= 4 ? 128 : 40)) / 8;

    /// <summary>A dictionary of revision 2 or 3 assembled from given values (V 1 or 2), for keys built independently in the test.</summary>
    private static PdfDictionary M4cLegacy(int r, int? lengthBits, byte[] o, byte[] u, long p)
    {
        var d = new PdfDictionary();
        d.Set(PdfName.Filter, PdfName.Get("Standard"));
        d.Set(PdfName.Get("V"), PdfInteger.Of(r == 2 ? 1 : 2));
        d.Set(PdfName.Get("R"), PdfInteger.Of(r));
        if (lengthBits is { } bits) d.Set(PdfName.Length, PdfInteger.Of(bits));
        d.Set(PdfName.Get("O"), new PdfString(o));
        d.Set(PdfName.Get("U"), new PdfString(u));
        d.Set(PdfName.Get("P"), PdfInteger.Of(p));
        return d;
    }

    /// <summary>Whether a handler of AES-256 encrypts with exactly <paramref name="key"/>: a string it encrypts decrypts with that key.</summary>
    private static bool M4cUsesKey(StandardSecurityHandler handler, byte[] key)
    {
        var plain = "sixteen byte blk"u8.ToArray();
        var sealedBytes = handler.EncryptString(new PdfObjectId(7, 0), plain);
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(sealedBytes.AsSpan(16), sealedBytes.AsSpan(0, 16), PaddingMode.None).AsSpan(0, 16).SequenceEqual(plain);
    }

    /// <summary>The file key of a revision-6 dictionary from /UE, computed independently with the user password.</summary>
    private static byte[] M4cKeyFromUe(PdfDictionary encrypt, byte[] userPassword)
    {
        var u = M4cBytes(encrypt, "U");
        return M4cRef.CbcDecrypt(M4cRef.Hash(userPassword, u[40..48], []), M4cBytes(encrypt, "UE")[..32]);
    }

    /// <summary>The rounds of algorithm 2.B as PDF-Knead runs them: (round, remainder chosen, last byte of E).</summary>
    private static List<(int Round, int Choice, byte LastOfE)> M4cRounds(byte[] password, byte[] salt, byte[] udata, out byte[] result)
    {
        var rounds = new List<(int, int, byte)>();
        result = StandardSecurityHandler.Hash(6, password, salt, udata, (round, choice, last) => rounds.Add((round, choice, last)));
        return rounds;
    }

    // ------------------------------------------------------------ 7.6.4.3.1: which algorithm per revision

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.1: up to revision 4 the file key comes from algorithm 2, from revision 6 from algorithm 2.A — a file of each revision of
    /// the collection opens with its known password, the revision is recognised and the first page has text; a wrong password is refused.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(M4cR2, "view", 2)]
    [InlineData(M4cR3, "view", 3)]
    [InlineData(M4cR4, "", 4)]
    [InlineData(M4cR6, "view", 6)]
    public void Algorithm2And2A_ForeignFilePerRevision_OpensWithItsPassword(string name, string password, int revision)
    {
        using (var doc = PdfDocument.Open(M4cFile(name), password))
        {
            Assert.Equal(revision, doc.Security!.Revision);
            Assert.False(string.IsNullOrWhiteSpace(FirstPageText(doc)));
        }
        Assert.True(Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(M4cFile(name), "falsch")).WrongPassword);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.1: the two ways differ — algorithm 2 hashes /ID[0], algorithm 2.A does not. With a changed /ID[0] the password of the
    /// revision-4 file no longer opens it, that of the revision-6 file still does.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(M4cR4, "", false)]
    [InlineData(M4cR6, "view", true)]
    public void Algorithm2And2A_FirstIdChanged_Revision4FailsRevision6Opens(string name, string password, bool opens)
    {
        var (encrypt, id) = M4cEncryptOf(name, password);
        var changed = M4cIds(M4cFlip(M4cId0(id), 0), ((PdfString)id.Get(1)).Bytes);
        Assert.NotNull(StandardSecurityHandler.Open(encrypt, id, password));
        if (opens) Assert.NotNull(StandardSecurityHandler.Open(encrypt, changed, password));
        else Assert.Throws<PdfPasswordException>(() => StandardSecurityHandler.Open(encrypt, changed, password));
    }

    // ------------------------------------------------------------ 7.6.4.3.2: algorithm 2

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.2, algorithm 2, step (a): the password is converted to PDFDocEncoding — „•“ is 80 and „€“ is A0 there (Latin-1 has no
    /// „•“ and puts „€“ nowhere). /O and /U of a new RC4 protection equal the values computed independently from those bytes; the passwords open.
    /// </summary>
    [Fact]
    public void Algorithm2_PasswordWithPdfDocEncodingCharacters_OAndUFromThoseBytes()
    {
        var (encrypt, _) = StandardSecurityHandler.Create(PdfCipher.Rc4, "a•€b", "c•€d", PdfPermissions.Print, M4cId);
        var paddedUser = M4cRef.Pad([0x61, 0x80, 0xA0, 0x62]);
        var paddedOwner = M4cRef.Pad([0x63, 0x80, 0xA0, 0x64]);
        var o = M4cBytes(encrypt, "O");
        Assert.Equal(M4cRef.O(paddedOwner, paddedUser, 3, 16), o);
        var key = M4cRef.FileKey(paddedUser, o, M4cRef.PBytes(encrypt.GetInteger(PdfName.Get("P"))!.Value), M4cId, 3, 16);
        Assert.Equal(M4cRef.U3(key, M4cId), M4cBytes(encrypt, "U")[..16]);
        Assert.False(StandardSecurityHandler.Open(encrypt, M4cIds(M4cId), "a•€b").IsOwner);
        Assert.True(StandardSecurityHandler.Open(encrypt, M4cIds(M4cId), "c•€d").IsOwner);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.2, algorithm 2, step (a): the padding string is the fixed one of the standard, byte for byte; a short password is filled
    /// up with its first 32 − n bytes, an empty one becomes the whole string, a longer one is cut to 32 bytes.
    /// </summary>
    [Fact]
    public void Algorithm2_Padding_FixedStringFillsUpOrCuts()
    {
        Assert.Equal(M4cRef.Padding, StandardSecurityHandler.Padding);
        Assert.Equal((byte[])[.. "abc"u8, .. M4cRef.Padding[..29]], StandardSecurityHandler.Pad("abc"u8.ToArray()));
        Assert.Equal(M4cRef.Padding, StandardSecurityHandler.Pad([]));
        var forty = Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz0123456789ABCD");
        Assert.Equal(forty[..32], StandardSecurityHandler.Pad(forty));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.2, algorithm 2, step (a), 7.6.4.4.1 (passwords up to revision 4 have at most 32 characters) and 7.6.4.4.2, algorithm 3,
    /// step (a): for RC4 and AES-128, user and owner passwords of 40 characters count with their first 32 — those open (the owner one as owner),
    /// 31 do not.
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Rc4)]
    [InlineData(PdfCipher.Aes128)]
    public void Algorithm2And3_PasswordOf40Characters_OnlyTheFirst32Count(PdfCipher cipher)
    {
        const string user = "abcdefghijklmnopqrstuvwxyz0123456789ABCD", owner = "ZYXWVUTSRQPONMLKJIHGFEDCBA9876543210zyxw";
        var (encrypt, _) = StandardSecurityHandler.Create(cipher, user, owner, PdfPermissions.Print, M4cId);
        var ids = M4cIds(M4cId);
        Assert.False(StandardSecurityHandler.Open(encrypt, ids, user[..32]).IsOwner);
        Assert.True(StandardSecurityHandler.Open(encrypt, ids, owner[..32]).IsOwner);
        Assert.Throws<PdfPasswordException>(() => StandardSecurityHandler.Open(encrypt, ids, user[..31]));
        Assert.Throws<PdfPasswordException>(() => StandardSecurityHandler.Open(encrypt, ids, owner[..31]));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.2, algorithm 2, steps (b) to (g) for revision 2 (no rounds): the file key of the collection file of revision 2 with „view“ is
    /// the first 5 bytes of MD5(padded password ‖ /O ‖ /P ‖ /ID[0]), computed here — it is genuine, as algorithm 4 with it gives the /U of the file
    /// — and PDF-Knead's algorithm 2 gives the same; an unpadded password gives another key.
    /// </summary>
    [TestPdfsFact]
    public void Algorithm2_ForeignRevision2File_KnownAnswer()
    {
        var (encrypt, id) = M4cEncryptOf(M4cR2, "view");
        var o = M4cBytes(encrypt, "O");
        var p = encrypt.GetInteger(PdfName.Get("P"))!.Value;
        var key = M4cRef.FileKey(M4cRef.Pad("view"u8.ToArray()), o, M4cRef.PBytes(p), M4cId0(id), 2, 5);
        Assert.Equal(M4cRef.U2(key), M4cBytes(encrypt, "U")[..32]);
        Assert.Equal(key, StandardSecurityHandler.ComputeKey(StandardSecurityHandler.Pad("view"u8.ToArray()), o, (int)p, M4cId0(id), 2, 5, true));
        Assert.NotEqual(key, StandardSecurityHandler.ComputeKey("view"u8.ToArray(), o, (int)p, M4cId0(id), 2, 5, true));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.2, algorithm 2, step (c): /O enters the hash — two /O differing only in byte 31 give different keys, each the one computed
    /// here; an /O of 48 bytes enters with its first 32 (so the key is that of those).
    /// </summary>
    [Fact]
    public void Algorithm2_O_EntersWithItsFirst32Bytes()
    {
        var padded = M4cRef.Pad("nutzer"u8.ToArray());
        var o1 = Enumerable.Range(0, 32).Select(i => (byte)(i * 3 + 1)).ToArray();
        var o2 = M4cFlip(o1, 31);
        var pBytes = M4cRef.PBytes(-3904);
        var key1 = StandardSecurityHandler.ComputeKey(padded, o1, -3904, M4cId, 3, 16, true);
        var key2 = StandardSecurityHandler.ComputeKey(padded, o2, -3904, M4cId, 3, 16, true);
        Assert.Equal(M4cRef.FileKey(padded, o1, pBytes, M4cId, 3, 16), key1);
        Assert.Equal(M4cRef.FileKey(padded, o2, pBytes, M4cId, 3, 16), key2);
        Assert.NotEqual(key1, key2);
        Assert.Equal(key1, StandardSecurityHandler.ComputeKey(padded, [.. o1, .. new byte[16]], -3904, M4cId, 3, 16, true));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.2, algorithm 2, step (d): /P enters as an unsigned 32-bit number, low-order byte first (−3904 = C0 F0 FF FF). A
    /// dictionary built here opens whether /P is written as −3904 or as the unsigned 4294963392; one whose /U was made with the bytes in the other
    /// order does not.
    /// </summary>
    [Theory]
    [InlineData(-3904L, true, true)]
    [InlineData(4294963392L, true, true)]
    [InlineData(-3904L, false, false)]
    public void Algorithm2_P_Unsigned32BitsLowOrderFirst(long written, bool lowOrderFirst, bool opens)
    {
        var paddedUser = M4cRef.Pad("nutzer"u8.ToArray());
        var o = M4cRef.O(M4cRef.Pad("eigner"u8.ToArray()), paddedUser, 3, 16);
        byte[] pBytes = lowOrderFirst ? [0xC0, 0xF0, 0xFF, 0xFF] : [0xFF, 0xFF, 0xF0, 0xC0];
        var key = M4cRef.FileKey(paddedUser, o, pBytes, M4cId, 3, 16);
        var encrypt = M4cLegacy(3, 128, o, [.. M4cRef.U3(key, M4cId), .. new byte[16]], written);
        if (opens) Assert.False(StandardSecurityHandler.Open(encrypt, M4cIds(M4cId), "nutzer").IsOwner);
        else Assert.Throws<PdfPasswordException>(() => StandardSecurityHandler.Open(encrypt, M4cIds(M4cId), "nutzer"));
        Assert.Equal(lowOrderFirst, StandardSecurityHandler.ComputeKey(paddedUser, o, -3904, M4cId, 3, 16, true).AsSpan().SequenceEqual(key));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.2, algorithm 2, step (e): only the first element of /ID enters — in the collection file of revision 3, a changed /ID[0] makes
    /// „view“ fail, a changed /ID[1] does not.
    /// </summary>
    [TestPdfsFact]
    public void Algorithm2_FirstIdElementOnly_EntersTheKey()
    {
        var (encrypt, id) = M4cEncryptOf(M4cR3, "view");
        var first = M4cId0(id);
        var second = ((PdfString)id.Get(1)).Bytes;
        Assert.Throws<PdfPasswordException>(() => StandardSecurityHandler.Open(encrypt, M4cIds(M4cFlip(first, 15), second), "view"));
        Assert.False(StandardSecurityHandler.Open(encrypt, M4cIds(first, M4cFlip(second, 15)), "view").IsOwner);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.2, algorithm 2, step (f): from revision 4 with unencrypted metadata, FF FF FF FF enters the hash. The collection file of
    /// revision 4 with /EncryptMetadata false opens with its (empty) password and its /U is that of the key computed here with the four bytes;
    /// with /EncryptMetadata turned to true it no longer opens. In a revision-3 file /EncryptMetadata false changes nothing.
    /// </summary>
    [TestPdfsFact]
    public void Algorithm2_UnencryptedMetadata_FourFFBytesFromRevision4()
    {
        var (encrypt, id) = M4cEncryptOf(M4cR4ClearMeta, "");
        Assert.Equal(4L, encrypt.GetInteger(PdfName.Get("R")));
        Assert.False(encrypt.GetBoolean(PdfName.Get("EncryptMetadata")));
        var handler = StandardSecurityHandler.Open(encrypt, id, "");
        Assert.False(handler.EncryptMetadata);
        var n = M4cKeyLength(encrypt, 4);
        var key = M4cRef.FileKey(M4cRef.Pad([]), M4cBytes(encrypt, "O"), M4cRef.PBytes(encrypt.GetInteger(PdfName.Get("P"))!.Value), M4cId0(id), 4, n,
            encryptMetadata: false);
        Assert.Equal(M4cRef.U3(key, M4cId0(id)), M4cBytes(encrypt, "U")[..16]);
        Assert.Throws<PdfPasswordException>(() => StandardSecurityHandler.Open(M4cCopy(encrypt, ("EncryptMetadata", PdfBoolean.True)), id, ""));

        var (r3, id3) = M4cEncryptOf(M4cR3, "view");
        Assert.False(StandardSecurityHandler.Open(M4cCopy(r3, ("EncryptMetadata", PdfBoolean.False)), id3, "view").IsOwner);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.2, algorithm 2, step (h): from revision 3, fifty times MD5 over the first n bytes of the previous hash. With /Length 40
    /// (n = 5, so the first n bytes differ from the whole hash) a dictionary built here opens when its key took 50 rounds over 5 bytes — not 49, not
    /// 51, not 50 over 16 bytes.
    /// </summary>
    [Theory]
    [InlineData(50, 5, true)]
    [InlineData(49, 5, false)]
    [InlineData(51, 5, false)]
    [InlineData(50, 16, false)]
    public void Algorithm2_Revision3_FiftyRoundsOverTheFirstNBytes(int rounds, int roundBytes, bool opens)
    {
        var paddedUser = M4cRef.Pad("nutzer"u8.ToArray());
        var o = M4cRef.O(M4cRef.Pad("eigner"u8.ToArray()), paddedUser, 3, 5);
        var key = M4cRef.FileKey(paddedUser, o, M4cRef.PBytes(-3904), M4cId, 3, 5, rounds: rounds, roundBytes: roundBytes);
        var encrypt = M4cLegacy(3, 40, o, [.. M4cRef.U3(key, M4cId), .. new byte[16]], -3904);
        if (opens) Assert.Equal(40, StandardSecurityHandler.Open(encrypt, M4cIds(M4cId), "nutzer").KeyBits);
        else Assert.Throws<PdfPasswordException>(() => StandardSecurityHandler.Open(encrypt, M4cIds(M4cId), "nutzer"));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.2, algorithm 2, step (i): the key has n bytes — 5 at revision 2 whatever /Length says (the collection file of revision 2 with
    /// /Length 128 added still opens with 40 bits), from revision 3 after /Length (the collection file of revision 3: 128 bits; /Length 40: 40 bits, see
    /// <see cref="Algorithm2_Revision3_FiftyRoundsOverTheFirstNBytes"/>).
    /// </summary>
    [TestPdfsFact]
    public void Algorithm2_KeyLength_Revision2FiveBytesRevision3AfterLength()
    {
        var (r2, id2) = M4cEncryptOf(M4cR2, "view");
        Assert.Equal(40, StandardSecurityHandler.Open(M4cCopy(r2, ("Length", PdfInteger.Of(128))), id2, "view").KeyBits);
        var (r3, id3) = M4cEncryptOf(M4cR3, "view");
        Assert.Equal(128L, r3.GetInteger(PdfName.Length));
        Assert.Equal(128, StandardSecurityHandler.Open(r3, id3, "view").KeyBits);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.2, algorithm 2, step (i): a new RC4 and AES-128 protection has a key of 16 bytes — RC4 (V 2) says so in /Length,
    /// AES-128 (V 4) in the /Length of its crypt filter, in bytes (Table 20, 25; docs/FEHLERGEDAECHTNIS.md, No. 1096, 1108).
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Rc4)]
    [InlineData(PdfCipher.Aes128)]
    public void Algorithm2_NewProtection_KeyOf128Bits(PdfCipher cipher)
    {
        var (encrypt, handler) = StandardSecurityHandler.Create(cipher, "nutzer", "eigner", PdfPermissions.Print, M4cId);
        Assert.Equal(128, handler.KeyBits);
        if (cipher == PdfCipher.Rc4) Assert.Equal(128L, encrypt.GetInteger(PdfName.Length));
        else Assert.Equal(16L, encrypt.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!.GetInteger(PdfName.Length));
        Assert.Equal(128, StandardSecurityHandler.Open(encrypt, M4cIds(M4cId), "nutzer").KeyBits);
    }

    // ------------------------------------------------------------ 7.6.4.3.3: algorithm 2.A

    /// <summary>ISO 32000-2, 7.6.4.3.3, algorithm 2.A: /O and /U have 48 bytes — with 47 the encryption data are reported incomplete, not a wrong password.</summary>
    [Theory]
    [InlineData("O")]
    [InlineData("U")]
    public void Algorithm2A_OOrUOf47Bytes_ReportedIncomplete(string key)
    {
        var (encrypt, _) = StandardSecurityHandler.Create(PdfCipher.Aes256, "nutzer", "eigner", PdfPermissions.Print, M4cId);
        var shortened = M4cCopy(encrypt, (key, new PdfString(M4cBytes(encrypt, key)[..47])));
        var error = Assert.Throws<PdfException>(() => StandardSecurityHandler.Open(shortened, M4cIds(M4cId), "nutzer"));
        Assert.Contains("unvollständig", error.Message);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.3, algorithm 2.A: /U is 32 bytes of hash, 8 of validation salt, 8 of key salt — in the collection file of revision 6, /U[0..32]
    /// is algorithm 2.B (computed here) over „view“ and /U[32..40]; with the two salts swapped neither password opens.
    /// </summary>
    [TestPdfsFact]
    public void Algorithm2A_ForeignRevision6_UHashOverValidationSalt()
    {
        var (encrypt, id) = M4cEncryptOf(M4cR6, "view");
        var u = M4cBytes(encrypt, "U");
        Assert.Equal(M4cRef.Hash("view"u8.ToArray(), u[32..40], []), u[..32]);
        var swapped = M4cCopy(encrypt, ("U", new PdfString([.. u[..32], .. u[40..48], .. u[32..40]])));
        Assert.Throws<PdfPasswordException>(() => StandardSecurityHandler.Open(swapped, id, "view"));
        Assert.Throws<PdfPasswordException>(() => StandardSecurityHandler.Open(swapped, id, "master"));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.3, algorithm 2.A, step (b): the UTF-8 password is cut to 127 bytes — 200 ASCII characters give their first 127, 126 × „a“
    /// and „ü“ give 127 bytes ending in C3 (cut inside the character); a file with the 200-character password opens with its first 127 characters,
    /// not with 126.
    /// </summary>
    [Fact]
    public void Algorithm2A_UtfPassword_CutTo127Bytes()
    {
        var long200 = string.Concat(Enumerable.Range(0, 200).Select(i => (char)('a' + i % 26)));
        Assert.Equal(Encoding.ASCII.GetBytes(long200[..127]), StandardSecurityHandler.PrepareAesPassword(long200));
        var umlaut = StandardSecurityHandler.PrepareAesPassword(new string('a', 126) + "ü");
        Assert.Equal(127, umlaut.Length);
        Assert.Equal(0xC3, umlaut[^1]);
        var (encrypt, _) = StandardSecurityHandler.Create(PdfCipher.Aes256, long200, "eigner", PdfPermissions.Print, M4cId);
        Assert.False(StandardSecurityHandler.Open(encrypt, M4cIds(M4cId), long200[..127]).IsOwner);
        Assert.Throws<PdfPasswordException>(() => StandardSecurityHandler.Open(encrypt, M4cIds(M4cId), long200[..126]));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.3, algorithm 2.A, step (c), and 7.6.4.4.11, algorithm 12: the owner password is checked with 2.B over password, owner
    /// validation salt and the 48 bytes of /U. In the collection file of revision 6 „master“ is the owner; /O[0..32] is 2.B (computed here) over „master“,
    /// /O[32..40] and /U. With /O byte 0 or /U byte 47 changed „master“ fails (/U enters), „view“ still opens as user.
    /// </summary>
    [TestPdfsFact]
    public void Algorithm2AAnd12_ForeignRevision6_OwnerPasswordOverOAndU()
    {
        var (encrypt, id) = M4cEncryptOf(M4cR6, "view");
        var o = M4cBytes(encrypt, "O");
        var u = M4cBytes(encrypt, "U");
        Assert.Equal(M4cRef.Hash("master"u8.ToArray(), o[32..40], u[..48]), o[..32]);
        Assert.True(StandardSecurityHandler.Open(encrypt, id, "master").IsOwner);
        foreach (var changed in new[] { M4cCopy(encrypt, ("O", new PdfString(M4cFlip(o, 0)))), M4cCopy(encrypt, ("U", new PdfString(M4cFlip(u, 47)))) })
        {
            Assert.True(Assert.Throws<PdfPasswordException>(() => StandardSecurityHandler.Open(changed, id, "master")).WrongPassword);
            Assert.False(StandardSecurityHandler.Open(changed, id, "view").IsOwner);
        }
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.3, algorithm 2.A, steps (d) and (e): the file key is /OE decrypted with 2.B over owner password, owner key salt and /U,
    /// or /UE with 2.B over user password and user key salt (AES-256-CBC, no padding, zero vector). In the collection file of revision 6 both, computed
    /// here, give the same key, and PDF-Knead encrypts with it under either password. A changed /OE gives the owner another key (/Perms then does
    /// not check out) and leaves the user's; a changed /UE the other way round.
    /// </summary>
    [TestPdfsFact]
    public void Algorithm2A_ForeignRevision6_FileKeyFromOEAndUE()
    {
        var (encrypt, id) = M4cEncryptOf(M4cR6, "view");
        var o = M4cBytes(encrypt, "O");
        var u = M4cBytes(encrypt, "U");
        var oe = M4cBytes(encrypt, "OE");
        var ue = M4cBytes(encrypt, "UE");
        var fromOe = M4cRef.CbcDecrypt(M4cRef.Hash("master"u8.ToArray(), o[40..48], u[..48]), oe[..32]);
        var fromUe = M4cRef.CbcDecrypt(M4cRef.Hash("view"u8.ToArray(), u[40..48], []), ue[..32]);
        Assert.Equal(fromOe, fromUe);
        foreach (var password in new[] { "master", "view" })
        {
            var handler = StandardSecurityHandler.Open(encrypt, id, password);
            Assert.True(M4cUsesKey(handler, fromOe));
            Assert.Equal(PermsState.Valid, handler.Perms);
        }

        var oeChanged = M4cCopy(encrypt, ("OE", new PdfString(M4cFlip(oe, 5))));
        var owner = StandardSecurityHandler.Open(oeChanged, id, "master");
        Assert.False(M4cUsesKey(owner, fromOe));
        Assert.Equal(PermsState.Invalid, owner.Perms);
        Assert.True(M4cUsesKey(StandardSecurityHandler.Open(oeChanged, id, "view"), fromOe));

        var ueChanged = M4cCopy(encrypt, ("UE", new PdfString(M4cFlip(ue, 5))));
        var user = StandardSecurityHandler.Open(ueChanged, id, "view");
        Assert.False(M4cUsesKey(user, fromOe));
        Assert.Equal(PermsState.Invalid, user.Perms);
        Assert.True(M4cUsesKey(StandardSecurityHandler.Open(ueChanged, id, "master"), fromOe));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.3, algorithm 2.A, step (f), and 7.6.4.4.12, algorithm 13: /Perms decrypted with the file key must hold „adb“ in bytes 9 to
    /// 11. Sealed anew here with the file key (computed from /UE) and „adb“ it checks out; with „adx“ it is invalid, the document says the rights are
    /// not sealed, and /P counts.
    /// </summary>
    [Theory]
    [InlineData("adb", PermsState.Valid)]
    [InlineData("adx", PermsState.Invalid)]
    public void Algorithm2AAnd13_PermsSealedAnew_AdbDecides(string marker, PermsState expected)
    {
        var bytes = PdfKnead.Tests.Pdf.TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: "nutzer", owner: "eigner", permissions: PdfPermissions.Print, patchEncrypt: d =>
        {
            var key = M4cKeyFromUe(d, "nutzer"u8.ToArray());
            var p = (int)d.GetInteger(PdfName.Get("P"))!.Value;
            var block = new byte[16];
            M4cRef.PBytes(p).CopyTo(block, 0);
            block[4] = block[5] = block[6] = block[7] = 0xFF;
            block[8] = (byte)'T';
            Encoding.ASCII.GetBytes(marker).CopyTo(block, 9);
            d.Set(PdfName.Get("Perms"), new PdfString(M4cRef.Ecb(key, block, encrypt: true)));
        });
        using var doc = PdfDocument.Open(bytes, "nutzer");
        Assert.Equal(expected, doc.Security!.Perms);
        Assert.Equal(PdfPermissions.Print | PdfPermissions.Accessibility, doc.Security.Permissions);
        Assert.Equal(expected == PermsState.Invalid, doc.Problems.Any(p => p.Contains("nicht versiegelt", StringComparison.Ordinal)));
        Assert.Equal(expected == PermsState.Invalid, doc.Security.SealedP is null);
    }

    // ------------------------------------------------------------ 7.6.4.3.4: algorithm 2.B

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.4, algorithm 2.B: K starts as SHA-256 of the input — revision 5 (which stops there) gives exactly that; revision 6 builds
    /// on it with the rounds and equals the computation here.
    /// </summary>
    [Fact]
    public void Algorithm2B_Start_Sha256OfTheInput()
    {
        var password = "kennwort"u8.ToArray();
        byte[] salt = [1, 2, 3, 4, 5, 6, 7, 8];
        var udata = Enumerable.Range(0, 48).Select(i => (byte)(i * 5)).ToArray();
        Assert.Equal(SHA256.HashData([.. password, .. salt, .. udata]), StandardSecurityHandler.Hash(5, password, salt, udata));
        Assert.Equal(M4cRef.Hash(password, salt, udata), StandardSecurityHandler.Hash(6, password, salt, udata));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.4, algorithm 2.B, steps (a) and (b): K1 is 64 repetitions of password, K and — for the owner — the 48 bytes of /U
    /// (64 × (8 + 32 + 48) bytes in the first round), for the user only password and K (64 × (8 + 32)); E is AES-128-CBC with the first 16 bytes of
    /// K as key and the second as vector. PDF-Knead's result equals the computation here; without /U, or with key and vector swapped, it would differ.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Algorithm2B_OwnerAndUserCase_AsTheIndependentComputation(bool owner)
    {
        var password = "kennwort"u8.ToArray();
        byte[] salt = [8, 7, 6, 5, 4, 3, 2, 1];
        var udata = owner ? Enumerable.Range(0, 48).Select(i => (byte)(255 - i)).ToArray() : [];
        var log = new List<M4cRef.Round>();
        var expected = M4cRef.Hash(password, salt, udata, log);
        Assert.Equal(64 * (8 + 32 + (owner ? 48 : 0)), log[0].K1Length);
        Assert.Equal(expected, StandardSecurityHandler.Hash(6, password, salt, udata));
        Assert.NotEqual(expected, M4cRef.Hash(password, salt, udata, swapKeyAndVector: true));
        if (owner) Assert.NotEqual(expected, StandardSecurityHandler.Hash(6, password, salt, []));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.4, algorithm 2.B, steps (c) and (d): the first 16 bytes of E as a big-endian number modulo 3 choose SHA-256, SHA-384 or
    /// SHA-512. Vectors with remainder 0, 1, 2 (as big numbers) have the byte sum with the same remainder (256 ≡ 1 modulo 3, so PDF-Knead may sum);
    /// over several inputs the remainder PDF-Knead chooses in each round equals that computed here as a big number, all three occur, and the results
    /// agree.
    /// </summary>
    [Fact]
    public void Algorithm2B_HashChoice_RemainderOfEModulo3()
    {
        foreach (var (vector, remainder) in new (byte[], int)[] { ([.. new byte[15], 0x03], 0), ([0x01, .. new byte[15]], 1), ([0xFF, .. new byte[14], 0xFE], 2) })
        {
            Assert.Equal(remainder, (int)(new BigInteger(vector, isUnsigned: true, isBigEndian: true) % 3));
            Assert.Equal(remainder, vector.Sum(b => b) % 3);
        }
        var seen = new HashSet<int>();
        for (var i = 0; i < 4; i++)
        {
            var password = Encoding.ASCII.GetBytes($"wahl-{i}");
            byte[] salt = [.. Enumerable.Range(0, 8).Select(j => (byte)(i * 8 + j))];
            var log = new List<M4cRef.Round>();
            var expected = M4cRef.Hash(password, salt, [], log);
            var rounds = M4cRounds(password, salt, [], out var result);
            Assert.Equal(log.Select(r => r.Choice), rounds.Select(r => r.Choice));
            Assert.Equal(expected, result);
            seen.UnionWith(rounds.Select(r => r.Choice));
        }
        Assert.Equal(new[] { 0, 1, 2 }, seen.Order());
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.4, algorithm 2.B, steps (e) and (f): at least 64 rounds, then on while the last byte of E is greater than the round
    /// number − 32. Over many inputs PDF-Knead's rounds equal those computed here, never fewer than 64, at most 287 (the last byte is at most 255);
    /// cases ending after 64 rounds and cases with more both occur, and each ends at the first round from 64 on where the rule allows.
    /// </summary>
    [Fact]
    public void Algorithm2B_Rounds_AtLeast64ThenUntilTheLastByteAllows()
    {
        bool exactly64 = false, more = false;
        for (var i = 0; i < 200 && !(exactly64 && more); i++)
        {
            var password = Encoding.ASCII.GetBytes($"runde-{i}");
            byte[] salt = [.. Enumerable.Range(0, 8).Select(j => (byte)(i + j))];
            var log = new List<M4cRef.Round>();
            M4cRef.Hash(password, salt, [], log);
            var rounds = M4cRounds(password, salt, [], out _);
            Assert.Equal(log.Count, rounds.Count);
            Assert.InRange(rounds.Count, 64, 287);
            for (var j = 63; j < rounds.Count; j++)
                Assert.Equal(j == rounds.Count - 1, rounds[j].LastOfE <= j + 1 - 32);
            exactly64 |= rounds.Count == 64;
            more |= rounds.Count > 64;
        }
        Assert.True(exactly64 && more);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.3.4, algorithm 2.B: the output is the first 32 bytes of the last K — also where that K has 48 bytes (SHA-384) or 64
    /// (SHA-512): PDF-Knead's output equals the computation here in both cases.
    /// </summary>
    [Fact]
    public void Algorithm2B_LastKOf48Or64Bytes_OutputItsFirst32()
    {
        var found = new HashSet<int>();
        for (var i = 0; i < 100 && found.Count < 2; i++)
        {
            var password = Encoding.ASCII.GetBytes($"ende-{i}");
            byte[] salt = [.. Enumerable.Range(0, 8).Select(j => (byte)(i * 3 + j))];
            var log = new List<M4cRef.Round>();
            var expected = M4cRef.Hash(password, salt, [], log);
            if (log[^1].KLength == 32) continue;
            found.Add(log[^1].KLength);
            var rounds = M4cRounds(password, salt, [], out var result);
            Assert.Equal(log[^1].Choice, rounds[^1].Choice);
            Assert.Equal(expected, result);
        }
        Assert.Equal(new[] { 48, 64 }, found.Order());
    }

    // ------------------------------------------------------------ 7.6.4.4.1: what the handler supplies

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.1 (Tables 20 and 21): a new protection per version — 1.4 RC4 (V 2, R 3), 1.6 AES-128 (V 4, R 4), 1.7 and 2.0 AES-256
    /// (V 5, R 6) — has /Filter /Standard, /V, /R, /P, /O and /U of 32 or 48 bytes, from revision 6 /OE and /UE of 32 bytes and /Perms of 16, with
    /// crypt filters /CF with StdCF (/CFM, /AuthEvent DocOpen) and /StmF, /StrF StdCF.
    /// </summary>
    [Theory]
    [InlineData("1.4", 2, 3, null)]
    [InlineData("1.6", 4, 4, "AESV2")]
    [InlineData("1.7", 5, 6, "AESV3")]
    [InlineData("2.0", 5, 6, "AESV3")]
    public void Algorithms_NewProtectionPerVersion_DictionaryAsTables20And21(string version, int v, int r, string? method)
    {
        using var original = PdfDocument.Open(PdfKnead.Tests.Pdf.TestPdf.Simple().Build());
        using var output = new MemoryStream();
        PdfWriter.Write(original, PageItem.From(original), output,
            new PdfSaveOptions { Version = version, Protection = new PdfProtection("nutzer", "eigner", PdfPermissions.Print) });
        using var doc = PdfDocument.Open(output.ToArray(), "nutzer");
        var encrypt = doc.EncryptDictionary!;
        Assert.Equal("Standard", encrypt.GetName(PdfName.Filter)?.Value);
        Assert.Equal(v, encrypt.GetInteger(PdfName.Get("V")));
        Assert.Equal(r, encrypt.GetInteger(PdfName.Get("R")));
        Assert.NotNull(encrypt.GetInteger(PdfName.Get("P")));
        Assert.Equal(r == 6 ? 48 : 32, M4cBytes(encrypt, "O").Length);
        Assert.Equal(r == 6 ? 48 : 32, M4cBytes(encrypt, "U").Length);
        Assert.Equal(r == 6, encrypt.ContainsKey(PdfName.Get("OE")) && M4cBytes(encrypt, "OE").Length == 32 && M4cBytes(encrypt, "UE").Length == 32
                             && M4cBytes(encrypt, "Perms").Length == 16);
        var std = encrypt.GetDictionary(PdfName.Get("CF"))?.GetDictionary(PdfName.Get("StdCF"));
        Assert.Equal(method, std?.GetName(PdfName.Get("CFM"))?.Value);
        if (method is null) return;
        Assert.Equal("DocOpen", std!.GetName(PdfName.Get("AuthEvent"))?.Value);
        Assert.Equal("StdCF", encrypt.GetName(PdfName.Get("StmF"))?.Value);
        Assert.Equal("StdCF", encrypt.GetName(PdfName.Get("StrF"))?.Value);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.1: from revision 6 the file key is 256 random bits, not derived from the passwords — two protections with the same
    /// passwords have different file keys (computed here from /UE); granting all permissions changes /P and /Perms but leaves /O, /U, /OE, /UE.
    /// </summary>
    [Fact]
    public void Algorithms8To10_FileKeyRandom_DecoupledFromThePasswords()
    {
        var (first, handler) = StandardSecurityHandler.Create(PdfCipher.Aes256, "nutzer", "eigner", PdfPermissions.Print, M4cId);
        var (second, _) = StandardSecurityHandler.Create(PdfCipher.Aes256, "nutzer", "eigner", PdfPermissions.Print, M4cId);
        Assert.Equal(256, handler.KeyBits);
        Assert.NotEqual(M4cKeyFromUe(first, "nutzer"u8.ToArray()), M4cKeyFromUe(second, "nutzer"u8.ToArray()));
        var (granted, _) = handler.WithAllPermissions(first, M4cId);
        foreach (var key in new[] { "O", "U", "OE", "UE" }) Assert.Equal(M4cBytes(first, key), M4cBytes(granted, key));
        Assert.NotEqual(first.GetInteger(PdfName.Get("P")), granted.GetInteger(PdfName.Get("P")));
        Assert.NotEqual(M4cBytes(first, "Perms"), M4cBytes(granted, "Perms"));
    }

    // ------------------------------------------------------------ 7.6.4.4.2: algorithm 3

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.2, algorithm 3 (known answer): /O of the collection files of revision 2 and 3 is the value computed here from „master“ and
    /// „view“ (key from MD5 of the padded owner password, from revision 3 with 50 rounds; RC4, from revision 3 with 19 more rounds).
    /// </summary>
    [TestPdfsTheory]
    [InlineData(M4cR2, 2)]
    [InlineData(M4cR3, 3)]
    public void Algorithm3_ForeignFiles_OFromOwnerAndUserPassword(string name, int r)
    {
        var (encrypt, _) = M4cEncryptOf(name, "view");
        Assert.Equal(M4cRef.O(M4cRef.Pad("master"u8.ToArray()), M4cRef.Pad("view"u8.ToArray()), r, M4cKeyLength(encrypt, r)), M4cBytes(encrypt, "O")[..32]);
    }

    /// <summary>ISO 32000-2, 7.6.4.4.2, algorithm 3, step (a): without an owner password the user password is taken — it then opens as owner (RC4).</summary>
    [Fact]
    public void Algorithm3_NoOwnerPassword_UserPasswordTaken()
    {
        var (encrypt, _) = StandardSecurityHandler.Create(PdfCipher.Rc4, "nutzer", "", PdfPermissions.Print, M4cId);
        var padded = M4cRef.Pad("nutzer"u8.ToArray());
        Assert.Equal(M4cRef.O(padded, padded, 3, 16), M4cBytes(encrypt, "O"));
        Assert.True(StandardSecurityHandler.Open(encrypt, M4cIds(M4cId), "nutzer").IsOwner);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.2, algorithm 3, steps (b) to (d): the RC4 key is MD5 of the padded owner password — at revision 2 without rounds, from
    /// revision 3 with 50 rounds over all 16 bytes (not the first n) — cut to n bytes (5 at revision 2, after /Length from 3).
    /// </summary>
    [Theory]
    [InlineData(2, 5)]
    [InlineData(3, 5)]
    [InlineData(3, 16)]
    public void Algorithm3_OwnerKey_Md5RoundsOverAllSixteenBytes(int r, int n)
    {
        var padded = M4cRef.Pad("eigner"u8.ToArray());
        Assert.Equal(M4cRef.OwnerKey(padded, r, n), StandardSecurityHandler.OwnerKey(padded, r, n));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.2, algorithm 3, steps (e) to (h): /O of a new RC4 protection (revision 3) is the padded user password encrypted with RC4
    /// and then 19 more times with the key XOR 1 to 19, as computed here; 18 or 20 rounds would give another value.
    /// </summary>
    [Fact]
    public void Algorithm3_NewRevision3_ONineteenMoreRounds()
    {
        var (encrypt, _) = StandardSecurityHandler.Create(PdfCipher.Rc4, "nutzer", "eigner", PdfPermissions.Print, M4cId);
        var paddedOwner = M4cRef.Pad("eigner"u8.ToArray());
        var paddedUser = M4cRef.Pad("nutzer"u8.ToArray());
        var o = M4cBytes(encrypt, "O");
        Assert.Equal(M4cRef.O(paddedOwner, paddedUser, 3, 16), o);
        Assert.NotEqual(M4cRef.O(paddedOwner, paddedUser, 3, 16, rounds: 18), o);
        Assert.NotEqual(M4cRef.O(paddedOwner, paddedUser, 3, 16, rounds: 20), o);
    }

    // ------------------------------------------------------------ 7.6.4.4.3 to 7.6.4.4.6: algorithms 4 to 7

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.3: algorithm 4 (revision 2) is deprecated in PDF 2.0 — the collection file of revision 2, its encryption kept and asked for
    /// as 2.0, is written as 1.7 with revision 2 and opens as before.
    /// </summary>
    [TestPdfsFact]
    public void Algorithm4_KeptRevision2AskedFor20_WrittenAs17()
    {
        using var doc = PdfDocument.Open(M4cFile("enc-R2,V1.pdf"), "");
        Assert.Equal(2, doc.Security!.Revision);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "2.0", KeepEncryption = true });
        using var saved = PdfDocument.Open(output.ToArray(), "");
        Assert.Equal("1.7", saved.Version);
        Assert.Equal(2, saved.Security!.Revision);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.3, algorithm 4: at revision 2 /U is the padding encrypted with RC4 and the file key — the collection file opens with „view“ as
    /// user and its /U is that computed here; granting all permissions writes an /U that is again RC4 of the padding under the new key (new /P, new
    /// /ID[0]).
    /// </summary>
    [TestPdfsFact]
    public void Algorithm4_ForeignRevision2_UIsTheEncryptedPadding()
    {
        using var doc = PdfDocument.Open(M4cFile(M4cR2), "view");
        Assert.False(doc.Security!.IsOwner);
        var encrypt = doc.EncryptDictionary!;
        var id0 = M4cId0((PdfArray)doc.Trailer.Get(PdfName.ID));
        var o = M4cBytes(encrypt, "O");
        var key = M4cRef.FileKey(M4cRef.Pad("view"u8.ToArray()), o, M4cRef.PBytes(encrypt.GetInteger(PdfName.Get("P"))!.Value), id0, 2, 5);
        Assert.Equal(M4cRef.U2(key), M4cBytes(encrypt, "U")[..32]);

        var (granted, _) = doc.Security.WithAllPermissions(encrypt, M4cId);
        var newKey = M4cRef.FileKey(M4cRef.Pad("view"u8.ToArray()), o, M4cRef.PBytes(granted.GetInteger(PdfName.Get("P"))!.Value), M4cId, 2, 5);
        Assert.Equal(M4cRef.U2(newKey), M4cBytes(granted, "U"));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.4, algorithm 5: from revision 3 /U[0..16] is MD5 of padding and /ID[0], encrypted with RC4 and 19 more times with the
    /// key XOR 1 to 19 — the collection file's /U is that computed here (18 rounds would differ); a new RC4 protection writes 32 bytes whose first 16 are
    /// computed so.
    /// </summary>
    [TestPdfsFact]
    public void Algorithm5_Revision3_UFromPaddingAndFirstId()
    {
        var (encrypt, id) = M4cEncryptOf(M4cR3, "view");
        var key = M4cRef.FileKey(M4cRef.Pad("view"u8.ToArray()), M4cBytes(encrypt, "O"), M4cRef.PBytes(encrypt.GetInteger(PdfName.Get("P"))!.Value),
            M4cId0(id), 3, M4cKeyLength(encrypt, 3));
        var u = M4cBytes(encrypt, "U");
        Assert.Equal(M4cRef.U3(key, M4cId0(id)), u[..16]);
        Assert.NotEqual(M4cRef.U3(key, M4cId0(id), rounds: 18), u[..16]);

        var (created, _) = StandardSecurityHandler.Create(PdfCipher.Rc4, "nutzer", "eigner", PdfPermissions.Print, M4cId);
        var createdKey = M4cRef.FileKey(M4cRef.Pad("nutzer"u8.ToArray()), M4cBytes(created, "O"),
            M4cRef.PBytes(created.GetInteger(PdfName.Get("P"))!.Value), M4cId, 3, 16);
        Assert.Equal(32, M4cBytes(created, "U").Length);
        Assert.Equal(M4cRef.U3(createdKey, M4cId), M4cBytes(created, "U")[..16]);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.5, algorithm 6: the user password is right if the computed value equals /U — from revision 3 only its first 16 bytes. In
    /// the collection file of revision 3 a changed /U byte 20 still opens with „view“, a changed byte 15 does not; at revision 2 a changed byte 31 does not.
    /// </summary>
    [TestPdfsFact]
    public void Algorithm6_Revision3First16Bytes_Revision2All32()
    {
        var (r3, id3) = M4cEncryptOf(M4cR3, "view");
        var u3 = M4cBytes(r3, "U");
        Assert.False(StandardSecurityHandler.Open(M4cCopy(r3, ("U", new PdfString(M4cFlip(u3, 20)))), id3, "view").IsOwner);
        Assert.True(Assert.Throws<PdfPasswordException>(() => StandardSecurityHandler.Open(M4cCopy(r3, ("U", new PdfString(M4cFlip(u3, 15)))), id3, "view"))
            .WrongPassword);
        var (r2, id2) = M4cEncryptOf(M4cR2, "view");
        Assert.True(Assert.Throws<PdfPasswordException>(() =>
            StandardSecurityHandler.Open(M4cCopy(r2, ("U", new PdfString(M4cFlip(M4cBytes(r2, "U"), 31)))), id2, "view")).WrongPassword);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.6, algorithm 7: the owner password decrypts /O to the padded user password — once at revision 2, from revision 3 twenty
    /// times with the key XOR 19 down to 0. In the collection files „master“ opens as owner, „view“ not; /O decrypted with „master“ gives Pad(„view“), in
    /// PDF-Knead and here; without the last step (counter 0) it would not. (RC4 steps commute, so the order of the counters cannot be told apart.)
    /// </summary>
    [TestPdfsTheory]
    [InlineData(M4cR2, 2)]
    [InlineData(M4cR3, 3)]
    public void Algorithm7_ForeignFiles_OwnerPasswordRecoversTheUserPassword(string name, int r)
    {
        var (encrypt, id) = M4cEncryptOf(name, "view");
        Assert.True(StandardSecurityHandler.Open(encrypt, id, "master").IsOwner);
        Assert.False(StandardSecurityHandler.Open(encrypt, id, "view").IsOwner);
        var o = M4cBytes(encrypt, "O");
        var n = M4cKeyLength(encrypt, r);
        var paddedOwner = M4cRef.Pad("master"u8.ToArray());
        var paddedUser = M4cRef.Pad("view"u8.ToArray());
        Assert.Equal(paddedUser, M4cRef.UserFromOwner(paddedOwner, o, r, n));
        Assert.Equal(paddedUser, StandardSecurityHandler.UserPasswordFromOwner(paddedOwner, o, r, n));
        if (r >= 3) Assert.NotEqual(paddedUser, M4cRef.UserFromOwner(paddedOwner, o, r, n, lowest: 1));
    }

    // ------------------------------------------------------------ 7.6.4.4.7 to 7.6.4.4.12: algorithms 8 to 13

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.7, algorithm 8: a new revision-6 protection has /U of 48 bytes, /U[0..32] = 2.B (computed here) over the SASLprep form of
    /// the password (a no-break space becomes a space) and the validation salt /U[32..40]; /UE decrypted with 2.B over password and key salt
    /// /U[40..48] is the file key, the same as from /OE. Two protections have different salts.
    /// </summary>
    [Fact]
    public void Algorithm8_NewRevision6_UAndUEAsComputedHere()
    {
        var (encrypt, _) = StandardSecurityHandler.Create(PdfCipher.Aes256, "nutzer eins", "eigner", PdfPermissions.Print, M4cId);
        var prepared = "nutzer eins"u8.ToArray();
        var u = M4cBytes(encrypt, "U");
        Assert.Equal(48, u.Length);
        Assert.Equal(M4cRef.Hash(prepared, u[32..40], []), u[..32]);
        var fromUe = M4cKeyFromUe(encrypt, prepared);
        var o = M4cBytes(encrypt, "O");
        Assert.Equal(M4cRef.CbcDecrypt(M4cRef.Hash("eigner"u8.ToArray(), o[40..48], u), M4cBytes(encrypt, "OE")), fromUe);
        var (second, _) = StandardSecurityHandler.Create(PdfCipher.Aes256, "nutzer eins", "eigner", PdfPermissions.Print, M4cId);
        Assert.NotEqual(u[32..48], M4cBytes(second, "U")[32..48]);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.8, algorithm 9: /O[0..32] = 2.B (computed here) over owner password, owner validation salt /O[32..40] and the 48 bytes of
    /// /U; /OE decrypted with 2.B over owner password, owner key salt /O[40..48] and /U is the file key (that of /UE). Without /U neither would match.
    /// </summary>
    [Fact]
    public void Algorithm9_NewRevision6_OAndOEOverU()
    {
        var (encrypt, _) = StandardSecurityHandler.Create(PdfCipher.Aes256, "nutzer", "eigner", PdfPermissions.Print, M4cId);
        var owner = "eigner"u8.ToArray();
        var o = M4cBytes(encrypt, "O");
        var u = M4cBytes(encrypt, "U");
        Assert.Equal(48, o.Length);
        Assert.Equal(M4cRef.Hash(owner, o[32..40], u), o[..32]);
        Assert.NotEqual(M4cRef.Hash(owner, o[32..40], []), o[..32]);
        var fromUe = M4cKeyFromUe(encrypt, "nutzer"u8.ToArray());
        Assert.Equal(fromUe, M4cRef.CbcDecrypt(M4cRef.Hash(owner, o[40..48], u), M4cBytes(encrypt, "OE")));
        Assert.NotEqual(fromUe, M4cRef.CbcDecrypt(M4cRef.Hash(owner, o[40..48], []), M4cBytes(encrypt, "OE")));
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.9, algorithm 10, and 7.6.4.4.12 (byte 8): /Perms of a new revision-6 protection, decrypted with the file key (computed
    /// here from /UE) with AES-256-ECB, holds /P low-order byte first in bytes 0 to 3, FF in 4 to 7, „T“ or „F“ after /EncryptMetadata in byte 8,
    /// „adb“ in 9 to 11; after granting all permissions the same with the new /P, and bytes 12 to 15 are random (two grants differ).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Algorithm10_NewPerms_BlockAsTheSteps(bool encryptMetadata)
    {
        var (encrypt, handler) = StandardSecurityHandler.Create(PdfCipher.Aes256, "nutzer", "eigner", PdfPermissions.Print, M4cId, encryptMetadata);
        var key = M4cKeyFromUe(encrypt, "nutzer"u8.ToArray());
        void Check(PdfDictionary d)
        {
            var block = M4cRef.Ecb(key, M4cBytes(d, "Perms"), encrypt: false);
            Assert.Equal(M4cRef.PBytes(d.GetInteger(PdfName.Get("P"))!.Value), block[..4]);
            Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, block[4..8]);
            Assert.Equal((byte)(encryptMetadata ? 'T' : 'F'), block[8]);
            Assert.Equal("adb"u8.ToArray(), block[9..12]);
        }
        Check(encrypt);
        var (grantedOnce, _) = handler.WithAllPermissions(encrypt, M4cId);
        var (grantedTwice, _) = handler.WithAllPermissions(encrypt, M4cId);
        Check(grantedOnce);
        Check(grantedTwice);
        Assert.NotEqual(M4cRef.Ecb(key, M4cBytes(grantedOnce, "Perms"), false)[12..], M4cRef.Ecb(key, M4cBytes(grantedTwice, "Perms"), false)[12..]);
    }

    /// <summary>
    /// ISO 32000-2, 7.6.4.4.10, algorithm 11: the user password is right if 2.B over it and the user validation salt equals /U[0..32] — the collection file
    /// of revision 6 opens with „view“ as user (not owner); with /U byte 0 (hash) or byte 33 (validation salt) changed „view“ is wrong.
    /// </summary>
    [TestPdfsFact]
    public void Algorithm11_ForeignRevision6_UserPasswordOverU()
    {
        var (encrypt, id) = M4cEncryptOf(M4cR6, "view");
        Assert.False(StandardSecurityHandler.Open(encrypt, id, "view").IsOwner);
        var u = M4cBytes(encrypt, "U");
        foreach (var index in new[] { 0, 33 })
            Assert.True(Assert.Throws<PdfPasswordException>(() =>
                StandardSecurityHandler.Open(M4cCopy(encrypt, ("U", new PdfString(M4cFlip(u, index)))), id, "view")).WrongPassword);
    }
}
