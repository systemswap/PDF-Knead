using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.ViewModels;

/// <summary>What the left mouse button does in the document view.</summary>
public enum ViewTool
{
    /// <summary>Selects over text, pans elsewhere.</summary>
    SelectText,

    /// <summary>Always pans.</summary>
    Pan,

    /// <summary>Drag a rectangle: fixed text.</summary>
    InsertText,

    /// <summary>Drag a rectangle: fillable text field.</summary>
    InsertField,

    /// <summary>Click or drag a rectangle: the chosen signature.</summary>
    InsertSignature,

    /// <summary>Click or drag a rectangle: the chosen image.</summary>
    InsertImage,

    /// <summary>Drag or click: the chosen shape; Shift constrains to square, circle or 45°. See KONZEPT.md, section 5.13.</summary>
    InsertShape,

    /// <summary>Drag a rectangle or click: empty signature field.</summary>
    InsertSignatureField,

    /// <summary>Click or drag a rectangle: a QR, Data Matrix or bar code (KONZEPT.md, section 5.19); the content goes in the panel on the right.</summary>
    InsertCode,

    /// <summary>Click: a note (KONZEPT.md, section 5.5a); its text goes in the area „Notizen“ on the right.</summary>
    InsertNote,

    /// <summary>Drag a rectangle: marks an area for redaction; stays active for further areas.</summary>
    InsertRedaction,

    /// <summary>Every text selection becomes a redaction mark on release; stays active until switched off or Esc.</summary>
    RedactText,

    /// <summary>„Verborgenes schwärzen“: hidden text on the pages is framed, a click marks it for redaction; stays on until switched off or Esc. See KONZEPT.md, section 5.18.</summary>
    RedactHidden,

    /// <summary>Form design: select, move and resize fields, properties on the right; clicks do not fill in.</summary>
    DesignForm,

    /// <summary>Insert field (type in <see cref="DocumentViewModel.FieldPreset"/>): click or drag; stays active until a second click on the button or Esc.</summary>
    InsertFormField,

    /// <summary>
    /// „Bearbeiten“: text, images and codes of the page content in one mode (KONZEPT.md, section 5.22). The text blocks of the visible
    /// pages are framed; a click chooses what lies there — own elements first, then a code, a text block, an image (Alt+click what lies
    /// underneath, a page-filling scan by double-click). What is chosen is in <see cref="DocumentViewModel.EditTarget"/>. Stays on until
    /// Esc, „Beenden“ or a second click on the button.
    /// </summary>
    Edit,

    /// <summary>„Messen“: lengths, polylines, areas, angles and radii with the scale of the page (Ablaufplan 7.6; DocumentViewModel.Measure.cs).</summary>
    Measure,
}

/// <summary>A search hit: page and range in that page's text.</summary>
public readonly record struct SearchMatch(int Page, TextRange Range);

/// <summary>Text of the document: selection, copying, search.</summary>
public sealed partial class DocumentViewModel
{
    // ------------------------------------------------------------ Page text

    // Text per page dictionary: moving and rotating pages do not change it (moving foreign annotations does: stamps). With a limit,
    // because otherwise after a search through a thick document every page would keep its text until closing (a book with 352
    // pages: 40 MB); evicted pages are reread when needed (~3 ms per page with a glyph list). Budget: 1/128 of the available
    // memory, clamped to 32 … 64 MB.
    internal static long TextBudget { get; set; } = Math.Clamp(
        (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is > 0 and var available ? available : 8L << 30) / 128, 32L << 20, 64L << 20);

    private readonly object _textGate = new();
    private CancellationTokenSource _textCancel = new();
    private readonly Dictionary<PdfDictionary, LinkedListNode<(PdfDictionary Key, long Stamp, PageText Text)>> _texts = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<(PdfDictionary Key, long Stamp, PageText Text)> _textOrder = new();   // most recently used first
    private readonly Dictionary<PdfDictionary, (long Stamp, Task<PageText> Task)> _textTasks = new(ReferenceEqualityComparer.Instance);
    private long _textBytes;

    /// <summary>Returns the text of a page if it has been read and is still cached.</summary>
    /// <param name="page">Page index (position).</param>
    /// <returns>The text, or null.</returns>
    public PageText? TryGetText(int page)
    {
        if ((uint)page >= (uint)Pages.Count) return null;
        var item = Pages[page].Item;
        lock (_textGate) return Touch(item.Page.Dictionary, ForeignStamp(item));
    }

    /// <summary>Text from the cache, moved to the front (only under _textGate); also null if foreign annotations have moved since.</summary>
    private PageText? Touch(PdfDictionary key, long stamp)
    {
        if (!_texts.TryGetValue(key, out var node) || node.Value.Stamp != stamp) return null;
        if (node != _textOrder.First)
        {
            _textOrder.Remove(node);
            _textOrder.AddFirst(node);
        }
        return node.Value.Text;
    }

    /// <summary>Estimated memory used by the cached page texts, in bytes (for tests).</summary>
    public long TextBytes
    {
        get
        {
            lock (_textGate) return _textBytes;
        }
    }

    /// <summary>
    /// Reads the text of a page (in the background, once per page). Uses a finished display list of
    /// the view if there is one; otherwise the page is read separately, without evicting entries from
    /// the display cache (the search goes through all pages).
    /// </summary>
    /// <param name="page">Page index.</param>
    /// <returns>The page text; empty for an index outside the document.</returns>
    public Task<PageText> GetTextAsync(int page)
    {
        if ((uint)page >= (uint)Pages.Count) return Task.FromResult(PageText.Empty);
        var item = Pages[page].Item;
        var key = item.Page.Dictionary;
        var stamp = ForeignStamp(item);
        lock (_textGate)
        {
            if (Touch(key, stamp) is { } done) return Task.FromResult(done);
            if (_textTasks.TryGetValue(key, out var running) && running.Stamp == stamp) return running.Task;
            var cancel = _textCancel.Token;
            var task = Task.Run(() => BuildText(item, stamp, cancel), cancel);
            _textTasks[key] = (stamp, task);
            return task;
        }
    }

    private PageText BuildText(PageItem item, long stamp, CancellationToken cancel)
    {
        var key = item.Page.Dictionary;
        PageText text;
        using (var use = Renderer.Use(item.Source))
        {
            if (use is null) return PageText.Empty;   // the file is being closed
            try
            {
                // Without a finished display list read only the text (a quarter of the time and memory)
                var (glyphs, unreadable, misread) = Renderer.TryGetDisplayList(item.Id) is { } list
                    ? (list.Text, list.UnreadableGlyphs, list.MisreadGlyphs)
                    : PageInterpreter.BuildTextAndUnreadable(item.Source, item.Page, cancel, withoutOwnElements: true);
                text = PageText.Create(WithForeignText(item, glyphs, cancel), unreadable, misread);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                text = PageText.Empty;   // damaged page: no text, but the search carries on
            }
        }
        lock (_textGate)
        {
            if (_textTasks.TryGetValue(key, out var mine) && mine.Stamp == stamp) _textTasks.Remove(key);
            if (!cancel.IsCancellationRequested && _texts.TryGetValue(key, out var old) && old.Value.Stamp != stamp)
            {
                _textOrder.Remove(old);   // foreign annotations were in a different position
                _texts.Remove(key);
                _textBytes -= old.Value.Text.EstimatedBytes;
            }
            if (!cancel.IsCancellationRequested && !_texts.ContainsKey(key))
            {
                _texts[key] = _textOrder.AddFirst((key, stamp, text));
                _textBytes += text.EstimatedBytes;
                // Over the limit: discard the least recently used pages (the new one stays)
                while (_textBytes > TextBudget && _textOrder.Last is { } oldest && oldest != _textOrder.First)
                {
                    _textOrder.RemoveLast();
                    _texts.Remove(oldest.Value.Key);
                    _textBytes -= oldest.Value.Text.EstimatedBytes;
                }
            }
        }
        return text;
    }

    /// <summary>Position of the foreign annotations as a stamp: when they are moved or deleted, the page text counts as new.</summary>
    private static long ForeignStamp(PageItem item)
    {
        var hash = new HashCode();
        foreach (var element in item.Elements)
            if (element is ForeignAnnotation foreign)
            {
                hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(foreign.Original));
                hash.Add(foreign.Rect);
                hash.Add(foreign.Value);   // filled in: new text
            }
        return hash.ToHashCode();
    }

    /// <summary>Text of the page and of its foreign annotations (they are missing from the page's glyph list; see <see cref="ForeignAnnotations.Text"/>).</summary>
    private static TextGlyph[] WithForeignText(PageItem item, TextGlyph[] page, CancellationToken cancel) =>
        ForeignAnnotations.Text(item, cancel, Math.Max(0, DisplayListBuilder.MaxTextGlyphs - page.Length)) is { Length: > 0 } foreign ? [.. page, .. foreign] : page;

    /// <summary>Discards the texts read so far (after saving they belong to the old file).</summary>
    private void ResetText()
    {
        lock (_textGate)
        {
            // the old source is cancelled, not disposed: running reads still check its token, and without a timer it holds nothing
            _textCancel.Cancel();
            _textCancel = new CancellationTokenSource();
            _texts.Clear();
            _textOrder.Clear();
            _textBytes = 0;
            _textTasks.Clear();
        }
        ResetBlocks();
        ClearSelection();
    }

    /// <summary>Raised when the selection or the search hits have changed; the view redraws them.</summary>
    public event Action? HighlightsChanged;

    // ------------------------------------------------------------ Tool and selection

    [ObservableProperty] public partial ViewTool Tool { get; set; } = ViewTool.SelectText;

    /// <summary>Page of the text selection; -1 = none.</summary>
    public int SelectionPage { get; private set; } = -1;

    public TextRange Selection { get; private set; }

    public bool HasTextSelection => SelectionPage >= 0 && !Selection.IsEmpty;

    /// <summary>Sets the text selection.</summary>
    /// <param name="page">Page index; -1 = no selection.</param>
    /// <param name="range">Range in the page text.</param>
    public void Select(int page, TextRange range)
    {
        if (page == SelectionPage && range == Selection) return;
        var had = HasTextSelection;
        SelectionPage = page;
        Selection = range;
        if (had != HasTextSelection) OnPropertyChanged(nameof(HasTextSelection));
        HighlightsChanged?.Invoke();
    }

    /// <summary>Clears the text selection.</summary>
    public void ClearSelection() => Select(-1, default);

    /// <summary>The selected text (for copying).</summary>
    public string SelectedText => HasTextSelection && TryGetText(SelectionPage) is { } text ? text.GetText(Selection) : "";

    /// <summary>Whether the document permits copying text (permissions of a protected file; the owner may do everything).</summary>
    public bool CopyAllowed => HasAllRights || Document.Security!.Permissions.HasFlag(PdfPermissions.Copy);

    // ------------------------------------------------------------ Search

    [ObservableProperty] public partial bool IsSearchOpen { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; } = "";

    [ObservableProperty] public partial bool MatchCase { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatus))]
    public partial int CurrentMatch { get; set; } = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatus))]
    public partial bool IsSearching { get; set; }

    private readonly List<SearchMatch> _matches = [];
    private readonly Dictionary<int, List<TextRange>> _matchesByPage = [];
    private CancellationTokenSource? _search;
    private int _searchedPages;

    public IReadOnlyList<SearchMatch> Matches => _matches;

    /// <summary>Returns the hits on a page (for highlighting).</summary>
    /// <param name="page">Page index.</param>
    /// <returns>The hits; empty if there are none.</returns>
    public IReadOnlyList<TextRange> MatchesOn(int page) => _matchesByPage.TryGetValue(page, out var list) ? list : [];

    public SearchMatch? Current => CurrentMatch >= 0 && CurrentMatch < _matches.Count ? _matches[CurrentMatch] : null;

    /// <summary>„3 von 17“, „Keine Treffer“; with progress while searching.</summary>
    public string SearchStatus
    {
        get
        {
            if (PageText.NormalizeQuery(SearchText).Length == 0) return "";
            if (_matches.Count == 0)
                return IsSearching ? Tr.T("Suche … Seite {0} von {1}", _searchedPages, PageCount) : Tr.T("Keine Treffer");
            var status = CurrentMatch >= 0 ? Tr.T("{0} von {1}", CurrentMatch + 1, _matches.Count)
                : _matches.Count == 1 ? Tr.T("1 Treffer") : Tr.T("{0} Treffer", _matches.Count);
            return IsSearching ? status + " …" : status;
        }
    }

    /// <summary>Asks the view to show this spot (page, quadrilateral in page space).</summary>
    public event Action<int, TextQuad>? RevealRequested;

    partial void OnSearchTextChanged(string value) => RestartSearch();

    partial void OnMatchCaseChanged(bool value) => RestartSearch();

    partial void OnIsSearchOpenChanged(bool value) => RestartSearch();

    [RelayCommand]
    public void OpenSearch() => IsSearchOpen = true;

    [RelayCommand]
    public void CloseSearch() => IsSearchOpen = false;

    [RelayCommand]
    public void FindNext() => Step(1);

    [RelayCommand]
    public void FindPrevious() => Step(-1);

    private void Step(int direction)
    {
        if (_matches.Count == 0) return;
        var from = CurrentMatch >= 0 ? CurrentMatch : direction > 0 ? -1 : 0;
        CurrentMatch = ((from + direction) % _matches.Count + _matches.Count) % _matches.Count;
        Reveal();
        HighlightsChanged?.Invoke();
    }

    private void Reveal()
    {
        if (Current is not { } match) return;
        if (TryGetText(match.Page) is { } text)
        {
            if (text.Quads(match.Range) is [var quad, ..]) RevealRequested?.Invoke(match.Page, quad);
            return;
        }
        _ = RevealLater(match);   // the page text was evicted: reread it, then show
    }

    private async Task RevealLater(SearchMatch match)
    {
        var text = await GetTextAsync(match.Page);
        if (Current == match && text.Quads(match.Range) is [var quad, ..]) RevealRequested?.Invoke(match.Page, quad);
    }

    private void RestartSearch()
    {
        _search?.Cancel();
        _search = null;
        _matches.Clear();
        _matchesByPage.Clear();
        _searchedPages = 0;
        CurrentMatch = -1;
        HighlightsChanged?.Invoke();
        var query = PageText.NormalizeQuery(SearchText);
        if (!IsSearchOpen || query.Length == 0)
        {
            IsSearching = false;
            OnPropertyChanged(nameof(SearchStatus));
            return;
        }
        var cancel = _search = new CancellationTokenSource();
        IsSearching = true;
        _ = SearchAsync(query, MatchCase, CurrentPage?.Index ?? 0, cancel.Token);
    }

    /// <summary>
    /// Searches all pages in order (continues on the UI thread; reading happens in the
    /// background, with the next pages read ahead). The first hit from the current page on
    /// is shown immediately; the count grows while the search continues.
    /// </summary>
    private async Task SearchAsync(string query, bool matchCase, int startPage, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(150, cancel);   // wait for typing to pause
            var ahead = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
            for (var page = 0; page < PageCount; page++)
            {
                for (var k = 1; k <= ahead && page + k < PageCount; k++) _ = GetTextAsync(page + k);
                var text = await GetTextAsync(page).WaitAsync(cancel);
                cancel.ThrowIfCancellationRequested();
                _searchedPages = page + 1;
                var hits = text.Find(query, matchCase);
                if (hits.Count > 0)
                {
                    _matchesByPage[page] = hits;
                    foreach (var hit in hits) _matches.Add(new SearchMatch(page, hit));
                    if (CurrentMatch < 0 && page >= startPage)
                    {
                        CurrentMatch = _matches.Count - hits.Count;
                        Reveal();
                    }
                    HighlightsChanged?.Invoke();
                }
                OnPropertyChanged(nameof(SearchStatus));
            }
            if (CurrentMatch < 0 && _matches.Count > 0)
            {
                // Found only before the current page: start from the beginning
                CurrentMatch = 0;
                Reveal();
                HighlightsChanged?.Invoke();
            }
            IsSearching = false;
            OnPropertyChanged(nameof(SearchStatus));
        }
        catch (OperationCanceledException)
        {
            // new search or document closed
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Unreadable page or similar: the search ends visibly instead of silently hanging at „Suche …“
            Core.Platform.AppLog.Write("Suche abgebrochen (Fehler)", e.ToString());
            IsSearching = false;
            OnPropertyChanged(nameof(SearchStatus));
        }
    }

    private void DisposeText()
    {
        _search?.Cancel();
        _textCancel.Cancel();
        _blockCancel.Cancel();
    }
}
