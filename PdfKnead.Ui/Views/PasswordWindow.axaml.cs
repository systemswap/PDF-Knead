using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PdfKnead.Views;

/// <summary>Asks for the password of an encrypted file (or of the own certificate). Result: the password, or null if cancelled.</summary>
public partial class PasswordWindow : Window
{
    public PasswordWindow() => InitializeComponent();

    public PasswordWindow(string fileName, bool wrongPassword) : this()
    {
        FileText.Text = fileName;
        WrongText.IsVisible = wrongPassword;
        Opened += (_, _) => PasswordBox.Focus();
    }

    /// <summary>Asks for another password than that of a file (the own certificate): its heading, a line below it and the button.</summary>
    /// <param name="heading">The heading (translated).</param>
    /// <param name="detail">The line below it (translated).</param>
    /// <param name="button">The text of the button that confirms (translated).</param>
    /// <param name="wrongPassword">The password given before was wrong.</param>
    public PasswordWindow(string heading, string detail, string button, bool wrongPassword) : this(detail, wrongPassword)
    {
        HeadingText.Text = heading;
        OkButton.Content = button;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(PasswordBox.Text ?? "");

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
