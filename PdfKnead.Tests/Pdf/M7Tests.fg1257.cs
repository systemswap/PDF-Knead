using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, FG 1257 (display; found by the length check of the guard m7-abschluss, from FG 1200): a text object whose overlapping glyphs overprint
/// kept its knockout group, and its glyphs were drawn as paths in a layer of their own — without the simulation of overprinting, where that
/// group changes nothing, up to 98 levels different at the edges from the same glyphs in text objects of their own (drawn from pre-rendered
/// masks). A line the redaction wrote anew glyph by glyph (their boxes overlap a little) thus looked different from the original beside the
/// bars. Such a group is now no group without the simulation.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>The glyphs „A“ and „V“ overprinting (/OP, /op true, /OPM 1, a CMYK colour without yellow) at 40 pt, „V“ moved onto „A“ (0.3 em): in one text object or in two.</summary>
    private static byte[] Fg1257File(bool oneObject) => E7File(
        "/G gs 0.3 0.2 0 0.6 k " + (oneObject
            ? "BT /F1 40 Tf 20 50 Td [(A) 300 (V)] TJ ET"
            : "BT /F1 40 Tf 20 50 Td (A) Tj ET BT /F1 40 Tf 34.68 50 Td (V) Tj ET"),
        $"/ExtGState << /G << /OP true /op true /OPM 1 >> >> {E7Helvetica}");

    private static SKBitmap Fg1257Render(byte[] file, bool overprintPreview)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        return SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 3, new ImageCache(), overprintPreview: overprintPreview);
    }

    /// <summary>
    /// ISO 32000-2, 9.3.8 (a text object with /TK true is a knockout group; its knockout only shows where its glyphs overlap and something in it is
    /// transparent or overprints) with 8.6.7 (overprinting shows only where it is simulated): without the simulation the overlapping overprinting
    /// glyphs in one text object look exactly like the same glyphs in two; the group is still there for the simulation (marked overprint-only).
    /// </summary>
    [Fact]
    public void Fg1257_OverprintingOverlappingGlyphs_WithoutSimulation_SameAsInTwoTextObjects()
    {
        using (var doc = PdfDocument.Open(Fg1257File(oneObject: true)))
            Assert.Contains(PageInterpreter.Build(doc, doc.Pages[0]).Groups.ToArray(), g => g.NeedsLayer && g.OverprintOnly);
        using var one = Fg1257Render(Fg1257File(oneObject: true), overprintPreview: false);
        using var two = Fg1257Render(Fg1257File(oneObject: false), overprintPreview: false);
        var differing = 0;
        for (var y = 0; y < one.Height; y++)
            for (var x = 0; x < one.Width; x++)
                if (one.GetPixel(x, y) != two.GetPixel(x, y)) differing++;
        Assert.Equal(0, differing);
        // with the simulation the text object is drawn as its knockout group (the glyphs replace one another where they overlap)
        using var simulated = Fg1257Render(Fg1257File(oneObject: true), overprintPreview: true);
        Assert.Equal(one.Width, simulated.Width);
    }
}
