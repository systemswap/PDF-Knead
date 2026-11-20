using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using SkiaSharp;

namespace PdfKnead.Rendering;

/// <summary>
/// A picture made of several images of the page content (tiles, see KONZEPT.md, section 5.20) put together into one image for copying and
/// saving: each tile at its place in its own resolution, cut to its visible part, as the page shows them — without text or drawings on top.
/// </summary>
public static class ContentImageExport
{
    /// <summary>
    /// Puts the images together at the highest resolution among them; with more than <paramref name="maxPixels"/> the whole is reduced
    /// evenly. Transparent where no tile lies.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="images">The images (their place on the page, not moved: their layout is exact there).</param>
    /// <param name="maxPixels">Largest number of pixels of the result.</param>
    /// <returns>Width, height and RGBA without premultiplied alpha; null if none of the images can be read.</returns>
    public static (int Width, int Height, byte[] Rgba)? Compose(PdfDocument doc, IReadOnlyList<ContentObject> images, long maxPixels)
    {
        var box = images.Select(i => i.Visible).Where(v => !v.IsEmpty).DefaultIfEmpty(RectD.Empty).Aggregate((a, b) => a.Union(b));
        if (box.IsEmpty || box.Width > 1e6 || box.Height > 1e6) return null;
        var dpi = images.Select(i => Math.Max(i.DpiX, i.DpiY)).Where(double.IsFinite).DefaultIfEmpty(72).Max();
        var scale = Math.Max(dpi, 1) / 72;
        if (box.Width * scale * box.Height * scale > maxPixels) scale = Math.Sqrt(maxPixels / (box.Width * box.Height));
        var width = Math.Max(1, (int)Math.Round(box.Width * scale));
        var height = Math.Max(1, (int)Math.Round(box.Height * scale));
        // page space (y upwards) to the result (y downwards, the top of the box at row 0)
        var pageToResult = Matrix.Translate(-box.Left, -box.Bottom) * Matrix.Scale(scale, -scale);

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var drawn = 0;
            foreach (var image in images)
            {
                if (image.Source is not { } source || PdfImageDecoder.Decode(source, doc) is not { } decoded) continue;
                var rgba = decoded.Rgba;
                using var tile = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                System.Runtime.InteropServices.Marshal.Copy(rgba, 0, tile.GetPixels(), Math.Min(rgba.Length, decoded.Width * decoded.Height * 4));
                // pixel (0, 0) is the top left of the image: its unit square's (0, 1)
                var pixelToResult = Matrix.Scale(1.0 / decoded.Width, -1.0 / decoded.Height) * Matrix.Translate(0, 1) * image.ImageToPage * pageToResult;
                var clip = pageToResult.TransformBounds(image.Visible);
                canvas.Save();
                canvas.ClipRect(SKRect.Create((float)clip.Left, (float)clip.Top, (float)clip.Width, (float)clip.Height));
                canvas.SetMatrix(new SKMatrix((float)pixelToResult.A, (float)pixelToResult.C, (float)pixelToResult.E,
                    (float)pixelToResult.B, (float)pixelToResult.D, (float)pixelToResult.F, 0, 0, 1));
                using var paint = new SKPaint { IsAntialias = false };
                using var skImage = SKImage.FromBitmap(tile);
                canvas.DrawImage(skImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
                canvas.Restore();
                drawn++;
            }
            if (drawn == 0) return null;
        }
        return Rgba(bitmap);
    }

    /// <summary>
    /// A page drawn as a picture, at most <paramref name="maxSide"/> pixels on its longer side (and at most four pixels per point): copied
    /// objects (B7) as an image for other programs.
    /// </summary>
    /// <param name="doc">The document whose objects the page draws.</param>
    /// <param name="page">The page (<see cref="ContentObjectEditor.PageOf"/>).</param>
    /// <param name="maxSide">Largest width or height in pixels.</param>
    /// <returns>Width, height and RGBA; null if the page has no area.</returns>
    public static (int Width, int Height, byte[] Rgba)? Picture(PdfDocument doc, PdfPage page, int maxSide)
    {
        var longer = Math.Max(page.MediaBox.Width, page.MediaBox.Height);
        if (!(longer > 0) || !double.IsFinite(longer)) return null;
        using var bitmap = SkiaRenderer.RenderPage(Core.Pdf.Content.PageInterpreter.Build(doc, page), doc, page, Math.Min(4, maxSide / longer), new ImageCache());
        return Rgba(bitmap);
    }

    private static (int Width, int Height, byte[] Rgba)? Rgba(SKBitmap bitmap)
    {
        var (width, height) = (bitmap.Width, bitmap.Height);
        var result = new byte[width * height * 4];
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        unsafe
        {
            fixed (byte* p = result)
                if (!bitmap.PeekPixels().ReadPixels(info, (nint)p, width * 4, 0, 0)) return null;
        }
        return (width, height, result);
    }
}
