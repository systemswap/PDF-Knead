using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// M7, package E20 (docs/FEHLERGEDAECHTNIS.md, No. 1185): the editor of own formatted text moves and deletes by units of the syllabic
/// scripts, finds words per code point, takes control codes as separators and keeps the pairs of directional formatting characters.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class E20EditorUiTests
{
    /// <summary>
    /// UNI 16.4.2 sentences 85–86, 13.8.1 sentences 13–15, 23.1.2 sentences 1–28, 23.2.5 sentence 17 (editor): in „ក\u17D2ក“ with the caret at the
    /// end the left arrow goes before the whole unit (0: the first ka and the coeng are a grapheme cluster, the coeng and the second ka a unit of
    /// the script, No. 1195) and the backspace key deletes coeng and ka together (before the arrow went between them and the coeng stayed alone); Ctrl+Left goes to the start of a Mro word (before one UTF-16 unit); a typed form feed starts a paragraph; deleting
    /// „RLE b“ from „a RLE b PDF c“ leaves „ac“; the Arabic backspace rule of No. 1180 stays.
    /// </summary>
    [Fact]
    public void Editor_UnitsWordsSeparatorsAndPairs()
    {
        var path = TestFiles.Write(TestPdf.Simple(1).Build(), "e20-editor.pdf");
        UiSession.Run(async () =>
        {
            var doc = new DocumentViewModel(path, PdfDocument.Open(path));
            try
            {
                RichTextEditor Editor(string text, int caret)
                {
                    var element = new RichTextElement
                    {
                        Rect = new PdfRectangle(50, 50, 300, 100),
                        Content = new RichText(text, new RichStyle { Family = "Liberation Sans", Size = 12 }),
                    };
                    doc.AddElement(0, element, "Test");
                    return new RichTextEditor(doc, new ElementRef(0, element), caret);
                }

                var khmer = Editor("ក\u17D2ក", 3);
                khmer.MoveLeft(false, false);
                Assert.Equal(0, khmer.Caret);
                khmer.SetCaret(3);
                khmer.Backspace(false);
                Assert.Equal("ក", khmer.Current.Content.Text);

                var mro = Editor("ab \U00016A40\U00016A41\U00016A42", 9);
                mro.MoveLeft(false, true);
                Assert.Equal(3, mro.Caret);

                var devanagari = Editor("क\u093F क\u0940", 5);
                devanagari.MoveLeft(false, true);
                Assert.Equal(3, devanagari.Caret);

                var typed = Editor("", 0);
                typed.Type("eins\u000Czwei\u000Bdrei");
                Assert.Equal("eins\u2028zwei\u2028drei", typed.Current.Content.Text);   // FF and VT: line separator (No. 1196)

                var pairs = Editor("a\u202Bb\u202Cc", 0);
                pairs.Select(1, 3);
                pairs.DeleteSelection();
                Assert.Equal("ac", pairs.Current.Content.Text);
                Assert.Equal(1, pairs.Caret);

                var isolate = Editor("a\u2067b\u2069", 0);
                isolate.Select(1, 3);
                isolate.Type("x");
                Assert.Equal("ax", isolate.Current.Content.Text);

                var opened = Editor("ab", 1);
                opened.Type("\u202Bx");
                Assert.Equal("a\u202Bx\u202Cb", opened.Current.Content.Text);

                var arabic = Editor("ب\u064E\u0651", 3);
                arabic.Backspace(false);
                Assert.Equal("ب\u0651", arabic.Current.Content.Text);
            }
            finally
            {
                await doc.Renderer.WaitUntilUnused([doc.Document]).WaitAsync(TimeSpan.FromSeconds(10));
                doc.Dispose();
            }
        });
    }
}
