using System.Text;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>ITU-T T.801 M.6.1.3: the fallbacks of the compatibility list, as the reader chooses its way.</summary>
public sealed partial class JpxColourTests
{
    /// <summary>A JPX file with brand „jpx “ and the compatibility list <paramref name="list"/> (sRGB, the codestream).</summary>
    private static byte[] AnnexMFallbackFile(byte[] codestream, string list) =>
    [
        .. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
        .. Box("ftyp", Encoding.ASCII.GetBytes("jpx "), U32(0), Encoding.ASCII.GetBytes(list)),
        .. Box("jp2h", Enumerated(16, 0, 1)),
        .. Box("jp2c", codestream),
    ];

    /// <summary>
    /// T.801 M.6.1.3 (sentences 1, 12): the reader picks one of the fallbacks it can read; PDF-Knead always takes the same way (the first
    /// codestream, the first compositing layer) — the same file with the list „jp2 “, „jpx jpxb“, „jpxb jpx “ or „jpx “ gives the same samples,
    /// those of the codestream in sRGB.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("jp2 ")]
    [InlineData("jpx jpxb")]
    [InlineData("jpxb jpx ")]
    [InlineData("jpx ")]
    public void CompatibilityList_TheReaderChoosesTheSameWay(string list)
    {
        var cs = Rgb.Value;
        var image = JpxDecoder.Decode(AnnexMFallbackFile(cs, list), long.MaxValue);
        Assert.Equal((JpxColor.Rgb, 3), (image.Color, image.Channels));
        Assert.Equal(Plain(cs), image.Samples);
    }
}
