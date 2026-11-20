using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Rendering;

namespace PdfKnead.ViewModels;

/// <summary>
/// Sort pages: all pages as tiles; several can be selected (Ctrl/Shift+click, rubber band) and dragged to another position.
/// Each tile shows the number the page had in the document. „Übernehmen“ rearranges the page panel in a single undo step.
/// </summary>
public sealed partial class SortViewModel : ObservableObject
{
    private readonly DocumentViewModel _doc;
    private readonly PageViewModel[] _original;

    public SortViewModel(DocumentViewModel doc)
    {
        _doc = doc;
        _original = [.. doc.History.Pages.Select((item, i) => new PageViewModel(item, i))];
        Pages = _original;
        UpdateSelection();
    }

    public string FileName => _doc.FileName;

    public DocumentRenderer Renderer => _doc.Renderer;

    /// <summary>The pages in the new order.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    public partial IReadOnlyList<PageViewModel> Pages { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToStartCommand), nameof(ToEndCommand))]
    public partial string SelectionText { get; set; } = "";

    /// <summary>Whether the order differs from the document.</summary>
    public bool IsChanged => !Pages.SequenceEqual(_original);

    private bool HasSelection => Pages.Any(p => p.IsSelected);

    /// <summary>Updates <see cref="SelectionText"/> after the selection has changed.</summary>
    public void UpdateSelection()
    {
        var count = Pages.Count(p => p.IsSelected);
        SelectionText = count switch
        {
            0 => Tr.T("Seiten wählen (Strg/Umschalt+Klick, Rahmen ziehen) und an die neue Stelle ziehen"),
            1 => Tr.T("1 Seite gewählt"),
            _ => Tr.T("{0} Seiten gewählt", count),
        };
    }

    /// <summary>Moves the selected pages (in their current order) in front of the page at index <paramref name="insertBefore"/>.</summary>
    /// <param name="insertBefore">Index in <see cref="Pages"/>, clamped to 0 … count; the count moves them to the end.</param>
    public void MoveSelected(int insertBefore)
    {
        var selected = Pages.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) return;
        var before = Pages.Take(Math.Clamp(insertBefore, 0, Pages.Count)).Count(p => !p.IsSelected);
        var rest = Pages.Where(p => !p.IsSelected).ToList();
        rest.InsertRange(before, selected);
        if (!rest.SequenceEqual(Pages)) Pages = rest;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToStart() => MoveSelected(0);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToEnd() => MoveSelected(Pages.Count);

    [RelayCommand(CanExecute = nameof(IsChanged))]
    private void Reset() => Pages = _original;

    /// <summary>The new order for the document.</summary>
    public IReadOnlyList<PageItem> Order => [.. Pages.Select(p => p.Item)];
}
