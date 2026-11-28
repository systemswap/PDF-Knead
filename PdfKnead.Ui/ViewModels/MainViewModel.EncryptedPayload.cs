using PdfKnead.Core;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Security;
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
                doc.FileName, DisplayNames.Safe(payload.Name), DisplayNames.Safe(payload.CryptographicFilter)),
            Tr.T("Dokument öffnen"), null);
        if (answer != 1) return;
        await OpenEncryptedPayloadAsync(payload);
    }

    /// <summary>Opens the payload of a wrapper as a new, untitled document; a method PDF-Knead cannot open is said in the message bar.</summary>
    /// <param name="payload">The payload.</param>
    internal async Task OpenEncryptedPayloadAsync(EncryptedPayload payload)
    {
        string? password = null;
        List<System.Security.Cryptography.X509Certificates.X509Certificate2>? certificates = null;
        // the name of the attachment from the file: as the document's name (tab, window title, the name proposed when saving) without its
        // directional formatting characters, in the questions with them shown (Unicode 9, UTR #36; docs/FEHLERGEDAECHTNIS.md, No. 1217)
        var name = DisplayNames.FileName(payload.Name) is { Length: > 0 } clean ? clean : "payload.pdf";
        var shown = DisplayNames.Safe(payload.Name);
        while (true)
        {
            try
            {
                var document = await Task.Run(() => PdfDocument.Open(payload.Data, password, certificates));
                var opened = new DocumentViewModel(name, document, password, untitled: true) { Tool = Tool, OpenCertificates = certificates };
                Documents.Add(opened);
                SelectedDocument = opened;
                return;
            }
            catch (PdfPasswordException ex)
            {
                if (AskPassword is null) return;
                password = await AskPassword(shown, ex.WrongPassword);
                if (password is null) return;
            }
            catch (PdfRecipientException ex) when (AskChoice is not null && PickCertificateFile is not null)
            {
                if (await AskRecipientCertificateAsync(shown, ex, again: certificates is not null) is not { } certificate) return;
                (certificates ??= []).Add(certificate);
            }
            catch (Exception ex) when (Failure.Handled(ex))
            {
                Message = Tr.T("{0}: {1}", shown, Failure.Text(ex));
                return;
            }
        }
    }
}
