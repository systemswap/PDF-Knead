using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, FG 1251 (redaction check; found by the guard m7-abschluss): since the text extraction reads /ActualText (FG 1180), the step
/// "everything outside is still there, at its place" of the redaction check compared the replacement text instead of the characters the page
/// draws — a replacement text that goes on purpose with a bar (FG 624) counted as lost, and the drawn characters within such a sequence were
/// no longer checked at all. The check now reads the characters as drawn (<c>drawnText</c>); the replacement texts are checked by the fates
/// of the words.
/// </summary>
public sealed partial class M7NormTests
{
    private static (PdfKnead.Core.Pdf.Document.PdfDocument Doc, PdfKnead.Core.Pdf.Graphics.DisplayList List) Fg1251Page(string content) =>
        InterpreterTests.Page(content, _ => TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Dict(("Type", TestPdf.N("Font")),
            ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")), ("Encoding", TestPdf.N("WinAnsiEncoding"))))))));

    /// <summary>
    /// ISO 32000-2, 14.9.4 with 9.10.1: the same page read for the text extraction gives the replacement text, read with <c>drawnText</c> each
    /// drawn character with its own text at its own place — also in a nested sequence; the characters outside the sequence are the
    /// same in both readings.
    /// </summary>
    [Fact]
    public void Fg1251_DrawnText_EachCharacterWithItsOwnText_ReplacementOnlyForTheExtraction()
    {
        const string content = "BT /F1 20 Tf 20 100 Td (a ) Tj /Span <</ActualText (ersatz)>> BDC (struk) Tj /Span <</ActualText (Q)>> BDC (tur) Tj EMC EMC ( z) Tj ET";
        var (doc, list) = Fg1251Page(content);
        using (doc)
        {
            Assert.Equal("a ersatz z", PageText.Create(list.Text).Text);
            var drawn = PageInterpreter.Build(doc, doc.Pages[0], drawnText: true).Text;
            Assert.Equal("a struktur z", PageText.Create(drawn).Text);
            Assert.All(drawn.Where(g => g.Text.Trim().Length > 0), g => Assert.Equal(1, g.Text.Length));
            // the characters outside the sequence lie where they lie in the extraction
            var outside = list.Text.Where(g => g.Text is "a" or "z").Select(g => (g.X0, g.Y0)).ToList();
            Assert.Equal(outside, drawn.Where(g => g.Text is "a" or "z").Select(g => (g.X0, g.Y0)).ToList());
            // each drawn character of the sequence lies within the quadrilateral the replacement text was given
            var replacement = list.Text.Single(g => g.Text == "ersatz");
            Assert.All(drawn.Where(g => "struktur".Contains(g.Text, StringComparison.Ordinal) && g.Text.Length == 1),
                g => Assert.InRange((g.X0 + g.X1) / 2, replacement.X0 - 0.01, replacement.X1 + 0.01));
        }
    }
}
