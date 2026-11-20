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
