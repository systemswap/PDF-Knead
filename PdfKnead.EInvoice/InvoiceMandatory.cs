using System.Globalization;
using System.Text.RegularExpressions;

namespace PdfKnead.EInvoice;

/// <summary>Whether a mandatory detail stands in the invoice.</summary>
public enum MandatoryState
{
    /// <summary>It stands in the XML.</summary>
    Present,

    /// <summary>It, or a part of it, is missing.</summary>
    Missing,

    /// <summary>One invoice cannot tell (a running number; a note only some services need).</summary>
    NotCheckable,

    /// <summary>Not needed here (an invoice of small amount, not a self-billed one …).</summary>
    NotNeeded,
}

/// <summary>A mandatory detail of § 14 Abs. 4 UStG (or § 14a) and how it stands in the invoice.</summary>
/// <param name="Key">What it is: „seller“, „buyer“, „taxId“, „date“, „number“, „items“, „delivery“, „amounts“, „taxes“, „retention“,
/// „selfBilled“, „reverseCharge“, „intraCommunity“ (the display names it).</param>
/// <param name="Law">The provision (§ 14 Abs. 4 Nr. 1 UStG …).</param>
/// <param name="State">Whether it stands in the XML.</param>
/// <param name="Missing">The parts missing, by their terms (BT-35, BT-129 …); empty if none.</param>
public sealed record MandatoryItem(string Key, string Law, MandatoryState State, IReadOnlyList<string> Missing);

/// <summary>
/// The mandatory details of an invoice (KONZEPT.md, section 5.24, „Hilfen für den Einkäufer“, E2; concept approved by the user 15.11.2026):
/// § 14 Abs. 4 Satz 1 Nr. 1 to 10 UStG, the cases of § 14a (reverse charge, intra-community supply) and the small amounts of § 33 UStDV,
/// each with the terms of EN 16931 it stands in. It says whether a detail stands in the XML — not whether it is right, and nothing for the
/// tax office (an aid, never „gültig“). Only the invoice itself is read (P1).
/// </summary>
public static class InvoiceMandatory
{
    /// <summary>The highest total of an invoice of small amount (§ 33 UStDV).</summary>
    public const decimal SmallAmount = 250m;

    /// <summary>
    /// Whether the list applies: not for profiles that are no e-invoice in Germany (MINIMUM, BASIC WL, ZUGFeRD 1) — there the visible part is
    /// the invoice and holds the details (analysis of the collections, 15.11.2026: their XML lacks the lines by design).
    /// </summary>
    public static bool Applies(InvoiceKind kind) => kind.EInvoiceInGermany != false;

    /// <summary>The details and how they stand.</summary>
    public static IReadOnlyList<MandatoryItem> Check(InvoiceData data)
    {
        var all = data.Root.AllFields().Where(f => !string.IsNullOrWhiteSpace(f.Text)).ToList();
        bool Has(string id) => all.Any(f => f.Id == id);
        string? Text(string id) => all.FirstOrDefault(f => f.Id == id)?.Text?.Trim();
        var vat = data.Root.AllGroups().Where(g => g.Id == "BG-23").ToList();
        var categories = vat.Select(g => g.Text("BT-118")?.Trim().ToUpperInvariant()).OfType<string>().ToHashSet();
        // § 33 UStDV: up to 250 € in all, not for distance sales, intra-community supplies and reverse charge (§§ 3c, 6a, 13b UStG)
        var small = decimal.TryParse(Text("BT-112"), NumberStyles.Number, CultureInfo.InvariantCulture, out var total) && total <= SmallAmount
                    && (Text("BT-5") ?? "EUR") == "EUR" && !categories.Overlaps(["K", "AE"]);
        var items = new List<MandatoryItem>();
        MandatoryItem Item(string key, string law, params (string Id, bool Present)[] parts)
        {
            var missing = parts.Where(p => !p.Present).Select(p => p.Id).ToList();
            return new MandatoryItem(key, law, missing.Count == 0 ? MandatoryState.Present : MandatoryState.Missing, missing);
        }

        // Nr. 1: name and full address of the seller and of the buyer (the buyer's not needed for a small amount)
        items.Add(Item("seller", "§ 14 Abs. 4 Nr. 1 UStG", ("BT-27", Has("BT-27")), ("BT-35", Has("BT-35")), ("BT-37", Has("BT-37")), ("BT-40", Has("BT-40"))));
        items.Add(small ? new MandatoryItem("buyer", "§ 33 UStDV", MandatoryState.NotNeeded, [])
            : Item("buyer", "§ 14 Abs. 4 Nr. 1 UStG", ("BT-44", Has("BT-44")), ("BT-50", Has("BT-50")), ("BT-52", Has("BT-52")), ("BT-55", Has("BT-55"))));
        // Nr. 2: the seller's tax number or VAT identification number (of a tax representative too)
        items.Add(small ? new MandatoryItem("taxId", "§ 33 UStDV", MandatoryState.NotNeeded, [])
            : Item("taxId", "§ 14 Abs. 4 Nr. 2 UStG", ("BT-31/BT-32", Has("BT-31") || Has("BT-32") || Has("BT-63"))));
        // Nr. 3 and 4: date of issue; invoice number (whether it runs on, one invoice cannot tell)
        items.Add(Item("date", "§ 14 Abs. 4 Nr. 3 UStG", ("BT-2", Has("BT-2"))));
        items.Add(small ? new MandatoryItem("number", "§ 33 UStDV", MandatoryState.NotNeeded, [])
            : Has("BT-1") ? new MandatoryItem("number", "§ 14 Abs. 4 Nr. 4 UStG", MandatoryState.Present, []) : Item("number", "§ 14 Abs. 4 Nr. 4 UStG", ("BT-1", false)));
        // Nr. 5: quantity and kind of every line
        var lines = data.Root.AllGroups().Where(g => g.Id == "BG-25").ToList();
        var lineParts = new List<(string, bool)>
        {
            ("BG-25", lines.Count > 0),
            ("BT-153", lines.All(l => l.AllFields().Any(f => f.Id == "BT-153" && !string.IsNullOrWhiteSpace(f.Text)))),
            ("BT-129", lines.All(l => l.AllFields().Any(f => f.Id == "BT-129" && !string.IsNullOrWhiteSpace(f.Text)))),
        };
        items.Add(Item("items", small ? "§ 33 UStDV" : "§ 14 Abs. 4 Nr. 5 UStG", [.. lineParts]));
        // Nr. 6: when the supply was made — a date of delivery, a period, or one on every line (not needed for a small amount). The law asks for
        // no field of its own: a date in a note or a line's description („an folgenden Terminen: 11.09.2018 …“) may be it — then not checkable,
        // not missing (found in the collections, 16.11.2026: a physiotherapist's invoice names its dates so); with dates on some lines only,
        // how many lack one
        var dated = lines.Count(l => l.AllFields().Any(f => f.Id is "BT-134" or "BT-135" && !string.IsNullOrWhiteSpace(f.Text)));
        var delivery = Has("BT-72") || Has("BT-73") || Has("BT-74") || lines.Count > 0 && dated == lines.Count;
        if (small) items.Add(new MandatoryItem("delivery", "§ 33 UStDV", MandatoryState.NotNeeded, []));
        else if (delivery) items.Add(new MandatoryItem("delivery", "§ 14 Abs. 4 Nr. 6 UStG", MandatoryState.Present, []));
        else if (all.Any(f => f.Id is "BT-22" or "BT-127" or "BT-154" && HasDate(f.Text!)))
            items.Add(new MandatoryItem("delivery", "§ 14 Abs. 4 Nr. 6 UStG", MandatoryState.NotCheckable, ["dateInText"]));
        else
            items.Add(new MandatoryItem("delivery", "§ 14 Abs. 4 Nr. 6 UStG", MandatoryState.Missing,
                dated > 0 ? [$"BG-26:{lines.Count - dated}/{lines.Count}"] : ["BT-72/BG-14"]));
        // Nr. 7: the amount broken down by rate and exemption
        items.Add(Item("amounts", small ? "§ 33 UStDV" : "§ 14 Abs. 4 Nr. 7 UStG",
            ("BG-23", vat.Count > 0), ("BT-116", vat.Count > 0 && vat.All(g => !string.IsNullOrWhiteSpace(g.Text("BT-116"))))));
        // Nr. 8: rate and tax amount, or a note of the exemption
        var exempt = new[] { "E", "AE", "K", "G", "O" };
        var taxes = vat.Count > 0 && vat.All(g => exempt.Contains(g.Text("BT-118")?.Trim().ToUpperInvariant())
            ? !string.IsNullOrWhiteSpace(g.Text("BT-120")) || !string.IsNullOrWhiteSpace(g.Text("BT-121"))
            : !string.IsNullOrWhiteSpace(g.Text("BT-119")) && !string.IsNullOrWhiteSpace(g.Text("BT-117")));
        items.Add(Item("taxes", small ? "§ 33 UStDV" : "§ 14 Abs. 4 Nr. 8 UStG", ("BT-119/BT-117/BT-120", taxes)));
        // Nr. 9: the note on keeping the invoice — only for work on land for a private person (§ 14b Abs. 1 Satz 5): not checkable
        items.Add(new MandatoryItem("retention", "§ 14 Abs. 4 Nr. 9 UStG", MandatoryState.NotCheckable, []));
        // Nr. 10: „Gutschrift“ on a self-billed invoice (type 389). The word in a note counts; the code alone — whether it replaces the word in an
        // e-invoice is not settled (UStAE 14.3 asks for the word; no statement of the administration on the code found, 15.11.2026): not checkable
        if (Text("BT-3") != "389") items.Add(new MandatoryItem("selfBilled", "§ 14 Abs. 4 Nr. 10 UStG", MandatoryState.NotNeeded, []));
        else if (all.Any(f => f.Id is "BT-22" or "BT-21" && f.Text!.Contains("Gutschrift", StringComparison.OrdinalIgnoreCase)))
            items.Add(new MandatoryItem("selfBilled", "§ 14 Abs. 4 Nr. 10 UStG", MandatoryState.Present, []));
        else items.Add(new MandatoryItem("selfBilled", "§ 14 Abs. 4 Nr. 10 UStG", MandatoryState.NotCheckable, ["code389"]));
        // § 14a: reverse charge — the note „Steuerschuldnerschaft des Leistungsempfängers“ (as the exemption reason) and the buyer's VAT ID
        if (categories.Contains("AE"))
            items.Add(Item("reverseCharge", "§ 14a Abs. 5 UStG", ("BT-120/BT-121", vat.Where(g => g.Text("BT-118")?.Trim().ToUpperInvariant() == "AE")
                .All(g => !string.IsNullOrWhiteSpace(g.Text("BT-120")) || !string.IsNullOrWhiteSpace(g.Text("BT-121"))))));
        // § 14a: intra-community supply — the VAT IDs of both
        if (categories.Contains("K"))
            items.Add(Item("intraCommunity", "§ 14a Abs. 3 UStG", ("BT-31", Has("BT-31")), ("BT-48", Has("BT-48"))));
        return items;
    }

    /// <summary>
    /// A date in a text: day.month.year, year-month-day, day/month/year, or a month named with its year (the calendar month suffices as the time
    /// of the supply, § 31 Abs. 4 UStDV) — German, English and French names.
    /// </summary>
    private static readonly Regex DateInText = new(
        @"\b\d{1,2}\.\s?\d{1,2}\.\s?(?:\d{4}|\d{2})\b|\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2}/\d{1,2}/(?:\d{4}|\d{2})\b|" +
        @"\b(?:januar|jänner|februar|märz|april|mai|juni|juli|august|september|oktober|november|dezember|january|february|march|may|june|july|" +
        @"october|december|janvier|février|mars|avril|juin|juillet|août|septembre|octobre|novembre|décembre)\s+\d{4}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Whether a text of the XML holds a date (a text too long or too slow to search counts as none).</summary>
    internal static bool HasDate(string text)
    {
        try
        {
            return DateInText.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Whether the invoice is one of small amount (§ 33 UStDV): the display says so above the list.</summary>
    public static bool IsSmallAmount(IReadOnlyList<MandatoryItem> items) => items.Any(i => i.Law == "§ 33 UStDV");
}
