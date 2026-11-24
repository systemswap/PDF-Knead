using PdfKnead.Core;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>
/// The unencrypted wrapper document (ISO 32000-2, 7.6.7): opened, it is named as such, and its encrypted payload can be opened as a document
/// of its own — from memory, nothing written to disk, with the password asked as for any file (design decision, 24.11.2026;
/// docs/FEHLERGEDAECHTNIS.md, No. 1115).
/// </summary>
public partial class MainViewModel
{
    /// <summary>After a document opened: if it is a wrapper, says so and offers to open the payload.</summary>
    /// <param name="doc">The document just opened.</param>
    internal async Task OfferEncryptedPayloadAsync(DocumentViewModel doc)
    {
        if (AskChoice is null || EncryptedPayload.Of(doc.Document) is not { } payload) return;
        var answer = await AskChoice(Tr.T("Verschlüsseltes Dokument in einer Hülle"),
            Tr.T("„{0}“ ist eine Hülle: das eigentliche Dokument „{1}“ steckt verschlüsselt darin (Verfahren „{2}“). Soll es geöffnet werden?",
                doc.FileName, payload.Name, payload.CryptographicFilter),
            Tr.T("Dokument öffnen"), null);
        if (answer != 1) return;
        await OpenEncryptedPayloadAsync(payload);
    }

    /// <summary>Opens the payload of a wrapper as a new, untitled document; a method PDF-Knead cannot open is said in the message bar.</summary>
    /// <param name="payload">The payload.</param>
    internal async Task OpenEncryptedPayloadAsync(EncryptedPayload payload)
    {
        string? password = null;
        while (true)
        {
            try
            {
                var document = await Task.Run(() => PdfDocument.Open(payload.Data, password));
                var opened = new DocumentViewModel(payload.Name, document, password, untitled: true) { Tool = Tool };
                Documents.Add(opened);
                SelectedDocument = opened;
                return;
            }
            catch (PdfPasswordException ex)
            {
                if (AskPassword is null) return;
                password = await AskPassword(payload.Name, ex.WrongPassword);
                if (password is null) return;
            }
            catch (Exception ex) when (Failure.Handled(ex))
            {
                Message = Tr.T("{0}: {1}", payload.Name, Failure.Text(ex));
                return;
            }
        }
    }
}
