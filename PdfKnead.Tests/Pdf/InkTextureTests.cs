using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using PdfKnead.Tests.Fonts;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Ballpoint texture of signatures: pattern, colour by Lambert-Beer, resolution on saving, preview.</summary>
public sealed class InkTextureTests
{
    /// <summary>Draws a thick and a thin oblique stroke side by side, at <paramref name="scale"/> pixels per unit.</summary>
    private static (float[] Coverage, int Width, int Height) Strokes(double scale)
    {
        int w = (int)Math.Ceiling(60 * scale), h = (int)Math.Ceiling(40 * scale);
        var raster = new CoverageRaster(w, h);
        // thick: 6 units wide, vertical (downstroke); thin: 1.5 units, 30° up to the right (upstroke)
        raster.Polygon([(12 * scale, 35 * scale), (18 * scale, 35 * scale), (18 * scale, 5 * scale), (12 * scale, 5 * scale)]);
        double nx = 0.75 * Math.Sin(Math.PI / 6), ny = 0.75 * Math.Cos(Math.PI / 6);
        double ex = 30 + 25, ey = 30 - 25 * Math.Tan(Math.PI / 6);
        raster.Polygon([((30 - nx) * scale, (30 - ny) * scale), ((ex - nx) * scale, (ey - ny) * scale), ((ex + nx) * scale, (ey + ny) * scale), ((30 + nx) * scale, (30 + ny) * scale)]);
        return (raster.Coverage(), w, h);
    }

    private static double Mean(byte[] levels, float[] coverage, Func<int, bool>? where = null)
    {
        double sum = 0;
        var n = 0;
        for (var i = 0; i < levels.Length; i++)
        {
            if (coverage[i] < 1 || where?.Invoke(i) == false) continue;
            sum += levels[i];
            n++;
        }
        return sum / Math.Max(1, n) / (PenTexture.Levels - 1);
    }

    [Fact]
    public void Raster_CoversShapesExactly()
    {
        var raster = new CoverageRaster(10, 10);
        raster.Polygon([(2, 2), (6, 2), (6, 6), (2, 6)]);
        raster.Polygon([(4, 4), (8, 4), (8, 8), (4, 8)]);   // overlaps: counts once
        var c = raster.Coverage();
        Assert.Equal(1, c[3 * 10 + 3], 3);
        Assert.Equal(1, c[5 * 10 + 5], 3);
        Assert.Equal(0, c[1 * 10 + 1], 3);
        Assert.Equal(16 + 16 - 4, c.Sum(), 1);
        var circle = new CoverageRaster(40, 40);
        circle.Circle(20, 20, 10);
        Assert.Equal(Math.PI * 100, circle.Coverage().Sum(), 0);   // accurate to one pixel
    }

    [Fact]
    public void Texture_IsTheSameForTheSameSeed_AndDiffersOtherwise()
    {
        var (cov, w, h) = Strokes(8);
        var a = PenTexture.Generate(cov, w, h, 3, 42);
        var b = PenTexture.Generate(cov, w, h, 3, 42);
        var c = PenTexture.Generate(cov, w, h, 3, 43);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        // No ink outside the shape
        for (var i = 0; i < a.Length; i++)
            if (cov[i] == 0) Assert.Equal(0, a[i]);
    }

    [Fact]
    public void Texture_LooksAlikeAtEveryResolution_DownstrokesDenser()
    {
        // Preview (coarse) and saved (fine): the same impression in mean density and share of light spots
        var (coarse, cw, ch) = Strokes(6);
        var (fine, fw, fh) = Strokes(18);
        var lowRes = PenTexture.Generate(coarse, cw, ch, 6 * 40 / 170.0, 7);
        var highRes = PenTexture.Generate(fine, fw, fh, 18 * 40 / 170.0, 7);
        Assert.InRange(Mean(highRes, fine) - Mean(lowRes, coarse), -0.06, 0.06);
        Assert.InRange(Mean(highRes, fine), 0.12, 0.6);   // like ballpoint ink: loose on average (upstrokes light), dense spots full
        // Ballpoint: the thick steep stroke (downstroke) is denser than the thin flat one (upstroke)
        double down = Mean(highRes, fine, i => i % fw < 27 * 18), up = Mean(highRes, fine, i => i % fw > 29 * 18);
        Assert.True(down > up + 0.05, $"Abstrich {down:0.000}, Aufstrich {up:0.000}");
        // and grainy, not smooth
        var values = highRes.Where((v, i) => fine[i] == 1).Select(v => (double)v).ToArray();
        var mean = values.Average();
        Assert.True(Math.Sqrt(values.Average(v => (v - mean) * (v - mean))) > 6, "keine Körnung");
    }

    [Fact]
    public void Palette_FromPaperToTheChosenColor_NeverDarker()
    {
        foreach (var color in new[] { SignatureElement.InkBlue, 0x2A4BC0, 0x1E6B3A, 0xB32030, 0x000000 })
        {
            var palette = InkColor.Palette(color, PenTexture.Levels);
            Assert.Equal([255, 255, 255], palette[..3]);
            for (var level = 1; level < PenTexture.Levels; level++)
                for (var c = 0; c < 3; c++)
                {
                    var chosen = Math.Max((color >> (16 - 8 * c)) & 0xFF, 3);
                    Assert.True(palette[level * 3 + c] <= palette[(level - 1) * 3 + c], "dichter = dunkler");
                    Assert.True(palette[level * 3 + c] >= chosen - 1, "nie dunkler als die gewählte Farbe");
                }
            var full = (int)Math.Ceiling(InkColor.DarkestAt * (PenTexture.Levels - 1)) * 3;
            for (var c = 0; c < 3; c++) Assert.InRange(palette[full + c], Math.Max((color >> (16 - 8 * c)) & 0xFF, 3) - 1, Math.Max((color >> (16 - 8 * c)) & 0xFF, 3) + 1);
        }
        // Applied thinly, blue stays blue (Lambert-Beer) instead of turning grey as with opacity over white
        var blue = InkColor.Palette(SignatureElement.InkBlue, PenTexture.Levels);
        var light = 10 * 3;
        Assert.True(blue[light + 2] - blue[light] > 255 * 0.3 * (0x8C - 0x1B) / 255.0, "zu grau");
    }

    [Fact]
    public void Strength_MakesInkDenser_ButNeverDarker()
    {
        var normal = InkColor.Palette(SignatureElement.InkBlue, PenTexture.Levels);
        var dense = InkColor.Palette(SignatureElement.InkBlue, PenTexture.Levels, 2);
        var light = InkColor.Palette(SignatureElement.InkBlue, PenTexture.Levels, 0.5);
        var mid = 15 * 3;   // medium density
        Assert.True(dense[mid] < normal[mid] && normal[mid] < light[mid]);
        Assert.Equal(normal[^3..], dense[^3..]);   // the densest level is always exactly the chosen colour
        Assert.True(dense.Select((v, i) => v >= normal[normal.Length - 3 + i % 3] - 1).All(b => b));   // never darker than the colour
    }

    [Fact]
    public void FineGrain_SpecklesDenseInk_PaperStaysWhite()
    {
        var palette = InkColor.GrainPalette(SignatureElement.InkBlue, PenTexture.Levels, 1.6);
        Assert.Equal(PenTexture.Levels * InkColor.GrainClasses * 3, palette.Length);
        for (var g = 0; g < InkColor.GrainClasses; g++) Assert.Equal([255, 255, 255], palette[(g * 3)..(g * 3 + 3)]);   // paper
        for (var i = 0; i < palette.Length; i++) Assert.True(palette[i] >= Math.Max((SignatureElement.InkBlue >> (16 - 8 * (i % 3))) & 0xFF, 3) - 1);
        // Saturated spot: the grain levels differ (light specks), the lightest is lighter than the darkest
        var dense = (PenTexture.Levels - 1) * InkColor.GrainClasses * 3;
        Assert.True(palette[dense] > palette[dense + (InkColor.GrainClasses - 1) * 3]);
        // The grain levels are equally frequent and fixed per position
        var counts = new int[InkColor.GrainClasses];
        for (var y = 0; y < 200; y++) for (var x = 0; x < 200; x++) counts[InkColor.GrainClass(x, y)]++;
        Assert.All(counts, c => Assert.InRange(c, 8500, 11500));
        Assert.Equal(InkColor.GrainClass(17, 4), InkColor.GrainClass(17, 4));
    }

    [Fact]
    public void TargetTable_AscendsAndIsDenserInDownstrokes()
    {
        // Ascending per direction and thickness; vertical thick downstrokes denser than thin upstrokes to the upper right
        double Median(int fan, int thick) => PenTexture.Target[(fan * 2 + thick) * 101 + 50] / 255.0;
        for (var row = 0; row < 12; row++)
            for (var k = 1; k <= 100; k++) Assert.True(PenTexture.Target[row * 101 + k] >= PenTexture.Target[row * 101 + k - 1]);
        Assert.True(Median(3, 1) > Median(1, 0) + 0.05);
        Assert.InRange(PenTexture.Target[(3 * 2 + 1) * 101 + 60] / 255.0, InkColor.DarkestAt - 0.01, InkColor.DarkestAt + 0.01);
    }

    [Fact]
    public void Density_FromAnInkImage_RoundTrips()
    {
        // Ink by Lambert-Beer (as scanned) with known levels; full ink plentiful as the darkest level
        var palette = new byte[PenTexture.Levels * 3];
        for (var level = 0; level < PenTexture.Levels; level++)
            for (var c = 0; c < 3; c++)
                palette[level * 3 + c] = (byte)Math.Round(255 * Math.Pow(new[] { 8, 26, 101 }[c] / 255.0, level / (double)(PenTexture.Levels - 1)));
        var levels = Enumerable.Range(0, 400).Select(i => i < 100 ? PenTexture.Levels - 1 : i % PenTexture.Levels).ToArray();
        var image = RgbaImage.Blank(400, 1);
        for (var i = 0; i < levels.Length; i++)
        {
            image.Pixels[i * 4] = palette[levels[i] * 3];
            image.Pixels[i * 4 + 1] = palette[levels[i] * 3 + 1];
            image.Pixels[i * 4 + 2] = palette[levels[i] * 3 + 2];
            image.Pixels[i * 4 + 3] = 255;
        }
        var density = InkColor.Density(image, PenTexture.Levels);
        for (var i = 0; i < levels.Length; i++) Assert.InRange(density[i], levels[i] - 2, levels[i] + 2);
    }

    private static PdfDocument Save(PageItem item)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(item.Source, [item], output);
        return PdfDocument.Open(output.ToArray());
    }

    [Fact]
    public void Saved_At600Dpi_AsInkImageMultiplied()
    {
        var signature = new SignatureElement
        {
            Rect = new PdfRectangle(100, 600, 400, 660), Name = "Max Mustermann", FontFile = "GochiHand-Regular.ttf", Textured = true,
        };
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        using var copy = Save(PageItem.From(original)[0].WithElements([signature]));
        var images = new List<PdfStream>();
        var states = new List<PdfDictionary>();
        void Collect(PdfDictionary? resources, int depth)
        {
            if (resources is null || depth > 4) return;
            if (resources.GetDictionary(PdfName.Get("ExtGState")) is { } gs)
                foreach (var key in gs.Keys) if (gs.GetDictionary(key) is { } s) states.Add(s);
            if (resources.GetDictionary(PdfName.Get("XObject")) is not { } xobjects) return;
            foreach (var key in xobjects.Keys)
            {
                if (xobjects.GetStream(key) is not { } x) continue;
                if (x.Dictionary.GetName(PdfName.Subtype)?.Value == "Image") images.Add(x);
                else Collect(x.Dictionary.GetDictionary(PdfName.Resources), depth + 1);
            }
        }
        Collect(copy.Pages[0].Resources, 0);
        var ink = Assert.Single(images);
        // Width of the content (fitted, 2 pt margin) at 600 dpi, plus 3 pixels of margin per side
        var contentWidth = Math.Min(300 - 2 * ElementAppearance.Padding, (60 - 2 * ElementAppearance.Padding) * ElementAppearance.SignatureAspect(signature));
        Assert.InRange(ink.Dictionary.Get(PdfName.Get("Width")) is PdfInteger w ? w.Value : 0, contentWidth * 600 / 72 + 4, contentWidth * 600 / 72 + 8);
        Assert.Equal("Indexed", (ink.Dictionary.GetArray(PdfName.Get("ColorSpace"))?.Get(0) as PdfName)?.Value);
        // 64 levels × 4 grain levels as a palette (white up to the chosen colour), fully opaque: no mask
        Assert.Equal(PenTexture.Levels * InkColor.GrainClasses - 1, (ink.Dictionary.GetArray(PdfName.Get("ColorSpace"))?.Get(2) as PdfInteger)?.Value);
        Assert.IsType<PdfNull>(ink.Dictionary.Get(PdfName.Get("SMask")));
        Assert.Contains(states, s => s.GetName(PdfName.Get("BM"))?.Value == "Multiply");   // glazing like real ink

        // Drawn: grainy, on average close to the chosen colour, paper white
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(copy, copy.Pages[0]), copy, copy.Pages[0], 2, new ImageCache());
        var inkPixels = new List<SKColor>();
        for (var y = (int)((841.89 - 660) * 2); y < (841.89 - 600) * 2; y++)
            for (var x = 200; x < 800; x++)
                if (bitmap.GetPixel(x, y) is var p && p.Red < 200) inkPixels.Add(p);
        Assert.True(inkPixels.Count > 2000);
        Assert.True(inkPixels.Average(p => p.Blue) > inkPixels.Average(p => p.Red) + 50, "nicht blau");
        Assert.True(inkPixels.Select(p => (int)p.Red).Distinct().Count() > 20, "keine Struktur");
    }

    [Fact]
    public void Display_ShowsSmoothFirst_ThenTheTexture()
    {
        var name = "Erika Musterfrau " + Guid.NewGuid().ToString("N")[..6];   // new name: nothing in the cache yet
        var signature = new SignatureElement { Rect = new PdfRectangle(0, 0, 200, 50), Name = name, FontFile = "ArchitectsDaughter-Regular.ttf", Textured = true };
        using var improved = new ManualResetEventSlim();
        void OnImproved() => improved.Set();
        InkTextures.Improved += OnImproved;
        try
        {
            var first = ElementAppearance.For(signature);
            Assert.Equal(0, first.Texture!.Scale);   // smooth shape; the texture follows in the background
            // The signal may also come from other signatures (tests run in parallel): wait until this one is done. The worker runs on the
            // thread pool, which the full test run keeps busy: 10 s were too short there twice (M3 and M5), alone it takes about 1 s
            var later = first;
            for (var until = DateTime.UtcNow.AddSeconds(60); DateTime.UtcNow < until && later.Texture!.Coarse;)
            {
                improved.Wait(TimeSpan.FromMilliseconds(200));
                improved.Reset();
                later = ElementAppearance.For(signature);
            }
            Assert.NotSame(first, later);
            Assert.False(later.Texture!.Coarse);

            // Moving (same size) recomputes nothing and shares the image
            var moved = ElementAppearance.Moved(signature, new PdfRectangle(50, 50, 250, 100));
            Assert.Same(later.Form, ElementAppearance.For(moved).Form);
        }
        finally
        {
            InkTextures.Improved -= OnImproved;
        }
    }

    [Fact]
    public void Final_WaitsForFullResolution_AndHugeSignaturesStayBounded()
    {
        var name = "Max Mustermann " + Guid.NewGuid().ToString("N")[..6];
        var huge = new SignatureElement { Rect = new PdfRectangle(0, 0, 2000, 600), Name = name, FontFile = "ComingSoon-Regular.ttf", Textured = true };
        var final = ElementAppearance.Final(huge);
        Assert.False(final.Texture!.Coarse);
        var texture = InkTextures.Best(final.Texture.Source.Key)!;
        Assert.True((long)texture.Width * texture.Height <= InkTextures.MaxPixels);
    }

    [Theory]
    [InlineData(TestColorFonts.Sbix)]
    [InlineData(TestColorFonts.Cbdt)]
    [InlineData(TestColorFonts.Svg)]
    public void ColorFont_TheCheckBoxDecides_TextureOverTheFormOfTheImages(string font)
    {
        // Without the check box the font's own images, with it the ballpoint texture – over the form of the ink in the images
        var plain = new SignatureElement { Rect = new PdfRectangle(0, 0, 300, 60), Name = "Max Mustermann", FontFile = font };
        Assert.Null(ElementAppearance.Final(plain).Texture);
        var final = ElementAppearance.Final(plain with { Textured = true });
        Assert.NotNull(final.Texture);
        var texture = InkTextures.Best(final.Texture.Source.Key)!;
        var picture = ElementAppearance.SignatureImage(plain)!;
        int inked = 0, inkedHit = 0, empty = 0, emptyHit = 0;
        for (var y = 0; y < picture.Height; y++)
            for (var x = 0; x < picture.Width; x++)
            {
                var tx = (int)(texture.Pad + (x + 0.5) * texture.Scale);
                var ty = (int)(texture.Pad + (y + 0.5) * texture.Scale);
                var hit = texture.Levels[ty * texture.Width + tx] > 0;
                var level = picture.Levels[y * picture.Width + x];
                if (level > PenTexture.Levels / 2) (inked, inkedHit) = (inked + 1, inkedHit + (hit ? 1 : 0));
                else if (level == 0 && Clear(picture, x, y)) (empty, emptyHit) = (empty + 1, emptyHit + (hit ? 1 : 0));
            }
        Assert.True(inked > 100 && empty > 100);
        Assert.True(inkedHit > inked * 0.8, $"Tinte: nur {inkedHit} von {inked} Pixeln mit Struktur");
        Assert.True(emptyHit < empty * 0.02, $"Papier: {emptyHit} von {empty} Pixeln mit Struktur");

        // No ink within two pixels: there the texture must stay empty
        static bool Clear(InkTexture image, int x, int y)
        {
            for (var dy = -2; dy <= 2; dy++)
                for (var dx = -2; dx <= 2; dx++)
                {
                    int px = x + dx, py = y + dy;
                    if (px >= 0 && py >= 0 && px < image.Width && py < image.Height && image.Levels[py * image.Width + px] > 0) return false;
                }
            return true;
        }
    }

    [Theory]
    [InlineData(TestColorFonts.Sbix)]
    [InlineData(TestColorFonts.Cbdt)]
    [InlineData(TestColorFonts.Svg)]
    public void ColorFont_TakesTheChosenColor(string font)
    {
        SKColor Average(int color)
        {
            var signature = new SignatureElement { Rect = new PdfRectangle(0, 0, 300, 60), Name = "Max", FontFile = font, Color = color };
            var page = BlankPage.Create(300, 60).Page;
            using var bitmap = new SKBitmap(300, 60);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                ElementOverlay.Draw(canvas, page, [signature], 1);
            }
            var ink = Enumerable.Range(0, 300 * 60).Select(i => bitmap.GetPixel(i % 300, i / 300)).Where(p => p.Red + p.Green + p.Blue < 600).ToList();
            Assert.True(ink.Count > 100);
            return new SKColor((byte)ink.Average(p => p.Red), (byte)ink.Average(p => p.Green), (byte)ink.Average(p => p.Blue));
        }
        var blue = Average(SignatureElement.InkBlue);
        var black = Average(0x000000);
        var red = Average(0xB32030);
        Assert.True(blue.Blue > blue.Red + 40);
        Assert.InRange(black.Blue - black.Red, -12, 12);
        Assert.True(red.Red > red.Blue + 40);
    }
}
