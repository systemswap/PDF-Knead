using System.IO.Compression;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Flate with PNG predictor, exact palette (lossless) and palette with colour reduction. See KONZEPT.md, section 5.12.</summary>
public sealed class PaletteTests
{
    private static byte Photo(int x, int y, int c) => (byte)((x * 7 + y * 13 + c * 50 + x * y) & 255);

    /// <summary>Screenshot-like: flat areas, a gradient and soft (anti-aliased) edges; more than 256 colours, but few base tones.</summary>
    private static byte Screenshot(int x, int y, int c)
    {
        // Title bar with a gradient in two directions (red to the right, green downwards): a few thousand colours
        if (y < 60) return (byte)(c switch { 0 => 40 + x * 100 / 600, 1 => 60 + y * 2, _ => 200 });
        var box = x is > 100 and < 400 && y is > 120 and < 300;
        var edge = Math.Clamp(Math.Abs(x - 100) * 40, 0, 255);   // soft edge on the left of the box
        var v = box ? 30 + c * 70 : 245 - c * 10;
        return (byte)Math.Clamp(box && x < 107 ? (v * edge + 245 * (255 - edge)) / 255 : v, 0, 255);
    }

    [Theory]
    [InlineData(3, 8)]
    [InlineData(1, 8)]
    [InlineData(1, 4)]
    public void Predictor_RoundTripsThroughTheDecoder(int colors, int bits)
    {
        const int width = 123, height = 77;
        var stride = (width * colors * bits + 7) / 8;
        var samples = new byte[stride * height];
        for (var i = 0; i < samples.Length; i++) samples[i] = Photo(i % stride, i / stride, 0);
        var rows = PngPredictor.Encode(samples, width, height, colors, bits);
        Assert.Equal((stride + 1) * height, rows.Length);
        Assert.Contains(rows.Where((_, i) => i % (stride + 1) == 0), f => f != 0);   // not only "None"
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, true)) z.Write(rows);
        var parms = TestPdf.Dict(("Predictor", PdfInteger.Of(15)), ("Colors", PdfInteger.Of(colors)), ("BitsPerComponent", PdfInteger.Of(bits)),
            ("Columns", PdfInteger.Of(width)));
        var decoded = StreamFilters.Decode(packed.ToArray(), TestPdf.Dict(("Filter", TestPdf.N("FlateDecode")), ("DecodeParms", parms)));
        Assert.Equal(samples, decoded.Data);
    }

    [Fact]
    public void ExactPalette_IsLossless_OnlyUpTo256Colors()
    {
        var few = new byte[300 * 3];
        for (var i = 0; i < 300; i++) (few[i * 3], few[i * 3 + 1], few[i * 3 + 2]) = ((byte)(i % 5 * 50), (byte)(i % 3 * 80), 7);
        var exact = PaletteQuantizer.Exact(few, 300, 3)!;
        Assert.Equal(15, exact.Count);
        Assert.Equal(4, exact.Bits);
        for (var i = 0; i < 300; i++)
            Assert.Equal(few.AsSpan(i * 3, 3).ToArray(), exact.Palette.AsSpan(exact.Indices[i] * 3, 3).ToArray());
        var many = new byte[1000 * 3];
        for (var i = 0; i < 1000; i++) (many[i * 3], many[i * 3 + 1]) = ((byte)i, (byte)(i >> 8));
        Assert.Null(PaletteQuantizer.Exact(many, 1000, 3));
    }

    [Fact]
    public void Quantize_Screenshot_KeepsQuality()
    {
        const int width = 600, height = 400;
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var c = 0; c < 3; c++) rgb[(y * width + x) * 3 + c] = Screenshot(x, y, c);
        Assert.Null(PaletteQuantizer.Exact(rgb, width * height, 3));
        var palette = PaletteQuantizer.Quantize(rgb, width, height, 256, 0)!;
        Assert.True(palette.Count <= 256);
        Assert.True(palette.Psnr > 40, $"PSNR {palette.Psnr:0.0}");
        var few = PaletteQuantizer.Quantize(rgb, width, height, 8, 1)!;
        Assert.True(few.Count <= 8);
        Assert.True(few.Psnr < palette.Psnr);
    }


    private static (PdfDocument Doc, PdfDictionary Image, long Bytes) Compress(byte[] file, CompressionSettings settings)
    {
        using var original = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(original, PageItem.From(original), output, new PdfSaveOptions { Compression = settings, ImageCodec = Rendering.SkiaImageCodec.Instance });
        var doc = PdfDocument.Open(output.ToArray());
        var image = (PdfStream)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im0"))!;
        return (doc, image.Dictionary, image.DataLength);
    }

    /// <summary>"Lossless": a graphic with few colours becomes an exact palette, pixel-identical and smaller.</summary>
    [Fact]
    public void Lossless_FewColors_BecomesExactPalette()
    {
        var file = CompressionWriterTests.WithImage(800, 600, 3, (x, y, c) => (byte)(((x / 50 + y / 40) % 6) * 40 + c * 15), inches: 8);
        using var original = PdfDocument.Open(file);
        var before = ((PdfStream)original.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im0"))!).DataLength;
        var (doc, image, bytes) = Compress(file, CompressionSettings.For(CompressionPreset.Lossless));
        using (doc)
        {
            var space = Assert.IsType<PdfArray>(image.Get(PdfName.Get("ColorSpace")));
            Assert.Equal("Indexed", ((PdfName)space.Get(0)!).Value);
            Assert.Equal(4, image.GetInteger(PdfName.Get("BitsPerComponent")));
            Assert.True(bytes < before, $"{bytes} von {before}");
            Assert.Equal(0, CompressionWriterTests.Difference(original, doc));
        }
    }

    /// <summary>"Lossless": a photo without JPEG stays RGB, with or without predictor, pixel-identical.</summary>
    [Fact]
    public void Lossless_Photo_StaysExact()
    {
        var file = CompressionWriterTests.WithImage(400, 300, 3, (x, y, c) => (byte)(c switch { 0 => x / 2, 1 => y * 3 / 4, _ => (x + y) / 4 }), inches: 6);
        using var original = PdfDocument.Open(file);
        var (doc, image, _) = Compress(file, CompressionSettings.For(CompressionPreset.Lossless));
        using (doc)
        {
            Assert.Equal("DeviceRGB", image.GetName(PdfName.Get("ColorSpace"))?.Value);
            Assert.Equal(15, image.GetDictionary(PdfName.Get("DecodeParms"))?.GetInteger(PdfName.Get("Predictor")));
            Assert.Equal(0, CompressionWriterTests.Difference(original, doc));
        }
    }

    /// <summary>E-book with "Palette": the screenshot becomes a palette instead of JPEG (sharp, hardly different); with "Flate" it stays JPEG.</summary>
    [Fact]
    public void Screenshot_PaletteInsteadOfJpeg()
    {
        var file = CompressionWriterTests.WithImage(600, 400, 3, Screenshot, inches: 6);
        using var original = PdfDocument.Open(file);
        var ebook = CompressionSettings.For(CompressionPreset.EBook);
        Assert.Equal(GraphicsMethod.Palette, ebook.Graphics);
        var (doc, image, _) = Compress(file, ebook);
        using (doc)
        {
            Assert.IsType<PdfArray>(image.Get(PdfName.Get("ColorSpace")));
            Assert.Equal("FlateDecode", image.GetName(PdfName.Filter)?.Value);
            Assert.True(CompressionWriterTests.Difference(original, doc) < 1.0);
        }
        var (jpeg, jpegImage, _) = Compress(file, ebook with { Graphics = GraphicsMethod.Flate });
        using (jpeg) Assert.Equal("DCTDecode", jpegImage.GetName(PdfName.Filter)?.Value);
        // Minimum quality 100 missed: then JPEG as without the palette
        var (strict, strictImage, _) = Compress(file, ebook with { PaletteQuality = 100, PaletteColors = 4 });
        using (strict) Assert.Equal("DCTDecode", strictImage.GetName(PdfName.Filter)?.Value);
    }
}

/// <summary>Timing of the palette: without tests running in parallel (otherwise unreliable under full load).</summary>
[Collection(nameof(LimitCollection))]
public sealed class PaletteTimingTests
{
    /// <summary>
    /// Noisy photo without JPEG (A4 at 150 dpi, 1.8 million colours): misses the minimum quality, which the estimate from the
    /// histogram detects before every pixel is assigned (assigning every pixel first took 4.9 s only to discard the result).
    /// The screenshot case stays the same.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void Quantize_NoisyPhoto_GivesUpEarly()
    {
        const int width = 1240, height = 1754;
        var rgb = new byte[width * height * 3];
        var random = new Random(3);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var c = 0; c < 3; c++) rgb[(y * width + x) * 3 + c] = (byte)Math.Clamp(128 + 90 * Math.Sin(x / (40.0 + c * 9)) * Math.Cos(y / 53.0) + random.Next(-20, 21), 0, 255);
        Assert.Null(PaletteQuantizer.Quantize(rgb, width, height, 256, 0, minimumPsnr: CompressionSettings.MinimumPsnr(80)));   // warm up once (JIT)
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(PaletteQuantizer.Quantize(rgb, width, height, 256, 0, minimumPsnr: CompressionSettings.MinimumPsnr(80)));
#if !DEBUG
        Assert.True(watch.Elapsed.TotalSeconds < 1 * TestKind.Factor, $"{watch.Elapsed.TotalSeconds:0.00} s");   // time checked only in the Release build (Debug is several times slower)
#endif
        // Without a minimum quality: a palette as usual (worse)
        Assert.True(PaletteQuantizer.Quantize(rgb, width, height, 256, 0)!.Psnr < CompressionSettings.MinimumPsnr(80));
    }
}
