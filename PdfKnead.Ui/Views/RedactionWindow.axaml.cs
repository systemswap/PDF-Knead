using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>A reported code with its check box in the redaction question.</summary>
public sealed partial class RedactionCodeChoice(RedactionCode code) : ObservableObject
{
    /// <summary>The code.</summary>
    public RedactionCode Code { get; } = code;

    /// <summary>Whether it gets a bar over the whole code; checked at first.</summary>
    [ObservableProperty]
    public partial bool Redact { get; set; } = true;

    /// <summary>The line shown: page, kind and why.</summary>
    public string Text { get; } = code.Phrase is { } phrase
        ? Tr.T("Seite {0}: {1} enthält „{2}“", code.Page + 1, DocumentViewModel.FormatName(code.Code.Code.Format), Core.Barcodes.BarcodeInterpreter.Safe(phrase))
        : Tr.T("Seite {0}: {1} nur teilweise geschwärzt — der Inhalt kann trotzdem lesbar sein", code.Page + 1, DocumentViewModel.FormatName(code.Code.Code.Format));
}

/// <summary>A line of the other places in the redaction question.</summary>
/// <param name="hit">The hit.</param>
public sealed class RedactionHitLine(RedactionHit hit)
{
    /// <summary>The hit.</summary>
    public RedactionHit Hit { get; } = hit;

    /// <summary>It is invisible and goes with the old text recognition anyway.</summary>
    public bool GoesAlong { get; set; }

    /// <summary>„Seite 1“, for invisible text with its note.</summary>
    public string Where => !Hit.Invisible ? Tr.T("Seite {0}", Hit.Page + 1)
        : GoesAlong ? Tr.T("Seite {0} (versteckt, wird mit entfernt)", Hit.Page + 1)
        : Tr.T("Seite {0} (versteckt)", Hit.Page + 1);

    /// <summary>The line as text (for screen readers and tests).</summary>
    public override string ToString() => Where + ": " + Hit.Context;

    /// <summary>The line to show: the hit set off, „versteckt“ in orange — grey if it goes anyway.</summary>
    internal Control Build()
    {
        var text = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Inlines = [] };
        Avalonia.Automation.AutomationProperties.SetName(text, ToString());
        if (GoesAlong) text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("TextMutedBrush"));
        var page = new Run(Where + ": ");
        if (Hit.Invisible && !GoesAlong)
        {
            page.Bind(Avalonia.Controls.Documents.TextElement.ForegroundProperty, text.GetResourceObservable("WarnBrush"));
            page.FontWeight = FontWeight.SemiBold;
        }
        text.Inlines!.Add(page);
        var (at, length) = (Hit.InContext.Start, Hit.InContext.Length);
        if (length <= 0 || at + length > Hit.Context.Length)
        {
            text.Inlines.Add(new Run(Hit.Context));
            return text;
        }
        text.Inlines.Add(new Run(Hit.Context[..at]));
        text.Inlines.Add(new Run(Hit.Context.Substring(at, length)) { FontWeight = FontWeight.Bold });
        text.Inlines.Add(new Run(Hit.Context[(at + length)..]));
        return text;
    }
}

/// <summary>
/// “Redact completely first, or keep marking?”, asked before anything leaves the document: redact (in the open document,
/// optionally also turning the pages into images) or keep marking.
/// </summary>
public partial class RedactionWindow : Window
{
    public RedactionWindow()
    {
        InitializeComponent();
        // never taller than the screen: the content rolls, the buttons stay visible
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } screen) MaxHeight = screen.WorkingArea.Height / screen.Scaling * 0.92;
            // with other places to look at: at the right edge of the main window, so that the page beside it stays in view
            if (_hits.Count > 0 && Owner is Window owner)
            {
                var scale = owner.RenderScaling;
                var x = owner.Position.X + (int)((owner.Bounds.Width - Bounds.Width - 16) * scale);
                Position = new Avalonia.PixelPoint(Math.Max(owner.Position.X, x), owner.Position.Y + (int)(90 * scale));
            }
        };
    }

    /// <param name="question">How many redactions there are, what happens afterwards and what is allowed.</param>
    public RedactionWindow(RedactionQuestion question) : this()
    {
        CountText.Text = (question.Marks, question.Pages) switch
        {
            (1, _) => Tr.T("1 Schwärzung auf 1 Seite"),
            (_, 1) => Tr.T("{0} Schwärzungen auf 1 Seite", question.Marks),
            _ => Tr.T("{0} Schwärzungen auf {1} Seiten", question.Marks, question.Pages),
        };
        if (question.Action is { } action)
        {
            ActionText.IsVisible = true;
            ActionText.Text = Tr.T("Vor „{0}“ wird zuerst geschwärzt.", action);
        }
        // Hidden when “Save as image” is pending: the pages become images anyway.
        AsImages.IsVisible = question.CanChooseImages;
        // The old text recognition: only if the document has one
        if (question.OcrPages.Count > 0)
        {
            RemoveOcr.IsVisible = true;
            OcrText.Text = question.OcrPages.Count == 1
                ? Tr.T("Unsichtbarer Text eines Scans auf 1 Seite. Gescannte Seiten sind danach nicht mehr durchsuchbar.")
                : Tr.T("Unsichtbarer Text eines Scans auf {0} Seiten. Gescannte Seiten sind danach nicht mehr durchsuchbar.", question.OcrPages.Count);
        }
        // Reasons (B-14): for all marks without their own; the options only count with a reason
        Reason.ItemsSource = question.ReasonSuggestions;
        ReasonName.Text = question.RedactionName;
        if (question.OwnReasons > 0)
        {
            OwnReasonsText.IsVisible = true;
            OwnReasonsText.Text = question.OwnReasons == 1
                ? Tr.T("1 Markierung hat einen eigenen Grund; der Grund hier gilt für die übrigen.")
                : Tr.T("{0} Markierungen haben einen eigenen Grund; der Grund hier gilt für die übrigen.", question.OwnReasons);
            // how many carry a code (wish of 4.11.2026): written into the bar as chosen when marking, all switched off below
            if (question.CodedReasons > 0)
                OwnReasonsText.Text += " " + (question.CodedReasons == 1
                    ? Tr.T("1 davon mit Schwärzungscode.")
                    : Tr.T("{0} davon mit Schwärzungscode.", question.CodedReasons));
        }
        // A new text recognition from what stays visible: offered where text recognition goes (old OCR removed, pages as images)
        _question = question;
        NewOcrPanel.IsVisible = question.OcrPages.Count > 0 || question.CanChooseImages;
        RefreshOcrSizes(question.OcrSize);
        NewOcr.IsChecked = question.OcrPages.Count > 0 && _installed.Count > 0;
        OnHiddenChoiceChanged(null, null);
        OnReasonChanged(null, null);
        // Redacted text still appears elsewhere: exactly the marked text (ticked) and single words of it (not ticked), every place to click
        _hits = [.. question.Unredacted.Select(h => new RedactionHitLine(h))];
        UnredactedList.ItemTemplate = WordsList.ItemTemplate = new FuncDataTemplate<RedactionHitLine>((line, _) => line?.Build());
        RefreshHitLines();
        // QR and bar codes: their error correction may restore content under a partial bar, and redacted text may sit in their content
        if (question.Codes.Count > 0)
        {
            CodesPanel.IsVisible = true;
            CodesTitle.Text = question.Codes.Count == 1
                ? Tr.T("1 QR- oder Barcode ist nicht ganz geschwärzt:")
                : Tr.T("{0} QR- oder Barcodes sind nicht ganz geschwärzt:", question.Codes.Count);
            _codes = question.Codes.Select(c => new RedactionCodeChoice(c)).ToList();
            CodesList.ItemsSource = _codes;
        }
        // pages whose search ran out of time (crafted or damaged content): never silently taken as "no codes"
        if (question.UncheckedCodePages.Count > 0)
        {
            CodesPanel.IsVisible = true;
            if (question.Codes.Count == 0)
            {
                CodesTitle.IsVisible = false;
                CodesHint.IsVisible = false;
            }
            CodesUnchecked.IsVisible = true;
            var pages = string.Join(", ", question.UncheckedCodePages.Order().Take(10).Select(p => (p + 1).ToString(System.Globalization.CultureInfo.CurrentCulture)));
            if (question.UncheckedCodePages.Count > 10) pages += " …";
            CodesUnchecked.Text = question.UncheckedCodePages.Count == 1
                ? Tr.T("Auf Seite {0} wurden QR- und Barcodes nicht geprüft: das Zeichnen der Seite dauert zu lange. Bitte dort selbst nachsehen.", pages)
                : Tr.T("Auf den Seiten {0} wurden QR- und Barcodes nicht geprüft: das Zeichnen dauert zu lange. Bitte dort selbst nachsehen.", pages);
        }
        // Outside the page text (properties, bookmarks, comments …) no redaction box helps, only cleaning up.
        if (question.Elsewhere.Count > 0)
        {
            ElsewherePanel.IsVisible = true;
            var places = question.Elsewhere.Where(e => !e.Sentence).Select(e => e.Where).ToList();
            ElsewhereText.Text = string.Join("\n", (places.Count > 0 ? [Tr.T("Geschwärzter Text steht auch in: {0}", string.Join(", ", places))] : Array.Empty<string>())
                .Concat(question.Elsewhere.Where(e => e.Sentence).Select(e => e.Where)));
            _keepWarnings = string.Join("\n", question.Elsewhere.Select(e => e.KeepWarning).OfType<string>());
            CleanElsewhere.IsCheckedChanged += (_, _) => ShowKeepWarning();
            ShowKeepWarning();
            CleanElsewhere.IsVisible = question.Elsewhere.Any(e => e.Cleanup != Core.Pdf.Writing.CleanupItems.None);
            ManualHint.IsVisible = question.Elsewhere.Any(e => e.Cleanup == Core.Pdf.Writing.CleanupItems.None);
        }
    }

    /// <summary>What stays if „Gleich mit entfernen“ is unticked (the entries' warnings); empty: nothing to warn of.</summary>
    private string _keepWarnings = "";

    /// <summary>The warning under „Gleich mit entfernen“ while it is unticked (e.g. an e-invoice stays with all its data).</summary>
    private void ShowKeepWarning()
    {
        KeepWarning.Text = _keepWarnings;
        KeepWarning.IsVisible = _keepWarnings.Length > 0 && CleanElsewhere.IsChecked != true;
    }

    /// <summary>
    /// Keeping the old text recognition shows the warning; the area applies while something is to be removed. A new text recognition needs
    /// models and pages it is for: the old one removed, or the pages turned into images — keeping the old one greys it out.
    /// </summary>
    private void OnHiddenChoiceChanged(object? sender, RoutedEventArgs? e)
    {
        if (_question is null) return;
        var ocr = RemoveOcr.IsVisible && RemoveOcr.IsChecked == true;
        OcrWarning.IsVisible = RemoveOcr.IsVisible && RemoveOcr.IsChecked != true;
        ScopePanel.IsEnabled = CleanHidden.IsChecked == true || ocr;
        NewOcr.IsEnabled = _installed.Count > 0 && (ocr || AsImages.IsVisible && AsImages.IsChecked == true);
        OcrSizePanel.IsEnabled = NewOcr.IsEnabled && NewOcr.IsChecked == true;
        // the same choice right under „als Bild flachlegen“ (wish of 4.11.2026): only there with models and while the pages become images
        AsImagesOcr.IsVisible = AsImages.IsVisible;
        AsImagesOcr.IsEnabled = _installed.Count > 0 && AsImages.IsChecked == true;
        ToolTip.SetTip(AsImagesOcr, _installed.Count > 0 ? null : Tr.T("Die Modelle der Texterkennung fehlen im Ordner „ocr“ neben dem Programm."));
        SyncOcrChoices(fromBelow: false);
        RefreshHitLines();
    }

    private bool _syncingOcr;

    /// <summary>Keeps the check box under „als Bild flachlegen“ and the one above („Neue Texterkennung anlegen“) the same choice.</summary>
    private void SyncOcrChoices(bool fromBelow)
    {
        if (_syncingOcr) return;
        _syncingOcr = true;
        try
        {
            if (fromBelow) NewOcr.IsChecked = AsImagesOcr.IsChecked;
            else AsImagesOcr.IsChecked = NewOcr.IsChecked == true;
        }
        finally
        {
            _syncingOcr = false;
        }
    }

    private void OnNewOcrChanged(object? sender, RoutedEventArgs e) => OnHiddenChoiceChanged(sender, e);

    private void OnAsImagesOcrChanged(object? sender, RoutedEventArgs e)
    {
        if (_question is null || _syncingOcr) return;
        SyncOcrChoices(fromBelow: true);
    }

    private List<RedactionHitLine> _hits = [];
    private bool _fillingHits;

    /// <summary>The lines of the other places, as the choice about the old text recognition has them: what goes anyway is grey and needs no tick.</summary>
    private void RefreshHitLines()
    {
        if (_question is null || _hits.Count == 0) return;
        var removeOcr = RemoveOcr.IsVisible && RemoveOcr.IsChecked == true;
        foreach (var line in _hits) line.GoesAlong = _question.GoesAlong(line.Hit, removeOcr, WholeDocument.IsChecked == true);
        Fill(UnredactedPanel, UnredactedList, UnredactedTitle, AlsoUnredacted, _hits.Where(l => l.Hit.Whole).ToList(), n => n == 1
            ? Tr.T("Geschwärzter Text steht noch an 1 weiteren Stelle:") : Tr.T("Geschwärzter Text steht noch an {0} weiteren Stellen:", n));
        Fill(WordsPanel, WordsList, WordsTitle, AlsoWords, _hits.Where(l => !l.Hit.Whole).ToList(), n => n == 1
            ? Tr.T("Einzelne Wörter daraus stehen noch an 1 weiteren Stelle:") : Tr.T("Einzelne Wörter daraus stehen noch an {0} weiteren Stellen:", n));
    }

    private void Fill(Border panel, ListBox list, TextBlock title, CheckBox tick, List<RedactionHitLine> lines, Func<int, string> caption)
    {
        panel.IsVisible = lines.Count > 0;
        title.Text = caption(lines.Count);
        tick.IsVisible = lines.Any(l => !l.GoesAlong);
        var chosen = list.SelectedItem;
        _fillingHits = true;
        list.ItemsSource = null;
        list.ItemsSource = lines;
        list.SelectedItem = chosen;
        _fillingHits = false;
    }

    /// <summary>A line chosen: the viewer jumps there and frames the place; the frame goes when no line is chosen any more.</summary>
    private async void OnHitChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (_fillingHits || _question?.ShowHit is not { } show) return;
        var other = ReferenceEquals(sender, UnredactedList) ? WordsList : UnredactedList;
        if (e.AddedItems.Count > 0 && other.SelectedItem is not null)
        {
            _fillingHits = true;
            other.SelectedItem = null;
            _fillingHits = false;
        }
        if ((sender as ListBox)?.SelectedItem is RedactionHitLine line) await show(line.Hit);
        else if (UnredactedList.SelectedItem is null && WordsList.SelectedItem is null) await show(null);
    }

    /// <summary>The lines of the other places (for tests).</summary>
    internal IReadOnlyList<RedactionHitLine> HitLines => _hits;

    private RedactionQuestion? _question;
    private IReadOnlyList<Core.Ocr.OcrModelSize> _installed = [];

    /// <summary>Asks which sizes are there (both come with the program); only those can be chosen, the wished one if possible, else small.</summary>
    private void RefreshOcrSizes(Core.Ocr.OcrModelSize wished)
    {
        _installed = _question!.InstalledOcr();
        OcrMedium.IsEnabled = _installed.Contains(Core.Ocr.OcrModelSize.Medium);
        OcrSmall.IsEnabled = _installed.Contains(Core.Ocr.OcrModelSize.Small);
        var size = _installed.Contains(wished) ? wished : _installed.Contains(Core.Ocr.OcrModelSize.Small) ? Core.Ocr.OcrModelSize.Small
            : _installed.FirstOrDefault(wished);
        OcrMedium.IsChecked = size == Core.Ocr.OcrModelSize.Medium;
        OcrSmall.IsChecked = size == Core.Ocr.OcrModelSize.Small;
    }

    /// <summary>The options of the reasons count only with a reason; a reason or name that names what is redacted is warned about.</summary>
    private void OnReasonChanged(object? sender, RoutedEventArgs? e)
    {
        if (_question is null) return;
        var any = RedactionReasons.Clean(Reason.Text) is not null || _question.OwnReasons > 0;
        ReasonPanel.IsEnabled = any;
        ProtocolFirst.IsEnabled = ProtocolLast.IsEnabled = ReasonName.IsEnabled = ReasonProtocol.IsChecked == true;
        // where the reason for all does not fit into a bar, not even as the code it names in brackets: told at once (No. 529)
        var reason = RedactionReasons.Clean(Reason.Text);
        var notes = ReasonNotes.IsChecked == true;
        var tooSmall = reason is null || ReasonInBars.IsChecked != true ? 0
            : _question.BarsWithoutReason.Count(b => !RedactionReasons.FitsInBar(reason, b, notes));
        FitWarning.IsVisible = tooSmall > 0;
        FitWarning.Text = tooSmall == 0 ? ""
            : Tr.T("Passt in {0} von {1} Balken nicht — dort wird er weggelassen, im Schwärzungsprotokoll steht er.", tooSmall,
                _question.BarsWithoutReason.Count);
        var revealing = RedactionReasons.Revealing([Reason.Text, ReasonName.Text], _question.RedactedPhrases);
        ReasonWarning.IsVisible = revealing.Count > 0;
        ReasonWarning.Text = revealing.Count > 0
            ? Tr.T("Achtung: „{0}“ steht auch im geschwärzten Text — ein Grund oder Name verriete dann, was weg ist.", string.Join("“, „", revealing.Take(3)))
            : "";
    }

    /// <summary>The reasons chosen; null if there is none (redacted as always).</summary>
    private RedactionReasonSettings? Reasons
    {
        get
        {
            var reason = RedactionReasons.Clean(Reason.Text);
            if (reason is null && _question?.OwnReasons is not > 0) return null;
            var settings = new RedactionReasonSettings(reason, ReasonInBars.IsChecked == true, ReasonNotes.IsChecked == true,
                ReasonProtocol.IsChecked != true ? RedactionProtocolPlace.None : ProtocolLast.IsChecked == true ? RedactionProtocolPlace.Last : RedactionProtocolPlace.First,
                RedactionReasons.Clean(ReasonName.Text));
            return settings.Any ? settings : null;
        }
    }

    /// <summary>The name as entered (empty to forget it).</summary>
    internal string EnteredName => ReasonName.Text?.Trim() ?? "";

    /// <summary>The size chosen for the new text recognition, or null if none is to follow.</summary>
    private Core.Ocr.OcrModelSize? NewOcrSize =>
        !NewOcrPanel.IsVisible || !NewOcr.IsEnabled || NewOcr.IsChecked != true ? null
        : OcrMedium.IsChecked == true ? Core.Ocr.OcrModelSize.Medium
        : OcrSmall.IsChecked == true ? Core.Ocr.OcrModelSize.Small
        : null;

    /// <summary>Builds the reply with the selected check boxes (for the window and for tests).</summary>
    /// <param name="answer">The button pressed.</param>
    /// <returns>The reply; <see cref="RedactionAnswer.Apply"/> becomes <see cref="RedactionAnswer.ApplyAsImages"/> if that option is visible and checked.</returns>
    internal RedactionReply Reply(RedactionAnswer answer) =>
        new(answer == RedactionAnswer.Apply && AsImages.IsVisible && AsImages.IsChecked == true ? RedactionAnswer.ApplyAsImages : answer,
            UnredactedPanel.IsVisible && AlsoUnredacted.IsVisible && AlsoUnredacted.IsChecked == true,
            ElsewherePanel.IsVisible && CleanElsewhere.IsVisible && CleanElsewhere.IsChecked == true,
            [.. _codes.Where(c => c.Redact).Select(c => c.Code)],
            HideLengths.IsChecked == true,
            CleanHidden.IsChecked == true,
            RemoveOcr.IsVisible && RemoveOcr.IsChecked == true,
            WholeDocument.IsChecked == true,
            NewOcrSize,
            Reasons,
            WordsPanel.IsVisible && AlsoWords.IsVisible && AlsoWords.IsChecked == true);

    private List<RedactionCodeChoice> _codes = [];

    /// <summary>The reported codes with their check boxes (for tests).</summary>
    internal IReadOnlyList<RedactionCodeChoice> Codes => _codes;

    private void OnApply(object? sender, RoutedEventArgs e) => Close(Reply(RedactionAnswer.Apply));

    private void OnKeepMarking(object? sender, RoutedEventArgs e) => Close(new RedactionReply(RedactionAnswer.KeepMarking));
}
