using System.Runtime.CompilerServices;

namespace PdfKnead.Core.Fonts.Layout;

/// <summary>
/// One shaped glyph: glyph index, origin in the text (index of its first UTF-16 code unit) and position in
/// font units, with advance and offset as in common shaping engines.
/// </summary>
public struct ShapedGlyph
{
    /// <summary>Glyph index in the font.</summary>
    public int Glyph;

    /// <summary>Index of the first UTF-16 code unit of the text this glyph comes from.</summary>
    public int Cluster;

    /// <summary>Horizontal advance in font units.</summary>
    public double XAdvance;

    /// <summary>Vertical advance in font units.</summary>
    public double YAdvance;

    /// <summary>Horizontal offset from the pen position in font units.</summary>
    public double XOffset;

    /// <summary>Vertical offset from the pen position in font units.</summary>
    public double YOffset;

    /// <summary>Text the glyph stands for (several characters for ligatures, empty for further glyphs of a multiple substitution), for ToUnicode.</summary>
    public string Text;

    /// <summary>
    /// For a mark attached by GPOS (to a base, a ligature or a mark): how many glyphs back its base stands (0: none) — a line set right to left
    /// places the mark at its base again (docs/FEHLERGEDAECHTNIS.md, No. 910).
    /// </summary>
    internal int AttachBack;

    /// <summary>The joining form of the character in Arabic script (<see cref="ArabicJoining"/>): it selects isol, init, medi or fina.</summary>
    internal ArabicJoining.Form Form;
}

/// <summary>
/// Text shaper following OpenType, for Latin script and — since No. 910 — Arabic script (joining forms isol, init, medi, fina applied each
/// where the letter takes that form, <see cref="ArabicJoining"/>; script „arab“; marks on ligatures). Characters → glyphs via the cmap,
/// then GSUB (ccmp, locl, rlig, liga, clig, calt — contextual forms of connected script fonts)
/// and GPOS (kern, curs, mark, mkmk, dist) or the legacy kern table. Works like common shaping engines, but only covers what text and
/// signatures in these scripts need: no reordering (the line is set right to left by the caller, No. 910).
/// </summary>
/// <remarks>
/// One shaper is shared per <see cref="OpenTypeFont"/>; <see cref="Shape"/> keeps its state per call and is safe to call from
/// several threads. Not supported: GSUB types 3 and 8, GPOS types 7 and 8, device tables; on a ligature a mark takes the last component.
/// The work per call is bounded (lookup attempts and matching steps, No. 1137).
/// </remarks>
public sealed class TextShaper
{
    private static readonly ConditionalWeakTable<OpenTypeFont, TextShaper> Shapers = new();

    /// <summary>Substitution features in the usual order (they are applied in LookupList order).</summary>
    public static readonly IReadOnlyList<string> DefaultSubstitutions = ["ccmp", "locl", "rlig", "liga", "clig", "calt"];

    /// <summary>Positioning features enabled by default.</summary>
    public static readonly IReadOnlyList<string> DefaultPositioning = ["kern", "curs", "mark", "mkmk", "dist"];

    private readonly OpenTypeFont _font;
    private readonly LayoutTable? _gsub;
    private readonly LayoutTable? _gpos;
    private readonly GlyphDefinitions? _gdef;
    private readonly TableData? _kern;

    private TextShaper(OpenTypeFont font)
    {
        _font = font;
        if (font.TableData("GSUB") is { Length: > 10 } gsub) _gsub = new LayoutTable(gsub.ToArray(), 7);
        if (font.TableData("GPOS") is { Length: > 10 } gpos) _gpos = new LayoutTable(gpos.ToArray(), 9);
        if (font.TableData("GDEF") is { Length: > 10 } gdef) _gdef = new GlyphDefinitions(gdef.ToArray());
        if (font.TableData("kern") is { Length: > 8 } kern) _kern = new TableData(kern.ToArray());
    }

    /// <summary>Returns the shaper of a font.</summary>
    /// <param name="font">The font.</param>
    /// <returns>The shaper, created once per font and then shared.</returns>
    public static TextShaper For(OpenTypeFont font) => Shapers.GetValue(font, f => new TextShaper(f));

    /// <summary>Describes the features and lookup types of the font (command line "schrift").</summary>
    /// <returns>One line per entry.</returns>
    public IEnumerable<string> Describe()
    {
        if (_gsub is not null)
        {
            yield return "GSUB:";
            foreach (var line in _gsub.Describe()) yield return "  " + line;
        }
        if (_gpos is not null)
        {
            yield return "GPOS:";
            foreach (var line in _gpos.Describe()) yield return "  " + line;
        }
        if (_kern is not null) yield return "kern-Tabelle (alt)";
        if (_gdef is not null) yield return "GDEF";
    }

    /// <summary>Shapes a text.</summary>
    /// <param name="text">The text; a tab is shaped as a space.</param>
    /// <param name="features">Features to switch on in addition (tag; it applies to substitution and positioning, the font's tables decide
    /// where it exists) or off ("-liga"), in this order; null for the defaults.</param>
    /// <returns>The glyphs in visual order (the same as text order, since there is no reordering).</returns>
    public ShapedGlyph[] Shape(string text, IEnumerable<string>? features = null)
    {
        var substitutions = new HashSet<string>(DefaultSubstitutions, StringComparer.Ordinal);
        var positioning = new HashSet<string>(DefaultPositioning, StringComparer.Ordinal);
        foreach (var f in features ?? [])
        {
            if (f.StartsWith('-'))
            {
                substitutions.Remove(f[1..]);
                positioning.Remove(f[1..]);
            }
            else
            {
                // a feature can be a substitution or a positioning one (or both); before, it went to GSUB only and a positioning feature
                // could not be switched on (docs/FEHLERGEDAECHTNIS.md, No. 271)
                substitutions.Add(f);
                positioning.Add(f);
            }
        }

        var run = new List<ShapedGlyph>(text.Length);
        var codePoints = new List<int>(text.Length);
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = rune.Value == '\t' ? _font.GlyphForUnicode(' ') : _font.GlyphForUnicode(rune.Value);
            run.Add(new ShapedGlyph { Glyph = glyph, Cluster = index, Text = rune.ToString() });
            codePoints.Add(rune.Value);
            index += rune.Utf16SequenceLength;
        }
        // Arabic script: every letter's joining form, the script „arab“ for the font's lookups (No. 910)
        var arabic = codePoints.Exists(ArabicJoining.Joins);
        var script = arabic ? "arab" : "latn";
        if (arabic)
        {
            var forms = ArabicJoining.Forms(codePoints);
            for (var i = 0; i < run.Count; i++) run[i] = run[i] with { Form = forms[i] };
        }

        // Work limit as in common shaping engines (an operation budget): a damaged or malicious font can nest lookups into each
        // other until shaping hangs; once the limit is reached the rest stays unshaped. Besides the lookup attempts the matching steps
        // count (see Work), each glyph of the input with a generous share (No. 1137)
        var work = new Work(Math.Max(16_384, run.Count * 64), Math.Max(1L << 22, run.Count * 4096L));
        if (_gsub is not null && !arabic)
            foreach (var lookup in _gsub.LookupsFor(substitutions))
                new Substitution(this, run, work).ApplyLookup(lookup);
        else if (_gsub is not null)
        {
            // OpenType Arabic: composition and local forms first, then each joining feature only where the letter takes that form, then
            // the required and other ligatures and the remaining features (the specification's order of stages)
            string[] first = ["ccmp", "locl"];
            foreach (var lookup in _gsub.LookupsFor(first, script)) new Substitution(this, run, work).ApplyLookup(lookup);
            foreach (var (feature, form) in new[] { ("isol", ArabicJoining.Form.Isolated), ("fina", ArabicJoining.Form.Final),
                         ("medi", ArabicJoining.Form.Medial), ("init", ArabicJoining.Form.Initial) })
                foreach (var lookup in _gsub.LookupsFor([feature], script, required: false))
                    new Substitution(this, run, work).ApplyLookup(lookup, form);
            var rest = substitutions.Where(f => !first.Contains(f)).ToList();
            foreach (var lookup in _gsub.LookupsFor(rest, script, required: false)) new Substitution(this, run, work).ApplyLookup(lookup);
        }

        for (var i = 0; i < run.Count; i++)
        {
            var g = run[i];
            g.XAdvance = _font.AdvanceWidth(g.Glyph);
            // Marks (GDEF class 3) do not advance
            if (_gdef?.GlyphClass(g.Glyph) == 3 && _gpos is not null) g.XAdvance = 0;
            run[i] = g;
        }

        var kerned = false;
        if (_gpos is not null)
        {
            var lookups = _gpos.LookupsFor(positioning, script);
            kerned = positioning.Contains("kern") && _gpos.LookupsFor(["kern"], script).Length > 0;
            foreach (var lookup in lookups) new Positioning(this, run, work).ApplyLookup(lookup);
        }
        if (!kerned && positioning.Contains("kern") && _kern is { } kern) LegacyKerning(kern, run, work);
        return [.. run];
    }

    // ------------------------------------------------------------ Shared

    /// <summary>Maximum number of glyphs produced by shaping (multiple substitutions would otherwise multiply them).</summary>
    private const int MaxGlyphs = 100_000;

    /// <summary>The RIGHT_TO_LEFT flag of a lookup (bit 0 of its lookupFlag).</summary>
    private const int RightToLeft = 1;

    /// <summary>
    /// Aligns the exit anchor of glyph <paramref name="i"/> with the entry anchor of glyph <paramref name="j"/>, as the specification asks
    /// (horizontal layout). In the direction of the line the advance of the first glyph (in logical order) is adjusted: that moves the
    /// second so that the anchors meet. Across the line the placement of one glyph is adjusted: of the second if the RIGHT_TO_LEFT flag
    /// is clear; of the first if it is set — then the last glyph of the connected sequence keeps its place and the ones before it, joined
    /// already (<paramref name="chain"/>), move with the first.
    /// </summary>
    internal static void Join(List<ShapedGlyph> run, int i, int j, (int X, int Y) exit, (int X, int Y) entry, bool rightToLeft, List<int> chain)
    {
        var first = run[i];
        var second = run[j];
        // the exit anchor lies at pen + offset + exit of the first glyph, the entry anchor at the next pen + offset + entry of the second
        first.XAdvance = first.XOffset + exit.X - (second.XOffset + entry.X);
        run[i] = first;
        if (!rightToLeft)
        {
            second.YOffset = first.YOffset + exit.Y - entry.Y;
            run[j] = second;
            chain.Clear();
            return;
        }
        // a new connected sequence unless the first glyph ends the one so far
        if (chain.Count == 0 || chain[^1] != i)
        {
            chain.Clear();
            chain.Add(i);
        }
        var move = second.YOffset + entry.Y - exit.Y - first.YOffset;
        foreach (var k in chain)
        {
            var g = run[k];
            g.YOffset += move;
            run[k] = g;
        }
        chain.Add(j);
    }

    /// <summary>
    /// Remaining work of one <see cref="Shape"/> call (one instance per call, hence thread-safe): the lookup attempts at a position, and
    /// the matching steps — each subtable, rule, ligature and substitution record tried, each glyph a context, a ligature or a mark
    /// passes on its way, and the glyphs moved when the run changes length. Counting the attempts alone let a font whose lookups,
    /// rule sets and ligature sets are filled up to 65 535 entries make one attempt cost billions of steps, and allocate a position
    /// array as long as the rule claimed (No. 1137).
    /// </summary>
    private sealed class Work(int ops, long steps)
    {
        public int Left = ops;

        private long _steps = steps;

        public bool Take() => --Left >= 0;

        /// <summary>Spends matching steps; once they are used up, no further lookup is applied (the rest stays unshaped).</summary>
        public bool Spend(long n)
        {
            if ((_steps -= n) >= 0) return true;
            Left = 0;
            return false;
        }

        /// <summary>Glyphs moved in the run (insertions, removals): cheap block copies, one step per 64.</summary>
        public bool Move(int glyphs) => Spend(1 + glyphs / 64);
    }

    private bool Skips(Lookup lookup, int glyph) => _gdef?.Skips(lookup, glyph) == true;

    /// <summary>Next position after <paramref name="i"/> that the lookup does not skip; -1 = none.</summary>
    private int Next(List<ShapedGlyph> run, Lookup lookup, int i)
    {
        for (var j = i + 1; j < run.Count; j++)
            if (!Skips(lookup, run[j].Glyph)) return j;
        return -1;
    }

    private int Previous(List<ShapedGlyph> run, Lookup lookup, int i)
    {
        for (var j = i - 1; j >= 0; j--)
            if (!Skips(lookup, run[j].Glyph)) return j;
        return -1;
    }

    /// <summary><see cref="Next(List{ShapedGlyph}, Lookup, int)"/>, the glyphs passed counted as steps; -1 also when the steps are used up.</summary>
    private int Next(List<ShapedGlyph> run, Lookup lookup, int i, Work work)
    {
        var j = Next(run, lookup, i);
        return work.Spend(j < 0 ? run.Count - i : j - i) ? j : -1;
    }

    private int Previous(List<ShapedGlyph> run, Lookup lookup, int i, Work work)
    {
        var j = Previous(run, lookup, i);
        return work.Spend(j < 0 ? i + 1 : i - j) ? j : -1;
    }

    /// <summary>
    /// Matches a context (GSUB 5/6): input from <paramref name="start"/>, backtrack (backwards) and
    /// lookahead. The match functions receive the position within their sequence and the glyph.
    /// </summary>
    private bool MatchContext(Work work, List<ShapedGlyph> run, Lookup lookup, int start,
        int backtrack, Func<int, int, bool> backtrackMatch,
        int input, Func<int, int, bool> inputMatch,
        int lookahead, Func<int, int, bool> lookaheadMatch,
        out int[] positions)
    {
        positions = [];
        var length = Math.Max(1, input);
        // A rule can match only glyphs that are there: one whose input and lookahead are longer than the rest of the run, or whose
        // backtrack is longer than the glyphs before it, never matches — decided before the positions are allocated (No. 1137)
        if (!work.Spend(1) || length + lookahead > run.Count - start || backtrack > start) return false;
        positions = new int[length];
        positions[0] = start;
        var j = start;
        for (var k = 1; k < input; k++)
        {
            j = Next(run, lookup, j, work);
            if (j < 0 || !inputMatch(k, run[j].Glyph)) return false;
            positions[k] = j;
        }
        var after = j;
        for (var k = 0; k < lookahead; k++)
        {
            after = Next(run, lookup, after, work);
            if (after < 0 || !lookaheadMatch(k, run[after].Glyph)) return false;
        }
        var before = start;
        for (var k = 0; k < backtrack; k++)
        {
            before = Previous(run, lookup, before, work);
            if (before < 0 || !backtrackMatch(k, run[before].Glyph)) return false;
        }
        return true;
    }

    // ------------------------------------------------------------ GSUB

    private readonly struct Substitution(TextShaper shaper, List<ShapedGlyph> run, Work work)
    {
        private TableData T => shaper._gsub!.T;

        /// <param name="index">The lookup.</param>
        /// <param name="only">Only at glyphs of this joining form (Arabic isol, init, medi, fina); null: everywhere.</param>
        public void ApplyLookup(int index, ArabicJoining.Form? only = null)
        {
            var lookup = shaper._gsub!.Lookup(index);
            for (var i = 0; i < run.Count && run.Count < MaxGlyphs && work.Left > 0;)
            {
                if (only is { } form && run[i].Form != form || shaper.Skips(lookup, run[i].Glyph) || !ApplyAt(lookup, i, 0, out var next)) i++;
                else i = Math.Max(next, i + 1);
            }
        }

        /// <summary>Applies a lookup at exactly one position (also nested from a context); next = position after it.</summary>
        private bool ApplyAt(Lookup lookup, int i, int depth, out int next)
        {
            next = i + 1;
            if (depth > 8 || !work.Take()) return false;
            foreach (var sub in lookup.Subtables)
                if (!work.Spend(1)) return false;
                else if (ApplySubtable(lookup, sub, i, depth, out next)) return true;
            return false;
        }

        private bool ApplySubtable(Lookup lookup, int sub, int i, int depth, out int next)
        {
            next = i + 1;
            var t = T;
            var glyph = run[i].Glyph;
            var format = t.U16(sub);
            switch (lookup.Type)
            {
                case 1:
                {
                    var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                    if (c < 0) return false;
                    Replace(i, format == 1 ? (glyph + t.I16(sub + 4)) & 0xFFFF : t.U16(sub + 6 + c * 2));
                    return true;
                }
                case 2:
                {
                    var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                    if (c < 0 || c >= t.U16(sub + 4)) return false;
                    var seq = sub + t.U16(sub + 6 + c * 2);
                    var count = t.U16(seq);
                    // A count of 0 (deleting the glyph, not allowed by the specification) would otherwise skip the next glyph
                    if (count == 0 || run.Count + count > MaxGlyphs || !work.Move(count + run.Count - i)) return false;
                    var original = run[i];
                    var glyphs = new ShapedGlyph[count];
                    for (var k = 0; k < count; k++) glyphs[k] = original with { Glyph = t.U16(seq + 2 + k * 2), Text = k == 0 ? original.Text : "" };
                    // in one block: inserting glyph by glyph moved the rest of the run once per glyph (No. 1137)
                    run[i] = glyphs[0];
                    run.InsertRange(i + 1, glyphs[1..]);
                    next = i + count;
                    return true;
                }
                case 4:
                    return Ligature(lookup, sub, i, out next);
                case 5:
                    return Context(lookup, sub, i, depth, chained: false, out next);
                case 6:
                    return Context(lookup, sub, i, depth, chained: true, out next);
                default:
                    return false;   // 3 (alternates to choose from), 8 (reverse chaining, Arabic): not needed for Latin script
            }
        }

        private void Replace(int i, int glyph)
        {
            var g = run[i];
            g.Glyph = glyph;
            run[i] = g;
        }

        private bool Ligature(Lookup lookup, int sub, int i, out int next)
        {
            next = i + 1;
            var t = T;
            var c = t.Coverage(sub + t.U16(sub + 2), run[i].Glyph);
            if (c < 0 || c >= t.U16(sub + 4)) return false;
            var set = sub + t.U16(sub + 6 + c * 2);
            for (var l = 0; l < t.U16(set); l++)
            {
                if (!work.Spend(1)) return false;
                var lig = set + t.U16(set + 2 + l * 2);
                var components = t.U16(lig + 2);
                // damaged: a ligature made of nothing; one of more glyphs than the rest of the run never matches (No. 1137)
                if (components == 0 || components > run.Count - i) continue;
                var positions = new int[components];
                positions[0] = i;
                var ok = true;
                for (var k = 1; k < components && ok; k++)
                {
                    positions[k] = shaper.Next(run, lookup, positions[k - 1], work);
                    ok = positions[k] >= 0 && run[positions[k]].Glyph == t.U16(lig + 4 + (k - 1) * 2);
                }
                if (!ok || !work.Move(components * (run.Count - i))) continue;
                var text = "";
                foreach (var p in positions) text += run[p].Text;
                var first = run[i];
                first.Glyph = t.U16(lig);
                first.Text = text;
                run[i] = first;
                for (var k = components - 1; k >= 1; k--) run.RemoveAt(positions[k]);
                return true;
            }
            return false;
        }

        /// <summary>Contextual substitution (type 5) and chained contextual substitution (type 6), formats 1–3 each.</summary>
        private bool Context(Lookup lookup, int sub, int i, int depth, bool chained, out int next)
        {
            next = i + 1;
            var t = T;
            var glyph = run[i].Glyph;
            var format = t.U16(sub);
            if (!chained)
            {
                switch (format)
                {
                    case 1:
                    {
                        var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                        if (c < 0 || c >= t.U16(sub + 4)) return false;
                        var set = sub + t.U16(sub + 6 + c * 2);
                        for (var r = 0; r < t.U16(set); r++)
                        {
                            var rule = set + t.U16(set + 2 + r * 2);
                            int count = t.U16(rule), subs = t.U16(rule + 2);
                            var tt = t;
                            if (shaper.MatchContext(work, run, lookup, i, 0, None, count, (k, g) => g == tt.U16(rule + 4 + (k - 1) * 2), 0, None, out var pos))
                                return Apply(pos, rule + 4 + (count - 1) * 2, subs, depth, out next);
                        }
                        return false;
                    }
                    case 2:
                    {
                        if (t.Coverage(sub + t.U16(sub + 2), glyph) < 0) return false;
                        var classDef = sub + t.U16(sub + 4);
                        var cls = t.Class(classDef, glyph);
                        if (cls >= t.U16(sub + 6) || t.U16(sub + 8 + cls * 2) == 0) return false;
                        var set = sub + t.U16(sub + 8 + cls * 2);
                        for (var r = 0; r < t.U16(set); r++)
                        {
                            var rule = set + t.U16(set + 2 + r * 2);
                            int count = t.U16(rule), subs = t.U16(rule + 2);
                            var tt = t;
                            if (shaper.MatchContext(work, run, lookup, i, 0, None, count, (k, g) => tt.Class(classDef, g) == tt.U16(rule + 4 + (k - 1) * 2), 0, None, out var pos))
                                return Apply(pos, rule + 4 + (count - 1) * 2, subs, depth, out next);
                        }
                        return false;
                    }
                    case 3:
                    {
                        int count = t.U16(sub + 2), subs = t.U16(sub + 4);
                        var tt = t;
                        if (count == 0 || t.Coverage(sub + t.U16(sub + 6), glyph) < 0) return false;
                        if (!shaper.MatchContext(work, run, lookup, i, 0, None, count, (k, g) => tt.Coverage(sub + tt.U16(sub + 6 + k * 2), g) >= 0, 0, None, out var pos))
                            return false;
                        return Apply(pos, sub + 6 + count * 2, subs, depth, out next);
                    }
                    default:
                        return false;
                }
            }

            switch (format)
            {
                case 1:
                {
                    var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                    if (c < 0 || c >= t.U16(sub + 4)) return false;
                    var set = sub + t.U16(sub + 6 + c * 2);
                    for (var r = 0; r < t.U16(set); r++)
                    {
                        var rule = set + t.U16(set + 2 + r * 2);
                        var (bt, inp, la, records, subs) = ChainParts(rule);
                        var tt = t;
                        if (shaper.MatchContext(work, run, lookup, i,
                                tt.U16(rule), (k, g) => g == tt.U16(bt + k * 2),
                                tt.U16(inp - 2), (k, g) => g == tt.U16(inp + (k - 1) * 2),
                                tt.U16(la - 2), (k, g) => g == tt.U16(la + k * 2), out var pos))
                            return Apply(pos, records, subs, depth, out next);
                    }
                    return false;
                }
                case 2:
                {
                    if (t.Coverage(sub + t.U16(sub + 2), glyph) < 0) return false;
                    int backDef = sub + t.U16(sub + 4), inputDef = sub + t.U16(sub + 6), aheadDef = sub + t.U16(sub + 8);
                    var cls = t.Class(inputDef, glyph);
                    if (cls >= t.U16(sub + 10) || t.U16(sub + 12 + cls * 2) == 0) return false;
                    var set = sub + t.U16(sub + 12 + cls * 2);
                    for (var r = 0; r < t.U16(set); r++)
                    {
                        var rule = set + t.U16(set + 2 + r * 2);
                        var (bt, inp, la, records, subs) = ChainParts(rule);
                        var tt = t;
                        if (shaper.MatchContext(work, run, lookup, i,
                                tt.U16(rule), (k, g) => tt.Class(backDef, g) == tt.U16(bt + k * 2),
                                tt.U16(inp - 2), (k, g) => tt.Class(inputDef, g) == tt.U16(inp + (k - 1) * 2),
                                tt.U16(la - 2), (k, g) => tt.Class(aheadDef, g) == tt.U16(la + k * 2), out var pos))
                            return Apply(pos, records, subs, depth, out next);
                    }
                    return false;
                }
                case 3:
                {
                    var btCount = t.U16(sub + 2);
                    var inp = sub + 4 + btCount * 2;
                    var inCount = t.U16(inp);
                    var la = inp + 2 + inCount * 2;
                    var laCount = t.U16(la);
                    var records = la + 2 + laCount * 2;
                    var tt = t;
                    if (inCount == 0 || t.Coverage(sub + t.U16(inp + 2), glyph) < 0) return false;
                    if (!shaper.MatchContext(work, run, lookup, i,
                            btCount, (k, g) => tt.Coverage(sub + tt.U16(sub + 4 + k * 2), g) >= 0,
                            inCount, (k, g) => tt.Coverage(sub + tt.U16(inp + 2 + k * 2), g) >= 0,
                            laCount, (k, g) => tt.Coverage(sub + tt.U16(la + 2 + k * 2), g) >= 0, out var pos))
                        return false;
                    return Apply(pos, records + 2, t.U16(records), depth, out next);
                }
                default:
                    return false;
            }
        }

        /// <summary>Chained rule (formats 1/2): start of the backtrack, input (from the second glyph) and lookahead sequences, and the substitution records.</summary>
        private (int Backtrack, int Input, int Lookahead, int Records, int Count) ChainParts(int rule)
        {
            var t = T;
            var bt = rule + 2;
            var inCountAt = bt + t.U16(rule) * 2;
            var inp = inCountAt + 2;
            var laCountAt = inp + Math.Max(0, t.U16(inCountAt) - 1) * 2;
            var la = laCountAt + 2;
            var subsAt = la + t.U16(laCountAt) * 2;
            return (bt, inp, la, subsAt + 2, t.U16(subsAt));
        }

        private static bool None(int k, int g) => false;

        /// <summary>
        /// Applies the nested lookups at the input positions (SubstLookupRecords). A record names its place in the input sequence as the
        /// records before it left it (OpenType GSUB, 21.4.6): glyphs a multiple substitution adds after a place are places of their own,
        /// those a ligature joins into its first component are gone. Before, the places were counted in the sequence as matched, and a
        /// record after a ligature hit the wrong glyph (docs/FEHLERGEDAECHTNIS.md, No. 1142).
        /// </summary>
        private bool Apply(int[] matched, int records, int count, int depth, out int next)
        {
            var positions = new List<int>(matched);
            var end = matched[^1] + 1;
            for (var r = 0; r < count && work.Spend(1); r++)
            {
                var seq = T.U16(records + r * 4);
                var lookupIndex = T.U16(records + r * 4 + 2);
                if (seq >= positions.Count) continue;
                var before = run.Count;
                shaper.ApplyNested(this, lookupIndex, positions[seq], depth + 1);
                var delta = run.Count - before;
                if (delta == 0) continue;
                end += delta;
                work.Spend(positions.Count - seq + Math.Max(0, delta));
                if (delta > 0)
                {
                    // the new glyphs follow the place where they came from
                    for (var k = seq + 1; k < positions.Count; k++) positions[k] += delta;
                    positions.InsertRange(seq + 1, Enumerable.Range(positions[seq] + 1, delta));
                }
                else
                {
                    // joined into the glyph at this place: the places after it that it took are gone, the later ones move up
                    var gone = Math.Min(-delta, positions.Count - seq - 1);
                    positions.RemoveRange(seq + 1, gone);
                    for (var k = seq + 1; k < positions.Count; k++) positions[k] = Math.Max(positions[seq] + 1, positions[k] + delta);
                }
            }
            next = Math.Max(matched[0] + 1, end);
            return true;
        }

        internal void Nested(int lookupIndex, int position, int depth)
        {
            if (position < 0 || position >= run.Count) return;
            var lookup = shaper._gsub!.Lookup(lookupIndex);
            ApplyAt(lookup, position, depth, out _);
        }
    }

    private void ApplyNested(Substitution substitution, int lookupIndex, int position, int depth) =>
        substitution.Nested(lookupIndex, position, depth);

    // ------------------------------------------------------------ GPOS

    private readonly struct Positioning(TextShaper shaper, List<ShapedGlyph> run, Work work)
    {
        private TableData T => shaper._gpos!.T;

        /// <summary>The glyphs joined so far by cursive attachment with the RIGHT_TO_LEFT flag (one connected sequence; see <see cref="Join"/>).</summary>
        private readonly List<int> _chain = [];

        public void ApplyLookup(int index)
        {
            var lookup = shaper._gpos!.Lookup(index);
            for (var i = 0; i < run.Count && work.Left > 0;)
            {
                if (shaper.Skips(lookup, run[i].Glyph) || !ApplyAt(lookup, i, 0, out var next)) i++;
                else i = Math.Max(next, i + 1);
            }
        }

        private bool ApplyAt(Lookup lookup, int i, int depth, out int next)
        {
            next = i + 1;
            if (depth > 8 || !work.Take()) return false;
            foreach (var sub in lookup.Subtables)
                if (!work.Spend(1)) return false;
                else if (ApplySubtable(lookup, sub, i, depth, out next)) return true;
            return false;
        }

        private bool ApplySubtable(Lookup lookup, int sub, int i, int depth, out int next)
        {
            next = i + 1;
            var t = T;
            var glyph = run[i].Glyph;
            var format = t.U16(sub);
            switch (lookup.Type)
            {
                case 1:
                {
                    var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                    if (c < 0) return false;
                    var valueFormat = t.U16(sub + 4);
                    var value = format == 1 ? sub + 6 : sub + 8 + c * ValueSize(valueFormat);
                    AddValue(i, sub, value, valueFormat);
                    return true;
                }
                case 2:
                {
                    var c = t.Coverage(sub + t.U16(sub + 2), glyph);
                    if (c < 0) return false;
                    var j = shaper.Next(run, lookup, i);
                    if (j < 0) return false;
                    int vf1 = t.U16(sub + 4), vf2 = t.U16(sub + 6);
                    int size1 = ValueSize(vf1), size2 = ValueSize(vf2);
                    var second = run[j].Glyph;
                    if (format == 1)
                    {
                        if (c >= t.U16(sub + 8)) return false;
                        var set = sub + t.U16(sub + 10 + c * 2);
                        var record = 2 + size1 + size2;
                        int lo = 0, hi = t.U16(set) - 1;
                        while (lo <= hi)
                        {
                            var mid = (lo + hi) >> 1;
                            var at = set + 2 + mid * record;
                            var g = t.U16(at);
                            if (g < second) lo = mid + 1;
                            else if (g > second) hi = mid - 1;
                            else
                            {
                                AddValue(i, set, at + 2, vf1);
                                AddValue(j, set, at + 2 + size1, vf2);
                                next = vf2 != 0 ? j + 1 : j;
                                return true;
                            }
                        }
                        return false;
                    }
                    if (format == 2)
                    {
                        var class1 = t.Class(sub + t.U16(sub + 8), glyph);
                        var class2 = t.Class(sub + t.U16(sub + 10), second);
                        int count1 = t.U16(sub + 12), count2 = t.U16(sub + 14);
                        if (class1 >= count1 || class2 >= count2) return false;
                        var at = sub + 16 + (class1 * count2 + class2) * (size1 + size2);
                        AddValue(i, sub, at, vf1);
                        AddValue(j, sub, at + size1, vf2);
                        next = vf2 != 0 ? j + 1 : j;
                        return true;
                    }
                    return false;
                }
                case 3:
                    return Cursive(lookup, sub, i);
                case 4 or 6:
                    return MarkToBase(lookup, sub, i, toMark: lookup.Type == 6);
                case 5:
                    return MarkToLigature(sub, i);
                case 7 or 8:
                    return false;   // contextual positioning: rare in Latin script, not supported
                default:
                    return false;
            }
        }

        private static int ValueSize(int format) => 2 * System.Numerics.BitOperations.PopCount((uint)(format & 0xFF));

        /// <summary>Adds a ValueRecord (offset and advance) to a glyph; device tables are ignored.</summary>
        private void AddValue(int i, int parent, int at, int format)
        {
            var t = T;
            var g = run[i];
            var k = 0;
            if ((format & 1) != 0) g.XOffset += t.I16(at + 2 * k++);
            if ((format & 2) != 0) g.YOffset += t.I16(at + 2 * k++);
            if ((format & 4) != 0) g.XAdvance += t.I16(at + 2 * k++);
            if ((format & 8) != 0) g.YAdvance += t.I16(at + 2 * k);
            run[i] = g;
        }

        private (int X, int Y)? Anchor(int at) => at == 0 ? null : (T.I16(at + 2), T.I16(at + 4));

        /// <summary>
        /// Cursive attachment (lookup type 3, CursivePosFormat1): the exit anchor of the glyph aligned with the entry anchor of the following
        /// glyph the lookup does not skip, both from the EntryExit record of their coverage index; a NULL anchor on either side: no
        /// adjustment. Written from the OpenType specification (GPOS, „Lookup type 3 subtable: cursive attachment positioning“); no code of
        /// another shaper was used (the calculation taken over before was replaced on 16.11.2026; docs/LIZENZPRUEFUNG.md).
        /// </summary>
        private bool Cursive(Lookup lookup, int sub, int i)
        {
            var t = T;
            var cov = sub + t.U16(sub + 2);
            var c = t.Coverage(cov, run[i].Glyph);
            if (c < 0) return false;
            var j = shaper.Next(run, lookup, i);
            if (j < 0) return false;
            var cj = t.Coverage(cov, run[j].Glyph);
            if (cj < 0) return false;
            var exitAt = t.U16(sub + 6 + c * 4 + 2);
            var entryAt = t.U16(sub + 6 + cj * 4);
            if (Anchor(exitAt == 0 ? 0 : sub + exitAt) is not { } exit || Anchor(entryAt == 0 ? 0 : sub + entryAt) is not { } entry) return false;
            // a connected sequence moves as a whole at each join (No. 1137)
            if (!work.Spend(_chain.Count)) return false;
            Join(run, i, j, exit, entry, (lookup.Flag & RightToLeft) != 0, _chain);
            return true;
        }

        /// <summary>Attaches a mark to a base glyph (type 4) or to a preceding mark (type 6).</summary>
        private bool MarkToBase(Lookup lookup, int sub, int i, bool toMark)
        {
            var t = T;
            var markIndex = t.Coverage(sub + t.U16(sub + 2), run[i].Glyph);
            if (markIndex < 0) return false;
            // Base: the nearest preceding glyph that is not a mark (for mark-to-mark: the preceding glyph)
            var b = i - 1;
            if (!toMark)
                while (b >= 0 && shaper._gdef?.GlyphClass(run[b].Glyph) == 3) b--;
            // the marks passed and, in Attach, the advances summed (No. 1137)
            if (b < 0 || !work.Spend(2 * (i - b))) return false;
            var baseIndex = t.Coverage(sub + t.U16(sub + 4), run[b].Glyph);
            if (baseIndex < 0) return false;
            var classCount = t.U16(sub + 6);
            var markArray = sub + t.U16(sub + 8);
            var baseArray = sub + t.U16(sub + 10);
            if (markIndex >= t.U16(markArray) || baseIndex >= t.U16(baseArray)) return false;
            var markRecord = markArray + 2 + markIndex * 4;
            var cls = t.U16(markRecord);
            if (cls >= classCount) return false;
            var markAnchor = Anchor(markArray + t.U16(markRecord + 2));
            var baseAnchorOffset = t.U16(baseArray + 2 + (baseIndex * classCount + cls) * 2);
            if (markAnchor is not { } m || baseAnchorOffset == 0 || Anchor(baseArray + baseAnchorOffset) is not { } ba) return false;
            Attach(i, b, ba, m);
            return true;
        }

        /// <summary>Places mark <paramref name="i"/> so that its anchor meets the anchor of glyph <paramref name="b"/> before it.</summary>
        private void Attach(int i, int b, (int X, int Y) baseAnchor, (int X, int Y) markAnchor)
        {
            // Distance from the base to the mark: the advances in between
            double advance = 0;
            for (var k = b; k < i; k++) advance += run[k].XAdvance;
            var g = run[i];
            g.XOffset = run[b].XOffset + baseAnchor.X - markAnchor.X - advance;
            g.YOffset = run[b].YOffset + baseAnchor.Y - markAnchor.Y;
            g.AttachBack = i - b;   // for a line set right to left (No. 910)
            run[i] = g;
        }

        /// <summary>
        /// Attaches a mark to a ligature (lookup type 5, MarkLigPosFormat1; OpenType GPOS, „Lookup type 5 subtable: mark-to-ligature
        /// attachment positioning“): the nearest preceding glyph that is not a mark, with the anchor of the mark's class in one of its
        /// components. The component a mark belongs to is not tracked through substitution here; the last one is taken — the mark follows
        /// the ligature in the text, so it belongs to its last character in the common case (lam-alef with a mark on the alef) (No. 910).
        /// </summary>
        private bool MarkToLigature(int sub, int i)
        {
            var t = T;
            var markIndex = t.Coverage(sub + t.U16(sub + 2), run[i].Glyph);
            if (markIndex < 0) return false;
            var b = i - 1;
            while (b >= 0 && shaper._gdef?.GlyphClass(run[b].Glyph) == 3) b--;
            if (b < 0 || !work.Spend(2 * (i - b))) return false;
            var ligatureIndex = t.Coverage(sub + t.U16(sub + 4), run[b].Glyph);
            if (ligatureIndex < 0) return false;
            var classCount = t.U16(sub + 6);
            var markArray = sub + t.U16(sub + 8);
            var ligatureArray = sub + t.U16(sub + 10);
            if (classCount == 0 || markIndex >= t.U16(markArray) || ligatureIndex >= t.U16(ligatureArray)) return false;
            var markRecord = markArray + 2 + markIndex * 4;
            var cls = t.U16(markRecord);
            if (cls >= classCount) return false;
            var attach = ligatureArray + t.U16(ligatureArray + 2 + ligatureIndex * 2);
            var components = t.U16(attach);
            if (components == 0) return false;
            var anchorOffset = t.U16(attach + 2 + ((components - 1) * classCount + cls) * 2);
            if (Anchor(markArray + t.U16(markRecord + 2)) is not { } m || anchorOffset == 0 || Anchor(attach + anchorOffset) is not { } la) return false;
            Attach(i, b, la, m);
            return true;
        }
    }

    /// <summary>
    /// Legacy kern table (format 0, horizontal), used when GPOS has no "kern" feature. Each subtable passing the run counts as steps: the
    /// table can hold 65 535 overlapping subtables of six bytes claiming 65 535 pairs each, searched for every pair of glyphs (No. 1137).
    /// </summary>
    private static void LegacyKerning(TableData kern, List<ShapedGlyph> run, Work work)
    {
        if (kern.U16(0) != 0) return;   // only the Microsoft version
        var tables = kern.U16(2);
        var at = 4;
        for (var s = 0; s < tables && at < kern.Length; s++)
        {
            var length = kern.U16(at + 2);
            var coverage = kern.U16(at + 4);
            if (!work.Spend(1)) return;
            if ((coverage >> 8) == 0 && (coverage & 1) != 0 && (coverage & 4) == 0)
            {
                var pairs = kern.U16(at + 6);
                // a binary search per pair of glyphs
                if (!work.Spend((long)run.Count * (1 + System.Numerics.BitOperations.Log2((uint)pairs + 1)))) return;
                for (var i = 0; i + 1 < run.Count; i++)
                {
                    var key = ((uint)run[i].Glyph << 16) | (uint)run[i + 1].Glyph;
                    int lo = 0, hi = pairs - 1;
                    while (lo <= hi)
                    {
                        var mid = (lo + hi) >> 1;
                        var p = at + 14 + mid * 6;
                        var k = ((uint)kern.U16(p) << 16) | (uint)kern.U16(p + 2);
                        if (k < key) lo = mid + 1;
                        else if (k > key) hi = mid - 1;
                        else
                        {
                            var g = run[i];
                            g.XAdvance += kern.I16(p + 4);
                            run[i] = g;
                            break;
                        }
                    }
                }
            }
            at += Math.Max(length, 6);
        }
    }
}
