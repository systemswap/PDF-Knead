using PdfKnead.Core.Imaging;

namespace PdfKnead.Tests.Imaging;

/// <summary>
/// The exact area coverage of <see cref="CoverageRaster"/> against values worked out by hand (docs/OFFENE-PUNKTE.md, „Eigene Fassung der
/// Kantenrasterung“; the edge procedure written anew on 16.11.2026): rectangles on and off the pixel grid, triangles, slanted and nearly
/// horizontal edges, edges beyond the raster, holes, overlaps — and that the coverage of a shape inside the raster adds up to its area.
/// </summary>
public sealed class CoverageRasterTests
{
    private static float At(float[] coverage, int width, int x, int y) => coverage[y * width + x];

    [Fact]
    public void Rectangle_OnTheGrid_FullOrEmpty()
    {
        var raster = new CoverageRaster(6, 5);
        raster.Polygon([(1, 1), (4, 1), (4, 3), (1, 3)]);
        var c = raster.Coverage();
        for (var y = 0; y < 5; y++)
            for (var x = 0; x < 6; x++)
                Assert.Equal(x is >= 1 and < 4 && y is >= 1 and < 3 ? 1.0 : 0.0, At(c, 6, x, y), 5);
    }

    [Fact]
    public void Rectangle_OffTheGrid_PartsOfPixels()
    {
        var raster = new CoverageRaster(5, 4);
        raster.Polygon([(1.25, 1.5), (3.75, 1.5), (3.75, 2.5), (1.25, 2.5)]);
        var c = raster.Coverage();
        Assert.Equal(0.75 * 0.5, At(c, 5, 1, 1), 5);   // left column, upper row: 3/4 wide, 1/2 high
        Assert.Equal(1.0 * 0.5, At(c, 5, 2, 1), 5);
        Assert.Equal(0.75 * 0.5, At(c, 5, 3, 1), 5);
        Assert.Equal(0.75 * 0.5, At(c, 5, 1, 2), 5);
        Assert.Equal(0, At(c, 5, 0, 1), 5);
        Assert.Equal(0, At(c, 5, 4, 2), 5);
    }

    [Fact]
    public void Triangle_DiagonalCellsHalfCovered()
    {
        // x + y < 4: the cells the diagonal halves (corners summing to 3 … 5) are half covered, those below it full
        var raster = new CoverageRaster(5, 5);
        raster.Polygon([(0, 0), (4, 0), (0, 4)]);
        var c = raster.Coverage();
        for (var k = 0; k < 4; k++) Assert.Equal(0.5, At(c, 5, k, 3 - k), 5);
        Assert.Equal(1, At(c, 5, 0, 0), 5);
        Assert.Equal(1, At(c, 5, 1, 1), 5);
        Assert.Equal(0, At(c, 5, 3, 3), 5);
    }

    [Fact]
    public void SlantedEdge_AcrossManyColumns_ExactArea()
    {
        // a nearly horizontal edge: from (0, 1) to (10, 2) over the top of a band down to y = 3 — the row between y 1 and 2 is cut by it
        var raster = new CoverageRaster(10, 4);
        raster.Polygon([(0, 1), (10, 2), (10, 3), (0, 3)]);
        var c = raster.Coverage();
        for (var x = 0; x < 10; x++)
        {
            // in column x the edge runs from y = 1 + x/10 to 1 + (x + 1)/10: the part below it is 1 − (x + 0.5)/10
            Assert.Equal(1 - (x + 0.5) / 10, At(c, 10, x, 1), 4);
            Assert.Equal(1, At(c, 10, x, 2), 5);
            Assert.Equal(0, At(c, 10, x, 0), 5);
        }
    }

    [Fact]
    public void BeyondTheBorders_LeftCountsFully_RightNot()
    {
        var raster = new CoverageRaster(4, 3);
        raster.Polygon([(-5, 0), (2, 0), (2, 3), (-5, 3)]);   // mostly left of the raster
        var c = raster.Coverage();
        for (var y = 0; y < 3; y++)
        {
            Assert.Equal(1, At(c, 4, 0, y), 5);
            Assert.Equal(1, At(c, 4, 1, y), 5);
            Assert.Equal(0, At(c, 4, 2, y), 5);
        }
        var right = new CoverageRaster(4, 3);
        right.Polygon([(3, -2), (9, -2), (9, 9), (3, 9)]);       // from column 3 to beyond the right border and above and below
        c = right.Coverage();
        for (var y = 0; y < 3; y++)
        {
            Assert.Equal(1, At(c, 4, 3, y), 5);
            Assert.Equal(0, At(c, 4, 2, y), 5);
        }
    }

    [Fact]
    public void Hole_StaysEmpty_Overlap_CountsOnce()
    {
        // Polygon() gives every polygon the same winding; a hole is drawn by hand against the winding of the outline (which Polygon() draws
        // as (0, 0) → (0, 8) → (8, 8) → (8, 0))
        var raster = new CoverageRaster(8, 8);
        raster.Polygon([(0, 0), (8, 0), (8, 8), (0, 8)]);
        raster.MoveTo(2, 2);
        raster.LineTo(6, 2);
        raster.LineTo(6, 6);
        raster.LineTo(2, 6);
        raster.Close();
        var c = raster.Coverage();
        Assert.Equal(1, At(c, 8, 1, 1), 5);
        Assert.Equal(0, At(c, 8, 3, 3), 5);
        var twice = new CoverageRaster(4, 4);
        twice.Polygon([(0, 0), (4, 0), (4, 4), (0, 4)]);
        twice.Polygon([(0, 0), (4, 0), (4, 4), (0, 4)]);         // the same direction twice: still 1
        Assert.All(twice.Coverage(), v => Assert.Equal(1, v, 5));
    }

    [Fact]
    public void ShapesInside_CoverageAddsUpToTheirArea()
    {
        var random = new Random(16_11);
        for (var run = 0; run < 300; run++)
        {
            // a convex polygon, wholly inside the raster: corners on a circle at sorted angles — it never crosses itself, so the shoelace
            // formula gives the area it covers (with corners at different distances it may cross itself, and the formula would count a
            // region it winds twice twice, the coverage once)
            var corners = random.Next(3, 12);
            double cx = 20 + random.NextDouble() * 20, cy = 20 + random.NextDouble() * 20, radius = 3 + random.NextDouble() * 15;
            var angles = Enumerable.Range(0, corners).Select(_ => random.NextDouble() * 2 * Math.PI).Order().ToArray();
            var points = angles.Select(a => (cx + Math.Cos(a) * radius, cy + Math.Sin(a) * radius)).ToArray();
            double area = 0;
            for (var k = 0; k < points.Length; k++)
            {
                var (x0, y0) = points[k];
                var (x1, y1) = points[(k + 1) % points.Length];
                area += x0 * y1 - x1 * y0;
            }
            var raster = new CoverageRaster(64, 64);
            raster.Polygon(points);
            Assert.Equal(Math.Abs(area) / 2, raster.Coverage().Sum(v => (double)v), 2);
        }
    }
}
