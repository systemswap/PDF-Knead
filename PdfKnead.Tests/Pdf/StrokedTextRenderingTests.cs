using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Text filled and stroked with a thin outline (render mode 2 — a bold a printer driver makes from a regular font with a 0.2 pt outline): its
/// outline is drawn in its real width, never enhanced to a full pixel like a thin line. Enhanced, it closed the counters of small letters and
/// a footer in such a bold could not be read below about 375 % (docs/FEHLERGEDAECHTNIS.md, No. 816).
/// </summary>
public sealed class StrokedTextRenderingTests
{
    /// <summary>Two lines of 7 pt text, the same words: at y 70 filled only, at y 30 filled and stroked with a 0.2 pt outline.</summary>
    private static byte[] TwoLines() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 200 100]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
        [4] = ("", "BT /F1 7 Tf 10 70 Td (Max Mustermann aus Musterstadt) Tj ET\n0.2 w BT /F1 7 Tf 2 Tr 10 30 Td (Max Mustermann aus Musterstadt) Tj ET"),
        [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
    });

    /// <summary>Ink of a band of rows (sum of 255 − red).</summary>
    private static long Ink(SKBitmap b, int top, int bottom)
    {
        long sum = 0;
        for (var y = Math.Max(0, top); y < Math.Min(b.Height, bottom); y++)
            for (var x = 0; x < b.Width; x++)
                sum += 255 - b.GetPixel(x, y).Red;
        return sum;
    }

    /// <summary>
    /// At 74 % and 90 % (71 and 86 dpi, as the viewer draws them) the stroked line carries only somewhat more ink than the filled one — its
    /// 0.2 pt outline is a fifth of a pixel, smoothed along every edge (measured 1.34 and 1.41). Enhanced to a full pixel it carried 2.4 and 2.3
    /// times as much and its letters ran together.
    /// </summary>
    [Theory]
    [InlineData(71 / 72.0)]
    [InlineData(86 / 72.0)]
    public void ThinOutline_NotEnhancedToAFullPixel(double pixelsPerPoint)
    {
        using var doc = PdfDocument.Open(TwoLines());
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        using var image = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], pixelsPerPoint, new ImageCache());
        var rowsPerPoint = image.Height / 100.0;
        var filled = Ink(image, (int)((100 - 80) * rowsPerPoint), (int)((100 - 66) * rowsPerPoint));
        var stroked = Ink(image, (int)((100 - 40) * rowsPerPoint), (int)((100 - 26) * rowsPerPoint));
        Assert.True(filled > 0);
        Assert.InRange(stroked / (double)filled, 1.0, 1.6);
    }

    /// <summary>At a size where the outline is wider than a pixel it is drawn as before: the line is clearly bolder.</summary>
    [Fact]
    public void WideOutline_StillBolder()
    {
        using var doc = PdfDocument.Open(TwoLines());
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        using var image = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 8, new ImageCache());
        var rowsPerPoint = image.Height / 100.0;
        var filled = Ink(image, (int)((100 - 80) * rowsPerPoint), (int)((100 - 66) * rowsPerPoint));
        var stroked = Ink(image, (int)((100 - 40) * rowsPerPoint), (int)((100 - 26) * rowsPerPoint));
        Assert.True(stroked > filled * 1.05);
    }
}
