namespace PdfKnead.Core.Scanning.Processing;

/// <summary>What the skew search found.</summary>
/// <param name="Degrees">The skew of the page in degrees, clockwise positive (turn by the negative to straighten).</param>
/// <param name="Confidence">How clearly the best angle stands out: its score over the median score of all angles tried.</param>
/// <param name="Sure">Whether the angle can be trusted; otherwise the page stays as it is.</param>
public readonly record struct SkewResult(double Degrees, double Confidence, bool Sure);

/// <summary>
/// Finds the skew of a scanned page from its lines of print (KONZEPT §5.25, „Schräglage ausgleichen“): the bottom edges of the print (dark
/// samples with a light one below) are projected along trial angles; at the right angle the edges of a line fall into few rows and the profile
/// gets sharp peaks. Score: the sum of the squared differences of neighbouring rows of the profile. Coarse over ±20° in steps of 0.5°, then
/// fine around the best in steps of 0.05° with a parabola through the three best.
/// </summary>
/// <remarks>
/// Measured only inside a margin of 4 % (edges of the glass, shadows) and without solid dark areas (photos, black areas: blocks more than half
/// dark). The angle counts as sure when the best score clearly exceeds the median and no second, separate peak comes close — otherwise
/// (little text, a photo, mixed directions) the page stays as it is. Ruled and squared paper give their direction through their lines.
/// </remarks>
public static class SkewDetector
{
    /// <summary>The largest skew searched.</summary>
    public const double MaxDegrees = 20;

    // the work image: about 100 dpi is enough for lines of print and keeps the search fast
    private const int WorkDpi = 100;

    /// <summary>Finds the skew of a page.</summary>
    /// <param name="page">The page (grey or colour).</param>
    /// <param name="cancel">Stops the search.</param>
    /// <returns>The skew; <see cref="SkewResult.Sure"/> false when the page shows no clear direction.</returns>
    public static SkewResult Detect(ScannedImage page, CancellationToken cancel = default)
    {
        var edges = Edges(page, out var w, out var h);
        if (edges.Count < 200) return new SkewResult(0, 0, false);
        cancel.ThrowIfCancellationRequested();

        // coarse: every half degree
        var coarse = new List<(double Angle, double Score)>();
        for (var a = -MaxDegrees; a <= MaxDegrees + 1e-9; a += 0.5) coarse.Add((a, Score(edges, w, h, a)));
        cancel.ThrowIfCancellationRequested();
        var best = coarse.MaxBy(c => c.Score);
        var scores = coarse.Select(c => c.Score).Order().ToList();
        var median = scores[scores.Count / 2];
        // a second peak: the best score at least 3° away from the best angle that is itself a local peak
        var second = coarse.Where((c, i) => Math.Abs(c.Angle - best.Angle) >= 3
                && (i == 0 || coarse[i - 1].Score <= c.Score) && (i == coarse.Count - 1 || coarse[i + 1].Score <= c.Score))
            .Select(c => c.Score).DefaultIfEmpty(0).Max();

        // fine: every 0.05° around the best, then a parabola through the best three
        var fine = new List<(double Angle, double Score)>();
        for (var a = best.Angle - 0.6; a <= best.Angle + 0.6 + 1e-9; a += 0.05) fine.Add((a, Score(edges, w, h, a)));
        var k = fine.IndexOf(fine.MaxBy(f => f.Score));
        var angle = fine[k].Angle;
        if (k > 0 && k < fine.Count - 1)
        {
            var (s0, s1, s2) = (fine[k - 1].Score, fine[k].Score, fine[k + 1].Score);
            var denominator = s0 - 2 * s1 + s2;
            if (denominator < 0) angle += 0.05 * 0.5 * (s0 - s2) / denominator;
        }
        var confidence = median > 0 ? best.Score / median : 0;
        // lines of print give scores far above the median (from about 3 up to hundreds; squared paper under a large drawing about 2); a
        // page without direction stays near 1 or gives no edges at all
        var sure = confidence >= 2 && second < best.Score * 0.8 && Math.Abs(angle) < MaxDegrees - 0.5;
        return new SkewResult(Math.Round(angle, 2), confidence, sure);
    }

    /// <summary>
    /// The bottom edges of the print on a reduced, black-and-white copy of the page: dark samples (darker than 75 % of the paper around them —
/// faint ruled or squared lines count too)
    /// with a light one below, inside the margin and outside solid dark areas. As (x, y) in work pixels.
    /// </summary>
    private static List<(short X, short Y)> Edges(ScannedImage page, out int w, out int h)
    {
        var factor = Math.Max(1, (int)Math.Round(page.Dpi / (double)WorkDpi));
        var small = Reduce(page, factor, out w, out h);
        var (ww, hh) = (w, h);
        var paper = PaperBackground.Estimate(new ScannedImage(ww, hh, 1, Math.Max(1, page.Dpi / factor), small));
        var dark = new bool[ww * hh];
        for (var y = 0; y < hh; y++)
            for (var x = 0; x < ww; x++)
                dark[y * ww + x] = small[y * ww + x] < paper.At(x, y, 0) * 0.75f;

        // solid areas: blocks of 16 × 16 more than half dark are photos or black areas, not print
        const int block = 16;
        var solid = new bool[((ww + block - 1) / block) * ((hh + block - 1) / block)];
        var cols = (ww + block - 1) / block;
        for (var by = 0; by * block < hh; by++)
            for (var bx = 0; bx < cols; bx++)
            {
                var (n, d) = (0, 0);
                for (var y = by * block; y < Math.Min(hh, (by + 1) * block); y++)
                    for (var x = bx * block; x < Math.Min(ww, (bx + 1) * block); x++)
                    {
                        n++;
                        if (dark[y * ww + x]) d++;
                    }
                solid[by * cols + bx] = d * 2 > n;
            }

        var edges = new List<(short, short)>();
        var (mx, my) = ((int)(ww * 0.04), (int)(hh * 0.04));
        for (var y = my; y < hh - my - 1; y++)
            for (var x = mx; x < ww - mx; x++)
                if (dark[y * ww + x] && !dark[(y + 1) * ww + x] && !solid[(y / block) * cols + x / block]) edges.Add(((short)x, (short)y));
        return edges;
    }

    /// <summary>The score of an angle: the edges projected along it into rows; sum of the squared differences of neighbouring rows.</summary>
    private static double Score(List<(short X, short Y)> edges, int w, int h, double degrees)
    {
        var slope = Math.Tan(degrees * Math.PI / 180);
        var reach = (int)Math.Ceiling(Math.Abs(slope) * w) + 2;
        var rows = new int[h + 2 * reach];
        foreach (var (x, y) in edges)
        {
            var r = (int)Math.Round(y - slope * (x - w / 2.0)) + reach;
            if ((uint)r < (uint)rows.Length) rows[r]++;
        }
        double sum = 0;
        for (var i = 1; i < rows.Length; i++)
        {
            double d = rows[i] - rows[i - 1];
            sum += d * d;
        }
        return sum;
    }

    /// <summary>A grey copy reduced by a whole factor (mean of the samples).</summary>
    private static byte[] Reduce(ScannedImage page, int factor, out int w, out int h)
    {
        (w, h) = (Math.Max(1, page.Width / factor), Math.Max(1, page.Height / factor));
        var (ww, c) = (w, page.Channels);
        var result = new byte[w * h];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < ww; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = ((long)(y * factor + dy) * page.Width + x * factor) * c;
                    for (var dx = 0; dx < factor; dx++)
                    {
                        var at = row + dx * c;
                        sum += c == 1 ? page.Pixels[at] : (page.Pixels[at] * 77 + page.Pixels[at + 1] * 150 + page.Pixels[at + 2] * 29) >> 8;
                    }
                }
                result[y * ww + x] = (byte)(sum / (factor * factor));
            }
        });
        return result;
    }
}
