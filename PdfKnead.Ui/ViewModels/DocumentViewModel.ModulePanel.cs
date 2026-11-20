using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PdfKnead.ViewModels;

/// <summary>The area of a module on the right of the document (<see cref="MainViewModel.ShowModulePanel"/>): one area at a time on the right.</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>The area of a module shown on the right; null: none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModulePanelOpen))]
    public partial Control? ModulePanel { get; set; }

    /// <summary>Whether the area of a module is open.</summary>
    public bool IsModulePanelOpen => ModulePanel is not null;

    // another area opens: the module's area closes (one area at a time on the right)
    partial void OnIsAccessibilityOpenChanged(bool value) => CloseModulePanelFor(value);

    partial void OnIsCleanupOpenChanged(bool value) => CloseModulePanelFor(value);

    partial void OnIsNotesOpenChanged(bool value) => CloseModulePanelFor(value);

    partial void OnIsFieldProposalsOpenChanged(bool value) => CloseModulePanelFor(value);

    private void CloseModulePanelFor(bool otherOpened)
    {
        if (otherOpened) ModulePanel = null;
    }

    /// <summary>
    /// Opens the file as it is saved once more (with the document's password), for work of a module in the background — the document shown
    /// is not touched from another thread; null for a new document not saved yet or a file that is gone. The caller disposes it.
    /// </summary>
    public Core.Pdf.Document.PdfDocument? OpenSavedCopy() =>
        IsUntitled || !System.IO.File.Exists(Path) ? null : Core.Pdf.Document.PdfDocument.Open(Path, _password);

    /// <summary>
    /// Brings a spot of a page into view and marks it (<see cref="ModulePlace"/>) — a module's list of places jumps with it, and the spot is
    /// to be seen at once (decided on 15.11.2026).
    /// </summary>
    /// <param name="pageIndex">The page, from 0.</param>
    /// <param name="quad">The spot in page space.</param>
    public void RevealPlace(int pageIndex, Core.Pdf.Text.TextQuad quad) => RevealPlaces([(pageIndex, quad)], 0);

    /// <summary>
    /// Marks several spots of one thing (a value found more than once) and brings one of them into view — that one marked most (wish of the
    /// user, 15.11.2026: which of two equal amounts is meant is not said, so both are shown, and the next click goes to the next).
    /// </summary>
    /// <param name="places">The spots: page from 0, quad in page space.</param>
    /// <param name="current">The one brought into view.</param>
    public void RevealPlaces(IReadOnlyList<(int Page, Core.Pdf.Text.TextQuad Quad)> places, int current)
    {
        if (places.Count == 0) return;
        current = Math.Clamp(current, 0, places.Count - 1);
        (ModulePlaces, ModulePlace) = (places, places[current]);
        HiddenSpotsChanged?.Invoke();
        RevealRequested?.Invoke(places[current].Page, places[current].Quad);
    }

    /// <summary>The spot a module last brought into view, marked on the page until its area closes or the document changes; null: none.</summary>
    public (int Page, Core.Pdf.Text.TextQuad Quad)? ModulePlace { get; private set; }

    /// <summary>All spots marked with it (<see cref="RevealPlaces"/>); empty: none.</summary>
    public IReadOnlyList<(int Page, Core.Pdf.Text.TextQuad Quad)> ModulePlaces { get; private set; } = [];

    /// <summary>Takes the mark of a module away (its area closed, or the pages changed under it).</summary>
    private void ClearModulePlace()
    {
        if (ModulePlace is null) return;
        (ModulePlace, ModulePlaces) = (null, []);
        HiddenSpotsChanged?.Invoke();
    }

    partial void OnModulePanelChanged(Control? value)
    {
        if (value is null) ClearModulePlace();
    }

    /// <summary>When the file was last written (a module's result for an older state is stale); null for a new document.</summary>
    public DateTime? SavedAt => IsUntitled || !System.IO.File.Exists(Path) ? null : System.IO.File.GetLastWriteTimeUtc(Path);
}
