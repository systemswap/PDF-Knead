using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>The „Signaturen“ window: the digital signatures of the document and their check (see <see cref="SignaturesViewModel"/>).</summary>
public partial class SignaturesWindow : Window
{
    public SignaturesWindow() => InitializeComponent();

    public SignaturesWindow(SignaturesViewModel model) : this()
    {
        DataContext = model;
        model.CloseRequested += Close;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
