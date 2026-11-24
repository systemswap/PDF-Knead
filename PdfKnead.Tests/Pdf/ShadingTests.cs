using System.Diagnostics;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Shadings: types 1–7 with "sh", as patterns for fills and stencil masks, invalid and crafted ones.</summary>
public sealed class ShadingTests
{
    private static SKBitmap Render(string content, Func<TestPdf, PdfDictionary> resources)
    {
        var (doc, list) = InterpreterTests.Page(content, resources);
        using (doc) return SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
    }

    private static SKColor At(SKBitmap b, int x, int pdfY) => b.GetPixel(x, b.Height - 1 - pdfY);

    private static PdfArray Bools(params bool[] v) => new(v.Select(x => (PdfObject)PdfBoolean.Of(x)));

    /// <summary>Red → blue, linear.</summary>
    private static PdfDictionary RedToBlue() => TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)),
        ("C0", TestPdf.Box(1, 0, 0)), ("C1", TestPdf.Box(0, 0, 1)), ("N", PdfInteger.Of(1)));

    private static PdfDictionary Axial(double x0, double x1, bool extendStart, bool extendEnd, PdfObject? function = null) => TestPdf.Dict(
        ("ShadingType", PdfInteger.Of(2)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Coords", TestPdf.Box(x0, 0, x1, 0)),
        ("Function", function ?? RedToBlue()), ("Extend", Bools(extendStart, extendEnd)));

    private static Func<TestPdf, PdfDictionary> Shading(PdfObject shading) =>
        _ => TestPdf.Dict(("Shading", TestPdf.Dict(("Sh", shading))));

    private static Func<TestPdf, PdfDictionary> Stream(PdfDictionary dict, byte[] data) =>
        pdf => TestPdf.Dict(("Shading", TestPdf.Dict(("Sh", TestPdf.Ref(pdf.Add(new PdfStream(dict, data)))))));

    private static void Reddish(SKColor c) => Assert.True(c.Red > 200 && c.Blue < 60, $"nicht rot: {c}");
    private static void Bluish(SKColor c) => Assert.True(c.Blue > 200 && c.Red < 60, $"nicht blau: {c}");

    private static PdfDictionary Radial(double x, double y, double r, bool extend) => TestPdf.Dict(
        ("ShadingType", PdfInteger.Of(3)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Coords", TestPdf.Box(x, y, 0, x, y, r)),
        ("Function", RedToBlue()), ("Extend", Bools(extend, extend)));

    /// <summary>The area the only entry of the page's display list fills (page space, 200 × 200 pt).</summary>
    private static RectD Filled(string content, Func<TestPdf, PdfDictionary> resources)
    {
        var (doc, list) = InterpreterTests.Page(content, resources);
        using (doc)
        {
            var item = Assert.Single(list.Items.ToArray());
            return RectD.FromLtrb(item.Left, item.Top, item.Right, item.Bottom);
        }
    }

    private static void Near(RectD expected, RectD actual)
    {
        foreach (var (e, a) in new[] { (expected.Left, actual.Left), (expected.Top, actual.Top), (expected.Right, actual.Right), (expected.Bottom, actual.Bottom) })
            Assert.True(Math.Abs(e - a) < 0.5, $"erwartet {expected}, gefüllt {actual}");
    }

    /// <summary>
    /// „sh“ fills only where the shading paints (fuzzing S7): the circles of a radial shading and the band of a linear one without
    /// /Extend, the /Domain of a function-based one, the triangles of a mesh, the /BBox — with /Extend the whole clip; a shading off the
    /// page adds nothing. (2 pt more on each side for smoothed edges.)
    /// </summary>
    [Fact]
    public void Sh_FillsOnlyWhereTheShadingPaints()
    {
        Near(RectD.FromLtrb(38, 78, 82, 122), Filled("/Sh sh", Shading(Radial(60, 100, 20, false))));
        Near(RectD.FromLtrb(0, 0, 200, 200), Filled("/Sh sh", Shading(Radial(60, 100, 20, true))));
        Near(RectD.FromLtrb(48, 0, 152, 200), Filled("/Sh sh", Shading(Axial(50, 150, false, false))));
        Near(RectD.FromLtrb(0, 0, 152, 200), Filled("/Sh sh", Shading(Axial(50, 150, true, false))));
        Near(RectD.FromLtrb(48, 0, 200, 200), Filled("/Sh sh", Shading(Axial(50, 150, false, true))));
        var boxed = Axial(0, 200, true, true);
        boxed.Set(PdfName.Get("BBox"), TestPdf.Box(10, 90, 30, 110));
        Near(RectD.FromLtrb(8, 88, 32, 112), Filled("/Sh sh", Shading(boxed)));

        var function = new PdfStream(TestPdf.Dict(("FunctionType", PdfInteger.Of(4)), ("Domain", TestPdf.Box(0, 1, 0, 1)),
            ("Range", TestPdf.Box(0, 1))), System.Text.Encoding.ASCII.GetBytes("{ pop }"));
        Near(RectD.FromLtrb(18, 0, 122, 200), Filled("/Sh sh", pdf => TestPdf.Dict(("Shading", TestPdf.Dict(("Sh", TestPdf.Dict(
            ("ShadingType", PdfInteger.Of(1)), ("ColorSpace", TestPdf.N("DeviceGray")), ("Domain", TestPdf.Box(0, 1, 0, 1)),
            ("Matrix", TestPdf.Box(100, 0, 0, 200, 20, 0)), ("Function", TestPdf.Ref(pdf.Add(function))))))))));

        var mesh = TestPdf.Dict(("ShadingType", PdfInteger.Of(4)), ("ColorSpace", TestPdf.N("DeviceRGB")),
            ("BitsPerCoordinate", PdfInteger.Of(8)), ("BitsPerComponent", PdfInteger.Of(8)), ("BitsPerFlag", PdfInteger.Of(8)),
            ("Decode", TestPdf.Box(0, 255, 0, 255, 0, 1, 0, 1, 0, 1)));
        Near(RectD.FromLtrb(18, 78, 122, 122), Filled("/Sh sh", Stream(mesh, [0, 20, 80, 255, 0, 0, 0, 120, 80, 0, 255, 0, 0, 20, 120, 0, 0, 255])));

        var (doc, list) = InterpreterTests.Page("/Sh sh", Shading(Radial(500, 100, 20, false)));
        using (doc) Assert.Equal(0, list.Count);
    }

    /// <summary>
    /// A Type 3 font with a radial shading in its glyph, shown 20,000 times in one string (fuzzing S7): the glyphs run far off the page,
    /// their shadings paint nothing there and are not drawn — before, each filled the whole page (18 s at 400 % zoom). The glyphs on the
    /// page still show their shading.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void Type3_ShadingPerGlyph_OnlyThoseOnThePageAreDrawn()
    {
        var text = "(" + new string('a', 20_000) + ") Tj";
        var (doc, list) = InterpreterTests.Page($"BT /T3 50 Tf 10 100 Td {text} ET", pdf =>
        {
            var proc = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("1000 0 d0 /Sh sh")));
            var font = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")),
                ("FontMatrix", TestPdf.Box(0.001, 0, 0, 0.001, 0, 0)), ("FontBBox", TestPdf.Box(0, 0, 1000, 1000)),
                ("CharProcs", TestPdf.Dict(("g", TestPdf.Ref(proc)))),
                ("Encoding", TestPdf.Dict(("Differences", new PdfArray([PdfInteger.Of(97), TestPdf.N("g")])))),
                ("FirstChar", PdfInteger.Of(97)), ("LastChar", PdfInteger.Of(97)), ("Widths", new PdfArray([PdfInteger.Of(1000)])),
                ("Resources", TestPdf.Dict(("Shading", TestPdf.Dict(("Sh", Radial(500, 500, 400, false)))))));
            return TestPdf.Dict(("Font", TestPdf.Dict(("T3", font))));
        });
        using (doc)
        {
            Assert.InRange(list.Count, 4, 6);   // 50 pt apart on a page 200 pt wide
            var watch = Stopwatch.StartNew();
            using var b = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 4, new ImageCache());
            Assert.True(watch.ElapsedMilliseconds < 2000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
            Reddish(b.GetPixel(4 * 35, b.Height - 1 - 4 * 125));   // the centre of the first glyph's shading
        }
    }

    [Fact]
    public void Axial_RunsFromStartToEndColor()
    {
        using var b = Render("/Sh sh", Shading(Axial(0, 200, true, true)));
        Reddish(At(b, 3, 100));
        Bluish(At(b, 196, 100));
        var mid = At(b, 100, 100);
        Assert.InRange(mid.Red, 100, 155);
        Assert.InRange(mid.Blue, 100, 155);
    }

    [Fact]
    public void Axial_WithoutExtend_LeavesTheEndsEmpty_OneSidedExtendsOnlyThatSide()
    {
        using (var b = Render("/Sh sh", Shading(Axial(50, 150, false, false))))
        {
            Assert.Equal(SKColors.White, At(b, 20, 100));
            Assert.Equal(SKColors.White, At(b, 180, 100));
            Assert.NotEqual(SKColors.White, At(b, 100, 100));
        }
        using (var b = Render("/Sh sh", Shading(Axial(50, 150, true, false))))
        {
            Reddish(At(b, 20, 100));                          // extended before the start
            Assert.Equal(SKColors.White, At(b, 180, 100));    // not after the end
        }
        using (var b = Render("/Sh sh", Shading(Axial(50, 150, false, true))))
        {
            Assert.Equal(SKColors.White, At(b, 20, 100));
            Bluish(At(b, 180, 100));
        }
    }

    [Fact]
    public void SameShading_ManyTimes_SharesOneShader()
    {
        // One Skia shader for all places: only an owner of its own shader may release it (identity matrix: the same one, otherwise a crash)
        var (doc, list) = InterpreterTests.Page("/Sh sh q 0.5 0 0 1 0 0 cm /Sh sh Q /Sh sh /Pattern cs /P scn 0 0 50 50 re f",
            pdf => TestPdf.Dict(("Shading", TestPdf.Dict(("Sh", Axial(0, 200, true, true)))),
                ("Pattern", TestPdf.Dict(("P", TestPdf.Ref(pdf.Add(TestPdf.Dict(("PatternType", PdfInteger.Of(2)), ("Shading", Axial(0, 200, true, true))))))))));
        using (doc)
            for (var i = 0; i < 3; i++)
            {
                using var b = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
                Reddish(At(b, 3, 100));
            }
    }

    [Fact]
    public void Radial_RunsFromCenterOutwards()
    {
        var radial = TestPdf.Dict(("ShadingType", PdfInteger.Of(3)), ("ColorSpace", TestPdf.N("DeviceRGB")),
            ("Coords", TestPdf.Box(100, 100, 0, 100, 100, 80)), ("Function", RedToBlue()));
        using var b = Render("/Sh sh", Shading(radial));
        Reddish(At(b, 100, 100));
        Bluish(At(b, 176, 100));
        Assert.Equal(SKColors.White, At(b, 5, 5));   // outside the circle, without Extend
    }

    /// <summary>
    /// radial_gradients pages 4/5: the start point lies outside the end circle, without /Extend at the end; the whole end circle belongs
    /// to the shading (the smaller value applies there because the larger one lies beyond 1). Otherwise it stays empty except for a crescent.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Radial_PointOutsideEndCircle_WithoutExtendEnd_FillsTheEndCircle(bool extendStart)
    {
        var radial = TestPdf.Dict(("ShadingType", PdfInteger.Of(3)), ("ColorSpace", TestPdf.N("DeviceRGB")),
            ("Coords", TestPdf.Box(150, 100, 0, 60, 100, 40)), ("Function", RedToBlue()), ("Extend", Bools(extendStart, false)));
        using var b = Render("/Sh sh", Shading(radial));
        Bluish(At(b, 40, 100));     // t = 0.85 (the larger value 2.2 lies beyond the end)
        Bluish(At(b, 40, 110));     // slightly above, t = 0.86
        Reddish(At(b, 146, 100));   // close to the start point
        Assert.Equal(SKColors.White, At(b, 100, 180));   // outside the cone
    }

    [Fact]
    public void Pattern_FillsOnlyThePath_InPatternSpace()
    {
        // Pattern with a matrix: shading 0..100 in pattern space, shifted 100 to the right
        var pattern = TestPdf.Dict(("PatternType", PdfInteger.Of(2)), ("Shading", Axial(0, 100, true, true)),
            ("Matrix", TestPdf.Box(1, 0, 0, 1, 100, 0)));
        using var b = Render("/Pattern cs /P scn 0 0 200 100 re f",
            pdf => TestPdf.Dict(("Pattern", TestPdf.Dict(("P", TestPdf.Ref(pdf.Add(pattern)))))));
        Reddish(At(b, 50, 50));                           // before the start: extended red
        Bluish(At(b, 197, 50));
        Assert.Equal(SKColors.White, At(b, 100, 150));    // outside the path
    }

    [Fact]
    public void Pattern_ColorsAStencilMask()
    {
        // Stencil mask 2 × 1: left painted (bit 0), right not; coloured with the shading instead of a fallback colour
        var pattern = TestPdf.Dict(("PatternType", PdfInteger.Of(2)), ("Shading", Axial(0, 200, true, true)));
        var mask = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(1)), ("ImageMask", PdfBoolean.True)), [0b0100_0000]);
        using var b = Render("/Pattern cs /P scn q 200 0 0 200 0 0 cm /M Do Q", pdf => TestPdf.Dict(
            ("Pattern", TestPdf.Dict(("P", TestPdf.Ref(pdf.Add(pattern))))),
            ("XObject", TestPdf.Dict(("M", TestPdf.Ref(pdf.Add(mask)))))));
        Reddish(At(b, 10, 100));
        var mid = At(b, 90, 100);
        Assert.True(mid.Red > 80 && mid.Blue > 60, $"kein Verlauf: {mid}");
        Assert.Equal(SKColors.White, At(b, 150, 100));
    }

    [Fact]
    public void FreeFormMesh_InterpolatesVertexColors()
    {
        // Type 4, one triangle: (0,0) red, (200,0) green, (0,200) blue; one byte per value, Decode onto the page
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(4)), ("ColorSpace", TestPdf.N("DeviceRGB")),
            ("BitsPerCoordinate", PdfInteger.Of(8)), ("BitsPerComponent", PdfInteger.Of(8)), ("BitsPerFlag", PdfInteger.Of(8)),
            ("Decode", TestPdf.Box(0, 255, 0, 255, 0, 1, 0, 1, 0, 1)));
        byte[] data = [0, 0, 0, 255, 0, 0, 0, 200, 0, 0, 255, 0, 0, 0, 200, 0, 0, 255];
        using var b = Render("/Sh sh", Stream(dict, data));
        Reddish(At(b, 4, 4));
        var green = At(b, 190, 4);
        Assert.True(green.Green > 200 && green.Red < 60, $"nicht grün: {green}");
        Bluish(At(b, 4, 190));
        Assert.Equal(SKColors.White, At(b, 150, 150));   // outside the triangle
    }

    /// <summary>
    /// A free-form mesh in an /Indexed space (palette 0 red, 1 green, 2 blue) without a function: between a red and a blue vertex the colours
    /// blend to violet (docs/FEHLERGEDAECHTNIS.md, No. 595). Before, the palette indices were interpolated — entry 1, green, in the middle.
    /// </summary>
    [Fact]
    public void FreeFormMesh_Indexed_BlendsTheColoursNotThePaletteEntries()
    {
        var palette = new PdfString(new byte[] { 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0 });
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(4)), ("ColorSpace", new PdfArray([TestPdf.N("Indexed"), TestPdf.N("DeviceRGB"), PdfInteger.Of(3), palette])),
            ("BitsPerCoordinate", PdfInteger.Of(8)), ("BitsPerComponent", PdfInteger.Of(8)), ("BitsPerFlag", PdfInteger.Of(8)),
            ("Decode", TestPdf.Box(0, 255, 0, 255, 0, 255)));
        byte[] data = [0, 0, 0, 0, 0, 200, 0, 2, 0, 0, 200, 2];
        using var b = Render("/Sh sh", Stream(dict, data));
        Reddish(At(b, 4, 4));
        var middle = At(b, 100, 4);
        Assert.True(middle.Red > 60 && middle.Blue > 60 && middle.Green < 60, $"nicht violett: {middle}");
    }

    /// <summary>
    /// A crafted free-form mesh of 5,000 triangles, each many pages large: only the triangles up to 64 sheets of A0 are kept — drawing costs
    /// what they cover (6 s per page before); an ordinary mesh of 5,000 small triangles stays whole (docs/FEHLERGEDAECHTNIS.md, No. 277).
    /// </summary>
    [Fact]
    public void FreeFormMesh_HugeTriangles_AreLimitedByArea()
    {
        int Triangles(double extent, int count)
        {
            var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(4)), ("ColorSpace", TestPdf.N("DeviceRGB")),
                ("BitsPerCoordinate", PdfInteger.Of(8)), ("BitsPerComponent", PdfInteger.Of(8)), ("BitsPerFlag", PdfInteger.Of(8)),
                ("Decode", TestPdf.Box(-extent, extent, -extent, extent, 0, 1, 0, 1, 0, 1)));
            var data = new List<byte>();
            for (var t = 0; t < count; t++)
                data.AddRange([0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 255, 0, 0, 0, 255, 0, 0, 255]);   // corners (−e, −e), (e, −e), (−e, e)
            var (doc, list) = InterpreterTests.Page("/Sh sh", Stream(dict, [.. data]));
            using (doc) return list.Shadings.OfType<MeshFill>().Sum(m => m.Colors.Length / 3);
        }
        var huge = Triangles(20_000, 5_000);   // each 8·10⁸ pt²: about the area of 100 sheets of A0
        Assert.InRange(huge, 1, 2);
        Assert.Equal(5_000, Triangles(20, 5_000));   // 800 pt² each
    }

    [Fact]
    public void CoonsPatch_PutsCornerColorsAtTheirCorners()
    {
        // Type 6, one patch 20..180: boundary counter-clockwise from the bottom left, colours at corners 1, 4, 7, 10
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(6)), ("ColorSpace", TestPdf.N("DeviceRGB")),
            ("BitsPerCoordinate", PdfInteger.Of(8)), ("BitsPerComponent", PdfInteger.Of(8)), ("BitsPerFlag", PdfInteger.Of(8)),
            ("Decode", TestPdf.Box(0, 255, 0, 255, 0, 1, 0, 1, 0, 1)));
        var data = new List<byte> { 0 };
        int[] points = [20, 20, 20, 73, 20, 127, 20, 180, 73, 180, 127, 180, 180, 180, 180, 127, 180, 73, 180, 20, 127, 20, 73, 20];
        data.AddRange(points.Select(p => (byte)p));
        data.AddRange([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]);   // red, green, blue, yellow
        using var b = Render("/Sh sh", Stream(dict, [.. data]));
        Reddish(At(b, 24, 24));
        var green = At(b, 24, 176);
        Assert.True(green.Green > 200 && green.Red < 60, $"nicht grün: {green}");
        Bluish(At(b, 176, 176));
        var yellow = At(b, 176, 24);
        Assert.True(yellow.Red > 200 && yellow.Green > 200 && yellow.Blue < 60, $"nicht gelb: {yellow}");
        Assert.Equal(SKColors.White, At(b, 10, 10));
    }

    [Fact]
    public void InvalidFunction_DrawsNothing()
    {
        // A function with two inputs for a type 2 shading: invalid, no reader draws it (not even a fallback colour)
        var function = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1, 0, 1)),
            ("C0", TestPdf.Box(1, 0, 0)), ("C1", TestPdf.Box(0, 0, 1)), ("N", PdfInteger.Of(1)));
        using var b = Render("/Sh sh", Shading(Axial(0, 200, true, true, function)));
        Assert.Equal(SKColors.White, At(b, 100, 100));
    }

    [Fact]
    public void Background_FillsOutsideTheShading_OnlyAsPattern()
    {
        // Shading 50…150 without Extend, /Background green: green outside as a pattern, nothing with "sh" (Table 77)
        PdfDictionary WithBackground()
        {
            var shading = Axial(50, 150, false, false);
            shading.Set(TestPdf.N("Background"), TestPdf.Box(0, 1, 0));
            return shading;
        }
        using (var b = Render("/Pattern cs /P scn 0 0 200 200 re f", pdf => TestPdf.Dict(("Pattern", TestPdf.Dict(("P",
                   TestPdf.Ref(pdf.Add(TestPdf.Dict(("PatternType", PdfInteger.Of(2)), ("Shading", WithBackground()))))))))))
        {
            Assert.Equal(SKColors.Lime, At(b, 20, 100));
            Assert.NotEqual(SKColors.Lime, At(b, 100, 100));
        }
        using (var b = Render("/Sh sh", Shading(WithBackground())))
            Assert.Equal(SKColors.White, At(b, 20, 100));
    }

    [Fact]
    public void FunctionBased_IsSampledAndClippedToTheDomain()
    {
        // Type 1: grey = x over /Domain [0 1 0 1], stretched to 100 × 200 pt; nothing to the right of it
        var function = new PdfStream(TestPdf.Dict(("FunctionType", PdfInteger.Of(4)), ("Domain", TestPdf.Box(0, 1, 0, 1)),
            ("Range", TestPdf.Box(0, 1))), System.Text.Encoding.ASCII.GetBytes("{ pop }"));
        using var b = Render("/Sh sh", pdf => TestPdf.Dict(("Shading", TestPdf.Dict(("Sh", TestPdf.Dict(("ShadingType", PdfInteger.Of(1)),
            ("ColorSpace", TestPdf.N("DeviceGray")), ("Domain", TestPdf.Box(0, 1, 0, 1)), ("Matrix", TestPdf.Box(100, 0, 0, 200, 0, 0)),
            ("Function", TestPdf.Ref(pdf.Add(function)))))))));
        Assert.True(At(b, 2, 100).Red < 20, $"links dunkel erwartet: {At(b, 2, 100)}");
        var mid = At(b, 50, 100);
        Assert.InRange(mid.Red, 110, 145);
        Assert.True(At(b, 97, 100).Red > 235);
        Assert.Equal(SKColors.White, At(b, 150, 100));   // outside the /Domain
    }

    [Fact]
    public void StrokeWithPattern_UsesTheAlphaOnce()
    {
        // CA 0.5 and a shading pattern red → red: half red (not 25 %, which results when the opacity is applied twice)
        var red = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(1, 0, 0)),
            ("C1", TestPdf.Box(1, 0, 0)), ("N", PdfInteger.Of(1)));
        var pattern = TestPdf.Dict(("PatternType", PdfInteger.Of(2)), ("Shading", Axial(0, 200, true, true, red)));
        using var b = Render("/H gs /Pattern CS /P SCN 40 w 0 100 m 200 100 l S", pdf => TestPdf.Dict(
            ("ExtGState", TestPdf.Dict(("H", TestPdf.Dict(("CA", new PdfReal(0.5)))))),
            ("Pattern", TestPdf.Dict(("P", TestPdf.Ref(pdf.Add(pattern)))))));
        var c = At(b, 100, 100);
        Assert.True(c.Red > 245 && c.Green is > 115 and < 140, $"halb Rot erwartet: {c}");
    }

    [Fact]
    public void TransparentMesh_IsPaintedAsAWhole()
    {
        // Two overlapping triangles (red, then blue), ca 0.5: half blue in the overlap, not blue over half red
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(4)), ("ColorSpace", TestPdf.N("DeviceRGB")),
            ("BitsPerCoordinate", PdfInteger.Of(8)), ("BitsPerComponent", PdfInteger.Of(8)), ("BitsPerFlag", PdfInteger.Of(8)),
            ("Decode", TestPdf.Box(0, 255, 0, 255, 0, 1, 0, 1, 0, 1)));
        byte[] data =
        [
            0, 0, 0, 255, 0, 0, 0, 200, 0, 255, 0, 0, 0, 0, 200, 255, 0, 0,
            0, 0, 0, 0, 0, 255, 0, 200, 0, 0, 0, 255, 0, 0, 200, 0, 0, 255,
        ];
        using var b = Render("/H gs /Sh sh", pdf => TestPdf.Dict(
            ("ExtGState", TestPdf.Dict(("H", TestPdf.Dict(("ca", new PdfReal(0.5)))))),
            ("Shading", TestPdf.Dict(("Sh", TestPdf.Ref(pdf.Add(new PdfStream(dict, data))))))));
        var c = At(b, 30, 30);
        Assert.True(c.Blue > 245 && c.Red is > 115 and < 140 && c.Green is > 115 and < 140, $"halb Blau erwartet: {c}");
    }

    [Fact]
    public void ManyDrawsOfABigMesh_HitTheWorkLimit()
    {
        // Mesh of many triangles drawn very often: the triangles count towards the work limit of the page
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(4)), ("ColorSpace", TestPdf.N("DeviceRGB")),
            ("BitsPerCoordinate", PdfInteger.Of(8)), ("BitsPerComponent", PdfInteger.Of(8)), ("BitsPerFlag", PdfInteger.Of(8)),
            ("Decode", TestPdf.Box(0, 255, 0, 255, 0, 1, 0, 1, 0, 1)));
        var data = new List<byte>();
        for (var t = 0; t < 100_000; t++)
        {
            byte x = (byte)(t % 250), y = (byte)(t / 250 % 250);
            data.AddRange([0, x, y, 255, 0, 0, 0, (byte)(x + 1), y, 0, 255, 0, 0, x, (byte)(y + 1), 0, 0, 255]);
        }
        var (doc, list) = InterpreterTests.Page(string.Concat(Enumerable.Repeat("/Sh sh\n", 2000)),
            pdf => TestPdf.Dict(("Shading", TestPdf.Dict(("Sh", TestPdf.Ref(pdf.Add(new PdfStream(dict, [.. data]))))))));
        using (doc) Assert.InRange(list.Count, 1, 60);   // 16 million / (4 × 100 000) ≈ 40 draws, then stop
    }

    [Fact]
    public void HugeFunctionArray_IsRefused_WithoutStackOverflow()
    {
        // 200 000 parts: without a limit a stack overflow in the fallback colour (the process terminates)
        var (doc, list) = InterpreterTests.Page("/Sh sh", pdf =>
        {
            var part = TestPdf.Ref(pdf.Add(TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)),
                ("C0", TestPdf.Box(0)), ("C1", TestPdf.Box(1)), ("N", PdfInteger.Of(1)))));
            var parts = new PdfArray(Enumerable.Repeat<PdfObject>(part, 200_000));
            return TestPdf.Dict(("Shading", TestPdf.Dict(("Sh", TestPdf.Dict(("ShadingType", PdfInteger.Of(2)),
                ("ColorSpace", TestPdf.N("DeviceRGB")), ("Coords", TestPdf.Box(0, 0, 200, 0)), ("Function", parts))))));
        });
        using (doc) Assert.Empty(list.Items.ToArray());   // invalid: nothing drawn
    }

    [Fact]
    public void ReversedDomain_StillEvaluates()
    {
        // /Domain [1 0]: Math.Clamp throws for it (66 times in fuzzing, caught); evaluated like [0 1]
        var function = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(1, 0)),
            ("C0", TestPdf.Box(1, 0, 0)), ("C1", TestPdf.Box(0, 0, 1)), ("N", PdfInteger.Of(1)));
        using var b = Render("/Sh sh", Shading(Axial(0, 200, true, true, function)));
        Reddish(At(b, 3, 100));
        Bluish(At(b, 196, 100));
    }

    [Fact]
    public void LatticeWithHugeRows_IsRefused()
    {
        // Type 5 with a huge row length: triangles arise only after one row; without a limit 8 KB of data allocated 538 MB
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(5)), ("ColorSpace", TestPdf.N("DeviceRGB")),
            ("VerticesPerRow", PdfInteger.Of(int.MaxValue)), ("BitsPerCoordinate", PdfInteger.Of(1)), ("BitsPerComponent", PdfInteger.Of(1)),
            ("Decode", TestPdf.Box(0, 1, 0, 1, 0, 1, 0, 1, 0, 1)));
        var before = GC.GetAllocatedBytesForCurrentThread();   // this thread only: other tests run in parallel
        using var b = Render("/Sh sh", Stream(dict, new byte[1 << 20]));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 << 20, "zu viel Speicher");
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void RejectedTriangles_DoNotReadTheWholeStream()
    {
        // All triangles rejected (infinite coordinates): the points read are counted, not the accepted triangles
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(4)), ("ColorSpace", TestPdf.N("DeviceRGB")),
            ("BitsPerCoordinate", PdfInteger.Of(8)), ("BitsPerComponent", PdfInteger.Of(8)), ("BitsPerFlag", PdfInteger.Of(8)),
            ("Decode", TestPdf.Box(0, 1e39, 0, 1e39, 0, 1, 0, 1, 0, 1)));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var data = new byte[64 << 20];
        Array.Fill(data, (byte)0xFF);
        using var b = Render("/Sh sh", Stream(dict, data));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5 * TestKind.Factor), $"{watch.Elapsed}");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void CraftedMeshes_DoNotCrash(int type)
    {
        // Truncated data, huge bit counts, row length 0 and edge flags without a predecessor: draw what is possible, never crash
        var random = new Random(type);
        foreach (var bits in new[] { 1, 8, 16, 32, 64, 0, -3 })
        {
            var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(type)), ("ColorSpace", TestPdf.N("DeviceRGB")),
                ("BitsPerCoordinate", PdfInteger.Of(bits)), ("BitsPerComponent", PdfInteger.Of(8)), ("BitsPerFlag", PdfInteger.Of(8)),
                ("VerticesPerRow", PdfInteger.Of(bits % 3)), ("Decode", TestPdf.Box(0, 1e30, -1e30, 1e30, 0, 1, 0, 1, 0, 1)));
            var data = new byte[random.Next(0, 400)];
            random.NextBytes(data);
            if (data.Length > 0) data[0] = 3;
            using var b = Render("/Sh sh", Stream(dict, data));
            Assert.Equal(200, b.Width);
        }
    }

    // ---- 7.4 E-09: free-form and lattice meshes with a function interpolate t, not the colours (ISO 32000-2, 8.7.4.5.5, 8.7.4.5.6;
    // docs/FEHLERGEDAECHTNIS.md, No. 569)

    /// <summary>Mesh with t from 0 to 1 and a sampled function red → green → blue (three stops); one byte per value, Decode onto the page.</summary>
    private static Func<TestPdf, PdfDictionary> MeshWithStops(int type, byte[] data) => pdf =>
    {
        var function = pdf.Add(new PdfStream(TestPdf.Dict(("FunctionType", PdfInteger.Of(0)), ("Domain", TestPdf.Box(0, 1)),
            ("Range", TestPdf.Box(0, 1, 0, 1, 0, 1)), ("Size", new PdfArray([PdfInteger.Of(3)])), ("BitsPerSample", PdfInteger.Of(8))),
            [255, 0, 0, 0, 255, 0, 0, 0, 255]));
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(type)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Function", TestPdf.Ref(function)),
            ("BitsPerCoordinate", PdfInteger.Of(8)), ("BitsPerComponent", PdfInteger.Of(8)), ("Decode", TestPdf.Box(0, 255, 0, 255, 0, 1)));
        if (type == 4) dict.Set(TestPdf.N("BitsPerFlag"), PdfInteger.Of(8));
        else dict.Set(TestPdf.N("VerticesPerRow"), PdfInteger.Of(2));
        return TestPdf.Dict(("Shading", TestPdf.Dict(("Sh", TestPdf.Ref(pdf.Add(new PdfStream(dict, data)))))));
    };

    private static void Greenish(SKColor c) => Assert.True(c.Green > 200 && c.Red < 60 && c.Blue < 60, $"nicht grün: {c}");

    /// <summary>
    /// Type 4, one triangle: (0,0) and (200,0) with t = 0, (0,200) with t = 1. Halfway up t is 0.5 — green. Before: red and blue mixed at
    /// the corners gave violet.
    /// </summary>
    [Fact]
    public void FreeFormMeshWithFunction_InterpolatesTheParameter()
    {
        using var b = Render("/Sh sh", MeshWithStops(4, [0, 0, 0, 0, 0, 200, 0, 0, 0, 0, 200, 255]));
        Reddish(At(b, 4, 4));
        Greenish(At(b, 4, 100));
        Bluish(At(b, 2, 196));
    }

    /// <summary>The same as type 5: rows (0,0) (200,0) with t = 0 and (0,200) (200,200) with t = 1 — green across the middle.</summary>
    [Fact]
    public void LatticeMeshWithFunction_InterpolatesTheParameter()
    {
        using var b = Render("/Sh sh", MeshWithStops(5, [0, 0, 0, 200, 0, 0, 0, 200, 255, 200, 200, 255]));
        Reddish(At(b, 100, 4));
        Greenish(At(b, 50, 100));
        Greenish(At(b, 150, 100));
        Bluish(At(b, 100, 196));
    }

    /// <summary>
    /// A type 4 mesh whose coordinates all lie between 1e38 and 3e38 (finite each, but the sum of any triangle's coordinates is no finite float)
    /// with a steep function between its corners: every triangle and every part of it is dropped. Before, each was first split six levels
    /// deep with four function evaluations per level and only then dropped — the triangle limit never counted them, so 1.5 million points
    /// were read: measured 2.7 s per 10 000 triangles, over two minutes for this stream (M6 draft h-8745, security; docs/FEHLERGEDAECHTNIS.md,
    /// No. 1122).
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void DroppedTriangles_NotSplitFirst()
    {
        var steep = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)),
            ("C0", TestPdf.Box(1, 0, 0)), ("C1", TestPdf.Box(0, 0, 1)), ("N", PdfInteger.Of(50)));
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(4)), ("ColorSpace", TestPdf.N("DeviceRGB")),
            ("BitsPerCoordinate", PdfInteger.Of(8)), ("BitsPerComponent", PdfInteger.Of(8)), ("BitsPerFlag", PdfInteger.Of(8)),
            ("Decode", TestPdf.Box(1e38, 3e38, 1e38, 3e38, 0, 1)), ("Function", steep));
        // flag 0, x, y, t per vertex: corners far apart in t, so that a split would always go to the full depth
        var data = new byte[4 * 1_500_000];
        for (var v = 0; v < 1_500_000; v++)
        {
            data[4 * v + 1] = (byte)(v % 3 == 1 ? 255 : 0);
            data[4 * v + 2] = (byte)(v % 3 == 2 ? 255 : 0);
            data[4 * v + 3] = (byte)(v % 2 == 0 ? 0 : 255);
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var b = Render("/Sh sh", Stream(dict, data));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5 * TestKind.Factor), $"{watch.Elapsed}");
    }

    /// <summary>
    /// 8.7.4.5.6 with 8.7.4.5.5: every vertex of a lattice-form mesh begins at a whole byte — with 4-bit coordinates and a 4-bit grey (12 bits per
    /// vertex) the four corners of a black square read right. Before, type 5 read on bit by bit (types 4, 6 and 7 aligned already): the corners
    /// came out shifted (M6 draft h-8745; docs/FEHLERGEDAECHTNIS.md, No. 1123).
    /// </summary>
    [Fact]
    public void LatticeMesh_EachVertexAtAWholeByte()
    {
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(5)), ("ColorSpace", TestPdf.N("DeviceGray")), ("VerticesPerRow", PdfInteger.Of(2)),
            ("BitsPerCoordinate", PdfInteger.Of(4)), ("BitsPerComponent", PdfInteger.Of(4)), ("Decode", TestPdf.Box(0, 150, 0, 150, 0, 1)));
        // x and y in the first byte, grey 0 in the high nibble of the second
        byte[] data = [0x00, 0x00, 0xF0, 0x00, 0x0F, 0x00, 0xFF, 0x00];
        using var b = Render("/Sh sh", Stream(dict, data));
        foreach (var (x, y) in new[] { (20, 20), (130, 20), (20, 130), (130, 130), (75, 75) })
            Assert.True(At(b, x, y) is var c && c.Red < 40, $"({x}, {y}): {At(b, x, y)}");
        Assert.True(At(b, 180, 180).Red > 200);
    }

    /// <summary>
    /// A shading that cannot be read — a mesh of type 4 as a plain dictionary without data, a type 5 with one vertex per row, an unknown
    /// /ShadingType — paints nothing with „sh“, and its /Background never applies to „sh“ (8.7.4.3, Table 77). Before, the whole clip was
    /// filled with the colour of the middle: black from zeros, or the /Background colour — a broken shading could black out a page (M6 drafts
    /// g-871-874-4, h-8745; docs/FEHLERGEDAECHTNIS.md, No. 1123).
    /// </summary>
    [Theory]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(9, 0)]
    public void UnreadableShading_ShPaintsNothing(int type, int perRow)
    {
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(type)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Background", TestPdf.Box(0, 0, 0)),
            ("BitsPerCoordinate", PdfInteger.Of(8)), ("BitsPerComponent", PdfInteger.Of(8)), ("BitsPerFlag", PdfInteger.Of(8)),
            ("VerticesPerRow", PdfInteger.Of(perRow)), ("Decode", TestPdf.Box(0, 200, 0, 200, 0, 1, 0, 1, 0, 1)));
        using var b = Render("/Sh sh", Shading(dict));
        Assert.True(At(b, 100, 100).Red > 240, $"{At(b, 100, 100)}");
    }

    /// <summary>
    /// 8.7.4.5.7 (sentences 17–18): where a patch overlaps itself, the point with the larger v lies on top, at equal v the one with the larger u.
    /// A tensor patch folded back over itself along u (y rises and falls again), sheared so that the two layers meet at different v, its colour
    /// running with v from red to blue: at (105, 130) the layer with v 0.5 (purple) covers the one with v 0.2 (reddish). Before, the triangles were
    /// laid by u, the larger u came last (M6 draft h-8745; docs/FEHLERGEDAECHTNIS.md, No. 1125).
    /// </summary>
    [Fact]
    public void FoldedPatch_LargerVOnTop()
    {
        // control points p[i, j]: x = 40 + 100 j/3 + 60 i/3, y over i = 40, 200, 200, 40 (i runs along u of the standard, j along v)
        (double X, double Y) P(int i, int j) => (40 + 100.0 * j / 3 + 60.0 * i / 3, i is 1 or 2 ? 200 : 40);
        (int I, int J)[] order = [(0, 0), (0, 1), (0, 2), (0, 3), (1, 3), (2, 3), (3, 3), (3, 2), (3, 1), (3, 0), (2, 0), (1, 0), (1, 1), (1, 2), (2, 2), (2, 1)];
        var data = new List<byte> { 0 };   // flag 0
        foreach (var (i, j) in order)
        {
            var (x, y) = P(i, j);
            foreach (var v in new[] { x, y })
            {
                var raw = (ushort)Math.Round(v / 200 * 65535);
                data.Add((byte)(raw >> 8));
                data.Add((byte)raw);
            }
        }
        // c00 red, c03 blue, c33 blue, c30 red: the colour runs with j (v of the standard) only
        data.AddRange([255, 0, 0, 0, 0, 255, 0, 0, 255, 255, 0, 0]);
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(7)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerCoordinate", PdfInteger.Of(16)),
            ("BitsPerComponent", PdfInteger.Of(8)), ("BitsPerFlag", PdfInteger.Of(8)), ("Decode", TestPdf.Box(0, 200, 0, 200, 0, 1, 0, 1, 0, 1)));
        using var b = Render("/Sh sh", Stream(dict, [.. data]));
        var c = At(b, 105, 130);
        Assert.True(c.Blue > 95 && c.Red < 175, $"{c}");
    }
}
