using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Scanning;
using PdfKnead.Rendering;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>Scannen (KONZEPT §5.25): the window and what happens with its result.</summary>
public partial class MainViewModel
{
    /// <summary>Shows the scan window (set by the view); returns the result or null if closed without taking over.</summary>
    public Func<ScanViewModel, Task<ScanResult?>>? ShowScan { get; set; }

    /// <summary>The page „Scanprofile“ of the file area.</summary>
    public ScanProfilesViewModel ScanProfiles { get; } = new();

    /// <summary>The scanner interface (tests put a fake one here).</summary>
    internal Func<IScanBackend> ScanBackend { get; set; } = NetworkScanning.Backend;

    /// <summary>
    /// Opens the scan window. The pages it hands over become a new, not yet saved document (in the language of the program) or are appended
    /// to the open document in one undo step; on request the text recognition runs on them afterwards.
    /// </summary>
    [RelayCommand]
    private async Task Scan()
    {
        if (ShowScan is null) return;
        var target = SelectedDocument is { CanEditPages: true, IsWorking: false } open ? open : null;
        var scan = new ScanViewModel(ScanBackend(), target is not null);
        BackstageOpen = false;
        var result = await ShowScan(scan);
        if (result is null) return;
        var pages = result.Pages;
        var count = pages.PageCount;
        try
        {
            DocumentViewModel doc;
            int first;
            if (!result.NewDocument && target is not null && Documents.Contains(target) && Editable(target))
            {
                CommitInputs(target);
                first = target.Pages.Count;
                target.InsertPages(first, pages, [.. Enumerable.Range(0, pages.Pages.Count)]);
                SelectedDocument = doc = target;
                ShowNotice(count == 1 ? Tr.T("1 gescannte Seite angehängt — Strg+Z nimmt es zurück.")
                    : Tr.T("{0} gescannte Seiten angehängt — Strg+Z nimmt es zurück.", count));
            }
            else
            {
                // built on an empty document of the program, so the new file has its language and title like „Neu“
                var size = pages.Pages[0];
                var main = BlankPage.CreateDocument(size.Width, size.Height, 1, "1.7", null, Tr.Culture.Name);
                doc = new DocumentViewModel(ScannedName(), main, untitled: true) { Tool = Tool };
                doc.ApplyMerged(PageItem.From(pages), [pages], []);
                Documents.Add(doc);
                SelectedDocument = doc;
                first = 0;
                ShowNotice(count == 1 ? Tr.T("Neue Datei mit 1 gescannten Seite — noch nicht gespeichert.")
                    : Tr.T("Neue Datei mit {0} gescannten Seiten — noch nicht gespeichert.", count));
            }
            Busy = null;
            if (result.Recognize)
                await RecognizeAsync(doc, [.. Enumerable.Range(first, count)], UserSettings.Current.OcrSize, Tr.T("Text erkennen"));
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Übernehmen der Scans fehlgeschlagen: {0}", Failure.Text(ex));
        }
        finally
        {
            Busy = null;
        }
    }

    /// <summary>„Scan 1.pdf“, „Scan 2.pdf“ …: the first name not used by an open untitled tab.</summary>
    private string ScannedName()
    {
        for (var n = 1; ; n++)
        {
            var name = Tr.T("Scan {0}", n) + ".pdf";
            if (!Documents.Any(d => d.IsUntitled && d.FileName == name)) return name;
        }
    }
}
