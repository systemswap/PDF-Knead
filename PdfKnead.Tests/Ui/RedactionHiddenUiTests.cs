using Avalonia.Controls;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Jetzt schwärzen?“ removes what nobody sees and the old text recognition along with the bars (KONZEPT.md, section 5.6, „Schwärzen-Umbau“):
/// both checked at first, in the whole document; „Alte Texterkennung“ only if the document has one, keeping it shows a warning; „nur auf
/// Seiten mit Schwärzung“ leaves the other pages as they are — also when saving later; probably invisible text (white on white) stays for
/// „Verborgenes schwärzen“.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class RedactionHiddenUiTests
{
    /// <summary>Two pages, each with visible text, invisible text (OCR), text on a hidden layer and white text on white; the name on page 1 is redacted.</summary>
    private static byte[] File()
    {
        const string Page = "BT /F1 12 Tf 72 700 Td (Name Mustermann{0}) Tj ET q BT 3 Tr /F1 12 Tf 72 660 Td (Ocr{0}) Tj ET Q " +
                            "/OC /L1 BDC BT /F1 12 Tf 72 640 Td (Ebene{0}) Tj ET EMC 1 g BT /F1 12 Tf 72 620 Td (Weiss{0}) Tj ET";
        return CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/OCProperties<</OCGs[9 0 R]/D<</OFF[9 0 R]>>>>>>",
            [2] = "<</Type/Pages/Kids[4 0 R 5 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 3 0 R>>/Properties<</L1 9 0 R>>>>>>",
            [5] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources<</Font<</F1 3 0 R>>/Properties<</L1 9 0 R>>>>>>",
            [6] = ("", string.Format(Page, "Eins")),
            [7] = ("", string.Format(Page, "Zwei")),
            [9] = "<</Type/OCG/Name(Intern)>>",
        });
    }

    [Fact]
    public void HiddenAndOcrGoAlong_WholeDocumentOrRedactedPagesOnly()
    {
        var pdf = TestFiles.Write(File(), "schwaerzen-verborgenes.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Secure;
                var text = await doc.GetTextAsync(0);
                doc.Select(0, Assert.Single(text.Find("MustermannEins", matchCase: true)));
                vm.RedactTextCommand.Execute(null);

                // what is left per page: the hidden spots (kind and text), and whether the redacted name is still there
                string Left(PdfDocument d) => string.Join(" | ", d.Pages.Select(p =>
                    string.Join(", ", HiddenText.Find(d, p).Select(h => $"{h.Kind}:{h.Text.Trim()}")) +
                    (PageText.Create(Core.Pdf.Content.PageInterpreter.BuildText(d, p), 0).Text.Contains("MustermannEins") ? " / MustermannEins" : "")));
                Assert.Equal("Invisible:OcrEins, HiddenLayer:EbeneEins, Suspicious:WeissEins / MustermannEins | " +
                             "Invisible:OcrZwei, HiddenLayer:EbeneZwei, Suspicious:WeissZwei", Left(doc.Document));
                async Task<string> Redact(Action<RedactionWindow> choose)
                {
                    vm.ConfirmRedaction = question =>
                    {
                        Assert.Equal([0, 1], question.OcrPages);
                        var dialog = new RedactionWindow(question);
                        choose(dialog);
                        return Task.FromResult(dialog.Reply(RedactionAnswer.Apply));
                    };
                    await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                    Assert.True(doc.HasAppliedRedactions, vm.Message);
                    return Left(doc.Document);
                }

                // the safe choices at first; keeping the old text recognition shows the warning
                var left = await Redact(dialog =>
                {
                    var ocr = dialog.FindControl<CheckBox>("RemoveOcr")!;
                    Assert.True(ocr.IsVisible);
                    Assert.False(dialog.FindControl<TextBlock>("OcrWarning")!.IsVisible);
                    var reply = dialog.Reply(RedactionAnswer.Apply);
                    Assert.True(reply.CleanHidden && reply.RemoveOcr && reply.WholeDocument);
                    ocr.IsChecked = false;
                    Assert.True(dialog.FindControl<TextBlock>("OcrWarning")!.IsVisible);
                    ocr.IsChecked = true;
                });
                // white on white stays: the user decides about it in „Verborgenes schwärzen“
                Assert.Equal("Suspicious:WeissEins | Suspicious:WeissZwei", left);
                vm.UndoCommand.Execute(null);

                // the old text recognition kept: it stays, hidden layers go
                left = await Redact(dialog => dialog.FindControl<CheckBox>("RemoveOcr")!.IsChecked = false);
                Assert.Equal("Invisible:OcrEins, Suspicious:WeissEins | Invisible:OcrZwei, Suspicious:WeissZwei", left);
                vm.UndoCommand.Execute(null);

                // only on the page with the redaction: page 2 keeps its text recognition and its layer, also when saved later
                left = await Redact(dialog => dialog.FindControl<RadioButton>("RedactedPagesOnly")!.IsChecked = true);
                Assert.Equal("Suspicious:WeissEins | Invisible:OcrZwei, HiddenLayer:EbeneZwei, Suspicious:WeissZwei", left);
                Assert.Single(DocumentCleanup.HiddenLayerNames(doc.Document));
                Assert.Equal(CleanupItems.None, doc.Cleaned & DocumentCleanup.PageItems);
                var copy = Path.Combine(Path.GetDirectoryName(pdf)!, "schwaerzen-verborgenes-kopie.pdf");
                await doc.SaveAsync(copy, doc.SaveOptions("1.7"));
                using (var saved = PdfDocument.Open(System.IO.File.ReadAllBytes(copy)))
                    Assert.Equal(left, Left(saved));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
