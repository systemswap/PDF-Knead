using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Rendering;
using PdfMatrix = PdfKnead.Core.Pdf.Graphics.Matrix;

namespace PdfKnead.Controls;

/// <summary>
/// Elements of a page with the mapping from page space to control space — cut at <paramref name="Clip"/> (the visible area of the page in
/// control space: readers show nothing beyond it; docs/FEHLERGEDAECHTNIS.md, No. 622), or with <paramref name="Outside"/> only beyond it, pale
/// (<paramref name="Opacity"/>: what a dragged element has beyond the edge).
/// </summary>
internal readonly record struct ElementLayer(PdfPage Page, IReadOnlyList<PageElement> Elements, PdfMatrix ToControl, Rect? Clip = null, bool Outside = false,
    double Opacity = 1);

/// <summary>
/// Draws overlaid elements directly onto the user interface's Skia canvas, as vectors that are sharp at
/// every zoom level, without touching the page tiles underneath.
/// </summary>
internal sealed class ElementDrawing(Rect bounds, IReadOnlyList<ElementLayer> layers, ImageCache? images = null,
    Func<PdfPage, ElementAppearance, Core.Pdf.Graphics.DisplayList?>? foreignLists = null, bool overprintPreview = false) : ICustomDrawOperation
{
    public Rect Bounds => bounds;

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Dispose()
    {
    }

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        canvas.Save();
        canvas.ClipRect(new SkiaSharp.SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom));
        // At most ElementOverlay.FrameTime per frame across all pages (guards against crafted files).
        var until = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(ElementOverlay.FrameTime.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        foreach (var layer in layers)
        {
            canvas.Save();
            if (layer.Clip is { } clip)
                canvas.ClipRect(new SkiaSharp.SKRect((float)clip.Left, (float)clip.Top, (float)clip.Right, (float)clip.Bottom),
                    layer.Outside ? SkiaSharp.SKClipOperation.Difference : SkiaSharp.SKClipOperation.Intersect, antialias: true);
            if (layer.Opacity < 1)
            {
                using var pale = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(layer.Opacity, 0, 1) * 255)) };
                canvas.SaveLayer(pale);
            }
            ElementOverlay.Draw(canvas, layer.Page, layer.Elements, layer.ToControl, images, foreignLists, until, overprintPreview);
            canvas.Restore();
            if (layer.Opacity < 1) canvas.Restore();
        }
        canvas.Restore();
    }
}
