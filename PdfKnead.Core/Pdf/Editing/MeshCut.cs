using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// A mesh shading (types 4–7, ISO 32000-2, 8.7.4.5.5–8.7.4.5.8) without its data under the bars (docs/FEHLERGEDAECHTNIS.md, No. 701; before
/// the clip around it only hid them, No. 455): its triangles as the display draws them (<see cref="Shadings.Create"/>, patches subdivided
/// for their size on the page) are cut at the bars — a triangle beside them stays as it is, one reaching under them loses that part, the
/// colour of a new corner interpolated as the triangle's colour runs (Gouraud) — and written as a free-form triangle mesh (type 4) in
/// DeviceRGB with the colours the display shows. Nothing under the bars is left of the points or the colours.
/// </summary>
internal static class MeshCut
{
    /// <summary>The result: the new shading, or that nothing of it is left beside the bars (then it is not drawn at all).</summary>
    internal readonly record struct Result(PdfStream? Shading)
    {
        /// <summary>Nothing is left: the shading lies under the bars entirely.</summary>
        public bool Empty => Shading is null;
    }

    /// <summary>
    /// <paramref name="shading"/> cut at <paramref name="bars"/> (page space); null if it is no mesh, cannot be read or reaches no bar (it
    /// stays as it is).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="shading">The shading dictionary or stream.</param>
    /// <param name="resources">Resources for named colour spaces.</param>
    /// <param name="toPage">Shading space → page.</param>
    /// <param name="bars">The bars, page space.</param>
    public static Result? Cut(PdfDocument doc, PdfObject? shading, PdfDictionary? resources, Matrix toPage, IReadOnlyList<RectD> bars)
    {
        if (shading is not PdfStream stream || stream.Dictionary.GetInteger(PdfName.Get("ShadingType")) is not (4 or 5 or 6 or 7) || !toPage.IsInvertible)
            return null;
        if (Shadings.Create(stream, doc, resources, toPage) is not MeshFill mesh) return null;
        // the bars in shading space: convex quadrilaterals, counter-clockwise
        var toShading = toPage.Invert();
        var quads = new List<(double X, double Y)[]>(bars.Count);
        foreach (var bar in bars)
        {
            var (l, r) = (Math.Min(bar.Left, bar.Right), Math.Max(bar.Left, bar.Right));
            var (b, t) = (Math.Min(bar.Top, bar.Bottom), Math.Max(bar.Top, bar.Bottom));
            (double X, double Y)[] quad = [toShading.Transform(l, b), toShading.Transform(r, b), toShading.Transform(r, t), toShading.Transform(l, t)];
            if (Area(quad) < 0) Array.Reverse(quad);
            quads.Add(quad);
        }

        var points = mesh.Points;
        var colors = mesh.Colors;
        var kept = new List<Vertex>(points.Length / 2);
        var hit = false;
        for (var i = 0; i + 5 < points.Length; i += 6)
        {
            var c = i / 2;
            List<Vertex> triangle = [new(points[i], points[i + 1], colors[c]), new(points[i + 2], points[i + 3], colors[c + 1]), new(points[i + 4], points[i + 5], colors[c + 2])];
            var pieces = new List<List<Vertex>> { triangle };
            foreach (var quad in quads)
            {
                if (!Overlaps(triangle, quad)) continue;
                var next = new List<List<Vertex>>();
                foreach (var piece in pieces) next.AddRange(Subtract(piece, quad));
                pieces = next;
                hit = true;
            }
            foreach (var piece in pieces)
                for (var k = 1; k + 1 < piece.Count; k++)   // a convex polygon as a fan
                {
                    kept.Add(piece[0]);
                    kept.Add(piece[k]);
                    kept.Add(piece[k + 1]);
                }
        }
        if (!hit) return null;
        if (kept.Count == 0) return new Result(null);
        return new Result(Write(stream.Dictionary, kept));
    }

    /// <summary>A corner: position (shading space) and colour (red, green, blue 0–255).</summary>
    private readonly record struct Vertex(double X, double Y, double R, double G, double B)
    {
        public Vertex(float x, float y, uint argb) : this(x, y, (argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF) { }

        public static Vertex Between(in Vertex a, in Vertex b, double t) =>
            new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
    }

    /// <summary>Twice the signed area of a polygon (positive: counter-clockwise).</summary>
    private static double Area(IReadOnlyList<(double X, double Y)> p)
    {
        double sum = 0;
        for (var i = 0; i < p.Count; i++)
        {
            var (a, b) = (p[i], p[(i + 1) % p.Count]);
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum;
    }

    /// <summary>Whether the bounds of the triangle and of the quadrilateral meet (a quick test; the cut decides exactly).</summary>
    private static bool Overlaps(List<Vertex> triangle, (double X, double Y)[] quad) =>
        triangle.Max(v => v.X) >= quad.Min(p => p.X) && triangle.Min(v => v.X) <= quad.Max(p => p.X) &&
        triangle.Max(v => v.Y) >= quad.Min(p => p.Y) && triangle.Min(v => v.Y) <= quad.Max(p => p.Y);

    /// <summary>
    /// A convex polygon without the convex quadrilateral: the parts outside each of its edges in turn (at most four convex pieces); what lies
    /// inside all of them goes.
    /// </summary>
    private static List<List<Vertex>> Subtract(List<Vertex> polygon, (double X, double Y)[] quad)
    {
        var pieces = new List<List<Vertex>>();
        var rest = polygon;
        for (var e = 0; e < 4 && rest.Count >= 3; e++)
        {
            var (a, b) = (quad[e], quad[(e + 1) % 4]);
            // inside: left of the edge a→b (the quadrilateral is counter-clockwise)
            double Side(Vertex v) => (b.X - a.X) * (v.Y - a.Y) - (b.Y - a.Y) * (v.X - a.X);
            var outside = Clip(rest, Side, keepPositive: false);
            if (outside.Count >= 3 && Math.Abs(PolygonArea(outside)) > 1e-12) pieces.Add(outside);
            rest = Clip(rest, Side, keepPositive: true);
        }
        return pieces;
    }

    private static double PolygonArea(List<Vertex> p) => Area([.. p.Select(v => (v.X, v.Y))]);

    /// <summary>The part of a convex polygon on one side of a line (Sutherland–Hodgman); points on the line belong to both sides.</summary>
    private static List<Vertex> Clip(List<Vertex> polygon, Func<Vertex, double> side, bool keepPositive)
    {
        var result = new List<Vertex>(polygon.Count + 2);
        for (var i = 0; i < polygon.Count; i++)
        {
            var (current, next) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            var (sc, sn) = (side(current), side(next));
            if (!keepPositive) (sc, sn) = (-sc, -sn);
            if (sc >= 0) result.Add(current);
            if (sc > 0 && sn < 0 || sc < 0 && sn > 0) result.Add(Vertex.Between(current, next, sc / (sc - sn)));
        }
        return result;
    }

    /// <summary>The triangles as a free-form mesh (type 4): 32-bit coordinates over their bounds, 8-bit RGB, an 8-bit flag 0 per corner.</summary>
    private static PdfStream Write(PdfDictionary original, List<Vertex> vertices)
    {
        double x0 = vertices.Min(v => v.X), x1 = vertices.Max(v => v.X), y0 = vertices.Min(v => v.Y), y1 = vertices.Max(v => v.Y);
        if (x1 - x0 < 1e-9) x1 = x0 + 1;
        if (y1 - y0 < 1e-9) y1 = y0 + 1;
        var data = new byte[vertices.Count * 12];
        var at = 0;
        foreach (var v in vertices)
        {
            data[at++] = 0;   // every triangle on its own (the flags of its second and third corner are read and not used)
            Coordinate((v.X - x0) / (x1 - x0));
            Coordinate((v.Y - y0) / (y1 - y0));
            data[at++] = (byte)Math.Clamp(Math.Round(v.R), 0, 255);
            data[at++] = (byte)Math.Clamp(Math.Round(v.G), 0, 255);
            data[at++] = (byte)Math.Clamp(Math.Round(v.B), 0, 255);
        }
        void Coordinate(double t)
        {
            var value = (uint)Math.Clamp(Math.Round(t * uint.MaxValue), 0, uint.MaxValue);
            data[at++] = (byte)(value >> 24);
            data[at++] = (byte)(value >> 16);
            data[at++] = (byte)(value >> 8);
            data[at++] = (byte)value;
        }
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("ShadingType"), PdfInteger.Of(4));
        dict.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceRGB"));
        dict.Set(PdfName.Get("BitsPerCoordinate"), PdfInteger.Of(32));
        dict.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(8));
        dict.Set(PdfName.Get("BitsPerFlag"), PdfInteger.Of(8));
        dict.Set(PdfName.Get("Decode"), new PdfArray([new PdfReal(x0), new PdfReal(x1), new PdfReal(y0), new PdfReal(y1),
            PdfInteger.Of(0), PdfInteger.Of(1), PdfInteger.Of(0), PdfInteger.Of(1), PdfInteger.Of(0), PdfInteger.Of(1)]));
        // what applies to the area: the box and smoothing; /Background is in the old colour space and only for patterns
        foreach (var key in (ReadOnlySpan<string>)["BBox", "AntiAlias"])
            if (original.Get(PdfName.Get(key)) is { } value and not PdfNull) dict.Set(PdfName.Get(key), value);
        return new PdfStream(dict, data);
    }
}
