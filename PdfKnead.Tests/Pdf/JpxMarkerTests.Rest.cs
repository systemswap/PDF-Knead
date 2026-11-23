using System.Buffers.Binary;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// A codestream whose Rsiz names profile 0 or 1 (ITU-T T.800, Table A.10: 1 or 2) but which breaks a restriction Table A.45 sets for that
/// profile: decoded as any codestream and reported (<see cref="JpxImage.Truncated"/>; decided on 23.11.2026: read and report;
/// docs/FEHLERGEDAECHTNIS.md, No. 1077) — before it was read silently. Each case is set against the same codestream with Rsiz 0, which must
/// give the same image and no report.
/// </summary>
public sealed partial class JpxMarkerTests
{
    /// <summary>
    /// The test codestreams of <see cref="JpxTests.AnnexG"/> (Rsiz 0, tiles of one grid point, blocks of 64 × 64, style 0, no levels, one
    /// tile-part per tile): a 1 × 1 image is one tile for the whole image and keeps profile 0; a 4 × 1 image of four tiles keeps profile 1
    /// (square tiles of at most 1024) but not profile 0 (tiles of 128 or one tile, Table A.45). Blocks of 128 × 32 (xcb 7, ycb 5; a single
    /// block in every tile either way) are beyond both; a COD in the tile-part header is beyond profile 0. The tile-part header COD repeats
    /// that of the main header, so the image stays the same.
    /// </summary>
    [Theory]
    [InlineData("one tile", 1, false)]
    [InlineData("one tile", 2, false)]
    [InlineData("four tiles", 2, false)]
    [InlineData("four tiles", 1, true)]
    [InlineData("four tiles", 3, false)]
    [InlineData("blocks 128 x 32", 2, true)]
    [InlineData("blocks 128 x 32", 1, true)]
    [InlineData("COD in the tile-part", 1, true)]
    [InlineData("COD in the tile-part", 2, false)]
    public void ProfileNamedByRsiz_RestrictionBroken_ReadAndReported(string change, int rsiz, bool reported)
    {
        byte[] cod = [0xFF, 0x52, 0, 12, 0, 0, 0, 1, 0, 0, 4, 4, 0, 1];
        int[] r = [10, 200, 30, 255], g = [0, 100, 220, 5], b = [128, 60, 90, 250];
        var cs = change switch
        {
            "one tile" => JpxTests.AnnexG.Lossless8(1, 1, [r[0]], [g[0]], [b[0]]),
            "COD in the tile-part" => JpxTests.AnnexG.Codestream(new JpxTests.AnnexG.Image
            {
                Components = [(8, false), (8, false), (8, false)], Exponent = 9, TileMarkers = _ => cod,
                Value = (_, c) => JpxTests.AnnexG.Coded.Full((c switch { 0 => r[0], 1 => g[0], _ => b[0] }) - 128),
            }),
            _ => JpxTests.AnnexG.Lossless8(4, 1, r, g, b),
        };
        if (change == "blocks 128 x 32")
        {
            var at = 4 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(4));
            Assert.Equal(0xFF52, BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(at)));
            (cs[at + 10], cs[at + 11]) = (5, 3);
        }
        var plain = JpxDecoder.Decode(WithRsiz(cs, 0), long.MaxValue);
        var named = JpxDecoder.Decode(WithRsiz(cs, rsiz), long.MaxValue);
        Assert.False(plain.Truncated);
        Assert.Equal(plain.Samples, named.Samples);
        Assert.Equal(reported, named.Truncated);
        // the same inside a JP2 file (the codestream behind the boxes)
        Assert.Equal(reported, JpxDecoder.Decode(PdfAJpeg2000.Wrap(WithRsiz(cs, rsiz))!, long.MaxValue).Truncated);
    }

    /// <summary>
    /// What the check finds: the profile Rsiz names where it is kept, nothing named for Rsiz 0 or 3; a broken restriction even where the
    /// codestream cannot be walked to its end (no EOC) — but a codestream that keeps its profile as far as it can be walked is no breach: it is
    /// not reported for the profile (whatever the decoder makes of the missing end).
    /// </summary>
    [Fact]
    public void ProfileCheck_BreachAgainstDamage()
    {
        var one = JpxTests.AnnexG.Lossless8(1, 1, [10], [20], [30]);
        var four = JpxTests.AnnexG.Lossless8(4, 1, [10, 20, 30, 40], [50, 60, 70, 80], [90, 100, 110, 120]);
        Assert.Equal(JpxProfileResult.Profile0, JpxProfile.Check(WithRsiz(one, 1)));
        Assert.Equal(JpxProfileResult.Profile1, JpxProfile.Check(WithRsiz(one, 2)));
        Assert.Equal(JpxProfileResult.Profile1, JpxProfile.Check(WithRsiz(four, 2)));
        Assert.Equal(JpxProfileResult.NotNamed, JpxProfile.Check(WithRsiz(four, 0)));
        Assert.Equal(JpxProfileResult.NotNamed, JpxProfile.Check(WithRsiz(four, 3)));
        Assert.Equal(JpxProfileResult.Broken, JpxProfile.Check(WithRsiz(four, 1)));
        Assert.Equal(JpxProfileResult.Broken, JpxProfile.Check(WithRsiz(four, 1)[..^2]));
        Assert.Equal(JpxProfileResult.Unreadable, JpxProfile.Check(WithRsiz(one, 1)[..^2]));
        Assert.Equal(JpxDecoder.Decode(WithRsiz(one, 0)[..^2], long.MaxValue).Truncated,
            JpxDecoder.Decode(WithRsiz(one, 1)[..^2], long.MaxValue).Truncated);
    }
}
