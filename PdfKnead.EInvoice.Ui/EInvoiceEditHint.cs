using PdfKnead.Core.Pdf;
using PdfKnead.ViewModels;

namespace PdfKnead.EInvoice.Ui;

/// <summary>
/// The hint when a PDF with an embedded e-invoice is changed for the first time (KONZEPT.md, section 5.24, „Oberfläche“; design decision
/// 15.11.2026): the XML is what counts, and changing the pages does not change it. Only a hint — PDF-Knead never changes an e-invoice (P4: it
/// is a checker, an aid); none while the tab is hidden (the host asks only the tabs shown). Searched on the file as saved, in the background.
/// </summary>
internal static class EInvoiceEditHint
{
    /// <summary>The longest search before it is given up (stage 1 alone usually takes milliseconds).</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>The text of the hint.</summary>
    public static string Text => Tr.T("Diese PDF enthält eine E-Rechnung. Maßgeblich ist die XML; Änderungen an den Seiten ändern sie nicht.");

    /// <summary>The text when the first change is „Als Bild sichern“: the file of page images keeps no attachment.</summary>
    public static string AsImagesText => Tr.T("Diese PDF enthält eine E-Rechnung. Als Bild gesichert fällt sie weg — wie alle Anhänge und Metadaten.");

    /// <summary>Tests: the last search, to wait for.</summary>
    internal static Task Last { get; set; } = Task.CompletedTask;

    /// <summary>Shows the hint if the saved file of the document holds an e-invoice; says nothing otherwise or if the file cannot be read.</summary>
    public static async Task ShowAsync(MainViewModel main, DocumentViewModel document)
    {
        using var cancel = new CancellationTokenSource(Limit);
        bool found;
        try
        {
            found = await Task.Run(() =>
            {
                using var copy = document.OpenSavedCopy();
                return copy is not null && InvoiceLocator.Locate(copy, null, cancel.Token).HasInvoice;
            }, cancel.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or PdfException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return;   // a hint, nothing more: unread is unsaid
        }
        if (found && main.Documents.Contains(document)) main.Message = document.RasterOnSave is null ? Text : AsImagesText;
    }
}
