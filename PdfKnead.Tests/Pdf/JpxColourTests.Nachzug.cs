using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics.Icc;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Gaps left after the decisions of 23.11.2026 („read and report“): colour specification methods a JP2 file must not have (T.800 Table I.9;
/// docs/FEHLERGEDAECHTNIS.md, No. 1061), and two cases of T.801 so far only checked at the parser — a CBD with Ncbd 0x4001 and an MCC
/// collection with more than 16 384 outputs, each in a whole codestream.
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>
    /// T.800 Table I.9: in a JP2 file METH is 1 (enumerated) or 2 (restricted ICC); every other value is reserved — the methods 3 (any ICC), 4
    /// (vendor) and 5 (parameterized) belong to JPX (T.801 Table M.22). A JP2 file (brand „jp2 “, „jpx “ not compatible) with such a box ahead
    /// of an sRGB specification of lower precedence is read as the same boxes in a file that lists „jpx “ — the any-ICC profile and the
    /// parameterized specification chosen by their precedence, the others never — and reported; 1 and 2 are no damage. Before without a word.
    /// In the file that lists „jpx “ the methods 0 and 6 to 255 are reserved as well (T.801 Table M.22) and reported since 23.11.2026
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1073); 1 to 5 are no damage there.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(0, true)]
    [InlineData(6, true)]
    [InlineData(255, true)]
    public void ColourMethod_OtherThan1Or2InAJp2File_ReadAsInJpxAndReported(int method, bool reported)
    {
        var cs = Jp2HeaderBoxes.Three();
        var first = method switch
        {
            1 => Enumerated(17, 1),                                         // sRGB grey: not fitting three channels, passed over
            2 or 3 => Box("colr", [(byte)method, 1, 0], CalibratedProfile.RommRgb()),
            5 => Parameterized(1, 13, 0, true, precedence: 1),              // BT.709 primaries, sRGB curve, identity matrix (G, B, R)
            _ => Box("colr", [(byte)method, 1, 0], [.. new byte[16], 1, 2, 3]),
        };
        byte[][] header = [Jp2HeaderBoxes.Ihdr(2, 3, 3, 7), first, Enumerated(16)];
        var asJpx = Reported(Jp2HeaderBoxes.File(cs, header, "jp2 jpx "));
        var asJp2 = Reported(Jp2HeaderBoxes.File(cs, header));
        Assert.Equal(method is 0 or 6 or 255, asJpx.Truncated);
        Assert.True(Same(asJpx, asJp2));
        Assert.Equal(reported, asJp2.Truncated);
        // the methods 2, 3 and 5 are taken (the profile, the codes), the others are not: then the image is the one of sRGB alone
        var srgbOnly = Reported(Jp2HeaderBoxes.File(cs, [Jp2HeaderBoxes.Ihdr(2, 3, 3, 7), Enumerated(16)]));
        Assert.Equal(method is not (2 or 3 or 5), Same(asJp2, srgbOnly));

        static bool Same(JpxImage a, JpxImage b) =>
            a.Color == b.Color && a.Samples.SequenceEqual(b.Samples) && (a.IccProfile ?? []).SequenceEqual(b.IccProfile ?? []);
    }

    /// <summary>
    /// T.800 I.5.2: the brand decides — „jpx “ as brand, or among the compatible brands of a „jp2 “ file, makes METH 3 and 5 allowed; with
    /// „jp2 “ alone (also beside other brands) they are reported. A File Type box shorter than its fixed part lists no brands.
    /// </summary>
    [Fact]
    public void ColourMethod_ReservedOnlyWithoutJpxBrand()
    {
        var cs = Jp2HeaderBoxes.Three();
        byte[][] header = [Jp2HeaderBoxes.Ihdr(2, 3, 3, 7), Box("colr", [3, 1, 0], CalibratedProfile.RommRgb()), Enumerated(16)];
        Assert.False(Reported(Jpx(cs, header)).Truncated);
        Assert.False(Reported(Jp2HeaderBoxes.File(cs, header, "jpx ")).Truncated);
        Assert.False(Reported(Jp2HeaderBoxes.File(cs, header, "jp2 jpxbjpx ")).Truncated);
        Assert.True(Reported(Jp2HeaderBoxes.File(cs, header, "jp2 jpxb")).Truncated);
        Assert.True(Reported(Jp2HeaderBoxes.File(cs, header, "jp2 jpm ")).Truncated);
    }

    /// <summary>
    /// T.801 A.3.6, Table A.30: Ncbd 0x4001 names 16 385 components, beyond 16 384 — in a whole codestream with the array-based transformation
    /// (inverse identity with the offsets 128 in place of the level shift) and 16 385 depths: the segment counts as damaged, the components of
    /// SIZ are decoded (K.1.2) — three components of 8 bits, the image with a CBD equal to SIZ — and the image is reported.
    /// </summary>
    [TestPdfsFact]
    public void Cbd_16385Components_InAWholeCodestream_SizComponentsAndReported()
    {
        byte[][] rest = [Part2Segments.Offsets(1, 128, 128, 128), Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 1)),
            Part2Segments.Mco(0)];
        var defined = Reported(Part2Segments.File([Part2Segments.Cbd(0x8003, 0x07), .. rest]));
        Assert.False(defined.Truncated);
        var file = Part2Segments.File([Part2Segments.Cbd(0x4001, [.. Enumerable.Repeat((byte)0x07, 0x4001)]), .. rest]);
        var info = JpxCodestream.Parse(file, 0, file.Length);
        Assert.Equal(info.Components, info.Output);
        // three components of 8 bits: the spatially reconstructed values plus the offsets 128, within 0 … 255
        Part2Segments.Equal([.. Part2Segments.Raw().Select(c => c.Select(v => Math.Clamp(v + 128, 0, 255)).ToArray())], Part2Segments.Planes(file));
        var image = Reported(file);
        Assert.Equal(defined.Samples, image.Samples);
        Assert.True(image.Truncated);
    }

    /// <summary>
    /// T.801 Table A.37: Mmcc counts 1 to 16 384 output components. A collection of 16 385 outputs (components 3 to 16 387, none of them shown;
    /// without a matrix only the first takes input 0) beside the identity for components 0 to 2 is read as given and reported; the same with
    /// 16 384 outputs is no damage. The shown components are the spatially reconstructed ones plus the offsets either way.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(16384, false)]
    [InlineData(16385, true)]
    public void Mcc_OutputsAbove16384_ReadAndReported(int outputs, bool reported)
    {
        var raw = Part2Segments.Raw();
        const int l = Part2Segments.Level;
        var many = Part2Segments.Collection([0], [.. Enumerable.Range(3, outputs)], 0, 0, wideOutputs: true);
        var identity = Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 1);
        var file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Offsets(1, l, l, l), Part2Segments.Mcc(0, many, identity),
            Part2Segments.Mco(0));
        Part2Segments.Equal([.. raw.Select(c => Part2Segments.Of(c, l))], Part2Segments.Planes(file));
        Assert.Equal(reported, JpxDecoder.DecodePlanes(file, long.MaxValue).Truncated);
    }
}
