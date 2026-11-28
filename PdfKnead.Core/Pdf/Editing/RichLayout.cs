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

    /// <summary>Points per font unit of <see cref="Face"/> (size, horizontal scaling): places the carets of a ligature from the font (No. 1161); 0 if unknown.</summary>
    internal double FontScale { get; init; }

    /// <summary>
    /// The embedding level of the glyph's first character in its line (UAX #9, after L1 of that line; even: left to right, odd: right to left);
    /// the appearance reorders a line with an odd level (L2; docs/FEHLERGEDAECHTNIS.md, No. 1173).
    /// </summary>
    internal byte Level { get; init; }

    /// <summary>
    /// A soft hyphen (U+00AD): invisible and without advance within a line; where the line breaks after it, drawn as a hyphen with this advance
    /// (<see cref="Glyph"/> is then the hyphen of the font; Unicode 23.2.1; docs/FEHLERGEDAECHTNIS.md, No. 1185). 0 for any other glyph.
    /// </summary>
    internal double Hyphen { get; init; }

    /// <summary>Ascender of the font in em units.</summary>
    public double AscentEm => Face?.Ascent ?? Original?.Ascent ?? 0.8;

    /// <summary>Descender of the font in em units (negative).</summary>
    public double DescentEm => Face?.Descent ?? Original?.Descent ?? -0.2;

    /// <summary>
    /// The natural line height of the font in em units (<see cref="ElementFace.LineHeight"/>); for a font from the file, which brings no line
    /// metrics of its own here, <see cref="ElementFace.FallbackLineHeight"/>.
    /// </summary>
    public double LineHeightEm => Face?.LineHeight ?? ElementFace.FallbackLineHeight;
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

    /// <summary>Ends with a paragraph break, a line separator U+2028 or the end of the text (a line that must end there).</summary>
    public required bool EndsParagraph { get; init; }

    /// <summary>Baseline in points below the top edge of the element.</summary>
    public double Baseline { get; internal set; }

    /// <summary>Ascent in points above the baseline (including rise).</summary>
    public required double Ascent { get; init; }

    /// <summary>Descent in points below the baseline (positive).</summary>
    public required double Descent { get; init; }

    /// <summary>Largest font size in the line (for the line spacing).</summary>
    public required double Size { get; init; }

    /// <summary>
    /// The largest natural line height of the fonts in the line, in points (<see cref="LaidGlyph.LineHeightEm"/> times the size): the line
    /// spacing of a block whose spacing is a multiple of the line height of its fonts (docs/FEHLERGEDAECHTNIS.md, No. 1211); 0 if not measured.
    /// </summary>
    public double LineHeight { get; init; }

    /// <summary>Start of the line in points from the left edge.</summary>
    public required double X { get; init; }

    /// <summary>Visible width in points (without trailing spaces); for justified lines the full available width.</summary>
    public required double Width { get; init; }

    /// <summary>The glyphs of the line, including trailing spaces.</summary>
    public required IReadOnlyList<LaidGlyph> Glyphs { get; init; }
}

/// <summary>
/// Layout of formatted text: spans shaped per font (own shaper: kerning, ligatures), broken at the break opportunities of UAX #14 (words
/// that are too long are broken hard), aligned (justified: the spaces become wider, except in the last line of a paragraph). Coordinates within
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
        Layout(element.Content, element.UprightSize.Width, element.Alignment, element.LineGap, element.FirstBaseline, element.HangingIndent, element.TabStops,
            element.Language, element.FontLineHeight);

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
    /// <param name="lineGap">
    /// Distance between baselines as a multiple of the largest natural line height of the fonts in the following line
    /// (<paramref name="fontLineHeight"/>), else of its largest font size.
    /// </param>
    /// <param name="firstBaseline">Distance of the first baseline from the top edge in points; null = ascent of the first line.</param>
    /// <param name="hangingIndent">Indent in points: positive for the following lines of a paragraph, negative for the first.</param>
    /// <param name="tabStops">Tab stops from the left edge, ordered by position; beyond them the default grid of 1.25 cm.</param>
    /// <param name="language">The language of the text (BCP 47), for the font's language systems; null if unknown.</param>
    /// <param name="fontLineHeight">
    /// <paramref name="lineGap"/> multiplies the natural line height of the fonts (<see cref="RichTextElement.FontLineHeight"/>; No. 1211),
    /// else the font size (blocks of earlier versions, edited text of the page).
    /// </param>
    /// <returns>The layout.</returns>
    public static RichLayout Layout(RichText text, double width, BlockAlignment alignment, double lineGap, double? firstBaseline, double hangingIndent,
        IReadOnlyList<TabStop>? tabStops = null, string? language = null, bool fontLineHeight = false)
    {
        width = Math.Max(1, width);
        var lines = new List<LaidLine>();
        var full = text.Text;
        var home = Home(text);
        var whole = WholeBlockFallback(text);
        // the levels of the Unicode Bidirectional Algorithm for the whole text (paragraphs at every paragraph separator, P1): the characters at
        // odd levels are mirrored before shaping (L4), each line gets its own L1 (docs/FEHLERGEDAECHTNIS.md, No. 1173)
        var bidi = Bidi.HasRightToLeft(full) ? Bidi.Analyze(full) : null;
        // the break opportunities of the whole text after UAX #14 (docs/FEHLERGEDAECHTNIS.md, No. 1196)
        var breaks = LineBreaking.Analyze(full);
        // the character shaping and numeric shape selectors U+206C–U+206F (Unicode 23.3.1; docs/FEHLERGEDAECHTNIS.md, No. 1221)
        var selectors = FormatSelectors.States(full);
        // the optical bounds at the ends of the lines (OpenType 66.2, 66.20, 67.16; No. 1220)
        var bounds = new OpticalBounds(alignment, language);
        var pieceStart = 0;
        var first = true;
        while (true)
        {
            // a mandatory break ends a piece (UAX #14, LB4–LB6): LF, CR, CR LF, NEL and the paragraph separator U+2029 end a paragraph; the
            // line separator U+2028, VT and FF end a line without starting a paragraph — the next line has no first-line indent (Unicode 23.2.1;
            // docs/FEHLERGEDAECHTNIS.md, No. 1185, 1196). The editor turns typed control codes into LF and U+2028; a free text without it (a
            // field value from another program) brings them along
            var separator = full.IndexOfAny(HardBreaks, pieceStart);
            var last = separator < 0;
            var pieceEnd = last ? full.Length : separator;
            var glyphs = Shape(text, pieceStart, pieceEnd, home, whole, language, bidi, selectors);
            BreakParagraph(text, glyphs, pieceStart, pieceEnd, width, alignment, hangingIndent, tabStops ?? [], lines, bidi,
                (from, to) => Shape(text, from, to, home, whole, language, bidi, selectors), breaks, bounds, first);
            if (last) break;
            var c = full[separator];
            pieceStart = separator + (c == '\r' && separator + 1 < full.Length && full[separator + 1] == '\n' ? 2 : 1);
            first = c is not (LineSeparator or '\u000B' or '\u000C');
        }
        // Baselines: the first after the ascent (or as in the original), then spaced by the largest line height of the fonts (or the largest
        // font size) of the following line
        double y = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            y = i == 0 ? firstBaseline ?? lines[0].Ascent : y + lineGap * (fontLineHeight ? lines[i].LineHeight : lines[i].Size);
            lines[i].Baseline = y;
        }
        return new RichLayout(text, lines, width);
    }

    /// <summary>
    /// The hard line breaks of UAX #14 (LB4, LB5: the classes BK, CR, LF, NL): LF, CR, NEL and PARAGRAPH SEPARATOR end a paragraph, LINE
    /// SEPARATOR, VT and FF a line (Unicode 23.2.1).
    /// </summary>
    private static readonly char[] HardBreaks = ['\n', '\r', '\u0085', '\u2029', '\u2028', '\u000B', '\u000C'];

    /// <summary>LINE SEPARATOR: a new line in the same paragraph (Unicode 23.2.1).</summary>
    private const char LineSeparator = '\u2028';

    /// <summary>
    /// Whether a glyph takes the letter spacing of its span: not one standing only for format characters (ZWSP, ZWJ, WJ, ZWNBSP …, which are
    /// passed over by letter spacing, Unicode 16.1.1) nor for a space of fixed width (U+2000–U+200A, U+202F, U+205F, whose width spacing must
    /// not change, Unicode 23.2.1); before every glyph took it (docs/FEHLERGEDAECHTNIS.md, No. 1185). The spacing between the letters on
    /// either side of a ZWSP stays.
    /// </summary>
    internal static bool TakesLetterSpacing(string text)
    {
        if (text.Length == 0) return true;
        foreach (var rune in text.EnumerateRunes())
        {
            var c = rune.Value;
            if (c is >= 0x2000 and <= 0x200A or 0x202F or 0x205F || SearchFolding.Ignorable(c) || Bidi.IsControl(c)) continue;
            return true;
        }
        return false;
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
    private static List<LaidGlyph> Shape(RichText text, int start, int end, Document.PdfDocument? home, IReadOnlySet<Objects.PdfDictionary> whole,
        string? language, Bidi.Analysis? bidi, byte[]? selectors)
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
                else AddShaped(result, piece, runFrom, runLength, from, span, style, original, language, bidi, text.Text, selectors);
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
            // a directional formatting character: neither glyph nor advance (UAX #9, 2; No. 1173)
            if (Bidi.IsControl(rune.Value))
            {
                at += rune.Utf16SequenceLength;
                continue;
            }
            // a space the font lacks (drawn glyphs, No. 611): no code, a shift of a third of an em (glyph number -1, see AppendRun)
            var shifted = !original.Codes.TryGetValue(rune.Value == '\t' ? ' ' : rune.Value, out var known);
            var (code, codeLength) = shifted ? (0u, 1) : known;
            var advance = (shifted ? OriginalFace.ShiftedSpace : original.Width(code, codeLength)) * style.Size * style.Stretch
                + (TakesLetterSpacing(rune.ToString()) ? style.CharSpacing : 0);
            // a soft hyphen: without advance, drawn as the hyphen of the font only where the line breaks after it (No. 1185)
            double hyphen = 0;
            if (rune.Value == 0x00AD && !shifted)
            {
                if (original.Codes.TryGetValue(HyphenFor(piece, at, original.Codes.ContainsKey), out var dash)) (code, codeLength) = dash;
                hyphen = original.Width(code, codeLength) * style.Size * style.Stretch + style.CharSpacing;
                advance = 0;
            }
            // Fake bold/italic of this span, not of the first span that used the font
            result.Add(new LaidGlyph(shifted ? -1 : (int)code, 0, advance, null, style.Original?.FakeBold ?? false, style.Original?.FakeItalic ?? false, span, offset + at,
                rune.Utf16SequenceLength, rune.ToString(), 0, 0)
            {
                Original = original, CodeLength = codeLength, Stretch = style.Stretch, Hyphen = hyphen,
            });
            at += rune.Utf16SequenceLength;
        }
    }

    /// <summary>
    /// Characters in the chosen font (laid out with the shaper); where an original font lacks a character, scaled to its width. The shaper splits a
    /// run of one font into runs of one script and shapes each with the lookups of its script and of the text's language (No. 1162); a character
    /// at an odd level is mirrored before (No. 1173). After U+206E the digits are drawn in the national shapes of the script around them, and after
    /// U+206D the Arabic presentation forms are shaped (Unicode 23.3.1; <paramref name="selectors"/> of <see cref="FormatSelectors.States"/> for
    /// <paramref name="full"/>, the whole text; No. 1221) — the glyphs stand for the characters as typed.
    /// </summary>
    private static void AddShaped(List<LaidGlyph> result, string piece, int from, int length, int offset, int span, RichStyle style, OriginalFace? original,
        string? language, Bidi.Analysis? bidi, string full, byte[]? selectors)
    {
        var font = RichFonts.For(style);
        var sub = piece.Substring(from, length);
        // the text drawn: national digit shapes (before the fonts are chosen, so that a font with those digits draws them — where none of the
        // fonts the text may take has them, the digit keeps its nominal shape; No. 1221)
        var shown = selectors is null ? sub : FormatSelectors.NationalDigits(full, offset + from, length, selectors, language,
            c => CoverageRuns(char.ConvertFromUtf32(c), font, style, language).First().Face.Glyph(c) != 0);
        // Characters the font lacks are drawn by the matching style of Liberation (such as "€" in an old font)
        foreach (var (runStart, runLength, face, fakeBold, fakeItalic) in LevelRuns(CoverageRuns(shown, font, style, language), offset + from, bidi))
        {
            var run = sub.Substring(runStart, runLength);
            var at = offset + from + runStart;
            // L4 before shaping: a character at an odd level as its mirror image where the font has it (BidiMirroring.txt); the glyph still
            // stands for the character stored — as two of the three reference readers read a mirrored bracket back (No. 1173)
            // (not where symmetric swapping is inhibited by U+206A, No. 1215); the text drawn may carry national digit shapes (No. 1221)
            var unswapped = bidi?.SwappingInhibitedBefore(at) ?? false;
            var drawn = ReferenceEquals(shown, sub) ? run : shown.Substring(runStart, runLength);
            var shapedText = NoBreakHyphen(bidi is null ? drawn : Bidi.MirrorOdd(drawn, i => bidi.Level(at + i), c => face.Glyph(c) != 0, unswapped), face);
            // the direction of the level run selects the features ltra/ltrm or rtla/rtlm, and italic without an italic face asks for the font's
            // italic forms (ital) — the glyphs it replaces are not slanted as well (OpenType 65.19, 66.7, 67.17; docs/FEHLERGEDAECHTNIS.md, No. 1174)
            var rightToLeft = bidi is not null && JoinedLevel(bidi, at) % 2 == 1;
            var forms = selectors is not null && (selectors[at] & FormatSelectors.Shaping) != 0;
            var shaped = TextShaper.For(face.Font).Shape(shapedText.Replace('\t', ' '), fakeItalic ? ["ital"] : null, language, rightToLeft, unswapped, forms);
            var scale = style.Size / face.Font.UnitsPerEm;
            var stretch = style.Stretch * (original?.StretchFor(face) ?? 1);
            // directional formatting characters take neither glyph nor advance (UAX #9, 2; No. 1173): left out, a mark after one keeps its base
            var placed = new int[shaped.Length];
            for (var k = 0; k < shaped.Length; k++)
            {
                var g = shaped[k];
                placed[k] = -1;
                var text = g.Text is { } t && g.Cluster >= 0 && g.Cluster + t.Length <= run.Length ? run.Substring(g.Cluster, t.Length) : g.Text ?? "";
                if (Bidi.OnlyControls(text)) continue;
                var back = g.AttachBack > 0 && k - g.AttachBack >= 0 && placed[k - g.AttachBack] >= 0 ? result.Count - placed[k - g.AttachBack] : 0;
                placed[k] = result.Count;
                var advance = g.XAdvance * scale * stretch + (TakesLetterSpacing(text) ? style.CharSpacing : 0);
                // A tab is shaped as a space (its advance is set when breaking lines) but stays recognisable as a tab
                var glyphText = run[g.Cluster] == '\t' ? "\t" : text;
                // a soft hyphen: without advance, drawn as the hyphen of the font only where the line breaks after it (No. 1185)
                var glyph = g.Glyph;
                double hyphen = 0;
                if (text == "\u00AD")
                {
                    if (face.Glyph(HyphenFor(piece, from + runStart + g.Cluster, c => face.Glyph(c) > 0)) is > 0 and var dash) glyph = dash;
                    hyphen = face.Font.AdvanceWidth(glyph) * scale * stretch + style.CharSpacing;
                    advance = 0;
                }
                result.Add(new LaidGlyph(glyph, 0, advance, face, fakeBold, fakeItalic && !g.Italic, span, at + g.Cluster, Math.Max(1, g.Text?.Length ?? 1), glyphText,
                    text == "\u00AD" ? 0 : g.XOffset * scale * stretch, text == "\u00AD" ? 0 : g.YOffset * scale)
                {
                    Stretch = stretch, AttachBack = back, FontScale = scale * stretch, Hyphen = hyphen,
                });
            }
        }
    }

    /// <summary>
    /// The runs further split where the embedding level changes: shaping stays within a level run (UAX #9, 3.5), so that letters do not join
    /// across an embedding or override (docs/FEHLERGEDAECHTNIS.md, No. 1173). <paramref name="offset"/>: index of the piece in the text.
    /// </summary>
    private static IEnumerable<(int Start, int Length, ElementFace Face, bool FakeBold, bool FakeItalic)> LevelRuns(
        IEnumerable<(int Start, int Length, ElementFace Face, bool FakeBold, bool FakeItalic)> runs, int offset, Bidi.Analysis? bidi)
    {
        foreach (var run in runs)
        {
            if (bidi is null)
            {
                yield return run;
                continue;
            }
            var start = run.Start;
            for (var i = run.Start + 1; i < run.Start + run.Length; i++)
            {
                if (JoinedLevel(bidi, offset + i) == JoinedLevel(bidi, offset + i - 1) || char.IsLowSurrogate(bidi.Text[offset + i])) continue;
                yield return (start, i - start, run.Face, run.FakeBold, run.FakeItalic);
                start = i;
            }
            yield return (start, run.Start + run.Length - start, run.Face, run.FakeBold, run.FakeItalic);
        }
    }

    /// <summary>
    /// For ZERO WIDTH JOINER and NON-JOINER the character they belong to (Unicode 9.2, 23.2): a joining letter before them, else one after them,
    /// else the character before (else after) — so that a joiner shapes with that letter even where the font or the embedding level changes at
    /// it; for any other character itself. Index in <paramref name="text"/> (UTF-16 units).
    /// </summary>
    private static int JoinerTarget(string text, int index)
    {
        if (index < 0 || index >= text.Length || text[index] is not ('\u200C' or '\u200D')) return index;
        // a few joiners in a row at most (a long row of them would make each look far: they stay with themselves)
        const int reach = 8;
        var before = index - 1;
        while (before >= 0 && index - before <= reach && text[before] is '\u200C' or '\u200D') before--;
        if (before > 0 && char.IsLowSurrogate(text[before])) before--;
        var after = index + 1;
        while (after < text.Length && after - index <= reach && text[after] is '\u200C' or '\u200D') after++;
        if (before >= 0 && text[before] is '\u200C' or '\u200D' || after < text.Length && text[after] is '\u200C' or '\u200D') return index;
        bool Joins(int i) => i >= 0 && i < text.Length && ArabicJoining.Joins(char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i) : text[i]);
        if (Joins(before)) return before;
        if (Joins(after)) return after;
        return before >= 0 ? before : after < text.Length ? after : index;
    }

    /// <summary>The embedding level a character shapes with: a joiner's that of the letter it belongs to (<see cref="JoinerTarget"/>), else its own.</summary>
    internal static byte JoinedLevel(Bidi.Analysis bidi, int index) => bidi.Level(JoinerTarget(bidi.Text, index));

    private static IEnumerable<(int Start, int Length, ElementFace Face, bool FakeBold, bool FakeItalic)> CoverageRuns(string piece, RichFonts.Resolved font, RichStyle style,
        string? language)
    {
        // Per character: the font, otherwise Liberation, otherwise Noto Arabic (No. 910) or Noto CJK (typed Chinese, Japanese, Korean
        // characters; Han in the face of the block language, No. 1186); what none has stays with Liberation (placeholder glyph)
        RichFonts.Resolved? fallback = null;
        RichFonts.Resolved Fallback() =>
            fallback ??= RichFonts.For(style with { Family = RichFonts.Kind(style.Family) switch { (_, true) => "Liberation Mono", (true, _) => "Liberation Serif", _ => "Liberation Sans" } });
        RichFonts.Resolved Source(Rune rune)
        {
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune) || font.Face.Glyph(rune.Value) != 0) return font;
            // a soft hyphen goes with the font of the text, which then shows its hyphen; a no-break hyphen with the font if it has a hyphen
            // (No. 1196)
            if (rune.Value is 0x00AD or 0x2011 && font.Face.Glyph('-') != 0) return font;
            if (Fallback().Face.Glyph(rune.Value) != 0) return Fallback();
            return RichFonts.Arabic(rune.Value, style) ?? RichFonts.Cjk(rune.Value, style, language) ?? Fallback();
        }
        var runStart = 0;
        RichFonts.Resolved? current = null;
        var i = 0;
        foreach (var rune in piece.EnumerateRunes())
        {
            // a variation selector stays with the character before it, whose font chooses the variant (cmap format 14; No. 1160); a zero width
            // joiner or non-joiner goes with the letter it belongs to, so that it acts on that letter's joining (Unicode 9.2) — before, a joiner
            // the chosen font has formed a run of its own beside Arabic letters of the fallback font and did not act (No. 1174)
            var target = rune.Value is 0x200C or 0x200D ? JoinerTarget(piece, i) : i;
            // the selectors U+206C–U+206F (drawn as nothing) stay with the font of the text before them, so that they do not cut a run of
            // letters that join (Unicode 23.3.1; No. 1221)
            var source = (Core.Fonts.OpenTypeFont.IsVariationSelector(rune.Value) || rune.Value is >= 0x206C and <= 0x206F) && current is { } before ? before
                : target != i ? Source(Core.Fonts.Layout.TextRunes.RuneAt(piece, target)) : Source(rune);
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

    /// <summary>
    /// The text shaped for a run: NON-BREAKING HYPHEN as HYPHEN or HYPHEN-MINUS where the font lacks it — the same look, the break behaviour
    /// stays with the character stored (UAX #14 5.1: GL; before a font without it drew the missing glyph; docs/FEHLERGEDAECHTNIS.md, No. 1196).
    /// </summary>
    internal static string NoBreakHyphen(string text, ElementFace face)
    {
        if (!text.Contains('‑') || face.Glyph(0x2011) != 0) return text;
        var hyphen = face.Glyph(0x2010) != 0 ? '‐' : '-';
        return text.Replace('‑', hyphen);
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

    /// <summary>
    /// The character a soft hyphen shows where a line breaks after it: the hyphen of the script of the letter before it — ARMENIAN HYPHEN
    /// U+058A in Armenian, MONGOLIAN TODO SOFT HYPHEN U+1806 in Mongolian text — where the font has it, else U+002D (UAX #14 5.4; before always
    /// U+002D; docs/FEHLERGEDAECHTNIS.md, No. 1196). <paramref name="index"/>: the soft hyphen in <paramref name="text"/>.
    /// </summary>
    internal static int HyphenFor(string text, int index, Func<int, bool> has)
    {
        var i = Math.Min(index, text.Length);
        for (var steps = 0; i > 0 && steps < 32; steps++)
        {
            i--;
            if (i > 0 && char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1])) i--;
            var script = UnicodeScripts.Script(TextRunes.RuneAt(text, i).Value);
            if (UnicodeScripts.IsNeutral(script)) continue;
            var hyphen = script switch { "Armn" => 0x058A, "Mong" => 0x1806, _ => '-' };
            return hyphen != '-' && has(hyphen) ? hyphen : '-';
        }
        return '-';
    }

    /// <summary>
    /// Whether a glyph hangs at the end of a line: U+0020 and OGHAM SPACE MARK (shown only within a line, UAX #14 5.7) do not count towards
    /// the width that must fit — a word that fits up to the edge stays in its line though the space after it does not.
    /// </summary>
    internal static bool Hangs(string text) => text is " " or "\u1680";

    /// <summary>
    /// Breaks a paragraph (or the part of it up to a line separator) into lines: greedily at the last break opportunity of UAX #14 that still
    /// fits (<paramref name="breaks"/>; after a soft hyphen only if the hyphen it then shows still fits; spaces at the end of a line hang); a word
    /// that is too long is broken hard (emergency break), between clusters only. Before, lines broke only after a space, tab, ZERO WIDTH SPACE
    /// or soft hyphen (docs/FEHLERGEDAECHTNIS.md, No. 1185, 1196). With the optical bounds of the font (<paramref name="bounds"/>) a line fits
    /// with the advances its end glyphs take there (OpenType 66.20 sentence 11): the first glyph's from the start, each further glyph's as the
    /// last one of the line; a line of both directions, whose ends are known only in visual order, is checked once more and broken at the
    /// break opportunity before where it does not fit (the end only moves back, No. 1220).
    /// </summary>
    /// <param name="firstLine">Whether the first line is the first of a paragraph (after a line separator it is not: no first-line indent).</param>
    private static void BreakParagraph(RichText text, List<LaidGlyph> glyphs, int paragraphStart, int paragraphEnd, double width, BlockAlignment alignment,
        double hangingIndent, IReadOnlyList<TabStop> tabStops, List<LaidLine> lines, Bidi.Analysis? bidi, Func<int, int, List<LaidGlyph>> shape,
        LineBreaking.Breaks breaks, OpticalBounds bounds, bool firstLine = true)
    {
        var start = 0;
        var opportunities = new List<int>();
        do
        {
            // Positive: the following lines indented (bullet list); negative: the first one (start of paragraph)
            var indent = firstLine ? Math.Clamp(-hangingIndent, 0, width * 0.9) : Math.Clamp(hangingIndent, 0, width * 0.9);
            var available = width - indent;
            // in a paragraph right to left its first glyph stands at the right end of the line, its last one at the left (No. 1220)
            var leftToRight = bidi is null || start >= glyphs.Count || bidi.ParagraphLevel(glyphs[start].CharStart) % 2 == 0;
            double used = start < glyphs.Count ? bounds.Of(glyphs, start, leftToRight).Advance : 0;
            var end = start;
            var best = -1;   // the last break opportunity before which the line still fits
            opportunities.Clear();
            for (; end < glyphs.Count; end++)
            {
                // a break opportunity before this glyph: after a soft hyphen only if the hyphen it then shows still fits (Unicode 23.2.1)
                if (end > start && BreakBetween(glyphs, end, breaks)
                    && (glyphs[end - 1].Text != "\u00AD" || used + glyphs[end - 1].Hyphen + bounds.Of(glyphs, end - 1, !leftToRight).Advance <= available + 1e-6))
                {
                    best = end;
                    opportunities.Add(end);
                }
                // Tab: up to the next tab stop (depends on where it stands in the line)
                if (glyphs[end].Text == "\t") glyphs[end] = glyphs[end] with { Advance = TabAdvance(indent + used, tabStops, FollowingWidth(glyphs, end + 1)) };
                if (!Hangs(glyphs[end].Text) && end != start
                    && used + glyphs[end].Advance + bounds.Of(glyphs, Base(glyphs, end, start), !leftToRight).Advance > available + 1e-6) break;
                used += glyphs[end].Advance;
            }
            if (end < glyphs.Count && best > start)
            {
                end = best;
                // a line of both directions: its ends in visual order may take other bounds than assumed above \u2014 back to an earlier break
                // opportunity while it does not fit (No. 1220)
                if (bidi is not null && bounds.Active)
                    for (var k = opportunities.Count - 2; k >= 0 && opportunities[k] > start && Overflows(glyphs, start, end, available, bidi, bounds); k--)
                        end = opportunities[k];
            }
            else if (end < glyphs.Count && end > start)
            {
                var cut = ClusterBoundary(glyphs, start, end, breaks);
                end = cut < glyphs.Count ? ShapeAtHardBreak(glyphs, start, cut, paragraphEnd, shape) : cut;
            }
            // a soft hyphen at the end of a broken line shows the hyphen
            if (end < glyphs.Count && end > start && glyphs[end - 1].Text == "\u00AD")
                glyphs[end - 1] = glyphs[end - 1] with { Advance = glyphs[end - 1].Hyphen };
            var endsParagraph = end >= glyphs.Count;
            lines.Add(Line(text, glyphs, start, end, indent, width, alignment, endsParagraph,
                start < glyphs.Count ? glyphs[start].CharStart : paragraphStart, endsParagraph ? paragraphEnd : glyphs[end].CharStart, bidi, bounds));
            start = end;
            firstLine = false;
        } while (start < glyphs.Count);
    }

    /// <summary>
    /// Where a word too long for the line is broken hard: before <paramref name="end"/>, or further back at the nearest place between two
    /// clusters — not between glyphs of one cluster (a reordered syllable: the pre-base part of a Khmer vowel and its consonant), not before a
    /// mark (Mn, Mc, Me, also with an advance of its own), not between a virama or coeng and the consonant after it, not beside a joiner, WJ or
    /// ZWNBSP (Unicode 16.4.2, 16.7.1, 23.2.1; before the break fell at the glyph that no longer fit; docs/FEHLERGEDAECHTNIS.md, No. 1185);
    /// only where the analysis of UAX #14 allows an emergency break (grapheme clusters, orthographic syllables of LB28a, the tailorings of
    /// <see cref="LineBreaking"/>). If the line holds no such place, the first one after it ends the line, which then runs over the edge — a
    /// cluster or syllable is never cut (before it was broken where the width ended; No. 1196).
    /// </summary>
    private static int ClusterBoundary(List<LaidGlyph> glyphs, int start, int end, LineBreaking.Breaks breaks)
    {
        for (var at = end; at > start; at--)
            if (EmergencyBetween(glyphs, at, breaks)) return at;
        for (var at = end + 1; at < glyphs.Count; at++)
            if (EmergencyBetween(glyphs, at, breaks)) return at;
        return glyphs.Count;
    }

    private static bool EmergencyBetween(List<LaidGlyph> glyphs, int at, LineBreaking.Breaks breaks) =>
        BreakAllowedBefore(glyphs, at) && breaks.EmergencyAllowed(glyphs[at].CharStart);

    /// <summary>
    /// Whether UAX #14 gives a break opportunity between the glyphs <paramref name="at"/> − 1 and <paramref name="at"/>: at a cluster boundary,
    /// at the first character of the glyph after it or at a character without a glyph between them (a directional formatting character).
    /// </summary>
    private static bool BreakBetween(List<LaidGlyph> glyphs, int at, LineBreaking.Breaks breaks)
    {
        var position = glyphs[at].CharStart;
        var before = glyphs[at - 1].CharStart;
        if (position <= before) return false;
        for (var q = Math.Min(position, before + Math.Max(1, ClusterText(glyphs, at - 1).Length)); q <= position; q++)
            if (breaks.Allowed(q)) return true;
        return false;
    }

    /// <summary>
    /// Whether a hard break may fall between the glyphs <paramref name="at"/> − 1 and <paramref name="at"/>. The characters on both sides come
    /// from the cluster of each glyph (the glyphs with its CharStart): in a reordered syllable only the first glyph carries the text of the
    /// syllable, the others carry none — before, a glyph without text refused every break after it, so that no boundary was found after a
    /// syllable with a pre-base vowel sign and the word was cut inside the next syllable (docs/FEHLERGEDAECHTNIS.md, No. 1190).
    /// </summary>
    private static bool BreakAllowedBefore(List<LaidGlyph> glyphs, int at)
    {
        var (before, after) = (glyphs[at - 1], glyphs[at]);
        if (after.CharStart == before.CharStart) return false;
        var (beforeText, afterText) = (ClusterText(glyphs, at - 1), ClusterText(glyphs, at));
        if (afterText.Length == 0 || beforeText.Length == 0) return true;   // (distinct clusters without a text to judge by)
        var first = Core.Fonts.Layout.TextRunes.First(afterText).Value;
        if (SearchFolding.IsMark(first) || first is 0x200C or 0x200D or 0x2060 or 0xFEFF or 0x034F) return false;
        var last = Core.Fonts.Layout.TextRunes.Last(beforeText).Value;
        if (last is 0x200C or 0x200D or 0x2060 or 0xFEFF) return false;
        return SyllableData.SyllabicOf(last) is not (SyllabicCategory.Virama or SyllabicCategory.InvisibleStacker);
    }

    /// <summary>
    /// Whether an emergency break may fall before the character at <paramref name="position"/> of a plain text (field values, free text;
    /// <see cref="ElementAppearance.Layout"/>): where the analysis of UAX #14 allows it (boundaries of grapheme clusters, not inside an
    /// orthographic syllable …), not before a mark, a joiner, WJ, ZWNBSP or CGJ, not after a joiner, WJ, ZWNBSP, virama or invisible stacker —
    /// as <see cref="BreakAllowedBefore"/> in formatted text (before the plain text broke at the glyph that no longer fitted;
    /// docs/FEHLERGEDAECHTNIS.md, No. 1196).
    /// </summary>
    internal static bool EmergencyAt(string text, int position, LineBreaking.Breaks breaks)
    {
        if (position <= 0 || position >= text.Length || !breaks.EmergencyAllowed(position)) return false;
        var first = TextRunes.RuneAt(text, position).Value;
        if (SearchFolding.IsMark(first) || first is 0x200C or 0x200D or 0x2060 or 0xFEFF or 0x034F) return false;
        var last = TextRunes.Last(text[Math.Max(0, position - 2)..position]).Value;
        if (last is 0x200C or 0x200D or 0x2060 or 0xFEFF) return false;
        return SyllableData.SyllabicOf(last) is not (SyllabicCategory.Virama or SyllabicCategory.InvisibleStacker);
    }

    /// <summary>The text of the cluster glyph <paramref name="i"/> belongs to: its own, else that of a neighbour with the same CharStart (at most
    /// a syllable apart, bounded by the syllable limit of the shaper); empty if none of them has a text.</summary>
    private static string ClusterText(List<LaidGlyph> glyphs, int i)
    {
        if (glyphs[i].Text.Length > 0) return glyphs[i].Text;
        var start = glyphs[i].CharStart;
        for (var k = i - 1; k >= 0 && k >= i - 64 && glyphs[k].CharStart == start; k--)
            if (glyphs[k].Text.Length > 0) return glyphs[k].Text;
        for (var k = i + 1; k < glyphs.Count && k <= i + 64 && glyphs[k].CharStart == start; k++)
            if (glyphs[k].Text.Length > 0) return glyphs[k].Text;
        return "";
    }

    /// <summary>Characters of shaping context taken beyond the glyphs replaced after a hard break.</summary>
    private const int ShapingContext = 32;

    /// <summary>
    /// A word broken hard between the glyphs <paramref name="end"/> − 1 and <paramref name="end"/> in a cursive script (a letter joining across
    /// the break, ArabicShaping.txt): both sides are shaped anew, so that the letters at the break take the forms of a word end and a word start
    /// (UAX #9, 3.5.1; Unicode 9.2). The line is shaped whole (it is at most a line wide); after the break a window of the following text with
    /// context on both sides, so that a long word broken many times costs linear time. Returns the new end of the line in
    /// <paramref name="glyphs"/> (docs/FEHLERGEDAECHTNIS.md, No. 1173).
    /// </summary>
    private static int ShapeAtHardBreak(List<LaidGlyph> glyphs, int start, int end, int paragraphEnd, Func<int, int, List<LaidGlyph>> shape)
    {
        var lineFrom = glyphs[start].CharStart;
        var cut = glyphs[end].CharStart;
        if (cut <= lineFrom || !JoinsAcross(glyphs, start, end)) return end;
        var head = shape(lineFrom, cut);
        if (head.Count == 0) return end;
        // after the break: the glyphs up to a boundary both shapings share, far enough from the break and from the window's end
        var windowEnd = paragraphEnd - cut <= 8 * ShapingContext ? paragraphEnd : cut + 4 * ShapingContext;
        var tail = shape(cut, windowEnd);
        var keepFrom = glyphs.Count;   // first old glyph kept
        var tailCount = tail.Count;
        if (windowEnd < paragraphEnd)
        {
            var newStarts = new HashSet<int>(tail.Select(g => g.CharStart));
            keepFrom = -1;
            for (var k = end; k < glyphs.Count && glyphs[k].CharStart <= windowEnd - ShapingContext; k++)
                if (glyphs[k].CharStart >= cut + ShapingContext && newStarts.Contains(glyphs[k].CharStart))
                {
                    keepFrom = k;
                    break;
                }
            if (keepFrom < 0)
            {
                tail = shape(cut, paragraphEnd);
                tailCount = tail.Count;
                keepFrom = glyphs.Count;
            }
            else
            {
                var boundary = glyphs[keepFrom].CharStart;
                tailCount = tail.FindIndex(g => g.CharStart >= boundary);
            }
        }
        glyphs.RemoveRange(start, keepFrom - start);
        glyphs.InsertRange(start, head.Concat(tail.Take(tailCount)));
        return start + head.Count;

        // whether a letter before the break joins one after it (skipping transparent marks)
        static bool JoinsAcross(List<LaidGlyph> glyphs, int start, int end)
        {
            var before = 'T';
            for (var k = end - 1; k >= start && before == 'T'; k--)
                before = glyphs[k].Text is { Length: > 0 } t ? ArabicJoining.TypeOf(Core.Fonts.Layout.TextRunes.Last(t).Value) : 'U';
            if (before is not ('D' or 'L' or 'C')) return false;
            var after = 'T';
            for (var k = end; k < glyphs.Count && after == 'T'; k++)
                after = glyphs[k].Text is { Length: > 0 } t ? ArabicJoining.TypeOf(Core.Fonts.Layout.TextRunes.First(t).Value) : 'U';
            return after is 'D' or 'R' or 'C';
        }
    }

    /// <summary>
    /// A line laid out: aligned, justified, its glyphs placed. The glyphs at its ends take the optical bounds of their font (<paramref name="bounds"/>;
    /// the outermost visible glyphs in visual order, a mark moving with its base), which change the line's natural width before it is aligned and
    /// justified (OpenType 66.20 sentence 11; docs/FEHLERGEDAECHTNIS.md, No. 1220).
    /// </summary>
    private static LaidLine Line(RichText text, List<LaidGlyph> glyphs, int start, int end, double indent, double width, BlockAlignment alignment,
        bool endsParagraph, int charStart, int charEnd, Bidi.Analysis? bidi, OpticalBounds bounds)
    {
        // spaces at the end hang; at the end of a broken line OGHAM SPACE MARK hangs too and is not shown (UAX #14 5.7; No. 1196)
        var visibleEnd = VisibleEnd(glyphs, start, end, endsParagraph);
        double natural = 0;
        for (var i = start; i < visibleEnd; i++) natural += glyphs[i].Advance;
        // L1 for this line: separators and the white space at its end back to the paragraph level (No. 1173)
        var levels = bidi?.LineLevels(charStart, charEnd);
        var (leftEnd, rightEnd) = bounds.Active ? Ends(glyphs, start, visibleEnd, levels, charStart) : (-1, -1);
        var leftBound = leftEnd >= 0 ? bounds.Of(glyphs, leftEnd, true) : default;
        var rightBound = rightEnd >= 0 ? bounds.Of(glyphs, rightEnd, false) : default;
        natural += leftBound.Advance + rightBound.Advance;
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
        // the change of placement and advance of a glyph at an end of the line (both ends for a line of one glyph)
        (double Placement, double Advance) Bound(int i) =>
            ((i == leftEnd ? leftBound.Placement : 0) + (i == rightEnd ? rightBound.Placement : 0), (i == leftEnd ? leftBound.Advance : 0) + (i == rightEnd ? rightBound.Advance : 0));
        for (var i = start; i < end; i++)
        {
            var g = glyphs[i];
            // a soft hyphen within the line: no glyph (only at the end of a broken line it shows the hyphen; No. 1185); OGHAM SPACE MARK at the
            // end of a broken line: no glyph (No. 1196)
            if (i >= visibleEnd && g.Text != " " && Hangs(g.Text)) continue;
            if (g.Text == "\u00AD" && g.Advance == 0) continue;
            var (place, more) = leftEnd >= 0 || rightEnd >= 0 ? Bound(i) : default;
            // a mark attached to a glyph at an end of the line stays on it: the glyph moves by its placement, the mark's pen by its advance
            if (g.AttachBack > 0 && i - g.AttachBack is var b && (b == leftEnd || b == rightEnd) && b >= start)
            {
                var (basePlace, baseMore) = Bound(b);
                place += basePlace - baseMore;
            }
            var advance = g.Advance + more + (justify && i < visibleEnd && g.Text == " " ? extra : 0);
            var level = levels is not null && g.CharStart - charStart is var at && at >= 0 && at < levels.Length ? levels[at] : (byte)0;
            laid.Add(g with { X = pen, Advance = advance, Level = level, XOffset = g.XOffset + place });
            pen += advance;
        }
        // Ascent and descent: of the characters in the line (in their actual font); an empty line uses the formatting at its position
        double ascent = 0, descent = 0, size = 0, lineHeight = 0;
        void Measure(RichStyle style, double ascentEm, double descentEm, double lineHeightEm)
        {
            ascent = Math.Max(ascent, ascentEm * style.Size + Math.Max(0, style.Rise));
            descent = Math.Max(descent, -descentEm * style.Size - Math.Min(0, style.Rise));
            size = Math.Max(size, style.Size);
            lineHeight = Math.Max(lineHeight, lineHeightEm * style.Size);
        }
        if (laid.Count == 0)
        {
            var style = text.StyleAt(charStart);
            var face = RichFonts.For(style).Face;
            Measure(style, face.Ascent, face.Descent, face.LineHeight);
        }
        foreach (var (span, ascentEm, descentEm, lineHeightEm) in laid.Select(g => (g.Span, g.AscentEm, g.DescentEm, g.LineHeightEm)).Distinct())
            Measure(text.Spans[span].Style, ascentEm, descentEm, lineHeightEm);
        return new LaidLine
        {
            Start = charStart, End = charEnd, EndsParagraph = endsParagraph, Ascent = ascent, Descent = descent, Size = size, LineHeight = lineHeight,
            X = x, Width = justify ? available : natural, Glyphs = laid,
        };
    }

    // ------------------------------------------------------------ Optical bounds (OpenType 66.2, 66.20, 67.16)

    /// <summary>
    /// The optical bounds of the lines of a block (docs/FEHLERGEDAECHTNIS.md, No. 1220): the glyph at the left end of a line takes the lfbd of its
    /// font when the block is left aligned or justified, the glyph at the right end the rtbd when it is right aligned or justified (OpenType 66.2
    /// sentence 7, 67.16 sentence 7; opbd where the font has only that, 66.20 sentence 10; <see cref="TextShaper.OpticalBound"/>). A centred
    /// block takes none. Glyphs of a font from the file and a tab take none. The values found are kept per font, glyph, end and script.
    /// </summary>
    internal sealed class OpticalBounds(BlockAlignment alignment, string? language)
    {
        private readonly bool _left = alignment is BlockAlignment.Left or BlockAlignment.Justify;
        private readonly bool _right = alignment is BlockAlignment.Right or BlockAlignment.Justify;
        private readonly Dictionary<(Core.Fonts.OpenTypeFont Font, int Glyph, bool Left, string? Script), (double, double)> _known = [];

        /// <summary>Whether a line end can take a bound at all (by the alignment).</summary>
        public bool Active => _left || _right;

        /// <summary>The change of placement and advance in points of glyph <paramref name="k"/> standing at the left or right end of a line.</summary>
        public (double Placement, double Advance) Of(List<LaidGlyph> glyphs, int k, bool left)
        {
            if (!(left ? _left : _right) || k < 0 || k >= glyphs.Count) return (0, 0);
            var g = glyphs[k];
            if (g.Face is not { } face || g.FontScale <= 0 || g.Text == "\t") return (0, 0);
            var shaper = TextShaper.For(face.Font);
            if (!shaper.HasOpticalBounds) return (0, 0);
            var script = TextShaper.ScriptOf(g.Text) ?? ScriptNear(glyphs, k);
            var key = (face.Font, g.Glyph, left, script);
            if (!_known.TryGetValue(key, out var value)) _known[key] = value = shaper.OpticalBound(g.Glyph, left, script, language);
            return (value.Item1 * g.FontScale, value.Item2 * g.FontScale);
        }

        /// <summary>The script of the nearest glyph with a script of its own (after <paramref name="k"/>, else before it; a few glyphs at most).</summary>
        private static string? ScriptNear(List<LaidGlyph> glyphs, int k)
        {
            for (var i = k + 1; i < glyphs.Count && i <= k + 64; i++)
                if (TextShaper.ScriptOf(glyphs[i].Text) is { } after) return after;
            for (var i = k - 1; i >= 0 && i >= k - 64; i--)
                if (TextShaper.ScriptOf(glyphs[i].Text) is { } before) return before;
            return null;
        }
    }

    /// <summary>The end of the visible glyphs of a line (spaces at its end hang; at the end of a broken line OGHAM SPACE MARK too, UAX #14 5.7).</summary>
    private static int VisibleEnd(List<LaidGlyph> glyphs, int start, int end, bool endsParagraph)
    {
        var visibleEnd = end;
        while (visibleEnd > start && (glyphs[visibleEnd - 1].Text == " " || !endsParagraph && Hangs(glyphs[visibleEnd - 1].Text))) visibleEnd--;
        return visibleEnd;
    }

    /// <summary>The glyph a mark at <paramref name="k"/> is attached to (within the line from <paramref name="start"/>), else <paramref name="k"/>.</summary>
    private static int Base(List<LaidGlyph> glyphs, int k, int start) =>
        glyphs[k].AttachBack > 0 && k - glyphs[k].AttachBack >= start ? k - glyphs[k].AttachBack : k;

    /// <summary>
    /// The glyphs at the left and right ends of a line (indices into <paramref name="glyphs"/>; -1 for none): its first and last visible glyph,
    /// or — with an odd level among them (<paramref name="levels"/> of the line, L1) — the outermost units of a base and its marks in visual
    /// order (UAX #9 L2), each the base (No. 1220).
    /// </summary>
    private static (int Left, int Right) Ends(List<LaidGlyph> glyphs, int start, int visibleEnd, byte[]? levels, int charStart)
    {
        if (visibleEnd <= start) return (-1, -1);
        byte LevelOf(int i) => levels is not null && glyphs[i].CharStart - charStart is var at && at >= 0 && at < levels.Length ? levels[at] : (byte)0;
        var odd = false;
        for (var i = start; i < visibleEnd && !odd; i++) odd = LevelOf(i) % 2 == 1;
        if (!odd) return (Base(glyphs, start, start), Base(glyphs, visibleEnd - 1, start));
        var units = new List<int>();
        for (var i = start; i < visibleEnd; i++)
            if (i == start || glyphs[i].AttachBack == 0 || i - glyphs[i].AttachBack < start) units.Add(i);
        var order = Bidi.VisualOrder([.. units.Select(LevelOf)]);
        return (units[order[0]], units[order[^1]]);
    }

    /// <summary>
    /// Whether the line of glyphs <paramref name="start"/> … <paramref name="end"/> − 1 (a broken line) is wider than <paramref name="available"/>
    /// with the optical bounds of its ends in visual order (a soft hyphen at its end showing the hyphen; No. 1220).
    /// </summary>
    private static bool Overflows(List<LaidGlyph> glyphs, int start, int end, double available, Bidi.Analysis bidi, OpticalBounds bounds)
    {
        var visibleEnd = VisibleEnd(glyphs, start, end, false);
        double natural = 0;
        for (var i = start; i < visibleEnd; i++) natural += i == end - 1 && glyphs[i].Text == "\u00AD" ? glyphs[i].Hyphen : glyphs[i].Advance;
        var charStart = glyphs[start].CharStart;
        var (left, right) = Ends(glyphs, start, visibleEnd, bidi.LineLevels(charStart, glyphs[end].CharStart), charStart);
        if (left >= 0) natural += bounds.Of(glyphs, left, true).Advance;
        if (right >= 0) natural += bounds.Of(glyphs, right, false).Advance;
        return natural > available + 1e-6;
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

    /// <summary>Gets the x of an insertion point in its line (within a ligature at the caret its font gives, else proportionally).</summary>
    /// <param name="line">The line.</param>
    /// <param name="caret">Character index of the insertion point.</param>
    /// <returns>x in points from the left edge.</returns>
    public static double XAt(LaidLine line, int caret) => XAt(line, caret, 0);

    /// <summary>
    /// <see cref="XAt(LaidLine, int)"/> looking from glyph <paramref name="from"/> on: the glyphs before it all end at or before the caret
    /// (<see cref="GlyphEnd"/>), so the result is the same.
    /// </summary>
    private static double XAt(LaidLine line, int caret, int from)
    {
        var glyphs = line.Glyphs;
        for (var i = from; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            if (caret <= g.CharStart) return g.X;
            if (caret < g.CharStart + g.CharLength) return g.X + InsideLigature(g, caret - g.CharStart);
        }
        return glyphs.Count > 0 ? glyphs[^1].X + glyphs[^1].Advance : line.X;
    }

    /// <summary>The first insertion point <see cref="XAt(LaidLine, int)"/> no longer finds in a glyph (after its characters, after its start at least).</summary>
    private static int GlyphEnd(LaidGlyph g) => g.CharStart + Math.Max(1, g.CharLength);

    /// <summary>
    /// The x of an insertion point <paramref name="offset"/> characters into a glyph that stands for several (a ligature), from its start: at the
    /// caret its font gives for that boundary between the components (GDEF LigCaretList, OpenType 18.2.4; one component per character, the
    /// carets in logical order as the line is laid out), else — no carets, another number of them, a boundary inside a surrogate pair — at the
    /// share of the characters before it. Before, always the share (docs/FEHLERGEDAECHTNIS.md, No. 1161).
    /// </summary>
    private static double InsideLigature(LaidGlyph g, int offset)
    {
        if (g.Face is { } face && g.FontScale > 0 && Boundary(g.Text, offset) is var (k, n) && k > 0
            && TextShaper.For(face.Font).LigatureCarets(g.Glyph, n) is { } carets)
            return carets[k - 1] * g.FontScale;
        return g.Advance * offset / g.CharLength;

        // the boundary after character k of n (code points), 0 when the offset lies inside one
        static (int K, int N) Boundary(string text, int offset)
        {
            int k = 0, n = 0, at = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (at == offset) k = n;
                at += rune.Utf16SequenceLength;
                n++;
            }
            return (k, n);
        }
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

    /// <summary>
    /// Finds the insertion point in a line nearest to x (for clicks and arrow up/down): a stop of the editor (<see cref="EditUnits.IsStop"/>,
    /// a boundary of the grapheme clusters and of the units of the syllabic scripts; before, any character boundary, so a click could put the
    /// caret between a letter and its accent; docs/FEHLERGEDAECHTNIS.md, No. 1195), the start and the end of the line always. One pass over the
    /// line: from stop to stop, the glyph of each looked for from that of the stop before (before, every place of the line was tested and its
    /// glyph looked for from the start of the line — quadratic in a long line; No. 1210).
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="x">x in points.</param>
    /// <returns>Character index of the insertion point.</returns>
    public int CaretInLine(LaidLine line, double x)
    {
        var best = line.Start;
        var bestDistance = double.MaxValue;
        var text = Text.Text;
        var glyphs = line.Glyphs;
        var from = 0;   // the glyphs before it end at or before the caret (the places only grow)
        for (var caret = line.Start; ; )
        {
            while (from < glyphs.Count && GlyphEnd(glyphs[from]) <= caret) from++;
            var d = Math.Abs(XAt(line, caret, from) - x);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = caret;
            }
            if (caret >= line.End) break;
            // on to the next stop (EditUnits.Next jumps over a long grapheme cluster), the end of the line at the latest
            var next = EditUnits.Next(text, caret);
            caret = next > caret ? Math.Min(line.End, next) : line.End;
        }
        // Wrapped line: the position after the last space already belongs to the next line
        if (best == line.End && !line.EndsParagraph && best > line.Start) best = Math.Max(line.Start, EditUnits.Previous(text, best));
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
