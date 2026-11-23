using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.Services;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>The window „Über PDF-Knead“ with the license texts (see <see cref="AboutViewModel"/>) and the contact address (<see cref="Contact"/>).</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        ContactAddress.Text = Contact.Address;
    }

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

    private async void OnContact(object? sender, RoutedEventArgs e) => await Contact.WriteAsync(this);

    private async void OnCopyContact(object? sender, RoutedEventArgs e) => await Contact.CopyAsync(this);
}
