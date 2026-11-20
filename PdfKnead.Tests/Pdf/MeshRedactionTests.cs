using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Redacting mesh shadings (types 4–7): their data under the bars leaves the file (docs/FEHLERGEDAECHTNIS.md, No. 701; before, the clip
/// around them only hid it, No. 455).
/// </summary>
public sealed class MeshRedactionTests
{
    private static readonly PdfRectangle Bar = new(96, 695, 180, 714);

    /// <summary>
    /// A free-form triangle mesh (type 4, DeviceRGB, 16-bit coordinates over the page) of the given triangles, each corner with its colour;
    /// drawn with „sh“ on a one-page file.
    /// </summary>
    private static byte[] File(params (double X, double Y, byte R, byte G, byte B)[] corners) => Build("/Shading<</Sh0 5 0 R>>", "q /Sh0 sh Q", corners);

    /// <summary>As <see cref="File"/>, the page with <paramref name="resources"/> and <paramref name="content"/> (the mesh is object 5).</summary>
    private static byte[] Build(string resources, string content, (double X, double Y, byte R, byte G, byte B)[] corners)
    {
        var data = new StringBuilder();
        foreach (var (x, y, r, g, b) in corners)
        {
            var (cx, cy) = ((int)Math.Round(x / 595 * 65535), (int)Math.Round(y / 842 * 65535));
            data.Append((char)0).Append((char)(cx >> 8)).Append((char)(cx & 0xFF)).Append((char)(cy >> 8)).Append((char)(cy & 0xFF))
                .Append((char)r).Append((char)g).Append((char)b);
        }
        return CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = $"<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<{resources}>>>>",
            [4] = ("", content),
            [5] = ("/ShadingType 4/ColorSpace/DeviceRGB/BitsPerCoordinate 16/BitsPerComponent 8/BitsPerFlag 8/Decode[0 595 0 842 0 1 0 1 0 1]",
                data.ToString()),
        });
    }

    /// <summary>The corners (page space) of the meshes the page's content draws with „sh“, read as the display reads them.</summary>
    private static List<(double X, double Y)> Corners(PdfDocument doc)
    {
        var page = doc.Pages[0];
        var content = Encoding.Latin1.GetString(doc.GetStreamData((PdfStream)page.Dictionary.Get(PdfName.Contents)!));
        var result = new List<(double, double)>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(content, @"/(\w+) sh"))
            if (Shadings.Create(page.Resources!.GetDictionary(PdfName.Get("Shading"))!.Get(PdfName.Get(m.Groups[1].Value)), doc, page.Resources, Matrix.Identity)
                is MeshFill mesh)
                for (var i = 0; i + 1 < mesh.Points.Length; i += 2) result.Add((mesh.Points[i], mesh.Points[i + 1]));
        return result;
    }

    /// <summary>
    /// A mesh across the bar: afterwards no corner lies inside the bar, the parts beside it are still drawn — with the colours they had
    /// (a corner on the bar's edge takes the colour the triangle had there) — and the original data is gone from the file.
    /// </summary>
    [Fact]
    public void MeshAcrossTheBar_LosesItsDataUnderIt()
    {
        var bytes = File((60, 680, 255, 0, 0), (260, 680, 0, 0, 255), (60, 730, 255, 0, 0), (260, 680, 0, 0, 255), (260, 730, 0, 0, 255), (60, 730, 255, 0, 0));
        using var doc = PdfDocument.Open(bytes);
        var saved = RedactionTests.WithBars(doc, (0, Bar));
        using var after = PdfDocument.Open(saved);
        var corners = Corners(after);
        Assert.NotEmpty(corners);
        Assert.DoesNotContain(corners, c => c.X > Bar.Left + 0.01 && c.X < Bar.Right - 0.01 && c.Y > Bar.Bottom + 0.01 && c.Y < Bar.Top - 0.01);
        Assert.Contains(corners, c => Math.Abs(c.X - 60) < 0.01);    // beside the bar it stays
        Assert.Contains(corners, c => Math.Abs(c.X - 260) < 0.01);
        Assert.DoesNotContain("/BitsPerCoordinate 16", Encoding.Latin1.GetString(saved));   // the original mesh left the file
        // the colour at the left edge of the bar: on the red-to-blue run from x 60 to 260 at x 96 (18 % of the way)
        var shadings = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Shading"))!;
        var mesh = (MeshFill)Shadings.Create(shadings.Get(shadings.Entries.First().Key), after, after.Pages[0].Resources, Matrix.Identity)!;
        for (var i = 0; i + 1 < mesh.Points.Length; i += 2)
            if (Math.Abs(mesh.Points[i] - 96) < 0.01 && Math.Abs(mesh.Points[i + 1] - 680) < 0.01)
            {
                var red = (int)((mesh.Colors[i / 2] >> 16) & 0xFF);
                Assert.InRange(red, 205, 213);   // 255 · (1 − 36/200) ≈ 209
            }
    }

    /// <summary>
    /// The same mesh as the shading of a pattern filling a path across the bar (shifted by the pattern's matrix): the path is cut at the bar,
    /// and the pattern's mesh loses its data under it as well.
    /// </summary>
    [Fact]
    public void MeshPatternAcrossTheBar_LosesItsDataUnderIt()
    {
        // the mesh as the shading of a pattern placed at (60, 680), painted by a path
        var bytes = Build("/Pattern<</P0<</PatternType 2/Shading 5 0 R/Matrix[1 0 0 1 60 680]>>>>", "/Pattern cs /P0 scn 60 680 200 50 re f",
            [(0, 0, 255, 0, 0), (200, 0, 0, 0, 255), (0, 50, 255, 0, 0), (200, 0, 0, 0, 255), (200, 50, 0, 0, 255), (0, 50, 255, 0, 0)]);
        using var doc = PdfDocument.Open(bytes);
        var saved = RedactionTests.WithBars(doc, (0, Bar));
        using var after = PdfDocument.Open(saved);
        var pattern = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Pattern"))!.GetDictionary(PdfName.Get("P0"))!;
        var mesh = (MeshFill)Shadings.Create(pattern.Get(PdfName.Get("Shading")), after, after.Pages[0].Resources, Matrix.Identity)!;
        Assert.NotEmpty(mesh.Points);
        for (var i = 0; i + 1 < mesh.Points.Length; i += 2)
        {
            var (x, y) = (mesh.Points[i] + 60, mesh.Points[i + 1] + 680);   // pattern space → page
            Assert.False(x > Bar.Left + 0.01 && x < Bar.Right - 0.01 && y > Bar.Bottom + 0.01 && y < Bar.Top - 0.01, $"({x}, {y})");
        }
        Assert.DoesNotContain("/BitsPerCoordinate 16", Encoding.Latin1.GetString(saved));
    }

    /// <summary>A mesh entirely under the bar: nothing of it is left — it is not drawn and its data is gone.</summary>
    [Fact]
    public void MeshUnderTheBar_GoesEntirely()
    {
        var bytes = File((100, 698, 255, 0, 0), (170, 698, 0, 255, 0), (100, 710, 0, 0, 255));
        using var doc = PdfDocument.Open(bytes);
        var saved = RedactionTests.WithBars(doc, (0, Bar));
        using var after = PdfDocument.Open(saved);
        Assert.Empty(Corners(after));
        Assert.DoesNotContain("/ShadingType 4", Encoding.Latin1.GetString(saved));
    }
}
