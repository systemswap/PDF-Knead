using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Overprint preview: takes effect only where overprinting takes effect in PDF, and only with the preview switched on.</summary>
public sealed class OverprintTests
{
    private static SKBitmap Render(string content, bool preview, PdfDictionary? colorSpaces = null)
    {
        var (doc, list) = InterpreterTests.Page(content, _ =>
        {
            var states = TestPdf.Dict(
                ("OP1", TestPdf.Dict(("OP", PdfBoolean.True), ("op", PdfBoolean.True), ("OPM", PdfInteger.Of(1)))),
                ("OP0", TestPdf.Dict(("OP", PdfBoolean.True), ("op", PdfBoolean.True), ("OPM", PdfInteger.Of(0)))),
                ("Stroke", TestPdf.Dict(("OP", PdfBoolean.True), ("op", PdfBoolean.False), ("OPM", PdfInteger.Of(1)))));
            var resources = TestPdf.Dict(("ExtGState", states));
            if (colorSpaces is not null) resources.Set(TestPdf.N("ColorSpace"), colorSpaces);
            return resources;
        });
        using (doc) return SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache(), overprintPreview: preview);
    }

    private static SKColor At(SKBitmap b, int x, int pdfY) => b.GetPixel(x, b.Height - 1 - pdfY);

    private static void Greenish(SKColor c) => Assert.True(c.Green > 120 && c.Red < 60 && c.Blue < 100, $"nicht grün: {c}");

    private const string CyanThenYellow = "1 0 0 0 k 0 0 120 200 re f /{0} gs 0 0 1 0 k 80 0 120 200 re f";

    [Fact]
    public void CmykWithOpm1_MixesOnlyInThePreview()
    {
        using (var b = Render(string.Format(CyanThenYellow, "OP1"), preview: false))
        {
            var c = At(b, 100, 100);
            Assert.True(c.Red > 200 && c.Green > 200 && c.Blue < 60, $"ohne Vorschau Gelb erwartet: {c}");
        }
        using (var b = Render(string.Format(CyanThenYellow, "OP1"), preview: true))
        {
            Greenish(At(b, 100, 100));
            var yellow = At(b, 150, 100);   // without cyan underneath it stays yellow
            Assert.True(yellow.Red > 200 && yellow.Green > 200 && yellow.Blue < 60, $"Gelb erwartet: {yellow}");
        }
    }

    [Fact]
    public void CmykWithOpm0_PaintsAllPlates()
    {
        // /OPM 0: with DeviceCMYK all four separations are painted, so overprinting changes nothing
        using var b = Render(string.Format(CyanThenYellow, "OP0"), preview: true);
        var c = At(b, 100, 100);
        Assert.True(c.Red > 200 && c.Green > 200 && c.Blue < 60, $"Gelb erwartet: {c}");
    }

    [Fact]
    public void Separation_Overprints()
    {
        var yellowFn = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(0, 0, 0, 0)),
            ("C1", TestPdf.Box(0, 0, 1, 0)), ("N", PdfInteger.Of(1)));
        var spot = new PdfArray([TestPdf.N("Separation"), TestPdf.N("Gelb"), TestPdf.N("DeviceCMYK"), yellowFn]);
        using var b = Render("1 0 0 0 k 0 0 120 200 re f /OP0 gs /Spot cs 1 sc 80 0 120 200 re f", preview: true,
            TestPdf.Dict(("Spot", spot)));
        Greenish(At(b, 100, 100));   // spot colour: the other separations remain, even with /OPM 0
    }

    [Fact]
    public void CmykImage_AfterBlackText_IsNotMultiplied()
    {
        // /op and /OPM 1 with a black fill colour (0 0 0 1 k, component 0): a CMYK image drawn afterwards is opaque, because /OPM applies
        // to the current colour, not to images; only images in Separation/DeviceN overprint
        var (doc, list) = InterpreterTests.Page("1 0 0 0 k 0 0 200 200 re f /OP1 gs 0 0 0 1 k q 200 0 0 200 0 0 cm /Im Do Q", pdf => TestPdf.Dict(
            ("ExtGState", TestPdf.Dict(("OP1", TestPdf.Dict(("OP", PdfBoolean.True), ("op", PdfBoolean.True), ("OPM", PdfInteger.Of(1)))))),
            ("XObject", TestPdf.Dict(("Im", TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
                ("Width", PdfInteger.Of(1)), ("Height", PdfInteger.Of(1)), ("BitsPerComponent", PdfInteger.Of(8)),
                ("ColorSpace", TestPdf.N("DeviceCMYK"))), [0, 0, 255, 0]))))))));
        using (doc)
        {
            using var b = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache(), overprintPreview: true);
            var c = At(b, 100, 100);
            Assert.True(c.Red > 200 && c.Green > 200 && c.Blue < 60, $"Gelb erwartet (Bild deckt): {c}");
        }
    }

    [Fact]
    public void OnlyStrokeOverprint_LeavesFillsAlone()
    {
        // /OP true, /op false: fills are opaque, strokes overprint
        using var b = Render("1 0 0 0 k 1 0 0 0 K 0 0 200 200 re f /Stroke gs 0 0 1 0 k 0 0 1 0 K 40 w 0 50 m 200 50 l S 0 120 200 40 re f",
            preview: true);
        Greenish(At(b, 100, 50));
        var fill = At(b, 100, 140);
        Assert.True(fill.Red > 200 && fill.Green > 200 && fill.Blue < 60, $"Gelb erwartet: {fill}");
    }
}
