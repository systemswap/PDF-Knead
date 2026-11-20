using System.Diagnostics;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Rendering;

namespace PdfKnead.Cli;

/// <summary>
/// „schwaerzpruefung … --fremd &lt;folder&gt;“ (docs/PRUEFUNGEN.md, section 6j): every redacted file is read once more by other readers, which
/// share none of our blind spots — what matters is not what our display shows but what any reader shows or extracts. Per case:
/// <list type="number">
/// <item>Text: each reader extracts the text of the redacted file; a word that must be gone and that any of them finds is a finding.</item>
/// <item>Picture: two readers draw the original and the redacted file at 150 dpi; outside the bars (and the areas our own check allows to
/// change) both must look alike — with the same tolerance as our own comparison —, and under the bars every picture must be dark.</item>
/// <item>Text that stays: a word that must stay and that a reader read in the original it still reads (copying and searching keep working).</item>
/// <item>Fonts: the font list before and after; no font that stays may change its kind (its outlines read as another format).</item>
/// </list>
/// The readers are local tools (not part of the program, its packages or the repository): the folder holds them, otherwise they are taken
/// from the PATH. A deviation of a reader is first a finding against the redaction; the strictest reader counts.
/// </summary>
internal static class ForeignReaders
{
    /// <summary>The folder with the readers (`--fremd`); null = off.</summary>
    internal static string? Folder { get; set; }

    private const double Dpi = 150;
    private const int Threshold = 64;   // as our own comparison: a difference below this many levels per channel does not count
    private const int MaxPages = 20;

    /// <summary>What our own check allows to change on a page (bars, the pieces they grew by, annotations that go, removed shapes …) and the
    /// bands in which the rest of a line may move.</summary>
    internal sealed record PageAllowance(List<PdfRectangle> Bars, List<PdfRectangle> Excluded, List<PdfRectangle> Bands);

    private static string Tool(string name)
    {
        if (Folder is { Length: > 0 } folder && File.Exists(Path.Combine(folder, name))) return Path.Combine(folder, name);
        return name;
    }

    /// <summary>Checks one case with the other readers.</summary>
    /// <param name="test">The case (words and bars).</param>
    /// <param name="originalDoc">The original, opened.</param>
    /// <param name="redacted">The redacted file.</param>
    /// <param name="password">Password of the file, if any.</param>
    /// <param name="pages">Per page what our own check allows to change.</param>
    /// <param name="withReasons">Reasons are written into the bars: then a bar is not entirely dark.</param>
    /// <returns>The findings.</returns>
    public static List<string> Check(RedactionCase test, PdfDocument originalDoc, byte[] redacted, string? password,
        IReadOnlyDictionary<int, PageAllowance> pages, bool withReasons)
    {
        var problems = new List<string>();
        var work = Path.Combine(Path.GetTempPath(), "pdf-knead-fremd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var before = Path.Combine(work, "vorher.pdf");
            var after = Path.Combine(work, "nachher.pdf");
            File.WriteAllBytes(before, test.Pdf);
            File.WriteAllBytes(after, redacted);

            // 1. text: each reader on its own
            var gone = test.Expectations.Where(e => e.Fate == Fate.Gone).Select(e => e.Word).Distinct().ToList();
            var stays = test.Expectations.Where(e => e.Fate == Fate.Stays && Regex.IsMatch(e.Word, @"^\w+$")).Select(e => e.Word).Distinct().ToList();
            List<(string Reader, string? Text)>? originalTexts = null;
            foreach (var (reader, text) in Texts(after, password))
            {
                if (text is null)
                {
                    // a finding only if the reader reads the original (a crafted original it refuses tells nothing about the redaction)
                    originalTexts ??= [.. Texts(before, password)];
                    if (originalTexts.First(t => t.Reader == reader).Text is not null) problems.Add($"{reader}: liest die geschwärzte Datei nicht");
                    else Console.WriteLine($"{test.Name}: {reader} liest schon das Original nicht (Hinweis)");
                    continue;
                }
                var tokens = new HashSet<string>(Regex.Matches(text, @"\w+").Select(m => m.Value), StringComparer.Ordinal);
                foreach (var word in gone)
                    if (Regex.IsMatch(word, @"^\w+$") ? tokens.Contains(word) : text.Contains(word, StringComparison.Ordinal))
                        problems.Add($"{reader} liest „{word}“, das weg sein muss");
                // what stays stays readable: a word the reader read in the original it still reads (lost Unicode mappings of text that is
                // still shown would turn it into garbage when copied or searched)
                if (stays.Count > 0)
                {
                    originalTexts ??= [.. Texts(before, password)];
                    if (originalTexts.First(t => t.Reader == reader).Text is { } was)
                    {
                        var read = new HashSet<string>(Regex.Matches(was, @"\w+").Select(m => m.Value), StringComparer.Ordinal);
                        var lost = stays.Where(w => read.Contains(w) && !tokens.Contains(w)).ToList();
                        if (lost.Count > 0) problems.Add($"{reader} liest {lost.Count} stehende Wörter nicht mehr („{string.Join("“, „", lost.Take(3))}“)");
                    }
                }
            }

            // 2. pictures: original and result through each reader
            var count = Math.Min(originalDoc.PageCount, MaxPages);
            using var redactedDoc = OpenOrNull(redacted, password);
            var ours = new Dictionary<(bool Redacted, int Page), (int Width, int Height, byte[] Rgb)?>();
            (int Width, int Height, byte[] Rgb)? Ours(bool isRedacted, int page)
            {
                if (!ours.TryGetValue((isRedacted, page), out var picture))
                    ours[(isRedacted, page)] = picture = isRedacted ? (redactedDoc is null ? null : OurPicture(redactedDoc, page)) : OurPicture(originalDoc, page);
                return picture;
            }
            foreach (var reader in new[] { "pdftoppm", "mutool" })
            {
                var one = Render(reader, before, password, count, Path.Combine(work, reader + "-vorher"));
                var two = Render(reader, after, password, count, Path.Combine(work, reader + "-nachher"));
                if (one is null || two is null)
                {
                    if (two is null && one is not null) problems.Add($"{reader}: zeichnet die geschwärzte Datei nicht");
                    continue;
                }
                for (var p = 0; p < count && p < one.Count && p < two.Count; p++)
                {
                    var allowance = pages.GetValueOrDefault(p) ?? new PageAllowance([], [], []);
                    // a reader that lays the page out in another size (a broken crop box it reads otherwise) draws nothing where our bars
                    // lie: its pictures cannot be compared by place
                    var ourSize = PageGeometry.DeviceSize(originalDoc.Pages[p], Dpi / 72);
                    if (Math.Abs(ourSize.Width - one[p].Width) > 2 || Math.Abs(ourSize.Height - one[p].Height) > 2)
                    {
                        Console.WriteLine($"{test.Name}: S. {p + 1}, {reader} legt die Seite anders an ({one[p].Width} × {one[p].Height} statt " +
                                          $"{ourSize.Width} × {ourSize.Height}), Bild nicht verglichen (Hinweis)");
                        continue;
                    }
                    if (Compare(reader, originalDoc.Pages[p], one[p], two[p], allowance, withReasons) is not { } finding) continue;
                    // the reader drew the original otherwise than we do and draws the result as we do: the change is the reader's own
                    // (a crafted image it decodes otherwise, written anew by the redaction) — shown, not counted. Every other
                    // difference counts against the redaction.
                    // The change lies only where the reader drew the original otherwise than our display, and the result is no further from it
                    // (a crafted image it decodes otherwise, written anew by the redaction): at least 95 % of the changed pixels within two
                    // pixels of that disagreement (without the partly covered last row)
                    var empty = new PageAllowance([], allowance.Excluded, allowance.Bands);
                    if (!finding.Contains("Balken nicht dunkel", StringComparison.Ordinal) && Ours(false, p) is { } ourBefore && Ours(true, p) is { } ourAfter &&
                        ReadersOwn(originalDoc.Pages[p], ourBefore, ourAfter, one[p], two[p], allowance, empty) is var (was, left, inside) && was > 3 && left <= was && inside >= 0.95)
                        Console.WriteLine($"{test.Name}: S. {p + 1}, {reader} zeichnete schon das Original anders als wir ({was} Pixel), die Änderung liegt dort " +
                                          $"({inside:P0}), das Ergebnis näher an uns (noch {left}) (Hinweis)");
                    else
                    {
                        // for looking into it: how far the reader is from our display, on the original and on the result
                        var against = Ours(false, p) is { } b0 && Ours(true, p) is { } a0 && !finding.Contains("Balken nicht dunkel", StringComparison.Ordinal)
                            ? $" (gegen unsere Anzeige: Original {Changed(originalDoc.Pages[p], b0, one[p], empty, true).Count}, Ergebnis " +
                              $"{Changed(originalDoc.Pages[p], a0, two[p], allowance, true).Count} Pixel)"
                            : "";
                        problems.Add($"S. {p + 1}, {finding}{against}");
                    }
                }
            }

            // 3. fonts
            var fontsBefore = Fonts(before, password);
            var fontsAfter = Fonts(after, password);
            if (fontsBefore is not null && fontsAfter is not null)
                foreach (var (name, kind) in fontsAfter)
                    if (fontsBefore.TryGetValue(name, out var was))
                    {
                        // not "no longer embedded": a font that shows nothing any more becomes a bare stand-in of the same name, which is right;
                        // what the loss of an embedded font changes in what is shown, the picture comparison finds
                        if (!was.Types.Overlaps(kind.Types)) problems.Add($"pdffonts: „{name}“ {string.Join("/", was.Types)} → {string.Join("/", kind.Types)}");
                    }
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch (IOException)
            {
            }
        }
        return problems;
    }

    // ------------------------------------------------------------ Text

    private static IEnumerable<(string Reader, string? Text)> Texts(string file, string? password)
    {
        var pw = password is { Length: > 0 };
        yield return ("pdftotext", Run(Tool("pdftotext"), [.. pw ? new[] { "-upw", password! } : [], "-q", "-enc", "UTF-8", file, "-"]));
        yield return ("gs", Run(Tool("gs"), ["-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", .. pw ? new[] { "-sPDFPassword=" + password } : [], "-sDEVICE=txtwrite",
            "-sOutputFile=-", file]));
        yield return ("mutool", Run(Tool("mutool"), ["draw", "-q", .. pw ? new[] { "-p", password! } : [], "-F", "txt", "-o", "-", file]));
    }

    // ------------------------------------------------------------ Pictures

    /// <summary>The pages drawn by a reader at 150 dpi (RGB), each within its crop box like our own drawing (pdftoppm draws the media box
    /// unless told otherwise); null if it fails.</summary>
    private static List<(int Width, int Height, byte[] Rgb)>? Render(string reader, string file, string? password, int count, string prefix)
    {
        var pw = password is { Length: > 0 };
        string? output = reader == "pdftoppm"
            ? Run(Tool("pdftoppm"), [.. pw ? new[] { "-upw", password! } : [], "-q", "-cropbox", "-r", "150", "-f", "1", "-l", count.ToString(), file, prefix])
            : Run(Tool("mutool"), ["draw", "-q", .. pw ? new[] { "-p", password! } : [], "-r", "150", "-c", "rgb", "-o", prefix + "-%d.pnm", file, $"1-{count}"]);
        if (output is null) return null;
        var folder = Path.GetDirectoryName(prefix)!;
        var name = Path.GetFileName(prefix);
        // pdftoppm numbers pages with as many digits as the last page has; mutool from 1 without zeros
        var files = Directory.GetFiles(folder, name + "-*").Select(f => (File: f, Number: int.TryParse(Path.GetFileNameWithoutExtension(f)[(name.Length + 1)..], out var n) ? n : -1))
            .Where(f => f.Number > 0).OrderBy(f => f.Number).ToList();
        var pages = new List<(int, int, byte[])>();
        foreach (var (f, _) in files)
        {
            if (ReadPnm(File.ReadAllBytes(f)) is not { } image) return null;
            pages.Add(image);
            File.Delete(f);
        }
        return pages;
    }

    private static PdfDocument? OpenOrNull(byte[] data, string? password)
    {
        try
        {
            return password is { Length: > 0 } ? PdfDocument.Open(data, password) : PdfDocument.Open(data);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>Our own drawing of a page at 150 dpi in its crop box (RGB), as the readers draw it; null if it cannot be drawn.</summary>
    private static (int Width, int Height, byte[] Rgb)? OurPicture(PdfDocument doc, int index)
    {
        try
        {
            var page = doc.Pages[index];
            using var bitmap = SkiaRenderer.RenderPage(Core.Pdf.Content.PageInterpreter.Build(doc, page), doc, page, Dpi / 72, new ImageCache());
            // as plain RGB, whatever the bitmap's byte order: copied once into an RGBA bitmap of known layout
            using var rgba = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(bitmap.Width, bitmap.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul));
            if (!bitmap.CopyTo(rgba, SkiaSharp.SKColorType.Rgba8888)) return null;
            var pixels = rgba.GetPixelSpan();
            var rgb = new byte[bitmap.Width * bitmap.Height * 3];
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                {
                    int i = (y * bitmap.Width + x) * 3, j = y * rgba.RowBytes + x * 4;
                    (rgb[i], rgb[i + 1], rgb[i + 2]) = (pixels[j], pixels[j + 1], pixels[j + 2]);
                }
            return (bitmap.Width, bitmap.Height, rgb);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A binary PPM (P6) or PGM (P5) as RGB.</summary>
    private static (int Width, int Height, byte[] Rgb)? ReadPnm(byte[] data)
    {
        var pos = 0;
        string Token()
        {
            while (pos < data.Length && (char.IsWhiteSpace((char)data[pos]) || data[pos] == '#'))
                if (data[pos] == '#') while (pos < data.Length && data[pos] != '\n') pos++;
                else pos++;
            var start = pos;
            while (pos < data.Length && !char.IsWhiteSpace((char)data[pos])) pos++;
            return System.Text.Encoding.ASCII.GetString(data, start, pos - start);
        }
        var magic = Token();
        if (magic is not ("P6" or "P5") || !int.TryParse(Token(), out var w) || !int.TryParse(Token(), out var h) || !int.TryParse(Token(), out var max) || max != 255)
            return null;
        pos++;
        var channels = magic == "P6" ? 3 : 1;
        if (data.Length - pos < (long)w * h * channels) return null;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
            for (var c = 0; c < 3; c++) rgb[i * 3 + c] = data[pos + i * channels + (channels == 3 ? c : 0)];
        return (w, h, rgb);
    }

    /// <summary>Compares a page of the original and of the result as a reader draws them; a description of the first finding, or null.</summary>
    private static string? Compare(string reader, PdfPage page, (int Width, int Height, byte[] Rgb) a, (int Width, int Height, byte[] Rgb) b, PageAllowance allowance,
        bool withReasons)
    {
        if (Math.Abs(a.Width - b.Width) > 1 || Math.Abs(a.Height - b.Height) > 1) return $"{reader}: andere Größe ({a.Width} × {a.Height} → {b.Width} × {b.Height})";
        var (count, most, mx, my) = Changed(page, a, b, allowance, false);
        if (count > 3) return $"{reader}: sichtbar verändert außerhalb, {count} Pixel bis {most} Stufen, zuerst bei ({mx * 72 / Dpi:0}, {my * 72 / Dpi:0}) pt (von oben links)";
        return withReasons ? null : DarkBars(reader, page, b, allowance);
    }

    /// <summary>
    /// Pixels that differ between two pictures of a page outside what <paramref name="allowance"/> excludes, by the tolerance of our own
    /// comparison; with <paramref name="trimEdge"/> without the last row and column (a reader and our display round the size of a page that
    /// is not a whole number of pixels differently, and that row is only partly covered).
    /// </summary>
    private static (int Count, int Most, int X, int Y) Changed(PdfPage page, (int Width, int Height, byte[] Rgb) a, (int Width, int Height, byte[] Rgb) b,
        PageAllowance allowance, bool trimEdge) => Changed(page, a, b, allowance, trimEdge, null);

    private static (int Count, int Most, int X, int Y) Changed(PdfPage page, (int Width, int Height, byte[] Rgb) a, (int Width, int Height, byte[] Rgb) b,
        PageAllowance allowance, bool trimEdge, bool[]? where)
    {
        int width = Math.Min(a.Width, b.Width) - (trimEdge ? 1 : 0), height = Math.Min(a.Height, b.Height) - (trimEdge ? 1 : 0);
        if (width <= 0 || height <= 0) return (0, 0, 0, 0);
        var toDevice = PageGeometry.PageToDevice(page, Dpi / 72);
        var skip = new bool[width * height];
        void Mark(PdfRectangle r, double grow)
        {
            var d = toDevice.TransformBounds(RectD.FromLtrb(Math.Max(r.Left - grow, -1e5), Math.Max(r.Bottom - grow, -1e5), Math.Min(r.Right + grow, 1e5), Math.Min(r.Top + grow, 1e5)));
            int x0 = Math.Max(0, (int)Math.Floor(d.Left) - 1), x1 = Math.Min(width, (int)Math.Ceiling(d.Right) + 1);
            int y0 = Math.Max(0, (int)Math.Floor(d.Top) - 1), y1 = Math.Min(height, (int)Math.Ceiling(d.Bottom) + 1);
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++) skip[y * width + x] = true;
        }
        foreach (var r in allowance.Excluded) Mark(r, 2);
        foreach (var r in allowance.Bands) Mark(r, 0);

        int count = 0, most = 0, mx = 0, my = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (skip[y * width + x]) continue;
                int i = (y * a.Width + x) * 3, j = (y * b.Width + x) * 3;
                var diff = Math.Max(Math.Abs(a.Rgb[i] - b.Rgb[j]), Math.Max(Math.Abs(a.Rgb[i + 1] - b.Rgb[j + 1]), Math.Abs(a.Rgb[i + 2] - b.Rgb[j + 2])));
                if (diff < Threshold) continue;
                // smoothing of edges only, as in our own comparison: each value lies within its neighbours in the other picture
                if (Near(a, x, y, b.Rgb, j) && Near(b, x, y, a.Rgb, i)) continue;
                if (count++ == 0) (mx, my) = (x, y);
                most = Math.Max(most, diff);
                if (where is not null && y * a.Width + x < where.Length) where[y * a.Width + x] = true;
            }
        return (count, most, mx, my);
    }

    /// <summary>
    /// How far the reader is from our display on the original (<c>Was</c>) and on the result (<c>Left</c>), and which share of the pixels the
    /// redaction changed in the reader's picture lies within two pixels of where it drew the original otherwise than we do.
    /// </summary>
    private static (int Was, int Left, double Inside) ReadersOwn(PdfPage page, (int Width, int Height, byte[] Rgb) ourBefore, (int Width, int Height, byte[] Rgb) ourAfter,
        (int Width, int Height, byte[] Rgb) readerBefore, (int Width, int Height, byte[] Rgb) readerAfter, PageAllowance allowance, PageAllowance empty)
    {
        var disagree = new bool[readerBefore.Width * readerBefore.Height];
        var was = Changed(page, ourBefore, readerBefore, empty, true, disagree).Count;
        var left = Changed(page, ourAfter, readerAfter, allowance, true).Count;
        var change = new bool[readerBefore.Width * readerBefore.Height];
        var changed = Changed(page, readerBefore, readerAfter, allowance, false, change).Count;
        if (changed == 0) return (was, left, 1);
        int w = readerBefore.Width, h = readerBefore.Height, inside = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (!change[y * w + x]) continue;
                var near = false;
                for (var dy = -2; dy <= 2 && !near; dy++)
                    for (var dx = -2; dx <= 2 && !near; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        near = (uint)nx < (uint)w && (uint)ny < (uint)h && disagree[ny * w + nx];
                    }
                if (near) inside++;
            }
        return (was, left, (double)inside / changed);
    }

    /// <summary>Under the bars every picture must be dark (a pixel and a half inside their edge); a description of the first light bar, or null.</summary>
    private static string? DarkBars(string reader, PdfPage page, (int Width, int Height, byte[] Rgb) b, PageAllowance allowance)
    {
        int width = b.Width, height = b.Height;
        var toDevice = PageGeometry.PageToDevice(page, Dpi / 72);
        foreach (var bar in allowance.Bars)
        {
            var d = toDevice.TransformBounds(RectD.FromLtrb(bar.Left, bar.Bottom, bar.Right, bar.Top));
            int x0 = Math.Max(0, (int)Math.Ceiling(d.Left + 1.5)), x1 = Math.Min(width, (int)Math.Floor(d.Right - 1.5));
            int y0 = Math.Max(0, (int)Math.Ceiling(d.Top + 1.5)), y1 = Math.Min(height, (int)Math.Floor(d.Bottom - 1.5));
            int light = 0, all = 0;
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    all++;
                    var j = (y * b.Width + x) * 3;
                    if (Math.Max(b.Rgb[j], Math.Max(b.Rgb[j + 1], b.Rgb[j + 2])) > 96) light++;
                }
            if (all > 0 && light > Math.Max(3, all / 100))
                return $"{reader}: Balken nicht dunkel ({light} von {all} Pixeln hell), Balken ({bar.Left:0.#}, {bar.Bottom:0.#}) – ({bar.Right:0.#}, {bar.Top:0.#})";
        }
        return null;
    }

    private static bool Near((int Width, int Height, byte[] Rgb) image, int x, int y, byte[] other, int at)
    {
        for (var c = 0; c < 3; c++)
        {
            int low = 255, high = 0;
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)image.Width || (uint)ny >= (uint)image.Height) continue;
                    var v = image.Rgb[(ny * image.Width + nx) * 3 + c];
                    low = Math.Min(low, v);
                    high = Math.Max(high, v);
                }
            if (other[at + c] < low - Threshold || other[at + c] > high + Threshold) return false;
        }
        return true;
    }

    // ------------------------------------------------------------ Fonts

    private static Dictionary<string, (HashSet<string> Types, bool Embedded, bool Unicode)>? Fonts(string file, string? password)
    {
        var pw = password is { Length: > 0 };
        if (Run(Tool("pdffonts"), [.. pw ? new[] { "-upw", password! } : [], "-l", MaxPages.ToString(), file]) is not { } table) return null;
        var fonts = new Dictionary<string, (HashSet<string> Types, bool Embedded, bool Unicode)>(StringComparer.Ordinal);
        var lines = table.Split('\n');
        // name type encoding emb sub uni object ID: the columns stand where the dashes of the second line are (the type holds spaces,
        // "Type 1C"; the object ID is one word for a font written directly into its dictionary, "[none]", otherwise two)
        if (lines.Length < 2) return fonts;
        var columns = Regex.Matches(lines[1], "-+").Select(m => (Start: m.Index, End: m.Index + m.Length)).ToList();
        if (columns.Count < 7) return null;
        string Column(string line, int k)
        {
            var start = columns[k].Start;
            var end = k + 1 < columns.Count ? columns[k + 1].Start : line.Length;
            return start >= line.Length ? "" : line[start..Math.Min(end, line.Length)].Trim();
        }
        foreach (var line in lines.Skip(2))
        {
            if (line.Trim().Length == 0) continue;
            var name = Column(line, 0);
            var type = Column(line, 1);
            bool embedded = Column(line, 3) == "yes", unicode = Column(line, 5) == "yes";
            // by name (the object numbers change when the file is written anew); several fonts of one name: what any of them has (the
            // redaction adds bare stand-ins under the names of fonts that show nothing any more, beside the font itself)
            fonts[name] = fonts.TryGetValue(name, out var known) ? ([.. known.Types, type], known.Embedded || embedded, known.Unicode || unicode) : ([type], embedded, unicode);
        }
        return fonts;
    }

    // ------------------------------------------------------------ Running a reader

    private static string? Run(string tool, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in arguments) info.ArgumentList.Add(a);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(true);
                return null;
            }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
