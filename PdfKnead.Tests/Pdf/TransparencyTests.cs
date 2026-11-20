using System.Diagnostics;
using System.Text;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Transparency groups, knockout and soft masks. Page 200 × 200 pt, 1 pixel per point.</summary>
public sealed class TransparencyTests
{
    private static SKBitmap Render(string content, Func<TestPdf, PdfDictionary> resources)
    {
        var (doc, list) = InterpreterTests.Page(content, resources);
        using (doc) return SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
    }

    private static SKColor At(SKBitmap b, int x, int pdfY) => b.GetPixel(x, b.Height - 1 - pdfY);

    private static void Near(SKColor expected, SKColor actual, int tolerance = 6) =>
        Assert.True(Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance
            && Math.Abs(expected.Blue - actual.Blue) <= tolerance, $"erwartet {expected}, ist {actual}");

    private static PdfStream Form(string content, PdfDictionary? group = null, PdfDictionary? resources = null)
    {
        var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 200, 200)));
        if (group is not null) dict.Set(TestPdf.N("Group"), group);
        if (resources is not null) dict.Set(TestPdf.N("Resources"), resources);
        return new PdfStream(dict, Encoding.ASCII.GetBytes(content));
    }

    private static PdfDictionary Group(bool isolated = false, bool knockout = false, PdfObject? colorSpace = null)
    {
        var g = TestPdf.Dict(("S", TestPdf.N("Transparency")), ("I", PdfBoolean.Of(isolated)), ("K", PdfBoolean.Of(knockout)));
        if (colorSpace is not null) g.Set(TestPdf.N("CS"), colorSpace);
        return g;
    }

    private static PdfDictionary States(params (string Name, PdfDictionary State)[] states) =>
        TestPdf.Dict(states.Select(s => (s.Name, (PdfObject)s.State)).ToArray());

    /// <summary>Resources with the form XObject /Fm and graphics states.</summary>
    private static Func<TestPdf, PdfDictionary> WithForm(PdfStream form, PdfDictionary? states = null) => pdf =>
    {
        var resources = TestPdf.Dict(("XObject", TestPdf.Dict(("Fm", TestPdf.Ref(pdf.Add(form))))));
        if (states is not null) resources.Set(TestPdf.N("ExtGState"), states);
        return resources;
    };

    private static PdfDictionary Half() => States(("H", TestPdf.Dict(("ca", new PdfReal(0.5)))));

    private const string TwoRedSquares = "1 0 0 rg 0 0 120 120 re f 80 80 120 120 re f";

    [Fact]
    public void GroupAlpha_AppliesToTheWholeGroup()
    {
        // ca 0.5 on a group: the overlap is not darker than the rest
        using var b = Render("/H gs /Fm Do", WithForm(Form(TwoRedSquares, Group()), Half()));
        Near(new SKColor(255, 128, 128), At(b, 50, 50));
        Near(new SKColor(255, 128, 128), At(b, 100, 100));
    }

    [Fact]
    public void FormWithoutGroup_PaintsEachObjectOnItsOwn()
    {
        // Without a group ca applies to each object separately: the overlap becomes darker
        using var b = Render("/H gs /Fm Do", WithForm(Form(TwoRedSquares), Half()));
        Near(new SKColor(255, 128, 128), At(b, 50, 50));
        Near(new SKColor(255, 64, 64), At(b, 100, 100), 8);
    }

    [Fact]
    public void Knockout_LaterObjectsReplaceEarlierOnes()
    {
        // Red, then semi-transparent blue: in a knockout group the blue replaces the red (half blue over the white of the page)
        const string content = "1 0 0 rg 0 0 120 120 re f /H gs 0 0 1 rg 80 80 120 120 re f";
        using (var b = Render("/Fm Do", WithForm(Form(content, Group(isolated: true, knockout: true), TestPdf.Dict(("ExtGState", Half()))))))
            Near(new SKColor(128, 128, 255), At(b, 100, 100));
        using (var b = Render("/Fm Do", WithForm(Form(content, Group(isolated: true), TestPdf.Dict(("ExtGState", Half()))))))
            Near(new SKColor(128, 0, 128), At(b, 100, 100));   // without knockout: blue over red
    }

    [Fact]
    public void Knockout_TextReplacesOnlyItsGlyphs()
    {
        // Text on an area in a knockout group: between the glyphs the area remains (no white boxes)
        const string content = "0.6 0.8 1 rg 10 10 180 180 re f 0 g BT /F1 14 Tf 20 100 Td (Knockout Text) Tj ET";
        var font = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")));
        var fonts = TestPdf.Dict(("Font", TestPdf.Dict(("F1", font))));
        using var b = Render("/Fm Do", WithForm(Form(content, Group(isolated: true, knockout: true), fonts)));
        var white = 0;
        var dark = 0;
        for (var y = 96; y <= 112; y++)
        for (var x = 20; x <= 120; x++)
        {
            var c = At(b, x, y);
            if (c.Red > 250 && c.Green > 250 && c.Blue > 250) white++;
            if (c.Red < 60) dark++;
        }
        Assert.Equal(0, white);
        Assert.True(dark > 50, "Text fehlt");
    }

    [Fact]
    public void Knockout_StencilReplacesOnlyItsShape()
    {
        // Stencil mask 2 × 1 (left painted) over a red area in a knockout group: red stays on the right
        var mask = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(1)), ("ImageMask", PdfBoolean.True)), [0b0100_0000]);
        using var b = Render("/Fm Do", pdf =>
        {
            var inner = TestPdf.Dict(("XObject", TestPdf.Dict(("M", TestPdf.Ref(pdf.Add(mask))))));
            var form = Form("1 0 0 rg 0 0 200 200 re f 0 0 1 rg q 200 0 0 200 0 0 cm /M Do Q", Group(isolated: true, knockout: true), inner);
            return TestPdf.Dict(("XObject", TestPdf.Dict(("Fm", TestPdf.Ref(pdf.Add(form))))));
        });
        Near(SKColors.Blue, At(b, 50, 100));
        Near(SKColors.Red, At(b, 150, 100));
    }

    [Fact]
    public void IsolatedGroup_BlendsOnlyWithinItself()
    {
        // Yellow on the page, blue with Multiply above it: isolated it stays blue, otherwise it becomes black
        const string content = "/M gs 0 0 1 rg 50 50 100 100 re f";
        var multiply = States(("M", TestPdf.Dict(("BM", TestPdf.N("Multiply")))));
        using (var b = Render("1 1 0 rg 0 0 200 200 re f /Fm Do", WithForm(Form(content, Group(isolated: true), TestPdf.Dict(("ExtGState", multiply))))))
            Near(SKColors.Blue, At(b, 100, 100));
        using (var b = Render("1 1 0 rg 0 0 200 200 re f /Fm Do", WithForm(Form(content, Group(isolated: false), TestPdf.Dict(("ExtGState", multiply))))))
            Near(SKColors.Black, At(b, 100, 100));
    }

    /// <summary>Soft mask whose group /G draws the content <paramref name="maskContent"/>.</summary>
    private static Func<TestPdf, PdfDictionary> Masked(string maskContent, string type = "Luminosity", PdfArray? backdrop = null,
        PdfObject? transfer = null, PdfObject? groupSpace = null, Action<TestPdf, PdfDictionary>? extra = null, PdfArray? bbox = null) => pdf =>
    {
        var form = Form(maskContent, Group(isolated: true, colorSpace: groupSpace));
        if (bbox is not null) form.Dictionary.Set(TestPdf.N("BBox"), bbox);
        var g = pdf.Add(form);
        var mask = TestPdf.Dict(("Type", TestPdf.N("Mask")), ("S", TestPdf.N(type)), ("G", TestPdf.Ref(g)));
        if (backdrop is not null) mask.Set(TestPdf.N("BC"), backdrop);
        if (transfer is not null) mask.Set(TestPdf.N("TR"), transfer);
        var states = States(("SM", TestPdf.Dict(("SMask", mask))), ("NoSM", TestPdf.Dict(("SMask", TestPdf.N("None")))));
        var resources = TestPdf.Dict(("ExtGState", states));
        extra?.Invoke(pdf, resources);
        return resources;
    };

    [Fact]
    public void LuminosityMask_LetsThroughWhereTheMaskIsLight()
    {
        using var b = Render("/SM gs 1 0 0 rg 0 0 200 200 re f", Masked("1 g 0 0 100 200 re f"));
        Near(SKColors.Red, At(b, 50, 100));
        Near(SKColors.White, At(b, 150, 100));    // backdrop /BC black: nothing
    }

    [Fact]
    public void LuminosityMask_GrayGivesPartialAlpha()
    {
        using var b = Render("/SM gs 1 0 0 rg 0 0 200 200 re f", Masked("0.5 g 0 0 200 200 re f"));
        Near(new SKColor(255, 128, 128), At(b, 100, 100), 8);
    }

    [Fact]
    public void AlphaMask_UsesTheCoverageOfTheMask()
    {
        // Black but opaque: with Alpha only the coverage counts
        using var b = Render("/SM gs 1 0 0 rg 0 0 200 200 re f", Masked("0 g 0 0 100 200 re f", type: "Alpha"));
        Near(SKColors.Red, At(b, 50, 100));
        Near(SKColors.White, At(b, 150, 100));
    }

    [Fact]
    public void Backdrop_FillsWhereTheMaskHasNothing()
    {
        // /BC white in the colour space of the group: where the mask paints nothing it lets through; black blocks
        using var b = Render("/SM gs 1 0 0 rg 0 0 200 200 re f",
            Masked("0 0 0 rg 0 0 100 200 re f", backdrop: TestPdf.Box(1, 1, 1), groupSpace: TestPdf.N("DeviceRGB")));
        Near(SKColors.White, At(b, 50, 100));
        Near(SKColors.Red, At(b, 150, 100));
    }

    [Fact]
    public void Transfer_InvertsTheMask()
    {
        var invert = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(1)), ("C1", TestPdf.Box(0)),
            ("N", PdfInteger.Of(1)));
        using var b = Render("/SM gs 1 0 0 rg 0 0 200 200 re f", Masked("1 g 0 0 100 200 re f", transfer: invert));
        Near(SKColors.White, At(b, 50, 100));
        Near(SKColors.Red, At(b, 150, 100));
    }

    [Fact]
    public void EmptyMask_AlphaHidesAll_LuminosityIsIgnored()
    {
        // nonisolated_blend_smask: an empty alpha mask blocks everything (two of the reference readers); an empty luminosity mask is ignored
        // by the other readers (issue14200: otherwise a photo is missing)
        using (var b = Render("/SM gs 1 0 0 rg 0 0 200 200 re f", Masked("", type: "Alpha")))
            Near(SKColors.White, At(b, 100, 100));
        using (var b = Render("/SM gs 1 0 0 rg 0 0 200 200 re f", Masked("")))
            Near(SKColors.Red, At(b, 100, 100));
    }

    [Fact]
    public void AlphaMask_OutsideItsBBox_IgnoresTheTransfer()
    {
        // smask_alpha_oob_transfer: alpha mask with /TR 0 → 0.5 and a group only over 50…150; inside the BBox /TR applies,
        // outside it the mask stays 0 (not half covered there, which would make the whole page light red)
        var half = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(0.5)), ("C1", TestPdf.Box(1)),
            ("N", PdfInteger.Of(1)));
        using var b = Render("/SM gs 1 0 0 rg 0 0 200 200 re f", Masked("1 g 50 50 50 100 re f", type: "Alpha", transfer: half, bbox: TestPdf.Box(50, 50, 150, 150)));
        Near(SKColors.White, At(b, 20, 20));                          // outside the BBox: nothing
        Near(SKColors.Red, At(b, 75, 100));                           // painted: TR(1) = 1
        Near(new SKColor(255, 128, 128), At(b, 125, 100), 8);         // inside the BBox, unpainted: TR(0) = 0.5
    }

    [Fact]
    public void Mask_UsesTheCtmWhenItIsSet_AndEndsWithQ()
    {
        // Set under half the width: the mask (0…100) covers 0…50; after Q it is gone
        using var b = Render("q 0.5 0 0 1 0 0 cm /SM gs 2 0 0 1 0 0 cm 1 0 0 rg 0 100 200 100 re f Q 0 0 1 rg 0 0 200 100 re f",
            Masked("1 g 0 0 100 200 re f"));
        Near(SKColors.Red, At(b, 25, 150));
        Near(SKColors.White, At(b, 75, 150));
        Near(SKColors.Blue, At(b, 150, 50));
    }

    [Fact]
    public void SameMaskInManyRuns_IsRasterizedOnce_AlsoInAnOffsetTile()
    {
        // Three runs under the same mask (unmasked in between): the rasterised mask must sit correctly in every tile
        const string content = "/SM gs 1 0 0 rg 0 150 200 50 re f /NoSM gs 0 0 1 rg 0 0 10 10 re f "
            + "/SM gs 1 0 0 rg 0 100 200 50 re f /NoSM gs 0 0 1 rg 190 0 10 10 re f /SM gs 1 0 0 rg 0 50 200 50 re f";
        var (doc, list) = InterpreterTests.Page(content, Masked("1 g 0 0 100 200 re f"));
        using (doc)
        {
            Assert.Equal(3, list.Groups.Count(g => g.Mask >= 0));
            using var page = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
            Near(SKColors.Red, At(page, 50, 175));
            Near(SKColors.White, At(page, 150, 125));
            Near(SKColors.Red, At(page, 50, 75));
            // Tile at the top right, offset (device pixels 64…192 × 0…128): red on the left, white on the right
            using var bitmap = new SKBitmap(128, 128);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                SkiaRenderer.RenderTile(canvas, list, doc, PdfKnead.Rendering.PageGeometry.PageToDevice(doc.Pages[0], 1), SKRectI.Create(64, 0, 128, 128),
                    new ImageCache());
            }
            Near(SKColors.Red, bitmap.GetPixel(10, 30));     // page x 74, y 169
            Near(SKColors.White, bitmap.GetPixel(100, 30));  // page x 164
        }
    }

    [Fact]
    public void SameMaskAtTwoPlaces_SharesItsContent()
    {
        // The same mask under two CTMs: the content is built once, each placement has its own matrix
        var (doc, list) = InterpreterTests.Page(
            "q 0.5 0 0 1 0 0 cm /SM gs 2 0 0 1 0 0 cm 1 0 0 rg 0 100 200 100 re f Q q /SM gs 1 0 0 rg 0 0 200 100 re f Q",
            Masked("1 g 0 0 100 200 re f"));
        using (doc)
        {
            Assert.Equal(2, list.Masks.Length);
            Assert.Same(list.Masks[0].Content, list.Masks[1].Content);
            using var b = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
            Near(SKColors.Red, At(b, 25, 150));
            Near(SKColors.White, At(b, 75, 150));    // upper placement: mask half as wide
            Near(SKColors.Red, At(b, 75, 50));       // lower placement: full width 0…100
            Near(SKColors.White, At(b, 150, 50));
        }
    }

    [Fact]
    public void RotatedMask_KeepsTheShapeOfItsBBox()
    {
        // Mask set at 45°, its content fills everything, the /BBox [0 0 100 100] clips: a diamond, not an axis-aligned
        // bounding rectangle (issue16742)
        using var r = Render("q 0.7071 0.7071 -0.7071 0.7071 100 0 cm /SM gs 1 0 0 rg -500 -500 1000 1000 re f Q", pdf =>
        {
            var g = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 100, 100)),
                ("Group", Group(isolated: true))), Encoding.ASCII.GetBytes("1 g -500 -500 1000 1000 re f"));
            var mask = TestPdf.Dict(("S", TestPdf.N("Luminosity")), ("G", TestPdf.Ref(pdf.Add(g))));
            return TestPdf.Dict(("ExtGState", States(("SM", TestPdf.Dict(("SMask", mask))))));
        });
        Near(SKColors.Red, At(r, 100, 70));      // centre of the diamond (corners at (100,0), (171,71), (100,141), (29,71))
        Near(SKColors.White, At(r, 160, 10));    // inside the bounding rectangle, but outside the diamond
        Near(SKColors.White, At(r, 40, 130));
    }

    [Fact]
    public void Mask_None_EndsTheMask()
    {
        using var b = Render("/SM gs 1 0 0 rg 0 100 200 100 re f /NoSM gs 0 0 1 rg 0 0 200 100 re f", Masked("1 g 0 0 100 200 re f"));
        Near(SKColors.White, At(b, 150, 150));
        Near(SKColors.Blue, At(b, 150, 50));
    }

    [Fact]
    public void MaskedGroup_IsMaskedAsAWhole()
    {
        // Group with opacity 1 under a mask: the mask applies to it as well
        using var b = Render("/SM gs /Fm Do", Masked("1 g 0 0 100 200 re f",
            extra: (pdf, resources) => resources.Set(TestPdf.N("XObject"), TestPdf.Dict(("Fm", TestPdf.Ref(pdf.Add(Form(TwoRedSquares, Group(isolated: true)))))))));
        Near(SKColors.Red, At(b, 50, 50));
        Near(SKColors.White, At(b, 110, 110));
    }

    [Fact]
    public void OuterMask_StaysWhenTheGroupSwitchesItsOwnOff()
    {
        // Outer mask (left) at the Do of a plain group; /SMask /None inside: the outer mask applies to the group as a whole
        var none = TestPdf.Dict(("ExtGState", States(("NoSM", TestPdf.Dict(("SMask", TestPdf.N("None")))))));
        using var b = Render("/SM gs /Fm Do", Masked("1 g 0 0 100 200 re f", extra: (pdf, resources) =>
        {
            var form = Form("/NoSM gs 1 0 0 rg 0 0 200 200 re f", Group(), none);
            resources.Set(TestPdf.N("XObject"), TestPdf.Dict(("Fm", TestPdf.Ref(pdf.Add(form)))));
        }));
        Near(SKColors.Red, At(b, 50, 100));
        Near(SKColors.White, At(b, 150, 100));
    }

    [Fact]
    public void BlendModeUnderAMask_BlendsWithTheBackdrop()
    {
        // Yellow page, blue with Multiply above it under a mask (left): black on the left, yellow on the right (not blue on the left)
        using var b = Render("1 1 0 rg 0 0 200 200 re f /SMB gs 0 0 1 rg 0 0 200 200 re f", Masked("1 g 0 0 100 200 re f",
            extra: (pdf, resources) =>
            {
                var states = resources.GetDictionary(TestPdf.N("ExtGState"))!;
                var mask = states.GetDictionary(TestPdf.N("SM"))!.Get(TestPdf.N("SMask"));
                states.Set(TestPdf.N("SMB"), TestPdf.Dict(("SMask", mask), ("BM", TestPdf.N("Multiply"))));
            }));
        Near(SKColors.Black, At(b, 50, 100));
        Near(SKColors.Yellow, At(b, 150, 100));
    }

    [Fact]
    public void GroupWithZeroAlpha_PaintsNothing()
    {
        using var b = Render("/Z gs /Fm Do 0 0 1 rg 0 0 10 10 re f", pdf => TestPdf.Dict(
            ("ExtGState", States(("Z", TestPdf.Dict(("ca", new PdfReal(0)))))),
            ("XObject", TestPdf.Dict(("Fm", TestPdf.Ref(pdf.Add(Form(TwoRedSquares, Group(isolated: true)))))))));
        Assert.Equal(SKColors.White, At(b, 50, 50));
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void MaskUsingItself_Terminates()
    {
        var watch = Stopwatch.StartNew();
        using var b = Render("/SM gs 1 0 0 rg 0 0 200 200 re f", pdf =>
        {
            var gNumber = pdf.Add(new PdfDictionary());
            var mask = TestPdf.Dict(("S", TestPdf.N("Luminosity")), ("G", TestPdf.Ref(gNumber)));
            var states = States(("SM", TestPdf.Dict(("SMask", mask))));
            pdf.Replace(gNumber, Form("/SM gs 1 g 0 0 100 200 re f", Group(isolated: true), TestPdf.Dict(("ExtGState", states))));
            return TestPdf.Dict(("ExtGState", states));
        });
        Near(SKColors.Red, At(b, 50, 100));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10 * TestKind.Factor));
    }

    [Fact]
    public void ManyNestedGroups_StayBalanced()
    {
        // Form XObjects call each other nested as groups with opacity; every level must close
        using var b = Render("q /H gs /F0 Do Q 0 0 1 rg 0 0 10 10 re f", pdf =>
        {
            var half = States(("H", TestPdf.Dict(("ca", new PdfReal(0.9)))));
            var next = pdf.Add(Form("1 0 0 rg 0 0 200 200 re f", Group(isolated: true)));
            for (var i = 0; i < 20; i++)
                next = pdf.Add(Form("/H gs /Fn Do", Group(), TestPdf.Dict(("ExtGState", half), ("XObject", TestPdf.Dict(("Fn", TestPdf.Ref(next)))))));
            return TestPdf.Dict(("ExtGState", half), ("XObject", TestPdf.Dict(("F0", TestPdf.Ref(next)))));
        });
        Near(SKColors.Blue, At(b, 5, 5));   // after the groups opaque on the page again
        Assert.True(At(b, 100, 100).Green > 100);   // 0.9^21 opacity: pale red
    }

    /// <summary>
    /// /BM as an array: the first blend mode the reader knows counts — an unknown first name before Multiply gave Normal
    /// (docs/FEHLERGEDAECHTNIS.md, No. 268).
    /// </summary>
    [Fact]
    public void BlendModeArray_FirstKnownNameCounts()
    {
        Assert.Equal(1, Core.Pdf.Content.BlendModes.From(new PdfArray([TestPdf.N("NewerMode"), TestPdf.N("Multiply"), TestPdf.N("Screen")])));
        Assert.Equal(2, Core.Pdf.Content.BlendModes.From(new PdfArray([TestPdf.N("Screen")])));
        Assert.Equal(0, Core.Pdf.Content.BlendModes.From(new PdfArray([TestPdf.N("NewerMode")])));
        Assert.Equal(0, Core.Pdf.Content.BlendModes.From(new PdfArray([])));
        Assert.Equal(1, Core.Pdf.Content.BlendModes.From(TestPdf.N("Multiply")));
    }

    // ---- 7.2: a mask of the image's own replaces that of the graphics state; /Matte unblended (docs/FEHLERGEDAECHTNIS.md, No. 562)

    private static PdfStream Pixel(byte[] samples, string space = "DeviceRGB", params (string Key, PdfObject Value)[] more)
    {
        var dict = TestPdf.Dict([("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(1)), ("Height", PdfInteger.Of(1)),
            ("BitsPerComponent", PdfInteger.Of(8)), .. more]);
        if (space.Length > 0) dict.Set(TestPdf.N("ColorSpace"), TestPdf.N(space));
        return new PdfStream(dict, samples);
    }

    private static PdfStream GrayMask(byte value, params (string Key, PdfObject Value)[] more) => Pixel([value], "DeviceGray", more);

    /// <summary>A soft mask of the state that lets nothing through, and the image /Im (made by <paramref name="image"/>) in the resources.</summary>
    private static Func<TestPdf, PdfDictionary> BlockedWithImage(Func<TestPdf, PdfStream> image) => pdf =>
    {
        var resources = Masked("0 g 0 0 200 200 re f")(pdf);
        resources.Set(TestPdf.N("XObject"), TestPdf.Dict(("Im", TestPdf.Ref(pdf.Add(image(pdf))))));
        return resources;
    };

    private const string DrawImage = "/SM gs q 200 0 0 200 0 0 cm /Im Do Q";

    /// <summary>An image with a soft mask of its own is drawn without the mask of the state (ISO 32000-2, 11.6.4.3) — red, not hidden. Before: white.</summary>
    [Fact]
    public void ImageWithOwnSoftMask_IgnoresTheMaskOfTheState()
    {
        using var b = Render(DrawImage, BlockedWithImage(pdf => Pixel([255, 0, 0], "DeviceRGB", ("SMask", TestPdf.Ref(pdf.Add(GrayMask(255)))))));
        Near(SKColors.Red, At(b, 100, 100));
    }

    /// <summary>A colour key mask counts as the image's own mask as well. Before: white.</summary>
    [Fact]
    public void ImageWithColourKeyMask_IgnoresTheMaskOfTheState()
    {
        using var b = Render(DrawImage, BlockedWithImage(_ => Pixel([255, 0, 0], "DeviceRGB",
            ("Mask", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0)])))));
        Near(SKColors.Red, At(b, 100, 100));
    }

    /// <summary>A stencil mask paints in the fill colour and stays under the mask of the state (guard, green before).</summary>
    [Fact]
    public void StencilMask_StaysUnderTheMaskOfTheState()
    {
        using var b = Render("/SM gs 1 0 0 rg q 200 0 0 200 0 0 cm /Im Do Q", BlockedWithImage(_ => new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")),
            ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(1)), ("Height", PdfInteger.Of(1)), ("ImageMask", PdfBoolean.True)), [0x00])));
        Near(SKColors.White, At(b, 100, 100));
    }

    private static Func<TestPdf, PdfDictionary> WithImage(Func<TestPdf, PdfStream> image) => pdf =>
        TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(pdf.Add(image(pdf)))))));

    /// <summary>
    /// /Matte [0 0 0]: the colour (128, 0, 0) is premultiplied with black at α = 128/255 — unblended it is red, over white about
    /// (255, 128, 128). Before: about (191, 128, 128), a dark seam (ISO 32000-2, 11.6.5.2).
    /// </summary>
    [Fact]
    public void SoftMaskWithMatte_UndoesThePremultipliedColour()
    {
        using var b = Render("q 200 0 0 200 0 0 cm /Im Do Q", WithImage(pdf => Pixel([128, 0, 0], "DeviceRGB",
            ("SMask", TestPdf.Ref(pdf.Add(GrayMask(128, ("Matte", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0)])))))))));
        Near(new SKColor(255, 128, 128), At(b, 100, 100), 8);
    }

    /// <summary>An indexed image: its palette colours are premultiplied, /Matte counts the components of the base — unblended the same way.</summary>
    [Fact]
    public void IndexedImageWithMatte_PaletteIsUnblended()
    {
        var indexed = new PdfArray([TestPdf.N("Indexed"), TestPdf.N("DeviceRGB"), PdfInteger.Of(0), new PdfString([128, 0, 0])]);
        using var b = Render("q 200 0 0 200 0 0 cm /Im Do Q", WithImage(pdf =>
        {
            var image = Pixel([0], "", ("ColorSpace", indexed),
                ("SMask", TestPdf.Ref(pdf.Add(GrayMask(128, ("Matte", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(0)])))))));
            return image;
        }));
        Near(new SKColor(255, 128, 128), At(b, 100, 100), 8);
    }

    /// <summary>/Matte with the wrong number of components is ignored (the colours stay as stored).</summary>
    [Fact]
    public void SoftMaskWithInvalidMatte_ColoursAsStored()
    {
        using var b = Render("q 200 0 0 200 0 0 cm /Im Do Q", WithImage(pdf => Pixel([128, 0, 0], "DeviceRGB",
            ("SMask", TestPdf.Ref(pdf.Add(GrayMask(128, ("Matte", new PdfArray([PdfInteger.Of(0)])))))))));
        Near(new SKColor(191, 128, 128), At(b, 100, 100), 8);
    }

    // ---- 7.4 E-06: a non-isolated group blends inside with the backdrop (ISO 32000-2, 11.4.4; docs/FEHLERGEDAECHTNIS.md, No. 567)

    private const string BlackLeftWhiteRight = "0 g 0 0 100 200 re f 1 g 100 0 100 200 re f";

    private static PdfDictionary HalfAndDifference() =>
        States(("A", TestPdf.Dict(("ca", new PdfReal(0.5)))), ("D", TestPdf.Dict(("BM", TestPdf.N("Difference")))));

    /// <summary>
    /// Left black, right white; a non-isolated group at opacity 0.5 draws white with Difference over all: inside it mixes with the backdrop
    /// (left white, right black), put back at 0.5 — both halves about grey 128. Before: the group started empty, Difference against nothing
    /// gave white, the right half stayed white.
    /// </summary>
    [Fact]
    public void NonIsolatedGroupWithAlpha_BlendsInsideWithTheBackdrop()
    {
        using var b = Render(BlackLeftWhiteRight + " /A gs /Fm Do", WithForm(Form("/D gs 1 g 0 0 200 200 re f", Group()), HalfAndDifference()));
        Near(new SKColor(128, 128, 128), At(b, 50, 100), 8);
        Near(new SKColor(128, 128, 128), At(b, 150, 100), 8);
    }

    /// <summary>The same with a soft mask that lets everything through instead of the opacity: the Difference over the backdrop, left white, right black.</summary>
    [Fact]
    public void NonIsolatedGroupWithMask_BlendsInsideWithTheBackdrop()
    {
        using var b = Render(BlackLeftWhiteRight + " /SM gs /Fm Do", Masked("1 g 0 0 200 200 re f", extra: (pdf, resources) =>
        {
            var inner = TestPdf.Dict(("ExtGState", HalfAndDifference()));
            var form = pdf.Add(Form("/D gs 1 g 0 0 200 200 re f", Group(), inner));
            resources.Set(TestPdf.N("XObject"), TestPdf.Dict(("Fm", TestPdf.Ref(form))));
        }));
        Near(SKColors.White, At(b, 50, 100), 8);
        Near(SKColors.Black, At(b, 150, 100), 8);
    }

    /// <summary>An isolated group blends only within itself (guard): Difference against its empty start gives white over both halves at 0.5.</summary>
    [Fact]
    public void IsolatedGroupWithAlpha_BlendsOnlyWithinItself()
    {
        using var b = Render(BlackLeftWhiteRight + " /A gs /Fm Do", WithForm(Form("/D gs 1 g 0 0 200 200 re f", Group(isolated: true)), HalfAndDifference()));
        Near(new SKColor(128, 128, 128), At(b, 50, 100), 8);
        Near(SKColors.White, At(b, 150, 100), 8);
    }

    // ---- 7.4 E-06 step 5: the ICC colour space of an isolated group redefines its device colours (ISO 32000-2, 11.7.2;
    // docs/FEHLERGEDAECHTNIS.md, No. 568)

    /// <summary>
    /// A half-transparent red area under a non-isolated group with two small multiplied squares (docs/FEHLERGEDAECHTNIS.md, No. 591): between
    /// them, inside the bounds of the group, the red looks as outside it. Before, the group's layer — started with the backdrop — went back
    /// over it and counted the half-transparent red twice: 75 % instead of 50 % opaque within the group's bounds.
    /// </summary>
    [Theory]
    [InlineData(255)]
    [InlineData(128)]
    public void NonIsolatedBlendGroup_OverAHalfTransparentBackdrop_LeavesItAsItIs(int groupAlpha)
    {
        using var b = Render("/H gs 1 0 0 rg 0 0 200 200 re f /G gs /Fm Do", pdf => TestPdf.Dict(
            ("ExtGState", TestPdf.Dict(("H", TestPdf.Dict(("ca", new PdfReal(0.5)))), ("G", TestPdf.Dict(("ca", new PdfReal(groupAlpha / 255.0)))))),
            ("XObject", TestPdf.Dict(("Fm", TestPdf.Ref(pdf.Add(Form("/M gs 0 0 1 rg 100 100 10 10 re f 180 180 10 10 re f", Group(),
                TestPdf.Dict(("ExtGState", TestPdf.Dict(("M", TestPdf.Dict(("BM", TestPdf.N("Multiply")))))))))))))));
        var outside = At(b, 50, 50);
        var between = At(b, 150, 150);
        Assert.True(Math.Abs(outside.Green - between.Green) <= 2 && Math.Abs(outside.Red - between.Red) <= 2, $"außen {outside}, zwischen {between}");
        Assert.True(At(b, 105, 105).Green < outside.Green - 20, $"multipliziert: {At(b, 105, 105)}");
    }

    /// <summary>A CMYK profile of the plain formula: 100 % magenta is pure magenta (the default profile gives a bluish red, blue about 140).</summary>
    private static readonly Lazy<byte[]> PlainCmyk = new(() =>
        Core.Pdf.Graphics.Icc.CmykProfile.Bake((c, m, y, k) => ((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k))));

    private static PdfArray Icc(TestPdf pdf, byte[] profile) =>
        new([TestPdf.N("ICCBased"), TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), profile)))]);

    /// <summary>Resources with the form /Fm: content, group with the plain CMYK profile as /CS, and the form's own resources.</summary>
    private static Func<TestPdf, PdfDictionary> PlainCmykForm(string content, bool isolated, Func<TestPdf, PdfDictionary>? resources = null) => pdf =>
        TestPdf.Dict(("XObject", TestPdf.Dict(("Fm", TestPdf.Ref(pdf.Add(Form(content,
            Group(isolated: isolated, colorSpace: Icc(pdf, PlainCmyk.Value)), resources?.Invoke(pdf))))))));

    private static bool IsPlainMagenta(SKColor c) => c is { Red: > 245, Green: < 10, Blue: > 245 };

    /// <summary>Isolated group with an ICC CMYK /CS: DeviceCMYK inside (k, and /CS0 naming /DeviceCMYK) is read in its profile.</summary>
    [Fact]
    public void GroupWithIccCmyk_DeviceCmykUsesTheGroupProfile()
    {
        using var b = Render("/Fm Do 0 1 0 0 k 0 150 200 50 re f", PlainCmykForm("0 1 0 0 k 0 0 100 150 re f /CS0 cs 0 1 0 0 sc 100 0 100 150 re f",
            isolated: true, _ => TestPdf.Dict(("ColorSpace", TestPdf.Dict(("CS0", TestPdf.N("DeviceCMYK")))))));
        Assert.True(IsPlainMagenta(At(b, 50, 75)), At(b, 50, 75).ToString());
        Assert.True(IsPlainMagenta(At(b, 150, 75)), At(b, 150, 75).ToString());
        // after the group the device space applies again
        Assert.False(IsPlainMagenta(At(b, 50, 175)), At(b, 50, 175).ToString());
    }

    /// <summary>
    /// A page group with an ICC /CS and an annotation in DeviceCMYK (docs/FEHLERGEDAECHTNIS.md, No. 724): built on its own — as the overlay of own
    /// elements and the background lists of foreign annotations do — the annotation reads its device colours in the page group's profile, as
    /// when the whole page is drawn. Before, the page drew it in the profile, the overlay without: a signature looked different before and after
    /// saving (OFFENE-PUNKTE 8i, `signiertest` on a page with a transparency group).
    /// </summary>
    [Fact]
    public void PageGroupWithIcc_AnnotationBuiltAlone_LikeOnThePage()
    {
        var pdf = TestPdf.Simple(1, null);
        var appearance = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 100, 100))),
            "0 1 0 0 k 0 0 100 100 re f"u8.ToArray()));
        var annot = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Square")), ("Rect", TestPdf.Box(0, 0, 100, 100)),
            ("AP", TestPdf.Dict(("N", TestPdf.Ref(appearance))))));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Get("Group"), TestPdf.Dict(("S", TestPdf.N("Transparency")), ("CS", Icc(pdf, PlainCmyk.Value))));
        page.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(annot)]));
        using var doc = Core.Pdf.Document.PdfDocument.Open(pdf.Build());
        using (var whole = SkiaRenderer.RenderPage(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 1, new ImageCache()))
            Assert.True(IsPlainMagenta(At(whole, 50, 50)), At(whole, 50, 50).ToString());
        var alone = Core.Pdf.Content.PageInterpreter.BuildAnnotations(doc, doc.Pages[0], [(PdfDictionary)doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!]);
        using var drawn = SkiaRenderer.RenderPage(alone, doc, doc.Pages[0], 1, new ImageCache());
        Assert.True(IsPlainMagenta(At(drawn, 50, 50)), At(drawn, 50, 50).ToString());
    }

    /// <summary>
    /// An isolated group with /CS /DeviceCMYK inside one with the plain ICC profile: a device space as /CS takes that of the nearest group
    /// around it (ISO 32000-2, 11.7.2) — magenta inside stays the plain magenta (docs/FEHLERGEDAECHTNIS.md, No. 594). Before, the inner
    /// group ended the redefinition and the default CMYK profile gave a bluish red.
    /// </summary>
    [Fact]
    public void InnerGroupWithDeviceCmyk_KeepsTheProfileOfTheGroupAround()
    {
        using var b = Render("/Fm Do", PlainCmykForm("/In Do", isolated: true, pdf => TestPdf.Dict(("XObject", TestPdf.Dict(("In", TestPdf.Ref(pdf.Add(
            Form("0 1 0 0 k 0 0 200 200 re f", Group(isolated: true, colorSpace: TestPdf.N("DeviceCMYK")))))))))));
        Assert.True(IsPlainMagenta(At(b, 100, 100)), At(b, 100, 100).ToString());
    }

    /// <summary>Guards: not in a non-isolated group, and /DefaultCMYK of the resources goes first.</summary>
    [Fact]
    public void GroupWithIccCmyk_NonIsolatedOrDefaultCmyk_KeepsTheirSpace()
    {
        using var b = Render("/Fm Do", PlainCmykForm("0 1 0 0 k 0 0 200 200 re f", isolated: false));
        Assert.False(IsPlainMagenta(At(b, 100, 100)), At(b, 100, 100).ToString());
        using var c = Render("/Fm Do", PlainCmykForm("0 1 0 0 k 0 0 200 200 re f", isolated: true,
            pdf => TestPdf.Dict(("ColorSpace", TestPdf.Dict(("DefaultCMYK", Icc(pdf, Core.Pdf.Graphics.Icc.CmykProfile.Bytes())))))));
        Assert.False(IsPlainMagenta(At(c, 100, 100)), At(c, 100, 100).ToString());
    }
}
