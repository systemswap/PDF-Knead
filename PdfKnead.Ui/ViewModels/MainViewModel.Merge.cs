using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>Zusammenführen (KONZEPT.md, section 5.2): the window and what happens with its result.</summary>
public partial class MainViewModel
{
    /// <summary>Shows „Seiten zusammenführen“ (set by the view); returns 1 = into the open document, 2 = as a new file, null = cancelled.</summary>
    public Func<MergeViewModel, Task<int?>>? ShowMerge { get; set; }

    /// <summary>
    /// Opens the window. The result goes into the open document (one undo step) or into a new, not yet saved document; for that, pages of
    /// the open document are taken from a copy of its current state, so the new file depends on neither the open document nor any original.
    /// The new document is built on that copy, so it keeps the open document's title, metadata, attachments, output intent and bookmarks.
    /// </summary>
    /// <param name="selectCurrentPage">„Aus Datei“: the current page is selected in the result.</param>
    private async Task MergeAsync(bool selectCurrentPage)
    {
        if (ShowMerge is null) return;
        var target = SelectedDocument is { CanEditPages: true, IsWorking: false } open ? open : null;
        if (target is not null) CommitInputs(target);
        var merge = new MergeViewModel(target, selectCurrentPage);
        try
        {
            var choice = await ShowMerge(merge);
            if (choice == 1 && target is not null && Documents.Contains(target))
            {
                var result = merge.TakeResult(null);
                target.ApplyMerged(result.Pages, result.Sources, result.WithoutBookmarks);
                BackstageOpen = false;
                ShowNotice(Tr.T("Seiten zusammengeführt — Strg+Z nimmt es zurück."));
            }
            else if (choice == 2)
            {
                (PdfDocument, PageItem[])? copy = null;
                if (merge.UsesOpenDocument && target is not null && Documents.Contains(target))
                {
                    Busy = Tr.T("Übernehme die Seiten von {0} …", target.FileName);
                    copy = await target.WorkAsync(() => Task.Run(() => target.CopyCurrentPages()));
                }
                var result = merge.TakeResult(copy);
                // built on the copy of the open document if its pages are used: its title, metadata, attachments, output intent and bookmarks
                // stay as when merging into it; otherwise on an empty document
                var main = copy?.Item1 ?? BlankPage.CreateDocument(595.28, 841.89, 1);
                // the copy keeps the open document's protection (as when merging into it) and opens with its password
                var doc = new DocumentViewModel(MergedName(), main, copy is null ? null : target!.OpenPassword, untitled: true) { Tool = Tool };
                doc.ApplyMerged(result.Pages, result.Sources.Where(s => !ReferenceEquals(s, main)), result.WithoutBookmarks.Where(s => !ReferenceEquals(s, main)));
                Documents.Add(doc);
                SelectedDocument = doc;
                BackstageOpen = false;
                ShowNotice(result.Pages.Count == 1 ? Tr.T("Neue Datei mit 1 Seite — noch nicht gespeichert.")
                    : Tr.T("Neue Datei mit {0} Seiten — noch nicht gespeichert.", result.Pages.Count));
            }
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Message = Tr.T("Zusammenführen fehlgeschlagen: {0}", Failure.Text(ex));
        }
        finally
        {
            Busy = null;
            await merge.ReleaseAsync();
        }
    }

    /// <summary>„Zusammengeführt.pdf“, „Zusammengeführt 2.pdf“ …: the first name not used by an open untitled tab.</summary>
    private string MergedName()
    {
        for (var n = 1; ; n++)
        {
            var name = (n == 1 ? Tr.T("Zusammengeführt") : Tr.T("Zusammengeführt {0}", n)) + ".pdf";
            if (!Documents.Any(d => d.IsUntitled && d.FileName == name)) return name;
        }
    }
}
