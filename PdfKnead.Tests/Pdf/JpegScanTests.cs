using PdfKnead.Core.Pdf.Filters.Jpeg;
using PdfKnead.Rendering;
using W = PdfKnead.Tests.Pdf.TestJpegWriter;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Scans, tables and progressive Huffman coding of ITU-T T.81 one rule at a time (rows of T.81 split on 20.11.2026, the check for collective
/// rows). The images come from the test writer with given quantised coefficients: an image coded another way from the same coefficients gives
/// exactly the same samples, and libjpeg-turbo (the codec of the display before Stufe A) checks where it reads the image.
/// </summary>
public sealed class JpegScanTests
{
    private const int Width = 61, Height = 37;
    private static readonly byte[][] Quant = [W.Quant(2), W.Quant(4)];

    private static Dictionary<(int, int), (byte[] Counts, byte[] Symbols)> Tables(params (int Class, int Destination, (byte[], byte[]) Table)[] list) =>
        list.ToDictionary(t => (t.Class, t.Destination), t => t.Table);

    /// <summary>The samples of the image in the plain way: one interleaved baseline scan with the typical tables of K.3.</summary>
    private static byte[] Reference(W.Component[] comps) => JpegDecoder.Decode(W.Baseline(Width, Height, comps, Quant, withTables: true)).Samples;

    private static void SameAsTheCodec(byte[] jpeg, string what = "")
    {
        var own = JpegDecoder.Decode(jpeg);
        Assert.False(own.Damaged);
        var codec = SkiaImageCodec.Instance.Decode(jpeg, "DCTDecode", long.MaxValue);
        Assert.True(codec is not null, "libjpeg-turbo liest das Bild nicht");
        var (max, mean) = JpegDecoderTests.Compare(own, codec.Value);
        Assert.True(max == 0, $"{what}: größter Unterschied {max}, Mittel {mean:0.000}");
    }

    /// <summary>
    /// B.2.4.2, F.1.2: a Huffman table defined again for the same destination between the scans of a sequential image replaces the one before
    /// for the scans after it — three scans of one component each, all on destination 0, before the second the chrominance tables of K.3, before
    /// the third tables of their own (Bericht M2, Zeile 94; the old proof showed the opposite case, the quantisation table kept in progressive
    /// scans). B.2.4.1 likewise for a quantisation table: before the second scan destination 0 gets the table its component was quantised with
    /// (Zeile 332).
    /// </summary>
    [Fact]
    public void TablesDefinedAgainBetweenScans_ApplyFromThen()
    {
        var comps = W.Components(Width, Height, 3, 1, 1, Quant);
        var reference = Reference(comps);
        var onZero = comps.Select(c => c with { Td = 0, Ta = 0 }).ToArray();
        W.Scan[] three = [new([0], 0, 63, 0, 0), new([1], 0, 63, 0, 0), new([2], 0, 63, 0, 0)];
        var huffman = W.Huffman(Width, Height, onZero, Quant, three, 0xC0, index => index switch
        {
            0 => Tables((0, 0, W.Typical[0]), (1, 0, W.Typical[2])),
            1 => Tables((0, 0, W.Typical[1]), (1, 0, W.Typical[3])),
            _ => Tables((0, 0, W.Flat(false)), (1, 0, W.Flat(true))),
        });
        Assert.Equal(reference, JpegDecoder.Decode(huffman).Samples);
        SameAsTheCodec(huffman);

        // all components on quantisation table 0, which before the second scan becomes the table the chrominance was quantised with
        var oneTable = comps.Select(c => c with { Tq = 0 }).ToArray();
        byte[] dqt = [0xFF, 0xDB, 0, 67, 0, .. Quant[1]];
        var requantised = W.Huffman(Width, Height, oneTable, Quant, three, 0xC0,
            index => index == 0 ? Tables((0, 0, W.Typical[0]), (1, 0, W.Typical[2]), (0, 1, W.Typical[1]), (1, 1, W.Typical[3])) : null,
            index => index == 1 ? dqt : null);
        Assert.Equal(reference, JpegDecoder.Decode(requantised).Samples);
    }

    /// <summary>
    /// G.1.2.1–G.1.2.3: progressive Huffman coding — the DC with the point transform Al = 1 and its refinement, the AC coefficients in two bands
    /// with Al = 2, refined to 1 and 0, end-of-band runs and correction bits — gives the samples of the sequential image, as libjpeg-turbo does
    /// (Zeilen 136, 344; the old proof checked the codec, not the own decoder, and the test writer wrote progressive images only arithmetic).
    /// </summary>
    [Fact]
    public void ProgressiveHuffman_DcAndAcRefinement()
    {
        var comps = W.Components(Width, Height, 3, 1, 1, Quant);
        var scans = new List<W.Scan> { new([0, 1, 2], 0, 0, 0, 1) };
        for (var c = 0; c < 3; c++) scans.AddRange([new([c], 1, 5, 0, 2), new([c], 6, 63, 0, 2)]);
        for (var c = 0; c < 3; c++) scans.AddRange([new([c], 1, 63, 2, 1), new([c], 1, 63, 1, 0)]);
        scans.Add(new([0, 1, 2], 0, 0, 1, 0));
        var progressive = W.Huffman(Width, Height, comps, Quant, [.. scans], 0xC2,
            index => index == 0 ? Tables((0, 0, W.Flat(false)), (0, 1, W.Flat(false)), (1, 0, W.Flat(true)), (1, 1, W.Flat(true))) : null);
        Assert.Equal(Reference(comps), JpegDecoder.Decode(progressive).Samples);
        SameAsTheCodec(progressive);
    }

    /// <summary>
    /// G.1.2.2 Sätze 23, 24 and G.1.2.3 Sätze 14, 15, 17, 18 (with E.1.4): with restart intervals every interval starts with EOBRUN and the
    /// buffered correction bits at zero and sends what is left at its end — the end-of-band runs of the high band (50–63, mostly zero) would
    /// otherwise run across the restart markers. The same scans as above, in intervals of 1, 3 and 7 MCUs: the samples of the sequential image,
    /// as libjpeg-turbo gives them. And Satz 23 on the decoding side: a run sent at the end of an interval four blocks longer than the interval
    /// holds is cut off at the restart marker — the same samples.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void ProgressiveHuffman_RestartIntervals_RunsAndCorrectionsEndWithThem(int interval)
    {
        var comps = W.Components(Width, Height, 3, 1, 1, Quant);
        var scans = new List<W.Scan> { new([0, 1, 2], 0, 0, 0, 1) };
        for (var c = 0; c < 3; c++) scans.AddRange([new([c], 1, 49, 0, 2), new([c], 50, 63, 0, 2)]);
        for (var c = 0; c < 3; c++) scans.AddRange([new([c], 1, 49, 2, 1), new([c], 50, 63, 2, 1), new([c], 1, 63, 1, 0)]);
        scans.Add(new([0, 1, 2], 0, 0, 1, 0));
        var flat = Tables((0, 0, W.Flat(false)), (0, 1, W.Flat(false)), (1, 0, W.Flat(true)), (1, 1, W.Flat(true)));
        var progressive = W.Huffman(Width, Height, comps, Quant, [.. scans], 0xC2, index => index == 0 ? flat : null, restartInterval: interval);
        Assert.Equal(Reference(comps), JpegDecoder.Decode(progressive).Samples);
        SameAsTheCodec(progressive, $"Intervall {interval}");
        var overlong = W.Huffman(Width, Height, comps, Quant, [.. scans], 0xC2, index => index == 0 ? flat : null, restartInterval: interval,
            overlongRuns: 4);
        Assert.NotEqual(progressive, overlong);
        Assert.Equal(Reference(comps), JpegDecoder.Decode(overlong).Samples);
    }

    /// <summary>
    /// G.1.2.2 Sätze 4, 5, 20–22 (Table G.1): an end-of-band run is coded as EOBn with n additional bits, EOB14 the longest (16 384–32 767); a
    /// longer run goes as runs of 32 767 and the rest. A grey image of 2048 × 2048 with DC only: the AC scan is one run of 65 536 blocks — sent as
    /// 32 767, 32 767 and 2 — and gives the samples of the sequential image.
    /// </summary>
    [Fact]
    public void ProgressiveHuffman_EndOfBandRunAbove32767_Split()
    {
        const int size = 2048;
        byte[][] quant = [W.Quant(2)];
        var blocks = Enumerable.Range(0, size / 8).Select(y => Enumerable.Range(0, size / 8).Select(x =>
        {
            var zz = new short[64];
            zz[0] = (short)((x * 7 + y * 3) % 61 - 30);
            return zz;
        }).ToArray()).ToArray();
        W.Component[] grey = [new(1, 1, 1, 0, 0, 0, blocks)];
        var flat = Tables((0, 0, W.Flat(false)), (1, 0, W.Flat(true)));
        var progressive = W.Huffman(size, size, grey, quant, [new([0], 0, 0, 0, 0), new([0], 1, 63, 0, 0)], 0xC2, index => index == 0 ? flat : null);
        var sequential = W.Huffman(size, size, grey, quant, [new([0], 0, 63, 0, 0)], 0xC1, index => index == 0 ? flat : null);
        var image = JpegDecoder.Decode(progressive);
        Assert.False(image.Damaged);
        Assert.Equal(JpegDecoder.Decode(sequential).Samples, image.Samples);
    }

    /// <summary>
    /// F.1.3, F.2.3, G.1.1: an extended sequential or progressive frame has four destinations of each kind — four components, each with its
    /// own Huffman tables 0–3 in one scan (SOF1), progressive Huffman (SOF2), and arithmetic with its own conditioning per table, DAC for each
    /// of the four (SOF9, SOF10): the same samples as with the tables 0 and 1, from a writer of its own (Zeilen 341, 342, 343; the old proof
    /// used only destinations 2 and 3). libjpeg-turbo gives four components only converted to RGB, so it is compared on three components with
    /// the destinations 1 to 3, in all four frame types.
    /// </summary>
    [Fact]
    public void FourTableDestinations_HuffmanAndArithmetic()
    {
        var comps = W.Components(Width, Height, 4, 1, 1, Quant);
        var reference = JpegDecoder.Decode(W.Write(Width, Height, comps, Quant)).Samples;
        var four = comps.Select((c, i) => c with { Td = i, Ta = i }).ToArray();
        var all = Tables((0, 0, W.Typical[0]), (0, 1, W.Typical[1]), (0, 2, W.Flat(false)), (0, 3, W.Typical[0]),
            (1, 0, W.Typical[2]), (1, 1, W.Typical[3]), (1, 2, W.Flat(true)), (1, 3, W.Typical[3]));
        W.Scan[] interleaved = [new([0, 1, 2, 3], 0, 63, 0, 0)];
        var sequential = W.Huffman(Width, Height, four, Quant, interleaved, 0xC1, i => i == 0 ? all : null);
        Assert.Equal(reference, JpegDecoder.Decode(sequential).Samples);

        var flat = Tables((0, 0, W.Flat(false)), (0, 1, W.Flat(false)), (0, 2, W.Flat(false)), (0, 3, W.Flat(false)),
            (1, 0, W.Flat(true)), (1, 1, W.Flat(true)), (1, 2, W.Flat(true)), (1, 3, W.Flat(true)));
        var scans = new List<W.Scan> { new([0, 1, 2, 3], 0, 0, 0, 1) };
        for (var c = 0; c < 4; c++) scans.AddRange([new([c], 1, 63, 0, 1), new([c], 1, 63, 1, 0)]);
        scans.Add(new([0, 1, 2, 3], 0, 0, 1, 0));
        var progressive = W.Huffman(Width, Height, four, Quant, [.. scans], 0xC2, i => i == 0 ? flat : null);
        Assert.Equal(reference, JpegDecoder.Decode(progressive).Samples);

        var conditioning = new W.Conditioning([(0, 1), (2, 5), (1, 3), (0, 0)], [5, 40, 2, 63]);
        foreach (var arithmetic in new[] { W.Write(Width, Height, four, Quant, null, 0, conditioning), W.Write(Width, Height, four, Quant, [.. scans], 0, conditioning) })
            Assert.Equal(reference, JpegDecoder.Decode(arithmetic).Samples);

        // libjpeg-turbo hands four components out only as RGB, so it checks the destinations 1 to 3 on three components (YCbCr), each kind
        var three = W.Components(Width, Height, 3, 1, 1, Quant).Select((c, i) => c with { Td = i + 1, Ta = i + 1 }).ToArray();
        var threeScans = new List<W.Scan> { new([0, 1, 2], 0, 0, 0, 1) };
        for (var c = 0; c < 3; c++) threeScans.AddRange([new([c], 1, 63, 0, 1), new([c], 1, 63, 1, 0)]);
        threeScans.Add(new([0, 1, 2], 0, 0, 1, 0));
        var threeConditioning = new W.Conditioning([(0, 1), (2, 5), (1, 3), (0, 0)], [5, 40, 2, 63]);
        SameAsTheCodec(W.Huffman(Width, Height, three, Quant, [new([0, 1, 2], 0, 63, 0, 0)], 0xC1, i => i == 0 ? all : null), "SOF1");
        SameAsTheCodec(W.Huffman(Width, Height, three, Quant, [.. threeScans], 0xC2, i => i == 0 ? flat : null), "SOF2");
        SameAsTheCodec(W.Write(Width, Height, three, Quant, null, 0, threeConditioning), "SOF9");
        SameAsTheCodec(W.Write(Width, Height, three, Quant, [.. threeScans], 0, threeConditioning), "SOF10");
    }
}
