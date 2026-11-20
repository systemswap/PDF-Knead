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
}
