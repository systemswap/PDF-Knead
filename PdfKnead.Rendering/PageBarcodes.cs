using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using SkiaSharp;

namespace PdfKnead.Rendering;

/// <summary>A code on a page.</summary>
/// <param name="Code">What was read (corners in the pixels of the rendered area).</param>
/// <param name="Corners">Corners in page space (top left, top right, bottom right, bottom left in the code's reading orientation).</param>
/// <param name="ModulePoints">Size of one module in points.</param>
public sealed record PageBarcode(FoundBarcode Code, (double X, double Y)[] Corners, double ModulePoints)
{
    /// <summary>Size of one module in millimetres.</summary>
    public double ModuleMillimetres => ModulePoints / 72 * 25.4;

    /// <summary>Width and height in points (along the code's own axes).</summary>
    public (double Width, double Height) Size =>
        (Math.Sqrt(Square(Corners[1].X - Corners[0].X) + Square(Corners[1].Y - Corners[0].Y)),
         Math.Sqrt(Square(Corners[3].X - Corners[0].X) + Square(Corners[3].Y - Corners[0].Y)));

    /// <summary>Turn of the code on the page in degrees counter-clockwise (0 … 359, from its top edge).</summary>
    public int Rotation
    {
        get
        {
            var angle = Math.Atan2(Corners[1].Y - Corners[0].Y, Corners[1].X - Corners[0].X) * 180 / Math.PI;
            return ((int)Math.Round(angle) % 360 + 360) % 360;
        }
    }

    private static double Square(double v) => v * v;
}

/// <summary>
/// Reads codes on a page by rendering the area around a point (with the elements inserted in PDF Knead) and searching the image: a
/// code is found whether it is an image, vector graphics or set in a bar code font. Too fine codes are rendered again at about eight
/// pixels per module, codes larger than the area in a larger one.
/// </summary>
public static class PageBarcodes
{
    private const double FirstScale = 4;            // pixels per point for the first look (288 dpi)
    private const double FirstReach = 110;          // points around the point (a square of about 78 mm)
    private const double WantedModule = 8;          // pixels per module for the second look
    private const long MaxPixels = 24_000_000;

    /// <summary>The code at a point of the page.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="list">The display list of the page (without own elements).</param>
    /// <param name="elements">Elements on the page (drawn on top).</param>
    /// <param name="images">Image cache of the document.</param>
    /// <param name="x">X in page space.</param>
    /// <param name="y">Y in page space.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The code; null if none is there.</returns>
    public static PageBarcode? ReadAt(PdfDocument doc, PdfPage page, DisplayList list, IReadOnlyList<PageElement> elements, ImageCache images,
        double x, double y, CancellationToken cancel = default)
    {
        var area = new RectD(x - FirstReach, y - FirstReach, x + FirstReach, y + FirstReach);
        var scale = FirstScale;
        PageBarcode? earlier = null;   // what a coarser look read, in case the closer one does not
        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            if (Render(doc, page, list, elements, images, area, scale, cancel) is not var (image, toImage)) return earlier;
            var (ix, iy) = toImage.Transform(x, y);
            var found = BarcodeScanner.ReadAt(image, ix, iy);
            if (found is null)
            {
                if (earlier is not null) return earlier;
                // not readable yet: where is the dark area at the point? Larger than the area, or too fine: render just it, whole and sharp
                if (attempt < 2 && BarcodeScanner.Locate(image, ix, iy) is var (spot, unit, touches) && (touches || unit < 5))
                {
                    var back = toImage.Invert();
                    var c = spot.Select(p => back.Transform(p.X, p.Y)).ToArray();
                    double l = c.Min(p => p.X), r = c.Max(p => p.X), b = c.Min(p => p.Y), t = c.Max(p => p.Y);
                    var unitPoints = unit / scale;
                    // beyond the edge the area may go on: as far again as it is large, at least room for a quiet zone
                    var grow = touches ? Math.Max(r - l, t - b) : 12 * unitPoints;
                    area = new RectD(l - grow, b - grow, r + grow, t + grow);
                    scale = touches ? scale : Math.Clamp(WantedModule / Math.Max(unitPoints, 1e-3), 1, 24);
                    continue;
                }
                // a large code may not fit the first area, a very fine one needs more pixels: one more look of each kind
                if (attempt <= 2)
                {
                    area = new RectD(x - 3 * FirstReach, y - 3 * FirstReach, x + 3 * FirstReach, y + 3 * FirstReach);
                    scale = 2;
                    attempt = 2;
                    continue;
                }
                if (attempt == 3)
                {
                    area = new RectD(x - FirstReach / 3, y - FirstReach / 3, x + FirstReach / 3, y + FirstReach / 3);
                    scale = 12;
                    continue;
                }
                return null;
            }
            var fromImage = toImage.Invert();
            var corners = found.Corners.Select(c => fromImage.Transform(c.X, c.Y)).ToArray();
            var modulePoints = found.Module / scale;
            // the code reaches the edge of the area, or its modules are fine: render just it (with room for the quiet zone) once more
            if (attempt < 5 && (found.TouchesEdge || found.Module < 5))
            {
                earlier = new PageBarcode(found, corners, modulePoints);
                double left = corners.Min(c => c.X), right = corners.Max(c => c.X), bottom = corners.Min(c => c.Y), top = corners.Max(c => c.Y);
                var margin = Math.Max(12 * modulePoints, 6);
                if (found.TouchesEdge) margin = Math.Max(margin, Math.Max(right - left, top - bottom));
                area = new RectD(left - margin, bottom - margin, right + margin, top + margin);
                scale = Math.Clamp(WantedModule / Math.Max(modulePoints, 1e-3), 1, 24);
                attempt = Math.Max(attempt, 4);
                continue;
            }
            return new PageBarcode(found, corners, modulePoints);
        }
        return null;
    }

    /// <summary>
    /// All codes on a page: the page rendered at up to 4 pixels per point (within 16 megapixels) and searched area by area; codes with
    /// fine modules are read again more closely, so their corners and module size are exact.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="list">The display list of the page (without own elements).</param>
    /// <param name="elements">Elements on the page (drawn on top).</param>
    /// <param name="images">Image cache of the document.</param>
    /// <param name="cancel">Cancels the search.</param>
    /// <returns>The codes of the page.</returns>
    public static IReadOnlyList<PageBarcode> FindAll(PdfDocument doc, PdfPage page, DisplayList list, IReadOnlyList<PageElement> elements, ImageCache images,
        CancellationToken cancel = default)
    {
        var (width, height) = PageGeometry.DeviceSize(page, 1);
        var scale = Math.Min(3, Math.Sqrt(16_000_000.0 / Math.Max(1, (double)width * height)));
        var box = page.CropBox;
        if (Render(doc, page, list, elements, images, new RectD(box.Left, box.Bottom, box.Right, box.Top), scale, cancel) is not var (image, toImage)) return [];
        var fromImage = toImage.Invert();
        var result = new List<PageBarcode>();
        var (codes, suspects) = BarcodeScanner.Search(image, cancel);
        foreach (var code in codes)
        {
            var corners = code.Corners.Select(c => fromImage.Transform(c.X, c.Y)).ToArray();
            var found = new PageBarcode(code, corners, code.Module / scale);
            if (code.Module < 5)
            {
                // fine modules: read again more closely, which gives exact corners and module size — and the closer reading wins: at
                // two or three pixels per module thin spaces blur, and bars that run together can happen to form another valid code
                var (mx, my) = (corners.Average(c => c.X), corners.Average(c => c.Y));
                if (ReadAt(doc, page, list, elements, images, mx, my, cancel) is { } closer) found = closer;
                // nothing closely: a short bar code read only coarsely is too likely such a chance (one in ten for its check digit)
                else if (code.Format is BarcodeFormat.UpcE or BarcodeFormat.Ean8) continue;
            }
            if (result.Any(r => r.Code.Text == found.Code.Text && Contains(r.Corners, found.Corners.Average(c => c.X), found.Corners.Average(c => c.Y)))) continue;
            result.Add(found);
        }
        // areas that look like a code but are too coarse at this resolution: each rendered once more, just it, at about eight
        // pixels per module
        foreach (var (left, top, right, bottom) in suspects)
        {
            cancel.ThrowIfCancellationRequested();
            var (sx, sy) = fromImage.Transform((left + right) / 2.0, (top + bottom) / 2.0);
            if (result.Any(r => Contains(r.Corners, sx, sy))) continue;
            var module = BarcodeScanner.Locate(image, (left + right) / 2.0, (top + bottom) / 2.0)?.Module ?? 2;
            var modulePoints = module / scale;
            var a = fromImage.Transform(left, top);
            var b = fromImage.Transform(right + 1, bottom + 1);
            var margin = 15 * modulePoints;
            var area = new RectD(Math.Min(a.X, b.X) - margin, Math.Min(a.Y, b.Y) - margin, Math.Max(a.X, b.X) + margin, Math.Max(a.Y, b.Y) + margin);
            var closeScale = Math.Clamp(WantedModule / Math.Max(modulePoints, 1e-3), scale, 24);
            if (Render(doc, page, list, elements, images, area, closeScale, cancel) is not var (close, toClose)) continue;
            var (cx, cy) = toClose.Transform(sx, sy);
            if (BarcodeScanner.ReadAt(close, cx, cy) is not { } code) continue;
            var back = toClose.Invert();
            var corners = code.Corners.Select(c => back.Transform(c.X, c.Y)).ToArray();
            if (!result.Any(r => r.Code.Text == code.Text && Contains(r.Corners, sx, sy)))
                result.Add(new PageBarcode(code, corners, code.Module / closeScale));
        }
        return result;
    }

    private static bool Contains((double X, double Y)[] q, double x, double y)
    {
        double left = q.Min(p => p.X), right = q.Max(p => p.X), bottom = q.Min(p => p.Y), top = q.Max(p => p.Y);
        return x >= left && x <= right && y >= bottom && y <= top;
    }

    /// <summary>The area of the page as a grey image, and page space → image pixels.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="list">The display list of the page.</param>
    /// <param name="elements">Elements drawn on top.</param>
    /// <param name="images">Image cache.</param>
    /// <param name="area">The area in page space.</param>
    /// <param name="scale">Pixels per point (lowered to keep within 24 megapixels).</param>
    /// <param name="cancel">Cancels the drawing.</param>
    /// <returns>Image and transform; null if the area lies (almost) outside the page.</returns>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static (GrayImage Image, Matrix ToImage)? Render(PdfDocument doc, PdfPage page, DisplayList list, IReadOnlyList<PageElement> elements,
        ImageCache images, RectD area, double scale, CancellationToken cancel = default)
    {
        var toDevice = PageGeometry.PageToDevice(page, scale);
        var (pageWidth, pageHeight) = PageGeometry.DeviceSize(page, scale);
        var bounds = toDevice.TransformBounds(area);
        int left = Math.Max(0, (int)Math.Floor(Math.Min(bounds.Left, bounds.Right))), right = Math.Min(pageWidth, (int)Math.Ceiling(Math.Max(bounds.Left, bounds.Right)));
        int top = Math.Max(0, (int)Math.Floor(Math.Min(bounds.Top, bounds.Bottom))), bottom = Math.Min(pageHeight, (int)Math.Ceiling(Math.Max(bounds.Top, bounds.Bottom)));
        int width = right - left, height = bottom - top;
        if (width < 8 || height < 8) return null;
        if ((long)width * height > MaxPixels)
        {
            // keep the pixel budget: a coarser image of the same area
            var shrink = Math.Sqrt((double)MaxPixels / ((long)width * height));
            return Render(doc, page, list, elements, images, area, scale * shrink * 0.99, cancel);
        }
        // in stripes of at most about 4 MB of colour, one buffer reused for all of them: only the grey image stays (a quarter)
        var gray = new byte[width * height];
        // at least 16 rows per stripe, but never more than the image has (a flat page of a few points)
        var stripe = Math.Min(Math.Max((4 << 20) / (width * 4), 16), height);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(width * stripe * 4);
        try
        {
            for (var y0 = 0; y0 < height; y0 += stripe)
            {
                var rows = Math.Min(stripe, height - y0);
                var info = new SKImageInfo(width, rows, SKColorType.Rgba8888, SKAlphaType.Premul);
                unsafe
                {
                    fixed (byte* p = buffer)
                    {
                        using var bitmap = new SKBitmap();
                        bitmap.InstallPixels(info, (nint)p, info.RowBytes);
                        using (var canvas = new SKCanvas(bitmap))
                        {
                            canvas.Clear(SkiaRenderer.Paper);
                            SkiaRenderer.RenderTile(canvas, list, doc, toDevice, new SKRectI(left, top + y0, right, top + y0 + rows), images, cancel, enhanceThinLines: false);
                            if (elements.Count > 0)
                            {
                                canvas.ResetMatrix();
                                ElementOverlay.Draw(canvas, page, elements, toDevice * Matrix.Translate(-left, -top - y0), images);
                            }
                        }
                    }
                }
                for (var i = 0; i < width * rows; i++)
                {
                    // Rec. 601 luma of the (opaque) pixel
                    int r = buffer[i * 4], g = buffer[i * 4 + 1], b = buffer[i * 4 + 2];
                    gray[y0 * width + i] = (byte)((r * 299 + g * 587 + b * 114 + 500) / 1000);
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
        return (new GrayImage(width, height, gray), toDevice * Matrix.Translate(-left, -top));
    }
}
