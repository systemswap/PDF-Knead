using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Content;

/// <summary>
/// Builds an appearance for annotations without /AP, as common PDF readers do: Square, Circle, Line,
/// PolyLine, Polygon, Ink, the text markups Highlight, Underline, StrikeOut and Squiggly, the icon of a Text note and of a file attachment — from /Rect,
/// /C, /IC, /BS or /Border, /CA, /L, /LE, /Vertices, /InkList and /QuadPoints. The result is a form XObject in page space
/// (BBox = /Rect, extended to the points where necessary) that is drawn like a stored appearance; line endings (/LE) follow widely used readers.
/// </summary>
internal static class AnnotationAppearance
{
    // Limit against crafted files: at most this many numbers (coordinates) per annotation (InkList, Vertices, QuadPoints).
    private const int MaxNumbers = 200_000;

    /// <summary>
    /// Builds the appearance stream of <paramref name="annot"/>; null if the subtype is not supported or there is nothing to draw.
    /// </summary>
    public static PdfStream? Build(PdfDictionary annot, Document.PdfRectangle rect)
    {
        var subtype = annot.GetName(PdfName.Subtype)?.Value;
        if (subtype is not ("Square" or "Circle" or "Line" or "PolyLine" or "Polygon" or "Ink" or "Highlight" or "Underline" or "StrikeOut" or "Squiggly" or "Text" or
            "FileAttachment"))
            return null;
        if (subtype == "Text" && annot.Get(PdfName.Get("IRT")) is not (null or PdfNull)) return null;   // reply to a note: no icon of its own
        var sb = new StringBuilder();
        var stroke = Color(annot.GetArray(PdfName.Get("C")), stroking: true);
        var fill = Color(annot.GetArray(PdfName.Get("IC")), stroking: false);
        var (width, dash) = Border(annot);
        var resources = new PdfDictionary();
        var gs = new PdfDictionary();
        if (annot.GetNumber(PdfName.Get("CA")) is { } ca && ca < 1)
        {
            gs.Set(PdfName.Get("CA"), new PdfReal(Math.Clamp(ca, 0, 1)));
            gs.Set(PdfName.Get("ca"), new PdfReal(Math.Clamp(ca, 0, 1)));
        }
        if (subtype == "Highlight") gs.Set(PdfName.Get("BM"), PdfName.Get("Multiply"));   // keeps the text underneath legible
        var bounds = new Bounds();
        if (gs.Count > 0)
        {
            var states = new PdfDictionary();
            states.Set(PdfName.Get("GS0"), gs);
            resources.Set(PdfName.Get("ExtGState"), states);
            sb.Append("/GS0 gs\n");
        }

        switch (subtype)
        {
            case "Text":
                // Note without an appearance (test file rc_annotation): an icon chosen by /Name, 20 × 20 pt as in common PDF editors, with its
                // top-left corner at that of /Rect (other readers draw one as well; /Rect is often empty); colour /C, otherwise light yellow.
                rect = new Document.PdfRectangle(rect.Left, rect.Top - NoteSize, rect.Left + NoteSize, rect.Top);
                NoteIcon(sb, annot.GetName(PdfName.Get("Name"))?.Value, Color(annot.GetArray(PdfName.Get("C")), stroking: false) ?? NoteFill, rect);
                break;
            case "FileAttachment":
            {
                // File attachment without an appearance (test file example_041): the icon its /Name asks for (12.5.6.15 Table 187: PushPin, the default,
                // Paperclip, Graph, Tag; readers should provide them, two of the three reference readers draw one), square in the top-left corner
                // of /Rect, 20 pt where /Rect has no area; colour /C, otherwise light grey (docs/FEHLERGEDAECHTNIS.md, No. 893).
                var side = rect.Width >= 1 && rect.Height >= 1 ? Math.Min(rect.Width, rect.Height) : NoteSize;
                rect = new Document.PdfRectangle(rect.Left, rect.Top - side, rect.Left + side, rect.Top);
                AttachmentIcon(sb, annot.GetName(PdfName.Get("Name"))?.Value, Color(annot.GetArray(PdfName.Get("C")), stroking: false) ?? AttachmentFill, rect);
                break;
            }
            case "Square" or "Circle":
            {
                var hasStroke = stroke is not null && width > 0;
                // neither a border (none or width 0) nor an interior: nothing to draw — before, a path filled without its own colour, black
                // (docs/FEHLERGEDAECHTNIS.md, No. 1126)
                if (!hasStroke && fill is null) return null;
                if (hasStroke) Stroke(sb, stroke!, width, dash);
                if (fill is not null) sb.Append(fill).Append('\n');
                var inset = hasStroke ? width / 2 : 0;
                double x0 = rect.Left + inset, y0 = rect.Bottom + inset, x1 = rect.Right - inset, y1 = rect.Top - inset;
                if (x1 <= x0 || y1 <= y0) return null;
                if (subtype == "Square") sb.Append(N(x0)).Append(' ').Append(N(y0)).Append(' ').Append(N(x1 - x0)).Append(' ').Append(N(y1 - y0)).Append(" re\n");
                else Ellipse(sb, x0, y0, x1, y1);
                sb.Append(hasStroke ? fill is not null ? "B\n" : "S\n" : "f\n");
                break;
            }
            case "Line":
            {
                if (stroke is null || width <= 0 || Numbers(annot.GetArray(PdfName.Get("L")), 4) is not { Length: 4 } l) return null;
                Stroke(sb, stroke, width, dash);
                sb.Append(N(l[0])).Append(' ').Append(N(l[1])).Append(" m ").Append(N(l[2])).Append(' ').Append(N(l[3])).Append(" l S\n");
                bounds.Add(l);
                Endings(sb, annot, l[0], l[1], l[2], l[3], l[2], l[3], l[0], l[1], width, fill, ref bounds);
                break;
            }
            case "PolyLine" or "Polygon":
            {
                if (Numbers(annot.GetArray(PdfName.Get("Vertices")), MaxNumbers) is not { Length: >= 4 } v) return null;
                var closed = subtype == "Polygon";
                var hasStroke = stroke is not null && width > 0;
                if (!hasStroke && !(closed && fill is not null)) return null;
                if (hasStroke) Stroke(sb, stroke!, width, dash);
                if (closed && fill is not null) sb.Append(fill).Append('\n');
                Polyline(sb, v);
                sb.Append(closed ? hasStroke ? fill is not null ? "b\n" : "s\n" : "h f\n" : "S\n");
                bounds.Add(v);
                if (!closed && hasStroke)
                    Endings(sb, annot, v[0], v[1], v[2], v[3], v[^2], v[^1], v[^4], v[^3], width, fill, ref bounds);
                break;
            }
            case "Ink":
            {
                if (stroke is null || width <= 0 || annot.GetArray(PdfName.Get("InkList")) is not { } ink) return null;
                Stroke(sb, stroke, width, dash);
                sb.Append("1 J 1 j\n");
                var total = 0;
                for (var i = 0; i < ink.Count && total < MaxNumbers; i++)
                    if (Numbers(ink.Get(i) as PdfArray, MaxNumbers - total) is { Length: >= 2 } points)
                    {
                        total += points.Length;
                        bounds.Add(points);
                        Polyline(sb, points);
                        if (points.Length == 2) sb.Append(N(points[0])).Append(' ').Append(N(points[1])).Append(" l\n");   // a single point: a dot
                        sb.Append("S\n");
                    }
                break;
            }
            default:   // text markups via /QuadPoints (8 numbers each: top left, top right, bottom left, bottom right — as common PDF editors write them)
            {
                if (stroke is null || Numbers(annot.GetArray(PdfName.Get("QuadPoints")), MaxNumbers) is not { Length: >= 8 } q) return null;
                if (subtype == "Highlight") sb.Append(Color(annot.GetArray(PdfName.Get("C")), stroking: false)).Append('\n');
                for (var i = 0; i + 8 <= q.Length; i += 8)
                    Markup(sb, subtype!, q.AsSpan(i, 8), stroke);
                bounds.Add(q);
                break;
            }
        }
        // If the points lie outside /Rect (test file issue13447: /Rect written by another program that had rotated the page),
        // the other readers still draw them completely, so the box encloses them as well. Otherwise it stays /Rect.
        // An empty /Rect (only a position, see RectOf) adds nothing: the box is that of the points.
        var box = rect;
        if (bounds.Valid)
        {
            var margin = width / 2 + bounds.Margin + 1;
            var (left, bottom, right, top) = (bounds.Left - margin, bounds.Bottom - margin, bounds.Right + margin, bounds.Top + margin);
            if (rect.IsEmpty) box = new Document.PdfRectangle(left, bottom, right, top);
            else if (left < rect.Left || bottom < rect.Bottom || right > rect.Right || top > rect.Top)
                box = new Document.PdfRectangle(Math.Min(left, rect.Left), Math.Min(bottom, rect.Bottom), Math.Max(right, rect.Right), Math.Max(top, rect.Top));
        }
        else if (rect.IsEmpty) return null;
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Get("XObject"));
        dict.Set(PdfName.Subtype, PdfName.Get("Form"));
        dict.Set(PdfName.Get("BBox"), new PdfArray([new PdfReal(box.Left), new PdfReal(box.Bottom), new PdfReal(box.Right), new PdfReal(box.Top)]));
        dict.Set(PdfName.Get("Resources"), resources);
        return new PdfStream(dict, Encoding.ASCII.GetBytes(sb.ToString()));
    }

    /// <summary>Draws one text markup over a quadrilateral; "bottom" and "height" run along its sides (so rotated pages work too).</summary>
    private static void Markup(StringBuilder sb, string subtype, ReadOnlySpan<double> q, string color)
    {
        double x1 = q[0], y1 = q[1], x2 = q[2], y2 = q[3], x3 = q[4], y3 = q[5], x4 = q[6], y4 = q[7];
        if (subtype == "Highlight")
        {
            sb.Append(N(x1)).Append(' ').Append(N(y1)).Append(" m ").Append(N(x2)).Append(' ').Append(N(y2)).Append(" l ")
              .Append(N(x4)).Append(' ').Append(N(y4)).Append(" l ").Append(N(x3)).Append(' ').Append(N(y3)).Append(" l h f\n");
            return;
        }
        // Height: from bottom to top (left side)
        double hx = x1 - x3, hy = y1 - y3;
        var height = Math.Sqrt(hx * hx + hy * hy);
        if (height <= 0 || !double.IsFinite(height)) return;
        var line = Math.Max(0.5, height / 14);
        sb.Append(color).Append("\n[] 0 d ").Append(N(line)).Append(" w\n");
        switch (subtype)
        {
            case "Underline":
            {
                var t = line / height;   // just above the bottom edge
                sb.Append(N(x3 + hx * t)).Append(' ').Append(N(y3 + hy * t)).Append(" m ").Append(N(x4 + hx * t)).Append(' ').Append(N(y4 + hy * t)).Append(" l S\n");
                break;
            }
            case "StrikeOut":
                sb.Append(N((x1 + x3) / 2)).Append(' ').Append(N((y1 + y3) / 2)).Append(" m ").Append(N((x2 + x4) / 2)).Append(' ').Append(N((y2 + y4) / 2)).Append(" l S\n");
                break;
            default:   // Squiggly: zigzag along the bottom edge
            {
                double bx = x4 - x3, by = y4 - y3;
                var length = Math.Sqrt(bx * bx + by * by);
                if (length <= 0) return;
                var step = Math.Max(height / 6, 0.5);
                var count = (int)Math.Min(Math.Ceiling(length / step), 10_000);
                var amplitude = height / 10;
                sb.Append(N(x3)).Append(' ').Append(N(y3)).Append(" m ");
                for (var i = 1; i <= count; i++)
                {
                    var t = Math.Min(1, i / (double)count);
                    var up = i % 2 == 1 ? amplitude / height : 0;
                    sb.Append(N(x3 + bx * t + hx * up)).Append(' ').Append(N(y3 + by * t + hy * up)).Append(" l ");
                }
                sb.Append("S\n");
                break;
            }
        }
    }

    internal const double NoteSize = 20;

    /// <summary>
    /// Where an annotation draws on the page, as the interpreter draws it: /Rect for a stored appearance; for one built here its box (it
    /// encloses points beyond /Rect, and a note's icon lies at the top-left corner even of an empty /Rect); with NoRotate on a rotated page,
    /// turned about the top-left corner of /Rect. Redaction drops an annotation that a bar touches there — by /Rect alone, a line drawn
    /// beyond it showed over the bar (docs/FEHLERGEDAECHTNIS.md, No. 167). Null if it has no usable rectangle.
    /// </summary>
    public static Document.PdfRectangle? DrawnBounds(PdfDictionary annot, int pageRotate)
    {
        if (RectOf(annot) is not { } rect) return null;
        var normal = annot.GetDictionary(PdfName.Get("AP"))?.Get(PdfName.N);
        if (normal is PdfDictionary states && normal is not PdfStream)
            normal = annot.GetName(PdfName.Get("AS")) is { } state ? states.Get(state) : null;
        var box = rect;
        if (normal is null or PdfNull && annot.GetName(PdfName.Subtype)?.Value is not ("Widget" or "FreeText") && Build(annot, rect) is { } generated &&
            Document.PdfRectangle.From(generated.Dictionary.Get(PdfName.Get("BBox"))) is { } built)
            box = built;
        if (((annot.GetInteger(PdfName.Get("F")) ?? 0) & 16) == 0 || pageRotate % 360 == 0) return box;
        var turn = Graphics.Matrix.Translate(-rect.Left, -rect.Top) * Graphics.Matrix.Rotate(pageRotate) * Graphics.Matrix.Translate(rect.Left, rect.Top);
        var turned = turn.TransformBounds(Graphics.RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top));
        return new Document.PdfRectangle(Math.Min(box.Left, turned.Left), Math.Min(box.Bottom, turned.Top), Math.Max(box.Right, turned.Right), Math.Max(box.Top, turned.Bottom));
    }

    /// <summary>
    /// Returns the /Rect of an annotation. For a note (Text) an empty /Rect (test file rc_annotation: [50 50 50 50]) is accepted
    /// as well, because its icon only needs the top-left corner; so it is for lines, polylines, polygons and ink, which are drawn from
    /// their points when they have no appearance of their own (test file annotation-line-without-appearance-empty-Rect: [0 0 0 0]; other
    /// readers draw the line; docs/FEHLERGEDAECHTNIS.md, No. 244). Null if there is no usable rectangle.
    /// </summary>
    public static Document.PdfRectangle? RectOf(PdfDictionary annot)
    {
        var raw = annot.Get(PdfName.Get("Rect"));
        if (Document.PdfRectangle.From(raw) is { } rect) return rect;
        if (annot.GetName(PdfName.Subtype)?.Value is not ("Text" or "Line" or "PolyLine" or "Polygon" or "Ink") || Numbers(raw as PdfArray, 4) is not { Length: 4 } v)
            return null;
        double left = Math.Min(v[0], v[2]), top = Math.Max(v[1], v[3]);
        return Math.Abs(left) < 1e7 && Math.Abs(top) < 1e7 ? new Document.PdfRectangle(left, top, left, top) : null;
    }

    /// <summary>
    /// Draws the icon of a note (Text annotation) in a 20 × 20 pt box: speech bubble (Note, Comment and unknown names), question mark (Help),
    /// caret (Insert), key (Key), pilcrow (Paragraph, NewParagraph). Filled with <paramref name="fill"/>, lines dark.
    /// </summary>
    private static void NoteIcon(StringBuilder sb, string? name, string fill, Document.PdfRectangle box)
    {
        sb.Append("q 1 0 0 1 ").Append(N(box.Left)).Append(' ').Append(N(box.Bottom)).Append(" cm\n");
        sb.Append(fill).Append('\n').Append(NoteLine).Append(" 1 w 1 J 1 j\n");
        switch (name)
        {
            case "Comment":
                sb.Append(NoteBubble(NoteSize, fill, NoteLine));
                break;
            case "Help":
                Ellipse(sb, 1.5, 1.5, 18.5, 18.5);
                sb.Append("b\n2 w 7 13 m 7 16.5 13 16.5 13 13 c 13 10.5 10 10.5 10 8 c S\n10 4.5 m 10 4.6 l S\n");
                break;
            case "Insert":
                sb.Append("2.5 2.5 m 10 17.5 l 17.5 2.5 l h b\n");
                break;
            case "Key":
                Ellipse(sb, 1.5, 8, 10.5, 17);
                sb.Append("b\n2 w 10.5 12.5 m 18.5 12.5 l 15.5 12.5 m 15.5 9 l 18 12.5 m 18 9.5 l S\n");
                break;
            case "Paragraph" or "NewParagraph":
                sb.Append("2 18.5 m 18 18.5 l 18 1.5 l 2 1.5 l h b\n0.25 0.25 0.25 rg\n");
                sb.Append("10 16.5 m 6.5 16.5 5 14.5 5 12.5 c 5 10.5 6.5 8.5 10 8.5 c h f\n1.5 w 10 16.5 m 10 4 l 13 16.5 m 13 4 l 9.5 16.5 m 14.5 16.5 l S\n");
                break;
            default:   // Note
                sb.Append(NoteBubble(NoteSize, fill, NoteLine));
                break;
        }
        sb.Append("Q\n");
    }

    /// <summary>
    /// Draws the icon of a file attachment in <paramref name="box"/> (drawn in a 20 × 20 unit square, scaled): push pin (PushPin and names it does
    /// not know), paper clip (Paperclip), bar chart (Graph), tag (Tag). Filled with <paramref name="fill"/>, lines dark.
    /// </summary>
    private static void AttachmentIcon(StringBuilder sb, string? name, string fill, Document.PdfRectangle box)
    {
        var k = box.Width / NoteSize;
        sb.Append("q ").Append(N(k)).Append(" 0 0 ").Append(N(k)).Append(' ').Append(N(box.Left)).Append(' ').Append(N(box.Bottom)).Append(" cm\n");
        sb.Append(fill).Append('\n').Append(NoteLine).Append(" 1 w 1 J 1 j\n");
        switch (name)
        {
            case "Paperclip":
                sb.Append("1.6 w 8 5 m 8 14.5 l 8 18 14 18 14 14.5 c 14 5.5 l 14 2 10.5 2 10.5 5.5 c 10.5 13.5 l S\n");
                break;
            case "Graph":
                sb.Append("2 2 16 16 re b\n0.35 0.35 0.35 rg 5 4 2.5 6 re f 8.75 4 2.5 11 re f 12.5 4 2.5 8 re f\n");
                break;
            case "Tag":
                sb.Append("1.5 10 m 7.5 16.5 l 18.5 16.5 l 18.5 3.5 l 7.5 3.5 l h b\n");
                Ellipse(sb, 5.5, 8.5, 8.5, 11.5);
                sb.Append("S\n");
                break;
            default:   // PushPin
                sb.Append("6 15.5 8 3 re b\n8.5 10 m 7.5 15.5 l 12.5 15.5 l 11.5 10 l h b\n4 8.5 12 1.5 re b\n1.5 w 10 8.5 m 10 1 l S\n");
                break;
        }
        sb.Append("Q\n");
    }

    /// <summary>The light grey of a file attachment's icon without /C.</summary>
    private const string AttachmentFill = "0.88 0.88 0.88 rg";

    /// <summary>The light yellow of a note (wish of 30.10.2026), for the symbol at a bar as well.</summary>
    internal const string NoteFill = "1 0.87 0.45 rg";

    private const string NoteLine = "0.45 0.4 0.25 RG";

    /// <summary>
    /// A speech bubble with rounded corners in a square, its tip at the bottom left; from 8 pt with two lines of writing (smaller only the shape).
    /// </summary>
    /// <param name="s">The side of the square.</param>
    /// <param name="fill">The fill colour operators.</param>
    /// <param name="outline">The outline colour operators; null: no outline.</param>
    /// <returns>The content.</returns>
    internal static string NoteBubble(double s, string fill, string? outline)
    {
        double r = s * 0.2, k = r * 0.55, x0 = s * 0.06, x1 = s * 0.94, y0 = s * 0.28, y1 = s * 0.94;
        var sb = new StringBuilder().Append(fill).Append('\n');
        if (outline is not null) sb.Append(outline).Append(' ').Append(N(Math.Max(s * 0.05, 0.3))).Append(" w\n");
        sb.Append($"{N(x0 + r)} {N(y1)} m {N(x1 - r)} {N(y1)} l {N(x1 - r + k)} {N(y1)} {N(x1)} {N(y1 - r + k)} {N(x1)} {N(y1 - r)} c ");
        sb.Append($"{N(x1)} {N(y0 + r)} l {N(x1)} {N(y0 + r - k)} {N(x1 - r + k)} {N(y0)} {N(x1 - r)} {N(y0)} c ");
        sb.Append($"{N(s * 0.52)} {N(y0)} l {N(s * 0.28)} {N(s * 0.04)} l {N(s * 0.3)} {N(y0)} l {N(x0 + r)} {N(y0)} l ");
        sb.Append($"{N(x0 + r - k)} {N(y0)} {N(x0)} {N(y0 + r - k)} {N(x0)} {N(y0 + r)} c {N(x0)} {N(y1 - r)} l {N(x0)} {N(y1 - r + k)} {N(x0 + r - k)} {N(y1)} {N(x0 + r)} {N(y1)} c h ");
        sb.Append(outline is null ? "f\n" : "B\n");
        if (s >= 8)
            sb.Append($"0.35 0.35 0.35 RG {N(s * 0.07)} w 1 J {N(s * 0.26)} {N(s * 0.72)} m {N(s * 0.74)} {N(s * 0.72)} l {N(s * 0.26)} {N(s * 0.5)} m {N(s * 0.6)} {N(s * 0.5)} l S\n");
        return sb.ToString();
    }

    /// <summary>
    /// Draws the line endings /LE [start end] of Line and PolyLine (other readers draw them): arrows, square, circle,
    /// diamond, butt and slash, scaled with the stroke width as in widely used readers. Closed shapes are filled with /IC.
    /// (x0, y0) is the start, (x1, y1) the point after it; (x2, y2) the end, (x3, y3) the point before it.
    /// </summary>
    private static void Endings(StringBuilder sb, PdfDictionary annot, double x0, double y0, double x1, double y1, double x2, double y2,
        double x3, double y3, double width, string? fill, ref Bounds bounds)
    {
        if (annot.GetArray(PdfName.Get("LE")) is not { Count: >= 1 } le) return;
        var start = le.Get(0) is PdfName a ? a.Value : null;
        var end = le.Count >= 2 && le.Get(1) is PdfName b ? b.Value : null;
        if (start is null or "None" && end is null or "None") return;
        sb.Append("[] 0 d 0 J 0 j\n");   // endings always solid, with butt caps and mitre joins
        Ending(sb, start, x0, y0, x0 - x1, y0 - y1, width, fill, ref bounds);
        Ending(sb, end, x2, y2, x2 - x3, y2 - y3, width, fill, ref bounds);
    }

    /// <summary>Draws one line ending at (x, y); (dx, dy) points outwards from the line.</summary>
    private static void Ending(StringBuilder sb, string? style, double x, double y, double dx, double dy, double width, string? fill, ref Bounds bounds)
    {
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (style is null or "None" || length <= 0 || !double.IsFinite(length)) return;
        double ux = dx / length, uy = dy / length, nx = -uy, ny = ux;
        var r = Math.Max(1, width);
        string P(double along, double across) => N(x + ux * along * r + nx * across * r) + " " + N(y + uy * along * r + ny * across * r);
        if (fill is not null) sb.Append(fill).Append('\n');   // colour before the path (colour operators are not allowed inside a path)
        var closing = fill is not null ? "b\n" : "s\n";
        switch (style)
        {
            case "OpenArrow" or "ClosedArrow":
                sb.Append(P(-8.8, 4.5)).Append(" m ").Append(P(0, 0)).Append(" l ").Append(P(-8.8, -4.5)).Append(" l ").Append(style == "OpenArrow" ? "S\n" : closing);
                break;
            case "ROpenArrow" or "RClosedArrow":
                sb.Append(P(8.8, 4.5)).Append(" m ").Append(P(0, 0)).Append(" l ").Append(P(8.8, -4.5)).Append(" l ").Append(style == "ROpenArrow" ? "S\n" : closing);
                break;
            case "Square":
                sb.Append(P(-2.5, -2.5)).Append(" m ").Append(P(2.5, -2.5)).Append(" l ").Append(P(2.5, 2.5)).Append(" l ").Append(P(-2.5, 2.5)).Append(" l ").Append(closing);
                break;
            case "Diamond":
                sb.Append(P(-2.5, 0)).Append(" m ").Append(P(0, -2.5)).Append(" l ").Append(P(2.5, 0)).Append(" l ").Append(P(0, 2.5)).Append(" l ").Append(closing);
                break;
            case "Circle":
                Ellipse(sb, x - 2.5 * r, y - 2.5 * r, x + 2.5 * r, y + 2.5 * r);
                sb.Append(fill is not null ? "b\n" : "S\n");
                break;
            case "Butt":
                sb.Append(P(0, 4.5)).Append(" m ").Append(P(0, -4.5)).Append(" l S\n");
                break;
            case "Slash":   // 30° from the perpendicular to the line
                sb.Append(P(-2.25, 3.9)).Append(" m ").Append(P(2.25, -3.9)).Append(" l S\n");
                break;
            default:
                return;
        }
        bounds.Margin = Math.Max(bounds.Margin, 10 * r);
    }

    /// <summary>Bounding box of the points of an annotation (x, y pairs); <see cref="Margin"/> is the extra space line endings need beyond it.</summary>
    private struct Bounds
    {
        public double Left, Bottom, Right, Top, Margin;
        public bool Valid;

        public void Add(ReadOnlySpan<double> points)
        {
            for (var i = 0; i + 1 < points.Length; i += 2)
            {
                double x = points[i], y = points[i + 1];
                if (!Valid)
                {
                    (Left, Right, Bottom, Top, Valid) = (x, x, y, y, true);
                    continue;
                }
                Left = Math.Min(Left, x);
                Right = Math.Max(Right, x);
                Bottom = Math.Min(Bottom, y);
                Top = Math.Max(Top, y);
            }
        }
    }

    private static void Stroke(StringBuilder sb, string color, double width, double[]? dash)
    {
        sb.Append(color).Append('\n').Append(N(width)).Append(" w\n");
        if (dash is { Length: > 0 })
        {
            sb.Append('[');
            foreach (var d in dash) sb.Append(N(d)).Append(' ');
            sb.Append("] 0 d\n");
        }
    }

    private static void Polyline(StringBuilder sb, double[] p)
    {
        sb.Append(N(p[0])).Append(' ').Append(N(p[1])).Append(" m\n");
        for (var i = 2; i + 1 < p.Length; i += 2) sb.Append(N(p[i])).Append(' ').Append(N(p[i + 1])).Append(" l\n");
    }

    private static void Ellipse(StringBuilder sb, double x0, double y0, double x1, double y1)
    {
        const double k = 0.5522847498;
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, rx = (x1 - x0) / 2, ry = (y1 - y0) / 2;
        sb.Append(N(cx + rx)).Append(' ').Append(N(cy)).Append(" m\n");
        Curve(sb, cx + rx, cy + ry * k, cx + rx * k, cy + ry, cx, cy + ry);
        Curve(sb, cx - rx * k, cy + ry, cx - rx, cy + ry * k, cx - rx, cy);
        Curve(sb, cx - rx, cy - ry * k, cx - rx * k, cy - ry, cx, cy - ry);
        Curve(sb, cx + rx * k, cy - ry, cx + rx, cy - ry * k, cx + rx, cy);
        sb.Append("h\n");
    }

    private static void Curve(StringBuilder sb, double a, double b, double c, double d, double e, double f) =>
        sb.Append(N(a)).Append(' ').Append(N(b)).Append(' ').Append(N(c)).Append(' ').Append(N(d)).Append(' ').Append(N(e)).Append(' ').Append(N(f)).Append(" c\n");

    /// <summary>Border width and dash pattern: /BS (/W, /S, /D), otherwise /Border [h v w (dash)]; width 1 if neither is given.</summary>
    private static (double Width, double[]? Dash) Border(PdfDictionary annot)
    {
        if (annot.GetDictionary(PdfName.Get("BS")) is { } bs)
        {
            var w = bs.GetNumber(PdfName.Get("W")) ?? 1;
            var style = bs.GetName(PdfName.Get("S"))?.Value;
            double[]? dash = style == "D" ? Numbers(bs.GetArray(PdfName.Get("D")), 16) ?? [3] : null;
            return (Math.Clamp(w, 0, 1000), dash is { Length: > 0 } && dash.All(d => d >= 0) && dash.Any(d => d > 0) ? dash : null);
        }
        if (annot.GetArray(PdfName.Get("Border")) is { Count: >= 3 } border)
        {
            double[]? dash = border.Count >= 4 ? Numbers(border.Get(3) as PdfArray, 16) : null;
            return (Math.Clamp(border.GetNumber(2) ?? 1, 0, 1000), dash is { Length: > 0 } && dash.All(d => d >= 0) && dash.Any(d => d > 0) ? dash : null);
        }
        return (1, null);
    }

    /// <summary>Colour as an operator (no numbers: none; 1 grey, 3 RGB, 4 CMYK).</summary>
    private static string? Color(PdfArray? c, bool stroking)
    {
        if (Numbers(c, 4) is not { } n) return null;
        string Join() => string.Join(' ', n.Select(v => N(Math.Clamp(v, 0, 1))));
        return n.Length switch
        {
            1 => Join() + (stroking ? " G" : " g"),
            3 => Join() + (stroking ? " RG" : " rg"),
            4 => Join() + (stroking ? " K" : " k"),
            _ => null,
        };
    }

    private static double[]? Numbers(PdfArray? array, int max)
    {
        if (array is null || array.Count == 0) return null;
        var count = Math.Min(array.Count, max);
        var result = new double[count];
        for (var i = 0; i < count; i++)
        {
            if (array.GetNumber(i) is not { } v || !double.IsFinite(v)) return null;
            result[i] = v;
        }
        return result;
    }

    private static string N(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
