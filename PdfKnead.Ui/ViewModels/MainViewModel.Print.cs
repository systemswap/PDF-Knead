using CommunityToolkit.Mvvm.Input;

namespace PdfKnead.ViewModels;

public partial class MainViewModel
{
    /// <summary>Shows the print window (set by the view); returns a message for the main window, or null if cancelled.</summary>
    public Func<PrintViewModel, Task<string?>>? ShowPrint { get; set; }

    /// <summary>File → Print (Ctrl+P): options on the left, preview on the right.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Print()
    {
        if (SelectedDocument is not { } doc || ShowPrint is null) return;
        CommitInputs(doc);
        if (!await RedactFirstAsync(doc, Tr.T("Drucken"))) return;
        BackstageOpen = false;
        if (await ShowPrint(new PrintViewModel(doc) { IsOpen = path => Documents.Any(d => SamePath(d.Path, path)) }) is { } message) ShowNotice(message);
    }
}
