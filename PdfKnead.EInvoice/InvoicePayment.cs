namespace PdfKnead.EInvoice;

/// <summary>
/// What a buyer needs to pay an invoice (KONZEPT.md, section 5.24, „Hilfen für den Einkäufer“, E1 Zahlungskasten; concept approved by the user
/// 15.11.2026): the payee, the accounts with whether their check digits fit, the amount due, the due date or terms, the reference and a
/// direct debit — the facts of the XML only, read from the invoice itself (P1: no supplier records, no earlier invoices). Whether they also
/// stand visibly in the PDF says the comparison (stage 3); this record says nothing about it.
/// </summary>
/// <param name="Payee">Who is paid: the payee (BT-59) or, without one, the seller (BT-27).</param>
/// <param name="Seller">The seller (BT-27).</param>
/// <param name="PayeeIsSeller">Whether the payee is the seller (no payee of its own, or the same name).</param>
/// <param name="Accounts">The accounts to pay to (BT-84) and whether the check digits of each fit (ISO 13616, MOD 97-10).</param>
/// <param name="Bic">The BIC (BT-86).</param>
/// <param name="Amount">The amount due for payment (BT-115).</param>
/// <param name="Due">The due date (BT-9).</param>
/// <param name="Terms">The payment terms (BT-20), where discounts are named.</param>
/// <param name="Reference">The remittance information (BT-83).</param>
/// <param name="DirectDebit">Paid by direct debit (a mandate, a debited account, or the means 49 or 59).</param>
/// <param name="Mandate">The mandate reference (BT-89).</param>
/// <param name="DebitedAccount">The account debited (BT-91).</param>
/// <param name="Net">The total without VAT (BT-109) — orders are mostly net: the buyer compares it first (decided on 15.11.2026).</param>
/// <param name="Vat">The VAT total (BT-110), booked apart as input tax.</param>
/// <param name="Gross">
/// The total with VAT (BT-112). It also stands for the amount due where that is missing — a ZUGFeRD 1 BASIC correction names none (found in
/// the collections, 15.11.2026: without it the box was missing).
/// </param>
/// <param name="Prepaid">The amount paid already (BT-113), only if not zero: then the amount due is less than the gross total.</param>
public sealed record InvoicePayment(InvoiceField? Payee, InvoiceField? Seller, bool PayeeIsSeller, IReadOnlyList<(InvoiceField Iban, bool Valid)> Accounts,
    InvoiceField? Bic, InvoiceField? Amount, InvoiceField? Due, InvoiceField? Terms, InvoiceField? Reference, bool DirectDebit, InvoiceField? Mandate,
    InvoiceField? DebitedAccount, InvoiceField? Net = null, InvoiceField? Vat = null, InvoiceField? Gross = null, InvoiceField? Prepaid = null)
{
    /// <summary>The payment facts of an invoice.</summary>
    public static InvoicePayment Of(InvoiceData data)
    {
        var all = data.Root.AllFields().Where(f => !string.IsNullOrWhiteSpace(f.Text)).ToList();
        InvoiceField? First(string id) => all.FirstOrDefault(f => f.Id == id);
        var seller = First("BT-27");
        var payee = First("BT-59");
        // BT-84 may hold an account number that is no IBAN (outside SEPA, ZUGFeRD 1): its check digits are not judged
        var accounts = all.Where(f => f.Id == "BT-84").Select(f => (f, !LooksLikeIban(f.Text!) || Core.Pdf.Text.NumberChecks.IbanValid(f.Text!))).ToList();
        var directDebit = all.Any(f => f.Id is "BT-89" or "BT-91") || all.Any(f => f.Id == "BT-81" && f.Text!.Trim() is "49" or "59");
        return new InvoicePayment(payee ?? seller, seller, payee is null || seller is null || SameName(payee.Text, seller.Text), accounts,
            First("BT-86"), First("BT-115"), First("BT-9"), First("BT-20"), First("BT-83"), directDebit, First("BT-89"), First("BT-91"),
            First("BT-109"), First("BT-110"), First("BT-112"), First("BT-113") is { } paid && !IsZero(paid.Text) ? paid : null);
    }

    /// <summary>
    /// What to check before paying (the panel and „erechnung --einkauf“ alike): check digits that fail; an account or the amount of the XML
    /// not found in the visible text of the PDF where the page has text (the pattern of invoice fraud). Not checked (cancelled, no readable
    /// text) is no issue. Only searched, never read out of the PDF (P2).
    /// </summary>
    public IReadOnlyList<(InvoicePaymentIssue Kind, InvoiceField Field)> Issues(InvoiceComparisonResult? comparison)
    {
        bool NotVisible(InvoiceField f) => comparison is { HasVisibleText: true } &&
                                           comparison.Matches.FirstOrDefault(m => ReferenceEquals(m.Field, f)) is { Status: InvoiceMatchStatus.NotFound, VisibleOutOfContext: false };
        var issues = new List<(InvoicePaymentIssue, InvoiceField)>();
        foreach (var (iban, valid) in Accounts)
        {
            if (!valid) issues.Add((InvoicePaymentIssue.CheckDigits, iban));
            if (NotVisible(iban)) issues.Add((InvoicePaymentIssue.AccountNotVisible, iban));
        }
        if (Amount is { } amount && SomethingDue && NotVisible(amount)) issues.Add((InvoicePaymentIssue.AmountNotVisible, amount));
        // no account in the XML, no direct debit, but something to pay: an IBAN only in the picture is no part of the e-invoice
        if (Accounts.Count == 0 && !DirectDebit && SomethingDue && (Amount ?? Gross) is { } anchor) issues.Add((InvoicePaymentIssue.NoAccount, anchor));
        return issues;
    }

    /// <summary>Whether an account identifier is built like an IBAN (two letters, two digits, letters or digits): only then are its check digits judged.</summary>
    public static bool LooksLikeIban(string text)
    {
        var plain = string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        return plain.Length is >= 15 and <= 34 && char.IsAsciiLetterUpper(plain[0]) && char.IsAsciiLetterUpper(plain[1])
               && char.IsAsciiDigit(plain[2]) && char.IsAsciiDigit(plain[3]) && plain.All(char.IsAsciiLetterOrDigit);
    }

    /// <summary>Whether something is to be paid: an amount due (or, without one, the gross total) above zero — a credit or a correction to zero asks for no account.</summary>
    private bool SomethingDue => (Amount ?? Gross)?.Text is { } due
        && (!decimal.TryParse(due, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) || value > 0);

    /// <summary>Whether an amount of the XML is zero („0“, „0.00“).</summary>
    private static bool IsZero(string? text) =>
        decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) && value == 0;

    /// <summary>Whether there is anything to show.</summary>
    public bool HasContent => Accounts.Count > 0 || Amount is not null || Gross is not null || Due is not null || Terms is not null || Reference is not null || DirectDebit;

    /// <summary>An IBAN in groups of four, as it is printed („DE02 1203 0000 0000 2020 51“).</summary>
    public static string Grouped(string iban)
    {
        var plain = string.Concat(iban.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        return string.Join(' ', Enumerable.Range(0, (plain.Length + 3) / 4).Select(i => plain.Substring(4 * i, Math.Min(4, plain.Length - 4 * i))));
    }

    /// <summary>Two names alike apart from case and spacing.</summary>
    private static bool SameName(string? a, string? b) => string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);

    private static string Normal(string? s) => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>What to check before paying an invoice.</summary>
public enum InvoicePaymentIssue
{
    /// <summary>The check digits of an IBAN fail.</summary>
    CheckDigits,

    /// <summary>An account of the XML does not stand visibly in the PDF.</summary>
    AccountNotVisible,

    /// <summary>The amount due of the XML does not stand visibly in the PDF.</summary>
    AmountNotVisible,

    /// <summary>
    /// The XML names no account (and no direct debit) though something is to be paid: an IBAN only in the picture is no part of the e-invoice
    /// (a warning, decided on 15.11.2026).
    /// </summary>
    NoAccount,
}
