using PdfKnead.Core.Pdf.Filters.Jbig2;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The MQ decoder after ITU-T T.800, B.10.7 and Annex C: the end of the data and the marker, and the
/// decimal column of Table C.2. The coded data are made here with the encoder of the project from an own sequence of decisions.
/// </summary>
public sealed partial class Jbig2Tests
{
    /// <summary>An own sequence of decisions in three contexts (mostly 0, now and then 1), and its coded data with the closing marker.</summary>
    private static (int[] Decisions, byte[] Data) MqTestSequence(int count)
    {
        var decisions = new int[count];
        var seed = 20261123u;
        for (var i = 0; i < count; i++)
        {
            seed = seed * 1103515245 + 12345;
            decisions[i] = (seed >> 16) % (i % 3 == 2 ? 3u : 7u) == 0 ? 1 : 0;
        }
        var encoder = new MqEncoder();
        var contexts = new byte[3];
        for (var i = 0; i < count; i++) encoder.Encode(contexts, i % 3, decisions[i]);
        return (decisions, encoder.Finish());
    }

    private static int[] MqTestDecode(byte[] data, int count)
    {
        var decoder = new MqDecoder(data, 0, data.Length);
        var contexts = new byte[3];
        return [.. Enumerable.Range(0, count).Select(i => decoder.Decode(contexts, i % 3))];
    }

    /// <summary>
    /// T.800 B.10.7 (sentence 5, the note): an 0xFF left off at the end is made up by the arithmetic decoder — an own sequence of 380
    /// decisions, its data ending with a byte 0xFF and the marker byte 0xAC, decodes the same with them, without 0xAC and without both bytes
    /// (the last decisions of this sequence need the bits of that 0xFF; zeros in its place would give others).
    /// </summary>
    [Fact]
    public void MqDecoder_WithoutTheClosingBytes_SameDecisions()
    {
        var (decisions, data) = MqTestSequence(380);
        Assert.Equal([0xFF, 0xAC], data[^2..]);
        Assert.Contains(1, decisions);
        Assert.Equal(decisions, MqTestDecode(data, 380));
        Assert.Equal(decisions, MqTestDecode(data[..^1], 380));
        Assert.Equal(decisions, MqTestDecode(data[..^2], 380));
    }

    /// <summary>
    /// T.800 C.3 (sentences 77, 78): at a marker BP stays on its 0xFF and 1-bits are fed until the end — decoding 200 decisions more than the
    /// own sequence was coded with gives the same decisions with the data as they are, with four bytes of something else behind the marker,
    /// and with stuffed bytes "FF 7F" (seven 1-bits each) in place of the marker; the first 400 are the coded ones.
    /// </summary>
    [Fact]
    public void MqDecoder_AtTheMarker_StaysAndFeedsOnes()
    {
        var (decisions, data) = MqTestSequence(400);
        var plain = MqTestDecode(data, 600);
        Assert.Equal(decisions, plain[..400]);
        Assert.Equal(plain, MqTestDecode([.. data, 0x12, 0x34, 0x56, 0x78], 600));
        Assert.Equal(plain, MqTestDecode([.. data[..^2], .. Enumerable.Range(0, 60).SelectMany(_ => new byte[] { 0xFF, 0x7F })], 600));
    }

    /// <summary>
    /// T.800 C.2 (sentences 55, 66, 72), Table C.2, decimal column (values as data): Qe divided by (4/3) · 0x8000 is the probability of the
    /// table for each of the 47 indexes. The table rounds not quite by this rule (up to 8 · 10⁻⁶ off, e.g. index 0: 0.503929 against 0.503937),
    /// so within 1 · 10⁻⁵.
    /// </summary>
    [Fact]
    public void MqQe_AsTheDecimalColumnOfTableC2()
    {
        double[] table =
        [
            0.503937, 0.304715, 0.140650, 0.063012, 0.030053, 0.012474, 0.503937, 0.492218, 0.421904, 0.328153, 0.281277,
            0.210964, 0.164088, 0.128931, 0.503937, 0.492218, 0.474640, 0.421904, 0.328153, 0.304715, 0.281277, 0.234401,
            0.210964, 0.199245, 0.164088, 0.140650, 0.128931, 0.117212, 0.105493, 0.099634, 0.063012, 0.057153, 0.050561,
            0.030053, 0.024926, 0.015404, 0.012474, 0.007347, 0.006249, 0.003044, 0.001671, 0.000847, 0.000481, 0.000206,
            0.000114, 0.000023, 0.503937,
        ];
        Assert.Equal(47, MqDecoder.Qe.Length);
        for (var i = 0; i < 47; i++)
            Assert.True(Math.Abs(MqDecoder.Qe[i] / (4.0 / 3 * 0x8000) - table[i]) < 1e-5, $"Index {i}: {MqDecoder.Qe[i] / (4.0 / 3 * 0x8000):F6} statt {table[i]}");
    }
}
