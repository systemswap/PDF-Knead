using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.ViewModels;

namespace PdfKnead.EInvoice.Ui;

/// <summary>
/// The entry for „Jetzt schwärzen?“ when the PDF holds an e-invoice (KONZEPT.md, section 5.24; decided on 15.11.2026): named always,
/// whatever the search found — the XML writes the values otherwise (an IBAN without spaces, amounts, dates) — and removed with „Gleich mit
/// entfernen“ (ticked at first), together with its data in the XMP (Fehlergedächtnis 865). The XML itself is never changed (P4); unticked,
/// the warning says what stays. Searched on the file as saved, in the background.
/// </summary>
internal static class EInvoiceRedaction
{
    /// <summary>The sentence in the box „Weitere Fundstellen“.</summary>
    public static string Text => Tr.T("Diese PDF enthält eine E-Rechnung (XML). Ihre Angaben lassen sich nicht schwärzen und stehen dort oft in anderer Schreibweise — „Gleich mit entfernen“ nimmt die E-Rechnung samt ihren Angaben in den Metadaten heraus.");

    /// <summary>What stays when „Gleich mit entfernen“ is unticked.</summary>
    public static string KeepWarning => Tr.T("Ohne Entfernen bleibt die E-Rechnung mit allen Angaben in der Datei — auch mit dem, was Sie schwärzen.");

    /// <summary>The entry, or null if the saved file holds no e-invoice or cannot be read.</summary>
    public static async Task<RedactionElsewhere?> AskAsync(DocumentViewModel document)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var found = await Task.Run(() =>
            {
                using var copy = document.OpenSavedCopy();
                return copy is not null && InvoiceLocator.Locate(copy, null, cancel.Token).HasInvoice;
            }, cancel.Token);
            return found ? new RedactionElsewhere(CleanupItems.Attachments, Text, KeepWarning, Sentence: true) : null;
        }
        catch (Exception e) when (e is OperationCanceledException or PdfException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
