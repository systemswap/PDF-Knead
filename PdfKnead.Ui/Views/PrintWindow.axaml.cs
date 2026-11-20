using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PdfKnead.Core.Printing;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>Print window: options on the left, preview on the right. Result: the message for the main window, or null if cancelled.</summary>
public partial class PrintWindow : Window
{
    private readonly PrintViewModel? _vm;
    private bool _finished;

    public PrintWindow() => InitializeComponent();

    public PrintWindow(PrintViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        Title = Tr.T("Drucken – {0}", vm.FileName);
        Poster.TileClicked += vm.OpenTile;
        vm.Finished += message =>
        {
            _finished = true;
            Close(message);
        };
        // Closing while printing (also with the X or Alt+F4): cancel first and wait, because the print job still reads from the
        // pages; otherwise they would be released underneath it and the program would crash.
        Closing += async (_, e) =>
        {
            if (_finished || !vm.IsPrinting) return;
            e.Cancel = true;
            vm.CancelPrintingCommand.Execute(null);
            await vm.WaitForJobAsync();
            _finished = true;
            Close(null);
        };
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
        vm.OpenDriverDialog = (printer, settings) =>
            TryGetPlatformHandle()?.Handle is { } handle ? Printers.DriverDialog(handle, printer, settings) : null;
        WindowAnimations.StopOnClose(this);
        Closed += (_, _) => vm.Dispose();
    }

    /// <summary>Page Up/Page Down turn the pages of the preview.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm is null || e.Handled) return;
        if (e.Key == Key.PageDown && _vm.NextSheetCommand.CanExecute(null)) _vm.NextSheetCommand.Execute(null);
        else if (e.Key == Key.PageUp && _vm.PreviousSheetCommand.CanExecute(null)) _vm.PreviousSheetCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    /// <summary>While printing, “Cancel” cancels the job; otherwise it closes the window.</summary>
    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_vm is { IsPrinting: true })
        {
            _vm.CancelPrintingCommand.Execute(null);
            return;
        }
        Close(null);
    }
}
