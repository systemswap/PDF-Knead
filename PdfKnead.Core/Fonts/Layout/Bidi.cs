using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>The bidirectional character types (UAX #9, Table 4; Bidi_Class of the Unicode Character Database).</summary>
internal enum BidiClass : byte { L, R, AL, EN, ES, ET, AN, CS, NSM, BN, B, S, WS, ON, LRE, LRO, RLE, RLO, PDF, LRI, RLI, FSI, PDI }

/// <summary>
/// The Unicode Bidirectional Algorithm (UAX #9, revision 50, Unicode 16.0.0) for text the program sets itself — formatted text, plain text,
/// field values and free text: the paragraphs (P1) and their level (P2, P3, isolates skipped), the explicit embeddings, overrides and isolates
/// with the fixed directional status stack of 125 levels and its overflow counters (X1–X8), the removal of the formatting characters (X9), the
/// isolating run sequences (X10), the weak types (W1–W7), the bracket pairs with their stack of 63 (BD16, N0), the neutrals (N1, N2), the
/// implicit levels (I1, I2), and per line the reset of separators and trailing white space (L1), the reordering (L2) and the mirrored glyphs
/// (L4). Bidi classes from DerivedBidiClass.txt, mirroring pairs from BidiMirroring.txt and bracket pairs from BidiBrackets.txt (Unicode
/// 16.0.0, shipped in Fonts/Data); checked against BidiTest.txt and BidiCharacterTest.txt. Linear in the length of the text, no recursion.
/// Written from UAX #9, no code of another implementation used (docs/FEHLERGEDAECHTNIS.md, No. 910 and 1173).
/// </summary>
internal static class Bidi
{
    /// <summary>The deepest explicit embedding level (BD2: max_depth).</summary>
    internal const int MaxDepth = 125;

    /// <summary>The size of the stack that pairs brackets (BD16).</summary>
    private const int BracketStack = 63;

    private sealed record Tables(byte[][] Pages, Dictionary<int, int> Mirrors, Dictionary<int, (int Pair, bool Open)> Brackets);

    private static readonly Lazy<Tables> Data = new(Load);

    private static Tables Load()
    {
        // every code point first; then folded into pages of 256 sharing equal contents (about 500 distinct pages)
        var all = new byte[0x110000];
        foreach (var (first, last, value) in Entries("Fonts/DerivedBidiClass.txt", missing: true))
            if (ClassName(value) is { } c)
                Array.Fill(all, (byte)c, first, last - first + 1);
        var pages = new byte[0x1100][];
        var shared = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var p = 0; p < pages.Length; p++)
        {
            var key = Convert.ToBase64String(all, p << 8, 256);
            if (!shared.TryGetValue(key, out var page)) shared[key] = page = all.AsSpan(p << 8, 256).ToArray();
            pages[p] = page;
        }
        var mirrors = new Dictionary<int, int>();
        foreach (var (first, _, value) in Entries("Fonts/BidiMirroring.txt", missing: false))
            if (int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mirror)) mirrors[first] = mirror;
        var brackets = new Dictionary<int, (int, bool)>();
        foreach (var (first, _, value) in Entries("Fonts/BidiBrackets.txt", missing: false))
        {
            var fields = value.Split(';');
            if (fields.Length == 2 && int.TryParse(fields[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pair) &&
                fields[1].Trim() is "o" or "c")
                brackets[first] = (pair, fields[1].Trim() == "o");
        }
        return new Tables(pages, mirrors, brackets);
    }

    /// <summary>
    /// The entries of a file of the Unicode Character Database: code point or range, and the rest of the line up to the comment; with
    /// <paramref name="missing"/> first the defaults of the <c>@missing</c> lines (unassigned code points in right-to-left blocks).
    /// </summary>
    private static IEnumerable<(int First, int Last, string Value)> Entries(string resource, bool missing)
    {
        using var stream = typeof(Bidi).Assembly.GetManifestResourceStream(resource);
        if (stream is null) yield break;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var text = line;
            if (missing && text.StartsWith("# @missing:", StringComparison.Ordinal)) text = text["# @missing:".Length..];
            else
            {
                var hash = text.IndexOf('#');
                if (hash >= 0) text = text[..hash];
            }
            var semicolon = text.IndexOf(';');
            if (semicolon < 0) continue;
            var range = text[..semicolon].Trim();
            var dots = range.IndexOf("..", StringComparison.Ordinal);
            if (!int.TryParse(dots < 0 ? range : range[..dots], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first)) continue;
            var last = first;
            if (dots >= 0 && !int.TryParse(range[(dots + 2)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out last)) continue;
            if (first < 0 || last > 0x10FFFF || last < first) continue;
            yield return (first, last, text[(semicolon + 1)..].Trim());
        }
    }

    private static BidiClass? ClassName(string name) => name switch
    {
        "Left_To_Right" => BidiClass.L,
        "Right_To_Left" => BidiClass.R,
        "Arabic_Letter" => BidiClass.AL,
        "European_Terminator" => BidiClass.ET,
        "Boundary_Neutral" => BidiClass.BN,
        _ => Enum.TryParse<BidiClass>(name, out var c) && Enum.IsDefined(c) && name.All(char.IsAsciiLetterUpper) ? c : null,
    };

    /// <summary>The Bidi_Class of a code point (outside the code space: L).</summary>
    internal static BidiClass ClassOf(int codePoint) =>
        codePoint is >= 0 and <= 0x10FFFF ? (BidiClass)Data.Value.Pages[codePoint >> 8][codePoint & 0xFF] : BidiClass.L;

    /// <summary>
    /// Whether a character is one of the directional formatting characters (Bidi_Control: ALM, LRM, RLM, LRE … RLO, LRI … PDI): they are drawn
    /// with neither glyph nor advance, and searching passes over them (UAX #9, sections 1 and 2; docs/FEHLERGEDAECHTNIS.md, No. 1173).
    /// </summary>
    internal static bool IsControl(int c) => c is 0x061C or 0x200E or 0x200F or >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069;

    /// <summary>Whether a text consists only of directional formatting characters (<see cref="IsControl"/>) and is not empty.</summary>
    internal static bool OnlyControls(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (!IsControl(c)) return false;
        return true;
    }

    /// <summary>Whether the text holds a character that can make a level odd (R, AL, AN, RLE, RLO, RLI) — only then is the algorithm needed.</summary>
    internal static bool HasRightToLeft(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (ClassOf(rune.Value) is BidiClass.R or BidiClass.AL or BidiClass.AN or BidiClass.RLE or BidiClass.RLO or BidiClass.RLI) return true;
        return false;
    }

    /// <summary>
    /// The mirror image of a character shown right to left (L4, Bidi_Mirroring_Glyph of BidiMirroring.txt), else the character itself — also for
    /// mirrored characters without a counterpart in the file (nothing is invented for them).
    /// </summary>
    internal static int Mirrored(int c) => Data.Value.Mirrors.TryGetValue(c, out var mirror) ? mirror : c;

    /// <summary>INHIBIT SYMMETRIC SWAPPING: the characters with Bidi_Mirrored after it are shown as their names say, left and right (The Unicode
    /// Standard, 23.3.1).</summary>
    internal const char InhibitSwapping = (char)0x206A;

    /// <summary>ACTIVATE SYMMETRIC SWAPPING: the default state again, the characters with Bidi_Mirrored after it are opening and closing (L4).</summary>
    internal const char ActivateSwapping = (char)0x206B;

    /// <summary>
    /// The state of symmetric swapping after one UTF-16 unit (The Unicode Standard, 23.3.1): U+206A inhibits it, U+206B activates it again (they do
    /// not nest: the last one counts), and a paragraph separator (Bidi_Class B, P1) ends the paragraph and with it the state, as for the other
    /// formatting characters of the bidirectional text (UAX #9, 2) — each paragraph begins activated, the default state without a higher-level
    /// protocol (docs/FEHLERGEDAECHTNIS.md, No. 1215).
    /// </summary>
    internal static bool SwappingInhibitedAfter(bool inhibited, char c) => c switch
    {
        InhibitSwapping => true,
        ActivateSwapping => false,
        '\n' or '\r' or '\u001C' or '\u001D' or '\u001E' or '\u0085' or '\u2029' => false,
        _ => inhibited,
    };

    /// <summary>
    /// The embedding level of every UTF-16 unit of <paramref name="text"/>, each paragraph taken as one line (L1 at its end): paragraphs split
    /// after every paragraph separator (P1), each one's level from its first strong character outside isolates, else
    /// <paramref name="defaultLevel"/> (P2, P3). Even: left to right; odd: right to left.
    /// </summary>
    internal static byte[] Levels(string text, byte defaultLevel = 0) => Analyze(text, null, defaultLevel).LineLevels(0, text.Length);

    /// <summary>
    /// Resolves the levels of a text (UAX #9, 3.3); the reordering of each line then follows with <see cref="Analysis.LineLevels"/> and
    /// <see cref="VisualOrder"/>.
    /// </summary>
    /// <param name="text">The text in logical order.</param>
    /// <param name="direction">The paragraph level set from outside (0 or 1; HL1), or null: from the text (P2, P3).</param>
    /// <param name="defaultLevel">Without a strong character and without <paramref name="direction"/>: the paragraph level.</param>
    internal static Analysis Analyze(string text, byte? direction = null, byte defaultLevel = 0)
    {
        // interlinear annotations: the main text and each annotation ordered on their own (Unicode 23.8.3; docs/FEHLERGEDAECHTNIS.md, No. 1222)
        if (BidiAnnotations.Analyze(text, direction, defaultLevel) is { } annotated) return annotated;
        // code points, each with the index of its first UTF-16 unit
        var points = new List<int>(text.Length);
        var starts = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            starts.Add(i);
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                points.Add(char.ConvertToUtf32(text[i], text[i + 1]));
                i++;
            }
            else points.Add(text[i]);
        }
        var n = points.Count;
        var classes = new BidiClass[n];
        for (var i = 0; i < n; i++) classes[i] = ClassOf(points[i]);
        var levels = new byte[n];
        var paragraphLevels = new byte[n];
        // P1: a paragraph separator ends its paragraph and belongs to it
        for (var start = 0; start < n;)
        {
            var end = start;
            while (end < n && classes[end] != BidiClass.B) end++;
            if (end < n) end++;
            var level = Paragraph(points, classes, start, end, direction, defaultLevel, levels);
            Array.Fill(paragraphLevels, level, start, end - start);
            start = end;
        }
        // back to UTF-16 units (the low surrogate as its high one)
        var unitClasses = new BidiClass[text.Length];
        var unitLevels = new byte[text.Length];
        var unitParagraphs = new byte[text.Length];
        for (var k = 0; k < n; k++)
        {
            var to = k + 1 < n ? starts[k + 1] : text.Length;
            for (var u = starts[k]; u < to; u++)
            {
                unitClasses[u] = classes[k];
                unitLevels[u] = levels[k];
                unitParagraphs[u] = paragraphLevels[k];
            }
        }
        return new Analysis(text, unitClasses, unitLevels, unitParagraphs);
    }

    /// <summary>The resolved levels of a text before the reordering of its lines.</summary>
    internal sealed class Analysis
    {
        private readonly BidiClass[] _classes;
        private readonly byte[] _levels;
        private readonly byte[] _paragraphLevels;

        internal Analysis(string text, BidiClass[] classes, byte[] levels, byte[] paragraphLevels)
        {
            Text = text;
            _classes = classes;
            _levels = levels;
            _paragraphLevels = paragraphLevels;
            foreach (var level in levels)
                if (level % 2 == 1)
                {
                    HasOddLevel = true;
                    break;
                }
        }

        /// <summary>The text analysed.</summary>
        internal string Text { get; }

        /// <summary>Whether a character got an odd (right-to-left) level: only then does a line need reordering.</summary>
        internal bool HasOddLevel { get; }

        /// <summary>The resolved level of a UTF-16 unit (before L1).</summary>
        internal byte Level(int index) => _levels[index];

        /// <summary>The level of the paragraph a UTF-16 unit belongs to.</summary>
        internal byte ParagraphLevel(int index) => _paragraphLevels[index];

        /// <summary>
        /// Whether symmetric swapping is inhibited at the start of the UTF-16 unit <paramref name="index"/> — by a U+206A before it in its
        /// paragraph without a U+206B between (<see cref="SwappingInhibitedAfter"/>); the states are found once for the whole text, and only
        /// if it holds a U+206A.
        /// </summary>
        internal bool SwappingInhibitedBefore(int index)
        {
            if (_swapping is null)
            {
                var states = Array.Empty<bool>();
                if (Text.Contains(InhibitSwapping))
                {
                    states = new bool[Text.Length];
                    var inhibited = false;
                    for (var i = 0; i < Text.Length; i++)
                    {
                        states[i] = inhibited;
                        inhibited = SwappingInhibitedAfter(inhibited, Text[i]);
                    }
                }
                _swapping = states;
            }
            return index >= 0 && index < _swapping.Length && _swapping[index];
        }

        private bool[]? _swapping;

        /// <summary>Whether a UTF-16 unit is removed by X9 (embeddings, overrides, their terminator, BN): it takes part in no rule.</summary>
        internal bool IsRemoved(int index) => _classes[index] is BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF or BidiClass.BN;

        /// <summary>
        /// The levels of one line (UTF-16 units <paramref name="start"/> to <paramref name="end"/>) after L1: segment and paragraph
        /// separators, the white space and isolate formatting characters before them and those at the end of the line go back to the
        /// paragraph level (by their original types; the characters removed by X9 count with the white space).
        /// </summary>
        internal byte[] LineLevels(int start, int end)
        {
            start = Math.Clamp(start, 0, Text.Length);
            end = Math.Clamp(end, start, Text.Length);
            var result = _levels.AsSpan(start, end - start).ToArray();
            for (var i = start; i < end; i++)
            {
                if (_classes[i] is not (BidiClass.S or BidiClass.B)) continue;
                result[i - start] = _paragraphLevels[i];
                for (var j = i - 1; j >= start && Trailing(_classes[j]); j--) result[j - start] = _paragraphLevels[j];
            }
            for (var j = end - 1; j >= start && Trailing(_classes[j]); j--) result[j - start] = _paragraphLevels[j];
            return result;
        }

        private static bool Trailing(BidiClass c) => c is BidiClass.WS or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI or BidiClass.BN
            or BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF;
    }

    private static bool IsInitiator(BidiClass c) => c is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI;

    private static bool IsRemovedByX9(BidiClass c) => c is BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF or BidiClass.BN;

    /// <summary>
    /// P2, P3: the level of the first strong character (L: 0; R, AL: 1) between <paramref name="from"/> and <paramref name="to"/>, passing over
    /// the characters between an isolate initiator and its matching PDI (to the end without one); <paramref name="fallback"/> without one.
    /// </summary>
    private static byte FirstStrong(BidiClass[] classes, int[] matchingPdi, int from, int to, byte fallback)
    {
        for (var i = from; i < to; i++)
        {
            switch (classes[i])
            {
                case BidiClass.L: return 0;
                case BidiClass.R or BidiClass.AL: return 1;
                case BidiClass.LRI or BidiClass.RLI or BidiClass.FSI:
                    if (matchingPdi[i] < 0) return fallback;
                    i = matchingPdi[i];
                    break;
            }
        }
        return fallback;
    }

    /// <summary>Resolves one paragraph (code points <paramref name="start"/> to <paramref name="end"/>) into <paramref name="levels"/>; returns its level.</summary>
    private static byte Paragraph(List<int> allPoints, BidiClass[] allClasses, int start, int end, byte? direction, byte defaultLevel, byte[] levels)
    {
        var n = end - start;
        var original = allClasses.AsSpan(start, n).ToArray();
        var points = allPoints.GetRange(start, n);
        var level = new byte[n];

        // BD9: the matching PDI of every isolate initiator (a stack of the open initiators; at most one entry per character)
        var matchingPdi = new int[n];
        var matchingInitiator = new int[n];
        Array.Fill(matchingPdi, -1);
        Array.Fill(matchingInitiator, -1);
        var open = new int[n];
        var openCount = 0;
        for (var i = 0; i < n; i++)
        {
            if (IsInitiator(original[i])) open[openCount++] = i;
            else if (original[i] == BidiClass.PDI && openCount > 0)
            {
                var initiator = open[--openCount];
                matchingPdi[initiator] = i;
                matchingInitiator[i] = initiator;
            }
            else if (original[i] == BidiClass.B) openCount = 0;
        }

        var paragraphLevel = direction is { } d ? (byte)(d & 1) : FirstStrong(original, matchingPdi, 0, n, (byte)(defaultLevel & 1));

        // X1–X8: the directional status stack, fixed at max_depth + 2 entries
        var types = (BidiClass[])original.Clone();
        Span<byte> stackLevel = stackalloc byte[MaxDepth + 2];
        Span<byte> stackOverride = stackalloc byte[MaxDepth + 2];   // 0 neutral, 1 L, 2 R
        Span<bool> stackIsolate = stackalloc bool[MaxDepth + 2];
        var depth = 0;
        stackLevel[0] = paragraphLevel;
        depth = 1;
        int overflowIsolates = 0, overflowEmbeddings = 0, validIsolates = 0;
        for (var i = 0; i < n; i++)
        {
            var t = original[i];
            var top = depth - 1;
            switch (t)
            {
                case BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO:
                {
                    var next = t is BidiClass.RLE or BidiClass.RLO ? (stackLevel[top] + 1) | 1 : (stackLevel[top] + 2) & ~1;
                    if (next <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                    {
                        stackLevel[depth] = (byte)next;
                        stackOverride[depth] = t switch { BidiClass.LRO => 1, BidiClass.RLO => 2, _ => 0 };
                        stackIsolate[depth] = false;
                        depth++;
                    }
                    else if (overflowIsolates == 0) overflowEmbeddings++;
                    level[i] = stackLevel[top];
                    break;
                }
                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                {
                    level[i] = stackLevel[top];
                    if (stackOverride[top] != 0) types[i] = stackOverride[top] == 1 ? BidiClass.L : BidiClass.R;
                    var rightToLeft = t == BidiClass.RLI ||
                                      t == BidiClass.FSI && FirstStrong(original, matchingPdi, i + 1, matchingPdi[i] < 0 ? n : matchingPdi[i], 0) == 1;
                    var next = rightToLeft ? (stackLevel[top] + 1) | 1 : (stackLevel[top] + 2) & ~1;
                    if (next <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                    {
                        validIsolates++;
                        stackLevel[depth] = (byte)next;
                        stackOverride[depth] = 0;
                        stackIsolate[depth] = true;
                        depth++;
                    }
                    else overflowIsolates++;
                    break;
                }
                case BidiClass.PDI:
                    if (overflowIsolates > 0) overflowIsolates--;
                    else if (validIsolates > 0)
                    {
                        overflowEmbeddings = 0;
                        while (!stackIsolate[depth - 1]) depth--;
                        depth--;
                        validIsolates--;
                    }
                    top = depth - 1;
                    level[i] = stackLevel[top];
                    if (stackOverride[top] != 0) types[i] = stackOverride[top] == 1 ? BidiClass.L : BidiClass.R;
                    break;
                case BidiClass.PDF:
                    if (overflowIsolates > 0) { }
                    else if (overflowEmbeddings > 0) overflowEmbeddings--;
                    else if (!stackIsolate[top] && depth >= 2) depth--;
                    level[i] = stackLevel[depth - 1];
                    break;
                case BidiClass.B:
                    level[i] = paragraphLevel;
                    break;
                case BidiClass.BN:
                    level[i] = stackLevel[top];
                    break;
                default:
                    level[i] = stackLevel[top];
                    if (stackOverride[top] != 0) types[i] = stackOverride[top] == 1 ? BidiClass.L : BidiClass.R;
                    break;
            }
        }

        // X9: the characters taking part (the others are removed); X10: level runs and the isolating run sequences
        var kept = new int[n];
        var keptAt = new int[n];
        var m = 0;
        for (var i = 0; i < n; i++)
        {
            keptAt[i] = -1;
            if (IsRemovedByX9(original[i])) continue;
            keptAt[i] = m;
            kept[m++] = i;
        }
        var runStarts = new List<int>();
        var runOf = new int[n];
        for (var p = 0; p < m; p++)
        {
            if (p == 0 || level[kept[p]] != level[kept[p - 1]]) runStarts.Add(p);
            runOf[kept[p]] = runStarts.Count - 1;
        }
        var runCount = runStarts.Count;
        var consumed = new bool[runCount];
        var sequence = new List<int>();
        var work = new Work(n);
        // sos and eos come from the explicit levels, before any sequence resolves its implicit ones
        var explicitLevel = (byte[])level.Clone();
        for (var r = 0; r < runCount; r++)
        {
            if (consumed[r]) continue;
            sequence.Clear();
            var run = r;
            while (true)
            {
                consumed[run] = true;
                var to = run + 1 < runCount ? runStarts[run + 1] : m;
                for (var p = runStarts[run]; p < to; p++) sequence.Add(kept[p]);
                var last = kept[to - 1];
                if (!IsInitiator(original[last]) || matchingPdi[last] < 0) break;
                var following = runOf[matchingPdi[last]];
                if (following <= run || consumed[following]) break;
                run = following;
            }
            Sequence(sequence, types, points, level, explicitLevel, kept, keptAt, m, paragraphLevel, work);
        }

        // the characters removed by X9 take the level of the character before them (the paragraph level at its start)
        for (var i = 0; i < n; i++)
            if (keptAt[i] < 0) level[i] = i == 0 ? paragraphLevel : level[i - 1];
        level.CopyTo(levels, start);
        return paragraphLevel;
    }

    /// <summary>Scratch arrays for the isolating run sequences of one paragraph.</summary>
    private sealed class Work(int size)
    {
        internal readonly BidiClass[] Types = new BidiClass[size];
        internal readonly BidiClass[] BeforeW1 = new BidiClass[size];
        internal readonly int[] OpenPair = new int[BracketStack];
        internal readonly int[] OpenAt = new int[BracketStack];
        internal readonly List<(int Open, int Close)> Pairs = [];
    }

    /// <summary>W1–W7, N0–N2, I1–I2 on one isolating run sequence (character indices in logical order).</summary>
    private static void Sequence(List<int> chars, BidiClass[] types, List<int> points, byte[] level, byte[] explicitLevel, int[] kept, int[] keptAt, int m,
        byte paragraphLevel, Work work)
    {
        var count = chars.Count;
        var t = work.Types;
        for (var k = 0; k < count; k++) t[k] = types[chars[k]];
        var runLevel = explicitLevel[chars[0]];
        var before = keptAt[chars[0]] - 1;
        var sos = Math.Max(runLevel, before >= 0 ? explicitLevel[kept[before]] : paragraphLevel) % 2 == 1 ? BidiClass.R : BidiClass.L;
        int succeeding;
        if (IsInitiator(t[count - 1])) succeeding = paragraphLevel;
        else
        {
            var after = keptAt[chars[count - 1]] + 1;
            succeeding = after < m ? explicitLevel[kept[after]] : paragraphLevel;
        }
        var eos = Math.Max(runLevel, succeeding) % 2 == 1 ? BidiClass.R : BidiClass.L;
        var embedding = runLevel % 2 == 1 ? BidiClass.R : BidiClass.L;
        Array.Copy(t, work.BeforeW1, count);

        // W1: a nonspacing mark takes the type of the character before it (ON after an isolate initiator or PDI, sos at the start)
        for (var k = 0; k < count; k++)
            if (t[k] == BidiClass.NSM)
                t[k] = k == 0 ? sos : t[k - 1] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI ? BidiClass.ON : t[k - 1];
        // W2: a European number after Arabic letters is an Arabic number
        var strong = sos;
        for (var k = 0; k < count; k++)
        {
            if (t[k] is BidiClass.L or BidiClass.R or BidiClass.AL) strong = t[k];
            else if (t[k] == BidiClass.EN && strong == BidiClass.AL) t[k] = BidiClass.AN;
        }
        // W3: Arabic letters are R
        for (var k = 0; k < count; k++)
            if (t[k] == BidiClass.AL) t[k] = BidiClass.R;
        // W4: a single separator between two numbers of the same kind
        for (var k = 1; k + 1 < count; k++)
        {
            if (t[k] == BidiClass.ES && t[k - 1] == BidiClass.EN && t[k + 1] == BidiClass.EN) t[k] = BidiClass.EN;
            else if (t[k] == BidiClass.CS && t[k - 1] == t[k + 1] && t[k - 1] is BidiClass.EN or BidiClass.AN) t[k] = t[k - 1];
        }
        // W5: terminators next to a European number belong to it
        for (var k = 0; k < count; k++)
        {
            if (t[k] != BidiClass.ET) continue;
            var j = k;
            while (j < count && t[j] == BidiClass.ET) j++;
            if (k > 0 && t[k - 1] == BidiClass.EN || j < count && t[j] == BidiClass.EN)
                for (var x = k; x < j; x++) t[x] = BidiClass.EN;
            k = j - 1;
        }
        // W6: the remaining separators and terminators are neutral
        for (var k = 0; k < count; k++)
            if (t[k] is BidiClass.ES or BidiClass.ET or BidiClass.CS) t[k] = BidiClass.ON;
        // W7: a European number after left-to-right text is L
        strong = sos;
        for (var k = 0; k < count; k++)
        {
            if (t[k] is BidiClass.L or BidiClass.R) strong = t[k];
            else if (t[k] == BidiClass.EN && strong == BidiClass.L) t[k] = BidiClass.L;
        }

        Brackets(chars, points, t, work, count, sos, embedding);

        // N1, N2: a run of neutrals and isolate formatting characters between two strong types of one direction takes it (numbers count as
        // R); otherwise the embedding direction
        for (var k = 0; k < count; k++)
        {
            if (!IsNeutral(t[k])) continue;
            var j = k;
            while (j < count && IsNeutral(t[j])) j++;
            var leading = k == 0 ? sos : Strong(t[k - 1]);
            var trailing = j == count ? eos : Strong(t[j]);
            var resolved = leading == trailing ? leading : embedding;
            for (var x = k; x < j; x++) t[x] = resolved;
            k = j - 1;
        }
        // I1, I2: the implicit levels
        for (var k = 0; k < count; k++)
        {
            var at = chars[k];
            var l = level[at];
            level[at] = (byte)(l % 2 == 0
                ? t[k] switch { BidiClass.R => l + 1, BidiClass.AN or BidiClass.EN => l + 2, _ => l }
                : t[k] switch { BidiClass.L or BidiClass.EN or BidiClass.AN => l + 1, _ => l });
        }
    }

    private static bool IsNeutral(BidiClass c) => c is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON or BidiClass.LRI or BidiClass.RLI
        or BidiClass.FSI or BidiClass.PDI;

    /// <summary>The direction of a resolved type for N0 and N1: L stays L; R, EN and AN count as R; anything else is not strong (ON).</summary>
    private static BidiClass Strong(BidiClass c) => c switch
    {
        BidiClass.L => BidiClass.L,
        BidiClass.R or BidiClass.EN or BidiClass.AN => BidiClass.R,
        _ => BidiClass.ON,
    };

    /// <summary>The bracket of a code point for pairing, with the canonical equivalents U+2329/U+232A taken as U+3008/U+3009 (BD16).</summary>
    private static int Canonical(int c) => c switch { 0x2329 => 0x3008, 0x232A => 0x3009, _ => c };

    /// <summary>
    /// BD16 and N0: the bracket pairs of the sequence (a fixed stack of 63 openers; when it overflows, no pairs at all), each pair resolved in
    /// the order of its opening bracket from the strong types inside it and, against the embedding direction, from the context before it; the
    /// nonspacing marks after a changed bracket follow it.
    /// </summary>
    private static void Brackets(List<int> chars, List<int> points, BidiClass[] t, Work work, int count, BidiClass sos, BidiClass embedding)
    {
        var tables = Data.Value;
        var pairs = work.Pairs;
        pairs.Clear();
        var depth = 0;
        for (var k = 0; k < count; k++)
        {
            if (t[k] != BidiClass.ON || !tables.Brackets.TryGetValue(points[chars[k]], out var bracket)) continue;
            if (bracket.Open)
            {
                if (depth == BracketStack)
                {
                    pairs.Clear();
                    return;
                }
                work.OpenPair[depth] = Canonical(bracket.Pair);
                work.OpenAt[depth] = k;
                depth++;
            }
            else
            {
                var closing = Canonical(points[chars[k]]);
                for (var s = depth - 1; s >= 0; s--)
                {
                    if (work.OpenPair[s] != closing) continue;
                    pairs.Add((work.OpenAt[s], k));
                    depth = s;
                    break;
                }
            }
        }
        if (pairs.Count == 0) return;
        pairs.Sort((a, b) => a.Open.CompareTo(b.Open));
        var opposite = embedding == BidiClass.L ? BidiClass.R : BidiClass.L;
        foreach (var (open, close) in pairs)
        {
            var foundEmbedding = false;
            var foundOpposite = false;
            for (var k = open + 1; k < close && !foundEmbedding; k++)
            {
                var s = Strong(t[k]);
                if (s == embedding) foundEmbedding = true;
                else if (s == opposite) foundOpposite = true;
            }
            BidiClass resolved;
            if (foundEmbedding) resolved = embedding;
            else if (foundOpposite)
            {
                var context = sos;
                for (var k = open - 1; k >= 0; k--)
                {
                    var s = Strong(t[k]);
                    if (s == BidiClass.ON) continue;
                    context = s;
                    break;
                }
                resolved = context == opposite ? opposite : embedding;
            }
            else continue;
            t[open] = t[close] = resolved;
            for (var k = open + 1; k < count && work.BeforeW1[k] == BidiClass.NSM; k++) t[k] = resolved;
            for (var k = close + 1; k < count && work.BeforeW1[k] == BidiClass.NSM; k++) t[k] = resolved;
        }
    }

    /// <summary>
    /// The visual order of items with the given levels (L2): from the highest level down to the lowest odd one, every run of items at that
    /// level or higher is reversed. Returns the item indices from left to right.
    /// </summary>
    internal static int[] VisualOrder(IReadOnlyList<byte> levels)
    {
        var order = new int[levels.Count];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        if (levels.Count == 0) return order;
        byte highest = 0, lowestOdd = byte.MaxValue;
        foreach (var l in levels)
        {
            highest = Math.Max(highest, l);
            if (l % 2 == 1) lowestOdd = Math.Min(lowestOdd, l);
        }
        for (int level = highest; level >= lowestOdd && level > 0; level--)
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

    /// <summary>
    /// A text with the characters at odd levels replaced by their mirror image (L4, before shaping) where <paramref name="has"/> says the font
    /// draws the counterpart and both are of the same UTF-16 length; the text itself if nothing changes. Where symmetric swapping is inhibited
    /// (after U+206A up to U+206B or the end of the paragraph, The Unicode Standard, 23.3.1) a character keeps its own glyph also at an odd level
    /// (L4 is not applied there; the text stored, copied and searched stays as it is; docs/FEHLERGEDAECHTNIS.md, No. 1215).
    /// </summary>
    /// <param name="text">The text, or a part of a longer text.</param>
    /// <param name="levelAt">The level of a UTF-16 unit of <paramref name="text"/>.</param>
    /// <param name="has">Whether the font draws a character.</param>
    /// <param name="inhibitedAtStart">Whether symmetric swapping is inhibited at the start of <paramref name="text"/> (by a U+206A before it in
    /// the longer text, <see cref="Analysis.SwappingInhibitedBefore"/>).</param>
    internal static string MirrorOdd(string text, Func<int, byte> levelAt, Func<int, bool> has, bool inhibitedAtStart = false)
    {
        StringBuilder? sb = null;
        var inhibited = inhibitedAtStart;
        for (var i = 0; i < text.Length; i++)
        {
            inhibited = SwappingInhibitedAfter(inhibited, text[i]);
            var c = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            var length = c > 0xFFFF ? 2 : 1;
            if (!inhibited && levelAt(i) % 2 == 1 && Mirrored(c) is var mirror && mirror != c && (mirror > 0xFFFF ? 2 : 1) == length && has(mirror))
            {
                sb ??= new StringBuilder(text);
                var replacement = char.ConvertFromUtf32(mirror);
                for (var k = 0; k < length; k++) sb[i + k] = replacement[k];
            }
            i += length - 1;
        }
        return sb?.ToString() ?? text;
    }
}
