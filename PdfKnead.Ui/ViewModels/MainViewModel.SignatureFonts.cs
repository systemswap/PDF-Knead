using CommunityToolkit.Mvvm.Input;

namespace PdfKnead.ViewModels;

public partial class MainViewModel
{
    /// <summary>Shows a folder in the file manager (set by the view).</summary>
    public Func<string, Task>? ShowFolder { get; set; }

    /// <summary>Shows the „Handschriften“ window (set by the view).</summary>
    public Func<SignatureFontsViewModel, Task>? ShowSignatureFonts { get; set; }

    /// <summary>Opens the window with the handwriting fonts for signatures (from the ribbon and from File).</summary>
    [RelayCommand]
    private async Task ManageSignatureFonts()
    {
        BackstageOpen = false;
        if (ShowSignatureFonts is null) return;
        var model = new SignatureFontsViewModel(ShowFolder);
        model.Load();
        await ShowSignatureFonts(model);
    }
}
