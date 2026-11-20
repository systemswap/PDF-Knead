using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Document;

/// <summary>
/// Every object reachable from given roots — the place-independent way to find what can hang almost anywhere (associated files, ISO 32000-2
/// 14.13; object metadata, 14.3.2), instead of listing places (docs/FEHLERGEDAECHTNIS.md, No. 443 and 446).
/// </summary>
public static class ReachableObjects
{
    /// <summary>At most this many objects are visited.</summary>
    public const int MaxObjects = 200_000;

    /// <summary>
    /// Visits every object reachable from the roots by reference, each once. Pages not in <paramref name="livePages"/> are not entered: a
    /// page tree, a destination or the structure tree still names deleted pages, which are not written.
    /// </summary>
    /// <param name="roots">The roots with the document each belongs to (pages of other files bring their own).</param>
    /// <param name="livePages">The page dictionaries kept; null: every page.</param>
    /// <param name="visit">Called with each dictionary and stream (and its document).</param>
    public static void Visit(IEnumerable<(PdfDocument Doc, PdfObject Root)> roots, IReadOnlySet<PdfDictionary>? livePages,
        Action<PdfDocument, PdfObject> visit)
    {
        var seen = new HashSet<(PdfDocument, int)>();
        var visited = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(PdfDocument, PdfObject)>(roots);
        var count = 0;
        while (queue.TryDequeue(out var next) && count++ < MaxObjects) Walk(next.Item1, next.Item2, 0);

        void Walk(PdfDocument doc, PdfObject obj, int depth)
        {
            if (depth > 32) return;
            switch (obj)
            {
                case PdfReference r:
                    if (!seen.Add((doc, r.Id.Number))) return;
                    try
                    {
                        queue.Enqueue((doc, doc.GetObject(r.Id.Number)));
                    }
                    catch (PdfException)
                    {
                        // an object that cannot be read holds nothing to find
                    }
                    return;
                case PdfStream stream:
                    if (!visited.Add(stream)) return;
                    visit(doc, stream);
                    Entries(doc, stream.Dictionary, depth);
                    return;
                case PdfDictionary dict:
                    if (!visited.Add(dict) || livePages is not null && dict.IsType(PdfName.Get("Page")) && !livePages.Contains(dict)) return;
                    visit(doc, dict);
                    Entries(doc, dict, depth);
                    return;
                case PdfArray array:
                    for (var i = 0; i < array.Count && i < 100_000; i++) Walk(doc, array[i], depth + 1);
                    return;
            }
        }

        void Entries(PdfDocument doc, PdfDictionary dict, int depth)
        {
            foreach (var (key, _) in dict.Entries)
                if (dict.GetRaw(key) is { } value) Walk(doc, value, depth + 1);
        }
    }

    /// <summary>A file specification as a dictionary: /Type /Filespec, or an /EF with the embedded file.</summary>
    public static bool IsFileSpecification(PdfDictionary dict) => dict.IsType(PdfName.Get("Filespec")) || dict.ContainsKey(PdfName.Get("EF"));
}
