using System.Globalization;
using Avalonia;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using Avalonia.Controls;
using PdfMatrix = PdfKnead.Core.Pdf.Graphics.Matrix;
using Avalonia.Input;
using Avalonia.Media;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.ViewModels;

namespace PdfKnead.Controls;

/// <summary>
/// „Messen“ in the view (Ablaufplan 7.6 c, e): a click sets a point — snapped to corners and lines of the drawing within a few pixels, with
/// Alt where clicked —, the mouse shows where the next one would go and the value so far; a double-click or Enter ends polylines, areas and
/// arcs, Esc drops the measurement going on, Backspace takes back the last point. Finished measurements stay drawn until the tool is switched
/// off, each in its colour: a length with arrows and leader ticks, its value along it; a polyline with an arrow at its end and its total to
/// the right of it; an area filled; an arc with its centre — and, where switched on, the length of each piece and the angle at each bend.
/// A click on a value chooses its measurement: handles at its points can be dragged (snapping as when measuring), Delete removes it, and the
/// window places a bar above it (<see cref="MeasureBarPlaced"/>). See docs/FEHLERGEDAECHTNIS.md, No. 581, 585, 586.
/// </summary>
public sealed partial class DocumentView
{
    /// <summary>Snapping distance in screen pixels.</summary>
    private const double SnapPixels = 8;

    private static readonly IPen MeasureHalo = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 3.5);
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly Typeface LabelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Color DefaultMeasureColor = Color.FromRgb(0xEA, 0x58, 0x0C);

    /// <summary>Half the size of a handle, and how near a press has to be to take it, in pixels.</summary>
    private const double MeasureHandleSize = 4.5;

    /// <summary>The value labels drawn last, with their measurement — a click on one chooses it.</summary>
    private readonly List<(MeasureRow Row, Rect Box)> _measureHits = [];

    /// <summary>The handle being dragged: the measurement and its point.</summary>
    private (MeasureRow Row, int Index)? _measureDrag;

    /// <summary>
    /// The label of a measurement's value being dragged (design decision, docs/FEHLERGEDAECHTNIS.md, No. 703): the measurement, where the mouse
    /// holds it (from its middle) and its own place (view).
    /// </summary>
    private (MeasureRow Row, Vector Grab, Point Home)? _labelDrag;

    /// <summary>Of each measurement drawn, the label of its value: its own place and where it is drawn (its middle, view).</summary>
    private readonly Dictionary<MeasureRow, (Point Home, Point Centre)> _labelPlaces = new(ReferenceEqualityComparer.Instance);

    /// <summary>The label of the value of the measurement drawn last: its own place and where it was drawn (view).</summary>
    private (Point Home, Point Centre) _lastLabel;

    /// <summary>Near its own place a dragged label goes back there (view units).</summary>
    private const double LabelSnap = 8;

    /// <summary>
    /// The first arc of a polyline or area being dragged out with Ctrl+Alt (decided on 9.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 664):
    /// the page, where the press was, and the direction (page space) the mouse first moved in — null until it moved a few pixels.
    /// </summary>
    private (int Page, Point Press, (double X, double Y)? Direction)? _tangentDrag;

    /// <summary>How far the mouse moves before a drag with Ctrl+Alt counts as giving the direction (view units).</summary>
    private const double DirectionPixels = 8;

    /// <summary>Where the chosen measurement is in the view (its points' bounds), as last told.</summary>
    private Rect? _measureBar;

    /// <summary>
    /// Raised (after drawing, on the UI thread) when the chosen measurement moves in the view or the choice ends: the bounds of its points in
    /// coordinates of this view, or null — the window places the bar of the measurement above it.
    /// </summary>
    public event Action<Rect?>? MeasureBarPlaced;

    /// <summary>Whether a point is set free (not snapped): Alt with snapping on, without Alt with snapping off.</summary>
    private bool FreePoint(KeyModifiers modifiers) => _doc?.MeasureSnap ?? true ? modifiers.HasFlag(KeyModifiers.Alt) : !modifiers.HasFlag(KeyModifiers.Alt);

    /// <summary>Whether the angle is free (not straightened): Shift with straight lines on, without Shift with them off (Ablaufplan 7.6 g).</summary>
    private bool FreeAngle(KeyModifiers modifiers) => _doc?.MeasureStraight ?? true ? modifiers.HasFlag(KeyModifiers.Shift) : !modifiers.HasFlag(KeyModifiers.Shift);

    /// <summary>
    /// Whether the next point is an arc point (Ablaufplan 7.6 j): Ctrl+Shift in a polyline or area — the next point after it ends the arc through
    /// it. A point of an arc is never straightened (its direction is that of the arc).
    /// </summary>
    private bool ArcPoint(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Control) && modifiers.HasFlag(KeyModifiers.Shift) && _doc?.MeasureKind is MeasureKind.Polyline or MeasureKind.Area;

    /// <summary>
    /// Whether the next point ends a tangent arc (Ablaufplan 7.6 k; design decision): Ctrl+Alt in a polyline or area. Alt is part of the
    /// key, not „set free“ here: the end snaps as a plain click does.
    /// </summary>
    private bool TangentArc(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Control) && modifiers.HasFlag(KeyModifiers.Alt) && !modifiers.HasFlag(KeyModifiers.Shift) &&
        _doc?.MeasureKind is MeasureKind.Polyline or MeasureKind.Area;

    /// <summary>
    /// The measurement under <paramref name="position"/> — at one of its points (the point), else on its line — the one drawn last first; a
    /// measurement being continued does not count. Null if none.
    /// </summary>
    private (MeasureRow Row, int? Point)? MeasurementAt(DocumentViewModel doc, Point position)
    {
        for (var i = doc.Measurements.Count - 1; i >= 0; i--)
        {
            var row = doc.Measurements[i];
            if (doc.IsContinuing(row) || row.Page >= _pageRects.Count || !_shown[row.Page]) continue;
            if (HandleAt(row, position) is { } point) return (row, point);
        }
        for (var i = doc.Measurements.Count - 1; i >= 0; i--)
        {
            var row = doc.Measurements[i];
            if (doc.IsContinuing(row) || row.Page >= _pageRects.Count || !_shown[row.Page]) continue;
            var m = row.Measurement;
            IReadOnlyList<(double X, double Y)> course = m.Kind == MeasureKind.Length && m.Points.Count >= 2 ? [m.Points[0], m.Points[^1]] : Course(m);
            var closed = m.Kind == MeasureKind.Area && m.Arc is null;
            var count = closed ? course.Count : course.Count - 1;
            for (var k = 0; k < count; k++)
            {
                var (a, b) = (ToView(row.Page, course[k].X, course[k].Y), ToView(row.Page, course[(k + 1) % course.Count].X, course[(k + 1) % course.Count].Y));
                var d = b - a;
                var length = d.X * d.X + d.Y * d.Y;
                var t = length > 0 ? Math.Clamp(((position.X - a.X) * d.X + (position.Y - a.Y) * d.Y) / length, 0, 1) : 0;
                if (Length(a + d * t - position) <= MeasureHandleSize + 2) return (row, null);
            }
        }
        return null;
    }

    /// <summary>The piece of a polyline or area under <paramref name="position"/> (within a few pixels of its course) and the page point there; null if none.</summary>
    private (int Piece, double X, double Y)? PieceAt(MeasureRow row, Point position)
    {
        if (row.Measurement.Outline is not { } outline || row.Page >= _pageRects.Count || !_shown[row.Page]) return null;
        (int Piece, double Distance)? best = null;
        for (var k = 0; k < outline.Pieces.Count; k++)
        {
            var path = outline.Pieces[k].Path;
            for (var i = 0; i + 1 < path.Count; i++)
            {
                var (a, b) = (ToView(row.Page, path[i].X, path[i].Y), ToView(row.Page, path[i + 1].X, path[i + 1].Y));
                var d = b - a;
                var length = d.X * d.X + d.Y * d.Y;
                var t = length > 0 ? Math.Clamp(((position.X - a.X) * d.X + (position.Y - a.Y) * d.Y) / length, 0, 1) : 0;
                var distance = Length(a + d * t - position);
                if (distance <= MeasureHandleSize + 2 && (best is null || distance < best.Value.Distance)) best = (k, distance);
            }
        }
        if (best is not { } hit) return null;
        var (x, y) = PageToView(row.Page).Invert().Transform(position.X, position.Y);
        return (hit.Piece, x, y);
    }

    /// <summary>
    /// The point to measure at: snapped to the drawing of the page (unless <paramref name="free"/>) and, with a previous point
    /// <paramref name="from"/>, straight from it at 0°, 45°, 90° … when near (unless <paramref name="freeAngle"/>; No. 602).
    /// </summary>
    /// <param name="wait">A click that sets the point: if the drawing of the page is not at hand, wait briefly until it is built again.</param>
    private (double X, double Y, SnapKind Snap) MeasurePoint(int page, Point position, bool free, bool freeAngle = true, (double X, double Y)? from = null,
        bool wait = false)
    {
        var (x, y) = PageToView(page).Invert().Transform(position.X, position.Y);
        if (_doc is null || free && freeAngle) return (x, y, SnapKind.None);
        var list = SnapList(_doc, page, wait);
        var ((sx, sy), snap) = MeasureSnap.Find(list, x, y, SnapPixels / Math.Max(Scale, 1e-6), from, drawing: !free, straight: !freeAngle);
        return (sx, sy, snap);
    }

    /// <summary>
    /// The drawing of a page to snap to: the one the document keeps while „Messen“ is on (No. 625), else from the shared cache — kept from then
    /// on. The shared cache drops the least used lists when memory gets tight: then the list is built again — a moving mouse does not wait for
    /// it (it is kept once it is there, so the clicks after it do not wait), a click that sets a point waits up to a second, so that the point
    /// snaps as the mouse showed it (docs/FEHLERGEDAECHTNIS.md, No. 610). Before 625 every dropped list had to be built again, and a click under
    /// load could still come too early.
    /// </summary>
    private static Core.Pdf.Graphics.DisplayList? SnapList(DocumentViewModel doc, int page, bool wait)
    {
        var key = doc.Pages[page].Key;
        if (doc.KeptSnapList(key) is { } kept) return kept;
        if (doc.Renderer.TryGetDisplayList(key) is { } list)
        {
            doc.KeepSnapList(key, list);
            return list;
        }
        var building = doc.Renderer.GetDisplayList(key);
        if (!wait)
        {
            // kept as soon as it is built (on the thread of the view)
            building.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result is { } built) Avalonia.Threading.Dispatcher.UIThread.Post(() => doc.KeepSnapList(key, built));
            }, TaskScheduler.Default);
            return null;
        }
        try
        {
            if (!building.Wait(TimeSpan.FromSeconds(1)) || building.Result is not { } result) return null;
            doc.KeepSnapList(key, result);
            return result;
        }
        catch (AggregateException)
        {
            return null;   // an unreadable page: no drawing to snap to
        }
    }

    /// <summary>
    /// The point the line being set comes from (to straighten it): the last point being measured, the start of the calibration line — none when
    /// the last point is an arc point (the next one ends its arc).
    /// </summary>
    /// <param name="near">Where the mouse is (page space): continuing a polyline before its end is decided, the end nearer to it counts.</param>
    private static (double X, double Y)? StraightFrom(DocumentViewModel doc, int page, (double X, double Y)? near = null)
    {
        if (doc.IsCalibrating) return doc.CalibrationPage == page && doc.CalibrationPoints.Count == 1 ? doc.CalibrationPoints[0] : null;
        return doc.MeasureKind != MeasureKind.Radius && doc.MeasurePage == page ? doc.NextFrom(near) : null;
    }

    /// <summary>The neighbour of a dragged point (the one before it, of the first the next one); none for circles and arcs, nor for a point of an arc.</summary>
    private static (double X, double Y)? StraightFrom(MeasureRow row, int index)
    {
        var points = row.Measurement.Points;
        if (row.Measurement.Kind == MeasureKind.Radius || points.Count < 2) return null;
        var neighbour = index > 0 ? index - 1 : 1;
        // the point is on an arc or ends one, or the neighbour is an arc point: the piece between them is no straight line
        if (row.Measurement.Outline is { } outline && outline.Pieces.Any(p => p.IsArc && (p.From == index || p.To == index || p.Through == index)))
            return null;
        return points[neighbour];
    }

    /// <summary>The straight direction the point snapped to: from where, to the point (page space) — drawn as a guide.</summary>
    private (int Page, double FromX, double FromY, double X, double Y)? _straightGuide;

    /// <summary>Where the mouse was last over the view with „Messen“ on (Shift or Alt alone change the point there).</summary>
    private Point? _measureMouse;

    private bool MeasurePressed(PointerPressedEventArgs e, Point position)
    {
        if (_doc is not { IsMeasuring: true } doc) return false;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            if (!doc.CancelCalibration()) doc.CancelMeasure();
            return true;
        }
        if (!props.IsLeftButtonPressed) return false;   // middle button: pan the view as usual
        // calibrating (7.6 g): the clicks set the start and end of the line of known length, snapping as when measuring
        if (doc.IsCalibrating)
        {
            if (PageAt(position) is { } calibrated)
            {
                var (cx, cy, _) = MeasurePoint(calibrated, position, FreePoint(e.KeyModifiers), FreeAngle(e.KeyModifiers), StraightFrom(doc, calibrated), wait: true);
                doc.CalibrateAt(calibrated, cx, cy);
            }
            return true;
        }
        // nothing being measured: clicks on measurements choose them; Ctrl+click on the line of the chosen one puts a point in there, anywhere
        // else Ctrl+click begins a new measurement all the same (Ablaufplan 7.6 l, design decision: a new one often starts where an old one
        // ends)
        var ctrl = e.KeyModifiers == KeyModifiers.Control;
        if (doc.MeasurePoints.Count == 0 && ctrl && doc.SelectedMeasurement is { } line && HandleAt(line, position) is null &&
            PieceAt(line, position) is { } piece && doc.InsertMeasurePoint(line, piece.Piece, piece.X, piece.Y))
            return true;
        if (doc.MeasurePoints.Count == 0 && !ctrl)
        {
            // a handle of the chosen measurement is chosen (Del takes it out) and dragged, a value chooses its measurement
            if (doc.SelectedMeasurement is { } chosen && HandleAt(chosen, position) is { } index)
            {
                doc.SelectedMeasurePoint = index;
                _measureDrag = (chosen, index);
                e.Pointer.Capture(this);
                doc.MeasureHoverOff();   // no snapping mark left where the press began
                return true;
            }
            // the label of a value: dragged within its rules, a double-click puts it back (design decision, No. 703) — a point of a
            // measurement under it goes first, the line after it
            if (e.KeyModifiers == KeyModifiers.None && LabelHit(position) is { } labelled && MeasurementAt(doc, position) is not { Point: not null })
            {
                doc.SelectedMeasurement = labelled;
                if (e.ClickCount >= 2)
                {
                    doc.SetMeasureLabel(labelled, null);
                    return true;
                }
                var (home, centre) = _labelPlaces[labelled];
                _labelDrag = (labelled, position - centre, home);
                e.Pointer.Capture(this);
                doc.MeasureHoverOff();
                return true;
            }
            // a double-click on the line of a polyline: it goes on at the end nearer to the next click (Ablaufplan 7.6 l)
            if (e.ClickCount >= 2 && MeasurementAt(doc, position) is { Row: { CanContinue: true } continued })
            {
                doc.ContinueMeasurement(continued);
                return true;
            }
            // a point of any measurement: chosen and dragged at once; its line: chosen (Ablaufplan 7.6 l; design decision)
            if (e.KeyModifiers == KeyModifiers.None && MeasurementAt(doc, position) is { } hit)
            {
                doc.SelectedMeasurement = hit.Row;
                if (hit.Point is { } point)
                {
                    doc.SelectedMeasurePoint = point;
                    _measureDrag = (hit.Row, point);
                    e.Pointer.Capture(this);
                    doc.MeasureHoverOff();
                }
                return true;
            }
            for (var i = _measureHits.Count - 1; i >= 0; i--)
                if (_measureHits[i].Box.Contains(position))
                {
                    doc.SelectedMeasurement = _measureHits[i].Row;
                    return true;
                }
        }
        if (PageAt(position) is not { } page) return true;
        if (e.ClickCount >= 2)
        {
            doc.FinishMeasure();
            return true;
        }
        var arc = ArcPoint(e.KeyModifiers);
        var tangent = TangentArc(e.KeyModifiers);
        // Ctrl+Alt after the first point: no direction yet — dragging gives the direction of the first arc, a click makes an arc point (No. 664)
        if (tangent && doc.MeasureNeedsDirection && page == doc.MeasurePage)
        {
            _tangentDrag = (page, position, null);
            e.Pointer.Capture(this);
            return true;
        }
        var (x, y, _) = MeasurePoint(page, position, FreePoint(tangent ? e.KeyModifiers & ~KeyModifiers.Alt : e.KeyModifiers), arc || tangent || FreeAngle(e.KeyModifiers),
            arc || tangent ? null : StraightFrom(doc, page, ToPage(page, position)), wait: true);
        doc.MeasureAt(page, x, y, arc, tangent);
        return true;
    }

    /// <summary>The measurement whose label of its value is at <paramref name="position"/> (the one drawn last first); null if none.</summary>
    private MeasureRow? LabelHit(Point position)
    {
        for (var i = _measureHits.Count - 1; i >= 0; i--)
            if (_measureHits[i].Box.Contains(position)) return _measureHits[i].Row;
        return null;
    }

    /// <summary>The point of a measurement whose handle is at <paramref name="position"/>; null if none.</summary>
    private int? HandleAt(MeasureRow row, Point position)
    {
        if (row.Page >= _pageRects.Count || !_shown[row.Page]) return null;
        var points = row.Measurement.Points;
        for (var i = points.Count - 1; i >= 0; i--)
        {
            var at = ToView(row.Page, points[i].X, points[i].Y);
            if (Math.Abs(at.X - position.X) <= MeasureHandleSize + 2 && Math.Abs(at.Y - position.Y) <= MeasureHandleSize + 2) return i;
        }
        return null;
    }

    /// <summary>A handle let go: the point stays where it is (a dimension is saved). false = no handle was dragged.</summary>
    private bool MeasureReleased(PointerReleasedEventArgs e)
    {
        if (_tangentDrag is { } tangentDrag)
        {
            _tangentDrag = null;
            e.Pointer.Capture(null);
            if (_doc is not { IsMeasuring: true } measuring) return true;
            // where the mouse was let go (snapped as the end of an arc); without a drag where it was pressed: an arc point
            var at = tangentDrag.Direction is null ? tangentDrag.Press : e.GetPosition(this);
            var (ex, ey, _) = MeasurePoint(tangentDrag.Page, at, FreePoint(e.KeyModifiers & ~KeyModifiers.Alt), freeAngle: true, wait: true);
            measuring.MeasureAt(tangentDrag.Page, ex, ey, tangent: true, direction: tangentDrag.Direction);
            return true;
        }
        if (_labelDrag is { } labelDrag)
        {
            _labelDrag = null;
            e.Pointer.Capture(null);
            // where it was drawn last (within its rules), in page space
            (double X, double Y)? at = labelDrag.Row.LabelAt is not null && _labelPlaces.TryGetValue(labelDrag.Row, out var drawn)
                ? ToPage(labelDrag.Row.Page, drawn.Centre)
                : null;
            _doc?.SetMeasureLabel(labelDrag.Row, at);
            return true;
        }
        if (_measureDrag is not { } drag) return false;
        _measureDrag = null;
        e.Pointer.Capture(null);
        _doc?.EndMeasureDrag(drag.Row);   // a dimension is saved now, in one undo step (No. 605)
        return true;
    }

    /// <summary>Mouse moved with „Messen“ on: the point the next click would set. false = the tool is off.</summary>
    private bool MeasureMoved(Point position, KeyModifiers modifiers)
    {
        var handled = MeasureMovedCore(position, modifiers);
        // where the magnifier goes is decided as the mouse moves; drawing only lets it glide there (No. 684)
        if (handled && _doc is { } doc) UpdateLoupeTarget(doc);
        return handled;
    }

    private bool MeasureMovedCore(Point position, KeyModifiers modifiers)
    {
        if (_doc is not { IsMeasuring: true } doc) return false;
        // over a measurement (nothing being measured): a hand — a click chooses it, a point can be dragged at once (Ablaufplan 7.6 l)
        Cursor = doc.MeasurePoints.Count == 0 && _measureDrag is null && !modifiers.HasFlag(KeyModifiers.Control) && MeasurementAt(doc, position) is not null
            ? HandCursor : CrossCursor;
        // the way the mouse is moving, smoothed (the magnifier keeps out of it; No. 664)
        if (_measureMouse is { } before && position != before)
            _mouseMotion = _mouseMotion * 0.6 + (position - before) * 0.4;
        _measureMouse = position;
        _straightGuide = null;
        // the dragged measurement is no longer the chosen one (deleted, another chosen): the drag ends (No. 598)
        if (_measureDrag is { } stale && !ReferenceEquals(stale.Row, doc.SelectedMeasurement)) _measureDrag = null;
        if (_labelDrag is { } labelStale && !doc.Measurements.Contains(labelStale.Row)) _labelDrag = null;
        if (_labelDrag is { } labelDrag)
        {
            // the label follows the mouse; near its own place it goes back there (the drawing keeps it within its rules)
            var wanted = position - labelDrag.Grab;
            labelDrag.Row.LabelAt = Span(wanted, labelDrag.Home) < LabelSnap ? null : ToPage(labelDrag.Row.Page, wanted);
            InvalidateVisual();
            return true;
        }
        if (_measureDrag is { } drag)
        {
            // the point follows the mouse on the page of its measurement, snapping as when measuring
            var neighbour = StraightFrom(drag.Row, drag.Index);
            var (dx, dy, dragSnap) = MeasurePoint(drag.Row.Page, position, FreePoint(modifiers), FreeAngle(modifiers), neighbour);
            if (dragSnap.HasFlag(SnapKind.Straight) && neighbour is { } n) _straightGuide = (drag.Row.Page, n.X, n.Y, dx, dy);
            doc.MoveMeasurePoint(drag.Row, drag.Index, dx, dy);
            return true;
        }
        if (PageAt(position) is not { } page)
        {
            doc.MeasureHoverOff();
            return true;
        }
        var arcPoint = ArcPoint(modifiers);
        var tangentArc = TangentArc(modifiers);
        // the first arc being dragged out: its direction is where the mouse first went from the press (No. 664)
        if (_tangentDrag is { } dragging && dragging.Page == page)
        {
            if (dragging.Direction is null && Span(dragging.Press, position) >= DirectionPixels)
            {
                var (px, py) = ToPage(page, dragging.Press);
                var (mx, my) = ToPage(page, position);
                _tangentDrag = dragging with { Direction = (mx - px, my - py) };
            }
            tangentArc = true;
            arcPoint = false;
        }
        var from = arcPoint || tangentArc ? null : StraightFrom(doc, page, ToPage(page, position));
        var (x, y, snap) = MeasurePoint(page, position, FreePoint(tangentArc ? modifiers & ~KeyModifiers.Alt : modifiers), arcPoint || tangentArc || FreeAngle(modifiers), from);
        if (snap.HasFlag(SnapKind.Straight) && from is { } f) _straightGuide = (page, f.X, f.Y, x, y);
        doc.MeasureHoverAt(page, x, y, snap, tangentArc, arcPoint, _tangentDrag?.Direction);
        InvalidateVisual();
        return true;
    }

    /// <summary>Shift, Alt or Ctrl pressed or let go with „Messen“ on: the point under the mouse is worked out again at once (No. 602, 620).</summary>
    private void MeasureModifiers(Key key, KeyModifiers modifiers, bool down)
    {
        if (_doc is not { IsMeasuring: true } || _measureMouse is not { } at) return;
        var flag = key switch
        {
            Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
            Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
            Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
            _ => KeyModifiers.None,
        };
        if (flag == KeyModifiers.None) return;
        MeasureMoved(at, down ? modifiers | flag : modifiers & ~flag);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        MeasureModifiers(e.Key, e.KeyModifiers, down: false);
    }

    /// <summary>
    /// Keys with „Messen“ on: Enter ends, Esc asks about a measurement going on that could be ended and drops one that cannot (with nothing
    /// going on it switches the tool off; No. 619), Backspace takes back a point.
    /// </summary>
    private bool MeasureKey(KeyEventArgs e)
    {
        if (_doc is not { IsMeasuring: true } doc) return false;
        MeasureModifiers(e.Key, e.KeyModifiers, down: true);
        switch (e.Key)
        {
            case Key.Enter:
                return doc.FinishMeasure();
            case Key.Back:
                return doc.UndoMeasurePoint();
            // a tangent arc being placed (Ctrl+Alt held, or the first one dragged out): it flips to the other side of its chord; otherwise the
            // space bar pages on as ever
            case Key.Space when _tangentDrag is not null || TangentArc(e.KeyModifiers):
                if (!doc.FlipMeasureArc()) return false;
                if (_measureMouse is { } at) MeasureMoved(at, e.KeyModifiers);   // the preview at once
                return true;
            // a chosen point goes (Ablaufplan 7.6 k); without one the whole measurement
            case Key.Delete when doc.SelectedMeasurePoint is not null && doc.MeasurePoints.Count == 0:
                return doc.DeleteSelectedMeasurePoint();
            case Key.Delete when doc.SelectedMeasurement is not null && doc.MeasurePoints.Count == 0:
                doc.DeleteSelectedMeasurementCommand.Execute(null);
                return true;
            case Key.Escape:
                if (doc.IsCalibrating && doc.CancelCalibration()) return true;
                if (doc.MeasurePoints.Count > 0)
                {
                    _ = doc.EscapeMeasureAsync();
                    return true;
                }
                if (doc.SelectedMeasurement is not null)
                {
                    doc.SelectedMeasurement = null;
                    return true;
                }
                doc.Tool = ViewTool.SelectText;
                return true;
        }
        return false;
    }

    /// <summary>The colour of a choice of colour; the orange of the tool without one.</summary>
    private static Color ColorOf(FieldColorOption? option) => option?.Rgb is { } c ? ColorOf(c) : DefaultMeasureColor;

    /// <summary>A colour 0xRRGGBB.</summary>
    private static Color ColorOf(int c) => Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);

    private void DrawMeasurements(DrawingContext context)
    {
        _measureHits.Clear();
        if (_doc is not { IsMeasuring: true } doc)
        {
            PlaceMeasureBar(null);
            return;
        }
        _labelPlaces.Clear();
        foreach (var row in doc.Measurements)
            if (row.Page < _pageRects.Count && _shown[row.Page] && !doc.IsContinuing(row) &&
                DrawMeasurement(context, row.Page, row.Measurement, ColorOf(row.Rgb), row.ShowSegments, row.ShowAngles, row.StrokeWidth, row.Fill, live: false,
                    labelAt: row.LabelAt) is { } box)
            {
                _measureHits.Add((row, box));
                _labelPlaces[row] = _lastLabel;
            }
        var color = ColorOf(doc.MeasureColor);
        DrawStraightGuide(context, doc);
        if (doc.MeasurePage is var page and >= 0 && page < _pageRects.Count && doc.CurrentMeasurement(drawing: true) is { } current)
        {
            // the direction of the piece being drawn, beside the value (No. 602)
            var direction = doc.MeasureHover is { } towards && towards.Page == page && doc.MeasurePoints.Count > 0 &&
                            !doc.MeasureArcPoints.Contains(doc.MeasurePoints.Count - 1)
                ? DirectionText(ToView(page, doc.MeasurePoints[^1].X, doc.MeasurePoints[^1].Y), ToView(page, towards.X, towards.Y))
                : null;
            DrawMeasurement(context, page, current, color, doc.MeasureShowSegments, doc.MeasureShowAngles, MeasureWidthDefault, MeasureFillDefault, live: true,
                direction);
            // the point would be an arc point: a small arc beside the mouse (No. 675)
            if (doc.MeasureHoverMakesArc && doc.MeasureHover is { } at) DrawArcSign(context, ToView(page, at.X, at.Y), color);
        }

        DrawCalibration(context, doc);

        // the chosen measurement: handles at its points, and where the bar goes
        Rect? bar = null;
        if (doc.SelectedMeasurement is { } chosen && chosen.Page < _pageRects.Count && _shown[chosen.Page])
        {
            var pen = new Pen(new SolidColorBrush(ColorOf(chosen.Rgb)), 1.5);
            var points = chosen.Measurement.Points.Select(p => ToView(chosen.Page, p.X, p.Y)).ToList();
            for (var i = 0; i < points.Count; i++)
            {
                // the chosen point filled (Del takes it out)
                var p = points[i];
                var fill = doc.SelectedMeasurePoint == i ? pen.Brush : Brushes.White;
                context.DrawRectangle(fill, pen, new Rect(p.X - MeasureHandleSize, p.Y - MeasureHandleSize, 2 * MeasureHandleSize, 2 * MeasureHandleSize));
            }
            if (chosen.Measurement.Arc is { } arc) points.AddRange(arc.Path.Select(p => ToView(chosen.Page, p.X, p.Y)));
            if (chosen.Measurement.Outline is { } outline) points.AddRange(outline.Path.Select(p => ToView(chosen.Page, p.X, p.Y)));
            if (points.Count > 0)
                bar = new Rect(new Point(points.Min(p => p.X), points.Min(p => p.Y)), new Point(points.Max(p => p.X), points.Max(p => p.Y)));
        }
        PlaceMeasureBar(bar);
        if (doc.MeasureHover is { } hover && hover.Page < _pageRects.Count && hover.Snap != SnapKind.None)
        {
            var at = ToView(hover.Page, hover.X, hover.Y);
            var pen = new Pen(new SolidColorBrush(color), 1.5);
            if (hover.Snap == SnapKind.Corner) context.DrawRectangle(null, pen, new Rect(at.X - 5, at.Y - 5, 10, 10));
            else context.DrawEllipse(null, pen, at, 5, 5);
        }

        // the magnifier: while calibrating, and while measuring when wanted — at the mouse, or at the point being dragged (No. 604)
        if (LoupeAt(doc) is { } loupe) DrawLoupe(context, doc, loupe.Page, loupe.X, loupe.Y);
        else if (_loupeTimer is { IsEnabled: true })
        {
            // hidden while gliding: no more redrawing for it; shown again, it glides on from where it was
            _loupeTimer.Stop();
            _loupeTicks = null;
        }
    }

    /// <summary>
    /// Where the magnifier looks (page space), or null: while calibrating always, while measuring with „Lupe beim Setzen der Punkte“ on (No.
    /// 604) — at the point a handle drags, else at the mouse over a page.
    /// </summary>
    private (int Page, double X, double Y)? LoupeAt(DocumentViewModel doc)
    {
        if (!doc.IsMeasuring || !doc.IsCalibrating && !doc.MeasureLoupe) return null;
        (int Page, double X, double Y)? at = _measureDrag is { } drag && drag.Index < drag.Row.Measurement.Points.Count
            ? (drag.Row.Page, drag.Row.Measurement.Points[drag.Index].X, drag.Row.Measurement.Points[drag.Index].Y)
            : doc.MeasureHover is { } hover ? (hover.Page, hover.X, hover.Y) : null;
        return at is { } a && a.Page >= 0 && a.Page < _pageRects.Count && _shown[a.Page] ? a : null;
    }

    private static readonly Color CalibrationColor = Color.FromRgb(0x0E, 0x7C, 0x86);
    private static readonly IPen CalibrationPen = new ImmutablePen(new ImmutableSolidColorBrush(CalibrationColor), 2, new ImmutableDashStyle([5, 3], 0));
    private static readonly IPen LoupeRing = new ImmutablePen(Brushes.White, 2.5);
    private static readonly IPen LoupeShadow = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 3.5);
    private static readonly IPen LoupeCrossShadow = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 3.5);
    private static readonly IPen LoupeCross = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xD9, 0x1E, 0x18)), 1.25);

    /// <summary>Radius of the magnifier and how much it enlarges (as in the image converter: 62 px, 4 ×).</summary>
    private const double LoupeRadius = 62, LoupeZoom = 4;

    /// <summary>Images of the magnifier (its own cache: the renderer's belongs to its threads).</summary>
    private readonly PdfKnead.Rendering.ImageCache _loupeImages = new();

    /// <summary>
    /// Calibrating (Ablaufplan 7.6 g): the line of known length — set, or from its start to the mouse — dashed with ticks at its ends, its
    /// length on the paper beside it; while points are set a magnifier beside the mouse shows the page enlarged with a crosshair.
    /// </summary>
    private void DrawCalibration(DrawingContext context, DocumentViewModel doc)
    {
        var points = doc.CalibrationPoints.ToList();
        var page = doc.CalibrationPage;
        if (doc.IsCalibrating && points.Count == 1 && doc.MeasureHover is { } towards && towards.Page == page) points.Add((towards.X, towards.Y));
        if (page >= 0 && page < _pageRects.Count && _shown[page] && points.Count >= 1)
        {
            var view = points.Select(p => ToView(page, p.X, p.Y)).ToList();
            if (view.Count == 2)
            {
                context.DrawLine(MeasureHalo, view[0], view[1]);
                context.DrawLine(CalibrationPen, view[0], view[1]);
                var n = Normal(view[0], view[1]) * 7;
                var solid = new Pen(new SolidColorBrush(CalibrationColor), 2);
                foreach (var p in view) context.DrawLine(solid, p - n, p + n);
                var paper = Math.Sqrt(Math.Pow(points[1].X - points[0].X, 2) + Math.Pow(points[1].Y - points[0].Y, 2)) * Measuring.MillimetresPerPoint
                    * (_doc!.Pages[page].Item.Page.UserUnit);
                var text = doc.HasCalibrationLine && doc.CalibrationLength.Trim().Length > 0
                    ? doc.CalibrationLength.Trim() + " " + doc.ScaleUnit
                    : Measuring.PaperOf(1).Distance.Format(paper);   // as everywhere: „40 mm“, „105.8 mm“
                if (doc.IsCalibrating) text += " · " + DirectionText(view[0], view[1]);
                Along(context, text, CalibrationColor, view[0], view[1], Up(view[0], view[1]));
            }
            else context.DrawEllipse(new SolidColorBrush(CalibrationColor), null, view[0], 3, 3);
        }
    }

    /// <summary>
    /// The magnifier (as in the image converter): a circle beside the point, where it is least in the way (<see cref="LoupeCentre"/>), the page
    /// around it drawn anew at four times the zoom (so lines stay sharp), a crosshair in the middle.
    /// </summary>
    private void DrawLoupe(DrawingContext context, DocumentViewModel doc, int page, double x, double y)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (LoupeImage(page, x, y, scaling) is not { } image) return;
        var at = ToView(page, x, y);
        var centre = LoupeCentre(doc, at);
        var (cx, cy) = (centre.X, centre.Y);
        var dest = new Rect(cx - LoupeRadius, cy - LoupeRadius, 2 * LoupeRadius, 2 * LoupeRadius);
        context.Custom(new LoupeDrawing(dest, image));
        // the lines of the measurements on the page, enlarged with it — the one being drawn too, so one can aim (No. 604)
        using (context.PushGeometryClip(new EllipseGeometry(dest)))
            foreach (var (points, color, closed) in LoupeLines(doc, page))
            {
                var pen = new Pen(new SolidColorBrush(color), 1.5);
                var mapped = points.Select(p => centre + (ToView(page, p.X, p.Y) - at) * LoupeZoom).ToList();
                for (var i = 1; i < mapped.Count; i++) context.DrawLine(pen, mapped[i - 1], mapped[i]);
                if (closed && mapped.Count > 2) context.DrawLine(pen, mapped[^1], mapped[0]);
            }
        foreach (var pen in new[] { LoupeCrossShadow, LoupeCross })
        {
            context.DrawLine(pen, new Point(cx - 12, cy), new Point(cx - 3, cy));
            context.DrawLine(pen, new Point(cx + 3, cy), new Point(cx + 12, cy));
            context.DrawLine(pen, new Point(cx, cy - 12), new Point(cx, cy - 3));
            context.DrawLine(pen, new Point(cx, cy + 3), new Point(cx, cy + 12));
        }
        context.DrawEllipse(null, LoupeShadow, centre, LoupeRadius, LoupeRadius);
        context.DrawEllipse(null, LoupeRing, centre, LoupeRadius, LoupeRadius);
    }

    /// <summary>The distance of the magnifier's centre from the point (view units).</summary>
    private const double LoupeDistance = (LoupeRadius + 26) * 1.38;

    /// <summary>How fast the magnifier glides to its new place: the time constant of the easing (ms; about four of them to arrive).</summary>
    private const double LoupeGlideMs = 65;

    /// <summary>The steps of its directions around the point (every 22.5°).</summary>
    private const int LoupeDirections = 16;

    /// <summary>The direction of the magnifier from the point as shown now (radians, view space, y down); NaN: not shown yet.</summary>
    private double _loupeAngle = double.NaN;

    /// <summary>The direction it is gliding to (<see cref="LoupeCentre"/>).</summary>
    private double _loupeTarget = double.NaN;

    /// <summary>When the magnifier last moved (for the glide); null: no glide running.</summary>
    private long? _loupeTicks;

    /// <summary>For tests: the glide only goes on by <see cref="AdvanceLoupe"/> with the times given (no clock, no timer).</summary>
    internal bool LoupeGlideByHand { get; set; }

    /// <summary>Redraws while the magnifier glides (the mouse may stand still meanwhile).</summary>
    private Avalonia.Threading.DispatcherTimer? _loupeTimer;

    /// <summary>The way the mouse has been moving lately (view units, smoothed): the magnifier keeps out of it.</summary>
    private Vector _mouseMotion;

    private static Vector Direction(double angle) => new(Math.Cos(angle), Math.Sin(angle));

    /// <summary>The difference from <paramref name="from"/> to <paramref name="to"/> the short way round (-π … π).</summary>
    private static double AngleBetween(double from, double to) => Math.IEEERemainder(to - from, 2 * Math.PI);

    /// <summary>
    /// Where the magnifier goes (decided on 9.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 664 and 684), decided as the mouse moves: it
    /// stays where it is as long as it is not in front of the mouse (where the mouse is heading: from the last point set to the mouse while
    /// drawing, else its recent movement) and inside the view — over lines it may lie and pass. Only when the mouse comes towards it does it
    /// give way, to the nearest direction clearly out of the way (leaving and landing have different bounds, so it does not swing to and fro at
    /// the edge), and it glides there round the point at the same distance (<see cref="AdvanceLoupe"/>), like pushed away by a magnet, so the
    /// eye can follow it. Before 684 it jumped between eight places, also to keep off lines, and had to be looked for.
    /// </summary>
    private void UpdateLoupeTarget(DocumentViewModel doc)
    {
        if (LoupeAt(doc) is not { } loupe) return;
        var at = ToView(loupe.Page, loupe.X, loupe.Y);
        var ahead = _mouseMotion;
        if (doc.MeasurePage == loupe.Page && doc.MeasurePoints.Count > 0 && _measureDrag is null)
        {
            var last = ToView(loupe.Page, doc.MeasurePoints[^1].X, doc.MeasurePoints[^1].Y);
            if (Span(last, at) > 4) ahead = at - last;
        }
        // a mouse hardly moving heads nowhere
        var heading = Length(ahead) > 1.5 ? Unit(ahead) : new Vector(0, 0);
        double Outside(double angle)
        {
            var centre = at + Direction(angle) * LoupeDistance;
            return Math.Max(0, LoupeRadius - centre.X) + Math.Max(0, centre.X + LoupeRadius - Bounds.Width) +
                   Math.Max(0, LoupeRadius - centre.Y) + Math.Max(0, centre.Y + LoupeRadius - Bounds.Height);
        }
        double Toward(double angle) => Vector.Dot(Direction(angle), heading);
        // first shown: to the upper left, unless that is in the way
        var current = double.IsNaN(_loupeTarget) ? -0.75 * Math.PI : _loupeTarget;
        if (Toward(current) <= LoupeLeaves && Outside(current) <= 0)
        {
            _loupeTarget = current;
            return;
        }
        // the nearest direction clearly out of the way, turning the short way (the way off the mouse's course first)
        var step = 2 * Math.PI / LoupeDirections;
        var away = Toward(current + step) <= Toward(current - step) ? 1 : -1;
        for (var k = 1; k <= LoupeDirections / 2; k++)
            foreach (var sign in new[] { away, -away })
            {
                var angle = current + sign * k * step;
                if (Toward(angle) <= LoupeLands && Outside(angle) <= 0)
                {
                    _loupeTarget = Math.IEEERemainder(angle, 2 * Math.PI);
                    return;
                }
            }
        // nowhere quite free (a small view): the least in the way
        var best = current;
        var bestScore = double.MaxValue;
        for (var k = 0; k < LoupeDirections; k++)
        {
            var angle = current + k * step;
            var score = Outside(angle) * 20 + 400 * Math.Max(0, Toward(angle));
            if (score < bestScore) (best, bestScore) = (angle, score);
        }
        _loupeTarget = Math.IEEERemainder(best, 2 * Math.PI);
    }

    /// <summary>The magnifier gives way once it lies this much in front of the mouse (cosine of the angle to its course: about 70°)…</summary>
    private const double LoupeLeaves = 0.35;

    /// <summary>…and lands where it lies clearly behind the side (about 95°).</summary>
    private const double LoupeLands = -0.1;

    /// <summary>Where the magnifier is drawn: round the point at <see cref="_loupeAngle"/>, glided on up to now.</summary>
    private Point LoupeCentre(DocumentViewModel doc, Point at)
    {
        if (double.IsNaN(_loupeTarget)) UpdateLoupeTarget(doc);
        if (!LoupeGlideByHand) AdvanceLoupe(System.Diagnostics.Stopwatch.GetTimestamp());
        return at + Direction(double.IsNaN(_loupeAngle) ? -0.75 * Math.PI : _loupeAngle) * LoupeDistance;
    }

    /// <summary>
    /// The glide of the magnifier towards <see cref="_loupeTarget"/> up to <paramref name="now"/> (Stopwatch ticks): eased, the short way round
    /// the point; the first time it is put there at once. While it glides a timer redraws the view; it stops once the magnifier has arrived.
    /// </summary>
    internal void AdvanceLoupe(long now)
    {
        if (double.IsNaN(_loupeTarget)) return;
        if (double.IsNaN(_loupeAngle))
        {
            _loupeAngle = _loupeTarget;
            _loupeTicks = null;
            return;
        }
        var left = AngleBetween(_loupeAngle, _loupeTarget);
        if (Math.Abs(left) < 0.004)
        {
            _loupeAngle = _loupeTarget;
            _loupeTicks = null;
            _loupeTimer?.Stop();
            return;
        }
        var elapsed = _loupeTicks is { } then ? (now - then) * 1000.0 / System.Diagnostics.Stopwatch.Frequency : 16;
        elapsed = Math.Clamp(elapsed, 0, 100);   // after a pause (the window hidden) not all at once
        _loupeAngle += left * (1 - Math.Exp(-elapsed / LoupeGlideMs));
        _loupeTicks = now;
        if (LoupeGlideByHand) return;
        _loupeTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(15), Avalonia.Threading.DispatcherPriority.Render,
            (_, _) => InvalidateVisual());
        if (!_loupeTimer.IsEnabled) _loupeTimer.Start();
    }

    /// <summary>Where the magnifier is now (for tests): its centre in the view, or null when it is not shown.</summary>
    internal Point? LoupeCentreShown => _doc is { } doc && LoupeAt(doc) is { } at && !double.IsNaN(_loupeAngle)
        ? ToView(at.Page, at.X, at.Y) + Direction(_loupeAngle) * LoupeDistance
        : null;

    /// <summary>Where the magnifier is going (for tests): its centre in the view once it has arrived, or null when it is not shown.</summary>
    internal Point? LoupeCentreTarget => _doc is { } doc && LoupeAt(doc) is { } at && !double.IsNaN(_loupeTarget)
        ? ToView(at.Page, at.X, at.Y) + Direction(_loupeTarget) * LoupeDistance
        : null;

    private static readonly IPen GuidePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x90, 0x6B, 0x72, 0x80)), 1,
        new ImmutableDashStyle([2, 3], 0));

    /// <summary>The straight direction the point snapped to: a thin dotted line through the previous point and on beyond the mouse.</summary>
    private void DrawStraightGuide(DrawingContext context, DocumentViewModel doc)
    {
        if (_straightGuide is not { } guide || guide.Page >= _pageRects.Count || !_shown[guide.Page]) return;
        var (a, b) = (ToView(guide.Page, guide.FromX, guide.FromY), ToView(guide.Page, guide.X, guide.Y));
        if (Span(a, b) < 1) return;
        var u = Unit(b - a);
        var reach = Bounds.Width + Bounds.Height;
        context.DrawLine(GuidePen, a - u * reach, a + u * reach);
    }

    /// <summary>
    /// The direction from <paramref name="a"/> to <paramref name="b"/> as seen on the screen, anticlockwise from the right (0° to the right,
    /// 90° up, 180° to the left, 270° down), to a tenth of a degree — „0°“, „90°“ when straight (No. 602).
    /// </summary>
    internal static string DirectionText(Point a, Point b)
    {
        var degrees = Math.Round(Math.Atan2(a.Y - b.Y, b.X - a.X) * 180 / Math.PI, 1);
        if (degrees < 0) degrees += 360;
        if (degrees >= 360 || degrees == 0) degrees = 0;   // also no „-0“
        return degrees.ToString("0.#", CultureInfo.CurrentCulture) + "°";
    }

    /// <summary>
    /// The picture in the magnifier around a point of the page (square, <paramref name="scaling"/> device pixels per view unit, the point in
    /// the middle): the page drawn anew at four times the zoom and on it the elements of the page — a picture put in (a floor plan), shapes,
    /// notes — as the view draws them above the page (docs/FEHLERGEDAECHTNIS.md, No. 603: before, they were missing, the magnifier stayed white
    /// over a picture put in). Null before the page is read.
    /// </summary>
    internal SKImage? LoupeImage(int page, double x, double y, double scaling)
    {
        if (_doc is null || page < 0 || page >= _doc.Pages.Count || _doc.Renderer.TryGetDisplayList(_doc.Pages[page].Key) is not { } list) return null;
        var at = ToView(page, x, y);
        var size = (int)Math.Ceiling(2 * LoupeRadius * scaling);
        // page → pixels of the magnifier: as on screen, enlarged about the point, the point in the middle
        var toDevice = PageToView(page) * PdfMatrix.Translate(-at.X, -at.Y) * PdfMatrix.Scale(LoupeZoom * scaling, LoupeZoom * scaling)
            * PdfMatrix.Translate(size / 2.0, size / 2.0);
        var item = _doc.Pages[page].Item;
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(PdfKnead.Rendering.SkiaRenderer.Paper);
        try
        {
            PdfKnead.Rendering.SkiaRenderer.RenderTile(surface.Canvas, list, item.Source, toDevice, new SKRectI(0, 0, size, size), _loupeImages);
            // the elements in their order; those that keep their size (notes) about their anchor, as in the view (No. 577)
            var until = System.Diagnostics.Stopwatch.GetTimestamp()
                + (long)(PdfKnead.Rendering.ElementOverlay.FrameTime.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
            foreach (var element in item.Elements)
            {
                if (_doc.EditingElement is { } editing && editing.Page == page && ReferenceEquals(editing.Element, element)) continue;
                if (_doc.IsBoundDimension(element)) continue;   // a dimension as a measurement: among the lines below (No. 605)
                var matrix = KeepSizeMatrix(element) is { } keep ? keep * toDevice : toDevice;
                PdfKnead.Rendering.ElementOverlay.Draw(surface.Canvas, _doc.Pages[page].Page, [element], matrix, _loupeImages,
                    _doc.Renderer.TryGetElementList, until, _doc.Renderer.OverprintPreview);
            }
        }
        catch (Exception e) when (e is Core.Pdf.PdfException or InvalidOperationException or ArgumentException)
        {
        }
        return surface.Snapshot();
    }

    /// <summary>Whether a straight guide is shown (for tests).</summary>
    internal bool StraightGuideShown => _straightGuide is not null;

    /// <summary>The lines shown in the magnifier: the measurements on the page (arcs along their arc), the one being drawn to the mouse,
    /// the calibration line — each with its colour and whether it is closed.</summary>
    private List<(IReadOnlyList<(double X, double Y)> Points, Color Color, bool Closed)> LoupeLines(DocumentViewModel doc, int page)
    {
        var lines = new List<(IReadOnlyList<(double X, double Y)>, Color, bool)>();
        foreach (var row in doc.Measurements)
            if (row.Page == page && !doc.IsContinuing(row))
                lines.Add((Course(row.Measurement), ColorOf(row.Rgb), row.Measurement.Kind == MeasureKind.Area && row.Measurement.Arc is null));
        if (doc.MeasurePage == page && doc.CurrentMeasurement(drawing: true) is { } current)
            lines.Add((Course(current), ColorOf(doc.MeasureColor), false));
        if (doc.CalibrationPage == page)
        {
            var points = doc.CalibrationPoints.ToList();
            if (doc.IsCalibrating && points.Count == 1 && doc.MeasureHover is { } towards && towards.Page == page) points.Add((towards.X, towards.Y));
            if (points.Count == 2) lines.Add((points, CalibrationColor, false));
        }
        return lines;
    }

    /// <summary>The course of a measurement in page space: of an arc the arc, of a polyline or area its pieces (arcs along their arc), else its points.</summary>
    private static IReadOnlyList<(double X, double Y)> Course(Measurement m) => m.Arc?.Path ?? m.Outline?.Path ?? m.Points;

    /// <summary>Whether the magnifier is shown (for tests).</summary>
    internal bool LoupeShown => _doc is { } doc && LoupeAt(doc) is not null;

    /// <summary>Draws the image of the magnifier into its circle; owns the image.</summary>
    private sealed class LoupeDrawing(Rect bounds, SKImage image) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() => image.Dispose();

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            using var circle = new SKPath();
            circle.AddOval(new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom));
            canvas.ClipPath(circle, antialias: true);
            canvas.DrawImage(image, new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            canvas.Restore();
        }
    }

    /// <summary>Tells the window where the chosen measurement is (once per change, after drawing).</summary>
    private void PlaceMeasureBar(Rect? bar)
    {
        if (bar == _measureBar) return;
        _measureBar = bar;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => MeasureBarPlaced?.Invoke(_measureBar));
    }

    /// <summary>Where the chosen measurement is in the view, or null (for tests).</summary>
    internal Rect? MeasureBarArea => _measureBar;

    /// <summary>The middle of the value label of a measurement in the view, or null (for tests: a click there chooses it).</summary>
    internal Point? MeasureLabelCentre(MeasureRow row) => _measureHits.FirstOrDefault(h => ReferenceEquals(h.Row, row)) is { Row: not null } hit ? hit.Box.Center : null;

    /// <summary>The handle of a point of a measurement in the view (for tests).</summary>
    internal Point MeasureHandle(MeasureRow row, int index) => ToView(row.Page, row.Measurement.Points[index].X, row.Measurement.Points[index].Y);

    private const double MeasureWidthDefault = 1.5;
    private const double MeasureFillDefault = 0.2;

    private Point ToView(int page, double x, double y)
    {
        var (vx, vy) = PageToView(page).Transform(x, y);
        return new Point(vx, vy);
    }

    /// <summary>
    /// The course of a polyline or area on the screen: its path (arcs divided in steps of half a degree) mapped into the view and simplified
    /// to within 0.3 units (Douglas–Peucker) — what the screen can show of it. Kept per outline as long as the view does not move or zoom:
    /// drawing it, filling it and placing its labels (which look at the whole course) cost per frame what the screen needs, not what the
    /// measurement holds. Before an area with 40 arcs (15 000 points) cost about 190 ms per frame, and dragging anything else on the page
    /// stalled (reported on 9.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 662).
    /// </summary>
    private static List<Point> ViewCourse(MeasureOutline outline, PdfMatrix toView)
    {
        if (ViewCourses.TryGetValue(outline, out var kept) && kept.ToView == toView) return kept.Course;
        var mapped = new List<Point>(outline.Path.Count);
        foreach (var (x, y) in outline.Path)
        {
            var (vx, vy) = toView.Transform(x, y);
            mapped.Add(new Point(vx, vy));
        }
        var course = Simplified(mapped, CourseTolerance);
        ViewCourses.AddOrUpdate(outline, new ViewCourseEntry(toView, course));
        return course;
    }

    /// <summary>How far the course on the screen may lie from the measured one (view units, about a third of a pixel).</summary>
    private const double CourseTolerance = 0.3;

    private sealed record ViewCourseEntry(PdfMatrix ToView, List<Point> Course);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeasureOutline, ViewCourseEntry> ViewCourses = new();

    /// <summary>
    /// A line of points simplified (Douglas–Peucker, iterative): the points left lie within <paramref name="tolerance"/> of the line; the first
    /// and the last always stay.
    /// </summary>
    internal static List<Point> Simplified(IReadOnlyList<Point> points, double tolerance)
    {
        if (points.Count <= 2) return [.. points];
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int From, int To)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (from, to) = stack.Pop();
            if (to - from < 2) continue;
            var (a, b) = (points[from], points[to]);
            double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
            var (farthest, distance) = (-1, 0.0);
            for (var i = from + 1; i < to; i++)
            {
                var p = points[i];
                var d = length < 1e-9 ? Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y)) : Math.Abs(dy * (p.X - a.X) - dx * (p.Y - a.Y)) / length;
                if (d > distance) (farthest, distance) = (i, d);
            }
            if (farthest < 0 || distance <= tolerance) continue;
            keep[farthest] = true;
            stack.Push((from, farthest));
            stack.Push((farthest, to));
        }
        var result = new List<Point>();
        for (var i = 0; i < points.Count; i++)
            if (keep[i]) result.Add(points[i]);
        return result;
    }

    /// <summary>The course of a measurement as the screen draws it (for tests): its points in the view.</summary>
    internal IReadOnlyList<Point>? MeasureCourseInView(MeasureRow row) =>
        row.Measurement.Outline is { } outline && row.Page < _pageRects.Count ? ViewCourse(outline, PageToView(row.Page)) : null;

    /// <summary>Draws a measurement; returns where its value label is (null without one).</summary>
    /// <param name="direction">While drawing: the direction of the piece to the mouse, shown beside the value (null: none).</param>
    private Rect? DrawMeasurement(DrawingContext context, int page, Measurement measurement, Color color, bool segments, bool angles, double width,
        double fill, bool live, string? direction = null, (double X, double Y)? labelAt = null)
    {
        var (kind, points, text) = (measurement.Kind, measurement.Points, measurement.Text);
        if (points.Count == 0) return null;
        if (direction is not null && kind is MeasureKind.Length or MeasureKind.Polyline && text.Length > 0) text += " · " + direction;
        var brush = new SolidColorBrush(color);
        var pen = live ? new Pen(brush, width, new DashStyle([4, 3], 0)) : new Pen(brush, width);
        var toView = PageToView(page);
        Point ToV((double X, double Y) p)
        {
            var (vx, vy) = toView.Transform(p.X, p.Y);
            return new Point(vx, vy);
        }
        var view = points.Select(ToV).ToList();
        // a polyline or area along its pieces — arcs along their arc (Ablaufplan 7.6 j), as the screen shows them (ViewCourse); else from point
        // to point
        var outline = kind is MeasureKind.Polyline or MeasureKind.Area ? measurement.Outline : null;
        var course = outline is null ? view : ViewCourse(outline, toView);
        var area = kind == MeasureKind.Area && view.Count >= 3;
        if (area)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(course[0], true);
                foreach (var p in course.Skip(1)) g.LineTo(p);
                g.EndFigure(true);
            }
            if (fill > 0) context.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)Math.Round(fill * 255), color.R, color.G, color.B)), null, geometry);
        }

        // the lines: of an arc the arc through the points, otherwise from point to point (an area closed)
        if (kind == MeasureKind.Radius)
        {
            if (measurement.Arc is { } arc)
            {
                Polyline(context, pen, [.. arc.Path.Select(p => ToView(page, p.X, p.Y))]);
                var centre = ToView(page, arc.Centre.X, arc.Centre.Y);
                context.DrawLine(pen, centre - new Vector(4, 4), centre + new Vector(4, 4));
                context.DrawLine(pen, centre - new Vector(4, -4), centre + new Vector(4, -4));
            }
        }
        else if (kind == MeasureKind.Length && view.Count >= 2 && Span(view[0], view[^1]) > 1)
        {
            // a length: the line ends inside its heads — or, too short for both between the ends, runs on beyond them and the heads come from
            // outside (as in technical drawings, ISO 129-1; No. 685 — before they lay on top of each other)
            var (from, to, outside) = LengthLine(view[0], view[^1], ArrowLength(width));
            Polyline(context, pen, [from, to]);
            var d = Unit(view[^1] - view[0]);
            Arrow(context, brush, view[0], outside ? view[0] - d : view[^1], width);
            Arrow(context, brush, view[^1], outside ? view[^1] + d : view[0], width);
            var n = Normal(view[0], view[^1]) * 6;
            context.DrawLine(pen, view[0] - n, view[0] + n);
            context.DrawLine(pen, view[^1] - n, view[^1] + n);
        }
        else if (kind == MeasureKind.Polyline && course.Count >= 2)
        {
            // the arrow in the direction the last piece arrives (of an arc its tangent); the line ends inside it
            Vector? arriving = outline is { Pieces.Count: > 0 } && points.Count >= 2 ? ViewDirection(page, points[^1], outline.Pieces[^1].EndDirection)
                : view.Count >= 2 && Span(view[^2], view[^1]) > 1 ? Unit(view[^1] - view[^2]) : null;
            var line = course.ToList();
            if (arriving is { } u && Span(line[^2], line[^1]) > ArrowLength(width)) line[^1] -= u * ArrowLength(width) * 0.7;
            Polyline(context, pen, line);
            if (arriving is { } w) Arrow(context, brush, view[^1], view[^1] - w, width);
        }
        else Polyline(context, pen, area ? [.. course, course[0]] : course);
        if (kind != MeasureKind.Length)
            for (var i = 0; i < view.Count; i++)
            {
                var p = view[i];
                // an arc point round, the other points square
                if (measurement.ArcPoints.Contains(i)) context.DrawEllipse(Brushes.White, new Pen(brush, 1.2), p, 2.6, 2.6);
                else context.DrawRectangle(brush, null, new Rect(p.X - 2, p.Y - 2, 4, 4));
            }

        // the length of each piece, along it at its middle (an arc with its radius) — of an area on the outside
        if (segments && outline is not null)
            for (var i = 0; i < measurement.Segments.Count && i < outline.Pieces.Count; i++)
            {
                if (measurement.Segments[i].Length == 0) continue;
                var piece = outline.Pieces[i];
                // its length on the screen: along its path in page space, by the scale of the view (no point mapped one by one)
                var onPage = 0.0;
                for (var k = 0; k + 1 < piece.Path.Count; k++)
                    onPage += Math.Sqrt((piece.Path[k + 1].X - piece.Path[k].X) * (piece.Path[k + 1].X - piece.Path[k].X) +
                                        (piece.Path[k + 1].Y - piece.Path[k].Y) * (piece.Path[k + 1].Y - piece.Path[k].Y));
                var shown = onPage * Math.Sqrt(Math.Abs(toView.A * toView.D - toView.B * toView.C));
                if (shown < 24) continue;
                var middle = ToView(page, piece.Middle.X, piece.Middle.Y);
                var along = ViewDirection(page, piece.Middle, piece.Direction) * Math.Min(shown, 48) / 2;
                var (a, b) = (middle - along, middle + along);
                Along(context, measurement.Segments[i], color, a, b, area ? Outside(course, a, b) : Up(a, b));
            }

        // the angle at each bend, inside it, with a small arc — between the directions of the pieces meeting there (of an arc its tangent)
        if (angles && outline is not null)
            for (var i = 0; i < measurement.Corners.Count && i < view.Count; i++)
            {
                if (measurement.Corners[i].Length == 0) continue;
                if (outline.Pieces.FirstOrDefault(p => p.To == i) is not { } incoming || outline.Pieces.FirstOrDefault(p => p.From == i) is not { } outgoing) continue;
                var here = view[i];
                var (u, w) = (-ViewDirection(page, points[i], incoming.EndDirection), ViewDirection(page, points[i], outgoing.StartDirection));
                var bisector = u + w;
                bisector = Length(bisector) < 1e-6 ? new Vector(-u.Y, u.X) : Unit(bisector);
                if (area && !Inside(course, here + bisector * 3)) bisector = -bisector;
                AngleArc(context, pen, here, u, w, bisector, 12);
                Centred(context, measurement.Corners[i], color, here + bisector * 26);
            }

        // the value: at its own place, or where the user moved it — within the rules (MeasureLabel; design decision, No. 703)
        if (text.Length == 0 || view.Count < 2 && kind != MeasureKind.Radius) return null;
        var label = Label(text, color);
        var (lw, lh) = (label.Width + 8, label.Height + 2);
        double angle = 0;
        Point home;
        IReadOnlyList<Point> keep = view;   // the course the label keeps to
        switch (kind)
        {
            case MeasureKind.Length:
            {
                // along the line, never upside down, beside it
                var (a, b) = (view[0], view[^1]);
                angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
                if (angle > Math.PI / 2) angle -= Math.PI;
                else if (angle <= -Math.PI / 2) angle += Math.PI;
                home = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2) + Up(a, b) * (lh / 2 + 3);
                keep = [a, b];
                break;
            }
            case MeasureKind.Polyline:
                // the total to the right of the end
                home = view[^1] + new Vector(10 + lw / 2, 0);
                keep = course;
                break;
            case MeasureKind.Area:
                if (direction is not null) Beside(context, direction, color, view[^1] + new Vector(10, 0));
                home = Centroid(course);
                keep = course.Count > 2 && course[0] == course[^1] ? course.Take(course.Count - 1).ToList() : course;
                break;
            case MeasureKind.Angle when view.Count >= 3:
            {
                var (u, w) = (Unit(view[0] - view[1]), Unit(view[2] - view[1]));
                var bisector = u + w;
                bisector = Length(bisector) < 1e-6 ? new Vector(-u.Y, u.X) : Unit(bisector);
                AngleArc(context, pen, view[1], u, w, bisector, 16);
                home = view[1] + bisector * 34;
                break;
            }
            case MeasureKind.Radius when measurement.Arc is { Path.Count: > 1 } arc:
            {
                var middle = ToView(page, arc.Path[arc.Path.Count / 2].X, arc.Path[arc.Path.Count / 2].Y);
                var centre = ToView(page, arc.Centre.X, arc.Centre.Y);
                var outward = Span(middle, centre) > 1 ? Unit(middle - centre) : new Vector(0, -1);
                if (!live) context.DrawLine(new Pen(brush, 1, new DashStyle([3, 3], 0)), centre, middle);
                home = middle + outward * 18;
                keep = [.. arc.Path.Select(p => ToView(page, p.X, p.Y))];
                break;
            }
            default:
                home = view[^1] + new Vector(10 + lw / 2, 0);
                break;
        }
        var place = home;
        if (!live && labelAt is { } wanted)
        {
            var target = ToV(wanted);
            var kept = MeasureLabel.Clamp(kind, [.. keep.Select(p => (p.X, p.Y))], (target.X, target.Y), lw, lh, angle);
            place = new Point(kept.X, kept.Y);
        }
        _lastLabel = (home, place);
        return DrawLabel(context, label, color, Matrix.CreateTranslation(-lw / 2, -lh / 2) * Matrix.CreateRotation(angle) * Matrix.CreateTranslation(place.X, place.Y));
    }

    /// <summary>A direction of page space at a point as a direction in the view (length 1).</summary>
    private Vector ViewDirection(int page, (double X, double Y) at, (double X, double Y) direction)
    {
        var d = ToView(page, at.X + direction.X, at.Y + direction.Y) - ToView(page, at.X, at.Y);
        return Length(d) > 1e-9 ? Unit(d) : new Vector(1, 0);
    }

    /// <summary>
    /// A line of a measurement: a white halo (it stays visible on a dark plan), a faint glow of its colour (design decision, No. 685; as the
    /// saved dimension draws it), the line.
    /// </summary>
    private static void Polyline(DrawingContext context, IPen pen, IReadOnlyList<Point> points)
    {
        for (var i = 0; i + 1 < points.Count; i++) context.DrawLine(MeasureHalo, points[i], points[i + 1]);
        if (pen.Brush is ISolidColorBrush { Color: var c })
        {
            var glow = new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * PdfKnead.Core.Pdf.Editing.ElementAppearance.DimensionGlowOpacity), c.R, c.G, c.B)),
                pen.Thickness + 2.4 * 1.33, lineCap: PenLineCap.Round);
            for (var i = 0; i + 1 < points.Count; i++) context.DrawLine(glow, points[i], points[i + 1]);
        }
        for (var i = 0; i + 1 < points.Count; i++) context.DrawLine(pen, points[i], points[i + 1]);
    }

    /// <summary>The length of an arrow head on the screen for a line width (view units): in the proportion of the saved dimension (No. 685).</summary>
    private static double ArrowLength(double width) => PdfKnead.Core.Pdf.Editing.ElementAppearance.DimensionHeadLength(width) * 1.2;

    /// <summary>
    /// The line of a length between <paramref name="a"/> and <paramref name="b"/> with arrow heads of <paramref name="head"/>: ending inside the
    /// heads when both fit between the ends (with a little room), else running on beyond both ends, the heads then outside (No. 685).
    /// </summary>
    internal static (Point From, Point To, bool Outside) LengthLine(Point a, Point b, double head)
    {
        var d = Unit(b - a);
        if (Span(a, b) >= 2 * head + 4) return (a + d * head * 0.7, b - d * head * 0.7, false);
        return (a - d * (head + 6), b + d * (head + 6), true);
    }

    /// <summary>A filled arrow head of 30° (as in technical drawings) at <paramref name="tip"/>, pointing away from <paramref name="from"/>.</summary>
    private static void Arrow(DrawingContext context, IBrush brush, Point tip, Point from, double width)
    {
        var d = Unit(tip - from);
        var n = new Vector(-d.Y, d.X);
        var length = ArrowLength(width);
        var half = length * PdfKnead.Core.Pdf.Editing.ElementAppearance.DimensionHeadSlope;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(tip, true);
            g.LineTo(tip - d * length + n * half);
            g.LineTo(tip - d * length - n * half);
            g.EndFigure(true);
        }
        // a gradient along the head: the colour at the tip a little deeper, lighter towards the base (as the saved dimension; No. 685)
        IBrush fill = brush;
        if (brush is ISolidColorBrush { Color: var c })
        {
            var (dark, light) = PdfKnead.Core.Pdf.Editing.ElementAppearance.HeadColors((c.R << 16) | (c.G << 8) | c.B);
            static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            var baseMiddle = tip - d * length;
            fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(tip, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(baseMiddle, RelativeUnit.Absolute),
                GradientStops = [new GradientStop(Color.FromArgb(c.A, B(dark[0]), B(dark[1]), B(dark[2])), 0),
                    new GradientStop(Color.FromArgb(c.A, B(light[0]), B(light[1]), B(light[2])), 1)],
            };
        }
        context.DrawGeometry(fill, null, geometry);
    }

    /// <summary>A small arc at a bend, from one piece to the other on the side of <paramref name="bisector"/>.</summary>
    private static void AngleArc(DrawingContext context, IPen pen, Point here, Vector u, Vector w, Vector bisector, double radius)
    {
        double a = Math.Atan2(u.Y, u.X), b = Math.Atan2(w.Y, w.X), c = Math.Atan2(bisector.Y, bisector.X);
        static double Positive(double x) => (x % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
        var sweep = Positive(b - a);
        if (Positive(c - a) > sweep) sweep -= 2 * Math.PI;   // the other way round, through the bisector
        var arc = new List<Point>();
        for (var k = 0; k <= 16; k++)
        {
            var t = a + sweep * k / 16;
            arc.Add(here + new Vector(Math.Cos(t), Math.Sin(t)) * radius);
        }
        for (var i = 0; i + 1 < arc.Count; i++) context.DrawLine(pen, arc[i], arc[i + 1]);
    }

    /// <summary>
    /// A label's text laid out — kept by text and colour (the labels of a measurement are the same from frame to frame; laying out 160 of
    /// them anew cost about 50 ms per frame, No. 662); the store is emptied once it holds many.
    /// </summary>
    private static FormattedText Label(string text, Color color)
    {
        if (LaidOut.TryGetValue((text, color), out var kept)) return kept;
        if (LaidOut.Count >= 4000) LaidOut.Clear();
        var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 11.5, new SolidColorBrush(color));
        LaidOut[(text, color)] = label;
        return label;
    }

    /// <summary>The labels laid out (<see cref="Label"/>); only on the thread of the user interface.</summary>
    private static readonly Dictionary<(string Text, Color Color), FormattedText> LaidOut = [];

    /// <summary>The pen of a label's frame per colour of the measurement.</summary>
    private static IPen LabelFramePen(Color color)
    {
        if (Frames.TryGetValue(color, out var kept)) return kept;
        if (Frames.Count >= 256) Frames.Clear();
        return Frames[color] = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x90, color.R, color.G, color.B)), 1);
    }

    private static readonly Dictionary<Color, IPen> Frames = [];

    /// <summary>
    /// A label on a white field with a frame in the colour of the measurement, drawn with <paramref name="transform"/>; returns its bounds in
    /// the view.
    /// </summary>
    private static Rect DrawLabel(DrawingContext context, FormattedText label, Color color, Matrix transform)
    {
        var box = new Rect(0, 0, label.Width + 8, label.Height + 2);
        using (context.PushTransform(transform))
        {
            context.DrawRectangle(LabelBack, LabelFramePen(color), box, 3, 3);
            context.DrawText(label, new Point(4, 1));
        }
        return box.TransformToAABB(transform);
    }

    /// <summary>A label centred on a point.</summary>
    private static Rect Centred(DrawingContext context, string text, Color color, Point at)
    {
        var label = Label(text, color);
        return DrawLabel(context, label, color, Matrix.CreateTranslation(at.X - (label.Width + 8) / 2, at.Y - (label.Height + 2) / 2));
    }

    /// <summary>A label beginning at a point (to its right), centred in height.</summary>
    private static Rect Beside(DrawingContext context, string text, Color color, Point at)
    {
        var label = Label(text, color);
        return DrawLabel(context, label, color, Matrix.CreateTranslation(at.X, at.Y - (label.Height + 2) / 2));
    }

    /// <summary>A label along the piece from <paramref name="a"/> to <paramref name="b"/>, beside it on the side of <paramref name="side"/>, never upside down.</summary>
    private static Rect Along(DrawingContext context, string text, Color color, Point a, Point b, Vector side)
    {
        var label = Label(text, color);
        var (w, h) = (label.Width + 8, label.Height + 2);
        var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
        if (angle > Math.PI / 2) angle -= Math.PI;
        else if (angle <= -Math.PI / 2) angle += Math.PI;
        var centre = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2) + side * (h / 2 + 3);
        return DrawLabel(context, label, color, Matrix.CreateTranslation(-w / 2, -h / 2) * Matrix.CreateRotation(angle) * Matrix.CreateTranslation(centre.X, centre.Y));
    }

    /// <summary>The straight distance between two points of the view.</summary>
    private static double Span(Point a, Point b) => Length(a - b);

    private static double Length(Vector v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);

    private static Vector Unit(Vector v) => Length(v) is var l and > 0 ? v / l : new Vector(1, 0);

    /// <summary>The unit normal of a piece.</summary>
    private static Vector Normal(Point a, Point b) => Unit(new Vector(-(b.Y - a.Y), b.X - a.X));

    /// <summary>The normal of a piece that points up on the screen (left for an upright piece).</summary>
    private static Vector Up(Point a, Point b)
    {
        var n = Normal(a, b);
        return n.Y < 0 || n.Y == 0 && n.X < 0 ? n : -n;
    }

    /// <summary>The normal of an edge of a polygon that points out of it.</summary>
    private static Vector Outside(IReadOnlyList<Point> polygon, Point a, Point b)
    {
        var n = Normal(a, b);
        var middle = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        return Inside(polygon, middle + n * 3) ? -n : n;
    }

    /// <summary>Whether a point lies inside a polygon (even–odd).</summary>
    private static bool Inside(IReadOnlyList<Point> polygon, Point p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if (a.Y > p.Y != b.Y > p.Y && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>The centroid of a polygon (its middle by area; the mean of the points if it has none).</summary>
    private static Point Centroid(IReadOnlyList<Point> polygon)
    {
        double twice = 0, cx = 0, cy = 0;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var cross = polygon[j].X * polygon[i].Y - polygon[i].X * polygon[j].Y;
            twice += cross;
            cx += (polygon[j].X + polygon[i].X) * cross;
            cy += (polygon[j].Y + polygon[i].Y) * cross;
        }
        if (Math.Abs(twice) < 1e-9) return new Point(polygon.Average(p => p.X), polygon.Average(p => p.Y));
        return new Point(cx / (3 * twice), cy / (3 * twice));
    }

    /// <summary>
    /// A small arc below and to the right of the mouse (the next point will be an arc point; No. 675): a half circle of 6 pixels' radius in the
    /// colour of the measurement on a white halo, so that it shows on any page.
    /// </summary>
    private static void DrawArcSign(DrawingContext context, Point mouse, Color color)
    {
        var centre = mouse + new Point(16, 16);
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(centre + new Point(-6, 2), false);
            g.ArcTo(centre + new Point(6, 2), new Size(6, 6), 0, false, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(Brushes.White, 4.5, lineCap: PenLineCap.Round), arc);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2, lineCap: PenLineCap.Round), arc);
    }
}
