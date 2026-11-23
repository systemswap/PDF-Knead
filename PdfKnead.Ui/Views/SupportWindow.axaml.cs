using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.Services;

namespace PdfKnead.Views;

/// <summary>
/// The window that invites support after a successful save (<see cref="Services.SupportPrompt"/>): true after „Unterstützen“ (the page then
/// opens in the browser of the system), false after „Vielleicht später“; closing it counts as „Vielleicht später“. The contact address under
/// the picture opens the mail program (<see cref="Contact"/>) and leaves the window open.
/// </summary>
public sealed partial class SupportWindow : Window
{
    public SupportWindow()
    {
        InitializeComponent();
        ContactAddress.Text = Contact.Address;
    }

    private void OnSupport(object? sender, RoutedEventArgs e) => Close(true);

    private void OnLater(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnContact(object? sender, RoutedEventArgs e) => await Contact.WriteAsync(this);

    private async void OnCopyContact(object? sender, RoutedEventArgs e) => await Contact.CopyAsync(this);
}
