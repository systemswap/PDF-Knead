using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PdfKnead.Views;

/// <summary>„OCR anzeigen“: the whole text recognition of a document to read; copying only where the document allows it.</summary>
public partial class OcrTextWindow : Window
{
    private readonly string _text = "";
    private readonly Func<string, Task>? _copy;

    public OcrTextWindow() => InitializeComponent();

    /// <param name="title">Title of the window.</param>
    /// <param name="text">The text.</param>
    /// <param name="copyAllowed">Whether the document allows copying its text.</param>
    /// <param name="copy">Puts text on the clipboard; null without one.</param>
    public OcrTextWindow(string title, string text, bool copyAllowed, Func<string, Task>? copy) : this()
    {
        Title = title;
        _text = text;
        _copy = copyAllowed ? copy : null;
        // with the copy restriction of the document the text can be read but not selected
        Selectable.Text = copyAllowed ? text : null;
        Selectable.IsVisible = copyAllowed;
        Plain.Text = copyAllowed ? null : text;
        Plain.IsVisible = !copyAllowed;
        CopyButton.IsVisible = _copy is not null;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (_copy is not null) await _copy(_text);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
