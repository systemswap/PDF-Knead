using CommunityToolkit.Mvvm.Input;
using PdfKnead.Ai;
using PdfKnead.Core.Ocr;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

public partial class MainViewModel
{
    /// <summary>Shows the window „Text erkennen“ (set by the view); true for „Erkennen“.</summary>
    public Func<RecognizeTextViewModel, Task<bool>>? ShowRecognizeText { get; set; }

    /// <summary>
    /// Opens the recognition of a size (set by the tests: a recognition that needs no models); by default PP-OCRv6 from the folder „ocr“.
    /// </summary>
    internal Func<OcrModelSize, ITextRecognizer> OpenRecognizer { get; set; } = size => PaddleOcr.Open(size);

    /// <summary>„Start“ → „Text erkennen“: makes scans searchable — pages and size in a window, then the recognition in the background.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RecognizeText()
    {
        if (SelectedDocument is not { } doc || ShowRecognizeText is null) return;
        if (doc.IsRecognizing)
        {
            ShowNotice(Tr.T("Die Texterkennung läuft schon — unten in der Statuszeile lässt sie sich abbrechen."));
            return;
        }
        var model = new RecognizeTextViewModel(doc.PageCount, doc.CurrentPage?.Index ?? 0, UserSettings.Current.OcrSize);
        if (!await ShowRecognizeText(model) || !Documents.Contains(doc)) return;
        var size = model.Size;
        UserSettings.Update(s => s with { OcrSize = size });
        await RecognizeAsync(doc, model.Pages, size, Tr.T("Text erkennen"));
    }

    /// <summary>Runs the recognition on pages of a document and tells what it did.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="pages">The pages (indices).</param>
    /// <param name="size">The size of the recognition.</param>
    /// <param name="description">The name of the undo step.</param>
    internal async Task RecognizeAsync(DocumentViewModel doc, IReadOnlyList<int> pages, OcrModelSize size, string description)
    {
        RecognitionResult result;
        try
        {
            result = await doc.RecognizeTextAsync(pages, () => OpenRecognizer(size), description);
        }
        catch (TextRecognitionException ex)
        {
            AppLog.Write("Texterkennung nicht gestartet", ex.ToString());
            Message = Tr.T("Die Texterkennung ließ sich nicht starten — die Modelle im Ordner „ocr“ neben dem Programm fehlen oder sind beschädigt. Bitte PDF-Knead neu entpacken bzw. installieren.");
            return;
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Texterkennung fehlgeschlagen: {0}", Failure.Text(ex));
            return;
        }
        catch (OperationCanceledException)
        {
            return;   // the tab has been closed in the meantime
        }
        ShowNotice(result switch
        {
            { Read: 0, Cancelled: true } => Tr.T("Texterkennung abgebrochen — noch keine Seite war fertig."),
            { Read: 0, WithoutImage: > 0, Empty: 0 } => Tr.T("Nichts zu erkennen: Die Seiten enthalten keine Bilder, ihr Text ist schon echter Text."),
            { Read: 0 } => Tr.T("Auf den Seiten wurde kein Text gefunden."),
            { Cancelled: true } => Tr.T("Texterkennung abgebrochen — {0} Seiten haben ihren Text bekommen (Strg+Z nimmt es zurück).", result.Read),
            { Read: 1 } => Tr.T("Text auf 1 Seite erkannt — jetzt durchsuchbar (Strg+Z nimmt es zurück)."),
            _ => Tr.T("Text auf {0} Seiten erkannt — jetzt durchsuchbar (Strg+Z nimmt es zurück).", result.Read),
        });
    }

    /// <summary>Shows the text recognition of a document (set by the view): title, the text, whether it may be copied.</summary>
    public Func<string, string, bool, Task>? ShowOcrText { get; set; }

    /// <summary>„Verborgenes schwärzen“ → „OCR anzeigen“: the whole text recognition of the document in a window, to read and copy.</summary>
    [RelayCommand]
    private async Task ShowOcr()
    {
        if (SelectedDocument is not { } doc || ShowOcrText is null) return;
        var text = await doc.OcrTextAsync();
        await ShowOcrText(Tr.T("Texterkennung (OCR) — {0}", doc.FileName),
            text.Length > 0 ? text : Tr.T("Dieses Dokument hat keine Texterkennung."), doc.CopyAllowed);
    }
}
