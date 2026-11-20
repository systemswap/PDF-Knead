using Avalonia.Controls;
using Avalonia.VisualTree;

namespace PdfKnead.Views;

/// <summary>
/// Stops the animations of a window when it closes. An indeterminate progress bar keeps running on Avalonia's shared clock even after
/// its window is closed; through the animation, the window, its view model and the whole document stay in memory (soak test: +15 MB
/// managed, +25 MB process per opening of the compress or print window).
/// </summary>
internal static class WindowAnimations
{
    /// <summary>Stops all indeterminate progress bars of the window when it closes.</summary>
    /// <param name="window">The window whose progress bars are stopped on <see cref="Window.Closed"/>.</param>
    public static void StopOnClose(Window window) =>
        window.Closed += (_, _) =>
        {
            foreach (var bar in window.GetVisualDescendants().OfType<ProgressBar>()) bar.IsIndeterminate = false;
        };
}
