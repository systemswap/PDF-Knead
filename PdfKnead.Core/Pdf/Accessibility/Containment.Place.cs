using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>A place for a new structure element: the parent (a handle of <see cref="StructureView"/>) and the index in its /K it goes to.</summary>
internal readonly record struct Placement(PdfObject Parent, int Index);

public static partial class Containment
{
    /// <summary>
    /// Where a new element of <paramref name="newType"/> goes so that the tree keeps the containment rules (docs/ABLAUFPLAN.md, 6.2 step 3;
    /// ISO 32000-2, Annex L; ISO/TS 32005, 7.2): into the anchor at its end where the anchor may hold it; else, from a list, list item, table,
    /// table section or row, down into the last item body or cell that may; else up to the first ancestor that may, right after the branch the
    /// anchor is in — the reading order stays close to the content (ISO 14289-2, 8.9.3.2). Never into the root: with no place in the tree
    /// (a broken one) null, and the caller puts the element at the end of the top Document.
    /// </summary>
    /// <param name="view">The tree as it stands.</param>
    /// <param name="anchor">The element the new one belongs to (where the content lies it replaces or stands beside).</param>
    /// <param name="newType">The standard type of the new element.</param>
    /// <param name="beside">
    /// Only beside the anchor, never into it or below it: for content that belongs to no element and only takes its place in the reading
    /// order after the anchor (an annotation without an element, flattened — not part of the comment drawn before it).
    /// </param>
    internal static Placement? PlaceFor(StructureView view, PdfObject anchor, string newType, bool beside = false)
    {
        if (view.Element(anchor) is null) return null;
        if (!beside)
        {
            if (Takes(view, anchor, newType)) return new Placement(anchor, view.Items(anchor).Count);
            var down = anchor;
            for (var depth = 0; depth < 8 && Inward(view, down) is { } inner; depth++)
            {
                if (Takes(view, inner, newType)) return new Placement(inner, view.Items(inner).Count);
                down = inner;
            }
        }
        var branch = anchor;
        for (var step = 0; step < 256 && view.Parent(branch) is { } parent && view.Element(parent) is not null; step++)
        {
            if (Takes(view, parent, newType))
            {
                var items = view.Items(parent);
                var at = Enumerable.Range(0, items.Count).FirstOrDefault(i => StructureView.Same(items[i], branch), -1);
                return new Placement(parent, at < 0 ? items.Count : at + 1);
            }
            branch = parent;
        }
        return null;
    }

    /// <summary>
    /// Whether an element may take one more kid of <paramref name="newType"/>: its standard type is known to the facts (NonStruct takes the
    /// rules of the nearest ancestor that is not NonStruct), the pair is allowed — as a grouping element where it holds no content directly — and
    /// the kid would not go over the most the pair allows.
    /// </summary>
    private static bool Takes(StructureView view, PdfObject element, string newType)
    {
        var holder = element;
        var type = view.TypeOf(holder);
        for (var step = 0; step < 256 && type == "NonStruct" && view.Parent(holder) is { } up && view.Element(up) is not null; step++)
            type = view.TypeOf(holder = up);
        if (type is null or "NonStruct" || !Known(type)) return false;
        var groups = !view.HoldsContent(element);
        if (!Allowed(type, newType, groups)) return false;
        return Most(type, newType, groups) is not { } most || view.Kids(element).Count(k => view.TypeOf(k) is { } t && Key(t) == Key(newType)) < most;
    }

    /// <summary>Where lists and tables hold their content: the last item of a list, an item's body, the last row of a table or section, a row's last cell.</summary>
    private static PdfObject? Inward(StructureView view, PdfObject element)
    {
        PdfObject? Last(params string[] types) => view.Kids(element).LastOrDefault(k => view.TypeOf(k) is { } t && types.Contains(t));
        return view.TypeOf(element) switch
        {
            "L" => Last("LI"),
            "LI" => Last("LBody"),
            "Table" => Last("TR", "THead", "TBody", "TFoot"),
            "THead" or "TBody" or "TFoot" => Last("TR"),
            "TR" => Last("TD", "TH"),
            _ => null,
        };
    }
}
