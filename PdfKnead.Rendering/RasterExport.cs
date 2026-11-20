using System.IO.Compression;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Filters.Jbig2;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Writing;
using SkiaSharp;

namespace PdfKnead.Rendering;

/// <summary>How the pages are saved as images: resolution and encoding.</summary>
/// <param name="Dpi">Resolution in dots per inch; very large pages get less (see <see cref="RasterExport.Scale"/>).</param>
/// <param name="Lossless">Lossless (Flate, larger) instead of JPEG.</param>
/// <param name="JpegQuality">JPEG quality, clamped to 50 … 100.</param>
/// <param name="Compact">
/// Compact (MRC): text and lines as sharp 1-bit masks (JBIG2) at twice <paramref name="Dpi"/> (at most 600 dpi), the rest without text
/// as JPEG at half the resolution (at least 100 dpi) — much smaller, the text without JPEG fringes, still images only.
/// </param>
public sealed record RasterSettings(int Dpi = 300, bool Lossless = false, int JpegQuality = 90, bool Compact = false);

/// <summary>
/// "Save as image": every page rendered as displayed (with annotations and placed elements), as an image in a new PDF — without text,
/// fonts, metadata, bookmarks, fields, attachments, scripts. Afterwards nothing can be copied, searched or edited.
/// </summary>
public static class RasterExport
{
    /// <summary>Maximum number of pixels per page (200 MB as RGBA); larger pages (A0 …) get correspondingly fewer dpi.</summary>
    public const long MaxPixels = 50_000_000;

    /// <summary>Longest edge in pixels.</summary>
    public const int MaxSide = 32_768;

    /// <summary>Pixels per point for a page: the requested resolution, less for very large pages.</summary>
    /// <param name="page">The page.</param>
    /// <param name="dpi">Requested resolution in dots per inch.</param>
    /// <returns>Pixels per point, limited by <see cref="MaxPixels"/> and <see cref="MaxSide"/>; 0.001 for nonsensical page sizes.</returns>
    public static double Scale(PdfKnead.Core.Pdf.Document.PdfPage page, int dpi)
    {
        var scale = dpi / 72.0;
        // Nonsensical page sizes (crafted /MediaBox or /UserUnit): smallest resolution instead of an unbounded bitmap
        if (!double.IsFinite(page.Width) || !double.IsFinite(page.Height) || page.Width <= 0 || page.Height <= 0) return 1e-3;
        var pixels = page.Width * scale * page.Height * scale;
        if (pixels > MaxPixels) scale *= Math.Sqrt(MaxPixels / pixels);
        // Also limit per edge: a very narrow, very long strip has few pixels but an edge beyond int
        var longest = Math.Max(page.Width, page.Height) * scale;
        return longest > MaxSide ? scale * MaxSide / longest : scale;
    }

    /// <summary>Renders a page as displayed: content and annotations, own elements on top.</summary>
    /// <param name="item">The page with its elements.</param>
    /// <param name="pixelsPerPoint">Resolution (1 = 72 dpi).</param>
    /// <param name="images">Image cache.</param>
    /// <param name="cancel">Cancels rendering.</param>
    /// <param name="export">For image files, a format without layers: layers in their export state (ISO 32000-2, 8.11.4.5).</param>
    /// <returns>The bitmap; the caller disposes it.</returns>
    public static SKBitmap Render(PageItem item, double pixelsPerPoint, ImageCache images, CancellationToken cancel = default, bool export = false)
    {
        var list = PageInterpreter.Build(item.Source, item.Page, cancel, withoutOwnElements: true, forExport: export);
        var bitmap = SkiaRenderer.RenderPage(list, item.Source, item.Page, pixelsPerPoint, images, cancel);
        if (item.Elements.Count > 0)
        {
            // Ballpoint structure at full resolution (until then the display may show a coarser one)
            foreach (var element in item.Elements) ElementAppearance.Final(element);
            using var canvas = new SKCanvas(bitmap);
            ElementOverlay.Draw(canvas, item.Page, item.Elements, pixelsPerPoint, images: images);
        }
        return bitmap;
    }

    /// <summary>Writes all pages as an image PDF to <paramref name="output"/> (one page at a time in memory).</summary>
    /// <param name="pages">The pages.</param>
    /// <param name="output">Target stream.</param>
    /// <param name="settings">Resolution and encoding.</param>
    /// <param name="progress">Reports the number of finished pages, or null.</param>
    /// <param name="cancel">Cancels writing.</param>
    public static void Write(IReadOnlyList<PageItem> pages, Stream output, RasterSettings settings, IProgress<int>? progress = null, CancellationToken cancel = default)
    {
        var builder = new RasterPdfBuilder(output);
        var images = new ImageCache(256L << 20);
        // Compact: flatten placed elements first — then they are part of the display list and are separated into layers as well
        Core.Pdf.IO.MemoryPdf? buffer = null;
        PdfKnead.Core.Pdf.Document.PdfDocument? flat = null;
        if (settings.Compact && pages.Any(p => p.Elements.Count > 0))
        {
            buffer = new Core.Pdf.IO.MemoryPdf();
            PdfWriter.Write(pages[0].Source, pages, buffer, new PdfSaveOptions { Flatten = true, KeepEncryption = false, ObjectStreams = false }, cancel);
            flat = buffer.OpenDocument();
            pages = PageItem.From(flat);
        }
        using var _ = flat;
        using var __ = buffer;
        for (var i = 0; i < pages.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var item = pages[i];
            if (settings.Compact && AddLayered(builder, item, settings, images, cancel))
            {
                progress?.Report(i + 1);
                continue;
            }
            var scale = Scale(item.Page, settings.Dpi);
            using var bitmap = Render(item, scale, images, cancel);
            var (data, filter) = settings.Lossless ? (Flate(bitmap), "FlateDecode") : (Jpeg(bitmap, settings.JpegQuality), "DCTDecode");
            builder.AddPage(item.Page.Width, item.Page.Height, data, filter, bitmap.Width, bitmap.Height);
            progress?.Report(i + 1);
        }
        builder.Finish();
        images.Clear();
    }

    /// <summary>
    /// Writes each page as an image file to <paramref name="folder"/> (<see cref="PageImageWriter"/>: PNG, JPEG, TIFF — also all pages in
    /// one file — or WebP; „Name_001.png“ …). Existing files are never overwritten („(2)“ etc. is appended instead).
    /// </summary>
    /// <param name="pages">The pages.</param>
    /// <param name="folder">Target folder.</param>
    /// <param name="baseName">Start of the file names.</param>
    /// <param name="settings">Format, quality and resolution (very large pages get less, see <see cref="Scale"/>).</param>
    /// <param name="progress">Reports the number of finished pages, or null.</param>
    /// <param name="cancel">Cancels writing.</param>
    /// <returns>The paths of the written files.</returns>
    public static List<string> WriteImages(IReadOnlyList<PageItem> pages, string folder, string baseName, Core.Imaging.PageImageSettings settings,
        IProgress<int>? progress = null, CancellationToken cancel = default)
    {
        var images = new ImageCache(256L << 20);
        try
        {
            return PageImageWriter.Write(pages.Count, i =>
            {
                var item = pages[i];
                var scale = Scale(item.Page, settings.Dpi);
                return new PageImageWriter.BitmapRaster(Render(item, scale, images, cancel, export: true), scale * 72);
            }, folder, baseName, settings, progress, cancel);
        }
        finally
        {
            images.Clear();
        }
    }

    /// <summary>At most this many colours get their own mask; rarer ones stay in the background.</summary>
    private const int MaxMaskColors = 16;

    /// <summary>Pages with more entries are not separated into layers (the overlap check would be too expensive) — they become a single image.</summary>
    private const int MaxLayeredItems = 300_000;

    /// <summary>At most this many overlaps are checked to keep the order of the layers; beyond it the page is written as a single image.</summary>
    private const long MaxLayeredWork = 20_000_000;

    /// <summary>
    /// Page as layers (MRC): text, lines and small single-coloured areas as 1-bit masks per colour at twice the resolution (at most
    /// 600 dpi), the rest without them as JPEG at half the resolution (completely white: no image at all). Whatever is covered by
    /// something drawn later, lies in a transparency group, is transparent or has a blend mode stays in the background — so the
    /// stacking order is preserved. False = nothing to separate (then the page becomes a single image).
    /// </summary>
    private static bool AddLayered(RasterPdfBuilder builder, PageItem item, RasterSettings settings, ImageCache images, CancellationToken cancel)
    {
        var (doc, page) = (item.Source, item.Page);
        // Own elements never from the file: deleted ones would otherwise come back; existing ones are part of the content after flattening
        var list = PageInterpreter.Build(doc, page, cancel, withoutOwnElements: true);
        var items = list.Items.Span;
        if (items.Length == 0 || items.Length > MaxLayeredItems) return false;

        // Foreground: colour per entry (−1 = background)
        var inGroup = new bool[items.Length];
        foreach (var group in list.Groups)
            for (var i = Math.Max(0, group.Start); i < Math.Min(items.Length, group.End); i++) inGroup[i] = true;
        var color = new int[items.Length];
        for (var i = 0; i < items.Length; i++) color[i] = inGroup[i] ? -1 : ForegroundColor(items[i], list.Paints[items[i].Paint]);
        // Palette: the most frequent colours; the masks are laid over the background in this order
        var palette = color.Where(c => c >= 0).GroupBy(c => c).OrderByDescending(g => g.Count()).Take(MaxMaskColors).Select(g => g.Key).ToList();
        var paletteIndex = palette.Select((c, k) => (c, k)).ToDictionary(p => p.c, p => p.k);
        for (var i = 0; i < color.Length; i++)
            if (color[i] >= 0) color[i] = paletteIndex.TryGetValue(color[i], out var k) ? k : -1;
        // Preserve the order, from back to front (what comes later is already decided): if an entry is covered by something drawn
        // later that ends up in the background or in a mask laid earlier, it would otherwise appear on top — so it becomes background
        // itself. Example: black text on a small white cover-up; black is more frequent, so its mask would lie under the white one.
        // The work grows with the overlaps: a page of 70,000 entries lying over each other took 19 s (a damaged file); beyond
        // MaxLayeredWork the page becomes a single image (docs/FEHLERGEDAECHTNIS.md, No. 278)
        long work = 0;
        for (var i = items.Length - 1; i >= 0; i--)
        {
            if (color[i] < 0) continue;
            ref readonly var it = ref items[i];
            using var hits = list.QueryHits(RectD.FromLtrb(it.Left, it.Top, it.Right, it.Bottom));
            if ((work += hits.Count) > MaxLayeredWork) return false;
            for (var h = hits.Count - 1; h >= 0 && hits[h] > i; h--)
                if (color[hits[h]] < color[i])
                {
                    color[i] = -1;
                    break;
                }
        }
        // Colours that became empty are dropped (order preserved)
        var used = new bool[palette.Count];
        foreach (var c in color)
            if (c >= 0) used[c] = true;
        var renumber = new int[palette.Count];
        for (int k = 0, n = 0; k < palette.Count; k++) renumber[k] = used[k] ? n++ : -1;
        for (var i = 0; i < color.Length; i++)
            if (color[i] >= 0) color[i] = renumber[color[i]];
        palette = [.. palette.Where((_, k) => used[k])];
        if (palette.Count == 0) return false;   // only images and areas: single image

        // One mask per colour at twice the resolution (at most 600 dpi): 1 bit has no anti-aliasing — every reader smooths when
        // scaling down
        var maskScale = Scale(page, Math.Min(600, settings.Dpi * 2));
        var (mw, mh) = PageGeometry.DeviceSize(page, maskScale);
        var masks = new List<RasterPdfBuilder.ColorMask>(palette.Count);
        using (var alpha = new SKBitmap(new SKImageInfo(mw, mh, SKColorType.Alpha8, SKAlphaType.Premul)))
        {
            var rowBytes = (mw + 7) / 8;
            var bits = new byte[rowBytes * mh];
            for (var k = 0; k < palette.Count; k++)
            {
                cancel.ThrowIfCancellationRequested();
                using (var canvas = new SKCanvas(alpha))
                {
                    canvas.Clear(SKColors.Transparent);
                    var index = k;
                    SkiaRenderer.RenderTile(canvas, list, doc, PageGeometry.PageToDevice(page, maskScale), new SKRectI(0, 0, mw, mh), images,
                        n => color[n] == index, cancel);
                }
                // Covered from half coverage = paint (bit 0), otherwise 1
                Array.Fill(bits, (byte)0xFF);
                var pixels = alpha.GetPixelSpan();
                for (var y = 0; y < mh; y++)
                {
                    var row = pixels.Slice(y * alpha.RowBytes, mw);
                    for (var x = 0; x < mw; x++)
                        if (row[x] >= 128) bits[y * rowBytes + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
                }
                var rgb = (uint)palette[k];
                masks.Add(new RasterPdfBuilder.ColorMask((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb,
                    Jbig2Encoder.Generic(bits, mw, mh, rowBytes, cancel), mw, mh));
            }
        }

        // Background without text, half the resolution (at least 100 dpi, unless the masks are coarser)
        var backgroundScale = Math.Max(Scale(page, settings.Dpi) / 2, Math.Min(maskScale, 100 / 72.0));
        var (bw, bh) = PageGeometry.DeviceSize(page, backgroundScale);
        (byte[], int, int, bool)? background = null;
        using (var bitmap = new SKBitmap(new SKImageInfo(bw, bh, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SkiaRenderer.Paper);
                SkiaRenderer.RenderTile(canvas, list, doc, PageGeometry.PageToDevice(page, backgroundScale), new SKRectI(0, 0, bw, bh), images,
                    n => color[n] < 0, cancel);
            }
            var (white, gray) = Inspect(bitmap);
            if (!white)
            {
                var data = gray ? GrayJpeg(bitmap, settings.JpegQuality) : Jpeg(bitmap, settings.JpegQuality);
                background = (data, bw, bh, gray);
            }
        }
        builder.AddLayeredPage(page.Width, page.Height, background, masks);
        return true;
    }

    /// <summary>Foreground colour (RGB) of an entry, or −1: single-coloured, opaque, without blend mode or overprint — text, lines, small areas.</summary>
    private static int ForegroundColor(in DisplayItem item, in Paint paint)
    {
        if (paint.BlendMode != 0 || paint.Overprint != 0) return -1;
        static int Solid(uint argb, int shading) => argb >> 24 == 255 && shading < 0 ? (int)(argb & 0xFFFFFF) : -1;
        var (w, h) = (item.Right - item.Left, item.Bottom - item.Top);
        var small = Math.Min(w, h) < 3 || w * h < 600;   // thin line or about the size of a character
        return item.Kind switch
        {
            DisplayItemKind.Fill when item.Glyph || small => Solid(paint.Fill, paint.FillShading),
            DisplayItemKind.Stroke => Solid(paint.Stroke, paint.StrokeShading),
            DisplayItemKind.FillStroke when small && paint.Fill == paint.Stroke => Solid(paint.Fill, paint.FillShading),
            _ => -1,
        };
    }

    /// <summary>Completely white (then no background image)? Only grey tones (then a greyscale image, a third of the data)?</summary>
    private static (bool White, bool Gray) Inspect(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        bool white = true, gray = true;
        for (var y = 0; y < bitmap.Height && (white || gray); y++)
        {
            var row = pixels.Slice(y * bitmap.RowBytes, bitmap.Width * 4);
            for (var x = 0; x < row.Length; x += 4)
            {
                int r = row[x], g = row[x + 1], b = row[x + 2];
                if (r < 250 || g < 250 || b < 250) white = false;
                if (Math.Abs(r - g) > 6 || Math.Abs(r - b) > 6) gray = false;
            }
        }
        return (white, gray);
    }

    private static byte[] GrayJpeg(SKBitmap bitmap, int quality)
    {
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var gray = new SKBitmap(info);
        bitmap.CopyTo(gray, SKColorType.Gray8);
        using var image = SKImage.FromBitmap(gray);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 50, 100));
        return data.ToArray();
    }

    private static byte[] Jpeg(SKBitmap bitmap, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 50, 100));
        return data.ToArray();
    }

    /// <summary>RGB, 8 bit, Flate-compressed (the paper is opaque white, alpha is dropped).</summary>
    private static byte[] Flate(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        var bgra = bitmap.ColorType == SKColorType.Bgra8888;
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[bitmap.Width * 3];
            for (var y = 0; y < bitmap.Height; y++)
            {
                var line = pixels.Slice(y * bitmap.RowBytes, bitmap.Width * 4);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    row[x * 3] = line[x * 4 + (bgra ? 2 : 0)];
                    row[x * 3 + 1] = line[x * 4 + 1];
                    row[x * 3 + 2] = line[x * 4 + (bgra ? 0 : 2)];
                }
                z.Write(row);
            }
        }
        return output.ToArray();
    }
}
