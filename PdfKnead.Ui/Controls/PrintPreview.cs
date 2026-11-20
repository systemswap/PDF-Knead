using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SkiaSharp;

namespace PdfKnead.Controls;

/// <summary>
/// Preview of a printed sheet: the sheet centred with a shadow, on it the image from the print path, and as a dashed line the margin
/// that the printer cannot print on.
/// </summary>
public sealed class PrintPreview : Control
{
    public static readonly StyledProperty<SKImage?> ImageProperty = AvaloniaProperty.Register<PrintPreview, SKImage?>(nameof(Image));
    public static readonly StyledProperty<double> SheetWidthProperty = AvaloniaProperty.Register<PrintPreview, double>(nameof(SheetWidth), 595.28);
    public static readonly StyledProperty<double> SheetHeightProperty = AvaloniaProperty.Register<PrintPreview, double>(nameof(SheetHeight), 841.89);
    public static readonly StyledProperty<double> MarginPointsProperty = AvaloniaProperty.Register<PrintPreview, double>(nameof(MarginPoints));

    private static readonly BoxShadows Shadow = BoxShadows.Parse("0 2 10 0 #40000000");
    private static readonly IPen MarginPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 79, 70, 229)), 1, new DashStyle([4, 3], 0));

    protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new PreviewAutomationPeer(this);

    static PrintPreview() => AffectsRender<PrintPreview>(ImageProperty, SheetWidthProperty, SheetHeightProperty, MarginPointsProperty);

    public SKImage? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public double SheetWidth
    {
        get => GetValue(SheetWidthProperty);
        set => SetValue(SheetWidthProperty, value);
    }

    public double SheetHeight
    {
        get => GetValue(SheetHeightProperty);
        set => SetValue(SheetHeightProperty, value);
    }

    /// <summary>Non-printable margin in points; 0 = no line.</summary>
    public double MarginPoints
    {
        get => GetValue(MarginPointsProperty);
        set => SetValue(MarginPointsProperty, value);
    }

    /// <summary>Where the sheet lies (user interface units).</summary>
    internal Rect SheetRect
    {
        get
        {
            const double pad = 24;
            var (w, h) = (Math.Max(1, SheetWidth), Math.Max(1, SheetHeight));
            var scale = Math.Min(Math.Max(1, Bounds.Width - 2 * pad) / w, Math.Max(1, Bounds.Height - 2 * pad) / h);
            var size = new Size(w * scale, h * scale);
            return new Rect(new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2), size);
        }
    }

    public override void Render(DrawingContext context)
    {
        var rect = SheetRect;
        context.DrawRectangle(Brushes.White, null, new RoundedRect(rect), Shadow);
        if (Image is { } image)
            context.Custom(new SkiaImageDrawing(rect, [new SkiaImageDraw(image, new SKRect(0, 0, image.Width, image.Height),
                new SKRect((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom))]));
        if (MarginPoints > 0)
        {
            var m = MarginPoints * rect.Width / Math.Max(1, SheetWidth);
            if (rect.Width > 4 * m && rect.Height > 4 * m) context.DrawRectangle(null, MarginPen, rect.Deflate(m));
        }
    }
}
