using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.ViewModels;
using PdfMatrix = PdfKnead.Core.Pdf.Graphics.Matrix;

namespace PdfKnead.Controls;

/// <summary>
/// Editing formatted text in the page (see KONZEPT.md, section 5.14): typing happens directly in the block, which redraws after every
/// change exactly as it will be saved, with the selection and a blinking caret on top. Keys, text input (including dead keys) and mouse
/// go to the <see cref="RichTextEditor"/>; a click outside the block ends editing.
/// </summary>
public sealed partial class DocumentView
{
    private static readonly IBrush RichSelectionBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x3D, 0x7E, 0xFF));
    private DispatcherTimer? _caretTimer;
    private bool _caretVisible = true;
    private bool _richDragging;

    private RichTextEditor? Editor => _doc?.RichEditor;

    /// <summary>Text layout space (x from the left, y from the top) to view; null if the page is not shown at the moment.</summary>
    private PdfMatrix? RichToView(RichTextEditor editor)
    {
        var page = editor.Element.Page;
        if (page >= _pageRects.Count || !_shown[page]) return null;
        var element = editor.Current;
        var (_, h) = element.UprightSize;
        return new PdfMatrix(1, 0, 0, -1, 0, h) * ElementAppearance.For(element).UprightToPage() * PageToView(page);
    }

    /// <summary>Point of the view in the block's layout space (x, y from the top).</summary>
    private (double X, double Y)? ViewToRich(RichTextEditor editor, Point viewPoint)
    {
        if (RichToView(editor) is not { } m || !m.IsInvertible) return null;
        return m.Invert().Transform(viewPoint.X, viewPoint.Y);
    }

    private void DrawRichEditor(DrawingContext context)
    {
        if (Editor is not { } editor || RichToView(editor) is not { } m) return;
        var layout = editor.Layout;
        if (editor.HasSelection)
            foreach (var (left, top, right, bottom) in layout.Selection(editor.SelectionStart, editor.SelectionEnd))
                context.DrawGeometry(RichSelectionBrush, null, Quad(m, left, top, right, bottom));
        DrawPreedit(context);
        if (!_caretVisible) return;
        var (x, baseline, ascent, descent) = layout.Caret(editor.Caret);
        var (ax, ay) = m.Transform(x, baseline - ascent);
        var (bx, by) = m.Transform(x, baseline + descent);
        var color = Color.FromRgb((byte)(editor.Style.Color >> 16), (byte)(editor.Style.Color >> 8), (byte)editor.Style.Color);
        context.DrawLine(new Pen(new SolidColorBrush(color), Math.Max(1, Math.Min(2, editor.Style.Size * Scale * 0.08))), new Point(ax, ay), new Point(bx, by));
    }

    private static StreamGeometry Quad(PdfMatrix m, double left, double top, double right, double bottom)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        Point P(double x, double y)
        {
            var (vx, vy) = m.Transform(x, y);
            return new Point(vx, vy);
        }
        g.BeginFigure(P(left, top), true);
        g.LineTo(P(right, top));
        g.LineTo(P(right, bottom));
        g.LineTo(P(left, bottom));
        g.EndFigure(true);
        return geometry;
    }

    /// <summary>
    /// If the edited block lies under the panel on the right (<see cref="RightInset"/>), scrolls right until it stands clear to the left of the
    /// panel, but at most so far that its start stays visible.
    /// </summary>
    private void RevealEditor()
    {
        if (Editor is not { } editor || _rightInset <= 0 || (uint)editor.Element.Page >= (uint)_pageRects.Count) return;
        var r = editor.Current.Rect;
        var toView = PageToView(editor.Element.Page);
        double left = double.MaxValue, right = double.MinValue;
        foreach (var (x, y) in (ReadOnlySpan<(double, double)>)[(r.Left, r.Bottom), (r.Left, r.Top), (r.Right, r.Bottom), (r.Right, r.Top)])
        {
            var (vx, _) = toView.Transform(x, y);
            left = Math.Min(left, vx);
            right = Math.Max(right, vx);
        }
        const double margin = 12;
        var shift = Math.Min(right - (Bounds.Width - _rightInset - margin), left - margin);
        if (shift > 0) Offset += new Vector(shift, 0);
    }

    /// <summary>Editing started or ended: the caret blinks and the keyboard belongs to the block (or both stop).</summary>
    private void RichEditorChanged()
    {
        if (Editor is not null)
        {
            _caretTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Background, (_, _) =>
            {
                _caretVisible = !_caretVisible;
                InvalidateVisual();
            });
            _caretVisible = true;
            _caretTimer.Start();
            Focus();
            RevealEditor();
        }
        else
        {
            _caretTimer?.Stop();
            _richDragging = false;
        }
        RequeryInputClient();
        InvalidateVisual();
    }

    /// <summary>Text or caret changed: makes the caret visible (blinking restarts afterwards).</summary>
    private void RichEditorUpdated()
    {
        _caretVisible = true;
        _inputClient?.CaretMoved();
        _caretTimer?.Stop();
        _caretTimer?.Start();
        InvalidateVisual();
    }

    // ------------------------------------------------------------ Mouse

    /// <summary>Click while a block is being edited: inside the block it places the caret (double-click word, triple-click paragraph, Shift
    /// extends); outside, editing ends (false: the click is handled further).</summary>
    private bool RichEditPressed(PointerPressedEventArgs e, Point position)
    {
        if (Editor is not { } editor || _doc is not { } doc) return false;
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return false;
        // Alt in „Bearbeiten“: the click chooses what lies under the block (KONZEPT.md, section 5.22), not a place in it
        if (doc.Tool == ViewTool.Edit && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
        // Handles and border: change the width or move the block (editing ends afterwards).
        if (HandleAt(position) is not null || OnElementBorder(editor.Element, position)) return false;
        var (w, h) = editor.Current.UprightSize;
        if (ViewToRich(editor, position) is not { } p || p.X < 0 || p.X > w || p.Y < 0 || p.Y > h)
        {
            doc.EndRichEdit();
            return false;
        }
        var caret = editor.Layout.HitTest(p.X, p.Y);
        switch (e.ClickCount)
        {
            case 2:
                editor.SelectWord(caret);
                break;
            case >= 3:
                editor.SelectParagraph(caret);
                break;
            default:
                editor.SetCaret(caret, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                _richDragging = true;
                e.Pointer.Capture(this);
                break;
        }
        return true;
    }

    /// <summary>Dragging with the button pressed selects; without a button, the text cursor is shown over the block.</summary>
    private bool RichEditMoved(Point position)
    {
        if (Editor is not { } editor) return false;
        if (_richDragging && ViewToRich(editor, position) is { } p)
        {
            editor.SetCaret(editor.Layout.HitTest(p.X, p.Y), extend: true);
            return true;
        }
        if (HandleAt(position) is null && !OnElementBorder(editor.Element, position) && ViewToRich(editor, position) is { } q)
        {
            var (w, h) = editor.Current.UprightSize;
            if (q.X >= -4 && q.X <= w + 4 && q.Y >= -4 && q.Y <= h + 4)
            {
                Cursor = TextCursor;
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether the point lies just outside the block (within 4 user interface units), where it is grabbed for moving.</summary>
    private bool OnElementBorder(ElementRef element, Point viewPoint)
    {
        if (element.Page >= _pageRects.Count || !_shown[element.Page]) return false;
        var rect = ViewRect(element.Page, element.Element.Rect);
        return !rect.Contains(viewPoint) && rect.Inflate(4).Contains(viewPoint);
    }

    private bool RichEditReleased(PointerReleasedEventArgs e)
    {
        if (!_richDragging) return false;
        _richDragging = false;
        e.Pointer.Capture(null);
        return true;
    }

    // ------------------------------------------------------------ Input methods

    private RichInputClient? _inputClient;
    private string? _preedit;

    /// <summary>
    /// The system's input method (IME for Chinese, Japanese, Korean …; on Linux also dead keys via IBus/Fcitx): while editing, the page is its
    /// input field; the candidate window appears at the caret, and the text not yet committed appears underlined there.
    /// </summary>
    private void OnInputClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        if (Editor is null) return;
        e.Client = _inputClient ??= new RichInputClient(this);
    }

    /// <summary>Editing started or ended: the input method asks again whether the page is currently an input field.</summary>
    private void RequeryInputClient()
    {
        _preedit = null;
        if (IsFocused) RaiseEvent(new TextInputMethodClientRequeryRequestedEventArgs { RoutedEvent = InputMethod.TextInputMethodClientRequeryRequestedEvent });
    }

    /// <summary>Caret rectangle in the view (for the candidate window of the input method).</summary>
    private Rect CaretRect()
    {
        if (Editor is not { } editor || RichToView(editor) is not { } m) return default;
        var (x, baseline, ascent, descent) = editor.Layout.Caret(editor.Caret);
        var (ax, ay) = m.Transform(x, baseline - ascent);
        var (bx, by) = m.Transform(x, baseline + descent);
        return new Rect(new Point(Math.Min(ax, bx), Math.Min(ay, by)), new Point(Math.Max(ax, bx) + 1, Math.Max(ay, by)));
    }

    /// <summary>Draws the input method's uncommitted text: underlined at the caret, in the size and colour of the text there.</summary>
    private void DrawPreedit(DrawingContext context)
    {
        if (_preedit is not { Length: > 0 } preedit || Editor is not { } editor) return;
        var caret = CaretRect();
        if (caret.Height <= 0) return;
        var style = editor.Style;
        var color = Color.FromRgb((byte)(style.Color >> 16), (byte)(style.Color >> 8), (byte)style.Color);
        var text = new FormattedText(preedit, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default,
            Math.Max(6, caret.Height * 0.8), new SolidColorBrush(color));
        var origin = new Point(caret.X, caret.Bottom - text.Height);
        context.FillRectangle(Brushes.White, new Rect(origin, new Size(text.Width, text.Height)));
        context.DrawText(text, origin);
        context.DrawLine(new Pen(new SolidColorBrush(color), 1), new Point(origin.X, caret.Bottom), new Point(origin.X + text.Width, caret.Bottom));
    }

    /// <summary>The page as input field for the input method (without surrounding text: the block is not an ordinary text box).</summary>
    private sealed class RichInputClient(DocumentView view) : TextInputMethodClient
    {
        public override Visual TextViewVisual => view;

        public override bool SupportsPreedit => true;

        public override bool SupportsSurroundingText => false;

        public override string SurroundingText => "";

        public override Rect CursorRectangle => view.CaretRect();

        public override TextSelection Selection
        {
            get => new(0, 0);
            set { }
        }

        public override void SetPreeditText(string? preeditText)
        {
            view._preedit = preeditText;
            view.InvalidateVisual();
        }

        /// <summary>Caret moved: the candidate window follows.</summary>
        public void CaretMoved() => RaiseCursorRectangleChanged();
    }

    // ------------------------------------------------------------ Keyboard

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (Editor is not { } editor || e.Text is not { Length: > 0 } text) return;
        // Control characters arrive via the keys (Enter, Backspace, Ctrl+letter); do not handle them twice.
        if (text.All(char.IsControl)) return;
        _preedit = null;   // committed: the input method's preliminary text is now real text
        editor.Type(text);
        e.Handled = true;
    }

    /// <summary>Handles keys in the block; true = handled.</summary>
    private bool RichKey(RichTextEditor editor, KeyEventArgs e)
    {
        // Windows reports AltGr as Ctrl+Alt: that is a character (Polish AltGr+A “ą”, AltGr+C “ć”), not a shortcut.
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Left: editor.MoveLeft(shift, ctrl); return true;
            case Key.Right: editor.MoveRight(shift, ctrl); return true;
            case Key.Up: editor.MoveVertical(-1, shift); return true;
            case Key.Down: editor.MoveVertical(1, shift); return true;
            case Key.Home: editor.MoveHome(shift, ctrl); return true;
            case Key.End: editor.MoveEnd(shift, ctrl); return true;
            case Key.Back: editor.Backspace(ctrl); return true;
            case Key.Delete: editor.Delete(ctrl); return true;
            case Key.Enter: editor.NewParagraph(); return true;
            case Key.Escape:
                _doc?.EndRichEdit();
                return true;
            case Key.Space: return true;   // arrives as text input; the page must not scroll
            case Key.A when ctrl: editor.SelectAll(); return true;
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                editor.Redo();
                return true;
            case Key.Z when ctrl: editor.Undo(); return true;
            case Key.B when ctrl: editor.Toggle(s => s.Bold, (s, on) => s with { Bold = on }); return true;
            case Key.I when ctrl: editor.Toggle(s => s.Italic, (s, on) => s with { Italic = on }); return true;
            case Key.U when ctrl: editor.Toggle(s => s.Underline, (s, on) => s with { Underline = on }); return true;
            case Key.C when ctrl:
                CopyRich(editor, cut: false);
                return true;
            case Key.X when ctrl:
                CopyRich(editor, cut: true);
                return true;
            case Key.V when ctrl:
                PasteRich(editor);
                return true;
            // Tab in the text (does not arrive as text input, being a control character); otherwise focus would leave the document.
            // Shift+Tab types nothing but is taken as well, for the same reason (the editing would end without a word).
            case Key.Tab when !ctrl:
                if (!shift) editor.Type("\t");
                return true;
            // Other Ctrl shortcuts go to the window (save, print, search; saving ends editing itself); they do not arrive as
            // characters (control characters, OnTextInput).
            default: return false;
        }
    }

    // Text last copied here, with formatting: if the clipboard still holds the same text, it is pasted back formatted.
    private static (string Plain, RichText Rich)? _richClipboard;

    private async void CopyRich(RichTextEditor editor, bool cut)
    {
        if (!editor.HasSelection || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var plain = editor.SelectedText.Replace("\n", Environment.NewLine);
        _richClipboard = (plain, editor.SelectedRich);
        if (cut) editor.DeleteSelection();
        try
        {
            await clipboard.SetTextAsync(plain);
        }
        catch (Exception ex)
        {
            Core.Platform.AppLog.Write("Zwischenablage", ex.ToString());
        }
    }

    private async void PasteRich(RichTextEditor editor)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync();
        }
        catch (Exception ex)
        {
            Core.Platform.AppLog.Write("Zwischenablage", ex.ToString());
            return;
        }
        if (string.IsNullOrEmpty(text) || !ReferenceEquals(Editor, editor)) return;
        if (_richClipboard is { } own && own.Plain == text) editor.Paste(own.Rich);
        else editor.Type(text);
    }
}
