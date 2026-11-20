using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Rendering;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>A file in the upper part of „Seiten zusammenführen“ (one tab).</summary>
public sealed partial class MergeSourceViewModel : ObservableObject
{
    internal MergeSourceViewModel(string name, PdfDocument document, bool openedWithPassword, IReadOnlyList<PageItem> items, bool isOpenDocument)
    {
        Name = name;
        Document = document;
        OpenedWithPassword = openedWithPassword;
        IsOpenDocument = isOpenDocument;
        Pages = [.. items.Select((item, i) => new PageViewModel(item, i))];
    }

    /// <summary>File name shown on the tab.</summary>
    public string Name { get; }

    /// <summary>The file (for the open document: its main document; its pages may come from further files).</summary>
    public PdfDocument Document { get; }

    /// <summary>Whether it was opened with a password.</summary>
    public bool OpenedWithPassword { get; }

    /// <summary>The document open in the main window: its pages belong to it, the window does not close it.</summary>
    public bool IsOpenDocument { get; }

    /// <summary>Its pages as tiles.</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>The file name: what screen readers announce for the tab.</summary>
    /// <returns><see cref="Name"/>.</returns>
    public override string ToString() => Name;
}

/// <summary>What „Danach nicht mehr da“ says about one file.</summary>
/// <param name="File">File name.</param>
/// <param name="Lines">What it no longer has afterwards.</param>
public sealed record MergeNoteGroup(string File, IReadOnlyList<string> Lines);

/// <summary>The result of the window for the main window.</summary>
/// <param name="Pages">The pages in their order, each entry once.</param>
/// <param name="Sources">Files loaded in the window that pages come from; the receiving document takes ownership of them.</param>
/// <param name="WithoutBookmarks">Of these, the files whose bookmarks are left out.</param>
public sealed record MergeResult(IReadOnlyList<PageItem> Pages, IReadOnlyList<PdfDocument> Sources, IReadOnlyList<PdfDocument> WithoutBookmarks);

/// <summary>
/// „Seiten zusammenführen“ (KONZEPT.md, section 5.2): files as tabs at the top, the result at the bottom — it starts with the pages of the
/// open document (empty without one). Pages are taken from the tabs into the result (dragging, „Am Ende anfügen“, „Hinter der Auswahl
/// einfügen“), moved, removed and turned there, with undo. On the right „Danach nicht mehr da“: what the files lose. Source files are only
/// read; a page taken twice becomes a copy of its own.
/// </summary>
public sealed partial class MergeViewModel : ObservableObject
{
    private static readonly Color UsedMark = Color.FromRgb(0x3D, 0x7E, 0xFF);

    private readonly DocumentViewModel? _target;
    private readonly List<IReadOnlyList<PageItem>> _undo = [], _redo = [];
    private readonly List<PdfDocument> _loaded = [];   // files the window opened (closed by it unless handed over)
    private readonly HashSet<PdfDocument> _handedOver = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfDocument, bool> _withPassword = new(ReferenceEqualityComparer.Instance);
    // Pages of the open document in the result and their copies: index in the document and the turn added here (for a new file they
    // are taken from a copy of the document, KONZEPT.md, section 5.2 „Nur Kopien“)
    private readonly Dictionary<PageItem, (int Index, int Turn)> _origin = new(ReferenceEqualityComparer.Instance);

    /// <param name="target">The document open in the main window; null = none (the result starts empty).</param>
    /// <param name="selectCurrentPage">„Aus Datei“: the current page is selected in the result, so that „Hinter der Auswahl einfügen“ goes after it.</param>
    public MergeViewModel(DocumentViewModel? target, bool selectCurrentPage = false)
    {
        _target = target;
        Renderer = new DocumentRenderer
        {
            EnhanceThinLines = UserSettings.Current.EnhanceThinLines,
            OverprintPreview = UserSettings.Current.OverprintPreview,
        };
        IReadOnlyList<PageItem> start = [];
        if (target is not null)
        {
            start = [.. target.History.Pages];
            for (var i = 0; i < start.Count; i++) _origin[start[i]] = (i, 0);
            var open = new MergeSourceViewModel(target.FileName, target.Document, false, start, isOpenDocument: true);
            Sources.Add(open);
            SelectedSource = open;
        }
        Result = Tiles(start);
        if (selectCurrentPage && target?.CurrentPage?.Index is int current && current < Result.Count) Result[current].IsSelected = true;
        Refresh();
    }

    /// <summary>Draws the tiles of all tabs and of the result.</summary>
    public DocumentRenderer Renderer { get; }

    /// <summary>The files at the top.</summary>
    public ObservableCollection<MergeSourceViewModel> Sources { get; } = [];

    /// <summary>The tab shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    [NotifyCanExecuteChangedFor(nameof(AppendCommand), nameof(InsertAfterSelectionCommand))]
    public partial MergeSourceViewModel? SelectedSource { get; set; }

    /// <summary>The pages of the result.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText), nameof(HasResult))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial IReadOnlyList<PageViewModel> Result { get; private set; }

    /// <summary>„Danach nicht mehr da“ per file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial IReadOnlyList<MergeNoteGroup> Notes { get; private set; } = [];

    /// <summary>Carry the bookmarks of the files over (on by default).</summary>
    [ObservableProperty] public partial bool KeepBookmarks { get; set; } = true;

    /// <summary>Problems while loading (wrong file, no permission …); empty if none.</summary>
    [ObservableProperty] public partial string Message { get; set; } = "";

    /// <summary>Whether a document is open in the main window (then the result can go into it).</summary>
    public bool HasTarget => _target is not null;

    /// <summary>Name of the open document, or empty.</summary>
    public string TargetName => _target?.FileName ?? "";

    public bool HasResult => Result.Count > 0;

    public bool HasNotes => Notes.Count > 0;

    /// <summary>„Ergebnis – 14 Seiten“.</summary>
    public string ResultText => Result.Count == 1 ? Tr.T("Ergebnis – 1 Seite") : Tr.T("Ergebnis – {0} Seiten", Result.Count);

    /// <summary>What is selected at the top, or how to start.</summary>
    public string SourceText
    {
        get
        {
            if (SelectedSource is not { } source) return Tr.T("Dateien laden: „PDF laden …“ oder ins Fenster ziehen");
            var count = source.Pages.Count(p => p.IsSelected);
            return count == 0 ? Tr.T("Seiten wählen (Strg/Umschalt+Klick, Rahmen ziehen) und nach unten ziehen")
                : count == 1 ? Tr.T("1 Seite gewählt") : Tr.T("{0} Seiten gewählt", count);
        }
    }

    // ------------------------------------------------------------ Delegates (set by the window)

    /// <summary>Asks for the open password of a file (file name, wrong before); null = cancelled.</summary>
    public Func<string, bool, Task<string?>>? AskPassword { get; set; }

    /// <summary>Asks for the permissions password of a file that does not allow taking its pages (file name, wrong before); null = cancelled.</summary>
    public Func<string, bool, Task<string?>>? AskOwnerPassword { get; set; }

    /// <summary>Closing a tab whose pages are in the result: true = remove them from it too, false = keep them, null = cancel.</summary>
    public Func<string, Task<bool?>>? AskRemoveOnClose { get; set; }

    /// <summary>Raised when the selection or the pages of a grid have changed (the grids redraw).</summary>
    public event Action? Changed;

    // ------------------------------------------------------------ Loading

    /// <summary>Loads files as tabs (password and permissions asked as needed); the last one becomes the shown tab.</summary>
    /// <param name="paths">The files.</param>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var problems = new List<string>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            PdfDocument? doc;
            string? password;
            try
            {
                (doc, password) = await OpenAsync(path, AskPassword);
            }
            catch (Exception ex) when (Failure.Handled(ex))
            {
                problems.Add(Tr.T("{0}: {1}", name, Failure.Text(ex)));
                continue;
            }
            if (doc is null) continue;   // password prompt cancelled
            if (doc.PageCount == 0)
            {
                problems.Add(Tr.T("{0}: keine Seiten", name));
                doc.Dispose();
                continue;
            }
            // Copying not allowed: only with the permissions password (otherwise the restriction could be lifted through another document)
            var wrong = false;
            while (!MainViewModel.MayTakePages(doc))
            {
                var owner = AskOwnerPassword is null ? null : await AskOwnerPassword(name, wrong);
                if (owner is null)
                {
                    problems.Add(Tr.T("{0}: Die Datei erlaubt kein Übernehmen ihrer Seiten (Rechte der Datei).", name));
                    doc.Dispose();
                    doc = null;
                    break;
                }
                try
                {
                    var reopened = await Task.Run(() => PdfDocument.Open(path, owner));
                    doc.Dispose();
                    doc = reopened;
                    wrong = !MainViewModel.MayTakePages(doc);
                }
                catch (PdfPasswordException)
                {
                    wrong = true;
                }
            }
            if (doc is null) continue;
            _loaded.Add(doc);
            _withPassword[doc] = password is { Length: > 0 };
            var source = new MergeSourceViewModel(name, doc, password is { Length: > 0 }, PageItem.From(doc), isOpenDocument: false);
            Sources.Add(source);
            SelectedSource = source;
        }
        Message = string.Join(Environment.NewLine, problems);
        Refresh();
    }

    /// <summary>Opens a file; if it is protected, asks until the password fits or the user cancels (then null).</summary>
    private static async Task<(PdfDocument? Document, string? Password)> OpenAsync(string path, Func<string, bool, Task<string?>>? ask)
    {
        string? password = null;
        while (true)
        {
            try
            {
                return (await Task.Run(() => PdfDocument.Open(path, password)), password);
            }
            catch (PdfPasswordException ex)
            {
                if (ask is null) throw;
                password = await ask(Path.GetFileName(path), ex.WrongPassword);
                if (password is null) return (null, null);
            }
        }
    }

    /// <summary>
    /// Closes a tab. If pages of its file are in the result, asks whether they go too; kept, the file stays open in the background
    /// until the window is closed.
    /// </summary>
    /// <param name="source">The tab.</param>
    [RelayCommand]
    private async Task CloseSource(MergeSourceViewModel? source)
    {
        if (source is null) return;
        var used = Items.Where(item => Belongs(item, source)).ToList();
        if (used.Count > 0)
        {
            if (AskRemoveOnClose is null || await AskRemoveOnClose(source.Name) is not { } remove) return;
            if (remove) Change([.. Items.Where(item => !Belongs(item, source))]);
        }
        var index = Sources.IndexOf(source);
        Sources.Remove(source);
        if (ReferenceEquals(SelectedSource, source) || SelectedSource is null)
            SelectedSource = Sources.Count == 0 ? null : Sources[Math.Clamp(index, 0, Sources.Count - 1)];
        Refresh();
        if (!source.IsOpenDocument && !Items.Any(item => ReferenceEquals(item.Source, source.Document))) await ReleaseFile(source.Document);
    }

    /// <summary>Files loaded here that are still open (closed tabs whose pages stay in the result count too).</summary>
    internal int OpenFiles => _loaded.Count;

    /// <summary>
    /// Closes a file whose tab was closed and none of whose pages is in the result: its pages also leave the undo history (bringing
    /// them back would need the file), then it is closed as soon as nothing draws from it any more — a closed tab frees its file at
    /// once, not only with the window.
    /// </summary>
    private async Task ReleaseFile(PdfDocument file)
    {
        if (!_loaded.Remove(file)) return;
        _withPassword.Remove(file);
        for (var i = 0; i < _undo.Count; i++) _undo[i] = [.. _undo[i].Where(item => !ReferenceEquals(item.Source, file))];
        for (var i = 0; i < _redo.Count; i++) _redo[i] = [.. _redo[i].Where(item => !ReferenceEquals(item.Source, file))];
        await Renderer.WaitUntilUnused([file]);
        file.Dispose();
    }

    // ------------------------------------------------------------ Taking pages and editing the result

    private IReadOnlyList<PageItem> Items => [.. Result.Select(p => p.Item)];

    /// <summary>Takes the pages selected in the shown tab into the result before index <paramref name="at"/>.</summary>
    /// <param name="at">Position in the result (clamped).</param>
    public void TakeSelected(int at)
    {
        if (SelectedSource is not { } source) return;
        var chosen = source.Pages.Where(p => p.IsSelected).Select(p => p.Item).ToList();
        if (chosen.Count == 0) return;
        var items = Items.ToList();
        var present = new HashSet<PageItem>(items, ReferenceEqualityComparer.Instance);
        var added = new List<PageItem>();
        foreach (var item in chosen)
        {
            // Taken twice (or already in the result): a copy of its own, so every entry is unique
            var page = present.Add(item) ? item : CopyOf(item, 0);
            added.Add(page);
        }
        at = Math.Clamp(at, 0, items.Count);
        items.InsertRange(at, added);
        Change(items, select: added);
    }

    private bool HasSourceSelection => SelectedSource?.Pages.Any(p => p.IsSelected) == true;

    /// <summary>„Am Ende anfügen“.</summary>
    [RelayCommand(CanExecute = nameof(HasSourceSelection))]
    private void Append() => TakeSelected(Result.Count);

    /// <summary>„Hinter der Auswahl einfügen“: behind the last page selected in the result (at the end if none is).</summary>
    [RelayCommand(CanExecute = nameof(HasSourceSelection))]
    private void InsertAfterSelection()
    {
        var last = -1;
        for (var i = 0; i < Result.Count; i++)
            if (Result[i].IsSelected) last = i;
        TakeSelected(last < 0 ? Result.Count : last + 1);
    }

    /// <summary>Moves the pages selected in the result before index <paramref name="insertBefore"/> (dragging within the result).</summary>
    /// <param name="insertBefore">Position (0 … count).</param>
    public void MoveSelected(int insertBefore)
    {
        var selected = Result.Where(p => p.IsSelected).Select(p => p.Item).ToList();
        if (selected.Count == 0) return;
        var before = Result.Take(Math.Clamp(insertBefore, 0, Result.Count)).Count(p => !p.IsSelected);
        var rest = Result.Where(p => !p.IsSelected).Select(p => p.Item).ToList();
        rest.InsertRange(before, selected);
        if (!rest.SequenceEqual(Items)) Change(rest, select: selected);
    }

    private bool HasResultSelection => Result.Any(p => p.IsSelected);

    /// <summary>Removes the pages selected in the result (Del).</summary>
    [RelayCommand(CanExecute = nameof(HasResultSelection))]
    private void RemoveSelected() => Change([.. Result.Where(p => !p.IsSelected).Select(p => p.Item)]);

    /// <summary>Turns the pages selected in the result by 90° clockwise.</summary>
    [RelayCommand(CanExecute = nameof(HasResultSelection))]
    private void RotateRight() => Rotate(90);

    /// <summary>Turns the pages selected in the result by 90° anticlockwise.</summary>
    [RelayCommand(CanExecute = nameof(HasResultSelection))]
    private void RotateLeft() => Rotate(-90);

    private void Rotate(int degrees)
    {
        var turned = new List<PageItem>();
        var items = Result.Select(p =>
        {
            if (!p.IsSelected) return p.Item;
            var copy = CopyOf(p.Item, degrees);
            turned.Add(copy);
            return copy;
        }).ToList();
        Change(items, select: turned);
    }

    /// <summary>A new entry for the page (new identifier), turned by <paramref name="degrees"/>; keeps its origin in the open document.</summary>
    private PageItem CopyOf(PageItem item, int degrees)
    {
        var copy = item.Rotated(degrees);
        if (_origin.TryGetValue(item, out var origin)) _origin[copy] = (origin.Index, ((origin.Turn + degrees) % 360 + 360) % 360);
        return copy;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        _redo.Add(Items);
        SetResult(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    private bool CanUndo => _undo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        _undo.Add(Items);
        SetResult(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
    }

    private bool CanRedo => _redo.Count > 0;

    /// <summary>A change of the result as one undo step; <paramref name="select"/> are selected afterwards (otherwise the selection stays).</summary>
    private void Change(IReadOnlyList<PageItem> items, IReadOnlyList<PageItem>? select = null)
    {
        _undo.Add(Items);
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        SetResult(items, select);
    }

    private void SetResult(IReadOnlyList<PageItem> items, IReadOnlyList<PageItem>? select = null)
    {
        var selected = select is not null
            ? new HashSet<PageItem>(select, ReferenceEqualityComparer.Instance)
            : new HashSet<PageItem>(Result.Where(p => p.IsSelected).Select(p => p.Item), ReferenceEqualityComparer.Instance);
        Result = Tiles(items);
        foreach (var page in Result) page.IsSelected = selected.Contains(page.Item);
        Refresh();
    }

    private static IReadOnlyList<PageViewModel> Tiles(IReadOnlyList<PageItem> items) => [.. items.Select((item, i) => new PageViewModel(item, i))];

    /// <summary>After a change: tiles to draw, marks of the pages used, notes, commands.</summary>
    public void Refresh()
    {
        var all = new Dictionary<int, RenderPage>();
        foreach (var source in Sources)
            foreach (var page in source.Pages) all.TryAdd(page.Item.Id, new RenderPage(page.Item.Id, page.Item.Source, page.Item.Page, page.Item.TiledAnnotations));
        foreach (var page in Result) all.TryAdd(page.Item.Id, new RenderPage(page.Item.Id, page.Item.Source, page.Item.Page, page.Item.TiledAnnotations));
        Renderer.SetPages(all.Values);

        // Pages of a tab that are in the result: marked (the same page of the same file, also as a turned copy)
        var used = new HashSet<(PdfDocument, int)>(Result.Select(p => (p.Item.Source, p.Item.Page.Index)));
        foreach (var source in Sources)
            foreach (var page in source.Pages)
                page.Marks = used.Contains((page.Item.Source, page.Item.Page.Index)) ? [UsedMark] : null;

        Notes = BuildNotes();
        OnPropertyChanged(nameof(SourceText));
        AppendCommand.NotifyCanExecuteChanged();
        InsertAfterSelectionCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        Changed?.Invoke();
    }

    partial void OnKeepBookmarksChanged(bool value) => Notes = BuildNotes();

    /// <summary>„Danach nicht mehr da“ for the files loaded here whose pages are in the result, in the order they first appear.</summary>
    private List<MergeNoteGroup> BuildNotes()
    {
        var taken = _target is null ? new HashSet<string>(StringComparer.Ordinal) : MergeNotes.FieldNames(_target.Document);
        // the open document, where its pages are in the result (its structure decides what an untagged file costs)
        var keptTarget = _target is not null && Result.Any(p => !_loaded.Contains(p.Item.Source)) ? _target.Document : null;
        var groups = new List<MergeNoteGroup>();
        foreach (var file in Result.Select(p => p.Item.Source).Distinct(ReferenceEqualityComparer.Instance).Cast<PdfDocument>())
        {
            if (!_loaded.Contains(file)) continue;   // the open document's own pages
            var pages = Result.Where(p => ReferenceEquals(p.Item.Source, file)).Select(p => p.Item.Page).ToList();
            var lines = MergeNotes.For(file, _withPassword.GetValueOrDefault(file), pages, KeepBookmarks, taken, keptTarget);
            if (lines.Count > 0) groups.Add(new MergeNoteGroup(NameOf(file), lines));
        }
        return groups;
    }

    private string NameOf(PdfDocument file) =>
        Sources.FirstOrDefault(s => ReferenceEquals(s.Document, file))?.Name ?? (file.FilePath is { } path ? Path.GetFileName(path) : "");

    /// <summary>Whether the page in the result comes from the tab's file.</summary>
    private static bool Belongs(PageItem item, MergeSourceViewModel source) =>
        source.IsOpenDocument ? source.Pages.Any(p => ReferenceEquals(p.Item.Source, item.Source)) : ReferenceEquals(item.Source, source.Document);

    /// <summary>The selection in a grid has changed.</summary>
    public void SelectionChanged()
    {
        OnPropertyChanged(nameof(SourceText));
        AppendCommand.NotifyCanExecuteChanged();
        InsertAfterSelectionCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------ Finishing

    private bool CanOk => Result.Count > 0;

    /// <summary>Raised by „OK“; the window asks where the result goes.</summary>
    public event Action? OkRequested;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok() => OkRequested?.Invoke();

    /// <summary>Whether pages of the open document are in the result (for a new file they are taken from a copy of it).</summary>
    public bool UsesOpenDocument => Result.Any(p => _origin.ContainsKey(p.Item));

    /// <summary>
    /// The result. For the open document: its pages as they are. For a new file (<paramref name="copy"/> = a copy of the open document,
    /// needed if <see cref="UsesOpenDocument"/>): the open document's pages are replaced by those of the copy, so that the new file
    /// depends on neither the open document nor its file. The files loaded here whose pages are used are handed over.
    /// </summary>
    /// <param name="copy">Copy of the open document and its pages (<see cref="DocumentViewModel.CopyCurrentPages"/>); null for the open document.</param>
    /// <returns>The pages, the files handed over, and of these those without bookmarks.</returns>
    public MergeResult TakeResult((PdfDocument Document, PageItem[] Pages)? copy)
    {
        var pages = new List<PageItem>();
        var takenFromCopy = new HashSet<int>();
        foreach (var item in Items)
        {
            if (copy is { } c && _origin.TryGetValue(item, out var origin) && origin.Index < c.Pages.Length)
            {
                var page = c.Pages[origin.Index];
                // the same page twice (or turned): an entry of its own
                pages.Add(origin.Turn == 0 && takenFromCopy.Add(origin.Index) ? page : page.Rotated(origin.Turn));
                continue;
            }
            pages.Add(item);
        }
        var sources = pages.Select(p => p.Source).Where(s => _loaded.Contains(s)).Distinct(ReferenceEqualityComparer.Instance).Cast<PdfDocument>().ToList();
        if (copy is { } made) sources.Add(made.Document);
        _handedOver.UnionWith(sources);
        IReadOnlyList<PdfDocument> withoutBookmarks = KeepBookmarks ? [] : sources;
        return new MergeResult(pages, sources, withoutBookmarks);
    }

    /// <summary>
    /// Closes the window's work: the drawing stops, and the files loaded here that were not handed over are closed (only once nothing
    /// reads from them any more).
    /// </summary>
    public async Task ReleaseAsync()
    {
        Renderer.SetPages([]);
        Renderer.Dispose();   // cancels display lists being built
        await Renderer.WaitUntilUnused(_loaded);
        foreach (var doc in _loaded)
            if (!_handedOver.Contains(doc)) doc.Dispose();
    }
}
