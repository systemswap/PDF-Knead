using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;
using Builder = PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Konformitätsabgleich M4, sentence by sentence: the dictionary of the standard security handler and its permission bits (ISO 32000-2,
/// 7.6.4.2, Tables 21 and 22), crypt filters (7.6.6, Table 25), public-key handlers (7.6.5) and the unencrypted wrapper (7.6.7). Check values
/// are computed here with MD5, SHA-2 and AES of the runtime and an RC4 of the test's own (held against a published test vector), not with
/// PDF-Knead's code; foreign files with known passwords come from a public test collection.
/// </summary>
public sealed partial class EncryptionNormTests
{
    /// <summary>The first part of /ID the test files carry (the same as <see cref="Builder.Build"/> writes).</summary>
    private static readonly byte[] M4bId = [.. Enumerable.Range(1, 16).Select(i => (byte)(i * 7))];

    /// <summary>The 32 padding bytes of the standard security handler (Algorithm 2, step a), as data.</summary>
    private static readonly byte[] M4bPasswordPadding = Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");

    // ---- independent building blocks (MD5, SHA-2 and AES of the runtime; RC4 written here)

    /// <summary>RC4 written for the tests, independent of PDF-Knead's own.</summary>
    internal static byte[] Arcfour(byte[] key, byte[] data)
    {
        var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
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

    /// <summary>A password truncated or padded to 32 bytes (Algorithm 2, step a).</summary>
    private static byte[] Padded(string password)
    {
        var raw = Encoding.ASCII.GetBytes(password);
        var n = Math.Min(32, raw.Length);
        return [.. raw.Take(n), .. M4bPasswordPadding.Take(32 - n)];
    }

    private static byte[] XorEach(byte[] key, int value) => [.. key.Select(b => (byte)(b ^ value))];

    /// <summary>Algorithm 2: the file key from the padded user password, /O, /P (four bytes, low-order first) and the first part of /ID.</summary>
    private static byte[] FileKeyR2To4(byte[] paddedUser, byte[] o, int p, byte[] id, int revision, int bytes, bool encryptMetadata = true)
    {
        var pBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(pBytes, p);
        byte[] input = [.. paddedUser, .. o.Take(32), .. pBytes, .. id];
        if (revision >= 4 && !encryptMetadata) input = [.. input, 0xFF, 0xFF, 0xFF, 0xFF];
        var hash = MD5.HashData(input);
        if (revision >= 3)
            for (var i = 0; i < 50; i++) hash = MD5.HashData(hash.AsSpan(0, bytes));
        return hash[..bytes];
    }

    /// <summary>The RC4 key from the owner password (Algorithm 3, steps a to d).</summary>
    private static byte[] OwnerRc4Key(string owner, int revision, int bytes)
    {
        var hash = MD5.HashData(Padded(owner));
        if (revision >= 3)
            for (var i = 0; i < 50; i++) hash = MD5.HashData(hash);
        return hash[..bytes];
    }

    /// <summary>Algorithm 3: /O from both passwords.</summary>
    private static byte[] OwnerEntryR2To4(string owner, string user, int revision, int bytes)
    {
        var key = OwnerRc4Key(owner, revision, bytes);
        var value = Arcfour(key, Padded(user));
        if (revision >= 3)
            for (var i = 1; i <= 19; i++) value = Arcfour(XorEach(key, i), value);
        return value;
    }

    /// <summary>Algorithm 4 (revision 2: all 32 bytes) and 5 (from revision 3: the first 16 bytes of /U).</summary>
    private static byte[] UserEntryR2To4(byte[] fileKey, byte[] id, int revision)
    {
        if (revision == 2) return Arcfour(fileKey, M4bPasswordPadding);
        var value = Arcfour(fileKey, MD5.HashData([.. M4bPasswordPadding, .. id]));
        for (var i = 1; i <= 19; i++) value = Arcfour(XorEach(fileKey, i), value);
        return value;
    }

    /// <summary>Algorithm 7: the padded user password recovered from /O with the owner password.</summary>
    private static byte[] UserFromOwnerR2To4(string owner, byte[] o, int revision, int bytes)
    {
        var key = OwnerRc4Key(owner, revision, bytes);
        var value = o[..32];
        if (revision == 2) return Arcfour(key, value);
        for (var i = 19; i >= 0; i--) value = Arcfour(XorEach(key, i), value);
        return value;
    }

    /// <summary>Algorithm 2.B (revision 6), written from the description: the first 16 bytes of E as a big-endian number modulo 3 choose the
    /// hash; at least 64 rounds, then until the last byte of E is at most the count of rounds minus 32.</summary>
    private static byte[] Hash2B(byte[] password, byte[] salt, byte[] udata)
    {
        var k = SHA256.HashData([.. password, .. salt, .. udata]);
        for (var rounds = 1; ; rounds++)
        {
            byte[] block = [.. password, .. k, .. udata];
            var k1 = Enumerable.Repeat(block, 64).SelectMany(b => b).ToArray();
            byte[] e;
            using (var aes = Aes.Create())
            {
                aes.Key = k[..16];
                e = aes.EncryptCbc(k1, k[16..32], PaddingMode.None);
            }
            k = (int)(new BigInteger(e.AsSpan(0, 16), isUnsigned: true, isBigEndian: true) % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
            if (rounds >= 64 && e[^1] <= rounds - 32) break;
        }
        return k[..32];
    }

    /// <summary>
    /// A file of revision 2 (V 1, 40-bit RC4) built independently: /O by Algorithm 3, the key by Algorithm 2, /U by Algorithm 4; the user
    /// password is empty, the owner password „Besitzer“. One empty page, no strings or streams to encrypt.
    /// </summary>
    internal static byte[] Revision2File(int permissions)
    {
        var o = OwnerEntryR2To4("Besitzer", "", 2, 5);
        var u = UserEntryR2To4(FileKeyR2To4(Padded(""), o, permissions, M4bId, 2, 5), M4bId, 2);
        var id = "<" + Convert.ToHexString(M4bId) + ">";
        var text = Encoding.Latin1.GetString(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /Filter /Standard /V 1 /R 2 /Length 40 /P {permissions} /O <{Convert.ToHexString(o)}> /U <{Convert.ToHexString(u)}> >>"));
        return Encoding.Latin1.GetBytes(text.Replace("/Root 1 0 R >>", $"/Root 1 0 R /Encrypt 4 0 R /ID [{id} {id}] >>"));
    }

    /// <summary>
    /// A one-page file encrypted with a handler of <see cref="StandardSecurityHandler.Create(PdfCipher, string, string, PdfPermissions, byte[], bool)"/>
    /// (owner password „Besitzer“, all permissions); the catalogue names the streams in /Extra (objects 4 on), each stored encrypted with the
    /// default method for streams or plain, as given. <paramref name="patchEncrypt"/> changes the /Encrypt dictionary before it is written.
    /// </summary>
    private static byte[] EncryptedFileWith(PdfCipher cipher, string user, Action<PdfDictionary>? patchEncrypt,
        params (string Dictionary, byte[] Data, bool Encrypted)[] streams)
    {
        var (encrypt, handler) = StandardSecurityHandler.Create(cipher, user, "Besitzer", PdfPermissions.All, M4bId);
        patchEncrypt?.Invoke(encrypt);
        var output = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s) => output.Write(Encoding.Latin1.GetBytes(s));
        Write("%PDF-1.7\n");
        var extra = string.Join(" ", streams.Select((_, i) => $"{i + 4} 0 R"));
        foreach (var body in new[] { $"<< /Type /Catalog /Pages 2 0 R /Extra [{extra}] >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                     "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>" })
        {
            offsets.Add(output.Position);
            Write($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        foreach (var (dictionary, data, encrypted) in streams)
        {
            var number = offsets.Count + 1;
            var stored = encrypted ? handler.EncryptStream(new PdfObjectId(number, 0), data) : data;
            offsets.Add(output.Position);
            Write($"{number} 0 obj\n<< {dictionary} /Length {stored.Length} >>\nstream\n");
            output.Write(stored);
            Write("\nendstream\nendobj\n");
        }
        offsets.Add(output.Position);
        Write($"{offsets.Count} 0 obj\n");
        PdfSerializer.Write(encrypt, output);
        Write("\nendobj\n");
        var xref = output.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n \n");
        var id = "<" + Convert.ToHexString(M4bId) + ">";
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Encrypt {offsets.Count} 0 R /ID [{id} {id}] >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>The data of the stream number <paramref name="index"/> of /Extra, decoded as the program reads it.</summary>
    private static byte[] ExtraData(PdfDocument doc, int index) =>
        doc.DecodeStream(Assert.IsType<PdfStream>(doc.Catalog!.GetArray(PdfName.Get("Extra"))!.Get(index))).Data;

    /// <summary>The /Encrypt dictionary of new protection with the given method (owner „Besitzer“, user „geheim“).</summary>
    private static PdfDictionary NewEncrypt(PdfCipher cipher, PdfPermissions permissions = PdfPermissions.Print) =>
        StandardSecurityHandler.Create(cipher, "geheim", "Besitzer", permissions, M4bId).Encrypt;

    /// <summary>A file of <see cref="Builder.Simple"/> with a document metadata stream (/Metadata of the catalogue) holding <paramref name="xmp"/>.</summary>
    private static Builder WithDocumentXmp(byte[] xmp)
    {
        var pdf = Builder.Simple(1);
        var metadata = pdf.Add(new PdfStream(Builder.Dict(("Type", Builder.N("Metadata")), ("Subtype", Builder.N("XML"))), xmp));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Metadata, Builder.Ref(metadata));
        return pdf;
    }

    private static readonly byte[] SampleXmp = Encoding.ASCII.GetBytes(
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta><?xpacket end=\"w\"?>");

    // ---- 7.6.4.2, Table 21: the entries of the standard security handler

    /// <summary>
    /// 7.6.4.2, Table 21 (/R required, it says by which revision the dictionary is read): a file whose /R was removed is refused with a
    /// message naming the revision, not read after some guessed one.
    /// </summary>
    [Fact]
    public void Revision_Missing_IsRefusedNotGuessed()
    {
        var file = Builder.Simple(1).Build(cipher: PdfCipher.Aes128, patchEncrypt: d => d.Remove(PdfName.Get("R")));
        var error = Assert.ThrowsAny<PdfException>(() => PdfDocument.Open(file).Dispose());
        Assert.IsNotType<PdfPasswordException>(error);
        Assert.Contains("Revision 0", error.Message);
    }

    /// <summary>
    /// 7.6.4.2, Table 21 (/R 3 with V 2, /R 4 with V 4, /R 6 with V 5; revision 5 shall not be used): new protection writes V and R as the
    /// norm pairs them for each method, and never revision 5.
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Rc4, 2, 3)]
    [InlineData(PdfCipher.Aes128, 4, 4)]
    [InlineData(PdfCipher.Aes256, 5, 6)]
    public void NewProtection_VersionAndRevision_PerMethod(PdfCipher cipher, int v, int r)
    {
        var encrypt = NewEncrypt(cipher);
        Assert.Equal(v, encrypt.GetInteger(PdfName.Get("V")));
        Assert.Equal(r, encrypt.GetInteger(PdfName.Get("R")));
        Assert.NotEqual(5, encrypt.GetInteger(PdfName.Get("R")));
    }

    /// <summary>
    /// 7.6.4.2, Table 21 (/R 2: V 1, 40-bit key): a file of revision 2 built independently (Algorithms 2, 3 and 4 in the test) opens with the
    /// empty user password as user, with its permissions, and with the owner password as owner; the key has 40 bits.
    /// </summary>
    [Fact]
    public void Revision2_IndependentlyBuilt_OpensWithBothPasswords()
    {
        const int p = -60;   // bits 1 to 2 cleared, bit 3 (print) set, bits 4 to 6 cleared, the rest set
        using (var user = PdfDocument.Open(Revision2File(p)))
        {
            Assert.Equal(2, user.Security!.Revision);
            Assert.Equal(40, user.Security.KeyBits);
            Assert.False(user.Security.IsOwner);
            Assert.Equal(PdfPermissions.All & ~(PdfPermissions.Modify | PdfPermissions.Copy | PdfPermissions.Annotate), user.Security.Permissions);
        }
        using var owner = PdfDocument.Open(Revision2File(p), "Besitzer");
        Assert.True(owner.Security!.IsOwner);
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(Revision2File(p), "falsch").Dispose());
    }

    /// <summary>
    /// 7.6.4.2, Table 21 (revisions 2 and 3) with Algorithms 2 to 5 and 7: the test files of a public collection of revision 2 (V 1, 40 bit) and 3 (V 2, 128 bit)
    /// with the user password „view“ and the owner password „master“. The test checks /U itself (the RC4 of the test against the published
    /// vector first), then the program opens them with either password and reads the text of the unencrypted base file.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("enc-R2,V1,U=view,O=master.pdf", 2, 5, "view", false)]
    [InlineData("enc-R2,V1,U=view,O=master.pdf", 2, 5, "master", true)]
    [InlineData("enc-R3,V2,U=view,O=master.pdf", 3, 16, "view", false)]
    [InlineData("enc-R3,V2,U=view,O=master.pdf", 3, 16, "master", true)]
    public void Revision2And3_ForeignFiles_OpenWithBothPasswords(string name, int revision, int keyBytes, string password, bool owner)
    {
        Assert.Equal("BBF316E8D940AF0AD3", Convert.ToHexString(Arcfour("Key"u8.ToArray(), "Plaintext"u8.ToArray())));
        var path = TestPdf("testsammlungen/qpdf/qpdf/qtest/qpdf/" + name);
        using var doc = PdfDocument.Open(File.ReadAllBytes(path), password);
        var encrypt = doc.EncryptDictionary!;
        var o = encrypt.GetString(PdfName.Get("O"))!.Bytes;
        var u = encrypt.GetString(PdfName.Get("U"))!.Bytes;
        var p = (int)encrypt.GetInteger(PdfName.Get("P"))!.Value;
        var id = ((PdfString)((PdfArray)doc.Trailer.Get(PdfName.ID)!).Get(0)).Bytes;
        var paddedUser = owner ? UserFromOwnerR2To4(password, o, revision, keyBytes) : Padded(password);
        var key = FileKeyR2To4(paddedUser, o, p, id, revision, keyBytes);
        var compared = revision == 2 ? 32 : 16;
        Assert.Equal(u[..compared], UserEntryR2To4(key, id, revision)[..compared]);

        Assert.Equal(revision, doc.Security!.Revision);
        Assert.Equal(keyBytes * 8, doc.Security.KeyBits);
        Assert.Equal(owner, doc.Security.IsOwner);
        using var plain = PdfDocument.Open(File.ReadAllBytes(TestPdf("testsammlungen/qpdf/qpdf/qtest/qpdf/enc-base.pdf")));
        Assert.Equal(FirstPageText(plain), FirstPageText(doc));
        Assert.NotEmpty(FirstPageText(doc).Trim());
    }

    /// <summary>
    /// 7.6.4.2, Table 21 (/O and /U: 32 bytes up to revision 4, 48 bytes in revision 6; /OE and /UE: 32 bytes, revision 6 only): the lengths
    /// new protection writes; up to revision 4 /O is also computed by Algorithm 3 in the test.
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Rc4, 32, 0)]
    [InlineData(PdfCipher.Aes128, 32, 0)]
    [InlineData(PdfCipher.Aes256, 48, 32)]
    public void NewProtection_PasswordEntries_LengthsPerRevision(PdfCipher cipher, int ouLength, int eLength)
    {
        var encrypt = NewEncrypt(cipher);
        var o = encrypt.GetString(PdfName.Get("O"))!.Bytes;
        Assert.Equal(ouLength, o.Length);
        Assert.Equal(ouLength, encrypt.GetString(PdfName.Get("U"))!.Bytes.Length);
        foreach (var key in new[] { "OE", "UE" })
            if (eLength == 0) Assert.False(encrypt.ContainsKey(PdfName.Get(key)), key);
            else Assert.Equal(eLength, encrypt.GetString(PdfName.Get(key))!.Bytes.Length);
        if (cipher != PdfCipher.Aes256)
            Assert.Equal(OwnerEntryR2To4("Besitzer", "geheim", (int)encrypt.GetInteger(PdfName.Get("R"))!.Value, 16), o);
    }

    /// <summary>
    /// 7.6.4.2, Table 21 (/O, /U, /OE, /UE required, of a fixed length): an entry one byte too short, or /OE or /UE missing, is reported as
    /// incomplete encryption data — not as a wrong password, and not read past its end.
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Aes128, "O", 31)]
    [InlineData(PdfCipher.Aes128, "U", 31)]
    [InlineData(PdfCipher.Aes256, "O", 47)]
    [InlineData(PdfCipher.Aes256, "U", 47)]
    [InlineData(PdfCipher.Aes256, "OE", 31)]
    [InlineData(PdfCipher.Aes256, "UE", 31)]
    [InlineData(PdfCipher.Aes256, "OE", -1)]
    [InlineData(PdfCipher.Aes256, "UE", -1)]
    public void PasswordEntry_TooShortOrMissing_ReportedIncomplete(PdfCipher cipher, string entry, int length)
    {
        var file = Builder.Simple(1).Build(cipher: cipher, user: "geheim", patchEncrypt: d =>
        {
            var name = PdfName.Get(entry);
            if (length < 0) d.Remove(name);
            else d.Set(name, new PdfString(d.GetString(name)!.Bytes[..length]));
        });
        var error = Assert.ThrowsAny<PdfException>(() => PdfDocument.Open(file, "geheim").Dispose());
        Assert.IsNotType<PdfPasswordException>(error);
        Assert.Contains("unvollständig", error.Message);
    }

    /// <summary>
    /// 7.6.4.2, Table 21 (/OE from the owner password, /UE from the user password, each used in computing the file key): with the other
    /// entry replaced by random bytes, the owner password still decrypts the file key from /OE and the user password from /UE — the page
    /// content reads.
    /// </summary>
    [Theory]
    [InlineData("Besitzer", "UE", true)]
    [InlineData("geheim", "OE", false)]
    public void Aes256_EachPasswordTakesTheFileKeyFromItsOwnEntry(string password, string spoiled, bool owner)
    {
        var file = Builder.Simple(1).Build(cipher: PdfCipher.Aes256, user: "geheim", owner: "Besitzer",
            patchEncrypt: d => d.Set(PdfName.Get(spoiled), new PdfString(RandomNumberGenerator.GetBytes(32))));
        using var doc = PdfDocument.Open(file, password);
        Assert.Equal(owner, doc.Security!.IsOwner);
        Assert.Contains("Seite 1", FirstPageText(doc));
    }

    /// <summary>
    /// 7.6.4.2, Table 21 (/EncryptMetadata has a meaning only with V 4 and 5): in an RC4 file of V 2 whose /Encrypt says /EncryptMetadata
    /// false, the document's metadata stream is encrypted like every stream and must be decrypted. PDF-Knead honours the entry also below
    /// V 4 and reads the encrypted bytes as they stand.
    /// </summary>
    [Fact]
    public void EncryptMetadata_BelowV4_HasNoMeaning()
    {
        var file = WithDocumentXmp(SampleXmp).Build(cipher: PdfCipher.Rc4, patchEncrypt: d => d.Set(PdfName.Get("EncryptMetadata"), PdfBoolean.False));
        using var doc = PdfDocument.Open(file);
        Assert.Equal(2, doc.EncryptDictionary!.GetInteger(PdfName.Get("V")));
        Assert.Equal(SampleXmp, doc.DecodeStream(Assert.IsType<PdfStream>(doc.Catalog!.Get(PdfName.Metadata))).Data);
    }

    /// <summary>
    /// 7.6.4.2, Table 21 (/EncryptMetadata, default true) and 7.6.6, Table 27 (the same default): an AES-128 file without the entry counts as
    /// encrypting its metadata — the document's metadata stream is decrypted and reads as written.
    /// </summary>
    [Fact]
    public void EncryptMetadata_Missing_CountsAsTrue()
    {
        var file = WithDocumentXmp(SampleXmp).Build(cipher: PdfCipher.Aes128);
        using var doc = PdfDocument.Open(file);
        Assert.False(doc.EncryptDictionary!.ContainsKey(PdfName.Get("EncryptMetadata")));
        Assert.True(doc.Security!.EncryptMetadata);
        var stream = Assert.IsType<PdfStream>(doc.Catalog!.Get(PdfName.Metadata));
        Assert.False(doc.StoredStreamData(stream).SequenceEqual(SampleXmp));
        Assert.Equal(SampleXmp, doc.DecodeStream(stream).Data);
    }

    /// <summary>
    /// 7.6.4.2, Table 21 (/P is read as an unsigned 32-bit number): an RC4 file whose /P is written as the unsigned number of the same bits
    /// opens with the same key (/P enters Algorithm 2) and the same permissions as with the signed notation.
    /// </summary>
    [Fact]
    public void Permissions_UnsignedNotation_SameBitsAsSigned()
    {
        var pdf = Builder.Simple(1);
        var signed = pdf.Build(cipher: PdfCipher.Rc4, permissions: PdfPermissions.Print);
        long? p = null;
        var unsigned = pdf.Build(cipher: PdfCipher.Rc4, permissions: PdfPermissions.Print, patchEncrypt: d =>
        {
            p = d.GetInteger(PdfName.Get("P"));
            d.Set(PdfName.Get("P"), PdfInteger.Of((uint)(int)p!.Value));
        });
        Assert.True(p < 0);
        using var a = PdfDocument.Open(signed);
        using var b = PdfDocument.Open(unsigned);
        Assert.Equal((long)(uint)(int)p!.Value, b.EncryptDictionary!.GetInteger(PdfName.Get("P")));
        Assert.Equal(a.Security!.Permissions, b.Security!.Permissions);
        Assert.Equal(PdfPermissions.Print | PdfPermissions.Accessibility, b.Security.Permissions);
        Assert.Contains("Seite 1", FirstPageText(b));
    }

    // ---- 7.6.4.2, Table 22: the permission bits

    /// <summary>
    /// 7.6.4.2, Table 22 (bits numbered from 1, the low-order bit; a set bit grants): for each bit a reader honours, a file granting only
    /// that permission writes exactly that bit (besides the reserved ones and bit 10) and opens for the user with exactly that permission.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(12)]
    public void PermissionBit_Alone_GivesExactlyItsPermission(int bit)
    {
        var flag = 1 << (bit - 1);
        var file = Builder.Simple(1).Build(cipher: PdfCipher.Aes128, permissions: (PdfPermissions)flag);
        using var doc = PdfDocument.Open(file);
        Assert.False(doc.Security!.IsOwner);
        Assert.Equal(unchecked((int)0xFFFFF0C0) | flag | (1 << 9), (int)doc.EncryptDictionary!.GetInteger(PdfName.Get("P"))!.Value);
        Assert.Equal((PdfPermissions)flag | PdfPermissions.Accessibility, doc.Security.Permissions);
    }

    /// <summary>
    /// 7.6.4.2, Table 21 (readers ignore all bits but 3, 4, 5, 6, 9, 10, 11 and 12): with exactly those set the user has every permission;
    /// with every other bit set and bit 5 cleared, exactly copying is missing — the other bits add nothing. Revision 5 file built in the test.
    /// </summary>
    [Theory]
    [InlineData(0xF3C, PdfPermissions.All)]
    [InlineData(~0x10, PdfPermissions.All & ~PdfPermissions.Copy)]
    public void PermissionBits_OtherThanTheEight_Ignored(int p, PdfPermissions expected)
    {
        using var doc = PdfDocument.Open(Revision5File(p), "nutzer");
        Assert.Equal(PermsState.Valid, doc.Security!.Perms);
        Assert.Equal(expected, doc.Security.Permissions);
    }

    /// <summary>The /P new protection writes for each permission set, and /P after „Einschränkungen entfernen“ (WithAllPermissions).</summary>
    private static IEnumerable<int> WrittenP(PdfCipher cipher)
    {
        foreach (var permissions in new[] { PdfPermissions.None, PdfPermissions.Print, PdfPermissions.All })
            yield return (int)NewEncrypt(cipher, permissions).GetInteger(PdfName.Get("P"))!.Value;
        using var doc = PdfDocument.Open(Builder.Simple(1).Build(cipher: cipher, permissions: PdfPermissions.Print));
        yield return (int)doc.Security!.WithAllPermissions(doc.EncryptDictionary!, M4bId).Encrypt.GetInteger(PdfName.Get("P"))!.Value;
    }

    /// <summary>7.6.4.2, Table 22 (bits 1 and 2 reserved, 0): new protection and lifted restrictions write them cleared.</summary>
    [Theory]
    [InlineData(PdfCipher.Rc4)]
    [InlineData(PdfCipher.Aes128)]
    [InlineData(PdfCipher.Aes256)]
    public void PermissionBits1And2_WrittenCleared(PdfCipher cipher)
    {
        foreach (var p in WrittenP(cipher)) Assert.Equal(0, p & 0x3);
    }

    /// <summary>7.6.4.2, Table 22 (bits 7 and 8 reserved, 1): new protection and lifted restrictions write them set.</summary>
    [Theory]
    [InlineData(PdfCipher.Rc4)]
    [InlineData(PdfCipher.Aes128)]
    [InlineData(PdfCipher.Aes256)]
    public void PermissionBits7And8_WrittenSet(PdfCipher cipher)
    {
        foreach (var p in WrittenP(cipher)) Assert.Equal(0xC0, p & 0xC0);
    }

    /// <summary>7.6.4.2, Table 22 (bits 13 to 32 reserved, 1): new protection and lifted restrictions write them set.</summary>
    [Theory]
    [InlineData(PdfCipher.Rc4)]
    [InlineData(PdfCipher.Aes128)]
    [InlineData(PdfCipher.Aes256)]
    public void PermissionBits13To32_WrittenSet(PdfCipher cipher)
    {
        foreach (var p in WrittenP(cipher)) Assert.Equal(unchecked((int)0xFFFFF000), p & unchecked((int)0xFFFFF000));
    }

    // ---- 7.6.6: crypt filters

    /// <summary>
    /// 7.6.6 (crypt filters only with V 4 or higher): an RC4 file of V 2 whose /Encrypt also carries /CF naming AESV2 for /StmF and /StrF is
    /// read with RC4 as V 2 asks — the crypt filters are passed over, the page content reads.
    /// </summary>
    [Fact]
    public void CryptFilters_BelowV4_PassedOver()
    {
        var file = Builder.Simple(1).Build(cipher: PdfCipher.Rc4, patchEncrypt: d =>
        {
            d.Set(PdfName.Get("CF"), Builder.Dict(("StdCF", Builder.Dict(("CFM", Builder.N("AESV2")), ("Length", PdfInteger.Of(16))))));
            d.Set(PdfName.Get("StmF"), Builder.N("StdCF"));
            d.Set(PdfName.Get("StrF"), Builder.N("StdCF"));
        });
        using var doc = PdfDocument.Open(file);
        Assert.Equal(2, doc.EncryptDictionary!.GetInteger(PdfName.Get("V")));
        Assert.Contains("Seite 1", FirstPageText(doc));
    }

    /// <summary>
    /// 7.6.6 (/CF holds crypt filter dictionaries) with Table 25 (/Type, if present, is CryptFilter): the filter new protection writes under
    /// V 4 and V 5 is a dictionary of type CryptFilter.
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Aes128, 4)]
    [InlineData(PdfCipher.Aes256, 5)]
    public void NewProtection_CryptFilter_IsOfTypeCryptFilter(PdfCipher cipher, int v)
    {
        var encrypt = NewEncrypt(cipher);
        Assert.Equal(v, encrypt.GetInteger(PdfName.Get("V")));
        var std = Assert.IsType<PdfDictionary>(Assert.IsType<PdfDictionary>(encrypt.Get(PdfName.Get("CF"))).Get(PdfName.Get("StdCF")));
        Assert.Equal("CryptFilter", std.GetName(PdfName.Type)?.Value);
    }

    /// <summary>
    /// 7.6.6, Table 25 (handlers may add private entries, named after Annex E): PDF-Knead adds none — the filter of new protection has only
    /// /Type, /CFM, /AuthEvent and /Length.
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Aes128)]
    [InlineData(PdfCipher.Aes256)]
    public void NewProtection_CryptFilter_NoPrivateEntries(PdfCipher cipher)
    {
        var std = NewEncrypt(cipher).GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!;
        Assert.Equal(["AuthEvent", "CFM", "Length", "Type"], std.Entries.Select(e => e.Key.Value).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// 7.6.6 (a stream picks its crypt filter by the /Name of its /Crypt filter parameters; without /Name, Identity): in an AES-128 file, a
    /// stream with a /Crypt filter whose parameters have no /Name is stored plain and reads plain; a stream beside it without the filter is
    /// decrypted with the default filter.
    /// </summary>
    [Fact]
    public void CryptFilter_WithoutName_IsIdentity()
    {
        var file = EncryptedFileWith(PdfCipher.Aes128, "", null,
            ("/Filter /Crypt /DecodeParms << /Type /CryptFilterDecodeParms >>", "Klartext ohne Namen"u8.ToArray(), false),
            ("", "verschluesselt mit StdCF"u8.ToArray(), true));
        using var doc = PdfDocument.Open(file);
        Assert.Equal("Klartext ohne Namen"u8.ToArray(), ExtraData(doc, 0));
        Assert.Equal("verschluesselt mit StdCF"u8.ToArray(), ExtraData(doc, 1));
        Assert.DoesNotContain(doc.Problems, p => p.Contains("Kryptfilter", StringComparison.Ordinal));
    }

    /// <summary>
    /// 7.6.6, Table 25 (/CFM None: the application does not decrypt, the security handler decides): a crypt filter with /CFM None used for
    /// streams leaves their data as stored — it reads unchanged — and the file says so.
    /// </summary>
    [Fact]
    public void CryptFilterMethodNone_DataReadAsStored_AndSaid()
    {
        var file = EncryptedFileWith(PdfCipher.Aes128, "",
            d => d.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!.Set(PdfName.Get("CFM"), Builder.N("None")),
            ("", "nicht verschluesselt"u8.ToArray(), false));
        using var doc = PdfDocument.Open(file);
        Assert.Equal("nicht verschluesselt"u8.ToArray(), ExtraData(doc, 0));
        Assert.Contains(doc.Problems, p => p.Contains("(None)", StringComparison.Ordinal));
    }

    /// <summary>
    /// 7.6.6, Table 25 (/CFM: only the listed values; default None) with the tolerance of sonderregeln.md A16: a crypt filter present in /CF
    /// without /CFM is taken as the method V names (V 5: AES-256), the data decrypt, and the file says that the method was assumed.
    /// </summary>
    [Fact]
    public void CryptFilterWithoutMethod_AssumedAfterV_AndSaid()
    {
        var file = EncryptedFileWith(PdfCipher.Aes256, "",
            d => d.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!.Remove(PdfName.Get("CFM")),
            ("", "Inhalt unter AESV3"u8.ToArray(), true));
        using var doc = PdfDocument.Open(file);
        Assert.Equal("Inhalt unter AESV3"u8.ToArray(), ExtraData(doc, 0));
        Assert.Contains(doc.Problems, p => p.Contains("Verfahren angenommen", StringComparison.Ordinal));
    }

    /// <summary>
    /// 7.6.6, Table 25 (/AuthEvent is ignored for the filters of /StrF and /StmF; DocOpen is used): an AES-128 file whose only filter says
    /// EFOpen but stands for streams and strings asks for the password when it is opened — it does not open with only attachments locked.
    /// </summary>
    [Fact]
    public void AuthEventEFOpen_OnTheDefaultFilters_ActsAsDocOpen()
    {
        var file = Builder.Simple(1).Build(cipher: PdfCipher.Aes128, user: "geheim", patchEncrypt: d =>
            d.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!.Set(PdfName.Get("AuthEvent"), Builder.N("EFOpen")));
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(file).Dispose());
        using var doc = PdfDocument.Open(file, "geheim");
        Assert.False(doc.Security!.AttachmentsLocked);
        Assert.Contains("Seite 1", FirstPageText(doc));
    }

    /// <summary>
    /// 7.6.6, Table 25 (AESV2: AES-128 in CBC mode, 16-byte blocks, a random 16-byte initialisation vector in front, the key from the
    /// handler, 128 bits): the test derives the file key itself (Algorithm 2, checked against /U by Algorithm 5) and the object key
    /// (Algorithm 1 with „sAlT“), and decrypts what the handler wrote with AES of the runtime — twice the same data, two different vectors.
    /// </summary>
    [Fact]
    public void Aesv2_StreamData_DecryptIndependently()
    {
        var (encrypt, handler) = StandardSecurityHandler.Create(PdfCipher.Aes128, "geheim", "Besitzer", PdfPermissions.Print, M4bId);
        var o = encrypt.GetString(PdfName.Get("O"))!.Bytes;
        var p = (int)encrypt.GetInteger(PdfName.Get("P"))!.Value;
        var fileKey = FileKeyR2To4(Padded("geheim"), o, p, M4bId, 4, 16);
        Assert.Equal(encrypt.GetString(PdfName.Get("U"))!.Bytes[..16], UserEntryR2To4(fileKey, M4bId, 4));
        Assert.Equal(128, handler.KeyBits);

        var id = new PdfObjectId(300, 2);
        var objectKey = MD5.HashData([.. fileKey, 0x2C, 0x01, 0x00, 0x02, 0x00, .. "sAlT"u8.ToArray()]);
        var data = Encoding.ASCII.GetBytes("Daten eines Stroms, laenger als ein Block.");
        var first = handler.EncryptStream(id, data);
        var second = handler.EncryptStream(id, data);
        Assert.False(first.AsSpan(0, 16).SequenceEqual(second.AsSpan(0, 16)), "derselbe Startwert zweimal");
        foreach (var stored in new[] { first, second })
        {
            Assert.Equal(0, stored.Length % 16);
            using var aes = Aes.Create();
            aes.Key = objectKey;
            Assert.Equal(data, aes.DecryptCbc(stored.AsSpan(16), stored.AsSpan(0, 16), PaddingMode.PKCS7));
        }
    }

    /// <summary>
    /// 7.6.6, Table 25 (AESV3: AES-256 in CBC mode with the padding of Algorithm 1.A, a random initialisation vector in front, the file key
    /// itself, 256 bits): the test takes the file key from /UE with Algorithm 2.B (checked against /U) and decrypts what the handler wrote
    /// with AES of the runtime — twice the same data, two different vectors.
    /// </summary>
    [Fact]
    public void Aesv3_StreamData_DecryptIndependently()
    {
        var (encrypt, handler) = StandardSecurityHandler.Create(PdfCipher.Aes256, "geheim", "Besitzer", PdfPermissions.Print, M4bId);
        var u = encrypt.GetString(PdfName.Get("U"))!.Bytes;
        var password = "geheim"u8.ToArray();
        Assert.Equal(u[..32], Hash2B(password, u[32..40], []));
        byte[] fileKey;
        using (var aes = Aes.Create())
        {
            aes.Key = Hash2B(password, u[40..48], []);
            fileKey = aes.DecryptCbc(encrypt.GetString(PdfName.Get("UE"))!.Bytes, new byte[16], PaddingMode.None);
        }
        Assert.Equal(256, handler.KeyBits);

        var data = Encoding.ASCII.GetBytes("Daten eines Stroms, laenger als ein Block.");
        var first = handler.EncryptStream(new PdfObjectId(7, 0), data);
        var second = handler.EncryptStream(new PdfObjectId(7, 0), data);
        Assert.False(first.AsSpan(0, 16).SequenceEqual(second.AsSpan(0, 16)), "derselbe Startwert zweimal");
        foreach (var stored in new[] { first, second })
        {
            using var aes = Aes.Create();
            aes.Key = fileKey;
            Assert.Equal(data, aes.DecryptCbc(stored.AsSpan(16), stored.AsSpan(0, 16), PaddingMode.PKCS7));
        }
    }

    /// <summary>
    /// 7.6.6, Table 25 (AESV3) and 7.6.4.2 (revision 6): the test file of a public collection of revision 6 with AESV3, user password „view“, owner password
    /// „master“. The test checks /U or /O itself with Algorithm 2.B, then the program opens it with either password and reads the text of
    /// the unencrypted base file.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("view", false)]
    [InlineData("master", true)]
    public void Aesv3_ForeignFile_ReadsAsItsBase(string password, bool owner)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(TestPdf("testsammlungen/qpdf/qpdf/qtest/qpdf/enc-XI-R6,V5,U=view,O=master.pdf")), password);
        var encrypt = doc.EncryptDictionary!;
        var u = encrypt.GetString(PdfName.Get("U"))!.Bytes[..48];
        var checkedEntry = owner ? encrypt.GetString(PdfName.Get("O"))!.Bytes[..48] : u;
        Assert.Equal(checkedEntry[..32], Hash2B(Encoding.UTF8.GetBytes(password), checkedEntry[32..40], owner ? u : []));
        Assert.Equal("AESV3", encrypt.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!.GetName(PdfName.Get("CFM"))!.Value);

        Assert.Equal(6, doc.Security!.Revision);
        Assert.Equal(owner, doc.Security.IsOwner);
        using var plain = PdfDocument.Open(File.ReadAllBytes(TestPdf("testsammlungen/qpdf/qpdf/qtest/qpdf/enc-XI-base.pdf")));
        Assert.Equal(FirstPageText(plain), FirstPageText(doc));
        Assert.NotEmpty(FirstPageText(doc).Trim());
    }

    // ---- 7.6.5: public-key security handlers (not written)

    /// <summary>
    /// 7.6.5 (public-key handlers: /SubFilter, /Recipients, certificates of the recipients): PDF-Knead protects with the standard handler only
    /// — whatever version and method, the written /Encrypt names /Standard and has neither /SubFilter nor /Recipients, nor has its filter.
    /// </summary>
    [Theory]
    [InlineData("1.4")]
    [InlineData("1.6")]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void NewProtection_OnlyTheStandardHandler(string version)
    {
        using var doc = PdfDocument.Open(FileWith("<< /Length 3 >>\nstream\nABC\nendstream"));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output,
            new PdfSaveOptions { Version = version, Protection = new PdfProtection("geheim", "Besitzer", PdfPermissions.Print) });
        using var saved = PdfDocument.Open(output.ToArray(), "geheim");
        var encrypt = saved.EncryptDictionary!;
        Assert.Equal("Standard", encrypt.GetName(PdfName.Filter)?.Value);
        Assert.False(encrypt.ContainsKey(PdfName.Get("SubFilter")));
        Assert.False(encrypt.ContainsKey(PdfName.Get("Recipients")));
        if (encrypt.GetDictionary(PdfName.Get("CF")) is { } cf)
            foreach (var (_, filter) in cf.Entries)
                Assert.False(((PdfDictionary)filter).ContainsKey(PdfName.Get("Recipients")));
        Assert.Equal("ABC"u8.ToArray(), saved.DecodeStream(Assert.IsType<PdfStream>(saved.Catalog!.Get(PdfName.Get("Extra")))).Data);
    }

    // ---- 7.6.7: unencrypted wrapper documents (not made, foreign ones kept)

    /// <summary>
    /// 7.6.7 (a wrapper has /Collection with the payload as /D and /View /H, and the payload's file specification in /EmbeddedFiles and in
    /// /AF of the catalogue): a foreign wrapper saved as PDF 2.0 keeps all of it — /D and /View, the one name of /EmbeddedFiles and /AF
    /// pointing at the same file specification, with /AFRelationship /EncryptedPayload.
    /// </summary>
    [Fact]
    public void EncryptedPayload_WrapperKeptWhenSaved()
    {
        var file = DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(geheim.pdf) 5 0 R] >> >> /AF [5 0 R] " +
            "/Collection << /Type /Collection /D (geheim.pdf) /View /H >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Filespec /F (geheim.pdf) /UF (geheim.pdf) /EF << /F 6 0 R >> /AFRelationship /EncryptedPayload " +
            "/EP << /Type /EncryptedPayload /Subtype /Beispiel >> >>",
            "<< /Type /EmbeddedFile /Subtype /application#2Fpdf /Length 3 >>\nstream\nABC\nendstream");
        using var saved = DocumentStructureNormTests.Saved(file, new PdfSaveOptions { Version = "2.0" });
        var catalog = saved.Catalog!;
        var collection = catalog.GetDictionary(PdfName.Get("Collection"))!;
        Assert.Equal("geheim.pdf", Encoding.ASCII.GetString(collection.GetString(PdfName.Get("D"))!.Bytes));
        Assert.Equal("H", collection.GetName(PdfName.Get("View"))?.Value);
        var names = catalog.GetDictionary(PdfName.Get("Names"))!.GetDictionary(PdfName.Get("EmbeddedFiles"))!.GetArray(PdfName.Get("Names"))!;
        Assert.Equal(2, names.Count);
        Assert.Equal("geheim.pdf", Encoding.ASCII.GetString(((PdfString)names.Get(0)).Bytes));
        var af = catalog.GetArray(PdfName.Get("AF"))!;
        Assert.Single(af);
        Assert.Equal(Assert.IsType<PdfReference>(names[1]).Id, Assert.IsType<PdfReference>(af[0]).Id);
        Assert.Equal("EncryptedPayload", ((PdfDictionary)af.Get(0)).GetName(PdfName.Get("AFRelationship"))?.Value);
    }
}
