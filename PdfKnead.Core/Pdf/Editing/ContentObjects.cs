using System.Globalization;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>Where an image or drawing is drawn: its content stream and its operator there.</summary>
/// <param name="Scope">
/// The way to the content stream: empty for the page content, otherwise the numbers of the "Do" operators of the forms on the way,
/// separated by "/" — a form invoked twice is two scopes, and each invocation can be edited alone.
/// </param>
/// <param name="Op">Number of the operator in that stream ("Do", "BI", "sh" or the painting operator of a path).</param>
public readonly record struct ContentKey(string Scope, int Op)
{
    /// <summary>The "Do" operators of the forms on the way, from the page inwards.</summary>
    internal IEnumerable<int> Forms => Scope.Length == 0 ? [] : Scope.Split('/').Select(int.Parse);

    /// <inheritdoc />
    public override string ToString() => Scope.Length == 0 ? Op.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{Scope}:{Op}";
}

/// <summary>What a content object is.</summary>
public enum ContentKind : byte
{
    /// <summary>An image (Do of an image XObject, or an inline image).</summary>
    Image,

    /// <summary>A path that is filled or stroked.</summary>
    Path,

    /// <summary>A shading painted with "sh" (a gradient, usually within a clip).</summary>
    Shading,
}

/// <summary>
/// An image or a drawing the page content draws itself — not one placed in PDF Knead —, also inside forms: where it lies and how it can be
/// moved (see KONZEPT.md, section 5.20). What is drawn where comes from the interpreter of the display; only the structure of the content
/// (blocks) is read here.
/// </summary>
/// <param name="Key">Its content stream and operator.</param>
/// <param name="Kind">Image, path or shading.</param>
/// <param name="ImageToPage">The unit square of an image in page space; for a drawing the unit square onto its bounds (strokes with their line width).</param>
/// <param name="Block">
/// Number of the "q" (in the same stream) that opens the block drawing this object and nothing else (with its clipping path, as layout programs
/// write <c>q … W n q … cm /Im Do Q Q</c>); −1 if there is none — then the operator itself is moved, and a clip it shares stays where it is.
/// </param>
/// <param name="BlockMatrix">The CTM at <paramref name="Block"/> (or at the operator if there is no block).</param>
/// <param name="OuterClip">Bounds of the clip where the movable part begins: it stays when the object moves. <see cref="RectD.Infinite"/> if none.</param>
/// <param name="InnerClip">Bounds of the clip set inside the block (it moves with the object); <see cref="RectD.Infinite"/> if none.</param>
public sealed record ContentObject(ContentKey Key, ContentKind Kind, Matrix ImageToPage, int Block, Matrix BlockMatrix, RectD OuterClip, RectD InnerClip)
{
    /// <summary>Width in pixels (images).</summary>
    public int PixelWidth { get; init; }

    /// <summary>Height in pixels (images).</summary>
    public int PixelHeight { get; init; }

    /// <summary>An inline image (BI … EI).</summary>
    public bool Inline { get; init; }

    /// <summary>Object number of the image XObject; null for an inline image, a direct object or a drawing.</summary>
    public int? ObjectNumber { get; init; }

    /// <summary>The image itself, for copying, saving and cutting it (stencil masks in black); null for a drawing.</summary>
    public ImageSource? Source { get; init; }

    /// <summary>A path that also sets the clip ("W f"): it can only be moved within a block of its own (wrapped, the clip would end with it).</summary>
    public bool AlsoClips { get; init; }

    /// <summary>For a drawing: what it forms one graphic with (a block of drawings, a form, a word of outlined letters); null if it stands alone.</summary>
    public string? Group { get; init; }

    /// <summary>How many objects belong to <see cref="Group"/>; 0 without a group.</summary>
    public int GroupSize { get; init; }

    /// <summary>The "q" of the block the group is (in the same stream); −1 if the group is no block (a form, a word) or there is none.</summary>
    public int GroupBlock { get; init; } = -1;

    /// <summary>The CTM at <see cref="GroupBlock"/>.</summary>
    public Matrix GroupMatrix { get; init; } = Matrix.Identity;

    /// <summary>The clip at <see cref="GroupBlock"/> without the boxes of forms: it stays when the whole group moves.</summary>
    internal RectD GroupStays { get; init; } = RectD.Infinite;

    /// <summary>The clips set since <see cref="GroupBlock"/> opened (the group's own, and this object's): they move with the whole group.</summary>
    internal RectD GroupOwnClip { get; init; } = RectD.Infinite;

    /// <summary>
    /// The object moved together with its whole group as one block (<see cref="GroupBlock"/>): the clips the group sets move along — a logo
    /// clipped to its own box stays visible (docs/FEHLERGEDAECHTNIS.md, No. 398). Only for a group that is a block.
    /// </summary>
    /// <param name="transform">The move in page space.</param>
    /// <returns>The object at its new place.</returns>
    public ContentObject MovedAsGroup(Matrix transform)
    {
        var moved = Moved(transform);
        var own = GroupOwnClip == RectD.Infinite ? RectD.Infinite : transform.TransformBounds(GroupOwnClip);
        return moved with { InnerClip = own, OwnClip = own, OuterClip = GroupStays };
    }

    /// <summary>
    /// Whether the moves leave the whole group of <paramref name="o"/> in one piece: every member moved by the same matrix, none deleted —
    /// then the group's block moves instead of each member's (<see cref="MovedAsGroup"/>).
    /// </summary>
    /// <param name="o">An object of the group.</param>
    /// <param name="members">How many moved objects belong to its group with the same move.</param>
    /// <returns>Whether it moves as a group.</returns>
    internal static bool MovesAsGroup(ContentObject o, int members) => o.GroupBlock >= 0 && o.GroupSize > 1 && members == o.GroupSize;

    /// <summary>
    /// The forms on the way to it (outermost first): each form's space mapped to the page. A form clips to its box (/BBox); moved within it,
    /// the object would be cut off at the old frame — the copy of each form on the way gets a box that holds the new place.
    /// </summary>
    public IReadOnlyList<Matrix> FormsToPage { get; init; } = [];

    /// <summary>The box (/BBox) of each form on the way, as in <see cref="FormsToPage"/>: its corners in page space; null if it has none.</summary>
    internal IReadOnlyList<(double X, double Y)[]?> FormBoxes { get; init; } = [];

    /// <summary>The CTM at the operator (for an image the same as <see cref="ImageToPage"/>).</summary>
    public Matrix Ctm { get; init; }

    /// <summary>For a path: half its line width in page space (with square caps), how far a stroke reaches beyond the path.</summary>
    public double StrokeReach { get; init; }

    /// <summary>
    /// <see cref="OuterClip"/> without the boxes (/BBox) of the forms on the way: moving the object grows the box of each form it is in, so
    /// at its new place only these clips cut it.
    /// </summary>
    internal RectD OuterClipOutsideForms { get; init; } = RectD.Infinite;

    /// <summary>The clips its block sets itself, not cut by those around the block: they move with the object; <see cref="RectD.Infinite"/> if none.</summary>
    internal RectD OwnClip { get; init; } = RectD.Infinite;

    /// <summary>For a path: where its construction lies in the content, and how it is painted — for hit testing its real shape.</summary>
    internal PathShape? Shape { get; init; }

    /// <summary>
    /// Whether the point (page space) lies on what is drawn: an image within its (possibly turned) square, a path on its fill (by its fill
    /// rule) or within <paramref name="tolerance"/> plus half its line width of its stroke, a shading within its clip — and always within the clip.
    /// </summary>
    /// <param name="x">x in page space.</param>
    /// <param name="y">y in page space.</param>
    /// <param name="tolerance">Allowance in page units (a few screen pixels: thin lines can be hit).</param>
    /// <returns>Whether it is hit.</returns>
    public bool Hits(double x, double y, double tolerance)
    {
        var visible = Visible.Inflate(tolerance);
        if (x < visible.Left || x > visible.Right || y < visible.Top || y > visible.Bottom) return false;
        switch (Kind)
        {
            case ContentKind.Image:
                if (!ImageToPage.IsInvertible) return false;
                var (u, v) = ImageToPage.Invert().Transform(x, y);
                var slack = tolerance / Math.Max(1e-9, ImageToPage.MeanScale);
                return u >= -slack && u <= 1 + slack && v >= -slack && v <= 1 + slack;
            case ContentKind.Path when Shape is { } shape:
                return shape.Hits(Ctm, x, y, tolerance, StrokeReach);
            default:
                return true;   // a shading fills its clip; a path whose construction was not found counts by its bounds
        }
    }

    /// <summary>A drawing (path or shading), not an image: it can be moved, turned, deleted and replaced, not cut, copied or saved as an image.</summary>
    public bool IsDrawing => Kind != ContentKind.Image;

    /// <summary>Bounds in page space (y upwards: <see cref="RectD.Top"/> is the smaller y), clipped or not.</summary>
    public RectD Bounds => ImageToPage.TransformBounds(RectD.FromLtrb(0, 0, 1, 1));

    /// <summary>The part that can be seen: <see cref="Bounds"/> within both clips (layout programs place images larger than their frame).</summary>
    public RectD Visible => Bounds.Intersect(InnerClip).Intersect(OuterClip);

    /// <summary>
    /// This object moved by <paramref name="transform"/> (page space): the clips its block sets itself go with it, those around it stay —
    /// without the boxes of the forms it is in, which the move grows.
    /// </summary>
    /// <param name="transform">The transformation in page space.</param>
    /// <returns>The object at its new place.</returns>
    public ContentObject Moved(Matrix transform)
    {
        var inner = OwnClip == RectD.Infinite ? RectD.Infinite : transform.TransformBounds(OwnClip);
        // a shading fills its clip: what moves is the area it showed (the editor cuts it to that area)
        var toPage = Kind == ContentKind.Shading && Visible is { IsEmpty: false } shown && transform.TransformBounds(shown) is var area
            ? Matrix.Scale(area.Width, area.Height) * Matrix.Translate(area.Left, area.Top)
            : ImageToPage * transform;
        return this with
        {
            ImageToPage = toPage,
            Ctm = Ctm * transform,
            StrokeReach = StrokeReach * transform.MeanScale,
            InnerClip = inner,
            OwnClip = inner,
            OuterClip = OuterClipOutsideForms,
        };
    }

    /// <summary>Resolution across an image (pixels per inch along its width); 0 for a drawing or without width.</summary>
    public double DpiX => Length(ImageToPage.TransformVector(1, 0)) is > 1e-9 and var w ? PixelWidth / (w / 72) : 0;

    /// <summary>Resolution along an image's height.</summary>
    public double DpiY => Length(ImageToPage.TransformVector(0, 1)) is > 1e-9 and var h ? PixelHeight / (h / 72) : 0;

    /// <summary>Upright or turned by a multiple of 90°, not sheared: such images can join into one (tiles).</summary>
    public bool AxisAligned =>
        Math.Abs(ImageToPage.B) < 1e-6 * Math.Abs(ImageToPage.A) && Math.Abs(ImageToPage.C) < 1e-6 * Math.Abs(ImageToPage.D) ||
        Math.Abs(ImageToPage.A) < 1e-6 * Math.Abs(ImageToPage.B) && Math.Abs(ImageToPage.D) < 1e-6 * Math.Abs(ImageToPage.C);

    private static double Length((double X, double Y) v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);
}

/// <summary>
/// Finds the images and drawings a page draws — in its content and in the forms it invokes, as the interpreter of the display reports them —
/// and what belongs together: one picture cut into tiles, one graphic of many paths. Every object of the page is taken, however many there are
/// (a plan has hundreds of thousands of paths), up to a memory budget against prepared files.
/// </summary>
public static class ContentObjects
{
    /// <summary>A graphic of a block or form holds at most this many paths and this share of the page — otherwise a page drawn in one block would be one graphic.</summary>
    private const int MaxGroupPaths = 2000;

    private const double MaxGroupShare = 0.4;

    /// <summary>Memory of one object in the table, with its share of the grid and of the lists while building (measured, rounded up).</summary>
    private const int BytesPerObject = 160;

    /// <summary>
    /// At most this many objects per page: a sixteenth of the memory available — the interpreter allows a page 16 million operators, which a
    /// prepared file could spend on as many tiny paths. A real plan needs a fraction of it.
    /// </summary>
    private static readonly int MaxObjects = (int)Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 16 / BytesPerObject, 100_000, int.MaxValue / 2);

    /// <summary>
    /// The images and drawings of <paramref name="page"/>, in drawing order: in its content and in the forms it invokes (not those on hidden
    /// layers, in soft masks, patterns or annotations).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The objects; none if the content cannot be read.</returns>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static PageObjects Find(PdfDocument doc, PdfPage page, CancellationToken cancel = default)
    {
        var table = new ContentTable { Page = page.Dictionary };
        var collector = new Collector(table, MaxObjects);
        try
        {
            PageInterpreter.Observe(doc, page, collector, cancel);
            using (var content = Redactor.PageContent(doc, page.Dictionary)) table.Contents[0] = content.Span.ToArray();
            // a form invoked many times is read once
            var read = new Dictionary<PdfStream, byte[]>(ReferenceEqualityComparer.Instance);
            foreach (var (scope, form) in collector.Forms)
            {
                if (!read.TryGetValue(form, out var data)) read[form] = data = doc.GetStreamData(form);
                table.Contents[scope] = data;
            }
            var pageArea = page.CropBox.Width * page.CropBox.Height;
            // without a block: the whole clip at the operator stays when the object moves
            for (var i = 0; i < table.Count; i++)
            {
                ref var e = ref table[i];
                e.OuterClip = table.ClipId(collector.Chains.Full(collector.ClipAt[i]));
                e.OuterOpen = table.ClipId(collector.Chains.OutsideForms(collector.ClipAt[i]));
            }
            for (var scope = 0; scope < table.Scopes.Count; scope++)
                if (table.Contents[scope] is { } content) Structure(scope, content, table, collector.ClipAt, collector.Chains, collector.Saves, pageArea, cancel);
            JoinNeighbours(table, pageArea);
            table.Finish(cancel);
            return new PageObjects(table, null);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            return PageObjects.Empty;
        }
    }

    // ------------------------------------------------------------ What is drawn (from the interpreter)

    /// <summary>Takes the images, paths and shadings the interpreter reports into the table, with the CTM and clip at each "q".</summary>
    private sealed class Collector(ContentTable table, int limit) : IContentObserver
    {
        public readonly List<(int Scope, PdfStream Stream)> Forms = [];
        public readonly Dictionary<long, (int Ctm, int Link)> Saves = [];   // (scope, op of a "q") → CTM and link of the clip chain there
        public readonly List<int> ClipAt = [];   // per entry: the link of the clip chain at its operator
        public readonly ClipChains Chains = new();
        private readonly Stack<int> _scopes = new([0]);   // −1: not editable (soft mask, pattern cell, hidden form)
        private int _link;   // the link of the clip chain in effect (0: none)
        private double _reach;

        private int Scope => _scopes.Peek();

        public bool WantsStroke => true;

        public void Stroke(double reach, DashPattern? dash) => _reach = double.IsFinite(reach) ? reach : 0;

        public bool WantsClip => true;

        public void Clip(RectD bounds)
        {
        }

        public bool WantsClipChain => true;

        public void ClipLink(int link, int parent, RectD own, bool formBox) => Chains.Add(link, parent, own, formBox);

        public void ClipChain(int link) => _link = link;

        public bool WantsSaveState => true;

        public void SaveState(int op, Matrix ctm, RectD clip, int link)
        {
            if (Scope >= 0) Saves[Key(Scope, op)] = (table.MatrixId(ctm), link);
        }

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize,
            double horizontalScale, bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible)
        {
        }

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden)
        {
            if (hidden || !Finite(bounds)) return;
            // a line along an axis has no width: half a point, so that it can be hit and framed
            bounds = RectD.FromLtrb(bounds.Left, bounds.Top, Math.Max(bounds.Right, bounds.Left + 0.5), Math.Max(bounds.Bottom, bounds.Top + 0.5));
            Add(op, ContentKind.Path, bounds, ctm, -1);
            _reach = 0;
        }

        public void Image(int op, Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool hidden)
        {
            if (hidden || !imageToPage.IsFinite || (stream?.Dictionary ?? inline?.Dictionary) is not { } dict || !Open(op)) return;
            var stencil = (dict.GetBoolean(PdfName.Get("ImageMask")) ?? dict.GetBoolean(PdfName.Get("IM"))) == true;
            var source = new ImageSource(stream, inline, resources, stencil, 0xFF000000);
            int Pixels(string name, string shortName) => (int)Math.Clamp(dict.GetInteger(PdfName.Get(name)) ?? dict.GetInteger(PdfName.Get(shortName)) ?? 0, 0, int.MaxValue);
            table.Images.Add(new ContentTable.ImageData(source, imageToPage, Pixels("Width", "W"), Pixels("Height", "H"), stream?.Id?.Number, inline is not null));
            Add(op, ContentKind.Image, imageToPage.TransformBounds(RectD.FromLtrb(0, 0, 1, 1)), imageToPage, table.Images.Count - 1);
        }

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden)
        {
            if (hidden || !Finite(bounds) || bounds.IsEmpty) return;
            Add(op, ContentKind.Shading, bounds, ctm, -1);
        }

        public void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden)
        {
            var inner = -1;
            if (!hidden && Scope >= 0)
            {
                var outer = table.Scopes[Scope];
                inner = table.ScopeId(outer.Length == 0 ? op.ToString(CultureInfo.InvariantCulture) : $"{outer}/{op.ToString(CultureInfo.InvariantCulture)}");
                table.FormsToPage[inner] = [.. table.FormsToPage[Scope], formToPage];
                (double X, double Y)[]? box = Document.PdfRectangle.From(form.Dictionary.Get(PdfName.Get("BBox"))) is { } b
                    ? [formToPage.Transform(b.Left, b.Bottom), formToPage.Transform(b.Right, b.Bottom), formToPage.Transform(b.Right, b.Top), formToPage.Transform(b.Left, b.Top)]
                    : null;
                table.FormBoxes[inner] = [.. table.FormBoxes[Scope], box];
                Forms.Add((inner, form));
            }
            _scopes.Push(inner);
        }

        public void BeginSoftMask(int op, PdfStream group, Matrix groupToPage) => _scopes.Push(-1);

        public void BeginPatternCell(int op, bool stroke, PdfStream pattern, Matrix cellToPage) => _scopes.Push(-1);

        public void EndForm()
        {
            if (_scopes.Count > 1) _scopes.Pop();
        }

        /// <summary>Whether an object at <paramref name="op"/> can be taken: in an editable scope, not reported before, within the budget.</summary>
        private bool Open(int op)
        {
            if (Scope < 0) return false;
            // the same operator reported twice (a form invoked from itself is not followed): a stream reports its operators in order
            if (table.ScopeEntries[Scope] is { Count: > 0 } known && table[known[^1]].Op >= op) return false;
            if (table.Count < limit) return true;
            table.Truncated = true;
            return false;
        }

        private void Add(int op, ContentKind kind, RectD bounds, Matrix ctm, int image)
        {
            if (image < 0 && !Open(op)) return;
            var matrix = table.MatrixId(ctm);
            var index = table.Add(new ContentTable.Entry
            {
                Bounds = bounds, Op = op, Scope = Scope, Ctm = matrix, BlockMatrix = matrix, Block = -1, Image = image,
                Reach = (float)_reach, Kind = kind,
            });
            table.ScopeEntries[Scope].Add(index);
            ClipAt.Add(_link);
        }

        private static bool Finite(RectD r) => double.IsFinite(r.Left) && double.IsFinite(r.Top) && double.IsFinite(r.Right) && double.IsFinite(r.Bottom);
    }

    private static long Key(int scope, int op) => (long)scope << 32 | (uint)op;

    // ------------------------------------------------------------ The structure of each content stream

    /// <summary>
    /// Reads the blocks of one content stream: for each object the outermost block that draws it and nothing else (it moves with its clip),
    /// whether a path also clips and where its construction lies, and the blocks — or the whole form — that draw only drawings (one graphic).
    /// </summary>
    private static void Structure(int scope, byte[] content, ContentTable table, List<int> clipAt, ClipChains chains, Dictionary<long, (int Ctm, int Link)> saves,
        double pageArea, CancellationToken cancel)
    {
        var own = table.ScopeEntries[scope];
        var next = 0;   // the next object of this stream (they come in operator order)
        // per open "q": its operator, how many things were painted inside, the object if it is the only one, the drawings inside and whether
        // anything else was painted (then it is no graphic)
        var stack = new List<(int Op, int Painted, int Object, List<int>? Drawings, bool Mixed)>();
        var root = (Painted: 0, Drawings: new List<int>(), Mixed: false);
        var clipping = false;
        var parser = new ContentParser(content);
        var number = -1;
        var end = 0;
        var pathStart = -1;   // byte where the path under construction begins (its first operator's operands)
        while (parser.Next() is { } op)
        {
            if ((++number & 0xFFFF) == 0) cancel.ThrowIfCancellationRequested();
            var start = end;
            end = parser.Position;
            int painted;   // -1: something that is no listed object; ≥ 0: that object
            switch (op)
            {
                case "m" or "l" or "c" or "v" or "y" or "h" or "re":
                    if (pathStart < 0) pathStart = start;
                    continue;
                case "q":
                    if (stack.Count < 4096) stack.Add((number, 0, -1, null, false));
                    continue;
                case "Q":
                    if (stack.Count == 0) continue;
                    var block = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    if (block.Painted == 1 && block.Object >= 0 && saves.TryGetValue(Key(scope, block.Op), out var save))
                    {
                        // the outermost wins: it is closed last
                        ref var entry = ref table[block.Object];
                        var link = clipAt[block.Object];
                        entry.Block = block.Op;
                        entry.BlockMatrix = save.Ctm;
                        entry.OuterClip = table.ClipId(chains.Full(save.Link));
                        entry.InnerClip = link == save.Link ? 0 : table.ClipId(chains.Full(link));
                        entry.OuterOpen = table.ClipId(chains.OutsideForms(save.Link));
                        entry.OwnClip = link == save.Link ? 0 : table.ClipId(chains.Since(link, save.Link));
                    }
                    if (!block.Mixed && Graphic(block.Drawings, table, pageArea))
                    {
                        // the block of the graphic: moved as a whole, it moves there — with the clips it sets (a logo clipped to its box)
                        (int, int, int)? at = saves.TryGetValue(Key(scope, block.Op), out var groupSave)
                            ? (block.Op, groupSave.Ctm, table.ClipId(chains.OutsideForms(groupSave.Link)))
                            : null;
                        var group = table.AddGroup($"{table.Scopes[scope]}#{block.Op.ToString(CultureInfo.InvariantCulture)}", at);
                        foreach (var d in block.Drawings!)
                        {
                            table[d].Group = group;
                            table[d].GroupOwnClip = at is null ? 0 : table.ClipId(chains.Since(clipAt[d], groupSave.Link));
                        }
                    }
                    if (stack.Count > 0 && block.Painted > 0)
                    {
                        var parent = stack[^1];
                        var merged = Merge(parent.Drawings, block.Drawings);
                        stack[^1] = parent with
                        {
                            Painted = parent.Painted + block.Painted, Object = parent.Painted == 0 && block.Painted == 1 ? block.Object : -1,
                            Drawings = merged, Mixed = parent.Mixed || block.Mixed || merged is null && block.Drawings is { Count: > 0 },
                        };
                    }
                    continue;
                case "W" or "W*":
                    clipping = true;
                    continue;
                case "n":
                    clipping = false;
                    pathStart = -1;
                    continue;
                case "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "S" or "s" or "Do" or "BI" or "sh" or "Tj" or "TJ" or "'" or "\"":
                    while (next < own.Count && table[own[next]].Op < number) next++;
                    painted = next < own.Count && table[own[next]].Op == number ? own[next] : -1;
                    if (painted >= 0 && table[painted].Kind == ContentKind.Path)
                    {
                        ref var path = ref table[painted];
                        var strokes = op is "S" or "s" or "B" or "B*" or "b" or "b*";
                        path.Flags = (byte)((clipping ? ContentTable.AlsoClips : 0) | (op is not ("S" or "s") ? ContentTable.Fills : 0) |
                                            (strokes ? ContentTable.Strokes : 0) | (op is "f*" or "B*" or "b*" ? ContentTable.EvenOdd : 0));
                        (path.ShapeStart, path.ShapeEnd) = pathStart >= 0 ? (pathStart, end) : (-1, 0);
                    }
                    if (op is not ("Do" or "BI" or "sh" or "Tj" or "TJ" or "'" or "\""))
                    {
                        clipping = false;
                        pathStart = -1;
                    }
                    // a "Do" of a form draws through its own objects (reported in its scope): here it is "something else"
                    break;
                default:
                    continue;
            }
            var isDrawing = painted >= 0 && table[painted].Kind != ContentKind.Image;
            if (stack.Count == 0)
            {
                if (isDrawing && root.Drawings.Count <= MaxGroupPaths) root.Drawings.Add(painted);
                root.Painted++;
                root.Mixed |= !isDrawing;
                continue;
            }
            var top = stack[^1];
            var list = top.Drawings;
            if (isDrawing && (list?.Count ?? 0) <= MaxGroupPaths) (list ??= []).Add(painted);
            stack[^1] = top with
            {
                Painted = top.Painted + 1, Object = top.Painted == 0 ? painted : -1, Drawings = list,
                Mixed = top.Mixed || !isDrawing || list is not { Count: <= MaxGroupPaths },
            };
        }
        // a form that draws only drawings (a logo placed as a form): one graphic, for those not in a smaller one
        if (scope > 0 && !root.Mixed && Graphic(root.Drawings, table, pageArea))
        {
            var group = -1;
            foreach (var d in root.Drawings)
                if (table[d].Group < 0) table[d].Group = group >= 0 ? group : group = table.AddGroup($"{table.Scopes[scope]}#form");
        }
    }

    private static List<int>? Merge(List<int>? parent, List<int>? inner)
    {
        if (inner is not { Count: > 0 }) return parent;
        if ((parent?.Count ?? 0) + inner.Count > MaxGroupPaths + 1) return null;
        var merged = parent ?? [];
        merged.AddRange(inner);
        return merged;
    }

    /// <summary>Drawings that may form one graphic: some, not too many, not covering most of the page.</summary>
    private static bool Graphic(List<int>? drawings, ContentTable table, double pageArea)
    {
        if (drawings is not { Count: > 0 and <= MaxGroupPaths }) return false;
        var area = table[drawings[0]].Bounds;
        foreach (var d in drawings) area = area.Union(table[d].Bounds);
        return area.Width * area.Height <= MaxGroupShare * pageArea;
    }

    // ------------------------------------------------------------ Words of outlined letters

    /// <summary>A graphic with at most this many paths counts as a letter or a sign that may join its neighbours.</summary>
    private const int SmallGraphic = 3;

    /// <summary>A letter or sign covers at most this share of the page (a line across it is none).</summary>
    private const double MaxLetterShare = 0.03;

    /// <summary>How many small graphics drawn before one are compared with it.</summary>
    private const int NeighbourWindow = 50;

    /// <summary>
    /// Small graphics of a similar height drawn near one another, on the same line and with gaps at most their height, form one graphic: the
    /// letters of a word in outlines, a logo whose letters a layout program wrote each in a block of its own (not necessarily from left to
    /// right). Each is compared with the <see cref="NeighbourWindow"/> small graphics drawn before it; an image in between breaks the run.
    /// </summary>
    private static void JoinNeighbours(ContentTable table, double pageArea)
    {
        // each graphic (or single drawing) as a unit with its box and number of members, in drawing order; −1 = a break (an image)
        var unitOf = new int[table.Count];
        var boxes = new List<RectD>();
        var sizes = new List<int>();
        var groupUnit = new Dictionary<int, int>();   // group → unit
        for (var i = 0; i < table.Count; i++)
        {
            ref var e = ref table[i];
            if (e.Kind == ContentKind.Image)
            {
                unitOf[i] = -1;
                continue;
            }
            var visible = table.Visible(i);
            if (e.Group >= 0 && groupUnit.TryGetValue(e.Group, out var known))
            {
                unitOf[i] = known;
                boxes[known] = boxes[known].Union(visible);
                sizes[known]++;
                continue;
            }
            if (e.Group >= 0) groupUnit[e.Group] = boxes.Count;
            unitOf[i] = boxes.Count;
            boxes.Add(visible);
            sizes.Add(1);
        }
        bool Small(int u) => sizes[u] <= SmallGraphic && !boxes[u].IsEmpty && boxes[u].Width * boxes[u].Height <= MaxLetterShare * pageArea;
        var parent = new int[boxes.Count];
        for (var u = 0; u < parent.Length; u++) parent[u] = u;
        int Root(int u)
        {
            while (parent[u] != u) u = parent[u] = parent[parent[u]];
            return u;
        }
        // in drawing order: the units of the entries, each once, with the breaks between them
        var recent = new List<int>();   // small units since the last break, the latest last
        var seen = new bool[boxes.Count];
        for (var i = 0; i < table.Count; i++)
        {
            var u = unitOf[i];
            if (u < 0)
            {
                recent.Clear();
                continue;
            }
            if (seen[u]) continue;
            seen[u] = true;
            if (!Small(u)) continue;
            var box = boxes[u];
            for (var k = Math.Max(0, recent.Count - NeighbourWindow); k < recent.Count; k++)
            {
                var other = boxes[recent[k]];
                var overlap = Math.Min(other.Bottom, box.Bottom) - Math.Max(other.Top, box.Top);
                var gap = Math.Max(box.Left - other.Right, other.Left - box.Right);
                // letters of one line have a similar height (a line across the page overlaps everything, but is no letter)
                var similar = Math.Max(other.Height, box.Height) <= 3 * Math.Min(other.Height, box.Height);
                if (similar && overlap >= 0.5 * Math.Min(other.Height, box.Height) && gap <= Math.Max(other.Height, box.Height))
                    parent[Root(u)] = Root(recent[k]);
            }
            if (recent.Count >= 4 * NeighbourWindow) recent.RemoveRange(0, recent.Count - NeighbourWindow);
            recent.Add(u);
        }
        var joined = new Dictionary<int, int>();   // root → number of small units
        for (var u = 0; u < boxes.Count; u++)
            if (Small(u)) joined[Root(u)] = joined.GetValueOrDefault(Root(u)) + 1;
        var ids = new Dictionary<int, int>();
        for (var i = 0; i < table.Count; i++)
        {
            var u = unitOf[i];
            if (u < 0 || !Small(u) || joined[Root(u)] < 2) continue;
            if (!ids.TryGetValue(Root(u), out var id))
            {
                ref var first = ref table[i];
                ids[Root(u)] = id = table.AddGroup("word:" + new ContentKey(table.Scopes[first.Scope], first.Op));
            }
            table[i].Group = id;
        }
    }
}

/// <summary>
/// The clip chain of a page as the interpreter builds it (<see cref="IContentObserver.ClipLink"/>): each link with its own bounds and whether it
/// is the box of a form. From it: the whole clip at a link, the same without the boxes of forms, and the clips set after a given link.
/// </summary>
internal sealed class ClipChains
{
    private readonly List<(int Parent, RectD Own, bool FormBox)> _links = [(-1, RectD.Infinite, false)];
    private readonly Dictionary<int, RectD> _full = [], _outside = [];

    public void Add(int link, int parent, RectD own, bool formBox)
    {
        while (_links.Count <= link) _links.Add((0, RectD.Infinite, false));   // links of a build the observer did not see (none in practice)
        _links[link] = (parent, own, formBox);
    }

    private bool Known(int link) => link > 0 && link < _links.Count && _links[link].Parent >= 0 && _links[link].Parent < link;

    /// <summary>The whole clip at a link (page space).</summary>
    public RectD Full(int link) => Fold(link, _full, outsideForms: false);

    /// <summary>The clip at a link without the boxes of the forms on the way.</summary>
    public RectD OutsideForms(int link) => Fold(link, _outside, outsideForms: true);

    private RectD Fold(int link, Dictionary<int, RectD> memo, bool outsideForms)
    {
        if (!Known(link)) return RectD.Infinite;
        if (memo.TryGetValue(link, out var known)) return known;
        // iteratively: a chain can be long (a clip per path)
        var way = new List<int>();
        var at = link;
        while (Known(at) && !memo.ContainsKey(at))
        {
            way.Add(at);
            at = _links[at].Parent;
        }
        var bounds = Known(at) ? memo[at] : RectD.Infinite;
        for (var i = way.Count - 1; i >= 0; i--)
        {
            var (_, own, box) = _links[way[i]];
            if (!(outsideForms && box)) bounds = own.Intersect(bounds);
            memo[way[i]] = bounds;
        }
        return bounds;
    }

    /// <summary>
    /// The clips set after <paramref name="ancestor"/> up to <paramref name="link"/> (those a block sets itself), not cut by those before;
    /// if <paramref name="ancestor"/> is not on the way, the whole clip without the boxes of forms.
    /// </summary>
    public RectD Since(int link, int ancestor)
    {
        var bounds = RectD.Infinite;
        for (var at = link; at != ancestor; at = _links[at].Parent)
        {
            if (!Known(at)) return OutsideForms(link);
            if (!_links[at].FormBox) bounds = _links[at].Own.Intersect(bounds);
        }
        return bounds;
    }
}
