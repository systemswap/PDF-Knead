using System.Text;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Sub invoice lines of CII (KONZEPT.md, section 5.24, stage 2, „Positionen samt Unterpositionen“): a line whose ParentLineID names another
/// line's LineID is shown inside it as BG-DEX-01, as UBL nests its SubInvoiceLine. A missing or doubled parent, a circle and a chain deeper
/// than the limit leave the line among the lines, its ParentLineID visible. Only invented invoices.
/// </summary>
public sealed class InvoiceSubLineTests
{
    /// <summary>The invented CII invoice with these lines instead of its own (LineID, ParentLineID).</summary>
    private static InvoiceData WithLines(params (string Id, string? Parent)[] lines)
    {
        var cii = InvoiceReaderTests.Cii;
        const string open = "<ram:IncludedSupplyChainTradeLineItem>", close = "</ram:IncludedSupplyChainTradeLineItem>";
        var start = cii.IndexOf(open, StringComparison.Ordinal);
        var end = cii.LastIndexOf(close, StringComparison.Ordinal) + close.Length;
        var sb = new StringBuilder();
        foreach (var (id, parent) in lines)
            sb.Append($"{open}<ram:AssociatedDocumentLineDocument><ram:LineID>{id}</ram:LineID>")
                .Append(parent is null ? "" : $"<ram:ParentLineID>{parent}</ram:ParentLineID>")
                .Append($"</ram:AssociatedDocumentLineDocument><ram:SpecifiedTradeProduct><ram:Name>Artikel {id}</ram:Name></ram:SpecifiedTradeProduct>")
                .Append("<ram:SpecifiedLineTradeSettlement><ram:SpecifiedTradeSettlementLineMonetarySummation><ram:LineTotalAmount>1.00</ram:LineTotalAmount>")
                .Append($"</ram:SpecifiedTradeSettlementLineMonetarySummation></ram:SpecifiedLineTradeSettlement>{close}");
        return InvoiceReaderTests.Read(cii[..start] + sb + cii[end..]);
    }

    private static string? Id(InvoiceGroup g) => g.Text("BT-126");

    [Fact]
    public void ParentLineId_NestsTheLine_InDocumentOrder()
    {
        var data = WithLines(("1", null), ("1.1", "1"), ("1.1.1", "1.1"), ("1.2", "1"), ("2", null));
        var top = data.Groups("BG-25").ToList();
        Assert.Equal(["1", "2"], top.Select(Id));
        var subs = top[0].GroupsOf("BG-DEX-01").ToList();
        Assert.Equal(["1.1", "1.2"], subs.Select(Id));
        Assert.Equal("1.1.1", Id(subs[0].GroupsOf("BG-DEX-01").Single()));
        Assert.Equal("Artikel 1.1.1", subs[0].GroupsOf("BG-DEX-01").Single().GroupsOf("BG-31").Single().Text("BT-153"));   // its item kept
        Assert.Equal("Unterposition", subs[0].Label);
        Assert.DoesNotContain(data.Further, f => f.Node.Element.Local == "ParentLineID");   // the nesting shows it
    }

    [Fact]
    public void MissingDoubledOrCircularParent_LineStaysAndParentLineIdVisible()
    {
        var data = WithLines(("1", null), ("2", "9"), ("3", "4"), ("4", "3"), ("5", null), ("5", null), ("6", "5"), ("7", "7"));
        Assert.Equal(["1", "2", "3", "4", "5", "5", "6", "7"], data.Groups("BG-25").Select(Id));
        Assert.Empty(data.Groups("BG-25").SelectMany(g => g.GroupsOf("BG-DEX-01")));
        Assert.Equal(["9", "4", "3", "5", "7"], data.Further.Where(f => f.Node.Element.Local == "ParentLineID").Select(f => f.Text));
    }

    [Fact]
    public void LongChain_NestedUpToTheLimit()
    {
        // each line the parent of the next: 16 levels, then the 17th starts again among the lines
        var lines = Enumerable.Range(1, 20).Select(i => (i.ToString(), i == 1 ? null : (i - 1).ToString())).Select(p => (p.Item1, (string?)p.Item2)).ToArray();
        var data = WithLines(lines);
        Assert.Equal(["1", "17"], data.Groups("BG-25").Select(Id));
        var depth = 1;
        for (var g = data.Groups("BG-25").First(); g.GroupsOf("BG-DEX-01").SingleOrDefault() is { } next; g = next) depth++;
        Assert.Equal(InvoiceReader.MaxSubLineDepth, depth);
    }

    [Fact]
    public void RulesOfTheStandard_CountTheSubLinesOfCii()
    {
        // in CII a line naming a parent is a line of its own in the model of the standard: the sum of the lines (BR-CO-10), the taxable amount
        // of its category (BR-S-08) and the decimals of its net amount (BR-DEC-23) count it, as the artefacts of CII do (FG 843)
        const string second = "<ram:LineID>2</ram:LineID>";
        var sub = InvoiceReaderTests.Cii.Replace(second, second + "<ram:ParentLineID>1</ram:ParentLineID>", StringComparison.Ordinal);
        var data = InvoiceReaderTests.Read(sub);
        Assert.Single(data.Groups("BG-25"));   // shown inside line 1
        var ids = InvoiceRules.Check(data).Select(v => v.Rule.Id).ToList();
        Assert.DoesNotContain("BR-CO-10", ids);
        Assert.DoesNotContain("BR-S-08", ids);
        var decimals = sub.Replace("<ram:LineTotalAmount>10.00</ram:LineTotalAmount>", "<ram:LineTotalAmount>10.001</ram:LineTotalAmount>", StringComparison.Ordinal);
        Assert.Contains("BR-DEC-23", InvoiceRules.Check(InvoiceReaderTests.Read(decimals)).Select(v => v.Rule.Id));
    }
}
