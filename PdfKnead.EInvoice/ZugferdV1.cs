namespace PdfKnead.EInvoice;

/// <summary>
/// ZUGFeRD 1.0 in readable form (KONZEPT.md, section 5.24, „Weg für Stufe 2“): its tree (CrossIndustryDocument, UN/CEFACT data standards of
/// version 12/15) is copied with the names of CII 2 (CrossIndustryInvoice, version 100), so that the mapping of CII reads it. The renaming
/// follows the structure both have in common — header and line parts, the monetary summation, the rate of a tax; values whose names differ
/// otherwise stay as they are and show up as „Weitere Angaben“. The data are not changed, only the names; the representation says it was read
/// after ZUGFeRD 1.
/// </summary>
internal static class ZugferdV1
{
    private const string UnCefact = "urn:un:unece:uncefact:data:standard:";

    /// <summary>Element names of ZUGFeRD 1 and their names in CII 2; some depend on the parent (header or line).</summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["CrossIndustryDocument"] = "CrossIndustryInvoice",
        ["SpecifiedExchangedDocumentContext"] = "ExchangedDocumentContext",
        ["HeaderExchangedDocument"] = "ExchangedDocument",
        ["SpecifiedSupplyChainTradeTransaction"] = "SupplyChainTradeTransaction",
        ["ApplicableSupplyChainTradeAgreement"] = "ApplicableHeaderTradeAgreement",
        ["ApplicableSupplyChainTradeDelivery"] = "ApplicableHeaderTradeDelivery",
        ["ApplicableSupplyChainTradeSettlement"] = "ApplicableHeaderTradeSettlement",
        ["SpecifiedSupplyChainTradeAgreement"] = "SpecifiedLineTradeAgreement",
        ["SpecifiedSupplyChainTradeDelivery"] = "SpecifiedLineTradeDelivery",
        ["SpecifiedSupplyChainTradeSettlement"] = "SpecifiedLineTradeSettlement",
    };

    /// <summary>A copy of the tree with the names of CII 2.</summary>
    public static InvoiceXmlDocument Translate(InvoiceXmlDocument xml)
    {
        var root = Copy(xml.Root, null);
        var pending = new Stack<(InvoiceElement Source, InvoiceElement Target)>();
        pending.Push((xml.Root, root));
        while (pending.Count > 0)
        {
            var (source, target) = pending.Pop();
            if (source.Children.Count == 0) continue;
            var children = new InvoiceElement[source.Children.Count];
            for (var i = 0; i < children.Length; i++)
            {
                children[i] = Copy(source.Children[i], target);
                pending.Push((source.Children[i], children[i]));
            }
            target.SetChildren(children);
        }
        return new InvoiceXmlDocument(root, xml.Notes, xml.Elements, xml.MaxDepthSeen);
    }

    private static InvoiceElement Copy(InvoiceElement source, InvoiceElement? parent)
    {
        var copy = new InvoiceElement(Namespace(source.Namespace), Local(source.Local, parent?.Local), parent)
        {
            Text = source.Text,
            Binary = source.Binary,
        };
        if (source.Attributes.Count > 0) copy.SetAttributes([.. source.Attributes]);
        return copy;
    }

    /// <summary>The root of ZUGFeRD 1 (and its drafts) becomes the root of CII; the data standards of UN/CEFACT become version 100.</summary>
    private static string Namespace(string ns)
    {
        if (ns.StartsWith("urn:ferd:", StringComparison.Ordinal)) return InvoiceKind.Cii;
        if (ns.StartsWith(UnCefact, StringComparison.Ordinal))
        {
            var colon = ns.LastIndexOf(':');
            if (colon > UnCefact.Length && ns[(colon + 1)..].All(char.IsAsciiDigit)) return ns[..(colon + 1)] + "100";
        }
        return ns;
    }

    private static string Local(string local, string? parent) => local switch
    {
        // the monetary summation: of the header or of a line, by its (already renamed) parent
        "SpecifiedTradeSettlementMonetarySummation" => parent == "SpecifiedLineTradeSettlement"
            ? "SpecifiedTradeSettlementLineMonetarySummation" : "SpecifiedTradeSettlementHeaderMonetarySummation",
        "ApplicablePercent" when parent is "ApplicableTradeTax" or "CategoryTradeTax" => "RateApplicablePercent",
        _ => Names.GetValueOrDefault(local, local),
    };
}
