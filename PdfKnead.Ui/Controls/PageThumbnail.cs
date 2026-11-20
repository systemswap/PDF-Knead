using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PdfKnead.Rendering;
using PdfKnead.ViewModels;
using SkiaSharp;

namespace PdfKnead.Controls;

/// <summary>
/// Thumbnail of a page in the sidebar: the coarse overview image from the
/// <see cref="DocumentRenderer"/>. Only visible thumbnails are requested; the list
/// is virtualised, so entries that are not visible do not exist at all.
/// </summary>
public sealed class PageThumbnail : Control
{
    public static readonly StyledProperty<PageViewModel?> PageProperty =
        AvaloniaProperty.Register<PageThumbnail, PageViewModel?>(nameof(Page));

    public static readonly StyledProperty<DocumentViewModel?> DocumentProperty =
        AvaloniaProperty.Register<PageThumbnail, DocumentViewModel?>(nameof(Document));

    /// <summary>All currently visible thumbnails per document, with one subscription to the renderer for all of them.</summary>
    private static readonly Dictionary<DocumentRenderer, Subscription> Visible = new(ReferenceEqualityComparer.Instance);

    private DocumentRenderer? _renderer;

    static PageThumbnail() => AffectsRender<PageThumbnail>(PageProperty, DocumentProperty);

    public PageViewModel? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public DocumentViewModel? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Register();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unregister();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty || change.Property == PageProperty)
        {
            Unregister();
            if (VisualRoot is not null) Register();
        }
    }

    private void Register()
    {
        if (Document?.Renderer is not { } renderer || Page is null) return;
        _renderer = renderer;
        if (!Visible.TryGetValue(renderer, out var subscription))
            Visible[renderer] = subscription = new Subscription(renderer);
        subscription.Add(this);
        _watched = Page;
        _watched.PropertyChanged += OnPageChanged;
    }

    private void Unregister()
    {
        if (_watched is not null) _watched.PropertyChanged -= OnPageChanged;
        _watched = null;
        if (_renderer is not { } renderer) return;
        if (Visible.TryGetValue(renderer, out var subscription) && subscription.Remove(this))
            Visible.Remove(renderer);
        _renderer = null;
    }

    // Only the page's elements changed (new item, same tile): redrawing is enough.
    private PageViewModel? _watched;

    private void OnPageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageViewModel.Item)) InvalidateVisual();
    }

    // Thumbnails stay unrotated: their frame has the page's format, and the rotation applies only to the main view.
    private TileKey Key(DocumentRenderer renderer) =>
        new(Page!.Key, renderer.OverviewLevel(Page.Key), 0, 0, 0);

    /// <summary>
    /// One subscription per renderer: collects finished tiles (background thread), then once per batch on
    /// the UI thread redraws only the affected thumbnails and requests once more, instead of
    /// every thumbnail receiving every finished tile of the document.
    /// </summary>
    private sealed class Subscription
    {
        private readonly DocumentRenderer _renderer;
        private readonly HashSet<PageThumbnail> _thumbnails = [];
        private readonly ConcurrentQueue<TileKey> _ready = new();
        private int _posted;          // 1 while a drain is pending
        private volatile bool _all;   // redraw everything (tiles discarded)
        private volatile bool _elements;   // display list of a foreign annotation finished: redraw the visible thumbnails
        private bool _requestPosted;

        public Subscription(DocumentRenderer renderer)
        {
            _renderer = renderer;
            renderer.TileReady += OnTileReady;
            renderer.Cleared += OnCleared;
            renderer.ElementListReady += OnElementListReady;
        }

        public void Add(PageThumbnail thumbnail)
        {
            _thumbnails.Add(thumbnail);
            PostRequest();   // request entries that become visible while scrolling together
        }

        /// <summary>Removes a thumbnail; returns true if none is visible any more (the subscription has then ended).</summary>
        public bool Remove(PageThumbnail thumbnail)
        {
            _thumbnails.Remove(thumbnail);
            if (_thumbnails.Count > 0) return false;
            _renderer.TileReady -= OnTileReady;
            _renderer.Cleared -= OnCleared;
            _renderer.ElementListReady -= OnElementListReady;
            _renderer.Request("vorschau", []);
            return true;
        }

        private void OnTileReady(TileKey key)
        {
            if (key.X != 0 || key.Y != 0 || key.Rotation != 0) return;
            _ready.Enqueue(key);
            Post();
        }

        private void OnElementListReady()
        {
            _elements = true;
            Post();
        }

        private void OnCleared()
        {
            _all = true;
            Post();
        }

        private void Post()
        {
            if (Interlocked.Exchange(ref _posted, 1) == 0)
                Dispatcher.UIThread.Post(Drain, DispatcherPriority.Background);
        }

        private void Drain()
        {
            Volatile.Write(ref _posted, 0);
            var all = _all;
            _all = false;
            var elements = _elements;
            _elements = false;
            HashSet<int>? pages = null;
            while (_ready.TryDequeue(out var key))
                if (key.Level == _renderer.OverviewLevel(key.Page)) (pages ??= []).Add(key.Page);
            if (all || elements || pages is not null)
                foreach (var thumbnail in _thumbnails)
                    if (thumbnail.Page is { } page && (all || elements || pages!.Contains(page.Key)))
                        thumbnail.InvalidateVisual();
            if (all) Request();
        }

        public void PostRequest()
        {
            if (_requestPosted) return;
            _requestPosted = true;
            Dispatcher.UIThread.Post(() =>
            {
                _requestPosted = false;
                Request();
            }, DispatcherPriority.Background);
        }

        private void Request()
        {
            if (_thumbnails.Count == 0) return;
            var keys = new List<TileKey>(_thumbnails.Count);
            foreach (var thumbnail in _thumbnails)
            {
                if (thumbnail.Page is null) continue;
                var key = thumbnail.Key(_renderer);
                if (_renderer.TryGetTile(key) is null) keys.Add(key);
            }
            keys.Sort((a, b) => a.Page.CompareTo(b.Page));
            _renderer.Request("vorschau", keys);
        }
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.White, rect);
        if (_renderer is not { } renderer || Page is null) return;
        if (renderer.TryGetTile(Key(renderer)) is { } image)
        {
            context.Custom(new SkiaImageDrawing(rect,
                [new SkiaImageDraw(image, SKRect.Create(image.Width, image.Height), new SKRect(0, 0, (float)rect.Width, (float)rect.Height))]));
            // Overlaid elements (not yet saved) on top
            if (Page.Item.OverlayElements.Count > 0 && Page.Page.Width > 0)
                context.Custom(new ElementDrawing(rect,
                    [new ElementLayer(Page.Page, Page.Item.OverlayElements, PageGeometry.PageToDevice(Page.Page, rect.Width / Page.Page.Width))],
                    renderer.Images, foreignLists: renderer.TryGetElementList, overprintPreview: renderer.OverprintPreview));
        }
        else if (Visible.TryGetValue(renderer, out var subscription))
        {
            subscription.PostRequest();   // missing (not there yet or evicted)
        }
    }
}
