using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Rendering;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// Merge: all pages of a file as tiles with check marks; the result is the selected
/// pages (indices in the file) or null. Default: all.
/// </summary>
public partial class PageSelectionWindow : Window
{
    private readonly DocumentRenderer? _renderer;
    private readonly List<PageViewModel> _pages = [];
    private readonly PdfDocument? _document;

    public PageSelectionWindow() => InitializeComponent();

    /// <param name="document">The document whose pages are offered; it must stay open until <see cref="Cleanup"/> has completed.</param>
    /// <param name="fileName">File name shown in the header and title.</param>
    public PageSelectionWindow(PdfDocument document, string fileName) : this()
    {
        _document = document;
        TitleText.Text = fileName;
        Title = Tr.T("Seiten einfügen aus {0}", fileName);
        var items = PageItem.From(document);
        for (var i = 0; i < items.Length; i++) _pages.Add(new PageViewModel(items[i], i) { IsSelected = true });
        _renderer = new DocumentRenderer
        {
            EnhanceThinLines = Services.UserSettings.Current.EnhanceThinLines,
            OverprintPreview = Services.UserSettings.Current.OverprintPreview,
        };
        _renderer.SetPages(items.Select(p => new RenderPage(p.Id, document, p.Page)));

        var grid = GridView.Grid;
        grid.CheckMode = true;
        grid.Renderer = _renderer;
        grid.Pages = _pages;
        grid.SelectionChanged += UpdateCount;
        SizeSlider.Value = grid.TileSize;
        SizeSlider.ValueChanged += (_, e) => grid.TileSize = e.NewValue;
        grid.PropertyChanged += (_, e) =>
        {
            if (e.Property == PageGrid.TileSizeProperty) SizeSlider.Value = grid.TileSize;
        };
        UpdateCount();
        Opened += (_, _) => grid.Focus();
        Closed += (_, _) => Cleanup = CleanupAsync();
    }

    /// <summary>Completes when the window's renderer no longer reads from the file (only then may the file be closed).</summary>
    public Task Cleanup { get; private set; } = Task.CompletedTask;

    private async Task CleanupAsync()
    {
        if (_renderer is null || _document is null) return;
        _renderer.SetPages([]);
        _renderer.Dispose();   // cancels display lists being built
        await _renderer.WaitUntilUnused([_document]);
    }

    private void UpdateCount()
    {
        var count = _pages.Count(p => p.IsSelected);
        CountText.Text = Tr.T("{0} von {1} Seiten ausgewählt", count, _pages.Count);
        OkButton.IsEnabled = count > 0;
    }

    private void Apply(Func<int, bool> selected)
    {
        for (var i = 0; i < _pages.Count; i++) _pages[i].IsSelected = selected(i);
        GridView.Grid.InvalidateVisual();
        UpdateCount();
    }

    private void OnAll(object? sender, RoutedEventArgs e)
    {
        RangeBox.Text = "";
        Apply(_ => true);
    }

    private void OnNone(object? sender, RoutedEventArgs e)
    {
        RangeBox.Text = "";
        Apply(_ => false);
    }

    /// <summary>Typing a range (“1-3, 7, 10-”) selects exactly these pages.</summary>
    private void OnRangeChanged(object? sender, TextChangedEventArgs e)
    {
        var text = RangeBox.Text ?? "";
        if (text.Trim().Length == 0)
        {
            RangeError.IsVisible = false;
            return;
        }
        var range = PageRange.Parse(text, _pages.Count);
        RangeError.IsVisible = range is null;
        if (range is null) return;
        var set = range.ToHashSet();
        Apply(set.Contains);
        if (range.Count > 0) GridView.Grid.ScrollIntoView(range[0]);
    }

    private void OnOk(object? sender, RoutedEventArgs e) =>
        Close((IReadOnlyList<int>)_pages.Where(p => p.IsSelected).Select(p => p.Index).ToList());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
