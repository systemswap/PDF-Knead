using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Pdf.Graphics;
using SkiaSharp;
using Matrix = PdfKnead.Core.Pdf.Graphics.Matrix;

namespace PdfKnead.Rendering;

/// <summary>
/// Pre-rasterised glyphs (see KONZEPT.md, section 3.4): the same glyph occurs hundreds of times on a text page, in the same size and
/// orientation — only translated. Instead of rasterising the outline with anti-aliasing every time (about 4 µs), it is drawn once
/// per size and quarter-pixel offset as an alpha mask and afterwards only copied, tinted with the fill colour. All common text renderers do
/// it this way.
/// </summary>
/// <remarks>
/// One set of masks per display list: the path number is valid only within one list. Tiles of the same page share the masks (also
/// in parallel). Large glyphs (over <see cref="MaxSize"/> pixels) stay paths — they are rare and expensive as masks.
/// </remarks>
internal sealed class GlyphMasks : IDisposable
{
    /// <summary>Largest edge length of a mask in pixels.</summary>
    public const int MaxSize = 96;

    /// <summary>Maximum bytes of masks per list; beyond that, glyphs are drawn as paths again.</summary>
    private const long Budget = 16L << 20;

    private static readonly ConditionalWeakTable<DisplayList, GlyphMasks> All = new();

    private readonly ConcurrentDictionary<Key, Mask?> _masks = new();
    private long _bytes;
    private bool _disposed;

    /// <summary>Key: path, size and orientation (2×2 part of the matrix) and the quarter-pixel offset.</summary>
    private readonly record struct Key(int Path, float A, float B, float C, float D, byte X, byte Y);

    private sealed record Mask(SKImage Image, int Left, int Top);

    public static GlyphMasks For(DisplayList list) => All.GetValue(list, _ => new GlyphMasks());

    /// <summary>Releases the masks of a list (the list is dropped from the cache).</summary>
    public static void Release(DisplayList list)
    {
        if (All.TryGetValue(list, out var masks))
        {
            All.Remove(list);
            masks.Dispose();
        }
    }

    /// <summary>
    /// Draws the glyph with the matrix <paramref name="glyphToDevice"/> as a mask. The canvas must have the identity matrix (device
    /// pixels). Returns false if the caller must draw it as a path.
    /// </summary>
    public bool TryDraw(SKCanvas canvas, DisplayList list, int path, in Matrix glyphToDevice, SKPaint paint, SKPath scratch)
    {
        // Position in quarter pixels: the integer part only translates, the remainder belongs to the mask
        var qx = Math.Floor(glyphToDevice.E * 4);
        var qy = Math.Floor(glyphToDevice.F * 4);
        if (!(Math.Abs(qx) < 1e9 && Math.Abs(qy) < 1e9)) return false;   // also NaN
        var ix = (long)qx;
        var iy = (long)qy;
        var key = new Key(path, (float)glyphToDevice.A, (float)glyphToDevice.B, (float)glyphToDevice.C, (float)glyphToDevice.D,
            (byte)(ix & 3), (byte)(iy & 3));
        if (!_masks.TryGetValue(key, out var mask)) mask = Create(key, list, scratch);
        if (mask is null) return false;
        canvas.DrawImage(mask.Image, (ix >> 2) + mask.Left, (iy >> 2) + mask.Top, paint);
        return true;
    }

    private Mask? Create(Key key, DisplayList list, SKPath scratch)
    {
        if (_disposed || Interlocked.Read(ref _bytes) > Budget) return null;   // full: do not remember, draw as path
        scratch.Rewind();
        SkiaRenderer.AppendPath(list, key.Path, scratch);
        scratch.FillType = SKPathFillType.Winding;
        scratch.Transform(new SKMatrix(key.A, key.C, key.X / 4f, key.B, key.D, key.Y / 4f, 0, 0, 1));
        var bounds = scratch.Bounds;
        Mask? mask = null;
        if (bounds.Width <= MaxSize && bounds.Height <= MaxSize && float.IsFinite(bounds.Left) && float.IsFinite(bounds.Top))
        {
            // One pixel of margin for anti-aliasing
            var left = (int)Math.Floor(bounds.Left) - 1;
            var top = (int)Math.Floor(bounds.Top) - 1;
            var width = (int)Math.Ceiling(bounds.Right) - left + 1;
            var height = (int)Math.Ceiling(bounds.Bottom) - top + 1;
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint { IsAntialias = true, Color = SKColors.White })
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(-left, -top);
                canvas.DrawPath(scratch, paint);
            }
            mask = new Mask(SKImage.FromBitmap(bitmap), left, top);
            Interlocked.Add(ref _bytes, width * height + 256L);
        }
        // Too large: remember that as well (as null) so that it is not measured again every time
        if (_masks.TryAdd(key, mask)) return mask;
        mask?.Image.Dispose();   // another thread was faster
        return _masks.TryGetValue(key, out var existing) ? existing : null;
    }

    public void Dispose()
    {
        _disposed = true;
        // Only after a grace period: a tile might still be drawing with these masks
        var masks = _masks.Values.ToList();
        _masks.Clear();
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
        {
            foreach (var mask in masks) mask?.Image.Dispose();
        }, TaskScheduler.Default);
    }
}
