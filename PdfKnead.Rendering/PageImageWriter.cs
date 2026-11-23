using System.IO.Compression;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Scanning;
using SkiaSharp;

namespace PdfKnead.Rendering;

/// <summary>
/// Saves pages as image files (KONZEPT §5.25, „Als Bild speichern“) — scanned pages from the scan window and rendered pages of a document
/// alike: PNG (black and white with one bit, greyscale with one channel), JPEG and WebP with a quality, TIFF one file per page or all
/// pages in one file (black and white with CCITT Group 4). PNG, JPEG and TIFF note the resolution; WebP has no place for it. Names follow
/// „Name_001“; an existing file is never replaced (then „Name_001 (2)“).
/// </summary>
public static class PageImageWriter
{
    /// <summary>Saves scanned pages.</summary>
    /// <param name="count">The number of pages.</param>
    /// <param name="page">Gives page <c>i</c> (processed as it will be saved); asked for in order, one at a time.</param>
    /// <param name="folder">The target folder.</param>
    /// <param name="baseName">The start of the file names.</param>
    /// <param name="settings">Format and quality (<see cref="PageImageSettings.Dpi"/> is not used: scans keep their resolution).</param>
    /// <param name="progress">Reports the number of finished pages, or null.</param>
    /// <param name="cancel">Stops between pages.</param>
    /// <returns>The paths of the written files.</returns>
    public static List<string> Write(int count, Func<int, ScannedImage> page, string folder, string baseName, PageImageSettings settings,
        IProgress<int>? progress = null, CancellationToken cancel = default) =>
        Write(count, i => new ScanRaster(page(i)), folder, baseName, settings, progress, cancel);

    /// <summary>Saves pages given as rasters (scans or rendered pages); each raster is disposed once written.</summary>
    internal static List<string> Write(int count, Func<int, Raster> page, string folder, string baseName, PageImageSettings settings,
        IProgress<int>? progress, CancellationToken cancel)
    {
        settings = settings.Normalized();
        baseName = PageImageFiles.CleanBaseName(baseName);
        var extension = PageImageFiles.Extension(settings.Format);
        var written = new List<string>();
        if (count <= 0) return written;

        if (settings is { Format: PageImageFormat.Tiff, OneFile: true })
        {
            var path = PageImageFiles.Free(folder, baseName, extension);
            Raster? current = null;
            try
            {
                PdfFileSaver.SaveFile(path, output => TiffEncoder.EncodePages(count, i =>
                {
                    cancel.ThrowIfCancellationRequested();
                    if (i > 0) progress?.Report(i);
                    current?.Dispose();
                    current = page(i);
                    return Tiff(current);
                }, output, cancel));
            }
            finally
            {
                current?.Dispose();
            }
            progress?.Report(count);
            written.Add(path);
            return written;
        }

        for (var i = 0; i < count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            using var raster = page(i);
            var path = PageImageFiles.Free(folder, PageImageFiles.Name(baseName, i + 1, count), extension);
            var number = i;
            PdfFileSaver.SaveFile(path, output => Encode(raster, settings, output, number, count));   // temporary file, then moved: never half an image
            written.Add(path);
            progress?.Report(i + 1);
        }
        return written;
    }

    /// <summary>Encodes one page in the chosen format.</summary>
    /// <param name="page">The number of the page in its document (0 for the first), for PageNumber in TIFF.</param>
    /// <param name="pages">The pages of the document.</param>
    internal static void Encode(Raster raster, PageImageSettings settings, Stream output, int page = 0, int pages = 1)
    {
        switch (settings.Format)
        {
            case PageImageFormat.Tiff:
                TiffEncoder.EncodePages(1, _ => Tiff(raster), output, firstPage: page, documentPages: pages);
                break;
            case PageImageFormat.Jpeg:
            case PageImageFormat.WebP:
            {
                using var image = raster.Image();
                using var data = image.Encode(settings.Format == PageImageFormat.Jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Webp, settings.Quality)
                    ?? throw new IOException("Das Bild ließ sich nicht packen.");
                var bytes = data.ToArray();
                if (settings.Format == PageImageFormat.Jpeg) bytes = PageImageFiles.WithJpegDensity(bytes, raster.Dpi);
                output.Write(bytes);
                break;
            }
            default:
                Png(raster, output);
                break;
        }
    }

    private static void Png(Raster raster, Stream output)
    {
        var (w, h) = (raster.Width, raster.Height);
        switch (raster.Kind)
        {
            // the pages are rendered in sRGB: grey and colour say so with the profile (RFC 2083, 9.2, 9.3; decided by the user on
            // 22.11.2026); black and white needs none, as in the TIFF pages
            case TiffPageKind.BlackWhite:
                // one bit, 1 = white (PNG greyscale)
                PngEncoder.Encode(w, h, 1, PngColorType.Gray, (y, row) => Pack(raster.Line(y), row, inkIsOne: false), output,
                    dpi: raster.Dpi, level: CompressionLevel.Optimal);
                break;
            case TiffPageKind.Gray:
                PngEncoder.Encode(w, h, 8, PngColorType.Gray, (y, row) => raster.Line(y).CopyTo(row), output, iccProfile: PageImageFiles.RenderedProfile(false),
                    dpi: raster.Dpi, level: CompressionLevel.Optimal);
                break;
            default:
                // at most 256 colours (a chart, a form): a palette image (RFC 2083, 9.6)
                PngEncoder.EncodeColour(w, h, false, () => (y, row) => raster.Line(y).CopyTo(row), output, iccProfile: PageImageFiles.RenderedProfile(true),
                    dpi: raster.Dpi, level: CompressionLevel.Optimal);
                break;
        }
    }

    private static TiffPage Tiff(Raster raster) =>
        new(raster.Width, raster.Height, raster.Kind, raster.Dpi, raster.Kind == TiffPageKind.BlackWhite
            ? (y, row) => Pack(raster.Line(y), row, inkIsOne: true)
            : (y, row) => raster.Line(y).CopyTo(row));

    /// <summary>Packs a row of grey samples into bits, most significant first: ink (below 128) as 1 or as 0.</summary>
    private static void Pack(ReadOnlySpan<byte> grey, Span<byte> bits, bool inkIsOne)
    {
        bits.Clear();
        for (var x = 0; x < grey.Length; x++)
            if (grey[x] < 128 == inkIsOne) bits[x >> 3] |= (byte)(0x80 >> (x & 7));
    }

    /// <summary>A page to save: its size, what it holds, its resolution and its rows.</summary>
    internal abstract class Raster : IDisposable
    {
        public abstract int Width { get; }
        public abstract int Height { get; }
        public abstract TiffPageKind Kind { get; }
        public abstract double Dpi { get; }

        /// <summary>Row <c>y</c>: one byte per pixel (black and white: 0 or 255; greyscale) or three (RGB). Valid until the next call.</summary>
        public abstract ReadOnlySpan<byte> Line(int y);

        /// <summary>The page as a Skia image (for JPEG and WebP); the caller disposes it.</summary>
        public abstract SKImage Image();

        public virtual void Dispose()
        {
        }
    }

    /// <summary>A scanned page.</summary>
    private sealed class ScanRaster(ScannedImage page) : Raster
    {
        public override int Width => page.Width;
        public override int Height => page.Height;
        public override TiffPageKind Kind => page.BlackWhite && page.Channels == 1 ? TiffPageKind.BlackWhite : page.Channels == 1 ? TiffPageKind.Gray : TiffPageKind.Rgb;
        public override double Dpi => page.Dpi;
        public override ReadOnlySpan<byte> Line(int y) => page.Pixels.AsSpan(y * page.Width * page.Channels, page.Width * page.Channels);

        public override SKImage Image()
        {
            using var bitmap = ScanPdf.ToBitmap(page);
            return SKImage.FromBitmap(bitmap);
        }
    }

    /// <summary>A rendered page of a document (opaque RGBA); owns the bitmap.</summary>
    internal sealed class BitmapRaster(SKBitmap bitmap, double dpi) : Raster
    {
        private readonly SKBitmap _bitmap = bitmap.ColorType == SKColorType.Rgba8888 ? bitmap : Convert(bitmap);
        private readonly byte[] _line = new byte[bitmap.Width * 3];

        public override int Width => _bitmap.Width;
        public override int Height => _bitmap.Height;
        public override TiffPageKind Kind => TiffPageKind.Rgb;
        public override double Dpi => dpi;

        public override ReadOnlySpan<byte> Line(int y)
        {
            var source = _bitmap.GetPixelSpan().Slice(y * _bitmap.RowBytes, _bitmap.Width * 4);
            for (var x = 0; x < _bitmap.Width; x++)
            {
                _line[x * 3] = source[x * 4];
                _line[x * 3 + 1] = source[x * 4 + 1];
                _line[x * 3 + 2] = source[x * 4 + 2];
            }
            return _line;
        }

        public override SKImage Image() => SKImage.FromBitmap(_bitmap);

        public override void Dispose()
        {
            if (!ReferenceEquals(_bitmap, bitmap)) _bitmap.Dispose();
            bitmap.Dispose();
        }

        private static SKBitmap Convert(SKBitmap source)
        {
            var copy = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using var canvas = new SKCanvas(copy);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, 0, 0);
            return copy;
        }
    }
}
