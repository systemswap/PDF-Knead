using PdfKnead.Core.Pdf.Filters.Jpeg;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The SIMD ways of the own JPEG decoder (AVX2, SSE4.1) give bit for bit what the scalar ways give — inverse DCT, YCbCr → RGB, the vertical and
/// horizontal steps of upsampling — also for the values only crafted data has (coefficients far beyond what a valid stream holds). On a machine
/// without those instructions both are the scalar way and the comparison is trivially equal.
/// </summary>
public sealed class JpegSimdTests
{
    /// <summary>Realistic blocks (few non-zero coefficients) and crafted ones (every coefficient at the limits of 32 bits).</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void Idct_SameAsScalar(int seed, bool extreme)
    {
        var random = new Random(seed);
        var coef = new int[64];
        var a = new byte[8 * 11 + 8];
        var b = new byte[8 * 11 + 8];
        for (var round = 0; round < 2000; round++)
        {
            for (var k = 0; k < 64; k++)
                coef[k] = extreme ? random.Next(4) switch { 0 => int.MinValue, 1 => int.MaxValue, _ => random.Next(-70000, 70000) }
                    : random.Next(k == 0 ? 1 : 6) == 0 ? random.Next(-2048, 2048) : 0;
            JpegIdct.Transform(coef, a, 3, 11, simd: true);
            JpegIdct.Transform(coef, b, 3, 11, simd: false);
            Assert.Equal(b, a);
        }
    }

    /// <summary>The quantised form: 16-bit coefficients times tables up to 65 535, dequantised while loaded.</summary>
    [Fact]
    public void IdctQuantised_SameAsScalar()
    {
        var random = new Random(4);
        var block = new short[64];
        var quant = new int[64];
        var a = new byte[64];
        var b = new byte[64];
        for (var round = 0; round < 2000; round++)
        {
            for (var k = 0; k < 64; k++)
            {
                block[k] = (short)(round % 2 == 0 ? random.Next(short.MinValue, short.MaxValue + 1) : random.Next(5) == 0 ? random.Next(-100, 100) : 0);
                quant[k] = round % 3 == 0 ? random.Next(1, 65536) : random.Next(1, 100);
            }
            JpegIdct.TransformQuantised(block, quant, a, 0, 8, simd: true);
            JpegIdct.TransformQuantised(block, quant, b, 0, 8, simd: false);
            Assert.Equal(b, a);
        }
    }

    /// <summary>
    /// Adobe TN 5116, section 22 (the reverse DCT against an accurate one: no sample more than ±1 off) with the ideal IDCT of T.81, A.3.3:
    /// 20 000 blocks of pseudo-random dequantised coefficients — three ranges after the pattern of the usual accuracy tests (±256, ±5, ±300),
    /// mostly sparse as in real images — computed in double precision, level-shifted by 128, rounded and clamped, against the decoder's integer
    /// IDCT on the vector and the scalar path: no sample more than one step off, and on average far less.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Idct_WithinOneStepOfTheIdealTransform(bool simd)
    {
        var cos = new double[8, 8];
        for (var x = 0; x < 8; x++)
            for (var u = 0; u < 8; u++)
                cos[x, u] = (u == 0 ? Math.Sqrt(0.5) : 1) * Math.Cos((2 * x + 1) * u * Math.PI / 16);
        var random = new Random(1180);
        var coef = new int[64];
        var output = new byte[64];
        long worst = 0, total = 0, count = 0;
        foreach (var range in new[] { 256, 5, 300 })
            for (var n = 0; n < 20_000 / 3; n++)
            {
                Array.Clear(coef);
                var nonZero = 1 + random.Next(n % 4 == 0 ? 64 : 12);
                for (var i = 0; i < nonZero; i++) coef[random.Next(i == 0 ? 1 : 64)] = random.Next(-range, range + 1);
                JpegIdct.Transform(coef, output, 0, 8, simd);
                for (var y = 0; y < 8; y++)
                    for (var x = 0; x < 8; x++)
                    {
                        double sum = 0;
                        for (var v = 0; v < 8; v++)
                            for (var u = 0; u < 8; u++)
                                sum += cos[x, u] * cos[y, v] * coef[v * 8 + u];
                        var ideal = Math.Clamp((int)Math.Floor(sum / 4 + 128 + 0.5), 0, 255);
                        var d = Math.Abs(output[y * 8 + x] - ideal);
                        worst = Math.Max(worst, d);
                        total += d;
                        count++;
                    }
            }
        Assert.True(worst <= 1, $"größte Abweichung {worst}");
        Assert.True(total < count / 20, $"mittlere Abweichung {(double)total / count:0.0000}");
    }

    /// <summary>
    /// Every combination of Y, Cb, Cr (2^24) gives the same RGB on the vector path and the scalar one (FG 1010: the scalar tables used
    /// 22 553 for the Cb part of G, the vector path 22 554 — 12 058 channel values one step apart, on machines without AVX2 and in the last
    /// pixels of a row whose width is no multiple of 8; random samples had not hit them).
    /// </summary>
    [Fact]
    public void YccToRgb_SameAsScalar_EveryValue()
    {
        var y = Enumerable.Range(0, 256).Select(v => (byte)v).ToArray();
        byte[] cb = new byte[256], cr = new byte[256], a = new byte[768], b = new byte[768];
        var differ = 0;
        for (var u = 0; u < 256; u++)
            for (var v = 0; v < 256; v++)
            {
                Array.Fill(cb, (byte)u);
                Array.Fill(cr, (byte)v);
                JpegColor.YccToRgb(y, cb, cr, a, 256, simd: true);
                JpegColor.YccToRgb(y, cb, cr, b, 256, simd: false);
                for (var i = 0; i < 768; i++)
                    if (a[i] != b[i]) differ++;
            }
        Assert.Equal(0, differ);
    }

    [Fact]
    public void YccToRgb_SameAsScalar()
    {
        var random = new Random(5);
        foreach (var width in (int[])[1, 7, 8, 9, 16, 31, 64, 67])
        {
            byte[] y = new byte[width], cb = new byte[width], cr = new byte[width], a = new byte[3 * width], b = new byte[3 * width];
            random.NextBytes(y);
            random.NextBytes(cb);
            random.NextBytes(cr);
            JpegColor.YccToRgb(y, cb, cr, a, width, simd: true);
            JpegColor.YccToRgb(y, cb, cr, b, width, simd: false);
            Assert.Equal(b, a);
        }
    }

    [Fact]
    public void Upsampling_SameAsScalar()
    {
        var random = new Random(6);
        foreach (var w in (int[])[3, 4, 8, 9, 10, 17, 33, 70])
        {
            byte[] r0 = new byte[w], r1 = new byte[w];
            random.NextBytes(r0);
            random.NextBytes(r1);
            short[] s1 = new short[w], s2 = new short[w];
            JpegColor.VerticalSums(r0, r1, s1, w, simd: true);
            JpegColor.VerticalSums(r0, r1, s2, w, simd: false);
            Assert.Equal(s2, s1);
            byte[] o1 = new byte[2 * w], o2 = new byte[2 * w];
            JpegColor.Twofold(s1, o1, w, 8, 7, 4, simd: true);
            JpegColor.Twofold(s1, o2, w, 8, 7, 4, simd: false);
            Assert.Equal(o2, o1);
            JpegColor.Widen(r0, s1, w, simd: true);
            JpegColor.Widen(r0, s2, w, simd: false);
            Assert.Equal(s2, s1);
            JpegColor.Twofold(s1, o1, w, 1, 2, 2, simd: true);
            JpegColor.Twofold(s1, o2, w, 1, 2, 2, simd: false);
            Assert.Equal(o2, o1);
            // 4:2:2 at the edges: the sample itself
            Assert.Equal(r0[0], o2[0]);
            Assert.Equal(r0[w - 1], o2[2 * w - 1]);
        }
    }

    /// <summary>The span forms refuse what does not fit instead of writing past it.</summary>
    [Fact]
    public void SpanForms_RefuseWhatDoesNotFit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegIdct.Transform(new int[64], new byte[63], 0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegIdct.Transform(new int[64], new byte[64], 1, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegColor.Twofold(new short[8], new byte[15], 8, 8, 7, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegColor.YccToRgb(new byte[8], new byte[8], new byte[8], new byte[23], 8));
    }
}
