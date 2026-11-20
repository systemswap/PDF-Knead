using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PdfKnead.Controls;

/// <summary>
/// Area for signing with mouse, pen or finger: every movement is one stroke (a sequence of points in
/// user interface units). Drawn as it will later appear on the page: round ends, constant width.
/// </summary>
public sealed class InkCanvas : Control
{
    public static readonly StyledProperty<IBrush?> InkProperty =
        AvaloniaProperty.Register<InkCanvas, IBrush?>(nameof(Ink), Brushes.Black);

    /// <summary>Stroke width in user interface units.</summary>
    public const double StrokeWidth = 3;

    private readonly List<List<Point>> _strokes = [];
    private List<Point>? _current;

    static InkCanvas()
    {
        AffectsRender<InkCanvas>(InkProperty);
        ClipToBoundsProperty.OverrideDefaultValue<InkCanvas>(true);
    }

    public InkCanvas() => Cursor = new Cursor(StandardCursorType.Cross);

    public IBrush? Ink
    {
        get => GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    /// <summary>Gets the strokes: per stroke, x and y alternating; a new copy on every access.</summary>
    public IReadOnlyList<float[]> Strokes =>
        _strokes.Select(s => s.SelectMany(p => new[] { (float)p.X, (float)p.Y }).ToArray()).ToList();

    public bool IsEmpty => _strokes.Count == 0;

    /// <summary>Raised when the strokes have changed (drawn, cleared, undone).</summary>
    public event Action? Changed;

    /// <summary>Removes all strokes.</summary>
    public void Clear()
    {
        _strokes.Clear();
        _current = null;
        InvalidateVisual();
        Changed?.Invoke();
    }

    /// <summary>Removes the last stroke.</summary>
    public void UndoStroke()
    {
        if (_strokes.Count == 0) return;
        _strokes.RemoveAt(_strokes.Count - 1);
        InvalidateVisual();
        Changed?.Invoke();
    }

    /// <summary>Shows existing strokes (changing a signature), fitted into the area with a 20-unit margin and never enlarged.</summary>
    /// <param name="strokes">Strokes with x and y alternating; nothing is shown while the control has no width yet.</param>
    public void Load(IReadOnlyList<float[]> strokes)
    {
        _strokes.Clear();
        var points = strokes.SelectMany(s => Enumerable.Range(0, s.Length / 2).Select(i => new Point(s[2 * i], s[2 * i + 1]))).ToList();
        if (points.Count > 0 && Bounds.Width > 0)
        {
            double left = points.Min(p => p.X), top = points.Min(p => p.Y), right = points.Max(p => p.X), bottom = points.Max(p => p.Y);
            var scale = Math.Min(1, Math.Min((Bounds.Width - 40) / Math.Max(1, right - left), (Bounds.Height - 40) / Math.Max(1, bottom - top)));
            foreach (var s in strokes)
                _strokes.Add(Enumerable.Range(0, s.Length / 2).Select(i => new Point(20 + (s[2 * i] - left) * scale, 20 + (s[2 * i + 1] - top) * scale)).ToList());
        }
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _current = [e.GetPosition(this)];
        _strokes.Add(_current);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_current is null) return;
        // Take the device's intermediate points too (pens report far more often than the user interface draws).
        foreach (var point in e.GetIntermediatePoints(this))
        {
            var p = point.Position;
            var last = _current[^1];
            if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) >= 1.5) _current.Add(p);
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_current is null) return;
        _current = null;
        e.Pointer.Capture(null);
        Changed?.Invoke();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        // Line to sign on
        var baseline = Bounds.Height * 0.72;
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCE, 0xD8)), 1), new Point(24, baseline), new Point(Bounds.Width - 24, baseline));
        var pen = new Pen(Ink, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var stroke in _strokes)
        {
            if (stroke.Count == 1)
            {
                context.DrawEllipse(Ink, null, stroke[0], StrokeWidth / 2, StrokeWidth / 2);
                continue;
            }
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(stroke[0], false);
                for (var i = 1; i < stroke.Count; i++) g.LineTo(stroke[i]);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
