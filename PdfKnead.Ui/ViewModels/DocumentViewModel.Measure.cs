using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Localization;

namespace PdfKnead.ViewModels;

/// <summary>
/// A finished measurement of this session, shown on its page and in the area until the tool is switched off — with its own colour, and
/// whether the lengths of its pieces and the angles at its points are shown (Ablaufplan 7.6 e; docs/FEHLERGEDAECHTNIS.md, No. 585). Or a
/// dimension on a page (<see cref="Element"/>; Ablaufplan 7.6 h, No. 605): then each change is saved on the page.
/// </summary>
public sealed partial class MeasureRow : ObservableObject
{
    /// <param name="page">The page (index).</param>
    /// <param name="measurement">What was measured, with its text.</param>
    /// <param name="color">Its colour.</param>
    /// <param name="showSegments">Whether the lengths of the pieces are shown.</param>
    /// <param name="showAngles">Whether the angles at the points are shown.</param>
    public MeasureRow(int page, Measurement measurement, FieldColorOption? color, bool showSegments = false, bool showAngles = false, int pageId = -1)
    {
        Page = page;
        PageId = pageId;
        Measurement = measurement;
        Color = color;
        Rgb = color?.Rgb ?? 0xEA580C;
        ShowSegments = showSegments;
        ShowAngles = showAngles;
    }

    /// <summary>The page (index); follows the page when pages move (undo, delete before it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Line))]
    public partial int Page { get; set; }

    /// <summary>The identity of the page (<see cref="Core.Pdf.Editing.PageItem.Id"/>), to find it again after pages moved.</summary>
    public int PageId { get; internal set; }

    /// <summary>What was measured, with its text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Line), nameof(HasPieces), nameof(IsArea), nameof(CanSwitchArc), nameof(SwitchArcText), nameof(CanContinue))]
    public partial Measurement Measurement { get; set; }

    /// <summary>Its colour as chosen; null for a colour of a dimension that is not in the list (<see cref="Rgb"/> keeps it).</summary>
    [ObservableProperty]
    public partial FieldColorOption? Color { get; set; }

    partial void OnColorChanged(FieldColorOption? value)
    {
        if (value?.Rgb is { } rgb) Rgb = rgb;
    }

    /// <summary>Its colour exactly (0xRRGGBB) — also one of a dimension of another program that is not in the list.</summary>
    public int Rgb { get; internal set; }

    /// <summary>Whether the length of each piece is shown at it.</summary>
    [ObservableProperty]
    public partial bool ShowSegments { get; set; }

    /// <summary>Whether the angle at each point is shown.</summary>
    [ObservableProperty]
    public partial bool ShowAngles { get; set; }

    /// <summary>The width of its lines (Ablaufplan 7.6 e3); null for a width of a dimension that is not in the list.</summary>
    [ObservableProperty]
    public partial Choice<double>? Width { get; set; } = DocumentViewModel.MeasureWidths[1];

    partial void OnWidthChanged(Choice<double>? value)
    {
        if (value is not null) StrokeWidth = value.Value;
    }

    /// <summary>The width of its lines exactly, in points.</summary>
    public double StrokeWidth { get; internal set; } = DocumentViewModel.MeasureWidths[1].Value;

    /// <summary>How strongly an area is filled (Ablaufplan 7.6 e3); null for a fill of a dimension that is not in the list.</summary>
    [ObservableProperty]
    public partial Choice<double>? FillOpacity { get; set; } = DocumentViewModel.MeasureOpacities[1];

    partial void OnFillOpacityChanged(Choice<double>? value)
    {
        if (value is not null) Fill = value.Value;
    }

    /// <summary>How strongly an area is filled exactly (0–1).</summary>
    public double Fill { get; internal set; } = DocumentViewModel.MeasureOpacities[1].Value;

    /// <summary>
    /// The dimension on the page this measurement is (Ablaufplan 7.6 h; docs/FEHLERGEDAECHTNIS.md, No. 605): one of PDF Knead, or an annotation
    /// of another program; null for a measurement of this session only.
    /// </summary>
    public PageElement? Element { get; private set; }

    /// <summary>Whether it is a dimension on its page (saved with the file).</summary>
    public bool IsSaved => Element is not null;

    /// <summary>Whether it is a measurement of this session only (it can be inserted as a dimension).</summary>
    public bool IsSession => Element is null;

    /// <summary>The scale of the dimension itself (/Measure); null without one.</summary>
    internal RectilinearMeasure? OwnMeasure { get; private set; }

    /// <summary>The points of the dimension as on its page (to tell whether a drag moved one).</summary>
    internal IReadOnlyList<(double X, double Y)> SavedPoints { get; private set; } = [];

    /// <summary>The place of the dimension among the elements of its page (to find it again when it was replaced there).</summary>
    internal int ElementIndex { get; private set; } = -1;

    /// <summary>
    /// Where the user moved the label of its value to (its middle, page space; design decision, docs/FEHLERGEDAECHTNIS.md, No. 703); null: its own
    /// place. The view and the saved dimension keep it within the rules of <see cref="Core.Pdf.Document.MeasureLabel"/>.
    /// </summary>
    public (double X, double Y)? LabelAt { get; internal set; }

    /// <summary>The label's place as the dimension on the page has it (to tell whether a drag moved it).</summary>
    internal (double X, double Y)? SavedLabelAt { get; set; }

    /// <summary>Makes it the measurement of a dimension on its page.</summary>
    internal void Bind(PageElement element, RectilinearMeasure? measure, IReadOnlyList<(double X, double Y)> points, int index)
    {
        var was = Element is not null;
        Element = element;
        OwnMeasure = measure;
        SavedPoints = points;
        ElementIndex = index;
        if (was) return;
        OnPropertyChanged(nameof(IsSaved));
        OnPropertyChanged(nameof(IsSession));
        OnPropertyChanged(nameof(Line));
    }

    /// <summary>Whether it is an area (only then the fill means something).</summary>
    public bool IsArea => Measurement.Kind == MeasureKind.Area;

    /// <summary>Whether it can be taken as an arc (a polyline of three or more points) or back as a polyline (an arc).</summary>
    public bool CanSwitchArc => Measurement.Kind == MeasureKind.Radius || Measurement.Kind == MeasureKind.Polyline && Measurement.Points.Count >= 3;

    /// <summary>„Als Bogen“ or „Als Linienzug“.</summary>
    public string SwitchArcText => Measurement.Kind == MeasureKind.Radius ? Tr.T("Als Linienzug") : Tr.T("Als Bogen");

    /// <summary>Whether it has pieces and bends (a polyline or an area) — only then the two switches mean something.</summary>
    public bool HasPieces => Measurement.Kind is MeasureKind.Polyline or MeasureKind.Area;

    /// <summary>Whether it can be continued at its start or end (a polyline; Ablaufplan 7.6 l).</summary>
    public bool CanContinue => Measurement.Kind == MeasureKind.Polyline;

    /// <summary>„Länge — 5 m (Seite 2)“, of a dimension „Länge — 5 m (Seite 2, Bemaßung)“.</summary>
    public string Line => IsSaved
        ? Tr.T("{0} — {1} (Seite {2}, Bemaßung)", DocumentViewModel.KindName(Measurement.Kind), Measurement.Text, Page + 1)
        : Tr.T("{0} — {1} (Seite {2})", DocumentViewModel.KindName(Measurement.Kind), Measurement.Text, Page + 1);
}

/// <summary>
/// The tool „Messen“ (Ablaufplan 7.6 c; decision of 6.11.2026): points are set with clicks — snapped to the drawing, with Alt where clicked —
/// and measured with the scale of the page (ISO 32000-2, 12.9; without one, the paper in millimetres). A length ends after two points, an
/// angle and a radius after three, a polyline and an area with a double-click or Enter; Esc drops the measurement going on. Finished
/// measurements stay on the page until the tool is switched off; nothing is written to the file (see docs/FEHLERGEDAECHTNIS.md, No. 581) —
/// until they are inserted as dimensions; those, and the dimensions saved before, are measurements too (Ablaufplan 7.6 h, No. 605).
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>What is measured next.</summary>
    [ObservableProperty]
    public partial MeasureKind MeasureKind { get; set; } = MeasureKind.Length;

    /// <summary>Whether the tool „Messen“ is on.</summary>
    public bool IsMeasuring => Tool == ViewTool.Measure;

    /// <summary>The page of the measurement going on; −1 when none.</summary>
    public int MeasurePage { get; private set; } = -1;

    private readonly List<(double X, double Y)> _measurePoints = [];

    /// <summary>The points set so far (page space).</summary>
    public IReadOnlyList<(double X, double Y)> MeasurePoints => _measurePoints;

    private readonly List<int> _measureArcPoints = [];

    /// <summary>
    /// The arc points among <see cref="MeasurePoints"/> (Ablaufplan 7.6 j): set with Ctrl+Shift, each makes the piece from the point before to
    /// the point after an arc through it.
    /// </summary>
    public IReadOnlyList<int> MeasureArcPoints => _measureArcPoints;

    /// <summary>Where the next point would go (under the mouse, snapped), on <see cref="MeasurePage"/> or the page of the mouse.</summary>
    public (int Page, double X, double Y, SnapKind Snap)? MeasureHover { get; private set; }

    /// <summary>The measurements finished in this session.</summary>
    public ObservableCollection<MeasureRow> Measurements { get; } = [];

    /// <summary>The colours a measurement can have (strong ones, to stand out on plans).</summary>
    public static IReadOnlyList<FieldColorOption> MeasureColors { get; } =
    [
        new(0xEA580C, "Orange"),
        new(0xC00000, "Rot"),
        new(0x1F4E9A, "Blau"),
        new(0x1E7B34, "Grün"),
        new(0x7C3AED, "Violett"),
        new(0x000000, "Schwarz"),
    ];

    /// <summary>The colour of the next measurement.</summary>
    [ObservableProperty]
    public partial FieldColorOption MeasureColor { get; set; } = MeasureColors[0];

    /// <summary>The main switch: the lengths of the pieces at all measurements (and the next ones) on or off.</summary>
    [ObservableProperty]
    public partial bool MeasureShowSegments { get; set; }

    /// <summary>The main switch: the angles at the points of all measurements (and the next ones) on or off.</summary>
    [ObservableProperty]
    public partial bool MeasureShowAngles { get; set; }

    partial void OnMeasureShowSegmentsChanged(bool value) => SetOnAll(row => row.ShowSegments = value);

    partial void OnMeasureShowAnglesChanged(bool value) => SetOnAll(row => row.ShowAngles = value);

    /// <summary>Adds a finished measurement: in the colour chosen, with the switches as the main switches are; a change of it is redrawn.</summary>
    /// <returns>Its row in the list.</returns>
    private MeasureRow AddMeasurement(int page, Measurement measured)
    {
        var row = new MeasureRow(page, measured, MeasureColor, MeasureShowSegments, MeasureShowAngles, (uint)page < (uint)Pages.Count ? Pages[page].Item.Id : -1);
        _measuredPageCount = Pages.Count;
        row.PropertyChanged += (_, _) => MeasureChanged?.Invoke();
        Measurements.Add(row);
        return row;
    }

    /// <summary>The value of the measurement going on (with the point under the mouse), or of the last one finished; empty before.</summary>
    [ObservableProperty]
    public partial string MeasureText { get; set; } = "";

    /// <summary>More about the measurement shown (Ablaufplan 7.6 e): of an arc its length, angle and how far the points lie off it.</summary>
    [ObservableProperty]
    public partial string MeasureDetail { get; set; } = "";

    /// <summary>„Maßstab 1:100“, or that the paper is measured.</summary>
    [ObservableProperty]
    public partial string MeasureScaleText { get; set; } = "";

    /// <summary>Raised when points, the pointer or the list change (the view draws them).</summary>
    public event Action? MeasureChanged;

    /// <summary>The name of a kind of measurement.</summary>
    public static string KindName(MeasureKind kind) => kind switch
    {
        MeasureKind.Polyline => Tr.T("Linienzug"),
        MeasureKind.Area => Tr.T("Fläche"),
        MeasureKind.Angle => Tr.T("Winkel"),
        MeasureKind.Radius => Tr.T("Kreis/Bogen"),
        _ => Tr.T("Länge"),
    };

    /// <summary>Chooses what to measure (the measurement going on is dropped).</summary>
    [RelayCommand]
    private void SetMeasureKind(MeasureKind kind) => MeasureKind = kind;

    partial void OnMeasureKindChanged(MeasureKind value)
    {
        CancelMeasure();
        OnPropertyChanged(nameof(MeasureHint));
    }

    /// <summary>How the chosen kind is measured.</summary>
    public string MeasureHint => MeasureKind switch
    {
        MeasureKind.Polyline => Tr.T("Punkte nacheinander anklicken; Doppelklick oder Enter beendet. Bögen: Strg+Umschalt+Klick setzt einen Bogenpunkt, der nächste Klick beendet den Bogen durch ihn; Strg+Alt+Klick setzt das Ende eines Bogens, der ohne Knick anschließt — so auch S-Kurven."),
        MeasureKind.Area => Tr.T("Die Ecken der Fläche anklicken; Doppelklick oder Enter schließt sie. Bögen: Strg+Umschalt+Klick setzt einen Bogenpunkt, der nächste Klick beendet den Bogen durch ihn; Strg+Alt+Klick setzt das Ende eines Bogens, der ohne Knick anschließt."),
        MeasureKind.Angle => Tr.T("Drei Punkte: Schenkel, Scheitel, Schenkel."),
        MeasureKind.Radius => Tr.T("Mindestens drei Punkte auf dem Kreis oder Bogen — der erste und der letzte sind seine Enden; Doppelklick oder Enter beendet. Erster und letzter Punkt gleich: der ganze Kreis."),
        _ => Tr.T("Anfang und Ende anklicken."),
    };

    /// <summary>Points that end a measurement of the kind by themselves; 0 if it is ended by hand.</summary>
    private static int PointsOf(MeasureKind kind) => kind switch
    {
        MeasureKind.Length => 2,
        MeasureKind.Angle => 3,
        _ => 0,
    };

    /// <summary>
    /// The drawings of the pages measured on (display lists to snap to), kept while „Messen“ is on — the cache of display lists is shared by
    /// all open documents and drops lists when memory gets tight; a list built again in time for a click only within a second (No. 610) left
    /// points unsnapped under load. Kept here, a click never waits again (docs/FEHLERGEDAECHTNIS.md, No. 625). The last few pages only.
    /// </summary>
    private readonly Dictionary<int, Core.Pdf.Graphics.DisplayList> _snapLists = [];

    /// <summary>The order the drawings were last used in (oldest first).</summary>
    private readonly List<int> _snapOrder = [];

    /// <summary>How many drawings of pages are kept at most.</summary>
    private const int KeptSnapLists = 4;

    /// <summary>The drawing of a page kept for snapping (by the key of the page in the renderer); null if none is kept.</summary>
    internal Core.Pdf.Graphics.DisplayList? KeptSnapList(int key)
    {
        if (!_snapLists.TryGetValue(key, out var list)) return null;
        _snapOrder.Remove(key);
        _snapOrder.Add(key);
        return list;
    }

    /// <summary>Keeps the drawing of a page for snapping while „Messen“ is on (the oldest goes beyond <see cref="KeptSnapLists"/>).</summary>
    internal void KeepSnapList(int key, Core.Pdf.Graphics.DisplayList list)
    {
        if (!IsMeasuring) return;
        _snapLists[key] = list;
        _snapOrder.Remove(key);
        _snapOrder.Add(key);
        while (_snapOrder.Count > KeptSnapLists)
        {
            _snapLists.Remove(_snapOrder[0]);
            _snapOrder.RemoveAt(0);
        }
    }

    /// <summary>The viewports of a page (its scales).</summary>
    private IReadOnlyList<Viewport> ScalesOf(int page) =>
        (uint)page < (uint)Pages.Count ? PageScales.Of(Pages[page].Item.Page.Dictionary) : [];

    /// <summary>The size of a user space unit of the page in points of the paper (/UserUnit; FEHLERGEDAECHTNIS 583).</summary>
    private double UserUnitOf(int page) => (uint)page < (uint)Pages.Count ? Pages[page].Item.Page.UserUnit : 1;

    /// <summary>Measures on a page: its scale, else its paper; a polyline or area with its arc points (Ablaufplan 7.6 j).</summary>
    private Measurement MeasureOn(int page, MeasureKind kind, IReadOnlyList<(double X, double Y)> points, IReadOnlyList<int>? arcPoints = null) =>
        Measuring.Measure(kind, points, ScalesOf(page), UserUnitOf(page), MeasureDecimals.Value, arcPoints);

    /// <summary>A click: sets a point (on another page a new measurement begins); ends the measurement when it has its points.</summary>
    /// <param name="page">The page (index).</param>
    /// <param name="x">The point (page space, already snapped).</param>
    /// <param name="y">The point (page space).</param>
    /// <param name="arc">
    /// Ctrl+Shift held (Ablaufplan 7.6 j): in a polyline or area the point is an arc point — the next point ends the arc through it — unless
    /// the point before is one (then this point ends that arc). The first point is never one.
    /// </param>
    /// <param name="tangent">
    /// Ctrl+Alt held (Ablaufplan 7.6 k): in a polyline or area the point ends a tangent arc — it goes on from the last piece without a bend
    /// (<see cref="MeasureGeometry.TangentArcPoint(IReadOnlyList{ValueTuple{double, double}}, IReadOnlyList{int}, RectilinearMeasure, ValueTuple{double, double})"/>;
    /// its point halfway is set as its arc point). Without a direction yet (after the first point): with <paramref name="direction"/> dragged
    /// out the arc leaves the last point that way, else the point becomes an arc point as with Ctrl+Shift (decided on 9.11.2026;
    /// docs/FEHLERGEDAECHTNIS.md, No. 664) — before a plain point.
    /// </param>
    /// <param name="direction">The direction the first arc leaves the last point in (page space), dragged out with Ctrl+Alt; null: none.</param>
    public void MeasureAt(int page, double x, double y, bool arc = false, bool tangent = false, (double X, double Y)? direction = null)
    {
        if (!IsMeasuring || (uint)page >= (uint)Pages.Count) return;
        if (MeasurePage != page) ClearMeasurePoints();
        if (_measurePoints.Count == 0) SelectedMeasurement = null;
        MeasurePage = page;
        // the same point twice (a double-click's first click) adds nothing
        DecideContinuingEnd((x, y));   // continuing a polyline: the first click takes the nearer end (Ablaufplan 7.6 l)
        if (_measurePoints.Count == 0 || Math.Abs(_measurePoints[^1].X - x) > 1e-6 || Math.Abs(_measurePoints[^1].Y - y) > 1e-6)
        {
            if (tangent && MeasureKind is MeasureKind.Polyline or MeasureKind.Area &&
                TangentThrough(page, _measurePoints, _measureArcPoints, (x, y), direction) is { } through)
            {
                _measurePoints.Add(through);
                _measureArcPoints.Add(_measurePoints.Count - 1);
                arc = false;
                MeasureArcFlipped = false;   // the next arc starts on its own side again
            }
            // no direction to go on in yet: the point an arc point, as with Ctrl+Shift (No. 664)
            else if (tangent && MeasureNeedsDirection) arc = true;
            _measurePoints.Add((x, y));
            var index = _measurePoints.Count - 1;
            if (arc && MeasureKind is MeasureKind.Polyline or MeasureKind.Area && index > 0 && !_measureArcPoints.Contains(index - 1))
                _measureArcPoints.Add(index);
        }
        if (PointsOf(MeasureKind) is > 0 and var needed && _measurePoints.Count >= needed) FinishMeasure();
        else Update();
    }

    /// <summary>
    /// The point halfway along a tangent arc from the last of <paramref name="points"/> to <paramref name="end"/>, with the scale of the page — in
    /// the direction the last piece ends in, or in <paramref name="direction"/> where it is given (the first arc, No. 664); null if there is none.
    /// </summary>
    private (double X, double Y)? TangentThrough(int page, IReadOnlyList<(double X, double Y)> points, IReadOnlyList<int> arcs, (double X, double Y) end,
        (double X, double Y)? direction = null)
    {
        if (points.Count == 0 || arcs.Contains(points.Count - 1)) return null;
        var measure = MeasureOn(page, MeasureKind.Polyline, points, arcs).Measure;
        if (measure is null) return null;
        var through = direction is { } d ? MeasureGeometry.TangentArcPoint(points[^1], d, measure, end)
            : points.Count < 2 ? null : MeasureGeometry.TangentArcPoint(points, arcs, measure, end);
        // flipped (space bar): the same arc mirrored at its chord — it bulges to the other side, between the same two points
        return through is { } t && MeasureArcFlipped ? Mirrored(t, points[^1], end) : through;
    }

    /// <summary><paramref name="p"/> mirrored at the line through <paramref name="a"/> and <paramref name="b"/> (itself if they coincide).</summary>
    internal static (double X, double Y) Mirrored((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        var (ux, uy) = (b.X - a.X, b.Y - a.Y);
        var length = ux * ux + uy * uy;
        if (length < 1e-12) return p;
        var t = ((p.X - a.X) * ux + (p.Y - a.Y) * uy) / length;
        var (fx, fy) = (a.X + t * ux, a.Y + t * uy);
        return (2 * fx - p.X, 2 * fy - p.Y);
    }

    /// <summary>
    /// Whether the tangent arc being placed bulges to the other side of its chord (decided on 10.11.2026: going right, the arc came
    /// out below where it was wanted above). The space bar flips it while Ctrl+Alt is held or the first arc is dragged out; a tangent arc
    /// set, or a new measurement, starts unflipped. A flipped arc that goes on from a piece meets it with a bend.
    /// </summary>
    public bool MeasureArcFlipped { get; private set; }

    /// <summary>Flips the tangent arc being placed to the other side (<see cref="MeasureArcFlipped"/>); false if no arc is being placed.</summary>
    public bool FlipMeasureArc()
    {
        if (!IsMeasuring || MeasureKind is not (MeasureKind.Polyline or MeasureKind.Area) || _measurePoints.Count == 0) return false;
        MeasureArcFlipped = !MeasureArcFlipped;
        Update();
        return true;
    }

    /// <summary>
    /// Whether the measurement going on has no direction to go on in yet: a polyline or area with its first point only — Ctrl+Alt then drags out
    /// the direction of the first arc, or makes the point an arc point (No. 664).
    /// </summary>
    public bool MeasureNeedsDirection => MeasureKind is MeasureKind.Polyline or MeasureKind.Area && _measurePoints.Count == 1 && _continuing is null;

    /// <summary>Whether the point under the mouse would end a tangent arc (Ctrl+Alt held; for the preview).</summary>
    private bool _hoverTangent;

    /// <summary>Whether the point under the mouse would be an arc point (Ctrl+Shift held; for the preview, No. 664).</summary>
    private bool _hoverArc;

    /// <summary>The direction of the first arc being dragged out (Ctrl+Alt after the first point; for the preview); null: none.</summary>
    private (double X, double Y)? _hoverDirection;

    /// <summary>
    /// The mouse moved: the point it would set, for the preview — with Ctrl+Alt (<paramref name="tangent"/>) the end of a tangent arc (along
    /// <paramref name="direction"/> while it is dragged out), with Ctrl+Shift (<paramref name="arc"/>) an arc point.
    /// </summary>
    public void MeasureHoverAt(int page, double x, double y, SnapKind snap, bool tangent = false, bool arc = false, (double X, double Y)? direction = null)
    {
        if (!IsMeasuring) return;
        MeasureHover = (page, x, y, snap);
        _hoverTangent = tangent;
        _hoverArc = arc;
        _hoverDirection = direction;
        Update();
    }

    /// <summary>The mouse left the pages.</summary>
    public void MeasureHoverOff()
    {
        if (MeasureHover is null) return;
        MeasureHover = null;
        Update();
    }

    /// <summary>Points a measurement of the kind needs before it can be ended.</summary>
    private static int PointsToFinish(MeasureKind kind) => kind is MeasureKind.Area or MeasureKind.Angle or MeasureKind.Radius ? 3 : 2;

    /// <summary>Ends the measurement going on (Enter, double-click); false if it has too few points.</summary>
    public bool FinishMeasure()
    {
        if (MeasurePage < 0 || _measurePoints.Count < PointsToFinish(MeasureKind)) return false;
        CompleteMeasure();
        return true;
    }

    /// <summary>Ends the measurement going on (it must have enough points): its row in the list, or null if its points measure nothing.</summary>
    private MeasureRow? CompleteMeasure()
    {
        if (_continuing is not null) return CompleteContinuing();   // a polyline continued: it changes (Ablaufplan 7.6 l)
        var measured = MeasureOn(MeasurePage, MeasureKind, [.. _measurePoints], [.. _measureArcPoints]);
        var row = measured.Value is not null ? AddMeasurement(MeasurePage, measured) : null;
        ClearMeasurePoints();
        MeasurePage = -1;
        Update(measured);
        return row;
    }

    /// <summary>Drops the measurement going on (Esc); false if there was none.</summary>
    public bool CancelMeasure()
    {
        if (_measurePoints.Count == 0) return false;
        ClearMeasurePoints();
        MeasurePage = -1;
        Update();
        return true;
    }

    /// <summary>The points of the measurement going on and its arc points go.</summary>
    private void ClearMeasurePoints()
    {
        _measurePoints.Clear();
        _measureArcPoints.Clear();
        MeasureArcFlipped = false;
        _continuing = null;   // a polyline being continued stays as it was
        _continueAtStart = null;
    }

    /// <summary>Takes back the last point (Backspace), an arc point with its mark; false if there was none.</summary>
    public bool UndoMeasurePoint()
    {
        if (_measurePoints.Count == 0) return false;
        _measurePoints.RemoveAt(_measurePoints.Count - 1);
        _measureArcPoints.Remove(_measurePoints.Count);
        if (_measurePoints.Count == 0)
        {
            MeasurePage = -1;
            _continuing = null;
            _continueAtStart = null;
        }
        Update();
        return true;
    }

    /// <summary>Removes the finished measurements of this session from the page (dimensions stay: they are deleted one by one).</summary>
    [RelayCommand]
    private void ClearMeasurements()
    {
        if (SelectedMeasurement is { IsSession: true }) SelectedMeasurement = null;
        for (var i = Measurements.Count - 1; i >= 0; i--)
            if (Measurements[i].IsSession) Measurements.RemoveAt(i);
        MeasureText = "";
        MeasureDetail = "";
        MeasureChanged?.Invoke();
    }

    /// <summary>The measurement going on, with the point under the mouse as its next point; null without points.</summary>
    /// <param name="drawing">For drawing the preview (the same measurement: a point that would be an arc point shows as a straight piece, with
    /// a small arc beside the mouse, <see cref="MeasureHoverMakesArc"/>; No. 675).</param>
    public Measurement? CurrentMeasurement(bool drawing = false)
    {
        if (MeasurePage < 0 || _measurePoints.Count == 0) return null;
        var points = new List<(double X, double Y)>(_measurePoints);
        var arcs = new List<int>(_measureArcPoints);
        // the point under the mouse: the end of an arc whose arc point was the last one set, or of a tangent arc; continuing a polyline
        // before the end is decided, at the end nearer to it
        if (MeasureHover is { } hover && hover.Page == MeasurePage)
        {
            if (ContinuingPreview((hover.X, hover.Y)) is { } preview) (points, arcs) = preview;
            var curved = MeasureKind is MeasureKind.Polyline or MeasureKind.Area;
            if (_hoverTangent && curved && TangentThrough(MeasurePage, points, arcs, (hover.X, hover.Y), _hoverDirection) is { } through)
            {
                points.Add(through);
                arcs.Add(points.Count - 1);
            }
            points.Add((hover.X, hover.Y));
        }
        return MeasureOn(MeasurePage, MeasureKind, points, arcs);
    }

    /// <summary>
    /// Whether the point under the mouse would be an arc point (Ctrl+Shift, or Ctrl+Alt before the first arc has a direction): the preview
    /// stays a straight piece and the view shows a small arc beside the mouse (decided on 9.11.2026: the half circle of No. 664 looked
    /// odd; No. 675).
    /// </summary>
    public bool MeasureHoverMakesArc =>
        MeasureKind is MeasureKind.Polyline or MeasureKind.Area && MeasureHover is { } hover && hover.Page == MeasurePage && _measurePoints.Count > 0 &&
        !_measureArcPoints.Contains(_measurePoints.Count - 1) && (_hoverArc || _hoverTangent && _hoverDirection is null && MeasureNeedsDirection);

    /// <summary>The detail line of a measurement: „Bogen 3.93 m · 90° · Punkte bis 0.01 m vom Kreis“; empty for kinds without.</summary>
    public static string Detail(Measurement measurement) => Measuring.Detail(measurement);

    private void Update(Measurement? finished = null)
    {
        var current = finished ?? CurrentMeasurement();
        // the last value stays until the measurement going on has one of its own (an area with two points has none yet)
        if (current is { Text.Length: > 0 })
        {
            MeasureText = current.Text;
            MeasureDetail = Detail(current);
        }
        // the scale: that of the measurement, else the one at the pointer (12.9.1: the viewport of the first point decides)
        string? scale = null;
        var known = true;
        if (current is not null) scale = current.Scale;
        else if (MeasureHover is { } hover) scale = MeasureOn(hover.Page, MeasureKind.Length, [(hover.X, hover.Y), (hover.X, hover.Y)]).Scale;
        else known = false;
        if (known)
            MeasureScaleText = scale is null ? Tr.T("Kein Maßstab auf der Seite — gemessen wird das Papier (mm).")
                : scale.Length > 0 ? Tr.T("Maßstab: {0}", scale) : Tr.T("Maßstab der Seite");
        MeasureChanged?.Invoke();
    }

    // ------------------------------------------------------------ Calibrating (Ablaufplan 7.6 d)

    /// <summary>The units a scale can be set in.</summary>
    public static IReadOnlyList<string> ScaleUnits { get; } = [.. PageScales.Units.Select(u => u.Unit)];

    /// <summary>„100“ in „1 : 100“.</summary>
    [ObservableProperty]
    public partial string ScaleRatio { get; set; } = "100";

    /// <summary>The unit of the scale.</summary>
    [ObservableProperty]
    public partial string ScaleUnit { get; set; } = "m";

    /// <summary>A number as typed: point or comma; „1:100“ gives 100.</summary>
    private static double? Typed(string text)
    {
        var t = text.Trim();
        if (t.LastIndexOf(':') is var colon and >= 0) t = t[(colon + 1)..].Trim();
        t = t.Replace(',', '.');
        return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 && double.IsFinite(v) ? v : null;
    }

    private double MillimetresPerUnit => PageScales.Units.FirstOrDefault(u => u.Unit == ScaleUnit) is { Unit: not null } found ? found.Millimetres : 1000;

    // ------------------------------------------------------------ Calibrating with a line (Ablaufplan 7.6 g; decided on 6.11.2026)

    /// <summary>The real length of the calibration line, in <see cref="ScaleUnit"/>; typed, it fills <see cref="ScaleRatio"/>.</summary>
    [ObservableProperty]
    public partial string CalibrationLength { get; set; } = "";

    partial void OnCalibrationLengthChanged(string value) => FillRatioFromCalibration();

    partial void OnScaleUnitChanged(string value) => FillRatioFromCalibration();

    /// <summary>Whether the start and end of the calibration line are being set (with the magnifier).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalibrationHint))]
    public partial bool IsCalibrating { get; set; }

    private readonly List<(double X, double Y)> _calibration = [];

    /// <summary>The page of the calibration line; −1 without one.</summary>
    public int CalibrationPage { get; private set; } = -1;

    /// <summary>Start (and end) of the calibration line (page space).</summary>
    public IReadOnlyList<(double X, double Y)> CalibrationPoints => _calibration;

    /// <summary>Whether the calibration line is complete: its real length can be typed.</summary>
    public bool HasCalibrationLine => _calibration.Count == 2;

    /// <summary>The calibration line on the paper: „Strecke auf dem Papier: 105.8 mm“.</summary>
    public string CalibrationLineText => HasCalibrationLine
        ? Tr.T("Strecke auf dem Papier: {0}", Measuring.PaperOf(UserUnitOf(CalibrationPage)).Distance.Format(CalibrationPaper()))
        : "";

    /// <summary>What to do next in calibrating.</summary>
    public string CalibrationHint => IsCalibrating
        ? _calibration.Count == 0 ? Tr.T("Anfang der bekannten Strecke anklicken (etwa der Maßstabsleiste) — die Lupe hilft beim genauen Setzen.")
            : Tr.T("Ende der Strecke anklicken.")
        : HasCalibrationLine ? Tr.T("Wirkliche Länge der Strecke eingeben — daraus ergibt sich der Maßstab.")
        : Tr.T("Maßstab unbekannt? „Kalibrieren“ und eine Strecke bekannter Länge mit Anfang und Ende setzen.");

    /// <summary>Raised when the calibration line is complete: the panel puts the cursor into the length box.</summary>
    public event Action? CalibrationLengthWanted;

    /// <summary>„Kalibrieren“: the next two clicks set the start and end of a line of known length (a measuring in progress is dropped).</summary>
    [RelayCommand]
    private void StartCalibration()
    {
        CancelMeasure();
        SelectedMeasurement = null;
        _calibration.Clear();
        CalibrationPage = -1;
        IsCalibrating = true;
        NotifyCalibration();
    }

    /// <summary>A click while calibrating: the start, then the end of the line (on another page the line begins anew).</summary>
    public void CalibrateAt(int page, double x, double y)
    {
        if (!IsCalibrating || (uint)page >= (uint)Pages.Count) return;
        if (_calibration.Count == 1 && page != CalibrationPage) _calibration.Clear();
        if (_calibration.Count == 0) CalibrationPage = page;
        else if (Math.Abs(_calibration[0].X - x) < 1e-6 && Math.Abs(_calibration[0].Y - y) < 1e-6) return;   // a double-click's second click
        _calibration.Add((x, y));
        if (_calibration.Count == 2)
        {
            IsCalibrating = false;
            FillRatioFromCalibration();
            CalibrationLengthWanted?.Invoke();
        }
        NotifyCalibration();
    }

    /// <summary>Drops the calibration line (Esc, right button); false if there was none.</summary>
    public bool CancelCalibration()
    {
        if (!IsCalibrating && _calibration.Count == 0) return false;
        IsCalibrating = false;
        _calibration.Clear();
        CalibrationPage = -1;
        NotifyCalibration();
        return true;
    }

    private void NotifyCalibration()
    {
        OnPropertyChanged(nameof(HasCalibrationLine));
        OnPropertyChanged(nameof(CalibrationLineText));
        OnPropertyChanged(nameof(CalibrationHint));
        MeasureChanged?.Invoke();
    }

    /// <summary>The length of the calibration line in user space units.</summary>
    private double CalibrationUnits() => HasCalibrationLine
        ? Math.Sqrt(Math.Pow(_calibration[1].X - _calibration[0].X, 2) + Math.Pow(_calibration[1].Y - _calibration[0].Y, 2))
        : 0;

    /// <summary>The length of the calibration line on the paper in mm.</summary>
    private double CalibrationPaper() => CalibrationUnits() * Measuring.MillimetresPerPoint * UserUnitOf(CalibrationPage);

    /// <summary>With a calibration line and its real length: the ratio it gives goes into <see cref="ScaleRatio"/>.</summary>
    private void FillRatioFromCalibration()
    {
        if (HasCalibrationLine && Typed(CalibrationLength) is { } real && CalibrationPaper() is var paper and > 0)
            ScaleRatio = Ratio(real * MillimetresPerUnit / paper);
        MeasureChanged?.Invoke();
    }

    /// <summary>The scale of the page as typed into the box: that of its whole-page viewport („1:100“ gives 100), where it has one.</summary>
    private void ShowScaleOf(int page)
    {
        if (ScalesOf(page).LastOrDefault(v => v.Measure is not null) is { Measure.Ratio: { } ratio } && ratio.Contains(':') && Typed(ratio) is { } value)
            ScaleRatio = Ratio(value);
    }

    /// <summary>
    /// „Maßstab festlegen“: with a calibration line and its real length, the scale it gives (exactly, from the line); otherwise 1 :
    /// <see cref="ScaleRatio"/> — for the page of the line, of the measurement chosen or taken last, or the page in view; or for all pages.
    /// </summary>
    [RelayCommand]
    private void ApplyScale()
    {
        if (HasCalibrationLine)
        {
            if (Typed(CalibrationLength) is not { } real)
            {
                Notify(Tr.T("Bitte die wirkliche Länge der Strecke als Zahl eingeben."));
                return;
            }
            var page = CalibrationPage;
            var perPoint = real / CalibrationUnits();
            var ratio = "1:" + Ratio(real * MillimetresPerUnit / CalibrationPaper());
            // other pages: the same drawing ratio — a unit of a page with another /UserUnit is that much more or less paper
            SetScale([.. ScalePages(page).Select(p => (p, perPoint * UserUnitOf(p) / UserUnitOf(page)))], ratio);
            CancelCalibration();
            return;
        }
        if (Typed(ScaleRatio) is not { } typed)
        {
            Notify(Tr.T("Bitte das Verhältnis als Zahl eingeben, etwa 100 für 1:100."));
            return;
        }
        // the page of the measurement chosen or taken last — measured there, the scale is meant for it —, otherwise the page in view; not the
        // page the mouse was over last (it is on the panel now; No. 598)
        var target = SelectedMeasurement?.Page ?? Measurements.LastOrDefault()?.Page ?? (CurrentPage is { } current ? current.Number - 1 : 0);
        // one unit of user space is /UserUnit points of the paper, a point 25.4/72 mm, in the drawing ratio times as much
        SetScale([.. ScalePages(target).Select(p => (p, Measuring.MillimetresPerPoint * UserUnitOf(p) * typed / MillimetresPerUnit))], "1:" + Ratio(typed));
    }

    private static string Ratio(double value) =>
        Math.Round(value, value >= 100 ? 0 : 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The pages a scale is set for: the one given, or all (<see cref="ScaleAllPages"/>).</summary>
    private IEnumerable<int> ScalePages(int page) => ScaleAllPages ? Enumerable.Range(0, History.Count) : [page];

    /// <summary>
    /// Writes the scale into the pages (/VP; one undo step, also for all pages) and measures the measurements of those pages again.
    /// </summary>
    private void SetScale(IReadOnlyList<(int Page, double UnitsPerPoint)> pages, string ratio)
    {
        if (pages.Count == 0 || pages.Any(p => (uint)p.Page >= (uint)History.Count)) return;
        if (!CanModifyContent)   // the scale goes into the page (/VP), not into an annotation
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return;
        }
        // viewports of the file that are smaller than the page (details): the new scale is the last, so it holds there too — said once
        var details = pages.Any(p => PageScales.Of(History.Pages[p.Page].Page.Dictionary).Any(v => !Covers(v.Box, History.Pages[p.Page].Page.CropBox)));
        // the dimensions of the pages are measured anew with it in the same step (No. 605)
        var remeasured = 0;
        var changes = new List<(int Index, PageItem Old, PageItem Updated)>();
        foreach (var p in pages)
        {
            var old = History.Pages[p.Page];
            var updated = old.WithPageDictionary(PageScales.WithScale(old.Page.Dictionary, old.Page.CropBox, p.UnitsPerPoint, ScaleUnit, ratio));
            changes.Add((p.Page, old, WithDimensionsMeasured(p.Page, updated, ref remeasured)));
        }
        if (!History.ReplacePages(changes, Tr.T("Maßstab festlegen"))) return;
        var set = pages.Select(p => p.Page).ToHashSet();
        foreach (var row in Measurements)
            if (set.Contains(row.Page) && row.IsSession) row.Measurement = MeasureOn(row.Page, row.Measurement.Kind, row.Measurement.Points, row.Measurement.ArcPoints);
        if (Measurements.LastOrDefault() is { } latest)
        {
            MeasureText = latest.Measurement.Text;
            MeasureDetail = Detail(latest.Measurement);
        }
        var message = pages.Count == 1 ? Tr.T("Maßstab {0} für Seite {1} festgelegt — er wird mit der Datei gespeichert.", ratio, pages[0].Page + 1)
            : Tr.T("Maßstab {0} für alle {1} Seiten festgelegt — er wird mit der Datei gespeichert.", ratio, pages.Count);
        if (remeasured > 0) message += " " + Tr.T("Die Bemaßungen darauf rechnen mit ihm neu.");
        Notify(details ? message + " " + Tr.T("Die Seite hatte eigene Maßstäbe für Teilbereiche — der neue gilt jetzt auch dort.") : message);
        Update();
    }

    /// <summary>Whether a viewport's box covers the whole crop box (then it is no detail).</summary>
    private static bool Covers(double[] box, Core.Pdf.Document.PdfRectangle crop) =>
        box.Length == 4 && Math.Min(box[0], box[2]) <= crop.Left + 0.5 && Math.Min(box[1], box[3]) <= crop.Bottom + 0.5
        && Math.Max(box[0], box[2]) >= crop.Right - 0.5 && Math.Max(box[1], box[3]) >= crop.Top - 0.5;

    /// <summary>
    /// After undo, redo or pages rearranged (docs/FEHLERGEDAECHTNIS.md, No. 598): each measurement finds its page again by its identity and is
    /// measured anew (a scale undone or redone changes its value); measurements whose page is gone are dropped. Before, they kept the value of
    /// the scale undone, and a dimension inserted then carried it.
    /// </summary>
    private void MeasureHistoryChanged()
    {
        if (!IsMeasuring) return;
        var places = new Dictionary<int, int>();
        for (var i = 0; i < Pages.Count; i++) places.TryAdd(Pages[i].Item.Id, i);
        // the same number of pages: a page not found by its identity was replaced in its place (a scale set or undone gives the page a new one)
        var samePages = _measuredPageCount == Pages.Count;
        for (var i = Measurements.Count - 1; i >= 0; i--)
        {
            var row = Measurements[i];
            if (row.IsSaved) continue;   // dimensions: from the pages below
            if (!places.TryGetValue(row.PageId, out var at))
            {
                if (!samePages || (uint)row.Page >= (uint)Pages.Count)
                {
                    if (ReferenceEquals(row, SelectedMeasurement)) SelectedMeasurement = null;
                    Measurements.RemoveAt(i);
                    continue;
                }
                at = row.Page;
            }
            row.Page = at;
            row.PageId = Pages[at].Item.Id;
            row.Measurement = MeasureOn(at, row.Measurement.Kind, row.Measurement.Points, row.Measurement.ArcPoints);
        }
        _measuredPageCount = Pages.Count;
        SyncDimensions();
        if ((SelectedMeasurement ?? Measurements.LastOrDefault()) is { } shown)
        {
            MeasureText = shown.Measurement.Text;
            MeasureDetail = Detail(shown.Measurement);
        }
        Update();
    }

    /// <summary>The number of pages when the measurements were last placed (see <see cref="MeasureHistoryChanged"/>).</summary>
    private int _measuredPageCount;

    /// <summary>Whether a scale is set for all pages of the document instead of the one in view (Ablaufplan 7.6 e3).</summary>
    [ObservableProperty]
    public partial bool ScaleAllPages { get; set; }

    // ------------------------------------------------------------ Choosing and changing a measurement (Ablaufplan 7.6 e3)

    /// <summary>The widths a measurement can be drawn with.</summary>
    public static IReadOnlyList<Choice<double>> MeasureWidths { get; } =
        [.. new[] { 1, 1.5, 2, 3 }.Select(w => new Choice<double>(w, w.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " pt"))];

    /// <summary>How strongly an area can be filled.</summary>
    public static IReadOnlyList<Choice<double>> MeasureOpacities { get; } =
        [.. new[] { 0, 0.1, 0.2, 0.35, 0.5 }.Select(o => new Choice<double>(o, (o * 100).ToString("0", System.Globalization.CultureInfo.CurrentCulture) + " %"))];

    /// <summary>The decimals measurements are shown with: those of the file, or a number of places.</summary>
    public static IReadOnlyList<Choice<int?>> MeasureDecimalChoices { get; } =
        [new(null, "wie in der Datei"), .. Enumerable.Range(0, 5).Select(d => new Choice<int?>(d, d.ToString(System.Globalization.CultureInfo.InvariantCulture)))];

    /// <summary>The decimals chosen.</summary>
    [ObservableProperty]
    public partial Choice<int?> MeasureDecimals { get; set; } = MeasureDecimalChoices[0];

    partial void OnMeasureDecimalsChanged(Choice<int?> value)
    {
        foreach (var row in Measurements)
            row.Measurement = row.IsSaved ? MeasureSaved(row.Page, row.Measurement.Kind, row.Measurement.Points, row.OwnMeasure, row.Measurement.ArcPoints)
                : MeasureOn(row.Page, row.Measurement.Kind, row.Measurement.Points, row.Measurement.ArcPoints);
        if ((SelectedMeasurement ?? Measurements.LastOrDefault()) is { } shown)
        {
            MeasureText = shown.Measurement.Text;
            MeasureDetail = Detail(shown.Measurement);
        }
        Update();
    }

    /// <summary>Whether points snap to the drawing (with Alt the other way round).</summary>
    [ObservableProperty]
    public partial bool MeasureSnap { get; set; } = true;

    /// <summary>Whether lines snap straight — 0°, 45°, 90° … from the previous point (with Shift the other way round; Ablaufplan 7.6 g, No. 602).</summary>
    [ObservableProperty]
    public partial bool MeasureStraight { get; set; } = true;

    /// <summary>Whether a magnifier beside the mouse shows the spot enlarged while measuring (Ablaufplan 7.6 g, No. 604; while calibrating
    /// always).</summary>
    [ObservableProperty]
    public partial bool MeasureLoupe { get; set; } = true;

    /// <summary>The measurement chosen (a click on its value, or in the list): handles at its points, the bar above it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMeasurement))]
    public partial MeasureRow? SelectedMeasurement { get; set; }

    /// <summary>Whether a measurement is chosen.</summary>
    public bool HasSelectedMeasurement => SelectedMeasurement is not null;

    /// <summary>The point of the chosen measurement chosen with a click on its handle (Del takes it out; Ablaufplan 7.6 k); null if none.</summary>
    [ObservableProperty]
    public partial int? SelectedMeasurePoint { get; set; }

    partial void OnSelectedMeasurePointChanged(int? value) => MeasureChanged?.Invoke();

    partial void OnSelectedMeasurementChanged(MeasureRow? value)
    {
        SelectedMeasurePoint = null;
        if (value is not null)
        {
            ShowScaleOf(value.Page);
            MeasureText = value.Measurement.Text;
            MeasureDetail = Detail(value.Measurement);
        }
        MeasureChanged?.Invoke();
    }

    /// <summary>Removes the measurement chosen.</summary>
    [RelayCommand]
    private void DeleteSelectedMeasurement()
    {
        if (SelectedMeasurement is not { } row) return;
        if (row.IsSaved)
        {
            DeleteDimension(row);   // from its page (No. 605)
            return;
        }
        SelectedMeasurement = null;
        Measurements.Remove(row);
        MeasureChanged?.Invoke();
    }

    /// <summary>Gives the measurement chosen a colour.</summary>
    [RelayCommand]
    private void SetSelectedMeasureColor(FieldColorOption? color)
    {
        if (SelectedMeasurement is { } row && color is not null) row.Color = color;
    }

    /// <summary>Takes the polyline chosen as an arc through its points, or the arc back as a polyline.</summary>
    [RelayCommand]
    private void SwitchSelectedArc()
    {
        if (SelectedMeasurement is not { CanSwitchArc: true } row) return;
        var kind = row.Measurement.Kind == MeasureKind.Radius ? MeasureKind.Polyline : MeasureKind.Radius;
        var measured = MeasureAgain(row, kind, row.Measurement.Points);
        if (measured.Value is null)
        {
            Notify(Tr.T("Die Punkte liegen auf einer Geraden — kein Bogen."));
            return;
        }
        row.Measurement = measured;
        MeasureText = measured.Text;
        MeasureDetail = Detail(measured);
        MeasureChanged?.Invoke();
        if (row.IsSaved) SaveDimensions([row], Tr.T("Bemaßung ändern"));
    }

    /// <summary>Moves a point of a finished measurement (a handle dragged) and measures it again.</summary>
    /// <param name="row">The measurement.</param>
    /// <param name="index">The point.</param>
    /// <param name="x">Where to (page space, snapped).</param>
    /// <param name="y">Where to.</param>
    public void MoveMeasurePoint(MeasureRow row, int index, double x, double y)
    {
        if ((uint)index >= (uint)row.Measurement.Points.Count) return;
        var points = row.Measurement.Points.ToArray();
        // a closed circle keeps its ends together
        var closed = row.Measurement.Kind == MeasureKind.Radius && points.Length > 3 && points[0] == points[^1];
        points[index] = (x, y);
        if (closed && (index == 0 || index == points.Length - 1)) points[0] = points[^1] = (x, y);
        // a dimension is saved when the handle is let go (EndMeasureDrag) — one undo step for the whole drag
        var measured = MeasureAgain(row, row.Measurement.Kind, points, row.Measurement.ArcPoints);
        if (measured.Value is null) return;
        row.Measurement = measured;
        if (ReferenceEquals(row, SelectedMeasurement))
        {
            MeasureText = measured.Text;
            MeasureDetail = Detail(measured);
        }
        MeasureChanged?.Invoke();
    }

    /// <summary>
    /// Ctrl+click on the line of a measurement (Ablaufplan 7.6 k; decided on 8.11.2026): a point is put into the piece there — on a
    /// straight piece on the line, on an arc on its circle (<see cref="MeasureGeometry.Insert"/>); the value stays the same. The new point is
    /// chosen (Del takes it out again); a dimension is saved in one undo step.
    /// </summary>
    /// <param name="row">The measurement (a polyline or area).</param>
    /// <param name="piece">The piece clicked.</param>
    /// <param name="x">Where (page space).</param>
    /// <param name="y">Where.</param>
    /// <returns>Whether a point was put in.</returns>
    public bool InsertMeasurePoint(MeasureRow row, int piece, double x, double y)
    {
        var m = row.Measurement;
        if (m.Kind is not (MeasureKind.Polyline or MeasureKind.Area) || m.Measure is not { } measure ||
            MeasureGeometry.Insert(m.Points, m.ArcPoints, m.Kind == MeasureKind.Area, measure, piece, (x, y)) is not { } inserted) return false;
        if (!Remeasured(row, inserted.Points, inserted.ArcPoints, Tr.T("Punkt einfügen"))) return false;
        SelectedMeasurePoint = inserted.Index;
        return true;
    }

    /// <summary>
    /// Del with a point of the chosen measurement chosen (Ablaufplan 7.6 k): it is taken out (<see cref="MeasureGeometry.Remove"/>) — an arc
    /// point makes its piece straight. Too few points left: a note instead.
    /// </summary>
    /// <returns>Whether a point was chosen (the key is used).</returns>
    public bool DeleteSelectedMeasurePoint()
    {
        if (SelectedMeasurement is not { } row || SelectedMeasurePoint is not { } index) return false;
        var m = row.Measurement;
        var closed = m.Kind == MeasureKind.Area;
        if (m.Kind is not (MeasureKind.Polyline or MeasureKind.Area or MeasureKind.Radius))
        {
            Notify(Tr.T("Bei dieser Messung lassen sich keine Punkte entfernen."));
            return true;
        }
        if (m.Kind == MeasureKind.Radius)
        {
            // a circle or arc through its points: three at least
            if (m.Points.Count <= 3)
            {
                Notify(Tr.T("Ein Bogen braucht mindestens drei Punkte."));
                return true;
            }
            var rest = m.Points.Where((_, i) => i != index).ToList();
            if (Remeasured(row, rest, [], Tr.T("Punkt entfernen"))) SelectedMeasurePoint = null;
            return true;
        }
        if (MeasureGeometry.Remove(m.Points, m.ArcPoints, closed, index) is not { } removed)
        {
            Notify(closed ? Tr.T("Eine Fläche braucht mindestens drei Punkte.") : Tr.T("Ein Linienzug braucht mindestens zwei Punkte."));
            return true;
        }
        if (Remeasured(row, removed.Points, removed.ArcPoints, Tr.T("Punkt entfernen"))) SelectedMeasurePoint = null;
        return true;
    }

    /// <summary>Measures a row anew with changed points (a dimension saved in one undo step); false if they measure nothing.</summary>
    private bool Remeasured(MeasureRow row, IReadOnlyList<(double X, double Y)> points, IReadOnlyList<int> arcPoints, string description)
    {
        var measured = MeasureAgain(row, row.Measurement.Kind, points, arcPoints);
        if (measured.Value is null) return false;
        row.Measurement = measured;
        if (ReferenceEquals(row, SelectedMeasurement))
        {
            MeasureText = measured.Text;
            MeasureDetail = Detail(measured);
        }
        MeasureChanged?.Invoke();
        if (row.IsSaved) SaveDimensions([row], description);
        return true;
    }

    // ------------------------------------------------------------ As dimensions into the file (Ablaufplan 7.6 f)

    /// <summary>Saves the measurement chosen as a dimension on its page (one undo step); it leaves the list of measurements.</summary>
    [RelayCommand]
    private void InsertSelectedDimension()
    {
        if (SelectedMeasurement is { IsSession: true } row) InsertDimensions([row]);
    }

    /// <summary>Saves all measurements of the session as dimensions (one undo step).</summary>
    [RelayCommand]
    private void InsertAllDimensions() => InsertDimensions([.. Measurements.Where(r => r.IsSession)]);

    /// <summary>
    /// Puts measurements as dimensions on their pages — in their colour, width, fill, with their pieces and angles as switched, labels
    /// upright as the page is shown, the scale as /Measure (docs/FEHLERGEDAECHTNIS.md, No. 587). They are elements of the page then (moved and
    /// deleted like others, saved with the file) and stay in the list as dimensions (Ablaufplan 7.6 h, No. 605).
    /// </summary>
    private void InsertDimensions(IReadOnlyList<MeasureRow> rows)
    {
        if (rows.Count == 0) return;
        if (!CanAnnotate)   // dimensions are annotations
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return;
        }
        var byPage = new Dictionary<int, List<PageElement>>();
        var done = new List<MeasureRow>();
        PageElement? chosen = null;
        foreach (var row in rows)
        {
            if ((uint)row.Page >= (uint)History.Count || row.IsSaved) continue;
            // labels upright as the page is shown by any reader — its /Rotate, not a rotation of this view only (No. 598)
            if (ElementAppearance.Dimension(row.Measurement, row.Rgb, row.StrokeWidth, row.Fill, row.ShowSegments, row.ShowAngles,
                    History.Pages[row.Page].Page.Rotate, Detail(row.Measurement)) is not { } dimension) continue;
            if (ReferenceEquals(row, SelectedMeasurement)) chosen = dimension;
            if (!byPage.TryGetValue(row.Page, out var list)) byPage[row.Page] = list = [];
            list.Add(dimension);
            done.Add(row);
        }
        if (done.Count == 0) return;
        // the rows of the session go; the dimensions come back as rows of their own — the chosen one stays chosen
        SelectedMeasurement = null;
        foreach (var row in done) Measurements.Remove(row);
        _selectAfterSync = chosen;
        History.AddElements(byPage.ToDictionary(p => p.Key, p => (IReadOnlyList<PageElement>)p.Value),
            done.Count == 1 ? Tr.T("Als Bemaßung einfügen") : Tr.T("Messungen als Bemaßungen einfügen"));
        Notify(done.Count == 1 ? Tr.T("Als Bemaßung eingefügt — sie wird mit der Datei gespeichert; andere Programme messen dasselbe.")
            : Tr.T("{0} Messungen als Bemaßungen eingefügt — sie werden mit der Datei gespeichert; andere Programme messen dasselbe.", done.Count));
        MeasureChanged?.Invoke();
    }

    /// <summary>
    /// The tool is switched on or off: off, the measurements go — those not in the PDF after a question, with a view (they are kept for it,
    /// <see cref="AskLeaveMeasureAsync"/>).
    /// </summary>
    private void MeasureToolChanged(ViewTool oldValue, ViewTool newValue)
    {
        IReadOnlyList<MeasureRow>? parked = null;
        Func<ChoiceRequest, Task<ChoiceAnswer?>>? ask = null;
        if (oldValue == ViewTool.Measure && newValue != ViewTool.Measure)
        {
            parked = KeepForLeaveQuestion(out ask);
            ClearMeasurePoints();
            MeasurePage = -1;
            MeasureHover = null;
            SelectedMeasurement = null;
            Measurements.Clear();
            _boundDimensions.Clear();
            MeasureText = "";
            MeasureDetail = "";
        }
        if (oldValue == ViewTool.Measure && newValue != ViewTool.Measure)
        {
            CancelCalibration();
            _snapLists.Clear();   // the drawings kept for snapping go back to the shared cache (No. 625)
            _snapOrder.Clear();
        }
        OnPropertyChanged(nameof(IsMeasuring));
        if (newValue == ViewTool.Measure)
        {
            ShowScaleOf(CurrentPage is { } shown ? shown.Number - 1 : 0);
            SyncDimensions();   // the dimensions on the pages are measurements now (No. 605)
            Update();
        }
        MeasureChanged?.Invoke();
        if (parked is not null && ask is not null) _ = AskLeaveMeasureAsync(parked, ask);
    }
}
