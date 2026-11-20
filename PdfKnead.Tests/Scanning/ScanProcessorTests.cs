using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Processing;

namespace PdfKnead.Tests.Scanning;

/// <summary>
/// The processing of scanned pages (KONZEPT §5.25, „Nachbearbeitung“) on pages made for the test: grey paper with a shadow, show-through,
/// highlighter, edges — each slider moves in the right direction and leaves alone what it should.
/// </summary>
public sealed class ScanProcessorTests
{
    private const int W = 600, H = 400;

    /// <summary>A grey page at 150 dpi: paper with a shadow towards the left edge, a black bar, a pale grey bar (show-through) and a yellow bar.</summary>
    private static ScannedImage Page(byte paper = 200, byte shadow = 130, bool colour = true)
    {
        var c = colour ? 3 : 1;
        var px = new byte[W * H * c];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                // the paper darkens towards the left edge (lid shadow, spine)
                var p = x >= 150 ? paper : (byte)(shadow + (paper - shadow) * x / 150);
                var (r, g, b) = (p, p, p);
                if (y is >= 60 and < 80 && x is >= 40 and < 560) (r, g, b) = ((byte)(p * 0.2), (byte)(p * 0.2), (byte)(p * 0.2));   // print
                if (y is >= 160 and < 180 && x is >= 40 and < 560) (r, g, b) = ((byte)(p * 0.88), (byte)(p * 0.88), (byte)(p * 0.88));   // show-through
                if (colour && y is >= 260 and < 280 && x is >= 200 and < 560) (r, g, b) = (p, (byte)(p * 0.95), (byte)(p * 0.35));   // highlighter
                var at = (y * W + x) * c;
                px[at] = r;
                if (colour) (px[at + 1], px[at + 2]) = (g, b);
            }
        return new ScannedImage(W, H, c, 150, px);
    }

    private static byte At(ScannedImage img, int x, int y, int channel = 0) => img.Pixels[(y * img.Width + x) * img.Channels + channel];

    [Fact]
    public void Nothing_RawScanAsItIs()
    {
        var page = Page();
        Assert.Same(page, ScanProcessor.Apply(page, ScanAdjustments.None));
        Assert.True(ScanAdjustments.None.IsNone);
        Assert.False(new ScanAdjustments().AutoEnhance);   // new pages start from the raw scan
        Assert.True(new ScanAdjustments().LightIsNone);
    }

    [Fact]
    public void AutoEnhance_PaperWhiteAlsoInTheShadow_PrintBlack()
    {
        var result = ScanProcessor.Apply(Page(), new ScanAdjustments(AutoEnhance: true));
        Assert.InRange(At(result, 300, 300), 245, 255);   // paper
        Assert.InRange(At(result, 30, 300), 235, 255);    // paper in the shadow (a steep shadow: 70 levels over an inch)
        Assert.InRange(At(result, 300, 70), 0, 30);       // print
        Assert.InRange(At(result, 80, 70), 0, 40);        // print in the shadow
        // the pale bar stays visible without „Durchscheinen“
        Assert.InRange(At(result, 300, 170), 150, 240);
    }

    [Fact]
    public void ShowThrough_PaleGreyGone_PrintAndHighlighterStay()
    {
        var page = Page();
        var result = ScanProcessor.Apply(page, new ScanAdjustments(AutoEnhance: false, ShowThrough: 100));
        // without „automatisch verbessern“ the paper keeps its tone
        Assert.InRange(At(result, 300, 300), 195, 205);
        Assert.InRange(At(result, 300, 170), 193, 205);              // show-through lifted to the paper
        Assert.InRange(At(result, 300, 70), 0, 60);                  // print stays
        Assert.InRange(At(result, 300, 270, 2), 0, 110);             // highlighter keeps its colour (little blue)
        // at strength 0 nothing is lifted
        var none = ScanProcessor.Apply(page, new ScanAdjustments(AutoEnhance: false, ShowThrough: 0, Brightness: 1));
        Assert.InRange(At(none, 300, 170), 170, 182);
    }

    [Fact]
    public void BrightnessContrastLuminance_Directions_BlackAndWhiteStay()
    {
        var steps = new byte[] { 0, 64, 128, 192, 255 };
        var grey = new ScannedImage(steps.Length, 1, 1, 150, steps);
        byte[] Run(ScanAdjustments a) => ScanProcessor.Apply(grey, a with { AutoEnhance = false }).Pixels;
        var lighter = Run(new ScanAdjustments(Brightness: 50));
        Assert.Equal(0, lighter[0]);
        Assert.Equal(255, lighter[4]);
        Assert.True(lighter[2] > 150);
        Assert.True(Run(new ScanAdjustments(Brightness: -50))[2] < 105);
        var contrast = Run(new ScanAdjustments(Contrast: 60));
        Assert.True(contrast[1] < 50 && contrast[3] > 205);
        Assert.InRange(contrast[2], 120, 136);
        var grey50 = Run(new ScanAdjustments(Luminance: 50));
        Assert.True(grey50[2] > 150 && grey50[4] == 255);   // on grey pages too
        var colour = new ScannedImage(steps.Length, 1, 3, 150, [.. steps.SelectMany(v => new[] { v, v, v })]);
        byte[] RunColour(ScanAdjustments a) => ScanProcessor.Apply(colour, a with { AutoEnhance = false }).Pixels;
        var light = RunColour(new ScanAdjustments(Luminance: 50));
        Assert.True(light[3] > 80 && light[6] > 150);
        Assert.Equal(255, light[12]);
        Assert.Equal(0, light[0]);
        Assert.True(RunColour(new ScanAdjustments(Luminance: -50))[6] < 110);
    }

    [Fact]
    public void Luminance_KeepsTheColour()
    {
        var px = new byte[] { 160, 80, 40 };
        var result = ScanProcessor.Apply(ScanImage1(px), new ScanAdjustments(AutoEnhance: false, Luminance: 30)).Pixels;
        Assert.True(result[0] > 160 && result[1] > 80 && result[2] > 40);
        Assert.True(result[0] > result[1] && result[1] > result[2]);   // still the same hue order
    }

    /// <summary>One colour pixel.</summary>
    private static ScannedImage ScanImage1(byte[] rgb) => new(1, 1, 3, 150, rgb);

    [Fact]
    public void Sharpen_EdgesSteeper_FlatAreasStay()
    {
        var (w, h) = (40, 10);
        var px = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) px[y * w + x] = x < 20 ? (byte)60 : (byte)200;
        var page = new ScannedImage(w, h, 1, 300, px);
        var result = ScanProcessor.Apply(page, new ScanAdjustments(AutoEnhance: false, Sharpen: 100));
        Assert.True(result.Pixels[5 * w + 19] < 60, "dark side of the edge darker");
        Assert.True(result.Pixels[5 * w + 20] > 200, "light side lighter");
        Assert.Equal(60, result.Pixels[5 * w + 5]);
        Assert.Equal(200, result.Pixels[5 * w + 35]);
    }

    [Fact]
    public void BlackWhite_OneChannelOnlyBlackAndWhite_ThresholdDecides()
    {
        var page = Page();
        var bw = ScanProcessor.Apply(page, new ScanAdjustments(AutoEnhance: true, BlackWhite: true));
        Assert.True(bw.BlackWhite);
        Assert.Equal(1, bw.Channels);
        Assert.All(bw.Pixels, v => Assert.True(v is 0 or 255));
        Assert.Equal(0, At(bw, 300, 70));
        Assert.Equal(255, At(bw, 30, 300));   // the shadow does not become black: improved first
        int Black(ScannedImage i) => i.Pixels.Count(v => v == 0);
        var more = ScanProcessor.Apply(page, new ScanAdjustments(AutoEnhance: true, BlackWhite: true, Threshold: 95));
        Assert.True(Black(more) > Black(bw));
    }

    [Fact]
    public void Grey_ValuesOutOfRange_Cancel()
    {
        var grey = Page(colour: false);
        var result = ScanProcessor.Apply(grey, new ScanAdjustments(AutoEnhance: true, Brightness: double.NaN, Contrast: 1e9, Sharpen: -5));
        Assert.Equal(1, result.Channels);
        Assert.InRange(At(result, 300, 300), 245, 255);
        var odd = new ScanAdjustments(Brightness: double.PositiveInfinity, Threshold: double.NaN, ShowThrough: 400).Normalized();
        Assert.Equal((0, 50, 100), (odd.Brightness, odd.Threshold, odd.ShowThrough));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ScanProcessor.Apply(grey, new ScanAdjustments(AutoEnhance: true), cancel.Token));
    }

    [Fact]
    public void SaturationVibrance_OnlyTheColourChanges()
    {
        // a pale blue and a strong red
        var px = new byte[] { 150, 160, 190, 220, 30, 30 };
        var page = new ScannedImage(2, 1, 3, 150, px);
        double Chroma(ScannedImage i, int p)
        {
            var (_, a, b) = Core.Scanning.Processing.Oklab.FromSrgb(i.Pixels[p * 3] / 255f, i.Pixels[p * 3 + 1] / 255f, i.Pixels[p * 3 + 2] / 255f);
            return Math.Sqrt(a * a + b * b);
        }
        double Light(ScannedImage i, int p) => Core.Scanning.Processing.Oklab.FromSrgb(i.Pixels[p * 3] / 255f, i.Pixels[p * 3 + 1] / 255f, i.Pixels[p * 3 + 2] / 255f).L;
        ScannedImage Run(ScanAdjustments a) => ScanProcessor.Apply(page, a with { AutoEnhance = false });
        var more = Run(new ScanAdjustments(Saturation: 50));
        Assert.True(Chroma(more, 0) > Chroma(page, 0) * 1.3);
        Assert.Equal(Light(page, 0), Light(more, 0), 2);   // lightness stays
        var grey = Run(new ScanAdjustments(Saturation: -100));
        Assert.True(Chroma(grey, 0) < 0.01 && Chroma(grey, 1) < 0.01);
        // vibrance: the pale colour gains much more than the strong one
        var vibrant = Run(new ScanAdjustments(Vibrance: 80));
        Assert.True(Chroma(vibrant, 0) / Chroma(page, 0) > Chroma(vibrant, 1) / Chroma(page, 1) + 0.3);
        Assert.True(Run(new ScanAdjustments(Vibrance: -80)) is var paler && Chroma(paler, 0) < Chroma(page, 0));
    }

    [Fact]
    public void Grayscale_LikeAGreyScan_OneChannelEightBit()
    {
        var page = Page();
        var grey = ScanProcessor.Apply(page, new ScanAdjustments(AutoEnhance: false, Grayscale: true));
        Assert.Equal(1, grey.Channels);
        Assert.False(grey.BlackWhite);
        Assert.Contains(grey.Pixels, v => v is > 0 and < 255 and not 200);   // grey levels, not only black and white
        // the highlighter becomes a grey of its brightness
        var (r, g, b) = (At(page, 300, 270, 0), At(page, 300, 270, 1), At(page, 300, 270, 2));
        Assert.InRange(At(grey, 300, 270) - Math.Round(0.299 * r + 0.587 * g + 0.114 * b), -1, 1);
        // black and white takes precedence
        Assert.True(ScanProcessor.Apply(page, new ScanAdjustments(Grayscale: true, BlackWhite: true)).BlackWhite);
    }

    [Fact]
    public void Bands_ResultIndependentOfTheirSize()
    {
        var page = Page();
        var everything = new ScanAdjustments(AutoEnhance: true, ShowThrough: 60, Brightness: 20, Luminance: 10, Contrast: 30, Sharpen: 80, Saturation: 30, Vibrance: 40);
        var whole = ScanProcessor.Apply(page, everything, default, long.MaxValue);
        foreach (var rows in new[] { 1, 7, 64 })
            Assert.Equal(whole.Pixels, ScanProcessor.Apply(page, everything, default, (long)rows * W * 3).Pixels);
        var bw = everything with { BlackWhite = true };
        Assert.Equal(ScanProcessor.Apply(page, bw, default, long.MaxValue).Pixels, ScanProcessor.Apply(page, bw, default, 5L * W * 3).Pixels);
    }

    [Fact]
    public void DarkPage_NotFlattened()
    {
        // a photo-like dark page: no paper, the automatic improvement does not turn it into grey mush
        var px = new byte[200 * 200];
        for (var i = 0; i < px.Length; i++) px[i] = (byte)(30 + i % 200 / 4);
        var page = new ScannedImage(200, 200, 1, 150, px);
        var result = ScanProcessor.Apply(page, new ScanAdjustments(AutoEnhance: true));
        Assert.True(At(result, 199, 100) > At(result, 0, 100) + 20, "the gradient stays");
    }
}
