using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SkiaSharp;

namespace PdfKnead.Controls;

/// <summary>Pages of a booklet in reading order (the images are created in the background).</summary>
public interface IBookPages
{
    /// <summary>Gets the number of pages including the blank pages (a multiple of 4).</summary>
    int PageCount { get; }

    /// <summary>Gets the size of a booklet page in points.</summary>
    (double Width, double Height) PageSize { get; }

    /// <summary>Returns the image of the page once it is finished; until then null (loading starts with the first call).</summary>
    /// <param name="page">Page index in reading order, starting at 0.</param>
    /// <returns>The image, or null while it is not ready yet.</returns>
    SKImage? Get(int page);

    /// <summary>Raised when an image has been finished (possibly on a background thread).</summary>
    event Action? Changed;
}

/// <summary>
/// Preview of a booklet like a book in the hand: first only the front cover; when turning pages, the page turns around the fold and the
/// book lies open with two pages, and finally the back cover alone. A click on the right half turns forward, on the left half
/// back (reversed with right binding).
/// </summary>
public sealed class BookletPreview : Control
{
    public static readonly StyledProperty<IBookPages?> PagesProperty = AvaloniaProperty.Register<BookletPreview, IBookPages?>(nameof(Pages));

    public static readonly StyledProperty<int> SpreadProperty =
        AvaloniaProperty.Register<BookletPreview, int>(nameof(Spread), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> RightBindingProperty = AvaloniaProperty.Register<BookletPreview, bool>(nameof(RightBinding));

    /// <summary>Duration of one page turn.</summary>
    private static readonly TimeSpan TurnTime = TimeSpan.FromMilliseconds(520);

    private static readonly BoxShadows Shadow = BoxShadows.Parse("0 3 14 0 #48000000");

    private IBookPages? _subscribed;
    private int _from;
    private DateTime _turnStart;
    private bool _turning;
    private double? _frozen;

    protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new PreviewAutomationPeer(this);

    static BookletPreview()
    {
        AffectsRender<BookletPreview>(PagesProperty, RightBindingProperty);
        FocusableProperty.OverrideDefaultValue<BookletPreview>(true);
    }

    public IBookPages? Pages
    {
        get => GetValue(PagesProperty);
        set => SetValue(PagesProperty, value);
    }

    /// <summary>Open spread: 0 = closed (front cover), then pages 2|3, 4|5 … (counted from 1); the last one shows the back cover alone.</summary>
    public int Spread
    {
        get => GetValue(SpreadProperty);
        set => SetValue(SpreadProperty, value);
    }

    public bool RightBinding
    {
        get => GetValue(RightBindingProperty);
        set => SetValue(RightBindingProperty, value);
    }

    /// <summary>Returns the number of spreads (including the front and back cover shown alone).</summary>
    /// <param name="pages">Number of booklet pages.</param>
    /// <returns>pages / 2 + 1, or 0 without pages.</returns>
    public static int SpreadCount(int pages) => pages <= 0 ? 0 : pages / 2 + 1;

    /// <summary>Returns the pages of a spread (left, right, for left binding; −1 = none).</summary>
    /// <param name="spread">Index of the spread.</param>
    /// <param name="pages">Number of booklet pages.</param>
    /// <returns>Zero-based page indices of the left and right page.</returns>
    public static (int Left, int Right) PagesOf(int spread, int pages) =>
        (spread * 2 - 1 is var l && l >= 0 && l < pages ? l : -1, spread * 2 < pages ? spread * 2 : -1);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PagesProperty)
        {
            if (_subscribed is not null) _subscribed.Changed -= OnPagesChanged;
            _subscribed = Pages;
            if (_subscribed is not null) _subscribed.Changed += OnPagesChanged;
            _turning = false;
        }
        else if (change.Property == SpreadProperty)
        {
            var (old, now) = ((int)change.OldValue!, (int)change.NewValue!);
            _frozen = null;
            // Only single steps are animated; jumps (rebuild, far away) show the target at once.
            if (Math.Abs(now - old) == 1 && VisualRoot is not null)
            {
                _from = old;
                _turnStart = DateTime.UtcNow;
                if (!_turning)
                {
                    _turning = true;
                    TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
                }
            }
            else
            {
                _turning = false;
            }
            InvalidateVisual();
            Prefetch(now);
        }
    }

    /// <summary>For tests: freezes the turn from <paramref name="from"/> to the current spread at <paramref name="progress"/> (0 … 1).</summary>
    internal void FreezeTurn(int from, double progress)
    {
        _from = from;
        _frozen = progress;
        _turning = true;
        InvalidateVisual();
    }

    /// <summary>Starts loading the pages of the neighbouring spreads, so that they are ready when turning pages.</summary>
    private void Prefetch(int spread)
    {
        if (Pages is not { } pages) return;
        foreach (var s in new[] { spread - 1, spread + 1 })
        {
            if (s < 0 || s >= SpreadCount(pages.PageCount)) continue;
            var (left, right) = PagesOf(s, pages.PageCount);
            if (left >= 0) pages.Get(left);
            if (right >= 0) pages.Get(right);
        }
    }

    private void OnPagesChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);

    private void OnFrame(TimeSpan _)
    {
        if (!_turning) return;
        if (DateTime.UtcNow - _turnStart >= TurnTime) _turning = false;
        else TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Pages is not { } pages || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var forward = e.GetPosition(this).X >= Bounds.Width / 2 != RightBinding;
        var next = Spread + (forward ? 1 : -1);
        if (next >= 0 && next < SpreadCount(pages.PageCount)) Spread = next;
        e.Handled = true;
    }

    /// <summary>Arrow keys turn pages in reading direction (with right binding: left = forward).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Pages is not { } pages || e.Key is not (Key.Left or Key.Right)) return;
        var next = Spread + ((e.Key == Key.Right) != RightBinding ? 1 : -1);
        if (next >= 0 && next < SpreadCount(pages.PageCount)) Spread = next;
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        if (Pages is not { PageCount: > 0 } pages) return;
        var (pw, ph) = pages.PageSize;
        if (!(pw > 0) || !(ph > 0)) return;
        const double pad = 28;
        var scale = Math.Min(Math.Max(1, Bounds.Width - 2 * pad) / (2 * pw), Math.Max(1, Bounds.Height - 2 * pad) / ph);
        var (w, h) = (pw * scale, ph * scale);
        var top = (Bounds.Height - h) / 2;
        var center = Bounds.Width / 2;
        var count = pages.PageCount;
        var spread = Math.Clamp(Spread, 0, SpreadCount(count) - 1);

        // Fold: when closed, the front cover lies centred, and at the end the back cover.
        double SpineOf(int s) => s == 0 ? center - w / 2 : s == SpreadCount(count) - 1 ? center + w / 2 : center;

        var t = !_turning ? 1 : _frozen ?? Math.Clamp((DateTime.UtcNow - _turnStart) / TurnTime, 0, 1);
        var eased = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
        var draws = new List<(int Page, Rect Rect, double Shade, bool Turning)>();

        if (t >= 1 || _from == spread)
        {
            var spine = SpineOf(spread);
            var (left, right) = PagesOf(spread, count);
            if (left >= 0) draws.Add((left, new Rect(spine - w, top, w, h), 0, false));
            if (right >= 0) draws.Add((right, new Rect(spine, top, w, h), 0, false));
        }
        else
        {
            var forward = spread > _from;
            var spine = SpineOf(_from) + (SpineOf(spread) - SpineOf(_from)) * eased;
            var (oldLeft, oldRight) = PagesOf(_from, count);
            var (newLeft, newRight) = PagesOf(spread, count);
            // Pages that stay: forward, the old left one on the left and the new right one on the right (visible underneath); backward the other way round.
            var (staticLeft, staticRight) = forward ? (oldLeft, newRight) : (newLeft, oldRight);
            if (staticLeft >= 0) draws.Add((staticLeft, new Rect(spine - w, top, w, h), 0, false));
            if (staticRight >= 0) draws.Add((staticRight, new Rect(spine, top, w, h), 0, false));
            // The turning sheet: first its front getting narrower, then its back getting wider on the other side.
            var (front, back) = forward ? (oldRight, newLeft) : (oldLeft, newRight);
            var width = w * Math.Abs(Math.Cos(Math.PI * eased));
            var shade = 0.2 * Math.Sin(Math.PI * eased);
            var firstHalf = eased < 0.5;
            var page = firstHalf ? front : back;
            var onRight = forward == firstHalf;
            if (page >= 0 && width > 0.5)
                draws.Add((page, onRight ? new Rect(spine, top, width, h) : new Rect(spine - width, top, width, h), shade, true));
        }

        // Right binding: mirror everything at the centre (but not the images themselves).
        Rect Place(Rect r) => RightBinding ? new Rect(2 * center - r.Right, r.Y, r.Width, r.Height) : r;

        // The pages lying flat cast their shadows first, so that one does not darken the other beside it; the turning sheet casts its
        // shadow onto the pages underneath.
        foreach (var flat in draws.Where(d => !d.Turning)) context.DrawRectangle(Brushes.White, null, new RoundedRect(Place(flat.Rect)), Shadow);
        foreach (var (page, raw, shade, turning) in draws)
        {
            var rect = Place(raw);
            if (turning) context.DrawRectangle(Brushes.White, null, new RoundedRect(rect), Shadow);
            if (pages.Get(page) is { } image)
                context.Custom(new SkiaImageDrawing(rect, [new SkiaImageDraw(image, new SKRect(0, 0, image.Width, image.Height),
                    new SKRect((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom))]));
            if (shade > 0.01) context.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(shade * 255), 0, 0, 0)), rect);
        }
        // Fold when the book lies open: the paper bends down into the gutter, so both pages darken softly towards it; a thin line marks the fold.
        if (!_turning && PagesOf(spread, count) is (>= 0, >= 0))
        {
            var x = Place(new Rect(SpineOf(spread), top, 0, h)).X;
            var reach = Math.Max(6, w * FoldShadowReach);
            context.FillRectangle(FoldShadow(towardsRight: true), new Rect(x - reach, top, reach, h));
            context.FillRectangle(FoldShadow(towardsRight: false), new Rect(x, top, reach, h));
            context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), 1), new Point(x, top), new Point(x, top + h));
        }
    }

    /// <summary>Width of the shadow at the fold on each page, as a share of the page width.</summary>
    internal const double FoldShadowReach = 0.06;

    /// <summary>Darkness of the shadow right at the fold (alpha of black).</summary>
    internal const byte FoldShadowAlpha = 46;

    /// <summary>The shadow of one page at the fold: clear at the outer side, darkest at the fold, falling off faster near the fold than further out.</summary>
    /// <param name="towardsRight">True for the left page (the fold lies to its right).</param>
    /// <returns>A horizontal gradient over the strip next to the fold.</returns>
    private static LinearGradientBrush FoldShadow(bool towardsRight)
    {
        var stops = new GradientStops
        {
            new(Color.FromArgb(0, 0, 0, 0), 0),
            new(Color.FromArgb(FoldShadowAlpha / 5, 0, 0, 0), 0.55),
            new(Color.FromArgb(FoldShadowAlpha / 2, 0, 0, 0), 0.85),
            new(Color.FromArgb(FoldShadowAlpha, 0, 0, 0), 1),
        };
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(towardsRight ? 0 : 1, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(towardsRight ? 1 : 0, 0.5, RelativeUnit.Relative),
            GradientStops = stops,
        };
    }
}
