using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Processing;

namespace PdfKnead.Tests.Scanning;

/// <summary>
/// „Schräglage ausgleichen“ and „Ränder abschneiden“ (KONZEPT §5.25) on pages made for the test with a known angle: the angle is found to
/// within a tenth of a degree, pages without a clear direction stay as they are, dark borders are found and cut away.
/// </summary>
public sealed class StraighteningTests
{
    private const int Dpi = 150, W = 1240, H = 1754;   // A4 at 150 dpi

    /// <summary>A page with lines of „words“ (dark bars of x-height with gaps), paper 235, optionally fewer lines.</summary>
    private static ScannedImage TextPage(int lines = 40, int seed = 7)
    {
        var px = new byte[W * H];
        Array.Fill(px, (byte)235);
        var random = new Random(seed);
        for (var line = 0; line < lines; line++)
        {
            var y = 180 + line * 36;
            if (y > H - 200) break;
            for (var x = 150; x < W - 150;)
            {
                var word = random.Next(20, 90);
                for (var yy = y; yy < y + 12; yy++)
                    for (var xx = x; xx < Math.Min(W - 150, x + word); xx++)
                        if ((xx / 3 + yy) % 5 != 0) px[yy * W + xx] = 30;   // letters: not completely solid
                x += word + random.Next(10, 18);
            }
        }
        return new ScannedImage(W, H, 1, Dpi, px);
    }

    [Theory]
    [InlineData(-15)]
    [InlineData(-4.2)]
    [InlineData(-0.7)]
    [InlineData(0.3)]
    [InlineData(2.5)]
    [InlineData(9)]
    [InlineData(19)]
    public void Skew_FoundToATenthOfADegree(double degrees)
    {
        var skewed = ImageGeometry.Rotate(TextPage(), degrees);
        var found = SkewDetector.Detect(skewed);
        Assert.True(found.Sure, $"sicher bei {degrees}° (Güte {found.Confidence:0.00})");
        Assert.InRange(found.Degrees, degrees - 0.1, degrees + 0.1);
    }

    [Fact]
    public void Straight_PageStays()
    {
        var plan = new SkewStraightening().Find(TextPage());
        Assert.Null(plan);   // 0°: nothing to turn
    }

    [Fact]
    public void NoDirection_PageStays()
    {
        // blank paper
        var blank = new ScannedImage(W, H, 1, Dpi, Enumerable.Repeat((byte)235, W * H).ToArray());
        Assert.False(SkewDetector.Detect(blank).Sure);
        // noise like a photo
        var random = new Random(3);
        var noise = new byte[W * H];
        random.NextBytes(noise);
        Assert.False(SkewDetector.Detect(new ScannedImage(W, H, 1, Dpi, noise)).Sure);
        Assert.Null(new SkewStraightening().Find(new ScannedImage(W, H, 1, Dpi, noise)));
        // a single word is too little; one long line already gives the angle
        var word = new byte[W * H];
        Array.Fill(word, (byte)235);
        for (var y = 600; y < 612; y++)
            for (var x = 600; x < 640; x++) word[y * W + x] = 30;
        Assert.False(SkewDetector.Detect(ImageGeometry.Rotate(new ScannedImage(W, H, 1, Dpi, word), 3)).Sure);
        Assert.InRange(SkewDetector.Detect(ImageGeometry.Rotate(TextPage(lines: 1), 3)).Degrees, 2.9, 3.1);
    }

    [Fact]
    public void SquaredPaper_LinesGiveTheDirection()
    {
        var px = new byte[W * H];
        Array.Fill(px, (byte)240);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (y % 30 < 2 || x % 30 < 2) px[y * W + x] = 150;   // a grid of 5 mm
        var skewed = ImageGeometry.Rotate(new ScannedImage(W, H, 1, Dpi, px), 1.5);
        var found = SkewDetector.Detect(skewed);
        Assert.True(found.Sure);
        Assert.InRange(found.Degrees, 1.4, 1.6);
    }

    [Fact]
    public void DarkBorders_FoundAndCutAway()
    {
        var page = TextPage();
        var px = (byte[])page.Pixels.Clone();
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (x < 40 || y < 70 || x >= W - 10) px[y * W + x] = 15;
        var bordered = page with { Pixels = px };
        var (left, top, right, bottom) = ImageGeometry.DarkBorders(bordered);
        Assert.InRange(left * W, 40, 46);
        Assert.InRange(top * H, 70, 76);
        Assert.InRange(right * W, 10, 16);
        Assert.Equal(0, bottom);
        Assert.Equal((0, 0, 0, 0), ImageGeometry.DarkBorders(page));   // no border, nothing cut
        // the geometry cuts it: the result starts in the paper
        var geometry = PageStraightening.Find(bordered, Straighteners.None, cropBorders: true, manualDegrees: null);
        var cut = geometry.Apply(bordered);
        Assert.True(cut.Width <= W - 50 && cut.Height <= H - 70);
        Assert.True(cut.Pixels[0] > 200);
    }

    [Fact]
    public void Geometry_SameOnPreviewAndFullScan_ManualAngleWins_UnknownMethodIsNone()
    {
        var page = ImageGeometry.Rotate(TextPage(), 3);
        var geometry = PageStraightening.Find(page, Straighteners.Skew, cropBorders: true, manualDegrees: null);
        Assert.InRange(geometry.Plan!.Degrees, -3.1, -2.9);
        // a reduced copy is turned by the same angle and cut by the same shares
        var full = geometry.Apply(page);
        var half = geometry.Apply(new ScannedImage(W / 2, H / 2, 1, Dpi / 2, [.. Enumerable.Range(0, W / 2 * (H / 2)).Select(i => page.Pixels[(i / (W / 2)) * 2 * W + i % (W / 2) * 2])]));
        Assert.InRange(half.Width * 2, full.Width - 3, full.Width + 3);
        // straightened, the lines are level again
        Assert.InRange(SkewDetector.Detect(full).Degrees, -0.1, 0.1);
        Assert.Equal(1.0, PageStraightening.Find(page, Straighteners.Skew, false, manualDegrees: 1).Plan!.Degrees);
        Assert.Null(PageStraightening.Find(page, "unbekannt", false, null).Plan);
        Assert.Equal(Straighteners.None, Straighteners.For("unbekannt").Key);
    }

    [Fact]
    public void Rotate_CornersTakeThePaperColour_ColourPagesStayColour()
    {
        var px = new byte[200 * 300 * 3];
        for (var i = 0; i < px.Length; i += 3) (px[i], px[i + 1], px[i + 2]) = (240, 230, 200);   // yellowish paper
        var page = new ScannedImage(200, 300, 3, 100, px);
        var turned = ImageGeometry.Rotate(page, 10);
        Assert.Equal(3, turned.Channels);
        Assert.Equal((240, 230, 200), (turned.Pixels[0], turned.Pixels[1], turned.Pixels[2]));   // a corner
        Assert.Same(page, ImageGeometry.Rotate(page, 0));
    }
}
