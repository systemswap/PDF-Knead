namespace PdfKnead.Core.Imaging;

/// <summary>Prepares images for signatures and stamps.</summary>
public static class ImageTools
{
    /// <summary>
    /// Makes a light background (paper) transparent, as for a scanned or photographed
    /// signature. Paper and ink lightness are estimated from the image (most of it light =
    /// paper, darkest areas = ink); in between, the opacity is distributed linearly and the colour
    /// is "unmixed" from the paper colour, otherwise the ink would get a light fringe.
    /// </summary>
    /// <param name="image">The source image; it is not changed.</param>
    /// <returns>A new image of the same size.</returns>
    public static RgbaImage RemoveWhiteBackground(RgbaImage image)
    {
        var src = image.Pixels;
        var count = src.Length / 4;
        var histogram = new int[256];
        for (var i = 0; i < src.Length; i += 4) histogram[Lightness(src, i)]++;
        // Paper: 60 % of the pixels are lighter than this (a signature covers little area); ink: the darkest percent
        int Percentile(double p)
        {
            var target = count * p;
            var sum = 0;
            for (var v = 0; v < 256; v++)
                if ((sum += histogram[v]) >= target) return v;
            return 255;
        }
        var paperLight = Math.Max(Percentile(0.4), 160);
        var inkLight = Math.Min(Percentile(0.01), paperLight - 40);
        // Colour of the paper (slightly tinted scans): mean of the pixels from this lightness upwards
        double pr = 0, pg = 0, pb = 0;
        var paperPixels = 0;
        for (var i = 0; i < src.Length; i += 4)
        {
            if (Lightness(src, i) < paperLight) continue;
            pr += src[i];
            pg += src[i + 1];
            pb += src[i + 2];
            paperPixels++;
        }
        (pr, pg, pb) = paperPixels > 0 ? (pr / paperPixels, pg / paperPixels, pb / paperPixels) : (255, 255, 255);

        var result = RgbaImage.Blank(image.Width, image.Height);
        var dst = result.Pixels;
        for (var i = 0; i < src.Length; i += 4)
        {
            var alpha = Math.Clamp((paperLight - Lightness(src, i)) / (double)(paperLight - inkLight), 0, 1);
            if (alpha < 0.08) continue;   // paper texture, noise
            alpha *= src[i + 3] / 255.0;
            if (alpha <= 0) continue;
            dst[i] = Unmix(src[i], pr, alpha);
            dst[i + 1] = Unmix(src[i + 1], pg, alpha);
            dst[i + 2] = Unmix(src[i + 2], pb, alpha);
            dst[i + 3] = (byte)Math.Round(alpha * 255);
        }
        return result;
    }

    /// <summary>Lightness according to ITU-R BT.709 (0–255).</summary>
    private static int Lightness(byte[] p, int i) => (int)Math.Round(0.2126 * p[i] + 0.7152 * p[i + 1] + 0.0722 * p[i + 2]);

    /// <summary>Solves c = a·ink + (1 − a)·paper for the ink.</summary>
    private static byte Unmix(byte value, double paper, double alpha) => (byte)Math.Clamp(Math.Round((value - (1 - alpha) * paper) / alpha), 0, 255);

    /// <summary>Cuts off the transparent border, leaving <paramref name="margin"/> pixels of space (pixels with alpha up to 8 count as transparent).</summary>
    /// <param name="image">The source image.</param>
    /// <param name="margin">Space to keep around the visible content, in px.</param>
    /// <returns>A new image, the source itself if nothing is cut off, or null if everything is transparent.</returns>
    public static RgbaImage? Trim(RgbaImage image, int margin = 2)
    {
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                if (image.Pixels[(y * image.Width + x) * 4 + 3] > 8)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
        if (right < 0) return null;
        left = Math.Max(0, left - margin);
        top = Math.Max(0, top - margin);
        right = Math.Min(image.Width - 1, right + margin);
        bottom = Math.Min(image.Height - 1, bottom + margin);
        var w = right - left + 1;
        var h = bottom - top + 1;
        if (w == image.Width && h == image.Height) return image;
        var result = RgbaImage.Blank(w, h);
        for (var y = 0; y < h; y++)
            Array.Copy(image.Pixels, ((top + y) * image.Width + left) * 4, result.Pixels, y * w * 4, w * 4);
        return result;
    }

    /// <summary>Scales the image down until the longer side has at most <paramref name="maxSide"/> pixels (area average, weighted by alpha).</summary>
    /// <param name="image">The source image.</param>
    /// <param name="maxSide">Maximum length of the longer side in px.</param>
    /// <returns>A new image, or the source itself if it is small enough.</returns>
    public static RgbaImage Downscale(RgbaImage image, int maxSide)
    {
        var longest = Math.Max(image.Width, image.Height);
        if (longest <= maxSide) return image;
        var f = (double)longest / maxSide;
        var w = Math.Max(1, (int)Math.Round(image.Width / f));
        var h = Math.Max(1, (int)Math.Round(image.Height / f));
        var result = RgbaImage.Blank(w, h);
        for (var y = 0; y < h; y++)
        {
            int y0 = (int)(y * f), y1 = Math.Max(y0 + 1, Math.Min(image.Height, (int)((y + 1) * f)));
            for (var x = 0; x < w; x++)
            {
                int x0 = (int)(x * f), x1 = Math.Max(x0 + 1, Math.Min(image.Width, (int)((x + 1) * f)));
                double r = 0, g = 0, b = 0, a = 0;
                var n = 0;
                for (var sy = y0; sy < y1; sy++)
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var s = (sy * image.Width + sx) * 4;
                        var pa = image.Pixels[s + 3];
                        r += image.Pixels[s] * pa;
                        g += image.Pixels[s + 1] * pa;
                        b += image.Pixels[s + 2] * pa;
                        a += pa;
                        n++;
                    }
                var d = (y * w + x) * 4;
                if (a > 0)
                {
                    result.Pixels[d] = (byte)Math.Round(r / a);
                    result.Pixels[d + 1] = (byte)Math.Round(g / a);
                    result.Pixels[d + 2] = (byte)Math.Round(b / a);
                }
                result.Pixels[d + 3] = (byte)Math.Round(a / n);
            }
        }
        return result;
    }

    /// <summary>Checks whether the image has any pixel that is not fully opaque.</summary>
    /// <param name="image">The image.</param>
    /// <returns>True if any alpha value is below 255.</returns>
    public static bool HasTransparency(RgbaImage image)
    {
        for (var i = 3; i < image.Pixels.Length; i += 4)
            if (image.Pixels[i] < 255) return true;
        return false;
    }
}
