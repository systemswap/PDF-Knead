using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using PdfKnead.Services;

namespace PdfKnead.ViewModels;

/// <summary>A part when splitting: name (= file name), colour, pages in document order.</summary>
public sealed partial class SplitPartViewModel : ObservableObject
{
    public SplitPartViewModel(string name, Color color)
    {
        Name = name;
        Color = color;
        Brush = new SolidColorBrush(color);
        Pages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    [ObservableProperty] public partial string Name { get; set; }

    public Color Color { get; }

    public IBrush Brush { get; }

    public ObservableCollection<PageViewModel> Pages { get; } = [];

    /// <summary>Whether the pointer is over this part while dragging.</summary>
    [ObservableProperty] public partial bool IsDropTarget { get; set; }

    /// <summary>Summary such as „3 Seiten · 1-3, 7“.</summary>
    public string Summary => Pages.Count == 1
        ? Tr.T("1 Seite · {0}", PageRange.Format(Pages.Select(p => p.Index)))
        : Tr.T("{0} Seiten · {1}", Pages.Count, PageRange.Format(Pages.Select(p => p.Index)));
}

/// <summary>
/// Split window: all pages as tiles, the parts on the right. Selected pages dragged onto the
/// drop target form a new part; dragged onto a part, they are added to it. A page may belong to
/// several parts; assigned pages carry the colours of their parts. See KONZEPT.md, section 5.1.
/// </summary>
public sealed partial class SplitViewModel : ObservableObject
{
    private static readonly Color[] Palette =
    [
        Color.Parse("#4F46E5"), Color.Parse("#0EA5E9"), Color.Parse("#16A34A"), Color.Parse("#F59E0B"), Color.Parse("#E11D48"),
        Color.Parse("#9333EA"), Color.Parse("#0D9488"), Color.Parse("#EA580C"), Color.Parse("#64748B"), Color.Parse("#DB2777"),
    ];

    private readonly DocumentViewModel _doc;

    /// <summary>Many parts at once (quick actions): marks are updated only at the end, otherwise the work grows quadratically.</summary>
    private bool _batch;

    public SplitViewModel(DocumentViewModel doc)
    {
        _doc = doc;
        Pages = [.. doc.History.Pages.Select((item, i) => new PageViewModel(item, i))];
        Parts.CollectionChanged += (_, _) =>
        {
            if (_batch) return;
            UpdateMarks();
            OnPropertyChanged(nameof(HasParts));
            SaveCommand.NotifyCanExecuteChanged();
        };
    }

    public string FileName => _doc.FileName;

    public DocumentRenderer Renderer => _doc.Renderer;

    /// <summary>Pages of the document in its current form (including unsaved changes).</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    public ObservableCollection<SplitPartViewModel> Parts { get; } = [];

    public bool HasParts => Parts.Count > 0;

    /// <summary>Whether pages without a part are also saved as a separate file.</summary>
    [ObservableProperty] public partial bool IncludeRest { get; set; }

    /// <summary>Page count for „Alle X Seiten teilen“.</summary>
    [ObservableProperty] public partial decimal? EveryPages { get; set; } = 1;

    [ObservableProperty] public partial string SelectionText { get; set; } = "";

    public bool HasOutline => _doc.Outline.Any(o => o.PageIndex is not null);

    /// <summary>Lets the user choose the target folder (set by the view); returns the folder, or null.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>File names of the saved parts (for the feedback in the main window).</summary>
    public IReadOnlyList<string> SavedFiles { get; private set; } = [];

    [ObservableProperty] public partial string Error { get; set; } = "";

    /// <summary>Returns the selected pages.</summary>
    /// <returns>The selected pages in document order.</returns>
    public List<PageViewModel> Selected() => [.. Pages.Where(p => p.IsSelected)];

    /// <summary>Updates <see cref="SelectionText"/> after the selection has changed.</summary>
    public void UpdateSelection()
    {
        var count = Pages.Count(p => p.IsSelected);
        SelectionText = count == 0 ? Tr.T("Seiten auswählen und rechts auf das Ablagefeld ziehen") : count == 1
            ? Tr.T("1 Seite ausgewählt")
            : Tr.T("{0} Seiten ausgewählt", count);
    }

    private string NextName() => Tr.T("Teil {0}", Parts.Count + 1);

    private SplitPartViewModel NewPart(string name) => new(name, Palette[Parts.Count % Palette.Length]);

    /// <summary>Creates a new part from the selected pages (drop target); the selection is cleared afterwards.</summary>
    [RelayCommand]
    public void AddPart()
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        var part = NewPart(NextName());
        foreach (var page in selected) part.Pages.Add(page);
        Parts.Add(part);
        ClearSelection();
    }

    /// <summary>Adds the selected pages to a part (without duplicates, in document order).</summary>
    /// <param name="part">The part.</param>
    public void AddToPart(SplitPartViewModel part)
    {
        var pages = part.Pages.Union(Selected()).OrderBy(p => p.Index).ToList();
        part.Pages.Clear();
        foreach (var page in pages) part.Pages.Add(page);
        ClearSelection();
        UpdateMarks();
    }

    /// <summary>Removes a part.</summary>
    /// <param name="part">The part.</param>
    [RelayCommand]
    public void RemovePart(SplitPartViewModel part) => Parts.Remove(part);

    /// <summary>Removes a page from a part (the part stays, even if it becomes empty).</summary>
    /// <param name="part">The part.</param>
    /// <param name="page">The page.</param>
    public void RemovePage(SplitPartViewModel part, PageViewModel page)
    {
        part.Pages.Remove(page);
        UpdateMarks();
    }

    /// <summary>Replaces the parts with one part per <see cref="EveryPages"/> pages.</summary>
    [RelayCommand]
    public void SplitEvery()
    {
        var n = (int)Math.Clamp(EveryPages ?? 1, 1, Math.Max(1, Pages.Count));
        _batch = true;
        Parts.Clear();
        for (var start = 0; start < Pages.Count; start += n)
        {
            var part = NewPart(NextName());
            for (var i = start; i < Math.Min(Pages.Count, start + n); i++) part.Pages.Add(Pages[i]);
            Parts.Add(part);
        }
        EndBatch();
    }

    /// <summary>Splits at the top-level bookmarks; each part is named after its bookmark.</summary>
    [RelayCommand]
    public void SplitAtBookmarks()
    {
        var starts = new SortedDictionary<int, string>();
        foreach (var item in _doc.Outline)
            if (item.PageIndex is { } index && _doc.PositionOf(index) is { } position)
                starts.TryAdd(position, item.Title);
        if (starts.Count == 0) return;
        if (!starts.ContainsKey(0)) starts[0] = Tr.T("Anfang");
        _batch = true;
        Parts.Clear();
        var list = starts.ToList();
        for (var k = 0; k < list.Count; k++)
        {
            var end = k + 1 < list.Count ? list[k + 1].Key : Pages.Count;
            var part = NewPart(list[k].Value);
            for (var i = list[k].Key; i < end; i++) part.Pages.Add(Pages[i]);
            Parts.Add(part);
        }
        EndBatch();
    }

    private void EndBatch()
    {
        _batch = false;
        UpdateMarks();
        OnPropertyChanged(nameof(HasParts));
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Clears the page selection.</summary>
    public void ClearSelection()
    {
        foreach (var page in Pages) page.IsSelected = false;
        UpdateSelection();
    }

    /// <summary>Gives each page the colours of the parts it belongs to.</summary>
    public void UpdateMarks()
    {
        var marks = new Dictionary<PageViewModel, List<Color>>();
        foreach (var part in Parts)
            foreach (var page in part.Pages)
                (marks.TryGetValue(page, out var list) ? list : marks[page] = []).Add(part.Color);
        foreach (var page in Pages) page.Marks = marks.GetValueOrDefault(page);
        MarksChanged?.Invoke();
    }

    /// <summary>Raised when the marks have changed (the grid redraws).</summary>
    public event Action? MarksChanged;

    private bool CanSave => Parts.Count > 0;

    /// <summary>Saves each part as a separate file into a folder (part name = file name).</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task Save()
    {
        if (PickFolder is null || await PickFolder() is not { } folder) return;
        await SaveToAsync(folder);
    }

    /// <summary>
    /// Asks whether the attachments of the whole document go along into the parts (set by the main window's view model; null: they do not).
    /// Returns null if the user cancelled.
    /// </summary>
    public Func<Task<bool?>>? AskDocumentAttachments { get; init; }

    /// <summary>Saves into a folder; existing files are not overwritten („Name (2).pdf“).</summary>
    /// <param name="folder">Target folder.</param>
    /// <returns>false if all parts are empty or saving failed (<see cref="Error"/> says why).</returns>
    public async Task<bool> SaveToAsync(string folder)
    {
        var jobs = Parts.Where(p => p.Pages.Count > 0).Select(p => (p.Name, Items: p.Pages.Select(v => v.Item).ToList())).ToList();
        if (IncludeRest)
        {
            var used = Parts.SelectMany(p => p.Pages).ToHashSet();
            var rest = Pages.Where(p => !used.Contains(p)).Select(p => p.Item).ToList();
            if (rest.Count > 0) jobs.Add((Tr.T("Übrige Seiten"), rest));
        }
        if (jobs.Count == 0)
        {
            Error = Tr.T("Alle Teile sind leer.");
            return false;
        }
        // each part takes only what belongs to its pages; the attachments of the whole document only if the user says so (No. 447)
        var keep = false;
        if (AskDocumentAttachments is not null)
        {
            if (await AskDocumentAttachments() is not { } answer)
            {
                Error = "";
                return false;
            }
            keep = answer;
        }
        var saved = new List<string>();
        var taken = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            for (var k = 0; k < jobs.Count; k++)
            {
                var (name, items) = jobs[k];
                var path = UniquePath(folder, FileNameOf(name, k + 1), taken);
                var main = _doc.Document;
                // As when saving the document: pending protection and cleanup apply (otherwise they would be dropped in the parts).
                // Redactions were applied before the window opened („Schwärzen zuerst“); a mark left over refuses saving
                InDocumentRedaction.EnsureApplied(items);
                var options = _doc.SaveOptions(_doc.SaveVersion) with { PartOfDocument = true, KeepDocumentAttachments = keep };
                await _doc.WorkAsync(() => Task.Run(() => PdfFileSaver.Save(main, items, path, options)));
                saved.Add(Path.GetFileName(path));
            }
        }
        catch (Exception ex) when (Failure.Handled(ex))
        {
            Error = Tr.T("Speichern fehlgeschlagen: {0}", Failure.Text(ex));
            SavedFiles = saved;
            return false;
        }
        SavedFiles = saved;
        Error = "";
        return true;
    }

    /// <summary>Makes a valid file name (without extension) from the part name.</summary>
    /// <param name="name">Name of the part.</param>
    /// <param name="number">Number of the part, used for „Teil n“ if nothing usable remains of the name.</param>
    /// <returns>The file name without „.pdf“, at most 120 UTF-16 units long.</returns>
    public static string FileNameOf(string name, int number)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        // Format characters too (U+202E reverses the reading direction: „invoice[U+202E]fdp.exe“ would look like a different file)
        var clean = new string([.. name.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ||
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? '_' : c)]).Trim(' ', '.');
        if (clean.Length == 0) clean = Tr.T("Teil {0}", number);
        if (clean.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) clean = clean[..^4];
        // Device names (CON, NUL, COM1 …) are not files on Windows; otherwise splitting would abort halfway there
        var stem = clean.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]))
            clean = "_" + clean;
        if (clean.Length <= 120) return clean;
        // Do not cut in the middle of a character made of two UTF-16 units (emoji, rare characters)
        return char.IsHighSurrogate(clean[119]) ? clean[..119] : clean[..120];
    }

    private static string UniquePath(string folder, string name, HashSet<string> taken)
    {
        var path = Path.Combine(folder, name + ".pdf");
        for (var k = 2; File.Exists(path) || taken.Contains(path); k++) path = Path.Combine(folder, $"{name} ({k}).pdf");
        taken.Add(path);
        return path;
    }
}
