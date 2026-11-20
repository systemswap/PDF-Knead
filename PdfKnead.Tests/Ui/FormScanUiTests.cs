using PdfKnead.Core.Forms.Recognition;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Felder erkennen“ on a scan with the models in the window (KONZEPT.md, section 5.9a): the text recognition names the fields, the field
/// detector adds what the sheet does not draw (marked „KI“), and „Übernehmen“ also keeps the text read — unless unticked.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class FormScanUiTests
{
    private static byte[] Form() => FormScanTests.Scan(fill => fill(110, 697.5, 400, 698.5));

    private static FormScanTests.Reader Reader() => new(("Name:", 72, 698, 104, 708), ("Telefon:", 72, 643, 120, 653));

    private static FormScanTests.Detector Detector() => new((DetectedFieldKind.Text, 112, 699, 398, 712, 0.9), (DetectedFieldKind.Text, 130, 640, 400, 655, 0.8));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScannedForm_NamedByTheText_DetectorAdds_TextKeptIfTicked(bool keepText)
    {
        var pdf = TestFiles.Write(Form(), $"gescanntes formular {keepText}.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                vm.FieldModelsThere = () => (true, true);
                vm.OpenRecognizer = _ => Reader();
                vm.OpenFieldDetector = Detector;
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                await vm.RecognizeFieldsCommand.ExecuteAsync(null);
                Assert.Equal(["Name", "Telefon"], doc.FieldProposals.Select(r => r.Name));
                Assert.False(doc.FieldProposals[0].IsModel);
                Assert.True(doc.FieldProposals[1].IsModel);   // nothing drawn: only the detector saw it
                Assert.Contains("Texterkennung gelesen", doc.FieldRecognitionHint);
                Assert.Contains("Feld-Erkenner hat mitgesucht", doc.FieldRecognitionHint);
                Assert.True(doc.HasRecognizedText);
                Assert.True(doc.StoreRecognizedText);   // on by default
                doc.StoreRecognizedText = keepText;
                UiSession.Capture(window);

                await vm.AcceptFieldProposalsCommand.ExecuteAsync(null);
                Assert.Equal(2, doc.ElementsOn(0).Count(DocumentViewModel.IsFormField));
                var text = (await doc.GetTextAsync(0)).Text;
                Assert.Equal(keepText, text.Contains("Telefon"));
                // the fields are one step, the text another
                vm.UndoCommand.Execute(null);
                Assert.DoesNotContain(doc.ElementsOn(0), DocumentViewModel.IsFormField);
                if (keepText)
                {
                    vm.UndoCommand.Execute(null);
                    Assert.DoesNotContain("Telefon", (await doc.GetTextAsync(0)).Text);
                }
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void ScannedForm_WithoutModels_LinesFound_HintDamagedInstallation()
    {
        var pdf = TestFiles.Write(Form(), "gescanntes formular ohne modelle.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                vm.FieldModelsThere = () => (false, false);
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                await vm.RecognizeFieldsCommand.ExecuteAsync(null);
                Assert.Single(doc.FieldProposals);   // the line, without a name from its label
                Assert.Contains("keine Namen", doc.FieldRecognitionHint);
                Assert.Contains("neu entpacken", doc.FieldRecognitionHint);   // the models come with the program: missing means damaged
                Assert.False(doc.HasRecognizedText);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
