using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>
/// What the order of the headings and the references between elements of a structure tree say (docs/ABLAUFPLAN.md, 6.6 h): whether the numbered
/// headings begin with H1 and go down one level at most at a time (ISO 14289-1, 7.4.2; Matterhorn 14-002, 14-003), table cells whose /Headers
/// name an ID no element has (ISO 14289-1, 7.5; ISO 14289-2, 8.2.5.26), and /Ref entries that name no element (ISO 32000-2, 14.7.2, Table 355).
/// </summary>
internal static class StructureReferences
{
    private static readonly PdfName StructTreeRoot = PdfName.Get("StructTreeRoot"), A = PdfName.Get("A"), O = PdfName.Get("O"),
        Headers = PdfName.Get("Headers"), Id = PdfName.Get("ID"), Ref = PdfName.Get("Ref");

    /// <summary>The elements of a tree in reading order (depth first), at most 200 000.</summary>
    internal static IEnumerable<(PdfObject Handle, PdfDictionary Element)> Elements(StructureView view)
    {
        var stack = new Stack<(PdfObject Handle, int Depth)>();
        var root = view.Root;
        foreach (var kid in view.Items(root).Reverse()) stack.Push((kid, 0));
        var visited = new HashSet<int>();
        var budget = 200_000;
        while (stack.Count > 0 && --budget > 0)
        {
            var (handle, depth) = stack.Pop();
            if (handle is PdfReference r && !visited.Add(r.Id.Number)) continue;   // a cycle or a shared element: once
            if (view.Element(handle) is not { } element) continue;
            yield return (handle, element);
            if (depth < 128)
                foreach (var kid in view.Items(handle).Reverse()) stack.Push((kid, depth + 1));
        }
    }

    /// <summary>
    /// The levels of the numbered headings (H1 to H6, and Hn above 6 as written) in reading order; the plain heading H counts as none (PDF/UA-1
    /// uses either the one or the other).
    /// </summary>
    public static List<int> HeadingLevels(PdfDocument doc) =>
        doc.Catalog?.GetDictionary(StructTreeRoot) is { } root ? HeadingLevels(StructureView.Of(doc, root)) : [];

    /// <summary>The levels of the numbered headings of a tree as a view shows it (as <see cref="HeadingLevels(PdfDocument)"/>).</summary>
    public static List<int> HeadingLevels(StructureView view)
    {
        var levels = new List<int>();
        foreach (var (handle, _) in Elements(view))
            if (view.TypeOf(handle) is { Length: >= 2 } type && type[0] == 'H' && int.TryParse(type.AsSpan(1), out var level) && level >= 1)
                levels.Add(level);
        return levels;
    }

    /// <summary>Whether a sequence of heading levels breaks the order: the first is not 1, or one goes down more than one level.</summary>
    public static bool OrderBroken(IReadOnlyList<int> levels)
    {
        if (levels.Count == 0) return false;
        if (levels[0] != 1) return true;
        for (var i = 1; i < levels.Count; i++)
            if (levels[i] > levels[i - 1] + 1) return true;
        return false;
    }

    /// <summary>The IDs (as bytes, hexadecimal) of all elements of the tree.</summary>
    private static HashSet<string> Ids(StructureView view)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, element) in Elements(view))
            if (element.Get(Id) is PdfString id) ids.Add(Convert.ToHexString(id.Bytes));
        return ids;
    }

    /// <summary>The attribute objects of an element (/A: one, or an array of them with revision numbers between).</summary>
    internal static IEnumerable<PdfDictionary> Attributes(PdfDictionary element, Func<PdfObject?, PdfObject?> resolve)
    {
        switch (resolve(element.GetRaw(A)))
        {
            case PdfDictionary one:
                yield return one;
                break;
            case PdfArray list:
                for (var i = 0; i < list.Count && i < 256; i++)
                    if (resolve(list[i]) is PdfDictionary attributes) yield return attributes;
                break;
        }
    }

    /// <summary>Entries of /Headers (table attributes) naming an ID no element of the tree has.</summary>
    public static int DanglingHeaders(PdfDocument doc)
    {
        if (doc.Catalog?.GetDictionary(StructTreeRoot) is not { } root) return 0;
        var view = StructureView.Of(doc, root);
        var ids = Ids(view);
        var dangling = 0;
        foreach (var (_, element) in Elements(view))
            foreach (var attributes in Attributes(element, view.Resolve))
                if (attributes.GetName(O)?.Value == "Table" && view.Resolve(attributes.GetRaw(Headers)) is PdfArray headers)
                    for (var i = 0; i < headers.Count && i < 1024; i++)
                        if (view.Resolve(headers[i]) is PdfString id && !ids.Contains(Convert.ToHexString(id.Bytes))) dangling++;
        return dangling;
    }

    /// <summary>Entries of /Ref naming no structure element (null, a missing object, anything else).</summary>
    public static int DanglingRefs(PdfDocument doc)
    {
        if (doc.Catalog?.GetDictionary(StructTreeRoot) is not { } root) return 0;
        var view = StructureView.Of(doc, root);
        var dangling = 0;
        foreach (var (_, element) in Elements(view))
            switch (view.Resolve(element.GetRaw(Ref)))
            {
                case PdfArray refs:
                    for (var i = 0; i < refs.Count && i < 1024; i++)
                        if (view.Element(refs[i]) is null) dangling++;
                    break;
                case null:
                    break;
                case var one when view.Element(element.GetRaw(Ref)) is null && one is not PdfArray:
                    dangling++;
                    break;
            }
        return dangling;
    }
}
