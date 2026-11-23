using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Printing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// On the printed sheet the page is a form, and the page around it is the sheet (docs/FEHLERGEDAECHTNIS.md, No. 1005): a form or tiling pattern
/// without resources of its own takes those of the page it is used on (ISO 32000-2, 7.8.3, sentence 18; No. 791) — they are written into its copy.
/// Before, it looked in the sheet's resources: an image it names was missing in print, a graphics state with Multiply drew covering.
/// </summary>
public sealed class SheetInheritTests
{
    private static SkiaSharp.SKColor SheetPixel(byte[] file, int x, int pdfY)
    {
        using var doc = PdfDocument.Open(file);
        var sheets = PrintLayout.Arrange([(doc.Pages[0].Width, doc.Pages[0].Height)],
            new PrintLayoutOptions { Scaling = PrintScaling.ActualSize, Margin = 0, PaperWidth = doc.Pages[0].Width, PaperHeight = doc.Pages[0].Height });
        using var output = new MemoryStream();
        SheetPdf.Write(doc, [0], sheets, output);
        using var sheet = PdfDocument.Open(output.ToArray());
        using var bitmap = RasterExport.Render(PageItem.From(sheet)[0], 1, new ImageCache());
        return bitmap.GetPixel(x, bitmap.Height - 1 - pdfY);
    }

    private static byte[] Page(string content, Func<TestPdf, PdfDictionary> resources)
    {
        var pdf = new TestPdf();
        var catalog = TestPdf.Dict(("Type", TestPdf.N("Catalog")));
        var catalogNumber = pdf.Add(catalog);
        var pages = pdf.Add(new PdfDictionary());
        var res = resources(pdf);
        var stream = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content)));
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("MediaBox", TestPdf.Box(0, 0, 200, 100)),
            ("Resources", res), ("Contents", TestPdf.Ref(stream))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        catalog.Set(PdfName.Pages, TestPdf.Ref(pages));
        pdf.RootNumber = catalogNumber;
        return pdf.Build();
    }

    /// <summary>A form without resources draws another form by a name of the page (as in a test file of the collections): on the sheet as well.</summary>
    [Fact]
    public void FormWithoutResources_NamesAFormOfThePage_DrawnOnTheSheet()
    {
        var file = Page("q /Outer Do Q", pdf =>
        {
            var square = pdf.Add(TestPdf.FlateStream("0 0 0 rg 0 0 50 50 re f"u8.ToArray(),
                TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 50, 50)))));
            var outer = pdf.Add(TestPdf.FlateStream("q 1 0 0 1 20 20 cm /Square Do Q"u8.ToArray(),
                TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 200, 100)))));
            return TestPdf.Dict(("XObject", TestPdf.Dict(("Square", TestPdf.Ref(square)), ("Outer", TestPdf.Ref(outer)))));
        });
        var c = SheetPixel(file, 45, 45);
        Assert.True(c.Red < 60 && c.Green < 60 && c.Blue < 60, $"schwarz erwartet: {c}");
    }

    /// <summary>
    /// A transparency group without resources fills with a graphics state of the page (Multiply), over text-coloured cyan: yellow over cyan
    /// multiplies to green on the sheet as on the screen. Before, the state was not found, and the yellow covered.
    /// </summary>
    [Fact]
    public void GroupWithoutResources_UsesAGraphicsStateOfThePage_OnTheSheet()
    {
        var file = Page("0 1 1 rg 0 0 200 100 re f q /Mark Do Q", pdf =>
        {
            var mark = pdf.Add(TestPdf.FlateStream("/Multiply gs 1 1 0 rg 50 20 100 60 re f"u8.ToArray(),
                TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 200, 100)),
                    ("Group", TestPdf.Dict(("Type", TestPdf.N("Group")), ("S", TestPdf.N("Transparency")))))));
            return TestPdf.Dict(("XObject", TestPdf.Dict(("Mark", TestPdf.Ref(mark)))),
                ("ExtGState", TestPdf.Dict(("Multiply", TestPdf.Dict(("Type", TestPdf.N("ExtGState")), ("BM", TestPdf.N("Multiply")))))));
        });
        var c = SheetPixel(file, 100, 50);
        Assert.True(c.Green > 180 && c.Red < 60 && c.Blue < 60, $"grün erwartet: {c}");
    }

    /// <summary>A tiling pattern without resources paints a form of the page as its cell: the fill shows on the sheet.</summary>
    [Fact]
    public void PatternWithoutResources_NamesAFormOfThePage_PaintedOnTheSheet()
    {
        var file = Page("/Pattern cs /Tiles scn 0 0 200 100 re f", pdf =>
        {
            var dot = pdf.Add(TestPdf.FlateStream("1 0 0 rg 0 0 10 10 re f"u8.ToArray(),
                TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 10, 10)))));
            var tiles = pdf.Add(TestPdf.FlateStream("/Dot Do"u8.ToArray(),
                TestPdf.Dict(("Type", TestPdf.N("Pattern")), ("PatternType", PdfInteger.Of(1)), ("PaintType", PdfInteger.Of(1)), ("TilingType", PdfInteger.Of(1)),
                    ("BBox", TestPdf.Box(0, 0, 10, 10)), ("XStep", PdfInteger.Of(10)), ("YStep", PdfInteger.Of(10)))));
            return TestPdf.Dict(("XObject", TestPdf.Dict(("Dot", TestPdf.Ref(dot)))), ("Pattern", TestPdf.Dict(("Tiles", TestPdf.Ref(tiles)))));
        });
        var c = SheetPixel(file, 105, 55);
        Assert.True(c.Red > 180 && c.Green < 80 && c.Blue < 80, $"rot erwartet: {c}");
    }
}
