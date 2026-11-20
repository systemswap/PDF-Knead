namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>
/// Areas in page space, asked which of them lie near a box: with few areas all are looked at, with many only those in the cells of a grid
/// the box touches. A mark over hidden text can hold one area per line, and a crafted page hundreds of thousands of lines — testing each
/// character against every area grew with the square of their number (docs/FEHLERGEDAECHTNIS.md, No. 285).
/// </summary>
internal sealed class AreaIndex
{
    /// <summary>Up to this many areas every query looks at all of them.</summary>
    private const int Direct = 64;

    /// <summary>An area spanning more cells than this is kept in a list looked at by every query.</summary>
    private const int MaxCellsPerArea = 64;

    private readonly List<RectD> _areas;
    private readonly List<int>?[]? _cells;
    private readonly List<int> _wide = [];
    private readonly double _left, _top, _cellWidth, _cellHeight;
    private readonly int _columns, _rows;

    /// <summary>Indexes the areas (empty ones included: they are found by nothing).</summary>
    /// <param name="areas">The areas; the list is kept, not copied.</param>
    public AreaIndex(List<RectD> areas)
    {
        _areas = areas;
        if (areas.Count <= Direct) return;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var a in areas)
        {
            if (!Finite(a)) continue;
            left = Math.Min(left, a.Left);
            top = Math.Min(top, a.Top);
            right = Math.Max(right, a.Right);
            bottom = Math.Max(bottom, a.Bottom);
        }
        if (left > right || top > bottom)
        {
            for (var i = 0; i < areas.Count; i++) _wide.Add(i);
            return;
        }
        // about one cell per area, the cells about square: areas lying in a long strip (rotated text beside the page) spread over many
        // cells along it instead of each spanning all columns of a square grid
        double width = Math.Max(right - left, 1e-6), height = Math.Max(bottom - top, 1e-6);
        var size = Math.Sqrt(width * height / areas.Count);
        var columns = (int)Math.Clamp(Math.Ceiling(width / size), 1, 4096);
        var rows = (int)Math.Clamp(Math.Ceiling(height / size), 1, 4096);
        (_left, _top, _columns, _rows) = (left, top, columns, rows);
        _cellWidth = width / columns;
        _cellHeight = height / rows;
        _cells = new List<int>?[columns * rows];
        for (var i = 0; i < areas.Count; i++)
        {
            var a = areas[i];
            if (!Finite(a))
            {
                _wide.Add(i);
                continue;
            }
            var (c0, r0, c1, r1) = Cells(a);
            if ((long)(c1 - c0 + 1) * (r1 - r0 + 1) > MaxCellsPerArea)
            {
                _wide.Add(i);
                continue;
            }
            for (var r = r0; r <= r1; r++)
                for (var c = c0; c <= c1; c++)
                    (_cells[r * _columns + c] ??= []).Add(i);
        }
    }

    /// <summary>The areas, in the order given.</summary>
    public IReadOnlyList<RectD> Areas => _areas;

    /// <summary>Whether <paramref name="test"/> holds for one of the areas that may touch <paramref name="box"/> (the box widened by
    /// <paramref name="margin"/>); areas farther away are not tested.</summary>
    /// <param name="box">The box in page space.</param>
    /// <param name="margin">How far an area may lie beside the box and still be tested.</param>
    /// <param name="test">The test for an area.</param>
    /// <returns>True as soon as one area passes.</returns>
    public bool Any(RectD box, double margin, Func<RectD, bool> test)
    {
        if (_cells is null)
        {
            foreach (var i in _wide) if (test(_areas[i])) return true;
            if (_wide.Count == 0)
                foreach (var a in _areas) if (test(a)) return true;
            return false;
        }
        foreach (var i in _wide)
            if (test(_areas[i])) return true;
        var wide = RectD.FromLtrb(box.Left - margin, box.Top - margin, box.Right + margin, box.Bottom + margin);
        if (!Finite(wide)) return _areas.Any(test);
        var (c0, r0, c1, r1) = Cells(wide);
        for (var r = r0; r <= r1; r++)
            for (var c = c0; c <= c1; c++)
                if (_cells[r * _columns + c] is { } list)
                    foreach (var i in list)
                        if (test(_areas[i])) return true;
        return false;
    }

    /// <summary>The cells a box covers, clamped to the grid.</summary>
    private (int C0, int R0, int C1, int R1) Cells(RectD box) =>
        (Cell(box.Left, _left, _cellWidth, _columns), Cell(box.Top, _top, _cellHeight, _rows),
         Cell(box.Right, _left, _cellWidth, _columns), Cell(box.Bottom, _top, _cellHeight, _rows));

    private static int Cell(double value, double origin, double size, int count) =>
        (int)Math.Clamp(Math.Floor((value - origin) / size), 0, count - 1);

    private static bool Finite(RectD r) =>
        double.IsFinite(r.Left) && double.IsFinite(r.Top) && double.IsFinite(r.Right) && double.IsFinite(r.Bottom);
}
