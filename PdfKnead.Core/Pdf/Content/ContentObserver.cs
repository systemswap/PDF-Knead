using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Content;

/// <summary>
/// A character in the cell of a tiling pattern, as the visibility check knows it (characters in cells have no number in the page's
/// order): the pattern, the operator in the cell's own content and the byte where the character starts in the shown string — the same
/// in every build and every observation of the cell. Characters in forms or Type 3 glyphs inside the cell are not counted.
/// </summary>
/// <param name="Pattern">The pattern stream (compared by reference).</param>
/// <param name="Op">Number of the text operator in the cell's content.</param>
/// <param name="Start">Byte of the character in the shown string.</param>
public readonly record struct CellGlyph(PdfStream Pattern, int Op, int Start);

/// <summary>
/// Observer for redaction: while the page content is processed, it receives for each operator the geometry that operator
/// would draw — including content on hidden layers, because that content is still in the file. <c>op</c> is the number
/// of the operator in its content stream (page or form), counted as <see cref="ContentParser.Next"/> returns them.
/// The interpreter does not report the content of Type 3 glyphs or annotations (a Type 3 glyph counts as a character of its text operator).
/// <c>hidden</c>: the content lies on a hidden layer (optional content) and is therefore not displayed.
/// </summary>
internal interface IContentObserver
{
    /// <summary>
    /// Reports one character: the element of the text operator (Tj: 0, ": 2, TJ: index in the array), its bytes in the string, the cell
    /// in glyph space, the shape of the character (bounds of its outlines) and the mapping to the page, the advance in text space (as it
    /// moves the text matrix), font size, horizontal scaling, vertical writing, text space → page and the Unicode text of the glyph;
    /// <c>invisible</c>: text rendering mode 3 (invisible, for example the OCR text over a scan).
    /// </summary>
    void Glyph(int op, int element, int start, int length, RectD cell, RectD ink, Matrix cellToPage, double advance, double fontSize, double horizontalScale,
        bool vertical, Matrix textToPage, string? text, bool hidden, bool invisible);

    /// <summary>A path is painted (S, f, B …): its bounds in page space (including the line width) and the CTM.</summary>
    void Path(int op, RectD bounds, Matrix ctm, bool hidden);

    /// <summary>
    /// A path ended by "n" without a clip: it paints and clips nothing, but its coordinates stand in the content (ISO 32000-2, 8.5.3.1) —
    /// a shape of what was redacted, such as text converted to curves (docs/FEHLERGEDAECHTNIS.md, No. 449). Its bounds in page space and the CTM.
    /// </summary>
    void EmptyPath(int op, RectD bounds, Matrix ctm)
    {
    }

    /// <summary>Whether the observer wants the points of a path with these bounds in page space (<see cref="PathGeometry"/>).</summary>
    bool WantsPathGeometry(RectD bounds) => false;

    /// <summary>
    /// The points of a path just reported (<see cref="Path"/>, <see cref="EmptyPath"/> or <see cref="ClipPath"/> for one that only clips,
    /// only with <see cref="WantsPathGeometry"/>): its verbs and points in user space, the CTM, whether it is stroked, the line width in user
    /// space and whether it only clips ("W n"). Valid only during the call.
    /// </summary>
    void PathGeometry(int op, ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<float> points, Matrix ctm, bool stroke, double lineWidth, bool clip)
    {
    }

    /// <summary>Whether the observer wants the line of a path only stroked ("S", "s") with these bounds in page space (<see cref="StrokeLine"/>).</summary>
    bool WantsStrokeLine(RectD bounds) => false;

    /// <summary>
    /// The line of a path just reported (<see cref="Path"/>) that is only stroked, not filled (only with <see cref="WantsStrokeLine"/>): its
    /// verbs and points in user space, the CTM and how far in page space its stroke reaches beside the line — half the line width, times
    /// the ratio of its sharpest mitre or of square caps. Valid only during the call.
    /// </summary>
    void StrokeLine(int op, ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<float> points, Matrix ctm, double reach)
    {
    }

    /// <summary>Whether the observer wants to test the clip of a shading ("sh") with these bounds in page space (<see cref="ShadingClip"/>).</summary>
    bool WantsShadingClip(RectD bounds) => false;

    /// <summary>
    /// The clip a shading just reported (<see cref="Shading"/>) paints in: a test whether a point in page space lies inside it. Valid only
    /// during the call.
    /// </summary>
    void ShadingClip(int op, RectD bounds, Func<double, double, bool> inside)
    {
    }

    /// <summary>
    /// A path sets a clip (W, W*), also one that paints nothing ("W n"): its bounds in page space and the CTM, reported with the operator
    /// that ends the path.
    /// </summary>
    void ClipPath(int op, RectD bounds, Matrix ctm)
    {
    }

    /// <summary>Whether the observer wants the stroke of every painted path (<see cref="Stroke"/>; used by redaction to cut paths).</summary>
    bool WantsStroke => false;

    /// <summary>
    /// The stroke of the next painted path, reported immediately before <see cref="Path"/> (only with <see cref="WantsStroke"/>): how
    /// far its ends reach beyond the path in page space (half the width, square caps by √2 — corners are left out: cutting a path makes
    /// ends, no new corners) and its dash pattern (null: solid).
    /// </summary>
    void Stroke(double reach, DashPattern? dash)
    {
    }

    /// <summary>
    /// For a path both filled and stroked (B, B*, b, b*), reported after <see cref="Stroke"/> (only with <see cref="WantsStroke"/>): its
    /// opacities and blend mode — with any of them in effect, its stroke replaces its fill instead of blending with it (ISO 32000-2,
    /// 11.7.4.4) —, whether one of its colours is a pattern, and the stroke parameters as the display uses them (a copy of the path in a
    /// group of its own states them there instead of relying on what the group inherits).
    /// </summary>
    void FillStroke(double fillAlpha, double strokeAlpha, byte blendMode, bool pattern, StrokeStyle style)
    {
    }

    /// <summary>Whether <see cref="PathStart"/> is wanted.</summary>
    bool WantsPathStart => false;

    /// <summary>
    /// A path construction operator (l, c, v, y) begins a path without m: the point the display starts it at — the current point left by
    /// the content before, in user space —, reported with that operator.
    /// </summary>
    void PathStart(int op, double x, double y)
    {
    }

    /// <summary>Whether <see cref="TextClip"/> is wanted.</summary>
    bool WantsTextClip => false;

    /// <summary>
    /// A text operator adds its characters to the clip (text rendering mode 4–7 as the display has it: saved by q, inherited by forms,
    /// clamped and truncated) — reported for each string it shows, before its characters.
    /// </summary>
    void TextClip(int op)
    {
    }

    /// <summary>
    /// A character that adds its outline to the text clip (only with <see cref="WantsTextClip"/>): text rendering mode 4–7, an outline, not
    /// a Type 3 glyph — on a hidden layer too, as a clip acts there as everywhere (ISO 32000-2, 9.3.6 and 8.11.3.1). Reported before the
    /// character, with the element of its operator and its bytes in the shown string. A text object whose characters add no outline sets no
    /// clip (docs/FEHLERGEDAECHTNIS.md, No. 433 and 441).
    /// </summary>
    void TextClipGlyph(int op, int element, int start)
    {
    }

    /// <summary>Whether <see cref="LineMatrix"/> is wanted.</summary>
    bool WantsLineMatrix => false;

    /// <summary>
    /// After an operator that starts a line (Td, TD, Tm, T*, ' and "): the text line matrix the display goes on with — with the leading of
    /// the graphics state, saved by q and inherited by forms, so that a rewriter does not have to follow it on its own.
    /// </summary>
    void LineMatrix(int op, Matrix lineMatrix)
    {
    }

    /// <summary>An image (Do with an image XObject, or BI): mapping from the unit square to the page.</summary>
    void Image(int op, Matrix imageToPage, PdfStream? stream, InlineImage? inline, PdfDictionary? resources, bool hidden);

    /// <summary>"sh": fills the area <paramref name="bounds"/> (the clip in page space).</summary>
    void Shading(int op, RectD bounds, Matrix ctm, bool hidden);

    /// <summary>
    /// A marked-content sequence begins (BMC, BDC; ISO 32000-2, 14.6): its tag and its property list (inline, or the one it names in
    /// /Properties of the resources; null for BMC). Only for observers that follow the marked content (docs/FEHLERGEDAECHTNIS.md, No. 633).
    /// </summary>
    void MarkedContent(int op, PdfName tag, PdfDictionary? properties)
    {
    }

    /// <summary>The marked-content sequence begun last ends (EMC).</summary>
    void EndMarkedContent(int op)
    {
    }

    /// <summary>Start of a form XObject (Do): its operators are reported from here until <see cref="EndForm"/>.</summary>
    void BeginForm(int op, PdfStream form, Matrix formToPage, bool hidden);

    /// <summary>
    /// Start of the content of a soft mask (gs with /SMask; <paramref name="op"/> is that gs): the mask group in the space of the CTM
    /// at the gs (11.6.5.2). Its operators are reported from here until <see cref="EndForm"/>; otherwise text and images inside a mask
    /// would remain in the file under a redaction box.
    /// </summary>
    void BeginSoftMask(int op, PdfStream group, Matrix groupToPage);

    /// <summary>End of the form XObject, soft mask or pattern cell started by <see cref="BeginForm"/>, <see cref="BeginSoftMask"/> or <see cref="BeginPatternCell"/>.</summary>
    void EndForm();

    /// <summary>
    /// Whether the observer wants the content of the tiling pattern a path is painted with (<see cref="BeginPatternCell"/>), for the part
    /// it paints (path within the clip, page space): redaction asks for it where that part reaches under a bar, so that the cell can be
    /// redacted instead of emptied. Asked only for coloured tiling patterns whose painted part lies in a single tile — otherwise the same
    /// content shows in several places.
    /// </summary>
    bool WantsPatternCell(RectD painted) => false;

    /// <summary>
    /// Start of the content of the tiling pattern with which the path operator <paramref name="op"/> fills (<paramref name="stroke"/> false)
    /// or strokes: the cell of the single tile it paints, mapped to the page (8.7.3.1). Its operators are reported from here until
    /// <see cref="EndForm"/>, counted in the pattern's content stream.
    /// </summary>
    void BeginPatternCell(int op, bool stroke, PdfStream pattern, Matrix cellToPage)
    {
    }

    /// <summary>Whether the observer wants the state at every "q" (<see cref="SaveState"/>; used by editing images and drawings: what a block takes along when it moves).</summary>
    bool WantsSaveState => false;

    /// <summary>A "q" in the current content stream: its operator number, the CTM and the bounds of the clip in page space there (only with <see cref="WantsSaveState"/>).</summary>
    void SaveState(int op, Matrix ctm, RectD clip)
    {
    }

    /// <summary>The same with the link of the clip chain there (only with <see cref="WantsClipChain"/>).</summary>
    void SaveState(int op, Matrix ctm, RectD clip, int link) => SaveState(op, ctm, clip);

    /// <summary>Whether the observer wants the bounds of the clip (<see cref="Clip"/>) before every character, path and image; used by sanitising to find content that is clipped away completely.</summary>
    bool WantsClip => false;

    /// <summary>Bounds of the current clip in page space, reported immediately before the next character, path or image (only with <see cref="WantsClip"/>).</summary>
    void Clip(RectD bounds)
    {
    }

    /// <summary>
    /// Whether the observer wants the clip as a chain of links (<see cref="ClipLink"/>, <see cref="ClipChain"/>): editing images and
    /// drawings tells the clips a moved block sets itself (they go along) from those around it (they stay — except the box of a form, which the
    /// move grows).
    /// </summary>
    bool WantsClipChain => false;

    /// <summary>A new link of the clip chain (only with <see cref="WantsClipChain"/>).</summary>
    /// <param name="link">Its number (links are numbered from 1; 0 is no clip).</param>
    /// <param name="parent">The link it adds to (0: none).</param>
    /// <param name="own">Bounds of its own path in page space (not cut by the parent).</param>
    /// <param name="formBox">It is the box (/BBox) of a form.</param>
    void ClipLink(int link, int parent, RectD own, bool formBox)
    {
    }

    /// <summary>The link of the clip chain in effect, reported with <see cref="Clip"/> before the next painting operator (only with <see cref="WantsClipChain"/>).</summary>
    void ClipChain(int link)
    {
    }

    /// <summary>
    /// Number of the next character in content order, reported immediately before <see cref="Glyph"/>; the same number tags the entries
    /// it draws in a display list built with tagging (<see cref="Graphics.DisplayList.GlyphTags"/>). -1 inside soft masks and forms on
    /// hidden layers, which a build does not draw with the page.
    /// </summary>
    void GlyphSerial(int serial)
    {
    }

    /// <summary>
    /// Whether the next character draws anything, reported immediately before <see cref="Glyph"/>: it has an outline, or it is a Type 3
    /// glyph (whose procedure may draw). A character without an outline reports its cell as its shape, so the shape alone does not tell a
    /// space from a letter (docs/FEHLERGEDAECHTNIS.md, No. 438).
    /// </summary>
    void GlyphDraws(bool draws)
    {
    }

    /// <summary>
    /// For a character of a Type 3 font, reported immediately before <see cref="Glyph"/>: whether its procedure paints anything
    /// (<c>Type3Font.ProcedurePaints</c>). A letter that paints nothing keeps its text block locked; a blank space does not
    /// (docs/FEHLERGEDAECHTNIS.md, No. 489, 606). <see cref="GlyphDraws"/> keeps its meaning for the checks of redaction.
    /// </summary>
    /// <param name="paints">Whether its procedure paints anything.</param>
    /// <param name="textGuessed">Whether its text is only guessed from the code (<c>Type3Font.TextGuessed</c>): no readable text for editing.</param>
    void GlyphType3(bool paints, bool textGuessed)
    {
    }

    /// <summary>
    /// Whether content on a layer counts as hidden only if it is off when printed as well (<c>hidden</c> in the callbacks): „Verborgenes
    /// schwärzen“ must not remove a layer that only appears on paper, such as a watermark.
    /// </summary>
    bool HiddenOnlyIfUnprinted => false;

    /// <summary>Whether the observer wants the style of every character (<see cref="GlyphStyle"/>; used by text editing).</summary>
    bool WantsStyle => false;

    /// <summary>Style of the next character, reported immediately before <see cref="Glyph"/> (only with <see cref="WantsStyle"/>).</summary>
    void Style(in GlyphStyle style)
    {
    }
}

/// <summary>
/// Style of a character (text editing): font, text rendering mode, colours (ARGB, sRGB), line width in page space (synthetic bold
/// draws mode 2 with an outline), character and word spacing and text rise in unscaled text space (9.3), as set in the content.
/// </summary>
internal readonly record struct GlyphStyle(Fonts.PdfFont Font, int TextMode, uint FillArgb, uint StrokeArgb, double LineWidth, double CharSpacing,
    double WordSpacing, double Rise, bool FillIsPattern);

/// <summary>The stroke parameters of the graphics state as the display uses them (ISO 32000-2, 8.4.3): width, cap, join, miter limit, dash.</summary>
/// <param name="Width">Line width in user space (the absolute value of what the file sets).</param>
/// <param name="Cap">Line cap style 0 … 2.</param>
/// <param name="Join">Line join style 0 … 2.</param>
/// <param name="MiterLimit">Miter limit (at least 1).</param>
/// <param name="Dash">Dash pattern; null: solid.</param>
public readonly record struct StrokeStyle(double Width, byte Cap, byte Join, double MiterLimit, DashPattern? Dash);
