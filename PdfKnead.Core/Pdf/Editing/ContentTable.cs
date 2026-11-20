using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// The images and drawings of one page in a compact form (see KONZEPT.md, section 5.20): a plan draws hundreds of thousands of paths, and each
/// must be choosable. One entry per object with the matrices and clips it shares with others kept once, images in a table of their own; an
/// <see cref="ContentObject"/> is made only when asked for. A grid over the page finds what lies at a point without going through all of them.
/// Built by <see cref="ContentObjects.Find"/>, read through <see cref="PageObjects"/>; not changed once built.
/// </summary>
internal sealed class ContentTable
{
    /// <summary>One object.</summary>
    internal struct Entry
    {
        public RectD Bounds;        // page space; images: their square's bounds, paths at least half a point wide and high
        public int Op;              // operator number in its stream
        public int Scope;           // index into Scopes
        public int Ctm;             // index into Matrices
        public int BlockMatrix;     // index into Matrices
        public int Block;           // the "q" of its block, −1 if none
        public int OuterClip;       // index into Clips
        public int InnerClip;       // index into Clips
        public int OuterOpen;       // the outer clip without the boxes of forms (a move grows the box of a form): index into Clips
        public int OwnClip;         // the clips its block sets itself (they move with it): index into Clips
        public int Group = -1;      // index into GroupNames, −1 if it stands alone
        public int GroupOwnClip;    // the clips set since its group's block opened (they move with the whole group): index into Clips
        public int Image = -1;      // index into Images, −1 for a drawing
        public int ShapeStart = -1; // its path in the stream of its scope (−1: not found)
        public int ShapeEnd;
        public float Reach;         // half the line width in page space
        public ContentKind Kind;
        public byte Flags;          // see the constants below

        public Entry()
        {
        }
    }

    public const byte AlsoClips = 1, Fills = 2, Strokes = 4, EvenOdd = 8;

    /// <summary>An image: what is needed to copy, save and cut it, and its exact placement.</summary>
    internal sealed record ImageData(ImageSource Source, Matrix ImageToPage, int PixelWidth, int PixelHeight, int? ObjectNumber, bool Inline);

    // in blocks of a fixed size: growing copies nothing (a plan has hundreds of thousands of entries)
    private const int BlockBits = 12, BlockSize = 1 << BlockBits;
    private readonly List<Entry[]> _blocks = [];

    /// <summary>Number of objects.</summary>
    public int Count { get; private set; }

    /// <summary>The scopes ("" the page, "2/7" a form on the way), their content streams, the forms on the way, and their objects by operator.</summary>
    public readonly List<string> Scopes = [""];
    public readonly List<byte[]?> Contents = [null];
    public readonly List<Matrix[]> FormsToPage = [[]];
    public readonly List<(double X, double Y)[]?[]> FormBoxes = [[]];   // per scope: the box of each form on the way (corners, page space)
    public readonly List<List<int>> ScopeEntries = [[]];
    private readonly Dictionary<string, int> _scopeIds = new(StringComparer.Ordinal) { [""] = 0 };

    public readonly List<Matrix> Matrices = [];
    private readonly Dictionary<Matrix, int> _matrixIds = [];
    public readonly List<RectD> Clips = [RectD.Infinite];
    private readonly Dictionary<RectD, int> _clipIds = new() { [RectD.Infinite] = 0 };
    public readonly List<ImageData> Images = [];
    public readonly List<string> GroupNames = [];

    /// <summary>Per group: the "q" of the block it is (−1 for a form or a word), the CTM there (index into Matrices), and the clip there without
    /// the boxes of forms (index into Clips) — what stays when the whole group moves.</summary>
    public readonly List<(int Op, int Ctm, int Stays)> GroupBlocks = [];

    /// <summary>The dictionary of the page the objects were found in.</summary>
    public Objects.PdfDictionary? Page { get; init; }

    /// <summary>Not every object could be taken: the page has more than the memory budget allows (a prepared file).</summary>
    public bool Truncated { get; set; }

    public ref Entry this[int index] => ref _blocks[index >> BlockBits][index & (BlockSize - 1)];

    public int Add(in Entry entry)
    {
        if ((Count & (BlockSize - 1)) == 0) _blocks.Add(new Entry[BlockSize]);
        this[Count] = entry;
        return Count++;
    }

    public int ScopeId(string scope)
    {
        if (_scopeIds.TryGetValue(scope, out var id)) return id;
        id = Scopes.Count;
        _scopeIds[scope] = id;
        Scopes.Add(scope);
        Contents.Add(null);
        FormsToPage.Add([]);
        FormBoxes.Add([]);
        ScopeEntries.Add([]);
        return id;
    }

    /// <summary>The entries in a scope and in the forms invoked from it.</summary>
    public IEnumerable<int> InScope(string scope)
    {
        for (var s = 0; s < Scopes.Count; s++)
            if (Scopes[s] == scope || Scopes[s].StartsWith(scope + "/", StringComparison.Ordinal))
                foreach (var e in ScopeEntries[s]) yield return e;
    }

    /// <summary>The index of a scope; −1 if it has no objects.</summary>
    public int ScopeIndex(string scope) => _scopeIds.TryGetValue(scope, out var id) ? id : -1;

    public int MatrixId(Matrix m)
    {
        if (_matrixIds.TryGetValue(m, out var id)) return id;
        _matrixIds[m] = id = Matrices.Count;
        Matrices.Add(m);
        return id;
    }

    public int ClipId(RectD r)
    {
        if (_clipIds.TryGetValue(r, out var id)) return id;
        _clipIds[r] = id = Clips.Count;
        Clips.Add(r);
        return id;
    }

    public int AddGroup(string name, (int Op, int Ctm, int Stays)? block = null)
    {
        GroupNames.Add(name);
        GroupBlocks.Add(block ?? (-1, 0, 0));
        return GroupNames.Count - 1;
    }

    /// <summary>The entry of an operator of a scope; −1 if none (binary search: a stream reports its objects in operator order).</summary>
    public int Find(int scope, int op)
    {
        if ((uint)scope >= (uint)ScopeEntries.Count) return -1;
        var list = ScopeEntries[scope];
        int lo = 0, hi = list.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var at = this[list[mid]].Op;
            if (at == op) return list[mid];
            if (at < op) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>The part of an entry that can be seen: its bounds within both clips.</summary>
    public RectD Visible(int index)
    {
        ref var e = ref this[index];
        return e.Bounds.Intersect(Clips[e.InnerClip]).Intersect(Clips[e.OuterClip]);
    }

    /// <summary>The object of an entry, at the place the content draws it.</summary>
    public ContentObject Make(int index)
    {
        ref var e = ref this[index];
        var image = e.Image >= 0 ? Images[e.Image] : null;
        var toPage = image?.ImageToPage ?? Matrix.Scale(e.Bounds.Width, e.Bounds.Height) * Matrix.Translate(e.Bounds.Left, e.Bounds.Top);
        var content = Contents[e.Scope];
        return new ContentObject(new ContentKey(Scopes[e.Scope], e.Op), e.Kind, toPage, e.Block, Matrices[e.BlockMatrix], Clips[e.OuterClip], Clips[e.InnerClip])
        {
            FormsToPage = FormsToPage[e.Scope],
            FormBoxes = FormBoxes[e.Scope],
            Ctm = Matrices[e.Ctm],
            StrokeReach = e.Reach,
            PixelWidth = image?.PixelWidth ?? 0,
            PixelHeight = image?.PixelHeight ?? 0,
            Inline = image?.Inline ?? false,
            ObjectNumber = image?.ObjectNumber,
            Source = image?.Source,
            AlsoClips = (e.Flags & AlsoClips) != 0,
            Group = e.Group >= 0 ? GroupNames[e.Group] : null,
            GroupSize = e.Group >= 0 ? GroupMembers(e.Group).Length : 0,
            GroupBlock = e.Group >= 0 ? GroupBlocks[e.Group].Op : -1,
            GroupMatrix = e.Group >= 0 ? Matrices[GroupBlocks[e.Group].Ctm] : Matrix.Identity,
            GroupStays = e.Group >= 0 ? Clips[GroupBlocks[e.Group].Stays] : RectD.Infinite,
            GroupOwnClip = e.Group >= 0 ? Clips[e.GroupOwnClip] : RectD.Infinite,
            OuterClipOutsideForms = Clips[e.OuterOpen],
            OwnClip = Clips[e.OwnClip],
            Shape = e.Kind == ContentKind.Path && e.ShapeStart >= 0 && content is not null
                ? new PathShape(content, e.ShapeStart, e.ShapeEnd, (e.Flags & Fills) != 0, (e.Flags & Strokes) != 0, (e.Flags & EvenOdd) != 0)
                : null,
        };
    }

    // ------------------------------------------------------------ After building: groups, images, grid

    private int[] _groupStart = [0], _groupMembers = [];
    private int[] _images = [];
    private RectD _gridBox;
    private int _columns, _rows;
    private int[] _cellStart = [0], _cellItems = [];
    private int[] _large = [];

    /// <summary>A cell list longer than this many cells puts an object into the list checked always (a line across the plan).</summary>
    private const int MaxCellsPerObject = 64;

    /// <summary>The entries of a group, in drawing order.</summary>
    public ReadOnlySpan<int> GroupMembers(int group) =>
        (uint)group < (uint)(_groupStart.Length - 1) ? _groupMembers.AsSpan(_groupStart[group], _groupStart[group + 1] - _groupStart[group]) : [];

    /// <summary>The entries of images, in drawing order.</summary>
    public ReadOnlySpan<int> ImageEntries => _images;

    /// <summary>Builds the lists of groups and images and the grid; called once, when all entries are in.</summary>
    public void Finish(CancellationToken cancel)
    {
        // groups as one array of members
        _groupStart = new int[GroupNames.Count + 1];
        for (var i = 0; i < Count; i++)
            if (this[i].Group >= 0) _groupStart[this[i].Group + 1]++;
        for (var g = 0; g < GroupNames.Count; g++) _groupStart[g + 1] += _groupStart[g];
        _groupMembers = new int[_groupStart[^1]];
        var fill = _groupStart[..^1];
        var images = new List<int>();
        for (var i = 0; i < Count; i++)
        {
            if (this[i].Group >= 0) _groupMembers[fill[this[i].Group]++] = i;
            if (this[i].Image >= 0) images.Add(i);
        }
        _images = [.. images];
        cancel.ThrowIfCancellationRequested();
        BuildGrid();
    }

    private void BuildGrid()
    {
        var box = RectD.Empty;   // the union of what can be seen (a hairline counts)
        for (var i = 0; i < Count; i++)
        {
            var visible = Visible(i);
            if (Valid(visible)) box = box == RectD.Empty ? visible : RectD.FromLtrb(Math.Min(box.Left, visible.Left), Math.Min(box.Top, visible.Top),
                Math.Max(box.Right, visible.Right), Math.Max(box.Bottom, visible.Bottom));
        }
        if (!(box.Width > 0) || !(box.Height > 0) || !double.IsFinite(box.Width) || !double.IsFinite(box.Height))
        {
            _gridBox = RectD.Empty;
            _large = [.. Enumerable.Range(0, Count).Where(i => Valid(Visible(i)))];
            return;
        }
        // about four objects per cell, at most 256 × 256 cells, in the shape of the box
        var cells = Math.Clamp(Count / 4, 1, 65_536);
        var aspect = box.Width / box.Height;
        _columns = Math.Clamp((int)Math.Round(Math.Sqrt(cells * aspect)), 1, 256);
        _rows = Math.Clamp((int)Math.Round((double)cells / _columns), 1, 256);
        _gridBox = box;
        var counts = new int[_columns * _rows + 1];
        var large = new List<int>();
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < Count; i++)
            {
                var visible = Visible(i);
                if (!Valid(visible) || Cells(visible) is not var (c0, r0, c1, r1)) continue;
                if ((long)(c1 - c0 + 1) * (r1 - r0 + 1) > MaxCellsPerObject)
                {
                    if (pass == 0) large.Add(i);
                    continue;
                }
                for (var r = r0; r <= r1; r++)
                    for (var c = c0; c <= c1; c++)
                        if (pass == 0) counts[r * _columns + c + 1]++;
                        else _cellItems[counts[r * _columns + c]++] = i;
            }
            if (pass == 1) break;
            for (var k = 1; k < counts.Length; k++) counts[k] += counts[k - 1];
            _cellStart = (int[])counts.Clone();
            _cellItems = new int[counts[^1]];
        }
        _large = [.. large];
    }

    /// <summary>Not inverted: a line clipped to no width can still be hit within the tolerance (<see cref="RectD.IsEmpty"/> would drop it).</summary>
    internal static bool Valid(RectD r) => r.Right >= r.Left && r.Bottom >= r.Top;

    private (int C0, int R0, int C1, int R1)? Cells(RectD r)
    {
        if (!r.Intersects(_gridBox)) return null;
        int Column(double x) => Math.Clamp((int)((x - _gridBox.Left) / _gridBox.Width * _columns), 0, _columns - 1);
        int Row(double y) => Math.Clamp((int)((y - _gridBox.Top) / _gridBox.Height * _rows), 0, _rows - 1);
        return (Column(Math.Max(r.Left, _gridBox.Left)), Row(Math.Max(r.Top, _gridBox.Top)), Column(Math.Min(r.Right, _gridBox.Right)), Row(Math.Min(r.Bottom, _gridBox.Bottom)));
    }

    /// <summary>Entries whose visible part (where the content draws them) may meet <paramref name="area"/>; an entry may come more than once.</summary>
    public void Near(RectD area, List<int> into)
    {
        into.AddRange(_large);
        if (_gridBox == RectD.Empty || Cells(area) is not var (c0, r0, c1, r1)) return;
        for (var r = r0; r <= r1; r++)
            for (var c = c0; c <= c1; c++)
                for (var k = _cellStart[r * _columns + c]; k < _cellStart[r * _columns + c + 1]; k++)
                    into.Add(_cellItems[k]);
    }
}
