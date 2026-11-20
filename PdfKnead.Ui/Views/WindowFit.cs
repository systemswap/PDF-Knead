using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace PdfKnead.Views;

/// <summary>Large windows (compress, merge) open no larger than the screen's working area, with the title bar visible.</summary>
internal static class WindowFit
{
    /// <summary>Fits the window to the screen once it has opened (after centring over the program, which happens after Opened).</summary>
    /// <param name="window">The window.</param>
    public static void ToScreenWhenOpened(Window window) =>
        window.Opened += (_, _) => Dispatcher.UIThread.Post(() => ToScreen(window), DispatcherPriority.Background);

    /// <summary>Keeps the window no larger than the screen's working area, with the title bar visible.</summary>
    /// <param name="window">The window.</param>
    public static void ToScreen(Window window)
    {
        // Called with a delay: the window may already be closed again (quick closing, tests), and then there is no screen any more.
        if (!window.IsVisible || window.Screens.ScreenFromWindow(window) is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        // Title bar and frame are not part of Width/Height (a probe measured 1,000 px of content but a 1,038 px window).
        var frame = window.FrameSize ?? window.ClientSize;
        var (extraWidth, extraHeight) = (Math.Max(0, frame.Width - window.ClientSize.Width), Math.Max(0, frame.Height - window.ClientSize.Height));
        var (maxWidth, maxHeight) = (area.Width / scale - extraWidth, area.Height / scale - extraHeight);
        if (window.Width > maxWidth) window.Width = Math.Max(window.MinWidth, maxWidth);
        if (window.Height > maxHeight) window.Height = Math.Max(window.MinHeight, maxHeight);
        // Centred over the program, the window can stick out at the top or bottom: then move it back to the edge.
        var (x, y) = (window.Position.X, window.Position.Y);
        var right = area.X + area.Width - (int)Math.Ceiling((window.Width + extraWidth) * scale);
        var bottom = area.Y + area.Height - (int)Math.Ceiling((window.Height + extraHeight) * scale);
        window.Position = new PixelPoint(Math.Clamp(x, area.X, Math.Max(area.X, right)), Math.Clamp(y, area.Y, Math.Max(area.Y, bottom)));
    }
}
