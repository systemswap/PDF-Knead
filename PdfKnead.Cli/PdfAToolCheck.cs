using System.Text.RegularExpressions;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Cli;

/// <summary>
/// PDF/A on the command line (KONZEPT.md, section 5.21; docs/PRUEFUNGEN.md, section 6p): save a file as PDF/A, and check files — a folder
/// of the veraPDF test files (names with „pass“ or „fail“) is measured: which valid files the check rejects, which invalid ones it misses.
/// </summary>
internal static partial class PdfAToolCheck
{
    /// <summary>Level a: the language and the answers for the figures (--sprache, --schmuck, --alt &lt;key&gt;=&lt;text&gt;).</summary>
    public static PdfAAccessibility? Accessibility { get; set; }

    /// <summary>Level a from the arguments: --sprache &lt;tag&gt;, --schmuck (every figure without an answer is decoration), --alt &lt;key&gt;=&lt;text&gt; (repeated).</summary>
    public static PdfAAccessibility? AccessibilityFrom(string[] args)
    {
        var language = Array.IndexOf(args, "--sprache") is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : "";
        var figures = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--alt" && args[i + 1].IndexOf('=') is var eq and > 0)
                figures[args[i + 1][..eq]] = args[i + 1][(eq + 1)..] is { Length: > 0 } text ? text : null;
        return new PdfAAccessibility { Language = language, Figures = figures, AllDecorative = args.Contains("--schmuck") };
    }

    /// <summary>pdf-knead pdfa &lt;file&gt; &lt;output&gt; [--teil 2b|3b] [--trotzdem]: written only if it conforms (with --trotzdem always, for analysis).</summary>

    public static int Convert(string file, string output, PdfAConformance part, bool anyway, string? password)
    {
        if (Directory.Exists(file)) return ConvertAll(file, output, part, anyway);
        using var doc = PdfDocument.Open(file, password);
        var result = PdfAExport.Write(doc, PageItem.From(doc), part, accessibility: Accessibility);
        foreach (var v in result.Violations) Console.WriteLine($"{v.Rule,-11} {(v.Page is { } p ? $"S{p} " : "")}{v.Text}");
        if (result.Conforms || anyway) File.WriteAllBytes(output, result.Bytes);
        Console.WriteLine(result.Conforms ? Tr.T("Als PDF/A gespeichert.") : anyway ? Tr.T("Gespeichert, erfüllt PDF/A aber nicht.") : Tr.T("Nicht gespeichert: erfüllt PDF/A nicht."));
        return result.Conforms ? 0 : 1;
    }

    /// <summary>
    /// A folder: every file written as PDF/A into <paramref name="output"/> (only those that conform, with --trotzdem all) and the violations
    /// counted by rule — which rules the writer does not yet meet, over a whole test collection.
    /// </summary>
    private static int ConvertAll(string folder, string output, PdfAConformance part, bool anyway)
    {
        Directory.CreateDirectory(output);
        var passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(Path.Combine(folder, "_passwoerter.txt")))
            foreach (var line in File.ReadAllLines(Path.Combine(folder, "_passwoerter.txt")))
                if (line.Split('\t') is [var pdfName, var pw]) passwords[pdfName] = pw;
        int files = 0, conforming = 0, failed = 0;
        var byText = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*.pdf").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            files++;
            try
            {
                using var doc = PdfDocument.Open(file, passwords.GetValueOrDefault(name));
                var result = PdfAExport.Write(doc, PageItem.From(doc), part, accessibility: Accessibility);
                if (result.Conforms) conforming++;
                foreach (var v in result.Violations)
                    (byText.TryGetValue($"{v.Rule} {v.Text}", out var list) ? list : byText[$"{v.Rule} {v.Text}"] = []).Add(name);
                if (result.Conforms || anyway) File.WriteAllBytes(Path.Combine(output, name), result.Bytes);
            }
            catch (Exception e) when (e is PdfException or IOException or InvalidOperationException or ArgumentException)
            {
                failed++;
                Console.WriteLine($"FEHLER {name}: {e.GetType().Name}: {e.Message}");
            }
        }
        foreach (var (text, names) in byText.OrderByDescending(e => e.Value.Count))
            Console.WriteLine($"{names.Count,5}  {text}   z. B. {string.Join(", ", names.Take(4))}");
        Console.WriteLine($"Dateien: {files}, als PDF/A gültig: {conforming}, nicht gültig: {files - conforming - failed}, Fehler: {failed}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>pdf-knead pdfa-pruefen &lt;file|folder&gt;: the violations of one file, or the measurement over a folder.</summary>
    public static int Check(string path, bool verbose)
    {
        if (!Directory.Exists(path))
        {
            using var doc = PdfDocument.Open(path);
            var found = PdfACheck.Check(doc);
            foreach (var v in found) Console.WriteLine($"{v.Rule,-11} {(v.Page is { } p ? $"S{p} " : "")}{v.Text}");
            Console.WriteLine(found.Count == 0 ? Tr.T("Erfüllt die geprüften Regeln von PDF/A.") : Tr.T("{0} Regeln nicht erfüllt.", found.Count));
            return found.Count == 0 ? 0 : 1;
        }
        int pass = 0, passRejected = 0, fail = 0, failFound = 0, failRight = 0, unreadable = 0;
        var missedByRule = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var rejectedByRule = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var expectPass = name.Contains("pass", StringComparison.OrdinalIgnoreCase);
            var expectFail = name.Contains("fail", StringComparison.OrdinalIgnoreCase);
            if (!expectPass && !expectFail) continue;
            IReadOnlyList<PdfAViolation> found;
            try
            {
                using var doc = PdfDocument.Open(file);
                found = PdfACheck.Check(doc);
            }
            catch (Exception e) when (e is PdfException or IOException)
            {
                unreadable++;
                if (verbose) Console.WriteLine($"UNLESBAR {name}: {e.Message}");
                continue;
            }
            var rule = RuleOf(name);
            if (expectPass)
            {
                pass++;
                if (found.Count == 0) continue;
                passRejected++;
                foreach (var v in found) rejectedByRule[v.Rule] = rejectedByRule.GetValueOrDefault(v.Rule) + 1;
                if (verbose) Console.WriteLine($"ABGELEHNT {name}: {string.Join(" | ", found.Select(v => v.Rule + " " + v.Text))}");
            }
            else
            {
                fail++;
                if (found.Count > 0) failFound++;
                if (rule is not null && found.Any(v => rule.StartsWith(v.Rule, StringComparison.Ordinal) || v.Rule.StartsWith(rule, StringComparison.Ordinal))) failRight++;
                else if (rule is not null) missedByRule[rule] = missedByRule.GetValueOrDefault(rule) + 1;
                if (verbose && found.Count == 0) Console.WriteLine($"VERPASST {name}");
            }
        }
        Console.WriteLine($"gültige Dateien: {pass}, davon abgelehnt: {passRejected}");
        foreach (var (rule, count) in rejectedByRule) Console.WriteLine($"   abgelehnt nach {rule}: {count}");
        Console.WriteLine($"ungültige Dateien: {fail}, davon erkannt: {failFound}, mit der richtigen Regel: {failRight}");
        foreach (var (rule, count) in missedByRule) Console.WriteLine($"   nicht erkannt {rule}: {count}");
        if (unreadable > 0) Console.WriteLine($"unlesbar: {unreadable}");
        return passRejected == 0 ? 0 : 1;
    }

    /// <summary>
    /// The clause in a name of a test file: „veraPDF test suite 6-2-11-4-1-t01-fail-a.pdf“ → 6.2.11.4.1, „pdfa2-6-1-13-abc-t01-fail.pdf“ → 6.1.13
    /// (the part of the standard and a maker's mark before the number of the test are no part of the clause).
    /// </summary>
    private static string? RuleOf(string name) => RuleName().Match(name) is { Success: true } m ? m.Groups[1].Value.Replace('-', '.') : null;

    [GeneratedRegex(@"(?:pdfa\d-)?(\d+(?:-\d+)+)(?:-[a-z]+)?-t\d+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RuleName();

    /// <summary>pdf-knead pdfa-bilder &lt;file&gt;: the proposed language and the figures level a asks about, with their keys for --alt.</summary>
    public static int Figures(string file)
    {
        using var doc = PdfDocument.Open(file);
        var pages = PageItem.From(doc).Select(p => (p.Source, p.Page)).ToList();
        Console.WriteLine(Tr.T("Sprache: {0}", PdfATagging.ProposeLanguage(pages) is { Length: > 0 } language ? language : "–"));
        foreach (var figure in PdfATagging.Figures(pages))
            Console.WriteLine($"{figure.Key}\t{Tr.T("Seite {0}", figure.Page + 1)}\t" +
                              (figure.Bounds is { } b ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Min(b.Left, b.Right):0} {Math.Min(b.Top, b.Bottom):0} {Math.Abs(b.Width):0}×{Math.Abs(b.Height):0}") : "–"));
        return 0;
    }

    /// <summary>pdf-knead pdfa-struktur &lt;file&gt;: the structure tree (type, page, marked contents, alternative text), depth first.</summary>
    public static int Structure(string file)
    {
        if (Directory.Exists(file)) return StructureAll(file);
        using var doc = PdfDocument.Open(file);
        var catalog = doc.Catalog;
        Console.WriteLine($"Marked: {catalog?.GetDictionary(PdfName.Get("MarkInfo"))?.GetBoolean(PdfName.Get("Marked"))}  Lang: {catalog?.GetString(PdfName.Get("Lang"))?.ToText()}");
        if (catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is not { } root)
        {
            Console.WriteLine(Tr.T("Kein Strukturbaum."));
            return 1;
        }
        var pages = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < doc.PageCount; i++) pages.TryAdd(doc.Pages[i].Dictionary, i + 1);
        var budget = 20_000;
        void Walk(PdfObject? k, int depth)
        {
            if (k is null || depth > 64 || --budget < 0) return;
            switch (k)
            {
                case PdfArray array:
                    for (var i = 0; i < array.Count; i++) Walk(array.Get(i), depth);
                    break;
                case PdfInteger mcid:
                    Console.WriteLine($"{new string(' ', depth * 2)}MCID {mcid.Value}");
                    break;
                case PdfDictionary element when element.GetName(PdfName.Get("S")) is { } type:
                    var page = element.Get(PdfName.Get("Pg")) is PdfDictionary pg && pages.TryGetValue(pg, out var n) ? $" S{n}" : "";
                    var alt = element.GetString(PdfName.Get("Alt"))?.ToText() is { } a ? $" Alt „{a}“" : "";
                    Console.WriteLine($"{new string(' ', depth * 2)}{type.Value}{page}{alt}");
                    Walk(element.Get(PdfName.Get("K")), depth + 1);
                    break;
            }
        }
        Walk(root.Get(PdfName.Get("K")), 0);
        // the containment rules of the standard types (ISO/TS 32005, Table 5): what the tree breaks, counted by kind
        var broken = PdfKnead.Core.Pdf.Accessibility.Containment.Violations(root);
        Console.WriteLine($"Einschlussregeln: {broken.Count} Verstöße");
        foreach (var group in broken.GroupBy(b => b).OrderByDescending(g => g.Count()).Take(20)) Console.WriteLine($"  {group.Count()} × {group.Key}");
        return 0;
    }

    /// <summary>
    /// A folder: the breaches of the containment rules in each tagged file, and over all files by kind — to compare the role repair of two
    /// states over a collection (docs/FEHLERGEDAECHTNIS.md, No. 507).
    /// </summary>
    private static int StructureAll(string folder)
    {
        int files = 0, tagged = 0, clean = 0, total = 0;
        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*.pdf").Order(StringComparer.Ordinal))
        {
            files++;
            try
            {
                using var doc = PdfDocument.Open(file);
                if (doc.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is not { } root) continue;
                tagged++;
                var broken = PdfKnead.Core.Pdf.Accessibility.Containment.Violations(root);
                if (broken.Count == 0) clean++;
                total += broken.Count;
                foreach (var b in broken) byKind[b] = byKind.GetValueOrDefault(b) + 1;
                if (broken.Count > 0) Console.WriteLine($"{broken.Count,6}  {Path.GetFileName(file)}");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.WriteLine($"FEHLER {Path.GetFileName(file)}: {e.GetType().Name}");
            }
        }
        foreach (var (kind, count) in byKind.OrderByDescending(k => k.Value).Take(30)) Console.WriteLine($"  {count,6} × {kind}");
        Console.WriteLine($"Dateien: {files}, mit Strukturbaum: {tagged}, ohne Verstoß: {clean}, Verstöße: {total}");
        return 0;
    }
}
