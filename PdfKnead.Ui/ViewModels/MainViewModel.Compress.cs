using CommunityToolkit.Mvvm.Input;

namespace PdfKnead.ViewModels;

public partial class MainViewModel
{
    /// <summary>Shows the compress window (set by the view); returns a message for the main window, or null if cancelled.</summary>
    public Func<CompressViewModel, Task<string?>>? ShowCompress { get; set; }

    /// <summary>File → Compress: settings to try out on the left, before/after preview and the file sizes on the right.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Compress()
    {
        if (!EnsureAllowed("start.compress")) return;
        if (SelectedDocument is not { } doc || ShowCompress is null) return;
        CommitInputs(doc);
        if (!await RedactFirstAsync(doc, Tr.T("Komprimieren"))) return;
        BackstageOpen = false;
        var compress = new CompressViewModel(doc);
        var message = await ShowCompress(compress);
        if (compress.ReplacesOpenFile is { } settings)
        {
            // The open file is the target: save normally (the window released its files when it closed)
            await compress.Released;
            await SaveDocumentAsync(doc, doc.Path, doc.SaveVersion, settings);
        }
        else if (message is { Length: > 0 }) ShowNotice(message);
    }
}
