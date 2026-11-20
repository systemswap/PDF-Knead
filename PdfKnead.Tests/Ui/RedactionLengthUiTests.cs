using Avalonia.Controls;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Länge geschwärzter Wörter verbergen“ in the redaction question (docs/RECHERCHE-VERSTECKEN.md, B-13): checked at first, the reply carries
/// the choice, and redacting in the open document follows it — with it the word after the bar moves to the step, without it it stays.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class RedactionLengthUiTests
{
    [Fact]
    public void HideLengths_CheckedAtFirst_RedactingFollowsTheChoice()
    {
        var pdf = TestFiles.Write(RedactionLengthTests.File(RedactionLengthTests.JustifiedLine), "laenge-verbergen.pdf");
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
                async Task<double> StartOf(string word)
                {
                    var text = await doc.GetTextAsync(0);
                    return text.Quads(Assert.Single(text.Find(word, matchCase: true)))[0].X0;
                }
                var before = await StartOf("sagt");
                var name = await StartOf("Mustermann");
                var text = await doc.GetTextAsync(0);
                doc.Select(0, Assert.Single(text.Find("Mustermann", matchCase: true)));
                vm.RedactTextCommand.Execute(null);
                Assert.Equal(1, doc.RedactionCount);
                async Task<double> Redact(bool hide)
                {
                    vm.ConfirmRedaction = question =>
                    {
                        var dialog = new RedactionWindow(question);
                        Assert.True(dialog.Reply(RedactionAnswer.Apply).HideLengths, "das Häkchen ist zuerst gesetzt");
                        dialog.FindControl<CheckBox>("HideLengths")!.IsChecked = hide;
                        return Task.FromResult(dialog.Reply(RedactionAnswer.Apply));
                    };
                    await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                    Assert.True(doc.HasAppliedRedactions, vm.Message);
                    var after = await StartOf("sagt");
                    vm.UndoCommand.Execute(null);   // back to the mark
                    return after;
                }
                // without hiding, the words of the redacted line start on the grid of half a point: „sagt“ stays within half a step
                var plain = await Redact(hide: false);
                Assert.InRange(plain, before - 0.26, before + 0.26);
                var moved = await Redact(hide: true);
                Assert.True(moved > before + 1, $"„sagt“ rückt nach rechts ({before} → {moved})");
                var gap = moved - name;
                var step = PdfKnead.Core.Pdf.Editing.Redactor.LengthStepEm * 12;
                Assert.True(Math.Abs(gap - Math.Round(gap / step) * step) <= 0.51, $"Lücke {gap}");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
