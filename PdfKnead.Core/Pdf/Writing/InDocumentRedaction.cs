using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.IO;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Redaction in the open document ("redact first", see KONZEPT.md, section 5.6, and docs/FEHLERGEDAECHTNIS.md, No. 58):
/// the document is written to memory with the redactions applied and opened again; from then on, this is the state that
/// display, printing, export and saving see. Nothing is created on disk.
/// <list type="bullet">
/// <item>The encryption stays (same key): permissions such as the copy restriction continue to apply; a new protection that
/// has been set takes effect only when saving.</item>
/// <item>The program's own elements (text, signature, freehand, image, own fields) are not written into it, as they would
/// otherwise become fixed; they are attached to their page again afterwards. Whatever lay under a redaction is dropped, as
/// when saving.</item>
/// <item>Foreign annotations and fields are written with their current state (moved, filled in, deleted).</item>
/// <item>A pending sanitising is applied at the same time (for example, the redacted name in the title).</item>
/// </list>
/// </summary>
public static class InDocumentRedaction
{
    /// <summary>
    /// The lock in front of everything that leaves the document. Redactions are applied in one place only, by <see cref="Apply"/>
    /// behind the „Schwärzen zuerst“ gate; printing, exporting, extracting, splitting and compressing never redact along the way.
    /// A redaction mark that still arrives there means the gate was skipped: the step is refused instead of redacting halfway
    /// (without the gate's search for the same text elsewhere and its cleanup).
    /// </summary>
    /// <param name="pages">The pages about to leave the document.</param>
    /// <exception cref="PdfException">A page still carries a redaction mark.</exception>
    public static void EnsureApplied(IEnumerable<PageItem> pages)
    {
        if (pages.Any(p => p.Elements.Any(e => e is RedactionElement)))
            throw new PdfException(Tr.T("Die markierten Schwärzungen sind noch nicht angewendet — zuerst „Schwärzungen anwenden“."));
    }

    /// <summary>
    /// Applies the redactions. <paramref name="options"/> determines the version, sanitising and image codec; protection,
    /// flattening and compression are not applied here.
    /// </summary>
    /// <param name="main">The main document.</param>
    /// <param name="pages">The current pages with their elements.</param>
    /// <param name="options">The save options to take version, sanitising and image codec from.</param>
    /// <param name="password">Password with which <paramref name="main"/> was opened (it also opens the result).</param>
    /// <param name="cancel">Cancels the writing.</param>
    /// <param name="redact">Apply the redactions; false: only sanitise (<see cref="PdfSaveOptions.Cleanup"/>), and redaction marks stay marks.</param>
    /// <param name="report">Receives what the writer reports (pages whose marks of hidden content found nothing, pages where the length of
    /// a redacted word shows in part, the pieces the bars grew by).</param>
    /// <returns>The new document, owned by the caller, and its pages with the own elements attached again.</returns>
    public static (PdfDocument Document, PageItem[] Pages) Apply(PdfDocument main, IReadOnlyList<PageItem> pages, PdfSaveOptions options,
        string? password, CancellationToken cancel = default, bool redact = true, Action<PdfSaveResult>? report = null)
    {
        var written = new PageItem[pages.Count];
        for (var i = 0; i < pages.Count; i++)
            written[i] = pages[i].WithElements([.. pages[i].Elements.Where(e => e is ForeignAnnotation || redact && e is RedactionElement)]);
        var doc = Write(main, written, options with
        {
            ApplyRedactions = redact,
            Flatten = false,
            Protection = null,
            KeepEncryption = true,
            Compression = null,
            CompressionCache = null,
        }, password, cancel, report);
        try
        {
            return (doc, Adopt(doc, pages, redact: redact));
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Adopts a document that corresponds page for page to <paramref name="before"/> as the new state: the previous own
    /// elements are attached to their pages again, except those under a redaction.
    /// </summary>
    /// <param name="doc">The new document.</param>
    /// <param name="before">The pages before, in the same order.</param>
    /// <param name="changed">Indices of pages that additionally look different (for example, saved as an image).</param>
    /// <param name="redact">Whether redactions were applied; if false, no element is dropped.</param>
    /// <returns>The new pages.</returns>
    /// <exception cref="InvalidDataException">The page count of <paramref name="doc"/> differs.</exception>
    public static PageItem[] Adopt(PdfDocument doc, IReadOnlyList<PageItem> before, IReadOnlySet<int>? changed = null, bool redact = true)
    {
        if (doc.PageCount != before.Count) throw new InvalidDataException("Seitenzahl nach dem Schwärzen anders");
        var result = new PageItem[before.Count];
        for (var i = 0; i < before.Count; i++)
        {
            var item = before[i];
            // As in PdfWriter.ApplyRedactions: the same areas, the same test whether an element lies underneath
            // (marks of hidden content remove no elements: they take only what nobody sees)
            List<RectD> areas = !redact ? [] : item.Elements.OfType<RedactionElement>().Where(r => !r.Hidden).SelectMany(r => r.PageAreas).Where(a => !a.IsEmpty)
                .Select(a => RectD.FromLtrb(a.Left, a.Bottom, a.Right, a.Top)).ToList();
            var own = item.Elements.Where(e => e is not ForeignAnnotation && !(redact && e is RedactionElement) &&
                (areas.Count == 0 || !Redactor.Hits(areas, RectD.FromLtrb(e.Rect.Left, e.Rect.Bottom, e.Rect.Right, e.Rect.Top)))).ToList();
            var hidden = redact && item.Elements.Any(e => e is RedactionElement { Hidden: true });
            var sameLook = areas.Count == 0 && !hidden && changed?.Contains(i) != true && !item.Elements.Any(e => e is ForeignAnnotation { IsChanged: true });
            result[i] = item.InRedactedDocument(doc, doc.Pages[i], own, sameLook, item.Elements.Count(e => e is ForeignAnnotation));
        }
        return result;
    }

    /// <summary>Writes pages to a file in memory and opens it.</summary>
    /// <param name="main">The main document.</param>
    /// <param name="pages">The pages to write.</param>
    /// <param name="options">The save options.</param>
    /// <param name="password">Password that opens the written file, if it is encrypted.</param>
    /// <param name="cancel">Cancels the writing.</param>
    /// <param name="report">Receives what the writer reports.</param>
    /// <returns>The new document, owned by the caller.</returns>
    public static PdfDocument Write(PdfDocument main, IReadOnlyList<PageItem> pages, PdfSaveOptions options, string? password,
        CancellationToken cancel = default, Action<PdfSaveResult>? report = null)
    {
        using var buffer = new MemoryPdf();
        var result = PdfWriter.Write(main, pages, buffer, options, cancel);
        report?.Invoke(result);
        return buffer.OpenDocument(password);
    }
}
