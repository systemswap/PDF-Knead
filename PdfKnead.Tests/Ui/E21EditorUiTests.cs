using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// M7, package E21 (docs/FEHLERGEDAECHTNIS.md, No. 1195): the arrow keys of the editor of own formatted text move by extended grapheme clusters
/// (UAX #29 as the runtime gives them) together with the units of the syllabic scripts; the backspace key keeps its rules.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class E21EditorUiTests
{
    /// <summary>
    /// UNI 13.23.1 sentences 21–24 (editor): in Kirat Rai consonant with a vowel in two parts, the left arrow from the end goes before the vowel
    /// (2, not between its parts), the right arrow from the start behind the consonant, then to the end; an emoji ZWJ sequence and a pair of
    /// regional indicators are passed in one step, Shift+Right selects e with its acute whole; the backspace key deletes one code point (the
    /// last emoji of the sequence, the acute alone). Before, the caret stopped between the parts.
    /// </summary>
    [Fact]
    public void Editor_ArrowKeysByGraphemeClusters_BackspaceByItsRules()
    {
        var path = TestFiles.Write(TestPdf.Simple(1).Build(), "e21-editor.pdf");
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

                var kiratRai = Editor("\U00016D44\U00016D63\U00016D67", 6);
                kiratRai.MoveLeft(false, false);
                Assert.Equal(2, kiratRai.Caret);
                kiratRai.SetCaret(0);
                kiratRai.MoveRight(false, false);
                Assert.Equal(2, kiratRai.Caret);
                kiratRai.MoveRight(false, false);
                Assert.Equal(6, kiratRai.Caret);

                var family = Editor("a\U0001F468\u200D\U0001F469\u200D\U0001F467b", 1);
                family.MoveRight(false, false);
                Assert.Equal(9, family.Caret);
                family.MoveLeft(false, false);
                Assert.Equal(1, family.Caret);
                family.SetCaret(9);
                family.Backspace(false);
                Assert.Equal("a\U0001F468\u200D\U0001F469\u200Db", family.Current.Content.Text);

                var flags = Editor("\U0001F1E9\U0001F1EA\U0001F1EB\U0001F1F7", 0);
                flags.MoveRight(false, false);
                Assert.Equal(4, flags.Caret);

                var accent = Editor("e\u0301x", 0);
                accent.MoveRight(true, false);
                Assert.Equal((0, 2), (accent.SelectionStart, accent.SelectionEnd));
                accent.SetCaret(2);
                accent.Backspace(false);
                Assert.Equal("ex", accent.Current.Content.Text);
            }
            finally
            {
                await doc.Renderer.WaitUntilUnused([doc.Document]).WaitAsync(TimeSpan.FromSeconds(10));
                doc.Dispose();
            }
        });
    }
}
