using System.Security.Cryptography;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// AES-GCM (ISO/TS 32003): V 6, R 7, crypt filter AESV4 — read, and written where the encryption is kept, which is the default
/// (docs/ABLAUFPLAN.md, 5.4; docs/FEHLERGEDAECHTNIS.md, No. 555). The files are made here; there are none in the collections.
/// </summary>
public sealed class AesGcmTests
{
    private static byte[] GcmFile(string user = "geheim", Action<PdfDictionary>? patchEncrypt = null, bool declared = false)
    {
        var pdf = TestPdf.Simple(pages: 2);
        if (declared)
            ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("Extensions"), TestPdf.Dict(("ISO_", new PdfArray([TestPdf.Dict(
                ("Type", TestPdf.N("DeveloperExtensions")), ("BaseVersion", TestPdf.N("2.0")), ("ExtensionLevel", PdfInteger.Of(32003)),
                ("ExtensionRevision", new PdfString(":2023"u8.ToArray())), ("URL", new PdfString("https://www.iso.org/standard/45876.html"u8.ToArray())))]))));
        return pdf.Build(cipher: PdfCipher.Aes256Gcm, user: user, owner: "chef", version: "2.0", patchEncrypt: patchEncrypt);
    }

    private static string TextOf(PdfDocument doc, int page) =>
        string.Concat(PageInterpreter.Build(doc, doc.Pages[page]).Text.Select(t => t.Text));

    private static bool DeclaresGcm(PdfDocument doc) =>
        doc.Catalog!.GetDictionary(PdfName.Get("Extensions"))?.GetArray(PdfName.Get("ISO_")) is { } iso
        && Enumerable.Range(0, iso.Count).Any(i => (iso.Get(i) as PdfDictionary)?.GetInteger(PdfName.Get("ExtensionLevel")) == 32003);

    [Fact]
    public void AesGcm_IsSupportedHere() => Assert.True(AesGcm.IsSupported);

    /// <summary>
    /// A published GCM test vector (256-bit key, 96-bit IV, no associated data; checked beforehand with AES-CTR of an independent library and
    /// a GHASH of our own) packed as the standard lays it out — nonce ‖ ciphertext ‖ tag — decrypts to its plaintext; what the handler writes
    /// has the same layout.
    /// </summary>
    [Fact]
    public void NistVector_DecryptsInTheLayoutOfTheStandard()
    {
        var key = Convert.FromHexString("feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308");
        var plain = Convert.FromHexString("d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b391aafd255");
        var stored = Convert.FromHexString("cafebabefacedbaddecaf888"
            + "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662898015ad"
            + "b094dac5d93471bdec1a502270e3cc6c");
        var handler = StandardSecurityHandler.WithKey(key, PdfCipher.Aes256Gcm);
        var id = new PdfObjectId(7, 0);
        Assert.Equal(plain, handler.DecryptStream(id, stored));
        var written = handler.EncryptStream(id, plain);
        Assert.Equal(12 + plain.Length + 16, written.Length);
        using var gcm = new AesGcm(key, 16);
        var back = new byte[plain.Length];
        gcm.Decrypt(written.AsSpan(0, 12), written.AsSpan(12, plain.Length), written.AsSpan(12 + plain.Length), back);
        Assert.Equal(plain, back);
    }

    /// <summary>A GCM file opens with the user and with the owner password; a wrong one is said. Before: „Version 6 wird nicht unterstützt“.</summary>
    [Fact]
    public void Aes256Gcm_UserAndOwnerPasswords()
    {
        var file = GcmFile();
        using (var user = PdfDocument.Open(file, "geheim"))
        {
            Assert.Equal(PdfCipher.Aes256Gcm, user.Security!.StreamCipher);
            Assert.Equal(7, user.Security.Revision);
            Assert.False(user.Security.IsOwner);
            Assert.Equal("Testdokument", user.Info.Title);
            Assert.Contains("Seite 2", TextOf(user, 1));
            Assert.Empty(user.Problems);
        }
        using (var owner = PdfDocument.Open(file, "chef")) Assert.True(owner.Security!.IsOwner);
        Assert.True(Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(file, "falsch")).WrongPassword);
    }

    /// <summary>One byte of a content stream changed after encrypting: the tag no longer matches — the page stays empty and that is said, never garbage.</summary>
    [Fact]
    public void Aes256Gcm_TamperedStream_IsReported()
    {
        var file = GcmFile();
        var text = System.Text.Encoding.Latin1.GetString(file);
        var content = text.IndexOf("stream\n", text.IndexOf("\n4 0 obj", StringComparison.Ordinal), StringComparison.Ordinal) + 7;
        file[content + 20] ^= 0x01;
        using var doc = PdfDocument.Open(file, "geheim");
        Assert.DoesNotContain("Seite 1", TextOf(doc, 0));
        Assert.Contains("Seite 2", TextOf(doc, 1));
        Assert.Contains(doc.Problems, p => p.Contains("nach dem Verschlüsseln verändert", StringComparison.Ordinal));
    }

    /// <summary>Data shorter than nonce and tag: empty, said, no exception.</summary>
    [Fact]
    public void Aes256Gcm_ShortString_NoCrash()
    {
        var handler = StandardSecurityHandler.WithKey(RandomNumberGenerator.GetBytes(32), PdfCipher.Aes256Gcm);
        var failed = 0;
        handler.AuthenticationFailed += () => failed++;
        Assert.Empty(handler.DecryptString(new PdfObjectId(3, 0), new byte[10]));
        Assert.Equal(1, failed);
    }

    /// <summary>
    /// Saved with the encryption kept (the default): AES-GCM stays (/CFM /AESV4), the file is PDF 2.0, declares ISO/TS 32003 and reads the
    /// same. Removing the protection drops the declaration.
    /// </summary>
    [Fact]
    public void Aes256Gcm_KeptEncryption_RoundTrip_ProtectionRemoved_DeclarationDropped()
    {
        byte[] kept, plain;
        using (var doc = PdfDocument.Open(GcmFile(declared: true), "geheim"))
        {
            using var output = new MemoryStream();
            var result = PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "1.7" });
            Assert.Equal("2.0", result.Version);
            kept = output.ToArray();
            using var bare = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), bare, new PdfSaveOptions { Version = "2.0", KeepEncryption = false });
            plain = bare.ToArray();
        }
        // for the cross-check in other readers (password „geheim“)
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "aes-gcm-quelle.pdf"), GcmFile(declared: true));
            File.WriteAllBytes(Path.Combine(folder, "aes-gcm-behalten.pdf"), kept);
            // without an open password: a start file for the fuzzer
            File.WriteAllBytes(Path.Combine(folder, "aes-gcm-ohne-passwort.pdf"), GcmFile(user: "", declared: true));
        }
        using (var copy = PdfDocument.Open(kept, "geheim"))
        {
            Assert.Equal(PdfCipher.Aes256Gcm, copy.Security!.StreamCipher);
            Assert.Equal("2.0", copy.Version);
            Assert.Equal("AESV4", copy.EncryptDictionary!.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!.GetName(PdfName.Get("CFM"))?.Value);
            Assert.True(DeclaresGcm(copy));
            Assert.Contains("Seite 2", TextOf(copy, 1));
            Assert.Empty(copy.Problems);
        }
        using var open = PdfDocument.Open(plain);
        Assert.Null(open.Security);
        Assert.False(DeclaresGcm(open));
        Assert.Contains("Seite 2", TextOf(open, 1));
    }

    /// <summary>
    /// V 6 allows other filters beside AES-GCM (ISO/TS 32003, 5.1): streams with AESV3, strings with AESV4 — each by its filter; V 6
    /// without any AESV4 filter is reported.
    /// </summary>
    [Fact]
    public void Aes256Gcm_MixedFiltersUnderV6()
    {
        var (encrypt, _) = StandardSecurityHandler.Create(PdfCipher.Aes256Gcm, "geheim", "chef", PdfPermissions.Print, new byte[16]);
        var cf = encrypt.GetDictionary(PdfName.Get("CF"))!;
        cf.Set(PdfName.Get("Alt"), TestPdf.Dict(("Type", TestPdf.N("CryptFilter")), ("CFM", TestPdf.N("AESV3")), ("Length", PdfInteger.Of(32))));
        encrypt.Set(PdfName.Get("StmF"), TestPdf.N("Alt"));
        var mixed = StandardSecurityHandler.Open(encrypt, null, "geheim");
        Assert.Equal(PdfCipher.Aes256, mixed.StreamCipher);
        Assert.Equal(PdfCipher.Aes256Gcm, mixed.StringCipher);

        cf.Remove(PdfName.Get("StdCF"));
        encrypt.Set(PdfName.Get("StrF"), TestPdf.N("Alt"));
        Assert.Throws<PdfException>(() => StandardSecurityHandler.Open(encrypt, null, "geheim"));
    }
}
