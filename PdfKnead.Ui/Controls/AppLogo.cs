using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PdfKnead.Controls;

/// <summary>
/// Program logo: a sheet with a dog-ear and a magnifying glass on a square in the accent colour.
/// Drawn in code (48 × 48 grid) so that it stays sharp at any size; the same drawing also
/// produces Icon.png and app.ico (test <c>AppIconTests</c>).
/// </summary>
public sealed class AppLogo : Control
{
    private static readonly Geometry Page = Geometry.Parse(
        "M15,9 H28 L35,16 V37 A2,2 0 0 1 33,39 H15 A2,2 0 0 1 13,37 V11 A2,2 0 0 1 15,9 Z");

    private static readonly Geometry Fold = Geometry.Parse("M28,9 V14 A2,2 0 0 0 30,16 H35 Z");

    private static readonly Geometry Lines = Geometry.Parse("M18,21 H27 M18,25 H25 M18,29 H23");

    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<AppLogo, Color>(nameof(Accent), Color.Parse("#4F46E5"));

    static AppLogo() => AffectsRender<AppLogo>(AccentProperty);

    public Color Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var scale = size / 48.0;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            var accent = new SolidColorBrush(Accent);
            var dark = new SolidColorBrush(Color.FromRgb(
                (byte)(Accent.R * 0.75), (byte)(Accent.G * 0.75), (byte)(Accent.B * 0.75)));

            context.DrawRectangle(accent, null, new Rect(0, 0, 48, 48), 11, 11);
            context.DrawGeometry(Brushes.White, null, Page);
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#D9DCF5")), null, Fold);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#B8BCE8")), 1.6, lineCap: PenLineCap.Round), Lines);

            // Magnifying glass at the bottom right
            var lens = new Pen(dark, 3.2, lineCap: PenLineCap.Round);
            context.DrawEllipse(Brushes.White, lens, new Point(30.5, 30.5), 6.2, 6.2);
            context.DrawLine(lens, new Point(35, 35), new Point(39.5, 39.5));
        }
    }
}
