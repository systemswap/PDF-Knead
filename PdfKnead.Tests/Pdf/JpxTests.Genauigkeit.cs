using System.Buffers.Binary;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Precision up to the limits of ITU-T T.800 (decided on 23.11.2026): bit-planes beyond 29 and ROI shifts up to 255 (docs/FEHLERGEDAECHTNIS.md,
/// No. 1052), coordinates up to 2^32 − 1 (No. 1051), up to 9 361 progression order changes per header (No. 1053) and the image size of
/// Equation I-2 (No. 1054). The codestreams come from the encoder of <see cref="AnnexG"/>, or are the RGB test image moved or stretched where
/// that leaves every sample in place.
/// </summary>
public sealed partial class JpxTests
{
    private static readonly Lazy<byte[]> Rgb = new(() => JpxColourTests.Codestream("jp2k-resetprob.pdf"));

    private static int BitLength(long v) => 64 - (int)long.LeadingZeroCount(v);

    /// <summary>
    /// T.800 E.1 (Equation E-2) and Annex D: Mb = G + εb − 1 reaches 7 + 31 − 1 = 37 bit-planes (B.10.7.1, note). One coefficient per test, coded
    /// on all its planes; with the 5-3 filter and no levels the tile-component holds it as it is (the exact store, Mb above 23). Coefficients
    /// with their top bit in plane 30 to 36 — the planes the decoder used to leave out, the block empty and the image reported incomplete —
    /// come out exactly, as does one in plane 29, the last of the 32-bit magnitudes. In the 16-bit plane of the 38-bit component: (2^37 + q) / 2^22.
    /// </summary>
    [Theory]
    [InlineData(536870915L)]           // 2^29 + 3: plane 29, 32-bit magnitudes
    [InlineData(1073741825L)]          // 2^30 + 1
    [InlineData(-2147483649L)]         // −(2^31 + 1)
    [InlineData(103079227737L)]        // 2^36 + 2^35 + 12 345: plane 36, the top one
    [InlineData(-68719477513L)]        // −(2^36 + 777)
    public void BitPlanesAbove29_DecodedExactly(long q)
    {
        var cs = AnnexG.Codestream(new AnnexG.Image
        {
            Components = [(38, false)], Guard = 7, Exponent = 31, Value = (_, _) => new AnnexG.Coded(q, BitLength(Math.Abs(q)) - 1),
        });
        var tile = AnnexB.Tile(cs);
        var tc = tile.Components[0];
        Assert.NotNull(tc.Exact);
        Assert.Equal(q, tc.Exact![0]);
        var planes = JpxDecoder.DecodePlanes(cs, long.MaxValue);
        Assert.False(planes.Truncated);
        Assert.Equal((int)(((1L << 37) + q) >> 22), planes.Planes[0][0]);
    }

    /// <summary>
    /// T.800 H.1 and Table A.26: the ROI shift s runs from 0 to 255, so a code-block can have Mb + s bit-planes (H.2). Two tiles of a 12-bit
    /// component: a coefficient of the region (300, scaled up by 2^s, coded down to plane s) and one of the background (77, below 2^s), with
    /// s = 30 (its region beyond plane 29), 61, 62, 100 (beyond 64 bits) and 255 (the largest). Both come out as given, 2048 + 300 and 2048 + 77;
    /// before s was capped at 30 and blocks with a top plane above 29 were left empty.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(100)]
    [InlineData(255)]
    public void RoiShift_UpTo255_RegionAndBackgroundInPlace(int s)
    {
        var cs = AnnexG.Codestream(new AnnexG.Image
        {
            Width = 2, Components = [(12, false)], Exponent = 9, MainMarkers = [AnnexG.Rgn(0, s)],
            Value = (t, _) => t == 0 ? new AnnexG.Coded(300, 8 + s, s, s, s) : new AnnexG.Coded(77, 6, 0, s),
        });
        var planes = JpxDecoder.DecodePlanes(cs, long.MaxValue);
        Assert.Equal([2348, 2125], planes.Planes[0].Select(v => (int)v));
        Assert.False(planes.Truncated);
    }

    /// <summary>
    /// T.800 B.10.7.1 and D.3: without termination all coding passes of a code-block form one codeword segment — 3 · Mb − 2 = 109 without an
    /// ROI, up to 3 · (37 + 255) − 2 = 874 with one. A region coefficient with s = 40 coded down to plane 0 takes 1 + 3 · 48 = 145 passes in one
    /// segment (its planes below s are 0); before the decoder started a second segment with a new MQ decoder after pass 109. The region keeps
    /// its value (H.1: its bits below s carry nothing).
    /// </summary>
    [Fact]
    public void CodeBlock_MoreThan109Passes_OneSegment()
    {
        const int s = 40;
        var cs = AnnexG.Codestream(new AnnexG.Image
        {
            Width = 2, Components = [(12, false)], Exponent = 9, MainMarkers = [AnnexG.Rgn(0, s)],
            Value = (t, _) => t == 0 ? new AnnexG.Coded(300, 8 + s, 0, s, s) : new AnnexG.Coded(77, 6, 0, s),
        });
        var planes = JpxDecoder.DecodePlanes(cs, long.MaxValue);
        Assert.Equal([2348, 2125], planes.Planes[0].Select(v => (int)v));
        Assert.False(planes.Truncated);
    }

    /// <summary>The codestream with the image area and the tiling moved by (<paramref name="dx"/>, <paramref name="dy"/>) on the reference grid.</summary>
    private static byte[] Moved(byte[] cs, long dx, long dy)
    {
        var copy = (byte[])cs.Clone();
        void Add(int at, long by) => BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(at), checked((uint)(BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(at)) + by)));
        // SOC, SIZ, Lsiz, Rsiz, then Xsiz 8, Ysiz 12, XOsiz 16, YOsiz 20, XTsiz 24, YTsiz 28, XTOsiz 32, YTOsiz 36
        Add(8, dx);
        Add(12, dy);
        Add(16, dx);
        Add(20, dy);
        Add(32, dx);
        Add(36, dy);
        return copy;
    }

    /// <summary>
    /// T.800 Table A.9: Xsiz and Ysiz reach 2^32 − 1, the offsets 2^32 − 2. The RGB test image (one tile, five levels, precincts of 2^15) moved
    /// by 2^32 − 2^20 in both directions — a multiple of 2^(5 + 15), so tiles, resolutions, precincts and code-blocks lie the same — gives the
    /// same image; before every value above 2^31 − 1 was refused.
    /// </summary>
    [TestPdfsFact]
    public void Offsets_Near2To32_SameImage()
    {
        var cs = Rgb.Value;
        Assert.Equal(5, cs[MainCod(cs) + 9]);   // NL of COD
        const long by = (1L << 32) - (1L << 20);
        var moved = Moved(cs, by, by);
        Assert.Equal((1L << 32) - (1L << 20) + 40, JpxCodestream.Parse(moved, 0, moved.Length).X1);
        var expected = JpxDecoder.Decode(cs, long.MaxValue);
        var image = JpxDecoder.Decode(moved, long.MaxValue);
        Assert.Equal((expected.Width, expected.Height), (image.Width, image.Height));
        Assert.Equal(expected.Samples, image.Samples);
        Assert.False(image.Truncated);
    }

    /// <summary>
    /// T.800 Table A.9 at its limits: an image of one sample at XOsiz = YOsiz = 2^32 − 2 (Xsiz = Ysiz = 2^32 − 1), the tile at the same place —
    /// the sample decodes as without the offset.
    /// </summary>
    [Fact]
    public void Offset_2To32Minus2_OneSample()
    {
        var cs = AnnexG.Lossless8(1, 1, [200]);
        var moved = Moved(cs, (1L << 32) - 2, (1L << 32) - 2);
        var parsed = JpxCodestream.Parse(moved, 0, moved.Length);
        Assert.Equal(((1L << 32) - 2, (1L << 32) - 1), (parsed.X0, parsed.X1));
        var image = JpxDecoder.Decode(moved, long.MaxValue);
        Assert.Equal((1, 1), (image.Width, image.Height));
        Assert.Equal([200], image.Samples);
        Assert.False(image.Truncated);
    }

    private static int MainCod(byte[] cs)
    {
        var p = 2;
        while (BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p)) != 0xFF52) p += 2 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p + 2));
        return p;
    }

    /// <summary>
    /// T.800 I.5.3.1.1 (Equations I-1, I-2): with every component subsampled by a common factor M (here XRsiz = YRsiz = 2, so M = 2) the image is
    /// shown ⌈Xsiz/M⌉ − ⌈XOsiz/M⌉ wide and ⌈Ysiz/M⌉ − ⌈YOsiz/M⌉ high. The RGB test image with its reference grid doubled and every component
    /// subsampled by 2 keeps its samples: 40 × 27 pixels, the same image — before 80 × 54, every sample repeated.
    /// </summary>
    [TestPdfsFact]
    public void CommonSubsampling_ImageSizeOfEquationI2()
    {
        var cs = (byte[])Rgb.Value.Clone();
        void Double(int at) => BinaryPrimitives.WriteUInt32BigEndian(cs.AsSpan(at), BinaryPrimitives.ReadUInt32BigEndian(cs.AsSpan(at)) * 2);
        foreach (var at in new[] { 8, 12, 16, 20, 24, 28, 32, 36 }) Double(at);
        for (var c = 0; c < 3; c++) cs[42 + 3 * c + 1] = cs[42 + 3 * c + 2] = 2;   // XRsiz, YRsiz of each component
        var expected = JpxDecoder.Decode(Rgb.Value, long.MaxValue);
        var image = JpxDecoder.Decode(cs, long.MaxValue);
        Assert.Equal((40, 27), (image.Width, image.Height));
        Assert.Equal(expected.Samples, image.Samples);
        Assert.Equal(2, JpxDecoder.CommonSubsampling(JpxCodestream.Parse(cs, 0, cs.Length)));
    }

    /// <summary>
    /// T.800 I.5.3.1.1 with an offset: M = 2, Xsiz = 8, XOsiz = 2, Ysiz = 4 — ⌈8/2⌉ − ⌈2/2⌉ = 3 by ⌈4/2⌉ − 0 = 2 pixels, the samples of the
    /// component (columns 1 to 3, rows 0 and 1) one to one; before the image was the 6 × 4 of the grid, every sample repeated.
    /// </summary>
    [Fact]
    public void CommonSubsampling_WithOffsets()
    {
        // the encoder writes tiles of one sample: the 3 × 2 image with its grid stretched — tiles of 2 × 2 grid points from (2, 0) hold one
        // sample each of the component subsampled by 2
        var cs = AnnexG.Lossless8(3, 2, [10, 20, 30, 40, 50, 60]);
        void Set(int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(cs.AsSpan(at), v);
        Set(8, 8); Set(12, 4); Set(16, 2); Set(20, 0); Set(24, 2); Set(28, 2); Set(32, 2); Set(36, 0);
        cs[43] = cs[44] = 2;
        var image = JpxDecoder.Decode(cs, long.MaxValue);
        Assert.Equal((3, 2), (image.Width, image.Height));
        Assert.Equal([10, 20, 30, 40, 50, 60], image.Samples);
        Assert.False(image.Truncated);
    }

    /// <summary>
    /// T.800 A.6.6 and B.12.2: one POC segment holds up to 9 361 entries. 1 100 entries without packets (REpoc not above RSpoc), then the one
    /// that names all packets: the image of the codestream; before only the first 1 024 entries were kept and that last one was lost.
    /// </summary>
    [TestPdfsFact]
    public void ProgressionOrderChange_EntriesBeyond1024_Read()
    {
        var cs = Rgb.Value;
        var order = cs[MainCod(cs) + 5];
        byte[] empty = [0, 0, 0, 1, 0, 1, order];
        byte[] all = [0, 0, 0xFF, 0xFF, 33, 255, order];
        var body = Enumerable.Range(0, 1100).SelectMany(_ => empty).Concat(all).ToArray();
        byte[] poc = [0xFF, 0x5F, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];
        var at = 4 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(4));
        byte[] file = [.. cs[..at], .. poc, .. cs[at..]];
        var image = JpxDecoder.Decode(file, long.MaxValue);
        Assert.Equal(JpxDecoder.Decode(cs, long.MaxValue).Samples, image.Samples);
        Assert.False(image.Truncated);
    }
}
