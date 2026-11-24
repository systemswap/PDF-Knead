namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>
/// Collects drawing commands for a <see cref="DisplayList"/>. Matrices, styles and
/// dash patterns are interned, because a CAD drawing has hundreds of thousands of
/// lines but only a handful of different styles.
/// </summary>
/// <remarks>Not thread-safe: one builder per page build. All "Add" methods return the index of the stored object.</remarks>
public sealed class DisplayListBuilder
{
    /// <summary>
    /// Maximum number of entries, as protection against files that draw endlessly (malicious or broken). 5 million entries occupy
    /// about 0.5 GB; the largest real case (an A0 plan) has 780,000 (with a limit of 20 million, a 25 KB file occupied 2.3 GB).
    /// </summary>
    public const int MaxItems = 5_000_000;

    private DisplayItem[] _items = new DisplayItem[1024];
    private int _count;
    // own arrays instead of List<T>: handed over to the display list at the end without a copy (CAD: millions of points)
    private ArrayBuilder<PathVerb> _verbs = new(256);
    private ArrayBuilder<float> _points = new(512);
    private ArrayBuilder<(int Verb, int VerbCount, int Point)> _paths = new(64);
    private readonly List<Matrix> _matrices = [];
    private readonly Dictionary<Matrix, int> _matrixIndex = [];
    private readonly List<Paint> _paints = [];
    private readonly Dictionary<Paint, int> _paintIndex = [];
    private readonly List<DashPattern> _dashes = [];
    private readonly Dictionary<DashPattern, int> _dashIndex = [];
    private readonly List<ClipNode> _clips = [new ClipNode(-1, -1, 0, false, RectD.Infinite)];
    private readonly List<ImageSource> _images = [];
    private readonly List<ShadingFill> _shadings = [];
    private readonly List<TextGlyph> _text = [];

    /// <summary>Number of entries added so far.</summary>
    public int Count => _count;

    /// <summary>Maximum number of characters for search and highlighting per page (a dense text page has about 10,000; about 140 bytes per character).</summary>
    public const int MaxTextGlyphs = 500_000;

    /// <summary>Adds a character for search and selection; characters beyond <see cref="MaxTextGlyphs"/> are dropped.</summary>
    /// <param name="glyph">The character.</param>
    public void AddText(TextGlyph glyph)
    {
        if (_text.Count < MaxTextGlyphs) _text.Add(glyph);
    }

    /// <summary>Counts a character that is drawn but has no text that could be read (see <see cref="DisplayList.UnreadableGlyphs"/>).</summary>
    public void AddUnreadable() => _unreadable++;

    private int _unreadable;

    /// <summary>Characters whose read text is suspicious (see <see cref="DisplayList.MisreadGlyphs"/>), set when the page is complete.</summary>
    public int MisreadGlyphs { get; set; }

    /// <summary>
    /// Reserves space for this many entries and points up front (an estimate suffices). Capped, because the estimate comes from the
    /// length of the content: 1 GB of whitespace would otherwise reserve about 1.6 GB. Beyond the cap the lists grow as needed (an A0
    /// plan stays far below it).
    /// </summary>
    /// <param name="items">Expected number of entries (clamped to 1,024–4,000,000).</param>
    /// <param name="points">Expected number of points (clamped to at most 16,000,000).</param>
    public void Reserve(int items, int points)
    {
        items = Math.Clamp(items, 1024, 4_000_000);
        points = Math.Clamp(points, 0, 16_000_000);
        if (items > _items.Length) Array.Resize(ref _items, items);
        _points.EnsureCapacity(points * 2);
        _verbs.EnsureCapacity(points);
        _paths.EnsureCapacity(Math.Max(items, 64));
    }

    /// <summary>True once <see cref="MaxItems"/> is reached; further entries are dropped.</summary>
    public bool Full => _count >= MaxItems;

    // ------------------------------------------------------------ Characters of the entries (visibility of text)

    private int[]? _tags;
    private readonly List<int> _silent = [];
    private readonly List<int> _clipping = [];
    private int _glyphCount;

    /// <summary>
    /// Records for every entry which character of the page content drew it (<see cref="DisplayList.GlyphTags"/>); switch on before the
    /// first entry. Used to measure whether text can be seen (<see cref="Editing.TextVisibility"/>).
    /// </summary>
    public void TagGlyphs() => _tags ??= new int[_items.Length];

    /// <summary>Whether entries are tagged with their character (<see cref="TagGlyphs"/>).</summary>
    public bool Tagging => _tags is not null;

    /// <summary>Number of the character whose entries are being added; -1 = none (paths, images, annotations).</summary>
    public int GlyphTag { get; set; } = -1;

    /// <summary>Notes a character of the page content (its number counts from 0 in content order).</summary>
    /// <param name="serial">Its number.</param>
    /// <param name="silent">It should have painted something but added no entry (colour space /None, clipped away completely).</param>
    /// <param name="clips">It adds to the clip (text rendering modes 4–7): what it shows is the content painted through it.</param>
    public void NoteGlyph(int serial, bool silent, bool clips = false)
    {
        _glyphCount = Math.Max(_glyphCount, serial + 1);
        if (silent && _silent.Count < MaxItems) _silent.Add(serial);
        if (clips && _clipping.Count < MaxItems) _clipping.Add(serial);
    }

    // shared paths (in forms and Type 3 glyphs): hash over verbs and points → path numbers
    private readonly Dictionary<int, List<int>> _sharedPaths = [];

    /// <summary>
    /// Adds a path that is likely to repeat (symbols in forms, Type 3 glyphs): an identical path
    /// that already exists is reused instead of being stored again.
    /// </summary>
    /// <param name="verbs">The verbs.</param>
    /// <param name="points">The points (x, y alternating) in path space.</param>
    /// <returns>The path index.</returns>
    public int AddSharedPath(ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<float> points)
    {
        var h = new HashCode();
        h.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(verbs));
        h.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(points));
        var hash = h.ToHashCode();
        if (_sharedPaths.TryGetValue(hash, out var candidates))
        {
            foreach (var index in candidates)
            {
                var (verbStart, verbCount, pointStart) = _paths[index];
                if (verbCount == verbs.Length
                    && _verbs.Span.Slice(verbStart, verbCount).SequenceEqual(verbs)
                    && pointStart + points.Length <= _points.Count
                    && _points.Span.Slice(pointStart, points.Length).SequenceEqual(points))
                    return index;
            }
        }
        else
        {
            candidates = [];
            _sharedPaths[hash] = candidates;
        }
        var added = AddPath(verbs, points);
        candidates.Add(added);
        return added;
    }

    /// <summary>Adds a path without looking for an identical one.</summary>
    /// <param name="verbs">The verbs.</param>
    /// <param name="points">The points (x, y alternating) in path space.</param>
    /// <returns>The path index.</returns>
    public int AddPath(ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<float> points)
    {
        var verbStart = _verbs.Count;
        var pointStart = _points.Count;
        _verbs.AddRange(verbs);
        _points.AddRange(points);
        _paths.Add((verbStart, verbs.Length, pointStart));
        return _paths.Count - 1;
    }

    /// <summary>At most this many matrices are interned; beyond that the dictionary no longer pays off.</summary>
    private const int MaxInternedMatrices = 16_384;

    private int _lastMatrix = -1;

    /// <summary>Adds a matrix, reusing an identical one where possible.</summary>
    /// <param name="m">The matrix.</param>
    /// <returns>The matrix index.</returns>
    public int AddMatrix(Matrix m)
    {
        // usually the same as the previous one (runs of strokes), so no lookup is needed
        if (_lastMatrix >= 0 && _matrices[_lastMatrix] == m) return _lastMatrix;
        if (_matrixIndex.TryGetValue(m, out var i)) return _lastMatrix = i;
        _matrices.Add(m);
        _lastMatrix = _matrices.Count - 1;
        if (_matrixIndex.Count < MaxInternedMatrices) _matrixIndex[m] = _lastMatrix;
        return _lastMatrix;
    }

    /// <summary>Adds a matrix that does not repeat (position of a single glyph), bypassing the dictionary.</summary>
    /// <param name="m">The matrix.</param>
    /// <returns>The matrix index.</returns>
    public int AddUniqueMatrix(Matrix m)
    {
        _matrices.Add(m);
        return _matrices.Count - 1;
    }

    /// <summary>Adds a style, reusing an identical one.</summary>
    /// <param name="p">The style.</param>
    /// <returns>The style index.</returns>
    public int AddPaint(Paint p)
    {
        if (_paintIndex.TryGetValue(p, out var i)) return i;
        _paints.Add(p);
        return _paintIndex[p] = _paints.Count - 1;
    }

    /// <summary>Returns a stored style.</summary>
    /// <param name="index">The style index.</param>
    /// <returns>The style.</returns>
    public Paint PaintAt(int index) => _paints[index];

    /// <summary>Stores a shading or tiling pattern.</summary>
    /// <param name="shading">The fill.</param>
    /// <returns>The index for <see cref="Paint.FillShading"/>.</returns>
    public int AddShading(ShadingFill shading)
    {
        if (shading is MeshFill mesh && _meshesSeen.Add(mesh.Colors)) ShadingWork += mesh.Colors.Length / 3;   // construction counted once
        _shadings.Add(shading);
        return _shadings.Count - 1;
    }

    /// <summary>A stored shading or tiling pattern.</summary>
    /// <param name="index">Index from <see cref="AddShading"/>.</param>
    /// <returns>The fill.</returns>
    public ShadingFill ShadingAt(int index) => _shadings[index];

    private readonly HashSet<uint[]> _meshesSeen = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Mesh area on the page (pt²) that counts as one operation: the 16 million operations of a page correspond to 32 sheets of A0 for all
    /// drawings of meshes together — half of what one mesh may cover (<see cref="Shadings.MaxMeshArea"/>), so a mesh at that limit ends the
    /// page. Real pages stay far below (an A4 page could be covered 500 times).
    /// </summary>
    private const double MeshAreaPerOperation = Shadings.MaxMeshArea / 32_000_000;

    /// <summary>
    /// Work of drawing a mesh once into <paramref name="bounds"/> (page space): per triangle about as much as four operators (measured: about
    /// 170 ns per triangle), and the area covered — the mesh's own area, at most each triangle over the whole entry. Before, only the
    /// triangles counted: ten crafted meshes of 360 000 small triangles over one page (90 KB) took 31 s to draw at 72 dpi
    /// (docs/FEHLERGEDAECHTNIS.md, No. 762).
    /// </summary>
    private long MeshWork(int shading, RectD bounds)
    {
        if (shading < 0 || _shadings[shading] is not MeshFill mesh) return 0;
        var triangles = mesh.Colors.Length / 3;
        var area = Math.Min(mesh.Area * Math.Abs(mesh.ToPage.Determinant), (double)triangles * (bounds.Right - bounds.Left) * (bounds.Bottom - bounds.Top));
        return 4L * triangles + (area > 0 ? (long)Math.Min(area / MeshAreaPerOperation, 1e15) : 0);
    }

    /// <summary>
    /// Work of the meshes in triangles: their construction and every drawing (each entry with a mesh draws all of it, clipped
    /// to its path). Counts towards the work limit of the page; otherwise 10,000 × "sh" with a mesh of 400,000 triangles would cost
    /// only 10,000 operators but 4·10⁹ triangles per tile.
    /// </summary>
    public long ShadingWork { get; private set; }

    /// <summary>Adds a dash pattern, reusing an identical one.</summary>
    /// <param name="d">The pattern.</param>
    /// <returns>The index for <see cref="Paint.Dash"/>.</returns>
    public int AddDash(DashPattern d)
    {
        if (_dashIndex.TryGetValue(d, out var i)) return i;
        _dashes.Add(d);
        return _dashIndex[d] = _dashes.Count - 1;
    }

    // Share identical clips: office producers put "q … re W n … Q" with the same rectangle around
    // every object. Without sharing, every entry would have its own clip, and the renderer would have to
    // rebuild the clip chain for every entry (and could not batch anything)
    private readonly Dictionary<(int Parent, int Path, int Matrix, bool EvenOdd, bool FormBox), int> _clipIndex = [];

    /// <summary>Adds a clip link, reusing an identical one with the same parent.</summary>
    /// <param name="parent">The current clip (0 = none).</param>
    /// <param name="path">The clip path; for sharing to work it should have been stored with <see cref="AddSharedPath"/>.</param>
    /// <param name="matrix">Matrix index (path space → page).</param>
    /// <param name="evenOdd">Even-odd rule instead of non-zero winding.</param>
    /// <param name="bounds">Bounds of the path in page space; intersected with those of the parent.</param>
    /// <param name="formBox">The clip is the box (/BBox) of a form (not shared with the same path clipped by the content).</param>
    /// <returns>The index of the new clip chain.</returns>
    public int AddClip(int parent, int path, int matrix, bool evenOdd, RectD bounds, bool formBox = false)
    {
        var key = (parent, path, matrix, evenOdd, formBox);
        if (_clipIndex.TryGetValue(key, out var existing)) return existing;
        _clips.Add(new ClipNode(parent, path, matrix, evenOdd, bounds.Intersect(_clips[parent].Bounds), IsAxisRectangle(path, _matrices[matrix])));
        return _clipIndex[key] = _clips.Count - 1;
    }

    /// <summary>Whether the path is an axis-aligned rectangle (also after applying the matrix).</summary>
    private bool IsAxisRectangle(int path, Matrix m)
    {
        // a rectangle stays axis-aligned only if it is not rotated by angles other than 0/90/180/270 degrees
        var straight = Math.Abs(m.B) < 1e-9 && Math.Abs(m.C) < 1e-9 || Math.Abs(m.A) < 1e-9 && Math.Abs(m.D) < 1e-9;
        if (!straight) return false;
        var (verbStart, verbCount, pointStart) = _paths[path];
        // m l l l [l] h — four or five points
        if (verbCount is < 4 or > 6) return false;
        var points = 0;
        for (var v = verbStart; v < verbStart + verbCount; v++)
        {
            var verb = _verbs[v];
            if (v == verbStart ? verb != PathVerb.Move : verb is PathVerb.Move or PathVerb.Cubic) return false;
            if (verb != PathVerb.Close) points++;
        }
        if (points is not (4 or 5)) return false;
        // a fifth point closes the rectangle only where it is the first again (before, it was compared with the second: a diagonal back to a
        // corner went unnoticed); the four edges alternate horizontal and vertical, each with a length — back and forth along one edge has no
        // area and is no rectangle (8.5.4; docs/FEHLERGEDAECHTNIS.md, No. 1123)
        (float X, float Y) Point(int i) => (_points[pointStart + 2 * i], _points[pointStart + 2 * i + 1]);
        if (points == 5 && Point(4) != Point(0)) return false;
        bool? previous = null;
        for (var i = 0; i < 4; i++)
        {
            var (a, b) = (Point(i), Point((i + 1) % 4));
            var horizontal = a.Y == b.Y && a.X != b.X;
            if (!horizontal && !(a.X == b.X && a.Y != b.Y)) return false;
            if (previous == horizontal) return false;
            previous = horizontal;
        }
        return true;
    }

    /// <summary>Returns the bounds of a clip chain in page space.</summary>
    /// <param name="clip">The clip index (0 = none, unbounded).</param>
    /// <returns>The bounds.</returns>
    public RectD ClipBounds(int clip) => _clips[clip].Bounds;

    /// <summary>
    /// Whether a point lies inside a clip chain: inside the path of every link, by its rule (curves in 16 steps, every subpath closed). A
    /// link whose matrix flattens it clips everything away.
    /// </summary>
    /// <param name="clip">The clip index (0 = none, unbounded).</param>
    /// <param name="x">x in page space.</param>
    /// <param name="y">y in page space.</param>
    /// <returns>Whether what is painted there shows.</returns>
    public bool ClipContains(int clip, double x, double y)
    {
        for (var at = clip; at > 0; at = _clips[at].Parent)
        {
            var node = _clips[at];
            if (x < node.Bounds.Left || x > node.Bounds.Right || y < node.Bounds.Top || y > node.Bounds.Bottom) return false;
            if (node.Path < 0) continue;
            var m = _matrices[node.Matrix];
            if (!m.IsInvertible) return false;
            // the point into path space: an invertible affine map keeps the winding number up to its sign
            var (px, py) = m.Invert().Transform(x, y);
            var (verbStart, verbCount, pointStart) = _paths[node.Path];
            if (!InsidePath(_verbs.Span.Slice(verbStart, verbCount), _points.Span[pointStart..], px, py, node.EvenOdd)) return false;
        }
        return true;
    }

    private static bool InsidePath(ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<float> points, double x, double y, bool evenOdd)
    {
        int winding = 0, crossings = 0, at = 0;
        double sx = 0, sy = 0, cx = 0, cy = 0;
        void Edge(double ax, double ay, double bx, double by)
        {
            var side = (bx - ax) * (y - ay) - (x - ax) * (by - ay);
            if (ay <= y)
            {
                if (by > y && side > 0)
                {
                    winding++;
                    crossings++;
                }
            }
            else if (by <= y && side < 0)
            {
                winding--;
                crossings++;
            }
        }
        foreach (var verb in verbs)
        {
            switch (verb)
            {
                case PathVerb.Move when at + 1 < points.Length:
                    Edge(cx, cy, sx, sy);   // every subpath counts as closed
                    (sx, sy) = (points[at], points[at + 1]);
                    (cx, cy) = (sx, sy);
                    at += 2;
                    break;
                case PathVerb.Line when at + 1 < points.Length:
                    Edge(cx, cy, points[at], points[at + 1]);
                    (cx, cy) = (points[at], points[at + 1]);
                    at += 2;
                    break;
                case PathVerb.Cubic when at + 5 < points.Length:
                    double x1 = points[at], y1 = points[at + 1], x2 = points[at + 2], y2 = points[at + 3], x3 = points[at + 4], y3 = points[at + 5];
                    at += 6;
                    double lx = cx, ly = cy;
                    for (var k = 1; k <= 16; k++)
                    {
                        var t = k / 16.0;
                        var u = 1 - t;
                        var nx = u * u * u * cx + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3;
                        var ny = u * u * u * cy + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3;
                        Edge(lx, ly, nx, ny);
                        (lx, ly) = (nx, ny);
                    }
                    (cx, cy) = (x3, y3);
                    break;
                case PathVerb.Close:
                    Edge(cx, cy, sx, sy);
                    (cx, cy) = (sx, sy);
                    break;
            }
        }
        Edge(cx, cy, sx, sy);
        return evenOdd ? crossings % 2 == 1 : winding != 0;
    }

    /// <summary>Number of clip links so far (a new one gets the next index).</summary>
    public int ClipCount => _clips.Count;

    /// <summary>Stores an image.</summary>
    /// <param name="image">The image.</param>
    /// <returns>The index for <see cref="DisplayItem.Image"/>.</returns>
    public int AddImage(ImageSource image)
    {
        _images.Add(image);
        return _images.Count - 1;
    }

    /// <summary>Appends an entry; it is dropped if it lies entirely outside its clip or <see cref="Full"/> is reached.</summary>
    /// <param name="kind">What the entry draws.</param>
    /// <param name="evenOdd">Even-odd rule instead of non-zero winding.</param>
    /// <param name="path">Path index; -1 for images.</param>
    /// <param name="matrix">Matrix index.</param>
    /// <param name="paint">Style index.</param>
    /// <param name="clip">Clip index (0 = none).</param>
    /// <param name="image">Image index; -1 for paths.</param>
    /// <param name="bounds">Bounds in page space; stored intersected with the clip.</param>
    /// <param name="glyph">Filled glyph that the renderer may draw as a prepared mask.</param>
    public void Add(DisplayItemKind kind, bool evenOdd, int path, int matrix, int paint, int clip, int image, RectD bounds, bool glyph = false)
    {
        if (_count >= MaxItems) return;
        var b = bounds.Intersect(_clips[clip].Bounds);
        // width or height 0 is allowed (horizontal/vertical hairline); only "inverted" means nothing is visible
        if (!(b.Right >= b.Left && b.Bottom >= b.Top)) return;
        if (kind != DisplayItemKind.Stroke) ShadingWork += MeshWork(_paints[paint].FillShading, b);
        if (kind is DisplayItemKind.Stroke or DisplayItemKind.FillStroke) ShadingWork += MeshWork(_paints[paint].StrokeShading, b);
        if (_count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        if (_tags is not null)
        {
            if (_tags.Length < _items.Length) Array.Resize(ref _tags, _items.Length);
            _tags[_count] = GlyphTag;
        }
        _items[_count++] = new DisplayItem
        {
            Kind = kind, EvenOdd = evenOdd, Glyph = glyph, Path = path, Matrix = matrix, Paint = paint, Clip = clip, Image = image,
            Left = (float)b.Left, Top = (float)b.Top, Right = (float)b.Right, Bottom = (float)b.Bottom,
        };
    }

    // ------------------------------------------------------------ Groups and soft masks

    private readonly List<GroupNode> _groups = [];
    private readonly List<SoftMask> _masks = [];

    /// <summary>Stores a soft mask.</summary>
    /// <param name="mask">The mask.</param>
    /// <returns>The index for <see cref="BeginGroup"/>.</returns>
    public int AddMask(SoftMask mask)
    {
        _masks.Add(mask);
        return _masks.Count - 1;
    }

    /// <summary>Begins a group at the next entry; it must be closed with <see cref="EndGroup"/>.</summary>
    /// <param name="parent">The enclosing group (−1 = none).</param>
    /// <param name="alpha">Opacity 0–1 (NaN counts as 1).</param>
    /// <param name="blendMode">Blend mode number.</param>
    /// <param name="isolated">Isolated group.</param>
    /// <param name="knockout">Knockout group.</param>
    /// <param name="mask">Soft mask index (−1 = none).</param>
    /// <returns>The group index.</returns>
    public int BeginGroup(int parent, double alpha, byte blendMode, bool isolated, bool knockout, int mask)
    {
        var a = (byte)Math.Round(Math.Clamp(double.IsNaN(alpha) ? 1 : alpha, 0, 1) * 255);
        _groups.Add(new GroupNode(_count, _count, parent, a, blendMode, isolated, knockout, mask, false));
        return _groups.Count - 1;
    }

    /// <summary>Returns a group as stored so far.</summary>
    /// <param name="index">The group index.</param>
    /// <returns>The group.</returns>
    public GroupNode Group(int index) => _groups[index];

    /// <summary>
    /// Ends a group before the next entry. It needs a layer of its own only if it looks different without one: opacity,
    /// blend mode, knockout, mask, element of a knockout group — or isolated with blend modes inside (they then do not blend
    /// with what lies beneath the group).
    /// </summary>
    /// <param name="index">The group index returned by <see cref="BeginGroup"/>.</param>
    public void EndGroup(int index)
    {
        var g = _groups[index];
        // Knockout only takes effect with two or more visible elements; with one the group looks the same as without, and a layer of
        // its own (empty) would take the backdrop away from non-isolated subgroups (/Color over white text would tint it, issue18032)
        var needs = g.Alpha < 255 || g.BlendMode != 0 || g.Knockout && VisibleElements(index, g.Start, _count) >= 2 || g.Mask >= 0
            || (g.Parent >= 0 && _groups[g.Parent].Knockout) || (g.Isolated && HasBlendMode(index, g.Start, _count));
        _groups[index] = g with
        {
            End = _count, NeedsLayer = needs && _count > g.Start, Bounds = ItemBounds(g.Start, _count),
            // Only with Normal for the group itself: then starting with the backdrop is exact (source-over is associative); with
            // another group blend mode the backdrop would be blended twice, it stays as before (docs/FEHLERGEDAECHTNIS.md, No. 567)
            InnerBlend = !g.Isolated && g.BlendMode == 0 && HasBlendMode(index, g.Start, _count),
        };
    }

    /// <summary>
    /// Visible elements of a group: its own entries (not in subgroups) and subgroups with opacity and content; counting stops
    /// at two.
    /// </summary>
    private int VisibleElements(int index, int start, int end)
    {
        var count = 0;
        var covered = start;   // entries before this belong to a subgroup already counted
        for (var k = index + 1; k < _groups.Count && count < 2; k++)
        {
            var child = _groups[k];
            if (child.Parent != index) continue;
            count += Math.Max(0, child.Start - covered);
            if (child.Alpha > 0 && child.End > child.Start) count++;
            covered = Math.Max(covered, child.End);
        }
        return count + Math.Max(0, end - covered);
    }

    /// <summary>Union of the bounds (including hairlines of width 0, which is why <see cref="RectD.Union"/> is not used).</summary>
    private RectD ItemBounds(int start, int end)
    {
        if (end <= start) return RectD.Empty;
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        for (var i = start; i < end; i++)
        {
            ref readonly var item = ref _items[i];
            l = Math.Min(l, item.Left);
            t = Math.Min(t, item.Top);
            r = Math.Max(r, item.Right);
            b = Math.Max(b, item.Bottom);
        }
        return RectD.FromLtrb(l, t, r, b);
    }

    private bool HasBlendMode(int group, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (_paints[_items[i].Paint].BlendMode != 0) return true;
        for (var i = group + 1; i < _groups.Count; i++)
            if (_groups[i].BlendMode != 0 && _groups[i].Start >= start && _groups[i].Start < end) return true;
        return false;
    }

    private int _firstVisible;

    /// <summary>
    /// Everything added so far lies beneath the next entry — opaque over the whole visible page, as the interpreter decided: it stays
    /// in the list for whatever reads the content, but is no longer drawn (<see cref="DisplayList.FirstVisible"/>).
    /// </summary>
    public void HideEarlier()
    {
        if (_count < MaxItems) _firstVisible = _count;   // at the limit the next entry would not be added
    }

    private readonly List<(int Start, int End)> _hidden = [];

    /// <summary>
    /// Everything added since the start of a transparency group lies beneath the next entry — opaque over the whole visible page within
    /// the group, as the interpreter decided: whatever the group's blending, opacity or mask, its result there is that entry alone
    /// (ISO 32000-2:2020, 11.4), so the earlier entries of the group are no longer drawn (<see cref="DisplayList.IsHidden"/>).
    /// </summary>
    /// <param name="group">The innermost open group.</param>
    public void HideEarlierInGroup(int group)
    {
        if (_count >= MaxItems) return;
        var start = Math.Max(_groups[group].Start, _firstVisible);
        if (start >= _count) return;
        // ranges stay ascending and apart: a range of an enclosing group or an earlier hiding in this one is merged
        while (_hidden.Count > 0 && _hidden[^1].End >= start)
        {
            start = Math.Min(start, _hidden[^1].Start);
            _hidden.RemoveAt(_hidden.Count - 1);
        }
        _hidden.Add((start, _count));
    }

    /// <summary>
    /// The previous entry lies beneath the next one, an opaque image over <paramref name="covered"/> (page space): it is no longer drawn
    /// (<see cref="DisplayList.IsHidden"/>) if it is the same picture at the same place, or lies inside by at least a point — its edge then
    /// never mixes into the edge of the image, so the page looks the same. Content that draws the same picture at the same place again and
    /// again — a crafted file: 13,000 times a page-sized JPEG — then costs only the last time, also when the picture is a little smaller than
    /// the page (docs/FEHLERGEDAECHTNIS.md, No. 276). Only the entry right before, and not across the start of a group.
    /// </summary>
    /// <param name="covered">Where the next entry paints opaquely (within a clip of rectangles only).</param>
    /// <param name="image">Image index of the next entry (<see cref="AddImage"/>).</param>
    /// <param name="matrix">Its matrix.</param>
    /// <param name="clip">Its clip.</param>
    public void HidePreviousUnder(RectD covered, int image, Matrix matrix, int clip)
    {
        var previous = _count - 1;
        if (previous < _firstVisible || _count >= MaxItems) return;
        if (_groups.Count > 0 && _groups[^1].Start >= previous) return;   // a group begins with it or after it
        ref readonly var item = ref _items[previous];
        var same = item.Kind == DisplayItemKind.Image && item.Clip == clip && _matrices[item.Matrix] == matrix &&
                   _images[item.Image].Stream is { } stream && ReferenceEquals(stream, _images[image].Stream);
        const double margin = 1;
        if (!same && (item.Left < covered.Left + margin || item.Top < covered.Top + margin || item.Right > covered.Right - margin ||
                      item.Bottom > covered.Bottom - margin)) return;
        var start = previous;
        while (_hidden.Count > 0 && _hidden[^1].End >= start)
        {
            start = Math.Min(start, _hidden[^1].Start);
            _hidden.RemoveAt(_hidden.Count - 1);
        }
        _hidden.Add((start, _count));
    }

    /// <summary>Whether a clip consists of axis-aligned rectangles only (its bounds are then exactly the clipped area).</summary>
    /// <param name="clip">Clip index.</param>
    /// <returns>true for no clip or rectangles only.</returns>
    public bool ClipIsRectangular(int clip)
    {
        for (var c = clip; c > 0; c = _clips[c].Parent)
            if (!_clips[c].IsRectangle) return false;
        return true;
    }

    /// <summary>Creates the display list; the builder must not be used afterwards, because the list takes over its arrays.</summary>
    /// <param name="pageBox">Visible area of the page (CropBox) in page space.</param>
    /// <returns>The display list.</returns>
    private readonly List<NoZoomRange> _noZoom = [];
    private (int Start, double X, double Y)? _noZoomOpen;

    /// <summary>The following entries belong to an annotation with NoZoom, anchored at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void BeginNoZoom(double x, double y) => _noZoomOpen = (_count, x, y);

    /// <summary>Ends the entries begun with <see cref="BeginNoZoom"/>.</summary>
    public void EndNoZoom()
    {
        if (_noZoomOpen is { } open && _count > open.Start) _noZoom.Add(new NoZoomRange(open.Start, _count, open.X, open.Y));
        _noZoomOpen = null;
    }

    public DisplayList Build(RectD pageBox) =>
        new(_items.Length - _count > _items.Length / 8 ? _items[.._count] : _items, _count, _verbs.Finish(), _points.Finish(), _paths.Finish(),
            [.. _matrices], [.. _paints], [.. _dashes], [.. _clips], [.. _images], pageBox, [.. _text], [.. _shadings], [.. _groups], [.. _masks],
            _firstVisible, [.. _hidden])
        {
            UnreadableGlyphs = _unreadable,
            MisreadGlyphs = MisreadGlyphs,
            GlyphTags = _tags is null ? null : _tags.AsSpan(0, _count).ToArray(),
            GlyphCount = _glyphCount,
            SilentGlyphs = [.. _silent],
            NoZoom = [.. _noZoom],
            ClippingGlyphs = [.. _clipping],
            Truncated = Full,
        };
}

/// <summary>
/// Growing array for very many values: grows by half (not by doubling) and hands over its array
/// at the end without a copy — trimmed only if more than an eighth would remain empty.
/// Use it as a field of a class (it is a struct; do not copy it).
/// </summary>
internal struct ArrayBuilder<T>(int capacity)
{
    private T[] _items = new T[capacity];

    public int Count { get; private set; }

    public readonly T this[int index] => _items[index];

    public readonly Span<T> Span => _items.AsSpan(0, Count);

    public void Add(T item)
    {
        if (Count == _items.Length) Grow(Count + 1);
        _items[Count++] = item;
    }

    public void AddRange(ReadOnlySpan<T> items)
    {
        if (Count + items.Length > _items.Length) Grow(Count + items.Length);
        items.CopyTo(_items.AsSpan(Count));
        Count += items.Length;
    }

    public void EnsureCapacity(int capacity)
    {
        if (capacity > _items.Length) Array.Resize(ref _items, capacity);
    }

    private void Grow(int minimum) =>
        Array.Resize(ref _items, (int)Math.Clamp(Math.Max(minimum, (long)_items.Length * 3 / 2 + 16), minimum, Array.MaxLength));

    /// <summary>Array for the finished list (may be larger than <see cref="Count"/>).</summary>
    public readonly T[] Finish() => _items.Length - Count > _items.Length / 8 ? _items.AsSpan(0, Count).ToArray() : _items;
}
