namespace PdfKnead.Core.Imaging;

/// <summary>
/// Ballpoint pen texture for filled shapes (outlines of a handwriting font, drawn strokes): how densely the ink lies at
/// each point, modelled on measurements of scanned ballpoint pen writing (the reference ink sample; see docs/FEHLERGEDAECHTNIS.md, No. 38): fine,
/// barely directional grain, slow variation over a little more than one stroke width, a faint dot screen as in the scan, a slightly
/// denser rim and a lighter centre in wide strokes. The values are then mapped onto the measured density distribution of the reference ink sample per
/// stroke direction and thickness: vertical downstrokes dense, upstrokes towards the upper right light, like pressure on the pen.
/// All lengths depend on the stroke width (the grain is limited relative to the size of the content), so the texture looks the same
/// at every resolution, only sharper. The same seed gives the same pattern.
/// </summary>
public static class PenTexture
{
    /// <summary>Number of density levels: 0 = paper, <c>Levels − 1</c> = full ink.</summary>
    public const int Levels = 64;

    // Weights of the components (chosen by comparison with the reference ink sample in an experiment)
    private const double FineWeight = 0.87, FineScale = 0.7;
    private const double SlowWeight = 0.45, SlowOfWidth = 1.5;
    private const double RimWeight = 0.6, RimOfWidth = 0.12;
    private const double DotWeight = 0.12, DotPeriod = 3;
    private const double GrooveWeight = 0.3;

    /// <summary>
    /// Density distribution (0–255) of the reference ink sample per stroke direction (6 sectors of 30°, centred at 15°, 45° …, angle of the line
    /// against the horizontal, positive towards the upper right) and thickness (thin, thick): 101 quantiles each (0, 1, … 100 %).
    /// Measured on the sample text "Max Mustermann" in scanned ballpoint pen writing (the test <c>InkTextureTests.TargetTable_AscendsAndIsDenserInDownstrokes</c> checks its shape).
    /// </summary>
    internal static readonly byte[] Target =
    [
        0, 0, 4, 8, 12, 16, 16, 20, 20, 24, 24, 28, 28, 32, 32, 32, 36, 36, 40, 40, 40, 45, 45, 49, 49, 49, 53, 53, 53, 57, 57, 57, 61, 61, 61, 65, 65, 65, 69, 69, 69, 73, 77, 77, 77, 77, 81, 81, 85, 85, 85, 89, 89, 89, 93, 93, 97, 97, 97, 101, 101, 105, 105, 105, 109, 109, 109, 113, 113, 117, 117, 121, 121, 125, 125, 130, 130, 134, 138, 142, 146, 146, 150, 154, 158, 162, 166, 174, 182, 190, 194, 210, 231, 239, 255, 255, 255, 255, 255, 255, 255,
        0, 8, 16, 20, 24, 28, 32, 32, 36, 36, 40, 40, 45, 45, 49, 49, 49, 53, 53, 57, 57, 57, 61, 61, 61, 65, 65, 65, 69, 69, 73, 73, 77, 77, 77, 77, 81, 81, 81, 85, 85, 85, 85, 85, 89, 89, 89, 89, 93, 93, 93, 97, 97, 97, 97, 101, 101, 105, 105, 105, 109, 109, 109, 113, 113, 113, 113, 113, 117, 117, 121, 125, 125, 125, 130, 130, 134, 138, 138, 142, 142, 146, 150, 150, 154, 158, 162, 166, 178, 186, 194, 206, 219, 243, 255, 255, 255, 255, 255, 255, 255,
        0, 0, 0, 0, 4, 4, 4, 8, 8, 12, 12, 12, 16, 16, 16, 20, 20, 20, 20, 24, 24, 24, 24, 28, 28, 28, 32, 32, 32, 32, 32, 36, 36, 36, 36, 40, 40, 40, 45, 45, 45, 49, 49, 49, 49, 53, 53, 53, 53, 57, 57, 57, 61, 61, 61, 61, 61, 65, 65, 65, 69, 69, 73, 77, 77, 77, 77, 81, 85, 85, 85, 85, 89, 93, 93, 97, 101, 101, 109, 109, 113, 117, 117, 121, 125, 130, 134, 138, 146, 158, 162, 166, 186, 194, 223, 235, 255, 255, 255, 255, 255,
        0, 0, 4, 4, 8, 12, 12, 16, 16, 20, 20, 20, 24, 24, 28, 28, 32, 32, 32, 32, 36, 36, 36, 40, 40, 40, 45, 45, 45, 49, 49, 53, 53, 53, 53, 57, 57, 57, 61, 61, 61, 65, 65, 65, 65, 69, 69, 73, 73, 77, 77, 77, 77, 81, 81, 85, 85, 89, 89, 89, 89, 93, 93, 97, 97, 101, 101, 101, 105, 105, 109, 109, 113, 113, 117, 117, 121, 125, 125, 125, 130, 134, 138, 142, 146, 146, 150, 158, 158, 166, 174, 190, 194, 210, 231, 247, 255, 255, 255, 255, 255,
        0, 4, 12, 16, 20, 24, 24, 28, 32, 36, 36, 36, 36, 40, 45, 45, 49, 49, 53, 53, 53, 53, 53, 57, 57, 61, 65, 65, 65, 69, 69, 69, 73, 77, 77, 77, 77, 81, 81, 85, 85, 89, 89, 93, 93, 93, 93, 93, 97, 101, 101, 101, 105, 105, 109, 109, 109, 109, 113, 113, 117, 121, 121, 125, 130, 130, 130, 130, 130, 134, 142, 146, 150, 150, 150, 154, 158, 166, 166, 174, 178, 182, 186, 186, 190, 198, 215, 231, 235, 243, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        0, 0, 4, 8, 12, 16, 16, 20, 20, 24, 24, 28, 28, 28, 32, 32, 36, 36, 36, 40, 40, 40, 45, 45, 49, 49, 53, 53, 53, 53, 57, 57, 57, 61, 61, 61, 61, 65, 65, 65, 69, 69, 69, 73, 77, 77, 77, 81, 81, 85, 85, 85, 89, 89, 93, 93, 93, 97, 97, 97, 97, 101, 105, 105, 105, 109, 109, 113, 113, 117, 117, 121, 121, 125, 125, 125, 130, 134, 138, 142, 146, 146, 150, 154, 158, 162, 166, 174, 182, 190, 198, 210, 231, 235, 251, 255, 255, 255, 255, 255, 255,
        0, 0, 4, 8, 12, 16, 16, 20, 20, 24, 24, 28, 28, 32, 32, 32, 36, 36, 40, 40, 40, 45, 45, 49, 49, 49, 53, 53, 53, 57, 57, 57, 61, 61, 61, 65, 65, 65, 69, 69, 69, 73, 77, 77, 77, 77, 81, 81, 85, 85, 85, 89, 89, 89, 93, 93, 97, 97, 97, 101, 101, 105, 105, 105, 109, 109, 109, 113, 113, 117, 117, 121, 121, 125, 125, 130, 130, 134, 138, 142, 146, 146, 150, 154, 158, 162, 166, 174, 182, 190, 194, 210, 231, 239, 255, 255, 255, 255, 255, 255, 255,
        4, 16, 28, 32, 36, 40, 40, 45, 49, 53, 53, 57, 57, 61, 61, 65, 65, 65, 69, 69, 69, 73, 73, 77, 77, 81, 81, 81, 85, 85, 85, 89, 89, 89, 89, 93, 93, 97, 97, 97, 97, 101, 101, 101, 105, 105, 105, 105, 109, 109, 109, 113, 113, 113, 117, 117, 117, 121, 121, 121, 121, 125, 125, 130, 130, 130, 134, 138, 138, 142, 142, 146, 150, 154, 158, 162, 162, 166, 170, 174, 178, 182, 186, 190, 198, 206, 215, 223, 231, 239, 251, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        0, 0, 4, 8, 12, 16, 16, 20, 20, 24, 24, 28, 28, 32, 32, 32, 36, 36, 40, 40, 40, 45, 45, 49, 49, 49, 53, 53, 53, 57, 57, 57, 61, 61, 61, 65, 65, 65, 69, 69, 69, 73, 77, 77, 77, 77, 81, 81, 85, 85, 85, 89, 89, 89, 93, 93, 97, 97, 97, 101, 101, 105, 105, 105, 109, 109, 109, 113, 113, 117, 117, 121, 121, 125, 125, 130, 130, 134, 138, 142, 146, 146, 150, 154, 158, 162, 166, 174, 182, 190, 194, 210, 231, 239, 255, 255, 255, 255, 255, 255, 255,
        0, 8, 16, 20, 28, 28, 32, 32, 36, 40, 45, 45, 49, 49, 49, 53, 53, 57, 57, 57, 61, 65, 69, 69, 69, 73, 73, 77, 77, 77, 81, 81, 85, 85, 85, 89, 89, 89, 89, 93, 93, 93, 97, 97, 97, 97, 101, 105, 105, 105, 105, 109, 109, 109, 109, 109, 113, 113, 117, 117, 121, 121, 121, 121, 121, 125, 125, 125, 130, 134, 134, 138, 138, 142, 146, 146, 150, 154, 154, 158, 158, 162, 162, 166, 178, 182, 190, 194, 202, 210, 235, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        0, 0, 4, 8, 12, 16, 16, 20, 20, 24, 24, 28, 28, 32, 32, 32, 36, 36, 40, 40, 40, 45, 45, 49, 49, 49, 53, 53, 53, 57, 57, 57, 61, 61, 61, 65, 65, 65, 69, 69, 69, 73, 77, 77, 77, 77, 81, 81, 85, 85, 85, 89, 89, 89, 93, 93, 97, 97, 97, 101, 101, 105, 105, 105, 109, 109, 109, 113, 113, 117, 117, 121, 121, 125, 125, 130, 130, 134, 138, 142, 146, 146, 150, 154, 158, 162, 166, 174, 182, 190, 194, 210, 231, 239, 255, 255, 255, 255, 255, 255, 255,
        4, 12, 24, 28, 32, 32, 36, 40, 40, 45, 49, 49, 49, 53, 53, 57, 57, 61, 61, 65, 65, 69, 69, 69, 69, 73, 77, 77, 77, 77, 77, 81, 81, 85, 85, 85, 85, 89, 89, 89, 93, 93, 93, 93, 97, 97, 97, 97, 97, 97, 101, 101, 105, 105, 105, 105, 109, 109, 109, 109, 113, 113, 113, 117, 121, 121, 121, 125, 125, 125, 125, 130, 134, 134, 134, 138, 138, 142, 146, 150, 154, 158, 158, 162, 166, 170, 178, 178, 186, 194, 206, 219, 231, 251, 255, 255, 255, 255, 255, 255, 255,
    ];

    /// <summary>
    /// Computes the density per pixel (0 to <see cref="Levels"/> − 1) for the coverage <paramref name="coverage"/>; the edge is
    /// included (half covered = half as dense).
    /// </summary>
    /// <param name="coverage">Coverage per pixel (0 … 1), row by row, e.g. from <see cref="CoverageRaster"/>.</param>
    /// <param name="width">Width in px.</param>
    /// <param name="height">Height in px.</param>
    /// <param name="maxGrain">Upper limit of the grain size in px for wide strokes; give it relative to the size of the content so that the
    /// pattern does not depend on the resolution.</param>
    /// <param name="seed">Seed of the pattern (see <see cref="Seed"/>).</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <param name="threads">At most this many threads (−1: all cores); fewer in the background so that the user interface stays responsive.</param>
    /// <returns>One density level per pixel.</returns>
    /// <exception cref="ArgumentException">The length of <paramref name="coverage"/> is not <paramref name="width"/> × <paramref name="height"/>.</exception>
    public static byte[] Generate(float[] coverage, int width, int height, double maxGrain, uint seed, CancellationToken cancel = default, int threads = -1)
    {
        var n = width * height;
        if (coverage.Length != n) throw new ArgumentException("Größe passt nicht", nameof(coverage));
        var levels = new byte[n];
        var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = threads };

        // Distance of every inner pixel to the edge; half the stroke width = largest distance nearby; direction of the stroke
        var dist = Distance(coverage, width, height, options);
        var maxD = 0f;
        foreach (var d in dist) maxD = Math.Max(maxD, d);
        if (maxD <= 0) return levels;
        var half = MaxFilter(dist, width, height, (int)Math.Ceiling(maxD), options);
        var fullHalf = Percentile(half, coverage, maxD, 0.9);
        var strokeWidth = Math.Max(2, 2 * fullHalf);
        var (tx, ty) = Tangents(dist, coverage, width, height, Math.Max(1, (int)Math.Round(strokeWidth / 2)), options);

        // Raw value per pixel: grain, variation, dot screen, denser rim (relative to the local width, otherwise thin
        // strokes would be all "rim" and come out too dense), lighter centre of wide strokes
        var unit = Math.Max(0.6, Math.Min(strokeWidth / 10, maxGrain));
        var grain = Math.Max(0.5, unit * FineScale);
        var slow = strokeWidth * SlowOfWidth;
        var raw = new float[n];
        var rowMin = new float[height];
        var rowMax = new float[height];
        Parallel.For(0, height, options, y =>
        {
            float lo = float.MaxValue, hi = float.MinValue;
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                if (coverage[i] <= 0 || half[i] <= 0) continue;
                var thick = Math.Clamp((half[i] / fullHalf - 0.5) * 2, 0, 1);
                var depth = 1 - Math.Min(1, dist[i] / Math.Max(0.5, half[i]));   // 0 in the centre, 1 at the edge
                var v = (float)(FineWeight * (ValueNoise(x / grain, y / grain, seed) - 0.5)
                    + SlowWeight * (ValueNoise(x / slow, y / slow, seed + 5) - 0.5)
                    + DotWeight * Dot(x / unit, y / unit, seed + 9)
                    + RimWeight * Math.Exp(-dist[i] / Math.Max(0.6, 2 * half[i] * RimOfWidth))   // rim according to the stroke width at this point
                    - GrooveWeight * thick * Math.Exp(-depth * depth / (0.35 * 0.35)));
                raw[i] = v;
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
            rowMin[y] = lo;
            rowMax[y] = hi;
        });
        float min = float.MaxValue, max = float.MinValue;
        for (var y = 0; y < height; y++)
        {
            min = Math.Min(min, rowMin[y]);
            max = Math.Max(max, rowMax[y]);
        }
        if (min > max) return levels;

        // Rank via a fine histogram (linear time instead of sorting): share of the pixels with a smaller raw value
        const int Bins = 4096;
        var scale = (Bins - 1) / Math.Max(1e-9f, max - min);
        var histogram = new long[Bins];
        for (var i = 0; i < n; i++)
            if (coverage[i] > 0 && half[i] > 0) histogram[(int)((raw[i] - min) * scale)]++;
        var below = new double[Bins];
        double total = 0;
        for (var b = 0; b < Bins; b++)
        {
            below[b] = total + histogram[b] / 2.0;
            total += histogram[b];
        }

        // Map onto the distribution of the reference ink sample, blending smoothly between directions and thicknesses
        Parallel.For(0, height, options, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                if (coverage[i] <= 0 || half[i] <= 0) continue;
                var p = 100 * below[(int)((raw[i] - min) * scale)] / total;
                var angle = Math.Atan2(-ty[i], tx[i]) * 180 / Math.PI;
                if (angle < 0) angle += 180;
                if (angle >= 180) angle -= 180;
                var fan = angle / 30 - 0.5;   // sector k is centred at k
                var a0 = (int)Math.Floor(fan);
                var fa = fan - a0;
                var thick = Math.Clamp((half[i] / fullHalf - 0.35) / 0.5, 0, 1);
                double At(int t) => Quantile(a0, t, p) * (1 - fa) + Quantile(a0 + 1, t, p) * fa;
                var density = (At(0) * (1 - thick) + At(1) * thick) * coverage[i];
                levels[i] = (byte)Math.Clamp(Math.Round(density * (Levels - 1)), 0, Levels - 1);
            }
        });
        return levels;
    }

    /// <summary>Density (0–1) at <paramref name="percent"/> of the target distribution for the sector and thickness.</summary>
    private static double Quantile(int fan, int thick, double percent)
    {
        var start = ((fan % 6 + 6) % 6 * 2 + thick) * 101;
        var k = Math.Clamp((int)percent, 0, 99);
        var f = Math.Clamp(percent - k, 0, 1);
        return (Target[start + k] + (Target[start + k + 1] - Target[start + k]) * f) / 255.0;
    }

    /// <summary>Derives a seed from a text (FNV-1a; independent of the process, unlike <c>string.GetHashCode</c>).</summary>
    /// <param name="text">The text, e.g. the signature text.</param>
    /// <returns>The seed.</returns>
    public static uint Seed(string text)
    {
        var h = 2166136261u;
        foreach (var c in text) h = (h ^ c) * 16777619u;
        return h;
    }

    // ------------------------------------------------------------ Noise

    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352d;
        x ^= x >> 15;
        x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }

    private static double Random(int x, int y, uint seed) => Hash((uint)x * 0x9E3779B1u ^ Hash((uint)y + seed)) * (1.0 / uint.MaxValue);

    // Values on a grid, linear in between (smoothed interpolation would give blotches instead of grain)
    private static double ValueNoise(double x, double y, uint seed)
    {
        var fx = Math.Floor(x);
        var fy = Math.Floor(y);
        int xi = (int)fx, yi = (int)fy;
        double tx = x - fx, ty = y - fy;
        var a = Random(xi, yi, seed);
        var b = Random(xi + 1, yi, seed);
        var c = Random(xi, yi + 1, seed);
        var d = Random(xi + 1, yi + 1, seed);
        var top = a + (b - a) * tx;
        return top + (c + (d - c) * tx - top) * ty;
    }

    /// <summary>Dot screen as in the scan of the reference ink sample: one soft dot of random strength per cell, its centre slightly jittered.</summary>
    private static double Dot(double x, double y, uint seed)
    {
        double cx = x / DotPeriod, cy = y / DotPeriod;
        int ix = (int)Math.Floor(cx), iy = (int)Math.Floor(cy);
        var fx = cx - ix - 0.5 - (Random(ix, iy, seed + 1) - 0.5) * 0.6;
        var fy = cy - iy - 0.5 - (Random(ix, iy, seed + 2) - 0.5) * 0.6;
        var bump = Math.Max(0, 1 - (fx * fx + fy * fy) * 4);
        return (Random(ix, iy, seed) - 0.5) * 2 * bump + (bump - 0.4);
    }

    // ------------------------------------------------------------ Distance, width, direction

    /// <summary>Euclidean distance of every inner pixel to the nearest outer one (Felzenszwalb/Huttenlocher); 0 outside.</summary>
    private static float[] Distance(float[] coverage, int width, int height, ParallelOptions options)
    {
        const float Far = 1e20f;
        var f = new float[width * height];
        for (var i = 0; i < f.Length; i++) f[i] = coverage[i] >= 0.5f ? Far : 0;
        Parallel.For(0, width, options, () => new Scratch(Math.Max(width, height)), (x, _, s) =>
        {
            for (var y = 0; y < height; y++) s.Line[y] = f[y * width + x];
            Transform(s, height);
            for (var y = 0; y < height; y++) f[y * width + x] = s.Out[y];
            return s;
        }, _ => { });
        Parallel.For(0, height, options, () => new Scratch(Math.Max(width, height)), (y, _, s) =>
        {
            Array.Copy(f, y * width, s.Line, 0, width);
            Transform(s, width);
            for (var x = 0; x < width; x++) f[y * width + x] = MathF.Sqrt(s.Out[x]);
            return s;
        }, _ => { });
        return f;
    }

    private sealed class Scratch(int size)
    {
        public readonly float[] Line = new float[size];
        public readonly float[] Out = new float[size];
        public readonly int[] V = new int[size];
        public readonly float[] Z = new float[size + 1];
    }

    // Lower envelope of the parabolas: squared distance along one line
    private static void Transform(Scratch s, int n)
    {
        float[] f = s.Line, z = s.Z;
        var v = s.V;
        var k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (var q = 1; q < n; q++)
        {
            float cut;
            while (true)
            {
                cut = (f[q] + (float)q * q - (f[v[k]] + (float)v[k] * v[k])) / (2f * q - 2f * v[k]);
                if (cut <= z[k] && k > 0) k--;
                else break;
            }
            k++;
            v[k] = q;
            z[k] = cut;
            z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            var dq = q - v[k];
            s.Out[q] = (float)dq * dq + f[v[k]];
        }
    }

    /// <summary>Largest value in the square of radius <paramref name="r"/> (van Herk/Gil–Werman; cost independent of the radius).</summary>
    private static float[] MaxFilter(float[] src, int width, int height, int r, ParallelOptions options)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        Parallel.For(0, height, options, () => new MaxScratch(Math.Max(width, height) + 2 * r + 2), (y, _, s) =>
        {
            Pass(src, tmp, y * width, 1, width, r, s);
            return s;
        }, _ => { });
        Parallel.For(0, width, options, () => new MaxScratch(Math.Max(width, height) + 2 * r + 2), (x, _, s) =>
        {
            Pass(tmp, dst, x, width, height, r, s);
            return s;
        }, _ => { });
        return dst;
    }

    private sealed class MaxScratch(int size)
    {
        public readonly float[] Values = new float[size];
        public readonly float[] Forward = new float[size];
        public readonly float[] Backward = new float[size];
    }

    private static void Pass(float[] src, float[] dst, int start, int stride, int n, int r, MaxScratch s)
    {
        var block = 2 * r + 1;
        var len = n + 2 * r;
        // Values with r zeros before and after
        for (var i = 0; i < len; i++)
        {
            var j = i - r;
            s.Values[i] = j >= 0 && j < n ? src[start + j * stride] : 0;
        }
        for (var i = 0; i < len; i++)
            s.Forward[i] = i % block == 0 ? s.Values[i] : Math.Max(s.Forward[i - 1], s.Values[i]);
        for (var i = len - 1; i >= 0; i--)
            s.Backward[i] = i == len - 1 || (i + 1) % block == 0 ? s.Values[i] : Math.Max(s.Backward[i + 1], s.Values[i]);
        for (var j = 0; j < n; j++)
            dst[start + j * stride] = Math.Max(s.Backward[j], s.Forward[j + 2 * r]);
    }

    /// <summary>Value below which the share <paramref name="share"/> of the inner pixels lies (histogram in eighths of a pixel).</summary>
    private static float Percentile(float[] values, float[] coverage, float max, double share)
    {
        var bins = new int[(int)(max * 8) + 2];
        long count = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (coverage[i] <= 0.5f) continue;
            bins[Math.Min(bins.Length - 1, (int)(values[i] * 8))]++;
            count++;
        }
        if (count == 0) return 1;
        var target = (long)(count * share);
        long seen = 0;
        for (var b = 0; b < bins.Length; b++)
        {
            seen += bins[b];
            if (seen > target) return Math.Max(0.5f, (b + 0.5f) / 8);
        }
        return max;
    }

    /// <summary>Unit vector along the stroke for every pixel of the shape (principal direction of the smoothed structure tensor).</summary>
    private static (float[] X, float[] Y) Tangents(float[] dist, float[] coverage, int width, int height, int r, ParallelOptions options)
    {
        var n = width * height;
        var xx = new float[n];
        var xy = new float[n];
        var yy = new float[n];
        Parallel.For(1, Math.Max(1, height - 1), options, y =>
        {
            for (var x = 1; x < width - 1; x++)
            {
                var i = y * width + x;
                var gx = (dist[i + 1] - dist[i - 1]) * 0.5f;
                var gy = (dist[i + width] - dist[i - width]) * 0.5f;
                xx[i] = gx * gx;
                xy[i] = gx * gy;
                yy[i] = gy * gy;
            }
        });
        var tmp = new float[n];
        BoxBlur(xx, tmp, width, height, r, options);
        BoxBlur(xy, tmp, width, height, r, options);
        BoxBlur(yy, tmp, width, height, r, options);
        // The arrays are reused for the tangents (saves memory)
        var tx = xx;
        var ty = yy;
        Parallel.For(0, height, options, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                if (coverage[i] <= 0)
                {
                    tx[i] = 1;
                    ty[i] = 0;
                    continue;
                }
                // Double angle of the normal: (cos 2θ, sin 2θ) ∝ (xx − yy, 2 xy)
                double a = xx[i] - yy[i], b = 2 * xy[i];
                var len = Math.Sqrt(a * a + b * b);
                if (len < 1e-12)
                {
                    tx[i] = 1;
                    ty[i] = 0;
                    continue;
                }
                var cos2 = a / len;
                var cos = Math.Sqrt(Math.Max(0, (1 + cos2) / 2));
                var sin = Math.Sqrt(Math.Max(0, (1 - cos2) / 2)) * (b < 0 ? -1 : 1);
                // The tangent is perpendicular to the normal (cos, sin)
                tx[i] = (float)-sin;
                ty[i] = (float)cos;
            }
        });
        return (tx, ty);
    }

    /// <summary>Mean in the square of radius <paramref name="r"/>, in place (running sums, horizontal then vertical).</summary>
    private static void BoxBlur(float[] data, float[] tmp, int width, int height, int r, ParallelOptions options)
    {
        var scale = 1f / (2 * r + 1);
        Parallel.For(0, height, options, y =>
        {
            var row = y * width;
            float sum = 0;
            for (var x = -r; x <= r; x++) sum += data[row + Math.Clamp(x, 0, width - 1)];
            for (var x = 0; x < width; x++)
            {
                tmp[row + x] = sum * scale;
                sum += data[row + Math.Min(width - 1, x + r + 1)] - data[row + Math.Max(0, x - r)];
            }
        });
        // Vertical pass in strips, row by row (column by column every access would be a cache miss)
        const int Strip = 256;
        Parallel.For(0, (width + Strip - 1) / Strip, options, strip =>
        {
            var x0 = strip * Strip;
            var count = Math.Min(Strip, width - x0);
            Span<float> sums = stackalloc float[Strip];
            sums.Clear();
            for (var y = -r; y <= r; y++)
            {
                var row = Math.Clamp(y, 0, height - 1) * width + x0;
                for (var x = 0; x < count; x++) sums[x] += tmp[row + x];
            }
            for (var y = 0; y < height; y++)
            {
                int target = y * width + x0, add = Math.Min(height - 1, y + r + 1) * width + x0, remove = Math.Max(0, y - r) * width + x0;
                for (var x = 0; x < count; x++)
                {
                    data[target + x] = sums[x] * scale;
                    sums[x] += tmp[add + x] - tmp[remove + x];
                }
            }
        });
    }
}
