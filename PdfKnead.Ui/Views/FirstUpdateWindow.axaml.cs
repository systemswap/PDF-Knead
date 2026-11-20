using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PdfKnead.Views;

/// <summary>
/// At the first start: asks once whether PDF-Knead should check for updates (KONZEPT.md §5.26; off at first). Returns true for „Updates
/// einschalten“, false for „Nicht suchen“; closing the window counts as „Nicht suchen“.
/// </summary>
public partial class FirstUpdateWindow : Window
{
    public FirstUpdateWindow() => InitializeComponent();

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNo(object? sender, RoutedEventArgs e) => Close(false);
}
