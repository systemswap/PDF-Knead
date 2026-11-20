using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Felder erkennen“ (KONZEPT.md, section 5.9a): the proposals on the right and dashed on the page, a click on a frame unticks it, a name
/// changed in the list, „Übernehmen“ as one undo step and then „Formular bearbeiten“; a second run proposes nothing where fields now lie.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class FormRecognitionUiTests
{
    private static byte[] Form() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
        [6] = ("", "BT /F1 10 Tf 72 760 Td (Name:) Tj ET 0.7 w 110 758 m 400 758 l S " +
                   "BT /F1 10 Tf 72 720 Td (Geburtsdatum:) Tj ET 0.7 w 150 718 m 400 718 l S " +
                   "0.7 w 72 680 10 10 re S BT /F1 10 Tf 88 682 Td (Newsletter) Tj ET " +
                   "0.7 w 320 600 m 520 600 l S BT /F1 8 Tf 320 590 Td (Unterschrift) Tj ET"),
    });

    [Fact]
    public void Propose_UntickOnThePage_Rename_TakeOverAsOneStep()
    {
        var pdf = TestFiles.Write(Form(), "felder-erkennen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                var announced = false;
                vm.RecognizeFieldsCommand.CanExecuteChanged += (_, _) => announced = true;
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var view = window.GetVisualDescendants().OfType<PdfKnead.Controls.DocumentView>().Single();
                // the button in the ribbon becomes usable once a document is open (it is told so)
                Assert.True(announced);
                Assert.True(vm.RecognizeFieldsCommand.CanExecute(null));
                await vm.RecognizeFieldsCommand.ExecuteAsync(null);
                Assert.True(doc.IsFieldProposalsOpen);
                Assert.True(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "FormProposalsHost").IsVisible);
                Assert.Equal(["Name", "Geburtsdatum", "Newsletter", "Unterschrift"], doc.FieldProposals.Select(r => r.Name));
                Assert.All(doc.FieldProposals, r => Assert.True(r.IsChecked));
                UiSession.Capture(window);
                Assert.Equal(4, view.ProposalFrames.Count);

                // a click on the frame of „Newsletter“ unticks it
                var (frame, row) = view.ProposalFrames.Single(f => f.Row.Name == "Newsletter");
                var at = view.TranslatePoint(frame.Center, window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Assert.False(row.IsChecked);

                // nothing ticked: „Übernehmen“ creates nothing, says so and leaves the proposals open
                vm.TickFieldProposalsCommand.Execute("false");
                vm.AcceptFieldProposalsCommand.Execute(null);
                Assert.True(doc.IsFieldProposalsOpen);
                Assert.Contains("Kein Vorschlag angehakt", vm.Notice);
                Assert.DoesNotContain(doc.ElementsOn(0), DocumentViewModel.IsFormField);
                vm.TickFieldProposalsCommand.Execute("true");
                row.IsChecked = false;

                doc.FieldProposals.Single(r => r.Name == "Name").Name = "Nachname";
                // a name twice: the second field gets a name of its own (one name would be two fields in the file anyway)
                doc.FieldProposals.Single(r => r.Name == "Geburtsdatum").Name = "Nachname";

                vm.AcceptFieldProposalsCommand.Execute(null);
                Assert.False(doc.IsFieldProposalsOpen);
                Assert.True(vm.IsFormDesign);
                var elements = doc.ElementsOn(0);
                var fields = elements.OfType<FieldElement>().ToList();
                Assert.Equal(["Nachname", "Nachname_2"], fields.Select(f => f.Spec.Name));
                Assert.Equal(FieldFormatKind.Date, fields[1].Spec.Format.Kind);
                Assert.Equal("Unterschrift", Assert.Single(elements.OfType<SignatureFieldElement>()).FieldName);

                // one step back takes all of them
                vm.UndoCommand.Execute(null);
                Assert.DoesNotContain(doc.ElementsOn(0), DocumentViewModel.IsFormField);
                vm.RedoCommand.Execute(null);

                // again: only the check box left out before is proposed
                vm.Tool = ViewTool.SelectText;
                await vm.RecognizeFieldsCommand.ExecuteAsync(null);
                Assert.Equal(["Newsletter"], doc.FieldProposals.Select(r => r.Name));
                vm.CloseFieldProposalsCommand.Execute(null);
                Assert.False(doc.IsFieldProposalsOpen);
                UiSession.Capture(window);
                Assert.Empty(view.ProposalFrames);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
