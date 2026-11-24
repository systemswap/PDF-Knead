using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Content;

/// <summary>
/// Processes the content of a page (ISO 32000-2, clauses 8 and 9) and builds a
/// <see cref="DisplayList"/> from it: paths, fills, strokes, clipping, colours, transparency
/// (groups, soft masks), images, text (all font types, text rendering modes including text as a clip),
/// form XObjects, optional content (layers), annotations, shadings and tiling patterns.
/// </summary>
public sealed partial class PageInterpreter
{
    /// <summary>How deep forms in forms are drawn; the writer writes the content down to this depth in the form of 7.3.3 (No. 934).</summary>
    internal const int MaxFormDepth = 24;

    private readonly PdfDocument _doc;
    private readonly DisplayListBuilder _builder = new();
    private readonly Stack<GraphicsState> _stack = new();
    // Saved states of the content being run: a form or pattern cell may restore only its own (a "Q" too many there ended the matrix and
    // clip of whoever invoked it — a pasted form with a stray "Q" stood where it was copied from, docs/FEHLERGEDAECHTNIS.md, No. 256)
    private int _stackFloor;
    private readonly HashSet<PdfStream> _activeForms = new(ReferenceEqualityComparer.Instance);
    private readonly OptionalContent _optional;

    // For an observer that counts content as hidden only if it is not printed either (IContentObserver.HiddenOnlyIfUnprinted)
    private OptionalContent? _printOptional;

    /// <summary>Content marked with <paramref name="oc"/> is hidden: off on the screen and, for such an observer, also when printed.</summary>
    private bool LayerHidden(PdfObject? oc, PdfDictionary? resources) =>
        _optional.IsHidden(oc, resources) && (_printOptional is null || _printOptional.IsHidden(oc, resources));
    private readonly Dictionary<PdfObject, ColorSpace> _spaceCache = new(ReferenceEqualityComparer.Instance);
    private GraphicsState _gs = new();
    private int _formDepth;

    // Work limit per page: forms that call each other many times grow exponentially, even without painting anything
    // (in which case the limit of the display list does not apply). 16 million operators are several times a large CAD plan (an A0
    // plan: about 780,000 entries); with 50 million a crafted cascade ran for more than 40 s.
    private const long MaxOperations = 16_000_000;

    /// <summary>
    /// Work limit (in operators) for the appearance of a single annotation (overlay above the tiles, text): real stamps and
    /// fields need a fraction of it. Without it, an inflated appearance shared by many annotations would cost up to the page
    /// limit for every annotation (found by fuzzing: out of memory).
    /// </summary>
    public const long MaxAnnotationOperations = 1_000_000;

    private long _maxOperations = MaxOperations;
    private long _operations;
    private CancellationToken _cancel;

    // Current path in user space (transformed when painted, with the CTM valid at that moment)
    private readonly List<PathVerb> _verbs = new(64);
    private readonly List<float> _points = new(128);
    private double _cx, _cy, _sx, _sy;   // current point and start of the subpath
    private bool? _pendingClipEvenOdd;

    // Optional content: for each nested BDC/BMC, whether it is hidden
    private readonly Stack<bool> _hiddenStack = new();
    private int _hiddenDepth;
    private bool _withoutOwnElements;

    // Visibility of text in pattern cells: characters of the cells left out (the cell being built, when building one)
    private IReadOnlySet<CellGlyph>? _withoutCellGlyphs;
    private PdfStream? _cellPattern;

    // Text only (search, selection without a display list): paths, painting, images, shadings, masks and glyph outlines are skipped
    private bool _textOnly;

    // Redaction: observer, number of the current operator, element of the text operator
    private IContentObserver? _observer;

    // Full display list already reported (once per page, not for every skipped operator)
    private bool _fullReported;
    private int _op;
    private int _showElement;

    // Numbers of the characters of the page content in content order, the same in a build and for the observer (visibility of text):
    // content that only one of them executes does not count (the content of soft masks, forms on hidden layers)
    private int _glyphSerial;
    private int _serialPause;

    private PageInterpreter(PdfDocument doc, bool forPrint = false, bool forExport = false)
    {
        _doc = doc;
        // layers for the use at hand: the usage applications of /AS for printing, exporting or viewing (ISO 32000-2, 8.11.4.5; No. 767)
        _optional = OptionalContent.For(doc, forPrint ? OptionalContentUse.Print : forExport ? OptionalContentUse.Export : OptionalContentUse.View);
        _forPrint = forPrint;
        _forExport = forExport;
    }

    /// <summary>For printing: layers in their print state, annotations selected by the Print flag instead of by their display flags.</summary>
    private readonly bool _forPrint;

    /// <summary>For a format without layers (page images): layers in their export state.</summary>
    private readonly bool _forExport;

    /// <summary>
    /// Builds the display list of a page, including its annotations. Suitable for a background thread.
    /// </summary>
    /// <param name="doc">The document the page belongs to.</param>
    /// <param name="page">The page.</param>
    /// <param name="cancel">Cancels the build.</param>
    /// <param name="withoutOwnElements">Leaves out the program's own editable elements (<see cref="Editing.ElementStore"/>) because the
    /// user interface draws them on top.</param>
    /// <param name="forPrint">Builds the page as it is printed (layers in their print state, only annotations with the Print flag).</param>
    /// <param name="tagGlyphs">Records which character drew each entry (<see cref="DisplayList.GlyphTags"/>).</param>
    /// <param name="withoutCellGlyphs">Characters in the cells of tiling patterns that are left out (visibility of text in pattern cells:
    /// the page with and without them is compared, <see cref="Editing.TextVisibility"/>).</param>
    /// <param name="forExport">Builds the page for a format without layers (page images): layers in their export state (/AS with /Export,
    /// ISO 32000-2, 8.11.4.5); otherwise as on the screen.</param>
    /// <returns>The display list, clipped to the crop box. A page that exceeds the work limits is returned incomplete and a problem is
    /// added to the document.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was triggered.</exception>
    /// <exception cref="PdfLimitException">The page content exceeds a size limit.</exception>
    public static DisplayList Build(PdfDocument doc, PdfPage page, CancellationToken cancel = default, bool withoutOwnElements = false,
        bool forPrint = false, bool tagGlyphs = false, IReadOnlySet<CellGlyph>? withoutCellGlyphs = null, bool forExport = false)
    {
        var interpreter = new PageInterpreter(doc, forPrint, forExport) { _withoutOwnElements = withoutOwnElements, _withoutCellGlyphs = withoutCellGlyphs };
        if (tagGlyphs) interpreter._builder.TagGlyphs();
        interpreter.Run(page, cancel);
        var box = page.CropBox;
        interpreter.FinishGroups();
        return interpreter._builder.Build(RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top));
    }

    /// <summary>
    /// Collects only the text of a page (including annotations), the same characters as in <see cref="Build"/>, for search and
    /// selection when no display list is available. Paths, images, shadings, masks and glyph outlines are skipped (an A0 plan
    /// would otherwise cost 0.87 s and 272 MB for a page without any text).
    /// </summary>
    /// <param name="doc">The document the page belongs to.</param>
    /// <param name="page">The page.</param>
    /// <param name="cancel">Cancels the run.</param>
    /// <param name="withoutOwnElements">Leaves out the program's own editable elements (<see cref="Editing.ElementStore"/>).</param>
    /// <returns>The characters of the page.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was triggered.</exception>
    public static TextGlyph[] BuildText(PdfDocument doc, PdfPage page, CancellationToken cancel = default, bool withoutOwnElements = false) =>
        BuildTextAndUnreadable(doc, page, cancel, withoutOwnElements).Text;

    /// <summary>Like <see cref="BuildText"/>, together with the number of characters drawn without readable text (<see cref="DisplayList.UnreadableGlyphs"/>)
    /// and of those with suspicious text (<see cref="DisplayList.MisreadGlyphs"/>).</summary>
    public static (TextGlyph[] Text, int Unreadable, int Misread) BuildTextAndUnreadable(PdfDocument doc, PdfPage page, CancellationToken cancel = default,
        bool withoutOwnElements = false)
    {
        var interpreter = new PageInterpreter(doc, false) { _withoutOwnElements = withoutOwnElements, _textOnly = true };
        interpreter.Run(page, cancel);
        var box = page.CropBox;
        interpreter.FinishGroups();
        var list = interpreter._builder.Build(RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top));
        return (list.Text, list.UnreadableGlyphs, list.MisreadGlyphs);
    }

    /// <summary>
    /// Processes only the page content (without annotations) and reports the geometry of every operator to the observer;
    /// the basis of redaction (<see cref="Editing.Redactor"/>). Exceeding the work limit throws instead of truncating.
    /// </summary>
    internal static void Observe(PdfDocument doc, PdfPage page, IContentObserver observer, CancellationToken cancel = default)
    {
        var interpreter = new PageInterpreter(doc) { _observer = observer, _pageResources = page.Resources };
        if (observer.HiddenOnlyIfUnprinted) interpreter._printOptional = OptionalContent.For(doc, print: true);
        var media = page.MediaBox;
        interpreter._currentPageBox = RectD.FromLtrb(media.Left, media.Bottom, media.Right, media.Top);
        using var content = interpreter.PageContent(page.Dictionary);
        interpreter.Execute(content, page.Resources, cancel);
    }

    /// <summary>
    /// Builds a display list from annotations only (overlaid elements held in memory), in the space of
    /// <paramref name="page"/>; it is drawn on top of the page content.
    /// </summary>
    /// <param name="doc">The document the page belongs to.</param>
    /// <param name="page">The page whose space and rotation apply.</param>
    /// <param name="annotations">The annotation dictionaries to draw.</param>
    /// <param name="cancel">Cancels the build.</param>
    /// <param name="maxOperations">Work limit in operators for the whole run, for example <see cref="MaxAnnotationOperations"/>.</param>
    /// <returns>The display list, clipped to the crop box.</returns>
    public static DisplayList BuildAnnotations(PdfDocument doc, PdfPage page, IEnumerable<PdfDictionary> annotations, CancellationToken cancel = default,
        long maxOperations = MaxOperations)
    {
        var interpreter = new PageInterpreter(doc) { _cancel = cancel, _maxOperations = maxOperations, _pageResources = page.Resources };
        var media = page.MediaBox;
        interpreter._currentPageBox = RectD.FromLtrb(media.Left, media.Bottom, media.Right, media.Top);
        interpreter._pageRotate = page.Rotate;
        // the output intent of the page counts for its annotations as for its content (docs/FEHLERGEDAECHTNIS.md, No. 593)
        interpreter._pageCmyk = ColorSpace.PageCmyk(doc, page.Dictionary);
        using var pageScope = ColorSpace.ForPage(interpreter._pageCmyk);
        // the page group's blending space as well: the page draws its annotations inside it (Run), so an appearance built on its own (the
        // overlay of own elements, foreign annotations in the background) must read device colours the same way (docs/FEHLERGEDAECHTNIS.md, No. 724)
        interpreter.EnterGroupSpace(page.Dictionary, page.Resources, page: true);
        foreach (var annot in annotations) interpreter.DrawAnnotation(annot);
        var box = page.CropBox;
        interpreter.FinishGroups();
        return interpreter._builder.Build(RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top));
    }

    private ColorSpace? _pageCmyk;

    /// <summary>
    /// The resources of the page: a form, tiling pattern, Type 3 glyph or soft-mask group without resources of its own inherits them — from the
    /// page it is used on, wherever it is invoked, also from within a form that has resources of its own (ISO 32000-2, 7.8.3, sentence 18;
    /// S-A A90 changed to the wording of the standard, decided on 12.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 791). Before, the
    /// resources of the calling content.
    /// </summary>
    private PdfDictionary? _pageResources;

    private void Run(PdfPage page, CancellationToken cancel)
    {
        var media = page.MediaBox;
        _currentPageBox = RectD.FromLtrb(media.Left, media.Bottom, media.Right, media.Top);
        _pageRotate = page.Rotate;
        var resources = page.Resources;
        _pageResources = resources;
        _groupDevice = null;
        // DeviceCMYK of a page with its own output intent, for everything interpreted here and for its images (docs/FEHLERGEDAECHTNIS.md, No. 575)
        _pageCmyk = ColorSpace.PageCmyk(_doc, page.Dictionary);
        using var pageScope = ColorSpace.ForPage(_pageCmyk);
        EnterGroupSpace(page.Dictionary, resources, page: true);
        using (var content = PageContent(page.Dictionary))
        {
            // Estimate the space up front (measured on CAD plans: about 15 bytes per point, 50 per entry);
            // otherwise the lists grow in many steps and trigger expensive garbage collections
            _builder.Reserve(content.Length / 50, content.Length / 15);
            Execute(content, resources, cancel);
        }

        // annotations with an appearance (form fields, stamps, notes …)
        if (page.Dictionary.GetArray(PdfName.Get("Annots")) is { } annots)
            for (var i = 0; i < annots.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                if (annots.Get(i) is PdfDictionary annot && !(_withoutOwnElements && (Editing.ElementStore.IsOwn(_doc, page, annot) || Editing.ForeignAnnotations.IsElement(_doc, page, annot)))) DrawAnnotation(annot);
            }
    }

    private PooledBytes PageContent(PdfDictionary page) => PageContent(_doc, page);

    /// <summary>
    /// /Contents: a stream or an array of streams, concatenated, in rented buffers — the one way the content of a page is read, by the display
    /// and by everything that rewrites it after what the display reported (redaction counts operators: other bytes would move every number;
    /// docs/FEHLERGEDAECHTNIS.md, No. 452).
    /// </summary>
    internal static PooledBytes PageContent(PdfDocument doc, PdfDictionary page)
    {
        // (the other readers of /Contents as an array keep to ContentPartsLimit as well)
        switch (page.Get(PdfName.Contents))
        {
            case PdfStream s:
                return ContentBytes(doc, s);
            case PdfArray parts:
            {
                var pieces = new List<PooledBytes>();
                try
                {
                    long total = 0;
                    for (var i = 0; i < parts.Count; i++)
                    {
                        if (parts.Get(i) is not PdfStream part) continue;
                        var piece = ContentBytes(doc, part);
                        pieces.Add(piece);
                        total += piece.Length + 1L;
                        if (total > ContentPartsLimit(parts.Count)) throw new PdfLimitException(Tr.T("Der Seiteninhalt ist zu groß."));
                    }
                    // large: allocated and counted, not rented — the pool would keep it after the page (docs/FEHLERGEDAECHTNIS.md, No. 1003)
                    var buffer = DecodedMemory.Rent((int)Math.Max(total, 1), out var pooled);
                    var length = 0;
                    foreach (var piece in pieces)
                    {
                        piece.Span.CopyTo(buffer.AsSpan(length));
                        length += piece.Length;
                        buffer[length++] = (byte)'\n';   // operators may span the stream boundaries, tokens may not
                    }
                    return new PooledBytes(buffer, length, pooled);
                }
                finally
                {
                    foreach (var piece in pieces) piece.Dispose();
                }
            }
            default:
                return new PooledBytes([], 0, pooled: false);
        }
    }

    /// <summary>
    /// The bound of all parts of a page's content together (/Contents as an array, each part followed by one end of line): no more than one
    /// stream may decode to (<see cref="PdfLimits.MaxDecodedStreamBytes"/>), counted after every part before the next is decoded. One small
    /// decompression bomb named again and again in /Contents was decoded once per mention and held at once — 24 GB and minutes from a file
    /// of 256 KB (docs/FEHLERGEDAECHTNIS.md, No. 1002). Every reader that puts the parts together keeps to it and, beyond it, does what it does
    /// with a single stream above the limit.
    /// </summary>
    /// <param name="parts">The number of entries in /Contents.</param>
    internal static long ContentPartsLimit(int parts) => Math.Min(Array.MaxLength, PdfLimits.MaxDecodedStreamBytes + parts);

    // Forms and Type 3 glyphs are often drawn many times (CAD symbols, letters): from the second
    // use on, the decoded content is kept for this build (capped)
    private const long MaxCachedContentBytes = 64L << 20;
    private readonly HashSet<PdfStream> _usedOnce = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfStream, byte[]> _contentCache = new(ReferenceEqualityComparer.Instance);
    private long _cachedContentBytes;

    /// <summary>Greater than zero while a form or glyph runs for a repeated time: its paths are shared.</summary>
    private int _sharingDepth;

    /// <summary>
    /// Executes a form or glyph. From the second use on, content and paths are shared; not on the
    /// first, otherwise hashing would cost every path in forms that occur only once.
    /// </summary>
    private void ExecuteShared(PdfStream stream, PdfDictionary? resources)
    {
        var repeated = _usedOnce.Contains(stream);
        if (repeated) _sharingDepth++;
        try
        {
            using var content = ContentOf(stream);
            Execute(content, resources, _cancel);   // cancellable inside forms as well, otherwise cascades keep running
        }
        finally
        {
            if (repeated) _sharingDepth--;
        }
    }

    /// <summary>Content of a form or glyph; content drawn repeatedly is decoded only once.</summary>
    private PooledBytes ContentOf(PdfStream s)
    {
        if (_contentCache.TryGetValue(s, out var cached)) return new PooledBytes(cached, cached.Length, pooled: false);
        var content = Decode(s);
        if (_usedOnce.Add(s) || _cachedContentBytes + content.Length > MaxCachedContentBytes) return content;
        var copy = content.Span.ToArray();
        content.Dispose();
        _contentCache[s] = copy;
        _cachedContentBytes += copy.Length;
        return new PooledBytes(copy, copy.Length, pooled: false);
    }

    private PooledBytes Decode(PdfStream s) => ContentBytes(_doc, s);

    /// <summary>
    /// The content of a stream (page part, form, pattern cell, glyph) as the display executes it: a stream that cannot be decoded is empty —
    /// the one way, also for what rewrites it (docs/FEHLERGEDAECHTNIS.md, No. 452).
    /// </summary>
    internal static PooledBytes ContentBytes(PdfDocument doc, PdfStream s)
    {
        try
        {
            return doc.DecodeStreamPooled(s);
        }
        catch (PdfLimitException)
        {
            throw;
        }
        catch (PdfException e)
        {
            // shown empty, and said so: a content stream PDF-Knead cannot decode (an unknown or damaged filter) is noted among the document's
            // problems (ISO 32000-2, I.2 sentence 12, I.3) — before it stayed empty without a word (M3, Annex I, 23.11.2026); the same cause once
            doc.AddProblem(Tr.T("Ein Inhalt der Datei lässt sich nicht lesen ({0}); er wird leer gezeigt.", e.Message));
            return new PooledBytes([], 0, pooled: false);
        }
    }

    // ------------------------------------------------------------ Main loop

    /// <summary>
    /// Diagnostics (pdf-knead leistung --operatoren): receives the duration of every executed operator in
    /// Stopwatch ticks, including nested content (Do). Null = off, which costs nothing.
    /// </summary>
    /// <remarks>Static: applies to every interpreter on every thread.</remarks>
    public static Action<string, long>? OperatorTimer { get; set; }

    private void Execute(PooledBytes content, PdfDictionary? resources, CancellationToken cancel)
    {
        _cancel = cancel;
        // the bytes read count towards the work limit as well, 64 to an operator (16 million operators: 1 GB, as much as one stream may
        // decode to): a large form of nothing but white space drawn again and again has no operator, and 64 uses of one form of 256 MB took
        // six minutes over drawing, redacting and saving (docs/FEHLERGEDAECHTNIS.md, No. 1002)
        _operations += content.Length >> 6;
        if (_operations + _builder.ShadingWork > _maxOperations && OverBudget()) return;
        var parser = new ContentParser(content.Array, content.Length);
        var counter = 0;
        var timer = OperatorTimer;
        while (true)
        {
            string? op;
            try
            {
                op = parser.Next();
            }
            catch (Exception ex) when (ex is not PdfLimitException && IsBrokenData(ex))
            {
                return;   // rest of the stream unreadable: keep what was read so far instead of losing the whole page
            }
            if (op is null) break;
            if (++_operations + _builder.ShadingWork > _maxOperations && OverBudget()) return;
            _op = counter;   // number of this operator in its stream (for the observer)
            if ((++counter & 0x3FFF) == 0) cancel.ThrowIfCancellationRequested();
            if (_builder.Full && _observer is null)
            {
                if (!_fullReported) _doc.AddProblem(Tr.T("Eine Seite ist zu aufwendig und wird nur teilweise gezeigt."));
                _fullReported = true;
                return;
            }
            var started = timer is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                Operator(op, parser, resources);
                timer?.Invoke(op, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }
            catch (PdfLimitException)
            {
                throw;
            }
            catch (Exception ex) when (IsBrokenData(ex))
            {
                // a broken operator does not spoil the whole page
            }
        }
    }

    /// <summary>
    /// Work limit exceeded. When drawing, the page stays incomplete (with a notice); when redacting it must not, because the rest
    /// would remain unchecked in the file, so the operation is aborted.
    /// </summary>
    private bool OverBudget()
    {
        if (_observer is not null) throw new PdfLimitException(Tr.T("Die Seite ist zu aufwendig, um sie zu bearbeiten."));
        _doc.AddProblem(Tr.T("Eine Seite ist zu aufwendig und wird nur teilweise gezeigt."));
        return true;
    }

    /// <summary>Errors caused by nonsensical values in the file (not cancellation, not out of memory).</summary>
    private static bool IsBrokenData(Exception ex) =>
        ex is PdfException or ArgumentException or IndexOutOfRangeException or InvalidCastException or ArithmeticException or KeyNotFoundException;

    /// <summary>
    /// Operands an operator needs (ISO 32000-2, Annex A) — exactly that many: with fewer, the display ignores it; with more, the parser keeps
    /// the last ones (<see cref="ContentParser.Next"/>, No. 912). One table for the display and the
    /// redaction, which does not write such an operator again (docs/FEHLERGEDAECHTNIS.md, No. 416). Marked content and Type 3 glyph
    /// operators are not listed: the display follows them with any number of operands (the nesting of BDC … EMC must stay).
    /// </summary>
    private static readonly Dictionary<string, int> MinimumOperands = new(StringComparer.Ordinal)
    {
        ["w"] = 1, ["J"] = 1, ["j"] = 1, ["M"] = 1, ["d"] = 2, ["ri"] = 1, ["i"] = 1, ["gs"] = 1, ["cm"] = 6,
        ["m"] = 2, ["l"] = 2, ["c"] = 6, ["v"] = 4, ["y"] = 4, ["re"] = 4,
        ["CS"] = 1, ["cs"] = 1, ["G"] = 1, ["g"] = 1, ["RG"] = 3, ["rg"] = 3, ["K"] = 4, ["k"] = 4, ["sh"] = 1, ["Do"] = 1,
        ["Tc"] = 1, ["Tw"] = 1, ["Tz"] = 1, ["TL"] = 1, ["Tf"] = 2, ["Tr"] = 1, ["Ts"] = 1, ["Td"] = 2, ["TD"] = 2, ["Tm"] = 6,
        ["Tj"] = 1, ["TJ"] = 1, ["'"] = 1, ["\""] = 3,
    };

    /// <summary>
    /// The operators of content streams (ISO 32000-2, Annex A). The display ignores every other one — inside the construction of a path too,
    /// which goes on and is painted by the operator after it; so must whatever rewrites the content (docs/FEHLERGEDAECHTNIS.md, No. 454).
    /// </summary>
    internal static readonly HashSet<string> Operators =
    [
        "b", "B", "b*", "B*", "BDC", "BI", "BMC", "BT", "BX", "c", "cm", "CS", "cs", "d", "d0", "d1", "Do", "DP", "EI", "EMC", "ET", "EX", "f",
        "F", "f*", "G", "g", "gs", "h", "i", "ID", "j", "J", "K", "k", "l", "m", "M", "MP", "n", "q", "Q", "re", "RG", "rg", "ri", "s", "S", "SC",
        "sc", "SCN", "scn", "sh", "T*", "Tc", "Td", "TD", "Tf", "Tj", "TJ", "TL", "Tm", "Tr", "Ts", "Tw", "Tz", "v", "w", "W", "W*", "y", "'", "\"",
    ];

    /// <summary>The number of operands of an operator of fixed arity (<see cref="MinimumOperands"/>); null for the others.</summary>
    internal static int? Arity(string op) => MinimumOperands.TryGetValue(op, out var count) ? count : null;

    /// <summary>Whether the display ignores <paramref name="op"/> because it has fewer operands than it needs (<see cref="MinimumOperands"/>).</summary>
    internal static bool LacksOperands(string op, IReadOnlyList<Operand> operands) =>
        MinimumOperands.TryGetValue(op, out var least) && operands.Count < least;

    private void Operator(string op, ContentParser parser, PdfDictionary? resources)
    {
        var o = parser.Operands;
        if (LacksOperands(op, o)) return;
        // Recompute style and matrix only when the state may have changed: path and painting
        // operators (almost everything in CAD drawings) do not change it. Discard them only when the
        // operator changes them (q/Q bring their own valid values).
        if (PaintOperators.Contains(op)) _gs.PaintIndex = -1;
        else if (op == "cm") _gs.MatrixIndex = -1;
        // Type 3 glyph with d1: a form without its own colour, so colour operators are ignored (ISO 32000-2, 9.6.4)
        if (_uncolored && ColorOperators.Contains(op)) return;
        if (_textOnly && GraphicsOnlyOperators.Contains(op)) return;
        switch (op)
        {
            // ---------------------------------------------- Graphics state
            case "q":
                if (_observer is { WantsSaveState: true } saving)
                    saving.SaveState(_op, _gs.Ctm, _builder.ClipBounds(_gs.Clip), _gs.Clip);
                if (_stack.Count < 512) _stack.Push(_gs.Clone());
                break;
            case "Q":
                if (_stack.Count > _stackFloor) _gs = _stack.Pop();
                break;
            case "cm" when o.Count >= 6:
                var m = new Matrix(o[0].Number, o[1].Number, o[2].Number, o[3].Number, o[4].Number, o[5].Number);
                // check the product as well: many large factors in a row would otherwise give infinite coordinates
                if (m.IsFinite && m * _gs.Ctm is { IsFinite: true } ctm) _gs.Ctm = ctm;
                break;
            case "w" when o.Count >= 1:
                _gs.LineWidth = Math.Max(0, o[0].Number);   // out of range forced into it: a negative width is the thinnest line (8.4.1; No. 1123)
                break;
            case "J" when o.Count >= 1:
                _gs.LineCap = (byte)Math.Clamp((int)o[0].Number, 0, 2);
                break;
            case "j" when o.Count >= 1:
                _gs.LineJoin = (byte)Math.Clamp((int)o[0].Number, 0, 2);
                break;
            case "M" when o.Count >= 1:
                _gs.MiterLimit = Math.Max(1, o[0].Number);
                break;
            case "d" when o.Count >= 2:
                SetDash(o[0].Object as PdfArray, o[1].Number);
                break;
            case "gs" when o.Count >= 1 && o[0].Name is { } gsName:
                ExtGState(resources?.GetDictionary(PdfName.Get("ExtGState"))?.GetDictionary(gsName), resources);
                break;
            case "ri" when o.Count >= 1:
                _gs.Intent = ColorSpace.IntentFrom(o[0].Object);
                break;

            // ---------------------------------------------- Paths
            case "m" when o.Count >= 2:
                MoveTo(o[0].Number, o[1].Number);
                break;
            case "l" when o.Count >= 2:
                LineTo(o[0].Number, o[1].Number);
                break;
            case "c" when o.Count >= 6:
                CurveTo(o[0].Number, o[1].Number, o[2].Number, o[3].Number, o[4].Number, o[5].Number);
                break;
            case "v" when o.Count >= 4:
                CurveTo(_cx, _cy, o[0].Number, o[1].Number, o[2].Number, o[3].Number);
                break;
            case "y" when o.Count >= 4:
                CurveTo(o[0].Number, o[1].Number, o[2].Number, o[3].Number, o[2].Number, o[3].Number);
                break;
            case "h":
                ClosePath();
                break;
            case "re" when o.Count >= 4:
                double x = o[0].Number, y = o[1].Number, w = o[2].Number, h = o[3].Number;
                MoveTo(x, y);
                LineTo(x + w, y);
                LineTo(x + w, y + h);
                LineTo(x, y + h);
                ClosePath();
                break;

            // ---------------------------------------------- Painting
            case "S": Paint(DisplayItemKind.Stroke, false); break;
            case "s": ClosePath(); Paint(DisplayItemKind.Stroke, false); break;
            case "f" or "F": Paint(DisplayItemKind.Fill, false); break;
            case "f*": Paint(DisplayItemKind.Fill, true); break;
            case "B": Paint(DisplayItemKind.FillStroke, false); break;
            case "B*": Paint(DisplayItemKind.FillStroke, true); break;
            case "b": ClosePath(); Paint(DisplayItemKind.FillStroke, false); break;
            case "b*": ClosePath(); Paint(DisplayItemKind.FillStroke, true); break;
            case "n": Paint(null, false); break;
            case "W": _pendingClipEvenOdd = false; break;
            case "W*": _pendingClipEvenOdd = true; break;

            // ---------------------------------------------- Colours
            // the initial colour of the Pattern space is a pattern that paints nothing (8.6.8, Table 73): the pattern chosen before is
            // forgotten, until scn names one nothing is painted (before, the grey of a broken pattern or the old pattern; No. 1123)
            case "CS" when o.Count >= 1 && o[0].Object is { } csStroke:
                _gs.StrokeSpace = Space(csStroke, resources);
                _gs.StrokeColor = _gs.StrokeSpace.InitialColor;
                _gs.StrokePattern = null;
                _gs.StrokePatternUnset = _gs.StrokeSpace.IsPattern;
                _gs.PaintIndex = -1;
                break;
            case "cs" when o.Count >= 1 && o[0].Object is { } csFill:
                _gs.FillSpace = Space(csFill, resources);
                _gs.FillColor = _gs.FillSpace.InitialColor;
                _gs.FillPattern = null;
                _gs.FillPatternUnset = _gs.FillSpace.IsPattern;
                _gs.PaintIndex = -1;
                break;
            // Pattern name without a preceding "/Pattern cs" (files written by a browser, issue18466): use the pattern as the other readers
            // do; otherwise the initial colour black applies and a page-sized rectangle covers the text
            case "SC" or "SCN":
                _gs.StrokeColor = Numbers(o);
                if (!_gs.StrokeSpace.IsPattern && op == "SCN" && o.Count >= 1 && o[^1].Name is { } implicitStroke && PatternNamed(implicitStroke, resources) is not null)
                    _gs.StrokeSpace = Space(PdfName.Get("Pattern"), resources);
                _gs.StrokePattern = _gs.StrokeSpace.IsPattern && o.Count >= 1 && o[^1].Name is { } strokePattern ? PatternNamed(strokePattern, resources) : null;
                _gs.StrokePatternUnset = false;
                break;
            case "sc" or "scn":
                _gs.FillColor = Numbers(o);
                if (!_gs.FillSpace.IsPattern && op == "scn" && o.Count >= 1 && o[^1].Name is { } implicitFill && PatternNamed(implicitFill, resources) is not null)
                    _gs.FillSpace = Space(PdfName.Get("Pattern"), resources);
                _gs.FillPattern = _gs.FillSpace.IsPattern && o.Count >= 1 && o[^1].Name is { } fillPattern ? PatternNamed(fillPattern, resources) : null;
                _gs.FillPatternUnset = false;
                break;
            case "G" when o.Count >= 1:
                _gs.StrokeSpace = DeviceSpace(1, resources);
                _gs.StrokeColor = [o[0].Number];
                break;
            case "g" when o.Count >= 1:
                _gs.FillSpace = DeviceSpace(1, resources);
                _gs.FillColor = [o[0].Number];
                break;
            case "RG" when o.Count >= 3:
                _gs.StrokeSpace = DeviceSpace(3, resources);
                _gs.StrokeColor = [o[0].Number, o[1].Number, o[2].Number];
                break;
            case "rg" when o.Count >= 3:
                _gs.FillSpace = DeviceSpace(3, resources);
                _gs.FillColor = [o[0].Number, o[1].Number, o[2].Number];
                break;
            case "K" when o.Count >= 4:
                _gs.StrokeSpace = DeviceSpace(4, resources);
                _gs.StrokeColor = [o[0].Number, o[1].Number, o[2].Number, o[3].Number];
                break;
            case "k" when o.Count >= 4:
                _gs.FillSpace = DeviceSpace(4, resources);
                _gs.FillColor = [o[0].Number, o[1].Number, o[2].Number, o[3].Number];
                break;

            // ---------------------------------------------- XObjects, images, shadings
            case "Do" when o.Count >= 1 && o[0].Name is { } xName:
                if (resources?.GetDictionary(PdfName.Get("XObject"))?.Get(xName) is PdfStream xobject)
                {
                    if (_observer is { } observer) ObserveXObject(observer, xobject, resources);
                    else XObject(xobject, resources);
                }
                break;
            case "BI" when parser.InlineImage is { } inline:
                if (_observer is { } inlineObserver) ReportClip(inlineObserver);
                _observer?.Image(_op, _gs.Ctm, null, inline, resources, Hidden);
                if (!Hidden) Image(new ImageSource(null, inline, resources, IsStencil(inline.Dictionary), 0));
                break;
            case "sh" when o.Count >= 1 && o[0].Name is { } shName:
                // the clip of a shading as of every other painting operator: the observer would keep the one of what came before
                if (_observer is { } shadingObserver) ReportClip(shadingObserver);
                var shadingBounds = _builder.ClipBounds(_gs.Clip).Intersect(_currentPageBox);
                _observer?.Shading(_op, shadingBounds, _gs.Ctm, Hidden);
                if (_observer is { } clipTest && clipTest.WantsShadingClip(shadingBounds))
                {
                    var (builder, shadingClip) = (_builder, _gs.Clip);
                    clipTest.ShadingClip(_op, shadingBounds, (x, y) => builder.ClipContains(shadingClip, x, y));
                }
                Shading(resources?.GetDictionary(PdfName.Get("Shading"))?.Get(shName), resources);
                break;

            // ---------------------------------------------- Optional content (layers)
            case "BDC":
                var hidden = o.Count >= 2 && o[0].Name?.Value == "OC" && LayerHidden(o[1].Object, resources);
                _hiddenStack.Push(hidden);
                if (hidden) _hiddenDepth++;
                if (_observer is { } marking && o.Count >= 1 && o[0].Name is { } markTag)
                    marking.MarkedContent(_op, markTag, o.Count >= 2 ? o[1].Object switch
                    {
                        PdfDictionary inline => inline,
                        PdfName named => resources?.GetDictionary(PdfName.Get("Properties"))?.Get(named) as PdfDictionary,
                        _ => null,
                    } : null);
                break;
            case "BMC":
                _hiddenStack.Push(false);
                if (_observer is { } plainMarking && o.Count >= 1 && o[0].Name is { } plainTag) plainMarking.MarkedContent(_op, plainTag, null);
                break;
            case "EMC":
                if (_hiddenStack.Count > 0 && _hiddenStack.Pop()) _hiddenDepth--;
                _observer?.EndMarkedContent(_op);
                break;

            default:
                TextOperator(op, o, resources);
                break;
        }
    }

    /// <summary>Operators that change colour, opacity, blend mode or line style.</summary>
    private static readonly HashSet<string> PaintOperators =
        ["w", "J", "j", "M", "d", "gs", "ri", "CS", "cs", "SC", "SCN", "sc", "scn", "G", "g", "RG", "rg", "K", "k"];

    private static readonly HashSet<string> ColorOperators = ["CS", "cs", "SC", "SCN", "sc", "scn", "G", "g", "RG", "rg", "K", "k"];

    /// <summary>Operators without any effect on text (paths, painting, clipping, shadings, inline images); skipped in text-only mode.</summary>
    private static readonly HashSet<string> GraphicsOnlyOperators =
        ["m", "l", "c", "v", "y", "h", "re", "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n", "W", "W*", "sh", "BI"];

    private bool Hidden => _hiddenDepth > 0;

    private static double[] Numbers(IReadOnlyList<Operand> o)
    {
        var count = 0;
        for (var i = 0; i < o.Count; i++) if (o[i].IsNumber) count++;
        var result = new double[count];
        var k = 0;
        for (var i = 0; i < o.Count; i++) if (o[i].IsNumber) result[k++] = o[i].Number;
        return result;
    }

    // Device colour spaces of the most recently used resources (with /DefaultGray … and the document's DeviceCMYK): g, rg and k are
    // the most frequent operators of all, so the lookup through the resources should only happen when the resources change
    private PdfDictionary? _deviceResources;
    private ColorSpace[]? _deviceSpaces;

    private ColorSpace DeviceSpace(int components, PdfDictionary? resources)
    {
        if (_deviceSpaces is null || !ReferenceEquals(resources, _deviceResources))
        {
            _deviceResources = resources;
            _deviceSpaces = [GroupOrDevice(1, resources), GroupOrDevice(3, resources), GroupOrDevice(4, resources)];
        }
        return _deviceSpaces[components switch { 1 => 0, 3 => 1, _ => 2 }];
    }

    // /DefaultGray … of the resources first (stated for exactly these colours), then the ICC space of the enclosing isolated group or the
    // page group, then the device space itself
    private ColorSpace GroupOrDevice(int components, PdfDictionary? resources) =>
        _groupDevice?[components] is { } group && !ColorSpace.HasDefault(components, resources) ? group : ColorSpace.Device(components, resources, _doc);

    private ColorSpace Space(PdfObject obj, PdfDictionary? resources)
    {
        // do not rebuild the same colour space object every time (separation colours carry functions)
        if (obj is PdfName { Value: "DeviceGray" or "G" }) return DeviceSpace(1, resources);
        if (obj is PdfName { Value: "DeviceRGB" or "RGB" }) return DeviceSpace(3, resources);
        if (obj is PdfName { Value: "DeviceCMYK" or "CMYK" }) return DeviceSpace(4, resources);
        // a name the resources do not list stays the name: a missing entry reads as the null object, and all such names shared one entry of
        // the cache — /Pattern after a name not listed drew in that name's space (docs/FEHLERGEDAECHTNIS.md, No. 1004)
        var key = obj is PdfName n && resources?.GetDictionary(PdfName.Get("ColorSpace"))?.Get(n) is { } listed and not PdfNull ? listed : obj;
        // a resource name for a device space (/CS0 /DeviceCMYK) is the device space: also redefined by the group (No. 568)
        if (!ReferenceEquals(key, obj) && key is PdfName { Value: "DeviceGray" or "DeviceRGB" or "DeviceCMYK" }) return Space(key, resources);
        if (_spaceCache.TryGetValue(key, out var cached)) return cached;
        var space = ColorSpace.Create(obj, resources, _doc);
        if (_spaceCache.Count < 1024) _spaceCache[key] = space;
        return space;
    }

    private void SetDash(PdfArray? array, double phase)
    {
        if (array is null || array.Count == 0)
        {
            _gs.Dash = null;
            return;
        }
        var lengths = new float[array.Count];
        var total = 0.0;
        for (var i = 0; i < array.Count; i++)
        {
            lengths[i] = (float)Math.Max(0, array.GetNumber(i) ?? 0);
            total += lengths[i];
        }
        // only zeros: solid line (otherwise nothing at all would be drawn)
        _gs.Dash = total > 0 ? new DashPattern(lengths.Length % 2 == 1 ? [.. lengths, .. lengths] : lengths, (float)phase) : null;
    }

    private void ExtGState(PdfDictionary? d, PdfDictionary? resources)
    {
        if (d is null) return;
        if (d.GetNumber(PdfName.Get("LW")) is { } lw) _gs.LineWidth = Math.Max(0, lw);
        if (d.GetInteger(PdfName.Get("LC")) is { } lc) _gs.LineCap = (byte)Math.Clamp(lc, 0, 2);
        if (d.GetInteger(PdfName.Get("LJ")) is { } lj) _gs.LineJoin = (byte)Math.Clamp(lj, 0, 2);
        if (d.GetNumber(PdfName.Get("ML")) is { } ml) _gs.MiterLimit = Math.Max(1, ml);
        if (d.GetArray(PdfName.Get("D")) is { Count: >= 2 } dash) SetDash(dash.Get(0) as PdfArray, dash.GetNumber(1) ?? 0);
        // /Font [font size] acts like Tf (test file NegativeFontSize)
        if (d.GetArray(PdfName.Get("Font")) is { Count: >= 2 } font && font.Get(0).AsDictionary() is { } fontDict && font.GetNumber(1) is { } fontSize)
        {
            _gs.Font = FontOf(fontDict);
            _gs.FontSize = fontSize;
        }
        if (d.GetNumber(PdfName.Get("CA")) is { } ca) _gs.StrokeAlpha = Math.Clamp(ca, 0, 1);
        if (d.GetNumber(PdfName.Get("ca")) is { } fa) _gs.FillAlpha = Math.Clamp(fa, 0, 1);
        if (d.Get(PdfName.Get("BM")) is { } bm and not PdfNull) _gs.BlendMode = BlendModes.From(bm);
        if (!_textOnly && d.Get(PdfName.Get("SMask")) is { } mask and not PdfNull) _gs.SoftMask = mask is PdfName ? -1 : SoftMaskIndex(mask, resources);
        if (_observer is { } watcher && d.Get(PdfName.Get("SMask")).AsDictionary()?.Get(PdfName.Get("G")) is PdfStream group) ObserveSoftMask(watcher, group, resources);
        // /OP also applies to fills as long as /op is absent (ISO 32000-2, 8.4.5)
        if (d.GetBoolean(PdfName.Get("OP")) is { } op) _gs.StrokeOverprint = _gs.FillOverprint = op;
        if (d.GetBoolean(PdfName.Get("op")) is { } fillOp) _gs.FillOverprint = fillOp;
        if (d.GetInteger(PdfName.Get("OPM")) is { } opm) _gs.OverprintMode = (int)opm;
        // rendering intent and black point compensation (ISO 32000-2, 8.6.5.8, 8.6.5.9; docs/FEHLERGEDAECHTNIS.md, No. 572)
        if (d.Get(PdfName.Get("RI")) is PdfName ri) _gs.Intent = ColorSpace.IntentFrom(ri);
        if (d.Get(PdfName.Get("UseBlackPtComp")) is PdfName bpc) _gs.Bpc = ColorSpace.BpcFrom(bpc);
    }

    /// <summary>A colour space as the graphics state renders it: its variant for the rendering intent and black point compensation.</summary>
    private ColorSpace Rendered(ColorSpace space) => space.WithRendering(_gs.Intent, _gs.Bpc);

    // ------------------------------------------------------------ Path construction

    private void MoveTo(double x, double y)
    {
        // several m in a row: only the last one counts
        if (_verbs.Count > 0 && _verbs[^1] == PathVerb.Move)
        {
            _points[^2] = (float)x;
            _points[^1] = (float)y;
        }
        else
        {
            _verbs.Add(PathVerb.Move);
            _points.Add((float)x);
            _points.Add((float)y);
        }
        _cx = _sx = x;
        _cy = _sy = y;
    }

    /// <summary>A path begun without m starts at the current point; a rewriter is told where (<see cref="IContentObserver.PathStart"/>).</summary>
    private void StartAtCurrentPoint()
    {
        if (_observer is { WantsPathStart: true } observer) observer.PathStart(_op, _cx, _cy);
        MoveTo(_cx, _cy);
    }

    private void LineTo(double x, double y)
    {
        if (_verbs.Count == 0) StartAtCurrentPoint();
        _verbs.Add(PathVerb.Line);
        _points.Add((float)x);
        _points.Add((float)y);
        _cx = x;
        _cy = y;
    }

    private void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        if (_verbs.Count == 0) StartAtCurrentPoint();
        _verbs.Add(PathVerb.Cubic);
        _points.Add((float)x1); _points.Add((float)y1);
        _points.Add((float)x2); _points.Add((float)y2);
        _points.Add((float)x3); _points.Add((float)y3);
        _cx = x3;
        _cy = y3;
    }

    private void ClosePath()
    {
        if (_verbs.Count == 0 || _verbs[^1] == PathVerb.Close) return;
        _verbs.Add(PathVerb.Close);
        _cx = _sx;
        _cy = _sy;
    }

    /// <summary>Paints the path (or, for "n", only ends it) and applies a pending clip.</summary>
    private void Paint(DisplayItemKind? kind, bool evenOdd)
    {
        // redaction: every painted path, including invisible ones (hidden layer, colour without effect)
        (PdfStream Pattern, bool Stroke, RectD Painted)? fillCell = null, strokeCell = null;
        if (_observer is { } observer && kind is { } painted && _verbs.Count > 0)
        {
            var area = PathBounds();
            var filled = area;
            if (painted != DisplayItemKind.Fill) area = area.Inflate(StrokeExtent(HasJoins()));
            ReportClip(observer);
            if (observer.WantsStroke) observer.Stroke(StrokeExtent(hasJoins: false), _gs.Dash is { Lengths.Length: > 0 } dash ? dash : null);
            if (observer.WantsStroke && painted == DisplayItemKind.FillStroke)
                observer.FillStroke(_gs.FillAlpha, _gs.StrokeAlpha, _gs.BlendMode, _gs.FillSpace.IsPattern || _gs.StrokeSpace.IsPattern,
                    new StrokeStyle(_gs.LineWidth, _gs.LineCap, _gs.LineJoin, _gs.MiterLimit, _gs.Dash is { Lengths.Length: > 0 } d ? d : null));
            observer.Path(_op, area, _gs.Ctm, Hidden);
            if (observer.WantsPathGeometry(area))
                observer.PathGeometry(_op, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_verbs),
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_points), _gs.Ctm, painted != DisplayItemKind.Fill, _gs.LineWidth, false);
            if (painted == DisplayItemKind.Stroke && observer.WantsStrokeLine(area))
                observer.StrokeLine(_op, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_verbs),
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_points), _gs.Ctm, StrokeReach());
            // the cells of tiling patterns it paints with, observed once the path is done (their content would replace it)
            var clip = _builder.ClipBounds(_gs.Clip);
            if (painted is DisplayItemKind.Fill or DisplayItemKind.FillStroke && _gs.FillSpace.IsPattern && _gs.FillPattern is PdfStream fill)
                fillCell = (fill, false, filled.Intersect(clip));
            if (painted is DisplayItemKind.Stroke or DisplayItemKind.FillStroke && _gs.StrokeSpace.IsPattern && _gs.StrokePattern is PdfStream stroke)
                strokeCell = (stroke, true, area.Intersect(clip));
        }
        if (_observer is { } clipObserver && _pendingClipEvenOdd is not null && _verbs.Count > 0)
        {
            var bounds = PathBounds();
            clipObserver.ClipPath(_op, bounds, _gs.Ctm);
            // one that only clips ("W n") carries its coordinates as well, such as text converted to curves used as a clip (No. 455)
            if (kind is null && clipObserver.WantsPathGeometry(bounds))
                clipObserver.PathGeometry(_op, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_verbs),
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_points), _gs.Ctm, false, 0, true);
        }
        // "n" without a clip: nothing painted, but coordinates in the content (docs/FEHLERGEDAECHTNIS.md, No. 449)
        if (_observer is { } emptyObserver && kind is null && _pendingClipEvenOdd is null && _verbs.Count > 0)
        {
            var bounds = PathBounds();
            emptyObserver.EmptyPath(_op, bounds, _gs.Ctm);
            if (emptyObserver.WantsPathGeometry(bounds))
                emptyObserver.PathGeometry(_op, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_verbs),
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_points), _gs.Ctm, false, 0, false);
        }
        // invisible (hidden layer) and without a clip: nothing to do, not even bounds to compute
        if (_verbs.Count > 0 && (!Hidden || _pendingClipEvenOdd is not null))
        {
            var bounds = PathBounds();
            int? path = null;
            if (kind is { } k && !Hidden)
            {
                var paintsFill = k is DisplayItemKind.Fill or DisplayItemKind.FillStroke && !_gs.FillSpace.PaintsNothing;
                var paintsStroke = k is DisplayItemKind.Stroke or DisplayItemKind.FillStroke && !_gs.StrokeSpace.PaintsNothing;
                if (paintsFill || paintsStroke)
                {
                    var effective = paintsFill && paintsStroke ? DisplayItemKind.FillStroke : paintsFill ? DisplayItemKind.Fill : DisplayItemKind.Stroke;
                    path = AddCurrentPath();
                    var itemBounds = effective == DisplayItemKind.Fill ? bounds : bounds.Inflate(StrokeExtent(HasJoins()));
                    EnsureMask();
                    _builder.Add(effective, evenOdd, path.Value, CurrentMatrix(), CurrentPaint(), _gs.Clip, -1, itemBounds);
                }
            }

            if (_pendingClipEvenOdd is { } clipEvenOdd)
            {
                path ??= AddCurrentPath();
                _gs.Clip = AddClip(_gs.Clip, path.Value, _builder.AddMatrix(_gs.Ctm), clipEvenOdd, bounds);
            }
        }
        _pendingClipEvenOdd = null;
        _verbs.Clear();
        _points.Clear();
        if (_observer is { } cellObserver)
            foreach (var cell in (ReadOnlySpan<(PdfStream Pattern, bool Stroke, RectD Painted)?>)[fillCell, strokeCell])
                if (cell is { } c) ObservePatternCell(cellObserver, c.Pattern, c.Stroke, c.Painted);
    }

    private int _cellDepth;

    /// <summary>
    /// For the observer (redaction): reports the content of a coloured tiling pattern a path paints with, if the part it paints lies in a
    /// single tile and the observer asks for it (<see cref="IContentObserver.WantsPatternCell"/>) — the cell of that tile, mapped to the
    /// page by the tile's offset, the pattern matrix and the space of the content stream (ISO 32000-2, 8.7.3.1). Several tiles show the
    /// same content in several places; then nothing is reported.
    /// </summary>
    private void ObservePatternCell(IContentObserver observer, PdfStream pattern, bool stroke, RectD painted)
    {
        var dict = pattern.Dictionary;
        if (_cellDepth >= MaxPatternDepth || painted.Width < 0 || painted.Height < 0 || dict.GetInteger(PdfName.Get("PatternType")) != 1 ||
            dict.GetInteger(PdfName.Get("PaintType")) != 1 || !observer.WantsPatternCell(painted)) return;
        var xStep = Math.Abs(dict.GetNumber(PdfName.Get("XStep")) ?? 0);
        var yStep = Math.Abs(dict.GetNumber(PdfName.Get("YStep")) ?? 0);
        var toPage = (Matrix.FromArray(dict.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity) * _patternBase;
        if (!(xStep > 0) || !(yStep > 0) || !double.IsFinite(xStep) || !double.IsFinite(yStep) || !toPage.IsInvertible ||
            PdfKnead.Core.Pdf.Document.PdfRectangle.From(dict.Get(PdfName.Get("BBox"))) is not { } box) return;
        // the tiles whose cell meets the painted part, in pattern space
        var inPattern = toPage.Invert().TransformBounds(painted);
        var (k0, k1) = (Math.Ceiling((inPattern.Left - box.Right) / xStep), Math.Floor((inPattern.Right - box.Left) / xStep));
        var (m0, m1) = (Math.Ceiling((inPattern.Top - box.Top) / yStep), Math.Floor((inPattern.Bottom - box.Bottom) / yStep));
        if (k0 != k1 || m0 != m1 || !double.IsFinite(k0) || !double.IsFinite(m0)) return;
        var cellToPage = Matrix.Translate(k0 * xStep, m0 * yStep) * toPage;
        if (!_activeForms.Add(pattern)) return;
        observer.BeginPatternCell(_op, stroke, pattern, cellToPage);
        _serialPause++;   // a build draws pattern cells with an interpreter of their own
        var saved = _gs.Clone();
        var savedPatternBase = _patternBase;
        var savedStack = _stack.Count;
        var savedFloor = _stackFloor;
        _stackFloor = savedStack;
        var savedHidden = _hiddenStack.Count;
        var (tm, tlm, op, element, pending) = (_tm, _tlm, _op, _showElement, _textClipPending);
        _cellDepth++;
        try
        {
            _gs.Ctm = cellToPage;
            _gs.MatrixIndex = -1;
            _gs.PaintIndex = -1;
            _patternBase = cellToPage;
            ExecuteShared(pattern, dict.GetDictionary(PdfName.Resources) ?? _pageResources);
        }
        finally
        {
            _cellDepth--;
            _activeForms.Remove(pattern);
            while (_stack.Count > savedStack) _stack.Pop();
            _stackFloor = savedFloor;
            while (_hiddenStack.Count > savedHidden)
                if (_hiddenStack.Pop()) _hiddenDepth--;
            _gs = saved;
            _patternBase = savedPatternBase;
            (_tm, _tlm, _op, _showElement, _textClipPending) = (tm, tlm, op, element, pending);
            _verbs.Clear();
            _points.Clear();
            _serialPause--;
            observer.EndForm();
        }
    }

    /// <summary>Stores the current path; shared when a form or Type 3 glyph runs repeatedly, and for clip paths.</summary>
    private int AddCurrentPath()
    {
        var verbs = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_verbs);
        var points = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_points);
        // clip paths as well: identical clips (the same rectangle around every object) should share one entry
        return _sharingDepth > 0 || _pendingClipEvenOdd is not null
            ? _builder.AddSharedPath(verbs, points) : _builder.AddPath(verbs, points);
    }

    /// <summary>Bounds of the current path in page space (the control points suffice because they enclose the curve).</summary>
    private RectD PathBounds()
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        var ctm = _gs.Ctm;
        for (var i = 0; i + 1 < _points.Count; i += 2)
        {
            var (x, y) = ctm.Transform(_points[i], _points[i + 1]);
            if (x < l) l = x;
            if (x > r) r = x;
            if (y < t) t = y;
            if (y > b) b = y;
        }
        return l > r ? RectD.Empty : RectD.FromLtrb(l, t, r, b);
    }

    /// <summary>
    /// How far a stroke extends beyond the path, in page space: half the width; with corners and
    /// mitre joins up to the mitre limit; square caps by a factor of √2. Single lines (almost
    /// everything in CAD) have no corner, so their bounds stay tight; otherwise the spatial index becomes imprecise.
    /// </summary>
    private double StrokeExtent(bool hasJoins)
    {
        var ctm = _gs.Ctm;
        var scale = Math.Max(Math.Abs(ctm.A) + Math.Abs(ctm.C), Math.Abs(ctm.B) + Math.Abs(ctm.D));
        var half = _gs.LineWidth * scale / 2;
        // Up to a mitre limit of 10 the limit itself serves as the bound; above that, the corners are examined (test file LargeMitreLimit:
        // limit 333 at an acute angle; with the bounds capped at 10, tiles outside them would leave out the tip)
        if (hasJoins && _gs.LineJoin == 0) half *= _gs.MiterLimit <= 10 ? _gs.MiterLimit : Math.Max(MiterReach(), _gs.LineCap == 2 ? 1.415 : 1);
        else if (_gs.LineCap == 2) half *= 1.415;
        return half;
    }

    /// <summary>
    /// How far the stroke of the current path reaches beside its line, in page space: half the line width, times the ratio of its sharpest
    /// mitre (the corners themselves, whatever the limit) or of square caps — exact where <see cref="StrokeExtent"/> takes the limit.
    /// </summary>
    private double StrokeReach()
    {
        var ctm = _gs.Ctm;
        var scale = Math.Max(Math.Abs(ctm.A) + Math.Abs(ctm.C), Math.Abs(ctm.B) + Math.Abs(ctm.D));
        var half = _gs.LineWidth * scale / 2;
        var cap = _gs.LineCap == 2 ? 1.415 : 1;
        return half * (_gs.LineJoin == 0 && HasJoins() ? Math.Max(MiterReach(), cap) : cap);
    }

    /// <summary>
    /// Largest mitre ratio (tip ÷ half the line width) at the corners of the current path that does not exceed the mitre limit;
    /// above the limit the corner is bevelled. Corner with interior angle φ: 1 / sin(φ/2).
    /// </summary>
    private double MiterReach()
    {
        var limit = _gs.MiterLimit;
        var reach = 1.0;
        double startX = 0, startY = 0, x = 0, y = 0;      // start of the subpath, current point
        double firstX = 0, firstY = 0, lastX = 0, lastY = 0;
        bool hasFirst = false, hasLast = false;

        void Join(double ax, double ay, double bx, double by)
        {
            var cos = (ax * bx + ay * by) / Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by));
            var half = (1 + cos) / 2;   // sin²(φ/2)
            if (half > 0 && 1 / Math.Sqrt(half) is var ratio && ratio <= limit) reach = Math.Max(reach, ratio);
        }

        // new segment: direction at its start (for the corner with the previous one) and at its end (for the next one)
        void Segment(double inX, double inY, double outX, double outY)
        {
            if (inX == 0 && inY == 0) return;
            if (hasLast) Join(lastX, lastY, inX, inY);
            else (firstX, firstY, hasFirst) = (inX, inY, true);
            (lastX, lastY, hasLast) = (outX, outY, true);
        }

        var p = 0;
        foreach (var verb in _verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    (startX, startY) = (x, y) = (_points[p], _points[p + 1]);
                    p += 2;
                    hasFirst = hasLast = false;
                    break;
                case PathVerb.Line:
                {
                    double nx = _points[p], ny = _points[p + 1];
                    p += 2;
                    Segment(nx - x, ny - y, nx - x, ny - y);
                    (x, y) = (nx, ny);
                    break;
                }
                case PathVerb.Cubic:
                {
                    double x1 = _points[p], y1 = _points[p + 1], x2 = _points[p + 2], y2 = _points[p + 3], x3 = _points[p + 4], y3 = _points[p + 5];
                    p += 6;
                    // tangents: the first or last control point that does not coincide with the end point
                    var (inX, inY) = x1 != x || y1 != y ? (x1 - x, y1 - y) : x2 != x || y2 != y ? (x2 - x, y2 - y) : (x3 - x, y3 - y);
                    var (outX, outY) = x3 != x2 || y3 != y2 ? (x3 - x2, y3 - y2) : x3 != x1 || y3 != y1 ? (x3 - x1, y3 - y1) : (x3 - x, y3 - y);
                    Segment(inX, inY, outX, outY);
                    (x, y) = (x3, y3);
                    break;
                }
                case PathVerb.Close:
                    Segment(startX - x, startY - y, startX - x, startY - y);
                    if (hasFirst && hasLast) Join(lastX, lastY, firstX, firstY);
                    (x, y) = (startX, startY);
                    hasFirst = hasLast = false;
                    break;
            }
        }
        return reach;
    }

    /// <summary>Whether a subpath has more than one segment (or is closed); then it has corners.</summary>
    private bool HasJoins()
    {
        var segments = 0;
        foreach (var v in _verbs)
        {
            if (v == PathVerb.Move) segments = 0;
            else if (v == PathVerb.Close || ++segments > 1) return true;
        }
        return false;
    }

    private int CurrentMatrix() => _gs.MatrixIndex >= 0 ? _gs.MatrixIndex : _gs.MatrixIndex = _builder.AddMatrix(_gs.Ctm);

    private int CurrentPaint()
    {
        if (_gs.PaintIndex >= 0) return _gs.PaintIndex;
        var fillPattern = !_gs.FillSpace.IsPattern ? -1 : _gs.FillPatternUnset ? NoPattern : PatternIndex(_gs.FillPattern, _gs.FillSpace, _gs.FillColor, _gs.FillAlpha);
        var strokePattern = !_gs.StrokeSpace.IsPattern ? -1 : _gs.StrokePatternUnset ? NoPattern
            : PatternIndex(_gs.StrokePattern, _gs.StrokeSpace, _gs.StrokeColor, _gs.StrokeAlpha);
        // invalid tiling pattern: paint nothing (transparent) instead of the fallback colour
        return _gs.PaintIndex = _builder.AddPaint(new Paint(
            fillPattern == NoPattern ? 0u : Rendered(_gs.FillSpace).ToRgb(_gs.FillColor).ToArgb(_gs.FillAlpha),
            strokePattern == NoPattern ? 0u : Rendered(_gs.StrokeSpace).ToRgb(_gs.StrokeColor).ToArgb(_gs.StrokeAlpha),
            (float)_gs.LineWidth, _gs.LineCap, _gs.LineJoin, (float)_gs.MiterLimit,
            _gs.Dash is { } d ? _builder.AddDash(d) : -1, _gs.BlendMode,
            Math.Max(fillPattern, -1), Math.Max(strokePattern, -1),
            (byte)((Overprints(_gs.FillOverprint, _gs.FillSpace, _gs.FillColor) ? 1 : 0) | (Overprints(_gs.StrokeOverprint, _gs.StrokeSpace, _gs.StrokeColor) ? 2 : 0))));
    }

    /// <summary>
    /// Result of <see cref="PatternIndex"/>: a tiling pattern that paints nothing — without a valid step or BBox, or one that contains
    /// itself through a cycle (test file operator_list_cycle: other readers paint nothing there, whereas the grey
    /// fallback colour would cover the area).
    /// </summary>
    private const int NoPattern = -2;

    /// <summary>Overprint for images and shadings: only in Separation/DeviceN (/OPM applies to the current colour, not to them).</summary>
    private bool OverprintsIn(PdfObject? space, PdfDictionary? resources)
    {
        if (space is null or PdfNull) return false;
        try
        {
            return Space(space, resources).IsColorant;
        }
        catch (Exception e) when (IsBrokenData(e))
        {
            return false;
        }
    }

    /// <summary>
    /// Whether overprint takes effect (ISO 32000-2, 11.7.4.3): with Separation/DeviceN always (the other separations remain), with
    /// DeviceCMYK only with /OPM 1 and at least one component 0 (the remaining separations remain); otherwise all four are painted
    /// and overprint changes nothing.
    /// </summary>
    private bool Overprints(bool enabled, ColorSpace space, double[] color) =>
        enabled && (space.IsColorant || (space.IsDeviceCmyk && _gs.OverprintMode == 1 && color.Any(c => c == 0)));

    // ------------------------------------------------------------ Patterns and shadings

    /// <summary>Pattern from the resources (/Pattern); tiling patterns are streams, shading patterns dictionaries.</summary>
    private static PdfObject? PatternNamed(PdfName name, PdfDictionary? resources) =>
        resources?.GetDictionary(PdfName.Get("Pattern"))?.Get(name) is { } pattern and not PdfNull ? pattern : null;

    // Space that pattern matrices refer to (ISO 32000-2, 8.7.3.1): that of the content stream in which the pattern is used — the
    // page, or the form or annotation at the time it is invoked
    private Matrix _patternBase = Matrix.Identity;

    // Built shadings per (shading, matrix, opacity, background): a shading is often used many times
    private readonly Dictionary<(PdfObject Shading, Matrix ToPage, double Alpha, uint? Background, byte Intent, byte Bpc), int> _shadingIndex = [];

    // Colours or mesh per (shading, scale level): sampled once, then used at each place with only its own matrix and opacity (a
    // file with 3,511 uses of the same shading would otherwise sample it 3,511 times: 380 ms build time instead of 170 ms; with the
    // opacity in the key, a crafted file rebuilt the same mesh for every ca)
    private readonly Dictionary<(PdfObject Shading, int Scale, byte Intent, byte Bpc), ShadingFill?> _shadingFills = [];

    /// <summary>Pattern as a fill of the display list: tiling pattern (type 1) or shading pattern (type 2); -1 for broken ones (then the
    /// fallback colour applies), <see cref="NoPattern"/> for a tiling pattern that paints nothing.</summary>
    private int PatternIndex(PdfObject? pattern, ColorSpace space, double[] color, double alpha)
    {
        if (pattern is PdfStream tiling) return TilingIndex(tiling, space, color, alpha);
        if (pattern?.AsDictionary() is not { } dict || dict.GetInteger(PdfName.Get("PatternType")) != 2) return -1;
        var toPage = (Matrix.FromArray(dict.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity) * _patternBase;
        if (dict.Get(PdfName.Get("Shading")) is not { } shading || shading is PdfNull) return -1;
        if (shading.AsDictionary() is { } shadingDict && ShadingMarksNothing(shadingDict, null)) return NoPattern;
        // the pattern's /ExtGState: only what acts on painting the shading — rendering intent and black point compensation (ISO 32000-2,
        // 11.6.7, Table 75); line style, opacity, blend mode, soft mask and font do not apply (docs/FEHLERGEDAECHTNIS.md, No. 574)
        var (intent, bpc) = (_gs.Intent, _gs.Bpc);
        if (dict.GetDictionary(PdfName.Get("ExtGState")) is { } state)
        {
            if (state.Get(PdfName.Get("RI")) is PdfName ri) _gs.Intent = ColorSpace.IntentFrom(ri);
            if (state.Get(PdfName.Get("UseBlackPtComp")) is PdfName usebpc) _gs.Bpc = ColorSpace.BpcFrom(usebpc);
        }
        try
        {
            return ShadingIndex(shading, toPage, alpha, null, PatternBackground(shading));
        }
        finally
        {
            (_gs.Intent, _gs.Bpc) = (intent, bpc);
        }
    }

    /// <summary>A shading in the colorant /None (or DeviceN of /None only): it never marks, with „sh“ or as a pattern (8.6.6.4; No. 1123).</summary>
    private bool ShadingMarksNothing(PdfDictionary shading, PdfDictionary? resources) =>
        shading.Get(PdfName.Get("ColorSpace")) is { } space and not PdfNull && ColorSpace.Create(space, resources, _doc).PaintsNothing;

    /// <summary>/Background of a shading (ISO 32000-2, Table 77), which applies only when it fills as a pattern (not with "sh"): it fills
    /// the area the shading does not cover.</summary>
    private uint? PatternBackground(PdfObject shading)
    {
        if (shading.AsDictionary() is not { } dict || PdfFunction.Numbers(dict.GetArray(PdfName.Get("Background"))) is not { Length: > 0 } values) return null;
        try
        {
            var space = Rendered(Space(dict.Get(PdfName.Get("ColorSpace")) is { } cs and not PdfNull ? cs : PdfName.Get("DeviceGray"), null));
            return values.Length >= space.Components ? space.ToRgb(values).ToArgb(1) : null;
        }
        catch (Exception e) when (IsBrokenData(e))
        {
            return null;
        }
    }

    private int ShadingIndex(PdfObject shading, Matrix toPage, double alpha, PdfDictionary? resources, uint? background = null)
    {
        var key = (shading, toPage, alpha, background, _gs.Intent, _gs.Bpc);
        if (_shadingIndex.TryGetValue(key, out var index)) return index;
        // Fineness of the patches (types 6/7) by size: one level per power of two, only as far as the fineness still changes
        // (2 to 24 steps per patch); otherwise every new level would rebuild up to 400,000 triangles
        var scale = Shadings.ScaleDependent(shading) && double.IsFinite(toPage.MeanScale) && toPage.MeanScale > 0
            ? (int)Math.Clamp(Math.Round(Math.Log2(toPage.MeanScale)), -6, 6)
            : 0;
        if (!_shadingFills.TryGetValue((shading, scale, _gs.Intent, _gs.Bpc), out var fill))
            _shadingFills[(shading, scale, _gs.Intent, _gs.Bpc)] = fill = Shadings.Create(shading, _doc, resources, toPage, _gs.Intent, _gs.Bpc);
        var a = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        index = fill is null ? -1
            : _builder.AddShading(fill.ToPage == toPage && fill.Alpha == a && background is null ? fill : fill with { ToPage = toPage, Alpha = a, Background = background });
        return _shadingIndex[key] = index;
    }

    // ------------------------------------------------------------ Tiling patterns

    // patterns in pattern cells in pattern cells …: real files need no more than two levels
    private const int MaxPatternDepth = 8;
    private int _patternDepth;

    // Cells per pattern stream, shared with the interpreters of nested cells (a cell does not depend on where it is placed).
    // Null = broken or being built, so a pattern that uses itself finds itself empty.
    private Dictionary<PdfStream, DisplayList?> _tilingCells = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(PdfStream Pattern, Matrix ToPage, uint Color, double Alpha), int> _tilingIndex = [];

    /// <summary>
    /// Tiling pattern as a fill: the cell is built once, each place adds only matrix and colour. Uncoloured patterns (PaintType 2) take
    /// the colour from the numbers before the name, in the underlying colour space of the pattern colour space (ISO 32000-2, 8.7.3.3).
    /// </summary>
    private int TilingIndex(PdfStream pattern, ColorSpace space, double[] components, double alpha)
    {
        var dict = pattern.Dictionary;
        if (dict.GetInteger(PdfName.Get("PatternType")) != 1) return -1;
        uint? color = null;
        if (dict.GetInteger(PdfName.Get("PaintType")) == 2)
        {
            if (space.Components == 0 || components.Length < space.Components) return -1;
            color = Rendered(space).ToRgb(components).ToArgb(alpha);
        }
        var toPage = (Matrix.FromArray(dict.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity) * _patternBase;
        var key = (pattern, toPage, color ?? 0u, alpha);
        if (_tilingIndex.TryGetValue(key, out var index)) return index;
        index = NoPattern;
        var xStep = dict.GetNumber(PdfName.Get("XStep")) ?? 0;
        var yStep = dict.GetNumber(PdfName.Get("YStep")) ?? 0;
        if (xStep != 0 && yStep != 0 && double.IsFinite(xStep) && double.IsFinite(yStep)
            && PdfKnead.Core.Pdf.Document.PdfRectangle.From(dict.Get(PdfName.Get("BBox"))) is { } box && TilingCell(pattern, box) is { } cell)
            index = _builder.AddShading(new TilingFill(cell, RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top), Math.Abs(xStep), Math.Abs(yStep),
                color, toPage) { Alpha = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255) });
        return _tilingIndex[key] = index;
    }

    /// <summary>
    /// Cell of a tiling pattern as a display list of its own in pattern space, clipped to /BBox, starting from the initial graphics state.
    /// Built by a separate interpreter whose work counts towards the work limit of this build.
    /// </summary>
    private DisplayList? TilingCell(PdfStream pattern, PdfKnead.Core.Pdf.Document.PdfRectangle box)
    {
        if (_tilingCells.TryGetValue(pattern, out var cell)) return cell;
        if (_patternDepth >= MaxPatternDepth) return null;
        _tilingCells[pattern] = null;   // being built
        var bounds = RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top);
        // the cell for the same use as the page — printed, its layers in their print state too (docs/FEHLERGEDAECHTNIS.md, No. 767)
        var inner = new PageInterpreter(_doc, _forPrint, _forExport)
        {
            _cancel = _cancel, _maxOperations = Math.Max(0, _maxOperations - _operations), _patternDepth = _patternDepth + 1,
            _tilingCells = _tilingCells, _masksBuilding = _masksBuilding, _maskDepth = _maskDepth, _currentPageBox = bounds,
            _withoutCellGlyphs = _withoutCellGlyphs, _cellPattern = pattern, _pageResources = _pageResources,
            // the page's output intent and the group's device spaces hold in the cell too (No. 593)
            _pageCmyk = _pageCmyk, _groupDevice = _groupDevice,
        };
        try
        {
            var path = inner._builder.AddSharedPath(
                [PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Line, PathVerb.Close],
                [(float)box.Left, (float)box.Bottom, (float)box.Right, (float)box.Bottom, (float)box.Right, (float)box.Top, (float)box.Left, (float)box.Top]);
            inner._gs.Clip = inner._builder.AddClip(inner._gs.Clip, path, inner._builder.AddMatrix(Matrix.Identity), false, bounds);
            using var content = inner.Decode(pattern);
            // a pattern without resources of its own: those of the page (No. 791; before, none — while the writer lent the page's, No. 549)
            inner.Execute(content, pattern.Dictionary.GetDictionary(PdfName.Resources) ?? _pageResources, _cancel);
            inner.FinishGroups();
            cell = inner._builder.Build(bounds);
        }
        finally
        {
            _operations += inner._operations + inner._builder.ShadingWork;
        }
        return _tilingCells[pattern] = cell;
    }

    // ------------------------------------------------------------ XObjects and images

    private void XObject(PdfStream stream, PdfDictionary? resources)
    {
        var dict = stream.Dictionary;
        var oc = dict.Get(PdfName.Get("OC"));
        if (_builder.Tagging && _forPrint && dict.GetName(PdfName.Subtype)?.Value == "Form")
        {
            PrintedForm(stream, oc, resources);
            return;
        }
        if (_optional.IsHidden(oc, resources))
        {
            // ISO 32000-2, 8.9.5.4 c): an image hidden by its /OC shows the first of its /Alternates without /OC or with one that is on;
            // none: nothing (docs/FEHLERGEDAECHTNIS.md, No. 763)
            if (dict.GetName(PdfName.Subtype)?.Value == "Image" && !Hidden && !_textOnly && Alternate(dict, resources, printing: false, out _) is { } shown)
                Image(new ImageSource(shown, null, resources, IsStencil(shown.Dictionary), 0));
            return;
        }
        switch (dict.GetName(PdfName.Subtype)?.Value)
        {
            case "Image":
                if (Hidden || _textOnly) break;
                var image = stream;
                // 8.9.5.4 d): printed, an image without /OC takes the first of its /Alternates marked /DefaultForPrinting — whose own /OC
                // decides whether anything is printed
                if (_forPrint && oc is null or PdfNull && Alternate(dict, resources, printing: true, out var chosen) is var printed && chosen)
                {
                    if (printed is null) break;
                    image = printed;
                }
                Image(new ImageSource(image, null, resources, IsStencil(image.Dictionary), 0));
                break;
            case "Form":
                Form(stream, Matrix.Identity, resources);
                break;
        }
    }

    /// <summary>
    /// A form in a tagged build of the printed page (visibility of text, docs/FEHLERGEDAECHTNIS.md, No. 145): the characters are counted as
    /// on the screen, so that their numbers match those of the observer — a form off on the screen does not count (the observer skips it
    /// there), a form off only when printed counts but draws nothing.
    /// </summary>
    private void PrintedForm(PdfStream stream, PdfObject? oc, PdfDictionary? resources)
    {
        var printHidden = _optional.IsHidden(oc, resources);
        var screenHidden = (_screenOptional ??= OptionalContent.For(_doc)).IsHidden(oc, resources);
        if (printHidden && screenHidden) return;
        if (screenHidden) _serialPause++;
        if (printHidden) _hiddenDepth++;
        try
        {
            Form(stream, Matrix.Identity, resources);
        }
        finally
        {
            if (screenHidden) _serialPause--;
            if (printHidden) _hiddenDepth--;
        }
    }

    private OptionalContent? _screenOptional;

    /// <summary>Adds a link to the clip chain; an observer that wants the chain learns of each new one (<see cref="IContentObserver.ClipLink"/>).</summary>
    private int AddClip(int parent, int path, int matrix, bool evenOdd, RectD bounds, bool formBox = false)
    {
        var count = _builder.ClipCount;
        var clip = _builder.AddClip(parent, path, matrix, evenOdd, bounds, formBox);
        if (_builder.ClipCount > count && _observer is { WantsClipChain: true } observer) observer.ClipLink(clip, parent, bounds, formBox);
        return clip;
    }

    /// <summary>Reports the clip bounds to the observer if it wants them (sanitising: content that is clipped away completely).</summary>
    private void ReportClip(IContentObserver observer)
    {
        if (!observer.WantsClip) return;
        observer.Clip(_builder.ClipBounds(_gs.Clip));
        if (observer.WantsClipChain) observer.ClipChain(_gs.Clip);
    }

    /// <summary>
    /// XObject for the observer: images and forms also on hidden layers (they are still in the file);
    /// a form is announced with its operator number and then processed.
    /// </summary>
    private void ObserveXObject(IContentObserver observer, PdfStream stream, PdfDictionary? resources)
    {
        var dict = stream.Dictionary;
        var hidden = Hidden || LayerHidden(dict.Get(PdfName.Get("OC")), resources);
        switch (dict.GetName(PdfName.Subtype)?.Value)
        {
            case "Image":
                ReportClip(observer);
                observer.Image(_op, _gs.Ctm, stream, null, resources, hidden);
                break;
            case "Form":
                var formToPage = (Matrix.FromArray(dict.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity) * _gs.Ctm;
                observer.BeginForm(_op, stream, formToPage, hidden);
                // On a hidden layer by its own /OC: everything in it is hidden too. Drawing skips a form that is off on the screen
                // entirely, so its characters do not count either (also when it is printed)
                var own = hidden && !Hidden;
                var skipped = _optional.IsHidden(dict.Get(PdfName.Get("OC")), resources);
                if (own) _hiddenDepth++;
                if (skipped) _serialPause++;
                try
                {
                    Form(stream, Matrix.Identity, resources);
                }
                finally
                {
                    if (own) _hiddenDepth--;
                    if (skipped) _serialPause--;
                    observer.EndForm();
                }
                break;
        }
    }

    /// <summary>For the observer (redaction): reports the content of a soft mask like a form, in the space of the current CTM.</summary>
    private void ObserveSoftMask(IContentObserver observer, PdfStream group, PdfDictionary? resources)
    {
        var groupToPage = (Matrix.FromArray(group.Dictionary.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity) * _gs.Ctm;
        observer.BeginSoftMask(_op, group, groupToPage);
        _serialPause++;   // a build draws masks with an interpreter of their own
        // ... which leaves the text state alone: a mask set within BT … ET must not move the text after it (its content changed the
        // text matrix, so a word under a bar was looked for elsewhere and stayed in the file, docs/FEHLERGEDAECHTNIS.md, No. 141)
        var (tm, tlm, op, element, pending) = (_tm, _tlm, _op, _showElement, _textClipPending);
        var clipVerbs = _textClipVerbs.ToArray();
        var clipPoints = _textClipPoints.ToArray();
        try
        {
            Form(group, Matrix.Identity, resources);
        }
        finally
        {
            (_tm, _tlm, _op, _showElement, _textClipPending) = (tm, tlm, op, element, pending);
            _textClipVerbs.Clear();
            _textClipVerbs.AddRange(clipVerbs);
            _textClipPoints.Clear();
            _textClipPoints.AddRange(clipPoints);
            _serialPause--;
            observer.EndForm();
        }
    }

    /// <summary>
    /// The alternate image to draw instead of an image (ISO 32000-2, 8.9.5.4): on the screen, or printed with an image hidden by its /OC, the
    /// first entry of /Alternates without /OC or with one that is on; printed without /OC, the first one with /DefaultForPrinting. Only the
    /// display: observers (redaction, cleaning up) keep seeing the image itself — under a bar its alternates are dropped (No. 434).
    /// </summary>
    /// <param name="image">The dictionary of the base image.</param>
    /// <param name="resources">Resources for the optional content.</param>
    /// <param name="printing">Choose by /DefaultForPrinting (case d) instead of by /OC (case c).</param>
    /// <param name="chosen">An entry was chosen; the result is null if its /OC is off (then nothing is drawn).</param>
    /// <returns>The image XObject of the chosen entry; null if none was chosen or its /OC is off.</returns>
    private PdfStream? Alternate(PdfDictionary image, PdfDictionary? resources, bool printing, out bool chosen)
    {
        chosen = false;
        if (image.GetArray(PdfName.Get("Alternates")) is not { } alternates) return null;
        for (var i = 0; i < Math.Min(alternates.Count, 64); i++)
        {
            if (alternates.Get(i)?.AsDictionary() is not { } entry) continue;
            var oc = entry.Get(PdfName.Get("OC"));
            if (printing ? entry.GetBoolean(PdfName.Get("DefaultForPrinting")) != true : oc is not (null or PdfNull) && _optional.IsHidden(oc, resources)) continue;
            chosen = true;
            if (printing && _optional.IsHidden(oc, resources)) return null;
            return entry.Get(PdfName.Get("Image")) is PdfStream alternate && alternate.Dictionary.GetName(PdfName.Subtype)?.Value is null or "Image"
                ? alternate : null;
        }
        return null;
    }

    private static bool IsStencil(PdfDictionary dict) =>
        (dict.GetBoolean(PdfName.Get("ImageMask")) ?? dict.GetBoolean(PdfName.Get("IM")) ?? false);

    private void Image(ImageSource image)
    {
        if (image.IsStencil)
        {
            if (_gs.FillSpace.PaintsNothing) return;
            image = image with { StencilColor = Rendered(_gs.FillSpace).ToRgb(_gs.FillColor).ToArgb(_gs.FillAlpha) };
        }
        else
        {
            // an image in the colorant /None (or DeviceN of /None only) never marks (8.6.6.4; docs/FEHLERGEDAECHTNIS.md, No. 1123)
            if ((image.Dictionary.Get(PdfName.Get("ColorSpace")) is { } declared and not PdfNull ? declared : image.Dictionary.Get(PdfName.Get("CS"))) is
                { } named and not PdfNull && ColorSpace.Create(named, image.Resources, _doc).PaintsNothing) return;
            // /Intent of the image before that of the graphics state; /UseBlackPtComp only from the state (ISO 32000-2, 8.9.5, 8.6.5.9)
            var intent = image.Dictionary.Get(PdfName.Get("Intent")) is PdfName imageIntent ? ColorSpace.IntentFrom(imageIntent) : _gs.Intent;
            if (intent != image.Intent || _gs.Bpc != image.Bpc) image = image with { Intent = intent, Bpc = _gs.Bpc };
            if (_pageCmyk is not null) image = image with { DeviceCmyk = _pageCmyk };
        }
        var index = _builder.AddImage(image);
        var bounds = _gs.Ctm.TransformBounds(RectD.FromLtrb(0, 0, 1, 1));
        var paint = CurrentPaint();
        // overprint: for images their own colour space counts, not the fill colour (stencil masks paint in the fill colour)
        if (!image.IsStencil && _gs.FillOverprint)
        {
            var dict = image.Dictionary;
            // an inline image's dictionary has its abbreviations expanded (ContentParser), /ColorSpace stands there
            var overprints = OverprintsIn(dict.Get(PdfName.Get("ColorSpace")), image.Resources);
            var style = _builder.PaintAt(paint);
            paint = _builder.AddPaint(style with { Overprint = (byte)((style.Overprint & ~1) | (overprints ? 1 : 0)) });
        }
        // A mask of the image's own (/SMask, /Mask as stream or colour key, JPX with /SMaskInData) replaces the soft mask of the graphics
        // state for this image — one mask per composition; stencil masks stay under it (ISO 32000-2, 11.6.4.3; docs/FEHLERGEDAECHTNIS.md, No. 562)
        var own = image.Dictionary;
        var ownMask = !image.IsStencil && (own.Get(PdfName.Get("SMask")) is PdfStream || own.Get(PdfName.Get("Mask")) is PdfStream or PdfArray
                                           || (own.GetInteger(PdfName.Get("SMaskInData")) ?? 0) != 0);
        var stateMask = _gs.SoftMask;
        if (ownMask) _gs.SoftMask = -1;
        EnsureMask();
        // An opaque image upright on the page: without a mask, colour key or alpha channel of its own
        if (!image.IsStencil && _gs.Ctm.B == 0 && _gs.Ctm.C == 0 && own.Get(PdfName.Get("SMask")) is null or PdfNull
            && own.Get(PdfName.Get("Mask")) is null or PdfNull && (own.GetInteger(PdfName.Get("SMaskInData")) ?? 0) == 0)
        {
            var covered = bounds.Intersect(_builder.ClipBounds(_gs.Clip));
            HideBeneath(covered);
            if (_gs.BlendMode == 0 && _gs.FillAlpha >= 1 && !_gs.FillOverprint && _gs.SoftMask < 0 && _builder.ClipIsRectangular(_gs.Clip))
                _builder.HidePreviousUnder(covered, index, _gs.Ctm, _gs.Clip);
        }
        _builder.Add(DisplayItemKind.Image, false, -1, _builder.AddMatrix(_gs.Ctm), paint, _gs.Clip, index, bounds);
        _gs.SoftMask = stateMask;
    }

    /// <summary>Form XObject: its own content with /Matrix, clipped to /BBox.</summary>
    private void Form(PdfStream stream, Matrix extra, PdfDictionary? parentResources)
    {
        if (_formDepth >= MaxFormDepth || !_activeForms.Add(stream)) return;   // recursion
        var saved = _gs.Clone();
        var savedPatternBase = _patternBase;
        var savedStack = _stack.Count;
        var savedFloor = _stackFloor;
        _stackFloor = savedStack;
        var savedHidden = _hiddenStack.Count;
        var group = -1;
        var savedGroupDevice = _groupDevice;
        _formDepth++;
        try
        {
            var dict = stream.Dictionary;
            var matrix = Matrix.FromArray(dict.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity;
            _gs.Ctm = matrix * extra * _gs.Ctm;
            _gs.MatrixIndex = -1;
            _patternBase = _gs.Ctm;
            if (_gs.FillSpace.IsPattern || _gs.StrokeSpace.IsPattern) _gs.PaintIndex = -1;   // inherited pattern: here relative to the form
            var bboxObject = dict.Get(PdfName.Get("BBox"));
            // BBox of valid numbers but without area ([0 0 0 0]) clips everything away, as in common PDF
            // readers (issue6413). If it is missing or unreadable, there is no clip
            if (bboxObject is PdfArray { Count: 4 } box && Enumerable.Range(0, 4).All(i => box.GetNumber(i) is { } v && double.IsFinite(v))
                && PdfKnead.Core.Pdf.Document.PdfRectangle.From(box) is null)
                return;
            if (PdfKnead.Core.Pdf.Document.PdfRectangle.From(bboxObject) is { } bbox)
            {
                var path = _builder.AddSharedPath(
                    [PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Line, PathVerb.Close],
                    [(float)bbox.Left, (float)bbox.Bottom, (float)bbox.Right, (float)bbox.Bottom, (float)bbox.Right, (float)bbox.Top, (float)bbox.Left, (float)bbox.Top]);
                var bounds = _gs.Ctm.TransformBounds(RectD.FromLtrb(bbox.Left, bbox.Bottom, bbox.Right, bbox.Top));
                _gs.Clip = AddClip(_gs.Clip, path, _builder.AddMatrix(_gs.Ctm), false, bounds, formBox: true);
            }
            // without resources of its own: those of the page (7.8.3, sentence 18; No. 791) — an appearance drawn on its own (no caller) keeps
            // none, as before
            var resources = dict.GetDictionary(PdfName.Resources) ?? (parentResources is null ? null : _pageResources ?? parentResources);
            group = BeginTransparencyGroup(dict);
            savedGroupDevice = EnterGroupSpace(dict, resources, page: false);
            ExecuteShared(stream, resources);
        }
        finally
        {
            if (group >= 0) EndTransparencyGroup(group);
            LeaveGroupSpace(savedGroupDevice);
            _formDepth--;
            _activeForms.Remove(stream);
            while (_stack.Count > savedStack) _stack.Pop();
            _stackFloor = savedFloor;
            while (_hiddenStack.Count > savedHidden)
                if (_hiddenStack.Pop()) _hiddenDepth--;
            _gs = saved;
            _patternBase = savedPatternBase;
            _verbs.Clear();
            _points.Clear();
        }
    }

    /// <summary>
    /// "sh": the shading fills the current clip, as far as it paints there (<see cref="ShadingFill.Coverage"/>). If it cannot be read,
    /// an area in the colour at the middle of the shading is painted instead, which is better than a gap; shadings that no reader draws
    /// are skipped.
    /// </summary>
    private void Shading(PdfObject? shading, PdfDictionary? resources)
    {
        if (Hidden || shading?.AsDictionary() is not { } dict) return;
        if (ShadingMarksNothing(dict, resources)) return;
        var area = _builder.ClipBounds(_gs.Clip).Intersect(_currentPageBox);
        if (area.IsEmpty) return;
        var index = ShadingIndex(shading, _gs.Ctm, _gs.FillAlpha, resources);
        if (index >= 0) area = _builder.ShadingAt(index).Coverage(area);
        if (area.IsEmpty) return;
        var rect = _builder.AddPath(
            [PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Line, PathVerb.Close],
            [(float)area.Left, (float)area.Top, (float)area.Right, (float)area.Top, (float)area.Right, (float)area.Bottom, (float)area.Left, (float)area.Bottom]);
        if (index >= 0)
        {
            var shaded = _builder.AddPaint(new Paint(0, 0, 0, 0, 0, 10, -1, _gs.BlendMode, index,
                Overprint: (byte)(_gs.FillOverprint && OverprintsIn(dict.Get(PdfName.Get("ColorSpace")), resources) ? 1 : 0)));
            EnsureMask();
            if (Opaque(_builder.ShadingAt(index))) HideBeneath(area);
            _builder.Add(DisplayItemKind.Fill, false, rect, _builder.AddMatrix(Matrix.Identity), shaded, _gs.Clip, -1, area);
            return;
        }
        // a shading that cannot be read paints nothing, like an invalid one — before, the clip was filled with the colour of the middle
        // (black from zeros, or /Background, which never applies to "sh", 8.7.4.3): a broken shading could black out a page (No. 1123)
    }

    /// <summary>
    /// Whether a shading paints every point of the plane opaquely: linear with /Extend at both ends, radial with /Extend at both ends
    /// and one circle inside the other (otherwise the cone leaves space beside it), without /BBox. Its colours are opaque; the
    /// opacity of the graphics state is checked by <see cref="HideBeneath"/>.
    /// </summary>
    private static bool Opaque(ShadingFill fill)
    {
        if (fill is not GradientFill { ExtendStart: true, ExtendEnd: true, BBox: null, PaintsNothing: false } g || fill.Alpha < 255) return false;
        if (!g.Radial) return true;
        var c = g.Coords;
        if (c.Length < 6) return false;
        var distance = Math.Sqrt((c[3] - c[0]) * (c[3] - c[0]) + (c[4] - c[1]) * (c[4] - c[1]));
        return distance + Math.Min(c[2], c[5]) <= Math.Max(c[2], c[5]);
    }

    /// <summary>
    /// Before an entry that is opaque over <paramref name="covered"/> (page space): if that is the whole visible page, painted with
    /// Normal blending at full opacity, without overprinting and within a clip of rectangles only, nothing drawn
    /// before it can be seen any more (ISO 32000-2:2020, 11.3.6) and the display list draws from it on. Content that paints the whole
    /// page again and again — a crafted file: 18,000 glyphs, each with a shading over the page — then costs only the last time; what
    /// lies beneath stays in the list for whatever else reads it (hidden text when cleaning up). Inside a transparency group the same
    /// holds for the entries of that group since its start (a damaged file: 700 shadings over the page inside the group of a form took
    /// 7 s per page at 96 dpi).
    /// </summary>
    private void HideBeneath(RectD covered)
    {
        if (_gs.BlendMode != 0 || _gs.FillAlpha < 1 || _gs.FillOverprint) return;
        var page = _currentPageBox;
        if (covered.Left > page.Left || covered.Top > page.Top || covered.Right < page.Right || covered.Bottom < page.Bottom) return;
        if (!_builder.ClipIsRectangular(_gs.Clip)) return;
        if (_group < 0) _builder.HideEarlier();
        else _builder.HideEarlierInGroup(_group);
    }

    // "sh" without a clip fills the page; without a page box, limited to a generous area
    private RectD _currentPageBox = RectD.FromLtrb(-1e5, -1e5, 1e5, 1e5);

    // ------------------------------------------------------------ Annotations

    /// <summary>Draws the appearance /AP /N of an annotation, mapped onto its /Rect (ISO 32000-2, 12.5.5, Algorithm 8.1).</summary>
    private void DrawAnnotation(PdfDictionary annot)
    {
        var flags = annot.GetInteger(PdfName.Get("F")) ?? 0;
        if (_forPrint ? (flags & (2 | 4)) != 4 : (flags & (2 | 32)) != 0) return;   // Hidden, NoView; when printing: Print set and Hidden not set
        if (annot.GetName(PdfName.Subtype)?.Value == "Popup") return;  // appears only on click
        if (_optional.IsHidden(annot.Get(PdfName.Get("OC")), null)) return;

        var ap = annot.GetDictionary(PdfName.Get("AP"));
        var normal = ap?.Get(PdfName.N);
        if (normal is PdfDictionary states && normal is not PdfStream)
            normal = annot.GetName(PdfName.Get("AS")) is { } state ? states.Get(state) : null;
        if (AnnotationAppearance.RectOf(annot) is not { } rect) return;   // notes also with an empty /Rect
        var subtype = annot.GetName(PdfName.Subtype)?.Value;
        // NoZoom: on screen the size at 100 %, anchored at the top-left corner of /Rect (12.5.3); a closed text note behaves as if it were
        // set (12.5.6.4); printing uses /Rect (No. 576)
        var noZoom = ((flags & 8) != 0 || subtype == "Text") && !_forPrint;
        if (noZoom) _builder.BeginNoZoom(rect.Left, rect.Top);
        try
        {
            DrawAnnotation(annot, normal, rect, subtype, flags);
        }
        finally
        {
            if (noZoom) _builder.EndNoZoom();
        }
    }

    private void DrawAnnotation(PdfDictionary annot, PdfObject? normal, Document.PdfRectangle rect, string? subtype, long flags)
    {
        // NoRotate on a rotated page: stays upright, rotated about the top-left corner of /Rect (ISO 32000-2, 12.5.3; bug2082236)
        var upright = (flags & 16) != 0 && _pageRotate != 0;
        // Without any /AP /N: draw square, circle, line, ink, markups … ourselves, as the other readers do
        // (in page space; its box is /Rect or encloses the points that extend beyond it)
        if (normal is null or PdfNull && subtype is not ("Widget" or "FreeText") && AnnotationAppearance.Build(annot, rect) is { } generated)
        {
            DrawAppearance(generated, Document.PdfRectangle.From(generated.Dictionary.Get(PdfName.Get("BBox"))) ?? rect, upright ? rect : null);
            return;
        }
        // text without an appearance of its own: field values without /AP or with NeedAppearances, FreeText without /AP — laid out as when flattening;
        // over the appearance, or instead of it where it updates it (what stays of the old one inside, docs/FEHLERGEDAECHTNIS.md, No. 815)
        var text = subtype is "Widget" or "FreeText" ? TextAppearance(annot) : null;
        if (normal is PdfStream form && !rect.IsEmpty && text is not { Over: false }) DrawAppearance(form, rect, upright ? rect : null);
        if (text is { Form: var textForm }) DrawAppearance(textForm, rect, upright ? rect : null);
    }

    // /Rotate of the page (for annotations with NoRotate)
    private int _pageRotate;

    // Laid-out texts per annotation (a form with hundreds of fields would otherwise be laid out again on every build of the page).
    // Dictionaries compare only by identity, so the cached entry carries a key built from their values.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfDictionary, Tuple<string, PdfStream, bool>> TextAppearances = new();

    /// <summary>The laid-out text of an annotation (<see cref="Writing.FieldAppearance.TextAppearance"/>); <c>Over</c>: drawn over its appearance,
    /// otherwise instead of it.</summary>
    private (PdfStream Form, bool Over)? TextAppearance(PdfDictionary annot)
    {
        // key: what determines the text (value, /Contents, /DA, /Rect, /AP); unchanged → the previously laid-out appearance
        var key = $"{Editing.FormField.Of(annot)?.StoredValue}\u0001{annot.Get(PdfName.Contents)}\u0001{annot.Get(PdfName.Get("DA"))}\u0001{annot.Get(PdfName.Get("Rect"))}\u0001{annot.GetRaw(PdfName.Get("AP"))}";
        if (TextAppearances.TryGetValue(annot, out var cached) && cached.Item1 == key) return (cached.Item2, cached.Item3);
        if (Writing.FieldAppearance.TextAppearance(_doc, annot) is not { } laidOut) return null;
        TextAppearances.AddOrUpdate(annot, Tuple.Create(key, laidOut.Appearance.Form, laidOut.OverAppearance));
        return (laidOut.Appearance.Form, laidOut.OverAppearance);
    }

    /// <summary>
    /// Draws the form XObject of an annotation mapped onto its /Rect (ISO 32000-2, 12.5.5, Algorithm 8.1). <paramref name="upright"/>: /Rect of
    /// an annotation with NoRotate; the appearance is rotated about its top-left corner against the rotation of the page.
    /// </summary>
    private void DrawAppearance(PdfStream form, Document.PdfRectangle rect, Document.PdfRectangle? upright = null)
    {
        if (Document.PdfRectangle.From(form.Dictionary.Get(PdfName.Get("BBox"))) is not { } bbox) return;

        var formMatrix = Matrix.FromArray(form.Dictionary.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity;
        var transformed = formMatrix.TransformBounds(RectD.FromLtrb(bbox.Left, bbox.Bottom, bbox.Right, bbox.Top));
        if (transformed.Width <= 0 || transformed.Height <= 0) return;
        var a = Matrix.Translate(-transformed.Left, -transformed.Top)
                * Matrix.Scale(rect.Width / transformed.Width, rect.Height / transformed.Height)
                * Matrix.Translate(rect.Left, rect.Bottom);
        if (upright is { } corner)
            a = a * Matrix.Translate(-corner.Left, -corner.Top) * Matrix.Rotate(_pageRotate) * Matrix.Translate(corner.Left, corner.Top);

        _gs = new GraphicsState();
        _stack.Clear();
        Form(form, a, null);
    }
}

/// <summary>
/// Graphics state (ISO 32000-2, 8.4); copied on "q", restored on "Q". A struct, so the copy
/// lives in the stack itself and no object is allocated per "q" (files contain tens of thousands of them).
/// </summary>
internal struct GraphicsState
{
    public GraphicsState()
    {
    }

    public Matrix Ctm = Matrix.Identity;
    public int Clip;
    public ColorSpace FillSpace = ColorSpace.DeviceGray;
    public ColorSpace StrokeSpace = ColorSpace.DeviceGray;
    public double[] FillColor = [0];
    public double[] StrokeColor = [0];
    public double FillAlpha = 1;
    public double StrokeAlpha = 1;
    public double LineWidth = 1;
    public byte LineCap;
    public byte LineJoin;
    public double MiterLimit = 10;
    public DashPattern? Dash;
    public byte BlendMode;

    // soft mask: index into the masks of the display list, −1 = none
    public int SoftMask = -1;

    // overprint (ISO 32000-2, 11.7.4): /OP for strokes, /op for fills, /OPM
    public bool StrokeOverprint;
    public bool FillOverprint;
    public int OverprintMode;

    // patterns for fill and stroke colour when the colour space is Pattern
    public PdfObject? FillPattern;
    public PdfObject? StrokePattern;

    // the Pattern space chosen, but no pattern yet: its initial colour, a pattern that paints nothing (8.6.8; No. 1123)
    public bool FillPatternUnset;
    public bool StrokePatternUnset;

    // text state (ISO 32000-2, 9.3)
    public double CharSpacing;
    public double WordSpacing;
    public double HorizontalScale = 1;
    public double Leading;
    public Fonts.PdfFont? Font;
    public double FontSize = 1;
    public int TextMode;
    public double Rise;

    // rendering intent (ColorSpace.Perceptual …) and black point compensation (ColorSpace.BpcDefault …), ISO 32000-2, 8.6.5.8, 8.6.5.9
    public byte Intent = ColorSpace.RelativeColorimetric;
    public byte Bpc;

    // cache: index of style and matrix in the display list; -1 = recompute
    public int PaintIndex = -1;
    public int MatrixIndex = -1;

    public readonly GraphicsState Clone() => this;
}

/// <summary>Blend modes (ISO 32000-2, 11.3.5), stored as a byte per display list entry and per transparency group.</summary>
public static class BlendModes
{
    private static readonly string[] Names =
    [
        "Normal", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "ColorDodge", "ColorBurn",
        "HardLight", "SoftLight", "Difference", "Exclusion", "Hue", "Saturation", "Color", "Luminosity",
    ];

    /// <summary>Converts a /BM value into the blend mode number.</summary>
    /// <param name="obj">A name, or an array of names: the first one known counts (ISO 32000-2:2020, 11.6.3).</param>
    /// <returns>The index of the blend mode (0 = Normal); 0 for unknown names, including Compatible.</returns>
    public static byte From(PdfObject obj)
    {
        switch (obj)
        {
            case PdfName n:
                return Index(n.Value);
            case PdfArray a:
                for (var k = 0; k < Math.Min(a.Count, 64); k++)
                    if (a.Get(k) is PdfName name && Array.IndexOf(Names, name.Value) >= 0) return Index(name.Value);
                return 0;
            default:
                return 0;
        }

        static byte Index(string name) => (byte)Math.Max(0, Array.IndexOf(Names, name));
    }

    /// <summary>Returns the PDF name of a blend mode number.</summary>
    /// <param name="mode">The number returned by <see cref="From"/>.</param>
    /// <returns>The name without slash; "Normal" for numbers out of range.</returns>
    public static string Name(byte mode) => mode < Names.Length ? Names[mode] : "Normal";
}
