using System.Buffers.Binary;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Gaps left after the decisions of 23.11.2026: subsampling factors whose least common multiple lies far above 2^31 together with coordinates
/// above 2^31 − 1 (T.800 Table A.9; docs/FEHLERGEDAECHTNIS.md, No. 1060), at full and at reduced resolution, and the reserved bits of Scoc
/// (Table A.23; No. 1062).
/// </summary>
public sealed partial class JpxMarkerTests
{
    /// <summary>Four subsampling factors without a common divisor: their least common multiple is 3 368 562 317, above 2^31.</summary>
    private static readonly int[] PrimeFactors = [251, 241, 239, 233];

    /// <summary>
    /// A codestream of four unsigned 8-bit components, subsampled by <paramref name="factors"/> along one direction (by 1 along the other):
    /// the image area runs <paramref name="length"/> grid points from <paramref name="origin"/> along that direction and is one grid point
    /// across, at <paramref name="across"/>. Tiles of one grid point (T.800 B.3): each holds the one sample of every component whose grid
    /// (B-1, a multiple of its factor) lies there, coded by <see cref="JpxTests.AnnexG"/> (5-3 filter: the sample less 128, X); a component
    /// without a sample in the tile has no precincts and so no packet (B.6, B.9). Sample k of component c (counted from the first in the image)
    /// is <paramref name="sample"/>(c, k). With <paramref name="levels"/> L the tile-component of one sample at K (its coordinate along the
    /// direction; across a multiple of 2^L) lies in resolution r where K is a multiple of 2^(L − r) (B-15), else that resolution is empty and has
    /// no packet. A signal of one sample keeps its value at an even place and becomes 2X in the high band at an odd one (F.4.8): with K a
    /// multiple of 2^L the sample is the LL coefficient X in resolution 0, otherwise 2X in the HL (or LH) band of the level where K / 2^(j − 1)
    /// is odd, resolution L − j + 1 (Mb 11); the later resolutions hold no code-block of it and get an empty packet.
    /// </summary>
    private static byte[] Subsampled(bool vertical, long origin, int length, long across, int[] factors, Func<int, int, int> sample, int levels = 0)
    {
        Assert.Equal(0, across % (1L << levels));
        var b = new List<byte> { 0xFF, 0x4F };
        void U16(int v) => b.AddRange([(byte)(v >> 8), (byte)v]);
        void U32(long v) => b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        var n = factors.Length;
        long x0 = vertical ? across : origin, y0 = vertical ? origin : across;
        long x1 = vertical ? across + 1 : origin + length, y1 = vertical ? origin + length : across + 1;
        b.AddRange([0xFF, 0x51]);
        U16(38 + 3 * n);
        U16(0);
        U32(x1);
        U32(y1);
        U32(x0);
        U32(y0);
        U32(1);
        U32(1);
        U32(x0);
        U32(y0);
        U16(n);
        foreach (var f in factors) b.AddRange([7, (byte)(vertical ? 1 : f), (byte)(vertical ? f : 1)]);
        b.AddRange([0xFF, 0x52, 0, 12, 0, 0, 0, 1, 0, (byte)levels, 4, 4, 0, 1]);   // COD: LRCP, one layer, the levels, 5-3
        // QCD: no quantization, two guard bits, exponent 9 for LL (Mb = 10), 10 for the other bands (Mb = 11)
        b.AddRange([0xFF, 0x5C, 0, (byte)(4 + 3 * levels), 2 << 5, 9 << 3, .. Enumerable.Repeat((byte)(10 << 3), 3 * levels)]);
        for (var t = 0; t < length; t++)
        {
            var at = origin + t;
            var body = new List<byte>();
            for (var r = 0; r <= levels; r++)
                for (var c = 0; c < n; c++)
                {
                    if (at % factors[c] != 0) continue;
                    var place = at / factors[c];
                    if (place % (1L << (levels - r)) != 0) continue;   // resolution r is empty
                    var k = (int)(place - (origin + factors[c] - 1) / factors[c]);
                    var x = sample(c, k) - 128;
                    var own = place % (1L << levels) == 0 ? 0 : levels - long.TrailingZeroCount(place);
                    body.AddRange(r != own ? [0] : JpxTests.AnnexG.Packet(JpxTests.AnnexG.Coded.Full(own == 0 ? x : 2 * x), own == 0 ? 10 : 11));
                }
            b.AddRange([0xFF, 0x90, 0, 10]);
            U16(t);
            U32(12 + 2 + body.Count);
            b.AddRange([0, 1, 0xFF, 0x93]);
            b.AddRange(body);
        }
        b.AddRange([0xFF, 0xD9]);
        return [.. b];
    }

    /// <summary>
    /// T.800 Table A.9 allows XRsiz and YRsiz from 1 to 255 and coordinates up to 2^32 − 1; four components subsampled by 251, 241, 239 and 233
    /// (least common multiple 3 368 562 317) with the image area beyond 2^31 − 1, horizontally and vertically. Every pixel shows the sample of
    /// each component whose grid position is the last one at or before it (before the first: the first) — the same image as at an offset
    /// below 2^31 that differs from it by the least common multiple, where every component has the same phase. Before the codestream was
    /// refused („Koordinate zu groß“): the image could only move by a multiple of all four factors, and that exceeds the offset.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Subsampling_CommonMultipleAbove2To31_LargeOffsets_EverySampleInPlace(bool vertical)
    {
        const long lcm = 251L * 241 * 239 * 233;
        const long near = (1L << 29) + 12_345;
        const long far = lcm + near;
        const long across = (1L << 32) - 2;
        const int length = 600;
        static int Sample(int c, int k) => 20 + 50 * c + 30 * k;
        var cs = Subsampled(vertical, far, length, across, PrimeFactors, Sample);
        var parsed = JpxCodestream.Parse(cs, 0, cs.Length);
        Assert.Equal(far, vertical ? parsed.Y0 : parsed.X0);
        Assert.True(far > int.MaxValue && far + length < 1L << 32);

        var image = JpxDecoder.Decode(cs, long.MaxValue);
        Assert.False(image.Truncated);
        Assert.Equal(vertical ? (1, length) : (length, 1), (image.Width, image.Height));
        Assert.Equal(4, image.Channels);
        var expected = new byte[4 * length];
        for (var i = 0; i < length; i++)
            for (var c = 0; c < 4; c++)
            {
                long f = PrimeFactors[c];
                long first = (far + f - 1) / f, last = (far + length + f - 1) / f - 1;   // the samples of the component in the image area
                Assert.True(last > first);   // at least two samples, so each pixel shows where one ends
                expected[4 * i + c] = (byte)Sample(c, (int)(Math.Clamp((far + i) / f, first, last) - first));
            }
        Assert.Equal(expected, image.Samples);
        Assert.Equal(JpxDecoder.Decode(Subsampled(vertical, near, length, across, PrimeFactors, Sample), long.MaxValue).Samples, image.Samples);
    }

    /// <summary>
    /// The same four factors with two decomposition levels, the image area ending at the last grid point 2^32 − 1, decoded at full resolution
    /// and with one and two levels left out (a smaller area limit). With reduce r the image is ⌈x1 / 2^r⌉ − ⌈x0 / 2^r⌉ long; sample m of the
    /// reduced plane of a component with factor f sits at m · f on the reduced grid and is sample m · 2^r of the component (the low band of a
    /// signal of one sample at an even place is the sample, F.4.8; the decoder's plane runs from ⌈⌈x0 / f⌉ / 2^r⌉ to ⌈⌈x1 / f⌉ / 2^r⌉ − 1, B-15).
    /// Every pixel u shows, of each component, the last such sample at or before it, ⌊u / f⌋ (before the first: the first). With one level left
    /// out the reduced image still ends at 2^31, beyond 2^31 − 1: it has to move, with the factor 233 left out of the period and resampled
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1060); with two it ends at 2^30 and stays.
    /// </summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void Subsampling_CommonMultipleAbove2To31_ReducedResolution_EverySampleInPlace(bool vertical, int reduce)
    {
        const int levels = 2;
        const int length = 2600;
        const long end = (1L << 32) - 1;
        const long origin = end - length;
        const long across = (1L << 32) - 4;   // a multiple of 2^levels: the sample stays in the low band across
        static int Sample(int c, int k) => 10 + (50 * c + 37 * k) % 240;
        static long Ceil(long a, long b) => (a + b - 1) / b;
        var cs = Subsampled(vertical, origin, length, across, PrimeFactors, Sample, levels);
        var size = (int)(Ceil(end, 1L << reduce) - Ceil(origin, 1L << reduce));
        Assert.Equal(reduce == 1, reduce > 0 && Ceil(end, 1L << reduce) > int.MaxValue);

        var image = JpxDecoder.Decode(cs, reduce == 0 ? long.MaxValue : size);
        Assert.False(image.Truncated);
        Assert.Equal(vertical ? (1, size) : (size, 1), (image.Width, image.Height));
        Assert.Equal(4, image.Channels);
        var expected = new byte[4 * size];
        for (var i = 0; i < size; i++)
            for (var c = 0; c < 4; c++)
            {
                long f = PrimeFactors[c];
                var u = Ceil(origin, 1L << reduce) + i;   // the pixel on the reduced grid
                long first = Ceil(Ceil(origin, f), 1L << reduce), last = Ceil(Ceil(end, f), 1L << reduce) - 1;
                Assert.True(last > first);   // at least two samples, so each pixel shows where one ends
                var place = Math.Clamp(u / f, first, last) << reduce;   // the sample of the component at full resolution
                expected[4 * i + c] = (byte)Sample(c, (int)(place - Ceil(origin, f)));
            }
        Assert.Equal(expected, image.Samples);
    }

    /// <summary>
    /// T.800 Table A.23: Scoc is 0 (precincts of 2^15) or 1 (precincts given); every other value is reserved, and unlike Scod (Table A.13) no
    /// sentence lets a decoder ignore the other bits. A COC with such bits, in the main header and in a tile-part header, is read by bit 0 as
    /// before — the same image as with Scoc 0 or 1 — and reported (before without a word).
    /// </summary>
    [Theory]
    [InlineData(0x02, false)]
    [InlineData(0x04, false)]
    [InlineData(0x80, false)]
    [InlineData(0xFE, false)]
    [InlineData(0x03, false)]
    [InlineData(0xFF, false)]
    [InlineData(0x06, true)]
    [InlineData(0x81, true)]
    public void Scoc_ReservedBits_ReadByBit0AndReported(int scoc, bool tilePart)
    {
        var cs = JpxTests.AnnexG.Lossless8(2, 2, [10, 20, 30, 40], [50, 60, 70, 80], [90, 100, 110, 120]);
        // COC for component 1: Scoc, then SPcoc as the COD of the encoder (no levels, blocks of 64 × 64, style 0, 5-3), with bit 0 one
        // precinct size (PPx = PPy = 15)
        byte[] Coc(int s) => Segment(0xFF53, [1, (byte)s, 0, 4, 4, 0, 1, .. (s & 1) != 0 ? new byte[] { 0xFF } : []]);
        byte[] With(int s) => tilePart ? InFirstTilePart(cs, Coc(s)) : Insert(cs, FirstSot(cs), Coc(s));
        Assert.Equal(Plain(cs), Plain(With(scoc & 1)));
        SameImageReported(With(scoc), With(scoc & 1));
    }
}
