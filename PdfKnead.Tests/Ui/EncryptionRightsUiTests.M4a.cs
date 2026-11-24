using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// ISO 32000-2, 7.6.4.1 (Konformitätsabgleich M4, package b-763-7641): what the permissions of a protected file allow in the program — forms,
/// pages, printing and its quality. In the UI collection, as the view models need the UI thread (docs/FEHLERGEDAECHTNIS.md, No. 627). These
/// tests never print: the print window's state is only read.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class EncryptionRightsUiTests
{
    /// <summary>Opens the file as a document of the program and hands its view model and that of the print window to <paramref name="check"/>.</summary>
    private static void WithDocument(byte[] file, Action<DocumentViewModel, PrintViewModel> check)
    {
        var path = TestFiles.Write(file, "geschuetzt.pdf");
        UiSession.Run(async () =>
        {
            var doc = new DocumentViewModel(path, PdfDocument.Open(path));
            try
            {
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
            }
            finally
            {
                await doc.Renderer.WaitUntilUnused([doc.Document]).WaitAsync(TimeSpan.FromSeconds(10));
                doc.Dispose();
            }
        });
    }

    /// <summary>
    /// 7.6.4.1: with revision 2 only the bits of revision 2 count. A file built independently (revision 2, 40 bits) whose /P grants bits 3 to 6
    /// and clears bits 9 to 12: forms are filled by bit 6, pages assembled by bit 4, printing is in full quality by bit 3 — the bits of revision 3
    /// are not asked for.
    /// </summary>
    [Fact]
    public void Revision2_BitsNineToTwelveNotAsked_FormsPagesAndFullPrintByTheirOldBits()
    {
        const int p = unchecked((int)0xFFFFF0FC);   // bits 3 to 8 and 13 to 32 set, 9 to 12 cleared
        var file = EncryptionNormTests.LegacyFile(2, 5, "/V 1", permissions: p);
        WithDocument(file.File, (doc, print) =>
        {
            Assert.Equal(2, doc.Document.Security!.Revision);
            Assert.False(doc.HasAllRights);
            Assert.True(doc.CanFillForms);
            Assert.True(doc.CanEditPages);
            Assert.True(print.PrintAllowed);
            Assert.False(print.LowResolutionOnly);
        });
    }

    /// <summary>
    /// 7.6.4.1: from revision 3, printing in good quality needs bit 12 besides bit 3 — without it only a lower resolution is printed, with it
    /// full quality.
    /// </summary>
    [Theory]
    [InlineData(PdfPermissions.Print, true)]
    [InlineData(PdfPermissions.Print | PdfPermissions.PrintHighQuality, false)]
    public void Revision3_WithoutBit12_PrintsInLowResolution(PdfPermissions permissions, bool lowResolution)
    {
        var file = TestPdf.Simple(1).Build(cipher: PdfCipher.Rc4, user: "", permissions: permissions);
        WithDocument(file, (doc, print) =>
        {
            Assert.Equal(3, doc.Document.Security!.Revision);
            Assert.True(print.PrintAllowed);
            Assert.Equal(lowResolution, print.LowResolutionOnly);
        });
    }

    /// <summary>
    /// 7.6.4.1: readers keep to the permissions. A file opened without its owner password whose /P lacks bit 3 cannot be printed; with bit 3
    /// it can.
    /// </summary>
    [Theory]
    [InlineData(PdfPermissions.Copy, false)]
    [InlineData(PdfPermissions.Copy | PdfPermissions.Print, true)]
    public void PrintBit_Missing_PrintingNotAllowed(PdfPermissions permissions, bool allowed)
    {
        var file = TestPdf.Simple(1).Build(cipher: PdfCipher.Aes128, user: "", permissions: permissions);
        WithDocument(file, (doc, print) =>
        {
            Assert.False(doc.Document.Security!.IsOwner);
            Assert.Equal(allowed, print.PrintAllowed);
        });
    }
}
