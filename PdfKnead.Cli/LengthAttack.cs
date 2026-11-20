using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Cli;

/// <summary>
/// <c>pdf-knead laengenangriff</c> (docs/PRUEFUNGEN.md, section 6j): measures how well hiding the lengths of redacted words works — not by
/// the program's own notice, but by the attack it is meant to defeat (Bland, Iyer, Levchenko, „Story Beyond the Eye: Glyph Positions Break
/// PDF Text Redaction“, PoPETs 2023). Generated pages (Helvetica) with justified and ragged paragraphs, a name from a list in one line, written
/// with word spacing, with the numbers of TJ or with each word placed by its own Td; the name is redacted as „Text schwärzen“ does. Then
/// the attacker reads the redacted page: from the positions of the remaining characters and the widths of the font it estimates the width
/// of what was removed and keeps every name of the list whose width fits. Two attackers: the plain one takes the estimate as it is; the
/// informed one knows the program's step and keeps every name within one step below the estimate. Reported per kind of line: how often the
/// true name is among the candidates, how many candidates remain, how often the name is identified (one candidate).
/// </summary>
internal static class LengthAttack
{
    private const double Size = 11;
    private const double SpaceWidth = 0.278 * Size;   // Helvetica
    private static double Step => Redactor.LengthStepEm * Size;   // the program's step (Redactor.Widen.cs)
    private const double Tolerance = 1.5;             // every word starts on a grid of 0.5 pt, the spacing is read from several gaps

    public static int Run(int count, int first, bool hideLengths, string? keep, double? stepEm = null)
    {
        if (stepEm is { } step) Redactor.LengthStepEm = step;
        var names = Names();
        var widths = new Dictionary<string, double>();
        double Width(string word)
        {
            if (!widths.TryGetValue(word, out var w))
            {
                using var doc = PdfDocument.Open(MiniPdf($"BT /F1 {F(Size)} Tf 72 700 Td ({word}) Tj ET"));
                widths[word] = w = PageInterpreter.Build(doc, doc.Pages[0]).Text.Max(g => Math.Max(g.X1, g.X2)) - 72;
            }
            return w;
        }
        var nameWidths = names.Select(n => (Name: n, Width: Width(n))).ToList();
        var stats = new Dictionary<string, Stat>();
        Stat withNotice = new(), withoutNotice = new();   // the cases the program named as „Länge teilweise sichtbar“, and the others
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (keep is not null) Directory.CreateDirectory(keep);
        for (var seed = first; seed < first + count; seed++)
        {
            var rng = new Random(seed);
            var justified = rng.Next(2) == 0;
            var style = rng.Next(3);
            var name = names[rng.Next(names.Count)];
            var (content, lineY) = Page(rng, justified, style, name, Width);
            var file = MiniPdf(content);
            byte[] redacted;
            PdfRectangle area;
            IReadOnlyList<PdfRectangle> extensions;
            bool partly;
            using (var doc = PdfDocument.Open(file))
            {
                var text = PageText.Create(PageInterpreter.Build(doc, doc.Pages[0]).Text);
                var hits = text.Find(name, matchCase: true);
                if (hits.Count != 1) continue;   // the name twice on the page: not a case
                var q = text.Quads(hits[0]);
                area = new PdfRectangle(q.Min(x => Math.Min(x.X0, x.X3)), q.Min(x => Math.Min(x.Y0, x.Y1)), q.Max(x => Math.Max(x.X1, x.X2)), q.Max(x => Math.Max(x.Y2, x.Y3)));
                var items = PageItem.From(doc);
                items[0] = items[0].WithElements([new RedactionElement { Rect = area }]);
                using var output = new MemoryStream();
                var result = PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, HideRedactedLengths = hideLengths, ImageCodec = SkiaImageCodec.Instance });
                redacted = output.ToArray();
                extensions = result.BarExtensions.TryGetValue(0, out var list) ? list : [];
                partly = result.LengthsPartlyShown.Count > 0;
            }
            if (keep is not null)
            {
                File.WriteAllBytes(Path.Combine(keep, $"angriff-{seed}.pdf"), file);
                File.WriteAllBytes(Path.Combine(keep, $"angriff-{seed}-geschwaerzt.pdf"), redacted);
            }
            // the bar as the attacker sees it: the area and what it grew by on this line
            var bar = RectD.FromLtrb(area.Left, area.Bottom, area.Right, area.Top);
            foreach (var e in extensions)
                if (e.Bottom < area.Top && e.Top > area.Bottom) bar = RectD.FromLtrb(Math.Min(bar.Left, e.Left), bar.Top, Math.Max(bar.Right, e.Right), bar.Bottom);
            var estimate = Estimate(redacted, lineY, bar, out var seenJustified, out var why);
            var kind = $"{(justified ? "Blocksatz" : "Flattersatz")} {new[] { "Tw", "TJ", "Td" }[style]}";
            if (!stats.TryGetValue(kind, out var stat)) stats[kind] = stat = new Stat();
            var byNotice = partly ? withNotice : withoutNotice;
            stat.Cases++;
            byNotice.Cases++;
            if (partly) stat.Partly++;
            if (estimate is not { } w)
            {
                stat.NoEstimate++;
                byNotice.NoEstimate++;
                if (keep is not null) File.AppendAllText(Path.Combine(keep, "angriff.txt"), $"{seed}\t{kind}\t{name}\tkeine Schätzung: {why}\n");
                continue;
            }
            var trueWidth = Width(name);
            var plain = nameWidths.Where(n => Math.Abs(n.Width - w) <= Tolerance).Select(n => n.Name).ToList();
            var informed = nameWidths.Where(n => n.Width <= w + Tolerance && n.Width > w - Step - Tolerance).Select(n => n.Name).ToList();
            stat.Add(plain.Contains(name), plain.Count, informed.Contains(name), informed.Count);
            byNotice.Add(plain.Contains(name), plain.Count, informed.Contains(name), informed.Count);
            if (keep is not null)
                File.AppendAllText(Path.Combine(keep, "angriff.txt"), FormattableString.Invariant(
                    $"{seed}\t{kind}\t{name}\t{trueWidth:0.00}\t{w:0.00}\t{(seenJustified ? "Blocksatz" : "Flattersatz")}\t{plain.Count}\t{(plain.Contains(name) ? 1 : 0)}\t{informed.Count}\t{(informed.Contains(name) ? 1 : 0)}\t{(partly ? "Hinweis" : "")}\n"));
        }
        Console.WriteLine($"Längenangriff ({(hideLengths ? "mit" : "ohne")} „Länge verbergen“), {names.Count} Namen in der Liste, Schritt {Step:0.#} pt:");
        Console.WriteLine("  Art            Fälle  Hinweis  ohne Schätzung | einfacher Angreifer: Treffer  eindeutig  ≤3   Kandidaten (Median) | informierter: Treffer  eindeutig  ≤3   Kandidaten (Median)");
        var all = new Stat();
        foreach (var (kind, s) in stats.OrderBy(k => k.Key))
        {
            Console.WriteLine(s.Line(kind));
            all.Merge(s);
        }
        Console.WriteLine(all.Line("alle"));
        Console.WriteLine(withoutNotice.Line("ohne Hinweis"));
        Console.WriteLine(withNotice.Line("mit Hinweis"));
        Console.WriteLine(FormattableString.Invariant($"Zeit {watch.Elapsed.TotalSeconds:0.0} s"));
        return 0;
    }

    private sealed class Stat
    {
        public int Cases, Partly, NoEstimate;
        private readonly List<int> _plain = [], _informed = [];
        private int _plainHit, _informedHit;

        public void Add(bool plainHit, int plain, bool informedHit, int informed)
        {
            if (plainHit) _plainHit++;
            if (informedHit) _informedHit++;
            _plain.Add(plain);
            _informed.Add(informed);
        }

        public void Merge(Stat other)
        {
            Cases += other.Cases;
            Partly += other.Partly;
            NoEstimate += other.NoEstimate;
            _plainHit += other._plainHit;
            _informedHit += other._informedHit;
            _plain.AddRange(other._plain);
            _informed.AddRange(other._informed);
        }

        public string Line(string kind)
        {
            static string Part(List<int> counts, int hits)
            {
                if (counts.Count == 0) return "–";
                var sorted = counts.Order().ToList();
                return FormattableString.Invariant($"{100.0 * hits / counts.Count,6:0.0} %  {100.0 * counts.Count(c => c == 1) / counts.Count,6:0.0} %  {100.0 * counts.Count(c => c <= 3) / counts.Count,6:0.0} %  {sorted[sorted.Count / 2],6}");
            }
            return FormattableString.Invariant($"  {kind,-14} {Cases,5}  {Partly,7}  {NoEstimate,14} | {Part(_plain, _plainHit),-48} | {Part(_informed, _informedHit)}");
        }
    }

    // ------------------------------------------------------------ The attacker

    /// <summary>
    /// The attacker's estimate of the width of what was removed on the line at <paramref name="lineY"/> under <paramref name="bar"/>: in a
    /// justified line (its end is the end of a line above or below: the margin) from the margin, the visible words and their spacing
    /// (every gap of the line was set with the removed word); in a ragged line from the gap the bar leaves between its neighbours (two
    /// spaces of the font and the word). Null if the line cannot be read.
    /// </summary>
    private static double? Estimate(byte[] redacted, double lineY, RectD bar, out bool justified, out string why)
    {
        justified = false;
        why = "";
        using var doc = PdfDocument.Open(redacted);
        var glyphs = PageInterpreter.Build(doc, doc.Pages[0]).Text.Where(g => g.Visible && !string.IsNullOrWhiteSpace(g.Text)).ToList();
        var lines = glyphs.GroupBy(g => Math.Round(g.Y0)).Select(l => (Y: l.Key, Words: Words(l))).Where(l => l.Words.Count > 0).ToList();
        var line = lines.MinBy(l => Math.Abs(l.Y - lineY));
        if (line.Words is null || Math.Abs(line.Y - lineY) > Size)
        {
            why = "Zeile nicht gefunden";
            return null;
        }
        var words = line.Words;
        var before = words.Where(w => w.End <= bar.Left + 0.5).ToList();
        var after = words.Where(w => w.Start >= bar.Right - 0.5).ToList();
        if (before.Count + after.Count != words.Count)
        {
            why = FormattableString.Invariant($"Wort am Balken {bar.Left:0.0}-{bar.Right:0.0}: {string.Join(" ", words.Where(w => w.End > bar.Left + 0.5 && w.Start < bar.Right - 0.5).Select(w => $"{w.Start:0.0}-{w.End:0.0}"))}");
            return null;
        }
        double start = words[0].Start, end = words[^1].End;
        justified = lines.Any(l => l.Y != line.Y && Math.Abs(l.Y - line.Y) < 4 * Size && Math.Abs(l.Words[^1].End - end) <= 1.0);
        if (justified)
        {
            // the spacing of the line from its visible gaps (not those beside the bar)
            var gaps = new List<double>();
            for (var i = 1; i < before.Count; i++) gaps.Add(before[i].Start - before[i - 1].End);
            for (var i = 1; i < after.Count; i++) gaps.Add(after[i].Start - after[i - 1].End);
            var spacing = gaps.Count > 0 ? gaps.Order().ElementAt(gaps.Count / 2) : SpaceWidth;
            return end - start - words.Sum(w => w.End - w.Start) - words.Count * spacing;
        }
        if (before.Count == 0 || after.Count == 0)
        {
            why = "kein Nachbarwort";
            return null;
        }
        return after[0].Start - before[^1].End - 2 * SpaceWidth;
    }

    private static List<(double Start, double End)> Words(IEnumerable<TextGlyph> line)
    {
        var words = new List<(double Start, double End)>();
        foreach (var g in line.OrderBy(g => g.X0))
        {
            double s = Math.Min(g.X0, g.X3), e = Math.Max(g.X1, g.X2);
            if (words.Count > 0 && s - words[^1].End <= 0.15 * g.Size) words[^1] = (words[^1].Start, Math.Max(words[^1].End, e));
            else words.Add((s, e));
        }
        return words;
    }

    // ------------------------------------------------------------ The pages

    /// <summary>A page with a paragraph, the name in one of its lines; returns the content and the baseline of that line.</summary>
    private static (string Content, double LineY) Page(Random rng, bool justified, int style, string name, Func<string, double> width)
    {
        var target = 300 + rng.Next(100);
        var lines = new List<List<string>>();
        var count = 4 + rng.Next(3);
        var nameLine = 1 + rng.Next(count - 2);
        for (var l = 0; l < count; l++)
        {
            List<string> line = [];
            for (var tries = 0; tries < 300; tries++)
            {
                line = [];
                var w = 0.0;
                while (true)
                {
                    var word = Filler(rng);
                    var next = w + (line.Count > 0 ? SpaceWidth : 0) + width(word);
                    if (next > target) break;
                    line.Add(word);
                    w = next;
                }
                if (l == count - 1 || !justified || line.Count >= 4 && (target - w) / (line.Count - 1) <= 0.6 * Size) break;
            }
            if (l == count - 1) line = line.Take(2 + rng.Next(2)).ToList();
            lines.Add(line);
        }
        // a ragged paragraph: the line with the name is shorter than its neighbours (room to grow into); the name never first or last on
        // its line, so that the attacker has a neighbour on each side
        if (!justified) lines[nameLine] = lines[nameLine].Take(Math.Max(3, lines[nameLine].Count - 1)).ToList();
        lines[nameLine][1 + rng.Next(lines[nameLine].Count - 2)] = name;
        var content = new StringBuilder($"BT /F1 {F(Size)} Tf ");
        var lineY = 0.0;
        for (var l = 0; l < count; l++)
        {
            var words = lines[l];
            var natural = words.Sum(width) + (words.Count - 1) * SpaceWidth;
            var extra = justified && l < count - 1 ? (target - natural) / (words.Count - 1) : 0;
            var y = 700 - l * 1.25 * Size;
            if (l == nameLine) lineY = y;
            content.Append(FormattableString.Invariant($"1 0 0 1 72 {y:0.##} Tm "));
            switch (style)
            {
                case 0:
                    content.Append($"{F(extra)} Tw ({string.Join(" ", words)}) Tj 0 Tw ");
                    break;
                case 1:
                    content.Append('[').Append(string.Join($" {F(-extra * 1000 / Size)} ", words.Select((w, i) => $"({w}{(i < words.Count - 1 ? " " : "")})"))).Append("] TJ ");
                    break;
                default:
                    for (var i = 0; i < words.Count; i++)
                        content.Append(i == 0 ? $"({words[i]}) Tj " : $"{F(width(words[i - 1]) + SpaceWidth + extra)} 0 Td ({words[i]}) Tj ");
                    break;
            }
        }
        return (content.Append("ET").ToString(), lineY);
    }

    private static readonly string[] Onsets = ["b", "d", "f", "g", "h", "k", "l", "m", "n", "p", "r", "s", "t", "w", "sch", "st", "br", "kr", "fr", "gr"];
    private static readonly string[] Vowels = ["a", "e", "i", "o", "u", "ei", "au", "ie"];
    private static readonly string[] Codas = ["", "n", "r", "l", "s", "ch", "ck", "nn", "rt", "ld", "ng"];

    /// <summary>Invented surnames (no real person), 300 of them, of many widths.</summary>
    private static List<string> Names()
    {
        var rng = new Random(20261101);
        var names = new HashSet<string>();
        while (names.Count < 300)
        {
            var sb = new StringBuilder();
            var syllables = 1 + rng.Next(3);
            for (var i = 0; i < syllables; i++) sb.Append(Onsets[rng.Next(Onsets.Length)]).Append(Vowels[rng.Next(Vowels.Length)]).Append(Codas[rng.Next(Codas.Length)]);
            var name = sb.ToString();
            if (name.Length < 3) continue;
            names.Add(char.ToUpperInvariant(name[0]) + name[1..]);
        }
        return [.. names.Order()];
    }

    private static string Filler(Random rng)
    {
        var sb = new StringBuilder();
        var syllables = 1 + rng.Next(3);
        for (var i = 0; i < syllables; i++) sb.Append(Onsets[rng.Next(Onsets.Length)]).Append(Vowels[rng.Next(Vowels.Length)]).Append(Codas[rng.Next(Codas.Length)]);
        return sb.ToString();
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A one-page PDF (A4, Helvetica as /F1) with the content.</summary>
    private static byte[] MiniPdf(string content)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:0000000000} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
