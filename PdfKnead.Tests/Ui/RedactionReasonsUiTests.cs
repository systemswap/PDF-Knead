using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Jetzt schwärzen?“ with reasons (KONZEPT.md, section 5.6, B-14): a reason for all marks without their own; the reason in the bar (as
/// outlines, no text in the bar), a note at the bar, the protocol as first page — never the content; a reason naming what is redacted is
/// warned about; one undo step takes it all back; without a reason everything is as before.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class RedactionReasonsUiTests
{
    private static byte[] File() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
        [6] = ("", "BT /F1 12 Tf 72 700 Td (Kunde Mustermann zahlt) Tj ET BT /F1 12 Tf 72 600 Td (Konto 4711 bleibt geheim) Tj ET"),
    });

    private static string Text(PdfDocument doc, int page) => PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[page]), 0).Text;

    [Fact]
    public void ReasonsInBarsNotesAndProtocol_NeverTheContent_OneUndoStep()
    {
        var pdf = TestFiles.Write(File(), "schwaerzen-gruende.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var text = await doc.GetTextAsync(0);
                foreach (var word in (string[])["Mustermann", "4711"])
                    doc.AddElement(0, RedactionElement.FromPageAreas(text.RedactionAreas(Assert.Single(text.Find(word, matchCase: true)))), "Test");
                // the second mark gets its own reason in the panel on the right
                var marks = doc.History.Pages[0].Elements.OfType<RedactionElement>().ToList();
                doc.SelectedElement = new ElementRef(0, marks.Single(m => m.Rect.Bottom < 650));
                vm.ElementProperties.Refresh(doc);
                Assert.True(vm.ElementProperties.IsRedaction);
                vm.ElementProperties.Reason = "Betriebs- und Geschäftsgeheimnis (§ 6 IFG)";
                RedactionWindow? asked = null;
                vm.ConfirmRedaction = question =>
                {
                    var dialog = asked = new RedactionWindow(question);
                    dialog.Show();
                    Assert.Equal(1, question.OwnReasons);
                    // a reason naming what is redacted is warned about
                    var reason = dialog.FindControl<AutoCompleteBox>("Reason")!;
                    reason.Text = "Kunde Mustermann";
                    Assert.True(dialog.FindControl<TextBlock>("ReasonWarning")!.IsVisible);
                    reason.Text = "Personenbezogene Daten (Art. 6 DSGVO)";
                    Assert.False(dialog.FindControl<TextBlock>("ReasonWarning")!.IsVisible);
                    dialog.FindControl<CheckBox>("ReasonNotes")!.IsChecked = true;
                    dialog.FindControl<TextBox>("ReasonName")!.Text = "MM";
                    var reply = dialog.Reply(RedactionAnswer.Apply);
                    dialog.Close();
                    return Task.FromResult(reply);
                };
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.NotNull(asked);
                Assert.True(doc.HasAppliedRedactions, vm.Message);
                // the protocol first, then the page; neither has a redacted word, the page has no reason as text
                Assert.Equal(2, doc.Document.PageCount);
                var protocol = Text(doc.Document, 0);
                Assert.Contains("Schwärzungsprotokoll", protocol);
                Assert.Contains("Personenbezogene Daten (Art. 6 DSGVO)", protocol);
                Assert.Contains("Betriebs- und Geschäftsgeheimnis (§ 6 IFG)", protocol);
                Assert.Contains("MM", protocol);
                Assert.DoesNotContain("Mustermann", protocol);
                Assert.DoesNotContain("4711", protocol);
                var page = Text(doc.Document, 1);
                Assert.DoesNotContain("Mustermann", page);
                Assert.DoesNotContain("DSGVO", page);
                // the reason drawn in the bar, the notes at the bars
                var notes = doc.Document.Pages[1].Dictionary.GetArray(PdfName.Get("Annots"));
                var contents = Enumerable.Range(0, notes?.Count ?? 0).Select(i => notes!.Get(i)).OfType<PdfDictionary>()
                    .Select(a => a.GetString(PdfName.Get("Contents"))?.ToText()).ToList();
                Assert.Equal(2, contents.Count);
                Assert.Contains(contents, c => c!.Contains("Art. 6 DSGVO"));
                Assert.Equal("MM", PdfKnead.Services.UserSettings.Current.RedactionName);
                // one undo step
                vm.UndoCommand.Execute(null);
                Assert.Equal(1, doc.Document.PageCount);
                Assert.Contains("Mustermann", Text(doc.Document, 0));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void RedactionTool_MarkJustDrawnWithoutPanel_PickedByAClickWithIt()
    {
        var pdf = TestFiles.Write(File(), "schwaerzen-grund-im-werkzeug.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.Tool = ViewTool.InsertRedaction;
                var text = await doc.GetTextAsync(0);
                // just drawn: no panel, it would cover the page where the next bar is drawn
                doc.AddElement(0, RedactionElement.FromPageAreas(text.RedactionAreas(Assert.Single(text.Find("Mustermann", matchCase: true)))), "Test");
                Assert.False(vm.ElementProperties.IsVisible);
                // picked by a click, still in the tool (wish of 4.11.2026): with the panel and its reason
                doc.MarkPicked = true;
                Assert.True(vm.ElementProperties.IsVisible);
                Assert.True(vm.ElementProperties.IsRedaction);
                // no opacity for a bar: it always covers fully (report of 4.11.2026: the slider moved, the percentage stayed at 100)
                UiSession.Capture(window);
                var slider = window.GetVisualDescendants().OfType<ElementPropertiesPanel>().Single().GetVisualDescendants().OfType<Slider>()
                    .Single(s => Avalonia.Automation.AutomationProperties.GetName(s) == Tr.T("Deckkraft"));
                Assert.False(slider.IsEffectivelyVisible);
                vm.ElementProperties.Reason = "Schutz Dritter";
                Assert.True(vm.ElementProperties.IsVisible);   // the mark replaced by one with the reason stays picked
                // the next one drawn: without again
                doc.AddElement(0, RedactionElement.FromPageAreas(text.RedactionAreas(Assert.Single(text.Find("4711", matchCase: true)))), "Test");
                Assert.False(vm.ElementProperties.IsVisible);
                Assert.Contains(doc.History.Pages[0].Elements.OfType<RedactionElement>(), m => m.Reason == "Schutz Dritter");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void WithoutAReason_RedactedAsBefore()
    {
        var pdf = TestFiles.Write(File(), "schwaerzen-ohne-grund.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var text = await doc.GetTextAsync(0);
                doc.Select(0, Assert.Single(text.Find("Mustermann", matchCase: true)));
                vm.RedactTextCommand.Execute(null);
                vm.ConfirmRedaction = question =>
                {
                    var dialog = new RedactionWindow(question);
                    Assert.False(dialog.FindControl<StackPanel>("ReasonPanel")!.IsEnabled);
                    return Task.FromResult(dialog.Reply(RedactionAnswer.Apply));
                };
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.Equal(1, doc.Document.PageCount);
                Assert.IsNotType<PdfArray>(doc.Document.Pages[0].Dictionary.Get(PdfName.Get("Annots")));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// „Jetzt schwärzen?“ says where the reason for all does not fit into a bar, not even as its code (before only the panel on the right and
    /// „Suchen und schwärzen“ said so, for a code; docs/FEHLERGEDAECHTNIS.md, No. 529): a long reason with a code fits a short bar, one
    /// without does not; with „in die Balken“ off nothing is said.
    /// </summary>
    [Fact]
    public void ReasonForAll_DoesNotFitSomeBars_Told()
    {
        UiSession.Run(() =>
        {
            var question = new RedactionQuestion(2, 1, null, CanChooseImages: true)
            {
                BarsWithoutReason = [new PdfRectangle(86, 20.7, 124.7, 28.3), new PdfRectangle(100, 700, 400, 714)],
            };
            var dialog = new RedactionWindow(question);
            dialog.Show();
            try
            {
                var warning = dialog.FindControl<TextBlock>("FitWarning")!;
                var reason = dialog.FindControl<AutoCompleteBox>("Reason")!;
                reason.Text = "Personenbezogene Daten (Art. 6 DSGVO)";
                Assert.False(warning.IsVisible);
                reason.Text = "Schutz der Rechte Dritter nach dem Vertrag";
                Assert.True(warning.IsVisible);
                Assert.Equal(Tr.T("Passt in {0} von {1} Balken nicht — dort wird er weggelassen, im Schwärzungsprotokoll steht er.", 1, 2), warning.Text);
                dialog.FindControl<CheckBox>("ReasonInBars")!.IsChecked = false;
                Assert.False(warning.IsVisible);
            }
            finally
            {
                dialog.Close();
            }
        });
    }
}
