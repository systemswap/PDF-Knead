using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// Parts of the shaper added with docs/FEHLERGEDAECHTNIS.md, No. 1174: features of restricted reach (kern not at glyphs palt covers, cpsp in words
/// of capitals, rtlm at mirrored characters), chws or halt for CJK text, the transient reordering of Arabic marks (UAX #53; after it in No. 1175)
/// and the stretching of stch. Written from the OpenType feature registry and the Unicode Standard; no code of another shaper was used.
/// </summary>
public sealed partial class TextShaper
{
    /// <summary>Bit of <see cref="ShapedGlyph.Gates"/>: kern applies (every glyph except those palt covers while palt is off, OpenType 66.1).</summary>
    internal const byte GateKern = 1;

    /// <summary>Bit of <see cref="ShapedGlyph.Gates"/>: cpsp applies (a word of capitals, OpenType 64.19).</summary>
    internal const byte GateCpsp = 2;

    /// <summary>Bit of <see cref="ShapedGlyph.Gates"/>: rtlm applies (a mirrored character not replaced by its mirror image, OpenType 67.18).</summary>
    internal const byte GateRtlm = 4;

    /// <summary>The features of one <see cref="Shape(string, IEnumerable{string}?, string?, bool?)"/> call: switched on, switched off, the language systems.</summary>
    private sealed record RunOptions(HashSet<string> Substitutions, HashSet<string> Positioning, HashSet<string> Disabled, string[] Language);

    /// <summary>Bits of <see cref="ShapedGlyph.Gates"/>: numr, dnom and frac apply (the digits and the slash of a fraction, No. 1183).</summary>
    internal const byte GateNumr = 8, GateDnom = 16, GateFrac = 32;

    /// <summary>Bit of <see cref="ShapedGlyph.Gates"/>, no feature: symmetric swapping is inhibited at the character (U+206A, The Unicode
    /// Standard, 23.3.1) — rtlm does not apply (No. 1215).</summary>
    internal const byte GateUnswapped = 64;

    /// <summary>The bit a feature of restricted reach needs (0: the feature applies everywhere).</summary>
    private static byte GateOf(string feature) => feature switch
    {
        "kern" => GateKern, "cpsp" => GateCpsp, "rtlm" => GateRtlm, "numr" => GateNumr, "dnom" => GateDnom, "frac" => GateFrac, _ => 0,
    };

    /// <summary>
    /// The lookups of <paramref name="features"/> in LookupList order, each with the bits one of which a glyph must carry for it to apply there: 0
    /// for a lookup of a feature of unrestricted reach (also when a restricted feature names it as well), else the bits of its restricted features.
    /// </summary>
    private static SortedDictionary<int, byte> Gated(LayoutTable table, IReadOnlyCollection<string> features, string[] scripts, string[] language, bool required)
    {
        var result = new SortedDictionary<int, byte>();
        foreach (var lookup in table.LookupsFor([.. features.Where(f => GateOf(f) == 0)], scripts, language, required)) result[lookup] = 0;
        foreach (var feature in features)
        {
            var gate = GateOf(feature);
            if (gate == 0) continue;
            foreach (var lookup in table.LookupsFor([feature], scripts, language, required: false))
                result[lookup] = result.TryGetValue(lookup, out var known) ? known == 0 ? (byte)0 : (byte)(known | gate) : gate;
        }
        return result;
    }

    /// <summary>The direction of a run without one given: that of its first strong character (right to left for R and AL), else left to right.</summary>
    private static bool StartsRightToLeft(List<int> codePoints)
    {
        foreach (var c in codePoints)
            switch (Bidi.ClassOf(c))
            {
                case BidiClass.L:
                    return false;
                case BidiClass.R or BidiClass.AL:
                    return true;
            }
        return false;
    }

    // ------------------------------------------------------------ positioning features of the run

    /// <summary>The horizontal width features that exclude chws and halt (OpenType 64.15 sentence 27, 65.10 sentence 18).</summary>
    private static readonly string[] WidthFeatures = ["chws", "fwid", "halt", "hwid", "palt", "pkna", "pwid", "qwid", "twid", "pnum", "tnum"];

    /// <summary>
    /// The positioning features of a run: cpsp not in a connecting script (OpenType 64.19 sentence 13); in a run of CJK text (Han, kana, Hangul,
    /// Bopomofo), which PDF-Knead sets without a layout after CLREQ, JLREQ or KLREQ, chws where the font has it, else halt (64.15 sentences 21,
    /// 24; 65.10 sentences 12, 15) — mutually exclusive, and with either kern and apkn off for the run (64.15 sentence 28, 65.10 sentence 19)
    /// unless the caller asked for a width feature itself; kern off with any feature of fixed widths (66.1 sentences 38–39), and otherwise not at
    /// the glyphs palt lookups cover while palt is off and the font has no apkn (66.1 sentences 28–35). Before, cpsp, chws and halt were never
    /// applied and kern applied everywhere (No. 1174).
    /// </summary>
    private HashSet<string> PositioningFor(List<ShapedGlyph> run, string[] scripts, RunOptions options, bool joining, Work work)
    {
        var positioning = options.Positioning;
        HashSet<string>? changed = null;
        HashSet<string> Change() => changed ??= new HashSet<string>(positioning, StringComparer.Ordinal);
        if (joining && positioning.Contains("cpsp")) Change().Remove("cpsp");
        if (_gpos is not null && scripts is ["hani"] or ["kana"] or ["hang"] or ["bopo"] && !WidthFeatures.Any(positioning.Contains))
        {
            var width = new[] { "chws", "halt" }.FirstOrDefault(f => !options.Disabled.Contains(f)
                && _gpos.LookupsFor([f], scripts, options.Language, required: false).Length > 0);
            if (width is not null)
            {
                var set = Change();
                set.Add(width);
                set.Remove("apkn");
            }
        }
        // kern not together with a feature of fixed widths (OpenType 66.1 sentences 38–39, 64.15 sentence 28, 65.10 sentence 19)
        if ((changed ?? positioning).Contains("kern") && FixedWidthFeatures.Any((changed ?? positioning).Contains)) Change().Remove("kern");
        var current = changed ?? positioning;
        if (_gpos is not null && current.Contains("kern") && !current.Contains("palt")
            && _gpos.LookupsFor(["apkn"], scripts, options.Language, required: false).Length == 0
            && _gpos.LookupsFor(["palt"], scripts, options.Language, required: false) is { Length: > 0 } palt)
            ExcludeFromKerning(run, palt, work);
        return current;
    }

    /// <summary>The features that result in fixed widths, with which kern should not be used (OpenType 66.1 sentence 38).</summary>
    private static readonly string[] FixedWidthFeatures = ["chws", "fwid", "halt", "hwid", "qwid", "twid", "tnum"];

    /// <summary>Clears <see cref="GateKern"/> at each glyph a palt lookup covers (the coverage of its subtables; OpenType 66.1 sentence 29).</summary>
    private void ExcludeFromKerning(List<ShapedGlyph> run, int[] palt, Work work)
    {
        var t = _gpos!.T;
        var coverages = new List<int>();
        foreach (var index in palt)
        {
            var lookup = _gpos.Lookup(index);
            foreach (var sub in lookup.Subtables)
                if (FirstCoverage(t, lookup.Type, sub) is var at and >= 0) coverages.Add(at);
        }
        for (var i = 0; i < run.Count; i++)
        {
            if (!work.Spend(1 + coverages.Count)) return;
            foreach (var at in coverages)
                if (t.Coverage(at, run[i].Glyph) >= 0)
                {
                    run[i] = run[i] with { Gates = (byte)(run[i].Gates & ~GateKern) };
                    break;
                }
        }
    }

    /// <summary>The coverage of the first glyph a GPOS subtable acts on (types 1–6 and formats 1–2 of 7 and 8 at offset 2; format 3 of 7 and 8
    /// the first input coverage); -1 for none.</summary>
    private static int FirstCoverage(TableData t, int type, int sub)
    {
        var format = t.U16(sub);
        if (type is >= 1 and <= 6 || type is 7 or 8 && format is 1 or 2) return t.U16(sub + 2) is var c and > 0 ? sub + c : -1;
        if (type == 7 && format == 3) return t.U16(sub + 2) > 0 && t.U16(sub + 6) is var c7 and > 0 ? sub + c7 : -1;
        if (type == 8 && format == 3)
        {
            var input = sub + 4 + t.U16(sub + 2) * 2;
            return t.U16(input) > 0 && t.U16(input + 2) is var c8 and > 0 ? sub + c8 : -1;
        }
        return -1;
    }

    /// <summary>
    /// Marks the glyphs of words of capitals for cpsp (OpenType 64.19 sentence 10: the program's own recognition of such words): a word (letters
    /// and marks) of two or more capitals and no small letter, and a single capital next to such a word („A TALE“).
    /// </summary>
    private static void MarkCapitalWords(List<ShapedGlyph> run, List<int> codePoints)
    {
        var words = new List<(int Start, int End, int Capitals, bool Small)>();
        for (var i = 0; i < codePoints.Count;)
        {
            if (!Rune.IsValid(codePoints[i]) || !Rune.IsLetter(new Rune(codePoints[i])))
            {
                i++;
                continue;
            }
            var start = i;
            var capitals = 0;
            var small = false;
            for (; i < codePoints.Count && Rune.IsValid(codePoints[i]); i++)
            {
                var category = Rune.GetUnicodeCategory(new Rune(codePoints[i]));
                if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter) capitals++;
                else if (category == UnicodeCategory.LowercaseLetter) small = true;
                else if (!Rune.IsLetter(new Rune(codePoints[i])) && category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                             or UnicodeCategory.EnclosingMark)) break;
            }
            words.Add((start, i, capitals, small));
        }
        var capital = words.Select(w => !w.Small && w.Capitals >= 2).ToArray();
        for (var k = 0; k < words.Count; k++)
        {
            var (start, end, capitals, small) = words[k];
            if (!capital[k] && !(capitals == 1 && !small && (k > 0 && capital[k - 1] || k + 1 < words.Count && capital[k + 1]))) continue;
            for (var i = start; i < end; i++) run[i] = run[i] with { Gates = (byte)(run[i].Gates | GateCpsp) };
        }
    }

    /// <summary>
    /// Code points with Bidi_Mirrored = Yes but no Bidi_Mirroring_Glyph: listed as comments at the end of BidiMirroring.txt 16.0.0 („# 2140; …“).
    /// </summary>
    private static readonly Lazy<HashSet<int>> MirroredWithoutPair = new(() =>
    {
        var set = new HashSet<int>();
        using var stream = typeof(TextShaper).Assembly.GetManifestResourceStream("Fonts/BidiMirroring.txt");
        if (stream is null) return set;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var semicolon = line.IndexOf(';');
            if (!line.StartsWith("# ", StringComparison.Ordinal) || semicolon is < 6 or > 8) continue;
            if (int.TryParse(line.AsSpan(2, semicolon - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)) set.Add(cp);
        }
        return set;
    });

    /// <summary>
    /// Marks for rtlm the mirrored characters of a right-to-left run that are not shown by their mirror image (OpenType 67.18): Bidi_Mirrored
    /// without a counterpart, or with one the font lacks — the caller mirrored the others before shaping (UAX #9 L4, No. 1173); not where symmetric
    /// swapping is inhibited (No. 1215).
    /// </summary>
    private void MarkMirrored(List<ShapedGlyph> run, List<int> codePoints)
    {
        for (var i = 0; i < codePoints.Count; i++)
        {
            var c = codePoints[i];
            if ((run[i].Gates & GateUnswapped) != 0) continue;
            var mirror = Bidi.Mirrored(c);
            var mirrored = mirror != c || MirroredWithoutPair.Value.Contains(c);
            if (mirrored && (mirror == c || _font.GlyphForUnicode(mirror) <= 0)) run[i] = run[i] with { Gates = (byte)(run[i].Gates | GateRtlm) };
        }
    }

    // ------------------------------------------------------------ Arabic mark transient reordering (UAX #53)

    /// <summary>Most non-starters ordered together: the limit of the stream-safe text format (UAX #15: 30 non-starters); a longer sequence is
    /// ordered in pieces of this length, as if a CGJ stood after every 30th (No. 1180).</summary>
    private const int MaxReorderedMarks = 30;

    /// <summary>The modifier combining marks of UAX #53 with canonical combining class 220 (below).</summary>
    private static readonly int[] ModifierMarksBelow = [0x0655, 0x06E3, 0x08CF, 0x08D3];

    /// <summary>The modifier combining marks of UAX #53 with canonical combining class 230 (above).</summary>
    private static readonly int[] ModifierMarksAbove = [0x0654, 0x0658, 0x06DC, 0x06E7, 0x06E8, 0x08CA, 0x08CB, 0x08CD, 0x08CE, 0x08F3];

    /// <summary>What the shaper knows of a mark's canonical combining class (bits: 1 not 0, 2 class 230, 4 class 220), found once per character.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> MarkClasses = new();

    private const byte NonStarter = 1, ClassAbove = 2, ClassBelow = 4;

    /// <summary>
    /// The class bits of a character, taken from the canonical ordering of the runtime's normalization: a mark that NFD moves before U+0345 (class
    /// 240) has a class other than 0; one that keeps its place next to U+0301 (230) or U+0316 (220) in both orders has that class.
    /// </summary>
    private static byte MarkInfo(int c)
    {
        if (MarkClasses.TryGetValue(c, out var known)) return known;
        byte info = 0;
        if (Rune.IsValid(c) && Rune.GetUnicodeCategory(new Rune(c)) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
        {
            var s = char.ConvertFromUtf32(c);
            if (Swaps("\u0345", s))
            {
                info = NonStarter;
                if (!Swaps(s, "\u0301") && !Swaps("\u0301", s)) info |= ClassAbove;
                if (!Swaps(s, "\u0316") && !Swaps("\u0316", s)) info |= ClassBelow;
            }
        }
        if (MarkClasses.Count < 4096) MarkClasses[c] = info;
        return info;
    }

    /// <summary>Whether the canonical ordering puts <paramref name="second"/> before <paramref name="first"/> (after a base letter).</summary>
    private static bool Swaps(string first, string second)
    {
        try
        {
            return ("a" + first + second).Normalize(NormalizationForm.FormD) == "a" + second + first;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A mark of the Arabic blocks (Arabic, Supplement, Extended-A/B/C, presentation forms).</summary>
    private static bool IsArabicMark(int c) => (c is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x0870 and <= 0x08FF
        or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF or >= 0x10EC0 and <= 0x10EFF) && Rune.IsValid(c)
        && Rune.GetUnicodeCategory(new Rune(c)) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    /// <summary>The marks a precomposed base letter holds after it in its canonical decomposition (U+0622 → U+0653; U+0623 → U+0654 …); empty
    /// for any other character.</summary>
    private static int[] BaseMarks(int c)
    {
        if (c < 0x0600 || !Rune.IsValid(c) || (MarkInfo(c) & NonStarter) != 0) return [];
        string parts;
        try
        {
            parts = char.ConvertFromUtf32(c).Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return [];
        }
        var runes = parts.EnumerateRunes().Select(r => r.Value).ToArray();
        return runes.Length > 1 && runes.Skip(1).All(m => (MarkInfo(m) & NonStarter) != 0) ? runes[1..] : [];
    }

    /// <summary>
    /// The Arabic Mark Transient Reordering Algorithm (UAX #53, 3.2) for display, in every run (also one without a connecting letter, such as
    /// hamza with its marks): each longest sequence of non-starters (canonical combining class other than 0 — CGJ U+034F and the other marks of
    /// class 0 end it, so that they override the order) with at least one Arabic mark is put in canonical order (NFD), then the shaddas go to
    /// its front, then the modifier combining marks that lead the subsequence of class 230, then those that lead the subsequence of class 220 —
    /// only leading ones, so the result stays canonically equivalent (<see cref="AmtraOrder"/>). A sequence longer than the stream-safe text
    /// format allows is ordered in pieces of 30 non-starters (UAX #15). The marks a precomposed base letter holds (U+0622 … U+0626, U+06C0,
    /// U+06C2, U+06D3) take part as NFD places them: where the order puts a typed mark inside one of them and the font has the glyphs of the
    /// decomposed letter and its marks, the letter is drawn decomposed (alef-madda with superscript alef as alef, superscript alef, madda — the
    /// same picture as the decomposed text); otherwise it keeps its glyph and the marks typed after it are ordered as after the decomposed letter.
    /// A reordered or decomposed sequence becomes one cluster with its base: the first glyph carries the text as typed, the others none, so the
    /// appearance can give that text as /ActualText around the cluster. The text itself stays as typed. Before, a sequence over 30 stayed as
    /// typed, the base letter was never decomposed, and each glyph kept its own character, so the saved page read the marks in drawing order
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1174, No. 1175, No. 1180).
    /// </summary>
    private void ReorderArabicMarks(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        for (var i = 0; i < codePoints.Count;)
        {
            if ((MarkInfo(codePoints[i]) & NonStarter) == 0)
            {
                i++;
                continue;
            }
            var start = i;
            while (i < codePoints.Count && (MarkInfo(codePoints[i]) & NonStarter) != 0) i++;
            var count = i - start;
            var baseAt = start - 1;
            var hidden = baseAt >= 0 ? BaseMarks(codePoints[baseAt]) : [];
            if (count + hidden.Length < 2 || !work.Spend(count)) continue;
            var arabic = hidden.Any(IsArabicMark);
            for (var k = start; k < i && !arabic; k++) arabic = IsArabicMark(codePoints[k]);
            if (!arabic) continue;
            if (AmtraOrder(hidden, codePoints.GetRange(start, count)) is not { } order) continue;

            // the base letter decomposed where a typed mark goes inside one of its own and the font has every part
            var h = hidden.Length;
            var decompose = false;
            for (var k = 0; k < h && !decompose; k++) decompose = order[k] != k;
            var letter = decompose ? Rune.GetRuneAt(char.ConvertFromUtf32(codePoints[baseAt]).Normalize(NormalizationForm.FormD), 0).Value : 0;
            var parts = decompose ? hidden.Select(m => _font.GlyphForUnicode(m)).Prepend(_font.GlyphForUnicode(letter)).ToArray() : [];
            decompose = decompose && parts.All(g => g > 0) && run.Count + h <= MaxGlyphs;
            if (!decompose)
            {
                // the marks typed after the letter in their order, the letter's own staying in its glyph
                order = [.. order.Where(k => k >= h).Select(k => k - h)];
                h = 0;
                if (order.Select((k, q) => k == q).All(same => same)) continue;
            }

            // one cluster from the base (or the first mark, at the start of the run) to the last mark: its text as typed on the first glyph
            var from = baseAt >= 0 ? baseAt : start;
            var typed = new StringBuilder();
            for (var k = from; k < i; k++) typed.Append(run[k].Text);
            var cluster = run[from].Cluster;
            var glyphs = new List<ShapedGlyph>(order.Length);
            var points = new List<int>(order.Length);
            foreach (var k in order)
            {
                if (k < h)
                {
                    glyphs.Add(new ShapedGlyph { Glyph = parts[k + 1], Cluster = cluster, Text = "", Class = 3, Gates = run[baseAt].Gates });
                    points.Add(hidden[k]);
                }
                else
                {
                    glyphs.Add(run[start + k - h]);
                    points.Add(codePoints[start + k - h]);
                }
            }
            if (h > 0)
            {
                run[baseAt] = run[baseAt] with { Glyph = parts[0] };
                codePoints[baseAt] = letter;
            }
            run.RemoveRange(start, count);
            run.InsertRange(start, glyphs);
            codePoints.RemoveRange(start, count);
            codePoints.InsertRange(start, points);
            i = start + glyphs.Count;
            for (var k = from; k < i; k++) run[k] = run[k] with { Cluster = cluster, Text = k == from ? typed.ToString() : "" };
        }
    }

    /// <summary>
    /// Composes for display, in the runs without syllabic shaping, what composes canonically (NFC, so never a composition exclusion) where the
    /// font has the composed character — so a text typed either way is drawn alike with the font's glyph of the composed letter, as the
    /// reference shaper composes; the text stays as typed (docs/FEHLERGEDAECHTNIS.md, No. 1193):
    /// <list type="bullet">
    /// <item>Arabic: a letter with the mark immediately after it — madda, hamza above or below (U+0622 … U+0626, U+06C0, U+06C2, U+06D3) —
    /// after the reordering of AMTRA, no mark skipped (UAX #53, 3.2 sentences 9–13); yeh with hamza above without the dots of yeh, alef-madda as
    /// the font's ccmp draws it. The composed glyph carries the text of both; the appearance writes it as /ActualText (<see cref="IsMarkCluster"/>);</item>
    /// <item>every other script (<see cref="ComposeSequences"/>): a starter with the marks after it, as canonical composition does it (The
    /// Unicode Standard 3.11: in canonical order, a mark blocked by one of its class before it) — e U+0301 as é, A U+030A as Å, Ω U+0301 as Ώ,
    /// и U+0306 as й, か U+3099 as が, a U+0323 U+0302 as ậ (No. 1197).</item>
    /// </list>
    /// Before, each sequence kept the glyphs of its characters.
    /// </summary>
    private void ComposeForDisplay(List<ShapedGlyph> run, List<int> codePoints, Work work, bool sequences)
    {
        bool arabic = false, marks = false;
        foreach (var c in codePoints)
        {
            if (c is >= 0x0653 and <= 0x0655) arabic = true;
            if (sequences && !marks && IsNonStarterMark(c)) marks = true;
        }
        if (arabic) ComposeCanonically(run, codePoints, 0x0600, 0x06FF, work);
        if (marks) ComposeSequences(run, codePoints, work);
    }

    /// <summary>
    /// Canonical composition for display outside Arabic, per combining sequence (a starter and the non-starters after it, at most
    /// <see cref="MaxReorderedMarks"/> of them; one with an Arabic letter or mark is left to the rule of UAX #53): the starter is taken apart where
    /// the font has all its parts, the marks are put into canonical order, then each mark is composed with the starter where they compose
    /// canonically, no mark of the same class stands between them uncomposed (blocking, The Unicode Standard 3.11) and the font has the
    /// composed character. Only where something was composed is the sequence changed: the glyphs of the result, the first carrying the text of
    /// the whole sequence as typed, the others none (one cluster). A glyph other than that of the cmap (a variation sequence) leaves the
    /// sequence as it is. Hangul is composed by its own stage (the jamo are starters and are not taken apart here). (No. 1197)
    /// </summary>
    private void ComposeSequences(List<ShapedGlyph> run, List<int> codePoints, Work work)
    {
        for (var i = 0; i < codePoints.Count;)
        {
            var start = i++;
            while (i < codePoints.Count && IsNonStarterMark(codePoints[i])) i++;
            var count = i - start;
            if (count < 2 || count > MaxReorderedMarks + 1 || IsNonStarterMark(codePoints[start]) || !work.Spend(count)) continue;
            if (ComposedSequence(run, codePoints, start, count) is not { } result) continue;
            if (run.Count - count + result.Count > MaxGlyphs || !work.Move(run.Count - start)) continue;
            var first = run[start];
            var typed = new StringBuilder();
            for (var k = start; k < i; k++) typed.Append(run[k].Text);
            var glyphs = result.Select((cp, k) => first with
            {
                Glyph = _font.GlyphForUnicode(cp), Text = k == 0 ? typed.ToString() : "", Joiner = 0,
                Class = Rune.GetUnicodeCategory(new Rune(cp)) == UnicodeCategory.NonSpacingMark ? (byte)3 : (byte)1,
            }).ToList();
            run.RemoveRange(start, count);
            run.InsertRange(start, glyphs);
            codePoints.RemoveRange(start, count);
            codePoints.InsertRange(start, result);
            i = start + result.Count;
        }
    }

    /// <summary>A mark (Mn, Mc, Me) of a canonical combining class other than 0; the category is tested first, the class only for marks.</summary>
    private static bool IsNonStarterMark(int c) => c >= 0x0300 && Rune.IsValid(c)
        && Rune.GetUnicodeCategory(new Rune(c)) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
        && (MarkInfo(c) & NonStarter) != 0;

    /// <summary>
    /// The characters a combining sequence <paramref name="start"/> … +<paramref name="count"/> is drawn with after canonical composition where
    /// the font has the composed characters (<see cref="ComposeSequences"/>); null if nothing is composed or the sequence is left as it is.
    /// </summary>
    private List<int>? ComposedSequence(List<ShapedGlyph> run, List<int> codePoints, int start, int count)
    {
        for (var k = start; k < start + count; k++)
        {
            var c = codePoints[k];
            if (!Rune.IsValid(c) || IsArabicMark(c) || c is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x0870 and <= 0x08FF
                || run[k].Text != char.ConvertFromUtf32(c) || run[k].Glyph != _font.GlyphForUnicode(c))
                return null;
        }
        // the starter taken apart where the font has every part and the parts after the first are marks; the marks taken apart likewise
        var items = new List<int>(count + 4);
        for (var k = start; k < start + count; k++)
        {
            var parts = CanonicalDecomposition(codePoints[k]);
            var apart = parts.Length > 1 && parts.All(p => _font.GlyphForUnicode(p) > 0)
                && parts.Skip(k == start ? 1 : 0).All(p => (MarkInfo(p) & NonStarter) != 0);
            if (apart) items.AddRange(parts);
            else items.Add(codePoints[k]);
        }
        if (items.Count - 1 > MaxReorderedMarks) return null;
        if (items.Count > 2 && CanonicalMarkOrder(items, 1, items.Count - 1) is { } order)
        {
            var marks = order.Select(k => items[1 + k]).ToArray();
            for (var k = 0; k < marks.Length; k++) items[1 + k] = marks[k];
        }
        // canonical composition with blocking: in canonical order a mark is blocked by an uncomposed one of the same class before it
        var result = new List<int>(items.Count) { items[0] };
        var composed = false;
        var last = -1;   // the last mark left uncomposed
        for (var k = 1; k < items.Count; k++)
        {
            var c = items[k];
            var blocked = last >= 0 && !Swaps(char.ConvertFromUtf32(c), char.ConvertFromUtf32(last));
            if (!blocked && CanonicalComposition(result[0], c) is { } both && (MarkInfo(both) & NonStarter) == 0 && _font.GlyphForUnicode(both) > 0)
            {
                result[0] = both;
                composed = true;
                continue;
            }
            result.Add(c);
            last = c;
        }
        if (!composed) return null;
        if (result.Count == count)
        {
            var same = true;
            for (var k = 0; k < count && same; k++) same = result[k] == codePoints[start + k];
            if (same) return null;
        }
        return result;
    }

    /// <summary>
    /// The order AMTRA (UAX #53, 3.2) gives a sequence of non-starters <paramref name="typed"/> after a base letter whose canonical decomposition
    /// holds the marks <paramref name="hidden"/>: indices into hidden followed by typed (0 … h−1 the letter's own marks, from h on the typed
    /// ones), from the innermost (nearest to the letter) to the outermost; null if nothing moves. A sequence over 30 non-starters is ordered in
    /// pieces of 30 — the letter's own marks count in the first, as in the stream-safe text format (UAX #15) — and a piece without an Arabic mark
    /// stays as typed. Display, the reading of the saved text and the deletion of the outermost mark (<see cref="OutermostMark"/>) use this order.
    /// </summary>
    internal static int[]? AmtraOrder(IReadOnlyList<int> hidden, IReadOnlyList<int> typed)
    {
        var items = new int[hidden.Count + typed.Count];
        for (var k = 0; k < hidden.Count; k++) items[k] = hidden[k];
        for (var k = 0; k < typed.Count; k++) items[hidden.Count + k] = typed[k];
        var order = new int[items.Length];
        var moved = false;
        for (var from = 0; from < items.Length; from += MaxReorderedMarks)
        {
            var length = Math.Min(MaxReorderedMarks, items.Length - from);
            var piece = PieceOrder(items, from, length);
            for (var k = 0; k < length; k++)
            {
                order[from + k] = from + (piece?[k] ?? k);
                moved |= order[from + k] != from + k;
            }
        }
        return moved ? order : null;
    }

    /// <summary>
    /// The AMTRA order of the marks <paramref name="items"/>[<paramref name="from"/> … +<paramref name="length"/>] (indices within the piece);
    /// null if the piece has no Arabic mark or a mark that decomposes (it stays as it is).
    /// </summary>
    private static int[]? PieceOrder(int[] items, int from, int length)
    {
        if (length < 2) return null;
        var arabic = false;
        for (var k = 0; k < length && !arabic; k++) arabic = IsArabicMark(items[from + k]);
        if (!arabic) return null;
        // the canonical ordering, taken from the normalization of the runtime: the marks after a base that does not decompose (beh); a mark that
        // decomposes leaves the piece as it is
        var sb = new StringBuilder("ب");
        for (var k = 0; k < length; k++) sb.Append(char.ConvertFromUtf32(items[from + k]));
        string normalized;
        try
        {
            normalized = sb.ToString().Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return null;
        }
        var canonical = new List<int>(length);
        var used = new bool[length];
        foreach (var rune in normalized.EnumerateRunes().Skip(1))
        {
            var k = Enumerable.Range(0, length).FirstOrDefault(q => !used[q] && items[from + q] == rune.Value, -1);
            if (k < 0) return null;
            used[k] = true;
            canonical.Add(k);
        }
        if (canonical.Count != length) return null;
        // step 2: the shaddas to the front; steps 3 and 4: the modifier marks that lead the subsequence of class 230, then of class 220
        canonical = [.. canonical.Where(k => items[from + k] == 0x0651), .. canonical.Where(k => items[from + k] != 0x0651)];
        foreach (var (cls, modifiers) in new[] { (ClassAbove, ModifierMarksAbove), (ClassBelow, ModifierMarksBelow) })
        {
            var leading = canonical.Where(k => (MarkInfo(items[from + k]) & cls) != 0).TakeWhile(k => modifiers.Contains(items[from + k])).ToHashSet();
            canonical = [.. canonical.Where(leading.Contains), .. canonical.Where(k => !leading.Contains(k))];
        }
        return [.. canonical];
    }

    /// <summary>
    /// Whether <paramref name="text"/> is the text of a cluster of Arabic marks the shaper reordered (<see cref="ReorderArabicMarks"/>): more than
    /// one character with an Arabic mark of a class other than 0 among them. The glyph carrying it is followed or preceded by the cluster's other
    /// glyphs without a text of their own; the appearance writes the text as /ActualText around all of them (No. 1180).
    /// </summary>
    /// <param name="text">The text of a glyph.</param>
    /// <returns>Whether it is such a cluster text.</returns>
    public static bool IsMarkCluster(string? text)
    {
        if (text is null || text.Length < 2) return false;
        foreach (var rune in text.EnumerateRunes())
            if (IsArabicMark(rune.Value) && (MarkInfo(rune.Value) & NonStarter) != 0) return true;
        return false;
    }

    /// <summary>
    /// What the backspace key deletes at <paramref name="caret"/> after a sequence of marks with an Arabic mark: the outermost mark as AMTRA
    /// stacks them (UAX #53, 5.6), not the one typed last — beh, fatha, shadda loses the fatha (the shadda stands nearer to the letter). Where the
    /// outermost is the mark a precomposed letter holds (alef-madda with fatha: the madda stands outside the fatha), the letter is replaced by its
    /// base letter. Null where the backspace key deletes the character before the caret as usual: no such sequence ends at the caret (also when
    /// the caret stands inside one), or the outermost mark is the one typed last (No. 1180).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="caret">The caret (an index in <paramref name="text"/>).</param>
    /// <returns>The range to replace and its replacement (empty: delete); null for the usual deletion.</returns>
    public static (int Start, int Length, string Replacement)? OutermostMark(string text, int caret)
    {
        if (caret <= 0 || caret > text.Length) return null;
        if (caret < text.Length && Rune.TryGetRuneAt(text, caret, out var next) && (MarkInfo(next.Value) & NonStarter) != 0) return null;
        // the sequence of non-starters before the caret, as runes with their places (bounded: at most a few pieces are looked at)
        var marks = new List<(int Cp, int At, int Length)>();
        var at = caret;
        while (at > 0 && marks.Count < 64 * MaxReorderedMarks)
        {
            var back = char.IsLowSurrogate(text[at - 1]) && at >= 2 && char.IsHighSurrogate(text[at - 2]) ? 2 : 1;
            if (!Rune.TryGetRuneAt(text, at - back, out var rune) || (MarkInfo(rune.Value) & NonStarter) == 0) break;
            at -= back;
            marks.Add((rune.Value, at, back));
        }
        marks.Reverse();
        // a longer sequence than looked at: its pieces are not known from here — the usual deletion
        if (at > 0 && Rune.TryGetRuneAt(text, char.IsLowSurrogate(text[at - 1]) && at >= 2 ? at - 2 : at - 1, out var before)
            && (MarkInfo(before.Value) & NonStarter) != 0) return null;
        if (marks.Count == 0) return null;
        var baseLength = at >= 2 && char.IsLowSurrogate(text[at - 1]) && char.IsHighSurrogate(text[at - 2]) ? 2 : 1;
        var hidden = at > 0 && Rune.TryGetRuneAt(text, at - baseLength, out var letter) ? BaseMarks(letter.Value) : [];
        var order = AmtraOrder(hidden, [.. marks.Select(m => m.Cp)]);
        var outermost = order is null ? hidden.Length + marks.Count - 1 : order[^1];
        if (outermost == hidden.Length + marks.Count - 1) return null;
        if (outermost >= hidden.Length)
        {
            var mark = marks[outermost - hidden.Length];
            return (mark.At, mark.Length, "");
        }
        // the letter's own mark: the letter without it (recomposed from what remains)
        var parts = text.Substring(at - baseLength, baseLength).Normalize(NormalizationForm.FormD).EnumerateRunes().Select(r => r.Value).ToList();
        parts.RemoveAt(1 + outermost);
        var rest = string.Concat(parts.Select(char.ConvertFromUtf32)).Normalize(NormalizationForm.FormC);
        return (at - baseLength, baseLength, rest);
    }

    // ------------------------------------------------------------ stch (OpenType 67.28)

    /// <summary>Most glyphs one stretched character is drawn with (fixed and repeated ones).</summary>
    private const int MaxStretchGlyphs = 512;

    /// <summary>
    /// Stretching glyph decomposition (OpenType 67.28; Unicode 9.3.1 for the Syriac abbreviation mark): a glyph a stch lookup decomposes into an
    /// odd number of glyphs (GSUB type 2) spans the text after it up to the end of the word (letters and marks). The odd-numbered glyphs are fixed
    /// points spread evenly over that width — the first in place of the character itself, before the text, the last at its end — and the
    /// even-numbered ones are repeated to fill the space between them. The first takes the place of the stretched glyph (no advance); the others
    /// follow the text as glyphs attached to its last base (no advance, placed by offset), in the direction of the run. Before, the character kept
    /// its single glyph (No. 1174).
    /// </summary>
    private void Stretch(List<ShapedGlyph> run, string[] scripts, string[] language, bool rightToLeft, Work work)
    {
        if (_gsub is null) return;
        var lookups = _gsub.LookupsFor(["stch"], scripts, language, required: false);
        if (lookups.Length == 0) return;
        static bool InWord(ShapedGlyph g) => string.IsNullOrEmpty(g.Text) || TextRunes.First(g.Text) is var r && (Rune.IsLetter(r)
            || Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark);
        for (var i = 0; i < run.Count - 1 && work.Left > 0; i++)
        {
            if (Decomposition(lookups, run[i].Glyph, work) is not { Length: >= 3 } parts || parts.Length % 2 == 0) continue;
            var end = i + 1;
            while (end < run.Count && InWord(run[end])) end++;
            if (end == i + 1 || !work.Spend(end - i)) continue;
            var last = end - 1;
            while (last > i + 1 && run[last].XAdvance == 0) last--;
            double width = 0, after = 0;
            for (var k = i + 1; k < end; k++) width += run[k].XAdvance;
            for (var k = last; k < end; k++) after += run[k].XAdvance;

            // the glyphs at their places along the text, from its start in reading order
            var fixedCount = (parts.Length + 1) / 2;
            double fixedWidth = 0;
            for (var f = 0; f < fixedCount; f++) fixedWidth += _font.AdvanceWidth(parts[2 * f]);
            var share = (width - fixedWidth) / (fixedCount - 1);
            var placed = new List<(int Glyph, double At)>();
            double at = 0;
            for (var f = 0; f < fixedCount && placed.Count < MaxStretchGlyphs; f++)
            {
                var glyph = parts[2 * f];
                placed.Add((glyph, at));
                at += _font.AdvanceWidth(glyph);
                if (f == fixedCount - 1) break;
                var filler = parts[2 * f + 1];
                double w = _font.AdvanceWidth(filler);
                var copies = share > 0 && w > 0 ? (int)Math.Min(Math.Ceiling(share / w), MaxStretchGlyphs - fixedCount - placed.Count) : 0;
                for (var c = 0; c < copies; c++) placed.Add((filler, at + (copies == 1 ? (share - w) / 2 : c * (share - w) / (copies - 1))));
                at += share;
            }
            if (run.Count + placed.Count > MaxGlyphs || !work.Move(placed.Count + run.Count - end)) return;

            // left to right the glyph's offset from its pen: the stretched glyph at the start of the text, the others at its end; right to left
            // the caller reverses the line, so the start of the text is at its right edge and the last base at its left (attached glyphs keep
            // their distance to it)
            double Offset(int glyph, double place, bool attached) => rightToLeft
                ? (attached ? width - after : 0) - place - _font.AdvanceWidth(glyph)
                : place - (attached ? width : 0);
            var first = run[i];
            first.Glyph = placed[0].Glyph;
            first.XAdvance = 0;
            first.XOffset = Offset(placed[0].Glyph, placed[0].At, false);
            run[i] = first;
            var cluster = run[end - 1].Cluster;
            var inserted = new ShapedGlyph[placed.Count - 1];
            for (var k = 1; k < placed.Count; k++)
                inserted[k - 1] = new ShapedGlyph
                {
                    Glyph = placed[k].Glyph, Cluster = cluster, Text = "", Class = 3, Gates = first.Gates,
                    XOffset = Offset(placed[k].Glyph, placed[k].At, true), AttachBack = end + k - 1 - last,
                };
            run.InsertRange(end, inserted);
            // a mark after the text attached to a glyph before the inserted ones keeps its base
            for (var q = end + inserted.Length; q < run.Count; q++)
                if (run[q].AttachBack > 0 && q - inserted.Length - run[q].AttachBack < end)
                    run[q] = run[q] with { AttachBack = run[q].AttachBack + inserted.Length };
            i = end + inserted.Length - 1;
        }
    }

    /// <summary>The glyph sequence a stch lookup (multiple substitution, format 1) gives for a glyph; null if none covers it.</summary>
    private int[]? Decomposition(int[] lookups, int glyph, Work work)
    {
        var t = _gsub!.T;
        foreach (var index in lookups)
        {
            var lookup = _gsub.Lookup(index);
            if (lookup.Type != 2) continue;
            foreach (var sub in lookup.Subtables)
            {
                if (!work.Spend(1)) return null;
                if (t.U16(sub) != 1) continue;
                var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                if (c < 0 || c >= t.U16(sub + 4)) continue;
                var seq = sub + t.U16(sub + 6 + c * 2);
                var count = Math.Min(t.U16(seq), MaxStretchGlyphs);
                var parts = new int[count];
                for (var k = 0; k < count; k++) parts[k] = t.U16(seq + 2 + k * 2);
                return parts;
            }
        }
        return null;
    }
}
