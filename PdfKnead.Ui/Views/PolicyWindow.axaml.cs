using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PdfKnead.Core.Policy;

namespace PdfKnead.Views;

/// <summary>
/// The permissions that apply, for everybody: every function of the catalog by ribbon tab, a green tick when allowed, a red cross when the
/// function file of the organization switched it off.
/// </summary>
public sealed partial class PolicyWindow : Window
{
    public PolicyWindow()
    {
        InitializeComponent();
        SummaryText.Text = ViewModels.MainViewModel.PolicySummary();
        Build();
    }

    private void Build()
    {
        var stacks = Enumerable.Range(0, 3).Select(i =>
        {
            var stack = new StackPanel { Spacing = 12 };
            Grid.SetColumn(stack, i * 2);
            TabGrid.Children.Add(stack);
            return stack;
        }).ToArray();
        // three columns of about the same height, in the order of the tabs
        var columns = new Dictionary<string, int>
        {
            ["Start"] = 0, ["Formular"] = 0, ["E-Rechnung"] = 0, ["Seiten"] = 1, ["Einfügen"] = 1, ["Unterschrift"] = 1, ["Sicher"] = 2,
        };
        var next = 0;
        foreach (var (tab, functions) in PolicyCatalog.Tabs)
        {
            var list = new StackPanel { Spacing = 3 };
            list.Children.Add(new TextBlock { Text = Tr.T(tab), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
            foreach (var function in functions) list.Children.Add(Line(Tr.T(function.Name), ActivePolicy.Allows(function.Id)));
            var column = columns.TryGetValue(tab, out var c) ? c : next++ % 3;
            stacks[column].Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(12, 8), Child = list });
        }
    }

    private Control Line(string name, bool allowed)
    {
        var mark = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 8, 0),
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(allowed ? "M4,7.6 L6.4,9.9 L11,5.2" : "M5,5 L10,10 M10,5 L5,10"),
                Stroke = Brushes.White, StrokeThickness = 1.7, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                Width = 15, Height = 15,
            },
        };
        mark[!Border.BackgroundProperty] = this.GetResourceObservable(allowed ? "GoodBrush" : "DangerBrush").ToBinding();
        ToolTip.SetTip(mark, allowed ? Tr.T("erlaubt") : Tr.T("gesperrt"));
        var text = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Opacity = allowed ? 1 : 0.7 };
        // a long name wraps beside its mark instead of being cut off at the edge of the card
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(10, 0, 0, 0) };
        grid.Children.Add(mark);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
