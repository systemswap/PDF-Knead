using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Platform;
using PdfKnead.Services;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// „Seiten zusammenführen“ (KONZEPT.md, section 5.2): pages from the tabs at the top are dragged into the result at the bottom (an
/// insertion marker shows where), files can be dropped into the window. Result: 1 = into the open document, 2 = as a new file, null =
/// cancelled.
/// </summary>
public partial class MergeWindow : Window
{
    private readonly MergeViewModel? _vm;

    public MergeWindow() => InitializeComponent();

    public MergeWindow(MergeViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        if (UserSettings.Current.MergeWindow is { Width: >= 1100 and <= 10000, Height: >= 560 and <= 10000 } size)
            (Width, Height) = (size.Width, size.Height);
        WindowFit.ToScreenWhenOpened(this);
        vm.AskPassword = (name, wrong) => new PasswordWindow(name, wrong).ShowDialog<string?>(this);
        vm.AskOwnerPassword = (name, wrong) =>
            new PasswordWindow(Tr.T("{0} — erlaubt das Übernehmen der Seiten nur mit dem Berechtigungspasswort", name), wrong).ShowDialog<string?>(this);
        vm.AskRemoveOnClose = async name =>
        {
            var answer = await new ChoiceWindow(Tr.T("Reiter „{0}“ schließen", ChoiceWindow.ShortName(name)),
                Tr.T("Seiten dieser Datei stehen schon im Ergebnis. Sollen sie dort bleiben?"),
                Tr.T("Im Ergebnis behalten"), Tr.T("Auch aus dem Ergebnis entfernen")).ShowDialog<int>(this);
            return answer switch { 1 => false, 2 => true, _ => null };
        };
        vm.OkRequested += OnOk;
        vm.Changed += () =>
        {
            SourceView.Grid.InvalidateVisual();
            ResultView.Grid.InvalidateVisual();
        };

        // Both grids share the renderer: each asks for its tiles under a name of its own (otherwise one cancels the other's)
        var source = SourceView.Grid;
        source.Channel = "quellen";
        source.Renderer = vm.Renderer;
        source.Pages = vm.SelectedSource?.Pages;
        source.SelectionChanged += vm.SelectionChanged;
        source.PagesDragging += p => ResultView.Grid.ShowDropAt(OverResult(p));
        source.PagesDropped += p =>
        {
            var at = ResultView.Grid.ShowDropAt(OverResult(p));
            ResultView.Grid.ShowDropAt(null);
            if (at >= 0) vm.TakeSelected(at);
        };
        Bind(SourceSize, source);

        var result = ResultView.Grid;
        result.Channel = "ergebnis";
        result.ReorderMode = true;
        result.Renderer = vm.Renderer;
        result.Pages = vm.Result;
        result.SelectionChanged += vm.SelectionChanged;
        result.PagesReordered += vm.MoveSelected;
        Bind(ResultSize, result);

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MergeViewModel.SelectedSource))
            {
                source.Pages = vm.SelectedSource?.Pages;
                RangeBox.Text = "";
            }
            if (e.PropertyName == nameof(MergeViewModel.Result)) result.Pages = vm.Result;
        };

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Opened += (_, _) => (vm.SelectedSource is null ? result : source).Focus();
        Closing += (_, _) =>
        {
            if (WindowState == WindowState.Normal) UserSettings.Update(s => s with { MergeWindow = new WindowSize(Width, Height) });
        };
        Closed += (_, _) =>
        {
            source.Renderer = null;
            result.Renderer = null;
        };
    }

    /// <summary>The tile views (for tests).</summary>
    internal PageGrid SourceGrid => SourceView.Grid;

    internal PageGrid ResultGrid => ResultView.Grid;

    private static void Bind(Slider slider, PageGrid grid)
    {
        slider.Value = grid.TileSize;
        slider.ValueChanged += (_, e) => grid.TileSize = e.NewValue;
        grid.PropertyChanged += (_, e) =>
        {
            if (e.Property == PageGrid.TileSizeProperty) slider.Value = grid.TileSize;
        };
    }

    /// <summary>The point of the upper grid in the lower one, if it lies over it; otherwise null.</summary>
    private Point? OverResult(Point inSource)
    {
        var grid = ResultView.Grid;
        return SourceView.Grid.TranslatePoint(inSource, grid) is { } p && new Rect(grid.Bounds.Size).Contains(p) ? p : null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm is null || e.Handled || e.Source is TextBox) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Delete when _vm.RemoveSelectedCommand.CanExecute(null):
                _vm.RemoveSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Z when ctrl && _vm.UndoCommand.CanExecute(null):
                _vm.UndoCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Y when ctrl && _vm.RedoCommand.CanExecute(null):
                _vm.RedoCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToList();
        if (_vm is not null && paths is { Count: > 0 }) await _vm.AddFilesAsync(paths);
    }

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("PDF-Dateien zum Zusammenführen"),
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(Tr.T("PDF-Dokumente")) { Patterns = ["*.pdf", "*.PDF"], MimeTypes = ["application/pdf"] }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) await _vm.AddFilesAsync(paths);
    }

    /// <summary>„OK“: with an open document the question where the result goes, otherwise straight a new file.</summary>
    private async void OnOk()
    {
        if (_vm is null) return;
        if (!_vm.HasTarget)
        {
            Close(2);
            return;
        }
        var answer = await new ChoiceWindow(Tr.T("Wohin mit dem Ergebnis?"),
            Tr.T("In „{0}“ übernehmen: ein Schritt, der sich dort rückgängig machen lässt. Als neue Datei: ein neuer Reiter, unabhängig vom Original. Gespeichert wird danach getrennt.", _vm.TargetName),
            Tr.T("In „{0}“ übernehmen", ChoiceWindow.ShortName(_vm.TargetName)), Tr.T("Als neue Datei anlegen")).ShowDialog<int>(this);
        if (answer is 1 or 2) Close(answer);
    }

    private void Apply(Func<int, bool> selected)
    {
        if (_vm?.SelectedSource is not { } source) return;
        for (var i = 0; i < source.Pages.Count; i++) source.Pages[i].IsSelected = selected(i);
        SourceView.Grid.InvalidateVisual();
        _vm.SelectionChanged();
    }

    private void OnAll(object? sender, RoutedEventArgs e) => Apply(_ => true);

    private void OnNone(object? sender, RoutedEventArgs e) => Apply(_ => false);

    /// <summary>Typing a range („1-3, 7, 10-“) selects exactly these pages of the shown tab.</summary>
    private void OnRangeChanged(object? sender, TextChangedEventArgs e)
    {
        if (_vm?.SelectedSource is not { } source || RangeBox.Text is not { } text || text.Trim().Length == 0) return;
        if (PageRange.Parse(text, source.Pages.Count) is not { } range) return;
        var set = range.ToHashSet();
        Apply(set.Contains);
        if (range.Count > 0) SourceView.Grid.ScrollIntoView(range[0]);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
