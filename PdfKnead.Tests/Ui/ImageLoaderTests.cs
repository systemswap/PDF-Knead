using PdfKnead.Core.Imaging;
using PdfKnead.Services;
using SkiaSharp;

namespace PdfKnead.Tests.Ui;

/// <summary>Tests for loading images for signatures: decoding at reduced size and all EXIF orientations.</summary>
[Collection(nameof(UiCollection))]
public sealed class ImageLoaderTests
{
    /// <summary>Builds a 3 × 2 image in which every pixel has its own colour (red = column, green = row).</summary>
    private static RgbaImage Sample()
    {
        var image = RgbaImage.Blank(3, 2);
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 3; x++)
                ((byte[])[(byte)(x * 100), (byte)(y * 100), 0, 255]).CopyTo(image.Pixels, (y * 3 + x) * 4);
        return image;
    }

    private static (int X, int Y) At(RgbaImage image, int x, int y) =>
        (image.Pixels[(y * image.Width + x) * 4] / 100, image.Pixels[(y * image.Width + x) * 4 + 1] / 100);

    [Fact]
    public void Orient_AllEightExifOrientations()
    {
        var s = Sample();
        // Which source pixel ends up at the top left of the result? (EXIF 1–8)
        Assert.Equal((0, 0), At(ImageLoader.Orient(s, SKEncodedOrigin.TopLeft), 0, 0));
        Assert.Equal((2, 0), At(ImageLoader.Orient(s, SKEncodedOrigin.TopRight), 0, 0));      // mirrored
        Assert.Equal((2, 1), At(ImageLoader.Orient(s, SKEncodedOrigin.BottomRight), 0, 0));   // 180°
        Assert.Equal((0, 1), At(ImageLoader.Orient(s, SKEncodedOrigin.BottomLeft), 0, 0));    // flipped vertically
        Assert.Equal((0, 0), At(ImageLoader.Orient(s, SKEncodedOrigin.LeftTop), 0, 0));       // transposed
        Assert.Equal((0, 1), At(ImageLoader.Orient(s, SKEncodedOrigin.RightTop), 0, 0));      // 90° clockwise
        Assert.Equal((2, 1), At(ImageLoader.Orient(s, SKEncodedOrigin.RightBottom), 0, 0));   // transverse
        Assert.Equal((2, 0), At(ImageLoader.Orient(s, SKEncodedOrigin.LeftBottom), 0, 0));    // 270°
        foreach (var origin in (SKEncodedOrigin[])[SKEncodedOrigin.LeftTop, SKEncodedOrigin.RightTop, SKEncodedOrigin.RightBottom, SKEncodedOrigin.LeftBottom])
            Assert.Equal((2, 3), (ImageLoader.Orient(s, origin).Width, ImageLoader.Orient(s, origin).Height));
        // 90° and 270° cancel each other out, and so does mirroring twice
        Assert.Equal(s.Pixels, ImageLoader.Orient(ImageLoader.Orient(s, SKEncodedOrigin.RightTop), SKEncodedOrigin.LeftBottom).Pixels);
        Assert.Equal(s.Pixels, ImageLoader.Orient(ImageLoader.Orient(s, SKEncodedOrigin.RightBottom), SKEncodedOrigin.RightBottom).Pixels);
        Assert.Equal(s.Pixels, ImageLoader.Orient(ImageLoader.Orient(s, SKEncodedOrigin.LeftTop), SKEncodedOrigin.LeftTop).Pixels);
    }

    /// <summary>A large JPEG decoded at reduced size gives the same result as a full decode followed by downscaling.</summary>
    [Fact]
    public void LargeJpeg_DecodedScaled_LooksLikeFullDecode()
    {
        var file = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"foto-{Guid.NewGuid():N}.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var bitmap = new SKBitmap(4000, 3000))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                using var paint = new SKPaint { Color = new SKColor(20, 40, 140), StrokeWidth = 24, IsAntialias = true, Style = SKPaintStyle.Stroke };
                for (var i = 0; i < 12; i++) canvas.DrawLine(300 + i * 280, 900, 520 + i * 260, 2100 + i % 3 * 120, paint);
            }
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 92);
            File.WriteAllBytes(file, data.ToArray());
        }
        // Detailed messages: in the full test run this test failed twice without a message (suspected cause: a virus scanner locking the new file)
        var loaded = ImageLoader.Load(file);
        Assert.True(loaded is not null, $"ImageLoader.Load lieferte nichts (Datei da: {File.Exists(file)}, {new FileInfo(file).Length} Bytes)");
        using var decoded = SKBitmap.Decode(file);
        Assert.True(decoded is not null, "SKBitmap.Decode lieferte nichts");
        using var full = decoded.Copy(SKColorType.Rgba8888);
        var reference = ImageTools.Downscale(new RgbaImage(full.Width, full.Height, full.Bytes), ImageLoader.MaxSide);
        Assert.Equal((reference.Width, reference.Height), (loaded.Width, loaded.Height));
        long sum = 0;
        for (var i = 0; i < loaded.Pixels.Length; i++) sum += Math.Abs(loaded.Pixels[i] - reference.Pixels[i]);
        var mean = (double)sum / loaded.Pixels.Length;
        Assert.True(mean < 1.5, $"mittlere Abweichung {mean:0.00}");
        File.Delete(file);
    }
}
