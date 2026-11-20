using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>An element with content on a page, in the order of the structure.</summary>
/// <param name="Number">Its place in the reading order of the page (from 1).</param>
/// <param name="Type">Its standard type (after the role map), or its own type where none can be told.</param>
/// <param name="Bounds">The bounds of its content on the page (page space, y up as in the file: Top is the smaller value).</param>
/// <param name="Alt">Its alternative text (/Alt); null if it has none.</param>
public sealed record ReadingOrderBox(int Number, string Type, RectD Bounds, string? Alt);

/// <summary>What the helps of the human check show on a page: the reading order of the structure and the areas painted as artifacts.</summary>
/// <param name="Order">The elements with content on the page, in the order of the structure.</param>
/// <param name="Artifacts">The areas painted as artifacts.</param>
public sealed record PageReadingOrder(IReadOnlyList<ReadingOrderBox> Order, IReadOnlyList<RectD> Artifacts);

/// <summary>
/// The reading order of a page as the structure tree gives it (docs/ABLAUFPLAN.md, 6.7 step 5, helps M1, M2, M6; docs/FEHLERGEDAECHTNIS.md,
/// No. 670): the elements in the order of the tree, each that holds content of the page directly (marked content of the page's own stream, by
/// MCID; annotations by their rectangle) numbered, with the bounds of that content (<see cref="MarkedContentGeometry"/>); and the areas painted
/// as artifacts. A person compares it with the page: a screen reader reads in this order.
/// </summary>
public static class ReadingOrder
{
    private const int MaxElements = 500_000, MaxDepth = 256;

    private static readonly PdfName S = PdfName.Get("S"), K = PdfName.Get("K"), Pg = PdfName.Get("Pg"), Mcid = PdfName.Get("MCID"), Obj = PdfName.Get("Obj"),
        Stm = PdfName.Get("Stm"), Alt = PdfName.Get("Alt"), Rect = PdfName.Get("Rect");

    /// <summary>The reading order and the artifacts of a page.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="cancel">Cancels the work.</param>
    public static PageReadingOrder Of(PdfDocument doc, PdfPage page, CancellationToken cancel = default)
    {
        var (bounds, artifacts) = MarkedContentGeometry.Measure(doc, page, cancel);
        var order = new List<ReadingOrderBox>();
        if (doc.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is not { } root || page.Id is not { } pageId) return new(order, artifacts);
        bool OnPage(PdfDictionary holder, PdfDictionary? inherited) =>
            (holder.GetRaw(Pg) ?? inherited?.GetRaw(Pg)) is PdfReference r && r.Id.Number == pageId.Number;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        // depth first in the order of /K; the page an element names (/Pg) holds for its content without one of its own
        var stack = new Stack<(PdfObject? Node, PdfDictionary? PageFrom, int Depth)>();
        stack.Push((root.Get(K), null, 0));
        while (stack.Count > 0 && seen.Count < MaxElements)
        {
            cancel.ThrowIfCancellationRequested();
            var (node, pageFrom, depth) = stack.Pop();
            if (depth > MaxDepth) continue;
            if (node is PdfArray list)
            {
                for (var i = Math.Min(list.Count, 100_000) - 1; i >= 0; i--) stack.Push((list.Get(i), pageFrom, depth));
                continue;
            }
            if (node is not PdfDictionary element || element.Get(S) is not PdfName || !seen.Add(element)) continue;
            var from = element.ContainsKey(Pg) ? element : pageFrom;
            var area = RectD.Empty;
            void Content(PdfObject? kid)
            {
                switch (kid)
                {
                    case PdfInteger id when from is not null && OnPage(from, null) && bounds.TryGetValue((int)Math.Clamp(id.Value, -1, int.MaxValue), out var b):
                        area = area.Union(b);
                        break;
                    // marked content in a form XObject (/Stm) is not measured: its MCIDs are the form's own
                    case PdfDictionary { } mcr when mcr.GetName(PdfName.Type)?.Value == "MCR" && !mcr.ContainsKey(Stm) && OnPage(mcr, from) &&
                                                    mcr.Get(Mcid) is PdfInteger mid && bounds.TryGetValue((int)Math.Clamp(mid.Value, -1, int.MaxValue), out var mb):
                        area = area.Union(mb);
                        break;
                    case PdfDictionary { } objr when objr.GetName(PdfName.Type)?.Value == "OBJR" && OnPage(objr, from) &&
                                                     objr.Get(Obj) is PdfDictionary annot && PdfRectangle.From(annot.Get(Rect)) is { } rect:
                        area = area.Union(RectD.FromLtrb(rect.Left, Math.Min(rect.Top, rect.Bottom), rect.Right, Math.Max(rect.Top, rect.Bottom)));
                        break;
                }
            }
            var kids = element.Get(K);
            if (kids is PdfArray all)
                for (var i = 0; i < all.Count && i < 100_000; i++) Content(all.Get(i));
            else Content(kids);
            if (!area.IsEmpty)
            {
                var type = StandardStructure.Resolve(element, root)?.Name ?? element.GetName(S)!.Value;
                order.Add(new ReadingOrderBox(order.Count + 1, type, area, element.Get(Alt) is PdfString alt ? alt.ToText() : null));
            }
            stack.Push((kids, from, depth + 1));
        }
        return new(order, artifacts);
    }
}
