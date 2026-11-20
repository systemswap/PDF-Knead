using System.Diagnostics;
using System.Globalization;
using PdfKnead.Ai;
using PdfKnead.Core.Forms.Recognition;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Cli;

/// <summary>
/// pdf-knead formularerkennung &lt;file|folder&gt; [--einzeln] [--modelle &lt;folder&gt;] [--ohne-erkenner] [--scan] [--schwelle &lt;s&gt;]
/// (KONZEPT.md, section 5.9a; docs/PRUEFUNGEN.md, section 6q): the proposals of the form recognition. For files that are forms already, their
/// fields are the expectation — the recognition does not see them (the display list leaves widgets out) and has to find them again from lines
/// and boxes: found, too many, kind right. With the models (a folder with the model folders as published) the text recognition reads scans
/// and the field detector looks at every page; „--scan“ looks at an image copy of each form instead (its pages without the fields as JPEG at
/// 200 dpi, as a scanner gives them), measured against the fields of the original.
/// </summary>
internal static class FormRecognitionCheck
{
    /// <summary>The settings of a run.</summary>
    /// <param name="Models">The folder with the model folders; null: without models.</param>
    /// <param name="Detector">Use the field detector (when there are models).</param>
    /// <param name="Scan">Look at image copies of the forms.</param>
    /// <param name="MinScore">Threshold of the field detector.</param>
    public sealed record Settings(string? Models, bool Detector, bool Scan, double MinScore);

    public static int Run(string path, bool verbose, Settings? settings = null)
    {
        settings ??= new Settings(null, false, false, new FfdetrOptions().MinScore);
        using var recognizer = settings.Models is { } m && Directory.Exists(Path.Combine(m, "PP-OCRv6_medium_det_onnx")) ? new PaddleOcr(Path.Combine(m, "PP-OCRv6_medium_det_onnx"), Path.Combine(m, "PP-OCRv6_medium_rec_onnx")) : null;
        using var detector = settings is { Models: { } d, Detector: true }
            ? new FfdetrDetector(Path.Combine(d, OcrModels.FieldDetector.Folder, OcrModels.FieldDetector.Name), new FfdetrOptions(MinScore: settings.MinScore))
            : null;
        var images = new ImageCache(64L << 20);
        var files = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList() : [path];
        var passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = Path.Combine(Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path))!, "_passwoerter.txt");
        if (File.Exists(list))
            foreach (var line in File.ReadAllLines(list))
                if (line.Split('\t') is [var name, var pw]) passwords[name] = pw;
        int forms = 0, expected = 0, found = 0, kindRight = 0, proposals = 0, matched = 0, inForms = 0, named = 0, nameable = 0;
        var time = Stopwatch.StartNew();
        foreach (var file in files)
        {
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(file, passwords.GetValueOrDefault(Path.GetFileName(file)));
            }
            catch (Exception e) when (e is PdfException or IOException)
            {
                continue;
            }
            using (doc)
            {
                List<FieldProposal> got;
                PdfDocument? copy = null;
                try
                {
                    var look = doc;
                    if (settings.Scan) look = copy = ImageCopy(doc, images);
                    var items = PageItem.From(look);
                    // a scan is looked at in its image also without models (its lines need none)
                    Func<CancellationToken, PageScan?> Scan(int i) => cancel => PageFormScan.Look(items[i], recognizer, detector, images, cancel)?.Scan;
                    got = FormRecognizer.Recognize([.. look.Pages.Take(20).Select((p, i) => new FormRecognizer.PageInput(look, p, [], Scan(i)))], new HashSet<string>());
                    images.Clear();
                }
                catch (Exception e) when (e is PdfException or InvalidOperationException)
                {
                    Console.WriteLine($"FEHLER {Path.GetFileName(file)}: {e.Message}");
                    continue;
                }
                finally
                {
                    copy?.Dispose();
                }
                // the fields of the file (only those with an area a user could write in)
                var truth = new List<(int Page, PdfRectangle Rect, FormFieldKind Kind, string Words)>();
                for (var i = 0; i < Math.Min(doc.PageCount, 20); i++)
                    foreach (var a in ForeignAnnotations.For(doc, doc.Pages[i]))
                        if (FormField.Of(a) is { Kind: not FormFieldKind.PushButton } f && a.Rect.Width >= 4 && a.Rect.Height >= 4 &&
                            !(settings.Scan && doc.Pages[i].Rotate % 360 != 0))
                            truth.Add((i, settings.Scan ? Shift(a.Rect, -doc.Pages[i].CropBox.Left, -doc.Pages[i].CropBox.Bottom) : a.Rect, f.Kind,
                                f.Kind == FormFieldKind.Text && (FormField.Inherited(f.Widget, PdfName.Get("TU")) as PdfString)?.ToText() is { Length: > 0 } tip
                                    ? (f.Name + " " + tip).ToLowerInvariant() : ""));
                if (!Directory.Exists(path) || verbose)
                    foreach (var p in got)
                        Console.WriteLine($"{(truth.Count == 0 ? "" : truth.Any(t => t.Page == p.Page && Overlaps(p.Rect, t.Rect)) ? "✓ " : "+ ")}S{p.Page + 1} {p.Spec.Kind,-9} {p.Source,-8} {p.Confidence:0.00} [{p.Rect.Left:0} {p.Rect.Bottom:0} {p.Rect.Right:0} {p.Rect.Top:0}] " +
                                          $"\"{p.Spec.Name}\"{(p.Spec.Format.Kind != FieldFormatKind.None ? " " + p.Spec.Format.Kind : "")}{(p.Spec.Comb ? $" Kamm {p.Spec.MaxLength}" : "")}" +
                                          $"{(p.Spec.Multiline ? " mehrzeilig" : "")}");
                proposals += got.Count;
                if (truth.Count == 0) continue;
                forms++;
                inForms += got.Count;
                expected += truth.Count;
                var hits = 0;
                foreach (var t in truth)
                {
                    var match = got.FirstOrDefault(p => p.Page == t.Page && Overlaps(p.Rect, t.Rect));
                    if (match is null) continue;
                    hits++;
                    if (Same(match.Spec.Kind, t.Kind)) kindRight++;
                    // the name: its first word (of three letters or more) in the field's name or tooltip — text fields whose file describes them
                    if (t.Words.Count(char.IsLetter) >= 6 && FirstWord(match.Spec.Name) is { } word)
                    {
                        nameable++;
                        if (t.Words.Contains(word, StringComparison.Ordinal)) named++;
                    }
                }
                found += hits;
                if (!Directory.Exists(path) || verbose)
                    foreach (var t in truth.Where(t => !got.Any(p => p.Page == t.Page && Overlaps(p.Rect, t.Rect))))
                        Console.WriteLine($"- S{t.Page + 1} {t.Kind,-9} [{t.Rect.Left:0} {t.Rect.Bottom:0} {t.Rect.Right:0} {t.Rect.Top:0}] nicht gefunden");
                matched += got.Count(p => truth.Any(t => t.Page == p.Page && Overlaps(p.Rect, t.Rect)));
                if (Directory.Exists(path)) Console.WriteLine($"{Path.GetFileName(file)}: Felder {truth.Count}, gefunden {hits}, Vorschläge {got.Count}");
            }
        }
        Console.WriteLine($"Formulare: {forms}, Felder: {expected}, gefunden: {found} ({Percent(found, expected)}), Art richtig: {kindRight}; " +
                          $"Vorschläge: {proposals}, in den Formularen {inForms}, davon auf einem Feld: {matched} ({Percent(matched, inForms)}); " +
                          $"Name passt: {named} von {nameable} ({Percent(named, nameable)}); Zeit {time.Elapsed.TotalSeconds:0.0} s");
        return 0;
    }

    /// <summary>The first word of a proposed name with three letters or more, in lower case (the counter „_2“ left out); null if none.</summary>
    private static string? FirstWord(string name) =>
        name.Split([' ', '_', ',', '.', '/', '-', '(', ')', '"', '„', '“', ':'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(w => w.Count(char.IsLetter) >= 3)?.ToLowerInvariant();

    private static PdfRectangle Shift(PdfRectangle r, double dx, double dy) => new(r.Left + dx, r.Bottom + dy, r.Right + dx, r.Top + dy);

    /// <summary>
    /// An image copy of the first 20 pages: each page without its fields rendered at 200 dpi and stored as JPEG (quality 85) on a page of the
    /// size of its crop box — what a scanner makes of the printed form.
    /// </summary>
    private static PdfDocument ImageCopy(PdfDocument doc, ImageCache images)
    {
        using var output = new MemoryStream();
        var builder = new RasterPdfBuilder(output);
        foreach (var item in PageItem.From(doc).Take(20))
        {
            var list = PageInterpreter.Build(doc, item.Page, withoutOwnElements: true);
            var (image, _) = PageRecognition.Render(item.WithElements([]), list, images);
            using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(image.Rgba, 0, bitmap.GetPixels(), image.Rgba.Length);
            using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 85);
            var box = item.Page.CropBox;
            var turned = item.Page.Rotate % 180 != 0;
            builder.AddPage(turned ? box.Height : box.Width, turned ? box.Width : box.Height, jpeg.Span, "DCTDecode", image.Width, image.Height);
        }
        builder.Finish();
        return PdfDocument.Open(output.ToArray());
    }

    private static string Percent(int a, int b) => b == 0 ? "–" : $"{100.0 * a / b:0.0} %";

    private static bool Same(FormFieldKind proposed, FormFieldKind actual) =>
        proposed == actual || proposed is FormFieldKind.CheckBox or FormFieldKind.Radio && actual is FormFieldKind.CheckBox or FormFieldKind.Radio;

    /// <summary>The proposal lies on the field: at least half of the smaller one is shared.</summary>
    private static bool Overlaps(PdfRectangle a, PdfRectangle b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        return w > 0 && h > 0 && w * h >= 0.5 * Math.Min(a.Width * a.Height, b.Width * b.Height);
    }
}
