using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>ITU-T T.801 M.11.5.1 sentence by sentence: the image header of the JPX and JP2 wrapper around a bare codestream.</summary>
public sealed partial class PdfARepairTests
{
    /// <summary>
    /// T.801 M.11.5.1, Tables M.20 and M.21: the BPC field of the image header carries the bit depth of all components as Ssiz has it — the
    /// value plus 1 the depth (0 for 1 bit up to 0x25 for 38 bits), the high bit the sign —, beside HEIGHT, WIDTH, NC, C 7 (JPEG 2000), UnkC 1
    /// (the colour space guessed from the number of components, docs/FEHLERGEDAECHTNIS.md, No. 1074) and IPR 0; for four components in the JPX wrapper, for one and three in the JP2 wrapper. A reserved depth (0x26) and signed components
    /// are not wrapped.
    /// </summary>
    [Theory]
    [InlineData(0x00, 1, true)]
    [InlineData(0x07, 3, true)]
    [InlineData(0x0F, 4, true)]
    [InlineData(0x25, 4, true)]
    [InlineData(0x26, 4, false)]
    [InlineData(0x87, 3, false)]
    public void Wrap_ImageHeader_BitDepthAsSsiz(byte ssiz, int components, bool wrapped)
    {
        var cs = JpxRobustTests.Codestream(0, 64, 64, components, 1, 0, 1, 0);
        for (var c = 0; c < components; c++) cs[42 + 3 * c] = ssiz;
        var file = PdfAJpeg2000.Wrap(cs);
        if (!wrapped)
        {
            Assert.Null(file);
            return;
        }
        Assert.NotNull(file);
        Assert.Equal(components == 4 ? "jpx " : "jp2 ", System.Text.Encoding.ASCII.GetString(file, 20, 4));
        var ihdr = file.AsSpan().IndexOf("ihdr"u8) + 4;
        Assert.Equal(22, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(ihdr - 8)));   // 8 + 14 bytes
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0, 0, 0, 64, 0, (byte)components, ssiz, 7, 1, 0 }, file[ihdr..(ihdr + 14)]);
    }
}
