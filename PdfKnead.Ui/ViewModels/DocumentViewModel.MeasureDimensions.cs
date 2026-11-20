using System.ComponentModel;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Localization;

namespace PdfKnead.ViewModels;

/// <summary>
/// Saved dimensions as measurements (Ablaufplan 7.6 h; docs/FEHLERGEDAECHTNIS.md, No. 605; decided on 6.11.2026): with „Messen“ on,
/// the dimensions on the pages — inserted in this session, saved, or written by another program as the standard says — are measurements in the
/// list, with handles and bar like the others. Each change (a point dragged, colour, width, fill, pieces and angles, as an arc) replaces the
/// dimension on its page in one undo step; deleting deletes it. A dimension of another program becomes one of PDF Knead when it is changed. No
/// lock for other programs (/F Locked): they may change it too, and then their points count (<see cref="Dimensions.Reconcile"/>).
/// </summary>
public sealed partial class DocumentViewModel
{
    // the elements shown as measurements now (the view leaves them out: „Messen“ draws them, with handles)
    private readonly HashSet<PageElement> _boundDimensions = new(ReferenceEqualityComparer.Instance);

    // while the rows are filled from the pages (or several changed at once): their changes are not saved one by one
    private bool _syncingDimensions;

    // the dimension a row is to be chosen by after the next filling (one just inserted)
    private PageElement? _selectAfterSync;

    /// <summary>Whether an element of a page is shown as a measurement now — the view does not draw it then, „Messen“ does.</summary>
    public bool IsBoundDimension(PageElement element) => IsMeasuring && _boundDimensions.Contains(element);

    /// <summary>What an element of a page is as a dimension: one of PDF Knead as it is, one of another program when it is one; else null.</summary>
    private static DimensionElement? DimensionOf(PageItem item, PageElement element) => element switch
    {
        DimensionElement own => own,
        // annotations drawn by the tiles (pages with very many) stay as they are — the view could not leave them out
        ForeignAnnotation foreign when !item.InTiles(foreign) => ForeignDimensions.GetValue(foreign, f => new(Dimensions.Of(f, item.Page.Rotate))).Value,
        _ => null,
    };

    // what an annotation of another program is as a dimension, worked out once (it does not change; the list is filled after every change)
    private static readonly ConditionalWeakTable<ForeignAnnotation, StrongBox<DimensionElement?>> ForeignDimensions = new();

    /// <summary>Measures the points of a dimension with its own scale (as saved; on the paper without one).</summary>
    private Measurement MeasureSaved(int page, MeasureKind kind, IReadOnlyList<(double X, double Y)> points, RectilinearMeasure? measure,
        IReadOnlyList<int>? arcPoints = null) =>
        Dimensions.MeasureWith(kind, points, measure, UserUnitOf(page), MeasureDecimals.Value, arcPoints);

    /// <summary>
    /// Measures a row anew after a change: a measurement of this session with the scale of the page, a dimension with it too where the page has
    /// one at its first point, else with its own scale (one of another program on a page without /VP keeps what it measured with).
    /// </summary>
    private Measurement MeasureAgain(MeasureRow row, MeasureKind kind, IReadOnlyList<(double X, double Y)> points, IReadOnlyList<int>? arcPoints = null) =>
        row.Element is null || points.Count > 0 && PageScales.At(ScalesOf(row.Page), points[0].X, points[0].Y)?.Measure is not null
            ? MeasureOn(row.Page, kind, points, arcPoints)
            : MeasureSaved(row.Page, kind, points, row.OwnMeasure, arcPoints);

    /// <summary>
    /// Fills the list with the dimensions of the pages: rows for new ones (first in the list, in the order of the pages), the same row for a
    /// dimension replaced in its place (a change, undo, redo — so it stays chosen), none for those that are gone.
    /// </summary>
    private void SyncDimensions()
    {
        if (!IsMeasuring)
        {
            _boundDimensions.Clear();
            return;
        }
        _syncingDimensions = true;
        try
        {
            var found = new List<(int Page, int Index, PageElement Element, DimensionElement Dimension)>();
            for (var p = 0; p < Pages.Count; p++)
            {
                var item = Pages[p].Item;
                for (var i = 0; i < item.Elements.Count; i++)
                    if (DimensionOf(item, item.Elements[i]) is { } dimension) found.Add((p, i, item.Elements[i], dimension));
            }
            var still = new HashSet<PageElement>(found.Select(f => f.Element), ReferenceEqualityComparer.Instance);
            var rows = Measurements.Where(r => r.Element is not null).ToList();
            var used = new HashSet<MeasureRow>(ReferenceEqualityComparer.Instance);
            var result = new List<MeasureRow>(found.Count);
            foreach (var (page, index, element, dimension) in found)
            {
                var row = rows.FirstOrDefault(r => !used.Contains(r) && ReferenceEquals(r.Element, element))
                          ?? rows.FirstOrDefault(r => !used.Contains(r) && r.Page == page && r.ElementIndex == index && !still.Contains(r.Element!));
                if (row is null)
                {
                    row = new MeasureRow(page, MeasureSaved(page, dimension.Kind, dimension.PagePoints, dimension.Measure, dimension.ArcPoints), null);
                    row.PropertyChanged += BoundRowChanged;
                }
                Fill(row, page, index, element, dimension);
                used.Add(row);
                result.Add(row);
            }
            for (var i = Measurements.Count - 1; i >= 0; i--)
            {
                var row = Measurements[i];
                if (row.Element is null || used.Contains(row)) continue;
                if (ReferenceEquals(row, SelectedMeasurement)) SelectedMeasurement = null;
                row.PropertyChanged -= BoundRowChanged;
                Measurements.RemoveAt(i);
            }
            for (var k = 0; k < result.Count; k++)
            {
                var at = Measurements.IndexOf(result[k]);
                if (at < 0) Measurements.Insert(k, result[k]);
                else if (at != k) Measurements.Move(at, k);
            }
            _boundDimensions.Clear();
            _boundDimensions.UnionWith(still);
            if (_selectAfterSync is { } wanted)
            {
                _selectAfterSync = null;
                if (result.FirstOrDefault(r => ReferenceEquals(r.Element, wanted)) is { } chosen) SelectedMeasurement = chosen;
            }
        }
        finally
        {
            _syncingDimensions = false;
        }
        MeasureChanged?.Invoke();
    }

    /// <summary>A row as the dimension is: its points measured with its scale, its colour, width, fill and switches.</summary>
    private void Fill(MeasureRow row, int page, int index, PageElement element, DimensionElement d)
    {
        row.Bind(element, d.Measure, d.PagePoints, index);
        row.Page = page;
        row.PageId = Pages[page].Item.Id;
        row.Measurement = MeasureSaved(page, d.Kind, d.PagePoints, d.Measure, d.ArcPoints);
        row.Color = MeasureColors.FirstOrDefault(c => c.Rgb == d.Color);
        row.Rgb = d.Color;
        row.Width = MeasureWidths.FirstOrDefault(w => Math.Abs(w.Value - d.StrokeWidth) < 1e-6);
        row.StrokeWidth = d.StrokeWidth;
        row.FillOpacity = MeasureOpacities.FirstOrDefault(o => Math.Abs(o.Value - d.FillOpacity) < 1e-6);
        row.Fill = d.FillOpacity;
        row.ShowSegments = d.ShowSegments;
        row.ShowAngles = d.ShowAngles;
        row.LabelAt = row.SavedLabelAt = d.PageLabelAt;
    }

    /// <summary>A setting of a dimension changed (colour, width, fill, pieces, angles): saved on its page.</summary>
    private void BoundRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        MeasureChanged?.Invoke();
        if (_syncingDimensions || sender is not MeasureRow { Element: not null } row) return;
        if (e.PropertyName is nameof(MeasureRow.Color) or nameof(MeasureRow.Width) or nameof(MeasureRow.FillOpacity) or nameof(MeasureRow.ShowSegments)
            or nameof(MeasureRow.ShowAngles))
            SaveDimensions([row], Tr.T("Bemaßung ändern"));
    }

    /// <summary>
    /// The label of a measurement's value let go at <paramref name="at"/> (page space, within the rules as the view drew it), or put back to its own
    /// place (null; double-click on it): a dimension is saved in one undo step (design decision, docs/FEHLERGEDAECHTNIS.md, No. 703).
    /// </summary>
    public void SetMeasureLabel(MeasureRow row, (double X, double Y)? at)
    {
        row.LabelAt = at;
        MeasureChanged?.Invoke();
        if (row.Element is null || Nullable.Equals(row.SavedLabelAt, at)) return;
        SaveDimensions([row], at is null ? Tr.T("Beschriftung zurücksetzen") : Tr.T("Beschriftung verschieben"));
    }

    /// <summary>A handle let go: a dimension whose point moved is saved (one undo step for the whole drag).</summary>
    public void EndMeasureDrag(MeasureRow row)
    {
        if (row.Element is null || row.Measurement.Points.SequenceEqual(row.SavedPoints)) return;
        SaveDimensions([row], Tr.T("Bemaßung ändern"));
    }

    /// <summary>A switch set for all measurements (pieces, angles): the dimensions among them change in one undo step.</summary>
    private void SetOnAll(Action<MeasureRow> set)
    {
        var changed = new List<MeasureRow>();
        _syncingDimensions = true;
        try
        {
            foreach (var row in Measurements)
            {
                var before = (row.ShowSegments, row.ShowAngles);
                set(row);
                if (row.Element is not null && before != (row.ShowSegments, row.ShowAngles)) changed.Add(row);
            }
        }
        finally
        {
            _syncingDimensions = false;
        }
        SaveDimensions(changed, Tr.T("Bemaßungen ändern"));
    }

    /// <summary>The dimension a row is to be now (laid out anew; labels upright as its page is shown); null if it does not measure.</summary>
    private DimensionElement? DimensionFor(MeasureRow row) =>
        (uint)row.Page >= (uint)History.Count ? null
            : ElementAppearance.Dimension(row.Measurement, row.Rgb, row.StrokeWidth, row.Fill, row.ShowSegments, row.ShowAngles, History.Pages[row.Page].Page.Rotate,
                Detail(row.Measurement), row.LabelAt);

    /// <summary>
    /// Saves changed rows on their pages: one row in one undo step (an annotation its author locked is asked about first, No. 541), several
    /// together; without the right to change, or if nothing measures, the rows go back to what the pages say.
    /// </summary>
    private void SaveDimensions(IReadOnlyList<MeasureRow> rows, string description)
    {
        if (rows.Count == 0) return;
        if (!CanEditElements)
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            SyncDimensions();
            return;
        }
        var changes = new List<(int Page, PageElement Old, PageElement Updated)>();
        foreach (var row in rows)
            if (row.Element is { } old && (rows.Count == 1 || old is not ForeignAnnotation { IsLocked: true }) && DimensionFor(row) is { } updated)
                changes.Add((row.Page, old, updated));
        if (changes.Count == 0)
        {
            SyncDimensions();
            return;
        }
        if (changes.Count > 1)
        {
            if (!History.ReplaceElements(changes, description)) SyncDimensions();
            return;
        }
        var (page, element, dimension) = changes[0];
        void Replace()
        {
            if (!History.ReplaceElement(page, element, dimension, description)) SyncDimensions();
        }
        if (LockedAsks(element, dimension, Replace))
        {
            SyncDimensions();   // as it is until „Trotzdem ändern“
            return;
        }
        Replace();
    }

    /// <summary>Deletes the dimension of a row from its page (one undo step; a locked annotation is asked about first).</summary>
    private void DeleteDimension(MeasureRow row)
    {
        if (row.Element is not { } element) return;
        if (!CanEditElements)
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return;
        }
        var page = row.Page;
        void Delete()
        {
            if (ReferenceEquals(SelectedMeasurement, row)) SelectedMeasurement = null;
            History.RemoveElements(page, new HashSet<PageElement>([element], ReferenceEqualityComparer.Instance), Tr.T("Bemaßung löschen"));
        }
        if (!LockedAsks(element, null, Delete)) Delete();
    }

    /// <summary>
    /// A scale set for a page (<see cref="SetScale"/>): its dimensions measured anew with it, in the same undo step — they are measurements, and
    /// a scale is set because the one before was wrong. Locked annotations of other programs stay as they are.
    /// </summary>
    /// <param name="page">The page (index).</param>
    /// <param name="item">The page with the new scale.</param>
    /// <param name="count">Counts the dimensions measured anew.</param>
    private PageItem WithDimensionsMeasured(int page, PageItem item, ref int count)
    {
        var viewports = PageScales.Of(item.Page.Dictionary);
        PageElement[]? elements = null;
        foreach (var row in Measurements)
        {
            if (row.Page != page || row.Element is not { } element || element is ForeignAnnotation { IsLocked: true }) continue;
            elements ??= [.. item.Elements];
            var at = Array.FindIndex(elements, e => ReferenceEquals(e, element));
            if (at < 0) continue;
            var measured = Measuring.Measure(row.Measurement.Kind, row.Measurement.Points, viewports, item.Page.UserUnit, MeasureDecimals.Value,
                row.Measurement.ArcPoints);
            if (measured.Value is null || ElementAppearance.Dimension(measured, row.Rgb, row.StrokeWidth, row.Fill, row.ShowSegments, row.ShowAngles,
                    item.Page.Rotate, Detail(measured)) is not { } dimension) continue;
            elements[at] = dimension;
            count++;
        }
        return elements is null ? item : item.WithElements(elements);
    }
}
