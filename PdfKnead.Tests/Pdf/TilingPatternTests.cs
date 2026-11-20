using System.Diagnostics;
using System.Text;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Tiling patterns: coloured and uncoloured, matrix, overlapping cells, strokes, recursion and crafted values.</summary>
public sealed class TilingPatternTests
{
    private static SKBitmap Render(string content, Func<TestPdf, PdfDictionary> resources)
    {
        var (doc, list) = InterpreterTests.Page(content, resources);
        using (doc) return SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
    }

    private static SKColor At(SKBitmap b, int x, int pdfY) => b.GetPixel(x, b.Height - 1 - pdfY);

    private static PdfStream Pattern(string cell, double xStep, double yStep, double[] bbox, int paintType = 1, double[]? matrix = null,
        Func<TestPdf, PdfDictionary>? resources = null, TestPdf? pdf = null)
    {
        var dict = TestPdf.Dict(("Type", TestPdf.N("Pattern")), ("PatternType", PdfInteger.Of(1)), ("PaintType", PdfInteger.Of(paintType)),
            ("TilingType", PdfInteger.Of(1)), ("BBox", TestPdf.Box(bbox)), ("XStep", new PdfReal(xStep)), ("YStep", new PdfReal(yStep)));
        if (matrix is not null) dict.Set(TestPdf.N("Matrix"), TestPdf.Box(matrix));
        if (resources is not null && pdf is not null) dict.Set(TestPdf.N("Resources"), resources(pdf));
        return new PdfStream(dict, Encoding.ASCII.GetBytes(cell));
    }

    private static Func<TestPdf, PdfDictionary> With(Func<TestPdf, PdfStream> pattern, PdfObject? colorSpace = null) => pdf =>
    {
        var resources = TestPdf.Dict(("Pattern", TestPdf.Dict(("P", TestPdf.Ref(pdf.Add(pattern(pdf)))))));
        if (colorSpace is not null) resources.Set(TestPdf.N("ColorSpace"), TestPdf.Dict(("CS", colorSpace)));
        return resources;
    };

    [Fact]
    public void Colored_RepeatsTheCell()
    {
        // Cell 10 × 10, red square at the bottom left
        using var b = Render("/Pattern cs /P scn 0 0 200 200 re f", With(_ => Pattern("1 0 0 rg 0 0 5 5 re f", 10, 10, [0, 0, 10, 10])));
        Assert.Equal(SKColors.Red, At(b, 2, 2));
        Assert.Equal(SKColors.White, At(b, 7, 7));
        Assert.Equal(SKColors.Red, At(b, 102, 152));
        Assert.Equal(SKColors.White, At(b, 107, 152));
    }

    [Fact]
    public void HugeStep_CellWithSoftMask_IsDrawn()
    {
        // issue6737 (green header band missing): the cell contains a soft mask. Drawing the cell (with its clip "W n") directly
        // into the already clipped canvas loses everything, so the cell is recorded the same way as for the shader
        using var b = Render("/Pattern cs /P scn 0 0 200 200 re f", pdf =>
        {
            var maskGroup = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")),
                ("BBox", TestPdf.Box(0, 0, 200, 200)), ("Group", TestPdf.Dict(("S", TestPdf.N("Transparency")), ("CS", TestPdf.N("DeviceGray"))))),
                "1 g 0 0 200 200 re f"u8.ToArray()));
            var states = TestPdf.Dict(("GS", TestPdf.Dict(("SMask", TestPdf.Dict(("Type", TestPdf.N("Mask")), ("S", TestPdf.N("Luminosity")), ("G", TestPdf.Ref(maskGroup)))))));
            return With(_ => Pattern("q 10 10 180 180 re W n /GS gs 1 0 0 rg 20 20 160 160 re f Q", 90000, 90000, [0, 0, 200, 200],
                matrix: [1, 0, 0, -1, 0, 200], resources: _ => TestPdf.Dict(("ExtGState", states)), pdf: pdf))(pdf);   // mirrored as in that file
        });
        Assert.Equal(SKColors.Red, At(b, 100, 100));
        Assert.Equal(SKColors.White, At(b, 10, 100));
    }

    /// <summary>
    /// A pattern matrix that stretches by 10^9 in one direction (a fuzzed copy of a public test file): one tile spans billions of pixels
    /// on the screen. The picture shader recorded the cell again at that scale and drawing the page took minutes
    /// (docs/FEHLERGEDAECHTNIS.md, No. 407); now the cell is repeated as an image of fixed size, and the page is drawn at once.
    /// </summary>
    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void MatrixStretchingByBillions_DrawnAtOnce()
    {
        // stroked circles: Skia splits curves by their size on the screen, stretched that far into millions of segments
        static string Circle(double x, double y) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{x + 4} {y} m {x + 4} {y + 2.2} {x + 2.2} {y + 4} {x} {y + 4} c {x - 2.2} {y + 4} {x - 4} {y + 2.2} {x - 4} {y} c {x - 4} {y - 2.2} {x - 2.2} {y - 4} {x} {y - 4} c {x + 2.2} {y - 4} {x + 4} {y - 2.2} {x + 4} {y} c S ");
        var cell = "1 0 0 RG " + Circle(5, 5) + Circle(15, 5) + Circle(5, 15) + Circle(15, 15);
        var watch = Stopwatch.StartNew();
        using var b = Render("/Pattern cs /P scn 0 0 200 200 re f", With(_ => Pattern(cell, 20, 20, [0, 0, 100, 100], matrix: [1e9, 1, -0.5, 0.7, 0, 0])));
        Assert.True(watch.Elapsed.TotalSeconds < 3 * TestKind.Factor, $"{watch.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public void InvalidTilingPattern_PaintsNothing()
    {
        // operator_list_cycle (pattern without XStep/YStep that contains itself through form XObjects): two of the reference readers paint
        // nothing, so the grey fallback colour of the pattern colour space must not be painted either
        using (var b = Render("/Pattern cs /P scn 0 0 200 200 re f", With(_ => Pattern("1 0 0 rg 0 0 5 5 re f", 0, 10, [0, 0, 10, 10]))))
            Assert.Equal(SKColors.White, At(b, 100, 100));
        using (var b = Render("/Pattern cs /P scn 0 0 200 200 re f", With(_ => Pattern("1 0 0 rg 0 0 10 10 re f", 10, 10, [0, 0, 10, 10]))))
            Assert.Equal(SKColors.Red, At(b, 100, 100));   // valid: painted normally
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void HugeStep_KeepsTheCellSharp(int paintType)
    {
        // tiling-pattern-large-steps: step 90 000. The shader coarsens the cell until a whole step fits into 2 048 pixels, which
        // blurs the edges; therefore the visible tiles are drawn directly (also uncoloured ones, with the colour from outside)
        var content = paintType == 1 ? "/Pattern cs /P scn 0 0 200 200 re f" : "/CS cs 1 0 0 /P scn 0 0 200 200 re f";
        using var b = Render(content, With(_ => Pattern(paintType == 1 ? "1 0 0 rg 20 20 160 160 re f" : "20 20 160 160 re f", 90000, 90000,
            [0, 0, 200, 200], paintType), paintType == 2 ? new PdfArray([TestPdf.N("Pattern"), TestPdf.N("DeviceRGB")]) : null));
        Assert.Equal(SKColors.Red, At(b, 21, 100));
        Assert.Equal(SKColors.White, At(b, 18, 100));
        Assert.Equal(SKColors.Red, At(b, 100, 178));
        Assert.Equal(SKColors.White, At(b, 100, 181));
    }

    [Fact]
    public void PatternName_WithoutPatternColorSpace_IsTheFill()
    {
        // issue18466 (generated by a browser): "/P scn" without a preceding "/Pattern cs"; staying with DeviceGray would paint the
        // area black and cover the text
        using var b = Render("/P scn 0 0 200 200 re f", With(_ => Pattern("1 0 0 rg 0 0 5 5 re f", 10, 10, [0, 0, 10, 10])));
        Assert.Equal(SKColors.Red, At(b, 2, 2));
        Assert.Equal(SKColors.White, At(b, 7, 7));
    }

    [Fact]
    public void BBoxAwayFromTheOrigin_KeepsTheGrid()
    {
        // issue13561: BBox [3 3 13 13], step 10, red square 3…8; the grid lies at 3 + 10 k, not at 10 k
        using var b = Render("/Pattern cs /P scn 0 0 200 200 re f", With(_ => Pattern("1 0 0 rg 3 3 5 5 re f", 10, 10, [3, 3, 13, 13])));
        Assert.Equal(SKColors.Red, At(b, 5, 5));
        Assert.Equal(SKColors.White, At(b, 10, 10));
        Assert.Equal(SKColors.Red, At(b, 105, 155));
        Assert.Equal(SKColors.White, At(b, 101, 151));
    }

    [Fact]
    public void Uncolored_TakesTheColorFromScn()
    {
        var space = new PdfArray([TestPdf.N("Pattern"), TestPdf.N("DeviceRGB")]);
        using var b = Render("/CS cs 0 0 1 /P scn 0 0 200 200 re f",
            With(_ => Pattern("1 0 0 rg 0 0 5 5 re f", 10, 10, [0, 0, 10, 10], paintType: 2), space));
        Assert.Equal(SKColors.Blue, At(b, 52, 52));   // the colours of the cell do not count
        Assert.Equal(SKColors.White, At(b, 57, 57));
    }

    [Fact]
    public void Matrix_ScalesAndMovesTheGrid()
    {
        using var b = Render("/Pattern cs /P scn 0 0 200 200 re f",
            With(_ => Pattern("1 0 0 rg 0 0 5 5 re f", 10, 10, [0, 0, 10, 10], matrix: [2, 0, 0, 2, 3, 0])));
        Assert.Equal(SKColors.Red, At(b, 4, 2));      // 3…13 red
        Assert.Equal(SKColors.Red, At(b, 12, 8));
        Assert.Equal(SKColors.White, At(b, 1, 2));    // before it the previous cell: −17…−7
        Assert.Equal(SKColors.White, At(b, 16, 2));
        Assert.Equal(SKColors.Red, At(b, 24, 2));     // next one: 23…33
    }

    [Fact]
    public void CellsLargerThanTheStep_Overlap()
    {
        // BBox 20 wide, step 10: the red at 8…15 reaches into the next tile
        using var b = Render("/Pattern cs /P scn 0 0 200 200 re f", With(_ => Pattern("1 0 0 rg 8 0 7 5 re f", 10, 10, [0, 0, 20, 10])));
        Assert.Equal(SKColors.Red, At(b, 12, 2));     // reachable only via the neighbour
        Assert.Equal(SKColors.White, At(b, 16, 2));
        Assert.Equal(SKColors.Red, At(b, 109, 52));
    }

    [Fact]
    public void Clip_CutsTheCellAtItsBBox()
    {
        // Content larger than the BBox: nothing outside it
        using var b = Render("/Pattern cs /P scn 0 0 200 200 re f", With(_ => Pattern("1 0 0 rg -5 -5 30 30 re f", 20, 20, [0, 0, 10, 10])));
        Assert.Equal(SKColors.Red, At(b, 5, 5));
        Assert.Equal(SKColors.White, At(b, 15, 5));
    }

    [Fact]
    public void Stroke_WithPattern()
    {
        using var b = Render("/Pattern CS /P SCN 20 w 0 100 m 200 100 l S", With(_ => Pattern("1 0 0 rg 0 0 5 10 re f", 10, 10, [0, 0, 10, 10])));
        Assert.Equal(SKColors.Red, At(b, 52, 100));
        Assert.Equal(SKColors.White, At(b, 57, 100));
        Assert.Equal(SKColors.White, At(b, 52, 150));   // outside the stroke
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void PatternUsingItself_Terminates()
    {
        // The cell paints with the same pattern: while being built it counts as empty, nothing hangs
        var watch = Stopwatch.StartNew();
        using var b = Render("/Pattern cs /P scn 0 0 200 200 re f", pdf =>
        {
            var number = pdf.Add(new PdfDictionary());
            var self = TestPdf.Dict(("Pattern", TestPdf.Dict(("P", TestPdf.Ref(number)))));
            var pattern = Pattern("/Pattern cs /P scn 0 0 10 10 re f 1 0 0 rg 0 0 3 3 re f", 10, 10, [0, 0, 10, 10]);
            pattern.Dictionary.Set(TestPdf.N("Resources"), self);
            pdf.Replace(number, pattern);
            return self;
        });
        Assert.Equal(SKColors.Red, At(b, 1, 1));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10 * TestKind.Factor));
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void HugeOverlappingCell_RecordsOnlyItsOwnCopy()
    {
        // BBox 1 000, step 60, 20 000 areas: 289 neighbour copies would be 5.8 million entries, so only the own copy is recorded
        var cell = "1 0 0 rg " + string.Concat(Enumerable.Repeat("0 0 1000 1000 re f ", 20_000));
        var watch = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        using var b = Render("/Pattern cs /P scn 0 0 200 200 re f", With(_ => Pattern(cell, 60, 60, [0, 0, 1000, 1000])));
        Assert.Equal(SKColors.Red, At(b, 100, 100));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10 * TestKind.Factor), $"{watch.Elapsed}");
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 512L << 20, "zu viel Speicher");
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Theory]
    [InlineData(0, 10)]
    [InlineData(1e30, 10)]
    [InlineData(1e-6, 1e-6)]
    [InlineData(-10, -10)]
    [InlineData(double.NaN, 10)]
    public void CraftedSteps_DoNotCrashOrHang(double xStep, double yStep)
    {
        var watch = Stopwatch.StartNew();
        using var b = Render("/Pattern cs /P scn 0 0 200 200 re f", With(_ => Pattern("1 0 0 rg 0 0 5 5 re f", xStep, yStep, [0, 0, 10, 10])));
        Assert.Equal(200, b.Width);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10 * TestKind.Factor), $"{watch.Elapsed}");
        if (xStep == -10) Assert.Equal(SKColors.Red, At(b, 2, 2));   // negative: the same grid
    }
}
