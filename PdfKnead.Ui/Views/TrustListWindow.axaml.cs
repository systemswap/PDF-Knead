using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>The „EU-Vertrauensliste“ window (see <see cref="TrustListViewModel"/>).</summary>
public partial class TrustListWindow : Window
{
    public TrustListWindow() => InitializeComponent();

    public TrustListWindow(TrustListViewModel model) : this() => DataContext = model;

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrustListViewModel { Busy: true } model) model.CancelCommand.Execute(null);
        Close();
    }
}
