using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// Split: select pages on the left and drag them to the right (drop area = new part, existing part = add to it).
/// Result: true if the parts were saved.
/// </summary>
public partial class SplitWindow : Window
{
    private readonly SplitViewModel? _vm;

    public SplitWindow() => InitializeComponent();

    public SplitWindow(SplitViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        Title = Tr.T("Teilen – {0}", vm.FileName);
        vm.PickFolder ??= async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Tr.T("Ordner für die Teile") });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        };
        vm.SaveCommand.PropertyChanged += (_, e) =>
        {
            // Close after saving successfully.
            if (e.PropertyName == nameof(vm.SaveCommand.IsRunning) && !vm.SaveCommand.IsRunning && vm.SavedFiles.Count > 0 && vm.Error.Length == 0)
                Close(true);
        };
        vm.MarksChanged += () => GridView.Grid.InvalidateVisual();

        var grid = GridView.Grid;
        grid.Renderer = vm.Renderer;
        grid.Pages = vm.Pages;
        grid.SelectionChanged += vm.UpdateSelection;
        grid.PagesDragging += p => Highlight(TargetAt(grid, p));
        grid.PagesDropped += p => Drop(TargetAt(grid, p));
        SizeSlider.Value = grid.TileSize;
        SizeSlider.ValueChanged += (_, e) => grid.TileSize = e.NewValue;
        grid.PropertyChanged += (_, e) =>
        {
            if (e.Property == PageGrid.TileSizeProperty) SizeSlider.Value = grid.TileSize;
        };
        vm.UpdateSelection();
        Opened += (_, _) => grid.Focus();
        // Stay open while saving: the parts are being written, and closing would let saving continue unnoticed.
        Closing += (_, e) =>
        {
            if (vm.SaveCommand.IsRunning) e.Cancel = true;
        };
        Closed += (_, _) => grid.Renderer = null;
    }

    /// <summary>Drop target under the pointer: a part, the “new part” area (true) or nothing (null).</summary>
    private object? TargetAt(Visual from, Point point)
    {
        if (from.TranslatePoint(point, this) is not { } p || this.GetInputElementsAt(p).FirstOrDefault() is not Visual hit) return null;
        foreach (var visual in hit.GetSelfAndVisualAncestors())
        {
            if (visual is Button { Name: "NewPartTarget" }) return true;
            if (visual is Border { Tag: "part", DataContext: SplitPartViewModel part }) return part;
        }
        return null;
    }

    private void Highlight(object? target)
    {
        if (_vm is null) return;
        foreach (var part in _vm.Parts) part.IsDropTarget = ReferenceEquals(part, target);
        NewPartTarget.Classes.Set("dropTarget", target is true);
    }

    private void Drop(object? target)
    {
        Highlight(null);
        if (_vm is null) return;
        if (target is true) _vm.AddPart();
        else if (target is SplitPartViewModel part) _vm.AddToPart(part);
        if (target is not null) RangeBox.Text = "";
        GridView.Grid.InvalidateVisual();
    }

    private void OnRemovePage(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { DataContext: PageViewModel page } button) return;
        if (button.FindAncestorOfType<Border>() is not { } border) return;
        foreach (var visual in border.GetSelfAndVisualAncestors())
            if (visual is Border { Tag: "part", DataContext: SplitPartViewModel part })
            {
                _vm.RemovePage(part, page);
                return;
            }
    }

    private void Apply(Func<int, bool> selected)
    {
        if (_vm is null) return;
        for (var i = 0; i < _vm.Pages.Count; i++) _vm.Pages[i].IsSelected = selected(i);
        GridView.Grid.InvalidateVisual();
        _vm.UpdateSelection();
    }

    private void OnAll(object? sender, RoutedEventArgs e) => Apply(_ => true);

    private void OnNone(object? sender, RoutedEventArgs e) => Apply(_ => false);

    private void OnRangeChanged(object? sender, TextChangedEventArgs e)
    {
        if (_vm is null || RangeBox.Text is not { } text || text.Trim().Length == 0) return;
        if (PageRange.Parse(text, _vm.Pages.Count) is not { } range) return;
        var set = range.ToHashSet();
        Apply(set.Contains);
        if (range.Count > 0) GridView.Grid.ScrollIntoView(range[0]);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
