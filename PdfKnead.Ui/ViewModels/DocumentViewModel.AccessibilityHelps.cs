using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Rendering;

namespace PdfKnead.ViewModels;

/// <summary>What the pages show to help answer a question of the human check.</summary>
public enum AccessibilityHelp
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>M1: the reading order — numbered boxes in the order of the structure.</summary>
    ReadingOrder,

    /// <summary>M2: the figures and formulas with their alternative texts.</summary>
    Figures,

    /// <summary>M6: what is painted as an artifact.</summary>
    Artifacts,
}

/// <summary>
/// The helps of the human check on the pages (docs/ABLAUFPLAN.md, 6.7 step 5, M1, M2, M6; docs/FEHLERGEDAECHTNIS.md, No. 670): one at a time,
/// switched on at its question; per page worked out in the background when the page comes into view (<see cref="ReadingOrder"/>), as the file
/// has it. A change of the document or closing the area ends it.
/// </summary>
public sealed partial class DocumentViewModel
{
    private readonly Dictionary<int, PageReadingOrder?> _helpPages = [];
    private CancellationTokenSource? _helpWork;

    /// <summary>The help shown on the pages.</summary>
    [ObservableProperty]
    public partial AccessibilityHelp ShownAccessibilityHelp { get; private set; }

    partial void OnShownAccessibilityHelpChanged(AccessibilityHelp value)
    {
        foreach (var row in AccessibilityQuestionRows) row.HelpShown = row.PageHelp == value && value != AccessibilityHelp.None;
        AccessibilityHelpChanged?.Invoke();
    }

    /// <summary>The help or its data changed (the view draws again).</summary>
    public event Action? AccessibilityHelpChanged;

    /// <summary>The button of a question: shows its help on the pages, or hides it when it is shown.</summary>
    [RelayCommand]
    private void ToggleAccessibilityHelp(AccessibilityQuestionRow row)
    {
        if (row.PageHelp == AccessibilityHelp.None) return;
        ShownAccessibilityHelp = ShownAccessibilityHelp == row.PageHelp ? AccessibilityHelp.None : row.PageHelp;
    }

    /// <summary>
    /// The reading order and artifacts of a page for the help; null while it is worked out (the view is told when it is there) or for a page
    /// that cannot be read.
    /// </summary>
    /// <param name="page">The page (index).</param>
    public PageReadingOrder? AccessibilityHelpFor(int page)
    {
        if (ShownAccessibilityHelp == AccessibilityHelp.None || (uint)page >= (uint)Pages.Count) return null;
        if (_helpPages.TryGetValue(page, out var known)) return known;
        _helpPages[page] = null;   // asked once
        _helpWork ??= new CancellationTokenSource();
        var cancel = _helpWork.Token;
        var item = Pages[page].Item;
        _ = Task.Run(() =>
        {
            using var use = Renderer.Use(item.Source);
            try
            {
                return ReadingOrder.Of(item.Source, item.Page, cancel);
            }
            catch (Exception e) when (e is Core.Pdf.PdfException or InvalidOperationException)
            {
                return null;
            }
        }, cancel).ContinueWith(done =>
        {
            if (done.IsCompletedSuccessfully && !cancel.IsCancellationRequested)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (cancel.IsCancellationRequested) return;
                    _helpPages[page] = done.Result;
                    AccessibilityHelpChanged?.Invoke();
                });
        }, TaskScheduler.Default);
        return null;
    }

    /// <summary>Ends the help and forgets what was worked out (the document changed, the area closed).</summary>
    private void EndAccessibilityHelp()
    {
        _helpWork?.Cancel();
        _helpWork = null;
        _helpPages.Clear();
        ShownAccessibilityHelp = AccessibilityHelp.None;
        AccessibilityHelpChanged?.Invoke();
    }
}
