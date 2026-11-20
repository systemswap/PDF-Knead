using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.Core.Imaging;

namespace PdfKnead.Views;

/// <summary>
/// „Als Bild speichern“ (KONZEPT §5.25): which pages, the format (PNG, JPEG, TIFF — also all pages in one file —, WebP), the quality of
/// JPEG and WebP and, for pages of a document, the resolution (scans keep theirs). Result: the settings, or null if cancelled.
/// </summary>
public partial class PageImagesWindow : Window
{
    private static readonly int[] Resolutions = [150, 200, 300, 600];

    public PageImagesWindow() : this(new PageImageSettings(), 1, 0, document: true)
    {
    }

    /// <param name="start">The settings to start with (the last ones used).</param>
    /// <param name="all">The number of all pages.</param>
    /// <param name="selected">The number of selected pages; 0 disables „Ausgewählte“.</param>
    /// <param name="document">Pages of a document (resolution to choose) instead of scans (resolution kept).</param>
    public PageImagesWindow(PageImageSettings start, int all, int selected, bool document)
    {
        InitializeComponent();
        start = start.Normalized();
        AllChoice.Content = all == 1 ? Tr.T("Alle (1 Seite)") : Tr.T("Alle ({0} Seiten)", all);
        SelectedChoice.Content = selected == 1 ? Tr.T("Ausgewählte (1 Seite)") : Tr.T("Ausgewählte ({0} Seiten)", selected);
        SelectedChoice.IsEnabled = selected > 0 && selected < all;
        // a selection that is not the whole document is what the user means; otherwise all
        SelectedChoice.IsChecked = SelectedChoice.IsEnabled && (start.SelectedOnly || selected > 1);
        AllChoice.IsChecked = SelectedChoice.IsChecked != true;
        (start.Format switch
        {
            PageImageFormat.Jpeg => JpegChoice,
            PageImageFormat.Tiff => TiffChoice,
            PageImageFormat.WebP => WebPChoice,
            _ => PngChoice,
        }).IsChecked = true;
        OneFileChoice.IsChecked = start.OneFile;
        QualitySlider.Value = start.Quality;

        DpiLabel.IsVisible = DpiChoice.IsVisible = document;
        DpiChoice.ItemsSource = Resolutions.Select(dpi => dpi == 300 ? Tr.T("{0} dpi (Vorgabe)", dpi) : Tr.T("{0} dpi", dpi)).ToList();
        var nearest = Resolutions.MinBy(r => Math.Abs(r - start.Dpi));
        DpiChoice.SelectedIndex = Array.IndexOf(Resolutions, nearest);
        InfoText.Text = document
            ? Tr.T("Jede Seite so, wie sie angezeigt wird. Markierte Schwärzungen werden dabei angewendet. Sehr große Seiten (etwa A0) bekommen eine geringere Auflösung.")
            : Tr.T("Jede Seite so, wie sie aufbereitet übernommen würde, in der Auflösung des Scans.");

        foreach (var choice in new[] { PngChoice, JpegChoice, TiffChoice, WebPChoice }) choice.IsCheckedChanged += (_, _) => Update();
        QualitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) Update();
        };
        Update();
    }

    private PageImageFormat Format =>
        JpegChoice.IsChecked == true ? PageImageFormat.Jpeg
        : TiffChoice.IsChecked == true ? PageImageFormat.Tiff
        : WebPChoice.IsChecked == true ? PageImageFormat.WebP
        : PageImageFormat.Png;

    private void Update()
    {
        var lossy = Format is PageImageFormat.Jpeg or PageImageFormat.WebP;
        QualityLabel.IsEnabled = QualityPanel.IsEnabled = lossy;
        QualityLabel.Opacity = QualityPanel.Opacity = lossy ? 1 : 0.4;   // only JPEG and WebP have a quality
        QualityText.Text = ((int)QualitySlider.Value).ToString(System.Globalization.CultureInfo.CurrentCulture);
        OneFileChoice.IsEnabled = Format == PageImageFormat.Tiff;
    }

    /// <summary>Returns the selected settings.</summary>
    internal PageImageSettings Settings() => new(Format, (int)QualitySlider.Value, OneFileChoice.IsChecked == true,
        SelectedChoice.IsChecked == true, Resolutions[Math.Max(0, DpiChoice.SelectedIndex)]);

    private void OnApply(object? sender, RoutedEventArgs e) => Close(Settings());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
