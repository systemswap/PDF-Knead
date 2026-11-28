using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// The units the editor of formatted text moves, deletes and selects by, and how it takes in typed text (docs/FEHLERGEDAECHTNIS.md, No. 1185):
/// <list type="bullet">
/// <item>the caret stops neither within a surrogate pair, nor before a mark (a vowel sign stays at its syllable), nor between a virama or
/// coeng and the consonant after it — the coeng and its consonant are one unit (Unicode 16.4.2); the backspace key deletes a virama or coeng
/// itself alone (the halant at the end of a word), the caret does not stop before it (it belongs to the grapheme cluster of its consonant,
/// No. 1195);</item>
/// <item>words consist of letters, digits and marks per code point, also outside the Basic Multilingual Plane (Unicode 13.8.1, 13.21.1);</item>
/// <item>the control codes of Unicode 23.1.2 are taken as separators, not dropped; other control codes are dropped;</item>
/// <item>the paired directional formatting characters stay paired when text is deleted or inserted (Unicode 23.2.5);</item>
/// <item>the arrow keys and a click stop only at the boundaries of extended grapheme clusters (UAX #29, as the runtime gives them:
/// <see cref="StringInfo.GetNextTextElementLength(ReadOnlySpan{char})"/>) — a vowel in several parts (Kirat Rai, Unicode 13.23.1), Hangul
/// jamo of one syllable, emoji ZWJ sequences, pairs of regional indicators, CR LF are one unit. Where a grapheme cluster and the unit of a
/// syllabic script differ, the larger unit counts (both kinds of boundary must hold). The backspace key keeps its own rules (one code point,
/// a mark alone, a consonant after a coeng with it; the Arabic marks of No. 1180). Before, the caret stopped at every code point that is no
/// mark (docs/FEHLERGEDAECHTNIS.md, No. 1195).</item>
/// </list>
/// </summary>
public static class EditUnits
{
    // ------------------------------------------------------------ caret stops

    /// <summary>Whether the caret may stand at <paramref name="at"/> (0 and the end always): a boundary of the script's units and of the grapheme clusters.</summary>
    public static bool IsStop(string text, int at) => IsUnitStop(text, at) && IsGraphemeBoundary(text, at);

    /// <summary>
    /// The boundaries of the extended grapheme clusters of a text (one bit per place, the end included), counted once from its start and kept as
    /// long as the text lives (docs/FEHLERGEDAECHTNIS.md, No. 1210): an arrow key or a click then costs at most a pass over the text, also in a
    /// grapheme cluster of hundreds of thousands of units from a file. Before, every place counted the clusters from a place where a boundary
    /// is certain, up to 1 024 units back, and to the end of the cluster it lay in — quadratic in the length of a long cluster, and regional
    /// indicators beyond 1 024 units were paired from there.
    /// </summary>
    private static readonly ConditionalWeakTable<string, ulong[]> GraphemeBoundaries = new();

    /// <summary>
    /// Whether <paramref name="at"/> is a boundary of the extended grapheme clusters (UAX #29) of <paramref name="text"/>: the clusters as the
    /// runtime gives them, counted from the start of the text (<see cref="BoundariesOf"/>).
    /// </summary>
    private static bool IsGraphemeBoundary(string text, int at) => at <= 0 || at >= text.Length || IsSet(BoundariesOf(text), at);

    /// <summary>The boundaries of the grapheme clusters of <paramref name="text"/> (bit i: a boundary before unit i; 0 and the end set).</summary>
    private static ulong[] BoundariesOf(string text) => GraphemeBoundaries.GetValue(text, static t =>
    {
        var bits = new ulong[(t.Length >> 6) + 1];
        for (var p = 0; p < t.Length; p += Math.Max(1, StringInfo.GetNextTextElementLength(t.AsSpan(p))))
            bits[p >> 6] |= 1UL << p;
        bits[t.Length >> 6] |= 1UL << t.Length;
        return bits;
    });

    private static bool IsSet(ulong[] bits, int at) => (bits[at >> 6] & (1UL << at)) != 0;

    /// <summary>The first boundary at or after <paramref name="at"/> (the end of the text at the latest; whole words of 64 places at a time).</summary>
    private static int BoundaryFrom(ulong[] bits, int at)
    {
        var word = at >> 6;
        var rest = bits[word] & (ulong.MaxValue << at);
        while (rest == 0) rest = bits[++word];
        return (word << 6) + BitOperations.TrailingZeroCount(rest);
    }

    /// <summary>The last boundary at or before <paramref name="at"/> (0 at the latest).</summary>
    private static int BoundaryUpTo(ulong[] bits, int at)
    {
        var word = at >> 6;
        var rest = bits[word] & (ulong.MaxValue >> (63 - (at & 63)));
        while (rest == 0) rest = bits[--word];
        return (word << 6) + 63 - BitOperations.LeadingZeroCount(rest);
    }

    /// <summary>Whether <paramref name="at"/> is a boundary of the units of the syllabic scripts (No. 1185; also what the backspace key keeps).</summary>
    private static bool IsUnitStop(string text, int at)
    {
        if (at <= 0 || at >= text.Length) return true;
        if (char.IsLowSurrogate(text[at]) && char.IsHighSurrogate(text[at - 1])) return false;
        var c = CodePointAt(text, at);
        if (IsMark(c) && !IsVirama(c)) return false;
        // a consonant (or joiner) after a virama or coeng belongs to it
        var before = CodePointBefore(text, at, out var length);
        if (before is 0x200C or 0x200D && at - length > 0) before = CodePointBefore(text, at - length, out _);
        return !IsVirama(before);
    }

    /// <summary>The stop before <paramref name="at"/>.</summary>
    public static int Previous(string text, int at)
    {
        at = Math.Min(at, text.Length);
        if (at <= 0) return 0;
        // from boundary to boundary of the grapheme clusters, back to the first one that is also a boundary of the units (0 at the latest)
        var bits = BoundariesOf(text);
        for (var i = BoundaryUpTo(bits, at - 1); ; i = BoundaryUpTo(bits, i - 1))
            if (i <= 0 || IsUnitStop(text, i)) return i;
    }

    /// <summary>
    /// Where the backspace key deletes back to from <paramref name="at"/>: one code point, a mark alone (the caret stays at its syllable, the marks
    /// before it remain) — but a consonant after a virama or coeng together with it, so that no coeng stays alone (Unicode 16.4.2).
    /// </summary>
    public static int PreviousForDeletion(string text, int at)
    {
        at = Math.Min(at, text.Length);
        if (at <= 0) return 0;
        var i = at - 1;
        if (i > 0 && char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1])) i--;
        // the consonant (and a joiner) after a virama or coeng goes with it (the units of the script, not the grapheme clusters: No. 1195)
        while (i > 0 && !IsMark(CodePointAt(text, i)) && !IsUnitStop(text, i)) i--;
        return i;
    }

    /// <summary>The stop after <paramref name="at"/>.</summary>
    public static int Next(string text, int at)
    {
        if (at >= text.Length) return text.Length;
        // from boundary to boundary of the grapheme clusters, on to the first one that is also a boundary of the units (the end at the latest)
        var bits = BoundariesOf(text);
        for (var i = BoundaryFrom(bits, Math.Max(0, at) + 1); ; i = BoundaryFrom(bits, i + 1))
            if (i >= text.Length || IsUnitStop(text, i)) return i;
    }

    // ------------------------------------------------------------ words

    /// <summary>Whether the code point starting at <paramref name="index"/> belongs to a word: a letter, digit or mark, „_“, an apostrophe, a joiner.</summary>
    public static bool IsWordAt(string text, int index) => IsWord(CodePointAt(text, index));

    /// <summary>Whether the code point before <paramref name="index"/> belongs to a word; <paramref name="length"/>: its UTF-16 length.</summary>
    public static bool IsWordBefore(string text, int index, out int length) => IsWord(CodePointBefore(text, index, out length));

    private static bool IsWord(int c)
    {
        if (c is '_' or '\'' or '’' or 0x200C or 0x200D) return true;
        if (c is < 0 or > 0x10FFFF or >= 0xD800 and <= 0xDFFF) return false;
        return CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    // ------------------------------------------------------------ typed text

    /// <summary>
    /// Typed or pasted plain text for the editor: CR LF, CR, NEL and the information separators FS, GS, RS as a paragraph break („\n“; Unicode
    /// 23.1.2: B, a newline function), the paragraph separator U+2029 too; VT and FF as the line separator U+2028 (UAX #14: a hard line break,
    /// class BK, but no new paragraph — before FF began a paragraph; docs/FEHLERGEDAECHTNIS.md, No. 1196); the unit separator US as a tab (S);
    /// any other control code is dropped (no ISO/IEC 6429 control sequences are interpreted: ESC alone goes, what follows it stays text).
    /// Before VT, FF, FS–US and NEL were dropped and the words beside them ran together.
    /// </summary>
    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\r':
                    sb.Append('\n');
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    break;
                case '\n' or '\u0085' or '\u001C' or '\u001D' or '\u001E' or '\u2029':
                    sb.Append('\n');
                    break;
                case '\u000B' or '\u000C':
                    sb.Append('\u2028');
                    break;
                case '\t' or '\u001F':
                    sb.Append('\t');
                    break;
                default:
                    if (!char.IsControl(c)) sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ paired directional formatting characters

    /// <summary>
    /// The partners outside a deleted range of paired directional formatting characters inside it (LRE, RLE, LRO, RLO with PDF; LRI, RLI, FSI
    /// with PDI; Unicode 23.2.5, UAX #9 BD9): deleting one of a pair deletes the other as well, so no unpaired one stays behind.
    /// </summary>
    /// <param name="text">The text before the deletion.</param>
    /// <param name="start">Start of the deleted range.</param>
    /// <param name="length">Its length.</param>
    /// <returns>The places (in <paramref name="text"/>, outside the range) to delete as well, ascending.</returns>
    public static List<int> OrphanedPartners(string text, int start, int length)
    {
        var result = new List<int>();
        if (length <= 0) return result;
        var end = start + length;
        foreach (var (open, close) in Pairs(text))
        {
            var openIn = open >= start && open < end;
            var closeIn = close >= start && close < end;
            if (openIn && !closeIn) result.Add(close);
            else if (closeIn && !openIn) result.Add(open);
        }
        result.Sort();
        return result;
    }

    /// <summary>
    /// Inserted text with its directional formatting characters paired on their own: a closing one without its opening one is dropped, an
    /// opening one without its closing one gets it at the end (in the reverse order of opening) — the text around keeps its pairs (23.2.5).
    /// </summary>
    public static string Balanced(string inserted)
    {
        if (!inserted.Any(c => c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')) return inserted;
        var paired = new HashSet<int>();
        foreach (var (open, close) in Pairs(inserted))
        {
            paired.Add(open);
            paired.Add(close);
        }
        var sb = new StringBuilder(inserted.Length + 4);
        var unclosed = new List<char>();
        for (var i = 0; i < inserted.Length; i++)
        {
            var c = inserted[i];
            if (c is '\n' or '\u2029') CloseAll();
            if (IsOpening(c) && !paired.Contains(i)) unclosed.Add(c);
            else if (IsClosing(c) && !paired.Contains(i)) continue;
            sb.Append(c);
        }
        CloseAll();
        return sb.ToString();

        void CloseAll()
        {
            for (var k = unclosed.Count - 1; k >= 0; k--) sb.Append(unclosed[k] is >= '\u2066' and <= '\u2068' ? '\u2069' : '\u202C');
            unclosed.Clear();
        }
    }

    private static bool IsOpening(char c) => c is >= '\u202A' and <= '\u202B' or >= '\u202D' and <= '\u202E' or >= '\u2066' and <= '\u2068';

    private static bool IsClosing(char c) => c is '\u202C' or '\u2069';

    /// <summary>
    /// The matched pairs (opening, closing) of a text, per paragraph: a PDI closes the innermost open isolate and with it the embeddings opened
    /// inside it; a PDF closes the innermost embedding unless an isolate was opened after it; at most 125 levels, as UAX #9 counts.
    /// </summary>
    private static List<(int Open, int Close)> Pairs(string text)
    {
        var pairs = new List<(int, int)>();
        var stack = new List<(int At, bool Isolate)>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r' or '\u2029' or '\u0085' or >= '\u001C' and <= '\u001E')
            {
                stack.Clear();
            }
            else if (IsOpening(c))
            {
                if (stack.Count < 125) stack.Add((i, c >= '\u2066'));
            }
            else if (c == '\u2069')
            {
                var isolate = stack.FindLastIndex(s => s.Isolate);
                if (isolate < 0) continue;
                pairs.Add((stack[isolate].At, i));
                stack.RemoveRange(isolate, stack.Count - isolate);
            }
            else if (c == '\u202C' && stack.Count > 0 && !stack[^1].Isolate)
            {
                pairs.Add((stack[^1].At, i));
                stack.RemoveAt(stack.Count - 1);
            }
        }
        return pairs;
    }

    // ------------------------------------------------------------ helpers

    private static bool IsMark(int c) => SearchFolding.IsMark(c);

    private static bool IsVirama(int c) => c > 0 && SyllableData.SyllabicOf(c) is SyllabicCategory.Virama or SyllabicCategory.InvisibleStacker;

    private static int CodePointAt(string text, int i) =>
        i < 0 || i >= text.Length ? -1 : char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];

    private static int CodePointBefore(string text, int i, out int length)
    {
        length = 0;
        if (i <= 0 || i > text.Length) return -1;
        if (i >= 2 && char.IsLowSurrogate(text[i - 1]) && char.IsHighSurrogate(text[i - 2]))
        {
            length = 2;
            return char.ConvertToUtf32(text[i - 2], text[i - 1]);
        }
        length = 1;
        return text[i - 1];
    }
}
