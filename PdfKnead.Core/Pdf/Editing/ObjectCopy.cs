using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Objects taken out of their document (B7, pasting copied objects into another PDF): every reference resolved and copied, streams with their
/// data still encoded (images keep their quality) — the copy needs nothing of the document any more, also after it was closed.
/// </summary>
public static class ObjectCopy
{
    /// <summary>At most this many objects are copied: what a copied form needs is far less; more means a reference into the whole document.</summary>
    private const int MaxObjects = 200_000;

    /// <summary>The object with everything it refers to, without references (shared objects stay shared in the copy).</summary>
    /// <param name="doc">The document of the object.</param>
    /// <param name="obj">The object.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="PdfException">It refers to more than <see cref="MaxObjects"/> objects.</exception>
    public static PdfObject Standalone(PdfDocument doc, PdfObject obj) => new Copier(doc).Copy(obj, 0);

    private sealed class Copier(PdfDocument doc)
    {
        private readonly Dictionary<int, PdfObject> _copied = [];   // by object number: shared ones and cycles
        private readonly Dictionary<PdfObject, PdfObject> _direct = new(ReferenceEqualityComparer.Instance);
        private int _count;

        public PdfObject Copy(PdfObject obj, int depth)
        {
            if (depth > 256) return PdfNull.Instance;   // a chain of direct objects this deep is no real content
            if (obj is PdfReference reference)
            {
                if (_copied.TryGetValue(reference.Id.Number, out var known)) return known;
                PdfObject target;
                try
                {
                    target = doc.Resolve(reference.Id);
                }
                catch (PdfException)
                {
                    return PdfNull.Instance;   // a reference into nothing
                }
                return Fresh(target, depth, reference.Id.Number);
            }
            return obj switch
            {
                PdfDictionary or PdfArray or PdfStream => _direct.TryGetValue(obj, out var copy) ? copy : Fresh(obj, depth, null),
                _ => obj,   // numbers, names, strings, booleans, null: unchangeable
            };
        }

        private PdfObject Fresh(PdfObject obj, int depth, int? number)
        {
            if (++_count > MaxObjects) throw new PdfException(Tr.T("Die kopierten Objekte verweisen auf zu viele weitere Objekte."));
            switch (obj)
            {
                case PdfStream stream:
                {
                    var dict = new PdfDictionary(stream.Dictionary.Count);
                    var copy = new PdfStream(dict, doc.GetRawStreamData(stream));
                    Remember(obj, number, copy);
                    foreach (var (key, value) in stream.Dictionary.Entries)
                        if (key.Value is not ("Length" or "Parent")) dict.Set(key, Copy(value, depth + 1));
                    return copy;
                }
                case PdfDictionary source:
                {
                    var copy = new PdfDictionary(source.Count);
                    Remember(obj, number, copy);
                    // the page tree (/Parent, /P) would bring the whole document along
                    foreach (var (key, value) in source.Entries)
                        if (key.Value is not ("Parent" or "P" or "StructParent" or "StructParents")) copy.Set(key, Copy(value, depth + 1));
                    return copy;
                }
                case PdfArray source:
                {
                    var copy = new PdfArray();
                    Remember(obj, number, copy);
                    foreach (var item in source) copy.Add(Copy(item, depth + 1));
                    return copy;
                }
                default:
                    if (number is { } n) _copied[n] = obj;
                    return obj;
            }
        }

        private void Remember(PdfObject source, int? number, PdfObject copy)
        {
            if (number is { } n) _copied[n] = copy;
            else _direct[source] = copy;
        }
    }
}
