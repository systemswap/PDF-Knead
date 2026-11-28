using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, FG 1258 (display; found by the length check of the guard m7-abschluss, a file of an old PostScript converter with Type 3 glyphs as
/// stencil masks): the knockout group of a text object (FG 1165) was kept as soon as an image stood in it — opaque glyphs that overlap were then
/// drawn as paths in a layer, at the edges up to 95 levels different from the same glyphs in text objects of their own (a line the redaction
/// wrote anew looked different beside the bars). An image keeps the group only where it can be partly transparent: its opacity or a soft mask
/// of its own; a stencil mask, a hard mask and an opaque image cover like a glyph.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// Two „l“ of Helvetica at 100 pt, the second 4.2 pt right of the first, on it (in one text object or in two), and in the first text object a
    /// Type 3 glyph „a“ drawn by <paramref name="glyph"/> at 20 pt; <paramref name="state"/> is set before the text.
    /// </summary>
    private static byte[] Fg1258File(bool oneObject, string glyph, string state = "", params string[] objects) => E7File(
        $"{state} 0 g " + (oneObject
            ? "BT /F1 100 Tf 50 50 Td [(l) 180 (l)] TJ /F3 20 Tf 100 0 Td (a) Tj ET"
            : "BT /F1 100 Tf 50 50 Td (l) Tj /F3 20 Tf 100 0 Td (a) Tj ET BT /F1 100 Tf 54.2 50 Td (l) Tj ET"),
        "/ExtGState << /H << /ca 0.5 >> >> /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> /F3 5 0 R >>",
        [
            "<< /Type /Font /Subtype /Type3 /FontMatrix [0.001 0 0 0.001 0 0] /FontBBox [0 0 1000 1000] /CharProcs << /g 6 0 R >> " +
            "/Encoding << /Differences [97 /g] >> /FirstChar 97 /LastChar 97 /Widths [1000] /Resources << /XObject << /Im 7 0 R >> >> >>",
            E7Stream("", glyph), .. objects]);

    /// <summary>The glyph as an inline stencil mask of 8 × 8 samples, all painted (the form of the file the guard found).</summary>
    private const string Fg1258Stencil = "1000 0 0 0 1000 1000 d1 q 1000 0 0 1000 0 0 cm BI /IM true /W 8 /H 8 /BPC 1 /F /AHx ID 0000000000000000> EI Q";

    /// <summary>
    /// ISO 32000-2, 9.3.8 (with /TK true a text object is a knockout group) with 11.4.6.2 (knockout: a later object replaces an earlier one where
    /// it covers — an opaque one covers the same either way): with an opaque stencil mask among the glyphs the text object needs no layer, and its
    /// overlapping glyphs look exactly like the same glyphs in two text objects.
    /// </summary>
    [Fact]
    public void Fg1258_OpaqueStencilGlyphInTheTextObject_NoLayer_SameAsInTwoTextObjects()
    {
        using (var doc = PdfDocument.Open(Fg1258File(oneObject: true, Fg1258Stencil)))
            Assert.DoesNotContain(PageInterpreter.Build(doc, doc.Pages[0]).Groups.ToArray(), g => g.NeedsLayer);
        using var one = Fg1258Render(Fg1258File(oneObject: true, Fg1258Stencil));
        using var two = Fg1258Render(Fg1258File(oneObject: false, Fg1258Stencil));
        var differing = 0;
        for (var y = 0; y < one.Height; y++)
            for (var x = 0; x < one.Width; x++)
                if (one.GetPixel(x, y) != two.GetPixel(x, y)) differing++;
        Assert.Equal(0, differing);
    }

    /// <summary>
    /// The group stays where the knockout can be seen: under /ca 0.5 (the overlap no darker than one glyph, FG 1165), and with a Type 3 glyph that
    /// draws an image with a soft mask of its own (a d0 glyph: one with d1 draws no image but a stencil mask, 9.6.4).
    /// </summary>
    [Fact]
    public void Fg1258_TranslucentOrSoftMaskedImage_KeepsTheGroup()
    {
        using (var doc = PdfDocument.Open(Fg1258File(oneObject: true, Fg1258Stencil, "/H gs")))
            Assert.Contains(PageInterpreter.Build(doc, doc.Pages[0]).Groups.ToArray(), g => g.NeedsLayer);
        var softMasked = Fg1258File(oneObject: true, "1000 0 d0 q 1000 0 0 1000 0 0 cm /Im Do Q", "",
            E7Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode /SMask 8 0 R", "00>"),
            E7Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode", "80>"));
        using (var doc = PdfDocument.Open(softMasked))
            Assert.Contains(PageInterpreter.Build(doc, doc.Pages[0]).Groups.ToArray(), g => g.NeedsLayer);
    }

    private static SKBitmap Fg1258Render(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        return SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 3, new ImageCache());
    }
}
