using System.Text;
using System.Diagnostics;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Cli;

/// <summary>
/// "pdf-knead speichertest": rewrites every file, reopens the result and compares page by page — number of pages, size, display list
/// entries, text and a small image (at most 36 dpi, at most 2 levels difference per channel). Tests the writer against many
/// third-party files.
/// </summary>
internal static class SaveCheck
{

    /// <summary>--version: the version written, also by the checks that build their own save (text, images, merging; 1.7 without it).</summary>
    public static string Version { get; set; } = "1.7";

    /// <summary>
    /// --schuetzen: the copy protected as „Schützen“ does it — an owner password only (readers open it without one, so every checker can read it),
    /// printing allowed; the cipher follows the version (RC4 up to 1.5, AES-128 for 1.6, AES-256 from 1.7; docs/ABLAUFPLAN.md, 6.8 step 8).
    /// </summary>
    public static bool Protect { get; set; }

    /// <summary>--pdfa --teil: the PDF/A part and level written (PDF/A-2b without it).</summary>
    public static PdfAConformance PdfAPart { get; set; } = PdfAConformance.A2b;

    // Clean-up as preselected in the window (none of it is visible)
    private const CleanupItems Recommended = CleanupItems.Metadata | CleanupItems.Attachments | CleanupItems.Scripts | CleanupItems.Thumbnails | CleanupItems.PrivateData |
                                             CleanupItems.HiddenContent | CleanupItems.OtherData;

    /// <param name="reverse">Write the pages in reverse order — tests the path with a changed page order (bookmarks, form rebuilt).</param>
    /// <param name="twice">Additionally write a second time and show its allocations (writer only, objects already read).</param>
    /// <param name="cleanup">Clean up while writing (as "clean up document" with the preselection).</param>
    /// <param name="notices">With <paramref name="cleanup"/>: also remove the notices in fonts and colour profiles (not preselected).</param>
    /// <param name="hidden">Remove hidden content (hidden layers, invisible text, what nobody sees): the image must stay the same, the text need not.</param>
    /// <param name="foreign">Instead of rewriting: move all foreign annotations, then delete them (<see cref="ForeignCheck"/>).</param>
    /// <param name="redact">Every page gets a redaction band (40–55 % of the height), applied while writing; the text outside the band
    /// is compared (the images then differ).</param>
    /// <param name="signed">With <paramref name="forms"/>: sign every form first and append the filling (<see cref="FormCheck"/>).</param>
    /// <param name="compress">Compress with this preset (verlustfrei, bildschirm, ebook, druck; see <see cref="Settings"/>): lossless must
    /// keep every pixel, otherwise the mean deviation must stay small.</param>
    /// <param name="asImage">First "save as image" (jpeg or kompakt, 300 dpi), then compress the result (preset from
    /// <paramref name="compress"/>, otherwise E-Book): sizes and mean deviation image PDF → compressed (<see cref="AsImageCheck"/>).</param>
    /// <param name="hiddenRedact">Instead of rewriting: mark all hidden text of the first pages and apply it (<see cref="HiddenRedactCheck"/>).</param>
    /// <param name="ocrCleanup">Instead of rewriting: remove the invisible text as „Dokument bereinigen“ does, nothing of it may remain
    /// (<see cref="HiddenRedactCheck.RunCleanup"/>).</param>
    /// <param name="codeChange">Instead of rewriting: change every code found as „Code ändern …“ does (<see cref="CodeChangeCheck"/>).</param>
    /// <param name="imageEdit">Instead of rewriting: move, move back, delete and cut the pictures and graphics of every page as „Bilder bearbeiten“
    /// does (<see cref="ImageEditCheck"/>).</param>
    /// <param name="imageParts">Clean up „Verdeckte Teile von Bildern“ as well: the page must look the same (JPEG images are written anew, so with
    /// the tolerance of lossy compression), and afterwards no image may reach beyond what can be seen of it (pages with images that could not be
    /// cut aside).</param>
    /// <param name="fieldValues">Instead of rewriting: find form fields that show something other than their value, clean them up and check the
    /// copy (<see cref="FieldMismatchCheck"/>).</param>
    /// <param name="pageDelete">Instead of rewriting: write every file without its second page and with only its first, as deleting and
    /// extracting pages do, and check pages and structure (<see cref="PageDeleteCheck"/>).</param>
    /// <param name="newAnnotations">Instead of rewriting: every tagged file gets a note and a text field on its first page, saved twice, and
    /// their structure is checked (<see cref="NewAnnotationCheck"/>).</param>
    /// <param name="dataKeep">Instead of the usual comparison: what PDF-Knead does not know stays in the copy unchanged (<see cref="DataKeepCheck"/>).</param>
    /// <param name="arlington">With <paramref name="dataKeep"/>: the folder of the Arlington tables (the keys of the standard).</param>
    /// <param name="unlock">Remove restrictions (see KONZEPT.md, section 5.11): protected files only; the copy opens with the same password (or
    /// without one, like the original) and grants all permissions.</param>
    public static int Run(string path, string version, int maxPages, bool reverse = false, string? keep = null, bool allocations = false, bool twice = false,
        bool flatten = false, bool cleanup = false, bool redact = false, bool hidden = false, bool foreign = false, bool forms = false,
        string? compress = null, string? asImage = null, bool unlock = false, bool textEdit = false, bool signed = false, bool hiddenRedact = false,
        bool ocrCleanup = false, bool codeChange = false, bool merge = false, bool notices = false, bool imageEdit = false, bool imageParts = false, bool fieldValues = false,
        bool pdfA = false, bool pageDelete = false, bool newAnnotations = false, bool structure = false, bool redactAsImage = false,
        bool dataKeep = false, string? arlington = null)
    {
        int conforming = 0;
        var pdfAFailures = new Dictionary<string, int>(StringComparer.Ordinal);
        var flattenedCount = 0;   // PDF/A-1: pages with translucent areas as images
        var cleanItems = (cleanup ? Recommended | (notices ? CleanupItems.Notices : CleanupItems.None) | (structure ? CleanupItems.Structure : CleanupItems.None) : CleanupItems.None) |
                         (imageParts ? CleanupItems.HiddenImageParts : CleanupItems.None);
        int imagesCut = 0, imagesKept = 0;
        using var sampler = allocations ? new AllocationSampler() : null;
        sampler?.Start();
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [path];
        var passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = Path.Combine(Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path))!, "_passwoerter.txt");
        if (File.Exists(list))
            foreach (var line in File.ReadAllLines(list))
                if (line.Split('\t') is [var name, var pw]) passwords[name] = pw;
        if (keep is not null) Directory.CreateDirectory(keep);   // the output folder may be new
        Version = version;
        if (foreign) return ForeignCheck.Run(files, passwords, maxPages, keep);
        if (dataKeep) return DataKeepCheck.Run(files, passwords, version, arlington);
        if (fieldValues) return FieldMismatchCheck.Run(files, passwords);
        if (forms) return FormCheck.Run(files, passwords, maxPages, signed);
        if (textEdit) return TextEditCheck.Run(files, passwords, maxPages, keep);
        if (hiddenRedact) return HiddenRedactCheck.Run(files, passwords, maxPages, keep);
        if (ocrCleanup) return HiddenRedactCheck.RunCleanup(files, passwords, keep);
        if (codeChange) return CodeChangeCheck.Run(files, passwords, maxPages, keep);
        if (merge) return MergeCheck.Run(files, passwords, maxPages, keep);
        if (pageDelete) return PageDeleteCheck.Run(files, passwords, maxPages, keep);
        if (newAnnotations) return NewAnnotationCheck.Run(files, passwords, keep);
        if (imageEdit) return ImageEditCheck.Run(files, passwords, maxPages, keep);
        if (asImage is not null) return AsImageCheck(files, passwords, maxPages, asImage, Settings(compress ?? "ebook")!, keep);

        int ok = 0, skipped = 0, failed = 0, unprotected = 0;
        long before = 0, after = 0, allocated = 0;
        var worst = (File: "", Ratio: 0.0);
        long second = 0;
        var writeTime = TimeSpan.Zero;
        var total = Stopwatch.StartNew();
        foreach (var file in files)
        {
            PdfDocument original;
            try
            {
                original = PdfDocument.Open(file, passwords.GetValueOrDefault(Path.GetFileName(file)));
                if (original.PageCount == 0) throw new PdfKnead.Core.Pdf.PdfException("keine Seiten");
            }
            catch (Exception)
            {
                skipped++;   // the original itself is not readable
                continue;
            }
            if (unlock && original.Security is not { CanGrantAllPermissions: true })
            {
                unprotected++;   // no protection (or only attachments locked): nothing to unlock
                original.Dispose();
                continue;
            }
            using (original)
            {
                var problems = new List<string>();
                try
                {
                    using var output = new MemoryStream();
                    sampler?.Phase("Schreiben");
                    var alloc = GC.GetAllocatedBytesForCurrentThread();
                    var watch = Stopwatch.StartNew();
                    var items = PdfKnead.Core.Pdf.Editing.PageItem.From(original);
                    if (reverse) Array.Reverse(items);
                    if (redact)
                        for (var i = 0; i < Math.Min(items.Length, maxPages); i++)
                            items[i] = items[i].WithElements([.. items[i].Elements, new PdfKnead.Core.Pdf.Editing.RedactionElement { Rect = Band(items[i].Page) }]);
                    var options = new PdfSaveOptions { Version = version, Flatten = flatten, Cleanup = cleanItems | (hidden ? CleanupItems.HiddenLayers | CleanupItems.InvisibleText | CleanupItems.HiddenContent : CleanupItems.None),
                        ApplyRedactions = redact, ImageCodec = SkiaImageCodec.Instance, Compression = Settings(compress), GrantAllPermissions = unlock,
                        Protection = Protect ? new PdfProtection("", "besitzer", PdfKnead.Core.Pdf.Security.PdfPermissions.Print) : null, KeepEncryption = !Protect };
                    // PDF/A as „Als PDF/A speichern“ writes it (PDF/A-1: flattened where something is translucent)
                    IReadOnlyList<int> flattenedPages = [];
                    PdfSaveResult result;
                    if (pdfA)
                    {
                        var archived = PdfAExport.Write(original, items, PdfAPart, options, accessibility: PdfAToolCheck.Accessibility);
                        output.Write(archived.Bytes);
                        flattenedPages = archived.FlattenedPages;
                        flattenedCount += flattenedPages.Count;
                        result = new PdfSaveResult(PdfDocument.Open(archived.Bytes).Version, archived.Bytes.Length, 0, false);
                    }
                    else result = PdfWriter.Write(original, items, output, options);
                    // --als-bild with --schwaerzen: the redacted pages saved as their images as „zusätzlich als Bild“ does it (the image page stands
                    // for the old one; docs/ABLAUFPLAN.md, 6.6 g)
                    if (redact && redactAsImage) result = RedactedAsImages(output, maxPages);
                    writeTime += watch.Elapsed;
                    var used = GC.GetAllocatedBytesForCurrentThread() - alloc;
                    if (twice)
                    {
                        sampler?.Phase("Zweites Schreiben");
                        var a2 = GC.GetAllocatedBytesForCurrentThread();
                        PdfWriter.Write(original, items, Stream.Null, new PdfSaveOptions { Version = version, Flatten = flatten, Cleanup = cleanItems });
                        second += GC.GetAllocatedBytesForCurrentThread() - a2;
                        sampler?.Phase("Prüfen");
                    }
                    sampler?.Phase("Prüfen");
                    allocated += used;
                    if (original.FileSize > 100_000 && (double)used / original.FileSize > worst.Ratio) worst = (Path.GetFileName(file), (double)used / original.FileSize);
                    before += original.FileSize;
                    after += output.Length;
                    if (keep is not null) File.WriteAllBytes(Path.Combine(keep, Path.GetFileName(file)), output.ToArray());
                    using var copy = PdfDocument.Open(output.ToArray(), passwords.GetValueOrDefault(Path.GetFileName(file)));
                    if (copy.WasRepaired) problems.Add("Kopie musste repariert werden");
                    if (copy.PageCount != original.PageCount) problems.Add($"Seiten {original.PageCount} → {copy.PageCount}");
                    if (copy.Version != result.Version) problems.Add($"Version {result.Version} → {copy.Version}");
                    if (unlock) CompareUnlocked(original, copy, problems);
                    if (!pdfA && !result.XmpNotUpdated) CompareXmp(copy, problems);
                    if (pdfA)
                    {
                        // conformance counted apart: what the writer reports instead of fixing (fonts that are not embedded …) is no deviation
                        var violations = PdfACheck.Check(copy);
                        if (violations.Count == 0) conforming++;
                        foreach (var rule in violations.Select(v => v.Rule).Distinct()) pdfAFailures[rule] = pdfAFailures.GetValueOrDefault(rule) + 1;
                    }
                    var images = new ImageCache();
                    for (var i = 0; i < Math.Min(Math.Min(original.PageCount, copy.PageCount), maxPages); i++)
                        if (redact && redactAsImage) break;   // the pages are images now
                        else if (redact) CompareOutside(original, copy, i, problems);
                        else ComparePage(original, copy, reverse ? original.PageCount - 1 - i : i, i, images, problems, compareText: !hidden,
                                lossy: Settings(compress)?.Lossy == true || imageParts || flattenedPages.Contains(i), entries: !flattenedPages.Contains(i));
                    if (imageParts)
                    {
                        // what was hidden of the images is gone: nothing left to cut (but where an image could not be cut)
                        var area = (PdfPage p) => DocumentCleanup.VisibleArea(p);
                        var copyItems = PdfKnead.Core.Pdf.Editing.PageItem.From(copy);
                        for (var i = 0; i < copyItems.Length; i++)
                        {
                            var had = PdfKnead.Core.Pdf.Editing.ContentObjectEditor.PartlyHidden(PdfKnead.Core.Pdf.Editing.ContentObjectEditor.ObjectsOf(items[i]), area(items[i].Page)).Count;
                            var left = PdfKnead.Core.Pdf.Editing.ContentObjectEditor.PartlyHidden(PdfKnead.Core.Pdf.Editing.ContentObjectEditor.ObjectsOf(copyItems[i]), area(copyItems[i].Page)).Count;
                            imagesCut += had - left;
                            imagesKept += left;
                            if (left > 0 && !result.ImagePartsKept.Contains(i)) problems.Add($"S. {i + 1}: noch {left} Bilder mit verdeckten Teilen");
                        }
                    }
                }
                catch (Exception e)
                {
                    problems.Add($"{e.GetType().Name}: {e.Message}");
                }
                if (problems.Count == 0)
                {
                    ok++;
                }
                else
                {
                    failed++;
                    Console.WriteLine($"{Path.GetFileName(file)}: {string.Join("; ", problems.Take(4))}");
                }
            }
        }
        Console.WriteLine($"Dateien: {files.Count}, gleich: {ok}, Abweichungen: {failed}, Original unlesbar: {skipped}, Zeit {total.Elapsed.TotalSeconds:0.0} s (Schreiben {writeTime.TotalSeconds:0.00} s)");
        if (pdfA)
            Console.WriteLine($"{PdfAPart.Name()}: {conforming} Dateien erfüllen die Prüfung; nicht erfüllt: " +
                              string.Join(", ", pdfAFailures.OrderByDescending(e => e.Value).Select(e => $"{e.Key} ({e.Value})")) +
                              (flattenedCount > 0 ? $"; verflacht: {flattenedCount} Seiten" : ""));
        if (unlock) Console.WriteLine($"Entsperrt geprüft: {ok + failed} geschützte Dateien, ohne Schutz übersprungen: {unprotected}");
        if (imageParts) Console.WriteLine($"Verdeckte Bildteile: {imagesCut} Bilder zugeschnitten, {imagesKept} blieben (nicht zuschneidbar)");
        if (before > 0) Console.WriteLine($"Größe: {before >> 10:N0} KB → {after >> 10:N0} KB ({100.0 * after / before:0} %)");
        Console.WriteLine($"Schreiben belegt: {allocated >> 20:N0} MB (größtes Verhältnis zur Dateigröße: {worst.Ratio:0.0}× bei {worst.File})");
        if (second > 0) Console.WriteLine($"Zweites Schreiben (Objekte schon gelesen) belegt: {second >> 20:N0} MB");
        if (sampler is not null)
        {
            Thread.Sleep(500);
            sampler.Print();
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// "Save as image" followed by compression: the first pages (up to the page limit) as an image PDF (300 dpi), compressed with
    /// <paramref name="settings"/>, reopened and compared with the image PDF. For the compact image PDF (MRC) the 1-bit masks should stay
    /// as they are and only the background may change; the check itself compares the size (at most 1 % or 1 KB larger) and the mean
    /// deviation per page (at most 4 per channel).
    /// </summary>
    /// <summary>
    /// The redacted copy in <paramref name="output"/> written once more with its first <paramref name="maxPages"/> pages replaced by their images
    /// (<see cref="PdfKnead.Core.Pdf.Editing.PageItem.StandingFor"/>), as „zusätzlich als Bild“ does it; the result replaces the output.
    /// </summary>
    private static PdfSaveResult RedactedAsImages(MemoryStream output, int maxPages)
    {
        using var applied = PdfDocument.Open(output.ToArray());
        var pages = PdfKnead.Core.Pdf.Editing.PageItem.From(applied);
        var count = Math.Min(pages.Length, maxPages);
        using var image = new MemoryStream();
        RasterExport.Write([.. pages.Take(count)], image, new RasterSettings());
        using var imageDoc = PdfDocument.Open(image.ToArray());
        for (var k = 0; k < count; k++) pages[k] = new PdfKnead.Core.Pdf.Editing.PageItem(imageDoc, imageDoc.Pages[k]).StandingFor(pages[k]);
        output.SetLength(0);
        return PdfWriter.Write(applied, pages, output, new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance });
    }

    private static int AsImageCheck(List<string> files, Dictionary<string, string> passwords, int maxPages, string kind, CompressionSettings settings, string? keep)
    {
        var compact = kind.Equals("kompakt", StringComparison.OrdinalIgnoreCase);
        int ok = 0, skipped = 0, failed = 0;
        long original = 0, raster = 0, compressed = 0;
        var rasterTime = TimeSpan.Zero;
        var compressTime = TimeSpan.Zero;
        var total = Stopwatch.StartNew();
        foreach (var file in files)
        {
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(file, passwords.GetValueOrDefault(Path.GetFileName(file)));
                if (doc.PageCount == 0) throw new PdfKnead.Core.Pdf.PdfException("keine Seiten");
            }
            catch (Exception)
            {
                skipped++;
                continue;
            }
            using (doc)
            {
                var problems = new List<string>();
                try
                {
                    var items = PdfKnead.Core.Pdf.Editing.PageItem.From(doc).Take(maxPages).ToArray();
                    var watch = Stopwatch.StartNew();
                    using var image = new MemoryStream();
                    RasterExport.Write(items, image, new RasterSettings(300, Compact: compact));
                    rasterTime += watch.Elapsed;
                    using var rasterDoc = PdfDocument.Open(image.ToArray());
                    watch.Restart();
                    using var output = new MemoryStream();
                    PdfWriter.Write(rasterDoc, PdfKnead.Core.Pdf.Editing.PageItem.From(rasterDoc), output,
                        new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance, Compression = settings });
                    compressTime += watch.Elapsed;
                    original += doc.FileSize * items.Length / doc.PageCount;
                    raster += image.Length;
                    compressed += output.Length;
                    if (keep is not null)
                    {
                        File.WriteAllBytes(Path.Combine(keep, Path.GetFileNameWithoutExtension(file) + " (als Bild).pdf"), image.ToArray());
                        File.WriteAllBytes(Path.Combine(keep, Path.GetFileNameWithoutExtension(file) + " (komprimiert).pdf"), output.ToArray());
                    }
                    // A few hundred bytes more are normal (the writer always writes /Info and /ID)
                    if (output.Length > image.Length + Math.Max(1024, image.Length / 100)) problems.Add($"größer: {image.Length >> 10} → {output.Length >> 10} KB");
                    using var copy = PdfDocument.Open(output.ToArray());
                    if (copy.PageCount != rasterDoc.PageCount) problems.Add($"Seiten {rasterDoc.PageCount} → {copy.PageCount}");
                    var images = new ImageCache();
                    for (var i = 0; i < Math.Min(rasterDoc.PageCount, copy.PageCount); i++)
                        ComparePage(rasterDoc, copy, i, i, images, problems, compareText: false, lossy: true);
                }
                catch (Exception e)
                {
                    problems.Add($"{e.GetType().Name}: {e.Message}");
                }
                if (problems.Count == 0)
                {
                    ok++;
                }
                else
                {
                    failed++;
                    Console.WriteLine($"{Path.GetFileName(file)}: {string.Join("; ", problems.Take(4))}");
                }
            }
        }
        Console.WriteLine($"Dateien: {files.Count}, gleich: {ok}, Abweichungen: {failed}, Original unlesbar: {skipped}, Zeit {total.Elapsed.TotalSeconds:0.0} s " +
            $"(Als Bild {rasterTime.TotalSeconds:0.0} s, Komprimieren {compressTime.TotalSeconds:0.0} s)");
        if (raster > 0)
            Console.WriteLine($"Größe: Original (diese Seiten) {original >> 10:N0} KB → Als Bild ({(compact ? "kompakt" : "JPEG")}) {raster >> 10:N0} KB → " +
                $"komprimiert ({settings.Preset}) {compressed >> 10:N0} KB ({100.0 * compressed / raster:0} %)");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Unlocked copy: needs a password exactly when the original needed one, grants all permissions and keeps the cipher.</summary>
    /// <summary>
    /// The document's XMP after saving (docs/ABLAUFPLAN.md, 6.1; docs/FEHLERGEDAECHTNIS.md, No. 613): well-formed, and its date of change the
    /// same instant as /ModDate (ISO 32000-2, 14.3.4).
    /// </summary>
    private static void CompareXmp(PdfDocument copy, List<string> problems)
    {
        if (copy.Catalog?.Get(PdfName.Get("Metadata")) is not PdfStream stream) return;
        if (PdfAXmp.Read(copy.GetStreamData(stream)) is not { } xml)
        {
            problems.Add("XMP nicht lesbar");
            return;
        }
        var modified = PdfDate.Parse(copy.Trailer.GetDictionary(PdfName.Info)?.GetString(PdfName.Get("ModDate"))?.ToText());
        var dates = xml.GetElementsByTagName("ModifyDate", "http://ns.adobe.com/xap/1.0/").OfType<System.Xml.XmlElement>().Select(e => e.InnerText.Trim()).ToList();
        if (modified is null) return;
        if (dates.Count != 1 || !DateTimeOffset.TryParse(dates[0], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ||
            date != modified)
            problems.Add($"XMP-Änderungsdatum {string.Join("/", dates)} ≠ /ModDate");
    }

    private static void CompareUnlocked(PdfDocument original, PdfDocument copy, List<string> problems)
    {
        static bool NeedsPassword(PdfDocument doc) =>
            doc.EncryptDictionary is { } encrypt &&
            !PdfKnead.Core.Pdf.Security.StandardSecurityHandler.OpensWithoutPassword(encrypt, doc.Trailer.Get(PdfKnead.Core.Pdf.Objects.PdfName.ID) as PdfKnead.Core.Pdf.Objects.PdfArray);
        if (copy.Security is not { } security)
        {
            problems.Add("Kopie ohne Verschlüsselung");
            return;
        }
        const PdfKnead.Core.Pdf.Security.PdfPermissions all = PdfKnead.Core.Pdf.Security.PdfPermissions.All;
        if ((security.Permissions & all) != all) problems.Add($"Rechte nicht voll: {security.Permissions & all}");
        if (NeedsPassword(original) != NeedsPassword(copy)) problems.Add("Öffnen-Passwort geändert");
        if (security.StreamCipher != original.Security!.StreamCipher) problems.Add($"Verfahren {original.Security.StreamCipher} → {security.StreamCipher}");
    }

    /// <summary>
    /// Preset, optionally with "-fotos" or "-mrc" (text scans as photos or as layers), "-g4" (black and white with G4 instead of JBIG2)
    /// "-stark" (strong Flate) and "-brotli" (Brotli instead of Flate), e.g. "ebook-fotos", "verlustfrei-g4" or "verlustfrei-brotli" for
    /// comparison; unknown names mean lossless.
    /// </summary>
    private static CompressionSettings? Settings(string? preset)
    {
        if (preset is null) return null;
        var scans = preset.Contains("-fotos", StringComparison.OrdinalIgnoreCase) ? ScanMethod.AsPhotos
            : preset.Contains("-mrc", StringComparison.OrdinalIgnoreCase) ? ScanMethod.Mrc : (ScanMethod?)null;
        var settings = Preset(preset.Split('-')[0]);
        if (preset.Contains("-g4", StringComparison.OrdinalIgnoreCase)) settings = settings with { BilevelCodec = BilevelMethod.G4 };
        if (preset.Contains("-stark", StringComparison.OrdinalIgnoreCase)) settings = settings with { StrongFlate = true };
        if (preset.Contains("-brotli", StringComparison.OrdinalIgnoreCase)) settings = settings with { Brotli = true };
        return scans is { } s ? settings with { Scans = s } : settings;
    }

    private static CompressionSettings Preset(string preset) => preset.ToLowerInvariant() switch
    {
        "bildschirm" or "screen" => CompressionSettings.For(CompressionPreset.Screen),
        "druck" or "print" => CompressionSettings.For(CompressionPreset.Print),
        "ebook" => CompressionSettings.For(CompressionPreset.EBook),
        _ => CompressionSettings.For(CompressionPreset.Lossless),
    };

    internal static void ComparePage(PdfDocument original, PdfDocument copy, int index, int copyIndex, ImageCache images, List<string> problems, bool compareText = true,
        bool lossy = false, bool entries = true)
    {
        var a = original.Pages[index];
        var b = copy.Pages[copyIndex];
        if (Math.Abs(a.Width - b.Width) > 0.01 || Math.Abs(a.Height - b.Height) > 0.01 || a.Rotate != b.Rotate)
        {
            problems.Add($"S. {index + 1}: Größe/Drehung");
            return;
        }
        var listA = Build(original, a);
        var listB = Build(copy, b);
        if (listA is null || listB is null)
        {
            if ((listA is null) != (listB is null)) problems.Add($"S. {index + 1}: nur eine Seite lesbar");
            return;
        }
        // lossy compression may lay a scan out anew (MRC: a background and a mask per text colour, one image becomes five entries): there only
        // fewer entries count — something drawn would have gone (phase 9.1)
        if (compareText && entries && (lossy ? listB.Count < listA.Count : listA.Count != listB.Count))
            problems.Add($"S. {index + 1}: Einträge {listA.Count} → {listB.Count}");
        if (compareText && Comparable(PageText.Create(listA.Text).Text) != Comparable(PageText.Create(listB.Text).Text)) problems.Add($"S. {index + 1}: Text weicht ab");
        var scale = Math.Min(0.5, 1500 / Math.Max(a.Width, a.Height));
        using var imageA = SkiaRenderer.RenderPage(listA, original, a, scale, images);
        using var imageB = SkiaRenderer.RenderPage(listB, copy, b, scale, new ImageCache());
        if (lossy)
        {
            // images re-encoded: only the mean deviation counts
            var mean = MeanDifference(imageA, imageB);
            if (mean > 4)
            {
                // a scan laid out anew as MRC (more entries: a background and a mask per text colour) has sharp masks where the original had
                // smoothed text — each edge counts pixel by pixel, while what has to stay is the tone: the mean over blocks of 4 × 4 pixels.
                // A dense form of small print came to 6.8 by pixels and 3.0 in tone (3.7 before the ink colour of No. 876), the paper the same,
                // every letter there (issue7229; docs/FEHLERGEDAECHTNIS.md, No. 877)
                var tone = listB.Count > listA.Count ? ToneDifference(imageA, imageB, 4) : mean;
                if (tone > 4) problems.Add($"S. {index + 1}: mittlere Abweichung {mean:0.0}" + (tone < mean ? $", im Ton {tone:0.0}" : ""));
            }
            return;
        }
        var differing = Differing(imageA, imageB);
        if (differing > 0)
        {
            // stored losslessly as a 1-bit image, a scan is drawn otherwise when made smaller (the 1-bit way sharper than the grey one) — compared
            // once more at 400 dpi, where nothing is made smaller: the same pixels there, nothing was lost (issue12963; No. 74, No. 877)
            if (DifferingFine(original, a, listA, copy, b, listB, images) == 0) return;
            problems.Add($"S. {index + 1}: {differing} Pixel anders");
        }
    }

    /// <summary>
    /// Pixels that differ when both pages are drawn at 400 dpi (at most 60 million pixels a page; a larger page counts as differing, -1 if it
    /// cannot be compared).
    /// </summary>
    private static int DifferingFine(PdfDocument original, PdfPage a, PdfKnead.Core.Pdf.Graphics.DisplayList listA, PdfDocument copy, PdfPage b,
        PdfKnead.Core.Pdf.Graphics.DisplayList listB, ImageCache images)
    {
        const double Scale = 400 / 72.0;
        if (a.Width * Scale * a.Height * Scale > 60_000_000) return -1;
        using var fineA = SkiaRenderer.RenderPage(listA, original, a, Scale, images);
        using var fineB = SkiaRenderer.RenderPage(listB, copy, b, Scale, new ImageCache());
        return Differing(fineA, fineB);
    }

    /// <summary>The mean deviation of the means over blocks of <paramref name="block"/> × <paramref name="block"/> pixels: tone, not edges.</summary>
    private static double ToneDifference(SKBitmap a, SKBitmap b, int block)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 255;
        double sum = 0;
        long blocks = 0;
        for (var y = 0; y + block <= a.Height; y += block)
            for (var x = 0; x + block <= a.Width; x += block)
            {
                long r = 0, g = 0, bl = 0;
                for (var dy = 0; dy < block; dy++)
                    for (var dx = 0; dx < block; dx++)
                    {
                        var (p, q) = (a.GetPixel(x + dx, y + dy), b.GetPixel(x + dx, y + dy));
                        r += p.Red - q.Red;
                        g += p.Green - q.Green;
                        bl += p.Blue - q.Blue;
                    }
                sum += (Math.Abs(r) + Math.Abs(g) + Math.Abs(bl)) / (double)(block * block);
                blocks++;
            }
        return sum / (3.0 * Math.Max(1, blocks));
    }

    /// <summary>Mean deviation per colour channel (0 … 255).</summary>
    private static double MeanDifference(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 255;
        long sum = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
            {
                var (p, q) = (a.GetPixel(x, y), b.GetPixel(x, y));
                sum += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
            }
        return sum / (3.0 * Math.Max(1, a.Width * a.Height));
    }

    /// <summary>Redaction band across the page: 40 to 55 % of the height, 5 % margin left and right.</summary>
    private static PdfRectangle Band(PdfPage page)
    {
        var box = page.CropBox;
        return new PdfRectangle(box.Left + box.Width * 0.05, box.Bottom + box.Height * 0.40, box.Right - box.Width * 0.05, box.Bottom + box.Height * 0.55);
    }

    /// <summary>
    /// Redacted copy: text clearly outside the band is unchanged; no glyph remains under the band (the writer checks that
    /// itself).
    /// </summary>
    private static void CompareOutside(PdfDocument original, PdfDocument copy, int index, List<string> problems)
    {
        var listA = Build(original, original.Pages[index]);
        var listB = Build(copy, copy.Pages[index]);
        if (listA is null || listB is null)
        {
            if ((listA is null) != (listB is null)) problems.Add($"S. {index + 1}: nur eine Seite lesbar");
            return;
        }
        var band = Band(original.Pages[index]);
        string Outside(PdfKnead.Core.Pdf.Graphics.DisplayList list) => string.Concat(list.Text.Where(g =>
        {
            float top = Math.Max(Math.Max(g.Y0, g.Y1), Math.Max(g.Y2, g.Y3)), bottom = Math.Min(Math.Min(g.Y0, g.Y1), Math.Min(g.Y2, g.Y3));
            float left = Math.Min(Math.Min(g.X0, g.X1), Math.Min(g.X2, g.X3)), right = Math.Max(Math.Max(g.X0, g.X1), Math.Max(g.X2, g.X3));
            const double margin = 2;
            return bottom > band.Top + margin || top < band.Bottom - margin || right < band.Left - margin || left > band.Right + margin;
        }).Select(g => g.Text));
        string a = Outside(listA), b = Outside(listB);
        if (a == b) return;
        // Annotations and fields touching the band are removed entirely — including their text outside it
        if (TouchedAnnotations(original.Pages[index], band) > 0 && IsSubsequence(b, a)) return;
        problems.Add($"S. {index + 1}: Text außerhalb weicht ab ({a.Length} → {b.Length} Zeichen)");
    }

    private static int TouchedAnnotations(PdfPage page, PdfRectangle band)
    {
        var count = 0;
        if (page.Dictionary.GetArray(PdfKnead.Core.Pdf.Objects.PdfName.Get("Annots")) is { } annots)
            for (var i = 0; i < annots.Count; i++)
                if (annots.Get(i) is PdfKnead.Core.Pdf.Objects.PdfDictionary annot &&
                    PdfRectangle.From(annot.Get(PdfKnead.Core.Pdf.Objects.PdfName.Get("Rect"))) is { } r && !r.Intersect(band).IsEmpty)
                    count++;
        return count;
    }

    private static bool IsSubsequence(string part, string whole)
    {
        var j = 0;
        foreach (var c in whole)
            if (j < part.Length && part[j] == c) j++;
        return j == part.Length;
    }

    private static PdfKnead.Core.Pdf.Graphics.DisplayList? Build(PdfDocument doc, PdfPage page)
    {
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return PageInterpreter.Build(doc, page, cancel.Token);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static int Differing(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return a.Width * a.Height;
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        var count = 0;
        for (var i = 0; i + 3 < pa.Length; i += 4)
            if (Math.Abs(pa[i] - pb[i]) > 2 || Math.Abs(pa[i + 1] - pb[i + 1]) > 2 || Math.Abs(pa[i + 2] - pb[i + 2]) > 2)
                count++;
        return count;
    }

    /// <summary>
    /// Text for comparing: without the stand-ins PDF/A levels u and a give characters that have no text (a private use area, U+FFFD, a space for
    /// a glyph that draws nothing) — the original has nothing there, and the spacing of the line follows from it; so without any white space.
    /// </summary>
    private static string Comparable(string text) =>
        PdfAPart.NeedsUnicode()
            ? string.Concat(text.EnumerateRunes().Where(r => !(r.Value is >= 0xE000 and <= 0xF8FF or >= 0xF0000 or 0xFFFD) && !Rune.IsWhiteSpace(r)).Select(r => r.ToString()))
            : text;
}
