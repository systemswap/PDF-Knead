namespace PdfKnead.Core.Scanning.Processing;

/// <summary>
/// The colour of the paper over the page, estimated coarsely: in blocks of about 6 mm the brightest tenth of the samples is paper; blocks
/// full of print take the paper of their neighbours, the map is smoothed. Shadows of the lid, the darker spine of a book and a slightly
/// uneven light stay in the map — dividing by it makes the paper even.
/// </summary>
/// <remarks>
/// Blocks clearly darker than the paper of the page (a photo, a black area) count as „no paper“ and are filled from their neighbours, so a
/// photo is not flattened to grey. A page without paper at all (dark all over) gets one even value.
/// </remarks>
public sealed class PaperBackground
{
    private readonly float[] _map;   // per block and channel, 0 … 255
    private readonly int _cols, _rows, _channels, _block;

    private PaperBackground(float[] map, int cols, int rows, int channels, int block)
    {
        (_map, _cols, _rows, _channels, _block) = (map, cols, rows, channels, block);
    }

    /// <summary>The brightness of the paper of the page as a whole (0 … 255).</summary>
    public float Paper { get; private init; }

    /// <summary>Estimates the paper of a page.</summary>
    /// <param name="page">The page.</param>
    /// <returns>The map.</returns>
    public static PaperBackground Estimate(ScannedImage page)
    {
        var (w, h, c) = (page.Width, page.Height, page.Channels);
        var block = Math.Max(16, (int)Math.Round(page.Dpi / 25.4 * 3));
        var cols = Math.Max(1, (w + block - 1) / block);
        var rows = Math.Max(1, (h + block - 1) / block);
        var map = new float[cols * rows * c];
        var level = new float[cols * rows];
        var pixels = page.Pixels;
        var step = block >= 32 ? 2 : 1;   // every other sample is enough for a block of this size
        Parallel.For(0, rows, by =>
        {
            Span<int> histogram = stackalloc int[256];
            Span<double> sums = stackalloc double[3];
            for (var bx = 0; bx < cols; bx++)
            {
                histogram.Clear();
                var (x0, y0) = (bx * block, by * block);
                var (x1, y1) = (Math.Min(w, x0 + block), Math.Min(h, y0 + block));
                var count = 0;
                for (var y = y0; y < y1; y += step)
                    for (var x = x0; x < x1; x += step)
                    {
                        histogram[Luma(pixels, ((long)y * w + x) * c, c)]++;
                        count++;
                    }
                // the brightness that 90 % of the samples reach or stay below: the paper between the print
                var p90 = 255;
                for (var seen = 0; p90 > 0; p90--)
                {
                    seen += histogram[p90];
                    if (seen >= count / 10) break;
                }
                // the colour of the paper: the mean of the samples at least that bright
                sums.Clear();
                var n = 0;
                for (var y = y0; y < y1; y += step)
                    for (var x = x0; x < x1; x += step)
                    {
                        var at = ((long)y * w + x) * c;
                        if (Luma(pixels, at, c) < p90) continue;
                        for (var ch = 0; ch < c; ch++) sums[ch] += pixels[at + ch];
                        n++;
                    }
                var i = by * cols + bx;
                level[i] = p90;
                for (var ch = 0; ch < c; ch++) map[i * c + ch] = n > 0 ? (float)(sums[ch] / n) : p90;
            }
        });

        // the paper of the page: the brightest blocks
        var sorted = (float[])level.Clone();
        Array.Sort(sorted);
        var paper = Math.Max(1f, sorted[(int)(sorted.Length * 0.9)]);
        var known = new bool[level.Length];
        for (var i = 0; i < level.Length; i++) known[i] = level[i] >= paper * 0.6f;
        if (!known.Contains(true))
        {
            // no paper anywhere: one even value, the page is left as it is relative to it
            for (var i = 0; i < map.Length; i++) map[i] = paper;
            return new PaperBackground(map, cols, rows, c, block) { Paper = paper };
        }

        // a block full of print shows less paper than there is: the brightest of its neighbours counts
        var lifted = (float[])map.Clone();
        for (var by = 0; by < rows; by++)
            for (var bx = 0; bx < cols; bx++)
            {
                var i = by * cols + bx;
                if (!known[i]) continue;
                var best = i;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var (nx, ny) = (bx + dx, by + dy);
                        if (nx < 0 || ny < 0 || nx >= cols || ny >= rows) continue;
                        var j = ny * cols + nx;
                        if (known[j] && level[j] > level[best]) best = j;
                    }
                // only a little brighter than the block itself: the neighbour may lie in a lighter part of the page (shadow at the edge)
                if (level[best] <= level[i] * 1.15f)
                    for (var ch = 0; ch < c; ch++) lifted[i * c + ch] = map[best * c + ch];
            }
        map = lifted;

        // blocks without paper take the mean of their known neighbours, ring by ring
        var neighbours = new double[3];
        for (var pass = 0; pass < cols + rows && known.Contains(false); pass++)
        {
            var next = (bool[])known.Clone();
            for (var by = 0; by < rows; by++)
                for (var bx = 0; bx < cols; bx++)
                {
                    var i = by * cols + bx;
                    if (known[i]) continue;
                    Array.Clear(neighbours);
                    var n = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var (nx, ny) = (bx + dx, by + dy);
                            if (nx < 0 || ny < 0 || nx >= cols || ny >= rows || !known[ny * cols + nx]) continue;
                            for (var ch = 0; ch < c; ch++) neighbours[ch] += map[(ny * cols + nx) * c + ch];
                            n++;
                        }
                    if (n == 0) continue;
                    for (var ch = 0; ch < c; ch++) map[i * c + ch] = (float)(neighbours[ch] / n);
                    next[i] = true;
                }
            known = next;
        }

        // smooth twice over 3 × 3 blocks: no steps between the blocks
        for (var pass = 0; pass < 2; pass++)
        {
            var smooth = new float[map.Length];
            for (var by = 0; by < rows; by++)
                for (var bx = 0; bx < cols; bx++)
                    for (var ch = 0; ch < c; ch++)
                    {
                        var (sum, n) = (0f, 0);
                        for (var dy = -1; dy <= 1; dy++)
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                var (nx, ny) = (bx + dx, by + dy);
                                if (nx < 0 || ny < 0 || nx >= cols || ny >= rows) continue;
                                sum += map[(ny * cols + nx) * c + ch];
                                n++;
                            }
                        smooth[(by * cols + bx) * c + ch] = sum / n;
                    }
            map = smooth;
        }
        return new PaperBackground(map, cols, rows, c, block) { Paper = paper };
    }

    /// <summary>The paper at a sample, interpolated between the block middles (at least 1, never 0).</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <param name="channel">Channel.</param>
    /// <returns>0 … 255.</returns>
    public float At(int x, int y, int channel)
    {
        var fx = Math.Clamp((x + 0.5f) / _block - 0.5f, 0, _cols - 1);
        var fy = Math.Clamp((y + 0.5f) / _block - 0.5f, 0, _rows - 1);
        var (x0, y0) = ((int)fx, (int)fy);
        var (x1, y1) = (Math.Min(x0 + 1, _cols - 1), Math.Min(y0 + 1, _rows - 1));
        var (tx, ty) = (fx - x0, fy - y0);
        float M(int bx, int by) => _map[(by * _cols + bx) * _channels + channel];
        var top = M(x0, y0) + (M(x1, y0) - M(x0, y0)) * tx;
        var bottom = M(x0, y1) + (M(x1, y1) - M(x0, y1)) * tx;
        return Math.Max(1, top + (bottom - top) * ty);
    }

    // ITU-R BT.601 weights in 8-bit fixed point
    private static int Luma(byte[] p, long at, int channels) =>
        channels == 1 ? p[at] : (p[at] * 77 + p[at + 1] * 150 + p[at + 2] * 29) >> 8;
}
