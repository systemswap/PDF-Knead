using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics.Icc;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Cells of T.801 Annex M counted since docs/FEHLERGEDAECHTNIS.md, No. 1121 (they stood in the same paragraph as their table's caption and were
/// left out): the compression types of Table M.19 other than 7, METHDAT of the other methods (M.11.7.2), reserved APPROX values (Table M.23)
/// and the bi-level spaces with the sample 1 (Table M.25); report M2, rows 3519–3527.
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>
    /// Table M.19: C other than 7 names data of another kind (uncompressed, T.4/T.6, T.82, JPEG, JPEG-LS …) in the codestream box. PDF-Knead
    /// decodes only JPEG 2000 codestreams: such data — without SOC — are refused, never handed to another decoder.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void ImageHeader_CompressionOtherThan7_ForeignDataRefused(int compression)
    {
        // packed samples of 40 × 27 × 3 bytes as an uncompressed or other coder would store them — no SOC marker
        var foreign = Enumerable.Range(0, 40 * 27 * 3).Select(i => (byte)(i * 7 + 1)).ToArray();
        var file = Jp2HeaderBoxes.File(foreign, [Jp2HeaderBoxes.Ihdr(27, 40, 3, 7, compression), Enumerated(16)]);
        Assert.ThrowsAny<Core.Pdf.PdfException>(() => JpxDecoder.Decode(file, long.MaxValue));
    }

    /// <summary>
    /// M.11.7.2: METHDAT of the methods 1, 2, 3 and 5 is not empty — a specification with an empty METHDAT (a box of 3 bytes) at a higher
    /// precedence is passed over without an exception, the sRGB specification after it counts.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void ColourSpec_EmptyMethodData_PassedOver_TheNextCounts(int method)
    {
        var cs = Rgb.Value;
        var image = JpxDecoder.Decode(Jpx(cs, [Box("colr", [(byte)method, 5, 1]), Enumerated(16)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Assert.Equal(Plain(cs), image.Samples);
    }

    /// <summary>
    /// Table M.23: APPROX 1 to 4 rank the accuracy, 5 to 255 are reserved — a specification with a reserved APPROX at the same precedence ranks
    /// after one with APPROX 4: the ICC profile of APPROX 4 counts, not the sRGB specification before it.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(5)]
    [InlineData(255)]
    public void ColourSpec_ReservedApprox_RanksAfterFour(int approx)
    {
        var cs = Rgb.Value;
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(16, approx: (byte)approx), Icc(CalibratedProfile.RommRgb(), approx: 4)]), long.MaxValue);
        Assert.Equal(JpxColor.Icc, image.Color);
    }

    /// <summary>
    /// Table M.25: bi-level (EnumCS 0) — the sample 1 is black; bi-level(2) (EnumCS 15) — the sample 1 is white. A codestream of one bit per sample
    /// with the samples 1 and 0 (the existing test showed only the 0).
    /// </summary>
    [Fact]
    public void BiLevel_SampleOne_BlackOrWhite()
    {
        var cs = JpxTests.AnnexG.Lossless(2, 1, [(1, false)], [[1, 0]]);
        Assert.Equal(new byte[] { 0, 255 }, JpxDecoder.Decode(Jpx(cs, [Enumerated(0)]), long.MaxValue).Samples);
        Assert.Equal(new byte[] { 255, 0 }, JpxDecoder.Decode(Jpx(cs, [Enumerated(15)]), long.MaxValue).Samples);
    }
}
