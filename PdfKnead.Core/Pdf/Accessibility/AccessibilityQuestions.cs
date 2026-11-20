namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>An answer to a question of the human check.</summary>
public enum AccessibilityAnswer
{
    /// <summary>Not answered yet.</summary>
    Open,

    /// <summary>Confirmed.</summary>
    Yes,

    /// <summary>Not met: the file is not accessible in this respect.</summary>
    No,

    /// <summary>Does not apply (no tables, no forms, …).</summary>
    NotApplicable,
}

/// <summary>A question only a person can answer (docs/ABLAUFPLAN.md, 6.7 step 5).</summary>
/// <param name="Id">„M1“ to „M12“.</param>
/// <param name="Text">The question.</param>
/// <param name="Help">How to look, in one sentence.</param>
/// <param name="CanNotApply">Whether „trifft nicht zu“ is a sensible answer (a document without tables, forms, links …).</param>
public sealed record AccessibilityQuestion(string Id, string Text, string Help, bool CanNotApply);

/// <summary>
/// The human checks of „barrierefrei?“ (docs/ABLAUFPLAN.md, 6.7 step 5; docs/FEHLERGEDAECHTNIS.md, No. 669): what the standards ask that no
/// machine can judge — reading order, the sense of alternative texts, headings, table headers, lists, artifacts, language, colour, recognised
/// text, field labels, link purposes, the title. The texts in the language of the user interface (built when asked, after a switch too).
/// </summary>
public static class AccessibilityQuestions
{
    /// <summary>The twelve questions in their order.</summary>
    public static IReadOnlyList<AccessibilityQuestion> All =>
    [
        new("M1", Tr.T("Stimmt die Lesereihenfolge?"),
            Tr.T("Ein Bildschirmleser liest in der Reihenfolge der Struktur: Spalten, Kästen und Fußnoten müssen an der richtigen Stelle kommen."), false),
        new("M2", Tr.T("Beschreiben die Ersatztexte die Abbildungen treffend?"),
            Tr.T("Jedes Bild mit Inhalt braucht einen Text, der sagt, was es zeigt; reiner Schmuck ist ein Artefakt."), true),
        new("M3", Tr.T("Geben die Überschriften die Gliederung richtig wieder?"),
            Tr.T("Überschriften als solche getaggt, in der richtigen Stufe; nichts nur fett gesetzt."), true),
        new("M4", Tr.T("Sind die Kopfzellen der Tabellen richtig?"),
            Tr.T("Kopfzellen als TH, jeder Datenzelle die passende zugeordnet; keine Tabelle nur fürs Layout."), true),
        new("M5", Tr.T("Sind Listen als Listen getaggt?"),
            Tr.T("Aufzählungen als Liste mit Listenpunkten, nicht als einzelne Absätze."), true),
        new("M6", Tr.T("Sind Kopf- und Fußzeilen, Seitenzahlen und Schmuck Artefakte — und nichts anderes?"),
            Tr.T("Wiederkehrendes und Schmuck soll der Bildschirmleser überspringen; Inhalt darf nicht als Artefakt verschwinden."), false),
        new("M7", Tr.T("Stimmt die angegebene Sprache, auch bei Stellen in anderen Sprachen?"),
            Tr.T("Die Sprache bestimmt die Aussprache: ein englisches Zitat in einem deutschen Text braucht eine eigene Angabe."), false),
        new("M8", Tr.T("Kommt keine Bedeutung nur über Farbe, Form oder Lage?"),
            Tr.T("„Die rot markierten Felder“ oder „rechts oben“ hilft niemandem, der es nicht sieht."), false),
        new("M9", Tr.T("Ist erkannter Text (Texterkennung) weitgehend fehlerfrei?"),
            Tr.T("Eingescannte Seiten: Der unsichtbare Text ist das, was vorgelesen wird."), true),
        new("M10", Tr.T("Sind die Formularfelder verständlich beschriftet?"),
            Tr.T("Die Kurzinfo eines Felds sagt, was einzutragen ist — auch ohne das Blatt zu sehen."), true),
        new("M11", Tr.T("Ist der Zweck jedes Links erkennbar?"),
            Tr.T("„hier“ oder eine bloße Adresse sagt nicht, wohin es geht."), true),
        new("M12", Tr.T("Beschreibt der Titel das Dokument?"),
            Tr.T("Den Titel sagt der Bildschirmleser zuerst; ein Dateiname oder „Unbenannt“ hilft nicht."), false),
    ];

    /// <summary>An answer in words.</summary>
    public static string Label(AccessibilityAnswer answer) => answer switch
    {
        AccessibilityAnswer.Yes => Tr.T("Ja"),
        AccessibilityAnswer.No => Tr.T("Nein"),
        AccessibilityAnswer.NotApplicable => Tr.T("Trifft nicht zu"),
        _ => Tr.T("Offen"),
    };

    /// <summary>
    /// The headline with the questions (docs/ABLAUFPLAN.md, 6.7 step 6): errors of the check first; then questions answered „Nein“; then those
    /// still open; „barrierefrei“ only when the check found nothing at all and every question is confirmed or does not apply.
    /// </summary>
    /// <param name="report">The result of the machine check.</param>
    /// <param name="answers">The answers, one per question.</param>
    public static string Headline(AccessibilityReport report, IReadOnlyCollection<AccessibilityAnswer> answers)
    {
        if (report.Errors > 0) return report.Headline;
        var no = answers.Count(a => a == AccessibilityAnswer.No);
        if (no > 0) return no == 1 ? Tr.T("Nicht barrierefrei: 1 Frage mit „Nein“ beantwortet") : Tr.T("Nicht barrierefrei: {0} Fragen mit „Nein“ beantwortet", no);
        var open = answers.Count(a => a == AccessibilityAnswer.Open);
        if (open > 0) return Tr.T("{0} — {1} von {2} Fragen offen", report.Headline, open, answers.Count);
        return report.Findings.Count == 0
            ? Tr.T("Barrierefrei nach PDF/UA-{0}: alle Prüfungen bestanden, alle Fragen bestätigt", report.Part)
            : Tr.T("{0} — alle Fragen beantwortet", report.Headline);
    }
}
