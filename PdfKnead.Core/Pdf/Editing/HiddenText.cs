using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>Why nobody sees a hidden character (KONZEPT.md, section 5.18).</summary>
public enum HiddenTextKind
{
    /// <summary>Text rendering mode 3 (invisible), for example the text recognition (OCR) over a scan.</summary>
    Invisible,

    /// <summary>On a hidden layer (optional content switched off).</summary>
    HiddenLayer,

    /// <summary>Beside the page or clipped away completely.</summary>
    Outside,

    /// <summary>Smaller than 0.01 pt (font size 0, horizontal scaling 0).</summary>
    Tiny,

    /// <summary>Painted, but nobody sees it — measured by drawing (<see cref="TextVisibility"/>): white on white, the colour of its background,
    /// transparent, covered, clipped away by a clip of any shape, or painting nothing at all.</summary>
    Suspicious,
}

/// <summary>
/// A spot with hidden text: characters of the same kind lying together, in page space. <see cref="Lines"/> are the lines (one area
/// each), <see cref="Text"/> what they say. <see cref="OutsidePage"/>: the spot lies entirely beside the visible page.
/// </summary>
/// <param name="Kind">Why nobody sees it.</param>
/// <param name="Bounds">Bounds of all lines.</param>
/// <param name="Lines">The lines in page space.</param>
/// <param name="Text">The whole text (lines separated by line breaks); only if the page's spots together exceed
/// <see cref="HiddenText.MaxPageText"/> characters, the rest is cut off and the text ends in „…“.</param>
/// <param name="OutsidePage">Lies entirely beside the visible page.</param>
/// <param name="Glyphs">Number of characters.</param>
public sealed record HiddenTextSpot(HiddenTextKind Kind, PdfRectangle Bounds, IReadOnlyList<PdfRectangle> Lines, string Text, bool OutsidePage, int Glyphs);

/// <summary>Result of <see cref="HiddenText.Scan"/>: the spots of a page and whether every character was decided.</summary>
/// <param name="Spots">The spots, in content order.</param>
/// <param name="Complete">False if a limit left characters undecided (more than <see cref="HiddenText.MaxSpots"/> spots or
/// <see cref="HiddenText.MaxGlyphs"/> characters, a work limit of the measurement, a truncated display list); they are not among the spots.</param>
public sealed record HiddenTextScan(List<HiddenTextSpot> Spots, bool Complete);

/// <summary>
/// „Verborgenes schwärzen“ (KONZEPT.md, section 5.18): finds text in the page content that nobody sees — invisible (mode 3), on a hidden
/// layer, beside the page or clipped away, tiny, or painted but not to be seen (measured by drawing, <see cref="TextVisibility"/>). The
/// same test decides what <see cref="Redactor.RemoveHiddenIn"/> removes, so that what is shown is exactly what goes.
/// </summary>
public static class HiddenText
{
    /// <summary>
    /// At most this many characters of text for all spots of a page together. The text is kept whole so that it can be read in full
    /// without searching again; the budget only stops a crafted file (long ToUnicode strings for each of many characters) from filling
    /// the memory.
    /// </summary>
    public const int MaxPageText = 1_000_000;

    /// <summary>Smallest side of a mark in points: a spot of tiny text still gets an area that can be marked and applied.</summary>
    public const double MinMarkSize = 4;

    /// <summary>At most this many spots per page.</summary>
    public const int MaxSpots = 5000;

    /// <summary>At most this many hidden characters per page.</summary>
    public const int MaxGlyphs = 500_000;

    /// <summary>Finds the spots with hidden text on a page, in content order (<see cref="Scan"/> without telling whether it is complete).</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <param name="probe">Measures visibility by drawing; null = <see cref="TextVisibility.DefaultProbe"/>.</param>
    /// <returns>The spots; empty if there are none.</returns>
    public static List<HiddenTextSpot> Find(PdfDocument doc, PdfPage page, CancellationToken cancel = default, IVisibilityProbe? probe = null) =>
        Scan(doc, page, cancel, probe).Spots;

    /// <summary>Finds the spots with hidden text on a page and says whether every character was decided.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <param name="probe">Measures visibility by drawing; null = <see cref="TextVisibility.DefaultProbe"/>.</param>
    /// <returns>The spots and whether the search is complete.</returns>
    public static HiddenTextScan Scan(PdfDocument doc, PdfPage page, CancellationToken cancel = default, IVisibilityProbe? probe = null) =>
        Scan(doc, page, MaxPageText, cancel, probe);

    /// <summary>
    /// Like <see cref="Scan(PdfDocument, PdfPage, CancellationToken, IVisibilityProbe?)"/>, but draws only for the characters that touch
    /// <paramref name="areas"/> — for a question about a few places of a page (does the text there show?) instead of all of it: measuring every
    /// character of a page full of text took seconds per page on a damaged file. Characters elsewhere are decided only by the checks without
    /// drawing, so spots of covered text there may be missing; <see cref="HiddenTextScan.Complete"/> speaks of the areas.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="areas">The places that matter, in page space.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <param name="probe">Measures visibility by drawing; null = <see cref="TextVisibility.DefaultProbe"/>.</param>
    /// <returns>The spots and whether the search in the areas is complete.</returns>
    public static HiddenTextScan Scan(PdfDocument doc, PdfPage page, IReadOnlyList<PdfRectangle> areas, CancellationToken cancel = default,
        IVisibilityProbe? probe = null) =>
        Scan(doc, page, MaxPageText, cancel, probe, areas);

    /// <summary><see cref="Scan(PdfDocument, PdfPage, CancellationToken, IVisibilityProbe?)"/> with a given text budget for the page.</summary>
    internal static HiddenTextScan Scan(PdfDocument doc, PdfPage page, int maxText, CancellationToken cancel = default, IVisibilityProbe? probe = null,
        IReadOnlyList<PdfRectangle>? areas = null)
    {
        // First without drawing: what is decided by itself — invisible (mode 3), on a hidden layer, beside the page, tiny. Only the characters
        // left are measured by drawing, and only where they lie; with none left the page is not drawn at all. The text of a scan lies
        // invisible over its image: drawing each page with its large image took the analysis of „Dokument bereinigen“ 17 s for 82 pages
        // (docs/FEHLERGEDAECHTNIS.md, No. 704)
        var box = HiddenContent.VisibleBox(page);
        var first = new Test(RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top), null);
        var collected = new FindObserver(first, maxText);
        PageInterpreter.Observe(doc, page, collected, cancel);
        var undecided = first.Undecided;
        if (areas is not null)
        {
            // only the characters at the places that matter are drawn
            var wanted = areas.Select(a => RectD.FromLtrb(a.Left, a.Bottom, a.Right, a.Top)).ToList();
            undecided = [.. undecided.Where(u => wanted.Any(u.Intersects))];
        }
        if (undecided.Count == 0) return new HiddenTextScan(collected.Finish(first.Visible), first.Complete && !collected.Truncated);
        var test = Test.For(doc, page, cancel, probe, undecided);
        var observer = new FindObserver(test, maxText);
        PageInterpreter.Observe(doc, page, observer, cancel);
        return new HiddenTextScan(observer.Finish(test.Visible), test.Complete && !observer.Truncated);
    }

    /// <summary>The areas to mark for a spot: its lines, each enlarged to at least <see cref="MinMarkSize"/> around its centre.</summary>
    /// <param name="spot">The spot.</param>
    /// <returns>The areas in page space.</returns>
    public static List<PdfRectangle> MarkAreas(HiddenTextSpot spot) => [.. spot.Lines.Select(AtLeast)];

    private static PdfRectangle AtLeast(PdfRectangle r)
    {
        double cx = (r.Left + r.Right) / 2, cy = (r.Bottom + r.Top) / 2, w = Math.Max(r.Width, MinMarkSize) / 2, h = Math.Max(r.Height, MinMarkSize) / 2;
        return new PdfRectangle(Math.Min(r.Left, cx - w), Math.Min(r.Bottom, cy - h), Math.Max(r.Right, cx + w), Math.Max(r.Top, cy + h));
    }

    /// <summary>
    /// The test of a character, shared by finding and removing: the same visible box, the same clip, the same measured visibility. A
    /// character is identified by its number in content order (<see cref="Content.IContentObserver.GlyphSerial"/>), not by its position:
    /// a visible character exactly over a hidden one stays.
    /// </summary>
    internal sealed class Test(RectD visible, TextVisibility.Result? measured)
    {
        /// <summary>
        /// Without a measurement (<c>measured</c> null): the places of the characters that the cheap checks leave undecided (their cell with its
        /// ink, page space) — they are the ones to measure by drawing.
        /// </summary>
        public List<RectD> Undecided { get; } = [];

        private RectD _clip = RectD.Infinite;
        private int _serial = -1;

        public RectD Visible { get; } = visible;

        /// <summary>Every character was decided (no work limit, no truncated display list); otherwise the rest counts as visible.</summary>
        public bool Complete { get; private set; } = measured?.Complete ?? true;

        /// <summary>Builds the test for a page: the display list with tagged characters, measured by drawing.</summary>
        /// <param name="only">If given, only characters touching these areas are measured (all others count as visible).</param>
        public static Test For(PdfDocument doc, PdfPage page, CancellationToken cancel, IVisibilityProbe? probe = null, IReadOnlyList<RectD>? only = null)
        {
            var box = HiddenContent.VisibleBox(page);
            return new Test(RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top), TextVisibility.MeasurePage(doc, page, probe, cancel, only));
        }

        public void Clip(RectD bounds) => _clip = bounds;

        /// <summary>Number of the next character (reported before it).</summary>
        public void Serial(int serial) => _serial = serial;

        // Per open form, mask or pattern cell: the pattern if it is a cell — characters directly in a cell are measured by their place in
        // it (Content.CellGlyph), since they have no number
        private readonly Stack<PdfStream?> _open = new();

        /// <summary>The content of a pattern cell starts (<see cref="IContentObserver.BeginPatternCell"/>).</summary>
        public void BeginCell(PdfStream pattern) => _open.Push(pattern);

        /// <summary>A form or soft mask starts.</summary>
        public void BeginNested() => _open.Push(null);

        /// <summary>The form, mask or cell ends (<see cref="IContentObserver.EndForm"/>).</summary>
        public void End() => _open.TryPop(out _);

        /// <summary>Why nobody sees the character, or null if it is visible; <paramref name="bounds"/> is its cell in page space.</summary>
        /// <param name="op">Number of the text operator in its content (for characters in pattern cells).</param>
        /// <param name="start">Byte of the character in the shown string (for characters in pattern cells).</param>
        public HiddenTextKind? Classify(RectD cell, RectD ink, Matrix cellToPage, bool hidden, bool invisible, out RectD bounds, int op = -1, int start = -1)
        {
            bounds = cellToPage.TransformBounds(cell);
            if (invisible) return HiddenTextKind.Invisible;
            if (hidden) return HiddenTextKind.HiddenLayer;
            var all = cellToPage.TransformBounds(cell.Union(ink));
            if (!all.Intersects(Visible) || !all.Intersects(_clip)) return HiddenTextKind.Outside;
            if (Tiny(cellToPage, cell)) return HiddenTextKind.Tiny;
            if (measured is null)
            {
                Undecided.Add(all);
                return null;
            }
            if (_serial < 0 && _open.TryPeek(out var pattern) && pattern is not null && measured.IsHiddenCell(new CellGlyph(pattern, op, start)))
                return HiddenTextKind.Suspicious;
            if (measured.IsHidden(_serial)) return HiddenTextKind.Suspicious;
            if (_serial >= 0 && !measured.Knows(_serial)) Complete = false;   // beyond a truncated display list
            return null;
        }

        /// <summary>Cell smaller than 0.01 pt in one direction (or of almost no area) — as when sanitising.</summary>
        internal static bool Tiny(Matrix cellToPage, RectD cell)
        {
            var (ax, ay) = cellToPage.TransformVector(cell.Width, 0);
            var (bx, by) = cellToPage.TransformVector(0, cell.Height);
            return Math.Abs(ax * by - ay * bx) < 1e-4 || Math.Sqrt(ax * ax + ay * ay) < 0.01 || Math.Sqrt(bx * bx + by * by) < 0.01;
        }
    }

    /// <summary>Whether the centre of a character's cell lies in one of the areas (marks of hidden text are areas around such cells).</summary>
    internal static bool Within(List<RectD> areas, RectD bounds)
    {
        double x = (bounds.Left + bounds.Right) / 2, y = (bounds.Top + bounds.Bottom) / 2;
        foreach (var a in areas)
            if (x >= a.Left - 0.01 && x <= a.Right + 0.01 && y >= a.Top - 0.01 && y <= a.Bottom + 0.01) return true;
        return false;
    }

    /// <summary><see cref="Within(List{RectD}, RectD)"/> for many areas: only those near the centre are tested.</summary>
    internal static bool Within(AreaIndex areas, RectD bounds)
    {
        double x = (bounds.Left + bounds.Right) / 2, y = (bounds.Top + bounds.Bottom) / 2;
        return areas.Any(RectD.FromLtrb(x, y, x, y), 0.02, a => x >= a.Left - 0.01 && x <= a.Right + 0.01 && y >= a.Top - 0.01 && y <= a.Bottom + 0.01);
    }

    /// <summary>Collects the hidden characters and joins neighbouring ones of the same kind into spots and lines.</summary>
    private sealed class FindObserver(Test test, int maxText) : IContentObserver
    {
        private readonly List<Builder> _spots = [];
        private readonly TextBudget _budget = new() { Left = maxText };
        private Builder? _current;
        private int _glyphs;

        /// <summary>A limit (spots or characters) left hidden characters out.</summary>
        public bool Truncated { get; private set; }

        public bool WantsClip => true;

        public bool HiddenOnlyIfUnprinted => true;

        public void Clip(RectD bounds) => test.Clip(bounds);

        public void GlyphSerial(int serial) => test.Serial(serial);

        public void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize, double horizontalScale,
            bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible)
        {
            if (test.Classify(cell, ink, cellToPage, hidden, invisible, out var bounds, op, start) is not { } kind) return;
            if (++_glyphs > MaxGlyphs)
            {
                Truncated = true;
                return;
            }
            if (_current is null || _current.Kind != kind || !_current.Takes(bounds))
            {
                if (_spots.Count >= MaxSpots)
                {
                    Truncated = true;
                    return;
                }
                _current = new Builder(kind, _budget);
                _spots.Add(_current);
            }
            _current.Add(bounds, text);
        }

        public void Path(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void Image(int op, Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool hidden) { }

        public void Shading(int op, RectD bounds, Matrix ctm, bool hidden) { }

        public void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden) => test.BeginNested();

        public void BeginSoftMask(int op, PdfStream group, Matrix groupToPage) => test.BeginNested();

        // the content of tiling patterns: hidden text in a cell is found like that on the page (docs/FEHLERGEDAECHTNIS.md, No. 233)
        public bool WantsPatternCell(RectD painted) => true;

        public void BeginPatternCell(int op, bool stroke, PdfStream pattern, Matrix cellToPage) => test.BeginCell(pattern);

        public void EndForm() => test.End();

        public List<HiddenTextSpot> Finish(RectD visible) => [.. _spots.Select(s => s.Build(visible))];
    }

    /// <summary>A spot being collected: its lines (page space, y upwards as <see cref="RectD"/> stores it here: Top = lower y) and its text.</summary>
    /// <summary>The characters of text still allowed on the page (<see cref="MaxPageText"/>).</summary>
    private sealed class TextBudget
    {
        public int Left;
    }

    private sealed class Builder(HiddenTextKind kind, TextBudget budget)
    {
        private readonly List<RectD> _lines = [];
        private readonly StringBuilder _text = new();
        private RectD _bounds = RectD.Empty;
        private RectD _last = RectD.Empty;
        private int _count;
        private bool _cut;

        public HiddenTextKind Kind => kind;

        /// <summary>Close to the spot (within one and a half line heights): it belongs to it.</summary>
        public bool Takes(RectD glyph)
        {
            var reach = Math.Max(Math.Max(glyph.Height, _last.Height), 2) * 1.5;
            return Box(_bounds).Inflate(reach).Intersects(Box(glyph));
        }

        public void Add(RectD glyph, string? text)
        {
            var box = Box(glyph);
            var height = Math.Max(Math.Max(box.Height, _last.Height), 0.5);
            var sameLine = _count > 0 && Math.Abs((box.Top + box.Bottom) / 2 - (_last.Top + _last.Bottom) / 2) < height / 2;
            if (_count == 0 || !sameLine)
            {
                if (_count > 0) Append("\n");
                _lines.Add(box);
            }
            else
            {
                if (box.Left - _last.Right > height * 0.25) Append(" ");
                _lines[^1] = _lines[^1].Union(box);
            }
            Append(string.IsNullOrEmpty(text) ? "?" : text);
            _bounds = _count == 0 ? box : _bounds.Union(box);
            _last = box;
            _count++;
        }

        public HiddenTextSpot Build(RectD visible)
        {
            static PdfRectangle Pdf(RectD r) => new(r.Left, r.Top, r.Right, r.Bottom);
            return new HiddenTextSpot(kind, Pdf(_bounds), [.. _lines.Select(Pdf)], _cut ? _text + " …" : _text.ToString(), !_bounds.Intersects(visible), _count);
        }

        /// <summary>Appends text within the page's budget; what does not fit is cut off (once marked by „…“).</summary>
        private void Append(string text)
        {
            if (_cut) return;
            if (text.Length > budget.Left)
            {
                _text.Append(text.AsSpan(0, budget.Left));
                budget.Left = 0;
                _cut = true;
                return;
            }
            _text.Append(text);
            budget.Left -= text.Length;
        }

        /// <summary>A cell with at least a tiny area (tiny text would otherwise not count as a rectangle when joining).</summary>
        private static RectD Box(RectD r) => r.IsEmpty
            ? RectD.FromLtrb(Math.Min(r.Left, r.Right), Math.Min(r.Top, r.Bottom), Math.Max(r.Left, r.Right) + 0.01, Math.Max(r.Top, r.Bottom) + 0.01)
            : r;
    }
}
