using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>Path construction verb; each uses its own number of points from <see cref="DisplayList.Points"/>.</summary>
public enum PathVerb : byte
{
    /// <summary>Starts a subpath (one point).</summary>
    Move,

    /// <summary>Straight line to a point (one point).</summary>
    Line,

    /// <summary>Cubic Bézier curve (two control points and the end point).</summary>
    Cubic,

    /// <summary>Closes the subpath (no point).</summary>
    Close,
}

/// <summary>What a <see cref="DisplayItem"/> draws.</summary>
public enum DisplayItemKind : byte
{
    /// <summary>Fills the path.</summary>
    Fill,

    /// <summary>Strokes the path.</summary>
    Stroke,

    /// <summary>Fills and then strokes the path.</summary>
    FillStroke,

    /// <summary>Image in the unit square, mapped with the matrix of the entry.</summary>
    Image,
}

/// <summary>
/// A drawing command. Deliberately small and free of references: hundreds of thousands of them lie in
/// one array (structure of arrays across <see cref="DisplayList"/>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DisplayItem
{
    /// <summary>What the entry draws.</summary>
    public DisplayItemKind Kind;

    /// <summary>Even-odd fill rule instead of non-zero winding.</summary>
    public bool EvenOdd;

    /// <summary>
    /// Glyph of text that is filled (text rendering mode 0/4: the renderer may draw it as a prepared mask) or filled and stroked (2/6: drawn as
    /// a path, the outline in its real width, docs/FEHLERGEDAECHTNIS.md, No. 816).
    /// </summary>
    public bool Glyph;

    /// <summary>Index into <see cref="DisplayList.Paths"/>; -1 for images.</summary>
    public int Path;

    /// <summary>Index into <see cref="DisplayList.Matrices"/>: from the space of the path (or of the image unit square) to page space.</summary>
    public int Matrix;

    /// <summary>Index into <see cref="DisplayList.Paints"/>.</summary>
    public int Paint;

    /// <summary>Index of the clip chain in <see cref="DisplayList.Clips"/>; 0 = none.</summary>
    public int Clip;

    /// <summary>Index into <see cref="DisplayList.Images"/> for image entries; -1 otherwise.</summary>
    public int Image;

    /// <summary>Bounds in page space (PDF default coordinates of the page, y upwards; <see cref="Top"/> is the smaller y value).</summary>
    public float Left, Top, Right, Bottom;
}

/// <summary>
/// Stroke and fill colour and line style, shared by many entries. <see cref="FillShading"/> and
/// <see cref="StrokeShading"/>: shading or tiling pattern instead of a colour (index into <see cref="DisplayList.Shadings"/>); the colour
/// remains as a fallback. <see cref="Overprint"/>: overprint takes effect — bit value 1 fill, 2 stroke; shown only in the overprint preview.
/// </summary>
/// <param name="Fill">Fill colour as ARGB.</param>
/// <param name="Stroke">Stroke colour as ARGB.</param>
/// <param name="LineWidth">Line width in the space of the entry's matrix.</param>
/// <param name="Cap">Line cap: 0 butt, 1 round, 2 square.</param>
/// <param name="Join">Line join: 0 mitre, 1 round, 2 bevel.</param>
/// <param name="MiterLimit">Mitre limit (at least 1).</param>
/// <param name="Dash">Index into <see cref="DisplayList.Dashes"/>; -1 = solid.</param>
/// <param name="BlendMode">Blend mode number (<see cref="Content.BlendModes"/>).</param>
/// <param name="FillShading">Index into <see cref="DisplayList.Shadings"/> for the fill; -1 = colour.</param>
/// <param name="StrokeShading">Index into <see cref="DisplayList.Shadings"/> for the stroke; -1 = colour.</param>
/// <param name="Overprint">Overprint bits: 1 fill, 2 stroke.</param>
/// <param name="OverprintReplace">
/// Overprint mode 1 with a DeviceCMYK colour (ISO 32000-2, 8.6.7): the plates whose component is not 0 and so replaces what lies beneath — bits 1
/// cyan, 2 magenta, 4 yellow for the fill, the same shifted by 4 for the stroke; the overprint preview replaces the matching RGB channel (red,
/// green, blue) by that of the colour and multiplies the others. 0: the colour multiplies all channels (Separation, DeviceN).
/// </param>
public readonly record struct Paint(
    uint Fill,
    uint Stroke,
    float LineWidth,
    byte Cap,
    byte Join,
    float MiterLimit,
    int Dash,
    byte BlendMode,
    int FillShading = -1,
    int StrokeShading = -1,
    byte Overprint = 0,
    byte OverprintReplace = 0);

/// <summary>Dash pattern: lengths in the space of the path and the start offset; compared by value.</summary>
/// <param name="Lengths">Alternating dash and gap lengths (the interpreter doubles odd-length arrays, so the count is even).</param>
/// <param name="Phase">Offset into the pattern at the start of the path.</param>
public sealed record DashPattern(float[] Lengths, float Phase)
{
    /// <summary>Compares phase and lengths by value.</summary>
    /// <param name="other">The other pattern.</param>
    /// <returns>True if both describe the same pattern.</returns>
    public bool Equals(DashPattern? other) => other is not null && Phase == other.Phase && Lengths.AsSpan().SequenceEqual(other.Lengths);

    /// <summary>Hash over phase and lengths.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Phase);
        foreach (var l in Lengths) h.Add(l);
        return h.ToHashCode();
    }
}

/// <summary>
/// Transparency group (11.4) or run of entries under a soft mask: the entries <see cref="Start"/> to
/// <see cref="End"/> − 1 are drawn into a layer of their own and composited as a whole, with opacity, blend mode and
/// mask. Groups are contiguous ranges of the flat list and properly nested (ascending by start, parents before
/// children), so clips, styles and the spatial index stay unchanged.
/// </summary>
/// <param name="Start">Index of the first entry.</param>
/// <param name="End">Index after the last entry.</param>
/// <param name="Parent">Index of the enclosing group in <see cref="DisplayList.Groups"/>; −1 = none.</param>
/// <param name="Alpha">Opacity of the group, 0–255.</param>
/// <param name="BlendMode">Blend mode number (<see cref="Content.BlendModes"/>).</param>
/// <param name="Isolated">Isolated group: drawn on a transparent backdrop instead of what lies beneath.</param>
/// <param name="Knockout">Knockout group: each element replaces what other elements of the group have painted beneath it.</param>
/// <param name="Mask">Soft mask (index into <see cref="DisplayList.Masks"/>), −1 = none.</param>
/// <param name="NeedsLayer">Needs a layer of its own; otherwise it looks the same as without one (isolated without blend modes, empty).</param>
/// <param name="Bounds">Union of the bounds of its entries in page space; its layer is made this large.</param>
/// <param name="InnerBlend">A non-isolated group composited with Normal and with a blend mode inside: its layer starts with what lies
/// beneath it, the blending inside mixes with that backdrop (ISO 32000-2, 11.4.4, 11.4.5, 11.4.8; docs/FEHLERGEDAECHTNIS.md, No. 567).</param>
public readonly record struct GroupNode(int Start, int End, int Parent, byte Alpha, byte BlendMode, bool Isolated, bool Knockout, int Mask,
    bool NeedsLayer, RectD Bounds = default, bool InnerBlend = false);

/// <summary>
/// Soft mask (11.6.5.2): its content (group /G) as a display list of its own in mask space. Luminosity: the luminosity over the
/// backdrop <see cref="Backdrop"/> (/BC) becomes the opacity; Alpha: the opacity of the content. <see cref="Transfer"/>: /TR
/// as a table of 256 levels, null = unchanged.
/// </summary>
/// <param name="Content">The content of the mask group.</param>
/// <param name="Luminosity">True for a luminosity mask, false for an alpha mask.</param>
/// <param name="Backdrop">Backdrop colour as ARGB (luminosity masks only).</param>
/// <param name="Transfer">Transfer function as a 256-entry table; null = identity.</param>
public sealed record SoftMask(DisplayList Content, bool Luminosity, uint Backdrop, byte[]? Transfer)
{
    /// <summary>Mask space (the CTM when the mask was set) → page; the content is the same for all placements.</summary>
    public Matrix ToPage { get; init; } = Matrix.Identity;

    /// <summary>
    /// /BBox of the mask group in mask space (null: unknown). Outside it an alpha mask is 0, without applying /TR, which only acts
    /// inside (smask_alpha_oob_transfer: a /TR with TR(0) = 0.5 would otherwise tint the whole page half-transparent).
    /// </summary>
    public RectD? Bounds { get; init; }

    /// <summary>Rough size in memory in bytes, for cache budgets.</summary>
    public long EstimatedBytes => 64 + Content.EstimatedBytes + (Transfer?.Length ?? 0);
}

/// <summary>One link of a clip chain: a path with a fill rule, intersected with the parent link.</summary>
/// <param name="Parent">Index of the parent link in <see cref="DisplayList.Clips"/>; 0 = the root "no clip", whose own parent is −1.</param>
/// <param name="Path">Index into <see cref="DisplayList.Paths"/>.</param>
/// <param name="Matrix">Index into <see cref="DisplayList.Matrices"/> (path space → page).</param>
/// <param name="EvenOdd">Even-odd rule instead of non-zero winding.</param>
/// <param name="Bounds">Bounds of the clip in page space, intersected with those of the parent.</param>
/// <param name="IsRectangle">Axis-aligned rectangle: <see cref="Bounds"/> is then exactly the clip, so the renderer
/// skips it if it contains the whole tile and otherwise clips with a rectangle (much cheaper than a path).</param>
public readonly record struct ClipNode(int Parent, int Path, int Matrix, bool EvenOdd, RectD Bounds, bool IsRectangle = false);

/// <summary>Image for an <see cref="DisplayItemKind.Image"/> entry; it is decoded only when drawn.</summary>
/// <param name="Stream">The image XObject; null for an inline image.</param>
/// <param name="Inline">The inline image; null for an image XObject.</param>
/// <param name="Resources">The resources at the point of use (named colour spaces of inline images).</param>
/// <param name="IsStencil">Stencil mask (/ImageMask true): painted in <paramref name="StencilColor"/>.</param>
/// <param name="StencilColor">Fill colour as ARGB for stencil masks.</param>
/// <param name="Intent">Rendering intent of the image (its /Intent, else that of the graphics state; ColorSpace.Perceptual …).</param>
/// <param name="Bpc">Black point compensation of the graphics state (ColorSpace.BpcDefault …; docs/FEHLERGEDAECHTNIS.md, No. 573).</param>
/// <param name="DeviceCmyk">DeviceCMYK of a page with its own output intent; null for the document's (No. 575).</param>
public sealed record ImageSource(PdfStream? Stream, InlineImage? Inline, PdfDictionary? Resources, bool IsStencil, uint StencilColor,
    byte Intent = ColorSpace.RelativeColorimetric, byte Bpc = ColorSpace.BpcDefault, ColorSpace? DeviceCmyk = null)
{
    /// <summary>The image dictionary of the stream or inline image.</summary>
    public PdfDictionary Dictionary => Stream?.Dictionary ?? Inline!.Dictionary;
}

/// <summary>
/// A character for search and text selection: text and quadrilateral in page space (bottom left,
/// bottom right, top right, top left), in content stream order.
/// </summary>
/// <param name="Text">The Unicode text of the glyph.</param>
/// <param name="X0">x of the bottom-left corner.</param>
/// <param name="Y0">y of the bottom-left corner.</param>
/// <param name="X1">x of the bottom-right corner.</param>
/// <param name="Y1">y of the bottom-right corner.</param>
/// <param name="X2">x of the top-right corner.</param>
/// <param name="Y2">y of the top-right corner.</param>
/// <param name="X3">x of the top-left corner.</param>
/// <param name="Y3">y of the top-left corner.</param>
/// <param name="Size">Font size in page space (for line and word spacing).</param>
/// <param name="Visible">False for invisible text (text rendering modes 3 and 7, for example an OCR layer).</param>
/// <param name="Vertical">Vertical writing: the line runs from top to bottom (edge top → bottom).</param>
public readonly record struct TextGlyph(string Text, float X0, float Y0, float X1, float Y1, float X2, float Y2, float X3, float Y3, float Size, bool Visible,
    bool Vertical = false);

/// <summary>
/// The entries of an annotation with the NoZoom flag (ISO 32000-2, 12.5.3, Table 167): on screen they keep the size they have at 100 %,
/// anchored at the top-left corner of /Rect (docs/FEHLERGEDAECHTNIS.md, No. 576).
/// </summary>
/// <param name="Start">First entry.</param>
/// <param name="End">Entry after the last.</param>
/// <param name="AnchorX">Left edge of /Rect in page space.</param>
/// <param name="AnchorY">Top edge of /Rect in page space.</param>
public readonly record struct NoZoomRange(int Start, int End, double AnchorX, double AnchorY)
{
    /// <summary>Page space → page space: scaled by <paramref name="scale"/> about the anchor.</summary>
    /// <param name="scale">1 / zoom.</param>
    public Matrix Scaled(double scale) => Matrix.Translate(-AnchorX, -AnchorY) * Matrix.Scale(scale, scale) * Matrix.Translate(AnchorX, AnchorY);
}

/// <summary>
/// Immutable display list of a page: what to draw in which order,
/// in page coordinates, without any dependency on Skia. Built once in the background and
/// then read by any number of threads at the same time (tiles, thumbnails).
/// </summary>
public sealed class DisplayList
{
    internal DisplayList(DisplayItem[] items, int itemCount, PathVerb[] verbs, float[] points, (int Verb, int VerbCount, int Point)[] paths,
        Matrix[] matrices, Paint[] paints, DashPattern[] dashes, ClipNode[] clips, ImageSource[] images, RectD pageBox, TextGlyph[] text,
        ShadingFill[]? shadings = null, GroupNode[]? groups = null, SoftMask[]? masks = null, int firstVisible = 0, (int Start, int End)[]? hidden = null)
    {
        Text = text;
        Shadings = shadings ?? [];
        Groups = groups ?? [];
        Masks = masks ?? [];
        Items = new ReadOnlyMemory<DisplayItem>(items, 0, itemCount);
        _itemCapacity = items.Length;
        Verbs = verbs;
        Points = points;
        Paths = paths;
        Matrices = matrices;
        Paints = paints;
        Dashes = dashes;
        Clips = clips;
        Images = images;
        PageBox = pageBox;
        FirstVisible = Math.Clamp(firstVisible, 0, itemCount);
        _hidden = hidden ?? [];
        Index = new SpatialIndex(Items.Span, FirstVisible, _hidden);
    }

    private readonly (int Start, int End)[] _hidden;

    /// <summary>
    /// Whether an entry can never be seen: before <see cref="FirstVisible"/>, or inside a transparency group before an entry that is
    /// opaque over the whole page within that group. Hidden entries stay in <see cref="Items"/> but are not drawn.
    /// </summary>
    /// <param name="index">Entry index.</param>
    /// <returns>true if hidden.</returns>
    public bool IsHidden(int index)
    {
        if (index < FirstVisible) return true;
        int lo = 0, hi = _hidden.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (index < _hidden[mid].Start) hi = mid - 1;
            else if (index >= _hidden[mid].End) lo = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary>
    /// The first entry that can be seen: the entries before it lie beneath an opaque entry over the whole page. They stay in
    /// <see cref="Items"/> for whatever reads the content (hidden text when cleaning up, image resolutions), but the spatial index — and
    /// so the renderer and hit tests — leaves them out.
    /// </summary>
    public int FirstVisible { get; }

    /// <summary>The drawing commands in drawing order.</summary>
    public ReadOnlyMemory<DisplayItem> Items { get; }

    /// <summary>
    /// Per entry, the number of the character of the page content that drew it (counted from 0 in content order, as
    /// <see cref="Content.IContentObserver.GlyphSerial"/> reports it), -1 for other entries; null unless built with tagging. The entries
    /// drawn by a Type 3 glyph belong to it.
    /// </summary>
    public int[]? GlyphTags { get; init; }

    /// <summary>Number of characters counted while tagging (<see cref="GlyphTags"/>).</summary>
    public int GlyphCount { get; init; }

    /// <summary>Characters that should have painted something but added no entry (colour space /None, clipped away completely); tagging only.</summary>
    public int[] SilentGlyphs { get; init; } = [];

    /// <summary>Characters that add to the clip (text rendering modes 4–7); tagging only.</summary>
    public int[] ClippingGlyphs { get; init; } = [];

    /// <summary>The list reached <see cref="DisplayListBuilder.MaxItems"/>: later content is missing.</summary>
    public bool Truncated { get; init; }

    /// <summary>Verbs of all paths.</summary>
    public PathVerb[] Verbs { get; }

    /// <summary>Coordinates of all paths (x, y alternating), in the space of each path.</summary>
    public float[] Points { get; }

    /// <summary>Per path: first verb, number of verbs, first coordinate (x, y alternating).</summary>
    public (int Verb, int VerbCount, int Point)[] Paths { get; }

    /// <summary>Matrices referenced by entries and clips.</summary>
    public Matrix[] Matrices { get; }

    /// <summary>Styles referenced by <see cref="DisplayItem.Paint"/>.</summary>
    public Paint[] Paints { get; }

    /// <summary>Dash patterns referenced by <see cref="Paint.Dash"/>.</summary>
    public DashPattern[] Dashes { get; }

    /// <summary>Links of the clip chains; index 0 means "no clip".</summary>
    public ClipNode[] Clips { get; }

    /// <summary>Images referenced by <see cref="DisplayItem.Image"/>.</summary>
    public ImageSource[] Images { get; }

    /// <summary>Shadings and tiling patterns referenced by <see cref="Paint.FillShading"/> and <see cref="Paint.StrokeShading"/>.</summary>
    public ShadingFill[] Shadings { get; }

    /// <summary>Transparency groups and runs under soft masks, ascending by start.</summary>
    public GroupNode[] Groups { get; }

    /// <summary>Entries of annotations with NoZoom, in order, not overlapping; only on screen (<see cref="NoZoomRange"/>).</summary>
    public NoZoomRange[] NoZoom { get; init; } = [];

    /// <summary>Soft masks referenced by <see cref="GroupNode.Mask"/>.</summary>
    public SoftMask[] Masks { get; }

    /// <summary>Characters of the page in content order (search, highlighting, copying).</summary>
    public TextGlyph[] Text { get; }

    /// <summary>
    /// Characters drawn visibly whose text cannot be read (a font without a way from its codes to text): they are not in <see cref="Text"/>,
    /// so no search finds them (docs/RECHERCHE-VERSTECKEN.md, B-6).
    /// </summary>
    public int UnreadableGlyphs { get; init; }

    /// <summary>
    /// Characters drawn visibly whose read text is suspicious (docs/RECHERCHE-VERSTECKEN.md, B-5): many different shapes of a font read as the
    /// same text or as characters of the Private Use Area. A search may not find what they show.
    /// </summary>
    public int MisreadGlyphs { get; init; }

    /// <summary>Visible area of the page (CropBox) in page coordinates.</summary>
    public RectD PageBox { get; }

    /// <summary>Spatial index over the bounds of <see cref="Items"/>.</summary>
    public SpatialIndex Index { get; }

    /// <summary>Number of entries.</summary>
    public int Count => Items.Length;

    private int _blendModes;   // 0 = not yet checked, 1 = no, 2 = yes

    /// <summary>Whether anything (entries or groups) uses a blend mode other than Normal; the renderer then draws the page on a transparent backdrop.</summary>
    public bool UsesBlendModes
    {
        get
        {
            if (_blendModes == 0) _blendModes = Paints.Any(p => p.BlendMode != 0) || Groups.Any(g => g.BlendMode != 0) ? 2 : 1;
            return _blendModes == 2;
        }
    }

    /// <summary>Rough size in memory in bytes, for the budget of the cache.</summary>
    /// <remarks>
    /// Counts the actual array sizes (including unused reserve) and all parts; otherwise the
    /// display cache does not keep to its budget (text pages: one matrix per glyph).
    /// </remarks>
    public long EstimatedBytes =>
        (long)_itemCapacity * Unsafe.SizeOf<DisplayItem>() + Verbs.Length + Points.Length * 4L + Paths.Length * 12L
        + Matrices.Length * (long)Unsafe.SizeOf<Matrix>() + Paints.Length * (long)Unsafe.SizeOf<Paint>()
        + Clips.Length * (long)Unsafe.SizeOf<ClipNode>() + Images.Length * 64L + Text.Length * (long)Unsafe.SizeOf<TextGlyph>()
        + Shadings.DistinctBy(s => s.Data).Sum(s => s.EstimatedBytes) + Shadings.Length * 64L
        + Groups.Length * (long)Unsafe.SizeOf<GroupNode>() + Masks.DistinctBy(m => m.Content).Sum(m => m.EstimatedBytes) + Masks.Length * 64L
        + Index.EstimatedBytes;

    private readonly int _itemCapacity;

    /// <summary>Entries whose bounds intersect <paramref name="area"/>, in drawing order.</summary>
    /// <param name="area">The area in page space.</param>
    /// <returns>A new list of entry indices.</returns>
    public List<int> Query(RectD area) => Index.Query(area, Items.Span);

    /// <summary>Like <see cref="Query"/>, but in a rented buffer (renderer); dispose of it after use.</summary>
    /// <param name="area">The area in page space.</param>
    /// <returns>The entry indices in drawing order.</returns>
    public HitList QueryHits(RectD area) => Index.QueryHits(area, Items.Span);
}

/// <summary>
/// Packed R-tree (Sort-Tile-Recursive) over the bounds of the entries: when zoomed in,
/// only the entries in the visible area are drawn.
/// </summary>
/// <remarks>Immutable after construction; queries may run on several threads at once (scratch buffers are per thread).</remarks>
public sealed class SpatialIndex
{
    private const int NodeSize = 16;

    // level 0 = leaves (groups of entries); each higher level combines NodeSize nodes
    private readonly int[] _order;            // entry indices in tree order
    private readonly int _size;               // number of entries of the list (the highest index + 1)
    private readonly List<float[]> _levels;   // per level: bounds [l t r b] per node

    /// <summary>Builds the index.</summary>
    /// <param name="items">The entries whose bounds are indexed.</param>
    /// <param name="from">The first entry indexed; those before it are left out (hidden beneath an opaque one, <see cref="DisplayList.FirstVisible"/>).</param>
    /// <param name="hidden">Further ranges left out (start inclusive, end exclusive; ascending, not overlapping), see <see cref="DisplayList.IsHidden"/>.</param>
    public SpatialIndex(ReadOnlySpan<DisplayItem> items, int from = 0, (int Start, int End)[]? hidden = null)
    {
        _size = items.Length;
        from = Math.Clamp(from, 0, items.Length);
        var n = items.Length - from;
        if (hidden is { Length: > 0 })
        {
            var order = new List<int>(n);
            var next = 0;
            for (var i = from; i < items.Length; i++)
            {
                while (next < hidden.Length && hidden[next].End <= i) next++;
                if (next < hidden.Length && i >= hidden[next].Start) continue;
                order.Add(i);
            }
            _order = [.. order];
            n = _order.Length;
        }
        else
        {
            _order = new int[n];
            for (var i = 0; i < n; i++) _order[i] = from + i;
        }
        _levels = [];
        if (n == 0) return;

        // STR: by x centre into vertical slices, within them by y centre. Sort via key arrays
        // (no comparison delegate per comparison); one array suffices for both steps.
        var keys = new float[n];
        for (var i = 0; i < n; i++) keys[i] = (items[_order[i]].Left + items[_order[i]].Right) / 2;
        var leafCount = (n + NodeSize - 1) / NodeSize;
        var slices = (int)Math.Ceiling(Math.Sqrt(leafCount));
        var sliceSize = slices * NodeSize;
        Array.Sort(keys, _order);
        var ykeys = keys;
        for (var i = 0; i < n; i++) ykeys[i] = (items[_order[i]].Top + items[_order[i]].Bottom) / 2;
        for (var s = 0; s < n; s += sliceSize)
            Array.Sort(ykeys, _order, s, Math.Min(sliceSize, n - s));

        // leaves
        var leaves = new float[leafCount * 4];
        for (var leaf = 0; leaf < leafCount; leaf++)
        {
            float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
            for (var k = leaf * NodeSize; k < Math.Min(n, (leaf + 1) * NodeSize); k++)
            {
                ref readonly var it = ref items[_order[k]];
                l = Math.Min(l, it.Left); t = Math.Min(t, it.Top); r = Math.Max(r, it.Right); b = Math.Max(b, it.Bottom);
            }
            leaves[leaf * 4] = l; leaves[leaf * 4 + 1] = t; leaves[leaf * 4 + 2] = r; leaves[leaf * 4 + 3] = b;
        }
        _levels.Add(leaves);

        // higher levels (the nodes of a level are already in spatial order)
        while (_levels[^1].Length / 4 > 1)
        {
            var below = _levels[^1];
            var count = below.Length / 4;
            var parents = (count + NodeSize - 1) / NodeSize;
            var level = new float[parents * 4];
            for (var p = 0; p < parents; p++)
            {
                float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
                for (var k = p * NodeSize; k < Math.Min(count, (p + 1) * NodeSize); k++)
                {
                    l = Math.Min(l, below[k * 4]); t = Math.Min(t, below[k * 4 + 1]);
                    r = Math.Max(r, below[k * 4 + 2]); b = Math.Max(b, below[k * 4 + 3]);
                }
                level[p * 4] = l; level[p * 4 + 1] = t; level[p * 4 + 2] = r; level[p * 4 + 3] = b;
            }
            _levels.Add(level);
        }
    }

    /// <summary>Rough size in memory in bytes.</summary>
    public long EstimatedBytes => _order.Length * 4L + _levels.Sum(l => l.Length * 4L);

    /// <summary>
    /// Approximate number of entries in <paramref name="area"/>: leaves that intersect the area
    /// times the leaf size. Without a list and without sorting — for dividing up work, not for drawing.
    /// </summary>
    /// <param name="area">The area in page space.</param>
    /// <returns>An upper estimate of the number of entries.</returns>
    public int EstimateCount(RectD area)
    {
        if (_levels.Count == 0) return 0;
        float ql = (float)Math.Max(area.Left, float.MinValue), qt = (float)Math.Max(area.Top, float.MinValue);
        float qr = (float)Math.Min(area.Right, float.MaxValue), qb = (float)Math.Min(area.Bottom, float.MaxValue);
        var count = 0;
        var stack = t_stack ??= new Stack<(int Level, int Node)>();
        stack.Clear();
        stack.Push((_levels.Count - 1, 0));
        while (stack.Count > 0)
        {
            var (level, node) = stack.Pop();
            var b = _levels[level];
            if (b[node * 4] > qr || b[node * 4 + 2] < ql || b[node * 4 + 1] > qb || b[node * 4 + 3] < qt) continue;
            if (level == 0)
            {
                count += NodeSize;
                continue;
            }
            var childCount = _levels[level - 1].Length / 4;
            for (var c = node * NodeSize; c < Math.Min(childCount, (node + 1) * NodeSize); c++) stack.Push((level - 1, c));
        }
        return count;
    }

    [ThreadStatic] private static Stack<(int Level, int Node)>? t_stack;
    [ThreadStatic] private static ulong[]? t_bits;

    /// <summary>
    /// Entries in the area, in drawing order, in a rented buffer (dispose of it after
    /// use). Hits are marked in a bit array per thread and read out in sequence, which
    /// yields drawing order without sorting and without a growing list
    /// (the full view of an A0 plan hits 780,000 entries).
    /// </summary>
    /// <param name="area">The area in page space.</param>
    /// <param name="items">The entries the index was built from.</param>
    /// <returns>The entry indices in drawing order.</returns>
    public HitList QueryHits(RectD area, ReadOnlySpan<DisplayItem> items)
    {
        if (_levels.Count == 0) return new HitList([], 0);
        float ql = (float)Math.Max(area.Left, float.MinValue), qt = (float)Math.Max(area.Top, float.MinValue);
        float qr = (float)Math.Min(area.Right, float.MaxValue), qb = (float)Math.Min(area.Bottom, float.MaxValue);

        var words = (_size + 63) >> 6;
        var bits = t_bits;
        if (bits is null || bits.Length < words) t_bits = bits = new ulong[Math.Max(words, 64)];
        int minWord = int.MaxValue, maxWord = -1, count = 0;

        var stack = t_stack ??= new Stack<(int Level, int Node)>();
        stack.Clear();
        stack.Push((_levels.Count - 1, 0));
        while (stack.Count > 0)
        {
            var (level, node) = stack.Pop();
            var b = _levels[level];
            if (b[node * 4] > qr || b[node * 4 + 2] < ql || b[node * 4 + 1] > qb || b[node * 4 + 3] < qt) continue;
            if (level == 0)
            {
                for (var k = node * NodeSize; k < Math.Min(_order.Length, (node + 1) * NodeSize); k++)
                {
                    var i = _order[k];
                    ref readonly var it = ref items[i];
                    if (!(it.Left <= qr && it.Right >= ql && it.Top <= qb && it.Bottom >= qt)) continue;
                    var w = i >> 6;
                    bits[w] |= 1UL << (i & 63);
                    if (w < minWord) minWord = w;
                    if (w > maxWord) maxWord = w;
                    count++;
                }
            }
            else
            {
                var childCount = _levels[level - 1].Length / 4;
                for (var c = node * NodeSize; c < Math.Min(childCount, (node + 1) * NodeSize); c++) stack.Push((level - 1, c));
            }
        }
        if (count == 0) return new HitList([], 0);
        var buffer = System.Buffers.ArrayPool<int>.Shared.Rent(count);
        var n = 0;
        for (var w = minWord; w <= maxWord; w++)
        {
            var word = bits[w];
            while (word != 0)
            {
                buffer[n++] = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(word);
                word &= word - 1;
            }
            bits[w] = 0;
        }
        return new HitList(buffer, n);
    }

    /// <summary>Like <see cref="QueryHits"/>, but as a new list.</summary>
    /// <param name="area">The area in page space.</param>
    /// <param name="items">The entries the index was built from.</param>
    /// <returns>The entry indices in drawing order.</returns>
    public List<int> Query(RectD area, ReadOnlySpan<DisplayItem> items)
    {
        using var hits = QueryHits(area, items);
        var result = new List<int>(hits.Count);
        for (var i = 0; i < hits.Count; i++) result.Add(hits[i]);
        return result;
    }
}

/// <summary>Hits of a query in a rented buffer; call <see cref="Dispose"/> after use, exactly once.</summary>
public readonly struct HitList(int[] buffer, int count) : IDisposable
{
    /// <summary>Number of hits.</summary>
    public int Count => count;

    /// <summary>Entry index of hit <paramref name="index"/> (0 to <see cref="Count"/> − 1).</summary>
    public int this[int index] => buffer[index];

    /// <summary>Returns the buffer to the pool.</summary>
    public void Dispose()
    {
        if (buffer.Length > 0) System.Buffers.ArrayPool<int>.Shared.Return(buffer);
    }
}
