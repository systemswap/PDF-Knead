using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using PdfKnead.ViewModels;
using SkiaSharp;

namespace PdfKnead.Controls;

/// <summary>
/// Before/after view of a page (compression): left of the line the original, right of it the
/// result. The line and its handle can be dragged; the mouse wheel zooms around the mouse pointer, dragging pans, a double-click
/// shows the whole page again. If the view is sharper than the page image, <see cref="RegionWanted"/> reports the visible area
/// for re-rendering; it is shown as soon as it comes back as <see cref="Region"/>.
/// </summary>
public sealed class ComparePreview : Control
{
    public static readonly StyledProperty<SKImage?> BeforeImageProperty = AvaloniaProperty.Register<ComparePreview, SKImage?>(nameof(BeforeImage));
    public static readonly StyledProperty<SKImage?> AfterImageProperty = AvaloniaProperty.Register<ComparePreview, SKImage?>(nameof(AfterImage));
    public static readonly StyledProperty<ZoomRegion?> RegionProperty = AvaloniaProperty.Register<ComparePreview, ZoomRegion?>(nameof(Region));
    public static readonly StyledProperty<double> PageWidthProperty = AvaloniaProperty.Register<ComparePreview, double>(nameof(PageWidth), 595.28);
    public static readonly StyledProperty<double> PageHeightProperty = AvaloniaProperty.Register<ComparePreview, double>(nameof(PageHeight), 841.89);
    public static readonly StyledProperty<string?> BeforeLabelProperty = AvaloniaProperty.Register<ComparePreview, string?>(nameof(BeforeLabel));
    public static readonly StyledProperty<string?> AfterLabelProperty = AvaloniaProperty.Register<ComparePreview, string?>(nameof(AfterLabel));

    public static readonly DirectProperty<ComparePreview, double> ZoomPercentProperty =
        AvaloniaProperty.RegisterDirect<ComparePreview, double>(nameof(ZoomPercent), c => c.ZoomPercent);

    private const double Pad = 16, MaxZoom = 40, SplitHitRadius = 8, KnobRadius = 13;

    private static readonly BoxShadows Shadow = BoxShadows.Parse("0 2 10 0 #40000000");
    private static readonly IPen SplitPen = new ImmutablePen(Brushes.White, 2);
    private static readonly IPen SplitShadowPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 4);
    private static readonly IPen KnobPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 1);
    private static readonly IBrush KnobFill = new ImmutableSolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255));
    private static readonly IBrush KnobArrow = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0x30, 0x30, 0x30));
    private static readonly IBrush LabelBack = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0));

    private double _split = 0.5, _zoom = 1, _zoomPercent;
    private Vector _pan;
    private Point _dragStart;
    private Vector _panStart;
    private Drag _drag;

    private enum Drag { None, Split, Pan }

    protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new PreviewAutomationPeer(this);

    static ComparePreview()
    {
        AffectsRender<ComparePreview>(BeforeImageProperty, AfterImageProperty, RegionProperty, PageWidthProperty, PageHeightProperty,
            BeforeLabelProperty, AfterLabelProperty);
        FocusableProperty.OverrideDefaultValue<ComparePreview>(true);
        ClipToBoundsProperty.OverrideDefaultValue<ComparePreview>(true);
    }

    public SKImage? BeforeImage
    {
        get => GetValue(BeforeImageProperty);
        set => SetValue(BeforeImageProperty, value);
    }

    public SKImage? AfterImage
    {
        get => GetValue(AfterImageProperty);
        set => SetValue(AfterImageProperty, value);
    }

    public ZoomRegion? Region
    {
        get => GetValue(RegionProperty);
        set => SetValue(RegionProperty, value);
    }

    /// <summary>Page width in points (with <see cref="PageHeight"/> the page size).</summary>
    public double PageWidth
    {
        get => GetValue(PageWidthProperty);
        set => SetValue(PageWidthProperty, value);
    }

    public double PageHeight
    {
        get => GetValue(PageHeightProperty);
        set => SetValue(PageHeightProperty, value);
    }

    public string? BeforeLabel
    {
        get => GetValue(BeforeLabelProperty);
        set => SetValue(BeforeLabelProperty, value);
    }

    public string? AfterLabel
    {
        get => GetValue(AfterLabelProperty);
        set => SetValue(AfterLabelProperty, value);
    }

    /// <summary>Zoom as in the main view (100 % = actual size).</summary>
    public double ZoomPercent
    {
        get => _zoomPercent;
        private set => SetAndRaise(ZoomPercentProperty, ref _zoomPercent, value);
    }

    /// <summary>Position of the line as a fraction of the width (0 … 1).</summary>
    public double Split
    {
        get => _split;
        set
        {
            _split = Math.Clamp(value, 0, 1);
            InvalidateVisual();
        }
    }

    /// <summary>Requests sharper re-rendering: visible area (left, top, width, height in points, origin top left) and device pixels per point.</summary>
    public event Action<double, double, double, double, double>? RegionWanted;

    // ------------------------------------------------------------ Geometry

    private double FitScale => Math.Min(Math.Max(1, Bounds.Width - 2 * Pad) / Math.Max(1, PageWidth),
        Math.Max(1, Bounds.Height - 2 * Pad) / Math.Max(1, PageHeight));

    /// <summary>Where the page lies (user interface units).</summary>
    internal Rect PageRect
    {
        get
        {
            var s = FitScale * _zoom;
            var size = new Size(PageWidth * s, PageHeight * s);
            return new Rect(new Point((Bounds.Width - size.Width) / 2 + _pan.X, (Bounds.Height - size.Height) / 2 + _pan.Y), size);
        }
    }

    private double SplitX => Bounds.Width * _split;

    private Rect Visible => PageRect.Intersect(new Rect(Bounds.Size));

    private Point Knob => new(SplitX, Visible.Height > 0 ? Visible.Center.Y : Bounds.Height / 2);

    private bool NearSplit(Point p) => Math.Abs(p.X - SplitX) <= SplitHitRadius && Visible.Contains(p)
        || Point.Distance(p, Knob) <= KnobRadius + 2;

    /// <summary>Limits panning so that the page fills the area or stays centred.</summary>
    private void ClampPan()
    {
        var s = FitScale * _zoom;
        var (w, h) = (PageWidth * s, PageHeight * s);
        var limitX = Math.Max(0, (w - Bounds.Width) / 2 + Pad);
        var limitY = Math.Max(0, (h - Bounds.Height) / 2 + Pad);
        _pan = new Vector(Math.Clamp(_pan.X, -limitX, limitX), Math.Clamp(_pan.Y, -limitY, limitY));
    }

    /// <summary>Zooms around a point of the control; the factor is relative to the whole page.</summary>
    /// <param name="zoom">Zoom factor; 1 = whole page, clamped to 1 … 40.</param>
    /// <param name="at">Point in user interface units that stays in place.</param>
    public void ZoomAt(double zoom, Point at)
    {
        zoom = Math.Clamp(zoom, 1, MaxZoom);
        var page = PageRect;
        var s = FitScale * _zoom;
        var onPage = new Point((at.X - page.Left) / s, (at.Y - page.Top) / s);
        _zoom = zoom;
        var s2 = FitScale * _zoom;
        var (w, h) = (PageWidth * s2, PageHeight * s2);
        _pan = new Vector(at.X - onPage.X * s2 - (Bounds.Width - w) / 2, at.Y - onPage.Y * s2 - (Bounds.Height - h) / 2);
        ViewChanged();
    }

    /// <summary>Zooms around the centre of the control.</summary>
    /// <param name="factor">Factor applied to the current zoom.</param>
    public void ZoomBy(double factor) => ZoomAt(_zoom * factor, new Point(Bounds.Width / 2, Bounds.Height / 2));

    /// <summary>Shows the whole page again.</summary>
    public void Fit()
    {
        _zoom = 1;
        _pan = default;
        ViewChanged();
    }

    private void ViewChanged()
    {
        ClampPan();
        ZoomPercent = Math.Round(FitScale * _zoom * 72 / 96 * 100);
        InvalidateVisual();
        WantRegion();
    }

    /// <summary>Requests sharper re-rendering when the view shows more pixels than the page image has (and the current region does not already cover it).</summary>
    private void WantRegion()
    {
        if (BeforeImage is not { } image || Bounds.Width < 1) return;
        var s = FitScale * _zoom;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var devicePerPoint = s * scaling;
        if (devicePerPoint <= image.Width / Math.Max(1, PageWidth) * 1.1) return;
        var page = PageRect;
        var visible = Visible;
        if (visible.Width <= 0 || visible.Height <= 0) return;
        if (Region is { } r && r.Before.Width >= (int)(r.Width * devicePerPoint * 0.95)
            && r.Left <= (visible.Left - page.Left) / s + 0.5 && r.Top <= (visible.Top - page.Top) / s + 0.5
            && r.Left + r.Width >= (visible.Right - page.Left) / s - 0.5 && r.Top + r.Height >= (visible.Bottom - page.Top) / s - 0.5)
            return;
        RegionWanted?.Invoke((visible.Left - page.Left) / s, (visible.Top - page.Top) / s, visible.Width / s, visible.Height / s, devicePerPoint);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty || change.Property == PageWidthProperty || change.Property == PageHeightProperty)
            ViewChanged();
        else if (change.Property == AfterImageProperty || change.Property == BeforeImageProperty)
            WantRegion();
    }

    // ------------------------------------------------------------ Drawing

    public override void Render(DrawingContext context)
    {
        var page = PageRect;
        context.DrawRectangle(Brushes.White, null, new RoundedRect(page), Shadow);
        var visible = Visible;
        if (visible.Width <= 0 || visible.Height <= 0) return;
        var x = Math.Clamp(SplitX, visible.Left, visible.Right);
        Draw(context, new Rect(visible.Left, visible.Top, x - visible.Left, visible.Height), page, BeforeImage, Region?.Before);
        Draw(context, new Rect(x, visible.Top, visible.Right - x, visible.Height), page, AfterImage, Region?.After);

        var top = new Point(SplitX, visible.Top);
        var bottom = new Point(SplitX, visible.Bottom);
        context.DrawLine(SplitShadowPen, top, bottom);
        context.DrawLine(SplitPen, top, bottom);

        // Handle: circle with two arrowheads ◂ ▸
        var knob = Knob;
        context.DrawEllipse(KnobFill, KnobPen, knob, KnobRadius, KnobRadius);
        var arrows = new StreamGeometry();
        using (var g = arrows.Open())
        {
            foreach (var dir in new[] { -1, 1 })
            {
                g.BeginFigure(new Point(knob.X + dir * 9, knob.Y), true);
                g.LineTo(new Point(knob.X + dir * 3.5, knob.Y - 5));
                g.LineTo(new Point(knob.X + dir * 3.5, knob.Y + 5));
                g.EndFigure(true);
            }
        }
        context.DrawGeometry(KnobArrow, null, arrows);

        DrawTag(context, BeforeLabel, new Point(visible.Left + 8, visible.Top + 8), SplitX, alignRight: false);
        DrawTag(context, AfterLabel, new Point(visible.Right - 8, visible.Top + 8), SplitX, alignRight: true);
    }

    /// <summary>One side of the line: the whole page image, with the sharp section on top.</summary>
    private void Draw(DrawingContext context, Rect clip, Rect page, SKImage? image, SKImage? sharp)
    {
        if (clip.Width <= 0 || image is null) return;
        var draws = new List<SkiaImageDraw>(2)
        {
            new(image, new SKRect(0, 0, image.Width, image.Height), new SKRect((float)page.Left, (float)page.Top, (float)page.Right, (float)page.Bottom)),
        };
        if (sharp is not null && Region is { } r)
        {
            var s = page.Width / Math.Max(1, PageWidth);
            var (left, top) = (page.Left + r.Left * s, page.Top + r.Top * s);
            draws.Add(new(sharp, new SKRect(0, 0, sharp.Width, sharp.Height),
                new SKRect((float)left, (float)top, (float)(left + r.Width * s), (float)(top + r.Height * s))));
        }
        context.Custom(new SkiaImageDrawing(clip, draws));
    }

    private static void DrawTag(DrawingContext context, string? label, Point anchor, double splitX, bool alignRight)
    {
        if (string.IsNullOrEmpty(label)) return;
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White);
        var pad = new Thickness(7, 3);
        var box = new Size(text.Width + pad.Left + pad.Right, text.Height + pad.Top + pad.Bottom);
        var bx = alignRight ? anchor.X - box.Width : anchor.X;
        // Only on its own side of the line
        if (alignRight ? bx < splitX + 4 : bx + box.Width > splitX - 4) return;
        context.DrawRectangle(LabelBack, null, new RoundedRect(new Rect(new Point(bx, anchor.Y), box), 4));
        context.DrawText(text, new Point(bx + pad.Left, anchor.Y + pad.Top));
    }

    // ------------------------------------------------------------ Mouse and keys

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        Focus();
        if (e.ClickCount == 2)
        {
            Fit();
            e.Handled = true;
            return;
        }
        _dragStart = point.Position;
        _panStart = _pan;
        _drag = NearSplit(point.Position) ? Drag.Split : _zoom > 1 ? Drag.Pan : Drag.Split;
        if (_drag == Drag.Split) Split = point.Position.X / Math.Max(1, Bounds.Width);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        switch (_drag)
        {
            case Drag.Split:
                Split = p.X / Math.Max(1, Bounds.Width);
                break;
            case Drag.Pan:
                _pan = _panStart + (p - _dragStart);
                ViewChanged();
                break;
            default:
                Cursor = NearSplit(p) ? new Cursor(StandardCursorType.SizeWestEast) : _zoom > 1 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(_zoom * Math.Pow(1.2, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus:
                ZoomBy(1.25);
                break;
            case Key.Subtract or Key.OemMinus:
                ZoomBy(1 / 1.25);
                break;
            case Key.D0 or Key.NumPad0:
                Fit();
                break;
            case Key.Left:
                Split -= 0.02;
                break;
            case Key.Right:
                Split += 0.02;
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
