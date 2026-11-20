using System.Runtime.CompilerServices;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Ink density as an image (ballpoint texture): <see cref="Levels"/> per pixel, <see cref="Scale"/> pixels per unit of the
/// content (em of the handwriting font, unit of the drawn points, pixel of the colour glyph images), plus a margin of <see cref="Pad"/>
/// pixels all round.
/// </summary>
/// <param name="width">Width in pixels.</param>
/// <param name="height">Height in pixels.</param>
/// <param name="levels">Density level per pixel, row by row from the top.</param>
/// <param name="scale">Pixels per unit of the content.</param>
/// <param name="pad">Margin in pixels.</param>
public sealed class InkTexture(int width, int height, byte[] levels, double scale, double pad)
{
    /// <summary>Width in pixels.</summary>
    public int Width { get; } = width;

    /// <summary>Height in pixels.</summary>
    public int Height { get; } = height;

    /// <summary>Level per pixel (0 to <see cref="PenTexture.Levels"/> − 1), row by row from the top.</summary>
    public byte[] Levels { get; } = levels;

    /// <summary>Pixels per unit of the content.</summary>
    public double Scale { get; } = scale;

    /// <summary>Margin around the content in pixels.</summary>
    public double Pad { get; } = pad;
}

/// <summary>
/// Ballpoint texture of signatures, final only once content and size are settled: until then the element shows the smooth
/// form (or the texture of another size, scaled accordingly). A background worker computes the full resolution
/// (<see cref="Dpi"/>), the newest request first, and after a size change only once the size stays still. Saving waits for it
/// (<see cref="ElementAppearance.Final"/>). The pattern depends only on the content (name and font, strokes) and looks the
/// same at every resolution; colour and size do not change it, and shrinking costs nothing.
/// </summary>
/// <remarks>Thread-safe; finished textures are kept within a budget of 48 MB and evicted least recently used first.</remarks>
public static class InkTextures
{
    /// <summary>Resolution in dpi used for saving.</summary>
    public const double Dpi = 600;

    /// <summary>Largest image in pixels: very large signatures get somewhat less than <see cref="Dpi"/>.</summary>
    public const long MaxPixels = 4L << 20;

    // Delay before computing: short for new content (typing in the signature window), longer after a size change
    private const int NewMilliseconds = 40;
    private const int SettleMilliseconds = 250;

    // At most this many requests wait; older ones (intermediate states while typing) are dropped
    private const int MaxWaiting = 48;

    // Finished textures take at most this many bytes (one signature at 600 dpi: about 1 MB)
    private const long Budget = 48L << 20;

    /// <summary>Margin around the content in pixels: enough for the rasteriser's anti-aliasing.</summary>
    private const double Pad = 3;

    /// <summary>A (sharper) texture is ready; raised on the background thread, so that the display redraws.</summary>
    public static event Action? Improved;

    /// <summary>What is drawn: bounds in units (y downwards) and how the form gets into a raster.</summary>
    internal sealed class Source(string key, RectD bounds, uint seed, Action<CoverageRaster, double, double> draw)
    {
        public string Key { get; } = key;

        /// <summary>Bounds of the content, y downwards (Top = top).</summary>
        public RectD Bounds { get; } = bounds;

        public uint Seed { get; } = seed;

        /// <summary>Draws the form into the raster; arguments are scale (pixels per unit) and margin (pixels).</summary>
        public Action<CoverageRaster, double, double> Draw { get; } = draw;

        /// <summary>
        /// Instead of <see cref="Draw"/>: the coverage (0 to 1 per pixel) for width, height, scale and margin, for a form that comes as an
        /// image (colour glyph images of a font) rather than as outlines.
        /// </summary>
        public Func<int, int, double, double, float[]>? Coverage { get; init; }

        /// <summary>
        /// Scale, at most so large that the image (with margin) does not exceed <see cref="MaxPixels"/> — solved exactly, so
        /// that limiting twice gives the same result (otherwise the texture would forever count as too coarse).
        /// </summary>
        public double Limit(double scale)
        {
            double w = Bounds.Width, h = Bounds.Height, p = 2 * Pad + 1;   // + 1: rounding up to whole pixels
            if ((w * scale + p) * (h * scale + p) <= MaxPixels) return scale;
            // (w·s + p)(h·s + p) = Max  →  w·h·s² + p(w + h)·s + p² − Max = 0
            double a = w * h, b = p * (w + h), c = p * p - MaxPixels;
            return a <= 0 ? scale : Math.Min(scale, (-b + Math.Sqrt(b * b - 4 * a * c)) / (2 * a));
        }
    }

    private sealed class Entry(Source source)
    {
        public Source Source { get; } = source;

        public InkTexture? Best;

        /// <summary>Requested scale while the request waits (0: none).</summary>
        public double Wanted;

        /// <summary>Earliest time (<see cref="Environment.TickCount64"/>) for the request.</summary>
        public long Due;

        /// <summary>Order of the requests: the newest first.</summary>
        public long Order;

        /// <summary>Last use (for eviction).</summary>
        public long Used;
    }

    private static readonly Dictionary<string, Entry> Entries = [];
    private static readonly List<Entry> Waiting = [];
    private static long _bytes;
    private static long _clock;
    private static bool _working;

    /// <summary>
    /// Texture for <paramref name="scale"/> pixels per unit, if already finished (otherwise null or another resolution); what is
    /// missing is computed by the background worker (<see cref="Improved"/>).
    /// </summary>
    internal static InkTexture? Get(Source source, double scale)
    {
        scale = source.Limit(scale);
        lock (Entries)
        {
            var entry = Lookup(source);
            if (entry.Best is { } known && known.Scale >= scale * 0.999) return known;
            Request(entry, scale, entry.Best is null ? NewMilliseconds : SettleMilliseconds);
            return entry.Best;
        }
    }

    /// <summary>Texture at full resolution (for saving): computed now on the calling thread if it is missing.</summary>
    internal static InkTexture Final(Source source, double scale)
    {
        scale = source.Limit(scale);
        Entry entry;
        lock (Entries)
        {
            entry = Lookup(source);
            if (entry.Best is { } known && known.Scale >= scale * 0.999) return known;
        }
        var made = Make(source, scale, CancellationToken.None);
        lock (Entries)
        {
            Store(entry, made);
            return entry.Best!;
        }
    }

    /// <summary>
    /// Releases the cached textures (when the signature window is closed, its previews are no longer needed). What elements
    /// currently show is kept alive through their appearance; only another size is then computed again.
    /// </summary>
    public static void Release()
    {
        lock (Entries)
        {
            Entries.Clear();
            Waiting.Clear();
            _bytes = 0;
        }
    }

    /// <summary>The best finished texture (null: none yet), to check whether an appearance is outdated.</summary>
    internal static InkTexture? Best(string key)
    {
        lock (Entries) return Entries.TryGetValue(key, out var entry) ? entry.Best : null;
    }

    private static Entry Lookup(Source source)
    {
        if (!Entries.TryGetValue(source.Key, out var entry)) Entries[source.Key] = entry = new Entry(source);
        entry.Used = ++_clock;
        return entry;
    }

    // Under the lock: keep the finished texture (a coarser one never replaces a finer one), then enforce the budget
    private static void Store(Entry entry, InkTexture texture)
    {
        if (entry.Best is { } old && old.Scale >= texture.Scale) return;
        _bytes += texture.Levels.Length - (entry.Best?.Levels.Length ?? 0);
        entry.Best = texture;
        if (entry.Wanted <= texture.Scale * 1.001)
        {
            entry.Wanted = 0;
            Waiting.Remove(entry);
        }
        // Evict the least recently used first (never the one just stored)
        while (_bytes > Budget)
        {
            Entry? oldest = null;
            foreach (var e in Entries.Values)
                if (e != entry && e.Best is not null && (oldest is null || e.Used < oldest.Used)) oldest = e;
            if (oldest is null) break;
            _bytes -= oldest.Best!.Levels.Length;
            Waiting.Remove(oldest);
            Entries.Remove(oldest.Source.Key);
        }
    }

    // Under the lock: register a request; a new one for the same content replaces the old one including its delay
    private static void Request(Entry entry, double scale, int delay)
    {
        var waiting = entry.Wanted > 0;
        if (waiting && Math.Abs(entry.Wanted - scale) <= scale * 0.001) return;
        entry.Wanted = scale;
        entry.Due = Environment.TickCount64 + delay;
        entry.Order = ++_clock;
        if (!waiting) Waiting.Add(entry);
        while (Waiting.Count > MaxWaiting)
        {
            var oldest = Waiting.MinBy(e => e.Order)!;
            oldest.Wanted = 0;
            Waiting.Remove(oldest);
        }
        if (_working) return;
        _working = true;
        _ = Task.Run(Work);
    }

    // The background worker: always the newest due request; when nothing is left, it ends
    private static async Task Work()
    {
        while (true)
        {
            Entry? next = null;
            var scale = 0.0;
            var wait = 0L;
            lock (Entries)
            {
                if (Waiting.Count == 0)
                {
                    _working = false;
                    return;
                }
                var now = Environment.TickCount64;
                next = Waiting.Where(e => e.Due <= now).MaxBy(e => e.Order);
                if (next is null) wait = Waiting.Min(e => e.Due) - now;
                else
                {
                    scale = next.Wanted;
                    next.Wanted = 0;
                    Waiting.Remove(next);
                }
            }
            if (next is null)
            {
                await Task.Delay((int)Math.Clamp(wait, 1, SettleMilliseconds)).ConfigureAwait(false);
                continue;
            }
            try
            {
                var made = Make(next.Source, scale, CancellationToken.None, Math.Max(1, Environment.ProcessorCount / 2));
                lock (Entries)
                {
                    if (Entries.TryGetValue(next.Source.Key, out var current) && current == next) Store(next, made);
                }
                Improved?.Invoke();
            }
            catch (Exception ex)
            {
                // The smooth form stays; saving computes again (and reports an error there)
                Platform.AppLog.Write("Kuli-Struktur", ex.ToString());
            }
        }
    }

    private static InkTexture Make(Source source, double scale, CancellationToken cancel, int threads = -1)
    {
        var pad = Pad;
        var width = Math.Max(1, (int)Math.Ceiling(source.Bounds.Width * scale + 2 * pad));
        var height = Math.Max(1, (int)Math.Ceiling(source.Bounds.Height * scale + 2 * pad));
        float[] coverage;
        if (source.Coverage is { } fill) coverage = fill(width, height, scale, pad);
        else
        {
            var raster = new CoverageRaster(width, height);
            source.Draw(raster, scale, pad);
            coverage = raster.Coverage();
        }
        cancel.ThrowIfCancellationRequested();
        // Grain at most 1/170 of the height (0.08 mm for a signature 1.4 cm high): wide strokes stay finely grained
        var maxGrain = source.Bounds.Height * scale / 170;
        var levels = PenTexture.Generate(coverage, width, height, maxGrain, source.Seed, cancel, threads);
        return new InkTexture(width, height, levels, scale, pad);
    }

    // ------------------------------------------------------------ Sources

    /// <summary>Handwriting signature: the outlines of the laid-out name.</summary>
    internal static Source? For(SignatureElement element, List<(PathVerb[] Verbs, double[] Points)> glyphs, RectD bounds)
    {
        if (glyphs.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0) return null;
        var key = "S\n" + element.FontFile + "\n" + element.Name.Trim();
        // Outlines: y upwards (em); downwards in the raster
        var flipped = RectD.FromLtrb(bounds.Left, -bounds.Bottom, bounds.Right, -bounds.Top);
        return new Source(key, flipped, PenTexture.Seed(key), (raster, scale, pad) =>
        {
            foreach (var (verbs, points) in glyphs)
            {
                (double, double) P(int i) => ((points[i] - bounds.Left) * scale + pad, (bounds.Bottom - points[i + 1]) * scale + pad);
                var p = 0;
                foreach (var verb in verbs)
                {
                    switch (verb)
                    {
                        case PathVerb.Move:
                            var (mx, my) = P(p);
                            raster.MoveTo(mx, my);
                            p += 2;
                            break;
                        case PathVerb.Line:
                            var (lx, ly) = P(p);
                            raster.LineTo(lx, ly);
                            p += 2;
                            break;
                        case PathVerb.Cubic:
                            var (ax, ay) = P(p);
                            var (bx, by) = P(p + 2);
                            var (cx, cy) = P(p + 4);
                            raster.CubicTo(ax, ay, bx, by, cx, cy);
                            p += 6;
                            break;
                        case PathVerb.Close:
                            raster.Close();
                            break;
                    }
                }
                raster.Close();
            }
        });
    }

    /// <summary>Density from which a pixel of a colour glyph image counts as fully covered: light upstrokes still belong to the form.</summary>
    private const double FullFromDensity = 0.2;

    /// <summary>
    /// Handwriting signature in a font with colour glyph images: the form of the ink in the composed image (<paramref name="picture"/>,
    /// as density), so that the ballpoint texture also works for fonts whose outlines are only placeholders. Bounds and placement are
    /// those of the image, as without texture.
    /// </summary>
    internal static Source? For(SignatureElement element, InkTexture picture)
    {
        if (picture.Width <= 1 || picture.Height <= 1) return null;
        var key = "B\n" + element.FontFile + "\n" + element.Name.Trim();
        var (w0, h0, levels) = (picture.Width, picture.Height, picture.Levels);
        var full = FullFromDensity * (PenTexture.Levels - 1);
        return new Source(key, new RectD(0, 0, w0, h0), PenTexture.Seed(key), static (_, _, _) => { })
        {
            Coverage = (width, height, scale, pad) =>
            {
                // Bilinear from the image pixels (units) into the texture raster
                var coverage = new float[width * height];
                for (var y = 0; y < height; y++)
                {
                    var sy = Math.Clamp((y + 0.5 - pad) / scale - 0.5, 0, h0 - 1.001);
                    var y1 = (int)sy;
                    var fy = sy - y1;
                    for (var x = 0; x < width; x++)
                    {
                        var sx = Math.Clamp((x + 0.5 - pad) / scale - 0.5, 0, w0 - 1.001);
                        var x1 = (int)sx;
                        var fx = sx - x1;
                        var at = y1 * w0 + x1;
                        var v = (levels[at] * (1 - fx) + levels[at + 1] * fx) * (1 - fy) + (levels[at + w0] * (1 - fx) + levels[at + w0 + 1] * fx) * fy;
                        coverage[y * width + x] = (float)Math.Min(1, v / full);
                    }
                }
                return coverage;
            },
        };
    }

    private static readonly ConditionalWeakTable<IReadOnlyList<float[]>, string> StrokeKeys = new();
    private static long _strokeKeys;

    /// <summary>Drawn signature: the strokes, smoothed as in the vector form, with round caps.</summary>
    internal static Source? For(InkElement ink)
    {
        var bounds = ElementAppearance.InkBounds(ink);
        if (bounds.IsEmpty) return null;
        var key = "I\n" + StrokeKeys.GetValue(ink.Strokes, _ => Interlocked.Increment(ref _strokeKeys).ToString(System.Globalization.CultureInfo.InvariantCulture))
            + "\n" + ink.StrokeWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var seed = 2166136261u;
        foreach (var stroke in ink.Strokes)
            foreach (var v in stroke) seed = (seed ^ (uint)BitConverter.SingleToInt32Bits(v)) * 16777619u;
        var strokes = ink.Strokes;
        var half = ink.StrokeWidth / 2;
        return new Source(key, bounds, seed, (raster, scale, pad) =>
        {
            var r = half * scale;
            foreach (var line in ElementAppearance.InkPolylines(strokes, 0.25 / scale))
            {
                (double X, double Y) P(int i) => ((line[i] - bounds.Left) * scale + pad, (line[i + 1] - bounds.Top) * scale + pad);
                for (var i = 0; i + 1 < line.Length; i += 2)
                {
                    var a = P(i);
                    raster.Circle(a.X, a.Y, r);
                    if (i + 3 >= line.Length) continue;
                    var b = P(i + 2);
                    double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                    if (len <= 1e-9) continue;
                    double nx = -dy / len * r, ny = dx / len * r;
                    raster.Polygon([(a.X + nx, a.Y + ny), (b.X + nx, b.Y + ny), (b.X - nx, b.Y - ny), (a.X - nx, a.Y - ny)]);
                }
            }
        });
    }
}
