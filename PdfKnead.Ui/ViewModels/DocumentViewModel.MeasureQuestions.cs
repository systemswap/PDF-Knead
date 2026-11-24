using PdfKnead.Localization;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>
/// The questions of „Messen“ before something drawn is lost (decided on 8.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 619): Esc with a
/// measurement going on that could be ended — users press it to finish — asks whether to end it, end it and insert it into the PDF, or drop
/// it; switching the tool off with measurements that are not in the PDF asks whether to insert them as dimensions, drop them or go back
/// („Nicht mehr fragen“ switches that off, the area „Messen“ on again). Without a view (no <see cref="AskChoices"/>) all stays as before:
/// Esc drops, switching off clears.
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>Asks a question through the view (set by the main view model); the answer is null without a view.</summary>
    public Func<ChoiceRequest, Task<ChoiceAnswer?>>? AskChoices { get; set; }

    /// <summary>A question of „Messen“ is open (a second Esc or a second switch does not ask again).</summary>
    private bool _askingMeasure;

    /// <summary>Whether the measurement going on can be ended: enough points for its kind, and they measure.</summary>
    public bool CanFinishMeasure => MeasurePage >= 0 && _measurePoints.Count >= PointsToFinish(MeasureKind) &&
                                    MeasureOn(MeasurePage, MeasureKind, [.. _measurePoints], _measureArcPoints).Value is not null;

    /// <summary>Whether to ask about measurements not in the PDF when „Messen“ is switched off (saved with the settings).</summary>
    public bool MeasureAskOnLeave
    {
        get => UserSettings.Current.AskUnsavedMeasurements;
        set
        {
            if (value == MeasureAskOnLeave) return;
            UserSettings.Update(s => s with { AskUnsavedMeasurements = value });
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Esc with a measurement going on: one that cannot be ended yet (a single point) is dropped as before; one that can be ended asks —
    /// „Abschließen“ (the default), „Abschließen und in die PDF einfügen“ (as a dimension, where the file allows changes), „Verwerfen“, or
    /// „Weiter messen“ (Esc in the question). Without a view it is dropped.
    /// </summary>
    public async Task EscapeMeasureAsync()
    {
        if (_askingMeasure) return;
        if (!CanFinishMeasure || AskChoices is not { } ask)
        {
            CancelMeasure();
            return;
        }
        _askingMeasure = true;
        try
        {
            // a dimension being continued is in the PDF already
            var insert = CanAnnotate && _continuing is not { IsSaved: true };
            var answers = insert
                ? new[] { Tr.T("Abschließen"), Tr.T("Abschließen und in die PDF einfügen"), Tr.T("Verwerfen") }
                : new[] { Tr.T("Abschließen"), Tr.T("Verwerfen") };
            var answer = await ask(new ChoiceRequest(Tr.T("Messung abschließen?"),
                Tr.T("Die Messung ist noch nicht abgeschlossen und nicht in der PDF. Abgeschlossen bleibt sie sichtbar, solange „Messen“ an ist; in die PDF kommt sie als Bemaßung, die mit der Datei gespeichert wird."),
                answers, Tr.T("Weiter messen"), Icon: "Icon.Ruler"));
            switch (answer?.Answer)
            {
                case null:
                    CancelMeasure();
                    break;
                case 1:
                    FinishMeasure();
                    break;
                case 2 when insert:
                    if (CompleteMeasure() is { } row) InsertDimensions([row]);
                    break;
                case 2 or 3:
                    CancelMeasure();
                    break;
            }
        }
        finally
        {
            _askingMeasure = false;
        }
    }

    /// <summary>
    /// „Messen“ was switched off with measurements of this session (not in the PDF; <paramref name="parked"/>, already taken off the pages):
    /// „In die PDF einfügen“ (the default, where the file allows changes) puts them on their pages as dimensions, „Verwerfen“ lets them go,
    /// „Zurück zum Messen“ switches the tool on again and brings them back. „Nicht mehr fragen“ is saved whatever the answer.
    /// </summary>
    private async Task AskLeaveMeasureAsync(IReadOnlyList<MeasureRow> parked, Func<ChoiceRequest, Task<ChoiceAnswer?>> ask)
    {
        _askingMeasure = true;
        ChoiceAnswer? answer;
        var insert = CanAnnotate;
        try
        {
            var one = parked.Count == 1;
            var answers = insert ? new[] { one ? Tr.T("In die PDF einfügen") : Tr.T("Alle in die PDF einfügen"), Tr.T("Verwerfen") } : new[] { Tr.T("Verwerfen") };
            answer = await ask(new ChoiceRequest(one ? Tr.T("Messung in die PDF übernehmen?") : Tr.T("Messungen in die PDF übernehmen?"),
                one ? Tr.T("In „{0}“ ist eine Messung nicht in der PDF — ohne Einfügen ist sie fort, sobald „Messen“ aus ist. Als Bemaßung eingefügt wird sie mit der Datei gespeichert; andere Programme messen dasselbe.", FileName)
                    : Tr.T("In „{0}“ sind {1} Messungen nicht in der PDF — ohne Einfügen sind sie fort, sobald „Messen“ aus ist. Als Bemaßungen eingefügt werden sie mit der Datei gespeichert; andere Programme messen dasselbe.", FileName, parked.Count),
                answers, Tr.T("Zurück zum Messen"), Tr.T("Nicht mehr fragen (wieder einschaltbar im Bereich „Messen“)"), "Icon.Ruler"));
        }
        finally
        {
            _askingMeasure = false;
        }
        if (answer is not { } given) return;
        if (given.Checked) MeasureAskOnLeave = false;
        switch (given.Answer)
        {
            case 0:
                // back: the tool on again (the main view model follows), the measurements as they were
                Tool = ViewTool.Measure;
                if (!IsMeasuring) return;
                foreach (var row in parked)
                    if (!Measurements.Contains(row)) Measurements.Add(row);
                _measuredPageCount = Pages.Count;
                MeasureChanged?.Invoke();
                break;
            case 1 when insert:
                InsertDimensions(parked);
                break;
        }
    }

    /// <summary>
    /// When „Messen“ is switched off: the measurement going on is ended if it can be (what was drawn counts), and the measurements of this
    /// session are kept for the question; null if there is nothing to ask about (no view, nothing left, „Nicht mehr fragen“, a question open).
    /// </summary>
    private IReadOnlyList<MeasureRow>? KeepForLeaveQuestion(out Func<ChoiceRequest, Task<ChoiceAnswer?>>? ask)
    {
        ask = null;
        if (AskChoices is not { } asker || !MeasureAskOnLeave || _askingMeasure) return null;
        if (CanFinishMeasure) CompleteMeasure();
        var parked = Measurements.Where(r => r.IsSession).ToList();
        if (parked.Count == 0) return null;
        ask = asker;
        return parked;
    }
}
