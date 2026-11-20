using System.Collections.Concurrent;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using SkiaSharp;
using Matrix = PdfKnead.Core.Pdf.Graphics.Matrix;

namespace PdfKnead.Rendering;

/// <summary>
/// Tile of a page at a zoom level. <see cref="Page"/> is the key of the page (see <see cref="RenderPage"/>), not its position.
/// Level <c>k</c> means √2^k pixels per point (k = 0: 72 dpi, k = 1: ≈ 102 dpi …). <see cref="Rotation"/> is the additional rotation
/// of the view (0/90/180/270), not the /Rotate of the page.
/// </summary>
/// <param name="Page">Key of the page.</param>
/// <param name="Level">Zoom level.</param>
/// <param name="X">Tile column (in units of <see cref="DocumentRenderer.TileSize"/> pixels).</param>
/// <param name="Y">Tile row (in units of <see cref="DocumentRenderer.TileSize"/> pixels).</param>
/// <param name="Rotation">Additional view rotation in degrees, clockwise.</param>
public readonly record struct TileKey(int Page, int Level, int X, int Y, int Rotation)
{
    /// <summary>Resolution of the level in pixels per point.</summary>
    public double PixelsPerPoint => DocumentRenderer.Scale(Level);
}

/// <summary>
/// A page the renderer can draw: key (same key = same appearance), document and page. While editing, the pages come from several
/// files.
/// </summary>
/// <param name="Key">Key of the page; tiles are cached per key.</param>
/// <param name="Document">The document containing the page.</param>
/// <param name="Page">The page.</param>
/// <param name="TiledAnnotations">Foreign annotations drawn into the tiles (pages with many of them); when they change, the tiles
/// count as outdated. Used only with <see cref="DocumentRenderer.HideOwnElements"/>.</param>
public readonly record struct RenderPage(int Key, PdfDocument Document, PdfPage Page, IReadOnlyList<PdfDictionary>? TiledAnnotations = null);

/// <summary>
/// Renders tiles and thumbnails of a document in the background on several cores and keeps them in a cache. The display tells
/// with <see cref="Request"/> what it needs right now (most important first); everything else is cancelled. Finished work is
/// reported by <see cref="Changed"/> (from a background thread). Display lists, tiles and images are accounted in pools shared by
/// all documents (<see cref="CachePool"/>). Thread-safe.
/// </summary>
/// <remarks>
/// Owns its worker threads (below-normal priority); <see cref="Dispose"/> stops them. Before closing a document, call
/// <see cref="Dispose"/> or <see cref="SetPages"/> and then await <see cref="WaitUntilUnused"/>.
/// </remarks>
public sealed class DocumentRenderer : IDisposable, ICacheUser
{
    /// <summary>Edge length of a tile in pixels.</summary>
    public const int TileSize = 512;

    /// <summary>Edge length of the coarse overview of a page in pixels (instant preview, thumbnail).</summary>
    public const int OverviewSize = 512;

    private readonly ImageCache _images;

    /// <summary>Images of this document — also for placed elements (<see cref="ElementOverlay"/>); released on <see cref="Dispose"/>.</summary>
    public ImageCache Images => _images;

    private readonly CachePool _listPool;
    private readonly CachePool _tilePool;
    private long _listBytes;
    private long _lastActive = System.Diagnostics.Stopwatch.GetTimestamp();

    // Pages by key; replaced as a whole while editing (readers always see a complete state)
    private volatile Dictionary<int, RenderPage> _pages = [];
    private volatile HashSet<PdfDocument> _documents = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfDocument, int> _inUse = new(ReferenceEqualityComparer.Instance);

    // Display lists by page dictionary: rotating or moving a page does not rebuild them. In the same budget also the annotation
    // layers (key: AnnotationLayer) and foreign annotations drawn over the tiles (key: ElementAppearance)
    private readonly ConcurrentDictionary<object, Lazy<Task<DisplayList?>>> _lists = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<object> _listOrder = new();
    private readonly Dictionary<object, LinkedListNode<object>> _listNodes = new(ReferenceEqualityComparer.Instance);

    /// <summary>Foreign annotations drawn into the tiles of a page; a new state is a new object.</summary>
    private sealed class AnnotationLayer(IReadOnlyList<PdfDictionary> annotations)
    {
        public IReadOnlyList<PdfDictionary> Annotations { get; } = annotations;
    }

    // Layer per page key and the layer each tile was drawn with (outdated tiles are redrawn and stay visible until then); tiles to
    // renew that the display is currently showing
    private volatile Dictionary<int, AnnotationLayer> _layers = [];
    private readonly Dictionary<TileKey, AnnotationLayer> _tileLayers = [];
    private readonly HashSet<TileKey> _refresh = [];

    private readonly Lock _gate = new();
    private readonly Dictionary<TileKey, SKImage> _tiles = [];
    private readonly LinkedList<TileKey> _tileOrder = new();
    private readonly Dictionary<TileKey, LinkedListNode<TileKey>> _tileNodes = [];
    private long _tileBytes;
    private List<TileKey> _wanted = [];
    private int _nextWanted;                // processed up to here (instead of RemoveAt(0) per tile)
    // Evicted tiles: dispose only after a grace period — the render thread of the display might be drawing them at that very
    // moment. Without disposal only the GC would hold them (native memory, ~1 MB per tile, which it does not see).
    private readonly Queue<(SKImage Image, long Since)> _retired = new();
    private static readonly long RetireDelay = System.Diagnostics.Stopwatch.Frequency * 2;
    private readonly Dictionary<string, IReadOnlyList<TileKey>> _channels = [];
    private readonly Dictionary<TileKey, CancellationTokenSource> _running = [];
    private readonly SemaphoreSlim _work = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread[] _workers;

    /// <summary>Creates a renderer for all pages of a document.</summary>
    /// <param name="doc">Document whose pages are used with their index as key (no editing).</param>
    /// <param name="workers">Number of worker threads; default: number of cores minus one (at least one).</param>
    /// <param name="listBudgetBytes">Own limit for display lists; default: the shared pool <see cref="CachePool.Lists"/>.</param>
    /// <param name="tileBudgetBytes">Own limit for tiles; default: the shared pool <see cref="CachePool.Tiles"/>.</param>
    public DocumentRenderer(PdfDocument doc, int? workers = null, long? listBudgetBytes = null, long? tileBudgetBytes = null)
        : this(workers, listBudgetBytes, tileBudgetBytes) =>
        SetPages(doc.Pages.Select(p => new RenderPage(p.Index, doc, p)));

    /// <summary>Creates a renderer without pages; they are set with <see cref="SetPages"/>.</summary>
    /// <param name="workers">Number of worker threads; default: number of cores minus one (at least one).</param>
    /// <param name="listBudgetBytes">Own limit for display lists; default: the shared pool <see cref="CachePool.Lists"/>.</param>
    /// <param name="tileBudgetBytes">Own limit for tiles; default: the shared pool <see cref="CachePool.Tiles"/>.</param>
    public DocumentRenderer(int? workers = null, long? listBudgetBytes = null, long? tileBudgetBytes = null)
    {
        // Pools shared by all documents; own ones only if a limit is explicitly requested (tests)
        _listPool = listBudgetBytes is { } lb ? new CachePool("Zeichenlisten", lb) : CachePool.Lists;
        _tilePool = tileBudgetBytes is { } tb ? new CachePool("Kacheln", tb) : CachePool.Tiles;
        _listPool.Register(this);
        _tilePool.Register(this);
        _images = new ImageCache(CachePool.Images);
        var count = workers ?? Math.Max(1, Environment.ProcessorCount - 1);
        _workers = new Thread[count];
        for (var i = 0; i < count; i++)
        {
            _workers[i] = new Thread(Work) { IsBackground = true, Name = $"Kacheln {i + 1}", Priority = ThreadPriority.BelowNormal };
            _workers[i].Start();
        }
    }

    /// <summary>Something new is ready — redraw the display (raised from a background thread).</summary>
    public event Action? Changed;

    /// <summary>This tile is ready (raised from a background thread) — for requesters that care only about their own tiles.</summary>
    public event Action<TileKey>? TileReady;

    /// <summary>All tiles were discarded (e.g. "enhance thin lines" toggled).</summary>
    public event Action? Cleared;

    /// <summary>
    /// Sets the pages anew (after editing). Tiles stay valid per key; display lists of pages that no longer occur are discarded, and
    /// if a file is no longer used, the image cache is cleared. Running work for removed pages or changed layers is cancelled.
    /// </summary>
    /// <param name="pages">The pages with their keys.</param>
    public void SetPages(IEnumerable<RenderPage> pages)
    {
        var map = new Dictionary<int, RenderPage>();
        var documents = new HashSet<PdfDocument>(ReferenceEqualityComparer.Instance);
        var dicts = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var oldLayers = _layers;
        var layers = new Dictionary<int, AnnotationLayer>();
        foreach (var page in pages)
        {
            map[page.Key] = page;
            documents.Add(page.Document);
            dicts.Add(page.Page.Dictionary);
            // Same annotations: same layer, the tiles stay valid
            if (HideOwnElements && page.TiledAnnotations is { } tiled)
                layers[page.Key] = oldLayers.TryGetValue(page.Key, out var layer) && SameAnnotations(layer.Annotations, tiled) ? layer : new AnnotationLayer(tiled);
        }
        var keptLayers = new HashSet<AnnotationLayer>(layers.Values, ReferenceEqualityComparer.Instance);
        var removedDocument = _documents.Any(d => !documents.Contains(d));
        lock (_gate)
        {
            _pages = map;
            _documents = documents;
            _layers = layers;
            foreach (var (key, cts) in _running)
                if (!map.ContainsKey(key.Page) || !ReferenceEquals(layers.GetValueOrDefault(key.Page), oldLayers.GetValueOrDefault(key.Page))) cts.Cancel();
        }
        foreach (var key in _lists.Keys)
        {
            var keep = key switch
            {
                PdfDictionary dict => dicts.Contains(dict),
                AnnotationLayer layer => keptLayers.Contains(layer),
                ElementAppearance shape => documents.Contains(shape.Source),   // foreign annotations over the tiles
                _ => false,
            };
            if (keep || !_lists.TryRemove(key, out var removed)) continue;
            lock (_listOrder)
                if (_listNodes.Remove(key, out var node)) _listOrder.Remove(node);
            if (removed.IsValueCreated && removed.Value.IsCompletedSuccessfully && removed.Value.Result is { } l) ForgetList(l);
        }
        if (removedDocument) _images.Clear();
    }

    private static bool SameAnnotations(IReadOnlyList<PdfDictionary> a, IReadOnlyList<PdfDictionary> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    /// <summary>Tile present and drawn with the current annotation layer (only under _gate).</summary>
    private bool IsCurrent(TileKey key) =>
        _tiles.ContainsKey(key) && ReferenceEquals(_tileLayers.GetValueOrDefault(key), _layers.GetValueOrDefault(key.Page));

    /// <summary>Looks up the page for a key.</summary>
    /// <param name="key">Key of the page.</param>
    /// <param name="page">The page, if found.</param>
    /// <returns>False if the page does not exist (any more).</returns>
    public bool TryGetPage(int key, out RenderPage page) => _pages.TryGetValue(key, out page);

    /// <summary>
    /// Marks a document as in use (background work that reads its data).
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>A handle to dispose when done; null if the document no longer belongs to the pages — then read nothing from it any
    /// more, it is about to be closed.</returns>
    public IDisposable? Use(PdfDocument document)
    {
        lock (_inUse) _inUse[document] = _inUse.GetValueOrDefault(document) + 1;
        if (_documents.Contains(document)) return new Release(this, document);
        Unuse(document);
        return null;
    }

    private void Unuse(PdfDocument document)
    {
        lock (_inUse)
            if (--_inUse[document] == 0) _inUse.Remove(document);
    }

    private sealed class Release(DocumentRenderer owner, PdfDocument document) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Unuse(document);
        }
    }

    /// <summary>
    /// Waits until no background work reads from these documents any more (before closing). Without a time limit: disposing while
    /// reading is still going on would terminate the program (reading from a file that is already unmapped) — better a file stays
    /// open a little longer. Call <see cref="Dispose"/> or <see cref="SetPages"/> first so that running work is cancelled.
    /// </summary>
    /// <param name="documents">The documents about to be closed.</param>
    /// <returns>A task that completes when none of them is in use.</returns>
    public async Task WaitUntilUnused(IReadOnlyCollection<PdfDocument> documents)
    {
        for (var delay = 20; ; delay = Math.Min(delay * 2, 500))
        {
            lock (_inUse)
                if (documents.All(d => !_inUse.ContainsKey(d))) return;
            await Task.Delay(delay).ConfigureAwait(false);
        }
    }

    private volatile bool _enhanceThinLines = true;

    /// <summary>Do not draw the file's own editable elements into the tiles — the UI draws them as elements on top
    /// (<see cref="Core.Pdf.Editing.ElementStore"/>). Set only on creation.</summary>
    public bool HideOwnElements { get; init; }

    /// <summary>
    /// Draw strokes thinner than one pixel as hairlines. Toggling discards all tiles (they look different then) and raises
    /// <see cref="Cleared"/> and <see cref="Changed"/>.
    /// </summary>
    public bool EnhanceThinLines
    {
        get => _enhanceThinLines;
        set
        {
            if (_enhanceThinLines == value) return;
            _enhanceThinLines = value;
            DropAllTiles();
        }
    }

    private volatile bool _overprintPreview;

    /// <summary>
    /// Overprint preview: where overprinting applies, the colours mix as if printed on top of each other. Toggling discards all tiles
    /// and raises <see cref="Cleared"/> and <see cref="Changed"/>.
    /// </summary>
    public bool OverprintPreview
    {
        get => _overprintPreview;
        set
        {
            if (_overprintPreview == value) return;
            _overprintPreview = value;
            DropAllTiles();
        }
    }

    private double _noZoomPixelsPerPoint;

    /// <summary>
    /// Pixels per point at a zoom of 100 % on this screen (0 = annotations with NoZoom scale like the page, as in print and export):
    /// above 100 % they keep their size (ISO 32000-2, 12.5.3; docs/FEHLERGEDAECHTNIS.md, No. 576). Changing it discards all tiles.
    /// </summary>
    public double NoZoomPixelsPerPoint
    {
        get => _noZoomPixelsPerPoint;
        set
        {
            if (Math.Abs(_noZoomPixelsPerPoint - value) < 1e-9) return;
            _noZoomPixelsPerPoint = value;
            DropAllTiles();
        }
    }

    /// <summary>1 / zoom of a level for annotations with NoZoom; 1 at or below 100 % and when off (they then scale with the page).</summary>
    public static double NoZoomScale(double noZoomPixelsPerPoint, double pixelsPerPoint) =>
        noZoomPixelsPerPoint > 0 && pixelsPerPoint > noZoomPixelsPerPoint ? noZoomPixelsPerPoint / pixelsPerPoint : 1;

    /// <summary>
    /// The NoZoom scale the tiles of a view at <paramref name="pixelsPerPoint"/> are drawn with: that of its level (rounded up, as
    /// <see cref="LevelFor"/>). The view measures and draws its own elements with it, so that hit areas, frames and own notes match the
    /// annotations drawn into the tiles (docs/FEHLERGEDAECHTNIS.md, No. 589; before, the view used the exact zoom and the tiles the level —
    /// up to 29 % apart).
    /// </summary>
    public static double NoZoomScaleAt(double noZoomPixelsPerPoint, double pixelsPerPoint) =>
        NoZoomScale(noZoomPixelsPerPoint, Scale(LevelFor(pixelsPerPoint)));

    /// <summary>Discards all tiles (a display option has changed) and reports it.</summary>
    private void DropAllTiles()
    {
        lock (_gate)
        {
            foreach (var cts in _running.Values) cts.Cancel();
            foreach (var image in _tiles.Values) Retire(image);
            _tiles.Clear();
            _tileLayers.Clear();
            _refresh.Clear();
            _tileNodes.Clear();
            _tileOrder.Clear();
            _tilePool.Add(-_tileBytes);
            _tileBytes = 0;
        }
        Cleared?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Nothing requested and nothing in progress (for tests and measurements).</summary>
    public bool IsIdle
    {
        get
        {
            lock (_gate) return _nextWanted >= _wanted.Count && _running.Count == 0 && Volatile.Read(ref _pendingElementLists) == 0;
        }
    }

    /// <summary>Pixels per point of a zoom level: √2^level.</summary>
    /// <param name="level">Zoom level.</param>
    /// <returns>Pixels per point.</returns>
    public static double Scale(int level) => Math.Pow(Math.Sqrt(2), level);

    /// <summary>Smallest level that is at least as sharp as <paramref name="pixelsPerPoint"/>.</summary>
    /// <param name="pixelsPerPoint">Required resolution (values below 0.001 count as 0.001).</param>
    /// <returns>The zoom level.</returns>
    public static int LevelFor(double pixelsPerPoint) =>
        (int)Math.Ceiling(Math.Log(Math.Max(pixelsPerPoint, 1e-3)) / Math.Log(Math.Sqrt(2)) - 1e-9);

    /// <summary>Level of the coarse overview: the longer page edge fits into <see cref="OverviewSize"/>.</summary>
    /// <param name="page">Key of the page.</param>
    /// <returns>The level; 0 for an unknown page.</returns>
    public int OverviewLevel(int page)
    {
        if (!_pages.TryGetValue(page, out var entry)) return 0;
        var p = entry.Page;
        var longest = Math.Max(p.Width, p.Height);
        return LevelFor(OverviewSize / longest) - 1;
    }

    /// <summary>Tile size in pixels: at most <see cref="TileSize"/>, smaller at the right and bottom edge of the page.</summary>
    /// <param name="key">The tile.</param>
    /// <returns>Width and height in pixels (not positive for tiles outside the page).</returns>
    public (int Width, int Height) TilePixelSize(TileKey key)
    {
        var (w, h) = SizeAt(key.Page, key.Level, key.Rotation);
        return (Math.Min(TileSize, w - key.X * TileSize), Math.Min(TileSize, h - key.Y * TileSize));
    }

    /// <summary>Page size in pixels at a level (after the view rotation).</summary>
    /// <param name="page">Key of the page.</param>
    /// <param name="level">Zoom level.</param>
    /// <param name="rotation">Additional view rotation in degrees.</param>
    /// <returns>Width and height in pixels; (0, 0) for an unknown page.</returns>
    public (int Width, int Height) SizeAt(int page, int level, int rotation)
    {
        if (!_pages.TryGetValue(page, out var entry)) return (0, 0);
        var p = entry.Page;
        var s = Scale(level);
        var (w, h) = rotation is 90 or 270 ? (p.Height, p.Width) : (p.Width, p.Height);
        return ((int)Math.Ceiling(w * s), (int)Math.Ceiling(h * s));
    }

    // ------------------------------------------------------------ Access

    /// <summary>Returns a finished tile without waiting; an outdated tile is returned as well and queued for redrawing.</summary>
    /// <param name="key">The tile.</param>
    /// <returns>The tile image, or null if it is not ready; valid for at least the grace period after eviction.</returns>
    public SKImage? TryGetTile(TileKey key)
    {
        // Only what is visible gets drawn: the document is active
        _lastActive = System.Diagnostics.Stopwatch.GetTimestamp();
        SKImage? image;
        lock (_gate)
        {
            if (!_tiles.TryGetValue(key, out image)) return null;
            Touch(key);
            // Drawn with an old annotation layer: stays visible (no flicker) and is redrawn
            if (IsCurrent(key) || _running.ContainsKey(key) || !_refresh.Add(key)) return image;
            _wanted.Add(key);
        }
        if (_work.CurrentCount == 0) _work.Release();
        return image;
    }

    /// <summary>Order of the requesters: the main view before the page sidebar.</summary>
    private static readonly string[] ChannelOrder = ["ansicht", "vorschau"];

    /// <summary>
    /// What a requester (<c>"ansicht"</c>, <c>"vorschau"</c>, others after them) needs now, most important first. Replaces its previous
    /// list; running work that nobody needs any more is cancelled.
    /// </summary>
    /// <param name="channel">Name of the requester.</param>
    /// <param name="keys">The tiles needed, most important first.</param>
    public void Request(string channel, IReadOnlyList<TileKey> keys)
    {
        int open;
        // Whoever wants to see something right now is active: releases shared memory last
        if (keys.Count > 0) Interlocked.Exchange(ref _lastActive, System.Diagnostics.Stopwatch.GetTimestamp());
        lock (_gate)
        {
            // The display calls this on every frame: unchanged, nothing open and every tile present or in progress means nothing to do
            // (evicted or discarded tiles must be redone)
            if (_channels.TryGetValue(channel, out var previous) && Same(previous, keys) && _nextWanted >= _wanted.Count &&
                AllPresentOrRunning(keys))
                return;
            _channels[channel] = keys;
            var seen = new HashSet<TileKey>();
            var wanted = new List<TileKey>();
            foreach (var name in ChannelOrder)
                if (_channels.TryGetValue(name, out var list))
                    foreach (var key in list)
                        if (seen.Add(key)) wanted.Add(key);
            foreach (var (name, list) in _channels)
                if (Array.IndexOf(ChannelOrder, name) < 0)
                    foreach (var key in list)
                        if (seen.Add(key)) wanted.Add(key);
            // Outdated but visible tiles (new annotation layer) last
            _refresh.RemoveWhere(k => !_pages.ContainsKey(k.Page) || !_tiles.ContainsKey(k) || IsCurrent(k));
            foreach (var key in _refresh)
                if (seen.Add(key)) wanted.Add(key);
            foreach (var (key, cts) in _running)
                if (!seen.Contains(key)) cts.Cancel();
            wanted.RemoveAll(k => IsCurrent(k) || _running.ContainsKey(k));
            _wanted = wanted;
            _nextWanted = 0;
            DisposeRetired();
            open = wanted.Count;
        }
        // One signal per worker, at most as many as there is work (signals must not accumulate)
        var pending = Math.Min(_workers.Length, open) - _work.CurrentCount;
        for (var i = 0; i < pending; i++) _work.Release();
    }

    private bool AllPresentOrRunning(IReadOnlyList<TileKey> keys)
    {
        for (var i = 0; i < keys.Count; i++)
            if (!IsCurrent(keys[i]) && !_running.ContainsKey(keys[i])) return false;
        return true;
    }

    private static bool Same(IReadOnlyList<TileKey> a, IReadOnlyList<TileKey> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Returns an already finished display list without building it or moving it to the front of the cache (search).</summary>
    /// <param name="page">Key of the page.</param>
    /// <returns>The display list, or null if it is not ready or the page is unreadable.</returns>
    public DisplayList? TryGetDisplayList(int page) =>
        _pages.TryGetValue(page, out var entry) && _lists.TryGetValue(entry.Page.Dictionary, out var lazy) &&
        lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully ? lazy.Value.Result : null;

    /// <summary>Display list of a page (built in the background, cached).</summary>
    /// <param name="page">Key of the page.</param>
    /// <returns>A task with the display list; null for unknown or unreadable pages.</returns>
    public Task<DisplayList?> GetDisplayList(int page)
    {
        if (!_pages.TryGetValue(page, out var entry)) return Task.FromResult<DisplayList?>(null);
        return GetList(entry.Page.Dictionary, entry, () => PageInterpreter.Build(entry.Document, entry.Page, _shutdown.Token, HideOwnElements));
    }

    /// <summary>Display list of the annotation layer of a page (in the same cache as the pages).</summary>
    private Task<DisplayList?> GetLayerList(RenderPage entry, AnnotationLayer layer) =>
        GetList(layer, entry, () => PageInterpreter.BuildAnnotations(entry.Document, entry.Page, layer.Annotations, _shutdown.Token));

    private int _pendingElementLists;   // display lists for drawing over the tiles still being built (counted by IsIdle)

    /// <summary>A display list for drawing over the tiles is ready (raised from a background thread) — redraw.</summary>
    public event Action? ElementListReady;

    /// <summary>
    /// Display list of a foreign annotation (its form, <see cref="ElementAppearance.Shape"/>) for drawing over the tiles: built in
    /// the background like the pages (its appearance comes from the file and can be arbitrarily expensive — on the render thread it
    /// would freeze the UI), in the same cache.
    /// </summary>
    /// <param name="page">The page the annotation belongs to.</param>
    /// <param name="shape">The appearance of the annotation.</param>
    /// <returns>The display list; null while it is not ready (then <see cref="ElementListReady"/> follows) or if the document is not
    /// one of the renderer's documents.</returns>
    public DisplayList? TryGetElementList(PdfPage page, ElementAppearance shape)
    {
        if (!_documents.Contains(shape.Source)) return null;
        var task = GetList(shape, new RenderPage(-1, shape.Source, page), () => PageInterpreter.BuildAnnotations(shape.Source, page, [shape.Annotation], _shutdown.Token, PageInterpreter.MaxAnnotationOperations),
            () =>
            {
                ElementListReady?.Invoke();
                Changed?.Invoke();   // the view redraws on Changed
            });
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    private Task<DisplayList?> GetList(object key, RenderPage entry, Func<DisplayList> build, Action? done = null)
    {
        if (!_lists.TryGetValue(key, out var lazy))
            lazy = _lists.GetOrAdd(key, _ => new Lazy<Task<DisplayList?>>(() =>
            {
                var task = Task.Run(() => BuildList(key, entry, build));
                if (done is not null)
                {
                    Interlocked.Increment(ref _pendingElementLists);
                    task.ContinueWith(_ =>
                    {
                        Interlocked.Decrement(ref _pendingElementLists);
                        done();
                    }, TaskScheduler.Default);
                }
                return task;
            }));
        lock (_listOrder)
        {
            // Most recently used to the front — without searching and without a new node (called per tile)
            if (_listNodes.TryGetValue(key, out var node)) _listOrder.Remove(node);
            else _listNodes[key] = node = new LinkedListNode<object>(key);
            _listOrder.AddFirst(node);
        }
        return lazy.Value;
    }

    private DisplayList? BuildList(object key, RenderPage entry, Func<DisplayList> build)
    {
        using var use = Use(entry.Document);
        if (use is null)
        {
            // Document no longer part of the pages: remember nothing (undo may bring it back)
            _lists.TryRemove(key, out _);
            return null;
        }
        try
        {
            var list = build();
            Interlocked.Add(ref _listBytes, list.EstimatedBytes);
            _listPool.Add(list.EstimatedBytes);
            _listPool.Enforce(this);
            return list;
        }
        catch (Exception ex) when (ex is PdfException or OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Crafted or broken page (overflow, wrong index …): this page stays empty, the program carries on
            Broken("Seite nicht lesbar", ex);
            return null;
        }
    }

    private static int _brokenLogged;

    /// <summary>Writes an error caused by a file to the log — at most 20 per program run (a crafted file produces thousands).</summary>
    internal static void Broken(string what, Exception ex)
    {
        if (Interlocked.Increment(ref _brokenLogged) <= 20) Core.Platform.AppLog.Write(what + " (abgefangen)", ex.ToString());
    }

    /// <summary>A display list has been removed from the cache: remove it from the pool accounting.</summary>
    private void ForgetList(DisplayList list)
    {
        Interlocked.Add(ref _listBytes, -list.EstimatedBytes);
        _listPool.Add(-list.EstimatedBytes);
        GlyphMasks.Release(list);
    }

    /// <summary>Discards the display lists of the pages unused for the longest time until about <paramref name="bytes"/> are free.</summary>
    private void FreeLists(long bytes)
    {
        long freed = 0;
        lock (_listOrder)
        {
            // The most recently used one (needed right now or being built) stays
            for (var node = _listOrder.Last; node is not null && node != _listOrder.First && freed < bytes;)
            {
                var previous = node.Previous;
                if (_lists.TryGetValue(node.Value, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompleted &&
                    _lists.TryRemove(node.Value, out _))
                {
                    _listOrder.Remove(node);
                    _listNodes.Remove(node.Value);
                    if (lazy.Value.IsCompletedSuccessfully && lazy.Value.Result is { } l)
                    {
                        freed += l.EstimatedBytes;
                        ForgetList(l);
                    }
                }
                node = previous;
            }
        }
    }

    // ------------------------------------------------------------ Pools (shared with the other documents)

    /// <inheritdoc/>
    public long LastActive => Interlocked.Read(ref _lastActive);

    /// <inheritdoc/>
    public void Free(CachePool pool, long bytes)
    {
        if (ReferenceEquals(pool, _listPool)) FreeLists(bytes);
        else if (ReferenceEquals(pool, _tilePool)) FreeTiles(bytes);
    }

    // ------------------------------------------------------------ Workers

    private void Work()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                _work.Wait(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            TileKey key;
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_nextWanted >= _wanted.Count) continue;
                key = _wanted[_nextWanted++];
                if (IsCurrent(key) || _running.ContainsKey(key)) continue;
                cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                _running[key] = cts;
            }

            try
            {
                var (image, layer) = RenderTile(key, cts.Token);
                if (image is not null)
                {
                    lock (_gate)
                    {
                        if (_tiles.Remove(key, out var old))
                        {
                            _tileBytes -= Bytes(old);
                            _tilePool.Add(-Bytes(old));
                            Retire(old);
                        }
                        _tiles[key] = image;
                        if (layer is null) _tileLayers.Remove(key);
                        else _tileLayers[key] = layer;
                        _refresh.Remove(key);
                        _tileBytes += Bytes(image);
                        _tilePool.Add(Bytes(image));
                        Touch(key);
                        DisposeRetired();
                    }
                    // Outside the own lock: other documents take theirs while releasing
                    _tilePool.Enforce(this);
                    TileReady?.Invoke(key);
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                // no longer needed
            }
            catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(e => e is OperationCanceledException))
            {
                // no longer needed (parallel drawing)
            }
            catch (Exception ex)
            {
                // Own threads: an exception here would terminate the whole program
                Broken("Kachel nicht gezeichnet", ex);
            }
            finally
            {
                lock (_gate) _running.Remove(key);
                cts.Dispose();
                // More work? Then carry on right away
                bool more;
                lock (_gate) more = _nextWanted < _wanted.Count;
                if (more) _work.Release();
            }
        }
    }

    /// <summary>Draws a tile: page content, then the annotation layer on top (returned with the tile).</summary>
    private (SKImage? Image, AnnotationLayer? Layer) RenderTile(TileKey key, CancellationToken cancel)
    {
        if (!_pages.TryGetValue(key.Page, out var entry)) return default;
        var layer = _layers.GetValueOrDefault(key.Page);
        using var use = Use(entry.Document);
        if (use is null) return default;
        var list = GetDisplayList(key.Page).GetAwaiter().GetResult();
        if (list is null) return default;
        var annotations = layer is null ? null : GetLayerList(entry, layer).GetAwaiter().GetResult();
        cancel.ThrowIfCancellationRequested();

        var (w, h) = TilePixelSize(key);
        if (w <= 0 || h <= 0) return default;
        var doc = entry.Document;
        var page = entry.Page;
        var pageToDevice = PageToDevice(page, key.PixelsPerPoint, key.Rotation);
        var noZoom = NoZoomScale(_noZoomPixelsPerPoint, key.PixelsPerPoint);
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SkiaRenderer.Paper);
        var area = SKRectI.Create(key.X * TileSize, key.Y * TileSize, w, h);

        // Much content in one tile (coarse overview of a CAD drawing): split into pieces with about equal work and draw them in
        // parallel. Equal-sized strips do not help — in plans the content clusters (measured: 80 % of the entries in an eighth of
        // the area).
        var pieces = Split(list, pageToDevice, area, depth: 0);
        if (pieces.Count <= 1)
        {
            SkiaRenderer.RenderTile(canvas, list, doc, pageToDevice, area, _images, cancel, EnhanceThinLines, OverprintPreview, noZoom);
            if (annotations is not null) SkiaRenderer.RenderTile(canvas, annotations, doc, pageToDevice, area, _images, cancel, EnhanceThinLines, OverprintPreview, noZoom);
            return (surface.Snapshot(), layer);
        }

        var parts = new SKImage?[pieces.Count];
        Parallel.For(0, pieces.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            var piece = pieces[i];
            using var pieceSurface = SKSurface.Create(new SKImageInfo(piece.Width, piece.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            pieceSurface.Canvas.Clear(SkiaRenderer.Paper);
            SkiaRenderer.RenderTile(pieceSurface.Canvas, list, doc, pageToDevice, piece, _images, cancel, EnhanceThinLines, OverprintPreview, noZoom);
            parts[i] = pieceSurface.Snapshot();
        });
        for (var i = 0; i < pieces.Count; i++)
        {
            if (parts[i] is not { } part) continue;
            canvas.DrawImage(part, pieces[i].Left - area.Left, pieces[i].Top - area.Top);
            part.Dispose();
        }
        if (annotations is not null) SkiaRenderer.RenderTile(canvas, annotations, doc, pageToDevice, area, _images, cancel, EnhanceThinLines, OverprintPreview, noZoom);
        return (surface.Snapshot(), layer);
    }

    /// <summary>Number of entries above which a piece is split further.</summary>
    private const int PieceItems = 40_000;

    /// <summary>
    /// Halves the area (along the longer side) until each piece has at most <see cref="PieceItems"/> entries or becomes too small
    /// (depth 8, under 48 pixels). Empty halves are kept — they are cheap.
    /// </summary>
    private static List<SKRectI> Split(DisplayList list, Matrix pageToDevice, SKRectI area, int depth)
    {
        var hits = list.Index.EstimateCount(Visible(pageToDevice, area));
        if (hits <= PieceItems || depth >= 8 || (area.Width < 48 && area.Height < 48)) return [area];
        SKRectI a, b;
        if (area.Width >= area.Height)
        {
            var mid = area.Left + area.Width / 2;
            a = new SKRectI(area.Left, area.Top, mid, area.Bottom);
            b = new SKRectI(mid, area.Top, area.Right, area.Bottom);
        }
        else
        {
            var mid = area.Top + area.Height / 2;
            a = new SKRectI(area.Left, area.Top, area.Right, mid);
            b = new SKRectI(area.Left, mid, area.Right, area.Bottom);
        }
        return [.. Split(list, pageToDevice, a, depth + 1), .. Split(list, pageToDevice, b, depth + 1)];
    }

    private static RectD Visible(Matrix pageToDevice, SKRectI area) =>
        pageToDevice.Invert().TransformBounds(RectD.FromLtrb(area.Left, area.Top, area.Right, area.Bottom));

    /// <summary>Page → device with an additional view rotation (clockwise).</summary>
    /// <param name="page">The page.</param>
    /// <param name="pixelsPerPoint">Resolution (1 = 72 dpi).</param>
    /// <param name="rotation">View rotation in degrees (0, 90, 180, 270; other values count as 0).</param>
    /// <returns>The transformation matrix.</returns>
    public static Matrix PageToDevice(PdfPage page, double pixelsPerPoint, int rotation)
    {
        var m = PageGeometry.PageToDevice(page, pixelsPerPoint);
        var (w, h) = PageGeometry.DeviceSize(page, pixelsPerPoint);
        return rotation switch
        {
            90 => m * new Matrix(0, 1, -1, 0, h, 0),
            180 => m * new Matrix(-1, 0, 0, -1, w, h),
            270 => m * new Matrix(0, -1, 1, 0, 0, w),
            _ => m,
        };
    }

    /// <summary>Moves the most recently used tile to the front — relinking the existing node instead of creating a new one.</summary>
    private void Touch(TileKey key)
    {
        if (_tileNodes.TryGetValue(key, out var node))
        {
            if (node != _tileOrder.First)
            {
                _tileOrder.Remove(node);
                _tileOrder.AddFirst(node);
            }
        }
        else
        {
            _tileNodes[key] = _tileOrder.AddFirst(key);
        }
    }

    private static long Bytes(SKImage image) => (long)image.Width * image.Height * 4;

    /// <summary>Disposes later (only under _gate).</summary>
    private void Retire(SKImage image) => _retired.Enqueue((image, System.Diagnostics.Stopwatch.GetTimestamp()));

    /// <summary>Disposes tiles whose grace period has expired (only under _gate).</summary>
    private void DisposeRetired()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        while (_retired.TryPeek(out var entry) && now - entry.Since > RetireDelay)
        {
            _retired.Dequeue();
            entry.Image.Dispose();
        }
    }

    /// <summary>
    /// Discards the oldest tiles until about <paramref name="bytes"/> are free; overviews are kept preferentially (they are the
    /// fallback) — they go only when the pool holds twice its limit.
    /// </summary>
    private void FreeTiles(long bytes)
    {
        lock (_gate)
        {
            long freed = 0;
            var overviewsToo = _tilePool.Used > _tilePool.Limit * 2;
            for (var node = _tileOrder.Last; node is not null && freed < bytes;)
            {
                var previous = node.Previous;
                var key = node.Value;
                if (overviewsToo || key.Level > OverviewLevel(key.Page))
                {
                    if (_tiles.Remove(key, out var image))
                    {
                        _tileBytes -= Bytes(image);
                        _tilePool.Add(-Bytes(image));
                        freed += Bytes(image);
                        Retire(image);   // not immediately: the display might be drawing the image right now
                    }
                    _tileLayers.Remove(key);
                    _tileNodes.Remove(key);
                    _tileOrder.Remove(node);
                }
                node = previous;
            }
            DisposeRetired();
        }
    }

    /// <summary>Stops the workers, cancels running work and releases tiles, lists and images (tiles after a grace period).</summary>
    public void Dispose()
    {
        _shutdown.Cancel();
        _work.Release(_workers.Length);
        List<SKImage> rest;
        lock (_gate)
        {
            foreach (var cts in _running.Values) cts.Cancel();
            rest = [.. _tiles.Values];
            foreach (var (image, _) in _retired) rest.Add(image);
            _tiles.Clear();
            _retired.Clear();
            _tilePool.Add(-_tileBytes);
            _tileBytes = 0;
        }
        // Remove from the shared pools
        foreach (var key in _lists.Keys)
            if (_lists.TryRemove(key, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully && lazy.Value.Result is { } l)
                ForgetList(l);
        _listPool.Add(-Interlocked.Exchange(ref _listBytes, 0));   // remainder (lists that were still being built)
        _listPool.Unregister(this);
        _tilePool.Unregister(this);
        _images.Detach();
        // Dispose only after the grace period (a last frame of the display might still be in flight)
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => { foreach (var image in rest) image.Dispose(); }, TaskScheduler.Default);
    }
}
