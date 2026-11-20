using System.Globalization;
using System.Net;
using System.Text;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>
/// The result of the check „barrierefrei?“ as a report for the record (docs/ABLAUFPLAN.md, 6.7 step 6; docs/FEHLERGEDAECHTNIS.md, No. 668):
/// plain text or a self-contained HTML page (no scripts, no outside resources), in the language of the user interface. It says what was
/// checked (profile, part, stages) and that a person still has to confirm what no machine can judge; it names the file, not its content.
/// </summary>
public static class AccessibilityReportWriter
{
    /// <summary>The groups in their order, with their headings.</summary>
    private static IEnumerable<(string Title, List<AccessibilityFinding> Findings)> Groups(AccessibilityReport report) =>
        new[] { (AccessibilitySeverity.Error, Tr.T("Muss behoben werden")), (AccessibilitySeverity.Check, Tr.T("Bitte prüfen")), (AccessibilitySeverity.Hint, Tr.T("Hinweise")) }
            .Select(g => (g.Item2, report.Findings.Where(f => f.Severity == g.Item1).OrderBy(f => f.Page ?? 0).ToList()))
            .Where(g => g.Item2.Count > 0);

    /// <summary>The rule of a finding as the area shows it: part, clause and test, Matterhorn condition.</summary>
    public static string Rule(AccessibilityFinding finding, int part)
    {
        var number = finding.Id.StartsWith('H') ? Tr.T("Hinweis {0}", finding.Id)
            : finding.Id.StartsWith("32005", StringComparison.Ordinal) ? "ISO/TS 32005, " + Tr.T("Tabelle 5")
            : $"ISO 14289-{part}, {finding.Id}";
        return finding.Matterhorn is { } m ? $"{number} · Matterhorn {m}" : number;
    }

    private static string Profile(AccessibilityReport report) => report.Profile switch
    {
        AccessibilityProfile.HintsOnly => Tr.T("Nur Hinweise, PDF/UA-{0}", report.Part),
        AccessibilityProfile.Authority => Tr.T("Behörde, PDF/UA-{0}", report.Part),
        _ => $"PDF/UA-{report.Part} (ISO 14289-{report.Part})",
    };

    private static string Checked(DateTimeOffset when) => when.ToString("g", CultureInfo.CurrentCulture);

    private static string Headline(AccessibilityReport report, IReadOnlyList<(AccessibilityQuestion Question, AccessibilityAnswer Answer)>? questions) =>
        questions is { Count: > 0 } ? AccessibilityQuestions.Headline(report, [.. questions.Select(q => q.Answer)]) : report.Headline;

    /// <summary>The report as plain text (UTF-8, line breaks of the system).</summary>
    /// <param name="report">The result.</param>
    /// <param name="fileName">The name of the file checked (without its folder).</param>
    /// <param name="when">When it was checked.</param>
    /// <param name="questions">The questions of the human check with their answers; null to leave them out.</param>
    public static string Text(AccessibilityReport report, string fileName, DateTimeOffset when,
        IReadOnlyList<(AccessibilityQuestion Question, AccessibilityAnswer Answer)>? questions = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Tr.T("Prüfbericht Barrierefreiheit")).AppendLine();
        sb.AppendLine(Tr.T("Datei: {0}", fileName));
        sb.AppendLine(Tr.T("Geprüft am: {0}", Checked(when)));
        sb.AppendLine(Tr.T("Geprüft nach: {0}", Profile(report)));
        sb.AppendLine(Tr.T("Maschinell geprüfte Stufen: {0}", report.Stages)).AppendLine();
        sb.AppendLine(Headline(report, questions)).AppendLine();
        foreach (var (title, findings) in Groups(report))
        {
            sb.AppendLine($"{title} ({findings.Count})");
            foreach (var f in findings)
            {
                sb.Append("- ");
                if (f.Page is { } page) sb.Append(Tr.T("Seite {0}", page)).Append(": ");
                sb.AppendLine(f.Text);
                sb.Append("  ").AppendLine(Rule(f, report.Part));
            }
            sb.AppendLine();
        }
        if (questions is { Count: > 0 })
        {
            sb.AppendLine(Tr.T("Von Ihnen zu bestätigen"));
            foreach (var (question, answer) in questions)
                sb.Append("- ").Append(question.Id).Append(' ').Append(question.Text).Append(' ').AppendLine(AccessibilityQuestions.Label(answer));
            sb.AppendLine();
        }
        sb.AppendLine(Tr.T("Ob Ersatztexte stimmen, die Lesereihenfolge passt und Überschriften Sinn ergeben, prüft keine Maschine — das muss ein Mensch bestätigen."));
        return sb.ToString();
    }

    /// <summary>The report as one HTML page (styles inline, no scripts, no outside resources), readable with a screen reader.</summary>
    /// <param name="report">The result.</param>
    /// <param name="fileName">The name of the file checked (without its folder).</param>
    /// <param name="when">When it was checked.</param>
    /// <param name="language">The language of the page (a tag such as „de“).</param>
    /// <param name="questions">The questions of the human check with their answers; null to leave them out.</param>
    public static string Html(AccessibilityReport report, string fileName, DateTimeOffset when, string language,
        IReadOnlyList<(AccessibilityQuestion Question, AccessibilityAnswer Answer)>? questions = null)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(E(language)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(E(Tr.T("Prüfbericht Barrierefreiheit"))).Append(" – ").Append(E(fileName)).Append("</title>\n");
        sb.Append("<style>body{font-family:system-ui,sans-serif;max-width:48rem;margin:2rem auto;padding:0 1rem;line-height:1.5;color:#1b1b1f;background:#fff}")
          .Append("h1{font-size:1.5rem}h2{font-size:1.15rem;margin-top:2rem}dl{display:grid;grid-template-columns:max-content 1fr;gap:.25rem 1rem}")
          .Append("dt{font-weight:600}li{margin:.5rem 0}.rule{color:#4a4a55;font-size:.9rem}.headline{font-weight:600;font-size:1.1rem}</style>\n");
        sb.Append("</head>\n<body>\n<main>\n<h1>").Append(E(Tr.T("Prüfbericht Barrierefreiheit"))).Append("</h1>\n<dl>\n");
        void Row(string term, string value) => sb.Append("<dt>").Append(E(term)).Append("</dt><dd>").Append(E(value)).Append("</dd>\n");
        Row(Tr.T("Datei"), fileName);
        Row(Tr.T("Geprüft am"), Checked(when));
        Row(Tr.T("Geprüft nach"), Profile(report));
        Row(Tr.T("Maschinell geprüfte Stufen"), report.Stages);
        sb.Append("</dl>\n<p class=\"headline\">").Append(E(Headline(report, questions))).Append("</p>\n");
        foreach (var (title, findings) in Groups(report))
        {
            sb.Append("<h2>").Append(E($"{title} ({findings.Count})")).Append("</h2>\n<ul>\n");
            foreach (var f in findings)
            {
                sb.Append("<li>");
                if (f.Page is { } page) sb.Append(E(Tr.T("Seite {0}", page))).Append(": ");
                sb.Append(E(f.Text)).Append("<br><span class=\"rule\">").Append(E(Rule(f, report.Part))).Append("</span></li>\n");
            }
            sb.Append("</ul>\n");
        }
        if (questions is { Count: > 0 })
        {
            sb.Append("<h2>").Append(E(Tr.T("Von Ihnen zu bestätigen"))).Append("</h2>\n<ul>\n");
            foreach (var (question, answer) in questions)
                sb.Append("<li>").Append(E(question.Id + " " + question.Text)).Append(" <strong>").Append(E(AccessibilityQuestions.Label(answer))).Append("</strong></li>\n");
            sb.Append("</ul>\n");
        }
        sb.Append("<p>").Append(E(Tr.T("Ob Ersatztexte stimmen, die Lesereihenfolge passt und Überschriften Sinn ergeben, prüft keine Maschine — das muss ein Mensch bestätigen."))).Append("</p>\n");
        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }
}
