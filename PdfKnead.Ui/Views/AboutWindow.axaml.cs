using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>The window „Über PDF-Knead“ with the license texts (see <see cref="AboutViewModel"/>).</summary>
public partial class AboutWindow : Window
{
    public AboutWindow() => InitializeComponent();

    public AboutWindow(AboutViewModel model) : this()
    {
        DataContext = model;
        // Another text starts at its beginning
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AboutViewModel.Text)) TextScroller.Offset = default;
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
