using System.Runtime.CompilerServices;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>
/// Shading as a fill (8.7.4.5), in shading space; <see cref="ToPage"/> maps it onto the page. The renderer draws linear and
/// radial shadings itself (from sampled colours), function-based shadings as a small image (<see cref="ImageFill"/>) and all
/// others as coloured triangles; tiling patterns (<see cref="TilingFill"/>) are a display list of their own. The colours are
/// opaque; the opacity is stored in <see cref="Alpha"/> and applies to the shading as a whole when drawn (so overlapping mesh
/// patches do not show through each other, and a shading is not rebuilt for every opacity).
/// </summary>
/// <param name="ToPage">Shading space → page; replaced for each place where the same shading is used.</param>
public abstract record ShadingFill(Matrix ToPage)
{
    /// <summary>Clip in shading space (/BBox); null = none.</summary>
    public RectD? BBox { get; init; }

    /// <summary>Opacity (0…255) with which the shading is composited as a whole.</summary>
    public byte Alpha { get; init; } = 255;

    /// <summary>Colour (ARGB) beneath the shading (/Background, only for shading patterns); null = none.</summary>
    public uint? Background { get; init; }

    /// <summary>Rough size in memory in bytes.</summary>
    public abstract long EstimatedBytes { get; }

    /// <summary>
    /// The data (colours, mesh, cell), shared by all places where the same shading or pattern is used (only
    /// <see cref="ToPage"/> differs); counted once in the memory estimate.
    /// </summary>
    public abstract object Data { get; }

    /// <summary>Added around the painted bounds, in page units: edge pixels drawn with smoothing stay inside.</summary>
    private protected const double EdgeMargin = 2;

    /// <summary>
    /// The part of <paramref name="area"/> (page space) the shading actually paints, as a rectangle. A shading paints only where it is
    /// defined (ISO 32000-2:2020, 8.7.4.5): inside its /BBox, between the ends of its axis or its circles where /Extend is not set, on
    /// its /Domain, on its mesh. „sh“ fills only that part of the clip — a shading far off the page costs nothing, and a small one
    /// little (a Type 3 font with a radial shading in each glyph would otherwise fill the whole page once per glyph).
    /// </summary>
    /// <param name="area">The area to fill (the clip on the page).</param>
    /// <returns>The painted part; empty if the shading paints nothing there.</returns>
    public RectD Coverage(RectD area)
    {
        if (!ToPage.IsFinite) return area;
        if (BBox is { } box) area = area.Intersect(ToPage.TransformBounds(box).Inflate(EdgeMargin));
        return area.IsEmpty ? area : Painted(area);
    }

    /// <summary>Within <paramref name="area"/> (page space): the bounds of what this kind of shading paints; the whole area where it is unbounded.</summary>
    private protected virtual RectD Painted(RectD area) => area;
}

/// <summary>Linear (type 2) or radial (type 3) shading: coordinates as in /Coords, colours at the positions 0…1.</summary>
/// <param name="Radial">True for a radial shading.</param>
/// <param name="Coords">/Coords: x0 y0 x1 y1 (linear) or x0 y0 r0 x1 y1 r1 (radial), in shading space.</param>
/// <param name="Colors">Opaque ARGB colours at <paramref name="Positions"/>.</param>
/// <param name="Positions">Positions 0…1 along the axis, one per colour.</param>
/// <param name="ExtendStart">/Extend for the start.</param>
/// <param name="ExtendEnd">/Extend for the end.</param>
/// <param name="ToPage">Shading space → page.</param>
public sealed record GradientFill(bool Radial, double[] Coords, uint[] Colors, float[] Positions, bool ExtendStart, bool ExtendEnd, Matrix ToPage)
    : ShadingFill(ToPage)
{
    /// <inheritdoc/>
    public override long EstimatedBytes => 64 + Colors.Length * 8L + Coords.Length * 8L;

    /// <inheritdoc/>
    public override object Data => Colors;

    /// <summary>
    /// Whether the shading paints nothing at all, also with /Extend (ISO 32000-2, 8.7.4.5.3 and 8.7.4.5.4): an axis whose ends coincide,
    /// both radii 0, or the same circle twice (blend circles of no width). Negative radii count as 0, as everywhere here.
    /// </summary>
    public bool PaintsNothing
    {
        get
        {
            var c = Coords;
            if (!Radial) return c.Length < 4 || (c[0] == c[2] && c[1] == c[3]);
            if (c.Length < 6) return true;
            double r0 = Math.Max(0, c[2]), r1 = Math.Max(0, c[5]);
            return (r0 == 0 && r1 == 0) || (c[0] == c[3] && c[1] == c[4] && r0 == r1);
        }
    }

    /// <summary>
    /// Radial without /Extend: within the two circles' bounds (every circle in between lies in their hull). Linear: the band between
    /// the ends of the axis, open where /Extend is set — the area is cut at the closed ends in shading space. Nothing where the shading
    /// <see cref="PaintsNothing"/>.
    /// </summary>
    private protected override RectD Painted(RectD area)
    {
        var c = Coords;
        if (PaintsNothing) return RectD.Empty;
        if (Radial)
        {
            if (ExtendStart || ExtendEnd || c.Length < 6) return area;
            double r0 = Math.Max(0, c[2]), r1 = Math.Max(0, c[5]);
            var circles = RectD.FromLtrb(Math.Min(c[0] - r0, c[3] - r1), Math.Min(c[1] - r0, c[4] - r1),
                Math.Max(c[0] + r0, c[3] + r1), Math.Max(c[1] + r0, c[4] + r1));
            return area.Intersect(ToPage.TransformBounds(circles).Inflate(EdgeMargin));
        }
        if (ExtendStart && ExtendEnd || c.Length < 4 || !ToPage.IsInvertible) return area;
        double dx = c[2] - c[0], dy = c[3] - c[1];
        if (dx * dx + dy * dy <= 0) return area;
        var toShading = ToPage.Invert();
        var polygon = new List<(double X, double Y)>(8)
        {
            toShading.Transform(area.Left, area.Top), toShading.Transform(area.Right, area.Top),
            toShading.Transform(area.Right, area.Bottom), toShading.Transform(area.Left, area.Bottom),
        };
        if (!ExtendStart) polygon = Cut(polygon, p => (p.X - c[0]) * dx + (p.Y - c[1]) * dy);
        if (!ExtendEnd) polygon = Cut(polygon, p => (c[2] - p.X) * dx + (c[3] - p.Y) * dy);
        if (polygon.Count == 0) return RectD.Empty;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var (x, y) in polygon)
        {
            var (px, py) = ToPage.Transform(x, y);
            (left, right) = (Math.Min(left, px), Math.Max(right, px));
            (top, bottom) = (Math.Min(top, py), Math.Max(bottom, py));
        }
        return area.Intersect(RectD.FromLtrb(left, top, right, bottom).Inflate(EdgeMargin));
    }

    /// <summary>The part of a convex polygon where <paramref name="side"/> is not negative (one step of Sutherland–Hodgman).</summary>
    private static List<(double X, double Y)> Cut(List<(double X, double Y)> polygon, Func<(double X, double Y), double> side)
    {
        var kept = new List<(double X, double Y)>(polygon.Count + 1);
        for (var i = 0; i < polygon.Count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            double sa = side(a), sb = side(b);
            if (sa >= 0) kept.Add(a);
            if (sa >= 0 != sb >= 0)
            {
                var t = sa / (sa - sb);
                kept.Add((a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
        }
        return kept;
    }
}

/// <summary>Triangles with a colour per corner (Gouraud): three points (x, y alternating) and three colours each.</summary>
/// <param name="Points">Six coordinates per triangle, in shading space.</param>
/// <param name="Colors">Three opaque ARGB colours per triangle.</param>
/// <param name="ToPage">Shading space → page.</param>
public sealed record MeshFill(float[] Points, uint[] Colors, Matrix ToPage) : ShadingFill(ToPage)
{
    /// <inheritdoc/>
    public override long EstimatedBytes => 64 + Points.Length * 4L + Colors.Length * 4L;

    /// <summary>
    /// The area of the triangles added up, in shading space (on the page: times the determinant of <see cref="ShadingFill.ToPage"/>).
    /// Drawing costs what the triangles cover; the display list counts it towards the work of the page (docs/FEHLERGEDAECHTNIS.md, No. 762).
    /// </summary>
    public double Area { get; init; }

    /// <inheritdoc/>
    public override object Data => Colors;

    // Bounds of the triangles in shading space, computed once per mesh (copies at other places share the points)
    private static readonly ConditionalWeakTable<float[], StrongBox<RectD?>> PointBounds = new();

    /// <summary>The bounds of the triangles; nothing for a mesh without any.</summary>
    private protected override RectD Painted(RectD area)
    {
        var bounds = PointBounds.GetValue(Points, points =>
        {
            float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
            for (var i = 0; i + 1 < points.Length; i += 2)
            {
                if (!float.IsFinite(points[i]) || !float.IsFinite(points[i + 1])) continue;
                (left, right) = (Math.Min(left, points[i]), Math.Max(right, points[i]));
                (top, bottom) = (Math.Min(top, points[i + 1]), Math.Max(bottom, points[i + 1]));
            }
            return new StrongBox<RectD?>(left <= right ? RectD.FromLtrb(left, top, right, bottom) : null);
        }).Value;
        return bounds is { } box ? area.Intersect(ToPage.TransformBounds(box).Inflate(EdgeMargin)) : RectD.Empty;
    }
}

/// <summary>
/// Function-based shading (type 1) as a small image: <see cref="Samples"/> × <see cref="Samples"/> function values at the
/// grid points of /Domain (<paramref name="X0"/>…<paramref name="X1"/>, <paramref name="Y0"/>…<paramref name="Y1"/>),
/// drawn with bilinear smoothing and clipped to /Domain, as in widely used readers (instead of 8,192 triangles per shading).
/// <paramref name="Matrix"/>: /Matrix of the shading (/Domain space → shading space); <paramref name="ToPage"/> as for all shadings
/// (replaced for each place, hence kept separate).
/// </summary>
/// <param name="Pixels">Opaque ARGB values, row by row from <paramref name="Y0"/>, <see cref="Samples"/> per row.</param>
/// <param name="X0">Start of /Domain in x.</param>
/// <param name="X1">End of /Domain in x.</param>
/// <param name="Y0">Start of /Domain in y.</param>
/// <param name="Y1">End of /Domain in y.</param>
/// <param name="Matrix">/Matrix of the shading.</param>
/// <param name="ToPage">Shading space → page.</param>
public sealed record ImageFill(uint[] Pixels, double X0, double X1, double Y0, double Y1, Matrix Matrix, Matrix ToPage) : ShadingFill(ToPage)
{
    /// <summary>Samples per axis.</summary>
    public const int Samples = 65;

    /// <inheritdoc/>
    public override long EstimatedBytes => 64 + Pixels.Length * 4L;

    /// <inheritdoc/>
    public override object Data => Pixels;

    /// <summary>/Domain, placed by /Matrix (the image is clipped to it).</summary>
    private protected override RectD Painted(RectD area)
    {
        var domain = RectD.FromLtrb(Math.Min(X0, X1), Math.Min(Y0, Y1), Math.Max(X0, X1), Math.Max(Y0, Y1));
        return area.Intersect(ToPage.TransformBounds(Matrix.TransformBounds(domain)).Inflate(EdgeMargin));
    }
}

/// <summary>
/// Tiling pattern (PatternType 1, 8.7.3.3): the cell as a display list of its own in pattern space, clipped to
/// <paramref name="Box"/>, repeated at intervals of <paramref name="XStep"/> × <paramref name="YStep"/>.
/// </summary>
/// <param name="Cell">The content of one cell.</param>
/// <param name="Box">/BBox of the cell in pattern space.</param>
/// <param name="XStep">Horizontal spacing (positive).</param>
/// <param name="YStep">Vertical spacing (positive).</param>
/// <param name="Color">Uncoloured pattern (PaintType 2): the colour including opacity in which the cell appears; null = coloured.</param>
/// <param name="ToPage">Pattern space → page.</param>
public sealed record TilingFill(DisplayList Cell, RectD Box, double XStep, double YStep, uint? Color, Matrix ToPage) : ShadingFill(ToPage)
{
    /// <inheritdoc/>
    public override long EstimatedBytes => 128 + Cell.EstimatedBytes;

    /// <inheritdoc/>
    public override object Data => Cell;
}

/// <summary>Builds <see cref="ShadingFill"/> objects from PDF shadings (types 1 to 7).</summary>
public static class Shadings
{
    /// <summary>At most this many triangles per shading (crafted files: millions of patches).</summary>
    public const int MaxTriangles = 400_000;

    /// <summary>
    /// At most this much area (pt², the triangles added up) per mesh shading: 64 sheets of A0. A mesh covers its area about once, a folded
    /// one a few times; beyond it lies only work (<see cref="MeshBuilder.Build"/>).
    /// </summary>
    public const double MaxMeshArea = 64 * 2384.0 * 3370.0;

    private const int GradientStops = 256;

    /// <summary>
    /// Whether the fineness depends on the size on the page (types 6/7); a built shading then applies only to similar sizes,
    /// whereas all others can be reused for every place (only <see cref="ShadingFill.ToPage"/> changes).
    /// </summary>
    /// <param name="obj">The shading dictionary or stream.</param>
    /// <returns>True for shading types 6 and 7.</returns>
    public static bool ScaleDependent(PdfObject? obj) => obj?.AsDictionary()?.GetInteger(PdfName.Get("ShadingType")) is 6 or 7;

    /// <summary>
    /// Builds a shading (opaque; opacity through <see cref="ShadingFill.Alpha"/>).
    /// </summary>
    /// <param name="obj">The shading dictionary or stream.</param>
    /// <param name="doc">The document.</param>
    /// <param name="resources">Resources for named colour spaces; may be null.</param>
    /// <param name="toPage">Shading space → page; the fineness of the patches follows their size on the page.</param>
    /// <returns>The fill; null if the shading cannot be read or is invalid (the caller then uses the fallback colour).</returns>
    /// <exception cref="PdfLimitException">A limit was exceeded; passed on so that redaction aborts the page instead of skipping it.</exception>
    public static ShadingFill? Create(PdfObject? obj, PdfDocument doc, PdfDictionary? resources, Matrix toPage,
        byte intent = ColorSpace.RelativeColorimetric, byte bpc = ColorSpace.BpcDefault)
    {
        var stream = obj as PdfStream;
        if ((stream?.Dictionary ?? obj?.AsDictionary()) is not { } dict) return null;
        try
        {
            // the variant for the rendering intent and black point compensation of the state (docs/FEHLERGEDAECHTNIS.md, No. 573)
            var space = ColorSpace.Create(dict.Get(PdfName.Get("ColorSpace")) is { } cs and not PdfNull ? cs : PdfName.Get("DeviceGray"), resources, doc)
                .WithRendering(intent, bpc);
            var function = dict.Get(PdfName.Get("Function")) is { } fn and not PdfNull ? PdfFunction.CreateArray(fn, doc) : null;
            if (Invalid(dict, space, function)) return null;
            var colors = new Colorizer(space, function);
            ShadingFill? fill = (int)(dict.GetInteger(PdfName.Get("ShadingType")) ?? 0) switch
            {
                1 => FunctionBased(dict, colors, toPage),
                2 or 3 => Gradient(dict, colors, toPage),
                4 when stream is not null => FreeForm(stream, doc, colors, toPage),
                5 when stream is not null => Lattice(stream, doc, colors, toPage),
                6 or 7 when stream is not null => Patches(stream, doc, colors, toPage, tensor: dict.GetInteger(PdfName.Get("ShadingType")) == 7),
                _ => null,
            };
            if (fill is not null && PdfFunction.Numbers(dict.GetArray(PdfName.Get("BBox"))) is [var x0, var y0, var x1, var y1])
                fill = fill with { BBox = RectD.FromLtrb(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1)) };
            return fill;
        }
        // limits (PdfLimitException) are passed on: when redacting they abort the page instead of silently skipping it
        catch (Exception e) when (e is (PdfException and not PdfLimitException) or ArgumentException or IndexOutOfRangeException or InvalidDataException
            or OverflowException)
        {
            return null;   // broken shading: the caller uses the fallback colour
        }
    }

    /// <summary>
    /// Invalid according to the standard (8.7.4.5): the function does not have exactly one input (type 1: two) or has fewer outputs
    /// than the colour space has components, or types 1–3 have no function — no reader draws such shadings (not even a fallback
    /// colour). A crafted function with 18 inputs cost 2^18 steps per evaluation.
    /// </summary>
    /// <param name="dict">The shading dictionary.</param>
    /// <param name="space">Its colour space.</param>
    /// <param name="function">Its function; null if it has none.</param>
    /// <returns>True if the shading is invalid.</returns>
    public static bool Invalid(PdfDictionary dict, ColorSpace space, PdfFunction? function)
    {
        if (function is null) return dict.GetInteger(PdfName.Get("ShadingType")) is 1 or 2 or 3;   // these need a function
        var inputs = dict.GetInteger(PdfName.Get("ShadingType")) == 1 ? 2 : 1;
        return function.Inputs != inputs || function.Outputs < space.Components;
    }

    /// <summary>Like <see cref="Invalid(PdfDictionary, ColorSpace, PdfFunction?)"/>, from the object (for the fallback of unreadable shadings).</summary>
    /// <param name="obj">The shading dictionary or stream.</param>
    /// <param name="doc">The document.</param>
    /// <param name="resources">Resources for named colour spaces; may be null.</param>
    /// <returns>True if the shading is invalid or cannot be read.</returns>
    public static bool Invalid(PdfObject? obj, PdfDocument doc, PdfDictionary? resources)
    {
        if (obj?.AsDictionary() is not { } dict) return true;
        try
        {
            var space = ColorSpace.Create(dict.Get(PdfName.Get("ColorSpace")) is { } cs and not PdfNull ? cs : PdfName.Get("DeviceGray"), resources, doc);
            var function = dict.Get(PdfName.Get("Function")) is { } fn and not PdfNull ? PdfFunction.CreateArray(fn, doc) : null;
            return Invalid(dict, space, function);
        }
        catch (Exception e) when (e is PdfException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>Colour from components or (with a function) from the parameter t or (x, y).</summary>
    private sealed class Colorizer(ColorSpace space, PdfFunction? function)
    {
        public int Inputs => function is null ? Math.Max(space.Components, 1) : 1;

        public uint FromComponents(ReadOnlySpan<double> c)
        {
            if (function is null) return space.ToRgb(c).ToArgb(1);
            Span<double> output = stackalloc double[Math.Clamp(function.Outputs, 1, 32)];
            function.Evaluate(c, output);
            return space.ToRgb(output[..Math.Min(output.Length, Math.Max(space.Components, 1))]).ToArgb(1);
        }

        public uint FromParameter(double t) => FromComponents([t]);

        /// <summary>
        /// Interpolating the finished colours is exact enough: a device space without a function (ISO 32000-2, 8.7.4.4 allows converting
        /// before or after the interpolation there). With a function only t may be interpolated (8.7.4.5.5, 8.7.4.5.6), in CIE-based and
        /// other non-linear spaces the conversion comes after it (docs/FEHLERGEDAECHTNIS.md, No. 569). An /Indexed space too: its values are
        /// entries of a palette, the colours between two of them are blended, not the entries in between (No. 595).
        /// </summary>
        public bool Linear { get; } = function is null
            && (ReferenceEquals(space, ColorSpace.DeviceGray) || ReferenceEquals(space, ColorSpace.DeviceRgb) || space.IsDeviceCmyk || space.IsIndexed);

        public MeshVertex Vertex((float X, float Y) p, double[] c) => new(p, c, FromComponents(c));

        public MeshVertex Middle(in MeshVertex a, in MeshVertex b)
        {
            var c = new double[a.C.Length];
            for (var i = 0; i < c.Length; i++) c[i] = (a.C[i] + b.C[i]) / 2;
            return new(((a.P.X + b.P.X) / 2, (a.P.Y + b.P.Y) / 2), c, FromComponents(c));
        }
    }

    /// <summary>A vertex of a free-form or lattice mesh: position, its values (t or the components) and the finished colour.</summary>
    private readonly record struct MeshVertex((float X, float Y) P, double[] C, uint Argb);

    /// <summary>Deepest subdivision of one triangle (4⁶ = 4,096 parts at most).</summary>
    private const int MaxSplitDepth = 6;

    /// <summary>
    /// A triangle of a type 4 or 5 mesh. Unless <see cref="Colorizer.Linear"/>, split into four while the true colour at the edge midpoints
    /// and the centre differs by more than 2 levels from the blend of the corner colours (Gouraud), at most <see cref="MaxSplitDepth"/>
    /// levels deep: a function with several stops between red and blue gives green in the middle, not violet.
    /// </summary>
    private static void Triangle(MeshBuilder mesh, Colorizer colors, in MeshVertex a, in MeshVertex b, in MeshVertex c, int depth = 0)
    {
        if (colors.Linear || depth >= MaxSplitDepth || mesh.Count >= MaxTriangles)
        {
            mesh.Triangle(a.P, a.Argb, b.P, b.Argb, c.P, c.Argb);
            return;
        }
        var ab = colors.Middle(a, b);
        var bc = colors.Middle(b, c);
        var ca = colors.Middle(c, a);
        if (Close(ab.Argb, a.Argb, b.Argb) && Close(bc.Argb, b.Argb, c.Argb) && Close(ca.Argb, c.Argb, a.Argb) && Close(colors.Middle(ab, c).Argb, ab.Argb, c.Argb))
        {
            mesh.Triangle(a.P, a.Argb, b.P, b.Argb, c.P, c.Argb);
            return;
        }
        Triangle(mesh, colors, a, ab, ca, depth + 1);
        Triangle(mesh, colors, ab, b, bc, depth + 1);
        Triangle(mesh, colors, ca, bc, c, depth + 1);
        Triangle(mesh, colors, ab, bc, ca, depth + 1);

        // the true colour against the mean of two finished colours, per channel
        static bool Close(uint actual, uint x, uint y)
        {
            for (var shift = 0; shift < 24; shift += 8)
            {
                var mean = (((x >> shift) & 0xFF) + ((y >> shift) & 0xFF)) / 2.0;
                if (Math.Abs(((actual >> shift) & 0xFF) - mean) > 2) return false;
            }
            return true;
        }
    }

    // ------------------------------------------------------------ Types 2 and 3

    private static GradientFill? Gradient(PdfDictionary dict, Colorizer colors, Matrix toPage)
    {
        var radial = dict.GetInteger(PdfName.Get("ShadingType")) == 3;
        var coords = PdfFunction.Numbers(dict.GetArray(PdfName.Get("Coords")));
        if (coords is null || coords.Length < (radial ? 6 : 4) || coords.Any(c => !double.IsFinite(c))) return null;
        var domain = PdfFunction.Numbers(dict.GetArray(PdfName.Get("Domain"))) is [var d0, var d1] ? (d0, d1) : (0.0, 1.0);
        var extend = dict.GetArray(PdfName.Get("Extend"));
        var stops = new uint[GradientStops];
        var positions = new float[GradientStops];
        for (var i = 0; i < GradientStops; i++)
        {
            var s = i / (double)(GradientStops - 1);
            positions[i] = (float)s;
            stops[i] = colors.FromParameter(domain.Item1 + (domain.Item2 - domain.Item1) * s);
        }
        return new GradientFill(radial, coords, stops, positions, extend?.Get(0) is PdfBoolean { Value: true }, extend?.Get(1) is PdfBoolean { Value: true }, toPage);
    }

    // ------------------------------------------------------------ Type 1: function of (x, y)

    private static ImageFill FunctionBased(PdfDictionary dict, Colorizer colors, Matrix toPage)
    {
        var domain = PdfFunction.Numbers(dict.GetArray(PdfName.Get("Domain"))) is [var x0, var x1, var y0, var y1] ? (x0, x1, y0, y1) : (0.0, 1.0, 0.0, 1.0);
        var matrix = Matrix.FromArray(dict.GetArray(PdfName.Get("Matrix"))) ?? Matrix.Identity;
        const int Size = ImageFill.Samples;
        var pixels = new uint[Size * Size];
        for (var j = 0; j < Size; j++)
        for (var i = 0; i < Size; i++)
        {
            double x = domain.Item1 + (domain.Item2 - domain.Item1) * i / (Size - 1), y = domain.Item3 + (domain.Item4 - domain.Item3) * j / (Size - 1);
            pixels[j * Size + i] = colors.FromComponents([x, y]);
        }
        return new ImageFill(pixels, domain.Item1, domain.Item2, domain.Item3, domain.Item4, matrix, toPage);
    }

    // ------------------------------------------------------------ Meshes from bits (types 4 to 7)

    /// <summary>Reads numbers of any bit count (8.7.4.5.5); /Decode maps them onto their range.</summary>
    private sealed class MeshReader(byte[] data, int bitsPerCoordinate, int bitsPerComponent, int bitsPerFlag, double[] decode, int components)
    {
        private long _bit;
        private long _points;

        /// <summary>
        /// More data remains, and no more points have been read than the triangle limit allows. All points read are counted,
        /// not only the accepted triangles; otherwise a file whose triangles are all discarded (/Decode up to
        /// 1e39) would run through the whole stream.
        /// </summary>
        public bool HasData => _bit + Math.Max(bitsPerFlag, 1) <= data.Length * 8L && _points <= MaxPoints;

        private const long MaxPoints = 4L * MaxTriangles;

        private ulong Read(int bits)
        {
            ulong value = 0;
            for (var i = 0; i < bits; i++)
            {
                if (_bit >= data.Length * 8L) throw new InvalidDataException("Verlauf zu kurz");
                value = (value << 1) | (uint)((data[_bit >> 3] >> (7 - (int)(_bit & 7))) & 1);
                _bit++;
            }
            return value;
        }

        private static double Scale(ulong raw, int bits, double min, double max) =>
            min + raw * (max - min) / (bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1);

        /// <summary>The edge flag: only its two lowest bits count (ISO 32000-2, Tables 81, 83 and 84; docs/FEHLERGEDAECHTNIS.md, No. 761).</summary>
        public int Flag() => (int)Read(bitsPerFlag) & 3;

        public (float X, float Y) Point()
        {
            _points++;
            var x = Scale(Read(bitsPerCoordinate), bitsPerCoordinate, decode[0], decode[1]);
            var y = Scale(Read(bitsPerCoordinate), bitsPerCoordinate, decode[2], decode[3]);
            return ((float)x, (float)y);
        }

        public double[] Components()
        {
            var c = new double[components];
            for (var i = 0; i < components; i++)
                c[i] = Scale(Read(bitsPerComponent), bitsPerComponent, decode[4 + 2 * i], decode[5 + 2 * i]);
            return c;
        }

        /// <summary>Continues at the next whole byte (after every vertex or patch, as in widely used readers).</summary>
        public void Align() => _bit = (_bit + 7) & ~7L;
    }

    private static MeshReader? Reader(PdfStream stream, PdfDocument doc, Colorizer colors, bool flags)
    {
        var d = stream.Dictionary;
        var coordinate = (int)(d.GetInteger(PdfName.Get("BitsPerCoordinate")) ?? 0);
        var component = (int)(d.GetInteger(PdfName.Get("BitsPerComponent")) ?? 0);
        var flag = flags ? (int)(d.GetInteger(PdfName.Get("BitsPerFlag")) ?? 0) : 0;
        if (coordinate is < 1 or > 32 || component is < 1 or > 16 || flag is < 0 or > 8) return null;
        var decode = PdfFunction.Numbers(d.GetArray(PdfName.Get("Decode")));
        if (decode is null || decode.Length < 4 + 2 * colors.Inputs || decode.Any(v => !double.IsFinite(v))) return null;
        return new MeshReader(doc.GetStreamData(stream), coordinate, component, flag, decode, colors.Inputs);
    }

    /// <summary>Type 4: triangles with flags — 0 new triangle, 1 at edge BC, 2 at edge AC.</summary>
    private static MeshFill? FreeForm(PdfStream stream, PdfDocument doc, Colorizer colors, Matrix toPage)
    {
        if (Reader(stream, doc, colors, flags: true) is not { } reader) return null;
        var mesh = new MeshBuilder();
        var last = new MeshVertex[3];   // last triangle A, B, C
        var fresh = new MeshVertex[3];
        var pending = 0;      // points of a new triangle
        var any = false;      // a triangle exists already (otherwise every flag counts as 0)
        while (reader.HasData && mesh.Count < MaxTriangles)
        {
            int flag;
            MeshVertex v;
            try
            {
                flag = reader.Flag();
                var p = reader.Point();
                v = colors.Vertex(p, reader.Components());
            }
            catch (InvalidDataException)
            {
                break;   // truncated data: what is there counts
            }
            reader.Align();
            if (pending > 0 || flag == 0 || !any)
            {
                // new triangle: three points in a row (the flags of the second and third do not count)
                fresh[pending++] = v;
                if (pending < 3) continue;
                pending = 0;
                (last[0], last[1], last[2]) = (fresh[0], fresh[1], fresh[2]);
            }
            else if (flag == 1) (last[0], last[1], last[2]) = (last[1], last[2], v);   // at edge BC
            else (last[0], last[1], last[2]) = (last[0], last[2], v);                  // at edge AC
            any = true;
            Triangle(mesh, colors, last[0], last[1], last[2]);
        }
        return mesh.Build(toPage);
    }

    /// <summary>Type 5: points in rows of /VerticesPerRow; each pair of rows forms triangles.</summary>
    private static MeshFill? Lattice(PdfStream stream, PdfDocument doc, Colorizer colors, Matrix toPage)
    {
        var perRow = stream.Dictionary.GetInteger(PdfName.Get("VerticesPerRow")) ?? 0;
        // triangles only arise after a full row, so with huge rows the limit would never apply (an 8 KB file took 538 MB)
        if (perRow < 2 || perRow > MaxTriangles / 2 + 1 || Reader(stream, doc, colors, flags: false) is not { } reader) return null;
        var mesh = new MeshBuilder();
        var previous = new List<MeshVertex>();
        var row = new List<MeshVertex>();
        while (reader.HasData && mesh.Count < MaxTriangles)
        {
            try
            {
                row.Add(colors.Vertex(reader.Point(), reader.Components()));
            }
            catch (InvalidDataException)
            {
                break;
            }
            if (row.Count < perRow) continue;
            if (previous.Count == perRow)
                for (var i = 0; i + 1 < perRow; i++)
                {
                    Triangle(mesh, colors, previous[i], previous[i + 1], row[i]);
                    Triangle(mesh, colors, previous[i + 1], row[i + 1], row[i]);
                }
            (previous, row) = (row, previous);
            row.Clear();
        }
        return mesh.Build(toPage);
    }

    /// <summary>
    /// Type 6 (Coons) and 7 (tensor): patches from Bézier edges, colours at the corners. Edge in the order of the standard
    /// (p00 p01 p02 p03 p13 p23 p33 p32 p31 p30 p20 p10, tensor additionally p11 p12 p22 p21); flags 1–3 take over an
    /// edge of the previous patch. Evaluated as a tensor patch (Coons: interior points by the formula of the standard), split into
    /// a grid of triangles — finer the larger the patch is on the page.
    /// </summary>
    private static MeshFill? Patches(PdfStream stream, PdfDocument doc, Colorizer colors, Matrix toPage, bool tensor)
    {
        if (Reader(stream, doc, colors, flags: true) is not { } reader) return null;
        var mesh = new MeshBuilder();
        var boundary = new (float X, float Y)[12];
        var interior = new (float X, float Y)[4];
        var corner = new double[4][];
        var first = true;
        var pageScale = Math.Max(toPage.MeanScale, 1e-6);
        while (reader.HasData && mesh.Count < MaxTriangles)
        {
            try
            {
                var flag = reader.Flag();
                if (flag != 0 && first) flag = 0;   // first patch without a predecessor: treated as new
                var previous = ((float X, float Y)[])boundary.Clone();
                var previousColors = (double[][])corner.Clone();
                var start = 0;
                if (flag is >= 1 and <= 3)
                {
                    for (var i = 0; i < 4; i++) boundary[i] = previous[(3 * flag + i) % 12];
                    corner[0] = previousColors[flag];
                    corner[1] = previousColors[(flag + 1) % 4];
                    start = 4;
                }
                for (var i = start; i < 12; i++) boundary[i] = reader.Point();
                if (tensor)
                    for (var i = 0; i < 4; i++) interior[i] = reader.Point();
                for (var i = start == 0 ? 0 : 2; i < 4; i++) corner[i] = reader.Components();
                reader.Align();
                first = false;
                Tessellate(mesh, boundary, tensor ? interior : null, corner, colors, pageScale);
            }
            catch (InvalidDataException)
            {
                break;
            }
        }
        return mesh.Build(toPage);
    }

    private static void Tessellate(MeshBuilder mesh, (float X, float Y)[] b, (float X, float Y)[]? interior, double[][] corner, Colorizer colors, double pageScale)
    {
        // 4×4 control points p[row v][column u]
        var p = new (double X, double Y)[4, 4];
        p[0, 0] = b[0]; p[0, 1] = b[1]; p[0, 2] = b[2]; p[0, 3] = b[3];
        p[1, 3] = b[4]; p[2, 3] = b[5]; p[3, 3] = b[6];
        p[3, 2] = b[7]; p[3, 1] = b[8]; p[3, 0] = b[9];
        p[2, 0] = b[10]; p[1, 0] = b[11];
        if (interior is not null)
        {
            p[1, 1] = interior[0]; p[1, 2] = interior[1]; p[2, 2] = interior[2]; p[2, 1] = interior[3];
        }
        else
        {
            // Coons → Tensor (8.7.4.5.8)
            (double X, double Y) Mix(int a0, int a1, (int, int)[] six, (int, int)[] two, (int, int)[] three, (int, int) one)
            {
                double x = -4 * p[a0, a1].X, y = -4 * p[a0, a1].Y;
                foreach (var (i, j) in six) { x += 6 * p[i, j].X; y += 6 * p[i, j].Y; }
                foreach (var (i, j) in two) { x -= 2 * p[i, j].X; y -= 2 * p[i, j].Y; }
                foreach (var (i, j) in three) { x += 3 * p[i, j].X; y += 3 * p[i, j].Y; }
                x -= p[one.Item1, one.Item2].X;
                y -= p[one.Item1, one.Item2].Y;
                return (x / 9, y / 9);
            }
            p[1, 1] = Mix(0, 0, [(0, 1), (1, 0)], [(0, 3), (3, 0)], [(3, 1), (1, 3)], (3, 3));
            p[1, 2] = Mix(0, 3, [(0, 2), (1, 3)], [(0, 0), (3, 3)], [(3, 2), (1, 0)], (3, 0));
            p[2, 1] = Mix(3, 0, [(3, 1), (2, 0)], [(3, 3), (0, 0)], [(0, 1), (2, 3)], (0, 3));
            p[2, 2] = Mix(3, 3, [(3, 2), (2, 3)], [(3, 0), (0, 3)], [(0, 2), (2, 0)], (0, 0));
        }

        // fineness by the size on the page: about one grid step per 6 points, 2 to 24
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var q in p)
        {
            minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
            minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
        }
        var size = Math.Max(maxX - minX, maxY - minY) * pageScale;
        var n = double.IsFinite(size) ? (int)Math.Clamp(Math.Ceiling(size / 6), 2, 24) : 2;

        static double Bernstein(int i, double t) => i switch
        {
            0 => (1 - t) * (1 - t) * (1 - t),
            1 => 3 * t * (1 - t) * (1 - t),
            2 => 3 * t * t * (1 - t),
            _ => t * t * t,
        };

        var points = new (float X, float Y)[(n + 1) * (n + 1)];
        var values = new uint[points.Length];
        var components = corner[0].Length;
        var mixed = new double[components];
        for (var row = 0; row <= n; row++)
        for (var col = 0; col <= n; col++)
        {
            double u = col / (double)n, v = row / (double)n, x = 0, y = 0;
            for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
            {
                var w = Bernstein(i, v) * Bernstein(j, u);
                x += w * p[i, j].X;
                y += w * p[i, j].Y;
            }
            // colours bilinear: c00 at p00 (u0 v0), c03 at p03 (u1 v0), c33 at p33 (u1 v1), c30 at p30 (u0 v1)
            for (var k = 0; k < components; k++)
                mixed[k] = (1 - u) * (1 - v) * corner[0][k] + u * (1 - v) * corner[1][k] + u * v * corner[2][k] + (1 - u) * v * corner[3][k];
            points[row * (n + 1) + col] = ((float)x, (float)y);
            values[row * (n + 1) + col] = colors.FromComponents(mixed);
        }
        for (var row = 0; row < n; row++)
        for (var col = 0; col < n; col++)
        {
            int a = row * (n + 1) + col, b2 = a + 1, c = a + n + 1, d = c + 1;
            mesh.Triangle(points[a], values[a], points[b2], values[b2], points[c], values[c]);
            mesh.Triangle(points[b2], values[b2], points[d], values[d], points[c], values[c]);
        }
    }

    /// <summary>Collects triangles (up to <see cref="MaxTriangles"/>); triangles with non-finite coordinates are dropped.</summary>
    private sealed class MeshBuilder
    {
        private readonly List<float> _points = [];
        private readonly List<uint> _colors = [];

        public int Count => _colors.Count / 3;

        public void Triangle((float X, float Y) a, uint ca, (float X, float Y) b, uint cb, (float X, float Y) c, uint cc)
        {
            if (Count >= MaxTriangles || !float.IsFinite(a.X + a.Y + b.X + b.Y + c.X + c.Y)) return;
            _points.Add(a.X); _points.Add(a.Y); _points.Add(b.X); _points.Add(b.Y); _points.Add(c.X); _points.Add(c.Y);
            _colors.Add(ca); _colors.Add(cb); _colors.Add(cc);
        }

        /// <summary>
        /// The mesh in page space; triangles beyond <see cref="MaxMeshArea"/> of area on the page are left out. Drawing costs what the
        /// triangles cover: a crafted mesh of 10,000 triangles each several pages large took 6 s per page (docs/FEHLERGEDAECHTNIS.md, No. 277).
        /// </summary>
        public MeshFill? Build(Matrix toPage)
        {
            if (_colors.Count == 0) return null;
            var scale = Math.Abs(toPage.A * toPage.D - toPage.B * toPage.C);
            double area = 0, own = 0;   // on the page, in shading space (of the triangles kept)
            var keep = Count;
            for (var t = 0; t < Count; t++)
            {
                var p = t * 6;
                var a = Math.Abs((_points[p + 2] - _points[p]) * (double)(_points[p + 5] - _points[p + 1]) -
                                 (_points[p + 4] - _points[p]) * (double)(_points[p + 3] - _points[p + 1])) / 2;
                area += a * scale;
                if (area > MaxMeshArea)
                {
                    keep = Math.Max(1, t);
                    if (t == 0) own = a;
                    break;
                }
                own += a;
            }
            if (!double.IsFinite(own)) own = double.MaxValue;
            return keep == Count
                ? new MeshFill([.. _points], [.. _colors], toPage) { Area = own }
                : new MeshFill([.. _points.GetRange(0, keep * 6)], [.. _colors.GetRange(0, keep * 3)], toPage) { Area = own };
        }
    }
}
