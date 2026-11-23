using System.Text;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Findings of the sentence-by-sentence assignment of T.801 (23.11.2026), fixed and tested here.</summary>
public sealed partial class PdfARepairTests
{
    /// <summary>
    /// T.801 M.6 and M.6.1.3: a codestream with the capabilities of ISO/IEC 15444-2 (Rsiz bit 15) of one or three colours is framed as JPX —
    /// brand „jpx “ and only „jpx “ in the compatibility list — neither „jp2 “ (a JP2 reader need only read Part 1 codestreams) nor „jpxb“
    /// (M.6.1.3: a Part 2 codestream is not baseline) —, the Reader Requirements with
    /// feature 6 alone in mask bit 0x80, APPROX 1 — so the image stays; before it was framed as JP2 (decided on 23.11.2026). A Part 1 codestream
    /// of three colours stays JP2.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Jpeg2000Codestream_Part2OneOrThreeColours_FramedAsJpx(int components)
    {
        var part2 = JpxRobustTests.Codestream(0, 64, 64, components, 1, 0, 1, 0);
        part2[6] = 0x80;   // Rsiz: capabilities of ISO/IEC 15444-2
        var wrapped = PdfAJpeg2000.Wrap(part2)!;
        Assert.Equal("jpx ", Encoding.ASCII.GetString(wrapped, 20, 4));                                       // brand
        Assert.Equal("jpx ", Encoding.ASCII.GetString(wrapped, 28, 4));                                       // compatibility list: only „jpx “
        Assert.Equal(20u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(wrapped.AsSpan(12)));    // file type box: one entry only
        Assert.Equal("rreq", Encoding.ASCII.GetString(wrapped, 36, 4));
        Assert.Equal(new byte[] { 1, 0x80, 0x80, 0, 1, 0, 6, 0x80, 0, 0 }, wrapped[40..50]);
        var colr = wrapped.AsSpan().IndexOf("colr"u8);
        Assert.Equal(1, wrapped[colr + 6]);                                                                    // APPROX
        Assert.Null(PdfAJpeg2000.Fault(wrapped));
        Assert.Equal(components, JpxDecoder.Decode(wrapped, 16_000_000).Channels);

        var part1 = JpxRobustTests.Codestream(0, 64, 64, 3, 1, 0, 1, 0);
        Assert.Equal("jp2 ", Encoding.ASCII.GetString(PdfAJpeg2000.Wrap(part1)!, 20, 4));
    }
}
