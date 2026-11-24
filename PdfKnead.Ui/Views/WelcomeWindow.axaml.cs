using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// The first start in one window (decided on 24.11.2026): the language first — switched at once, the texts below follow — then the name
/// for the own certificate and the question about updates, each only where it is still open. „Los geht’s“ or closing ends it; what was
/// chosen is taken over by <see cref="MainViewModel.OfferWelcomeAsync"/>.
/// </summary>
public partial class WelcomeWindow : Window
{
    public WelcomeWindow() => InitializeComponent();

    /// <param name="model">The choices.</param>
    public WelcomeWindow(WelcomeViewModel model) : this()
    {
        DataContext = model;
        Opened += (_, _) => LanguageChoice.Focus();
    }

    private void OnStart(object? sender, RoutedEventArgs e) => Close(true);
}
