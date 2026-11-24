using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

public sealed partial class EncryptionNormTests
{
    private static PdfReference IntoTheVoid => new(new PdfObjectId(990, 0));

    /// <summary>
    /// S-A A67 for the keys of M4 (ISO 32000-2, 7.3.9: an entry whose reference leads nowhere counts as absent; docs/FEHLERGEDAECHTNIS.md,
    /// No. 1120): /AuthCode in the trailer and /KDFSalt in /Encrypt into the void ask for no MAC per revision — an AES-256 file without the MAC
    /// requirement may be signed and appended to, and a copy does not report a dropped integrity protection; before, the bare key counted and
    /// the file was refused as protected by ISO/TS 32004. The same key with a value still asks for it.
    /// </summary>
    [Theory]
    [InlineData("AuthCode", false)]
    [InlineData("KDFSalt", false)]
    [InlineData("AuthCode", true)]
    public void MacKeysIntoTheVoid_AskForNoMac(string key, bool withValue)
    {
        PdfObject value = withValue ? new PdfDictionary() : IntoTheVoid;
        var file = Tests.Pdf.TestPdf.Simple(1).Build(cipher: PdfCipher.Aes256, user: "", permissions: PdfPermissions.All, version: "2.0",
            patchTrailer: key == "AuthCode" ? t => t.Set(PdfName.Get(key), value) : null,
            patchEncrypt: key == "KDFSalt" ? e => e.Set(PdfName.Get(key), value) : null);
        using var doc = PdfDocument.Open(file);
        Assert.False(StandardSecurityHandler.RequiresMac(doc.Security!.RawPermissions, doc.Security.Revision));
        var refusal = IncrementalUpdate.EncryptionRefusal(doc, writeMac: false);
        if (withValue)
            Assert.Equal(Tr.T("Diese Datei ist zusätzlich gegen Veränderung geschützt (ISO/TS 32004); PDF-Knead kann sie noch nicht signieren."), refusal);
        else
        {
            Assert.Null(refusal);
            using var output = new MemoryStream();
            var result = PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "2.0" });
            Assert.False(result.IntegrityProtectionDropped);
        }
    }
}
