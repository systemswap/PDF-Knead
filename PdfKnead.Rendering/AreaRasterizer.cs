using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Writing;
using SkiaSharp;
using Matrix = PdfKnead.Core.Pdf.Graphics.Matrix;

namespace PdfKnead.Rendering;

/// <summary>
/// Draws an area of a page as the display shows it (<see cref="IAreaRasterizer"/>), for the transparency of PDF/A-1 copies
/// (<see cref="PdfAFlattening"/>): in the page's default space, unrotated, on white paper.
/// </summary>
public sealed class AreaRasterizer : IAreaRasterizer
{
    /// <summary>The shared instance (it has no state).</summary>
    public static readonly AreaRasterizer Instance = new();

    /// <summary>Largest area drawn, in pixels (an A0 page at 300 dpi has about 139 million).</summary>
    public const long MaxPixels = 160_000_000;

    /// <inheritdoc/>
    public RasterArea? Render(DisplayList list, PdfDocument doc, PdfPage page, RectD area, double pixelsPerPoint, CancellationToken cancel)
    {
        var width = (int)Math.Ceiling(area.Width * pixelsPerPoint - 1e-6);
        var height = (int)Math.Ceiling(area.Height * pixelsPerPoint - 1e-6);
        // RGBA within the budget of the machine as well (No. 743): on a small one a large area is drawn coarser by the caller
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels || width > RasterExport.MaxSide || height > RasterExport.MaxSide
            || (long)width * height * 4 > Core.Pdf.PdfLimits.Budget(MaxPixels * 4)) return null;
        // page space (y upwards) → pixels (y downwards): the top left corner of the area at (0, 0)
        var pageToDevice = new Matrix(pixelsPerPoint, 0, 0, -pixelsPerPoint, -area.Left * pixelsPerPoint, area.Bottom * pixelsPerPoint);
        var images = new ImageCache(256L << 20);
        try
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SkiaRenderer.Paper);
                SkiaRenderer.RenderTile(canvas, list, doc, pageToDevice, new SKRectI(0, 0, width, height), images, cancel);
            }
            // opaque on the paper: the colour channels as they are
            var rgba = bitmap.GetPixelSpan();
            var rgb = new byte[(long)width * height * 3];
            for (long i = 0, o = 0; o < rgb.Length; i += 4, o += 3)
            {
                rgb[o] = rgba[(int)i];
                rgb[o + 1] = rgba[(int)i + 1];
                rgb[o + 2] = rgba[(int)i + 2];
            }
            return new RasterArea(width, height, rgb);
        }
        finally
        {
            images.Detach();
        }
    }
}
