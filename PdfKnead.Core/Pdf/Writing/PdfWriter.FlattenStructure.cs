using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>
        /// The marked content around what an annotation draws into the content when it is flattened: the type of its structure element with
        /// the MCID the element got, or — with no element — an artifact of the layout (<see cref="Mcid"/> null).
        /// </summary>
        private sealed record ContentMark(PdfName Tag, int? Mcid);

        /// <summary>The page of the copy (index) by its object number (built once).</summary>
        private int PageIndexOf(int number)
        {
            _pageIndexByNumber ??= _pageNumbers.Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i);
            return _pageIndexByNumber.TryGetValue(number, out var index) ? index : -1;
        }

        private Dictionary<int, int>? _pageIndexByNumber;

        /// <summary>
        /// The structure of an annotation of the main file flattened in a tagged copy (docs/ABLAUFPLAN.md, 6.4 step 1; D-B3 with G2;
        /// docs/FEHLERGEDAECHTNIS.md, No. 632), through the <see cref="StructurePlan"/>:
        /// <list type="bullet">
        /// <item>with an element (its /StructParent in the parent tree): a free MCID of the page; in the element the object reference becomes that
        /// MCID, the key of the annotation leaves the parent tree. A field's element gets the attribute PrintField (role, state, /TU as /Desc —
        /// never as /Alt: the tooltip would replace the printed value); a comment that does not show its text itself gets its /Contents as
        /// /Alt (a free text shows it: none).</item>
        /// <item>without one, with /Contents: an element of its own at a place the containment rules allow (P for a free text or a field,
        /// else a Figure with /Alt).</item>
        /// <item>without either: an artifact of the layout.</item>
        /// <item>drawing nothing (hidden, for print not printed, no appearance): its object reference and key leave the tree, so that nothing
        /// fetches the annotation into the file; its element goes when nothing else is left in it.</item>
        /// </list>
        /// </summary>
        /// <param name="annot">The annotation (as moved, where it was).</param>
        /// <param name="entry">Its entry in /Annots (the reference an object reference names).</param>
        /// <param name="source">Its file.</param>
        /// <param name="pageNumber">The object number of its page in the copy.</param>
        /// <param name="drawn">Whether it draws anything into the content.</param>
        /// <returns>The marked content around what it draws; null where the copy keeps no structure of its file.</returns>
        private ContentMark? FlattenedIntoContent(PdfDictionary annot, PdfObject entry, Source source, int pageNumber, bool drawn)
        {
            if (!source.IsMain || StructureEdits() is not { } plan || PageIndexOf(pageNumber) is var page && page < 0) return null;
            var key = NumberTree.KeyOf(annot.Get(StructParentKey));
            var element = key is { } own ? plan.ElementForKey(own) : null;
            if (!drawn)
            {
                // the element left without kids goes as well: an empty Form breaks ISO 14289-1, 7.18.4 (one widget each) and holds nothing
                // (docs/FEHLERGEDAECHTNIS.md, No. 673)
                if (element is not null)
                {
                    if (plan.RemoveObjectReference(element, entry)) plan.RemoveIfEmpty(element);
                    else if (key is { } stale) plan.RemoveKey(stale);
                }
                return null;
            }
            var subtype = annot.GetName(PdfName.Subtype)?.Value;
            var contents = annot.Get(PdfName.Contents) is PdfString { Bytes.Length: > 0 } text ? text.ToText().Trim() : "";
            if (element is not null)
            {
                var mcid = plan.NextMcid(page);
                if (!plan.ReplaceObjectReference(element, entry, page, mcid))
                {
                    // the pair named the element, its /K did not: the content goes to the element all the same, the key goes
                    plan.AddMarkedContent(element, page, mcid);
                    plan.RemoveKey(key!.Value);
                }
                if (subtype == "Widget") plan.AddAttributes(element, PrintField(annot));
                else if (subtype != "FreeText" && contents.Length > 0) plan.DescribeIfUndescribed(element, contents);
                return new ContentMark(plan.View.Element(element)?.GetName(PdfName.Get("S")) ?? PdfName.Get("Span"), mcid);
            }
            if (contents.Length > 0)
            {
                var type = subtype is "FreeText" or "Widget" ? "P" : "Figure";
                if (plan.NewElement(type, plan.LastElementOnPage(page), page, alt: type == "Figure" ? contents : null, besideAnchor: true) is { } made)
                {
                    var mcid = plan.NextMcid(page);
                    plan.AddMarkedContent(made, page, mcid);
                    return new ContentMark(PdfName.Get(type), mcid);
                }
            }
            return new ContentMark(PdfName.Get("Artifact"), null);
        }

        /// <summary>
        /// The attribute PrintField of a flattened field (ISO 32000-2, 14.8.5.6, Table 383): the role by /FT and /Ff (radio button, push button,
        /// check box, list box, else a text value), the state of a check box or radio button (/Checked in 2.0, /checked before), /TU as /Desc.
        /// </summary>
        private PdfDictionary PrintField(PdfDictionary widget)
        {
            var type = FieldValue(widget, PdfName.Get("FT")) is PdfName ft ? ft.Value : "";
            var flags = FieldValue(widget, PdfName.Get("Ff")) is PdfInteger ff ? ff.Value : 0;
            var role = type switch
            {
                "Btn" when (flags & (1 << 15)) != 0 => "rb",
                "Btn" when (flags & (1 << 16)) != 0 => "pb",
                "Btn" => "cb",
                "Ch" when (flags & (1 << 17)) == 0 => "lb",
                _ => "tv",
            };
            var attributes = new PdfDictionary();
            attributes.Set(PdfName.Get("O"), PdfName.Get("PrintField"));
            attributes.Set(PdfName.Get("Role"), PdfName.Get(role));
            if (role is "rb" or "cb")
                attributes.Set(PdfName.Get(Pdf20 ? "Checked" : "checked"),
                    PdfName.Get(widget.GetName(PdfName.Get("AS")) is { } state && state.Value != "Off" ? "on" : "off"));
            if (FieldValue(widget, PdfName.Get("TU")) is PdfString { Bytes.Length: > 0 } tooltip) attributes.Set(PdfName.Get("Desc"), tooltip);
            return attributes;
        }
    }
}
