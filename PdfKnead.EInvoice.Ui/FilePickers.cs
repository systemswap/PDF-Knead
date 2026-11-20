using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace PdfKnead.EInvoice.Ui;

/// <summary>The file dialogs of the module (through the window the control sits in).</summary>
internal static class FilePickers
{
    /// <summary>Asks where to save a file under a suggested name; returns the local path, or null.</summary>
    public static async Task<string?> SaveAsync(Control owner, string name, string extension)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { } storage) return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Speichern unter"),
            SuggestedFileName = name,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = [$"*.{extension}"] }],
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Asks for an XML file to check; returns the local path, or null.</summary>
    public static async Task<string?> OpenXmlAsync(Control owner)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("XRechnung als eigene Datei prüfen …"),
            FileTypeFilter = [new FilePickerFileType("XML") { Patterns = ["*.xml", "*.XML"], MimeTypes = ["application/xml", "text/xml"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
