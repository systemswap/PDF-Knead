using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Editing existing text: turns a recognised text block into a block of formatted text at the same place — first baseline and
/// left edge as in the original, the same width, alignment, line spacing and indent. The lines of a paragraph are joined into
/// one (line breaking is redone), hard breaks are kept, and each formatting becomes a span that uses the font from the file.
/// </summary>
/// <remarks>See KONZEPT.md, section 5.14.</remarks>
public static class EditedText
{
    /// <summary>
    /// Converts a recognised block into an editable element. The original text is removed from the page separately by
    /// <see cref="Redactor.RemoveGlyphs"/>.
    /// </summary>
    /// <param name="doc">The document that contains the block's fonts.</param>
    /// <param name="block">The recognised text block.</param>
    /// <returns>The formatted text element, fitted by <see cref="RichLayout.Fitted"/>.</returns>
    public static RichTextElement FromBlock(PdfDocument doc, PageTextBlock block)
    {
        var spans = new List<RichSpan>();
        RichStyle? last = null;
        // Where each original line after the first starts in the text (to check whether the own layout breaks the lines the same way)
        var lineStarts = new List<int>();
        var length = 0;
        for (var i = 0; i < block.Lines.Count; i++)
        {
            var line = block.Lines[i];
            var runs = line.Runs.ToList();
            var lastLine = i + 1 == block.Lines.Count;
            for (var k = 0; k < runs.Count; k++)
            {
                var text = runs[k].Text;
                // White space at the end of a hard-broken line is invisible; it would otherwise be carried into the edited text
                if (k + 1 == runs.Count && (line.HardBreak || lastLine)) text = text.TrimEnd();
                // Add the original's tracking (spacing in TJ): same width, same line breaks
                last = Style(doc, runs[k].Format) with { CharSpacing = runs[k].Format.CharSpacing + runs[k].Tracking };
                if (text.Length > 0) spans.Add(new RichSpan(text, last));
                length += text.Length;
            }
            if (lastLine) break;
            // Wrapped line: one space (unless there already is one or the line ends with a hyphen); hard break: new paragraph
            var separator = line.HardBreak ? "\n" : line.Text.EndsWith(' ') || line.Text.EndsWith('-') ? "" : " ";
            if (separator.Length > 0) spans.Add(new RichSpan(separator, last ?? new RichStyle()));
            length += separator.Length;
            lineStarts.Add(length);
        }
        var content = new RichText(spans.Count > 0 ? spans : [new RichSpan("", last ?? new RichStyle())]);

        var size = block.Lines.Count > 0 ? block.Lines[0].Size : 10;
        // Slack in the width so that rounded widths do not make the longest line break differently (it lies on the free side and is
        // invisible) — not for justified text, which fills the width
        var slack = block.Alignment == BlockAlignment.Justify ? 0.05 : Math.Max(1, block.Width * 0.02);
        var rotation = ((int)Math.Round(block.Angle) % 360 + 360) % 360;
        // the language of the document (/Lang of the catalog): the font's language system for it (No. 1162)
        var language = Writing.PdfATagging.CleanLanguage(doc.Catalog?.GetString(Objects.PdfName.Get("Lang"))?.ToText(), 2);
        RichTextElement Build(double extra)
        {
            var wide = slack + extra;
            var (left, right) = block.Alignment switch
            {
                BlockAlignment.Right => (block.Left - wide, block.Right),
                BlockAlignment.Center => (block.Left - wide / 2, block.Right + wide / 2),
                _ => (block.Left, block.Right + wide),
            };
            var bounds = block.FrameToPage.TransformBounds(Graphics.RectD.FromLtrb(left, block.Bottom, right, block.Top));
            return new RichTextElement
            {
                Rect = new PdfRectangle(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                Rotation = rotation,
                Content = content,
                Alignment = block.Alignment,
                LineGap = block.Lines.Count >= 2 ? block.LineSpacing / Math.Max(1, block.Lines.Max(l => l.Size)) : 1.2,
                FontLineHeight = false,   // the spacing of the original, as a multiple of the font size: the edited text keeps its lines (No. 1211)
                FirstBaseline = block.Lines.Count > 0 ? block.Top - block.Lines[0].Baseline : null,
                // Indent only for left-aligned and justified text: centred and right-aligned lines start at different positions anyway
                // (otherwise a centred block received an "indent" of −110 pt and its second line wrapped)
                HangingIndent = block.Alignment is BlockAlignment.Left or BlockAlignment.Justify ? block.Indent : 0,
                // Tab stops measured from the element's left edge (for right-aligned blocks it lies further left by the slack)
                TabStops = [.. block.TabStops.Select(t => t with { Position = t.Position + block.Left - left })],
                Language = language,
            };
        }
        // If the own layout breaks differently from the original (spaces in the original set tighter than the font specifies: a line of
        // 50.6 pt became 53 pt wide, and a right-aligned block wrapped into three lines instead of two), widen the frame step by step
        // towards the free side, by at most four font sizes. Not for justified text: it fills the width, so a wider frame would look different.
        var element = Build(0);
        // Only for blocks of moderate size (amounts, labels, paragraphs): otherwise a block with 115 000 characters in a few lines needed
        // 17 layouts, 2.3 s instead of 0.4 s (found by fuzzing)
        if (block.Alignment != BlockAlignment.Justify && content.Length <= 2000 && !BreaksLikeOriginal(element, lineStarts))
            for (var step = 1; step <= 16; step++)
                if (Build(step * size / 4) is var wider && BreaksLikeOriginal(wider, lineStarts))
                {
                    element = wider;
                    break;
                }
        return RichLayout.Fitted(element);
    }

    /// <summary>Determines whether the laid-out lines start exactly where the original lines started.</summary>
    private static bool BreaksLikeOriginal(RichTextElement element, List<int> lineStarts)
    {
        var lines = RichLayout.Of(element).Lines;
        if (lines.Count != lineStarts.Count + 1) return false;
        for (var i = 0; i < lineStarts.Count; i++)
            if (lines[i + 1].Start != lineStarts[i]) return false;
        return true;
    }

    /// <summary>Converts the formatting of a recognised text run into a formatted-text style that uses the font from the file.</summary>
    /// <param name="doc">The document that contains the font.</param>
    /// <param name="format">The recognised formatting.</param>
    /// <returns>The style; the family falls back to Liberation Sans if the run has none.</returns>
    public static RichStyle Style(PdfDocument doc, TextFormat format) => new()
    {
        Family = format.Family.Length > 0 ? format.Family : "Liberation Sans",
        Bold = format.Bold,
        Italic = format.Italic,
        Underline = format.Underline,
        Strikeout = format.Strikeout,
        Size = format.Size,
        Color = format.Color,
        Rise = format.Rise,
        Stretch = format.Stretch,
        CharSpacing = format.CharSpacing,
        Original = format.Font is { } font ? new OriginalFont(doc, font, format.FakeBold, format.FakeItalic, format.FontId) : null,
    };
}
