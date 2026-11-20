using System.Diagnostics;
using PdfKnead.Core.Imaging;

namespace PdfKnead.Tests.Imaging;

/// <summary>Tests for Lanczos-3 resampling and sharpening of inserted images. See KONZEPT.md, section 5.13.</summary>
public sealed class LanczosTests
{
    private static RgbaImage Fill(int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var image = RgbaImage.Blank(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var i = (y * w + x) * 4;
                (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]) = (r, g, b, a);
            }
        return image;
    }

    [Theory]
    [InlineData(300, 200, 97, 61)]    // strongly reduced (with a pre-reduction step)
    [InlineData(300, 200, 250, 170)]  // slightly reduced
    [InlineData(40, 30, 130, 95)]     // enlarged
    public void UniformColor_StaysExact(int w, int h, int tw, int th)
    {
        var image = Fill(w, h, (_, _) => (200, 120, 40, 255));
        var result = Lanczos.Resize(image, tw, th);
        Assert.Equal((tw, th), (result.Width, result.Height));
        for (var i = 0; i < result.Pixels.Length; i += 4)
            Assert.Equal((200, 120, 40, 255), (result.Pixels[i], result.Pixels[i + 1], result.Pixels[i + 2], result.Pixels[i + 3]));
    }

    /// <summary>
    /// Strongly reduced with a width that is no multiple of the pre-reduction factor: the pixels at the right and bottom edge count — a red stripe
    /// there shows in the last column and row (before, the pre-pass dropped them; docs/FEHLERGEDAECHTNIS.md, No. 270).
    /// </summary>
    [Fact]
    public void StrongReduction_RemainderAtTheEdge_IsKept()
    {
        // 1009 × 1009 to 100 × 100: factor 5, four pixels left over; the last four columns and rows red, the rest white
        var image = Fill(1009, 1009, (x, y) => x >= 1005 || y >= 1005 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));
        var result = Lanczos.Resize(image, 100, 100);
        int Green(int x, int y) => result.Pixels[(y * 100 + x) * 4 + 1];
        Assert.True(Green(99, 50) < 230, $"rechts rot zu sehen (Grün {Green(99, 50)})");
        Assert.True(Green(50, 99) < 230, $"unten rot zu sehen (Grün {Green(50, 99)})");
        Assert.True(Green(50, 50) > 250, "die Mitte bleibt weiß");
    }

    [Fact]
    public void FineChecker_BecomesLinearGray_NotDarkGray()
    {
        // A 1-pixel black/white checkerboard at half size: 50 % in linear light ≈ sRGB 188 (not 128, which would be too dark)
        var image = Fill(200, 200, (x, y) => (x + y) % 2 == 0 ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)255));
        var result = Lanczos.Resize(image, 100, 100);
        var center = (50 * 100 + 50) * 4;
        Assert.InRange(result.Pixels[center], 183, 193);
    }

    [Fact]
    public void TransparentNeighbour_DoesNotBleedItsColor()
    {
        // Transparent (invisible) red on the left, opaque blue on the right: no red may bleed into the blue at the edge
        var image = Fill(100, 10, (x, _) => x < 50 ? ((byte)255, (byte)0, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255, (byte)255));
        var result = Lanczos.Resize(image, 33, 4);
        for (var i = 0; i < result.Pixels.Length; i += 4)
            if (result.Pixels[i + 3] > 20) Assert.True(result.Pixels[i] < 30, $"Rot {result.Pixels[i]} bei Deckkraft {result.Pixels[i + 3]}");
    }

    [Fact]
    public void Sharpen_RaisesEdgeContrast_LeavesFlatAreas()
    {
        var image = Fill(60, 20, (x, _) => x < 30 ? ((byte)90, (byte)90, (byte)90, (byte)255) : ((byte)170, (byte)170, (byte)170, (byte)255));
        Assert.Same(image, Sharpen.UnsharpMask(image, 0, 1));
        var sharp = Sharpen.UnsharpMask(image, 1, 1);
        int At(RgbaImage i, int x) => i.Pixels[(10 * 60 + x) * 4];
        Assert.True(At(sharp, 29) < 90 && At(sharp, 30) > 170, $"{At(sharp, 29)} / {At(sharp, 30)}");
        Assert.Equal(90, At(sharp, 5));    // flat area: unchanged
        Assert.Equal(170, At(sharp, 55));
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void LargePhoto_IsReducedQuickly()
    {
        var random = new Random(7);
        var photo = Fill(4000, 3000, (x, y) => ((byte)(x * 255 / 4000), (byte)(y * 255 / 3000), (byte)random.Next(256), 255));
        var watch = Stopwatch.StartNew();
        var small = Lanczos.Resize(photo, 1000, 750);
        var reduce = watch.Elapsed;
        watch.Restart();
        var print = Lanczos.Resize(photo, 2480, 1860);   // A4 landscape at 300 dpi
        var mid = watch.Elapsed;
        Assert.Equal((1000, 750), (small.Width, small.Height));
        Assert.Equal((2480, 1860), (print.Width, print.Height));
        // Generous limits (debug build, busy machines); measured in a release build: about 0.1 s and 0.5 s
        Assert.True(reduce.TotalSeconds < 4 * TestKind.Factor && mid.TotalSeconds < 8 * TestKind.Factor, $"{reduce.TotalMilliseconds:0} ms / {mid.TotalMilliseconds:0} ms");
    }
}
