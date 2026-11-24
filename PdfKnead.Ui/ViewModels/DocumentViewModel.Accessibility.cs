using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>A profile as the area „Barrierefreiheit“ offers it.</summary>
/// <param name="Profile">The profile; null: by the version of the file (PDF/UA-1 up to 1.7, PDF/UA-2 for 2.0).</param>
/// <param name="Label">Its name.</param>
public sealed record AccessibilityProfileChoice(AccessibilityProfile? Profile, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One finding of the check as a row of the area: what and where, the rule, a repair if PDF-Knead can do it.</summary>
public sealed class AccessibilityRow
{
    /// <summary>Creates the row.</summary>
    /// <param name="finding">The finding.</param>
    /// <param name="part">The part of ISO 14289 the number belongs to.</param>
    public AccessibilityRow(AccessibilityFinding finding, int part)
    {
        Finding = finding;
        Rule = AccessibilityReportWriter.Rule(finding, part);
        PageLabel = finding.Page is { } page ? Tr.T("Seite {0}", page) : "";
        RepairLabel = finding.Repair switch
        {
            AccessibilityRepair.Title => Tr.T("Titel eintragen"),
            AccessibilityRepair.Language => Tr.T("Sprache eintragen"),
            AccessibilityRepair.DisplayDocTitle => Tr.T("Titel statt Dateiname anzeigen"),
            AccessibilityRepair.RemoveXfa => Tr.T("XFA-Formular entfernen"),
            AccessibilityRepair.RemoveClaim => Tr.T("Kennung entfernen"),
            AccessibilityRepair.LayerNames => Tr.T("Ebenen-Ansichten benennen"),
            _ => "",
        };
        Guidance = finding.Repair switch
        {
            AccessibilityRepair.Tabs => Tr.T("Wird beim Speichern gesetzt, sobald die Seiten getaggt sind."),
            AccessibilityRepair.Permissions => Tr.T("Der Schutz der Datei verbietet Hilfsmitteln das Lesen — unter „Sicher“ neu schützen und das Kopieren für Bildschirmleser erlauben."),
            AccessibilityRepair.RemoveClaim => Tr.T("Die PDF/UA-Kennung ist fehlerhaft — „Kennung entfernen“ nimmt sie beim Speichern heraus."),
            // the missing identification: PDF-Knead writes it only when everything is met and confirmed (docs/ABLAUFPLAN.md, 6.7 step 8)
            _ when finding.Id == "5-1" => Tr.T("PDF-Knead schreibt die PDF/UA-Kennung nicht von sich aus: sie gehört erst in die Datei, wenn alle Prüfungen bestanden und die Fragen eines Menschen beantwortet sind."),
            _ => "",
        };
        Name = PageLabel.Length > 0 ? $"{PageLabel}: {finding.Text} ({Rule})" : $"{finding.Text} ({Rule})";
    }

    /// <summary>The finding.</summary>
    public AccessibilityFinding Finding { get; }

    /// <summary>What is wrong.</summary>
    public string Text => Finding.Text;

    /// <summary>The rule: part, clause and test, and the Matterhorn condition (shown folded).</summary>
    public string Rule { get; }

    /// <summary>„Seite 3“; empty for the document as a whole.</summary>
    public string PageLabel { get; }

    /// <summary>Whether there is a page to show.</summary>
    public bool HasPage => Finding.Page is not null;

    /// <summary>The label of the repair button; empty if PDF-Knead cannot repair it from here.</summary>
    public string RepairLabel { get; }

    /// <summary>Whether there is a repair button.</summary>
    public bool HasRepair => RepairLabel.Length > 0;

    /// <summary>What to do where PDF-Knead has no button; may be empty.</summary>
    public string Guidance { get; }

    /// <summary>Whether there is guidance.</summary>
    public bool HasGuidance => Guidance.Length > 0;

    /// <summary>The full name for screen readers.</summary>
    public string Name { get; }
}

/// <summary>A group of findings: „Muss behoben werden“, „Bitte prüfen“, „Hinweise“.</summary>
/// <param name="Title">The heading with the number of rows.</param>
/// <param name="Rows">The rows.</param>
public sealed record AccessibilityGroup(string Title, IReadOnlyList<AccessibilityRow> Rows);

/// <summary>
/// „Barrierefreiheit“ as an area on the right (docs/ABLAUFPLAN.md, 6.7 steps 6, 7 and 10; docs/FEHLERGEDAECHTNIS.md, No. 667): checks the file as
/// the next save would write it (<see cref="AccessibilityCheck"/>), shows the headline in words, the findings in groups with page, rule and a
/// repair where PDF-Knead can make one — title, language and „Titel statt Dateiname“ as the description written when saving, a faulty
/// PDF/UA identification removed when saving, the configurations of the layers named when saving, the XFA form with the scripts. Each repair checks again.
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>The profiles offered; „Nur Hinweise“ first, the recommended default (docs/ABLAUFPLAN.md, 6.7, decisions).</summary>
    public static IReadOnlyList<AccessibilityProfileChoice> AccessibilityProfiles { get; } =
    [
        new(AccessibilityProfile.HintsOnly, Tr.T("Nur Hinweise (PDF/UA nach Version)")),
        new(null, Tr.T("PDF/UA nach Version der Datei")),
        new(AccessibilityProfile.PdfUa1, "PDF/UA-1 (ISO 14289-1)"),
        new(AccessibilityProfile.PdfUa2, "PDF/UA-2 (ISO 14289-2)"),
        new(AccessibilityProfile.Authority, Tr.T("Behörde (BITV 2.0, EN 301 549)")),
    ];

    private CancellationTokenSource? _accessibilityWork;

    /// <summary>Whether the area „Barrierefreiheit“ is open.</summary>
    [ObservableProperty]
    public partial bool IsAccessibilityOpen { get; private set; }

    /// <summary>Whether the check runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibilityIdle))]
    public partial bool AccessibilityChecking { get; private set; }

    /// <summary>Whether no check runs (the buttons are enabled).</summary>
    public bool AccessibilityIdle => !AccessibilityChecking;

    /// <summary>The profile chosen; checks again when changed while the area is open.</summary>
    [ObservableProperty]
    public partial AccessibilityProfileChoice ChosenAccessibilityProfile { get; set; } = AccessibilityProfiles[0];

    partial void OnChosenAccessibilityProfileChanged(AccessibilityProfileChoice value)
    {
        if (IsAccessibilityOpen) _ = CheckAccessibilityAsync();
    }

    /// <summary>The headline in words (also announced): „Nicht barrierefrei nach PDF/UA-1: 3 Fehler“.</summary>
    [ObservableProperty]
    public partial string AccessibilityHeadline { get; private set; } = "";

    /// <summary>What the check could not do (a redaction not applied yet, a file that could not be written); empty if nothing.</summary>
    [ObservableProperty]
    public partial string AccessibilityProblem { get; private set; } = "";

    /// <summary>The findings in their groups.</summary>
    public ObservableCollection<AccessibilityGroup> AccessibilityGroups { get; } = [];

    /// <summary>The title typed for the repair „Titel eintragen“.</summary>
    [ObservableProperty]
    public partial string AccessibilityTitle { get; set; } = "";

    /// <summary>The language typed for the repair „Sprache eintragen“ (a tag such as „de-DE“), proposed from the text.</summary>
    [ObservableProperty]
    public partial string AccessibilityLanguage { get; set; } = "";

    /// <summary>The PDF/UA identification goes when saving (repair „Kennung entfernen“). Counts as a modification.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified), nameof(Title))]
    public partial bool RemoveClaimOnSave { get; set; }

    /// <summary>The configurations of the layers get names when saving (repair „Ebenen-Ansichten benennen“). Counts as a modification.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified), nameof(Title))]
    public partial bool NameLayersOnSave { get; set; }

    /// <summary>The result of the last check, for the report; null before the first or after a failed one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccessibilityReport))]
    public partial AccessibilityReport? LastAccessibilityReport { get; private set; }

    /// <summary>When the last check ran.</summary>
    public DateTimeOffset AccessibilityCheckedAt { get; private set; }

    /// <summary>Whether there is a result to save as a report.</summary>
    public bool HasAccessibilityReport => LastAccessibilityReport is not null;

    /// <summary>The headline changed (the window announces it as a status message).</summary>
    public event Action<string>? AccessibilityAnnounced;

    /// <summary>Opens the area and checks.</summary>
    public async Task OpenAccessibilityAsync()
    {
        IsAccessibilityOpen = true;
        var shown = DescriptionOnSave ?? FileDescription();
        AccessibilityTitle = shown.Title;
        AccessibilityLanguage = shown.Language;
        await CheckAccessibilityAsync();
    }

    /// <summary>Closes the area.</summary>
    public void CloseAccessibility()
    {
        _accessibilityWork?.Cancel();
        EndAccessibilityHelp();
        IsAccessibilityOpen = false;
        AccessibilityGroups.Clear();
        LastAccessibilityReport = null;
        AccessibilityHeadline = "";
        AccessibilityProblem = "";
    }

    /// <summary>
    /// Writes the document as the next save would (in memory, with the pending description, cleanup and protection), opens the copy and checks
    /// it; the result replaces the rows.
    /// </summary>
    [RelayCommand]
    public async Task CheckAccessibilityAsync()
    {
        _accessibilityWork?.Cancel();
        var cts = _accessibilityWork = new CancellationTokenSource();
        AccessibilityChecking = true;
        AccessibilityProblem = "";
        AccessibilityHeadline = Tr.T("Prüfe …");
        AccessibilityGroups.Clear();
        LastAccessibilityReport = null;
        try
        {
            var pages = History.Pages.ToArray();
            InDocumentRedaction.EnsureApplied(pages);
            var main = Document;
            var options = SaveOptions(SaveVersion);
            var password = PasswordAfterSaving(options);
            var profile = ChosenAccessibilityProfile.Profile;
            var cancel = cts.Token;
            var report = await WorkAsync(() => Task.Run(() =>
            {
                using var output = new MemoryStream();
                var saved = PdfWriter.Write(main, pages, output, options, cancel);
                using var copy = saved.Security is { IsPublicKey: true } ? PdfDocument.OpenSaved(output.ToArray(), saved) : PdfDocument.Open(output.ToArray(), password);
                return AccessibilityCheck.Check(copy, profile, cancel);
            }, cancel));
            if (cancel.IsCancellationRequested) return;
            Show(report);
        }
        catch (OperationCanceledException)
        {
            // checked again or closed in the meantime
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            if (cts.IsCancellationRequested) return;
            AccessibilityHeadline = Tr.T("Nicht geprüft");
            AccessibilityProblem = Failure.Text(ex);
            AccessibilityAnnounced?.Invoke(AccessibilityHeadline);
        }
        finally
        {
            if (ReferenceEquals(_accessibilityWork, cts)) AccessibilityChecking = false;
        }
    }

    private void Show(AccessibilityReport report)
    {
        AccessibilityGroups.Clear();
        LastAccessibilityReport = report;
        AccessibilityCheckedAt = DateTimeOffset.Now;
        var rows = report.Findings.Select(f => new AccessibilityRow(f, report.Part)).ToList();
        void Group(AccessibilitySeverity severity, string title)
        {
            var list = rows.Where(r => r.Finding.Severity == severity).OrderBy(r => r.Finding.Page ?? 0).ToList();
            if (list.Count > 0) AccessibilityGroups.Add(new AccessibilityGroup($"{title} ({list.Count})", list));
        }
        Group(AccessibilitySeverity.Error, Tr.T("Muss behoben werden"));
        Group(AccessibilitySeverity.Check, Tr.T("Bitte prüfen"));
        Group(AccessibilitySeverity.Hint, Tr.T("Hinweise"));
        AccessibilityHeadline = AccessibilityQuestions.Headline(report, [.. AccessibilityQuestionRows.Select(r => r.Answer)]);
        AccessibilityAnnounced?.Invoke(AccessibilityHeadline);
        if (rows.FirstOrDefault(r => r.Finding.Repair == AccessibilityRepair.Language)?.Finding.Suggestion is { Length: > 0 } suggestion &&
            AccessibilityLanguage.Length == 0)
            AccessibilityLanguage = suggestion;
    }

    /// <summary>A page button: brings the page into view.</summary>
    [RelayCommand]
    private void ShowAccessibilityPage(AccessibilityRow row)
    {
        if (row.Finding.Page is { } page && page >= 1 && page <= Pages.Count) ScrollToPage(page - 1);
    }

    /// <summary>
    /// A repair button: title, language and „Titel statt Dateiname“ become the description written when saving (as in „Eigenschaften“), the XFA
    /// form goes with the scripts (as in „Dokument bereinigen“); then the check runs again.
    /// </summary>
    [RelayCommand]
    private async Task RepairAccessibility(AccessibilityRow row)
    {
        var shown = DescriptionOnSave ?? FileDescription();
        switch (row.Finding.Repair)
        {
            case AccessibilityRepair.Title:
                if (AccessibilityTitle.Trim().Length == 0)
                {
                    AccessibilityProblem = Tr.T("Bitte zuerst oben einen Titel eintragen.");
                    return;
                }
                SetDescription(shown with { Title = AccessibilityTitle.Trim() });
                break;
            case AccessibilityRepair.Language:
                var typed = AccessibilityLanguage.Trim();
                var language = typed.Length > 0 && LanguageTags.Valid(typed) ? typed : LanguageTags.Proposal(typed);
                if (language is not { Length: > 0 })
                {
                    AccessibilityProblem = Tr.T("Bitte zuerst oben eine Sprache eintragen, etwa „de-DE“.");
                    return;
                }
                AccessibilityLanguage = language;   // the field shows the tag written (a name typed becomes it)
                SetDescription(shown with { Language = language });
                break;
            case AccessibilityRepair.DisplayDocTitle:
                SetDescription(shown with { DisplayTitle = true });
                break;
            case AccessibilityRepair.RemoveXfa:
                if (!CanModifyContent)   // removing parts of the document like „Dokument bereinigen“: bit 4 (Table 22; No. 1111)
                {
                    Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
                    break;
                }
                await CleanAsync(CleanupItems.Scripts);
                break;
            case AccessibilityRepair.RemoveClaim:
                RemoveClaimOnSave = true;
                break;
            case AccessibilityRepair.LayerNames:
                NameLayersOnSave = true;
                break;
            default:
                return;
        }
        await CheckAccessibilityAsync();
    }

    /// <summary>Proposes the language the text of the pages appears to be in.</summary>
    [RelayCommand]
    private async Task ProposeAccessibilityLanguage()
    {
        var list = History.Pages.Select(p => (p.Source, p.Page)).ToList();
        var proposed = await Task.Run(() => PdfATagging.ProposeLanguage(list));
        if (proposed.Length > 0) AccessibilityLanguage = proposed;
    }

    /// <summary>Writes the last result as a report: HTML for a name ending in .htm or .html, plain text otherwise (UTF-8).</summary>
    /// <param name="path">The target file.</param>
    public async Task SaveAccessibilityReportAsync(string path)
    {
        if (LastAccessibilityReport is not { } report) return;
        var html = System.IO.Path.GetExtension(path).ToLowerInvariant() is ".htm" or ".html";
        var name = FileName;
        var when = AccessibilityCheckedAt;
        var language = Tr.Culture.TwoLetterISOLanguageName;
        var answers = AccessibilityAnswers;
        var text = html ? AccessibilityReportWriter.Html(report, name, when, language, answers) : AccessibilityReportWriter.Text(report, name, when, answers);
        var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
        await Task.Run(() => Core.Pdf.Writing.PdfFileSaver.SaveFile(path, output => output.Write(bytes)));
    }

    private void SetDescription(DescriptionChange change)
    {
        DescriptionOnSave = change == FileDescription() ? null : change;
        ResetDescription();
        OnPropertyChanged(nameof(Properties));
    }
}
