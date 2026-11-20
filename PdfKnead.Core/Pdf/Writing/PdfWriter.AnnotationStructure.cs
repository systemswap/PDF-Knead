using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>Where the marked content of a page of the copy lies, looked up once per page (index).</summary>
        private readonly Dictionary<int, IReadOnlyDictionary<int, RectD>> _markedGeometry = [];

        /// <summary>
        /// A new annotation on a page of the main file in a tagged copy gets an element of its own (docs/ABLAUFPLAN.md, 6.4 step 2; ISO 14289-1,
        /// 7.18.1; ISO 14289-2, 8.9.2.1, 8.9.3.2; docs/FEHLERGEDAECHTNIS.md, No. 633): <paramref name="type"/> (Annot, or Form for a field) at the
        /// place the containment rules allow next to the content it sits on — the element of the marked content at its anchor point (a note:
        /// its corner; others: their middle) — else beside the last element of the page; an object reference to it there, and its
        /// /StructParent the new key. Invisible annotations and widgets of size 0 stay out of the tree.
        /// </summary>
        /// <returns>The element; null where the annotation stays out of the tree.</returns>
        private PdfObject? TagNewAnnotation(PdfDictionary annot, int number, PageItem item, int pageNumber, string type)
        {
            if (!ReferenceEquals(item.Source, _main) || StructureEdits() is not { } plan || PageIndexOf(pageNumber) is var page && page < 0) return null;
            var flags = annot.GetInteger(PdfName.F) ?? 0;
            if ((flags & 2) != 0 || (flags & 32) != 0 && (flags & 256) == 0) return null;
            if (Document.PdfRectangle.From(annot.Get(RectKey)) is not { } rect || rect.Width <= 0 || rect.Height <= 0) return null;
            var (x, y) = annot.GetName(PdfName.Subtype)?.Value == "Text" ? (rect.Left, rect.Top) : ((rect.Left + rect.Right) / 2, (rect.Bottom + rect.Top) / 2);
            if (!_markedGeometry.TryGetValue(page, out var geometry))
                _markedGeometry[page] = geometry = MarkedContentGeometry.Of(item.Source, item.Page, _cancel);
            var near = MarkedContentGeometry.At(geometry, x, y) is { } mcid ? plan.ElementOfMcid(page, mcid) : null;
            var element = plan.NewElement(type, near ?? plan.LastElementOnPage(page), page, besideAnchor: near is null);
            if (element is null) return null;
            annot.Set(StructParentKey, PdfInteger.Of(plan.AddObjectReference(element, page, new OutputReference(number))));
            return element;
        }

        /// <summary>
        /// The order chosen for the annotations of a page of a tagged copy carried into the tree (ISO 14289-1, 7.18.3; ISO 14289-2, 8.9.3.3;
        /// docs/FEHLERGEDAECHTNIS.md, No. 636): with /Tabs /S the order is that of their object references, so their elements are put in the
        /// chosen order among their siblings. Elements under different parents would need the tree rebuilt (and the reading order of the content
        /// changed): they stay, and the save says that the order follows the structure.
        /// </summary>
        /// <param name="elements">The element of each annotation in the chosen order; null for one without (not in the tree).</param>
        /// <returns>Whether the tree now gives exactly the chosen order.</returns>
        private bool TabOrderIntoTree(IReadOnlyList<PdfObject?> elements)
        {
            if (StructureEdits() is not { } plan || elements.Count == 0 || elements.Any(e => e is null)) return false;
            var parent = plan.View.Parent(elements[0]!);
            if (parent is null || elements.Any(e => !StructureView.Same(plan.View.Parent(e!), parent))) return false;
            return plan.OrderKids(parent, [.. elements.Select(e => e!)]);
        }

        /// <summary>
        /// PDF-Knead writes its own annotations anew on every save (<see cref="OwnedAnnotations"/>), and drops those deleted: the old ones of a
        /// page of the main file leave the tree before the page's new ones go in — their object reference, their key, and their element where
        /// nothing else is left in it (<see cref="StructurePlan.RemoveIfEmpty"/>). Otherwise dead elements would grow with every save; taken
        /// out by pruning instead, a tree holding nothing but such elements would be left empty and the copy untagged.
        /// </summary>
        private void ForgetRewrittenAnnotations(PageItem item)
        {
            if (!ReferenceEquals(item.Source, _main) || StructureEdits() is not { } plan || item.Page.Dictionary.Get(Annots) is not PdfArray annots) return;
            var own = ElementStore.For(item.Source, item.Page).Annotations;
            if (own.Count == 0) return;
            for (var i = 0; i < annots.Count && i < 100_000; i++)
            {
                if (annots[i] is not PdfReference reference || annots.Get(i) is not PdfDictionary annot || !own.Contains(annot) ||
                    NumberTree.KeyOf(annot.Get(StructParentKey)) is not { } key || plan.ElementForKey(key) is not { } element) continue;
                if (plan.RemoveObjectReference(element, reference)) plan.RemoveIfEmpty(element);
                else plan.RemoveKey(key);
            }
        }
    }
}
