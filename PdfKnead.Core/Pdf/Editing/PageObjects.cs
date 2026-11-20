using System.Collections;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// The images and drawings of a page as they are now (see KONZEPT.md, section 5.20): those the content draws (found once, see
/// <see cref="ContentObjects.Find"/>) with the edits of the page entry on top — moved, deleted, edited together. Making the view for new edits
/// costs only the edits, not the page: moving one line of a plan a hundred times does not read the plan a hundred times.
/// As a list: the objects not deleted, in drawing order; an <see cref="ContentObject"/> is made each time one is asked for — for many objects,
/// <see cref="At"/>, <see cref="GroupOf"/>, <see cref="KeyOf"/> and <see cref="VisibleOf"/> do without.
/// </summary>
public sealed class PageObjects : IReadOnlyList<ContentObject>
{
    private readonly ContentTable _table;
    private readonly Dictionary<int, (Matrix Move, RectD Visible)>? _moved;   // entry → its move and where it can be seen now
    private readonly HashSet<int>? _wholeGroup;   // moved entries whose whole graphic moved as one block
    private readonly int[]? _present;      // index → entry, if some are deleted
    private readonly int[]? _presentOf;    // entry → index (−1: deleted), if some are deleted
    private readonly Dictionary<int, int[]>? _editedTogether;   // entry → the entries it was edited with
    private int[]? _images;

    /// <summary>A page without images or drawings (or one that cannot be read).</summary>
    public static PageObjects Empty { get; } = new(Finished(new ContentTable()), null);

    private static ContentTable Finished(ContentTable table)
    {
        table.Finish(CancellationToken.None);
        return table;
    }

    internal PageObjects(ContentTable table, ContentObjectEdits? edits)
    {
        _table = table;
        if (edits is null) return;
        // a whole graphic moved by the same matrix moves as one block, with its clips (ContentObject.MovedAsGroup)
        var together = new Dictionary<(int Group, Matrix Move), int>();
        foreach (var (key, move) in edits.Moves)
            if (move != Matrix.Identity && move.IsFinite && !edits.Deleted.Contains(key) && Entry(key) is { } e && table[e].Group >= 0)
                together[(table[e].Group, move)] = together.GetValueOrDefault((table[e].Group, move)) + 1;
        foreach (var (key, move) in edits.Moves)
        {
            if (move == Matrix.Identity || !move.IsFinite || Entry(key) is not { } e) continue;
            var o = table.Make(e);
            var whole = table[e].Group >= 0 && ContentObject.MovesAsGroup(o, together.GetValueOrDefault((table[e].Group, move)));
            (_moved ??= [])[e] = (move, (whole ? o.MovedAsGroup(move) : o.Moved(move)).Visible);
            if (whole) (_wholeGroup ??= []).Add(e);
        }
        var deleted = edits.Deleted.Select(Entry).OfType<int>().ToHashSet();
        if (deleted.Count > 0)
        {
            _presentOf = new int[table.Count];
            var present = new List<int>(table.Count - deleted.Count);
            for (var i = 0; i < table.Count; i++)
            {
                _presentOf[i] = deleted.Contains(i) ? -1 : present.Count;
                if (_presentOf[i] >= 0) present.Add(i);
            }
            _present = [.. present];
        }
        foreach (var group in edits.Groups)
        {
            var members = group.Select(Entry).OfType<int>().Where(e => !deleted.Contains(e)).Distinct().Order().ToArray();
            foreach (var m in members) (_editedTogether ??= [])[m] = members;
        }
    }

    /// <summary>The dictionary of the page the objects were found in (null for <see cref="Empty"/>).</summary>
    public Objects.PdfDictionary? Page => _table.Page;

    /// <summary>The same page without edits: where the content draws its objects.</summary>
    public PageObjects Original => _moved is null && _present is null && _editedTogether is null ? this : new PageObjects(_table, null);

    /// <summary>The same objects with <paramref name="edits"/> (of the page whose content they were found in) on top.</summary>
    /// <param name="edits">The edits; null for none.</param>
    /// <returns>The view.</returns>
    public PageObjects WithEdits(ContentObjectEdits? edits) => edits is null ? Original : new PageObjects(_table, edits);

    /// <summary>
    /// Not every object of the page could be taken: it has more than the memory allows (a prepared file); those drawn last cannot be chosen.
    /// </summary>
    public bool Incomplete => _table.Truncated;

    /// <summary>Number of objects (not deleted).</summary>
    public int Count => _present?.Length ?? _table.Count;

    /// <summary>An object as it is now (made anew on each call).</summary>
    /// <param name="index">Index in drawing order.</param>
    public ContentObject this[int index]
    {
        get
        {
            var e = EntryAt(index);
            var o = _table.Make(e);
            return _moved is not null && _moved.TryGetValue(e, out var m) ? _wholeGroup?.Contains(e) == true ? o.MovedAsGroup(m.Move) : o.Moved(m.Move) : o;
        }
    }

    /// <summary>The key of an object.</summary>
    /// <param name="index">Index in drawing order.</param>
    /// <returns>Its content stream and operator.</returns>
    public ContentKey KeyOf(int index)
    {
        ref var e = ref _table[EntryAt(index)];
        return new ContentKey(_table.Scopes[e.Scope], e.Op);
    }

    /// <summary>Where an object can be seen now (see <see cref="ContentObject.Visible"/>).</summary>
    /// <param name="index">Index in drawing order.</param>
    /// <returns>The visible part in page space.</returns>
    public RectD VisibleOf(int index)
    {
        var e = EntryAt(index);
        return _moved is not null && _moved.TryGetValue(e, out var m) ? m.Visible : _table.Visible(e);
    }

    /// <summary>Whether an object is a drawing (see <see cref="ContentObject.IsDrawing"/>).</summary>
    /// <param name="index">Index in drawing order.</param>
    /// <returns>True for a path or shading.</returns>
    public bool IsDrawingAt(int index) => _table[EntryAt(index)].Kind != ContentKind.Image;

    /// <summary>The images among the objects (indices in drawing order).</summary>
    public IReadOnlyList<int> Images => _images ??= [.. Of(_table.ImageEntries)];

    /// <summary>The objects (not deleted, in drawing order) in the content of a form (<see cref="ContentKey.Scope"/>) and the forms it invokes.</summary>
    internal IEnumerable<int> InForm(string scope) => _table.InScope(scope).Select(Index).Where(i => i >= 0).Order();

    /// <summary>The index of the object with <paramref name="key"/>; null if there is none (any more).</summary>
    /// <param name="key">Its key.</param>
    /// <returns>The index, or null.</returns>
    public int? IndexOf(ContentKey key) => Entry(key) is { } e && Index(e) is var i && i >= 0 ? i : null;

    /// <summary>The objects with these keys that are there, in drawing order.</summary>
    /// <param name="keys">Their keys.</param>
    /// <returns>The objects as they are now.</returns>
    public IReadOnlyList<ContentObject> Of(IEnumerable<ContentKey> keys) =>
        [.. keys.Select(IndexOf).OfType<int>().Distinct().Order().Select(i => this[i])];

    /// <summary>
    /// The object under the point, by its real shape (a diagonal line is hit on the line, not anywhere in its box); of several, the smallest (the
    /// title over a picture, the photo over a background), of those the last drawn.
    /// </summary>
    /// <param name="x">x in page space.</param>
    /// <param name="y">y in page space.</param>
    /// <param name="tolerance">Allowance in page units.</param>
    /// <returns>Its index; null if none.</returns>
    public int? At(double x, double y, double tolerance)
    {
        int? best = null;
        var bestArea = double.MaxValue;
        foreach (var e in Near(RectD.FromLtrb(x - tolerance, y - tolerance, x + tolerance, y + tolerance)))
        {
            var visible = _moved is not null && _moved.TryGetValue(e, out var m) ? m.Visible : _table.Visible(e);
            if (x < visible.Left - tolerance || x > visible.Right + tolerance || y < visible.Top - tolerance || y > visible.Bottom + tolerance) continue;
            var area = visible.Width * visible.Height;
            var index = Index(e);
            // smaller wins; of the same size the one drawn later (on top)
            if (area > bestArea || area == bestArea && index < best || !this[index].Hits(x, y, tolerance)) continue;
            (best, bestArea) = (index, area);
        }
        return best;
    }

    /// <summary>
    /// Every object under a point, in the order <see cref="At"/> prefers them: the smallest first, of the same size the one drawn later (on
    /// top). Alt+click in „Bearbeiten“ walks through them.
    /// </summary>
    /// <param name="x">x in page space.</param>
    /// <param name="y">y in page space.</param>
    /// <param name="tolerance">Allowance in page units.</param>
    /// <returns>Their indices; empty if none.</returns>
    public IReadOnlyList<int> AllAt(double x, double y, double tolerance)
    {
        var hits = new List<(int Index, double Area)>();
        foreach (var e in Near(RectD.FromLtrb(x - tolerance, y - tolerance, x + tolerance, y + tolerance)))
        {
            var visible = _moved is not null && _moved.TryGetValue(e, out var m) ? m.Visible : _table.Visible(e);
            if (x < visible.Left - tolerance || x > visible.Right + tolerance || y < visible.Top - tolerance || y > visible.Bottom + tolerance) continue;
            var index = Index(e);
            if (this[index].Hits(x, y, tolerance)) hits.Add((index, visible.Width * visible.Height));
        }
        hits.Sort((a, b) => a.Area != b.Area ? a.Area.CompareTo(b.Area) : b.Index.CompareTo(a.Index));
        return hits.Select(h => h.Index).ToList();
    }

    /// <summary>
    /// The objects that belong together with object <paramref name="index"/>: those it was edited with; for a drawing its graphic (a block of
    /// drawings, a form, a word of outlined letters); for an image the tiles of one picture — they meet at an edge (touching or overlapping by at
    /// most <paramref name="seam"/> points), are upright and have the same resolution. An image lying on top of another (a photo on a background)
    /// overlaps it widely and does not belong to it.
    /// </summary>
    /// <param name="index">The object clicked.</param>
    /// <param name="seam">Largest gap or overlap at a seam, in points.</param>
    /// <returns>Indices in drawing order (at least <paramref name="index"/> itself); empty for an index outside.</returns>
    public IReadOnlyList<int> GroupOf(int index, double seam = 1.5)
    {
        if ((uint)index >= (uint)Count) return [];
        var entry = EntryAt(index);
        if (_editedTogether is not null && _editedTogether.TryGetValue(entry, out var together)) return [.. Of(together)];
        ref var e = ref _table[entry];
        if (e.Kind != ContentKind.Image) return e.Group < 0 ? [index] : [.. Of(_table.GroupMembers(e.Group))];
        var first = this[index];
        if (!first.AxisAligned) return [index];
        var group = new SortedDictionary<int, ContentObject> { [index] = first };
        var queue = new Queue<ContentObject>([first]);
        while (queue.TryDequeue(out var a))
        {
            var boxA = a.Visible.Inflate(seam);
            foreach (var near in Near(boxA))
            {
                var j = Index(near);
                if (_table[near].Kind != ContentKind.Image || group.ContainsKey(j)) continue;
                var b = this[j];
                if (!b.AxisAligned || !SameResolution(a, b)) continue;
                var boxB = b.Visible;
                if (!boxA.Intersects(boxB)) continue;
                // meeting at an edge: the common part is thin in one direction and long in the other
                var common = boxA.Intersect(boxB.Inflate(seam));
                var thin = Math.Min(common.Width, common.Height);
                var along = Math.Max(common.Width, common.Height);
                if (thin > 3 * seam || along < 4 * seam) continue;
                group[j] = b;
                queue.Enqueue(b);
            }
        }
        return [.. group.Keys];
    }

    private static bool SameResolution(ContentObject a, ContentObject b)
    {
        static bool Near(double x, double y) => x > 0 && y > 0 && Math.Abs(x - y) <= 0.03 * Math.Max(x, y);
        return Near(a.DpiX, b.DpiX) && Near(a.DpiY, b.DpiY) || Near(a.DpiX, b.DpiY) && Near(a.DpiY, b.DpiX);
    }

    /// <summary>The entries not deleted that may lie in <paramref name="area"/> now, each once.</summary>
    private HashSet<int> Near(RectD area)
    {
        var found = new List<int>();
        _table.Near(area, found);
        var result = new HashSet<int>();
        foreach (var e in found)
            if (Index(e) >= 0 && (_moved is null || !_moved.ContainsKey(e))) result.Add(e);
        if (_moved is not null)
            foreach (var (e, m) in _moved)
                if (Index(e) >= 0 && ContentTable.Valid(m.Visible) && m.Visible.Intersects(area)) result.Add(e);
        return result;
    }

    private int? Entry(ContentKey key) => _table.ScopeIndex(key.Scope) is var scope && scope >= 0 && _table.Find(scope, key.Op) is var e && e >= 0 ? e : null;

    private int EntryAt(int index) =>
        (uint)index < (uint)Count ? _present?[index] ?? index : throw new ArgumentOutOfRangeException(nameof(index));

    private int Index(int entry) => _presentOf?[entry] ?? entry;

    private IEnumerable<int> Of(ReadOnlySpan<int> entries)
    {
        var result = new List<int>(entries.Length);
        foreach (var e in entries)
            if (Index(e) is var i && i >= 0) result.Add(i);
        return result;
    }

    /// <inheritdoc />
    public IEnumerator<ContentObject> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
