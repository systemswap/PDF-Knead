using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Opening a document encrypted for recipients (ISO 32000-2, 7.6.5; docs/FEHLERGEDAECHTNIS.md, No. 1117): no certificate of the store fits, so
/// PDF-Knead says why and asks for a certificate file and its password — a wrong password is asked again, the right one opens the document;
/// „Abbrechen“ opens nothing and leaves no message of an error.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class PublicKeyUiTests
{
    private static string Folder()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "TestPDFs"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("TestPDFs");
        return Path.Combine(root, "TestPDFs", "pdfium", "fx", "FRC_3.5_part1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecipientFile_CertificateFileAskedFor_OpensWithIt(bool choose)
    {
        var pdf = Path.Combine(Folder(), "FRC_3.5_Filter_PubSec_SubFilter_s5.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            string? heading = null;
            var passwords = new Queue<string>(["falsch", "111111"]);
            var asked = 0;
            vm.AskChoice = (title, _, _, _) =>
            {
                heading = title;
                return Task.FromResult(choose ? 1 : 0);
            };
            vm.PickCertificateFile = _ => Task.FromResult<string?>(Path.Combine(Folder(), "pkcs_12_phantom.pfx"));
            vm.AskSessionPassword = (_, _, _, wrong) =>
            {
                asked++;
                Assert.Equal(asked > 1, wrong);
                return Task.FromResult<string?>(passwords.Dequeue());
            };
            await vm.OpenAsync([pdf]);
            Assert.Equal("Für Empfänger verschlüsselt", heading);
            if (choose)
            {
                var doc = Assert.Single(vm.Documents);
                Assert.True(doc.Document.Security!.IsPublicKey);
                Assert.Equal(2, asked);
                doc.Dispose();
            }
            else
            {
                Assert.Empty(vm.Documents);
                Assert.Equal(0, asked);
            }
            Assert.True(string.IsNullOrEmpty(vm.Message), vm.Message);
        });
    }

    private static X509Certificate2 Person(string name)
    {
        using var rsa = RSA.Create(2048);
        using var created = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>
    /// „Für Empfänger (Zertifikate)“ in „Passwort und Rechte“: without a recipient refused, a certificate only once; one recipient with „Alle
    /// Rechte“, one with the rights below (no copying, no modifying). Saved, the document is open again at once (with the written key, though
    /// the one saving is no recipient), and the file opens for each recipient with exactly those rights.
    /// </summary>
    [Fact]
    public void ProtectionWindow_Recipients_SavedFileOpensForEachWithTheirRights()
    {
        using var chef = Person("Erika Musterfrau");
        using var leser = Person("Max Mustermann");
        var path = TestFiles.Write(TestPdf.Simple(1).Build(), "empfaenger.pdf");
        var target = Path.Combine(Path.GetDirectoryName(path)!, "empfaenger-verschluesselt.pdf");
        UiSession.Run(async () =>
        {
            var window = new ProtectionWindow(false, PdfPermissions.All, "2.0");
            window.Show();
            ProtectionChange change;
            try
            {
                UiSession.Capture(window);
                CheckBox Check(string name) => window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == name);
                window.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "RecipientMode").IsChecked = true;
                UiSession.Capture(window);
                Assert.Equal(Tr.T("Bitte mindestens ein Zertifikat eines Empfängers hinzufügen."), window.Validate());
                Assert.Null(window.AddRecipient(X509CertificateLoader.LoadCertificate(chef.RawData)));
                Assert.NotNull(window.AddRecipient(X509CertificateLoader.LoadCertificate(chef.RawData)));   // only once
                Assert.Null(window.AddRecipient(X509CertificateLoader.LoadCertificate(leser.RawData)));
                window.Recipients[0].FullAccess.IsChecked = true;
                Check("RightsBox").IsChecked = true;
                Check("CopyBox").IsChecked = false;
                Check("ModifyBox").IsChecked = false;
                Assert.Null(window.Validate());
                change = window.Result()!;
                Assert.Equal(2, change.Recipients!.Count);
                Assert.Null(change.Protection);
            }
            finally
            {
                window.Close();
            }

            var doc = new DocumentViewModel(path, PdfDocument.Open(path)) { PendingProtection = change };
            Assert.True(doc.IsProtected);
            await doc.SaveAsync(target, doc.SaveOptions("2.0"));
            Assert.True(doc.Document.Security!.IsPublicKey);
            doc.Dispose();
        });
        using (var asChef = PdfDocument.Open(target, null, [chef]))
            Assert.True(asChef.Security!.IsOwner);
        using var asLeser = PdfDocument.Open(target, null, [leser]);
        Assert.False(asLeser.Security!.IsOwner);
        Assert.False(asLeser.Security.Permissions.HasFlag(PdfPermissions.Copy));
        Assert.False(asLeser.Security.Permissions.HasFlag(PdfPermissions.Modify));
        Assert.True(asLeser.Security.Permissions.HasFlag(PdfPermissions.Print));
    }
}
