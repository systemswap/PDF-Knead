using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters.Jbig2;
using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Scanning;
using SkiaSharp;

namespace PdfKnead.Rendering;

/// <summary>
/// Scanned pages as PDF pages: colour and greyscale as JPEG, black and white as a lossless JBIG2 mask (ISO 32000-2, 7.4.7 and 8.9.6.2),
/// each page as large as the scanned area at its resolution. Also the small preview pages behind the tiles of the scan window.
/// </summary>
public static class ScanPdf
{
    /// <summary>The JPEG quality of taken-over pages: text stays sharp, the file stays moderate (as „PDF als Bild sichern“).</summary>
    public const int Quality = 90;

    /// <summary>The longest edge of a preview page in pixels: sharp enough for the tiles, small enough for many pages.</summary>
    public const int PreviewEdge = 1000;

    /// <summary>Builds a document from the pages, in memory.</summary>
    /// <param name="pages">The pages in their order; at least one.</param>
    /// <param name="quality">The JPEG quality (50 … 100).</param>
    /// <param name="cancel">Cancels between pages.</param>
    /// <returns>The document (one page per image); the caller owns it.</returns>
    /// <exception cref="ArgumentException">No page.</exception>
    public static PdfDocument Build(IReadOnlyList<ScannedImage> pages, int quality = Quality, CancellationToken cancel = default)
    {
        if (pages.Count == 0) throw new ArgumentException("no page", nameof(pages));
        return Build(pages.Count, i => pages[i], quality, cancel);
    }

    /// <summary>
    /// Builds a document from pages made one at a time (processed at full resolution): each page is packed as soon as it is made, so only
    /// one processed page is in memory, not all of them.
    /// </summary>
    /// <param name="count">The number of pages; at least one.</param>
    /// <param name="page">Gives page <c>i</c>; asked for in order.</param>
    /// <param name="quality">The JPEG quality (50 … 100).</param>
    /// <param name="cancel">Cancels between pages.</param>
    /// <returns>The document (one page per image); the caller owns it.</returns>
    /// <exception cref="ArgumentException">No page.</exception>
    public static PdfDocument Build(int count, Func<int, ScannedImage> page, int quality = Quality, CancellationToken cancel = default)
    {
        if (count <= 0) throw new ArgumentException("no page", nameof(count));
        using var buffer = new MemoryPdf();
        var builder = new RasterPdfBuilder(buffer);
        for (var i = 0; i < count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            Add(builder, page(i), quality, cancel);
        }
        builder.Finish();
        return buffer.OpenDocument();
    }

    /// <summary>A one-page document with a reduced copy of the page (for the tiles); the page keeps the size of the original in points.</summary>
    /// <param name="page">The scanned page.</param>
    /// <param name="edge">The longest edge of the copy in pixels.</param>
    /// <returns>The document; the caller owns it.</returns>
    public static PdfDocument Preview(ScannedImage page, int edge = PreviewEdge)
    {
        var small = Reduce(page, edge);
        using var buffer = new MemoryPdf();
        var builder = new RasterPdfBuilder(buffer);
        builder.AddPage(page.WidthPoints, page.HeightPoints, Jpeg(small, 85), "DCTDecode", small.Width, small.Height, small.Channels == 1);
        builder.Finish();
        return buffer.OpenDocument();
    }

    private static void Add(RasterPdfBuilder builder, ScannedImage page, int quality, CancellationToken cancel)
    {
        if (page.BlackWhite)
        {
            var rowBytes = (page.Width + 7) / 8;
            var bits = new byte[(long)rowBytes * page.Height];
            Array.Fill(bits, (byte)0xFF);   // 1 = no ink; set ink to 0 (/ImageMask: 0 paints)
            for (var y = 0; y < page.Height; y++)
                for (var x = 0; x < page.Width; x++)
                    if (page.Pixels[(long)y * page.Width + x] < 128) bits[(long)y * rowBytes + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
            var mask = new RasterPdfBuilder.ColorMask(0, 0, 0, Jbig2Encoder.Generic(bits, page.Width, page.Height, rowBytes, cancel), page.Width, page.Height);
            builder.AddLayeredPage(page.WidthPoints, page.HeightPoints, null, [mask]);
            return;
        }
        builder.AddPage(page.WidthPoints, page.HeightPoints, Jpeg(page, quality), "DCTDecode", page.Width, page.Height, page.Channels == 1);
    }

    /// <summary>The page as JPEG (greyscale stays one channel).</summary>
    internal static byte[] Jpeg(ScannedImage page, int quality)
    {
        using var bitmap = ToBitmap(page);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 50, 100));
        return data.ToArray();
    }

    /// <summary>The page as a Skia bitmap: Gray8 or opaque RGBA.</summary>
    public static SKBitmap ToBitmap(ScannedImage page)
    {
        if (page.Channels == 1)
        {
            var gray = new SKBitmap(new SKImageInfo(page.Width, page.Height, SKColorType.Gray8, SKAlphaType.Opaque));
            var span = gray.GetPixelSpan();
            for (var y = 0; y < page.Height; y++)
                page.Pixels.AsSpan(y * page.Width, page.Width).CopyTo(span.Slice(y * gray.RowBytes, page.Width));
            return gray;
        }
        var rgba = new SKBitmap(new SKImageInfo(page.Width, page.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = rgba.GetPixelSpan();
        for (var y = 0; y < page.Height; y++)
        {
            var source = page.Pixels.AsSpan(y * page.Width * 3, page.Width * 3);
            var target = pixels.Slice(y * rgba.RowBytes, page.Width * 4);
            for (var x = 0; x < page.Width; x++)
            {
                target[x * 4] = source[x * 3];
                target[x * 4 + 1] = source[x * 3 + 1];
                target[x * 4 + 2] = source[x * 3 + 2];
                target[x * 4 + 3] = 255;
            }
        }
        return rgba;
    }

    /// <summary>A copy whose longest edge is at most <paramref name="edge"/> pixels (box average); the page itself if it is smaller.</summary>
    /// <param name="page">The page.</param>
    /// <param name="edge">The longest edge.</param>
    /// <returns>The reduced page; its resolution is scaled with it, so the size in points stays.</returns>
    public static ScannedImage Reduce(ScannedImage page, int edge)
    {
        var longest = Math.Max(page.Width, page.Height);
        if (longest <= edge) return page;
        var factor = (int)Math.Ceiling(longest / (double)edge);
        var w = Math.Max(1, page.Width / factor);
        var h = Math.Max(1, page.Height / factor);
        var c = page.Channels;
        var result = new byte[(long)w * h * c];
        var area = factor * factor;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var k = 0; k < c; k++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < factor; dy++)
                    {
                        var row = (long)(y * factor + dy) * page.Width * c;
                        for (var dx = 0; dx < factor; dx++) sum += page.Pixels[row + (x * factor + dx) * c + k];
                    }
                    result[((long)y * w + x) * c + k] = (byte)((sum + area / 2) / area);
                }
        return new ScannedImage(w, h, c, Math.Max(1, page.Dpi / factor), result);
    }
}
