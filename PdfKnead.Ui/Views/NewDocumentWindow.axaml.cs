using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// File → New and “Insert blank pages”: format (DIN A, Letter, Legal or custom size), orientation, count, and when
/// inserting also the position. Result: <see cref="BlankPagesChoice"/>, or null if cancelled.
/// </summary>
public partial class NewDocumentWindow : Window
{
    /// <summary>Edge length of the preview in pixels.</summary>
    private const double PreviewSize = 112;

    /// <summary>The window is setting values itself (no feedback through the events).</summary>
    private bool _updating;

    public NewDocumentWindow() : this(new PageSetup(), insert: false)
    {
    }

    /// <param name="setup">Initial page setup (e.g. the last one used).</param>
    /// <param name="insert">Insert blank pages into the open document instead of creating a new document; shows the position.</param>
    public NewDocumentWindow(PageSetup setup, bool insert)
    {
        InitializeComponent();
        setup = setup.Checked();
        FormatChoice.ItemsSource = PageSetup.Formats.Select(Describe).Append(Tr.T("Eigene Größe")).ToList();
        _updating = true;
        FormatChoice.SelectedIndex = setup.IsCustom ? PageSetup.Formats.Count : IndexOf(setup.Format);
        WidthBox.Value = (decimal)setup.Millimeters.Width;
        HeightBox.Value = (decimal)setup.Millimeters.Height;
        LandscapeChoice.IsChecked = setup.Landscape;
        PortraitChoice.IsChecked = !setup.Landscape;
        CountBox.Value = setup.Pages;
        _updating = false;
        LanguageBox.Value = Tr.Culture.Name;   // the language of the program as the default (No. 492)
        if (insert)
        {
            Title = HeadText.Text = Tr.T("Leere Seiten einfügen");
            SubText.Text = Tr.T("Leere Seiten im gewählten Format — vor oder nach der aktuellen Seite oder am Ende.");
            HeadIcon.Data = this.FindResource("Icon.DocumentOnePage") as Avalonia.Media.Geometry;
            PositionLabel.IsVisible = PositionPanel.IsVisible = true;
            TitleLabel.IsVisible = TitleBox.IsVisible = LanguageLabel.IsVisible = LanguageBox.IsVisible = false;
            ApplyButton.Content = Tr.T("Einfügen");
        }

        FormatChoice.SelectionChanged += (_, _) => OnFormatChanged();
        PortraitChoice.IsCheckedChanged += (_, _) => OnOrientationChanged();
        WidthBox.ValueChanged += (_, _) => OnSizeChanged();
        HeightBox.ValueChanged += (_, _) => OnSizeChanged();
        Refresh();
    }

    private static int IndexOf(string format) => Math.Max(0, PageSetup.Formats.ToList().IndexOf(format));

    /// <summary>“A4 · 210 × 297 mm” (dimensions in portrait).</summary>
    private static string Describe(string format)
    {
        var (w, h) = PageFormat.Millimeters(format) ?? (210, 297);
        return $"{format} · {Tr.T("{0} × {1} mm", w.ToString("0.#", Tr.Culture), h.ToString("0.#", Tr.Culture))}";
    }

    private bool IsCustom => FormatChoice.SelectedIndex >= PageSetup.Formats.Count;

    /// <summary>Landscape, read from the portrait button: its event arrives before the group switches the other one.</summary>
    private bool Landscape => PortraitChoice.IsChecked != true;

    private string Format => IsCustom ? PageSetup.Custom : PageSetup.Formats[Math.Max(0, FormatChoice.SelectedIndex)];

    /// <summary>Returns the current settings.</summary>
    internal PageSetup Setup() => new(Format, Landscape,
        (double)(WidthBox.Value ?? 210), (double)(HeightBox.Value ?? 297), (int)(CountBox.Value ?? 1));

    private BlankPagePosition InsertAt =>
        BeforeChoice.IsChecked == true ? BlankPagePosition.Before : EndChoice.IsChecked == true ? BlankPagePosition.End : BlankPagePosition.After;

    /// <summary>Another format: the custom size fields show its dimensions (so they can be adjusted starting from there).</summary>
    private void OnFormatChanged()
    {
        if (_updating) return;
        if (!IsCustom)
        {
            _updating = true;
            var (w, h) = Setup().Millimeters;
            WidthBox.Value = (decimal)w;
            HeightBox.Value = (decimal)h;
            _updating = false;
        }
        Refresh();
    }

    /// <summary>Portrait/landscape: for a custom size, swaps width and height so that both match.</summary>
    private void OnOrientationChanged()
    {
        if (_updating) return;
        _updating = true;
        var landscape = Landscape;
        if (IsCustom && WidthBox.Value is { } w && HeightBox.Value is { } h && (w > h) != landscape && w != h)
            (WidthBox.Value, HeightBox.Value) = (h, w);
        else if (!IsCustom)
        {
            var (fw, fh) = Setup().Millimeters;
            WidthBox.Value = (decimal)fw;
            HeightBox.Value = (decimal)fh;
        }
        _updating = false;
        Refresh();
    }

    /// <summary>Custom dimensions changed: the orientation follows (wider than high = landscape).</summary>
    private void OnSizeChanged()
    {
        if (_updating || !IsCustom) return;
        _updating = true;
        if (WidthBox.Value is { } w && HeightBox.Value is { } h && w != h)
        {
            LandscapeChoice.IsChecked = w > h;
            PortraitChoice.IsChecked = w < h;
        }
        _updating = false;
        Refresh();
    }

    /// <summary>Shows the size fields only for “Custom size”, and updates the preview and the dimensions below it.</summary>
    private void Refresh()
    {
        SizeLabel.IsVisible = SizePanel.IsVisible = IsCustom;
        var (w, h) = Setup().Millimeters;
        var scale = PreviewSize / Math.Max(w, h);
        PreviewPage.Width = Math.Max(4, w * scale);
        PreviewPage.Height = Math.Max(4, h * scale);
        SizeText.Text = Tr.T("{0} × {1} mm", w.ToString("0.#", Tr.Culture), h.ToString("0.#", Tr.Culture));
    }

    private void OnApply(object? sender, RoutedEventArgs e) =>
        Close(new BlankPagesChoice(Setup(), InsertAt, TitleBox.Text is { Length: > 0 } title ? title.Trim() : null,
            LanguageBox.Value is { Length: > 0 } language ? language.Trim() : null));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
