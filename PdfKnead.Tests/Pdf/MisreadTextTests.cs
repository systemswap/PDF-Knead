using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Text that reads suspiciously (docs/RECHERCHE-VERSTECKEN.md, B-5): a font whose different shapes all read as the same text, or as characters
/// of the Private Use Area, is counted, so that „Suchen und schwärzen“ can name the page; ordinary text, and a few bullets, are not.
/// </summary>
public sealed class MisreadTextTests
{
    /// <summary>
    /// A page with a Type 3 font of six different shapes (boxes of different sizes) whose ToUnicode maps code n to <paramref name="texts"/>[n - 1],
    /// drawing <paramref name="codes"/>, and a line of ordinary text.
    /// </summary>
    internal static byte[] File(string[] texts, string codes)
    {
        var procs = string.Join("", Enumerable.Range(1, 6).Select(n => $"/g{n} {10 + n} 0 R"));
        var map = string.Join(" ", texts.Select((t, i) => $"<{i + 1:X2}> <{string.Concat(t.Select(c => ((int)c).ToString("X4")))}>"));
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources<</Font<</F1 3 0 R/T3 5 0 R>>>>>>",
            [5] = $"<</Type/Font/Subtype/Type3/FontBBox[0 0 750 750]/FontMatrix[0.001 0 0 0.001 0 0]/CharProcs<<{procs}>>" +
                  "/Encoding<</Type/Encoding/Differences[1/g1/g2/g3/g4/g5/g6]>>/FirstChar 1/LastChar 6/Widths[800 800 800 800 800 800]/ToUnicode 9 0 R>>",
            [7] = ("", $"BT /T3 20 Tf 72 700 Td <{codes}> Tj ET BT /F1 12 Tf 72 600 Td (Lesbarer Text mit vielen Buchstaben) Tj ET"),
            [9] = ("", "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /T3 def 1 begincodespacerange <00> <FF> endcodespacerange " +
                       $"{texts.Length} beginbfchar {map} endbfchar endcmap CMapName currentdict /CMap defineresource pop end end"),
        };
        for (var n = 1; n <= 6; n++) objects[10 + n] = ("", $"800 0 0 0 {100 * n} {100 + 50 * n} d1 0 0 {100 * n} {100 + 50 * n} re f");
        return CraftedFileTests.Build(objects);
    }

    private static int Misread(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        var (glyphs, unreadable, misread) = PageInterpreter.BuildTextAndUnreadable(doc, doc.Pages[0]);
        Assert.Equal(misread, PageInterpreter.Build(doc, doc.Pages[0]).MisreadGlyphs);   // the same count from the display list
        Assert.Equal(misread, PageText.Create(glyphs, unreadable, misread).MisreadGlyphs);
        return misread;
    }

    [Fact]
    public void DifferentShapesReadingTheSame_Counted()
    {
        Assert.Equal(7, Misread(File(["x", "x", "x", "x", "x", "x"], "01020304050601")));
        Assert.Equal(6, Misread(File([" ", " ", " ", " ", " ", " "], "010203040506")));   // all read as spaces
        Assert.Equal(1, Misread(File(["\uFFFD", "b", "c", "d", "e", "f"], "0102")));   // „no character“: from the first shape
    }

    [Fact]
    public void DifferentShapesInThePrivateUseArea_Counted()
    {
        Assert.Equal(6, Misread(File(["\uE000", "\uE001", "\uE002", "\uE003", "\uE004", "\uE005"], "010203040506")));
    }

    [Fact]
    public void OrdinaryTextAndAFewBullets_NothingCounted()
    {
        Assert.Equal(0, Misread(File(["a", "b", "c", "d", "e", "f"], "010203040506")));
        Assert.Equal(0, Misread(File(["x", "x", "x", "d", "e", "f"], "0102030102030405")));   // three shapes the same: still usual
        Assert.Equal(0, Misread(File(["\uE000", "\uE001", "c", "d", "e", "f"], "0101010202020102")));   // bullets of a symbol font
        Assert.Equal(0, Misread(File(["\uF8EB", "\uF8EC", "\uF8ED", "\uF8F6", "\uF8F7", "\uF8F8"], "010203040506")));   // pieces of large brackets
        Assert.Equal(0, Misread(UnreadableTextTests.File()));
    }
}
