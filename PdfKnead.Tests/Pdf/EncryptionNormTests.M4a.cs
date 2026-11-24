using System.Buffers.Binary;
using System.IO.Compression;
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
/// ISO 32000-2, 7.6.1 to 7.6.4.1 (Konformitätsabgleich M4, packages a-762 and b-763-7641): the encryption dictionary, the general encryption
/// algorithms 1 and 1.A and the standard security handler's passwords. Every value checked is computed here independently, with MD5 and AES
/// of the runtime and the RC4 below — the files of revisions 2 to 4 are built here, not with PDF-Knead's own encryption.
/// </summary>
public sealed partial class EncryptionNormTests
{
    /// <summary>The 32 bytes a password is padded with (ISO 32000-2, 7.6.4.3.2, algorithm 2 step a).</summary>
    private static readonly byte[] PasswordPadding = Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");

    /// <summary>The first part of /ID of the files built here.</summary>
    private static readonly byte[] LegacyId = [.. Enumerable.Range(0, 16).Select(i => (byte)(0x30 + i * 5))];

    /// <summary>RC4, written here for the tests (the runtime has none): key schedule, then the key stream XOR the data.</summary>
    internal static byte[] Arc4(byte[] key, byte[] data)
    {
        var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) % 256;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) % 256;
            j = (j + s[i]) % 256;
            (s[i], s[j]) = (s[j], s[i]);
            output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) % 256]);
        }
        return output;
    }

    /// <summary>Algorithm 1 steps a to d: MD5 over the file key, three low bytes of the object number, two of the generation (low first) and
    /// „sAlT“ for AES; the first n + 5 bytes, at most 16 (all 16 for AES).</summary>
    internal static byte[] ObjectKeyOf(byte[] fileKey, int number, int generation, bool aes)
    {
        byte[] input = [.. fileKey, (byte)number, (byte)(number >> 8), (byte)(number >> 16), (byte)generation, (byte)(generation >> 8),
            .. (aes ? "sAlT"u8.ToArray() : [])];
        var hash = MD5.HashData(input);
        return aes ? hash : hash[..Math.Min(fileKey.Length + 5, 16)];
    }

    /// <summary>AES-CBC with the runtime: the initialisation vector in front, PKCS#7 padding.</summary>
    private static byte[] AesSeal(byte[] key, byte[] iv, byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return [.. iv, .. aes.EncryptCbc(plain, iv, PaddingMode.PKCS7)];
    }

    /// <summary>AES-CBC decryption with the runtime: the first 16 bytes are the initialisation vector.</summary>
    private static byte[] AesOpen(byte[] key, byte[] data, PaddingMode padding = PaddingMode.PKCS7)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), padding);
    }

    /// <summary>A password padded or cut to 32 bytes (algorithm 2 step a; ASCII only here, PDFDocEncoding and Latin-1 agree).</summary>
    private static byte[] PadPassword(string password)
    {
        var raw = Encoding.Latin1.GetBytes(password);
        var n = Math.Min(32, raw.Length);
        return [.. raw.Take(n), .. PasswordPadding.Take(32 - n)];
    }

    /// <summary>Algorithm 2: the file key of revisions 2 to 4 from the padded user password, /O, /P, the first part of /ID.</summary>
    private static byte[] LegacyFileKey(byte[] paddedUser, byte[] o, int p, byte[] id, int r, int n, bool encryptMetadata = true)
    {
        var pBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(pBytes, p);
        byte[] input = [.. paddedUser, .. o[..32], .. pBytes, .. id, .. (r >= 4 && !encryptMetadata ? new byte[] { 0xFF, 0xFF, 0xFF, 0xFF } : [])];
        var hash = MD5.HashData(input);
        if (r >= 3)
            for (var i = 0; i < 50; i++) hash = MD5.HashData(hash[..n]);
        return hash[..n];
    }

    /// <summary>The file key of a file PDF-Knead wrote with revision 3 or 4, computed here from its /O, /P, /ID and the user password.</summary>
    private static byte[] LegacyKeyOf(PdfDocument doc, string user)
    {
        var e = doc.EncryptDictionary!;
        var r = (int)e.GetInteger(PdfName.Get("R"))!;
        var n = r == 2 ? 5 : (int)(e.GetInteger(PdfName.Length) ?? 128) / 8;
        var id = Assert.IsType<PdfString>(doc.Trailer.GetArray(PdfName.ID)!.Get(0)).Bytes;
        return LegacyFileKey(PadPassword(user), e.GetString(PdfName.Get("O"))!.Bytes, (int)e.GetInteger(PdfName.Get("P"))!, id, r,
            n, e.GetBoolean(PdfName.Get("EncryptMetadata")) ?? true);
    }

    /// <summary>A literal string with escapes: „\(“, „\)“, „\\“, „\n“, „\r“ and octal ones for other bytes outside the printable range.</summary>
    private static string Literal(byte[] bytes)
    {
        var text = new StringBuilder("(");
        foreach (var b in bytes)
            text.Append(b switch
            {
                (byte)'(' => "\\(",
                (byte)')' => "\\)",
                (byte)'\\' => "\\\\",
                (byte)'\n' => "\\n",
                (byte)'\r' => "\\r",
                < 0x20 or >= 0x7F => "\\" + Convert.ToString(b, 8).PadLeft(3, '0'),
                _ => ((char)b).ToString(),
            });
        return text.Append(')').ToString();
    }

    /// <summary>A file built by <see cref="LegacyFile"/> and its file key.</summary>
    internal sealed record LegacyPdf(byte[] File, byte[] Key);

    /// <summary>The plain string of /Note in the catalogue of the files built here.</summary>
    internal static readonly byte[] LegacyNote = "Notiz 4711 (erfunden)"u8.ToArray();

    /// <summary>The plain data of the stream of the files built here (object 4, /Extra of the catalogue).</summary>
    internal static readonly byte[] LegacyData = "Inhalt 4711 des Stroms"u8.ToArray();

    /// <summary>
    /// A file of revision 2 to 4 built here independently of PDF-Knead's encryption (algorithms 1 to 5 with MD5 and AES of the runtime and
    /// <see cref="Arc4"/>): owner password „eigner“, user password <paramref name="user"/>; /O by algorithm 3, the file key by algorithm 2 with
    /// <paramref name="keyBytes"/> bytes, /U by algorithm 4 (revision 2) or 5. Object 1 is the catalogue with the direct string /Note, object 4
    /// a stream (/Extra), object 5 the encryption dictionary with <paramref name="entries"/> (/V, /Length, crypt filters as the test needs them).
    /// Strings and the stream are encrypted with the methods given (None: stored plain).
    /// </summary>
    internal static LegacyPdf LegacyFile(int r, int keyBytes, string entries, PdfCipher strings = PdfCipher.Rc4, PdfCipher streams = PdfCipher.Rc4,
        int permissions = -4, string user = "", byte[]? note = null, bool literalNote = false, string streamDict = "", byte[]? streamData = null)
    {
        var paddedUser = PadPassword(user);
        // algorithm 3: /O
        var ownerHash = MD5.HashData(PadPassword("eigner"));
        if (r >= 3)
            for (var i = 0; i < 50; i++) ownerHash = MD5.HashData(ownerHash);
        var ownerKey = ownerHash[..keyBytes];
        var o = Arc4(ownerKey, paddedUser);
        if (r >= 3)
            for (var i = 1; i <= 19; i++) o = Arc4([.. ownerKey.Select(b => (byte)(b ^ i))], o);
        // algorithm 2: the file key; algorithm 4 or 5: /U
        var key = LegacyFileKey(paddedUser, o, permissions, LegacyId, r, keyBytes);
        byte[] u;
        if (r == 2) u = Arc4(key, PasswordPadding);
        else
        {
            u = Arc4(key, MD5.HashData([.. PasswordPadding, .. LegacyId]));
            for (var i = 1; i <= 19; i++) u = Arc4([.. key.Select(b => (byte)(b ^ i))], u);
            u = [.. u, .. new byte[16]];
        }

        byte[] Seal(PdfCipher cipher, int number, byte[] plain) => cipher switch
        {
            PdfCipher.None => plain,
            PdfCipher.Rc4 => Arc4(ObjectKeyOf(key, number, 0, aes: false), plain),
            PdfCipher.Aes128 => AesSeal(ObjectKeyOf(key, number, 0, aes: true), [.. Enumerable.Range(0, 16).Select(i => (byte)(number * 16 + i))], plain),
            _ => throw new ArgumentOutOfRangeException(nameof(cipher)),
        };
        static string Hex(byte[] b) => "<" + Convert.ToHexString(b) + ">";
        var sealedNote = Seal(strings, 1, note ?? LegacyNote);
        var data = Seal(streams, 4, streamData ?? LegacyData);

        var output = new MemoryStream();
        void Write(string s) => output.Write(Encoding.Latin1.GetBytes(s));
        Write("%PDF-1.6\n");
        var offsets = new List<long>();
        void Object(string body)
        {
            offsets.Add(output.Position);
            Write($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        Object($"<< /Type /Catalog /Pages 2 0 R /Extra 4 0 R /Note {(literalNote ? Literal(sealedNote) : Hex(sealedNote))} >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        offsets.Add(output.Position);
        Write($"4 0 obj\n<< {streamDict} /Length {data.Length} >>\nstream\n");
        output.Write(data);
        Write("\nendstream\nendobj\n");
        Object($"<< /Filter /Standard {entries} /R {r} /P {permissions} /O {Hex(o)} /U {Hex(u)} >>");
        var xref = output.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) Write($"{off:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Encrypt 5 0 R /ID [{Hex(LegacyId)} {Hex(LegacyId)}] >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");
        return new LegacyPdf(output.ToArray(), key);
    }

    /// <summary>The stream /Extra of the catalogue, decoded.</summary>
    private static byte[] ExtraData(PdfDocument doc) => doc.DecodeStream(Assert.IsType<PdfStream>(doc.Catalog!.Get(PdfName.Get("Extra")))).Data;

    /// <summary>The string /Note of the catalogue, as read.</summary>
    private static byte[] NoteOf(PdfDocument doc) => doc.Catalog!.GetString(PdfName.Get("Note"))!.Bytes;

    // ------------------------------------------------------------ 7.6.2: the encryption dictionary

    /// <summary>
    /// 7.6.2: an embedded file is encrypted like any other stream. A file with an attachment (/Type /EmbeddedFile, „Anlage 4711“) saved with a
    /// new protection for RC4 (1.4), AES-128 (1.6) and AES-256 (1.7): the plain text does not stand in the bytes, the copy reads it again; for
    /// RC4 and AES-128 the stored bytes decrypt with the key computed here from /O, /P and /ID (algorithms 1 and 2).
    /// </summary>
    [Theory]
    [InlineData("1.4")]
    [InlineData("1.6")]
    [InlineData("1.7")]
    public void EmbeddedFile_NewProtection_EncryptedLikeAnyStream(string version)
    {
        using var doc = PdfDocument.Open(FileWith("<< /Type /EmbeddedFile /Length 11 >>\nstream\nAnlage 4711\nendstream"));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output,
            new PdfSaveOptions { Version = version, ObjectStreams = false, Protection = new PdfProtection("geheim", "Besitzer", PdfPermissions.All) });
        var bytes = output.ToArray();
        Assert.DoesNotContain("Anlage 4711", Encoding.Latin1.GetString(bytes));
        using var saved = PdfDocument.Open(bytes, "geheim");
        var stream = Assert.IsType<PdfStream>(saved.Catalog!.Get(PdfName.Get("Extra")));
        Assert.True(stream.Dictionary.IsType(PdfName.Get("EmbeddedFile")));
        Assert.Equal("Anlage 4711"u8.ToArray(), saved.DecodeStream(stream).Data);
        var stored = saved.FileBytes(stream.DataOffset, stream.DataLength).ToArray();
        var id = stream.Id!.Value;
        switch (version)
        {
            case "1.4":
                Assert.Equal("Anlage 4711"u8.ToArray(), Arc4(ObjectKeyOf(LegacyKeyOf(saved, "geheim"), id.Number, id.Generation, aes: false), stored));
                break;
            case "1.6":
                Assert.Equal("Anlage 4711"u8.ToArray(), AesOpen(ObjectKeyOf(LegacyKeyOf(saved, "geheim"), id.Number, id.Generation, aes: true), stored));
                break;
            default:
                Assert.Equal(32, stored.Length);   // 16 bytes initialisation vector, one block
                break;
        }
    }

    /// <summary>
    /// 7.6.2: where a file has several trailers (incremental updates), /Encrypt should be the same in all of them. An update appended to an
    /// encrypted file — behind a classic table (/Encrypt a reference) and behind a cross-reference stream (/Encrypt direct) — carries exactly
    /// the /Encrypt of the trailer before it in its own newest trailer, not only somewhere in the merged trailer.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncrementalUpdate_EncryptedFile_EncryptTheSameInTheNewTrailer(bool objectStreams)
    {
        static string Text(PdfObject obj)
        {
            using var s = new MemoryStream();
            PdfSerializer.Write(obj, s);
            return Encoding.Latin1.GetString(s.ToArray());
        }
        using var doc = PdfDocument.Open(FileWith("<< /Length 3 >>\nstream\nABC\nendstream"));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output,
            new PdfSaveOptions { Version = "1.7", ObjectStreams = objectStreams, Protection = new PdfProtection("geheim", "Besitzer", PdfPermissions.All) });
        using var saved = PdfDocument.Open(output.ToArray(), "geheim");
        var before = Text(saved.NewestTrailer!.GetRaw(PdfName.Encrypt)!);
        var update = new IncrementalUpdate(saved);
        update.Add(new PdfDictionary());
        using var appended = new MemoryStream();
        update.WriteTo(appended);
        using var again = PdfDocument.Open(appended.ToArray(), "geheim");
        var newest = again.NewestTrailer!;
        Assert.NotNull(newest.GetRaw(PdfName.Prev));   // the trailer of the update, not the first one
        Assert.Equal(before, Text(newest.GetRaw(PdfName.Encrypt) ?? PdfNull.Instance));
        Assert.Equal("ABC"u8.ToArray(), ExtraData(again));
    }

    /// <summary>
    /// 7.6.2, Table 20: V 2 and 3 take the length of the file key from /Length (default 40 bits), V 4 has 128 bits — files built here with keys of
    /// 5 and 16 bytes open with them, the stream and the direct string decrypt; a V-4 file with no /Length anywhere (neither in the dictionary
    /// nor in its crypt filter) opens with 16 bytes.
    /// </summary>
    [Theory]
    [InlineData(3, 5, "/V 2 /Length 40", 40)]
    [InlineData(3, 16, "/V 2 /Length 128", 128)]
    [InlineData(3, 5, "/V 2", 40)]
    [InlineData(4, 16, "/V 4 /CF << /StdCF << /CFM /V2 >> >> /StmF /StdCF /StrF /StdCF", 128)]
    [InlineData(4, 16, "/V 4 /CF << /StdCF << /CFM /V2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF", 128)]
    public void KeyLength_LengthOfV2AndV3_V4With128Bits(int r, int keyBytes, string entries, int bits)
    {
        var file = LegacyFile(r, keyBytes, entries);
        using var doc = PdfDocument.Open(file.File);
        Assert.Equal(bits, doc.Security!.KeyBits);
        Assert.Equal(LegacyData, ExtraData(doc));
        Assert.Equal(LegacyNote, NoteOf(doc));
    }

    /// <summary>
    /// 7.6.2, Table 20 with files of another program: V 1 (revision 2, 40 bits) and V 2 with /Length 128 (revision 3) open with their user and
    /// their owner password and have the key length of their dictionary.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("enc-R2,V1,U=view,O=master.pdf", "view", false, 40)]
    [InlineData("enc-R2,V1,U=view,O=master.pdf", "master", true, 40)]
    [InlineData("enc-R3,V2,U=view,O=master.pdf", "view", false, 128)]
    [InlineData("enc-R3,V2,U=view,O=master.pdf", "master", true, 128)]
    public void KeyLength_ForeignFiles_AsTheirDictionarySays(string name, string password, bool owner, int bits)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(TestPdf("testsammlungen/qpdf/qpdf/qtest/qpdf/" + name)), password);
        Assert.Equal(bits, doc.Security!.KeyBits);
        Assert.Equal(owner, doc.Security.IsOwner);
        Assert.True(doc.PageCount > 0);
        Assert.False(string.IsNullOrWhiteSpace(FirstPageText(doc)));
    }

    /// <summary>
    /// 7.6.2, Table 20: /V is required and 0 is not to be used — a file with /V 0 or without /V is refused with a message that names version 0,
    /// not read as something else.
    /// </summary>
    [Theory]
    [InlineData("/V 0 /Length 40")]
    [InlineData("/Length 40")]
    public void Version_ZeroOrMissing_Refused(string entries)
    {
        var file = LegacyFile(3, 5, entries);
        var error = Assert.ThrowsAny<PdfException>(() => PdfDocument.Open(file.File).Dispose());
        Assert.Equal(Tr.T("Verschlüsselung Version {0} wird nicht unterstützt.", 0), error.Message);
    }

    /// <summary>
    /// 7.6.2, Table 20: V 3 (an unpublished method) does not occur in a conforming file; one that has it with /Length 128 is read as RC4 with the
    /// key length of /Length (a read tolerance, S-A A82) — the stream and the string decrypt.
    /// </summary>
    [Fact]
    public void Version3_ReadAsRc4()
    {
        var file = LegacyFile(3, 16, "/V 3 /Length 128");
        using var doc = PdfDocument.Open(file.File);
        Assert.Equal(PdfCipher.Rc4, doc.Security!.StreamCipher);
        Assert.Equal(LegacyData, ExtraData(doc));
        Assert.Equal(LegacyNote, NoteOf(doc));
    }

    /// <summary>
    /// 7.6.2, Table 20: a new protection writes the version of its method — RC4 with more than 40 bits V 2 (revision 3, no crypt filters),
    /// AES-128 V 4 (revision 4), AES-256 V 5 (revision 6), both with crypt filters.
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Rc4, 2, 3)]
    [InlineData(PdfCipher.Aes128, 4, 4)]
    [InlineData(PdfCipher.Aes256, 5, 6)]
    public void Create_VersionAfterTheMethod(PdfCipher cipher, int v, int r)
    {
        var (dict, _) = StandardSecurityHandler.Create(cipher, "", "eigner", PdfPermissions.All, LegacyId);
        Assert.Equal(v, dict.GetInteger(PdfName.Get("V")));
        Assert.Equal(r, dict.GetInteger(PdfName.Get("R")));
        Assert.Equal(v >= 4, dict.ContainsKey(PdfName.Get("CF")));
    }

    /// <summary>
    /// 7.6.2, Table 20: /Length is the key length in bits, a multiple of 8 from 40 to 128. Files built here with 40, 64 and 128 bits open with 5,
    /// 8 and 16 bytes; values outside (41, 8, 4096) are limited to 5 to 16 bytes as the reader takes them, without a crash.
    /// </summary>
    [Theory]
    [InlineData("/Length 40", 5)]
    [InlineData("/Length 64", 8)]
    [InlineData("/Length 128", 16)]
    [InlineData("/Length 41", 5)]
    [InlineData("/Length 8", 5)]
    [InlineData("/Length 4096", 16)]
    public void Length_Read_InBitsLimitedTo40To128(string length, int keyBytes)
    {
        var file = LegacyFile(3, keyBytes, "/V 2 " + length);
        using var doc = PdfDocument.Open(file.File);
        Assert.Equal(keyBytes * 8, doc.Security!.KeyBits);
        Assert.Equal(LegacyData, ExtraData(doc));
    }

    /// <summary>
    /// 7.6.2, Table 20: /Length belongs to V 2 and 3 only. A new protection with AES-128 (V 4, written for 1.6) has no /Length in its
    /// dictionary — the key length comes from the crypt filter; RC4 (V 2, 1.4) has it.
    /// </summary>
    [Theory]
    [InlineData("1.4", true)]
    [InlineData("1.6", false)]
    public void Length_Written_OnlyWithV2AndV3(string version, bool hasLength)
    {
        using var doc = PdfDocument.Open(FileWith("<< /Length 3 >>\nstream\nABC\nendstream"));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output,
            new PdfSaveOptions { Version = version, Protection = new PdfProtection("geheim", "Besitzer", PdfPermissions.All) });
        using var saved = PdfDocument.Open(output.ToArray(), "geheim");
        Assert.Equal(hasLength, saved.EncryptDictionary!.ContainsKey(PdfName.Length));
    }

    /// <summary>
    /// 7.6.2, Table 20: entries of /CF under a standard name are ignored, the standard filter counts. A V-4 file whose /CF has an entry
    /// „Identity“ (with AESV2) and whose /StmF is Identity stores its stream plain — and it is read plain; strings by StdCF (RC4).
    /// </summary>
    [Fact]
    public void CryptFilters_EntryUnderStandardName_Ignored()
    {
        var file = LegacyFile(4, 16, "/V 4 /CF << /Identity << /CFM /AESV2 >> /StdCF << /CFM /V2 >> >> /StmF /Identity /StrF /StdCF",
            strings: PdfCipher.Rc4, streams: PdfCipher.None);
        using var doc = PdfDocument.Open(file.File);
        Assert.Equal(LegacyData, ExtraData(doc));
        Assert.Equal(LegacyNote, NoteOf(doc));
    }

    /// <summary>7.6.2, Table 20: /CF, /StmF and /StrF are meaningful only with V 4 and 5 — under V 2 they are passed over, everything is RC4.</summary>
    [Fact]
    public void CryptFilters_UnderV2_PassedOver()
    {
        var file = LegacyFile(3, 16, "/V 2 /Length 128 /CF << /StdCF << /CFM /AESV2 >> >> /StmF /StdCF /StrF /StdCF");
        using var doc = PdfDocument.Open(file.File);
        Assert.Equal(PdfCipher.Rc4, doc.Security!.StreamCipher);
        Assert.Equal(LegacyData, ExtraData(doc));
        Assert.Equal(LegacyNote, NoteOf(doc));
    }

    /// <summary>
    /// 7.6.2, Table 20: every crypt filter used is in /CF. A stream naming the filter „Fremd“ (/Crypt with /Name), which /CF does not have, is
    /// reported with its name; it is read with /StmF as before.
    /// </summary>
    [Fact]
    public void CryptFilter_NotInCF_ReportedWithItsName()
    {
        var file = LegacyFile(4, 16, "/V 4 /CF << /StdCF << /CFM /V2 >> >> /StmF /StdCF /StrF /StdCF",
            streamDict: "/Filter /Crypt /DecodeParms << /Name /Fremd >>");
        using var doc = PdfDocument.Open(file.File);
        Assert.Equal(LegacyData, ExtraData(doc));
        Assert.Contains(Tr.T("Ein Datenstrom nennt einen Kryptfilter, den die Verschlüsselung nicht kennt („{0}“).", "Fremd"), doc.Problems);
    }

    /// <summary>7.6.2, Table 20: /StmF defaults to Identity — a V-4 file without it stores its streams plain and they are read plain.</summary>
    [Fact]
    public void StmF_Missing_StreamsIdentity()
    {
        var file = LegacyFile(4, 16, "/V 4 /CF << /StdCF << /CFM /V2 >> >> /StrF /StdCF", strings: PdfCipher.Rc4, streams: PdfCipher.None);
        using var doc = PdfDocument.Open(file.File);
        Assert.Equal(PdfCipher.None, doc.Security!.StreamCipher);
        Assert.Equal(LegacyData, ExtraData(doc));
        Assert.Equal(LegacyNote, NoteOf(doc));
    }

    /// <summary>7.6.2, Table 20: /StrF defaults to Identity — a V-4 file without it stores its strings plain and they are read plain.</summary>
    [Fact]
    public void StrF_Missing_StringsIdentity()
    {
        var file = LegacyFile(4, 16, "/V 4 /CF << /StdCF << /CFM /V2 >> >> /StmF /StdCF", strings: PdfCipher.None, streams: PdfCipher.Rc4);
        using var doc = PdfDocument.Open(file.File);
        Assert.Equal(PdfCipher.None, doc.Security!.StringCipher);
        Assert.Equal(LegacyNote, NoteOf(doc));
        Assert.Equal(LegacyData, ExtraData(doc));
    }

    /// <summary>
    /// 7.6.2, Table 20: without /EFF embedded files are encrypted with /StmF. A V-4 file built here (AES-128) with an attachment and no /EFF
    /// reads it with /StmF; a copy that keeps the encryption writes it with /StmF again — its stored bytes decrypt with the key of the file
    /// and the object key computed here.
    /// </summary>
    [Fact]
    public void EmbeddedFile_WithoutEff_ReadAndWrittenWithStmF()
    {
        var file = LegacyFile(4, 16, "/V 4 /CF << /StdCF << /CFM /AESV2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF",
            strings: PdfCipher.Aes128, streams: PdfCipher.Aes128, streamDict: "/Type /EmbeddedFile");
        using var doc = PdfDocument.Open(file.File);
        Assert.Equal(LegacyData, ExtraData(doc));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "1.7", ObjectStreams = false, KeepEncryption = true });
        using var saved = PdfDocument.Open(output.ToArray());
        var stream = Assert.IsType<PdfStream>(saved.Catalog!.Get(PdfName.Get("Extra")));
        Assert.True(stream.Dictionary.IsType(PdfName.Get("EmbeddedFile")));
        var id = stream.Id!.Value;
        Assert.Equal(LegacyData, AesOpen(ObjectKeyOf(file.Key, id.Number, id.Generation, aes: true), saved.FileBytes(stream.DataOffset, stream.DataLength).ToArray()));
        Assert.Equal(LegacyData, ExtraData(saved));
    }

    /// <summary>
    /// 7.6.2, Table 20: /EFF names a key of /CF or a standard name. A file whose /EFF names a filter /CF does not have („Fehlt“) is read with
    /// /StmF, as with /StmF and /StrF — and, as there, that is said.
    /// </summary>
    [Fact]
    public void EmbeddedFile_EffNotInCF_Reported()
    {
        var file = LegacyFile(4, 16, "/V 4 /CF << /StdCF << /CFM /V2 >> >> /StmF /StdCF /StrF /StdCF /EFF /Fehlt", streamDict: "/Type /EmbeddedFile");
        using var doc = PdfDocument.Open(file.File);
        Assert.Equal(LegacyData, ExtraData(doc));
        Assert.Contains(doc.Problems, p => p.Contains("Fehlt", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ 7.6.3: algorithms 1 and 1.A

    private static readonly byte[] Key32 = [.. Enumerable.Range(0, 32).Select(i => (byte)(0xA5 ^ (i * 7)))];

    /// <summary>
    /// 7.6.3.1: AES pads strings and streams to a multiple of 16 bytes as RFC 8018 asks — 16 − (M mod 16) bytes of that value. Encrypted by the
    /// handler (AES-256 with the file key, AES-128 with the object key), 0, 9 and 16 bytes become IV + 16, IV + 16 and IV + 32 bytes; decrypted
    /// here without removing the padding they end in 16 × 0x10, 7 × 0x07 and 16 × 0x10. Data padded by the runtime are read back exactly.
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Aes256, 0)]
    [InlineData(PdfCipher.Aes256, 9)]
    [InlineData(PdfCipher.Aes256, 16)]
    [InlineData(PdfCipher.Aes128, 9)]
    [InlineData(PdfCipher.Aes128, 16)]
    public void Aes_PaddingAsRfc8018(PdfCipher cipher, int length)
    {
        var handler = StandardSecurityHandler.WithKey(Key32, cipher);
        var id = new PdfObjectId(12, 0);
        var key = cipher == PdfCipher.Aes256 ? Key32 : ObjectKeyOf(Key32, 12, 0, aes: true);
        var plain = Enumerable.Range(0, length).Select(i => (byte)('a' + i)).ToArray();
        var pad = 16 - length % 16;
        var encrypted = handler.EncryptString(id, plain);
        Assert.Equal(16 + length + pad, encrypted.Length);
        var raw = AesOpen(key, encrypted, PaddingMode.None);
        Assert.Equal(plain, raw[..length]);
        Assert.All(raw[length..], b => Assert.Equal(pad, b));
        Assert.Equal(plain, handler.DecryptString(id, AesSeal(key, new byte[16], plain)));
    }

    /// <summary>
    /// 7.6.3.1 and 7.6.3.3: algorithm 1.A takes the 32-byte file key unchanged — strings of objects 1 0 and 258 7 decrypt with AES-256-CBC of the
    /// runtime with exactly that key; the object number does not enter.
    /// </summary>
    [Fact]
    public void Algorithm1A_FileKeyUnchanged_ObjectNumberNotUsed()
    {
        var handler = StandardSecurityHandler.WithKey(Key32, PdfCipher.Aes256);
        var plain = "Gleicher Inhalt in zwei Objekten"u8.ToArray();
        foreach (var id in new[] { new PdfObjectId(1, 0), new PdfObjectId(258, 7) })
            Assert.Equal(plain, AesOpen(Key32, handler.EncryptString(id, plain)));
    }

    /// <summary>
    /// 7.6.3.2, algorithm 1 steps a and b: the key of object 258, generation 7, is MD5 over the file key, 02 01 00 (the object number, low byte
    /// first) and 07 00 (the generation) — with a 5-byte file key its first 10 bytes are the RC4 key: the handler's key stream equals that of RC4
    /// computed here. A direct string takes the number of the object around it: /Note of the catalogue (object 1) of a file built here reads.
    /// </summary>
    [Fact]
    public void Algorithm1_ObjectNumberAndGeneration_LowByteFirst()
    {
        byte[] fileKey = [0x11, 0x22, 0x33, 0x44, 0x55];
        var handler = StandardSecurityHandler.WithKey(fileKey, PdfCipher.Rc4);
        var zeros = new byte[64];
        var expectedKey = MD5.HashData([.. fileKey, 0x02, 0x01, 0x00, 0x07, 0x00])[..10];
        Assert.Equal(Arc4(expectedKey, zeros), handler.EncryptString(new PdfObjectId(258, 7), zeros));
        Assert.Equal(zeros, handler.DecryptString(new PdfObjectId(258, 7), Arc4(expectedKey, zeros)));
        using var doc = PdfDocument.Open(LegacyFile(3, 5, "/V 2 /Length 40").File);
        Assert.Equal(LegacyNote, NoteOf(doc));
    }

    /// <summary>
    /// 7.6.3.2, algorithm 1 steps c and d: the object key is the first n + 5 bytes of the MD5 value, at most 16 — 10 bytes for a 5-byte file key,
    /// 13 for 8, 16 for 16 (RC4 key stream compared with RC4 computed here); for AES „sAlT“ is appended before MD5, and the stream decrypts with
    /// AES-128 of the runtime and that key.
    /// </summary>
    [Theory]
    [InlineData(5, 10)]
    [InlineData(8, 13)]
    [InlineData(16, 16)]
    public void Algorithm1_KeyNPlus5AtMost16_SaltForAes(int n, int objectKeyBytes)
    {
        var fileKey = Key32[..n];
        var zeros = new byte[48];
        var md5 = MD5.HashData([.. fileKey, 0x05, 0x00, 0x00, 0x00, 0x00]);
        Assert.Equal(Arc4(md5[..objectKeyBytes], zeros), StandardSecurityHandler.WithKey(fileKey, PdfCipher.Rc4).EncryptStream(new PdfObjectId(5, 0), zeros));
        if (n != 16) return;
        var aesKey = MD5.HashData([.. fileKey, 0x05, 0x00, 0x00, 0x00, 0x00, 0x73, 0x41, 0x6C, 0x54]);
        Assert.Equal(LegacyData, AesOpen(aesKey, StandardSecurityHandler.WithKey(fileKey, PdfCipher.Aes128).EncryptStream(new PdfObjectId(5, 0), LegacyData)));
    }

    /// <summary>
    /// 7.6.3.2 (AES-128) and 7.6.3.3 (AES-256): CBC with 16 random bytes as initialisation vector in front of the data. The same string encrypted
    /// twice starts with different 16 bytes; with those as IV, AES-CBC of the runtime decrypts the rest with the object key or the file key.
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Aes128)]
    [InlineData(PdfCipher.Aes256)]
    public void Aes_RandomIvInFront_Cbc(PdfCipher cipher)
    {
        var handler = StandardSecurityHandler.WithKey(Key32, cipher);
        var id = new PdfObjectId(7, 0);
        var key = cipher == PdfCipher.Aes256 ? Key32 : ObjectKeyOf(Key32, 7, 0, aes: true);
        var plain = "Zweimal derselbe Klartext, 33 Byte"u8.ToArray();
        var first = handler.EncryptString(id, plain);
        var second = handler.EncryptString(id, plain);
        Assert.NotEqual(first[..16], second[..16]);
        Assert.Equal(16 + 48, first.Length);
        Assert.Equal(plain, AesOpen(key, first));
        Assert.Equal(plain, AesOpen(key, second));
        // the blocks are chained: the same plain text in two blocks gives different cipher blocks (not ECB)
        var twice = handler.EncryptString(id, [.. plain[..16], .. plain[..16]]);
        Assert.NotEqual(twice[16..32], twice[32..48]);
    }

    /// <summary>
    /// 7.6.3.3: stream data are encrypted after the filters and /Length counts the stored bytes. A stream of 300 bytes saved with RC4 (1.4) and
    /// AES-128 (1.6): /Length equals the bytes up to „endstream“ (IV and padding included for AES), and the stored bytes, decrypted with the key
    /// computed here, are a Flate stream that inflates to the content.
    /// </summary>
    [Theory]
    [InlineData("1.4")]
    [InlineData("1.6")]
    public void Stream_EncryptedAfterTheFilters_LengthCountsTheStoredBytes(string version)
    {
        var content = string.Concat(Enumerable.Repeat("Zeile mit Inhalt 4711. ", 14))[..300];
        using var doc = PdfDocument.Open(FileWith($"<< /Length {content.Length} >>\nstream\n{content}\nendstream"));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output,
            new PdfSaveOptions { Version = version, ObjectStreams = false, Protection = new PdfProtection("", "Besitzer", PdfPermissions.All) });
        var bytes = output.ToArray();
        using var saved = PdfDocument.Open(bytes);
        var stream = Assert.IsType<PdfStream>(saved.Catalog!.Get(PdfName.Get("Extra")));
        Assert.Equal("FlateDecode", stream.Dictionary.GetName(PdfName.Filter)?.Value);
        var length = (int)stream.Dictionary.GetInteger(PdfName.Length)!;
        var start = (int)stream.DataOffset;
        Assert.Equal("\nendstream", Encoding.Latin1.GetString(bytes, start + length, 10));
        var stored = bytes[start..(start + length)];
        var id = stream.Id!.Value;
        var key = LegacyKeyOf(saved, "");
        byte[] packed;
        if (version == "1.4") packed = Arc4(ObjectKeyOf(key, id.Number, id.Generation, aes: false), stored);
        else
        {
            Assert.Equal(0, (length - 16) % 16);
            packed = AesOpen(ObjectKeyOf(key, id.Number, id.Generation, aes: true), stored);
        }
        using var inflate = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var plain = new MemoryStream();
        inflate.CopyTo(plain);
        Assert.Equal(content, Encoding.Latin1.GetString(plain.ToArray()));
    }

    /// <summary>
    /// 7.6.3.3: strings are decrypted after their escapes are resolved. A file built here whose encrypted /Note, written as a literal string,
    /// needs „\(“, „\)“, „\\“, „\n“ and octal escapes reads as the plain text.
    /// </summary>
    [Fact]
    public void LiteralString_WithEscapes_DecryptedAfterResolvingThem()
    {
        // the encrypted bytes are chosen (they contain every character that needs an escape); the plain text is what RC4 makes of them
        var target = Encoding.Latin1.GetBytes("Klammer ( zu ) Strich \\ Zeile\n\r Ende\u0001ÿ");
        var key = LegacyFile(3, 16, "/V 2 /Length 128").Key;
        var plain = Arc4(ObjectKeyOf(key, 1, 0, aes: false), target);
        var file = LegacyFile(3, 16, "/V 2 /Length 128", note: plain, literalNote: true);
        var text = Encoding.Latin1.GetString(file.File);
        Assert.Contains("\\(", text);
        Assert.Contains("\\\\", text);
        using var doc = PdfDocument.Open(file.File);
        Assert.Equal(plain, NoteOf(doc));
    }

    // ------------------------------------------------------------ 7.6.4.1: the standard security handler

    /// <summary>
    /// 7.6.4.1: a new protection uses crypt filters only with the name StdCF and /AuthEvent /DocOpen (AESV2 for revision 4, AESV3 for revision
    /// 6), /StmF and /StrF naming it, no other filter and no /EFF; RC4 (revision 3) has no crypt filters.
    /// </summary>
    [Theory]
    [InlineData("1.4", null, 0)]
    [InlineData("1.6", "AESV2", 16)]
    [InlineData("1.7", "AESV3", 32)]
    [InlineData("2.0", "AESV3", 32)]
    public void NewProtection_CryptFilterStdCFWithDocOpen(string version, string? method, int length)
    {
        using var doc = PdfDocument.Open(FileWith("<< /Length 3 >>\nstream\nABC\nendstream"));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output,
            new PdfSaveOptions { Version = version, Protection = new PdfProtection("geheim", "Besitzer", PdfPermissions.All) });
        using var saved = PdfDocument.Open(output.ToArray(), "geheim");
        var encrypt = saved.EncryptDictionary!;
        if (method is null)
        {
            Assert.Null(encrypt.GetRaw(PdfName.Get("CF")));
            return;
        }
        var cf = encrypt.GetDictionary(PdfName.Get("CF"))!;
        Assert.Equal(["StdCF"], cf.Entries.Select(e => e.Key.Value));
        var std = cf.GetDictionary(PdfName.Get("StdCF"))!;
        Assert.Equal("DocOpen", std.GetName(PdfName.Get("AuthEvent"))?.Value);
        Assert.Equal(method, std.GetName(PdfName.Get("CFM"))?.Value);
        Assert.Equal(length, std.GetInteger(PdfName.Length));
        Assert.Equal("StdCF", encrypt.GetName(PdfName.Get("StmF"))?.Value);
        Assert.Equal("StdCF", encrypt.GetName(PdfName.Get("StrF"))?.Value);
        Assert.Null(encrypt.GetRaw(PdfName.Get("EFF")));
    }

    /// <summary>
    /// 7.6.4.1, revision 6: the password is prepared with SASLprep, encoded as UTF-8 and cut to its first 127 bytes — 130 ASCII characters give
    /// their first 127 bytes, 64 × „ä“ (128 bytes) the first 127 (the last character cut in half). A file with such a password opens with the
    /// password cut to 127 bytes (127 characters, 65 × „ä“) and not with one byte less (126 characters, 63 × „ä“).
    /// </summary>
    [Theory]
    [InlineData(130, 'k', 127, 126)]
    [InlineData(64, 'ä', 65, 63)]
    public void Revision6_Password_First127BytesOfUtf8(int count, char c, int opens, int fails)
    {
        var password = new string(c, count - 1) + (c == 'k' ? "!" : "ä");
        var utf8 = Encoding.UTF8.GetBytes(password);
        Assert.True(utf8.Length > 127);
        Assert.Equal(utf8[..127], StandardSecurityHandler.PrepareAesPassword(password));
        var file = Pdf.TestPdf.Simple(1).Build(cipher: PdfCipher.Aes256, user: password, owner: "eigner");
        var cut = c == 'k' ? password[..opens] : new string(c, opens);
        using (var doc = PdfDocument.Open(file, cut)) Assert.False(doc.Security!.IsOwner);
        var shorter = c == 'k' ? password[..fails] : new string(c, fails);
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(file, shorter).Dispose());
    }
}
