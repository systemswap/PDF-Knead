using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// The standard form-script calls for format and calculation, written as common PDF editors write them. PDF Knead never executes
/// them, but many readers do: everything that goes into them (date patterns, currency symbols, field names — entered by
/// the user or taken from a crafted file) appears only as a safely escaped string literal, numbers only as clamped integers.
/// The calls are assembled from fixed building blocks, never from text.
/// </summary>
public static class FieldScripts
{
    private static readonly PdfName S = PdfName.Get("S"), JS = PdfName.Get("JS");

    /// <summary>
    /// Builds a JavaScript string literal in double quotes: only printable ASCII except <c>\ " ' &lt; &gt; &amp;</c> stays as it
    /// is, everything else (control characters, line breaks, U+2028/U+2029, non-ASCII) becomes <c>\uXXXX</c>, so that the literal
    /// can neither end early nor break a line, not even in an HTML or XFA context.
    /// </summary>
    /// <param name="text">The text to quote.</param>
    /// <returns>The literal including the quotes.</returns>
    public static string Quote(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            if (c is >= ' ' and <= '~' and not ('\\' or '"' or '\'' or '<' or '>' or '&')) sb.Append(c);
            else sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// Reads the content of a JavaScript string literal (without the quotes) by the rules of JavaScript, not .NET:
    /// <c>\n \t \xHH \uXXXX</c>; an unknown escape stands for the character itself (<c>"\q"</c> is "q"); an incomplete
    /// <c>\u12</c> stays as text. Never throws (Regex.Unescape throws on crafted scripts).
    /// </summary>
    /// <param name="literal">The literal's content without the quotes.</param>
    /// <returns>The decoded text.</returns>
    public static string Unescape(string literal)
    {
        if (!literal.Contains('\\')) return literal;
        var sb = new StringBuilder(literal.Length);
        for (var i = 0; i < literal.Length; i++)
        {
            var c = literal[i];
            if (c != '\\' || i + 1 >= literal.Length)
            {
                sb.Append(c);
                continue;
            }
            var e = literal[++i];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '0' when i + 1 >= literal.Length || !char.IsAsciiDigit(literal[i + 1]): sb.Append('\0'); break;
                case 'x' when Hex(literal, i + 1, 2) is { } x: sb.Append((char)x); i += 2; break;
                case 'u' when Hex(literal, i + 1, 4) is { } u: sb.Append((char)u); i += 4; break;
                case '\r' or '\n': break;   // line continuation
                default: sb.Append(e); break;
            }
        }
        return sb.ToString();

        static int? Hex(string s, int start, int count) =>
            start + count <= s.Length && int.TryParse(s.AsSpan(start, count), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Integer for a call, clamped.</summary>
    private static string Int(int value, int min, int max) => Math.Clamp(value, min, max).ToString(CultureInfo.InvariantCulture);

    /// <summary>Builds the format and keystroke scripts (/AA /F and /K) of a format.</summary>
    /// <param name="format">The field format.</param>
    /// <returns>Both scripts; both <see langword="null"/> without a format.</returns>
    public static (string? Format, string? Keystroke) ForFormat(FieldFormat format)
    {
        switch (format.Kind)
        {
            case FieldFormatKind.Number:
                var args = $"{Int(format.Decimals, 0, 10)}, {Int(format.Separators, 0, 4)}, {Int(format.Negative, 0, 3)}, 0, {Quote(format.Currency)}, " +
                           (format.CurrencyFirst ? "true" : "false");
                return ($"AFNumber_Format({args});", $"AFNumber_Keystroke({args});");
            case FieldFormatKind.Percent:
                var p = $"{Int(format.Decimals, 0, 10)}, {Int(format.Separators, 0, 4)}";
                return ($"AFPercent_Format({p});", $"AFPercent_Keystroke({p});");
            case FieldFormatKind.Date:
                return ($"AFDate_FormatEx({Quote(format.Pattern)});", $"AFDate_KeystrokeEx({Quote(format.Pattern)});");
            case FieldFormatKind.Time:
                return ($"AFTime_FormatEx({Quote(format.Pattern)});", $"AFTime_KeystrokeEx({Quote(format.Pattern)});");
            case FieldFormatKind.Special:
                var n = Int(format.Special, 0, 3);
                return ($"AFSpecial_Format({n});", $"AFSpecial_Keystroke({n});");
            default:
                return (null, null);
        }
    }

    /// <summary>Builds the calculation script (/AA /C) as common PDF editors write it for "value is the sum (…) of the following fields".</summary>
    /// <param name="calculation">The calculation; a formula is written in simplified field notation.</param>
    /// <returns>The script; at most 1 000 field names are included.</returns>
    public static string ForCalculation(FieldCalculation calculation)
    {
        if (calculation is { Operation: CalcOperation.Formula, Formula: { } formula }) return formula.Script();   // simplified field notation
        var op = calculation.Operation switch
        {
            CalcOperation.Product => "PRD",
            CalcOperation.Average => "AVG",
            CalcOperation.Minimum => "MIN",
            CalcOperation.Maximum => "MAX",
            _ => "SUM",
        };
        return $"AFSimple_Calculate(\"{op}\", new Array ({string.Join(", ", calculation.Fields.Take(1000).Select(Quote))}));";
    }

    /// <summary>Builds a JavaScript action (12.6.4.16) with the script as a string.</summary>
    /// <param name="script">The script.</param>
    /// <returns>The action dictionary.</returns>
    public static PdfDictionary Action(string script)
    {
        var action = new PdfDictionary();
        action.Set(PdfName.Type, PdfName.Get("Action"));
        action.Set(S, PdfName.Get("JavaScript"));
        action.Set(JS, new PdfString(PdfTextEncoding.Encode(script)));
        return action;
    }
}
