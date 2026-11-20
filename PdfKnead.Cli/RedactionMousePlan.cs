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
/// <c>pdf-knead schwaerzpruefung &lt;ordner&gt; --mausplan</c> (see docs/PRUEFUNGEN.md, section 6j): turns the cases of a folder (PDF with its
/// expectation file) into plans for redacting them in the program with the mouse. Every bar becomes a drag on the page, in coordinates
/// of the displayed page (0 … 1 from its top left corner, after crop box and rotation) — so a driver only has to find the page on the
/// screen:
/// <list type="bullet">
/// <item><c>text</c>: a bar that is exactly the mark of a piece of text („Text schwärzen“): drag from inside its first to inside its last
/// character; checked with the same hit test and caret the program uses.</item>
/// <item><c>bereich</c>: any other bar („Bereich schwärzen“): drag from a corner that lies on no character (else the tool marks text).</item>
/// <item><c>schieben</c>: a bar whose corners all lie on characters: drawn the same size at a free place, then moved there by its middle.</item>
/// </list>
/// A press must not land on a mark placed before (it would move that one); the plan keeps away from them.
/// </summary>
internal static class RedactionMousePlan
{
    // Distance in points kept from characters and from earlier marks (the program tolerates two screen pixels)
    private const double Margin = 1.5;

    /// <summary>Writes <c>&lt;name&gt;-plan.txt</c> beside every case of the folder.</summary>
    public static int Run(string folder, Dictionary<string, string> passwords)
    {
        int planned = 0, impossible = 0;
        var reasons = new Dictionary<string, int>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.pdf").Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var sidecar = Path.Combine(folder, name + "-erwartung.txt");
            if (!File.Exists(sidecar)) continue;
            var password = passwords.GetValueOrDefault(Path.GetFileName(file));
            List<string> lines;
            try
            {
                lines = Plan(File.ReadAllBytes(file), File.ReadAllLines(sidecar), password);
            }
            catch (Exception ex)
            {
                lines = [$"fehlt\t0\t{ex.GetType().Name}: {ex.Message}"];
            }
            if (password is not null) lines.Insert(0, "passwort\t" + password);
            File.WriteAllLines(Path.Combine(folder, name + "-plan.txt"), lines);
            var missing = lines.Where(l => l.StartsWith("fehlt\t", StringComparison.Ordinal)).ToList();
            if (missing.Count == 0)
            {
                planned++;
                continue;
            }
            impossible++;
            foreach (var m in missing)
            {
                var reason = m.Split('\t')[2];
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
            }
        }
        Console.WriteLine($"Pläne: {planned}, nicht mit der Maus: {impossible}");
        foreach (var (reason, count) in reasons.OrderByDescending(r => r.Value)) Console.WriteLine($"  {count} × {reason}");
        return 0;
    }

    /// <summary>The plan of one case (see the class for the lines).</summary>
    public static List<string> Plan(byte[] data, string[] expectation, string? password)
    {
        using var doc = PdfDocument.Open(data, password);
        var lines = new List<string>();
        var bars = new SortedDictionary<int, List<PdfRectangle>>();
        var hidden = false;
        foreach (var line in expectation)
        {
            var parts = line.Split('\t');
            if (parts[0] == "verborgen") hidden = true;
            if (parts[0] != "balken" || parts.Length < 3) continue;
            var v = parts[2].Split(' ').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            var page = int.Parse(parts[1], CultureInfo.InvariantCulture) - 1;
            if (!bars.TryGetValue(page, out var list)) bars[page] = list = [];
            list.Add(new PdfRectangle(v[0], v[1], v[2], v[3]));
        }
        foreach (var (p, list) in bars)
        {
            if (p >= doc.PageCount)
            {
                lines.Add($"fehlt\t{p + 1}\tSeite fehlt");
                continue;
            }
            var page = doc.Pages[p];
            var text = PageText.Create(PageInterpreter.BuildText(doc, page));
            var toDisplay = PageGeometry.PageToDevice(page, 1);
            var box = HiddenContent.VisibleBox(page);
            double width = page.Width, height = page.Height;
            lines.Add(FormattableString.Invariant($"seite\t{p + 1}\t{width:0.###}\t{height:0.###}\t{doc.PageCount}"));
            // a place on no character and far from every bar of the page: a click there takes the selection away before the next drag
            // (the handles of a selected mark reach eight pixels beyond it)
            if (FreeSpot(text, box, list) is { } spot)
                lines.Add($"frei\t{p + 1}\t{UOf(toDisplay, width, height, spot.X, spot.Y)}");
            string U(double x, double y) => UOf(toDisplay, width, height, x, y);
            var placed = new List<PdfRectangle>();
            bool Free(double x, double y) => !text.IsOverText(x, y, Margin) && !OnMark(placed, x, y) &&
                                             x >= box.Left && x <= box.Right && y >= box.Bottom && y <= box.Top;
            foreach (var bar in list)
            {
                if (TextDrag(text, bar, placed) is { } drag)
                    lines.Add($"text\t{p + 1}\t{U(drag.X1, drag.Y1)}\t{U(drag.X2, drag.Y2)}");
                else if (FreeCorner(bar, Free) is { } corner)
                    lines.Add($"bereich\t{p + 1}\t{U(corner.X1, corner.Y1)}\t{U(corner.X2, corner.Y2)}");
                else if (FreePlace(bar, box, Free, placed) is { } place)
                    lines.Add($"schieben\t{p + 1}\t{U(place.Left, place.Top)}\t{U(place.Right, place.Bottom)}\t" +
                              $"{U((place.Left + place.Right) / 2, (place.Bottom + place.Top) / 2)}\t{U((bar.Left + bar.Right) / 2, (bar.Bottom + bar.Top) / 2)}");
                else
                {
                    lines.Add($"fehlt\t{p + 1}\tkein freier Platz für einen Balken");
                    continue;
                }
                placed.Add(bar);
            }
        }
        if (hidden) lines.Add("verborgen");
        return lines;
    }

    private static string UOf(Matrix toDisplay, double width, double height, double x, double y)
    {
        var (dx, dy) = toDisplay.Transform(x, y);
        return FormattableString.Invariant($"{dx / width:0.######}\t{dy / height:0.######}");
    }

    /// <summary>The free place of the page farthest from its bars (on no character), searched on a grid.</summary>
    private static (double X, double Y)? FreeSpot(PageText text, PdfRectangle box, List<PdfRectangle> bars)
    {
        (double X, double Y)? best = null;
        var bestDistance = -1.0;
        var step = Math.Max(4, Math.Min(box.Width, box.Height) / 40);
        for (var y = box.Bottom + step; y < box.Top; y += step)
            for (var x = box.Left + step; x < box.Right; x += step)
            {
                if (text.IsOverText(x, y, 3)) continue;
                var d = bars.Count == 0 ? 1e9 : bars.Min(b => Math.Max(Math.Max(b.Left - x, x - b.Right), Math.Max(b.Bottom - y, y - b.Top)));
                if (d > bestDistance)
                {
                    bestDistance = d;
                    best = (x, y);
                }
            }
        return bestDistance >= 6 ? best : null;
    }

    /// <summary>
    /// The drag for a bar that is the mark of a piece of text: from inside its first to inside its last character, if the program's hit
    /// test and caret make exactly that range of it (and no earlier mark lies under the press).
    /// </summary>
    private static (double X1, double Y1, double X2, double Y2)? TextDrag(PageText text, PdfRectangle bar, List<PdfRectangle> placed)
    {
        // the characters whose middle lies in the bar
        int first = -1, last = -1;
        for (var c = 0; c < text.Text.Length; c++)
        {
            if (text.Quads(new TextRange(c, 1)) is not [var q]) continue;
            var b = q.Bounds;
            double x = (b.Left + b.Right) / 2, y = (b.Top + b.Bottom) / 2;
            if (x < bar.Left || x > bar.Right || y < bar.Bottom || y > bar.Top) continue;
            if (first < 0) first = c;
            last = c;
        }
        if (first < 0) return null;
        var range = new TextRange(first, last + 1 - first);
        if (text.RedactionAreas(range) is not [var area] || !Same(area, bar)) return null;
        var a = text.Quads(new TextRange(first, 1))[0];
        var z = text.Quads(new TextRange(last, 1))[0];
        foreach (var (s, e) in new[] { (0.3, 0.7), (0.4, 0.6), (0.2, 0.8) })
        {
            // along the baseline direction of each character, at half its height
            var (x1, y1) = Along(a, s);
            var (x2, y2) = Along(z, e);
            if (!text.IsOverText(x1, y1) || OnMark(placed, x1, y1)) continue;
            if (text.CaretAt(x1, y1) != range.Start || text.CaretAt(x2, y2) != range.End) continue;
            return (x1, y1, x2, y2);
        }
        return null;
    }

    private static (double X, double Y) Along(TextQuad q, double t)
    {
        // bottom left → bottom right is the writing direction; the middle between bottom and top edge
        double bx = q.X0 + (q.X1 - q.X0) * t, by = q.Y0 + (q.Y1 - q.Y0) * t;
        double tx = q.X3 + (q.X2 - q.X3) * t, ty = q.Y3 + (q.Y2 - q.Y3) * t;
        return ((bx + tx) / 2, (by + ty) / 2);
    }

    private static bool Same(PdfRectangle a, PdfRectangle b) =>
        Math.Abs(a.Left - b.Left) < 0.01 && Math.Abs(a.Right - b.Right) < 0.01 && Math.Abs(a.Bottom - b.Bottom) < 0.01 && Math.Abs(a.Top - b.Top) < 0.01;

    private static bool OnMark(List<PdfRectangle> marks, double x, double y) =>
        marks.Any(m => x >= m.Left - Margin && x <= m.Right + Margin && y >= m.Bottom - Margin && y <= m.Top + Margin);

    /// <summary>A corner of the bar on no character and no earlier mark, and the corner opposite it.</summary>
    private static (double X1, double Y1, double X2, double Y2)? FreeCorner(PdfRectangle bar, Func<double, double, bool> free)
    {
        (double, double)[] corners = [(bar.Left, bar.Top), (bar.Right, bar.Top), (bar.Left, bar.Bottom), (bar.Right, bar.Bottom)];
        foreach (var (x, y) in corners)
            if (free(x, y))
                return (x, y, bar.Left + bar.Right - x, bar.Bottom + bar.Top - y);
        return null;
    }

    /// <summary>
    /// A place on the page for a mark of the bar's size whose top left corner lies on no character and no earlier mark, and whose area
    /// touches no earlier mark (its middle is pressed to move it).
    /// </summary>
    private static PdfRectangle? FreePlace(PdfRectangle bar, PdfRectangle box, Func<double, double, bool> free, List<PdfRectangle> placed)
    {
        double w = bar.Right - bar.Left, h = bar.Top - bar.Bottom;
        var step = Math.Max(4, Math.Min(box.Width, box.Height) / 60);
        for (var y = box.Top - 2; y - h >= box.Bottom + 2; y -= step)
            for (var x = box.Left + 2; x + w <= box.Right - 2; x += step)
            {
                var place = new PdfRectangle(x, y - h, x + w, y);
                if (!free(x, y)) continue;
                if (placed.Any(m => place.Left < m.Right + Margin && place.Right > m.Left - Margin && place.Bottom < m.Top + Margin && place.Top > m.Bottom - Margin))
                    continue;
                return place;
            }
        return null;
    }
}
