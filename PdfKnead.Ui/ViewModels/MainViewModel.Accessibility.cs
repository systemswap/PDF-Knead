using CommunityToolkit.Mvvm.Input;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>The area „Barrierefreiheit“ (docs/ABLAUFPLAN.md, 6.7 step 10).</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// „Sicher“ tab → „Barrierefreiheit“: opens the area on the right and checks the document as it would be saved; a second click closes it.
    /// One area at a time on the right, so tools with an area of their own end.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleAccessibility()
    {
        if (!EnsureAllowed("secure.accessibility")) return;
        if (SelectedDocument is not { } doc) return;
        if (doc.IsAccessibilityOpen)
        {
            doc.CloseAccessibility();
            return;
        }
        if (Tool is not ViewTool.SelectText and not ViewTool.Pan) Tool = ViewTool.SelectText;
        if (doc.IsCleanupOpen) doc.CloseCleanup();
        if (doc.IsNotesOpen) doc.CloseNotes();
        if (doc.IsFieldProposalsOpen) doc.CloseFieldProposals();
        // the headline as a status message (in words, not only in colour)
        doc.AccessibilityAnnounced -= ShowNotice;
        doc.AccessibilityAnnounced += ShowNotice;
        try
        {
            await doc.OpenAccessibilityAsync();
        }
        catch (OperationCanceledException)
        {
            // the tab has been closed in the meantime
        }
    }

    /// <summary>Lets the user choose where to save a report (set by the view): takes title, suggested name and folder; returns the path, or null.</summary>
    public Func<string, string, string?, Task<string?>>? PickReportSavePath { get; set; }

    /// <summary>„Bericht speichern …“ in the area: the last result as an HTML or text report for the record.</summary>
    [RelayCommand]
    private async Task SaveAccessibilityReport()
    {
        if (SelectedDocument is not { HasAccessibilityReport: true } doc || PickReportSavePath is null) return;
        var name = System.IO.Path.GetFileNameWithoutExtension(doc.FileName) + " – " + Tr.T("Barrierefreiheit") + ".html";
        if (await PickReportSavePath(Tr.T("Bericht speichern"), name, SaveFolder(doc)) is not { } path) return;
        try
        {
            await doc.SaveAccessibilityReportAsync(path);
            ShowNotice(Tr.T("Bericht gespeichert: {0}", System.IO.Path.GetFileName(path)));
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Der Bericht lässt sich nicht speichern: {0}", Failure.Text(ex));
        }
    }

    /// <summary>Closes the area „Barrierefreiheit“.</summary>
    [RelayCommand]
    private void CloseAccessibility() => SelectedDocument?.CloseAccessibility();
}
