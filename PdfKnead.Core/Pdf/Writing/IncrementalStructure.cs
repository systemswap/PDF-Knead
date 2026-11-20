using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// A new widget added by an incremental update (a visible signature) in a tagged file gets its structure (docs/ABLAUFPLAN.md, 6.4 step 5;
/// ISO 14289-1, 7.18.1, 7.18.4; ISO 14289-2, 8.9.2.1, 8.10.1; docs/FEHLERGEDAECHTNIS.md, No. 635): a Form element at the place the containment
/// rules allow next to the content under it (<see cref="MarkedContentGeometry"/>, else beside the last element of the page), an object
/// reference to the widget, a new key of the parent tree as its /StructParent. Only new and replaced objects: what earlier signatures cover
/// stays as it is. The <see cref="StructurePlan"/> belongs to the writer that writes a whole copy; here the same building blocks are used reading
/// (<see cref="StructureView"/>, <see cref="Containment.PlaceFor"/>, <see cref="NumberTree"/>) and the changes go into the update.
/// </summary>
internal static class IncrementalStructure
{
    private static readonly PdfName StructTreeRoot = PdfName.Get("StructTreeRoot"), ParentTree = PdfName.Get("ParentTree"),
        ParentTreeNextKey = PdfName.Get("ParentTreeNextKey"), StructParents = PdfName.Get("StructParents"), StructParent = PdfName.Get("StructParent"),
        K = PdfName.Get("K"), P = PdfName.Get("P"), S = PdfName.Get("S"), Pg = PdfName.Get("Pg"), Obj = PdfName.Get("Obj"), Nums = PdfName.Get("Nums"),
        Kids = PdfName.Get("Kids"), Limits = PdfName.Get("Limits"), Ns = PdfName.Get("NS"), Namespaces = PdfName.Get("Namespaces");

    /// <summary>Gives the widget its Form element; false where the file is not tagged or its tree offers no place that can be changed.</summary>
    /// <param name="doc">The file the update is for.</param>
    /// <param name="update">The update.</param>
    /// <param name="page">The page of the widget.</param>
    /// <param name="widget">The widget (its /StructParent is set; it may already be in the update).</param>
    /// <param name="widgetRef">The reference to the widget in the update.</param>
    public static bool TagNewWidget(PdfDocument doc, IncrementalUpdate update, PdfPage page, PdfDictionary widget, PdfReference widgetRef)
    {
        if (!StructureRoles.IsTagged(doc.Catalog) || page.Id is not { } pageId ||
            doc.Catalog!.GetRaw(StructTreeRoot) is not PdfReference rootRef || doc.Resolve(rootRef.Id) is not PdfDictionary root ||
            PdfRectangle.From(widget.Get(PdfName.Get("Rect"))) is not { } rect || rect.Width <= 0 || rect.Height <= 0)
            return false;
        var view = StructureView.Of(doc, root);
        var array = root.GetDictionary(ParentTree) is { } tree && NumberTree.KeyOf(page.Dictionary.Get(StructParents)) is { } key ? NumberTree.Value(tree, key) as PdfArray : null;
        PdfObject? near = null;
        var geometry = MarkedContentGeometry.Of(doc, page);
        if (array is not null && MarkedContentGeometry.At(geometry, (rect.Left + rect.Right) / 2, (rect.Bottom + rect.Top) / 2) is { } mcid && mcid < array.Count &&
            array[mcid] is PdfReference atMcid && view.Element(atMcid) is not null)
            near = atMcid;
        var anchor = near;
        for (var i = (array?.Count ?? 0) - 1; anchor is null && i >= 0; i--)
            if (array![i] is PdfReference last && view.Element(last) is not null) anchor = last;
        var place = anchor is not null ? Containment.PlaceFor(view, anchor, "Form", beside: near is null) : null;
        if (place is not { Parent: PdfReference parentRef } at)
        {
            // no place by the rules: the end of the top Document
            if (view.Items(rootRef).FirstOrDefault(k => k is PdfReference && (view.TypeOf(k) == "Document" || view.Element(k)?.GetName(S)?.Value == "Document"))
                is not PdfReference document) return false;
            (parentRef, at) = (document, new Placement(document, view.Items(document).Count));
        }
        if (view.Element(parentRef) is not { } parent) return false;

        var newKey = NextKey(root);
        var element = new PdfDictionary();
        element.Set(PdfName.Type, PdfName.Get("StructElem"));
        element.Set(S, PdfName.Get("Form"));
        if (Pdf20Namespace(doc, root) is { } ns) element.Set(Ns, ns);
        element.Set(P, parentRef);
        element.Set(Pg, new PdfReference(pageId));
        var objr = new PdfDictionary();
        objr.Set(PdfName.Type, PdfName.Get("OBJR"));
        objr.Set(Obj, widgetRef);
        objr.Set(Pg, new PdfReference(pageId));
        element.Set(K, objr);
        var elementRef = update.Add(element);

        var parentCopy = IncrementalParts.Copy(parent);
        var kids = view.Items(parentRef).ToList();
        kids.Insert(Math.Clamp(at.Index, 0, kids.Count), elementRef);
        parentCopy.Set(K, new PdfArray(kids));
        update.Replace(parentRef.Id, parentCopy);

        var rootCopy = IncrementalParts.Copy(root);
        AddPair(doc, update, root, rootCopy, newKey, elementRef);
        rootCopy.Set(ParentTreeNextKey, PdfInteger.Of(newKey + 1));
        update.Replace(rootRef.Id, rootCopy);
        widget.Set(StructParent, PdfInteger.Of(newKey));
        return true;
    }

    /// <summary>A new key: above /ParentTreeNextKey and every key the tree has (files have the first wrong or not at all).</summary>
    private static long NextKey(PdfDictionary root)
    {
        var next = root.GetInteger(ParentTreeNextKey) ?? 0;
        if (root.GetDictionary(ParentTree) is { } tree)
            foreach (var (key, _) in NumberTree.Pairs(tree))
                next = Math.Max(next, key + 1);
        return next;
    }

    /// <summary>
    /// The pair added to the parent tree: a flat tree is copied with the pair at its end (the key is the largest); a tree in kids gets a new
    /// leaf with its /Limits as its last kid; no tree, a new one.
    /// </summary>
    private static void AddPair(PdfDocument doc, IncrementalUpdate update, PdfDictionary root, PdfDictionary rootCopy, long key, PdfReference element)
    {
        var raw = root.GetRaw(ParentTree);
        var tree = raw is PdfReference r ? doc.Resolve(r.Id) as PdfDictionary : raw as PdfDictionary;
        PdfDictionary changed;
        if (tree is null)
        {
            changed = new PdfDictionary();
            changed.Set(Nums, new PdfArray([PdfInteger.Of(key), element]));
            rootCopy.Set(ParentTree, update.Add(changed));
            return;
        }
        changed = IncrementalParts.Copy(tree);
        if (tree.GetArray(Kids) is { } kids)
        {
            var leaf = new PdfDictionary();
            leaf.Set(Limits, new PdfArray([PdfInteger.Of(key), PdfInteger.Of(key)]));
            leaf.Set(Nums, new PdfArray([PdfInteger.Of(key), element]));
            changed.Set(Kids, new PdfArray([.. Enumerable.Range(0, kids.Count).Select(i => kids[i]), update.Add(leaf)]));
        }
        else changed.Set(Nums, new PdfArray([.. tree.GetArray(Nums) is { } nums ? Enumerable.Range(0, nums.Count).Select(i => nums[i]) : [], PdfInteger.Of(key), element]));
        if (raw is PdfReference treeRef) update.Replace(treeRef.Id, changed);
        else rootCopy.Set(ParentTree, changed);
    }

    /// <summary>The namespace for PDF 2.0 among /Namespaces, as a reference, for a file in 2.0 (its new element names it); null otherwise.</summary>
    private static PdfReference? Pdf20Namespace(PdfDocument doc, PdfDictionary root)
    {
        if (PdfWriter.CompareVersions(doc.Version, "2.0") < 0 || root.GetArray(Namespaces) is not { } namespaces) return null;
        for (var i = 0; i < namespaces.Count && i < 1000; i++)
            if (namespaces[i] is PdfReference reference && doc.Resolve(reference.Id) is PdfDictionary ns && StandardStructure.SpaceOf(ns) == StructureSpace.Pdf20)
                return reference;
        return null;
    }
}
