using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

// Hiding the lengths of redacted words moves characters that stay (Redactor.Widen.cs). Whether that went well is not decided by rules in
// the planning but checked afterwards, against what the page shows (docs/FEHLERGEDAECHTNIS.md, No. 359): every character that showed before
// still shows at the same text position of its line, lines that lost nothing stand exactly where they were, no character lies under a bar
// or under a piece a bar grew by, none has left the clip it is shown in. A line that fails is frozen — the page is written again with that
// line as it was (counted as partly shown) — so that an unforeseen way a file is built can never cost a character, only the hiding.
public static partial class Redactor
{
    /// <summary>A character as the page shows it (see <see cref="GlyphSnapshot"/>).</summary>
    /// <param name="Text">Its text.</param>
    /// <param name="X">Origin along the page, x.</param>
    /// <param name="Y">Origin, y: the line it lies on when upright.</param>
    /// <param name="Box">Its advance box on the page.</param>
    /// <param name="Size">Its height (font size on the page).</param>
    /// <param name="Angle">The direction of its line on the page, in radians; 0 exactly for a line that runs left to right along the page
    /// (<see cref="Upright"/>). One float beside the flags instead of a flag: the entry stays as large as before — a page of a million
    /// characters is snapshot four times (docs/FEHLERGEDAECHTNIS.md, No. 384 and 485).</param>
    /// <param name="Shown">Visible: not on a hidden layer, not invisible (mode 3), inside the clip.</param>
    /// <param name="Touched">Its box touches a bar (set for the comparison: it may have gone with the bar).</param>
    /// <param name="Ink">The bounds of its shape on the page (empty without one).</param>
    /// <param name="Present">Not on a hidden layer and inside the clip — shown, or invisible (the text layer of a scan, which must stay with
    /// the ink it belongs to).</param>
    internal readonly record struct Seen(string Text, double X, double Y, RectD Box, double Size, float Angle, bool Shown, bool Blank, bool Touched = false,
        RectD Ink = default, bool Present = false)
    {
        /// <summary>Its line runs left to right along the page (the lines that may be widened).</summary>
        public bool Upright => Angle == 0;
    }

    /// <summary>
    /// Characters a page may have for its lengths to be hidden — as many as the lines of a content stream are evened for at all
    /// (<see cref="MaxLayout"/>). Beyond it the characters are not compared one by one (each takes about 70 bytes, twice: a damaged page with
    /// 13 million characters took gigabytes, docs/FEHLERGEDAECHTNIS.md, No. 384), and the lines are not evened out; the gaps are still rounded.
    /// </summary>
    private const int MaxSnapshot = MaxLayout;

    /// <summary>
    /// Characters a page may have for the last check of what stays (<see cref="StaysChecked(PdfDocument, PdfPage, List{Seen}, PdfDictionary,
    /// PdfDictionary, List{RectD}, bool)"/>): far beyond <see cref="MaxSnapshot"/> — a page above that was saved unchecked (Analyse F3b-2;
    /// decided on 4.11.2026: „safety before speed“; docs/FEHLERGEDAECHTNIS.md, No. 460). Two snapshots of this many take about
    /// 400 MB; a page with more is damaged or made to tire the program, and it is not redacted (<see cref="Apply"/>).
    /// </summary>
    internal const int MaxCheckSnapshot = 2_000_000;

    /// <summary>The characters of a page before redacting, for <see cref="MovesChecked"/>; null if it has more than <paramref name="limit"/>.</summary>
    private static List<Seen>? Snapshot(PdfDocument doc, PdfPage page, int limit = MaxSnapshot)
    {
        var observer = new GlyphSnapshot(limit);
        PageInterpreter.Observe(doc, page, observer);
        return observer.Overflow ? null : observer.Glyphs;
    }

    /// <summary>
    /// Checks the moved characters of a redacted page against the page before (see the top of this file). Returns the lines (baseline y) to
    /// freeze: those on which a character is missing, in another order, under a bar or out of its clip, and those whose piece covers a
    /// character of another line. Empty if all is well. A failure that belongs to no line that could have moved freezes every such line.
    /// </summary>
    private static List<double> MovesChecked(PdfDocument doc, PdfPage page, List<Seen> before, RedactionResult result, List<RectD> rects, List<(RectD Rect, double Line)> pieces)
    {
        var check = new PdfPage(page.Index, result.Page, null, result.Resources, page.MediaBox, page.CropBox, page.Rotate);
        var after = Snapshot(doc, check, int.MaxValue)!;   // no more than before
        var frozen = new List<double>();
        // the lines that lost characters: a character whose box touches a bar goes (as a whole, or it is the one the bar is for)
        bool Touched(Seen g) => rects.Any(r => r.Intersects(g.Box));
        // (sorted, looked up by halving: a page of tens of thousands of characters asks for every one of them)
        var lost = before.Where(g => g.Upright && Touched(g)).Select(g => g.Y).Distinct().Order().ToArray();
        bool Lost(double y)
        {
            var at = Array.BinarySearch(lost, y);
            if (at >= 0) return true;
            at = ~at;
            return at < lost.Length && lost[at] - y < 0.3 || at > 0 && y - lost[at - 1] < 0.3;
        }
        static bool Same(double a, double b, double size) => Math.Abs(a - b) < 0.3 * Math.Max(size, 1);
        var frozenNear = new Dictionary<long, List<double>>();   // the frozen lines by a band of 0.3 (looked up, not searched: see Lost)
        void Freeze(double y)
        {
            var band = (long)Math.Floor(y / 0.3);
            for (var b = band - 1; b <= band + 1; b++)
                if (frozenNear.TryGetValue(b, out var near) && near.Any(v => Math.Abs(v - y) < 0.3)) return;
            (frozenNear.TryGetValue(band, out var list) ? list : frozenNear[band] = []).Add(y);
            frozen.Add(y);
        }
        void FreezeAll()
        {
            foreach (var y in lost) Freeze(y);
        }

        // 1. no character lies under a bar or a piece — invisible ones (the text layer of a scan) and those on hidden layers included, as
        // the final check counts them (nothing under a bar stays in the file): a character half under a bar (its middle outside) may stay
        // where it was — the bar grazed it before, in the same place — but nothing moves into a bar; half a step of the grid at the edge of a
        // piece is the rounding of a word start
        // (the characters before by text and a cell of one point, built when first asked: going through all of them for every character
        // a bar grazes grew with the square of the characters, docs/FEHLERGEDAECHTNIS.md, No. 999)
        Dictionary<(string Text, long X, long Y), List<int>>? near = null;
        bool GrazedBefore(Seen g, RectD r)
        {
            if (near is null)
            {
                near = [];
                for (var i = 0; i < before.Count; i++)
                {
                    var key = (before[i].Text, CellOf(before[i].X), CellOf(before[i].Y));
                    (near.TryGetValue(key, out var list) ? list : near[key] = []).Add(i);
                }
            }
            for (var x = CellOf(g.X - 0.6); ; x++)
            {
                for (var y = CellOf(g.Y - 0.6); ; y++)
                {
                    if (near.TryGetValue((g.Text, x, y), out var list))
                        foreach (var i in list)
                            if (before[i] is var b && Math.Abs(b.X - g.X) <= 0.6 && Math.Abs(b.Y - g.Y) <= 0.6 && Overlaps(r, b.Box))
                                return true;
                    if (y == CellOf(g.Y + 0.6)) break;
                }
                if (x == CellOf(g.X + 0.6)) break;
            }
            return false;
        }
        foreach (var g in after)
        {
            if (g.Blank) continue;
            var area = Math.Max(g.Box.Width * g.Box.Height, 1e-9);
            foreach (var r in rects)
            {
                var cut = r.Intersect(g.Box);
                if (cut.Width <= 0 || cut.Height <= 0) continue;
                if (cut.Width * cut.Height >= 0.5 * area || !GrazedBefore(g, r))
                {
                    if (g.Upright && Lost(g.Y)) Freeze(g.Y);
                    else FreezeAll();
                }
            }
            var box = g.Box.Width > PositionGrid && g.Box.Height > PositionGrid ? g.Box.Inflate(-PositionGrid / 2) : g.Box;
            foreach (var (rect, line) in pieces)
                if (Overlaps(rect, box)) Freeze(line);
        }

        // 2. every character that showed before still shows: on the lines that lost characters in the same order along the line (one that
        // touched a bar may have gone with it), on every other line (and for text that does not run left to right) exactly where it was
        var shownBefore = before.Where(g => g.Shown && !g.Blank).ToList();
        var shownAfter = after.Where(g => g.Shown && !g.Blank).ToList();
        var byLine = shownAfter.Where(g => g.Upright).GroupBy(g => Math.Round(g.Y * 2) / 2).ToDictionary(l => l.Key, l => l.OrderBy(g => g.X).ToList());
        foreach (var line in shownBefore.Where(g => g.Upright && Lost(g.Y)).GroupBy(g => Math.Round(g.Y * 2) / 2))
        {
            var expected = line.OrderBy(g => g.X).Select(g => g with { Touched = Touched(g) }).ToList();
            // the characters of the line after, from all baselines within reach of the one before (rounding of the key)
            var actual = byLine.Where(l => Same(l.Key, line.Key, expected[0].Size)).SelectMany(l => l.Value).OrderBy(g => g.X).ToList();
            if (!Matches(expected, actual)) Freeze(line.Key);
        }
        var others = shownBefore.Where(g => !(g.Upright && Lost(g.Y)) && !Touched(g)).ToList();
        var exact = new HashSet<int>();   // indices in shownAfter already matched
        // the characters after by text and by their place in a grid of one point (a character within 0.6 of its place lies in its cell or a
        // neighbouring one): a list of every „e“ of a page searched for every „e“ grew with the square of the characters
        var pool = new Dictionary<(string Text, long X, long Y), List<int>>();
        for (var i = 0; i < shownAfter.Count; i++)
        {
            var key = (shownAfter[i].Text, (long)Math.Floor(shownAfter[i].X), (long)Math.Floor(shownAfter[i].Y));
            (pool.TryGetValue(key, out var list) ? list : pool[key] = []).Add(i);
        }
        int Find(Seen g)
        {
            var (cx, cy) = ((long)Math.Floor(g.X), (long)Math.Floor(g.Y));
            for (var dx = -1L; dx <= 1; dx++)
                for (var dy = -1L; dy <= 1; dy++)
                    if (pool.TryGetValue((g.Text, cx + dx, cy + dy), out var candidates))
                        foreach (var i in candidates)
                            if (!exact.Contains(i) && Math.Abs(shownAfter[i].X - g.X) <= 0.6 && Math.Abs(shownAfter[i].Y - g.Y) <= 0.6) return i;
            return -1;
        }
        foreach (var g in others)
        {
            var at = Find(g);
            if (at < 0)
            {
                FreezeAll();
                break;
            }
            exact.Add(at);
        }
        return frozen;
    }

    private static bool Overlaps(RectD a, RectD b) => a.Intersect(b) is var i && i.Width > 0 && i.Height > 0;

    /// <summary>A cell of one point for looking characters up by place, kept in order beyond the range of a long.</summary>
    private static long CellOf(double v) => v >= 9e18 ? long.MaxValue : v <= -9e18 ? long.MinValue : (long)Math.Floor(v);

    /// <summary>The check below, with the characters of the page before taken here (for tests).</summary>
    internal static bool StaysChecked(PdfDocument doc, PdfPage page, PdfDictionary newPage, PdfDictionary resources, List<RectD> rects, bool moved = false) =>
        Snapshot(doc, page, MaxCheckSnapshot) is { } before && StaysChecked(doc, page, before, newPage, resources, rects, moved);

    /// <summary>
    /// The last check of what stays, after the last round and whether lengths were hidden or not: every character outside the bars that the
    /// page had before — shown, or invisible (the text layer of a scan) — is there again. On the lines that lost characters in the same
    /// order (moved along the line only by hiding lengths, or by half the grid a rounded gap moves the rest); on every other line with the
    /// same text, its origin within 0.6 pt and its shape in the same place (another font after a lost Tf, an empty glyph instead of a letter
    /// or a .notdef box would show there). Before, only hiding lengths checked this, and a character missing outside froze every line and was
    /// then accepted in the last round: a fault elsewhere in the rewriter saved a page with text missing or moved (docs/FEHLERGEDAECHTNIS.md,
    /// No. 450, Analyse F 3b). Characters on hidden layers are not compared (their place is not shown).
    /// </summary>
    /// <param name="moved">Lengths were hidden in the last round: characters after a bar may have moved along their line by any amount.</param>
    /// <param name="cancel">Abort.</param>
    /// <returns>False if a character is missing, moved or changed.</returns>
    private static bool StaysChecked(PdfDocument doc, PdfPage page, List<Seen> before, PdfDictionary newPage, PdfDictionary resources, List<RectD> rects, bool moved, CancellationToken cancel = default)
    {
        var check = new PdfPage(page.Index, newPage, null, resources, page.MediaBox, page.CropBox, page.Rotate);
        if (Snapshot(doc, check, int.MaxValue) is not { } after) return false;
        bool Touched(Seen g) => rects.Any(r => r.Intersects(g.Box));
        // Lines in their own direction (docs/FEHLERGEDAECHTNIS.md, No. 485): every character is looked at in the frame of its line — along
        // (U) and across (V) — so a line turned or slightly slanted (the text layer of a scan, a label along an axis) is checked like a
        // level one: the writer moves such lines along themselves as well. For level text the frame is the page's (U = x, V = y).
        static (double U, double V) Framed(Seen g)
        {
            if (g.Angle == 0) return (g.X, g.Y);
            var (sin, cos) = Math.SinCos(g.Angle);
            return (g.X * cos + g.Y * sin, g.Y * cos - g.X * sin);
        }
        static int Family(Seen g) => (int)Math.Round(g.Angle * 180 / Math.PI * 4);   // the direction, in quarter degrees
        // the baselines (V) that lost characters, per direction
        var lost = before.Where(Touched).GroupBy(Family)
            .ToDictionary(f => f.Key, f => f.Select(g => Framed(g).V).Distinct().Order().ToArray());
        static bool Within(double[] sorted, double v, double reach)
        {
            var at = Array.BinarySearch(sorted, v);
            if (at >= 0) return true;
            at = ~at;
            return at < sorted.Length && sorted[at] - v <= reach || at > 0 && v - sorted[at - 1] <= reach;
        }
        // a line that lost characters, or a line the writer moves with it: a baseline within three quarters of the character's size — the
        // raised and lowered parts of a text line (indices, a formula's parts, a slightly offset run) are written as one line, and rounding
        // the word starts or hiding the lengths moves them along with it (No. 485). They keep their own baseline and their order.
        bool NearLost(Seen g) => lost.TryGetValue(Family(g), out var lines) && Within(lines, Framed(g).V, Math.Max(0.3, 0.75 * Math.Max(g.Size, 1)));
        // whether a line moves is decided by its baseline, not by each character's size: the small characters of a line whose larger ones are
        // near a redacted line move with them (an index „-nion15“ after „PAGA“, DocLayNet)
        var movingBaselines = before.Where(g => g.Present && !g.Blank && NearLost(g)).GroupBy(Family)
            .ToDictionary(f => f.Key, f => f.Select(g => Framed(g).V).Distinct().Order().ToArray());
        bool Moving(Seen g) => NearLost(g) || movingBaselines.TryGetValue(Family(g), out var baselines) && Within(baselines, Framed(g).V, BaselineSlack);
        // (the characters by index, not copied: a page of a million took hundreds of megabytes twice over, No. 460)
        var present = new List<int>(after.Count);
        for (var i = 0; i < after.Count; i++)
            if (after[i] is { Present: true, Blank: false }) present.Add(i);
        // the lines that move, with lengths hidden: in order, along the line (as MovesChecked accepted them in this round)
        if (moved)
        {
            static Seen InFrame(Seen g)
            {
                var (u, v) = Framed(g);
                return g with { X = u, Y = v };
            }
            var lines = before.Where(g => g.Present && !g.Blank && Moving(g)).GroupBy(g => (Family(g), Math.Round(Framed(g).V * 2) / 2)).ToList();
            // only the characters after on these lines and the buckets beside them, framed (a copy of every character of a page of a million
            // took 300 MB more, No. 460)
            var wanted = new HashSet<(int, double)>(lines.SelectMany(l => new[] { (l.Key.Item1, l.Key.Item2 - 0.5), l.Key, (l.Key.Item1, l.Key.Item2 + 0.5) }));
            var byLine = new Dictionary<(int, double), List<Seen>>();
            foreach (var i in present)
            {
                var a = after[i];
                var key = (Family(a), Math.Round(Framed(a).V * 2) / 2);
                if (wanted.Contains(key)) (byLine.TryGetValue(key, out var list) ? list : byLine[key] = []).Add(InFrame(a));
            }
            foreach (var line in lines)
            {
                // (by the place along the line, then by text: dots of a leader a hair apart kept their order only by chance)
                var expected = line.Select(g => InFrame(g) with { Touched = Touched(g) }).OrderBy(g => Math.Round(g.X, 2)).ThenBy(g => g.Text, StringComparer.Ordinal).ToList();
                // the same line after: only the characters on one of the line's own baselines (the buckets beside it too, a baseline may round
                // either way) — the whole bucket took a line 0.48 pt below into the redacted one (DocLayNet), a third of the size took the
                // invisible text layer of a scan 2 pt above (test file issue12963, No. 462)
                var baselines = expected.Select(g => g.Y).Distinct().Order().ToArray();
                var (family, key) = line.Key;
                var actual = new[] { key - 0.5, key, key + 0.5 }
                    .SelectMany(k => byLine.TryGetValue((family, k), out var same) ? same : [])
                    .Where(a => Within(baselines, a.Y, BaselineSlack))
                    .OrderBy(g => Math.Round(g.X, 2)).ThenBy(g => g.Text, StringComparer.Ordinal).ToList();
                if (!Matches(expected, actual) && !(Overlaid(expected) && SameCharacters(expected, actual)))
                {
                    Debug($"Zeile quer {key:0.##} (Richtung {family / 4.0:0.##}°): vorher „{string.Concat(expected.Select(g => g.Text))}“ (an Balken {expected.Count(g => g.Touched)} von " +
                          $"{expected.Count}), nachher „{string.Concat(actual.Select(g => g.Text))}“");
                    return false;
                }
            }
        }
        // every other character: by text and place, and its shape where it was. The characters after are looked up in an index (StayIndex)
        // by text, direction and a cell of one point across the line, sorted along it: the nearest are tried first, and the search stops
        // where none nearer can follow. Before, a grid of one-point cells was searched — or, where that window was wider than the characters
        // of the text (huge characters, No. 545), every character of the page: with a font size of 4 294 967 295 (fuzzing) that went through
        // all characters for each one, 50 s for 4 173 characters on one page, at the limit of two million a hang the abort did not reach
        // (docs/FEHLERGEDAECHTNIS.md, No. 999). The conditions are the same as before; of equally near ones the first is taken.
        // (one index for the page, the conditions in it: a closure per character took 150 MB more on a page of a million)
        var index = new StayIndex(after, present, Framed, Family, SameShape, cancel);
        var looked = 0;
        foreach (var g in before)
        {
            if (!g.Present || g.Blank || Touched(g)) continue;
            if ((++looked & 1023) == 0) cancel.ThrowIfCancellationRequested();
            var movingLine = Moving(g);
            if (movingLine && moved) continue;
            // on a moving line a word start moves to the grid along the line: half a step either way, or up to two steps on to keep a
            // reader's space (No. 451) — one by one, not by order: lines a few thousandths of a point apart fell into one line by the key
            var along = movingLine ? 2 * PositionGrid + 0.006 * g.Size + 0.05 : 0.6;
            var (gu, gv) = Framed(g);
            // the nearest one that fits (StayIndex.Consider): the first that fit took the character of a line 0.05 pt away, and the last one
            // found none; the window follows the line — along it as far as allowed, across it 0.6 pt (a square of the allowed reach both ways
            // took seconds per character on pages with huge characters, No. 506)
            var best = index.Find(g, gu, gv, along);
            if (best < 0)
            {
                if (DebugOn)
                {
                    var near = present.Select(i => after[i]).Where(a => a.Text == g.Text && Math.Abs(a.X - g.X) < 5 && Math.Abs(a.Y - g.Y) < 5)
                        .OrderBy(a => Math.Abs(a.X - g.X) + Math.Abs(a.Y - g.Y)).Take(2)
                        .Select(a => $"({a.X:0.###}, {a.Y:0.###}) Form {a.Ink.Left:0.##}..{a.Ink.Right:0.##} × {a.Ink.Top:0.##}..{a.Ink.Bottom:0.##}");
                    Debug($"Zeichen „{g.Text}“ bei ({g.X:0.###}, {g.Y:0.###}), Größe {g.Size:0.##}, Richtung {Family(g) / 4.0:0.##}°, Form {g.Ink.Left:0.##}..{g.Ink.Right:0.##} × " +
                          $"{g.Ink.Top:0.##}..{g.Ink.Bottom:0.##}, bewegte Zeile {movingLine}, sichtbar {g.Shown}, verlängert {moved}: nicht wieder da; nah: {string.Join("; ", near)}");
                }
                return false;
            }
            index.Take(best);
        }
        return true;

        // the shape where it was, moved as the origin moved (within 0.1 pt and 1 % of the size)
        static bool SameShape(Seen b, Seen a)
        {
            if (b.Ink.IsEmpty != a.Ink.IsEmpty) return false;
            if (b.Ink.IsEmpty) return true;
            var tol = 0.1 + 0.01 * b.Size;
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Abs(a.Ink.Left - dx - b.Ink.Left) <= tol && Math.Abs(a.Ink.Right - dx - b.Ink.Right) <= tol &&
                   Math.Abs(a.Ink.Top - dy - b.Ink.Top) <= tol && Math.Abs(a.Ink.Bottom - dy - b.Ink.Bottom) <= tol;
        }
    }

    /// <summary>
    /// Whether the characters of a line after redacting are those before, in order, with some of those that touched a bar left out (a
    /// character a bar touches may have gone with it, or not — matched by trying both, not greedily: a letter of the removed name that
    /// happened to equal the next letter after the bar took its place and the rest no longer matched). How many are left out is fixed
    /// (before minus after), so the trial runs along the line with one row of „left out so far“ — a table of every character before against
    /// every one after ran out of memory on a damaged file with tens of thousands of characters on one baseline (docs/FEHLERGEDAECHTNIS.md,
    /// No. 376). Beyond <see cref="MatchBudget"/> steps it answers no: the line is frozen, which never costs a character.
    /// </summary>
    /// <param name="shift">How far a character may have moved along its line (the rest of a line after a rounded gap: half the grid, plus a
    /// little for the character's size); null: any distance (hiding lengths moves it).</param>
    private static bool Matches(List<Seen> expected, List<Seen> actual, double? shift = null)
    {
        var left = expected.Count - actual.Count;   // how many of those before went
        if (left < 0 || (long)expected.Count * (left + 1) > MatchBudget) return false;
        // reach[k]: the characters before up to here can be matched with k of them left out (so k fewer after)
        var reach = new bool[left + 1];
        var next = new bool[left + 1];
        reach[0] = true;
        for (var i = 0; i < expected.Count; i++)
        {
            Array.Clear(next);
            var e = expected[i];
            var any = false;
            for (var k = 0; k <= left; k++)
            {
                if (!reach[k]) continue;
                var j = i - k;
                if (j < actual.Count && actual[j].Text == e.Text && Math.Abs(actual[j].Y - e.Y) < 0.3 * Math.Max(e.Size, 1) &&
                    (shift is not { } most || Math.Abs(actual[j].X - e.X) <= most + 0.006 * e.Size + 0.05))
                    next[k] = any = true;
                if (e.Touched && k < left) next[k + 1] = any = true;
            }
            if (!any) return false;
            (reach, next) = (next, reach);
        }
        return reach[left];
    }

    /// <summary>
    /// Whether characters of a line lie over each other (two runs set on one baseline, a leader of dots over colons): the writer moves each
    /// run on its own, and their order along the line may change without anything going (docs/FEHLERGEDAECHTNIS.md, No. 485, DocLayNet).
    /// </summary>
    private static bool Overlaid(List<Seen> line)
    {
        for (var i = 1; i < line.Count; i++)
            if (line[i].X - line[i - 1].X < 0.25 * Math.Max(Math.Min(line[i].Size, line[i - 1].Size), 1) && line[i].Text != line[i - 1].Text) return true;
        return false;
    }

    /// <summary>
    /// The same characters on the line, counted text by text (for overlaid runs, whose order is not kept): of each text at least those a bar
    /// did not touch and at most all of them — nothing missing, nothing added.
    /// </summary>
    private static bool SameCharacters(List<Seen> expected, List<Seen> actual)
    {
        var counts = actual.GroupBy(a => a.Text).ToDictionary(t => t.Key, t => t.Count());
        foreach (var group in expected.GroupBy(e => e.Text))
        {
            var have = counts.GetValueOrDefault(group.Key);
            if (have < group.Count(e => !e.Touched) || have > group.Count()) return false;
            counts.Remove(group.Key);
        }
        return counts.Count == 0;
    }

    /// <summary>How far a character moved along its line may lie from its baseline before (the writer's rounding of the text position).</summary>
    private const double BaselineSlack = 0.05;

    /// <summary>Whether the last check says why it refused a page (environment variable PDFKNEAD_SCHWAERZ_DEBUG=1; on standard error).</summary>
    private static readonly bool DebugOn = Environment.GetEnvironmentVariable("PDFKNEAD_SCHWAERZ_DEBUG") == "1";

    private static void Debug(string message)
    {
        if (DebugOn) Console.Error.WriteLine("[Schwärzen, letzte Prüfung] " + message);
    }

    /// <summary>The most steps <see cref="Matches"/> takes for one line (characters before × those left out).</summary>
    private const long MatchBudget = 20_000_000;

    /// <summary>Collects every character of a page as the page shows it (<see cref="Seen"/>), inside forms too.</summary>
    private sealed class GlyphSnapshot(int limit) : IContentObserver
    {
        public List<Seen> Glyphs { get; } = [];

        /// <summary>The page has more characters than the limit (those beyond it are not recorded).</summary>
        public bool Overflow { get; private set; }
        private RectD _clip = RectD.Infinite;

        public bool WantsClip => true;

        private bool _draws = true;

        public void GlyphDraws(bool draws) => _draws = draws;

        public void Clip(RectD bounds) => _clip = bounds;

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize, double horizontalScale,
            bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible)
        {
            if (text is null || !cellToPage.IsInvertible || Overflow) return;
            if (Glyphs.Count >= limit)
            {
                Overflow = true;
                return;
            }
            var (x, y) = cellToPage.Transform(0, 0);
            if (!double.IsFinite(x) || !double.IsFinite(y)) return;
            var box = cellToPage.TransformBounds(cell);
            var (dx, dy) = textToPage.TransformVector(1, 0);
            var upright = !vertical && dx > 0 && Math.Abs(dy) < 1e-3 * Math.Abs(dx);
            // the direction the line runs (vertical writing: down the page in the text's frame)
            var (lx, ly) = vertical ? textToPage.TransformVector(0, -1) : (dx, dy);
            var angle = upright ? 0f : !double.IsFinite(lx) || !double.IsFinite(ly) || lx * lx + ly * ly < 1e-24 ? MathF.PI : (float)Math.Atan2(ly, lx);
            if (!upright && angle == 0) angle = float.Epsilon;   // not level, however close
            var inside = _clip.Intersect(box) is var shown && shown.Width > 0 && shown.Height > 0 && shown.Width * shown.Height >= 0.5 * box.Width * box.Height;
            Glyphs.Add(new Seen(text, x, y, box, Math.Max(box.Height, 1e-3), angle, !hidden && !invisible && inside, CarriesNothing(text, _draws, invisible),
                Ink: ink.IsEmpty ? default : cellToPage.TransformBounds(ink), Present: !hidden && inside));
        }

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void Image(int op, Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool hidden) { }

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden) { }

        public void BeginSoftMask(int op, PdfStream group, Matrix groupToPage) { }

        public void EndForm() { }
    }

    /// <summary>
    /// The characters after redacting, for the last check of what stays (<see cref="StaysChecked(PdfDocument, PdfPage, List{Seen},
    /// PdfDictionary, PdfDictionary, List{RectD}, bool, CancellationToken)"/>; docs/FEHLERGEDAECHTNIS.md, No. 999): grouped by text,
    /// direction (in quarter degrees) and a cell of one point across the line, each group sorted along the line — every character in its own
    /// frame, as the check compares them. A search goes out from the place of the character looked for, the nearer side first, and stops
    /// where no character nearer than the best so far can follow: the distance along the line is never more than the distance on the page
    /// (in the frame of the character looked for; one of a slightly different direction in the same quarter degree may differ by the turn
    /// over the distance from the origin, which the bound allows for). Characters taken are linked past. Those whose place in their frame
    /// overflows (coordinates near the largest number) are tried by every search of their text and direction, as are all of them by a
    /// character whose own place overflows — what the conditions let through then.
    /// </summary>
    private sealed class StayIndex
    {
        private readonly List<Seen> _after;
        private readonly List<int> _present;
        private readonly Func<Seen, (double U, double V)> _framed;
        private readonly Func<Seen, int> _family;
        private readonly Func<Seen, Seen, bool> _sameShape;
        private readonly CancellationToken _cancel;
        private readonly bool[] _taken;
        private readonly int[] _slotOf;       // position in the present characters → slot (−1: its place overflows)
        private readonly int[] _bySlot;       // slot → position in the present characters
        private readonly double[] _along;     // slot → its place along its line (U)
        private readonly int[] _right;        // slot → the next slot not taken from it to the right (the last: the end)
        private readonly int[] _left;         // slot + 1 → the next slot + 1 not taken from it to the left (0: before the start)
        private readonly Dictionary<(string Text, int Family, long Cell), Group> _groups = [];
        private readonly Dictionary<(string Text, int Family), List<int>> _overflowing = [];
        private int _visits;
        // the search under way: the character looked for, its place in its frame, how far along its line, the best so far
        private Seen _g;
        private int _gFamily, _best;
        private double _gu, _gv, _reach, _nearest;

        private sealed record Group(int Start, int End, float LeastAngle, float MostAngle);

        internal StayIndex(List<Seen> after, List<int> present, Func<Seen, (double U, double V)> framed, Func<Seen, int> family,
            Func<Seen, Seen, bool> sameShape, CancellationToken cancel)
        {
            (_after, _present, _framed, _family, _sameShape, _cancel) = (after, present, framed, family, sameShape, cancel);
            _taken = new bool[present.Count];
            _slotOf = new int[present.Count];
            var ids = new Dictionary<(string, int, long), int>();
            var keys = new List<(string Text, int Family, long Cell)>();
            var angles = new List<(float Least, float Most)>();
            var entries = new List<(int Group, double U, int Index)>(present.Count);
            for (var i = 0; i < present.Count; i++)
            {
                var a = after[present[i]];
                var (u, v) = framed(a);
                if (!double.IsFinite(u) || !double.IsFinite(v))
                {
                    _slotOf[i] = -1;
                    var loose = (a.Text, family(a));
                    (_overflowing.TryGetValue(loose, out var list) ? list : _overflowing[loose] = []).Add(i);
                    continue;
                }
                var key = (a.Text, family(a), CellOf(v));
                if (!ids.TryGetValue(key, out var id))
                {
                    ids[key] = id = keys.Count;
                    keys.Add(key);
                    angles.Add((a.Angle, a.Angle));
                }
                else angles[id] = (Math.Min(angles[id].Least, a.Angle), Math.Max(angles[id].Most, a.Angle));
                entries.Add((id, u, i));
            }
            var sorted = entries.ToArray();
            Array.Sort(sorted, (x, y) => x.Group != y.Group ? x.Group.CompareTo(y.Group) : x.U != y.U ? x.U.CompareTo(y.U) : x.Index.CompareTo(y.Index));
            _bySlot = new int[sorted.Length];
            _along = new double[sorted.Length];
            _right = new int[sorted.Length + 1];
            _left = new int[sorted.Length + 1];
            for (var s = 0; s <= sorted.Length; s++) (_right[s], _left[s]) = (s, s);
            for (int s = 0, start = 0; s < sorted.Length; s++)
            {
                (_bySlot[s], _along[s]) = (sorted[s].Index, sorted[s].U);
                _slotOf[sorted[s].Index] = s;
                if (s + 1 == sorted.Length || sorted[s + 1].Group != sorted[s].Group)
                {
                    var id = sorted[s].Group;
                    _groups[keys[id]] = new Group(start, s + 1, angles[id].Least, angles[id].Most);
                    start = s + 1;
                }
            }
        }

        /// <summary>A character found: no search tries it again.</summary>
        internal void Take(int index)
        {
            _taken[index] = true;
            var s = _slotOf[index];
            if (s < 0) return;
            _right[s] = s + 1;
            _left[s + 1] = s;
        }

        private int Right(int s)
        {
            while (_right[s] != s) s = _right[s] = _right[_right[s]];
            return s;
        }

        private int Left(int s)
        {
            var k = s + 1;
            while (_left[k] != k) k = _left[k] = _left[_left[k]];
            return k - 1;
        }

        /// <summary>
        /// One character after, as the check takes it (No. 450, 485, 506): not taken, the same text and direction, within the reach along
        /// the line and 0.6 pt across it in its own frame, its shape where it was; the nearest on the page, of equally near ones the first.
        /// </summary>
        private void Consider(int i)
        {
            if ((++_visits & 4095) == 0) _cancel.ThrowIfCancellationRequested();
            if (_taken[i]) return;
            var a = _after[_present[i]];
            var (au, av) = _framed(a);
            if (a.Text != _g.Text || _family(a) != _gFamily || Math.Abs(au - _gu) > _reach || Math.Abs(av - _gv) > 0.6) return;
            var distance = (a.X - _g.X) * (a.X - _g.X) + (a.Y - _g.Y) * (a.Y - _g.Y);
            if ((distance < _nearest || distance == _nearest && i < _best) && _sameShape(_g, a)) (_best, _nearest) = (i, distance);
        }

        /// <summary>First slot in [start, end) whose place along the line is not below <paramref name="u"/> (with <paramref name="above"/>: above it).</summary>
        private int Bound(int start, int end, double u, bool above)
        {
            while (start < end)
            {
                var mid = start + (end - start) / 2;
                if (above ? _along[mid] <= u : _along[mid] < u) start = mid + 1;
                else end = mid;
            }
            return start;
        }

        /// <summary>
        /// The character after that <paramref name="g"/> is found again as (its position among the present characters), -1 if none: of
        /// those of its text and direction within <paramref name="along"/> along its line and 0.6 pt across it, every one that could be
        /// nearer than the best so far is considered.
        /// </summary>
        internal int Find(Seen g, double gu, double gv, double along)
        {
            var family = _family(g);
            (_g, _gFamily, _gu, _gv, _reach, _best, _nearest) = (g, family, gu, gv, along, -1, double.MaxValue);
            if (_overflowing.TryGetValue((g.Text, family), out var loose))
                foreach (var i in loose) Consider(i);
            if (!double.IsFinite(gu) || !double.IsFinite(gv))
            {
                // its own place overflows: the conditions decide for every character of its text and direction
                foreach (var (key, group) in _groups)
                    if (key.Text == g.Text && key.Family == family)
                        for (var s = Right(group.Start); s < group.End; s = Right(s + 1)) Consider(_bySlot[s]);
                return _best;
            }
            var distance = Math.Sqrt(g.X * g.X + g.Y * g.Y);
            var margin = 1e-9 * (1 + Math.Abs(gu) + along);
            var (first, last) = (CellOf(gv - 0.6), CellOf(gv + 0.6));
            for (var cell = first; ; cell++)
            {
                if (_groups.TryGetValue((g.Text, family, cell), out var group))
                {
                    // the bound along the line: within the rounding, and the turn of a slightly different direction over the distance from the origin
                    var turn = Math.Max(Math.Abs(g.Angle - group.LeastAngle), Math.Abs(g.Angle - group.MostAngle));
                    var slack = 1e-9 * (1 + Math.Abs(gu) + distance) + (turn > 0 ? distance * turn : 0);
                    var lo = Bound(group.Start, group.End, gu - along - margin, above: false);
                    var hi = Bound(lo, group.End, gu + along + margin, above: true);
                    if (lo < hi)
                    {
                        var mid = Bound(lo, hi, gu, above: false);
                        var (r, l) = (Right(mid), Left(mid - 1));
                        while (true)
                        {
                            var right = r < hi;
                            var left = l >= lo;
                            if (!right && !left) break;
                            var toRight = right && (!left || _along[r] - gu <= gu - _along[l]);
                            var away = toRight ? _along[r] - gu : gu - _along[l];
                            if (away - slack > Math.Sqrt(_nearest)) break;
                            Consider(_bySlot[toRight ? r : l]);
                            if (toRight) r = Right(r + 1);
                            else l = Left(l - 1);
                        }
                    }
                }
                if (cell == last) break;
            }
            return _best;
        }
    }
}
