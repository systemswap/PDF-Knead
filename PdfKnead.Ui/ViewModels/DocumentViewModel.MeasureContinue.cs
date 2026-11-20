using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Localization;

namespace PdfKnead.ViewModels;

/// <summary>
/// Going on with a finished polyline (Ablaufplan 7.6 l; decided on 8.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 623): a double-click on its
/// line or „Fortsetzen“ in the bar makes it the measurement going on again; the next click decides the end — the one nearer to it, the start
/// or the end — and from there it goes on with everything a polyline can (arcs with Ctrl+Shift and Ctrl+Alt, Backspace, Enter). Ending it
/// changes the measurement (a dimension in one undo step); dropping it (Esc → „Verwerfen“, right click) leaves it as it was.
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>The finished polyline being continued; null when none.</summary>
    private MeasureRow? _continuing;

    /// <summary>Whether it goes on at its start (its points are kept reversed meanwhile); null until the first click decided it.</summary>
    private bool? _continueAtStart;

    /// <summary>Whether a measurement is being continued (the view draws it as the one going on, not as itself).</summary>
    public bool IsContinuing(MeasureRow row) => ReferenceEquals(_continuing, row);

    /// <summary>„Fortsetzen“ in the bar: the chosen polyline goes on at the end nearer to the next click.</summary>
    [RelayCommand]
    private void ContinueSelectedMeasurement()
    {
        if (SelectedMeasurement is { } row) ContinueMeasurement(row);
    }

    /// <summary>
    /// Makes a finished polyline the measurement going on again (double-click on its line, „Fortsetzen“); false for other kinds or a page that
    /// is not there. Its kind, points and arc points become those of the measurement going on.
    /// </summary>
    public bool ContinueMeasurement(MeasureRow row)
    {
        if (!IsMeasuring || row.Measurement.Kind != MeasureKind.Polyline || (uint)row.Page >= (uint)Pages.Count) return false;
        if (!CanEditElements && row.IsSaved)
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return false;
        }
        CancelMeasure();
        MeasureKind = MeasureKind.Polyline;
        SelectedMeasurement = null;
        _measurePoints.AddRange(row.Measurement.Points);
        _measureArcPoints.AddRange(row.Measurement.ArcPoints);
        MeasurePage = row.Page;
        _continuing = row;
        _continueAtStart = null;
        Notify(Tr.T("Linienzug fortsetzen: der nächste Klick setzt am näheren Ende an — Enter beendet, Esc fragt."));
        Update();
        return true;
    }

    /// <summary>
    /// Before a point is set while continuing: on the first click, the end nearer to it is taken — the start: the points are reversed, so that
    /// the new ones follow on (back in order when it ends).
    /// </summary>
    private void DecideContinuingEnd((double X, double Y) next)
    {
        if (_continuing is null || _continueAtStart is not null || _measurePoints.Count < 2) return;
        static double Distance((double X, double Y) a, (double X, double Y) b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
        _continueAtStart = Distance(next, _measurePoints[0]) < Distance(next, _measurePoints[^1]);
        if (_continueAtStart == true) Reverse(_measurePoints, _measureArcPoints);
    }

    /// <summary>
    /// The point the next piece starts from (to straighten it): the last point — continuing a polyline before its end is decided, the end nearer
    /// to <paramref name="near"/> —; none without points or when the last point is an arc point (the next one ends its arc).
    /// </summary>
    public (double X, double Y)? NextFrom((double X, double Y)? near)
    {
        if (_measurePoints.Count == 0) return null;
        if (_continuing is not null && _continueAtStart is null && near is { } at && _measurePoints.Count >= 2)
        {
            static double Distance((double X, double Y) a, (double X, double Y) b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
            if (Distance(at, _measurePoints[0]) < Distance(at, _measurePoints[^1])) return _measurePoints[0];   // the start is never an arc point
        }
        return _measureArcPoints.Contains(_measurePoints.Count - 1) ? null : _measurePoints[^1];
    }

    /// <summary>The points reversed and the arc points with them (an arc point stays between the same two points).</summary>
    private static void Reverse(List<(double X, double Y)> points, List<int> arcs)
    {
        points.Reverse();
        for (var i = 0; i < arcs.Count; i++) arcs[i] = points.Count - 1 - arcs[i];
        arcs.Sort();
    }

    /// <summary>
    /// The points and arc points for the preview while continuing before the end is decided: reversed if the point under the mouse is nearer
    /// to the start; null when not continuing or decided.
    /// </summary>
    private (List<(double X, double Y)> Points, List<int> Arcs)? ContinuingPreview((double X, double Y) hover)
    {
        if (_continuing is null || _continueAtStart is not null || _measurePoints.Count < 2) return null;
        var points = new List<(double X, double Y)>(_measurePoints);
        var arcs = new List<int>(_measureArcPoints);
        static double Distance((double X, double Y) a, (double X, double Y) b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
        if (Distance(hover, points[0]) < Distance(hover, points[^1])) Reverse(points, arcs);
        return (points, arcs);
    }

    /// <summary>
    /// Ends a continued polyline: it takes the points (in their order, new ones at the start before the old start), measured anew (a dimension
    /// with its own scale where the page has none, saved in one undo step). Null if they measure nothing — it stays as it was.
    /// </summary>
    private MeasureRow? CompleteContinuing()
    {
        var row = _continuing!;
        var points = new List<(double X, double Y)>(_measurePoints);
        var arcs = new List<int>(_measureArcPoints);
        if (_continueAtStart == true) Reverse(points, arcs);
        var measured = MeasureAgain(row, MeasureKind.Polyline, points, arcs);
        _continuing = null;
        _continueAtStart = null;
        ClearMeasurePoints();
        MeasurePage = -1;
        if (measured.Value is null)
        {
            Update();
            return null;
        }
        row.Measurement = measured;
        if (row.IsSaved) SaveDimensions([row], Tr.T("Bemaßung fortsetzen"));
        Update(measured);
        return row;
    }
}
