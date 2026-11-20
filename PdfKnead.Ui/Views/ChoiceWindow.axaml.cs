using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>
/// A question with one or more answers and a way back, optionally with a box to tick (<see cref="ChoiceRequest"/>). The first answer is the
/// default one (Enter, highlighted, rightmost); Esc and the close button take the way back. Result: <see cref="ChoiceAnswer"/> — or, through
/// the older constructor, 1 or 2 for the answers and 0 for „Abbrechen“.
/// </summary>
public partial class ChoiceWindow : Window
{
    public ChoiceWindow() => InitializeComponent();

    /// <param name="heading">The question.</param>
    /// <param name="message">What the answers mean.</param>
    /// <param name="first">The default answer (highlighted).</param>
    /// <param name="second">The other answer; null when it would only repeat „Abbrechen“ (the button is not shown).</param>
    public ChoiceWindow(string heading, string message, string first, string? second)
        : this(new ChoiceRequest(heading, message, second is null ? [first] : [first, second], Tr.T("Abbrechen"), Icon: "Icon.Merge"))
    {
        _numbers = true;
    }

    /// <param name="request">The question, its answers, the way back and the box to tick.</param>
    public ChoiceWindow(ChoiceRequest request) : this()
    {
        Title = request.Heading;
        HeadingText.Text = request.Heading;
        MessageText.Text = request.Message;
        CancelButton.Content = request.Cancel;
        if (request.Check is { } check)
        {
            CheckBox.Content = check;
            CheckBox.IsVisible = true;
        }
        if ((request.Icon ?? "Icon.Info") is var key && this.TryFindResource(key, out var icon) && icon is Geometry geometry) HeadingIcon.Data = geometry;
        // the answers right of the way back, the default one rightmost (as before: Abbrechen, second, first)
        for (var i = request.Answers.Count - 1; i >= 0; i--)
        {
            var number = i + 1;
            var button = new Button { Content = request.Answers[i], Name = $"Answer{number}" };
            if (number == 1)
            {
                button.Classes.Add("primary");
                button.IsDefault = true;
            }
            button.Click += (_, _) => Answer(number);
            Buttons.Children.Add(button);
        }
    }

    /// <summary>Called through the older constructor: the result is the number of the answer alone (<see cref="int"/>).</summary>
    private readonly bool _numbers;

    /// <summary>A file name for a button, shortened in the middle if it is long („Quartalsbericht Vertr…und langem Namen.pdf“).</summary>
    /// <param name="name">The file name.</param>
    /// <returns>At most 40 characters.</returns>
    public static string ShortName(string name) => name.Length <= 40 ? name : name[..22] + "…" + name[^17..];

    private void Answer(int number)
    {
        if (_numbers) Close(number);
        else Close(new ChoiceAnswer(number, CheckBox.IsChecked == true));
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_numbers) Close(0);
        else Close(new ChoiceAnswer(0, CheckBox.IsChecked == true));
    }
}
