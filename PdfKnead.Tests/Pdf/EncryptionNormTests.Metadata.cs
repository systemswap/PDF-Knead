using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;

namespace PdfKnead.Tests.Pdf;

public sealed partial class EncryptionNormTests
{
    /// <summary>
    /// /EncryptMetadata counts with crypt filters only (ISO 32000-2, 7.6.4.2, Table 21: V 4 and 5): an RC4 file (V 2) that says „false“
    /// still has its document metadata encrypted and is read decrypted — before, the entry was obeyed and the XMP read as plain garbage
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1101).
    /// </summary>
    [Fact]
    public void EncryptMetadataFalse_WithoutCryptFilters_MetadataStillDecrypted()
    {
        var xmp = Encoding.UTF8.GetBytes("<?xpacket begin=\"\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><dc:title>Musterbericht</dc:title></x:xmpmeta><?xpacket end=\"w\"?>");
        var pdf = Pdf.TestPdf.Simple(1);
        var metadata = pdf.Add(new PdfStream(Pdf.TestPdf.Dict(("Type", Pdf.TestPdf.N("Metadata")), ("Subtype", Pdf.TestPdf.N("XML"))), xmp));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Metadata, Pdf.TestPdf.Ref(metadata));
        var bytes = pdf.Build(cipher: PdfCipher.Rc4, user: "geheim", owner: "Besitzer",
            patchEncrypt: e => e.Set(PdfName.Get("EncryptMetadata"), PdfBoolean.False));
        using var doc = PdfDocument.Open(bytes, "geheim");
        Assert.Equal(2, doc.EncryptDictionary!.GetInteger(PdfName.Get("V")));
        Assert.True(doc.Security!.EncryptMetadata);
        Assert.Equal(xmp, doc.DecodeStream((PdfStream)doc.GetObject(metadata)).Data);
    }
    /// <summary>
    /// Every crypt filter named must stand in /CF (ISO 32000-2, 7.6.2, Table 20): /EFF naming one that is missing is said, and the attachments
    /// are read with the filter of the streams — before, silently (docs/FEHLERGEDAECHTNIS.md, No. 1102). A known /EFF gives no note.
    /// </summary>
    [Theory]
    [InlineData("Fremd", true)]
    [InlineData("StdCF", false)]
    public void EmbeddedFileFilter_MissingFromCf_IsSaid(string eff, bool said)
    {
        var bytes = Pdf.TestPdf.Simple(1).Build(cipher: PdfCipher.Aes128, user: "geheim", owner: "Besitzer",
            patchEncrypt: e => e.Set(PdfName.Get("EFF"), PdfName.Get(eff)));
        using var doc = PdfDocument.Open(bytes, "geheim");
        Assert.Equal(said, doc.Problems.Any(p => p.Contains("/EFF", StringComparison.Ordinal)));
        Assert.Equal(PdfCipher.Aes128, doc.Security!.EmbeddedFileCipher);
    }
    /// <summary>
    /// A revision-6 encryption sealed anew without the MAC requirement (ISO/TS 32004 dropped in the copy) sets bit 10 of /P, as writers
    /// always do (ISO 32000-2, 7.6.4.2, Table 22) — a source whose rights had it cleared kept it cleared (docs/FEHLERGEDAECHTNIS.md, No. 1105).
    /// The rights of the source are given a cleared bit 10 through the private setter: such a file cannot be built through the own writer.
    /// </summary>
    [Fact]
    public void SealedAnewWithoutMac_SetsBit10()
    {
        var bytes = Pdf.TestPdf.Simple(1).Build(cipher: PdfCipher.Aes256, user: "geheim", owner: "Besitzer", version: "2.0", macRequired: true);
        using var doc = PdfDocument.Open(bytes, "geheim");
        var handler = doc.Security!;
        var raw = typeof(StandardSecurityHandler).GetProperty("RawPermissions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        raw.SetValue(handler, (int)raw.GetValue(handler)! & ~(int)PdfPermissions.Accessibility);
        var (encrypt, sealedAnew) = handler.WithoutMacRequirement(doc.EncryptDictionary!);
        Assert.NotEqual(0, encrypt.GetInteger(PdfName.Get("P"))!.Value & (int)PdfPermissions.Accessibility);
        Assert.NotEqual(0, sealedAnew.RawPermissions & (int)PdfPermissions.Accessibility);
        Assert.NotEqual(0, sealedAnew.RawPermissions & StandardSecurityHandler.MacBit);
    }
    /// <summary>
    /// V 6 (AES-GCM, ISO/TS 32003) belongs to revision 7: a file naming revision 6 beside it opens (the password algorithm is the same) and
    /// says so — before, silently (docs/FEHLERGEDAECHTNIS.md, No. 1106). Revision 7 gives no note.
    /// </summary>
    [Theory]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void V6_WithAnotherRevision_IsSaid(int revision, bool said)
    {
        var bytes = Pdf.TestPdf.Simple(1).Build(cipher: PdfCipher.Aes256Gcm, user: "geheim", owner: "Besitzer", version: "2.0",
            patchEncrypt: e => e.Set(PdfName.Get("R"), PdfInteger.Of(revision)));
        using var doc = PdfDocument.Open(bytes, "geheim");
        Assert.Equal(PdfCipher.Aes256Gcm, doc.Security!.StreamCipher);
        Assert.Equal(said, doc.Problems.Any(p => p.Contains("Version 6", StringComparison.Ordinal)));
    }
}
