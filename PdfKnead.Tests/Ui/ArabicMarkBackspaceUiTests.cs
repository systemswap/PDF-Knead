using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// UAX #53, 5.6 sentences 3–5 (docs/FEHLERGEDAECHTNIS.md, No. 1180): in the editor of own formatted text the backspace key after Arabic marks
/// deletes the outermost mark as AMTRA stacks them, not the one typed last.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class ArabicMarkBackspaceUiTests
{
    /// <summary>
    /// UAX53 5.6 sentences 3–5: beh, fatha, shadda loses the fatha (the shadda stands nearer to the letter), beh, kasra, shadda the kasra; beh,
    /// shadda, kasra the kasra as before (typed last and outermost); alef-madda with fatha loses the madda, which stands outside the fatha
    /// (alef with fatha remains); after a CGJ only the marks after it count (beh, damma, CGJ, hamza loses the hamza); the caret moves back by
    /// what was deleted; with the caret inside the marks, or with Ctrl, the usual deletion. Before, the backspace key deleted in the order of the
    /// text: beh, fatha, shadda lost the shadda.
    /// </summary>
    [Fact]
    public void Backspace_AfterArabicMarks_DeletesTheOutermost()
    {
        var path = TestFiles.Write(TestPdf.Simple(1).Build(), "amtra-ruecktaste.pdf");
        UiSession.Run(async () =>
        {
            var doc = new DocumentViewModel(path, PdfDocument.Open(path));
            try
            {
                var cases = new (string Typed, int Caret, bool Word, string After, int CaretAfter)[]
                {
                    ("\u0628\u064E\u0651", 3, false, "\u0628\u0651", 2),
                    ("\u0628\u0650\u0651", 3, false, "\u0628\u0651", 2),
                    ("\u0628\u0651\u0650", 3, false, "\u0628\u0651", 2),
                    ("\u0622\u064E", 2, false, "\u0627\u064E", 2),
                    ("\u0628\u064F\u034F\u0654", 4, false, "\u0628\u064F\u034F", 3),
                    ("x \u0628\u064E\u0651 y", 5, false, "x \u0628\u0651 y", 4),
                    ("\u0628\u064E\u0651", 2, false, "\u0628\u0651", 1),
                    ("ab \u0628\u064E\u0651", 6, true, "ab ", 3),
                };
                foreach (var (typed, caret, word, after, caretAfter) in cases)
                {
                    var element = new RichTextElement
                    {
                        Rect = new PdfRectangle(50, 50, 300, 100),
                        Content = new RichText(typed, new RichStyle { Family = "Liberation Sans", Size = 12 }),
                    };
                    doc.AddElement(0, element, "Test");
                    var editor = new RichTextEditor(doc, new ElementRef(0, element), caret);
                    editor.Backspace(word);
                    Assert.Equal(after, editor.Current.Content.Text);
                    Assert.Equal(caretAfter, editor.Caret);
                }
            }
            finally
            {
                await doc.Renderer.WaitUntilUnused([doc.Document]).WaitAsync(TimeSpan.FromSeconds(10));
                doc.Dispose();
            }
        });
    }
}
