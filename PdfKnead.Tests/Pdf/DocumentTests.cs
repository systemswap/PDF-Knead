using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;

namespace PdfKnead.Tests.Pdf;

public sealed class DocumentTests
{
    private static void AssertSimple(PdfDocument doc, int pages = 3, bool withInfo = true)
    {
        Assert.Equal(pages, doc.PageCount);
        if (withInfo) Assert.Equal("Testdokument", doc.Info.Title);
        for (var i = 0; i < pages; i++)
        {
            var page = doc.Pages[i];
            var content = doc.GetStreamData(page.Dictionary.GetStream(PdfName.Contents)!);
            Assert.Equal(TestPdf.PageContent(i + 1), content);
            Assert.NotNull(page.Resources?.GetDictionary(PdfName.Get("Font")));   // inherited from the /Pages node
        }
    }

    [Theory]
    [InlineData(XRefStyle.Table)]
    [InlineData(XRefStyle.Stream)]
    public void Reads_TableAndStreamXRef(XRefStyle style)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple().Build(style));
        AssertSimple(doc);
        Assert.False(doc.WasRepaired);
        Assert.Empty(doc.Problems);
    }

    [Fact]
    public void Pages_InheritAttributes_AndKnowTheirFormat()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple().Build());
        var a4 = doc.Pages[0];
        Assert.Equal(("A4", false), PageFormat.Detect(a4.Width, a4.Height));

        var letter = doc.Pages[1];   // 792 × 612, rotated by 90° → displayed in portrait
        Assert.Equal(90, letter.Rotate);
        Assert.Equal(612, letter.Width, 1);
        Assert.Equal(("Letter", false), PageFormat.Detect(letter.Width, letter.Height));
    }

    [Fact]
    public void IncrementalUpdate_NewerObjectsWin()
    {
        var pdf = TestPdf.Simple();
        var original = pdf.Build();
        var newInfo = TestPdf.Dict(("Title", new PdfString("Neuer Titel"u8.ToArray())));
        var updated = TestPdf.AppendUpdate(original, pdf.Count + 1, pdf.RootNumber, pdf.InfoNumber, (pdf.InfoNumber!.Value, newInfo));

        using var doc = PdfDocument.Open(updated);
        Assert.Equal("Neuer Titel", doc.Info.Title);
        Assert.Equal(3, doc.PageCount);
        Assert.False(doc.WasRepaired);
    }

    [Fact]
    public void GarbageBeforeHeader_IsTolerated()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple().Build(prefix: Encoding.ASCII.GetBytes("Content-Type: application/pdf\r\n\r\n")));
        AssertSimple(doc);
        Assert.Contains(doc.Problems, p => p.Contains("Bytes"));
    }

    [Fact]
    public void MissingHeader_IsTolerated_WhenItLooksLikeAPdf()
    {
        var text = Encoding.Latin1.GetString(TestPdf.Simple().Build());
        using var doc = PdfDocument.Open(Encoding.Latin1.GetBytes(text.Replace("%PDF-1.7\n", "")));
        AssertSimple(doc);
        Assert.Equal("1.4", doc.HeaderVersion);
        Assert.NotEmpty(doc.Problems);
    }

    [Fact]
    public void WrongStartXRef_IsRepaired()
    {
        var bytes = TestPdf.Simple().Build();
        var text = Encoding.Latin1.GetString(bytes);
        var at = text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10;
        var end = text.IndexOf('\n', at);
        text = text[..at] + "12345" + text[end..];

        using var doc = PdfDocument.Open(Encoding.Latin1.GetBytes(text));
        AssertSimple(doc);
        Assert.True(doc.WasRepaired);
        Assert.NotEmpty(doc.Problems);
    }

    [Theory]
    [InlineData(XRefStyle.Table)]
    [InlineData(XRefStyle.Stream)]
    public void MissingXRef_IsRebuiltFromObjects(XRefStyle style)
    {
        var bytes = TestPdf.Simple().Build(style);
        var text = Encoding.Latin1.GetString(bytes);
        // Cut off everything from the table, or after the last endobj
        var cut = style == XRefStyle.Table ? text.LastIndexOf("xref\n0 ", StringComparison.Ordinal) : text.LastIndexOf("startxref", StringComparison.Ordinal);
        using var doc = PdfDocument.Open(Encoding.Latin1.GetBytes(text[..cut]));
        // Without a trailer there is no /Info (with the table it is only there; in the cross-reference stream it is kept)
        AssertSimple(doc, withInfo: style == XRefStyle.Stream);
        Assert.True(doc.WasRepaired);
    }

    [Fact]
    public void ShiftedOffsets_AreRepairedOnDemand()
    {
        // Insert a comment line after the header: the table is present, but all offsets
        // point 11 bytes too early (a single space would simply be skipped by the reader)
        var bytes = TestPdf.Simple().Build().ToList();
        bytes.InsertRange(15, "% Einschub\n"u8.ToArray());
        using var doc = PdfDocument.Open(bytes.ToArray());
        AssertSimple(doc);
        Assert.True(doc.WasRepaired);
    }

    [Fact]
    public void PageTreeCycle_DoesNotHang()
    {
        var pdf = TestPdf.Simple(pages: 2);
        var bytes = Encoding.Latin1.GetString(pdf.Build());
        // Let the Kids of the /Pages node (object 2) also point to itself
        bytes = bytes.Replace("/Kids [", "/Kids [2 0 R ");
        using var doc = PdfDocument.Open(Encoding.Latin1.GetBytes(bytes));
        Assert.Equal(2, doc.PageCount);
    }

    [Fact]
    public void BrokenPageTree_PagesStillInheritFromParent()
    {
        // issue9418: broken page tree, pages are collected by scanning; fonts, size and rotation are only on the parent node.
        // Without inheritance such pages would get only their own entries and show no text (while the saved copy shows it, because the writer follows /Parent)
        var bytes = Encoding.Latin1.GetString(TestPdf.Simple(pages: 2).Build());
        Assert.Contains("/Pages 2 0 R", bytes);
        using var doc = PdfDocument.Open(Encoding.Latin1.GetBytes(bytes.Replace("/Pages 2 0 R", "/Pages 99 0 R")));
        Assert.Equal(2, doc.PageCount);
        Assert.NotNull(doc.Pages[0].Resources?.GetDictionary(PdfName.Get("Font")));
        Assert.Equal(595, doc.Pages[0].MediaBox.Width, 0);   // A4 from the /Pages node, not Letter
    }

    [Fact]
    public void Outline_WithDirectAndNamedDestinations()
    {
        var pdf = TestPdf.Simple(pages: 3);
        // Page objects: 5, 7, 9 (content and page alternate after catalog, pages node, font)
        var page3 = TestPdf.Ref(9);
        var outlines = pdf.Add(new PdfDictionary());
        var first = pdf.Add(new PdfDictionary());
        var second = pdf.Add(new PdfDictionary());
        var child = pdf.Add(new PdfDictionary());
        pdf.Replace(first, TestPdf.Dict(("Title", new PdfString("Einleitung"u8.ToArray())), ("Parent", TestPdf.Ref(outlines)),
            ("Next", TestPdf.Ref(second)), ("Dest", new PdfArray([page3, PdfName.Get("Fit")]))));
        pdf.Replace(second, TestPdf.Dict(("Title", new PdfString("Anhang"u8.ToArray())), ("Parent", TestPdf.Ref(outlines)),
            ("First", TestPdf.Ref(child)), ("Count", PdfInteger.Of(1)),
            ("A", TestPdf.Dict(("S", PdfName.Get("GoTo")), ("D", new PdfString("ziel"u8.ToArray()))))));
        pdf.Replace(child, TestPdf.Dict(("Title", new PdfString("Kreis"u8.ToArray())), ("Parent", TestPdf.Ref(second)),
            ("Next", TestPdf.Ref(child)),   // loop onto itself; must not hang
            ("Dest", new PdfArray([TestPdf.Ref(5), PdfName.Get("XYZ")]))));
        pdf.Replace(outlines, TestPdf.Dict(("Type", PdfName.Get("Outlines")), ("First", TestPdf.Ref(first)), ("Last", TestPdf.Ref(second))));
        var names = pdf.Add(TestPdf.Dict(("Dests", TestPdf.Dict(("Names", new PdfArray([new PdfString("ziel"u8.ToArray()), new PdfArray([TestPdf.Ref(7), PdfName.Get("Fit")])]))))));

        var bytes = Encoding.Latin1.GetString(pdf.Build());
        bytes = bytes.Replace("<</Type/Catalog", $"<</Outlines {outlines} 0 R/Names {names} 0 R/Type/Catalog");
        using var doc = PdfDocument.Open(Encoding.Latin1.GetBytes(bytes));
        var outline = PdfOutline.Read(doc);

        Assert.Equal(["Einleitung", "Anhang"], outline.Select(o => o.Title));
        Assert.Equal(2, outline[0].PageIndex);    // direct destination: page 3
        Assert.Equal(1, outline[1].PageIndex);    // named destination via the name tree: page 2
        Assert.True(outline[1].Open);
        Assert.Equal(0, Assert.Single(outline[1].Children).PageIndex);
    }

    [Fact]
    public void NotAPdf_IsRejected()
    {
        Assert.Throws<PdfException>(() => PdfDocument.Open("Hallo Welt, das ist Text."u8.ToArray()));
        Assert.Throws<PdfException>(() => PdfDocument.Open([]));
    }

    [Fact]
    public void OpensFromFile_MemoryMappedWhenLarge()
    {
        // Above 4 MB: memory-mapped instead of read
        var pdf = TestPdf.Simple();
        pdf.Add(new PdfStream(new PdfDictionary(), new byte[5 << 20]));
        var path = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-big-").FullName, "gross.pdf");
        File.WriteAllBytes(path, pdf.Build());
        using (var doc = PdfDocument.Open(path))
        {
            AssertSimple(doc);
            Assert.True(doc.FileSize > 5 << 20);
        }
        File.Delete(path);   // released again after Dispose
    }
}

public sealed class EncryptionTests
{
    /// <summary>Without a cipher no dictionary is made: before, an R3 dictionary came out for a handler that encrypted nothing (docs/FEHLERGEDAECHTNIS.md, No. 273).</summary>
    [Fact]
    public void Create_WithoutCipher_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => StandardSecurityHandler.Create(PdfCipher.None, "a", "b", PdfPermissions.All, new byte[16]));

    [Theory]
    [InlineData(PdfCipher.Rc4, XRefStyle.Table)]
    [InlineData(PdfCipher.Aes128, XRefStyle.Table)]
    [InlineData(PdfCipher.Aes256, XRefStyle.Table)]
    [InlineData(PdfCipher.Aes256, XRefStyle.Stream)]
    public void UserAndOwnerPasswords(PdfCipher cipher, XRefStyle style)
    {
        var bytes = TestPdf.Simple().Build(style, cipher, user: "geheim", owner: "chef", permissions: PdfPermissions.Print | PdfPermissions.Copy);

        var none = Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes));
        Assert.False(none.WrongPassword);
        Assert.True(Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes, "falsch")).WrongPassword);

        using (var user = PdfDocument.Open(bytes, "geheim"))
        {
            Assert.False(user.Security!.IsOwner);
            Assert.Equal(PdfPermissions.Print | PdfPermissions.Copy | PdfPermissions.Accessibility, user.Security.Permissions);
            Assert.Equal("Testdokument", user.Info.Title);
            Assert.Equal(TestPdf.PageContent(1), user.GetStreamData(user.Pages[0].Dictionary.GetStream(PdfName.Contents)!));
        }

        using var owner = PdfDocument.Open(bytes, "chef");
        Assert.True(owner.Security!.IsOwner);
        Assert.Equal(PdfPermissions.All, owner.Security.Permissions);
        Assert.Equal(3, owner.PageCount);
    }

    /// <summary>
    /// An encryption dictionary with an entry that holds itself twice (as a damaged file brings it through references to each other): saved
    /// with the encryption kept, the copy of /Encrypt stays small and quick — put in part by part, it grew exponentially up to the nesting
    /// limit until the memory ran out (docs/FEHLERGEDAECHTNIS.md, No. 378). The file opens with its password as before.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void EncryptionDictionaryThatHoldsItself_SavedQuickly()
    {
        var bytes = TestPdf.Simple(1).Build(XRefStyle.Table, PdfCipher.Aes128, user: "geheim", owner: "chef");
        using var doc = PdfDocument.Open(bytes, "geheim");
        var loop = new PdfArray();
        loop.Add(loop);
        loop.Add(loop);
        doc.EncryptDictionary!.Set(PdfName.Get("Kreis"), loop);
        using var output = new MemoryStream();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Core.Pdf.Writing.PdfWriter.Write(doc, Core.Pdf.Editing.PageItem.From(doc), output);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10 * TestKind.Factor), watch.Elapsed.ToString());
        using var saved = PdfDocument.Open(output.ToArray(), "geheim");
        Assert.Equal(TestPdf.PageContent(1), saved.GetStreamData(saved.Pages[0].Dictionary.GetStream(PdfName.Contents)!));
    }

    /// <summary>
    /// A kept encryption of revision 2 to 4 (RC4, AES-128) is deprecated in PDF 2.0 and a new one would need the passwords: saved as 2.0, the
    /// file is written as PDF 1.7 with its encryption; AES-256 stays at 2.0 (docs/FEHLERGEDAECHTNIS.md, No. 371).
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Rc4, "1.7")]
    [InlineData(PdfCipher.Aes128, "1.7")]
    [InlineData(PdfCipher.Aes256, "2.0")]
    public void KeptEncryption_SavedAsPdf20_OnlyWithAes256(PdfCipher cipher, string version)
    {
        var bytes = TestPdf.Simple().Build(XRefStyle.Table, cipher, user: "geheim", owner: "chef");
        using var doc = PdfDocument.Open(bytes, "geheim");
        using var output = new MemoryStream();
        var result = Core.Pdf.Writing.PdfWriter.Write(doc, Core.Pdf.Editing.PageItem.From(doc), output, new Core.Pdf.Writing.PdfSaveOptions { Version = "2.0" });
        Assert.Equal(version, result.Version);
        Assert.StartsWith($"%PDF-{version}", System.Text.Encoding.ASCII.GetString(output.ToArray(), 0, 8));
        using var saved = PdfDocument.Open(output.ToArray(), "geheim");
        Assert.Equal(cipher == PdfCipher.Aes256 ? 6 : doc.Security!.Revision, saved.Security!.Revision);
        Assert.Equal(TestPdf.PageContent(1), saved.GetStreamData(saved.Pages[0].Dictionary.GetStream(PdfName.Contents)!));
    }

    /// <summary>
    /// AES-256: the permissions the owner sealed in /Perms count, not the plain /P that anyone can edit. A /P loosened to „everything“ in
    /// the bytes of the file was taken as a real permission (docs/FEHLERGEDAECHTNIS.md, No. 432).
    /// </summary>
    [Fact]
    public void Aes256_LoosenedP_PermissionsComeFromPerms()
    {
        var bytes = TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: "geheim", owner: "chef", permissions: PdfPermissions.Print);
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var at = text.IndexOf("/P -", StringComparison.Ordinal);
        Assert.True(at > 0);
        var end = text.IndexOfAny([' ', '/', '>'], at + 4);
        // the same length, so that the offsets of the file stay
        var loosened = ("/P -1" + new string(' ', end - at)).Substring(0, end - at);
        var patched = System.Text.Encoding.Latin1.GetBytes(text[..at] + loosened + text[end..]);
        using var doc = PdfDocument.Open(patched, "geheim");
        Assert.False(doc.Security!.IsOwner);
        Assert.Equal(PdfPermissions.Print | PdfPermissions.Accessibility, doc.Security.Permissions);
    }

    [Theory]
    [InlineData(PdfCipher.Rc4)]
    [InlineData(PdfCipher.Aes256)]
    public void EmptyUserPassword_OpensWithoutAsking(PdfCipher cipher)
    {
        var bytes = TestPdf.Simple().Build(cipher: cipher, user: "", owner: "chef", permissions: PdfPermissions.Print);
        using var doc = PdfDocument.Open(bytes);
        Assert.True(doc.IsEncrypted);
        Assert.Equal(PdfPermissions.Print | PdfPermissions.Accessibility, doc.Security!.Permissions);
        Assert.Equal("Testdokument", doc.Info.Title);
    }

    [Fact]
    public void Aes256_UnicodePassword()
    {
        var bytes = TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: "Grüße€", owner: "x");
        using var doc = PdfDocument.Open(bytes, "Grüße€");
        Assert.Equal(3, doc.PageCount);
    }
}
