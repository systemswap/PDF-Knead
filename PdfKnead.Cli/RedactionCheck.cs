using System.Diagnostics;
using System.Globalization;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Cli;

/// <summary>
/// <c>pdf-knead schwaerzpruefung</c> (see docs/PRUEFUNGEN.md, section 6j): redacts generated test cases (<see cref="RedactionCases"/>) or
/// real files exactly as the program does — the gate's <see cref="InDocumentRedaction.Apply"/>, then saving — and checks the rules every
/// redaction must keep:
/// <list type="number">
/// <item>The result opens without repair and has the same pages.</item>
/// <item>No character remains under a bar (judged by its drawn shape, an invisible one by its text box); no hidden character remains in
/// a marked area.</item>
/// <item>Every character outside the marked areas is still there, at its place (to within the rounded gap of 0.25 pt).</item>
/// <item>Nothing visible changes outside the marked areas and the lines whose rest moved by the rounded gap — on the screen and in print;
/// pages without marks stay identical to the pixel (shared fonts, forms and images).</item>
/// <item>The words that must go are nowhere in the file any more (leak search); those that must stay are still there.</item>
/// </list>
/// </summary>
internal static class RedactionCheck
{
    private const double Scale = 1.5;          // pixels per point for the comparison
    private const int Threshold = 24;          // a pixel differs from this many levels in a channel
    private const double Shift = 0.35;         // the rounded gap moves characters by up to 0.25 pt

    // a word start that keeps a reader's space from the word before moves up to two steps of the grid (1 pt) to the right: that much more,
    // along the line and to the right only (docs/FEHLERGEDAECHTNIS.md, No. 451)
    private const double ReadersShift = 0.75;

    /// <summary>Runs the generated cases <paramref name="first"/> … or the files of a folder.</summary>
    /// <param name="hideLengths">Redact with „Länge geschwärzter Wörter verbergen“: the rest of a line that lost characters may move along
    /// it by up to the step, and nothing may lie under the pieces the bars grew by either.</param>
    public static int Run(string? folder, int count, int first, string? keep, Dictionary<string, string> passwords, int maxPages, bool hideLengths = false)
    {
        if (keep is not null) Directory.CreateDirectory(keep);
        int ok = 0, failed = 0, skipped = 0;
        var watch = Stopwatch.StartNew();
        IEnumerable<(RedactionCase? Case, string? File)> work = folder is null
            ? Enumerable.Range(first, count).Select(seed => ((RedactionCase?)RedactionCases.Make(seed, Structure), (string?)null))
            : Directory.EnumerateFiles(folder, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).Select(f => ((RedactionCase?)null, (string?)f));
        foreach (var (made, file) in work)
        {
            RedactionCase? test = made;
            string? password = null;
            if (file is not null)
            {
                password = passwords.GetValueOrDefault(Path.GetFileName(file));
                try
                {
                    test = FromFile(file, password, maxPages);
                }
                catch (Exception)
                {
                    skipped++;
                    continue;
                }
                if (test is null)
                {
                    skipped++;
                    continue;
                }
            }
            var problems = Check(test!, password, keep, hideLengths: hideLengths);
            // generated cases exist only in memory: kept beside their results (for cross-checks with other programs)
            if (keep is not null && file is null) File.WriteAllBytes(Path.Combine(keep, test!.Name + ".pdf"), test.Pdf);
            if (problems.Count == 0)
            {
                ok++;
                continue;
            }
            failed++;
            Console.WriteLine($"{test!.Name}: {string.Join("; ", problems.Take(4))}{(test.Description.Length > 0 ? "  [" + test.Description + "]" : "")}");
            if (keep is not null && file is not null) File.WriteAllBytes(Path.Combine(keep, test.Name + ".pdf"), test.Pdf);
        }
        Console.WriteLine($"Fälle: {ok + failed}, bestanden: {ok}, Abweichungen: {failed}, übersprungen: {skipped}, Zeit {watch.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Checks results made elsewhere — by the program itself, driven with the mouse (<see cref="RedactionMousePlan"/>): for every case of
    /// the folder (PDF with expectation file) the file <c>&lt;name&gt;-maus.pdf</c> in <paramref name="results"/> is checked against the same
    /// rules. A case without a result is counted, not judged (the driver says why).
    /// </summary>
    /// <param name="hideLengths">The program redacted with „Länge geschwärzter Wörter verbergen“ (its default): the drawn bars no planned one
    /// claims are taken as the pieces the bars grew by.</param>
    public static int RunResults(string folder, string results, Dictionary<string, string> passwords, bool hideLengths = false)
    {
        int ok = 0, failed = 0, missing = 0;
        var watch = Stopwatch.StartNew();
        foreach (var file in Directory.EnumerateFiles(folder, "*.pdf").Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var sidecar = Path.Combine(folder, name + "-erwartung.txt");
            if (!File.Exists(sidecar)) continue;
            var result = Path.Combine(results, name + "-maus.pdf");
            if (!File.Exists(result))
            {
                missing++;
                continue;
            }
            var password = passwords.GetValueOrDefault(Path.GetFileName(file));
            var test = FromSidecar(name, File.ReadAllBytes(file), File.ReadAllLines(sidecar));
            var data = File.ReadAllBytes(result);
            List<string> problems;
            try
            {
                var planned = test.Bars;
                var grown = new Dictionary<int, IReadOnlyList<PdfRectangle>>();
                test = test with { Bars = DrawnBars(planned, data, password, out var unmatched, out var extra, grown) };
                problems = Check(test, password, result: data, hideLengths: hideLengths, grown: hideLengths ? grown : null);
                // how the drawn bars fit the plan: tells the mouse (a bar drawn elsewhere) from the program (all as planned)
                if (problems.Count > 0) problems.Insert(0, $"Balken: {planned.Values.Sum(b => b.Count)} geplant, {unmatched} ohne Partner, {extra} zusätzlich");
            }
            catch (Exception ex)
            {
                problems = [$"{ex.GetType().Name}: {ex.Message}" + (Environment.GetEnvironmentVariable("PDFKNEAD_STAPEL") is null ? "" : ex.StackTrace)];
            }
            if (problems.Count == 0)
            {
                ok++;
                continue;
            }
            failed++;
            Console.WriteLine($"{name}: {string.Join("; ", problems.Take(4))}");
            // to tell the program from the mouse: the same file with the bars as drawn, for a run of this command without --ergebnisse
            // (it then redacts as the program does, with exactly these bars)
            var again = Path.Combine(results, "nachstellen");
            Directory.CreateDirectory(again);
            File.Copy(file, Path.Combine(again, name + ".pdf"), overwrite: true);
            string R(PdfRectangle r) => FormattableString.Invariant($"{r.Left:0.###} {r.Bottom:0.###} {r.Right:0.###} {r.Top:0.###}");
            File.WriteAllLines(Path.Combine(again, name + "-erwartung.txt"),
                test.Expectations.Select(e => $"{(e.Fate == Fate.Gone ? "weg" : "bleibt")}\t{e.Page + 1}\t{e.Word}")
                    .Concat(test.Bars.SelectMany(b => b.Value.Select(bar => $"balken\t{b.Key + 1}\t{R(bar)}")))
                    .Concat(test.MarkHidden ? ["verborgen"] : []));
        }
        Console.WriteLine($"Fälle: {ok + failed}, bestanden: {ok}, Abweichungen: {failed}, ohne Ergebnis: {missing}, Zeit {watch.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// The bars as drawn with the mouse: each planned bar replaced by the black bar of the result whose edges lie within 2 pt of it (the
    /// mouse places a bar to a fraction of a pixel, not to the point; the redaction cuts paths at the drawn bar). A planned bar without
    /// such a partner stays — then the rules find it missing.
    /// </summary>
    private static Dictionary<int, List<PdfRectangle>> DrawnBars(Dictionary<int, List<PdfRectangle>> planned, byte[] result, string? password,
        out int unmatched, out int extra, Dictionary<int, IReadOnlyList<PdfRectangle>>? unclaimed = null)
    {
        using var doc = PdfDocument.Open(result, password);
        var drawn = new Dictionary<int, List<PdfRectangle>>();
        unmatched = extra = 0;
        foreach (var (page, bars) in planned)
        {
            if (page >= doc.PageCount)
            {
                drawn[page] = bars;
                continue;
            }
            var found = new List<PdfRectangle>();
            var text = System.Text.Encoding.Latin1.GetString(ContentOf(doc, doc.Pages[page]));
            // the bars close the content of a redacted page: "q 0 g", rectangles, "f Q" (one block per pass)
            foreach (System.Text.RegularExpressions.Match block in System.Text.RegularExpressions.Regex.Matches(text, @"q\s+0\s+g\s+((?:[-+\d.]+\s+[-+\d.]+\s+[-+\d.]+\s+[-+\d.]+\s+re\s+)+)f\s+Q"))
                foreach (System.Text.RegularExpressions.Match r in System.Text.RegularExpressions.Regex.Matches(block.Groups[1].Value, @"([-+\d.]+)\s+([-+\d.]+)\s+([-+\d.]+)\s+([-+\d.]+)\s+re"))
                {
                    double V(int k) => double.Parse(r.Groups[k].Value, CultureInfo.InvariantCulture);
                    double x = V(1), y = V(2), w = V(3), h = V(4);
                    found.Add(new PdfRectangle(Math.Min(x, x + w), Math.Min(y, y + h), Math.Max(x, x + w), Math.Max(y, y + h)));
                }
            var used = new HashSet<int>();
            var list = new List<PdfRectangle>();
            foreach (var b in bars)
            {
                var best = Enumerable.Range(0, found.Count).Where(i => Math.Abs(found[i].Left - b.Left) <= 2 && Math.Abs(found[i].Right - b.Right) <= 2 &&
                                                                       Math.Abs(found[i].Bottom - b.Bottom) <= 2 && Math.Abs(found[i].Top - b.Top) <= 2)
                    .OrderBy(i => Math.Abs(found[i].Left - b.Left) + Math.Abs(found[i].Right - b.Right) + Math.Abs(found[i].Bottom - b.Bottom) + Math.Abs(found[i].Top - b.Top))
                    .Select(i => (int?)i).FirstOrDefault();
                if (best is { } k)
                {
                    used.Add(k);
                    list.Add(found[k]);
                }
                else
                {
                    unmatched++;
                    list.Add(b);
                }
            }
            // drawn bars no planned one claims (a bar that equals another one counts once)
            var others = Enumerable.Range(0, found.Count).Where(i => !used.Contains(i) && !list.Any(l => Math.Abs(l.Left - found[i].Left) < 0.01 &&
                Math.Abs(l.Right - found[i].Right) < 0.01 && Math.Abs(l.Bottom - found[i].Bottom) < 0.01 && Math.Abs(l.Top - found[i].Top) < 0.01)).ToList();
            extra += others.Count;
            if (unclaimed is not null && others.Count > 0) unclaimed[page] = [.. others.Select(i => found[i])];
            drawn[page] = list;
        }
        return drawn;
    }

    /// <summary>The content of a page, with the forms it draws without a matrix of their own (a page drawn whole through a form).</summary>
    private static byte[] ContentOf(PdfDocument doc, PdfPage page)
    {
        var all = new MemoryStream();
        var contents = page.Dictionary.Get(Core.Pdf.Objects.PdfName.Contents);
        IEnumerable<Core.Pdf.Objects.PdfObject?> parts = contents is Core.Pdf.Objects.PdfArray array
            ? Enumerable.Range(0, array.Count).Select(i => (Core.Pdf.Objects.PdfObject?)array.Get(i)) : [contents];
        foreach (var part in parts)
            if (part is Core.Pdf.Objects.PdfStream stream)
            {
                all.Write(doc.GetStreamData(stream));
                all.WriteByte((byte)'\n');
            }
        if (page.Resources?.GetDictionary(Core.Pdf.Objects.PdfName.Get("XObject")) is { } forms)
            foreach (var (key, _) in forms.Entries)
                if (forms.Get(key) is Core.Pdf.Objects.PdfStream form && form.Dictionary.GetName(Core.Pdf.Objects.PdfName.Subtype)?.Value == "Form" &&
                    form.Dictionary.Get(Core.Pdf.Objects.PdfName.Get("Matrix")) is null)
                {
                    all.Write(doc.GetStreamData(form));
                    all.WriteByte((byte)'\n');
                }
        return all.ToArray();
    }

    /// <summary>
    /// Applies the case as the program does and checks the rules; returns the problems found. With <paramref name="result"/> the case
    /// is not applied here: that file (made elsewhere) is checked.
    /// </summary>
    /// <param name="grown">With <paramref name="result"/>: the pieces its bars grew by to hide the length of words, per page.</param>
    public static List<string> Check(RedactionCase test, string? password = null, string? keep = null, byte[]? result = null, bool hideLengths = false,
        IReadOnlyDictionary<int, IReadOnlyList<PdfRectangle>>? grown = null)
    {
        var problems = new List<string>();
        try
        {
            using var original = PdfDocument.Open(test.Pdf, password);
            var items = PageItem.From(original);
            var hiddenAreas = new Dictionary<int, List<PdfRectangle>>();
            var incomplete = new HashSet<int>();
            for (var p = 0; p < items.Length; p++)
            {
                var marks = new List<PageElement>();
                if (test.Bars.TryGetValue(p, out var bars)) marks.AddRange(bars.Select(b => RedactionElement.FromPageAreas([b])));
                if (test.MarkHidden)
                {
                    var scan = HiddenText.Scan(original, original.Pages[p]);
                    if (!scan.Complete) incomplete.Add(p);
                    if (scan.Spots.Count > 0)
                    {
                        hiddenAreas[p] = [.. scan.Spots.SelectMany(HiddenText.MarkAreas)];
                        marks.AddRange(scan.Spots.Select(s => RedactionElement.FromPageAreas(HiddenText.MarkAreas(s)) with { Hidden = true }));
                    }
                }
                if (marks.Count > 0) items[p] = items[p].WithElements([.. items[p].Elements, .. marks]);
            }
            var idle = new List<int>();
            var extensions = grown?.ToDictionary() ?? new Dictionary<int, IReadOnlyList<PdfRectangle>>();
            var partly = new List<int>();
            var options = new PdfSaveOptions
            {
                ImageCodec = SkiaImageCodec.Instance, HideRedactedLengths = hideLengths,
                Cleanup = CleanHidden ? CleanupItems.HiddenContent | CleanupItems.HiddenLayers | CleanupItems.HiddenImageParts | CleanupItems.InvisibleText : CleanupItems.None,
            };
            byte[] data;
            if (result is not null) data = result;
            else
            {
                var (applied, adopted) = InDocumentRedaction.Apply(original, items, options, password, report: r =>
                {
                    idle.AddRange(r.IdleHiddenMarks);
                    partly.AddRange(r.LengthsPartlyShown);
                    foreach (var (page, list) in r.BarExtensions) extensions[page] = list;
                });
                using (applied)
                {
                    using var output = new MemoryStream();
                    PdfWriter.Write(applied, adopted, output, options with { TextLayers = NewOcr is { } ocr ? NewTextLayers(original, items, adopted, ocr) : null });
                    data = output.ToArray();
                    // with reasons (B-14): in the bars, as notes and a protocol after the last page, as the program writes them — the rules below
                    // hold for the redaction itself, the reasons are checked against it
                    if (Reasons && RedactionReasons.Apply(applied, items, new RedactionReasonSettings(RedactionReasons.Suggestions[test.Name.Length % RedactionReasons.Suggestions.Count],
                            InBars: true, Notes: true, RedactionProtocolPlace.Last, "Prüfung"), test.Name + ".pdf", options, password) is var (reasoned, reasonedPages))
                        using (reasoned)
                        {
                            using var withReasons = new MemoryStream();
                            PdfWriter.Write(reasoned, reasonedPages, withReasons, options);
                            problems.AddRange(CheckReasons(data, withReasons.ToArray(), test, password));
                            if (keep is not null) File.WriteAllBytes(Path.Combine(keep, test.Name + "-gruende.pdf"), withReasons.ToArray());
                        }
                }
            }
            if (keep is not null && result is null)
            {
                File.WriteAllBytes(Path.Combine(keep, test.Name + "-geschwaerzt.pdf"), data);
                // the fates of the words, for a cross-check of the files with other programs: "weg" or "bleibt", page, word
                // and the bars with the visible box and turn of their page: "balken", page, left bottom right top, box, rotation
                string R(PdfRectangle r) => FormattableString.Invariant($"{r.Left:0.###} {r.Bottom:0.###} {r.Right:0.###} {r.Top:0.###}");
                File.WriteAllLines(Path.Combine(keep, test.Name + "-erwartung.txt"),
                    test.Expectations.Select(e => $"{(e.Fate == Fate.Gone ? "weg" : "bleibt")}\t{e.Page + 1}\t{e.Word}")
                        .Concat(test.Bars.SelectMany(b => b.Value.Select(bar =>
                            $"balken\t{b.Key + 1}\t{R(bar)}\t{R(HiddenContent.VisibleBox(original.Pages[b.Key]))}\t{original.Pages[b.Key].Rotate}")))
                        .Concat(test.MarkHidden ? ["verborgen"] : []));
            }
            if (idle.Count > 0) problems.Add($"Markierung ohne Wirkung auf Seite {string.Join(", ", idle.Select(i => i + 1))}");
            if (partly.Count > 0) Console.WriteLine($"{test.Name}: Länge teilweise sichtbar auf Seite {string.Join(", ", partly.Select(i => i + 1))} (Hinweis)");

            // 1. opens, same pages
            using var copy = PdfDocument.Open(data, password);
            if (copy.WasRepaired) problems.Add("Ergebnis musste repariert werden");
            if (copy.PageCount != original.PageCount)
            {
                problems.Add($"Seiten {original.PageCount} → {copy.PageCount}");
                return problems;
            }

            var touched = TouchedAcrossPages(original, test.Bars);
            var allowances = new Dictionary<int, ForeignReaders.PageAllowance>();   // what may change per page, for the other readers (--fremd)
            for (var p = 0; p < original.PageCount; p++)
            {
                var bars = test.Bars.GetValueOrDefault(p) ?? [];
                var marked = hiddenAreas.GetValueOrDefault(p) ?? [];
                // with the defaults of the program what nobody sees goes along — not the probably invisible text: the user decides about
                // it, so in these areas only the other kinds must be gone
                // with a new text recognition the old one's place gets new invisible text: what it contains is checked by the fate of the
                // words (5.), a redacted word must not come back
                var cleaned = CleanHidden
                    ? HiddenText.Scan(original, original.Pages[p]).Spots
                        .Where(s => s.Kind != HiddenTextKind.Suspicious && !(NewOcr is not null && s.Kind == HiddenTextKind.Invisible))
                        .SelectMany(HiddenText.MarkAreas).ToList()
                    : [];
                List<PdfRectangle> hidden = [.. marked, .. cleaned];
                // an annotation touched by a bar goes as a whole (with its pop-up): its place may change
                var dropped = DroppedAnnotations(original.Pages[p], bars, touched);
                // what must stay is the page without the annotations a bar touches: they go as a whole, also their text that is
                // clipped away beside them (a form field whose lines overflow it)
                // the characters as drawn, each with its own text (docs/FEHLERGEDAECHTNIS.md, No. 1251): a replacement text (/ActualText) that goes
                // with a bar is no lost character (No. 624; its fate is checked with the words, 5.), and the characters drawn within it are checked
                var before = PageInterpreter.Build(original, WithoutAnnotations(original.Pages[p], bars, touched), drawnText: true).Text;
                var after = PageInterpreter.Build(copy, copy.Pages[p], drawnText: true).Text;

                // 2. nothing under a bar (nor under what it grew by to hide the length of a word); nothing hidden left in a marked area
                var grownHere = extensions.GetValueOrDefault(p) ?? [];
                // by its middle, and at least half of what it draws: a thin bar across a tall character (a brace over several lines) leaves it
                // mostly visible above and below — it stays, as the redaction keeps it (29.10.2026)
                var allBars = bars.Concat(grownHere).ToList();
                foreach (var (text, cx, cy, ink) in Shapes(copy, copy.Pages[p]))
                    if (allBars.Where(b => Contains(Shrink(b, 0.5), (cx, cy))).Select(b => (PdfRectangle?)b).FirstOrDefault() is { } bar && Covered(ink, allBars) >= 0.5)
                    {
                        problems.Add($"S. {p + 1}: Zeichen „{text}“ unter einem Balken übrig, Mitte ({cx:0.#}, {cy:0.#}) pt, " +
                            $"Balken ({bar.Left:0.#}, {bar.Bottom:0.#}) – ({bar.Right:0.#}, {bar.Top:0.#})");
                        break;
                    }
                if (bars.Count > 0 && !incomplete.Contains(p))
                {
                    // hidden characters under a bar, by their middle as for painted ones above (a line of a spot keeps its box from the
                    // character before a bar to the one after it — "most of the line under bars" also hit lines whose characters all lie
                    // beside the bars, 29.10.2026); spaces aside, they carry nothing (the word gaps left between two bars); and nothing
                    // painted touches a bar that nobody sees any more (what showed of it lies under the bar)
                    var leftUnder = after.Where(g => !string.IsNullOrWhiteSpace(g.Text))
                        .Select(g => ((g.X0 + g.X1 + g.X2 + g.X3) / 4.0, (g.Y0 + g.Y1 + g.Y2 + g.Y3) / 4.0))
                        .Where(m => bars.Any(b => Contains(Shrink(b, 0.5), m))).ToList();
                    var under = HiddenText.Scan(copy, copy.Pages[p]).Spots.Where(s => s.Lines.Any(line => leftUnder.Any(m => Contains(Inflate(line, 0.5), m))) ||
                        s.Kind == HiddenTextKind.Suspicious && !string.IsNullOrWhiteSpace(s.Text) && s.Lines.Any(line => Covered(line, bars) > 0.001)).ToList();
                    if (under.Count > 0)
                        problems.Add($"S. {p + 1}: verborgener Text unter einem Balken übrig („{Short(under[0].Text)}“, {under[0].Kind} bei " +
                            $"({under[0].Bounds.Left:0.#}, {under[0].Bounds.Bottom:0.#}) – ({under[0].Bounds.Right:0.#}, {under[0].Bounds.Top:0.#}))");
                }
                if (hidden.Count > 0 && !incomplete.Contains(p))
                {
                    var rest = HiddenText.Scan(copy, copy.Pages[p]).Spots.Where(s => marked.Any(a => Contains(Inflate(a, 1), Center(s.Bounds))) ||
                        s.Kind != HiddenTextKind.Suspicious && cleaned.Any(a => Contains(Inflate(a, 1), Center(s.Bounds)))).ToList();
                    if (rest.Count > 0)
                        problems.Add($"S. {p + 1}: noch {rest.Count} verborgene Stellen in markierten Flächen („{Short(rest[0].Text)}“, {rest[0].Kind} bei " +
                            $"({rest[0].Bounds.Left:0.#}, {rest[0].Bounds.Bottom:0.#}) – ({rest[0].Bounds.Right:0.#}, {rest[0].Bounds.Top:0.#}))");
                }

                // 3. everything outside is still there, at its place
                // with a new text recognition the old one is replaced on purpose: only what is drawn must stay (its words are checked in 5.)
                var lost = NewOcr is null ? Lost(before, after, bars, hidden, dropped, hideLengths)
                    : Lost([.. before.Where(g => g.Visible)], [.. after.Where(g => g.Visible)], bars, hidden, dropped, hideLengths);
                if (lost.Missing.Count > 0)
                {
                    // where the first one is, and how far the nearest character of the same text lies from it afterwards
                    var g = lost.Missing[0];
                    var (mx, my) = Center(g);
                    var near = after.Where(o => o.Text == g.Text).Select(o => Center(o)).Select(c => (c.X - mx, c.Y - my))
                        .OrderBy(d => d.Item1 * d.Item1 + d.Item2 * d.Item2).Select(d => ((double X, double Y)?)d).FirstOrDefault();
                    problems.Add($"S. {p + 1}: {lost.Missing.Count} Zeichen außerhalb fehlen oder sind verrutscht („{string.Concat(lost.Missing.Take(12).Select(m => m.Text))}“, " +
                        $"das erste bei ({mx:0.#}, {my:0.#}) pt{(near is { } n ? $", nächstes gleiches um ({n.X:0.##}, {n.Y:0.##}) pt" : ", keines mehr")})");
                }

                // no point of a painted path under a bar (beyond a seam at its edge: the cut ends of a stroke lie its reach inside)
                if (bars.Count > 0 && PathUnderBar(copy, copy.Pages[p], bars, grownHere) is { } path) problems.Add($"S. {p + 1}: {path}");
                // the cut pieces of paths by their place and paint (on their path, painted alike); their pixels are left out (CutPieces)
                var (offPath, cutPaths) = bars.Count > 0
                    ? CutPieces(original, original.Pages[p], copy, copy.Pages[p], [.. bars, .. grownHere])
                    : ((string?)null, new List<PaintedLine>());
                if (offPath is not null) problems.Add($"S. {p + 1}: {offPath}");

                // image data under the bars is blacked out (the bar over it hides it on the screen only)
                if (bars.Count > 0 && ImageUnderBar(copy, copy.Pages[p], bars) is { } image) problems.Add($"S. {p + 1}: {image}");

                // the bars are there and black (a bar drawn in a state the content left behind was a thin line)
                if (bars.Count > 0 && Unpainted(copy, copy.Pages[p], bars) is { } thin) problems.Add($"S. {p + 1}: Balken nicht deckend ({thin})");

                // 4. nothing visible changes outside (screen and print) — but a character under a bar goes as a whole, also what of its shape
                // sticks out of the bar (an accent above the line): keeping it would leave the character in the file
                // a character touching a bar may go as a whole (a slanted one from a quarter covered on)
                var removedShapes = bars.Count == 0 ? [] : Shapes(original, original.Pages[p])
                    .Where(s => bars.Any(b => s.Ink.Left < b.Right && s.Ink.Right > b.Left && s.Ink.Bottom < b.Top && s.Ink.Top > b.Bottom)).Select(s => s.Ink).ToList();
                // the rest of their line may move by the rounded gap — also in the cell of a pattern, which the text of the page lacks
                var shapeBands = removedShapes.Select(r => new PdfRectangle(-1e6, r.Bottom - 2, 1e6, r.Top + 2)).ToList();
                // so may the rest of a line that lost marked hidden characters — also characters no search can read, which the page text
                // (and so the bands of the lost characters) lacks
                if (hidden.Count > 0)
                    shapeBands.AddRange(Shapes(original, original.Pages[p]).Where(s => hidden.Any(h => Contains(Inflate(h, 0.5), (s.X, s.Y))))
                        .Select(s => new PdfRectangle(-1e6, s.Ink.Bottom - 2, 1e6, s.Ink.Top + 2)));
                var unreadable = bars.Count == 0 ? [] : UnreadableImages(original, original.Pages[p], bars);
                var refined = new List<PdfRectangle>();
                var seams = bars.Count == 0 ? [] : ImageSeams(original, original.Pages[p], bars, refined);
                foreach (var print in new[] { false, true })
                {
                    var change = Changed(original, original.Pages[p], copy, copy.Pages[p], print, [.. bars, .. grownHere, .. dropped, .. removedShapes, .. unreadable, .. seams], [.. lost.Bands, .. shapeBands], refined,
                        offPath is null ? cutPaths : null);
                    if (change is { } c) problems.Add($"S. {p + 1}{(print ? " (Druck)" : "")}: sichtbar verändert außerhalb, {c}");
                }
                allowances[p] = new ForeignReaders.PageAllowance(allBars, [.. bars, .. grownHere, .. dropped, .. removedShapes, .. unreadable, .. seams],
                    [.. lost.Bands, .. shapeBands]);
            }

            // the same file through other readers (--fremd): what any of them shows or extracts counts, not only what our display shows
            if (ForeignReaders.Folder is not null) problems.AddRange(ForeignReaders.Check(test, original, data, password, allowances, Reasons));

            // 5. the fate of the words — a word gone from the text that is also a name of the content's syntax (the tag /Figure of
            // marked content, a resource name) is found by the leak search there: syntax, not text (29.10.2026)
            HashSet<string>? names = null;
            var unseenText = new Dictionary<int, string>();
            foreach (var e in test.Expectations)
            {
                // a word met only inside a name of the syntax — as a whole name token (the tag /Formula, /Placement /Inline), never inside a
                // string or the page text — is syntax (No. 792, 803; before, a word that was part of any such name was excused everywhere)
                var found = LeakSearch.Find(data, [e.Word], password, out _, names ??= ContentNames(copy)).Count > 0;
                if (e.Fate == Fate.Gone && found) problems.Add($"„{e.Word}“ noch in der Datei (S. {e.Page + 1})");
                if (e.Fate == Fate.Stays && !found && !GoesAlong(e)) problems.Add($"„{e.Word}“ fehlt (S. {e.Page + 1})");
            }

            // with the defaults of the program a word that nobody saw — only in invisible text (the old text recognition), on a hidden layer,
            // beside the page — goes along with it
            bool GoesAlong(Expectation e)
            {
                if (!CleanHidden) return false;
                if (!unseenText.TryGetValue(e.Page, out var text))
                    unseenText[e.Page] = text = string.Join("\n", HiddenText.Scan(original, original.Pages[e.Page]).Spots
                        .Where(s => s.Kind != HiddenTextKind.Suspicious).Select(s => s.Text));
                return text.Contains(e.Word, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            problems.Add($"{ex.GetType().Name}: {ex.Message}" + (Environment.GetEnvironmentVariable("PDFKNEAD_STAPEL") is null ? "" : ex.StackTrace));
        }
        return problems;
    }

    private static (double X, double Y) Center(TextGlyph g) => ((g.X0 + g.X1 + g.X2 + g.X3) / 4, (g.Y0 + g.Y1 + g.Y2 + g.Y3) / 4);

    private static (double X, double Y) Center(PdfRectangle r) => ((r.Left + r.Right) / 2, (r.Bottom + r.Top) / 2);

    private static bool Contains(PdfRectangle r, (double X, double Y) p) => p.X >= r.Left && p.X <= r.Right && p.Y >= r.Bottom && p.Y <= r.Top;

    private static bool Inside(PdfRectangle r, TextGlyph g) => Contains(r, Center(g));

    /// <summary>
    /// Middle of each character with text as the reader meets it: of the drawn shape for a visible character — a shape drawn mostly
    /// beside its text box (a distorted font matrix) is seen there, and a bar over the box hides nothing of it —, of the text box for
    /// an invisible one (text mode 3, a layer that is off), which can only be found by selecting it.
    /// </summary>
    /// <returns>Per character its text, the middle and the bounds of its drawn shape (of the text box without a shape).</returns>
    /// <summary>How often <paramref name="word"/> stands in <paramref name="text"/>, ignoring case.</summary>
    private static int Occurrences(string text, string word)
    {
        var count = 0;
        for (var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + word.Length, StringComparison.OrdinalIgnoreCase))
            count++;
        return count;
    }

    /// <summary>Every name the content of the pages and their forms uses as an operand or in marked content (tags such as /Figure, resource names,
    /// keys of inline images).</summary>
    private static HashSet<string> ContentNames(PdfDocument doc)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        foreach (var page in doc.Pages)
        {
            using (var content = Redactor.PageContent(doc, page.Dictionary)) Read(content.Span.ToArray(), page.Resources, page.Resources, 0);
        }
        // the standard vocabulary of the structure (12.11.2026): a standard structure type (/S /Formula) and the values of the standard
        // attribute owners (/Placement /Inline) are syntax as well — a word redacted from the text that only stands there is not left over.
        // Role names a file defines itself (its /RoleMap) are not taken: one of them could carry what was redacted
        Structure(doc.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")));
        return names;

        void Structure(PdfDictionary? root)
        {
            if (root is null) return;
            var stack = new Stack<(PdfObject Node, int Depth)>();
            stack.Push((root, 0));
            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var budget = 200_000;
            while (stack.TryPop(out var item) && --budget > 0)
            {
                if (item.Depth > 64 || item.Node.AsDictionary() is not { } element || !visited.Add(element)) continue;
                if (element.Get(PdfName.Get("S")) is PdfName type && (Core.Pdf.Accessibility.StandardStructure.IsStandard(type.Value, Core.Pdf.Accessibility.StructureSpace.Pdf17) ||
                                                                     Core.Pdf.Accessibility.StandardStructure.IsStandard(type.Value, Core.Pdf.Accessibility.StructureSpace.Pdf20)))
                    names.Add(type.Value);
                var attributes = element.Get(PdfName.Get("A"));
                foreach (var attribute in attributes is PdfArray list ? list.Select((_, i) => list.Get(i)) : [attributes])
                    if (attribute.AsDictionary() is { } dict && dict.Get(PdfName.Get("O")) is PdfName { Value: "Layout" or "List" or "PrintField" or "Table" })
                        foreach (var (key, value) in dict.Entries)
                        {
                            names.Add(key.Value);
                            if (value is PdfName v) names.Add(v.Value);
                        }
                switch (element.Get(PdfName.Get("K")))
                {
                    case PdfArray kids:
                        for (var i = 0; i < kids.Count && i < 10_000; i++) stack.Push((kids.Get(i), item.Depth + 1));
                        break;
                    case PdfDictionary kid:
                        stack.Push((kid, item.Depth + 1));
                        break;
                }
            }
        }

        void Read(byte[] content, PdfDictionary? resources, PdfDictionary? page, int depth)
        {
            var parser = new ContentParser(content);
            while (parser.Next() is not null)
            {
                foreach (var operand in parser.Operands)
                {
                    if (operand.Name is { } name) names.Add(name.Value);
                    if (operand.Object is PdfDictionary properties)
                        foreach (var (key, value) in properties.Entries)
                        {
                            names.Add(key.Value);
                            if (value is PdfName v) names.Add(v.Value);
                        }
                }
                if (parser.InlineImage is { } inline)
                    foreach (var (key, value) in inline.Dictionary.Entries)
                    {
                        names.Add(key.Value);
                        if (value is PdfName v) names.Add(v.Value);
                    }
            }
            if (depth >= 8 || resources?.GetDictionary(PdfName.Get("XObject")) is not { } xobjects) return;
            foreach (var (key, _) in xobjects.Entries)
                if (xobjects.Get(key) is PdfStream form && form.Dictionary.GetName(PdfName.Subtype)?.Value == "Form" && seen.Add(form))
                    Read(doc.GetStreamData(form), form.Dictionary.GetDictionary(PdfName.Resources) ?? page, page, depth + 1);   // as the display (No. 791)
        }
    }

    private static List<(string Text, double X, double Y, PdfRectangle Ink)> Shapes(PdfDocument doc, PdfPage page)
    {
        // what each character really draws, from the entries tagged with its number (the glyph of a Type 3 font may paint beyond its box)
        var list = PageInterpreter.Build(doc, page, default, withoutOwnElements: true, tagGlyphs: true);
        var drawn = new Dictionary<int, RectD>();
        if (list.GlyphTags is { } tags)
        {
            var items = list.Items.Span;
            for (var i = 0; i < items.Length && i < tags.Length; i++)
            {
                if (tags[i] < 0) continue;
                var r = RectD.FromLtrb(items[i].Left, items[i].Top, items[i].Right, items[i].Bottom);
                drawn[tags[i]] = drawn.TryGetValue(tags[i], out var known) ? known.Union(r) : r;
            }
        }
        var collector = new ShapeCollector(drawn);
        PageInterpreter.Observe(doc, page, collector);
        return collector.Shapes;
    }

    /// <summary>
    /// A point of a painted path (not a character) inside a bar, beyond a seam
    /// at its edge of 0.5 pt, for a stroke its width more, and not on the edge of another bar; null if there is none.
    /// </summary>
    /// <param name="grown">The pieces the bars grew by (hiding the lengths of words): their edges are edges of the painted bars too.</param>
    private static string? PathUnderBar(PdfDocument doc, PdfPage page, List<PdfRectangle> bars, IReadOnlyList<PdfRectangle>? grown = null)
    {
        List<PdfRectangle> edges = [.. bars, .. grown ?? []];
        var list = PageInterpreter.Build(doc, page, default, withoutOwnElements: true, tagGlyphs: true);
        var items = list.Items.Span;
        var tags = list.GlyphTags;
        for (var i = 0; i < items.Length; i++)
        {
            ref readonly var item = ref items[i];
            if (item.Path < 0 || item.Glyph || tags is not null && i < tags.Length && tags[i] >= 0) continue;
            if (item.Kind is not (DisplayItemKind.Fill or DisplayItemKind.Stroke or DisplayItemKind.FillStroke)) continue;
            var paint = list.Paints[item.Paint];
            var stroked = item.Kind != DisplayItemKind.Fill;
            var matrix = list.Matrices[item.Matrix];
            var seam = 0.5 + (stroked ? paint.LineWidth * matrix.MeanScale : 0);
            var kind = item.Kind;
            var (verb, verbCount, first) = list.Paths[item.Path];
            // "sh" fills its clip: the interpreter makes that rectangle itself (in page space), it is no path of the file
            if (item.Kind == DisplayItemKind.Fill && paint.FillShading >= 0 && matrix == Matrix.Identity && verbCount == 5 &&
                list.Verbs[verb] == PathVerb.Move && list.Verbs[verb + 4] == PathVerb.Close) continue;
            var count = 0;
            for (var v = verb; v < verb + verbCount && v < list.Verbs.Length; v++)
                count += list.Verbs[v] switch { PathVerb.Move or PathVerb.Line => 1, PathVerb.Cubic => 3, _ => 0 };
            var points = list.Points;
            // the points the path passes through: end points, and along each curve (its control points may lie anywhere in its hull —
            // a curve passing beside a bar can have them under it)
            var at = first;
            double cx = 0, cy = 0;
            for (var v = verb; v < verb + verbCount && v < list.Verbs.Length && at + 1 < points.Length; v++)
            {
                switch (list.Verbs[v])
                {
                    case PathVerb.Move or PathVerb.Line:
                        (cx, cy) = (points[at], points[at + 1]);
                        at += 2;
                        if (Inside(cx, cy) is { } atPoint) return atPoint;
                        break;
                    case PathVerb.Cubic when at + 5 < points.Length:
                        double x1 = points[at], y1 = points[at + 1], x2 = points[at + 2], y2 = points[at + 3], x3 = points[at + 4], y3 = points[at + 5];
                        at += 6;
                        for (var k = 1; k <= 16; k++)
                        {
                            var t = k / 16.0;
                            var u = 1 - t;
                            var (bx, by) = (u * u * u * cx + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3, u * u * u * cy + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3);
                            if (Inside(bx, by) is { } onCurve) return onCurve;
                        }
                        (cx, cy) = (x3, y3);
                        break;
                }
            }

            string? Inside(double px, double py)
            {
                var (x, y) = matrix.Transform(px, py);
                // points on the edge of a bar: its own corners (one lies inside another where they overlap), and where a cut path crosses it
                if (edges.Any(c => (Math.Abs(x - c.Left) < 0.01 || Math.Abs(x - c.Right) < 0.01) && y >= c.Bottom - 0.01 && y <= c.Top + 0.01 ||
                                  (Math.Abs(y - c.Bottom) < 0.01 || Math.Abs(y - c.Top) < 0.01) && x >= c.Left - 0.01 && x <= c.Right + 0.01)) return null;
                foreach (var bar in bars)
                    if (x > bar.Left + seam && x < bar.Right - seam && y > bar.Bottom + seam && y < bar.Top - seam)
                        return $"Pfadpunkt ({x:0.#}, {y:0.#}) unter dem Balken ({bar.Left:0.#}, {bar.Bottom:0.#}) – ({bar.Right:0.#}, {bar.Top:0.#}), " +
                               $"{kind}, {count} Punkte, Breite {paint.LineWidth * matrix.MeanScale:0.##}" +
                               (Environment.GetEnvironmentVariable("DEBUGPFAD") is null ? "" : $" roh {string.Join(" ", Enumerable.Range(0, count).Select(j => $"({points[first + 2 * j]:0.###},{points[first + 2 * j + 1]:0.###})"))} Matrix {matrix}");
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// A painted path near the bars as the page shows it: one subpath as a fine polyline (page space), how far its stroke reaches, and what
    /// it is painted with — its filling and its stroke each on its own (colours, width, caps, joins, dash lengths, blending; not where the
    /// dashes start, which a cut piece changes): a path filled and stroked by one operator is cut as a fill followed by a stroke.
    /// </summary>
    private readonly record struct PaintedLine(List<(double X, double Y)> Points, double Reach, string[] Paints);

    /// <summary>
    /// The painted paths of a page (not characters) whose subpaths come near the bars, each subpath as a polyline in page space — straight
    /// pieces as they are, curves in steps of at most half a point (at most 4096), so that a point on the curve lies within a fiftieth of a
    /// point of the polyline wherever the curve bends less sharply than a radius of 1.6 pt.
    /// </summary>
    private static List<PaintedLine> PaintedLinesNear(PdfDocument doc, PdfPage page, List<PdfRectangle> bars)
    {
        var result = new List<PaintedLine>();
        var list = PageInterpreter.Build(doc, page, default, withoutOwnElements: true, tagGlyphs: true);
        var items = list.Items.Span;
        var tags = list.GlyphTags;
        for (var i = 0; i < items.Length; i++)
        {
            ref readonly var item = ref items[i];
            if (item.Path < 0 || item.Glyph || tags is not null && i < tags.Length && tags[i] >= 0) continue;
            if (item.Kind is not (DisplayItemKind.Fill or DisplayItemKind.Stroke or DisplayItemKind.FillStroke)) continue;
            var paint = list.Paints[item.Paint];
            var matrix = list.Matrices[item.Matrix];
            var reach = item.Kind == DisplayItemKind.Fill ? 0 : Math.Max(paint.LineWidth * matrix.MeanScale, 0) / 2;
            if (!double.IsFinite(reach)) continue;
            var scale = matrix.MeanScale;
            var fills = item.Kind != DisplayItemKind.Stroke;
            var strokes = item.Kind != DisplayItemKind.Fill;
            var dash = strokes && paint.Dash >= 0 && paint.Dash < list.Dashes.Length
                ? string.Join(" ", list.Dashes[paint.Dash].Lengths.Select(l => (l * scale).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))
                : "";
            string[] signature =
            [
                .. fills ? [FormattableString.Invariant($"Füllung {paint.Fill:X8}/{paint.FillShading >= 0}/{paint.BlendMode}/{paint.Overprint}")] : Array.Empty<string>(),
                .. strokes ? [FormattableString.Invariant($"Strich {paint.Stroke:X8}/{paint.StrokeShading >= 0}/{paint.LineWidth * scale:0.###}/{paint.Cap}/{paint.Join}/{dash}/{paint.BlendMode}/{paint.Overprint}")] : Array.Empty<string>(),
            ];
            var (verb, verbCount, first) = list.Paths[item.Path];
            var points = list.Points;
            List<(double X, double Y)>? line = null;
            (double X, double Y) start = default, current = default;
            void Finish()
            {
                if (line is not { Count: > 1 }) return;
                double l = line.Min(q => q.X), r = line.Max(q => q.X), b = line.Min(q => q.Y), t = line.Max(q => q.Y);
                var margin = reach + 2;
                if (bars.Any(bar => l < bar.Right + margin && r > bar.Left - margin && b < bar.Top + margin && t > bar.Bottom - margin))
                    result.Add(new PaintedLine(line, reach, signature));
            }
            var at = first;
            for (var v = verb; v < verb + verbCount && v < list.Verbs.Length; v++)
            {
                switch (list.Verbs[v])
                {
                    case PathVerb.Move when at + 1 < points.Length:
                        Finish();
                        start = current = matrix.Transform(points[at], points[at + 1]);
                        line = [current];
                        at += 2;
                        break;
                    case PathVerb.Line when at + 1 < points.Length:
                        current = matrix.Transform(points[at], points[at + 1]);
                        (line ??= [start]).Add(current);
                        at += 2;
                        break;
                    case PathVerb.Cubic when at + 5 < points.Length:
                    {
                        var p0 = current;
                        var p1 = matrix.Transform(points[at], points[at + 1]);
                        var p2 = matrix.Transform(points[at + 2], points[at + 3]);
                        var p3 = matrix.Transform(points[at + 4], points[at + 5]);
                        at += 6;
                        var hull = Distance(p0, p1) + Distance(p1, p2) + Distance(p2, p3);
                        var steps = double.IsFinite(hull) ? Math.Clamp((int)Math.Ceiling(hull / 0.5), 8, 4096) : 8;
                        line ??= [p0];
                        for (var k = 1; k <= steps; k++)
                        {
                            double t = (double)k / steps, u = 1 - t;
                            line.Add((u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                                u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y));
                        }
                        current = p3;
                        break;
                    }
                    case PathVerb.Close:
                        if (line is { Count: > 0 }) line.Add(start);
                        current = start;
                        break;
                }
            }
            Finish();
        }
        return result;

        static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    /// <summary>
    /// The painted paths of the copy near the bars, checked by their geometry and their paint: every point they pass through outside the
    /// bars (beyond the seam a cut leaves) lies on a painted path of the original near the bars, to a fiftieth of a point, and one painted
    /// alike (<see cref="PaintedLine.Paint"/>). A cut piece is an exact part of its path — the cutter splits curves, it does not draw them
    /// anew — yet a renderer breaks a shorter curve into lines, and approximates the edges of its stroke, at other places than for the whole
    /// one: a thin ring and two close strokes beside a bar changed by up to 57 levels in a few pixels, with the pieces on their curves to
    /// 0.00003 pt (DocLayNet e87c5622, 40eb69a0; docs/FEHLERGEDAECHTNIS.md, No. 875). So the pixels along the pieces that were cut are left
    /// out of the comparison (<see cref="Changed"/>), and what decides them — where they lie and how they are painted — is checked here,
    /// far finer than any pixel. Paths that stayed as they were are compared by their pixels as before.
    /// </summary>
    /// <returns>A problem — a point of the copy off the paths of the original, or painted otherwise — or null; and the pieces that were cut.</returns>
    private static (string? Problem, List<PaintedLine> Cut) CutPieces(PdfDocument original, PdfPage page, PdfDocument copy, PdfPage copyPage,
        List<PdfRectangle> bars)
    {
        var pieces = PaintedLinesNear(copy, copyPage, bars);
        if (pieces.Count == 0) return (null, pieces);
        var paths = PaintedLinesNear(original, page, bars);
        // the straight pieces of the original paths with their paint, sorted into cells of 4 pt for the nearest ones
        const double Cell = 4, Tolerance = 0.02;
        var cells = new Dictionary<(int, int), List<((double X, double Y) A, (double X, double Y) B, string[] Paints)>>();
        foreach (var path in paths)
            for (var k = 1; k < path.Points.Count; k++)
            {
                var (a, b) = (path.Points[k - 1], path.Points[k]);
                int x0 = (int)Math.Floor((Math.Min(a.X, b.X) - Tolerance) / Cell), x1 = (int)Math.Floor((Math.Max(a.X, b.X) + Tolerance) / Cell);
                int y0 = (int)Math.Floor((Math.Min(a.Y, b.Y) - Tolerance) / Cell), y1 = (int)Math.Floor((Math.Max(a.Y, b.Y) + Tolerance) / Cell);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1_000_000) continue;   // a degenerate piece across the world
                for (var cx = x0; cx <= x1; cx++)
                    for (var cy = y0; cy <= y1; cy++)
                    {
                        if (!cells.TryGetValue((cx, cy), out var list)) cells[(cx, cy)] = list = [];
                        list.Add((a, b, path.Paints));
                    }
            }
        // a piece the same as a path of the original (same points, same paint) was not cut: its pixels stay compared
        var unchanged = new HashSet<string>(paths.Select(Key));
        var cut = new List<PaintedLine>();
        foreach (var piece in pieces)
        {
            if (unchanged.Contains(Key(piece))) continue;
            var seam = 0.5 + piece.Reach + 0.1;
            foreach (var point in piece.Points)
            {
                if (bars.Any(bar => point.X > bar.Left - seam && point.X < bar.Right + seam && point.Y > bar.Bottom - seam && point.Y < bar.Top + seam))
                    continue;
                var nearest = double.MaxValue;
                var there = new HashSet<string>();   // how the original paints the paths through this point (its filling, its stroke)
                if (cells.TryGetValue(((int)Math.Floor(point.X / Cell), (int)Math.Floor(point.Y / Cell)), out var candidates))
                    foreach (var (a, b, paints) in candidates)
                    {
                        var d = SegmentDistance(point, a, b);
                        nearest = Math.Min(nearest, d);
                        if (d <= Tolerance) there.UnionWith(paints);
                    }
                if (nearest > Tolerance)
                    return ($"Pfadstück nicht auf dem Pfad des Originals: ({point.X:0.##}, {point.Y:0.##}) pt" +
                            (nearest < double.MaxValue ? $", {nearest:0.###} pt daneben" : ", kein Pfad in der Nähe"), cut);
                if (piece.Paints.FirstOrDefault(p => !there.Contains(p)) is { } other)
                    return ($"Pfadstück anders gemalt als sein Pfad im Original: ({point.X:0.##}, {point.Y:0.##}) pt, {other} statt {string.Join(" oder ", there.Take(3))}", cut);
            }
            cut.Add(piece);
        }
        return (null, cut);

        static string Key(PaintedLine line) =>
            string.Join("+", line.Paints) + ":" + string.Join(";", line.Points.Select(q => FormattableString.Invariant($"{q.X:0.####},{q.Y:0.####}")));

        static double SegmentDistance((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
            var t = length > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1) : 0;
            double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return Math.Sqrt(x * x + y * y);
        }
    }

    /// <summary>The images a page draws (also in forms and pattern cells): mapping of the unit square to the page, the image, its resources.</summary>
    private static List<(Matrix ToPage, Core.Pdf.Objects.PdfStream? Stream, InlineImage? Inline, Core.Pdf.Objects.PdfDictionary? Resources)> Images(PdfDocument doc, PdfPage page)
    {
        var collector = new ShapeCollector();
        PageInterpreter.Observe(doc, page, collector);
        return collector.Images;
    }

    /// <summary>
    /// Image data under a bar that is not blacked out: under each bar, all pixels of an image whose centre lies in it must be alike — colour
    /// and opacity within 48 levels (a JPEG packed again blurs a little). That also holds for a stencil, which paints its covered part in the
    /// fill colour, and it finds a soft mask that kept the shape of what was redacted. Image objects are read in full resolution, only the
    /// rows of the bars (<see cref="Redactor.Uniformity"/>, as the redaction checks itself); reduced to the display's size, a rest of two
    /// pixels of a large scan was averaged away, and a margin of 1 pt in from the edge of the bar was never looked at — redaction blacks out
    /// every pixel whose centre lies under the bar (docs/FEHLERGEDAECHTNIS.md, No. 448). Inline images (small) and images that cannot be read
    /// row by row: as the display decodes them. Null if all are alike.
    /// </summary>
    private static string? ImageUnderBar(PdfDocument doc, PdfPage page, List<PdfRectangle> bars)
    {
        foreach (var (toPage, stream, inline, resources) in Images(doc, page))
        {
            var bounds = toPage.TransformBounds(RectD.FromLtrb(0, 0, 1, 1));
            var hit = bars.Select(b => Shrink(b, 0.05)).Where(b => b.Right > b.Left && b.Top > b.Bottom &&
                bounds.Left < b.Right && bounds.Right > b.Left && bounds.Top < b.Top && bounds.Bottom > b.Bottom).ToList();
            if (hit.Count == 0) continue;
            var dict = stream?.Dictionary ?? inline!.Dictionary;
            var stencil = (dict.GetBoolean(Core.Pdf.Objects.PdfName.Get("ImageMask")) ?? dict.GetBoolean(Core.Pdf.Objects.PdfName.Get("IM"))) == true;
            if (stream is not null)
            {
                var areas = hit.Select(b => RectD.FromLtrb(b.Left, b.Bottom, b.Right, b.Top)).ToList();
                switch (Redactor.Uniformity(doc, stream, resources, stencil, toPage, areas))
                {
                    case true:
                        continue;
                    case false:
                        return $"Pixel eines Bildes ({dict.GetNumber(Core.Pdf.Objects.PdfName.Get("Width")):0} × {dict.GetNumber(Core.Pdf.Objects.PdfName.Get("Height")):0}, " +
                               $"volle Auflösung) unter einem Balken nicht einheitlich ({string.Join("; ", hit.Select(b => $"{b.Left:0.#}, {b.Bottom:0.#} – {b.Right:0.#}, {b.Top:0.#}"))})";
                }
            }
            if (PdfImageDecoder.Decode(new ImageSource(stream, inline, resources, stencil, 0xFF000000), doc) is not { } image) continue;
            var bpp = image.BytesPerPixel;
            var toImage = toPage.Invert();
            foreach (var bar in hit)
            {
                int[] low = [255, 255, 255, 255], high = [0, 0, 0, 0];
                var count = 0;
                // only the pixels within the bounds of the bar (large images: a few rows instead of every pixel)
                var inImage = toImage.TransformBounds(RectD.FromLtrb(bar.Left, bar.Bottom, bar.Right, bar.Top));
                var (x0, x1) = (Math.Max(0, (int)Math.Floor(inImage.Left * image.Width)), Math.Min(image.Width, (int)Math.Ceiling(inImage.Right * image.Width)));
                var (y0, y1) = (Math.Max(0, (int)Math.Floor((1 - inImage.Bottom) * image.Height)), Math.Min(image.Height, (int)Math.Ceiling((1 - inImage.Top) * image.Height)));
                for (var y = y0; y < y1; y++)
                    for (var x = x0; x < x1; x++)
                    {
                        var (px, py) = toPage.Transform((x + 0.5) / image.Width, 1 - (y + 0.5) / image.Height);
                        if (px < bar.Left || px > bar.Right || py < bar.Bottom || py > bar.Top) continue;
                        count++;
                        var at = (y * image.Width + x) * bpp;
                        for (var c = 0; c < bpp; c++)
                        {
                            low[c] = Math.Min(low[c], image.Pixels[at + c]);
                            high[c] = Math.Max(high[c], image.Pixels[at + c]);
                        }
                    }
                if (count < 2) continue;
                var spread = Enumerable.Range(0, bpp).Max(c => high[c] - low[c]);
                if (spread > 48)
                    return $"{count} Pixel eines Bildes ({image.Width} × {image.Height}, {image.Format}) unter dem Balken ({bar.Left:0.#}, {bar.Bottom:0.#}) – " +
                           $"({bar.Right:0.#}, {bar.Top:0.#}) nicht einheitlich (Spanne {spread})";
            }
        }
        return null;
    }

    /// <summary>
    /// Images under a bar that cannot be read: redaction drops them (nothing of them can be blacked out), the display drew a
    /// placeholder for them — their place may change.
    /// </summary>
    private static List<PdfRectangle> UnreadableImages(PdfDocument doc, PdfPage page, List<PdfRectangle> bars)
    {
        var result = new List<PdfRectangle>();
        foreach (var (toPage, stream, inline, resources) in Images(doc, page))
        {
            var bounds = toPage.TransformBounds(RectD.FromLtrb(0, 0, 1, 1));
            if (!bars.Any(b => bounds.Left < b.Right && bounds.Right > b.Left && bounds.Top < b.Top && bounds.Bottom > b.Bottom)) continue;
            var dict = stream?.Dictionary ?? inline!.Dictionary;
            var stencil = (dict.GetBoolean(Core.Pdf.Objects.PdfName.Get("ImageMask")) ?? dict.GetBoolean(Core.Pdf.Objects.PdfName.Get("IM"))) == true;
            if (PdfImageDecoder.Decode(new ImageSource(stream, inline, resources, stencil, 0xFF000000), doc) is null)
                result.Add(new PdfRectangle(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        }
        return result;
    }

    /// <summary>
    /// The seam beside each bar on an image it touches: every image pixel the bar merely touches goes black (also its part beside the
    /// bar), and a reader smoothing the image spreads that by another pixel. Fine images keep their pixels (at most one point), coarse
    /// ones are refined to <see cref="ImageBlanker.Tolerance"/> first — so the seam is two of their pixels, within the image. The areas of
    /// refined images go into <paramref name="refined"/>: a reader averages the refined copy where it drew the original's blocks.
    /// </summary>
    private static List<PdfRectangle> ImageSeams(PdfDocument doc, PdfPage page, List<PdfRectangle> bars, List<PdfRectangle> refined)
    {
        var result = new List<PdfRectangle>();
        foreach (var (toPage, stream, inline, _) in Images(doc, page))
        {
            var bounds = toPage.TransformBounds(RectD.FromLtrb(0, 0, 1, 1));
            var dict = stream?.Dictionary ?? inline!.Dictionary;
            var w = (int)(dict.GetNumber(Core.Pdf.Objects.PdfName.Get("Width")) ?? dict.GetNumber(Core.Pdf.Objects.PdfName.Get("W")) ?? 0);
            var h = (int)(dict.GetNumber(Core.Pdf.Objects.PdfName.Get("Height")) ?? dict.GetNumber(Core.Pdf.Objects.PdfName.Get("H")) ?? 0);
            if (w <= 0 || h <= 0) continue;
            var (px, py) = ImageBlanker.PixelSize(toPage, w, h);
            var coarse = Math.Max(px, py) > ImageBlanker.RefineAbove;
            var pixel = coarse ? ImageBlanker.Tolerance : Math.Max(px, py);
            foreach (var bar in bars)
            {
                if (!(bounds.Left < bar.Right && bounds.Right > bar.Left && bounds.Top < bar.Top && bounds.Bottom > bar.Bottom)) continue;
                if (coarse) refined.Add(new PdfRectangle(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
                var seam = 2 * pixel;
                result.Add(new PdfRectangle(Math.Max(bounds.Left, bar.Left - seam), Math.Max(bounds.Top, bar.Bottom - seam),
                    Math.Min(bounds.Right, bar.Right + seam), Math.Min(bounds.Bottom, bar.Top + seam)));
            }
        }
        return result;
    }

    private sealed class ShapeCollector(Dictionary<int, RectD>? drawn = null) : IContentObserver
    {
        private int _serial = -1;

        public void GlyphSerial(int serial) => _serial = serial;

        private bool _draws = true;

        public void GlyphDraws(bool draws) => _draws = draws;

        public List<(string Text, double X, double Y, PdfRectangle Ink)> Shapes { get; } = [];

        public List<(Matrix ToPage, Core.Pdf.Objects.PdfStream? Stream, InlineImage? Inline, Core.Pdf.Objects.PdfDictionary? Resources)> Images { get; } = [];

        // characters in the cell of a pattern count as well (redaction looks into cells under bars)
        public bool WantsPatternCell(RectD painted) => true;

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize, double horizontalScale,
            bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible)
        {
            // a space carries nothing only if it draws nothing (as the redaction's own check, docs/FEHLERGEDAECHTNIS.md, No. 438)
            if (Redactor.CarriesNothing(text, _draws, invisible) || !cellToPage.IsInvertible) return;
            // a character no search can read (no ToUnicode) counts as well: under a bar it must go as any other, and the rest of its line may
            // move (29.10.2026: a page of such text was reported changed beside the bar, its lines unknown to the check)
            text ??= "�";
            var b = cellToPage.TransformBounds(hidden || invisible ? cell : ink);
            var shape = cellToPage.TransformBounds(ink);
            if (drawn is not null && drawn.TryGetValue(_serial, out var painted)) shape = shape.Union(painted);
            Shapes.Add((text, (b.Left + b.Right) / 2, (b.Top + b.Bottom) / 2, new PdfRectangle(shape.Left, shape.Top, shape.Right, shape.Bottom)));
        }

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void Image(int op, Matrix imageToPage, Core.Pdf.Objects.PdfStream? stream, InlineImage? inline, Core.Pdf.Objects.PdfDictionary? resources, bool hidden)
        {
            if (Images.Count < 2000) Images.Add((imageToPage, stream, inline, resources));
        }

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void BeginForm(int op, Core.Pdf.Objects.PdfStream form, Matrix formToPage, bool hidden) { }

        public void BeginSoftMask(int op, Core.Pdf.Objects.PdfStream group, Matrix groupToPage) { }

        public void EndForm() { }
    }

    /// <summary>Fraction of the rectangle under the bars (overlaps of bars count more than once, at most 1).</summary>
    private static double Covered(PdfRectangle r, List<PdfRectangle> bars)
    {
        var area = Math.Max((r.Right - r.Left) * (r.Top - r.Bottom), 1e-6);
        var covered = bars.Sum(b => Math.Max(0, Math.Min(r.Right, b.Right) - Math.Max(r.Left, b.Left)) * Math.Max(0, Math.Min(r.Top, b.Top) - Math.Max(r.Bottom, b.Bottom)));
        return Math.Min(1, covered / area);
    }

    private static PdfRectangle Inflate(PdfRectangle r, double d) => new(r.Left - d, r.Bottom - d, r.Right + d, r.Top + d);

    private static PdfRectangle Shrink(PdfRectangle r, double d) => Inflate(r, -d);

    private static string Short(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length > 30 ? line[..30] + " …" : line;
    }

    /// <summary>
    /// Characters outside the marked areas that are missing afterwards or moved by more than the rounded gap, and the bands of the lines
    /// in which characters went (the rest of such a line may move by the gap).
    /// </summary>
    /// <param name="hideLengths">The lengths of redacted words were hidden: on a line that lost characters the rest may move along it to
    /// the right by up to the step (<see cref="Redactor.LengthStepEm"/>, the em at most 1.6 times the drawn height; allowed: that much and a
    /// little more of the height of the character's box).</param>
    private static (List<TextGlyph> Missing, List<PdfRectangle> Bands) Lost(TextGlyph[] before, TextGlyph[] after, List<PdfRectangle> bars, List<PdfRectangle> hidden,
        List<PdfRectangle> dropped, bool hideLengths = false)
    {
        var pool = after.Where(g => g.Text.Trim().Length > 0).GroupBy(g => g.Text).ToDictionary(k => k.Key, k => k.ToList());
        var missing = new List<TextGlyph>();
        var bands = new List<PdfRectangle>();
        bool RemovedHere(TextGlyph g) => bars.Any(b => Inside(Inflate(b, 0.5), g)) || hidden.Any(h => Inside(Inflate(h, 0.5), g));
        // the lines that lost characters (middle heights), known before matching: their rest may have moved further
        double Height(TextGlyph g) => Math.Max(Math.Max(g.Y0, g.Y1), Math.Max(g.Y2, g.Y3)) - Math.Min(Math.Min(g.Y0, g.Y1), Math.Min(g.Y2, g.Y3));
        // how far a character may lie from its place: the rounded gap, and inside a word the spacing rounded to 1/100 em (half of it either
        // way, the em about the size of the character — at 75 pt that alone is 0.38 pt)
        double Near(TextGlyph g) => Shift + 0.05 + 0.006 * g.Size;
        // lines in their own direction: along and across the line — a vertical or turned line moves up or down the page (docs/FEHLERGEDAECHTNIS.md,
        // No. 485: words of a label along an axis moved by the rounded gap and counted as shifted)
        double Width(TextGlyph g) => Math.Max(Math.Max(g.X0, g.X1), Math.Max(g.X2, g.X3)) - Math.Min(Math.Min(g.X0, g.X1), Math.Min(g.X2, g.X3));
        bool Level(TextGlyph g) => !g.Vertical && Math.Abs(g.X1 - g.X0) >= Math.Abs(g.Y1 - g.Y0);
        (double Along, double Across) Frame(TextGlyph g) => Center(g) is var (x, y) && Level(g) ? (x, y) : (y, x);
        double Thickness(TextGlyph g) => Level(g) ? Height(g) : Width(g);
        var lossLines = hideLengths
            // (a character the bar only touches counts as well: the writer moves its line too — docs/FEHLERGEDAECHTNIS.md, No. 485)
            ? before.Where(g => g.Text.Trim().Length > 0 && (RemovedHere(g) || bars.Any(b => Overlaps(b, g))))
                .Select(g => (Across: Frame(g).Across, Thick: Thickness(g), Level: Level(g))).Distinct().ToList()
            : [];
        // on such a line: within the height of the larger character (a raised index belongs to its line)
        int LineOf(TextGlyph g)
        {
            var across = Frame(g).Across;
            for (var i = 0; i < lossLines.Count; i++)
                if (lossLines[i].Level == Level(g) && Math.Abs(lossLines[i].Across - across) < Math.Max(Thickness(g), lossLines[i].Thick)) return i;
            return -1;
        }
        var moved = new List<TextGlyph>();   // on such a line: matched there in the order of the line (it keeps its order, its words move right)
        foreach (var g in before)
        {
            if (g.Text.Trim().Length == 0) continue;
            if (hideLengths && !RemovedHere(g) && LineOf(g) >= 0)
            {
                moved.Add(g);
                continue;
            }
            if (RemovedHere(g))
            {
                // the rest of this line may have moved by the rounded gap
                double low = Math.Min(Math.Min(g.Y0, g.Y1), Math.Min(g.Y2, g.Y3)), high = Math.Max(Math.Max(g.Y0, g.Y1), Math.Max(g.Y2, g.Y3));
                double left = Math.Min(Math.Min(g.X0, g.X1), Math.Min(g.X2, g.X3)), right = Math.Max(Math.Max(g.X0, g.X1), Math.Max(g.X2, g.X3));
                // along the baseline: across the page for horizontal text, up and down for vertical or turned text
                var along = Math.Abs(g.X1 - g.X0) >= Math.Abs(g.Y1 - g.Y0);
                bands.Add(along && !g.Vertical ? new PdfRectangle(-1e6, low - 2, 1e6, high + 2) : new PdfRectangle(left - 2, -1e6, right + 2, 1e6));
                continue;
            }
            // along and across its own line (a turned line moves up or down the page by the rounded word start: No. 485)
            var (x, y) = Frame(g);
            if (pool.TryGetValue(g.Text, out var candidates))
            {
                var near = Near(g);
                var at = candidates.FindIndex(c => Level(c) == Level(g) && Frame(c).Along - x >= -near && Frame(c).Along - x <= near + ReadersShift && Math.Abs(Frame(c).Across - y) <= near);
                if (at >= 0)
                {
                    candidates.RemoveAt(at);
                    continue;
                }
            }
            Gone(g);
        }
        // the lines that lost characters keep their order, their words move to the right by up to the step (2 em; allowed: that much of the height
        // of a character's box) or by the rounding of the gap — in a justified line set anew also to the left, as far (Fehlergedächtnis 356):
        // along the line each character takes the leftmost one left over in that window — first those clear of the bars, then those a bar
        // touches (they may have gone with it)
        foreach (var g in moved.OrderBy(g => bars.Any(b => Overlaps(b, g)) ? 1 : 0).ThenBy(g => Frame(g).Along))
        {
            var (x, y) = Frame(g);
            var at = -1;
            // the step is that of the redacted text of the line: an index moves with its line as far as the letters (its own box is smaller)
            var line = LineOf(g);
            // the step (Redactor.LengthStepEm), the em at most 1.6 times the height of what is drawn (fonts with small letters in a large em,
            // Fehlergedächtnis 263): up to step × 1.6 times that height
            var reach = (Redactor.LengthStepEm * 1.6 + 0.1) * Math.Max(Thickness(g), line >= 0 ? lossLines[line].Thick : 0);
            if (pool.TryGetValue(g.Text, out var candidates))
                for (var k = 0; k < candidates.Count; k++)
                {
                    if (Level(candidates[k]) != Level(g)) continue;
                    var (cx, cy) = Frame(candidates[k]);
                    var near = Near(g);
                    if (cx - x < -near - reach || cx - x > near + reach || Math.Abs(cy - y) > near) continue;
                    if (at < 0 || cx < Frame(candidates[at]).Along) at = k;
                }
            if (at >= 0) candidates!.RemoveAt(at);
            else Gone(g);
        }
        // a band across the page reaches over the whole line, not only over the redacted characters: a small redacted index moved the
        // tall letters of its line too, and their tops counted as changed (29.10.2026)
        for (var i = 0; i < bands.Count; i++)
        {
            var band = bands[i];
            if (band.Left > -1e5) continue;   // a band up and down the page (vertical or turned text) stays
            double low = band.Bottom, high = band.Top;
            foreach (var g in before)
            {
                var (_, cy) = Center(g);
                if (cy < band.Bottom || cy > band.Top || g.Text.Trim().Length == 0) continue;
                low = Math.Min(low, Math.Min(Math.Min(g.Y0, g.Y1), Math.Min(g.Y2, g.Y3)) - 2);
                high = Math.Max(high, Math.Max(Math.Max(g.Y0, g.Y1), Math.Max(g.Y2, g.Y3)) + 2);
            }
            bands[i] = new PdfRectangle(band.Left, low, band.Right, high);
        }
        return (missing, bands);

        void Gone(TextGlyph g)
        {
            // a character half under a bar (its centre outside) may go with it, and the text of an annotation the bar touched
            if (bars.Any(b => Overlaps(b, g)) || dropped.Any(d => Inside(Inflate(d, 1), g))) return;
            missing.Add(g);
        }
    }

    private static bool Overlaps(PdfRectangle r, TextGlyph g)
    {
        double left = Math.Min(Math.Min(g.X0, g.X1), Math.Min(g.X2, g.X3)), right = Math.Max(Math.Max(g.X0, g.X1), Math.Max(g.X2, g.X3));
        double low = Math.Min(Math.Min(g.Y0, g.Y1), Math.Min(g.Y2, g.Y3)), high = Math.Max(Math.Max(g.Y0, g.Y1), Math.Max(g.Y2, g.Y3));
        return left < r.Right && right > r.Left && low < r.Top && high > r.Bottom;
    }

    /// <summary>Whether every bar is painted black over its whole area (1 pt in from its edge); null if so, else where not.</summary>
    private static string? Unpainted(PdfDocument doc, PdfPage page, List<PdfRectangle> bars)
    {
        using var image = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, Scale, new ImageCache(64L << 20));
        var toDevice = PageGeometry.PageToDevice(page, Scale);
        var pixels = image.GetPixelSpan();
        foreach (var bar in bars)
        {
            if (bar.Width < 3 || bar.Height < 3) continue;
            var d = toDevice.TransformBounds(RectD.FromLtrb(bar.Left + 1, bar.Bottom + 1, bar.Right - 1, bar.Top - 1));
            int x0 = Math.Max(0, (int)Math.Ceiling(d.Left)), x1 = Math.Min(image.Width, (int)Math.Floor(d.Right));
            int y0 = Math.Max(0, (int)Math.Ceiling(d.Top)), y1 = Math.Min(image.Height, (int)Math.Floor(d.Bottom));
            int light = 0, all = 0;
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var i = y * image.RowBytes + x * 4;
                    all++;
                    if (pixels[i] > 60 || pixels[i + 1] > 60 || pixels[i + 2] > 60) light++;
                }
            if (all > 0 && light > all / 50) return $"{light} von {all} Pixeln hell bei ({x0 / Scale:0}, {y0 / Scale:0}) pt";
        }
        return null;
    }

    /// <summary>
    /// The annotations a bar touches on any page and the form fields they belong to: a field goes with all its widgets, also those on pages
    /// without a bar — its value is one and the same everywhere, and a widget left elsewhere would still show what was redacted.
    /// </summary>
    private static HashSet<Core.Pdf.Objects.PdfDictionary> TouchedAcrossPages(PdfDocument doc, Dictionary<int, List<PdfRectangle>> bars)
    {
        var touched = new HashSet<Core.Pdf.Objects.PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var (p, list) in bars)
        {
            if ((uint)p >= (uint)doc.PageCount || doc.Pages[p].Dictionary.Get(Core.Pdf.Objects.PdfName.Get("Annots")) is not Core.Pdf.Objects.PdfArray annots) continue;
            for (var i = 0; i < annots.Count && i < 10_000; i++)
            {
                if (annots.Get(i) is not Core.Pdf.Objects.PdfDictionary annot || AnnotationAppearance.DrawnBounds(annot, doc.Pages[p].Rotate) is not { } r) continue;
                if (!list.Any(b => r.Left <= b.Right && r.Right >= b.Left && r.Bottom <= b.Top && r.Top >= b.Bottom)) continue;
                touched.Add(annot);
                if (FieldOf(annot) is { } field) touched.Add(field);
            }
        }
        return touched;
    }

    /// <summary>The terminal field of a widget: itself if it has a name, else the first parent with one (at most 32 levels).</summary>
    private static Core.Pdf.Objects.PdfDictionary? FieldOf(Core.Pdf.Objects.PdfDictionary annot)
    {
        var t = Core.Pdf.Objects.PdfName.Get("T");
        var node = annot;
        for (var depth = 0; node is not null && depth < 32; depth++, node = node.Get(Core.Pdf.Objects.PdfName.Parent) as Core.Pdf.Objects.PdfDictionary)
            if (node.ContainsKey(t)) return node;
        return null;
    }

    private static bool Gone(Core.Pdf.Objects.PdfDictionary annot, PdfRectangle r, List<PdfRectangle> bars, HashSet<Core.Pdf.Objects.PdfDictionary> touched) =>
        bars.Any(b => r.Left <= b.Right && r.Right >= b.Left && r.Bottom <= b.Top && r.Top >= b.Bottom) || touched.Contains(annot) ||
        FieldOf(annot) is { } field && touched.Contains(field);

    /// <summary>Where the annotations a bar touches draw (or those of a field redacted on another page), and the rectangles of their pop-ups (they are removed as a whole).</summary>
    private static List<PdfRectangle> DroppedAnnotations(PdfPage page, List<PdfRectangle> bars, HashSet<Core.Pdf.Objects.PdfDictionary> touched)
    {
        var result = new List<PdfRectangle>();
        if (bars.Count == 0 && touched.Count == 0 || page.Dictionary.Get(Core.Pdf.Objects.PdfName.Get("Annots")) is not Core.Pdf.Objects.PdfArray annots) return result;
        for (var i = 0; i < annots.Count && i < 10_000; i++)
        {
            if (annots.Get(i) is not Core.Pdf.Objects.PdfDictionary annot || AnnotationAppearance.DrawnBounds(annot, page.Rotate) is not { } r) continue;
            if (!Gone(annot, r, bars, touched)) continue;
            result.Add(r);
            if (annot.Get(Core.Pdf.Objects.PdfName.Get("Popup")) is Core.Pdf.Objects.PdfDictionary popup && PdfRectangle.From(popup.Get(Core.Pdf.Objects.PdfName.Get("Rect"))) is { } pr)
                result.Add(pr);
        }
        return result;
    }

    /// <summary>The page without the annotations a bar touches (or of a field redacted on another page) and their pop-ups (as redaction drops them).</summary>
    private static PdfPage WithoutAnnotations(PdfPage page, List<PdfRectangle> bars, HashSet<Core.Pdf.Objects.PdfDictionary> touched)
    {
        var annotsName = Core.Pdf.Objects.PdfName.Get("Annots");
        if (bars.Count == 0 && touched.Count == 0 || page.Dictionary.Get(annotsName) is not Core.Pdf.Objects.PdfArray annots) return page;
        var gone = new HashSet<Core.Pdf.Objects.PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < annots.Count && i < 10_000; i++)
        {
            if (annots.Get(i) is not Core.Pdf.Objects.PdfDictionary annot || AnnotationAppearance.DrawnBounds(annot, page.Rotate) is not { } r) continue;
            if (!Gone(annot, r, bars, touched)) continue;
            gone.Add(annot);
            if (annot.Get(Core.Pdf.Objects.PdfName.Get("Popup")) is Core.Pdf.Objects.PdfDictionary popup) gone.Add(popup);
        }
        if (gone.Count == 0) return page;
        var kept = new Core.Pdf.Objects.PdfArray { Resolver = annots.Resolver };
        for (var i = 0; i < annots.Count; i++)
            if (!(annots.Get(i) is Core.Pdf.Objects.PdfDictionary a && gone.Contains(a))) kept.Add(annots[i]);
        var dict = new Core.Pdf.Objects.PdfDictionary { Resolver = page.Dictionary.Resolver };
        foreach (var (key, value) in page.Dictionary.Entries) dict.Set(key, key == annotsName ? kept : value);
        return new PdfPage(page.Index, dict, page.Id, page.Resources, page.MediaBox, page.CropBox, page.Rotate);
    }

    /// <summary>Where the page looks different outside the bars and the bands; null if nowhere.</summary>
    /// <param name="soft">Areas where averaging may soften edges (refined images): a value only has to lie within its neighbours before.</param>
    /// <param name="cutPaths">Pieces of cut paths whose place and paint were checked (<see cref="CutPieces"/>): within their reach and 1.5
    /// pixels around them, left out — the renderer draws a shorter curve and the edges of its stroke a little otherwise.</param>
    private static string? Changed(PdfDocument a, PdfPage pa, PdfDocument b, PdfPage pb, bool print, List<PdfRectangle> bars, List<PdfRectangle> bands,
        List<PdfRectangle> soft, List<PaintedLine>? cutPaths = null)
    {
        using var first = SkiaRenderer.RenderPage(PageInterpreter.Build(a, pa, forPrint: print), a, pa, Scale, new ImageCache(128L << 20));
        using var second = SkiaRenderer.RenderPage(PageInterpreter.Build(b, pb, forPrint: print), b, pb, Scale, new ImageCache(128L << 20));
        if (first.Width != second.Width || first.Height != second.Height) return "andere Größe";
        var toDevice = PageGeometry.PageToDevice(pa, Scale);
        var skip = new bool[first.Width * first.Height];
        foreach (var r in bars.Select(r => Inflate(r, 2)).Concat(bands))
        {
            var d = toDevice.TransformBounds(RectD.FromLtrb(Math.Max(r.Left, -1e5), Math.Max(r.Bottom, -1e5), Math.Min(r.Right, 1e5), Math.Min(r.Top, 1e5)));
            int x0 = Math.Max(0, (int)Math.Floor(d.Left) - 1), x1 = Math.Min(first.Width, (int)Math.Ceiling(d.Right) + 1);
            int y0 = Math.Max(0, (int)Math.Floor(d.Top) - 1), y1 = Math.Min(first.Height, (int)Math.Ceiling(d.Bottom) + 1);
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++) skip[y * first.Width + x] = true;
        }
        var softened = new bool[first.Width * first.Height];
        foreach (var r in soft)
        {
            var d = toDevice.TransformBounds(RectD.FromLtrb(r.Left, r.Bottom, r.Right, r.Top));
            for (var y = Math.Max(0, (int)Math.Floor(d.Top)); y < Math.Min(first.Height, (int)Math.Ceiling(d.Bottom)); y++)
                for (var x = Math.Max(0, (int)Math.Floor(d.Left)); x < Math.Min(first.Width, (int)Math.Ceiling(d.Right)); x++) softened[y * first.Width + x] = true;
        }
        // along the checked pieces: every half pixel of each straight piece, the square of its reach and 1.5 pixels around it
        foreach (var path in cutPaths ?? [])
        {
            var radius = (int)Math.Ceiling(path.Reach * Scale + 1.5);
            for (var k = 1; k < path.Points.Count; k++)
            {
                var (ax, ay) = toDevice.Transform(path.Points[k - 1].X, path.Points[k - 1].Y);
                var (bx, by) = toDevice.Transform(path.Points[k].X, path.Points[k].Y);
                var steps = (int)Math.Clamp(Math.Ceiling(Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay)) * 2), 1, 1 << 16);
                for (var s = 0; s <= steps; s++)
                {
                    int cx = (int)(ax + (bx - ax) * s / steps), cy = (int)(ay + (by - ay) * s / steps);
                    if (cx < -radius || cy < -radius || cx >= first.Width + radius || cy >= first.Height + radius) continue;
                    for (var y = Math.Max(0, cy - radius); y <= Math.Min(first.Height - 1, cy + radius); y++)
                        for (var x = Math.Max(0, cx - radius); x <= Math.Min(first.Width - 1, cx + radius); x++) skip[y * first.Width + x] = true;
                }
            }
        }
        var p1 = first.GetPixelSpan();
        var p2 = second.GetPixelSpan();
        int count = 0, most = 0, mx = 0, my = 0;
        // PDFKNEAD_DIFFBILD=<ordner>: the differences that count, red on the result (to find them by eye)
        var marks = Environment.GetEnvironmentVariable("PDFKNEAD_DIFFBILD") is { Length: > 0 } ? new List<(int X, int Y)>() : null;
        for (var y = 0; y < first.Height; y++)
            for (var x = 0; x < first.Width; x++)
            {
                if (skip[y * first.Width + x]) continue;
                int i = y * first.RowBytes + x * 4, j = y * second.RowBytes + x * 4;
                var diff = Math.Max(Math.Abs(p1[i] - p2[j]), Math.Max(Math.Abs(p1[i + 1] - p2[j + 1]), Math.Abs(p1[i + 2] - p2[j + 2])));
                if (diff < Threshold) continue;
                // Smoothing of edges only: each value lies within its neighbours in the other picture. Drawing under any clip changes
                // the smoothing of some edge pixels (by up to 85 levels where strokes meet), without anything looking different
                if (Near(p1, first.RowBytes, x, y, first.Width, first.Height, p2, j) &&
                    (softened[y * first.Width + x] || Near(p2, second.RowBytes, x, y, first.Width, first.Height, p1, i))) continue;
                if (count++ == 0) (mx, my) = (x, y);
                most = Math.Max(most, diff);
                marks?.Add((x, y));
            }
        if (marks is { Count: > 3 })
        {
            using var shown = second.Copy();
            foreach (var (x, y) in marks)
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                        if ((uint)(x + dx) < (uint)shown.Width && (uint)(y + dy) < (uint)shown.Height) shown.SetPixel(x + dx, y + dy, SkiaSharp.SKColors.Red);
            var folder = Environment.GetEnvironmentVariable("PDFKNEAD_DIFFBILD")!;
            Directory.CreateDirectory(folder);
            using var png = shown.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(folder, $"unterschied-s{pa.Index + 1}{(print ? "-druck" : "")}.png"), png.ToArray());
        }
        return count > 3 ? $"{count} Pixel bis {most} Stufen, zuerst bei ({mx / Scale:0}, {my / Scale:0}) pt (von oben links)" : null;
    }

    /// <summary>
    /// Whether the colour at <paramref name="at"/> in <paramref name="other"/> lies, per channel, within the range of the 3 × 3 pixels
    /// around (x, y) in <paramref name="pixels"/> — as a smoothed edge pixel between its neighbours does. Something missing, added or of
    /// another colour lies outside it, also a line one pixel wide.
    /// </summary>
    private static bool Near(ReadOnlySpan<byte> pixels, int rowBytes, int x, int y, int width, int height, ReadOnlySpan<byte> other, int at)
    {
        for (var c = 0; c < 3; c++)
        {
            int low = 255, high = 0;
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) continue;
                    var v = pixels[ny * rowBytes + nx * 4 + c];
                    low = Math.Min(low, v);
                    high = Math.Max(high, v);
                }
            if (other[at + c] < low - Threshold || other[at + c] > high + Threshold) return false;
        }
        return true;
    }

    /// <summary>
    /// A case from a real file: on up to <paramref name="maxPages"/> pages, bars over a few words (as „Text schwärzen“ marks them) and,
    /// sometimes, over an area, and all hidden text marked. Words that occur once in the document and nowhere outside the pages must go;
    /// other unique words must stay. Null if the file has no text to redact.
    /// </summary>
    /// <summary>The case of an expectation file (see <see cref="Check"/>): "weg"/"bleibt" lines, "balken" lines, "verborgen".</summary>
    /// <summary>
    /// Only the bars of the cases (--nur-balken): no marks over hidden content, and of the words that must go only those under a bar —
    /// to compare with other programs, which know no such marks (tools/vergleich, 29.10.2026).
    /// </summary>
    internal static bool OnlyBars { get; set; }

    /// <summary>
    /// Redact as the program does by default (KONZEPT.md, section 5.6, „Schwärzen-Umbau“): what nobody sees, hidden layers, hidden parts of
    /// images and the old text recognition go along, in the whole document (`--verborgenes`).
    /// </summary>
    internal static bool CleanHidden { get; set; }

    /// <summary>
    /// After redacting, recognise the text anew on the pages whose old text recognition went, as „Neue Texterkennung anlegen“ does
    /// (KONZEPT.md, section 5.6, step c; `--neue-ocr <models>`): the rules then also hold for the new layer — no redacted word may come back.
    /// Null: no new text recognition.
    /// </summary>
    internal static PdfKnead.Core.Ocr.ITextRecognizer? NewOcr { get; set; }

    /// <summary>
    /// Redact with reasons (KONZEPT.md, section 5.6, B-14; `--gruende`): a reason for all marks, written into the bars and onto protocol pages after
    /// the last page — the rules hold all the same, and no redacted word may stand in the protocol (the leak search covers the whole file).
    /// </summary>
    internal static bool Reasons { get; set; }

    /// <summary>
    /// Generated cases tagged, with a structure tree whose elements carry descriptions (`--struktur`; docs/FEHLERGEDAECHTNIS.md, No. 618): on a
    /// page with a bar every description must go, also that of a parent reaching the page only through its kids, elsewhere they stay.
    /// </summary>
    internal static bool Structure { get; set; }

    /// <summary>
    /// The redaction with reasons against the one without: the same pages plus the protocol after them; outside the bars every page looks the
    /// same (the reasons lie in the bars, the notes beside them are not printed); no redacted word in the file — not in the protocol, the
    /// notes or as text in a bar.
    /// </summary>
    private static List<string> CheckReasons(byte[] plain, byte[] reasoned, RedactionCase test, string? password)
    {
        var problems = new List<string>();
        using var a = PdfDocument.Open(plain, password);
        using var b = PdfDocument.Open(reasoned, password);
        if (b.PageCount <= a.PageCount) problems.Add($"Gründe: keine Protokollseite ({a.PageCount} → {b.PageCount} Seiten)");
        for (var p = 0; p < a.PageCount && p < b.PageCount; p++)
        {
            var bars = test.Bars.GetValueOrDefault(p) ?? [];
            if (Changed(a, a.Pages[p], b, b.Pages[p], print: true, [.. bars], [], []) is { } c) problems.Add($"Gründe: S. {p + 1} (Druck) verändert außerhalb der Balken, {c}");
        }
        var protocol = string.Join("\n", Enumerable.Range(a.PageCount, Math.Max(0, b.PageCount - a.PageCount))
            .Select(p => PageText.Create(PageInterpreter.BuildText(b, b.Pages[p]), 0).Text));
        if (!protocol.Contains(Core.Localization.Tr.T("Schwärzungsprotokoll"), StringComparison.Ordinal)) problems.Add("Gründe: Protokoll ohne Titel");
        foreach (var e in test.Expectations.Where(e => e.Fate == Fate.Gone))
            if (LeakSearch.Find(reasoned, [e.Word], password, out _).Count > 0 && LeakSearch.Find(plain, [e.Word], password, out _).Count == 0)
                problems.Add($"Gründe: „{e.Word}“ steht in Protokoll, Notiz oder Balken");
        return problems;
    }

    /// <summary>The new text layers of the pages that had invisible text before (with <see cref="CleanHidden"/> it went in the whole document).</summary>
    private static Dictionary<int, PdfStream> NewTextLayers(PdfDocument original, PageItem[] before, PageItem[] redacted, PdfKnead.Core.Ocr.ITextRecognizer ocr)
    {
        var layers = new Dictionary<int, PdfStream>();
        var images = new ImageCache(64L << 20);
        foreach (var p in DocumentCleanup.PagesWithInvisibleText(before))
            if (PageRecognition.Recognize(redacted[p], ocr, images) is var (lines, box) && PdfKnead.Core.Ocr.OcrTextLayer.Form(lines, box) is { } form)
                layers[p] = form;
        return layers;
    }

    internal static RedactionCase FromSidecar(string name, byte[] data, string[] lines)
    {
        var bars = new Dictionary<int, List<PdfRectangle>>();
        var expectations = new List<Expectation>();
        var hidden = false;
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            switch (parts[0])
            {
                case "weg" or "bleibt" when parts.Length >= 3:
                    expectations.Add(new Expectation(int.Parse(parts[1], CultureInfo.InvariantCulture) - 1, parts[2], parts[0] == "weg" ? Fate.Gone : Fate.Stays));
                    break;
                case "balken" when parts.Length >= 3:
                    var v = parts[2].Split(' ').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    var page = int.Parse(parts[1], CultureInfo.InvariantCulture) - 1;
                    if (!bars.TryGetValue(page, out var list)) bars[page] = list = [];
                    list.Add(new PdfRectangle(v[0], v[1], v[2], v[3]));
                    break;
                case "verborgen" when !OnlyBars:
                    hidden = true;
                    break;
            }
        }
        if (OnlyBars) expectations = UnderBars(data, bars, expectations);
        return new RedactionCase(name, data, bars, hidden, expectations, "aus Erwartungsdatei");
    }

    /// <summary>The expectations without words that must go but lie under no bar (they went by the marks over hidden content).</summary>
    private static List<Expectation> UnderBars(byte[] data, Dictionary<int, List<PdfRectangle>> bars, List<Expectation> expectations)
    {
        using var doc = PdfDocument.Open(data);
        var texts = new Dictionary<int, PageText>();
        return [.. expectations.Where(e =>
        {
            if (e.Fate != Fate.Gone) return true;
            if (e.Page >= doc.PageCount || !bars.TryGetValue(e.Page, out var onPage)) return false;
            if (!texts.TryGetValue(e.Page, out var text)) texts[e.Page] = text = PageText.Create(PageInterpreter.Build(doc, doc.Pages[e.Page]).Text);
            return text.Find(e.Word, matchCase: true).Any(hit => text.Quads(hit).Any(q =>
            {
                var b = q.Bounds;
                double cx = (b.Left + b.Right) / 2, cy = (b.Top + b.Bottom) / 2;
                return onPage.Any(r => cx >= r.Left && cx <= r.Right && cy >= r.Bottom && cy <= r.Top);
            }));
        })];
    }

    private static RedactionCase? FromFile(string file, string? password, int maxPages)
    {
        var data = File.ReadAllBytes(file);
        // a case written by --ablegen beside the file: its bars and words instead of a new choice (to narrow a finding down)
        var sidecar = Path.Combine(Path.GetDirectoryName(file) ?? ".", Path.GetFileNameWithoutExtension(file) + "-erwartung.txt");
        if (File.Exists(sidecar)) return FromSidecar(Path.GetFileNameWithoutExtension(file), data, File.ReadAllLines(sidecar));
        using var doc = PdfDocument.Open(data, password);
        if (doc.PageCount == 0) return null;
        // the same choice on every run (string hashes differ between processes)
        var rng = new Random(Path.GetFileName(file).Aggregate(17, (h, c) => unchecked(h * 31 + c)) & 0x7FFFFFFF);
        var texts = new PageText[doc.PageCount];
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var p = 0; p < doc.PageCount; p++)
        {
            texts[p] = PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[p]));
            foreach (var w in Words(texts[p].Text)) counts[w] = counts.GetValueOrDefault(w) + 1;
            // a word divided at the end of a line counts as well (other programs join it again): „inde-“ / „pendence“
            foreach (var w in Words(System.Text.RegularExpressions.Regex.Replace(texts[p].Text, @"(\p{L})-\s*\n\s*(\p{L})", "$1$2")).Except(Words(texts[p].Text)))
                counts[w] = counts.GetValueOrDefault(w) + 1;
        }
        var elsewhere = LeakBaseline.Elsewhere(doc, data, password);
        // words the leak search also finds in the file without any page content (names in the file syntax, annotations, fonts …)
        var candidates = counts.Where(c => c.Value == 1).Select(c => c.Key).ToList();
        var outsideContent = LeakBaseline.FoundWithoutContent(doc, candidates, password);
        // words the content holds more often than the page text shows them (in text nobody sees, in names and keys of inline images):
        // the leak search would still find the other place after the redaction (29.10.2026)
        var syntax = LeakBaseline.ContentSyntax(doc);
        outsideContent.UnionWith(candidates.Where(w => Occurrences(syntax, w) > 1));
        var bars = new Dictionary<int, List<PdfRectangle>>();
        var expect = new List<Expectation>();
        var pages = Enumerable.Range(0, doc.PageCount).OrderBy(_ => rng.Next()).Take(maxPages).ToList();
        var barsOverHidden = rng.Next(2) == 0;
        foreach (var p in pages)
        {
            var words = Words(texts[p].Text).Where(w => counts[w] == 1 && !outsideContent.Contains(w) && !elsewhere.Contains(w, StringComparison.OrdinalIgnoreCase)
                && !WrittenByRedaction.Contains(w)
                && !counts.Keys.Any(other => other != w && other.Contains(w, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
            // only selections on the page: bars lie there (text beside it is hidden content, checked with its marks)
            var pageBox = HiddenContent.VisibleBox(doc.Pages[p]);
            bool OnPage(List<PdfRectangle> areas) => areas.Count > 0 && areas.All(a =>
                a.Left >= pageBox.Left - 1 && a.Right <= pageBox.Right + 1 && a.Bottom >= pageBox.Bottom - 1 && a.Top <= pageBox.Top + 1);
            foreach (var w in words.OrderBy(_ => rng.Next()).Take(3))
            {
                var at = texts[p].Text.IndexOf(w, StringComparison.Ordinal);
                var areas = texts[p].RedactionAreas(new TextRange(at, w.Length));   // as „Text schwärzen“ marks it
                if (!OnPage(areas)) continue;
                if (!bars.TryGetValue(p, out var list)) bars[p] = list = [];
                list.AddRange(areas);
                expect.Add(new Expectation(p, w, Fate.Gone));
            }
            // selections checked by their place (rules 2 and 3): also pages without a unique long word are tested, and a single
            // character is the narrowest bar — its neighbours close beside it must stay
            foreach (var (range, word) in Positions(texts[p].Text, rng, counts))
            {
                var areas = texts[p].RedactionAreas(range);
                if (!OnPage(areas)) continue;
                if (!bars.TryGetValue(p, out var list)) bars[p] = list = [];
                list.AddRange(areas);
                // a unique word that lost three letters is nowhere in the file any more
                if (word is not null && words.Contains(word) && expect.All(e => e.Word != word)) expect.Add(new Expectation(p, word, Fate.Gone));
            }
            // bars over parts of images (also under other content): their data must be blacked out there
            foreach (var (toPage, _, _, _) in Images(doc, doc.Pages[p]).OrderBy(_ => rng.Next()).Take(2))
            {
                var ib = toPage.TransformBounds(RectD.FromLtrb(0, 0, 1, 1)).Intersect(RectD.FromLtrb(pageBox.Left, pageBox.Bottom, pageBox.Right, pageBox.Top));
                if (ib.Width < 4 || ib.Height < 4) continue;
                double w = ib.Width * (0.25 + 0.35 * rng.NextDouble()), h = ib.Height * (0.25 + 0.35 * rng.NextDouble());
                double x = ib.Left + (ib.Width - w) * rng.NextDouble(), y = ib.Top + (ib.Height - h) * rng.NextDouble();
                if (!bars.TryGetValue(p, out var list)) bars[p] = list = [];
                list.Add(new PdfRectangle(x, y, x + w, y + h));
            }
            // bars over hidden text (only in cases without „Verborgenes schwärzen“): the bar alone must remove it
            if (barsOverHidden)
                foreach (var spot in HiddenText.Scan(doc, doc.Pages[p]).Spots.Where(s => !s.OutsidePage && s.Kind != HiddenTextKind.Outside).OrderBy(_ => rng.Next()).Take(2))
                {
                    if (!bars.TryGetValue(p, out var list)) bars[p] = list = [];
                    list.AddRange(HiddenText.MarkAreas(spot));
                    // its words must go if all of it lies on the page (bars lie on the page only)
                    if (!OnPage([spot.Bounds])) continue;
                    foreach (var w in words.Where(w => spot.Text.Contains(w, StringComparison.Ordinal) && expect.All(e => e.Word != w)))
                        expect.Add(new Expectation(p, w, Fate.Gone));
                }
            // a word in hidden content (beside the page, white on white …) goes with the marks of hidden content: it cannot be one that stays
            var hiddenMarks = HiddenText.Scan(doc, doc.Pages[p]).Spots.SelectMany(HiddenText.MarkAreas).ToList();
            bool InHidden(string w)
            {
                var at = texts[p].Text.IndexOf(w, StringComparison.Ordinal);
                return texts[p].RedactionAreas(new TextRange(at, w.Length)).Any(a => hiddenMarks.Any(h => a.Left <= h.Right && a.Right >= h.Left && a.Bottom <= h.Top && a.Top >= h.Bottom));
            }
            // nor one under a bar of another word (small text inside the box of a very large word)
            bool UnderBar(string w)
            {
                var at = texts[p].Text.IndexOf(w, StringComparison.Ordinal);
                return bars.TryGetValue(p, out var list) && texts[p].RedactionAreas(new TextRange(at, w.Length))
                    .Any(a => list.Any(h => a.Left < h.Right && a.Right > h.Left && a.Bottom < h.Top && a.Top > h.Bottom));
            }
            foreach (var w in words.Except(expect.Select(e => e.Word)).Where(w => !InHidden(w) && !UnderBar(w)).OrderBy(_ => rng.Next()).Take(3))
                expect.Add(new Expectation(p, w, Fate.Stays));
        }
        if (expect.Count == 0 && bars.Count == 0) return null;
        // bars lie on the page, as the program places them (areas of hidden text can reach beyond it)
        foreach (var p in bars.Keys.ToList())
        {
            var box = HiddenContent.VisibleBox(doc.Pages[p]);
            bars[p] = [.. bars[p].Select(b => new PdfRectangle(Math.Max(b.Left, box.Left), Math.Max(b.Bottom, box.Bottom), Math.Min(b.Right, box.Right), Math.Min(b.Top, box.Top)))
                .Where(b => b.Right - b.Left > 0.5 && b.Top - b.Bottom > 0.5)];
        }
        var name = Path.GetFileNameWithoutExtension(file);
        return new RedactionCase(name, data, bars, MarkHidden: !barsOverHidden, expect, barsOverHidden ? "Balken über Verborgenes" : "");
    }

    /// <summary>
    /// Selections by place: a single character (letter, digit, punctuation, CJK — not half of a surrogate pair), a short word of up to four
    /// letters, one occurrence of a word of five or more letters that occurs more than once, and three letters inside a word (with the
    /// word they belong to).
    /// </summary>
    private static IEnumerable<(TextRange Range, string? Word)> Positions(string text, Random rng, Dictionary<string, int> counts)
    {
        var singles = new List<int>();
        for (var i = 0; i < text.Length; i++)
            if (!char.IsWhiteSpace(text[i]) && !char.IsControl(text[i]) && !char.IsSurrogate(text[i])) singles.Add(i);
        if (singles.Count > 0) yield return (new TextRange(singles[rng.Next(singles.Count)], 1), null);
        var runs = new List<TextRange>();
        for (int i = 0, start = -1; i <= text.Length; i++)
        {
            var letter = i < text.Length && char.IsLetter(text[i]);
            if (letter && start < 0) start = i;
            else if (!letter && start >= 0)
            {
                runs.Add(new TextRange(start, i - start));
                start = -1;
            }
        }
        var shortWords = runs.Where(r => r.Length <= 4).ToList();
        if (shortWords.Count > 0) yield return (shortWords[rng.Next(shortWords.Count)], null);
        var repeated = runs.Where(r => r.Length >= 5 && counts.GetValueOrDefault(text.Substring(r.Start, r.Length)) > 1).ToList();
        if (repeated.Count > 0) yield return (repeated[rng.Next(repeated.Count)], null);
        // three letters inside a word, with letters before and after them: those must stay at their places (the gap is right)
        var long5 = runs.Where(r => r.Length >= 5).ToList();
        if (long5.Count > 0)
        {
            var w = long5[rng.Next(long5.Count)];
            yield return (new TextRange(w.Start + 1 + rng.Next(w.Length - 4), 3), text.Substring(w.Start, w.Length));
        }
    }

    /// <summary>Words the redaction itself writes into the file (the bare replacement font of a font that lost all its characters).</summary>
    private static readonly HashSet<string> WrittenByRedaction = new(["Helvetica", "Identity", "Adobe", "Supplement", "Ordering", "Registry"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Words of at least five letters (whole words of the page text).</summary>
    private static IEnumerable<string> Words(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var letter = i < text.Length && char.IsLetter(text[i]);
            if (letter && start < 0) start = i;
            else if (!letter && start >= 0)
            {
                if (i - start >= 5) yield return text[start..i];
                start = -1;
            }
        }
    }
}

/// <summary>
/// Text the leak search finds in a file that is not page content: document properties, bookmarks and other data outside the pages (the
/// same file written with one blank page), embedded font programs (names, sample texts) and the data of embedded pages and images.
/// </summary>
internal static class LeakBaseline
{
    public static string Elsewhere(PdfDocument doc, byte[] data, string? password)
    {
        var text = new System.Text.StringBuilder();
        try
        {
            using var blank = BlankPage.CreateDocument(612, 792, 1);
            using var rest = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(blank), rest, new PdfSaveOptions());
            using var outside = PdfDocument.Open(rest.ToArray(), password);
            AppendStrings(outside, text);
        }
        catch (Exception)
        {
            // without it, more words count as elsewhere-free; the check stays strict
        }
        var fonts = new HashSet<Core.Pdf.Objects.PdfStream>(ReferenceEqualityComparer.Instance);
        foreach (var page in doc.Pages)
        {
            var resources = new List<Core.Pdf.Objects.PdfDictionary?> { page.Resources };
            foreach (var x in DocumentCleanup.EmbeddedForms(page.Resources))
            {
                resources.Add(x.Dictionary.GetDictionary(Core.Pdf.Objects.PdfName.Resources));
                foreach (var (key, _) in x.Dictionary.Entries)
                    if (DocumentCleanup.IsEmbeddingInfo(key) || key.Value == "Metadata") Append(doc, x.Dictionary.Get(key), text, 0);
            }
            foreach (var r in resources)
                if (r?.GetDictionary(Core.Pdf.Objects.PdfName.Get("Font")) is { } dict)
                    foreach (var (key, _) in dict.Entries)
                        if (dict.Get(key) is Core.Pdf.Objects.PdfDictionary font)
                            foreach (var descriptor in Descriptors(font))
                                foreach (var file in (ReadOnlySpan<string>)["FontFile", "FontFile2", "FontFile3"])
                                    if (descriptor.Get(Core.Pdf.Objects.PdfName.Get(file)) is Core.Pdf.Objects.PdfStream program && fonts.Add(program))
                                        Append(doc, program, text, 0);
        }
        return text.ToString();
    }

    /// <summary>
    /// The syntax of the page contents and their forms as text: every string (as Latin-1), every name and the keys and names of inline
    /// images — to count how often a word stands there, also in text nobody sees.
    /// </summary>
    public static string ContentSyntax(PdfDocument doc)
    {
        var text = new System.Text.StringBuilder();
        var seen = new HashSet<Core.Pdf.Objects.PdfStream>(ReferenceEqualityComparer.Instance);
        try
        {
            foreach (var page in doc.Pages)
                using (var content = Redactor.PageContent(doc, page.Dictionary)) Read(content.Span.ToArray(), page.Resources, 0);
        }
        catch (Exception)
        {
            // a damaged content: what was read counts
        }
        return text.ToString();

        void Read(byte[] content, Core.Pdf.Objects.PdfDictionary? resources, int depth)
        {
            var parser = new Core.Pdf.Content.ContentParser(content);
            while (parser.Next() is not null)
            {
                foreach (var operand in parser.Operands)
                {
                    if (operand.Name is { } name) text.Append(name.Value).Append(' ');
                    else if (operand.Object is Core.Pdf.Objects.PdfString s) text.Append(System.Text.Encoding.Latin1.GetString(s.Bytes)).Append(' ');
                    else if (operand.Object is Core.Pdf.Objects.PdfArray array)
                    {
                        // TJ: the pieces of a word with kerning between them belong together
                        for (var i = 0; i < array.Count; i++)
                            if (array.Get(i) is Core.Pdf.Objects.PdfString piece) text.Append(System.Text.Encoding.Latin1.GetString(piece.Bytes));
                        text.Append(' ');
                    }
                }
                if (parser.InlineImage is { } inline)
                    foreach (var (key, value) in inline.Dictionary.Entries)
                        text.Append(key.Value).Append(' ').Append(value is Core.Pdf.Objects.PdfName v ? v.Value : "").Append(' ');
            }
            if (depth >= 8 || resources?.GetDictionary(Core.Pdf.Objects.PdfName.Get("XObject")) is not { } xobjects) return;
            foreach (var (key, _) in xobjects.Entries)
                if (xobjects.Get(key) is Core.Pdf.Objects.PdfStream form && form.Dictionary.GetName(Core.Pdf.Objects.PdfName.Subtype)?.Value == "Form" && seen.Add(form))
                    Read(doc.GetStreamData(form), form.Dictionary.GetDictionary(Core.Pdf.Objects.PdfName.Resources) ?? resources, depth + 1);
        }
    }

    /// <summary>Which of the words the leak search finds in the file written without any page content (all else kept).</summary>
    public static HashSet<string> FoundWithoutContent(PdfDocument doc, List<string> words, string? password)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (words.Count == 0) return found;
        try
        {
            var items = PageItem.From(doc).Select(item =>
            {
                var page = item.Page;
                var dict = new Core.Pdf.Objects.PdfDictionary { Resolver = page.Dictionary.Resolver };
                foreach (var (key, value) in page.Dictionary.Entries) dict.Set(key, value);
                dict.Set(Core.Pdf.Objects.PdfName.Contents, new Core.Pdf.Objects.PdfStream(new Core.Pdf.Objects.PdfDictionary(), []));
                return new PageItem(doc, new PdfPage(page.Index, dict, page.Id, page.Resources, page.MediaBox, page.CropBox, page.Rotate));
            }).ToArray();
            using var output = new MemoryStream();
            PdfWriter.Write(doc, items, output, new PdfSaveOptions());
            foreach (var hit in LeakSearch.Find(output.ToArray(), words, password, out _))
                if (hit.StartsWith('„') && hit.IndexOf('“') is var end and > 1) found.Add(hit[1..end]);
        }
        catch (Exception)
        {
            found.UnionWith(words);   // unsure: no word is checked
        }
        return found;
    }

    private static void AppendStrings(PdfDocument doc, System.Text.StringBuilder text)
    {
        if (doc.Trailer.GetDictionary(Core.Pdf.Objects.PdfName.Info) is { } info) Append(doc, info, text, 0);
        if (doc.Catalog is { } catalog) Append(doc, catalog, text, 0);
    }

    private static void Append(PdfDocument doc, Core.Pdf.Objects.PdfObject? value, System.Text.StringBuilder text, int depth)
    {
        if (depth > 8 || text.Length > 16 << 20) return;
        switch (value)
        {
            case Core.Pdf.Objects.PdfString s:
                text.Append(s.ToText()).Append(' ');
                break;
            case Core.Pdf.Objects.PdfStream stream:
                try
                {
                    if (stream.DataLength > 8 << 20) return;
                    var data = doc.GetStreamData(stream);
                    text.Append(System.Text.Encoding.Latin1.GetString(data)).Append(' ').Append(System.Text.Encoding.BigEndianUnicode.GetString(data)).Append(' ');
                }
                catch (Core.Pdf.PdfException)
                {
                }
                break;
            case Core.Pdf.Objects.PdfDictionary d:
                foreach (var (key, _) in d.Entries)
                    if (key.Value is not ("Pages" or "Parent" or "Kids" or "P" or "Pg")) Append(doc, d.Get(key), text, depth + 1);
                break;
            case Core.Pdf.Objects.PdfArray a:
                for (var i = 0; i < a.Count && i < 10_000; i++) Append(doc, a.Get(i), text, depth + 1);
                break;
        }
    }

    private static IEnumerable<Core.Pdf.Objects.PdfDictionary> Descriptors(Core.Pdf.Objects.PdfDictionary font)
    {
        if (font.GetDictionary(Core.Pdf.Objects.PdfName.Get("FontDescriptor")) is { } own) yield return own;
        if (font.GetArray(Core.Pdf.Objects.PdfName.Get("DescendantFonts")) is { Count: > 0 } kids &&
            kids.Get(0) is Core.Pdf.Objects.PdfDictionary cid && cid.GetDictionary(Core.Pdf.Objects.PdfName.Get("FontDescriptor")) is { } inner)
            yield return inner;
    }
}
