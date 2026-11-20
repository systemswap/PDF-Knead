using System.Text;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Forms.Recognition;

/// <summary>
/// Proposes form fields for pages with real text and lines, without a model (KONZEPT.md, section 5.9a, the part without AI): lines to write on,
/// empty boxes and table cells, small squares and circles, rows of equal small boxes — each with its label (left in the line, inside the box at
/// the top, above, below a line, the head of a table column), kind and format from the rule table (<see cref="FieldRules"/>), a name of its
/// own, in reading order. Nothing is set: the proposals are shown for checking and taken over by the user.
/// </summary>
/// <remarks>
/// Coordinates are page space with y upwards; <see cref="RectD.Top"/> is the smaller y. Where fields already lie nothing is proposed, so the
/// recognition can run again after taking over a part.
/// </remarks>
public static class FormRecognizer
{
    /// <summary>At most this many proposals per page.</summary>
    public const int MaxPerPage = 500;

    /// <summary>A page to look at.</summary>
    /// <param name="Doc">Its document.</param>
    /// <param name="Page">The page.</param>
    /// <param name="Occupied">Where fields already lie (page space).</param>
    /// <param name="Scan">What the models and the page image add (<see cref="PageScan"/>), made when the page is looked at — so that only one
    /// page image is in memory at a time; null or returning null: the page alone.</param>
    public sealed record PageInput(PdfDocument Doc, PdfPage Page, IReadOnlyList<PdfRectangle> Occupied, Func<CancellationToken, PageScan?>? Scan = null);

    /// <summary>Proposals for the pages, named across all of them.</summary>
    /// <param name="pages">The pages in their order.</param>
    /// <param name="existingNames">Names of the fields already in the document: proposals get others.</param>
    /// <param name="cancel">Cancels the recognition.</param>
    /// <param name="progress">Receives the number of pages done.</param>
    /// <returns>The proposals in reading order, page by page.</returns>
    public static List<FieldProposal> Recognize(IReadOnlyList<PageInput> pages, IReadOnlySet<string> existingNames, CancellationToken cancel = default,
        IProgress<int>? progress = null)
    {
        var all = new List<FieldProposal>();
        for (var i = 0; i < pages.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                all.AddRange(Page(pages[i], i, cancel));
            }
            catch (Pdf.PdfException)
            {
                // a page that cannot be read gets no proposals
            }
            progress?.Report(i + 1);
        }
        return Name(all, existingNames);
    }

    // ------------------------------------------------------------ One page

    private sealed class Candidate(RectD rect, ProposalSource source, double confidence)
    {
        public RectD Rect = rect;
        public ProposalSource Source = source;
        public double Confidence = confidence;
        public string Label = "";
        public FormFieldKind Kind = FormFieldKind.Text;
        public int Comb;
        public bool Round;
        public string? Group;
        public string Option = "";

        /// <summary>The field detector saw it as well.</summary>
        public bool Confirmed;

        /// <summary>The detector saw a field to sign.</summary>
        public bool Signature;

        /// <summary>The group of a comb line printed in groups (from 1), and its line; 0 otherwise.</summary>
        public int Part, Row;
    }

    /// <summary>Proposals of one page, named after their labels (the names made unique later).</summary>
    internal static List<FieldProposal> Page(PageInput input, int index, CancellationToken cancel)
    {
        var list = PageInterpreter.Build(input.Doc, input.Page, cancel, withoutOwnElements: true);
        var scan = input.Scan?.Invoke(cancel);
        var leaders = new List<Segment>();
        var phrases = PageWords.Phrases(list.Text, leaders);
        if (scan is { Lines.Count: > 0 }) phrases = PageWords.Phrases(scan.Lines, leaders, phrases);
        var shapes = PageShapes.From(list, cancel, scan?.Image);
        if (shapes.TooMany || shapes.Boxes.Count > PageShapes.MaxBoxes) return [];
        var candidates = new List<Candidate>();
        var page = list.PageBox;
        var pageArea = (page.Right - page.Left) * (page.Bottom - page.Top);

        // 1. small squares and circles: check boxes, option buttons, combs
        var small = shapes.Boxes.Where(IsSmall).Select(b => (Box: b, Round: false))
            .Concat(shapes.Circles.Select(c => (Box: c, Round: true)))
            .Where(s => !phrases.Any(p => Inside(Center(p.Box), Shrink(s.Box, 1))))   // already ticked or printed: not empty
            .ToList();
        var inComb = new HashSet<RectD>();
        foreach (var row in Combs(shapes.Boxes))
        {
            var rect = new RectD(row[0].Left, row[0].Top, row[^1].Right, row[0].Bottom);
            if (phrases.Any(p => Inside(Center(p.Box), rect))) continue;
            foreach (var cell in row) inComb.Add(cell);
            candidates.Add(new Candidate(Shrink(rect, 0.5), ProposalSource.Comb, 0.8) { Comb = row.Count, Label = LabelLeftOrAbove(phrases, rect) ?? "" });
        }
        var boxes = small.Where(s => !inComb.Contains(s.Box)).ToList();
        foreach (var (box, round) in boxes)
        {
            var label = LabelRight(phrases, box) ?? LabelLeftOrAbove(phrases, box);
            candidates.Add(new Candidate(Shrink(box, 0.5), ProposalSource.CheckBox, label is null ? 0.5 : 0.85)
                { Kind = FormFieldKind.CheckBox, Label = label ?? "", Round = round, Option = label ?? "" });
        }
        Choices(candidates.Where(c => c.Kind == FormFieldKind.CheckBox).ToList(), phrases);

        // 2. empty boxes and table cells
        var largeBoxes = shapes.Boxes.Where(b => !IsSmall(b) && !inComb.Contains(b) && Width(b) >= 18 && Height(b) >= 9 && Height(b) <= 300 &&
                                                   Width(b) * Height(b) < 0.4 * pageArea).ToList();
        foreach (var box in largeBoxes)
        {
            if (largeBoxes.Count(other => other != box && Contains(box, other)) >= 2) continue;   // a frame around others
            if (boxes.Any(s => Contains(box, s.Box))) continue;   // around a check box and its label
            var inside = phrases.Where(p => Inside(Center(p.Box), Shrink(box, 1))).ToList();
            var cell = IsCell(box, shapes);
            if (inside.Count == 0)
            {
                var label = cell ? CellLabel(phrases, box, largeBoxes) : LabelLeftOrAbove(phrases, box);
                candidates.Add(new Candidate(Shrink(box, 1.5), cell ? ProposalSource.Cell : ProposalSource.Box, label is null ? 0.6 : 0.85) { Label = label ?? "" });
                continue;
            }
            // a label at the top inside the box (common on printed forms) with room below it
            var lowest = inside.Min(p => p.Box.Top);
            if (inside.All(p => p.Box.Top >= box.Top + 0.45 * Height(box)) && lowest - box.Top >= 10)
                candidates.Add(new Candidate(new RectD(box.Left + 1.5, box.Top + 1.5, box.Right - 1.5, lowest - 1), ProposalSource.Box, 0.8)
                    { Label = string.Join(" ", inside.OrderByDescending(p => p.Box.Bottom).ThenBy(p => p.Box.Left).Select(p => p.Text)) });
        }

        // 3a. comb lines: a line with a small stroke per character; printed in groups with gaps (an IBAN in fours), a field per group, so that
        // each character stands in its cell — the groups of a line share its label
        var combLines = CombLines(shapes);
        for (var row = 0; row < combLines.Count; row++)
        {
            var comb = combLines[row];
            // as high as there is room: up to the next text or line above, at least 9 points (lines set close together)
            var rect = comb.Rect;
            var top = rect.Bottom;
            foreach (var p in phrases)
                if (p.Box.Top > rect.Top + 1 && p.CenterY > rect.Top + 3 && Overlap(p.Box.Left, p.Box.Right, rect.Left, rect.Right) > 0) top = Math.Min(top, p.Box.Top - 0.5);
            foreach (var h in shapes.Horizontal)
                if (h.Pos > rect.Top + 1 && Overlap(h.From, h.To, rect.Left, rect.Right) > 0.3 * Width(rect)) top = Math.Min(top, h.Pos - 0.5);
            rect = rect with { Bottom = Math.Max(rect.Top + 9, top) };
            if (phrases.Any(p => p.Box.Top >= rect.Top - 2 && p.Box.Top <= rect.Top + 3 && Overlap(p.Box.Left, p.Box.Right, rect.Left, rect.Right) > 0)) continue;
            // the label of a comb often stands right under it, starting in its first third („IBAN* (…)“; not a heading across the page, not
            // the label of the next line further right); a remark in brackets alone is none
            var below = LabelBelow(phrases.Where(p => p.Box.Left >= rect.Left - 5 && p.Box.Left <= rect.Left + 0.3 * Width(rect)).ToList(),
                new Segment(rect.Top - 0.8, rect.Left, rect.Right));
            var label = (below is not null && WithoutRemarks(below).Length > 0 ? below : null) ?? LabelLeft(phrases, rect) ??
                        LabelAbove(phrases, new Segment(rect.Top - 0.8, rect.Left, rect.Right), Height(rect));
            for (var part = 0; part < comb.Groups.Count; part++)
            {
                var (left, right, cells) = comb.Groups[part];
                candidates.Add(new Candidate(rect with { Left = left + 0.3, Right = right - 0.3 }, ProposalSource.Comb, label is null ? 0.6 : 0.85)
                    { Comb = cells, Label = label ?? "", Part = comb.Groups.Count > 1 ? part + 1 : 0, Row = row + 1 });
            }
        }
        var inCombLine = combLines.SelectMany(c => c.Parts).ToHashSet();

        // 3b. lines to write on
        foreach (var line in shapes.Horizontal.Where(h => !inCombLine.Contains(h)).Select(s => (Line: s, Leader: false)).Concat(leaders.Select(s => (Line: s, Leader: true))))
        {
            var (s, leader) = line;
            if (s.Length < (leader ? 15 : 30) || s.Length > 0.95 * (page.Right - page.Left)) continue;
            // the edge of a box, a cell or a row of comb boxes — unless a row of boxes drawn as lines stands on it (its sides rise from it): the
            // line is that row's bottom as well (docs/FEHLERGEDAECHTNIS.md, No. 346)
            var sides = shapes.Vertical.Any(v => (Math.Abs(v.Pos - s.From) <= 3 || Math.Abs(v.Pos - s.To) <= 3) && v.From <= s.Pos + 2 && v.To >= s.Pos + 12);
            var edge = !leader && shapes.Boxes.Any(b => OnEdge(s, b));
            if (edge && !sides) continue;
            // underlined text: a phrase stands on the line (one or two characters do not: the cross at the start of a signature line)
            if (phrases.Any(p => p.Text.Length > 2 && p.Box.Top >= s.Pos - 2 && p.Box.Top <= s.Pos + 3 &&
                                 Overlap(p.Box.Left, p.Box.Right, s.From, s.To) > 0.3 * Math.Min(Width(p.Box), s.Length))) continue;
            // the room above: up to the next text or line over it
            // (a text counts that covers a fifth of the line, or a short one at its left end: the label at the top left of a wide box)
            var room = 40.0;
            foreach (var p in phrases)
                if (p.Box.Top > s.Pos && Overlap(p.Box.Left, p.Box.Right, s.From, s.To) is var over &&
                    (over > 0.2 * s.Length || Math.Abs(p.Box.Left - s.From) <= 6 && over > 0.5 * Width(p.Box)))
                    room = Math.Min(room, p.Box.Top - s.Pos);
            var topLine = double.NaN;
            foreach (var h in shapes.Horizontal)
                if (h.Pos > s.Pos + 0.8 && Overlap(h.From, h.To, s.From, s.To) > 0.3 * s.Length)
                {
                    room = Math.Min(room, h.Pos - s.Pos);
                    if (!(h.Pos >= topLine)) topLine = h.Pos;
                }
            if (room < 9) continue;
            // the label: beside it; in a box drawn as lines — a stroke rising from an end of the line, or the label at the top just under a line
            // closing the room — at the top of the box (printed forms set it there; the text under the line is the next box's label); else under
            // the line („Ort, Datum“, „Unterschrift“) or above it
            var above = WordAbove(phrases, s, room);
            // on the edge of a box only as the bottom of a row with its label at the top left (a table's rows carry their text, not a label above
            // room to write in)
            if (edge && (above is null || Math.Abs(above.Box.Left - s.From) > 8)) continue;
            var box = sides || above is not null && topLine - above.Box.Bottom is >= -1 and <= 8;
            var label = LabelLeft(phrases, new RectD(s.From, s.Pos, s.To, s.Pos + Math.Min(room, 16))) ?? (box ? above?.Text : null) ?? LabelBelow(phrases, s) ??
                        above?.Text;
            var signature = label is not null && FieldRules.Meaning(label) == FieldMeaning.Signature;
            var height = Math.Min(room - 1.5, signature ? 36 : 18);
            var rect = new RectD(s.From + 1, s.Pos + 0.8, s.To - 1, s.Pos + 0.8 + height);
            var confidence = label is null ? 0.5 : 0.8;
            // on an edge only where no box or cell at least as certain is proposed: the line under a row of cells would give way to them in step 4
            // anyway, but it would first take in the detector's fields of narrow cells (3c) and lose them with it
            if (edge && candidates.Any(c => c.Confidence >= confidence && Covers(c.Rect, rect))) continue;
            candidates.Add(new Candidate(rect, ProposalSource.Line, confidence) { Label = label ?? "" });
        }

        // 3c. the fields of the detector: what both see is more certain; what only the detector sees snaps to the lines and boxes nearby,
        // what only the drawing shows is less certain (KONZEPT.md, section 5.9a, step 4)
        if (scan is { Detected: true }) Detections(candidates, scan.Fields, phrases, shapes);

        // 4. not where fields lie, one proposal per place (the more certain one), the page's own frame not at all
        var occupied = input.Occupied.Select(r => new RectD(r.Left, r.Bottom, r.Right, r.Top)).ToList();
        var kept = new List<Candidate>();
        foreach (var c in candidates.OrderByDescending(c => c.Confidence))
        {
            if (Width(c.Rect) < 4 || Height(c.Rect) < 4) continue;
            if (occupied.Any(o => Covers(o, c.Rect))) continue;
            if (kept.Any(k => Covers(k.Rect, c.Rect) || Covers(c.Rect, k.Rect))) continue;
            kept.Add(c);
            if (kept.Count >= MaxPerPage) break;
        }

        // 5. reading order: rows from the top (by the lower edge: fields of one line stand on it, whatever their height), in a row from the left
        kept.Sort((a, b) => Math.Abs(a.Rect.Top - b.Rect.Top) > 6 ? b.Rect.Top.CompareTo(a.Rect.Top) : a.Rect.Left.CompareTo(b.Rect.Left));
        return [.. kept.Select(c => Proposal(c, index))];
    }

    /// <summary>A comb line: the area over it, its groups (separated by gaps: left, right, cells), the lines it is made of.</summary>
    private sealed record CombLine(RectD Rect, List<(double Left, double Right, int Cells)> Groups, List<Segment> Parts);

    /// <summary>
    /// Comb lines (KONZEPT.md, section 5.9a): at least three short strokes rising from a line at equal distances (4 … 30 points, each within
    /// a fifth of the first) have a cell between each two — a line may carry several such runs with gaps between them; runs on one height,
    /// close together (a gap under one and a half cells) and with the same cell width, belong together — an IBAN printed in groups of four —
    /// and each group separated by a gap is a field of its own. A line with marks only at its ends is no comb, and a line needs at least three
    /// cells.
    /// </summary>
    private static List<CombLine> CombLines(PageShapes shapes)
    {
        var marks = shapes.Ticks.Concat(shapes.Vertical.Where(v => v.Length <= 8)).OrderBy(t => t.Pos).ToList();
        var groups = new List<(Segment Line, double Width, List<double> At)>();
        if (marks.Count < 3) return [];
        foreach (var line in shapes.Horizontal)
        {
            if (line.Length < 8) continue;
            // the marks standing on the line (from the line upwards), one per place
            var at = new List<double>();
            var first = LowerBound(marks, line.From - 1.5);
            for (var i = first; i < marks.Count && marks[i].Pos <= line.To + 1.5; i++)
            {
                var t = marks[i];
                if (Math.Abs(t.From - line.Pos) > 1 || t.To - line.Pos < 1) continue;
                if (at.Count == 0 || t.Pos - at[^1] > 1) at.Add(t.Pos);
            }
            // runs of equal distances: one line may carry several groups with gaps between them (postcode, then town)
            var start = 0;
            while (start + 2 < at.Count)
            {
                var width = at[start + 1] - at[start];
                var end = start + 1;
                while (end + 1 < at.Count && Math.Abs(at[end + 1] - at[end] - width) <= 0.2 * width) end++;
                if (end - start >= 2 && width is >= 4 and <= 30)
                {
                    groups.Add((line, width, at.GetRange(start, end - start + 1)));
                    start = end;   // the mark ending one run may begin the next
                }
                else start++;
            }
            if (groups.Count > 2000) break;
        }
        var combs = new List<CombLine>();
        foreach (var row in groups.GroupBy(g => Math.Round(g.Line.Pos)).Select(r => r.OrderBy(g => g.At[0]).ToList()))
        {
            var run = new List<(Segment Line, double Width, List<double> At)> { row[0] };
            for (var k = 1; k <= row.Count; k++)
            {
                if (k < row.Count && Math.Abs(row[k].Width - run[0].Width) <= 0.15 * run[0].Width && row[k].At[0] - run[^1].At[^1] <= 1.5 * run[0].Width)
                {
                    run.Add(row[k]);
                    continue;
                }
                var cells = run.Sum(g => g.At.Count - 1);
                var pos = run[0].Line.Pos;
                // two cells alone are too easily the feet of letters over a short line
                if (cells < 3)
                {
                    if (k < row.Count) run = [row[k]];
                    continue;
                }
                double left = run[0].At[0], right = run[^1].At[^1];
                var height = Math.Clamp(1.3 * run[0].Width, 10, 18);
                // runs touching each other (sharing a mark) are one group; a gap starts the next
                var parts = new List<(double Left, double Right, int Cells)>();
                foreach (var g in run)
                    if (parts.Count > 0 && g.At[0] - parts[^1].Right <= 1) parts[^1] = (parts[^1].Left, g.At[^1], parts[^1].Cells + g.At.Count - 1);
                    else parts.Add((g.At[0], g.At[^1], g.At.Count - 1));
                combs.Add(new CombLine(new RectD(left + 0.3, pos + 0.8, right - 0.3, pos + 0.8 + height), parts, [.. run.Select(g => g.Line)]));
                if (k < row.Count) run = [row[k]];
            }
        }
        return combs;
    }

    /// <summary>The first mark at or right of a place (the marks sorted by their place).</summary>
    private static int LowerBound(List<Segment> marks, double x)
    {
        int lo = 0, hi = marks.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (marks[mid].Pos < x) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Merges the fields of the detector into the candidates of a page.</summary>
    private static void Detections(List<Candidate> candidates, IReadOnlyList<PageDetection> fields, List<Word> phrases, PageShapes shapes)
    {
        var drawn = candidates.ToList();
        foreach (var d in fields)
        {
            var choice = d.Kind == DetectedFieldKind.Choice;
            if (drawn.FirstOrDefault(c => (c.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio) == choice && Covers(c.Rect, d.Box)) is { } both)
            {
                if (!both.Confirmed) both.Confidence = Math.Min(0.95, both.Confidence + 0.1);
                both.Confirmed = true;
                both.Signature |= d.Kind == DetectedFieldKind.Signature;
                continue;
            }
            if (candidates.Any(c => c.Source == ProposalSource.Model && Covers(c.Rect, d.Box))) continue;
            var rect = choice ? d.Box : Snap(d.Box, shapes);
            var label = choice ? LabelRight(phrases, rect) ?? LabelLeftOrAbove(phrases, rect) : LabelLeft(phrases, rect) ?? LabelLeftOrAbove(phrases, rect) ??
                (shapes.Horizontal.Any(h => Math.Abs(h.Pos - rect.Top) <= 2) ? LabelBelow(phrases, new Segment(rect.Top, rect.Left, rect.Right)) : null);
            // seen by one source only: less certain than a field both see; without a label or with a weak score (under 0.4) not ticked
            var confidence = label is null || d.Score < 0.4 ? 0.55 : Math.Clamp(d.Score, 0.6, 0.75);
            candidates.Add(new Candidate(rect, ProposalSource.Model, confidence)
            {
                Kind = choice ? FormFieldKind.CheckBox : FormFieldKind.Text, Label = label ?? "", Option = choice ? label ?? "" : "",
                Confirmed = true, Signature = d.Kind == DetectedFieldKind.Signature,
            });
        }
        foreach (var c in drawn)
            if (!c.Confirmed) c.Confidence -= 0.1;
        Choices(candidates.Where(c => c.Kind == FormFieldKind.CheckBox).ToList(), phrases);
    }

    /// <summary>
    /// A text field of the detector on the lines of the page: over a line running along its lower part it stands on the line (from end to end
    /// of the line where it lies within the field), in a box it fills the box; otherwise it stays as found.
    /// </summary>
    private static RectD Snap(RectD box, PageShapes shapes)
    {
        var height = Height(box);
        if (shapes.Horizontal.Where(h => h.Pos >= box.Top - 0.3 * height && h.Pos <= box.Top + 0.4 * height &&
                                         Overlap(h.From, h.To, box.Left, box.Right) >= 0.6 * Width(box) && h.Length <= 2 * Width(box) + 20)
                .OrderBy(h => Math.Abs(h.Pos - box.Top)).FirstOrDefault() is { Length: > 0 } line)
        {
            double left = Math.Max(line.From, box.Left - 0.3 * Width(box)) + 1, right = Math.Min(line.To, box.Right + 0.3 * Width(box)) - 1;
            return new RectD(left, line.Pos + 0.8, right, line.Pos + 0.8 + Math.Max(9, Math.Min(height, 36)));
        }
        if (shapes.Boxes.Where(b => Covers(b, box) && Width(b) * Height(b) <= 2.5 * Width(box) * height).OrderBy(b => Width(b) * Height(b)).FirstOrDefault() is { } around &&
            Width(around) > 0)
            return Shrink(around, 1.5);
        return box;
    }

    private static FieldProposal Proposal(Candidate c, int page)
    {
        var label = Clean(WithoutRemarks(c.Label) is { Length: > 0 } plain ? plain : c.Label);
        var meaning = FieldRules.Meaning(label);
        var spec = new FieldSpec { Name = label, Tooltip = label, Required = FieldRules.Required(c.Label), BorderColor = null, FillColor = null };
        switch (c.Kind)
        {
            case FormFieldKind.CheckBox:
                spec = spec with { Kind = FormFieldKind.CheckBox, ExportValue = Tr.T("Ja") };
                break;
            case FormFieldKind.Radio:
                var option = Clean(c.Option);
                spec = spec with
                {
                    Kind = FormFieldKind.Radio, Name = Clean(c.Group ?? ""), Tooltip = Clean(c.Group ?? ""), ExportValue = option.Length > 0 ? option : Tr.T("Auswahl"),
                    NoToggleToOff = true, CheckStyle = c.Round ? CheckStyle.Circle : spec.CheckStyle,
                };
                break;
            default:
                if (c.Signature || meaning == FieldMeaning.Signature && c.Source is ProposalSource.Line or ProposalSource.Box or ProposalSource.Model)
                {
                    spec = spec with { Kind = FormFieldKind.Signature };
                    break;
                }
                spec = meaning switch
                {
                    FieldMeaning.Date => spec with { Format = new FieldFormat(FieldFormatKind.Date, Pattern: "dd.mm.yyyy") },
                    FieldMeaning.DateEnglish => spec with { Format = new FieldFormat(FieldFormatKind.Date, Pattern: "mm/dd/yyyy") },
                    FieldMeaning.Amount => spec with { Format = new FieldFormat(FieldFormatKind.Number, 2, 2), Alignment = ElementAlignment.Right },
                    FieldMeaning.AmountEnglish => spec with { Format = new FieldFormat(FieldFormatKind.Number, 2), Alignment = ElementAlignment.Right },
                    FieldMeaning.Number => spec with { Format = new FieldFormat(FieldFormatKind.Number), Alignment = ElementAlignment.Right },
                    FieldMeaning.Iban => spec with { MaxLength = 34 },
                    FieldMeaning.Multiline => spec with { Multiline = true },
                    _ => spec,
                };
                if (Height(c.Rect) > 34) spec = spec with { Multiline = true };
                if (c.Comb >= 2) spec = spec with { MaxLength = c.Comb, Comb = true, Multiline = false };
                // a date in eight or six cells is written without points (otherwise it would not fit)
                if (c.Comb >= 2 && spec.Format.Kind == FieldFormatKind.Date && spec.Format.Pattern.Length > c.Comb)
                    spec = spec with
                    {
                        Format = spec.Format with
                        {
                            Pattern = c.Comb switch { 8 => meaning == FieldMeaning.DateEnglish ? "mmddyyyy" : "ddmmyyyy", 6 => meaning == FieldMeaning.DateEnglish ? "mmddyy" : "ddmmyy", _ => spec.Format.Pattern },
                        },
                    };
                break;
        }
        // a group of a line (a part of a date) has no format of its own
        if (c.Part > 0 && spec.Format.Kind != FieldFormatKind.None) spec = spec with { Format = new FieldFormat(FieldFormatKind.None), Alignment = ElementAlignment.Left };
        return new FieldProposal(page, new PdfRectangle(c.Rect.Left, c.Rect.Top, c.Rect.Right, c.Rect.Bottom), spec, label, c.Confidence, c.Source, c.Part, c.Row);
    }

    /// <summary>
    /// Option groups: several boxes in a row whose labels are typical options („ja“, „nein“) or that are round belong to one question — the
    /// text before the first box, or above the row. The rest stay check boxes.
    /// </summary>
    private static void Choices(List<Candidate> boxes, List<Word> phrases)
    {
        foreach (var row in boxes.GroupBy(b => Math.Round(Center(b.Rect).Y / 4)).Select(g => g.OrderBy(b => b.Rect.Left).ToList()))
        {
            if (row.Count < 2 || !(row.All(b => b.Round) || row.All(b => FieldRules.IsOption(b.Option)))) continue;
            var first = row[0].Rect;
            var question = phrases.Where(p => Math.Abs(p.CenterY - Center(first).Y) <= 0.6 * Math.Max(p.Height, Height(first)) && p.Box.Right <= first.Left + 1 &&
                                              !row.Any(b => b.Option == p.Text))
                               .OrderByDescending(p => p.Box.Right).FirstOrDefault()?.Text
                           ?? phrases.Where(p => p.Box.Top > first.Bottom && p.Box.Top - first.Bottom <= 20 && p.Box.Left <= first.Right + 40 && p.Box.Right >= first.Left - 40)
                               .OrderBy(p => p.Box.Top).FirstOrDefault()?.Text;
            foreach (var b in row)
            {
                b.Kind = FormFieldKind.Radio;
                b.Group = question ?? Tr.T("Optionsgruppe");
            }
        }
    }

    // ------------------------------------------------------------ Geometry

    private static double Width(RectD r) => r.Right - r.Left;
    private static double Height(RectD r) => r.Bottom - r.Top;
    private static (double X, double Y) Center(RectD r) => ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    private static bool Inside((double X, double Y) p, RectD r) => p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;
    private static RectD Shrink(RectD r, double by) => new(r.Left + by, r.Top + by, r.Right - by, r.Bottom - by);
    private static bool Contains(RectD outer, RectD inner) =>
        inner.Left >= outer.Left - 0.5 && inner.Right <= outer.Right + 0.5 && inner.Top >= outer.Top - 0.5 && inner.Bottom <= outer.Bottom + 0.5 && inner != outer;
    private static double Overlap(double a0, double a1, double b0, double b1) => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));

    /// <summary>More than a third of the smaller rectangle lies in the other.</summary>
    private static bool Covers(RectD a, RectD b)
    {
        var area = Overlap(a.Left, a.Right, b.Left, b.Right) * Overlap(a.Top, a.Bottom, b.Top, b.Bottom);
        return area > 0.33 * Math.Min(Width(a) * Height(a), Width(b) * Height(b));
    }

    /// <summary>A small square: a check box on paper.</summary>
    private static bool IsSmall(RectD r) =>
        Width(r) is >= 5 and <= 24 && Height(r) is >= 5 and <= 24 && Math.Abs(Width(r) - Height(r)) <= 0.25 * Math.Max(Width(r), Height(r));

    /// <summary>The line lies on the upper or lower edge of a box.</summary>
    private static bool OnEdge(Segment s, RectD b) =>
        (Math.Abs(s.Pos - b.Top) <= 1.2 || Math.Abs(s.Pos - b.Bottom) <= 1.2) && Overlap(s.From, s.To, b.Left, b.Right) > 0.5 * Math.Min(s.Length, Width(b));

    /// <summary>A box with neighbours sharing its edges: a cell of a table.</summary>
    private static bool IsCell(RectD box, PageShapes shapes) =>
        shapes.Boxes.Any(o => o != box && (Math.Abs(o.Top - box.Bottom) < 1 || Math.Abs(o.Bottom - box.Top) < 1 || Math.Abs(o.Left - box.Right) < 1 ||
                                           Math.Abs(o.Right - box.Left) < 1) && Overlap(o.Left, o.Right, box.Left, box.Right) + Overlap(o.Top, o.Bottom, box.Top, box.Bottom) > 2);

    /// <summary>Rows of four or more equal small boxes side by side, touching or nearly: one character each.</summary>
    private static IEnumerable<List<RectD>> Combs(List<RectD> boxes)
    {
        var cells = boxes.Where(b => Width(b) is >= 5 and <= 26 && Height(b) is >= 7 and <= 30).OrderBy(b => b.Top).ThenBy(b => b.Left).ToList();
        foreach (var line in cells.GroupBy(b => (Math.Round(b.Top), Math.Round(b.Bottom))))
        {
            var row = line.OrderBy(b => b.Left).ToList();
            var run = new List<RectD> { row[0] };
            for (var i = 1; i <= row.Count; i++)
            {
                if (i < row.Count && row[i].Left - run[^1].Right is >= -1 and <= 2.5 && Math.Abs(Width(row[i]) - Width(run[0])) <= 1.5)
                {
                    run.Add(row[i]);
                    continue;
                }
                if (run.Count >= 4) yield return run;
                if (i < row.Count) run = [row[i]];
            }
        }
    }

    // ------------------------------------------------------------ Labels

    /// <summary>Running text (a sentence across the page) is no label.</summary>
    private const int MaxLabel = 140;

    /// <summary>
    /// The nearest text left of the field in its line (at its height before one at its edge); if that is only a remark in brackets („(privat)“), with the text right above it
    /// („E-Mail“).
    /// </summary>
    private static string? LabelLeft(List<Word> phrases, RectD field)
    {
        var left = phrases.Where(p => p.Text.Length <= MaxLabel && p.CenterY >= field.Top - 3 && p.CenterY <= field.Bottom + 3 && p.Box.Right <= field.Left + 3 &&
                                      field.Left - p.Box.Right <= 220)
            // the nearest, but a text at the height of the field before one at its upper edge (a heading over the row)
            .OrderBy(p => field.Left - p.Box.Right + 2 * Math.Max(0, Math.Abs(p.CenterY - Center(field).Y) - 0.3 * Height(field))).FirstOrDefault();
        if (left is null || WithoutRemarks(left.Text).Length > 0) return left?.Text;
        // the line above (lines set close together overlap with their boxes: by their middles)
        var above = phrases.FirstOrDefault(p => p.Text.Length <= MaxLabel && Math.Abs(p.Box.Left - left.Box.Left) <= 3 && p.CenterY - left.CenterY >= 0.5 * left.Height &&
                                                p.CenterY - left.CenterY <= 1.6 * left.Height);
        return above is null ? left.Text : above.Text + " " + left.Text;
    }

    /// <summary>A label without its remarks in brackets („IBAN* (Die IBAN finden Sie …)“ → „IBAN*“); empty if nothing else is left.</summary>
    internal static string WithoutRemarks(string label)
    {
        var text = label;
        for (var guard = 0; guard < 10; guard++)
        {
            var open = text.IndexOf('(');
            if (open < 0) break;
            var close = text.IndexOf(')', open);
            text = close < 0 ? text[..open] : text[..open] + text[(close + 1)..];
        }
        return text.Trim();
    }

    /// <summary>Text right after a small box in its line (the label of a check box).</summary>
    private static string? LabelRight(List<Word> phrases, RectD box) =>
        phrases.Where(p => p.Text.Length <= MaxLabel && Math.Abs(p.CenterY - Center(box).Y) <= 0.6 * Math.Max(p.Height, Height(box)) && p.Box.Left >= box.Right - 1 &&
                           p.Box.Left - box.Right <= 40)
            .OrderBy(p => p.Box.Left).FirstOrDefault()?.Text;

    /// <summary>Text left of a box in its line, otherwise just above it.</summary>
    private static string? LabelLeftOrAbove(List<Word> phrases, RectD box) =>
        LabelLeft(phrases, box) ??
        phrases.Where(p => p.Text.Length <= MaxLabel && p.Box.Top >= box.Bottom - 1 && p.Box.Top - box.Bottom <= 16 && Overlap(p.Box.Left, p.Box.Right, box.Left - 10, box.Right) > 0)
            .OrderBy(p => p.Box.Top).ThenBy(p => p.Box.Left).FirstOrDefault()?.Text;

    /// <summary>Small text just under a line („Ort, Datum“, „Unterschrift“).</summary>
    private static string? LabelBelow(List<Word> phrases, Segment line) =>
        // under the line: its middle below, its box may touch the line (descenders of the line's own label do not count)
        phrases.Where(p => p.Text.Length <= MaxLabel && p.CenterY <= line.Pos - 1 && p.Box.Bottom <= line.Pos + 1.5 && line.Pos - p.Box.Bottom <= 14 &&
                           Overlap(p.Box.Left, p.Box.Right, line.From, line.To) > 0.5 * Width(p.Box))
            .OrderByDescending(p => p.Box.Bottom).ThenBy(p => p.Box.Left).FirstOrDefault()?.Text;

    /// <summary>Text above the room over a line, starting near its left end.</summary>
    private static string? LabelAbove(List<Word> phrases, Segment line, double room) => WordAbove(phrases, line, room)?.Text;

    private static Word? WordAbove(List<Word> phrases, Segment line, double room) =>
        phrases.Where(p => p.Text.Length <= MaxLabel && p.Box.Top >= line.Pos + room - 1 && p.Box.Top - line.Pos <= room + 14 && Math.Abs(p.Box.Left - line.From) <= 30)
            .OrderBy(p => p.Box.Top).FirstOrDefault();

    /// <summary>A cell of a table: the head of its column and the label of its row („Menge – Position 1“).</summary>
    private static string? CellLabel(List<Word> phrases, RectD cell, List<RectD> boxes)
    {
        // the head: the nearest text above in the column
        var head = phrases.Where(p => p.Box.Top >= cell.Bottom - 1 && p.Box.Top - cell.Bottom <= 80 && Inside(Center(p.Box) with { Y = cell.Top + 1 }, new RectD(cell.Left - 2, cell.Top, cell.Right + 2, cell.Bottom)))
            .OrderBy(p => p.Box.Top).FirstOrDefault()?.Text;
        // the row: the nearest text left in the same row
        var row = phrases.Where(p => p.CenterY >= cell.Top && p.CenterY <= cell.Bottom && p.Box.Right <= cell.Left + 1 && cell.Left - p.Box.Right <= 600)
            .OrderBy(p => cell.Left - p.Box.Right).FirstOrDefault()?.Text;
        return head is not null && row is not null && head != row ? head + " – " + row : head ?? row;
    }

    // ------------------------------------------------------------ Names

    /// <summary>A label as a field name: one line, without „:“ or „*“ at the end, no full stops (they separate the parts of a name), at most 40 characters.</summary>
    /// <param name="label">The label (or a name typed in).</param>
    /// <returns>The name; empty if nothing is left.</returns>
    public static string Clean(string label)
    {
        var sb = new StringBuilder();
        foreach (var c in label)
            if (!char.IsControl(c)) sb.Append(c == '.' ? ' ' : c);
        var text = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd(':', '*', ' ', '–', '-').TrimStart('*', ' ');
        if (text.Length > 40)
        {
            var cut = text.LastIndexOf(' ', 40);
            text = text[..(cut > 10 ? cut : 40)].TrimEnd();
        }
        return text;
    }

    /// <summary>
    /// Names across the pages: the label, otherwise the kind („Textfeld“); a further field with the same label gets „_2“ …, the options of a
    /// group share its name. The same label at the same place on another page (a head such as „Aktenzeichen“ on every page) gives the same
    /// name: the fields are linked, one value (KONZEPT.md, sections 5.4a and 5.9a).
    /// </summary>
    private static List<FieldProposal> Name(List<FieldProposal> all, IReadOnlySet<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.Ordinal);
        var named = new List<FieldProposal>(all.Count);
        var groups = new Dictionary<(int Page, string Group, double Row), string>();
        var placed = new List<FieldProposal>();
        var lines = new Dictionary<(int Page, int Row), string>();
        foreach (var p in all)
        {
            var stem = p.Spec.Name.Length > 0 ? p.Spec.Name : p.Spec.Kind switch
            {
                FormFieldKind.CheckBox => Tr.T("Kontrollkästchen"),
                FormFieldKind.Radio => Tr.T("Optionsgruppe"),
                FormFieldKind.Signature => Tr.T("Unterschrift"),
                _ => Tr.T("Textfeld"),
            };
            if (p.Spec.Kind == FormFieldKind.Radio)
            {
                // the buttons of a group share its name; a group is one row (Choices) — the same question twice on a page is two groups
                var key = (p.Page, stem, Math.Round((p.Rect.Bottom + p.Rect.Top) / 8));
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = Free(stem, taken);
                named.Add(p with { Spec = p.Spec with { Name = group } });
                continue;
            }
            if (p.Part > 0)
            {
                // the groups of a comb line: the name of the line, the number of the group after a hyphen („IBAN-1“ … „IBAN-6“; a second such
                // line „IBAN_2-1“ …)
                if (!lines.TryGetValue((p.Page, p.Row), out var line)) lines[(p.Page, p.Row)] = line = Free(stem, taken);
                named.Add(p with { Spec = p.Spec with { Name = Free($"{line}-{p.Part}", taken) } });
                continue;
            }
            // with a label, at the place of a field of that label on another page: linked
            var same = p.Spec.Name.Length > 0 && FieldLinks.Linkable(p.Spec.Kind)
                ? placed.FirstOrDefault(o => o.Page != p.Page && o.Label == p.Label && o.Spec.Kind == p.Spec.Kind && SamePlace(o.Rect, p.Rect) &&
                                             !named.Any(n => n.Page == p.Page && n.Spec.Name == o.Spec.Name))
                : null;
            var proposal = p with { Spec = p.Spec with { Name = same?.Spec.Name ?? Free(stem, taken) } };
            named.Add(proposal);
            if (p.Spec.Name.Length > 0 && same is null) placed.Add(proposal);
        }
        return named;
    }

    /// <summary>Whether two fields lie at the same place of their pages (within 6 points at each edge).</summary>
    private static bool SamePlace(PdfRectangle a, PdfRectangle b) =>
        Math.Abs(a.Left - b.Left) <= 6 && Math.Abs(a.Right - b.Right) <= 6 && Math.Abs(a.Bottom - b.Bottom) <= 6 && Math.Abs(a.Top - b.Top) <= 6;

    private static string Free(string stem, HashSet<string> taken)
    {
        if (taken.Add(stem)) return stem;
        var k = 2;
        while (!taken.Add($"{stem}_{k}")) k++;
        return $"{stem}_{k}";
    }
}
