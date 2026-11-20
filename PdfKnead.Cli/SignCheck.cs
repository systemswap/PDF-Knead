using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Cli;

/// <summary>
/// „pdf-knead signiertest &lt;file|folder&gt;“ (KONZEPT.md, section 5.17, round S7): signs every file in three steps the way the program
/// saves and checks each result three ways — the own check (valid, what changed later), the signed page drawn against the page with
/// the signature placed (it must sit where it was put, see <see cref="Compare"/>), and with --ablegen the files for a reference checker.
/// <list type="number">
/// <item>An unsigned file written anew with a first signature on page 1 (kinds in turn: drawn, handwriting with ballpoint texture,
/// handwriting with origin line, image; every third certifying „only filling in and signing“), own certificate (RSA 3072).</item>
/// <item>A second signature on the last page, appended, with an ECDSA P-256 certificate from a file.</item>
/// <item>If the file has a text field: filled in together with a third signature (RSA 2048 issued by a test authority, its chain
/// embedded), appended in one save.</item>
/// </list>
/// Pages keep their rotation and crop box, so rotated pages and boxes not starting at 0,0 are covered by the test files.
/// </summary>
internal static class SignCheck
{
    private sealed record Certificates(SigningCertificate Own, SigningCertificate Ecdsa, SigningCertificate Issued) : IDisposable
    {
        public void Dispose()
        {
            Own.Dispose();
            Ecdsa.Dispose();
            Issued.Dispose();
        }
    }

    public static int Run(string path, string? keep, int maxFiles)
    {
        var files = Directory.Exists(path) ? Directory.GetFiles(path, "*.pdf").Order(StringComparer.Ordinal).Take(maxFiles).ToArray() : [path];
        var work = Path.Combine(Path.GetTempPath(), "pdf-knead-pruefung", $"signiertest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        if (keep is not null) Directory.CreateDirectory(keep);
        int ok = 0, failed = 0, skipped = 0, steps = 0;
        var total = Stopwatch.StartNew();
        using (var certificates = Create(work))
            for (var n = 0; n < files.Length; n++)
            {
                var file = files[n];
                var problems = new List<string>();
                PdfDocument doc;
                try
                {
                    doc = PdfDocument.Open(file);
                }
                catch (Exception e) when (e is Core.Pdf.PdfException or IOException or InvalidDataException)
                {
                    skipped++;   // protected or no readable PDF: nothing to sign
                    continue;
                }
                using (doc)
                {
                    if (doc.IsEncrypted || doc.WasRepaired || doc.PageCount == 0)
                    {
                        skipped++;
                        continue;
                    }
                    try
                    {
                        steps += Check(doc, n, certificates, problems, keep is null ? null : Path.Combine(keep, Path.GetFileNameWithoutExtension(file)));
                    }
                    catch (Exception e)
                    {
                        problems.Add($"{e.GetType().Name}: {e.Message}");
                    }
                }
                if (problems.Count == 0) ok++;
                else
                {
                    failed++;
                    Console.WriteLine($"{Path.GetFileName(file)}: {string.Join("; ", problems.Take(5))}");
                }
            }
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
        }
        Console.WriteLine($"Dateien: {files.Length}, geprüft: {ok + failed} ({steps} Signierschritte), richtig: {ok}, Abweichungen: {failed}, " +
                          $"übersprungen (verschlüsselt, repariert, unlesbar): {skipped}, Zeit {total.Elapsed.TotalSeconds:0.0} s");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Own certificate (RSA 3072), an ECDSA P-256 one from a file, an RSA 2048 one issued by a test authority.</summary>
    private static Certificates Create(string work)
    {
        var own = SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(work, "eigen.p12"));
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        using var ecKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ecRequest = new CertificateRequest("CN=Erika Mustermann", ecKey, HashAlgorithmName.SHA256);
        ecRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var ec = ecRequest.CreateSelfSigned(from, from.AddYears(1));
        var ecFile = Path.Combine(work, "ecdsa.pfx");
        File.WriteAllBytes(ecFile, ec.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, "geheim"));

        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Muster-Zertifizierungsstelle", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(from, from.AddYears(2));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Max Mustermann (Firma)", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var issued = request.Create(ca, from, from.AddYears(1), [7, 7, 7]);
        using var withKey = issued.CopyWithPrivateKey(key);
        var issuedFile = Path.Combine(work, "firma.pfx");
        var collection = new X509Certificate2Collection { withKey, X509CertificateLoader.LoadCertificate(ca.RawData) };
        File.WriteAllBytes(issuedFile, collection.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, "geheim")!);
        return new Certificates(own, SigningCertificates.LoadFile(ecFile, "geheim"), SigningCertificates.LoadFile(issuedFile, "geheim"));
    }

    /// <summary>The three steps for one file; returns the number of signatures made.</summary>
    private static int Check(PdfDocument doc, int n, Certificates certificates, List<string> problems, string? keep)
    {
        var options = new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance };
        // 1. written anew with the first signature on page 1
        var first = Place(doc, 0, n, n % 3 == 0 ? CertificationLevel.FormsAndSigning : null);
        var pages = PageItem.From(doc);
        pages[0] = pages[0].WithElements([.. pages[0].Elements, first]);
        var one = Save(doc, pages, options, appendOnly: false, certificates.Own, keep, 1);
        using var signed = PdfDocument.Open(one);
        Expect(signed, one, 1, problems, "1");
        Compare(doc, pages[0], signed, 0, problems, "1");

        // 2. a second signature on the last page, appended
        var last = signed.PageCount - 1;
        var second = Place(signed, last, n + 1, null, 0.78);
        var saved = PageItem.From(signed);
        var withSecond = (PageItem[])saved.Clone();
        withSecond[last] = saved[last].WithElements([.. saved[last].Elements, second]);
        if (!SignedSaving.AppendsOnly(signed, saved, withSecond, out var reason))
        {
            problems.Add($"2: nicht anhängbar {reason}");
            return 1;
        }
        var two = Save(signed, withSecond, options, appendOnly: true, certificates.Ecdsa, keep, 2);
        if (!two.AsSpan(0, one.Length).SequenceEqual(one)) problems.Add("2: Original nicht unverändert");
        using var twice = PdfDocument.Open(two);
        Expect(twice, two, 2, problems, "2");
        Compare(signed, withSecond[last], twice, last, problems, "2");

        // 3. a text field filled in together with a third signature, appended in one save
        var items = PageItem.From(twice);
        var at = Array.FindIndex(items, i => i.Elements.Any(IsTextField));
        if (at < 0) return 2;
        var field = items[at].Elements.First(IsTextField);
        var third = Place(twice, at, n + 2, null, 0.22);
        var filled = (PageItem[])items.Clone();
        filled[at] = items[at].WithElements([.. items[at].Elements.Select(e => ReferenceEquals(e, field) ? ((ForeignAnnotation)e) with { Value = "Musterstadt" } : e), third]);
        if (!SignedSaving.AppendsOnly(twice, items, filled, out reason))
        {
            problems.Add($"3: nicht anhängbar {reason}");
            return 2;
        }
        var three = Save(twice, filled, options, appendOnly: true, certificates.Issued, keep, 3);
        using var thrice = PdfDocument.Open(three);
        Expect(thrice, three, 3, problems, "3");
        var name = FormField.Of((ForeignAnnotation)field)!.Name;
        var stored = PageItem.From(thrice)[at].Elements.OfType<ForeignAnnotation>().Select(FormField.Of).FirstOrDefault(f => f?.Name == name)?.StoredValue;
        if (stored != "Musterstadt") problems.Add($"3: Feld {name} = „{stored}“");
        return 3;
    }

    private static bool IsTextField(PageElement element) =>
        element is ForeignAnnotation f && FormField.Of(f) is { Fillable: true, Kind: FormFieldKind.Text, Comb: false, MaxLength: 0 or > 20 };

    /// <summary>A signature of the n-th kind in the middle of the visible page, upright as the page is shown.</summary>
    private static PageElement Place(PdfDocument doc, int pageIndex, int n, CertificationLevel? certify, double at = 0.5)
    {
        var page = doc.Pages[pageIndex];
        var box = page.CropBox;
        var rotated = page.Rotate is 90 or 270;
        var (w, h) = (Math.Min(160, (rotated ? box.Height : box.Width) * 0.6), Math.Min(50, (rotated ? box.Width : box.Height) * 0.2));
        // „at“ runs along the page as it is shown (top to bottom), so several signatures on one page do not overlap
        var (cx, cy) = page.Rotate switch
        {
            90 => (box.Left + box.Width * (1 - at), (box.Bottom + box.Top) / 2),
            180 => ((box.Left + box.Right) / 2, box.Bottom + box.Height * at),
            270 => (box.Left + box.Width * at, (box.Bottom + box.Top) / 2),
            _ => ((box.Left + box.Right) / 2, box.Bottom + box.Height * (1 - at)),
        };
        var rect = rotated ? new PdfRectangle(cx - h / 2, cy - w / 2, cx + h / 2, cy + w / 2) : new PdfRectangle(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2);
        var digital = new DigitalSigning(n % 4 == 2 ? "Max Mustermann · 26.10.2026 12:00" : null, null, certify);
        return (n % 4) switch
        {
            0 => new InkElement { Rect = rect, Rotation = page.Rotate, Strokes = [[0, 20, 40, 50, 90, 10, 140, 45, 175, 25]], StrokeWidth = 3, Color = 0x1B3A8C, Digital = digital },
            1 => new SignatureElement { Rect = rect, Rotation = page.Rotate, Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf", Textured = true, Digital = digital },
            2 => new SignatureElement { Rect = rect, Rotation = page.Rotate, Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf", Textured = false, Digital = digital },
            _ => new ImageElement { Rect = rect, Rotation = page.Rotate, Image = Stamp(), IsSignature = true, Digital = digital },
        };
    }

    /// <summary>A small image signature: a blue wave on transparent ground.</summary>
    private static RgbaImage Stamp()
    {
        const int w = 120, h = 40;
        var pixels = new byte[w * h * 4];
        for (var x = 0; x < w; x++)
            for (var dy = -2; dy <= 2; dy++)
            {
                var y = (int)(h / 2 + 12 * Math.Sin(x / 12.0)) + dy;
                if (y < 0 || y >= h) continue;
                var i = (y * w + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (0x1B, 0x3A, 0x8C, 0xFF);
            }
        return new RgbaImage(w, h, pixels);
    }

    private static byte[] Save(PdfDocument doc, IReadOnlyList<PageItem> pages, PdfSaveOptions options, bool appendOnly, SigningCertificate certificate, string? keep, int step)
    {
        var target = Path.Combine(Path.GetTempPath(), "pdf-knead-pruefung", $"signiert-{Guid.NewGuid():N}.pdf");
        try
        {
            SignedSaving.Save(doc, pages, target, options, appendOnly, certificate, DateTimeOffset.Now);
            var bytes = File.ReadAllBytes(target);
            if (keep is not null) File.WriteAllBytes($"{keep}-schritt{step}.pdf", bytes);
            return bytes;
        }
        finally
        {
            File.Delete(target);
        }
    }

    /// <summary>The signatures made by the steps so far are valid; the newest covers the whole file, the earlier ones later only signing and filling in.</summary>
    private static void Expect(PdfDocument doc, byte[] file, int count, List<string> problems, string step)
    {
        if (doc.WasRepaired) problems.Add($"{step}: Datei musste repariert werden");
        var ours = SignatureVerifier.Verify(doc).Where(c => c.SignerName is "Max Mustermann" or "Erika Mustermann" or "Max Mustermann (Firma)").ToList();
        if (ours.Count != count) problems.Add($"{step}: {ours.Count} statt {count} eigene Signaturen");
        foreach (var check in ours)
        {
            if (!SignatureTexts.IsValid(check))
                problems.Add($"{step}: {check.SignerName} {check.Outcome}: {check.Problem} {string.Join(" ", check.LaterDetails)}");
            else if (check.Later > LaterChanges.FormsAndSignatures)
                problems.Add($"{step}: {check.SignerName} danach {check.Later}");
        }
        if (!ours.Any(c => c.CoversWholeFile)) problems.Add($"{step}: keine Signatur über die ganze Datei");
    }

    /// <summary>
    /// Whether the signature sits where it was placed: the area where the page changes through the signature is found twice — in the
    /// preview (the page with the signature placed, before saving) and in the signed file — and both must agree within a few pixels.
    /// Pixel counts would not do: the ballpoint texture is a quick preview until saving, and edges are smoothed slightly differently.
    /// If neither shows a change (ink multiplied onto a dark area), there is nothing to compare.
    /// </summary>
    private static void Compare(PdfDocument original, PageItem before, PdfDocument copy, int index, List<string> problems, string step)
    {
        var page = original.Pages[index];
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (Math.Min(page.Width, page.Height) < 72) return;   // smaller than an inch: a signature line drawn there is too small to compare
        var scale = Math.Clamp(1500 / Math.Max(page.Width, page.Height), 0.5, 3);
        SKBitmap Render(PdfDocument doc, PdfPage p, IReadOnlyList<PageElement>? elements)
        {
            var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, p, cancel.Token, withoutOwnElements: elements is not null), doc, p, scale, new ImageCache());
            if (elements is not null)
                using (var canvas = new SKCanvas(bitmap)) ElementOverlay.Draw(canvas, p, elements, scale);
            return bitmap;
        }
        using var plain = Render(original, page, [.. before.Elements.Where(e => e.Digital is null)]);
        using var preview = Render(original, page, before.Elements);
        using var signed = Render(copy, copy.Pages[index], null);
        var expected = Changed(plain, preview);
        var found = Changed(plain, signed);
        // ballpoint ink is multiplied like real ink, so on a dark area it changes nothing — in the preview and in the file alike
        if (expected is null && found is null) return;
        if (expected is null) problems.Add($"{step}, S. {index + 1}: Unterschrift nur in der Datei sichtbar, nicht in der Vorschau");
        else if (found is null) problems.Add($"{step}, S. {index + 1}: Unterschrift in der Datei nicht sichtbar");
        else if (!Near(expected.Value, found.Value, 3))
        {
            problems.Add($"{step}, S. {index + 1}: Unterschrift an anderer Stelle (Vorschau {expected}, Datei {found})");
            if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { } folder)
                foreach (var (name, bitmap) in new[] { ("vorschau", preview), ("signiert", signed) })
                    using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                        File.WriteAllBytes(Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(original.FilePath ?? "datei")}-schritt{step}-s{index + 1}-{name}.png"), data.ToArray());
        }
    }

    /// <summary>
    /// The bounding box of the pixels that differ between two renderings of the same size; null if none differ. Differences up to 16
    /// levels per channel do not count: with a multiplied ballpoint signature on the page, translucent areas elsewhere are blended with
    /// slightly different rounding.
    /// </summary>
    private static SKRectI? Changed(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return new SKRectI(0, 0, Math.Max(a.Width, b.Width), Math.Max(a.Height, b.Height));
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
            {
                var i = (y * a.Width + x) * 4;
                if (Math.Abs(pa[i] - pb[i]) <= 16 && Math.Abs(pa[i + 1] - pb[i + 1]) <= 16 && Math.Abs(pa[i + 2] - pb[i + 2]) <= 16) continue;
                (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
            }
        return right < 0 ? null : new SKRectI(left, top, right + 1, bottom + 1);
    }

    private static bool Near(SKRectI a, SKRectI b, int tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance && Math.Abs(a.Top - b.Top) <= tolerance && Math.Abs(a.Right - b.Right) <= tolerance && Math.Abs(a.Bottom - b.Bottom) <= tolerance;
}
