using System.Text;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Shapes: outline or line in reading orientation (0 … width × 0 … height of the shape), with the border inside as in the
/// square and circle annotations of common PDF editors (inset by half the stroke width). The writer stores the same points as /Vertices or /L, so that
/// another program that redraws the appearance arrives at the same shape.
/// </summary>
/// <remarks>See KONZEPT.md, section 5.13.</remarks>
public sealed partial class ElementAppearance
{
    /// <summary>Gets the dash pattern of a shape.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>Dash and gap in points: 4 and 3 stroke widths, at least 3 and 2 points.</returns>
    public static double[] ShapeDash(ShapeElement shape) => [Math.Max(3, shape.StrokeWidth * 4), Math.Max(2, shape.StrokeWidth * 3)];

    /// <summary>
    /// Gets the size of an arrowhead, proportional to the stroke width (a fixed allowance would make the head at 2 pt twice as large
    /// as in office programs): 4 stroke widths long and wide, at least 4 points for thin lines. On short arrows the head shrinks to a third of
    /// the line length, but not below 2 stroke widths, so that the head does not make up the whole arrow.
    /// </summary>
    /// <param name="shape">The arrow.</param>
    /// <param name="lineLength">Length of the line in points; infinite if unknown.</param>
    /// <returns>Length and half width of the head in points.</returns>
    public static (double Length, double HalfWidth) ArrowHead(ShapeElement shape, double lineLength = double.PositiveInfinity)
    {
        var length = Math.Max(4, shape.StrokeWidth * 4);
        if (lineLength / 3 < length) length = Math.Max(lineLength / 3, Math.Min(length, shape.StrokeWidth * 2));
        return (length, length / 2);
    }

    /// <summary>Gets the corners of a polygon shape (triangle, diamond, star, callout) in reading orientation.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>The corners in points, inset by half the stroke width; empty for rectangle, ellipse, line and arrow.</returns>
    public static (double X, double Y)[] ShapeVertices(ShapeElement shape)
    {
        (double X, double Y)[] unit = shape.Kind switch
        {
            ShapeKind.Triangle => [(0.5, 1), (1, 0), (0, 0)],
            ShapeKind.Diamond => [(0.5, 1), (1, 0.5), (0.5, 0), (0, 0.5)],
            ShapeKind.Star => Star(),
            ShapeKind.Callout => [(0, 1), (1, 1), (1, 0.25), (0.4, 0.25), (0.15, 0), (0.22, 0.25), (0, 0.25)],
            _ => [],
        };
        if (unit.Length == 0) return unit;
        var (w, h) = shape.UprightSize;
        var inset = Inset(shape);
        double sw = Math.Max(0, w - 2 * inset), sh = Math.Max(0, h - 2 * inset);
        return [.. unit.Select(p => (inset + p.X * sw, inset + p.Y * sh))];
    }

    /// <summary>Five-pointed star, stretched to 0 … 1 in both directions (the points touch the edges).</summary>
    private static (double X, double Y)[] Star()
    {
        var raw = new (double X, double Y)[10];
        for (var i = 0; i < 10; i++)
        {
            var angle = Math.PI / 2 + i * Math.PI / 5;
            var r = i % 2 == 0 ? 1 : 0.382;
            raw[i] = (Math.Cos(angle) * r, Math.Sin(angle) * r);
        }
        double minX = raw.Min(p => p.X), maxX = raw.Max(p => p.X), minY = raw.Min(p => p.Y), maxY = raw.Max(p => p.Y);
        return [.. raw.Select(p => ((p.X - minX) / (maxX - minX), (p.Y - minY) / (maxY - minY)))];
    }

    /// <summary>Gets start and end of a line or arrow in reading orientation.</summary>
    /// <param name="shape">The line or arrow.</param>
    /// <returns>Start and end point in points.</returns>
    public static ((double X, double Y) Start, (double X, double Y) End) ShapeLine(ShapeElement shape)
    {
        var (w, h) = shape.UprightSize;
        return ((shape.Start.X * w, shape.Start.Y * h), (shape.End.X * w, shape.End.Y * h));
    }

    // Border inside: inset by half the stroke width (sufficient for the star because it has round joins)
    private static double Inset(ShapeElement shape) => shape.StrokeColor is null ? 0 : shape.StrokeWidth / 2;

    /// <summary>A note: the speech bubble the display also draws for notes without an appearance, in its colour, filling the rectangle.</summary>
    private static ElementAppearance BuildNote(NoteElement note)
    {
        var (w, _) = note.UprightSize;
        var content = Content.AnnotationAppearance.NoteBubble(w, Rgb(note.Color) + " rg", "0.55 0.5 0.3 RG");
        return new ElementAppearance(note, FormStream(note, new PdfDictionary(), Encoding.ASCII.GetBytes(content)), null, NoGlyphs);
    }

    private static ElementAppearance BuildShape(ShapeElement shape)
    {
        var (w, h) = shape.UprightSize;
        var sb = new StringBuilder("q\n");
        var resources = new PdfDictionary();
        if (shape.Opacity < 1)
        {
            var state = new PdfDictionary();
            var alpha = new PdfReal(Math.Clamp(shape.Opacity, 0, 1));
            state.Set(PdfName.Get("CA"), alpha);
            state.Set(PdfName.Get("ca"), alpha);
            var states = new PdfDictionary();
            states.Set(PdfName.Get("GS0"), state);
            resources.Set(PdfName.Get("ExtGState"), states);
            sb.Append("/GS0 gs\n");
        }
        var stroke = shape.StrokeColor is { } sc && shape.StrokeWidth > 0 ? sc : (int?)null;
        var fill = shape.IsLine ? null : shape.FillColor;
        if (stroke is { } s) sb.Append(Rgb(s)).Append(" RG ").Append(ElementFace.Number(shape.StrokeWidth)).Append(" w\n");
        if (fill is { } f) sb.Append(Rgb(f)).Append(" rg\n");
        if (shape.Dashed && stroke is not null)
            sb.Append('[').Append(string.Join(' ', ShapeDash(shape).Select(ElementFace.Number))).Append("] 0 d\n");
        sb.Append(shape.Kind == ShapeKind.Star ? "1 j\n" : "0 j\n");
        var paint = stroke is not null ? fill is not null ? "B" : "S" : fill is not null ? "f" : "n";
        var inset = Inset(shape);

        switch (shape.Kind)
        {
            case ShapeKind.Rectangle:
                sb.Append(ElementFace.Number(inset)).Append(' ').Append(ElementFace.Number(inset)).Append(' ')
                  .Append(ElementFace.Number(Math.Max(0, w - 2 * inset))).Append(' ').Append(ElementFace.Number(Math.Max(0, h - 2 * inset))).Append(" re ").Append(paint).Append('\n');
                break;
            case ShapeKind.Ellipse:
                ShapeEllipse(sb, inset, inset, w - inset, h - inset);
                sb.Append(paint).Append('\n');
                break;
            case ShapeKind.Line or ShapeKind.Arrow:
            {
                if (stroke is null) break;
                var ((x1, y1), (x2, y2)) = ShapeLine(shape);
                double dx = x2 - x1, dy = y2 - y1, length = Math.Sqrt(dx * dx + dy * dy);
                if (shape.Kind == ShapeKind.Arrow && length > 0)
                {
                    // Line up to the base of the head (otherwise the line end pokes through the head on thick lines), then the filled head
                    var (headLength, half) = ArrowHead(shape, length);
                    headLength = Math.Min(headLength, length);
                    double ux = dx / length, uy = dy / length;
                    double bx = x2 - ux * headLength, by = y2 - uy * headLength;
                    sb.Append("0 J ").Append(P(x1, y1)).Append(" m ").Append(P(bx + ux * 0.5, by + uy * 0.5)).Append(" l S\n");
                    sb.Append("[] 0 d ").Append(Rgb(stroke.Value)).Append(" rg ")
                      .Append(P(x2, y2)).Append(" m ").Append(P(bx - uy * half, by + ux * half)).Append(" l ")
                      .Append(P(bx + uy * half, by - ux * half)).Append(" l h f\n");
                }
                else
                {
                    sb.Append("1 J ").Append(P(x1, y1)).Append(" m ").Append(P(x2, y2)).Append(" l S\n");
                }
                break;
            }
            default:
            {
                var points = ShapeVertices(shape);
                if (points.Length < 3) break;
                sb.Append(P(points[0].X, points[0].Y)).Append(" m\n");
                for (var i = 1; i < points.Length; i++) sb.Append(P(points[i].X, points[i].Y)).Append(" l\n");
                sb.Append("h ").Append(paint).Append('\n');
                break;
            }
        }
        sb.Append("Q\n");
        return new ElementAppearance(shape, FormStream(shape, resources, Encoding.ASCII.GetBytes(sb.ToString())), null, NoGlyphs);
    }

    private static string P(double x, double y) => ElementFace.Number(x) + " " + ElementFace.Number(y);

    private static void ShapeEllipse(StringBuilder sb, double x0, double y0, double x1, double y1)
    {
        const double k = 0.5522847498;
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, rx = Math.Max(0, (x1 - x0) / 2), ry = Math.Max(0, (y1 - y0) / 2);
        sb.Append(P(cx + rx, cy)).Append(" m\n");
        sb.Append(P(cx + rx, cy + ry * k)).Append(' ').Append(P(cx + rx * k, cy + ry)).Append(' ').Append(P(cx, cy + ry)).Append(" c\n");
        sb.Append(P(cx - rx * k, cy + ry)).Append(' ').Append(P(cx - rx, cy + ry * k)).Append(' ').Append(P(cx - rx, cy)).Append(" c\n");
        sb.Append(P(cx - rx, cy - ry * k)).Append(' ').Append(P(cx - rx * k, cy - ry)).Append(' ').Append(P(cx, cy - ry)).Append(" c\n");
        sb.Append(P(cx + rx * k, cy - ry)).Append(' ').Append(P(cx + rx, cy - ry * k)).Append(' ').Append(P(cx + rx, cy)).Append(" c h\n");
    }

    /// <summary>Gets the mapping from reading orientation to the page (for /L and /Vertices of the saved annotation).</summary>
    /// <returns>The form's /Matrix combined with the form-to-page mapping.</returns>
    public Matrix UprightToPage()
    {
        var matrix = Matrix.FromArray(Form.Dictionary.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity;
        return matrix * FormToPage();
    }
}
