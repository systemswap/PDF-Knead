using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>Kind of format of a text field.</summary>
public enum FieldFormatKind
{
    /// <summary>No format.</summary>
    None,
    /// <summary>Number, optionally with currency (<c>AFNumber_Format</c>).</summary>
    Number,
    /// <summary>Percentage (<c>AFPercent_Format</c>).</summary>
    Percent,
    /// <summary>Date (<c>AFDate_Format</c>, <c>AFDate_FormatEx</c>).</summary>
    Date,
    /// <summary>Time (<c>AFTime_Format</c>, <c>AFTime_FormatEx</c>).</summary>
    Time,
    /// <summary>Special format such as a ZIP code or phone number (<c>AFSpecial_Format</c>).</summary>
    Special,
}

/// <summary>
/// Format of a text field, taken from the standard form-script calls in its format script (/AA /F), e.g.
/// <c>AFDate_FormatEx("dd.mm.yyyy")</c> or <c>AFNumber_Format(2, 2, 0, 0, " €", false)</c>. The script is never executed:
/// only this one known call is read as text and reproduced here (as other readers without scripting do as well).
/// </summary>
/// <param name="Kind">Kind of format.</param>
/// <param name="Decimals">Number of decimal places (0 … 10).</param>
/// <param name="Separators">Separator style (sepStyle of AFNumber_Format): 0 "1,234.56", 1 "1234.56", 2 "1.234,56", 3 "1234,56", 4 "1'234.56".</param>
/// <param name="Negative">Style for negative numbers (negStyle of AFNumber_Format); 2 and 3 show them in parentheses, all others with a minus sign.</param>
/// <param name="Currency">Currency symbol including any space; empty for none.</param>
/// <param name="CurrencyFirst">Whether the currency symbol precedes the number.</param>
/// <param name="Pattern">Date or time pattern in form-script notation (AFDate_FormatEx syntax, e.g. "dd.mm.yyyy").</param>
/// <param name="Special">Special format: 0 = ZIP code (5 digits), 1 = ZIP+4 code (9 digits), 2 = phone number (10 digits), 3 = social security number (9 digits).</param>
public sealed partial record FieldFormat(FieldFormatKind Kind, int Decimals = 0, int Separators = 0, int Negative = 0, string Currency = "",
    bool CurrencyFirst = false, string Pattern = "", int Special = 0)
{
    /// <summary>No format.</summary>
    public static readonly FieldFormat None = new(FieldFormatKind.None);

    private static readonly PdfName AA = PdfName.Get("AA"), F = PdfName.Get("F"), JS = PdfName.Get("JS");

    // Short formats of AFDate_Format(n) and AFTime_Format(n) (from the JavaScript reference of the form functions)
    private static readonly string[] DateFormats =
        ["m/d", "m/d/yy", "mm/dd/yy", "mm/yy", "d-mmm", "d-mmm-yy", "dd-mmm-yy", "yy-mm-dd", "mmm-yy", "mmmm-yy", "mmm d, yyyy", "mmmm d, yyyy", "m/d/yy h:MM tt", "m/d/yy HH:MM"];
    private static readonly string[] TimeFormats = ["HH:MM", "h:MM tt", "HH:MM:ss", "h:MM:ss tt"];

    // Exactly the one call only (anchored): with anything before or after it, it is a custom script that stays unchanged on
    // saving — otherwise "AFNumber_Format(…); customCode()" would count as standard and the addition would be lost
    [GeneratedRegex(@"^\s*AFNumber_Format\(\s*(-?\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*\d+\s*,\s*""((?:[^""\\]|\\.)*)""\s*,\s*(true|false)\s*\)\s*;?\s*$")]
    private static partial Regex NumberCall();

    [GeneratedRegex(@"^\s*AFPercent_Format\(\s*(-?\d+)\s*,\s*(\d+)\s*(?:,\s*\d+\s*)?\)\s*;?\s*$")]
    private static partial Regex PercentCall();

    [GeneratedRegex(@"^\s*AF(Date|Time)_FormatEx\(\s*""((?:[^""\\]|\\.)*)""\s*\)\s*;?\s*$")]
    private static partial Regex PatternCall();

    [GeneratedRegex(@"^\s*AF(Date|Time)_Format\(\s*(\d+)\s*\)\s*;?\s*$")]
    private static partial Regex IndexCall();

    [GeneratedRegex(@"^\s*AFSpecial_Format\(\s*(\d)\s*\)\s*;?\s*$")]
    private static partial Regex SpecialCall();

    /// <summary>Gets the format of a field (script on the widget, otherwise on the field).</summary>
    /// <param name="field">The form field.</param>
    /// <returns>The recognised format; <see cref="None"/> if none is recognised.</returns>
    public static FieldFormat Of(FormField field)
    {
        var script = Script(field.Widget) ?? Script(field.Field);
        return script is null ? None : Parse(script);
    }

    private static string? Script(PdfDictionary dict)
    {
        if (dict.GetDictionary(AA)?.GetDictionary(F) is not { } action) return null;
        return action.Get(JS) is PdfString s ? s.ToText() : null;   // scripts stored as streams (rare) are not read
    }

    /// <summary>Number from the script, saturated (crafted files: "AFNumber_Format(99999999999, …)" would otherwise overflow).</summary>
    private static int Int(string digits) =>
        int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : digits.StartsWith('-') ? int.MinValue : int.MaxValue;

    /// <summary>Recognises the format from the text of a format script.</summary>
    /// <param name="script">The script text; scripts over 100 000 characters are ignored.</param>
    /// <returns>The format; <see cref="None"/> if the script is not exactly one known standard call.</returns>
    public static FieldFormat Parse(string script)
    {
        if (script.Length > 100_000) return None;
        if (NumberCall().Match(script) is { Success: true } n)
            return new FieldFormat(FieldFormatKind.Number, Math.Clamp(Int(n.Groups[1].Value), 0, 10),
                Int(n.Groups[2].Value), Int(n.Groups[3].Value),
                FieldScripts.Unescape(n.Groups[4].Value), n.Groups[5].Value == "true");
        if (PercentCall().Match(script) is { Success: true } p)
            return new FieldFormat(FieldFormatKind.Percent, Math.Clamp(Int(p.Groups[1].Value), 0, 10),
                Int(p.Groups[2].Value));
        if (PatternCall().Match(script) is { Success: true } d)
            return new FieldFormat(d.Groups[1].Value == "Date" ? FieldFormatKind.Date : FieldFormatKind.Time, Pattern: FieldScripts.Unescape(d.Groups[2].Value));
        if (IndexCall().Match(script) is { Success: true } i)
        {
            var list = i.Groups[1].Value == "Date" ? DateFormats : TimeFormats;
            var k = Int(i.Groups[2].Value);
            if (k < list.Length) return new FieldFormat(i.Groups[1].Value == "Date" ? FieldFormatKind.Date : FieldFormatKind.Time, Pattern: list[k]);
        }
        if (SpecialCall().Match(script) is { Success: true } s)
            return new FieldFormat(FieldFormatKind.Special, Special: s.Groups[1].Value[0] - '0');
        return None;
    }

    /// <summary>
    /// Converts input into the stored value: numbers without thousands separators and with a decimal point (as common PDF editors store
    /// them in /V), date and time in the field's format, special formats as digits only.
    /// </summary>
    /// <param name="input">The text entered.</param>
    /// <returns>The value to store; empty for empty input; <see langword="null"/> if the input does not match the format.</returns>
    public string? Normalize(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return "";
        switch (Kind)
        {
            case FieldFormatKind.Number or FieldFormatKind.Percent:
                return ParseNumber(input, Separators) is { } number
                    ? (Kind == FieldFormatKind.Percent && input.EndsWith('%') ? number / 100 : number).ToString("0.##########", CultureInfo.InvariantCulture)
                    : null;
            case FieldFormatKind.Date:
                return ParseDate(input) is { } date ? FormatDate(date, Pattern) : null;
            case FieldFormatKind.Time:
                return ParseDate(input) is { } time ? FormatDate(time, Pattern) : null;
            case FieldFormatKind.Special:
                var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
                return digits.Length == (Special switch { 0 => 5, 1 => 9, 2 => 10, 3 => 9, _ => digits.Length }) ? digits : null;
            default:
                return input;
        }
    }

    /// <summary>Formats a stored value as the field shows it (thousands separators, currency, percent, special format).</summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The display text; the value itself if it does not match the format.</returns>
    public string Display(string value)
    {
        if (value.Length == 0) return value;
        switch (Kind)
        {
            case FieldFormatKind.Number when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                var text = FormatNumber(Math.Abs(number), Decimals, Separators);
                text = Currency.Length == 0 ? text : CurrencyFirst ? Currency + text : text + Currency;
                return number >= 0 || Math.Round(Math.Abs(number), Decimals) == 0 ? text : Negative is 2 or 3 ? $"({text})" : "-" + text;
            case FieldFormatKind.Percent when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var share):
                return (share < 0 ? "-" : "") + FormatNumber(Math.Abs(share * 100), Decimals, Separators) + "%";
            case FieldFormatKind.Special:
                return Special switch
                {
                    1 when value.Length == 9 => $"{value[..5]}-{value[5..]}",
                    2 when value.Length == 10 => $"({value[..3]}) {value[3..6]}-{value[6..]}",
                    3 when value.Length == 9 => $"{value[..3]}-{value[3..5]}-{value[5..]}",
                    _ => value,
                };
            default:
                return value;
        }
    }

    /// <summary>Reads a date or time from the stored value (for the calendar).</summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The date; <see langword="null"/> if the value is none.</returns>
    public DateTime? DateOf(string value) => ParseDate(value, Pattern);

    /// <summary>Formats a date in the field's format.</summary>
    /// <param name="date">The date.</param>
    /// <returns>The formatted date; with an empty pattern the invariant short date.</returns>
    public string Format(DateTime date) => FormatDate(date, Pattern);

    // Separators by sepStyle: 0 "1,234.56", 1 "1234.56", 2 "1.234,56", 3 "1234,56", 4 "1'234.56"
    private static (string Group, string Decimal) Separator(int style) => style switch
    {
        0 => (",", "."),
        1 => ("", "."),
        2 => (".", ","),
        3 => ("", ","),
        4 => ("'", "."),
        _ => (",", "."),
    };

    private static string FormatNumber(double value, int decimals, int style)
    {
        decimals = Math.Clamp(decimals, 0, 10);   // already limited when read from files; Math.Round throws outside 0…15 (found by fuzzing)
        var (group, dec) = Separator(style);
        var text = Math.Round(value, decimals).ToString("F" + decimals, CultureInfo.InvariantCulture);
        var point = text.IndexOf('.');
        var whole = point < 0 ? text : text[..point];
        var fraction = point < 0 ? "" : text[(point + 1)..];
        var sb = new StringBuilder();
        for (var i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0) sb.Append(group);
            sb.Append(whole[i]);
        }
        if (decimals > 0) sb.Append(dec).Append(fraction);
        return sb.ToString();
    }

    /// <summary>Number from input: the decimal separator is the last comma or point, depending on the format; currency and spaces are dropped.</summary>
    private static double? ParseNumber(string input, int style)
    {
        var negative = input.Contains('-') || input.StartsWith('(');
        var clean = new string(input.Where(c => char.IsAsciiDigit(c) || c is '.' or ',').ToArray());
        if (clean.Length == 0) return null;
        var decimalSeparator = Separator(style).Decimal[0];
        // Only one kind of separator: it is the decimal separator if it is the format's own, or occurs once and is not followed by exactly three digits (otherwise thousands)
        var lastComma = clean.LastIndexOf(',');
        var lastPoint = clean.LastIndexOf('.');
        int decimalAt;
        if (lastComma >= 0 && lastPoint >= 0) decimalAt = Math.Max(lastComma, lastPoint);
        else if (lastComma < 0 && lastPoint < 0) decimalAt = -1;
        else
        {
            var at = Math.Max(lastComma, lastPoint);
            var only = clean[at];
            var digitsAfter = clean.Length - at - 1;
            var occurrences = clean.Count(c => c == only);
            decimalAt = only == decimalSeparator || (occurrences == 1 && digitsAfter != 3) ? at : -1;
        }
        var number = new StringBuilder();
        for (var i = 0; i < clean.Length; i++)
            if (char.IsAsciiDigit(clean[i])) number.Append(clean[i]);
            else if (i == decimalAt) number.Append('.');
        return double.TryParse(number.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? negative ? -value : value
            : null;
    }

    /// <summary>Date from input: in the field's format, otherwise common notations (17.10.2026, 2026-10-17, 10/17/2026).</summary>
    private DateTime? ParseDate(string input, string? pattern = null)
    {
        input = input.Trim();
        if (pattern is { Length: > 0 } && ParseWithPattern(input, pattern) is { } exact) return exact;
        if (Pattern.Length > 0 && ParseWithPattern(input, Pattern) is { } own) return own;
        string[] usual = ["d.M.yyyy", "d.M.yy", "yyyy-M-d", "M/d/yyyy", "M/d/yy", "d.M.yyyy H:mm", "H:mm", "H:mm:ss", "h:mm tt"];
        return DateTime.TryParseExact(input, usual, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null;
    }

    /// <summary>Converts a form-script pattern (AFDate_FormatEx syntax: d, dd, m, mm, mmm, mmmm, yy, yyyy, H, HH, h, hh, M, MM, ss, tt) into a .NET pattern.</summary>
    private static string NetPattern(string pattern)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pattern.Length;)
        {
            var c = pattern[i];
            var run = 1;
            while (i + run < pattern.Length && pattern[i + run] == c) run++;
            var token = pattern.Substring(i, run);
            sb.Append(c switch
            {
                'd' => run >= 2 ? "dd" : "d",
                'm' => run switch { 1 => "M", 2 => "MM", 3 => "MMM", _ => "MMMM" },
                'y' => run >= 4 ? "yyyy" : "yy",
                'H' => run >= 2 ? "HH" : "H",
                'h' => run >= 2 ? "hh" : "h",
                'M' => run >= 2 ? "mm" : "m",
                's' => run >= 2 ? "ss" : "s",
                't' => "tt",
                _ when char.IsLetter(c) => "'" + token + "'",
                _ => token.Replace("'", "\\'"),
            });
            i += run;
        }
        return sb.ToString();
    }

    private static DateTime? ParseWithPattern(string input, string pattern) =>
        DateTime.TryParseExact(input, NetPattern(pattern), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null;

    private static string FormatDate(DateTime date, string pattern) =>
        pattern.Length == 0 ? date.ToString("d", CultureInfo.InvariantCulture) : date.ToString(NetPattern(pattern), CultureInfo.InvariantCulture);
}
