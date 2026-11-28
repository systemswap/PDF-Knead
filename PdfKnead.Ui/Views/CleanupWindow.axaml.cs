using Avalonia.Controls;
using Avalonia.Interactivity;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// Lists the hidden data found, to be ticked. Result: what is removed from the document right away, or null if cancelled.
/// Whatever is useful to the reader (comments, bookmarks, structure tree) is not preselected.
/// </summary>
public partial class CleanupWindow : Window
{
    private readonly List<(CleanupItems Item, CheckBox Box)> _boxes = [];

    /// <summary>At most this many pages per finding as buttons; more are hinted with „…“.</summary>
    private const int MaxPageLinks = 30;

    public CleanupWindow() => InitializeComponent();

    /// <summary>Every item the sanitising offers, in the order of the window (<see cref="CleanupLabels.Order"/>).</summary>
    internal static IReadOnlyList<CleanupItems> AllItems => CleanupLabels.Order;

    /// <param name="found">Findings of the check.</param>
    /// <param name="chosen">Items already selected earlier (if none, the recommendation applies).</param>
    /// <param name="show">Shows a page of a finding in the document behind the window with its places framed (item, page index); returns how
    /// many places it framed (0: none lie visibly on the page, -1: the page could not be read). null: the pages of a finding are not offered.</param>
    public CleanupWindow(IReadOnlyList<CleanupFinding> found, CleanupItems chosen, Func<CleanupItems, int, Task<int>>? show = null) : this()
    {
        foreach (var item in AllItems)
        {
            if (found.FirstOrDefault(f => f.Item == item) is not { } finding) continue;
            var box = new CheckBox
            {
                IsChecked = chosen != CleanupItems.None ? chosen.HasFlag(item) : CleanupLabels.Recommended.HasFlag(item),
                Content = Describe(finding),
            };
            _boxes.Add((item, box));
            if (finding.Pages is { Count: > 0 } pages && show is not null)
            {
                // where it is: the pages as small buttons that show the page behind the window with the places framed (the window stays open
                // and moves aside), and a line that says what was framed
                var row = new WrapPanel { Margin = new Avalonia.Thickness(28, 0, 0, 4) };
                var result = new TextBlock { Classes = { "muted" }, FontSize = 11, IsVisible = false, Margin = new Avalonia.Thickness(28, 0, 0, 0),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                row.Children.Add(new TextBlock
                {
                    Text = pages.Count == 1 ? Tr.T("Seite:") : Tr.T("Seiten:"), Classes = { "muted" }, FontSize = 11,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 4, 0),
                });
                foreach (var page in pages.Take(MaxPageLinks))
                {
                    var target = page;
                    var link = new Button
                    {
                        Content = (page + 1).ToString(System.Globalization.CultureInfo.CurrentCulture), Classes = { "subtle" }, FontSize = 11,
                        Padding = new Avalonia.Thickness(5, 0), MinHeight = 18, Margin = new Avalonia.Thickness(0, 0, 2, 0),
                    };
                    Avalonia.Automation.AutomationProperties.SetName(link, Tr.T("Seite {0} zeigen", page + 1));
                    ToolTip.SetTip(link, Tr.T("Seite {0} zeigen", page + 1));
                    link.Bind(Button.ForegroundProperty, link.GetResourceObservable("AccentBrush"));
                    link.Click += async (_, _) =>
                    {
                        MoveAside();
                        var framed = await show(item, target);
                        result.Text = framed switch
                        {
                            < 0 => Tr.T("Seite {0} ließ sich nicht prüfen.", target + 1),
                            0 => Tr.T("Seite {0}: Nichts davon liegt sichtbar auf der Seite (neben der Seite, weggeschnitten oder nie gezeichnet).", target + 1),
                            1 => Tr.T("Seite {0}: 1 Stelle rot umrandet.", target + 1),
                            _ => Tr.T("Seite {0}: {1} Stellen rot umrandet.", target + 1, framed),
                        };
                        result.IsVisible = true;
                    };
                    row.Children.Add(link);
                }
                if (pages.Count > MaxPageLinks)
                    row.Children.Add(new TextBlock { Text = "…", Classes = { "muted" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                var group = new StackPanel();
                group.Children.Add(box);
                group.Children.Add(row);
                group.Children.Add(result);
                Items.Children.Add(group);
            }
            else
            {
                Items.Children.Add(box);
            }
        }
        NothingText.IsVisible = found.Count == 0;
        ApplyButton.IsEnabled = found.Count > 0;
    }

    /// <summary>
    /// Moves the window to the right edge of the main window the first time a page is shown, so that the page and its framed places are not
    /// hidden behind it.
    /// </summary>
    private void MoveAside()
    {
        if (_movedAside || Owner is not Window owner) return;
        _movedAside = true;
        var scale = owner.DesktopScaling;
        var x = owner.Position.X + (int)((owner.Bounds.Width - Bounds.Width - 16) * scale);
        Position = new Avalonia.PixelPoint(Math.Max(owner.Position.X, x), owner.Position.Y + (int)(90 * scale));
    }

    private bool _movedAside;

    /// <summary>Builds the label: what, how many, and the details of the finding (e.g. the entries found in the metadata).</summary>
    private static Control Describe(CleanupFinding finding)
    {
        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(new TextBlock { Text = CleanupLabels.Title(finding), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (finding.Detail.Length > 0)
            panel.Children.Add(new TextBlock { Text = Core.Security.DisplayNames.Isolated(finding.Detail), Classes = { "muted" }, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        if (CleanupLabels.Note(finding.Item) is { } note)
            panel.Children.Add(new TextBlock { Text = note, Classes = { "muted" }, FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        return panel;
    }

    /// <summary>Returns the selected items.</summary>
    internal CleanupItems Chosen() =>
        _boxes.Where(b => b.Box.IsChecked == true).Aggregate(CleanupItems.None, (all, b) => all | b.Item);

    private void OnApply(object? sender, RoutedEventArgs e) => Close((CleanupItems?)Chosen());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
