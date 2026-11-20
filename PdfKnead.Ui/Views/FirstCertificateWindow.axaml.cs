using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PdfKnead.Views;

/// <summary>
/// At the first start: asks for the name with which the own certificate for digital signatures is created (KONZEPT.md, section 5.17).
/// Returns the name, or null for „Später“.
/// </summary>
public partial class FirstCertificateWindow : Window
{
    public FirstCertificateWindow() => InitializeComponent();

    /// <param name="suggested">A name already known (the last one used for signatures).</param>
    public FirstCertificateWindow(string? suggested) : this()
    {
        NameBox.Text = suggested ?? "";
        CreateButton.IsEnabled = NameBox.Text.Trim().Length > 0;
        NameBox.TextChanged += (_, _) => CreateButton.IsEnabled = (NameBox.Text ?? "").Trim().Length > 0;
        Opened += (_, _) => NameBox.Focus();
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        if ((NameBox.Text ?? "").Trim() is { Length: > 0 } name) Close(name);
    }

    private void OnLater(object? sender, RoutedEventArgs e) => Close(null);
}
