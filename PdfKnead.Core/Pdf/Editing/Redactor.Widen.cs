using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Pdf.Editing;

// Hiding the length of redacted words (docs/RECHERCHE-VERSTECKEN.md, B-13): a bar as wide as the word it covers tells its length, and with
// the widths of the font a dictionary narrows the word down. The gap in the line is widened to the next step of LengthStep em, the bar
// with it; the words after it move to the right and the line takes the room from its word spaces up to the next bar (or the end of its
// part), or from free space after its end. Where the room is not enough, the gap widens only as far as it goes (counted as partly shown). A
// justified line is set anew as a whole (ResetJustified): its spacing was chosen for the line with the removed word and would give its
// width away. Upright lines only. This is the plan; whether it went well is checked afterwards against the page (Redactor.MoveCheck.cs),
// and a line that fails is frozen — nothing here needs to foresee every way a file is built.
public static partial class Redactor
{
    /// <summary>Step to which the gap of a redacted word is widened, in em of its text (settable for measuring, <see cref="LengthStepEm"/>).</summary>
    private static double LengthStep => LengthStepEm;

    /// <summary>
    /// The step of <see cref="LengthStep"/> — changed only by measuring tools (the attack on glyph positions with other steps). 2 em: measured
    /// with <c>pdf-knead laengenangriff</c>, a list of 300 names leaves the informed attacker about 114 candidates (1.5 em: 92, 2.5 em: 140),
    /// with a few more lines too tight to widen (docs/PRUEFUNGEN.md, section 6j; chosen by the user).
    /// </summary>
    public static double LengthStepEm { get; set; } = 2.0;

    /// <summary>A gap wider than this (em) separates parts of a line (columns, cells of a table): nothing moves across it.</summary>
    private const double PartGap = 1.0;

    /// <summary>
    /// Smallest share of a word space that stays when the line makes room (and at least <see cref="MinWordGap"/> em). It acts only on gaps
    /// above 0.56 em; its reason is the look (no gap halves more than the others), not what readers need.
    /// </summary>
    private const double KeptWordGap = 0.5;

    /// <summary>
    /// The gap (em) from which the strictest reference reader keeps two words apart (the two others from 0.10 and 0.15 em): a gap of the
    /// page at least this wide is a space to every reader, and rounding a word start never narrows it below what it was
    /// (<c>LineWriter</c>; docs/FEHLERGEDAECHTNIS.md, No. 451).
    /// </summary>
    private const double ReaderSpaceGap = 0.28;

    /// <summary>
    /// The smallest gap between two words (em) the line plans when it makes room and when a justified line is set anew
    /// (<c>ResetJustified</c>), and keeps when a word start is rounded to the grid (<c>LineWriter</c>): a third of an em, above
    /// <see cref="ReaderSpaceGap"/> with room to spare, so that no program writes the words together — with 0.2 the strictest reader did,
    /// and searching, copying and reading aloud failed after a widened gap (No. 451); 0.28 was its limit with nothing to spare
    /// (decided on 4.11.2026: „a somewhat larger value, so that it is certain“; No. 458). The informed attacker gains nothing;
    /// the simple one sees the length on more lines (length attack, docs/PRUEFUNGEN.md §6j). A gap already smaller gives no room and is
    /// not widened.
    /// </summary>
    private const double MinWordGap = 1.0 / 3;

    /// <summary>
    /// The gap a line that makes room plans between two words: <see cref="MinWordGap"/> and one step of the grid more — the word starts are
    /// rounded to the grid afterwards (<c>LineWriter</c>, half a step each), and a gap planned at exactly the minimum came out 0.04 em
    /// smaller (docs/FEHLERGEDAECHTNIS.md, No. 451).
    /// </summary>
    private static double ReadersGap(double em) => MinWordGap * em + PositionGrid;

    /// <summary>A generous width of a space (em): a gap between words beyond it was widened (justified), not a plain space.</summary>
    private const double SpaceWidth = 1.0 / 3;

    /// <summary>
    /// The em is at most this many times the highest character of a part: the highest letters of a word reach about 0.7 to 0.95 em, so only
    /// fonts whose characters are drawn far smaller than their size are bounded.
    /// </summary>
    private const double InkToEm = 1.6;

    /// <summary>A character in its text operator.</summary>
    private readonly record struct GlyphKey(int Op, int Element, int Start);

    /// <summary>The em of a character along its line (page units).</summary>
    private static double GlyphEm(GlyphLayout g) => Math.Abs(g.FontSize * g.Scale) * g.PerText;

    /// <summary>Where a character ends along its line (page units): its start and its width, without the spacing it was shown with.</summary>
    private static double GlyphEnd(GlyphLayout g) => g.U + (g.Advance - (g.CharSpacing + (g.Space ? g.WordSpacing : 0)) * g.Scale) * g.PerText;

    /// <summary>
    /// What hiding the lengths does to a content stream: how far each character that stays moves along its line, the pieces the bars grow by
    /// (page space), how many gaps could not be widened to their step, and for each removed character where its run starts now (page space,
    /// along the line): a text operator that begins with removed characters is placed there when its position is written anew — written
    /// where its first character stood, it told the width of what was removed before it in the run (the first half of a word set in two
    /// operators, small capitals) or the spacing before the run (a word placed on its own; docs/FEHLERGEDAECHTNIS.md, No. 356).
    /// </summary>
    private sealed record Widening(Dictionary<GlyphKey, double> Shift, List<(RectD Rect, double Line)> Extensions, int PartlyShown, Dictionary<GlyphKey, double> RunStart)
    {
        public static readonly Widening None = new([], [], 0, []);
    }

    /// <summary>Plans the widening of the gaps on the lines that lose characters.</summary>
    /// <param name="layout">Every character of the content stream.</param>
    /// <param name="lineOf">The line a character lies on, if it is a line that loses characters (text object and number of the line).</param>
    /// <param name="bars">The redaction areas (page space).</param>
    /// <param name="frozen">Lines (baseline y, page space) that keep their characters where they are: the check after a first try found a
    /// character of theirs missing, out of order, out of its clip or under a bar (<see cref="MovesChecked"/>); they count as partly shown.</param>
    private static Widening Widen(List<GlyphLayout> layout, Func<GlyphLayout, (int TextObject, int Line)?> lineOf,
        IReadOnlyList<RectD> bars, IReadOnlyList<double> frozen)
    {
        var shift = new Dictionary<GlyphKey, double>();
        var runStart = new Dictionary<GlyphKey, double>();
        var extensions = new List<(RectD Rect, double Line)>();
        var partly = 0;
        static double Em(GlyphLayout g) => GlyphEm(g);
        static double End(GlyphLayout g) => GlyphEnd(g);

        // the lines that lose characters, each with its characters along it
        var lines = new Dictionary<(int, int), List<GlyphLayout>>();
        foreach (var g in layout)
        {
            if (lineOf(g) is not { } line) continue;
            if (!lines.TryGetValue(line, out var list)) lines[line] = list = [];
            list.Add(g);
        }
        if (lines.Count == 0) return Widening.None;
        var segments = new SegmentIndex(Segments(layout));
        // the characters by their baseline, to find those reaching into a line without going through all of them for every line: a page
        // that lost a thousand lines of 60,000 characters took two seconds per pass (docs/FEHLERGEDAECHTNIS.md, No. 384). The few much
        // taller than the rest are looked at for every line (their baseline may lie far below one they reach into)
        var heights = layout.Select(g => g.Height).Order().ToArray();
        var regular = 4 * heights[heights.Length / 2];
        var byBaseline = layout.Where(g => g.Height <= regular).OrderBy(g => g.V).ToArray();
        var baselines = byBaseline.Select(g => g.V).ToArray();
        var tall = layout.Where(g => !(g.Height <= regular)).ToArray();
        foreach (var (_, glyphs) in lines)
        {
            if (!glyphs.Any(g => g.Evens) || glyphs.Any(g => !(g.DirX > 0.999))) continue;   // upright lines only
            glyphs.Sort((a, b) => a.U.CompareTo(b.U));
            var baseline = glyphs[0].V;
            var height = glyphs.Max(g => g.Height);
            if (frozen.Any(v => Math.Abs(v - baseline) < 0.3))
            {
                partly++;
                continue;
            }
            // text that is not part of this line, on the same line: nothing may move onto it — other text objects, and raised or lowered
            // characters (an index or exponent in a formula, also of the same text object on a baseline of its own), whatever reaches into the
            // middle of the line; by the baseline and the text object alone they were missed and the words after a widened gap ran into them
            // (docs/FEHLERGEDAECHTNIS.md, No. 257). Lines above and below do not reach that far
            double low = baseline + 0.05 * height, high = baseline + 0.85 * height;
            var own = glyphs.Select(g => (g.Op, g.Element, g.Start)).ToHashSet();
            var others = new List<double>();
            void Other(in GlyphLayout g)
            {
                if (g.V < high && g.V + g.Height > low && !own.Contains((g.Op, g.Element, g.Start))) others.Add(g.U);
            }
            var from = Array.BinarySearch(baselines, low - regular);
            for (var i = from < 0 ? ~from : from; i < byBaseline.Length && byBaseline[i].V < high; i++) Other(byBaseline[i]);
            foreach (var g in tall) Other(g);
            others.Sort();
            // the first other character after a place, by halving (others is sorted)
            int After(double u)
            {
                var at = others.BinarySearch(u);
                if (at < 0) return ~at;
                while (at < others.Count && others[at] <= u) at++;
                return at;
            }

            // the parts of the line (a large gap separates them)
            var parts = new List<List<GlyphLayout>> { new() { glyphs[0] } };
            for (var i = 1; i < glyphs.Count; i++)
            {
                if (glyphs[i].U - End(glyphs[i - 1]) > PartGap * Em(glyphs[i - 1])) parts.Add([]);
                parts[^1].Add(glyphs[i]);
            }
            for (var p = 0; p < parts.Count; p++)
            {
                var part = parts[p];
                var partEnd = End(part[^1]);
                // free room after the last part of the line: up to where the lines of its column above and below reach (a ragged line grows
                // into the room the longer lines around it have — never into the gap between columns), away from the next text on the line
                var free = 0.0;
                if (p == parts.Count - 1)
                {
                    var limit = ColumnEdge(segments, part[0].U, partEnd, baseline, height);
                    var next = After(partEnd - 0.01) is var n && n < others.Count ? others[n] : double.MaxValue;
                    limit = Math.Min(limit, next - Em(part[^1]));
                    free = Math.Max(0, limit - partEnd);
                }
                // other text inside this part: leave it as it is — and say so if it lost words (their length stays to be seen)
                if (After(part[0].U) is var o && o < others.Count && others[o] < partEnd)
                {
                    if (part.Any(g => g.Evens)) partly++;
                    continue;
                }
                // a justified line: set anew as a whole; where that is not possible, its spacing still tells the width (partly shown)
                if (Justified(part, segments, baseline, height))
                {
                    if (ResetJustified(part, bars, shift, runStart, extensions)) continue;
                    if (part.Any(g => g.Evens)) partly++;
                    WidenPart(part, free, bars, shift, runStart, extensions, ref partly, spacingChecked: true);
                    continue;
                }
                WidenPart(part, free, bars, shift, runStart, extensions, ref partly, spacingChecked: false);
            }
        }
        return new Widening(shift, extensions, partly, runStart);

        static void WidenPart(List<GlyphLayout> part, double free, IReadOnlyList<RectD> bars, Dictionary<GlyphKey, double> shift,
            Dictionary<GlyphKey, double> runStart, List<(RectD Rect, double Line)> extensions, ref int partly, bool spacingChecked)
        {
            // runs of redacted characters: [first, last]
            var runs = new List<(int First, int Last)>();
            for (var i = 0; i < part.Count; i++)
            {
                if (!part[i].Evens) continue;
                var j = i;
                while (j + 1 < part.Count && part[j + 1].Removed) j++;
                runs.Add((i, j));
                i = j;
            }
            for (var r = 0; r < runs.Count; r++)
            {
                var (first, last) = runs[r];
                var start = part[first].U;
                // where the run starts, for the text operators that begin inside it (see Widening)
                for (var k = first; k <= last; k++)
                    if (part[k].Removed) runStart[new GlyphKey(part[k].Op, part[k].Element, part[k].Start)] = start;
                // spacing wider than the spaces themselves before the run (a justified line, by Tw or by the numbers in TJ): it was chosen so
                // that the line with the removed word reaches the margin — measured, it gives away that word's width (glyph positions, PoPETs
                // 2023). Those words stay where they are, so the length counts as partly shown (docs/FEHLERGEDAECHTNIS.md, No. 354)
                // (between words without a space character — each placed on its own — the gap is compared with a generous space, a third of an em)
                for (var k = 0; k < first && !spacingChecked; k++)
                {
                    if (part[k].Removed || part[k + 1].Space) continue;
                    var gap = part[k + 1].U - End(part[k]);
                    var wider = part[k].Space ? gap : gap > 0.15 * Em(part[k]) ? gap - SpaceWidth * Em(part[k]) : 0;
                    if (wider > 0.05 * Em(part[k]))
                    {
                        partly++;
                        break;
                    }
                }
                // the em of the font, but at most 1.6 times its highest character in this part: a font whose characters are drawn far smaller
                // than its size says (Tf 58 for letters 12 pt high) would make steps far too large (docs/FEHLERGEDAECHTNIS.md, No. 263)
                var highest = part.Max(g => g.InkHeight);
                var em = highest > 0 ? Math.Min(Em(part[first]), InkToEm * highest) : Em(part[first]);
                var step = LengthStep * em;
                var next = last + 1;                                          // the first character after the run
                var until = r + 1 < runs.Count ? runs[r + 1].First : part.Count; // up to the next run (or the end of the part)
                // what tells the length is the distance from the start of the run to the next word that shows (the space after the bar
                // counts with it — it is as visible as the bar); the word space right after it is not taken back, it would tell the same
                var word = next;
                while (word < until && part[word].Space) word++;
                var width = word < until ? part[word].U - start : End(part[last]) - start;
                var desired = Math.Ceiling(width / step - 1e-9) * step - width;
                if (desired < 0.01) continue;
                // room: the word spaces after that word up to the next run, and after the last run the free room behind the part
                var slack = new List<(int At, double Room)>();
                for (var k = word + 1; k < until; k++)
                {
                    var g = part[k];
                    if (g.Removed || g.Space) continue;
                    // the last character before it that is not a space
                    var b = k - 1;
                    while (b > word && part[b].Space) b--;
                    var gap = g.U - End(part[b]);
                    var startsWord = part[k - 1].Space || gap > 0.15 * Em(g);
                    if (!startsWord) continue;
                    var keep = Math.Max(KeptWordGap * gap, ReadersGap(Em(g)));
                    if (gap > keep) slack.Add((k, gap - keep));
                }
                var end = r + 1 < runs.Count ? 0 : free;
                var extra = Math.Min(desired, slack.Sum(s => s.Room) + end);
                if (extra < 0.01)
                {
                    partly++;
                    continue;
                }
                // the bar over the run grows by the extra room, on this line only
                var top = part[first].V + 1.1 * part[first].Height;
                var bottom = part[first].V - 0.4 * part[first].Height;
                var runBox = RectD.FromLtrb(start, bottom, End(part[last]), top);
                // characters removed without a bar over this line (hidden ones marked elsewhere): nothing grows here — MaxBy of no rectangle
                // threw instead of giving none (docs/FEHLERGEDAECHTNIS.md, No. 257)
                var touching = bars.Where(b => b.Intersects(runBox)).ToList();
                if (touching.Count == 0) continue;
                var bar = touching.MaxBy(b => Overlap(b, runBox));
                // a bar reaching beyond this line (an area over a block): the piece covers the line as the text layer boxes its characters
                // (0.2 of the height below the baseline, 0.8 above) — the generous line box reached into the lines above and below, beside
                // the area, wherever lines stand closer than 1.5 heights (docs/FEHLERGEDAECHTNIS.md, No. 783; a variant of No. 357). A bar
                // within the line box keeps it: the piece is as high as its bar and joins it (No. 354)
                if (bar.Top < bottom - 0.01 || bar.Bottom > top + 0.01)
                    (bottom, top) = (part[first].V - 0.2 * part[first].Height, part[first].V + 0.8 * part[first].Height);
                var piece = RectD.FromLtrb(bar.Right, Math.Max(bar.Top, bottom), bar.Right + extra, Math.Min(bar.Bottom, top));
                // the words after the run move by the extra room; the word spaces take it back, all of them evenly (as far as each has room),
                // the rest stays as the line growing into the free room after it. Evenly, not the nearest first: which spaces shrink by how
                // much would tell how much room the gap needed — and with it the width of what was removed (glyph positions, Bland, Iyer,
                // Levchenko, PoPETs 2023; docs/FEHLERGEDAECHTNIS.md, No. 354). In a justified line the words after the gap then stand where
                // the step and the visible words put them, whatever the removed word's width
                var takes = new double[slack.Count];
                var left = Math.Max(0, extra - end);
                for (var open = Enumerable.Range(0, slack.Count).ToList(); left > 1e-9 && open.Count > 0;)
                {
                    var share = left / open.Count;
                    var still = new List<int>();
                    foreach (var i in open)
                    {
                        var take = Math.Min(share, slack[i].Room - takes[i]);
                        takes[i] += take;
                        left -= take;
                        if (slack[i].Room - takes[i] > 1e-9) still.Add(i);
                    }
                    open = still;
                }
                var current = extra;
                var s = 0;
                for (var k = next; k < until; k++)
                {
                    if (s < slack.Count && slack[s].At == k)
                    {
                        current -= takes[s];
                        s++;
                    }
                    if (part[k].Removed || current <= 0) continue;
                    shift[new GlyphKey(part[k].Op, part[k].Element, part[k].Start)] = current;
                }
                if (extra < desired - 1e-6) partly++;
                extensions.Add((piece, part[first].V));
            }
        }

        // A justified line: its spacing is wider than spaces somewhere (by Tw or by the numbers in TJ; between words placed each on its own,
        // wider than a generous space of a third of an em), and it ends where a line of its column above or below ends — the margin. Spacing
        // alone is not enough: a line set with wide spacing by choice (a letter-spaced heading) keeps its spacing whatever was removed, and
        // set anew it would tell the width through its end instead
        static bool Justified(List<GlyphLayout> part, SegmentIndex segments, double v, double height)
        {
            // Spaces in a row fill up to a tab stop (a word processor writes a tab as a run of spaces, each widened by a number in TJ): a
            // justified line has one space between words (two after a full stop). Taken for justified, a line of three labels set apart
            // so was set anew as a whole, and the labels after the redacted word moved 32 pt to the left (public form test files,
            // docs/FEHLERGEDAECHTNIS.md, No. 453)
            for (int k = 0, row = 0; k < part.Count; k++)
            {
                row = part[k].Space ? row + 1 : 0;
                if (row >= 3) return false;
            }
            var wide = false;
            for (var k = 0; k + 1 < part.Count && !wide; k++)
            {
                if (part[k + 1].Space) continue;
                var gap = part[k + 1].U - End(part[k]);
                var wider = part[k].Space ? gap : gap > 0.15 * Em(part[k]) ? gap - SpaceWidth * Em(part[k]) : 0;
                wide = wider > 0.05 * Em(part[k]);
            }
            if (!wide) return false;
            double start = part[0].U, end = End(part[^1]), tolerance = Math.Max(0.5, 0.15 * Em(part[^1]));
            return segments.Near(v, height).Any(s => Math.Abs(s.V - v) > 0.3 * height && Math.Abs(s.V - v) < 4 * Math.Max(height, s.Height) && s.Start < end && s.End > start &&
                                     Math.Abs(s.End - end) <= tolerance);
        }

        // Sets a justified line anew (glyph positions, Bland, Iyer, Levchenko, PoPETs 2023; docs/FEHLERGEDAECHTNIS.md, No. 356): its start and
        // end stay, every redacted run becomes a bar of whole steps (with the margins its bar had beside the characters), and all gaps between
        // words and bars become the same. Then every position on the line follows from the visible words, the margins and the steps — not from
        // the width of what was removed. Done only if the new bar covers all of the old one (what lay under it — image pixels blacked out, cut
        // paths — stays covered) and the gaps stay wide enough; otherwise false, and nothing is changed.
        static bool ResetJustified(List<GlyphLayout> part, IReadOnlyList<RectD> bars, Dictionary<GlyphKey, double> shift,
            Dictionary<GlyphKey, double> runStart, List<(RectD Rect, double Line)> extensions)
        {
            double WordEnd(int first, int last) => Enumerable.Range(first, last - first + 1).Max(k => End(part[k]));
            // the words of the line (characters within 0.15 em of the word so far — measured from its rightmost end: an accent set back over
            // the letter before it ends early, and the comma after „HU+0303“ began a word of its own —, no space between)
            var words = new List<(int First, int Last)>();
            var wordEnd = double.NegativeInfinity;
            var gapSeen = true;
            for (var i = 0; i < part.Count; i++)
            {
                if (part[i].Space)
                {
                    gapSeen = true;
                    continue;
                }
                if (!gapSeen && words.Count > 0 && part[i].U - wordEnd <= 0.15 * Em(part[i]))
                {
                    words[^1] = (words[^1].First, i);
                    wordEnd = Math.Max(wordEnd, End(part[i]));
                }
                else
                {
                    words.Add((i, i));
                    wordEnd = End(part[i]);
                }
                gapSeen = false;
            }
            // the runs of redacted words, one per bar: redacted words under the same bar make one run, under two bars two (the space between
            // them stays between the bars)
            var items = new List<(int First, int Last, RectD? Bar)>();
            foreach (var (first, last) in words)
            {
                int removed = 0, shown = 0;
                for (var k = first; k <= last; k++)
                {
                    if (part[k].Space) continue;
                    if (part[k].Removed && !part[k].Evens) return false;   // hidden text removed beside the bars: it moves nobody
                    if (part[k].Removed) removed++;
                    else shown++;
                }
                if (removed > 0 && shown > 0) return false;   // part of a word redacted: the rest of it stays where it is
                if (removed == 0)
                {
                    items.Add((first, last, null));
                    continue;
                }
                var g = part[first];
                var box = RectD.FromLtrb(g.U, g.V - 0.4 * g.Height, WordEnd(first, last), g.V + 1.1 * g.Height);
                var touching = bars.Where(b => b.Intersects(box)).ToList();
                if (touching.Count == 0) return false;
                var bar = touching.MaxBy(b => Overlap(b, box));
                if (items.Count > 0 && items[^1].Bar is { } before && before == bar) items[^1] = (items[^1].First, last, bar);
                else items.Add((first, last, bar));
            }
            if (items.Count < 2 || !items.Any(i => i.Bar is not null) || !items.Any(i => i.Bar is null)) return false;
            // a space that stays inside a run would land under the new bar
            foreach (var item in items.Where(i => i.Bar is not null))
                for (var k = item.First; k <= item.Last; k++)
                    if (!part[k].Removed) return false;

            // each run with its bar: over this line only, not shared with another run; the bar grows to whole steps (as for a ragged line,
            // the em at most 1.6 times the highest character, No. 263), the room of the run is that less the margins the bar had beside its
            // characters
            var highest = part.Max(g => g.InkHeight);
            var runs = new Dictionary<int, (RectD Bar, double Left, double Right, double Step, double Drawn)>();
            var used = new HashSet<RectD>();
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Bar is not { } bar) continue;
                var g = part[items[i].First];
                if (bar.Top < g.V - 0.6 * g.Height || bar.Bottom > g.V + 1.5 * g.Height || !used.Add(bar)) return false;
                double start = g.U, end = WordEnd(items[i].First, items[i].Last);
                var em = highest > 0 ? Math.Min(Em(g), InkToEm * highest) : Em(g);
                var step = LengthStep * em;
                runs[i] = (bar, start - bar.Left, bar.Right - end, step, Math.Ceiling(bar.Width / step - 1e-9) * step);
            }

            // one gap for all, the start and the end of the line staying; a new bar that does not cover all of its old one grows by a step
            // (twice at most)
            double lineStart = part[items[0].First].U, lineEnd = WordEnd(items[^1].First, items[^1].Last);
            var widths = new double[items.Count];
            var starts = new double[items.Count];
            double gapAll = 0;
            List<RectD>? newBars = null;
            for (var round = 0; round < 3 && newBars is null; round++)
            {
                for (var i = 0; i < items.Count; i++)
                    widths[i] = runs.TryGetValue(i, out var r) ? r.Drawn - r.Left - r.Right : WordEnd(items[i].First, items[i].Last) - part[items[i].First].U;
                gapAll = (lineEnd - lineStart - widths.Sum()) / (items.Count - 1);
                var at = lineStart;
                for (var i = 0; i < items.Count; i++)
                {
                    starts[i] = at;
                    at += widths[i] + gapAll;
                }
                var drawn = new List<RectD>();
                var short_ = false;
                foreach (var i in runs.Keys.ToList())
                {
                    var r = runs[i];
                    var bar = RectD.FromLtrb(starts[i] - r.Left, r.Bar.Top, starts[i] + widths[i] + r.Right, r.Bar.Bottom);
                    if (bar.Left > r.Bar.Left + 1e-6 || bar.Right < r.Bar.Right - 1e-6)
                    {
                        runs[i] = r with { Drawn = r.Drawn + r.Step };
                        short_ = true;
                    }
                    drawn.Add(bar);
                }
                if (!short_) newBars = drawn;
            }
            if (newBars is null) return false;

            // the conditions: gaps wide enough (between words as between a word and a bar)
            for (var i = 1; i < items.Count; i++)
            {
                var em = Em(part[items[i].First]);
                if (gapAll < ReadersGap(em)) return false;
                if (runs.TryGetValue(i - 1, out var before) && gapAll - before.Right < 0.1 * em || runs.TryGetValue(i, out var here) && gapAll - here.Left < 0.1 * em)
                    return false;
            }

            // where every character that stays lands: a word moves as a whole; spaces between two words keep to the word before them, spaces
            // beside a bar stand in the middle of the gap that shows (their middle never under the bar)
            var moves = new Dictionary<GlyphKey, double>();
            var starting = new Dictionary<GlyphKey, double>();   // removed characters: where their run starts now (see Widening)
            for (var i = 0; i < items.Count; i++)
            {
                var by = starts[i] - part[items[i].First].U;
                for (var k = items[i].First; k <= items[i].Last; k++)
                {
                    var key = new GlyphKey(part[k].Op, part[k].Element, part[k].Start);
                    if (items[i].Bar is null) moves[key] = by;
                    else starting[key] = starts[i];
                }
                if (i + 1 >= items.Count) continue;
                var spaces = Enumerable.Range(items[i].Last + 1, items[i + 1].First - items[i].Last - 1).Where(k => !part[k].Removed).ToList();
                if (spaces.Count == 0) continue;
                double Width(int k) => End(part[k]) - part[k].U;
                double from;
                if (items[i].Bar is null && items[i + 1].Bar is null) from = starts[i] + widths[i];
                else
                {
                    var left = starts[i] + widths[i] + (runs.TryGetValue(i, out var a) ? a.Right : 0);
                    var right = starts[i + 1] - (runs.TryGetValue(i + 1, out var b) ? b.Left : 0);
                    from = (left + right) / 2 - spaces.Sum(Width) / 2;
                }
                foreach (var k in spaces)
                {
                    moves[new GlyphKey(part[k].Op, part[k].Element, part[k].Start)] = from - part[k].U;
                    from += Width(k);
                }
            }

            foreach (var (key, by) in moves)
                if (Math.Abs(by) > 1e-9) shift[key] = by;
            foreach (var (key, at) in starting) runStart[key] = at;
            extensions.AddRange(newBars.Select(b => (b, part[items[0].First].V)));
            return true;
        }

        // every line of the content in its parts (a large gap separates them): start, end and baseline along the writing direction
        static List<(double Start, double End, double V, double Height)> Segments(List<GlyphLayout> all)
        {
            var result = new List<(double, double, double, double)>();
            var upright = all.Where(g => g.DirX > 0.999).OrderBy(g => g.V).ThenBy(g => g.U).ToList();
            var i = 0;
            while (i < upright.Count)
            {
                // one line: baselines within a third of its height
                var j = i + 1;
                while (j < upright.Count && upright[j].V - upright[i].V < 0.3 * Math.Max(upright[i].Height, upright[j].Height)) j++;
                var line = upright.GetRange(i, j - i);
                line.Sort((a, b) => a.U.CompareTo(b.U));
                double start = line[0].U, stop = End(line[0]), height = line[0].Height;
                for (var k = 1; k < line.Count; k++)
                {
                    if (line[k].U - stop > PartGap * Em(line[k - 1]))
                    {
                        result.Add((start, stop, upright[i].V, height));
                        (start, height) = (line[k].U, 0);
                    }
                    stop = Math.Max(stop, End(line[k]));
                    height = Math.Max(height, line[k].Height);
                }
                result.Add((start, stop, upright[i].V, height));
                i = j;
            }
            return result;
        }

        // how far the lines of the column around a part reach: those within four lines above and below that overlap it; its own end if none
        static double ColumnEdge(SegmentIndex segments, double start, double end, double v, double height)
        {
            var edge = end;
            foreach (var s in segments.Near(v, height))
                if (Math.Abs(s.V - v) > 0.3 * height && Math.Abs(s.V - v) < 4 * Math.Max(height, s.Height) && s.Start < end && s.End > start)
                    edge = Math.Max(edge, s.End);
            return edge;
        }

        static double Overlap(RectD a, RectD b) => a.Intersect(b) is var i && i.Width > 0 && i.Height > 0 ? i.Width * i.Height : 0;
    }

    /// <summary>
    /// The segments of the lines on a page by their baseline: those near a line — a baseline less than four heights away — are found by halving
    /// instead of going through all of them for every line (a page that lost fourteen thousand lines took seconds per pass,
    /// docs/FEHLERGEDAECHTNIS.md, No. 384). The few much taller than the rest are offered for every line.
    /// </summary>
    private sealed class SegmentIndex
    {
        private readonly (double Start, double End, double V, double Height)[] _regular, _tall;
        private readonly double[] _baselines;
        private readonly double _cap;

        public SegmentIndex(List<(double Start, double End, double V, double Height)> segments)
        {
            var heights = segments.Select(s => s.Height).Order().ToArray();
            _cap = heights.Length > 0 ? 4 * heights[heights.Length / 2] : 0;
            _regular = [.. segments.Where(s => s.Height <= _cap).OrderBy(s => s.V)];
            _tall = [.. segments.Where(s => !(s.Height <= _cap))];
            _baselines = [.. _regular.Select(s => s.V)];
        }

        /// <summary>A superset of the segments whose baseline lies less than four times the larger height from <paramref name="v"/>.</summary>
        public IEnumerable<(double Start, double End, double V, double Height)> Near(double v, double height)
        {
            var reach = 4 * Math.Max(height, _cap);
            var at = Array.BinarySearch(_baselines, v - reach);
            for (at = at < 0 ? ~at : at; at > 0 && _baselines[at - 1] >= v - reach; at--) { }
            for (; at < _regular.Length && _baselines[at] <= v + reach; at++) yield return _regular[at];
            foreach (var s in _tall) yield return s;
        }
    }
}
