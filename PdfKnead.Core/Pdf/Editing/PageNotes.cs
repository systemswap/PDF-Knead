using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>A note of the document, as far as it can be read: who, when, about what, and the answers to it.</summary>
/// <param name="Page">The page (index).</param>
/// <param name="Annotation">The annotation; null for a note of PDF Knead not saved yet or read back as an element (<paramref name="Own"/>).</param>
/// <param name="Kind">Its kind (/Subtype).</param>
/// <param name="Rect">Where it stands on the page now (moved: there; without an appearance of its own: the symbol the display draws).</param>
/// <param name="Text">The text (/Contents); may be empty.</param>
/// <param name="Author">Who wrote it (/T), if given.</param>
/// <param name="Subject">About what (/Subj), if given.</param>
/// <param name="Date">When (/M), if given and readable.</param>
/// <param name="Replies">The answers to it (/IRT), in the order of the page.</param>
/// <param name="Own">The note as an element of PDF Knead (written with „Notiz“, changeable); null for notes of other programs.</param>
public sealed record PageNote(int Page, PdfDictionary? Annotation, string Kind, PdfRectangle Rect, string Text, string? Author, string? Subject,
    DateTimeOffset? Date, IReadOnlyList<PageNote> Replies, NoteElement? Own = null);

/// <summary>
/// The notes of a page, to read them (KONZEPT.md, section 5.5a; wish of 30.10.2026 — the note at a bar could not be opened): sticky notes always,
/// other markup only if it carries a text of its own (a comment on a highlight). Free text shows its text on the page, form fields, links and
/// popups are not notes. Notes without an appearance of their own are no elements of the page but are drawn by the display all the same, so they
/// are read from /Annots; annotations deleted as elements are gone, moved ones stand where they were moved to.
/// </summary>
public static class PageNotes
{
    private static readonly PdfName Annots = PdfName.Get("Annots"), Contents = PdfName.Get("Contents"), T = PdfName.Get("T"), Subj = PdfName.Get("Subj"),
        M = PdfName.Get("M"), Irt = PdfName.Get("IRT"), Rect = PdfName.Get("Rect"), F = PdfName.Get("F"), Oc = PdfName.Get("OC");

    /// <summary>Reads the notes of a page, in the order of the page; replies go with the note they answer.</summary>
    /// <param name="index">The page (index).</param>
    /// <param name="item">The page with its elements.</param>
    /// <returns>The notes; empty if there are none or the page cannot be read.</returns>
    public static IReadOnlyList<PageNote> Of(int index, PageItem item)
    {
        List<PageNote>? notes = null;
        // the own notes (elements of the page: new ones, and saved ones read back)
        foreach (var own in item.Elements.OfType<NoteElement>())
            (notes ??= []).Add(new PageNote(index, null, "Text", own.Rect, own.Text, own.Author.Length > 0 ? own.Author : null, null, own.Modified, [], own));
        if (item.Page.Dictionary.Get(Annots) is not PdfArray annots) return notes ?? (IReadOnlyList<PageNote>)[];
        var doc = item.Source;
        var optional = OptionalContent.For(doc);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < annots.Count; i++)
        {
            try
            {
                if (annots.Get(i) is not PdfDictionary annot || !seen.Add(annot)) continue;
                var kind = annot.GetName(PdfName.Subtype)?.Value;
                if (kind is null or "Popup" or "Link" or "Widget" or "FreeText" || annot.Get(Irt) is PdfDictionary) continue;
                var text = Read(annot, Contents);
                if (kind != "Text" && text is null) continue;
                if (((annot.GetInteger(F) ?? 0) & (2 | 32)) != 0 || optional.IsHidden(annot.Get(Oc), null) || ElementStore.IsOwn(doc, item.Page, annot)) continue;
                if (Where(doc, item, annot, kind) is not { } rect) continue;
                (notes ??= []).Add(Note(index, annot, kind, rect, Replies(index, annots, annot, rect)));
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or IOException or ArgumentException)
            {
                // unreadable: no note to list
            }
        }
        return notes ?? (IReadOnlyList<PageNote>)[];
    }

    /// <summary>Where the note stands now; null if it was deleted.</summary>
    private static PdfRectangle? Where(PdfDocument doc, PageItem item, PdfDictionary annot, string kind)
    {
        if (ForeignAnnotations.IsElement(doc, item.Page, annot))
            return item.Elements.OfType<ForeignAnnotation>().FirstOrDefault(f => ReferenceEquals(f.Original, annot))?.Rect;
        if (PdfRectangle.From(annot.Get(Rect)) is not { } rect) return null;
        // without an appearance the display draws a symbol at the top left corner of /Rect
        return kind == "Text" ? new PdfRectangle(rect.Left, rect.Top - AnnotationAppearance.NoteSize, rect.Left + AnnotationAppearance.NoteSize, rect.Top) : rect;
    }

    private static IReadOnlyList<PageNote> Replies(int index, PdfArray annots, PdfDictionary note, PdfRectangle rect)
    {
        List<PageNote>? replies = null;
        for (var i = 0; i < annots.Count; i++)
            if (annots.Get(i) is PdfDictionary answer && ReferenceEquals(answer.Get(Irt), note))
                (replies ??= []).Add(Note(index, answer, answer.GetName(PdfName.Subtype)?.Value ?? "", rect, []));
        return replies ?? (IReadOnlyList<PageNote>)[];
    }

    private static PageNote Note(int index, PdfDictionary annot, string kind, PdfRectangle rect, IReadOnlyList<PageNote> replies) =>
        new(index, annot, kind, rect, Read(annot, Contents) ?? "", Read(annot, T), Read(annot, Subj), PdfDate.Parse(Read(annot, M)), replies);

    private static string? Read(PdfDictionary annot, PdfName key) =>
        annot.Get(key) is PdfString text && text.ToText().Trim() is { Length: > 0 } value ? value : null;
}
