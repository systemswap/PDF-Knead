using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.ViewModels;

namespace PdfKnead.Controls;

/// <summary>
/// <see cref="DocumentView"/> with scroll bars on the right and at the bottom, and the input box in which
/// the text of an element is edited (it lies exactly over the element, in its font).
/// </summary>
public sealed class DocumentViewer : Grid
{
    public static readonly StyledProperty<DocumentViewModel?> DocumentProperty =
        AvaloniaProperty.Register<DocumentViewer, DocumentViewModel?>(nameof(Document));

    private readonly DocumentView _view = new();
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical, AllowAutoHide = true };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, AllowAutoHide = true };
    private readonly Canvas _overlay = new() { ClipToBounds = true };
    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true,
        AcceptsTab = false,
        MinWidth = 0,
        MinHeight = 0,
        Background = Brushes.Transparent,
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x7E, 0xFF)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(0),
        VerticalContentAlignment = VerticalAlignment.Top,
        IsVisible = false,
        RenderTransformOrigin = RelativePoint.Center,
    };
    private DocumentViewModel? _doc;
    private bool _syncing;
    private bool _ending;

    public DocumentViewer()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        RowDefinitions = new RowDefinitions("*,Auto");
        Children.Add(_view);
        Children.Add(_overlay);
        SetColumn(_vertical, 1);
        SetRow(_horizontal, 1);
        Children.Add(_vertical);
        Children.Add(_horizontal);
        _overlay.Children.Add(_editor);
        _editor.Classes.Add("element-editor");
        // The input box stays transparent (otherwise the theme colours it on focus and under the mouse).
        foreach (var state in new[] { ":focus", ":pointerover" })
            Styles.Add(new Style(x => x.OfType<TextBox>().Class("element-editor").Class(state).Template().OfType<Border>().Name("PART_BorderElement"))
            {
                Setters = { new Setter(Border.BackgroundProperty, Brushes.Transparent) },
            });

        _view.OffsetChanged += SyncBars;
        _view.OffsetChanged += PlaceEditor;
        _vertical.ValueChanged += (_, e) => Scroll(new Vector(_view.Offset.X, e.NewValue));
        _horizontal.ValueChanged += (_, e) => Scroll(new Vector(e.NewValue, _view.Offset.Y));
        Avalonia.Automation.AutomationProperties.SetName(_editor, Tr.T("Text auf der Seite"));
        _editor.LostFocus += (_, _) => EndEdit();
        _editor.KeyDown += OnEditorKeyDown;
        _editor.AddHandler(KeyDownEvent, OnEditorPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // When filling in: the font is as large as in the finished field already while typing (“Automatic”, “Shrink font automatically”).
        _editor.TextChanged += (_, _) =>
        {
            if (!_editor.IsVisible || _doc?.FillFontSize(_editor.Text ?? "") is not { } size || Math.Abs(size - (_liveFontSize ?? 0)) < 0.01) return;
            _liveFontSize = size;
            PlaceEditor();
        };
    }

    // Font size of the field for the text being typed (null: that of the element).
    private double? _liveFontSize;

    public DocumentViewModel? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>Gets the inner document view.</summary>
    public DocumentView View => _view;

    /// <summary>The input box (for tests).</summary>
    internal TextBox Editor => _editor;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != DocumentProperty) return;
        EndEdit();
        if (_doc is not null)
        {
            _doc.PropertyChanged -= OnDocumentChanged;
            _doc.CommitEditRequested -= EndEdit;
        }
        _doc = change.GetNewValue<DocumentViewModel?>();
        if (_doc is not null)
        {
            _doc.PropertyChanged += OnDocumentChanged;
            _doc.CommitEditRequested += EndEdit;
        }
        _view.Document = _doc;
        ShowEditor();
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.EditingElement):
                ShowEditor();
                break;
            case nameof(DocumentViewModel.Zoom) or nameof(DocumentViewModel.Rotation):
                Dispatcher.UIThread.Post(PlaceEditor, DispatcherPriority.Render);
                break;
        }
    }

    // ------------------------------------------------------------ Editing text

    private void ShowEditor()
    {
        if (_doc?.EditingElement is not { Element: TextElement element })
        {
            _editor.IsVisible = false;
            return;
        }
        _liveFontSize = null;   // new input box: start with the element's size
        _editor.Text = element.Text;
        _editor.FontFamily = element.Font switch
        {
            // Liberation has the metrics of Arial, Times and Courier, so the box wraps lines almost identically.
            ElementFont.Serif => new FontFamily("Times New Roman, Liberation Serif, serif"),
            ElementFont.Mono => new FontFamily("Courier New, Liberation Mono, monospace"),
            _ => new FontFamily("Arial, Liberation Sans, Helvetica, sans-serif"),
        };
        _editor.Foreground = new SolidColorBrush(Color.FromRgb((byte)(element.Color >> 16), (byte)(element.Color >> 8), (byte)element.Color));
        _editor.TextAlignment = element.Alignment switch
        {
            ElementAlignment.Center => TextAlignment.Center,
            ElementAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        _editor.TextWrapping = element.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        // Filling in a field of another program: maximum length, password, opaque over the old value, single-line without line breaks.
        var field = _doc.FillingField is { Element: var filling } ? _doc.SpecOf(filling) : null;
        _editor.MaxLength = field?.MaxLength ?? 0;
        _editor.PasswordChar = field is { Password: true } ? '*' : default;
        _editor.AcceptsReturn = field is null || field is { Kind: FormFieldKind.Text, Multiline: true };
        _editor.Background = field is null ? Brushes.Transparent : Brushes.White;
        _editor.IsVisible = true;
        PlaceEditor();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_editor.IsVisible) return;
            _editor.Focus();
            _editor.CaretIndex = _editor.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    /// <summary>Places the input box over the element (again after scrolling and zooming).</summary>
    private void PlaceEditor()
    {
        if (!_editor.IsVisible || _doc?.EditingElement is not { Element: TextElement element }) return;
        if (_view.EditorGeometry() is not { } g)
        {
            // Page turned away (single/two-page view): commit as with a click outside, instead of staying in place.
            EndEdit();
            return;
        }
        var (width, height) = element.UprightSize;
        var w = width * g.Scale;
        var h = height * g.Scale;
        var size = (_liveFontSize ?? element.FontSize) * g.Scale;
        _editor.FontSize = Math.Max(1, size);
        _editor.LineHeight = size * ElementAppearance.LineStep(element, ElementFace.For(element.Font));   // as the appearance spaces the lines (No. 1211)
        var pad = ElementAppearance.Padding * g.Scale;
        _editor.Padding = new Thickness(pad - 1, pad - 1, pad - 1, 0);   // 1 = border
        _editor.Width = w;
        _editor.MinHeight = h;
        _editor.RenderTransform = g.Turn == 0 ? null : new RotateTransform(g.Turn);
        // The centre stays and the box is rotated around it (at 90°/270° width and height swap in the view).
        Canvas.SetLeft(_editor, g.Box.Center.X - w / 2);
        Canvas.SetTop(_editor, g.Box.Center.Y - h / 2);
    }

    /// <summary>Gives keyboard focus to the page (after a change in the panel on the right, typing continues in the edited text).</summary>
    public void FocusPage() => _view.Focus();

    /// <summary>When filling in, before the text box: Tab commits and moves to the next field, Enter commits single-line fields.</summary>
    private void OnEditorPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_doc?.FillingField is null) return;
        if (e.Key == Key.Tab)
        {
            EndEdit();
            _view.Focus();
            _view.FocusNextField(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !_editor.AcceptsReturn)
        {
            EndEdit();
            _view.Focus();
            e.Handled = true;
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc or Ctrl+Enter: done (the text is kept).
        if (e.Key == Key.Escape || (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            EndEdit();
            _view.Focus();
            e.Handled = true;
        }
    }

    private void EndEdit()
    {
        if (_ending || _doc?.EditingElement is null) return;
        _ending = true;
        try
        {
            var text = (_editor.Text ?? "").Replace("\r\n", "\n");
            _editor.IsVisible = false;
            _doc.EndEdit(text);
        }
        finally
        {
            _ending = false;
        }
    }

    // ------------------------------------------------------------ Scrolling

    private void Scroll(Vector offset)
    {
        if (_syncing) return;
        _view.Offset = offset;
    }

    private void SyncBars()
    {
        _syncing = true;
        var extent = _view.Extent;
        var size = _view.Bounds.Size;
        _vertical.Maximum = Math.Max(0, extent.Height - size.Height);
        _vertical.ViewportSize = size.Height;
        _vertical.LargeChange = size.Height * 0.9;
        _vertical.SmallChange = 50;
        _vertical.Value = _view.Offset.Y;
        _vertical.IsVisible = extent.Height > size.Height + 0.5;
        _horizontal.Maximum = Math.Max(0, extent.Width - size.Width);
        _horizontal.ViewportSize = size.Width;
        _horizontal.LargeChange = size.Width * 0.9;
        _horizontal.SmallChange = 50;
        _horizontal.Value = _view.Offset.X;
        _horizontal.IsVisible = extent.Width > size.Width + 0.5;
        _syncing = false;
    }
}
