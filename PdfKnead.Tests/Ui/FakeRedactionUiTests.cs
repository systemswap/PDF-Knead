using Avalonia.Media.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The warning about a redaction that is none (KONZEPT.md, section 5.6, „Vorgetäuschte Schwärzung“): after opening, the bar names the page;
/// „Richtig schwärzen“ marks the place, applying and saving removes the text from the file, and the saved file raises no warning.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class FakeRedactionUiTests
{
    [Fact]
    public void Warned_MarkedProperly_TextGoneAfterwards()
    {
        var file = TestFiles.Write(FakeRedactionTests.Page(
            "BT /F1 12 Tf 72 700 Td (Max Mustermann 4711) Tj ET 0 g 66 694 160 22 re f BT /F1 12 Tf 72 600 Td (Sichtbar bleibt) Tj ET"), "vorgetaeuscht.pdf");
        var target = Path.Combine(Path.GetDirectoryName(file)!, "richtig-geschwaerzt.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                await doc.CheckFakeRedactionsAsync();
                Assert.True(doc.FakeRedactionWarningVisible);
                Assert.Contains(Tr.T("Seite {0}", 1), doc.FakeRedactionText);
                if (UiSession.ScreenshotFolder() is { } folder)
                    UiSession.Capture(window).Save(Path.Combine(folder, "79-vorgetaeuschte-schwaerzung.png"), PngBitmapEncoderOptions.Default);

                doc.MarkFakeRedactionsCommand.Execute(null);
                Assert.False(doc.FakeRedactionWarningVisible);
                Assert.Single(doc.History.Pages[0].Elements.OfType<RedactionElement>());

                vm.ConfirmRedaction = question =>
                {
                    var dialog = new RedactionWindow(question);
                    dialog.Show();
                    var reply = dialog.Reply(RedactionAnswer.Apply);
                    dialog.Close();
                    return Task.FromResult(reply);
                };
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                await vm.SaveAsCommand.ExecuteAsync(null);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
        var saved = File.ReadAllBytes(target);
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(saved, ["Mustermann"], null, out _));
        using var doc = PdfDocument.Open(saved);
        Assert.Contains("Sichtbar", string.Concat(Core.Pdf.Content.PageInterpreter.BuildText(doc, doc.Pages[0]).Select(g => g.Text)));
        Assert.Empty(PdfKnead.Rendering.FakeRedactions.Find(doc, doc.Pages[0], new PdfKnead.Rendering.ImageCache()));
    }
}
