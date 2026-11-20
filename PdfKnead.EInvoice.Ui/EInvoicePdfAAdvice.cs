using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Modules;
using PdfKnead.ViewModels;

namespace PdfKnead.EInvoice.Ui;

/// <summary>
/// The advice for „Als PDF/A speichern …“ when the PDF holds an e-invoice (KONZEPT.md, section 5.24; decided on 15.11.2026): the
/// specification allows only PDF/A-3 or PDF/A-4f (BR-HYBRID-02), the other parts drop the invoice XML — said at the top, PDF/A-3b chosen
/// first and marked; the other parts stay open to choose. Searched on the file as saved, in the background.
/// </summary>
internal static class EInvoicePdfAAdvice
{
    /// <summary>The sentence at the top of the dialog.</summary>
    public static string Text => Tr.T("Diese PDF enthält eine E-Rechnung. Erhalten bleibt sie nur als PDF/A-3 oder PDF/A-4f — bei den anderen Fassungen fällt sie weg.");

    /// <summary>The advice, or null if the saved file holds no e-invoice or cannot be read.</summary>
    public static async Task<PdfAAdvice?> AskAsync(DocumentViewModel document)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var found = await Task.Run(() =>
            {
                using var copy = document.OpenSavedCopy();
                return copy is not null && InvoiceLocator.Locate(copy, null, cancel.Token).HasInvoice;
            }, cancel.Token);
            return found ? new PdfAAdvice(Text, PdfAConformance.A3b) : null;
        }
        catch (Exception e) when (e is OperationCanceledException or PdfException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
