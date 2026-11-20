using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Processing;

namespace PdfKnead.Tests.Scanning;

/// <summary>
/// Show-through with the back of the sheet (KONZEPT §5.25, S5): the back shines through the front mirrored, blurred and slightly shifted;
/// with the back known the show-through disappears where the front is blank, the print of the front stays, and a back that does not fit
/// changes nothing.
/// </summary>
public sealed class ShowThroughBackTests
{
    private const int Dpi = 150, W = 1240, H = 1754;
    private const byte Paper = 232;

    /// <summary>Lines of „words“ between two heights.</summary>
    private static byte[] Text(int seed, int from, int to)
    {
        var px = new byte[W * H];
        Array.Fill(px, Paper);
        var random = new Random(seed);
        for (var y = from; y < to; y += 33)
            for (var x = 130; x < W - 130;)
            {
                var word = random.Next(18, 80);
                for (var yy = y; yy < y + 11; yy++)
                    for (var xx = x; xx < Math.Min(W - 130, x + word); xx++) px[yy * W + xx] = 30;
                x += word + random.Next(9, 16);
            }
        return px;
    }

    /// <summary>The front as scanned: its print times the transmission of the back (mirrored left–right, blurred, shifted by a few samples).</summary>
    private static ScannedImage Front(byte[] front, byte[] back, double alpha, int shiftX, int shiftY)
    {
        var blurred = new double[W * H];
        const int r = 2;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var (sum, n) = (0.0, 0);
                for (var dy = -r; dy <= r; dy++)
                    for (var dx = -r; dx <= r; dx += 2)
                    {
                        var (bx, by) = (W - 1 - x + dx + shiftX, y + dy + shiftY);
                        sum += (uint)bx < W && (uint)by < H ? back[by * W + bx] : Paper;
                        n++;
                    }
                blurred[y * W + x] = sum / n / Paper;
            }
        var seen = new byte[W * H];
        for (var i = 0; i < seen.Length; i++) seen[i] = (byte)Math.Round(front[i] * (1 - alpha * (1 - blurred[i])));
        return new ScannedImage(W, H, 1, Dpi, seen);
    }

    /// <summary>How much the blank lower half of the front is still darkened: the mean shortfall against the paper.</summary>
    private static double Shadow(ScannedImage page)
    {
        double sum = 0;
        var n = 0;
        for (var y = H / 2 + 50; y < H - 100; y += 2)
            for (var x = 150; x < W - 150; x += 2)
            {
                sum += Math.Max(0, 1 - page.Pixels[y * W + x] / (double)Paper);
                n++;
            }
        return sum / n;
    }

    [Fact]
    public void Back_ShowThroughGone_PrintStays()
    {
        var front = Text(1, 180, H / 2 - 40);   // print only in the upper half
        var back = Text(2, 180, H - 220);       // the back printed all over
        var seen = Front(front, back, 0.25, 6, -4);
        var backPage = new ScannedImage(W, H, 1, Dpi, back);
        var adjustments = new ScanAdjustments(ShowThrough: 70);
        var before = Shadow(seen);
        var oneSide = Shadow(ScanProcessor.Apply(seen, adjustments));
        var bothSides = ScanProcessor.Apply(seen, adjustments, backPage);
        var after = Shadow(bothSides);
        Assert.True(before > 0.02, $"vorher {before:0.000}");
        Assert.True(after < before * 0.25 && after < oneSide, $"vorher {before:0.000}, einseitig {oneSide:0.000}, beidseitig {after:0.000}");
        // the print of the front stays dark: as many dark samples in the upper half as before
        int Dark(ScannedImage p) => p.Pixels.Take(W * (H / 2 - 40)).Count(v => v < 90);
        Assert.InRange(Dark(bothSides), Dark(seen) * 0.95, Dark(seen) * 1.05);
        // the measured strength is about the one made
        var prepared = ShowThroughBack.Prepare(seen, backPage);
        Assert.NotNull(prepared);
        Assert.InRange(prepared.Strength, 0.18, 0.32);
    }

    [Fact]
    public void BackThatDoesNotFit_ChangesNothing()
    {
        var front = Text(1, 180, H - 220);
        var page = new ScannedImage(W, H, 1, Dpi, front);   // no show-through at all
        var unrelated = new ScannedImage(W, H, 1, Dpi, Text(9, 300, 900));
        Assert.Null(ShowThroughBack.Prepare(page, unrelated));
        var adjustments = new ScanAdjustments(ShowThrough: 70);
        Assert.Equal(ScanProcessor.Apply(page, adjustments).Pixels, ScanProcessor.Apply(page, adjustments, unrelated).Pixels);
    }
}
