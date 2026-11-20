using System.IO.Compression;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Large images (scans, plans) are read row by row and scaled down.</summary>
public sealed class LargeImageTests
{
    private static DecodedImage? DecodeImage(PdfStream image)
    {
        var (doc, list) = InterpreterTests.Page("q 200 0 0 200 0 0 cm /Im Do Q", pdf =>
            TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(pdf.Add(image)))))));
        using (doc) return PdfImageDecoder.Decode(list.Images[0], doc);
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data);
        return output.ToArray();
    }

    [Fact]
    public void LargeFlateImageWithPngPredictor_IsReducedByAveraging()
    {
        // 6000 × 3000 grey (18 MP > 16 MP): black on the left, white on the right, rows with the PNG filter "Up"
        const int w = 6000, h = 3000;
        var raw = new byte[(w + 1) * h];
        for (var y = 0; y < h; y++)
        {
            raw[y * (w + 1)] = (byte)(y == 0 ? 0 : 2);                                   // first row without filter, then "Up"
            if (y == 0) for (var x = w / 2; x < w; x++) raw[1 + x] = 255;               // difference to the previous row: 0
        }
        var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(w)), ("Height", PdfInteger.Of(h)),
            ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", TestPdf.N("DeviceGray")), ("Filter", TestPdf.N("FlateDecode")),
            ("DecodeParms", TestPdf.Dict(("Predictor", PdfInteger.Of(12)), ("Columns", PdfInteger.Of(w)), ("Colors", PdfInteger.Of(1)))));
        var image = DecodeImage(new PdfStream(dict, Zlib(raw)));

        Assert.NotNull(image);
        Assert.Equal(3000, image.Width);                                                  // factor 2
        Assert.Equal(1500, image.Height);
        Assert.True((long)image.Width * image.Height <= PdfImageDecoder.MaxFullPixels);
        Assert.Equal(0, image.Rgba[(700 * image.Width + 100) * 4]);                      // black on the left
        Assert.Equal(255, image.Rgba[(700 * image.Width + 2900) * 4]);                   // white on the right
    }

    [Fact]
    public void LargeStencil_BecomesCoverageAlpha()
    {
        // 8000 × 2100 stencil mask (16.8 MP): columns alternately painted (0) and empty (1) → 50 % coverage
        const int w = 8000, h = 2100;
        var rowBytes = w / 8;
        var data = new byte[rowBytes * h];
        Array.Fill(data, (byte)0b0101_0101);
        var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(w)), ("Height", PdfInteger.Of(h)),
            ("ImageMask", PdfBoolean.True), ("Filter", TestPdf.N("FlateDecode")));
        var image = DecodeImage(new PdfStream(dict, Zlib(data)));

        Assert.NotNull(image);
        Assert.Equal(4000, image.Width);
        var alpha = image.Rgba[(500 * image.Width + 1000) * 4 + 3];
        Assert.InRange(alpha, 120, 135);
    }

    [Fact]
    public void LargeJpeg_IsDecodedScaledDown()
    {
        using var bitmap = new SKBitmap(4800, 4000);   // 19.2 MP
        bitmap.Erase(new SKColor(200, 40, 40));
        using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
        var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(4800)), ("Height", PdfInteger.Of(4000)),
            ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Filter", TestPdf.N("DCTDecode")));
        var image = DecodeImage(new PdfStream(dict, jpeg.ToArray()));

        Assert.NotNull(image);
        Assert.True((long)image.Width * image.Height <= PdfImageDecoder.MaxFullPixels, $"{image.Width} × {image.Height}");
        Assert.InRange(image.Rgba[0], 190, 210);
    }
}
