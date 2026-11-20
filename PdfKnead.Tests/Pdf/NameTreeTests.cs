using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Name trees (ISO 32000-2, 7.9.6) as the shared building block reads and builds them (<see cref="NameTree"/>), used for the /IDTree.</summary>
public sealed class NameTreeTests
{
    private static PdfString Key(string text) => new(Encoding.ASCII.GetBytes(text));

    private static string Text(byte[] key) => Encoding.ASCII.GetString(key);

    /// <summary>
    /// Built in the byte order of the keys (not as given, and „B“ before „a“): one node up to the pairs per node, beyond leaves with /Limits and
    /// the root without.
    /// </summary>
    [Fact]
    public void Build_SortsByBytes_SplitsWithLimits()
    {
        var stored = new List<PdfDictionary>();
        PdfObject Store(PdfDictionary node)
        {
            stored.Add(node);
            return new OutputReference(stored.Count - 1);
        }
        var entries = new[] { "a", "B", "P1_2", "P1", "Z" }.Select(k => (Encoding.ASCII.GetBytes(k), (PdfObject)PdfInteger.Of(k.Length))).ToList();
        var flat = NameTree.Build(entries, Store);
        Assert.Empty(stored);
        Assert.Equal(["B", "P1", "P1_2", "Z", "a"], NameTree.Pairs(flat).Select(p => Text(p.Key)));

        var root = NameTree.Build(entries, Store, pairsPerNode: 2);
        Assert.False(root.ContainsKey(PdfName.Get("Limits")));
        Assert.Equal(2, ((PdfArray)root.GetRaw(PdfName.Get("Kids"))!).Count);   // 5 pairs → 3 leaves of at most 2 → 2 nodes of at most 2 kids
        Assert.Equal(3, stored.Count(s => s.ContainsKey(PdfName.Get("Names"))));
        var last = stored[^1];
        Assert.Equal(["a", "a"], ((PdfArray)last.GetRaw(PdfName.Get("Limits"))!).Select(l => ((PdfString)l).ToText()));
        Assert.Equal(["B", "P1", "P1_2", "Z", "a"], NameTree.Pairs(root, o => o is OutputReference r ? stored[r.Number] : o).Select(p => Text(p.Key)));
    }

    /// <summary>Reading through /Kids: all pairs in the order of the tree, a kid named twice read once, a key that is neither string nor name skipped
    /// (a name stands for its bytes, docs/FEHLERGEDAECHTNIS.md, No. 896).</summary>
    [Fact]
    public void Pairs_ThroughKids_EachNodeOnce()
    {
        var left = new PdfDictionary();
        left.Set(PdfName.Get("Names"), new PdfArray([Key("A"), PdfInteger.Of(1), PdfInteger.Of(7), PdfInteger.Of(9), Key("C"), PdfInteger.Of(3)]));
        var right = new PdfDictionary();
        right.Set(PdfName.Get("Names"), new PdfArray([Key("D"), PdfInteger.Of(4)]));
        var root = new PdfDictionary();
        root.Set(PdfName.Get("Kids"), new PdfArray([left, right, left]));
        Assert.Equal([("A", 1L), ("C", 3L), ("D", 4L)], NameTree.Pairs(root).Select(p => (Text(p.Key), ((PdfInteger)p.Value).Value)));
    }
}
