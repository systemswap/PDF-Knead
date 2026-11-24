using System.Formats.Asn1;
using System.Reflection;
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
/// M4, sentence by sentence: the tests missing in the assignment of ISO/TS 32003 (AES-GCM) and RFC 3394 (AES key wrap). Values are computed
/// again in the test with the building blocks of the runtime (AES, SHA-2, HKDF, ASN.1), not with the code of PDF-Knead.
/// </summary>
public sealed partial class EncryptionNormTests
{
    // ------------------------------------------------------------ ISO/TS 32003 (AES-GCM)

    private const string M4dTs32003Url = "https://www.iso.org/standard/45876.html";

    /// <summary>A GCM file (V 6, R 7, AESV4) of PDF 2.0 without any /Extensions in its catalogue.</summary>
    private static byte[] M4dGcmFile(int pages = 2, Action<PdfDictionary>? patchEncrypt = null) =>
        PdfKnead.Tests.Pdf.TestPdf.Simple(pages).Build(cipher: PdfCipher.Aes256Gcm, user: "geheim", owner: "chef", version: "2.0", patchEncrypt: patchEncrypt);

    /// <summary>Saved with the encryption kept.</summary>
    private static byte[] M4dSavedKept(byte[] file, bool objectStreams = true, bool writeMac = false)
    {
        using var doc = PdfDocument.Open(file, "geheim");
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output,
            new PdfSaveOptions { Version = "2.0", KeepEncryption = true, ObjectStreams = objectStreams, WriteMac = writeMac });
        return output.ToArray();
    }

    /// <summary>The ISO_ entries of the catalogue's /Extensions with level 32003.</summary>
    private static List<PdfDictionary> M4dGcmDeclarations(PdfDocument doc)
    {
        var iso = Assert.IsType<PdfArray>(doc.Catalog!.GetDictionary(PdfName.Get("Extensions"))!.Get(PdfName.Get("ISO_")));
        return [.. Enumerable.Range(0, iso.Count).Select(i => iso.Get(i)).OfType<PdfDictionary>()
            .Where(d => d.GetInteger(PdfName.Get("ExtensionLevel")) == 32003)];
    }

    /// <summary>
    /// ISO/TS 32003, 4, sentences 1–5 (Table 1): a GCM source without any declaration, saved with its encryption kept, declares the extension
    /// in the copy — under ISO_, as an element of an array, exactly once, with /BaseVersion /2.0, /ExtensionLevel 32003, /ExtensionRevision
    /// „:2023“ (the colon included) and the /URL of the specification. (The existing round trip started from a source declared already.)
    /// </summary>
    [Fact]
    public void Gcm_KeptWithoutDeclaration_DeclaresTs32003WithTheValuesOfTable1()
    {
        var source = M4dGcmFile();
        using (var before = PdfDocument.Open(source, "geheim")) Assert.Null(before.Catalog!.GetDictionary(PdfName.Get("Extensions")));
        using var copy = PdfDocument.Open(M4dSavedKept(source), "geheim");
        Assert.Equal(PdfCipher.Aes256Gcm, copy.Security!.StreamCipher);
        var entry = Assert.Single(M4dGcmDeclarations(copy));
        Assert.Equal("2.0", entry.GetName(PdfName.Get("BaseVersion"))!.Value);
        Assert.IsType<PdfInteger>(entry.Get(PdfName.Get("ExtensionLevel")));
        Assert.Equal(":2023", Encoding.ASCII.GetString(entry.GetString(PdfName.Get("ExtensionRevision"))!.Bytes));
        Assert.Equal(M4dTs32003Url, Encoding.ASCII.GetString(entry.GetString(PdfName.Get("URL"))!.Bytes));
    }

    /// <summary>
    /// ISO/TS 32003, 4, sentences 3–5 (Table 1): the developer extensions dictionary of the declaration carries /Type /DeveloperExtensions —
    /// <c>ExtensionDeclarations.Raise</c> writes none (ISO 32000-2, Table 49 leaves it optional; Table 1 of the TS lists it with its value).
    /// </summary>
    [Fact]
    public void Gcm_KeptWithoutDeclaration_DeclarationHasTypeDeveloperExtensions()
    {
        using var copy = PdfDocument.Open(M4dSavedKept(M4dGcmFile()), "geheim");
        var entry = Assert.Single(M4dGcmDeclarations(copy));
        Assert.Equal("DeveloperExtensions", entry.GetName(PdfName.Type)?.Value);
    }

    /// <summary>
    /// ISO/TS 32003, 5, sentence 26: the /Length of an AESV4 filter is given as for AESV3 — in bytes, 32, for the standard security handler
    /// (ISO 32000-2, Table 25 with the resolution of pdf-issues 184; docs/FEHLERGEDAECHTNIS.md, No. 1096). <c>Create</c> writes AESV4 with the
    /// same /Length as AESV3.
    /// </summary>
    [Fact]
    public void Gcm_Create_CryptFilterLengthAsForAesv3_32Bytes()
    {
        static PdfDictionary StdCf(PdfDictionary encrypt) => encrypt.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!;
        var (gcm, _) = StandardSecurityHandler.Create(PdfCipher.Aes256Gcm, "geheim", "chef", PdfPermissions.Print, new byte[16]);
        var (aes, _) = StandardSecurityHandler.Create(PdfCipher.Aes256, "geheim", "chef", PdfPermissions.Print, new byte[16]);
        Assert.Equal("AESV4", StdCf(gcm).GetName(PdfName.Get("CFM"))!.Value);
        Assert.Equal("AESV3", StdCf(aes).GetName(PdfName.Get("CFM"))!.Value);
        Assert.Equal(32, StdCf(gcm).GetInteger(PdfName.Length));
        Assert.Equal(StdCf(aes).GetInteger(PdfName.Length), StdCf(gcm).GetInteger(PdfName.Length));
    }

    /// <summary>
    /// ISO/TS 32003, 5, sentence 26, reading and kept encryption: an AESV4 filter with /Length 256 (bits, as 2.0 files of other writers have
    /// it) or 32 opens without a note, and the copy with the encryption kept carries 32 (bytes, as for AESV3).
    /// </summary>
    [Theory]
    [InlineData(256)]
    [InlineData(32)]
    public void Gcm_KeptCryptFilterLength_WrittenAs32Bytes(int sourceLength)
    {
        var source = M4dGcmFile(patchEncrypt: e => e.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!.Set(PdfName.Length, PdfInteger.Of(sourceLength)));
        using (var doc = PdfDocument.Open(source, "geheim"))
        {
            Assert.Equal(sourceLength, doc.EncryptDictionary!.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!.GetInteger(PdfName.Length));
            Assert.Empty(doc.Problems);
            Assert.Contains("Seite 1", FirstPageText(doc));
        }
        using var copy = PdfDocument.Open(M4dSavedKept(source), "geheim");
        var std = copy.EncryptDictionary!.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!;
        Assert.Equal("AESV4", std.GetName(PdfName.Get("CFM"))!.Value);
        Assert.Equal(32, std.GetInteger(PdfName.Length));
        Assert.Contains("Seite 1", FirstPageText(copy));
    }

    /// <summary>
    /// ISO/TS 32003, 5, sentence 32: no initialisation vector twice under the one key of the filter — the same data encrypted again, for the
    /// same and for other objects, as strings and as streams, always get another IV (the first 12 bytes).
    /// </summary>
    [Fact]
    public void Gcm_InitializationVectors_NeverTwiceUnderOneKey()
    {
        var handler = StandardSecurityHandler.WithKey(Enumerable.Range(0, 32).Select(i => (byte)(i * 3 + 1)).ToArray(), PdfCipher.Aes256Gcm);
        var data = "the same plain text"u8.ToArray();
        var ivs = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            var id = new PdfObjectId(i % 5 + 1, 0);
            var sealedData = i % 2 == 0 ? handler.EncryptStream(id, data) : handler.EncryptString(id, data);
            Assert.Equal(12 + data.Length + 16, sealedData.Length);
            ivs.Add(Convert.ToHexString(sealedData, 0, 12));
        }
        Assert.Equal(ivs.Count, ivs.Distinct().Count());
    }

    /// <summary>
    /// ISO/TS 32003, 5, sentence 32, in a saved copy: every encrypted stream of a GCM file saved with its encryption kept has an IV of its own.
    /// </summary>
    [Fact]
    public void Gcm_SavedCopy_EveryStreamHasItsOwnInitializationVector()
    {
        var bytes = M4dSavedKept(M4dGcmFile(pages: 6), objectStreams: false);
        var text = Encoding.Latin1.GetString(bytes);
        var ivs = new List<string>();
        foreach (Match m in Regex.Matches(text, @"(?<!end)stream\r?\n"))
        {
            var dict = text[text.LastIndexOf(" obj", m.Index, StringComparison.Ordinal)..m.Index];
            if (dict.Contains("/XRef", StringComparison.Ordinal)) continue;
            ivs.Add(Convert.ToHexString(bytes, m.Index + m.Length, 12));
        }
        Assert.True(ivs.Count >= 6, $"{ivs.Count} streams");
        Assert.Equal(ivs.Count, ivs.Distinct().Count());
        using var copy = PdfDocument.Open(bytes, "geheim");
        Assert.Empty(copy.Problems);
    }

    /// <summary>
    /// ISO/TS 32003, 5, sentence 43: at most (2^39 − 256) bits — 2^36 − 32 bytes — of plain text per string or stream. Every way into the
    /// encryption of the handler takes the plain text as one array or span, whose length the runtime keeps below that limit.
    /// </summary>
    [Fact]
    public void Gcm_PlaintextLimit_NoEncryptionInputBeyondTheLimit()
    {
        var limit = (1L << 36) - 32;
        Assert.True(Array.MaxLength < limit, $"{Array.MaxLength} >= {limit}");
        var ways = typeof(StandardSecurityHandler).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name.StartsWith("Encrypt", StringComparison.Ordinal) || m.Name == "GcmSeal").ToList();
        Assert.Contains(ways, m => m.Name == "GcmSeal");
        Assert.Contains(ways, m => m.Name == "EncryptStream");
        foreach (var way in ways)
            foreach (var parameter in way.GetParameters())
                Assert.True(parameter.ParameterType == typeof(PdfObjectId) || parameter.ParameterType == typeof(PdfCipher)
                            || parameter.ParameterType == typeof(byte[]) || parameter.ParameterType == typeof(ReadOnlySpan<byte>),
                    $"{way.Name}({parameter.ParameterType.Name} {parameter.Name})");
    }

    // ------------------------------------------------------------ RFC 3394 (AES key wrap)

    /// <summary>
    /// RFC 3394, 2.2.1 in the form with shifting registers (sentences 7–12) — written here with the AES of the runtime, independent of
    /// <see cref="PdfMac"/>; <paramref name="iv"/> the initial value of A.
    /// </summary>
    private static byte[] M4dShiftWrap(byte[] kek, byte[] key, byte[] iv)
    {
        var n = key.Length / 8;
        var a = iv.ToArray();
        var r = Enumerable.Range(0, n).Select(i => key[(i * 8)..(i * 8 + 8)]).ToList();
        using var aes = Aes.Create();
        aes.Key = kek;
        for (var t = 1; t <= 6 * n; t++)
        {
            var b = aes.EncryptEcb([.. a, .. r[0]], PaddingMode.None);
            a = M4dXorT(b[..8], t);
            r.RemoveAt(0);
            r.Add(b[8..]);
        }
        return [.. a, .. r.SelectMany(x => x)];
    }

    /// <summary>RFC 3394, 2.2.2 with shifting registers (sentences 6–11), independent of <see cref="PdfMac"/>; null if A0 is not <paramref name="iv"/>.</summary>
    private static byte[]? M4dShiftUnwrap(byte[] kek, byte[] wrapped, byte[] iv)
    {
        var n = wrapped.Length / 8 - 1;
        var a = wrapped[..8];
        var r = Enumerable.Range(1, n).Select(i => wrapped[(i * 8)..(i * 8 + 8)]).ToList();
        using var aes = Aes.Create();
        aes.Key = kek;
        for (var t = 6 * n; t >= 1; t--)
        {
            var b = aes.DecryptEcb([.. M4dXorT(a, t), .. r[n - 1]], PaddingMode.None);
            a = b[..8];
            r.RemoveAt(n - 1);
            r.Insert(0, b[8..]);
        }
        return a.SequenceEqual(iv) ? [.. r.SelectMany(x => x)] : null;
    }

    private static byte[] M4dXorT(byte[] a, int t)
    {
        var x = a.ToArray();
        for (var k = 0; k < 8; k++) x[k] ^= (byte)((ulong)t >> (8 * (7 - k)));
        return x;
    }

    private static readonly byte[] M4dDefaultIv = Convert.FromHexString("A6A6A6A6A6A6A6A6");

    /// <summary>A test vector of RFC 3394, section 4: inputs, output, and every row of its wrap and unwrap tables as „t|phase|A‖R“.</summary>
    private sealed record M4dKeyWrapVector(string Section, string Kek, string Key, string Ciphertext, string Plaintext, string[] Wrap, string[] Unwrap);

    private static M4dKeyWrapVector M4dVector(string section) => section switch
    {
        "4.1" => M4dRfc3394_4_1,
        "4.2" => M4dRfc3394_4_2,
        "4.3" => M4dRfc3394_4_3,
        "4.4" => M4dRfc3394_4_4,
        "4.5" => M4dRfc3394_4_5,
        "4.6" => M4dRfc3394_4_6,
        _ => throw new ArgumentOutOfRangeException(nameof(section)),
    };

    /// <summary>
    /// RFC 3394, 4.1–4.6, wrap: every intermediate value of the tables — for each step t the registers A, R1 … Rn as they go in, after the
    /// encryption (MSB to A, LSB to Ri) and after the XOR with t — and the ciphertext; KEK of 128, 192 and 256 bits (1, sentences 11–12;
    /// 2, sentences 12–14), 128, 192 and 256 bits of key data. The table rows follow one another (each „In“ is the „XorT“ before it).
    /// </summary>
    [Theory]
    [InlineData("4.1")]
    [InlineData("4.2")]
    [InlineData("4.3")]
    [InlineData("4.4")]
    [InlineData("4.5")]
    [InlineData("4.6")]
    public void KeyWrap_Rfc3394Section4_EveryStepOfTheWrap(string section)
    {
        var v = M4dVector(section);
        Assert.Equal(3 * 6 * (v.Key.Length / 16), v.Wrap.Length);
        var rows = new List<string>();
        var wrapped = PdfMac.Wrap(Convert.FromHexString(v.Kek), Convert.FromHexString(v.Key),
            (t, phase, a, r) => rows.Add($"{t}|{phase}|{Convert.ToHexString(a)}{Convert.ToHexString(r)}"));
        Assert.Equal(v.Wrap, rows);
        Assert.Equal(v.Ciphertext, Convert.ToHexString(wrapped));
        Assert.Equal(v.Ciphertext, v.Wrap[^1].Split('|')[2]);
        Assert.Equal("A6A6A6A6A6A6A6A6" + v.Key, v.Wrap[0].Split('|')[2]);
    }

    /// <summary>
    /// RFC 3394, 4.1–4.6, unwrap: every intermediate value — for each step t from 6n down to 1 the registers as they go in, after the XOR of A
    /// with t and after the decryption — the recovered A0 = A6…A6 with the key data, and the output.
    /// </summary>
    [Theory]
    [InlineData("4.1")]
    [InlineData("4.2")]
    [InlineData("4.3")]
    [InlineData("4.4")]
    [InlineData("4.5")]
    [InlineData("4.6")]
    public void KeyUnwrap_Rfc3394Section4_EveryStepOfTheUnwrap(string section)
    {
        var v = M4dVector(section);
        Assert.Equal(3 * 6 * (v.Key.Length / 16), v.Unwrap.Length);
        var rows = new List<string>();
        var key = PdfMac.Unwrap(Convert.FromHexString(v.Kek), Convert.FromHexString(v.Ciphertext),
            (t, phase, a, r) => rows.Add($"{t}|{phase}|{Convert.ToHexString(a)}{Convert.ToHexString(r)}"));
        Assert.Equal(v.Unwrap, rows);
        Assert.Equal(v.Plaintext, rows[^1].Split('|')[2]);
        Assert.StartsWith("A6A6A6A6A6A6A6A6", v.Plaintext);
        Assert.Equal(v.Key, Convert.ToHexString(key!));
    }

    /// <summary>RFC 3394, 2, sentences 3–6: wrapping takes whole 64-bit blocks, at least two — 0, 8, 20 or 23 bytes are refused.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(23)]
    public void KeyWrap_FewerThanTwoBlocksOrNotWholeBlocks_Refused(int length) =>
        Assert.Throws<ArgumentException>(() => PdfMac.Wrap(new byte[16], Enumerable.Range(0, length).Select(i => (byte)i).ToArray()));

    /// <summary>
    /// RFC 3394, 2, sentences 3–6, unwrapping: fewer than three blocks or not whole blocks give nothing — also where the bytes would unwrap
    /// (one block wrapped in six steps; a valid wrap with one byte behind it).
    /// </summary>
    [Fact]
    public void KeyUnwrap_FewerThanThreeBlocksOrNotWholeBlocks_Null()
    {
        var kek = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        var oneBlock = M4dShiftWrap(kek, Convert.FromHexString("0011223344556677"), M4dDefaultIv);
        Assert.Equal(16, oneBlock.Length);
        Assert.Equal("0011223344556677", Convert.ToHexString(M4dShiftUnwrap(kek, oneBlock, M4dDefaultIv)!));
        Assert.Null(PdfMac.Unwrap(kek, oneBlock));
        var valid = Convert.FromHexString("1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5");
        Assert.NotNull(PdfMac.Unwrap(kek, valid));
        Assert.Null(PdfMac.Unwrap(kek, [.. valid, 0x00]));
    }

    /// <summary>
    /// RFC 3394, 2, sentences 7–11: no upper limit for n — 64 blocks (512 bytes) are wrapped as the independent form with shifting registers
    /// wraps them (2.2.1, sentences 7–12 and 15), unwrapped back, and one changed byte gives nothing.
    /// </summary>
    [Fact]
    public void KeyWrap_SixtyFourBlocks_AsTheShiftingForm_AndBack()
    {
        var kek = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var key = Enumerable.Range(0, 512).Select(i => (byte)(i * 37 + 11)).ToArray();
        var wrapped = PdfMac.Wrap(kek, key);
        Assert.Equal(520, wrapped.Length);
        Assert.Equal(M4dShiftWrap(kek, key, M4dDefaultIv), wrapped);
        Assert.Equal(key, PdfMac.Unwrap(kek, wrapped));
        wrapped[260] ^= 0x01;
        Assert.Null(PdfMac.Unwrap(kek, wrapped));
    }

    /// <summary>
    /// RFC 3394, 2.2.3.2, sentences 1–6: PDF-Knead knows only the default initial value (ISO/TS 32004 names aes256-wrap) — data wrapped
    /// correctly with another one (here that of RFC 5649 for 16 bytes) are not unwrapped; with A6…A6 the independent form gives the value of 4.1.
    /// </summary>
    [Fact]
    public void KeyUnwrap_OtherInitialValue_Null()
    {
        var kek = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        var key = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        Assert.Equal("1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5", Convert.ToHexString(M4dShiftWrap(kek, key, M4dDefaultIv)));
        var otherIv = Convert.FromHexString("A65959A600000010");
        var wrapped = M4dShiftWrap(kek, key, otherIv);
        Assert.Equal(key, M4dShiftUnwrap(kek, wrapped, otherIv));
        Assert.Null(PdfMac.Unwrap(kek, wrapped));
    }

    /// <summary>
    /// RFC 3394, 3: of the three identifiers PDF-Knead uses aes256-wrap ({aes 45}); a MAC token naming aes128-wrap ({aes 5}) or aes192-wrap
    /// ({aes 25}) is „not checkable“ — never valid, never wrongly „violated“.
    /// </summary>
    [Theory]
    [InlineData("05")]
    [InlineData("19")]
    public void MacToken_Aes128OrAes192Wrap_NotCheckable(string lastArc)
    {
        var file = PdfKnead.Tests.Pdf.TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: "geheim", owner: "chef", version: "2.0",
            macRequired: true, mac: TestMac.Real);
        using (var doc = PdfDocument.Open(file, "geheim")) Assert.Equal(MacState.Valid, doc.Integrity.State);
        var text = Encoding.Latin1.GetString(file);
        var at = text.IndexOf("060960864801650304012d", StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.Equal(-1, text.IndexOf("060960864801650304012d", at + 1, StringComparison.Ordinal));
        Encoding.ASCII.GetBytes(lastArc).CopyTo(file, at + 20);
        using var changed = PdfDocument.Open(file, "geheim");
        Assert.Equal(MacState.NotCheckable, changed.Integrity.State);
    }

    /// <summary>Algorithm 2.B of ISO 32000-2 (7.6.4.3.4), written here with the runtime's SHA-2 and AES, independent of the handler.</summary>
    private static byte[] M4dHash2B(byte[] password, byte[] salt, byte[] userKey)
    {
        var k = SHA256.HashData([.. password, .. salt, .. userKey]);
        byte[] e = [];
        for (var round = 0; round < 64 || e[^1] > round - 32; round++)
        {
            byte[] once = [.. password, .. k, .. userKey];
            var k1 = new byte[once.Length * 64];
            for (var i = 0; i < 64; i++) once.CopyTo(k1, i * once.Length);
            using var aes = Aes.Create();
            aes.Key = k[..16];
            e = aes.EncryptCbc(k1, k[16..32], PaddingMode.None);
            k = (e[..16].Sum(x => x) % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
        }
        return k[..32];
    }

    /// <summary>The strings of an object written unencrypted (the encryption dictionary, the trailer with /AuthCode), as read.</summary>
    private static IEnumerable<byte[]> M4dStrings(PdfObject? value, int depth = 0) => value switch
    {
        PdfString s => [s.Bytes],
        PdfDictionary d when depth < 8 => d.Entries.SelectMany(e => M4dStrings(e.Value, depth + 1)),
        PdfArray a when depth < 8 => a.SelectMany(x => M4dStrings(x, depth + 1)),
        _ => [],
    };

    private static bool M4dContains(byte[] file, byte[] secret) =>
        file.AsSpan().IndexOf(secret) >= 0
        || file.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Convert.ToHexStringLower(secret))) >= 0
        || file.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Convert.ToHexString(secret))) >= 0;

    /// <summary>
    /// RFC 3394, 5, sentences 5–6: the key encryption key stays secret. A file saved with a fresh MAC holds neither the KEK nor the MAC key
    /// nor the file key — not as bytes, not as hexadecimal digits. The keys are recovered here independently: the file key from /UE with the
    /// user password (algorithm 2.A with 2.B), the KEK with HKDF (ISO/TS 32004, 6.4), the MAC key by unwrapping the token's encrypted key with
    /// the shifting form — which succeeds, so they are the keys the file uses.
    /// </summary>
    [Fact]
    public void MacFile_Saved_NeitherKekNorMacKeyNorFileKeyInTheFile()
    {
        var source = PdfKnead.Tests.Pdf.TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: "geheim", owner: "chef", version: "2.0",
            macRequired: true, mac: TestMac.Real);
        var bytes = M4dSavedKept(source, objectStreams: false, writeMac: true);
        using var copy = PdfDocument.Open(bytes, "geheim");
        Assert.Equal(MacState.Valid, copy.Integrity.State);

        var encrypt = copy.EncryptDictionary!;
        var u = encrypt.GetString(PdfName.Get("U"))!.Bytes;
        var password = "geheim"u8.ToArray();
        Assert.Equal(u[..32], M4dHash2B(password, u[32..40], []));
        using var aes = Aes.Create();
        aes.Key = M4dHash2B(password, u[40..48], []);
        var fileKey = aes.DecryptCbc(encrypt.GetString(PdfName.Get("UE"))!.Bytes, new byte[16], PaddingMode.None);
        var salt = encrypt.GetString(PdfName.Get("KDFSalt"))!.Bytes;
        var kek = HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey, 32, salt, "PDFMAC"u8.ToArray());

        var text = Encoding.Latin1.GetString(bytes);
        var gapStart = text.IndexOf('<', text.LastIndexOf("/MAC", StringComparison.Ordinal)) + 1;
        var token = Convert.FromHexString(text[gapStart..text.IndexOf('>', gapStart)]);
        var outer = new AsnReader(token, AsnEncodingRules.DER).ReadSequence();
        outer.ReadObjectIdentifier();
        var data = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        data.ReadInteger();
        var pwri = data.ReadSetOf().ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 3));
        pwri.ReadInteger();
        pwri.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        pwri.ReadSequence();
        var macKey = M4dShiftUnwrap(kek, pwri.ReadOctetString(), M4dDefaultIv);
        Assert.NotNull(macKey);

        // the bytes of the file (streams written unencrypted, hexadecimal strings) and the strings of the unencrypted dictionaries as read
        // (a literal string can hold a key with escapes in between)
        var plainStrings = M4dStrings(encrypt).Concat(M4dStrings(copy.Trailer)).Concat(M4dStrings(copy.NewestTrailer)).ToList();
        Assert.NotEmpty(plainStrings);
        foreach (var (secret, what) in new[] { (kek, "KEK"), (macKey!, "MAC key"), (fileKey, "file key") })
        {
            Assert.False(M4dContains(bytes, secret), what);
            Assert.DoesNotContain(plainStrings, s => M4dContains(s, secret));
        }
    }

    // RFC 3394, section 4: the test values (data), every row of the tables. Taken from the text of the RFC by a script (hexadecimal values
    // only); the RFC prints the XorT value of A in step 17 of 4.5 with 17 digits („39128CE5E4325F3B1“) — the row stands here with the value
    // that the In of step 18 and the unwrap table give (39128CE5E435F3B1, Enc 39128CE5E435F3A0 XOR 0x11).
    private static readonly M4dKeyWrapVector M4dRfc3394_4_1 = new(
        "4.1",
        "000102030405060708090A0B0C0D0E0F",
        "00112233445566778899AABBCCDDEEFF",
        "1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5",
        "A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF",
        [
            "1|In|A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF",
            "1|Enc|F4740052E82A225174CE86FBD7B805E78899AABBCCDDEEFF",
            "1|XorT|F4740052E82A225074CE86FBD7B805E78899AABBCCDDEEFF",
            "2|In|F4740052E82A225074CE86FBD7B805E78899AABBCCDDEEFF",
            "2|Enc|06BA4EBDE7768D0B74CE86FBD7B805E7D132EE38147E76F8",
            "2|XorT|06BA4EBDE7768D0974CE86FBD7B805E7D132EE38147E76F8",
            "3|In|06BA4EBDE7768D0974CE86FBD7B805E7D132EE38147E76F8",
            "3|Enc|FC967627BE937208FE6E8D679C5D3460D132EE38147E76F8",
            "3|XorT|FC967627BE93720BFE6E8D679C5D3460D132EE38147E76F8",
            "4|In|FC967627BE93720BFE6E8D679C5D3460D132EE38147E76F8",
            "4|Enc|5896EA9028EE203BFE6E8D679C5D346007B2BD973E36A6FC",
            "4|XorT|5896EA9028EE203FFE6E8D679C5D346007B2BD973E36A6FC",
            "5|In|5896EA9028EE203FFE6E8D679C5D346007B2BD973E36A6FC",
            "5|Enc|93AEA71B258D90C325F5A3ADC219540107B2BD973E36A6FC",
            "5|XorT|93AEA71B258D90C625F5A3ADC219540107B2BD973E36A6FC",
            "6|In|93AEA71B258D90C625F5A3ADC219540107B2BD973E36A6FC",
            "6|Enc|E3EE986344D878F725F5A3ADC2195401F14863BB1E9CA90A",
            "6|XorT|E3EE986344D878F125F5A3ADC2195401F14863BB1E9CA90A",
            "7|In|E3EE986344D878F125F5A3ADC2195401F14863BB1E9CA90A",
            "7|Enc|2BFC21B2C20E4006B556D35ED8CEF052F14863BB1E9CA90A",
            "7|XorT|2BFC21B2C20E4001B556D35ED8CEF052F14863BB1E9CA90A",
            "8|In|2BFC21B2C20E4001B556D35ED8CEF052F14863BB1E9CA90A",
            "8|Enc|4BE8CE99C0A43A7DB556D35ED8CEF05264BAE5818D0570BB",
            "8|XorT|4BE8CE99C0A43A75B556D35ED8CEF05264BAE5818D0570BB",
            "9|In|4BE8CE99C0A43A75B556D35ED8CEF05264BAE5818D0570BB",
            "9|Enc|EBE1CE91067024F3BE114B343EB0098164BAE5818D0570BB",
            "9|XorT|EBE1CE91067024FABE114B343EB0098164BAE5818D0570BB",
            "10|In|EBE1CE91067024FABE114B343EB0098164BAE5818D0570BB",
            "10|Enc|5A9C7B1F5B1C3B46BE114B343EB009814FD3D2B7D74FBB42",
            "10|XorT|5A9C7B1F5B1C3B4CBE114B343EB009814FD3D2B7D74FBB42",
            "11|In|5A9C7B1F5B1C3B4CBE114B343EB009814FD3D2B7D74FBB42",
            "11|Enc|93B71967EED41FFCAEF34BD8FB5A7B824FD3D2B7D74FBB42",
            "11|XorT|93B71967EED41FF7AEF34BD8FB5A7B824FD3D2B7D74FBB42",
            "12|In|93B71967EED41FF7AEF34BD8FB5A7B824FD3D2B7D74FBB42",
            "12|Enc|1FA68B0A8112B44BAEF34BD8FB5A7B829D3E862371D2CFE5",
            "12|XorT|1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5",
        ],
        [
            "12|In|1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5",
            "12|XorT|1FA68B0A8112B44BAEF34BD8FB5A7B829D3E862371D2CFE5",
            "12|Dec|93B71967EED41FF7AEF34BD8FB5A7B824FD3D2B7D74FBB42",
            "11|In|93B71967EED41FF7AEF34BD8FB5A7B824FD3D2B7D74FBB42",
            "11|XorT|93B71967EED41FFCAEF34BD8FB5A7B824FD3D2B7D74FBB42",
            "11|Dec|5A9C7B1F5B1C3B4CBE114B343EB009814FD3D2B7D74FBB42",
            "10|In|5A9C7B1F5B1C3B4CBE114B343EB009814FD3D2B7D74FBB42",
            "10|XorT|5A9C7B1F5B1C3B46BE114B343EB009814FD3D2B7D74FBB42",
            "10|Dec|EBE1CE91067024FABE114B343EB0098164BAE5818D0570BB",
            "9|In|EBE1CE91067024FABE114B343EB0098164BAE5818D0570BB",
            "9|XorT|EBE1CE91067024F3BE114B343EB0098164BAE5818D0570BB",
            "9|Dec|4BE8CE99C0A43A75B556D35ED8CEF05264BAE5818D0570BB",
            "8|In|4BE8CE99C0A43A75B556D35ED8CEF05264BAE5818D0570BB",
            "8|XorT|4BE8CE99C0A43A7DB556D35ED8CEF05264BAE5818D0570BB",
            "8|Dec|2BFC21B2C20E4001B556D35ED8CEF052F14863BB1E9CA90A",
            "7|In|2BFC21B2C20E4001B556D35ED8CEF052F14863BB1E9CA90A",
            "7|XorT|2BFC21B2C20E4006B556D35ED8CEF052F14863BB1E9CA90A",
            "7|Dec|E3EE986344D878F125F5A3ADC2195401F14863BB1E9CA90A",
            "6|In|E3EE986344D878F125F5A3ADC2195401F14863BB1E9CA90A",
            "6|XorT|E3EE986344D878F725F5A3ADC2195401F14863BB1E9CA90A",
            "6|Dec|93AEA71B258D90C625F5A3ADC219540107B2BD973E36A6FC",
            "5|In|93AEA71B258D90C625F5A3ADC219540107B2BD973E36A6FC",
            "5|XorT|93AEA71B258D90C325F5A3ADC219540107B2BD973E36A6FC",
            "5|Dec|5896EA9028EE203FFE6E8D679C5D346007B2BD973E36A6FC",
            "4|In|5896EA9028EE203FFE6E8D679C5D346007B2BD973E36A6FC",
            "4|XorT|5896EA9028EE203BFE6E8D679C5D346007B2BD973E36A6FC",
            "4|Dec|FC967627BE93720BFE6E8D679C5D3460D132EE38147E76F8",
            "3|In|FC967627BE93720BFE6E8D679C5D3460D132EE38147E76F8",
            "3|XorT|FC967627BE937208FE6E8D679C5D3460D132EE38147E76F8",
            "3|Dec|06BA4EBDE7768D0974CE86FBD7B805E7D132EE38147E76F8",
            "2|In|06BA4EBDE7768D0974CE86FBD7B805E7D132EE38147E76F8",
            "2|XorT|06BA4EBDE7768D0B74CE86FBD7B805E7D132EE38147E76F8",
            "2|Dec|F4740052E82A225074CE86FBD7B805E78899AABBCCDDEEFF",
            "1|In|F4740052E82A225074CE86FBD7B805E78899AABBCCDDEEFF",
            "1|XorT|F4740052E82A225174CE86FBD7B805E78899AABBCCDDEEFF",
            "1|Dec|A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF",
        ]);

    private static readonly M4dKeyWrapVector M4dRfc3394_4_2 = new(
        "4.2",
        "000102030405060708090A0B0C0D0E0F1011121314151617",
        "00112233445566778899AABBCCDDEEFF",
        "96778B25AE6CA435F92B5B97C050AED2468AB8A17AD84E5D",
        "A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF",
        [
            "1|In|A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF",
            "1|Enc|DFE8FD5D1A3786A7351D385096CCFB298899AABBCCDDEEFF",
            "1|XorT|DFE8FD5D1A3786A6351D385096CCFB298899AABBCCDDEEFF",
            "2|In|DFE8FD5D1A3786A6351D385096CCFB298899AABBCCDDEEFF",
            "2|Enc|9D9B32B9ED742E02351D385096CCFB2951F22F3286758A2D",
            "2|XorT|9D9B32B9ED742E00351D385096CCFB2951F22F3286758A2D",
            "3|In|9D9B32B9ED742E00351D385096CCFB2951F22F3286758A2D",
            "3|Enc|7B8E343CA51CF8ABBC164F51E20CC98351F22F3286758A2D",
            "3|XorT|7B8E343CA51CF8A8BC164F51E20CC98351F22F3286758A2D",
            "4|In|7B8E343CA51CF8A8BC164F51E20CC98351F22F3286758A2D",
            "4|Enc|02A97C5897140595BC164F51E20CC98305FC2D8F8FF4B919",
            "4|XorT|02A97C5897140591BC164F51E20CC98305FC2D8F8FF4B919",
            "5|In|02A97C5897140591BC164F51E20CC98305FC2D8F8FF4B919",
            "5|Enc|15D4B63F66583817429487269D3A001605FC2D8F8FF4B919",
            "5|XorT|15D4B63F66583812429487269D3A001605FC2D8F8FF4B919",
            "6|In|15D4B63F66583812429487269D3A001605FC2D8F8FF4B919",
            "6|Enc|AE2D0B76A6951EEA429487269D3A001605A2D8FB4DD5BD7A",
            "6|XorT|AE2D0B76A6951EEC429487269D3A001605A2D8FB4DD5BD7A",
            "7|In|AE2D0B76A6951EEC429487269D3A001605A2D8FB4DD5BD7A",
            "7|Enc|79F849444F4B8AA8D40B091CDBAC034005A2D8FB4DD5BD7A",
            "7|XorT|79F849444F4B8AAFD40B091CDBAC034005A2D8FB4DD5BD7A",
            "8|In|79F849444F4B8AAFD40B091CDBAC034005A2D8FB4DD5BD7A",
            "8|Enc|5933A9195B5F5E21D40B091CDBAC034089F0D6C06F8CA9B4",
            "8|XorT|5933A9195B5F5E29D40B091CDBAC034089F0D6C06F8CA9B4",
            "9|In|5933A9195B5F5E29D40B091CDBAC034089F0D6C06F8CA9B4",
            "9|Enc|57ADA800299C2E854D5B3DFE7C04ABBA89F0D6C06F8CA9B4",
            "9|XorT|57ADA800299C2E8C4D5B3DFE7C04ABBA89F0D6C06F8CA9B4",
            "10|In|57ADA800299C2E8C4D5B3DFE7C04ABBA89F0D6C06F8CA9B4",
            "10|Enc|BF17BD6A9BC801634D5B3DFE7C04ABBAEB24CCFA52EA9078",
            "10|XorT|BF17BD6A9BC801694D5B3DFE7C04ABBAEB24CCFA52EA9078",
            "11|In|BF17BD6A9BC801694D5B3DFE7C04ABBAEB24CCFA52EA9078",
            "11|Enc|B68BF270AE81544FF92B5B97C050AED2EB24CCFA52EA9078",
            "11|XorT|B68BF270AE815444F92B5B97C050AED2EB24CCFA52EA9078",
            "12|In|B68BF270AE815444F92B5B97C050AED2EB24CCFA52EA9078",
            "12|Enc|96778B25AE6CA439F92B5B97C050AED2468AB8A17AD84E5D",
            "12|XorT|96778B25AE6CA435F92B5B97C050AED2468AB8A17AD84E5D",
        ],
        [
            "12|In|96778B25AE6CA435F92B5B97C050AED2468AB8A17AD84E5D",
            "12|XorT|96778B25AE6CA439F92B5B97C050AED2468AB8A17AD84E5D",
            "12|Dec|B68BF270AE815444F92B5B97C050AED2EB24CCFA52EA9078",
            "11|In|B68BF270AE815444F92B5B97C050AED2EB24CCFA52EA9078",
            "11|XorT|B68BF270AE81544FF92B5B97C050AED2EB24CCFA52EA9078",
            "11|Dec|BF17BD6A9BC801694D5B3DFE7C04ABBAEB24CCFA52EA9078",
            "10|In|BF17BD6A9BC801694D5B3DFE7C04ABBAEB24CCFA52EA9078",
            "10|XorT|BF17BD6A9BC801634D5B3DFE7C04ABBAEB24CCFA52EA9078",
            "10|Dec|57ADA800299C2E8C4D5B3DFE7C04ABBA89F0D6C06F8CA9B4",
            "9|In|57ADA800299C2E8C4D5B3DFE7C04ABBA89F0D6C06F8CA9B4",
            "9|XorT|57ADA800299C2E854D5B3DFE7C04ABBA89F0D6C06F8CA9B4",
            "9|Dec|5933A9195B5F5E29D40B091CDBAC034089F0D6C06F8CA9B4",
            "8|In|5933A9195B5F5E29D40B091CDBAC034089F0D6C06F8CA9B4",
            "8|XorT|5933A9195B5F5E21D40B091CDBAC034089F0D6C06F8CA9B4",
            "8|Dec|79F849444F4B8AAFD40B091CDBAC034005A2D8FB4DD5BD7A",
            "7|In|79F849444F4B8AAFD40B091CDBAC034005A2D8FB4DD5BD7A",
            "7|XorT|79F849444F4B8AA8D40B091CDBAC034005A2D8FB4DD5BD7A",
            "7|Dec|AE2D0B76A6951EEC429487269D3A001605A2D8FB4DD5BD7A",
            "6|In|AE2D0B76A6951EEC429487269D3A001605A2D8FB4DD5BD7A",
            "6|XorT|AE2D0B76A6951EEA429487269D3A001605A2D8FB4DD5BD7A",
            "6|Dec|15D4B63F66583812429487269D3A001605FC2D8F8FF4B919",
            "5|In|15D4B63F66583812429487269D3A001605FC2D8F8FF4B919",
            "5|XorT|15D4B63F66583817429487269D3A001605FC2D8F8FF4B919",
            "5|Dec|02A97C5897140591BC164F51E20CC98305FC2D8F8FF4B919",
            "4|In|02A97C5897140591BC164F51E20CC98305FC2D8F8FF4B919",
            "4|XorT|02A97C5897140595BC164F51E20CC98305FC2D8F8FF4B919",
            "4|Dec|7B8E343CA51CF8A8BC164F51E20CC98351F22F3286758A2D",
            "3|In|7B8E343CA51CF8A8BC164F51E20CC98351F22F3286758A2D",
            "3|XorT|7B8E343CA51CF8ABBC164F51E20CC98351F22F3286758A2D",
            "3|Dec|9D9B32B9ED742E00351D385096CCFB2951F22F3286758A2D",
            "2|In|9D9B32B9ED742E00351D385096CCFB2951F22F3286758A2D",
            "2|XorT|9D9B32B9ED742E02351D385096CCFB2951F22F3286758A2D",
            "2|Dec|DFE8FD5D1A3786A6351D385096CCFB298899AABBCCDDEEFF",
            "1|In|DFE8FD5D1A3786A6351D385096CCFB298899AABBCCDDEEFF",
            "1|XorT|DFE8FD5D1A3786A7351D385096CCFB298899AABBCCDDEEFF",
            "1|Dec|A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF",
        ]);

    private static readonly M4dKeyWrapVector M4dRfc3394_4_3 = new(
        "4.3",
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F",
        "00112233445566778899AABBCCDDEEFF",
        "64E8C3F9CE0F5BA263E9777905818A2A93C8191E7D6E8AE7",
        "A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF",
        [
            "1|In|A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF",
            "1|Enc|794314D454E3FDE1F661BD9F31FBFA318899AABBCCDDEEFF",
            "1|XorT|794314D454E3FDE0F661BD9F31FBFA318899AABBCCDDEEFF",
            "2|In|794314D454E3FDE0F661BD9F31FBFA318899AABBCCDDEEFF",
            "2|Enc|D450EA5C5BBCB561F661BD9F31FBFA31F60E0CDB7F429FE8",
            "2|XorT|D450EA5C5BBCB563F661BD9F31FBFA31F60E0CDB7F429FE8",
            "3|In|D450EA5C5BBCB563F661BD9F31FBFA31F60E0CDB7F429FE8",
            "3|Enc|85DBDF1879D5C0A55602001BFA07AD8BF60E0CDB7F429FE8",
            "3|XorT|85DBDF1879D5C0A65602001BFA07AD8BF60E0CDB7F429FE8",
            "4|In|85DBDF1879D5C0A65602001BFA07AD8BF60E0CDB7F429FE8",
            "4|Enc|738C291128B7226D5602001BFA07AD8B58924F777C3F678C",
            "4|XorT|738C291128B722695602001BFA07AD8B58924F777C3F678C",
            "5|In|738C291128B722695602001BFA07AD8B58924F777C3F678C",
            "5|Enc|2656A02DFFF054DCF4DF378183E3D5B258924F777C3F678C",
            "5|XorT|2656A02DFFF054D9F4DF378183E3D5B258924F777C3F678C",
            "6|In|2656A02DFFF054D9F4DF378183E3D5B258924F777C3F678C",
            "6|Enc|DDFD0C0E8B52A63AF4DF378183E3D5B291AC1D36A964F41B",
            "6|XorT|DDFD0C0E8B52A63CF4DF378183E3D5B291AC1D36A964F41B",
            "7|In|DDFD0C0E8B52A63CF4DF378183E3D5B291AC1D36A964F41B",
            "7|Enc|39AB00D4AE4399EA5271D5CED80F34ED91AC1D36A964F41B",
            "7|XorT|39AB00D4AE4399ED5271D5CED80F34ED91AC1D36A964F41B",
            "8|In|39AB00D4AE4399ED5271D5CED80F34ED91AC1D36A964F41B",
            "8|Enc|4CE414878463EAAC5271D5CED80F34ED67D8ED899E7929B8",
            "8|XorT|4CE414878463EAA45271D5CED80F34ED67D8ED899E7929B8",
            "9|In|4CE414878463EAA45271D5CED80F34ED67D8ED899E7929B8",
            "9|Enc|FBB44DB106AA07890DF7E5082912364867D8ED899E7929B8",
            "9|XorT|FBB44DB106AA07800DF7E5082912364867D8ED899E7929B8",
            "10|In|FBB44DB106AA07800DF7E5082912364867D8ED899E7929B8",
            "10|Enc|877112A7308ADCC50DF7E508291236483472D5993D318FD2",
            "10|XorT|877112A7308ADCCF0DF7E508291236483472D5993D318FD2",
            "11|In|877112A7308ADCCF0DF7E508291236483472D5993D318FD2",
            "11|Enc|78E40190807CC15163E9777905818A2A3472D5993D318FD2",
            "11|XorT|78E40190807CC15A63E9777905818A2A3472D5993D318FD2",
            "12|In|78E40190807CC15A63E9777905818A2A3472D5993D318FD2",
            "12|Enc|64E8C3F9CE0F5BAE63E9777905818A2A93C8191E7D6E8AE7",
            "12|XorT|64E8C3F9CE0F5BA263E9777905818A2A93C8191E7D6E8AE7",
        ],
        [
            "12|In|64E8C3F9CE0F5BA263E9777905818A2A93C8191E7D6E8AE7",
            "12|XorT|64E8C3F9CE0F5BAE63E9777905818A2A93C8191E7D6E8AE7",
            "12|Dec|78E40190807CC15A63E9777905818A2A3472D5993D318FD2",
            "11|In|78E40190807CC15A63E9777905818A2A3472D5993D318FD2",
            "11|XorT|78E40190807CC15163E9777905818A2A3472D5993D318FD2",
            "11|Dec|877112A7308ADCCF0DF7E508291236483472D5993D318FD2",
            "10|In|877112A7308ADCCF0DF7E508291236483472D5993D318FD2",
            "10|XorT|877112A7308ADCC50DF7E508291236483472D5993D318FD2",
            "10|Dec|FBB44DB106AA07800DF7E5082912364867D8ED899E7929B8",
            "9|In|FBB44DB106AA07800DF7E5082912364867D8ED899E7929B8",
            "9|XorT|FBB44DB106AA07890DF7E5082912364867D8ED899E7929B8",
            "9|Dec|4CE414878463EAA45271D5CED80F34ED67D8ED899E7929B8",
            "8|In|4CE414878463EAA45271D5CED80F34ED67D8ED899E7929B8",
            "8|XorT|4CE414878463EAAC5271D5CED80F34ED67D8ED899E7929B8",
            "8|Dec|39AB00D4AE4399ED5271D5CED80F34ED91AC1D36A964F41B",
            "7|In|39AB00D4AE4399ED5271D5CED80F34ED91AC1D36A964F41B",
            "7|XorT|39AB00D4AE4399EA5271D5CED80F34ED91AC1D36A964F41B",
            "7|Dec|DDFD0C0E8B52A63CF4DF378183E3D5B291AC1D36A964F41B",
            "6|In|DDFD0C0E8B52A63CF4DF378183E3D5B291AC1D36A964F41B",
            "6|XorT|DDFD0C0E8B52A63AF4DF378183E3D5B291AC1D36A964F41B",
            "6|Dec|2656A02DFFF054D9F4DF378183E3D5B258924F777C3F678C",
            "5|In|2656A02DFFF054D9F4DF378183E3D5B258924F777C3F678C",
            "5|XorT|2656A02DFFF054DCF4DF378183E3D5B258924F777C3F678C",
            "5|Dec|738C291128B722695602001BFA07AD8B58924F777C3F678C",
            "4|In|738C291128B722695602001BFA07AD8B58924F777C3F678C",
            "4|XorT|738C291128B7226D5602001BFA07AD8B58924F777C3F678C",
            "4|Dec|85DBDF1879D5C0A65602001BFA07AD8BF60E0CDB7F429FE8",
            "3|In|85DBDF1879D5C0A65602001BFA07AD8BF60E0CDB7F429FE8",
            "3|XorT|85DBDF1879D5C0A55602001BFA07AD8BF60E0CDB7F429FE8",
            "3|Dec|D450EA5C5BBCB563F661BD9F31FBFA31F60E0CDB7F429FE8",
            "2|In|D450EA5C5BBCB563F661BD9F31FBFA31F60E0CDB7F429FE8",
            "2|XorT|D450EA5C5BBCB561F661BD9F31FBFA31F60E0CDB7F429FE8",
            "2|Dec|794314D454E3FDE0F661BD9F31FBFA318899AABBCCDDEEFF",
            "1|In|794314D454E3FDE0F661BD9F31FBFA318899AABBCCDDEEFF",
            "1|XorT|794314D454E3FDE1F661BD9F31FBFA318899AABBCCDDEEFF",
            "1|Dec|A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF",
        ]);

    private static readonly M4dKeyWrapVector M4dRfc3394_4_4 = new(
        "4.4",
        "000102030405060708090A0B0C0D0E0F1011121314151617",
        "00112233445566778899AABBCCDDEEFF0001020304050607",
        "031D33264E15D33268F24EC260743EDCE1C6C7DDEE725A936BA814915C6762D2",
        "A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF0001020304050607",
        [
            "1|In|A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF0001020304050607",
            "1|Enc|DFE8FD5D1A3786A7351D385096CCFB298899AABBCCDDEEFF0001020304050607",
            "1|XorT|DFE8FD5D1A3786A6351D385096CCFB298899AABBCCDDEEFF0001020304050607",
            "2|In|DFE8FD5D1A3786A6351D385096CCFB298899AABBCCDDEEFF0001020304050607",
            "2|Enc|9D9B32B9ED742E02351D385096CCFB2951F22F3286758A2D0001020304050607",
            "2|XorT|9D9B32B9ED742E00351D385096CCFB2951F22F3286758A2D0001020304050607",
            "3|In|9D9B32B9ED742E00351D385096CCFB2951F22F3286758A2D0001020304050607",
            "3|Enc|2C8E19A519025B7C351D385096CCFB2951F22F3286758A2DFF540E514DE120A3",
            "3|XorT|2C8E19A519025B7F351D385096CCFB2951F22F3286758A2DFF540E514DE120A3",
            "4|In|2C8E19A519025B7F351D385096CCFB2951F22F3286758A2DFF540E514DE120A3",
            "4|Enc|E727C7BDF822602EA08DAA041D17BBBA51F22F3286758A2DFF540E514DE120A3",
            "4|XorT|E727C7BDF822602AA08DAA041D17BBBA51F22F3286758A2DFF540E514DE120A3",
            "5|In|E727C7BDF822602AA08DAA041D17BBBA51F22F3286758A2DFF540E514DE120A3",
            "5|Enc|15B61F7B25D51700A08DAA041D17BBBAAE82BC1118A5DEA4FF540E514DE120A3",
            "5|XorT|15B61F7B25D51705A08DAA041D17BBBAAE82BC1118A5DEA4FF540E514DE120A3",
            "6|In|15B61F7B25D51705A08DAA041D17BBBAAE82BC1118A5DEA4FF540E514DE120A3",
            "6|Enc|A187755AEA64719CA08DAA041D17BBBAAE82BC1118A5DEA4D1E708FD13778787",
            "6|XorT|A187755AEA64719AA08DAA041D17BBBAAE82BC1118A5DEA4D1E708FD13778787",
            "7|In|A187755AEA64719AA08DAA041D17BBBAAE82BC1118A5DEA4D1E708FD13778787",
            "7|Enc|5A994895D81644B7926ED65A9E853FD9AE82BC1118A5DEA4D1E708FD13778787",
            "7|XorT|5A994895D81644B0926ED65A9E853FD9AE82BC1118A5DEA4D1E708FD13778787",
            "8|In|5A994895D81644B0926ED65A9E853FD9AE82BC1118A5DEA4D1E708FD13778787",
            "8|Enc|864F408C8AB8CDCF926ED65A9E853FD9552A09E141D08AE3D1E708FD13778787",
            "8|XorT|864F408C8AB8CDC7926ED65A9E853FD9552A09E141D08AE3D1E708FD13778787",
            "9|In|864F408C8AB8CDC7926ED65A9E853FD9552A09E141D08AE3D1E708FD13778787",
            "9|Enc|53F4373F575EB7A4926ED65A9E853FD9552A09E141D08AE3ED5E8456E61BD295",
            "9|XorT|53F4373F575EB7AD926ED65A9E853FD9552A09E141D08AE3ED5E8456E61BD295",
            "10|In|53F4373F575EB7AD926ED65A9E853FD9552A09E141D08AE3ED5E8456E61BD295",
            "10|Enc|9EAA4CDA0B1BA5FF98883EDC6B080FB5552A09E141D08AE3ED5E8456E61BD295",
            "10|XorT|9EAA4CDA0B1BA5F598883EDC6B080FB5552A09E141D08AE3ED5E8456E61BD295",
            "11|In|9EAA4CDA0B1BA5F598883EDC6B080FB5552A09E141D08AE3ED5E8456E61BD295",
            "11|Enc|B1B9902C68E0EB5298883EDC6B080FB563F6D88A0663FEF9ED5E8456E61BD295",
            "11|XorT|B1B9902C68E0EB5998883EDC6B080FB563F6D88A0663FEF9ED5E8456E61BD295",
            "12|In|B1B9902C68E0EB5998883EDC6B080FB563F6D88A0663FEF9ED5E8456E61BD295",
            "12|Enc|FCE591D77709A6E098883EDC6B080FB563F6D88A0663FEF9463437433A93EFE5",
            "12|XorT|FCE591D77709A6EC98883EDC6B080FB563F6D88A0663FEF9463437433A93EFE5",
            "13|In|FCE591D77709A6EC98883EDC6B080FB563F6D88A0663FEF9463437433A93EFE5",
            "13|Enc|428428D2BD88CF58C46965F34EFB226163F6D88A0663FEF9463437433A93EFE5",
            "13|XorT|428428D2BD88CF55C46965F34EFB226163F6D88A0663FEF9463437433A93EFE5",
            "14|In|428428D2BD88CF55C46965F34EFB226163F6D88A0663FEF9463437433A93EFE5",
            "14|Enc|6AC861AB961DA578C46965F34EFB226156E3CEE892BBEFC4463437433A93EFE5",
            "14|XorT|6AC861AB961DA576C46965F34EFB226156E3CEE892BBEFC4463437433A93EFE5",
            "15|In|6AC861AB961DA576C46965F34EFB226156E3CEE892BBEFC4463437433A93EFE5",
            "15|Enc|E80DB49CC9A1EA61C46965F34EFB226156E3CEE892BBEFC484943C8C67FCFD53",
            "15|XorT|E80DB49CC9A1EA6EC46965F34EFB226156E3CEE892BBEFC484943C8C67FCFD53",
            "16|In|E80DB49CC9A1EA6EC46965F34EFB226156E3CEE892BBEFC484943C8C67FCFD53",
            "16|Enc|ABEE3534AC465C2C68F24EC260743EDC56E3CEE892BBEFC484943C8C67FCFD53",
            "16|XorT|ABEE3534AC465C3C68F24EC260743EDC56E3CEE892BBEFC484943C8C67FCFD53",
            "17|In|ABEE3534AC465C3C68F24EC260743EDC56E3CEE892BBEFC484943C8C67FCFD53",
            "17|Enc|E7CC8D8CEDE62BF768F24EC260743EDCE1C6C7DDEE725A9384943C8C67FCFD53",
            "17|XorT|E7CC8D8CEDE62BE668F24EC260743EDCE1C6C7DDEE725A9384943C8C67FCFD53",
            "18|In|E7CC8D8CEDE62BE668F24EC260743EDCE1C6C7DDEE725A9384943C8C67FCFD53",
            "18|Enc|031D33264E15D32068F24EC260743EDCE1C6C7DDEE725A936BA814915C6762D2",
            "18|XorT|031D33264E15D33268F24EC260743EDCE1C6C7DDEE725A936BA814915C6762D2",
        ],
        [
            "18|In|031D33264E15D33268F24EC260743EDCE1C6C7DDEE725A936BA814915C6762D2",
            "18|XorT|031D33264E15D32068F24EC260743EDCE1C6C7DDEE725A936BA814915C6762D2",
            "18|Dec|E7CC8D8CEDE62BE668F24EC260743EDCE1C6C7DDEE725A9384943C8C67FCFD53",
            "17|In|E7CC8D8CEDE62BE668F24EC260743EDCE1C6C7DDEE725A9384943C8C67FCFD53",
            "17|XorT|E7CC8D8CEDE62BF768F24EC260743EDCE1C6C7DDEE725A9384943C8C67FCFD53",
            "17|Dec|ABEE3534AC465C3C68F24EC260743EDC56E3CEE892BBEFC484943C8C67FCFD53",
            "16|In|ABEE3534AC465C3C68F24EC260743EDC56E3CEE892BBEFC484943C8C67FCFD53",
            "16|XorT|ABEE3534AC465C2C68F24EC260743EDC56E3CEE892BBEFC484943C8C67FCFD53",
            "16|Dec|E80DB49CC9A1EA6EC46965F34EFB226156E3CEE892BBEFC484943C8C67FCFD53",
            "15|In|E80DB49CC9A1EA6EC46965F34EFB226156E3CEE892BBEFC484943C8C67FCFD53",
            "15|XorT|E80DB49CC9A1EA61C46965F34EFB226156E3CEE892BBEFC484943C8C67FCFD53",
            "15|Dec|6AC861AB961DA576C46965F34EFB226156E3CEE892BBEFC4463437433A93EFE5",
            "14|In|6AC861AB961DA576C46965F34EFB226156E3CEE892BBEFC4463437433A93EFE5",
            "14|XorT|6AC861AB961DA578C46965F34EFB226156E3CEE892BBEFC4463437433A93EFE5",
            "14|Dec|428428D2BD88CF55C46965F34EFB226163F6D88A0663FEF9463437433A93EFE5",
            "13|In|428428D2BD88CF55C46965F34EFB226163F6D88A0663FEF9463437433A93EFE5",
            "13|XorT|428428D2BD88CF58C46965F34EFB226163F6D88A0663FEF9463437433A93EFE5",
            "13|Dec|FCE591D77709A6EC98883EDC6B080FB563F6D88A0663FEF9463437433A93EFE5",
            "12|In|FCE591D77709A6EC98883EDC6B080FB563F6D88A0663FEF9463437433A93EFE5",
            "12|XorT|FCE591D77709A6E098883EDC6B080FB563F6D88A0663FEF9463437433A93EFE5",
            "12|Dec|B1B9902C68E0EB5998883EDC6B080FB563F6D88A0663FEF9ED5E8456E61BD295",
            "11|In|B1B9902C68E0EB5998883EDC6B080FB563F6D88A0663FEF9ED5E8456E61BD295",
            "11|XorT|B1B9902C68E0EB5298883EDC6B080FB563F6D88A0663FEF9ED5E8456E61BD295",
            "11|Dec|9EAA4CDA0B1BA5F598883EDC6B080FB5552A09E141D08AE3ED5E8456E61BD295",
            "10|In|9EAA4CDA0B1BA5F598883EDC6B080FB5552A09E141D08AE3ED5E8456E61BD295",
            "10|XorT|9EAA4CDA0B1BA5FF98883EDC6B080FB5552A09E141D08AE3ED5E8456E61BD295",
            "10|Dec|53F4373F575EB7AD926ED65A9E853FD9552A09E141D08AE3ED5E8456E61BD295",
            "9|In|53F4373F575EB7AD926ED65A9E853FD9552A09E141D08AE3ED5E8456E61BD295",
            "9|XorT|53F4373F575EB7A4926ED65A9E853FD9552A09E141D08AE3ED5E8456E61BD295",
            "9|Dec|864F408C8AB8CDC7926ED65A9E853FD9552A09E141D08AE3D1E708FD13778787",
            "8|In|864F408C8AB8CDC7926ED65A9E853FD9552A09E141D08AE3D1E708FD13778787",
            "8|XorT|864F408C8AB8CDCF926ED65A9E853FD9552A09E141D08AE3D1E708FD13778787",
            "8|Dec|5A994895D81644B0926ED65A9E853FD9AE82BC1118A5DEA4D1E708FD13778787",
            "7|In|5A994895D81644B0926ED65A9E853FD9AE82BC1118A5DEA4D1E708FD13778787",
            "7|XorT|5A994895D81644B7926ED65A9E853FD9AE82BC1118A5DEA4D1E708FD13778787",
            "7|Dec|A187755AEA64719AA08DAA041D17BBBAAE82BC1118A5DEA4D1E708FD13778787",
            "6|In|A187755AEA64719AA08DAA041D17BBBAAE82BC1118A5DEA4D1E708FD13778787",
            "6|XorT|A187755AEA64719CA08DAA041D17BBBAAE82BC1118A5DEA4D1E708FD13778787",
            "6|Dec|15B61F7B25D51705A08DAA041D17BBBAAE82BC1118A5DEA4FF540E514DE120A3",
            "5|In|15B61F7B25D51705A08DAA041D17BBBAAE82BC1118A5DEA4FF540E514DE120A3",
            "5|XorT|15B61F7B25D51700A08DAA041D17BBBAAE82BC1118A5DEA4FF540E514DE120A3",
            "5|Dec|E727C7BDF822602AA08DAA041D17BBBA51F22F3286758A2DFF540E514DE120A3",
            "4|In|E727C7BDF822602AA08DAA041D17BBBA51F22F3286758A2DFF540E514DE120A3",
            "4|XorT|E727C7BDF822602EA08DAA041D17BBBA51F22F3286758A2DFF540E514DE120A3",
            "4|Dec|2C8E19A519025B7F351D385096CCFB2951F22F3286758A2DFF540E514DE120A3",
            "3|In|2C8E19A519025B7F351D385096CCFB2951F22F3286758A2DFF540E514DE120A3",
            "3|XorT|2C8E19A519025B7C351D385096CCFB2951F22F3286758A2DFF540E514DE120A3",
            "3|Dec|9D9B32B9ED742E00351D385096CCFB2951F22F3286758A2D0001020304050607",
            "2|In|9D9B32B9ED742E00351D385096CCFB2951F22F3286758A2D0001020304050607",
            "2|XorT|9D9B32B9ED742E02351D385096CCFB2951F22F3286758A2D0001020304050607",
            "2|Dec|DFE8FD5D1A3786A6351D385096CCFB298899AABBCCDDEEFF0001020304050607",
            "1|In|DFE8FD5D1A3786A6351D385096CCFB298899AABBCCDDEEFF0001020304050607",
            "1|XorT|DFE8FD5D1A3786A7351D385096CCFB298899AABBCCDDEEFF0001020304050607",
            "1|Dec|A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF0001020304050607",
        ]);

    private static readonly M4dKeyWrapVector M4dRfc3394_4_5 = new(
        "4.5",
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F",
        "00112233445566778899AABBCCDDEEFF0001020304050607",
        "A8F9BC1612C68B3FF6E6F4FBE30E71E4769C8B80A32CB8958CD5D17D6B254DA1",
        "A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF0001020304050607",
        [
            "1|In|A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF0001020304050607",
            "1|Enc|794314D454E3FDE1F661BD9F31FBFA318899AABBCCDDEEFF0001020304050607",
            "1|XorT|794314D454E3FDE0F661BD9F31FBFA318899AABBCCDDEEFF0001020304050607",
            "2|In|794314D454E3FDE0F661BD9F31FBFA318899AABBCCDDEEFF0001020304050607",
            "2|Enc|D450EA5C5BBCB561F661BD9F31FBFA31F60E0CDB7F429FE80001020304050607",
            "2|XorT|D450EA5C5BBCB563F661BD9F31FBFA31F60E0CDB7F429FE80001020304050607",
            "3|In|D450EA5C5BBCB563F661BD9F31FBFA31F60E0CDB7F429FE80001020304050607",
            "3|Enc|9DF8F5405FBC00C1F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B5154",
            "3|XorT|9DF8F5405FBC00C2F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B5154",
            "4|In|9DF8F5405FBC00C2F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B5154",
            "4|Enc|F1D28EA6295891EC0CC86A4D9B9C6A31F60E0CDB7F429FE86CA405593A3B5154",
            "4|XorT|F1D28EA6295891E80CC86A4D9B9C6A31F60E0CDB7F429FE86CA405593A3B5154",
            "5|In|F1D28EA6295891E80CC86A4D9B9C6A31F60E0CDB7F429FE86CA405593A3B5154",
            "5|Enc|BF213BFD04E8A24F0CC86A4D9B9C6A31AEBE2D5C8BF747A96CA405593A3B5154",
            "5|XorT|BF213BFD04E8A24A0CC86A4D9B9C6A31AEBE2D5C8BF747A96CA405593A3B5154",
            "6|In|BF213BFD04E8A24A0CC86A4D9B9C6A31AEBE2D5C8BF747A96CA405593A3B5154",
            "6|Enc|6F85BFBDB7E880E30CC86A4D9B9C6A31AEBE2D5C8BF747A939EBC1A1A53FF55B",
            "6|XorT|6F85BFBDB7E880E50CC86A4D9B9C6A31AEBE2D5C8BF747A939EBC1A1A53FF55B",
            "7|In|6F85BFBDB7E880E50CC86A4D9B9C6A31AEBE2D5C8BF747A939EBC1A1A53FF55B",
            "7|Enc|D532789E4E79D819444F92BF78E77BB1AEBE2D5C8BF747A939EBC1A1A53FF55B",
            "7|XorT|D532789E4E79D81E444F92BF78E77BB1AEBE2D5C8BF747A939EBC1A1A53FF55B",
            "8|In|D532789E4E79D81E444F92BF78E77BB1AEBE2D5C8BF747A939EBC1A1A53FF55B",
            "8|Enc|2A5FFCEF1F1916D8444F92BF78E77BB1C6874607903270CD39EBC1A1A53FF55B",
            "8|XorT|2A5FFCEF1F1916D0444F92BF78E77BB1C6874607903270CD39EBC1A1A53FF55B",
            "9|In|2A5FFCEF1F1916D0444F92BF78E77BB1C6874607903270CD39EBC1A1A53FF55B",
            "9|Enc|01271BA91D9804F6444F92BF78E77BB1C6874607903270CD740A273461ED82C6",
            "9|XorT|01271BA91D9804FF444F92BF78E77BB1C6874607903270CD740A273461ED82C6",
            "10|In|01271BA91D9804FF444F92BF78E77BB1C6874607903270CD740A273461ED82C6",
            "10|Enc|A3223BD7237F7033FB1611A83BEB567FC6874607903270CD740A273461ED82C6",
            "10|XorT|A3223BD7237F7039FB1611A83BEB567FC6874607903270CD740A273461ED82C6",
            "11|In|A3223BD7237F7039FB1611A83BEB567FC6874607903270CD740A273461ED82C6",
            "11|Enc|B50C330616E7B1C7FB1611A83BEB567F73EDC8CB9322C34E740A273461ED82C6",
            "11|XorT|B50C330616E7B1CCFB1611A83BEB567F73EDC8CB9322C34E740A273461ED82C6",
            "12|In|B50C330616E7B1CCFB1611A83BEB567F73EDC8CB9322C34E740A273461ED82C6",
            "12|Enc|FB8AFF3F083E12CEFB1611A83BEB567F73EDC8CB9322C34E0B08CFDF48020F0D",
            "12|XorT|FB8AFF3F083E12C2FB1611A83BEB567F73EDC8CB9322C34E0B08CFDF48020F0D",
            "13|In|FB8AFF3F083E12C2FB1611A83BEB567F73EDC8CB9322C34E0B08CFDF48020F0D",
            "13|Enc|82F597607784A33CFB1F2965FCE1E78373EDC8CB9322C34E0B08CFDF48020F0D",
            "13|XorT|82F597607784A331FB1F2965FCE1E78373EDC8CB9322C34E0B08CFDF48020F0D",
            "14|In|82F597607784A331FB1F2965FCE1E78373EDC8CB9322C34E0B08CFDF48020F0D",
            "14|Enc|D48E5E83B7C906DBFB1F2965FCE1E783D36F4FFBA2C82ED90B08CFDF48020F0D",
            "14|XorT|D48E5E83B7C906D5FB1F2965FCE1E783D36F4FFBA2C82ED90B08CFDF48020F0D",
            "15|In|D48E5E83B7C906D5FB1F2965FCE1E783D36F4FFBA2C82ED90B08CFDF48020F0D",
            "15|Enc|1BF2B1CD947311B6FB1F2965FCE1E783D36F4FFBA2C82ED9C490C33642717146",
            "15|XorT|1BF2B1CD947311B9FB1F2965FCE1E783D36F4FFBA2C82ED9C490C33642717146",
            "16|In|1BF2B1CD947311B9FB1F2965FCE1E783D36F4FFBA2C82ED9C490C33642717146",
            "16|Enc|C9F5F26A378011DEF6E6F4FBE30E71E4D36F4FFBA2C82ED9C490C33642717146",
            "16|XorT|C9F5F26A378011CEF6E6F4FBE30E71E4D36F4FFBA2C82ED9C490C33642717146",
            "17|In|C9F5F26A378011CEF6E6F4FBE30E71E4D36F4FFBA2C82ED9C490C33642717146",
            "17|Enc|39128CE5E435F3A0F6E6F4FBE30E71E4769C8B80A32CB895C490C33642717146",
            "17|XorT|39128CE5E435F3B1F6E6F4FBE30E71E4769C8B80A32CB895C490C33642717146",
            "18|In|39128CE5E435F3B1F6E6F4FBE30E71E4769C8B80A32CB895C490C33642717146",
            "18|Enc|A8F9BC1612C68B2DF6E6F4FBE30E71E4769C8B80A32CB8958CD5D17D6B254DA1",
            "18|XorT|A8F9BC1612C68B3FF6E6F4FBE30E71E4769C8B80A32CB8958CD5D17D6B254DA1",
        ],
        [
            "18|In|A8F9BC1612C68B3FF6E6F4FBE30E71E4769C8B80A32CB8958CD5D17D6B254DA1",
            "18|XorT|A8F9BC1612C68B2DF6E6F4FBE30E71E4769C8B80A32CB8958CD5D17D6B254DA1",
            "18|Dec|39128CE5E435F3B1F6E6F4FBE30E71E4769C8B80A32CB895C490C33642717146",
            "17|In|39128CE5E435F3B1F6E6F4FBE30E71E4769C8B80A32CB895C490C33642717146",
            "17|XorT|39128CE5E435F3A0F6E6F4FBE30E71E4769C8B80A32CB895C490C33642717146",
            "17|Dec|C9F5F26A378011CEF6E6F4FBE30E71E4D36F4FFBA2C82ED9C490C33642717146",
            "16|In|C9F5F26A378011CEF6E6F4FBE30E71E4D36F4FFBA2C82ED9C490C33642717146",
            "16|XorT|C9F5F26A378011DEF6E6F4FBE30E71E4D36F4FFBA2C82ED9C490C33642717146",
            "16|Dec|1BF2B1CD947311B9FB1F2965FCE1E783D36F4FFBA2C82ED9C490C33642717146",
            "15|In|1BF2B1CD947311B9FB1F2965FCE1E783D36F4FFBA2C82ED9C490C33642717146",
            "15|XorT|1BF2B1CD947311B6FB1F2965FCE1E783D36F4FFBA2C82ED9C490C33642717146",
            "15|Dec|D48E5E83B7C906D5FB1F2965FCE1E783D36F4FFBA2C82ED90B08CFDF48020F0D",
            "14|In|D48E5E83B7C906D5FB1F2965FCE1E783D36F4FFBA2C82ED90B08CFDF48020F0D",
            "14|XorT|D48E5E83B7C906DBFB1F2965FCE1E783D36F4FFBA2C82ED90B08CFDF48020F0D",
            "14|Dec|82F597607784A331FB1F2965FCE1E78373EDC8CB9322C34E0B08CFDF48020F0D",
            "13|In|82F597607784A331FB1F2965FCE1E78373EDC8CB9322C34E0B08CFDF48020F0D",
            "13|XorT|82F597607784A33CFB1F2965FCE1E78373EDC8CB9322C34E0B08CFDF48020F0D",
            "13|Dec|FB8AFF3F083E12C2FB1611A83BEB567F73EDC8CB9322C34E0B08CFDF48020F0D",
            "12|In|FB8AFF3F083E12C2FB1611A83BEB567F73EDC8CB9322C34E0B08CFDF48020F0D",
            "12|XorT|FB8AFF3F083E12CEFB1611A83BEB567F73EDC8CB9322C34E0B08CFDF48020F0D",
            "12|Dec|B50C330616E7B1CCFB1611A83BEB567F73EDC8CB9322C34E740A273461ED82C6",
            "11|In|B50C330616E7B1CCFB1611A83BEB567F73EDC8CB9322C34E740A273461ED82C6",
            "11|XorT|B50C330616E7B1C7FB1611A83BEB567F73EDC8CB9322C34E740A273461ED82C6",
            "11|Dec|A3223BD7237F7039FB1611A83BEB567FC6874607903270CD740A273461ED82C6",
            "10|In|A3223BD7237F7039FB1611A83BEB567FC6874607903270CD740A273461ED82C6",
            "10|XorT|A3223BD7237F7033FB1611A83BEB567FC6874607903270CD740A273461ED82C6",
            "10|Dec|01271BA91D9804FF444F92BF78E77BB1C6874607903270CD740A273461ED82C6",
            "9|In|01271BA91D9804FF444F92BF78E77BB1C6874607903270CD740A273461ED82C6",
            "9|XorT|01271BA91D9804F6444F92BF78E77BB1C6874607903270CD740A273461ED82C6",
            "9|Dec|2A5FFCEF1F1916D0444F92BF78E77BB1C6874607903270CD39EBC1A1A53FF55B",
            "8|In|2A5FFCEF1F1916D0444F92BF78E77BB1C6874607903270CD39EBC1A1A53FF55B",
            "8|XorT|2A5FFCEF1F1916D8444F92BF78E77BB1C6874607903270CD39EBC1A1A53FF55B",
            "8|Dec|D532789E4E79D81E444F92BF78E77BB1AEBE2D5C8BF747A939EBC1A1A53FF55B",
            "7|In|D532789E4E79D81E444F92BF78E77BB1AEBE2D5C8BF747A939EBC1A1A53FF55B",
            "7|XorT|D532789E4E79D819444F92BF78E77BB1AEBE2D5C8BF747A939EBC1A1A53FF55B",
            "7|Dec|6F85BFBDB7E880E50CC86A4D9B9C6A31AEBE2D5C8BF747A939EBC1A1A53FF55B",
            "6|In|6F85BFBDB7E880E50CC86A4D9B9C6A31AEBE2D5C8BF747A939EBC1A1A53FF55B",
            "6|XorT|6F85BFBDB7E880E30CC86A4D9B9C6A31AEBE2D5C8BF747A939EBC1A1A53FF55B",
            "6|Dec|BF213BFD04E8A24A0CC86A4D9B9C6A31AEBE2D5C8BF747A96CA405593A3B5154",
            "5|In|BF213BFD04E8A24A0CC86A4D9B9C6A31AEBE2D5C8BF747A96CA405593A3B5154",
            "5|XorT|BF213BFD04E8A24F0CC86A4D9B9C6A31AEBE2D5C8BF747A96CA405593A3B5154",
            "5|Dec|F1D28EA6295891E80CC86A4D9B9C6A31F60E0CDB7F429FE86CA405593A3B5154",
            "4|In|F1D28EA6295891E80CC86A4D9B9C6A31F60E0CDB7F429FE86CA405593A3B5154",
            "4|XorT|F1D28EA6295891EC0CC86A4D9B9C6A31F60E0CDB7F429FE86CA405593A3B5154",
            "4|Dec|9DF8F5405FBC00C2F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B5154",
            "3|In|9DF8F5405FBC00C2F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B5154",
            "3|XorT|9DF8F5405FBC00C1F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B5154",
            "3|Dec|D450EA5C5BBCB563F661BD9F31FBFA31F60E0CDB7F429FE80001020304050607",
            "2|In|D450EA5C5BBCB563F661BD9F31FBFA31F60E0CDB7F429FE80001020304050607",
            "2|XorT|D450EA5C5BBCB561F661BD9F31FBFA31F60E0CDB7F429FE80001020304050607",
            "2|Dec|794314D454E3FDE0F661BD9F31FBFA318899AABBCCDDEEFF0001020304050607",
            "1|In|794314D454E3FDE0F661BD9F31FBFA318899AABBCCDDEEFF0001020304050607",
            "1|XorT|794314D454E3FDE1F661BD9F31FBFA318899AABBCCDDEEFF0001020304050607",
            "1|Dec|A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF0001020304050607",
        ]);

    private static readonly M4dKeyWrapVector M4dRfc3394_4_6 = new(
        "4.6",
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F",
        "00112233445566778899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F",
        "28C9F404C4B810F4CBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43BFB988B9B7A02DD21",
        "A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F",
        [
            "1|In|A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F",
            "1|Enc|794314D454E3FDE1F661BD9F31FBFA318899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F",
            "1|XorT|794314D454E3FDE0F661BD9F31FBFA318899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F",
            "2|In|794314D454E3FDE0F661BD9F31FBFA318899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F",
            "2|Enc|D450EA5C5BBCB561F661BD9F31FBFA31F60E0CDB7F429FE8000102030405060708090A0B0C0D0E0F",
            "2|XorT|D450EA5C5BBCB563F661BD9F31FBFA31F60E0CDB7F429FE8000102030405060708090A0B0C0D0E0F",
            "3|In|D450EA5C5BBCB563F661BD9F31FBFA31F60E0CDB7F429FE8000102030405060708090A0B0C0D0E0F",
            "3|Enc|9DF8F5405FBC00C1F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B515408090A0B0C0D0E0F",
            "3|XorT|9DF8F5405FBC00C2F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B515408090A0B0C0D0E0F",
            "4|In|9DF8F5405FBC00C2F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B515408090A0B0C0D0E0F",
            "4|Enc|564408FDD0DD2EA4F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B5154E5923CB9FDB56FBC",
            "4|XorT|564408FDD0DD2EA0F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B5154E5923CB9FDB56FBC",
            "5|In|564408FDD0DD2EA0F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B5154E5923CB9FDB56FBC",
            "5|Enc|4EF02EDD3146AFBBE7D1194D853E53F8F60E0CDB7F429FE86CA405593A3B5154E5923CB9FDB56FBC",
            "5|XorT|4EF02EDD3146AFBEE7D1194D853E53F8F60E0CDB7F429FE86CA405593A3B5154E5923CB9FDB56FBC",
            "6|In|4EF02EDD3146AFBEE7D1194D853E53F8F60E0CDB7F429FE86CA405593A3B5154E5923CB9FDB56FBC",
            "6|Enc|963AAFFD96B223ECE7D1194D853E53F8EFD48BA3049455766CA405593A3B5154E5923CB9FDB56FBC",
            "6|XorT|963AAFFD96B223EAE7D1194D853E53F8EFD48BA3049455766CA405593A3B5154E5923CB9FDB56FBC",
            "7|In|963AAFFD96B223EAE7D1194D853E53F8EFD48BA3049455766CA405593A3B5154E5923CB9FDB56FBC",
            "7|Enc|66D7A8ADD086B9DDE7D1194D853E53F8EFD48BA304945576C365B66943E2D760E5923CB9FDB56FBC",
            "7|XorT|66D7A8ADD086B9DAE7D1194D853E53F8EFD48BA304945576C365B66943E2D760E5923CB9FDB56FBC",
            "8|In|66D7A8ADD086B9DAE7D1194D853E53F8EFD48BA304945576C365B66943E2D760E5923CB9FDB56FBC",
            "8|Enc|C58B9D3AC6D5B94EE7D1194D853E53F8EFD48BA304945576C365B66943E2D76073E3B6CBE5D05D74",
            "8|XorT|C58B9D3AC6D5B946E7D1194D853E53F8EFD48BA304945576C365B66943E2D76073E3B6CBE5D05D74",
            "9|In|C58B9D3AC6D5B946E7D1194D853E53F8EFD48BA304945576C365B66943E2D76073E3B6CBE5D05D74",
            "9|Enc|1A681354E84C41F8D6AE29ECE7192D43EFD48BA304945576C365B66943E2D76073E3B6CBE5D05D74",
            "9|XorT|1A681354E84C41F1D6AE29ECE7192D43EFD48BA304945576C365B66943E2D76073E3B6CBE5D05D74",
            "10|In|1A681354E84C41F1D6AE29ECE7192D43EFD48BA304945576C365B66943E2D76073E3B6CBE5D05D74",
            "10|Enc|DBA417FB51F9E3CBD6AE29ECE7192D43FBEC169FA5C0F6BAC365B66943E2D76073E3B6CBE5D05D74",
            "10|XorT|DBA417FB51F9E3C1D6AE29ECE7192D43FBEC169FA5C0F6BAC365B66943E2D76073E3B6CBE5D05D74",
            "11|In|DBA417FB51F9E3C1D6AE29ECE7192D43FBEC169FA5C0F6BAC365B66943E2D76073E3B6CBE5D05D74",
            "11|Enc|0629EB29A42E4FD9D6AE29ECE7192D43FBEC169FA5C0F6BAF56701DAF038821673E3B6CBE5D05D74",
            "11|XorT|0629EB29A42E4FD2D6AE29ECE7192D43FBEC169FA5C0F6BAF56701DAF038821673E3B6CBE5D05D74",
            "12|In|0629EB29A42E4FD2D6AE29ECE7192D43FBEC169FA5C0F6BAF56701DAF038821673E3B6CBE5D05D74",
            "12|Enc|F9ED8A1429515665D6AE29ECE7192D43FBEC169FA5C0F6BAF56701DAF03882163CF149E90E8C04D9",
            "12|XorT|F9ED8A1429515669D6AE29ECE7192D43FBEC169FA5C0F6BAF56701DAF03882163CF149E90E8C04D9",
            "13|In|F9ED8A1429515669D6AE29ECE7192D43FBEC169FA5C0F6BAF56701DAF03882163CF149E90E8C04D9",
            "13|Enc|2E8E2B6BB20166964745856AF333F01FFBEC169FA5C0F6BAF56701DAF03882163CF149E90E8C04D9",
            "13|XorT|2E8E2B6BB201669B4745856AF333F01FFBEC169FA5C0F6BAF56701DAF03882163CF149E90E8C04D9",
            "14|In|2E8E2B6BB201669B4745856AF333F01FFBEC169FA5C0F6BAF56701DAF03882163CF149E90E8C04D9",
            "14|Enc|15342443CB95ADB14745856AF333F01FBCA418BBF7DCE60BF56701DAF03882163CF149E90E8C04D9",
            "14|XorT|15342443CB95ADBF4745856AF333F01FBCA418BBF7DCE60BF56701DAF03882163CF149E90E8C04D9",
            "15|In|15342443CB95ADBF4745856AF333F01FBCA418BBF7DCE60BF56701DAF03882163CF149E90E8C04D9",
            "15|Enc|33FE29365885C4B74745856AF333F01FBCA418BBF7DCE60BC272E9466AAE98F93CF149E90E8C04D9",
            "15|XorT|33FE29365885C4B84745856AF333F01FBCA418BBF7DCE60BC272E9466AAE98F93CF149E90E8C04D9",
            "16|In|33FE29365885C4B84745856AF333F01FBCA418BBF7DCE60BC272E9466AAE98F93CF149E90E8C04D9",
            "16|Enc|5075496800978B4A4745856AF333F01FBCA418BBF7DCE60BC272E9466AAE98F940F68C91DB49702C",
            "16|XorT|5075496800978B5A4745856AF333F01FBCA418BBF7DCE60BC272E9466AAE98F940F68C91DB49702C",
            "17|In|5075496800978B5A4745856AF333F01FBCA418BBF7DCE60BC272E9466AAE98F940F68C91DB49702C",
            "17|Enc|A5382A26B47551F11BB8C765A84195E7BCA418BBF7DCE60BC272E9466AAE98F940F68C91DB49702C",
            "17|XorT|A5382A26B47551E01BB8C765A84195E7BCA418BBF7DCE60BC272E9466AAE98F940F68C91DB49702C",
            "18|In|A5382A26B47551E01BB8C765A84195E7BCA418BBF7DCE60BC272E9466AAE98F940F68C91DB49702C",
            "18|Enc|F19D80D437EFE8F91BB8C765A84195E7F7EDAD518C960D36C272E9466AAE98F940F68C91DB49702C",
            "18|XorT|F19D80D437EFE8EB1BB8C765A84195E7F7EDAD518C960D36C272E9466AAE98F940F68C91DB49702C",
            "19|In|F19D80D437EFE8EB1BB8C765A84195E7F7EDAD518C960D36C272E9466AAE98F940F68C91DB49702C",
            "19|Enc|B422B444B87A190B1BB8C765A84195E7F7EDAD518C960D361CFBF6B4C24CB98240F68C91DB49702C",
            "19|XorT|B422B444B87A19181BB8C765A84195E7F7EDAD518C960D361CFBF6B4C24CB98240F68C91DB49702C",
            "20|In|B422B444B87A19181BB8C765A84195E7F7EDAD518C960D361CFBF6B4C24CB98240F68C91DB49702C",
            "20|Enc|D058823360F88A371BB8C765A84195E7F7EDAD518C960D361CFBF6B4C24CB98207DFE775B9687E73",
            "20|XorT|D058823360F88A231BB8C765A84195E7F7EDAD518C960D361CFBF6B4C24CB98207DFE775B9687E73",
            "21|In|D058823360F88A231BB8C765A84195E7F7EDAD518C960D361CFBF6B4C24CB98207DFE775B9687E73",
            "21|Enc|C89A96CA7B163ECCCBCCB35CFB87F826F7EDAD518C960D361CFBF6B4C24CB98207DFE775B9687E73",
            "21|XorT|C89A96CA7B163ED9CBCCB35CFB87F826F7EDAD518C960D361CFBF6B4C24CB98207DFE775B9687E73",
            "22|In|C89A96CA7B163ED9CBCCB35CFB87F826F7EDAD518C960D361CFBF6B4C24CB98207DFE775B9687E73",
            "22|Enc|39D02FE7435870EDCBCCB35CFB87F8263F5786E2D80ED3261CFBF6B4C24CB98207DFE775B9687E73",
            "22|XorT|39D02FE7435870FBCBCCB35CFB87F8263F5786E2D80ED3261CFBF6B4C24CB98207DFE775B9687E73",
            "23|In|39D02FE7435870FBCBCCB35CFB87F8263F5786E2D80ED3261CFBF6B4C24CB98207DFE775B9687E73",
            "23|Enc|0AEB82AE3146A91BCBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43B07DFE775B9687E73",
            "23|XorT|0AEB82AE3146A90CCBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43B07DFE775B9687E73",
            "24|In|0AEB82AE3146A90CCBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43B07DFE775B9687E73",
            "24|Enc|28C9F404C4B810ECCBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43BFB988B9B7A02DD21",
            "24|XorT|28C9F404C4B810F4CBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43BFB988B9B7A02DD21",
        ],
        [
            "24|In|28C9F404C4B810F4CBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43BFB988B9B7A02DD21",
            "24|XorT|28C9F404C4B810ECCBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43BFB988B9B7A02DD21",
            "24|Dec|0AEB82AE3146A90CCBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43B07DFE775B9687E73",
            "23|In|0AEB82AE3146A90CCBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43B07DFE775B9687E73",
            "23|XorT|0AEB82AE3146A91BCBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43B07DFE775B9687E73",
            "23|Dec|39D02FE7435870FBCBCCB35CFB87F8263F5786E2D80ED3261CFBF6B4C24CB98207DFE775B9687E73",
            "22|In|39D02FE7435870FBCBCCB35CFB87F8263F5786E2D80ED3261CFBF6B4C24CB98207DFE775B9687E73",
            "22|XorT|39D02FE7435870EDCBCCB35CFB87F8263F5786E2D80ED3261CFBF6B4C24CB98207DFE775B9687E73",
            "22|Dec|C89A96CA7B163ED9CBCCB35CFB87F826F7EDAD518C960D361CFBF6B4C24CB98207DFE775B9687E73",
            "21|In|C89A96CA7B163ED9CBCCB35CFB87F826F7EDAD518C960D361CFBF6B4C24CB98207DFE775B9687E73",
            "21|XorT|C89A96CA7B163ECCCBCCB35CFB87F826F7EDAD518C960D361CFBF6B4C24CB98207DFE775B9687E73",
            "21|Dec|D058823360F88A231BB8C765A84195E7F7EDAD518C960D361CFBF6B4C24CB98207DFE775B9687E73",
            "20|In|D058823360F88A231BB8C765A84195E7F7EDAD518C960D361CFBF6B4C24CB98207DFE775B9687E73",
            "20|XorT|D058823360F88A371BB8C765A84195E7F7EDAD518C960D361CFBF6B4C24CB98207DFE775B9687E73",
            "20|Dec|B422B444B87A19181BB8C765A84195E7F7EDAD518C960D361CFBF6B4C24CB98240F68C91DB49702C",
            "19|In|B422B444B87A19181BB8C765A84195E7F7EDAD518C960D361CFBF6B4C24CB98240F68C91DB49702C",
            "19|XorT|B422B444B87A190B1BB8C765A84195E7F7EDAD518C960D361CFBF6B4C24CB98240F68C91DB49702C",
            "19|Dec|F19D80D437EFE8EB1BB8C765A84195E7F7EDAD518C960D36C272E9466AAE98F940F68C91DB49702C",
            "18|In|F19D80D437EFE8EB1BB8C765A84195E7F7EDAD518C960D36C272E9466AAE98F940F68C91DB49702C",
            "18|XorT|F19D80D437EFE8F91BB8C765A84195E7F7EDAD518C960D36C272E9466AAE98F940F68C91DB49702C",
            "18|Dec|A5382A26B47551E01BB8C765A84195E7BCA418BBF7DCE60BC272E9466AAE98F940F68C91DB49702C",
            "17|In|A5382A26B47551E01BB8C765A84195E7BCA418BBF7DCE60BC272E9466AAE98F940F68C91DB49702C",
            "17|XorT|A5382A26B47551F11BB8C765A84195E7BCA418BBF7DCE60BC272E9466AAE98F940F68C91DB49702C",
            "17|Dec|5075496800978B5A4745856AF333F01FBCA418BBF7DCE60BC272E9466AAE98F940F68C91DB49702C",
            "16|In|5075496800978B5A4745856AF333F01FBCA418BBF7DCE60BC272E9466AAE98F940F68C91DB49702C",
            "16|XorT|5075496800978B4A4745856AF333F01FBCA418BBF7DCE60BC272E9466AAE98F940F68C91DB49702C",
            "16|Dec|33FE29365885C4B84745856AF333F01FBCA418BBF7DCE60BC272E9466AAE98F93CF149E90E8C04D9",
            "15|In|33FE29365885C4B84745856AF333F01FBCA418BBF7DCE60BC272E9466AAE98F93CF149E90E8C04D9",
            "15|XorT|33FE29365885C4B74745856AF333F01FBCA418BBF7DCE60BC272E9466AAE98F93CF149E90E8C04D9",
            "15|Dec|15342443CB95ADBF4745856AF333F01FBCA418BBF7DCE60BF56701DAF03882163CF149E90E8C04D9",
            "14|In|15342443CB95ADBF4745856AF333F01FBCA418BBF7DCE60BF56701DAF03882163CF149E90E8C04D9",
            "14|XorT|15342443CB95ADB14745856AF333F01FBCA418BBF7DCE60BF56701DAF03882163CF149E90E8C04D9",
            "14|Dec|2E8E2B6BB201669B4745856AF333F01FFBEC169FA5C0F6BAF56701DAF03882163CF149E90E8C04D9",
            "13|In|2E8E2B6BB201669B4745856AF333F01FFBEC169FA5C0F6BAF56701DAF03882163CF149E90E8C04D9",
            "13|XorT|2E8E2B6BB20166964745856AF333F01FFBEC169FA5C0F6BAF56701DAF03882163CF149E90E8C04D9",
            "13|Dec|F9ED8A1429515669D6AE29ECE7192D43FBEC169FA5C0F6BAF56701DAF03882163CF149E90E8C04D9",
            "12|In|F9ED8A1429515669D6AE29ECE7192D43FBEC169FA5C0F6BAF56701DAF03882163CF149E90E8C04D9",
            "12|XorT|F9ED8A1429515665D6AE29ECE7192D43FBEC169FA5C0F6BAF56701DAF03882163CF149E90E8C04D9",
            "12|Dec|0629EB29A42E4FD2D6AE29ECE7192D43FBEC169FA5C0F6BAF56701DAF038821673E3B6CBE5D05D74",
            "11|In|0629EB29A42E4FD2D6AE29ECE7192D43FBEC169FA5C0F6BAF56701DAF038821673E3B6CBE5D05D74",
            "11|XorT|0629EB29A42E4FD9D6AE29ECE7192D43FBEC169FA5C0F6BAF56701DAF038821673E3B6CBE5D05D74",
            "11|Dec|DBA417FB51F9E3C1D6AE29ECE7192D43FBEC169FA5C0F6BAC365B66943E2D76073E3B6CBE5D05D74",
            "10|In|DBA417FB51F9E3C1D6AE29ECE7192D43FBEC169FA5C0F6BAC365B66943E2D76073E3B6CBE5D05D74",
            "10|XorT|DBA417FB51F9E3CBD6AE29ECE7192D43FBEC169FA5C0F6BAC365B66943E2D76073E3B6CBE5D05D74",
            "10|Dec|1A681354E84C41F1D6AE29ECE7192D43EFD48BA304945576C365B66943E2D76073E3B6CBE5D05D74",
            "9|In|1A681354E84C41F1D6AE29ECE7192D43EFD48BA304945576C365B66943E2D76073E3B6CBE5D05D74",
            "9|XorT|1A681354E84C41F8D6AE29ECE7192D43EFD48BA304945576C365B66943E2D76073E3B6CBE5D05D74",
            "9|Dec|C58B9D3AC6D5B946E7D1194D853E53F8EFD48BA304945576C365B66943E2D76073E3B6CBE5D05D74",
            "8|In|C58B9D3AC6D5B946E7D1194D853E53F8EFD48BA304945576C365B66943E2D76073E3B6CBE5D05D74",
            "8|XorT|C58B9D3AC6D5B94EE7D1194D853E53F8EFD48BA304945576C365B66943E2D76073E3B6CBE5D05D74",
            "8|Dec|66D7A8ADD086B9DAE7D1194D853E53F8EFD48BA304945576C365B66943E2D760E5923CB9FDB56FBC",
            "7|In|66D7A8ADD086B9DAE7D1194D853E53F8EFD48BA304945576C365B66943E2D760E5923CB9FDB56FBC",
            "7|XorT|66D7A8ADD086B9DDE7D1194D853E53F8EFD48BA304945576C365B66943E2D760E5923CB9FDB56FBC",
            "7|Dec|963AAFFD96B223EAE7D1194D853E53F8EFD48BA3049455766CA405593A3B5154E5923CB9FDB56FBC",
            "6|In|963AAFFD96B223EAE7D1194D853E53F8EFD48BA3049455766CA405593A3B5154E5923CB9FDB56FBC",
            "6|XorT|963AAFFD96B223ECE7D1194D853E53F8EFD48BA3049455766CA405593A3B5154E5923CB9FDB56FBC",
            "6|Dec|4EF02EDD3146AFBEE7D1194D853E53F8F60E0CDB7F429FE86CA405593A3B5154E5923CB9FDB56FBC",
            "5|In|4EF02EDD3146AFBEE7D1194D853E53F8F60E0CDB7F429FE86CA405593A3B5154E5923CB9FDB56FBC",
            "5|XorT|4EF02EDD3146AFBBE7D1194D853E53F8F60E0CDB7F429FE86CA405593A3B5154E5923CB9FDB56FBC",
            "5|Dec|564408FDD0DD2EA0F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B5154E5923CB9FDB56FBC",
            "4|In|564408FDD0DD2EA0F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B5154E5923CB9FDB56FBC",
            "4|XorT|564408FDD0DD2EA4F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B5154E5923CB9FDB56FBC",
            "4|Dec|9DF8F5405FBC00C2F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B515408090A0B0C0D0E0F",
            "3|In|9DF8F5405FBC00C2F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B515408090A0B0C0D0E0F",
            "3|XorT|9DF8F5405FBC00C1F661BD9F31FBFA31F60E0CDB7F429FE86CA405593A3B515408090A0B0C0D0E0F",
            "3|Dec|D450EA5C5BBCB563F661BD9F31FBFA31F60E0CDB7F429FE8000102030405060708090A0B0C0D0E0F",
            "2|In|D450EA5C5BBCB563F661BD9F31FBFA31F60E0CDB7F429FE8000102030405060708090A0B0C0D0E0F",
            "2|XorT|D450EA5C5BBCB561F661BD9F31FBFA31F60E0CDB7F429FE8000102030405060708090A0B0C0D0E0F",
            "2|Dec|794314D454E3FDE0F661BD9F31FBFA318899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F",
            "1|In|794314D454E3FDE0F661BD9F31FBFA318899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F",
            "1|XorT|794314D454E3FDE1F661BD9F31FBFA318899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F",
            "1|Dec|A6A6A6A6A6A6A6A600112233445566778899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F",
        ]);
}
