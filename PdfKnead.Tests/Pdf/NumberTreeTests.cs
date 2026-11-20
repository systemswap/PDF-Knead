using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Number trees (ISO 32000-2, 7.9.7) as the shared building block reads and builds them (<see cref="NumberTree"/>).</summary>
public sealed class NumberTreeTests
{
    private static PdfDictionary Node(string key, PdfObject value)
    {
        var node = new PdfDictionary();
        node.Set(PdfName.Get(key), value);
        return node;
    }

    private static PdfArray Ints(params long[] values) => new(values.Select(v => (PdfObject)PdfInteger.Of(v)));

    /// <summary>
    /// A tree beyond the pairs per node: leaves with /Limits, nodes with /Limits above them where there are more leaves than kids per node, the
    /// root without /Limits.
    /// </summary>
    [Fact]
    public void Build_SplitsWithLimits()
    {
        var stored = new List<PdfDictionary>();
        PdfObject Store(PdfDictionary node)
        {
            stored.Add(node);
            return new OutputReference(stored.Count - 1);
        }
        var entries = Enumerable.Range(0, 20).Select(i => ((long)i * 2, (PdfObject)PdfInteger.Of(i))).ToList();
        var flat = NumberTree.Build(entries.Take(4).ToList(), Store, pairsPerNode: 4);
        Assert.Equal(8, ((PdfArray)flat.GetRaw(PdfName.Get("Nums"))!).Count);
        Assert.Empty(stored);

        var root = NumberTree.Build(entries, Store, pairsPerNode: 4);
        Assert.False(root.ContainsKey(PdfName.Get("Limits")));
        var top = (PdfArray)root.GetRaw(PdfName.Get("Kids"))!;
        Assert.Equal(2, top.Count);   // 5 leaves of 4 pairs → 2 nodes of at most 4 kids
        var first = stored[((OutputReference)top[0]).Number];
        Assert.Equal([0L, 30L], ((PdfArray)first.GetRaw(PdfName.Get("Limits"))!).Select(l => ((PdfInteger)l).Value));
        var leaves = stored.Where(s => s.ContainsKey(PdfName.Get("Nums"))).ToList();
        Assert.Equal(5, leaves.Count);
        Assert.Equal([32L, 38L], ((PdfArray)leaves[^1].GetRaw(PdfName.Get("Limits"))!).Select(l => ((PdfInteger)l).Value));
    }

    /// <summary>Reading through /Kids: all pairs in the order of the tree; a key looked up only where /Limits hold it; a kid named twice read once.</summary>
    [Fact]
    public void Pairs_AndValue_ThroughKids()
    {
        var left = Node("Nums", Ints(1, 10, 3, 30));
        left.Set(PdfName.Get("Limits"), Ints(1, 3));
        var right = Node("Nums", Ints(5, 50));
        right.Set(PdfName.Get("Limits"), Ints(5, 5));
        var root = Node("Kids", new PdfArray([left, right, left]));
        Assert.Equal([(1L, 10L), (3L, 30L), (5L, 50L)], NumberTree.Pairs(root).Select(p => (p.Key, ((PdfInteger)p.Value).Value)));
        Assert.Equal(30, ((PdfInteger)NumberTree.Value(root, 3)!).Value);
        Assert.Equal(50, ((PdfInteger)NumberTree.Value(root, 5)!).Value);
        Assert.Null(NumberTree.Value(root, 4));
    }
}
