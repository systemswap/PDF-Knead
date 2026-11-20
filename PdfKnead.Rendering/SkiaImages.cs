using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using SkiaSharp;

namespace PdfKnead.Rendering;

/// <summary>JPEG decoding and encoding via Skia (libjpeg-turbo) for the image decoder and writer of the core library. Thread-safe.</summary>
public sealed class SkiaImageCodec : IImageCodec
{
    /// <summary>The shared instance (the codec has no state).</summary>
    public static readonly SkiaImageCodec Instance = new();

    /// <summary>
    /// libjpeg-turbo (in Skia) as the reference for the differential test of the own decoder — <c>pdf-knead jpegvergleich</c> and the tests;
    /// the program itself decodes JPEG only with its own decoder (docs/FEHLERGEDAECHTNIS.md, No. 805). Only <c>DCTDecode</c> (null otherwise);
    /// colour JPEGs come back as RGBA (4 components; CMYK converted by Skia as inverted CMYK, without a profile), grey ones as 1 byte per pixel;
    /// images above <see cref="PdfImageDecoder.MaxPixels"/> are rejected, those above <paramref name="maxPixels"/> decoded at a reduced scale.
    /// </summary>
    public (int Width, int Height, int Components, byte[] Samples, bool AlreadyRgb)? Decode(byte[] data, string filter, long maxPixels)
    {
        if (filter != "DCTDecode") return null;   // JPEG 2000, CCITT and JBIG2 are decoded by the core library
        // Sometimes there are bytes before the JPEG start (FF D8) — inline images with CR LF after "ID" (bug1065245: Skia did not
        // recognise the format, the image stayed pale): search the first bytes for the start, as common PDF readers do
        if (data.Length > 2 && !(data[0] == 0xFF && data[1] == 0xD8) && data.AsSpan(0, Math.Min(data.Length, 64)).IndexOf([(byte)0xFF, (byte)0xD8]) is > 0 and var soi)
            data = data[soi..];
        if (TooCostly(data)) return null;   // crafted: gigabytes or hours for a few kilobytes (docs/FEHLERGEDAECHTNIS.md, No. 742)
        SKColorSpace? target = null;
        using var codec = SKCodec.Create(new SKMemoryStream(data));
        if (codec is null) return null;
        var size = codec.Info.Size;
        if ((long)size.Width * size.Height > PdfImageDecoder.MaxPixels) return null;
        // Decode large JPEGs at a reduced scale right away: libjpeg computes 1/2, 1/4, 1/8 at almost no extra cost
        if ((long)size.Width * size.Height > maxPixels)
            size = codec.GetScaledDimensions((float)Math.Sqrt((double)maxPixels / ((long)size.Width * size.Height)));
        // Grey JPEGs (common in scans) as 1 byte per pixel — the same values, a quarter of the memory
        var gray = codec.Info.ColorType == SKColorType.Gray8;
        var info = gray
            ? new SKImageInfo(size.Width, size.Height, SKColorType.Gray8, SKAlphaType.Opaque)
            : new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, target);
        // Directly into the final array (a detour via bitmap, copy, RGB and RGBA again would cost 19 instead of 4 bytes per pixel); more
        // than an array can hold: not decoded
        if ((long)info.Width * info.Height * info.BytesPerPixel > Array.MaxLength) return null;
        var pixels = new byte[(long)info.Width * info.Height * info.BytesPerPixel];
        SKCodecResult result;
        unsafe
        {
            fixed (byte* p = pixels) result = codec.GetPixels(info, (IntPtr)p);
        }
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
        return (info.Width, info.Height, gray ? 1 : 4, pixels, true);
    }

    /// <summary>Coefficient memory above which a progressive JPEG must justify itself by its length.</summary>
    private const long CostlyCoefficientBytes = 64L << 20;

    /// <summary>Most 8 × 8 blocks times scans the codec may work through for one image.</summary>
    private const long MaxBlockScans = 4_000_000_000;

    /// <summary>
    /// Whether a JPEG would cost far more than its data can stand for (Konformitätsabgleich M2, security; docs/FEHLERGEDAECHTNIS.md, No. 742):
    /// <list type="bullet">
    /// <item>Progressive (SOF2, 6, 10, 14): the codec keeps the coefficients of the whole image (128 bytes per 8 × 8 block and component), also
    /// when it decodes smaller. A complete stream needs at least one bit per block and component in its first DC scan (ITU-T T.81, G.1.2.1),
    /// so its coefficients are at most 1 024 times its length; above 64 MB and twice that ratio it is a crafted size — a 7 KB file claiming
    /// 12 000 × 12 000 pixels took 950 MB.</item>
    /// <item>Any JPEG: blocks times scans (each scan passes over the image) above <see cref="MaxBlockScans"/> — thousands of scans over a large
    /// image.</item>
    /// </list>
    /// </summary>
    /// <param name="data">The JPEG from its start marker.</param>
    internal static bool TooCostly(ReadOnlySpan<byte> data)
    {
        long blocks = 0;
        var progressive = false;
        for (var at = 2; at + 4 <= data.Length && data[at] == 0xFF;)
        {
            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            var length = data[at + 2] << 8 | data[at + 3];
            if (length < 2 || at + 2 + length > data.Length) break;
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC) && length >= 8)
            {
                progressive = marker is 0xC2 or 0xC6 or 0xCA or 0xCE;
                int height = data[at + 5] << 8 | data[at + 6], width = data[at + 7] << 8 | data[at + 8], n = data[at + 9];
                if (length < 8 + 3 * n) break;
                int hMax = 1, vMax = 1;
                for (var c = 0; c < n; c++)
                {
                    hMax = Math.Max(hMax, data[at + 11 + 3 * c] >> 4);
                    vMax = Math.Max(vMax, data[at + 11 + 3 * c] & 15);
                }
                // per component its blocks after subsampling (A.1.1); a height of 0 (number of lines later, DNL) counted as one block row
                for (var c = 0; c < n; c++)
                {
                    int h = Math.Max(1, data[at + 11 + 3 * c] >> 4), v = Math.Max(1, data[at + 11 + 3 * c] & 15);
                    long cw = ((long)width * h + hMax - 1) / hMax, ch = ((long)Math.Max(1, height) * v + vMax - 1) / vMax;
                    blocks += (cw + 7) / 8 * ((ch + 7) / 8);
                }
                break;
            }
            if (marker == 0xDA) break;
            at += 2 + length;
        }
        if (blocks == 0) return false;
        if (progressive && blocks * 128 > CostlyCoefficientBytes && blocks * 128 > 2048L * data.Length) return true;
        // scans: an SOS marker cannot occur inside entropy-coded data (FF there is followed by 00 or a restart marker)
        long scans = 0;
        for (var i = data.IndexOf((byte)0xFF); i >= 0 && i + 1 < data.Length;)
        {
            if (data[i + 1] == 0xDA) scans++;
            var next = data[(i + 1)..].IndexOf((byte)0xFF);
            i = next < 0 ? -1 : i + 1 + next;
        }
        return blocks * Math.Max(1, scans) > MaxBlockScans;
    }

    /// <inheritdoc/>
    /// <remarks>Accepts 1 (grey), 3 (RGB) or 4 (RGBA, alpha ignored) components; the quality is clamped to 30 … 98. Returns null for
    /// invalid arguments.</remarks>
    public byte[]? EncodeJpeg(int width, int height, int components, ReadOnlySpan<byte> samples, int quality)
    {
        if (components is not (1 or 3 or 4) || width <= 0 || height <= 0 || samples.Length < (long)width * height * components) return null;
        if (components == 3)
        {
            // Skia has no 3-byte RGB format: RGBA, opaque
            var rgba = new byte[width * height * 4];
            for (int i = 0, o = 0; i < width * height; i++, o += 4)
            {
                rgba[o] = samples[i * 3];
                rgba[o + 1] = samples[i * 3 + 1];
                rgba[o + 2] = samples[i * 3 + 2];
                rgba[o + 3] = 255;
            }
            return EncodeJpeg(width, height, 4, rgba, quality);
        }
        // Grey or RGBA (the decoder delivers colour JPEGs that way; alpha does not count): directly from the data, without a copy
        var info = new SKImageInfo(width, height, components == 1 ? SKColorType.Gray8 : SKColorType.Rgba8888, SKAlphaType.Opaque);
        unsafe
        {
            fixed (byte* p = samples)
            {
                using var image = SKImage.FromPixels(info, (IntPtr)p, width * components);
                using var data = image?.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 30, 98));
                return data?.ToArray();
            }
        }
    }
}

/// <summary>
/// Decoded images as <see cref="SKImage"/>, shared by all tiles and threads of a document. Each image is decoded only once. The
/// memory is accounted in a <see cref="CachePool"/> — for the renderer the pool shared by all documents; when it gets too much, the
/// images not drawn for the longest time are evicted. Thread-safe.
/// </summary>
/// <remarks>Images obtained from <see cref="Get"/> stay valid only within a <see cref="BeginDrawing"/> … Dispose bracket: evicted
/// images are disposed as soon as no drawing that began before the eviction is still running.</remarks>
public sealed class ImageCache : ICacheUser
{
    private sealed class Entry(Lazy<SKImage?> image)
    {
        public Lazy<SKImage?> Image { get; } = image;
        public long LastUse;
        public int Counted;   // 1 once the size is accounted in the pool (exactly once, even if many threads wait)
        public int Binary = -1;   // only 0 and 255 (screened scan, fax, stencil): 1, otherwise 0; -1 = not checked yet
        public SKImage? Reduced;  // area-reduced for the most recently used target size (accounted in the pool); for
                                  // inserted images the Lanczos version (SmoothFor)
        public (int Width, int Height) Pending;   // Lanczos version in progress (in the background)
        public CancellationTokenSource? Smoothing;   // cancels it (different size wanted, image evicted)
    }

    // Lanczos and sharpening are parallel themselves: one version at a time across all documents (otherwise zooming over many images
    // would start one task per image and step)
    private static readonly SemaphoreSlim SmoothingGate = new(1, 1);

    /// <summary>
    /// While set (an element is being dragged or sized): no new Lanczos version starts — every step of a drag would start one and drop it at
    /// the next, each reading and resampling the whole picture (docs/FEHLERGEDAECHTNIS.md, No. 672); the newest version is used at any
    /// size (Skia scales it), the sharp one follows when the drag ends.
    /// </summary>
    public bool Holding { get; set; }

    /// <summary>How many Lanczos versions were started (for the tests of <see cref="Holding"/>).</summary>
    internal int SmoothVersionsStarted => Volatile.Read(ref _smoothVersionsStarted);

    private int _smoothVersionsStarted;

    /// <summary>Raised when a Lanczos version is ready, so that the display redraws (until then it shows the fast version); raised on a background thread.</summary>
    public static event Action? Improved;

    /// <summary>
    /// Inserted images (see KONZEPT.md, section 5.13, recognised by <see cref="Core.Pdf.Editing.ElementAppearance.DisplayKey"/>; not
    /// images in the page content): Lanczos version for <paramref name="deviceWidth"/> × <paramref name="deviceHeight"/> pixels,
    /// sharpened as when saved — in steps of eighth octaves (so zooming does not recompute every image; Skia scales the small
    /// remainder), computed in the background. With sharpening also without reduction.
    /// </summary>
    /// <param name="source">The image source, key of the cache entry.</param>
    /// <param name="image">The image as returned by <see cref="Get"/>.</param>
    /// <param name="deviceWidth">Target width in device pixels.</param>
    /// <param name="deviceHeight">Target height in device pixels.</param>
    /// <returns>The smoothed image; null if none is (yet) available — then the caller draws <paramref name="image"/> itself.</returns>
    public SKImage? SmoothFor(ImageSource source, SKImage image, double deviceWidth, double deviceHeight)
    {
        if (Core.Pdf.Editing.ElementAppearance.DisplaySharpness(source.Stream) is not { } mark || deviceWidth < 1 || deviceHeight < 1 ||
            image.ColorType is SKColorType.Gray8 or SKColorType.Alpha8) return null;
        var sharpen = Math.Clamp(mark, 0, 1);
        var factor = Math.Min(image.Width / deviceWidth, image.Height / deviceHeight);
        if (factor < 1.2 && sharpen <= 0) return null;
        if (!_entries.TryGetValue(source, out var entry) || !ReferenceEquals(entry.Image.Value, image)) return null;
        // Eighth octaves above the target size (never smaller than needed), at most the image size
        var steps = factor >= 1.2 ? Math.Floor(Math.Log2(factor) * 8) / 8 : 0;
        var (tw, th) = (Math.Max(1, (int)Math.Round(image.Width / Math.Pow(2, steps))), Math.Max(1, (int)Math.Round(image.Height / Math.Pow(2, steps))));
        lock (entry)
        {
            var done = entry.Reduced;
            if (done is not null && done.Width == tw && done.Height == th) return done;
            if (Holding) return done;
            if (entry.Pending != (tw, th))
            {
                entry.Pending = (tw, th);
                entry.Smoothing?.Cancel();
                var cancel = (entry.Smoothing = new CancellationTokenSource()).Token;
                Interlocked.Increment(ref _smoothVersionsStarted);
                _ = Task.Run(() => BuildSmooth(source, entry, image, tw, th, sharpen, cancel));
            }
            // While computing: an older version if it is large enough
            return done is not null && done.Width >= deviceWidth && done.Height >= deviceHeight ? done : null;
        }
    }

    private async Task BuildSmooth(ImageSource source, Entry entry, SKImage image, int width, int height, double sharpen, CancellationToken cancel)
    {
        try
        {
            await SmoothingGate.WaitAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            // Like a drawing: while it runs, the image is not disposed. Still in the cache means: an eviction can only come afterwards
            // and waits for this drawing (otherwise it could be disposed in the middle of reading)
            using var drawing = BeginDrawing();
            if (!Current(source, entry)) return;
            // the inserted picture's own pixels where they are known (no copy of the decoded image), else read back from it
            var original = Core.Pdf.Editing.ElementAppearance.DisplayImage(source.Stream) is { } known && known.Width == image.Width && known.Height == image.Height
                ? known
                : null;
            if (original is null)
            {
                var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                var pixels = new byte[info.BytesSize];
                unsafe
                {
                    fixed (byte* p = pixels)
                        if (!image.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0)) return;
                }
                original = new Core.Imaging.RgbaImage(image.Width, image.Height, pixels);
            }
            var result = Core.Imaging.Lanczos.Resize(original, width, height, cancel);
            if (sharpen > 0)
                result = Core.Imaging.Sharpen.UnsharpMask(result, sharpen * Core.Pdf.Editing.ElementAppearance.SharpenAmount, 0.6, cancel: cancel);
            var smooth = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), result.Pixels);
            if (smooth is null) return;
            lock (entry)
            {
                if (entry.Pending != (width, height) || cancel.IsCancellationRequested || !Current(source, entry))
                {
                    smooth.Dispose();   // meanwhile a different size is wanted or the image was evicted
                    return;
                }
                if (entry.Reduced is { } old) ReplaceReduced(entry, old);
                entry.Reduced = smooth;
                if (Volatile.Read(ref entry.Counted) == 1)
                {
                    Interlocked.Add(ref _bytes, SizeOf(smooth));
                    _pool.Add(SizeOf(smooth));
                }
            }
            Improved?.Invoke();
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or ObjectDisposedException or InvalidOperationException
                                       or OperationCanceledException)
        {
            // Then the fast version stays
        }
        finally
        {
            SmoothingGate.Release();
        }
    }

    /// <summary>Whether <paramref name="entry"/> is still in the cache (not evicted or replaced by a new load).</summary>
    private bool Current(ImageSource source, Entry entry) => _entries.TryGetValue(source, out var current) && ReferenceEquals(current, entry);

    /// <summary>
    /// Image for drawing at <paramref name="deviceWidth"/> × <paramref name="deviceHeight"/> pixels. Pure black and white (screened
    /// scans, fax, JBIG2, stencils) in 1-byte formats is area-averaged when reduced by more than 1.5 — Skia's mipmaps (2 × 2 steps)
    /// produce moiré on screens (issue1810). One version per image for the most recently used target size (tiles of the same zoom level
    /// share it).
    /// </summary>
    /// <param name="source">The image source, key of the cache entry.</param>
    /// <param name="image">The image as returned by <see cref="Get"/>.</param>
    /// <param name="deviceWidth">Target width in device pixels.</param>
    /// <param name="deviceHeight">Target height in device pixels.</param>
    /// <returns>The reduced image; null if the caller should draw <paramref name="image"/> itself.</returns>
    public SKImage? ReducedFor(ImageSource source, SKImage image, double deviceWidth, double deviceHeight)
    {
        if (image.ColorType is not (SKColorType.Gray8 or SKColorType.Alpha8)) return null;
        if (!(deviceWidth * 1.5 < image.Width && deviceHeight * 1.5 < image.Height) || deviceWidth < 1 || deviceHeight < 1) return null;
        if (!_entries.TryGetValue(source, out var entry) || !ReferenceEquals(entry.Image.Value, image)) return null;
        // Exactly the target size: a stepped size (quarter octaves) would be resampled once more afterwards, which brings the beating
        // back for screens close to the pixel size (bpl13210, screen tiles)
        var (tw, th) = (Math.Max(1, (int)Math.Round(deviceWidth)), Math.Max(1, (int)Math.Round(deviceHeight)));
        lock (entry)
        {
            if (entry.Binary < 0) entry.Binary = IsBinary(image) ? 1 : 0;
            if (entry.Binary == 0) return null;
            if (entry.Reduced is { } done && done.Width == tw && done.Height == th) return done;
            using var pixmap = image.PeekPixels();
            if (pixmap is null) return null;
            var rowBytes = pixmap.RowBytes;
            var address = pixmap.GetPixels();
            var row = 0;
            var pixels = PdfImageDecoder.AreaReduce(image.Width, image.Height, tw, th, line => Marshal.Copy(address + row++ * rowBytes, line, 0, line.Length));
            var reduced = SKImage.FromPixelCopy(new SKImageInfo(tw, th, image.ColorType, image.AlphaType), pixels);
            if (reduced is null) return null;
            if (entry.Reduced is { } old) ReplaceReduced(entry, old);
            entry.Reduced = reduced;
            if (Volatile.Read(ref entry.Counted) == 1)
            {
                Interlocked.Add(ref _bytes, SizeOf(reduced));
                _pool.Add(SizeOf(reduced));
            }
            return reduced;
        }
    }

    private void ReplaceReduced(Entry entry, SKImage old)
    {
        if (Volatile.Read(ref entry.Counted) == 1)
        {
            Interlocked.Add(ref _bytes, -SizeOf(old));
            _pool.Add(-SizeOf(old));
        }
        lock (_retired) _retired.Enqueue((old, Interlocked.Increment(ref _ticket)));
    }

    /// <summary>
    /// Image for drawing at <paramref name="deviceWidth"/> × <paramref name="deviceHeight"/> pixels when one direction is reduced at least
    /// twice as much as the other (a strip of 40 000 × 512 pixels on a box of 750 × 187): reduced by area in that direction until both are
    /// reduced alike, the rest is left to the mipmaps. Skia picks the mipmap level by the stronger reduction, so the other direction was
    /// blurred over several pixels (large_jpeg_downscale: a sharp edge came out as a ramp of 8 pt). In eighth octaves, so that zooming does
    /// not compute a version for every step. One version per image, as for <see cref="ReducedFor"/>.
    /// </summary>
    /// <param name="source">The image source, key of the cache entry.</param>
    /// <param name="image">The image as returned by <see cref="Get"/>.</param>
    /// <param name="deviceWidth">Target width in device pixels.</param>
    /// <param name="deviceHeight">Target height in device pixels.</param>
    /// <returns>The evened image; null if the caller should draw <paramref name="image"/> itself.</returns>
    public SKImage? EvenFor(ImageSource source, SKImage image, double deviceWidth, double deviceHeight)
    {
        if (deviceWidth < 1 || deviceHeight < 1 || image.ColorType != SKColorType.Rgba8888 && image.ColorType != SKColorType.Gray8) return null;
        double fx = image.Width / deviceWidth, fy = image.Height / deviceHeight;
        var (strong, weak) = (Math.Max(fx, fy), Math.Max(1, Math.Min(fx, fy)));
        if (strong < 2 * weak) return null;
        if (!_entries.TryGetValue(source, out var entry) || !ReferenceEquals(entry.Image.Value, image)) return null;
        var steps = Math.Floor(Math.Log2(strong / weak) * 8) / 8;
        var horizontal = fx > fy;
        var (tw, th) = horizontal ? (Math.Max(1, (int)Math.Round(image.Width / Math.Pow(2, steps))), image.Height)
            : (image.Width, Math.Max(1, (int)Math.Round(image.Height / Math.Pow(2, steps))));
        lock (entry)
        {
            if (entry.Reduced is { } done && done.Width == tw && done.Height == th) return done;
            using var pixmap = image.PeekPixels();
            if (pixmap is null) return null;
            var bpp = image.ColorType == SKColorType.Gray8 ? 1 : 4;
            var pixels = AreaReduceAxis(pixmap.GetPixels(), pixmap.RowBytes, image.Width, image.Height, bpp, tw, th,
                weighted: bpp == 4 && image.AlphaType == SKAlphaType.Unpremul);
            var reduced = SKImage.FromPixelCopy(new SKImageInfo(tw, th, image.ColorType, image.AlphaType), pixels);
            if (reduced is null) return null;
            if (entry.Reduced is { } old) ReplaceReduced(entry, old);
            entry.Reduced = reduced;
            if (Volatile.Read(ref entry.Counted) == 1)
            {
                Interlocked.Add(ref _bytes, SizeOf(reduced));
                _pool.Add(SizeOf(reduced));
            }
            return reduced;
        }
    }

    /// <summary>
    /// Reduces interleaved 8-bit pixels by area along one axis (width to <paramref name="tw"/> or height to <paramref name="th"/>; the
    /// other stays): each target pixel averages the source pixels it covers, the ones at its edges by their covered part. With
    /// <paramref name="weighted"/> (RGBA without premultiplied alpha) the colours count by their opacity, so that transparent pixels do not
    /// darken their neighbours.
    /// </summary>
    internal static unsafe byte[] AreaReduceAxis(IntPtr source, int rowBytes, int width, int height, int bpp, int tw, int th, bool weighted)
    {
        var result = new byte[tw * th * bpp];
        var horizontal = tw != width;
        var (n, m) = horizontal ? (width, tw) : (height, th);
        var scale = (double)n / m;
        // the source pixels of each target pixel and their covered parts, computed once
        var first = new int[m];
        var weights = new double[m][];
        for (var t = 0; t < m; t++)
        {
            double from = t * scale, to = (t + 1) * scale;
            var k0 = (int)from;
            var k1 = Math.Min(n, (int)Math.Ceiling(to));
            first[t] = k0;
            var w = weights[t] = new double[Math.Max(0, k1 - k0)];
            for (var k = k0; k < k1; k++) w[k - k0] = Math.Max(0, Math.Min(to, k + 1) - Math.Max(from, k)) / scale;
        }
        var lines = horizontal ? height : width;
        // the lines in parallel, read straight from the pixels of the image (it stays alive and unchanged while this runs)
        Parallel.For(0, lines, line =>
        {
            var copy = (byte*)source;
            Span<double> sums = stackalloc double[4];
            for (var t = 0; t < m; t++)
            {
                sums.Clear();
                var w = weights[t];
                for (var j = 0; j < w.Length; j++)
                {
                    var k = first[t] + j;
                    var at = horizontal ? line * rowBytes + k * bpp : k * rowBytes + line * bpp;
                    var weight = w[j];
                    if (weighted)
                    {
                        var a = copy[at + 3] * weight;
                        sums[0] += a * copy[at];
                        sums[1] += a * copy[at + 1];
                        sums[2] += a * copy[at + 2];
                        sums[3] += a;
                    }
                    else
                        for (var c = 0; c < bpp; c++) sums[c] += weight * copy[at + c];
                }
                var target = horizontal ? (line * tw + t) * bpp : (t * tw + line) * bpp;
                if (weighted)
                {
                    var alpha = sums[3];
                    for (var c = 0; c < 3; c++) result[target + c] = (byte)Math.Clamp(Math.Round(alpha > 0 ? sums[c] / alpha : 0), 0, 255);
                    result[target + 3] = (byte)Math.Clamp(Math.Round(alpha), 0, 255);
                }
                else
                    for (var c = 0; c < bpp; c++) result[target + c] = (byte)Math.Clamp(Math.Round(sums[c]), 0, 255);
            }
        });
        return result;
    }

    private static bool IsBinary(SKImage image)
    {
        using var pixmap = image.PeekPixels();
        if (pixmap is null) return false;
        var span = pixmap.GetPixelSpan();
        var rowBytes = pixmap.RowBytes;
        for (var y = 0; y < image.Height; y++)
            if (span.Slice(y * rowBytes, image.Width).IndexOfAnyExcept((byte)0, (byte)255) >= 0) return false;
        return true;
    }

    // Key by value (ImageSource is a record): the same image, drawn at several places, on several pages or in a newly built
    // display list, is decoded only once — provided stream and colour spaces of the resources are the same objects (SameImage).
    // Stencils in different colours share the image (the colour is added only when drawing)
    private readonly ConcurrentDictionary<ImageSource, Entry> _entries = new(SameImage.Instance);
    private readonly CachePool _pool;
    private long _clock;
    private long _bytes;
    private long _lastActive;

    // Dispose evicted images as soon as no drawing that began before the eviction is still running (it might be painting the image).
    // Otherwise they would stay pinned until the finaliser runs: pool 64 MB, process nevertheless over 500 MB
    private readonly ConcurrentDictionary<long, byte> _drawing = new();
    private readonly Queue<(SKImage Image, long Since)> _retired = new();
    private long _ticket;

    /// <summary>A drawing with images from this cache (call <see cref="Get"/> only inside); dispose it at the end.</summary>
    public readonly struct Drawing(ImageCache cache, long ticket) : IDisposable
    {
        /// <summary>Ends the drawing and disposes evicted images no longer in use.</summary>
        public void Dispose() => cache.EndDrawing(ticket);
    }

    /// <summary>Starts a drawing; images evicted from now on are not disposed before it ends.</summary>
    /// <returns>The drawing; dispose it when done.</returns>
    public Drawing BeginDrawing()
    {
        var ticket = Interlocked.Increment(ref _ticket);
        _drawing[ticket] = 0;
        return new Drawing(this, ticket);
    }

    private void EndDrawing(long ticket)
    {
        _drawing.TryRemove(ticket, out _);
        DisposeRetired();
    }

    private void Retire(Entry entry)
    {
        if (!entry.Image.IsValueCreated || entry.Image.Value is not { } image) return;
        lock (_retired)
        {
            _retired.Enqueue((image, Interlocked.Increment(ref _ticket)));
            lock (entry)
            {
                entry.Smoothing?.Cancel();
                if (entry.Reduced is { } reduced)
                {
                    _retired.Enqueue((reduced, Interlocked.Increment(ref _ticket)));
                    entry.Reduced = null;
                }
            }
        }
    }

    private void DisposeRetired()
    {
        lock (_retired)
        {
            if (_retired.Count == 0) return;
            var oldest = long.MaxValue;
            foreach (var ticket in _drawing.Keys) oldest = Math.Min(oldest, ticket);
            while (_retired.TryPeek(out var retired) && retired.Since < oldest)
            {
                _retired.Dequeue();
                retired.Image.Dispose();
            }
        }
    }

    /// <summary>Creates a cache with its own pool (command line, tests).</summary>
    /// <param name="budgetBytes">Limit of the own pool in bytes.</param>
    public ImageCache(long budgetBytes = 512L << 20) : this(new CachePool("Bilder", budgetBytes)) { }

    /// <summary>Creates a cache in the given pool, e.g. <see cref="CachePool.Images"/>, shared by all documents.</summary>
    /// <param name="pool">The pool; the cache registers itself (see <see cref="Detach"/>).</param>
    public ImageCache(CachePool pool)
    {
        _pool = pool;
        pool.Register(this);
    }

    /// <inheritdoc/>
    public long LastActive => Interlocked.Read(ref _lastActive);

    /// <summary>Bytes occupied by the decoded images (including reduced versions).</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Pixels decoded so far, every decoding counted (an image decoded again after it was evicted counts again): the work the
    /// images cost, for budgets of work instead of time.</summary>
    public long DecodedPixels => Interlocked.Read(ref _decodedPixels);

    private long _decodedPixels;

    /// <summary>Returns the decoded image, decoding it on first use; may evict other images.</summary>
    /// <param name="source">The image source from the display list.</param>
    /// <param name="doc">The document the image belongs to.</param>
    /// <returns>The image, or null if it cannot be decoded (broken images are remembered as null).</returns>
    public SKImage? Get(ImageSource source, PdfDocument doc)
    {
        if (!_entries.TryGetValue(source, out var entry))
            entry = _entries.GetOrAdd(source, s => new Entry(new Lazy<SKImage?>(() => Load(s, doc), LazyThreadSafetyMode.ExecutionAndPublication)));
        entry.LastUse = Interlocked.Increment(ref _clock);
        Interlocked.Exchange(ref _lastActive, System.Diagnostics.Stopwatch.GetTimestamp());
        var image = entry.Image.Value;
        if (image is not null && Interlocked.CompareExchange(ref entry.Counted, 1, 0) == 0)
        {
            Interlocked.Add(ref _decodedPixels, (long)image.Width * image.Height);
            var size = SizeOf(image);
            Interlocked.Add(ref _bytes, size);
            _pool.Add(size);
            _pool.Enforce(this);
        }
        return image;
    }

    /// <summary>Memory of an image (1 or 4 bytes per pixel).</summary>
    private static long SizeOf(SKImage image) => (long)image.Info.RowBytes * image.Height;

    private static SKImage? Load(ImageSource source, PdfDocument doc)
    {
        DecodedImage? decoded;
        try
        {
            decoded = PdfImageDecoder.Decode(source, doc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Lazy remembers an exception and rethrows it on every drawing: broken image = no image
            DocumentRenderer.Broken("Bild nicht lesbar", ex);
            return null;
        }
        if (decoded is null) return null;
        // Grey and coverage (stencils) with 1 byte per pixel; Skia paints a stencil in the colour of the paint
        var info = decoded.Format switch
        {
            ImagePixels.Gray => new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Gray8, SKAlphaType.Opaque),
            ImagePixels.Alpha => new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Alpha8, SKAlphaType.Premul),
            _ => new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul),
        };
        // Copy small images; hand large ones (from 85 KB, in the large object heap anyway) to Skia pinned, without a copy —
        // released when Skia lets go of the image
        if (decoded.Pixels.Length < 85_000) return SKImage.FromPixelCopy(info, decoded.Pixels);
        var handle = GCHandle.Alloc(decoded.Pixels, GCHandleType.Pinned);
        var image = SKImage.FromPixels(new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes),
            static (_, context) => ((GCHandle)context!).Free(), handle);
        if (image is null) handle.Free();
        return image;
    }

    /// <summary>Evicts the images not drawn for the longest time until about <paramref name="bytes"/> are free.</summary>
    /// <param name="pool">The requesting pool; requests from other pools are ignored.</param>
    /// <param name="bytes">Number of bytes to release.</param>
    public void Free(CachePool pool, long bytes)
    {
        if (!ReferenceEquals(pool, _pool) || bytes <= 0) return;
        long freed = 0;
        // The image just requested stays, even if it alone is larger than the budget: evicted at once, every drawing decoded it anew
        // (a soft mask of 150 million pixels, three seconds per tile; docs/FEHLERGEDAECHTNIS.md, No. 409). It goes when the next image
        // needs the room.
        var newest = Interlocked.Read(ref _clock);
        // Snapshot (ToArray locks): OrderBy directly on the dictionary allocates an array by Count and copies afterwards — if another
        // thread removes something in between, empty entries remain (NullReference with parallel tiles)
        foreach (var (key, entry) in _entries.ToArray().OrderBy(e => e.Value.LastUse))
        {
            if (freed >= bytes) break;
            if (entry.LastUse == newest) continue;
            if (!_entries.TryRemove(key, out _)) continue;
            freed += Uncount(entry);
            Retire(entry);
        }
        DisposeRetired();
    }

    private long Uncount(Entry entry)
    {
        if (!entry.Image.IsValueCreated || entry.Image.Value is not { } img || Interlocked.Exchange(ref entry.Counted, 0) != 1) return 0;
        long size;
        lock (entry) size = SizeOf(img) + (entry.Reduced is { } reduced ? SizeOf(reduced) : 0);
        Interlocked.Add(ref _bytes, -size);
        _pool.Add(-size);
        return size;
    }

    /// <summary>Evicts all images (they are disposed once no drawing uses them any more).</summary>
    public void Clear()
    {
        foreach (var key in _entries.Keys)
            if (_entries.TryRemove(key, out var entry))
            {
                Uncount(entry);
                Retire(entry);
            }
        DisposeRetired();
    }

    /// <summary>Releases everything and leaves the pool (document closed).</summary>
    public void Detach()
    {
        Clear();
        _pool.Unregister(this);
    }
}

/// <summary>
/// Same image regardless of the stencil colour (the renderer adds it when drawing). Of the resources only the colour space
/// dictionary counts (named colour spaces, /DefaultRGB …); the decoder reads nothing else: otherwise a logo on every page with its
/// own page resources would be decoded per page (40 pages: 458 MB instead of 11 MB).
/// </summary>
internal sealed class SameImage : IEqualityComparer<ImageSource>
{
    public static readonly SameImage Instance = new();

    public bool Equals(ImageSource? a, ImageSource? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && ReferenceEquals(a.Stream, b.Stream) && Equals(a.Inline, b.Inline) &&
                                  ReferenceEquals(Spaces(a), Spaces(b)) && a.IsStencil == b.IsStencil && a.Intent == b.Intent && a.Bpc == b.Bpc
                                  && ReferenceEquals(a.DeviceCmyk, b.DeviceCmyk));

    public int GetHashCode(ImageSource s) => HashCode.Combine(s.Stream, s.Inline, Spaces(s), s.IsStencil, s.Intent, s.Bpc);

    private static readonly PdfName ColorSpace = PdfName.Get("ColorSpace");

    private static PdfDictionary? Spaces(ImageSource s) => s.Resources?.GetDictionary(ColorSpace);
}
