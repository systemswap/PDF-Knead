using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>The license window (<see cref="LicenseViewModel"/>).</summary>
public sealed partial class LicenseWindow : Window
{
    public LicenseWindow() => InitializeComponent();

    public LicenseWindow(LicenseViewModel model) : this()
    {
        DataContext = model;
        model.CloseRequested += Close;
        Opened += (_, _) => KeyBox.Focus();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
