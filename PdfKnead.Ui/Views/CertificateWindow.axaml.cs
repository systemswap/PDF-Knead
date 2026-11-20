using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>The „Zertifikat“ window: which certificate signs digital signatures (see <see cref="CertificateViewModel"/>).</summary>
public partial class CertificateWindow : Window
{
    public CertificateWindow() => InitializeComponent();

    public CertificateWindow(CertificateViewModel model) : this()
    {
        model.PickExportFile = PickExportFileAsync;
        DataContext = model;
    }

    private async Task<string?> PickExportFileAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Zertifikat exportieren"),
            SuggestedFileName = "PDF-Knead-Zertifikat.p12",
            DefaultExtension = "p12",
            FileTypeChoices = [new FilePickerFileType(Tr.T("Zertifikat mit Schlüssel")) { Patterns = ["*.p12"] }],
        });
        return file?.TryGetLocalPath();
    }

    private CertificateViewModel? Model => DataContext as CertificateViewModel;

    private async void OnPickFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Zertifikat aus einer Datei (.pfx, .p12)"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Tr.T("Zertifikat mit Schlüssel")) { Patterns = ["*.pfx", "*.p12"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && Model is { } model) model.FilePath = path;
    }

    private async void OnPickTimestampCertificate(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Kundenzertifikat für den Zeitstempeldienst"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Tr.T("Zertifikat mit Schlüssel")) { Patterns = ["*.pfx", "*.p12"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && Model is { } model) model.TimestampCertificate = path;
    }

    private async void OnPickCardModule(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Kartentreiber (PKCS#11)"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Tr.T("Kartentreiber")) { Patterns = ["*.so", "*.so.*", "*.dylib"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && Model is { } model) model.AddCardModule(path);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Model is { HasCertificate: true } model && Clipboard is { } clipboard) await clipboard.SetTextAsync(model.Fingerprint);
    }

    /// <summary>
    /// „Schließen“: with something not complete the warning is shown first (decided on 11.11.2026; No. 733); „Trotzdem schließen“ in it
    /// and a second „Schließen“ close.
    /// </summary>
    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || model.TryClose()) Close();
    }
}
