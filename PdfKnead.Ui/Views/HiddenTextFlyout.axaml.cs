using Avalonia.Controls;

namespace PdfKnead.Views;

/// <summary>
/// Flyout beside the panel „Verborgener Inhalt“: the whole text of the chosen spot, to scroll through (a row shows only its beginning).
/// The text can be selected and copied only if the document allows copying; the spot can be marked for redaction here as well.
/// </summary>
public sealed partial class HiddenTextFlyout : UserControl
{
    public HiddenTextFlyout()
    {
        InitializeComponent();
        // another spot: its text starts at the top
        DataContextChanged += (_, _) => Scroller.Offset = default;
        CopyableText.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) Scroller.Offset = default;
        };
    }

    /// <summary>The text as shown (for tests).</summary>
    internal string? ShownText => CopyableText.IsVisible ? CopyableText.Text : ReadOnlyText.Text;

    /// <summary>The text can be selected and copied.</summary>
    internal bool CanCopyText => CopyableText.IsVisible;
}
