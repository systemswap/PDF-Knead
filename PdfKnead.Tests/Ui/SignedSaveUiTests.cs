using Avalonia.Controls;
using Avalonia.Media.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Saving a digitally signed document with a change that rewrites the file (here: a turned page) breaks the signature: PDF-Knead
/// asks first (KONZEPT.md, section 5.17, step S0). Without changes, or with changes that are appended (S5), nothing is asked. Cancel and “no view” keep the file byte for byte; “save as new file” keeps the signed original;
/// “save anyway” overwrites, and the next save no longer asks because nothing intact is left.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class SignedSaveUiTests
{
    [Fact]
    public void SignedDocument_AsksBeforeSave()
    {
        var bytes = SignedTestPdf.Build();
        var pdf = TestFiles.Write(bytes, "unterschrieben.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.True(doc.AppendsToSigned(null));   // unchanged: saving would only keep the bytes
                doc.History.Rotate([0], 90);              // a change that rewrites the file
                Assert.False(doc.AppendsToSigned(null));

                // 1. without an answer (no view): nothing is saved; the window sets the real question, so remove it here
                vm.ConfirmSignedSave = null;
                Assert.False(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(bytes, File.ReadAllBytes(pdf));

                // 2. cancel: the question names the file and the count, the file stays as it was
                var questions = new List<SignedSaveQuestion>();
                vm.ConfirmSignedSave = question =>
                {
                    questions.Add(question);
                    var dialog = new SignedSaveWindow(question) { SizeToContent = SizeToContent.Manual, Height = 260 };
                    dialog.Show();
                    Shoot(dialog, question.OverwritesOriginal ? "60-signiert-speichern" : "61-signiert-neue-datei");
                    dialog.Close();
                    return Task.FromResult(SignedSaveAnswer.Cancel);
                };
                Assert.False(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                var asked = Assert.Single(questions);
                Assert.Equal(("unterschrieben.pdf", 1, true), (asked.FileName, asked.Count, asked.OverwritesOriginal));
                Assert.Equal(bytes, File.ReadAllBytes(pdf));

                // 3. save as a new file: the signed original stays byte for byte, the document now is the copy
                var copy = Path.Combine(Path.GetDirectoryName(pdf)!, "unterschrieben-kopie.pdf");
                string? suggested = null;
                vm.ConfirmSignedSave = _ => Task.FromResult(SignedSaveAnswer.SaveAsNew);
                vm.PickSavePath = (_, name, _) =>
                {
                    suggested = name;
                    return Task.FromResult<string?>(copy);
                };
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal($"unterschrieben ({Tr.T("bearbeitet")}).pdf", suggested);
                Assert.Equal(bytes, File.ReadAllBytes(pdf));
                Assert.Equal(Path.GetFullPath(copy), doc.Path);
                using (var saved = PdfDocument.Open(copy))
                    Assert.Equal(0, DigitalSignatures.CountIntact(saved));

                // 4. the copy has no intact signature left: saving it again asks nothing
                questions.Clear();
                vm.ConfirmSignedSave = question =>
                {
                    questions.Add(question);
                    return Task.FromResult(SignedSaveAnswer.Cancel);
                };
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Empty(questions);

                // 5. the original once more, saved anyway: overwritten, the signature is broken
                await vm.OpenAsync([pdf]);
                var original = vm.SelectedDocument!;
                original.History.Rotate([0], 90);
                vm.ConfirmSignedSave = question =>
                {
                    questions.Add(question);
                    return Task.FromResult(SignedSaveAnswer.Save);
                };
                Assert.True(await vm.SaveDocumentAsync(original, original.Path, original.SaveVersion));
                Assert.Single(questions);
                Assert.NotEqual(bytes, File.ReadAllBytes(pdf));
                using (var overwritten = PdfDocument.Open(pdf))
                    Assert.Equal(0, DigitalSignatures.CountIntact(overwritten));

                // 6. Save As to another file shows the variant without „save as new file“
                await vm.OpenAsync([TestFiles.Write(bytes, "unterschrieben-2.pdf")]);
                var second = vm.SelectedDocument!;
                second.History.Rotate([0], 90);
                questions.Clear();
                Assert.True(await vm.SaveDocumentAsync(second, Path.Combine(Path.GetDirectoryName(pdf)!, "anderswo.pdf"), second.SaveVersion));
                Assert.False(Assert.Single(questions).OverwritesOriginal);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
