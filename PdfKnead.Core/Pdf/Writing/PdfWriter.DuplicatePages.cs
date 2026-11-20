using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>The keys into the parent tree of the second and further copies of a page that got a structure of their own: page object number → key.</summary>
        private readonly Dictionary<int, long> _copyPageKeys = [];

        /// <summary>
        /// A page taken more than once (docs/ABLAUFPLAN.md, 6.5 d, step 14; decided on 8.11.2026: the second copy gets a copy of its
        /// structure elements, both are read out): every further copy of a page whose tree the copy keeps gets a key of its own and, for it,
        /// copies of the elements that hold content of the page — with their ancestors up to below the top Document, so that a cell stays in its
        /// row and table (ISO 32000-2, Annex L) — under a new part at the place of the copy in the reading order. A copy holds only the content
        /// of this page (MCIDs, marked-content references), no /ID (IDs are unique, 14.7.2), its /Pg is the copy. Not copied (decided here, see
        /// the plan): object references — an annotation object shared by the copies has one /StructParent, it stays with the first; a page whose
        /// content draws a form with keys of its own (/StructParents) keeps no structure (its content would name the first copy's elements) —
        /// the save says so (<see cref="CollectAccessibilityBreaks"/>). Before: both copies named the same elements, whose /Pg was the first.
        /// </summary>
        private void DuplicatePageStructure()
        {
            if ((KeepsStructure() ? StructureEdits() : _newTreePlan) is not { } plan) return;
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < _pages.Count; i++)
            {
                var item = _pages[i];
                if (seen.Add(item.Page.Dictionary)) continue;   // the first copy keeps the tree as it is
                var source = _sources[item.Source];
                var keeps = source.IsMain ? KeepsStructure() : source.StructKeyOffset is not null;
                if (!keeps || item.Page.Id is not { } pageId || NumberTree.KeyOf(item.Page.Dictionary.Get(StructParents)) is not { } key || key < 0 ||
                    DrawsFormWithKeys(item)) continue;
                if (CopyPageStructure(plan, source, i, pageId.Number, key) is { } copyKey) _copyPageKeys[_pageNumbers[i]] = copyKey;
            }
        }

        /// <summary>Whether a further copy of a page with tagged content (/StructParents) got no structure of its own.</summary>
        private bool CopiesWithoutStructure()
        {
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < _pages.Count; i++)
                if (!seen.Add(_pages[i].Page.Dictionary) && NumberTree.KeyOf(_pages[i].Page.Dictionary.Get(StructParents)) is not null &&
                    !_copyPageKeys.ContainsKey(_pageNumbers[i]))
                    return true;
            return false;
        }

        /// <summary>Whether a page's content draws a form (also within forms, 8 levels) that has keys of its own into the parent tree.</summary>
        private static bool DrawsFormWithKeys(PageItem item)
        {
            var seen = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<(PdfDictionary? Resources, int Depth)>([(item.Page.Resources, 0)]);
            while (stack.TryPop(out var next))
            {
                if (next.Depth > 8 || next.Resources?.GetDictionary(PdfName.Get("XObject")) is not { } xobjects) continue;
                foreach (var (name, _) in xobjects.Entries.Take(10_000))
                {
                    if (xobjects.Get(name) is not PdfStream form || !seen.Add(form) || form.Dictionary.GetName(PdfName.Subtype)?.Value != "Form") continue;
                    if (NumberTree.KeyOf(form.Dictionary.Get(StructParents)) is not null) return true;
                    stack.Push((form.Dictionary.GetDictionary(PdfName.Resources), next.Depth + 1));
                }
            }
            return false;
        }

        /// <summary>
        /// The structure of one further copy of a page (its index in the copy <paramref name="page"/>, its object number in the source
        /// <paramref name="sourcePage"/>, its key there <paramref name="key"/>): the copies of the elements, the part they hang in, the page's
        /// array under a key of its own. Null if the page names no element the copy writes.
        /// </summary>
        private long? CopyPageStructure(StructurePlan plan, Source source, int page, int sourcePage, long key)
        {
            if (source.Doc.Catalog?.GetDictionary(StructTreeRoot)?.GetDictionary(ParentTreeKey) is not { } tree ||
                NumberTree.Value(tree, key) is not { } value)
                return null;
            var raw = value is PdfReference arrayRef ? source.Doc.Resolve(arrayRef.Id) : value;
            if (raw is not PdfArray array) return null;

            // the elements with content of the page, and their ancestors up to below the top Document
            var holding = new List<int?>();   // per MCID: the element (object number) or none
            var copied = new Dictionary<int, PdfReference>();
            for (var mcid = 0; mcid < Math.Min(array.Count, 1 << 16); mcid++)
            {
                if (array[mcid] is not PdfReference element || source.Removed.Contains(element.Id.Number) || Current(source, element) is not { } dict ||
                    !IsElement(dict))
                {
                    holding.Add(null);
                    continue;
                }
                holding.Add(element.Id.Number);
                var current = element;
                for (var depth = 0; depth < 64 && current is not null && copied.TryAdd(current.Id.Number, current); depth++)
                    current = Current(source, current)?.GetRaw(PdfName.Get("P")) is PdfReference parent && !IsTop(source, parent) ? parent : null;
            }
            if (copied.Count == 0) return null;

            // the copies: handles first (they name each other), then their entries
            var copies = new Dictionary<int, (OutputReference Handle, PdfDictionary Dict)>();
            foreach (var number in copied.Keys)
            {
                var dict = new PdfDictionary();
                copies[number] = (plan.AddElement(dict), dict);
            }
            var pageNumber = _pageNumbers[page];
            var tops = new List<(int Number, int Order)>();
            foreach (var (number, reference) in copied)
            {
                var original = Current(source, reference)!;
                var (handle, dict) = copies[number];
                foreach (var (entry, entryValue) in original.Entries)
                    if (entry.Value is not ("K" or "P" or "Pg" or "ID")) dict.Set(entry, Rebase(entryValue, source));
                var ownPage = original.GetRaw(Pg) is PdfReference pg ? pg.Id.Number : InheritedPage(source, original);
                if (ownPage == sourcePage) dict.Set(Pg, new OutputReference(pageNumber));
                dict.Set(StructKids, new PdfArray(CopiedKids(source, original, ownPage, sourcePage, pageNumber, copies)));
                if (original.GetRaw(PdfName.Get("P")) is PdfReference parent && copies.TryGetValue(parent.Id.Number, out var parentCopy))
                    dict.Set(PdfName.Get("P"), parentCopy.Handle);
                else tops.Add((number, OrderOf(source, number)));
            }

            // the part at the place of the copy, the top copies in the order of the tree
            var type = plan.Pdf20Types ? "DocumentFragment" : "Part";
            if (plan.NewTopElement(type, page, source.PartLanguage) is not { } part) return null;
            foreach (var (number, _) in tops.OrderBy(t => t.Order))
            {
                copies[number].Dict.Set(PdfName.Get("P"), part);
                plan.AppendKid(part, copies[number].Handle);
            }
            var pairs = new PdfArray(holding.Select(n => n is { } e ? (PdfObject)copies[e].Handle : PdfNull.Instance));
            return plan.AddPageArray(pairs);
        }

        /// <summary>
        /// The /K of a copy: the copies of its kids that are copied, its MCIDs and marked-content references on the page (without /Stm), in
        /// their order; object references and content of other pages stay with the original.
        /// </summary>
        private static List<PdfObject> CopiedKids(Source source, PdfDictionary original, int? ownPage, int sourcePage, int pageNumber,
            Dictionary<int, (OutputReference Handle, PdfDictionary Dict)> copies)
        {
            var kids = new List<PdfObject>();
            var items = original.GetRaw(StructKids) switch
            {
                PdfArray list => [.. list.Take(100_000)],
                null => [],
                var one => new List<PdfObject> { one },
            };
            foreach (var kid in items)
                switch (kid)
                {
                    case PdfReference reference when copies.TryGetValue(reference.Id.Number, out var copy):
                        kids.Add(copy.Handle);
                        break;
                    case PdfInteger when ownPage == sourcePage:
                        kids.Add(kid);
                        break;
                    case PdfDictionary or PdfReference when (kid is PdfReference r ? Current(source, r) : (PdfDictionary)kid) is { } item &&
                                                            item.GetName(PdfName.Type)?.Value == "MCR" && !item.ContainsKey(PdfName.Get("Stm")) &&
                                                            (item.GetRaw(Pg) is PdfReference mcrPage ? mcrPage.Id.Number : ownPage) == sourcePage:
                        var mcr = new PdfDictionary();
                        mcr.Set(PdfName.Type, PdfName.Get("MCR"));
                        mcr.Set(Pg, new OutputReference(pageNumber));
                        if (item.Get(PdfName.Get("MCID")) is PdfInteger mcid) mcr.Set(PdfName.Get("MCID"), mcid);
                        kids.Add(mcr);
                        break;
                }
            return kids;
        }

        /// <summary>The page (object number) an element's content is on where it names none itself: that of the nearest ancestor naming one.</summary>
        private static int? InheritedPage(Source source, PdfDictionary element)
        {
            var current = element;
            for (var depth = 0; depth < 64 && current.GetRaw(PdfName.Get("P")) is PdfReference parent && Current(source, parent) is { } up; depth++)
            {
                if (up.GetRaw(Pg) is PdfReference pg) return pg.Id.Number;
                current = up;
            }
            return null;
        }

        /// <summary>Whether a parent is the top of a source's tree as the copy has it: the root, or a Document right below it.</summary>
        private bool IsTop(Source source, PdfReference parent)
        {
            if (Current(source, parent) is not { } dict || dict.GetName(PdfName.Type)?.Value == "StructTreeRoot") return true;
            if (source.Removed.Contains(parent.Id.Number)) return true;   // the Document of a merged file, unwrapped
            return dict.GetName(S)?.Value == "Document" && dict.GetRaw(PdfName.Get("P")) is PdfReference up &&
                   Current(source, up)?.GetName(PdfName.Type)?.Value == "StructTreeRoot";
        }

        /// <summary>The place of an element in the source's tree (depth-first order of the root's /K); for sorting the top copies.</summary>
        private static int OrderOf(Source source, int number)
        {
            if (source.Doc.Catalog?.GetDictionary(StructTreeRoot) is not { } root) return int.MaxValue;
            var stack = new Stack<PdfObject>([root.GetRaw(StructKids) ?? PdfNull.Instance]);
            var order = 0;
            var seen = new HashSet<int>();
            while (stack.TryPop(out var next) && order < 200_000)
                switch (next)
                {
                    case PdfArray list:
                        for (var i = Math.Min(list.Count, 100_000) - 1; i >= 0; i--) stack.Push(list[i]);
                        break;
                    case PdfReference reference when seen.Add(reference.Id.Number):
                        if (reference.Id.Number == number) return order;
                        order++;
                        if (Current(source, reference)?.GetRaw(StructKids) is { } kids) stack.Push(kids);
                        break;
                }
            return int.MaxValue;
        }
    }
}
