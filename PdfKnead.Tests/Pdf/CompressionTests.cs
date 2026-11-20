using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Compression.</summary>
public sealed class CompressionTests
{
    /// <summary>Encodes with G4 and decodes with the own decoder: bit-identical (text-like content, noise, edges, long runs).</summary>
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(17, 5, 1)]
    [InlineData(1728, 200, 2)]
    [InlineData(3000, 40, 3)]
    [InlineData(2551, 300, 4)]
    public void G4_RoundTrip(int width, int height, int pattern)
    {
        var rowBytes = (width + 7) / 8;
        var bits = new byte[rowBytes * height];
        var random = new Random(pattern * 31 + width);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var black = pattern switch
                {
                    0 => false,
                    1 => (x + y) % 3 == 0,
                    2 => (x / 7 + y / 9) % 5 == 0 || random.Next(200) == 0,   // text-like blocks and specks
                    3 => x > 2600 || y % 13 == 0,                             // runs longer than 2560
                    _ => random.Next(2) == 0,
                };
                if (black == false) bits[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));   // 0 = black (BlackIs1 false)
            }
        // Fill bits at the end of each row as the decoder produces them (white = 1)
        if (width % 8 != 0)
            for (var y = 0; y < height; y++) bits[y * rowBytes + rowBytes - 1] |= (byte)(0xFF >> (width % 8));

        var packed = CcittEncoder.EncodeG4(bits, width, height, rowBytes, blackIs1: false);
        var parms = new PdfDictionary();
        parms.Set(PdfName.Get("K"), PdfInteger.Of(-1));
        parms.Set(PdfName.Get("Columns"), PdfInteger.Of(width));
        parms.Set(PdfName.Get("Rows"), PdfInteger.Of(height));
        var truncated = false;
        var decoded = CcittFax.Decode(packed, parms, ref truncated);
        Assert.False(truncated);
        Assert.Equal(bits, decoded);
        if (pattern == 2) Assert.True(packed.Length < bits.Length / 4, $"{packed.Length} von {bits.Length}");
    }
}
