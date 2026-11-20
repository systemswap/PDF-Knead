namespace PdfKnead.Core.Scanning.Processing;

/// <summary>
/// What a straightening method found for a page: applied to the page at any resolution (it is found on a reduced copy, applied to the
/// preview and to the full scan alike).
/// </summary>
public abstract class StraightenPlan
{
    /// <summary>The angle the page is turned by, clockwise positive (0: not turned).</summary>
    public abstract double Degrees { get; }

    /// <summary>Applies the plan to the page (or to a reduced copy of it).</summary>
    /// <param name="page">The page.</param>
    /// <param name="cancel">Stops the work.</param>
    /// <returns>The straightened page.</returns>
    public abstract ScannedImage Apply(ScannedImage page, CancellationToken cancel = default);
}

/// <summary>Turning the page about its middle by a small angle (the corners fill with the colour of the paper).</summary>
/// <param name="degrees">The angle, clockwise positive.</param>
public sealed class RotationPlan(double degrees) : StraightenPlan
{
    /// <inheritdoc/>
    public override double Degrees { get; } = degrees;

    /// <inheritdoc/>
    public override ScannedImage Apply(ScannedImage page, CancellationToken cancel = default) => ImageGeometry.Rotate(page, Degrees, cancel);
}

/// <summary>A method of straightening offered in the scan window (KONZEPT §5.25, „Begradigen“).</summary>
public interface IStraightener
{
    /// <summary>The key kept in profiles and pages.</summary>
    string Key { get; }

    /// <summary>The name in the choice.</summary>
    string DisplayName { get; }

    /// <summary>Finds what to do with a page.</summary>
    /// <param name="page">The page (a reduced copy is enough).</param>
    /// <param name="cancel">Stops the search.</param>
    /// <returns>The plan, or null when the page stays as it is (nothing to do, or the method is not sure).</returns>
    StraightenPlan? Find(ScannedImage page, CancellationToken cancel = default);
}

/// <summary>„Nicht begradigen“.</summary>
public sealed class NoStraightening : IStraightener
{
    /// <inheritdoc/>
    public string Key => Straighteners.None;

    /// <inheritdoc/>
    public string DisplayName => Tr.T("Nicht begradigen");

    /// <inheritdoc/>
    public StraightenPlan? Find(ScannedImage page, CancellationToken cancel = default) => null;
}

/// <summary>„Schräglage ausgleichen“: only turning, and only with a sure angle (<see cref="SkewDetector"/>).</summary>
public sealed class SkewStraightening : IStraightener
{
    /// <inheritdoc/>
    public string Key => Straighteners.Skew;

    /// <inheritdoc/>
    public string DisplayName => Tr.T("Schräglage ausgleichen");

    /// <inheritdoc/>
    public StraightenPlan? Find(ScannedImage page, CancellationToken cancel = default)
    {
        var skew = SkewDetector.Detect(page, cancel);
        // below a tenth of a degree (0.35 mm over an A4 page) turning only blurs the page
        return skew.Sure && Math.Abs(skew.Degrees) >= 0.1 ? new RotationPlan(-skew.Degrees) : null;
    }
}

/// <summary>
/// The straightening methods of the scan window: the choice is filled from this list, profiles keep the key — a further method (such as a
/// book correction) only adds an entry; an unknown key means „Nicht begradigen“.
/// </summary>
public static class Straighteners
{
    /// <summary>The key of „Nicht begradigen“.</summary>
    public const string None = "none";

    /// <summary>The key of „Schräglage ausgleichen“.</summary>
    public const string Skew = "skew";

    /// <summary>The key of „Buchseite entzerren“.</summary>
    public const string Book = "book";

    /// <summary>All methods, in the order of the choice.</summary>
    public static IReadOnlyList<IStraightener> All { get; } = [new NoStraightening(), new SkewStraightening(), new BookStraightening()];

    /// <summary>The method of a key; an unknown key gives „Nicht begradigen“.</summary>
    public static IStraightener For(string? key) => All.FirstOrDefault(s => s.Key == key) ?? All[0];
}

/// <summary>Turning, cropping and finding dark borders on scanned pages.</summary>
public static class ImageGeometry
{
    /// <summary>
    /// Turns a page about its middle (bilinear, same size); what comes in at the corners takes the colour of the paper near the edge.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="degrees">The angle, clockwise positive.</param>
    /// <param name="cancel">Stops the work.</param>
    /// <returns>The turned page; the page itself for 0.</returns>
    public static ScannedImage Rotate(ScannedImage page, double degrees, CancellationToken cancel = default)
    {
        if (Math.Abs(degrees) < 1e-6) return page;
        var (w, h, c) = (page.Width, page.Height, page.Channels);
        var fill = PaperColour(page);
        var (sin, cos) = Math.SinCos(-degrees * Math.PI / 180);
        var (cx, cy) = ((w - 1) / 2.0, (h - 1) / 2.0);
        var result = new byte[(long)w * h * c];
        var source = page.Pixels;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var sx = cx + (x - cx) * cos - (y - cy) * sin;
                var sy = cy + (x - cx) * sin + (y - cy) * cos;
                var at = ((long)y * w + x) * c;
                if (sx < 0 || sy < 0 || sx > w - 1 || sy > h - 1)
                {
                    for (var ch = 0; ch < c; ch++) result[at + ch] = fill[ch];
                    continue;
                }
                var (x0, y0) = ((int)sx, (int)sy);
                var (x1, y1) = (Math.Min(x0 + 1, w - 1), Math.Min(y0 + 1, h - 1));
                var (fx, fy) = (sx - x0, sy - y0);
                for (var ch = 0; ch < c; ch++)
                {
                    double P(int px, int py) => source[((long)py * w + px) * c + ch];
                    var v = (P(x0, y0) * (1 - fx) + P(x1, y0) * fx) * (1 - fy) + (P(x0, y1) * (1 - fx) + P(x1, y1) * fx) * fy;
                    result[at + ch] = (byte)(v + 0.5);
                }
            }
        });
        return page with { Pixels = result, BlackWhite = false };
    }

    /// <summary>The colour of the paper: the brightest tenth of the samples near the edges (inside the outermost 2 %).</summary>
    private static byte[] PaperColour(ScannedImage page)
    {
        var (w, h, c) = (page.Width, page.Height, page.Channels);
        var samples = new List<(int Lum, int Index)>();
        var (mx, my) = (Math.Max(1, w / 50), Math.Max(1, h / 50));
        void Add(int x, int y)
        {
            var at = ((long)y * w + x) * c;
            var lum = c == 1 ? page.Pixels[at] : (page.Pixels[at] * 77 + page.Pixels[at + 1] * 150 + page.Pixels[at + 2] * 29) >> 8;
            samples.Add((lum, (int)(at / c)));
        }
        for (var x = 0; x < w; x += 4)
        {
            Add(x, Math.Min(h - 1, my * 2));
            Add(x, Math.Max(0, h - 1 - my * 2));
        }
        for (var y = 0; y < h; y += 4)
        {
            Add(Math.Min(w - 1, mx * 2), y);
            Add(Math.Max(0, w - 1 - mx * 2), y);
        }
        var bright = samples.OrderByDescending(s => s.Lum).Take(Math.Max(1, samples.Count / 10)).ToList();
        var fill = new byte[c];
        for (var ch = 0; ch < c; ch++) fill[ch] = (byte)bright.Average(s => page.Pixels[(long)s.Index * c + ch]);
        return fill;
    }

    /// <summary>A part of a page.</summary>
    /// <param name="page">The page.</param>
    /// <param name="left">Columns cut away on the left.</param>
    /// <param name="top">Rows cut away at the top.</param>
    /// <param name="right">Columns cut away on the right.</param>
    /// <param name="bottom">Rows cut away at the bottom.</param>
    /// <returns>The rest of the page (at least one sample).</returns>
    public static ScannedImage Crop(ScannedImage page, int left, int top, int right, int bottom)
    {
        var (w, h, c) = (page.Width, page.Height, page.Channels);
        left = Math.Clamp(left, 0, w - 1);
        top = Math.Clamp(top, 0, h - 1);
        var nw = Math.Max(1, w - left - Math.Clamp(right, 0, w - 1 - left));
        var nh = Math.Max(1, h - top - Math.Clamp(bottom, 0, h - 1 - top));
        if (nw == w && nh == h) return page;
        var result = new byte[(long)nw * nh * c];
        for (var y = 0; y < nh; y++)
            Array.Copy(page.Pixels, ((long)(y + top) * w + left) * c, result, (long)y * nw * c, (long)nw * c);
        return page with { Width = nw, Height = nh, Pixels = result };
    }

    /// <summary>
    /// The dark borders of a page, as shares of its width and height to cut away on each side: from each edge inwards, line by line, as long
    /// as a line is mostly dark (more than half its samples darker than half the paper) — the edge of the glass, the shadow of the lid, a dark
    /// lid around a smaller sheet. At most a quarter of each side; a page without dark border gives zero everywhere.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <returns>Left, top, right and bottom as shares (0 … 0.25).</returns>
    public static (double Left, double Top, double Right, double Bottom) DarkBorders(ScannedImage page)
    {
        var (w, h, c) = (page.Width, page.Height, page.Channels);
        int Lum(long at) => c == 1 ? page.Pixels[at] : (page.Pixels[at] * 77 + page.Pixels[at + 1] * 150 + page.Pixels[at + 2] * 29) >> 8;
        // the paper: the brightness that 90 % of a sparse sample reach or stay below
        var sample = new List<int>();
        for (var y = h / 8; y < h - h / 8; y += Math.Max(1, h / 64))
            for (var x = w / 8; x < w - w / 8; x += Math.Max(1, w / 64)) sample.Add(Lum(((long)y * w + x) * c));
        if (sample.Count == 0) return (0, 0, 0, 0);
        sample.Sort();
        var dark = sample[(int)(sample.Count * 0.9)] / 2;
        bool RowDark(int y)
        {
            var n = 0;
            for (var x = 0; x < w; x += 2) if (Lum(((long)y * w + x) * c) < dark) n++;
            return n * 2 > (w + 1) / 2;
        }
        bool ColumnDark(int x)
        {
            var n = 0;
            for (var y = 0; y < h; y += 2) if (Lum(((long)y * w + x) * c) < dark) n++;
            return n * 2 > (h + 1) / 2;
        }
        int Inward(int length, Func<int, bool> isDark, bool fromEnd)
        {
            var limit = length / 4;
            var count = 0;
            while (count < limit && isDark(fromEnd ? length - 1 - count : count)) count++;
            // a line or two more: the soft edge between border and paper
            return count == 0 ? 0 : Math.Min(limit, count + Math.Max(1, length / 400));
        }
        return (Inward(w, ColumnDark, false) / (double)w, Inward(h, RowDark, false) / (double)h,
            Inward(w, ColumnDark, true) / (double)w, Inward(h, RowDark, true) / (double)h);
    }
}

/// <summary>What straightening does to one page: the plan of the method and the dark borders to cut away (as shares of the straightened page).</summary>
/// <param name="Plan">The plan; null when the page is not turned or warped.</param>
/// <param name="Crop">The shares to cut away on the left, top, right and bottom.</param>
public sealed record PageStraightening(StraightenPlan? Plan, (double Left, double Top, double Right, double Bottom) Crop)
{
    /// <summary>Nothing to do.</summary>
    public static readonly PageStraightening None = new(null, (0, 0, 0, 0));

    /// <summary>The longest edge of the copy the geometry is found on.</summary>
    public const int FindEdge = 2000;

    /// <summary>Whether the page changes (turned, warped or cut).</summary>
    public bool ChangesPage => Plan is not null || Crop != (0, 0, 0, 0);

    /// <summary>
    /// Finds the geometry of a page on a reduced copy: the plan of the method (or the given angle), then the dark borders of the straightened
    /// copy.
    /// </summary>
    /// <param name="page">The page as scanned (or a reduced copy).</param>
    /// <param name="method">The key of the straightening method.</param>
    /// <param name="cropBorders">Whether dark borders are cut away.</param>
    /// <param name="manualDegrees">An angle set by hand (replaces what the method finds); null: the method decides.</param>
    /// <param name="cancel">Stops the search.</param>
    /// <returns>The geometry.</returns>
    public static PageStraightening Find(ScannedImage page, string method, bool cropBorders, double? manualDegrees, CancellationToken cancel = default)
    {
        var small = Shrink(page, FindEdge);
        var plan = manualDegrees is { } m ? (Math.Abs(m) < 1e-6 ? null : new RotationPlan(m)) : Straighteners.For(method).Find(small, cancel);
        cancel.ThrowIfCancellationRequested();
        var crop = cropBorders ? ImageGeometry.DarkBorders(plan?.Apply(small, cancel) ?? small) : (0, 0, 0, 0);
        return new PageStraightening(plan, crop);
    }

    /// <summary>Applies the geometry to the page or to a reduced copy of it.</summary>
    /// <param name="page">The page.</param>
    /// <param name="cancel">Stops the work.</param>
    /// <returns>The straightened and cut page.</returns>
    public ScannedImage Apply(ScannedImage page, CancellationToken cancel = default)
    {
        var straight = Plan?.Apply(page, cancel) ?? page;
        var (w, h) = (straight.Width, straight.Height);
        return Crop == (0, 0, 0, 0) ? straight : ImageGeometry.Crop(straight, (int)Math.Round(Crop.Left * w), (int)Math.Round(Crop.Top * h),
            (int)Math.Round(Crop.Right * w), (int)Math.Round(Crop.Bottom * h));
    }

    /// <summary>A copy reduced by a whole factor so that its longest edge is at most <paramref name="edge"/>.</summary>
    private static ScannedImage Shrink(ScannedImage page, int edge)
    {
        var longest = Math.Max(page.Width, page.Height);
        if (longest <= edge) return page;
        var factor = (int)Math.Ceiling(longest / (double)edge);
        var (w, h, c) = (page.Width / factor, page.Height / factor, page.Channels);
        var result = new byte[(long)w * h * c];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
                for (var k = 0; k < c; k++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < factor; dy++)
                    {
                        var row = ((long)(y * factor + dy) * page.Width + x * factor) * c + k;
                        for (var dx = 0; dx < factor; dx++) sum += page.Pixels[row + dx * c];
                    }
                    result[((long)y * w + x) * c + k] = (byte)(sum / (factor * factor));
                }
        });
        return new ScannedImage(w, h, c, Math.Max(1, page.Dpi / factor), result);
    }
}
