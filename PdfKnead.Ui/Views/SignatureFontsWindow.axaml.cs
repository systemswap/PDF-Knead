using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>The „Handschriften“ window: the handwriting fonts for signatures, adding and removing them (see <see cref="SignatureFontsViewModel"/>).</summary>
public partial class SignatureFontsWindow : Window
{
    public SignatureFontsWindow()
    {
        InitializeComponent();
        // The samples are drawn only for this window; their images must not remain after it closes
        Closed += (_, _) => Controls.ElementPreview.ReleaseImages();
    }

    public SignatureFontsWindow(SignatureFontsViewModel model) : this()
    {
        model.PickFontFile = PickFontFileAsync;
        DataContext = model;
    }

    private async Task<string?> PickFontFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Schrift wählen"),
            FileTypeFilter =
            [
                new FilePickerFileType(Tr.T("Schriften (TrueType, OpenType)")) { Patterns = ["*.ttf", "*.otf", "*.TTF", "*.OTF"] },
                FilePickerFileTypes.All,
            ],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
