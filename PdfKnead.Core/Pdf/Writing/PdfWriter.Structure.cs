using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>
        /// No tagged content of the source is left in the copy (<see cref="PruneStructure"/>): the copy is no longer tagged — no structure tree,
        /// no /MarkInfo, no /StructParents or /StructParent, the same way as „Bereinigen: Struktur“ (<see cref="Cleanup"/>); a PDF/UA
        /// identification goes with its reason (docs/FEHLERGEDAECHTNIS.md, No. 626).
        /// </summary>
        private bool _structureGone;

        /// <summary>
        /// The cleanup items in force for the keys of the copied objects: those chosen and, once no tagged content is left
        /// (<see cref="_structureGone"/>), the structure as well.
        /// </summary>
        private CleanupItems Cleanup => _structureGone ? _options.Cleanup | CleanupItems.Structure : _options.Cleanup;

        /// <summary>The copy keeps a structure tree of the source: the source is tagged, and its structure is neither cleaned up nor all gone.</summary>
        private bool TaggedCopy() => Tagged() && !Cleanup.HasFlag(CleanupItems.Structure);

        /// <summary>
        /// Structure tree when not all pages are written (delete, extract, split): elements whose content lies only on missing
        /// pages are dropped, together with /Alt, /ActualText and /E, and via /OBJR also their annotations and field values; object
        /// references to objects that are not written (annotations removed by a redaction) are dropped as well;
        /// otherwise the whole tree would be copied unchanged. Kept elements get their shortened /K via
        /// <see cref="Source.Overrides"/>; dropped ones are listed in <see cref="Source.Removed"/>. See docs/FEHLERGEDAECHTNIS.md, No. 59.
        /// The trees that name elements lose them as well (<see cref="PruneTree"/>): the parent tree the pairs of the pages and annotations not
        /// written and those naming only dropped elements, the /IDTree the IDs of dropped elements (ISO 32000-2, 14.7.2 and 14.7.5.4;
        /// No. 626). With nothing of the tree left, the copy is no longer tagged (<see cref="_structureGone"/>).
        /// </summary>
        private void PruneStructure()
        {
            var main = _sources[_main];
            if (_main.Catalog?.GetRaw(StructTreeRoot) is not PdfReference rootRef || _main.Resolve(rootRef.Id) is not PdfDictionary root) return;
            var written = main.PageOut.Keys.ToHashSet();
            // a page replaced by its image („zusätzlich als Bild“): its elements go as with a deleted page — the image page has none of its
            // marked content; before they stayed and named the image page with MCIDs it does not have (docs/ABLAUFPLAN.md, 6.6 g)
            var replaced = ReplacedMainPages();
            written.ExceptWith(replaced);
            var pruner = new TreePruner(main, written) { Replaced = replaced };
            PdfDictionary? rootCopy = null;
            PdfDictionary RootCopy()
            {
                rootCopy ??= Clone(root);
                main.Overrides[rootRef.Id.Number] = rootCopy;
                return rootCopy;
            }
            var rootKids = root.GetRaw(StructKids);
            var prunedKids = pruner.Prune(rootKids, null, 0, rootRef);
            _imagePagePlaces = pruner.Places(main);
            if (rootKids is not null && !ReferenceEquals(prunedKids, rootKids))
            {
                // nothing of the tree left: the copy has no tagged content — it no longer claims to be tagged (before: a root without /K and
                // /Marked true stayed; docs/FEHLERGEDAECHTNIS.md, No. 523 and 626)
                // — unless pages replaced by their images take the content's place: their Figures go into a Document made anew
                // (ImagePageFigures, 6.6 g; before, a file whose pages were all saved as images lost its tree)
                if (prunedKids is null && (replaced.Count == 0 || _pdfA is not null))
                {
                    _structureGone = true;
                    return;
                }
                RootCopy().Set(StructKids, prunedKids ?? new PdfArray());
            }

            // the parent tree: the pair of a page or annotation not written goes as a whole (one key per content stream or annotation,
            // ISO 32000-2, 14.7.5.4), and so does a pair naming only objects that are not written — written as null it named nothing for its
            // number (docs/FEHLERGEDAECHTNIS.md, No. 363 and 626)
            var unusedKeys = KeysOfPagesNotWritten(_main, written);
            var budget = pruner.Budget;
            bool DropsPair(PdfObject key, PdfObject? value) =>
                key is PdfInteger number && unusedKeys.Contains(number.Value) || value is not null && pruner.NamesOnlyRemoved(value);
            // a tree all of whose pairs go is written empty — before, „nothing left“ was taken for „nothing changed“ and the tree stayed whole,
            // its pairs naming null (pages all replaced by their images; docs/FEHLERGEDAECHTNIS.md, No. 693)
            PdfDictionary EmptyNumberTree()
            {
                var empty = new PdfDictionary();
                empty.Set(NumsKey, new PdfArray());
                return empty;
            }
            switch (root.GetRaw(ParentTreeKey))
            {
                case PdfReference treeRef when _main.Resolve(treeRef.Id) is PdfDictionary tree:
                    var (prunedTree, noPairs) = PruneTree(tree, NumsKey, DropsPair, ref budget, 0);
                    if (noPairs) main.Overrides[treeRef.Id.Number] = EmptyNumberTree();
                    else if (prunedTree is not null) main.Overrides[treeRef.Id.Number] = prunedTree;
                    break;
                case PdfDictionary direct:
                    var (prunedDirect, noDirectPairs) = PruneTree(direct, NumsKey, DropsPair, ref budget, 0);
                    if (noDirectPairs) RootCopy().Set(ParentTreeKey, EmptyNumberTree());
                    else if (prunedDirect is not null) RootCopy().Set(ParentTreeKey, prunedDirect);
                    break;
            }
            // the /IDTree: the ID of an element not written goes — before it named null (ISO 32000-2, 14.7.2; No. 626); a tree left empty goes
            // from the root (the entry is only needed if an element has an ID)
            bool DropsId(PdfObject key, PdfObject? value) => value is PdfReference element && main.Removed.Contains(element.Id.Number);
            switch (root.GetRaw(IdTreeKey))
            {
                case PdfReference treeRef when _main.Resolve(treeRef.Id) is PdfDictionary tree:
                    var (prunedIds, noIds) = PruneTree(tree, NamesKey, DropsId, ref budget, 0);
                    if (noIds) RootCopy().Remove(IdTreeKey);
                    else if (prunedIds is not null) main.Overrides[treeRef.Id.Number] = prunedIds;
                    break;
                case PdfDictionary direct:
                    var (prunedDirectIds, noDirectIds) = PruneTree(direct, NamesKey, DropsId, ref budget, 0);
                    if (noDirectIds) RootCopy().Remove(IdTreeKey);
                    else if (prunedDirectIds is not null) RootCopy().Set(IdTreeKey, prunedDirectIds);
                    break;
            }
            // what the elements left say of those that went, and the order of their headings (6.6 h, No. 655)
            var kept = new Accessibility.StructureView(rootCopy ?? root, Kept);
            PruneReferences(main, kept);
            _headingOrderBroken = !Accessibility.StructureReferences.OrderBroken(Accessibility.StructureReferences.HeadingLevels(Accessibility.StructureView.Of(_main, root))) &&
                                  Accessibility.StructureReferences.OrderBroken(Accessibility.StructureReferences.HeadingLevels(kept));
        }

        /// <summary>
        /// The numbered headings of the tree left after pruning begin with another level than H1 or skip one, where those of the source did not
        /// (6.6 h): the save names it (<see cref="CollectAccessibilityBreaks"/>). The headings are not re-levelled — that would change the
        /// outline the author made (docs/ABLAUFPLAN.md, 6.6 h).
        /// </summary>
        private bool _headingOrderBroken;

        private static readonly PdfName RefKey = PdfName.Get("Ref"), OwnerKey = PdfName.Get("O");

        /// <summary>An object of the main source as the copy keeps it: its replacement, nothing for one not written (<see cref="Source.Removed"/>).</summary>
        private PdfObject? Kept(PdfObject? value)
        {
            if (value is not PdfReference r) return value;
            var main = _sources[_main];
            if (main.Removed.Contains(r.Id.Number)) return null;
            return main.Overrides.TryGetValue(r.Id.Number, out var replaced) ? replaced : _main.Resolve(r.Id);
        }

        /// <summary>
        /// References between elements after pruning (docs/ABLAUFPLAN.md, 6.6 h; docs/FEHLERGEDAECHTNIS.md, No. 655): /Headers of table
        /// attributes lose the IDs of elements that went (ISO 14289-1, 7.5; ISO 14289-2, 8.2.5.26), /Ref loses the elements that went (ISO
        /// 32000-2, 14.7.2) — an entry left empty goes, never null (an attribute object left without /Headers keeps its other entries). Before a cell named header cells that were no longer there, and /Ref named
        /// nothing. A changed attribute object is written into its element's copy (another element may share it).
        /// </summary>
        private void PruneReferences(Source main, Accessibility.StructureView kept)
        {
            if (main.Removed.Count == 0) return;
            var removedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var number in main.Removed)
                if (_main.GetObject(number) is PdfDictionary gone && gone.Get(IdKey) is PdfString id) removedIds.Add(Convert.ToHexString(id.Bytes));
            foreach (var (handle, element) in Accessibility.StructureReferences.Elements(kept).ToList())
            {
                if (handle is not PdfReference reference) continue;   // a direct element: left as it is
                PdfDictionary? copy = null;
                PdfDictionary Copy()
                {
                    if (copy is not null) return copy;
                    copy = main.Overrides.TryGetValue(reference.Id.Number, out var own) && own is PdfDictionary replaced &&
                           !ReferenceEquals(replaced, _main.Resolve(reference.Id)) ? replaced : Clone(element);
                    main.Overrides[reference.Id.Number] = copy;
                    return copy;
                }
                if (removedIds.Count > 0 && PrunedAttributes(element, removedIds, kept) is { } attributes)
                    Copy().Set(AttributesKey, attributes);
                switch (element.GetRaw(RefKey))
                {
                    case PdfReference single when main.Removed.Contains(single.Id.Number):
                        Copy().Remove(RefKey);
                        break;
                    case var refs when kept.Resolve(refs) is PdfArray list && Enumerable.Range(0, list.Count).Any(i => list[i] is PdfReference r && main.Removed.Contains(r.Id.Number)):
                        var left = new PdfArray();
                        for (var i = 0; i < list.Count; i++)
                            if (!(list[i] is PdfReference r && main.Removed.Contains(r.Id.Number))) left.Add(list[i]);
                        if (left.Count == 0) Copy().Remove(RefKey);
                        else Copy().Set(RefKey, left);
                        break;
                }
            }
        }

        /// <summary>
        /// An element's /A without the IDs of <paramref name="removedIds"/> in the /Headers of its table attributes: the new /A (an attribute
        /// object left without /Headers keeps its other entries); null if nothing changes.
        /// </summary>
        private static PdfObject? PrunedAttributes(PdfDictionary element, HashSet<string> removedIds, Accessibility.StructureView kept)
        {
            static bool Gone(PdfObject? id, HashSet<string> ids) => id is PdfString s && ids.Contains(Convert.ToHexString(s.Bytes));
            PdfObject? One(PdfObject? stored)
            {
                if (kept.Resolve(stored) is not PdfDictionary attributes || attributes.GetName(OwnerKey)?.Value != "Table" ||
                    kept.Resolve(attributes.GetRaw(HeadersKey)) is not PdfArray headers || !Enumerable.Range(0, headers.Count).Any(i => Gone(kept.Resolve(headers[i]), removedIds)))
                    return null;
                var changed = Clone(attributes);
                var left = new PdfArray();
                for (var i = 0; i < headers.Count; i++)
                    if (!Gone(kept.Resolve(headers[i]), removedIds)) left.Add(headers[i]);
                if (left.Count == 0) changed.Remove(HeadersKey);
                else changed.Set(HeadersKey, left);
                return changed;
            }
            switch (element.GetRaw(AttributesKey))
            {
                case var stored when kept.Resolve(stored) is PdfArray list:
                    var result = new PdfArray();
                    var any = false;
                    for (var i = 0; i < list.Count; i++)
                        if (i < 256 && One(list[i]) is { } changed)
                        {
                            result.Add(changed);
                            any = true;
                        }
                        else result.Add(list[i]);
                    return any ? result : null;
                case var single:
                    return One(single);
            }
        }

        /// <summary>
        /// The marked content of the changed pages of a tagged copy that painted before and paints nothing now (docs/ABLAUFPLAN.md, 6.6 b): the
        /// content of the page as read against the content written — of each page whose content is another (redacted, hidden content removed,
        /// text or images edited) and that has a key into the parent tree. The pruning drops elements left with nothing else
        /// (<see cref="TreePruner"/>); before, a paragraph whose text lay wholly under a bar stayed as an empty element, and screen readers
        /// announced empty headings, paragraphs and list items (ISO 32000-2, 14.8.2.4).
        /// </summary>
        private void EmptiedMarkedContent()
        {
            if (!TaggedCopy()) return;
            foreach (var item in _pages)
            {
                if (item.Page.Id is not { } id || NumberTree.KeyOf(item.Page.Dictionary.Get(StructParents)) is null) continue;
                var source = _sources[item.Source];
                if (source.EmptiedMcids.ContainsKey(id.Number) || item.Source.Pages.FirstOrDefault(p => p.Id == id) is not { } original ||
                    ReferenceEquals(original.Dictionary.GetRaw(PdfName.Contents), item.Page.Dictionary.GetRaw(PdfName.Contents)))
                    continue;
                try
                {
                    using var before = Content.PageInterpreter.PageContent(item.Source, original.Dictionary);
                    using var after = Content.PageInterpreter.PageContent(item.Source, item.Page.Dictionary);
                    var painted = Accessibility.MarkedContentIds.Painting(before.Span.ToArray(), original.Resources?.GetDictionary(PdfName.Get("Properties")));
                    painted.ExceptWith(Accessibility.MarkedContentIds.Painting(after.Span.ToArray(), item.Page.Resources?.GetDictionary(PdfName.Get("Properties"))));
                    if (painted.Count > 0) source.EmptiedMcids[id.Number] = painted;
                }
                catch (PdfException)
                {
                    // unreadable: nothing is pruned for it
                }
            }
        }

        /// <summary>The content streams (in memory) of changed pages whose emptied MCIDs lost their element (<see cref="PlanEmptiedArtifacts"/>).</summary>
        private readonly Dictionary<PdfStream, (HashSet<int> Mcids, PdfDictionary? Properties)> _emptiedArtifacts = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The marked content left empty whose element went (<see cref="EmptiedMarkedContent"/>, pruned by <see cref="TreePruner"/>): in the
        /// page's content it becomes artifact content (<c>/Artifact BMC</c>) — else an MCID would stay in the content with no element in the
        /// parent tree, content outside the structure (ISO 14289-1, 7.1). Only content in memory (a changed page's), each stream as it is
        /// written (<see cref="DrainAll"/>), only those operators replaced (<see cref="Accessibility.MarkedContentIds.AsArtifacts"/>: the rest
        /// byte for byte — a rewrite of all of it normalised the bars' artifacts too).
        /// </summary>
        private void PlanEmptiedArtifacts()
        {
            foreach (var item in _pages)
            {
                var source = _sources[item.Source];
                if (item.Page.Id is not { } id || !source.EmptiedMcids.TryGetValue(id.Number, out var emptied) ||
                    NumberTree.KeyOf(item.Page.Dictionary.Get(StructParents)) is not { } key ||
                    item.Source.Catalog?.GetDictionary(StructTreeRoot)?.GetDictionary(ParentTreeKey) is not { } tree ||
                    NumberTree.Value(tree, key) is not { } value ||
                    (value is PdfReference arrayRef ? item.Source.Resolve(arrayRef.Id) : value) is not PdfArray array)
                    continue;
                var gone = new HashSet<int>();
                foreach (var mcid in emptied)
                    if (mcid < array.Count && array[mcid] is PdfReference element && source.Removed.Contains(element.Id.Number)) gone.Add(mcid);
                if (gone.Count > 0) ArtifactBrackets(item, gone);
            }
        }

        /// <summary>
        /// The brackets of the marked content <paramref name="mcids"/> in the content of a changed page (its streams in memory) become artifact
        /// content when written (<see cref="Accessibility.MarkedContentIds.AsArtifacts"/>); together with those planned already.
        /// </summary>
        private void ArtifactBrackets(Editing.PageItem item, HashSet<int> mcids)
        {
            var properties = item.Page.Resources?.GetDictionary(PdfName.Get("Properties"));
            var contents = item.Page.Dictionary.Get(PdfName.Contents) switch
            {
                PdfStream single => [single],
                PdfArray parts => Enumerable.Range(0, Math.Min(parts.Count, 10_000)).Select(parts.Get).OfType<PdfStream>().ToList(),
                _ => new List<PdfStream>(),
            };
            foreach (var stream in contents)
                if (stream.Id is null && stream.RawData is not null)
                    _emptiedArtifacts[stream] = _emptiedArtifacts.TryGetValue(stream, out var planned) ? ([.. planned.Mcids, .. mcids], properties) : (mcids, properties);
        }

        /// <summary>
        /// The pruning of the structure tree of one source to the pages written (shared by the main file and the files merged into it;
        /// docs/ABLAUFPLAN.md, 6.2 step 7): elements whose content lies only on pages not written go (into <see cref="Source.Removed"/>),
        /// together with object references to objects not written; kept elements with a shortened /K become copies in
        /// <see cref="Source.Overrides"/>. At most 200 000 steps, 64 levels (crafted trees).
        /// </summary>
        private sealed class TreePruner(Source source, HashSet<int> written)
        {
            /// <summary>The steps left.</summary>
            public int Budget = 200_000;

            /// <summary>Pages (object numbers) replaced by their image, whose first place in the tree is wanted (<see cref="Places"/>).</summary>
            public IReadOnlySet<int> Replaced { get; init; } = new HashSet<int>();

            // per replaced page, in the order of the tree: the parent and the index in its pruned /K where a kid of that page went
            private readonly Dictionary<int, List<(PdfReference Parent, int Index)>> _places = [];

            /// <summary>
            /// Per replaced page the first place in the tree where something of it went, whose parent stays (a parent that went itself left its
            /// own place one level up).
            /// </summary>
            public Dictionary<int, (PdfReference Parent, int Index)> Places(Source kept) =>
                _places.Where(p => p.Value.Any(e => !kept.Removed.Contains(e.Parent.Id.Number)))
                    .ToDictionary(p => p.Key, p => p.Value.First(e => !kept.Removed.Contains(e.Parent.Id.Number)));

            /// <summary>
            /// The page a kid lies on: its own /Pg, else the first /Pg below it (a row or list without one of its own), else the page of its
            /// parent.
            /// </summary>
            private int? PageOf(PdfObject? kid, int? page, int depth = 0)
            {
                if ((kid is PdfReference r ? source.Doc.Resolve(r.Id) : kid) is not PdfDictionary d) return page;
                if (d.GetRaw(Pg) is PdfReference own) return own.Id.Number;
                if (depth >= 8) return page;
                switch (d.GetRaw(StructKids))
                {
                    case PdfArray kids:
                        for (var i = 0; i < kids.Count && i < 64; i++)
                            if (PageOf(kids[i], null, depth + 1) is { } below) return below;
                        break;
                    case { } one when PageOf(one, null, depth + 1) is { } below:
                        return below;
                }
                return page;
            }

            /// <summary>Shortened /K, or null (nothing left in it); if unchanged, the same object is returned.</summary>
            /// <param name="parent">The element (or the root) the /K belongs to, where it is an object of its own; null: not.</param>
            public PdfObject? Prune(PdfObject? k, int? page, int depth, PdfReference? parent = null)
            {
                if (k is null || depth > 64 || --Budget < 0) return k;
                switch (k)
                {
                    case PdfArray array:
                    {
                        var kept = new PdfArray();
                        var changed = false;
                        for (var i = 0; i < array.Count; i++)
                        {
                            var kid = Prune(array[i], page, depth + 1);
                            if (kid is null)
                            {
                                changed = true;
                                if (parent is not null && Replaced.Count > 0 && PageOf(array[i], page) is { } on && Replaced.Contains(on))
                                {
                                    if (!_places.TryGetValue(on, out var list)) _places[on] = list = [];
                                    list.Add((parent, kept.Count));
                                }
                                continue;
                            }
                            if (!ReferenceEquals(kid, array[i])) changed = true;
                            kept.Add(kid);
                        }
                        return kept.Count == 0 ? null : changed ? kept : array;
                    }
                    case PdfInteger:   // MCID: content of the element's page
                        return page is { } p && !written.Contains(p) ? null : k;
                    case PdfReference reference:
                    {
                        if (source.Doc.Resolve(reference.Id) is not PdfDictionary element) return k;
                        var result = PruneElement(element, page, depth, reference);
                        if (result is null)
                        {
                            source.Removed.Add(reference.Id.Number);
                            return null;
                        }
                        if (!ReferenceEquals(result, element)) source.Overrides[reference.Id.Number] = result;
                        return k;
                    }
                    case PdfDictionary element:
                        return PruneElement(element, page, depth);
                    default:
                        return k;
                }
            }

            /// <summary>
            /// Element, marked-content reference (MCR) or object reference (OBJR): null if none of it lies on a written page — or if all that is
            /// left of an element is marked content left empty (<see cref="Source.EmptiedMcids"/>; 6.6 b), except a table cell or list item
            /// (TD, TH, LI: the table or list would become irregular), which stays with its marked content — or empty, where what it held went
            /// (No. 681). An element whose content lies only on pages not written goes as a whole, with the elements in it that hold nothing
            /// (No. 682); a table cell reached that way (its row stays) stays, empty.
            /// </summary>
            private PdfDictionary? PruneElement(PdfDictionary element, int? page, int depth, PdfReference? self = null)
            {
                if (element.GetRaw(Pg) is PdfReference own) page = own.Id.Number;
                var type = element.GetName(PdfName.Type)?.Value;
                // an object reference to something that is not written (an annotation a bar touched goes as a whole) goes with it
                if (type == "OBJR" && element.GetRaw(ObjKey) is PdfReference target && source.Removed.Contains(target.Id.Number)) return null;
                if (type is "MCR" or "OBJR") return page is { } p && !written.Contains(p) ? null : element;
                if (element.GetRaw(StructKids) is not { } kids) return page is { } q && !written.Contains(q) ? null : element;
                var cell = StandardType(element) is "TD" or "TH";
                // all its content on pages not written: the element goes as a whole — before, elements in it that hold nothing (empty cells
                // of a table on a removed page) kept it, and its rows were left with only those, irregular (ISO 14289-1, 7.2; -2, 8.2.5.26;
                // docs/FEHLERGEDAECHTNIS.md, No. 682)
                if (!cell && Lies(element, page, 0) is { Any: true, Written: false })
                {
                    RemoveAll(kids, depth + 1);   // all below goes with it (the IDs, /Headers and /Ref of what is written are pruned by it)
                    return null;
                }
                var pruned = Prune(kids, page, depth + 1, self);
                if (pruned is null)
                {
                    // a table cell or list item whose content went (a bar or an edit emptied what was in it) stays, empty, while its page is
                    // written: without it the row or list would be irregular (ISO 14289-1, 7.2; -2, 8.2.5.26; docs/FEHLERGEDAECHTNIS.md,
                    // No. 681) — before, only a cell holding the emptied marked content itself stayed, not one holding it in a paragraph.
                    // A table cell stays as well where its content lay on a page not written while its row stays (a row across two pages,
                    // No. 682): a list may grow shorter, a row may not
                    var emptied = source.EmptiedMcids.Count > 0 && PageOf(element, page) is { } on && written.Contains(on) && KeptWhenEmpty(element);
                    if (!emptied && !cell) return null;
                    var empty = Clone(element);
                    empty.Remove(StructKids);
                    if (empty.GetRaw(Pg) is PdfReference gone && !written.Contains(gone.Id.Number)) empty.Remove(Pg);
                    return empty;
                }
                // nothing left but marked content that paints nothing any more: the element goes (an element with visible content keeps all its
                // marked content — the parent tree still names it for each)
                if (source.EmptiedMcids.Count > 0 && OnlyEmptied(pruned, page) && !KeptWhenEmpty(element)) return null;
                if (ReferenceEquals(pruned, kids)) return element;
                var copy = Clone(element);
                copy.Set(StructKids, pruned);
                return copy;
            }

            /// <summary>Whether /K holds only marked content left empty (MCIDs, MCRs without /Stm) — no element, no object reference.</summary>
            private bool OnlyEmptied(PdfObject kids, int? page)
            {
                bool Emptied(int? on, long mcid) => on is { } p && source.EmptiedMcids.TryGetValue(p, out var set) && set.Contains((int)mcid);
                bool One(PdfObject? kid) => kid switch
                {
                    PdfInteger mcid => Emptied(page, mcid.Value),
                    PdfReference r => source.Doc.Resolve(r.Id) is PdfDictionary d && d.GetName(PdfName.Type)?.Value == "MCR" && One(d),
                    PdfDictionary mcr when mcr.GetName(PdfName.Type)?.Value == "MCR" && !mcr.ContainsKey(PdfName.Get("Stm")) && mcr.Get(PdfName.Get("MCID")) is PdfInteger id =>
                        Emptied(mcr.GetRaw(Pg) is PdfReference p ? p.Id.Number : page, id.Value),
                    _ => false,
                };
                return kids is PdfArray list ? list.Count > 0 && list.Take(100_000).All(One) : One(kids);
            }

            /// <summary>A table cell or list item (by its standard type through the role map): stays when left empty.</summary>
            private bool KeptWhenEmpty(PdfDictionary element) => StandardType(element) is "TD" or "TH" or "LI";

            /// <summary>Every object of its own below a /K into <see cref="Source.Removed"/> (an element that went as a whole).</summary>
            private void RemoveAll(PdfObject? k, int depth)
            {
                if (depth > 64 || --Budget < 0) return;
                switch (k)
                {
                    case PdfArray array:
                        for (var i = 0; i < array.Count; i++) RemoveAll(array[i], depth + 1);
                        break;
                    case PdfReference reference:
                        if (!source.Removed.Add(reference.Id.Number)) break;   // looked at before (a damaged tree naming itself)
                        if (source.Doc.Resolve(reference.Id) is PdfDictionary below) RemoveAll(below.GetRaw(StructKids), depth + 1);
                        break;
                    case PdfDictionary element:
                        RemoveAll(element.GetRaw(StructKids), depth + 1);
                        break;
                }
            }

            /// <summary>The standard type of an element through the role map, or null.</summary>
            private string? StandardType(PdfDictionary element) =>
                Accessibility.StandardStructure.Resolve(element, source.Doc.Catalog?.GetDictionary(StructTreeRoot))?.Name;

            // per element looked at by Lies: the page it was looked at with and the answer (an element of a damaged file in two places may
            // inherit two pages: then it is looked at again)
            private readonly Dictionary<PdfDictionary, (int? Page, bool Any, bool Written)> _lies = new(ReferenceEqualityComparer.Instance);

            /// <summary>
            /// Whether a kid of /K holds content — marked content, object references, an element with a /Pg and nothing in it — and whether any
            /// of it lies on a written page or on no page known. Elements without /K and without /Pg hold nothing. Deeper than 64 levels: as
            /// if written (the element stays, as before).
            /// </summary>
            private (bool Any, bool Written) Lies(PdfObject? kid, int? page, int depth)
            {
                if (depth > 64) return (true, true);
                switch (kid)
                {
                    case PdfInteger:
                        return (true, page is not { } p || written.Contains(p));
                    case PdfReference r:
                        return Lies(source.Doc.Resolve(r.Id), page, depth);
                    case PdfArray array:
                    {
                        var any = false;
                        for (var i = 0; i < array.Count; i++)
                        {
                            var (one, onWritten) = Lies(array[i], page, depth + 1);
                            if (onWritten) return (true, true);
                            any |= one;
                        }
                        return (any, false);
                    }
                    case PdfDictionary d:
                    {
                        if (_lies.TryGetValue(d, out var known) && known.Page == page) return (known.Any, known.Written);
                        var own = d.GetRaw(Pg) as PdfReference;
                        var on = own?.Id.Number ?? page;
                        var type = d.GetName(PdfName.Type)?.Value;
                        var result = type is "MCR" or "OBJR" || d.GetRaw(StructKids) is not { } kids
                            ? (Any: type is "MCR" or "OBJR" || own is not null, Written: on is not { } q || written.Contains(q))
                            : Lies(kids, on, depth + 1);
                        if (!result.Any) result = (false, false);
                        _lies[d] = (page, result.Any, result.Written);
                        return result;
                    }
                    default:
                        return (false, false);
                }
            }

            /// <summary>
            /// A value of the parent tree naming only objects that are not written: one of them, or an array of the elements of a page's marked
            /// contents all of which are dropped (null entries count as dropped: they name nothing).
            /// </summary>
            public bool NamesOnlyRemoved(PdfObject value)
            {
                if (value is PdfReference reference)
                {
                    if (source.Removed.Contains(reference.Id.Number)) return true;
                    if (source.Doc.Resolve(reference.Id) is not PdfArray indirect) return false;
                    value = indirect;
                }
                if (value is not PdfArray { Count: > 0 } array) return false;
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is not PdfNull && !(array[i] is PdfReference entry && source.Removed.Contains(entry.Id.Number)))
                        return false;
                return true;
            }
        }

        /// <summary>
        /// The keys of the parent tree only pages of a file that are not written use (their /StructParents and the /StructParent of their
        /// annotations); a key a written page uses as well (a damaged file giving two pages the same) stays.
        /// </summary>
        private static HashSet<long> KeysOfPagesNotWritten(Document.PdfDocument doc, HashSet<int> written)
        {
            var notWritten = new HashSet<long>();
            var stillUsed = new HashSet<long>();
            foreach (var page in doc.Pages)
            {
                var keys = page.Id is { } id && written.Contains(id.Number) ? stillUsed : notWritten;
                if (NumberTree.KeyOf(page.Dictionary.Get(StructParents)) is { } own) keys.Add(own);
                if (page.Dictionary.Get(Annots) is not PdfArray annots) continue;
                for (var i = 0; i < annots.Count && i < 10_000; i++)
                    if (annots.Get(i) is PdfDictionary annot && NumberTree.KeyOf(annot.Get(StructParentKey)) is { } key) keys.Add(key);
            }
            notWritten.ExceptWith(stillUsed);
            return notWritten;
        }

        /// <summary>
        /// A node of a number or name tree (ISO 32000-2, 7.9.6 and 7.9.7; <paramref name="entriesKey"/> /Nums or /Names) without the pairs
        /// <paramref name="drops"/> names (key, value as stored): leaves lose them, kids left empty go from /Kids, and /Limits are drawn in to
        /// the keys left — a bound naming a key that is gone no longer told the least and greatest key. Indirect kids changed get their own
        /// override. <c>Pruned</c> is null if nothing changed; <c>Empty</c> when the node changed and nothing is left in it.
        /// </summary>
        private (PdfDictionary? Pruned, bool Empty) PruneTree(PdfDictionary node, PdfName entriesKey, Func<PdfObject, PdfObject?, bool> drops,
            ref int budget, int depth)
        {
            if (depth > 32 || --budget < 0) return (null, false);
            var main = _sources[_main];
            PdfArray? keptKids = null;
            PdfDictionary? firstKid = null, lastKid = null;   // the kids left as written (pruned or their own), for the bounds
            var kidsChanged = false;
            if (node.GetArray(KidsKey) is { } kids)
            {
                keptKids = new PdfArray();
                for (var i = 0; i < kids.Count; i++)
                {
                    var stored = kids[i];
                    var kid = stored is PdfReference kidRef ? _main.Resolve(kidRef.Id) as PdfDictionary : stored as PdfDictionary;
                    if (kid is null)
                    {
                        keptKids.Add(stored);
                        firstKid = lastKid = null;   // a kid that cannot be read: its bounds are unknown
                        continue;
                    }
                    var (prunedKid, emptyKid) = PruneTree(kid, entriesKey, drops, ref budget, depth + 1);
                    if (emptyKid)
                    {
                        kidsChanged = true;
                        continue;
                    }
                    if (prunedKid is not null)
                    {
                        kidsChanged = true;   // its bounds may have moved
                        if (stored is PdfReference changedRef) main.Overrides[changedRef.Id.Number] = prunedKid;
                        else stored = prunedKid;
                    }
                    if (keptKids.Count == 0) firstKid = prunedKid ?? kid;
                    lastKid = prunedKid ?? kid;
                    keptKids.Add(stored);
                }
            }
            PdfArray? keptEntries = null;
            var entriesChanged = false;
            if (node.GetArray(entriesKey) is { } entries)
            {
                keptEntries = new PdfArray();
                for (var i = 0; i + 1 < entries.Count; i += 2)
                {
                    if (entries[i] is { } key && drops(key, entries[i + 1]))
                    {
                        entriesChanged = true;
                        continue;
                    }
                    keptEntries.Add(entries[i]);
                    keptEntries.Add(entries[i + 1]);
                }
            }
            if (!kidsChanged && !entriesChanged) return (null, false);
            if ((keptKids?.Count ?? 0) == 0 && (keptEntries?.Count ?? 0) == 0) return (null, true);
            var copy = Clone(node);
            if (keptKids is not null) copy.Set(KidsKey, keptKids);
            if (keptEntries is not null) copy.Set(entriesKey, keptEntries);
            // the root has no /Limits (ISO 32000-2, Table 36 and 37): only a node that had them gets them drawn in
            if (node.ContainsKey(LimitsKey) && Bounds(firstKid, lastKid, keptEntries) is { } bounds) copy.Set(LimitsKey, bounds);
            return (copy, false);
        }

        /// <summary>
        /// The /Limits of a tree node from what is left in it: the first and last key of its entries, or the lower bound of its first kid and the
        /// upper bound of its last (as written); null if there is none to take.
        /// </summary>
        private static PdfArray? Bounds(PdfDictionary? firstKid, PdfDictionary? lastKid, PdfArray? entries)
        {
            if (entries is { Count: >= 2 }) return new PdfArray([entries[0], entries[entries.Count - 2]]);
            if (firstKid?.GetArray(LimitsKey) is not { Count: 2 } low || lastKid?.GetArray(LimitsKey) is not { Count: 2 } high) return null;
            return new PdfArray([low[0], high[1]]);
        }

        private static readonly PdfName StructTreeRoot = PdfName.Get("StructTreeRoot");
        private static readonly PdfName ObjKey = PdfName.Get("Obj");
        private static readonly PdfName ParentTreeKey = PdfName.Get("ParentTree");
        private static readonly PdfName IdTreeKey = PdfName.Get("IDTree");
        private static readonly PdfName NumsKey = PdfName.Get("Nums");
        private static readonly PdfName NamesKey = PdfName.Get("Names");
        private static readonly PdfName LimitsKey = PdfName.Get("Limits");
    }
}
