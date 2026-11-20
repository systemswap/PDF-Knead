using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.Core.Licensing;

namespace PdfKnead.Views;

/// <summary>
/// Before switching from an authority or commercial use to a free kind of use: friendly, with the way to support the development. True =
/// switch.
/// </summary>
public sealed partial class SupportWindow : Window
{
    private readonly Func<Task>? _support;

    public SupportWindow() : this(UsageKind.Private, null)
    {
    }

    /// <param name="usage">The free kind of use chosen.</param>
    /// <param name="support">Opens the page to support the development (in the browser of the system, only on the click).</param>
    public SupportWindow(UsageKind usage, Func<Task>? support)
    {
        InitializeComponent();
        _support = support;
        var name = Services.Licensing.UsageName(usage);
        SwitchText.Text = Tr.T("Mit „{0}“ sind alle Funktionen frei. Das ist richtig, wenn Sie sich verklickt haben oder PDF-Knead jetzt privat, in einer Bildungseinrichtung, einer gemeinnützigen Einrichtung oder einem kleinen Verein nutzen.", name);
        SwitchButton.Content = Tr.T("Auf „{0}“ umstellen", name);
    }

    private async void OnSupport(object? sender, RoutedEventArgs e)
    {
        if (_support is { } support) await support();
    }

    private void OnSwitch(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
