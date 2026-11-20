using System.Diagnostics;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.EInvoice;

/// <summary>
/// The command „pdf-knead erechnung &lt;datei|ordner&gt; [--einzeln] [--anzeigen] [--abgleich] [--werte &lt;ordner&gt;] [--xml &lt;ordner&gt;]“ of the e-invoice module: what the checker finds in PDFs and loose XML files (KONZEPT.md,
/// section 5.24, stage 1) — the invoice XML and the other attachments, XMP data, syntax, profile, whether that profile is an e-invoice in
/// Germany, why an XML was refused, and the findings of the cross-checks (several invoices, XMP, /AF, revisions, signature). Over a folder it
/// counts per syntax and profile and per finding, and names the slowest file and the one that took most memory. With --anzeigen it reads
/// each invoice into the readable form of stage 2 and shows it (one file) or counts it (a folder: read, values without a mapping). With
/// --abgleich it compares each value of a PDF's invoice with the visible text of the PDF (stage 3) and shows the status per value (one file)
/// or counts the statuses (a folder). With --speichertest each PDF with an invoice is saved in several ways and read back (InvoiceSaveCheck;
/// --speichertest-aus keeps the PDF/A-3b copies).
/// </summary>
public static class EInvoiceCommand
{
    public static int Run(string path, string[] args)
    {
        var single = args.Contains("--einzeln");
        var show = args.Contains("--anzeigen");
        var compare = args.Contains("--abgleich");
        var rules = args.Contains("--regeln");
        // --regeln-aus <ordner>: per file the identifiers of the violated rules, one per line — for the comparison with the reference checker
        var rulesTo = Array.IndexOf(args, "--regeln-aus") is var ri and >= 0 && ri + 1 < args.Length ? args[ri + 1] : null;
        if (rulesTo is not null)
        {
            rules = true;
            Directory.CreateDirectory(rulesTo);
        }
        // --stellen-aus <ordner>: per file every finding with a place as „id TAB place“, none left out — for the comparison place by place
        var placesTo = Array.IndexOf(args, "--stellen-aus") is var pi and >= 0 && pi + 1 < args.Length ? args[pi + 1] : null;
        if (placesTo is not null)
        {
            rules = true;
            Directory.CreateDirectory(placesTo);
        }
        // --werte <ordner>: per file the terms as „id TAB value“ (document order) — for the comparison with the KoSIT visualization
        var valuesTo = Array.IndexOf(args, "--werte") is var vi and >= 0 && vi + 1 < args.Length ? args[vi + 1] : null;
        if (valuesTo is not null)
        {
            show = true;
            Directory.CreateDirectory(valuesTo);
        }
        // --xml <ordner>: the invoice XML of each PDF as a file of its own (the comparison takes the embedded invoices too)
        var xmlTo = Array.IndexOf(args, "--xml") is var xi and >= 0 && xi + 1 < args.Length ? args[xi + 1] : null;
        if (xmlTo is not null) Directory.CreateDirectory(xmlTo);
        // --pdfregeln: the rules for hybrid documents (stage 4, part h) — apart from --regeln, whose files the reference checkers of the XML
        // are compared with; --pdfregeln-aus <ordner>: per PDF the identifiers, one per line, with the value after a TAB
        var pdfRules = args.Contains("--pdfregeln");
        var pdfRulesTo = Array.IndexOf(args, "--pdfregeln-aus") is var hi and >= 0 && hi + 1 < args.Length ? args[hi + 1] : null;
        if (pdfRulesTo is not null)
        {
            pdfRules = true;
            Directory.CreateDirectory(pdfRulesTo);
        }
        // --einkauf: what the box „Für den Einkauf“ shows (payment, its issues, the mandatory details of § 14 UStG) — over the collections, to
        // see whether it holds outside the tests (decided on 15.11.2026)
        var purchase = args.Contains("--einkauf");
        // --speichertest: each PDF with an invoice saved in several ways and read back — the invoice must stay as it was (InvoiceSaveCheck)
        var saveTest = args.Contains("--speichertest");
        // --speichertest-aus <ordner>: the PDF/A-3b copies kept there (for the check with the standalone veraPDF)
        var saveTestTo = Array.IndexOf(args, "--speichertest-aus") is var si and >= 0 && si + 1 < args.Length ? args[si + 1] : null;
        if (saveTestTo is not null)
        {
            saveTest = true;
            Directory.CreateDirectory(saveTestTo);
        }
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).ToList()
            : [path];
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var findings = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var slowest = (Name: "", Ms: 0L);
        var hungriest = (Name: "", Mb: 0L);
        var watch = new Stopwatch();
        foreach (var file in files)
        {
            _currentFile = Path.GetFileName(file);
            watch.Restart();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var lines = new List<string>();
            try
            {
                if (file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    var location = InvoiceLocator.Read(File.ReadAllBytes(file));
                    Describe(location, "(Datei)", lines, counts, findings, show, single || files.Count == 1);
                    if (rules && location is { Xml: { } rx, Kind: { } rk } && ReadOrNull(rx, rk) is { } rd)
                        Rules(rd, lines, counts, single || files.Count == 1, rulesTo is null ? null : Path.Combine(rulesTo, Path.GetFileName(file) + ".txt"),
                            placesTo is null ? null : Path.Combine(placesTo, Path.GetFileName(file) + ".txt"));
                    if (purchase && location is { Xml: { } px, Kind: { } pk } && ReadOrNull(px, pk) is { } pd) Purchase(null, pd, lines, counts, pk);
                    if (valuesTo is not null && location is { Xml: { } vx, Kind: { } vk } && ReadOrNull(vx, vk) is { } vd)
                        File.WriteAllLines(Path.Combine(valuesTo, Path.GetFileName(file) + ".tsv"),
                            vd.Root.AllFields().Select(f => f.Id + "\t" + Value(f)), new System.Text.UTF8Encoding(false));
                }
                else
                {
                    using var doc = PdfDocument.Open(file);
                    var location = InvoiceLocator.Locate(doc);
                    if (xmlTo is not null && location.Data is { } xmlData)
                        File.WriteAllBytes(Path.Combine(xmlTo, $"{files.IndexOf(file):D4}-{Path.GetFileNameWithoutExtension(file)}.xml"), xmlData);
                    if (location.Xmp is { } xmp)
                        lines.Add($"  XMP: {Short(xmp.DocumentType)} {Short(xmp.DocumentFileName)} Version {Short(xmp.Version)} {Short(xmp.ConformanceLevel)}");
                    foreach (var a in location.Attachments)
                    {
                        lines.Add($"  Beilage „{Short(a.DisplayName)}“ {Short(a.Relationship ?? "ohne AFRelationship")} {Short(a.MimeType ?? "ohne Subtype")} ({Where(a)})");
                        Count(counts, "Beilage");
                    }
                    if (!location.HasInvoice)
                    {
                        lines.Add("  keine Rechnungs-XML");
                        Count(counts, "PDF ohne Rechnungs-XML");
                    }
                    foreach (var other in location.OtherInvoices) lines.Add($"  weitere Rechnungs-XML „{Short(other.DisplayName)}“ ({Where(other)})");
                    if (location.File is { } f)
                        lines.Add($"  Rechnung „{Short(f.DisplayName)}“ {Short(f.Relationship ?? "ohne AFRelationship")} {Short(f.MimeType ?? "ohne Subtype")} ({Where(f)})");
                    Describe(location, location.File?.DisplayName ?? "", lines, counts, findings, show, single || files.Count == 1);
                    if (compare && location is { Xml: { } cx, Kind: { } ck } && ReadOrNull(cx, ck) is { } cd)
                        Compare(doc, cd, lines, counts, single || files.Count == 1);
                    if (rules && location is { Xml: { } rx, Kind: { } rk } && ReadOrNull(rx, rk) is { } rd)
                        Rules(rd, lines, counts, single || files.Count == 1,
                            rulesTo is null ? null : Path.Combine(rulesTo, $"{files.IndexOf(file):D4}-{Path.GetFileNameWithoutExtension(file)}.xml.txt"),
                            placesTo is null ? null : Path.Combine(placesTo, $"{files.IndexOf(file):D4}-{Path.GetFileNameWithoutExtension(file)}.xml.txt"));
                    if (purchase && location is { Xml: { } px, Kind: { } pk } && ReadOrNull(px, pk) is { } pd) Purchase(doc, pd, lines, counts, pk);
                    if (saveTest && location.HasInvoice)
                        foreach (var (way, difference) in InvoiceSaveCheck.Run(doc, location,
                                     saveTestTo is null ? null : Path.Combine(saveTestTo, $"{files.IndexOf(file):D4}-{Path.GetFileNameWithoutExtension(file)}-a3b.pdf")))
                        {
                            Count(counts, difference is null ? $"Speichern {way}: gleich" : $"Speichern {way}: {difference}");
                            if (difference is not null)
                            {
                                lines.Add($"    Speichern {way}: {difference}");
                                if (_saveDifferences.Count < 200) _saveDifferences.Add($"  {_currentFile}: {way}: {difference}");
                            }
                        }
                    if (pdfRules && location is { Xml: { } hx, Kind: { } hk } && ReadOrNull(hx, hk) is { } hd)
                        HybridRules(doc, location, hd, lines, counts, single || files.Count == 1, pdfRulesTo is null ? null
                            // the path below the folder, „__“ for the separators: files of the same name in subfolders stay apart
                            : Path.Combine(pdfRulesTo, (Directory.Exists(path) ? Path.GetRelativePath(path, file) : Path.GetFileName(file))
                                .Replace(Path.DirectorySeparatorChar.ToString(), "__") + ".txt"));
                }
            }
            // also a path the file system refuses (empty, invalid characters): a line for the file, not an abort of the run (FG 841)
            catch (Exception e) when (e is PdfException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException)
            {
                lines.Add("  PDF nicht lesbar: " + e.GetType().Name);
                Count(counts, "PDF nicht lesbar");
            }
            watch.Stop();
            var mb = (GC.GetAllocatedBytesForCurrentThread() - allocated) >> 20;
            if (watch.ElapsedMilliseconds > slowest.Ms) slowest = (Path.GetFileName(file), watch.ElapsedMilliseconds);
            if (mb > hungriest.Mb) hungriest = (Path.GetFileName(file), mb);
            if (single || files.Count == 1)
            {
                Console.WriteLine($"{Path.GetFileName(file)} ({watch.ElapsedMilliseconds} ms, {mb} MB angelegt)");
                foreach (var line in lines) Console.WriteLine(line);
            }
        }
        Console.WriteLine($"{files.Count} Dateien");
        foreach (var (key, n) in counts) Console.WriteLine($"  {n,5}  {key}");
        foreach (var (key, n) in findings) Console.WriteLine($"  {n,5}  {key}");
        Console.WriteLine($"  langsamste: {slowest.Name} ({slowest.Ms} ms); meister Speicher: {hungriest.Name} ({hungriest.Mb} MB angelegt, "
            + $"Höchststand des Prozesses {Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB)");
        if (_mostWork.PerElement > 0) Console.WriteLine($"  höchster Aufwand beim Lesen: {_mostWork.Name} ({_mostWork.PerElement:0.0} je Element)");
        if (_purchaseFindings.Count > 0 && !(single || files.Count == 1))
        {
            Console.WriteLine("Für den Einkauf — Warnungen und fehlende Pflichtangaben:");
            foreach (var line in _purchaseFindings) Console.WriteLine(line);
        }
        if (_saveDifferences.Count > 0 && !(single || files.Count == 1))
        {
            Console.WriteLine("Abweichungen nach dem Speichern:");
            foreach (var line in _saveDifferences) Console.WriteLine(line);
        }
        return 0;
    }

    /// <summary>
    /// The box „Für den Einkauf“ as the panel shows it (KONZEPT.md, section 5.24, E1/E2): payment facts and their issues — with the comparison
    /// against the visible text for a PDF — and the mandatory details missing; counted, and named per file over a folder.
    /// </summary>
    private static void Purchase(PdfDocument? doc, InvoiceData data, List<string> lines, SortedDictionary<string, int> counts, InvoiceKind profile)
    {
        var payment = InvoicePayment.Of(data);
        InvoiceComparisonResult? comparison = null;
        if (doc is not null)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            comparison = InvoiceComparison.Compare(doc, data, limit.Token);
        }
        if (payment.HasContent)
        {
            Count(counts, "Einkauf: Zahlungskasten");
            lines.Add($"    Einkauf: {string.Join(", ", payment.Accounts.Select(a => (InvoicePayment.LooksLikeIban(a.Iban.Text!) ? InvoicePayment.Grouped(a.Iban.Text!) : Short(a.Iban.Text!)) + (a.Valid ? "" : " (Prüfziffer falsch)")))}"
                      + $"; zu zahlen {Short(payment.Amount?.Text ?? "–")}; fällig {Short(payment.Due?.Text ?? "–")}{(payment.DirectDebit ? "; Lastschrift" : "")}"
                      + $"{(payment.PayeeIsSeller ? "" : "; anderer Zahlungsempfänger")}");
        }
        else Count(counts, "Einkauf: keine Zahlungsangaben");
        // no account in a profile that is no e-invoice: the visible part is the invoice — no warning (as in the panel)
        foreach (var (kind, field) in payment.Issues(comparison).Where(i => i.Kind != InvoicePaymentIssue.NoAccount || InvoiceMandatory.Applies(profile)))
        {
            Count(counts, "Einkauf-Warnung: " + kind);
            var line = $"Warnung {kind}: {Short(field.Text ?? "")}";
            lines.Add("    " + line);
            if (_purchaseFindings.Count < 400) _purchaseFindings.Add($"  {_currentFile}: {line}");
        }
        if (!InvoiceMandatory.Applies(profile))
        {
            Count(counts, "Pflichtangaben: Profil ist keine E-Rechnung (sichtbarer Teil maßgeblich)");
            return;
        }
        foreach (var item in InvoiceMandatory.Check(data))
        {
            Count(counts, $"Pflichtangabe {item.Key}: {item.State}");
            if (item.State != MandatoryState.Missing) continue;
            var line = $"Pflichtangabe fehlt {item.Key} ({string.Join(", ", item.Missing)})";
            lines.Add("    " + line);
            if (_purchaseFindings.Count < 400) _purchaseFindings.Add($"  {_currentFile}: {line}");
        }
    }

    /// <summary>The issues and missing details of --einkauf over a folder, named per file (at most 400).</summary>
    private static readonly List<string> _purchaseFindings = [];

    /// <summary>The differences of --speichertest over a folder, named per file (at most 200).</summary>
    private static readonly List<string> _saveDifferences = [];

    /// <summary>Stage 4: the business rules — per violation with its place (one file), counted per rule; the identifiers to a file if asked.</summary>
    private static void Rules(InvoiceData data, List<string> lines, SortedDictionary<string, int> counts, bool detail, string? to, string? placesTo = null)
    {
        // for the places none left out; otherwise as shown
        var violations = InvoiceRules.Check(data, placesTo is null ? InvoiceRules.MaxListedPerRule : int.MaxValue);
        lines.Add(violations.Count == 0
            ? $"    Regeln ({InvoiceRules.Source}): keine Auffälligkeit gefunden"
            : $"    Regeln ({InvoiceRules.Source}): {violations.Count} Auffälligkeiten");
        foreach (var v in violations)
        {
            Count(counts, "Stufe 4: " + v.Rule.Id);
            if (detail)
                lines.Add($"      {v.Rule.Id}: {v.Rule.Description} — {v.Group.Label}" + (v.Field is { } f ? $" / {f.Label} = {Short(f.Text)}" : "")
                    + (v.Place is not null ? $" — Stelle {Short(v.Place)}" + (v.Value is not null ? $" = „{Short(v.Value)}“" : "")
                        : v.Rule.Source == InvoiceRules.CodeSource || v.Value is not null ? $" — Code „{Short(v.Value ?? "")}“" : "")
                    + (v.Further > 0 ? $" (und {v.Further} weitere)" : ""));
        }
        if (violations.Count == 0) Count(counts, "Stufe 4: keine Auffälligkeit");
        if (to is not null) File.WriteAllLines(to, violations.Select(v => v.Rule.Id).Distinct().Order(StringComparer.Ordinal));
        if (placesTo is not null) File.WriteAllLines(placesTo, violations.Where(v => v.Place is not null).Select(v => v.Rule.Id + "\t" + v.Place));
    }

    /// <summary>Stage 4, part h: the rules for hybrid documents — per violation with its value and place (one file), counted per rule.</summary>
    private static void HybridRules(PdfDocument doc, InvoiceLocation location, InvoiceData data, List<string> lines, SortedDictionary<string, int> counts,
        bool detail, string? to)
    {
        var violations = InvoiceHybridRules.Check(doc, location, data);
        lines.Add(violations.Count == 0
            ? $"    PDF-Regeln ({InvoiceHybridRules.Source}): keine Auffälligkeit gefunden"
            : $"    PDF-Regeln ({InvoiceHybridRules.Source}): {violations.Count} Auffälligkeiten");
        foreach (var v in violations)
        {
            Count(counts, "Stufe 4h: " + v.Rule.Id);
            if (detail)
                lines.Add($"      {v.Rule.Id} ({v.Rule.Severity}): {v.Rule.Description} — {Short(v.Place ?? "")}" + (v.Value is not null ? $" = „{Short(v.Value)}“" : ""));
        }
        if (violations.Count == 0) Count(counts, "Stufe 4h: keine Auffälligkeit");
        if (to is not null) File.WriteAllLines(to, violations.Select(v => v.Rule.Id + "\t" + (v.Value ?? "")), new System.Text.UTF8Encoding(false));
    }

    /// <summary>Stage 3: each value against the visible text of the PDF — per value with its status (one file), counted per status.</summary>
    private static void Compare(PdfDocument doc, InvoiceData data, List<string> lines, SortedDictionary<string, int> counts, bool detail)
    {
        var watch = Stopwatch.StartNew();
        var result = InvoiceComparison.Compare(doc, data);
        lines.Add($"    Abgleich ({watch.ElapsedMilliseconds} ms, {result.PagesChecked} von {result.Pages} Seiten): "
            + string.Join(", ", Enum.GetValues<InvoiceMatchStatus>().Where(s => result.Count(s) > 0)
                .Select(s => $"{result.Count(s)} {InvoiceComparison.Describe(s)}")));
        foreach (var m in result.Matches)
        {
            Count(counts, "Stufe 3: " + InvoiceComparison.Describe(m.Status));
            if (!detail) continue;
            var where = m.Place is { } p ? $" — S. {p.Page} „{Short(p.Form)}“" : "";
            var note = m.Note is null ? "" : $" ({Short(m.Note)})";
            var unmeasured = m.VisibilityChecked ? "" : " [Sichtbarkeit nicht vollständig geprüft]";
            lines.Add($"      {InvoiceComparison.Describe(m.Status)}: {m.Field.Label} ({m.Field.Id}) = {Short(InvoiceFormat.Display(m.Field, data))}{where}{note}{unmeasured}");
        }
    }

    /// <summary>The readable invoice for the values file; null without a mapping or if it was refused as too costly (FG 826).</summary>
    private static InvoiceData? ReadOrNull(InvoiceXmlDocument xml, InvoiceKind kind)
    {
        try
        {
            return InvoiceReader.Read(xml, kind);
        }
        catch (InvoiceXmlException)
        {
            return null;
        }
    }

    private static string Where(InvoiceFile f) =>
        string.Join(", ", f.Places.Select(p => p.Place == InvoiceFilePlace.Annotation ? $"Anmerkung S. {p.Page}" : p.Place.ToString()));

    private static void Describe(InvoiceLocation location, string name, List<string> lines, SortedDictionary<string, int> counts,
        SortedDictionary<string, int> findings, bool show, bool detail)
    {
        if (show && location.Kind is { } k && location.Xml is { } x)
        {
            var readWatch = Stopwatch.StartNew();
            var readBytes = GC.GetAllocatedBytesForCurrentThread();
            InvoiceData? data = null;
            InvoiceXmlRejection? refused = null;
            try
            {
                data = InvoiceReader.Read(x, k);
            }
            catch (InvoiceXmlException e)
            {
                refused = e.Reason;   // too costly to read (FG 826): refused with its reason, like the reader refuses
            }
            readWatch.Stop();
            readBytes = (GC.GetAllocatedBytesForCurrentThread() - readBytes) >> 20;
            if (refused is { } why)
            {
                lines.Add($"    lesbar: abgelehnt ({InvoiceLocator.Describe(why)})");
                Count(counts, $"Stufe 2: abgelehnt: {why}");
            }
            else if (data is null) Count(counts, "Stufe 2: keine Zuordnung");
            else
            {
                Count(counts, "Stufe 2: gelesen");
                if (data.Further.Count > 0) Count(counts, "Stufe 2: mit weiteren Angaben");
                if (string.IsNullOrEmpty(data.Text("BT-1"))) Count(counts, "Stufe 2: ohne Rechnungsnummer");
                var work = data.Work / (double)Math.Max(x.Elements, 1);
                if (work > _mostWork.PerElement) _mostWork = (_currentFile, work);
                lines.Add($"    lesbar: {data.Root.AllFields().Count()} Angaben, {data.Further.Count} weitere (Lesen {readWatch.ElapsedMilliseconds} ms, {readBytes} MB, "
                    + $"Aufwand {work:0.0} je Element) — {data.Mapping}");
                if (data.Note is { } readNote) lines.Add("    " + readNote);
                if (detail) Show(data, 3, lines);
                foreach (var finding in InvoiceFormat.Check(data))
                {
                    var weight = finding.Weight == InvoiceFindingWeight.Finding ? "Auffälligkeit" : "Hinweis";
                    lines.Add($"    {weight} (Darstellung): {finding.Text}");
                    Count(findings, $"Stufe 2, {weight}: {finding.Kind}");
                }
                foreach (var f in data.Further.Take(40)) lines.Add($"      weitere Angabe {Short(f.Path)} = {Short(f.Text)}");
                if (data.Further.Count > 40) lines.Add($"      … {data.Further.Count - 40} weitere");
            }
        }
        if (location.Kind is { } kind && location.Xml is { } xml)
        {
            var german = kind.EInvoiceInGermany switch { true => "E-Rechnung (DE)", false => "keine E-Rechnung (DE)", _ => "nicht eingeordnet" };
            lines.Add($"    {name}: {kind.Syntax}, {kind.Profile} „{kind.Guideline}“, EN 16931 {kind.Norm ?? "–"}, {german}; {xml.Elements} Elemente, Tiefe {xml.MaxDepthSeen}"
                + (kind.Syntax == InvoiceSyntax.Unknown ? $", Wurzel {{{xml.Root.Namespace}}}{xml.Root.Local}" : ""));
            Count(counts, $"{kind.Syntax} {kind.Profile}");
        }
        else if (location.Refused is { } reason)
        {
            lines.Add($"    {name}: abgelehnt ({reason})");
            Count(counts, $"abgelehnt: {reason}");
        }
        foreach (var finding in location.Findings)
        {
            var weight = finding.Weight == InvoiceFindingWeight.Finding ? "Auffälligkeit" : "Hinweis";
            lines.Add($"    {weight}: {finding.Text}");
            Count(findings, $"{weight}: {finding.Kind}");
        }
    }

    private static void Count(SortedDictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

    /// <summary>The file whose reading visited the most nodes per element (the measure for the work limit of the reader, FG 826).</summary>
    private static (string Name, double PerElement) _mostWork;

    /// <summary>The file being checked (for the summary).</summary>
    private static string _currentFile = "";

    /// <summary>The readable invoice as indented lines: groups with their label, terms as „label (BT-x): value“.</summary>
    private static void Show(InvoiceData data, int depth, List<string> lines)
    {
        lines.Add("    " + InvoiceData.Disclaimer);
        var pending = new Stack<(InvoiceGroup Group, int Depth)>();
        pending.Push((data.Root, depth));
        while (pending.Count > 0)
        {
            var (g, d) = pending.Pop();
            var pad = new string(' ', d * 2);
            if (g.Id != "INVOICE") lines.Add($"{pad}[{g.Label} ({g.Id})]");
            foreach (var f in g.Fields)
                lines.Add($"{pad}  {f.Label} ({f.Id}): {Short(InvoiceFormat.Display(f, data))}");
            for (var i = g.Groups.Count - 1; i >= 0; i--) pending.Push((g.Groups[i], d + 1));
        }
    }

    /// <summary>A value for the comparison: the text with line breaks as spaces, an attachment as its base64.</summary>
    private static string Value(InvoiceField f) =>
        f.Node is { IsAttribute: false, Element.Binary: { } binary } ? "base64:" + Convert.ToBase64String(binary)
        : (f.Text ?? "").ReplaceLineEndings(" ").Replace('\t', ' ');

    /// <summary>A value of the file on one line of the console: plain text (no control character reaches the terminal), cut after 100 characters.</summary>
    private static string Short(string? text)
    {
        if (text is null) return "";
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return InvoiceFormat.Plain(text, 100);
    }
}
