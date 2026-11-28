using System.Runtime.InteropServices;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Content;

/// <summary>Text objects and text operators (ISO 32000-2, 9.3 and 9.4), Type 3 glyphs (ISO 32000-2, 9.6.4).</summary>
public sealed partial class PageInterpreter
{
    private Matrix _tm = Matrix.Identity;      // text matrix
    private Matrix _tlm = Matrix.Identity;     // text line matrix
    private bool _uncolored;                   // Type 3 glyph with d1, or the cell of an uncoloured pattern (8.6.8)

    // text as a clip (modes 4–7): glyph outlines in page space, applied at ET
    private readonly List<PathVerb> _textClipVerbs = [];
    private readonly List<float> _textClipPoints = [];
    private bool _textClipPending;

    // each outline enters the display list only once per page
    private readonly Dictionary<GlyphOutline, int> _glyphPaths = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfObject, PdfFont> _fonts = new(ReferenceEqualityComparer.Instance);

    private PdfDictionary? _textResources;    // resources at the text operator (for Type 3 glyphs without their own)

    /// <summary>
    /// Whether a text operator sets the text line matrix anew as the display reads it: BT, T*, Td and TD with two operands, Tm with six that
    /// give a finite matrix, ' with its string, " with three operands and its string. One that only looks like it (a Td with one operand in a
    /// damaged file) leaves the text position where the text before it ended. Rewriting content reads them through this as well: a redaction
    /// took such a Td for the start of a line, left out the width of the text removed before it, and the rest of the line slid under the bar
    /// (docs/FEHLERGEDAECHTNIS.md, No. 383).
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="o">Its operands.</param>
    /// <returns>True if it starts a line.</returns>
    internal static bool StartsLine(string op, IReadOnlyList<Operand> o) => op switch
    {
        "BT" or "T*" => true,
        "Td" or "TD" => o.Count >= 2,
        "Tm" => o.Count >= 6 && new Matrix(o[0].Number, o[1].Number, o[2].Number, o[3].Number, o[4].Number, o[5].Number).IsFinite,
        "'" => o.Count >= 1 && o[0].Object is PdfString,
        "\"" => o.Count >= 3 && o[2].Object is PdfString,
        _ => false,
    };

    private void TextOperator(string op, IReadOnlyList<Operand> o, PdfDictionary? resources)
    {
        _textResources = resources;
        switch (op)
        {
            case "BT":
                _tm = _tlm = Matrix.Identity;
                BeginTextObject();
                break;
            case "ET":
                EndText();
                EndTextObject();
                break;
            case "Tc" when o.Count >= 1:
                _gs.CharSpacing = o[0].Number;
                break;
            case "Tw" when o.Count >= 1:
                _gs.WordSpacing = o[0].Number;
                break;
            case "Tz" when o.Count >= 1:
                _gs.HorizontalScale = o[0].Number / 100;
                break;
            case "TL" when o.Count >= 1:
                _gs.Leading = o[0].Number;
                break;
            case "Tf" when o.Count >= 2:
                // a name the resources lack: the substitute in the size of Tf, as two of three reference readers (special rule, V14; probe B12e)
                _gs.Font = FontResource(o[0].Name, resources) ?? DefaultFont;
                _gs.FontSize = o[1].Number;
                break;
            // a mode outside 0–7 is passed over and the one before stays, as one reference reader does (the other two agree on what is seen; probes B12a,
            // B12b) — before it was clamped: 9 became a clip that hid the text and everything after it, −1 made invisible text visible
            // (ISO 32000-2, 9.3.6, Table 104; docs/FEHLERGEDAECHTNIS.md, No. 1165). Fractions are cut off (3.5 as 3)
            case "Tr" when o.Count >= 1 && o[0].Number is >= 0 and < 8:
                _gs.TextMode = (int)o[0].Number;
                break;
            case "Ts" when o.Count >= 1:
                _gs.Rise = o[0].Number;
                break;
            case "Td" when StartsLine(op, o):
                NextLine(o[0].Number, o[1].Number);
                ReportLine();
                break;
            case "TD" when StartsLine(op, o):
                _gs.Leading = -o[1].Number;
                NextLine(o[0].Number, o[1].Number);
                ReportLine();
                break;
            case "Tm" when StartsLine(op, o):
                _tm = _tlm = new Matrix(o[0].Number, o[1].Number, o[2].Number, o[3].Number, o[4].Number, o[5].Number);
                ReportLine();
                break;
            case "T*":
                NextLine(0, -_gs.Leading);
                ReportLine();
                break;
            case "Tj" when o.Count >= 1 && o[0].Object is PdfString tj:
                _showElement = 0;
                ShowText(tj.Bytes);
                break;
            case "'" when StartsLine(op, o) && o[0].Object is PdfString quote:
                NextLine(0, -_gs.Leading);
                ReportLine();
                _showElement = 0;
                ShowText(quote.Bytes);
                break;
            case "\"" when StartsLine(op, o) && o[2].Object is PdfString dquote:
                _gs.WordSpacing = o[0].Number;
                _gs.CharSpacing = o[1].Number;
                NextLine(0, -_gs.Leading);
                ReportLine();
                _showElement = 2;
                ShowText(dquote.Bytes);
                break;
            case "TJ" when o.Count >= 1 && o[0].Object is PdfArray tjArray:
                for (var i = 0; i < tjArray.Count; i++)
                {
                    var item = tjArray[i];
                    if (item is PdfString s)
                    {
                        _showElement = i;
                        ShowText(s.Bytes);
                    }
                    else if (item.AsNumber() is { } adjust) Adjust(adjust);
                }
                break;
            // only in the glyph description of a Type 3 font (ISO 32000-2, 9.6.4 sentence 89): in the page content or a form it is passed over, as
            // two of three reference readers — before it switched the colour operators off for the rest of the page (probe D1a;
            // docs/FEHLERGEDAECHTNIS.md, No. 1165)
            case "d1" when _type3Depth > 0:
                _uncolored = true;
                break;
        }
    }

    /// <summary>Tells the observer the line matrix after an operator that starts a line (<see cref="IContentObserver.LineMatrix"/>).</summary>
    private void ReportLine()
    {
        if (_observer is { WantsLineMatrix: true } observer) observer.LineMatrix(_op, _tlm);
    }

    private void NextLine(double tx, double ty)
    {
        _tlm = Matrix.Translate(tx, ty) * _tlm;
        _tm = _tlm;
    }

    /// <summary>Displacement from TJ: a number in thousandths of the font size, to the left (or downwards for vertical writing).</summary>
    private void Adjust(double amount)
    {
        var shift = -amount / 1000 * _gs.FontSize;
        _tm = _gs.Font is { Vertical: true }
            ? Matrix.Translate(0, shift) * _tm
            : Matrix.Translate(shift * _gs.HorizontalScale, 0) * _tm;
    }

    private PdfFont? FontResource(PdfName? name, PdfDictionary? resources)
    {
        if (name is null || resources?.GetDictionary(PdfName.Get("Font")) is not { } fonts || fonts.Get(name) is not PdfDictionary dict) return null;
        var font = FontOf(dict);
        // remember the object number (text editing: new text refers to the same font when saved)
        if (font.Id is null && fonts.GetRaw(name) is PdfReference reference) font.Id = reference.Id;
        return font;
    }

    /// <summary>Font for a font dictionary (from /Font in the resources or /Font in an ExtGState), created once per dictionary.</summary>
    private PdfFont FontOf(PdfDictionary dict)
    {
        if (_fonts.TryGetValue(dict, out var font)) return font;
        font = PdfFont.Get(dict, _doc);
        _fonts[dict] = font;
        return font;
    }

    // ------------------------------------------------------------ Showing characters

    private void ShowText(byte[] bytes)
    {
        var font = _gs.Font ?? DefaultFont;
        var size = _gs.FontSize;
        var th = _gs.HorizontalScale;
        var mode = _gs.TextMode;
        // no Tf yet (font and size have no initial value, ISO 32000-2, 9.3.1): none of the three reference readers draws such text (probe B12d);
        // it is still read, as invisible text in size 1 that clips nothing, so that search, copying and redaction find it
        // (docs/FEHLERGEDAECHTNIS.md, No. 1165)
        if (_gs.Font is null) mode = 3;
        if (mode >= 4)
        {
            _textClipPending = true;
            if (_observer is { WantsTextClip: true } clipObserver) clipObserver.TextClip(_op);
        }
        var span = bytes.AsSpan();
        // style for the observer once per operator (converting colours is costly with ICC; Type 3 glyphs change the state only
        // within their own scope, which is restored afterwards)
        GlyphStyle? style = null;
        for (var pos = 0; pos < span.Length;)
        {
            // every character counts towards the work of the page, and the work stays cancellable inside one text operator: a string of
            // 56 million characters was a single operation, and redacting it took over ten seconds without a way to cancel
            // (docs/FEHLERGEDAECHTNIS.md, No. 420)
            if (++_operations > _maxOperations && OverBudget()) return;
            if ((_operations & 0x3FFF) == 0) _cancel.ThrowIfCancellationRequested();
            var start = pos;
            var length = font.ReadCode(span, pos, out var code);
            pos += Math.Max(1, length);
            var glyph = font.Glyph(code, length);
            // a bitmap glyph (embedded font without outlines): the strike nearest to the size of the em on the page, before anything reads its
            // shape — drawing, clipping and redaction see the same (docs/FEHLERGEDAECHTNIS.md, No. 1169)
            if (glyph.Bitmap is { } bitmap && _tm * _gs.Ctm is var emToPage)
                glyph = bitmap.At(Math.Abs(size) * Math.Sqrt(emToPage.C * emToPage.C + emToPage.D * emToPage.D));
            if (AddsToTextClip(glyph, mode) && _observer is { WantsTextClip: true } clipGlyphObserver)
                clipGlyphObserver.TextClipGlyph(_op, _showElement, start);
            var spacing = _gs.CharSpacing + (PdfFont.IsWordSpace(code, length) ? _gs.WordSpacing : 0);

            // glyph space: font size, horizontal scaling, rise, then the text matrix
            var textSpace = new Matrix(size * th, 0, 0, size, 0, _gs.Rise);
            if (font.Vertical) textSpace = Matrix.Translate(-glyph.OriginX, -glyph.OriginY) * textSpace;
            var textToUser = textSpace * _tm;
            var advance = font.Vertical ? glyph.VerticalAdvance * size + spacing : (glyph.Width * size + spacing) * th;
            var serial = _serialPause > 0 ? -1 : _glyphSerial++;
            if (_observer is { } observer)
            {
                var (cell, ink) = GlyphCell(font, glyph);
                ReportClip(observer);
                if (observer.WantsStyle) observer.Style(style ??= CurrentStyle(font));
                observer.GlyphSerial(serial);
                observer.GlyphDraws(glyph.Procedure is not null || !glyph.Outline.IsEmpty);
                if (font is Type3Font type3) observer.GlyphType3(type3.ProcedurePaints(code), type3.TextGuessed(code));
                observer.Glyph(_op, _showElement, start, Math.Max(1, length), cell, ink, textToUser * _gs.Ctm, advance, size, th,
                    font.Vertical, _tm * _gs.Ctm, glyph.Text, Hidden, mode == 3);
            }
            if (_builder.Tagging && _builder.GlyphTag < 0 && serial >= 0)
            {
                // Everything the character draws belongs to it, also the content of a Type 3 glyph
                var before = _builder.Count;
                _builder.GlyphTag = serial;
                try
                {
                    DrawGlyph(font, glyph, textToUser, mode);
                }
                finally
                {
                    _builder.GlyphTag = -1;
                }
                // a space that paints nothing hides nothing (Type 3 fonts draw it as an empty procedure)
                // Should paint (not a space; a Type 3 glyph only with a text, an empty one without is a spacer) but painted nothing:
                // on a hidden layer, colour space /None, clipped away completely
                var paints = mode is 0 or 1 or 2 && (glyph.Procedure is not null ? !string.IsNullOrWhiteSpace(glyph.Text) : !glyph.Outline.IsEmpty)
                             && !(glyph.Text is { } t && string.IsNullOrWhiteSpace(t));
                _builder.NoteGlyph(serial, silent: paints && (Hidden || _builder.Count == before && !_builder.Full), clips: mode >= 4);
            }
            // a character of the pattern cell being built that the visibility check leaves out (it still advances)
            else if (!(_withoutCellGlyphs is { } leftOut && _cellPattern is { } cellPattern && _formDepth == 0 && _type3Depth == 0
                       && leftOut.Contains(new CellGlyph(cellPattern, _op, start))))
            {
                DrawGlyph(font, glyph, textToUser, mode);
            }

            _tm = font.Vertical
                ? Matrix.Translate(0, advance) * _tm
                : Matrix.Translate(advance, 0) * _tm;
        }
    }

    private static readonly PdfFont DefaultFont = SimpleFont.Fallback(new PdfDictionary());

    /// <summary>Style for the observer (text editing); colours as drawn, patterns as their fallback colour.</summary>
    private GlyphStyle CurrentStyle(PdfFont font) => new(font, _gs.TextMode,
        Rendered(_gs.FillSpace).ToRgb(_gs.FillColor).ToArgb(_gs.FillAlpha), Rendered(_gs.StrokeSpace).ToRgb(_gs.StrokeColor).ToArgb(_gs.StrokeAlpha),
        _gs.LineWidth * _gs.Ctm.MeanScale, _gs.CharSpacing, _gs.WordSpacing, _gs.Rise, _gs.FillSpace.IsPattern);

    /// <summary>
    /// Whether a character adds its outline to the text clip: mode 4–7, an outline, not a Type 3 glyph (ISO 32000-2, 9.3.6), a font size other
    /// than 0 (text of size 0 clips no pixel, 9.3.1; before, its empty outlines clipped away everything after it, docs/FEHLERGEDAECHTNIS.md,
    /// No. 768). One condition for the clip the display builds and for what it reports to redaction, so that the two never part (No. 441).
    /// </summary>
    private bool AddsToTextClip(PdfGlyph glyph, int mode) => mode >= 4 && glyph.Procedure is null && !glyph.Outline.IsEmpty && _gs.FontSize != 0;

    private void DrawGlyph(PdfFont font, PdfGlyph glyph, Matrix textToUser, int mode)
    {
        // the outline adds to the text clip on a hidden layer as well: a clip acts there as everywhere (ISO 32000-2, 8.11.3.1; No. 441)
        if (!_textOnly && AddsToTextClip(glyph, mode)) AppendTextClip(glyph.Outline, glyph.ToText * textToUser * _gs.Ctm);
        if (Hidden) return;
        var textToPage = textToUser * _gs.Ctm;
        RecordText(font, glyph, textToPage, mode != 3 && mode != 7);
        if (_textOnly && glyph.Procedure is null) return;   // Type 3 procedures can show text themselves, so they still run
        if (_gs.FontSize == 0) return;   // text of size 0 marks no pixel (9.3.1; No. 768) — it is still read as text

        if (glyph.Procedure is { } procedure)
        {
            if (mode is not (3 or 7)) Type3Glyph(font, procedure, glyph.ToText * textToPage);
            return;
        }
        var outline = glyph.Outline;
        if (outline.IsEmpty) return;
        var glyphToPage = glyph.ToText * textToPage;

        DisplayItemKind? kind = mode switch
        {
            0 or 4 => _gs.FillSpace.PaintsNothing ? null : DisplayItemKind.Fill,
            1 or 5 => _gs.StrokeSpace.PaintsNothing ? null : DisplayItemKind.Stroke,
            2 or 6 => DisplayItemKind.FillStroke,
            _ => null,
        };
        if (kind is not { } k) return;

        var bounds = glyphToPage.TransformBounds(outline.Bounds);
        // Entirely outside the page: never visible, so no entry and no matrix of its own (for search and copying it was recorded above).
        // Otherwise a line of 2 million characters far beyond the edge occupied 2.3 GB. Masks and pattern cells have no page edge;
        // they use their own area
        if (!bounds.Inflate(1).Intersects(_currentPageBox)) return;
        int path, paint;
        var matrix = glyphToPage;
        if (k == DisplayItemKind.Fill)
        {
            path = GlyphPath(outline);
            paint = CurrentPaint();
        }
        else
        {
            // the line width applies in user space (9.3.6): the outline is taken into user space and stroked there, then the CTM — before the
            // width was scaled into glyph space by its mean scale, and a horizontal scaling (Tz 300) made the vertical edges of a glyph 7 and
            // the horizontal ones 2.5 wide for 4 w (two of three reference readers stroke in user space: probe K16, No. 1153)
            var inUser = new GlyphOutline();
            inUser.Append(outline, glyph.ToText * textToUser);
            inUser.Finish();
            path = _builder.AddPath(inUser.VerbSpan, inUser.PointSpan);
            matrix = _gs.Ctm;
            // like the current style (colours, patterns, overprint) and with its dash pattern, which applies in user space as the width does
            // (9.3.6; before the dashes were dropped, as one of three reference readers does — probe K19, docs/FEHLERGEDAECHTNIS.md, No. 1156)
            paint = _builder.AddPaint(_builder.PaintAt(CurrentPaint()) with { LineWidth = (float)_gs.LineWidth });
            bounds = bounds.Inflate(_gs.LineWidth * _gs.Ctm.MeanScale);
        }
        EnsureMask();
        var patternGroup = BeginPatternGroup(k is DisplayItemKind.Fill or DisplayItemKind.FillStroke, k is DisplayItemKind.Stroke or DisplayItemKind.FillStroke);
        if (k == DisplayItemKind.Fill && glyph.Levels is { } levels && _builder.PaintAt(paint) is { FillShading: < 0 } style)
        {
            // a grey bitmap glyph: each grey level filled with its share of the opacity, in the fill colour (EBDT bit depth 2, 4, 8; No. 1169)
            var at = _builder.AddUniqueMatrix(matrix);
            foreach (var (shape, alpha) in levels)
            {
                var fill = style.Fill & 0x00FFFFFFu | (uint)((style.Fill >> 24) * alpha / 255) << 24;
                _builder.Add(k, false, GlyphPath(shape), at, _builder.AddPaint(style with { Fill = fill }), _gs.Clip, -1, glyphToPage.TransformBounds(shape.Bounds), glyph: true);
            }
        }
        else
        {
            // filled and stroked text is text as well: the renderer draws its outline in its real width, never enhanced to a full pixel (No. 816)
            _builder.Add(k, false, path, _builder.AddUniqueMatrix(matrix), paint, _gs.Clip, -1, bounds, glyph: k is DisplayItemKind.Fill or DisplayItemKind.FillStroke);
        }
        EndPatternGroup(patternGroup);
    }

    private int GlyphPath(GlyphOutline outline)
    {
        if (_glyphPaths.TryGetValue(outline, out var index)) return index;
        index = _builder.AddPath(outline.VerbSpan, outline.PointSpan);
        _glyphPaths[outline] = index;
        return index;
    }

    /// <summary>
    /// Cell of a character in glyph space (advance width × descender to ascender, extended by the outlines where they extend beyond it),
    /// for redaction, also for characters without Unicode and for spaces; and its shape (bounds of the outlines, or the cell without an outline).
    /// </summary>
    private static (RectD Cell, RectD Ink) GlyphCell(PdfFont font, PdfGlyph glyph)
    {
        double x0, x1, y0, y1;
        if (font.Vertical)
        {
            (x0, x1) = (0, glyph.Width > 0 ? glyph.Width : 1);
            (y0, y1) = (glyph.OriginY + glyph.VerticalAdvance, glyph.OriginY);
        }
        else if (font.IsType3 && font.Ascent > font.Descent)
        {
            // the height of a Type 3 font comes from its glyphs' boxes (or the usual proportions): at least 0.9 of the size would make
            // a cell five times as high as small glyphs set large, and a bar over them would not remove them (docs/FEHLERGEDAECHTNIS.md, No. 159)
            (x0, x1) = (0, glyph.Width > 0 ? glyph.Width : 0.25);
            (y0, y1) = (font.Descent, font.Ascent);
        }
        else
        {
            (x0, x1) = (0, glyph.Width > 0 ? glyph.Width : 0.25);
            (y0, y1) = (Math.Min(font.Descent, -0.2), Math.Max(font.Ascent, 0.7));
        }
        var cell = RectD.FromLtrb(x0, Math.Min(y0, y1), x1, Math.Max(y0, y1));
        if (glyph.Procedure is null && !glyph.Outline.IsEmpty)
        {
            var outline = glyph.ToText.TransformBounds(glyph.Outline.Bounds);
            return (RectD.FromLtrb(Math.Min(cell.Left, outline.Left), Math.Min(cell.Top, outline.Top), Math.Max(cell.Right, outline.Right), Math.Max(cell.Bottom, outline.Bottom)), outline);
        }
        return (cell, cell);
    }

    /// <summary>Read texts of the visible characters, to notice suspicious ones (<see cref="DisplayList.MisreadGlyphs"/>).</summary>
    private readonly TextReading _reading = new();

    // ------------------------------------------------------------ Replacement text (/ActualText)

    /// <summary>
    /// A marked-content sequence with /ActualText being read (ISO 32000-2, 14.9.4): its text replaces the text of the characters drawn within it.
    /// Their quadrilaterals are gathered as they come, in the frame of the first one (along its baseline; across it the extent of the characters
    /// that advance — marks above and below their letter do not make the line taller), so the memory stays the same however many characters the
    /// sequence holds.
    /// </summary>
    private sealed class ActualTextSpan(string text, int depth)
    {
        public readonly string Text = text;

        /// <summary>The number of open marked-content sequences with this one (it ends when fewer are open).</summary>
        public readonly int Depth = depth;

        public int Glyphs;
        public double Ox, Oy, Ux, Uy, Nx, Ny, SMin, SMax, TMinAll, TMaxAll, TMin = double.MaxValue, TMax = double.MinValue, Size;
        public bool Visible, Vertical;
    }

    /// <summary>The replacement text being read (an inner one within it counts as part of it, as the outer replaces all its content).</summary>
    private ActualTextSpan? _actualText;

    /// <summary>
    /// Starts reading a replacement text when the properties of a marked-content sequence just opened carry /ActualText and none is being read.
    /// Like two of the three reference readers, the text extraction gives the replacement text in place of the characters drawn within the
    /// sequence, on their quadrilateral (an empty one hides them); before, it was ignored and the characters read as their fonts map them — own
    /// text with reordered Arabic marks or a letter drawn with another letter's glyph read wrongly (docs/FEHLERGEDAECHTNIS.md, No. 1180).
    /// </summary>
    private void BeginActualText(PdfDictionary? properties)
    {
        if (_drawnText || _actualText is not null || properties?.GetString(PdfName.Get("ActualText")) is not { } actual) return;
        _actualText = new ActualTextSpan(WithoutLanguageEscapes(actual.ToText()), _hiddenStack.Count);
    }

    /// <summary>Ends the replacement text being read once its marked-content sequence is closed (or with <paramref name="all"/>, at the end of
    /// the page content), adding its text with the gathered quadrilateral.</summary>
    private void EndActualText(bool all = false)
    {
        if (_actualText is not { } span || !all && _hiddenStack.Count >= span.Depth) return;
        _actualText = null;
        if (span.Glyphs == 0 || span.Text.Length == 0) return;
        var (t0, t1) = span.TMin <= span.TMax ? (span.TMin, span.TMax) : (span.TMinAll, span.TMaxAll);
        double X(double s, double t) => span.Ox + s * span.Ux + t * span.Nx;
        double Y(double s, double t) => span.Oy + s * span.Uy + t * span.Ny;
        _builder.AddText(new TextGlyph(span.Text, (float)X(span.SMin, t0), (float)Y(span.SMin, t0), (float)X(span.SMax, t0), (float)Y(span.SMax, t0),
            (float)X(span.SMax, t1), (float)Y(span.SMax, t1), (float)X(span.SMin, t1), (float)Y(span.SMin, t1), (float)span.Size, span.Visible, span.Vertical));
    }

    /// <summary>A text string without its language escape sequences (ESC, language code, ESC; ISO 32000-2, 7.9.2.2).</summary>
    private static string WithoutLanguageEscapes(string text)
    {
        if (!text.Contains('\u001B')) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        var inside = false;
        foreach (var c in text)
        {
            if (c == '\u001B') inside = !inside;
            else if (!inside) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Adds a character's quadrilateral (corners a, b, c, d as in <see cref="TextGlyph"/>) to the replacement text being read.</summary>
    private static void Gather(ActualTextSpan span, (double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d,
        double size, bool visible, bool vertical, bool advances)
    {
        if (span.Glyphs++ == 0)
        {
            // the frame: origin at a, along a → b, across perpendicular to it towards d
            var (ux, uy) = (b.X - a.X, b.Y - a.Y);
            var length = Math.Sqrt(ux * ux + uy * uy);
            (ux, uy) = length > 1e-9 ? (ux / length, uy / length) : (1, 0);
            var (nx, ny) = (-uy, ux);
            if ((d.X - a.X) * nx + (d.Y - a.Y) * ny < 0) (nx, ny) = (uy, -ux);
            (span.Ox, span.Oy, span.Ux, span.Uy, span.Nx, span.Ny) = (a.X, a.Y, ux, uy, nx, ny);
            (span.SMin, span.SMax, span.TMinAll, span.TMaxAll) = (double.MaxValue, double.MinValue, double.MaxValue, double.MinValue);
            span.Vertical = vertical;
        }
        foreach (var (x, y) in (ReadOnlySpan<(double, double)>)[a, b, c, d])
        {
            var s = (x - span.Ox) * span.Ux + (y - span.Oy) * span.Uy;
            var t = (x - span.Ox) * span.Nx + (y - span.Oy) * span.Ny;
            if (!double.IsFinite(s) || !double.IsFinite(t)) continue;
            (span.SMin, span.SMax) = (Math.Min(span.SMin, s), Math.Max(span.SMax, s));
            (span.TMinAll, span.TMaxAll) = (Math.Min(span.TMinAll, t), Math.Max(span.TMaxAll, t));
            if (advances) (span.TMin, span.TMax) = (Math.Min(span.TMin, t), Math.Max(span.TMax, t));
        }
        span.Size = Math.Max(span.Size, size);
        span.Visible |= visible;
    }

    /// <summary>Records a character with its quadrilateral for search and selection.</summary>
    private void RecordText(PdfFont font, PdfGlyph glyph, Matrix textToPage, bool visible)
    {
        if (_actualText is { } span)
        {
            // within a replacement text: the character counts with its quadrilateral, not with its own text (nor as unreadable)
            var (gx0, gx1) = font.Vertical ? (0, glyph.Width > 0 ? glyph.Width : 1) : (0, glyph.Width > 0 ? glyph.Width : 0.25);
            var (gy0, gy1) = font.Vertical ? (glyph.OriginY + glyph.VerticalAdvance, glyph.OriginY) : (font.Descent, font.Ascent);
            var em = textToPage.TransformVector(0, 1);
            Gather(span, textToPage.Transform(gx0, gy0), textToPage.Transform(gx1, gy0), textToPage.Transform(gx1, gy1), textToPage.Transform(gx0, gy1),
                Math.Sqrt(em.X * em.X + em.Y * em.Y), visible, font.Vertical, glyph.Width > 0);
            return;
        }
        if (glyph.Text is not { Length: > 0 } text)
        {
            // drawn, but without text: no search can find it — counted, so that searching to redact can say so
            if (visible && (!glyph.Outline.IsEmpty || glyph.Procedure is not null)) _builder.AddUnreadable();
            return;
        }
        if (visible && (!glyph.Outline.IsEmpty || glyph.Procedure is not null)) _reading.Add(font, glyph, text);
        double x0, x1, y0, y1;
        if (font.Vertical)
        {
            // vertical: cell around the origin, one vertical advance high
            (x0, x1) = (0, glyph.Width > 0 ? glyph.Width : 1);
            (y0, y1) = (glyph.OriginY + glyph.VerticalAdvance, glyph.OriginY);
        }
        else
        {
            (x0, x1) = (0, glyph.Width > 0 ? glyph.Width : 0.25);
            (y0, y1) = (font.Descent, font.Ascent);
        }
        var (ax, ay) = textToPage.Transform(x0, y0);
        var (bx, by) = textToPage.Transform(x1, y0);
        var (cx, cy) = textToPage.Transform(x1, y1);
        var (dx, dy) = textToPage.Transform(x0, y1);
        var size = textToPage.TransformVector(0, 1);
        _builder.AddText(new TextGlyph(text, (float)ax, (float)ay, (float)bx, (float)by, (float)cx, (float)cy, (float)dx, (float)dy,
            (float)Math.Sqrt(size.X * size.X + size.Y * size.Y), visible, font.Vertical, glyph.AlternateText));
    }

    // ------------------------------------------------------------ Text as a clip

    private void AppendTextClip(GlyphOutline outline, Matrix glyphToPage)
    {
        // the segments count towards the work of the page as those of a path do: a character counted once, and a string of 400 000
        // characters in mode 7 (a few KB compressed) collected some 24 million segments until ET (docs/FEHLERGEDAECHTNIS.md, No. 1140)
        _operations += outline.VerbSpan.Length;
        var points = outline.PointSpan;
        var p = 0;
        foreach (var verb in outline.VerbSpan)
        {
            _textClipVerbs.Add(verb);
            var count = verb switch { PathVerb.Cubic => 3, PathVerb.Close => 0, _ => 1 };
            for (var i = 0; i < count; i++, p += 2)
            {
                var (x, y) = glyphToPage.Transform(points[p], points[p + 1]);
                _textClipPoints.Add((float)x);
                _textClipPoints.Add((float)y);
            }
        }
    }

    /// <summary>ET: in modes 4–7 the union of all glyphs becomes the new clip (ISO 32000-2, 9.3.6).</summary>
    private void EndText()
    {
        if (!_textClipPending) return;
        _textClipPending = false;
        // no outline at all (no glyph, only spaces, Type 3 glyphs): the clip stays as it is (ISO 32000-2, 9.3.6) — an empty clip hid
        // everything that followed (docs/FEHLERGEDAECHTNIS.md, No. 433). Redaction writes the empty clip it needs itself (No. 152)
        if (_textClipVerbs.Count == 0) return;
        RectD bounds = RectD.Empty;
        var path = _builder.AddPath(CollectionsMarshal.AsSpan(_textClipVerbs), CollectionsMarshal.AsSpan(_textClipPoints));
        for (var i = 0; i + 1 < _textClipPoints.Count; i += 2)
        {
            var r = RectD.FromLtrb(_textClipPoints[i], _textClipPoints[i + 1], _textClipPoints[i], _textClipPoints[i + 1]);
            bounds = i == 0 ? r : RectD.FromLtrb(Math.Min(bounds.Left, r.Left), Math.Min(bounds.Top, r.Top), Math.Max(bounds.Right, r.Right), Math.Max(bounds.Bottom, r.Bottom));
        }
        _gs.Clip = AddClip(_gs.Clip, path, _builder.AddMatrix(Matrix.Identity), false, bounds);
        _textClipVerbs.Clear();
        _textClipPoints.Clear();
    }

    // ------------------------------------------------------------ Type3

    private const int MaxType3Depth = 4;
    private int _type3Depth;

    /// <summary>Glyph of a Type 3 font: its content stream, mapped with FontMatrix × text matrix × CTM.</summary>
    private void Type3Glyph(PdfFont font, PdfStream procedure, Matrix glyphToPage)
    {
        if (_type3Depth >= MaxType3Depth || !_activeForms.Add(procedure)) return;
        var saved = _gs.Clone();
        var savedStack = _stack.Count;
        var savedHidden = _hiddenStack.Count;
        var (savedTm, savedTlm, savedUncolored) = (_tm, _tlm, _uncolored);
        var observer = _observer;
        _observer = null;   // the glyph belongs to its text operator; its content is not reported separately
        var (savedOp, savedElement) = (_op, _showElement);   // the glyph content counts its own operators
        _type3Depth++;
        _serialPause++;   // characters inside a glyph belong to it (and whether it runs depends on the layer state)
        try
        {
            _gs.Ctm = glyphToPage;
            _gs.MatrixIndex = _gs.PaintIndex = -1;
            // a glyph shown inside a d1 glyph or an uncoloured cell stays restricted: the restriction holds for everything called from there (8.6.8)
            _uncolored = savedUncolored;
            // a Type 3 font without resources of its own: those of the page (7.8.3, sentence 18; No. 791)
            ExecuteShared(procedure, font.Type3Resources ?? _pageResources ?? _textResources);
        }
        finally
        {
            _observer = observer;
            (_op, _showElement) = (savedOp, savedElement);
            _type3Depth--;
            _serialPause--;
            _activeForms.Remove(procedure);
            while (_stack.Count > savedStack) _stack.Pop();
            while (_hiddenStack.Count > savedHidden)
                if (_hiddenStack.Pop()) _hiddenDepth--;
            EndActualText();
            _gs = saved;
            (_tm, _tlm, _uncolored) = (savedTm, savedTlm, savedUncolored);
            _verbs.Clear();
            _points.Clear();
        }
    }
}
