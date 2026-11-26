using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Fills a glyph outline into a small image (non-zero winding, 4 × 4 samples per pixel): the stand-in for a glyph of a colour font that has
/// no colour image (OpenType sbix 34.1, SVG 35.6: such a glyph is drawn with its outline).
/// </summary>
internal static class OutlineRaster
{
    private const int Samples = 4;

    /// <summary>Renders the outline at <paramref name="scale"/> px per font unit in opaque black.</summary>
    /// <param name="outline">The outline in font units (y upwards).</param>
    /// <param name="scale">Pixels per font unit.</param>
    /// <returns>The lower left corner of the image relative to the glyph origin in px (y upwards) and the image; null for an empty outline.</returns>
    public static (int X, int Y, RgbaImage Image)? Render(GlyphOutline outline, double scale)
    {
        var segments = Flatten(outline, scale);
        if (segments.Count == 0) return null;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x0, y0, x1, y1) in segments)
        {
            minX = Math.Min(minX, Math.Min(x0, x1));
            maxX = Math.Max(maxX, Math.Max(x0, x1));
            minY = Math.Min(minY, Math.Min(y0, y1));
            maxY = Math.Max(maxY, Math.Max(y0, y1));
        }
        int left = (int)Math.Floor(minX), bottom = (int)Math.Floor(minY);
        int width = (int)Math.Ceiling(maxX) - left, height = (int)Math.Ceiling(maxY) - bottom;
        if (width < 1 || height < 1 || (long)width * height > ColorGlyphs.MaxGlyphPixels) return null;
        var coverage = new float[width * height];
        var crossings = new List<(double X, int Winding)>();
        for (var row = 0; row < height * Samples; row++)
        {
            // the sample line in pixels, measured from the bottom of the image
            var y = bottom + (row + 0.5) / Samples;
            crossings.Clear();
            foreach (var (x0, y0, x1, y1) in segments)
            {
                if (y0 == y1 || y < Math.Min(y0, y1) || y >= Math.Max(y0, y1)) continue;
                crossings.Add((x0 + (y - y0) * (x1 - x0) / (y1 - y0), y1 > y0 ? 1 : -1));
            }
            crossings.Sort((a, b) => a.X.CompareTo(b.X));
            var line = height - 1 - row / Samples;
            var winding = 0;
            for (var i = 0; i + 1 < crossings.Count; i++)
            {
                winding += crossings[i].Winding;
                if (winding != 0) Span(coverage.AsSpan(line * width, width), crossings[i].X - left, crossings[i + 1].X - left);
            }
        }
        var image = RgbaImage.Blank(width, height);
        for (var i = 0; i < coverage.Length; i++) image.Pixels[i * 4 + 3] = (byte)Math.Round(Math.Clamp(coverage[i], 0, 1) * 255);
        return (left, bottom, image);
    }

    /// <summary>Adds the coverage of one span of a sample line (from <paramref name="from"/> to <paramref name="to"/> in pixels).</summary>
    private static void Span(Span<float> line, double from, double to)
    {
        from = Math.Clamp(from, 0, line.Length);
        to = Math.Clamp(to, 0, line.Length);
        const float share = 1f / Samples;
        for (var x = (int)from; x < to && x < line.Length; x++)
            line[x] += share * (float)(Math.Min(to, x + 1) - Math.Max(from, x));
    }

    /// <summary>The outline as line segments in pixels, curves flattened, every contour closed.</summary>
    private static List<(double X0, double Y0, double X1, double Y1)> Flatten(GlyphOutline outline, double scale)
    {
        var result = new List<(double, double, double, double)>();
        var points = outline.Points;
        int p = 0;
        double startX = 0, startY = 0, x = 0, y = 0;
        void Line(double toX, double toY)
        {
            if (toX != x || toY != y) result.Add((x, y, toX, toY));
            (x, y) = (toX, toY);
        }
        foreach (var verb in outline.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    Line(startX, startY);
                    (startX, startY) = (x, y) = (points[p] * scale, points[p + 1] * scale);
                    p += 2;
                    break;
                case PathVerb.Line:
                    Line(points[p] * scale, points[p + 1] * scale);
                    p += 2;
                    break;
                case PathVerb.Cubic:
                {
                    double ax = x, ay = y;
                    double bx = points[p] * scale, by = points[p + 1] * scale, cx = points[p + 2] * scale, cy = points[p + 3] * scale;
                    double dx = points[p + 4] * scale, dy = points[p + 5] * scale;
                    p += 6;
                    // enough steps that each piece stays within about a quarter pixel
                    var length = Math.Abs(bx - ax) + Math.Abs(by - ay) + Math.Abs(cx - bx) + Math.Abs(cy - by) + Math.Abs(dx - cx) + Math.Abs(dy - cy);
                    var steps = Math.Clamp((int)Math.Ceiling(Math.Sqrt(length) * 2), 1, 64);
                    for (var i = 1; i <= steps; i++)
                    {
                        var t = (double)i / steps;
                        var u = 1 - t;
                        Line(u * u * u * ax + 3 * u * u * t * bx + 3 * u * t * t * cx + t * t * t * dx,
                            u * u * u * ay + 3 * u * u * t * by + 3 * u * t * t * cy + t * t * t * dy);
                    }
                    break;
                }
                case PathVerb.Close:
                    Line(startX, startY);
                    break;
            }
        }
        Line(startX, startY);
        return result;
    }
}
