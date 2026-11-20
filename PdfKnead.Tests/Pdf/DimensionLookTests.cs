using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// How a saved length dimension looks (decided on 10.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 685): arrow heads of 30° as in technical
/// drawings, filled with a gradient along them (an axial shading clipped to the head), a faint glow of the colour beneath the line; a length too
/// short for both heads between its ends gets them from outside, the line running on beyond its ends (ISO 129-1). Before, the heads were
/// stubby and lay on top of each other on a short length.
/// </summary>
public sealed partial class DimensionLookTests
{
    private static string Content(DimensionElement dimension) => Encoding.ASCII.GetString(ElementAppearance.For(dimension).Form.RawData!);

    private static DimensionElement Length(double span) =>
        ElementAppearance.Dimension(Measuring.Measure(MeasureKind.Length, [(100, 300), (100 + span, 300)], []), 0x1E7B34, 1, 0, false, false, 0)!;

    /// <summary>The clipping paths of the heads („… m … l … l h W n /Sh0 sh“): their three corners, in the form's space.</summary>
    private static List<(double X, double Y)[]> Heads(string content) =>
        [.. HeadPattern().Matches(content).Select(m => new[]
        {
            (Num(m.Groups[1]), Num(m.Groups[2])), (Num(m.Groups[3]), Num(m.Groups[4])), (Num(m.Groups[5]), Num(m.Groups[6])),
        })];

    private static double Num(Group g) => double.Parse(g.Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(-?[\d.]+) (-?[\d.]+) m (-?[\d.]+) (-?[\d.]+) l (-?[\d.]+) (-?[\d.]+) l h W n /Sh\d+ sh")]
    private static partial Regex HeadPattern();

    /// <summary>A length of 100 pt: both heads between its ends, pointing outwards, slim (30°); filled by a shading, the glow beneath.</summary>
    [Fact]
    public void LongLength_HeadsInside_Slim_Gradient_Glow()
    {
        var dimension = Length(100);
        var content = Content(dimension);
        var heads = Heads(content);
        Assert.Equal(2, heads.Count);
        var (left, right) = (100 - dimension.Rect.Left, 200 - dimension.Rect.Left);
        foreach (var head in heads)
        {
            // the base lies between the ends: inside
            var baseX = (head[1].X + head[2].X) / 2;
            Assert.InRange(baseX, left, right);
            var length = Math.Abs(head[0].X - baseX);
            var width = Math.Abs(head[1].Y - head[2].Y);
            Assert.InRange(width / length, 0.5, 0.58);   // 2 · tan 15°
        }
        var resources = ElementAppearance.For(dimension).Form.Dictionary.GetDictionary(PdfName.Get("Resources"))!;
        var shading = resources.GetDictionary(PdfName.Get("Shading"))!.GetDictionary(PdfName.Get("Sh0"))!;
        Assert.Equal(2, shading.GetInteger(PdfName.Get("ShadingType")));
        Assert.Equal(0.2, resources.GetDictionary(PdfName.Get("ExtGState"))!.GetDictionary(PdfName.Get("GS1"))!.GetNumber(PdfName.Get("CA"))!.Value, 3);
        Assert.Contains("/GS1 gs", content);
    }

    /// <summary>A length of 8 pt (two heads need about 15): the heads lie outside its ends, pointing inwards — not on top of each other.</summary>
    [Fact]
    public void ShortLength_HeadsComeFromOutside()
    {
        var dimension = Length(8);
        var heads = Heads(Content(dimension));
        Assert.Equal(2, heads.Count);
        var (left, right) = (100 - dimension.Rect.Left, 108 - dimension.Rect.Left);
        var bases = heads.Select(h => (h[1].X + h[2].X) / 2).Order().ToList();
        Assert.True(bases[0] < left - 1 && bases[1] > right + 1, $"Grundlinien {bases[0]:0.0}/{bases[1]:0.0}, Enden {left:0.0}/{right:0.0}");
        // the tips at the ends
        var tips = heads.Select(h => h[0].X).Order().ToList();
        Assert.Equal(left, tips[0], 1);
        Assert.Equal(right, tips[1], 1);
    }
}
