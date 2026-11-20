using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using Xunit.Abstractions;

namespace PdfKnead.Tests.Pdf;

public sealed class ContentParserTests
{
    [Fact]
    public void OperatorsAndOperands()
    {
        var p = new ContentParser("1 0 0 1 50.5 -2 cm /F1 12 Tf [(a) -20 (b)] TJ q Q"u8.ToArray());
        Assert.Equal("cm", p.Next());
        Assert.Equal([1, 0, 0, 1, 50.5, -2], p.Operands.Select(o => o.Number));
        Assert.Equal("Tf", p.Next());
        Assert.Equal("F1", p.Operands[0].Name!.Value);
        Assert.Equal("TJ", p.Next());
        Assert.Equal(3, Assert.IsType<PdfArray>(p.Operands[0].Object).Count);
        Assert.Equal("q", p.Next());
        Assert.Equal("Q", p.Next());
        Assert.Null(p.Next());
    }

    /// <summary>"EI" right after the data, without whitespace before it (the standard does not ask for it): the image ends there, the data
    /// do not take in the "EI" (a copy that writes the image anew would show two of them; docs/FEHLERGEDAECHTNIS.md, No. 305).</summary>
    [Fact]
    public void InlineImage_EiWithoutWhitespaceBefore_EndsTheData()
    {
        var bytes = Encoding.Latin1.GetBytes("BI /IM true /W 8 /H 1 /BPC 1 /F /CCF /DP <</K -1/Columns 8>> ID \u0080\u0001EI\n");
        var p = new ContentParser(bytes);
        Assert.Equal("BI", p.Next());
        Assert.Equal("\u0080\u0001", Encoding.Latin1.GetString(p.InlineImage!.Data));
        Assert.Null(p.Next());
    }

    [Fact]
    public void InlineImage_WithEiInsideData()
    {
        // 2×1 pixels RGB, the data happen to contain "EI"
        var bytes = Encoding.Latin1.GetBytes("BI /W 2 /H 1 /BPC 8 /CS /RGB ID EI EI\n EI Q");
        var p = new ContentParser(bytes);
        Assert.Equal("BI", p.Next());
        Assert.Equal("EI EI\n", Encoding.Latin1.GetString(p.InlineImage!.Data));
        Assert.Equal("Q", p.Next());
    }

    [Theory]
    [InlineData("BI /W 2 /H 1 /F /A85 ID Gb EI 9u~> EI Q", "Gb EI 9u~>")]   // issue10614: "EI" in the middle of ASCII85 data
    [InlineData("BI /W 2 /H 1 /F /A85 ID Gb9u~ EI Q BI /W 2 /H 1 /F /A85 ID 9u~> EI Q", "Gb9u~")]   // issue11385: the end is only "~"
    [InlineData("BI /W 2 /H 1 /F /AHx ID 0F E0> EI Q", "0F E0>")]
    public void InlineImage_TextFilters_EndAtTheirMarker(string content, string data)
    {
        var p = new ContentParser(Encoding.Latin1.GetBytes(content));
        Assert.Equal("BI", p.Next());
        Assert.Equal(data, Encoding.Latin1.GetString(p.InlineImage!.Data).TrimEnd());
        Assert.Equal("Q", p.Next());
    }

    [Fact]
    public void InlineImage_AbbreviatedKeysWinOverFullNames()
    {
        // issue14256: /W 20 and /Width 10 at the same time; the abbreviation wins, also for filter and colour space
        var bytes = Encoding.Latin1.GetBytes("BI /W 20 /Width 10 /H 10 /Height 40 /BPC 8 /BitsPerComponent 4 /CS /RGB /ColorSpace /G /F /AHx /Filter /A85 ID 00> EI Q");
        var p = new ContentParser(bytes);
        Assert.Equal("BI", p.Next());
        var dict = p.InlineImage!.Dictionary;
        Assert.Equal(20, dict.GetInteger(PdfName.Get("Width")));
        Assert.Equal(10, dict.GetInteger(PdfName.Get("Height")));
        Assert.Equal(8, dict.GetInteger(PdfName.Get("BitsPerComponent")));
        Assert.Equal("RGB", dict.GetName(PdfName.Get("ColorSpace"))?.Value);
        Assert.Equal("AHx", dict.GetName(PdfName.Filter)?.Value);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("-3.5")]
    [InlineData(".25")]
    [InlineData("-.5")]
    [InlineData("+7")]
    [InlineData("5.")]
    [InlineData("0.1")]
    [InlineData("612.0001")]
    [InlineData("-0")]
    [InlineData("123456789012345678")]
    [InlineData("1234567890123456789012")]      // too long for the fast path
    [InlineData("0.12345678901234567890123")]   // very many decimal places
    [InlineData("--5")]                         // double sign: 5, as in common readers (SyntaxTests)
    [InlineData("1.2.3")]                       // a second point ends the number
    [InlineData("-")]
    [InlineData(".")]
    public void Numbers_FastPath_EqualsLexer(string number)
    {
        // The fast path in the ContentParser and the lexer must read bit-identically
        var expected = new Core.Pdf.Parsing.PdfLexer(Core.Pdf.IO.PdfSource.FromBytes(Encoding.ASCII.GetBytes(number + " "))).Next();
        var value = expected.Kind == Core.Pdf.Parsing.PdfTokenKind.Integer ? expected.Integer : expected.Real;
        foreach (var text in new[] { $"{number} w", $"{number}\nw", $"[{number}] TJ", $"{number}" + "%x\nw" })
        {
            var p = new ContentParser(Encoding.ASCII.GetBytes(text));
            var op = p.Next();
            Assert.NotNull(op);
            var operand = p.Operands[0].Object is PdfArray array ? array.GetNumber(0) ?? double.NaN : p.Operands[0].Number;
            Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(operand));
        }
        // At the end without a following space
        var last = new ContentParser(Encoding.ASCII.GetBytes("w " + number));
        Assert.Equal("w", last.Next());
        Assert.Null(last.Next());
    }
}

public sealed class FunctionAndColorTests
{
    private static PdfDocument EmptyDoc() => PdfDocument.Open(TestPdf.Simple(1).Build());

    private static double[] Eval(PdfFunction f, params double[] input)
    {
        var output = new double[f.Outputs];
        f.Evaluate(input, output);
        return output;
    }

    [Fact]
    public void Exponential_And_Stitching()
    {
        using var doc = EmptyDoc();
        var exp = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)),
            ("C0", TestPdf.Box(0)), ("C1", TestPdf.Box(10)), ("N", PdfInteger.Of(2)));
        Assert.Equal(2.5, Eval(PdfFunction.Create(exp, doc)!, 0.5)[0], 6);

        var stitch = TestPdf.Dict(("FunctionType", PdfInteger.Of(3)), ("Domain", TestPdf.Box(0, 1)),
            ("Functions", new PdfArray([exp, exp])), ("Bounds", TestPdf.Box(0.5)), ("Encode", TestPdf.Box(0, 1, 1, 0)));
        var f = PdfFunction.Create(stitch, doc)!;
        Assert.Equal(10 * 0.25, Eval(f, 0.25)[0], 6);   // first segment: 0.25 → 0.5 → 2.5
        Assert.Equal(10 * 0.25, Eval(f, 0.75)[0], 6);   // second one, reversed: 0.75 → 0.5
    }

    [Fact]
    public void Sampled_TwoInputs_InterpolatesBetweenCorners()
    {
        // 2 × 2 samples 0, 1 / 1, 0: 0.5 in the middle (not the nearest sample, which Math.Round(0.5) = 0 would give)
        using var doc = EmptyDoc();
        var stream = new PdfStream(TestPdf.Dict(("FunctionType", PdfInteger.Of(0)), ("Domain", TestPdf.Box(0, 1, 0, 1)), ("Range", TestPdf.Box(0, 1)),
            ("Size", TestPdf.Box(2, 2)), ("BitsPerSample", PdfInteger.Of(8))), [0, 255, 255, 0]);
        var f = PdfFunction.Create(stream, doc)!;
        var o = new double[1];
        f.Evaluate([0.5, 0.5], o);
        Assert.Equal(0.5, o[0], 3);
        f.Evaluate([0.25, 0], o);
        Assert.Equal(0.25, o[0], 3);
        // Input with a /Domain of width 0 (devicen-none-zero-domain): no NaN
        var flat = new PdfStream(TestPdf.Dict(("FunctionType", PdfInteger.Of(0)), ("Domain", TestPdf.Box(0, 1, 0, 0)), ("Range", TestPdf.Box(0, 1)),
            ("Size", TestPdf.Box(2, 2)), ("BitsPerSample", PdfInteger.Of(8))), [0, 255, 0, 0]);
        PdfFunction.Create(flat, doc)!.Evaluate([0.5, 0], o);
        Assert.Equal(0.5, o[0], 3);
    }

    [Fact]
    public void PostScript_WithIfElse()
    {
        using var doc = EmptyDoc();
        var program = "{ dup 0.5 gt { 2 mul } { 1 exch sub } ifelse }"u8.ToArray();
        var stream = new PdfStream(TestPdf.Dict(("FunctionType", PdfInteger.Of(4)), ("Domain", TestPdf.Box(0, 1)), ("Range", TestPdf.Box(0, 10))), program);
        var f = PdfFunction.Create(stream, doc)!;
        Assert.Equal(1.6, Eval(f, 0.8)[0], 6);
        Assert.Equal(0.8, Eval(f, 0.2)[0], 6);
    }

    [Fact]
    public void PostScript_BrokenProgram_DoesNotThrow()
    {
        using var doc = EmptyDoc();
        var stream = new PdfStream(TestPdf.Dict(("FunctionType", PdfInteger.Of(4)), ("Domain", TestPdf.Box(0, 1)), ("Range", TestPdf.Box(0, 1))),
            "{ pop pop pop add }"u8.ToArray());
        var f = PdfFunction.Create(stream, doc)!;
        Assert.Equal(0, Eval(f, 0.5)[0]);
    }

    [Fact]
    public void Separation_UsesTintTransform()
    {
        using var doc = EmptyDoc();
        // Spot colour → CMYK via an exponential function: tint 1 = 100 % cyan
        var tint = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)),
            ("C0", TestPdf.Box(0, 0, 0, 0)), ("C1", TestPdf.Box(1, 0, 0, 0)), ("N", PdfInteger.Of(1)));
        var cs = new PdfArray([PdfName.Get("Separation"), PdfName.Get("Firmenblau"), PdfName.Get("DeviceCMYK"), tint]);
        var space = ColorSpace.Create(cs, null, doc);
        Assert.Equal(1, space.Components);
        // Like 100 % cyan in DeviceCMYK (via the FOGRA39 profile, not the simple formula)
        var cyan = ColorSpace.DeviceCmykOf(doc).ToRgb([1, 0, 0, 0]);
        Assert.Equal(cyan, space.ToRgb([1]));
        Assert.True(cyan is { R: < 40, G: > 120 and < 200, B: > 200 }, cyan.ToString());
        var paper = space.ToRgb([0]);
        Assert.True(paper is { R: >= 250, G: >= 250, B: >= 250 }, paper.ToString());
    }

    [Fact]
    public void Indexed_LooksUpPalette()
    {
        using var doc = EmptyDoc();
        var cs = new PdfArray([PdfName.Get("Indexed"), PdfName.Get("DeviceRGB"), PdfInteger.Of(1), new PdfString([255, 0, 0, 0, 0, 255])]);
        var space = ColorSpace.Create(cs, null, doc);
        Assert.Equal(new Rgb(0, 0, 255), space.ToRgb([1]));
    }

    [Fact]
    public void Lab_WithInvertedRange_UsesTheDefault()
    {
        // Found by fuzzing: /Range [65536 127 …]; Math.Clamp would throw during conversion (reaching as far as redaction and saving)
        using var doc = EmptyDoc();
        var lab = new PdfArray([PdfName.Get("Lab"), TestPdf.Dict(("WhitePoint", TestPdf.Box(0.9505, 1, 1.089)), ("Range", TestPdf.Box(65536, 127, 0, double.NaN)))]);
        var space = ColorSpace.Create(lab, null, doc);
        var white = space.ToRgb([100, 0, 0]);
        Assert.True(white is { R: > 240, G: > 240, B: > 240 }, white.ToString());
    }
}

public sealed class InterpreterTests
{
    /// <summary>Builds a single-page PDF with its own content; <paramref name="resources"/> may create further objects.</summary>
    internal static (PdfDocument Doc, DisplayList List) Page(string content, Func<TestPdf, PdfDictionary>? resources = null, Action<TestPdf, PdfDictionary>? catalog = null)
    {
        var pdf = new TestPdf();
        var cat = TestPdf.Dict(("Type", TestPdf.N("Catalog")));
        var catNumber = pdf.Add(cat);
        var pagesNumber = pdf.Add(new PdfDictionary());
        var res = resources?.Invoke(pdf) ?? new PdfDictionary();
        var contentNumber = pdf.Add(TestPdf.FlateStream(Encoding.Latin1.GetBytes(content)));
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNumber)), ("MediaBox", TestPdf.Box(0, 0, 200, 200)),
            ("Resources", res), ("Contents", TestPdf.Ref(contentNumber))));
        pdf.Replace(pagesNumber, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        cat.Set(PdfName.Pages, TestPdf.Ref(pagesNumber));
        catalog?.Invoke(pdf, cat);
        pdf.RootNumber = catNumber;
        var doc = PdfDocument.Open(pdf.Build());
        return (doc, PageInterpreter.Build(doc, doc.Pages[0]));
    }

    private static SkiaSharp.SKColor PixelAt(PdfDocument doc, DisplayList list, double x, double pdfY)
    {
        using var bitmap = PdfKnead.Rendering.SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new PdfKnead.Rendering.ImageCache());
        return bitmap.GetPixel((int)x, (int)(bitmap.Height - pdfY));
    }

    /// <summary>
    /// A function-based shading (type 1) with one function per colour component, each a function of x and y, painted as a pattern and
    /// with sh: red where the functions say so, nothing beside its domain. Read as a function with one input, the shading counted as invalid
    /// and a grey box stood in its place (docs/FEHLERGEDAECHTNIS.md, No. 436).
    /// </summary>
    [Theory]
    [InlineData("/Pattern cs /P1 scn 0 0 200 200 re f")]
    [InlineData("/Sh1 sh")]
    public void FunctionBasedShading_OneFunctionPerComponent_Painted(string content)
    {
        var (doc, list) = Page(content, pdf =>
        {
            PdfObject F(string code) => TestPdf.Ref(pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes(code), TestPdf.Dict(("FunctionType", PdfInteger.Of(4)),
                ("Domain", TestPdf.Box(0, 1, 0, 1)), ("Range", TestPdf.Box(0, 1))))));
            var shading = pdf.Add(TestPdf.Dict(("ShadingType", PdfInteger.Of(1)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Domain", TestPdf.Box(0, 1, 0, 1)),
                ("Matrix", TestPdf.Box(100, 0, 0, 100, 50, 50)), ("Function", new PdfArray([F("{ pop pop 1 }"), F("{ pop pop 0 }"), F("{ pop pop 0 }")]))));
            var pattern = pdf.Add(TestPdf.Dict(("PatternType", PdfInteger.Of(2)), ("Shading", TestPdf.Ref(shading))));
            return TestPdf.Dict(("Pattern", TestPdf.Dict(("P1", TestPdf.Ref(pattern)))), ("Shading", TestPdf.Dict(("Sh1", TestPdf.Ref(shading)))));
        });
        using (doc)
        {
            var inside = PixelAt(doc, list, 100, 100);
            Assert.True(inside.Red > 200 && inside.Green < 60 && inside.Blue < 60, $"innen {inside}");
            var outside = PixelAt(doc, list, 20, 20);
            Assert.True(outside.Red > 240 && outside.Green > 240 && outside.Blue > 240, $"außen {outside}");
        }
    }

    /// <summary>
    /// A stitching function with as many bounds as functions, the last bound at the end of the domain (as some generators write it): read
    /// without the last bound, as the common readers do — the axial shading was left out entirely (docs/FEHLERGEDAECHTNIS.md, No. 436).
    /// </summary>
    [Fact]
    public void StitchingFunctionWithOneBoundTooMany_Painted()
    {
        var (doc, list) = Page("/Sh1 sh", pdf =>
        {
            PdfObject Exp(double r0, double g0, double b0, double r1, double g1, double b1) => TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)),
                ("C0", TestPdf.Box(r0, g0, b0)), ("C1", TestPdf.Box(r1, g1, b1)), ("N", PdfInteger.Of(1)));
            var function = TestPdf.Dict(("FunctionType", PdfInteger.Of(3)), ("Domain", TestPdf.Box(0, 1)), ("Functions", new PdfArray([Exp(1, 0, 0, 0, 1, 0), Exp(0, 1, 0, 0, 0, 1)])),
                ("Bounds", TestPdf.Box(0.5, 1)), ("Encode", TestPdf.Box(0, 1, 0, 1)));
            var shading = pdf.Add(TestPdf.Dict(("ShadingType", PdfInteger.Of(2)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Coords", TestPdf.Box(0, 0, 200, 0)),
                ("Function", function), ("Extend", new PdfArray([PdfBoolean.True, PdfBoolean.True]))));
            return TestPdf.Dict(("Shading", TestPdf.Dict(("Sh1", TestPdf.Ref(shading)))));
        });
        using (doc)
        {
            var left = PixelAt(doc, list, 10, 100);
            Assert.True(left.Red > 150 && left.Blue < 60, $"links {left}");
            var right = PixelAt(doc, list, 190, 100);
            Assert.True(right.Blue > 150 && right.Red < 60, $"rechts {right}");
        }
    }

    [Fact]
    public void Fill_HasColorAndBounds()
    {
        var (doc, list) = Page("1 0 0 rg 10 20 100 50 re f");
        using (doc)
        {
            var item = Assert.Single(list.Items.ToArray());
            Assert.Equal(DisplayItemKind.Fill, item.Kind);
            Assert.Equal((10f, 20f, 110f, 70f), (item.Left, item.Top, item.Right, item.Bottom));
            Assert.Equal(0xFFFF0000u, list.Paints[item.Paint].Fill);
        }
    }

    [Fact]
    public void Stroke_InflatesBoundsByHalfWidth_AndTransformsWithCtm()
    {
        var (doc, list) = Page("2 0 0 2 0 0 cm 4 w 1 J 10 10 m 20 10 l S");
        using (doc)
        {
            var item = Assert.Single(list.Items.ToArray());
            Assert.Equal(DisplayItemKind.Stroke, item.Kind);
            // Line 20..40 (×2), half width 4·2/2 = 4
            Assert.Equal(16f, item.Left);
            Assert.Equal(44f, item.Right);
            Assert.Equal(4f, list.Paints[item.Paint].LineWidth);   // in the space of the path; the matrix scales
        }
    }

    [Fact]
    public void SaveRestore_AndClipping()
    {
        var (doc, list) = Page("q 0 0 50 50 re W n 0 0 1 rg 0 0 200 200 re f Q 0 g 100 100 10 10 re f");
        using (doc)
        {
            var items = list.Items.ToArray();
            Assert.Equal(2, items.Length);
            Assert.Equal(50f, items[0].Right);              // clipped to the clipping path
            Assert.NotEqual(0, items[0].Clip);
            Assert.Equal(0, items[1].Clip);                 // no clipping after Q
            Assert.Equal(0xFF000000u, list.Paints[items[1].Paint].Fill);
        }
    }

    [Fact]
    public void FullyClippedItems_AreDropped()
    {
        var (doc, list) = Page("0 0 10 10 re W n 100 100 10 10 re f");
        using (doc) Assert.Equal(0, list.Count);
    }

    [Fact]
    public void HiddenLayer_IsSkipped()
    {
        var ocgNumber = 0;
        var (doc, list) = Page("/OC /Aus BDC 0 0 10 10 re f EMC 20 20 10 10 re f",
            pdf =>
            {
                ocgNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Bemaßung"u8.ToArray()))));
                return TestPdf.Dict(("Properties", TestPdf.Dict(("Aus", TestPdf.Ref(ocgNumber)))));
            },
            (pdf, cat) =>
            {
                var ocgRef = TestPdf.Ref(ocgNumber);
                cat.Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([ocgRef])),
                    ("D", TestPdf.Dict(("OFF", new PdfArray([ocgRef]))))));
            });
        using (doc) Assert.Equal(20f, Assert.Single(list.Items.ToArray()).Left);
    }

    [Fact]
    public void FormXObject_IsDrawn_AndSelfReferenceDoesNotLoop()
    {
        var (doc, list) = Page("q 2 0 0 2 0 0 cm /Fm Do Q",
            pdf =>
            {
                // The form XObject draws a rectangle and calls itself again
                var number = pdf.Count + 1;
                var form = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 100, 100)),
                    ("Resources", TestPdf.Dict(("XObject", TestPdf.Dict(("Fm", TestPdf.Ref(number))))))), "5 5 10 10 re f /Fm Do"u8.ToArray());
                pdf.Add(form);
                return TestPdf.Dict(("XObject", TestPdf.Dict(("Fm", TestPdf.Ref(number)))));
            });
        using (doc)
        {
            var item = Assert.Single(list.Items.ToArray());
            Assert.Equal(10f, item.Left);   // 5 × 2
            Assert.NotEqual(0, item.Clip);  // BBox
        }
    }

    [Fact]
    public void Image_IsPlacedInUnitSquare_AndDecodes()
    {
        var (doc, list) = Page("q 40 0 0 20 10 10 cm /Im Do Q",
            pdf =>
            {
                var image = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
                    ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(1)), ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", TestPdf.N("DeviceRGB"))),
                    [255, 0, 0, 0, 0, 255]);
                var n = pdf.Add(image);
                return TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(n)))));
            });
        using (doc)
        {
            var item = Assert.Single(list.Items.ToArray());
            Assert.Equal(DisplayItemKind.Image, item.Kind);
            Assert.Equal((10f, 10f, 50f, 30f), (item.Left, item.Top, item.Right, item.Bottom));
            var decoded = PdfImageDecoder.Decode(list.Images[item.Image], doc)!;
            Assert.Equal([255, 0, 0, 255, 0, 0, 255, 255], decoded.Rgba);
        }
    }

    [Fact]
    public void StencilMask_UsesFillColor()
    {
        var (doc, list) = Page("0 1 0 rg q 8 0 0 1 0 0 cm BI /W 8 /H 1 /IM true ID \u000F EI Q");
        using (doc)
        {
            var item = Assert.Single(list.Items.ToArray());
            var decoded = PdfImageDecoder.Decode(list.Images[item.Image], doc)!;
            Assert.Equal(255, decoded.Rgba[1]);      // bit 0 = paint → green
            Assert.Equal(255, decoded.Rgba[3]);
            Assert.Equal(0, decoded.Rgba[7 * 4 + 3]); // bit 1 = transparent
        }
    }

    [Fact]
    public void SpatialIndex_ReturnsOnlyVisibleItems_InPaintOrder()
    {
        // Grid of 50 × 50 rectangles
        var sb = new StringBuilder();
        for (var y = 0; y < 50; y++)
            for (var x = 0; x < 50; x++)
                sb.Append($"{x * 4} {y * 4} 3 3 re f\n");
        var (doc, list) = Page(sb.ToString());
        using (doc)
        {
            Assert.Equal(2500, list.Count);
            var hits = list.Query(RectD.FromLtrb(0, 0, 10, 10));   // 3 × 3 rectangles
            Assert.Equal(9, hits.Count);
            Assert.Equal(hits.Order(), hits);
        }
    }
}

/// <summary>Translates all pages of the corpus into display lists: no exceptions, time within bounds.</summary>
public sealed class DisplayListCorpusTests(ITestOutputHelper output)
{
    private static string Folder([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "TestPDFs"));

    [Fact]
    public void Corpus_BuildsDisplayLists()
    {
        var folder = Folder();
        if (!Directory.Exists(folder)) return;
        var watch = Stopwatch.StartNew();
        long items = 0;
        var pages = 0;
        var slowest = (Name: "", Ms: 0L, Items: 0);
        foreach (var file in Directory.EnumerateFiles(folder, "*.pdf", SearchOption.AllDirectories))
        {
            PdfDocument doc;
            try { doc = PdfDocument.Open(file); }
            catch (PdfException) { continue; }
            using (doc)
            {
                foreach (var page in doc.Pages.Take(5))
                {
                    var t = Stopwatch.StartNew();
                    DisplayList list;
                    try { list = PageInterpreter.Build(doc, page); }
                    catch (PdfLimitException) { continue; }
                    items += list.Count;
                    pages++;
                    if (t.ElapsedMilliseconds > slowest.Ms) slowest = (Path.GetFileName(file), t.ElapsedMilliseconds, list.Count);
                }
            }
        }
        output.WriteLine($"{pages} Seiten, {items:N0} Einträge, {watch.Elapsed.TotalSeconds:0.0} s; langsamste: {slowest.Name} {slowest.Ms} ms ({slowest.Items:N0} Einträge)");
    }
}
