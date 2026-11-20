using CommunityToolkit.Mvvm.Input;

namespace PdfKnead.ViewModels;

/// <summary>The area „Notizen“ (KONZEPT.md, section 5.5a).</summary>
public sealed partial class MainViewModel
{
    /// <summary>Opens or closes the area „Notizen“; one area at a time on the right, so tools with an area of their own end.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleNotes()
    {
        if (SelectedDocument is not { } doc) return;
        if (doc.IsNotesOpen)
        {
            doc.CloseNotes();
            return;
        }
        if (Tool is not ViewTool.SelectText and not ViewTool.Pan) Tool = ViewTool.SelectText;
        doc.OpenNotes();
    }

    /// <summary>Closes the area „Notizen“.</summary>
    [RelayCommand]
    private void CloseNotes() => SelectedDocument?.CloseNotes();
}
