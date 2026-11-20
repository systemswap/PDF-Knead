using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Document;

/// <summary>
/// Number trees (ISO 32000-2, 7.9.7) — the parent tree of the structure, page labels: looking a key up, reading all pairs, building one. Every
/// walk stops at a depth of 32 and within a budget (crafted trees).
/// </summary>
internal static class NumberTree
{
    private const int MaxDepth = 32;

    /// <summary>Pairs per node of a tree built; more go into /Kids with /Limits.</summary>
    internal const int PairsPerNode = 4096;

    private static readonly PdfName Nums = PdfName.Get("Nums"), Kids = PdfName.Get("Kids"), Limits = PdfName.Get("Limits");

    /// <summary>
    /// The key a number tree entry stands for: an integer (ISO 32000-2, 7.9.7, Table 37), also a real with an integral value (3.0 stands for 3;
    /// read so when looking up and when listing — before, a lookup cut 3.7 to 3 and a listing dropped 3.0, and a writer building the tree anew
    /// from the listing lost the pair; docs/FEHLERGEDAECHTNIS.md, No. 896); null for anything else (3.7 is no key).
    /// </summary>
    public static long? KeyOf(PdfObject? key) => key switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real when double.IsFinite(real.Value) && real.Value == Math.Floor(real.Value) && Math.Abs(real.Value) < 9e15 => (long)real.Value,
        _ => null,
    };

    /// <summary>The value for a key (resolved); null if the tree has none. Kids whose /Limits do not hold the key are not looked into.</summary>
    public static PdfObject? Value(PdfDictionary root, long key) => Value(root, key, 0);

    private static PdfObject? Value(PdfDictionary node, long key, int depth)
    {
        if (depth > MaxDepth) return null;
        if (node.Get(Nums) is PdfArray pairs)
            for (var i = 0; i + 1 < pairs.Count; i += 2)
                if (KeyOf(pairs.Get(i)) == key) return pairs.Get(i + 1);
        if (node.Get(Kids) is PdfArray kids)
            for (var i = 0; i < kids.Count && i < 10_000; i++)
                if (kids.Get(i) is PdfDictionary kid &&
                    (kid.GetArray(Limits) is not { Count: 2 } limits || limits.Get(0).AsNumber() <= key && key <= limits.Get(1).AsNumber()) &&
                    Value(kid, key, depth + 1) is { } found)
                    return found;
        return null;
    }

    /// <summary>
    /// All pairs, in the order of the tree, each node once: the key and the value as it stands (a reference stays a reference — a writer keeps
    /// it as it is).
    /// </summary>
    /// <param name="root">The root node.</param>
    /// <param name="resolve">Resolves nodes, keys and arrays; null: as the dictionaries resolve them themselves.</param>
    /// <param name="budget">At most this many pairs and kids.</param>
    public static IEnumerable<(long Key, PdfObject Value)> Pairs(PdfDictionary root, Func<PdfObject?, PdfObject?>? resolve = null, int budget = 1_000_000)
    {
        PdfObject? Entry(PdfDictionary owner, PdfName key) => resolve is null ? owner.Get(key) : resolve(owner.GetRaw(key));
        PdfObject? Item(PdfArray array, int index) => resolve is null ? array.Get(index) : resolve(array[index]);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfDictionary Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.TryPop(out var item) && budget > 0)
        {
            if (item.Depth > MaxDepth || !seen.Add(item.Node)) continue;
            if (Entry(item.Node, Nums) is PdfArray nums)
                for (var i = 0; i + 1 < nums.Count && --budget > 0; i += 2)
                    if (KeyOf(Item(nums, i)) is { } key) yield return (key, nums[i + 1]);
            if (Entry(item.Node, Kids) is PdfArray kids)
                for (var i = Math.Min(kids.Count, 100_000) - 1; i >= 0 && --budget > 0; i--)
                    if (Item(kids, i) is PdfDictionary kid) stack.Push((kid, item.Depth + 1));
        }
    }

    /// <summary>
    /// A number tree of the entries (sorted by key): one node with /Nums up to <paramref name="pairsPerNode"/> pairs, else leaves of that many
    /// pairs and nodes of that many kids, each with /Limits — the root without.
    /// </summary>
    /// <param name="entries">The pairs, sorted by key.</param>
    /// <param name="store">Writes a node as an object of its own and returns the reference to it.</param>
    /// <param name="pairsPerNode">Pairs per leaf and kids per node.</param>
    /// <returns>The root node.</returns>
    public static PdfDictionary Build(IReadOnlyList<(long Key, PdfObject Value)> entries, Func<PdfDictionary, PdfObject> store, int pairsPerNode = PairsPerNode)
    {
        // the keys in ascending order (ISO 32000-2, 7.9.7) whatever order the caller gives — the tree does not depend on each caller sorting
        if (!entries.Zip(entries.Skip(1)).All(p => p.First.Key <= p.Second.Key)) entries = [.. entries.OrderBy(e => e.Key)];
        if (entries.Count <= pairsPerNode) return Leaf(entries, 0, entries.Count);
        var level = new List<(PdfDictionary Node, long First, long Last)>();
        for (var start = 0; start < entries.Count;)
        {
            var end = Math.Min(entries.Count, start + pairsPerNode);
            // a key given twice stays within one leaf: the ranges of the leaves do not overlap (7.9.7 with 7.9.6)
            while (end < entries.Count && entries[end].Key == entries[end - 1].Key) end++;
            var leaf = Leaf(entries, start, end);
            leaf.Set(Limits, Bounds(entries[start].Key, entries[end - 1].Key));
            level.Add((leaf, entries[start].Key, entries[end - 1].Key));
            start = end;
        }
        while (level.Count > pairsPerNode)
        {
            var up = new List<(PdfDictionary Node, long First, long Last)>();
            for (var start = 0; start < level.Count; start += pairsPerNode)
            {
                var group = level.Skip(start).Take(pairsPerNode).ToList();
                var node = new PdfDictionary();
                node.Set(Kids, new PdfArray(group.Select(g => store(g.Node))));
                node.Set(Limits, Bounds(group[0].First, group[^1].Last));
                up.Add((node, group[0].First, group[^1].Last));
            }
            level = up;
        }
        var root = new PdfDictionary();
        root.Set(Kids, new PdfArray(level.Select(l => store(l.Node))));
        return root;
    }

    private static PdfArray Bounds(long first, long last) => new([PdfInteger.Of(first), PdfInteger.Of(last)]);

    private static PdfDictionary Leaf(IReadOnlyList<(long Key, PdfObject Value)> entries, int start, int end)
    {
        var nums = new PdfArray();
        for (var i = start; i < end; i++)
        {
            nums.Add(PdfInteger.Of(entries[i].Key));
            nums.Add(entries[i].Value);
        }
        var node = new PdfDictionary();
        node.Set(Nums, nums);
        return node;
    }
}
