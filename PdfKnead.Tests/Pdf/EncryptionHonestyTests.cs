using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The standard security handler said honestly (docs/ABLAUFPLAN.md, 5.1; docs/FEHLERGEDAECHTNIS.md, No. 544): an unknown crypt filter method or
/// revision is reported instead of guessed, incomplete data noted, the sealed permissions (/Perms) checked with both passwords and never
/// silently replaced by an edited /P — and a copy that keeps the encryption does not carry such an edit on.
/// </summary>
public sealed class EncryptionHonestyTests
{
    private static void SetFilterMethod(PdfDictionary encrypt, string method) =>
        ((PdfDictionary)encrypt.GetDictionary(PdfName.Get("CF"))!.Get(PdfName.Get("StdCF")))!.Set(PdfName.Get("CFM"), PdfName.Get(method));

    [Theory]
    [InlineData(PdfCipher.Aes256, "Foo")]
    [InlineData(PdfCipher.Aes128, "Foo")]
    public void UnknownCryptFilterMethod_IsReported(PdfCipher cipher, string method)
    {
        var bytes = TestPdf.Simple().Build(cipher: cipher, user: "", patchEncrypt: e => SetFilterMethod(e, method));
        var error = Assert.Throws<PdfException>(() => PdfDocument.Open(bytes));
        Assert.Equal(Tr.T("Verschlüsselungsverfahren „{0}“ wird nicht unterstützt.", method), error.Message);
    }

    /// <summary>AES-GCM (AESV4) is read since 5.4 — but only under V 6 (ISO/TS 32003, 5.1): under V 4 and 5 it is reported, never guessed.</summary>
    [Theory]
    [InlineData(PdfCipher.Aes256, 5)]
    [InlineData(PdfCipher.Aes128, 4)]
    public void Aesv4BelowV6_IsReported(PdfCipher cipher, int v)
    {
        var bytes = TestPdf.Simple().Build(cipher: cipher, user: "", patchEncrypt: e => SetFilterMethod(e, "AESV4"));
        Assert.Equal(Tr.T("Verschlüsselungsverfahren „{0}“ passt nicht zu Version {1}.", "AESV4", v), Assert.Throws<PdfException>(() => PdfDocument.Open(bytes)).Message);
    }

    [Fact]
    public void Aesv3UnderV4_IsReported()
    {
        var bytes = TestPdf.Simple().Build(cipher: PdfCipher.Aes128, user: "", patchEncrypt: e => SetFilterMethod(e, "AESV3"));
        Assert.Equal(Tr.T("Verschlüsselungsverfahren „{0}“ passt nicht zu Version {1}.", "AESV3", 4), Assert.Throws<PdfException>(() => PdfDocument.Open(bytes)).Message);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void UnknownRevision_IsReported(int revision)
    {
        var bytes = TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: "", patchEncrypt: e => e.Set(PdfName.Get("R"), PdfInteger.Of(revision)));
        Assert.Equal(Tr.T("Verschlüsselung Revision {0} wird nicht unterstützt.", revision), Assert.Throws<PdfException>(() => PdfDocument.Open(bytes)).Message);
    }

    /// <summary>A crypt filter named in /StmF and /StrF but missing in /CF: the method assumed after V, with a note — the file still opens.</summary>
    [Fact]
    public void MissingCryptFilterEntry_FallsBackWithNote()
    {
        var bytes = TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: "", patchEncrypt: e =>
        {
            var cf = e.GetDictionary(PdfName.Get("CF"))!;
            cf.Set(PdfName.Get("Anders"), cf.Get(PdfName.Get("StdCF"))!);
            cf.Remove(PdfName.Get("StdCF"));
        });
        using var doc = PdfDocument.Open(bytes);
        Assert.Equal("Testdokument", doc.Info.Title);
        Assert.Contains(Tr.T("Verschlüsselungsangaben unvollständig — Verfahren angenommen."), doc.Problems);
    }

    /// <summary>/P of a file with AES-256 replaced by „everything allowed“ in the bytes (the same length, so the offsets stay).</summary>
    private static byte[] Loosened(byte[] bytes, bool removePerms = false)
    {
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var at = text.IndexOf("/P -", StringComparison.Ordinal);
        Assert.True(at > 0);
        var end = text.IndexOfAny([' ', '/', '>'], at + 4);
        text = text[..at] + ("/P -1" + new string(' ', end - at))[..(end - at)] + text[end..];
        if (removePerms)
        {
            var perms = text.IndexOf("/Perms", StringComparison.Ordinal);
            Assert.True(perms > 0);
            var close = text.IndexOf('>', perms);
            text = text[..perms] + new string(' ', close + 1 - perms) + text[(close + 1)..];
        }
        return System.Text.Encoding.Latin1.GetBytes(text);
    }

    private static byte[] PrintOnly() =>
        TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: "geheim", owner: "chef", permissions: PdfPermissions.Print);

    /// <summary>/P loosened beside a valid /Perms: the sealed rights count and it is said — with the user and with the owner password.</summary>
    [Theory]
    [InlineData("geheim")]
    [InlineData("chef")]
    public void Aes256_LoosenedP_IsReported(string password)
    {
        using var doc = PdfDocument.Open(Loosened(PrintOnly()), password);
        Assert.Equal(PermsState.DiffersFromP, doc.Security!.Perms);
        Assert.Contains(Tr.T("Die offenen Rechte (/P) weichen von den versiegelten ab; es gelten die versiegelten. Die Datei verstößt damit gegen ISO 32000-2."),
            doc.Problems);
        if (password == "geheim") Assert.Equal(PdfPermissions.Print | PdfPermissions.Accessibility, doc.Security.Permissions);
    }

    /// <summary>/Perms removed and /P loosened: /P counts (FG 432 keeps that), but no longer silently.</summary>
    [Fact]
    public void Aes256_PermsRemovedAndPLoosened_IsReported()
    {
        using var doc = PdfDocument.Open(Loosened(PrintOnly(), removePerms: true), "geheim");
        Assert.Equal(PermsState.Missing, doc.Security!.Perms);
        Assert.Contains(Tr.T("Die Rechte dieser Datei sind nicht versiegelt (/Perms fehlt oder passt nicht) — sie könnten nachträglich verändert sein."),
            doc.Problems);
    }

    /// <summary>/EncryptMetadata false patched into a file sealed with „T“: the sealed value counts (strings stay readable) and it is said.</summary>
    [Fact]
    public void Aes256_EncryptMetadataFlipped_SealedValueWins()
    {
        var bytes = TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: "", patchEncrypt: e => e.Set(PdfName.Get("EncryptMetadata"), PdfBoolean.False));
        using var doc = PdfDocument.Open(bytes);
        Assert.True(doc.Security!.EncryptMetadata);
        Assert.Equal(PermsState.EncryptMetadataDiffers, doc.Security.Perms);
        Assert.Equal("Testdokument", doc.Info.Title);
    }

    /// <summary>The raw permission bits come from /Perms where it is valid: a /P edited to clear bit 13 (a MAC required) does not count.</summary>
    [Fact]
    public void RawPermissions_FromTheSealedBits()
    {
        using var doc = PdfDocument.Open(Loosened(PrintOnly()), "geheim");
        Assert.NotEqual(0, doc.Security!.RawPermissions & 0x1000);
        Assert.Equal(doc.Security.SealedP, doc.Security.RawPermissions);
    }

    /// <summary>A copy that keeps the encryption writes the sealed rights in /P, not the loosened ones.</summary>
    [Fact]
    public void KeptEncryption_LoosenedP_WrittenSealed()
    {
        using var doc = PdfDocument.Open(Loosened(PrintOnly()), "geheim");
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions());
        using var copy = PdfDocument.Open(output.ToArray(), "geheim");
        Assert.Equal(doc.Security!.SealedP, (int)copy.EncryptDictionary!.GetInteger(PdfName.Get("P"))!);
        Assert.Equal(PermsState.Valid, copy.Security!.Perms);
    }

    // ---- 5.2: files of ISO/TS 32004 (MAC required) saved without a MAC — not unlocked (docs/FEHLERGEDAECHTNIS.md, No. 546)

    internal static byte[] MacFile(string user = "geheim") =>
        TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: user, owner: "chef", permissions: PdfPermissions.Print, version: "2.0", macRequired: true);

    private static bool DeclaresMac(PdfDocument doc) =>
        doc.Catalog!.GetDictionary(PdfName.Get("Extensions"))?.GetArray(PdfName.Get("ISO_")) is { } iso
        && Enumerable.Range(0, iso.Count).Any(i => (iso.Get(i) as PdfDictionary)?.GetInteger(PdfName.Get("ExtensionLevel")) == 32004);

    private static (byte[] Bytes, PdfSaveResult Result) Saved(byte[] file, string password, PdfSaveOptions options)
    {
        using var doc = PdfDocument.Open(file, password);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, PageItem.From(doc), output, options);
        return (output.ToArray(), result);
    }

    /// <summary>
    /// A file that asks for a MAC per revision, saved with its encryption kept (with the user and with the owner password): the copy no
    /// longer asks for one — bit 13 set in /P and /Perms alike, no /KDFSalt, no declaration 32004, no /AuthCode — the rights exactly Print
    /// (never „all“ because the owner opened it), the same password, and the result says the protection was dropped.
    /// </summary>
    [Theory]
    [InlineData("geheim")]
    [InlineData("chef")]
    public void MacRequired_SavedWithKeptEncryption_NoLongerRequiresIt(string password)
    {
        var file = MacFile();
        using (var source = PdfDocument.Open(file, "geheim"))
        {
            Assert.Equal(0, source.Security!.RawPermissions & 0x1000);
            Assert.True(DeclaresMac(source));
        }
        var (bytes, result) = Saved(file, password, new PdfSaveOptions { Version = "2.0" });
        // for the cross-check with the object model checker: the source and the copy (password „geheim“)
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder && password == "geheim")
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "mac-pflicht-quelle.pdf"), file);
            File.WriteAllBytes(Path.Combine(folder, "mac-pflicht-kopie.pdf"), bytes);
        }
        Assert.True(result.IntegrityProtectionDropped);
        using var copy = PdfDocument.Open(bytes, "geheim");
        Assert.NotEqual(0, copy.Security!.RawPermissions & 0x1000);
        Assert.Equal(PermsState.Valid, copy.Security.Perms);
        Assert.Equal(PdfPermissions.Print | PdfPermissions.Accessibility, copy.Security.Permissions);
        Assert.False(copy.EncryptDictionary!.ContainsKey(PdfName.Get("KDFSalt")));
        Assert.False(copy.Trailer.ContainsKey(PdfName.Get("AuthCode")));
        Assert.False(DeclaresMac(copy));
        Assert.Equal("Testdokument", copy.Info.Title);
    }

    /// <summary>Saved without encryption, with new protection, as PDF/A: the declaration 32004 goes, and the result says so.</summary>
    [Fact]
    public void MacRequired_WithoutEncryption_NewProtection_PdfA_DropTheDeclaration()
    {
        var (plain, plainResult) = Saved(MacFile(), "geheim", new PdfSaveOptions { Version = "2.0", KeepEncryption = false });
        Assert.True(plainResult.IntegrityProtectionDropped);
        using (var doc = PdfDocument.Open(plain))
        {
            Assert.False(doc.IsEncrypted);
            Assert.False(DeclaresMac(doc));
        }
        var (fresh, _) = Saved(MacFile(), "geheim", new PdfSaveOptions { Version = "2.0", Protection = new PdfProtection("neu-1", "neu-2", PdfPermissions.Print) });
        using (var doc = PdfDocument.Open(fresh, "neu-1"))
        {
            Assert.False(doc.EncryptDictionary!.ContainsKey(PdfName.Get("KDFSalt")));
            Assert.False(DeclaresMac(doc));
        }
        var pdfA = PdfATests.Write(MacFile(user: ""), PdfAConformance.A2b);
        using (var doc = PdfDocument.Open(pdfA.Bytes))
            Assert.False(DeclaresMac(doc));
    }

    /// <summary>„Einschränkungen entfernen“ on such a file: all rights, and no /KDFSalt left behind (the salt belongs to the MAC).</summary>
    [Fact]
    public void MacRequired_GrantAllPermissions_NoKdfSaltNoDeclaration()
    {
        var (bytes, _) = Saved(MacFile(), "chef", new PdfSaveOptions { Version = "2.0", GrantAllPermissions = true });
        using var copy = PdfDocument.Open(bytes, "geheim");
        Assert.False(copy.EncryptDictionary!.ContainsKey(PdfName.Get("KDFSalt")));
        Assert.False(DeclaresMac(copy));
        Assert.NotEqual(0, copy.Security!.RawPermissions & 0x1000);
    }

    /// <summary>
    /// A file asking for a MAC whose attachments alone are encrypted, opened without their password: the handler has no file key, so
    /// nothing can be sealed anew — the copy keeps /Encrypt as it is instead of throwing (found by the fuzz run closing phase 5;
    /// docs/FEHLERGEDAECHTNIS.md, No. 560).
    /// </summary>
    [Fact]
    public void MacRequired_AttachmentsOnlyLocked_SavedWithoutThrowing()
    {
        var file = TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: "Anhang", owner: "chef", version: "2.0", macRequired: true, attachmentsOnly: true);
        using var doc = PdfDocument.Open(file);
        Assert.True(doc.Security!.AttachmentsLocked);
        var (bytes, result) = Saved(file, "", new PdfSaveOptions { Version = "2.0" });
        Assert.True(result.IntegrityProtectionDropped);
        using var copy = PdfDocument.Open(bytes);
        Assert.Equal("Testdokument", copy.Info.Title);
    }
}
