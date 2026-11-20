using System.Text;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Formatting of a span of formatted text (see KONZEPT.md, section 5.14). <see cref="Family"/> is the name as in the file
/// ("Arial", "TimesNewRoman") or one of the program's own ("Liberation Sans"); <see cref="RichFonts"/> decides which font
/// actually draws it.
/// </summary>
public sealed record RichStyle
{
    /// <summary>Font family name.</summary>
    public string Family { get; init; } = "Liberation Sans";

    /// <summary>Bold.</summary>
    public bool Bold { get; init; }

    /// <summary>Italic.</summary>
    public bool Italic { get; init; }

    /// <summary>Underlined.</summary>
    public bool Underline { get; init; }

    /// <summary>Struck out.</summary>
    public bool Strikeout { get; init; }

    /// <summary>Font size in points.</summary>
    public double Size { get; init; } = 12;

    /// <summary>Colour as 0xRRGGBB.</summary>
    public int Color { get; init; }

    /// <summary>Superscript (positive) or subscript offset in points.</summary>
    public double Rise { get; init; }

    /// <summary>Width of the characters (horizontal scaling): 1 = normal, 0.9 = condensed.</summary>
    public double Stretch { get; init; } = 1;

    /// <summary>Additional spacing after each character in points.</summary>
    public double CharSpacing { get; init; }

    /// <summary>Font from the file that the span should keep using where it has the characters; null = none.</summary>
    public OriginalFont? Original { get; init; }

    /// <summary>
    /// Settles the style after a change: if family, bold or italic differ from <paramref name="before"/>, the file's font no
    /// longer fits, because it has only its one style. The family is then drawn in the new style (system font or Liberation,
    /// <see cref="RichFonts"/>).
    /// </summary>
    /// <param name="before">The style before the change.</param>
    /// <returns>This style, without <see cref="Original"/> if the font from the file no longer fits.</returns>
    public RichStyle Settled(RichStyle before) =>
        Original is not null && (Family != before.Family || Bold != before.Bold || Italic != before.Italic) ? this with { Original = null } : this;
}

/// <summary>Font of a file for edited text: its dictionary in the source document; bold or italic there only faked.</summary>
/// <param name="Document">The source document.</param>
/// <param name="Font">The font dictionary.</param>
/// <param name="FakeBold">Bold is faked with this font in the file.</param>
/// <param name="FakeItalic">Italic is faked with this font in the file.</param>
/// <param name="Id">Object number of the font dictionary; null for a direct object.</param>
public sealed record OriginalFont(Document.PdfDocument Document, Objects.PdfDictionary Font, bool FakeBold, bool FakeItalic, Objects.PdfObjectId? Id = null)
{
    /// <summary>Compares document and font dictionary by reference and the fake styles; <see cref="Id"/> does not count.</summary>
    /// <param name="other">The font to compare with.</param>
    /// <returns><see langword="true"/> for the same font with the same fake styles.</returns>
    public bool Equals(OriginalFont? other) => other is not null && ReferenceEquals(Document, other.Document) && ReferenceEquals(Font, other.Font) &&
                                               FakeBold == other.FakeBold && FakeItalic == other.FakeItalic;

    /// <summary>Hash code consistent with <see cref="Equals(OriginalFont?)"/>.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Font), FakeBold, FakeItalic);
}

/// <summary>A span: text with one formatting; "\n" separates paragraphs.</summary>
/// <param name="Text">The text.</param>
/// <param name="Style">Its formatting.</param>
public sealed record RichSpan(string Text, RichStyle Style);

/// <summary>
/// Formatted text (see KONZEPT.md, section 5.14): spans with their own formatting, "\n" separates paragraphs. Immutable —
/// inserting, deleting and formatting create a new text (undo keeps the old one). Normalised after every change: adjacent spans
/// with the same formatting become one, empty ones are dropped; an empty text keeps one empty span with its formatting (typing
/// continues there). Positions count in UTF-16 characters like <see cref="string"/>.
/// </summary>
public sealed class RichText : IEquatable<RichText>
{
    private readonly string _text;
    private readonly int[] _starts;   // start per span

    /// <summary>Creates formatted text from spans, normalising them.</summary>
    /// <param name="spans">The spans.</param>
    public RichText(IEnumerable<RichSpan> spans)
    {
        var list = Normalize(spans);
        Spans = list;
        _text = string.Concat(list.Select(s => s.Text));
        _starts = new int[list.Count];
        for (int i = 0, at = 0; i < list.Count; at += list[i].Text.Length, i++) _starts[i] = at;
    }

    /// <summary>Creates formatted text with a single formatting.</summary>
    /// <param name="text">The text.</param>
    /// <param name="style">The formatting.</param>
    public RichText(string text, RichStyle style) : this([new RichSpan(text, style)]) { }

    /// <summary>The normalised spans; at least one.</summary>
    public IReadOnlyList<RichSpan> Spans { get; }

    /// <summary>The plain text of all spans.</summary>
    public string Text => _text;

    /// <summary>Length in UTF-16 characters.</summary>
    public int Length => _text.Length;

    /// <summary>Gets the start of a span in the text.</summary>
    /// <param name="span">Index of the span.</param>
    /// <returns>Character index.</returns>
    public int StartOf(int span) => _starts[span];

    /// <summary>Gets the span that contains the character <paramref name="index"/>.</summary>
    /// <param name="index">Character index.</param>
    /// <returns>Index of the span; the last span at or beyond the end.</returns>
    public int SpanAt(int index)
    {
        if (index >= _text.Length) return Spans.Count - 1;
        var found = Array.BinarySearch(_starts, Math.Max(0, index));
        if (found >= 0)
        {
            // Several spans cannot start at the same position (empty ones are dropped) — except the single empty one
            return found;
        }
        return ~found - 1;
    }

    /// <summary>
    /// Gets the formatting for new text at the insertion point <paramref name="caret"/> (as in office programs): that of the character
    /// before it; at the start of a paragraph that of the first character after it.
    /// </summary>
    /// <param name="caret">Character index of the insertion point; clamped to the text.</param>
    /// <returns>The formatting.</returns>
    public RichStyle StyleAt(int caret)
    {
        if (_text.Length == 0) return Spans[0].Style;
        caret = Math.Clamp(caret, 0, _text.Length);
        var before = caret > 0 && _text[caret - 1] != '\n' ? caret - 1 : Math.Min(caret, _text.Length - 1);
        if (before >= 0 && _text[before] == '\n' && before > 0) before--;   // empty paragraph: the formatting before it
        return Spans[SpanAt(before)].Style;
    }

    /// <summary>Gets the formattings within a range (for the toolbar buttons).</summary>
    /// <param name="start">Start of the range.</param>
    /// <param name="length">Length of the range.</param>
    /// <returns>The style of every span the range touches; for an empty range the style at <paramref name="start"/>.</returns>
    public IEnumerable<RichStyle> StylesIn(int start, int length)
    {
        if (length <= 0)
        {
            yield return StyleAt(start);
            yield break;
        }
        var end = Math.Min(_text.Length, start + length);
        for (var i = SpanAt(start); i < Spans.Count && _starts[i] < end; i++) yield return Spans[i].Style;
    }

    /// <summary>Inserts text at a position in the formatting <paramref name="style"/>.</summary>
    /// <param name="index">Character index.</param>
    /// <param name="text">The text to insert.</param>
    /// <param name="style">The formatting; null = as at the position (<see cref="StyleAt"/>).</param>
    /// <returns>The new text.</returns>
    public RichText Insert(int index, string text, RichStyle? style = null) => Replace(index, 0, text, style ?? StyleAt(index));

    /// <summary>Deletes a range.</summary>
    /// <param name="start">Start of the range.</param>
    /// <param name="length">Length of the range.</param>
    /// <returns>The new text.</returns>
    public RichText Delete(int start, int length) => Replace(start, length, "", StyleAt(start));

    /// <summary>Replaces a range with plain text (typing over a selection, pasting).</summary>
    /// <param name="start">Start of the range; clamped to the text.</param>
    /// <param name="length">Length of the range; clamped to the text.</param>
    /// <param name="text">The new text.</param>
    /// <param name="style">Its formatting.</param>
    /// <returns>The new text.</returns>
    public RichText Replace(int start, int length, string text, RichStyle style)
    {
        start = Math.Clamp(start, 0, _text.Length);
        var end = Math.Clamp(start + Math.Max(0, length), start, _text.Length);
        var result = new List<RichSpan>(Spans.Count + 2);
        result.AddRange(Cut(0, start));
        result.Add(new RichSpan(text, style));
        result.AddRange(Cut(end, _text.Length));
        // An empty text keeps the formatting in which typing would continue
        return new RichText(result.All(s => s.Text.Length == 0) ? [new RichSpan("", style)] : result);
    }

    /// <summary>Replaces a range with formatted text (pasting from the clipboard, undo).</summary>
    /// <param name="start">Start of the range; clamped to the text.</param>
    /// <param name="length">Length of the range; clamped to the text.</param>
    /// <param name="text">The formatted text to insert.</param>
    /// <returns>The new text.</returns>
    public RichText Replace(int start, int length, RichText text)
    {
        start = Math.Clamp(start, 0, _text.Length);
        var end = Math.Clamp(start + Math.Max(0, length), start, _text.Length);
        return new RichText([.. Cut(0, start), .. text.Spans, .. Cut(end, _text.Length)]);
    }

    /// <summary>Changes the formatting of a range (bold, colour …).</summary>
    /// <param name="start">Start of the range.</param>
    /// <param name="length">Length of the range.</param>
    /// <param name="change">Maps each old style to the new one.</param>
    /// <returns>The new text; this instance for an empty range.</returns>
    public RichText Format(int start, int length, Func<RichStyle, RichStyle> change)
    {
        start = Math.Clamp(start, 0, _text.Length);
        var end = Math.Clamp(start + Math.Max(0, length), start, _text.Length);
        if (end == start) return this;
        return new RichText([.. Cut(0, start), .. Cut(start, end).Select(s => s with { Style = change(s.Style) }), .. Cut(end, _text.Length)]);
    }

    /// <summary>Gets a part of the text (copying).</summary>
    /// <param name="start">Start of the range.</param>
    /// <param name="length">Length of the range.</param>
    /// <returns>The part; for an empty range an empty text with the formatting at <paramref name="start"/>.</returns>
    public RichText Slice(int start, int length)
    {
        start = Math.Clamp(start, 0, _text.Length);
        var end = Math.Clamp(start + Math.Max(0, length), start, _text.Length);
        var spans = Cut(start, end).ToList();
        return new RichText(spans.Count == 0 ? [new RichSpan("", StyleAt(start))] : spans);
    }

    /// <summary>The spans between two positions, cut at the edges.</summary>
    private IEnumerable<RichSpan> Cut(int start, int end)
    {
        if (end <= start) yield break;
        for (var i = SpanAt(start); i < Spans.Count && _starts[i] < end; i++)
        {
            var span = Spans[i];
            var from = Math.Max(start, _starts[i]) - _starts[i];
            var to = Math.Min(end, _starts[i] + span.Text.Length) - _starts[i];
            if (to > from) yield return from == 0 && to == span.Text.Length ? span : span with { Text = span.Text[from..to] };
        }
    }

    private static List<RichSpan> Normalize(IEnumerable<RichSpan> spans)
    {
        var result = new List<RichSpan>();
        RichStyle? lastStyle = null;
        var text = new StringBuilder();
        foreach (var span in spans)
        {
            lastStyle ??= span.Style;
            if (span.Text.Length == 0) continue;
            if (text.Length > 0 && span.Style != result[^1].Style || result.Count == 0)
            {
                if (result.Count > 0) result[^1] = result[^1] with { Text = text.ToString() };
                text.Clear();
                result.Add(span with { Text = "" });
            }
            text.Append(span.Text);
        }
        if (result.Count > 0) result[^1] = result[^1] with { Text = text.ToString() };
        else result.Add(new RichSpan("", lastStyle ?? new RichStyle()));
        return result;
    }

    /// <summary>Compares text and spans, including their formatting.</summary>
    /// <param name="other">The text to compare with.</param>
    /// <returns><see langword="true"/> if text and spans are equal.</returns>
    public bool Equals(RichText? other) => other is not null && (ReferenceEquals(this, other) || _text == other._text && Spans.SequenceEqual(other.Spans));

    /// <summary>Compares with another object; see <see cref="Equals(RichText?)"/>.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> for an equal formatted text.</returns>
    public override bool Equals(object? obj) => Equals(obj as RichText);

    /// <summary>Hash code of the plain text.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => _text.GetHashCode();

    /// <summary>Returns the plain text.</summary>
    /// <returns>The text without formatting.</returns>
    public override string ToString() => _text;
}
