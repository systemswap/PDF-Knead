using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Document;

/// <summary>A bookmark (outline item, ISO 32000-2, 12.3.3) with its target page and child items.</summary>
/// <param name="Title">The title; "…" if the item has none.</param>
/// <param name="PageIndex">Zero-based index of the target page, or null if the target cannot be resolved.</param>
/// <param name="Open">True if the item is shown expanded (positive /Count).</param>
/// <param name="Children">The child items, in document order.</param>
/// <param name="Target">The target with its view (place and zoom); null if it cannot be resolved.</param>
public sealed record OutlineItem(string Title, int? PageIndex, bool Open, IReadOnlyList<OutlineItem> Children, PdfDestination? Target = null);

/// <summary>The kinds of destination (ISO 32000-2, 12.3.2.2, Table 149).</summary>
public enum DestinationKind
{
    /// <summary>[page /XYZ left top zoom]: the point (left, top) at the upper left corner, the zoom; null keeps the current value.</summary>
    Xyz,
    /// <summary>The whole page in the window.</summary>
    Fit,
    /// <summary>The page width in the window, <see cref="PdfDestination.Top"/> at the top.</summary>
    FitH,
    /// <summary>The page height in the window, <see cref="PdfDestination.Left"/> at the left.</summary>
    FitV,
    /// <summary>The rectangle <see cref="PdfDestination.Rect"/> in the window.</summary>
    FitR,
    /// <summary>The bounding box of the page content in the window.</summary>
    FitB,
    /// <summary>The width of the bounding box, <see cref="PdfDestination.Top"/> at the top.</summary>
    FitBH,
    /// <summary>The height of the bounding box, <see cref="PdfDestination.Left"/> at the left.</summary>
    FitBV,
}

/// <summary>
/// A destination with its view (ISO 32000-2, 12.3.2.2): the page and how it is shown. Coordinates in default user space; a missing value
/// (null in the file) keeps the current one; a zoom of 0 counts as null.
/// </summary>
public sealed record PdfDestination(int PageIndex, DestinationKind Kind, double? Left = null, double? Top = null, double? Zoom = null,
    PdfRectangle? Rect = null);

/// <summary>
/// Reads the document outline (/Outlines) and resolves targets: direct destinations (/Dest), actions
/// (/A /GoTo) and named destinations (/Names /Dests or /Dests). Loops and excessive numbers of entries in
/// damaged files are caught.
/// </summary>
public static class PdfOutline
{
    private const int MaxItems = 100_000;

    /// <summary>Maximum number of name tree nodes visited (protection against crafted trees).</summary>
    private const int MaxNameTreeNodes = 200_000;

    /// <summary>Reads the outline of a document.</summary>
    /// <param name="doc">The document.</param>
    /// <returns>The top-level items; empty if the document has no outline or it cannot be read.</returns>
    /// <remarks>At most 100,000 items and 32 nesting levels are read; an item that appears a second time ends
    /// its list.</remarks>
    public static IReadOnlyList<OutlineItem> Read(PdfDocument doc)
    {
        var root = doc.Catalog?.GetDictionary(PdfName.Outlines);
        if (root is null) return [];
        var pages = PageNumbers(doc);
        var count = 0;
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        try
        {
            // Named destinations are built once as a table: searching the name tree for each bookmark is exponential
            // for crafted trees (a node that points to itself four times) and freezes the user interface
            var names = new Lazy<Dictionary<string, PdfObject>>(() => NameTable(doc));
            return Children(doc, root, pages, names, visited, ref count, 0);
        }
        catch (PdfException)
        {
            return [];
        }
    }

    private static List<OutlineItem> Children(PdfDocument doc, PdfDictionary parent, Dictionary<PdfObjectId, int> pages,
        Lazy<Dictionary<string, PdfObject>> names, HashSet<PdfDictionary> visited, ref int count, int depth)
    {
        var result = new List<OutlineItem>();
        if (depth > 32) return result;
        for (var node = parent.GetDictionary(PdfName.First); node is not null && count < MaxItems; node = node.GetDictionary(PdfName.Get("Next")))
        {
            if (!visited.Add(node)) break;   // loop
            count++;
            var title = node.GetString(PdfName.Get("Title"))?.ToText().Trim() ?? "";
            var page = Destination(doc, node, pages, names);
            var target = TargetOf(doc, node.Get(PdfName.Get("Dest")), node.GetDictionary(PdfName.Get("A")), pages);
            var open = (node.GetInteger(PdfName.Count) ?? 0) > 0;
            var children = Children(doc, node, pages, names, visited, ref count, depth + 1);
            result.Add(new OutlineItem(title.Length > 0 ? title : "…", target?.PageIndex ?? page, open, children, target));
        }
        return result;
    }

    /// <summary>Object number of each page → page index.</summary>
    private static Dictionary<PdfObjectId, int> PageNumbers(PdfDocument doc)
    {
        var map = new Dictionary<PdfObjectId, int>();
        foreach (var page in doc.Pages)
            if (page.Id is { } id) map.TryAdd(id, page.Index);
        return map;
    }

    private static int? Destination(PdfDocument doc, PdfDictionary item, Dictionary<PdfObjectId, int> pages,
        Lazy<Dictionary<string, PdfObject>> names)
    {
        var dest = item.Get(PdfName.Get("Dest"));
        if (dest is PdfNull && item.GetDictionary(PdfName.Get("A")) is { } action && action.GetName(PdfName.Get("S"))?.Value == "GoTo")
            dest = action.Get(PdfName.Get("D"));
        return ResolveDestination(doc, dest, pages, 0, names);
    }

    /// <summary>
    /// The target of a link or bookmark with its view: a GoTo action first (its structure destination /SD before /D, 12.6.4.2), then /Dest
    /// (an action wins over /Dest, 12.5.6.5); null if there is none or it cannot be resolved (docs/FEHLERGEDAECHTNIS.md, No. 563).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="dest">The /Dest entry (or null).</param>
    /// <param name="action">The /A entry (or null).</param>
    public static PdfDestination? Target(PdfDocument doc, PdfObject? dest, PdfDictionary? action) => TargetOf(doc, dest, action, PageNumbers(doc));

    private static PdfDestination? TargetOf(PdfDocument doc, PdfObject? dest, PdfDictionary? action, Dictionary<PdfObjectId, int> pages)
    {
        if (action is not null)
        {
            if (action.GetName(PdfName.Get("S"))?.Value != "GoTo") return null;
            if (action.Get(PdfName.Get("SD")) is PdfArray { Count: > 0 } sd && sd.Get(0) is PdfDictionary element && IsStructElement(element))
                return View(StructPage(element, pages, 0, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance)) ?? 0, sd);
            return Destination(doc, action.Get(PdfName.Get("D")), pages);
        }
        return dest is null or PdfNull ? null : Destination(doc, dest, pages);
    }

    /// <summary>A destination (explicit array, named, or a dictionary with /D) with its view; null if it cannot be resolved.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="dest">The destination object.</param>
    public static PdfDestination? Destination(PdfDocument doc, PdfObject? dest) => Destination(doc, dest, PageNumbers(doc));

    private static PdfDestination? Destination(PdfDocument doc, PdfObject? dest, Dictionary<PdfObjectId, int> pages)
    {
        if (dest is null || ExplicitDestination(doc, dest) is not { Count: > 0 } array) return null;
        int? page = array[0] switch
        {
            PdfReference r => pages.TryGetValue(r.Id, out var index) ? index : null,
            PdfInteger i when i.Value >= 0 && i.Value < doc.PageCount => (int)i.Value,
            _ => null,
        };
        return page is { } p ? View(p, array) : null;
    }

    /// <summary>The view of a destination array (its first element, the page, already resolved): kind and values (Table 149).</summary>
    private static PdfDestination View(int page, PdfArray array)
    {
        double? At(int i)
        {
            var v = i < array.Count ? array.Get(i).AsNumber() : null;
            return v is { } d && double.IsFinite(d) ? d : null;
        }
        var kind = (array.Count > 1 ? array.Get(1) as PdfName : null)?.Value;
        return kind switch
        {
            "Fit" => new PdfDestination(page, DestinationKind.Fit),
            "FitH" => new PdfDestination(page, DestinationKind.FitH, Top: At(2)),
            "FitV" => new PdfDestination(page, DestinationKind.FitV, Left: At(2)),
            "FitR" when At(2) is { } l && At(3) is { } b && At(4) is { } r && At(5) is { } t =>
                new PdfDestination(page, DestinationKind.FitR, Rect: new PdfRectangle(Math.Min(l, r), Math.Min(b, t), Math.Max(l, r), Math.Max(b, t))),
            "FitB" => new PdfDestination(page, DestinationKind.FitB),
            "FitBH" => new PdfDestination(page, DestinationKind.FitBH, Top: At(2)),
            "FitBV" => new PdfDestination(page, DestinationKind.FitBV, Left: At(2)),
            "XYZ" => new PdfDestination(page, DestinationKind.Xyz, At(2), At(3), At(4) is > 0 and var z ? z : null),
            // an unknown or broken kind: the page as a whole
            _ => new PdfDestination(page, DestinationKind.Fit),
        };
    }

    private static bool IsStructElement(PdfDictionary dict) =>
        dict.GetName(PdfName.Type)?.Value is null or "StructElem" && dict.ContainsKey(PdfName.Get("S"));

    /// <summary>
    /// The page of a structure element (12.3.2.3): its kids in order, the first piece of content decides — a marked-content id on the /Pg of
    /// its element, a marked-content or object reference with its own /Pg (or that of the element), a structure element recursively; null if
    /// there is none (then the first page). Depth and cycles are bounded.
    /// </summary>
    private static int? StructPage(PdfDictionary element, Dictionary<PdfObjectId, int> pages, int depth, HashSet<PdfDictionary> seen)
    {
        if (depth > 64 || !seen.Add(element)) return null;
        int? PageOf(PdfDictionary holder) =>
            (holder.GetRaw(PdfName.Get("Pg")) ?? element.GetRaw(PdfName.Get("Pg"))) is PdfReference r && pages.TryGetValue(r.Id, out var i) ? i : null;
        var kids = element.Get(PdfName.Get("K")) switch
        {
            PdfArray array => Enumerable.Range(0, Math.Min(array.Count, 10_000)).Select(array.Get).ToList(),
            PdfNull => [],
            var single => [single],
        };
        foreach (var kid in kids)
            switch (kid)
            {
                case PdfInteger:
                    return PageOf(element);
                case PdfDictionary dict when dict.GetName(PdfName.Type)?.Value is "MCR" or "OBJR":
                    if (PageOf(dict) is { } page) return page;
                    if (dict.GetDictionary(PdfName.Get("Obj"))?.GetRaw(PdfName.Get("P")) is PdfReference p && pages.TryGetValue(p.Id, out var annotPage)) return annotPage;
                    return null;
                case PdfDictionary child when IsStructElement(child):
                    if (StructPage(child, pages, depth + 1, seen) is { } found) return found;
                    break;
            }
        return null;
    }

    /// <summary>Resolves a destination to a page index.</summary>
    /// <param name="doc">The document the destination belongs to.</param>
    /// <param name="dest">An array [page /XYZ …], a dictionary with /D, or a name or string (named destination).</param>
    /// <param name="depth">Current indirection depth; resolution stops after four levels.</param>
    /// <returns>The zero-based page index, or null if the destination cannot be resolved.</returns>
    /// <remarks>Builds the page lookup table on every call, so the cost grows with the page count.</remarks>
    public static int? ResolveDestination(PdfDocument doc, PdfObject dest, int depth = 0) =>
        ResolveDestination(doc, dest, PageNumbers(doc), depth, null);

    private static int? ResolveDestination(PdfDocument doc, PdfObject dest, Dictionary<PdfObjectId, int> pages, int depth,
        Lazy<Dictionary<string, PdfObject>>? names)
    {
        if (depth > 4) return null;
        switch (dest)
        {
            case PdfArray { Count: > 0 } array:
                return array[0] switch
                {
                    PdfReference r => pages.TryGetValue(r.Id, out var index) ? index : null,
                    PdfInteger i when i.Value >= 0 && i.Value < doc.PageCount => (int)i.Value,   // destination in another file: page number
                    _ => null,
                };
            case PdfDictionary d:
                return ResolveDestination(doc, d.Get(PdfName.Get("D")), pages, depth + 1, names);
            case PdfName name:
                return NamedDestination(doc, name.Value, pages, depth, names);
            case PdfString s:
                return NamedDestination(doc, System.Text.Encoding.Latin1.GetString(s.Bytes), pages, depth, names);
            default:
                return null;
        }
    }

    /// <summary>
    /// Returns the destination as an explicit array [page /XYZ …], with named destinations and /D dictionaries
    /// resolved (when pages are inserted into another document, the source's names no longer apply).
    /// </summary>
    /// <param name="doc">The document the destination belongs to.</param>
    /// <param name="dest">The destination object.</param>
    /// <param name="depth">Current indirection depth; resolution stops after four levels.</param>
    /// <returns>The explicit destination array, or null if there is none.</returns>
    public static PdfArray? ExplicitDestination(PdfDocument doc, PdfObject dest, int depth = 0)
    {
        if (depth > 4) return null;
        switch (dest)
        {
            case PdfArray { Count: > 0 } array:
                return array;
            case PdfDictionary d:
                return ExplicitDestination(doc, d.Get(PdfName.Get("D")), depth + 1);
            case PdfName or PdfString:
                var name = dest is PdfName n ? n.Value : System.Text.Encoding.Latin1.GetString(((PdfString)dest).Bytes);
                var catalog = doc.Catalog;
                if (catalog?.GetDictionary(PdfName.Get("Dests"))?.Get(PdfName.Get(name)) is { } old and not PdfNull)
                    return ExplicitDestination(doc, old, depth + 1);
                var tree = catalog?.GetDictionary(PdfName.Get("Names"))?.GetDictionary(PdfName.Get("Dests"));
                return tree is not null && LookupNameTree(tree, name) is { } found ? ExplicitDestination(doc, found, depth + 1) : null;
            default:
                return null;
        }
    }

    private static int? NamedDestination(PdfDocument doc, string name, Dictionary<PdfObjectId, int> pages, int depth,
        Lazy<Dictionary<string, PdfObject>>? names)
    {
        // PDF 1.1: /Dests in the catalogue; from PDF 1.2: name tree /Names /Dests
        var catalog = doc.Catalog;
        if (catalog?.GetDictionary(PdfName.Get("Dests"))?.Get(PdfName.Get(name)) is { } old and not PdfNull)
            return ResolveDestination(doc, old, pages, depth + 1, names);
        PdfObject? found;
        if (names is not null) found = names.Value.GetValueOrDefault(name);
        else found = catalog?.GetDictionary(PdfName.Get("Names"))?.GetDictionary(PdfName.Get("Dests")) is { } tree ? LookupNameTree(tree, name) : null;
        return found is not null ? ResolveDestination(doc, found, pages, depth + 1, names) : null;
    }

    /// <summary>All named destinations of the name tree (/Names /Dests); for duplicate names, the first one wins.</summary>
    private static Dictionary<string, PdfObject> NameTable(PdfDocument doc)
    {
        var table = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        if (doc.Catalog?.GetDictionary(PdfName.Get("Names"))?.GetDictionary(PdfName.Get("Dests")) is not { } tree) return table;
        Walk(tree, null, node =>
        {
            if (node.GetArray(PdfName.Get("Names")) is { } names)
                for (var i = 0; i + 1 < names.Count && table.Count < MaxItems; i += 2)
                    if (names.Get(i) is PdfString s)
                        table.TryAdd(System.Text.Encoding.Latin1.GetString(s.Bytes), names.Get(i + 1));
            return null;
        });
        return table;
    }

    /// <summary>Name tree lookup (ISO 32000-2, 7.9.6): /Names [key value …] in leaves, /Kids with /Limits.</summary>
    internal static PdfObject? LookupNameTree(PdfDictionary root, string key) =>
        Walk(root, kid => kid.GetArray(PdfName.Get("Limits")) is { Count: >= 2 } limits &&
                          limits.Get(0) is PdfString lo && limits.Get(1) is PdfString hi &&
                          (string.CompareOrdinal(key, System.Text.Encoding.Latin1.GetString(lo.Bytes)) < 0 ||
                           string.CompareOrdinal(key, System.Text.Encoding.Latin1.GetString(hi.Bytes)) > 0),
            node =>
            {
                if (node.GetArray(PdfName.Get("Names")) is { } names)
                    for (var i = 0; i + 1 < names.Count; i += 2)
                        if (names.Get(i) is PdfString s && System.Text.Encoding.Latin1.GetString(s.Bytes) == key)
                            return names.Get(i + 1);
                return null;
            });

    /// <summary>
    /// Visits the nodes of the tree in order (depth first), each at most once and with a total limit. A recursive
    /// walk without a visited set is not safe: a node that points to itself several times in /Kids results in 4^32
    /// calls. Stops as soon as <paramref name="visit"/> returns something; <paramref name="skip"/> skips subtrees
    /// (bounds from /Limits).
    /// </summary>
    private static PdfObject? Walk(PdfDictionary root, Func<PdfDictionary, bool>? skip, Func<PdfDictionary, PdfObject?> visit)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { root };
        var stack = new Stack<(PdfDictionary Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && seen.Count <= MaxNameTreeNodes)
        {
            var (node, depth) = stack.Pop();
            if (visit(node) is { } found) return found;
            if (depth >= 32 || node.GetArray(PdfName.Kids) is not { } kids) continue;
            // Pushed in reverse so that the first subtree is visited first (document order)
            for (var i = kids.Count - 1; i >= 0; i--)
                if (kids.Get(i) is PdfDictionary kid && (skip is null || !skip(kid)) && seen.Add(kid))
                    stack.Push((kid, depth + 1));
        }
        return null;
    }
}
