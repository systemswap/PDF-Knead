using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Building blocks for incremental updates (<see cref="IncrementalUpdate"/>): copying objects of another document into the update,
/// hanging annotations onto a page, walking the field tree. Shared by signing and by appending allowed changes to signed files.
/// </summary>
internal static class IncrementalParts
{
    private static readonly PdfName Annots = PdfName.Get("Annots"), Title = PdfName.Get("T");

    /// <summary>
    /// A shallow copy of a dictionary (values as written, references stay references) that resolves references like the original:
    /// without the resolver, reading an indirect value such as /Fields 12 0 R from the copy would give nothing, and an update built on
    /// it would replace the list by an empty one (Fehlergedächtnis 105).
    /// </summary>
    public static PdfDictionary Copy(PdfDictionary source)
    {
        var copy = new PdfDictionary(source.Count + 2) { Resolver = source.Resolver };
        foreach (var (key, value) in source.Entries) copy.Set(key, value);
        return copy;
    }

    /// <summary>
    /// Copies an object of another document into the update, with all objects it refers to (each once, cycles end at the number
    /// reserved first). References already in <paramref name="copied"/> are taken as they are — seeded with the pages of the other
    /// document mapped to those of the updated file, an annotation's /P and the like point to the right page instead of copying the
    /// other document's page tree.
    /// </summary>
    /// <param name="source">The document the object comes from.</param>
    /// <param name="update">The update to add to.</param>
    /// <param name="obj">The object, as written in <paramref name="source"/>.</param>
    /// <param name="copied">Numbers of <paramref name="source"/> already copied, and their reference in the update.</param>
    /// <param name="depth">Nesting depth (0 at the start).</param>
    /// <returns>The copy, with references into the update.</returns>
    /// <exception cref="PdfLimitException">The objects are nested too deeply.</exception>
    public static PdfObject Graft(PdfDocument source, IncrementalUpdate update, PdfObject obj, Dictionary<PdfObjectId, PdfReference> copied, int depth = 0)
    {
        if (depth > PdfLimits.MaxNesting) throw new PdfLimitException(Tr.T("Objekte sind zu tief verschachtelt."));
        switch (obj)
        {
            case PdfReference reference:
                if (copied.TryGetValue(reference.Id, out var done)) return done;
                var target = source.Resolve(reference.Id);
                // Reserve the number first, so that cycles end here; the content follows
                var placeholder = update.Add(PdfNull.Instance);
                copied[reference.Id] = placeholder;
                update.Replace(placeholder.Id, Graft(source, update, target, copied, depth + 1));
                return placeholder;
            case PdfStream stream:
                var dict = (PdfDictionary)Graft(source, update, stream.Dictionary, copied, depth + 1);
                return new PdfStream(dict, source.GetRawStreamData(stream));
            case PdfDictionary dictionary:
                var copy = new PdfDictionary(dictionary.Count);
                foreach (var (key, value) in dictionary.Entries) copy.Set(key, Graft(source, update, value, copied, depth + 1));
                return copy;
            case PdfArray array:
                return new PdfArray(array.Select(item => Graft(source, update, item, copied, depth + 1)));
            default:
                return obj;
        }
    }

    /// <summary>Adds annotations to the /Annots of a page (an indirect array is replaced as an object, otherwise the page).</summary>
    /// <param name="update">The update.</param>
    /// <param name="page">The page of the updated file.</param>
    /// <param name="pageId">Its object number.</param>
    /// <param name="added">References of the annotations (already in the update).</param>
    /// <param name="tabsByStructure">
    /// The page of a tagged file gets /Tabs /S if it has another order (ISO 14289-1, 7.18.3; ISO 14289-2, 8.9.3.3; docs/FEHLERGEDAECHTNIS.md,
    /// No. 635).
    /// </param>
    public static void AddAnnotations(IncrementalUpdate update, PdfPage page, PdfObjectId pageId, IEnumerable<PdfReference> added, bool tabsByStructure = false)
    {
        var raw = page.Dictionary.GetRaw(Annots);
        var annots = page.Dictionary.GetArray(Annots) is { } list ? new PdfArray(list) : new PdfArray();
        foreach (var reference in added) annots.Add(reference);
        var tabs = tabsByStructure && page.Dictionary.GetName(Tabs)?.Value != "S";
        if (raw is PdfReference annotsRef)
        {
            update.Replace(annotsRef.Id, annots);
            if (!tabs) return;
        }
        var copy = Copy(page.Dictionary);
        if (raw is not PdfReference) copy.Set(Annots, annots);
        if (tabs) copy.Set(Tabs, PdfName.Get("S"));
        update.Replace(pageId, copy);
    }

    private static readonly PdfName Tabs = PdfName.Get("Tabs");

    /// <summary>The fields of a field list and their descendants, with their full names (bounded against prepared files).</summary>
    /// <param name="doc">The document.</param>
    /// <param name="fields">The /Fields array (or /Kids).</param>
    /// <param name="prefix">Full name of the parent ("" at the top).</param>
    /// <param name="depth">Nesting depth (0 at the top).</param>
    /// <returns>Object number, dictionary and full name of every field and widget that is an object of its own.</returns>
    public static IEnumerable<(PdfObjectId Id, PdfDictionary Field, string Name)> Walk(PdfDocument doc, PdfArray fields, string prefix = "", int depth = 0)
    {
        if (depth > 32) yield break;
        for (var i = 0; i < Math.Min(fields.Count, 10_000); i++)
        {
            if (fields[i] is not PdfReference reference || doc.Resolve(reference.Id) is not PdfDictionary field) continue;
            var part = field.GetString(Title)?.ToText();
            var full = part is null ? prefix : prefix.Length == 0 ? part : $"{prefix}.{part}";
            yield return (reference.Id, field, full);
            if (field.GetArray(PdfName.Kids) is { } kids)
                foreach (var kid in Walk(doc, kids, full, depth + 1)) yield return kid;
        }
    }
}
