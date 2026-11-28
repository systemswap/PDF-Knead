using System.Text;
using PdfKnead.Core.Fonts.Layout;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// The text a stored block must show for <see cref="Redactor.RemoveForms"/> to take its form back as a block: the characters of the stored text
/// without white space and without the directional formatting characters (they are drawn with neither glyph nor advance, UAX #9, 2), in the order the
/// appearance draws them. Text without a right-to-left level is drawn in its stored order. A paragraph with right-to-left text is drawn line by line
/// in visual order (UAX #9, L1 and L2 per line, as <see cref="RichLayout"/> and the appearance do); the line breaks are not stored, so the drawn
/// characters must be the visual order of the stored paragraph divided into lines where the drawing changes its baseline, found line by line from the
/// top: a line ends at such a change where the characters drawn so far are exactly those of the stored characters so far and, reordered with the
/// levels of the whole stored text, give the same sequence. For right-to-left text this is stricter than the comparison of the logical text before:
/// every drawn character is a stored one, none is missing, and their order is the one the stored text is shown in on the lines drawn — a
/// right-to-left word drawn in its logical order on one baseline no longer counts as the stored text. Two things may differ without changing what is
/// shown: the characters of one glyph (a ligature, a letter with its marks composed) may stand in either order, as the glyph's text keeps its logical
/// order also where the glyph stands in a right-to-left run; and the marks on one letter may stand in any order (the shaper may reorder them).
/// Before, the drawn sequence was compared with the logical text, and own text with Hebrew or Arabic letters or directional formatting characters did
/// not become a block again on opening (docs/FEHLERGEDAECHTNIS.md, No. 1177).
/// </summary>
public sealed class ShownText
{
    private readonly string _text;
    private readonly Bidi.Analysis? _bidi;

    /// <summary>Creates the expected text of a block.</summary>
    /// <param name="text">The stored text in logical order (paragraphs separated by '\n'), as the layout receives it.</param>
    public ShownText(string text)
    {
        _text = text;
        // the same analysis as the layout: the levels of the whole text, paragraphs at every paragraph separator
        _bidi = Bidi.HasRightToLeft(text) ? Bidi.Analyze(text) : null;
        var length = 0;
        foreach (var c in text)
            if (Shown(c)) length++;
        Length = length;
    }

    /// <summary>The number of characters the block draws (without white space and directional formatting characters).</summary>
    public int Length { get; }

    /// <summary>
    /// Whether a character of the stored text is drawn with a glyph that has a text — not white space, not a directional formatting character
    /// and not a soft hyphen (invisible within a line, a hyphen standing for itself where the line broke after it; docs/FEHLERGEDAECHTNIS.md,
    /// No. 1185); the drawn side leaves the same out.
    /// </summary>
    internal static bool Shown(char c) => !char.IsWhiteSpace(c) && !Bidi.IsControl(c) && c != '\u00AD';

    /// <summary>
    /// Whether the drawn characters show the stored text.
    /// </summary>
    /// <param name="drawn">The texts of the drawn glyphs in drawing order, without white space.</param>
    /// <param name="units">The index in <paramref name="drawn"/> at which each glyph's text starts, ascending.</param>
    /// <param name="lines">The index in <paramref name="drawn"/> of each glyph that stands on another baseline than the glyph before it (a line
    /// of the drawing may begin there; a mark or a raised span may begin one too — only there may a right-to-left paragraph break).</param>
    /// <returns><see langword="true"/> if the drawn characters are the stored ones in the order the block shows them.</returns>
    public bool Matches(string drawn, IReadOnlyList<int> units, IReadOnlyList<int> lines)
    {
        if (drawn.Length != Length) return false;
        if (_bidi is not { HasOddLevel: true }) return Logical(0, _text.Length, drawn, 0) == drawn.Length;
        var unitStart = new bool[drawn.Length + 1];
        foreach (var u in units)
            if ((uint)u < (uint)unitStart.Length) unitStart[u] = true;
        unitStart[drawn.Length] = true;
        var lineStart = new bool[drawn.Length + 1];
        foreach (var l in lines)
            if ((uint)l < (uint)lineStart.Length) lineStart[l] = true;
        lineStart[drawn.Length] = true;
        // effort of the reordering checks: a few times the length (a crafted text whose characters agree at many places but in another order
        // would otherwise cost the square of its length)
        long budget = 4L * drawn.Length + 4096;
        var k = 0;
        for (var start = 0; start <= _text.Length;)
        {
            var end = _text.IndexOf('\n', start);
            if (end < 0) end = _text.Length;
            k = Odd(start, end) ? Paragraph(start, end, drawn, k, unitStart, lineStart, ref budget) : Logical(start, end, drawn, k);
            if (k < 0) return false;
            start = end + 1;
        }
        return k == drawn.Length;
    }

    /// <summary>Whether a character of the paragraph has an odd level.</summary>
    private bool Odd(int start, int end)
    {
        for (var i = start; i < end; i++)
            if (_bidi!.Level(i) % 2 == 1) return true;
        return false;
    }

    /// <summary>Compares the shown characters of a stretch in stored order; the position in <paramref name="drawn"/> after them, or -1.</summary>
    private int Logical(int start, int end, string drawn, int k)
    {
        var expected = new StringBuilder(end - start);
        for (var i = start; i < end; i++)
            if (Shown(_text[i])) expected.Append(_text[i]);
        if (k + expected.Length > drawn.Length || SortedMarks(expected.ToString()) != SortedMarks(drawn.Substring(k, expected.Length))) return -1;
        return k + expected.Length;
    }

    /// <summary>
    /// The text with each run of consecutive nonspacing marks in code point order: the shaper may put the marks on one letter in another order
    /// than they were typed (shadda before fatha, UAX #53), which changes nothing that is shown; every mark must still be
    /// there, at its letter.
    /// </summary>
    private static string SortedMarks(string text)
    {
        char[]? chars = null;
        for (var i = 0; i < text.Length; i++)
        {
            if (!Mark(text[i])) continue;
            var j = i + 1;
            while (j < text.Length && Mark(text[j])) j++;
            if (j - i > 1)
            {
                chars ??= text.ToCharArray();
                Array.Sort(chars, i, j - i);
            }
            i = j - 1;
        }
        return chars is null ? text : new string(chars);
    }

    private static bool Mark(char c) => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark;

    /// <summary>A paragraph with right-to-left text, line by line (see the class); the position in <paramref name="drawn"/> after it, or -1.</summary>
    private int Paragraph(int start, int end, string drawn, int k, bool[] unitStart, bool[] lineStart, ref long budget)
    {
        var p = start;
        var balance = new Dictionary<char, int>();
        while (true)
        {
            while (p < end && !Shown(_text[p])) p++;
            if (p >= end) return k;
            // the line from p: the stored characters taken so far against as many drawn ones, until both hold the same characters and the
            // reordered stored line gives the drawn sequence
            balance.Clear();
            var unequal = 0;
            var m = 0;
            var found = false;
            for (var i = p; i < end; i++)
            {
                if (!Shown(_text[i])) continue;
                if (k + m >= drawn.Length) return -1;
                Count(balance, _text[i], 1, ref unequal);
                Count(balance, drawn[k + m], -1, ref unequal);
                m++;
                var q = i + 1;
                if (unequal != 0 || !unitStart[k + m] || !lineStart[k + m] || q < end && char.IsLowSurrogate(_text[q]) && char.IsHighSurrogate(_text[i])) continue;
                if ((budget -= m) < 0) return -1;
                if (!LineMatches(p, q, drawn, k, m, unitStart)) continue;
                p = q;
                k += m;
                found = true;
                break;
            }
            if (!found) return -1;
        }
    }

    private static void Count(Dictionary<char, int> balance, char c, int delta, ref int unequal)
    {
        balance.TryGetValue(c, out var before);
        var after = before + delta;
        balance[c] = after;
        if (before == 0) unequal++;
        if (after == 0) unequal--;
    }

    /// <summary>
    /// Whether the stored line <paramref name="p"/> to <paramref name="q"/>, reordered with its levels after L1 (UAX #9, L2; per code point),
    /// gives the <paramref name="m"/> drawn characters from <paramref name="k"/>, each glyph's characters in either order.
    /// </summary>
    private bool LineMatches(int p, int q, string drawn, int k, int m, bool[] unitStart)
    {
        var levels = _bidi!.LineLevels(p, q);
        var starts = new List<int>(q - p);
        var itemLevels = new List<byte>(q - p);
        for (var i = p; i < q; i++)
        {
            starts.Add(i);
            itemLevels.Add(levels[i - p]);
            if (char.IsHighSurrogate(_text[i]) && i + 1 < q && char.IsLowSurrogate(_text[i + 1])) i++;
        }
        var visual = new StringBuilder(m);
        foreach (var item in Bidi.VisualOrder(itemLevels))
        {
            var from = starts[item];
            var to = item + 1 < starts.Count ? starts[item + 1] : q;
            for (var i = from; i < to; i++)
                if (Shown(_text[i])) visual.Append(_text[i]);
        }
        if (visual.Length != m) return false;
        var expected = SortedMarks(visual.ToString());
        var shown = SortedMarks(drawn.Substring(k, m));
        for (var u = k; u < k + m;)
        {
            var next = u + 1;
            while (next < k + m && !unitStart[next]) next++;
            if (!Same(shown, u - k, next - k, expected, u - k) && !Backwards(drawn, u, next, expected, u - k)) return false;
            u = next;
        }
        return true;
    }

    /// <summary>
    /// Whether a glyph's text that holds a letter with its marks (<paramref name="drawn"/> from <paramref name="from"/> to <paramref name="to"/>,
    /// unsorted) is the expected stretch backwards, its marks then in any order: a cluster of reordered Arabic marks written with its text as typed
    /// on one glyph stands in a right-to-left run with the marks reversed before the letter (docs/FEHLERGEDAECHTNIS.md, No. 1180).
    /// </summary>
    private static bool Backwards(string drawn, int from, int to, string expected, int at)
    {
        if (to - from < 2 || at + to - from > expected.Length) return false;
        var reversed = new StringBuilder(to - from);
        for (var i = to; i > from;)
        {
            var length = i - 2 >= from && char.IsLowSurrogate(drawn[i - 1]) && char.IsHighSurrogate(drawn[i - 2]) ? 2 : 1;
            i -= length;
            reversed.Append(drawn, i, length);
        }
        return string.CompareOrdinal(SortedMarks(reversed.ToString()), 0, expected, at, to - from) == 0;
    }

    /// <summary>Whether a glyph's text (<paramref name="drawn"/> from <paramref name="from"/> to <paramref name="to"/>) is the expected stretch, forwards or backwards by code points.</summary>
    private static bool Same(string drawn, int from, int to, string expected, int at)
    {
        var forwards = true;
        for (var i = from; i < to && forwards; i++) forwards = drawn[i] == expected[at + i - from];
        if (forwards || to - from < 2) return forwards;
        // backwards: the code points of the glyph's text in reverse order
        var position = at;
        for (var i = to; i > from;)
        {
            var length = i - 2 >= from && char.IsLowSurrogate(drawn[i - 1]) && char.IsHighSurrogate(drawn[i - 2]) ? 2 : 1;
            i -= length;
            for (var j = 0; j < length; j++)
                if (drawn[i + j] != expected[position++]) return false;
        }
        return true;
    }
}
