namespace PdfKnead.Core.Pdf.Filters.Jpx;

/// <summary>
/// Inverse discrete wavelet transform (ISO/IEC 15444-1, Annex F): 5/3 in integers, 9/7 with lifting; first every row, then every
/// column (2D_SR), with symmetric extension at the edges. Also the component transforms RCT and ICT (Annex G).
/// </summary>
internal static class JpxWavelet
{
    private const int Pad = 4;

    private const float Alpha = -1.586134342059924f, Beta = -0.052980118572961f, Gamma = 0.882911075530934f, Delta = 0.443506852043971f;
    private const float K = 1.230174104914001f, InvK = 1 / K;

    /// <summary>Columns are processed in strips of this width: whole rows of the strip at once (cache, vectors).</summary>
    private const int Strip = 32;

    /// <summary>Applies the inverse wavelet transform to a tile-component up to resolution <see cref="JpxTileComponent.Keep"/>, in place.</summary>
    public static void Inverse(JpxTileComponent tc)
    {
        if (tc.Keep == 0) return;
        var top = tc.Resolutions[tc.Keep];
        var stride = top.Width;
        if (stride == 0 || top.Height == 0) return;
        var data = tc.Data;
        var reversible = tc.Coding.Reversible;
        for (var r = 1; r <= tc.Keep; r++)
        {
            var res = tc.Resolutions[r];
            var lower = tc.Resolutions[r - 1];
            int w = res.Width, h = res.Height;
            if (w == 0 || h == 0) continue;
            // Small levels without threads (distributing the work would cost more than the work itself)
            var parallel = (long)w * h >= 1 << 16;

            // Rows: low-pass on the left (lower.Width), high-pass on the right
            var oddX = res.X0 & 1;
            var lowWidth = lower.Width;
            Chunks(h, 16, parallel, () => new float[w + 2 * Pad], (from, to, line) =>
            {
                for (var y = from; y < to; y++)
                {
                    var row = y * stride;
                    for (var j = 0; j < w; j++)
                    {
                        var low = ((oddX + j) & 1) == 0;
                        line[Pad + j] = data[row + (low ? j >> 1 : lowWidth + (j >> 1))];
                    }
                    Lift(line, w, oddX, reversible);
                    Array.Copy(line, Pad, data, row, w);
                }
            });

            // Columns in strips: low-pass at the top (lower.Height), high-pass at the bottom
            var oddY = res.Y0 & 1;
            var lowHeight = lower.Height;
            Chunks((w + Strip - 1) / Strip, 1, parallel, () => new float[(h + 2 * Pad) * Strip], (from, to, buffer) =>
            {
                for (var strip = from; strip < to; strip++)
                {
                    var x0 = strip * Strip;
                    var s = Math.Min(Strip, w - x0);
                    for (var j = 0; j < h; j++)
                    {
                        var low = ((oddY + j) & 1) == 0;
                        Array.Copy(data, (low ? j >> 1 : lowHeight + (j >> 1)) * stride + x0, buffer, (Pad + j) * Strip, s);
                    }
                    LiftStrip(buffer, h, s, oddY, reversible);
                    for (var j = 0; j < h; j++) Array.Copy(buffer, (Pad + j) * Strip, data, j * stride + x0, s);
                }
            });
        }
    }

    /// <summary>Runs over [0, count) in pieces of <paramref name="chunk"/>, distributed over the cores; one buffer per thread.</summary>
    private static void Chunks(int count, int chunk, bool parallel, Func<float[]> buffer, Action<int, int, float[]> body)
    {
        if (!parallel)
        {
            body(0, count, buffer());
            return;
        }
        var pieces = (count + chunk - 1) / chunk;
        Parallel.For(0, pieces, buffer, (i, _, b) =>
        {
            body(i * chunk, Math.Min(count, (i + 1) * chunk), b);
            return b;
        }, _ => { });
    }

    /// <summary>
    /// Like <see cref="Lift"/>, but for <paramref name="s"/> columns at once: row k of the strip lies in
    /// <paramref name="b"/>[k · Strip …]. Every step processes whole rows (vectors).
    /// </summary>
    private static void LiftStrip(float[] b, int n, int s, int odd, bool reversible)
    {
        if (n == 1)
        {
            if (odd == 1)
                for (var c = 0; c < s; c++) b[Pad * Strip + c] = reversible ? MathF.Truncate(b[Pad * Strip + c] / 2) : b[Pad * Strip + c] / 2;
            return;
        }
        var period = 2 * (n - 1);
        for (var k = 1; k <= Pad; k++)
        {
            Array.Copy(b, (Pad + Mirror(-k, n, period)) * Strip, b, (Pad - k) * Strip, s);
            Array.Copy(b, (Pad + Mirror(n - 1 + k, n, period)) * Strip, b, (Pad + n - 1 + k) * Strip, s);
        }
        var length = n + 2 * Pad;
        var lowStart = odd;   // Pad is even: row k is low-pass if (odd + k) is even
        var highStart = 1 - odd;

        if (reversible)
        {
            for (var k = lowStart == 0 ? 2 : 1; k < length - 1; k += 2) RowStep53(b, k, s, low: true);
            for (var k = highStart == 0 ? 2 : 1; k < length - 1; k += 2) RowStep53(b, k, s, low: false);
            return;
        }
        for (var k = lowStart; k < length; k += 2) RowScale(b, k, s, K);
        for (var k = highStart; k < length; k += 2) RowScale(b, k, s, InvK);
        for (var k = lowStart == 0 ? 2 : 1; k < length - 1; k += 2) RowStep(b, k, s, Delta);
        for (var k = highStart == 0 ? 2 : 1; k < length - 1; k += 2) RowStep(b, k, s, Gamma);
        for (var k = lowStart == 0 ? 2 : 1; k < length - 1; k += 2) RowStep(b, k, s, Beta);
        for (var k = highStart == 0 ? 2 : 1; k < length - 1; k += 2) RowStep(b, k, s, Alpha);
    }

    private static void RowScale(float[] b, int k, int s, float factor)
    {
        var at = k * Strip;
        var c = 0;
        var v = System.Numerics.Vector<float>.Count;
        for (; c + v <= s; c += v)
            (new System.Numerics.Vector<float>(b, at + c) * factor).CopyTo(b, at + c);
        for (; c < s; c++) b[at + c] *= factor;
    }

    private static void RowStep(float[] b, int k, int s, float factor)
    {
        int at = k * Strip, up = at - Strip, down = at + Strip;
        var c = 0;
        var v = System.Numerics.Vector<float>.Count;
        for (; c + v <= s; c += v)
        {
            var sum = new System.Numerics.Vector<float>(b, up + c) + new System.Numerics.Vector<float>(b, down + c);
            (new System.Numerics.Vector<float>(b, at + c) - sum * factor).CopyTo(b, at + c);
        }
        for (; c < s; c++) b[at + c] -= factor * (b[up + c] + b[down + c]);
    }

    /// <summary>5/3: low-pass X −= ⌊(left + right + 2) / 4⌋, high-pass X += ⌊(left + right) / 2⌋.</summary>
    private static void RowStep53(float[] b, int k, int s, bool low)
    {
        int at = k * Strip, up = at - Strip, down = at + Strip;
        var c = 0;
        var v = System.Numerics.Vector<float>.Count;
        for (; c + v <= s; c += v)
        {
            var sum = new System.Numerics.Vector<float>(b, up + c) + new System.Numerics.Vector<float>(b, down + c);
            var x = new System.Numerics.Vector<float>(b, at + c);
            x = low ? x - System.Numerics.Vector.Floor((sum + new System.Numerics.Vector<float>(2f)) * 0.25f)
                    : x + System.Numerics.Vector.Floor(sum * 0.5f);
            x.CopyTo(b, at + c);
        }
        for (; c < s; c++)
            b[at + c] = low ? b[at + c] - MathF.Floor((b[up + c] + b[down + c] + 2) * 0.25f) : b[at + c] + MathF.Floor((b[up + c] + b[down + c]) * 0.5f);
    }

    /// <summary>
    /// One row or column (1D_SR): values in <paramref name="x"/>[Pad … Pad + n), interleaved (low-pass at even
    /// absolute positions). <paramref name="odd"/>: the first position is odd.
    /// </summary>
    internal static void Lift(float[] x, int n, int odd, bool reversible)
    {
        if (n == 1)
        {
            // A single high-pass value (ISO/IEC 15444-1, F.3.7)
            if (odd == 1) x[Pad] = reversible ? MathF.Truncate(x[Pad] / 2) : x[Pad] / 2;
            return;
        }
        // Extend symmetrically at the edges (period 2(n − 1))
        var period = 2 * (n - 1);
        for (var k = 1; k <= Pad; k++)
        {
            x[Pad - k] = x[Pad + Mirror(-k, n, period)];
            x[Pad + n - 1 + k] = x[Pad + Mirror(n - 1 + k, n, period)];
        }
        var length = n + 2 * Pad;
        // First index with low-pass (even absolute position) and with high-pass
        var lowStart = (odd + Pad) & 1;   // Pad is even: index k is low-pass if (odd + k) is even
        var highStart = 1 - lowStart;

        if (reversible)
        {
            for (var k = lowStart == 0 ? 2 : 1; k < length - 1; k += 2)
                x[k] -= MathF.Floor((x[k - 1] + x[k + 1] + 2) * 0.25f);
            for (var k = highStart == 0 ? 2 : 1; k < length - 1; k += 2)
                x[k] += MathF.Floor((x[k - 1] + x[k + 1]) * 0.5f);
            return;
        }

        for (var k = lowStart; k < length; k += 2) x[k] *= K;
        for (var k = highStart; k < length; k += 2) x[k] *= InvK;
        Step(x, length, lowStart, Delta);
        Step(x, length, highStart, Gamma);
        Step(x, length, lowStart, Beta);
        Step(x, length, highStart, Alpha);
    }

    private static void Step(float[] x, int length, int start, float factor)
    {
        for (var k = start == 0 ? 2 : 1; k < length - 1; k += 2)
            x[k] -= factor * (x[k - 1] + x[k + 1]);
    }

    private static int Mirror(int i, int n, int period)
    {
        i %= period;
        if (i < 0) i += period;
        return i >= n ? period - i : i;
    }

    /// <summary>Inverse component transform over the first three components (of equal size), in place: RCT if reversible, otherwise ICT.</summary>
    public static void InverseColor(float[] y0, float[] y1, float[] y2, int count, bool reversible)
    {
        if (reversible)
        {
            for (var i = 0; i < count; i++)
            {
                var g = y0[i] - MathF.Floor((y2[i] + y1[i]) * 0.25f);
                var r = y2[i] + g;
                var b = y1[i] + g;
                y0[i] = r;
                y1[i] = g;
                y2[i] = b;
            }
            return;
        }
        for (var i = 0; i < count; i++)
        {
            float y = y0[i], cb = y1[i], cr = y2[i];
            y0[i] = y + 1.402f * cr;
            y1[i] = y - 0.34413f * cb - 0.71414f * cr;
            y2[i] = y + 1.772f * cb;
        }
    }
}
