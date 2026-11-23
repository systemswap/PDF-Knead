using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Crypt filters of their own and /EFF (ISO 32000-2, 7.4.10 and 7.6.6; docs/FEHLERGEDAECHTNIS.md, No. 442).</summary>
public sealed class CryptFilterTests
{
    private static byte[] Save(PdfDocument doc, PdfSaveOptions options)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, options);
        return output.ToArray();
    }

    private static List<PdfStream> Streams(PdfDocument doc)
    {
        var streams = new List<PdfStream>();
        for (var n = 1; n <= doc.HighestObjectNumber; n++)
        {
            try
            {
                if (doc.GetObject(n) is PdfStream stream) streams.Add(stream);
            }
            catch (PdfException)
            {
                // a free or unreadable entry
            }
        }
        return streams;
    }

    private static string Text(PdfDocument doc, PdfStream stream) => Encoding.Latin1.GetString(doc.DecodeStream(stream).Data);

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        return "";
    }

    /// <summary>
    /// The public test file with crypt filters of their own (V 4: /StmF AES-128, /StrF and the form and image RC4 by their own /Crypt
    /// filter, /EFF Identity, one attachment with a filter of its own): the form, the image and both attachments read as plain data, and
    /// they still do after saving — with the encryption kept (then without the RC4 filters of their own, S-B B46: FG 991), and without it (no
    /// /Crypt filter left then). Every stream was read with
    /// /StmF: the RC4 form came as garbage, the image empty, the unencrypted attachment as zero bytes, and saving destroyed it
    /// (docs/FEHLERGEDAECHTNIS.md, No. 442).
    /// </summary>
    [TestPdfsFact]
    public void CryptFilters_PdfJs_FormsImagesAndAttachmentsRead()
    {
        using var doc = PdfDocument.Open(Path.Combine(Root(), "TestPDFs", "pdfjs", "crypt_filters.pdf"));
        Assert.Equal("Embedded file: /EFF /Identity", Text(doc, (PdfStream)doc.GetObject(14)));
        Assert.StartsWith("% This comment", Text(doc, (PdfStream)doc.GetObject(6)));
        Assert.NotEmpty(doc.DecodeStream((PdfStream)doc.GetObject(10)).Data);
        var attachments = Streams(doc).Where(s => s.Dictionary.IsType(PdfName.Get("EmbeddedFile"))).Select(s => Text(doc, s)).Order().ToList();
        Assert.Equal(2, attachments.Count);
        Assert.All(attachments, a => Assert.True(a.All(c => c is >= ' ' and <= '~' or '\n' or '\r'), $"nicht lesbar: {a}"));

        foreach (var keep in new[] { true, false })
        {
            using var saved = PdfDocument.Open(Save(doc, new PdfSaveOptions { KeepEncryption = keep }));
            var files = Streams(saved).Where(s => s.Dictionary.IsType(PdfName.Get("EmbeddedFile"))).Select(s => Text(saved, s)).Order().ToList();
            Assert.Equal(attachments, files);
            if (!keep) Assert.DoesNotContain(Streams(saved), s => StreamFilters.Chain(s.Dictionary).Any(f => f.Name == "Crypt"));
            // ISO 32000-2, Table 14 (S-B B46, decided on 20.11.2026): no stream keeps a filter of its own with V2 or AESV2 — the RC4
            // form goes to the default filter of the document and still reads; the Identity filter of the attachments stays. The exception
            // (FG 991): the attachment with AES of its own keeps it, as /EFF is Identity — without it the copy would hold it unencrypted
            Assert.Contains(Streams(saved), s => Text(saved, s).StartsWith("% This comment", StringComparison.Ordinal));
            var keptOwn = 0;
            foreach (var s in Streams(saved))
                if (StreamFilters.Chain(s.Dictionary) is [("Crypt", var parms), ..] &&
                    saved.Security!.CipherNamed(parms?.GetName(PdfName.Name)) is PdfCipher.Rc4 or PdfCipher.Aes128)
                {
                    var file = s.Dictionary.IsType(PdfName.Get("EmbeddedFile"));
                    Assert.True(file && saved.Security.EmbeddedFileCipher == PdfCipher.None || !file && saved.Security.StreamCipher == PdfCipher.None,
                        $"eigener Kryptfilter {parms?.GetName(PdfName.Name)?.Value}");
                    keptOwn++;
                }
            Assert.Equal(keep ? 1 : 0, keptOwn);
        }
    }
    /// <summary>
    /// A one-page file whose only attachment („anlage.txt“ in the name tree) is encrypted with a password of its own, „Anhang“ (AES-128,
    /// /EFF with AuthEvent EFOpen; streams and strings Identity). <paramref name="line"/>: the text on the page, if any.
    /// </summary>
    internal static byte[] WithLockedAttachment(byte[] content, string? line = null)
    {
        var pdf = TestPdf.Simple(1);
        if (line is not null) pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes($"BT /F1 12 Tf 72 700 Td ({line}) Tj ET")));
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), content));
        var spec = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", new PdfString("anlage.txt"u8.ToArray())),
            ("EF", TestPdf.Dict(("F", TestPdf.Ref(file))))));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("Names"),
            TestPdf.Dict(("EmbeddedFiles", TestPdf.Dict(("Names", new PdfArray([new PdfString("anlage.txt"u8.ToArray()), TestPdf.Ref(spec)]))))));
        return pdf.Build(cipher: Core.Pdf.Security.PdfCipher.Aes128, user: "Anhang", owner: "Anhang", attachmentsOnly: true);
    }

    /// <summary>
    /// Only the attachments encrypted with a password of their own (AES-128, /EFF with AuthEvent EFOpen; streams and strings Identity — as
    /// the public test file FRC_3.5_AuthEvent_EFOpen is built). The document opens without it and the attachment is locked; saving
    /// breaks off with the exception the program answers by asking for that password. A wrong one does not open it; with the right one the
    /// attachment reads, and the saved copy keeps it encrypted for the same password. Before, there was no way to give that password:
    /// saving broke off for good, and the search before redacting could only report the attachment as not searchable (design decision,
    /// 4.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 459).
    /// </summary>
    [Fact]
    public void AttachmentsWithAPasswordOfTheirOwn_UnlockedWithIt_ReadAndCarriedOver()
    {
        var content = Encoding.ASCII.GetBytes("Anlage von Max Mustermann: Kontonummer 4711");
        using var doc = PdfDocument.Open(WithLockedAttachment(content));
        Assert.True(doc.Security!.AttachmentsLocked);
        var attachment = Assert.Single(Streams(doc), s => s.Dictionary.IsType(PdfName.Get("EmbeddedFile")));
        Assert.ThrowsAny<PdfException>(() => doc.DecodeStream(attachment));
        Assert.Throws<PdfAttachmentsLockedException>(() => Save(doc, new PdfSaveOptions()));

        Assert.Equal(PdfDocument.AttachmentUnlock.WrongPassword, doc.UnlockAttachments("falsch"));
        Assert.True(doc.Security!.AttachmentsLocked);
        Assert.Equal(PdfDocument.AttachmentUnlock.Opened, doc.UnlockAttachments("Anhang"));
        Assert.False(doc.Security!.AttachmentsLocked);
        Assert.Equal(content, doc.DecodeStream(attachment).Data);

        using var copy = PdfDocument.Open(Save(doc, new PdfSaveOptions()));
        Assert.True(copy.Security!.AttachmentsLocked);
        Assert.Equal(PdfDocument.AttachmentUnlock.Opened, copy.UnlockAttachments("Anhang"));
        var carried = Assert.Single(Streams(copy), s => s.Dictionary.IsType(PdfName.Get("EmbeddedFile")));
        Assert.Equal(content, copy.DecodeStream(carried).Data);
    }

    /// <summary>
    /// The public test file FRC_3.5_AuthEvent_EFOpen: the password „1“ matches the password check, but its attachment decrypts with no
    /// variant of the standard (the padding is never whole). Taken anyway, the garbage would be written anew and the attachment destroyed:
    /// it stays locked, and saving still breaks off (No. 459).
    /// </summary>
    [TestPdfsFact]
    public void AttachmentThatDoesNotDecryptWithTheRightPassword_StaysLocked()
    {
        using var doc = PdfDocument.Open(Path.Combine(Root(), "TestPDFs", "pdfium", "fx", "FRC_3.5_part1", "FRC_3.5_AuthEvent_EFOpen.pdf"));
        Assert.True(doc.Security!.AttachmentsLocked);
        Assert.Equal(PdfDocument.AttachmentUnlock.WrongPassword, doc.UnlockAttachments("2"));
        Assert.Equal(PdfDocument.AttachmentUnlock.NotDecryptable, doc.UnlockAttachments("1"));
        Assert.True(doc.Security!.AttachmentsLocked);
        Assert.Throws<PdfAttachmentsLockedException>(() => Save(doc, new PdfSaveOptions()));
    }

    /// <summary>
    /// S-B B46 (FG 991): a stream with an AES-128 filter of its own in a file whose default filter does not encrypt it (only the attachments are
    /// encrypted, /StmF Identity) keeps that filter when saved with the encryption kept — written without it, it would lie in the copy
    /// unencrypted. The stream: the page content, encrypted under the filter /StdCF of the attachments, read after unlocking them.
    /// </summary>
    [Fact]
    public void OwnFilter_WhereTheDefaultDoesNotEncrypt_KeptWhenSaved()
    {
        var pdf = TestPdf.Simple(1);
        var plain = "BT /F1 12 Tf 72 700 Td (geheim) Tj ET"u8.ToArray();
        byte[] firstId = [.. Enumerable.Range(1, 16).Select(i => (byte)(i * 7))];
        var (_, handler) = StandardSecurityHandler.Create(PdfCipher.Aes128, "Anhang", "Anhang", PdfPermissions.Print, firstId, true);
        var parms = TestPdf.Dict(("Type", TestPdf.N("CryptFilterDecodeParms")), ("Name", TestPdf.N("StdCF")));
        pdf.Replace(4, new PdfStream(TestPdf.Dict(("Filter", new PdfArray([TestPdf.N("Crypt")])), ("DecodeParms", new PdfArray([parms]))),
            handler.EncryptStream(new PdfObjectId(4, 0), plain)));
        using var doc = PdfDocument.Open(pdf.Build(cipher: PdfCipher.Aes128, user: "Anhang", owner: "Anhang", attachmentsOnly: true));
        Assert.Equal(PdfDocument.AttachmentUnlock.Opened, doc.UnlockAttachments("Anhang"));
        Assert.Equal(plain, doc.DecodeStream((PdfStream)doc.GetObject(4)).Data);

        using var copy = PdfDocument.Open(Save(doc, new PdfSaveOptions { KeepEncryption = true }));
        var content = (PdfStream)copy.Pages[0].Dictionary.Get(PdfName.Contents);
        Assert.Equal("Crypt", StreamFilters.Chain(content.Dictionary)[0].Name);
        Assert.False(copy.StoredStreamData(content).SequenceEqual(plain), "unverschlüsselt in der Kopie");
        Assert.Equal(PdfDocument.AttachmentUnlock.Opened, copy.UnlockAttachments("Anhang"));
        Assert.Equal(plain, copy.DecodeStream(content).Data);
    }
}
