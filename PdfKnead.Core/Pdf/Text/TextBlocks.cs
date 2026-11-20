using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Text;

/// <summary>Alignment of a text block (detected from the margins of its lines).</summary>
public enum BlockAlignment
{
    /// <summary>Flush left.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Flush right.</summary>
    Right,

    /// <summary>Justified (both margins flush, last line shorter).</summary>
    Justify,
}

/// <summary>
/// Formatting of a run as set in the file; measures in points in page space. <see cref="Font"/> is the font dictionary of the file
/// (new text can keep using it if it has all characters; see KONZEPT.md, section 5.14).
/// </summary>
public sealed record TextFormat
{
    /// <summary>Name of the font without the subset prefix, for example "Arial,Bold".</summary>
    public string FontName { get; init; } = "";

    /// <summary>Family without style, for example "Arial".</summary>
    public string Family { get; init; } = "";

    /// <summary>Bold (from the font name, weight or flags, or synthetic).</summary>
    public bool Bold { get; init; }

    /// <summary>Italic (from the font name, italic angle or flags, or synthetic).</summary>
    public bool Italic { get; init; }

    /// <summary>Synthetic bold only: characters drawn with an outline (text rendering mode 2), the font itself is regular.</summary>
    public bool FakeBold { get; init; }

    /// <summary>Synthetic italic only: the font is slanted.</summary>
    public bool FakeItalic { get; init; }

    /// <summary>Font size in points.</summary>
    public double Size { get; init; }

    /// <summary>Colour as 0xRRGGBB.</summary>
    public int Color { get; init; }

    /// <summary>Width of the characters relative to normal (horizontal scaling, compressed text matrix): 1 = normal, 0.9 = narrow.</summary>
    public double Stretch { get; init; } = 1;

    /// <summary>Additional character spacing in points.</summary>
    public double CharSpacing { get; init; }

    /// <summary>Additional word spacing in points.</summary>
    public double WordSpacing { get; init; }

    /// <summary>Superscript (positive) or subscript (negative) offset in points.</summary>
    public double Rise { get; init; }

    /// <summary>Underlined (a matching line was found below the text).</summary>
    public bool Underline { get; init; }

    /// <summary>Struck through (a matching line was found across the text).</summary>
    public bool Strikeout { get; init; }

    /// <summary>Font dictionary of the file (null for an unreadable font).</summary>
    public PdfDictionary? Font { get; init; }

    /// <summary>Object number of the font dictionary (if it is an indirect object).</summary>
    public PdfObjectId? FontId { get; init; }

    /// <summary>Compares the formatting, with measures accurate to 2 % of the size.</summary>
    /// <param name="other">The other formatting.</param>
    /// <returns>True if both describe the same formatting (the same font dictionary by reference).</returns>
    public bool SameAs(TextFormat other)
    {
        var tolerance = Math.Max(Size, other.Size) * 0.02 + 0.01;
        return ReferenceEquals(Font, other.Font) && FontName == other.FontName && Bold == other.Bold && Italic == other.Italic && FakeBold == other.FakeBold &&
               FakeItalic == other.FakeItalic && Color == other.Color && Underline == other.Underline && Strikeout == other.Strikeout &&
               Math.Abs(Size - other.Size) <= tolerance && Math.Abs(Rise - other.Rise) <= tolerance && Math.Abs(Stretch - other.Stretch) < 0.02 &&
               Math.Abs(CharSpacing - other.CharSpacing) <= tolerance && Math.Abs(WordSpacing - other.WordSpacing) <= tolerance;
    }
}

/// <summary>Run of a line with one formatting; <see cref="Glyphs"/>: numbers of the characters in the page content (see <see cref="TextBlocks"/>).</summary>
/// <param name="Text">The text, including inserted spaces and tabs.</param>
/// <param name="Format">The formatting.</param>
/// <param name="Glyphs">Ordinals of the characters (<see cref="TextBlocks.Glyph.Ordinal"/>), including characters drawn twice.</param>
public sealed record TextRun(string Text, TextFormat Format, IReadOnlyList<int> Glyphs)
{
    /// <summary>
    /// Tracking in points: mean spacing between the characters beyond their advance (adjustments in TJ, for example from a printer
    /// driver); new text with this tracking is as wide as the original and wraps the same way.
    /// </summary>
    public double Tracking { get; init; }
}

/// <summary>
/// A line of a block: runs from left to right (in reading order). <see cref="Start"/>/<see cref="End"/> and
/// <see cref="Baseline"/> in the frame of the block (along and across the writing direction, in points).
/// </summary>
/// <param name="Runs">The runs in reading order.</param>
/// <param name="Start">Start of the line along the writing direction.</param>
/// <param name="End">End of the line along the writing direction.</param>
/// <param name="Baseline">Position of the baseline across the writing direction.</param>
public sealed record TextBlockLine(IReadOnlyList<TextRun> Runs, double Start, double End, double Baseline)
{
    /// <summary>The text of all runs.</summary>
    public string Text => string.Concat(Runs.Select(r => r.Text));

    /// <summary>Largest font size of the line; 0 without runs.</summary>
    public double Size => Runs.Count == 0 ? 0 : Runs.Max(r => r.Format.Size);

    /// <summary>Where the tabs of the line jump to (in the frame of the block, like <see cref="Start"/>).</summary>
    public IReadOnlyList<double> Tabs { get; init; } = [];

    /// <summary>End of the text after each tab (up to the next tab or the end of the line), for right-aligned and centred tab stops.</summary>
    public IReadOnlyList<double> TabEnds { get; init; } = [];

    /// <summary>
    /// The line end is a hard break: the first word of the next line would still have fitted here (address, short lines,
    /// end of paragraph); otherwise the text only wraps there and is wrapped anew when edited.
    /// </summary>
    public bool HardBreak { get; init; } = true;
}

/// <summary>
/// A text block: paragraph, table cell or single line, as a reader sees it. Position in a frame of its own: rotated by
/// <see cref="Angle"/> (degrees, counter-clockwise) about the origin of page space (<see cref="FrameToPage"/>), with x along the
/// text and y across it, upwards. See KONZEPT.md, section 5.14.
/// </summary>
public sealed class PageTextBlock
{
    internal PageTextBlock(IReadOnlyList<TextBlockLine> lines, double angle, BlockAlignment alignment, double lineSpacing, string? locked,
        IReadOnlyList<int> glyphs, IReadOnlyList<int> paths, double top, double bottom)
    {
        Lines = lines;
        Angle = angle;
        Alignment = alignment;
        LineSpacing = lineSpacing;
        Locked = locked;
        Glyphs = glyphs;
        Paths = paths;
        Left = lines.Min(l => l.Start);
        Right = lines.Max(l => l.End);
        Top = top;
        Bottom = bottom;
    }

    /// <summary>The lines from top to bottom.</summary>
    public IReadOnlyList<TextBlockLine> Lines { get; }

    /// <summary>Writing direction in degrees (0 = horizontal from left to right), counter-clockwise.</summary>
    public double Angle { get; }

    /// <summary>Detected alignment.</summary>
    public BlockAlignment Alignment { get; }

    /// <summary>Baseline spacing in points (single line: 1.2 × font size).</summary>
    public double LineSpacing { get; }

    /// <summary>
    /// Indent in points: positive if the following lines start further right (list item; for a single line the text after the marker),
    /// negative if the first line does (indented paragraph start).
    /// </summary>
    public double Indent { get; init; }

    /// <summary>Tab stops from the left edge of the block (where the tabs of its lines jump to), ordered by position.</summary>
    public IReadOnlyList<TabStop> TabStops { get; init; } = [];

    /// <summary>Why the block cannot be edited (for the notice, translated); null = editable.</summary>
    public string? Locked { get; private set; }

    /// <summary>
    /// Set in a bar code font: not edited as text — a click in „Bearbeiten“ shows the code, and „Code ändern …“ makes it anew (phase 8.2).
    /// <see cref="Locked"/> then says so.
    /// </summary>
    public bool CodeFont { get; init; }

    /// <summary>Locks the block afterwards (for example the origin line of a finalised signature); an earlier reason is kept.</summary>
    internal void Lock(string reason) => Locked ??= reason;

    /// <summary>All characters of the block (numbers in the page content), including those drawn twice (synthetic bold), which are removed when replacing.</summary>
    public IReadOnlyList<int> Glyphs { get; }

    /// <summary>Underlines and strikethroughs (numbers of the paths in the page content).</summary>
    public IReadOnlyList<int> Paths { get; }

    /// <summary>Extent in the frame along the writing direction: left edge.</summary>
    public double Left { get; }

    /// <summary>Extent in the frame along the writing direction: right edge.</summary>
    public double Right { get; }

    /// <summary>Extent in the frame across the writing direction: top edge (largest y, including ascenders).</summary>
    public double Top { get; }

    /// <summary>Extent in the frame across the writing direction: bottom edge (smallest y, including descenders).</summary>
    public double Bottom { get; }

    /// <summary>Right − Left.</summary>
    public double Width => Right - Left;

    /// <summary>The text of all lines, separated by '\n'.</summary>
    public string Text => string.Join('\n', Lines.Select(l => l.Text));

    /// <summary>Frame → page space.</summary>
    public Matrix FrameToPage => Matrix.Rotate(Angle);

    /// <summary>The four corners in page space (bottom left, bottom right, top right, top left).</summary>
    public TextQuad Quad => Outline(0);

    /// <summary>The corners with a margin all round (frame in the view).</summary>
    /// <param name="margin">Margin in points.</param>
    /// <returns>The corners in page space (bottom left, bottom right, top right, top left).</returns>
    public TextQuad Outline(double margin)
    {
        var m = FrameToPage;
        var (ax, ay) = m.Transform(Left - margin, Bottom - margin);
        var (bx, by) = m.Transform(Right + margin, Bottom - margin);
        var (cx, cy) = m.Transform(Right + margin, Top + margin);
        var (dx, dy) = m.Transform(Left - margin, Top + margin);
        return new TextQuad(ax, ay, bx, by, cx, cy, dx, dy);
    }

    /// <summary>Bounds in page space.</summary>
    public RectD Bounds => Quad.Bounds;

    /// <summary>
    /// Joins several blocks of a page into one (KONZEPT.md, section 5.14, „Blöcke zusammenfassen“): a paragraph the detection split, a
    /// heading with its paragraph. The lines follow in reading order (top to bottom, then left to right); each block keeps its own line
    /// breaks, so its last line still ends a paragraph. The detection itself is not changed — the joined block is for this one edit.
    /// </summary>
    /// <param name="blocks">At least two blocks of the same page.</param>
    /// <param name="reason">Why they cannot be joined (translated), if the result is null: different writing directions, or a locked block.</param>
    /// <returns>The joined block, or null.</returns>
    public static PageTextBlock? Joined(IReadOnlyList<PageTextBlock> blocks, out string? reason)
    {
        reason = null;
        if (blocks.Count < 2) return null;
        if (blocks.FirstOrDefault(b => b.Locked is not null) is { } locked)
        {
            reason = locked.Locked;
            return null;
        }
        var angle = blocks[0].Angle;
        if (blocks.Any(b => Math.Abs(((b.Angle - angle) % 360 + 540) % 360 - 180) > 1))
        {
            reason = Tr.T("Blöcke mit verschiedener Schreibrichtung lassen sich nicht zusammenfassen.");
            return null;
        }
        var lines = blocks.SelectMany(b => b.Lines).OrderByDescending(l => l.Baseline).ThenBy(l => l.Start).ToList();
        var alignment = blocks.GroupBy(b => b.Alignment).OrderByDescending(g => g.Sum(b => b.Lines.Count)).First().Key;
        var spacing = blocks.Select(b => b.LineSpacing).Order().ElementAt(blocks.Count / 2);
        return new PageTextBlock(lines, angle, alignment, spacing, null, [.. blocks.SelectMany(b => b.Glyphs).Distinct()],
            [.. blocks.SelectMany(b => b.Paths).Distinct()], blocks.Max(b => b.Top), blocks.Min(b => b.Bottom)) { TabStops = blocks[0].TabStops };
    }

    /// <summary>Tests whether a point lies in the block.</summary>
    /// <param name="x">x in page space.</param>
    /// <param name="y">y in page space.</param>
    /// <param name="margin">Margin in points.</param>
    /// <returns>True if the point lies within the block plus the margin.</returns>
    public bool Contains(double x, double y, double margin = 0)
    {
        var (fx, fy) = FrameToPage.Invert().Transform(x, y);
        return fx >= Left - margin && fx <= Right + margin && fy >= Bottom - margin && fy <= Top + margin;
    }
}

/// <summary>
/// Detects the text blocks of a page. The basis are the characters as the interpreter reports them — with font, colour, text
/// rendering mode and their number in content order (replacing uses the same count; forms and soft masks are counted too). The
/// detection itself lives in <c>Layout/</c>: per writing direction a chain of named steps (<see cref="Steps"/>).
/// See KONZEPT.md, section 5.14.
/// </summary>
public static partial class TextBlocks
{
    /// <summary>Pages with more characters are not split into blocks (plans with hundreds of thousands of labels).</summary>
    public const int MaxGlyphs = 200_000;

    /// <summary>Detects all blocks of the page (without annotations).</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="cancel">Cancels the detection.</param>
    /// <returns>The blocks; empty with too many characters (<see cref="MaxGlyphs"/>).</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was triggered.</exception>
    public static IReadOnlyList<PageTextBlock> Find(PdfDocument doc, PdfPage page, CancellationToken cancel = default)
    {
        if (Collect(doc, page, cancel) is not { } found) return [];
        var blocks = Build(found.Glyphs, found.Paths, null, cancel);
        LockSignatureOrigins(doc, blocks);
        return blocks;
    }

    internal static string SignatureLock => Tr.T("Herkunftszeile einer festgeschriebenen Unterschrift — nicht bearbeitbar.");

    /// <summary>
    /// The origin line under a finalised signature (name, date, device code) is text of the page but belongs to the signature:
    /// lock a block if it contains an origin or code from the records in the file (<see cref="Editing.SealRecord"/>); otherwise the
    /// proof under the signature could be rewritten. Because the records are read from the file, this also covers files saved earlier.
    /// </summary>
    private static void LockSignatureOrigins(PdfDocument doc, IReadOnlyList<PageTextBlock> blocks)
    {
        if (blocks.Count == 0) return;
        // read once per document and capped: otherwise a crafted file with 20,000 records cost 350 ms per page
        var marks = SealMarks.GetValue(doc, d => [.. Editing.SealRecord.Read(d)
            .SelectMany(r => (string[])[Compact(r.Seal.Origin), Compact(r.Seal.Code)])
            .Where(m => m.Length >= 6).Distinct().Take(MaxSealMarks)]);
        if (marks.Length == 0) return;
        foreach (var block in blocks)
        {
            var text = Compact(block.Text);
            if (marks.Any(m => text.Contains(m, StringComparison.Ordinal))) block.Lock(SignatureLock);
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfDocument, string[]> SealMarks = new();
    private const int MaxSealMarks = 256;

    /// <summary>Text without white space (the lines and spaces of the detection differ from the text as set).</summary>
    private static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>Collects the visible characters and lines of the page as the detection sees them (also used for measurements).</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="cancel">Cancels the run.</param>
    /// <returns>Characters and flat paths; null with too many characters.</returns>
    public static (IReadOnlyList<Glyph> Glyphs, IReadOnlyList<PathMark> Paths)? Collect(PdfDocument doc, PdfPage page, CancellationToken cancel = default)
    {
        var collector = new Collector(cancel, page.CropBox);
        try
        {
            PageInterpreter.Observe(doc, page, collector, cancel);
        }
        catch (TooManyGlyphsException)
        {
            return null;
        }
        return (collector.Glyphs, collector.Paths);
    }

    // ------------------------------------------------------------ Collecting characters

    /// <summary>
    /// A character with its look, in page space. <see cref="Symbol"/>: from a symbol font — locks its block unless it is the block's list
    /// marker (office programs set bullets in Symbol). <see cref="CodeFont"/>: from a bar code font — its block is changed through the code
    /// tool (phase 8.2).
    /// </summary>
    /// <param name="Ordinal">Number of the character in content order (counting all reported characters, also hidden ones).</param>
    /// <param name="Text">Unicode text; null if unknown.</param>
    /// <param name="X">x of the origin.</param>
    /// <param name="Y">y of the origin.</param>
    /// <param name="EndX">x of the origin moved by the advance.</param>
    /// <param name="EndY">y of the origin moved by the advance.</param>
    /// <param name="UpX">x of the unit vector perpendicular to the writing direction (upwards for upright text).</param>
    /// <param name="UpY">y of that unit vector.</param>
    /// <param name="Ascent">Largest extent along the up vector from the origin, in page units.</param>
    /// <param name="Descent">Smallest extent along the up vector from the origin (usually negative).</param>
    /// <param name="Format">The formatting.</param>
    /// <param name="Locked">Why this character makes its block uneditable; null = editable.</param>
    /// <param name="Symbol">From a symbol font.</param>
    /// <param name="CodeFont">From a bar code font (<see cref="Barcodes.CodeFonts"/>).</param>
    public sealed record Glyph(int Ordinal, string? Text, double X, double Y, double EndX, double EndY, double UpX, double UpY,
        double Ascent, double Descent, TextFormat Format, string? Locked, bool Symbol = false, bool CodeFont = false);

    /// <summary>Why a block with a symbol font is locked.</summary>
    internal static string SymbolLock => Tr.T("Symbolschrift — nicht bearbeitbar (neuer Text ergäbe ein falsches Zeichen).");

    /// <summary>Why a block with a bar code font is not edited as text: the code tool makes the code anew (phase 8.2).</summary>
    internal static string CodeFontLock => Tr.T("Code-Schrift — wird über das Code-Werkzeug geändert (Klick zeigt den Code).");

    /// <summary>A painted path (candidate for an underline): number and bounds in page space.</summary>
    /// <param name="Ordinal">Number of the path in content order.</param>
    /// <param name="Bounds">Bounds in page space, including the line width.</param>
    public readonly record struct PathMark(int Ordinal, RectD Bounds);

    private sealed class TooManyGlyphsException : Exception;

    private sealed class Collector(CancellationToken cancel, PdfRectangle crop) : IContentObserver
    {
        public readonly List<Glyph> Glyphs = [];
        public readonly List<PathMark> Paths = [];
        private int _glyphOrdinal, _pathOrdinal, _maskDepth;
        private readonly Stack<bool> _scopes = new();
        private GlyphStyle _style;
        private TextFormat? _format;
        private bool _symbol;   // symbol font (determined per formatting, not per character)
        private bool _codeFont;   // bar code font (likewise)
        private (GlyphStyle, double, double, double, double, bool) _formatKey;

        public bool WantsStyle => true;

        public void Style(in GlyphStyle style) => _style = style;

        private bool _type3Paints, _type3Guessed;

        public void GlyphType3(bool paints, bool textGuessed) => (_type3Paints, _type3Guessed) = (paints, textGuessed);

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize, double horizontalScale,
            bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible)
        {
            var ordinal = _glyphOrdinal++;
            if ((ordinal & 4095) == 0) cancel.ThrowIfCancellationRequested();
            // discarded characters (outside the page, hidden) count towards a limit as well: otherwise a page with millions of characters outside
            // would run through completely (5.5 s instead of 0.7 s, found by fuzzing), because the limit for kept characters never applies there
            if (ordinal >= 2 * MaxGlyphs) throw new TooManyGlyphsException();
            if (hidden || _maskDepth > 0) return;   // hidden layer, content of a mask: not visible
            if (Glyphs.Count >= MaxGlyphs) throw new TooManyGlyphsException();
            var along = textToPage.TransformVector(1, 0);
            var up = textToPage.TransformVector(0, 1);
            double alongLength = Math.Sqrt(along.X * along.X + along.Y * along.Y), upLength = Math.Sqrt(up.X * up.X + up.Y * up.Y);
            if (alongLength < 1e-9 || upLength < 1e-9 || !double.IsFinite(alongLength + upLength)) return;   // without area: not visible
            var size = fontSize * upLength;
            if (size < 0.5) return;
            double dirX = along.X / alongLength, dirY = along.Y / alongLength;
            // perpendicular to the text (upwards if it is upright); slanted = synthetic italic
            double normalX = -dirY, normalY = dirX;
            var cross = along.X * up.Y - along.Y * up.X;
            var mirrored = cross < 0;
            var skew = Math.Abs(dirX * up.X + dirY * up.Y) / upLength;   // cos of the angle between the axes
            var (x, y) = textToPage.Transform(0, 0);
            var (endX, endY) = vertical ? textToPage.Transform(0, advance) : textToPage.Transform(advance, 0);
            // ascent and descent across the baseline (from the cell, including rise): the four corners without an array per character
            double ascent = double.MinValue, descent = double.MaxValue;
            void Corner(double cx, double cy)
            {
                var (px, py) = cellToPage.Transform(cx, cy);
                var d = (px - x) * normalX + (py - y) * normalY;
                ascent = Math.Max(ascent, d);
                descent = Math.Min(descent, d);
            }
            Corner(cell.Left, cell.Top);
            Corner(cell.Left, cell.Bottom);
            Corner(cell.Right, cell.Top);
            Corner(cell.Right, cell.Bottom);
            // infinite or absurdly large positions (crafted file: "Td" with 400 digits) do not count; otherwise they would run through the
            // column search as NaN
            const double far = 1e7;
            if (!(Math.Abs(x) < far && Math.Abs(y) < far && Math.Abs(endX) < far && Math.Abs(endY) < far && Math.Abs(ascent) < far && Math.Abs(descent) < far)) return;
            // entirely outside the visible page (bleed area): nobody sees it, so no block and no frame beside the page
            if (!crop.IsEmpty)
            {
                double minX = Math.Min(x, endX) + Math.Min(normalX * ascent, normalX * descent), maxX = Math.Max(x, endX) + Math.Max(normalX * ascent, normalX * descent);
                double minY = Math.Min(y, endY) + Math.Min(normalY * ascent, normalY * descent), maxY = Math.Max(y, endY) + Math.Max(normalY * ascent, normalY * descent);
                if (maxX < crop.Left - 1 || minX > crop.Right + 1 || maxY < crop.Bottom - 1 || minY > crop.Top + 1) return;
            }
            // consecutive characters almost always look the same: determine the formatting again only when something changes
            var key = (_style, Math.Round(size, 3), Math.Round(horizontalScale * alongLength / upLength, 4), Math.Round(alongLength, 4), Math.Round(upLength, 4), skew > 0.08);
            if (_format is null || !key.Equals(_formatKey))
            {
                _format = Format(_style, size, horizontalScale, alongLength, upLength, skew > 0.08);
                _codeFont = Barcodes.CodeFonts.IsCodeFont(_format.FontName);
                _symbol = !_codeFont && SymbolFont(_format.FontName);
                _formatKey = key;
            }
            var format = _format;
            string? locked = null;
            if (vertical) locked = Tr.T("Senkrechte Schrift — nicht bearbeitbar.");
            else if (mirrored) locked = Tr.T("Gespiegelte Schrift — nicht bearbeitbar.");
            else if (invisible) locked = Tr.T("Unsichtbarer Text (meist die Texterkennung über einem Scan) — nicht bearbeitbar.");
            // Type 3 fonts are editable (phase 8.1, No. 606); a letter whose procedure paints nothing stays locked, so that invisible text
            // cannot be slipped in (a blank space — white space that paints nothing — is fine: No. 489)
            // a Type 3 letter whose text is only guessed from its code (No. 611): no readable text for editing
            else if (_style.Font.IsType3 && _type3Guessed && !string.IsNullOrWhiteSpace(text)) locked = Tr.T("Zeichen ohne lesbaren Text — nicht bearbeitbar.");
            else if (_style.Font.IsType3 && !_type3Paints && !string.IsNullOrWhiteSpace(text))
                locked = Tr.T("Zeichen einer Type-3-Schrift, die nichts zeichnen — nicht bearbeitbar.");
            else if (_style.TextMode is not (0 or 2)) locked = Tr.T("Kontur- oder Beschneidungsschrift — nicht bearbeitbar.");
            else if (_style.FillIsPattern) locked = Tr.T("Mit Muster oder Verlauf gefüllte Schrift — nicht bearbeitbar.");
            else if (text is null || text.Length == 0 || text.Contains('�')) locked = Tr.T("Zeichen ohne lesbaren Text — nicht bearbeitbar.");
            Glyphs.Add(new Glyph(ordinal, text, x, y, endX, endY, normalX, normalY, ascent, descent, format, locked, _symbol, _codeFont));
        }

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden)
        {
            var ordinal = _pathOrdinal++;
            if (hidden || _maskDepth > 0) return;
            // underlines: flat and wider than high
            if (bounds.Height < 6 && bounds.Width > 2 * bounds.Height && Paths.Count < MaxGlyphs) Paths.Add(new PathMark(ordinal, bounds));
        }

        public void Image(int op, Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool hidden) { }

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden) => _scopes.Push(false);

        public void BeginSoftMask(int op, PdfStream group, Matrix groupToPage)
        {
            _scopes.Push(true);
            _maskDepth++;
        }

        public void EndForm()
        {
            if (_scopes.Count > 0 && _scopes.Pop()) _maskDepth--;
        }

        /// <summary>
        /// Formatting from the look of the character. <paramref name="along"/>/<paramref name="up"/>: text space → page along and across the
        /// text; spacing applies in unscaled text space (9.3), along the text additionally with the horizontal scaling.
        /// </summary>
        private static TextFormat Format(in GlyphStyle style, double size, double horizontalScale, double along, double up, bool skewed)
        {
            var dict = style.Font.Dictionary;
            var name = StripSubset(style.Font.BaseFont);
            var descriptor = FontDescriptor(dict);
            var flags = (int)(descriptor?.GetInteger(PdfName.Get("Flags")) ?? 0);
            var weight = descriptor?.GetNumber(PdfName.Get("FontWeight")) ?? 0;
            var italicAngle = descriptor?.GetNumber(PdfName.Get("ItalicAngle")) ?? 0;
            var lower = name.ToLowerInvariant();
            var bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("semibold") || lower.Contains("demi")
                       || weight >= 600 || (flags & 0x40000) != 0;
            var italic = lower.Contains("italic") || lower.Contains("oblique") || lower.Contains("kursiv") || Math.Abs(italicAngle) > 0.5 || (flags & 64) != 0;
            // synthetic bold: filled and outlined in the same colour (with a noticeably wide stroke)
            // (not with Type 3: its glyph procedures paint themselves, the outline of mode 2 adds nothing — No. 606)
            var fakeBold = !bold && !style.Font.IsType3 && style.TextMode == 2 && style.LineWidth > size * 0.008 && SameColor(style.FillArgb, style.StrokeArgb);
            var fakeItalic = !italic && skewed;
            var color = style.TextMode == 1 ? style.StrokeArgb : style.FillArgb;
            return new TextFormat
            {
                FontName = name,
                Family = Family(name),
                Bold = bold || fakeBold,
                Italic = italic || fakeItalic,
                FakeBold = fakeBold,
                FakeItalic = fakeItalic,
                Size = size,
                Color = (int)(color & 0xFFFFFF),
                Stretch = horizontalScale * along / up,
                CharSpacing = style.CharSpacing * horizontalScale * along,
                WordSpacing = style.WordSpacing * horizontalScale * along,
                Rise = style.Rise * up,
                Font = dict,
                FontId = style.Font.Id,
            };
        }

        private static bool SameColor(uint a, uint b)
        {
            int Diff(int shift) => Math.Abs((int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF));
            return Diff(16) < 24 && Diff(8) < 24 && Diff(0) < 24;
        }
    }

    private static PdfDictionary? FontDescriptor(PdfDictionary? font)
    {
        if (font is null) return null;
        if (font.GetDictionary(PdfName.Get("FontDescriptor")) is { } descriptor) return descriptor;
        // composite font: the descriptor belongs to the descendant font
        return font.GetArray(PdfName.Get("DescendantFonts"))?.Get(0) is PdfDictionary descendant ? descendant.GetDictionary(PdfName.Get("FontDescriptor")) : null;
    }

    /// <summary>Removes a subset prefix: "ABCDEF+Arial,Bold" → "Arial,Bold" (six capital letters and "+").</summary>
    /// <param name="name">The /BaseFont name.</param>
    /// <returns>The name without the prefix; unchanged if there is none.</returns>
    public static string StripSubset(string name) =>
        name.Length > 7 && name[6] == '+' && name.AsSpan(0, 6).IndexOfAnyExceptInRange('A', 'Z') < 0 ? name[7..] : name;

    private static readonly string[] StyleWords =
        ["BoldItalic", "BoldOblique", "SemiBold", "Semibold", "DemiBold", "Demibold", "ExtraBold", "Bold", "Black", "Heavy", "Italic", "Oblique",
         "Regular", "Roman", "Book", "Medium", "Light", "Normal", "PSMT", "PS", "MT"];

    /// <summary>Family without style: "Arial,Bold", "Arial-BoldMT", "ArialMT" → "Arial"; "TimesNewRomanPSMT" → "TimesNewRoman".</summary>
    /// <param name="name">The font name without subset prefix.</param>
    /// <returns>The family name.</returns>
    public static string Family(string name)
    {
        var family = name;
        var cut = family.IndexOfAny([',', '-']);
        if (cut > 0) family = family[..cut];
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var word in StyleWords)
                if (family.Length > word.Length + 1 && family.EndsWith(word, StringComparison.Ordinal))
                {
                    family = family[..^word.Length];
                    changed = true;
                }
        }
        return family;
    }

    // (bar code fonts: Barcodes.CodeFonts; „ocr-b“ is a readable font, kept here as before — noted in the plan of phase 8.2)
    private static readonly string[] SymbolWords = ["symbol", "dingbat", "wingding", "webding", "marlett", "ocr-b", "ocrb", "pdf417"];

    private static bool SymbolFont(string name)
    {
        var lower = name.ToLowerInvariant();
        return SymbolWords.Any(lower.Contains);
    }
}

/// <summary>
/// Tab stop in points from the left edge: left (the text starts there), right (it ends there — amounts, page numbers) or centred.
/// Justify does not occur.
/// </summary>
/// <param name="Position">Distance from the left edge of the block in points.</param>
/// <param name="Alignment">How the text after the tab aligns to the stop.</param>
public readonly record struct TabStop(double Position, BlockAlignment Alignment = BlockAlignment.Left);
