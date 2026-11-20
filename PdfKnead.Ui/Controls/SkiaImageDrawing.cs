using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace PdfKnead.Controls;

/// <summary>A Skia image (tile) with source and destination rectangle (destination in user interface units).</summary>
public readonly record struct SkiaImageDraw(SKImage Image, SKRect Source, SKRect Destination);

/// <summary>
/// Draws finished tiles directly onto Avalonia's Skia canvas, on the graphics card if Avalonia
/// uses it: the tiles are uploaded there as textures and, when panning and zooming, are only
/// moved and scaled.
/// </summary>
public sealed class SkiaImageDrawing(Rect bounds, IReadOnlyList<SkiaImageDraw> draws) : ICustomDrawOperation
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

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
        canvas.ClipRect(new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom));
        using var paint = new SKPaint { IsAntialias = false };
        foreach (var d in draws)
            canvas.DrawImage(d.Image, d.Source, d.Destination, Sampling, paint);
        canvas.Restore();
    }
}
