using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Storage format of decoded images: grey without a mask as 1 byte per pixel, stencil masks as coverage, everything else as
/// RGBA, with the same values as when stored as RGBA.
/// </summary>
public sealed class ImageFormatTests
{
    private static DecodedImage Decode(PdfDictionary dict, byte[] data, string content = "q 4 0 0 2 0 0 cm /Im Do Q")
    {
        var (doc, list) = InterpreterTests.Page(content, pdf =>
            TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(pdf.Add(new PdfStream(dict, data))))))));
        using (doc) return PdfImageDecoder.Decode(list.Images[0], doc)!;
    }

    private static PdfDictionary Image(params (string, PdfObject)[] extra) => TestPdf.Dict([
        ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(4)), ("Height", PdfInteger.Of(2)),
        .. extra]);

    [Fact]
    public void Jpeg_WithBytesBeforeTheStart_IsStillDecoded()
    {
        // bug1065245: inline image with CR LF after "ID"; the LF precedes FF D8, and Skia does not recognise the format with it
        var rgba = Enumerable.Range(0, 8 * 4 * 4).Select(i => (byte)(i % 4 == 3 ? 255 : 80)).ToArray();
        var jpeg = SkiaImageCodec.Instance.EncodeJpeg(8, 4, 4, rgba, 90)!;
        var image = Decode(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(8)),
            ("Height", PdfInteger.Of(4)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N("DCTDecode"))),
            [(byte)'\n', .. jpeg]);
        Assert.Equal((8, 4), (image.Width, image.Height));
        Assert.InRange(image.Rgba[0], 70, 90);
    }

    /// <summary>A 16 × 8 JPEG (red left, blue right) with the number of lines in its header replaced, and a DNL segment before its end.</summary>
    private static byte[] JpegWithLines(int lines, int? dnl)
    {
        var rgb = new byte[16 * 8 * 3];
        for (var i = 0; i < 16 * 8; i++)
            if (i % 16 < 8) rgb[i * 3] = 230;
            else rgb[i * 3 + 2] = 230;
        var jpeg = SkiaImageCodec.Instance.EncodeJpeg(16, 8, 3, rgb, 90)!;
        for (var at = 2; ; at += 2 + (jpeg[at + 2] << 8 | jpeg[at + 3]))
            if (jpeg[at + 1] is 0xC0 or 0xC1 or 0xC2)
            {
                (jpeg[at + 5], jpeg[at + 6]) = ((byte)(lines >> 8), (byte)lines);
                break;
            }
        return dnl is { } count ? [.. jpeg.AsSpan(0, jpeg.Length - 2), 0xFF, 0xDC, 0x00, 0x04, (byte)(count >> 8), (byte)count, 0xFF, 0xD9] : jpeg;
    }

    private static DecodedImage DecodeJpeg(byte[] jpeg, int height = 8, PdfDictionary? parms = null) => Decode(TestPdf.Dict(
        [("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(16)), ("Height", PdfInteger.Of(height)),
            ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N("DCTDecode")),
            .. parms is null ? Array.Empty<(string, PdfObject)>() : [("DecodeParms", parms)]]), jpeg);

    /// <summary>
    /// The number of lines of a JPEG in a DNL segment (0 in the header, or a scanner's placeholder) or not given at all: the height of the
    /// PDF is taken when it agrees, and the picture is shown. The codec reads no DNL (docs/FEHLERGEDAECHTNIS.md, No. 457, Analyse E-34).
    /// </summary>
    [Theory]
    [InlineData(0, 8)]
    [InlineData(0xFFFF, 8)]
    [InlineData(0xFF00, null)]
    [InlineData(0, null)]
    public void JpegWithItsLinesInADnlSegmentOrAPlaceholder_IsDecoded(int lines, int? dnl)
    {
        var image = DecodeJpeg(JpegWithLines(lines, dnl));
        Assert.Equal((16, 8), (image.Width, image.Height));
        Assert.InRange(image.Rgba[7 * 16 * 4], 200, 255);           // red at the left of the last line
        Assert.InRange(image.Rgba[(7 * 16 + 15) * 4 + 2], 200, 255);   // blue at its right
    }

    /// <summary>A DNL segment that does not agree with the PDF, or a header that already holds a number: the JPEG stays as it is (No. 457).</summary>
    [Theory]
    [InlineData(0xFFFF, 6)]
    [InlineData(8, 8)]
    [InlineData(8, null)]
    public void JpegWhoseLinesThePdfDoesNotConfirm_StaysAsItIs(int lines, int? dnl)
    {
        var jpeg = JpegWithLines(lines, dnl);
        Assert.Same(jpeg, Core.Pdf.Graphics.JpegHeader.Prepare(jpeg, null, 8));
    }

    /// <summary>
    /// /ColorTransform 0 of a JPEG with three components: the samples are RGB as they stand, not YCbCr — red, encoded as YCbCr, comes out as
    /// its Y, Cb and Cr. The entry was not read; the codec went by the JFIF marker (docs/FEHLERGEDAECHTNIS.md, No. 457, Analyse E-25).
    /// </summary>
    [Fact]
    public void DctColorTransformZero_TakesTheSamplesAsRgb()
    {
        var rgb = new byte[16 * 8 * 3];
        for (var i = 0; i < rgb.Length; i += 3) rgb[i] = 255;
        var jpeg = SkiaImageCodec.Instance.EncodeJpeg(16, 8, 3, rgb, 95)!;
        var plain = DecodeJpeg(jpeg);
        Assert.InRange(plain.Rgba[0], 240, 255);
        var asRgb = DecodeJpeg(jpeg, parms: TestPdf.Dict(("ColorTransform", PdfInteger.Of(0))));
        Assert.InRange(asRgb.Rgba[0], 70, 82);    // Y of red
        Assert.InRange(asRgb.Rgba[1], 79, 91);    // Cb
        Assert.InRange(asRgb.Rgba[2], 245, 255);  // Cr
        var asYcc = DecodeJpeg(jpeg, parms: TestPdf.Dict(("ColorTransform", PdfInteger.Of(1))));
        Assert.InRange(asYcc.Rgba[0], 240, 255);
    }

    /// <summary>
    /// The public test file bug_86459: a progressive JPEG with the placeholder 65,535 in its header and no DNL segment; the PDF gives 792
    /// lines. With the placeholder the codec expected more blocks in every scan than there are and gave nothing — the page showed the grey
    /// stand-in instead of the white scan with „BAD SCANNER!“ (No. 457).
    /// </summary>
    [TestPdfsFact]
    public void ProgressiveJpegWithAPlaceholderHeight_IsShown()
    {
        static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
        {
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
            return "";
        }
        using var doc = Core.Pdf.Document.PdfDocument.Open(File.ReadAllBytes(Path.Combine(Root(), "TestPDFs", "pdfium", "pdfium", "bug_86459.pdf")));
        using var bitmap = SkiaRenderer.RenderPage(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 0.5, new ImageCache());
        Assert.InRange((int)bitmap.GetPixel(20, 20).Red, 250, 255);   // white, not the grey stand-in
        var dark = 0;
        for (var x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, bitmap.Height * 25 / 49).Red < 100) dark++;
        Assert.True(dark > 10, $"{dark} dunkle Pixel auf der Zeile der Schrift");

        // under a bar over its left half the scan is blacked out, not left out: the right half of „BAD SCANNER!“ still shows. Before, the
        // image could not be decoded for blacking out and went from the page as a whole
        var items = Core.Pdf.Editing.PageItem.From(doc);
        items[0] = items[0].WithElements([new Core.Pdf.Editing.RedactionElement { Rect = new Core.Pdf.Document.PdfRectangle(0, 0, 300, 792) }]);
        using var saved = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(doc, items, saved, new Core.Pdf.Writing.PdfSaveOptions { ApplyRedactions = true, ImageCodec = SkiaImageCodec.Instance });
        using var after = Core.Pdf.Document.PdfDocument.Open(saved.ToArray());
        using var redacted = SkiaRenderer.RenderPage(Core.Pdf.Content.PageInterpreter.Build(after, after.Pages[0]), after, after.Pages[0], 0.5, new ImageCache());
        var right = 0;
        for (var x = 160; x < redacted.Width; x++)
            if (redacted.GetPixel(x, redacted.Height * 25 / 49).Red < 100) right++;
        Assert.True(right > 5, $"{right} dunkle Pixel rechts vom Balken");
    }

    [Fact]
    public void FineDitherMask_IsAveragedWhenReduced()
    {
        // issue11518: image mask as a dot screen, much finer than the image and above the limit for masked images. Taking only
        // one mask dot per target pixel gives full or empty columns depending on the phase (stripes). Averaged: half covered everywhere
        const int width = 4000, height = 1001;   // just above the limit of 4 million pixels
        var mask = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(width)),
            ("Height", PdfInteger.Of(height)), ("ImageMask", PdfBoolean.True));
        var bits = Enumerable.Repeat((byte)0b1010_1010, width / 8 * height).ToArray();   // every second column
        var (doc, list) = InterpreterTests.Page("q 4 0 0 2 0 0 cm /Im Do Q", pdf =>
            TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(pdf.Add(new PdfStream(
                Image(("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)), ("Mask", TestPdf.Ref(pdf.Add(TestPdf.FlateStream(bits, mask))))),
                [0, 64, 128, 255, 10, 20, 30, 40]))))))));
        using (doc)
        {
            var image = PdfImageDecoder.Decode(list.Images[0], doc)!;
            Assert.True(image.Width < width);   // scaled down
            var alpha = Enumerable.Range(0, image.Width * image.Height).Select(i => image.Rgba[i * 4 + 3]).ToArray();
            Assert.InRange(alpha.Min(), 90, 165);
            Assert.InRange(alpha.Max(), 90, 165);
        }
    }

    [Fact]
    public void BitMaskAndByteMask_AverageAlike()
    {
        // Packed 1-bit masks are counted bit by bit when scaled down, 8-bit masks value by value; with an uneven ratio
        // (4 001 → 2 001, 1 001 → 501) both must give the same opacity
        const int width = 4001, height = 1001, rowBytes = (width + 7) / 8;
        var random = new Random(11518);
        var bits = new byte[rowBytes * height];
        random.NextBytes(bits);
        var gray = new byte[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            gray[y * width + x] = (bits[y * rowBytes + (x >> 3)] >> (7 - (x & 7)) & 1) != 0 ? (byte)0 : (byte)255;   // bit 0 paints
        byte[] Alphas(string key, PdfDictionary maskDict, byte[] data)
        {
            var (doc, list) = InterpreterTests.Page("q 4 0 0 2 0 0 cm /Im Do Q", pdf =>
                TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(pdf.Add(new PdfStream(
                    Image(("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)), (key, TestPdf.Ref(pdf.Add(TestPdf.FlateStream(data, maskDict))))),
                    [0, 64, 128, 255, 10, 20, 30, 40]))))))));
            using (doc)
            {
                var image = PdfImageDecoder.Decode(list.Images[0], doc)!;
                Assert.Equal((2001, 501), (image.Width, image.Height));
                return Enumerable.Range(0, image.Width * image.Height).Select(i => image.Rgba[i * 4 + 3]).ToArray();
            }
        }
        PdfDictionary MaskDict(params (string, PdfObject)[] extra) => TestPdf.Dict([("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(width)), ("Height", PdfInteger.Of(height)), .. extra]);
        var fromBits = Alphas("Mask", MaskDict(("ImageMask", PdfBoolean.True)), bits);
        var fromBytes = Alphas("SMask", MaskDict(("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8))), gray);
        Assert.Equal(fromBytes, fromBits);
        Assert.InRange(fromBits.Select(a => (int)a).Average(), 120, 135);
    }

    [Fact]
    public void GrayImage_IsStoredAsGray()
    {
        var image = Decode(Image(("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8))), [0, 64, 128, 255, 10, 20, 30, 40]);
        Assert.Equal(ImagePixels.Gray, image.Format);
        Assert.Equal([0, 64, 128, 255, 10, 20, 30, 40], image.Pixels);
        Assert.Equal(new byte[] { 64, 64, 64, 255 }, image.Rgba[4..8]);   // as RGBA the same values as RGBA storage gives
    }

    /// <summary>
    /// TIFF predictor with 4 bits: each row 1, 1, 1, 1 as differences is 1, 2, 3, 4. The predictor was undone only for 8 and 16 bits; the
    /// image showed 1, 1, 1, 1 (docs/FEHLERGEDAECHTNIS.md, No. 456, Analyse E-24).
    /// </summary>
    [Fact]
    public void TiffPredictorWithFourBits_Undone()
    {
        using var packed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            z.Write([0x11, 0x11, 0x11, 0x11]);
        var image = Decode(Image(("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(4)), ("Filter", TestPdf.N("FlateDecode")),
            ("DecodeParms", TestPdf.Dict(("Predictor", PdfInteger.Of(2)), ("Colors", PdfInteger.Of(1)), ("BitsPerComponent", PdfInteger.Of(4)),
                ("Columns", PdfInteger.Of(4))))), packed.ToArray());
        Assert.Equal([17, 34, 51, 68, 17, 34, 51, 68], image.Pixels);
    }

    /// <summary>The same at 1 and 2 bits, with two colours at 2 bits, modulo the depth; the bits after the last sample stay (No. 456).</summary>
    [Theory]
    [InlineData(1, 1, 8, new byte[] { 0b1000_1000 }, new byte[] { 0b1111_0000 })]
    [InlineData(1, 1, 5, new byte[] { 0b1000_0101 }, new byte[] { 0b1111_1101 })]
    [InlineData(2, 2, 3, new byte[] { 0b0111_1101, 0b1100_0000 }, new byte[] { 0b0111_0000, 0b1100_0000 })]
    public void TiffPredictorWithFewBits_Undone(int bits, int colors, int columns, byte[] encoded, byte[] expected)
    {
        var parms = TestPdf.Dict(("Predictor", PdfInteger.Of(2)), ("Colors", PdfInteger.Of(colors)), ("BitsPerComponent", PdfInteger.Of(bits)),
            ("Columns", PdfInteger.Of(columns)));
        var damaged = false;
        Assert.Equal(expected, Core.Pdf.Filters.Predictor.Apply(encoded, parms, ref damaged));
        Assert.False(damaged);
    }

    [Fact]
    public void OneBitImage_IsGray_WithDecodeArray()
    {
        // 1 bit, /Decode [1 0]: 1 = black
        var image = Decode(Image(("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(1)),
            ("Decode", new PdfArray([PdfInteger.Of(1), PdfInteger.Of(0)]))), [0b1010_0000, 0b0101_0000]);
        Assert.Equal(ImagePixels.Gray, image.Format);
        Assert.Equal([0, 255, 0, 255, 255, 0, 255, 0], image.Pixels);
    }

    [Fact]
    public void Stencil_IsStoredAsCoverage()
    {
        var image = Decode(Image(("ImageMask", PdfBoolean.True)), [0b0011_0000, 0b1100_0000], "q 1 0 0 rg 4 0 0 2 0 0 cm /Im Do Q");
        Assert.Equal(ImagePixels.Alpha, image.Format);
        Assert.Equal([255, 255, 0, 0, 0, 0, 255, 255], image.Pixels);
        Assert.Equal(0xFFFF0000u, image.Color);
    }

    [Fact]
    public void ColorImageAndColorKey_StayRgba()
    {
        var rgb = Decode(Image(("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8))), new byte[24]);
        Assert.Equal(ImagePixels.Rgba, rgb.Format);
        // Grey with a colour key needs opacity per pixel
        var keyed = Decode(Image(("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)),
            ("Mask", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(10)]))), [0, 64, 128, 255, 10, 20, 30, 40]);
        Assert.Equal(ImagePixels.Rgba, keyed.Format);
        Assert.Equal(0, keyed.Pixels[3]);
        Assert.Equal(255, keyed.Pixels[7]);
    }
}
