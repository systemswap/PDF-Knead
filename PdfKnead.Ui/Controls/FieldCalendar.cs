using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PdfKnead.Controls;

/// <summary>
/// Calendar for filling in date fields: at the top a header in the accent colour with the date in large print, below it the month with
/// calendar weeks on the left (small, as badges), weeks starting on Monday, round days; today outlined, the selected one filled. A click on
/// month and year switches to the month and then to the year overview. Buttons “Today”, “Clear”, “Type in”. Keyboard: arrows,
/// Page Up/Down (month), Home (today), Enter confirms. Appearance in Styles/Controls.axaml (classes cal…).
/// </summary>
public sealed class FieldCalendar : Border
{
    private enum Mode
    {
        Days,
        Months,
        Years,
    }

    private readonly Button _title = new() { HorizontalAlignment = HorizontalAlignment.Center, Classes = { "calTitle" } };
    private readonly TextBlock _year = new() { Classes = { "calYear" } };
    private readonly TextBlock _date = new() { Classes = { "calDate" } };
    private readonly Panel _body = new() { Height = 236 };
    private readonly DateTime? _selected;
    private DateTime _focus;
    private DateTime _month;
    private Mode _mode = Mode.Days;

    /// <summary>Raised when a date has been chosen.</summary>
    public event Action<DateTime>? DateChosen;

    /// <summary>Raised by “Clear”.</summary>
    public event Action? Cleared;

    /// <summary>Raised by “Type in”: the user prefers to enter the date as text.</summary>
    public event Action? TypeRequested;

    /// <param name="selected">The current date of the field, or null (then today is shown).</param>
    public FieldCalendar(DateTime? selected)
    {
        _selected = selected?.Date;
        _focus = Limit(_selected ?? DateTime.Today);
        _month = new DateTime(_focus.Year, _focus.Month, 1);
        Focusable = true;
        Width = 330;

        // Header: year small, date large (the selected one, otherwise today).
        var header = new Border { Classes = { "calHeader" }, Child = new StackPanel { Spacing = 2, Children = { _year, _date } } };

        // Month with arrows
        _title.Click += (_, _) => Zoom();
        var nav = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(10, 10, 10, 4) };
        nav.Children.Add(NavButton(Geometry.Parse("M 10 4 L 6 8 L 10 12"), () => Step(-1), Tr.T("Zurück")));
        Grid.SetColumn(_title, 1);
        nav.Children.Add(_title);
        var next = NavButton(Geometry.Parse("M 6 4 L 10 8 L 6 12"), () => Step(1), Tr.T("Weiter"));
        Grid.SetColumn(next, 2);
        nav.Children.Add(next);

        // Footer
        var today = new Button { Content = Tr.T("Heute"), Classes = { "primary", "calToday" } };
        today.Click += (_, _) => DateChosen?.Invoke(DateTime.Today);
        var clear = new Button { Content = Tr.T("Leeren"), Classes = { "calLink" } };
        clear.Click += (_, _) => Cleared?.Invoke();
        var type = new Button { Content = Tr.T("Eintippen"), Classes = { "calLink" } };
        type.Click += (_, _) => TypeRequested?.Invoke();
        var footer = new DockPanel { Margin = new Thickness(12, 8, 12, 12), LastChildFill = false };
        DockPanel.SetDock(today, Dock.Left);
        footer.Children.Add(today);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        right.Children.Add(clear);
        right.Children.Add(type);
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);

        var line = new Border { Height = 1, Margin = new Thickness(12, 4, 12, 0) };
        line.Bind(Border.BackgroundProperty, line.GetResourceObservable("LineBrush"));
        _body.Margin = new Thickness(10, 0, 10, 4);
        Child = new StackPanel { Children = { header, nav, _body, line, footer } };
        Build();
    }

    private static CultureInfo Culture => Tr.Culture;

    private static Button NavButton(Geometry chevron, Action click, string name)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = chevron,
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 16,
            Height = 16,
        };
        var button = new Button { Content = path, Classes = { "calNav" } };
        // Stroke in the button's foreground colour (changes along with it on hover).
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, button.GetObservable(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty));
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    // Years 100 to 9899: otherwise the overview showed a year 0 at the edge and a click threw an exception (e.g. after
    // typing “01.01.0001” or pressing “Next” in December 9999).
    private static readonly DateTime First = new(100, 1, 1), Last = new(9899, 12, 31);

    private static DateTime Limit(DateTime day) => day < First ? First : day > Last ? Last.Date : day;

    /// <summary>Forward or back: one month, one year or twelve years, depending on the view.</summary>
    private void Step(int direction)
    {
        var next = _mode switch
        {
            Mode.Days => _month.AddMonths(direction),
            Mode.Months => _month.AddYears(direction),
            _ => _month.AddYears(12 * direction),
        };
        if (next < new DateTime(First.Year, 1, 1) || next > Last) return;
        _month = next;
        if (_mode == Mode.Days) _focus = Clamp(_focus, _month);
        Build();
    }

    /// <summary>Click on the title: days → months → years.</summary>
    private void Zoom()
    {
        _mode = _mode switch { Mode.Days => Mode.Months, _ => Mode.Years };
        Build();
    }

    private static DateTime Clamp(DateTime day, DateTime month) =>
        new(month.Year, month.Month, Math.Min(day.Day, DateTime.DaysInMonth(month.Year, month.Month)));

    private void Build()
    {
        var shown = _selected ?? _focus;
        _year.Text = shown.Year.ToString(CultureInfo.InvariantCulture);
        _date.Text = shown.ToString(Culture.TwoLetterISOLanguageName == "de" ? "dddd, d. MMMM" : "dddd, MMMM d", Culture);
        _body.Children.Clear();
        switch (_mode)
        {
            case Mode.Days:
                _title.Content = _month.ToString("MMMM yyyy", Culture);
                _body.Children.Add(Days());
                break;
            case Mode.Months:
                _title.Content = _month.Year.ToString(CultureInfo.InvariantCulture);
                _body.Children.Add(Tiles(12, i => (Culture.DateTimeFormat.AbbreviatedMonthNames[i], i + 1 == _month.Month), i =>
                {
                    _month = new DateTime(_month.Year, i + 1, 1);
                    _focus = Clamp(_focus, _month);
                    _mode = Mode.Days;
                    Build();
                }));
                break;
            default:
                var first = _month.Year - _month.Year % 12;
                _title.Content = $"{first} – {first + 11}";
                _body.Children.Add(Tiles(12, i => ((first + i).ToString(CultureInfo.InvariantCulture), first + i == _month.Year), i =>
                {
                    if (first + i < First.Year || first + i > Last.Year) return;
                    _month = new DateTime(first + i, _month.Month, 1);
                    _mode = Mode.Months;
                    Build();
                }));
                break;
        }
    }

    /// <summary>Month view: the calendar week (ISO 8601) small on the left, then Monday to Sunday.</summary>
    private Grid Days()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*,*,*,*,*,*,*"), RowDefinitions = new RowDefinitions("24,*,*,*,*,*,*") };
        Add(grid, new TextBlock { Text = Tr.T("KW"), Classes = { "calWeekday" } }, 0, 0);
        for (var d = 0; d < 7; d++)
        {
            var weekday = new TextBlock { Text = Culture.DateTimeFormat.GetShortestDayName((DayOfWeek)((d + 1) % 7)), Classes = { "calWeekday" } };
            if (d >= 5) weekday.Classes.Add("weekend");
            Add(grid, weekday, 0, d + 1);
        }
        var start = _month.AddDays(-(((int)_month.DayOfWeek + 6) % 7));   // Monday of the first week
        for (var week = 0; week < 6; week++)
        {
            var monday = start.AddDays(week * 7);
            Add(grid, new Border
            {
                Classes = { "calWeek" },
                Child = new TextBlock { Text = ISOWeek.GetWeekOfYear(monday).ToString(CultureInfo.InvariantCulture) },
            }, week + 1, 0);
            for (var d = 0; d < 7; d++)
            {
                var date = monday.AddDays(d);
                var button = new Button { Content = date.Day.ToString(CultureInfo.InvariantCulture), Classes = { "calDay" } };
                if (d >= 5) button.Classes.Add("weekend");
                if (date.Month != _month.Month) button.Classes.Add("other");
                if (date == DateTime.Today) button.Classes.Add("today");
                if (date == _focus && date != _selected) button.Classes.Add("focus");
                if (date == _selected) button.Classes.Add("selected");
                AutomationProperties.SetName(button, date.ToString("D", Culture));
                button.Click += (_, _) => DateChosen?.Invoke(date);
                Add(grid, button, week + 1, d + 1);
            }
        }
        return grid;
    }

    /// <summary>Months or years as 4 × 3 tiles.</summary>
    private static Grid Tiles(int count, Func<int, (string Text, bool Current)> label, Action<int> chosen)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            RowDefinitions = new RowDefinitions("*,*,*"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (var i = 0; i < count; i++)
        {
            var (text, current) = label(i);
            var index = i;
            var button = new Button { Content = text, Classes = { "calTile" } };
            if (current) button.Classes.Add("selected");
            button.Click += (_, _) => chosen(index);
            Add(grid, button, i / 4, i % 4);
        }
        return grid;
    }

    private static void Add(Grid grid, Control child, int row, int column)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _mode != Mode.Days) return;
        var moved = e.Key switch
        {
            Key.Left => _focus.AddDays(-1),
            Key.Right => _focus.AddDays(1),
            Key.Up => _focus.AddDays(-7),
            Key.Down => _focus.AddDays(7),
            Key.PageUp => _focus.AddMonths(-1),
            Key.PageDown => _focus.AddMonths(1),
            Key.Home => DateTime.Today,
            _ => (DateTime?)null,
        };
        if (moved is { } day && day >= First && day <= Last)
        {
            _focus = day;
            _month = new DateTime(day.Year, day.Month, 1);
            Build();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            DateChosen?.Invoke(_focus);
            e.Handled = true;
        }
    }
}
