using System.Text.RegularExpressions;

namespace PdfKnead.EInvoice;

/// <summary>What a finding means for the buyer (KONZEPT.md, section 5.24, E4 „Abweichungen zuerst und Klartext“).</summary>
public enum InvoiceMeaning
{
    /// <summary>The file does not follow the format (the schema): it is then an other invoice, no e-invoice (BMF 15.10.2025, Rn. 6a).</summary>
    Format,

    /// <summary>A rule broken on a mandatory detail of § 14 Abs. 4 or § 14a UStG: the invoice is not proper (Rn. 35a).</summary>
    MandatoryDetail,

    /// <summary>Another business rule (Rn. 6b): for VAT of no account as long as no mandatory detail is concerned (Rn. 35a).</summary>
    BusinessRule,

    /// <summary>The PDF around the invoice, its metadata, the profile declared, hints of PDF-Knead: not the invoice data.</summary>
    Technical,
}

/// <summary>
/// Sorts a finding by what it means for the buyer — the order of the list too (the weightiest first) — following the BMF letter of 15.10.2025
/// (Rn. 6a, 6b, 35a; read in the letter itself, 16.11.2026): format errors make the file an other invoice; business rule errors on the
/// details of §§ 14 Abs. 4, 14a UStG make it improper; those on other details are of no account for VAT. By the identifier of the rule and the
/// business term it concerns; never „gültig“ (the checker supports the buyer's own check, Rn. 35a).
/// </summary>
public static class InvoiceFindingMeaning
{
    /// <summary>
    /// The business terms that hold a mandatory detail: Nr. 1 names and addresses, Nr. 2 tax number or VAT ID (also of a tax representative),
    /// Nr. 3 date, Nr. 4 number, Nr. 5 quantity and kind, Nr. 6 time of the supply, Nr. 7 the amount by rate, Nr. 8 rate and tax amount or the
    /// note of the exemption; § 14a the buyer's VAT ID.
    /// </summary>
    private static readonly HashSet<string> MandatoryTerms = new(StringComparer.Ordinal)
    {
        "BT-27", "BT-35", "BT-36", "BT-37", "BT-38", "BT-40", "BT-44", "BT-50", "BT-51", "BT-52", "BT-53", "BT-55",
        "BT-31", "BT-32", "BT-63", "BT-2", "BT-1", "BT-153", "BT-129", "BT-130", "BT-72", "BT-73", "BT-74", "BT-134", "BT-135",
        "BT-116", "BT-110", "BT-117", "BT-118", "BT-119", "BT-120", "BT-121", "BT-48",
    };

    /// <summary>
    /// The groups that are a mandatory detail as a whole (the VAT breakdown) — for a rule on the group without a term that names no term in its
    /// description either.
    /// </summary>
    private static readonly HashSet<string> MandatoryGroups = new(StringComparer.Ordinal) { "BG-23" };

    /// <summary>The terms a description of a rule names („verlangt das Lieferdatum (BT-72) …“).</summary>
    private static readonly Regex NamedTerms = new(@"\bBT-\d+\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>The rules on the PDF and its metadata, the declared profile and the hints of PDF-Knead.</summary>
    private static readonly string[] TechnicalPrefixes = ["BR-HYBRID", "PDFK-PDF-", "BR-FX-DE-", "PDFK-FX-", "PDFK-EN-", "PDFK-XML-"];

    /// <summary>What the finding means.</summary>
    public static InvoiceMeaning Of(InvoiceRuleViolation violation)
    {
        var id = violation.Rule.Id;
        if (id.StartsWith("PDFK-XSD-", StringComparison.Ordinal)) return InvoiceMeaning.Format;
        if (TechnicalPrefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal))) return InvoiceMeaning.Technical;
        if (violation.Field is { } field) return MandatoryTerms.Contains(field.Id) ? InvoiceMeaning.MandatoryDetail : InvoiceMeaning.BusinessRule;
        // a rule on a group without a term: by the terms its description names — the delivery date of an intra-community supply (BR-IC-11,
        // BT-72) is a mandatory detail, its country of delivery (BR-IC-12, BT-80) is not (found in the collections, 16.11.2026); naming none,
        // by the group
        var named = NamedTerms.Matches(violation.Rule.German).Select(m => m.Value).ToList();
        if (named.Count > 0) return named.Any(MandatoryTerms.Contains) ? InvoiceMeaning.MandatoryDetail : InvoiceMeaning.BusinessRule;
        return MandatoryGroups.Contains(violation.Group.Id) ? InvoiceMeaning.MandatoryDetail : InvoiceMeaning.BusinessRule;
    }
}
