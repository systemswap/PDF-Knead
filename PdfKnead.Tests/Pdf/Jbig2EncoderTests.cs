using PdfKnead.Core.Pdf.Filters.Jbig2;

namespace PdfKnead.Tests.Pdf;

/// <summary>JBIG2 encoder, always lossless: checked against the test sequence of the standard and round-tripped through the own decoder. See KONZEPT.md, section 5.12.</summary>
public sealed class Jbig2EncoderTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    [Fact]
    public void MqEncoder_MatchesTheTestSequenceOfTheStandard()
    {
        // ITU-T T.88, Annex H.2: 256 bits in the same context yield exactly these bytes
        var bits = Hex("00020051 000000C0 0352872A AAAAAAAA 82C02000 FCD79EF6 BF7FED90 4F46A3BF");
        var expected = Hex("84C73BFC E1A14304 02200000 410DBB86 F4317FFF 88FF3747 1ADB6ADF FFAC");
        var encoder = new MqEncoder();
        var cx = new byte[1];
        for (var i = 0; i < bits.Length * 8; i++) encoder.Encode(cx, 0, (bits[i >> 3] >> (7 - (i & 7))) & 1);
        Assert.Equal(expected, encoder.Finish());
    }

    [Fact]
    public void Integers_AndSymbolIds_RoundTrip()
    {
        int?[] values = [0, 1, -1, 3, 4, -4, 19, 20, 83, 84, 339, 340, 4435, 4436, 100_000, -100_000, int.MaxValue / 4, null, 7, null, -3];
        var encoder = new MqEncoder();
        var cx = new byte[512];
        var ids = new byte[1 << 9];
        foreach (var v in values) encoder.EncodeInt(cx, v);
        for (var id = 0; id < 256; id += 37) encoder.EncodeId(ids, 8, id);
        var data = encoder.Finish();
        var decoder = new MqDecoder(data, 0, data.Length);
        Array.Clear(cx);
        Array.Clear(ids);
        foreach (var v in values) Assert.Equal(v, decoder.DecodeInt(cx));
        for (var id = 0; id < 256; id += 37) Assert.Equal(id, decoder.DecodeId(ids, 8));
    }

    /// <summary>Generic region: various widths (including widths not divisible by 8), empty, full, identical rows (TPGDON), "text".</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 5)]
    [InlineData(8, 8)]
    [InlineData(9, 40)]
    [InlineData(301, 97)]
    [InlineData(1700, 120)]
    public void GenericRegion_RoundTripsThroughTheDecoder(int width, int height)
    {
        var rowBytes = (width + 7) / 8 + 1;   // wider than necessary, as with some masks
        var random = new Random(width * 31 + height);
        foreach (var kind in new[] { "leer", "voll", "schrift", "zufall" })
        {
            var bits = new byte[rowBytes * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var black = kind switch
                    {
                        "leer" => false,
                        "voll" => true,
                        "schrift" => y % 12 is > 2 and < 9 && (x * 7 + y / 12 * 3) % 11 < 4,
                        _ => random.Next(3) == 0,
                    };
                    if (!black) bits[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));   // PDF: 0 = black
                }
            Assert.Equal(0, Jbig2Encoder.Generic(bits, width, height, rowBytes)[58]);   // region flags: without TPGDON (with it one of the reference readers shows only noise), template 0, arithmetic coding
            foreach (var tpgdon in new[] { false, true })
            {
                var encoded = Jbig2Encoder.Generic(bits, width, height, rowBytes, tpgdon: tpgdon);
                var truncated = false;
                var decoded = Jbig2Decoder.Decode(encoded, null, width, height, ref truncated);
                Assert.False(truncated);
                var stride = (width + 7) / 8;
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                        Assert.True(((decoded[y * stride + (x >> 3)] ^ bits[y * rowBytes + (x >> 3)]) & (0x80 >> (x & 7))) == 0, $"{kind} {tpgdon} ({x}, {y})");
            }
        }
    }
    /// <summary>
    /// ISO 32000-2, 7.4.7: the embedded stream for /JBIG2Decode has no file header, no end-of-page and no end-of-file segment, and every segment
    /// belongs to page 1 (docs/FEHLERGEDAECHTNIS.md, No. 731: the encoder wrote an end-of-page segment).
    /// </summary>
    [Fact]
    public void Encoded_OnlyTheSegmentsPdfAllows()
    {
        var bits = new byte[4 * 10];
        bits[5] = 0x0F;
        var data = Jbig2Encoder.Generic(bits, 32, 10, 4);
        Assert.NotEqual((byte)0x97, data[0]);
        var types = new List<int>();
        for (var p = 0; p < data.Length;)
        {
            var type = data[p + 4] & 0x3F;
            Assert.Equal(0, data[p + 5]);   // no referred-to segments
            Assert.Equal(1, data[p + 6]);   // page 1
            var length = data[p + 7] << 24 | data[p + 8] << 16 | data[p + 9] << 8 | data[p + 10];
            types.Add(type);
            p += 11 + length;
        }
        Assert.Equal([48, 38], types);
    }
}
