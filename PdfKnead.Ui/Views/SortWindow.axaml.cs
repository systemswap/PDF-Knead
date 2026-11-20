using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>Sorts pages in a tile view. Result: the new order, or null if cancelled.</summary>
public partial class SortWindow : Window
{
    private readonly SortViewModel? _vm;

    public SortWindow() => InitializeComponent();

    public SortWindow(SortViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        Title = Tr.T("Seiten sortieren – {0}", vm.FileName);
        var grid = GridView.Grid;
        grid.ReorderMode = true;
        grid.Renderer = vm.Renderer;
        grid.Pages = vm.Pages;
        grid.SelectionChanged += vm.UpdateSelection;
        grid.PagesReordered += vm.MoveSelected;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SortViewModel.Pages)) grid.Pages = vm.Pages;
        };
        SizeSlider.Value = grid.TileSize;
        SizeSlider.ValueChanged += (_, e) => grid.TileSize = e.NewValue;
        grid.PropertyChanged += (_, e) =>
        {
            if (e.Property == PageGrid.TileSizeProperty) SizeSlider.Value = grid.TileSize;
        };
        Opened += (_, _) => grid.Focus();
        Closed += (_, _) => grid.Renderer = null;
    }

    /// <summary>The tile view (for tests).</summary>
    internal PageGrid Grid => GridView.Grid;

    private void OnApply(object? sender, RoutedEventArgs e) => Close(_vm?.Order);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
