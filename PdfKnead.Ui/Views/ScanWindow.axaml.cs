using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PdfKnead.Controls;
using PdfKnead.Core.Platform;
using PdfKnead.Core.Scanning.Escl;
using PdfKnead.Services;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// The window „Scannen“ (KONZEPT §5.25): settings on the left with the profile fixed at the top and „Scannen“ fixed at the bottom; on the right
/// the scanned pages as tiles above, the preview of one page below, a divider between them. Result: <see cref="ScanResult"/> or null.
/// </summary>
public partial class ScanWindow : Window
{
    private readonly ScanViewModel? _vm;

    public ScanWindow() => InitializeComponent();

    /// <param name="vm">The window's view model; release it with <see cref="Cleanup"/> after closing.</param>
    public ScanWindow(ScanViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        if (UserSettings.Current.ScanWindow is { Width: >= 1000 and <= 10000, Height: >= 600 and <= 10000 } size)
            (Width, Height) = (size.Width, size.Height);
        WindowFit.ToScreenWhenOpened(this);

        var grid = GridView.Grid;
        grid.Channel = "scan";
        grid.ReorderMode = true;
        grid.Renderer = vm.Renderer;
        grid.Pages = vm.Pages;
        grid.TileSize = TileSlider.Value;
        grid.SelectionChanged += vm.SelectionChanged;
        grid.PagesReordered += vm.MoveSelected;
        grid.BadgeClicked += vm.BadgeClicked;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScanViewModel.Pages))
            {
                grid.Pages = vm.Pages;
                if (vm.Current is { } current && vm.Pages.FirstOrDefault(p => ReferenceEquals(p.Item, current.Item)) is { } tile) grid.ScrollIntoView(tile.Index);
            }
            else if (e.PropertyName == nameof(ScanViewModel.IsNamingProfile) && vm.IsNamingProfile)
            {
                ProfileName.Focus();
                ProfileName.SelectAll();
            }
        };
        TileSlider.ValueChanged += (_, e) => grid.TileSize = e.NewValue;
        grid.PropertyChanged += (_, e) =>
        {
            if (e.Property == PageGrid.TileSizeProperty) TileSlider.Value = grid.TileSize;
        };
        Share(UserSettings.Current.ScanTilesShare);
        var closed = UserSettings.Current.ScanGroupsClosed ?? [];
        ScannerGroup.IsExpanded = !closed.Contains("scanner");
        ProcessingGroup.IsExpanded = !closed.Contains("processing");
        PreviewFit.Click += (_, _) => PreviewView.Fit();

        vm.TakeOverRequested += result => Close(result);
        vm.PickPageImages = async (start, all, selected) =>
            await new PageImagesWindow(start, all, selected, document: false).ShowDialog<Core.Imaging.PageImageSettings?>(this);
        vm.PickFolder = async title =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = title });
            return folders.FirstOrDefault() is { } picked ? Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(picked) : null;
        };
        if (vm.Network is { } network)
        {
            NetworkInput.LostFocus += (_, _) => network.CheckNetworkInput();
            network.PickSaveFile = async (title, name) =>
            {
                var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = title,
                    SuggestedFileName = name,
                    DefaultExtension = "txt",
                });
                return file is null ? null : Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file);
            };
        }
        vm.ConfirmCertificate = async (device, question) =>
        {
            var message = question.Previous is null
                ? Tr.T("„{0}“ weist sich mit einem eigenen Zertifikat aus. Fingerabdruck:\n{1}\n\nWer diesem Gerät vertraut, verbindet sich künftig nur noch mit genau diesem Zertifikat. Sonst wird der Scanner über den Scanner-Treiber des Systems angesprochen.",
                    device.DisplayName, EsclTrust.Display(question.Fingerprint))
                : Tr.T("Das Zertifikat von „{0}“ hat sich geändert. Wurde das Gerät zurückgesetzt oder ersetzt? Sonst könnte sich ein anderes Gerät als dieser Scanner ausgeben.\n\nBisher: {1}\nJetzt: {2}",
                    device.DisplayName, EsclTrust.Display(question.Previous), EsclTrust.Display(question.Fingerprint));
            var answer = await new ChoiceWindow(new ChoiceRequest(Tr.T("Zertifikat des Scanners"), message, [Tr.T("Vertrauen")], Tr.T("Nicht vertrauen"),
                Icon: "Icon.ShieldLock")).ShowDialog<ChoiceAnswer?>(this);
            return answer is { Answer: 1 };
        };
        KeyDown += OnKeyDown;
        // turning by hand takes its keys before any control does (the tiles would scroll on page up/down)
        AddHandler(KeyDownEvent, OnRotateKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            grid.Focus();
            await vm.StartAsync();
        };
        Closing += (_, _) =>
        {
            var rows = RightSide.RowDefinitions;
            var both = rows[1].ActualHeight + rows[3].ActualHeight;
            var share = both > 0 ? rows[1].ActualHeight / both : UserSettings.Current.ScanTilesShare;
            string[] folded = [.. new[] { ("scanner", ScannerGroup.IsExpanded), ("processing", ProcessingGroup.IsExpanded) }.Where(g => !g.Item2).Select(g => g.Item1)];
            UserSettings.Update(s => s with
            {
                ScanWindow = new WindowSize(Width, Height),
                ScanTilesShare = Math.Clamp(share, 0.1, 0.9),
                ScanGroupsClosed = folded,
            });
        };
        Closed += (_, _) =>
        {
            grid.Renderer = null;
            Cleanup = vm.ReleaseAsync();
        };
    }

    /// <summary>Completes when the device, the renderer and the preview documents are released.</summary>
    public Task Cleanup { get; private set; } = Task.CompletedTask;

    /// <summary>The tile view (for tests).</summary>
    internal PageGrid Grid => GridView.Grid;

    /// <summary>Shares the height between tiles (row 1) and preview (row 3) as last left; the divider between them changes it.</summary>
    private void Share(double tiles)
    {
        tiles = Math.Clamp(tiles, 0.1, 0.9);
        var rows = RightSide.RowDefinitions;
        rows[1].Height = new GridLength(tiles, GridUnitType.Star);
        rows[3].Height = new GridLength(1 - tiles, GridUnitType.Star);
    }

    /// <summary>
    /// Turning by hand: arrows a tenth of a degree, page up/down one degree, Enter keeps, Esc leaves (only in this mode — a scan is never
    /// stopped by Esc).
    /// </summary>
    private void OnRotateKey(object? sender, KeyEventArgs e)
    {
        if (_vm is not { IsFreeRotating: true } vm || e.Source is TextBox) return;
        double? step = e.Key switch { Key.Left => -0.1, Key.Right => 0.1, Key.PageDown => -1, Key.PageUp => 1, _ => null };
        if (step is { } s) vm.FreeAngle += s;
        else if (e.Key == Key.Enter) vm.ConfirmFreeRotateCommand.Execute(null);
        else if (e.Key == Key.Escape) vm.CancelFreeRotateCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Handled) return;
        // deliberately no Esc for cancelling a scan: pressed by mistake it would stop a feeder job (design decision)
        if (e.Key == Key.Delete && _vm.DeleteCommand.CanExecute(null))
        {
            _vm.DeleteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.R && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? _vm.RotateLeftCommand : _vm.RotateRightCommand;
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        }
    }
}
