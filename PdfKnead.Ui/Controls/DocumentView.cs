using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Rendering;
using PdfKnead.ViewModels;
using SkiaSharp;
using PdfMatrix = PdfKnead.Core.Pdf.Graphics.Matrix;

namespace PdfKnead.Controls;

/// <summary>
/// Continuous view of a document: pages one below the other, drawn from tiles of the
/// <see cref="DocumentRenderer"/>. First the coarse overview image of each page appears (immediately,
/// scaled by the graphics card), then the sharp tiles on top as soon as they are finished, never
/// half-finished. Panning and zooming only move and scale finished tiles.
/// </summary>
public sealed partial class DocumentView : Control
{
    /// <summary>Gap between pages in user interface units.</summary>
    public const double Gap = 16;
    /// <summary>Margin around the pages in user interface units.</summary>
    public const double PageMargin = 24;

    /// <summary>User interface units per point at 100 %: 96 dpi screen, 72 points per inch.</summary>
    public const double DipPerPoint = 96.0 / 72.0;

    public static readonly StyledProperty<DocumentViewModel?> DocumentProperty =
        AvaloniaProperty.Register<DocumentView, DocumentViewModel?>(nameof(Document));

    private static readonly IBrush PageBrush = Brushes.White;
    private static readonly BoxShadows PageShadow = BoxShadows.Parse("0 2 8 0 #33101828");

    // Highlights over the paper (always white, hence fixed colours even in the dark theme).
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x3D, 0x7E, 0xFF));
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xD4, 0x3B));
    private static readonly IBrush CurrentMatchBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0x8C, 0x00));

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor PanningCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

    private DocumentViewModel? _doc;
    private Vector _offset;
    private Point? _dragStart;
    private Vector _dragOffset;
    private bool _dragMoved;
    private bool _invalidatePosted;
    private bool _syncingCurrentPage;

    /// <summary>Text selection with the mouse: page and caret position where it began.</summary>
    private (int Page, int Caret)? _selectAnchor;

    static DocumentView()
    {
        FocusableProperty.OverrideDefaultValue<DocumentView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<DocumentView>(true);
    }

    public DocumentView()
    {
        Cursor = HandCursor;
        // Input methods (IME, dead keys on Linux): while text is being edited, the page registers as an input field.
        AddHandler(TextInputMethodClientRequestedEvent, OnInputClientRequested);
    }

    public DocumentViewModel? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>Scroll offset of the visible section (user interface units), clamped to the extent.</summary>
    public Vector Offset
    {
        get => _offset;
        set
        {
            var clamped = Clamp(value);
            if (clamped == _offset) return;
            _offset = clamped;
            OffsetChanged?.Invoke();
            UpdateCurrentPage();
            InvalidateVisual();
            if (!_followingRecognition && _doc is { IsRecognizing: true } doc) doc.RecognitionScrolled(RecognitionPageInView());
        }
    }

    /// <summary>Gets the total size of all pages (for the scroll bars).</summary>
    public Size Extent { get; private set; }

    private double _rightInset;
    private bool _fitBesideInset;
    private (double Inset, bool Fit)? _pendingPanel;

    /// <summary>
    /// Strip covered on the right, in pixels, by a floating panel („Text“, „Form“, „Bildinfo“ …; KONZEPT.md, section 4.4): the view can
    /// scroll this much further to the right, so that the right edge of a page stands beside the panel, and an edited block underneath
    /// scrolls clear of it. Set together with <see cref="FitBesideInset"/> through <see cref="SetRightPanel"/>.
    /// </summary>
    public double RightInset
    {
        get => _rightInset;
        set => SetRightPanel(value, _pendingPanel?.Fit ?? _fitBesideInset);
    }

    /// <summary>
    /// The panel on the right belongs to a tool that stays on („Bearbeiten“, „Verborgenes schwärzen“, „Text einfügen“): „Page
    /// width“ and „Whole page“ fit the pages into the room left of it, and a page that would reach underneath moves left as far as needed
    /// (at most to the margin; in a wide window nothing moves). Not for panels that only show a selection: there the page stays where it
    /// is and can be scrolled clear — otherwise it would jump with every click, also between pressing and releasing the mouse.
    /// </summary>
    public bool FitBesideInset
    {
        get => _fitBesideInset;
        set => SetRightPanel(_pendingPanel?.Inset ?? _rightInset, value);
    }

    /// <summary>
    /// Sets <see cref="RightInset"/> and <see cref="FitBesideInset"/>. While the mouse is pressed (a click that deselects, a drag), the
    /// change waits until it is released: the page must not move under the pointer between press and release, or a new element would land
    /// beside the clicked spot.
    /// </summary>
    /// <param name="inset">Strip covered on the right, in pixels; 0 = no panel.</param>
    /// <param name="fitBeside">Whether the panel belongs to a tool that stays on.</param>
    public void SetRightPanel(double inset, bool fitBeside)
    {
        if (Interacting)
        {
            _pendingPanel = (inset, fitBeside);
            return;
        }
        _pendingPanel = null;
        bool insetChanged = Math.Abs(inset - _rightInset) >= 0.5, fitChanged = fitBeside != _fitBesideInset;
        if (!insetChanged && !fitChanged) return;
        _rightInset = inset;
        _fitBesideInset = fitBeside;
        if (fitChanged || _fitBesideInset) ApplyZoomMode(jump: false);
        Relayout();
        if (insetChanged) RevealEditor();
    }

    /// <summary>Whether the mouse is pressed in the view (panning, selecting text, dragging an element or in an edited block).</summary>
    private bool Interacting => _dragStart is not null || _elementDrag is not null || _selectAnchor is not null || _richDragging;

    /// <summary>Applies a change of the panel that waited for the mouse to be released.</summary>
    private void ApplyPendingPanel()
    {
        if (_pendingPanel is { } panel && !Interacting) SetRightPanel(panel.Inset, panel.Fit);
    }

    /// <summary>Where a page lies in the view (control coordinates; for tests).</summary>
    internal Rect PageInView(int page) => _pageRects[page].Translate(-_offset);

    /// <summary>Width the zoom modes fit into: the view, less the panel on the right if it belongs to a tool (<see cref="FitBesideInset"/>).</summary>
    private double FitWidth => Bounds.Width - (_fitBesideInset ? _rightInset : 0);

    /// <summary>
    /// Left edge of a row of pages: centred in the view; beside the panel of a tool (<see cref="FitBesideInset"/>) moved left as far as
    /// needed — at most to the margin — so that it does not lie under the panel.
    /// </summary>
    private double RowLeft(double rowWidth, double width)
    {
        var centred = (width - rowWidth) / 2;
        return !_fitBesideInset || _rightInset <= 0 ? centred
            : Math.Max(Math.Min(centred, PageMargin), Math.Min(centred, Bounds.Width - _rightInset - PageMargin - rowWidth));
    }

    /// <summary>Raised when the offset, the total size or the window size has changed.</summary>
    public event Action? OffsetChanged;

    // ------------------------------------------------------------ Switching documents

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty) Attach(change.GetNewValue<DocumentViewModel?>());
        if (change.Property == BoundsProperty)
        {
            ApplyZoomMode(jump: false);
            Relayout();
        }
    }

    private void Attach(DocumentViewModel? doc)
    {
        if (_doc is not null)
        {
            _doc.PropertyChanged -= OnDocumentChanged;
            _doc.FieldSelectionChanged -= InvalidateVisual;
            _doc.Renderer.Changed -= OnRendered;
            _doc.ScrollToPageRequested -= ScrollToPage;
            _doc.ScrollToDestinationRequested -= ScrollTo;
            _doc.HighlightsChanged -= InvalidateVisual;
            _doc.RevealRequested -= Reveal;
            _doc.RecognitionFollowRequested -= FollowRecognition;
            _doc.PagesChanged -= OnPagesChanged;
            _doc.ElementsChanged -= InvalidateVisual;
            _doc.FitRequested -= OnFitRequested;
            _doc.BlocksReady -= InvalidateVisual;
            _doc.DragCancelRequested -= CancelDragsAfterPress;
            _doc.ContentObjectsReady -= InvalidateVisual;
            _doc.ImageSelectionChanged -= InvalidateVisual;
            _doc.HiddenSpotsChanged -= InvalidateVisual;
            _doc.AccessibilityHelpChanged -= InvalidateVisual;
            _doc.FieldProposalsChanged -= InvalidateVisual;
            _doc.RichEditorUpdated -= RichEditorUpdated;
            _doc.MeasureChanged -= InvalidateVisual;
            InkTextures.Improved -= OnInkImproved;
            PdfKnead.Rendering.ImageCache.Improved -= OnInkImproved;   // Lanczos version of an inserted image finished
        }
        _doc = doc;
        _offset = default;
        _selectAnchor = null;
        if (doc is not null)
        {
            doc.PropertyChanged += OnDocumentChanged;
            doc.FieldSelectionChanged += InvalidateVisual;
            doc.Renderer.Changed += OnRendered;
            doc.ScrollToPageRequested += ScrollToPage;
            doc.ScrollToDestinationRequested += ScrollTo;
            doc.HighlightsChanged += InvalidateVisual;
            doc.RevealRequested += Reveal;
            doc.RecognitionFollowRequested += FollowRecognition;
            doc.PagesChanged += OnPagesChanged;
            doc.ElementsChanged += InvalidateVisual;
            doc.FitRequested += OnFitRequested;
            doc.BlocksReady += InvalidateVisual;
            doc.DragCancelRequested += CancelDragsAfterPress;
            doc.ContentObjectsReady += InvalidateVisual;
            doc.ImageSelectionChanged += InvalidateVisual;
            doc.HiddenSpotsChanged += InvalidateVisual;
            doc.AccessibilityHelpChanged += InvalidateVisual;
            doc.FieldProposalsChanged += InvalidateVisual;
            doc.RichEditorUpdated += RichEditorUpdated;
            doc.MeasureChanged += InvalidateVisual;
            InkTextures.Improved += OnInkImproved;
            PdfKnead.Rendering.ImageCache.Improved += OnInkImproved;
            ApplyZoomMode(jump: true);
        }
        Relayout();
        // Caret and input method belong to the document: off when switching, and on again if editing is in progress there.
        RichEditorChanged();
    }

    // Ballpoint-pen texture of a signature or Lanczos version of an image finished (background thread): redraw the elements.
    private void OnInkImproved() => Dispatcher.UIThread.Post(InvalidateVisual);

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Attach(null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_doc is null && Document is { } d) Attach(d);
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.PageLayout):
                // Stay on the current page.
                var current = _doc?.CurrentPage?.Index ?? 0;
                _single = current;
                ApplyZoomMode(jump: false);
                Relayout();
                ScrollToPage(current);
                break;
            case nameof(DocumentViewModel.Zoom) or nameof(DocumentViewModel.Rotation):
                if (e.PropertyName == nameof(DocumentViewModel.Rotation)) ApplyZoomMode(jump: false);
                Relayout();
                break;
            case nameof(DocumentViewModel.ZoomMode):
                ApplyZoomMode(jump: true);
                break;
            case nameof(DocumentViewModel.SelectedElement) or nameof(DocumentViewModel.EditingElement) or nameof(DocumentViewModel.ShowLayers) or nameof(DocumentViewModel.IsFieldProposalsOpen)
                or nameof(DocumentViewModel.InspectedImage) or nameof(DocumentViewModel.InspectedBarcode):
                InvalidateVisual();
                break;
            case nameof(DocumentViewModel.Tool):
                _elementDrag = null;
                _hoverBlock = null;
                InvalidateVisual();
                break;
            case nameof(DocumentViewModel.IsPickingColor):
                ColorPickChanged();
                break;
            case nameof(DocumentViewModel.RichEditor):
                RichEditorChanged();
                break;
            case nameof(DocumentViewModel.CurrentPage) when !_syncingCurrentPage && _doc?.CurrentPage is { } page:
                // Selection in the sidebar: jump there unless the page is already the current one.
                if (PageAtCenter() != page.Index) ScrollToPage(page.Index);
                break;
        }
    }

    /// <summary>Pages deleted, moved, rotated or inserted: lay them out again.</summary>
    private void OnPagesChanged()
    {
        _selectAnchor = null;
        if (_doc is { } d) _single = Math.Clamp(_single, 0, Math.Max(0, d.PageCount - 1));
        Relayout();
    }

    /// <summary>Finished tile from a background thread: redraw once (batched).</summary>
    private void OnRendered()
    {
        if (_invalidatePosted) return;
        _invalidatePosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _invalidatePosted = false;
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    // ------------------------------------------------------------ Page layout

    /// <summary>Position of every page (user interface units); in the single-page view only the current one.</summary>
    private readonly List<Rect> _pageRects = [];
    private readonly List<bool> _shown = [];

    /// <summary>Single-page view: the page shown.</summary>
    private int _single;

    private double Scale => (_doc?.Zoom ?? 1) * DipPerPoint;

    private PageLayout Layout => _doc?.PageLayout ?? PageLayout.Continuous;

    /// <summary>Page size in user interface units at the given scale, after the view rotation.</summary>
    private Size PageSize(int index, double scale)
    {
        var p = _doc!.Pages[index].Page;
        var (w, h) = _doc.Rotation is 90 or 270 ? (p.Height, p.Width) : (p.Width, p.Height);
        return new Size(w * scale, h * scale);
    }

    /// <summary>Width of a row containing page <paramref name="index"/> (in the two-page view with its neighbour).</summary>
    private double RowWidth(int index, double scale) =>
        Layout == PageLayout.TwoPages && (index | 1) < _doc!.PageCount
            ? PageSize(index & ~1, scale).Width + Gap + PageSize(index | 1, scale).Width
            : PageSize(index, scale).Width;

    private IEnumerable<int> ShownPages()
    {
        for (var i = 0; i < _pageRects.Count; i++)
            if (_shown[i]) yield return i;
    }

    private void Relayout()
    {
        // Remember the centre (from the old layout), so that a zoom without the mouse pointer keeps the position — not where the whole document
        // fitted into the view so far (it then stays at its top; docs/FEHLERGEDAECHTNIS.md, No. 321)
        var anchor = Extent.Height > _laidOutHeight + 1 ? DocumentPointAt(new Point(Bounds.Width / 2, Bounds.Height / 2)) : null;
        _pageRects.Clear();
        _shown.Clear();
        if (_doc is null || _doc.PageCount == 0)
        {
            Extent = default;
            OffsetChanged?.Invoke();
            InvalidateVisual();
            return;
        }

        var scale = Scale;
        var count = _doc.PageCount;
        for (var i = 0; i < count; i++)
        {
            _pageRects.Add(default);
            _shown.Add(false);
        }

        double width;
        double y = PageMargin;
        switch (Layout)
        {
            case PageLayout.Single:
                _single = Math.Clamp(_single, 0, count - 1);
                var single = PageSize(_single, scale);
                width = Math.Max(Bounds.Width, single.Width + 2 * PageMargin);
                _pageRects[_single] = new Rect(RowLeft(single.Width, width), y, single.Width, single.Height);
                _shown[_single] = true;
                y += single.Height + Gap;
                break;

            case PageLayout.TwoPages:
                var widest = 0.0;
                for (var i = 0; i < count; i += 2) widest = Math.Max(widest, RowWidth(i, scale));
                width = Math.Max(Bounds.Width, widest + 2 * PageMargin);
                for (var i = 0; i < count; i += 2)
                {
                    var left = PageSize(i, scale);
                    var right = i + 1 < count ? PageSize(i + 1, scale) : default;
                    var rowWidth = RowWidth(i, scale);
                    var x = RowLeft(rowWidth, width);
                    _pageRects[i] = new Rect(x, y, left.Width, left.Height);
                    _shown[i] = true;
                    if (i + 1 < count)
                    {
                        _pageRects[i + 1] = new Rect(x + left.Width + Gap, y, right.Width, right.Height);
                        _shown[i + 1] = true;
                    }
                    y += Math.Max(left.Height, right.Height) + Gap;
                }
                break;

            default:
                var maxWidth = 0.0;
                for (var i = 0; i < count; i++) maxWidth = Math.Max(maxWidth, PageSize(i, scale).Width);
                width = Math.Max(Bounds.Width, maxWidth + 2 * PageMargin);
                for (var i = 0; i < count; i++)
                {
                    var size = PageSize(i, scale);
                    _pageRects[i] = new Rect(RowLeft(size.Width, width), y, size.Width, size.Height);
                    _shown[i] = true;
                    y += size.Height + Gap;
                }
                break;
        }
        // Panel on the right open: only as much extra room as the pages reach underneath it (a narrow, centred page needs none;
        // otherwise a scroll bar would appear as soon as a shape is selected).
        var pagesRight = 0.0;
        for (var i = 0; i < count; i++)
            if (_shown[i]) pagesRight = Math.Max(pagesRight, _pageRects[i].Right);
        Extent = new Size(_rightInset > 0 ? Math.Max(width, pagesRight + _rightInset + PageMargin) : width, y - Gap + PageMargin);

        // (a centre below the last page — the document is shorter than the view — anchors nothing: otherwise the view scrolled to the end and the
        // last page became the current one; docs/FEHLERGEDAECHTNIS.md, No. 321)
        if (anchor is { } a && a.V <= 1 && _shown[Math.Clamp(a.Page, 0, count - 1)]) RestoreAnchor(a, new Point(Bounds.Width / 2, Bounds.Height / 2));
        _laidOutHeight = Bounds.Height;
        _offset = Clamp(_offset);
        OffsetChanged?.Invoke();
        UpdateCurrentPage();
        InvalidateVisual();
    }

    /// <summary>The height of the view the pages were last laid out for.</summary>
    private double _laidOutHeight;

    private Vector Clamp(Vector v) => new(
        Math.Clamp(v.X, 0, Math.Max(0, Extent.Width - Bounds.Width)),
        Math.Clamp(v.Y, 0, Math.Max(0, Extent.Height - Bounds.Height)));

    /// <summary>Position in the document (page, fraction within the page) under a point of the view.</summary>
    private (int Page, double U, double V)? DocumentPointAt(Point viewPoint)
    {
        if (_pageRects.Count == 0) return null;
        var p = viewPoint + _offset;
        var index = -1;
        foreach (var i in ShownPages())
        {
            var r = _pageRects[i];
            if (r.Top > p.Y) break;
            // Two-page view: in the row, the page under the point (or the left one).
            if (index < 0 || _pageRects[index].Top < r.Top || r.Left <= p.X) index = i;
        }
        if (index < 0) index = ShownPages().FirstOrDefault();
        var rect = _pageRects[index];
        return rect.Width <= 0 ? null : (index, (p.X - rect.Left) / rect.Width, (p.Y - rect.Top) / rect.Height);
    }

    private void RestoreAnchor((int Page, double U, double V) anchor, Point viewPoint)
    {
        if (anchor.Page >= _pageRects.Count || !_shown[anchor.Page]) return;
        var r = _pageRects[anchor.Page];
        var docPoint = new Point(r.Left + anchor.U * r.Width, r.Top + anchor.V * r.Height);
        _offset = Clamp(new Vector(docPoint.X - viewPoint.X, docPoint.Y - viewPoint.Y));
    }

    /// <summary>Page to which “Whole page” was last fitted (key; stays valid when pages are moved).</summary>
    private int? _fitKey;

    /// <summary>“Whole page”/“Page width” chosen again: fit again to the page in the centre.</summary>
    private void OnFitRequested() => ApplyZoomMode(jump: true);

    /// <summary>
    /// Page width or whole page: computes the zoom from the window size. With <paramref name="jump"/>
    /// (mode chosen, document opened), the page in the centre is fitted and the view jumps to it;
    /// otherwise (window or panel size changes) the fitted page and the position stay. Refitting to the
    /// centre page here would make the view jump to the top of that page every time a scroll bar appears
    /// or disappears, and in a tall A0 plan it would be impossible to move on.
    /// </summary>
    private void ApplyZoomMode(bool jump)
    {
        if (_doc is null || _doc.PageCount == 0 || Bounds.Width <= 0) return;
        var center = Math.Clamp(PageAtCenter(), 0, _doc.PageCount - 1);
        var reference = center;
        if (jump || _fitKey is null) _fitKey = _doc.Pages[center].Key;
        else
            for (var i = 0; i < _doc.PageCount; i++)
                if (_doc.Pages[i].Key == _fitKey)
                {
                    reference = i;
                    break;
                }
        var size = PageSize(reference, DipPerPoint);
        var rowWidth = RowWidth(reference, DipPerPoint);
        var widest = Layout == PageLayout.Single
            ? size.Width
            : Enumerable.Range(0, _doc.PageCount).Max(i => RowWidth(i, DipPerPoint));
        var zoom = _doc.ZoomMode switch
        {
            ZoomMode.FitWidth => (FitWidth - 2 * PageMargin) / widest,
            ZoomMode.FitPage => Math.Min((FitWidth - 2 * PageMargin) / rowWidth, (Bounds.Height - 2 * PageMargin) / size.Height),
            _ => _doc.Zoom,
        };
        zoom = Math.Clamp(zoom, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);
        if (Math.Abs(zoom - _doc.Zoom) > 1e-6) _doc.Zoom = zoom;
        if (jump && _doc.ZoomMode == ZoomMode.FitPage) ScrollToPage(reference);
    }

    /// <summary>
    /// Shows a destination with its view (ISO 32000-2, 12.3.2.2, Table 149): Fit → the whole page, FitH → page width with top at the top,
    /// FitV → whole page with left at the left, FitR → zoom onto the rectangle, XYZ → the point (left, top) at the upper left and its zoom; a
    /// missing value keeps the current one. FitB, FitBH, FitBV as Fit, FitH, FitV — a deliberate approximation (the bounding box of the
    /// content is not measured; KONZEPT). Page coordinates through the page's matrix (rotation, crop box).
    /// </summary>
    /// <param name="index">The page's position in the view.</param>
    /// <param name="target">The destination.</param>
    public void ScrollTo(int index, PdfDestination target)
    {
        if (_doc is null || index < 0 || index >= _doc.PageCount) return;
        switch (target.Kind)
        {
            case DestinationKind.Fit or DestinationKind.FitB or DestinationKind.FitV or DestinationKind.FitBV:
                _doc.ZoomMode = ZoomMode.FitPage;
                break;
            case DestinationKind.FitH or DestinationKind.FitBH:
                _doc.ZoomMode = ZoomMode.FitWidth;
                break;
            case DestinationKind.FitR when target.Rect is { } r && r.Right - r.Left > 0 && r.Top - r.Bottom > 0:
                var zoom = Math.Min((Bounds.Width - 2 * PageMargin) / ((r.Right - r.Left) * DipPerPoint), (Bounds.Height - 2 * PageMargin) / ((r.Top - r.Bottom) * DipPerPoint));
                _doc.ZoomMode = ZoomMode.Custom;
                _doc.Zoom = Math.Clamp(zoom, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);
                break;
            case DestinationKind.Xyz when target.Zoom is { } z:
                _doc.ZoomMode = ZoomMode.Custom;
                _doc.Zoom = Math.Clamp(z, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);
                break;
        }
        ScrollToPage(index);
        double? x = target.Kind switch
        {
            DestinationKind.Xyz or DestinationKind.FitV or DestinationKind.FitBV => target.Left,
            DestinationKind.FitR => target.Rect?.Left,
            _ => null,
        };
        double? y = target.Kind switch
        {
            DestinationKind.Xyz or DestinationKind.FitH or DestinationKind.FitBH => target.Top,
            DestinationKind.FitR => target.Rect?.Top,
            _ => null,
        };
        if (x is null && y is null || index >= _pageRects.Count) return;
        var at = PagePointToView(index, x ?? 0, y ?? 0);
        Offset = new Vector(x is null ? _offset.X : _offset.X + at.X - PageMargin, y is null ? _offset.Y : _offset.Y + at.Y - PageMargin);
    }

    /// <summary>Scrolls to the top of a page (in the single-page view: shows that page).</summary>
    /// <param name="index">Page index; out-of-range values are ignored.</param>
    public void ScrollToPage(int index)
    {
        if (_doc is null || index < 0 || index >= _doc.PageCount) return;
        if (Layout == PageLayout.Single)
        {
            if (_single != index)
            {
                _single = index;
                _offset = default;
                Relayout();
            }
            Offset = default;
            return;
        }
        if (index >= _pageRects.Count) return;
        Offset = new Vector(_offset.X, _pageRects[index].Top - PageMargin);
    }

    /// <summary>
    /// The current page: the one at the centre of the view. Where the centre lies below the last page (all pages fit into a tall view, see
    /// docs/FEHLERGEDAECHTNIS.md, No. 321), the topmost page in view — not the last one.
    /// </summary>
    private int PageAtCenter()
    {
        if (_pageRects.Count == 0) return 0;
        if (Layout == PageLayout.Single) return _single;
        var center = _offset.Y + Bounds.Height / 2;
        int? topmost = null;
        var last = 0;
        foreach (var i in ShownPages())
        {
            if (_pageRects[i].Bottom + Gap / 2 >= center) return i;
            if (topmost is null && _pageRects[i].Bottom >= _offset.Y) topmost = i;
            last = i;
        }
        return topmost ?? last;
    }

    private void UpdateCurrentPage()
    {
        if (_doc is null || _pageRects.Count == 0) return;
        var index = PageAtCenter();
        if (_doc.CurrentPage?.Index == index) return;
        _syncingCurrentPage = true;
        _doc.CurrentPage = _doc.Pages[index];
        _syncingCurrentPage = false;
    }

    /// <summary>Single-page view: turn to the next page at the end of a page instead of stopping.</summary>
    private bool FlipPage(int direction)
    {
        if (Layout != PageLayout.Single || _doc is null) return false;
        var target = _single + direction;
        if (target < 0 || target >= _doc.PageCount) return false;
        ScrollToPage(target);
        if (direction < 0) Offset = new Vector(_offset.X, Extent.Height);   // previous page from the bottom
        return true;
    }

    // ------------------------------------------------------------ Drawing

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_doc is null || _pageRects.Count == 0) return;

        var renderer = _doc.Renderer;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        // annotations with NoZoom keep the size they have at 100 % on this screen (ISO 32000-2, 12.5.3; docs/FEHLERGEDAECHTNIS.md, No. 576)
        renderer.NoZoomPixelsPerPoint = DipPerPoint * scaling;
        var dipPerPoint = Scale;
        var level = DocumentRenderer.LevelFor(dipPerPoint * scaling);
        var view = new Rect(_offset.X, _offset.Y, Bounds.Width, Bounds.Height);

        var draws = new List<SkiaImageDraw>();
        var overviews = new List<TileKey>();
        var exact = new List<(TileKey Key, double Distance)>();
        var center = view.Center;

        for (var i = 0; i < _pageRects.Count; i++)
        {
            if (!_shown[i]) continue;
            var rect = _pageRects[i];
            if (rect.Bottom < view.Top - view.Height || rect.Top > view.Bottom + view.Height) continue;   // one screen ahead
            var visible = rect.Intersects(view);
            var screen = rect.Translate(-_offset);

            if (visible)
            {
                context.DrawRectangle(PageBrush, null, new RoundedRect(screen), PageShadow);
            }

            // 1. Coarse overview image: there at once, even when scrolling fast
            var key0 = _doc.Pages[i].Key;
            var overviewLevel = renderer.OverviewLevel(key0);
            var overview = new TileKey(key0, overviewLevel, 0, 0, _doc.Rotation);
            if (renderer.TryGetTile(overview) is { } overviewImage)
            {
                if (visible) draws.Add(new SkiaImageDraw(overviewImage, SKRect.Create(overviewImage.Width, overviewImage.Height), ToSk(screen)));
            }
            else
            {
                overviews.Add(overview);
            }
            if (!visible || level <= overviewLevel) continue;

            // 2. Sharp tiles for the visible part
            var (pw, ph) = renderer.SizeAt(key0, level, _doc.Rotation);
            var pxToDip = rect.Width / pw;
            var tileDip = DocumentRenderer.TileSize * pxToDip;
            var part = rect.Intersect(view);
            var x0 = (int)Math.Floor((part.Left - rect.Left) / tileDip);
            var x1 = (int)Math.Floor((part.Right - rect.Left - 0.01) / tileDip);
            var y0 = (int)Math.Floor((part.Top - rect.Top) / tileDip);
            var y1 = (int)Math.Floor((part.Bottom - rect.Top - 0.01) / tileDip);
            for (var ty = y0; ty <= y1; ty++)
            for (var tx = x0; tx <= x1; tx++)
            {
                var key = new TileKey(key0, level, tx, ty, _doc.Rotation);
                var (tw, th) = renderer.TilePixelSize(key);
                if (tw <= 0 || th <= 0) continue;
                var tileRect = new Rect(rect.Left + tx * tileDip, rect.Top + ty * tileDip, tw * pxToDip, th * pxToDip);
                if (renderer.TryGetTile(key) is { } tile)
                    draws.Add(new SkiaImageDraw(tile, SKRect.Create(tile.Width, tile.Height), ToSk(tileRect.Translate(-_offset))));
                else
                    exact.Add((key, Distance(tileRect.Center, center)));
            }
        }

        if (draws.Count > 0) context.Custom(new SkiaImageDrawing(new Rect(Bounds.Size), draws));
        DrawElements(context, view);
        if (_sampling) return;   // eyedropper: only page and elements, no view aids
        DrawHighlights(context, view);
        DrawTextBlocks(context, view);
        DrawHiddenSpots(context, view);
        DrawCleanupPlaces(context, view);
        DrawAccessibilityHelp(context, view);
        DrawShownPlace(context, view);
        DrawModulePlace(context, view);
        DrawRecognitionPlace(context, view);
        DrawFieldProposals(context, view);
        DrawElementFrames(context);
        DrawRichEditor(context);
        DrawLayerNumbers(context);
        DrawColorPick(context);
        DrawMeasurements(context);
        DrawContentImages(context, view);
        DrawBarcode(context);
        DrawImageInfo(context);
        DrawLayerLabel(context);

        // Overview images first, then tiles from the centre outwards.
        var wanted = new List<TileKey>(overviews.Count + exact.Count);
        wanted.AddRange(overviews);
        wanted.AddRange(exact.OrderBy(e => e.Distance).Select(e => e.Key));
        renderer.Request("ansicht", wanted);
    }

    /// <summary>Draws search matches and the text selection of the visible pages.</summary>
    private void DrawHighlights(DrawingContext context, Rect view)
    {
        var doc = _doc!;
        var current = doc.Current;
        for (var i = 0; i < _pageRects.Count; i++)
        {
            if (!_shown[i] || !_pageRects[i].Intersects(view)) continue;
            var matches = doc.MatchesOn(i);
            var selected = doc.SelectionPage == i && doc.HasTextSelection;
            if (matches.Count == 0 && !selected) continue;
            if (doc.TryGetText(i) is not { } text)
            {
                // Text not read yet: read it, then redraw.
                _ = doc.GetTextAsync(i).ContinueWith(_ => Dispatcher.UIThread.Post(InvalidateVisual), TaskScheduler.Default);
                continue;
            }
            var toView = PageToView(i);
            foreach (var range in matches)
            {
                var brush = current is { } c && c.Page == i && c.Range == range ? CurrentMatchBrush : MatchBrush;
                foreach (var quad in text.Quads(range)) context.DrawGeometry(brush, null, QuadGeometry(quad, toView));
            }
            if (selected)
                foreach (var quad in text.Quads(doc.Selection)) context.DrawGeometry(SelectionBrush, null, QuadGeometry(quad, toView));
        }
    }

    private static StreamGeometry QuadGeometry(TextQuad q, PdfMatrix toView)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        Point P(double x, double y)
        {
            var (vx, vy) = toView.Transform(x, y);
            return new Point(vx, vy);
        }
        g.BeginFigure(P(q.X0, q.Y0), true);
        g.LineTo(P(q.X1, q.Y1));
        g.LineTo(P(q.X2, q.Y2));
        g.LineTo(P(q.X3, q.Y3));
        g.EndFigure(true);
        return geometry;
    }

    /// <summary>Page space (PDF points, y upwards) to view (user interface units), including the view rotation.</summary>
    private PdfMatrix PageToView(int index) =>
        DocumentRenderer.PageToDevice(_doc!.Pages[index].Page, Scale, _doc.Rotation)
        * PdfMatrix.Translate(_pageRects[index].Left - _offset.X, _pageRects[index].Top - _offset.Y);

    /// <summary>Point in page space to point of the view (for tests).</summary>
    internal Point PagePointToView(int page, double x, double y)
    {
        var (vx, vy) = PageToView(page).Transform(x, y);
        return new Point(vx, vy);
    }

    /// <summary>The link pressed with the press under way (followed on release without dragging).</summary>
    private (int Position, PageLink Link, double X, double Y)? _pressedLink;

    /// <summary>The link under a point of the view, with the page and the point in page space; null if there is none.</summary>
    private (int Position, PageLink Link, double X, double Y)? LinkAt(Point viewPoint)
    {
        if (_doc is null || PageAt(viewPoint) is not { } page) return null;
        var (x, y) = ToPage(page, viewPoint);
        foreach (var link in _doc.LinksAt(page))
            if (link.Contains(x, y)) return (page, link, x, y);
        return null;
    }

    /// <summary>Page under a point of the view (with an optional margin); null outside.</summary>
    private int? PageAt(Point viewPoint, double margin = 0)
    {
        var p = viewPoint + _offset;
        for (var i = 0; i < _pageRects.Count; i++)
            if (_shown[i] && _pageRects[i].Inflate(margin).Contains(p)) return i;
        return null;
    }

    /// <summary>Point of the view in the page space of a page.</summary>
    private (double X, double Y) ToPage(int index, Point viewPoint) => PageToView(index).Invert().Transform(viewPoint.X, viewPoint.Y);

    /// <summary>
    /// Text under the point (“Select text” tool): page and text if the point lies on
    /// a character. If the page's text has not been read yet, reading starts now (and null is returned).
    /// </summary>
    private (int Page, PageText Text, double X, double Y)? TextAt(Point viewPoint)
    {
        if (_doc is null || PageAt(viewPoint) is not { } page) return null;
        if (_doc.TryGetText(page) is not { } text)
        {
            _ = _doc.GetTextAsync(page);
            return null;
        }
        var (x, y) = ToPage(page, viewPoint);
        // Two pixels of tolerance, converted into page points.
        var margin = 2 / Math.Max(Scale, 1e-6);
        return text.IsOverText(x, y, margin) ? (page, text, x, y) : null;
    }

    /// <summary>Shows a search match: brings it to the centre unless it is already well visible.</summary>
    private void Reveal(int page, TextQuad quad)
    {
        if (_doc is null || page < 0 || page >= _doc.PageCount) return;
        if (!_shown[page]) ScrollToPage(page);   // single-page view: go there first
        if (page >= _pageRects.Count || !_shown[page]) return;
        var toView = PageToView(page);
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var (x, y) in new[] { (quad.X0, quad.Y0), (quad.X1, quad.Y1), (quad.X2, quad.Y2), (quad.X3, quad.Y3) })
        {
            var (vx, vy) = toView.Transform(x, y);
            left = Math.Min(left, vx);
            right = Math.Max(right, vx);
            top = Math.Min(top, vy);
            bottom = Math.Max(bottom, vy);
        }
        var margin = 48;
        var target = _offset;
        if (top < margin || bottom > Bounds.Height - margin) target = target.WithY(_offset.Y + (top + bottom) / 2 - Bounds.Height / 2);
        if (left < margin || right > Bounds.Width - margin) target = target.WithX(_offset.X + (left + right) / 2 - Bounds.Width / 2);
        Offset = target;
        InvalidateVisual();
    }

    private static double Distance(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static SKRect ToSk(Rect r) => new((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom);

    // ------------------------------------------------------------ Interaction

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_doc is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ZoomAt(e.GetPosition(this), Math.Pow(1.15, e.Delta.Y));
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            Offset -= new Vector(e.Delta.Y * 60, 0);
        }
        else
        {
            // Single-page view: turn the page at the edge.
            var atBottom = _offset.Y >= Extent.Height - Bounds.Height - 0.5;
            var atTop = _offset.Y <= 0.5;
            if (e.Delta.Y < 0 && atBottom && FlipPage(1)) { e.Handled = true; return; }
            if (e.Delta.Y > 0 && atTop && FlipPage(-1)) { e.Handled = true; return; }
            Offset -= new Vector(e.Delta.X * 60, e.Delta.Y * 60);
        }
        e.Handled = true;
    }

    /// <summary>Zooms so that the position under <paramref name="viewPoint"/> stays in place; switches to custom zoom.</summary>
    /// <param name="viewPoint">Point of the view in user interface units.</param>
    /// <param name="factor">Factor applied to the current zoom (the result is clamped to the zoom range).</param>
    public void ZoomAt(Point viewPoint, double factor)
    {
        if (_doc is null) return;
        var anchor = DocumentPointAt(viewPoint);
        _doc.ZoomMode = ZoomMode.Custom;
        _doc.Zoom = Math.Clamp(_doc.Zoom * factor, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);
        if (anchor is { } a)
        {
            RestoreAnchor(a, viewPoint);
            OffsetChanged?.Invoke();
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var position = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (CleanupPressed(e, position) || FieldProposalPressed(e, position) || HiddenPressed(e, position) || ColorPickPressed(e, position) || MeasurePressed(e, position) ||
            RichEditPressed(e, position) || EditPressed(e, position))
        {
            e.Handled = true;
            return;
        }
        if (props.IsLeftButtonPressed && ElementPressed(e, position))
        {
            e.Handled = true;
            return;
        }
        if (props.IsRightButtonPressed && (ElementMenu(position) || EditMenu(position)))
        {
            e.Handled = true;
            return;
        }

        // A link (decision J): in „Text markieren“ only with Ctrl, with the hand tool by a plain click — followed on release without dragging
        // (docs/FEHLERGEDAECHTNIS.md, No. 566); never in the editing tools
        if (props.IsLeftButtonPressed && _doc is { } linkDoc && (linkDoc.Tool == ViewTool.Pan || linkDoc.Tool == ViewTool.SelectText && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            && LinkAt(position) is { } pressed)
        {
            _pressedLink = pressed;
            _dragStart = position;
            _dragOffset = _offset;
            _dragMoved = false;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Select text: on a character the selection begins (double-click word, triple-click line). In the redaction tools
        // the selection becomes a redaction mark (on release; word and line at once).
        if (props.IsLeftButtonPressed && _doc is { Tool: ViewTool.SelectText or ViewTool.InsertRedaction or ViewTool.RedactText } doc && TextAt(position) is { } hit)
        {
            var caret = hit.Text.CaretAt(hit.X, hit.Y);
            var redact = doc.Tool is ViewTool.InsertRedaction or ViewTool.RedactText;
            if (e.ClickCount == 2)
            {
                doc.Select(hit.Page, hit.Text.WordAt(caret));
                if (redact) doc.RedactSelection();
            }
            else if (e.ClickCount >= 3)
            {
                doc.Select(hit.Page, hit.Text.LineAt(caret));
                if (redact) doc.RedactSelection();
            }
            else
            {
                _selectAnchor = (hit.Page, caret);
                doc.Select(hit.Page, default);
                e.Pointer.Capture(this);
            }
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed || props.IsMiddleButtonPressed)
        {
            _dragStart = position;
            _dragOffset = _offset;
            _dragMoved = false;
            e.Pointer.Capture(this);
            Cursor = PanningCursor;
            e.Handled = true;
        }
    }

    /// <summary>Whether a press is still being dragged (a frame, an element, a picture, the view, a text selection); for tests.</summary>
    internal bool IsDragging => _elementDrag is not null || _imageDrag is not null || _blockBand is not null || _dragStart is not null || _richDragging || _selectAnchor is not null;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_dragStart is null && (HiddenMoved(position) || EditMoved(position, e.KeyModifiers.HasFlag(KeyModifiers.Shift)))) return;
        if (ElementMoved(position, e.KeyModifiers.HasFlag(KeyModifiers.Shift))) return;
        if (_dragStart is null && RichEditMoved(position)) return;
        if (_dragStart is null && ColorPickMoved(position)) return;
        if (_dragStart is null && MeasureMoved(position, e.KeyModifiers)) return;
        if (_selectAnchor is { } anchor && _doc is { } doc)
        {
            // Selection up to the nearest position on the same page.
            if (doc.TryGetText(anchor.Page) is { } text)
            {
                var (x, y) = ToPage(anchor.Page, position);
                doc.Select(anchor.Page, PageText.Between(anchor.Caret, text.CaretAt(x, y)));
            }
            return;
        }
        if (_dragStart is { } start)
        {
            var delta = position - start;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 3) _dragMoved = true;
            Offset = _dragOffset - delta;
            return;
        }
        // Without a button pressed: over a link (reading tools) the hand and its target, text cursor over text, otherwise hand.
        if (_doc is { Tool: ViewTool.SelectText or ViewTool.Pan } reading && ElementCursor(position) is null && LinkAt(position) is { } over)
        {
            Cursor = HandCursor;
            var tip = DocumentViewModel.Describe(over.Link.Target);
            if (reading.Tool == ViewTool.SelectText) tip += "\n" + Tr.T("Strg+Klick folgt dem Link");
            if (!Equals(ToolTip.GetTip(this), tip)) ToolTip.SetTip(this, tip);
            return;
        }
        if (ToolTip.GetTip(this) is not null) ToolTip.SetTip(this, null);
        Cursor = ElementCursor(position) ?? (_doc is { Tool: ViewTool.SelectText or ViewTool.RedactText } && TextAt(position) is not null ? TextCursor : HandCursor);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // Mouse capture lost in the middle of dragging: cancel everything so that nothing is left hanging.
        CancelDrags();
    }

    /// <summary>
    /// The document asks to let go of the drag before a question: the name box loses its focus inside the press on the page, before this
    /// view has started its drag — so the drag is cancelled now and once more after the press has been handled.
    /// </summary>
    private void CancelDragsAfterPress()
    {
        CancelDrags();
        Dispatcher.UIThread.Post(CancelDrags, DispatcherPriority.Input);
    }

    /// <summary>
    /// Cancels every drag in progress; nothing of it is applied. Also called when the document asks for it before a question opens: the
    /// click that started the drag (on the page, leaving the name box of a field) will never see its release — the dialog takes it — and the
    /// selection frame would follow the mouse until the next click (docs/FEHLERGEDAECHTNIS.md, No. 397).
    /// </summary>
    private void CancelDrags()
    {
        _measureDrag = null;   // a handle of a measurement: let go (No. 598)
        ElementCaptureLost();
        ImageEditCaptureLost();
        _blockBand = null;
        _richDragging = false;
        _selectAnchor = null;
        if (_dragStart is not null)
        {
            _dragStart = null;
            Cursor = HandCursor;
        }
        ApplyPendingPanel();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _leftDown = false;
        // A panel that opened or closed during the press takes effect once the release has been handled
        if (_pendingPanel is not null) Dispatcher.UIThread.Post(ApplyPendingPanel);
        if (MeasureReleased(e) || TextEditReleased(e) || ImageEditReleased(e) || RichEditReleased(e) || ElementReleased(e, e.GetPosition(this))) return;
        if (_selectAnchor is not null)
        {
            _selectAnchor = null;
            e.Pointer.Capture(null);
            if (_doc is { Tool: ViewTool.InsertRedaction or ViewTool.RedactText } redacting) redacting.RedactSelection();   // redaction tools: the selection becomes a mark
            return;
        }
        if (_dragStart is null) return;
        _dragStart = null;
        e.Pointer.Capture(null);
        Cursor = HandCursor;
        if (_pressedLink is { } link)
        {
            _pressedLink = null;
            if (!_dragMoved && e.InitialPressMouseButton == MouseButton.Left && _doc is { } linked) linked.FollowLink(link.Position, link.Link, link.X, link.Y);
            return;
        }
        // A click beside the text (without panning) clears the selection; on an image it shows its details (reading tools).
        if (!_dragMoved && e.InitialPressMouseButton == MouseButton.Left)
        {
            _doc?.ClearSelection();
            ReadingClick(e.GetPosition(this));
        }
    }

    /// <summary>Selects everything on the current page (Ctrl+A).</summary>
    private async void SelectAllOnCurrentPage()
    {
        if (_doc?.CurrentPage is not { } page) return;
        var doc = _doc;
        PdfKnead.Core.Pdf.Text.PageText text;
        try
        {
            text = await doc.GetTextAsync(page.Index);
        }
        catch (OperationCanceledException)
        {
            return;   // document switched or closed (must not end up as “Unexpected error”)
        }
        if (!ReferenceEquals(doc, _doc) || text.IsEmpty) return;
        doc.Select(page.Index, new TextRange(0, text.Text.Length));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_doc is null || e.Handled) return;
        // Esc cancels the eyedropper (the shape stays selected; while editing a block, the eyedropper is cancelled first).
        if (e.Key == Key.Escape && _doc.IsPickingColor)
        {
            _doc.EndColorPick(null);
            e.Handled = true;
            return;
        }
        // „Messen“: Enter ends, Esc drops, Backspace takes back a point
        if (MeasureKey(e))
        {
            e.Handled = true;
            return;
        }
        // Editing formatted text: the keys belong to the block.
        if (Editor is { } editor && RichKey(editor, e))
        {
            e.Handled = true;
            return;
        }
        // „Text bearbeiten“ with chosen blocks: Enter edits them as one, Esc drops the choice first.
        if (TextEditKey(e))
        {
            e.Handled = true;
            return;
        }
        // „Bearbeiten“ with a chosen picture: arrows move it, Esc lets go of it first; with a code shown, Esc lets go of the code; while
        // reading, Esc closes the details of an image.
        if (ImageEditKey(e) || EditKey(e) || ReadingImageInfoKey(e))
        {
            e.Handled = true;
            return;
        }
        // Esc closes the flyout with the whole hidden text first; the tool stays on.
        if (e.Key == Key.Escape && _doc.IsHiddenTextOpen)
        {
            _doc.IsHiddenTextOpen = false;
            e.Handled = true;
            return;
        }
        // „Bearbeiten“ with an element or field selected: Esc first lets go of it.
        if (e.Key == Key.Escape && _doc is { Tool: ViewTool.Edit, SelectedElement: not null, EditingElement: null } && _elementDrag is null)
        {
            _doc.SelectFields([]);
            _doc.SelectedElement = null;
            e.Handled = true;
            return;
        }
        // Esc ends the tools that stay on (redaction, „Bearbeiten“), like a second click on the button.
        if (e.Key == Key.Escape && _doc.Tool is ViewTool.InsertRedaction or ViewTool.RedactText or ViewTool.RedactHidden or ViewTool.Edit
                && _elementDrag is null && _doc.EditingElement is null)
        {
            _doc.SelectedElement = null;
            _doc.ClearSelection();
            _doc.Tool = ViewTool.SelectText;
            e.Handled = true;
            return;
        }
        // While editing, no shortcuts for fields and elements (Ctrl+Page Up would move the block in the layer order); zooming and paging remain.
        if (Editor is null && (FieldKey(e) || ElementKey(e)))
        {
            e.Handled = true;
            return;
        }
        var page = Bounds.Height * 0.9;
        // Without Alt: AltGr (Ctrl+Alt) +0 is “}”, +Plus is “~”, not a zoom.
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        switch (e.Key)
        {
            case Key.Down: Offset += new Vector(0, 50); break;
            case Key.Up: Offset -= new Vector(0, 50); break;
            case Key.Right when !ctrl: Offset += new Vector(50, 0); break;
            case Key.Left when !ctrl: Offset -= new Vector(50, 0); break;
            case Key.PageDown or Key.Space:
                if (_offset.Y >= Extent.Height - Bounds.Height - 0.5 && FlipPage(1)) break;
                Offset += new Vector(0, page);
                break;
            case Key.PageUp:
                if (_offset.Y <= 0.5 && FlipPage(-1)) break;
                Offset -= new Vector(0, page);
                break;
            case Key.Home when ctrl: Offset = default; break;
            case Key.End when ctrl: Offset = new Vector(_offset.X, Extent.Height); break;
            // the same steps as the zoom buttons: the next multiple of 5 % (DocumentViewModel.ZoomStep)
            case Key.OemPlus or Key.Add when ctrl && _doc is not null: ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), DocumentViewModel.ZoomStep(_doc.Zoom, 1) / _doc.Zoom); break;
            case Key.OemMinus or Key.Subtract when ctrl && _doc is not null: ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), DocumentViewModel.ZoomStep(_doc.Zoom, -1) / _doc.Zoom); break;
            case Key.D0 or Key.NumPad0 when ctrl: _doc.Fit(ZoomMode.FitPage); break;
            case Key.D1 or Key.NumPad1 when ctrl:
                _doc.ZoomMode = ZoomMode.Custom;
                _doc.Zoom = 1;
                break;
            case Key.A when ctrl:
                SelectAllOnCurrentPage();
                break;
            case Key.Escape when _doc.HasTextSelection:
                _doc.ClearSelection();
                break;
            case Key.Escape when _doc.IsSearchOpen:
                _doc.CloseSearch();
                break;
            default: return;
        }
        e.Handled = true;
    }
}
