using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Text that cannot be read (docs/RECHERCHE-VERSTECKEN.md, B-6): characters drawn by a font without any way from its codes to text are not
/// in the page text — no search finds them. They are counted, so that „Suchen und schwärzen“ can name their pages.
/// </summary>
public sealed class UnreadableTextTests
{
    /// <summary>A page with three characters of a Type 3 font whose glyph names mean nothing, and a line of ordinary text.</summary>
    internal static byte[] File() => CraftedFileTests.Build(new()
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources<</Font<</F1 3 0 R/T3 5 0 R>>>>>>",
        [5] = "<</Type/Font/Subtype/Type3/FontBBox[0 0 750 750]/FontMatrix[0.001 0 0 0.001 0 0]/CharProcs<</q17 6 0 R>>" +
              "/Encoding<</Type/Encoding/Differences[1/q17]>>/FirstChar 1/LastChar 1/Widths[800]>>",
        [6] = ("", "800 0 0 0 750 750 d1 0 0 750 750 re f"),
        [7] = ("", "BT /T3 20 Tf 72 700 Td <010101> Tj ET BT /F1 12 Tf 72 600 Td (Lesbarer Text) Tj ET"),
    });

    [Fact]
    public void CharactersWithoutText_Counted_NotInTheText()
    {
        using var doc = PdfDocument.Open(File());
        var (glyphs, unreadable, _) = PageInterpreter.BuildTextAndUnreadable(doc, doc.Pages[0]);
        Assert.Equal(3, unreadable);
        var text = PageText.Create(glyphs, unreadable);
        Assert.Equal(3, text.UnreadableGlyphs);
        Assert.Contains("Lesbarer Text", text.Text);
        Assert.Equal(3, PageInterpreter.Build(doc, doc.Pages[0]).UnreadableGlyphs);   // the same count from the display list
    }

    [Fact]
    public void OrdinaryText_NothingUnreadable()
    {
        using var doc = PdfDocument.Open(CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
            [7] = ("", "BT /F1 12 Tf 72 600 Td (Lesbarer Text) Tj 3 Tr (Unsichtbar) Tj ET"),
        }));
        Assert.Equal(0, PageInterpreter.BuildTextAndUnreadable(doc, doc.Pages[0]).Unreadable);
    }
}
