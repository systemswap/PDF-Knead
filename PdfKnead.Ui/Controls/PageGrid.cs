using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PdfKnead.Rendering;
using PdfKnead.ViewModels;
using SkiaSharp;

namespace PdfKnead.Controls;

/// <summary>
/// All pages as tiles in a grid (merge, split, sort). Draws itself and only what is visible, so that
/// even thousands of pages stay smooth; the thumbnails come from the
/// <see cref="DocumentRenderer"/>. Selection via <see cref="PageViewModel.IsSelected"/>:
/// click, Ctrl+click, Shift+click, dragging a frame; in check mode a click toggles.
/// Selected pages can be dragged out (<see cref="PagesDragging"/>, <see cref="PagesDropped"/>).
/// In reorder mode (<see cref="ReorderMode"/>) they are moved within the tiles (<see cref="PagesReordered"/>).
/// </summary>
public sealed class PageGrid : Control
{
    public static readonly StyledProperty<DocumentRenderer?> RendererProperty =
        AvaloniaProperty.Register<PageGrid, DocumentRenderer?>(nameof(Renderer));

    public static readonly StyledProperty<IReadOnlyList<PageViewModel>?> PagesProperty =
        AvaloniaProperty.Register<PageGrid, IReadOnlyList<PageViewModel>?>(nameof(Pages));

    public static readonly StyledProperty<double> TileSizeProperty =
        AvaloniaProperty.Register<PageGrid, double>(nameof(TileSize), 150, coerce: (_, v) => Math.Clamp(v, MinTile, MaxTile));

    public static readonly StyledProperty<bool> CheckModeProperty =
        AvaloniaProperty.Register<PageGrid, bool>(nameof(CheckMode));

    public static readonly StyledProperty<bool> ReorderModeProperty =
        AvaloniaProperty.Register<PageGrid, bool>(nameof(ReorderMode));

    /// <summary>Smallest tile size in user interface units.</summary>
    public const double MinTile = 70;
    /// <summary>Largest tile size in user interface units.</summary>
    public const double MaxTile = 420;
    private const double Pad = 10;
    private const double Label = 22;
    private const double MarkHeight = 4;

    private double _offset;
    private bool _invalidatePosted;
    private DocumentRenderer? _subscribed;

    // Interaction
    private int _anchor = -1;
    private Point? _pressAt;
    private int _pressedIndex = -1;
    private bool _deferredSingle;
    private bool _dragging;
    private Rect? _band;
    private HashSet<int>? _bandBase;
    private Point _lastPointer;
    private bool _autoScrolling;
    private int _insertAt = -1;            // reordering: insertion point while dragging
    private bool _insertAfterPrevious;     // marker on the right of the tile before (end of a row)
    private int _dropAt = -1;              // pages dragged in from another grid: insertion point shown
    private bool _dropAfterPrevious;

    /// <summary>
    /// Pages dragged in from another grid (merging): shows the insertion point under <paramref name="point"/> (control coordinates) as
    /// when reordering, or hides it with null.
    /// </summary>
    /// <param name="point">The pointer in control coordinates; null hides the marker.</param>
    /// <returns>The insertion index (0 … count), or -1 if hidden.</returns>
    public int ShowDropAt(Point? point)
    {
        var at = point is { } p ? InsertionAt(p, out _dropAfterPrevious) : -1;
        if (at != _dropAt)
        {
            _dropAt = at;
            InvalidateVisual();
        }
        return at;
    }

    static PageGrid()
    {
        FocusableProperty.OverrideDefaultValue<PageGrid>(true);
        ClipToBoundsProperty.OverrideDefaultValue<PageGrid>(true);
        AffectsRender<PageGrid>(PagesProperty, TileSizeProperty, CheckModeProperty);
    }

    public DocumentRenderer? Renderer
    {
        get => GetValue(RendererProperty);
        set => SetValue(RendererProperty, value);
    }

    public IReadOnlyList<PageViewModel>? Pages
    {
        get => GetValue(PagesProperty);
        set => SetValue(PagesProperty, value);
    }

    /// <summary>
    /// Name under which the grid asks the renderer for its tiles. Two grids sharing one renderer (merging: files and result) need
    /// names of their own — under the same name each request replaces the other's, and its tiles would be cancelled.
    /// </summary>
    public string Channel { get; set; } = "raster";

    /// <summary>Edge length of a tile, clamped to <see cref="MinTile"/> … <see cref="MaxTile"/> (Ctrl+mouse wheel changes it).</summary>
    public double TileSize
    {
        get => GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    /// <summary>Check marks instead of selection: a click toggles the page (choosing pages to insert).</summary>
    public bool CheckMode
    {
        get => GetValue(CheckModeProperty);
        set => SetValue(CheckModeProperty, value);
    }

    /// <summary>Reordering: selected pages are dragged within the tiles; a marker shows where they will land (<see cref="PagesReordered"/>).</summary>
    public bool ReorderMode
    {
        get => GetValue(ReorderModeProperty);
        set => SetValue(ReorderModeProperty, value);
    }

    /// <summary>Reordering: the selected pages were dropped before the position with this index (0 … count).</summary>
    public event Action<int>? PagesReordered;

    /// <summary>Raised when the total height or the visible section has changed (for the scroll bar).</summary>
    public event Action? ScrollChanged;

    /// <summary>Raised when the selection has changed.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised when the symbol at the top right of a tile (<see cref="PageViewModel.Badge"/>) is clicked; the selection stays.</summary>
    public event Action<int>? BadgeClicked;

    /// <summary>The symbol of a tile: a circle of 20 px at the top right of its page.</summary>
    private Rect BadgeRect(int index)
    {
        var page = Pages![index];
        var cell = CellRect(index);
        var scale = Math.Min(TileSize / page.Page.Width, TileSize / page.Page.Height);
        var (pw, ph) = (Math.Max(4, page.Page.Width * scale), Math.Max(4, page.Page.Height * scale));
        var pageRect = new Rect(cell.X + Pad + (TileSize - pw) / 2, cell.Y + Pad + (TileSize - ph) / 2, pw, ph);
        return new Rect(pageRect.Right - 24, pageRect.Top + 4, 20, 20);
    }

    /// <summary>Raised while selected pages are being dragged (pointer relative to this control).</summary>
    public event Action<Point>? PagesDragging;

    /// <summary>Raised when dragged pages have been dropped (pointer relative to this control).</summary>
    public event Action<Point>? PagesDropped;

    /// <summary>Vertical scroll offset in user interface units, clamped to the scrollable range.</summary>
    public double Offset
    {
        get => _offset;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, ExtentHeight - Bounds.Height));
            if (Math.Abs(clamped - _offset) < 0.01) return;
            _offset = clamped;
            ScrollChanged?.Invoke();
            InvalidateVisual();
        }
    }

    /// <summary>Gets the total height of all rows in user interface units.</summary>
    public double ExtentHeight => Layout.Extent;

    private int Count => Pages?.Count ?? 0;

    private double CellWidth => TileSize + 2 * Pad;

    private double CellHeight => TileSize + Label + 2 * Pad;

    private int Columns => Math.Max(1, (int)((Bounds.Width - Pad) / CellWidth));

    private double Left => Math.Max(Pad / 2, (Bounds.Width - Columns * CellWidth) / 2);

    /// <summary>The band above a group (<see cref="PageViewModel.GroupHeader"/>): a small gap and the hint.</summary>
    private const double HeaderHeight = 30;

    /// <summary>
    /// The rows of the tiles: the first page and the number of pages of each row and its top (without the scroll offset), the bands of the
    /// groups, and the total height. A page that starts a group starts a new row below its band; without groups every row is full.
    /// </summary>
    private sealed record GridLayout(int[] RowStart, int[] RowCount, double[] RowTop, (double Top, string Text)[] Headers, double Extent);

    private GridLayout? _layout;
    private (IReadOnlyList<PageViewModel>? Pages, int Count, int Columns, double TileSize) _layoutKey;

    private GridLayout Layout
    {
        get
        {
            var key = (Pages, Count, Columns, TileSize);
            if (_layout is { } known && ReferenceEquals(_layoutKey.Pages, key.Pages) && _layoutKey.Count == key.Count
                && _layoutKey.Columns == key.Columns && _layoutKey.TileSize.Equals(key.TileSize))
                return known;
            var (starts, counts, tops, headers) = (new List<int>(), new List<int>(), new List<double>(), new List<(double, string)>());
            var y = Pad / 2;
            for (var i = 0; i < key.Count;)
            {
                if (key.Pages![i].GroupHeader is { } header)
                {
                    headers.Add((y, header));
                    y += HeaderHeight;
                }
                // a row: up to the columns, ending before the next group
                var n = 1;
                while (n < key.Columns && i + n < key.Count && key.Pages[i + n].GroupHeader is null) n++;
                starts.Add(i);
                counts.Add(n);
                tops.Add(y);
                y += CellHeight;
                i += n;
            }
            _layoutKey = key;
            return _layout = new GridLayout([.. starts], [.. counts], [.. tops], [.. headers], y + Pad / 2);
        }
    }

    /// <summary>The row of a page (the last row starting at or before it).</summary>
    private static int RowOf(GridLayout layout, int index)
    {
        var row = Array.BinarySearch(layout.RowStart, index);
        return row >= 0 ? row : ~row - 1;
    }

    /// <summary>The last row whose top lies at or above <paramref name="y"/> (document coordinates); -1 above the first.</summary>
    private static int RowAtY(GridLayout layout, double y)
    {
        var row = Array.BinarySearch(layout.RowTop, y);
        return row >= 0 ? row : ~row - 1;
    }

    /// <summary>Returns the cell of page <paramref name="index"/> in control coordinates.</summary>
    /// <param name="index">Page index.</param>
    /// <returns>The cell rectangle, taking the scroll offset into account.</returns>
    public Rect CellRect(int index)
    {
        var layout = Layout;
        if (layout.RowStart.Length == 0) return new Rect(Left, Pad / 2 - _offset, CellWidth, CellHeight);
        var row = Math.Max(0, RowOf(layout, index));
        return new Rect(Left + (index - layout.RowStart[row]) * CellWidth, layout.RowTop[row] - _offset, CellWidth, CellHeight);
    }

    /// <summary>Returns the page under a point.</summary>
    /// <param name="p">Point in control coordinates.</param>
    /// <returns>The page index, or -1 outside the pages (also in the band above a group).</returns>
    public int IndexAt(Point p)
    {
        var layout = Layout;
        var y = p.Y + _offset;
        var row = RowAtY(layout, y);
        if (row < 0 || y >= layout.RowTop[row] + CellHeight) return -1;
        var column = (int)Math.Floor((p.X - Left) / CellWidth);
        return column >= 0 && column < layout.RowCount[row] ? layout.RowStart[row] + column : -1;
    }

    /// <summary>
    /// Reordering: insertion point under the pointer, i.e. the gap nearest to it (left half of a tile: before it,
    /// right half: after it); below the last row: at the end; in the band above a group: before its first page. <paramref name="afterPrevious"/>:
    /// the position lies at the end of a row.
    /// </summary>
    /// <param name="p">Point in control coordinates.</param>
    /// <param name="afterPrevious">Set to true if the marker belongs on the right of the tile before (end of a row or of the list).</param>
    /// <returns>Insertion index (0 … count).</returns>
    public int InsertionAt(Point p, out bool afterPrevious)
    {
        afterPrevious = false;
        if (Count == 0) return 0;
        var layout = Layout;
        var y = p.Y + _offset;
        var row = RowAtY(layout, y);
        if (row < 0) return 0;
        if (y >= layout.RowTop[row] + CellHeight)
        {
            if (row == layout.RowStart.Length - 1)
            {
                afterPrevious = true;
                return Count;
            }
            return layout.RowStart[row + 1];
        }
        var gap = Math.Clamp((int)Math.Floor((p.X - Left) / CellWidth + 0.5), 0, layout.RowCount[row]);
        afterPrevious = gap == layout.RowCount[row];
        return layout.RowStart[row] + gap;
    }

    /// <summary>Scrolls a page into view.</summary>
    /// <param name="index">Page index; out-of-range values are ignored.</param>
    public void ScrollIntoView(int index)
    {
        if (index < 0 || index >= Count) return;
        var rect = CellRect(index);
        if (rect.Top < 0) Offset += rect.Top;
        else if (rect.Bottom > Bounds.Height) Offset += rect.Bottom - Bounds.Height;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RendererProperty) Subscribe();
        if (change.Property == BoundsProperty || change.Property == TileSizeProperty || change.Property == PagesProperty)
        {
            _offset = Math.Clamp(_offset, 0, Math.Max(0, ExtentHeight - Bounds.Height));
            ScrollChanged?.Invoke();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribed is not null) _subscribed.TileReady -= OnTileReady;
        _subscribed?.Request("raster", []);
        _subscribed = null;
    }

    private void Subscribe()
    {
        if (_subscribed is not null) _subscribed.TileReady -= OnTileReady;
        _subscribed = VisualRoot is null ? null : Renderer;
        if (_subscribed is not null) _subscribed.TileReady += OnTileReady;
    }

    private void OnTileReady(TileKey key)
    {
        if (_invalidatePosted) return;
        _invalidatePosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _invalidatePosted = false;
            InvalidateVisual();
        }, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------ Drawing

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static readonly BoxShadows Shadow = BoxShadows.Parse("0 1 4 0 #33101828");

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Pages is not { } pages || Renderer is not { } renderer) return;

        var accent = Brush("AccentBrush", Brushes.RoyalBlue);
        if (pages.Count == 0)
        {
            // empty, pages dragged in: the marker where the first one lands
            if (_dropAt >= 0) context.FillRectangle(accent, new Rect(Left + Pad / 2, Pad / 2, 4, CellHeight - Pad), 2);
            return;
        }
        var soft = Brush("AccentSoftBrush", Brushes.AliceBlue);
        var text = Brush("TextBrush", Brushes.Black);
        var muted = Brush("TextMutedBrush", Brushes.Gray);
        var line = new Pen(Brush("LineStrongBrush", Brushes.LightGray), 1);
        var onAccent = Brush("OnAccentBrush", Brushes.White);
        var typeface = new Typeface(FontFamily.Default);

        var layout = Layout;
        var rows = layout.RowStart.Length;
        var firstRow = Math.Max(0, RowAtY(layout, _offset));
        var lastRow = Math.Min(rows - 1, RowAtY(layout, _offset + Bounds.Height));
        var draws = new List<SkiaImageDraw>();
        var wanted = new List<TileKey>();
        var checks = new List<Rect>();

        // the bands above the groups: a thin line and the hint, muted
        foreach (var (top, hint) in layout.Headers)
        {
            var y = top - _offset;
            if (y > Bounds.Height || y + HeaderHeight < 0) continue;
            var label = new FormattedText(hint, Tr.Culture, FlowDirection.LeftToRight, typeface, 12, muted);
            var x = Left + Pad;
            context.DrawText(label, new Point(x, y + HeaderHeight - Pad / 2 - label.Height));
            var lineY = y + HeaderHeight - Pad / 2 - label.Height / 2;
            var right = Left + Columns * CellWidth - Pad;
            if (x + label.Width + 8 < right) context.DrawLine(line, new Point(x + label.Width + 8, lineY), new Point(right, lineY));
        }

        // Request one screen ahead downwards, so that the images are already there when scrolling.
        var aheadRows = Math.Max(1, (int)(Bounds.Height / CellHeight));
        for (var row = firstRow; row <= Math.Min(rows - 1, lastRow + aheadRows); row++)
        for (var column = 0; column < layout.RowCount[row]; column++)
        {
            var index = layout.RowStart[row] + column;
            var page = pages[index];
            var key = new TileKey(page.Key, renderer.OverviewLevel(page.Key), 0, 0, 0);
            var image = renderer.TryGetTile(key);
            if (image is null) wanted.Add(key);
            if (row > lastRow) continue;

            var cell = CellRect(index);
            if (page.IsSelected && !CheckMode) context.FillRectangle(soft, cell.Deflate(2), 8);

            // Fit the page into the square.
            var (w, h) = (page.Page.Width, page.Page.Height);
            var scale = Math.Min(TileSize / w, TileSize / h);
            var pw = Math.Max(4, w * scale);
            var ph = Math.Max(4, h * scale);
            var pageRect = new Rect(cell.X + Pad + (TileSize - pw) / 2, cell.Y + Pad + (TileSize - ph) / 2, pw, ph);
            context.DrawRectangle(Brushes.White, line, new RoundedRect(pageRect, 2), Shadow);
            if (image is not null)
                draws.Add(new SkiaImageDraw(image, SKRect.Create(image.Width, image.Height),
                    new SKRect((float)pageRect.Left, (float)pageRect.Top, (float)pageRect.Right, (float)pageRect.Bottom)));
            if (page.IsSelected && CheckMode) checks.Add(pageRect);
            else if (page.IsSelected) context.DrawRectangle(null, new Pen(accent, 2), new RoundedRect(pageRect.Inflate(2), 3));

            // Marks (parts when splitting) as coloured bars below the page
            if (page.Marks is { Count: > 0 } marks)
            {
                var width = Math.Min(pw, marks.Count * 18);
                var each = width / marks.Count;
                for (var m = 0; m < marks.Count; m++)
                    context.FillRectangle(new SolidColorBrush(marks[m]),
                        new Rect(pageRect.Center.X - width / 2 + m * each + 1, pageRect.Bottom + 3, Math.Max(2, each - 2), MarkHeight), 2);
            }

            var number = new FormattedText(page.Number.ToString(Tr.Culture), Tr.Culture, FlowDirection.LeftToRight, typeface, 12,
                page.IsSelected ? text : muted);
            context.DrawText(number, new Point(cell.Center.X - number.Width / 2, cell.Bottom - Pad - Label + 6));
        }

        if (draws.Count > 0) context.Custom(new SkiaImageDrawing(new Rect(Bounds.Size), draws));

        // symbols at the top right of the tiles (above the images): accent when applied, muted when discarded
        if (this.TryFindResource("Icon.ArrowReset", ActualThemeVariant, out var icon) && icon is Geometry badgeIcon)
            for (var row = firstRow; row <= lastRow; row++)
                for (var column = 0; column < layout.RowCount[row]; column++)
                {
                    var index = layout.RowStart[row] + column;
                    if (pages[index].Badge == PageBadge.None) continue;
                    var circle = BadgeRect(index);
                    var on = pages[index].Badge == PageBadge.Applied;
                    context.DrawEllipse(on ? accent : Brushes.White, new Pen(on ? onAccent : line.Brush, 1.5), circle);
                    var bounds = badgeIcon.Bounds;
                    var size = 12 / Math.Max(bounds.Width, bounds.Height);
                    using (context.PushTransform(Matrix.CreateTranslation(-bounds.X, -bounds.Y) * Matrix.CreateScale(size, size)
                               * Matrix.CreateTranslation(circle.Center.X - bounds.Width * size / 2, circle.Center.Y - bounds.Height * size / 2)))
                        context.DrawGeometry(on ? onAccent : muted, null, badgeIcon);
                }

        // Check marks above the images
        foreach (var rect in checks)
        {
            context.DrawRectangle(null, new Pen(accent, 2), new RoundedRect(rect.Inflate(2), 3));
            var circle = new Rect(rect.Right - 22, rect.Top + 4, 18, 18);
            context.DrawEllipse(accent, new Pen(onAccent, 1.5), circle);
            var tick = new StreamGeometry();
            using (var g = tick.Open())
            {
                g.BeginFigure(new Point(circle.X + 4.5, circle.Y + 9.5), false);
                g.LineTo(new Point(circle.X + 7.8, circle.Y + 12.8));
                g.LineTo(new Point(circle.X + 13.5, circle.Y + 6));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(onAccent, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), tick);
        }

        if (_band is { } band)
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0x3D, 0x7E, 0xFF)), new Pen(accent, 1), band);
        }

        // Reordering, or pages dragged in from another grid: vertical marker at the insertion point
        if (_dragging && ReorderMode && _insertAt >= 0) DrawInsertion(_insertAt, _insertAfterPrevious);
        else if (_dropAt >= 0) DrawInsertion(_dropAt, _dropAfterPrevious);

        void DrawInsertion(int at, bool afterPrevious)
        {
            var previous = afterPrevious && at > 0;
            var cell = CellRect(previous ? at - 1 : Math.Min(at, pages.Count - 1));
            var x = previous || at >= pages.Count ? cell.Right : cell.Left;
            context.FillRectangle(accent, new Rect(x - 2, cell.Top + Pad / 2, 4, cell.Height - Pad), 2);
        }
        renderer.Request(Channel, wanted);
    }

    // ------------------------------------------------------------ Interaction

    private void SetSelection(Func<int, bool> selected)
    {
        if (Pages is not { } pages) return;
        for (var i = 0; i < pages.Count; i++) pages[i].IsSelected = selected(i);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Pages is not { } pages) return;
        var position = e.GetPosition(this);
        var index = IndexAt(position);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _pressAt = position;
        _pressedIndex = index;
        _deferredSingle = false;
        e.Pointer.Capture(this);
        e.Handled = true;

        if (index >= 0 && pages[index].Badge != PageBadge.None && BadgeRect(index).Inflate(2).Contains(position))
        {
            // a click on the symbol neither selects nor starts dragging
            (_pressAt, _pressedIndex) = (null, -1);
            e.Pointer.Capture(null);
            BadgeClicked?.Invoke(index);
            InvalidateVisual();
            return;
        }

        if (index < 0)
        {
            // Drag a frame; with Ctrl, add to the existing selection.
            _band = new Rect(position, position);
            _bandBase = ctrl ? [.. Enumerable.Range(0, pages.Count).Where(i => pages[i].IsSelected)] : [];
            if (!ctrl) SetSelection(_ => false);
            return;
        }

        if (shift && _anchor >= 0)
        {
            int from = Math.Min(_anchor, index), to = Math.Max(_anchor, index);
            var keep = ctrl || CheckMode;
            var state = !CheckMode || pages[_anchor].IsSelected;
            SetSelection(i => i >= from && i <= to ? state : keep && pages[i].IsSelected);
            return;
        }
        if (CheckMode || ctrl)
        {
            pages[index].IsSelected = !pages[index].IsSelected;
            _anchor = index;
            SelectionChanged?.Invoke();
            InvalidateVisual();
            return;
        }
        // Click on one of several selected pages: select it alone only on release (otherwise not all of them could be dragged).
        if (pages[index].IsSelected) _deferredSingle = true;
        else SetSelection(i => i == index);
        _anchor = index;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressAt is null) ShowBadgeTip(e.GetPosition(this));
        if (_pressAt is not { } start || Pages is not { } pages) return;
        var position = e.GetPosition(this);
        _lastPointer = position;

        if (_band is not null)
        {
            _band = new Rect(start.WithY(start.Y), position).Normalize();
            UpdateBand();
            RunAutoScroll();
            return;
        }
        if (_pressedIndex < 0 || CheckMode || (PagesDragging is null && !ReorderMode)) return;
        if (!_dragging)
        {
            var delta = position - start;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 6 || !pages[_pressedIndex].IsSelected) return;
            _dragging = true;
            _deferredSingle = false;
            Cursor = new Cursor(StandardCursorType.DragMove);
        }
        if (ReorderMode)
        {
            UpdateInsertion();
            RunAutoScroll();
            return;
        }
        PagesDragging?.Invoke(position);
    }

    private int _tipIndex = -1;

    /// <summary>The tooltip of the symbol under the pointer (<see cref="PageViewModel.BadgeTip"/>); elsewhere none.</summary>
    private void ShowBadgeTip(Point position)
    {
        var index = IndexAt(position);
        var over = index >= 0 && Pages is { } pages && pages[index].Badge != PageBadge.None && pages[index].BadgeTip is not null
            && BadgeRect(index).Inflate(2).Contains(position) ? index : -1;
        if (over == _tipIndex) return;
        _tipIndex = over;
        ToolTip.SetTip(this, over >= 0 ? Pages![over].BadgeTip : null);
        ToolTip.SetIsOpen(this, over >= 0);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _tipIndex = -1;
        ToolTip.SetTip(this, null);
        ToolTip.SetIsOpen(this, false);
    }

    private void UpdateInsertion()
    {
        _insertAt = InsertionAt(_lastPointer, out _insertAfterPrevious);
        InvalidateVisual();
    }

    private void UpdateBand()
    {
        if (_band is not { } band || Pages is not { } pages) return;
        var hit = new HashSet<int>(_bandBase ?? []);
        for (var i = 0; i < pages.Count; i++)
        {
            var cell = CellRect(i);
            if (cell.Bottom < 0) continue;
            if (cell.Top > Bounds.Height) break;
            if (cell.Deflate(Pad).Intersects(band)) hit.Add(i);
        }
        SetSelection(hit.Contains);
    }

    /// <summary>Scrolls along while a frame or (when reordering) dragged pages are at the top or bottom edge.</summary>
    private async void RunAutoScroll()
    {
        if (_autoScrolling) return;
        _autoScrolling = true;
        try
        {
            while (_band is not null || (_dragging && ReorderMode))
            {
                var speed = _lastPointer.Y < 24 ? -Math.Clamp(24 - _lastPointer.Y, 4, 40)
                    : _lastPointer.Y > Bounds.Height - 24 ? Math.Clamp(_lastPointer.Y - Bounds.Height + 24, 4, 40) : 0;
                if (speed != 0)
                {
                    var before = _offset;
                    Offset += speed;
                    if (_band is { } band && Math.Abs(before - _offset) > 0)
                    {
                        // The frame is attached to the document: move its start along.
                        _pressAt = _pressAt!.Value.WithY(_pressAt.Value.Y - (_offset - before));
                        _band = new Rect(_pressAt.Value, _lastPointer).Normalize();
                        UpdateBand();
                    }
                    else if (_dragging && ReorderMode)
                    {
                        UpdateInsertion();
                    }
                }
                await Task.Delay(16);
            }
        }
        finally
        {
            _autoScrolling = false;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // Evaluate first, then release the pointer (releasing cancels a running drag).
        var dragging = _dragging;
        var single = _deferredSingle && _pressedIndex >= 0 ? _pressedIndex : -1;
        var insertAt = _insertAt;
        _insertAt = -1;
        _dragging = false;
        _band = null;
        _bandBase = null;
        _pressAt = null;
        _deferredSingle = false;
        Cursor = null;
        e.Pointer.Capture(null);
        if (dragging && ReorderMode)
        {
            if (insertAt >= 0) PagesReordered?.Invoke(insertAt);
        }
        else if (dragging)
        {
            PagesDropped?.Invoke(e.GetPosition(this));
        }
        else if (single >= 0)
        {
            SetSelection(i => i == single);
        }
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _band = null;
        _pressAt = null;
        _insertAt = -1;
        if (_dragging)
        {
            _dragging = false;
            Cursor = null;
        }
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Change the size and keep the page under the pointer in view.
            var index = IndexAt(e.GetPosition(this));
            TileSize *= Math.Pow(1.12, e.Delta.Y);
            if (index >= 0) ScrollIntoView(index);
        }
        else
        {
            Offset -= e.Delta.Y * 80;
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SetSelection(_ => true);
            e.Handled = true;
        }
        else if (e.Key is Key.PageDown or Key.PageUp)
        {
            Offset += (e.Key == Key.PageDown ? 1 : -1) * Bounds.Height * 0.9;
            e.Handled = true;
        }
    }
}

/// <summary><see cref="PageGrid"/> with a scroll bar.</summary>
public sealed class PageGridView : Grid
{
    private readonly ScrollBar _bar = new() { Orientation = Orientation.Vertical, AllowAutoHide = false };
    private bool _syncing;

    public PageGridView()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        Children.Add(Grid);
        SetColumn(_bar, 1);
        Children.Add(_bar);
        Grid.ScrollChanged += Sync;
        _bar.ValueChanged += (_, e) =>
        {
            if (!_syncing) Grid.Offset = e.NewValue;
        };
    }

    /// <summary>Gets the inner page grid.</summary>
    public PageGrid Grid { get; } = new();

    private void Sync()
    {
        _syncing = true;
        var height = Grid.Bounds.Height;
        _bar.Maximum = Math.Max(0, Grid.ExtentHeight - height);
        _bar.ViewportSize = height;
        _bar.LargeChange = height * 0.9;
        _bar.SmallChange = 60;
        _bar.Value = Grid.Offset;
        _bar.IsVisible = Grid.ExtentHeight > height + 0.5;
        _syncing = false;
    }
}
