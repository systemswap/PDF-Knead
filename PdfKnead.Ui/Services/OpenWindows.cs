using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using PdfKnead.ViewModels;

namespace PdfKnead.Services;

/// <summary>The main windows of this run, for what concerns all of them (updates: „Jetzt neu starten“ only without unsaved changes).</summary>
public static class OpenWindows
{
    /// <summary>The view models of the open main windows.</summary>
    public static IEnumerable<MainViewModel> Main =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.Select(w => w.DataContext).OfType<MainViewModel>()
            : [];

    /// <summary>Whether a document in any open window has unsaved changes.</summary>
    public static bool AnyUnsaved() => Main.Any(vm => vm.Documents.Any(d => d.IsModified));

    /// <summary>Ends the program (all windows) — after the new version took over.</summary>
    public static void ShutDown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
