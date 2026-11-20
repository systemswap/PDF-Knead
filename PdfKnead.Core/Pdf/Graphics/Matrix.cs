namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>
/// Affine transformation as in PDF (ISO 32000-2, 8.3.3): [a b c d e f] maps (x, y) to
/// (a·x + c·y + e, b·x + d·y + f). Concatenation: <c>m1 * m2</c> applies m1 first, then m2
/// — exactly the order of "cm" (new CTM = matrix × old CTM).
/// </summary>
/// <param name="A">Element a.</param>
/// <param name="B">Element b.</param>
/// <param name="C">Element c.</param>
/// <param name="D">Element d.</param>
/// <param name="E">Element e (translation in x).</param>
/// <param name="F">Element f (translation in y).</param>
public readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity matrix.</summary>
    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>Translation by (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <param name="x">Offset in x.</param>
    /// <param name="y">Offset in y.</param>
    /// <returns>The translation matrix.</returns>
    public static Matrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    /// <summary>Scaling by <paramref name="x"/> horizontally and <paramref name="y"/> vertically.</summary>
    /// <param name="x">Horizontal factor.</param>
    /// <param name="y">Vertical factor.</param>
    /// <returns>The scaling matrix.</returns>
    public static Matrix Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    /// <summary>Rotation about the origin.</summary>
    /// <param name="degrees">Angle in degrees, counter-clockwise; multiples of 90° give exact values.</param>
    /// <returns>The rotation matrix.</returns>
    public static Matrix Rotate(double degrees)
    {
        var r = degrees * Math.PI / 180;
        double c = Math.Cos(r), s = Math.Sin(r);
        // exact values for multiples of 90° (otherwise rounding noise in page coordinates)
        if (degrees % 90 == 0)
        {
            c = Math.Round(c);
            s = Math.Round(s);
        }
        return new Matrix(c, s, -s, c, 0, 0);
    }

    /// <summary>Concatenation: applies <paramref name="first"/> first, then <paramref name="second"/>.</summary>
    /// <param name="first">The transformation applied first.</param>
    /// <param name="second">The transformation applied second.</param>
    /// <returns>The combined matrix.</returns>
    public static Matrix operator *(Matrix first, Matrix second) => new(
        first.A * second.A + first.B * second.C,
        first.A * second.B + first.B * second.D,
        first.C * second.A + first.D * second.C,
        first.C * second.B + first.D * second.D,
        first.E * second.A + first.F * second.C + second.E,
        first.E * second.B + first.F * second.D + second.F);

    /// <summary>Transforms a point.</summary>
    /// <param name="x">x of the point.</param>
    /// <param name="y">y of the point.</param>
    /// <returns>The transformed point.</returns>
    public (double X, double Y) Transform(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>Transforms a vector without translation (for lengths and directions).</summary>
    /// <param name="x">x of the vector.</param>
    /// <param name="y">y of the vector.</param>
    /// <returns>The transformed vector.</returns>
    public (double X, double Y) TransformVector(double x, double y) => (A * x + C * y, B * x + D * y);

    /// <summary>Determinant a·d − b·c; negative for mirroring transformations.</summary>
    public double Determinant => A * D - B * C;

    /// <summary>True if the determinant is finite and its magnitude exceeds 1e-12.</summary>
    public bool IsInvertible => Math.Abs(Determinant) > 1e-12 && double.IsFinite(Determinant);

    /// <summary>Computes the inverse.</summary>
    /// <returns>The inverse; <see cref="Identity"/> if the matrix is not invertible.</returns>
    public Matrix Invert()
    {
        var det = Determinant;
        if (!IsInvertible) return Identity;
        return new Matrix(D / det, -B / det, -C / det, A / det, (C * F - D * E) / det, (B * E - A * F) / det);
    }

    /// <summary>Mean scale (square root of the absolute determinant), for example for line widths.</summary>
    public double MeanScale => Math.Sqrt(Math.Abs(Determinant));

    /// <summary>Whether all elements are finite.</summary>
    public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);

    /// <summary>Bounding rectangle of a rectangle after the transformation.</summary>
    /// <param name="r">The rectangle.</param>
    /// <returns>The axis-aligned bounds of the four transformed corners.</returns>
    public RectD TransformBounds(RectD r)
    {
        var (x1, y1) = Transform(r.Left, r.Top);
        var (x2, y2) = Transform(r.Right, r.Top);
        var (x3, y3) = Transform(r.Left, r.Bottom);
        var (x4, y4) = Transform(r.Right, r.Bottom);
        return RectD.FromLtrb(Math.Min(Math.Min(x1, x2), Math.Min(x3, x4)), Math.Min(Math.Min(y1, y2), Math.Min(y3, y4)),
            Math.Max(Math.Max(x1, x2), Math.Max(x3, x4)), Math.Max(Math.Max(y1, y2), Math.Max(y3, y4)));
    }

    /// <summary>Reads a PDF matrix array [a b c d e f].</summary>
    /// <param name="array">The array; may be null.</param>
    /// <returns>The matrix; null unless the array has exactly six finite numbers.</returns>
    public static Matrix? FromArray(Objects.PdfArray? array)
    {
        if (array is not { Count: 6 }) return null;
        var v = new double[6];
        for (var i = 0; i < 6; i++)
        {
            if (array.GetNumber(i) is not { } n || !double.IsFinite(n)) return null;
            v[i] = n;
        }
        return new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]);
    }
}

/// <summary>
/// Axis-aligned rectangle with Left ≤ Right and Top ≤ Bottom in the mathematical sense of
/// "smaller/larger" — so in PDF coordinates Top lies at the bottom. Arithmetic only, no direction.
/// </summary>
/// <param name="Left">Smallest x.</param>
/// <param name="Top">Smallest y.</param>
/// <param name="Right">Largest x.</param>
/// <param name="Bottom">Largest y.</param>
public readonly record struct RectD(double Left, double Top, double Right, double Bottom)
{
    /// <summary>The empty rectangle at the origin.</summary>
    public static readonly RectD Empty = new(0, 0, 0, 0);

    /// <summary>Everything, for "no clip".</summary>
    public static readonly RectD Infinite = new(double.MinValue, double.MinValue, double.MaxValue, double.MaxValue);

    /// <summary>Creates a rectangle from its edges (no normalisation).</summary>
    /// <param name="l">Left.</param>
    /// <param name="t">Top (smallest y).</param>
    /// <param name="r">Right.</param>
    /// <param name="b">Bottom (largest y).</param>
    /// <returns>The rectangle.</returns>
    public static RectD FromLtrb(double l, double t, double r, double b) => new(l, t, r, b);

    /// <summary>Right − Left.</summary>
    public double Width => Right - Left;

    /// <summary>Bottom − Top.</summary>
    public double Height => Bottom - Top;

    /// <summary>True unless both width and height are positive (a hairline of width 0 counts as empty).</summary>
    public bool IsEmpty => !(Right > Left && Bottom > Top);

    /// <summary>Computes the intersection.</summary>
    /// <param name="o">The other rectangle.</param>
    /// <returns>The intersection; inverted (not normalised) if the rectangles do not overlap.</returns>
    public RectD Intersect(RectD o) => new(Math.Max(Left, o.Left), Math.Max(Top, o.Top), Math.Min(Right, o.Right), Math.Min(Bottom, o.Bottom));

    /// <summary>Computes the union.</summary>
    /// <param name="o">The other rectangle.</param>
    /// <returns>The smallest rectangle enclosing both; an empty rectangle (see <see cref="IsEmpty"/>) is ignored.</returns>
    public RectD Union(RectD o) => IsEmpty ? o : o.IsEmpty ? this : new(Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));

    /// <summary>Tests whether the rectangles touch or overlap (edges included).</summary>
    /// <param name="o">The other rectangle.</param>
    /// <returns>True if they touch or overlap.</returns>
    public bool Intersects(RectD o) => Left <= o.Right && o.Left <= Right && Top <= o.Bottom && o.Top <= Bottom;

    /// <summary>Enlarges the rectangle on every side.</summary>
    /// <param name="d">The amount per side (negative shrinks).</param>
    /// <returns>The enlarged rectangle.</returns>
    public RectD Inflate(double d) => new(Left - d, Top - d, Right + d, Bottom + d);
}
