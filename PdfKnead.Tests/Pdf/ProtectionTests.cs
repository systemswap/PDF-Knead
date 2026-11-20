using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Setting, changing and removing password and permissions on saving.</summary>
public sealed class ProtectionTests
{
    private static byte[] Write(PdfDocument doc, PdfSaveOptions options, IReadOnlyList<PageItem>? pages = null)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, pages ?? PageItem.From(doc), output, options);
        // Store for checking with other programs (local only)
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder && options.Protection is { } p)
            File.WriteAllBytes(Path.Combine(folder, $"schutz-{options.Version}-{(p.UserPassword.Length > 0 ? "passwort" : "rechte")}.pdf"), output.ToArray());
        return output.ToArray();
    }

    private static string TextOf(PdfDocument doc, int page) =>
        string.Concat(PageInterpreter.Build(doc, doc.Pages[page]).Text.Select(t => t.Text));

    [Theory]
    [InlineData("1.4", PdfCipher.Rc4)]
    [InlineData("1.6", PdfCipher.Aes128)]
    [InlineData("1.7", PdfCipher.Aes256)]
    [InlineData("2.0", PdfCipher.Aes256)]
    public void OpenPassword_AndPermissions_PerVersion(string version, PdfCipher cipher)
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var rights = PdfPermissions.Print | PdfPermissions.Accessibility;
        var bytes = Write(original, new PdfSaveOptions { Version = version, Protection = new PdfProtection("geheim", "Besitzer-1", rights) });

        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes));
        Assert.True(Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes, "falsch")).WrongPassword);
        using (var user = PdfDocument.Open(bytes, "geheim"))
        {
            Assert.Equal(cipher, user.Security!.StreamCipher);
            Assert.False(user.Security.IsOwner);
            Assert.Equal(rights, user.Security.Permissions & PdfPermissions.All);
            Assert.Contains("Seite 2", TextOf(user, 1));   // content decrypted correctly
            Assert.Equal(version == "1.6" ? "1.6" : version, user.Version);
            if (version == "1.7")   // AES-256 in 1.7: Adobe extension level 8
                Assert.Equal(8, user.Catalog!.GetDictionary(PdfName.Get("Extensions"))!.GetDictionary(PdfName.Get("ADBE"))!.GetInteger(PdfName.Get("ExtensionLevel")));
        }
        using (var owner = PdfDocument.Open(bytes, "Besitzer-1"))
            Assert.True(owner.Security!.IsOwner);
    }

    /// <summary>Variants for narrowing down problems with other programs (only with PDFKNEAD_ABLAGE): with and without object streams.</summary>
    [Fact]
    public void Variants_ForOtherViewers()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is not { Length: > 0 } folder) return;
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var protection = new PdfProtection("geheim", "Besitzer-1", PdfPermissions.Print | PdfPermissions.Accessibility);
        foreach (var (version, objectStreams, name) in (ReadOnlySpan<(string, bool, string)>)
                 [("1.5", true, "rc4-mit-objektstreams"), ("1.6", false, "aes128-ohne-objektstreams"), ("1.7", false, "aes256-ohne-objektstreams")])
        {
            using var output = new MemoryStream();
            PdfWriter.Write(original, PageItem.From(original), output, new PdfSaveOptions { Version = version, ObjectStreams = objectStreams, Protection = protection });
            File.WriteAllBytes(Path.Combine(folder, $"variante-{version}-{name}.pdf"), output.ToArray());
            using var check = PdfDocument.Open(output.ToArray(), "geheim");
            Assert.Contains("Seite 1", TextOf(check, 0));
        }
    }

    [Fact]
    public void OnlyPermissions_OpenWithoutPassword()
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var bytes = Write(original, new PdfSaveOptions { Protection = new PdfProtection("", "Besitzer-1", PdfPermissions.Print) });
        using var doc = PdfDocument.Open(bytes);
        Assert.False(doc.Security!.IsOwner);
        Assert.Equal(PdfPermissions.Print | PdfPermissions.Accessibility, doc.Security.Permissions & PdfPermissions.All);
        Assert.Contains("Seite 1", TextOf(doc, 0));
    }

    [Fact]
    public void Protection_IsReplaced_OrRemoved()
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var first = Write(original, new PdfSaveOptions { Protection = new PdfProtection("alt", "alt-besitzer", PdfPermissions.All) });
        using var encrypted = PdfDocument.Open(first, "alt-besitzer");

        // New password: the old one no longer works
        var changed = Write(encrypted, new PdfSaveOptions { Protection = new PdfProtection("neu", "neu-besitzer", PdfPermissions.Copy) });
        Assert.True(Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(changed, "alt")).WrongPassword);
        using (var reopened = PdfDocument.Open(changed, "neu"))
            Assert.Equal(PdfPermissions.Copy | PdfPermissions.Accessibility, reopened.Security!.Permissions & PdfPermissions.All);

        // Remove the protection: without /Encrypt, content readable
        var plain = Write(encrypted, new PdfSaveOptions { KeepEncryption = false });
        using var open = PdfDocument.Open(plain);
        Assert.Null(open.Security);
        Assert.Null(open.Trailer.Get(PdfName.Encrypt).AsDictionary());
        Assert.Contains("Seite 3", TextOf(open, 2));
    }

    /// <summary>Removing restrictions: both passwords stay and the permissions are full, whether opened as user or owner. See KONZEPT.md, section 5.11.</summary>
    [Theory]
    [InlineData("1.4", false)]
    [InlineData("1.4", true)]
    [InlineData("1.6", false)]
    [InlineData("1.6", true)]
    [InlineData("1.7", false)]
    [InlineData("1.7", true)]
    public void GrantAllPermissions_KeepsBothPasswords(string version, bool openedAsOwner)
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var restricted = Write(original, new PdfSaveOptions { Version = version, Protection = new PdfProtection("geheim", "Besitzer-1", PdfPermissions.Print) });
        using var opened = PdfDocument.Open(restricted, openedAsOwner ? "Besitzer-1" : "geheim");
        Assert.Equal(openedAsOwner, opened.Security!.IsOwner);
        Assert.True(opened.Security.CanGrantAllPermissions);

        var lifted = Write(opened, new PdfSaveOptions { Version = version, GrantAllPermissions = true });
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(lifted));
        using (var user = PdfDocument.Open(lifted, "geheim"))
        {
            Assert.False(user.Security!.IsOwner);
            Assert.Equal(PdfPermissions.All, user.Security.Permissions & PdfPermissions.All);
            Assert.Equal(PdfProtection.CipherFor(version), user.Security.StreamCipher);
            Assert.Contains("Seite 2", TextOf(user, 1));
        }
        using (var owner = PdfDocument.Open(lifted, "Besitzer-1"))
            Assert.True(owner.Security!.IsOwner);

        // Cross-check: without the option the restrictions stay
        using var kept = PdfDocument.Open(Write(opened, new PdfSaveOptions { Version = version }), "geheim");
        Assert.Equal(PdfPermissions.Print | PdfPermissions.Accessibility, kept.Security!.Permissions & PdfPermissions.All);
    }

    [Theory]
    [InlineData("1.4")]
    [InlineData("1.7")]
    public void OpenPasswordAndOwnerPassword_AreRecognised(string version)
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        using var withOpen = PdfDocument.Open(
            Write(original, new PdfSaveOptions { Version = version, Protection = new PdfProtection("geheim", "Besitzer-1", PdfPermissions.None) }), "geheim");
        using var rightsOnly = PdfDocument.Open(
            Write(original, new PdfSaveOptions { Version = version, Protection = new PdfProtection("", "Besitzer-1", PdfPermissions.None) }));
        var id = (PdfArray?)withOpen.Trailer.Get(PdfName.ID);
        Assert.False(StandardSecurityHandler.OpensWithoutPassword(withOpen.EncryptDictionary!, id));
        Assert.True(StandardSecurityHandler.OpensWithoutPassword(rightsOnly.EncryptDictionary!, (PdfArray?)rightsOnly.Trailer.Get(PdfName.ID)));
        Assert.True(StandardSecurityHandler.IsOwnerPassword(withOpen.EncryptDictionary!, id, "Besitzer-1"));
        Assert.False(StandardSecurityHandler.IsOwnerPassword(withOpen.EncryptDictionary!, id, "geheim"));
        Assert.False(StandardSecurityHandler.IsOwnerPassword(withOpen.EncryptDictionary!, id, "falsch"));
    }

    [Fact]
    public void KeptEncryption_UsesTheIdOfTheKey_NotADamagedTrailerId()
    {
        // Found by fuzzing: after repair the trailer may hold a damaged /ID; a copy that keeps the RC4 encryption must not take it,
        // otherwise it asks for a password afterwards
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        using var protectedDoc = PdfDocument.Open(Write(original, new PdfSaveOptions { Version = "1.4", Protection = new PdfProtection("", "Besitzer-1", PdfPermissions.Print) }));
        protectedDoc.Trailer.Set(PdfName.ID, new PdfArray { new PdfString([1, 2, 3, 4, 5, 6, 7, 8, 9]), new PdfString([9, 9]) });
        using var copy = PdfDocument.Open(Write(protectedDoc, new PdfSaveOptions { Version = "1.4" }));
        Assert.NotNull(copy.Security);
        Assert.Contains("Seite", TextOf(copy, 0));
    }

    /// <summary>
    /// Crypt filters as an object of their own (<c>/CF 7 0 R</c>): the kept encryption must carry them, otherwise other readers cannot tell
    /// the cipher and read nothing (docs/FEHLERGEDAECHTNIS.md, No. 242).
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Aes128, "1.6")]
    [InlineData(PdfCipher.Aes256, "1.7")]
    public void KeptEncryption_CryptFiltersOfTheirOwn_AreWrittenIn(PdfCipher cipher, string version)
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build(cipher: cipher, version: version, cryptFiltersApart: true));
        Assert.IsType<PdfReference>(original.EncryptDictionary!.GetRaw(PdfName.Get("CF")));
        using var copy = PdfDocument.Open(Write(original, new PdfSaveOptions { Version = version }));
        var filters = Assert.IsType<PdfDictionary>(copy.EncryptDictionary!.GetRaw(PdfName.Get("CF")));
        var standard = Assert.IsType<PdfDictionary>(filters.GetRaw(PdfName.Get("StdCF")));
        Assert.Equal(cipher == PdfCipher.Aes256 ? "AESV3" : "AESV2", standard.GetName(PdfName.Get("CFM"))?.Value);
        Assert.Contains("Seite", TextOf(copy, 0));
    }

    [Fact]
    public void ElementsAndFonts_AreEncryptedToo()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var text = new TextElement { Rect = new PdfRectangle(100, 600, 400, 640), Text = "Max Mustermann" };
        var bytes = Write(original, new PdfSaveOptions { Protection = new PdfProtection("geheim", "", PdfPermissions.All) },
            [PageItem.From(original)[0].WithElements([text])]);
        Assert.DoesNotContain("Mustermann", System.Text.Encoding.Latin1.GetString(bytes));   // nothing in plain text
        using var doc = PdfDocument.Open(bytes, "geheim");
        Assert.Equal("Max Mustermann", ((TextElement)Assert.Single(PageItem.From(doc)[0].Elements)).Text);
        Assert.True(doc.Security!.IsOwner);   // without an own owner password, the open password counts for both
    }

    /// <summary>
    /// Text for assistive technology (bit 10) is always granted: written into /P (and with AES-256 into the sealed /Perms) even when only
    /// printing was chosen — readers ignore the bit (ISO 32000-2, 7.6.4.2, Table 22; ISO 14289-1, 7.16; docs/FEHLERGEDAECHTNIS.md, No. 490).
    /// </summary>
    [Theory]
    [InlineData("1.6")]
    [InlineData("1.7")]
    public void Accessibility_IsAlwaysGranted(string version)
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var bytes = Write(original, new PdfSaveOptions { Version = version, Protection = new PdfProtection("geheim", "Besitzer-1", PdfPermissions.Print) });
        using var user = PdfDocument.Open(bytes, "geheim");
        Assert.NotEqual(0L, user.EncryptDictionary!.GetInteger(PdfName.Get("P"))!.Value & 0x200);
        Assert.Equal(PdfPermissions.Print | PdfPermissions.Accessibility, user.Security!.Permissions & PdfPermissions.All);
    }

    /// <summary>A file whose /P clears bit 10: the bit counts as granted, a file that otherwise allows everything is not restricted.</summary>
    [Fact]
    public void Accessibility_BitClearedInTheFile_CountsAsGranted()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple().Build(cipher: PdfCipher.Aes128, permissions: PdfPermissions.All & ~PdfPermissions.Accessibility));
        Assert.Equal(PdfPermissions.All, doc.Security!.Permissions & PdfPermissions.All);
    }

    /// <summary>
    /// A file whose attachments have a password of their own, opened without it: the key for them is unknown (empty). Encrypting with it is
    /// refused with a message, as decrypting is — found by fuzzing a damaged copy of a public test file, where the writer reached the
    /// encryption with the empty key and the runtime threw (docs/FEHLERGEDAECHTNIS.md, No. 521).
    /// </summary>
    [TestPdfsFact]
    public void AttachmentKeyUnknown_EncryptingIsRefusedWithAMessage()
    {
        static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
        using var doc = PdfDocument.Open(Path.Combine(Root(), "TestPDFs", "pdfjs", "encrypted-attachment.pdf"));
        var security = doc.Security!;
        Assert.True(security.AttachmentsLocked);
        var refused = Assert.Throws<PdfException>(() => security.EncryptStream(new PdfObjectId(5, 0), [1, 2, 3], PdfCipher.Aes256));
        Assert.Equal(Tr.T("Verschlüsselungsdaten unvollständig."), refused.Message);
    }

    // ---- 5.3 c: /EncryptMetadata false leaves only the document's metadata plain; no /Length for V 5 in 2.0 (docs/FEHLERGEDAECHTNIS.md, No. 551)

    private static PdfStream Xmp(string text) => new(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))),
        System.Text.Encoding.UTF8.GetBytes($"<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">{text}</x:xmpmeta><?xpacket end=\"w\"?>"));

    /// <summary>A page with metadata of its own („Max Mustermann“) and the document's („Katalog“), encrypted with /EncryptMetadata false.</summary>
    private static byte[] WithPageXmp(PdfCipher cipher, bool allMetadataPlain = false)
    {
        var pdf = TestPdf.Simple(pages: 1);
        const int page = 5;
        ((PdfDictionary)pdf.Get(page)).Set(PdfName.Metadata, TestPdf.Ref(pdf.Add(Xmp("Seite von Max Mustermann"))));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Metadata, TestPdf.Ref(pdf.Add(Xmp("Katalog"))));
        return pdf.Build(cipher: cipher, user: "geheim", encryptMetadata: false, allMetadataPlain: allMetadataPlain);
    }

    private static string XmpText(PdfDocument doc, PdfDictionary holder) =>
        System.Text.Encoding.UTF8.GetString(doc.GetStreamData((PdfStream)holder.Get(PdfName.Metadata)));

    private static bool Contains(byte[] file, string text) => System.Text.Encoding.Latin1.GetString(file).Contains(text, StringComparison.Ordinal);

    /// <summary>
    /// /EncryptMetadata false concerns only the document's metadata stream (ISO 32000-2, Table 20): read, a page's metadata is decrypted as
    /// any stream; saved with the encryption kept, it is encrypted, the document's stays plain. Before, every metadata stream was taken as
    /// plain — read as garbage, and written in plain text into an „encrypted“ file.
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Aes128)]
    [InlineData(PdfCipher.Aes256)]
    public void KeptEncryption_EncryptMetadataFalse_OnlyTheDocumentXmpStaysPlain(PdfCipher cipher)
    {
        var file = WithPageXmp(cipher);
        Assert.True(Contains(file, "Katalog"));
        Assert.False(Contains(file, "Max Mustermann"));
        byte[] saved;
        using (var doc = PdfDocument.Open(file, "geheim"))
        {
            Assert.False(doc.Security!.EncryptMetadata);
            Assert.Contains("Max Mustermann", XmpText(doc, doc.Pages[0].Dictionary));
            Assert.Contains("Katalog", XmpText(doc, doc.Catalog!));
            saved = Write(doc, new PdfSaveOptions { Version = doc.Version });
        }
        Assert.True(Contains(saved, "Katalog"));
        Assert.False(Contains(saved, "Max Mustermann"));
        using var copy = PdfDocument.Open(saved, "geheim");
        Assert.False(copy.Security!.EncryptMetadata);
        Assert.Contains("Max Mustermann", XmpText(copy, copy.Pages[0].Dictionary));
        Assert.Contains("Katalog", XmpText(copy, copy.Catalog!));
    }

    /// <summary>Appended (as when signing): a page's metadata stream in the update is encrypted, the document's stays plain.</summary>
    [Fact]
    public void Appended_EncryptMetadataFalse_PageXmpEncrypted()
    {
        var file = WithPageXmp(PdfCipher.Aes256);
        byte[] updated;
        using (var doc = PdfDocument.Open(file, "geheim"))
        {
            var update = new IncrementalUpdate(doc);
            var pageXmp = (PdfReference)doc.Pages[0].Dictionary.GetRaw(PdfName.Metadata)!;
            var documentXmp = (PdfReference)doc.Catalog!.GetRaw(PdfName.Metadata)!;
            update.Replace(pageXmp.Id, Xmp("Neu: Erika Mustermann"));
            update.Replace(documentXmp.Id, Xmp("Neuer Katalog"));
            using var output = new MemoryStream();
            update.WriteTo(output);
            updated = output.ToArray();
        }
        var added = updated[file.Length..];
        Assert.True(Contains(added, "Neuer Katalog"));
        Assert.False(Contains(added, "Erika Mustermann"));
        using var copy = PdfDocument.Open(updated, "geheim");
        Assert.Contains("Erika Mustermann", XmpText(copy, copy.Pages[0].Dictionary));
        Assert.Contains("Neuer Katalog", XmpText(copy, copy.Catalog!));
    }

    /// <summary>A writer that leaves every metadata stream plain (against the standard): read as stored, with a note.</summary>
    [Fact]
    public void EncryptMetadataFalse_PlainPageXmp_ReadAsStored()
    {
        using var doc = PdfDocument.Open(WithPageXmp(PdfCipher.Aes256, allMetadataPlain: true), "geheim");
        Assert.Contains("Max Mustermann", XmpText(doc, doc.Pages[0].Dictionary));
        Assert.Contains(doc.Problems, p => p.Contains("unverschlüsselt gespeichert", StringComparison.Ordinal));
    }

    /// <summary>New protection: /Length belongs to V 2 and 3 — written for AES-256 in 1.7, not in 2.0, where it is deprecated (Table 20).</summary>
    [Theory]
    [InlineData("1.7", true)]
    [InlineData("2.0", false)]
    public void Aes256_EncryptLengthOnlyBefore20(string version, bool length)
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var bytes = Write(original, new PdfSaveOptions { Version = version, Protection = new PdfProtection("geheim", "Besitzer-1", PdfPermissions.Print) });
        using var copy = PdfDocument.Open(bytes, "geheim");
        Assert.Equal(PdfCipher.Aes256, copy.Security!.StreamCipher);
        Assert.Equal(length, copy.EncryptDictionary!.ContainsKey(PdfName.Length));
    }

    // ---- 5.6: SASLprep with its prohibitions when a password is set for AES-256 (docs/FEHLERGEDAECHTNIS.md, No. 558)

    private static string? Refusal(string password)
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        try
        {
            Write(original, new PdfSaveOptions { Version = "1.7", Protection = new PdfProtection(password, "Besitzer-1", PdfPermissions.Print) });
            return null;
        }
        catch (PdfException e)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// A control character, a private-use character, mixed writing directions: a conforming reader refuses such a password (ISO 32000-2,
    /// algorithm 2.A), the file would open only in PDF-Knead — refused when setting it, with the reason. Before: accepted.
    /// </summary>
    [Theory]
    [InlineData("Max\u0007Mustermann", "Steuerzeichen")]
    [InlineData("\uE000Mustermann", "privaten Bereich")]
    [InlineData("\u05E9\u05DC\u05D5\u05DDAbc", "Schreibrichtungen")]
    [InlineData("Abc\u202EAbc", "nicht erlaubt")]
    public void Aes256_ForbiddenPassword_IsRefusedWhenSetting(string password, string reason) =>
        Assert.Contains(reason, Refusal(password) ?? "(angenommen)", StringComparison.Ordinal);

    /// <summary>
    /// What SASLprep allows stays allowed: umlauts, a non-breaking space (mapped to a space), Hebrew alone — and the file opens with it (and,
    /// the non-breaking space mapped, with a plain space).
    /// </summary>
    [Theory]
    [InlineData("Grüße mit\u00A0Leerzeichen", "Grüße mit Leerzeichen")]
    [InlineData("\u05E9\u05DC\u05D5\u05DD", "\u05E9\u05DC\u05D5\u05DD")]
    public void Aes256_AllowedPassword_SetAndOpens(string password, string alsoOpensWith)
    {
        Assert.Null(StandardSecurityHandler.SaslRefusal(password));
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var bytes = Write(original, new PdfSaveOptions { Version = "1.7", Protection = new PdfProtection(password, "Besitzer-1", PdfPermissions.Print) });
        using (PdfDocument.Open(bytes, password)) { }
        using (PdfDocument.Open(bytes, alsoOpensWith)) { }
    }

    /// <summary>Opening stays tolerant: a lone surrogate as password is a wrong password, not an exception of the runtime.</summary>
    [Fact]
    public void LoneSurrogate_OpeningDoesNotThrow()
    {
        var file = TestPdf.Simple(1).Build(cipher: PdfCipher.Aes256, user: "geheim");
        Assert.True(Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(file, "\uD800")).WrongPassword);
        Assert.NotNull(StandardSecurityHandler.SaslRefusal("ab\uD800"));
    }

    /// <summary>The mapping as before: the AES-256 test file of the public collection opens with its password (ª and a soft hyphen in it) and its NFKC form.</summary>
    [TestPdfsFact]
    public void SaslPrep_Mapping_AsBefore()
    {
        var file = File.ReadAllBytes(TestPdfs.File("pdfjs", "saslprep-r6.pdf"));
        using (PdfDocument.Open(file, "S\u00AASL\u00ADprep")) { }
        using (PdfDocument.Open(file, "SaSLprep")) { }
    }
}
