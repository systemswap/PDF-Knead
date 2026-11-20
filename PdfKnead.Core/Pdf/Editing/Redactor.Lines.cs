using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Editing;

// Evening out the lines that lose characters (docs/RECHERCHE-VERSTECKEN.md, B-12; docs/FEHLERGEDAECHTNIS.md, No. 236). After the
// redacted characters are gone, the rest of their line still tells of them: the producing program laid the line out with the redacted
// word, and the spacing after it (numbers in TJ, the moves of Td, word and character spacing Tw/Tc for justified text) depends on its
// width — with the widths of the font, a dictionary narrows the word down ("Edact-Ray", Bland, Iyer, Levchenko, PoPETs 2023). Rounding
// only the gap (No. 130, 138, 154) leaves these. So in such a line every word now starts on the grid of PositionGrid, the spacing
// inside a word is rounded to a hundredth of an em, its line starts on the grid, and Tw/Tc are 0 while it is written and set back to their
// value right after it — every other line stays as it is in the content, byte for byte (No. 804). What the page shows moves by at most a
// quarter point per word.
public static partial class Redactor
{
    /// <summary>A character as the redaction saw it: where it starts along its line (page space), how wide it is, which spacing it had.</summary>
    /// <param name="Op">Number of the text operator.</param>
    /// <param name="Element">Element of the operator (index in TJ; 0 for Tj and ', 2 for ").</param>
    /// <param name="Start">First byte in the string.</param>
    /// <param name="Length">Bytes of the character.</param>
    /// <param name="U">Origin along the line (page space).</param>
    /// <param name="V">Origin across the line (page space): the line it lies on.</param>
    /// <param name="Show">What it shares with the characters around it (direction, size, spacing).</param>
    /// <param name="Advance">How far it moved the text, in text space (with Tc, Tw and Tz).</param>
    /// <param name="Removed">It goes.</param>
    /// <param name="Evens">Its going evens out its line (not for invisible text, which moves nobody).</param>
    /// <param name="Space">It is a space (word spacing applies to it).</param>
    /// <param name="InkHeight">How high it is drawn across its line (page units, 0 if unknown; for an upright line its height on the page).</param>
    /// <param name="InkBottom">Where its drawn part begins across the line, from its baseline (page units; negative below it, as a
    /// descender or the hanging piece of a tall bracket; meaningful with <paramref name="InkHeight"/> above 0).</param>
    /// <remarks>Every character of a content stream is recorded; what the characters of a text operator share is held once in
    /// <see cref="ShowLayout"/>, so an entry takes half the memory.</remarks>
    private readonly record struct GlyphLayout(int Op, int Element, int Start, int Length, double U, double V, ShowLayout Show, double Advance,
        bool Removed, bool Evens, bool Space = false, float InkHeight = 0, float InkBottom = 0)
    {
        public double DirX => Show.DirX;
        public double DirY => Show.DirY;
        public double PerText => Show.PerText;
        public double Height => Show.Height;
        public double FontSize => Show.FontSize;
        public double Scale => Show.Scale;
        public double CharSpacing => Show.CharSpacing;
        public double WordSpacing => Show.WordSpacing;
    }

    /// <summary>What consecutive characters share: the direction of their line and how they were shown.</summary>
    /// <param name="DirX">Direction of the line, x.</param>
    /// <param name="DirY">Direction of the line, y.</param>
    /// <param name="PerText">Page units per unit of text space along the line.</param>
    /// <param name="Height">Font size in page space (tolerance for "the same line").</param>
    /// <param name="FontSize">Font size (Tf).</param>
    /// <param name="Scale">Horizontal scaling (Tz / 100).</param>
    /// <param name="CharSpacing">Tc when it was shown.</param>
    /// <param name="WordSpacing">Tw when it was shown.</param>
    private sealed record ShowLayout(double DirX, double DirY, double PerText, double Height, double FontSize, double Scale, double CharSpacing,
        double WordSpacing);

    /// <summary>At most this many characters per content stream are recorded; beyond it the lines are not evened out (the gap is still rounded).</summary>
    private const int MaxLayout = 300_000;

    /// <summary>Spacing inside a word is rounded to this part of an em: kerning stays, what the producer's rounding left there goes.</summary>
    private const double KernGrid = 0.01;

    /// <summary>
    /// Whether a rounded place lies within half a step of the grid from where the character falls anyway — half a step included: with the
    /// standard widths of the standard 14 fonts (whole thousandths, FG 884) the distances inside a word are often exact multiples of half
    /// a step, and „less than“ moved such a character by half a step away from the one before (docs/FEHLERGEDAECHTNIS.md, No. 884).
    /// </summary>
    private static bool WithinHalfStep(double rounded, double falls, double em) => Math.Abs(rounded - falls) <= 0.5 * KernGrid * em * (1 + 1e-9);

    /// <summary>The widest gap (in em) the rounding may leave between two characters of a word: just below the 0.15 em from which the page
    /// text reads a space, so that a letter-spaced word stays one word.</summary>
    private const double InWordGap = 0.1499;

    /// <summary>Records a character for evening out its line (horizontal writing only; vertical lines keep the rounded gap).</summary>
    /// <param name="ink">Where it is drawn (page space), if known.</param>
    private static void RecordLayout(Scope scope, int op, int element, int start, int length, double advance, double fontSize, double scale, bool vertical,
        Matrix textToPage, in GlyphStyle style, bool removed, bool evens = true, bool space = false, RectD? ink = null)
    {
        if (scope.LayoutTruncated || vertical) return;
        var (ox, oy) = textToPage.Transform(0, 0);
        var (dx, dy) = textToPage.TransformVector(1, 0);
        var (hx, hy) = textToPage.TransformVector(0, 1);
        var per = Math.Sqrt(dx * dx + dy * dy);
        var height = Math.Abs(fontSize) * Math.Sqrt(hx * hx + hy * hy);
        if (!(per > 1e-9) || !double.IsFinite(per) || !double.IsFinite(ox) || !double.IsFinite(oy) || fontSize * scale == 0 || !double.IsFinite(advance))
            return;
        dx /= per;
        dy /= per;
        var layout = scope.Layout ??= [];
        if (layout.Count >= MaxLayout)
        {
            scope.LayoutTruncated = true;
            return;
        }
        var show = scope.LastShow;
        if (show is null || show.DirX != dx || show.DirY != dy || show.PerText != per || show.Height != height || show.FontSize != fontSize ||
            show.Scale != scale || show.CharSpacing != style.CharSpacing || show.WordSpacing != style.WordSpacing)
            scope.LastShow = show = new ShowLayout(dx, dy, per, height, fontSize, scale, style.CharSpacing, style.WordSpacing);
        var v = oy * dx - ox * dy;
        float inkHeight = 0, inkBottom = 0;
        if (ink is { } drawn)
        {
            // across the line (for an upright line its height on the page): the lowest and the highest of its corners
            double a = drawn.Top * dx - drawn.Left * dy, b = drawn.Top * dx - drawn.Right * dy, c = drawn.Bottom * dx - drawn.Left * dy,
                d = drawn.Bottom * dx - drawn.Right * dy;
            double lowest = Math.Min(Math.Min(a, b), Math.Min(c, d)), highest = Math.Max(Math.Max(a, b), Math.Max(c, d));
            if (double.IsFinite(lowest) && double.IsFinite(highest))
            {
                inkHeight = (float)(highest - lowest);
                inkBottom = (float)(lowest - v);
            }
        }
        layout.Add(new GlyphLayout(op, element, start, length, ox * dx + oy * dy, v, show, advance, removed, removed && evens, space, inkHeight, inkBottom));
    }

    /// <summary>
    /// The characters of a line that loses characters which lie in a part of it with nothing removed — beyond a gap of <see cref="PartGap"/>
    /// em, as <see cref="Widen"/> parts a line — and are shown on another line of the text flow than every removed character of that line
    /// (their own line start that moved across the line: another column of the same text object, set on the same baseline). Their place
    /// does not follow from the removed text, so they stay exactly where they are and count as other text on the line, which nothing moves
    /// onto: rounded to the grid as words of the line, the second column of a page moved by up to a quarter point where the first lost a
    /// word on the same baseline (DocLayNet 002e8522; docs/FEHLERGEDAECHTNIS.md, No. 874). A part on the same line of the text flow — a
    /// loosely justified line, words placed by Td along it — stays a part of the line: its places were laid out with the removed word.
    /// </summary>
    /// <param name="layout">Every character of the content stream.</param>
    /// <param name="lineOf">The line a character lies on, by its baseline (<c>BaselineOf</c>).</param>
    /// <param name="flowOf">For each text operator, the line start of the text flow it is shown on.</param>
    private static HashSet<(int Op, int Element, int Start)> ApartParts(List<GlyphLayout> layout, Func<GlyphLayout, (int TextObject, int Line)?> lineOf,
        Dictionary<int, int> flowOf)
    {
        var apart = new HashSet<(int, int, int)>();
        var lines = new Dictionary<(int, int), List<GlyphLayout>>();
        foreach (var g in layout)
        {
            if (lineOf(g) is not { } line) continue;
            if (!lines.TryGetValue(line, out var list)) lines[line] = list = [];
            list.Add(g);
        }
        foreach (var (_, glyphs) in lines)
        {
            glyphs.Sort((a, b) => a.U.CompareTo(b.U));
            var removedFlows = glyphs.Where(g => g.Removed).Select(g => flowOf.GetValueOrDefault(g.Op, -1)).ToHashSet();
            var start = 0;
            for (var i = 1; i <= glyphs.Count; i++)
            {
                if (i < glyphs.Count && glyphs[i].U - GlyphEnd(glyphs[i - 1]) <= PartGap * GlyphEm(glyphs[i - 1])) continue;
                var removedHere = false;
                for (var k = start; k < i && !removedHere; k++) removedHere = glyphs[k].Removed;
                if (!removedHere)
                    for (var k = start; k < i; k++)
                        if (!removedFlows.Contains(flowOf.GetValueOrDefault(glyphs[k].Op, -1))) apart.Add((glyphs[k].Op, glyphs[k].Element, glyphs[k].Start));
                start = i;
            }
        }
        return apart;
    }

    /// <summary>
    /// The pieces of characters built from glyphs set one above the other — a tall bracket, a brace or a radical of a formula: the same
    /// direction and size, origins along the line within 0.01 of each other, baselines on different lines, and the drawn part of the lower
    /// piece reaching the drawn part of the upper one. Such a piece belongs to no line that loses characters: moved with the words of one
    /// line (to the grid, or to make room), it broke away from the pieces above or below it — the lower end of a bracket beside a formula
    /// whose index was redacted moved 0.12 pt, the rest of the bracket not (DocLayNet 5b5658d7; docs/FEHLERGEDAECHTNIS.md, No. 872).
    /// Characters that stay are looked at; text lines whose first letters share their place do not touch across a line.
    /// </summary>
    private static HashSet<(int Op, int Element, int Start)> StackedPieces(List<GlyphLayout> layout)
    {
        var found = new HashSet<(int, int, int)>();
        var candidates = layout.Where(g => !g.Removed && g.InkHeight > 0)
            .OrderBy(g => g.DirX).ThenBy(g => g.DirY).ThenBy(g => g.FontSize).ThenBy(g => g.U).ToList();
        for (var i = 0; i < candidates.Count;)
        {
            var j = i + 1;
            while (j < candidates.Count && candidates[j].DirX == candidates[i].DirX && candidates[j].DirY == candidates[i].DirY
                   && candidates[j].FontSize == candidates[i].FontSize && candidates[j].U - candidates[j - 1].U < 0.01)
                j++;
            if (j - i > 1)
            {
                var column = candidates.GetRange(i, j - i);
                column.Sort((a, b) => a.V.CompareTo(b.V));
                for (var k = 1; k < column.Count; k++)
                {
                    var (lower, upper) = (column[k - 1], column[k]);
                    var size = Math.Max(lower.Height, upper.Height);
                    if (upper.V - lower.V <= 0.3 * size) continue;   // the same line (LineOf)
                    // touching: the top of the lower piece reaches the bottom of the upper one, a twentieth of the size apart at most
                    if (lower.V + lower.InkBottom + lower.InkHeight < upper.V + upper.InkBottom - 0.05 * size) continue;
                    found.Add((lower.Op, lower.Element, lower.Start));
                    found.Add((upper.Op, upper.Element, upper.Start));
                }
            }
            i = j;
        }
        return found;
    }

    private sealed partial class Rewriter
    {
        /// <summary>
        /// Writes the lines that lose characters anew (see above), operator by operator as <see cref="RewriteCore"/> goes through the
        /// content; <see cref="Handle"/> returns true if it wrote the operator itself.
        /// </summary>
        private sealed class LineWriter
        {
            private readonly Dictionary<int, List<(GlyphLayout Glyph, bool OnLine)>> _byOp;
            private readonly HashSet<int> _text;                         // text operators written anew (they lie on lines that lose characters)
            private readonly HashSet<(int Op, char Kind)> _zero;          // Tw ('w') and Tc ('c') written as 0 (also of ")
            // positioning operators of evened lines: where the line starts (and how far hiding lengths moves it), and its first character
            private readonly Dictionary<int, (double U, double Shift, double PerText, GlyphLayout First)> _lineStart;
            private readonly Dictionary<int, double> _leading;            // per text operator: page shift of numbers in TJ before its first character
            private readonly Widening _widening;                          // hiding the lengths of redacted words (Redactor.Widen.cs)
            // the line matrix after each operator that starts a line, as the display has it: the leading belongs to the graphics state (saved
            // by q, inherited by forms); followed here on its own, a T* after "q … 30 TL … Q" moved the next line by the wrong leading
            // (docs/FEHLERGEDAECHTNIS.md, No. 415)
            private readonly Dictionary<int, Matrix> _lines;

            /// <summary>The pieces the bars grow by (page space) and how many gaps could not be widened to their step.</summary>
            public Widening Widening => _widening;

            private Matrix _tlm = Matrix.Identity, _written = Matrix.Identity;   // line matrix as in the content, and as written
            private double _tl;                                                 // leading (TL)
            private double? _pen, _start;                                       // where the next character lands as written (page, along the line)
            // word and character spacing the content has where the written stream has 0 (set for an evened line, No. 804): written back only
            // before a text operator written as it is that uses it — Tw only where it shows a space —, never for nothing: the value of a
            // justified line that held the name would tell its width, and no other line may show it unless that line uses it (its positions
            // show it anyway). A form keeps 0 as before (whether it shows text is not known here; its own Tw/Tc count)
            private double? _owedTw, _owedTc;
            private readonly Stack<(double?, double?)> _owedSaved = new();
            // an operator that ended in removed characters: where they ended (on the grid), where it ended as written, and its units — the text
            // after it goes on from the pen, but one written as it is would go on from where it ended as written (see Show)
            private (double At, double Pen, double PerText, double Size)? _owed;
            // the last character written on the line and its word, so that a word set in several text operators (a font change inside it, as
            // in small capitals) stays one word: before, every operator began a word on the grid (docs/FEHLERGEDAECHTNIS.md, No. 275)
            private (GlyphLayout Glyph, double Natural, double Target, double WordU, double WordTarget, bool Space, bool AfterRemoved)? _last;

            private LineWriter(Dictionary<int, List<(GlyphLayout, bool)>> byOp, HashSet<int> text, HashSet<(int, char)> zero,
                Dictionary<int, (double, double, double, GlyphLayout)> lineStart, Dictionary<int, double> leading, Widening widening,
                Dictionary<int, Matrix> lines)
            {
                _widening = widening;
                _lines = lines;
                _byOp = byOp;
                _text = text;
                _zero = zero;
                _lineStart = lineStart;
                _leading = leading;
            }

            /// <summary>The plan for a content stream: which lines lose characters, which operators are written anew; null if none.</summary>
            /// <param name="content">The content stream.</param>
            /// <param name="scope">What the redaction found in it.</param>
            /// <param name="hideLengths">The bars, if the lengths of redacted words are to be hidden (Redactor.Widen.cs); null otherwise.</param>
            /// <param name="frozen">Lines (baseline y, page space) that keep their characters where they are (see <see cref="Widen"/>).</param>
            public static LineWriter? For(byte[] content, Scope scope, IReadOnlyList<RectD>? hideLengths = null, IReadOnlyList<double>? frozen = null)
            {
                if (scope.Layout is not { Count: > 0 } layout || scope.LayoutTruncated || !layout.Any(g => g.Evens)) return null;
                var glyphsOf = new Dictionary<int, List<GlyphLayout>>();
                foreach (var g in layout)
                {
                    if (!glyphsOf.TryGetValue(g.Op, out var list)) glyphsOf[g.Op] = list = [];
                    list.Add(g);
                }

                // Who sets the spacing of each text operator, where each line starts, the numbers before the first character of a TJ
                var spacingOf = new Dictionary<int, (int, int)>();
                var positioningOf = new Dictionary<int, int>();
                var twValue = new Dictionary<int, double>();
                var tcValue = new Dictionary<int, double>();
                var leading = new Dictionary<int, double>();
                var textObjectOf = new Dictionary<int, int>();
                // the line of the text flow each text operator is shown on: the last line start that moved across the line (BT, T*, ', ",
                // Tm, Td or TD with a move across) — a word placed by Td along the line stays on the line of the word before
                var flowOf = new Dictionary<int, int>();
                var saved = new Stack<(int, int)>();
                int tw = -1, tc = -1, positioning = -1, textObject = -1, flow = -1;
                var parser = new ContentParser(content);
                var index = 0;
                while (parser.Next() is { } op)
                {
                    var number = index++;
                    var operands = parser.Operands;
                    switch (op)
                    {
                        case "q" when saved.Count < 4096:
                            saved.Push((tw, tc));
                            break;
                        case "Q" when saved.Count > 0:
                            (tw, tc) = saved.Pop();
                            break;
                        case "Tw" when operands.Count >= 1:
                            tw = number;
                            twValue[number] = operands[0].Number;
                            break;
                        case "Tc" when operands.Count >= 1:
                            tc = number;
                            tcValue[number] = operands[0].Number;
                            break;
                        case "BT" or "Td" or "TD" or "Tm" or "T*" when PageInterpreter.StartsLine(op, operands):
                            positioning = number;
                            if (op == "BT") textObject = number;
                            if (op is "BT" or "Tm" or "T*" || operands.Count >= 2 && operands[1].Number != 0) flow = number;
                            break;
                    }
                    if (op is not ("Tj" or "TJ" or "'" or "\"")) continue;
                    if (op == "\"" && operands.Count >= 3)
                    {
                        tw = tc = number;
                        twValue[number] = operands[0].Number;
                        tcValue[number] = operands[1].Number;
                    }
                    if (op is "'" or "\"" && PageInterpreter.StartsLine(op, operands)) positioning = flow = number;
                    spacingOf[number] = (tw, tc);
                    positioningOf[number] = positioning;
                    textObjectOf[number] = textObject;
                    flowOf[number] = flow;
                    if (op == "TJ" && operands.Count >= 1 && operands[0].Object is PdfArray parts && glyphsOf.TryGetValue(number, out var glyphs))
                    {
                        // numbers before the element of the first character move the pen before it (positive to the left)
                        var first = glyphs[0];
                        var sum = 0.0;
                        for (var i = 0; i < Math.Min(first.Element, parts.Count); i++)
                            if (parts.GetNumber(i) is { } n) sum += n;
                        leading[number] = sum / 1000 * first.FontSize * first.Scale * first.PerText;
                    }
                }

                // Lines that lose characters: the same baseline in the same text object as a removed character. Only there the spacing
                // was laid out with the removed text; text of another text object on the same baseline (rows of squares over a word)
                // stays where it is
                var removedIn = new Dictionary<int, List<GlyphLayout>>();
                foreach (var g in layout)
                    if (g.Evens && textObjectOf.TryGetValue(g.Op, out var t))
                    {
                        if (!removedIn.TryGetValue(t, out var list)) removedIn[t] = list = [];
                        list.Add(g);
                    }
                // one entry per line (a word of many removed characters is one line), sorted across the lines: each character looks only
                // at the lines within reach (comparing every character with every line grew with their product)
                var linesIn = new Dictionary<int, (GlyphLayout[] Lines, double[] V, double MaxHeight, double[] FirstRemoved)>();
                foreach (var (t, list) in removedIn)
                {
                    var groups = list.GroupBy(g => (Math.Round(g.V, 1), Math.Round(g.DirX, 3), Math.Round(g.DirY, 3)))
                        .Select(l => (Line: l.MaxBy(g => g.Height)!, First: l.Min(g => g.U))).OrderBy(l => l.Line.V).ToArray();
                    GlyphLayout[] lines = [.. groups.Select(l => l.Line)];
                    linesIn[t] = (lines, [.. lines.Select(g => g.V)], lines.Max(g => g.Height), [.. groups.Select(l => l.First)]);
                }
                // a piece of a character set from glyphs one above the other stays where it is, as other text on the line does (StackedPieces;
                // looked for once, and only when a line of a text object loses characters)
                HashSet<(int Op, int Element, int Start)>? stacked = null;
                (int TextObject, int Line)? BaselineOf(GlyphLayout g)
                {
                    if (!textObjectOf.TryGetValue(g.Op, out var t) || !linesIn.TryGetValue(t, out var removed)) return null;
                    if (!g.Removed && (stacked ??= StackedPieces(layout)).Contains((g.Op, g.Element, g.Start))) return null;
                    var reach = 0.3 * Math.Max(g.Height, removed.MaxHeight);
                    var i = Array.BinarySearch(removed.V, g.V - reach);
                    for (i = i < 0 ? ~i : i; i < removed.V.Length && removed.V[i] <= g.V + reach; i++)
                    {
                        var r = removed.Lines[i];
                        if (g.DirX * r.DirX + g.DirY * r.DirY > 0.999 && Math.Abs(g.V - r.V) < 0.3 * Math.Max(g.Height, r.Height)) return (t, i);
                    }
                    return null;
                }
                // another column of the same text object on the same baseline stays where it was (ApartParts)
                var apart = ApartParts(layout, BaselineOf, flowOf);
                (int TextObject, int Line)? LineOf(GlyphLayout g) => apart.Contains((g.Op, g.Element, g.Start)) ? null : BaselineOf(g);
                // a character before the first removed one of its line stays exactly where it was: its place tells nothing of the removed
                // text, and rounding it (or keeping a word gap behind words moved right) pushed a letter just before a bar into the bar —
                // the check refused the page (DocLayNet; docs/FEHLERGEDAECHTNIS.md, No. 486)
                bool OnLine(GlyphLayout g) => LineOf(g) is { } line && linesIn[line.TextObject].FirstRemoved[line.Line] is var first && g.U >= first - 1e-6;
                var widening = hideLengths is null ? Widening.None : Widen(layout, LineOf, hideLengths, frozen ?? []);
                double Shifted(GlyphLayout g) => g.U + widening.Shift.GetValueOrDefault(new GlyphKey(g.Op, g.Element, g.Start));
                var byOp = new Dictionary<int, List<(GlyphLayout, bool)>>();
                foreach (var (op, glyphs) in glyphsOf) byOp[op] = [.. glyphs.Select(g => (g, OnLine(g)))];

                // The text operators on lines that lose characters, and where those lines start
                var text = new HashSet<int>(byOp.Where(e => e.Value.Any(g => g.Item2) && spacingOf.ContainsKey(e.Key)).Select(e => e.Key));
                if (text.Count == 0) return null;
                // the first text operator with characters after each positioning (one pass: looked up per line, a search through all
                // operators grew with the square of their number)
                var firstAfterOf = new Dictionary<int, int>();
                foreach (var (op, p) in positioningOf)
                    if (p >= 0 && byOp.ContainsKey(op) && (!firstAfterOf.TryGetValue(p, out var known) || op < known)) firstAfterOf[p] = op;
                var lineStart = new Dictionary<int, (double, double, double, GlyphLayout)>();
                foreach (var op in text.Order())
                {
                    if (positioningOf.GetValueOrDefault(op, -1) is not (>= 0 and var p) || lineStart.ContainsKey(p)) continue;
                    // the first text operator after this positioning carries the start of the line (its first character, before the
                    // numbers in front of it); only if that is this one
                    if (firstAfterOf.GetValueOrDefault(p, -1) != op) continue;
                    var g = byOp[op][0].Item1;
                    // an operator that begins with removed characters starts where their run starts now (Widening.RunStart)
                    var at = g.Removed && widening.RunStart.TryGetValue(new GlyphKey(g.Op, g.Element, g.Start), out var run) ? run : Shifted(g);
                    lineStart[p] = (g.U + leading.GetValueOrDefault(op), at - g.U, g.PerText, g);
                }

                // Spacing that acts on these lines is written as 0 (its value was laid out with the removed text). Every other line that used
                // it stays as it is, byte for byte: the value is set again just before it, and only if it needs it (LineWriter.Restore) —
                // its positions show the value anyway. Before, such lines were written anew at their positions: the same places in numbers,
                // but summed otherwise, and single characters of untouched lines fell into another sub-pixel step of a renderer (39 pages
                // of DocLayNet with „Länge verbergen“; docs/FEHLERGEDAECHTNIS.md, No. 804). Only a " that sets spacing written as 0 and shows
                // nothing removed is still written anew (it cannot be written as it is without its value)
                var zero = new HashSet<(int, char)>();
                foreach (var op in text)
                {
                    var (w, c) = spacingOf[op];
                    if (w >= 0 && twValue.GetValueOrDefault(w) != 0) zero.Add((w, 'w'));
                    if (c >= 0 && tcValue.GetValueOrDefault(c) != 0) zero.Add((c, 'c'));
                }
                foreach (var (op, (w, c)) in spacingOf)
                    if (byOp.ContainsKey(op) && !text.Contains(op) && (w == op && zero.Contains((w, 'w')) || c == op && zero.Contains((c, 'c'))))
                        text.Add(op);
                return new LineWriter(byOp, text, zero, lineStart, leading, widening, scope.LineMatrices);
            }

            /// <summary>Follows the operator; writes it if it belongs to an evened line (or must be written differently because of one).</summary>
            /// <returns>True if written here.</returns>
            public bool Handle(MemoryStream output, int number, string op, IReadOnlyList<Operand> operands)
            {
                switch (op)
                {
                    case "q":
                        if (_owedSaved.Count < 4096) _owedSaved.Push((_owedTw, _owedTc));
                        return false;
                    case "Q":
                        if (_owedSaved.Count > 0) (_owedTw, _owedTc) = _owedSaved.Pop();
                        return false;
                    case "Tw" when operands.Count >= 1:
                        if (!_zero.Contains((number, 'w')))
                        {
                            _owedTw = null;   // the content sets it: the written stream has it again
                            return false;
                        }
                        output.Write("0 Tw\n"u8);
                        _owedTw = operands[0].Number;
                        return true;
                    case "Tc" when operands.Count >= 1:
                        if (!_zero.Contains((number, 'c')))
                        {
                            _owedTc = null;
                            return false;
                        }
                        output.Write("0 Tc\n"u8);
                        _owedTc = operands[0].Number;
                        return true;
                    case "BT":
                        _tlm = _written = Matrix.Identity;
                        _pen = _start = null;
                        _owed = null;
                        _last = null;
                        if (Snapped(number, null) is not { } begin) return false;
                        output.Write("BT\n"u8);
                        WriteMatrix(output, begin.Matrix);
                        return true;
                    case "ET":
                        _pen = _start = null;
                        _owed = null;
                        _last = null;
                        return false;
                    case "TL" when operands.Count >= 1:
                        _tl = operands[0].Number;
                        return false;
                    case "Td" or "TD" when PageInterpreter.StartsLine(op, operands):
                        if (op == "TD") _tl = -operands[1].Number;
                        var move = Matrix.Translate(operands[0].Number, operands[1].Number);
                        return Position(output, number, _lines.GetValueOrDefault(number, move * _tlm), move * _written, op == "TD");
                    case "Tm" when PageInterpreter.StartsLine(op, operands):
                        var matrix = new Matrix(operands[0].Number, operands[1].Number, operands[2].Number, operands[3].Number, operands[4].Number, operands[5].Number);
                        return Position(output, number, matrix, matrix, false);
                    case "T*":
                        var down = NextLine(number);
                        return Position(output, number, down * _tlm, down * _written, false);
                    case "Tj" or "TJ" or "'" or "\"":
                        return Show(output, number, op, operands);
                    default:
                        return false;
                }
            }

            /// <summary>
            /// A positioning operator: on an evened line its start goes onto the grid (as an absolute Tm); on any other line after one, the
            /// line matrix as in the content is set again (absolute, so that no relative move from a word of the evened line remains).
            /// </summary>
            /// <param name="next">The line matrix after the operator, as in the content.</param>
            /// <param name="verbatim">The line matrix if the operator were written as it is.</param>
            private bool Position(MemoryStream output, int number, Matrix next, Matrix verbatim, bool setsLeading)
            {
                var last = _last;
                _tlm = next;
                _pen = _start = null;
                _owed = null;
                _last = null;
                if (Snapped(number, last) is { } snapped)
                {
                    if (setsLeading) WriteLeading(output);
                    WriteMatrix(output, snapped.Matrix);
                    return true;
                }
                if (verbatim == _tlm)
                {
                    _written = verbatim;
                    return false;
                }
                if (setsLeading) WriteLeading(output);
                WriteMatrix(output, _tlm);
                return true;
            }

            /// <summary>
            /// The line matrix of a positioning operator of an evened line: its start onto the grid (moved by hiding the lengths). If its first
            /// character goes on with the word written last — on its line, no space and nothing removed between, closer than a space
            /// (the rule of the text layer, <see cref="PageText"/>) —, it stands in that word as inside one text operator: at its distance
            /// from the start of the word. Each character set by itself went onto the grid on its own, the distances inside words changed,
            /// and a reference reader split them into pieces („Can cellation“, docs/FEHLERGEDAECHTNIS.md, No. 418).
            /// </summary>
            private (Matrix Matrix, double U)? Snapped(int number, (GlyphLayout Glyph, double Natural, double Target, double WordU, double WordTarget, bool Space, bool AfterRemoved)? last)
            {
                if (!_lineStart.TryGetValue(number, out var start)) return null;
                double u;
                if (InWord(start.First, last) is { } inWord)
                {
                    u = inWord;
                    // the line goes on from where the character stands (the text operator is written anew without numbers before it)
                    var moved = Matrix.Translate((u - start.U) / start.PerText, 0) * _tlm;
                    // the text operator goes on from here, as the next character of the word
                    _written = moved;
                    _pen = u;
                    _last = last;
                    return (moved, u);
                }
                // where it starts as written: moved by hiding the lengths (from where it is in the content), on the grid
                u = Math.Round((start.U + start.Shift) / PositionGrid) * PositionGrid;
                var placed = Matrix.Translate((u - start.U) / start.PerText, 0) * _tlm;
                _written = placed;
                _start = u;
                return (placed, u);
            }

            /// <summary>Where <paramref name="g"/> stands if it goes on with the word written last (see <see cref="Snapped"/>); null if it starts a word.</summary>
            private double? InWord(GlyphLayout g, (GlyphLayout Glyph, double Natural, double Target, double WordU, double WordTarget, bool Space, bool AfterRemoved)? last)
            {
                if (last is not { } l || l.AfterRemoved || l.Space || g.Removed) return null;
                var em = Math.Abs(g.FontSize * g.Scale) * g.PerText;
                if (!(em > 0) || Math.Abs(g.V - l.Glyph.V) > 0.01 * em || g.DirX != l.Glyph.DirX || g.DirY != l.Glyph.DirY) return null;
                double U(GlyphLayout x) => x.U + _widening.Shift.GetValueOrDefault(new GlyphKey(x.Op, x.Element, x.Start));
                var u = U(g);
                var natural = (g.Advance - (g.CharSpacing + (g.Space ? g.WordSpacing : 0)) * g.Scale) * g.PerText;
                // after the one before (left to right), or before it (a word set from right to left: the content may place its characters in
                // any order)
                var forward = u - (U(l.Glyph) + l.Natural);
                var backward = U(l.Glyph) - (u + natural);
                double falls;
                if (forward > -0.5 * em && forward < 0.15 * em) falls = l.Target + l.Natural;
                else if (backward > -0.5 * em && backward < 0.15 * em) falls = l.Target - natural;
                else return null;
                var target = l.WordTarget + Math.Round((u - l.WordU) / (KernGrid * em)) * KernGrid * em;
                target = Math.Min(target, l.Target + l.Natural + InWordGap * em);
                // as inside a text operator: within half a step of where the character falls next to the one before, there — rounded each,
                // the characters of a word overlapped by hundredths of a point, and a reference reader split the word there
                return WithinHalfStep(target, falls, em) ? falls : target;
            }

            /// <summary>
            /// Writes back the spacing the content has where the written stream has 0 (see <see cref="_owedTw"/>), for something that uses it:
            /// <paramref name="tw"/> and <paramref name="tc"/> are the values it needs (the content's at that point).
            /// </summary>
            private void Restore(MemoryStream output, double? tw, double? tc)
            {
                if (_owedTw is not null && tw is { } w)
                {
                    WriteOperator(output, [new Operand(w)], "Tw");
                    _owedTw = null;
                }
                if (_owedTc is not null && tc is { } c)
                {
                    WriteOperator(output, [new Operand(c)], "Tc");
                    _owedTc = null;
                }
            }

            private void WriteMatrix(MemoryStream output, Matrix m)
            {
                WriteOperator(output, [new Operand(m.A), new Operand(m.B), new Operand(m.C), new Operand(m.D), new Operand(m.E), new Operand(m.F)], "Tm");
                _written = m;
            }

            private void WriteLeading(MemoryStream output) => WriteOperator(output, [new Operand(_tl)], "TL");

            /// <summary>
            /// The move of T*, ' or " to the next line, as the display made it: a shift by the leading (ISO 32000-2, 9.4.2), read off its line
            /// matrix; the own leading only without one.
            /// </summary>
            private Matrix NextLine(int number)
            {
                if (_lines.TryGetValue(number, out var next) && _tlm.IsInvertible && (next * _tlm.Invert()) is { IsFinite: true } step)
                    _tl = -Math.Round(step.F, 9);
                return Matrix.Translate(0, -_tl);
            }

            /// <summary>A text operator: written anew if it lies on an evened line or used spacing written as 0.</summary>
            private bool Show(MemoryStream output, int number, string op, IReadOnlyList<Operand> operands)
            {
                var newLine = op is "'" or "\"" && PageInterpreter.StartsLine(op, operands);
                var before = _tlm;   // ' and " move to the next line first (T*)
                if (newLine) _tlm = NextLine(number) * _tlm;
                if (!_text.Contains(number) || !_byOp.TryGetValue(number, out var glyphs))
                {
                    // written as it is: with the spacing the content has (" sets its own)
                    if (op == "\"" && operands.Count >= 3) _owedTw = _owedTc = null;
                    else if (_byOp.TryGetValue(number, out var shownHere) && shownHere.Count > 0)
                        Restore(output, shownHere.Any(g => g.Item1.Space && !g.Item1.Removed) ? shownHere[0].Item1.WordSpacing : null,
                            shownHere[0].Item1.CharSpacing);
                    if (newLine)
                    {
                        // a new line after an evened one: from the line matrix as in the content
                        if (_written != before) WriteMatrix(output, before);
                        _written = _tlm;
                    }
                    // written as it is: what follows no longer goes on from the pen — unless it shows nothing and moves nothing as the display
                    // reads it (a Tj without a string in a damaged file). Forgetting the pen there, the rest of the line was placed from where
                    // its first character stood before, though the characters removed before it had left their width behind, and slid under
                    // the bar (docs/FEHLERGEDAECHTNIS.md, No. 383)
                    if (newLine || _byOp.ContainsKey(number) || op == "TJ" && operands.Count >= 1 && operands[0].Object is PdfArray parts &&
                        Enumerable.Range(0, parts.Count).Any(i => parts.GetNumber(i) is { } n && n != 0))
                    {
                        // it goes on from where the text before it ended as written: an operator before that ended in removed characters
                        // left their width out (the text after it would be placed from the pen), so it is brought where they ended first.
                        // Only here, and rounded to the grid as a gap: a line that goes on from the pen or starts anew tells nothing of them
                        if (!newLine && _owed is { } owed)
                        {
                            var size = _byOp.TryGetValue(number, out var shown) && shown.Count > 0 ? shown[0].Item1.FontSize * shown[0].Item1.Scale : owed.Size;
                            var move = Math.Round(-(owed.At - owed.Pen) / owed.PerText * 1000 / size, 3);
                            if (move != 0 && double.IsFinite(move))
                            {
                                PdfSerializer.Write(new PdfArray([new PdfReal(move)]), output);
                                output.Write(" TJ\n"u8);
                            }
                        }
                        _pen = _start = null;
                        _owed = null;
                        _last = null;
                    }
                    return false;
                }
                _owed = null;   // this one goes on from the pen
                // its word and character spacing: 0 while it is written (they were laid out with the removed text), afterwards the value
                // the content has there again — for ", which sets them, its own operands (No. 804)
                var setsSpacing = op == "\"" && operands.Count >= 3;
                var (wordSpacing, charSpacing) = setsSpacing ? (operands[0].Number, operands[1].Number) : (glyphs[0].Glyph.WordSpacing, glyphs[0].Glyph.CharSpacing);
                if (setsSpacing || wordSpacing != 0 && _owedTw is null) output.Write("0 Tw\n"u8);
                if (setsSpacing || charSpacing != 0 && _owedTc is null) output.Write("0 Tc\n"u8);
                if (wordSpacing != 0) _owedTw = wordSpacing;
                else if (setsSpacing) _owedTw = null;
                if (charSpacing != 0) _owedTc = charSpacing;
                else if (setsSpacing) _owedTc = null;
                if (newLine)
                {
                    _pen = _start = null;
                    _last = null;
                    if (Snapped(number, null) is { } snapped)
                    {
                        WriteMatrix(output, snapped.Matrix);
                    }
                    else if (_written == before)
                    {
                        output.Write("T*\n"u8);
                        _written = _tlm;
                    }
                    else
                    {
                        WriteMatrix(output, _tlm);
                    }
                }

                PdfString? Text(int element) => op switch
                {
                    "TJ" => operands.Count >= 1 && operands[0].Object is PdfArray parts && element < parts.Count ? parts[element] as PdfString : null,
                    "\"" => operands.Count >= 3 ? operands[2].Object as PdfString : null,
                    _ => operands.Count >= 1 ? operands[0].Object as PdfString : null,   // the string the display shows (PageInterpreter)
                };
                var first = glyphs[0].Glyph;
                double U(GlyphLayout g) => g.U + _widening.Shift.GetValueOrDefault(new GlyphKey(g.Op, g.Element, g.Start));
                var pen = _pen ?? _start ?? U(first) + _leading.GetValueOrDefault(number);

                var array = new PdfArray();
                var run = new List<byte>();
                var runHex = false;
                void Flush()
                {
                    if (run.Count == 0) return;
                    array.Add(new PdfString([.. run], runHex));
                    run.Clear();
                }
                GlyphLayout? previous = null;
                double previousNatural = 0, previousTarget = 0, wordU = 0, wordTarget = 0;
                bool previousSpace = false, afterRemoved = false;
                if (_pen is not null && _last is { } last)
                    (previous, previousNatural, previousTarget, wordU, wordTarget, previousSpace, afterRemoved) = last;
                var carried = previous is not null;
                foreach (var (g, onLine) in glyphs)
                {
                    if (g.Removed)
                    {
                        afterRemoved = true;
                        continue;
                    }
                    if (Text(g.Element) is not { } s || g.Start < 0 || g.Start + g.Length > s.Bytes.Length) continue;
                    var bytes = s.Bytes.AsSpan(g.Start, g.Length);
                    var space = g.Length == 1 && bytes[0] == (byte)' ';
                    var natural = (g.Advance - (g.CharSpacing + (space ? g.WordSpacing : 0)) * g.Scale) * g.PerText;
                    var written = natural;   // written with Tw and Tc at 0 (see above)
                    var em = Math.Abs(g.FontSize * g.Scale) * g.PerText;
                    double target;
                    var u = U(g);
                    if (!onLine)
                    {
                        target = g.U;   // a line that loses nothing: exactly where it was
                        (wordU, wordTarget) = (u, target);   // what follows on an evened line counts from here, not from a word before
                    }
                    else if (carried && previous is { } c && (Math.Abs(g.V - c.V) > 0.01 * em || g.DirX != c.DirX || g.DirY != c.DirY))
                    {
                        // carried over from the operator before, but on another line or raised (text rise): a word starts here
                        target = Math.Round(u / PositionGrid) * PositionGrid;
                        wordU = u;
                        wordTarget = target;
                    }
                    else if (previous is not { } p || afterRemoved || previousSpace || u - (U(p) + previousNatural) > 0.15 * em)
                    {
                        target = Math.Round(u / PositionGrid) * PositionGrid;   // a word starts on the grid
                        // a gap every reader took for a space (at least ReaderSpaceGap) stays one, one of at least MinWordGap keeps that (No. 458):
                        // where the nearest point would narrow it below that,
                        // a point further right — every point lies on the grid and tells nothing of the width (docs/FEHLERGEDAECHTNIS.md,
                        // No. 451). At most one step beyond the next point on the right: the word before may itself have moved right, and
                        // without a limit the moves would add up along a line of gaps at the limit. Narrower gaps round as before (the rest
                        // of a line stays within half a step, No. 138); after a space character the words stay apart by it
                        if (previous is { } prior && !previousSpace && u - (U(prior) + previousNatural) is var wordGap && wordGap >= ReaderSpaceGap * em)
                        {
                            var need = previousTarget + previousNatural + (wordGap >= MinWordGap * em ? MinWordGap : ReaderSpaceGap) * em;
                            // (not for less than the grid inside words: such a rest is no gap a reader judges)
                            if (target < need - KernGrid * em) target = Math.Max(target, Math.Min(Math.Ceiling(need / PositionGrid), Math.Ceiling(u / PositionGrid) + 1) * PositionGrid);
                        }
                        wordU = u;
                        wordTarget = target;
                    }
                    else
                    {
                        // inside a word: its distance from the start of the word, rounded — not from the character before, or the rounding
                        // adds up along the word (Tc on every character; docs/FEHLERGEDAECHTNIS.md, No. 241)
                        target = wordTarget + Math.Round((u - wordU) / (KernGrid * em)) * KernGrid * em;
                        // the rounding must not open a gap a reader takes for a space: letter-spaced text just below that gap read
                        // „S ECTI ON“ afterwards (docs/FEHLERGEDAECHTNIS.md, No. 275). The limit lies at the gap itself, above every gap
                        // inside a word, so a capped character comes back to its place with the next ones (a lower limit added up along
                        // a long word: half a point)
                        target = Math.Min(target, previousTarget + previousNatural + InWordGap * em);
                        // within half a step of where the character falls anyway: no number (the word stays one string, searchable)
                        if (WithinHalfStep(target, pen, em)) target = pen;
                    }
                    var shift = Math.Round(-(target - pen) / g.PerText * 1000 / (g.FontSize * g.Scale), 3);
                    if (shift != 0 && double.IsFinite(shift))
                    {
                        Flush();
                        array.Add(new PdfReal(shift));
                        pen -= shift / 1000 * g.FontSize * g.Scale * g.PerText;
                    }
                    if (run.Count > 0 && runHex != s.IsHex) Flush();
                    runHex = s.IsHex;
                    run.AddRange(bytes);
                    carried = false;
                    previousTarget = target;
                    previous = g;
                    previousNatural = natural;
                    previousSpace = space;
                    afterRemoved = false;
                    pen += written;
                }
                // On a line that loses nothing the pen ends where it ended before: the next text operator may not be written anew (it sets
                // spacing of its own) and goes on from here — without this the spacing set to 0 for the last character was missing, and
                // the rest of a justified line stood 2 pt to the left (docs/FEHLERGEDAECHTNIS.md, No. 260)
                if (glyphs[^1] is (var end, false) && end.PerText != 0 && end.FontSize * end.Scale != 0)
                {
                    var trailing = Math.Round(-(end.U + end.Advance * end.PerText - pen) / end.PerText * 1000 / (end.FontSize * end.Scale), 3);
                    if (trailing != 0 && double.IsFinite(trailing))
                    {
                        Flush();
                        array.Add(new PdfReal(trailing));
                        pen -= trailing / 1000 * end.FontSize * end.Scale * end.PerText;
                    }
                }
                Flush();
                PdfSerializer.Write(array, output);
                output.Write(" TJ\n"u8);
                _pen = pen;
                _last = previous is { } kept ? (kept, previousNatural, previousTarget, wordU, wordTarget, previousSpace, afterRemoved) : null;
                _owed = glyphs[^1] is (var gone, true) && gone.Removed && gone.PerText != 0 && gone.FontSize * gone.Scale != 0
                    ? (Math.Round((gone.U + gone.Advance * gone.PerText) / PositionGrid) * PositionGrid, pen, gone.PerText, gone.FontSize * gone.Scale)
                    : null;
                return true;
            }
        }
    }
}
