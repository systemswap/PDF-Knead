using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PdfKnead.Controls;

/// <summary>
/// A grid of thin lines over an area (turning a scanned page by hand: lines and edges of the page are aligned to it). The grid stays fixed
/// while the page beneath turns; it does not take the mouse.
/// </summary>
public sealed class GridOverlay : Control
{
    /// <summary>The distance of the lines in device-independent pixels.</summary>
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<GridOverlay, double>(nameof(Spacing), 32);

    static GridOverlay() => AffectsRender<GridOverlay>(SpacingProperty);

    public GridOverlay() => IsHitTestVisible = false;

    /// <summary>The distance of the lines.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        var step = Math.Max(8, Spacing);
        // dark and light on top of each other: visible on white paper and on dark print alike
        var dark = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0x20, 0x40, 0xA0)), 1);
        var light = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1);
        for (var x = step / 2; x < w; x += step)
        {
            context.DrawLine(light, new Point(x + 1, 0), new Point(x + 1, h));
            context.DrawLine(dark, new Point(x, 0), new Point(x, h));
        }
        for (var y = step / 2; y < h; y += step)
        {
            context.DrawLine(light, new Point(0, y + 1), new Point(w, y + 1));
            context.DrawLine(dark, new Point(0, y), new Point(w, y));
        }
    }
}
