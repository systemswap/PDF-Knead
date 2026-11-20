using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Cli;

/// <summary>
/// `barrierefrei-vergleich &lt;quelle&gt; &lt;kopie&gt; --profil ua1|ua2` (docs/ABLAUFPLAN.md, 6.8 step 4): the round trip measured with the own
/// check instead of an approximation. For every PDF in the copy folder the one of the same name in the source folder; both checked in the
/// profile; per file what the copy breaks that the source did not (new rule numbers), and what became of the PDF/UA identification — kept
/// although something new broke, removed, or kept with nothing broken. Summed up per rule. Exit code 0 when no copy has a new finding
/// (hints of „Bitte prüfen“ not counted) and no identification stayed over a new break. With --einzeln, besides each copy with a new
/// finding, the identification of every copy („KENNUNG name: ua1|ua2|keine“).
/// </summary>
internal static class AccessibilityRoundTrip
{
    private sealed record Checked(HashSet<string> Errors, int? Claim);

    public static int Run(string source, string copies, AccessibilityProfile profile, bool verbose)
    {
        if (!Directory.Exists(source) || !Directory.Exists(copies))
        {
            Console.Error.WriteLine("Aufruf: pdf-knead barrierefrei-vergleich <quelle> <kopie> --profil ua1|ua2 [--einzeln]");
            return 2;
        }
        int files = 0, missing = 0, unreadable = 0, clean = 0, claimKeptOverBreak = 0, claimRemoved = 0, claimKept = 0;
        var newByRule = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var copy in Directory.EnumerateFiles(copies, "*.pdf").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(copy);
            var original = Path.Combine(source, name);
            // copies named after their source with a mark of the step („…-neu.pdf“, „…-in_A.pdf“, „…-runde1.pdf“)
            for (var stem = Path.GetFileNameWithoutExtension(name); !File.Exists(original) && stem.LastIndexOf('-') is var dash and > 0;)
            {
                stem = stem[..dash];
                original = Path.Combine(source, stem + ".pdf");
            }
            if (!File.Exists(original))
            {
                missing++;
                continue;
            }
            files++;
            Checked? before = Check(original, profile), after = Check(copy, profile);
            if (before is null || after is null)
            {
                unreadable++;
                if (verbose) Console.WriteLine($"UNLESBAR {name}");
                continue;
            }
            var added = after.Errors.Where(id => !before.Errors.Contains(id)).Order(StringComparer.Ordinal).ToList();
            foreach (var id in added)
            {
                if (!newByRule.TryGetValue(id, out var list)) newByRule[id] = list = [];
                list.Add(name);
            }
            if (added.Count == 0) clean++;
            if (before.Claim is not null)
            {
                if (after.Claim is null) claimRemoved++;
                else if (added.Count > 0) claimKeptOverBreak++;
                else claimKept++;
            }
            if (verbose && (added.Count > 0 || before.Claim is not null && after.Claim is null))
                Console.WriteLine($"{name}: neu {string.Join(" ", added)}{(before.Claim is not null ? after.Claim is null ? " | Kennung entfernt" : " | Kennung geblieben" : "")}");
            // the identification of every copy (tools/erzeugungswege/auswerten.py checks a copy against the PDF/UA profile it names)
            if (verbose) Console.WriteLine($"KENNUNG {name}: {(after.Claim is { } part ? "ua" + part : "keine")}");
        }
        Console.WriteLine($"Dateien: {files}, ohne neuen Fund: {clean}, unlesbar: {unreadable}{(missing > 0 ? $", ohne Quelle: {missing}" : "")}");
        Console.WriteLine($"Kennung: geblieben ohne neuen Fund {claimKept}, entfernt {claimRemoved}, geblieben trotz neuem Fund {claimKeptOverBreak}");
        foreach (var (id, names) in newByRule)
            Console.WriteLine($"   neu {id}: {names.Count}  z. B. {string.Join(", ", names.Take(3))}");
        return newByRule.Count == 0 && claimKeptOverBreak == 0 ? 0 : 1;
    }

    /// <summary>The errors of the check (no hints) and the part the identification names; null if the file cannot be read.</summary>
    private static Checked? Check(string path, AccessibilityProfile profile)
    {
        try
        {
            using var doc = PdfDocument.Open(path);
            var report = AccessibilityCheck.Check(doc, profile);
            int? claim = null;
            if (doc.Catalog?.Get(PdfName.Metadata) is PdfStream { DataLength: <= 16 << 20 } xmp)
                claim = AccessibilityClaims.Read(doc.GetStreamData(xmp))?.PdfUaPart;
            return new Checked([.. report.Findings.Where(f => f.Severity == AccessibilitySeverity.Error).Select(f => f.Id)], claim);
        }
        catch (Exception e) when (e is PdfException or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
