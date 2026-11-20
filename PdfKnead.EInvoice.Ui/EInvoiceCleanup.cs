using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Modules;

namespace PdfKnead.EInvoice.Ui;

/// <summary>
/// The row „E-Rechnung“ in „Dokument bereinigen“ (KONZEPT.md, section 5.24; decision (4) of 15.11.2026): an e-invoice is a row of its
/// own, not ticked at first — cleaning up before sending an invoice must not take the e-invoice away unnoticed; „Eingebettete Dateien“ counts
/// the other attachments. Ticked, the invoice XML goes with its data in the XMP (Fehlergedächtnis 865); the XML is never changed (P4).
/// </summary>
internal static class EInvoiceCleanup
{
    /// <summary>What follows if the row is ticked.</summary>
    public static string Note => Tr.T("Bleibt, solange nicht angehakt. Entfernt ist die PDF keine E-Rechnung mehr — die Rechnungs-XML und ihre Angaben in den Metadaten gehen.");

    /// <summary>The row, or null if the document holds no e-invoice (read only, in the background like the inspection of the area).</summary>
    public static ModuleCleanupFinding? Find(PdfDocument doc)
    {
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (InvoiceLocator.Locate(doc, null, cancel.Token) is not { HasInvoice: true, File: { } file }) return null;
            return new ModuleCleanupFinding(Tr.T("E-Rechnung ({0})", InvoiceFormat.Plain(file.DisplayName, 60)), Note,
                new HashSet<Core.Pdf.Objects.PdfObjectId> { file.StreamId }, 1);
        }
        catch (Exception e) when (e is OperationCanceledException or PdfException or IOException or InvalidDataException)
        {
            return null;   // not found is not offered: „Eingebettete Dateien“ counts it then
        }
    }
}
