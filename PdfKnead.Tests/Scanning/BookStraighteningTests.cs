using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Processing;

namespace PdfKnead.Tests.Scanning;

/// <summary>
/// „Buchseite entzerren“ (KONZEPT §5.25) on book pages made for the test (<see cref="BookScanSimulator"/>): the lines are straight again
/// afterwards, the spine is found on either side, a double page is corrected as two halves, a contact sensor page is only lightened, flat
/// and empty pages stay.
/// </summary>
public sealed class BookStraighteningTests
{
    private const int Dpi = 150, W = 1240, H = 1754;

    /// <summary>A page of 40 lines of „words“ (A4 at 150 dpi).</summary>
    private static ScannedImage TextPage(int seed = 11)
    {
        var px = new byte[W * H];
        Array.Fill(px, (byte)235);
        var random = new Random(seed);
        for (var y = 180; y < H - 220; y += 33)
            for (var x = 130; x < W - 130;)
            {
                var word = random.Next(18, 80);
                for (var yy = y; yy < y + 11; yy++)
                    for (var xx = x; xx < Math.Min(W - 130, x + word); xx++)
                        if ((xx / 3 + yy) % 4 != 0) px[yy * W + xx] = 35;
                x += word + random.Next(9, 16);
            }
        return new ScannedImage(W, H, 1, Dpi, px);
    }

    /// <summary>How bent the lines of a page are: the deviation of their centres from a straight line, in work pixels (RMS).</summary>
    private static double Bending(ScannedImage page)
    {
        var lines = BookStraightening.Work.From(page).Lines(default, null);
        Assert.True(lines.Count >= 20, $"{lines.Count} Zeilen");
        double sum = 0;
        var n = 0;
        foreach (var line in lines)
        {
            var (k, d) = BookStraightening.LineFit(line.Select(p => ((double)p.Key, p.Value)));
            foreach (var p in line)
            {
                sum += Math.Pow(p.Value - (k * p.Key + d), 2);
                n++;
            }
        }
        return Math.Sqrt(sum / n);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Optics_LinesStraightAgain_EitherSide(bool spineRight)
    {
        var flat = Bending(TextPage());
        var bent = BookScanSimulator.Page(TextPage(), new BookLie(15, 60, 300, SpineAlongSensor: true, AxisAt: 0.45, BlurPerMm: 0), spineRight);
        var before = Bending(bent);
        var plan = new BookStraightening().Find(bent)!;
        Assert.IsType<BookPlan>(plan is CombinedPlan combined ? combined.Second : plan);
        var after = Bending(plan.Apply(bent));
        Assert.True(before > flat * 2, $"vorher {before:0.00}, flach {flat:0.00}");
        Assert.True(after < before * 0.5 && after < flat + 0.6, $"vorher {before:0.00}, nachher {after:0.00}, flach {flat:0.00}");
    }

    [Fact]
    public void Askew_BookPage_SkewTakenOutToo()
    {
        var bent = BookScanSimulator.Page(TextPage(), new BookLie(12, 60, 300, true, BlurPerMm: 0, SkewDegrees: 1.5), spineRight: true);
        var result = new BookStraightening().Find(bent)!.Apply(bent);
        Assert.InRange(SkewDetector.Detect(result).Degrees, -0.25, 0.25);
    }

    [Fact]
    public void DoublePage_TwoHalves()
    {
        var spread = BookScanSimulator.Spread(TextPage(1), TextPage(2), new BookLie(14, 60, 320, true, BlurPerMm: 0), turned: false);
        var found = new BookStraightening().Find(spread)!;
        var plan = Assert.IsType<DoublePagePlan>(found is CombinedPlan combined ? combined.Second : found);
        Assert.InRange(plan.Split, 0.45, 0.55);
        var result = found.Apply(spread);
        // each half straight again
        var half = result.Width / 2;
        Assert.True(Bending(ImageGeometry.Crop(result, 0, 0, result.Width - half, 0)) < 1.5);
        Assert.True(Bending(ImageGeometry.Crop(result, half, 0, 0, 0)) < 1.5);
    }

    [Fact]
    public void ContactSensor_NoBending_SpineLightened()
    {
        var page = BookScanSimulator.Page(TextPage(), new BookLie(10, 40, null, false, Shade: 0.5, BlurPerMm: 0), spineRight: false);
        var plan = Assert.IsType<BookPlan>(new BookStraightening().Find(page));
        Assert.Equal(1, plan.WidthRatio, 3);   // the geometry stays
        var result = plan.Apply(page);
        // the paper next to the spine (no print there) is lighter
        Assert.True(result.Pixels[100 * result.Width + 3] > page.Pixels[100 * page.Width + 3] + 40);
        Assert.InRange(result.Pixels[100 * result.Width + W / 2], 228, 242);   // the paper in the middle unchanged
    }

    [Fact]
    public void FlatAndEmptyPages_Stay()
    {
        Assert.Null(new BookStraightening().Find(TextPage()));
        Assert.Null(new BookStraightening().Find(new ScannedImage(W, H, 1, Dpi, Enumerable.Repeat((byte)235, W * H).ToArray())));
        // a flat, askew page is only turned
        Assert.IsType<RotationPlan>(new BookStraightening().Find(ImageGeometry.Rotate(TextPage(), 2)));
    }

    [Fact]
    public void Plan_FoundOnAReducedCopy_AppliesToTheFullPage()
    {
        var bent = BookScanSimulator.Page(TextPage(), new BookLie(15, 60, 300, true, BlurPerMm: 0), spineRight: true);
        var geometry = PageStraightening.Find(bent, Straighteners.Book, cropBorders: false, manualDegrees: null);
        Assert.IsType<BookPlan>(geometry.Plan is CombinedPlan combined ? combined.Second : geometry.Plan);
        Assert.True(Bending(geometry.Apply(bent)) < 1.5);
        Assert.Contains(Straighteners.All, s => s.Key == Straighteners.Book);
        Assert.Equal(Straighteners.Book, new ScanAdjustments(Straighten: "book").Normalized().Straighten);
    }
}
