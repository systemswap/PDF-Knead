namespace PdfKnead.Core.Imaging;

/// <summary>
/// Sharpens inserted images: unsharp masking, i.e. the image minus its Gaussian
/// blur is added back amplified; small differences (noise, smooth areas) below the threshold stay untouched.
/// Opacity is not sharpened.
/// </summary>
public static class Sharpen
{
    /// <summary>Applies an unsharp mask.</summary>
    /// <param name="image">The source image.</param>
    /// <param name="amount">Strength: 0 = off, 1 = amplify edges by 100 %.</param>
    /// <param name="sigma">Radius of the blur in pixels (Gaussian sigma).</param>
    /// <param name="threshold">Minimum difference in tonal levels (0–255).</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>A new image, or the source itself if <paramref name="amount"/> or <paramref name="sigma"/> is practically zero.</returns>
    public static RgbaImage UnsharpMask(RgbaImage image, double amount, double sigma, double threshold = 2, CancellationToken cancel = default)
    {
        if (amount <= 0.001 || sigma <= 0.05) return image;
        int width = image.Width, height = image.Height;
        var src = image.Pixels;
        var blur = new float[(long)width * height * 3];
        for (var p = 0; p < width * height; p++)
        {
            blur[p * 3] = src[p * 4];
            blur[p * 3 + 1] = src[p * 4 + 1];
            blur[p * 3 + 2] = src[p * 4 + 2];
        }
        Gauss(blur, width, height, sigma, cancel);
        var result = RgbaImage.Blank(width, height);
        var dst = result.Pixels;
        float amt = (float)amount, thr = (float)threshold;
        var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };
        Parallel.For(0, height, options, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var p = y * width + x;
                dst[p * 4 + 3] = src[p * 4 + 3];
                for (var c = 0; c < 3; c++)
                {
                    float original = src[p * 4 + c];
                    var diff = original - blur[p * 3 + c];
                    var v = MathF.Abs(diff) < thr ? original : original + amt * diff;
                    dst[p * 4 + c] = (byte)(v <= 0 ? 0 : v >= 255 ? 255 : v + 0.5f);
                }
            }
        });
        return result;
    }

    /// <summary>Gaussian blur (separable, horizontal and vertical) on three interleaved channels, edge pixels repeated.</summary>
    private static void Gauss(float[] data, int width, int height, double sigma, CancellationToken cancel)
    {
        var radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var kernel = new float[2 * radius + 1];
        double sum = 0;
        for (var i = -radius; i <= radius; i++) sum += kernel[i + radius] = (float)Math.Exp(-i * i / (2 * sigma * sigma));
        for (var i = 0; i < kernel.Length; i++) kernel[i] = (float)(kernel[i] / sum);
        var tmp = new float[data.Length];
        var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };
        Parallel.For(0, height, options, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var s = (row + Math.Clamp(x + k, 0, width - 1)) * 3;
                    var w = kernel[k + radius];
                    r += data[s] * w;
                    g += data[s + 1] * w;
                    b += data[s + 2] * w;
                }
                var d = (row + x) * 3;
                tmp[d] = r;
                tmp[d + 1] = g;
                tmp[d + 2] = b;
            }
        });
        Parallel.For(0, height, options, y =>
        {
            for (var x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var s = (Math.Clamp(y + k, 0, height - 1) * width + x) * 3;
                    var w = kernel[k + radius];
                    r += tmp[s] * w;
                    g += tmp[s + 1] * w;
                    b += tmp[s + 2] * w;
                }
                var d = (y * width + x) * 3;
                data[d] = r;
                data[d + 1] = g;
                data[d + 2] = b;
            }
        });
    }
}
