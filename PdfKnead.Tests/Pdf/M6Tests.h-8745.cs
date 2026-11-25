using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>M6 sentence by sentence, package h-8745: ISO 32000-2, 8.7.4.5 (shading types 1 to 7).</summary>
public sealed partial class M6NormTests
{
    private static SKBitmap H8745_Render(string content, Func<TestPdf, PdfDictionary> resources)
    {
        var (doc, list) = InterpreterTests.Page(content, resources);
        using (doc) return SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
    }

    private static SKColor H8745_At(SKBitmap b, double x, double pdfY) => b.GetPixel((int)x, b.Height - 1 - (int)pdfY);

    private static bool H8745_Red(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;
    private static bool H8745_Green(SKColor c) => c.Green > 200 && c.Red < 60 && c.Blue < 60;
    private static bool H8745_Blue(SKColor c) => c.Blue > 200 && c.Red < 60 && c.Green < 60;
    private static bool H8745_White(SKColor c) => c.Red > 240 && c.Green > 240 && c.Blue > 240;
    private static bool H8745_Purple(SKColor c) => c.Red is >= 112 and <= 143 && c.Blue is >= 112 and <= 143 && c.Green < 20;

    /// <summary>(255, 0, 128): the components 2, −1 and 0.5 clipped to their range.</summary>
    private static bool H8745_Clipped(SKColor c) => c.Red >= 254 && c.Green <= 1 && c.Blue is >= 122 and <= 133;

    private static PdfArray H8745_Indexed => new([TestPdf.N("Indexed"), TestPdf.N("DeviceRGB"), PdfInteger.Of(1), new PdfString([0xFF, 0, 0, 0, 0, 0xFF])]);

    /// <summary>Resources with the shading /Sh; streams become objects of their own.</summary>
    private static Func<TestPdf, PdfDictionary> H8745_Sh(Func<TestPdf, PdfObject> shading) => pdf =>
    {
        var obj = shading(pdf);
        return TestPdf.Dict(("Shading", TestPdf.Dict(("Sh", obj is PdfStream ? TestPdf.Ref(pdf.Add(obj)) : obj))));
    };

    private static PdfDictionary H8745_Exponential(double[] c0, double[] c1, double[]? domain = null) => TestPdf.Dict(("FunctionType", PdfInteger.Of(2)),
        ("Domain", TestPdf.Box(domain ?? [0, 1])), ("C0", TestPdf.Box(c0)), ("C1", TestPdf.Box(c1)), ("N", PdfInteger.Of(1)));

    private static PdfDictionary H8745_RedToBlue() => H8745_Exponential([1, 0, 0], [0, 0, 1]);

    /// <summary>A PostScript calculator function (type 4), as an object of its own.</summary>
    private static PdfReference H8745_PostScript(TestPdf pdf, string code, double[] domain, double[] range) => TestPdf.Ref(pdf.Add(new PdfStream(
        TestPdf.Dict(("FunctionType", PdfInteger.Of(4)), ("Domain", TestPdf.Box(domain)), ("Range", TestPdf.Box(range))), Encoding.ASCII.GetBytes(code))));

    /// <summary>The sampled function red → green → blue (three stops) over t 0…1, as an object of its own.</summary>
    private static PdfReference H8745_Stops(TestPdf pdf) => TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("FunctionType", PdfInteger.Of(0)),
        ("Domain", TestPdf.Box(0, 1)), ("Range", TestPdf.Box(0, 1, 0, 1, 0, 1)), ("Size", new PdfArray([PdfInteger.Of(3)])),
        ("BitsPerSample", PdfInteger.Of(8))), [255, 0, 0, 0, 255, 0, 0, 0, 255])));

    /// <summary>A type 1 shading: <paramref name="function"/> null leaves /Function out; <paramref name="domain"/>, <paramref name="matrix"/> likewise.</summary>
    private static PdfDictionary H8745_Type1(PdfObject? function, PdfObject colorSpace, double[]? domain, double[]? matrix)
    {
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(1)), ("ColorSpace", colorSpace));
        if (function is not null) dict.Set(TestPdf.N("Function"), function);
        if (domain is not null) dict.Set(TestPdf.N("Domain"), TestPdf.Box(domain));
        if (matrix is not null) dict.Set(TestPdf.N("Matrix"), TestPdf.Box(matrix));
        return dict;
    }

    private static PdfDictionary H8745_Axial(double x0, double x1, PdfObject function, bool? extend = true, PdfObject? colorSpace = null, double[]? domain = null)
    {
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(2)), ("ColorSpace", colorSpace ?? TestPdf.N("DeviceRGB")), ("Coords", TestPdf.Box(x0, 0, x1, 0)),
            ("Function", function));
        if (extend is { } e) dict.Set(TestPdf.N("Extend"), new PdfArray([PdfBoolean.Of(e), PdfBoolean.Of(e)]));
        if (domain is not null) dict.Set(TestPdf.N("Domain"), TestPdf.Box(domain));
        return dict;
    }

    private static PdfDictionary H8745_Radial(double[] coords, PdfObject function, bool extend = false, PdfObject? colorSpace = null, double[]? domain = null)
    {
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(3)), ("ColorSpace", colorSpace ?? TestPdf.N("DeviceRGB")), ("Coords", TestPdf.Box(coords)),
            ("Function", function), ("Extend", new PdfArray([PdfBoolean.Of(extend), PdfBoolean.Of(extend)])));
        if (domain is not null) dict.Set(TestPdf.N("Domain"), TestPdf.Box(domain));
        return dict;
    }

    /// <summary>The dictionary of a mesh shading (types 4–7); flags only for 4, 6 and 7, /VerticesPerRow only for 5.</summary>
    private static PdfDictionary H8745_MeshDict(int type, int coordinate, int component, double[] decode, PdfObject? colorSpace = null, int flag = 8, int perRow = 2)
    {
        var dict = TestPdf.Dict(("ShadingType", PdfInteger.Of(type)), ("ColorSpace", colorSpace ?? TestPdf.N("DeviceRGB")),
            ("BitsPerCoordinate", PdfInteger.Of(coordinate)), ("BitsPerComponent", PdfInteger.Of(component)), ("Decode", TestPdf.Box(decode)));
        if (type == 5) dict.Set(TestPdf.N("VerticesPerRow"), PdfInteger.Of(perRow));
        else dict.Set(TestPdf.N("BitsPerFlag"), PdfInteger.Of(flag));
        return dict;
    }

    private static readonly double[] H8745_ByteRgb = [0, 255, 0, 255, 0, 1, 0, 1, 0, 1];

    /// <summary>Bits from the highest to the lowest, as a mesh is read (8.7.4.5.5).</summary>
    private sealed class H8745_Bits
    {
        private readonly List<byte> _bytes = [];
        private int _used;   // bits used in the last byte

        public H8745_Bits Put(ulong value, int bits)
        {
            for (var i = bits - 1; i >= 0; i--)
            {
                if (_used == 0) _bytes.Add(0);
                if (((value >> i) & 1) != 0) _bytes[^1] |= (byte)(0x80 >> _used);
                _used = (_used + 1) & 7;
            }
            return this;
        }

        public H8745_Bits Bytes(params int[] values)
        {
            foreach (var v in values) Put((ulong)v, 8);
            return this;
        }

        /// <summary>Pads to the next whole byte.</summary>
        public H8745_Bits Align()
        {
            _used = 0;
            return this;
        }

        public byte[] ToArray() => [.. _bytes];
    }

    /// <summary>The meshes the page's „/Sh sh“ builds (shading space = page space here).</summary>
    private static MeshFill H8745_MeshOf(Func<TestPdf, PdfDictionary> resources)
    {
        var (doc, list) = InterpreterTests.Page("/Sh sh", resources);
        using (doc) return Assert.Single(list.Shadings.OfType<MeshFill>());
    }

    private static List<(double X, double Y)> H8745_Vertices(MeshFill mesh)
    {
        var result = new List<(double, double)>();
        for (var i = 0; i + 1 < mesh.Points.Length; i += 2) result.Add((mesh.Points[i], mesh.Points[i + 1]));
        return result;
    }

    private static void H8745_HasVertex(MeshFill mesh, (double X, double Y) expected, double tolerance = 0.05) =>
        Assert.True(H8745_Vertices(mesh).Any(p => Math.Abs(p.X - expected.X) < tolerance && Math.Abs(p.Y - expected.Y) < tolerance),
            $"kein Netzpunkt bei ({expected.X:0.###}, {expected.Y:0.###})");

    private static double H8745_Bernstein(int i, double t) => i switch
    {
        0 => (1 - t) * (1 - t) * (1 - t),
        1 => 3 * t * (1 - t) * (1 - t),
        2 => 3 * t * t * (1 - t),
        _ => t * t * t,
    };

    /// <summary>The tensor surface S(u, v) = Σ p[i, j] · B_i(u) · B_j(v), i the column, j the row (8.7.4.5.8).</summary>
    private static (double X, double Y) H8745_Surface((double X, double Y)[,] p, double u, double v)
    {
        double x = 0, y = 0;
        for (var i = 0; i < 4; i++)
        for (var j = 0; j < 4; j++)
        {
            var w = H8745_Bernstein(i, u) * H8745_Bernstein(j, v);
            x += w * p[i, j].X;
            y += w * p[i, j].Y;
        }
        return (x, y);
    }

    /// <summary>The 16 points of a tensor patch in the order of the stream (8.7.4.5.8, Figure 47) as p[i, j].</summary>
    private static (double X, double Y)[,] H8745_Grid((double X, double Y)[] stream)
    {
        (int I, int J)[] order = [(0, 0), (0, 1), (0, 2), (0, 3), (1, 3), (2, 3), (3, 3), (3, 2), (3, 1), (3, 0), (2, 0), (1, 0), (1, 1), (1, 2), (2, 2), (2, 1)];
        var p = new (double X, double Y)[4, 4];
        for (var k = 0; k < order.Length && k < stream.Length; k++) p[order[k].I, order[k].J] = stream[k];
        return p;
    }

    /// <summary>A patch mesh (type 6 or 7) of one patch with flag 0: the points (raw values of <paramref name="bits"/> bits), then the corner colours.</summary>
    private static byte[] H8745_PatchData(int bits, IEnumerable<(double X, double Y)> points, double scale, int componentBits, params int[] components)
    {
        var data = new H8745_Bits().Bytes(0);
        var max = bits == 32 ? uint.MaxValue : (1UL << bits) - 1;
        foreach (var (x, y) in points)
        {
            data.Put((ulong)Math.Round(x / scale * max), bits);
            data.Put((ulong)Math.Round(y / scale * max), bits);
        }
        foreach (var c in components) data.Put((ulong)c, componentBits);
        return data.Align().ToArray();
    }

    private static (double X, double Y)[] H8745_Square(double from, double to)
    {
        double a = from, b = from + (to - from) / 3, c = from + 2 * (to - from) / 3, d = to;
        return [(a, a), (a, b), (a, c), (a, d), (b, d), (c, d), (d, d), (d, c), (d, b), (d, a), (c, a), (b, a)];
    }

    // ---------------------------------------------------------------- 8.7.4.5.2: type 1

    /// <summary>
    /// 8.7.4.5.2 sentence 5 (read leniently, S-A A182): type 1 must not be used with Indexed; one in [/Indexed /DeviceRGB 1 &lt;FF0000 0000FF&gt;] with
    /// { pop } over the page is read leniently through the palette — red on the left, blue on the right, no crash.
    /// </summary>
    [Fact]
    public void FunctionBased_Indexed_ReadThroughThePalette()
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(pdf => H8745_Type1(H8745_PostScript(pdf, "{ pop }", [0, 1, 0, 1], [0, 1]), H8745_Indexed,
            [0, 1, 0, 1], [200, 0, 0, 200, 0, 0])));
        Assert.True(H8745_Red(H8745_At(b, 20, 100)), $"{H8745_At(b, 20, 100)}");
        Assert.True(H8745_Blue(H8745_At(b, 180, 100)), $"{H8745_At(b, 180, 100)}");
    }

    /// <summary>8.7.4.5.2 sentences 6–9: without /Domain the function runs over [0 1 0 1] — mapped onto the page, grey x: dark at 10, light at 190, the page filled.</summary>
    [Fact]
    public void FunctionBased_WithoutDomain_UsesTheUnitSquare()
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(pdf => H8745_Type1(H8745_PostScript(pdf, "{ pop }", [0, 1, 0, 1], [0, 1]), TestPdf.N("DeviceGray"),
            null, [200, 0, 0, 200, 0, 0])));
        Assert.True(H8745_At(b, 10, 100).Red < 30, $"{H8745_At(b, 10, 100)}");
        Assert.True(H8745_At(b, 190, 100).Red > 220, $"{H8745_At(b, 190, 100)}");
        Assert.InRange(H8745_At(b, 100, 5).Red, 110, 145);
        Assert.InRange(H8745_At(b, 100, 195).Red, 110, 145);
    }

    /// <summary>8.7.4.5.2 sentences 10–11 and 13: without /Matrix the domain lies in the shading space itself — /Domain [0 200 0 200], grey x/200: a run over the whole page.</summary>
    [Fact]
    public void FunctionBased_WithoutMatrix_DomainInShadingSpace()
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(pdf => H8745_Type1(H8745_PostScript(pdf, "{ pop 200 div }", [0, 200, 0, 200], [0, 1]),
            TestPdf.N("DeviceGray"), [0, 200, 0, 200], null)));
        Assert.True(H8745_At(b, 10, 100).Red < 30, $"{H8745_At(b, 10, 100)}");
        Assert.InRange(H8745_At(b, 100, 100).Red, 110, 145);
        Assert.True(H8745_At(b, 190, 100).Red > 225, $"{H8745_At(b, 190, 100)}");
    }

    /// <summary>8.7.4.5.2 sentences 14–16: /Function is required and takes two inputs — without one, or with a function of one input, nothing is painted.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FunctionBased_WithoutATwoInputFunction_PaintsNothing(bool oneInput)
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => H8745_Type1(oneInput ? H8745_Exponential([0], [1]) : null, TestPdf.N("DeviceGray"),
            [0, 1, 0, 1], [200, 0, 0, 200, 0, 0])));
        Assert.True(H8745_White(H8745_At(b, 100, 100)), $"{H8745_At(b, 100, 100)}");
        Assert.True(H8745_White(H8745_At(b, 20, 20)), $"{H8745_At(b, 20, 20)}");
    }

    /// <summary>
    /// 8.7.4.5.2 sentence 17: a /Domain [0 2 0 1] beyond the function's own [0 1 0 1] — the function takes its input at the edge of its domain:
    /// 1 − x is black over the whole right half, a run over the left half.
    /// </summary>
    [Fact]
    public void FunctionBased_DomainBeyondTheFunction_TakesItsEdgeValue()
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(pdf => H8745_Type1(H8745_PostScript(pdf, "{ pop 1 exch sub }", [0, 1, 0, 1], [0, 1]),
            TestPdf.N("DeviceGray"), [0, 2, 0, 1], [100, 0, 0, 200, 0, 0])));
        Assert.True(H8745_At(b, 150, 100).Red < 20, $"{H8745_At(b, 150, 100)}");
        Assert.True(H8745_At(b, 190, 100).Red < 20, $"{H8745_At(b, 190, 100)}");
        Assert.InRange(H8745_At(b, 50, 100).Red, 110, 145);
        Assert.True(H8745_At(b, 5, 100).Red > 225, $"{H8745_At(b, 5, 100)}");
    }

    /// <summary>8.7.4.5.2 sentence 18: values beyond the range of a component are set to the nearest valid one — { pop pop 2 -1 0.5 } in DeviceRGB gives (255, 0, 128).</summary>
    [Fact]
    public void FunctionBased_ValuesBeyondTheComponentRange_AreClipped()
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(pdf => H8745_Type1(H8745_PostScript(pdf, "{ pop pop 2 -1 0.5 }", [0, 1, 0, 1], [-5, 5, -5, 5, -5, 5]),
            TestPdf.N("DeviceRGB"), [0, 1, 0, 1], [200, 0, 0, 200, 0, 0])));
        Assert.True(H8745_Clipped(H8745_At(b, 100, 100)), $"{H8745_At(b, 100, 100)}");
    }

    /// <summary>
    /// 8.7.4.5.2 sentences 19–21: /Matrix maps the domain rectangle onto a parallelogram — [100 0 100 200 0 0] shears it: at y 100 the run goes
    /// from x 50 (dark) to x 150 (light), white beside it; at y 190 it starts at x 95.
    /// </summary>
    [Fact]
    public void FunctionBased_MatrixMapsOntoAParallelogram()
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(pdf => H8745_Type1(H8745_PostScript(pdf, "{ pop }", [0, 1, 0, 1], [0, 1]), TestPdf.N("DeviceGray"),
            [0, 1, 0, 1], [100, 0, 100, 200, 0, 0])));
        Assert.True(H8745_At(b, 55, 100).Red < 40, $"{H8745_At(b, 55, 100)}");
        Assert.True(H8745_At(b, 145, 100).Red > 215, $"{H8745_At(b, 145, 100)}");
        Assert.True(H8745_At(b, 100, 190).Red < 40, $"{H8745_At(b, 100, 190)}");
        foreach (var (x, y) in new[] { (40, 100), (160, 100), (10, 190), (190, 10) })
            Assert.True(H8745_White(H8745_At(b, x, y)), $"({x}, {y}): {H8745_At(b, x, y)}");
    }

    /// <summary>8.7.4.5.2 sentence 22: as a pattern with /Background [0 1 0], the part of the fill beside the mapped domain (right half) is green.</summary>
    [Fact]
    public void FunctionBased_AsPattern_BackgroundBesideTheDomain()
    {
        using var b = H8745_Render("/Pattern cs /P scn 0 0 200 200 re f", pdf =>
        {
            var shading = H8745_Type1(H8745_PostScript(pdf, "{ pop dup dup }", [0, 1, 0, 1], [0, 1, 0, 1, 0, 1]), TestPdf.N("DeviceRGB"),
                [0, 1, 0, 1], [100, 0, 0, 200, 0, 0]);
            shading.Set(TestPdf.N("Background"), TestPdf.Box(0, 1, 0));
            return TestPdf.Dict(("Pattern", TestPdf.Dict(("P", TestPdf.Ref(pdf.Add(TestPdf.Dict(("PatternType", PdfInteger.Of(2)), ("Shading", shading))))))));
        });
        var green = H8745_At(b, 150, 100);
        Assert.True(green.Green > 240 && green.Red < 15 && green.Blue < 15, $"{green}");
        var grey = H8745_At(b, 50, 100);
        Assert.True(grey.Red is > 100 and < 155 && Math.Abs(grey.Red - grey.Green) < 5, $"{grey}");
    }

    /// <summary>8.7.4.5.2 sentence 23: a function that fails where it is evaluated ({ pop pop pop pop } with two inputs) — no crash, the page is drawn.</summary>
    [Fact]
    public void FunctionBased_FailingFunction_DoesNotCrash()
    {
        using var b = H8745_Render("/Sh sh 1 0 0 rg 0 0 10 10 re f", H8745_Sh(pdf => H8745_Type1(H8745_PostScript(pdf, "{ pop pop pop pop }", [0, 1, 0, 1], [0, 1]),
            TestPdf.N("DeviceGray"), [0, 1, 0, 1], [200, 0, 0, 200, 0, 0])));
        Assert.Equal(200, b.Width);
        Assert.True(H8745_Red(H8745_At(b, 5, 5)), $"{H8745_At(b, 5, 5)}");   // what follows is drawn
    }

    // ---------------------------------------------------------------- 8.7.4.5.3: type 2

    /// <summary>8.7.4.5.3 sentences 10–14: /Domain [0.5 1] — t runs from 0.5 at the start of the axis: red → blue begins with (128, 0, 128), ends blue.</summary>
    [Fact]
    public void Axial_DomainSetsTheParameterRange()
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => H8745_Axial(0, 200, H8745_RedToBlue(), domain: [0.5, 1])));
        Assert.True(H8745_Purple(H8745_At(b, 1, 100)), $"{H8745_At(b, 1, 100)}");
        Assert.True(H8745_Blue(H8745_At(b, 197, 100)), $"{H8745_At(b, 197, 100)}");
    }

    /// <summary>
    /// 8.7.4.5.3 sentences 15–19: an array of three functions 1 → 1 gives the same run as one function 1 → 3; and values beyond the range of a
    /// component are clipped — C0 = C1 = [2 −1 0.5] without /Range gives (255, 0, 128).
    /// </summary>
    [Fact]
    public void Axial_ThreeFunctionsOrUnclippedValues()
    {
        var three = new PdfArray([H8745_Exponential([1], [0]), H8745_Exponential([0], [0]), H8745_Exponential([0], [1])]);
        using (var split = H8745_Render("/Sh sh", H8745_Sh(_ => H8745_Axial(0, 200, three))))
        using (var one = H8745_Render("/Sh sh", H8745_Sh(_ => H8745_Axial(0, 200, H8745_RedToBlue()))))
            foreach (var x in new[] { 3, 50, 100, 150, 196 })
            {
                SKColor s = H8745_At(split, x, 100), o = H8745_At(one, x, 100);
                Assert.True(Math.Abs(s.Red - o.Red) <= 2 && Math.Abs(s.Green - o.Green) <= 2 && Math.Abs(s.Blue - o.Blue) <= 2, $"{x}: {s} statt {o}");
            }
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => H8745_Axial(0, 200, H8745_Exponential([2, -1, 0.5], [2, -1, 0.5]))));
        Assert.True(H8745_Clipped(H8745_At(b, 100, 100)), $"{H8745_At(b, 100, 100)}");
    }

    /// <summary>8.7.4.5.3 sentences 20–22: without /Extend ([false false]) nothing is painted before the start and beyond the end of the axis.</summary>
    [Fact]
    public void Axial_WithoutExtend_WhiteBeyondBothEnds()
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => H8745_Axial(50, 150, H8745_RedToBlue(), extend: null)));
        Assert.True(H8745_White(H8745_At(b, 20, 100)), $"{H8745_At(b, 20, 100)}");
        Assert.True(H8745_White(H8745_At(b, 180, 100)), $"{H8745_At(b, 180, 100)}");
        Assert.False(H8745_White(H8745_At(b, 100, 100)));
    }

    // ---------------------------------------------------------------- 8.7.4.5.4: type 3

    /// <summary>
    /// 8.7.4.5.4 sentence 4 (read leniently, S-A A182): type 3 must not be used with Indexed; one in [/Indexed /DeviceRGB 1 &lt;FF0000 0000FF&gt;] with a
    /// function 0 → 1 is read through the palette — red in the middle, blue at the edge, no crash.
    /// </summary>
    [Fact]
    public void Radial_Indexed_ReadThroughThePalette()
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => H8745_Radial([100, 100, 0, 100, 100, 80], H8745_Exponential([0], [1]), colorSpace: H8745_Indexed)));
        Assert.True(H8745_Red(H8745_At(b, 100, 100)), $"{H8745_At(b, 100, 100)}");
        Assert.True(H8745_Blue(H8745_At(b, 176, 100)), $"{H8745_At(b, 176, 100)}");
    }

    /// <summary>8.7.4.5.4 sentences 5–9 (read leniently, S-A A184, as two reference readers): a negative radius r0 −10 is taken as 0 — the same pixels as with r0 0, red in the middle.</summary>
    [Fact]
    public void Radial_NegativeRadius_AsZero()
    {
        using var negative = H8745_Render("/Sh sh", H8745_Sh(_ => H8745_Radial([100, 100, -10, 100, 100, 80], H8745_RedToBlue())));
        using var zero = H8745_Render("/Sh sh", H8745_Sh(_ => H8745_Radial([100, 100, 0, 100, 100, 80], H8745_RedToBlue())));
        Assert.True(H8745_Red(H8745_At(negative, 100, 100)), $"{H8745_At(negative, 100, 100)}");
        for (var x = 0; x < 200; x += 3)
        {
            SKColor n = H8745_At(negative, x, 100), z = H8745_At(zero, x, 100);
            Assert.True(Math.Abs(n.Red - z.Red) <= 3 && Math.Abs(n.Green - z.Green) <= 3 && Math.Abs(n.Blue - z.Blue) <= 3, $"{x}: {n} statt {z}");
        }
    }

    /// <summary>8.7.4.5.4 sentences 10–14: /Domain [0.5 1] — the start circle (radius 0, the middle) takes t 0.5: (128, 0, 128).</summary>
    [Fact]
    public void Radial_DomainSetsTheParameterRange()
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => H8745_Radial([100, 100, 0, 100, 100, 80], H8745_RedToBlue(), domain: [0.5, 1])));
        Assert.True(H8745_Purple(H8745_At(b, 100, 100)), $"{H8745_At(b, 100, 100)}");
        Assert.True(H8745_Blue(H8745_At(b, 177, 100)), $"{H8745_At(b, 177, 100)}");
    }

    /// <summary>
    /// 8.7.4.5.4 sentences 15–20: the function takes one input — one with two inputs paints nothing; values beyond the range of a component are
    /// clipped — C0 = C1 = [2 −1 0.5] gives (255, 0, 128) up to the edge.
    /// </summary>
    [Fact]
    public void Radial_TwoInputFunctionOrUnclippedValues()
    {
        using (var b = H8745_Render("/Sh sh", H8745_Sh(_ => H8745_Radial([100, 100, 0, 100, 100, 80], H8745_Exponential([1, 0, 0], [0, 0, 1], [0, 1, 0, 1])))))
            Assert.True(H8745_White(H8745_At(b, 100, 100)), $"{H8745_At(b, 100, 100)}");
        using (var b = H8745_Render("/Sh sh", H8745_Sh(_ => H8745_Radial([100, 100, 0, 100, 100, 80], H8745_Exponential([2, -1, 0.5], [2, -1, 0.5])))))
        {
            Assert.True(H8745_Clipped(H8745_At(b, 100, 100)), $"{H8745_At(b, 100, 100)}");
            Assert.True(H8745_Clipped(H8745_At(b, 176, 100)), $"{H8745_At(b, 176, 100)}");
        }
    }

    /// <summary>
    /// 8.7.4.5.4 sentences 37–41: concentric circles r0 10 and r1 50 with /Extend both ways — inside the start circle the colour at t0 (red), outside
    /// the end circle the colour at t1 (blue), half way between them both.
    /// </summary>
    [Fact]
    public void Radial_Concentric_ExtendedBothWays()
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => H8745_Radial([100, 100, 10, 100, 100, 50], H8745_RedToBlue(), extend: true)));
        Assert.True(H8745_Red(H8745_At(b, 100, 100)), $"{H8745_At(b, 100, 100)}");
        Assert.True(H8745_Red(H8745_At(b, 100, 106)), $"{H8745_At(b, 100, 106)}");
        Assert.True(H8745_Blue(H8745_At(b, 100, 170)), $"{H8745_At(b, 100, 170)}");
        Assert.True(H8745_Blue(H8745_At(b, 30, 100)), $"{H8745_At(b, 30, 100)}");
        Assert.True(H8745_Blue(H8745_At(b, 5, 5)), $"{H8745_At(b, 5, 5)}");
        var half = H8745_At(b, 100, 130);
        Assert.True(half.Red is > 95 and < 160 && half.Blue is > 95 and < 160, $"{half}");
    }

    // ---------------------------------------------------------------- 8.7.4.5.5: type 4

    /// <summary>
    /// 8.7.4.5.5 sentences 5–9 (writer) and 8.7.4.4 sentence 32 (writer): the mesh `MeshCut.Write` writes when a mesh is cut at a redaction bar is
    /// type 4 in /DeviceRGB with 32 bits per coordinate (8 per component and flag), and no function.
    /// </summary>
    [Fact]
    public void MeshCut_WritesType4With32BitCoordinatesInDeviceRgb()
    {
        (double X, double Y, byte R, byte G, byte B)[] corners = [(60, 680, 255, 0, 0), (260, 680, 0, 0, 255), (60, 730, 255, 0, 0)];
        var data = new StringBuilder();
        foreach (var (x, y, r, g, bl) in corners)
        {
            var (cx, cy) = ((int)Math.Round(x / 595 * 65535), (int)Math.Round(y / 842 * 65535));
            data.Append((char)0).Append((char)(cx >> 8)).Append((char)(cx & 0xFF)).Append((char)(cy >> 8)).Append((char)(cy & 0xFF))
                .Append((char)r).Append((char)g).Append((char)bl);
        }
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Shading<</Sh0 5 0 R>>>>>>",
            [4] = ("", "q /Sh0 sh Q"),
            [5] = ("/ShadingType 4/ColorSpace/DeviceRGB/BitsPerCoordinate 16/BitsPerComponent 8/BitsPerFlag 8/Decode[0 595 0 842 0 1 0 1 0 1]", data.ToString()),
        });
        using var doc = PdfDocument.Open(file);
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(96, 695, 180, 714))));
        var shadings = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Shading"))!;
        var cut = Assert.IsType<PdfStream>(shadings.Get(shadings.Entries.First().Key)).Dictionary;
        Assert.Equal(4, cut.GetInteger(PdfName.Get("ShadingType")));
        Assert.Equal("DeviceRGB", cut.GetName(PdfName.Get("ColorSpace"))?.Value);
        Assert.Equal(32, cut.GetInteger(PdfName.Get("BitsPerCoordinate")));
        Assert.Equal(8, cut.GetInteger(PdfName.Get("BitsPerComponent")));
        Assert.Equal(8, cut.GetInteger(PdfName.Get("BitsPerFlag")));
        Assert.False(cut.ContainsKey(PdfName.Get("Function")));
    }

    /// <summary>8.7.4.5.5 sentences 5–9 (reader; read leniently, S-A A184): 5 bits per coordinate are not among the allowed values, but are read — the triangle lies where 5-bit values put it.</summary>
    [Fact]
    public void FreeForm_FiveBitCoordinates_ReadLeniently()
    {
        var data = new H8745_Bits();
        foreach (var (x, y) in new[] { (0, 0), (31, 0), (0, 31) }) data.Bytes(0).Put((ulong)x, 5).Put((ulong)y, 5).Bytes(255, 0, 0).Align();
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(4, 5, 8, [0, 200, 0, 200, 0, 1, 0, 1, 0, 1]), data.ToArray())));
        Assert.True(H8745_Red(H8745_At(b, 20, 20)), $"{H8745_At(b, 20, 20)}");
        Assert.True(H8745_Red(H8745_At(b, 5, 180)), $"{H8745_At(b, 5, 180)}");
        Assert.True(H8745_White(H8745_At(b, 150, 150)), $"{H8745_At(b, 150, 150)}");
    }

    /// <summary>8.7.4.5.5 sentences 10–12: 17 bits per component are refused (nothing painted); with 4 bits the value 15 is the full component (pure red).</summary>
    [Fact]
    public void FreeForm_BitsPerComponent_SeventeenRefused_FourBitsFull()
    {
        foreach (var bits in new[] { 17, 4 })
        {
            var data = new H8745_Bits();
            foreach (var (x, y) in new[] { (0, 0), (200, 0), (0, 200) })
                data.Bytes(0, x, y).Put((1UL << bits) - 1, bits).Put(0, bits).Put(0, bits).Align();
            using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(4, 8, bits, H8745_ByteRgb), data.ToArray())));
            var c = H8745_At(b, 20, 20);
            if (bits == 17) Assert.True(H8745_White(c), $"17 Bit: {c}");
            else Assert.True(c.Red >= 254 && c.Green <= 1 && c.Blue <= 1, $"4 Bit: {c}");
        }
    }

    /// <summary>
    /// 8.7.4.5.5 sentences 39–42 (flag 2) and sentences 13–17 (flag 3, tolerance of today): after the triangle A B C, a vertex D with flag 2 forms the
    /// triangle A C D — painted at (60, 180), nothing at (180, 60), which only B C D would cover; the invalid flag 3 is read as 2.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void FreeForm_FlagTwoOrThree_TriangleACD(int flag)
    {
        var data = new H8745_Bits().Bytes(0, 0, 0, 255, 0, 0).Bytes(0, 200, 0, 255, 0, 0).Bytes(0, 0, 200, 255, 0, 0).Bytes(flag, 200, 200, 255, 0, 0);
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(4, 8, 8, H8745_ByteRgb), data.ToArray())));
        Assert.True(H8745_Red(H8745_At(b, 60, 180)), $"{H8745_At(b, 60, 180)}");
        Assert.True(H8745_White(H8745_At(b, 180, 60)), $"{H8745_At(b, 180, 60)}");
        Assert.True(H8745_Red(H8745_At(b, 20, 20)), $"{H8745_At(b, 20, 20)}");
    }

    /// <summary>8.7.4.5.5 sentences 13–17 (/BitsPerFlag 0, read leniently, S-A A184): no flag bits — every flag counts as 0, every three vertices form a triangle of their own.</summary>
    [Fact]
    public void FreeForm_NoFlagBits_EveryThreeVerticesATriangle()
    {
        var data = new H8745_Bits();
        foreach (var (x, y) in new[] { (0, 0), (200, 0), (0, 200), (200, 200), (100, 200), (200, 100) }) data.Bytes(x, y, 255, 0, 0);
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(4, 8, 8, H8745_ByteRgb, flag: 0), data.ToArray())));
        Assert.True(H8745_Red(H8745_At(b, 20, 20)), $"{H8745_At(b, 20, 20)}");
        Assert.True(H8745_Red(H8745_At(b, 190, 190)), $"{H8745_At(b, 190, 190)}");
        Assert.True(H8745_White(H8745_At(b, 130, 130)), $"{H8745_At(b, 130, 130)}");
    }

    /// <summary>
    /// 8.7.4.5.5 sentences 22–29: with a function each vertex carries t, mapped by /Decode onto its interval — t over [0.2 0.8], grey = t, raw values
    /// 0 and 255: grey 51 and 204.
    /// </summary>
    [Fact]
    public void FreeForm_FunctionParameterDecodedToItsInterval()
    {
        var dict = H8745_MeshDict(4, 8, 8, [0, 255, 0, 255, 0.2, 0.8], TestPdf.N("DeviceGray"));
        dict.Set(TestPdf.N("Function"), H8745_Exponential([0], [1]));
        var data = new H8745_Bits().Bytes(0, 0, 0, 0).Bytes(0, 200, 0, 0).Bytes(0, 0, 200, 255).ToArray();
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(dict, data)));
        Assert.InRange(H8745_At(b, 100, 1).Red, 44, 62);
        Assert.InRange(H8745_At(b, 1, 197).Red, 192, 210);
    }

    /// <summary>
    /// 8.7.4.5.5 sentences 22–29, 8.7.4.5.6 sentences 23–31 and 8.7.4.5.7 sentences 41–50 (read leniently, S-A A182): a mesh with a function must not be
    /// Indexed; one in [/Indexed /DeviceRGB 1 &lt;FF0000 0000FF&gt;] with t from 0 (bottom) to 1 (top) is read through the palette — red at the bottom,
    /// blue at the top, for types 4, 5 and 6.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Mesh_IndexedWithFunction_ReadThroughThePalette(int type)
    {
        var dict = H8745_MeshDict(type, 8, 8, [0, 255, 0, 255, 0, 1], H8745_Indexed);
        dict.Set(TestPdf.N("Function"), H8745_Exponential([0], [1]));
        var data = type switch
        {
            4 => new H8745_Bits().Bytes(0, 0, 0, 0).Bytes(0, 200, 0, 0).Bytes(0, 0, 200, 255).ToArray(),
            5 => new H8745_Bits().Bytes(0, 0, 0).Bytes(200, 0, 0).Bytes(0, 200, 255).Bytes(200, 200, 255).ToArray(),
            _ => H8745_PatchData(8, H8745_Square(0, 200), 255, 8, 0, 255, 255, 0),
        };
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(dict, data)));
        Assert.True(H8745_Red(H8745_At(b, 20, 20)), $"{H8745_At(b, 20, 20)}");
        Assert.True(H8745_Blue(H8745_At(b, 2, 190)), $"{H8745_At(b, 2, 190)}");
    }

    /// <summary>
    /// 8.7.4.5.5 sentences 34–38: a vertex with flag 0 begins a triangle of it and the next two, whose flags do not count — flags 0, 2, 1 give exactly
    /// one triangle of these three vertices.
    /// </summary>
    [Fact]
    public void FreeForm_NewTriangle_IgnoresTheFlagsOfItsSecondAndThirdVertex()
    {
        var data = new H8745_Bits().Bytes(0, 0, 0, 255, 0, 0).Bytes(2, 200, 0, 255, 0, 0).Bytes(1, 0, 200, 255, 0, 0).ToArray();
        var mesh = H8745_MeshOf(H8745_Sh(_ => new PdfStream(H8745_MeshDict(4, 8, 8, H8745_ByteRgb), data)));
        Assert.Equal(6, mesh.Points.Length);
        Assert.Equal(new (double, double)[] { (0, 0), (200, 0), (0, 200) }, H8745_Vertices(mesh));
    }

    /// <summary>8.7.4.5.5 sentence 55 (reader lenient): a triangle and two vertices more — the whole triangle is drawn, nothing of the rest.</summary>
    [Fact]
    public void FreeForm_SurplusVertices_OnlyWholeTrianglesDrawn()
    {
        var data = new H8745_Bits().Bytes(0, 0, 0, 255, 0, 0).Bytes(0, 100, 0, 255, 0, 0).Bytes(0, 0, 100, 255, 0, 0)
            .Bytes(0, 200, 200, 255, 0, 0).Bytes(0, 150, 200, 255, 0, 0).ToArray();
        var mesh = H8745_MeshOf(H8745_Sh(_ => new PdfStream(H8745_MeshDict(4, 8, 8, H8745_ByteRgb), data)));
        Assert.Equal(6, mesh.Points.Length);
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(4, 8, 8, H8745_ByteRgb), data)));
        Assert.True(H8745_Red(H8745_At(b, 20, 20)), $"{H8745_At(b, 20, 20)}");
        Assert.True(H8745_White(H8745_At(b, 185, 195)), $"{H8745_At(b, 185, 195)}");
    }

    /// <summary>
    /// 8.7.4.5.5 sentences 56–58: the values of a vertex run from the high bits to the low ones and every vertex begins at a whole byte — 2-bit flags,
    /// 4-bit coordinates and 4-bit RGB (22 bits, padded to 24): the triangle (0, 0), (150, 0), (0, 150) in red where it belongs.
    /// </summary>
    [Fact]
    public void FreeForm_EachVertexPaddedToAWholeByte()
    {
        var data = new H8745_Bits();
        foreach (var (x, y) in new[] { (0, 0), (15, 0), (0, 15) }) data.Put(0, 2).Put((ulong)x, 4).Put((ulong)y, 4).Put(15, 4).Put(0, 4).Put(0, 4).Align();
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(4, 4, 4, [0, 150, 0, 150, 0, 1, 0, 1, 0, 1], flag: 2), data.ToArray())));
        foreach (var (x, y) in new[] { (20, 20), (5, 140), (140, 5) }) Assert.True(H8745_Red(H8745_At(b, x, y)), $"({x}, {y}): {H8745_At(b, x, y)}");
        Assert.True(H8745_White(H8745_At(b, 100, 100)), $"{H8745_At(b, 100, 100)}");
    }

    // ---------------------------------------------------------------- 8.7.4.5.6: type 5

    /// <summary>8.7.4.5.6 sentences 7–10: 16 bits per coordinate — the square of two rows (20…180) in red where it belongs, white beside it.</summary>
    [Fact]
    public void Lattice_SixteenBitCoordinates()
    {
        var data = new H8745_Bits();
        foreach (var (x, y) in new[] { (20, 20), (180, 20), (20, 180), (180, 180) })
            data.Put((ulong)Math.Round(x / 200.0 * 65535), 16).Put((ulong)Math.Round(y / 200.0 * 65535), 16).Bytes(255, 0, 0);
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(5, 16, 8, [0, 200, 0, 200, 0, 1, 0, 1, 0, 1]), data.ToArray())));
        foreach (var (x, y) in new[] { (25, 25), (175, 175), (100, 100), (175, 25) }) Assert.True(H8745_Red(H8745_At(b, x, y)), $"({x}, {y}): {H8745_At(b, x, y)}");
        foreach (var (x, y) in new[] { (10, 10), (190, 100), (100, 190) }) Assert.True(H8745_White(H8745_At(b, x, y)), $"({x}, {y}): {H8745_At(b, x, y)}");
    }

    /// <summary>8.7.4.5.6 sentences 11–13: 16 bits per component — the bottom row red, the top row blue, half way both.</summary>
    [Fact]
    public void Lattice_SixteenBitComponents()
    {
        var data = new H8745_Bits();
        foreach (var (x, y, top) in new[] { (20, 20, false), (180, 20, false), (20, 180, true), (180, 180, true) })
            data.Bytes(x, y).Put(top ? 0UL : 65535, 16).Put(0, 16).Put(top ? 65535UL : 0, 16);
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(5, 8, 16, H8745_ByteRgb), data.ToArray())));
        Assert.True(H8745_Red(H8745_At(b, 100, 23)), $"{H8745_At(b, 100, 23)}");
        Assert.True(H8745_Blue(H8745_At(b, 100, 177)), $"{H8745_At(b, 100, 177)}");
        Assert.True(H8745_Purple(H8745_At(b, 100, 100)), $"{H8745_At(b, 100, 100)}");
    }

    /// <summary>
    /// 8.7.4.5.6 sentences 34–38: three rows of three vertices give eight triangles, (V[i, j], V[i, j+1], V[i+1, j]) and (V[i, j+1], V[i+1, j],
    /// V[i+1, j+1]) for i, j in 0…1 — exactly these vertex triples.
    /// </summary>
    [Fact]
    public void Lattice_ThreeRowsOfThree_EightTrianglesOfTheseVertices()
    {
        var data = new H8745_Bits();
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++) data.Bytes(20 + 80 * j, 20 + 80 * i, 255, 0, 0);
        var mesh = H8745_MeshOf(H8745_Sh(_ => new PdfStream(H8745_MeshDict(5, 8, 8, H8745_ByteRgb, perRow: 3), data.ToArray())));
        static string Key(IEnumerable<(double X, double Y)> corners) => string.Join(" ", corners.Select(c => $"{c.X:0}/{c.Y:0}").Order(StringComparer.Ordinal));
        static (double, double) V(int i, int j) => (20 + 80 * j, 20 + 80 * i);
        var expected = new List<string>();
        for (var i = 0; i < 2; i++)
        for (var j = 0; j < 2; j++)
        {
            expected.Add(Key([V(i, j), V(i, j + 1), V(i + 1, j)]));
            expected.Add(Key([V(i, j + 1), V(i + 1, j), V(i + 1, j + 1)]));
        }
        var vertices = H8745_Vertices(mesh);
        var actual = Enumerable.Range(0, vertices.Count / 3).Select(t => Key(vertices.Skip(3 * t).Take(3))).ToList();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- 8.7.4.5.7: type 6

    /// <summary>8.7.4.5.7 sentences 1–3: a Coons mesh needs at least one complete patch — a patch cut off after five points paints nothing.</summary>
    [Fact]
    public void Coons_WithoutACompletePatch_PaintsNothing()
    {
        var data = new H8745_Bits().Bytes(0, 20, 20, 20, 73, 20, 127, 20, 180, 73, 180).ToArray();
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(6, 8, 8, H8745_ByteRgb), data)));
        foreach (var (x, y) in new[] { (100, 100), (25, 25), (25, 175) }) Assert.True(H8745_White(H8745_At(b, x, y)), $"({x}, {y}): {H8745_At(b, x, y)}");
    }

    /// <summary>8.7.4.5.7 sentences 1–3: a side may degenerate — its four points the same: the patch becomes the triangle (100, 180), (180, 20), (20, 20).</summary>
    [Fact]
    public void Coons_CollapsedSide_PaintsATriangle()
    {
        (double, double)[] points = [(100, 180), (100, 180), (100, 180), (100, 180), (127, 127), (153, 73), (180, 20), (127, 20), (73, 20), (20, 20), (47, 73), (73, 127)];
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(6, 8, 8, H8745_ByteRgb),
            H8745_PatchData(8, points, 255, 8, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0))));
        foreach (var (x, y) in new[] { (100, 60), (100, 160), (40, 30), (160, 30) }) Assert.True(H8745_Red(H8745_At(b, x, y)), $"({x}, {y}): {H8745_At(b, x, y)}");
        foreach (var (x, y) in new[] { (30, 150), (170, 150), (100, 195) }) Assert.True(H8745_White(H8745_At(b, x, y)), $"({x}, {y}): {H8745_At(b, x, y)}");
    }

    /// <summary>
    /// 8.7.4.5.7 sentences 4–13: the Coons surface S = SC + SD − SB — for a patch with curved sides the mesh point at u = v = 0.5 is the mean of the
    /// midpoints of the four sides less the quarter sum of the corners (computed here from the formulas of the standard).
    /// </summary>
    [Fact]
    public void Coons_CentreFollowsTheCoonsFormula()
    {
        (double X, double Y)[] x = [(20, 20), (10, 70), (30, 130), (20, 180), (70, 190), (130, 170), (180, 180), (190, 130), (170, 70), (180, 20), (130, 30), (70, 10)];
        var mesh = H8745_MeshOf(H8745_Sh(_ => new PdfStream(H8745_MeshDict(6, 8, 8, H8745_ByteRgb), H8745_PatchData(8, x, 255, 8, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0))));
        static (double X, double Y) Mid((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d) =>
            ((a.X + 3 * b.X + 3 * c.X + d.X) / 8, (a.Y + 3 * b.Y + 3 * c.Y + d.Y) / 8);
        var d1 = Mid(x[0], x[1], x[2], x[3]);    // left side, p00 → p03
        var c2 = Mid(x[3], x[4], x[5], x[6]);    // top side, p03 → p33
        var d2 = Mid(x[9], x[8], x[7], x[6]);    // right side, p30 → p33
        var c1 = Mid(x[0], x[11], x[10], x[9]);  // bottom side, p00 → p30
        var corners = ((x[0].X + x[3].X + x[6].X + x[9].X) / 4, (x[0].Y + x[3].Y + x[6].Y + x[9].Y) / 4);
        var expected = ((c1.X + c2.X) / 2 + (d1.X + d2.X) / 2 - corners.Item1, (c1.Y + c2.Y) / 2 + (d1.Y + d2.Y) / 2 - corners.Item2);
        H8745_HasVertex(mesh, expected);
    }

    /// <summary>8.7.4.5.7 sentence 19: where patches overlap, the later one paints over the earlier — red 20…140, then blue 60…180: blue in the overlap.</summary>
    [Fact]
    public void Coons_LaterPatchPaintsOverTheEarlier()
    {
        var first = H8745_PatchData(8, H8745_Square(20, 140), 255, 8, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0);
        var second = H8745_PatchData(8, H8745_Square(60, 180), 255, 8, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255);
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(6, 8, 8, H8745_ByteRgb), [.. first, .. second])));
        Assert.True(H8745_Blue(H8745_At(b, 100, 100)), $"{H8745_At(b, 100, 100)}");
        Assert.True(H8745_Red(H8745_At(b, 30, 30)), $"{H8745_At(b, 30, 30)}");
        Assert.True(H8745_Blue(H8745_At(b, 170, 170)), $"{H8745_At(b, 170, 170)}");
    }

    /// <summary>8.7.4.5.7 sentences 23–27: 32 bits per coordinate — the patch 20…180 where it belongs.</summary>
    [Fact]
    public void Coons_ThirtyTwoBitCoordinates()
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(6, 32, 8, [0, 200, 0, 200, 0, 1, 0, 1, 0, 1]),
            H8745_PatchData(32, H8745_Square(20, 180), 200, 8, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0))));
        foreach (var (x, y) in new[] { (23, 23), (177, 177), (100, 100), (177, 23) }) Assert.True(H8745_Red(H8745_At(b, x, y)), $"({x}, {y}): {H8745_At(b, x, y)}");
        foreach (var (x, y) in new[] { (10, 10), (190, 100), (100, 190) }) Assert.True(H8745_White(H8745_At(b, x, y)), $"({x}, {y}): {H8745_At(b, x, y)}");
    }

    /// <summary>8.7.4.5.7 sentences 28–30: 16 bits per component — red, green, blue, yellow at the corners 1, 4, 7, 10.</summary>
    [Fact]
    public void Coons_SixteenBitComponents()
    {
        const int F = 65535;
        using var b = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(6, 8, 16, H8745_ByteRgb),
            H8745_PatchData(8, H8745_Square(20, 180), 255, 16, F, 0, 0, 0, F, 0, 0, 0, F, F, F, 0))));
        Assert.True(H8745_Red(H8745_At(b, 24, 24)), $"{H8745_At(b, 24, 24)}");
        Assert.True(H8745_Green(H8745_At(b, 24, 176)), $"{H8745_At(b, 24, 176)}");
        Assert.True(H8745_Blue(H8745_At(b, 176, 176)), $"{H8745_At(b, 176, 176)}");
        var yellow = H8745_At(b, 176, 24);
        Assert.True(yellow.Red > 200 && yellow.Green > 200 && yellow.Blue < 60, $"{yellow}");
    }

    /// <summary>
    /// A first patch 70…130 (red, green, blue, yellow at the corners 1, 4, 7, 10; a tensor patch with its interior points at the thirds), then a
    /// second whose flag byte is <paramref name="flagByte"/>: its first edge taken over from the first patch as the flag <paramref name="meaning"/>
    /// says, the eight further points running 60 outwards from that edge, its own two corners black.
    /// </summary>
    private static byte[] H8745_TwoPatches(bool tensor, int flagByte, int meaning, out (double X, double Y) a, out (double X, double Y) b,
        out (double X, double Y) d, out int colourA, out int colourB)
    {
        var first = H8745_Square(70, 130);
        a = first[3 * meaning % 12];
        b = first[(3 * meaning + 3) % 12];
        var (ex, ey) = (b.X - a.X, b.Y - a.Y);
        d = (-ey, ex);   // the outer side of the edge, 60 long
        (colourA, colourB) = (meaning % 4, (meaning + 1) % 4);
        var (start, side) = (a, d);
        (double X, double Y) At(double along, double outward) => (start.X + along * ex + outward * side.X, start.Y + along * ey + outward * side.Y);
        var data = new H8745_Bits().Bytes(0);
        foreach (var (x, y) in first) data.Bytes((int)Math.Round(x), (int)Math.Round(y));
        if (tensor) foreach (var (x, y) in new[] { (90, 90), (90, 110), (110, 110), (110, 90) }) data.Bytes(x, y);
        data.Bytes(255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0).Align();
        data.Bytes(flagByte);
        (double, double)[] further = [At(1, 1 / 3.0), At(1, 2 / 3.0), At(1, 1), At(2 / 3.0, 1), At(1 / 3.0, 1), At(0, 1), At(0, 2 / 3.0), At(0, 1 / 3.0)];
        foreach (var (x, y) in further) data.Bytes((int)Math.Round(x), (int)Math.Round(y));
        if (tensor)
            foreach (var (x, y) in new[] { At(1 / 3.0, 1 / 3.0), At(2 / 3.0, 1 / 3.0), At(2 / 3.0, 2 / 3.0), At(1 / 3.0, 2 / 3.0) })
                data.Bytes((int)Math.Round(x), (int)Math.Round(y));
        data.Bytes(0, 0, 0, 0, 0, 0).Align();
        return data.ToArray();
    }

    /// <summary>
    /// 8.7.4.5.7 sentences 63–65, 69–78 (flag 1), 79–86 (flag 2), 87 (flag 3) and 31–35 (flag 5 in 8 bits read as 1); 8.7.4.5.8 sentences 46–49,
    /// 50–54 (flag 1), 55–62 (flag 2), 63–70 (flag 3): a patch with flag 1, 2 or 3 takes over an edge of the previous one — points x4…x7, x7…x10 or
    /// x10, x11, x12, x1 and the colours c2/c3, c3/c4 or c4/c1 — and brings only the rest: the second patch lies beyond that edge, near its ends the
    /// colours of the first patch there, at its far side black.
    /// </summary>
    [Theory]
    [InlineData(false, 1, 1)]
    [InlineData(false, 2, 2)]
    [InlineData(false, 3, 3)]
    [InlineData(false, 5, 1)]
    [InlineData(true, 1, 1)]
    [InlineData(true, 2, 2)]
    [InlineData(true, 3, 3)]
    public void Patches_FlagTakesOverTheEdgeOfThePrevious(bool tensor, int flagByte, int meaning)
    {
        var data = H8745_TwoPatches(tensor, flagByte, meaning, out var a, out var b, out var d, out var colourA, out var colourB);
        using var bitmap = H8745_Render("/Sh sh", H8745_Sh(_ => new PdfStream(H8745_MeshDict(tensor ? 7 : 6, 8, 8, H8745_ByteRgb), data)));
        Rgb[] colours = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 0)];
        void Like(Rgb expected, SKColor actual, string where) => Assert.True(
            (expected.R == 255 ? actual.Red > 170 : actual.Red < 90) && (expected.G == 255 ? actual.Green > 170 : actual.Green < 90)
            && (expected.B == 255 ? actual.Blue > 170 : actual.Blue < 90), $"{where}: {expected} erwartet, {actual}");
        (double X, double Y) P(double along, double outward) => (a.X + along * (b.X - a.X) + outward * d.X, a.Y + along * (b.Y - a.Y) + outward * d.Y);
        var nearA = P(0.08, 0.08);
        var nearB = P(0.92, 0.08);
        var far = P(0.5, 0.95);
        Like(colours[colourA], H8745_At(bitmap, nearA.X, nearA.Y), "bei A");
        Like(colours[colourB], H8745_At(bitmap, nearB.X, nearB.Y), "bei B");
        var black = H8745_At(bitmap, far.X, far.Y);
        Assert.True(black.Red < 60 && black.Green < 60 && black.Blue < 60, $"außen schwarz erwartet: {black}");
        Assert.True(H8745_Red(H8745_At(bitmap, 73, 73)), $"{H8745_At(bitmap, 73, 73)}");   // the first patch unchanged
    }

    /// <summary>
    /// 8.7.4.5.7 sentences 41–50 and 66–68: with a function each corner carries t, t is interpolated over the patch and the function applied afterwards —
    /// red → green → blue, t 0 at the lower corners and 1 at the upper ones: green in the middle, not violet.
    /// </summary>
    [Fact]
    public void Coons_WithFunction_InterpolatesTheParameter()
    {
        using var b = H8745_Render("/Sh sh", H8745_Sh(pdf =>
        {
            var dict = H8745_MeshDict(6, 8, 8, [0, 255, 0, 255, 0, 1]);
            dict.Set(TestPdf.N("Function"), H8745_Stops(pdf));
            return new PdfStream(dict, H8745_PatchData(8, H8745_Square(20, 180), 255, 8, 0, 255, 255, 0));
        }));
        Assert.True(H8745_Green(H8745_At(b, 100, 100)), $"{H8745_At(b, 100, 100)}");
        Assert.True(H8745_Red(H8745_At(b, 100, 23)), $"{H8745_At(b, 100, 23)}");
        Assert.True(H8745_Blue(H8745_At(b, 100, 177)), $"{H8745_At(b, 100, 177)}");
    }

    // ---------------------------------------------------------------- 8.7.4.5.8: type 7

    /// <summary>
    /// 8.7.4.5.8 sentences 1–2 and 39–40: a Coons patch is the tensor patch whose interior points follow the four formulas — the curved patch as
    /// type 6, and as type 7 with p11, p12, p22, p21 computed here by those formulas, give the same mesh (32-bit coordinates).
    /// </summary>
    [Fact]
    public void Tensor_WithCoonsInteriorPoints_SameMeshAsCoons()
    {
        (double X, double Y)[] boundary = [(20, 20), (10, 70), (30, 130), (20, 180), (70, 190), (130, 170), (180, 180), (190, 130), (170, 70), (180, 20), (130, 30), (70, 10)];
        var p = H8745_Grid(boundary);
        (double X, double Y) Sum(params (double W, int I, int J)[] terms) => (terms.Sum(t => t.W * p[t.I, t.J].X) / 9, terms.Sum(t => t.W * p[t.I, t.J].Y) / 9);
        var p11 = Sum((-4, 0, 0), (6, 0, 1), (6, 1, 0), (-2, 0, 3), (-2, 3, 0), (3, 3, 1), (3, 1, 3), (-1, 3, 3));
        var p12 = Sum((-4, 0, 3), (6, 0, 2), (6, 1, 3), (-2, 0, 0), (-2, 3, 3), (3, 3, 2), (3, 1, 0), (-1, 3, 0));
        var p22 = Sum((-4, 3, 3), (6, 3, 2), (6, 2, 3), (-2, 3, 0), (-2, 0, 3), (3, 2, 0), (3, 0, 2), (-1, 0, 0));
        var p21 = Sum((-4, 3, 0), (6, 3, 1), (6, 2, 0), (-2, 3, 3), (-2, 0, 0), (3, 0, 1), (3, 2, 3), (-1, 0, 3));
        int[] colours = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];
        double[] decode = [0, 200, 0, 200, 0, 1, 0, 1, 0, 1];
        var coons = H8745_MeshOf(H8745_Sh(_ => new PdfStream(H8745_MeshDict(6, 32, 8, decode), H8745_PatchData(32, boundary, 200, 8, colours))));
        var tensor = H8745_MeshOf(H8745_Sh(_ => new PdfStream(H8745_MeshDict(7, 32, 8, decode),
            H8745_PatchData(32, [.. boundary, p11, p12, p22, p21], 200, 8, colours))));
        Assert.Equal(coons.Points.Length, tensor.Points.Length);
        for (var i = 0; i < coons.Points.Length; i++)
            Assert.True(Math.Abs(coons.Points[i] - tensor.Points[i]) < 0.01, $"{i}: {coons.Points[i]} / {tensor.Points[i]}");
        Assert.Equal(coons.Colors, tensor.Colors);
    }

    /// <summary>
    /// 8.7.4.5.8 sentences 4–22 and 41–45: the 16 points form the array p[i, j] with p00 at the lower left, and the 13th point of the stream is p11 —
    /// moving only it (from 70/70 to 30/30) in a flat patch 20…170 warps the lower left quarter most, the corners stay where they were.
    /// </summary>
    [Fact]
    public void Tensor_Point13IsP11_WarpsTheLowerLeftQuarter()
    {
        var boundary = H8745_Square(20, 170);
        (double X, double Y)[] interior = [(70, 70), (70, 120), (120, 120), (120, 70)];
        MeshFill Mesh((double X, double Y)[] inner) => H8745_MeshOf(H8745_Sh(_ => new PdfStream(H8745_MeshDict(7, 8, 8, H8745_ByteRgb),
            H8745_PatchData(8, [.. boundary, .. inner], 255, 8, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0))));
        var flat = H8745_Vertices(Mesh(interior));
        var moved = H8745_Vertices(Mesh([(30, 30), interior[1], interior[2], interior[3]]));
        Assert.Equal(flat.Count, moved.Count);
        var most = 0;
        for (var k = 0; k < flat.Count; k++)
        {
            var shift = Math.Sqrt(Math.Pow(moved[k].X - flat[k].X, 2) + Math.Pow(moved[k].Y - flat[k].Y, 2));
            var largest = Math.Sqrt(Math.Pow(moved[most].X - flat[most].X, 2) + Math.Pow(moved[most].Y - flat[most].Y, 2));
            if (shift > largest) most = k;
            if (flat[k] is (20.0, 20.0) or (20.0, 170.0) or (170.0, 20.0) or (170.0, 170.0)) Assert.True(shift < 1e-3, $"Ecke {flat[k]} verschoben");
        }
        Assert.True(flat[most].X < 95 && flat[most].Y < 95, $"am stärksten verzogen bei {flat[most]}");
    }

    /// <summary>
    /// 8.7.4.5.8 sentences 23–30: the surface is the sum of p[i, j] · B_i(u) · B_j(v) with the Bernstein polynomials — for 16 arbitrary points the mesh
    /// has a point at S(0.25, 0.75) as computed here.
    /// </summary>
    [Fact]
    public void Tensor_SurfaceIsTheBernsteinSum()
    {
        (double X, double Y)[] points = [(20, 25), (15, 80), (35, 120), (25, 180), (75, 170), (120, 190), (180, 175), (170, 120), (190, 70), (175, 20),
            (120, 35), (70, 15), (60, 50), (80, 140), (140, 110), (110, 60)];
        var mesh = H8745_MeshOf(H8745_Sh(_ => new PdfStream(H8745_MeshDict(7, 8, 8, H8745_ByteRgb),
            H8745_PatchData(8, points, 255, 8, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0))));
        H8745_HasVertex(mesh, H8745_Surface(H8745_Grid(points), 0.25, 0.75));
    }

    /// <summary>
    /// 8.7.4.5.8 sentence 71: a patch with flag 0 brings all 16 points — the twelve of the edge, then p11, p12, p22, p21 — and the four colours c00,
    /// c03, c33, c30: the colours at the corners p00, p03, p33, p30, the surface bent by the interior points (S(0.5, 0.5) by the formula).
    /// </summary>
    [Fact]
    public void Tensor_FlagZero_SixteenPointsThenFourColours()
    {
        (double X, double Y)[] points = [.. H8745_Square(20, 180), (50, 60), (80, 140), (150, 130), (120, 50)];
        var resources = H8745_Sh(_ => new PdfStream(H8745_MeshDict(7, 8, 8, H8745_ByteRgb), H8745_PatchData(8, points, 255, 8, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0)));
        using var b = H8745_Render("/Sh sh", resources);
        Assert.True(H8745_Red(H8745_At(b, 24, 24)), $"{H8745_At(b, 24, 24)}");
        Assert.True(H8745_Green(H8745_At(b, 24, 176)), $"{H8745_At(b, 24, 176)}");
        Assert.True(H8745_Blue(H8745_At(b, 176, 176)), $"{H8745_At(b, 176, 176)}");
        var yellow = H8745_At(b, 176, 24);
        Assert.True(yellow.Red > 200 && yellow.Green > 200 && yellow.Blue < 60, $"{yellow}");
        var grid = H8745_Grid([.. points.Select(q => (Math.Round(q.X), Math.Round(q.Y)))]);
        H8745_HasVertex(H8745_MeshOf(resources), H8745_Surface(grid, 0.5, 0.5));
    }
}
