using System.Text;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The narrow path language of the mapping tables (KONZEPT.md, section 5.24, „Weg für Stufe 2“): every form the tables use, evaluated on
/// the tree of the hardened reader; forms outside it are refused when read, never guessed.
/// </summary>
public sealed class InvoicePathTests
{
    private static readonly Dictionary<string, string> Ns = new()
    {
        ["a"] = "urn:beispiel:a",
        ["b"] = "urn:beispiel:b",
    };

    private static InvoiceElement Root(string xml) => InvoiceXml.Read(Encoding.UTF8.GetBytes(xml)).Root;

    private static List<string?> Texts(string path, InvoiceElement context) => [.. InvoicePath.Parse(path, Ns).Select(context).Select(n => n.Text)];

    private const string Doc = """
        <a:r xmlns:a="urn:beispiel:a" xmlns:b="urn:beispiel:b">
          <a:x><b:id schemeID="VA">DE1</b:id><b:id schemeID="FC">123</b:id><b:id>ohne</b:id></a:x>
          <a:line><a:flag>false</a:flag><a:amount currency="EUR">10</a:amount><a:code>50</a:code></a:line>
          <a:line><a:flag>true</a:flag><a:amount currency="USD">20</a:amount><a:code>130</a:code></a:line>
          <a:cur>EUR</a:cur>
          <a:sum><a:total currency="EUR">30</a:total><a:total currency="USD">31</a:total></a:sum>
        </a:r>
        """;

    [Fact]
    public void ChildSteps_AttributeAtTheEnd_RelativeAndAbsolute()
    {
        var root = Root(Doc);
        Assert.Equal(["DE1", "123", "ohne"], Texts("./a:x/b:id", root));
        Assert.Equal(["10", "20"], Texts("/a:r/a:line/a:amount", root));
        Assert.Equal(["EUR", "USD"], Texts("./a:line/a:amount/@currency", root));
        Assert.Empty(Texts("./a:nichts", root));
        Assert.Equal(["10"], Texts("./a:line[1]/a:amount", root));
    }

    [Fact]
    public void Conditions_AttributeValueListExistsEmptyNot()
    {
        var root = Root(Doc);
        Assert.Equal(["DE1"], Texts("./a:x/b:id[@schemeID='VA']", root));
        Assert.Equal(["DE1", "123"], Texts("./a:x/b:id[@schemeID=('VA', 'FC')]", root));
        Assert.Equal(["DE1", "123"], Texts("./a:x/b:id[exists(@schemeID)]", root));
        Assert.Equal(["123", "ohne"], Texts("./a:x/b:id[not(@schemeID = 'VA')]", root));
        Assert.Equal(["ohne"], Texts("./a:x/b:id[empty(@schemeID)]", root));
    }

    [Fact]
    public void Conditions_SiblingsAncestorNumbersAndOr()
    {
        var root = Root(Doc);
        Assert.Equal(["20"], Texts("./a:line/a:amount[preceding-sibling::a:flag = 'true']", root));
        Assert.Equal(["10"], Texts("./a:line/a:amount[following-sibling::a:code = 50]", root));   // a number compared as a number
        Assert.Equal(["10"], Texts("./a:line/a:amount[following-sibling::a:code != '130']", root));
        Assert.Equal(["20"], Texts("./a:line/a:amount[ancestor::a:line/a:flag = 'true' and following-sibling::a:code = '130']", root));
        Assert.Equal(["10", "20"], Texts("./a:line/a:amount[following-sibling::a:code = '50' or preceding-sibling::a:flag = 'true']", root));
        Assert.Equal(["10", "20"], Texts("./a:line[a:flag]/a:amount", root));            // a bare path: it exists
        Assert.Equal(["20"], Texts("./a:line[a:flag = 'true']/a:amount", root));
    }

    [Fact]
    public void Conditions_PathAgainstPath_TheCurrencyOfTheInvoice()
    {
        // the form of BT-110: the total in the currency the invoice names
        var root = Root(Doc);
        Assert.Equal(["30"], Texts("./a:sum/a:total[@currency = parent::a:sum/preceding-sibling::a:cur]", root));
    }

    [Fact]
    public void Patterns_MatchFromTheNodeUpwards()
    {
        var root = Root(Doc);
        var ids = InvoicePath.Parse("./a:x/b:id", Ns).Select(root);
        var va = InvoicePath.Parse("/a:r/a:x/b:id[@schemeID='VA']", Ns);
        Assert.True(va.Matches(ids[0]));
        Assert.False(va.Matches(ids[1]));
        Assert.True(InvoicePath.Parse("a:x/b:id", Ns).Matches(ids[2]));            // relative: fits anywhere
        Assert.False(InvoicePath.Parse("/a:x/b:id", Ns).Matches(ids[2]));          // absolute: from the root
        var currency = InvoicePath.Parse("./a:line/a:amount/@currency", Ns).Select(root)[0];
        Assert.True(InvoicePath.Parse("/a:r/a:line/a:amount/@currency", Ns).Matches(currency));
        Assert.False(InvoicePath.Parse("/a:r/a:line/a:amount", Ns).Matches(currency));
        // with a memo: the condition of the common parent is asked once and remembered, the node's own condition is not (FG 842)
        var memo = new Dictionary<(InvoicePath Path, int Step, InvoiceElement Element), bool>();
        var underFc = InvoicePath.Parse("/a:r/a:x[b:id/@schemeID = 'FC']/b:id", Ns);
        Assert.All(ids, id => Assert.True(underFc.Matches(id, memo)));
        Assert.True(va.Matches(ids[0], memo));
        Assert.False(va.Matches(ids[1], memo));
        Assert.Single(memo);
        Assert.Equal(ids[0].Element.Parent, memo.Keys.Single().Element);
    }

    [Fact]
    public void PathAgainstFixedValues_GeneralComparison_CountAndExists()
    {
        // a simple path compared with fixed values is walked node by node: the result is XPath's (true if any pair compares so)
        var root = Root(Doc);
        int Count(string path) => InvoicePath.Parse(path, Ns).Count(root);
        Assert.Equal(1, Count("./a:x[b:id/@schemeID = 'FC']"));
        Assert.Equal(0, Count("./a:x[b:id/@schemeID = 'XX']"));
        Assert.Equal(1, Count("./a:x[b:id/@schemeID != 'VA']"));            // FC differs
        Assert.Equal(1, Count("./a:x['FC' = b:id/@schemeID]"));             // the fixed values on the left
        Assert.Equal(1, Count("./a:x[b:id/@schemeID != ('VA', 'FC')]"));    // any pair: VA differs from FC
        Assert.Equal(0, Count("./a:x[not(b:id/@schemeID = ('VA', 'FC'))]"));
        Assert.Equal(1, Count("./a:line[a:code = 130]"));                   // as a number
        Assert.Equal(1, Count("./a:line[a:code = 130.0]"));
        Assert.Equal(1, Count("./a:x[b:id]"));                              // exists: ends at the first
        Assert.Equal(0, Count("./a:x[b:nichts]"));
        Assert.Equal(3, Count("./a:x/b:id"));
        Assert.Equal(2, Count("./a:x/b:id/@schemeID"));
        Assert.Equal(2, Count("./a:line[a:flag]"));                         // a path with a condition: counted from its list
    }

    [Fact]
    public void ThirtyThousandSiblings_ComparedWithoutLists()
    {
        // the shape of FG 842: 30 000 identifiers in one tax registration, asked for their scheme
        var ids = string.Concat(Enumerable.Repeat("<b:id schemeID=\"FC\">1</b:id>", 30000));
        var root = Root($"""<a:r xmlns:a="urn:beispiel:a" xmlns:b="urn:beispiel:b"><a:x>{ids}<b:id schemeID="VA">DE1</b:id></a:x></a:r>""");
        var last = InvoicePath.Parse("./a:x[b:id/@schemeID = 'VA']", Ns);
        var none = InvoicePath.Parse("./a:x[b:id/@schemeID = 'XX']", Ns);
        var other = InvoicePath.Parse("./a:x[b:id/@schemeID != 'FC']", Ns);
        Assert.Equal(1, last.Count(root));
        Assert.Equal(0, none.Count(root));
        Assert.Equal(1, other.Count(root));
        Assert.Equal(30001, InvoicePath.Parse("./a:x/b:id", Ns).Count(root));
        // the whole row walked, no list of the nodes nor of their values (before: some megabytes per question)
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++) _ = none.Count(root);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024, $"{GC.GetAllocatedBytesForCurrentThread() - before} bytes");
    }

    [Fact]
    public void StringValueOfAnElementWithChildren_MadeOnce()
    {
        // the shape of FG 847: an indicator holding 100 000 empty children, its value compared by several conditions — the joined text is made
        // once (the tree does not change), not by a walk over all children for each of them
        var empty = string.Concat(Enumerable.Repeat("<a:x/>", 100000));
        var root = Root($"""<a:r xmlns:a="urn:beispiel:a"><a:c><a:flag>fal{empty}se</a:flag></a:c></a:r>""");
        var paths = new[] { "./a:c[a:flag = 'false']", "./a:c[a:flag = 'true']", "./a:c[a:flag != 'true']", "./a:c[not(a:flag = 'x')]" }
            .Select(p => InvoicePath.Parse(p, Ns)).ToList();
        Assert.Equal(1, paths[0].Count(root));   // the first: made here
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal([1, 0, 1, 1], paths.Select(p => p.Count(root)));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024, $"{GC.GetAllocatedBytesForCurrentThread() - before} bytes");
    }

    [Theory]
    [InlineData("//a:x")]                        // descendant axis
    [InlineData("./a:x | ./a:y")]                 // union
    [InlineData("./x")]                           // without prefix
    [InlineData("./c:x")]                         // unknown prefix
    [InlineData("./a:x[count(a:y) > 1]")]         // functions
    [InlineData("./a:x[@b:attr]")]                // attribute with namespace
    [InlineData("current-group()/a:x")]           // grouping of XSLT
    [InlineData("./a:x/@y/a:z")]                  // nothing below an attribute
    [InlineData("descendant::a:x")]               // other axes
    public void FormsOutsideTheLanguage_Refused(string path) => Assert.Throws<FormatException>(() => InvoicePath.Parse(path, Ns));
}
