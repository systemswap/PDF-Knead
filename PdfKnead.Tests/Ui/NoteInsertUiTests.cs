using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Einfügen → „Notiz“ (KONZEPT.md, section 5.5a): a click places the bubble, the area „Notizen“ opens with the cursor in its text; text and name are
/// changed there and deleted with one step each; saved as a note annotation that other readers list, read back as a changeable note.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class NoteInsertUiTests
{
    [Fact]
    public void Insert_Write_Undo_Save_Reopen_Delete()
    {
        var pdf = TestFiles.Write(TestFiles.MinimalPdfBytes(), "notiz-einfuegen.pdf");
        var saved = Path.Combine(Path.GetDirectoryName(pdf)!, "notiz-einfuegen-gespeichert.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                var announced = false;
                vm.InsertNoteCommand.CanExecuteChanged += (_, _) => announced = true;
                await vm.OpenAsync([pdf]);
                Assert.True(announced);
                var doc = vm.SelectedDocument!;
                var view = window.GetVisualDescendants().OfType<DocumentView>().Single();
                vm.InsertNoteCommand.Execute(null);
                Assert.Equal(ViewTool.InsertNote, vm.Tool);
                UiSession.Capture(window);

                // a click on the page: the tip of the bubble there
                var at = view.TranslatePoint(view.PagePointToView(0, 100, 700), window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                var note = Assert.Single(doc.ElementsOn(0).OfType<NoteElement>());
                Assert.Equal(100, note.Rect.Left, 1);
                Assert.Equal(700, note.Rect.Bottom, 1);
                Assert.True(doc.IsNotesOpen);
                Assert.True(doc.SelectedNoteRow?.IsOwn);
                UiSession.Capture(window);
                var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NoteTextBox");
                // the area sets the focus through the dispatcher once it is laid out (under load later)
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (!box.IsFocused && waited.Elapsed < TimeSpan.FromSeconds(5)) Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.True(box.IsFocused);

                // text and name: one step each
                doc.NoteText = "Bitte den Betrag prüfen";
                doc.NoteAuthor = "Max Mustermann";
                Assert.Equal("Bitte den Betrag prüfen", doc.ElementsOn(0).OfType<NoteElement>().Single().Text);
                Assert.Equal("Bitte den Betrag prüfen", doc.SelectedNoteRow?.Text);
                Assert.Equal("Max Mustermann", DocumentViewModel.LastNoteAuthor);
                vm.UndoCommand.Execute(null);
                Assert.Equal("", doc.ElementsOn(0).OfType<NoteElement>().Single().Author);
                vm.RedoCommand.Execute(null);

                // saved: a note annotation other readers list
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(saved);
                await vm.SaveAsCommand.ExecuteAsync(null);
                using (var file = PdfDocument.Open(saved))
                {
                    var annot = Assert.IsType<PdfDictionary>(file.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0));
                    Assert.Equal("Text", annot.GetName(PdfName.Subtype)?.Value);
                    Assert.Equal("Bitte den Betrag prüfen", annot.GetString(PdfName.Contents)!.ToText());
                    Assert.Equal("Max Mustermann", annot.GetString(PdfName.Get("T"))!.ToText());
                    Assert.NotNull(annot.GetDictionary(PdfName.Get("AP")));
                }

                // read back: a note of PDF Knead again (changeable), not a foreign annotation
                using (var file = PdfDocument.Open(saved))
                {
                    var back = Assert.Single(PageItem.From(file)[0].Elements.OfType<NoteElement>());
                    Assert.Equal("Bitte den Betrag prüfen", back.Text);
                    Assert.Equal("Max Mustermann", back.Author);
                    Assert.Empty(PageItem.From(file)[0].Elements.OfType<ForeignAnnotation>());
                }

                // deleted in the area, one step back brings it again
                var row = Assert.Single(doc.NoteRows);
                Assert.True(row.IsOwn);
                doc.SelectedNoteRow = row;
                doc.DeleteNoteCommand.Execute(null);
                Assert.Empty(doc.ElementsOn(0).OfType<NoteElement>());
                Assert.True(doc.NotesNothing);
                vm.UndoCommand.Execute(null);
                Assert.Single(doc.ElementsOn(0).OfType<NoteElement>());
            }
            finally
            {
                window.Close();
            }
        });
    }
}
