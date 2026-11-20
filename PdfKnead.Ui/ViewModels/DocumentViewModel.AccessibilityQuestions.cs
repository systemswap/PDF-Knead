using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Pdf.Accessibility;

namespace PdfKnead.ViewModels;

/// <summary>A question of the human check as a row of the area: the question, how to look, the answer as three choices.</summary>
public sealed partial class AccessibilityQuestionRow : ObservableObject
{
    private readonly Action _changed;

    /// <summary>Creates the row.</summary>
    /// <param name="question">The question.</param>
    /// <param name="changed">Called when the answer changes (the headline follows).</param>
    public AccessibilityQuestionRow(AccessibilityQuestion question, Action changed)
    {
        Question = question;
        _changed = changed;
    }

    /// <summary>The question.</summary>
    public AccessibilityQuestion Question { get; }

    /// <summary>„M1 Stimmt die Lesereihenfolge?“.</summary>
    public string Text => $"{Question.Id}  {Question.Text}";

    /// <summary>How to look.</summary>
    public string Help => Question.Help;

    /// <summary>Whether „Trifft nicht zu“ is offered.</summary>
    public bool CanNotApply => Question.CanNotApply;

    /// <summary>The help on the pages for this question (M1, M2, M6); None for the others.</summary>
    public AccessibilityHelp PageHelp => Question.Id switch
    {
        "M1" => AccessibilityHelp.ReadingOrder,
        "M2" => AccessibilityHelp.Figures,
        "M6" => AccessibilityHelp.Artifacts,
        _ => AccessibilityHelp.None,
    };

    /// <summary>Whether the question has a help on the pages.</summary>
    public bool HasHelp => PageHelp != AccessibilityHelp.None;

    /// <summary>Whether its help is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HelpLabel))]
    public partial bool HelpShown { get; set; }

    /// <summary>The label of the help button.</summary>
    public string HelpLabel => HelpShown ? Tr.T("Auf den Seiten ausblenden") : PageHelp switch
    {
        AccessibilityHelp.ReadingOrder => Tr.T("Reihenfolge auf den Seiten zeigen"),
        AccessibilityHelp.Figures => Tr.T("Abbildungen auf den Seiten zeigen"),
        _ => Tr.T("Artefakte auf den Seiten zeigen"),
    };

    /// <summary>The group of the three choices (one per question, so the choices of different questions do not take each other).</summary>
    public string Group => "accessibility-" + Question.Id;

    /// <summary>The answer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsYes), nameof(IsNo), nameof(IsNotApplicable))]
    public partial AccessibilityAnswer Answer { get; set; }

    partial void OnAnswerChanged(AccessibilityAnswer value) => _changed();

    /// <summary>„Ja“ chosen.</summary>
    public bool IsYes
    {
        get => Answer == AccessibilityAnswer.Yes;
        set { if (value) Answer = AccessibilityAnswer.Yes; }
    }

    /// <summary>„Nein“ chosen.</summary>
    public bool IsNo
    {
        get => Answer == AccessibilityAnswer.No;
        set { if (value) Answer = AccessibilityAnswer.No; }
    }

    /// <summary>„Trifft nicht zu“ chosen.</summary>
    public bool IsNotApplicable
    {
        get => Answer == AccessibilityAnswer.NotApplicable;
        set { if (value) Answer = AccessibilityAnswer.NotApplicable; }
    }
}

/// <summary>
/// The questions of the human check in the area „Barrierefreiheit“ (docs/ABLAUFPLAN.md, 6.7 step 5; docs/FEHLERGEDAECHTNIS.md, No. 669). The
/// answers hold for this document as it is: every change of it — an edit, a repair, a pending title — lets them lapse. They are kept in
/// memory only (nothing records which files were checked); the report carries them.
/// </summary>
public sealed partial class DocumentViewModel
{
    private bool _resettingAnswers;

    /// <summary>Taking over the saved file (the same content): answers stay.</summary>
    private int _keepAnswers;

    /// <summary>The twelve questions with their answers.</summary>
    public ObservableCollection<AccessibilityQuestionRow> AccessibilityQuestionRows => _accessibilityQuestions ??= new ObservableCollection<AccessibilityQuestionRow>(
        AccessibilityQuestions.All.Select(q => new AccessibilityQuestionRow(q, OnAnswerChanged)));

    private ObservableCollection<AccessibilityQuestionRow>? _accessibilityQuestions;

    /// <summary>„Von Ihnen zu bestätigen (5 von 12 offen)“.</summary>
    public string AccessibilityQuestionsTitle
    {
        get
        {
            var open = AccessibilityQuestionRows.Count(r => r.Answer == AccessibilityAnswer.Open);
            return open == 0 ? Tr.T("Von Ihnen zu bestätigen (alle beantwortet)") : Tr.T("Von Ihnen zu bestätigen ({0} von {1} offen)", open, AccessibilityQuestionRows.Count);
        }
    }

    /// <summary>A note that the answers lapsed because the document changed; empty if not.</summary>
    [ObservableProperty]
    public partial string AccessibilityAnswersLapsed { get; private set; } = "";

    /// <summary>The questions with their answers, for the report.</summary>
    public IReadOnlyList<(AccessibilityQuestion Question, AccessibilityAnswer Answer)> AccessibilityAnswers =>
        [.. AccessibilityQuestionRows.Select(r => (r.Question, r.Answer))];

    private void OnAnswerChanged()
    {
        if (_resettingAnswers) return;
        AccessibilityAnswersLapsed = "";
        OnPropertyChanged(nameof(AccessibilityQuestionsTitle));
        if (LastAccessibilityReport is { } report) AccessibilityHeadline = AccessibilityQuestions.Headline(report, [.. AccessibilityQuestionRows.Select(r => r.Answer)]);
    }

    /// <summary>
    /// The document changed: answers given lapse (they were given for the document as it was), and the result shown is no longer that of the
    /// document — the area says so until it checks again.
    /// </summary>
    private void AccessibilityDocumentChanged()
    {
        // the help on the pages is worked out again for the pages as they are now
        if (ShownAccessibilityHelp != AccessibilityHelp.None)
        {
            _helpWork?.Cancel();
            _helpWork = null;
            _helpPages.Clear();
            AccessibilityHelpChanged?.Invoke();
        }
        if (_accessibilityQuestions is null || _keepAnswers > 0) return;
        if (_accessibilityQuestions.All(r => r.Answer == AccessibilityAnswer.Open)) return;
        _resettingAnswers = true;
        try
        {
            foreach (var row in _accessibilityQuestions) row.Answer = AccessibilityAnswer.Open;
        }
        finally
        {
            _resettingAnswers = false;
        }
        AccessibilityAnswersLapsed = Tr.T("Das Dokument hat sich geändert — die Antworten sind verfallen.");
        OnPropertyChanged(nameof(AccessibilityQuestionsTitle));
        if (LastAccessibilityReport is { } report) AccessibilityHeadline = AccessibilityQuestions.Headline(report, [.. AccessibilityQuestionRows.Select(r => r.Answer)]);
    }

    partial void OnDescriptionOnSaveChanged(DescriptionChange? value) => AccessibilityDocumentChanged();

    partial void OnRemoveClaimOnSaveChanged(bool value) => AccessibilityDocumentChanged();

    partial void OnNameLayersOnSaveChanged(bool value) => AccessibilityDocumentChanged();
}
