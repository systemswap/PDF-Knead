using System.Diagnostics;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Cli;

/// <summary>
/// "pdf-knead speichertest … --formulare": in every file, fills each fillable field with a suitable value (text with umlauts, digits
/// for comb fields, multi-line, date/number/percent/time in the field's format, check box on, the last radio option and choice),
/// saves, reopens and checks: values, states, image as displayed before, text on the page. Then a second round on the saved file
/// with different values. With "--signiert" every form is signed digitally first and the filling is appended to it (KONZEPT.md,
/// section 5.17, step S5): the signature must stay valid, with later changes no more than filled-in fields.
/// </summary>
internal static class FormCheck
{
    private sealed record Plan(FormField Field, string Value, string Shown);

    public static int Run(IReadOnlyList<string> files, Dictionary<string, string> passwords, int maxPages, bool signed = false)
    {
        int ok = 0, failed = 0, without = 0, skipped = 0, fields = 0, unsignable = 0;
        var certificateFile = Path.Combine(Path.GetTempPath(), "pdf-knead-pruefung", $"formulare-{Guid.NewGuid():N}.p12");
        using var certificate = signed ? Core.Platform.SigningCertificates.CreateOwn("Max Mustermann", certificateFile) : null;
        var kinds = new Dictionary<string, int>();
        var total = Stopwatch.StartNew();
        foreach (var file in files)
        {
            var password = passwords.GetValueOrDefault(Path.GetFileName(file));
            PdfDocument original;
            try
            {
                original = PdfDocument.Open(file, password);
                if (original.PageCount == 0) throw new Core.Pdf.PdfException("keine Seiten");
            }
            catch (Exception)
            {
                skipped++;
                continue;
            }
            var problems = new List<string>();
            string? ours = null;
            if (certificate is not null)
            {
                // Signed first (a small digital signature in the corner of page 1), then filled and appended
                if (!IncrementalUpdate.CanAppend(original) || original.Pages[0].Id is null)
                {
                    unsignable++;
                    original.Dispose();
                    continue;
                }
                var box = original.Pages[0].MediaBox;
                var mark = new InkElement
                {
                    Rect = new PdfRectangle(box.Left + 4, box.Bottom + 4, box.Left + 124, box.Bottom + 44), Strokes = [[0, 10, 40, 30, 80, 5, 116, 25]],
                    StrokeWidth = 2, Color = 0x1B3A8C, Digital = new DigitalSigning(null),
                };
                byte[] signedBytes;
                try
                {
                    signedBytes = Core.Pdf.Signatures.PdfSigner.Sign(original, 0, mark, certificate, DateTimeOffset.Now);
                }
                catch (Exception e)
                {
                    failed++;
                    Console.WriteLine($"{Path.GetFileName(file)}: Signieren: {e.GetType().Name}: {e.Message}");
                    original.Dispose();
                    continue;
                }
                original.Dispose();
                original = PdfDocument.Open(signedBytes, password);
                ours = Core.Pdf.Signatures.DigitalSignatures.Find(original).Single(e => e.ByteRange[2] + e.ByteRange[3] == signedBytes.Length).FullName;
            }
            using (original)
            {
                try
                {
                    var count = Round(original, password, maxPages, round: 1, problems, kinds, out var copy, Path.GetFileNameWithoutExtension(file), ours);
                    if (count == 0)
                    {
                        without++;
                        continue;
                    }
                    fields += count;
                    if (copy is not null)
                        using (copy)
                        {
                            Round(copy, password, maxPages, round: 2, problems, null, out var second, Path.GetFileNameWithoutExtension(file), ours);
                            second?.Dispose();
                        }
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
                Console.WriteLine($"{Path.GetFileName(file)}: {string.Join("; ", problems.Take(4))}");
            }
        }
        Console.WriteLine($"Dateien: {files.Count}, mit Formular: {ok + failed} ({fields} Felder: {string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}), " +
                          $"richtig: {ok}, Abweichungen: {failed}, ohne: {without}, Original unlesbar: {skipped}" +
                          (signed ? $", nicht signierbar: {unsignable}" : "") + $", Zeit {total.Elapsed.TotalSeconds:0.0} s");
        if (signed) Core.Platform.SigningCertificates.RemoveOwn(certificateFile);
        return failed == 0 ? 0 : 1;
    }

    /// <summary>One round: fill, save, reopen, check. Returns the number of filled fields; the saved file is returned in <paramref name="copy"/>.</summary>
    /// <param name="signature">Full name of the signature made before (signed mode: the filling is appended); null = written anew.</param>
    private static int Round(PdfDocument doc, string? password, int maxPages, int round, List<string> problems, Dictionary<string, int>? kinds, out PdfDocument? copy, string name,
        string? signature = null)
    {
        copy = null;
        var items = PageItem.From(doc);
        var pages = Math.Min(items.Length, maxPages);
        var plans = new Dictionary<Core.Pdf.Objects.PdfDictionary, Plan>(ReferenceEqualityComparer.Instance);
        // fields of the same name and kind are linked (one value, as the program fills and writes them): one plan for all of them
        var linked = new Dictionary<(string Name, FormFieldKind Kind), Plan>();
        for (var i = 0; i < pages; i++)
            foreach (var element in items[i].Elements)
            {
                if (element is not ForeignAnnotation foreign || FormField.Of(foreign) is not { Fillable: true } field || plans.ContainsKey(field.Field)) continue;
                (string, FormFieldKind)? link = FieldLinks.Linkable(field.Kind) && FieldLinks.CurrentName(foreign) is { Length: > 0 } linkName && !linkName.Contains('.')
                    ? (linkName, field.Kind) : null;
                if (link is { } key && linked.TryGetValue(key, out var shared)) plans[field.Field] = shared with { Field = field };
                else if (PlanFor(field, round, items, pages) is { } plan)
                {
                    plans[field.Field] = plan;
                    if (link is { } newKey) linked[newKey] = plan;
                }
            }
        if (plans.Count == 0) return 0;
        if (kinds is not null)
            foreach (var plan in plans.Values) kinds[plan.Field.Kind.ToString()] = kinds.GetValueOrDefault(plan.Field.Kind.ToString()) + 1;

        var filled = (PageItem[])items.Clone();
        for (var i = 0; i < pages; i++)
            filled[i] = items[i].WithElements([.. items[i].Elements.Select(e => e is ForeignAnnotation foreign && FormField.Of(foreign) is { } f &&
                                                                                 plans.TryGetValue(f.Field, out var plan)
                ? foreign with { Value = f.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio ? (f.OnState == plan.Value ? plan.Value : "Off") : plan.Value }
                : e)]);

        byte[] bytes;
        var options = new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance };
        if (signature is not null)
        {
            if (!AppendedChanges.CanAppend(doc, items, filled))
            {
                problems.Add($"Runde {round}: Ausfüllen nicht anhängbar");
                return plans.Count;
            }
            bytes = AppendedChanges.Apply(doc, filled, options);
        }
        else
        {
            using var output = new MemoryStream();
            PdfWriter.Write(doc, filled, output, options);
            bytes = output.ToArray();
        }
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { } keep) File.WriteAllBytes(Path.Combine(keep, $"{name}-runde{round}.pdf"), bytes);
        copy = PdfDocument.Open(bytes, password);
        if (copy.WasRepaired) problems.Add($"Runde {round}: Kopie musste repariert werden");
        if (signature is not null)
        {
            // The signature made before stays valid; later only filled-in fields
            var check = Core.Pdf.Signatures.SignatureVerifier.Verify(copy, password).FirstOrDefault(c => c.FieldName == signature);
            if (check is null) problems.Add($"Runde {round}: Signatur fehlt");
            else if (!Core.Pdf.Signatures.SignatureTexts.IsValid(check) || check.Later > Core.Pdf.Signatures.LaterChanges.FormsAndSignatures)
                problems.Add($"Runde {round}: Signatur {check.Outcome}, danach {check.Later}: {check.Problem} {string.Join(", ", check.LaterDetails)}");
        }
        if (copy.PageCount != items.Length)
        {
            problems.Add($"Runde {round}: Seiten {items.Length} → {copy.PageCount}");
            return plans.Count;
        }

        // Values and states — matched per widget by page and position (field names are not always unique)
        static (int, long, long, long, long) Key(int page, PdfRectangle r) =>
            (page, (long)Math.Round(r.Left * 10), (long)Math.Round(r.Bottom * 10), (long)Math.Round(r.Right * 10), (long)Math.Round(r.Top * 10));
        var expected = new Dictionary<(int, long, long, long, long), Plan>();
        for (var i = 0; i < pages; i++)
            foreach (var element in filled[i].Elements)
                if (element is ForeignAnnotation { Value: not null } f && FormField.Of(f) is { } ff && plans.TryGetValue(ff.Field, out var plan))
                    expected.TryAdd(Key(i, f.Rect), plan);
        var again = PageItem.From(copy);
        var seen = 0;
        for (var i = 0; i < pages; i++)
            foreach (var element in again[i].Elements)
            {
                if (element is not ForeignAnnotation foreign || FormField.Of(foreign) is not { } field ||
                    !expected.TryGetValue(Key(i, foreign.Rect), out var plan)) continue;
                seen++;
                if (field.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio)
                {
                    var state = field.OnState == plan.Value ? plan.Value : "Off";
                    if (field.State != state) problems.Add($"Runde {round}, S. {i + 1}: {field.Name} Zustand {field.State} statt {state}");
                    if (field.StoredValue != plan.Value) problems.Add($"Runde {round}: {field.Name} Wert {field.StoredValue} statt {plan.Value}");
                }
                else if (field.StoredValue != plan.Value)
                    problems.Add($"Runde {round}: {field.Name} = „{Short(field.StoredValue)}“ statt „{Short(plan.Value)}“");
            }
        if (seen != expected.Count) problems.Add($"Runde {round}: {expected.Count - seen} von {expected.Count} Widgets fehlen nach dem Speichern");

        if (round == 1 && signature is null) Variants(doc, filled, plans, pages, password, problems);

        // Text on the page (other readers see the value in the field's appearance) and image as displayed before saving
        for (var i = 0; i < pages; i++)
        {
            var text = PageText.Create(PageInterpreter.BuildText(copy, copy.Pages[i])).Text;
            var compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
            foreach (var plan in plans.Values)
                foreach (var widget in Widgets(filled[i], plan.Field))
                    if (Visible(plan, widget) is { Length: > 0 } shown &&
                        !compact.Contains(new string(shown.Where(c => !char.IsWhiteSpace(c)).ToArray()), StringComparison.Ordinal))
                    {
                        problems.Add($"Runde {round}, S. {i + 1}: „{Short(shown)}“ ({plan.Field.Name}) nicht im Text der Seite");
                        break;
                    }
            Compare(doc, filled[i], copy, i, problems, round);
        }
        return plans.Count;
    }

    /// <summary>
    /// Filled and additionally flattened (no fillable field left, the values as text on the page), saved with a password (values
    /// present after decryption) and with the first page twice (file valid, pages readable).
    /// </summary>
    private static void Variants(PdfDocument doc, PageItem[] filled, Dictionary<Core.Pdf.Objects.PdfDictionary, Plan> plans, int pages, string? password,
        List<string> problems)
    {
        using (var flat = SaveAndOpen(doc, filled, new PdfSaveOptions { Flatten = true, ImageCodec = SkiaImageCodec.Instance }, password, problems, "flach"))
            if (flat is not null)
                for (var i = 0; i < Math.Min(pages, flat.PageCount); i++)
                {
                    var left = PageItem.From(flat)[i].Elements.Count(e => e is ForeignAnnotation f && FormField.Of(f) is { Fillable: true });
                    if (left > 0) problems.Add($"flach, S. {i + 1}: {left} Felder übrig");
                    var compact = Compact(PageText.Create(PageInterpreter.BuildText(flat, flat.Pages[i])).Text);
                    foreach (var plan in plans.Values)
                        foreach (var widget in Widgets(filled[i], plan.Field))
                            if (Visible(plan, widget) is { Length: > 0 } shown && !compact.Contains(Compact(shown), StringComparison.Ordinal))
                            {
                                problems.Add($"flach, S. {i + 1}: „{Short(shown)}“ fehlt");
                                break;
                            }
                }
        using (var locked = SaveAndOpen(doc, filled, new PdfSaveOptions { Protection = new PdfProtection("geheim", "besitzer", Core.Pdf.Security.PdfPermissions.Print),
                   ImageCodec = SkiaImageCodec.Instance }, "geheim", problems, "Passwort"))
            if (locked is not null)
            {
                var values = new HashSet<string>();
                foreach (var item in PageItem.From(locked).Take(pages))
                    foreach (var e in item.Elements)
                        if (e is ForeignAnnotation f && FormField.Of(f) is { } ff) values.Add(ff.StoredValue);
                foreach (var plan in plans.Values)
                    if (plan.Field.Kind is FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox && !values.Contains(plan.Value))
                        problems.Add($"Passwort: „{Short(plan.Value)}“ fehlt");
            }
        using (var twice = SaveAndOpen(doc, [filled[0], new PageItem(doc, filled[0].Page), .. filled.Skip(1)], new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance },
                   password, problems, "doppelt"))
            if (twice is not null)
                for (var i = 0; i < Math.Min(2, twice.PageCount); i++)
                    _ = PageInterpreter.Build(twice, twice.Pages[i]);
    }

    private static PdfDocument? SaveAndOpen(PdfDocument doc, PageItem[] items, PdfSaveOptions options, string? password, List<string> problems, string what)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, options);
        var copy = PdfDocument.Open(output.ToArray(), password);
        if (copy.WasRepaired) problems.Add($"{what}: Kopie musste repariert werden");
        if (copy.PageCount == items.Length) return copy;
        problems.Add($"{what}: Seiten {items.Length} → {copy.PageCount}");
        copy.Dispose();
        return null;
    }

    /// <summary>
    /// What a field shows of its planned text: a comb field as many characters as it has cells (a field linked to one of another length gets
    /// the longer value).
    /// </summary>
    private static string Visible(Plan plan, FormField widget) =>
        widget.Comb && plan.Shown.Length > widget.MaxLength ? plan.Shown[..widget.MaxLength] : plan.Shown;

    private static string Compact(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static bool OnPage(PageItem item, FormField field) => Widgets(item, field).Any();

    /// <summary>The visible widgets of a field on a page that show its text (as their own field: the widget's /MaxLen counts).</summary>
    private static IEnumerable<FormField> Widgets(PageItem item, FormField field) =>
        item.Elements.Select(e => e is ForeignAnnotation f && FormField.Of(f) is { } ff && ReferenceEquals(ff.Field, field.Field) && f.Value is not null &&
                                  ff.Kind is FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox && !ff.Password &&
                                  (f.Original.GetInteger(Core.Pdf.Objects.PdfName.Get("F")) ?? 0) is var flags && (flags & (2 | 32)) == 0 ? ff : null)
            .OfType<FormField>();

    private static string Short(string s) => s.Length <= 30 ? s.Replace("\n", "⏎") : s[..30].Replace("\n", "⏎") + "…";

    /// <summary>Suitable value per field kind (different values in round 2); null if the field cannot be planned.</summary>
    private static Plan? PlanFor(FormField field, int round, PageItem[] items, int pages)
    {
        switch (field.Kind)
        {
            case FormFieldKind.CheckBox:
                return field.OnState is { } on ? new Plan(field, round == 1 ? on : "Off", "") : null;
            case FormFieldKind.Radio:
                // Option of the group: the last in round 1, the first in round 2
                var states = new List<string>();
                for (var i = 0; i < pages; i++)
                    foreach (var e in items[i].Elements)
                        if (e is ForeignAnnotation f && FormField.Of(f) is { } ff && ReferenceEquals(ff.Field, field.Field) && ff.OnState is { } s && !states.Contains(s))
                            states.Add(s);
                return states.Count == 0 ? null : new Plan(field, round == 1 ? states[^1] : states[0], "");
            case FormFieldKind.ComboBox or FormFieldKind.ListBox:
                var options = field.Options;
                if (options.Count == 0) return field.Editable ? new Plan(field, round == 1 ? "Eigener Wert" : "Anderer Wert", round == 1 ? "Eigener Wert" : "Anderer Wert") : null;
                var (export, display) = round == 1 ? options[^1] : options[0];
                return new Plan(field, export, display);
            case FormFieldKind.Text:
                var format = FieldFormat.Of(field);
                string value = format.Kind switch
                {
                    FieldFormatKind.Date => format.Format(round == 1 ? new DateTime(2026, 10, 17) : new DateTime(2027, 1, 5)),
                    FieldFormatKind.Time => format.Format(new DateTime(2026, 10, 17, round == 1 ? 9 : 17, 5, 30)),
                    FieldFormatKind.Number => round == 1 ? "1234.5" : "-42",
                    FieldFormatKind.Percent => round == 1 ? "0.125" : "1",
                    FieldFormatKind.Special => field.Special(round),
                    _ when field.Comb => Digits(field.MaxLength, round),
                    _ when field.Multiline => round == 1 ? "Max Mustermann\nMusterstraße 1" : "Zweite Runde\nÄnderung",
                    _ when field.Password => "geheim",
                    _ => round == 1 ? "Max Mustermann äöü ß 123" : "Zweite Runde",
                };
                if (field.MaxLength > 0 && value.Length > field.MaxLength) value = value[..field.MaxLength];
                var shown = format.Kind == FieldFormatKind.None ? value : format.Display(value);
                return new Plan(field, value, field.Password ? "" : shown);
            default:
                return null;
        }
    }

    private static string Digits(int length, int round) =>
        new(Enumerable.Range(0, Math.Min(length, 40)).Select(i => (char)('0' + (i + round) % 10)).ToArray());

    private static string Special(this FormField field, int round) => FieldFormat.Of(field).Special switch
    {
        0 => round == 1 ? "12345" : "54321",
        1 => "123456789",
        2 => "0301234567",
        _ => "123456789",
    };

    /// <summary>Compares the display before saving (page without foreign annotations, elements on top) with the saved page and its annotations.</summary>
    private static void Compare(PdfDocument original, PageItem before, PdfDocument copy, int index, List<string> problems, int round)
    {
        var page = original.Pages[index];
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var scale = Math.Min(0.5, 1500 / Math.Max(page.Width, page.Height));
        using var a = SkiaRenderer.RenderPage(PageInterpreter.Build(original, page, cancel.Token, withoutOwnElements: true), original, page, scale, new ImageCache());
        using (var canvas = new SKCanvas(a)) ElementOverlay.Draw(canvas, page, before.Elements, scale);
        using var b = SkiaRenderer.RenderPage(PageInterpreter.Build(copy, copy.Pages[index], cancel.Token), copy, copy.Pages[index], scale, new ImageCache());
        if (SaveCheck.Differing(a, b) is var differing and > 0)
        {
            problems.Add($"Runde {round}, S. {index + 1}: {differing} Pixel anders");
            if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { } folder)
                foreach (var (name, bitmap) in new[] { ("vorher", a), ("gespeichert", b) })
                    using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                        File.WriteAllBytes(Path.Combine(folder, $"r{round}-s{index + 1}-{name}.png"), data.ToArray());
        }
    }
}
