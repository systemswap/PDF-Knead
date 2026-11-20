using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// „Als PDF/A speichern“: part, notes, the check and its reasons (<see cref="PdfAViewModel"/>). Opened, it writes and checks every part once
/// (closing stops that). Result: the conforming copy, one the user saves anyway, or null.
/// </summary>
public partial class PdfAWindow : Window
{
    public PdfAWindow() => InitializeComponent();

    /// <param name="vm">The window's model; a conforming copy closes the window with it.</param>
    public PdfAWindow(PdfAViewModel vm) : this()
    {
        DataContext = vm;
        Action<PdfAResult> ready = result => Close(result);
        vm.Ready += ready;
        var probe = new CancellationTokenSource();
        Opened += (_, _) => _ = vm.ProbeAsync(probe.Token);
        Closed += (_, _) =>
        {
            vm.Ready -= ready;   // cancelled while checking: the result has no window to go to any more
            probe.Cancel();
        };
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((PdfAResult?)null);
}
