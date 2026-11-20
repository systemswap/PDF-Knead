using System.Diagnostics;
using System.Text;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Content beneath an opaque entry over the whole page is not drawn (fuzzing S7: 18,000 glyphs, each with a shading over the page,
/// took 14 s at 400 % zoom): it stays in the display list for whatever reads the content, only the spatial index leaves it out. Only
/// where nothing of it can show — at full opacity, Normal blending, without overprinting, within rectangular clips; inside a
/// transparency group only what the group drew before.
/// </summary>
public sealed class OcclusionTests
{
    private static PdfDictionary RedToBlue() => Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", Box(0, 1)),
        ("C0", Box(1, 0, 0)), ("C1", Box(0, 0, 1)), ("N", PdfInteger.Of(1)));

    private static PdfDictionary Axial(bool extend) => Dict(("ShadingType", PdfInteger.Of(2)), ("ColorSpace", N("DeviceRGB")),
        ("Coords", Box(0, 0, 200, 0)), ("Function", RedToBlue()), ("Extend", new PdfArray([PdfBoolean.Of(extend), PdfBoolean.Of(extend)])));

    private static PdfDictionary Radial(double x0, double r0, double x1, double r1) => Dict(("ShadingType", PdfInteger.Of(3)),
        ("ColorSpace", N("DeviceRGB")), ("Coords", Box(x0, 100, r0, x1, 100, r1)), ("Function", RedToBlue()),
        ("Extend", new PdfArray([PdfBoolean.True, PdfBoolean.True])));

    /// <summary>Shading /Sh, graphics states /Half (opacity 0.5), /Multiply, /Over (overprint) and an opaque image /I over the page.</summary>
    private static Func<TestPdf, PdfDictionary> Resources(PdfDictionary shading, PdfDictionary? image = null) => pdf =>
    {
        var pixels = Enumerable.Repeat((byte)0x80, 2 * 2 * 3).ToArray();
        var imageDict = image ?? Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(2)),
            ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", N("DeviceRGB")));
        return Dict(("Shading", Dict(("Sh", shading))),
            ("ExtGState", Dict(("Half", Dict(("ca", new PdfReal(0.5)))), ("Multiply", Dict(("BM", N("Multiply")))), ("Over", Dict(("op", PdfBoolean.True))))),
            ("XObject", Dict(("I", Ref(pdf.Add(FlateStream(pixels, imageDict)))))));
    };

    private static int FirstVisible(string content, PdfDictionary? shading = null, PdfDictionary? image = null)
    {
        var (doc, list) = InterpreterTests.Page(content, Resources(shading ?? Axial(true), image));
        using (doc) return list.FirstVisible;
    }

    [Fact]
    public void OpaqueOverThePage_HidesWhatLiesBeneath_OnlyForDrawing()
    {
        var (doc, list) = InterpreterTests.Page("1 0 0 rg 20 20 50 50 re f 0 0 1 rg 100 100 50 50 re f /Sh sh", Resources(Axial(true)));
        using (doc)
        {
            Assert.Equal(3, list.Count);   // the hidden entries stay in the list
            Assert.Equal(2, list.FirstVisible);
            Assert.Equal(new[] { 2 }, list.Query(list.PageBox));
            using var b = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
            var left = b.GetPixel(40, b.Height - 1 - 40);   // where the red square was: now the start of the shading
            Assert.True(left.Red > 200 && left.Blue < 80, $"{left}");
        }
        Assert.Equal(2, FirstVisible("1 0 0 rg 0 0 10 10 re f 0 0 10 10 re f q 200 0 0 200 0 0 cm /I Do Q"));
        Assert.Equal(1, FirstVisible("1 0 0 rg 0 0 10 10 re f q 0 0 200 200 re W n /Sh sh Q"));   // a rectangular clip around the page
        Assert.Equal(1, FirstVisible("1 0 0 rg 0 0 10 10 re f /Sh sh", Radial(100, 0, 100, 300)));   // one circle inside the other
    }

    /// <summary>
    /// The same opaque picture drawn again and again at the same place, a little smaller than the page (a crafted file: 13,000 times a
    /// page-sized JPEG hung the drawing): only the last one is drawn; what does not lie wholly beneath it, or lies beneath a half-transparent
    /// one, stays (docs/FEHLERGEDAECHTNIS.md, No. 276).
    /// </summary>
    [Fact]
    public void SamePictureAgainAndAgain_OnlyTheLastIsDrawn()
    {
        var again = string.Concat(Enumerable.Repeat("q 199.44 0 0 199.68 0.3 0 cm /I Do Q ", 400));   // numbers a float cannot hold exactly
        var (doc, list) = InterpreterTests.Page(again, Resources(Axial(true), null));
        using (doc)
        {
            Assert.Equal(400, list.Count);
            Assert.All(Enumerable.Range(0, 399), i => Assert.True(list.IsHidden(i), $"{i}"));
            Assert.False(list.IsHidden(399));
        }
        // a square beside the picture, and a picture drawn half transparent: they stay
        (doc, list) = InterpreterTests.Page("q 100 0 0 100 0 0 cm /I Do Q 150 150 10 10 re f q 100 0 0 100 0 0 cm /I Do Q /Half gs q 100 0 0 100 0 0 cm /I Do Q",
            Resources(Axial(true), null));
        using (doc)
        {
            Assert.True(list.IsHidden(0) == false && list.IsHidden(1) == false && list.IsHidden(2) == false && list.IsHidden(3) == false);
        }
    }

    /// <summary>Wherever something beneath could still show, nothing is hidden.</summary>
    [Theory]
    [InlineData("0 0 10 10 re f /Half gs /Sh sh")]
    [InlineData("0 0 10 10 re f /Multiply gs /Sh sh")]
    [InlineData("0 0 10 10 re f /Over gs /Sh sh")]
    [InlineData("0 0 10 10 re f q 50 50 100 100 re W n /Sh sh Q")]                       // clip smaller than the page
    [InlineData("0 0 10 10 re f q 0 0 m 200 0 l 100 200 l h W n /Sh sh Q")]               // clip not a rectangle
    [InlineData("0 0 10 10 re f q 100 0 0 100 0 0 cm /I Do Q")]                            // image smaller than the page
    [InlineData("0 0 10 10 re f q 141 141 -141 141 100 -100 cm /I Do Q")]                  // rotated image
    [InlineData("0 0 10 10 re f /Half gs q 200 0 0 200 0 0 cm /I Do Q")]
    public void NothingHidden_WhereItCouldShow(string content) => Assert.Equal(0, FirstVisible(content));

    [Fact]
    public void NothingHidden_ForShadingsWithGaps_OrImagesWithMasks()
    {
        Assert.Equal(0, FirstVisible("0 0 10 10 re f /Sh sh", Axial(false)));                 // ends left empty
        Assert.Equal(0, FirstVisible("0 0 10 10 re f /Sh sh", Radial(50, 20, 150, 20)));      // circles side by side: a cone
        var boxed = Axial(true);
        boxed.Set(N("BBox"), Box(0, 0, 200, 200));
        Assert.Equal(0, FirstVisible("0 0 10 10 re f /Sh sh", boxed));
        var keyed = Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(2)),
            ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", N("DeviceRGB")), ("Mask", Box(0, 10, 0, 10, 0, 10)));
        Assert.Equal(0, FirstVisible("0 0 10 10 re f q 200 0 0 200 0 0 cm /I Do Q", image: keyed));
        // inside a transparency group with opacity: the group is composited as a whole (a group without opacity, blend mode or mask
        // looks the same as none and is not begun — there, hiding is right)
        var (doc, list) = InterpreterTests.Page("0 0 10 10 re f /Half gs /G Do", pdf => Dict(
            ("ExtGState", Dict(("Half", Dict(("ca", new PdfReal(0.5)))))),
            ("XObject", Dict(("G", Ref(pdf.Add(new PdfStream(
                Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 200, 200)), ("Group", Dict(("S", N("Transparency")))),
                    ("Resources", Dict(("Shading", Dict(("Sh", Axial(true))))))),
                Encoding.ASCII.GetBytes("/Sh sh")))))))));
        using (doc) Assert.Equal(0, list.FirstVisible);
    }

    /// <summary>A form with a transparency group, drawn at half opacity: <paramref name="inner"/> its content (resources: /Sh, /Half).</summary>
    private static (Core.Pdf.Document.PdfDocument Doc, DisplayList List) InGroup(string outer, string inner) =>
        InterpreterTests.Page(outer, pdf =>
        {
            var half = Dict(("Half", Dict(("ca", new PdfReal(0.5)))));
            var resources = Dict(("Shading", Dict(("Sh", Axial(true)))), ("ExtGState", half));
            var form = Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 200, 200)),
                ("Group", Dict(("S", N("Transparency")))), ("Resources", resources));
            return Dict(("ExtGState", half), ("XObject", Dict(("G", Ref(pdf.Add(new PdfStream(form, Encoding.ASCII.GetBytes(inner))))))));
        });

    [Fact]
    public void InsideAGroup_OnlyTheGroupsEarlierEntriesAreHidden()
    {
        // outside: a green square (stays, the group is half transparent); inside: a green square beneath an opaque shading (hidden)
        var (doc, list) = InGroup("0 1 0 rg 100 100 50 50 re f /Half gs /G Do", "0 1 0 rg 20 20 50 50 re f /Sh sh");
        using (doc)
        {
            Assert.Equal(3, list.Count);
            Assert.Equal(0, list.FirstVisible);
            Assert.Equal([false, true, false], [list.IsHidden(0), list.IsHidden(1), list.IsHidden(2)]);
            Assert.Equal(new[] { 0, 2 }, list.Query(list.PageBox).Order());
            using var b = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
            // the group is the shading alone, at half opacity over white; the outer square shows through it
            var inner = b.GetPixel(40, b.Height - 1 - 40);
            var outer = b.GetPixel(120, b.Height - 1 - 120);
            Assert.True(inner.Red > 200 && inner.Green is > 110 and < 145, $"{inner}");   // shading half over white
            Assert.True(outer.Red < 80 && outer.Green is > 110 and < 145, $"{outer}");     // shading half over green
        }
        // translucent inside the group: nothing hidden
        var (doc2, list2) = InGroup("/Half gs /G Do", "0 1 0 rg 20 20 50 50 re f /Half gs /Sh sh");
        using (doc2) Assert.False(list2.IsHidden(0));
    }

    /// <summary>
    /// 700 opaque shadings over the page inside the transparency group of a form (a damaged file, found by fuzzing the code search: 7 s
    /// per page at 96 dpi, 33 s at the resolution of the search): only the last one is drawn.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void InsideAGroup_ManyShadingsOverThePage_OnlyTheLastIsDrawn()
    {
        var inner = string.Concat(Enumerable.Repeat("q 0 0 200 200 re W n /Sh sh Q\n", 700));
        var (doc, list) = InGroup("/Half gs /G Do", inner);
        using (doc)
        {
            Assert.Equal(700, list.Count);
            Assert.Equal(new[] { 699 }, list.Query(list.PageBox));
            var watch = Stopwatch.StartNew();
            using var b = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 4, new ImageCache());
            Assert.True(watch.ElapsedMilliseconds < 2000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>
    /// 18,000 glyphs of a Type 3 font on the page, each painting an opaque shading over the whole page (fuzzing S7): only the last one
    /// is drawn — before, 14 s at 400 % zoom.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void Type3_OpaqueShadingPerGlyph_OnlyTheLastIsDrawn()
    {
        var text = "(" + new string('a', 18_000) + ") Tj";
        var (doc, list) = InterpreterTests.Page($"BT /T3 50 Tf 10 10 Td {text} ET", pdf =>
        {
            var proc = pdf.Add(FlateStream(Encoding.ASCII.GetBytes("1000 0 d0 /Sh sh")));
            var font = Dict(("Type", N("Font")), ("Subtype", N("Type3")), ("FontMatrix", Box(0.001, 0, 0, 0.001, 0, 0)),
                ("FontBBox", Box(0, 0, 1000, 1000)), ("CharProcs", Dict(("g", Ref(proc)))),
                ("Encoding", Dict(("Differences", new PdfArray([PdfInteger.Of(97), N("g")])))),
                ("FirstChar", PdfInteger.Of(97)), ("LastChar", PdfInteger.Of(97)), ("Widths", new PdfArray([PdfInteger.Of(0)])),
                ("Resources", Dict(("Shading", Dict(("Sh", Axial(true)))))));
            return Dict(("Font", Dict(("T3", font))));
        });
        using (doc)
        {
            Assert.Equal(18_000, list.Count);
            Assert.Equal(17_999, list.FirstVisible);
            var watch = Stopwatch.StartNew();
            using var b = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 4, new ImageCache());
            Assert.True(watch.ElapsedMilliseconds < 2000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
            Assert.NotEqual(SKColors.White, b.GetPixel(b.Width / 2, b.Height / 2));
        }
    }
}
