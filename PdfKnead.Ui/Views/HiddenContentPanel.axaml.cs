using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>„Verborgener Inhalt“ panel: the hidden spots of the document, beside the page first, then on the page (KONZEPT.md, section 5.18).</summary>
public sealed partial class HiddenContentPanel : UserControl
{
    public HiddenContentPanel()
    {
        InitializeComponent();
        // before the list: it takes Enter for itself
        OutsideList.AddHandler(KeyDownEvent, OnSpotKeyDown, RoutingStrategies.Tunnel);
        OnPageList.AddHandler(KeyDownEvent, OnSpotKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>A double-click on a row shows its whole text (not on the check box: that only marks).</summary>
    private void OnSpotDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return;
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is HiddenSpotRow row)
        {
            e.Handled = true;
            ShowText(row);
        }
    }

    /// <summary>Enter on the chosen row shows its whole text.</summary>
    private void OnSpotKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || sender is not ListBox { SelectedItem: HiddenSpotRow row }) return;
        e.Handled = true;
        ShowText(row);
    }

    private void ShowText(HiddenSpotRow row) => (DataContext as DocumentViewModel)?.ShowHiddenText(row);
}
