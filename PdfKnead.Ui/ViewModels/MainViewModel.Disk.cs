using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>
/// Open files changed by another program (KONZEPT.md, „Datei von außen geändert“; docs/FEHLERGEDAECHTNIS.md, No. 240): the window watches
/// their folders and, when something happens (or the window becomes active again), asks per file — reload, keep the opened version, or
/// close the tab, depending on what is still readable.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Shows a question with one or two answers and „Abbrechen“ (set by the view): heading, message, first and second answer → 1, 2 or 0. The
    /// second is null where it would mean the same as „Abbrechen“.
    /// </summary>
    public Func<string, string, string, string?, Task<int>>? AskChoice { get; set; }

    /// <summary>Shows a question with any number of answers, a way back and optionally a box to tick (set by the view; <see cref="ChoiceRequest"/>).</summary>
    public Func<ChoiceRequest, Task<ChoiceAnswer>>? AskChoices { get; set; }

    /// <summary>
    /// A question of a document (<see cref="DocumentViewModel.AskChoices"/>): through the view, one after the other (switching a tool off asks
    /// in every document that has something to ask about); null without a view.
    /// </summary>
    private async Task<ChoiceAnswer?> AskForDocumentAsync(ChoiceRequest request)
    {
        if (AskChoices is null) return null;
        await _documentQuestion.WaitAsync();
        try
        {
            return AskChoices is { } ask ? await ask(request) : null;
        }
        finally
        {
            _documentQuestion.Release();
        }
    }

    private readonly SemaphoreSlim _documentQuestion = new(1, 1);

    private bool _checkingDisk;

    /// <summary>The folders of the open files (for the view's watchers).</summary>
    public IReadOnlyCollection<string> WatchedFolders =>
        [.. Documents.Where(d => !d.IsUntitled).Select(d => System.IO.Path.GetDirectoryName(d.Path)).OfType<string>().Distinct(StringComparer.Ordinal)];

    /// <summary>Checks every open file and asks about each that changed; one question at a time.</summary>
    public async Task CheckDiskAsync()
    {
        if (_checkingDisk || AskChoice is null) return;
        _checkingDisk = true;
        try
        {
            foreach (var doc in Documents.ToList())
            {
                var change = doc.CheckDisk();
                if (change == DiskChange.None || !Documents.Contains(doc)) continue;
                var name = ChoiceName(doc.FileName);
                var unsaved = doc.IsModified ? " " + Tr.T("Ihre ungespeicherten Änderungen gehen dabei verloren.") : "";
                switch (change)
                {
                    case DiskChange.Changed:
                    {
                        var answer = await AskChoice(Tr.T("„{0}“ wurde von einem anderen Programm geändert", name),
                            Tr.T("Neu laden zeigt die geänderte Datei.") + unsaved + " " +
                            Tr.T("Behalten arbeitet mit der geöffneten Fassung weiter; Speichern überschreibt dann die geänderte Datei."),
                            Tr.T("Neu laden"), Tr.T("Behalten"));
                        if (answer == 1) await ReloadAsync(doc);
                        else doc.AnswerDiskChange(change);
                        break;
                    }
                    case DiskChange.OverwrittenInPlace:
                    {
                        var answer = await AskChoice(Tr.T("„{0}“ wurde von einem anderen Programm überschrieben", name),
                            Tr.T("Die geöffnete Fassung lässt sich nicht mehr vollständig lesen. Neu laden zeigt die geänderte Datei.") + unsaved,
                            Tr.T("Neu laden"), Tr.T("Reiter schließen"));
                        if (answer == 1) await ReloadAsync(doc);
                        else if (answer == 2) await CloseWithoutAskingAsync(doc);
                        else doc.AnswerDiskChange(change);
                        break;
                    }
                    case DiskChange.Deleted:
                    {
                        var answer = await AskChoice(Tr.T("„{0}“ gibt es nicht mehr", name),
                            Tr.T("Die Datei wurde gelöscht oder verschoben. Im Programm behalten: weiterarbeiten, Speichern legt sie neu an.") +
                            (doc.IsModified ? " " + Tr.T("Schließen verwirft Ihre ungespeicherten Änderungen.") : ""),
                            Tr.T("Im Programm behalten"), Tr.T("Reiter schließen"));
                        if (answer == 2) await CloseWithoutAskingAsync(doc);
                        else doc.AnswerDiskChange(change);
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Failure.Text(ex);
        }
        finally
        {
            _checkingDisk = false;
        }
    }

    private static string ChoiceName(string name) => name.Length <= 40 ? name : name[..22] + "…" + name[^17..];

    /// <summary>Opens the file anew in the tab's place (the opened version and its unsaved changes are dropped).</summary>
    private async Task ReloadAsync(DocumentViewModel doc)
    {
        var index = Documents.IndexOf(doc);
        if (index < 0) return;
        PdfKnead.Core.Pdf.Document.PdfDocument? document = null;
        string? password = doc.OpenPassword;
        var certificates = doc.OpenCertificates;
        try
        {
            document = await Task.Run(() => PdfKnead.Core.Pdf.Document.PdfDocument.Open(doc.Path, password, certificates));
        }
        catch (Exception e) when (e is PdfKnead.Core.Pdf.PdfPasswordException or PdfKnead.Core.Pdf.PdfRecipientException)
        {
            (document, password, certificates) = await LoadAsync(doc.Path);
        }
        if (document is null) return;
        var fresh = new DocumentViewModel(doc.Path, document, password) { Tool = Tool, OpenCertificates = certificates };
        var selected = ReferenceEquals(SelectedDocument, doc);
        Documents[index] = fresh;
        if (selected || SelectedDocument is null) SelectedDocument = fresh;
        await doc.CloseAsync();
    }

    private async Task CloseWithoutAskingAsync(DocumentViewModel doc)
    {
        var index = Documents.IndexOf(doc);
        if (index < 0) return;
        Documents.Remove(doc);
        if (ReferenceEquals(SelectedDocument, doc) || SelectedDocument is null)
            SelectedDocument = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
        await doc.CloseAsync();
    }
}
