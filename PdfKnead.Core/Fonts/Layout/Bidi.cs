using System.Globalization;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// The Unicode Bidirectional Algorithm (UAX #9) for text the program sets itself — an appearance built for a free text or a field: the
/// paragraph level from the first strong character (P2, P3), the weak types (W1–W7), the neutrals (N1, N2), the implicit levels (I1, I2) and
/// the reordering of a line (L2), with the mirrored brackets of right-to-left runs (L4, for the common pairs). Not supported: explicit
/// embeddings, overrides and isolates (X1–X10: the characters are taken as neutral) and bracket pairs (N0). Bidi classes from the general
/// category and the script blocks; written from UAX #9, no code of another implementation used (docs/FEHLERGEDAECHTNIS.md, No. 910).
/// </summary>
internal static class Bidi
{
    private enum Kind : byte { L, R, AL, EN, ES, ET, AN, CS, NSM, B, S, WS, ON }

    private static Kind KindOf(int c)
    {
        if (c is >= 0x0660 and <= 0x0669 or 0x066B or 0x066C or >= 0x0600 and <= 0x0605 or 0x06DD or 0x08E2) return Kind.AN;
        if (c is >= '0' and <= '9' or >= 0x06F0 and <= 0x06F9 or 0x00B2 or 0x00B3 or 0x00B9 or >= 0xFF10 and <= 0xFF19) return Kind.EN;
        if (c is '+' or '-' or 0x207A or 0x207B or 0xFB29 or 0xFE62 or 0xFE63 or 0xFF0B or 0xFF0D) return Kind.ES;
        if (c is '#' or '$' or '%' or 0x00A2 or 0x00A3 or 0x00A4 or 0x00A5 or 0x00B0 or 0x00B1 or 0x066A or 0x2030 or 0x2031 or 0x20AC or >= 0x20A0 and <= 0x20CF)
            return Kind.ET;
        if (c is ',' or '.' or '/' or ':' or 0x00A0 or 0x060C or 0x202F or 0x2044 or 0xFE50 or 0xFE52 or 0xFE55 or 0xFF0C or 0xFF0E or 0xFF0F or 0xFF1A) return Kind.CS;
        if (c is '\n' or '\r' or 0x1C or 0x1D or 0x1E or 0x0085 or 0x2029) return Kind.B;
        if (c is '\t' or 0x0B or 0x1F) return Kind.S;
        if (c is ' ' or 0x0C or 0x1680 or >= 0x2000 and <= 0x200A or 0x2028 or 0x205F or 0x3000) return Kind.WS;
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) return Kind.NSM;
        if (c is >= 0x0590 and <= 0x05FF or >= 0x07C0 and <= 0x085F or >= 0xFB1D and <= 0xFB4F or >= 0x10800 and <= 0x10FFF and not (>= 0x10D00 and <= 0x10D3F)
            or >= 0x1E800 and <= 0x1EDFF)
            return category is UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol or UnicodeCategory.OtherSymbol ? Kind.ON : Kind.R;
        if (c is >= 0x0600 and <= 0x07BF or >= 0x0860 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF or >= 0x10D00 and <= 0x10D3F
            or >= 0x1EC70 and <= 0x1EFFF)
            return category is UnicodeCategory.OtherPunctuation && c is not (0x060C or 0x061B or 0x061F or 0x06D4) ? Kind.ON : Kind.AL;
        if (c is 0x200E) return Kind.L;
        if (c is 0x200F) return Kind.R;
        if (c is 0x061C) return Kind.AL;
        return category switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => Kind.L,
            UnicodeCategory.DecimalDigitNumber => Kind.L,
            _ => Kind.ON,
        };
    }

    /// <summary>Whether the text holds a right-to-left character (only then is the algorithm needed).</summary>
    internal static bool HasRightToLeft(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (KindOf(rune.Value) is Kind.R or Kind.AL or Kind.AN) return true;
        return false;
    }

    /// <summary>
    /// The embedding level of every UTF-16 unit of <paramref name="text"/> (paragraphs split at paragraph separators; each paragraph's level
    /// from its first strong character, else <paramref name="defaultLevel"/>). Even: left to right; odd: right to left.
    /// </summary>
    internal static byte[] Levels(string text, byte defaultLevel = 0)
    {
        var levels = new byte[text.Length];
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && !(text[i] is '\n' or '\u2029' || text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))) continue;
            Paragraph(text, start, i, levels, defaultLevel);
            // the separator itself at the level of the paragraph it ends (L1)
            if (i < text.Length) levels[i] = ParagraphLevel(text, start, i, defaultLevel);
            start = i + 1;
        }
        return levels;
    }

    private static byte ParagraphLevel(string text, int start, int end, byte defaultLevel)
    {
        for (var i = start; i < end; i++)
        {
            var c = char.IsHighSurrogate(text[i]) && i + 1 < end ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            switch (KindOf(c))
            {
                case Kind.L: return 0;
                case Kind.R or Kind.AL: return 1;
            }
        }
        return defaultLevel;
    }

    private static void Paragraph(string text, int start, int end, byte[] levels, byte defaultLevel)
    {
        if (end <= start) return;
        var level = ParagraphLevel(text, start, end, defaultLevel);
        var n = end - start;
        var kinds = new Kind[n];
        for (var i = 0; i < n; i++)
        {
            var at = start + i;
            if (char.IsLowSurrogate(text[at]) && i > 0)
            {
                kinds[i] = kinds[i - 1];
                continue;
            }
            var c = char.IsHighSurrogate(text[at]) && at + 1 < end ? char.ConvertToUtf32(text[at], text[at + 1]) : text[at];
            kinds[i] = KindOf(c);
        }
        var sos = level % 2 == 0 ? Kind.L : Kind.R;
        // W1: a mark takes the type of the character before it (at the start: sos)
        for (var i = 0; i < n; i++)
            if (kinds[i] == Kind.NSM) kinds[i] = i == 0 ? sos : kinds[i - 1] is Kind.B or Kind.S or Kind.WS ? Kind.ON : kinds[i - 1];
        // W2: a European number after Arabic letters is an Arabic number; W3: Arabic letters are R
        var strong = sos;
        for (var i = 0; i < n; i++)
        {
            if (kinds[i] is Kind.L or Kind.R or Kind.AL) strong = kinds[i];
            else if (kinds[i] == Kind.EN && strong == Kind.AL) kinds[i] = Kind.AN;
        }
        for (var i = 0; i < n; i++)
            if (kinds[i] == Kind.AL) kinds[i] = Kind.R;
        // W4: one separator between two numbers of the same kind joins them
        for (var i = 1; i + 1 < n; i++)
        {
            if (kinds[i] == Kind.ES && kinds[i - 1] == Kind.EN && kinds[i + 1] == Kind.EN) kinds[i] = Kind.EN;
            else if (kinds[i] == Kind.CS && kinds[i - 1] == kinds[i + 1] && kinds[i - 1] is Kind.EN or Kind.AN) kinds[i] = kinds[i - 1];
        }
        // W5: terminators next to a European number belong to it
        for (var i = 0; i < n; i++)
        {
            if (kinds[i] != Kind.ET) continue;
            var j = i;
            while (j < n && kinds[j] == Kind.ET) j++;
            if (i > 0 && kinds[i - 1] == Kind.EN || j < n && kinds[j] == Kind.EN)
                for (var k = i; k < j; k++) kinds[k] = Kind.EN;
            i = j - 1;
        }
        // W6: the rest of the separators and terminators are neutral
        for (var i = 0; i < n; i++)
            if (kinds[i] is Kind.ES or Kind.ET or Kind.CS) kinds[i] = Kind.ON;
        // W7: a European number after left-to-right text is L
        strong = sos;
        for (var i = 0; i < n; i++)
        {
            if (kinds[i] is Kind.L or Kind.R) strong = kinds[i];
            else if (kinds[i] == Kind.EN && strong == Kind.L) kinds[i] = Kind.L;
        }
        // N1, N2: a run of neutrals between two strong types of one direction takes it (numbers count as R); otherwise the embedding direction
        for (var i = 0; i < n; i++)
        {
            if (kinds[i] is not (Kind.B or Kind.S or Kind.WS or Kind.ON)) continue;
            var j = i;
            while (j < n && kinds[j] is Kind.B or Kind.S or Kind.WS or Kind.ON) j++;
            var before = i == 0 ? sos : Direction(kinds[i - 1]);
            var after = j == n ? sos : Direction(kinds[j]);
            var resolved = before == after ? before : sos;
            for (var k = i; k < j; k++) kinds[k] = resolved;
            i = j - 1;
        }
        // I1, I2: the implicit levels
        for (var i = 0; i < n; i++)
            levels[start + i] = (byte)(level % 2 == 0
                ? kinds[i] switch { Kind.R => level + 1, Kind.AN or Kind.EN => level + 2, _ => level }
                : kinds[i] switch { Kind.L or Kind.EN or Kind.AN => level + 1, _ => level });
        // L1: white space at the end of the paragraph and before a segment separator goes back to the paragraph level
        for (var i = n - 1; i >= 0 && KindOf(text[start + i]) is Kind.WS or Kind.S; i--) levels[start + i] = level;
    }

    private static Kind Direction(Kind kind) => kind is Kind.L ? Kind.L : Kind.R;

    /// <summary>
    /// The visual order of items with the given levels (L2): from the highest level down to the lowest odd one, every run of items at that
    /// level or higher is reversed. Returns the item indices from left to right.
    /// </summary>
    internal static int[] VisualOrder(IReadOnlyList<byte> levels)
    {
        var order = Enumerable.Range(0, levels.Count).ToArray();
        if (levels.Count == 0) return order;
        var highest = levels.Max();
        var lowestOdd = levels.Where(l => l % 2 == 1).DefaultIfEmpty((byte)(highest + 1)).Min();
        for (var level = highest; level >= lowestOdd && level > 0; level--)
            for (var i = 0; i < order.Length; i++)
            {
                if (levels[order[i]] < level) continue;
                var j = i;
                while (j < order.Length && levels[order[j]] >= level) j++;
                Array.Reverse(order, i, j - i);
                i = j;
            }
        return order;
    }

    /// <summary>The mirror image of a character shown right to left (L4; the common bracket pairs of BidiMirroring.txt), else itself.</summary>
    internal static int Mirrored(int c) => c switch
    {
        '(' => ')', ')' => '(', '[' => ']', ']' => '[', '{' => '}', '}' => '{', '<' => '>', '>' => '<',
        0x00AB => 0x00BB, 0x00BB => 0x00AB, 0x2039 => 0x203A, 0x203A => 0x2039, 0x2264 => 0x2265, 0x2265 => 0x2264,
        _ => c,
    };
}
