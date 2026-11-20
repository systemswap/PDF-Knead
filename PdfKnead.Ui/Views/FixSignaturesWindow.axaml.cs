using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// Shown before saving: signatures (or, when flattening, all elements) become fixed and can no longer be changed afterwards. Optionally
/// the document is also saved as an image (<see cref="FixingReply.SaveAsImage"/>).
/// </summary>
public partial class FixSignaturesWindow : Window
{
    public FixSignaturesWindow() => InitializeComponent();

    /// <param name="notice">Explanatory text shown in the window.</param>
    /// <param name="title">Heading and window title (otherwise the default “Unterschrift festschreiben?” from the XAML).</param>
    /// <param name="offerImage">Shows the check box “Auch als Bild flachlegen” (also save as image).</param>
    public FixSignaturesWindow(string notice, string? title = null, bool offerImage = false) : this()
    {
        NoticeText.Text = notice;
        if (title is not null) Title = TitleText.Text = title;
        ImageOption.IsVisible = offerImage;
    }

    private void OnSave(object? sender, RoutedEventArgs e) =>
        Close(ImageOption.IsVisible && AsImage.IsChecked == true ? FixingReply.SaveAsImage : FixingReply.Save);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(FixingReply.Cancel);
}
