using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        // ------------------------------------------------------------ Document parts after the pages changed (ISO 32000-2, 14.12; No. 790)

        private static readonly PdfName DPartRootNodeKey = PdfName.Get("DPartRootNode"), DPartsKey = PdfName.Get("DParts"),
            StartKey = PdfName.Get("Start"), EndKey = PdfName.Get("End"), NodeNameListKey = PdfName.Get("NodeNameList"),
            RecordLevelKey = PdfName.Get("RecordLevel");

        /// <summary>The hierarchy of document parts built anew for the pages written: the new /DPartRoot and the leaf of every page written.</summary>
        private sealed class PartsPlan
        {
            public int Root;
            public Dictionary<int, int> LeafOfPage { get; } = [];
        }

        private PartsPlan? _parts;

        /// <summary>A node of the hierarchy built anew: a copy of a node as read (null for a leaf of pages that had none), its children, its pages.</summary>
        private sealed class PartNode(PdfDictionary? original)
        {
            public PdfDictionary? Original { get; } = original;
            public List<PartNode> Children { get; } = [];
            public List<int> Pages { get; } = [];   // positions in the pages written (leaves)
            public int Number;
        }

        /// <summary>
        /// The document parts for pages deleted, reordered, taken twice or added (14.12.2, 14.12.3): every page written lies in the range of one
        /// leaf, the leaves in depth-first order follow the pages, a range runs over consecutive pages. Built from the hierarchy as read: the
        /// pages written fall into runs of the same leaf; each run becomes a leaf under the ancestors of its leaf as read, an ancestor open from
        /// the run before is shared, one closed before is copied (same entries, same metadata) — so a part split by reordering keeps its
        /// metadata in both places. Pages without a part (from another file, or a range the file left out) get a leaf of their own under the
        /// root, without metadata. Leaves and nodes left without pages go. Design decision (12.11.2026): rebuild rather than remove; the
        /// parts of other files merged in come with 14.12 in M11. Nothing is planned if the pages are as they were (the parts stay as read) or
        /// if the hierarchy as read cannot be followed (a cycle, a range that does not name the file's pages, ranges that overlap, more than 32
        /// levels or 100 000 nodes) — then the parts go, as said after saving (docs/FEHLERGEDAECHTNIS.md, No. 790).
        /// </summary>
        private void PlanDocumentParts(int[] pageNumbers)
        {
            if (_main.Catalog?.Get(DPartRootKey) is not PdfDictionary partRoot || StructureUnchanged()) return;
            if (partRoot.Get(DPartRootNodeKey) is not PdfDictionary top) return;
            // the leaves as read, in depth-first order, each with its ancestors (the top first)
            var leaves = new List<(PdfDictionary Leaf, PdfDictionary[] Ancestors)>();
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var budget = 100_000;
            var path = new List<PdfDictionary>();
            bool Walk(PdfDictionary node, int depth)
            {
                if (depth > 32 || --budget < 0 || !seen.Add(node)) return false;
                if (node.Get(DPartsKey) is PdfArray groups)
                {
                    path.Add(node);
                    for (var g = 0; g < groups.Count; g++)
                    {
                        if (groups.Get(g) is not PdfArray children) return false;
                        for (var c = 0; c < children.Count; c++)
                            if (children.Get(c) is not PdfDictionary child || !Walk(child, depth + 1)) return false;
                    }
                    path.RemoveAt(path.Count - 1);
                }
                else leaves.Add((node, [.. path]));
                return true;
            }
            if (!Walk(top, 0)) return;
            // the leaf of every page of the file as read: Start … End, without overlaps
            var indexOf = new Dictionary<int, int>();
            for (var i = 0; i < _main.Pages.Count; i++)
                if (_main.Pages[i].Id is { } id) indexOf.TryAdd(id.Number, i);
            int? IndexOf(PdfObject? page) => page is PdfReference r && indexOf.TryGetValue(r.Id.Number, out var index) ? index : null;
            var leafOf = Enumerable.Repeat(-1, _main.Pages.Count).ToArray();
            for (var l = 0; l < leaves.Count; l++)
            {
                var leaf = leaves[l].Leaf;
                if (leaf.GetRaw(StartKey) is null) continue;   // a leaf without pages
                if (IndexOf(leaf.GetRaw(StartKey)) is not { } start || IndexOf(leaf.GetRaw(EndKey) ?? leaf.GetRaw(StartKey)) is not { } end || end < start)
                    return;
                for (var p = start; p <= end; p++)
                {
                    if (leafOf[p] >= 0) return;   // ranges that overlap
                    leafOf[p] = l;
                }
            }
            // the pages written in runs of the same leaf (-1: none); the root stays open throughout (a root that is itself a leaf becomes a new
            // root above the leaves: there may be several runs now)
            var topIsLeaf = top.Get(DPartsKey) is not PdfArray;
            var root = new PartNode(topIsLeaf ? null : top);
            var open = new List<PartNode> { root };
            PartNode? last = null;
            var lastLeaf = -2;
            for (var position = 0; position < _pages.Count; position++)
            {
                var item = _pages[position];
                var index = ReferenceEquals(item.Source, _main) ? item.Page.Index : -1;
                var l = index >= 0 && index < leafOf.Length ? leafOf[index] : -1;
                if (last is not null && l == lastLeaf)
                {
                    last.Pages.Add(position);
                    continue;
                }
                // the ancestors of the leaf below the root: those still open shared, the rest copied anew
                PdfDictionary[] chain = l >= 0 && leaves[l].Ancestors.Length > 1 ? leaves[l].Ancestors[1..] : [];
                var shared = 1;
                while (shared - 1 < chain.Length && shared < open.Count && ReferenceEquals(open[shared].Original, chain[shared - 1])) shared++;
                open.RemoveRange(shared, open.Count - shared);
                for (var a = shared - 1; a < chain.Length; a++)
                {
                    var node = new PartNode(chain[a]);
                    open[^1].Children.Add(node);
                    open.Add(node);
                }
                last = new PartNode(l >= 0 ? leaves[l].Leaf : null);
                last.Pages.Add(position);
                open[^1].Children.Add(last);
                lastLeaf = l;
            }
            // write the hierarchy: numbers first (children and parents name each other)
            var plan = new PartsPlan { Root = NewNumber() };
            var depthOf = 0;
            void Number(PartNode node, int depth)
            {
                node.Number = NewNumber();
                depthOf = Math.Max(depthOf, depth);
                foreach (var child in node.Children) Number(child, depth + 1);
            }
            Number(root, 1);
            var main = _sources[_main];
            void Write(PartNode node, int parent)
            {
                var dict = new PdfDictionary();
                if (node.Original is { } original)
                    foreach (var (key, value) in original.Entries)
                        if (key != PdfName.Parent && key != DPartsKey && key != StartKey && key != EndKey) dict.Set(key, Rebase(value, main));
                dict.Set(PdfName.Type, PdfName.Get("DPart"));
                dict.Set(PdfName.Parent, new OutputReference(parent));
                if (node.Pages.Count > 0)
                {
                    dict.Set(StartKey, new OutputReference(pageNumbers[node.Pages[0]]));
                    if (node.Pages.Count > 1) dict.Set(EndKey, new OutputReference(pageNumbers[node.Pages[^1]]));
                    foreach (var position in node.Pages) plan.LeafOfPage[pageNumbers[position]] = node.Number;
                }
                else if (node.Children.Count > 0)
                    dict.Set(DPartsKey, new PdfArray([new PdfArray(node.Children.Select(c => (PdfObject)new OutputReference(c.Number)))]));
                _memoryQueue.Enqueue((node.Number, dict));
                foreach (var child in node.Children) Write(child, node.Number);
            }
            Write(root, plan.Root);
            var rootDict = new PdfDictionary();
            foreach (var (key, value) in partRoot.Entries)
                if (key != DPartRootNodeKey) rootDict.Set(key, Rebase(value, main));
            rootDict.Set(PdfName.Type, PdfName.Get("DPartRoot"));
            rootDict.Set(DPartRootNodeKey, new OutputReference(root.Number));
            // one name per level (Table 413): fewer levels left, fewer names; more levels than names (a root above a single part), no list
            // (it is optional); a record level below the last level goes
            if (partRoot.Get(NodeNameListKey) is PdfArray names && names.Count != depthOf)
            {
                if (names.Count > depthOf) rootDict.Set(NodeNameListKey, new PdfArray(Enumerable.Range(0, depthOf).Select(i => Rebase(names[i], main))));
                else rootDict.Remove(NodeNameListKey);
            }
            if (partRoot.GetInteger(RecordLevelKey) is { } record && record >= depthOf) rootDict.Remove(RecordLevelKey);
            _memoryQueue.Enqueue((plan.Root, rootDict));
            _parts = plan;
        }
    }
}
