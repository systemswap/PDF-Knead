using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Document;

/// <summary>
/// Name trees (ISO 32000-2, 7.9.6) — the /IDTree of the structure: reading all pairs, building one. Keys are strings compared byte by byte
/// (lexical order of the bytes, 7.9.6). Every walk stops at a depth of 32 and within a budget (crafted trees).
/// </summary>
internal static class NameTree
{
    private const int MaxDepth = 32;

    /// <summary>Pairs per node of a tree built; more go into /Kids with /Limits.</summary>
    internal const int PairsPerNode = 4096;

    private static readonly PdfName Names = PdfName.Get("Names"), Kids = PdfName.Get("Kids"), Limits = PdfName.Get("Limits");

    /// <summary>
    /// The bytes a name tree entry's key stands for: a string (ISO 32000-2, 7.9.6, Table 36), also a name — written where a string belongs, it
    /// stands for its own bytes (before, it was passed over, and a writer building the tree anew lost the pair; docs/FEHLERGEDAECHTNIS.md,
    /// No. 896); null for anything else.
    /// </summary>
    public static byte[]? KeyOf(PdfObject? key) => key switch
    {
        PdfString text => text.Bytes,
        PdfName name => System.Text.Encoding.Latin1.GetBytes(name.Value),
        _ => null,
    };

    /// <summary>All pairs in the order of the tree, each node once: the key's bytes and the value as it stands.</summary>
    /// <param name="root">The root node.</param>
    /// <param name="resolve">Resolves nodes, keys and arrays; null: as the dictionaries resolve them themselves.</param>
    /// <param name="budget">At most this many pairs and kids.</param>
    public static IEnumerable<(byte[] Key, PdfObject Value)> Pairs(PdfDictionary root, Func<PdfObject?, PdfObject?>? resolve = null, int budget = 1_000_000)
    {
        PdfObject? Entry(PdfDictionary owner, PdfName key) => resolve is null ? owner.Get(key) : resolve(owner.GetRaw(key));
        PdfObject? Item(PdfArray array, int index) => resolve is null ? array.Get(index) : resolve(array[index]);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfDictionary Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.TryPop(out var item) && budget > 0)
        {
            if (item.Depth > MaxDepth || !seen.Add(item.Node)) continue;
            if (Entry(item.Node, Names) is PdfArray names)
                for (var i = 0; i + 1 < names.Count && --budget > 0; i += 2)
                    if (KeyOf(Item(names, i)) is { } key) yield return (key, names[i + 1]);
            if (Entry(item.Node, Kids) is PdfArray kids)
                for (var i = Math.Min(kids.Count, 100_000) - 1; i >= 0 && --budget > 0; i--)
                    if (Item(kids, i) is PdfDictionary kid) stack.Push((kid, item.Depth + 1));
        }
    }

    /// <summary>Byte-wise order of keys (ISO 32000-2, 7.9.6).</summary>
    public static int Compare(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);

    /// <summary>
    /// A name tree of the entries (sorted here byte by byte): one node with /Names up to <paramref name="pairsPerNode"/> pairs, else leaves of
    /// that many and nodes of that many kids, each with /Limits — the root without.
    /// </summary>
    public static PdfDictionary Build(IEnumerable<(byte[] Key, PdfObject Value)> entries, Func<PdfDictionary, PdfObject> store, int pairsPerNode = PairsPerNode)
    {
        var sorted = entries.OrderBy(e => e.Key, Comparer<byte[]>.Create(Compare)).ToList();
        PdfDictionary Leaf(int start, int end)
        {
            var names = new PdfArray();
            for (var i = start; i < end; i++)
            {
                names.Add(new PdfString(sorted[i].Key));
                names.Add(sorted[i].Value);
            }
            var node = new PdfDictionary();
            node.Set(Names, names);
            return node;
        }
        if (sorted.Count <= pairsPerNode) return Leaf(0, sorted.Count);
        var level = new List<(PdfDictionary Node, byte[] First, byte[] Last)>();
        for (var start = 0; start < sorted.Count;)
        {
            var end = Math.Min(sorted.Count, start + pairsPerNode);
            // a key given twice stays within one leaf: the ranges of the leaves do not overlap (7.9.6)
            while (end < sorted.Count && Compare(sorted[end].Key, sorted[end - 1].Key) == 0) end++;
            var leaf = Leaf(start, end);
            leaf.Set(Limits, new PdfArray([new PdfString(sorted[start].Key), new PdfString(sorted[end - 1].Key)]));
            level.Add((leaf, sorted[start].Key, sorted[end - 1].Key));
            start = end;
        }
        while (level.Count > pairsPerNode)
        {
            var up = new List<(PdfDictionary Node, byte[] First, byte[] Last)>();
            for (var start = 0; start < level.Count; start += pairsPerNode)
            {
                var group = level.Skip(start).Take(pairsPerNode).ToList();
                var node = new PdfDictionary();
                node.Set(Kids, new PdfArray(group.Select(g => store(g.Node))));
                node.Set(Limits, new PdfArray([new PdfString(group[0].First), new PdfString(group[^1].Last)]));
                up.Add((node, group[0].First, group[^1].Last));
            }
            level = up;
        }
        var root = new PdfDictionary();
        root.Set(Kids, new PdfArray(level.Select(l => store(l.Node))));
        return root;
    }
}
