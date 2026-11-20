using PdfKnead.Core.Pdf.Accessibility;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The human check of „barrierefrei?“ (docs/ABLAUFPLAN.md, 6.7 steps 5 and 6; docs/FEHLERGEDAECHTNIS.md, No. 669): twelve questions, the
/// headline with them — „barrierefrei“ only with nothing found and every question confirmed — and the report carrying the answers.
/// </summary>
public sealed class AccessibilityQuestionTests
{
    private static AccessibilityReport Report(params AccessibilityFinding[] findings) => new(AccessibilityProfile.PdfUa1, 1, findings, AccessibilityCheck.Stages);

    private static AccessibilityAnswer[] All(AccessibilityAnswer answer) => [.. Enumerable.Repeat(answer, AccessibilityQuestions.All.Count)];

    [Fact]
    public void Questions_TwelveDistinct_EachWithHelp()
    {
        var all = AccessibilityQuestions.All;
        Assert.Equal(12, all.Count);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => "M" + i), all.Select(q => q.Id));
        Assert.All(all, q => Assert.False(string.IsNullOrWhiteSpace(q.Help)));
    }

    [Fact]
    public void Headline_ErrorsFirst_ThenNo_ThenOpen_AccessibleOnlyWithNothingFoundAndAllConfirmed()
    {
        var error = new AccessibilityFinding("7.1-9", "06-003", AccessibilitySeverity.Error, "Kein Titel.");
        var hint = new AccessibilityFinding("H11", null, AccessibilitySeverity.Check, "Sprache prüfen.");
        Assert.Equal(Report(error).Headline, AccessibilityQuestions.Headline(Report(error), All(AccessibilityAnswer.Yes)));

        var answers = All(AccessibilityAnswer.Yes);
        answers[3] = AccessibilityAnswer.No;
        Assert.StartsWith("Nicht barrierefrei: 1 Frage", AccessibilityQuestions.Headline(Report(), answers));

        answers = All(AccessibilityAnswer.Yes);
        answers[0] = answers[5] = AccessibilityAnswer.Open;
        Assert.EndsWith("2 von 12 Fragen offen", AccessibilityQuestions.Headline(Report(), answers));

        answers = All(AccessibilityAnswer.Yes);
        answers[3] = AccessibilityAnswer.NotApplicable;
        Assert.Equal("Barrierefrei nach PDF/UA-1: alle Prüfungen bestanden, alle Fragen bestätigt", AccessibilityQuestions.Headline(Report(), answers));
        // a sign still standing („Bitte prüfen“): not called accessible
        Assert.DoesNotContain("Barrierefrei nach", AccessibilityQuestions.Headline(Report(hint), answers));
    }

    [Fact]
    public void Report_CarriesTheAnswers()
    {
        var questions = AccessibilityQuestions.All.Select((q, i) => (q, i == 0 ? AccessibilityAnswer.No : AccessibilityAnswer.Open)).ToList();
        var when = new DateTimeOffset(2026, 11, 9, 15, 0, 0, TimeSpan.FromHours(1));
        var text = AccessibilityReportWriter.Text(Report(), "Mustermappe.pdf", when, questions);
        Assert.Contains("Von Ihnen zu bestätigen", text);
        Assert.Contains("- M1 Stimmt die Lesereihenfolge? Nein", text);
        Assert.Contains("Nicht barrierefrei: 1 Frage", text);
        var html = AccessibilityReportWriter.Html(Report(), "Mustermappe.pdf", when, "de", questions);
        Assert.Contains("M12 Beschreibt der Titel das Dokument? <strong>Offen</strong>", html);
        // without answers no section
        Assert.DoesNotContain("Von Ihnen zu bestätigen", AccessibilityReportWriter.Text(Report(), "Mustermappe.pdf", when));
    }
}
