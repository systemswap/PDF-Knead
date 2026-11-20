using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PdfKnead.Core.Platform;
using PdfKnead.Services;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>File → Compress: settings on the left, before/after on the right; the size can be changed and is remembered.</summary>
public partial class CompressWindow : Window
{
    private readonly CompressViewModel? _vm;

    /// <summary>
    /// Maximum height when opening, because a remembered height beyond the screen would hide the title bar buttons;
    /// the window can still be dragged larger.
    /// </summary>
    private const double MaxStartHeight = 800;

    public CompressWindow() => InitializeComponent();

    /// <param name="vm">View model of the compress window; disposed when the window closes.</param>
    public CompressWindow(CompressViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        Title = Tr.T("Komprimieren – {0}", vm.FileName);
        if (UserSettings.Current.CompressWindow is { Width: >= 820 and <= 10000, Height: > 0 and <= 10000 } size)
            (Width, Height) = (size.Width, Math.Clamp(size.Height, MinHeight, MaxStartHeight));
        WindowFit.ToScreenWhenOpened(this);
        vm.Finished += message => Close(message);
        vm.PickSavePath = async (title, name, folder) =>
        {
            var start = folder is { Length: > 0 } ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = name,
                SuggestedStartLocation = start,
                DefaultExtension = "pdf",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType(Tr.T("PDF-Dokumente")) { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] }],
            });
            return file?.TryGetLocalPath();
        };
        vm.ConfirmSignedSave = async question => await new SignedSaveWindow(question).ShowDialog<SignedSaveAnswer?>(this) ?? SignedSaveAnswer.Cancel;
        // Methods that are not available yet are greyed out in the list.
        foreach (var box in new[] { PhotoBox, GraphicsBox, BilevelBox, ScanBox, PackBox })
            box.ContainerPrepared += (_, e) => e.Container.IsEnabled = box.Items[e.Index] is not CodecInfo { Available: false };
        // Create the information box per method only on click: as flyout content in the XAML, the text blocks and backgrounds of the whole
        // window disappeared (found in a screenshot).
        void Info(Button button, Func<CodecInfo> info) => button.Click += (_, _) =>
            new Flyout { Placement = PlacementMode.RightEdgeAlignedTop, Content = new Controls.CodecInfoView { DataContext = info() } }.ShowAt(button);
        Info(PhotoInfo, () => vm.SelectedPhotoMethod);
        Info(GraphicsInfo, () => vm.SelectedGraphicsMethod);
        Info(BilevelInfo, () => vm.SelectedBilevelMethod);
        Info(ScanInfo, () => vm.SelectedScanMethod);
        Info(PackInfo, () => vm.SelectedPackMethod);
        Preview.RegionWanted += vm.RequestRegion;
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.Property == Controls.ComparePreview.ZoomPercentProperty) ZoomText.Text = Tr.T("{0} %", Preview.ZoomPercent);
        };
        Closing += (_, e) =>
        {
            // Stay open while saving: the file is being written.
            if (vm.Saving) e.Cancel = true;
            else if (WindowState == WindowState.Normal)
                UserSettings.Update(s => s with { CompressWindow = new WindowSize(Width, Height) });
        };
        WindowAnimations.StopOnClose(this);
        Closed += (_, _) => vm.Dispose();
    }

    /// <summary>Page Up/Page Down turn the pages.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm is null || e.Handled) return;
        if (e.Key == Key.PageDown && _vm.NextPageCommand.CanExecute(null)) _vm.NextPageCommand.Execute(null);
        else if (e.Key == Key.PageUp && _vm.PreviousPageCommand.CanExecute(null)) _vm.PreviousPageCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Preview.ZoomBy(1.25);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => Preview.ZoomBy(1 / 1.25);

    private void OnFit(object? sender, RoutedEventArgs e) => Preview.Fit();

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_vm is { Saving: true }) return;
        Close(null);
    }
}
