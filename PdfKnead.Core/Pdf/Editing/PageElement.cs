using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>Font family of a text element (embedded as Liberation Sans/Serif/Mono).</summary>
public enum ElementFont
{
    /// <summary>Sans-serif (Liberation Sans).</summary>
    Sans,
    /// <summary>Serif (Liberation Serif).</summary>
    Serif,
    /// <summary>Monospaced (Liberation Mono).</summary>
    Mono,
}

/// <summary>Horizontal alignment of text in an element (the values match /Q: 0, 1, 2).</summary>
public enum ElementAlignment
{
    /// <summary>Left-aligned.</summary>
    Left,
    /// <summary>Centred.</summary>
    Center,
    /// <summary>Right-aligned.</summary>
    Right,
}

/// <summary>
/// Something placed on a page (text, signature, freehand drawing, image, shape, form field, redaction mark, annotation of
/// another program …). Immutable: a change creates a new element (with <c>with</c>), and undo brings back the old one.
/// Position in page space (PDF points, unrotated, like the /Rect of an annotation).
/// </summary>
public abstract record PageElement
{
    /// <summary>Rectangle in page space (unrotated).</summary>
    public required PdfRectangle Rect { get; init; }

    /// <summary>
    /// Rotation of the content (0/90/180/270, clockwise): upright when the page is displayed with this rotation. The element
    /// rotates along when the page is rotated later.
    /// </summary>
    public int Rotation { get; init; }

    /// <summary>Origin (signatures): line below and signature with the device identifier; null = none.</summary>
    public SignatureSeal? Seal { get; init; }

    /// <summary>
    /// Digital signing (signatures): when saving, the element becomes the appearance of a signature field signed with a
    /// certificate (KONZEPT.md, section 5.17); null = not signed digitally. Such an element has no <see cref="Seal"/>.
    /// </summary>
    public DigitalSigning? Digital { get; init; }

    /// <summary>Text of the origin line below a signature, from its seal or its digital signing; null = no line.</summary>
    public string? OriginLine => Seal?.Origin ?? Digital?.Origin;

    /// <summary>Width and height in reading orientation (rotated as displayed).</summary>
    public (double Width, double Height) UprightSize =>
        Rotation is 90 or 270 ? (Rect.Height, Rect.Width) : (Rect.Width, Rect.Height);

    /// <summary>Equality by identity: two elements that look the same are still two elements.</summary>
    /// <param name="other">The element to compare with.</param>
    /// <returns><see langword="true"/> only for the same instance.</returns>
    public virtual bool Equals(PageElement? other) => ReferenceEquals(this, other);

    /// <summary>Identity hash code, consistent with <see cref="Equals(PageElement?)"/>.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>Text on the page: as a form field (fillable) or fixed in the page content.</summary>
public sealed record TextElement : PageElement
{
    /// <summary>The text; line breaks as "\n".</summary>
    public string Text { get; init; } = "";

    /// <summary>Font family.</summary>
    public ElementFont Font { get; init; } = ElementFont.Sans;

    /// <summary>Font size in points.</summary>
    public double FontSize { get; init; } = 12;

    /// <summary>Colour as 0xRRGGBB.</summary>
    public int Color { get; init; }

    /// <summary>Horizontal alignment.</summary>
    public ElementAlignment Alignment { get; init; } = ElementAlignment.Left;

    /// <summary>Wrap long lines (otherwise only at line breaks in the text).</summary>
    public bool Multiline { get; init; } = true;

    /// <summary>Save as a fillable form field; otherwise fixed in the page content.</summary>
    public bool IsField { get; init; }

    /// <summary>Name of the form field (empty: assigned on saving).</summary>
    public string FieldName { get; init; } = "";

    /// <summary>
    /// Label of a check box or radio button: the field's full name; empty = not a label. The label sits at the height of the
    /// box, moves along when the form is edited and can only be moved horizontally; clicking it checks the box.
    /// </summary>
    public string LabelFor { get; init; } = "";

    /// <summary>For radio buttons: the export value of the button in the group that this label belongs to (otherwise empty).</summary>
    public string LabelState { get; init; } = "";
}

/// <summary>
/// Formatted text: edited text of a page, or a text block with spans of their own formatting. Laid out in the width of the
/// rectangle (reading orientation), paragraphs at "\n"; the height follows the text (the block grows downwards). The first
/// baseline lies <see cref="FirstBaseline"/> points below the top edge, so that edited text sits on the original's baselines.
/// </summary>
/// <remarks>See KONZEPT.md, section 5.14.</remarks>
public sealed record RichTextElement : PageElement
{
    /// <summary>The text with its spans.</summary>
    public required RichText Content { get; init; }

    /// <summary>Alignment of the lines, including justification.</summary>
    public Text.BlockAlignment Alignment { get; init; } = Text.BlockAlignment.Left;

    /// <summary>Distance between baselines as a multiple of the font size (of the largest font in the line).</summary>
    public double LineGap { get; init; } = 1.15;

    /// <summary>Distance of the first baseline from the top edge in points; null = ascender of the first line.</summary>
    public double? FirstBaseline { get; init; }

    /// <summary>Indent in points: positive indents the following lines of a paragraph (bullet list: text below text, not below the bullet), negative the first line.</summary>
    public double HangingIndent { get; init; }

    /// <summary>Tab stops from the left edge (from the original: left, right or centre), ordered by position; beyond them every 1.25 cm as in office programs.</summary>
    public IReadOnlyList<Text.TabStop> TabStops { get; init; } = [];

    /// <summary>
    /// The marked content (MCIDs of the page content) whose text the block now carries, in ascending order: those that lost characters when
    /// the block was made from the page, or — reopened — the one around its form. In a tagged copy the block's text goes into their element
    /// (docs/ABLAUFPLAN.md, 6.6 i); empty: a new block, which gets a paragraph of its own.
    /// </summary>
    public IReadOnlyList<int> ReplacesMcids { get; init; } = [];
}

/// <summary>
/// Signature: a name in a handwriting font. Saved as outlines (vector paths), not as text, so that it can neither be copied as
/// text nor changed, and the font need not be embedded.
/// </summary>
public sealed record SignatureElement : PageElement
{
    /// <summary>Ballpoint blue.</summary>
    public const int InkBlue = 0x1B3A8C;

    /// <summary>The name written.</summary>
    public string Name { get; init; } = "";

    /// <summary>File name of the handwriting font (built in or from "Schriften-Unterschrift", <see cref="Core.Fonts.SignatureFonts"/>).</summary>
    public string FontFile { get; init; } = "";

    /// <summary>Colour as 0xRRGGBB.</summary>
    public int Color { get; init; } = InkBlue;

    /// <summary>With ballpoint texture (an image, ink uneven as with a ballpoint pen); otherwise smooth vector paths, or the font's own colour glyph images if it has them.</summary>
    public bool Textured { get; init; }

    /// <summary>Ink strength of the ballpoint texture (1 = like the reference ink sample, default 1.6; larger = denser and more saturated, never darker than <see cref="Color"/>).</summary>
    public double InkStrength { get; init; } = Imaging.InkColor.DefaultStrength;
}

/// <summary>
/// Freehand drawing (mouse, pen, finger): strokes as point sequences in a space of their own (y downwards, as when drawing; any
/// unit), fitted into the rectangle. Saved as paths with round caps — as vectors, sharp at any size.
/// </summary>
public sealed record InkElement : PageElement
{
    /// <summary>Per stroke, alternating x and y.</summary>
    public IReadOnlyList<float[]> Strokes { get; init; } = [];

    /// <summary>Stroke width in the space of the points.</summary>
    public double StrokeWidth { get; init; } = 3;

    /// <summary>Colour as 0xRRGGBB.</summary>
    public int Color { get; init; } = SignatureElement.InkBlue;

    /// <summary>With ballpoint texture (an image); otherwise smooth vector paths.</summary>
    public bool Textured { get; init; }

    /// <summary>Ink strength of the ballpoint texture (1 = like the reference ink sample, default 1.6; larger = denser and more saturated, never darker than <see cref="Color"/>).</summary>
    public double InkStrength { get; init; } = Imaging.InkColor.DefaultStrength;
}

/// <summary>Image on the page (photo, scanned signature, stamp), with transparency.</summary>
public sealed record ImageElement : PageElement
{
    /// <summary>The pixels.</summary>
    public required Imaging.RgbaImage Image { get; init; }

    /// <summary>Photo or scan of a signature (from the signature window): written permanently into the page like any signature.</summary>
    public bool IsSignature { get; init; }

    /// <summary>
    /// Aspect lock closed (see KONZEPT.md, section 5.13): the aspect ratio stays — the handles change width and height together
    /// and the image fills the rectangle. Open: width and height are free and the image is stretched.
    /// </summary>
    public bool KeepAspect { get; init; } = true;

    /// <summary>Opacity 0 to 1.</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>Sharpening from 0 (off) to 1 (strong); applied after downsampling to the saved resolution (and in the display).</summary>
    public double Sharpness { get; init; }

    /// <summary>
    /// What screen readers read for the image (/Contents of its annotation; docs/ABLAUFPLAN.md, 6.4 step 3); empty: a neutral word in the
    /// document's language (<see cref="Accessibility.FixedTexts"/>).
    /// </summary>
    public string AltText { get; init; } = "";
}

/// <summary>Kind of shape (see KONZEPT.md, section 5.13).</summary>
public enum ShapeKind
{
    /// <summary>Rectangle.</summary>
    Rectangle,
    /// <summary>Ellipse.</summary>
    Ellipse,
    /// <summary>Straight line.</summary>
    Line,
    /// <summary>Line with an arrowhead at its end.</summary>
    Arrow,
    /// <summary>Triangle.</summary>
    Triangle,
    /// <summary>Diamond.</summary>
    Diamond,
    /// <summary>Five-pointed star.</summary>
    Star,
    /// <summary>Speech bubble.</summary>
    Callout,
}

/// <summary>
/// Shape: rectangle, ellipse, line, arrow, triangle, diamond, star or speech bubble, with border and fill as in office programs. Saved as
/// a PDF annotation (Square, Circle, Line, Polygon) with a finished appearance — editable in other programs as well, and an
/// element again in PDF Knead. Line and arrow: end points relative to the rectangle (0 to 1, in reading orientation, y
/// upwards), so that they follow size changes.
/// </summary>
public sealed record ShapeElement : PageElement
{
    /// <summary>Kind of shape.</summary>
    public ShapeKind Kind { get; init; } = ShapeKind.Rectangle;

    /// <summary>Border or line colour as 0xRRGGBB; null = no border (not for line and arrow).</summary>
    public int? StrokeColor { get; init; } = 0x1F4E9A;

    /// <summary>Fill colour as 0xRRGGBB; null = no fill (meaningless for line and arrow).</summary>
    public int? FillColor { get; init; }

    /// <summary>Border or line width in points.</summary>
    public double StrokeWidth { get; init; } = 2;

    /// <summary>Dashed instead of solid.</summary>
    public bool Dashed { get; init; }

    /// <summary>Opacity 0 to 1 (border and fill).</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>Line and arrow: start (x, y) relative to the rectangle; the arrow points towards the end.</summary>
    public (double X, double Y) Start { get; init; } = (0, 0);

    /// <summary>Line and arrow: end (x, y) relative to the rectangle.</summary>
    public (double X, double Y) End { get; init; } = (1, 1);

    /// <summary>Line or arrow (end points instead of an outline).</summary>
    public bool IsLine => Kind is ShapeKind.Line or ShapeKind.Arrow;
}

/// <summary>
/// A measurement saved as a dimension (Ablaufplan 7.6 f; docs/FEHLERGEDAECHTNIS.md, No. 587): a length as a line dimension, a polyline and an
/// arc as a polyline dimension, an area as a polygon dimension, each with the scale it was measured with (/Measure, ISO 32000-2, 12.5.6.7,
/// 12.5.6.9, 12.9) and its value as caption and contents; an angle as a polyline without a scale. Other programs show it and measure the
/// same; in PDF Knead an element again — it can be moved and deleted, not stretched (its points are its measurement). Points relative to the
/// lower left corner of <see cref="PageElement.Rect"/> (page space), so that moving takes them along.
/// </summary>
public sealed record DimensionElement : PageElement
{
    /// <summary>What was measured.</summary>
    public required MeasureKind Kind { get; init; }

    /// <summary>The points measured, relative to the lower left corner of the rectangle.</summary>
    public required IReadOnlyList<(double X, double Y)> Points { get; init; }

    /// <summary>Of an arc: the arc finely divided (relative); empty otherwise.</summary>
    public IReadOnlyList<(double X, double Y)> Path { get; init; } = [];

    /// <summary>Of an arc: its centre (relative); null otherwise.</summary>
    public (double X, double Y)? Centre { get; init; }

    /// <summary>The value as shown („5 m“, „12 m²; 14 m“, „r 2.5 m; ⌀ 5 m“, „30°“).</summary>
    public required string Text { get; init; }

    /// <summary>More about it (of an arc its length and angle); empty otherwise.</summary>
    public string Detail { get; init; } = "";

    /// <summary>The length of each piece as text (polyline, area).</summary>
    public IReadOnlyList<string> Segments { get; init; } = [];

    /// <summary>The angle at each point as text (polyline, area).</summary>
    public IReadOnlyList<string> Corners { get; init; } = [];

    /// <summary>
    /// Of a polyline or area its arc points (Ablaufplan 7.6 j): each makes the piece from the point before to the point after an arc through it
    /// (<see cref="MeasureGeometry"/>); empty: all pieces straight.
    /// </summary>
    public IReadOnlyList<int> ArcPoints { get; init; } = [];

    /// <summary>Colour as 0xRRGGBB.</summary>
    public int Color { get; init; } = 0xEA580C;

    /// <summary>Line width in points.</summary>
    public double StrokeWidth { get; init; } = 1.5;

    /// <summary>How strongly an area is filled (0–1).</summary>
    public double FillOpacity { get; init; } = 0.2;

    /// <summary>Whether the length of each piece is shown at it.</summary>
    public bool ShowSegments { get; init; }

    /// <summary>Whether the angle at each bend is shown.</summary>
    public bool ShowAngles { get; init; }

    /// <summary>The scale it was measured with (/Measure); null for an angle.</summary>
    public RectilinearMeasure? Measure { get; init; }

    /// <summary>The rotation of the page when it was made (0/90/180/270): its labels stand upright as the page is shown.</summary>
    public int PageRotation { get; init; }

    /// <summary>
    /// Where the user moved the label of its value to (its middle, relative to the lower left corner of the rectangle; design decision,
    /// docs/FEHLERGEDAECHTNIS.md, No. 703); null: its own place.
    /// </summary>
    public (double X, double Y)? LabelAt { get; init; }

    /// <summary>The label's place in page space (<see cref="LabelAt"/>); null: its own place.</summary>
    public (double X, double Y)? PageLabelAt => LabelAt is { } l ? (Rect.Left + l.X, Rect.Bottom + l.Y) : null;

    /// <summary>The points in page space.</summary>
    public IReadOnlyList<(double X, double Y)> PagePoints => [.. Points.Select(p => (Rect.Left + p.X, Rect.Bottom + p.Y))];

    /// <summary>The arc in page space (empty if none).</summary>
    public IReadOnlyList<(double X, double Y)> PagePath => [.. Path.Select(p => (Rect.Left + p.X, Rect.Bottom + p.Y))];
}

/// <summary>
/// A note (KONZEPT.md, section 5.5a): a speech bubble on the page with a text that is read in the area „Notizen“, not printed on the page.
/// Saved as a note annotation (12.5.6.4, /Contents, /T, /M) with the bubble as its appearance — other readers show and open it — and an element
/// again in PDF Knead.
/// </summary>
public sealed record NoteElement : PageElement
{
    /// <summary>Side of the bubble in points.</summary>
    public const double Size = 20;

    /// <summary>The text of the note.</summary>
    public string Text { get; init; } = "";

    /// <summary>Who wrote it; empty if not given.</summary>
    public string Author { get; init; } = "";

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset Modified { get; init; } = DateTimeOffset.Now;

    /// <summary>Colour of the bubble as 0xRRGGBB (light yellow).</summary>
    public int Color { get; init; } = 0xFFF1A8;
}

/// <summary>
/// QR, Data Matrix or bar code placed on the page (KONZEPT.md, section 5.19), drawn as vector graphics from its content: 2D symbols
/// square and centred in the rectangle, bar codes across its width with the human-readable text below. Saved as an annotation with a
/// finished appearance — other readers show it — and an element again in PDF Knead.
/// </summary>
public sealed record CodeElement : PageElement
{
    /// <summary>The kind.</summary>
    public Barcodes.BarcodeFormat Format { get; init; } = Barcodes.BarcodeFormat.Qr;

    /// <summary>The content (GS1 data with identifiers in brackets; EAN/UPC digits with or without check digit).</summary>
    public string Text { get; init; } = "";

    /// <summary>QR and Data Matrix: the content is GS1 data (GS1-128 always is).</summary>
    public bool Gs1 { get; init; }

    /// <summary>QR: error correction level.</summary>
    public Barcodes.Qr.QrErrorCorrection Level { get; init; } = Barcodes.Qr.QrErrorCorrection.M;

    /// <summary>Data Matrix: square or rectangular symbol.</summary>
    public Barcodes.DataMatrix.DataMatrixShape Shape { get; init; } = Barcodes.DataMatrix.DataMatrixShape.Square;

    /// <summary>Colour of modules, bars and text as 0xRRGGBB.</summary>
    public int Color { get; init; }

    /// <summary>Background of the whole rectangle as 0xRRGGBB; null = transparent.</summary>
    public int? Background { get; init; } = 0xFFFFFF;

    /// <summary>Leave the quiet zone every kind needs around the code (inside the rectangle).</summary>
    public bool QuietZone { get; init; } = true;

    /// <summary>Bar codes: the human-readable text below the bars.</summary>
    public bool Caption { get; init; } = true;

    /// <summary>Alternative text for screen readers; empty = the kind and the content.</summary>
    public string AltText { get; init; } = "";

    /// <summary>
    /// The content of the code of the page this one replaced (as typed: GS1 in brackets); null for an inserted code. Only for the hint on
    /// text beside it that may still show the old content — not saved.
    /// </summary>
    public string? Replaces { get; init; }

    /// <summary>Whether it is a QR or Data Matrix symbol (kept square).</summary>
    public bool IsMatrix => Barcodes.CodeSymbol.IsMatrix(Format);
}

/// <summary>
/// Redaction: a marked area. Until it is applied it is only a mark (red outline, editable, saved as a Redact annotation); when
/// it is applied, the content underneath is removed and the area becomes black.
/// </summary>
public sealed record RedactionElement : PageElement
{
    /// <summary>Partial areas relative to the rectangle (0 to 1), e.g. one per text line; empty = the whole rectangle.</summary>
    public IReadOnlyList<PdfRectangle> Areas { get; init; } = [];

    /// <summary>
    /// Marks hidden content („Verborgenes schwärzen“, KONZEPT.md, section 5.18): applying removes only the characters nobody sees in
    /// the areas (<see cref="Redactor.RemoveHiddenIn"/>), without a bar; whatever is visible there stays.
    /// </summary>
    public bool Hidden { get; init; }

    /// <summary>
    /// Why it is redacted (KONZEPT.md, section 5.6, „Schwärzungsgründe“), e.g. „Personenbezogene Daten (Art. 6 DSGVO)“; null: the reason for
    /// the whole document given when applying applies. Never anything of the content underneath.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The redaction code of the reason („§ 5 IFG“, <see cref="RedactionReasonCode"/>; wish of 4.11.2026): written into the bar instead of the whole
    /// reason; null: an own reason, then the reason itself goes into the bar.
    /// </summary>
    public string? ReasonCode { get; init; }

    /// <summary>The legal basis of the code („Informationsfreiheitsgesetz des Bundes (IFG), § 5“), for the protocol; null: none.</summary>
    public string? ReasonBasis { get; init; }

    /// <summary>Whether the code (or the own reason) is written into the bar; set when marking (wish of 4.11.2026).</summary>
    public bool ReasonInBar { get; init; } = true;

    /// <summary>What goes into the bar: the code, else the reason; null if nothing (no reason, or not in the bar).</summary>
    public string? BarText => ReasonInBar ? ReasonCode ?? Reason : null;

    /// <summary>The areas in page space (they follow the rectangle when it is moved or resized).</summary>
    public IEnumerable<PdfRectangle> PageAreas => Areas.Count == 0
        ? [Rect]
        : Areas.Select(a => new PdfRectangle(Rect.Left + a.Left * Rect.Width, Rect.Bottom + a.Bottom * Rect.Height,
            Rect.Left + a.Right * Rect.Width, Rect.Bottom + a.Top * Rect.Height));

    /// <summary>
    /// Creates a redaction over areas in page space (such as the lines of a selected text). Areas without width or height cover nothing and
    /// are left out. The others are kept relative to their bounding rectangle; where that cannot hold them as they are — one far away (a
    /// character of a damaged file at 10^19 points) made the rectangle so large that the others were lost in rounding, and the mark covered
    /// nothing — the whole rectangle is marked: it covers more, never less, and shows so before it is applied (docs/FEHLERGEDAECHTNIS.md,
    /// No. 390).
    /// </summary>
    /// <param name="areas">The areas in page space; at least one.</param>
    /// <returns>The redaction with the bounding rectangle and, for several areas, their relative positions.</returns>
    /// <exception cref="ArgumentException"><paramref name="areas"/> is empty.</exception>
    public static RedactionElement FromPageAreas(IReadOnlyList<PdfRectangle> areas)
    {
        if (areas.Count == 0) throw new ArgumentException("Keine Fläche", nameof(areas));
        var covering = areas.Where(a => a.Width > 0 && a.Height > 0 && double.IsFinite(a.Width) && double.IsFinite(a.Height)).ToList();
        if (covering.Count > 0) areas = covering;
        var rect = new PdfRectangle(areas.Min(a => a.Left), areas.Min(a => a.Bottom), areas.Max(a => a.Right), areas.Max(a => a.Top));
        if (areas.Count == 1) return new RedactionElement { Rect = rect };
        double w = Math.Max(rect.Width, 1e-6), h = Math.Max(rect.Height, 1e-6);
        var mark = new RedactionElement
        {
            Rect = rect,
            Areas = [.. areas.Select(a => new PdfRectangle((a.Left - rect.Left) / w, (a.Bottom - rect.Bottom) / h, (a.Right - rect.Left) / w, (a.Top - rect.Bottom) / h))],
        };
        static bool Same(PdfRectangle a, PdfRectangle b) =>
            Math.Abs(a.Left - b.Left) <= AreaTolerance && Math.Abs(a.Bottom - b.Bottom) <= AreaTolerance &&
            Math.Abs(a.Right - b.Right) <= AreaTolerance && Math.Abs(a.Top - b.Top) <= AreaTolerance;
        return mark.PageAreas.Zip(areas).All(p => Same(p.First, p.Second)) ? mark : new RedactionElement { Rect = rect };
    }

    /// <summary>How far an area may come back from its relative position (points).</summary>
    private const double AreaTolerance = 0.001;
}

/// <summary>
/// Empty signature field (form field /Sig): the place where a signature belongs. Other programs offer signing there; PDF Knead
/// places a signature into it with one click.
/// </summary>
public sealed record SignatureFieldElement : PageElement
{
    /// <summary>Name of the form field (empty: assigned on saving).</summary>
    public string FieldName { get; init; } = "";

    /// <summary>Label in small type at the bottom left of the field (empty: none).</summary>
    public string Label { get; init; } = "";
}

/// <summary>
/// Annotation or form field from another program (stamp, free text, comment, drawing, highlight, field …) that can be
/// selected, moved and deleted. The appearance stays as the other program drew it; on saving, the annotation is moved
/// (<c>/Rect</c> and points) or removed together with its popup and replies.
/// </summary>
public sealed record ForeignAnnotation : PageElement
{
    /// <summary>Document the annotation comes from (references, decryption).</summary>
    public required PdfDocument Document { get; init; }

    /// <summary>The annotation in the source document (unchanged; the new position is in <see cref="PageElement.Rect"/>).</summary>
    public required Objects.PdfDictionary Original { get; init; }

    /// <summary>Original position (<c>/Rect</c>).</summary>
    public required PdfRectangle OriginalRect { get; init; }

    /// <summary>Kind (<c>/Subtype</c>), e.g. Stamp, FreeText, Ink, Widget.</summary>
    public string Kind { get; init; } = "";

    /// <summary>Offset from the original position (points).</summary>
    public (double X, double Y) Offset => (Rect.Left - OriginalRect.Left, Rect.Bottom - OriginalRect.Bottom);

    /// <summary>Moved (written with the new position on saving).</summary>
    public bool IsMoved => Math.Abs(Offset.X) > 1e-6 || Math.Abs(Offset.Y) > 1e-6;

    /// <summary>
    /// The author locked it (/F bit 8, value 128): not to be deleted, its properties — position and size included — not to be changed; its
    /// contents or field value may change (ISO 32000-2, 12.5.3, Table 167; docs/FEHLERGEDAECHTNIS.md, No. 541).
    /// </summary>
    public bool IsLocked => ((Original.GetInteger(Objects.PdfName.Get("F")) ?? 0) & 128) != 0;

    /// <summary>Its contents are locked (/F bit 10, value 512): deleting and moving allowed, the contents not to be changed (Table 167).</summary>
    public bool IsContentLocked => ((Original.GetInteger(Objects.PdfName.Get("F")) ?? 0) & 512) != 0;

    /// <summary>
    /// Filled in: new value of the form field — text or export value of the selected option; for check boxes and radio buttons
    /// the state of this widget (its on state or "Off"). Null = unchanged.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// Changed properties of a form field; null = unchanged. With changed properties the field gets a new appearance on saving,
    /// which is why it may also change its size.
    /// </summary>
    public FieldSpec? Spec { get; init; }

    /// <summary>Size changed compared with the file (only for fields with <see cref="Spec"/>).</summary>
    public bool IsResized => Math.Abs(Rect.Width - OriginalRect.Width) > 1e-6 || Math.Abs(Rect.Height - OriginalRect.Height) > 1e-6;

    /// <summary>Filled in, moved, resized or with changed properties: different from the file (the display draws it above the tiles).</summary>
    public bool IsChanged => IsMoved || Value is not null || Spec is not null || IsResized;
}

/// <summary>
/// New form field: text field (also with a format, such as a date), check box, radio button, combo box, list box or a reset
/// button. Saved as an ordinary field with an appearance; on the next opening it is a field like those of other programs
/// (<see cref="ForeignAnnotation"/> with <see cref="ForeignAnnotation.Spec"/>). Radio buttons with the same name form a group.
/// </summary>
public sealed record FieldElement : PageElement
{
    /// <summary>The field properties.</summary>
    public required FieldSpec Spec { get; init; }

    /// <summary>Value: text or export value of the selected option; for check boxes and radio buttons the state of this widget (export value or "Off").</summary>
    public string Value { get; init; } = "";
}
