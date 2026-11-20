using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace PdfKnead.Controls;

/// <summary>
/// The preview of a scanned page: zoom relative to the fitted page (1 = whole page), free panning — the page can be dragged until one of
/// its edges reaches the middle of the area, so borders and corners can be looked at closely —, and a before/after comparison with a line
/// that is dragged across the page (left before, right after; zoom and section the same for both). Ctrl+wheel zooms around the mouse
/// pointer, the wheel alone moves up and down (with Shift sideways), a double-click shows the whole page. While a page is turned by hand,
/// <see cref="Rotation"/> turns it about its middle.
/// </summary>
public sealed class ScanPreview : Control
{
    public static readonly StyledProperty<Bitmap?> AfterProperty = AvaloniaProperty.Register<ScanPreview, Bitmap?>(nameof(After));
    public static readonly StyledProperty<Bitmap?> BeforeProperty = AvaloniaProperty.Register<ScanPreview, Bitmap?>(nameof(Before));
    public static readonly StyledProperty<bool> CompareProperty = AvaloniaProperty.Register<ScanPreview, bool>(nameof(Compare));
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ScanPreview, double>(nameof(Zoom), 1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay, coerce: (_, v) => Math.Clamp(v, 1, MaxZoom));
    public static readonly StyledProperty<double> RotationProperty = AvaloniaProperty.Register<ScanPreview, double>(nameof(Rotation));
    public static readonly StyledProperty<string?> BeforeLabelProperty = AvaloniaProperty.Register<ScanPreview, string?>(nameof(BeforeLabel));
    public static readonly StyledProperty<string?> AfterLabelProperty = AvaloniaProperty.Register<ScanPreview, string?>(nameof(AfterLabel));

    /// <summary>The largest zoom, relative to the fitted page.</summary>
    public const double MaxZoom = 8;

    private const double Pad = 8, SplitHitRadius = 8, KnobRadius = 13;

    private static readonly BoxShadows Shadow = BoxShadows.Parse("0 2 10 0 #40000000");
    private static readonly IPen SplitPen = new ImmutablePen(Brushes.White, 2);
    private static readonly IPen SplitShadowPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 4);
    private static readonly IPen KnobPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 1);
    private static readonly IBrush KnobFill = new ImmutableSolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255));
    private static readonly IBrush KnobArrow = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0x30, 0x30, 0x30));
    private static readonly IBrush LabelBack = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0));

    private double _split = 0.5;
    private Vector _pan;
    private Point _dragStart;
    private Vector _panStart;
    private Drag _drag;
    private bool _zooming;

    private enum Drag { None, Split, Pan }

    static ScanPreview()
    {
        AffectsRender<ScanPreview>(AfterProperty, BeforeProperty, CompareProperty, RotationProperty, BeforeLabelProperty, AfterLabelProperty);
        FocusableProperty.OverrideDefaultValue<ScanPreview>(true);
        ClipToBoundsProperty.OverrideDefaultValue<ScanPreview>(true);
    }

    /// <summary>The page as processed („Nachher“).</summary>
    public Bitmap? After
    {
        get => GetValue(AfterProperty);
        set => SetValue(AfterProperty, value);
    }

    /// <summary>The page before the processing („Vorher“), shown left of the line while <see cref="Compare"/> is on.</summary>
    public Bitmap? Before
    {
        get => GetValue(BeforeProperty);
        set => SetValue(BeforeProperty, value);
    }

    /// <summary>Whether before and after are compared with a line.</summary>
    public bool Compare
    {
        get => GetValue(CompareProperty);
        set => SetValue(CompareProperty, value);
    }

    /// <summary>The zoom, 1 (whole page) … <see cref="MaxZoom"/>.</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>The page turned about its middle by this angle in degrees, clockwise (turning by hand).</summary>
    public double Rotation
    {
        get => GetValue(RotationProperty);
        set => SetValue(RotationProperty, value);
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

    /// <summary>The position of the line as a share of the width (0 … 1).</summary>
    public double Split
    {
        get => _split;
        set
        {
            _split = Math.Clamp(value, 0, 1);
            InvalidateVisual();
        }
    }

    /// <summary>The shift of the page from the middle (for tests).</summary>
    internal Vector Pan => _pan;

    // ------------------------------------------------------------ geometry

    private Size ImageSize => After?.Size ?? Before?.Size ?? default;

    private double FitScale
    {
        get
        {
            var size = ImageSize;
            if (size.Width <= 0 || size.Height <= 0) return 1;
            return Math.Max(0.01, Math.Min((Bounds.Width - 2 * Pad) / size.Width, (Bounds.Height - 2 * Pad) / size.Height));
        }
    }

    /// <summary>Where the page lies in the control.</summary>
    internal Rect PageRect
    {
        get
        {
            var size = ImageSize;
            var s = FitScale * Zoom;
            var (w, h) = (size.Width * s, size.Height * s);
            return new Rect(Bounds.Width / 2 - w / 2 + _pan.X, Bounds.Height / 2 - h / 2 + _pan.Y, w, h);
        }
    }

    private double SplitX => Bounds.Width * _split;

    /// <summary>Keeps the middle of the area on the page: the page can be dragged until one of its edges reaches the middle.</summary>
    private void ClampPan()
    {
        var size = ImageSize;
        var s = FitScale * Zoom;
        var (hw, hh) = (size.Width * s / 2, size.Height * s / 2);
        _pan = new Vector(Math.Clamp(_pan.X, -hw, hw), Math.Clamp(_pan.Y, -hh, hh));
    }

    /// <summary>Zooms so that the point <paramref name="at"/> of the page stays where it is.</summary>
    /// <param name="zoom">The new zoom.</param>
    /// <param name="at">A point in the control.</param>
    public void ZoomAt(double zoom, Point at)
    {
        var old = Zoom;
        zoom = Math.Clamp(zoom, 1, MaxZoom);
        if (Math.Abs(zoom - old) < 1e-9) return;
        var middle = new Point(Bounds.Width / 2, Bounds.Height / 2);
        // the offset of the point from the middle of the page scales with the zoom
        var fromCentre = at - (middle + _pan);
        _pan = at - middle - fromCentre * (zoom / old);
        _zooming = true;
        Zoom = zoom;
        _zooming = false;
        ClampPan();
        InvalidateVisual();
    }

    /// <summary>Moves the page (dragging, the wheel).</summary>
    /// <param name="delta">The shift in the control.</param>
    public void PanBy(Vector delta)
    {
        _pan += delta;
        ClampPan();
        InvalidateVisual();
    }

    /// <summary>The whole page again, in the middle.</summary>
    public void Fit()
    {
        _pan = default;
        Zoom = 1;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ZoomProperty && !_zooming)
        {
            // set from outside (the slider): around the middle of the area
            var (old, now) = (change.GetOldValue<double>(), change.GetNewValue<double>());
            if (old > 0) _pan *= now / old;
            ClampPan();
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty || change.Property == AfterProperty)
        {
            ClampPan();
        }
    }

    // ------------------------------------------------------------ drawing

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (After is not { } after) return;
        var page = PageRect;
        using (Rotation != 0 ? context.PushTransform(Matrix.CreateTranslation(-page.Center.X, -page.Center.Y)
                   * Matrix.CreateRotation(Rotation * Math.PI / 180) * Matrix.CreateTranslation(page.Center.X, page.Center.Y)) : default(DrawingContext.PushedState?))
        {
            context.DrawRectangle(Brushes.White, null, new RoundedRect(page), Shadow);
            if (!Compare || Before is not { } before || Rotation != 0)
            {
                context.DrawImage(after, new Rect(after.Size), page);
                return;
            }
            var x = Math.Clamp(SplitX, 0, Bounds.Width);
            using (context.PushClip(new Rect(0, 0, x, Bounds.Height))) context.DrawImage(before, new Rect(before.Size), page);
            using (context.PushClip(new Rect(x, 0, Bounds.Width - x, Bounds.Height))) context.DrawImage(after, new Rect(after.Size), page);
        }

        var top = new Point(SplitX, 0);
        var bottom = new Point(SplitX, Bounds.Height);
        context.DrawLine(SplitShadowPen, top, bottom);
        context.DrawLine(SplitPen, top, bottom);
        var knob = new Point(SplitX, Bounds.Height / 2);
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
        DrawTag(context, BeforeLabel, new Point(8, 8), SplitX, alignRight: false);
        DrawTag(context, AfterLabel, new Point(Bounds.Width - 8, 8), SplitX, alignRight: true);
    }

    private static void DrawTag(DrawingContext context, string? label, Point anchor, double splitX, bool alignRight)
    {
        if (string.IsNullOrEmpty(label)) return;
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White);
        var pad = new Thickness(7, 3);
        var box = new Size(text.Width + pad.Left + pad.Right, text.Height + pad.Top + pad.Bottom);
        var bx = alignRight ? anchor.X - box.Width : anchor.X;
        if (alignRight ? bx < splitX + 4 : bx + box.Width > splitX - 4) return;   // only on its own side of the line
        context.DrawRectangle(LabelBack, null, new RoundedRect(new Rect(new Point(bx, anchor.Y), box), 4));
        context.DrawText(text, new Point(bx + pad.Left, anchor.Y + pad.Top));
    }

    // ------------------------------------------------------------ mouse

    private bool NearSplit(Point p) => Compare && Before is not null && Rotation == 0 && Math.Abs(p.X - SplitX) <= Math.Max(SplitHitRadius, KnobRadius);

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
        _drag = NearSplit(point.Position) ? Drag.Split : Drag.Pan;
        Cursor = new Cursor(_drag == Drag.Split ? StandardCursorType.SizeWestEast : StandardCursorType.SizeAll);
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
                ClampPan();
                InvalidateVisual();
                break;
            default:
                Cursor = NearSplit(p) ? new Cursor(StandardCursorType.SizeWestEast) : new Cursor(StandardCursorType.Hand);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        Cursor = new Cursor(StandardCursorType.Hand);
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) ZoomAt(Zoom * Math.Pow(1.2, e.Delta.Y), e.GetPosition(this));
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) PanBy(new Vector(e.Delta.Y * 60, 0));
        else PanBy(new Vector(e.Delta.X * 60, e.Delta.Y * 60));
        e.Handled = true;
    }
}
