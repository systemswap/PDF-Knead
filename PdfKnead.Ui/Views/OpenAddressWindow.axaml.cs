using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PdfKnead.Views;

/// <summary>Asks before an address from a QR or bar code is opened; shows it exactly as it would be opened and the warnings about it.</summary>
public sealed partial class OpenAddressWindow : Window
{
    public OpenAddressWindow() => InitializeComponent();

    public OpenAddressWindow(string target, IReadOnlyList<string> warnings) : this()
    {
        TargetText.Text = target;
        WarningBox.IsVisible = warnings.Count > 0;
        WarningText.Text = string.Join("\n", warnings);
    }

    private void OnOpen(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
