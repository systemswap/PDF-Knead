using System.Runtime.CompilerServices;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Elements that stay editable after saving: texts, text fields, images, shapes, redaction marks and signature fields are
/// stored in the file as annotations with a finished appearance (every program shows and prints them) and an entry
/// <c>/PDFK_Element</c> with their settings. On opening they become elements again — movable, changeable, deletable —;
/// only flattening writes them permanently into the page. Signatures are always fixed, because otherwise any program could
/// move them or copy them out.
/// </summary>
public static class ElementStore
{
    /// <summary>Annotation entry holding the kind and settings of the element.</summary>
    public static readonly PdfName Key = PdfName.Get("PDFK_Element");

    /// <summary>
    /// The name of <see cref="Key"/> in files of earlier versions — not a second-class name as ISO 32000-2, Annex E.2 requires for a developer's
    /// own keys (four characters, then „_“; docs/FEHLERGEDAECHTNIS.md, No. 599); still read, never written.
    /// </summary>
    public static readonly PdfName LegacyKey = PdfName.Get("PDFKneadElement");

    /// <summary>Catalog entry marking a file that contains such annotations; without it, no search takes place.</summary>
    public static readonly PdfName CatalogKey = PdfName.Get("PDFK_Elements");

    /// <summary>The name of <see cref="CatalogKey"/> in files of earlier versions (No. 599); still read, never written.</summary>
    public static readonly PdfName LegacyCatalogKey = PdfName.Get("PDFKneadElements");

    /// <summary>The own data of an annotation, form or widget — under <see cref="Key"/>, or under the earlier name.</summary>
    public static PdfDictionary? DataOf(PdfDictionary dict) => dict.GetDictionary(Key) ?? dict.GetDictionary(LegacyKey);

    /// <summary>Whether an annotation carries own data (under either name).</summary>
    public static bool HasData(PdfDictionary dict) => dict.ContainsKey(Key) || dict.ContainsKey(LegacyKey);

    /// <summary>Whether the catalog marks the file as one with own elements (under either name).</summary>
    public static bool IsMarked(PdfDictionary? catalog) => catalog?.GetBoolean(CatalogKey) == true || catalog?.GetBoolean(LegacyCatalogKey) == true;

    private static readonly PdfName Art = PdfName.Get("Art"), TextKey = PdfName.Get("Text"), Schrift = PdfName.Get("Schrift"),
        Groesse = PdfName.Get("Groesse"), Farbe = PdfName.Get("Farbe"), Ausrichtung = PdfName.Get("Ausrichtung"),
        Umbruch = PdfName.Get("Umbruch"), Drehung = PdfName.Get("Drehung"), Bild = PdfName.Get("Bild"), ZeilenhoeheSchrift = PdfName.Get("ZeilenhoeheSchrift"),
        Beschriftung = PdfName.Get("Beschriftung"), Bereiche = PdfName.Get("Bereiche"), Rect = PdfName.Get("Rect"), V = PdfName.Get("V"), T = PdfName.Get("T"),
        Annots = PdfName.Get("Annots"), Parent = PdfName.Get("Parent"), Fuer = PdfName.Get("Fuer"), FuerZustand = PdfName.Get("FuerZustand"),
        Formart = PdfName.Get("Formart"), Rand = PdfName.Get("Rand"), Fuellung = PdfName.Get("Fuellung"), Breite = PdfName.Get("Breite"),
        Gestrichelt = PdfName.Get("Gestrichelt"), Deckkraft = PdfName.Get("Deckkraft"), Anfang = PdfName.Get("Anfang"), Ende = PdfName.Get("Ende"),
        Kette = PdfName.Get("Kette"), Verborgen = PdfName.Get("Verborgen"), Grund = PdfName.Get("Grund"), GrundCode = PdfName.Get("GrundCode"), GrundGrundlage = PdfName.Get("GrundGrundlage"),
        GrundNichtImBalken = PdfName.Get("GrundNichtImBalken"), Inhalt = PdfName.Get("Inhalt"),
        Alt = PdfName.Get("Alt"), Autor = PdfName.Get("Autor"), Datum = PdfName.Get("Datum"),
        Messart = PdfName.Get("Messart"), Punkte = PdfName.Get("Punkte"), Bogen = PdfName.Get("Bogen"), Mitte = PdfName.Get("Mitte"),
        Bogenpunkte = PdfName.Get("Bogenpunkte"),
        Wert = PdfName.Get("Wert"), Mehr = PdfName.Get("Mehr"), Abschnitte = PdfName.Get("Abschnitte"), Knickwinkel = PdfName.Get("Knickwinkel"),
        Masse = PdfName.Get("Masse"), WinkelZeigen = PdfName.Get("WinkelZeigen"), Seitendrehung = PdfName.Get("Seitendrehung"),
        Wertlage = PdfName.Get("Wertlage");

    /// <summary>Most points of a dimension read back (its arc is divided every 2°, so a few hundred at most).</summary>
    private const int MaxDimensionPoints = 10_000;

    /// <summary>At most this many characters of a note are read back (a crafted file cannot make the list unusable).</summary>
    private const int MaxNoteText = 100_000;

    /// <summary>
    /// Determines whether an element stays editable after saving (as an annotation): everything except signatures, sealed and
    /// digitally signed elements; annotations of other programs remain annotations anyway.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns><see langword="true"/> if the element is saved as an editable annotation.</returns>
    public static bool StaysEditable(PageElement element) =>
        element.Seal is null && element.Digital is null && element is TextElement or SignatureFieldElement or FieldElement or ImageElement { IsSignature: false } or RedactionElement or ShapeElement or CodeElement or NoteElement or DimensionElement or ForeignAnnotation;

    /// <summary>Elements of a page and the annotations they come from.</summary>
    public sealed record Found(IReadOnlyList<PageElement> Elements, IReadOnlySet<PdfDictionary> Annotations);

    private static readonly Found None = new([], new HashSet<PdfDictionary>());
    private static readonly ConditionalWeakTable<PdfDictionary, Found> Pages = new();
    private static readonly ConditionalWeakTable<PdfDocument, object> Marked = new();

    /// <summary>Gets the page's own elements, read once per page.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <returns>The elements and their annotations; empty for files without the catalog marker.</returns>
    public static Found For(PdfDocument doc, PdfPage page)
    {
        if (!HasElements(doc)) return None;
        return Pages.GetValue(page.Dictionary, _ => Read(doc, page));
    }

    /// <summary>Determines whether an annotation is a readable own element of the page.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="annotation">The annotation dictionary.</param>
    /// <returns><see langword="true"/> if the annotation was read as an element of this page.</returns>
    public static bool IsOwn(PdfDocument doc, PdfPage page, PdfDictionary annotation) =>
        HasElements(doc) && For(doc, page).Annotations.Contains(annotation);

    private static bool HasElements(PdfDocument doc) =>
        Marked.GetValue(doc, d => IsMarked(d.Catalog)) is true;

    private static Found Read(PdfDocument doc, PdfPage page)
    {
        if (page.Dictionary.Get(Annots) is not PdfArray annots) return None;
        var elements = new List<PageElement>();
        var owned = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < annots.Count; i++)
        {
            if (annots.Get(i) is not PdfDictionary annot || DataOf(annot) is not { } data) continue;
            try
            {
                if (ReadOne(doc, annot, data) is not { } element) continue;   // unreadable: stays an ordinary annotation
                elements.Add(element);
                owned.Add(annot);
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or IOException or ArgumentException)
            {
            }
        }
        return elements.Count == 0 ? None : new Found(elements, owned);
    }

    private static PageElement? ReadOne(PdfDocument doc, PdfDictionary annot, PdfDictionary data)
    {
        if (PdfRectangle.From(annot.Get(Rect)) is not { IsEmpty: false } rect) return null;
        // Do not accept absurd sizes from a crafted file (infinite, millions of points) as an editable element
        if (!IsSane(rect)) return null;
        var rotation = (int)(data.GetInteger(Drehung) ?? 0) is var r && r is 0 or 90 or 180 or 270 ? r : 0;
        switch (data.GetName(Art)?.Value)
        {
            case "Text" or "Textfeld":
                var isField = data.GetName(Art)!.Value == "Textfeld";
                // Field: the current value (another program may have filled it in) and the field name
                var text = isField && Field(annot, V) is PdfString value ? value.ToText() : data.GetString(TextKey)?.ToText() ?? "";
                return new TextElement
                {
                    Rect = rect,
                    Rotation = rotation,
                    Text = text.Replace("\r\n", "\n").Replace('\r', '\n'),
                    Font = Enum.TryParse<ElementFont>(data.GetName(Schrift)?.Value, out var font) && Enum.IsDefined(font) ? font : ElementFont.Sans,   // "4711" also parses as an enum value
                    FontSize = Math.Clamp(data.GetNumber(Groesse) ?? 12, 1, 500),
                    Color = (int)((data.GetInteger(Farbe) ?? 0) & 0xFFFFFF),
                    Alignment = (data.GetInteger(Ausrichtung) ?? 0) switch { 1 => ElementAlignment.Center, 2 => ElementAlignment.Right, _ => ElementAlignment.Left },
                    Multiline = data.GetBoolean(Umbruch) ?? true,
                    IsField = isField,
                    // the mark of No. 1211: lines spaced by the line height of the font; text of earlier versions has none and keeps 1.15 times the size
                    FontLineHeight = !isField && data.GetBoolean(ZeilenhoeheSchrift) == true,
                    FieldName = isField ? (Field(annot, T) as PdfString)?.ToText() ?? "" : "",
                    LabelFor = isField ? "" : data.GetString(Fuer)?.ToText() ?? "",
                    LabelState = isField ? "" : data.GetString(FuerZustand)?.ToText() ?? "",
                };
            case "Unterschriftsfeld":
                // signed meanwhile: no longer an empty field to move or delete (that would break the signature), but the file's signature
                if (Field(annot, V) is PdfDictionary) return null;
                return new SignatureFieldElement
                {
                    Rect = rect,
                    Rotation = rotation,
                    Label = data.GetString(Beschriftung)?.ToText() ?? "",
                    FieldName = (Field(annot, T) as PdfString)?.ToText() ?? "",
                };
            case "Schwaerzung":
                // Partial areas relative to the rectangle (0 to 1); without them the whole rectangle applies
                var areas = new List<PdfRectangle>();
                if (data.GetArray(Bereiche) is { } list)
                    for (var i = 0; i < list.Count && i < 10_000; i++)
                        if (PdfRectangle.From(list.Get(i)) is { } a && UnitArea(a) is { } unit) areas.Add(unit);
                return new RedactionElement
                {
                    Rect = rect, Areas = areas, Hidden = data.GetBoolean(Verborgen) ?? false,
                    Reason = data.GetString(Grund)?.ToText() is { Length: > 0 and <= 500 } reason ? reason : null,
                    ReasonCode = RedactionReasons.Clean(data.GetString(GrundCode)?.ToText()),
                    ReasonBasis = RedactionReasons.Clean(data.GetString(GrundGrundlage)?.ToText()),
                    ReasonInBar = data.GetBoolean(GrundNichtImBalken) != true,
                };
            case "Bild" when data.GetStream(Bild) is { } stream && ReadImage(doc, stream) is { } image:
                // Sharpening is already in the saved image (otherwise the next save would sharpen it again)
                return new ImageElement
                {
                    Rect = rect, Rotation = rotation, Image = image, KeepAspect = data.GetBoolean(Kette) ?? true,
                    Opacity = Math.Clamp(data.GetNumber(Deckkraft) ?? 1, 0.05, 1),
                    AltText = data.GetString(Alt)?.ToText() is { Length: <= MaxCodeText } imageAlt ? imageAlt : "",
                };
            case "Form" when Enum.TryParse<ShapeKind>(data.GetName(Formart)?.Value, out var kind) && Enum.IsDefined(kind):
            {
                int? Color(PdfName key) => data.GetInteger(key) is { } c ? (int)(c & 0xFFFFFF) : null;
                (double, double) Point(PdfName key, (double, double) fallback) =>
                    data.GetArray(key) is { Count: 2 } p && p.GetNumber(0) is { } x && p.GetNumber(1) is { } y && double.IsFinite(x) && double.IsFinite(y)
                        ? (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1))
                        : fallback;
                var shape = new ShapeElement
                {
                    Rect = rect,
                    Rotation = rotation,
                    Kind = kind,
                    StrokeColor = Color(Rand),
                    FillColor = Color(Fuellung),
                    StrokeWidth = Math.Clamp(data.GetNumber(Breite) ?? 2, 0, 100),
                    Dashed = data.GetBoolean(Gestrichelt) ?? false,
                    Opacity = Math.Clamp(data.GetNumber(Deckkraft) ?? 1, 0.05, 1),
                    Start = Point(Anfang, (0, 0)),
                    End = Point(Ende, (1, 1)),
                };
                // A line without colour would be invisible and could no longer be picked
                return shape.IsLine && shape.StrokeColor is null ? shape with { StrokeColor = 0 } : shape;
            }
            case "Code" when CodeLook.Read(data) is { } look && data.GetString(Inhalt)?.ToText() is { Length: <= MaxCodeText } content:
                return look.Apply(new CodeElement
                {
                    Rect = rect,
                    Rotation = rotation,
                    Text = content,
                    AltText = data.GetString(Alt)?.ToText() is { Length: <= MaxCodeText } alt ? alt : "",
                });
            case "Notiz":
                return new NoteElement
                {
                    Rect = rect,
                    Rotation = rotation,
                    Text = data.GetString(Inhalt)?.ToText() is { } note ? note.Length > MaxNoteText ? note[..MaxNoteText] : note : "",
                    Author = data.GetString(Autor)?.ToText() is { Length: <= 200 } author ? author : "",
                    Modified = PdfDate.Parse(data.GetString(Datum)?.ToText()) ?? DateTimeOffset.Now,
                    Color = data.GetInteger(Farbe) is { } color ? (int)(color & 0xFFFFFF) : 0xFFF1A8,
                };
            case "Bemassung" when Enum.TryParse<MeasureKind>(data.GetName(Messart)?.Value, out var measured) && Enum.IsDefined(measured)
                                  && PointList(data.GetArray(Punkte)) is { Count: >= 2 } points && data.GetString(Wert)?.ToText() is { Length: > 0 and <= 1000 } shown:
            {
                // a dimension (Ablaufplan 7.6 f): points relative to the rectangle; its scale is the /Measure of the annotation
                IReadOnlyList<string> Texts(PdfName key)
                {
                    var list = new List<string>();
                    if (data.GetArray(key) is { } array)
                        for (var i = 0; i < array.Count && i < MaxDimensionPoints; i++) list.Add(array.Get(i) is PdfString t && t.ToText() is { Length: <= 1000 } s ? s : "");
                    return list;
                }
                var centre = PointList(data.GetArray(Mitte)) is { Count: 1 } c ? c[0] : ((double, double)?)null;
                var measure = measured == MeasureKind.Angle ? null : RectilinearMeasure.Read(annot.GetDictionary(PdfName.Get("Measure")));
                // the arc points of a polyline or area (Ablaufplan 7.6 j): only those that can be
                var closed = measured == MeasureKind.Area;
                IReadOnlyList<int> arcPoints = [];
                if (measured is MeasureKind.Polyline or MeasureKind.Area && data.GetArray(Bogenpunkte) is { } marked)
                {
                    var indices = new List<int>();
                    for (var i = 0; i < marked.Count && i < MaxDimensionPoints; i++)
                        if (marked.Get(i) is PdfInteger { Value: >= 0 and < MaxDimensionPoints } index) indices.Add((int)index.Value);
                    arcPoints = MeasureGeometry.ValidArcPoints(points.Count, indices, closed);
                }
                // pieces and angles only as many as the shape has (a crafted list would not fit; No. 596)
                var pieces = Texts(Abschnitte);
                var bends = Texts(Knickwinkel);
                var pieceCount = measured is MeasureKind.Polyline or MeasureKind.Area
                    ? MeasureGeometry.Build(points, arcPoints, closed, measure ?? Measuring.Paper)?.Pieces.Count ?? -1
                    : points.Count - 1;
                if (pieces.Count != pieceCount) pieces = [];
                if (bends.Count != points.Count) bends = [];
                // the points of the file count before the own data: another program may have moved them (Ablaufplan 7.6 h, No. 605)
                return Dimensions.Reconcile(annot, new DimensionElement
                {
                    Rect = rect,
                    Kind = measured,
                    Points = points,
                    Path = PointList(data.GetArray(Bogen)) ?? [],
                    Centre = centre,
                    Text = shown,
                    Detail = data.GetString(Mehr)?.ToText() is { Length: <= 1000 } more ? more : "",
                    Segments = pieces,
                    Corners = bends,
                    ArcPoints = arcPoints,
                    Color = (int)((data.GetInteger(Farbe) ?? 0xEA580C) & 0xFFFFFF),
                    StrokeWidth = Math.Clamp(data.GetNumber(Breite) ?? 1.5, 0.25, 12),
                    FillOpacity = Math.Clamp(data.GetNumber(Deckkraft) ?? 0.2, 0, 1),
                    ShowSegments = data.GetBoolean(Masse) ?? false,
                    ShowAngles = data.GetBoolean(WinkelZeigen) ?? false,
                    Measure = measure,
                    PageRotation = (int)(data.GetInteger(Seitendrehung) ?? 0) is var turn && turn is 0 or 90 or 180 or 270 ? turn : 0,
                    LabelAt = PointList(data.GetArray(Wertlage)) is [var label] ? label : null,
                });
            }
            default:
                return null;
        }
    }

    /// <summary>Points as a flat array of numbers (x y x y …), finite and of reasonable size; null if it is not one.</summary>
    private static List<(double X, double Y)>? PointList(PdfArray? array)
    {
        if (array is null || array.Count % 2 != 0 || array.Count > 2 * MaxDimensionPoints) return null;
        var points = new List<(double X, double Y)>(array.Count / 2);
        for (var i = 0; i + 1 < array.Count; i += 2)
        {
            if (array.GetNumber(i) is not { } x || array.GetNumber(i + 1) is not { } y || !double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1e6 || Math.Abs(y) > 1e6)
                return null;
            points.Add((x, y));
        }
        return points;
    }

    /// <summary>Points as a flat array of numbers, rounded to a thousandth of a point.</summary>
    private static PdfArray PointArray(IEnumerable<(double X, double Y)> points)
    {
        var array = new PdfArray();
        foreach (var (x, y) in points)
        {
            array.Add(new PdfReal(Math.Round(x, 3)));
            array.Add(new PdfReal(Math.Round(y, 3)));
        }
        return array;
    }

    /// <summary>Longest content and alternative text of a code read back (a QR code holds at most 7 089 characters).</summary>
    private const int MaxCodeText = 10_000;

    /// <summary>Finite coordinates of reasonable size (a page has at most 200 inches, more with /UserUnit).</summary>
    private static bool IsSane(PdfRectangle r) =>
        double.IsFinite(r.Left) && double.IsFinite(r.Bottom) && double.IsFinite(r.Right) && double.IsFinite(r.Top) &&
        Math.Max(Math.Max(Math.Abs(r.Left), Math.Abs(r.Right)), Math.Max(Math.Abs(r.Bottom), Math.Abs(r.Top))) <= 1e6;

    /// <summary>Partial area relative to the rectangle, clamped to 0 … 1; null if it is not finite.</summary>
    private static PdfRectangle? UnitArea(PdfRectangle a) =>
        double.IsFinite(a.Left) && double.IsFinite(a.Bottom) && double.IsFinite(a.Right) && double.IsFinite(a.Top)
            ? new PdfRectangle(Math.Clamp(a.Left, 0, 1), Math.Clamp(a.Bottom, 0, 1), Math.Clamp(a.Right, 0, 1), Math.Clamp(a.Top, 0, 1))
            : null;

    /// <summary>Field property from the widget or inherited from a parent field (depth limited).</summary>
    private static PdfObject? Field(PdfDictionary widget, PdfName key)
    {
        var current = widget;
        for (var depth = 0; depth < 8 && current is not null; depth++)
        {
            if (current.Get(key) is not PdfNull and var value) return value;
            current = current.GetDictionary(Parent);
        }
        return null;
    }

    /// <summary>Converts an image as PDF Knead writes it (RGB, 8 bit, alpha as /SMask grey 8 bit) back into pixels.</summary>
    private static Imaging.RgbaImage? ReadImage(PdfDocument doc, PdfStream stream)
    {
        var dict = stream.Dictionary;
        var (w, h) = ((int)(dict.GetInteger(PdfName.Get("Width")) ?? 0), (int)(dict.GetInteger(PdfName.Get("Height")) ?? 0));
        if (w <= 0 || h <= 0 || (long)w * h > Imaging.PngDecoder.MaxPixels || dict.GetInteger(PdfName.Get("BitsPerComponent")) != 8 ||
            dict.GetName(PdfName.Get("ColorSpace"))?.Value != "DeviceRGB") return null;
        var rgb = doc.GetStreamData(stream);
        if (rgb.Length < w * h * 3) return null;
        byte[]? alpha = null;
        if (dict.GetStream(PdfName.Get("SMask")) is { } mask)
        {
            alpha = doc.GetStreamData(mask);
            if (alpha.Length < w * h) return null;
        }
        var image = Imaging.RgbaImage.Blank(w, h);
        for (var i = 0; i < w * h; i++)
        {
            image.Pixels[i * 4] = rgb[i * 3];
            image.Pixels[i * 4 + 1] = rgb[i * 3 + 1];
            image.Pixels[i * 4 + 2] = rgb[i * 3 + 2];
            image.Pixels[i * 4 + 3] = alpha is null ? (byte)255 : alpha[i];
        }
        return image;
    }

    /// <summary>Describes the settings of an element for <see cref="Key"/>.</summary>
    /// <param name="element">The element.</param>
    /// <param name="image">The image stream for image elements; otherwise <see langword="null"/>.</param>
    /// <returns>The settings dictionary (format version 1).</returns>
    public static PdfDictionary Describe(PageElement element, PdfStream? image)
    {
        var data = new PdfDictionary();
        data.Set(PdfName.Get("Version"), PdfInteger.Of(1));
        if (element.Rotation != 0) data.Set(Drehung, PdfInteger.Of(element.Rotation));
        switch (element)
        {
            case TextElement t:
                data.Set(Art, PdfName.Get(t.IsField ? "Textfeld" : "Text"));
                data.Set(TextKey, new PdfString(PdfTextEncoding.Encode(t.Text)));
                data.Set(Schrift, PdfName.Get(t.Font.ToString()));
                data.Set(Groesse, new PdfReal(t.FontSize));
                data.Set(Farbe, PdfInteger.Of(t.Color));
                data.Set(Ausrichtung, PdfInteger.Of((int)t.Alignment));
                data.Set(Umbruch, PdfBoolean.Of(t.Multiline));
                // lines spaced by the natural line height of the font (No. 1211); without the entry 1.15 times the size, as text of earlier
                // versions has it — so a file of an earlier version shows its text where it stood
                if (t.FontLineHeight && !t.IsField) data.Set(ZeilenhoeheSchrift, PdfBoolean.Of(true));
                if (t.LabelFor.Length > 0) data.Set(Fuer, new PdfString(PdfTextEncoding.Encode(t.LabelFor)));
                if (t.LabelState.Length > 0) data.Set(FuerZustand, new PdfString(PdfTextEncoding.Encode(t.LabelState)));
                break;
            case SignatureFieldElement f:
                data.Set(Art, PdfName.Get("Unterschriftsfeld"));
                if (f.Label.Length > 0) data.Set(Beschriftung, new PdfString(PdfTextEncoding.Encode(f.Label)));
                break;
            case RedactionElement r:
                data.Set(Art, PdfName.Get("Schwaerzung"));
                if (r.Hidden) data.Set(Verborgen, PdfBoolean.Of(true));   // only the flag: the hidden text itself is not stored with the mark
                if (r.Reason is { Length: > 0 } reason) data.Set(Grund, new PdfString(PdfTextEncoding.Encode(reason)));
                if (r.ReasonCode is { Length: > 0 } code) data.Set(GrundCode, new PdfString(PdfTextEncoding.Encode(code)));
                if (r.ReasonBasis is { Length: > 0 } basis) data.Set(GrundGrundlage, new PdfString(PdfTextEncoding.Encode(basis)));
                if (!r.ReasonInBar) data.Set(GrundNichtImBalken, PdfBoolean.Of(true));
                if (r.Areas.Count > 0)
                    data.Set(Bereiche, new PdfArray(r.Areas.Select(a => (PdfObject)new PdfArray(
                        [new PdfReal(a.Left), new PdfReal(a.Bottom), new PdfReal(a.Right), new PdfReal(a.Top)]))));
                break;
            case ImageElement picture when image is not null:
                data.Set(Art, PdfName.Get("Bild"));
                data.Set(Bild, image);
                if (!picture.KeepAspect) data.Set(Kette, PdfBoolean.Of(false));
                if (picture.Opacity < 1) data.Set(Deckkraft, new PdfReal(picture.Opacity));
                if (picture.AltText.Length > 0) data.Set(Alt, new PdfString(PdfTextEncoding.Encode(picture.AltText)));
                break;
            case NoteElement n:
                data.Set(Art, PdfName.Get("Notiz"));
                data.Set(Inhalt, new PdfString(PdfTextEncoding.Encode(n.Text)));
                if (n.Author.Length > 0) data.Set(Autor, new PdfString(PdfTextEncoding.Encode(n.Author)));
                data.Set(Datum, new PdfString(System.Text.Encoding.ASCII.GetBytes(PdfDate.Format(n.Modified))));
                data.Set(Farbe, PdfInteger.Of(n.Color));
                break;
            case CodeElement c:
                data.Set(Art, PdfName.Get("Code"));
                CodeLook.Of(c).Write(data);
                data.Set(Inhalt, new PdfString(PdfTextEncoding.Encode(c.Text)));
                if (c.AltText.Length > 0) data.Set(Alt, new PdfString(PdfTextEncoding.Encode(c.AltText)));
                break;
            case DimensionElement m:
                data.Set(Art, PdfName.Get("Bemassung"));
                data.Set(Messart, PdfName.Get(m.Kind.ToString()));
                data.Set(Punkte, PointArray(m.Points));
                if (m.Path.Count > 0) data.Set(Bogen, PointArray(m.Path));
                if (m.Centre is { } centre) data.Set(Mitte, PointArray([centre]));
                data.Set(Wert, new PdfString(PdfTextEncoding.Encode(m.Text)));
                if (m.Detail.Length > 0) data.Set(Mehr, new PdfString(PdfTextEncoding.Encode(m.Detail)));
                if (m.Segments.Count > 0) data.Set(Abschnitte, new PdfArray(m.Segments.Select(t => (PdfObject)new PdfString(PdfTextEncoding.Encode(t)))));
                if (m.Corners.Count > 0) data.Set(Knickwinkel, new PdfArray(m.Corners.Select(t => (PdfObject)new PdfString(PdfTextEncoding.Encode(t)))));
                if (m.ArcPoints.Count > 0) data.Set(Bogenpunkte, new PdfArray(m.ArcPoints.Select(i => (PdfObject)PdfInteger.Of(i))));
                data.Set(Farbe, PdfInteger.Of(m.Color));
                data.Set(Breite, new PdfReal(m.StrokeWidth));
                data.Set(Deckkraft, new PdfReal(m.FillOpacity));
                if (m.ShowSegments) data.Set(Masse, PdfBoolean.Of(true));
                if (m.ShowAngles) data.Set(WinkelZeigen, PdfBoolean.Of(true));
                if (m.PageRotation != 0) data.Set(Seitendrehung, PdfInteger.Of(m.PageRotation));
                // where the user moved the label of the value (No. 703); a line dimension says it in /CO as well
                if (m.LabelAt is { } label) data.Set(Wertlage, PointArray([label]));
                break;
            case ShapeElement s:
                data.Set(Art, PdfName.Get("Form"));
                data.Set(Formart, PdfName.Get(s.Kind.ToString()));
                if (s.StrokeColor is { } stroke) data.Set(Rand, PdfInteger.Of(stroke));
                if (s.FillColor is { } fill) data.Set(Fuellung, PdfInteger.Of(fill));
                data.Set(Breite, new PdfReal(s.StrokeWidth));
                if (s.Dashed) data.Set(Gestrichelt, PdfBoolean.Of(true));
                if (s.Opacity < 1) data.Set(Deckkraft, new PdfReal(s.Opacity));
                if (s.IsLine)
                {
                    data.Set(Anfang, new PdfArray([new PdfReal(s.Start.X), new PdfReal(s.Start.Y)]));
                    data.Set(Ende, new PdfArray([new PdfReal(s.End.X), new PdfReal(s.End.Y)]));
                }
                break;
        }
        return data;
    }
}
