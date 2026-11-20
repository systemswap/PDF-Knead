using System.Text;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Core.Pdf.Text;

/// <summary>Quadrilateral in page space: bottom left, bottom right, top right, top left.</summary>
/// <param name="X0">x of the bottom-left corner.</param>
/// <param name="Y0">y of the bottom-left corner.</param>
/// <param name="X1">x of the bottom-right corner.</param>
/// <param name="Y1">y of the bottom-right corner.</param>
/// <param name="X2">x of the top-right corner.</param>
/// <param name="Y2">y of the top-right corner.</param>
/// <param name="X3">x of the top-left corner.</param>
/// <param name="Y3">y of the top-left corner.</param>
public readonly record struct TextQuad(double X0, double Y0, double X1, double Y1, double X2, double Y2, double X3, double Y3)
{
    /// <summary>Axis-aligned bounds of the four corners.</summary>
    public RectD Bounds => RectD.FromLtrb(
        Math.Min(Math.Min(X0, X1), Math.Min(X2, X3)), Math.Min(Math.Min(Y0, Y1), Math.Min(Y2, Y3)),
        Math.Max(Math.Max(X0, X1), Math.Max(X2, X3)), Math.Max(Math.Max(Y0, Y1), Math.Max(Y2, Y3)));
}

/// <summary>Range of <see cref="PageText.Text"/>.</summary>
/// <param name="Start">Index of the first character.</param>
/// <param name="Length">Number of characters.</param>
public readonly record struct TextRange(int Start, int Length)
{
    /// <summary>Index after the last character.</summary>
    public int End => Start + Length;

    /// <summary>True if the range contains no characters.</summary>
    public bool IsEmpty => Length <= 0;
}

/// <summary>
/// The text of a page for search, selection and copying: the characters of the display list in
/// content order, assembled into lines. Spaces and line breaks are derived from the gaps (many
/// PDFs set words individually, without spaces); characters drawn twice (bold by overprinting)
/// count once. Every character of the text knows its glyph, so that hits and selections become
/// quadrilaterals on the page.
/// </summary>
/// <remarks>
/// For almost all files the content order is the reading order (as in other readers); columns are not
/// reordered. Invisible text (OCR layer) is included. Immutable after creation, apart from a search text built on first use.
/// </remarks>
public sealed class PageText
{
    /// <summary>Gap (in font sizes) from which a space is inserted between two characters.</summary>
    private const double SpaceGap = 0.15;

    /// <summary>Offset across the line (in font sizes) up to which it is the same line (superscript/subscript).</summary>
    private const double LineTolerance = 0.6;

    /// <summary>A page without text.</summary>
    public static readonly PageText Empty = Create([]);

    private readonly TextGlyph[] _glyphs;
    private readonly int[] _glyphOfChar;     // per character its glyph; -1 = inserted space or line break
    private readonly int[] _firstChar;       // per glyph its first character; -1 = skipped (duplicate)
    private readonly Line[] _lines;
    private (string Text, int[]? Map)? _searchText;

    /// <summary>A line: contiguous glyphs and their bounds (for fast hit testing).</summary>
    private readonly record struct Line(int FirstGlyph, int LastGlyph, RectD Bounds);

    private PageText(TextGlyph[] glyphs, string text, int[] glyphOfChar, int[] firstChar, Line[] lines)
    {
        _glyphs = glyphs;
        Text = text;
        _glyphOfChar = glyphOfChar;
        _firstChar = firstChar;
        _lines = lines;
    }

    /// <summary>Text of the page; lines separated by '\n'.</summary>
    public string Text { get; }

    /// <summary>True if the page has no text.</summary>
    public bool IsEmpty => Text.Length == 0;

    /// <summary>Approximate memory use in bytes (glyphs, text, mappings, lines).</summary>
    public long EstimatedBytes =>
        _glyphs.Length * (long)System.Runtime.CompilerServices.Unsafe.SizeOf<TextGlyph>() + Text.Length * 4L   // text and search text
        + _glyphOfChar.Length * 4L + _firstChar.Length * 4L + _lines.Length * 40L + 120;

    /// <summary>Assembles the text from the characters of a display list.</summary>
    /// <param name="glyphs">The characters in content order (<see cref="DisplayList.Text"/>); the array is kept, not copied (unless pieces
    /// of lines are joined, see <see cref="Stitched"/>).</param>
    /// <returns>The page text.</returns>
    public static PageText Create(TextGlyph[] glyphs) => Create(glyphs, 0);

    /// <summary>Assembles the text, noting how many characters the page draws without readable text (<see cref="UnreadableGlyphs"/>).</summary>
    /// <param name="glyphs">The characters, as for <see cref="Create(TextGlyph[])"/>.</param>
    /// <param name="unreadableGlyphs">Characters drawn without readable text (<see cref="DisplayList.UnreadableGlyphs"/>).</param>
    /// <param name="misreadGlyphs">Characters with suspicious text (<see cref="DisplayList.MisreadGlyphs"/>).</param>
    /// <returns>The page text.</returns>
    public static PageText Create(TextGlyph[] glyphs, int unreadableGlyphs, int misreadGlyphs = 0)
    {
        glyphs = Stitched(glyphs);
        var text = new StringBuilder(glyphs.Length + glyphs.Length / 5);
        var glyphOfChar = new List<int>(glyphs.Length + glyphs.Length / 5);
        var firstChar = new int[glyphs.Length];
        var lines = new List<Line>();
        var previous = -1;
        GlyphGeometry prev = default;   // geometry of the previous glyph, computed once per glyph rather than twice
        var lineStart = 0;
        var lineBounds = RectD.Empty;

        void Append(char c, int glyph)
        {
            text.Append(c);
            glyphOfChar.Add(glyph);
        }

        void EndLine(int lastGlyph)
        {
            if (previous >= 0) lines.Add(new Line(lineStart, lastGlyph, lineBounds));
        }

        for (var i = 0; i < glyphs.Length; i++)
        {
            var g = glyphs[i];
            var cur = Geometry(g);
            if (previous >= 0)
            {
                var tolerance = Math.Max(Math.Max(prev.Size, cur.Size), 0.1);
                // overprinted (synthetic bold, shadow): the same character at almost the same position
                if (g.Text == glyphs[previous].Text && Distance(cur.StartX, cur.StartY, prev.StartX, prev.StartY) < 0.15 * tolerance)
                {
                    firstChar[i] = -1;
                    continue;
                }
                var dx = cur.StartX - prev.EndX;
                var dy = cur.StartY - prev.EndY;
                var along = dx * prev.DirX + dy * prev.DirY;
                var across = Math.Abs(prev.DirX * dy - prev.DirY * dx);
                var sameLine = cur.DirX * prev.DirX + cur.DirY * prev.DirY > 0.9
                               && across < LineTolerance * tolerance
                               && along > -0.5 * tolerance;
                if (sameLine)
                {
                    if (along > SpaceGap * tolerance && !EndsWithSpace(text) && !char.IsWhiteSpace(g.Text[0])) Append(' ', -1);
                }
                else
                {
                    EndLine(previous);
                    lineStart = i;
                    lineBounds = RectD.Empty;
                    Append('\n', -1);
                }
            }
            firstChar[i] = text.Length;
            foreach (var c in g.Text)
            {
                if (Ligature(c) is { } letters)
                    foreach (var l in letters) Append(l, i);
                else
                    Append(c, i);
            }
            lineBounds = lineBounds.Union(Quad(g).Bounds);
            previous = i;
            prev = cur;
        }
        EndLine(previous);
        return new PageText(glyphs, text.ToString(), [.. glyphOfChar], firstChar, [.. lines]) { UnreadableGlyphs = unreadableGlyphs, MisreadGlyphs = misreadGlyphs };
    }

    /// <summary>Characters the page draws without readable text: no search finds them (docs/RECHERCHE-VERSTECKEN.md, B-6).</summary>
    public int UnreadableGlyphs { get; private init; }

    private PageText? _inPlace;
    private bool _inPlaceDone;

    /// <summary>
    /// The text read in the order of the places along each line, for pages whose content draws a line out of order (from right to left, word
    /// by word backwards, each character at a place of its own — a trick against redaction, docs/FEHLERGEDAECHTNIS.md, No. 267); null if no
    /// line is. „Suchen und schwärzen“ searches it as well: the text in content order stays for reading, selecting and copying, because on
    /// ordinary pages the content order is the better one (columns and notes in the margin on the same baseline, overlapping characters).
    /// </summary>
    public PageText? InPlace
    {
        get
        {
            if (_inPlaceDone) return _inPlace;
            var ordered = InPlaceOrder(_glyphs);
            _inPlace = ReferenceEquals(ordered, _glyphs) ? null : Create(ordered, UnreadableGlyphs, MisreadGlyphs);
            _inPlaceDone = true;
            return _inPlace;
        }
    }

    /// <summary>Characters whose read text is suspicious: a search may not find what they show (docs/RECHERCHE-VERSTECKEN.md, B-5).</summary>
    public int MisreadGlyphs { get; private init; }

    /// <summary>
    /// Lines whose characters the content draws out of order — from right to left, word by word backwards, each character at a place of its
    /// own (a trick against redaction: the text looks right, but reads scrambled, and a search does not find it; docs/RECHERCHE-VERSTECKEN.md,
    /// B-5, docs/FEHLERGEDAECHTNIS.md, No. 267; used for <see cref="InPlace"/>): each run of characters on one baseline in which the content steps back by more than
    /// <see cref="BackStep"/> font sizes is put in the order of the places along the line. Overprinted copies, kerning and accents drawn after
    /// their letter step back less and keep the content order; so does text in a right-to-left script (its content order is the reading
    /// order). Without such runs the array is returned as it is.
    /// </summary>
    private static TextGlyph[] InPlaceOrder(TextGlyph[] glyphs)
    {
        TextGlyph[]? result = null;
        var start = 0;
        var backwards = false;
        var prev = glyphs.Length > 0 ? Geometry(glyphs[0]) : default;
        for (var i = 1; i <= glyphs.Length; i++)
        {
            var cur = i < glyphs.Length ? Geometry(glyphs[i]) : default;
            var tolerance = Math.Max(Math.Max(prev.Size, cur.Size), 0.1);
            var dx = cur.StartX - prev.EndX;
            var dy = cur.StartY - prev.EndY;
            var across = Math.Abs(prev.DirX * (cur.StartY - prev.StartY) - prev.DirY * (cur.StartX - prev.StartX));
            var sameBaseline = i < glyphs.Length && cur.DirX * prev.DirX + cur.DirY * prev.DirY > 0.9 && across < LineTolerance * tolerance;
            if (sameBaseline)
            {
                var overprinted = glyphs[i].Text == glyphs[i - 1].Text && Distance(cur.StartX, cur.StartY, prev.StartX, prev.StartY) < 0.15 * tolerance;
                if (!overprinted && dx * prev.DirX + dy * prev.DirY < -BackStep * tolerance) backwards = true;
                prev = cur;
                continue;
            }
            if (backwards && i - start > 1 && !RightToLeft(glyphs, start, i))
            {
                result ??= (TextGlyph[])glyphs.Clone();
                Reorder(glyphs, start, i, result);
            }
            start = i;
            backwards = false;
            prev = cur;
        }
        return result ?? glyphs;
    }

    /// <summary>
    /// Puts a run in the order of the places along its line, kept apart where a gap of more than <see cref="ClusterGap"/> font sizes lies
    /// between two places: such groups are columns, labels or notes in the margin on the same baseline — they follow each other in the
    /// order the content draws them (as before), only within each group the places count.
    /// </summary>
    private static void Reorder(TextGlyph[] glyphs, int start, int end, TextGlyph[] result)
    {
        var first = Geometry(glyphs[start]);
        var sorted = Enumerable.Range(start, end - start).Select(i => (Index: i, At: Geometry(glyphs[i])))
            .OrderBy(g => g.At.StartX * first.DirX + g.At.StartY * first.DirY).ToList();
        var groups = new List<List<int>> { new() { sorted[0].Index } };
        for (var k = 1; k < sorted.Count; k++)
        {
            var (prev, cur) = (sorted[k - 1].At, sorted[k].At);
            var gap = (cur.StartX - prev.EndX) * first.DirX + (cur.StartY - prev.EndY) * first.DirY;
            if (gap > ClusterGap * Math.Max(Math.Max(prev.Size, cur.Size), 0.1)) groups.Add([]);
            groups[^1].Add(sorted[k].Index);
        }
        var at = start;
        foreach (var group in groups.OrderBy(g => g.Min()))
            foreach (var index in group) result[at++] = glyphs[index];
    }

    /// <summary>Gap along a line (in font sizes) from which places on one baseline count as separate groups (<see cref="Reorder"/>).</summary>
    private const double ClusterGap = 1.0;

    /// <summary>How far back (in font sizes) the content may step within a line and still be read in content order (<see cref="InPlaceOrder"/>).</summary>
    private const double BackStep = 0.3;

    /// <summary>Whether the glyphs hold characters of a right-to-left script (Hebrew, Arabic and their neighbours).</summary>
    private static bool RightToLeft(TextGlyph[] glyphs, int start, int end)
    {
        for (var i = start; i < end; i++)
            foreach (var c in glyphs[i].Text)
                if (c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF' or '\uD802' or '\uD803')
                    return true;
        return false;
    }

    /// <summary>Whether a glyph continues the line of the previous one (as in <see cref="Create"/>: same direction, across within
    /// <see cref="LineTolerance"/>, not far back).</summary>
    private static bool SameLine(GlyphGeometry prev, GlyphGeometry cur, double tolerance)
    {
        var dx = cur.StartX - prev.EndX;
        var dy = cur.StartY - prev.EndY;
        var along = dx * prev.DirX + dy * prev.DirY;
        var across = Math.Abs(prev.DirX * dy - prev.DirY * dx);
        return cur.DirX * prev.DirX + cur.DirY * prev.DirY > 0.9 && across < LineTolerance * tolerance && along > -0.5 * tolerance;
    }

    /// <summary>
    /// Pieces of lines that continue each other on the page but not in the content: a page drawn as clipped tiles, each tile with its
    /// part of every line ("func" in one tile, "tion ignores" in the next). Each piece is put behind the piece whose end it touches,
    /// so that the words are whole again for search, selection and redaction. Only pieces that touch — no gap a space would fill
    /// (<see cref="SpaceGap"/>) — on the same baseline, in the same direction and size: columns and table cells, which always have a
    /// gap, keep the content order. Without such pieces the array is returned as it is.
    /// </summary>
    private static TextGlyph[] Stitched(TextGlyph[] glyphs)
    {
        if (glyphs.Length < 2) return glyphs;
        // Pieces: runs of glyphs on one line in content order, as the assembly sees them (overprinted copies stay in their piece)
        var pieces = new List<(int First, int Last)>();
        var first = 0;
        var prev = Geometry(glyphs[0]);
        for (var i = 1; i < glyphs.Length; i++)
        {
            var cur = Geometry(glyphs[i]);
            var tolerance = Math.Max(Math.Max(prev.Size, cur.Size), 0.1);
            var overprinted = glyphs[i].Text == glyphs[i - 1].Text && Distance(cur.StartX, cur.StartY, prev.StartX, prev.StartY) < 0.15 * tolerance;
            if (!overprinted && !SameLine(prev, cur, tolerance))
            {
                pieces.Add((first, i - 1));
                first = i;
            }
            prev = cur;
        }
        pieces.Add((first, glyphs.Length - 1));
        if (pieces.Count < 2) return glyphs;

        // Starts of the pieces in a grid of 8 pt cells; an end looks in the cells around it
        const double cell = 8;
        static long Key(double x, double y) => (long)Math.Floor(x / cell) << 32 ^ (uint)(int)Math.Floor(y / cell);
        var starts = new Dictionary<long, List<int>>();
        for (var p = 0; p < pieces.Count; p++)
        {
            var s = Geometry(glyphs[pieces[p].First]);
            if (!double.IsFinite(s.StartX) || !double.IsFinite(s.StartY)) continue;
            var key = Key(s.StartX, s.StartY);
            if (!starts.TryGetValue(key, out var list)) starts[key] = list = [];
            list.Add(p);
        }
        var next = new int[pieces.Count];
        var hasPrevious = new bool[pieces.Count];
        Array.Fill(next, -1);
        var linked = false;
        for (var p = 0; p < pieces.Count; p++)
        {
            var end = Geometry(glyphs[pieces[p].Last]);
            if (!double.IsFinite(end.EndX) || !double.IsFinite(end.EndY)) continue;
            var best = -1;
            var bestScore = double.MaxValue;
            var cx = (long)Math.Floor(end.EndX / cell);
            var cy = (long)Math.Floor(end.EndY / cell);
            for (var gx = cx - 1; gx <= cx + 1; gx++)
                for (var gy = cy - 1; gy <= cy + 1; gy++)
                {
                    if (!starts.TryGetValue(gx << 32 ^ (uint)(int)gy, out var candidates)) continue;
                    foreach (var q in candidates)
                    {
                        if (q == p || hasPrevious[q]) continue;
                        var start = Geometry(glyphs[pieces[q].First]);
                        var tolerance = Math.Max(Math.Max(end.Size, start.Size), 0.1);
                        if (start.DirX * end.DirX + start.DirY * end.DirY < 0.99 || Math.Abs(start.Size - end.Size) > 0.05 * tolerance) continue;
                        var dx = start.StartX - end.EndX;
                        var dy = start.StartY - end.EndY;
                        var along = dx * end.DirX + dy * end.DirY;
                        var across = Math.Abs(end.DirX * dy - end.DirY * dx);
                        // touching (cut inside a word), or the glyph at the edge drawn by both tiles
                        var touches = along > -0.5 * tolerance && along < SpaceGap * tolerance;
                        var doubled = glyphs[pieces[q].First].Text == glyphs[pieces[p].Last].Text
                                      && Distance(start.StartX, start.StartY, end.StartX, end.StartY) < 0.15 * tolerance;
                        if (across > 0.2 * tolerance || !touches && !doubled) continue;
                        var score = Math.Abs(along) + across;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = q;
                        }
                    }
                }
            if (best < 0) continue;
            next[p] = best;
            hasPrevious[best] = true;
            linked = true;
        }
        if (!linked) return glyphs;

        // Chains in the content order of their first piece; pieces in a loop (cannot happen on a page, but crafted values) follow
        // in content order
        var result = new TextGlyph[glyphs.Length];
        var count = 0;
        var done = new bool[pieces.Count];
        void Emit(int p)
        {
            for (; p >= 0 && !done[p]; p = next[p])
            {
                done[p] = true;
                for (var i = pieces[p].First; i <= pieces[p].Last; i++) result[count++] = glyphs[i];
            }
        }
        for (var p = 0; p < pieces.Count; p++)
            if (!hasPrevious[p]) Emit(p);
        for (var p = 0; p < pieces.Count; p++) Emit(p);
        return result;
    }

    /// <summary>Ligatures (U+FB00–FB06) as single letters; otherwise "floating" would not find "ﬂoating".</summary>
    private static string? Ligature(char c) => c switch
    {
        'ﬀ' => "ff",
        'ﬁ' => "fi",
        'ﬂ' => "fl",
        'ﬃ' => "ffi",
        'ﬄ' => "ffl",
        'ﬅ' or 'ﬆ' => "st",
        _ => null,
    };

    private static bool EndsWithSpace(StringBuilder text) => text.Length > 0 && char.IsWhiteSpace(text[^1]);

    // ------------------------------------------------------------ Search

    /// <summary>Finds all occurrences of <paramref name="query"/>; white space counts as one space, line breaks as spaces.</summary>
    /// <param name="query">The search term; normalised with <see cref="NormalizeQuery"/>.</param>
    /// <param name="matchCase">Distinguish upper and lower case (otherwise ordinal, ignoring case).</param>
    /// <returns>Non-overlapping hits in text order; empty for an empty term.</returns>
    public List<TextRange> Find(string query, bool matchCase = false)
    {
        var result = new List<TextRange>();
        var needle = NormalizeQuery(query);
        if (needle.Length == 0) return result;
        var (hay, map) = _searchText ??= SearchText(Text);
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var pos = 0; pos <= hay.Length - needle.Length;)
        {
            var index = hay.IndexOf(needle, pos, comparison);
            if (index < 0) break;
            result.Add(InText(index, needle.Length, map));
            pos = index + needle.Length;
        }
        return result;
    }

    /// <summary>
    /// Finds many terms in one pass (redaction: dozens of names in hundreds of pages), instead of running each term through the text
    /// separately. The result per term is the same as with <see cref="Find"/>. Candidates are found through the first (up to three)
    /// characters and checked with the same comparison as in <see cref="Find"/>.
    /// </summary>
    /// <param name="queries">Already prepared with <see cref="NormalizeQuery"/>, not empty.</param>
    /// <param name="matchCase">Distinguish upper and lower case.</param>
    /// <returns>One list of non-overlapping hits per term, in the order of <paramref name="queries"/>.</returns>
    public List<TextRange>[] FindAll(IReadOnlyList<string> queries, bool matchCase = false)
    {
        var results = new List<TextRange>[queries.Count];
        for (var q = 0; q < queries.Count; q++) results[q] = [];
        if (queries.Count == 0) return results;
        var (hay, map) = _searchText ??= SearchText(Text);
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        // one table per prefix length (1, 2, 3): key from the first characters → terms
        var byPrefix = new Dictionary<ulong, List<int>>?[4];
        for (var q = 0; q < queries.Count; q++)
        {
            var length = Math.Min(3, queries[q].Length);
            if (length == 0) continue;
            var table = byPrefix[length] ??= [];
            var key = Key(queries[q], 0, length, matchCase);
            if (!table.TryGetValue(key, out var list)) table[key] = list = [];
            list.Add(q);
        }
        var next = new int[queries.Count];   // per term: hits do not overlap (as in Find)
        for (var pos = 0; pos < hay.Length; pos++)
            for (var length = 1; length <= 3 && pos + length <= hay.Length; length++)
            {
                if (byPrefix[length] is not { } table || !table.TryGetValue(Key(hay, pos, length, matchCase), out var candidates)) continue;
                foreach (var q in candidates)
                {
                    var needle = queries[q];
                    if (pos < next[q] || pos + needle.Length > hay.Length || string.Compare(hay, pos, needle, 0, needle.Length, comparison) != 0) continue;
                    results[q].Add(InText(pos, needle.Length, map));
                    next[q] = pos + needle.Length;
                }
            }
        return results;

        static ulong Key(string s, int start, int length, bool matchCase)
        {
            ulong key = (ulong)length;
            for (var i = 0; i < length; i++) key = key << 16 | (matchCase ? s[start + i] : char.ToUpperInvariant(s[start + i]));
            return key;
        }
    }

    /// <summary>
    /// The text searches run over (docs/FEHLERGEDAECHTNIS.md, No. 355): line breaks as spaces; letters with separate accents (u and a
    /// combining diaeresis, as some producers map them) composed, so that „Mündung“ finds them; soft hyphens left out; a hyphen at the end of
    /// a line before a small letter joins the word („Muster-“ / „mann“ is found as „Mustermann“), any other one keeps the hyphen without the
    /// break („Max-“ / „Planck“ as „Max-Planck“). <c>Map</c> gives, for each character of it, its place in <see cref="Text"/> (and one more
    /// entry: the end) — null if the text is the page text itself.
    /// </summary>
    private static (string Text, int[]? Map) SearchText(string text)
    {
        static bool Mark(char c) => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.EnclosingMark;
        if (!text.Contains('\u00AD') && !text.Contains("-\n", StringComparison.Ordinal) && !text.Any(Mark)) return (text.Replace('\n', ' '), null);
        var sb = new StringBuilder(text.Length);
        var map = new List<int>(text.Length + 1);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\u00AD') continue;
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                if (!(i > 0 && char.IsLetter(text[i - 1]) && i + 2 < text.Length && char.IsLower(text[i + 2])))
                {
                    sb.Append('-');
                    map.Add(i);
                }
                i++;
                continue;
            }
            if (i + 1 < text.Length && Mark(text[i + 1]))
            {
                var j = i + 1;
                while (j < text.Length && Mark(text[j])) j++;
                foreach (var composed in text.AsSpan(i, j - i).ToString().Normalize(NormalizationForm.FormC))
                {
                    sb.Append(composed);
                    map.Add(i);
                }
                i = j - 1;
                continue;
            }
            sb.Append(c == '\n' ? ' ' : c);
            map.Add(i);
        }
        map.Add(text.Length);
        return (sb.ToString(), [.. map]);
    }

    /// <summary>A hit in the search text as a range of <see cref="Text"/>.</summary>
    private static TextRange InText(int start, int length, int[]? map) =>
        map is null ? new TextRange(start, length) : new TextRange(map[start], map[start + length] - map[start]);

    /// <summary>Collapses white space in the search term into single spaces and trims it.</summary>
    /// <param name="query">The term as entered.</param>
    /// <returns>The normalised term; empty if it consists only of white space.</returns>
    public static string NormalizeQuery(string query)
    {
        var sb = new StringBuilder(query.Length);
        // composed as the search text (letters with separate accents), without soft hyphens
        foreach (var c in query.Normalize(NormalizationForm.FormC).Replace("\u00AD", "", StringComparison.Ordinal).Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ Quadrilaterals

    /// <summary>Quadrilaterals for a range: one per line (from the first to the last glyph).</summary>
    /// <param name="range">The range; clamped to the text.</param>
    /// <returns>The quadrilaterals in page space.</returns>
    public List<TextQuad> Quads(TextRange range)
    {
        return QuadsCore(range);
    }

    /// <summary>The characters of a range (each once, in the order of the text).</summary>
    /// <param name="range">The range in <see cref="Text"/>.</param>
    /// <returns>The characters.</returns>
    public IEnumerable<TextGlyph> GlyphsOf(TextRange range)
    {
        var last = -1;
        for (var c = Math.Max(0, range.Start); c < Math.Min(range.End, Text.Length); c++)
            if (_glyphOfChar[c] is var g and >= 0 && g != last)
            {
                last = g;
                yield return _glyphs[g];
            }
    }

    private List<TextQuad> QuadsCore(TextRange range)
    {
        var result = new List<TextQuad>();
        int first = -1, last = -1;
        var end = Math.Min(range.End, Text.Length);
        for (var c = Math.Max(0, range.Start); c < end; c++)
        {
            var g = _glyphOfChar[c];
            if (g < 0)
            {
                if (Text[c] == '\n') Flush();
                continue;
            }
            if (first < 0) first = g;
            last = g;
        }
        Flush();
        return result;

        void Flush()
        {
            if (first < 0) return;
            result.Add(Span(_glyphs[first], _glyphs[last]));
            first = last = -1;
        }
    }

    /// <summary>
    /// Areas to redact a text range („Text schwärzen“, „Suchen und schwärzen“): one rectangle per line, enlarged by
    /// <paramref name="pad"/>, but not into the neighbouring lines — where the box of a line reaches into the line above or below
    /// (headings with tight leading, fonts with tall boxes), the area ends halfway through the overlap. A bar over one line removed
    /// characters of the next and covered them (docs/FEHLERGEDAECHTNIS.md, No. 151). At least the middle half of the line stays covered.
    /// A slanted line is covered by a staircase of thin horizontal strips, each as wide as the line in its height: one rectangle
    /// around it would reach far into the neighbouring lines, and cut back to its middle it left the ends of the word uncovered
    /// (docs/FEHLERGEDAECHTNIS.md, No. 156).
    /// </summary>
    /// <param name="range">The range.</param>
    /// <param name="pad">Margin in points around the characters.</param>
    /// <returns>The areas in page space; empty if the range has no visible glyphs.</returns>
    public List<PdfRectangle> RedactionAreas(TextRange range, double pad = 0.5)
    {
        var result = new List<PdfRectangle>();
        var quads = Quads(range).Where(q => q.Bounds is { Width: > 0, Height: > 0 }).ToList();
        if (quads.Count == 0) return result;
        var lines = Quads(new TextRange(0, Text.Length)).Select(q => q.Bounds).Where(b => b.Width > 0 && b.Height > 0).ToList();
        // the direction the text runs (vertical writing runs from top to bottom of its glyphs)
        var turned = GlyphsOf(range).FirstOrDefault() is var g && (g.Vertical ? Math.Abs(g.Y3 - g.Y0) > Math.Abs(g.X3 - g.X0) : Math.Abs(g.Y1 - g.Y0) > Math.Abs(g.X1 - g.X0));
        foreach (var q in quads)
        {
            if (!Upright(q))
            {
                Staircase(q, pad, result);
                continue;
            }
            var r = q.Bounds;
            // a line running up or down the page (turned by 90°, or vertical writing): its neighbours lie beside it in x (before, the line
            // itself counted as the neighbour above, and the bar ended in the middle of the word — the last letters stayed;
            // docs/FEHLERGEDAECHTNIS.md, No. 274)
            if (turned)
            {
                result.Add(TurnedArea(r, lines, pad));
                continue;
            }
            // RectD: Top is the lower y in page space, Bottom the higher one
            double low = r.Top - pad, high = r.Bottom + pad, middle = (r.Top + r.Bottom) / 2;
            double keepLow = r.Top + r.Height * 0.25, keepHigh = r.Bottom - r.Height * 0.25;
            foreach (var line in lines)
            {
                if (line.Right <= r.Left || line.Left >= r.Right) continue;                // not beside it
                var centre = (line.Top + line.Bottom) / 2;
                if (centre >= r.Top && centre <= r.Bottom) continue;                       // the same line
                if (centre > middle && line.Top < high) high = Math.Max(keepHigh, Math.Min(high, (line.Top + r.Bottom) / 2));
                if (centre < middle && line.Bottom > low) low = Math.Min(keepLow, Math.Max(low, (line.Bottom + r.Top) / 2));
            }
            result.Add(new PdfRectangle(r.Left - pad, low, r.Right + pad, high));
        }
        return result;
    }

    /// <summary>The area of a line running up or down the page (turned by 90°), not into the lines left and right of it (as in
    /// <see cref="RedactionAreas"/> across the line).</summary>
    private static PdfRectangle TurnedArea(RectD r, List<RectD> lines, double pad)
    {
        double low = r.Left - pad, high = r.Right + pad, middle = (r.Left + r.Right) / 2;
        double keepLow = r.Left + r.Width * 0.25, keepHigh = r.Right - r.Width * 0.25;
        foreach (var line in lines)
        {
            if (line.Bottom <= r.Top || line.Top >= r.Bottom) continue;                    // not beside it
            var centre = (line.Left + line.Right) / 2;
            if (centre >= r.Left && centre <= r.Right) continue;                           // the same line
            if (centre > middle && line.Left < high) high = Math.Max(keepHigh, Math.Min(high, (line.Left + r.Right) / 2));
            if (centre < middle && line.Right > low) low = Math.Min(keepLow, Math.Max(low, (line.Right + r.Left) / 2));
        }
        return new PdfRectangle(low, r.Top - pad, high, r.Bottom + pad);
    }

    /// <summary>
    /// The text of the line is not turned: a side of a single glyph runs along an axis (within about a tenth of a degree, or a hundredth
    /// of a point) — the left side of the first glyph of a horizontal line, the lower side of the last of a vertical one (<see cref="Span"/>).
    /// The quadrilateral as a whole is no rectangle if the glyphs differ in size; that is no slant (a word 1400 pt high joined with a
    /// small one was covered with strips, docs/FEHLERGEDAECHTNIS.md, No. 160).
    /// </summary>
    private static bool Upright(TextQuad q)
    {
        static bool Axis(double dx, double dy) => Math.Min(Math.Abs(dx), Math.Abs(dy)) <= Math.Max(0.01, Math.Max(Math.Abs(dx), Math.Abs(dy)) * 2e-3);
        return Axis(q.X3 - q.X0, q.Y3 - q.Y0) || Axis(q.X1 - q.X0, q.Y1 - q.Y0);
    }

    /// <summary>
    /// Covers a slanted quadrilateral with horizontal strips of a third of its thickness (the shorter distance across it): per strip,
    /// the part of the quadrilateral in its height, enlarged by <paramref name="pad"/> sideways (and at the ends upwards and downwards).
    /// </summary>
    private static void Staircase(TextQuad q, double pad, List<PdfRectangle> result)
    {
        (double X, double Y)[] corners = [(q.X0, q.Y0), (q.X1, q.Y1), (q.X2, q.Y2), (q.X3, q.Y3)];
        static double Length(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);
        // thickness: the area divided by the longer side
        var area = Math.Abs((q.X1 - q.X0) * (q.Y3 - q.Y0) - (q.Y1 - q.Y0) * (q.X3 - q.X0));
        var longer = Math.Max(Length(q.X1 - q.X0, q.Y1 - q.Y0), Length(q.X3 - q.X0, q.Y3 - q.Y0));
        var bounds = q.Bounds;
        var step = Math.Max(longer > 0 ? area / longer / 3 : bounds.Height, 0.25);
        var count = (int)Math.Clamp(Math.Ceiling(bounds.Height / step), 1, 400);
        step = bounds.Height / count;
        for (var i = 0; i < count; i++)
        {
            double y0 = bounds.Top + i * step, y1 = i == count - 1 ? bounds.Bottom : y0 + step;
            double left = double.MaxValue, right = double.MinValue;
            for (var k = 0; k < 4; k++)
            {
                var (ax, ay) = corners[k];
                var (bx, by) = corners[(k + 1) % 4];
                if (ay >= y0 && ay <= y1) (left, right) = (Math.Min(left, ax), Math.Max(right, ax));
                // where the side crosses the bottom and the top of the strip
                foreach (var y in (ReadOnlySpan<double>)[y0, y1])
                    if (ay != by && (y - ay) * (y - by) <= 0)
                    {
                        var x = ax + (bx - ax) * (y - ay) / (by - ay);
                        (left, right) = (Math.Min(left, x), Math.Max(right, x));
                    }
            }
            if (left > right) continue;
            result.Add(new PdfRectangle(left - pad, i == 0 ? y0 - pad : y0, right + pad, i == count - 1 ? y1 + pad : y1));
        }
    }

    /// <summary>Quadrilateral from the first to the last glyph of a line.</summary>
    private static TextQuad Span(TextGlyph a, TextGlyph b) => a.Vertical
        ? new TextQuad(b.X0, b.Y0, b.X1, b.Y1, a.X2, a.Y2, a.X3, a.Y3)
        : new TextQuad(a.X0, a.Y0, b.X1, b.Y1, b.X2, b.Y2, a.X3, a.Y3);

    private static TextQuad Quad(TextGlyph g) => new(g.X0, g.Y0, g.X1, g.Y1, g.X2, g.Y2, g.X3, g.Y3);

    // ------------------------------------------------------------ Hit testing and selection

    /// <summary>Tests whether a point lies on a character.</summary>
    /// <param name="x">x in page space.</param>
    /// <param name="y">y in page space.</param>
    /// <param name="margin">Tolerance around the characters, in page units.</param>
    /// <returns>True if the point lies within <paramref name="margin"/> of a character.</returns>
    public bool IsOverText(double x, double y, double margin = 0)
    {
        foreach (var line in _lines)
        {
            if (DistanceToRect(line.Bounds, x, y) > margin) continue;
            for (var i = line.FirstGlyph; i <= line.LastGlyph; i++)
                if (_firstChar[i] >= 0 && DistanceToQuad(Quad(_glyphs[i]), x, y) <= margin) return true;
        }
        return false;
    }

    /// <summary>
    /// Caret position (character position 0 … Text.Length) for a point in page space: at the
    /// nearest glyph, before or after it depending on the half.
    /// </summary>
    /// <param name="x">x in page space.</param>
    /// <param name="y">y in page space.</param>
    /// <returns>The caret position; -1 without text.</returns>
    public int CaretAt(double x, double y)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        foreach (var line in _lines)
        {
            // skip lines that as a whole are already further away
            if (DistanceToRect(line.Bounds, x, y) > bestDistance) continue;
            for (var i = line.FirstGlyph; i <= line.LastGlyph; i++)
            {
                if (_firstChar[i] < 0) continue;
                var d = DistanceToQuad(Quad(_glyphs[i]), x, y);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
        }
        if (best < 0) return -1;
        var g = _glyphs[best];
        var geo = Geometry(g);
        var length = Distance(geo.StartX, geo.StartY, geo.EndX, geo.EndY);
        var t = length > 0 ? ((x - geo.StartX) * geo.DirX + (y - geo.StartY) * geo.DirY) / length : 0;
        return t > 0.5 ? _firstChar[best] + g.Text.Length : _firstChar[best];
    }

    /// <summary>Word at a caret position (double click): everything up to the next white space.</summary>
    /// <param name="caret">The caret position.</param>
    /// <returns>The word; empty on white space or without text.</returns>
    public TextRange WordAt(int caret)
    {
        if (Text.Length == 0 || caret < 0) return default;
        var i = Math.Min(caret, Text.Length - 1);
        if (char.IsWhiteSpace(Text[i]) && i > 0) i--;
        if (char.IsWhiteSpace(Text[i])) return default;
        int start = i, end = i + 1;
        while (start > 0 && !char.IsWhiteSpace(Text[start - 1])) start--;
        while (end < Text.Length && !char.IsWhiteSpace(Text[end])) end++;
        return new TextRange(start, end - start);
    }

    /// <summary>Whether the character belongs to invisible text (text rendering modes 3 and 7, for example an OCR layer).</summary>
    /// <param name="index">Position in <see cref="Text"/>.</param>
    /// <returns>false for visible characters and for inserted spaces and line breaks.</returns>
    public bool IsInvisible(int index) =>
        (uint)index < (uint)_glyphOfChar.Length && _glyphOfChar[index] is >= 0 and var g && !_glyphs[g].Visible;

    /// <summary>Line at a caret position (triple click).</summary>
    /// <param name="caret">The caret position.</param>
    /// <returns>The line without its line break; empty without text.</returns>
    public TextRange LineAt(int caret)
    {
        if (Text.Length == 0 || caret < 0) return default;
        var i = Math.Min(caret, Text.Length - 1);
        if (Text[i] == '\n' && i > 0) i--;
        var start = Text.LastIndexOf('\n', i) + 1;
        var end = Text.IndexOf('\n', i);
        if (end < 0) end = Text.Length;
        return new TextRange(start, end - start);
    }

    /// <summary>Selection between two caret positions (in any order).</summary>
    /// <param name="caretA">One caret position.</param>
    /// <param name="caretB">The other caret position.</param>
    /// <returns>The range; empty if either position is negative.</returns>
    public static TextRange Between(int caretA, int caretB) =>
        caretA < 0 || caretB < 0 ? default : new TextRange(Math.Min(caretA, caretB), Math.Abs(caretA - caretB));

    /// <summary>Text of a range for copying; line breaks as usual on the operating system.</summary>
    /// <param name="range">The range; clamped to the text.</param>
    /// <returns>The text with <see cref="Environment.NewLine"/> as line break.</returns>
    public string GetText(TextRange range)
    {
        var start = Math.Clamp(range.Start, 0, Text.Length);
        var end = Math.Clamp(range.End, start, Text.Length);
        return Text[start..end].Replace("\n", Environment.NewLine);
    }

    // ------------------------------------------------------------ Geometry

    /// <summary>Start and end of a glyph along the line, direction and size.</summary>
    private readonly record struct GlyphGeometry(double StartX, double StartY, double EndX, double EndY, double DirX, double DirY, double Size);

    private static GlyphGeometry Geometry(TextGlyph g)
    {
        double sx, sy, ex, ey;
        if (g.Vertical)
        {
            // vertical: from the top edge to the bottom edge
            (sx, sy) = ((g.X3 + g.X2) / 2, (g.Y3 + g.Y2) / 2);
            (ex, ey) = ((g.X0 + g.X1) / 2, (g.Y0 + g.Y1) / 2);
        }
        else
        {
            (sx, sy) = ((g.X0 + g.X3) / 2, (g.Y0 + g.Y3) / 2);
            (ex, ey) = ((g.X1 + g.X2) / 2, (g.Y1 + g.Y2) / 2);
        }
        var length = Distance(sx, sy, ex, ey);
        double dx, dy;
        if (length > 1e-9)
        {
            (dx, dy) = ((ex - sx) / length, (ey - sy) / length);
        }
        else
        {
            // width 0: direction from the baseline, otherwise horizontal
            var bx = g.X1 - g.X0;
            var by = g.Y1 - g.Y0;
            var bl = Math.Sqrt(bx * bx + by * by);
            (dx, dy) = bl > 1e-9 ? (bx / bl, by / bl) : (1.0, 0.0);
        }
        var size = g.Size > 0 ? g.Size : Distance(g.X0, g.Y0, g.X3, g.Y3);
        return new GlyphGeometry(sx, sy, ex, ey, dx, dy, size);
    }

    private static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistanceToRect(RectD r, double x, double y)
    {
        var dx = Math.Max(Math.Max(r.Left - x, 0), x - r.Right);
        var dy = Math.Max(Math.Max(r.Top - y, 0), y - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Distance to the quadrilateral; 0 inside.</summary>
    private static double DistanceToQuad(TextQuad q, double x, double y)
    {
        if (Inside(q, x, y)) return 0;
        return Math.Min(
            Math.Min(SegmentDistance(x, y, q.X0, q.Y0, q.X1, q.Y1), SegmentDistance(x, y, q.X1, q.Y1, q.X2, q.Y2)),
            Math.Min(SegmentDistance(x, y, q.X2, q.Y2, q.X3, q.Y3), SegmentDistance(x, y, q.X3, q.Y3, q.X0, q.Y0)));
    }

    /// <summary>Point inside the (convex) quadrilateral, whatever the winding direction of the corners.</summary>
    private static bool Inside(TextQuad q, double x, double y)
    {
        var c0 = Cross(q.X0, q.Y0, q.X1, q.Y1, x, y);
        var c1 = Cross(q.X1, q.Y1, q.X2, q.Y2, x, y);
        var c2 = Cross(q.X2, q.Y2, q.X3, q.Y3, x, y);
        var c3 = Cross(q.X3, q.Y3, q.X0, q.Y0, x, y);
        return (c0 >= 0 && c1 >= 0 && c2 >= 0 && c3 >= 0) || (c0 <= 0 && c1 <= 0 && c2 <= 0 && c3 <= 0);
    }

    private static double Cross(double ax, double ay, double bx, double by, double x, double y) =>
        (bx - ax) * (y - ay) - (by - ay) * (x - ax);

    private static double SegmentDistance(double x, double y, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / lengthSquared, 0, 1) : 0;
        return Distance(x, y, ax + t * dx, ay + t * dy);
    }
}
