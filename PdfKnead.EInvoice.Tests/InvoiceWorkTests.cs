using System.Text;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The reading stays linear in the size of the invoice (FG 826): a step by name through the index of an element with many children reaches
/// the same nodes as one through all children; many payment means of different codes cost work in proportion, and the direct debit of the
/// settlement is not repeated in every one of them; a shape that would still cost far more is refused as too costly instead of being read for
/// minutes. Only invented invoices.
/// </summary>
public sealed class InvoiceWorkTests
{
    private static readonly Dictionary<string, string> P = new() { ["p"] = "urn:test" };

    /// <summary>A root with children a, b, c, a, b, c … whose text is their place.</summary>
    private static InvoiceElement Tree(int count)
    {
        var sb = new StringBuilder("<p:r xmlns:p=\"urn:test\">");
        for (var i = 0; i < count; i++) sb.Append($"<p:{"abc"[i % 3]}>{i}</p:{"abc"[i % 3]}>");
        sb.Append("</p:r>");
        return InvoiceXml.Read(Encoding.UTF8.GetBytes(sb.ToString())).Root;
    }

    private static string[] Texts(IEnumerable<InvoiceNode> nodes) => [.. nodes.Select(n => n.Text ?? "")];

    [Theory]
    [InlineData(10)]    // gone through child by child
    [InlineData(40)]    // through the index of names
    public void StepsByName_SameNodesWithAndWithoutIndex(int count)
    {
        var root = Tree(count);
        var children = root.Children;
        for (var i = 0; i < children.Count; i++) Assert.Equal(i, children[i].Position);
        Assert.Equal(Texts(children.Where(c => c.Local == "b").Select(c => new InvoiceNode(c))), Texts(InvoicePath.Parse("./p:b", P).Select(root)));
        Assert.Equal(Texts(children.Where(c => c.Local == "c").Select(c => new InvoiceNode(c))),
            Texts(InvoicePath.Parse("./p:c[. != 'x']", P).Select(root)));   // the general step, with a condition
        Assert.Same(children.First(c => c.Local == "c"), root.Child("urn:test", "c"));
        Assert.Null(root.Child("urn:test", "d"));
        var following = InvoicePath.Parse("following-sibling::p:c", P);
        var preceding = InvoicePath.Parse("preceding-sibling::p:a", P);
        foreach (var b in children.Where(c => c.Local == "b"))
        {
            // following in document order, preceding nearest first — as before the index
            Assert.Equal(Texts(children.Skip(b.Position + 1).Where(c => c.Local == "c").Select(c => new InvoiceNode(c))), Texts(following.Select(b)));
            Assert.Equal(Texts(children.Take(b.Position).Reverse().Where(c => c.Local == "a").Select(c => new InvoiceNode(c))), Texts(preceding.Select(b)));
        }
        var condition = InvoicePath.Parse("./p:b[preceding-sibling::p:c = '5']", P);
        Assert.Equal(Texts(children.Where(c => c.Local == "b" && c.Position > 5).Select(c => new InvoiceNode(c))), Texts(condition.Select(root)));
    }

    /// <summary>The invented CII invoice with <paramref name="count"/> payment means of different codes and a direct debit mandate.</summary>
    private static string ManyPaymentMeans(int count)
    {
        var cii = InvoiceReaderTests.Cii;
        const string open = "<ram:SpecifiedTradeSettlementPaymentMeans>", close = "</ram:SpecifiedTradeSettlementPaymentMeans>";
        var start = cii.IndexOf(open, StringComparison.Ordinal);
        var end = cii.LastIndexOf(close, StringComparison.Ordinal) + close.Length;
        var means = new StringBuilder();
        for (var i = 0; i < count; i++)
            means.Append($"{open}<ram:TypeCode>{i}</ram:TypeCode><ram:PayeePartyCreditorFinancialAccount><ram:IBANID>DE02120300000000202051</ram:IBANID>"
                + $"</ram:PayeePartyCreditorFinancialAccount>{close}");
        means.Append("<ram:SpecifiedTradePaymentTerms><ram:DirectDebitMandateID>MANDAT-1</ram:DirectDebitMandateID></ram:SpecifiedTradePaymentTerms>");
        return cii[..start] + means + cii[end..];
    }

    [Fact]
    public void ManyPaymentMeans_WorkInProportion()
    {
        // FG 826: 20 000 payment means of different codes took 14 s (the settlement's 20 000 children gone through from every group, and the
        // direct debit rebuilt in every group); now the work per payment means stays the same
        var small = InvoiceReaderTests.Read(ManyPaymentMeans(2000));
        var large = InvoiceReaderTests.Read(ManyPaymentMeans(4000));
        Assert.Equal(4000, large.Groups("BG-16").Count());
        Assert.True(large.Work < 2.3 * small.Work, $"{small.Work} → {large.Work}");
    }

    /// <summary>The invented CII invoice with its settlement repeated <paramref name="count"/> times (each with one payment means of code 58).</summary>
    private static string ManySettlements(int count)
    {
        var cii = InvoiceReaderTests.Cii;
        const string open = "<ram:ApplicableHeaderTradeSettlement>", close = "</ram:ApplicableHeaderTradeSettlement>";
        var start = cii.IndexOf(open, StringComparison.Ordinal);
        var end = cii.IndexOf(close, StringComparison.Ordinal) + close.Length;
        return cii[..end] + string.Concat(Enumerable.Repeat(cii[start..end], count - 1)) + cii[end..];
    }

    [Fact]
    public void ManyMembersWithOneParent_WorkInProportion()
    {
        // FG 829 (fuzzer): 9 865 settlements, all payment means of one code — evaluated member by member, „../../ram:…Settlement“ went from each
        // to all settlements (97 million nodes, 2.9 GB); from the members together, step by step over distinct nodes, it stays linear
        var small = InvoiceReaderTests.Read(ManySettlements(500));
        var large = InvoiceReaderTests.Read(ManySettlements(1000));
        Assert.True(large.Work < 2.3 * small.Work, $"{small.Work} → {large.Work}");
        Assert.Single(large.Groups("BG-16"));   // one code: one payment group with every account
        Assert.Equal(2000, large.Groups("BG-16").Single().GroupsOf("BG-17").Count());
    }

    [Fact]
    public void ManyAmountsWithCurrencyCondition_LittleMemory()
    {
        // FG 829 (fuzzer): 35 000 tax totals, each checked against the currency of the invoice by a condition, took 46 times the file's size —
        // lists for every check; now a single attribute is read directly and the lists of an evaluation are lent and given back
        var amounts = string.Concat(Enumerable.Repeat("<ram:TaxTotalAmount currencyID=\"EUR\">4.50</ram:TaxTotalAmount>", 20_000));
        var xml = InvoiceReaderTests.Cii.Replace("<ram:TaxTotalAmount currencyID=\"EUR\">4.50</ram:TaxTotalAmount>", amounts, StringComparison.Ordinal);
        var doc = InvoiceXml.Read(Encoding.UTF8.GetBytes(xml));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var data = InvoiceReader.Read(doc, InvoiceKind.Of(doc))!;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(20_000, data.Root.AllFields().Count(f => f.Id == "BT-110"));
        Assert.True(allocated < 30L << 20, $"{allocated >> 20} MB for {xml.Length >> 10} KB");
    }

    [Fact]
    public void DirectDebitOfTheSettlement_RepeatedInTheFirstPaymentGroupsOnly()
    {
        var data = InvoiceReaderTests.Read(ManyPaymentMeans(20));
        var groups = data.Groups("BG-16").ToList();
        Assert.Equal(20, groups.Count);
        // as the visualization does, in each group — up to the limit; the rest says nothing new
        Assert.Equal(InvoiceReader.MaxRepeatedPaymentGroups, groups.Count(g => g.GroupsOf("BG-19").Any()));
        Assert.All(groups.Take(InvoiceReader.MaxRepeatedPaymentGroups), g => Assert.Equal("MANDAT-1", g.GroupsOf("BG-19").Single().Text("BT-89")));
        Assert.DoesNotContain(data.Further, f => f.Text == "MANDAT-1");   // taken, not left over
    }

    [Fact]
    public void ManySiblingsAgainstManySiblings_RefusedAsTooCostly()
    {
        // a document level allowance whose amounts each look back at thousands of charge indicators: each amount compares with all of them,
        // so the work grows with their product — refused with a reason instead of read for minutes
        var sb = new StringBuilder(InvoiceReaderTests.Ubl[..InvoiceReaderTests.Ubl.IndexOf("<cac:TaxTotal>", StringComparison.Ordinal)]);
        sb.Append("<cac:AllowanceCharge><cbc:ChargeIndicator>false</cbc:ChargeIndicator>");
        for (var i = 0; i < 3000; i++) sb.Append("<cbc:ChargeIndicator>true</cbc:ChargeIndicator>");
        for (var i = 0; i < 3000; i++) sb.Append("<cbc:Amount currencyID=\"EUR\">1.00</cbc:Amount>");
        sb.Append("</cac:AllowanceCharge>");
        sb.Append(InvoiceReaderTests.Ubl[InvoiceReaderTests.Ubl.IndexOf("<cac:TaxTotal>", StringComparison.Ordinal)..]);
        var doc = InvoiceXml.Read(Encoding.UTF8.GetBytes(sb.ToString()));
        var e = Assert.Throws<InvoiceXmlException>(() => InvoiceReader.Read(doc, InvoiceKind.Of(doc)));
        Assert.Equal(InvoiceXmlRejection.TooComplex, e.Reason);
        Assert.Equal("zu aufwendig zu lesen", InvoiceLocator.Describe(e.Reason));
    }

    [Fact]
    public void UsualInvoices_FarBelowTheLimit()
    {
        // the limit is a guard, not a measure the usual invoice comes near (over the collections at most 8.3 per element, docs/PRUEFUNGEN.md §6y)
        foreach (var xml in new[] { InvoiceReaderTests.Cii, InvoiceReaderTests.Ubl })
        {
            var doc = InvoiceXml.Read(Encoding.UTF8.GetBytes(xml));
            var data = InvoiceReader.Read(doc, InvoiceKind.Of(doc))!;
            Assert.True(data.Work < InvoiceReader.WorkPerElement * 1000 / 10, data.Work.ToString());
        }
    }
}
