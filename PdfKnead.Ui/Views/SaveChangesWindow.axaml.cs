using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>Asks before closing whether changes are to be saved.</summary>
public partial class SaveChangesWindow : Window
{
    public SaveChangesWindow() => InitializeComponent();

    public SaveChangesWindow(string fileName) : this() => FileText.Text = fileName;

    private void OnSave(object? sender, RoutedEventArgs e) => Close(SaveChoice.Save);

    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(SaveChoice.Discard);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(SaveChoice.Cancel);
}
