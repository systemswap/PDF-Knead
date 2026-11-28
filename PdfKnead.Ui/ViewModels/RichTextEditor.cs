using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.ViewModels;

/// <summary>
/// Editor for formatted text, without UI: the view draws caret and selection and passes keys and clicks in. Caret and anchor are
/// positions in the text (UTF-16 indices); there is a pending format for what is typed next (Ctrl+B without a selection). Every
/// change replaces the element in the document; the whole edit is one step there (<see cref="DocumentViewModel.ReplaceElement"/>
/// with continuation), while within the edit Ctrl+Z undoes individual changes (up to 500). See KONZEPT.md, section 5.14.
/// </summary>
public sealed class RichTextEditor
{
    /// <summary>Description of the step in the history.</summary>
    public static string Description => Tr.T("Text bearbeiten");

    private readonly DocumentViewModel _doc;
    private readonly List<(RichText Text, int Caret, int Anchor)> _undo = [];
    private readonly List<(RichText Text, int Caret, int Anchor)> _redo = [];
    private RichStyle? _pending;
    private RichLayout? _layout;
    private double? _goalX;   // arrow up/down: stay in the column where the movement started
    private bool _typing;     // consecutive typing is one step for undo within the editor

    /// <summary>Creates an editor for a rich text element.</summary>
    /// <param name="doc">The document containing the element.</param>
    /// <param name="element">The rich text element.</param>
    /// <param name="caret">Initial caret position, clamped to the text.</param>
    public RichTextEditor(DocumentViewModel doc, ElementRef element, int caret = 0)
    {
        _doc = doc;
        Element = element;
        Caret = Anchor = Math.Clamp(caret, 0, Current.Content.Length);
    }

    /// <summary>The edited element (replaced with every change).</summary>
    public ElementRef Element { get; private set; }

    public RichTextElement Current => (RichTextElement)Element.Element;

    public int Caret { get; private set; }

    /// <summary>Other end of the selection (equal to <see cref="Caret"/>: no selection).</summary>
    public int Anchor { get; private set; }

    public bool HasSelection => Caret != Anchor;

    public int SelectionStart => Math.Min(Caret, Anchor);

    public int SelectionEnd => Math.Max(Caret, Anchor);

    /// <summary>Whether a change is in progress (the document raises its notifications before the new element is stored here).</summary>
    public bool Applying { get; private set; }

    /// <summary>Whether anything has been changed since editing began.</summary>
    public bool Changed { get; private set; }

    /// <summary>
    /// For a page block that has just been converted: the state after the conversion. Without changes, the block is restored as it was
    /// when editing ends (<see cref="Core.Pdf.Editing.PageHistory.Retract"/>); null for a block that already existed.
    /// </summary>
    public IReadOnlyList<Core.Pdf.Editing.PageItem>? Converted { get; set; }

    /// <summary>Raised when caret, selection or text change; the view redraws and the right-hand panel rereads the formatting.</summary>
    public event Action? Updated;

    /// <summary>Layout of the current text (computed once per state).</summary>
    public RichLayout Layout => _layout ??= RichLayout.Of(Current);

    /// <summary>Formatting at the caret or at the start of the selection (for the buttons).</summary>
    public RichStyle Style => HasSelection ? Current.Content.StylesIn(SelectionStart, SelectionEnd - SelectionStart).First() : _pending ?? Current.Content.StyleAt(Caret);

    /// <summary>Checks whether a property holds for the whole selection (bold only if everything is bold).</summary>
    /// <param name="property">The property to test.</param>
    /// <returns>true if it holds for the whole selection, or for <see cref="Style"/> without a selection.</returns>
    public bool All(Func<RichStyle, bool> property) =>
        HasSelection ? Current.Content.StylesIn(SelectionStart, SelectionEnd - SelectionStart).All(property) : property(Style);

    public string SelectedText => Current.Content.Text[SelectionStart..SelectionEnd];

    public RichText SelectedRich => Current.Content.Slice(SelectionStart, SelectionEnd - SelectionStart);

    // ------------------------------------------------------------ Writing

    /// <summary>Typed (or pasted plain) text: replaces the selection, in the formatting at that position.</summary>
    /// <param name="text">The text; line endings and the separators among the control codes are taken as breaks, other control characters removed
    /// (<see cref="EditUnits.Clean"/>); its directional formatting characters are paired on their own (<see cref="EditUnits.Balanced"/>).</param>
    public void Type(string text)
    {
        text = EditUnits.Balanced(EditUnits.Clean(text));
        if (text.Length == 0) return;
        var style = _pending ?? Current.Content.StyleAt(SelectionStart);
        var content = WithoutOrphans(Current.Content, SelectionStart, SelectionEnd - SelectionStart, out var start);
        Apply(content.Replace(start, SelectionEnd - SelectionStart, text, style), start + text.Length, typing: !HasSelection && text.Length == 1);
        _pending = null;
    }

    /// <summary>
    /// Pastes formatted text (copied within this program). Spans using a font of another file (from another tab) are drawn with their
    /// family instead, because the other file may be closed, and drawing and saving would otherwise read from it.
    /// </summary>
    /// <param name="text">The formatted text.</param>
    public void Paste(RichText text)
    {
        var home = (uint)Element.Page < (uint)_doc.History.Count ? _doc.History.Pages[Element.Page].Source : null;
        if (text.Spans.Any(s => s.Style.Original is { } o && !ReferenceEquals(o.Document, home)))
            text = new RichText(text.Spans.Select(s => s.Style.Original is { } o && !ReferenceEquals(o.Document, home)
                ? s with { Style = s.Style with { Original = null } }
                : s));
        text = BalancedRich(text);
        var content = WithoutOrphans(Current.Content, SelectionStart, SelectionEnd - SelectionStart, out var start);
        Apply(content.Replace(start, SelectionEnd - SelectionStart, text), start + text.Length);
    }

    /// <summary>Starts a new paragraph (Enter).</summary>
    public void NewParagraph() => Type("\n");

    /// <summary>
    /// Backspace: deletes the selection or the character (with Ctrl the word) before the caret. After Arabic marks it deletes the outermost
    /// mark as they are stacked (UAX #53, 5.6) — beh, fatha, shadda loses the fatha, which stands outside the shadda — rather than the one typed
    /// last; before, it deleted in the order of the text (docs/FEHLERGEDAECHTNIS.md, No. 1180).
    /// </summary>
    /// <param name="word">true = delete the word (Ctrl).</param>
    public void Backspace(bool word)
    {
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }
        if (Caret == 0) return;
        if (!word && Core.Fonts.Layout.TextShaper.OutermostMark(Current.Content.Text, Caret) is { } outer)
        {
            var content = Current.Content;
            Apply(outer.Replacement.Length == 0
                ? content.Delete(outer.Start, outer.Length)
                : content.Replace(outer.Start, outer.Length, outer.Replacement, content.StyleAt(outer.Start + outer.Length)),
                Caret - outer.Length + outer.Replacement.Length);
            return;
        }
        // a mark alone, a consonant after a virama or coeng together with it (EditUnits.PreviousForDeletion; No. 1185)
        var from = word ? WordStart(Caret) : EditUnits.PreviousForDeletion(Current.Content.Text, Caret);
        var remaining = WithoutOrphans(Current.Content, from, Caret - from, out var at);
        Apply(remaining.Delete(at, Caret - from), at);
    }

    /// <summary>Delete: deletes the selection or the character (with Ctrl the word) after the caret.</summary>
    /// <param name="word">true = delete the word (Ctrl).</param>
    public void Delete(bool word)
    {
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }
        var text = Current.Content.Text;
        if (Caret >= text.Length) return;
        var to = word ? WordEnd(Caret) : NextStop(Caret);
        var content = WithoutOrphans(Current.Content, Caret, to - Caret, out var at);
        Apply(content.Delete(at, to - Caret), at);
    }

    /// <summary>Deletes the selection, if any.</summary>
    public void DeleteSelection()
    {
        if (!HasSelection) return;
        var content = WithoutOrphans(Current.Content, SelectionStart, SelectionEnd - SelectionStart, out var start);
        Apply(content.Delete(start, SelectionEnd - SelectionStart), start);
    }

    /// <summary>
    /// The text without the partners, outside a range about to be deleted or replaced, of the paired directional formatting characters inside it
    /// (Unicode 23.2.5: a text process that changes text keeps the pairs; before an unpaired PDF or PDI stayed behind; docs/FEHLERGEDAECHTNIS.md,
    /// No. 1185). <paramref name="newStart"/>: where the range starts in the returned text.
    /// </summary>
    private static RichText WithoutOrphans(RichText content, int start, int length, out int newStart)
    {
        newStart = start;
        foreach (var place in EditUnits.OrphanedPartners(content.Text, start, length).OrderByDescending(p => p))
        {
            content = content.Delete(place, 1);
            if (place < start) newStart--;
        }
        return content;
    }

    /// <summary>Pasted formatted text with its directional formatting characters paired on their own (<see cref="EditUnits.Balanced"/>).</summary>
    private static RichText BalancedRich(RichText text)
    {
        var balanced = EditUnits.Balanced(text.Text);
        if (balanced == text.Text) return text;
        // the unpaired closing ones dropped, the missing closing ones appended in the style of the end
        var kept = new List<int>();
        int i = 0, j = 0;
        var original = text.Text;
        while (i < original.Length)
        {
            if (j < balanced.Length && original[i] == balanced[j])
            {
                i++;
                j++;
            }
            else
            {
                kept.Add(i++);
            }
        }
        for (var k = kept.Count - 1; k >= 0; k--) text = text.Delete(kept[k], 1);
        if (j < balanced.Length && text.Length > 0) text = text.Replace(text.Length, 0, balanced[j..], text.StyleAt(text.Length - 1));
        return text;
    }

    /// <summary>
    /// Changes the formatting of the selection or, without one, of what is typed next. A different family, bold or italic detaches the
    /// text from the file's font (<see cref="RichStyle.Settled"/>); otherwise it would stay in the old style.
    /// </summary>
    /// <param name="requested">The requested change.</param>
    public void Format(Func<RichStyle, RichStyle> requested)
    {
        RichStyle change(RichStyle s) => requested(s).Settled(s);
        if (!HasSelection)
        {
            _pending = change(Style);
            Updated?.Invoke();
            return;
        }
        var (caret, anchor) = (Caret, Anchor);
        Apply(Current.Content.Format(SelectionStart, SelectionEnd - SelectionStart, change), caret, anchor);
    }

    /// <summary>Bold, italic, underline, strikeout: switches on if it does not yet apply everywhere, otherwise off (as in office programs).</summary>
    /// <param name="property">Reads the property.</param>
    /// <param name="set">Sets the property to the given value.</param>
    public void Toggle(Func<RichStyle, bool> property, Func<RichStyle, bool, RichStyle> set)
    {
        var on = !All(property);
        Format(s => set(s, on));
    }

    /// <summary>Changes the whole block (alignment and line spacing apply to the whole block).</summary>
    /// <param name="change">The change.</param>
    public void SetBlock(Func<RichTextElement, RichTextElement> change)
    {
        var updated = RichLayout.Fitted(change(Current));
        Replace(updated);
    }

    // ------------------------------------------------------------ Undo within the editor

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Undoes the last change within the editor.</summary>
    public void Undo()
    {
        if (_undo.Count == 0) return;
        var (text, caret, anchor) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((Current.Content, Caret, Anchor));
        Apply(text, caret, anchor, snapshot: false);
    }

    /// <summary>Redoes the last undone change within the editor.</summary>
    public void Redo()
    {
        if (_redo.Count == 0) return;
        var (text, caret, anchor) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((Current.Content, Caret, Anchor));
        Apply(text, caret, anchor, snapshot: false);
    }

    // ------------------------------------------------------------ Moving and selecting

    /// <summary>Sets the caret.</summary>
    /// <param name="caret">New position, clamped to the text.</param>
    /// <param name="extend">true = extend the selection to it (Shift).</param>
    /// <param name="keepGoal">true = keep the column for vertical movement.</param>
    public void SetCaret(int caret, bool extend = false, bool keepGoal = false)
    {
        caret = Math.Clamp(caret, 0, Current.Content.Length);
        Caret = caret;
        if (!extend) Anchor = caret;
        if (!keepGoal) _goalX = null;
        _pending = null;
        _typing = false;
        Updated?.Invoke();
    }

    /// <summary>Selects a range.</summary>
    /// <param name="start">Anchor position.</param>
    /// <param name="end">Caret position.</param>
    public void Select(int start, int end)
    {
        Anchor = Math.Clamp(start, 0, Current.Content.Length);
        Caret = Math.Clamp(end, 0, Current.Content.Length);
        _goalX = null;
        _pending = null;
        _typing = false;
        Updated?.Invoke();
    }

    /// <summary>Selects the whole text.</summary>
    public void SelectAll() => Select(0, Current.Content.Length);

    /// <summary>Selects the word at the position (double-click); outside a word, the character there.</summary>
    /// <param name="at">Text position.</param>
    public void SelectWord(int at)
    {
        var text = Current.Content.Text;
        at = Math.Clamp(at, 0, text.Length);
        if (text.Length == 0) return;
        var probe = at < text.Length && EditUnits.IsWordAt(text, at) ? at : at > 0 && EditUnits.IsWordBefore(text, at, out var back) ? at - back : -1;
        if (probe < 0)
        {
            Select(at, EditUnits.Next(text, at));
            return;
        }
        int start = probe, end = probe;
        while (start > 0 && EditUnits.IsWordBefore(text, start, out var length)) start -= length;
        while (end < text.Length && EditUnits.IsWordAt(text, end)) end += char.IsSurrogatePair(text, end) ? 2 : 1;
        Select(start, end);
    }

    /// <summary>Selects the paragraph at the position (triple-click).</summary>
    /// <param name="at">Text position.</param>
    public void SelectParagraph(int at)
    {
        var text = Current.Content.Text;
        at = Math.Clamp(at, 0, text.Length);
        var start = at > 0 ? text.LastIndexOf('\n', at - 1) + 1 : 0;
        var end = text.IndexOf('\n', at);
        Select(start, end < 0 ? text.Length : end);
    }

    /// <summary>Arrow left: one character (or word) back; with a selection and without <paramref name="extend"/>, to its start.</summary>
    /// <param name="extend">true = extend the selection (Shift).</param>
    /// <param name="word">true = by word (Ctrl).</param>
    public void MoveLeft(bool extend, bool word)
    {
        if (HasSelection && !extend)
        {
            SetCaret(SelectionStart);
            return;
        }
        SetCaret(word ? WordStart(Caret) : PreviousStop(Caret), extend);
    }

    /// <summary>Arrow right: one character (or word) on; with a selection and without <paramref name="extend"/>, to its end.</summary>
    /// <param name="extend">true = extend the selection (Shift).</param>
    /// <param name="word">true = by word (Ctrl).</param>
    public void MoveRight(bool extend, bool word)
    {
        if (HasSelection && !extend)
        {
            SetCaret(SelectionEnd);
            return;
        }
        SetCaret(word ? WordEnd(Caret) : NextStop(Caret), extend);
    }

    /// <summary>Home: start of the line; with Ctrl, start of the whole text.</summary>
    /// <param name="extend">true = extend the selection (Shift).</param>
    /// <param name="document">true = whole text (Ctrl).</param>
    public void MoveHome(bool extend, bool document)
    {
        if (document)
        {
            SetCaret(0, extend);
            return;
        }
        var line = Layout.Lines[Layout.LineOf(Caret)];
        SetCaret(line.Start, extend);
    }

    /// <summary>End: end of the line; with Ctrl, end of the whole text.</summary>
    /// <param name="extend">true = extend the selection (Shift).</param>
    /// <param name="document">true = whole text (Ctrl).</param>
    public void MoveEnd(bool extend, bool document)
    {
        if (document)
        {
            SetCaret(Current.Content.Length, extend);
            return;
        }
        var index = Layout.LineOf(Caret);
        var line = Layout.Lines[index];
        // Wrapped line: before the wrap (after the space the next line would already begin)
        SetCaret(line.EndsParagraph ? line.End : Math.Max(line.Start, line.End - 1), extend);
    }

    /// <summary>Arrow up/down: to the line above or below, in the same column.</summary>
    /// <param name="direction">−1 = up, +1 = down.</param>
    /// <param name="extend">true = extend the selection (Shift).</param>
    public void MoveVertical(int direction, bool extend)
    {
        var layout = Layout;
        var index = layout.LineOf(Caret);
        _goalX ??= layout.Caret(Caret).X;
        var target = index + direction;
        if (target < 0)
        {
            SetCaret(0, extend, keepGoal: true);
            return;
        }
        if (target >= layout.Lines.Count)
        {
            SetCaret(Current.Content.Length, extend, keepGoal: true);
            return;
        }
        SetCaret(layout.CaretInLine(layout.Lines[target], _goalX.Value), extend, keepGoal: true);
    }

    // ------------------------------------------------------------ Helpers

    /// <summary>
    /// Previous position: not within a surrogate pair, not before a mark, not between a virama or coeng and its consonant, not within an extended
    /// grapheme cluster (<see cref="EditUnits"/>; before each code point was a stop, and the coeng could stay alone; docs/FEHLERGEDAECHTNIS.md,
    /// No. 1185, No. 1195).
    /// </summary>
    private int PreviousStop(int at) => EditUnits.Previous(Current.Content.Text, at);

    private int NextStop(int at) => EditUnits.Next(Current.Content.Text, at);

    /// <summary>
    /// Word start to the left of the position (Ctrl+Left, Ctrl+Backspace): skips non-word characters (stopping at a line break), then the word —
    /// letters, digits and marks per code point (before per UTF-16 unit, so a word outside the BMP was no word; No. 1185).
    /// </summary>
    private int WordStart(int at)
    {
        var text = Current.Content.Text;
        var i = Math.Min(at, text.Length);
        while (i > 0 && !EditUnits.IsWordBefore(text, i, out var skip) && text[i - 1] != '\n') i -= skip;
        if (i > 0 && text[i - 1] == '\n' && i == at) return i - 1;
        while (i > 0 && EditUnits.IsWordBefore(text, i, out var length)) i -= length;
        return i == at ? PreviousStop(at) : i;
    }

    /// <summary>Word end to the right of the position (Ctrl+Right, Ctrl+Delete): the word, then the white space after it.</summary>
    private int WordEnd(int at)
    {
        var text = Current.Content.Text;
        var i = at;
        if (i < text.Length && text[i] == '\n') return i + 1;
        while (i < text.Length && EditUnits.IsWordAt(text, i)) i += char.IsSurrogatePair(text, i) ? 2 : 1;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        return i == at ? NextStop(at) : i;
    }

    private void Apply(RichText text, int caret, int? anchor = null, bool typing = false, bool snapshot = true)
    {
        if (snapshot && !(typing && _typing))
        {
            _undo.Add((Current.Content, Caret, Anchor));
            if (_undo.Count > 500) _undo.RemoveAt(0);
            _redo.Clear();
        }
        _typing = typing;
        Replace(RichLayout.Fitted(Current with { Content = text }));
        Caret = Math.Clamp(caret, 0, text.Length);
        Anchor = Math.Clamp(anchor ?? caret, 0, text.Length);
        _goalX = null;
        Updated?.Invoke();
    }

    /// <summary>Puts the new element into the document (merged into the step of the edit).</summary>
    private void Replace(RichTextElement updated)
    {
        Applying = true;
        try
        {
            if (_doc.ReplaceElement(Element, updated, Description, continueLast: true)) Element = new ElementRef(Element.Page, updated);
        }
        finally
        {
            Applying = false;
        }
        _layout = null;
        Changed = true;
        Updated?.Invoke();
    }
}
