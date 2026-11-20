using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>The window „Text erkennen“: pages and size of the text recognition (see <see cref="RecognizeTextViewModel"/>); closes with true for „Erkennen“.</summary>
public partial class RecognizeTextWindow : Window
{
    public RecognizeTextWindow()
    {
        InitializeComponent();
    }

    public RecognizeTextWindow(RecognizeTextViewModel model) : this()
    {
        DataContext = model;
        model.Started += () => Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
