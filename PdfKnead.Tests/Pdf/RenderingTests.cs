using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Pixel tests of the Skia renderer: page 200 × 200 pt, so 200 × 200 pixels at 72 dpi.</summary>
public sealed class RenderingTests
{
    private static SKBitmap Render(string content, Func<TestPdf, PdfDictionary>? resources = null, double pixelsPerPoint = 1)
    {
        var (doc, list) = InterpreterTests.Page(content, resources);
        using (doc) return SkiaRenderer.RenderPage(list, doc, doc.Pages[0], pixelsPerPoint, new ImageCache());
    }

    /// <summary>Returns the pixel at PDF coordinates (y upwards).</summary>
    private static SKColor At(SKBitmap b, int x, int pdfY) => b.GetPixel(x, b.Height - 1 - pdfY);

    [Fact]
    public void Fill_IsInTheRightPlace_WithYUp()
    {
        using var b = Render("1 0 0 rg 10 10 50 30 re f");
        Assert.Equal(SKColors.Red, At(b, 30, 20));
        Assert.Equal(SKColors.White, At(b, 30, 100));   // nothing above
        Assert.Equal(SKColors.White, At(b, 100, 20));   // nothing to the right
    }

    [Fact]
    public void Hairline_IsVisible_AtLowZoom()
    {
        // A 0.1 pt wide line at 1 pixel per point must still be visible
        using var b = Render("0.1 w 0 0 1 RG 0 100.5 m 200 100.5 l S");
        var c = At(b, 100, 100);
        Assert.True(c.Blue > 128 && c.Red < 128, $"Haarlinie nicht sichtbar: {c}");
    }

    [Fact]
    public void ZeroHeightFill_IsAHairline()
    {
        // issue4260: grid made of "x y w 0 re f"; common readers show the line
        using var b = Render("0 0 1 rg 10 100.5 180 0 re f");
        var c = At(b, 100, 100);
        Assert.True(c.Blue > 128 && c.Red < 128, $"Linie nicht sichtbar: {c}");
        // Real but narrow areas get no outline (otherwise small text and hatching would become bolder)
        using var thin = Render("0 0 1 rg 10 100.25 180 0.2 re f");
        Assert.True(At(thin, 100, 100).Red > 100);
    }

    [Theory]
    [InlineData(333, true)]
    [InlineData(100, false)]
    public void VeryAcuteMiter_IsDrawnUpToTheLimit(int miterLimit, bool tipExpected)
    {
        // Test file LargeMitreLimit: a corner with a ratio of ~160 (up, back with an offset of 0.625). Skia omits
        // mitres above ~90; here the tip reaches up to y ≈ 140. Below the limit of 100 the corner is bevelled: no tip
        using var b = Render($"1 w 0 j {miterLimit} M 1 0 0 RG 100 10 m 100 60 l 100.625 10 l S", pixelsPerPoint: 4);
        int Red(int pdfY) => Enumerable.Range(380, 40).Count(x => b.GetPixel(x, b.Height - 1 - pdfY * 4) is var c && c.Red > 200 && c.Green < 200);
        Assert.Equal(tipExpected, Red(100) > 0);
        Assert.Equal(0, Red(150));   // nothing above the tip
        Assert.True(Red(40) > 0);    // the stroke itself
        // The bounds of the entry (tiles, spatial index) must enclose the tip; capping them at 10 times would cut it off
        var (doc, list) = InterpreterTests.Page($"1 w 0 j {miterLimit} M 100 10 m 100 60 l 100.625 10 l S");
        using (doc)
        {
            var item = list.Items.Span[0];
            Assert.Equal(tipExpected, Math.Max(item.Top, item.Bottom) > 135);
        }
    }

    [Fact]
    public void BlackAndWhiteImage_ReducedWithoutMoire()
    {
        // issue1810: a screened 1-bit scan strongly reduced. Skia's mipmaps give a checkerboard pattern; averaged by area
        // the area stays evenly grey (as in one of the reference readers)
        const int side = 750, rowBytes = (side + 7) / 8;   // to 180 pixels: factor 4.17 as for issue1810 at 72 dpi
        var bits = new byte[rowBytes * side];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
            if (x % 4 != 0 && y % 4 != 0) bits[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));   // grid with period 4, cells white
        var image = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(side)),
            ("Height", PdfInteger.Of(side)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(1)));
        using var b = Render("q 180 0 0 180 10 10 cm /Im Do Q", pdf =>
            TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(pdf.Add(TestPdf.FlateStream(bits, image))))))));
        var values = new List<int>();
        for (var y = 40; y < 160; y++)
        for (var x = 40; x < 160; x++)
            values.Add(At(b, x, y).Red);
        var mean = values.Average();
        var spread = Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
        Assert.InRange(mean, 125, 160);   // (3/4)² white
        Assert.True(spread < 6, $"Schwankung {spread:0.0} (Moiré)");   // mipmaps: 17.4; averaged by area: 4.4
    }

    [Fact]
    public void ImageReducedUnevenly_EdgeStaysSharp()
    {
        // large_jpeg_downscale: a strip reduced 40 times across and 4 times down. Skia picks the mipmap level by the stronger reduction, and
        // a sharp edge across the strip came out as a ramp of several pixels; evened by area first it stays sharp (as in the reference readers)
        const int width = 4000, height = 512;
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            pixels[(y * width + x) * 3] = (byte)(y < height / 2 ? 90 : 254);
        var image = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(width)),
            ("Height", PdfInteger.Of(height)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)));
        using var b = Render("q 100 0 0 128 10 10 cm /Im Do Q", pdf =>
            TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(pdf.Add(TestPdf.FlateStream(pixels, image))))))));
        // the edge lies at y = 74; between dark and bright at most two pixels
        var between = Enumerable.Range(40, 70).Count(y => At(b, 60, y).Red is > 110 and < 230);
        Assert.True(between <= 2, $"{between} Pixel im Übergang");
    }

    [Theory]
    [InlineData("[0 0 0 0]", false)]
    [InlineData("[0 0 200 200]", true)]
    [InlineData("[0 0 x 200]", true)]   // unreadable: no bounds
    public void FormWithEmptyBBox_IsInvisible(string bbox, bool visible)
    {
        // issue6413: a BBox without area clips everything away, as in the reference readers; treating it as
        // "no bounds" would let the form cover content
        using var b = Render("/Fm Do", pdf =>
        {
            var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")));
            var box = bbox.Trim('[', ']').Split(' ');
            dict.Set(TestPdf.N("BBox"), new PdfArray(box.Select(v => double.TryParse(v, out var d) ? (PdfObject)new PdfReal(d) : TestPdf.N(v))));
            var form = pdf.Add(new PdfStream(dict, "1 0 0 rg 50 50 100 100 re f"u8.ToArray()));
            return TestPdf.Dict(("XObject", TestPdf.Dict(("Fm", TestPdf.Ref(form)))));
        });
        Assert.Equal(visible ? SKColors.Red : SKColors.White, At(b, 100, 100));
    }

    [Fact]
    public void Clipping_LimitsPainting()
    {
        using var b = Render("q 0 0 100 200 re W n 0 1 0 rg 0 0 200 200 re f Q");
        Assert.Equal(SKColors.Lime, At(b, 50, 50));
        Assert.Equal(SKColors.White, At(b, 150, 50));
    }

    /// <summary>
    /// The same rectangle clipped twice (a block inside a form clipping to the form's box, as producers do) draws its edge as once: each side
    /// is applied only where it cuts inside the clip around it. Applied again, the antialiased edge covered the pixels along it twice — a
    /// black area on a half pixel became lighter (docs/FEHLERGEDAECHTNIS.md, No. 248). A side that cuts further in still cuts.
    /// </summary>
    [Fact]
    public void SameRectangleClippedTwice_EdgeAsOnce()
    {
        using var once = Render("q 20.5 20.5 100 100 re W n 0 g 0 0 200 200 re f Q");
        using var twice = Render("q 20.5 20.5 100 100 re W n q 20.5 20.5 100 100 re W n 0 g 0 0 200 200 re f Q Q");
        using var further = Render("q 20.5 20.5 100 100 re W n q 20.5 20.5 60.5 100 re W n 0 g 0 0 200 200 re f Q Q");
        foreach (var (x, y) in new[] { (20, 60), (120, 60), (60, 20), (60, 120) })
            Assert.Equal(At(once, x, y), At(twice, x, y));
        Assert.Equal(At(once, 20, 60), At(further, 20, 60));      // the shared left edge as once
        Assert.Equal(SKColors.White, At(further, 100, 60));         // the inner clip still cuts on the right
        Assert.NotEqual(SKColors.White, At(once, 20, 60));          // the half pixel is grey, not white (the test would prove nothing)
    }

    [Fact]
    public void Clips_AreShared_AndRectanglesDetected()
    {
        // Like office-suite producers: the same rectangle as a clip around every object, plus an oblique clip
        var (doc, list) = InterpreterTests.Page(
            "q 0 0 100 200 re W n 0 1 0 rg 10 10 20 20 re f Q q 0 0 100 200 re W n 0 1 0 rg 50 10 20 20 re f Q " +
            "q 0 100 m 100 200 l 0 200 l h W n 1 0 0 rg 0 0 200 200 re f Q");
        using (doc)
        {
            Assert.Equal(3, list.Clips.Length);                 // root, the shared rectangle, the triangle
            Assert.True(list.Clips[1].IsRectangle);
            Assert.False(list.Clips[2].IsRectangle);
            using var b = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
            Assert.Equal(SKColors.Lime, At(b, 60, 20));
            Assert.Equal(SKColors.Red, At(b, 20, 150));         // inside the triangle
            Assert.Equal(SKColors.White, At(b, 90, 120));       // outside the triangle
        }
    }

    [Fact]
    public void Transparency_Blends()
    {
        using var b = Render("/GS1 gs 1 0 0 rg 0 0 200 200 re f",
            _ => TestPdf.Dict(("ExtGState", TestPdf.Dict(("GS1", TestPdf.Dict(("ca", new PdfReal(0.5))))))));
        var c = At(b, 100, 100);
        Assert.InRange(c.Green, 120, 135);   // half red on white = pink
        Assert.Equal(255, c.Red);
    }

    [Fact]
    public void Image_IsDrawnUprightAndScaled()
    {
        // 1 × 2 pixels: red at the top, blue at the bottom, stretched over the whole page
        using var b = Render("q 200 0 0 200 0 0 cm /Im Do Q",
            pdf =>
            {
                var n = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
                    ("Width", PdfInteger.Of(1)), ("Height", PdfInteger.Of(2)), ("BitsPerComponent", PdfInteger.Of(8)),
                    ("ColorSpace", TestPdf.N("DeviceRGB"))), [255, 0, 0, 0, 0, 255]));
                return TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(n)))));
            });
        Assert.Equal(SKColors.Red, At(b, 100, 190));
        Assert.Equal(SKColors.Blue, At(b, 100, 10));
    }

    [Fact]
    public void RotatedPage_IsTurnedClockwise()
    {
        // Page with /Rotate 90: bottom left (PDF) appears at the top left
        var pdf = TestPdf.Simple(pages: 2);
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[1];   // 792 × 612, Rotate 90
        Assert.Equal(90, page.Rotate);
        var m = PageGeometry.PageToDevice(page, 1);
        var (x, y) = m.Transform(0, 0);
        Assert.Equal((0.0, 0.0), (Math.Round(x, 3), Math.Round(y, 3)));
        var (x2, y2) = m.Transform(792, 0);   // bottom right → bottom left
        Assert.Equal((0.0, 792.0), (Math.Round(x2, 3), Math.Round(y2, 3)));
    }

    [Fact]
    public void Tile_MatchesTheWholePage()
    {
        var (doc, list) = InterpreterTests.Page("1 0 0 rg 0 0 100 100 re f 0 0 1 rg 100 100 100 100 re f");
        using (doc)
        {
            var page = doc.Pages[0];
            var pageToDevice = PageGeometry.PageToDevice(page, 1);
            using var tile = new SKBitmap(64, 64);
            using (var canvas = new SKCanvas(tile))
            {
                canvas.Clear(SkiaRenderer.Paper);
                // Tile at the top right (device): x 128..192, y 0..64 → blue
                SkiaRenderer.RenderTile(canvas, list, doc, pageToDevice, new SKRectI(128, 0, 192, 64), new ImageCache());
            }
            Assert.Equal(SKColors.Blue, tile.GetPixel(32, 32));
        }
    }

    // ---- 7.4 E-30: annotations with NoZoom keep their screen size (ISO 32000-2, 12.5.3, Table 167; docs/FEHLERGEDAECHTNIS.md, No. 576)

    /// <summary>
    /// A page without content and two annotations, each a black 20 × 20 square as its appearance: the first with NoZoom (/F 8, or 12 with
    /// Print), /Rect [50 130 70 150], the second without, /Rect [100 20 120 40].
    /// </summary>
    private static Core.Pdf.Document.PdfDocument TwoSquares(int flags)
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream([]));
        PdfObject Square(int x, int y, int f)
        {
            var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 20, 20))),
                "0 g 0 0 20 20 re f"u8.ToArray()));
            return TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Square")), ("Rect", TestPdf.Box(x, y, x + 20, y + 20)),
                ("F", PdfInteger.Of(f)), ("AP", TestPdf.Dict(("N", TestPdf.Ref(form)))))));
        }
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([Square(50, 130, flags), Square(100, 20, 4)]));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.MediaBox, TestPdf.Box(0, 0, 200, 200));
        return Core.Pdf.Document.PdfDocument.Open(pdf.Build());
    }

    private static int Dark(SKBitmap b, int left, int top, int right, int bottom)
    {
        var n = 0;
        for (var y = Math.Max(0, top); y < Math.Min(b.Height, bottom); y++)
        for (var x = Math.Max(0, left); x < Math.Min(b.Width, right); x++)
            if (b.GetPixel(x, y).Red < 128) n++;
        return n;
    }

    [Fact]
    public void NoZoomAnnotation_KeepsItsScreenSize()
    {
        using var doc = TwoSquares(8);
        var page = doc.Pages[0];
        var list = Core.Pdf.Content.PageInterpreter.Build(doc, page);
        Assert.Single(list.NoZoom);
        var top = page.MediaBox.Top;
        using var at100 = SkiaRenderer.RenderPage(list, doc, page, 1, new ImageCache(), noZoomScale: DocumentRenderer.NoZoomScale(1, 1));
        Assert.Equal(400, Dark(at100, 50, (int)(top - 150), 70, (int)(top - 130)));
        // 400 %: the NoZoom square stays 20 × 20 pixels at the top-left corner of its /Rect, the other grows to 80 × 80
        using var at400 = SkiaRenderer.RenderPage(list, doc, page, 4, new ImageCache(), noZoomScale: DocumentRenderer.NoZoomScale(1, 4));
        var anchorX = 50 * 4;
        var anchorY = (int)((top - 150) * 4);
        Assert.Equal(400, Dark(at400, anchorX, anchorY, anchorX + 20, anchorY + 20));
        Assert.Equal(400, Dark(at400, anchorX - 10, anchorY - 10, anchorX + 90, anchorY + 90));   // nothing beyond 20 × 20
        Assert.Equal(6400, Dark(at400, 400, (int)((top - 40) * 4), 480, (int)((top - 20) * 4)));
    }

    /// <summary>
    /// A NoZoom annotation 100 × 100 whose appearance has a square only at its lower right (docs/FEHLERGEDAECHTNIS.md, No. 589): at 400 % it is
    /// drawn shrunk about its upper left corner, so the square lies in a tile far from where it stands. That tile draws it; before, only the
    /// tile where it stands looked at it (and drew it outside its own area) — the square was missing.
    /// </summary>
    [Fact]
    public void NoZoomAnnotation_DrawnInTheTileWhereItIsShown()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream([]));
        var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 100, 100))),
            "0 g 80 0 20 20 re f"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")),
            ("Subtype", TestPdf.N("Square")), ("Rect", TestPdf.Box(50, 50, 150, 150)), ("F", PdfInteger.Of(8)), ("AP", TestPdf.Dict(("N", TestPdf.Ref(form)))))))]));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.MediaBox, TestPdf.Box(0, 0, 200, 200));
        using var doc = Core.Pdf.Document.PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var list = Core.Pdf.Content.PageInterpreter.Build(doc, page);
        var pageToDevice = PageGeometry.PageToDevice(page, 4);
        // shrunk by 1/4 about (50, 150): the square at page x 70–75, y 125–130 → device 280–300 both ways
        using var tile = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(tile))
        {
            canvas.Clear(SkiaRenderer.Paper);
            SkiaRenderer.RenderTile(canvas, list, doc, pageToDevice, new SKRectI(256, 256, 320, 320), new ImageCache(), default, noZoomScale: 0.25);
        }
        Assert.Equal(400, Dark(tile, 24, 24, 44, 44));
        // where it stands (device 520–600) nothing is drawn
        using var there = new SKBitmap(128, 128);
        using (var canvas = new SKCanvas(there))
        {
            canvas.Clear(SkiaRenderer.Paper);
            SkiaRenderer.RenderTile(canvas, list, doc, pageToDevice, new SKRectI(500, 500, 628, 628), new ImageCache(), default, noZoomScale: 0.25);
        }
        Assert.Equal(0, Dark(there, 0, 0, 128, 128));
    }

    /// <summary>
    /// The view measures NoZoom with the scale of the tile level its zoom is drawn at (No. 589): at 100 % on a 96-dpi screen the tiles are drawn
    /// at level 1 (1.414 px/pt for 1.333), so annotations come out 0.943 — and the view takes the same, not 1.
    /// </summary>
    [Fact]
    public void NoZoomScale_OfTheTileLevel()
    {
        var at100 = 96.0 / 72;
        Assert.Equal(DocumentRenderer.NoZoomScale(at100, DocumentRenderer.Scale(DocumentRenderer.LevelFor(at100))), DocumentRenderer.NoZoomScaleAt(at100, at100));
        Assert.Equal(at100 / Math.Sqrt(2), DocumentRenderer.NoZoomScaleAt(at100, at100), 9);
        Assert.Equal(1, DocumentRenderer.NoZoomScaleAt(at100, at100 * 0.6));            // level below 100 %: unchanged
        Assert.Equal(at100 / 4, DocumentRenderer.NoZoomScaleAt(at100, 4 * 0.999), 9);   // just below a level: that level
    }

    /// <summary>A closed text note behaves as if NoZoom were set (12.5.6.4), also without the flag; a square without the flag does not.</summary>
    [Fact]
    public void TextNote_ImplicitlyNoZoom()
    {
        var pdf = TestPdf.Simple(1);
        var annots = new PdfArray();
        foreach (var subtype in new[] { "Text", "Square" })
        {
            var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 20, 20))),
                "0 g 0 0 20 20 re f"u8.ToArray()));
            annots.Add(TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N(subtype)), ("Rect", TestPdf.Box(50, 130, 70, 150)),
                ("AP", TestPdf.Dict(("N", TestPdf.Ref(form))))))));
        }
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), annots);
        using var doc = Core.Pdf.Document.PdfDocument.Open(pdf.Build());
        var list = Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]);
        var range = Assert.Single(list.NoZoom);
        Assert.Equal(1, range.End - range.Start);           // the square of the text note only
        Assert.Equal(range.End + 1, list.Items.Length);      // the square without the flag follows outside
    }

    /// <summary>Printing (and anything without a screen zoom) uses /Rect: the list for printing has no NoZoom ranges, the square grows.</summary>
    [Fact]
    public void NoZoomAnnotation_PrintUsesRect()
    {
        using var doc = TwoSquares(8 | 4);
        var page = doc.Pages[0];
        var printed = Core.Pdf.Content.PageInterpreter.Build(doc, page, forPrint: true);
        Assert.Empty(printed.NoZoom);
        using var b = SkiaRenderer.RenderPage(printed, doc, page, 4, new ImageCache(), noZoomScale: 0.25);
        var top = page.MediaBox.Top;
        Assert.Equal(6400, Dark(b, 200, (int)((top - 150) * 4), 280, (int)((top - 130) * 4)));
        // without a zoom for NoZoom (export, thumbnails) the same
        using var exported = SkiaRenderer.RenderPage(Core.Pdf.Content.PageInterpreter.Build(doc, page), doc, page, 4, new ImageCache());
        Assert.Equal(6400, Dark(exported, 200, (int)((top - 150) * 4), 280, (int)((top - 130) * 4)));
    }
}

public sealed class ThinLineTests
{
    [Fact]
    public void EnhanceThinLines_MakesHairlinesDarker()
    {
        var (doc, list) = InterpreterTests.Page("0.05 w 0 G 0 100.5 m 200 100.5 l S");
        using (doc)
        {
            var page = doc.Pages[0];
            var m = PageGeometry.PageToDevice(page, 1);
            byte Darkness(bool enhance)
            {
                using var tile = new SKBitmap(200, 200);
                using (var canvas = new SKCanvas(tile))
                {
                    canvas.Clear(SkiaRenderer.Paper);
                    SkiaRenderer.RenderTile(canvas, list, doc, m, new SKRectI(0, 0, 200, 200), new ImageCache(), default, enhance);
                }
                var y = Enumerable.Range(95, 10).MinBy(row => tile.GetPixel(100, row).Red);
                return (byte)(255 - tile.GetPixel(100, y).Red);
            }
            Assert.True(Darkness(true) > Darkness(false) + 50, $"mit {Darkness(true)}, ohne {Darkness(false)}");
        }
    }
}
