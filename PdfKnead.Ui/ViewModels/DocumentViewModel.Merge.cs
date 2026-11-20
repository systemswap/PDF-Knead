using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.ViewModels;

/// <summary>The document's part in the window „Seiten zusammenführen“ (KONZEPT.md, section 5.2).</summary>
public sealed partial class DocumentViewModel
{
    // Inserted files whose bookmarks are left out on saving (option „Lesezeichen mitnehmen“ off)
    private readonly HashSet<PdfDocument> _outlinesLeftOut = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Takes over the result of the window as the new page list, in one undo step. The document takes ownership of the files whose pages
    /// it now contains.
    /// </summary>
    /// <param name="pages">The pages in their new order (each entry once).</param>
    /// <param name="sources">Files loaded in the window that pages come from.</param>
    /// <param name="withoutBookmarks">Of these, the files whose bookmarks are left out.</param>
    public void ApplyMerged(IReadOnlyList<PageItem> pages, IEnumerable<PdfDocument> sources, IEnumerable<PdfDocument> withoutBookmarks)
    {
        foreach (var source in sources)
            if (!_insertedSources.Contains(source)) _insertedSources.Add(source);
        _outlinesLeftOut.UnionWith(withoutBookmarks);
        History.Apply(Tr.T("Seiten zusammenführen"), [.. pages]);
    }

    /// <summary>Password the document was opened with; a copy (<see cref="CopyCurrentPages"/>) keeps its encryption and opens with it too.</summary>
    internal string? OpenPassword => _password;

    /// <summary>
    /// A copy of the current state for a new document: all pages written to memory as they are now (with filled-in fields and moved
    /// annotations; the program's own elements attached again), independent of this document and of its file.
    /// </summary>
    /// <param name="cancel">Cancels the writing.</param>
    /// <returns>The copy, owned by the caller, and its pages in the order of <see cref="PageHistory.Pages"/>.</returns>
    public (PdfDocument Document, PageItem[] Pages) CopyCurrentPages(CancellationToken cancel = default) =>
        InDocumentRedaction.Apply(Document, History.Pages, SaveOptions(SaveVersion) with { Cleanup = CleanupItems.None }, _password, cancel, redact: false);
}
