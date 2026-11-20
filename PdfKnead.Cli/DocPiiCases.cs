using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Rendering;

namespace PdfKnead.Cli;

/// <summary>
/// Redaction test cases from the texts of the DocPII data set (synthetic texts with personal data, MIT; `TestPDFs/docpii`, see its
/// LIESMICH.md): each text becomes a PDF in one of several forms — flowing text, a table of the data, text set in pieces with spacing in TJ
/// and without spaces, characters placed one by one (also in reverse order), a second invisible text layer over the text, text partly under
/// an image, the data again on a layer that is off, a scan with an invisible text layer. The data are searched as „Suchen und schwärzen“
/// searches (all of them at once, ignoring case); every hit becomes a bar as the program marks it. Checked are the search (it must find each
/// datum as often as the text holds it) and then everything <see cref="RedactionCheck.Check"/> checks: nothing of the data stays in the file,
/// the rest of the page stays as it was.
/// </summary>
/// <remarks>The tricks follow the research on redaction (deredaction by spacing, glyph positions, double text layers; PoPETs 2023).</remarks>
internal static class DocPiiCases
{
    private const double Size = 11, Leading = 15, Left = 60, Top = 780;
    private const int LineChars = 80;

    private static readonly string[] Forms = ["fliesstext", "tabelle", "tj-abstaende", "einzelzeichen", "doppelt", "unter-bild", "ebene", "scan"];

    private sealed record Record(string Uid, string Text, List<string> Entities);

    /// <summary>
    /// "pdf-knead schwaerzpruefung docpii &lt;anzahl&gt; [--start n] [--laengen] [--ablegen &lt;ordner&gt;]": the cases of the records, each searched,
    /// redacted and checked; prints every case with a problem and a summary per form.
    /// </summary>
    public static int Run(string json, int count, int first, string? keep, bool hideLengths)
    {
        if (keep is not null) Directory.CreateDirectory(keep);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int ok = 0, failed = 0, skipped = 0;
        var perForm = new Dictionary<string, (int Ok, int Failed)>();
        foreach (var (test, search) in Make(json, count, first))
        {
            if (test is null)
            {
                skipped++;
                continue;
            }
            var problems = new List<string>(search);
            problems.AddRange(RedactionCheck.Check(test, keep: keep, hideLengths: hideLengths));
            var (o, f) = perForm.GetValueOrDefault(test.Description);
            if (problems.Count == 0)
            {
                ok++;
                perForm[test.Description] = (o + 1, f);
                continue;
            }
            failed++;
            perForm[test.Description] = (o, f + 1);
            Console.WriteLine($"{test.Name}: {string.Join("; ", problems.Take(4))}");
            if (keep is not null) File.WriteAllBytes(Path.Combine(keep, test.Name + ".pdf"), test.Pdf);
        }
        foreach (var (form, (o, f)) in perForm.OrderBy(p => Array.IndexOf(Forms, p.Key)))
            Console.WriteLine($"  {form}: {o + f} Fälle, {f} Abweichungen");
        Console.WriteLine($"Fälle: {ok + failed}, bestanden: {ok}, Abweichungen: {failed}, übersprungen: {skipped}, Zeit {watch.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>The cases of the records <paramref name="first"/> … (1-based), each with its search check result.</summary>
    /// <param name="json">The data set (docPII-contextual-redaction-benchmark.json).</param>
    /// <param name="count">How many records.</param>
    /// <param name="first">The first record (1-based).</param>
    /// <returns>Per record the case (null if the text cannot be set in the standard font) and the problems of the search.</returns>
    public static IEnumerable<(RedactionCase? Case, List<string> SearchProblems)> Make(string json, int count, int first)
    {
        var records = Load(json);
        for (var i = first - 1; i < Math.Min(records.Count, first - 1 + count); i++)
        {
            var record = records[i];
            if (!Encodable(record.Text) || record.Entities.Any(e => !Encodable(e))) yield return (null, []);
            else yield return Build(record, i);
        }
    }

    private static List<Record> Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var list = new List<Record>();
        foreach (var uid in root.GetProperty("uid").EnumerateObject())
        {
            var key = uid.Name;
            var text = root.GetProperty("text").GetProperty(key).GetString() ?? "";
            var entities = root.GetProperty("entities").GetProperty(key).EnumerateArray()
                .Select(e => e.GetProperty("entity").GetString() ?? "").Where(e => e.Trim().Length >= 3).Distinct().ToList();
            list.Add(new Record(uid.Value.GetString() ?? key, text, entities));
        }
        return list;
    }

    // WinAnsiEncoding for the characters beyond Latin-1 that texts use (quotes, dashes, ellipsis)
    private static readonly Dictionary<char, byte> WinAnsi = new()
    {
        ['\u2018'] = 0x91, ['\u2019'] = 0x92, ['\u201C'] = 0x93, ['\u201D'] = 0x94, ['\u2013'] = 0x96, ['\u2014'] = 0x97, ['\u2026'] = 0x85, ['\u20AC'] = 0x80,
    };

    private static bool Encodable(string text) => text.All(c => c is '\n' or '\r' or '\t' or >= '\u0020' and <= '\u007E' or >= '\u00A0' and <= '\u00FF' || WinAnsi.ContainsKey(c));

    /// <summary>A PDF string literal of the text, in WinAnsiEncoding.</summary>
    private static string Literal(string text)
    {
        var sb = new StringBuilder("(");
        foreach (var c in text)
        {
            if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
            else if (WinAnsi.TryGetValue(c, out var code)) sb.Append((char)code);
            else sb.Append(c);
        }
        return sb.Append(')').ToString();
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The text broken into lines of at most <see cref="LineChars"/> characters, at spaces.</summary>
    private static List<string> Lines(string text)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.ReplaceLineEndings("\n").Replace('\t', ' ').Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > LineChars)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) lines.Add(line.ToString());
        }
        return lines.Take(45).ToList();   // one page
    }

    private static string Flowing(List<string> lines, string mode = "")
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
            sb.Append($"BT /F1 {F(Size)} Tf {mode}{F(Left)} {F(Top - i * Leading)} Td {Literal(lines[i])} Tj ET\n");
        return sb.ToString();
    }

    private static (RedactionCase? Case, List<string> SearchProblems) Build(Record record, int index)
    {
        var form = Forms[index % Forms.Length];
        var rng = new Random(index * 7919 + 13);
        var lines = Lines(record.Text);
        var shown = string.Join("\n", lines);
        var content = new StringBuilder();
        var extraResources = "";
        var markHidden = false;
        string? ocProperties = null;
        var images = new List<(string Name, byte[] Gray, int Width, int Height)>();
        switch (form)
        {
            case "fliesstext":
                content.Append(Flowing(lines));
                break;
            case "tabelle":
            {
                var half = lines.Take(Math.Max(1, lines.Count / 2)).ToList();
                content.Append(Flowing(half));
                var y = Top - (half.Count + 1) * Leading;
                content.Append($"0.5 w {F(Left)} {F(y + Leading - 3)} m 540 {F(y + Leading - 3)} l S\n");
                for (var e = 0; e < record.Entities.Count; e++, y -= Leading)
                {
                    content.Append($"BT /F1 {F(Size)} Tf {F(Left)} {F(y)} Td (Angabe {e + 1}) Tj ET BT /F1 {F(Size)} Tf 200 {F(y)} Td {Literal(record.Entities[e])} Tj ET\n");
                    content.Append($"{F(Left)} {F(y - 3)} m 540 {F(y - 3)} l S\n");
                }
                // the rest of the text below the table
                var rest = lines.Skip(half.Count).ToList();
                for (var i = 0; i < rest.Count; i++)
                    content.Append($"BT /F1 {F(Size)} Tf {F(Left)} {F(y - (i + 1) * Leading)} Td {Literal(rest[i])} Tj ET\n");
                // a datum broken by the table between the halves of the text is no longer one piece
                shown = string.Join("\n", half) + "\n|\n" + string.Join("\n|\n", record.Entities) + "\n|\n" + string.Join("\n", rest);
                break;
            }
            case "tj-abstaende":
                // each word in pieces of one to three characters with small kerning between them, the spaces as offsets in TJ
                for (var i = 0; i < lines.Count; i++)
                {
                    var tj = new StringBuilder("[");
                    var words = lines[i].Split(' ');
                    for (var w = 0; w < words.Length; w++)
                    {
                        if (w > 0) tj.Append(-(250 + rng.Next(60)));
                        var word = words[w];
                        for (var at = 0; at < word.Length;)
                        {
                            var take = Math.Min(word.Length - at, 1 + rng.Next(3));
                            if (at > 0) tj.Append(rng.Next(-8, 9));
                            tj.Append(Literal(word.Substring(at, take)));
                            at += take;
                        }
                    }
                    content.Append($"BT /F1 {F(Size)} Tf {F(Left)} {F(Top - i * Leading)} Td {tj}] TJ ET\n");
                }
                break;
            case "einzelzeichen":
            {
                // every character at its own place, measured from the flowing text; every other line drawn from right to left
                var glyphs = Measure(Flowing(lines));
                var byLine = glyphs.GroupBy(g => Math.Round(g.Y0, 1)).OrderByDescending(g => g.Key).ToList();
                for (var l = 0; l < byLine.Count; l++)
                {
                    var row = byLine[l].Where(g => g.Text != " ").ToList();
                    if (l % 2 == 1) row.Reverse();
                    content.Append($"BT /F1 {F(Size)} Tf\n");
                    foreach (var g in row) content.Append($"1 0 0 1 {F(g.X0)} {F(g.Y0)} Tm {Literal(g.Text)} Tj\n");
                    content.Append("ET\n");
                }
                break;
            }
            case "doppelt":
                // a second, invisible text layer over the text (as a recognised scan over its own text), one point lower
                content.Append(Flowing(lines));
                content.Append(Flowing(lines, "3 Tr ").Replace($" {F(Left)} ", $" {F(Left + 0.5)} "));
                break;
            case "unter-bild":
            {
                // a grey image over the first datum: the text under it is hidden, its search hit must still go
                content.Append(Flowing(lines));
                var glyphs = Measure(content.ToString());
                var text = PageText.Create(glyphs);
                var first = record.Entities.Select(e => text.Find(e)).FirstOrDefault(h => h.Count > 0);
                if (first is { Count: > 0 })
                {
                    var b = text.RedactionAreas(first[0]).Aggregate((a, c) => new PdfRectangle(Math.Min(a.Left, c.Left), Math.Min(a.Bottom, c.Bottom), Math.Max(a.Right, c.Right), Math.Max(a.Top, c.Top)));
                    images.Add(("Grau", Enumerable.Repeat((byte)180, 64).ToArray(), 8, 8));
                    content.Append($"q {F(b.Right - b.Left + 4)} 0 0 {F(b.Top - b.Bottom + 4)} {F(b.Left - 2)} {F(b.Bottom - 2)} cm /Grau Do Q\n");
                }
                break;
            }
            case "ebene":
                // the data once more on a layer that is off: no search finds them, „Verborgenes schwärzen“ removes them
                content.Append(Flowing(lines));
                content.Append("/OC /Aus BDC\n");
                for (var e = 0; e < record.Entities.Count; e++)
                    content.Append($"BT /F1 {F(Size)} Tf {F(Left)} {F(120 - e * Leading)} Td {Literal(record.Entities[e])} Tj ET\n");
                content.Append("EMC\n");
                extraResources = " /Properties << /Aus 5 0 R >>";
                ocProperties = " /OCProperties << /OCGs [5 0 R] /D << /OFF [5 0 R] >> >>";
                markHidden = true;
                break;
            case "scan":
            {
                // the page as an image (144 dpi, grey) with the text as an invisible layer over it
                var (gray, w, h) = Rendered(Flowing(lines));
                images.Add(("Scan", gray, w, h));
                content.Append("q 612 0 0 842 0 0 cm /Scan Do Q\n");
                content.Append(Flowing(lines, "3 Tr "));
                break;
            }
        }

        var pdf = Pdf(content.ToString(), images, extraResources, ocProperties);
        // the search as „Suchen und schwärzen“ does it: all data at once, ignoring case; every hit a bar as the program marks it
        using var doc = PdfDocument.Open(pdf);
        var page = PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]));
        var queries = record.Entities.Select(PageText.NormalizeQuery).Where(q => q.Length > 0).Distinct().ToList();
        // the text in content order and, on a page that draws a line out of order, in the order of the places (as the program searches)
        var bars = new List<PdfRectangle>();
        var problems = new List<string>();
        var counts = new int[queries.Count];
        var taken = new HashSet<Core.Pdf.Graphics.TextGlyph>();
        foreach (var (source, inPlace) in page.InPlace is { } placed ? [(page, false), (placed, true)] : new[] { (page, false) })
        {
            var found = source.FindAll(queries, matchCase: false);
            for (var q = 0; q < queries.Count; q++)
                foreach (var hit in found[q])
                {
                    var glyphs = source.GlyphsOf(hit).ToList();
                    if (inPlace && glyphs.All(taken.Contains)) continue;
                    taken.UnionWith(glyphs);
                    bars.AddRange(source.RedactionAreas(hit));
                    counts[q]++;
                }
        }
        for (var q = 0; q < queries.Count; q++)
        {
            var expected = Count(form == "ebene" ? string.Join("\n", lines) : shown, record.Entities[q]);
            if (counts[q] < expected) problems.Add($"Suche findet „{Short(record.Entities[q])}“ {counts[q]} von {expected} Mal");
        }
        // a datum the file holds without any page content (a name such as „Gray“ in /DeviceGray) cannot be checked by the leak search
        var syntax = LeakBaseline.FoundWithoutContent(doc, record.Entities, null);
        var expect = record.Entities.Where(e => !syntax.Contains(e)).Select(e => new Expectation(0, e, Fate.Gone)).ToList();
        // words that stay: unique, not part of any datum, away from every bar
        foreach (var w in Words(string.Join(" ", lines)).Where(w => w.Length >= 6 && Count(record.Text, w) == 1 &&
                     !record.Entities.Any(e => e.Contains(w, StringComparison.OrdinalIgnoreCase) || w.Contains(e, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(_ => rng.Next()).Take(3))
        {
            var hits = page.Find(w);
            if (hits.Count == 1 && !page.RedactionAreas(hits[0]).Any(a => bars.Any(b => a.Left < b.Right && a.Right > b.Left && a.Bottom < b.Top && a.Top > b.Bottom)))
                expect.Add(new Expectation(0, w, Fate.Stays));
        }
        var name = $"docpii-{index + 1}-{form}";
        return (new RedactionCase(name, pdf, bars.Count > 0 ? new() { [0] = bars } : [], markHidden, expect, form), problems);
    }

    /// <summary>How often the word stands in the text, ignoring case and with any run of white space as one space (lines are broken anew).</summary>
    private static int Count(string text, string word)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        word = System.Text.RegularExpressions.Regex.Replace(word.Trim(), @"\s+", " ");
        var n = 0;
        for (var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + word.Length, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    private static string Short(string text) => text.Length <= 40 ? text : text[..40] + "…";

    private static IEnumerable<string> Words(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\p{L}+").Select(m => m.Value);

    /// <summary>The characters of a page with this content, as the program reads them.</summary>
    private static Core.Pdf.Graphics.TextGlyph[] Measure(string content)
    {
        using var doc = PdfDocument.Open(Pdf(content, [], "", null));
        return PageInterpreter.BuildText(doc, doc.Pages[0]);
    }

    /// <summary>A page with this content drawn at 144 dpi, in grey.</summary>
    private static (byte[] Gray, int Width, int Height) Rendered(string content)
    {
        using var doc = PdfDocument.Open(Pdf(content, [], "", null));
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, doc.Pages[0]), doc, doc.Pages[0], 2, new ImageCache());
        var gray = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                gray[y * bitmap.Width + x] = (byte)((c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000);
            }
        return (gray, bitmap.Width, bitmap.Height);
    }

    /// <summary>A one-page PDF: Helvetica as /F1, the images as grey XObjects, object 5 a layer.</summary>
    private static byte[] Pdf(string content, List<(string Name, byte[] Gray, int Width, int Height)> images, string extraResources, string? ocProperties)
    {
        var objects = new List<byte[]>();
        int Add(string body)
        {
            objects.Add(Encoding.Latin1.GetBytes(body));
            return objects.Count;
        }
        int AddStream(string dict, byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
            objects.Add([.. Encoding.Latin1.GetBytes($"<< {dict} /Filter /FlateDecode /Length {ms.Length} >>\nstream\n"), .. ms.ToArray(), .. "\nendstream"u8.ToArray()]);
            return objects.Count;
        }
        Add($"<< /Type /Catalog /Pages 2 0 R{ocProperties} >>");
        Add("<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Add("");   // the page, below
        Add("<< /Type /OCG /Name (Angaben) >>");
        var xobjects = new StringBuilder();
        foreach (var (name, gray, w, h) in images)
            xobjects.Append($"/{name} {AddStream($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /BitsPerComponent 8 /ColorSpace /DeviceGray", gray)} 0 R ");
        var contents = AddStream("", Encoding.Latin1.GetBytes(content));
        objects[3] = Encoding.Latin1.GetBytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 842] /Contents {contents} 0 R " +
                                              $"/Resources << /Font << /F1 3 0 R >> /XObject << {xobjects}>>{extraResources} >> >>");
        using var output = new MemoryStream();
        output.Write("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"u8);
        var offsets = new long[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = output.Position;
            output.Write(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n"));
            output.Write(objects[i]);
            output.Write("\nendobj\n"u8);
        }
        var xref = output.Position;
        var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) table.Append($"{o:D10} 00000 n \n");
        table.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        output.Write(Encoding.ASCII.GetBytes(table.ToString()));
        return output.ToArray();
    }
}
