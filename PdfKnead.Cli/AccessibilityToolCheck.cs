using System.Text.Json;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf;

namespace PdfKnead.Cli;

/// <summary>
/// The check „barrierefrei?“ on the command line (docs/ABLAUFPLAN.md, 6.7 step 9): the findings of one file, or the measurement over a folder of
/// the test files of the reference checker (names like „7.1-t08-fail-a.pdf“: clause, test number, pass or fail) — per rule built, which
/// failing file it misses and which passing file it reports wrongly.
/// </summary>
internal static partial class AccessibilityToolCheck
{
    /// <summary>Clause and test number of a test file name, such as „7.1-8“ for „7.1-t08-fail-a.pdf“.</summary>
    [GeneratedRegex(@"^(?<clause>\d+(?:\.\d+)*)-t(?<test>\d+)-(?<verdict>pass|fail)-", RegexOptions.IgnoreCase)]
    private static partial Regex TestName();

    public static int Run(string path, AccessibilityProfile? profile, bool json, bool verbose)
    {
        if (!Directory.Exists(path))
        {
            using var doc = PdfDocument.Open(path);
            var report = AccessibilityCheck.Check(doc, profile);
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(report.Findings.Select(f => new Dictionary<string, object?>
                {
                    ["id"] = f.Id, ["matterhorn"] = f.Matterhorn, ["severity"] = f.Severity.ToString(), ["page"] = f.Page, ["text"] = f.Text,
                    ["repair"] = f.Repair.ToString(), ["suggestion"] = f.Suggestion,
                })));
                return report.Errors == 0 ? 0 : 1;
            }
            foreach (var f in report.Findings)
                Console.WriteLine($"{f.Id,-12} {f.Matterhorn ?? "",-7} {(f.Page is { } p ? $"S{p} " : "")}{f.Text}{(f.Suggestion is { } s ? $" → {s}" : "")}");
            Console.WriteLine(report.Headline);
            return report.Errors == 0 ? 0 : 1;
        }
        if (!Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories).Any(f => TestName().IsMatch(Path.GetFileName(f))))
            return Survey(path, profile, verbose);
        var built = new HashSet<string>(StringComparer.Ordinal);
        int part = 0, pass = 0, passWrong = 0, fail = 0, failFound = 0, failNotBuilt = 0, unreadable = 0;
        var missed = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var wrong = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (TestName().Match(name) is not { Success: true } m) continue;
            var rule = $"{m.Groups["clause"].Value}-{int.Parse(m.Groups["test"].Value, System.Globalization.CultureInfo.InvariantCulture)}";
            var expectFail = m.Groups["verdict"].Value.Equals("fail", StringComparison.OrdinalIgnoreCase);
            AccessibilityReport report;
            try
            {
                using var doc = PdfDocument.Open(file);
                report = AccessibilityCheck.Check(doc, profile);
            }
            catch (Exception e) when (e is PdfException or IOException or InvalidOperationException)
            {
                unreadable++;
                if (verbose) Console.WriteLine($"UNLESBAR {name}: {e.Message}");
                continue;
            }
            if (part == 0)
            {
                part = report.Part;
                built.UnionWith(AccessibilityCheck.RuleIds(part));
            }
            var ids = report.Findings.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            if (!expectFail)
            {
                pass++;
                // a passing file must not be reported under any rule built (the reference files pass every rule, not only their own)
                var reported = ids.Where(built.Contains).ToList();
                if (reported.Count == 0) continue;
                passWrong++;
                foreach (var id in reported) (wrong.TryGetValue(id, out var l) ? l : wrong[id] = []).Add(name);
                if (verbose) Console.WriteLine($"FÄLSCHLICH {name}: {string.Join(" | ", report.Findings.Where(f => built.Contains(f.Id)).Select(f => f.Id + " " + f.Text))}");
                continue;
            }
            fail++;
            if (!built.Contains(rule))
            {
                failNotBuilt++;
                continue;
            }
            if (ids.Contains(rule)) failFound++;
            else
            {
                (missed.TryGetValue(rule, out var l) ? l : missed[rule] = []).Add(name);
                if (verbose) Console.WriteLine($"ÜBERSEHEN {name}: gefunden {string.Join(", ", ids)}");
            }
        }
        Console.WriteLine($"PDF/UA-{part}, Stufen {AccessibilityCheck.Stages}: {built.Count} Regeln gebaut");
        Console.WriteLine($"pass-Dateien: {pass}, davon fälschlich gemeldet: {passWrong}");
        foreach (var (id, names) in wrong) Console.WriteLine($"   fälschlich {id}: {names.Count}  z. B. {string.Join(", ", names.Take(3))}");
        Console.WriteLine($"fail-Dateien: {fail}, Regel gebaut: {fail - failNotBuilt}, davon erkannt: {failFound}");
        foreach (var (id, names) in missed) Console.WriteLine($"   übersehen {id}: {names.Count}  z. B. {string.Join(", ", names.Take(3))}");
        if (unreadable > 0) Console.WriteLine($"unlesbar: {unreadable}");
        return passWrong == 0 && missed.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// A folder of ordinary files: every file checked, the findings counted by rule, exceptions named, the time per page measured (docs/ABLAUFPLAN.md,
    /// 6.7, counter-checks: no crash, time per page below that of the PDF/A check).
    /// </summary>
    private static int Survey(string path, AccessibilityProfile? profile, bool verbose)
    {
        int files = 0, pages = 0, unreadable = 0, crashed = 0;
        var byId = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var checkTime = TimeSpan.Zero;
        foreach (var file in Directory.EnumerateFiles(path, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(file);
            }
            catch (Exception e) when (e is PdfException or IOException or InvalidOperationException)
            {
                unreadable++;
                continue;
            }
            using (doc)
            {
                files++;
                try
                {
                    var started = watch.Elapsed;
                    var report = AccessibilityCheck.Check(doc, profile);
                    checkTime += watch.Elapsed - started;
                    pages += doc.PageCount;
                    foreach (var f in report.Findings) byId[f.Id] = byId.GetValueOrDefault(f.Id) + 1;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    crashed++;
                    Console.WriteLine($"AUSNAHME {Path.GetFileName(file)}: {e.GetType().Name}: {e.Message}");
                }
            }
        }
        foreach (var (id, count) in byId) Console.WriteLine($"   {id,-14} {count,6}");
        Console.WriteLine($"Dateien: {files}, Seiten: {pages}, unlesbar: {unreadable}, Ausnahmen: {crashed}, Prüfzeit {checkTime.TotalSeconds:0.0} s " +
                          $"({(pages > 0 ? checkTime.TotalMilliseconds / pages : 0):0.0} ms je Seite)");
        return crashed == 0 ? 0 : 1;
    }
}
