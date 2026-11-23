using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ITU-T T.801 M.6, the Reader Requirements box of the JPX files PdfAJpeg2000.Wrap writes for CMYK: each feature needed on its own a column
/// (bit) of the masks, so the masks read „CMYK and the codestream“; checked as M.6.3 has a reader check it.
/// </summary>
public sealed partial class PdfARepairTests
{
    /// <summary>The Reader Requirements box of a file: mask length, the two masks, the standard features with their masks.</summary>
    private static (int Length, int FullyUnderstand, int Display, Dictionary<int, int> Features, int Vendors) ReaderRequirementsOf(byte[] file)
    {
        var at = file.AsSpan().IndexOf("rreq"u8);
        Assert.True(at > 0);
        var end = at - 4 + System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at - 4));
        var p = at + 4;
        int length = file[p];
        Assert.Equal(1, length);
        int full = file[p + 1], display = file[p + 2];
        var count = file[p + 3] << 8 | file[p + 4];
        var features = new Dictionary<int, int>();
        p += 5;
        for (var i = 0; i < count; i++, p += 3) features.Add(file[p] << 8 | file[p + 1], file[p + 2]);
        var vendors = file[p] << 8 | file[p + 1];
        Assert.Equal(end, p + 2);
        return (length, full, display, features, vendors);
    }

    /// <summary>M.6.3: a reader ORs the masks of the features it has and compares the result with a mask of the file.</summary>
    private static bool MeetsTheMask(Dictionary<int, int> features, int mask, params int[] reader)
    {
        var or = 0;
        foreach (var f in reader) or |= features.GetValueOrDefault(f);
        return (or & mask) == mask;
    }

    /// <summary>
    /// T.801 M.6.2 (sentences 5, 6), M.6.2.1 (sentences 1, 8, 20, 50, 119), M.6.2.2 (sentence 3): CMYK (feature 55) and the codestream feature
    /// Rsiz gives — Part 1 profile 1 (Rsiz 2: 4), unrestricted Part 1 (Rsiz 0 and another Part 1 value, 3: 5), Part 2 (Rsiz bit 15, also with
    /// further capability bits: 6) — are both needed, so each has a column of its own: 55 in bit 0x80, the codestream in 0x40, both masks 0xC0.
    /// A reader with only one of the two meets neither mask, one with both meets both; profile 0 (Rsiz 1) needs nothing beyond CMYK: 55 alone,
    /// masks 0x80.
    /// </summary>
    [Theory]
    [InlineData(0x0000, 5)]
    [InlineData(0x0002, 4)]
    [InlineData(0x0003, 5)]
    [InlineData(0x8000, 6)]
    [InlineData(0x8300, 6)]
    public void Jpeg2000Codestream_CmykAsJpx_EachNeededFeatureItsOwnColumn(int rsiz, int codestreamFeature)
    {
        var cmyk = JpxRobustTests.Codestream(0, 64, 64, 4, 1, 0, 1, 0);
        cmyk[6] = (byte)(rsiz >> 8);
        cmyk[7] = (byte)rsiz;
        var file = PdfAJpeg2000.Wrap(cmyk)!;
        var rreq = ReaderRequirementsOf(file);
        Assert.Equal((0xC0, 0xC0, 0), (rreq.FullyUnderstand, rreq.Display, rreq.Vendors));
        Assert.Equal(new Dictionary<int, int> { [55] = 0x80, [codestreamFeature] = 0x40 }, rreq.Features);
        foreach (var mask in new[] { rreq.FullyUnderstand, rreq.Display })
        {
            Assert.False(MeetsTheMask(rreq.Features, mask, 55));
            Assert.False(MeetsTheMask(rreq.Features, mask, codestreamFeature));
            Assert.False(MeetsTheMask(rreq.Features, mask, 55, codestreamFeature == 5 ? 6 : 5));
            Assert.True(MeetsTheMask(rreq.Features, mask, 55, codestreamFeature));
        }

        var profile0 = JpxRobustTests.Codestream(0, 64, 64, 4, 1, 0, 1, 0);
        profile0[7] = 1;
        var only = ReaderRequirementsOf(PdfAJpeg2000.Wrap(profile0)!);
        Assert.Equal((0x80, 0x80), (only.FullyUnderstand, only.Display));
        Assert.Equal(new Dictionary<int, int> { [55] = 0x80 }, only.Features);
        Assert.True(MeetsTheMask(only.Features, only.Display, 55));
    }
}
