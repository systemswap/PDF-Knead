namespace PdfKnead.Core.Scanning.Processing;

/// <summary>
/// Show-through with the back of the sheet (KONZEPT §5.25, „Durchscheinen verhindern“, both sides): where the back is printed, its print
/// shines through the front, mirrored. The back is mirrored and laid onto the front, the strength of the show-through is measured where the
/// front is blank and the back printed, and the front is divided by the transmission there — print of the front itself stays, it is far
/// darker than the show-through.
/// </summary>
/// <remarks>
/// <para>Model (G. Sharma, „Show-through cancellation in scans of duplex printed documents“, IEEE TIP 10(5), 2001, simplified): relative to its
/// paper, the front seen is the front printed times the transmission of the back, t = 1 − α · (1 − b), b the back relative to its paper
/// (blurred: light spreads in the paper), α the strength of the show-through.</para>
/// <para>Laying on: both sides reduced; the back mirrored left–right or top–bottom (how the sheet was turned) and shifted by up to 4 % of the
/// size; the arrangement with the highest correlation of the faint tones of the front with the print of the back wins. Without clear show-
/// through (α small or correlation weak) nothing changes.</para>
/// </remarks>
public sealed class ShowThroughBack
{
    private readonly float[] _back;   // the back relative to its paper, blurred, mirrored and shifted onto the front, at the reduced size
    private readonly int _w, _h;      // the reduced size
    private readonly double _scale;   // reduced / front

    private ShowThroughBack(float[] back, int w, int h, double scale, double strength)
    {
        (_back, _w, _h, _scale, Strength) = (back, w, h, scale, strength);
    }

    /// <summary>The strength of the show-through (α): 0 … 0.6.</summary>
    public double Strength { get; }

    // the reduced size: about 75 dpi is enough for the blurred show-through and the laying on
    private const int WorkDpi = 75;

    /// <summary>Prepares the back for a front; null when the back shows no clear show-through on the front.</summary>
    /// <param name="front">The front (straightened as it will be processed).</param>
    /// <param name="back">The back of the same sheet (straightened).</param>
    /// <param name="cancel">Stops the work.</param>
    /// <returns>The prepared back, or null.</returns>
    public static ShowThroughBack? Prepare(ScannedImage front, ScannedImage back, CancellationToken cancel = default)
    {
        var factor = Math.Max(1, (int)Math.Round(front.Dpi / (double)WorkDpi));
        var (w, h) = (Math.Max(8, front.Width / factor), Math.Max(8, front.Height / factor));
        var f = Relative(front, w, h);
        var b = Relative(back, w, h);
        Blur(b, w, h, 1);
        cancel.ThrowIfCancellationRequested();

        // faint tones of the front (between print and paper) and print of the back
        var faint = f.Select(v => v is > 0.55f and < 0.98f ? 1 - v : 0).ToArray();
        var ink = b.Select(v => Math.Clamp(1 - v, 0, 1)).ToArray();
        // coarse on a copy reduced by four (all three ways of turning, shifts up to 4 % of the size), then fine around the best
        const int coarse = 4;
        var (cw, ch) = (Math.Max(4, w / coarse), Math.Max(4, h / coarse));
        var (cf, ci) = (Shrink(faint, w, h, cw, ch), Shrink(ink, w, h, cw, ch));
        var reach = Math.Max(2, (int)(Math.Max(cw, ch) * 0.04));
        var best = (Score: double.MinValue, MirrorX: false, MirrorY: false, Dx: 0, Dy: 0);
        foreach (var (mx, my) in new[] { (true, false), (false, true), (true, true) })
            for (var dy = -reach; dy <= reach; dy++)
                for (var dx = -reach; dx <= reach; dx++)
                {
                    var score = Correlation(cf, ci, cw, ch, mx, my, dx, dy, 1);
                    if (score > best.Score) best = (score, mx, my, dx, dy);
                }
        var (cdx, cdy) = (best.Dx * coarse, best.Dy * coarse);
        best.Score = double.MinValue;
        for (var dy = cdy - coarse; dy <= cdy + coarse; dy++)
            for (var dx = cdx - coarse; dx <= cdx + coarse; dx++)
            {
                var score = Correlation(faint, ink, w, h, best.MirrorX, best.MirrorY, dx, dy, 2);
                if (score > best.Score) best = (score, best.MirrorX, best.MirrorY, dx, dy);
            }
        cancel.ThrowIfCancellationRequested();

        // the back laid onto the front
        var laid = new float[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var bx = (best.MirrorX ? w - 1 - x : x) + best.Dx;
                var by = (best.MirrorY ? h - 1 - y : y) + best.Dy;
                laid[y * w + x] = (uint)bx < (uint)w && (uint)by < (uint)h ? b[by * w + bx] : 1;
            }

        // the strength: where the back is clearly printed and the front would be blank (light), α = (1 − f) / (1 − b)
        var ratios = new List<double>();
        for (var i = 0; i < laid.Length; i++)
            if (laid[i] < 0.6f && f[i] > 0.6f) ratios.Add((1 - f[i]) / (1 - laid[i]));
        if (ratios.Count < w * h / 400) return null;
        ratios.Sort();
        var alpha = ratios[ratios.Count / 2];
        // the faint tones must really follow the back: compare with the same tones where the back is blank
        var elsewhere = new List<double>();
        for (var i = 0; i < laid.Length; i++)
            if (laid[i] > 0.95f && f[i] > 0.6f) elsewhere.Add(1 - f[i]);
        elsewhere.Sort();
        var baseline = elsewhere.Count > 0 ? elsewhere[elsewhere.Count / 2] : 0;
        if (alpha < 0.03 || (1 - laid.Where(v => v < 0.6f).DefaultIfEmpty(1).Average()) * alpha < baseline * 1.5) return null;
        return new ShowThroughBack(laid, w, h, w / (double)front.Width, Math.Min(0.6, alpha));
    }

    /// <summary>The transmission at a sample of the front: 1 where the back is blank, lower where it is printed.</summary>
    /// <param name="x">Column of the front.</param>
    /// <param name="y">Row of the front.</param>
    /// <param name="amount">How much of the measured show-through is taken out (0 … 1).</param>
    /// <returns>0.4 … 1.</returns>
    public float Transmission(int x, int y, float amount)
    {
        var fx = Math.Clamp(x * _scale, 0, _w - 1.001);
        var fy = Math.Clamp(y * _scale, 0, _h - 1.001);
        var (x0, y0) = ((int)fx, (int)fy);
        var (tx, ty) = ((float)(fx - x0), (float)(fy - y0));
        float B(int px, int py) => _back[py * _w + px];
        var b = (B(x0, y0) * (1 - tx) + B(x0 + 1, y0) * tx) * (1 - ty) + (B(x0, y0 + 1) * (1 - tx) + B(x0 + 1, y0 + 1) * tx) * ty;
        return Math.Max(0.4f, 1 - amount * (float)Strength * (1 - Math.Clamp(b, 0, 1)));
    }

    /// <summary>How well the faint tones of the front follow the print of the back laid on in this way.</summary>
    private static double Correlation(float[] faint, float[] ink, int w, int h, bool mirrorX, bool mirrorY, int dx, int dy, int step)
    {
        double sum = 0;
        for (var y = 0; y < h; y += step)
        {
            var by = (mirrorY ? h - 1 - y : y) + dy;
            if ((uint)by >= (uint)h) continue;
            for (var x = 0; x < w; x += step)
            {
                var bx = (mirrorX ? w - 1 - x : x) + dx;
                if ((uint)bx < (uint)w) sum += faint[y * w + x] * ink[by * w + bx];
            }
        }
        return sum;
    }

    /// <summary>A map reduced by block means.</summary>
    private static float[] Shrink(float[] v, int w, int h, int nw, int nh)
    {
        var result = new float[nw * nh];
        var counts = new int[nw * nh];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = Math.Min(nh - 1, y * nh / h) * nw + Math.Min(nw - 1, x * nw / w);
                result[i] += v[y * w + x];
                counts[i]++;
            }
        for (var i = 0; i < result.Length; i++) result[i] /= Math.Max(1, counts[i]);
        return result;
    }

    /// <summary>A page reduced to the work size, relative to its own paper (1 = paper).</summary>
    private static float[] Relative(ScannedImage page, int w, int h)
    {
        var grey = new byte[w * h];
        var (sx, sy, c) = (page.Width / (double)w, page.Height / (double)h, page.Channels);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (px, py) = (Math.Min(page.Width - 1, (int)((x + 0.5) * sx)), Math.Min(page.Height - 1, (int)((y + 0.5) * sy)));
                var at = ((long)py * page.Width + px) * c;
                grey[y * w + x] = c == 1 ? page.Pixels[at] : (byte)((page.Pixels[at] * 77 + page.Pixels[at + 1] * 150 + page.Pixels[at + 2] * 29) >> 8);
            }
        var paper = PaperBackground.Estimate(new ScannedImage(w, h, 1, Math.Max(1, (int)(page.Dpi / sx)), grey));
        var result = new float[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) result[y * w + x] = Math.Min(1.1f, grey[y * w + x] / paper.At(x, y, 0));
        return result;
    }

    private static void Blur(float[] v, int w, int h, int r)
    {
        var copy = (float[])v.Clone();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (sum, n) = (0f, 0);
                for (var dy = -r; dy <= r; dy++)
                    for (var dx = -r; dx <= r; dx++)
                    {
                        var (px, py) = (x + dx, y + dy);
                        if ((uint)px >= (uint)w || (uint)py >= (uint)h) continue;
                        sum += copy[py * w + px];
                        n++;
                    }
                v[y * w + x] = sum / n;
            }
    }
}
