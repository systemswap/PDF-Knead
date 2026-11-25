using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M6 sentence by sentence, package g-871-874-4: ISO 32000-2, 8.7.1–8.7.3 (patterns, tiling patterns) and 8.7.4.1–8.7.4.4 (shading patterns,
/// „sh“, shading dictionaries, colour spaces of shadings).
/// </summary>
public sealed partial class M6NormTests
{
    private static SKBitmap G874_Render(string content, Func<TestPdf, PdfDictionary> resources)
    {
        var (doc, list) = InterpreterTests.Page(content, resources);
        using (doc) return SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
    }

    private static SKColor G874_At(SKBitmap b, int x, int pdfY) => b.GetPixel(x, b.Height - 1 - pdfY);

    private static bool G874_Red(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;
    private static bool G874_Blue(SKColor c) => c.Blue > 200 && c.Red < 60 && c.Green < 60;
    private static bool G874_White(SKColor c) => c.Red > 240 && c.Green > 240 && c.Blue > 240;
    private static bool G874_LightGrey(SKColor c) => Math.Abs(c.Red - 200) <= 3 && Math.Abs(c.Green - 200) <= 3 && Math.Abs(c.Blue - 200) <= 3;

    /// <summary>A tiling pattern stream (TilingType 1 unless given); <paramref name="patternType"/> null leaves /PatternType out.</summary>
    private static PdfStream G874_Tiling(string cell, double step, double[] bbox, int paintType = 1, double[]? matrix = null,
        PdfDictionary? resources = null, bool withType = true, int? patternType = 1, int tilingType = 1)
    {
        var dict = TestPdf.Dict(("PaintType", PdfInteger.Of(paintType)), ("TilingType", PdfInteger.Of(tilingType)), ("BBox", TestPdf.Box(bbox)),
            ("XStep", new PdfReal(step)), ("YStep", new PdfReal(step)));
        if (withType) dict.Set(TestPdf.N("Type"), TestPdf.N("Pattern"));
        if (patternType is { } type) dict.Set(TestPdf.N("PatternType"), PdfInteger.Of(type));
        if (matrix is not null) dict.Set(TestPdf.N("Matrix"), TestPdf.Box(matrix));
        if (resources is not null) dict.Set(TestPdf.N("Resources"), resources);
        return new PdfStream(dict, Encoding.ASCII.GetBytes(cell));
    }

    /// <summary>Resources with the pattern /P (as an object of its own), optionally the colour space /CS and more entries.</summary>
    private static Func<TestPdf, PdfDictionary> G874_With(Func<TestPdf, PdfObject> pattern, PdfObject? colorSpace = null,
        Action<TestPdf, PdfDictionary>? more = null) => pdf =>
    {
        var resources = TestPdf.Dict(("Pattern", TestPdf.Dict(("P", TestPdf.Ref(pdf.Add(pattern(pdf)))))));
        if (colorSpace is not null) resources.Set(TestPdf.N("ColorSpace"), TestPdf.Dict(("CS", colorSpace)));
        more?.Invoke(pdf, resources);
        return resources;
    };

    private static PdfArray G874_UncolouredRgb() => new([TestPdf.N("Pattern"), TestPdf.N("DeviceRGB")]);

    private static PdfDictionary G874_Exponential(double[] c0, double[] c1) => TestPdf.Dict(("FunctionType", PdfInteger.Of(2)),
        ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(c0)), ("C1", TestPdf.Box(c1)), ("N", PdfInteger.Of(1)));

    private static PdfDictionary G874_Axial(double x0, double x1, bool extend, PdfObject? function = null, PdfObject? colorSpace = null)
    {
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(2)), ("Coords", TestPdf.Box(x0, 0, x1, 0)),
            ("Function", function ?? G874_Exponential([1, 0, 0], [0, 0, 1])),
            ("Extend", new PdfArray([PdfBoolean.Of(extend), PdfBoolean.Of(extend)])));
        if (colorSpace is not PdfNull) dict.Set(TestPdf.N("ColorSpace"), colorSpace ?? TestPdf.N("DeviceRGB"));
        return dict;
    }

    private static Func<TestPdf, PdfDictionary> G874_Sh(PdfObject shading) => pdf =>
        TestPdf.Dict(("Shading", TestPdf.Dict(("Sh", shading is PdfStream ? TestPdf.Ref(pdf.Add(shading)) : shading))));

    private static PdfStream G874_StencilMask() => new(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
        ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(1)), ("ImageMask", PdfBoolean.True)), [0b0100_0000]);   // left painted, right not

    private static PdfDictionary G874_Helvetica() => TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")),
        ("BaseFont", TestPdf.N("Helvetica")), ("Encoding", TestPdf.N("WinAnsiEncoding")));

    /// <summary>A free-form mesh (type 4, one byte per value) of <paramref name="vertices"/>: flag, x, y, then the components.</summary>
    private static PdfStream G874_Mesh(PdfObject colorSpace, double[] decode, params byte[][] vertices) => new(TestPdf.Dict(
        ("ShadingType", PdfInteger.Of(4)), ("ColorSpace", colorSpace), ("BitsPerCoordinate", PdfInteger.Of(8)), ("BitsPerComponent", PdfInteger.Of(8)),
        ("BitsPerFlag", PdfInteger.Of(8)), ("Decode", TestPdf.Box(decode))), [.. vertices.SelectMany(v => v)]);

    // ---------------------------------------------------------------- 8.7.2, 8.7.3.1, 8.7.4.1: pattern types

    /// <summary>
    /// 8.7.2 sentences 1–3, 8.7.3.1 sentences 14–15 and 8.7.4.1 sentences 7–8 (behaviour of today): a pattern whose /PatternType is missing or does
    /// not fit its kind — a stream without it, with 3 or with 2; a dictionary without it, with 1 or with 3 — is no pattern: the fill shows the light
    /// grey fallback colour of the Pattern colour space (`PageInterpreter.TilingIndex` and `PatternIndex` give −1).
    /// </summary>
    [Theory]
    [InlineData(true, null)]
    [InlineData(true, 3)]
    [InlineData(true, 2)]
    [InlineData(false, null)]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    public void PatternType_MissingOrUnfit_PaintsTheLightGreyFallback(bool stream, int? type)
    {
        PdfObject Pattern()
        {
            if (stream) return G874_Tiling("1 0 0 rg 0 0 10 10 re f", 10, [0, 0, 10, 10], patternType: type);
            var dict = TestPdf.Dict(("Shading", G874_Axial(0, 200, true)));
            if (type is { } t) dict.Set(TestPdf.N("PatternType"), PdfInteger.Of(t));
            return dict;
        }
        using var b = G874_Render("/Pattern cs /P scn 0 0 200 200 re f", G874_With(_ => Pattern()));
        Assert.True(G874_LightGrey(G874_At(b, 100, 100)), $"{G874_At(b, 100, 100)}");
    }

    /// <summary>
    /// 8.7.4.1 sentences 9–11 (behaviour of today): a shading pattern without /Shading paints the light grey fallback colour (`PatternIndex`: −1).
    /// </summary>
    [Fact]
    public void ShadingPattern_WithoutShading_PaintsTheLightGreyFallback()
    {
        using var b = G874_Render("/Pattern cs /P scn 0 0 200 200 re f", G874_With(_ => TestPdf.Dict(("PatternType", PdfInteger.Of(2)))));
        Assert.True(G874_LightGrey(G874_At(b, 100, 100)), $"{G874_At(b, 100, 100)}");
    }

    // ---------------------------------------------------------------- 8.7.2: pattern space

    /// <summary>
    /// 8.7.2 sentences 8–9: a pattern of the page refers to the default space of the page; a „cm“ before it changes nothing. Scaled by 2, a tiling
    /// cell 10 × 10 with a red square 0–5 stays 10 wide on the page (white at 7/7), and a shading pattern on the axis 0–100 still ends at x 100
    /// (blue at 120, half way at 50).
    /// </summary>
    [Fact]
    public void PatternOfThePage_IgnoresTheCtm()
    {
        using (var b = G874_Render("2 0 0 2 0 0 cm /Pattern cs /P scn 0 0 100 100 re f",
                   G874_With(_ => G874_Tiling("1 0 0 rg 0 0 5 5 re f", 10, [0, 0, 10, 10]))))
        {
            Assert.True(G874_Red(G874_At(b, 2, 2)), $"{G874_At(b, 2, 2)}");
            Assert.True(G874_White(G874_At(b, 7, 7)), $"{G874_At(b, 7, 7)}");
            Assert.True(G874_Red(G874_At(b, 12, 2)), $"{G874_At(b, 12, 2)}");
        }
        using (var b = G874_Render("2 0 0 2 0 0 cm /Pattern cs /P scn 0 0 100 100 re f",
                   G874_With(_ => TestPdf.Dict(("PatternType", PdfInteger.Of(2)), ("Shading", G874_Axial(0, 100, true))))))
        {
            Assert.True(G874_Blue(G874_At(b, 120, 100)), $"{G874_At(b, 120, 100)}");
            Assert.InRange(G874_At(b, 50, 100).Red, 100, 155);
        }
    }

    /// <summary>
    /// 8.7.2 sentence 11: the matrix of a pattern used inside a pattern cell refers to the pattern space of the outer one. Outer /Matrix scales by 2,
    /// inner /Matrix moves by 5: the inner red squares (0–5 in a cell of 10) lie on the page at x 10–20, 30–40 … and y 0–10, 20–30 … — red at
    /// (12, 3) and (32, 23), white at (7, 2) where an inner matrix taken on the page would put red.
    /// </summary>
    [Fact]
    public void PatternInAPattern_RefersToTheOuterPatternSpace()
    {
        using var b = G874_Render("/Pattern cs /P scn 0 0 200 200 re f", G874_With(pdf =>
        {
            var inner = pdf.Add(G874_Tiling("1 0 0 rg 0 0 5 5 re f", 10, [0, 0, 10, 10], matrix: [1, 0, 0, 1, 5, 0]));
            return G874_Tiling("/Pattern cs /Q scn 0 0 100 100 re f", 100, [0, 0, 100, 100], matrix: [2, 0, 0, 2, 0, 0],
                resources: TestPdf.Dict(("Pattern", TestPdf.Dict(("Q", TestPdf.Ref(inner))))));
        }));
        Assert.True(G874_Red(G874_At(b, 12, 3)), $"{G874_At(b, 12, 3)}");
        Assert.True(G874_Red(G874_At(b, 32, 23)), $"{G874_At(b, 32, 23)}");
        Assert.True(G874_White(G874_At(b, 7, 2)), $"{G874_At(b, 7, 2)}");
        Assert.True(G874_White(G874_At(b, 12, 13)), $"{G874_At(b, 12, 13)}");
    }

    // ---------------------------------------------------------------- 8.7.3.1: tiling pattern dictionary

    /// <summary>8.7.3.1 sentences 11–13 (reader): /Type is optional — a tiling pattern without it paints its cell as with it.</summary>
    [Fact]
    public void TilingPattern_WithoutType_PaintsAsWithIt()
    {
        using var b = G874_Render("/Pattern cs /P scn 0 0 200 200 re f", G874_With(_ => G874_Tiling("1 0 0 rg 0 0 5 5 re f", 10, [0, 0, 10, 10], withType: false)));
        Assert.True(G874_Red(G874_At(b, 2, 2)), $"{G874_At(b, 2, 2)}");
        Assert.True(G874_White(G874_At(b, 7, 7)), $"{G874_At(b, 7, 7)}");
        Assert.True(G874_Red(G874_At(b, 102, 152)), $"{G874_At(b, 102, 152)}");
    }

    /// <summary>
    /// 8.7.3.1 sentences 11–13 (writer) and 8.7.3.3 sentences 2–5 (writer): a pattern only chosen as the colour of a fill that lies wholly under a
    /// redaction bar is replaced by an empty one (`Redactor.EmptyPattern`) — it names /Type /Pattern and /PatternType 1, and keeps /PaintType 2 for an
    /// uncoloured pattern (1 for a coloured one).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void RedactedChosenPattern_EmptyReplacementNamesTypeAndPaintType(int paintType)
    {
        var pdf = TestPdf.Simple(1);
        var cell = G874_Tiling(paintType == 1 ? "1 0 0 rg 0 0 10 10 re f" : "0 0 10 10 re f", 10, [0, 0, 10, 10], paintType,
            resources: new PdfDictionary());
        var resources = ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!;
        resources.Set(PdfName.Get("Pattern"), TestPdf.Dict(("P1", TestPdf.Ref(pdf.Add(cell)))));
        resources.Set(PdfName.Get("ColorSpace"), TestPdf.Dict(("CS", G874_UncolouredRgb())));
        var choose = paintType == 1 ? "/Pattern cs /P1 scn" : "/CS cs 1 0 0 /P1 scn";
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes($"BT /F1 12 Tf 100 760 Td (Sichtbar) Tj ET q {choose} 100 700 120 20 re f Q")));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(96, 696, 224, 724))));
        var replaced = Assert.IsType<PdfStream>(after.Pages[0].Resources!.GetDictionary(PdfName.Get("Pattern"))!.Get(PdfName.Get("P1")));
        Assert.Empty(after.GetStreamData(replaced));   // the empty replacement, not the original cell
        Assert.Equal("Pattern", replaced.Dictionary.GetName(PdfName.Type)?.Value);
        Assert.Equal(1, replaced.Dictionary.GetInteger(PdfName.Get("PatternType")));
        Assert.Equal(paintType, replaced.Dictionary.GetInteger(PdfName.Get("PaintType")));
    }

    /// <summary>
    /// 8.7.3.1 sentences 16–20: a coloured cell begins with the colours of the initial graphics state, not with those the page set before — the
    /// page sets red, the cell fills „0 0 5 5 re f“ without a colour of its own: black.
    /// </summary>
    [Fact]
    public void ColouredCell_StartsWithTheInitialColour()
    {
        using var b = G874_Render("1 0 0 rg /Pattern cs /P scn 0 0 200 200 re f", G874_With(_ => G874_Tiling("0 0 5 5 re f", 10, [0, 0, 10, 10])));
        var c = G874_At(b, 2, 2);
        Assert.True(c.Red < 30 && c.Green < 30 && c.Blue < 30, $"schwarz erwartet: {c}");
        Assert.True(G874_White(G874_At(b, 7, 7)), $"{G874_At(b, 7, 7)}");
    }

    /// <summary>
    /// 8.7.3.1 sentences 21–25: an uncoloured cell may paint a stencil mask — a mask of 2 × 1 (left set) over the left half of each cell of 10:
    /// there the colour from „scn“ (blue), beside it nothing.
    /// </summary>
    [Fact]
    public void UncolouredCell_StencilMask_InTheScnColour()
    {
        using var b = G874_Render("/CS cs 0 0 1 /P scn 0 0 200 200 re f", G874_With(pdf => G874_Tiling("q 10 0 0 10 0 0 cm /M Do Q", 10, [0, 0, 10, 10], 2,
            resources: TestPdf.Dict(("XObject", TestPdf.Dict(("M", TestPdf.Ref(pdf.Add(G874_StencilMask()))))))), G874_UncolouredRgb()));
        Assert.True(G874_Blue(G874_At(b, 2, 5)), $"{G874_At(b, 2, 5)}");
        Assert.True(G874_White(G874_At(b, 7, 5)), $"{G874_At(b, 7, 5)}");
        Assert.True(G874_Blue(G874_At(b, 52, 55)), $"{G874_At(b, 52, 55)}");
        Assert.True(G874_White(G874_At(b, 57, 55)), $"{G874_At(b, 57, 55)}");
    }

    /// <summary>
    /// 8.7.3.1 sentences 26–33 (/TilingType 2; types 1 and 3 in TilingType1And3_StepInWholeDevicePixels): with type 2 the step may vary by a
    /// pixel but is right on average — a step of 10.3 at scale 1 puts cell 10 at 103–108 and cell 19 at 195.7–200.7 (a step rounded to 10 would put it at
    /// 190–195).
    /// </summary>
    [Fact]
    public void TilingType2_KeepsTheStepOnAverage()
    {
        using var b = G874_Render("/Pattern cs /P scn 0 0 200 200 re f", G874_With(_ => G874_Tiling("1 0 0 rg 0 0 5 5 re f", 10.3, [0, 0, 10, 10], tilingType: 2)));
        foreach (var x in new[] { 105, 197 }) Assert.True(G874_Red(G874_At(b, x, 2)), $"{x}: {G874_At(b, x, 2)}");
        foreach (var x in new[] { 102, 109, 194 }) Assert.True(G874_White(G874_At(b, x, 2)), $"{x}: {G874_At(b, x, 2)}");
        Assert.True(G874_Red(G874_At(b, 2, 197)), $"{G874_At(b, 2, 197)}");
    }

    /// <summary>
    /// 8.7.3.1 sentences 34–38 (behaviour of today; the note on one pixel waits for M8): a /BBox of width 0 makes the pattern paint nothing — no
    /// cell and no fallback colour.
    /// </summary>
    [Fact]
    public void TilingBBoxWithoutArea_PaintsNothing_Today()
    {
        using var b = G874_Render("/Pattern cs /P scn 0 0 200 200 re f", G874_With(_ => G874_Tiling("1 0 0 rg 0 0 5 5 re f", 10, [0, 0, 0, 10])));
        foreach (var (x, y) in new[] { (2, 2), (0, 5), (100, 100) }) Assert.True(G874_White(G874_At(b, x, y)), $"({x}, {y}): {G874_At(b, x, y)}");
    }

    /// <summary>
    /// 8.7.3.1 sentences 58–59 (step d): painting the cell leaves the graphics state of the page as it was — the cell sets „1 0 0 rg“ and „5 w“; a
    /// fill of the page afterwards stays green (its „0 1 0 rg“ from before), a stroke stays 10 wide.
    /// </summary>
    [Fact]
    public void PatternCell_GraphicsStateRestoredAfterwards()
    {
        using var b = G874_Render("0 1 0 rg 10 w /Pattern CS /P SCN 0 30 m 200 30 l S 120 120 50 50 re f 0 0 1 RG 0 150 m 100 150 l S",
            G874_With(_ => G874_Tiling("1 0 0 rg 1 0 0 RG 5 w 0 0 10 10 re f", 10, [0, 0, 10, 10])));
        var green = G874_At(b, 140, 140);
        Assert.True(green.Green > 200 && green.Red < 60 && green.Blue < 60, $"grün erwartet: {green}");
        Assert.True(G874_Blue(G874_At(b, 50, 153)), $"Strich 10 breit erwartet: {G874_At(b, 50, 153)}");
        Assert.True(G874_Red(G874_At(b, 50, 30)), $"{G874_At(b, 50, 30)}");   // the stroke with the pattern itself
    }

    // ---------------------------------------------------------------- 8.7.3.2, 8.7.3.3: coloured and uncoloured

    /// <summary>
    /// 8.7.3.2 sentences 5–10 and 14: with a coloured tiling pattern as the fill, text (Tj) and a stencil mask (Do) show the cell. The glyph of a large
    /// „H“: red and white only, red pixels inside it, never red where the cell is white (columns 6–8 of each cell), and nothing beside the glyph at
    /// (2, 2); the mask (left half set) shows the cell on the left half only.
    /// </summary>
    [Fact]
    public void ColouredTiling_FillsGlyphsAndStencilMasks()
    {
        using (var b = G874_Render("/Pattern cs /P scn BT /F1 180 Tf 10 30 Td (H) Tj ET", G874_With(_ => G874_Tiling("1 0 0 rg 0 0 5 5 re f", 10, [0, 0, 10, 10]),
                   more: (_, r) => r.Set(TestPdf.N("Font"), TestPdf.Dict(("F1", G874_Helvetica()))))))
        {
            var red = 0;
            for (var y = 0; y < 200; y++)
            for (var x = 0; x < 200; x++)
            {
                var c = G874_At(b, x, y);
                Assert.True(c.Red > 240, $"({x}, {y}): nur Rot und Weiß erwartet, {c}");
                if (c.Green < 60) red++;
                if (x % 10 == 7) Assert.True(c.Green > 240, $"({x}, {y}): in der Lücke der Zelle {c}");
            }
            Assert.True(red > 100, $"{red} rote Pixel");
            Assert.True(G874_White(G874_At(b, 2, 2)));
        }
        using (var b = G874_Render("/Pattern cs /P scn q 200 0 0 200 0 0 cm /M Do Q", G874_With(_ => G874_Tiling("1 0 0 rg 0 0 5 5 re f", 10, [0, 0, 10, 10]),
                   more: (pdf, r) => r.Set(TestPdf.N("XObject"), TestPdf.Dict(("M", TestPdf.Ref(pdf.Add(G874_StencilMask()))))))))
        {
            Assert.True(G874_Red(G874_At(b, 2, 2)), $"{G874_At(b, 2, 2)}");
            Assert.True(G874_White(G874_At(b, 7, 7)), $"{G874_At(b, 7, 7)}");
            Assert.True(G874_Red(G874_At(b, 52, 52)), $"{G874_At(b, 52, 52)}");
            Assert.True(G874_White(G874_At(b, 152, 2)), $"{G874_At(b, 152, 2)}");
        }
    }

    /// <summary>
    /// 8.7.3.3 sentences 2–5 and 8.6.8 sentence 104T (reader): an uncoloured cell must not paint an ordinary image; one that does (RGB 1 × 1 red over
    /// the lower left of a cell of 20) is passed over as in a d1 glyph — the cell paints nothing. Before, the image acted as a full stencil in the
    /// colour from „scn“ (docs/FEHLERGEDAECHTNIS.md, No. 1130).
    /// </summary>
    [Fact]
    public void UncolouredCell_WithAnImage_PassesItOver()
    {
        using var b = G874_Render("/CS cs 0 0 1 /P scn 0 0 200 200 re f", G874_With(pdf =>
        {
            var image = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(1)),
                ("Height", PdfInteger.Of(1)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8))), [255, 0, 0]));
            return G874_Tiling("q 10 0 0 10 0 0 cm /I Do Q", 20, [0, 0, 20, 20], 2, resources: TestPdf.Dict(("XObject", TestPdf.Dict(("I", TestPdf.Ref(image))))));
        }, G874_UncolouredRgb()));
        foreach (var (x, y) in new[] { (5, 5), (25, 5), (15, 15) }) Assert.True(G874_White(G874_At(b, x, y)), $"({x}, {y}): {G874_At(b, x, y)}");
    }

    /// <summary>
    /// 8.7.3.3 sentences 6–7 and 9–11 (behaviour of today): the underlying space of a Pattern space cannot be a Pattern space; [/Pattern /Pattern]
    /// with an uncoloured pattern paints the light grey fallback colour.
    /// </summary>
    [Fact]
    public void PatternOverPattern_PaintsTheLightGreyFallback()
    {
        using var b = G874_Render("/CS cs 1 0 0 /P scn 0 0 200 200 re f", G874_With(_ => G874_Tiling("0 0 10 10 re f", 10, [0, 0, 10, 10], 2),
            new PdfArray([TestPdf.N("Pattern"), TestPdf.N("Pattern")])));
        Assert.True(G874_LightGrey(G874_At(b, 100, 100)), $"{G874_At(b, 100, 100)}");
    }

    /// <summary>8.7.3.3 sentences 12 and 14–17: the same uncoloured pattern twice, first red, then blue — two areas in the two colours.</summary>
    [Fact]
    public void UncolouredPattern_SamePatternInTwoColours()
    {
        using var b = G874_Render("/CS cs 1 0 0 /P scn 0 0 100 200 re f 0 0 1 /P scn 100 0 100 200 re f",
            G874_With(_ => G874_Tiling("0 0 10 10 re f", 10, [0, 0, 10, 10], 2), G874_UncolouredRgb()));
        Assert.True(G874_Red(G874_At(b, 50, 100)), $"{G874_At(b, 50, 100)}");
        Assert.True(G874_Blue(G874_At(b, 150, 100)), $"{G874_At(b, 150, 100)}");
    }

    // ---------------------------------------------------------------- 8.7.4.1: shading patterns

    /// <summary>
    /// 8.7.4.1 sentences 22–23: a shading pattern colours glyphs too, the shading lying in pattern space independent of the glyph — an „H“ filled with
    /// red → blue over 0–200: its leftmost ink reddish, its rightmost bluish, nothing beside it at (2, 2).
    /// </summary>
    [Fact]
    public void ShadingPattern_FillsGlyphs()
    {
        using var b = G874_Render("/Pattern cs /P scn BT /F1 180 Tf 10 30 Td (H) Tj ET",
            G874_With(_ => TestPdf.Dict(("PatternType", PdfInteger.Of(2)), ("Shading", G874_Axial(0, 200, true))),
                more: (_, r) => r.Set(TestPdf.N("Font"), TestPdf.Dict(("F1", G874_Helvetica())))));
        (int X, SKColor C)? left = null, right = null;
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 200; x++)
        {
            var c = G874_At(b, x, y);
            if (c.Green > 60) continue;   // white and the smoothed edges (the shading itself has no green)
            if (left is null || x < left.Value.X) left = (x, c);
            if (right is null || x > right.Value.X) right = (x, c);
        }
        Assert.NotNull(left);
        Assert.True(left!.Value.C.Red > left.Value.C.Blue + 50, $"links {left}");
        Assert.True(right!.Value.C.Blue > right.Value.C.Red + 50, $"rechts {right}");
        Assert.True(right.Value.X - left.Value.X > 60, $"{left} … {right}");
        Assert.True(G874_White(G874_At(b, 2, 2)));
    }

    // ---------------------------------------------------------------- 8.7.4.2: sh

    /// <summary>
    /// 8.7.4.2 sentences 1–3, 6–7 and 11–12: „sh“ paints in the current user space — after „2 0 0 1 0 0 cm“ an axis 0–100 (without /Extend) reaches
    /// x 200 on the page: bluish at 196, half way at 100.
    /// </summary>
    [Fact]
    public void Sh_PaintsInTheCurrentUserSpace()
    {
        using var b = G874_Render("2 0 0 1 0 0 cm /Sh sh", G874_Sh(G874_Axial(0, 100, false)));
        Assert.True(G874_Blue(G874_At(b, 196, 100)), $"{G874_At(b, 196, 100)}");
        Assert.InRange(G874_At(b, 100, 100).Red, 100, 155);
        Assert.True(G874_At(b, 3, 100).Red > 200);
    }

    /// <summary>
    /// 8.7.4.2 sentences 8–10: „sh“ neither uses nor changes the current colour — „1 0 0 rg /Sh sh“ and a rectangle afterwards: red; and it keeps to the
    /// current clip — „q 50 50 100 100 re W n /Sh sh Q“ with /Extend: painted inside the square, white outside it.
    /// </summary>
    [Fact]
    public void Sh_KeepsTheColourAndTheClip()
    {
        using (var b = G874_Render("1 0 0 rg /Sh sh 0 0 10 10 re f", G874_Sh(G874_Axial(0, 200, true, G874_Exponential([0, 1, 0], [0, 1, 0])))))
        {
            Assert.True(G874_Red(G874_At(b, 5, 5)), $"{G874_At(b, 5, 5)}");
            var green = G874_At(b, 100, 100);
            Assert.True(green.Green > 200 && green.Red < 60, $"{green}");
        }
        using (var b = G874_Render("q 50 50 100 100 re W n /Sh sh Q", G874_Sh(G874_Axial(0, 200, true))))
        {
            Assert.False(G874_White(G874_At(b, 100, 100)));
            foreach (var (x, y) in new[] { (20, 100), (100, 170), (160, 100), (100, 30) })
                Assert.True(G874_White(G874_At(b, x, y)), $"({x}, {y}): {G874_At(b, x, y)}");
        }
    }

    // ---------------------------------------------------------------- 8.7.4.3: shading dictionaries

    /// <summary>
    /// 8.7.4.3 sentences 1–11 (type 7): a tensor-product patch of 16 points, 20 to 180, its interior points at the thirds — the corner colours red,
    /// green, blue, yellow at p00, p03, p33, p30.
    /// </summary>
    [Fact]
    public void TensorPatch_PutsTheCornerColoursAtTheirCorners()
    {
        var data = new List<byte> { 0 };
        int[] points = [20, 20, 20, 73, 20, 127, 20, 180, 73, 180, 127, 180, 180, 180, 180, 127, 180, 73, 180, 20, 127, 20, 73, 20,
            73, 73, 73, 127, 127, 127, 127, 73];
        data.AddRange(points.Select(p => (byte)p));
        data.AddRange([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]);
        var stream = new PdfStream(TestPdf.Dict(("ShadingType", PdfInteger.Of(7)), ("ColorSpace", TestPdf.N("DeviceRGB")),
            ("BitsPerCoordinate", PdfInteger.Of(8)), ("BitsPerComponent", PdfInteger.Of(8)), ("BitsPerFlag", PdfInteger.Of(8)),
            ("Decode", TestPdf.Box(0, 255, 0, 255, 0, 1, 0, 1, 0, 1))), [.. data]);
        using var b = G874_Render("/Sh sh", G874_Sh(stream));
        Assert.True(G874_Red(G874_At(b, 24, 24)), $"{G874_At(b, 24, 24)}");
        var green = G874_At(b, 24, 176);
        Assert.True(green.Green > 200 && green.Red < 60 && green.Blue < 60, $"{green}");
        Assert.True(G874_Blue(G874_At(b, 176, 176)), $"{G874_At(b, 176, 176)}");
        var yellow = G874_At(b, 176, 24);
        Assert.True(yellow.Red > 200 && yellow.Green > 200 && yellow.Blue < 60, $"{yellow}");
        Assert.True(G874_White(G874_At(b, 10, 10)));
    }

    /// <summary>
    /// 8.7.4.3 sentences 15–17: /ShadingType is required, 1 to 7 — a shading without it, or with 8, paints nothing with „sh“ (the case of 9 holds
    /// ShadingTests.UnreadableShading_ShPaintsNothing).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(8)]
    public void ShadingType_MissingOrEight_ShPaintsNothing(int? type)
    {
        var dict = G874_Axial(0, 200, true);
        if (type is { } t) dict.Set(TestPdf.N("ShadingType"), PdfInteger.Of(t));
        else dict.Remove(TestPdf.N("ShadingType"));
        using var b = G874_Render("/Sh sh", G874_Sh(dict));
        Assert.True(G874_White(G874_At(b, 100, 100)), $"{G874_At(b, 100, 100)}");
    }

    /// <summary>
    /// 8.7.4.3 sentences 18–22 (behaviour of today): /ColorSpace /Pattern is not allowed — such a shading paints the light grey fallback colour of the
    /// Pattern space; without /ColorSpace the values are read as DeviceGray (a function 0 → 1: dark on the left, light on the right).
    /// </summary>
    [Fact]
    public void ShadingColorSpace_PatternIsLightGrey_MissingIsGray_Today()
    {
        using (var b = G874_Render("/Sh sh", G874_Sh(G874_Axial(0, 200, true, colorSpace: TestPdf.N("Pattern")))))
            Assert.True(G874_LightGrey(G874_At(b, 100, 100)), $"{G874_At(b, 100, 100)}");
        using (var b = G874_Render("/Sh sh", G874_Sh(G874_Axial(0, 200, true, G874_Exponential([0], [1]), PdfNull.Instance))))
        {
            Assert.True(G874_At(b, 3, 100).Red < 20, $"{G874_At(b, 3, 100)}");
            var light = G874_At(b, 196, 100);
            Assert.True(light.Red > 235 && light.Red == light.Green && light.Green == light.Blue, $"{light}");
        }
    }

    /// <summary>
    /// 8.7.4.3 sentences 29–33: /BBox lies in the space of the shading — a shading pattern with /BBox [10 90 30 110] and /Matrix moving by 100 paints
    /// only at x 110–130, y 90–110 on the page; a /BBox of width 0 paints nothing (behaviour of today; the note on one pixel waits for M8).
    /// </summary>
    [Fact]
    public void ShadingBBox_ClipsInShadingSpace()
    {
        var shading = G874_Axial(0, 200, true);
        shading.Set(TestPdf.N("BBox"), TestPdf.Box(10, 90, 30, 110));
        using (var b = G874_Render("/Pattern cs /P scn 0 0 200 200 re f", G874_With(_ => TestPdf.Dict(("PatternType", PdfInteger.Of(2)),
                   ("Shading", shading), ("Matrix", TestPdf.Box(1, 0, 0, 1, 100, 0))))))
        {
            Assert.False(G874_White(G874_At(b, 120, 100)), $"{G874_At(b, 120, 100)}");
            foreach (var (x, y) in new[] { (105, 100), (135, 100), (20, 100), (120, 85), (120, 114) })
                Assert.True(G874_White(G874_At(b, x, y)), $"({x}, {y}): {G874_At(b, x, y)}");
        }
        var flat = G874_Axial(0, 200, true);
        flat.Set(TestPdf.N("BBox"), TestPdf.Box(100, 0, 100, 200));
        using (var b = G874_Render("/Sh sh", G874_Sh(flat)))
            foreach (var x in new[] { 99, 100, 101, 150 }) Assert.True(G874_White(G874_At(b, x, 100)), $"{x}: {G874_At(b, x, 100)}");
    }

    /// <summary>
    /// 8.7.4.3 sentences 34–36 and 40 (behaviour of today): /AntiAlias is passed over — an axial shading with a step function (red below t 0.5, blue
    /// above) draws the same pixels with /AntiAlias true and false.
    /// </summary>
    [Fact]
    public void AntiAlias_TrueOrFalse_SamePixels_Today()
    {
        SKBitmap Draw(bool antiAlias)
        {
            var step = TestPdf.Dict(("FunctionType", PdfInteger.Of(3)), ("Domain", TestPdf.Box(0, 1)), ("Bounds", TestPdf.Box(0.5)),
                ("Encode", TestPdf.Box(0, 1, 0, 1)), ("Functions", new PdfArray([G874_Exponential([1, 0, 0], [1, 0, 0]), G874_Exponential([0, 0, 1], [0, 0, 1])])));
            var shading = G874_Axial(0, 200, true, step);
            shading.Set(TestPdf.N("AntiAlias"), PdfBoolean.Of(antiAlias));
            return G874_Render("/Sh sh", G874_Sh(shading));
        }
        using var on = Draw(true);
        using var off = Draw(false);
        Assert.True(G874_Red(G874_At(on, 50, 100)) && G874_Blue(G874_At(on, 150, 100)));
        for (var y = 0; y < 200; y += 7)
        for (var x = 0; x < 200; x++)
            Assert.Equal(off.GetPixel(x, y), on.GetPixel(x, y));
    }

    // ---------------------------------------------------------------- 8.7.4.4: colour spaces of shadings

    /// <summary>
    /// 8.7.4.4 sentences 18–21: a device space other than that of the device is converted by the display's formulas — a triangle in DeviceCMYK with
    /// the corners cyan, magenta, yellow shows at each corner the colour the display gives that value, in between colours between them.
    /// </summary>
    [Fact]
    public void CmykMesh_CornersAsTheDisplayConvertsThem()
    {
        using var b = G874_Render("/Sh sh", G874_Sh(G874_Mesh(TestPdf.N("DeviceCMYK"), [0, 255, 0, 255, 0, 1, 0, 1, 0, 1, 0, 1],
            [0, 0, 0, 255, 0, 0, 0], [0, 200, 0, 0, 255, 0, 0], [0, 0, 200, 0, 0, 255, 0])));
        var cmyk = ColorSpace.DeviceCmyk.WithRendering(ColorSpace.RelativeColorimetric, ColorSpace.BpcDefault);
        Rgb cyan = cmyk.ToRgb([1, 0, 0, 0]), magenta = cmyk.ToRgb([0, 1, 0, 0]), yellow = cmyk.ToRgb([0, 0, 1, 0]);
        void Near(Rgb expected, SKColor actual) => Assert.True(Math.Abs(expected.R - actual.Red) <= 12 && Math.Abs(expected.G - actual.Green) <= 12
            && Math.Abs(expected.B - actual.Blue) <= 12, $"erwartet {expected}, gezeichnet {actual}");
        Near(cyan, G874_At(b, 2, 2));
        Near(magenta, G874_At(b, 195, 1));
        Near(yellow, G874_At(b, 1, 195));
        var middle = G874_At(b, 100, 1);
        Assert.InRange(middle.Red, Math.Min(cyan.R, magenta.R) - 8, Math.Max(cyan.R, magenta.R) + 8);
        Assert.InRange(middle.Green, Math.Min(cyan.G, magenta.G) - 8, Math.Max(cyan.G, magenta.G) + 8);
        Assert.InRange(middle.Blue, Math.Min(cyan.B, magenta.B) - 8, Math.Max(cyan.B, magenta.B) + 8);
    }

    /// <summary>
    /// 8.7.4.4 sentences 23–25: in a CIE-based space the shading is interpolated in that space and converted afterwards — a free triangle in /Lab
    /// without a function, corners L 100 and L 0 (a = b = 0): half way along that edge the lightness of L 50 converted (about 119), not the mean of
    /// the converted ends (128).
    /// </summary>
    [Fact]
    public void LabMesh_InterpolatedInLab()
    {
        var lab = new PdfArray([TestPdf.N("Lab"), TestPdf.Dict(("WhitePoint", TestPdf.Box(0.9505, 1, 1.089)))]);
        using var b = G874_Render("/Sh sh", G874_Sh(G874_Mesh(lab, [0, 255, 0, 255, 0, 100, -128, 127, -128, 127],
            [0, 0, 0, 255, 128, 128], [0, 200, 0, 0, 128, 128], [0, 0, 200, 255, 128, 128])));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var expected = ColorSpace.Create(lab, null, doc).WithRendering(ColorSpace.RelativeColorimetric, ColorSpace.BpcDefault).ToRgb([50, 0, 0]);
        var middle = G874_At(b, 100, 1);
        Assert.True(Math.Abs(middle.Red - expected.R) <= 4, $"erwartet etwa {expected}, gezeichnet {middle}");
        Assert.True(middle.Red <= 124, $"{middle}");
    }

    /// <summary>
    /// 8.7.4.4 sentences 26–28: in Separation the shading is interpolated in the tint, so a non-linear tint transformation counts — sampled white, red,
    /// black over the tint; corners with tint 0 and 1: half way red, not grey.
    /// </summary>
    [Fact]
    public void SeparationMesh_InterpolatedInTheTint()
    {
        using var b = G874_Render("/Sh sh", pdf =>
        {
            var tint = pdf.Add(new PdfStream(TestPdf.Dict(("FunctionType", PdfInteger.Of(0)), ("Domain", TestPdf.Box(0, 1)), ("Range", TestPdf.Box(0, 1, 0, 1, 0, 1)),
                ("Size", new PdfArray([PdfInteger.Of(3)])), ("BitsPerSample", PdfInteger.Of(8))), [255, 255, 255, 255, 0, 0, 0, 0, 0]));
            var space = new PdfArray([TestPdf.N("Separation"), TestPdf.N("Spot"), TestPdf.N("DeviceRGB"), TestPdf.Ref(tint)]);
            return G874_Sh(G874_Mesh(space, [0, 255, 0, 255, 0, 1], [0, 0, 0, 0], [0, 200, 0, 255], [0, 0, 200, 0]))(pdf);
        });
        Assert.True(G874_Red(G874_At(b, 100, 1)), $"rot erwartet: {G874_At(b, 100, 1)}");
    }

    /// <summary>
    /// 8.7.4.4 sentence 32 and 8.7.4.5.3 sentence 5 (reader; read leniently, docs/normabgleich/sonderregeln.md S-A A182, as one reference reader):
    /// Indexed must not be used with a function; an axial shading
    /// in [/Indexed /DeviceRGB 1 &lt;FF0000 0000FF&gt;] with a function 0 → 1 is read leniently, one palette entry per value — red up to half way, blue
    /// beyond, no crash.
    /// </summary>
    [Fact]
    public void IndexedAxialWithFunction_PaletteEntryPerValue()
    {
        var indexed = new PdfArray([TestPdf.N("Indexed"), TestPdf.N("DeviceRGB"), PdfInteger.Of(1), new PdfString([0xFF, 0, 0, 0, 0, 0xFF])]);
        using var b = G874_Render("/Sh sh", G874_Sh(G874_Axial(0, 200, true, G874_Exponential([0], [1]), indexed)));
        Assert.True(G874_Red(G874_At(b, 3, 100)), $"{G874_At(b, 3, 100)}");
        Assert.True(G874_Red(G874_At(b, 90, 100)), $"{G874_At(b, 90, 100)}");
        Assert.True(G874_Blue(G874_At(b, 110, 100)), $"{G874_At(b, 110, 100)}");
        Assert.True(G874_Blue(G874_At(b, 196, 100)), $"{G874_At(b, 196, 100)}");
    }

    /// <summary>
    /// 8.7.4.4 sentence 32 (writer) and 8.7.4.5.3 sentence 5 (writer): the shadings PDF-Knead writes have functions and are never Indexed — the axial
    /// shadings of the arrow heads of a dimension (`ElementAppearance.Dimensions`) are type 2 in /DeviceRGB (the cut meshes of `MeshCut`:
    /// M6NormTests.MeshCut_WritesType4With32BitCoordinatesInDeviceRgb).
    /// </summary>
    [Fact]
    public void DimensionHeads_ShadingsInDeviceRgb()
    {
        var dimension = ElementAppearance.Dimension(Measuring.Measure(MeasureKind.Length, [(100, 300), (200, 300)], []), 0x1E7B34, 1, 0, false, false, 0)!;
        var shadings = ElementAppearance.For(dimension).Form.Dictionary.GetDictionary(PdfName.Get("Resources"))!.GetDictionary(PdfName.Get("Shading"))!;
        Assert.NotEmpty(shadings.Entries);
        foreach (var (key, _) in shadings.Entries)
        {
            var shading = shadings.GetDictionary(key)!;
            Assert.Equal(2, shading.GetInteger(PdfName.Get("ShadingType")));
            Assert.Equal("DeviceRGB", shading.GetName(PdfName.Get("ColorSpace"))?.Value);
            Assert.IsType<PdfDictionary>(shading.Get(PdfName.Get("Function")));
        }
    }
}
