namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Page list of a document with undo/redo. Every step is a new, immutable state of the list; undo brings back the previous
/// one. A state costs an array of references (8 bytes per page) plus the changed elements with their appearance (shared when
/// moving, see <see cref="ElementAppearance.Moved"/>).
/// </summary>
/// <remarks>Not thread-safe.</remarks>
public sealed class PageHistory
{
    /// <summary>Number of steps that can be undone.</summary>
    public const int Limit = 200;

    private readonly LinkedList<(string Description, PageItem[] Pages, Document.PdfDocument? Main)> _undo = new();
    private readonly Stack<(string Description, PageItem[] Pages, Document.PdfDocument? Main)> _redo = new();
    private PageItem[] _pages;
    private Document.PdfDocument? _main;
    private PageItem[] _saved;

    /// <summary>Creates the history with an initial state that counts as saved.</summary>
    /// <param name="pages">The initial pages.</param>
    /// <param name="main">The main document (catalog, bookmarks, protection) of this state; see <see cref="Main"/>.</param>
    public PageHistory(IReadOnlyList<PageItem> pages, Document.PdfDocument? main = null)
    {
        _pages = [.. pages];
        _main = main;
        _saved = _pages;
    }

    /// <summary>Current state (do not modify).</summary>
    public IReadOnlyList<PageItem> Pages => _pages;

    /// <summary>
    /// Main document of the current state: changes only with <see cref="ReplaceDocument"/> (redacting in the document), with
    /// <see cref="Reset"/>, and with undo/redo across such a step; null if the history was created without one.
    /// </summary>
    public Document.PdfDocument? Main => _main;

    /// <summary>Number of pages in the current state.</summary>
    public int Count => _pages.Length;

    /// <summary>Whether there is a step to undo.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether there is a step to redo.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>What undo reverts (translated); otherwise null.</summary>
    public string? UndoDescription => _undo.Last?.Value.Description;

    /// <summary>What redo restores (translated); otherwise null.</summary>
    public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    /// <summary>Differs from the state last saved (or opened).</summary>
    public bool IsModified => !ReferenceEquals(_pages, _saved);

    /// <summary>The state last saved (or the initial one); do not modify.</summary>
    public IReadOnlyList<PageItem> SavedPages => _saved;

    /// <summary>Raised when the list, the undo history or the modified state has changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Locked while background work reads from the state (saving, redacting …): editing, undo and redo then change nothing —
    /// otherwise changes would be lost when the work afterwards overwrites them with its older state.
    /// <see cref="ReplaceDocument"/> and <see cref="Reset"/> (the results of such work) still apply.
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>Applies a new state as one step. The same content as before does not count as a step.</summary>
    /// <param name="description">Translated description for undo/redo.</param>
    /// <param name="pages">The new page list; it is taken over, not copied.</param>
    public void Apply(string description, PageItem[] pages) => Apply(description, pages, false);

    /// <summary>
    /// Applies a new state as one step that further changes of <paramref name="continues"/> extend (<see cref="ReplaceElement"/>
    /// with the same description). Used for editing text (see KONZEPT.md, section 5.14): the page without the old text, the new
    /// block and everything typed form one step.
    /// </summary>
    /// <param name="description">Translated description for undo/redo.</param>
    /// <param name="pages">The new page list.</param>
    /// <param name="continues">The element whose later replacements join this step.</param>
    public void Apply(string description, PageItem[] pages, PageElement continues)
    {
        var redo = _redo.ToArray();
        Apply(description, pages, false);
        if (Locked) return;
        _continues = continues;
        _tentative = (_pages, redo);
    }

    // Last step, as long as nothing came after it, with the redo history from before (Retract)
    private (PageItem[] Pages, (string Description, PageItem[] Pages, Document.PdfDocument? Main)[] Redo)? _tentative;

    /// <summary>
    /// Withdraws the step that led to <paramref name="pages"/> — only if it is the last one and nothing came after it: a block
    /// that was clicked but left unchanged disappears without a trace, and the redo history is restored as well. Otherwise undo
    /// might revert another block's step, and the click would have cleared the redo history.
    /// </summary>
    /// <param name="pages">The state created by <see cref="Apply(string, PageItem[], PageElement)"/>.</param>
    /// <returns><see langword="false"/> if something came in between or the history is locked.</returns>
    public bool Retract(IReadOnlyList<PageItem> pages)
    {
        if (Locked || _tentative is not { } t || !ReferenceEquals(t.Pages, pages) || !ReferenceEquals(_pages, pages) || _undo.Last is not { } last) return false;
        _undo.RemoveLast();
        _pages = last.Value.Pages;
        _main = last.Value.Main;
        _redo.Clear();
        for (var i = t.Redo.Length - 1; i >= 0; i--) _redo.Push(t.Redo[i]);
        _tentative = null;
        _continues = null;
        Changed?.Invoke();
        return true;
    }

    private void Apply(string description, PageItem[] pages, bool mergeWithLast)
    {
        if (Locked) return;
        _continues = null;
        _tentative = null;
        if (pages.AsSpan().SequenceEqual(_pages)) return;
        if (!mergeWithLast)
        {
            _undo.AddLast((description, _pages, _main));
            if (_undo.Count > Limit) _undo.RemoveFirst();
        }
        _redo.Clear();
        _pages = pages;
        Changed?.Invoke();
    }

    // Element (or page entry) whose last step a further one may extend (arrow keys)
    private object? _continues;

    /// <summary>Reverts the last step; does nothing while <see cref="Locked"/> or without a step.</summary>
    public void Undo()
    {
        if (Locked) return;
        _continues = null;
        _tentative = null;
        if (_undo.Last is not { } last) return;
        _undo.RemoveLast();
        _redo.Push((last.Value.Description, _pages, _main));
        _pages = last.Value.Pages;
        _main = last.Value.Main;
        Changed?.Invoke();
    }

    /// <summary>Restores the last undone step; does nothing while <see cref="Locked"/> or without a step.</summary>
    public void Redo()
    {
        if (Locked) return;
        _continues = null;
        _tentative = null;
        if (_redo.Count == 0) return;
        var (description, pages, main) = _redo.Pop();
        _undo.AddLast((description, _pages, _main));
        _pages = pages;
        _main = main;
        Changed?.Invoke();
    }

    /// <summary>After saving: new pages (from the written file); the history starts afresh and the state counts as saved.</summary>
    /// <param name="pages">The pages of the written file.</param>
    /// <param name="main">The new main document; null keeps the current one.</param>
    public void Reset(IReadOnlyList<PageItem> pages, Document.PdfDocument? main = null)
    {
        _continues = null;
        _tentative = null;
        _main = main ?? _main;
        _undo.Clear();
        _redo.Clear();
        _pages = [.. pages];
        _saved = _pages;
        Changed?.Invoke();
    }

    /// <summary>Discards the oldest undo step (memory budget).</summary>
    /// <returns><see langword="false"/> if there is none.</returns>
    public bool DropOldest()
    {
        if (_undo.Count == 0) return false;
        _undo.RemoveFirst();
        return true;
    }

    /// <summary>Determines whether <paramref name="main"/> is the main document of a state (current, undo, redo); fast, because only the states are checked, not the pages.</summary>
    /// <param name="main">The document.</param>
    /// <returns><see langword="true"/> if a state uses it as its main document.</returns>
    public bool UsesMain(Document.PdfDocument main) =>
        ReferenceEquals(_main, main) || _undo.Any(s => ReferenceEquals(s.Main, main)) || _redo.Any(s => ReferenceEquals(s.Main, main));

    /// <summary>Gets all source documents the history still needs (current state, undo, redo).</summary>
    /// <returns>The documents, compared by reference.</returns>
    public HashSet<Document.PdfDocument> Sources()
    {
        var set = new HashSet<Document.PdfDocument>(ReferenceEqualityComparer.Instance);
        // Edited text draws with fonts of its file, so that file stays needed as long as a state contains the text
        void Add(PageItem item)
        {
            set.Add(item.Source);
            foreach (var element in item.Elements)
                if (element is RichTextElement rich)
                    foreach (var span in rich.Content.Spans)
                        if (span.Style.Original is { } original) set.Add(original.Document);
        }
        foreach (var item in _pages) Add(item);
        if (_main is not null) set.Add(_main);
        foreach (var (_, pages, main) in _undo.Concat(_redo))
        {
            if (main is not null) set.Add(main);
            foreach (var item in pages) Add(item);
        }
        return set;
    }

    // ------------------------------------------------------------ Editing

    /// <summary>Rotates pages as one step.</summary>
    /// <param name="indices">Page indices; invalid ones are ignored.</param>
    /// <param name="degrees">A multiple of 90, clockwise; negative values rotate to the left.</param>
    public void Rotate(IEnumerable<int> indices, int degrees)
    {
        var pages = (PageItem[])_pages.Clone();
        foreach (var i in Valid(indices)) pages[i] = pages[i].Rotated(degrees);
        Apply(degrees < 0 ? Tr.T("Nach links drehen") : Tr.T("Nach rechts drehen"), pages);
    }

    /// <summary>Deletes pages as one step; deleting all pages is not possible (a PDF needs at least one page).</summary>
    /// <param name="indices">Page indices; invalid ones are ignored.</param>
    /// <returns><see langword="false"/> if nothing or everything would be deleted.</returns>
    public bool Delete(IEnumerable<int> indices)
    {
        var remove = Valid(indices);
        if (remove.Count == 0 || remove.Count >= _pages.Length) return false;
        var pages = new PageItem[_pages.Length - remove.Count];
        var k = 0;
        for (var i = 0; i < _pages.Length; i++)
            if (!remove.Contains(i)) pages[k++] = _pages[i];
        Apply(remove.Count == 1 ? Tr.T("Seite löschen") : Tr.T("Seiten löschen"), pages);
        return true;
    }

    /// <summary>
    /// Moves pages: the selected ones (in their order) before the page currently at position <paramref name="insertBefore"/>
    /// (<see cref="Count"/> = to the end).
    /// </summary>
    /// <param name="indices">Page indices; invalid ones are ignored.</param>
    /// <param name="insertBefore">Target position in the current list.</param>
    /// <returns>New position of the first moved page; -1 if there was nothing to do.</returns>
    public int Move(IEnumerable<int> indices, int insertBefore)
    {
        var moving = Valid(indices);
        if (moving.Count == 0) return -1;
        insertBefore = Math.Clamp(insertBefore, 0, _pages.Length);
        var rest = new List<PageItem>(_pages.Length);
        var target = 0;
        for (var i = 0; i < _pages.Length; i++)
        {
            if (i == insertBefore) target = rest.Count;
            if (!moving.Contains(i)) rest.Add(_pages[i]);
        }
        if (insertBefore == _pages.Length) target = rest.Count;
        rest.InsertRange(target, moving.Order().Select(i => _pages[i]));
        Apply(moving.Count == 1 ? Tr.T("Seite verschieben") : Tr.T("Seiten verschieben"), [.. rest]);
        return target;
    }

    /// <summary>Inserts pages at position <paramref name="at"/> as one step.</summary>
    /// <param name="at">Position; clamped to the list.</param>
    /// <param name="items">The pages to insert.</param>
    /// <param name="description">Translated description for undo/redo.</param>
    public void Insert(int at, IReadOnlyList<PageItem> items, string description)
    {
        if (items.Count == 0) return;
        at = Math.Clamp(at, 0, _pages.Length);
        PageItem[] pages = [.. _pages.AsSpan(0, at), .. items, .. _pages.AsSpan(at)];
        Apply(description, pages);
    }

    /// <summary>
    /// Replaces the main document and its pages as one step (redacting in the document): undo brings back the old document with
    /// its pages. While the old one is in the history, it stays open (<see cref="Sources"/>). Applies even while
    /// <see cref="Locked"/>.
    /// </summary>
    /// <param name="description">Translated description for undo/redo.</param>
    /// <param name="main">The new main document.</param>
    /// <param name="pages">Its pages.</param>
    public void ReplaceDocument(string description, Document.PdfDocument main, PageItem[] pages)
    {
        _continues = null;
        _tentative = null;
        _undo.AddLast((description, _pages, _main));
        if (_undo.Count > Limit) _undo.RemoveFirst();
        _redo.Clear();
        _pages = pages;
        _main = main;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------ Elements

    /// <summary>Replaces the elements of a page as one step.</summary>
    /// <param name="index">Page index; invalid indices are ignored.</param>
    /// <param name="elements">The new elements.</param>
    /// <param name="description">Translated description for undo/redo.</param>
    public void SetElements(int index, IReadOnlyList<PageElement> elements, string description)
    {
        if (index < 0 || index >= _pages.Length) return;
        var pages = (PageItem[])_pages.Clone();
        pages[index] = pages[index].WithElements(elements);
        Apply(description, pages);
    }

    /// <summary>Adds elements on several pages in one step (such as all hits of "search and redact").</summary>
    /// <param name="byPage">Elements to add per page index.</param>
    /// <param name="description">Translated description for undo/redo.</param>
    public void AddElements(IReadOnlyDictionary<int, IReadOnlyList<PageElement>> byPage, string description)
    {
        var pages = (PageItem[])_pages.Clone();
        foreach (var (index, elements) in byPage)
            if (index >= 0 && index < pages.Length && elements.Count > 0)
                pages[index] = pages[index].WithElements([.. pages[index].Elements, .. elements]);
        Apply(description, pages);
    }

    /// <summary>Adds one element to a page as one step.</summary>
    /// <param name="index">Page index; invalid indices are ignored.</param>
    /// <param name="element">The element, placed on top.</param>
    /// <param name="description">Translated description for undo/redo.</param>
    public void AddElement(int index, PageElement element, string description)
    {
        if (index < 0 || index >= _pages.Length) return;
        SetElements(index, [.. _pages[index].Elements, element], description);
    }

    /// <summary>
    /// Replaces an element with a changed one. With <paramref name="continueLast"/> the change becomes part of the last step if
    /// that step produced exactly this element with the same description (several arrow key presses in a row = one undo).
    /// </summary>
    /// <param name="index">Page index.</param>
    /// <param name="old">The element on the page.</param>
    /// <param name="updated">The replacement.</param>
    /// <param name="description">Translated description for undo/redo.</param>
    /// <param name="continueLast">Whether the change may join the last step.</param>
    /// <returns><see langword="false"/> if the element is not (or no longer) on the page.</returns>
    public bool ReplaceElement(int index, PageElement old, PageElement updated, string description, bool continueLast = false)
    {
        if (index < 0 || index >= _pages.Length) return false;
        var elements = _pages[index].Elements.ToArray();
        var at = Array.IndexOf(elements, old);
        if (at < 0) return false;
        elements[at] = updated;
        var pages = (PageItem[])_pages.Clone();
        pages[index] = pages[index].WithElements(elements);
        var merge = continueLast && ReferenceEquals(_continues, old) && _undo.Last?.Value.Description == description;
        Apply(description, pages, merge);
        if (continueLast) _continues = updated;
        return true;
    }

    /// <summary>
    /// Replaces a page entry (moved images of the page content, see KONZEPT.md, section 5.20). With <paramref name="continueLast"/> the
    /// change becomes part of the last step if that step produced exactly <paramref name="old"/> with the same description (several arrow
    /// key presses in a row = one undo).
    /// </summary>
    /// <param name="index">Page index.</param>
    /// <param name="old">The entry now at <paramref name="index"/>.</param>
    /// <param name="updated">The replacement.</param>
    /// <param name="description">Translated description for undo/redo.</param>
    /// <param name="continueLast">Whether the change may join the last step.</param>
    /// <returns><see langword="false"/> if <paramref name="old"/> is no longer at <paramref name="index"/>.</returns>
    public bool ReplacePage(int index, PageItem old, PageItem updated, string description, bool continueLast = false)
    {
        if (index < 0 || index >= _pages.Length || !ReferenceEquals(_pages[index], old)) return false;
        var pages = (PageItem[])_pages.Clone();
        pages[index] = updated;
        var merge = continueLast && ReferenceEquals(_continues, old) && _undo.Last?.Value.Description == description;
        Apply(description, pages, merge);
        if (continueLast) _continues = updated;
        return true;
    }

    /// <summary>Replaces several page entries in one step (a scale set for all pages, Ablaufplan 7.6 e3).</summary>
    /// <param name="changes">Page index, the entry now there and its replacement.</param>
    /// <param name="description">Translated description for undo/redo.</param>
    /// <returns><see langword="false"/> if one of the old entries is no longer at its place (then nothing changes).</returns>
    public bool ReplacePages(IReadOnlyList<(int Index, PageItem Old, PageItem Updated)> changes, string description)
    {
        if (changes.Count == 0) return false;
        var pages = (PageItem[])_pages.Clone();
        foreach (var (index, old, updated) in changes)
        {
            if (index < 0 || index >= pages.Length || !ReferenceEquals(pages[index], old)) return false;
            pages[index] = updated;
        }
        Apply(description, pages, false);
        return true;
    }

    /// <summary>Replaces elements on several pages in one step (a form field with widgets on several pages).</summary>
    /// <param name="changes">Page index, old element and replacement per change.</param>
    /// <param name="description">Translated description for undo/redo.</param>
    /// <returns><see langword="false"/> if none of the old elements is still there.</returns>
    public bool ReplaceElements(IReadOnlyList<(int Page, PageElement Old, PageElement Updated)> changes, string description)
    {
        var pages = (PageItem[])_pages.Clone();
        var changed = false;
        foreach (var group in changes.GroupBy(c => c.Page))
        {
            if (group.Key < 0 || group.Key >= pages.Length) continue;
            var elements = pages[group.Key].Elements.ToArray();
            foreach (var (_, old, updated) in group)
                if (Array.IndexOf(elements, old) is var at and >= 0)
                {
                    elements[at] = updated;
                    changed = true;
                }
            pages[group.Key] = pages[group.Key].WithElements(elements);
        }
        if (!changed) return false;
        Apply(description, pages);
        return true;
    }

    /// <summary>Removes elements from a page as one step.</summary>
    /// <param name="index">Page index.</param>
    /// <param name="remove">The elements to remove.</param>
    /// <param name="description">Translated description for undo/redo.</param>
    /// <returns><see langword="false"/> if none of them is on the page.</returns>
    public bool RemoveElements(int index, IReadOnlyCollection<PageElement> remove, string description)
    {
        if (index < 0 || index >= _pages.Length) return false;
        var elements = _pages[index].Elements.Where(e => !remove.Contains(e)).ToArray();
        if (elements.Length == _pages[index].Elements.Count) return false;
        SetElements(index, elements, description);
        return true;
    }

    private HashSet<int> Valid(IEnumerable<int> indices)
    {
        var set = new HashSet<int>();
        foreach (var i in indices)
            if (i >= 0 && i < _pages.Length) set.Add(i);
        return set;
    }
}
