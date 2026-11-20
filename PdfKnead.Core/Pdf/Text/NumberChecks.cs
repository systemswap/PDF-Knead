namespace PdfKnead.Core.Pdf.Text;

/// <summary>
/// Check digits of numbers that are worth redacting (KONZEPT.md, section 5.6, „Suchen und schwärzen“): they tell a real account or card
/// number from a code that only looks like one.
/// </summary>
public static class NumberChecks
{
    /// <summary>
    /// Whether an IBAN is valid (ISO 13616-1:2020, 7.1; ISO 7064 MOD 97-10): two letters, two check digits, 11 to 30 letters or digits;
    /// the first four characters moved to the end, letters counted as 10 to 35, the number leaves 1 when divided by 97. Spaces are ignored.
    /// </summary>
    /// <param name="text">The IBAN as written.</param>
    /// <returns>true if it is well-formed and the check digits fit.</returns>
    public static bool IbanValid(string text)
    {
        Span<char> iban = stackalloc char[34];
        var length = 0;
        foreach (var c in text)
        {
            if (c == ' ') continue;
            if (length == iban.Length || !char.IsAsciiLetterOrDigit(c)) return false;
            iban[length++] = char.ToUpperInvariant(c);
        }
        if (length < 15 || !char.IsAsciiLetterUpper(iban[0]) || !char.IsAsciiLetterUpper(iban[1]) || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3]))
            return false;
        var remainder = 0;
        for (var i = 0; i < length; i++)
        {
            var c = iban[(i + 4) % length];
            remainder = char.IsAsciiDigit(c) ? (remainder * 10 + (c - '0')) % 97 : (remainder * 100 + (c - 'A' + 10)) % 97;
        }
        return remainder == 1;
    }

    /// <summary>
    /// Whether a card number is valid (ISO/IEC 7812-1:2017, Annex B, the Luhn formula): 13 to 19 digits, every second digit from the right
    /// doubled (minus 9 above 9), the sum divisible by 10. Spaces and hyphens are ignored; a number of one repeated digit does not count.
    /// </summary>
    /// <param name="text">The number as written.</param>
    /// <returns>true if it is one.</returns>
    public static bool CardValid(string text)
    {
        var digits = text.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19 || digits.Length != text.Count(c => c is not (' ' or '-')) || digits.All(d => d == digits[0])) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1) d = d * 2 > 9 ? d * 2 - 9 : d * 2;
            sum += d;
        }
        return sum % 10 == 0;
    }
}
