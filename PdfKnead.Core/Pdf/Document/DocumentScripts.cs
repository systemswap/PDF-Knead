using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Document;

/// <summary>
/// The scripts of a document as text (never run): the JavaScript of the document (name tree /JavaScript, open action, additional actions of the
/// catalogue), of the pages and their annotations and fields (/A, /AA, chains through /Next), of the outline — a /JS string or stream; at most
/// 20,000 actions. Shared by the search for redacted text outside the page and by the writer, which renames an attachment only where no script
/// names it (ISO 32000-2, 7.7.4, sentence 47; docs/FEHLERGEDAECHTNIS.md, No. 909).
/// </summary>
public static class DocumentScripts
{
    private const int MaxActions = 20_000;

    /// <summary>Whether the text of a script passes <paramref name="has"/>.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="has">The test of a script's text (null: a script whose text cannot be read); asked only for actions with /JS.</param>
    /// <param name="incomplete">The answer when not every script could be visited (more than 20,000 actions).</param>
    public static bool Any(PdfDocument doc, Func<string?, bool> has, bool incomplete = false)
    {
        var budget = MaxActions;
        var exhausted = false;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        bool Action(PdfObject? value, int depth)
        {
            if (value is PdfReference reference) value = doc.Resolve(reference.Id);
            if (depth > 16) return false;
            if (--budget < 0)
            {
                exhausted = true;
                return false;
            }
            switch (value)
            {
                case PdfArray chain:
                    return Enumerable.Range(0, Math.Min(chain.Count, 1000)).Any(i => Action(chain.Get(i), depth + 1));
                case PdfDictionary action when seen.Add(action):
                    // only an action with a script asks: its text, or null where it cannot be read (a stream over 16 MB, another type)
                    if (action.Get(PdfName.Get("JS")) is not PdfNull and var js)
                    {
                        var text = js switch
                        {
                            PdfString str => str.ToText(),
                            PdfStream stream when stream.DataLength <= 16 << 20 => System.Text.Encoding.Latin1.GetString(doc.GetStreamData(stream)),
                            _ => null,
                        };
                        if (has(text)) return true;
                    }
                    return Action(action.Get(PdfName.Get("Next")), depth + 1);
                default:
                    return false;
            }
        }
        bool Additional(PdfDictionary? holder) =>
            holder is not null && (Action(holder.Get(PdfName.Get("A")), 0) ||
                                   holder.GetDictionary(PdfName.Get("AA")) is { } aa && aa.Entries.Take(100).Any(e => Action(aa.Get(e.Key), 0)));
        var catalog = doc.Catalog;
        if (catalog is null) return false;
        if (Action(catalog.Get(PdfName.Get("OpenAction")), 0) || Additional(catalog)) return true;
        if (catalog.GetDictionary(PdfName.Get("Names"))?.GetDictionary(PdfName.Get("JavaScript")) is { } scripts &&
            NameTree.Pairs(scripts).Take(MaxActions).Any(p => Action(p.Value, 0)))
            return true;
        foreach (var page in doc.Pages.Take(10_000))
        {
            if (Additional(page.Dictionary)) return true;
            if (page.Dictionary.Get(PdfName.Get("Annots")) is PdfArray annots)
                for (var i = 0; i < annots.Count && i < 10_000; i++)
                    if (annots.Get(i) is PdfDictionary annot && Additional(annot)) return true;
        }
        for (var item = catalog.GetDictionary(PdfName.Get("Outlines"))?.GetDictionary(PdfName.Get("First")); item is not null && budget > 0;
             item = item.GetDictionary(PdfName.Get("Next")))
        {
            if (!seen.Add(item)) break;
            if (Action(item.Get(PdfName.Get("A")), 0)) return true;
        }
        return (exhausted || budget <= 0) && incomplete;
    }
}
