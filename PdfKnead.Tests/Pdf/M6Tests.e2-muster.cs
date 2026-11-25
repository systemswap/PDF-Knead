using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

// M6, patterns, shadings, glyphs, overprint (ISO 32000-2, 8.6.7, 8.6.8, 8.7.2–8.7.4): the reading after the decisions of 25.11.2026
// (docs/FEHLERGEDAECHTNIS.md, No. 1130).
public sealed partial class M6NormTests
{
    // ------------------------------------------------------------ helpers (E2)

    /// <summary>An ICC CMYK space whose perceptual table swaps cyan and magenta and whose relative colorimetric table is the plain formula.</summary>
    private static string E2_SwappedProfile => F_Stream("/N 4", IccTests.SwappedAndPlain.Value);

    private static bool E2_Magenta(SKColor c) => c is { Red: > 230, Green: < 25, Blue: > 230 };

    private static bool E2_Cyan(SKColor c) => c is { Red: < 25, Green: > 230, Blue: > 230 };

    private static bool E2_Red(SKColor c) => c is { Red: > 200, Green: < 60, Blue: < 60 };

    private static SKColor E2_Pixel(byte[] file, int x, int y, bool overprintPreview = false, double scale = 1)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, scale, new ImageCache(), overprintPreview: overprintPreview);
        return bitmap.GetPixel(x, bitmap.Height - 1 - y);
    }

    // ------------------------------------------------------------ 8.6.8: d1 glyphs and uncoloured patterns (decision 15)

    /// <summary>
    /// 8.6.8 sentence 104T: in a d1 glyph every image operator is passed over except one painting an image mask — an RGB image (XObject) and an
    /// inline RGB image over the glyph square paint nothing; an inline image mask over the right half paints in the blue of the text. Before, the
    /// images were drawn in their own colours.
    /// </summary>
    [Fact]
    public void D1Glyph_ImagesOtherThanMasks_PassedOver()
    {
        const string red = "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8";
        var image = F_Glyph("q 1000 0 0 1000 0 0 cm /Im Do Q", "/XObject << /Im 7 0 R >>", objects: [F_Stream(red, "ÿ\u0000\u0000")]);
        Assert.True(F_White(F_Pixel(image, 100, 100)), $"Bild {F_Pixel(image, 100, 100)}");
        var inline = F_Glyph("q 1000 0 0 1000 0 0 cm BI /W 1 /H 1 /CS /RGB /BPC 8 ID ÿ\u0000\u0000 EI Q " +
                             "q 500 0 0 1000 500 0 cm BI /W 1 /H 1 /IM true ID \u0000 EI Q");
        Assert.True(F_White(F_Pixel(inline, 75, 100)), $"Inline-Bild {F_Pixel(inline, 75, 100)}");
        Assert.True(F_Blue(F_Pixel(inline, 125, 100)), $"Inline-Maske {F_Pixel(inline, 125, 100)}");
    }

    /// <summary>8.6.8 sentences 89T and 92T: in a d1 glyph „sh“ is passed over — a red axial shading the glyph paints with „sh“ leaves the page white.</summary>
    [Fact]
    public void D1Glyph_Sh_PassedOver()
    {
        var file = F_Glyph("/Sh sh", "/Shading << /Sh << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1000 0] " +
                                     "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [1 0 0] /N 1 >> /Extend [true true] >> >>");
        Assert.True(F_White(F_Pixel(file, 100, 100)), $"{F_Pixel(file, 100, 100)}");
    }

    /// <summary>
    /// 8.6.8 sentences 89T and 103T: in a d1 glyph „ri“ and /UseBlackPtComp of a graphics state are passed over. The text colour magenta in an ICC
    /// space whose perceptual table swaps cyan and magenta stays magenta after „/Perceptual ri“ in the glyph; the black of a profile with a light
    /// black stays compensated (black) after a „gs“ with /UseBlackPtComp /OFF. Before, cyan and dark grey.
    /// </summary>
    [Fact]
    public void D1Glyph_RiAndUseBlackPtComp_PassedOver()
    {
        var intent = F_Glyph("/Perceptual ri 0 0 1000 1000 re f", before: "/CS0 cs 0 1 0 0 sc", pageResources: "/ColorSpace << /CS0 [/ICCBased 7 0 R] >>",
            objects: [E2_SwappedProfile]);
        Assert.True(E2_Magenta(F_Pixel(intent, 100, 100)), $"ri {F_Pixel(intent, 100, 100)}");
        var black = F_Glyph("/Off gs 0 0 1000 1000 re f", "/ExtGState << /Off << /UseBlackPtComp /OFF >> >>", "/CS0 cs 0 0 0 1 sc",
            "/ColorSpace << /CS0 [/ICCBased 7 0 R] >>", F_Stream("/N 4", IccTests.LightBlack.Value));
        Assert.True(F_Pixel(black, 100, 100).Red < 12, $"BPC {F_Pixel(black, 100, 100)}");
    }

    /// <summary>
    /// 8.6.8 sentences 80T–94T and 8.7.3.1 sentences 21–25: in the cell of an uncoloured pattern the colour operators are passed over — a cell that
    /// fills after „/N cs 1 sc“ in the colorant /None (which never marks) still paints in the colour from „scn“ (blue). Before, nothing.
    /// </summary>
    [Fact]
    public void UncolouredCell_ColourOperators_PassedOver()
    {
        using var b = G874_Render("/CS cs 0 0 1 /P scn 0 0 200 200 re f", G874_With(_ => G874_Tiling("/N cs 1 sc 0 0 5 5 re f", 10, [0, 0, 10, 10], 2,
            resources: TestPdf.Dict(("ColorSpace", TestPdf.Dict(("N", new PdfArray([TestPdf.N("Separation"), TestPdf.N("None"), TestPdf.N("DeviceGray"),
                G874_Exponential([0], [1])])))))), G874_UncolouredRgb()));
        Assert.True(G874_Blue(G874_At(b, 2, 2)), $"{G874_At(b, 2, 2)}");
        Assert.True(G874_White(G874_At(b, 7, 7)), $"{G874_At(b, 7, 7)}");
    }

    /// <summary>
    /// 8.6.8 sentences 89T and 92T in an uncoloured cell: „sh“ is passed over — a cell of 10 that only paints a shading leaves the page white.
    /// Before, the shading's area appeared in the colour from „scn“.
    /// </summary>
    [Fact]
    public void UncolouredCell_Sh_PassedOver()
    {
        using var b = G874_Render("/CS cs 0 0 1 /P scn 0 0 200 200 re f", G874_With(_ => G874_Tiling("/Sh sh", 10, [0, 0, 10, 10], 2,
            resources: TestPdf.Dict(("Shading", TestPdf.Dict(("Sh", G874_Axial(0, 10, true)))))), G874_UncolouredRgb()));
        foreach (var (x, y) in new[] { (2, 2), (7, 7), (100, 100) }) Assert.True(G874_White(G874_At(b, x, y)), $"({x}, {y}): {G874_At(b, x, y)}");
    }

    // ------------------------------------------------------------ 8.7.3.1, 8.7.4.1, 8.7.4.4: by the standard (decision 14)

    /// <summary>
    /// 8.7.3.1 sentences 16–20 and 58–59 (step b): a cell starts with the graphics state at the beginning of the pattern's parent content stream —
    /// for a pattern of a form that is the state when the form is invoked: the page sets red and draws the form, the form sets blue and fills with
    /// the pattern; the cell's „0 0 5 5 re f“ without a colour of its own is red. Before, black (the initial state of the page).
    /// </summary>
    [Fact]
    public void CellOfAPatternInAForm_StartsWithTheStateWhenTheFormIsInvoked()
    {
        var file = F_File("1 0 0 rg /Fm Do", "/XObject << /Fm 5 0 R >>",
            F_Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /Pattern << /P 6 0 R >> >>",
                "0 0 1 rg /Pattern cs /P scn 0 0 200 200 re f"),
            F_Stream("/PatternType 1 /PaintType 1 /TilingType 2 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >>", "0 0 5 5 re f"));
        Assert.True(E2_Red(F_Pixel(file, 2, 2)), $"rot erwartet: {F_Pixel(file, 2, 2)}");
        Assert.True(F_White(F_Pixel(file, 7, 7)), $"{F_Pixel(file, 7, 7)}");
    }

    /// <summary>
    /// 8.7.4.1 sentences 15–17 and 11.6.7 sentences 5–8: what the /ExtGState of a shading pattern does not give comes from the state at the beginning
    /// of the parent content stream, not from the current one — the shading (magenta in the ICC space whose perceptual table swaps cyan and magenta)
    /// stays magenta on the page after „/Perceptual ri“; in a form invoked after „/Perceptual ri“ it is cyan. Before, cyan on the page too.
    /// </summary>
    [Fact]
    public void ShadingPattern_UnspecifiedParametersFromTheStartOfTheParentStream()
    {
        const string shading = "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace [/ICCBased 6 0 R] /Coords [0 0 200 0] " +
                               "/Function << /FunctionType 2 /Domain [0 1] /C0 [0 1 0 0] /C1 [0 1 0 0] /N 1 >> /Extend [true true] >> >>";
        var page = F_File("/Perceptual ri /Pattern cs /P scn 0 0 200 200 re f", "/Pattern << /P 5 0 R >>", shading, E2_SwappedProfile);
        Assert.True(E2_Magenta(F_Pixel(page, 100, 100)), $"Seite {F_Pixel(page, 100, 100)}");
        var form = F_File("/Perceptual ri /Fm Do", "/XObject << /Fm 7 0 R >>", shading, E2_SwappedProfile,
            F_Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /Pattern << /P 5 0 R >> >>",
                "/RelativeColorimetric ri /Pattern cs /P scn 0 0 200 200 re f"));
        Assert.True(E2_Cyan(F_Pixel(form, 100, 100)), $"Form {F_Pixel(form, 100, 100)}");
    }

    /// <summary>
    /// 8.7.4.1 sentences 15–17 and 11.6.7 sentence 7: the /ExtGState of a shading pattern acts with everything that acts on painting the shading —
    /// /ca 0.5 makes the red shading half transparent (pink over white), /BM /Multiply mixes it with the green beneath (black, where /Normal
    /// would cover with red), /op true lets a shading in a Separation overprint in the preview (as
    /// „sh“ does), /SMask masks it in pattern space. Before, only /RI and /UseBlackPtComp were read.
    /// </summary>
    [Fact]
    public void ShadingPatternExtGState_OpacityBlendModeAndOverprint()
    {
        string Pattern(string state, string space = "/DeviceRGB", string c = "[1 0 0]") =>
            $"<< /PatternType 2 /ExtGState << {state} >> /Shading << /ShadingType 2 /ColorSpace {space} /Coords [0 0 200 0] " +
            $"/Function << /FunctionType 2 /Domain [0 1] /C0 {c} /C1 {c} /N 1 >> /Extend [true true] >> >>";
        var half = F_Pixel(F_File("/Pattern cs /P scn 0 0 200 200 re f", "/Pattern << /P 5 0 R >>", Pattern("/ca 0.5")), 100, 100);
        Assert.True(half is { Red: > 250, Green: > 115 and < 140, Blue: > 115 and < 140 }, $"/ca {half}");
        var multiply = F_Pixel(F_File("0 1 0 rg 0 0 200 200 re f /Pattern cs /P scn 0 0 200 200 re f", "/Pattern << /P 5 0 R >>", Pattern("/BM /Multiply")), 100, 100);
        Assert.True(multiply is { Red: < 20, Green: < 20, Blue: < 20 }, $"/BM {multiply}");
        var overprint = F_File("0 0 1 0 k 0 0 200 200 re f /Pattern cs /P scn 50 0 100 200 re f", "/Pattern << /P 5 0 R >>",
            Pattern("/op true", F_Cyan, "[1]"));
        Assert.True(F_Greenish(E2_Pixel(overprint, 100, 100, overprintPreview: true)), $"/op {E2_Pixel(overprint, 100, 100, true)}");
        Assert.False(F_Greenish(E2_Pixel(overprint, 100, 100)), $"ohne Vorschau {E2_Pixel(overprint, 100, 100)}");
        // a luminosity mask white over the left half (in pattern space, here the page): the shading only there
        var masked = F_File("/Pattern cs /P scn 0 0 200 200 re f", "/Pattern << /P 5 0 R >>",
            Pattern("/SMask << /Type /Mask /S /Luminosity /G 6 0 R >>"),
            F_Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >>", "1 g 0 0 100 200 re f"));
        Assert.True(E2_Red(F_Pixel(masked, 50, 100)), $"/SMask links {F_Pixel(masked, 50, 100)}");
        Assert.True(F_White(F_Pixel(masked, 150, 100)), $"/SMask rechts {F_Pixel(masked, 150, 100)}");
    }

    /// <summary>
    /// 8.7.4.4 sentences 29–31: an Indexed space with a CIE-based base is blended in the base — a free-form mesh over Indexed with Lab as the base,
    /// entry 0 L* 50 a* +80 (a red-purple), entry 1 L* 50 a* −80 (a green-teal), from left to right: in the middle the Lab mean, a neutral grey.
    /// Before, the two sRGB colours were averaged (a dull purple-blue).
    /// </summary>
    [Fact]
    public void IndexedMesh_WithCieBase_BlendedInTheBase()
    {
        byte[] data = [0, 0, 0, 0, 0, 255, 0, 255, 0, 0, 255, 0, 1, 255, 255, 255];
        var file = F_File("/Sh sh", "/Shading << /Sh 5 0 R >>", F_Stream("/ShadingType 4 /ColorSpace [/Indexed [/Lab << /WhitePoint [0.9505 1 1.089] >>] 1 " +
            "<80E680 801A80>] /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 200 0 200 0 1]", data));
        var middle = F_Pixel(file, 100, 100);
        Assert.True(Math.Abs(middle.Red - middle.Green) < 16 && Math.Abs(middle.Green - middle.Blue) < 16 && Math.Abs(middle.Red - middle.Blue) < 16,
            $"neutrales Grau erwartet: {middle}");
    }

    /// <summary>
    /// 8.7.4.4 sentences 29–31 for patches (types 6/7): the corner values of an Indexed space go into the base at once, the base is blended — a Coons
    /// patch from palette entry 0 (red) on the left to entry 2 (blue) on the right never shows entry 1 (green) in the middle, as blending the
    /// indices would. Before, the indices were blended.
    /// </summary>
    [Fact]
    public void IndexedPatch_BlendsTheColoursNotThePaletteEntries()
    {
        // one Coons patch over the page: 12 points (one byte each for x and y), the edges straight, corner values 0 (left) and 2 (right)
        byte[] points = [0, 0, 0, 85, 0, 170, 0, 255, 85, 255, 170, 255, 255, 255, 255, 170, 255, 85, 255, 0, 170, 0, 85, 0];
        byte[] data = [0, .. points, 0, 0, 2, 2];
        var file = F_File("/Sh sh", "/Shading << /Sh 5 0 R >>", F_Stream("/ShadingType 6 /ColorSpace [/Indexed /DeviceRGB 2 <FF0000 00FF00 0000FF>] " +
            "/BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 200 0 200 0 255]", data));
        var middle = F_Pixel(file, 100, 100);
        Assert.True(middle.Green < 60 && middle.Red > 60 && middle.Blue > 60, $"violett erwartet: {middle}");
    }

    // ------------------------------------------------------------ 8.7.3.1, 8.7.4.5.2: finer sampling, tiling types (decision 13)

    /// <summary>
    /// 8.7.3.1 sentences 26–30 and 33: /TilingType 1 and 3 space the cells by whole device pixels — a step of 10.3 at scale 1 becomes 10 pixels:
    /// cell 19 lies at 190–195 (red at 192, white at 197); with type 2 it lies at 195.7–200.7 (TilingType2_KeepsTheStepOnAverage). Before, types 1
    /// and 3 kept the exact step as well.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void TilingType1And3_StepInWholeDevicePixels(int tilingType)
    {
        using var b = G874_Render("/Pattern cs /P scn 0 0 200 200 re f", G874_With(_ => G874_Tiling("1 0 0 rg 0 0 5 5 re f", 10.3, [0, 0, 10, 10], tilingType: tilingType)));
        foreach (var x in new[] { 2, 102, 192 }) Assert.True(G874_Red(G874_At(b, x, 2)), $"{x}: {G874_At(b, x, 2)}");
        foreach (var x in new[] { 107, 197 }) Assert.True(G874_White(G874_At(b, x, 2)), $"{x}: {G874_At(b, x, 2)}");
    }

    /// <summary>
    /// 8.7.4.5.2 sentences 1–3: the function of a type 1 shading need not be continuous, and a jump stays a jump — blue left of x 0.5 of /Domain,
    /// red right of it, /Domain spread over 4 000 points of which 200 are visible: at most two pixels of a row are neither blue nor red, blue at
    /// x 97 and red at x 103. Before, 65 samples over 4 000 points blurred the jump over some 60 pixels.
    /// </summary>
    [Fact]
    public void FunctionBased_DiscontinuityStaysSharp()
    {
        var file = F_File("/Sh sh", "/Shading << /Sh << /ShadingType 1 /ColorSpace /DeviceRGB /Domain [0 1 0 1] /Matrix [4000 0 0 200 -1900 0] " +
                                    "/Function 5 0 R >> >>",
            F_Stream("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", "{ pop 0.5 gt { 1 0 0 } { 0 0 1 } ifelse }"));
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        SKColor At(int x) => bitmap.GetPixel(x, 100);
        var mixed = Enumerable.Range(60, 80).Count(x => !F_Blue(At(x)) && !E2_Red(At(x)));
        Assert.True(mixed <= 2, $"{mixed} Mischpixel");
        Assert.True(F_Blue(At(97)), $"{At(97)}");
        Assert.True(E2_Red(At(103)), $"{At(103)}");
    }

    // ------------------------------------------------------------ 8.6.7: overprint preview (decision 12)

    /// <summary>
    /// 8.6.7 sentences 21–22: in mode 1 a component other than 0 replaces that of what was painted before, only the zeros leave it — „0.2 0 0 0 k“
    /// over full cyan with /OP, /op, /OPM 1 in the preview shows the light cyan of „0.2 0 0 0 k“ in the red channel (the cyan plate), not the full
    /// cyan beneath it darkened. Before, the colours were multiplied: red stayed at the 0 of the full cyan.
    /// </summary>
    [Fact]
    public void OverprintMode1_NonZeroComponentReplacesItsPlate()
    {
        var alone = F_Pixel(F_File("0.2 0 0 0 k 0 0 200 200 re f"), 100, 100);
        var over = F_Pixel(F_File("1 0 0 0 k 0 0 200 200 re f /OP1 gs 0.2 0 0 0 k 50 0 100 200 re f", F_States), 100, 100, true);
        Assert.True(Math.Abs(over.Red - alone.Red) <= 3, $"Rotkanal {over} gegen {alone}");
        // the zeros keep the plates beneath: yellow over cyan still mixes to green (OverprintMode1_ZeroAsWrittenInTheFile)
        Assert.True(F_Greenish(F_Pixel(F_File("1 0 0 0 k 0 0 120 200 re f /OP1 gs 0 0 1 0 k 80 0 120 200 re f", F_States), 100, 100, true)));
    }

    /// <summary>
    /// 8.6.7 sentences 5–6 and 10–12: a fill with a shading pattern in a Separation paints only that colorant, like „sh“ with the same shading — over
    /// yellow with /OP and /op the preview mixes the cyan to green; without the preview cyan covers. Before, a shading pattern never overprinted.
    /// </summary>
    [Fact]
    public void ShadingPatternInASeparation_OverprintsAsSh()
    {
        var file = F_File("0 0 1 0 k 0 0 200 200 re f /OPOnly gs /Pattern cs /P scn 50 0 100 200 re f", $"{F_States} /Pattern << /P 5 0 R >>",
            $"<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace {F_Cyan} /Coords [0 0 200 0] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [1] /N 1 >> /Extend [true true] >> >>");
        Assert.True(F_Greenish(E2_Pixel(file, 100, 100, overprintPreview: true)), $"{E2_Pixel(file, 100, 100, true)}");
        Assert.False(F_Greenish(E2_Pixel(file, 100, 100)), $"ohne Vorschau {E2_Pixel(file, 100, 100)}");
    }
}
