using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// The deprecated format characters U+206C–U+206F in text PDF-Knead sets itself (The Unicode Standard 23.3.1; docs/FEHLERGEDAECHTNIS.md,
/// No. 1221): from U+206D ACTIVATE ARABIC FORM SHAPING up to a U+206C INHIBIT ARABIC FORM SHAPING the Arabic presentation forms are shaped as their
/// nominal letters (<see cref="TextShaper.NominalLetters"/>; the default state is inhibited: a presentation form keeps its own glyph, sentences 24–28),
/// and from U+206E NATIONAL DIGIT SHAPES up to a U+206F NOMINAL DIGIT SHAPES the digits U+0030–U+0039 are drawn in the digit shapes of the script
/// of the text around them (<see cref="NationalDigits"/>; the default state is nominal, sentences 30–36). Neither pair nests: each character only
/// sets its state. A paragraph (up to a character of the bidirectional class B) starts in the default states. Only the display changes: the
/// glyphs carry the characters as typed, which are saved, copied and searched; the format characters themselves are drawn as nothing.
/// </summary>
internal static class FormatSelectors
{
    /// <summary>Bit of a state: the shaping of presentation forms is active (after U+206D).</summary>
    internal const byte Shaping = 1;

    /// <summary>Bit of a state: national digit shapes (after U+206E).</summary>
    internal const byte National = 2;

    /// <summary>
    /// The state in force before each UTF-16 unit of <paramref name="text"/> (and at its end, index <c>text.Length</c>); null if the text holds
    /// none of the four characters (then everything is in the default states).
    /// </summary>
    internal static byte[]? States(string text)
    {
        if (text.AsSpan().IndexOfAnyInRange('\u206C', '\u206F') < 0) return null;
        var states = new byte[text.Length + 1];
        byte state = 0;
        for (var i = 0; i < text.Length; i++)
        {
            states[i] = state;
            state = text[i] switch
            {
                '\u206C' => (byte)(state & ~Shaping),
                '\u206D' => (byte)(state | Shaping),
                '\u206E' => (byte)(state | National),
                '\u206F' => (byte)(state & ~National),
                _ => Bidi.ClassOf(text[i]) == BidiClass.B ? (byte)0 : state,
            };
        }
        states[text.Length] = state;
        return states;
    }

    /// <summary>
    /// The text drawn for <paramref name="text"/>[<paramref name="from"/> … +<paramref name="length"/>]: each digit U+0030–U+0039 in national
    /// shapes (<paramref name="states"/> of <see cref="States"/>) as the digit of the same value of <see cref="NationalZero"/>, where that digit is
    /// of the same UTF-16 length and <paramref name="has"/> (if given) says the font draws it; the other characters as they are. The glyphs stand
    /// for the characters stored, as with the mirrored characters of UAX #9 L4.
    /// </summary>
    internal static string NationalDigits(string text, int from, int length, byte[] states, string? language, Func<int, bool>? has = null)
    {
        StringBuilder? sb = null;
        var zeroAt = -1;
        var zero = -1;
        for (var i = from; i < from + length; i++)
        {
            var c = text[i];
            if (c is < '0' or > '9' || (states[i] & National) == 0) continue;
            // the digits of one number share the script around them
            if (zeroAt < 0 || i != zeroAt + 1) zero = NationalZero(text, i, language);
            zeroAt = i;
            if (zero is < 0 or > 0xFFF6) continue;
            var digit = zero + (c - '0');
            if (has is not null && !has(digit)) continue;
            sb ??= new StringBuilder(text, from, length, length);
            sb[i - from] = (char)digit;
        }
        return sb?.ToString() ?? text.Substring(from, length);
    }

    /// <summary>How far the script of the text around a digit is looked for (UTF-16 units each way, within the paragraph).</summary>
    private const int Reach = 64;

    /// <summary>
    /// The digit zero of the national shapes for the digit at <paramref name="index"/>: by the script of the nearest character of a script of its
    /// own before it (else after it) in its paragraph — in Arabic text the Arabic-Indic digits U+0660–U+0669, in Persian and Urdu (language „fa“,
    /// „ur“) the extended Arabic-Indic digits U+06F0–U+06F9, in any other script its decimal digits where it has some (Devanagari U+0966, Thai
    /// U+0E50 …: the first zero of the script in the Unicode Character Database); -1 where there are none (Latin, Greek, no script near it) — the
    /// digit keeps its nominal shape. The digit shapes themselves are not specified by the standard (23.3.1 sentence 34): this is PDF-Knead's
    /// agreement for them (sentence 32).
    /// </summary>
    internal static int NationalZero(string text, int index, string? language)
    {
        var script = ScriptAround(text, index);
        if (script is null) return -1;
        if (script == "Arab")
        {
            var primary = language?.Trim().Replace('_', '-').Split('-')[0].ToLowerInvariant();
            return primary is "fa" or "fas" or "per" or "ur" or "urd" ? 0x06F0 : 0x0660;
        }
        return Zeros.Value.TryGetValue(script, out var zero) ? zero : -1;
    }

    private static string? ScriptAround(string text, int index)
    {
        for (var i = index - 1; i >= 0 && index - i <= Reach; i--)
        {
            if (Bidi.ClassOf(text[i]) == BidiClass.B) break;
            if (char.IsLowSurrogate(text[i]) && i > 0 && char.IsHighSurrogate(text[i - 1])) i--;
            var script = UnicodeScripts.Script(TextRunes.RuneAt(text, i).Value);
            if (!UnicodeScripts.IsNeutral(script)) return script;
        }
        for (var i = index + 1; i < text.Length && i - index <= Reach; i++)
        {
            if (Bidi.ClassOf(text[i]) == BidiClass.B) break;
            var script = UnicodeScripts.Script(TextRunes.RuneAt(text, i).Value);
            if (!UnicodeScripts.IsNeutral(script)) return script;
            if (char.IsHighSurrogate(text[i])) i++;
        }
        return null;
    }

    /// <summary>The first decimal digit zero of each script (category Nd, value 0, a script of its own), found once in the code space.</summary>
    private static readonly Lazy<Dictionary<string, int>> Zeros = new(() =>
    {
        var zeros = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var cp = 0x80; cp <= 0x1FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF || CharUnicodeInfo.GetUnicodeCategory(cp) != UnicodeCategory.DecimalDigitNumber) continue;
            if (CharUnicodeInfo.GetDecimalDigitValue(char.ConvertFromUtf32(cp), 0) != 0) continue;
            var script = UnicodeScripts.Script(cp);
            if (!UnicodeScripts.IsNeutral(script)) zeros.TryAdd(script, cp);
        }
        return zeros;
    });
}
