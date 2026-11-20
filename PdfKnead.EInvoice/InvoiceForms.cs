using System.Globalization;
using System.Text;

namespace PdfKnead.EInvoice;

/// <summary>
/// A search form of a value: its prepared text, whether it is the value rounded, whether it is one word of a text, whether it is a text
/// without its spaces (searched in the page text without spaces).
/// </summary>
internal readonly record struct InvoiceForm(string Text, bool Rounded = false, bool Word = false, bool Compact = false);

/// <summary>
/// How one value of the invoice is searched in stage 3: its forms, whether it is weak (counts only beside a strong value of its group),
/// why it cannot be checked at all, its number (for the sign) and how many words a text has.
/// </summary>
internal sealed record InvoiceValueSearch(InvoiceField Field, IReadOnlyList<InvoiceForm> Forms, bool Weak, string? NotCheckable, decimal? Number, int Words);

/// <summary>
/// The usual forms of a value (KONZEPT.md, section 5.24, „Weg für Stufe 3“): numbers with decimal comma or point and any thousands
/// separator, a value with more than two decimals also rounded; dates in numbers and with month names in German, English and French; an
/// IBAN with and without groups; a VAT identifier with and without spaces; a text as a whole and word by word. Codes have no form on the
/// page and attachments none in the text — they cannot be checked.
/// </summary>
internal static class InvoiceForms
{
    /// <summary>At most so many forms per value.</summary>
    internal const int MaxForms = 48;

    /// <summary>At most so many words of a text are searched one by one.</summary>
    internal const int MaxWords = 50;

    /// <summary>A text longer than this is searched only word by word.</summary>
    internal const int MaxWholeText = 300;

    /// <summary>Terms that hold an identifier of a specification or a process — never printed.</summary>
    private static readonly HashSet<string> NotShown = new(StringComparer.Ordinal) { "BT-23", "BT-24" };

    /// <summary>VAT identifiers and tax registration numbers: compared with and without spaces.</summary>
    private static readonly HashSet<string> TaxIds = new(StringComparer.Ordinal) { "BT-31", "BT-32", "BT-48", "BT-63" };

    public static InvoiceValueSearch Of(InvoiceField field)
    {
        if (field.Node is { IsAttribute: false, Element.Binary: not null }) return Unchecked(field, Tr.T("Anhang"));
        var text = field.Text?.Trim() ?? "";
        if (text.Length == 0) return Unchecked(field, Tr.T("ohne Wert"));
        if (NotShown.Contains(field.Id) || InvoiceCodes.ListOf(field.Id) is not null) return Unchecked(field, Tr.T("Code ohne Sichtform"));
        if (InvoiceFormat.Dates.Contains(field.Id) && InvoiceFormat.Date(text) is { } date) return new(field, Distinct(DateForms(date)), false, null, null, 0);
        var amount = InvoiceFormat.Amounts.Contains(field.Id);
        if ((amount || InvoiceFormat.Quantities.Contains(field.Id) || InvoiceFormat.Percentages.Contains(field.Id)) && InvoiceFormat.Number(text) is { } number
            && NumberForms(text, amount) is { Count: > 0 } numberForms)
            return new(field, Distinct(numberForms), WeakNumber(text), null, number, 0);
        if (InvoiceFormat.Accounts.Contains(field.Id)) return new(field, Distinct(AccountForms(text)), false, null, null, 0);
        if (TaxIds.Contains(field.Id)) return new(field, Distinct(TaxIdForms(text)), text.Replace(" ", "", StringComparison.Ordinal).Length <= 2, null, null, 0);
        return TextSearch(field, text);
    }

    private static InvoiceValueSearch Unchecked(InvoiceField field, string why) => new(field, [], false, why, null, 0);

    private static List<InvoiceForm> Distinct(IEnumerable<InvoiceForm> forms)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<InvoiceForm>();
        foreach (var f in forms)
        {
            var prepared = f.Compact ? InvoiceSearchText.Prepare(f.Text).Replace(" ", "", StringComparison.Ordinal) : InvoiceSearchText.Prepare(f.Text);
            if (prepared.Length == 0 || !seen.Add((f.Compact ? "c:" : "") + prepared)) continue;
            list.Add(f with { Text = prepared });
            if (list.Count == MaxForms) break;
        }
        return list;
    }

    // ------------------------------------------------------------ numbers

    /// <summary>
    /// Whether a number is weak — found everywhere, so it counts only beside a strong value of its group: zero, or a whole number of one or
    /// two digits („1“, „19“, „13,00“). A number with cents („7,90“) is specific enough on its own.
    /// </summary>
    internal static bool WeakNumber(string text)
    {
        var (integer, fraction) = Parts(text);
        var whole = integer.TrimStart('0');
        var cents = fraction.TrimEnd('0');
        return whole.Length + cents.Length == 0 || cents.Length == 0 && whole.Length <= 2;
    }

    private static (string Integer, string Fraction) Parts(string text)
    {
        var t = text.Trim().TrimStart('+', '-');
        var dot = t.IndexOf('.');
        return dot < 0 ? (t, "") : (t[..dot], t[(dot + 1)..]);
    }

    /// <summary>
    /// The forms of a number as written in the XML (always without sign — the sign is checked beside the hit): amounts with at least two
    /// decimals, quantities and percentages also without trailing zeros; more than two decimals also rounded to two.
    /// </summary>
    internal static List<InvoiceForm> NumberForms(string text, bool amount)
    {
        var (integer, fraction) = Parts(text);
        if (!integer.All(char.IsAsciiDigit) || !fraction.All(char.IsAsciiDigit) || integer.Length + fraction.Length == 0) return [];
        integer = integer.TrimStart('0');
        if (integer.Length == 0) integer = "0";
        var trimmed = fraction.TrimEnd('0');
        var variants = new List<(string Integer, string Fraction, bool Rounded)>();
        if (amount)
        {
            if (trimmed.Length <= 2) variants.Add((integer, trimmed.PadRight(2, '0'), false));
            else variants.Add((integer, trimmed, false));
        }
        else
        {
            variants.Add((integer, trimmed, false));
            if (trimmed.Length < 2) variants.Add((integer, trimmed.PadRight(2, '0'), false));
        }
        if (fraction.Length > Math.Max(trimmed.Length, 2)) variants.Add((integer, fraction, false));   // as written: „2.0000“
        if (trimmed.Length > 2 && InvoiceFormat.Number(text) is { } value)
        {
            var rounded = Math.Round(Math.Abs(value), 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
            var (ri, rf) = Parts(rounded);
            variants.Add((ri, rf, true));
        }
        var forms = new List<InvoiceForm>();
        foreach (var (i, f, rounded) in variants)
            foreach (var dec in new[] { ',', '.' })
                foreach (var group in new[] { "", ".", ",", " ", "'" })
                {
                    if (group.Length > 0 && (i.Length <= 3 || group[0] == dec)) continue;
                    var whole = Grouped(i, group);
                    forms.Add(new(f.Length > 0 ? whole + dec + f : whole, rounded));
                    if (amount && f == "00" && !rounded)
                    {
                        forms.Add(new(whole + dec + "-"));
                        forms.Add(new(whole + dec + "–"));
                    }
                }
        return forms;
    }

    private static string Grouped(string digits, string separator)
    {
        if (separator.Length == 0 || digits.Length <= 3) return digits;
        var sb = new StringBuilder();
        var first = digits.Length % 3;
        if (first > 0) sb.Append(digits, 0, first);
        for (var k = first; k < digits.Length; k += 3)
        {
            if (sb.Length > 0) sb.Append(separator);
            sb.Append(digits, k, 3);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ dates

    private static readonly string[] German = ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"];
    private static readonly string[] GermanShort = ["Jan.", "Feb.", "März", "Apr.", "Mai", "Juni", "Juli", "Aug.", "Sep.", "Okt.", "Nov.", "Dez."];
    private static readonly string[] English = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    private static readonly string[] EnglishShort = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] French = ["janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre"];

    internal static IEnumerable<InvoiceForm> DateForms(DateOnly d)
    {
        string D2(int v) => v.ToString("00", CultureInfo.InvariantCulture);
        string N(int v) => v.ToString(CultureInfo.InvariantCulture);
        var (day, month, year) = (d.Day, d.Month, d.Year);
        var m = month - 1;
        yield return new($"{D2(day)}.{D2(month)}.{N(year)}");
        yield return new($"{N(day)}.{N(month)}.{N(year)}");
        yield return new($"{D2(day)}.{D2(month)}.{D2(year % 100)}");
        yield return new($"{N(year)}-{D2(month)}-{D2(day)}");
        yield return new($"{D2(day)}/{D2(month)}/{N(year)}");
        yield return new($"{N(day)}/{N(month)}/{N(year)}");
        yield return new($"{D2(day)}-{D2(month)}-{N(year)}");
        yield return new($"{N(year)}/{D2(month)}/{D2(day)}");
        yield return new($"{N(day)}. {German[m]} {N(year)}");
        yield return new($"{D2(day)}. {German[m]} {N(year)}");
        yield return new($"{N(day)}. {GermanShort[m]} {N(year)}");
        yield return new($"{N(day)} {English[m]} {N(year)}");
        yield return new($"{English[m]} {N(day)}, {N(year)}");
        yield return new($"{N(day)} {EnglishShort[m]} {N(year)}");
        yield return new($"{EnglishShort[m]} {N(day)}, {N(year)}");
        yield return new($"{(day == 1 ? "1er" : N(day))} {French[m]} {N(year)}");
    }

    // ------------------------------------------------------------ identifiers

    /// <summary>An IBAN (two letters, two digits, letters and digits) compact and in groups of four; null if the value is no IBAN.</summary>
    private static List<InvoiceForm>? Iban(string text)
    {
        var compact = text.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (compact.Length < 5 || !char.IsAsciiLetter(compact[0]) || !char.IsAsciiLetter(compact[1]) || !char.IsAsciiDigit(compact[2])
            || !char.IsAsciiDigit(compact[3]) || !compact.All(char.IsAsciiLetterOrDigit)) return null;
        return [new(compact), new(string.Join(' ', compact.Chunk(4).Select(c => new string(c))))];
    }

    /// <summary>
    /// An account: an IBAN compact and in groups of four, another account number as written — and any of them also without its spaces against
    /// the page without its spaces, so that a grouping of its own is found („Kontonr. 1234 5678 90“, „DE88 20080000 0970375700“; found in the
    /// collections, 15.11.2026: the account number of an invoice stood visibly, grouped, and was reported as not visible). Not for short
    /// numbers (under six characters): they would be found anywhere.
    /// </summary>
    private static IEnumerable<InvoiceForm> AccountForms(string text)
    {
        if (Iban(text) is { } iban)
            foreach (var form in iban) yield return form;
        else yield return new(text);
        var compact = text.Replace(" ", "", StringComparison.Ordinal);
        if (compact.Length >= 6) yield return new(compact, Compact: true);
    }

    /// <summary>A VAT identifier as written, without spaces, with a space after the country and in groups of three after it.</summary>
    private static IEnumerable<InvoiceForm> TaxIdForms(string text)
    {
        yield return new(text);
        var compact = text.Replace(" ", "", StringComparison.Ordinal);
        yield return new(compact);
        if (compact.Length > 4 && char.IsAsciiLetter(compact[0]) && char.IsAsciiLetter(compact[1]))
        {
            var rest = compact[2..];
            yield return new(compact[..2] + " " + rest);
            yield return new(compact[..2] + " " + string.Join(' ', rest.Chunk(3).Select(c => new string(c))));
        }
    }

    // ------------------------------------------------------------ texts

    /// <summary>A text as a whole (up to <see cref="MaxWholeText"/> characters) and its words of two characters or more.</summary>
    private static InvoiceValueSearch TextSearch(InvoiceField field, string text)
    {
        var prepared = InvoiceSearchText.Prepare(text);
        var forms = new List<InvoiceForm>();
        if (prepared.Length <= MaxWholeText) forms.Add(new(prepared));
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length >= 2 && words.Count < MaxWords && seen.Add(sb.ToString())) words.Add(sb.ToString());
            sb.Clear();
        }
        foreach (var c in prepared)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else Flush();
        }
        Flush();
        // a text of more words also without its spaces (a break or a space around „/“ inside it), and word by word, counted when the
        // whole is not found; a text of one word is its whole form
        if (words.Count > 1)
        {
            if (prepared.Length <= MaxWholeText) forms.Add(new(prepared.Replace(" ", "", StringComparison.Ordinal), Compact: true));
            foreach (var w in words) forms.Add(new(w, Word: true));
        }
        return new(field, forms, prepared.Length <= 2, null, null, words.Count > 1 ? words.Count : 0);
    }
}
