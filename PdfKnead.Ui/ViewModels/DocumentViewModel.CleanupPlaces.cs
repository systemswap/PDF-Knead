using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.ViewModels;

/// <summary>A page of a finding in the area „Dokument bereinigen“: a small button that shows the page with the places framed.</summary>
public sealed class CleanupPageLink(CleanupRow row, int page)
{
    /// <summary>The page (index).</summary>
    public int Page => page;

    /// <summary>The page number as shown.</summary>
    public string Number => (page + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>„Seite 3 zeigen“ (tooltip and name for screen readers).</summary>
    public string Tip => Tr.T("Seite {0} zeigen", page + 1);

    /// <summary>The row it belongs to.</summary>
    public CleanupRow Row => row;
}

/// <summary>A finding in the area „Dokument bereinigen“: to be ticked, with its details and its pages.</summary>
public sealed partial class CleanupRow : ObservableObject
{
    /// <summary>Maximum number of pages shown as buttons; more are hinted with „…“.</summary>
    private const int MaxPageLinks = 30;

    /// <summary>Creates the row.</summary>
    /// <param name="finding">The finding.</param>
    /// <param name="chosen">Whether it is ticked at first.</param>
    public CleanupRow(CleanupFinding finding, bool chosen)
    {
        Item = finding.Item;
        Title = CleanupLabels.Title(finding);
        Detail = finding.Detail;
        Note = CleanupLabels.Note(finding.Item) ?? "";
        Pages = finding.Pages is { Count: > 0 } pages ? [.. pages.Take(MaxPageLinks).Select(p => new CleanupPageLink(this, p))] : [];
        MorePages = finding.Pages is { Count: > MaxPageLinks };
        IsChecked = chosen;
    }

    /// <summary>Creates the row of a module (KONZEPT.md, section 5.24, decision (4) of 15.11.2026): not ticked at first.</summary>
    /// <param name="module">What the module found.</param>
    public CleanupRow(Modules.ModuleCleanupFinding module)
    {
        Item = CleanupItems.None;
        Title = module.Title;
        Detail = "";
        Note = module.Note;
        Pages = [];
        Module = module;
    }

    /// <summary>The finding of a module (its own row); null for the program's own items.</summary>
    public Modules.ModuleCleanupFinding? Module { get; }

    /// <summary>The item.</summary>
    public CleanupItems Item { get; }

    /// <summary>What, how many.</summary>
    public string Title { get; }

    /// <summary>The details of the finding (e.g. the entries of the metadata); may be empty.</summary>
    public string Detail { get; }

    /// <summary>What has to be said right under it (consequences, licences); may be empty.</summary>
    public string Note { get; }

    /// <summary>The pages as buttons.</summary>
    public IReadOnlyList<CleanupPageLink> Pages { get; }

    /// <summary>Whether there are more pages than buttons.</summary>
    public bool MorePages { get; }

    /// <summary>Whether it has pages.</summary>
    public bool HasPages => Pages.Count > 0;

    /// <summary>Whether it is removed.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>What the last page button framed (empty: nothing shown yet).</summary>
    [ObservableProperty]
    public partial string Result { get; set; } = "";
}

/// <summary>
/// „Dokument bereinigen“ as an area on the right (KONZEPT.md, section 5.18; wish of 29.10.2026: not modal): the findings to tick; a page
/// button of a finding brings the page into view with its places framed in red, hidden text readable in them. The „x“ on a frame keeps that
/// place („nicht löschen“): it stays although its item is removed (<see cref="PdfSaveOptions.KeepPlaces"/>).
/// </summary>
public sealed partial class DocumentViewModel
{
    private CancellationTokenSource? _cleanupPlacesWork;
    private readonly List<CleanupKeep> _cleanupKept = [];

    /// <summary>The places shown for a finding of „Dokument bereinigen“: page, item and places; null if none are shown.</summary>
    public (int Page, CleanupItems Item, IReadOnlyList<CleanupPlace> Places)? CleanupPlaces { get; private set; }

    /// <summary>Whether the area „Dokument bereinigen“ is open.</summary>
    [ObservableProperty]
    public partial bool IsCleanupOpen { get; private set; }

    /// <summary>Whether the check runs (the area shows that it is looking).</summary>
    [ObservableProperty]
    public partial bool CleanupSearching { get; private set; }

    /// <summary>The findings in the area.</summary>
    public ObservableCollection<CleanupRow> CleanupRows { get; } = [];

    /// <summary>Whether nothing was found.</summary>
    public bool CleanupNothing => !CleanupSearching && CleanupRows.Count == 0;

    /// <summary>The places that stay although their item is removed.</summary>
    public IReadOnlyList<CleanupKeep> CleanupKept => _cleanupKept;

    /// <summary>„2 Stellen bleiben (mit x abgewählt)“; empty if none.</summary>
    public string CleanupKeptText => _cleanupKept.Count switch
    {
        0 => "",
        1 => Tr.T("1 Stelle bleibt (auf der Seite mit „x“ abgewählt)."),
        var n => Tr.T("{0} Stellen bleiben (auf der Seite mit „x“ abgewählt).", n),
    };

    /// <summary>The items ticked.</summary>
    public CleanupItems CleanupChosen => CleanupRows.Where(r => r.IsChecked).Aggregate(CleanupItems.None, (all, r) => all | r.Item);

    /// <summary>Every embedded file of the document when the area was opened (for keeping all but a module's files).</summary>
    private IReadOnlySet<Core.Pdf.Objects.PdfObjectId> _cleanupFiles = new HashSet<Core.Pdf.Objects.PdfObjectId>();

    /// <summary>
    /// What „Bereinigen“ removes: the items ticked; with rows of modules (an e-invoice; decision (4) of 15.11.2026) also which
    /// embedded files stay — a module's files unless its row is ticked; its row alone removes only its files — and whether the metadata
    /// describing them stay (they do while the files stay).
    /// </summary>
    public (CleanupItems Items, IReadOnlySet<Core.Pdf.Objects.PdfObjectId>? Kept, bool MetadataStays) CleanupChoice
    {
        get
        {
            var items = CleanupChosen;
            var modules = CleanupRows.Where(r => r.Module is not null).ToList();
            if (modules.Count == 0) return (items, null, false);
            var stay = modules.Where(r => !r.IsChecked).SelectMany(r => r.Module!.Files).ToHashSet();
            var go = modules.Where(r => r.IsChecked).SelectMany(r => r.Module!.Files).ToHashSet();
            if (items.HasFlag(CleanupItems.Attachments)) return stay.Count == 0 ? (items, null, false) : (items, stay, true);
            // no attachment removed: the module's files stay, and with „Metadaten“ ticked their data in the metadata too
            if (go.Count == 0) return (items, null, stay.Count > 0);
            // only the module's files: every other embedded file stays
            var others = _cleanupFiles.Where(f => !go.Contains(f)).ToHashSet();
            return (items | CleanupItems.Attachments, others.Count > 0 ? others : null, false);
        }
    }

    /// <summary>Opens the area: checks the document in the background and lists what was found, the recommendation ticked.</summary>
    /// <param name="modules">Rows of modules (an e-invoice): each its own row, not ticked; „Eingebettete Dateien“ counts the other attachments.</param>
    public async Task OpenCleanupAsync(IReadOnlyList<Func<PdfDocument, Modules.ModuleCleanupFinding?>>? modules = null)
    {
        ClearCleanupPlaces();
        _cleanupKept.Clear();
        CleanupRows.Clear();
        if (IsAccessibilityOpen) CloseAccessibility();
        IsCleanupOpen = true;
        CleanupSearching = true;
        OnCleanupChanged();
        try
        {
            var pages = History.Pages.ToArray();
            var (found, moduleRows, files) = await WorkAsync(() => Task.Run(() =>
            {
                var doc = Document;
                var inspected = DocumentCleanup.Inspect(doc, pages);   // also reads page content
                var rows = (modules ?? []).Select(m => m(doc)).OfType<Modules.ModuleCleanupFinding>().ToList();
                return (inspected, rows, rows.Count > 0 ? DocumentCleanup.EmbeddedFileStreams(doc, pages) : []);
            }));
            _cleanupFiles = files;
            var theirs = moduleRows.Sum(r => r.Attachments);
            foreach (var item in CleanupLabels.Order)
            {
                if (found.FirstOrDefault(f => f.Item == item) is not { } finding) continue;
                // the attachments a module shows in its own row are not counted twice (the detail on /AF would count them)
                if (item == CleanupItems.Attachments && theirs > 0)
                {
                    if (finding.Count <= theirs) continue;
                    finding = finding with { Count = finding.Count - theirs, Detail = "" };
                }
                CleanupRows.Add(new CleanupRow(finding, CleanupLabels.Recommended.HasFlag(item)));
                if (item == CleanupItems.Attachments) foreach (var row in moduleRows) CleanupRows.Add(new CleanupRow(row));
            }
            // only a module's attachments: its rows where „Eingebettete Dateien“ would stand
            if (!CleanupRows.Any(r => r.Module is not null)) foreach (var row in moduleRows) CleanupRows.Add(new CleanupRow(row));
        }
        finally
        {
            CleanupSearching = false;
            OnCleanupChanged();
        }
    }

    /// <summary>Closes the area; the frames and the places kept go.</summary>
    public void CloseCleanup()
    {
        IsCleanupOpen = false;
        _cleanupKept.Clear();
        CleanupRows.Clear();
        ClearCleanupPlaces();
        OnCleanupChanged();
    }

    /// <summary>A page button: the page with the places of the finding, and what was framed under the row.</summary>
    [RelayCommand]
    private async Task ShowCleanupPage(CleanupPageLink link)
    {
        var framed = await ShowCleanupPlacesAsync(link.Row.Item, link.Page);
        link.Row.Result = framed switch
        {
            < 0 => Tr.T("Seite {0} ließ sich nicht prüfen.", link.Page + 1),
            0 => Tr.T("Seite {0}: Nichts davon liegt sichtbar auf der Seite (neben der Seite, weggeschnitten oder nie gezeichnet).", link.Page + 1),
            1 => Tr.T("Seite {0}: 1 Stelle rot umrandet — „x“ am Rahmen: nicht löschen.", link.Page + 1),
            _ => Tr.T("Seite {0}: {1} Stellen rot umrandet — „x“ am Rahmen: nicht löschen.", link.Page + 1, framed),
        };
    }

    /// <summary>Whether a place shown stays (its „x“ was clicked).</summary>
    public bool IsCleanupKept(int page, CleanupItems item, CleanupPlace place) =>
        _cleanupKept.Any(k => k.Page == page && k.Item == item && k.Area == place.Bounds);

    /// <summary>The „x“ on a frame: the place stays — or, clicked again, goes with its item after all.</summary>
    public void ToggleCleanupKept(int page, CleanupItems item, CleanupPlace place)
    {
        var index = _cleanupKept.FindIndex(k => k.Page == page && k.Item == item && k.Area == place.Bounds);
        if (index >= 0) _cleanupKept.RemoveAt(index);
        else _cleanupKept.Add(new CleanupKeep(item, page, place.Bounds));
        OnCleanupChanged();
        HiddenSpotsChanged?.Invoke();
    }

    private void OnCleanupChanged()
    {
        OnPropertyChanged(nameof(CleanupKeptText));
        OnPropertyChanged(nameof(CleanupNothing));
    }

    /// <summary>Shows a page of a finding: finds its places in the background, frames them and brings the first into view.</summary>
    /// <param name="item">The item of the finding.</param>
    /// <param name="page">The page (index).</param>
    /// <returns>The number of places framed; 0 if nothing of it lies visibly on the page; -1 if the page could not be read.</returns>
    public async Task<int> ShowCleanupPlacesAsync(CleanupItems item, int page)
    {
        if ((uint)page >= (uint)Pages.Count) return 0;
        _cleanupPlacesWork?.Cancel();
        var cts = _cleanupPlacesWork = new CancellationTokenSource();
        var pageItem = Pages[page].Item;
        ScrollToPage(page);
        List<CleanupPlace>? places;
        try
        {
            places = await Task.Run(() =>
            {
                using var use = Renderer.Use(pageItem.Source);
                return use is null ? [] : DocumentCleanup.Places(pageItem, item, cts.Token);
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // a damaged page: nothing to frame, the page is shown all the same
            Core.Platform.AppLog.Write("Bereinigen: Stellen nicht gefunden", e.ToString());
            places = null;
        }
        if (cts.IsCancellationRequested) return 0;
        CleanupPlaces = (page, item, places ?? []);
        HiddenSpotsChanged?.Invoke();
        if (places is [var first, ..])
        {
            var b = first.Bounds;
            RevealRequested?.Invoke(page, new TextQuad(b.Left, b.Top, b.Right, b.Top, b.Right, b.Bottom, b.Left, b.Bottom));
        }
        return places?.Count ?? -1;
    }

    /// <summary>Takes the frames away (the area or the window of „Dokument bereinigen“ closed).</summary>
    public void ClearCleanupPlaces()
    {
        _cleanupPlacesWork?.Cancel();
        _cleanupPlacesWork = null;
        if (CleanupPlaces is null) return;
        CleanupPlaces = null;
        HiddenSpotsChanged?.Invoke();
    }
}
