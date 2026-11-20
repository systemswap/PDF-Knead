using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Backdrop for blending: the page is an isolated group on a transparent backdrop (11.4.7), fill and stroke in one operator act
/// like a knockout group (11.7.4.4), and a knockout group with only one element is not a separate layer.
/// </summary>
public sealed class TransparencyBackdropTests
{
    private static SKBitmap Render(string content, string state, string blendMode)
    {
        var pdf = TestPdf.Simple(1);
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Get("MediaBox"), TestPdf.Box(0, 0, 100, 100));
        var resources = TestPdf.Dict(("ExtGState", TestPdf.Dict((state, TestPdf.Dict(("BM", TestPdf.N(blendMode)))))));
        page.Set(PdfName.Resources, resources);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content)));
        using var doc = PdfDocument.Open(pdf.Build());
        return SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new ImageCache());
    }

    [Fact]
    public void BlendModeOnEmptyPage_ShowsTheColor()
    {
        // /Color on an empty backdrop gives the colour (not blended with the white of the paper, which would give white); over black, the black's luminosity
        using var bitmap = Render("0 0 0 rg 50 0 50 100 re f /G gs 1 0.5 0 rg 0 0 100 100 re f", "G", "Color");
        var empty = bitmap.GetPixel(25, 50);
        Assert.True(empty.Red > 200 && empty.Green is > 90 and < 170 && empty.Blue < 60, empty.ToString());
        var overBlack = bitmap.GetPixel(75, 50);
        Assert.True(overBlack.Red < 30 && overBlack.Green < 30 && overBlack.Blue < 30, overBlack.ToString());
    }

    [Fact]
    public void FillAndStroke_StrokeDoesNotBlendWithItsOwnFill()
    {
        // B with Difference: fill and stroke yellow; the stroke blends with the backdrop, not with the fill (otherwise black)
        using var bitmap = Render("/D gs 1 1 0 rg 1 1 0 RG 6 w 20 20 60 60 re B", "D", "Difference");
        foreach (var (x, y) in new[] { (50, 50), (20, 50), (50, 80) })
        {
            var p = bitmap.GetPixel(x, y);
            Assert.True(p.Red > 220 && p.Green > 220 && p.Blue < 40, $"({x},{y}) {p}");
        }
    }

    [TestPdfsFact]
    public void Issue18032_WhiteTextOverBlendedGradient_IsVisible()
    {
        // Knockout group with one visible element: /Color gradient over white text; the text stays white, orange beside it
        var path = Path.Combine(Root(), "TestPDFs", "pdfjs", "issue18032.pdf");
        using var doc = PdfDocument.Open(path);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 2, new ImageCache());
        // The white letters lie at x 176…203, y 633…642 (PDF), page 792 high; the gradient to the right of them has no text
        int Y(double pdfY) => (int)((792 - pdfY) * 2);
        var letter = Enumerable.Range(352, 50).Select(x => bitmap.GetPixel(x, Y(637))).Max(c => c.Blue);
        var gradient = bitmap.GetPixel(2 * 270, Y(645));
        Assert.True(letter > 230, $"Text nicht weiß: Blau höchstens {letter}");
        Assert.True(gradient.Red > 200 && gradient.Blue < 150, $"Verlauf nicht orange: {gradient}");
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
}
