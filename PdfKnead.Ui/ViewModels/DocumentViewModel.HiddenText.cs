using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.ViewModels;

/// <summary>
/// A spot with hidden text in the „Verborgener Inhalt“ panel: where, why, what it says, and whether it is marked for redaction
/// (ticking marks or unmarks it, one undo step).
/// </summary>
public sealed partial class HiddenSpotRow(DocumentViewModel doc, int page, HiddenTextSpot spot) : ObservableObject
{
    /// <summary>Page index.</summary>
    public int Page => page;

    /// <summary>Lies beside the page (the place is shown in orange).</summary>
    public bool IsOutside => spot.OutsidePage;

    /// <summary>The spot.</summary>
    public HiddenTextSpot Spot => spot;

    /// <summary>„Seite 3 · weiß auf weiß oder verdeckt“, with „wird mit dem Balken geschwärzt“ if a bar covers it.</summary>
    public string Place => Tr.T("Seite {0}", page + 1) + " · " + DocumentViewModel.HiddenKindText(spot) +
                           (IsUnderBar ? " · " + Tr.T("wird mit dem Balken geschwärzt") : "");

    /// <summary>
    /// A redaction bar covers the whole spot: it goes with the bar. Shown as marked and cannot be unticked (only taking the bar back
    /// keeps it).
    /// </summary>
    public bool IsUnderBar => doc.IsHiddenUnderBar(page, spot);

    /// <summary>The text on one line, shortened (only its beginning is looked at: the text is whole and can be long).</summary>
    public string Preview
    {
        get
        {
            var text = (spot.Text.Length > 400 ? spot.Text[..400] : spot.Text).ReplaceLineEndings(" ").Trim();
            return text.Length > 120 ? text[..120] + " …" : text;
        }
    }

    /// <summary>The whole text, for the flyout that shows it.</summary>
    public string FullText => spot.Text;

    /// <summary>„120 Zeichen“.</summary>
    public string GlyphCountText => spot.Glyphs == 1 ? Tr.T("1 Zeichen") : Tr.T("{0} Zeichen", spot.Glyphs);

    /// <summary>Copying the text is allowed (the document's permissions, as for „Text markieren“).</summary>
    public bool CopyAllowed => doc.CopyAllowed;

    /// <summary>Marked for redaction; setting it marks or unmarks the spot.</summary>
    public bool IsMarked
    {
        get => doc.IsHiddenMarked(page, spot);
        set
        {
            if (value != IsMarked && !IsUnderBar) doc.ToggleHiddenMark(page, spot);
            else OnPropertyChanged();   // the box springs back
        }
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsMarked));
        OnPropertyChanged(nameof(IsUnderBar));
        OnPropertyChanged(nameof(Place));
    }
}

/// <summary>
/// „Verborgenes schwärzen“ (KONZEPT.md, section 5.18): while the tool is on, all pages are searched for hidden text in the background;
/// the view frames the spots on the page, the panel on the right lists them (beside the page first, then on the page). A click marks a
/// spot for redaction (<see cref="RedactionElement.Hidden"/>); it is removed only by „Schwärzungen anwenden“.
/// </summary>
public sealed partial class DocumentViewModel
{
    // Spots per page (page dictionary: stays valid when pages are rearranged; a redacted page is a new one and is searched again)
    private readonly Dictionary<PdfDictionary, List<HiddenTextSpot>> _hidden = new(ReferenceEqualityComparer.Instance);
    // Pages whose search hit a limit, and pages that could not be searched at all (damaged): said in the status, never silently „none“
    private readonly HashSet<PdfDictionary> _hiddenIncomplete = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _hiddenFailed = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _hiddenScan;
    private long _hiddenRowsBuilt;

    /// <summary>Raised when spots of further pages are known (the view draws their frames).</summary>
    public event Action? HiddenSpotsChanged;

    /// <summary>Spots beside the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenOutside), nameof(HiddenStatus), nameof(HasHiddenSpots), nameof(HasOcrSpots))]
    public partial IReadOnlyList<HiddenSpotRow> HiddenOutside { get; private set; } = [];

    /// <summary>Spots on the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenOnPage), nameof(HiddenStatus), nameof(HasHiddenSpots), nameof(HasOcrSpots))]
    public partial IReadOnlyList<HiddenSpotRow> HiddenOnPage { get; private set; } = [];

    /// <summary>All rows in the order of „Vorherige/Nächste Stelle“: beside the page first, then on the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HiddenPosition))]
    public partial IReadOnlyList<HiddenSpotRow> HiddenRows { get; private set; } = [];

    /// <summary>The row chosen in one of the lists (or by a click on a frame); choosing one shows its spot.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedOutsideRow), nameof(SelectedOnPageRow), nameof(HiddenPosition))]
    public partial HiddenSpotRow? SelectedHiddenRow { get; set; }

    /// <summary>The chosen row of the list „Außerhalb des Blattes“ (null if the chosen one lies on the page); setting one chooses it.</summary>
    public HiddenSpotRow? SelectedOutsideRow
    {
        get => SelectedHiddenRow is { IsOutside: true } row ? row : null;
        set
        {
            if (value is not null) SelectedHiddenRow = value;
        }
    }

    /// <summary>The chosen row of the list „Auf dem Blatt“.</summary>
    public HiddenSpotRow? SelectedOnPageRow
    {
        get => SelectedHiddenRow is { IsOutside: false } row ? row : null;
        set
        {
            if (value is not null) SelectedHiddenRow = value;
        }
    }

    /// <summary>„Stelle 3 von 7“ for the chosen row; without one the number of spots.</summary>
    public string HiddenPosition
    {
        get
        {
            var at = SelectedHiddenRow is { } row ? HiddenRows.ToList().IndexOf(row) : -1;
            return at >= 0 ? Tr.T("Stelle {0} von {1}", at + 1, HiddenRows.Count)
                : HiddenRows.Count == 1 ? Tr.T("1 Stelle") : Tr.T("{0} Stellen", HiddenRows.Count);
        }
    }

    /// <summary>
    /// „Alle“ beside the page: ticked if all are marked, half if some are; ticking marks all, unticking takes all marks back. From the
    /// half state a click always marks all (the box would otherwise go to „none“ and take the marks already set back).
    /// </summary>
    public bool? AllOutsideMarked
    {
        get => AllMarked(HiddenOutside);
        set
        {
            var mark = value != false || AllMarked(HiddenOutside) is null;
            SetHiddenMarks(HiddenOutside, mark, mark ? Tr.T("Alles außerhalb schwärzen") : Tr.T("Verborgenes schwärzen"));
        }
    }

    /// <summary>„Alle“ on the page — without invisible text (OCR): removing it would make the document unsearchable, it is ticked one by one.</summary>
    public bool? AllOnPageMarked
    {
        get => AllMarked(OnPageWithoutOcr);
        set => SetHiddenMarks(OnPageWithoutOcr, value != false || AllMarked(OnPageWithoutOcr) is null, Tr.T("Verborgenes schwärzen"));
    }

    /// <summary>Whether „Alle“ on the page has something to mark (there are spots that are not OCR).</summary>
    public bool CanMarkAllOnPage => OnPageWithoutOcr.Any();

    /// <summary>Whether a text recognition (invisible text) was found: offers „OCR anzeigen“.</summary>
    public bool HasOcrSpots => HiddenOnPage.Any(r => r.Spot.Kind == HiddenTextKind.Invisible) || HiddenOutside.Any(r => r.Spot.Kind == HiddenTextKind.Invisible);

    private IEnumerable<HiddenSpotRow> OnPageWithoutOcr => HiddenOnPage.Where(r => r.Spot.Kind != HiddenTextKind.Invisible);

    private static bool? AllMarked(IEnumerable<HiddenSpotRow> rows)
    {
        int total = 0, marked = 0;
        foreach (var row in rows)
        {
            total++;
            if (row.IsMarked) marked++;
        }
        return marked == 0 ? false : marked == total ? true : null;
    }

    /// <summary>Marks the spots, or takes their marks back, in one undo step for all pages.</summary>
    private void SetHiddenMarks(IEnumerable<HiddenSpotRow> rows, bool mark, string description)
    {
        if (!CanRedact)
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            NotifyHiddenMarks();   // the box springs back
            return;
        }
        var pages = History.Pages.ToArray();
        var changed = false;
        foreach (var group in rows.GroupBy(r => r.Page).Where(g => g.Key < pages.Length))
        {
            var elements = pages[group.Key].Elements.ToList();
            foreach (var row in group)
            {
                if (IsHiddenUnderBar(row.Page, row.Spot)) continue;   // goes with its bar
                var marks = MarksOf(row.Page, row.Spot);
                if (mark && !IsHiddenMarked(row.Page, row.Spot)) elements.Add(MarkFor(row.Spot));
                else if (!mark && marks.Count > 0) elements.RemoveAll(e => marks.Any(m => ReferenceEquals(m, e)));
                else continue;
                changed = true;
            }
            pages[group.Key] = pages[group.Key].WithElements(elements);
        }
        if (changed) History.Apply(description, pages);
        else NotifyHiddenMarks();
    }

    private void NotifyHiddenMarks()
    {
        OnPropertyChanged(nameof(AllOutsideMarked));
        OnPropertyChanged(nameof(AllOnPageMarked));
    }

    /// <summary>Whether the search is still running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HiddenStatus))]
    public partial bool HiddenSearching { get; private set; }

    /// <summary>Pages searched so far.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HiddenStatus))]
    public partial int HiddenSearched { get; private set; }

    public bool HasHiddenOutside => HiddenOutside.Count > 0;

    public bool HasHiddenOnPage => HiddenOnPage.Count > 0;

    public bool HasHiddenSpots => HasHiddenOutside || HasHiddenOnPage;

    /// <summary>„Suche … Seite 3 von 12“, the number of spots, or that there are none — and which pages could not be searched completely.</summary>
    public string HiddenStatus
    {
        get
        {
            var count = HiddenOutside.Count + HiddenOnPage.Count;
            var found = count == 1 ? Tr.T("1 verborgene Stelle") : Tr.T("{0} verborgene Stellen", count);
            var status = HiddenSearching
                ? Tr.T("Suche … Seite {0} von {1}", Math.Min(HiddenSearched + 1, PageCount), PageCount) + (count > 0 ? " · " + found : "")
                : count == 0 && HiddenPagesNotChecked.Count == 0 ? Tr.T("Keine verborgenen Stellen gefunden.") : found;
            var failed = PagesOf(_hiddenFailed);
            var incomplete = PagesOf(_hiddenIncomplete);
            if (failed.Count > 0) status += " · " + (failed.Count == 1 ? Tr.T("Seite {0} ließ sich nicht prüfen", failed[0]) : Tr.T("{0} Seiten ließen sich nicht prüfen ({1})", failed.Count, Numbers(failed)));
            if (incomplete.Count > 0) status += " · " + (incomplete.Count == 1 ? Tr.T("Seite {0} nicht vollständig geprüft", incomplete[0]) : Tr.T("{0} Seiten nicht vollständig geprüft ({1})", incomplete.Count, Numbers(incomplete)));
            return status;

            static string Numbers(List<int> pages) => string.Join(", ", pages.Take(5)) + (pages.Count > 5 ? " …" : "");
        }
    }

    /// <summary>Page numbers (from 1) of the pages that could not be searched or only partly.</summary>
    public IReadOnlyList<int> HiddenPagesNotChecked => [.. PagesOf(_hiddenFailed).Concat(PagesOf(_hiddenIncomplete)).Distinct().Order()];

    private List<int> PagesOf(HashSet<PdfDictionary> set)
    {
        var pages = new List<int>();
        if (set.Count == 0) return pages;
        for (var i = 0; i < Pages.Count; i++)
            if (set.Contains(Pages[i].Item.Page.Dictionary)) pages.Add(i + 1);
        return pages;
    }

    /// <summary>Why nobody sees the spot, short (list and frame).</summary>
    public static string HiddenKindText(HiddenTextSpot spot) => spot.Kind switch
    {
        HiddenTextKind.Invisible => Tr.T("unsichtbarer Text, etwa OCR"),
        HiddenTextKind.HiddenLayer => Tr.T("auf einer ausgeblendeten Ebene"),
        HiddenTextKind.Outside when spot.OutsidePage => Tr.T("außerhalb der Seite"),
        HiddenTextKind.Outside => Tr.T("weggeschnitten"),
        HiddenTextKind.Tiny => Tr.T("winzige Schrift"),
        _ => Tr.T("weiß auf weiß oder verdeckt"),
    };

    /// <summary>The spots of a page; null while the page has not been searched yet (<see cref="HiddenSpotsChanged"/> follows).</summary>
    /// <param name="page">Page index.</param>
    public IReadOnlyList<HiddenTextSpot>? HiddenOn(int page) =>
        (uint)page < (uint)Pages.Count && _hidden.TryGetValue(Pages[page].Item.Page.Dictionary, out var spots) ? spots : null;

    /// <summary>Tool switched on or off: starts the search, or stops it and forgets the spots.</summary>
    private void HiddenToolChanged(ViewTool oldValue, ViewTool newValue)
    {
        if (newValue == ViewTool.RedactHidden)
        {
            SearchHidden();
            return;
        }
        if (oldValue != ViewTool.RedactHidden) return;
        _hiddenScan?.Cancel();
        _hiddenScan = null;
        _hidden.Clear();
        _hiddenIncomplete.Clear();
        _hiddenFailed.Clear();
        _hiddenOrder = [];
        SelectedHiddenRow = null;
        HiddenOutside = [];
        HiddenOnPage = [];
        HiddenRows = [];
        HiddenSearching = false;
    }

    /// <summary>Pages or marks have changed: the ticks follow; pages not searched yet (new, redacted) are searched.</summary>
    private void HiddenHistoryChanged()
    {
        if (Tool != ViewTool.RedactHidden) return;
        foreach (var row in HiddenOutside) row.Refresh();
        foreach (var row in HiddenOnPage) row.Refresh();
        NotifyHiddenMarks();
        if (Pages.Any(p => !_hidden.ContainsKey(p.Item.Page.Dictionary))) SearchHidden();
        // Pages rearranged, deleted or redacted: the rows (page numbers, spots) follow
        if (!_hiddenOrder.SequenceEqual(Pages.Select(p => p.Item.Page.Dictionary), ReferenceEqualityComparer.Instance)) BuildHiddenRows();
        HiddenSpotsChanged?.Invoke();
    }

    // The pages the rows were built for, in their order
    private List<PdfDictionary> _hiddenOrder = [];

    /// <summary>Searches the pages not yet searched, one after the other in the background.</summary>
    private void SearchHidden()
    {
        if (_hiddenScan is not null) return;
        var cts = new CancellationTokenSource();
        _hiddenScan = cts;
        HiddenSearching = true;
        _ = SearchHiddenAsync(cts);
    }

    private async Task SearchHiddenAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var item = Pages.Select(p => p.Item).FirstOrDefault(i => !_hidden.ContainsKey(i.Page.Dictionary));
                if (item is null) break;
                HiddenSearched = Pages.Count(p => _hidden.ContainsKey(p.Item.Page.Dictionary));
                HiddenTextScan? scan;
                try
                {
                    scan = await Task.Run(() =>
                    {
                        using var use = Renderer.Use(item.Source);
                        return use is null ? new HiddenTextScan([], true) : HiddenText.Scan(item.Source, item.Page, cts.Token);
                    }, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // damaged or too complex page: no spots, the others carry on — and the status names the page
                    Core.Platform.AppLog.Write("Verborgenes nicht gesucht", e.ToString());
                    scan = null;
                }
                if (cts.IsCancellationRequested) return;
                var spots = scan?.Spots ?? [];
                _hidden[item.Page.Dictionary] = spots;
                if (scan is null) _hiddenFailed.Add(item.Page.Dictionary);
                else if (!scan.Complete) _hiddenIncomplete.Add(item.Page.Dictionary);
                if (scan is not { Complete: true }) OnPropertyChanged(nameof(HiddenStatus));
                // The list follows at most four times a second (a scan with thousands of OCR lines would otherwise rebuild it per page)
                if (spots.Count > 0 && Environment.TickCount64 - _hiddenRowsBuilt > 250) BuildHiddenRows();
                HiddenSpotsChanged?.Invoke();
            }
        }
        finally
        {
            if (ReferenceEquals(_hiddenScan, cts))
            {
                _hiddenScan = null;
                HiddenSearching = false;
                BuildHiddenRows();
                HiddenSpotsChanged?.Invoke();
            }
            cts.Dispose();
        }
    }

    /// <summary>Builds the rows in page order: beside the page and on the page; the chosen row stays chosen.</summary>
    private void BuildHiddenRows()
    {
        _hiddenRowsBuilt = Environment.TickCount64;
        _hiddenOrder = [.. Pages.Select(p => p.Item.Page.Dictionary)];
        var chosen = SelectedHiddenRow?.Spot;
        List<HiddenSpotRow> outside = [], onPage = [];
        for (var i = 0; i < Pages.Count; i++)
            if (_hidden.TryGetValue(Pages[i].Item.Page.Dictionary, out var spots))
                foreach (var spot in spots)
                    (spot.OutsidePage ? outside : onPage).Add(new HiddenSpotRow(this, i, spot));
        HiddenOutside = outside;
        HiddenOnPage = onPage;
        HiddenRows = [.. outside, .. onPage];
        _choosingQuietly = true;
        SelectedHiddenRow = chosen is null ? null : outside.Concat(onPage).FirstOrDefault(r => ReferenceEquals(r.Spot, chosen));
        _choosingQuietly = false;
        OnPropertyChanged(nameof(CanMarkAllOnPage));
        NotifyHiddenMarks();
        HiddenSpotsChanged?.Invoke();
    }

    private bool _choosingQuietly;

    /// <summary>
    /// The flyout beside the panel shows the whole text of the chosen row; while it is open, choosing another row shows that one's text.
    /// A click elsewhere or Esc closes it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsHiddenTextOpen { get; set; }

    /// <summary>Chooses the row and opens the flyout with its whole text (double-click or Enter on a row).</summary>
    /// <param name="row">The row.</param>
    public void ShowHiddenText(HiddenSpotRow row)
    {
        SelectedHiddenRow = row;
        IsHiddenTextOpen = true;
    }

    /// <summary>Closes the flyout with the whole text.</summary>
    [RelayCommand]
    private void CloseHiddenText() => IsHiddenTextOpen = false;

    partial void OnSelectedHiddenRowChanged(HiddenSpotRow? value)
    {
        if (value is null) IsHiddenTextOpen = false;
        if (value is null || _choosingQuietly) return;
        ShowHiddenSpot(value);
        HiddenSpotsChanged?.Invoke();
    }

    /// <summary>Shows the spot: it comes into view — beside the page as far as the view can scroll (the view frames it or points to it).</summary>
    private void ShowHiddenSpot(HiddenSpotRow row)
    {
        var b = row.Spot.Bounds;
        RevealRequested?.Invoke(row.Page, new TextQuad(b.Left, b.Top, b.Right, b.Top, b.Right, b.Bottom, b.Left, b.Bottom));
    }

    /// <summary>
    /// Whether redaction marks hold the whole spot: every line wholly in a mark of hidden content or a bar, which removes everything
    /// under it, hidden text included (docs/FEHLERGEDAECHTNIS.md, No. 190). Wholly, also for marks of hidden content: the mark of an
    /// overlapping spot had the centre of a line under it, the spot counted as marked and its characters beside that mark stayed
    /// (No. 194). A mark over only its first line — after the page changed, or a mark from a saved file — does not count as marked, so
    /// ticking adds the full mark.
    /// </summary>
    public bool IsHiddenMarked(int page, HiddenTextSpot spot)
    {
        if ((uint)page >= (uint)Pages.Count || spot.Lines.Count == 0) return false;
        var marks = ElementsOn(page).OfType<RedactionElement>().ToList();
        return spot.Lines.All(line => marks.Any(m => Contains(m, line)));
    }

    /// <summary>
    /// Whether bars (marks that are not of hidden content) hold every line of the spot wholly: it goes with them and is not marked on its
    /// own. Only wholly: a bar over a few letters of a long line has the line's centre under it but removes those letters only
    /// (docs/FEHLERGEDAECHTNIS.md, No. 193).
    /// </summary>
    public bool IsHiddenUnderBar(int page, HiddenTextSpot spot)
    {
        if ((uint)page >= (uint)Pages.Count || spot.Lines.Count == 0) return false;
        var bars = ElementsOn(page).OfType<RedactionElement>().Where(r => !r.Hidden).ToList();
        return bars.Count > 0 && spot.Lines.All(line => bars.Any(m => Contains(m, line)));
    }

    /// <summary>Whether one area of the mark holds the whole line (to 0.01 pt).</summary>
    private static bool Contains(RedactionElement mark, Core.Pdf.Document.PdfRectangle line) =>
        mark.PageAreas.Any(a => line.Left >= a.Left - 0.01 && line.Right <= a.Right + 0.01 && line.Bottom >= a.Bottom - 0.01 && line.Top <= a.Top + 0.01);

    /// <summary>
    /// The marks of hidden content that hold a line of the spot wholly (they go when it is unticked) — not the mark of an overlapping
    /// spot that only reaches over it.
    /// </summary>
    private List<RedactionElement> MarksOf(int page, HiddenTextSpot spot)
    {
        if ((uint)page >= (uint)Pages.Count || spot.Lines.Count == 0) return [];
        return [.. ElementsOn(page).OfType<RedactionElement>().Where(r => r.Hidden && spot.Lines.Any(line => Contains(r, line)))];
    }

    /// <summary>Marks the spot for redaction, or takes the mark back (one undo step each).</summary>
    /// <param name="page">Page index.</param>
    /// <param name="spot">The spot.</param>
    public void ToggleHiddenMark(int page, HiddenTextSpot spot)
    {
        if ((uint)page >= (uint)Pages.Count) return;
        if (!CanRedact)
        {
            Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return;
        }
        if (IsHiddenUnderBar(page, spot))
        {
            Notify(Tr.T("Diese Stelle liegt unter einem Schwärzungsbalken und wird mit ihm entfernt."));
            return;
        }
        if (IsHiddenMarked(page, spot)) History.RemoveElements(page, MarksOf(page, spot), Tr.T("Verborgenes schwärzen"));
        else History.AddElement(page, MarkFor(spot), Tr.T("Verborgenes schwärzen"));
    }

    private static RedactionElement MarkFor(HiddenTextSpot spot) => RedactionElement.FromPageAreas(HiddenText.MarkAreas(spot)) with { Hidden = true };

    /// <summary>Chooses the row of a spot clicked on the page (without scrolling: it is in view already).</summary>
    public void ChooseHiddenSpot(int page, HiddenTextSpot spot)
    {
        _choosingQuietly = true;
        SelectedHiddenRow = HiddenRows.FirstOrDefault(r => r.Page == page && ReferenceEquals(r.Spot, spot));
        _choosingQuietly = false;
        HiddenSpotsChanged?.Invoke();
    }

    /// <summary>„Alles außerhalb schwärzen“: marks every spot beside the page that is not marked yet (one undo step).</summary>
    [RelayCommand]
    private void MarkHiddenOutside() => AllOutsideMarked = true;

    /// <summary>„Nächste Stelle“: the next row (beside the page first, then on the page), at the end back to the first.</summary>
    [RelayCommand]
    private void NextHiddenSpot() => StepHiddenSpot(1);

    /// <summary>„Vorherige Stelle“.</summary>
    [RelayCommand]
    private void PreviousHiddenSpot() => StepHiddenSpot(-1);

    private void StepHiddenSpot(int step)
    {
        var rows = HiddenRows.ToList();
        if (rows.Count == 0) return;
        var at = SelectedHiddenRow is { } current ? rows.IndexOf(current) : -1;
        var next = at < 0 ? (step > 0 ? 0 : rows.Count - 1) : ((at + step) % rows.Count + rows.Count) % rows.Count;
        if (ReferenceEquals(rows[next], SelectedHiddenRow)) ShowHiddenSpot(rows[next]);
        else SelectedHiddenRow = rows[next];
    }
}
