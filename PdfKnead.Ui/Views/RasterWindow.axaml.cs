using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.Rendering;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>Resolution and format for “Save as image” and for exporting as an image PDF. Result: the settings, or null if cancelled.</summary>
public partial class RasterWindow : Window
{
    private static readonly int[] Resolutions = [150, 200, 300, 600];

    public RasterWindow() : this(RasterPurpose.SaveAsImage)
    {
    }

    /// <param name="purpose">Selects the title, texts and offered formats.</param>
    public RasterWindow(RasterPurpose purpose)
    {
        InitializeComponent();
        DpiChoice.ItemsSource = Resolutions.Select(dpi => dpi == 300 ? Tr.T("{0} dpi (Vorgabe)", dpi) : Tr.T("{0} dpi", dpi)).ToList();
        DpiChoice.SelectedIndex = Array.IndexOf(Resolutions, 300);
        switch (purpose)
        {
            case RasterPurpose.ExportPdf:
                Title = HeadText.Text = Tr.T("PDF als Bild exportieren");
                SubText.Text = Tr.T("Eine neue PDF, jede Seite ein Bild — so, wie sie angezeigt wird. Die geöffnete Datei bleibt, wie sie ist.");
                ApplyButton.Content = Tr.T("Exportieren …");
                break;
        }
    }

    /// <summary>Returns the selected settings.</summary>
    internal RasterSettings Settings() =>
        new(Resolutions[Math.Max(0, DpiChoice.SelectedIndex)], Lossless: LosslessChoice.IsChecked == true, Compact: CompactChoice.IsChecked == true);

    private void OnApply(object? sender, RoutedEventArgs e) => Close(Settings());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
