using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters.Jpeg;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The own JPEG decoder (ISO 32000-2, 7.4.8 with ITU-T T.81; decided on 12.11.2026, „Stufe A“): the same samples as the codec
/// of the display, and the limits against crafted files — every size from the data checked before memory is taken, invalid tables and
/// headers refused, damaged data reported.
/// </summary>
public sealed class JpegDecoderTests
{
    private static string TestPdf(string path, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return Path.Combine(dir.FullName, "TestPDFs", path);
        throw new DirectoryNotFoundException();
    }

    /// <summary>A JPEG made by the codec: a gradient with edges, so that the chroma and the AC coefficients have something to do.</summary>
    private static byte[] Encoded(int width, int height, int components, int quality = 90)
    {
        var samples = new byte[width * height * components];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var c = 0; c < components; c++)
                    samples[(y * width + x) * components + c] = (byte)((x * 7 + y * 3 + c * 60 + ((x / 5 + y / 4) % 2) * 90) & 0xFF);
        return SkiaImageCodec.Instance.EncodeJpeg(width, height, components, samples, quality) ?? throw new InvalidOperationException("Codec");
    }

    internal static (int Max, double Mean) Compare(JpegImage own, (int Width, int Height, int Components, byte[] Samples, bool AlreadyRgb) codec)
    {
        Assert.Equal((own.Width, own.Height), (codec.Width, codec.Height));
        int max = 0;
        long sum = 0, count = 0;
        for (var p = 0; p < own.Width * own.Height; p++)
            for (var c = 0; c < own.Channels; c++)
            {
                var d = Math.Abs(own.Samples[p * own.Channels + c] - codec.Samples[p * codec.Components + c]);
                max = Math.Max(max, d);
                sum += d;
                count++;
            }
        return (max, (double)sum / count);
    }

    /// <summary>Grey and colour JPEGs of the codec (baseline, 4:2:0): the own decoder gives the same samples within one step.</summary>
    [Theory]
    [InlineData(1, 61, 37)]
    [InlineData(3, 61, 37)]
    [InlineData(3, 16, 16)]
    [InlineData(3, 1, 1)]
    public void SameAsTheCodec(int components, int width, int height)
    {
        var jpeg = Encoded(width, height, components);
        var own = JpegDecoder.Decode(jpeg);
        var codec = SkiaImageCodec.Instance.Decode(jpeg, "DCTDecode", long.MaxValue)!.Value;
        Assert.False(own.Damaged);
        Assert.Equal(components, own.Channels);
        var (max, mean) = Compare(own, codec);
        Assert.True(max <= 1, $"größter Unterschied {max}, Mittel {mean:0.000}");
    }

    /// <summary>Decoding smaller (1/2, 1/4, 1/8 by the number of pixels asked for) keeps the size rounded up and the mean colour.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Reduced_RoundsUpAndKeepsTheColour(int scale)
    {
        var jpeg = Encoded(61, 37, 1);
        var full = JpegDecoder.Decode(jpeg);
        var small = JpegDecoder.Decode(jpeg, new JpegOptions(MaxPixels: (long)Math.Ceiling(61.0 / scale) * (long)Math.Ceiling(37.0 / scale)));
        Assert.Equal(scale, small.Scale);
        Assert.Equal(((61 + scale - 1) / scale, (37 + scale - 1) / scale), (small.Width, small.Height));
        Assert.InRange(small.Samples.Average(b => (double)b), full.Samples.Average(b => (double)b) - 4, full.Samples.Average(b => (double)b) + 4);
    }

    // ------------------------------------------------------------ crafted headers

    private static byte[] Segment(byte marker, params byte[] payload) =>
        [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

    private static readonly byte[] Dqt = Segment(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]);

    /// <summary>A Huffman table (class and number in <paramref name="tcTh"/>) with <paramref name="count"/> codes of length 1.</summary>
    private static byte[] Table(byte tcTh, int count, params byte[] symbols) => [tcTh, (byte)count, .. new byte[15], .. symbols];

    // a DC and an AC table with one code of length 1 each (symbol 0: DC difference 0, AC end of block)
    private static readonly byte[] Dht = Segment(0xC4, [.. Table(0x00, 1, 0), .. Table(0x10, 1, 0)]);

    private static byte[] Sof(byte marker, int width, int height, byte sampling = 0x11, byte precision = 8) =>
        Segment(marker, precision, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, sampling, 0);

    private static readonly byte[] Sos = Segment(0xDA, 1, 1, 0x00, 0, 63, 0);

    private static byte[] Jpeg(params byte[][] parts) => [0xFF, 0xD8, .. parts.SelectMany(p => p), 0xFF, 0xD9];

    /// <summary>A small valid image from the crafted parts: 16 × 8 grey, all blocks DC 0 → 128.</summary>
    [Fact]
    public void CraftedValid_DecodesGrey()
    {
        var image = JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), Dht, Sos, [0x00]));
        Assert.Equal((16, 8, 1), (image.Width, image.Height, image.Channels));
        Assert.All(image.Samples, s => Assert.Equal(128, s));
    }

    /// <summary>
    /// Sizes from the data are checked before memory is taken (security): 65 535 × 65 535 is beyond the pixels of an image; a progressive
    /// 30 000 × 30 000 in a few bytes claims more coefficients than its data can carry (No. 742); 700 scans over 20 000 × 20 000 are beyond the
    /// work limit — each refused at once.
    /// </summary>
    [Fact]
    public void CraftedSizes_RefusedBeforeMemory()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 65535, 65535), Dht, Sos, [0x00])));
        Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC2, 30000, 30000), Dht, Segment(0xDA, 1, 1, 0x00, 0, 0, 0), [0x00])));
        var sos = Segment(0xDA, 1, 1, 0x00, 0, 0, 0);
        byte[] many = [.. Enumerable.Repeat(sos, 700).SelectMany(s => s), .. new byte[2_000_000]];
        Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC2, 20000, 20000), Dht, many)));
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Scans that cost time without data (found by the fuzzer: seconds for a few kilobytes): in a sequential image a component's second scan is
    /// passed over (T.81, 3.1.116: each component in a single scan); a scan whose data is used up ends at once instead of decoding every block —
    /// the image stays as the first scan made it, reported as damaged.
    /// </summary>
    [Fact]
    public void EmptyScans_CostNoTime()
    {
        var sos = Segment(0xDA, 1, 1, 0x00, 0, 63, 0);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // 4 000 × 4 000 grey sequential: one scan with almost no data, then 1 000 repetitions of the scan header (250 million blocks to visit)
        var sequential = JpegDecoder.Decode(Jpeg([Dqt, Sof(0xC0, 4000, 4000), Dht, sos, [0x00], .. Enumerable.Repeat(sos, 1000)]));
        Assert.True(sequential.Damaged);
        Assert.All(sequential.Samples.AsSpan(0, 4000).ToArray(), v => Assert.Equal(128, v));
        // progressive: a first DC scan, then 1 000 empty AC scans of the same image
        var dcFirst = Segment(0xDA, 1, 1, 0x00, 0, 0, 0);
        var acFirst = Segment(0xDA, 1, 1, 0x00, 1, 63, 0);
        var progressive = JpegDecoder.Decode(Jpeg([Dqt, Sof(0xC2, 4000, 4000), Dht, dcFirst, [0x00], .. Enumerable.Repeat(acFirst, 1000)]));
        Assert.True(progressive.Damaged);
        Assert.True(watch.ElapsedMilliseconds < 3000, $"{watch.ElapsedMilliseconds} ms");
    }

    // ------------------------------------------------------------ reading tolerances (S-A A93–A101): read as common decoders read them

    /// <summary>S-A A93: no EOI at the end (T.81, B.2.1) — the image is complete, nothing reported.</summary>
    [Fact]
    public void Tolerance_NoEndMarker()
    {
        byte[] jpeg = [0xFF, 0xD8, .. Dqt, .. Sof(0xC0, 16, 8), .. Dht, .. Sos, 0x00];
        var image = JpegDecoder.Decode(jpeg);
        Assert.Equal((16, 8), (image.Width, image.Height));
        Assert.False(image.Damaged);
    }

    /// <summary>S-A A94: a scan naming its components in another order than the frame (T.81, A.2, B.2.3) — read in the order of the scan.</summary>
    [Fact]
    public void Tolerance_ComponentsOutOfOrderInTheScan()
    {
        var sof = Segment(0xC0, 8, 0, 8, 0, 8, 2, 1, 0x11, 0, 2, 0x11, 0);
        var sos = Segment(0xDA, 2, 2, 0x00, 1, 0x00, 0, 63, 0);
        var image = JpegDecoder.Decode(Jpeg(Dqt, sof, Dht, sos, [0x00]));
        Assert.Equal((8, 8, 2), (image.Width, image.Height, image.Channels));
        Assert.False(image.Damaged);
    }

    /// <summary>S-A A95: sampling factors whose blocks per MCU add up to more than 10 (T.81, B.2.3) — allowed by Adobe TN 5116 (SOF1).</summary>
    [Fact]
    public void Tolerance_MoreThanTenBlocksPerMcu()
    {
        var sof = Segment(0xC1, 8, 0, 16, 0, 32, 3, 1, 0x42, 0, 2, 0x11, 0, 3, 0x21, 0);
        var sos = Segment(0xDA, 3, 1, 0x00, 2, 0x00, 3, 0x00, 0, 63, 0);
        var image = JpegDecoder.Decode(Jpeg(Dqt, sof, Dht, sos, [0x00, 0x00, 0x00]));
        Assert.Equal((32, 16, 3), (image.Width, image.Height, image.Channels));
        Assert.False(image.Damaged);
    }

    /// <summary>S-A A96: a sequential scan with Ss, Se, Ah, Al other than 0, 63, 0, 0 (T.81, B.2.3) — the fixed values are used.</summary>
    [Fact]
    public void Tolerance_SequentialScanParameters()
    {
        var odd = Segment(0xDA, 1, 1, 0x00, 5, 10, 0x21);
        Assert.Equal(JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), Dht, Sos, [0x00])).Samples,
            JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), Dht, odd, [0x00])).Samples);
    }

    /// <summary>S-A A97: a quantisation table of 16-bit values in an 8-bit process (T.81, B.2.4.1) — read with its values.</summary>
    [Fact]
    public void Tolerance_SixteenBitQuantisationTable()
    {
        byte[] values = [0x10, .. Enumerable.Range(0, 64).SelectMany(_ => new byte[] { 0, 1 })];
        var image = JpegDecoder.Decode(Jpeg(Segment(0xDB, values), Sof(0xC0, 16, 8), Dht, Sos, [0x00]));
        Assert.All(image.Samples, v => Assert.Equal(128, v));
    }

    /// <summary>S-A A98: a Huffman code of only one bits (T.81, C: reserved as a prefix) — used as written.</summary>
    [Fact]
    public void Tolerance_HuffmanCodeOfOnlyOnes()
    {
        // DC: code 0 → difference 0, code 1 (all ones) → category 7; one block: 1, the magnitude 1111110 (+126), AC end of block 0
        var dht = Segment(0xC4, [.. Table(0x00, 2, 0, 7), .. Table(0x10, 1, 0)]);
        var image = JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 8, 8), dht, Sos, [0b1111_1110, 0b0000_0000]));
        Assert.False(image.Damaged);
        Assert.NotEqual(128, image.Samples[0]);
    }

    /// <summary>
    /// After the inverse DCT and the level shift the samples are clamped to 0–255 (ITU-T T.81, F.2.1.5; docs/normabgleich/M2-filter.md,
    /// row 128): one block whose DC coefficient (category 11, ±2047, times 255) lies far beyond eight bits gives white or black everywhere,
    /// not values wrapped around.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x7F, 0xF7 }, 255)]   // DC code, magnitude +2047, end of block, one bits as padding
    [InlineData(new byte[] { 0x00, 0x07 }, 0)]     // magnitude −2047
    public void Samples_ClampedToEightBits(byte[] scan, int expected)
    {
        var dqt = Segment(0xDB, [0, .. Enumerable.Repeat((byte)255, 64)]);
        var dht = Segment(0xC4, [.. Table(0x00, 1, 11), .. Table(0x10, 1, 0)]);
        var image = JpegDecoder.Decode(Jpeg(dqt, Sof(0xC0, 8, 8), dht, Sos, scan));
        Assert.False(image.Damaged);
        Assert.All(image.Samples, v => Assert.Equal(expected, v));
    }

    /// <summary>
    /// The colour conversion clamps its results to 0–255 (ITU-T T.871, clause 7; docs/normabgleich/M2-filter.md, row 147): three components
    /// without a marker are YCbCr; Y and Cr both at 255 make R = Y + 1.402 (Cr − 128) about 433, both at 0 about −179 — read as 255 and 0,
    /// not wrapped to 177 and 77; G, inside the range, is not clamped.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0xFF, 0x00, 0xF1, 0xFF, 0x00, 0xEF }, 255, 255)]   // Y +2047, Cb 0, Cr +2047 (each × 255), FF stuffed
    [InlineData(new byte[] { 0x80, 0x01, 0x00, 0x0F }, 0, 0)]                     // Y −2047, Cb 0, Cr −2047
    public void ColourConversion_ClampedToEightBits(byte[] scan, int red, int blue)
    {
        var dqt = Segment(0xDB, [0, .. Enumerable.Repeat((byte)255, 64)]);
        var dht = Segment(0xC4, [.. Table(0x00, 2, 0, 11), .. Table(0x10, 1, 0)]);   // DC: „0“ difference 0, „1“ category 11
        var sof = Segment(0xC0, 8, 0, 8, 0, 8, 3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0);
        var sos = Segment(0xDA, 3, 1, 0x00, 2, 0x00, 3, 0x00, 0, 63, 0);
        var image = JpegDecoder.Decode(Jpeg(dqt, sof, dht, sos, scan));
        Assert.False(image.Damaged);
        Assert.Equal(3, image.Channels);
        for (var p = 0; p < 64; p++)
        {
            Assert.Equal(red, image.Samples[p * 3]);
            Assert.InRange(image.Samples[p * 3 + 1], 80, 175);   // G = Y − 0.714 (Cr − 128): about 164 and 91
            Assert.Equal(blue, image.Samples[p * 3 + 2]);
        }
    }

    /// <summary>
    /// ITU-T T.871, clause 7: RGB from YCbCr by the exact relations — R = Y + 1.402 (Cr − 128), G = Y − (0.114 · 1.772 (Cb − 128) + 0.299 ·
    /// 1.402 (Cr − 128)) / 0.587, B = Y + 1.772 (Cb − 128), each Round(x) = ⌊x + 0.5⌋ and clamped to 0–255. All 2^24 combinations of Y, Cb, Cr,
    /// with and without the vector path: the decoder's fixed-point conversion (16-bit constants as libjpeg-turbo) lies within one step of the
    /// relations everywhere — the limit for every JPEG (SameAsTheCodec) — and gives exactly their value for 16 759 957 of the 16 777 216 (99.9 %,
    /// measured 22.11.2026 after FG 1010; the value reached is kept as the limit, ARBEITSWEISE Grundsatz 14).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void YccToRgb_TheRelationsOfT871Clause7(bool simd)
    {
        static int Round(double x) => Math.Clamp((int)Math.Floor(x + 0.5), 0, 255);
        var y = Enumerable.Range(0, 256).Select(v => (byte)v).ToArray();
        var cb = new byte[256];
        var cr = new byte[256];
        var rgb = new byte[256 * 3];
        long exact = 0, worst = 0;
        for (var b = 0; b < 256; b++)
            for (var r = 0; r < 256; r++)
            {
                Array.Fill(cb, (byte)b);
                Array.Fill(cr, (byte)r);
                JpegColor.YccToRgb(y, cb, cr, rgb, 256, simd);
                for (var l = 0; l < 256; l++)
                {
                    int[] want = [Round(l + 1.402 * (r - 128)), Round(l - (0.114 * 1.772 * (b - 128) + 0.299 * 1.402 * (r - 128)) / 0.587),
                        Round(l + 1.772 * (b - 128))];
                    var same = true;
                    for (var c = 0; c < 3; c++)
                    {
                        var d = Math.Abs(rgb[l * 3 + c] - want[c]);
                        worst = Math.Max(worst, d);
                        same &= d == 0;
                    }
                    if (same) exact++;
                }
            }
        Assert.True(worst <= 1, $"größte Abweichung {worst}");
        Assert.True(exact >= 16_759_957, $"genau gleich: {exact} von {1 << 24}");
    }

    /// <summary>
    /// FG 1010, measured against the reference reader: an image of 9 × 8 pixels, 4:4:4, every block DC only with Y = 0, Cb = 2, Cr = 97 (a
    /// value pair where the two conversion paths disagreed) — the first eight pixels of a row go the vector path, the ninth the scalar one.
    /// Every pixel is the same, and libjpeg-turbo gives exactly these samples (before: G 66 in the first eight, 65 in the ninth).
    /// </summary>
    [Fact]
    public void ColourConversion_LastPixelOfARow_AsLibjpegTurbo()
    {
        byte[][] quant = [[.. Enumerable.Repeat((byte)8, 64)], [.. Enumerable.Repeat((byte)8, 64)]];
        TestJpegWriter.Component Flat(int id, short dc, int table)
        {
            short[] Block() { var zz = new short[64]; zz[0] = dc; return zz; }
            return new TestJpegWriter.Component(id, 1, 1, table, table, table, [[Block(), Block()]]);
        }
        // DC × 8 / 8 + 128: Y = 0, Cb = 2, Cr = 97
        var jpeg = TestJpegWriter.Baseline(9, 8, [Flat(1, -128, 0), Flat(2, -126, 1), Flat(3, -31, 1)], quant, withTables: true);
        var image = JpegDecoder.Decode(jpeg);
        Assert.False(image.Damaged);
        for (var p = 0; p < 9 * 8; p++)
            Assert.Equal(image.Samples.AsSpan(0, 3).ToArray(), image.Samples.AsSpan(p * 3, 3).ToArray());
        var (max, _) = Compare(image, SkiaImageCodec.Instance.Decode(jpeg, "DCTDecode", long.MaxValue)!.Value);
        Assert.Equal(0, max);
    }

    /// <summary>
    /// ITU-T T.871, 6.3 Sätze 2, 3: the JFIF marker is an APP0 segment whose first five bytes are the null-terminated string „JFIF“. With it
    /// three components named „R“, „G“, „B“ are YCbCr; an APP0 segment with „JFIF“ and another fifth byte is no JFIF marker — the components stay
    /// RGB, as without any marker (S-A A74).
    /// </summary>
    [Fact]
    public void Jfif_OnlyTheNullTerminatedString()
    {
        byte[][] quant = [TestJpegWriter.Quant(2), TestJpegWriter.Quant(4)];
        var comps = TestJpegWriter.Components(16, 8, 3, 1, 1, quant).Select((c, i) => c with { Id = "RGB"[i] }).ToArray();
        var plain = TestJpegWriter.Baseline(16, 8, comps, quant, true);
        byte[] With(byte fifth) => [0xFF, 0xD8, .. Segment(0xE0, [.. "JFIF"u8, fifth, 1, 2, 0, 0, 1, 0, 1, 0, 0]), .. plain.AsSpan(2)];
        var rgb = JpegDecoder.Decode(plain).Samples;
        Assert.NotEqual(rgb, JpegDecoder.Decode(With(0)).Samples);
        Assert.Equal(rgb, JpegDecoder.Decode(With((byte)' ')).Samples);
        Assert.Equal(rgb, JpegDecoder.Decode(With((byte)'X')).Samples);
    }

    /// <summary>S-A A99: a progressive AC scan before the first DC scan of the component (T.81, G.1.1.1.1) — decoded as it comes.</summary>
    [Fact]
    public void Tolerance_AcScanBeforeTheDcScan()
    {
        var ac = Segment(0xDA, 1, 1, 0x00, 1, 63, 0);
        var dc = Segment(0xDA, 1, 1, 0x00, 0, 0, 0);
        var image = JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC2, 16, 8), Dht, ac, [0x00], dc, [0x00]));
        Assert.Equal((16, 8), (image.Width, image.Height));
    }

    /// <summary>S-A A100: a restart marker with another number than the next (T.81, B.2.1) — taken, the interval goes on, reported as damaged.</summary>
    [Fact]
    public void Tolerance_RestartMarkerOutOfOrder()
    {
        var dri = Segment(0xDD, 0, 1);
        var image = JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), Dht, dri, Sos, [0x00, 0xFF, 0xD3, 0x00]));
        Assert.Equal((16, 8), (image.Width, image.Height));
        Assert.True(image.Damaged);
    }

    /// <summary>S-A A101: bytes between marker segments that are no marker (T.81, B.1.1.2) — passed over to the next marker, reported.</summary>
    [Fact]
    public void Tolerance_BytesBetweenMarkers()
    {
        var image = JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), [0x12, 0x34, 0x56], Dht, Sos, [0x00]));
        Assert.Equal((16, 8), (image.Width, image.Height));
        Assert.True(image.Damaged);
    }

    /// <summary>Invalid headers and tables (T.81, B.2): refused, never read past.</summary>
    [Fact]
    public void InvalidHeadersAndTables_Refused()
    {
        // a Huffman table promising three codes of length 1 (C.2: there is room for two)
        var tooMany = Segment(0xC4, Table(0x00, 3, 0, 1, 2));
        Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), tooMany, Sos, [0x00])));
        // sampling factor 5 (A.1.1: 1–4)
        Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8, 0x51), Dht, Sos, [0x00])));
        // 12 bits per sample (PDF: one byte per component, 7.4.8)
        Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC1, 16, 8, precision: 12), Dht, Sos, [0x00])));
        // lossless and hierarchical processes (not embodied: T.81, 7 asks only for the processes a decoder embodies; ISO 32000-2, 7.4.8)
        foreach (byte sof in new byte[] { 0xC3, 0xC5, 0xC6, 0xC7, 0xCB, 0xCD, 0xCE, 0xCF })
            Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, Sof(sof, 16, 8), Dht, Sos, [0x00])));
        // DAC (B.2.4.3): L above U, Kx 0 and 64, a destination above 3
        foreach (var dac in new[] { Segment(0xCC, 0x00, 0x12), Segment(0xCC, 0x10, 0), Segment(0xCC, 0x10, 64), Segment(0xCC, 0x04, 0x10) })
            Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, dac, Sof(0xC9, 16, 8), Sos, [0x00])));
        // a scan naming a component the frame does not have
        Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), Dht, Segment(0xDA, 1, 9, 0x00, 0, 63, 0), [0x00])));
        // a segment longer than the data
        Assert.Throws<JpegException>(() => JpegDecoder.Decode([0xFF, 0xD8, 0xFF, 0xDB, 0x40, 0x00, 0, 1, 2]));
        // no JPEG at all
        Assert.Throws<JpegException>(() => JpegDecoder.Decode([1, 2, 3]));
    }

    /// <summary>
    /// The values of T.81, Tables B.2 and B.3 at their bounds: X = 0 (Table B.2: 1–65 535), no component and five (PDF: 1–4, ISO 32000-2,
    /// 7.4.8), a vertical sampling factor 0 (1–4), quantisation table 4 (0–3) (the length Lf: Lengths_BeyondTablesB2B3B7_ReadAndReported); a scan with no
    /// component and with five (Table B.3: 1–4) and one naming DC table 4 (0–3) — each refused. The frame at the bounds themselves
    /// (X = 1, factors 4 × 4, table 3) is read.
    /// </summary>
    [Fact]
    public void TablesB2AndB3_ValuesOutsideRefused()
    {
        byte[] Frame(int width, int n, byte sampling = 0x11, byte tq = 0) =>
            Segment(0xC0, [8, 0, 8, (byte)(width >> 8), (byte)width, (byte)n, .. Enumerable.Range(1, n).SelectMany(i => new[] { (byte)i, sampling, tq })]);
        var dqt3 = Segment(0xDB, [3, .. Enumerable.Repeat((byte)1, 64)]);
        foreach (var frame in new[] { Frame(0, 1), Frame(16, 0), Frame(16, 5), Frame(16, 1, 0x10), Frame(16, 1, 0x11, 4) })
            Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, frame, Dht, Sos, [0x00])));
        foreach (var sos in new[] { Segment(0xDA, 0, 0, 63, 0), Segment(0xDA, 5, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 0, 63, 0), Segment(0xDA, 1, 1, 0x40, 0, 63, 0) })
            Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), Dht, sos, [0x00])));
        var bounds = JpegDecoder.Decode(Jpeg(dqt3, Frame(1, 1, 0x44, 3), Dht, Sos, [0x00]));
        Assert.Equal((1, 8), (bounds.Width, bounds.Height));
    }

    /// <summary>
    /// T.81, Annex C: BITS gives the number of codes of each length 1–16, HUFFVAL the symbols in order of rising length — within a length in
    /// any order, the codes going to them in list order. The sizes and codes after the procedures of Figures C.1 and C.2 (written out here),
    /// with own lists: one code of length 2, three of 3, two of 5, one of 16 → 00, 010, 011, 100, 10100, 10101, 1011000000000000 (worked out
    /// by hand). C.3: a code goes into the bytes from the most significant bit on, the integer after it follows with its own most significant
    /// bit first. The decoder reads a stream so written back to the same symbols and integers.
    /// </summary>
    [Fact]
    public void HuffmanCodes_AsFiguresC1AndC2_BitsFromTheTop()
    {
        int[] bits = [0, 1, 3, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1];
        byte[] values = [0x42, 0x07, 0xA1, 0x13, 0x00, 0x5C, 0xFE];
        // Figure C.1: Generate_size_table
        var size = new List<int>();
        for (var i = 1; i <= 16; i++)
            for (var j = 1; j <= bits[i - 1]; j++) size.Add(i);
        // Figure C.2: Generate_code_table
        var code = new List<int>();
        int k = 0, c = 0, si = size[0];
        while (k < size.Count)
        {
            while (k < size.Count && size[k] == si)
            {
                code.Add(c++);
                k++;
            }
            c <<= 1;
            si++;
        }
        Assert.Equal([2, 3, 3, 3, 5, 5, 16], size);
        Assert.Equal([0b00, 0b010, 0b011, 0b100, 0b10100, 0b10101, 0b1011000000000000], code);

        // symbols, each followed by an integer of as many bits as its low nibble says (C.3: most significant bit first)
        (int Index, int Extra)[] stream = [(6, 0), (1, 5), (0, 0), (4, 0), (3, 1), (5, 0), (2, 0), (6, 0), (1, 3)];
        var bitList = new List<int>();
        foreach (var (index, extra) in stream)
        {
            for (var b = size[index] - 1; b >= 0; b--) bitList.Add(code[index] >> b & 1);
            var n = values[index] & 15;
            for (var b = n - 1; b >= 0; b--) bitList.Add(extra >> b & 1);
        }
        while (bitList.Count % 8 != 0) bitList.Add(1);   // padded with ones (B.1.1.5)
        var bytes = new List<byte>();
        for (var at = 0; at < bitList.Count; at += 8)
        {
            var value = (byte)Enumerable.Range(0, 8).Aggregate(0, (v, b) => v << 1 | bitList[at + b]);
            bytes.Add(value);
            if (value == 0xFF) bytes.Add(0);
        }
        var table = new JpegHuffmanTable([0, .. bits], values);   // counts by length, index 1–16
        var reader = new JpegBits([.. bytes], 0, bytes.Count);
        foreach (var (index, extra) in stream)
        {
            Assert.Equal(values[index], reader.Decode(table));
            var n = values[index] & 15;
            if (n > 0) Assert.Equal(extra, reader.Read(n));
        }
    }

    /// <summary>
    /// T.81, F.1.2.1.1, F.1.2.2.1 (Tables F.1, F.2) and F.2.2.1 (EXTEND): every value of the categories SSSS 1–11 (DC differences; AC values
    /// up to 10) — category SSSS holds ±2^(SSSS−1) … ±(2^SSSS − 1). The SSSS additional bits are the low bits of the value when it is
    /// positive and of value − 1 when it is negative, so their first bit is 1 for positive and 0 for negative values; read back with EXTEND
    /// they give the value again.
    /// </summary>
    [Fact]
    public void Categories_AdditionalBitsOfBothSigns_Extended()
    {
        for (var ssss = 1; ssss <= 11; ssss++)
        {
            var values = Enumerable.Range(1 << (ssss - 1), 1 << (ssss - 1)).SelectMany(v => new[] { v, -v }).ToList();
            Assert.Equal(1 << ssss, values.Count);
            foreach (var v in values)
            {
                Assert.Equal(ssss, 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Abs(v)));   // CSIZE of Tables F.1, F.2
                var extra = (v > 0 ? v : v - 1) & ((1 << ssss) - 1);
                Assert.Equal(v > 0 ? 1 : 0, extra >> (ssss - 1));
                // the bits at the top of two bytes, the rest ones
                var word = extra << (16 - ssss) | ((1 << (16 - ssss)) - 1);
                byte[] data = [(byte)(word >> 8), (byte)word];
                if (data[0] == 0xFF) data = [0xFF, 0x00, data[1]];
                if (data[^1] == 0xFF) data = [.. data, 0x00];
                Assert.Equal(v, new JpegBits(data, 0, data.Length).Receive(ssss));
            }
        }
    }

    /// <summary>
    /// FG 1011, S-A A119 (T.81, Tables B.2, B.3, B.7: Lf = 8 + 3 × Nf, Ls = 6 + 2 × Ns, Lr = 4): a frame header three bytes longer than its
    /// component specifications, a scan header two bytes longer than its parameters, a DRI segment two bytes longer — each read by its length,
    /// the same samples, and reported damaged (the three reference readers refuse all three, tools/normabgleich/proben/jpeg_werte.py). A
    /// frame header shorter than its components ask is refused.
    /// </summary>
    [Fact]
    public void Lengths_BeyondTablesB2B3B7_ReadAndReported()
    {
        var reference = JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), Dht, Sos, [0x0F]));
        Assert.False(reference.Damaged);
        var longFrame = Segment(0xC0, 8, 0, 8, 0, 16, 1, 1, 0x11, 0, 0, 0, 0);
        var longScan = Segment(0xDA, 1, 1, 0x00, 0, 63, 0, 0, 0);
        var longDri = Segment(0xDD, 0, 0, 0, 0);
        foreach (var image in new[]
                 {
                     JpegDecoder.Decode(Jpeg(Dqt, longFrame, Dht, Sos, [0x0F])),
                     JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), Dht, longScan, [0x0F])),
                     JpegDecoder.Decode(Jpeg(Dqt, longDri, Sof(0xC0, 16, 8), Dht, Sos, [0x0F])),
                 })
        {
            Assert.True(image.Damaged);
            Assert.Equal(reference.Samples, image.Samples);
        }
        Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, Segment(0xC0, 8, 0, 8, 0, 16, 2, 1, 0x11, 0), Dht, Sos, [0x0F])));
    }

    /// <summary>
    /// S-A A120 (T.81, Table B.3: Tdj and Taj 0–1 in a baseline frame): a baseline image with its Huffman tables in destination 2 — read with
    /// them, as all three reference readers do, and reported damaged (tools/normabgleich/proben/jpeg_werte.py).
    /// </summary>
    [Fact]
    public void Baseline_TableDestination2_ReadAndReported()
    {
        var reference = JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), Dht, Sos, [0x0F]));
        var dht2 = Segment(0xC4, [.. Table(0x02, 1, 0), .. Table(0x12, 1, 0)]);
        var image = JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), dht2, Segment(0xDA, 1, 1, 0x22, 0, 63, 0), [0x0F]));
        Assert.True(image.Damaged);
        Assert.Equal(reference.Samples, image.Samples);
        // extended sequential allows destinations 0–3: nothing reported
        Assert.False(JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC1, 16, 8), dht2, Segment(0xDA, 1, 1, 0x22, 0, 63, 0), [0x0F])).Damaged);
    }

    /// <summary>
    /// T.81, Table B.3 for progressive scans (Ss 0–63, Se Ss–63 and 0 when Ss is 0 — footnote c —, Ah and Al 0–13; G.1.1.1.1: an AC band of one
    /// component, Ah = Al + 1 in a refinement): a scan with Ss above Se, Se 64, Ss 0 with Se 5, Ah 14, Al 14, an interleaved AC band, a
    /// refinement with Ah ≠ Al + 1 — each passed over and reported, the image stays its size; Taj 4 is refused.
    /// </summary>
    [Fact]
    public void TableB3_ProgressionOutside_ScanPassedOver()
    {
        var dc = Segment(0xDA, 1, 1, 0x00, 0, 0, 0);
        byte[][] bad =
        [
            Segment(0xDA, 1, 1, 0x00, 5, 3, 0), Segment(0xDA, 1, 1, 0x00, 1, 64, 0), Segment(0xDA, 1, 1, 0x00, 0, 5, 0),
            Segment(0xDA, 1, 1, 0x00, 1, 63, 0xE0), Segment(0xDA, 1, 1, 0x00, 1, 63, 0x0E), Segment(0xDA, 1, 1, 0x00, 1, 63, 0x31),
        ];
        Assert.False(JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC2, 16, 8), Dht, dc, [0x00])).Damaged);
        foreach (var scan in bad)
        {
            var image = JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC2, 16, 8), Dht, dc, [0x00], scan, [0x00]));
            Assert.True(image.Damaged);
            Assert.Equal((16, 8), (image.Width, image.Height));
        }
        var two = Segment(0xC2, 8, 0, 8, 0, 16, 2, 1, 0x11, 0, 2, 0x11, 0);
        var interleavedAc = JpegDecoder.Decode(Jpeg(Dqt, two, Dht, Segment(0xDA, 2, 1, 0x00, 2, 0x00, 0, 0, 0), [0x00],
            Segment(0xDA, 2, 1, 0x00, 2, 0x00, 1, 63, 0), [0x00]));
        Assert.True(interleavedAc.Damaged);
        Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), Dht, Segment(0xDA, 1, 1, 0x04, 0, 63, 0), [0x00])));
    }

    /// <summary>
    /// T.81, B.2.4.1 Satz 46 (a quantisation table destination never defined: the result is unpredictable): the frame names table 1, only table 0
    /// is defined — the scan is passed over and reported, the image grey (zero coefficients).
    /// </summary>
    [Fact]
    public void QuantisationTableNeverDefined_ScanPassedOverAndReported()
    {
        var image = JpegDecoder.Decode(Jpeg(Dqt, Segment(0xC0, 8, 0, 8, 0, 16, 1, 1, 0x11, 1), Dht, Sos, [0x0F]));
        Assert.True(image.Damaged);
        Assert.All(image.Samples, s => Assert.Equal(128, s));
    }

    /// <summary>
    /// T.81, Tables B.4 and B.5: Pq 0 or 1, Tq 0–3, Tc 0 or 1, Th 0–3 — a DQT with Pq 2 or Tq 4, a DHT with Tc 2 or Th 4 is refused. A step Qk of 0
    /// (Table B.4: 1–255) is read as 1 (S-A A97): the same samples as with 1.
    /// </summary>
    [Fact]
    public void TablesB4AndB5_ValuesOutside()
    {
        byte[] ones = [.. Enumerable.Repeat((byte)1, 64)];
        foreach (var dqt in new[] { Segment(0xDB, [0x20, .. ones]), Segment(0xDB, [0x04, .. ones]) })
            Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(dqt, Sof(0xC0, 16, 8), Dht, Sos, [0x0F])));
        foreach (var dht in new[] { Segment(0xC4, [.. Table(0x00, 1, 0), .. Table(0x20, 1, 0)]), Segment(0xC4, [.. Table(0x04, 1, 0), .. Table(0x10, 1, 0)]) })
            Assert.Throws<JpegException>(() => JpegDecoder.Decode(Jpeg(Dqt, Sof(0xC0, 16, 8), dht, Sos, [0x0F])));
        byte[][] quant = [TestJpegWriter.Quant(2), TestJpegWriter.Quant(4)];
        var comps = TestJpegWriter.Components(16, 8, 1, 1, 1, quant);
        byte[][] zeros = [[.. quant[0].Select(q => q == 1 ? (byte)0 : q)], quant[1]];
        Assert.Contains((byte)0, zeros[0]);
        Assert.Equal(JpegDecoder.Decode(TestJpegWriter.Baseline(16, 8, comps, quant, true)).Samples,
            JpegDecoder.Decode(TestJpegWriter.Baseline(16, 8, comps, zeros, true)).Samples);
    }

    /// <summary>
    /// ITU-T T.872, clause 7 with 6.5.3: YCCK is made from CMYK by converting C, M, Y as R, G, B by the relations of T.871 and complementing K;
    /// transform 0 of the Adobe marker holds CMYK with all four values complemented. Own values C, M, Y, K = 30, 180, 90, 60: the YCC of the
    /// C, M, Y (T.871, clause 7, RGB → YCbCr with C, M, Y for R, G, B) and the complemented K go into DC-only blocks of an image with transform 2; the decoder
    /// gives back C, M, Y, K complemented (225, 75, 165, 195) within one step — the same form as transform 0, so the colour space of the PDF
    /// reads both alike.
    /// </summary>
    [Fact]
    public void Ycck_IsTheComplementedCmykOfT872()
    {
        int[] cmyk = [30, 180, 90, 60];
        double r = cmyk[0], g = cmyk[1], b = cmyk[2];
        int Y = (int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b), cb = (int)Math.Round((-0.299 * r - 0.587 * g + 0.886 * b) / 1.772 + 128),
            cr = (int)Math.Round((0.701 * r - 0.587 * g - 0.114 * b) / 1.402 + 128), k = 255 - cmyk[3];
        byte[][] quant = [[.. Enumerable.Repeat((byte)8, 64)]];
        TestJpegWriter.Component Flat(int id, int value)
        {
            short[] Block() { var zz = new short[64]; zz[0] = (short)(value - 128); return zz; }   // DC × 8 / 8 + 128 = value
            return new TestJpegWriter.Component(id, 1, 1, 0, 0, 0, [[Block()]]);
        }
        var jpeg = TestJpegWriter.Baseline(8, 8, [Flat(1, Y), Flat(2, cb), Flat(3, cr), Flat(4, k)], quant, withTables: true, adobe: 2);
        var image = JpegDecoder.Decode(jpeg);
        Assert.False(image.Damaged);
        Assert.Equal(4, image.Channels);
        for (var c = 0; c < 4; c++)
            Assert.InRange(image.Samples[c], 255 - cmyk[c] - 1, 255 - cmyk[c] + 1);
    }

    /// <summary>
    /// S-A A121 (ITU-T T.872, 6.5.3 Satz 1: „Adobe“ and a null byte in the first six bytes; Adobe TN 5116: „Adobe“ and a two-byte version): an
    /// APP14 segment „AdobeX“ with transform 0 is the Adobe marker too — the three components are RGB, as with „Adobe“ and version 100 (first
    /// byte 0) and as all three reference readers take it (tools/normabgleich/proben/jpeg_werte.py); without the marker they are YCbCr.
    /// </summary>
    [Fact]
    public void Adobe_FiveLettersAsTn5116()
    {
        byte[][] quant = [TestJpegWriter.Quant(2), TestJpegWriter.Quant(4)];
        var comps = TestJpegWriter.Components(16, 8, 3, 1, 1, quant);
        var plain = TestJpegWriter.Baseline(16, 8, comps, quant, true);
        byte[] With(byte sixth) => [0xFF, 0xD8, .. Segment(0xEE, [.. "Adobe"u8, sixth, 0x64, 0, 0, 0, 0, 0]), .. plain.AsSpan(2)];
        var rgb = JpegDecoder.Decode(With(0)).Samples;
        Assert.NotEqual(JpegDecoder.Decode(plain).Samples, rgb);
        Assert.Equal(rgb, JpegDecoder.Decode(With((byte)'X')).Samples);
    }

    /// <summary>
    /// FG 1012 (ITU-T T.871, clause 9: the samples of a subsampled component are centred on the grid of the full one; T.81, A.1.1 allows
    /// ratios like 4 : 3): Y with the factor 4, Cb and Cr with 3 — each Cb block of its own value (98, 128, 158), Y and Cr 128. Output pixel x
    /// takes the Cb sample nearest to it, k = ⌊(2x + 1) × 3 / 8⌋; so pixel 21 has the third block already (before ⌊3x / 4⌋: the second). The
    /// blue channel shows which: Y + 1.772 (Cb − 128). libjpeg-turbo does not read such ratios.
    /// </summary>
    [Fact]
    public void Upsampling_RatioFourToThree_CentredAsT871()
    {
        byte[][] quant = [[.. Enumerable.Repeat((byte)8, 64)], [.. Enumerable.Repeat((byte)8, 64)]];
        short[] Block(int value) { var zz = new short[64]; zz[0] = (short)(value - 128); return zz; }   // DC × 8 / 8 + 128 = value
        int[] cb = [98, 128, 158];
        TestJpegWriter.Component[] comps =
        [
            new(1, 4, 1, 0, 0, 0, [[Block(128), Block(128), Block(128), Block(128)]]),
            new(2, 3, 1, 1, 1, 1, [[.. cb.Select(Block)]]),
            new(3, 3, 1, 1, 1, 1, [[Block(128), Block(128), Block(128)]]),
        ];
        var image = JpegDecoder.Decode(TestJpegWriter.Baseline(32, 8, comps, quant, withTables: true));
        Assert.False(image.Damaged);
        for (var x = 0; x < 32; x++)
        {
            var k = (2 * x + 1) * 3 / 8;
            var blue = Math.Clamp((int)Math.Round(128 + 1.772 * (cb[k / 8] - 128)), 0, 255);
            for (var y = 0; y < 8; y++)
                Assert.InRange(image.Samples[(y * 32 + x) * 3 + 2], blue - 1, blue + 1);
        }
    }

    /// <summary>
    /// Ties of the ratio 3 : 2 (decided by the user on 22.11.2026 after the probe of TN 5116): a pixel halfway between two samples takes, as
    /// Adobe TN 5116, table 2, vertically the upper sample and horizontally the mean of both; the others the nearest sample, centred as T.871,
    /// clause 9. Y with factors 3 × 3, Cb and Cr with 2 × 2; the Cb blocks vary in both directions (one horizontal and one vertical AC
    /// coefficient each). The same Cb blocks without subsampling (16 × 16, all factors 2) give the Cb plane itself; the 24 × 24 image must take
    /// from it as the rule says (blue = Y + 1.772 (Cb − 128), Y and Cr 128).
    /// </summary>
    [Fact]
    public void Upsampling_RatioThreeToTwo_TiesAsTn5116Table2()
    {
        byte[][] quant = [[.. Enumerable.Repeat((byte)8, 64)], [.. Enumerable.Repeat((byte)8, 64)]];
        short[] Flat(int value) { var zz = new short[64]; zz[0] = (short)(value - 128); return zz; }
        short[] Varied(int dc, int across, int down) { var zz = Flat(dc); zz[1] = (short)across; zz[2] = (short)down; return zz; }
        short[][][] cb = [[Varied(110, 6, -5), Varied(140, -7, 4)], [Varied(125, 5, 6), Varied(135, -4, -6)]];
        short[][][] Flats(int n) => [.. Enumerable.Range(0, n).Select(_ => Enumerable.Range(0, n).Select(_ => Flat(128)).ToArray())];
        var sub = JpegDecoder.Decode(TestJpegWriter.Baseline(24, 24,
            [new(1, 3, 3, 0, 0, 0, Flats(3)), new(2, 2, 2, 1, 1, 1, cb), new(3, 2, 2, 1, 1, 1, Flats(2))], quant, withTables: true));
        var full = JpegDecoder.Decode(TestJpegWriter.Baseline(16, 16,
            [new(1, 2, 2, 0, 0, 0, Flats(2)), new(2, 2, 2, 1, 1, 1, cb), new(3, 2, 2, 1, 1, 1, Flats(2))], quant, withTables: true));
        Assert.False(sub.Damaged || full.Damaged);
        int Blue(int r, int c) => full.Samples[(r * 16 + c) * 3 + 2];
        var ties = 0;
        for (var y = 0; y < 24; y++)
        {
            int vk = (2 * y + 1) * 2, r = vk / 6 - (vk % 6 == 0 ? 1 : 0);
            for (var x = 0; x < 24; x++)
            {
                int hk = (2 * x + 1) * 2, c = Math.Min(15, hk / 6);
                var tie = hk % 6 == 0;
                var expected = tie ? (Blue(r, c - 1) + Blue(r, c) + 1) / 2 : Blue(r, c);
                if (tie) ties++;
                Assert.InRange(sub.Samples[(y * 24 + x) * 3 + 2], expected - 1, expected + 1);
            }
        }
        Assert.True(ties > 0);
    }

    /// <summary>Cut off in the middle of the scan: reported as damaged, the rest grey (zero coefficients), no exception.</summary>
    [Fact]
    public void CutOff_ReportedAndGrey()
    {
        var jpeg = Encoded(64, 64, 1, 95);
        var scan = jpeg.AsSpan().LastIndexOf([(byte)0xFF, (byte)0xDA]);
        var cut = jpeg[..(scan + (jpeg.Length - scan) / 3)];
        var image = JpegDecoder.Decode(cut);
        Assert.True(image.Damaged);
        Assert.Equal((64, 64), (image.Width, image.Height));
        Assert.All(image.Samples.AsSpan(63 * 64, 64).ToArray(), s => Assert.Equal(128, s));
    }

    /// <summary>
    /// The number of lines (T.81, B.2.2, B.2.5): 0 in the header with a DNL segment after the first scan gives the DNL's number; without one
    /// the /Height of the PDF (S-A A15); without both the image is refused.
    /// </summary>
    [Fact]
    public void HeightZero_FromTheDnlOrThePdf()
    {
        byte[] Image(bool dnl) => Jpeg(Dqt, Sof(0xC0, 16, 0), Dht, Sos, [0x00], dnl ? Segment(0xDC, 0, 8) : []);
        Assert.Equal(8, JpegDecoder.Decode(Image(true)).Height);
        Assert.Equal(8, JpegDecoder.Decode(Image(false), new JpegOptions(PdfHeight: 8)).Height);
        Assert.Throws<JpegException>(() => JpegDecoder.Decode(Image(false)));
    }

    /// <summary>
    /// /ColorTransform (ISO 32000-2, 7.4.8, Table 13): an Adobe marker decides; without one the dictionary entry; without both 1 for three
    /// components. A YCCK JPEG of the public collection read without its marker but with /ColorTransform 1 gives the same CMYK.
    /// </summary>
    [TestPdfsFact]
    public void ColorTransform_MarkerThenEntryThenDefault()
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(TestPdf("pdfium/fx/other/1.pdf")));
        byte[]? jpeg = null;
        foreach (var page in doc.Pages)
            if (page.Resources?.GetDictionary(PdfName.Get("XObject")) is { } xobjects)
                foreach (var key in xobjects.Keys)
                    if (xobjects.Get(key) is PdfStream s && s.Dictionary.Get(PdfName.Filter) is PdfName { Value: "DCTDecode" })
                        jpeg ??= doc.DecodeStream(s).Data;
        Assert.NotNull(jpeg);
        var withMarker = JpegDecoder.Decode(jpeg);
        Assert.Equal(4, withMarker.Channels);
        // without the Adobe marker: the entry decides (1 → YCCK), no entry → none (default 0 for four components)
        var bare = WithoutAdobe(jpeg);
        Assert.Equal(withMarker.Samples, JpegDecoder.Decode(bare, new JpegOptions(ColorTransform: 1)).Samples);
        Assert.NotEqual(withMarker.Samples, JpegDecoder.Decode(bare).Samples);
    }

    /// <summary>
    /// A CMYK JPEG blacked out under a bar (its left half; Stufe A, docs/FEHLERGEDAECHTNIS.md, No. 805): every sample of the right half is
    /// written back as the own decoder reads it — losslessly, in CMYK.
    /// </summary>
    [TestPdfsFact]
    public void Redacted_CmykSamplesBesideTheBar_AsDecoded()
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(TestPdf("pdfjs/cmykjpeg.pdf")));
        var list = PdfKnead.Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]);
        var image = list.Items.ToArray().First(i => i.Kind == PdfKnead.Core.Pdf.Graphics.DisplayItemKind.Image &&
                                                    list.Images[i.Image].Stream?.Dictionary.Get(PdfName.Filter) is PdfName { Value: "DCTDecode" });
        var jpeg = JpegDecoder.Decode(doc.DecodeStream(list.Images[image.Image].Stream!).Data);
        Assert.Equal(4, jpeg.Channels);
        var bar = new PdfRectangle(image.Left, image.Top, image.Left + (image.Right - image.Left) / 2, image.Bottom);
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, bar)));
        var written = PdfKnead.Core.Pdf.Content.PageInterpreter.Build(after, after.Pages[0]);
        var item = written.Items.ToArray().First(i => i.Kind == PdfKnead.Core.Pdf.Graphics.DisplayItemKind.Image && Math.Abs(i.Left - image.Left) < 1);
        var data = after.GetStreamData(written.Images[item.Image].Stream!);
        Assert.Equal(jpeg.Samples.Length, data.Length);
        for (var y = 0; y < jpeg.Height; y++)
            for (var x = jpeg.Width / 2 + 2; x < jpeg.Width; x++)
                for (var c = 0; c < 4; c++)
                    Assert.Equal(jpeg.Samples[(y * jpeg.Width + x) * 4 + c], data[(y * jpeg.Width + x) * 4 + c]);
    }

    /// <summary>
    /// A JPEG without its Huffman tables (as Motion-JPEG frames are stored): the scans take the typical tables of T.81, K.3 — the same
    /// samples as with the DHT segments, nothing reported damaged, and libjpeg-turbo reads it the same way (S-A A102). Before, the scans were
    /// passed over (a grey image, reported damaged). The images come from the test writer (the codec writes optimised tables); that libjpeg-turbo
    /// reads the one with tables like the own decoder checks the writer.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void MissingHuffmanTables_TypicalTablesOfK3(int components)
    {
        byte[][] quant = [TestJpegWriter.Quant(2), TestJpegWriter.Quant(4)];
        var comps = TestJpegWriter.Components(61, 37, components, 2, 2, quant);
        var limit = components == 1 ? 0 : 1;   // upsampled chroma: the rounding of the upsampling (SameAsTheCodec)
        var with = TestJpegWriter.Baseline(61, 37, comps, quant, true);
        var reference = JpegDecoder.Decode(with);
        Assert.False(reference.Damaged);
        var (max, _) = Compare(reference, SkiaImageCodec.Instance.Decode(with, "DCTDecode", long.MaxValue)!.Value);
        Assert.True(max <= limit, $"mit Tabellen: größter Unterschied zum Codec {max}");

        var without = TestJpegWriter.Baseline(61, 37, comps, quant, false);
        Assert.DoesNotContain(0xC4, Markers(without));
        var own = JpegDecoder.Decode(without);
        Assert.False(own.Damaged);
        Assert.Equal(reference.Samples, own.Samples);
        (max, _) = Compare(own, SkiaImageCodec.Instance.Decode(without, "DCTDecode", long.MaxValue)!.Value);
        Assert.True(max <= limit, $"ohne Tabellen: größter Unterschied zum Codec {max}");
    }

    /// <summary>
    /// Extended sequential (SOF1, T.81 F.1.3, F.2.3; F.2.1.4: up to four quantisation tables): tables in the destinations 2 and 3 — the same
    /// samples as the baseline image with the destinations 0 and 1, and as libjpeg-turbo.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ExtendedSequential_FourTableDestinations(int components)
    {
        byte[][] quant = [TestJpegWriter.Quant(3), TestJpegWriter.Quant(5)];
        var comps = TestJpegWriter.Components(61, 37, components, 1, 1, quant);
        var baseline = JpegDecoder.Decode(TestJpegWriter.Baseline(61, 37, comps, quant, true));
        var data = TestJpegWriter.Baseline(61, 37, comps, quant, true, destinations: 2);
        Assert.Equal(0xC1, JpegDecoder.ReadFrame(data)!.Value.Marker);
        var extended = JpegDecoder.Decode(data);
        Assert.False(extended.Damaged);
        Assert.Equal(baseline.Samples, extended.Samples);
        var (max, _) = Compare(extended, SkiaImageCodec.Instance.Decode(data, "DCTDecode", long.MaxValue)!.Value);
        Assert.Equal(0, max);
    }

    /// <summary>
    /// Fill bytes X'FF' before markers (T.81, B.1.1.2; E.2.1: discarded before the marker is read) — before every header segment, before the
    /// scan and between the image data and EOI: the same samples, nothing reported damaged.
    /// </summary>
    [Fact]
    public void FillBytes_BeforeMarkers()
    {
        var jpeg = Encoded(16, 8, 3);
        var filled = new List<byte> { 0xFF, 0xD8 };
        var at = 2;
        for (; jpeg[at + 1] != 0xDA; at += 2 + (jpeg[at + 2] << 8 | jpeg[at + 3]))
            filled.AddRange([0xFF, 0xFF, 0xFF, .. jpeg.AsSpan(at, 2 + (jpeg[at + 2] << 8 | jpeg[at + 3]))]);
        Assert.Equal(0xD9, jpeg[^1]);
        filled.AddRange([0xFF, 0xFF, .. jpeg.AsSpan(at, jpeg.Length - 2 - at), 0xFF, 0xFF, 0xFF, 0xD9]);
        var image = JpegDecoder.Decode([.. filled]);
        Assert.False(image.Damaged);
        Assert.Equal(JpegDecoder.Decode(jpeg).Samples, image.Samples);
    }

    /// <summary>
    /// T.81, B.1.1.3 (Table B.1) and B.1.1.4: markers marked with an asterisk stand alone — here TEM (X'FF01') — while every other marker
    /// starts a segment whose two-byte length counts itself but not the marker. Segments the decoder has no use for are passed over by that
    /// length alone, even when their content holds bytes that look like markers (a comment with X'FFD9' and X'FFDA', an application segment,
    /// a JPGn segment, a reserved RES marker): the same samples as without them, nothing damaged.
    /// </summary>
    [Fact]
    public void Markers_StandAloneOrSegmentByItsLength()
    {
        var jpeg = Encoded(16, 8, 3);
        var at = 2;
        while (jpeg[at + 1] != 0xDA) at += 2 + (jpeg[at + 2] << 8 | jpeg[at + 3]);
        var comment = Segment(0xFE, 0xFF, 0xD9, 0xFF, 0xDA, 0x00, 0x01);
        Assert.Equal(2 + 6, comment[2] << 8 | comment[3]);   // the length counts itself, not the marker
        byte[] extra = [0xFF, 0x01, .. comment, .. Segment(0xE5, 7, 0xFF, 0xD8), 0xFF, 0x01, .. Segment(0xF3, 0xFF, 0xC0), .. Segment(0x02, 1, 2, 3)];
        byte[] data = [.. jpeg.AsSpan(0, at), .. extra, .. jpeg.AsSpan(at)];
        var image = JpegDecoder.Decode(data);
        Assert.False(image.Damaged);
        Assert.Equal(JpegDecoder.Decode(jpeg).Samples, image.Samples);
    }

    /// <summary>
    /// A progressive AC scan with two components (T.81, G.1.1.1.1: only DC scans interleave; B.2.1) is passed over and reported damaged; the
    /// other scans give the image as without it.
    /// </summary>
    [Fact]
    public void Progression_InterleavedAcScan_PassedOver()
    {
        byte[][] quant = [TestJpegWriter.Quant(2), TestJpegWriter.Quant(4)];
        var comps = TestJpegWriter.Components(61, 37, 3, 1, 1, quant);
        TestJpegWriter.Scan[] good = [new([0, 1, 2], 0, 0, 0, 0), new([0], 1, 63, 0, 0), new([1], 1, 63, 0, 0), new([2], 1, 63, 0, 0)];
        TestJpegWriter.Scan[] bad = [good[0], new([0, 1], 1, 63, 0, 0), .. good[1..]];
        var reference = JpegDecoder.Decode(TestJpegWriter.Write(61, 37, comps, quant, good));
        Assert.False(reference.Damaged);
        var image = JpegDecoder.Decode(TestJpegWriter.Write(61, 37, comps, quant, bad));
        Assert.True(image.Damaged);
        Assert.Equal(reference.Samples, image.Samples);
    }

    /// <summary>
    /// Four-component JPEGs of the test writer — without Adobe marker, with transform 0 (CMYK) and 2 (YCCK; TN 5116, T.872): the own decoder
    /// reports nothing damaged and gives the samples libjpeg-turbo gives, compared as `pdf-knead jpegvergleich` does (the codec shows them as
    /// inverted CMYK multiplied out): within one step (the rounding of that product).
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    public void FourComponents_SameAsLibjpegTurbo(int adobe)
    {
        byte[][] quant = [TestJpegWriter.Quant(2), TestJpegWriter.Quant(4)];
        var comps = TestJpegWriter.Components(61, 37, 4, 1, 1, quant);
        var data = TestJpegWriter.Baseline(61, 37, comps, quant, true, adobe: adobe < 0 ? null : adobe);
        var own = JpegDecoder.Decode(data);
        Assert.False(own.Damaged);
        Assert.Equal(4, own.Channels);
        var codec = SkiaImageCodec.Instance.Decode(data, "DCTDecode", long.MaxValue)!.Value;
        Assert.Equal((own.Width, own.Height, 4), (codec.Width, codec.Height, codec.Components));
        var max = 0;
        for (var p = 0; p < own.Width * own.Height; p++)
        {
            int k = own.Samples[p * 4 + 3];
            for (var i = 0; i < 3; i++)
            {
                var x = own.Samples[p * 4 + i] * k + 128;
                max = Math.Max(max, Math.Abs(((x + (x >> 8)) >> 8) - codec.Samples[p * 4 + i]));
            }
        }
        Assert.True(max <= 1, $"größter Unterschied {max}");
    }

    /// <summary>
    /// The JFIF marker (ITU-T T.871): with it three components are YCbCr, also if they are named „R“, „G“, „B“ (without it S-A A74 reads
    /// them as RGB). Not right after SOI (T.871, 6.3 Satz 1) it still counts, as in libjpeg-turbo (S-A A104); a JFIF extension segment the
    /// decoder does not use (JFXX, here with a thumbnail holding markers of its own) is passed over (T.871, 6.4 Satz 3) — nothing damaged.
    /// </summary>
    [Fact]
    public void Jfif_DecidesTheColourAndExtensionsArePassedOver()
    {
        byte[][] quant = [TestJpegWriter.Quant(2), TestJpegWriter.Quant(4)];
        var comps = TestJpegWriter.Components(61, 37, 3, 1, 1, quant).Select((c, i) => c with { Id = "RGB"[i] }).ToArray();
        var plain = TestJpegWriter.Baseline(61, 37, comps, quant, true);
        byte[] jfif = Segment(0xE0, [.. "JFIF\0"u8, 1, 2, 0, 0, 1, 0, 1, 0, 0]);
        byte[] jfxx = Segment(0xE0, [.. "JFXX\0"u8, 0x10, 0xFF, 0xD8, 0xFF, 0xD9]);
        byte[] comment = Segment(0xFE, [.. "x"u8]);
        byte[] With(params byte[][] segments) => [0xFF, 0xD8, .. segments.SelectMany(x => x), .. plain.AsSpan(2)];
        var rgb = JpegDecoder.Decode(plain);
        var first = JpegDecoder.Decode(With(jfif));
        var late = JpegDecoder.Decode(With(comment, jfif));
        var extended = JpegDecoder.Decode(With(jfif, jfxx));
        Assert.All(new[] { rgb, first, late, extended }, image => Assert.False(image.Damaged));
        Assert.NotEqual(rgb.Samples, first.Samples);
        Assert.Equal(first.Samples, late.Samples);
        Assert.Equal(first.Samples, extended.Samples);
    }

    /// <summary>The markers of the segments before the first scan.</summary>
    private static List<int> Markers(byte[] jpeg)
    {
        var markers = new List<int>();
        for (var at = 2; at + 4 <= jpeg.Length && jpeg[at] == 0xFF && jpeg[at + 1] != 0xDA; at += 2 + (jpeg[at + 2] << 8 | jpeg[at + 3]))
            markers.Add(jpeg[at + 1]);
        return markers;
    }

    private static byte[] WithoutAdobe(byte[] jpeg)
    {
        for (var at = jpeg.AsSpan().IndexOf([(byte)0xFF, (byte)0xD8]) + 2; at + 4 <= jpeg.Length && jpeg[at] == 0xFF;)
        {
            var length = jpeg[at + 2] << 8 | jpeg[at + 3];
            if (jpeg[at + 1] == 0xEE && jpeg.AsSpan(at + 4, 5).SequenceEqual("Adobe"u8)) return [.. jpeg.AsSpan(0, at), .. jpeg.AsSpan(at + 2 + length)];
            if (jpeg[at + 1] == 0xDA) break;
            at += 2 + length;
        }
        throw new InvalidDataException("kein Adobe-Marker");
    }

    // ---- rows of T.81 split on 20.11.2026 (the check for collective rows): each rule its own test

    /// <summary>
    /// ITU-T T.81, B.2.4.6 and B.3: application segments (here APP1 with Exif, APP2 with a profile part, APP13) before the frame header change
    /// nothing — the samples are those without them (Bericht M2, Zeile 145).
    /// </summary>
    [Fact]
    public void ApplicationSegmentsBeforeTheFrame_SameSamples()
    {
        var jpeg = Encoded(24, 16, 3);
        byte[] with =
        [
            .. jpeg[..2],
            .. Segment(0xE1, [.. "Exif"u8, 0, 0, .. "MM"u8, 0, 42, 0, 0, 0, 8]),
            .. Segment(0xE2, [.. "ICC_PROFILE"u8, 0, 1, 1, .. new byte[40]]),
            .. Segment(0xED, [.. "Photoshop 3.0"u8, 0, .. "8BIM"u8]),
            .. jpeg[2..],
        ];
        Assert.Equal(JpegDecoder.Decode(jpeg).Samples, JpegDecoder.Decode(with).Samples);
    }

    /// <summary>
    /// ITU-T T.81, B.3 and Annex J: the hierarchical process (DHP, then frames joined by EXP) is not implemented — a file with a first frame,
    /// its scan, EXP and a differential frame (SOF5) is refused as a whole, not drawn from its first frame (Zeile 333).
    /// </summary>
    [Fact]
    public void HierarchicalProcess_Refused()
    {
        var dhp = Segment(0xDE, 8, 0, 8, 0, 8, 1, 1, 0x11, 0);
        var exp = Segment(0xDF, 0x11);
        var jpeg = Jpeg(Dqt, Dht, dhp, Sof(0xC0, 8, 8), Sos, [0x3F], exp, Sof(0xC5, 8, 8), Sos, [0x3F]);
        Assert.Throws<JpegException>(() => JpegDecoder.Decode(jpeg));
    }
}
