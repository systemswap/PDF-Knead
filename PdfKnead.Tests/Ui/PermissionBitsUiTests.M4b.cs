using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The permission bits of the standard security handler as the window applies them (ISO 32000-2, 7.6.4.2, Table 22; Konformitätsabgleich
/// M4): printing (bits 3 and 12, bit 12 only from revision 3) and assembling (bit 11). The files open for the user, not the owner. These
/// tests never print.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class PermissionBitsUiTests
{
    /// <summary>Opens the file as user in a document view and the print dialog state, hands both to <paramref name="check"/>, then releases them.</summary>
    private static void WithPrintState(byte[] file, Action<DocumentViewModel, PrintViewModel> check)
    {
        var path = TestFiles.Write(file, "rechte.pdf");
        UiSession.Run(async () =>
        {
            using var doc = new DocumentViewModel(path, PdfDocument.Open(path));
            Assert.False(doc.Document.Security!.IsOwner);
            var print = new PrintViewModel(doc);
            try
            {
                check(doc, print);
            }
            finally
            {
                print.Dispose();
                await print.Released.WaitAsync(TimeSpan.FromSeconds(30));
            }
        });
    }

    private static byte[] Aes128With(PdfPermissions permissions) => TestPdf.Simple(1).Build(cipher: PdfCipher.Aes128, permissions: permissions);

    /// <summary>7.6.4.2, Table 22 (bit 3: print): with bit 3 cleared printing is not allowed; with it set it is.</summary>
    [Fact]
    public void PrintBit3_Cleared_PrintingNotAllowed()
    {
        WithPrintState(Aes128With(PdfPermissions.Copy | PdfPermissions.PrintHighQuality), (_, print) => Assert.False(print.PrintAllowed));
        WithPrintState(Aes128With(PdfPermissions.Print), (_, print) => Assert.True(print.PrintAllowed));
    }

    /// <summary>
    /// 7.6.4.2, Table 22 (bit 3: from revision 3, bit 12 decides the quality): a file of revision 2 with bit 3 set and bit 12 cleared prints
    /// at full quality — bit 12 has no meaning there. File built independently in the test (Algorithms 2 to 4).
    /// </summary>
    [Fact]
    public void Revision2_Bit12Cleared_PrintsAtFullQuality()
    {
        // bit 3 set, bits 4 to 6 and 12 cleared, bits 7 to 11 and 13 to 32 set
        WithPrintState(EncryptionNormTests.Revision2File(unchecked((int)0xFFFFF7C4)), (_, print) =>
        {
            Assert.True(print.PrintAllowed);
            Assert.False(print.LowResolutionOnly);
        });
    }

    /// <summary>
    /// 7.6.4.2, Table 22 (bit 12, from revision 3: printing at full quality; cleared while bit 3 is set, only a simpler, possibly poorer
    /// output): revision 4 with bit 3 set and bit 12 cleared allows printing only at low resolution; with bit 12 set, at full quality.
    /// </summary>
    [Fact]
    public void PrintBit12_Cleared_OnlyLowResolution()
    {
        WithPrintState(Aes128With(PdfPermissions.Print), (_, print) =>
        {
            Assert.True(print.PrintAllowed);
            Assert.True(print.LowResolutionOnly);
        });
        WithPrintState(Aes128With(PdfPermissions.Print | PdfPermissions.PrintHighQuality), (_, print) => Assert.False(print.LowResolutionOnly));
    }

    /// <summary>
    /// 7.6.4.2, Table 22 (bit 11, from revision 3: rearranging pages and adding bookmarks or thumbnails, also without bit 4): with bit 11 alone
    /// the pages may be edited; with neither bit 11 nor bit 4 they may not.
    /// </summary>
    [Fact]
    public void AssembleBit11_Alone_PagesEditable()
    {
        WithPrintState(Aes128With(PdfPermissions.Assemble), (doc, _) => Assert.True(doc.CanEditPages));
        WithPrintState(Aes128With(PdfPermissions.Print), (doc, _) => Assert.False(doc.CanEditPages));
    }
}
