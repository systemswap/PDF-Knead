using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Cli;

/// <summary>
/// Redaction of scanned pages and the copies an image can leave behind (docs/PRUEFUNGEN.md, section 6j „Bildfallen“): every case is a page
/// that is one image — lines of words in coloured ink on paper with noise — written in one of five ways (JPEG, JPEG with an EXIF thumbnail
/// in its APP1 segment, Flate RGB, Flate grey, 1 bit), and with traps chosen by the seed: a thumbnail of the page (/Thumb), an alternate
/// image, a thumbnail in the image's XMP metadata, an unused copy in the resources, a soft mask in the shape of the text, an invisible text
/// layer, an older revision in which a word was not yet blacked out, and the image data once more behind the end of the file. One to three
/// words are redacted — with bars over their ink, or with <c>--modelle</c> over the words a text recognition read there, as when a user
/// marks recognised text.
/// </summary>
/// <remarks>
/// Besides the rules of <see cref="RedactionCheck"/> (drawn images uniform under the bars, words gone from the file, nothing else changed):
/// no image anywhere in the result (drawn or not, of any size) shows anything in a redacted area; no piece of the encoded data of the
/// original images is in the result (also not of the older revision or behind the end); no JPEG keeps an EXIF segment, no metadata an XMP
/// thumbnail, no page a /Thumb; the result has one revision and nothing behind its end; and no ink of a redacted word lies outside the bars
/// (with a text recognition: the read word's box is smaller than its ink — ascenders, descenders).
/// </remarks>
internal static class ImageTrapCases
{
    private const int PageWidth = 612, PageHeight = 792;
    private const int Width = 1020, Height = 1320;   // 120 dpi
    private const double PxPerPt = Width / (double)PageWidth;

    /// <summary>Ink of the scan: a dark blue, told apart from a black bar.</summary>
    private static readonly SKColor InkColor = new(25, 30, 110);

    /// <summary>A redacted word of a case: its ink (pixels of the original image) and the areas it must lie under.</summary>
    private sealed record Target(string Word, SKRectI Box, List<(int X, int Y)> Ink);

    /// <summary>What the checks need beyond the case: the redacted words, the encoded image data that must not stay, the traps set.</summary>
    private sealed record Traps(List<Target> Targets, List<PdfRectangle> EarlierRedacted, List<byte[]> OriginalImages, string Description);

    /// <summary>Runs <paramref name="count"/> cases from seed <paramref name="first"/>.</summary>
    /// <param name="keep">Folder for originals and results, or null.</param>
    /// <param name="ocr">A text recognition: the bars come from what it reads (otherwise from the ink).</param>
    /// <returns>0 if all passed.</returns>
    public static int Run(int count, int first, string? keep, ITextRecognizer? ocr)
    {
        int ok = 0, failed = 0;
        var watch = Stopwatch.StartNew();
        var work = keep ?? Path.Combine(Path.GetTempPath(), "pdf-knead-bildfallen");
        Directory.CreateDirectory(work);
        for (var seed = first; seed < first + count; seed++)
        {
            List<string> problems;
            string name = $"bild-{seed}", description = "";
            try
            {
                var (test, traps) = Make(seed, ocr);
                description = test.Description;
                if (keep is not null) File.WriteAllBytes(Path.Combine(keep, name + ".pdf"), test.Pdf);
                problems = RedactionCheck.Check(test, keep: work);
                var resultPath = Path.Combine(work, name + "-geschwaerzt.pdf");
                if (File.Exists(resultPath)) problems.AddRange(CheckTraps(traps, test, File.ReadAllBytes(resultPath)));
                else problems.Add("kein Ergebnis");
                if (keep is null)
                    foreach (var f in Directory.EnumerateFiles(work, name + "-*")) File.Delete(f);
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
            Console.WriteLine($"{name}: {string.Join("; ", problems.Take(4))}  [{description}]");
        }
        Console.WriteLine($"Fälle: {ok + failed}, bestanden: {ok}, Abweichungen: {failed}, Zeit {watch.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------ Cases

    private static (RedactionCase Case, Traps Traps) Make(int seed, ITextRecognizer? ocr)
    {
        var rng = new Random(seed);
        var encoding = seed % 5;   // every way of writing in turn
        bool Pick() => rng.Next(2) == 0;
        bool thumb = Pick(), alternate = Pick(), xmp = Pick(), unused = Pick(), mask = encoding is 2 or 3 && Pick(), layer = ocr is null && Pick(),
             revision = Pick(), behind = Pick();
        // a text layer that lies up to 3 points beside the ink (as recognitions place it), and the bars made from it as when marking its text
        var shifted = layer && Pick();
        double dx = shifted ? rng.NextDouble() * 6 - 3 : 0, dy = shifted ? rng.NextDouble() * 6 - 3 : 0;

        // the scan: paper with noise, lines of unique words in ink
        using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var noise = new Random(seed * 7 + 1);
        var pixels = bitmap.GetPixelSpan();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var v = 238 + noise.Next(14);
            pixels[i] = (byte)v;
            pixels[i + 1] = (byte)(v - 3);
            pixels[i + 2] = (byte)(v - 10);
            pixels[i + 3] = 255;
        }
        var words = new List<(string Word, SKRectI Box, float X, float Y, float Size)>();
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = InkColor, IsAntialias = true })
        {
            var y = 110f;
            var counter = 0;
            while (y < Height - 80)
            {
                var size = 26 + rng.Next(14);
                using var font = new SKFont(SKTypeface.Default, size);
                var x = 70f;
                for (var k = 0; k < 3 + rng.Next(3); k++)
                {
                    var word = WordOf(seed, ++counter, rng);
                    font.MeasureText(word, out var bounds);
                    if (x + bounds.Right > Width - 60) break;
                    canvas.DrawText(word, x, y, SKTextAlign.Left, font, paint);
                    words.Add((word, new SKRectI((int)Math.Floor(x + bounds.Left) - 1, (int)Math.Floor(y + bounds.Top) - 1,
                        (int)Math.Ceiling(x + bounds.Right) + 1, (int)Math.Ceiling(y + bounds.Bottom) + 1), x, y, size));
                    x += bounds.Right + 22 + rng.Next(20);
                }
                y += size * 2.1f + rng.Next(20);
            }
        }
        var original = Rgb(bitmap);
        var targetIndices = Enumerable.Range(0, words.Count).OrderBy(_ => rng.Next()).Take(1 + rng.Next(3)).ToList();
        var targets = targetIndices.Select(i => new Target(words[i].Word, words[i].Box, InkOf(original, words[i].Box))).ToList();
        // a word blacked out in a later revision by another program: the older revision still shows it
        var earlierIndex = revision ? Enumerable.Range(0, words.Count).Except(targetIndices).OrderBy(_ => rng.Next()).FirstOrDefault(-1) : -1;
        var earlier = new List<PdfRectangle>();
        var current = original;
        if (earlierIndex >= 0)
        {
            current = (byte[])original.Clone();
            var box = words[earlierIndex].Box;
            for (var py = Math.Max(0, box.Top - 2); py < Math.Min(Height, box.Bottom + 2); py++)
                for (var px = Math.Max(0, box.Left - 2); px < Math.Min(Width, box.Right + 2); px++)
                    current.AsSpan((py * Width + px) * 3, 3).Clear();
            earlier.Add(ToPage(box));
        }

        var description = new StringBuilder(new[] { "JPEG", "JPEG mit EXIF-Miniatur", "Flate RGB", "Flate Grau", "1 Bit" }[encoding]);
        var pdf = new PdfBuilder();
        var originalImages = new List<byte[]>();
        (string Dict, byte[] Data) Encode(byte[] rgb, int w, int h)
        {
            switch (encoding)
            {
                case 0 or 1:
                    var jpeg = SkiaImageCodec.Instance.EncodeJpeg(w, h, 3, rgb, 90) ?? throw new InvalidOperationException("JPEG");
                    if (encoding == 1) jpeg = WithExifThumbnail(jpeg, rgb, w, h);
                    return ($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg);
                case 2:
                    return ($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", Deflate(rgb));
                case 3:
                    return ($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", Deflate(Gray(rgb)));
                default:
                    return ($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /FlateDecode", Deflate(Bits(rgb, w, h)));
            }
        }

        // the image as the current revision has it, with its traps
        var (dict, data) = Encode(current, Width, Height);
        var extra = new StringBuilder();
        if (mask)
        {
            // opaque paper, the ink a little transparent: the soft mask carries the shape of the words
            var alpha = new byte[Width * Height];
            for (var i = 0; i < alpha.Length; i++) alpha[i] = current[i * 3 + 2] < 160 ? (byte)230 : (byte)255;
            extra.Append($" /SMask {pdf.Stream($"/Type /XObject /Subtype /Image /Width {Width} /Height {Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", Deflate(alpha))} 0 R");
            description.Append(", Maske");
        }
        if (alternate)
        {
            var (ad, adata) = Encode(current, Width, Height);
            var copy = pdf.Stream(ad, adata);
            originalImages.Add(adata);
            extra.Append($" /Alternates [<< /Image {copy} 0 R >>]");
            description.Append(", Ersatzbild");
        }
        if (xmp)
        {
            var small = Scale(current, Width, Height, 4);
            var jpeg = SkiaImageCodec.Instance.EncodeJpeg(Width / 4, Height / 4, 3, small, 80)!;
            var packet = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                         "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:xmpGImg=\"http://ns.adobe.com/xap/1.0/g/img/\"><xmp:Thumbnails><rdf:Alt><rdf:li rdf:parseType=\"Resource\">" +
                         $"<xmpGImg:width>{Width / 4}</xmpGImg:width><xmpGImg:height>{Height / 4}</xmpGImg:height><xmpGImg:format>JPEG</xmpGImg:format>" +
                         $"<xmpGImg:image>{Convert.ToBase64String(jpeg)}</xmpGImg:image></rdf:li></rdf:Alt></xmp:Thumbnails></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
            extra.Append($" /Metadata {pdf.Raw($"/Type /Metadata /Subtype /XML", Encoding.UTF8.GetBytes(packet))} 0 R");
            originalImages.Add(jpeg);
            description.Append(", XMP-Miniatur");
        }
        var image = pdf.Stream(dict + extra, data);
        originalImages.Add(data);
        var xobjects = $"/Im {image} 0 R";
        if (unused)
        {
            var (ud, udata) = Encode(current, Width, Height);
            xobjects += $" /Spare {pdf.Stream(ud, udata)} 0 R";
            originalImages.Add(udata);
            description.Append(", unbenutzte Kopie");
        }
        var helvetica = pdf.Object("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var content = new StringBuilder($"q {PageWidth} 0 0 {PageHeight} 0 0 cm /Im Do Q");
        var expectations = new List<Expectation>();
        if (layer)
        {
            // an invisible text layer as a recognition writes it: each word over its ink
            foreach (var w in words)
            {
                var r = ToPage(w.Box);
                var size = w.Size / PxPerPt;
                var natural = Helvetica(w.Word) * size;
                var scale = natural > 0 ? (r.Right - r.Left) / natural * 100 : 100;
                content.Append(FormattableString.Invariant($" BT 3 Tr /F1 {size:0.##} Tf {scale:0.#} Tz {r.Left + dx:0.##} {PageHeight - w.Y / PxPerPt + dy:0.##} Td ({w.Word}) Tj ET"));
                // a redacted word goes from the layer as well; the word another program blacked out only in the image stays in the layer
                // (the program was not asked to redact it) — the other words are checked by the rules of the redaction itself
                if (targets.Any(t => t.Word == w.Word)) expectations.Add(new Expectation(0, w.Word, Fate.Gone));
            }
            description.Append(shifted ? FormattableString.Invariant($", Textschicht um ({dx:0.0}, {dy:0.0}) pt verschoben") : ", Textschicht");
        }
        var contents = pdf.Stream("", Encoding.Latin1.GetBytes(content.ToString()));
        string thumbEntry = "";
        if (thumb)
        {
            var small = Scale(current, Width, Height, 8);
            var t = Deflate(small);
            thumbEntry = $" /Thumb {pdf.Stream($"/Width {Width / 8} /Height {Height / 8} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", t)} 0 R";
            originalImages.Add(t);
            description.Append(", Vorschaubild");
        }
        var pageNumber = pdf.Reserve();
        var pages = pdf.Object($"<< /Type /Pages /Kids [{pageNumber} 0 R] /Count 1 >>");
        pdf.Set(pageNumber, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] /Resources << /XObject << {xobjects} >> /Font << /F1 {helvetica} 0 R >> >> /Contents {contents} 0 R{thumbEntry} >>");
        var catalog = pdf.Object($"<< /Type /Catalog /Pages {pages} 0 R >>");
        byte[] file;
        if (earlierIndex >= 0)
        {
            // the first revision shows the image without the black box; the second replaces the image object
            var (od, odata) = Encode(original, Width, Height);
            originalImages.Add(odata);
            file = pdf.WithRevision(catalog, image, dict + extra, data, od + extra, odata);
            description.Append(", ältere Revision");
        }
        else file = pdf.Write(catalog);
        if (behind)
        {
            // as when a tool shortens an image file in place: the old data stays behind the end
            file = [.. file, .. originalImages[^1]];
            description.Append(", Daten hinter dem Ende");
        }

        // the bars: over the ink of each word, or over what a text recognition read there
        var bars = new Dictionary<int, List<PdfRectangle>>();
        if (ocr is not null)
        {
            (file, var read) = WithRecognition(file, ocr);
            bars[0] = [];
            foreach (var t in targets)
            {
                var areas = ReadAreas(file, read, ToPage(t.Box));
                bars[0].AddRange(areas);
                if (areas.Count == 0) description.Append($", „{t.Word}“ nicht gelesen");
            }
            description.Append(", Balken aus Texterkennung");
        }
        else if (shifted)
        {
            using var withLayer = PdfDocument.Open(file);
            var text = PageText.Create(PageInterpreter.BuildText(withLayer, withLayer.Pages[0]));
            bars[0] = [.. targets.SelectMany(t => ReadAreas(file, text, ToPage(t.Box)))];
            description.Append(", Balken aus der Textschicht");
        }
        else bars[0] = [.. targets.Select(t => Inflate(ToPage(t.Box), 1))];
        var test = new RedactionCase($"bild-{seed}", file, bars, false, expectations, description.ToString());
        return (test, new Traps(targets, earlier, originalImages, description.ToString()));
    }

    /// <summary>A word of letters only, unique in the case (a leak search finds exactly it).</summary>
    private static string WordOf(int seed, int n, Random rng)
    {
        string[] syllables = ["ma", "ster", "ko", "len", "dra", "bu", "fin", "gel", "tor", "pah", "jin", "quy", "lo", "berg"];
        var sb = new StringBuilder();
        for (var k = 0; k < 2 + rng.Next(2); k++) sb.Append(syllables[rng.Next(syllables.Length)]);
        for (var x = seed * 131 + n * 7; sb.Length < 9; x /= 26) sb.Append((char)('a' + Math.Abs(x % 26)));
        sb.Append((char)('a' + n % 26)).Append((char)('a' + n / 26 % 26));
        return sb.ToString();
    }

    /// <summary>The ink pixels of a word: clearly bluer than the paper.</summary>
    private static List<(int X, int Y)> InkOf(byte[] rgb, SKRectI box)
    {
        var ink = new List<(int, int)>();
        for (var y = Math.Max(0, box.Top); y < Math.Min(Height, box.Bottom); y++)
            for (var x = Math.Max(0, box.Left); x < Math.Min(Width, box.Right); x++)
                if (IsInk(rgb, (y * Width + x) * 3))
                    ink.Add((x, y));
        return ink;
    }

    /// <summary>A pixel at least half inked: red well below the paper, blue well above red.</summary>
    private static bool IsInk(byte[] rgb, int at) => rgb[at] < 140 && rgb[at + 2] - rgb[at] > 30;

    private static PdfRectangle ToPage(SKRectI box) =>
        new(box.Left / PxPerPt, PageHeight - box.Bottom / PxPerPt, box.Right / PxPerPt, PageHeight - box.Top / PxPerPt);

    private static PdfRectangle Inflate(PdfRectangle r, double d) => new(r.Left - d, r.Bottom - d, r.Right + d, r.Top + d);

    /// <summary>Approximate width of a word in Helvetica at size 1 (average letter widths).</summary>
    private static double Helvetica(string word) => word.Sum(c => c is 'i' or 'j' or 'l' ? 0.22 : c is 'f' or 't' or 'r' ? 0.3 : c is 'm' or 'w' ? 0.83 : 0.55);

    private static byte[] Rgb(SKBitmap bitmap)
    {
        var src = bitmap.GetPixelSpan();
        var rgb = new byte[Width * Height * 3];
        for (int i = 0, j = 0; i < src.Length; i += 4, j += 3)
        {
            rgb[j] = src[i];
            rgb[j + 1] = src[i + 1];
            rgb[j + 2] = src[i + 2];
        }
        return rgb;
    }

    private static byte[] Gray(byte[] rgb)
    {
        var gray = new byte[rgb.Length / 3];
        for (var i = 0; i < gray.Length; i++) gray[i] = (byte)((rgb[i * 3] * 299 + rgb[i * 3 + 1] * 587 + rgb[i * 3 + 2] * 114) / 1000);
        return gray;
    }

    /// <summary>1 bit per pixel, 0 = black (the ink and anything dark).</summary>
    private static byte[] Bits(byte[] rgb, int w, int h)
    {
        var row = (w + 7) / 8;
        var bits = new byte[row * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var at = (y * w + x) * 3;
                if (rgb[at] * 299 + rgb[at + 1] * 587 + rgb[at + 2] * 114 >= 128_000) bits[y * row + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        return bits;
    }

    /// <summary>A smaller copy (each pixel the mean of a square of <paramref name="f"/> × <paramref name="f"/>).</summary>
    private static byte[] Scale(byte[] rgb, int w, int h, int f)
    {
        int sw = w / f, sh = h / f;
        var small = new byte[sw * sh * 3];
        for (var y = 0; y < sh; y++)
            for (var x = 0; x < sw; x++)
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < f; dy++)
                        for (var dx = 0; dx < f; dx++)
                            sum += rgb[((y * f + dy) * w + x * f + dx) * 3 + c];
                    small[(y * sw + x) * 3 + c] = (byte)(sum / (f * f));
                }
        return small;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>
    /// The JPEG with an APP1 segment after its start: EXIF (TIFF little endian) whose second directory holds a thumbnail of the picture as
    /// JPEG (JPEGInterchangeFormat, CIPA DC-008-2016, 4.6.3) — what cameras and scanners write and many tools keep.
    /// </summary>
    private static byte[] WithExifThumbnail(byte[] jpeg, byte[] rgb, int w, int h)
    {
        var thumb = SkiaImageCodec.Instance.EncodeJpeg(w / 8, h / 8, 3, Scale(rgb, w, h, 8), 75)!;
        using var tiff = new MemoryStream();
        var bw = new BinaryWriter(tiff);
        bw.Write("II"u8);
        bw.Write((ushort)42);
        bw.Write(8u);              // IFD0
        bw.Write((ushort)0);       // no entries
        bw.Write(14u);             // IFD1 at 14
        bw.Write((ushort)3);
        void Entry(ushort tag, ushort type, uint value)
        {
            bw.Write(tag);
            bw.Write(type);
            bw.Write(1u);
            bw.Write(value);
        }
        var thumbAt = 14u + 2 + 3 * 12 + 4;
        Entry(0x0103, 3, 6);                           // Compression: JPEG
        Entry(0x0201, 4, thumbAt);                     // JPEGInterchangeFormat
        Entry(0x0202, 4, (uint)thumb.Length);          // JPEGInterchangeFormatLength
        bw.Write(0u);                                  // no further IFD
        bw.Write(thumb);
        var payload = (byte[])[.. "Exif\0\0"u8, .. tiff.ToArray()];
        if (payload.Length + 2 > 0xFFFF) throw new InvalidOperationException("EXIF zu groß");
        var segment = (byte[])[0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)((payload.Length + 2) & 0xFF), .. payload];
        return [.. jpeg.AsSpan(0, 2), .. segment, .. jpeg.AsSpan(2)];
    }

    // ------------------------------------------------------------ Text recognition

    /// <summary>The file with a text layer as the program writes it after „Texterkennung“, and the text read.</summary>
    private static (byte[] File, PageText Read) WithRecognition(byte[] file, ITextRecognizer ocr)
    {
        using var doc = PdfDocument.Open(file);
        var items = PageItem.From(doc);
        var images = new ImageCache(64L << 20);
        var layers = new Dictionary<int, PdfStream>();
        if (PageRecognition.Recognize(items[0], ocr, images) is var (lines, box) && OcrTextLayer.Form(lines, box) is { } form) layers[0] = form;
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance, TextLayers = layers });
        var written = output.ToArray();
        using var again = PdfDocument.Open(written);
        return (written, PageText.Create(PageInterpreter.BuildText(again, again.Pages[0])));
    }

    /// <summary>
    /// The areas of the read text over an ink box, as the program marks them (<see cref="PageText.RedactionAreas"/>, grown to the ink by
    /// <see cref="InkSnap"/>): the words from the first to the last character whose centre lies in the box.
    /// </summary>
    private static List<PdfRectangle> ReadAreas(byte[] file, PageText text, PdfRectangle ink)
    {
        int first = -1, last = -1;
        for (var c = 0; c < text.Text.Length; c++)
        {
            if (char.IsWhiteSpace(text.Text[c]) || text.GlyphsOf(new TextRange(c, 1)).Take(1).ToList() is not [var g]) continue;
            var (cx, cy) = ((g.X0 + g.X1 + g.X2 + g.X3) / 4, (g.Y0 + g.Y1 + g.Y2 + g.Y3) / 4);
            if (cx < ink.Left || cx > ink.Right || cy < ink.Bottom || cy > ink.Top) continue;
            if (first < 0) first = c;
            last = c;
        }
        if (first < 0) return [];
        // the whole words, as searching or a double click marks them
        while (first > 0 && char.IsLetterOrDigit(text.Text[first - 1])) first--;
        while (last + 1 < text.Text.Length && char.IsLetterOrDigit(text.Text[last + 1])) last++;
        // as the program marks recognised text over a scan: the characters' areas grown to the ink they stand for
        var range = new TextRange(first, last - first + 1);
        using var doc = PdfDocument.Open(file);
        return InkSnap.Grow(doc, doc.Pages[0], PageInterpreter.Build(doc, doc.Pages[0]), text.RedactionAreas(range), new ImageCache(64L << 20),
            InkSnap.WholeWords(text.Text, range.Start, range.End));
    }

    // ------------------------------------------------------------ Checks

    private static List<string> CheckTraps(Traps traps, RedactionCase test, byte[] result)
    {
        var problems = new List<string>();
        var bars = test.Bars.GetValueOrDefault(0) ?? [];

        // ink of a redacted word no bar touches: it stays visible (the program blacks out every image pixel a bar touches, and keeps the rest)
        foreach (var t in traps.Targets)
        {
            var outside = t.Ink.Count(p =>
            {
                double left = p.X / PxPerPt, right = (p.X + 1) / PxPerPt, top = PageHeight - p.Y / PxPerPt, bottom = PageHeight - (p.Y + 1) / PxPerPt;
                return !bars.Any(b => left < b.Right && right > b.Left && bottom < b.Top && top > b.Bottom);
            });
            if (outside > 0) problems.Add($"{outside} von {t.Ink.Count} Pixeln Tinte von „{t.Word}“ außerhalb der Balken");
        }

        // one revision, nothing behind its end
        var eof = Encoding.ASCII.GetBytes("%%EOF");
        int count = 0, lastEof = -1;
        for (var from = 0; result.AsSpan(from).IndexOf(eof) is var n and >= 0; from = lastEof + eof.Length)
        {
            count++;
            lastEof = from + n;
        }
        if (count != 1) problems.Add($"{count} Dateienden (%%EOF)");
        else if (result.AsSpan(lastEof + eof.Length).IndexOfAnyExcept("\r\n \t\0"u8) >= 0) problems.Add("Daten hinter dem Dateiende");

        // no piece of the encoded data of an original image
        foreach (var data in traps.OriginalImages.Where(d => d.Length >= 400))
            foreach (var at in new[] { data.Length / 4, data.Length / 2, data.Length * 3 / 4 })
                if (result.AsSpan().IndexOf(data.AsSpan(at, 48)) >= 0)
                {
                    problems.Add($"Rohdaten eines ursprünglichen Bilds ({data.Length} Bytes) noch in der Datei");
                    goto doneRaw;
                }
        doneRaw:

        using var doc = PdfDocument.Open(result);
        if (doc.Trailer.ContainsKey(PdfName.Get("Prev"))) problems.Add("ältere Revision im Ergebnis (/Prev)");
        if (doc.Pages[0].Dictionary.ContainsKey(PdfName.Get("Thumb"))) problems.Add("Vorschaubild (/Thumb) auf der Seite");
        var redacted = bars.Concat(traps.EarlierRedacted).ToList();
        for (var n = 1; n <= doc.HighestObjectNumber; n++)
        {
            if (!doc.HasObject(n) || doc.GetObject(n) is not PdfStream stream) continue;
            var dict = stream.Dictionary;
            if (dict.GetName(PdfName.Get("Filter"))?.Value == "DCTDecode" && doc.DecodeStream(stream).Data is { } raw && raw.AsSpan().IndexOf("Exif\0\0"u8) >= 0)
                problems.Add($"Objekt {n}: JPEG mit EXIF-Segment");
            if (dict.GetName(PdfName.Get("Type"))?.Value == "Metadata" && Encoding.UTF8.GetString(doc.DecodeStream(stream).Data).Contains("xmpGImg:image"))
                problems.Add($"Objekt {n}: XMP mit Vorschaubild");
            var isImage = dict.GetName(PdfName.Get("Subtype"))?.Value == "Image" || dict.ContainsKey(PdfName.Get("Width")) && dict.ContainsKey(PdfName.Get("Height"));
            if (!isImage) continue;
            var stencil = dict.GetBoolean(PdfName.Get("ImageMask")) == true;
            if (PdfImageDecoder.Decode(new ImageSource(stream, null, null, stencil, 0xFF000000), doc) is not { } img) continue;
            // only pictures of the whole page (the scan, its copies and thumbnails)
            if (Math.Abs(img.Width / (double)img.Height - Width / (double)Height) > 0.03) continue;
            if (Uniform(img, redacted) is { } spread)
                problems.Add($"Objekt {n} ({img.Width} × {img.Height}) zeigt im geschwärzten Bereich noch Inhalt (Spanne {spread})");
        }
        return problems;
    }

    /// <summary>The largest spread of a colour channel inside a redacted area of a picture of the whole page, if above 48; else null.</summary>
    private static int? Uniform(DecodedImage img, List<PdfRectangle> areas)
    {
        var bpp = img.BytesPerPixel;
        var sx = img.Width / (double)PageWidth;
        var sy = img.Height / (double)PageHeight;
        var worst = 0;
        foreach (var a in areas)
        {
            // 1 point in from the edges (a reader smooths the border), and at least two pixels
            int x0 = (int)Math.Ceiling((a.Left + 1) * sx), x1 = (int)Math.Floor((a.Right - 1) * sx);
            int y0 = (int)Math.Ceiling((PageHeight - a.Top + 1) * sy), y1 = (int)Math.Floor((PageHeight - a.Bottom - 1) * sy);
            x0 = Math.Max(0, x0);
            y0 = Math.Max(0, y0);
            x1 = Math.Min(img.Width, x1);
            y1 = Math.Min(img.Height, y1);
            if (x1 - x0 < 2 || y1 - y0 < 2) continue;
            int[] low = [255, 255, 255, 255], high = [0, 0, 0, 0];
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                    for (var c = 0; c < bpp; c++)
                    {
                        var v = img.Pixels[(y * img.Width + x) * bpp + c];
                        low[c] = Math.Min(low[c], v);
                        high[c] = Math.Max(high[c], v);
                    }
            worst = Math.Max(worst, Enumerable.Range(0, bpp).Max(c => high[c] - low[c]));
        }
        return worst > 48 ? worst : null;
    }

    // ------------------------------------------------------------ Writing

    /// <summary>A small PDF writer for the cases: numbered objects, one cross-reference table, optionally a second revision.</summary>
    private sealed class PdfBuilder
    {
        private readonly List<byte[]> _objects = [];

        public int Object(string text)
        {
            _objects.Add(Encoding.Latin1.GetBytes(text));
            return _objects.Count;
        }

        public int Reserve() => Object("null");

        public void Set(int number, string text) => _objects[number - 1] = Encoding.Latin1.GetBytes(text);

        /// <summary>A stream with its data as given (a filter is part of the dictionary already).</summary>
        public int Stream(string dict, byte[] data) => Raw(dict, data);

        /// <summary>A stream with its data as given.</summary>
        public int Raw(string dict, byte[] data)
        {
            var head = Encoding.Latin1.GetBytes($"<< {dict} /Length {data.Length} >>\nstream\n");
            _objects.Add([.. head, .. data, .. "\nendstream"u8]);
            return _objects.Count;
        }

        public byte[] Write(int catalog)
        {
            using var output = new MemoryStream();
            output.Write("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"u8);
            WriteRevision(output, Enumerable.Range(1, _objects.Count).ToList(), catalog, prev: null);
            return output.ToArray();
        }

        /// <summary>
        /// Two revisions: the first has object <paramref name="image"/> as <paramref name="oldDict"/>/<paramref name="oldData"/>, the second
        /// (an incremental update, ISO 32000-2, 7.5.6) replaces it with <paramref name="dict"/>/<paramref name="data"/>.
        /// </summary>
        public byte[] WithRevision(int catalog, int image, string dict, byte[] data, string oldDict, byte[] oldData)
        {
            using var output = new MemoryStream();
            output.Write("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"u8);
            var head = Encoding.Latin1.GetBytes($"<< {oldDict} /Length {oldData.Length} >>\nstream\n");
            _objects[image - 1] = [.. head, .. oldData, .. "\nendstream"u8];
            var first = WriteRevision(output, Enumerable.Range(1, _objects.Count).ToList(), catalog, prev: null);
            head = Encoding.Latin1.GetBytes($"<< {dict} /Length {data.Length} >>\nstream\n");
            _objects[image - 1] = [.. head, .. data, .. "\nendstream"u8];
            WriteRevision(output, [image], catalog, prev: first);
            return output.ToArray();
        }

        private long WriteRevision(MemoryStream output, List<int> numbers, int catalog, long? prev)
        {
            var offsets = new Dictionary<int, long>();
            foreach (var n in numbers)
            {
                offsets[n] = output.Position;
                output.Write(Encoding.ASCII.GetBytes($"{n} 0 obj\n"));
                output.Write(_objects[n - 1]);
                output.Write("\nendobj\n"u8);
            }
            var xref = output.Position;
            var table = new StringBuilder("xref\n");
            if (prev is null)
            {
                table.Append($"0 {_objects.Count + 1}\n0000000000 65535 f \n");
                foreach (var n in numbers) table.Append($"{offsets[n]:D10} 00000 n \n");
            }
            else
                foreach (var n in numbers) table.Append($"{n} 1\n{offsets[n]:D10} 00000 n \n");
            var prevEntry = prev is { } p ? $" /Prev {p}" : "";
            table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {_objects.Count + 1} /Root {catalog} 0 R{prevEntry} >>\nstartxref\n{xref}\n%%EOF\n");
            output.Write(Encoding.ASCII.GetBytes(table.ToString()));
            return xref;
        }
    }
}
