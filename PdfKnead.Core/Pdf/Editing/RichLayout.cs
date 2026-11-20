using System.Text;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// A laid-out glyph: number in its font (for the file's font its code), position in the line (points), advance, font, span and
/// characters in the text.
/// </summary>
/// <param name="Glyph">Glyph number in <paramref name="Face"/>, or the character code for a font from the file.</param>
/// <param name="X">Position in the line in points, from the left edge of the element.</param>
/// <param name="Advance">Advance in points, including character spacing (and, for tabs and justified spaces, the adjustment).</param>
/// <param name="Face">The font; <see langword="null"/> for a font from the file (<see cref="Original"/>).</param>
/// <param name="FakeBold">Bold is faked.</param>
/// <param name="FakeItalic">Italic is faked.</param>
/// <param name="Span">Index of the span in the text.</param>
/// <param name="CharStart">Index of the first character in the text.</param>
/// <param name="CharLength">Number of UTF-16 characters the glyph stands for (more than 1 for ligatures and surrogate pairs).</param>
/// <param name="Text">The characters the glyph stands for; "\t" for a tab.</param>
/// <param name="XOffset">Horizontal offset in points (positioning by the shaper, e.g. accents).</param>
/// <param name="YOffset">Vertical offset in points.</param>
public readonly record struct LaidGlyph(int Glyph, double X, double Advance, ElementFace? Face, bool FakeBold, bool FakeItalic, int Span, int CharStart,
    int CharLength, string Text, double XOffset, double YOffset)
{
    /// <summary>Font from the file: then <see cref="Glyph"/> is its code (<see cref="CodeLength"/> bytes) and <see cref="Face"/> is null.</summary>
    public OriginalFace? Original { get; init; }

    /// <summary>Length of the code in bytes, for a font from the file.</summary>
    public int CodeLength { get; init; }

    /// <summary>Horizontal scaling (Tz): that of the span, additionally condensed for a fallback font that replaces a narrow original font.</summary>
    public double Stretch { get; init; } = 1;

    /// <summary>A mark attached to a glyph before it: how many glyphs back (0: none); a line set right to left keeps it at its base (No. 910).</summary>
    internal int AttachBack { get; init; }

    /// <summary>Ascender of the font in em units.</summary>
    public double AscentEm => Face?.Ascent ?? Original?.Ascent ?? 0.8;

    /// <summary>Descender of the font in em units (negative).</summary>
    public double DescentEm => Face?.Descent ?? Original?.Descent ?? -0.2;
}

/// <summary>
/// A laid-out line: characters <see cref="Start"/> to <see cref="End"/> (without the paragraph break), baseline
/// <see cref="Baseline"/> measured downwards from the top edge, ascent and descent (positive), glyphs in reading order.
/// </summary>
public sealed class LaidLine
{
    /// <summary>Index of the first character in the text.</summary>
    public required int Start { get; init; }

    /// <summary>Index after the last character of the line (exclusive).</summary>
    public required int End { get; init; }

    /// <summary>Ends with a paragraph break (or the end of the text).</summary>
    public required bool EndsParagraph { get; init; }

    /// <summary>Baseline in points below the top edge of the element.</summary>
    public double Baseline { get; internal set; }

    /// <summary>Ascent in points above the baseline (including rise).</summary>
    public required double Ascent { get; init; }

    /// <summary>Descent in points below the baseline (positive).</summary>
    public required double Descent { get; init; }

    /// <summary>Largest font size in the line (for the line spacing).</summary>
    public required double Size { get; init; }

    /// <summary>Start of the line in points from the left edge.</summary>
    public required double X { get; init; }

    /// <summary>Visible width in points (without trailing spaces); for justified lines the full available width.</summary>
    public required double Width { get; init; }

    /// <summary>The glyphs of the line, including trailing spaces.</summary>
    public required IReadOnlyList<LaidGlyph> Glyphs { get; init; }
}

/// <summary>
/// Layout of formatted text: spans shaped per font (own shaper: kerning, ligatures), broken at spaces (words that are too long
/// are broken hard), aligned (justified: the spaces become wider, except in the last line of a paragraph). Coordinates within
/// the element in reading orientation: x from the left, y downwards from the top edge. The same result draws the appearance and
/// drives the input box (caret, selection, hit testing), so that what is typed is what is printed.
/// </summary>
/// <remarks>See KONZEPT.md, section 5.14.</remarks>
public sealed class RichLayout
{
    private RichLayout(RichText text, List<LaidLine> lines, double width)
    {
        Text = text;
        Lines = lines;
        Width = width;
        Height = lines.Count == 0 ? 0 : lines[^1].Baseline + lines[^1].Descent;
    }

    /// <summary>The text laid out.</summary>
    public RichText Text { get; }

    /// <summary>The lines from top to bottom.</summary>
    public IReadOnlyList<LaidLine> Lines { get; }

    /// <summary>Layout width in points.</summary>
    public double Width { get; }

    /// <summary>Height needed: down to the descent of the last line.</summary>
    public double Height { get; }

    /// <summary>Lays out an element in its width (reading orientation).</summary>
    /// <param name="element">The element.</param>
    /// <returns>The layout.</returns>
    public static RichLayout Of(RichTextElement element) =>
        Layout(element.Content, element.UprightSize.Width, element.Alignment, element.LineGap, element.FirstBaseline, element.HangingIndent, element.TabStops);

    /// <summary>Gets the height an element needs for its text.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The height in points.</returns>
    public static double NeededHeight(RichTextElement element) => Of(element).Height;

    /// <summary>
    /// Adapts the height to the text (see KONZEPT.md, section 5.14: the block grows downwards, and shrinks again); the top edge in
    /// reading orientation stays.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>The element with the fitted rectangle (at least 1 point high), or the element itself if the height already fits.</returns>
    public static RichTextElement Fitted(RichTextElement element)
    {
        var (_, height) = element.UprightSize;
        var change = Math.Max(1, NeededHeight(element)) - height;
        if (Math.Abs(change) < 0.01) return element;
        var r = element.Rect;
        return element with
        {
            Rect = element.Rotation switch
            {
                90 => r with { Right = r.Right + change },
                180 => r with { Top = r.Top + change },
                270 => r with { Left = r.Left - change },
                _ => r with { Bottom = r.Bottom - change },
            },
        };
    }

    /// <summary>Lays out formatted text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="width">Layout width in points (at least 1).</param>
    /// <param name="alignment">Alignment of the lines.</param>
    /// <param name="lineGap">Distance between baselines as a multiple of the largest font size in the following line.</param>
    /// <param name="firstBaseline">Distance of the first baseline from the top edge in points; null = ascent of the first line.</param>
    /// <param name="hangingIndent">Indent in points: positive for the following lines of a paragraph, negative for the first.</param>
    /// <param name="tabStops">Tab stops from the left edge, ordered by position; beyond them the default grid of 1.25 cm.</param>
    /// <returns>The layout.</returns>
    public static RichLayout Layout(RichText text, double width, BlockAlignment alignment, double lineGap, double? firstBaseline, double hangingIndent,
        IReadOnlyList<TabStop>? tabStops = null)
    {
        width = Math.Max(1, width);
        var lines = new List<LaidLine>();
        var full = text.Text;
        var home = Home(text);
        var whole = WholeBlockFallback(text);
        var paragraphStart = 0;
        while (true)
        {
            var paragraphEnd = full.IndexOf('\n', paragraphStart);
            var last = paragraphEnd < 0;
            if (last) paragraphEnd = full.Length;
            var glyphs = Shape(text, paragraphStart, paragraphEnd, home, whole);
            BreakParagraph(text, glyphs, paragraphStart, paragraphEnd, width, alignment, hangingIndent, tabStops ?? [], lines);
            if (last) break;
            paragraphStart = paragraphEnd + 1;
        }
        // Baselines: the first after the ascent (or as in the original), then spaced by the largest font of the following line
        double y = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            y = i == 0 ? firstBaseline ?? lines[0].Ascent : y + lineGap * lines[i].Size;
            lines[i].Baseline = y;
        }
        return new RichLayout(text, lines, width);
    }

    // ------------------------------------------------------------ Shaping

    /// <summary>
    /// Gets the document whose fonts the text may use (that of the first span with an original font): an appearance is read in
    /// the context of one document, so original fonts of other files (pasted from another document) are drawn with the fallback
    /// font.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The document; <see langword="null"/> if no span uses a font from a file.</returns>
    public static Document.PdfDocument? Home(RichText text) => text.Spans.FirstOrDefault(s => s.Style.Original is not null)?.Style.Original?.Document;

    /// <summary>
    /// Fonts of the file that draw the text only as a whole (<see cref="OriginalFace.WholeBlockOnly"/>, Type 3) but lack a character of a
    /// span that uses them, or would have to fake bold: all their spans take the fallback font (KONZEPT.md, section 5.14, T4). Decided by
    /// the text alone — once the missing character is gone, the font returns.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The font dictionaries (by reference); empty if every font can draw its spans.</returns>
    public static IReadOnlySet<Objects.PdfDictionary> WholeBlockFallback(RichText text)
    {
        var home = Home(text);
        var result = new HashSet<Objects.PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var span in text.Spans)
        {
            if (span.Style.Original is not { } o || !ReferenceEquals(o.Document, home) || result.Contains(o.Font)) continue;
            if (OriginalFace.For(o) is not { WholeBlockOnly: true } face) continue;
            if (o.FakeBold) result.Add(o.Font);   // a drawn glyph cannot be emboldened with an outline
            else
                foreach (var rune in span.Text.EnumerateRunes())
                    if (rune.Value != '\n' && !face.Sets(rune.Value))
                    {
                        result.Add(o.Font);
                        break;
                    }
        }
        return result;
    }

    /// <summary>The glyphs of a paragraph (characters <paramref name="start"/> to <paramref name="end"/>), each span in its font.</summary>
    private static List<LaidGlyph> Shape(RichText text, int start, int end, Document.PdfDocument? home, IReadOnlySet<Objects.PdfDictionary> whole)
    {
        var result = new List<LaidGlyph>();
        if (end <= start) return result;
        for (var span = text.SpanAt(start); span < text.Spans.Count && text.StartOf(span) < end; span++)
        {
            var spanStart = text.StartOf(span);
            var from = Math.Max(start, spanStart);
            var to = Math.Min(end, spanStart + text.Spans[span].Text.Length);
            if (to <= from) continue;
            var style = text.Spans[span].Style;
            var piece = text.Text[from..to];
            // The file's font as far as it has the characters; the chosen font lays out the rest
            var original = style.Original is { } o && ReferenceEquals(o.Document, home) && !whole.Contains(o.Font) ? OriginalFace.For(o) : null;
            var runStart = 0;
            var runOriginal = false;
            var i = 0;
            foreach (var rune in piece.EnumerateRunes())
            {
                var covered = original is not null && (original.Has(rune.Value == '\t' ? ' ' : rune.Value) || original.Sets(rune.Value));
                if (i > 0 && covered != runOriginal)
                {
                    AddRun(runStart, i - runStart, runOriginal);
                    runStart = i;
                }
                if (i == 0 || covered != runOriginal) runOriginal = covered;
                i += rune.Utf16SequenceLength;
            }
            if (i > runStart) AddRun(runStart, i - runStart, runOriginal);

            void AddRun(int runFrom, int runLength, bool inOriginal)
            {
                if (inOriginal) AddOriginal(result, piece, runFrom, runLength, from, span, style, original!);
                else AddShaped(result, piece, runFrom, runLength, from, span, style, original);
            }
        }
        return result;
    }

    /// <summary>Characters in the file's font: its codes and widths (without kerning, which the original expressed as spacing).</summary>
    private static void AddOriginal(List<LaidGlyph> result, string piece, int from, int length, int offset, int span, RichStyle style, OriginalFace original)
    {
        var at = from;
        foreach (var rune in piece.AsSpan(from, length).ToString().EnumerateRunes())
        {
            // a space the font lacks (drawn glyphs, No. 611): no code, a shift of a third of an em (glyph number -1, see AppendRun)
            var shifted = !original.Codes.TryGetValue(rune.Value == '\t' ? ' ' : rune.Value, out var known);
            var (code, codeLength) = shifted ? (0u, 1) : known;
            var advance = (shifted ? OriginalFace.ShiftedSpace : original.Width(code, codeLength)) * style.Size * style.Stretch + style.CharSpacing;
            // Fake bold/italic of this span, not of the first span that used the font
            result.Add(new LaidGlyph(shifted ? -1 : (int)code, 0, advance, null, style.Original?.FakeBold ?? false, style.Original?.FakeItalic ?? false, span, offset + at,
                rune.Utf16SequenceLength, rune.ToString(), 0, 0)
            {
                Original = original, CodeLength = codeLength, Stretch = style.Stretch,
            });
            at += rune.Utf16SequenceLength;
        }
    }

    /// <summary>Characters in the chosen font (laid out with the shaper); where an original font lacks a character, scaled to its width.</summary>
    private static void AddShaped(List<LaidGlyph> result, string piece, int from, int length, int offset, int span, RichStyle style, OriginalFace? original)
    {
        var font = RichFonts.For(style);
        var sub = piece.Substring(from, length);
        // Characters the font lacks are drawn by the matching style of Liberation (such as "€" in an old font)
        foreach (var (runStart, runLength, face, fakeBold, fakeItalic) in CoverageRuns(sub, font, style))
        {
            var run = sub.Substring(runStart, runLength);
            var shaped = TextShaper.For(face.Font).Shape(run.Replace('\t', ' '));
            var scale = style.Size / face.Font.UnitsPerEm;
            var stretch = style.Stretch * (original?.StretchFor(face) ?? 1);
            foreach (var g in shaped)
            {
                var charStart = offset + from + runStart + g.Cluster;
                var advance = g.XAdvance * scale * stretch + style.CharSpacing;
                // A tab is shaped as a space (its advance is set when breaking lines) but stays recognisable as a tab
                var glyphText = run[g.Cluster] == '\t' ? "\t" : g.Text ?? "";
                result.Add(new LaidGlyph(g.Glyph, 0, advance, face, fakeBold, fakeItalic, span, charStart, Math.Max(1, g.Text?.Length ?? 1), glyphText,
                    g.XOffset * scale * stretch, g.YOffset * scale) { Stretch = stretch, AttachBack = g.AttachBack });
            }
        }
    }

    private static IEnumerable<(int Start, int Length, ElementFace Face, bool FakeBold, bool FakeItalic)> CoverageRuns(string piece, RichFonts.Resolved font, RichStyle style)
    {
        // Per character: the font, otherwise Liberation, otherwise Noto Arabic (No. 910) or Noto CJK (typed Chinese, Japanese, Korean
        // characters); what none has stays with Liberation (placeholder glyph)
        RichFonts.Resolved? fallback = null;
        RichFonts.Resolved Fallback() =>
            fallback ??= RichFonts.For(style with { Family = RichFonts.Kind(style.Family) switch { (_, true) => "Liberation Mono", (true, _) => "Liberation Serif", _ => "Liberation Sans" } });
        RichFonts.Resolved Source(Rune rune)
        {
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune) || font.Face.Glyph(rune.Value) != 0) return font;
            if (Fallback().Face.Glyph(rune.Value) != 0) return Fallback();
            return RichFonts.Arabic(rune.Value, style) ?? RichFonts.Cjk(rune.Value, style) ?? Fallback();
        }
        var runStart = 0;
        RichFonts.Resolved? current = null;
        var i = 0;
        foreach (var rune in piece.EnumerateRunes())
        {
            var source = Source(rune);
            if (current is { } c && !ReferenceEquals(c.Face, source.Face))
            {
                yield return (runStart, i - runStart, c.Face, c.FakeBold, c.FakeItalic);
                runStart = i;
            }
            current = source;
            i += rune.Utf16SequenceLength;
        }
        if (i > runStart && current is { } last) yield return (runStart, i - runStart, last.Face, last.FakeBold, last.FakeItalic);
    }

    /// <summary>Grid of the tab stops beyond the set ones (1.25 cm as in office programs), in points.</summary>
    private const double DefaultTab = 1.25 / 2.54 * 72;

    /// <summary>
    /// Advance of a tab at position <paramref name="x"/> up to the next tab stop that still leaves room (at least one point): for
    /// left stops the following text starts there, for right stops it ends there, for centre stops its middle is there
    /// (<paramref name="following"/>: its width up to the next tab or the end of the paragraph).
    /// </summary>
    private static double TabAdvance(double x, IReadOnlyList<TabStop> stops, double following)
    {
        foreach (var stop in stops)
        {
            var target = stop.Alignment switch
            {
                BlockAlignment.Right => stop.Position - following,
                BlockAlignment.Center => stop.Position - following / 2,
                _ => stop.Position,
            };
            if (target > x + 0.5) return target - x;
        }
        var next = (Math.Floor(x / DefaultTab) + 1) * DefaultTab;
        return Math.Max(1, next - x);
    }

    /// <summary>Width of the text from <paramref name="from"/> up to the next tab or the end of the paragraph (without trailing spaces).</summary>
    private static double FollowingWidth(List<LaidGlyph> glyphs, int from)
    {
        double width = 0, visible = 0;
        for (var i = from; i < glyphs.Count && glyphs[i].Text is not ("\t" or "\n"); i++)
        {
            width += glyphs[i].Advance;
            if (glyphs[i].Text is not " ") visible = width;
        }
        return visible;
    }

    /// <summary>Breaks a paragraph into lines: greedily after the last space that still fits; a word that is too long is broken hard.</summary>
    private static void BreakParagraph(RichText text, List<LaidGlyph> glyphs, int paragraphStart, int paragraphEnd, double width, BlockAlignment alignment,
        double hangingIndent, IReadOnlyList<TabStop> tabStops, List<LaidLine> lines)
    {
        var start = 0;
        var firstLine = true;
        do
        {
            // Positive: the following lines indented (bullet list); negative: the first one (start of paragraph)
            var indent = firstLine ? Math.Clamp(-hangingIndent, 0, width * 0.9) : Math.Clamp(hangingIndent, 0, width * 0.9);
            var available = width - indent;
            double used = 0;
            var end = start;
            var lastSpace = -1;
            while (end < glyphs.Count)
            {
                // Tab: up to the next tab stop (depends on where it stands in the line)
                if (glyphs[end].Text == "\t") glyphs[end] = glyphs[end] with { Advance = TabAdvance(indent + used, tabStops, FollowingWidth(glyphs, end + 1)) };
                if (used + glyphs[end].Advance > available + 1e-6 && end != start) break;
                if (glyphs[end].Text is " " or "\t") lastSpace = end;
                used += glyphs[end++].Advance;
            }
            if (end < glyphs.Count && glyphs[end].Text == " ")
            {
                // The word fits exactly: the spaces after it still belong to the line (they do not count towards the width)
                while (end < glyphs.Count && glyphs[end].Text == " ") end++;
            }
            else if (end < glyphs.Count && lastSpace >= start)
            {
                end = lastSpace + 1;   // in the middle of a word: break after the last space
            }
            var endsParagraph = end >= glyphs.Count;
            lines.Add(Line(text, glyphs, start, end, indent, width, alignment, endsParagraph,
                start < glyphs.Count ? glyphs[start].CharStart : paragraphStart, endsParagraph ? paragraphEnd : glyphs[end].CharStart));
            start = end;
            firstLine = false;
        } while (start < glyphs.Count);
    }

    private static LaidLine Line(RichText text, List<LaidGlyph> glyphs, int start, int end, double indent, double width, BlockAlignment alignment,
        bool endsParagraph, int charStart, int charEnd)
    {
        var visibleEnd = end;
        while (visibleEnd > start && glyphs[visibleEnd - 1].Text == " ") visibleEnd--;
        double natural = 0;
        for (var i = start; i < visibleEnd; i++) natural += glyphs[i].Advance;
        var available = width - indent;
        var spaces = 0;
        for (var i = start; i < visibleEnd; i++)
            if (glyphs[i].Text == " ") spaces++;
        var justify = alignment == BlockAlignment.Justify && !endsParagraph && spaces > 0 && natural < available;
        var extra = justify ? (available - natural) / spaces : 0;
        var x = indent + alignment switch
        {
            BlockAlignment.Center => (available - natural) / 2,
            BlockAlignment.Right => available - natural,
            _ => 0,
        };
        var laid = new List<LaidGlyph>(end - start);
        var pen = x;
        for (var i = start; i < end; i++)
        {
            var g = glyphs[i];
            var advance = g.Advance + (justify && i < visibleEnd && g.Text == " " ? extra : 0);
            laid.Add(g with { X = pen, Advance = advance });
            pen += advance;
        }
        // Ascent and descent: of the characters in the line (in their actual font); an empty line uses the formatting at its position
        double ascent = 0, descent = 0, size = 0;
        void Measure(RichStyle style, double ascentEm, double descentEm)
        {
            ascent = Math.Max(ascent, ascentEm * style.Size + Math.Max(0, style.Rise));
            descent = Math.Max(descent, -descentEm * style.Size - Math.Min(0, style.Rise));
            size = Math.Max(size, style.Size);
        }
        if (laid.Count == 0)
        {
            var style = text.StyleAt(charStart);
            var face = RichFonts.For(style).Face;
            Measure(style, face.Ascent, face.Descent);
        }
        foreach (var (span, ascentEm, descentEm) in laid.Select(g => (g.Span, g.AscentEm, g.DescentEm)).Distinct())
            Measure(text.Spans[span].Style, ascentEm, descentEm);
        return new LaidLine
        {
            Start = charStart, End = charEnd, EndsParagraph = endsParagraph, Ascent = ascent, Descent = descent, Size = size,
            X = x, Width = justify ? available : natural, Glyphs = laid,
        };
    }

    // ------------------------------------------------------------ Caret, selection, hit testing

    /// <summary>Gets the line of an insertion point (at the end of a wrapped line, the next line starts there).</summary>
    /// <param name="caret">Character index of the insertion point.</param>
    /// <returns>Index of the line; 0 without lines.</returns>
    public int LineOf(int caret)
    {
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            if (caret < line.End || caret == line.End && (line.EndsParagraph || i + 1 == Lines.Count)) return i;
            if (caret == line.End && i + 1 < Lines.Count && Lines[i + 1].Start > caret) return i;
        }
        return Math.Max(0, Lines.Count - 1);
    }

    /// <summary>Gets the position of the caret: x, baseline and the height of the line (ascent/descent), in points.</summary>
    /// <param name="caret">Character index of the insertion point.</param>
    /// <returns>The position; all zero without lines.</returns>
    public (double X, double Baseline, double Ascent, double Descent) Caret(int caret)
    {
        if (Lines.Count == 0) return (0, 0, 0, 0);
        var line = Lines[LineOf(caret)];
        return (XAt(line, caret), line.Baseline, line.Ascent, line.Descent);
    }

    /// <summary>Gets the x of an insertion point in its line (proportionally within a ligature).</summary>
    /// <param name="line">The line.</param>
    /// <param name="caret">Character index of the insertion point.</param>
    /// <returns>x in points from the left edge.</returns>
    public static double XAt(LaidLine line, int caret)
    {
        foreach (var g in line.Glyphs)
        {
            if (caret <= g.CharStart) return g.X;
            if (caret < g.CharStart + g.CharLength) return g.X + g.Advance * (caret - g.CharStart) / g.CharLength;
        }
        return line.Glyphs.Count > 0 ? line.Glyphs[^1].X + line.Glyphs[^1].Advance : line.X;
    }

    /// <summary>
    /// Finds the insertion point for a point (x from the left, y from the top): the line in whose band the point lies (the
    /// boundary between two lines halfway between the descent of the upper and the ascent of the lower line), then the nearest
    /// character boundary.
    /// </summary>
    /// <param name="x">x in points.</param>
    /// <param name="y">y in points below the top edge.</param>
    /// <returns>Character index of the insertion point.</returns>
    public int HitTest(double x, double y)
    {
        if (Lines.Count == 0) return 0;
        for (var i = 0; i + 1 < Lines.Count; i++)
        {
            var boundary = (Lines[i].Baseline + Lines[i].Descent + Lines[i + 1].Baseline - Lines[i + 1].Ascent) / 2;
            if (y <= boundary) return CaretInLine(Lines[i], x);
        }
        return CaretInLine(Lines[^1], x);
    }

    /// <summary>Finds the character boundary in a line nearest to x (for clicks and arrow up/down).</summary>
    /// <param name="line">The line.</param>
    /// <param name="x">x in points.</param>
    /// <returns>Character index of the insertion point.</returns>
    public int CaretInLine(LaidLine line, double x)
    {
        var best = line.Start;
        var bestDistance = double.MaxValue;
        for (var caret = line.Start; caret <= line.End; caret++)
        {
            var d = Math.Abs(XAt(line, caret) - x);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = caret;
            }
        }
        // Wrapped line: the position after the last space already belongs to the next line
        if (best == line.End && !line.EndsParagraph && best > line.Start) best--;
        return best;
    }

    /// <summary>Gets the rectangles of a selection, one per line (x left/right, y top/bottom from the top edge).</summary>
    /// <param name="start">Start of the selection (character index).</param>
    /// <param name="end">End of the selection (character index); may be before <paramref name="start"/>.</param>
    /// <returns>The rectangles in points.</returns>
    public IEnumerable<(double Left, double Top, double Right, double Bottom)> Selection(int start, int end)
    {
        if (end < start) (start, end) = (end, start);
        foreach (var line in Lines)
        {
            if (line.End < start || line.Start > end) continue;
            if (line.End == start && !line.EndsParagraph) continue;
            var left = XAt(line, Math.Max(start, line.Start));
            var right = XAt(line, Math.Min(end, line.End));
            // Paragraph break selected: a little beyond the end of the line
            if (end > line.End && line.EndsParagraph) right += line.Size * 0.3;
            if (right > left) yield return (left, line.Baseline - line.Ascent, right, line.Baseline + line.Descent);
        }
    }
}
