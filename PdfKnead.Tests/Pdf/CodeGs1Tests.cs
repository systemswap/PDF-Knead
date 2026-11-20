using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Codes against the GS1 General Specifications (phase 8.3, docs/FEHLERGEDAECHTNIS.md, No. 608): quiet zones of EAN/UPC, GS1-128 and
/// ITF-14, and the bearer bars of ITF-14.
/// </summary>
public sealed class CodeGs1Tests
{
    /// <summary>Quiet zones in modules as GS1 gives them (EAN/UPC per version, GS1-128 and ITF-14 ten on each side).</summary>
    [Theory]
    [InlineData(BarcodeFormat.Ean13, "4006381333931", 11, 7)]
    [InlineData(BarcodeFormat.Ean8, "52364228", 7, 7)]
    [InlineData(BarcodeFormat.UpcA, "036000291452", 9, 9)]
    [InlineData(BarcodeFormat.UpcE, "01234565", 9, 7)]
    [InlineData(BarcodeFormat.Gs1_128, "(01)04012345678901(10)A1", 10, 10)]
    [InlineData(BarcodeFormat.Itf, "12345678901231", 10, 10)]
    public void QuietZones_AsGs1Gives(BarcodeFormat format, string text, int left, int right)
    {
        var symbol = CodeSymbol.Create(format, text);
        Assert.Equal((left, right), (symbol.QuietLeft, symbol.QuietRight));
    }

    /// <summary>
    /// ITF-14 (fourteen digits) carries bearer bars at the top and bottom of its bars, at least twice the narrow bar thick — GS1 makes them
    /// mandatory unless technically not feasible; printing without plates needs only those two. The code still reads back. A shorter ITF
    /// has none.
    /// </summary>
    [Fact]
    public void Itf14_HasBearerBars_StillReads()
    {
        var code = new CodeElement { Format = BarcodeFormat.Itf, Text = "12345678901231", Rect = default };
        var (w, h) = ElementAppearance.DefaultCodeSize(code);
        code = code with { Rect = new PdfRectangle(100, 400, 100 + w, 400 + h), Caption = false };
        using var blank = BlankPage.CreateDocument(595, 842, 1);
        using var output = new MemoryStream();
        PdfWriter.Write(blank, [PageItem.From(blank)[0].WithElements([code])], output, new PdfSaveOptions { Flatten = true });
        using var doc = PdfDocument.Open(output.ToArray());
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 4, new ImageCache());
        // the first space between bars, at the very top and the very bottom of the code and in its middle
        var symbol = CodeSymbol.Create(BarcodeFormat.Itf, code.Text);
        var module = w / symbol.ModulesAcross;
        var space = Array.FindIndex(symbol.Bars!, 4, b => !b);   // after the start pattern's first bars: inside the first character
        var x = 100 + (symbol.QuietLeft + space + 0.5) * module;
        SkiaSharp.SKColor At(double py) => bitmap.GetPixel((int)(x * 4), (int)((842 - py) * 4));
        Assert.True(At(400 + h - 0.5 * module).Red < 100, "top bearer bar");
        Assert.True(At(400 + 0.5 * module).Red < 100, "bottom bearer bar");
        Assert.True(At(400 + h / 2).Red > 200, "the space itself stays light");
        var found = Assert.Single(PageBarcodes.FindAll(doc, page, PageInterpreter.Build(doc, page), [], new ImageCache()));
        Assert.Equal("12345678901231", found.Code.Text);

        Assert.True(symbol.BearerBars);
        Assert.False(CodeSymbol.Create(BarcodeFormat.Itf, "123457").BearerBars);
    }
}
