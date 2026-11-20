using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// A file protected against changes by a MAC (ISO/TS 32004) is saved without it — PDF-Knead cannot write the MAC yet. That is said after
/// saving and stays on screen, the copy keeps encryption and passwords (docs/ABLAUFPLAN.md, 5.2; docs/FEHLERGEDAECHTNIS.md, No. 546).
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class MacProtectionUiTests
{
    [Fact]
    public void MacFile_Saved_IntegrityNoticeShown()
    {
        var path = TestFiles.Write(EncryptionHonestyTests.MacFile(), "mac-pflicht.pdf");
        var target = Path.Combine(Path.GetDirectoryName(path)!, "mac-pflicht-gespeichert.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var asked = 0;
            vm.AskPassword = (_, _) => Task.FromResult<string?>(++asked == 1 ? "chef" : null);
            await vm.OpenAsync([path]);
            var doc = vm.SelectedDocument!;
            doc.RotateSelected(90);
            Assert.True(await vm.SaveDocumentAsync(doc, target, doc.SaveVersion));
            Assert.Equal(Tr.T("Gespeichert. Die Datei war zusätzlich gegen Veränderung geschützt (Integritätsschutz nach ISO/TS 32004). Diesen Schutz kann PDF-Knead noch nicht erneuern — die Datei ist weiter verschlüsselt, aber ohne ihn."),
                vm.Message);
            vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
        });
        using var saved = PdfDocument.Open(File.ReadAllBytes(target), "geheim");
        Assert.NotNull(saved.Security);
        Assert.NotEqual(0, saved.Security!.RawPermissions & 0x1000);
    }
}
