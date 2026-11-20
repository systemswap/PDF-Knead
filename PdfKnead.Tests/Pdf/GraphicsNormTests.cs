using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Graphics (ISO 32000-2, chapter 8; Konformitätsabgleich M6, docs/normabgleich/M6-grafik-farbe.md). Nachweise „GN.“ in the report.
/// </summary>
public sealed class GraphicsNormTests
{
    /// <summary>One page of 200 × 200 with the given content, rendered at 72 dpi (one pixel per unit; y counted from the top).</summary>
    private static SKBitmap Rendered(string content)
    {
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        return SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
    }

    private static bool Blue(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y) is { Blue: > 200, Red: < 60 };

    private static bool White(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y) is { Red: > 250, Green: > 250, Blue: > 250 };

    /// <summary>
    /// One page of 200 × 200 painting each shading of <paramref name="shadings"/> (/Sh0, /Sh1 …) with "sh" into its own square of 40 × 40
    /// (left to right at y 80–120, x 0, 50, 100, 150), rendered at 72 dpi.
    /// </summary>
    private static SKBitmap Shaded(params string[] shadings) => ShadedUnder("", shadings);

    /// <summary>As <see cref="Shaded(string[])"/>, with <paramref name="matrix"/> (a "cm") before each "sh".</summary>
    private static SKBitmap ShadedUnder(string matrix, params string[] shadings)
    {
        var resources = string.Join(" ", shadings.Select((_, i) => $"/Sh{i} {5 + i} 0 R"));
        var content = string.Join(" ", shadings.Select((_, i) => $"q {i * 50} 80 40 40 re W n {matrix} /Sh{i} sh Q"));
        var file = DocumentStructureNormTests.FileOf([
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Shading << {resources} >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", .. shadings]);
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        return SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
    }

    /// <summary>Red (t = 0) to blue (t = 1) in DeviceRGB.</summary>
    private const string RedToBlue = "/ColorSpace /DeviceRGB /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>";

    /// <summary>
    /// 8.7.4.5.3 and 8.7.4.5.4: an axial shading whose axis has coincident end points, and a radial one whose radii are both 0 (same or
    /// different centres) or whose circles coincide, paint nothing — also with /Extend. Before, the end colour filled the whole area.
    /// A proper axial shading with /Extend still fills its square.
    /// </summary>
    [Fact]
    public void Shading_Degenerate_PaintsNothing()
    {
        using var bitmap = Shaded(
            $"<< /ShadingType 2 {RedToBlue} /Coords [20 100 20 100] /Extend [true true] >>",
            $"<< /ShadingType 3 {RedToBlue} /Coords [70 100 0 70 100 0] /Extend [true true] >>",
            $"<< /ShadingType 3 {RedToBlue} /Coords [110 100 0 130 100 0] /Extend [true true] >>",
            $"<< /ShadingType 3 {RedToBlue} /Coords [170 100 10 170 100 10] /Extend [true true] >>");
        Assert.True(White(bitmap, 20, 100) && White(bitmap, 5, 85));
        Assert.True(White(bitmap, 70, 100) && White(bitmap, 55, 85));
        Assert.True(White(bitmap, 120, 100) && White(bitmap, 105, 85));
        Assert.True(White(bitmap, 170, 100) && White(bitmap, 155, 85));
        using var proper = Shaded($"<< /ShadingType 2 {RedToBlue} /Coords [10 100 30 100] /Extend [true true] >>");
        Assert.True(Blue(proper, 35, 100));
        Assert.False(White(proper, 5, 100));
    }

    /// <summary>
    /// 8.7.3.2 and 8.7.3.3: a coloured tiling pattern paints in the colours of its cell; an uncoloured one (PaintType 2) in the colour given
    /// before its name, in the underlying space of [/Pattern /DeviceRGB] — the cell's own „rg“ has no effect. Cells of 10 at a spacing of 20.
    /// </summary>
    [Theory]
    [InlineData(1, "/Pattern cs /P0 scn")]
    [InlineData(2, "/Cs0 cs 0 0 1 /P0 scn")]
    public void TilingPattern_ColouredOrUncoloured(int paintType, string select)
    {
        const string cell = "1 0 0 rg 0 0 10 10 re f";
        var content = select + " 0 0 200 200 re f";
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /ColorSpace << /Cs0 [/Pattern /DeviceRGB] >> /Pattern << /P0 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /Pattern /PatternType 1 /PaintType {paintType} /TilingType 1 /BBox [0 0 10 10] /XStep 20 /YStep 20 /Resources << >> /Length {cell.Length} >>\nstream\n{cell}\nendstream");
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        var inCell = bitmap.GetPixel(25, 175);   // page (25, 25): in the cell at (20, 20)
        Assert.True(paintType == 1 ? inCell is { Red: > 200, Blue: < 60 } : inCell is { Blue: > 200, Red: < 60 });
        Assert.True(White(bitmap, 15, 185));     // page (15, 15): between the cells
    }

    /// <summary>
    /// 8.9.5.4: an image hidden by its /OC shows the first of its /Alternates (left); an image without /OC is printed as the alternate marked
    /// /DefaultForPrinting and shown as itself on the screen (right). Before, /Alternates were not read: the left place stayed empty and
    /// the right one printed red (docs/FEHLERGEDAECHTNIS.md, No. 763). Base images red, the alternate blue.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Image_Alternates_ByOptionalContentAndForPrinting(bool printing)
    {
        const string content = "q 100 0 0 100 0 50 cm /Im0 Do Q q 100 0 0 100 100 50 cm /Im1 Do Q";
        const string image = "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3";
        var file = DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im0 5 0 R /Im1 8 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< {image} /OC 6 0 R /Alternates [<< /Image 7 0 R >>] >>\nstream\n\xff\x00\x00\nendstream",
            "<< /Type /OCG /Name (Ebene) >>",
            $"<< {image} >>\nstream\n\x00\x00\xff\nendstream",
            $"<< {image} /Alternates [<< /Image 7 0 R /DefaultForPrinting true >>] >>\nstream\n\xff\x00\x00\nendstream");
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page, forPrint: printing), doc, page, 1, new ImageCache());
        Assert.True(Blue(bitmap, 50, 100));
        var right = bitmap.GetPixel(150, 100);
        Assert.True(printing ? right is { Blue: > 200, Red: < 60 } : right is { Red: > 200, Blue: < 60 });
    }

    /// <summary>A page of 200 × 200 drawing /Im0 over the left half and /Im1 over the right half (y 50–150) from the given image objects (5 and 6, then 7 …).</summary>
    private static SKBitmap TwoImages(params string[] objects)
    {
        const string content = "q 100 0 0 100 0 50 cm /Im0 Do Q q 100 0 0 100 100 50 cm /Im1 Do Q";
        var file = DocumentStructureNormTests.FileOf([
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im0 5 0 R /Im1 6 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", .. objects]);
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        return SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
    }

    /// <summary>
    /// 8.9.5.1 (Table 87): an /SMask overrides the /Mask of the image — also a colour key. Two images of two pixels (red, blue), both with a
    /// colour key for red; the left one has an opaque /SMask and keeps its red, the right one loses it. Before, both lost it
    /// (docs/FEHLERGEDAECHTNIS.md, No. 764).
    /// </summary>
    [Fact]
    public void Image_SMaskOverridesTheColourKey()
    {
        const string image = "/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Mask [255 255 0 0 0 0] /Length 6";
        using var bitmap = TwoImages(
            $"<< {image} /SMask 7 0 R >>\nstream\n\xff\x00\x00\x00\x00\xff\nendstream",
            $"<< {image} >>\nstream\n\xff\x00\x00\x00\x00\xff\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 2 >>\nstream\n\xff\xff\nendstream");
        Assert.True(bitmap.GetPixel(25, 100) is { Red: > 200, Blue: < 60 });   // left image, red pixel: shown
        Assert.True(White(bitmap, 125, 100));                                   // right image, red pixel: keyed out
        Assert.True(Blue(bitmap, 175, 100));
    }

    /// <summary>
    /// 8.9.6.4: the colour key also holds for an image read reduced (large images): its pixels do not count, the share of the others gives
    /// the opacity. A 4 × 4 image, left half red (keyed), right half blue, read at a quarter. Before, the reduced image kept the red
    /// (docs/FEHLERGEDAECHTNIS.md, No. 764).
    /// </summary>
    [Fact]
    public void Image_ColourKey_AlsoWhenReduced()
    {
        var row = "\xff\x00\x00\xff\x00\x00\x00\x00\xff\x00\x00\xff";
        var data = string.Concat(Enumerable.Repeat(row, 4));
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Mask [255 255 0 0 0 0] /Length {data.Length} >>\nstream\n{data}\nendstream");
        using var doc = PdfDocument.Open(file);
        var stream = (PdfKnead.Core.Pdf.Objects.PdfStream)doc.GetObject(3);
        var image = PdfKnead.Core.Pdf.Graphics.PdfImageDecoder.DecodeReduced(new PdfKnead.Core.Pdf.Graphics.ImageSource(stream, null, null, false, 0), doc, 4);
        Assert.NotNull(image);
        Assert.Equal(2, image.Width);
        Assert.Equal(0, image.Pixels[3]);                        // left: keyed out
        Assert.Equal(255, image.Pixels[7]);                      // right: opaque blue
        Assert.True(image.Pixels[6] > 200 && image.Pixels[4] < 60);
    }

    /// <summary>
    /// 8.9.6.4: a colour key also holds for a JPEG (the samples the decoder delivers). 16 × 8 pixels, left half red (keyed with some
    /// tolerance for the compression), right half blue. Before, JPEGs kept their keyed colours (docs/FEHLERGEDAECHTNIS.md, No. 764).
    /// </summary>
    [Fact]
    public void Image_ColourKey_OnAJpeg()
    {
        using var source = new SKBitmap(16, 8);
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 16; x++)
            source.SetPixel(x, y, x < 8 ? new SKColor(255, 0, 0) : new SKColor(0, 0, 255));
        using var encoded = source.Encode(SKEncodedImageFormat.Jpeg, 100);
        var jpeg = System.Text.Encoding.Latin1.GetString(encoded.ToArray());
        var image = $"/Type /XObject /Subtype /Image /Width 16 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Mask [200 255 0 70 0 70] /Length {jpeg.Length}";
        using var bitmap = TwoImages($"<< {image} >>\nstream\n{jpeg}\nendstream", $"<< {image} >>\nstream\n{jpeg}\nendstream");
        Assert.True(White(bitmap, 25, 100));   // red: keyed out
        Assert.True(Blue(bitmap, 75, 100));
    }

    /// <summary>
    /// 8.9.7: /L gives the length of the data of an inline image. Here RunLength data whose 40 bytes read " EI Q q Q …" — printable text after
    /// an "EI", so the search took it for the end of the image and the rest for operators (docs/FEHLERGEDAECHTNIS.md, No. 765). Forty grey
    /// pixels of 5 units; the last one has the value 32 (a space), the third 73 ("I").
    /// </summary>
    [Fact]
    public void InlineImage_LengthEndsTheData()
    {
        var samples = " EI " + string.Concat(Enumerable.Repeat("Q q Q ", 6));
        var data = "\x27" + samples + "\x80";
        var content = $"q 200 0 0 10 0 0 cm BI /W 40 /H 1 /CS /G /BPC 8 /F /RL /L {data.Length} ID {data}\nEI Q";
        using var bitmap = Rendered(content);
        Assert.InRange(bitmap.GetPixel(197, 195).Red, 26, 38);
        Assert.InRange(bitmap.GetPixel(12, 195).Red, 67, 79);
    }

    /// <summary>A type 4 mesh of the given vertices (flag, x, y, 8 bits each; colour blue in DeviceRGB) as a stream object.</summary>
    private static string BlueMesh(IEnumerable<(int Flag, int X, int Y)> vertices)
    {
        var data = new string(vertices.SelectMany(v => new[] { (char)v.Flag, (char)v.X, (char)v.Y, (char)0, (char)0, (char)255 }).ToArray());
        return "<< /ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 " +
               $"/Decode [0 255 0 255 0 1 0 1 0 1] /Length {data.Length} >>\nstream\n{data}\nendstream";
    }

    /// <summary>
    /// 8.7.4.5.5 (Table 81): only the two lowest bits of an edge flag count — 5 is 1, the new vertex continues at edge BC. Before, 5 was
    /// read as „edge AC“ (docs/FEHLERGEDAECHTNIS.md, No. 761).
    /// </summary>
    [Fact]
    public void Shading_EdgeFlags_OnlyTheLowestTwoBits()
    {
        var mesh = BlueMesh([(0, 0, 0), (0, 200, 0), (0, 0, 200), (5, 200, 200)]);
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Shading << /Sh0 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Length 7 >>\nstream\n/Sh0 sh\nendstream", mesh);
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        Assert.True(Blue(bitmap, 20, 180));    // triangle ABC (page y 20)
        Assert.True(Blue(bitmap, 180, 100));   // only in B C D (x + y > 200, x > y); A C D would leave it white
    }

    /// <summary>
    /// Security (docs/FEHLERGEDAECHTNIS.md, No. 762): the area that meshes cover counts towards the work of a page, not only their
    /// triangles — at most about 32 sheets of A0 for all drawings together, then the page is shown in part. Here a mesh of 4 000
    /// triangles each covering half the page, drawn ten times: before, all ten were kept (1 % of the budget by triangles); now
    /// the page ends after about four.
    /// </summary>
    [Fact]
    public void Meshes_TheirAreaCountsTowardsTheWork()
    {
        (int, int)[] corners = [(0, 0), (200, 0), (0, 200), (200, 200)];
        var mesh = BlueMesh(Enumerable.Range(0, 4002).Select(i => (i < 3 ? 0 : 1, corners[i % 4].Item1, corners[i % 4].Item2)));
        var content = string.Concat(Enumerable.Repeat("/Sh0 sh ", 10));
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Shading << /Sh0 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", mesh);
        using var doc = PdfDocument.Open(file);
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        Assert.InRange(list.Count, 3, 5);
        Assert.NotEmpty(doc.Problems);
    }

    /// <summary>
    /// A valid shading of tiny extent in its own space, enlarged by the matrix (axis of 0.00002 units, 20 points on the page; radial
    /// circles of 0.00001): painted as a gradient — red before the axis, blue after it. Before, the end colour filled the whole area
    /// (the drawing library takes such sizes as degenerate; docs/FEHLERGEDAECHTNIS.md, No. 760).
    /// </summary>
    [Fact]
    public void Shading_TinyButValid_PaintsTheGradient()
    {
        using var bitmap = ShadedUnder("1000000 0 0 1000000 0 0 cm",
            $"<< /ShadingType 2 {RedToBlue} /Coords [0.00001 0.0001 0.00003 0.0001] /Extend [true true] >>",
            $"<< /ShadingType 3 {RedToBlue} /Coords [0.00007 0.0001 0 0.00007 0.0001 0.00001] /Extend [false true] >>");
        bool Red(int x, int y) => bitmap.GetPixel(x, y) is { Red: > 200, Blue: < 60 };
        Assert.True(Red(5, 100));
        Assert.True(Blue(bitmap, 35, 100));
        Assert.True(Red(70, 100));
        Assert.True(Blue(bitmap, 85, 85));
    }

    /// <summary>
    /// 8.10.2 (Table 93): a form XObject with /PieceInfo carries /LastModified — as written, whatever the source had (the time of writing,
    /// as for pages, No. 755). Before, the form went out without it (docs/FEHLERGEDAECHTNIS.md, No. 766).
    /// </summary>
    [Fact]
    public void Form_WithPieceInfo_WrittenWithLastModified()
    {
        const string content = "/Fm0 Do";
        const string form = "0 0 1 rg 0 0 10 10 re f";
        using var saved = DocumentStructureNormTests.Saved(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Fm0 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << >> /PieceInfo << /Test << /LastModified (D:20260101000000Z) /Private 1 >> >> /Length {form.Length} >>\nstream\n{form}\nendstream"));
        var written = saved.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Fm0")) as PdfStream;
        Assert.NotNull(written!.Dictionary.GetDictionary(PdfName.Get("PieceInfo")));
        Assert.NotNull(written.Dictionary.GetString(PdfName.Get("LastModified")));
    }

    /// <summary>A document whose /OCProperties /D is <paramref name="config"/>, with layers 3 … as given (object 3 is the first layer).</summary>
    private static PdfDocument Layers(string config, params string[] layers)
    {
        var refs = string.Join(" ", layers.Select((l, i) => l.Contains("/Type /OCG ") ? $"{3 + i} 0 R" : null).OfType<string>());
        return PdfDocument.Open(DocumentStructureNormTests.FileOf([
            $"<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [{refs}] /D {config} >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>", .. layers]));
    }

    private static bool Hidden(PdfDocument doc, int number, OptionalContentUse use = OptionalContentUse.View, string language = "de-DE") =>
        OptionalContent.For(doc, use, language).IsHidden(doc.GetObject(number), null);

    /// <summary>
    /// 8.11.2.3: a layer counts only if one of its intents (default /View) is among those of the configuration (default /View); otherwise it
    /// has no effect on visibility. An empty /Intent of the configuration: no layer counts. Before, a layer meant for /Design only that was
    /// off hid its content on the screen (docs/FEHLERGEDAECHTNIS.md, No. 767).
    /// </summary>
    [Fact]
    public void Layers_IntentDecidesWhetherALayerCounts()
    {
        using var doc = Layers("<< /OFF [3 0 R 4 0 R 5 0 R] >>",
            "<< /Type /OCG /Name (Entwurf) /Intent /Design >>", "<< /Type /OCG /Name (Ansicht) >>", "<< /Type /OCG /Name (Beides) /Intent [/Design /View] >>",
            "<< /Type /OCMD /OCGs [3 0 R 4 0 R] /P /AnyOn >>");
        Assert.False(Hidden(doc, 3));   // Design only: no effect
        Assert.True(Hidden(doc, 4));
        Assert.True(Hidden(doc, 5));
        Assert.True(Hidden(doc, 6));    // the membership counts only the layer that counts: off
        using var none = Layers("<< /OFF [3 0 R] /Intent [] >>", "<< /Type /OCG /Name (Ansicht) >>");
        Assert.False(Hidden(none, 3));
        using var all = Layers("<< /OFF [3 0 R] /Intent /All >>", "<< /Type /OCG /Name (Entwurf) /Intent /Design >>");
        Assert.True(Hidden(all, 3));
    }

    /// <summary>
    /// 8.11.4.4, 8.11.4.5: the usage application dictionaries of /AS apply for their event — /View on the screen, /Print when printing,
    /// /Export when exporting; for each layer all named categories must recommend ON, also across several dictionaries; /Language compares
    /// with the language of the program (exact, else language without region and /Preferred ON). Before, only /Print with the category /Print
    /// was read (docs/FEHLERGEDAECHTNIS.md, No. 767).
    /// </summary>
    [Fact]
    public void Layers_UsageApplicationsForViewPrintAndExport()
    {
        using var doc = Layers(
            "<< /AS [ << /Event /View /OCGs [3 0 R 4 0 R] /Category [/View] >> << /Event /Print /OCGs [3 0 R] /Category [/Print] >> " +
            "<< /Event /Export /OCGs [4 0 R] /Category [/Export] >> << /Event /View /OCGs [5 0 R 6 0 R 7 0 R] /Category [/Language] >> " +
            "<< /Event /View /OCGs [4 0 R] /Category [/Zoom] >> ] >>",
            "<< /Type /OCG /Name (Nur Papier) /Usage << /View << /ViewState /OFF >> /Print << /PrintState /ON >> >> >>",
            "<< /Type /OCG /Name (Nur Bildschirm) /Usage << /View << /ViewState /ON >> /Export << /ExportState /OFF >> /Zoom << /min 4 >> >> >>",
            "<< /Type /OCG /Name (Englisch) /Usage << /Language << /Lang (en-US) >> >> >>",
            "<< /Type /OCG /Name (Deutsch) /Usage << /Language << /Lang (de-AT) /Preferred /ON >> >> >>",
            "<< /Type /OCG /Name (Deutsch 2) /Usage << /Language << /Lang (de-CH) >> >> >>");
        Assert.True(Hidden(doc, 3));                                 // ViewState OFF on the screen
        Assert.False(Hidden(doc, 3, OptionalContentUse.Print));      // PrintState ON
        Assert.False(Hidden(doc, 4));                                // ViewState ON; /Zoom gives no recommendation
        Assert.True(Hidden(doc, 4, OptionalContentUse.Export));      // ExportState OFF
        Assert.False(Hidden(doc, 4, OptionalContentUse.Print));      // no /Print application for it
        Assert.True(Hidden(doc, 5));                                 // en-US: no match for de-DE
        Assert.False(Hidden(doc, 6));                                // de-AT: partial match, /Preferred ON
        Assert.True(Hidden(doc, 7));                                 // de-CH: partial match without /Preferred
        Assert.False(Hidden(doc, 5, language: "en-US"));             // exact match: only that one ON
        Assert.True(Hidden(doc, 6, language: "en-US"));
    }

    /// <summary>
    /// 8.11.4.5: a tiling pattern cell is drawn for the same use as its page — printed, its layers in their print state. A layer off only
    /// when printed (/AS /Print, /PrintState /OFF) in the cell: on the screen blue cells, printed none. Before, cells and soft masks were
    /// always built with the screen state (docs/FEHLERGEDAECHTNIS.md, No. 767).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Layers_InAPatternCell_FollowThePrintState(bool printing)
    {
        const string content = "/Pattern cs /P0 scn 0 0 200 200 re f";
        const string cell = "/OC /L0 BDC 0 0 1 rg 0 0 10 10 re f EMC";
        var file = DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R] /D << /AS [<< /Event /Print /OCGs [6 0 R] /Category [/Print] >>] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Pattern << /P0 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 20 /YStep 20 /Resources << /Properties << /L0 6 0 R >> >> /Length {cell.Length} >>\nstream\n{cell}\nendstream",
            "<< /Type /OCG /Name (Nur Bildschirm) /Usage << /Print << /PrintState /OFF >> >> >>");
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page, forPrint: printing), doc, page, 1, new ImageCache());
        Assert.Equal(!printing, Blue(bitmap, 25, 175));
    }

    /// <summary>A CMYK profile (v2, printer class, Lab) whose A2B0 is a lut8 table of the given grid, every entry mid grey.</summary>
    private static byte[] CmykProfile(int grid, int variant)
    {
        var body = new List<byte>();
        body.AddRange("mft1"u8.ToArray());
        body.AddRange(new byte[4]);
        body.AddRange([4, 3, (byte)grid, 0]);
        foreach (var v in new[] { 65536, 0, 0, 0, 65536, 0, 0, 0, 65536 }) body.AddRange(BigEndian(v));
        for (var i = 0; i < 4; i++) for (var b = 0; b < 256; b++) body.Add((byte)b);
        body.AddRange(Enumerable.Repeat((byte)0x80, (int)Math.Pow(grid, 4) * 3));
        for (var i = 0; i < 3; i++) for (var b = 0; b < 256; b++) body.Add((byte)b);
        const int tagAt = 128 + 4 + 12;
        var header = new List<byte>();
        header.AddRange(BigEndian(tagAt + body.Count));
        header.AddRange("none"u8.ToArray());
        header.AddRange([2, 0x10, 0, 0]);
        header.AddRange("prtrCMYKLab "u8.ToArray());
        header.AddRange(new byte[12]);
        header.AddRange("acspMSFT"u8.ToArray());
        header.AddRange(new byte[4]);
        header.AddRange("none"u8.ToArray());
        header.AddRange(BigEndian(variant));
        header.AddRange(new byte[12]);
        foreach (var v in new[] { 63190, 65536, 54061 }) header.AddRange(BigEndian(v));
        header.AddRange("none"u8.ToArray());
        header.AddRange(new byte[44]);
        header.AddRange(BigEndian(1));
        header.AddRange("A2B0"u8.ToArray());
        header.AddRange(BigEndian(tagAt));
        header.AddRange(BigEndian(body.Count));
        return [.. header, .. body];

        static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    }

    /// <summary>
    /// Security (8.6.5.5; docs/FEHLERGEDAECHTNIS.md, No. 759): the colour tables of ICC profiles stay with the colour spaces within a budget per
    /// document; a profile beyond it is used as if unreadable — its /Alternate. Before, a file of 626 KB with twenty profiles took 2.9 GB and,
    /// with a limit of 1.1 GB for the process, ended it (measured with the command line). Here: room for the tables of two profiles of three.
    /// </summary>
    [Fact]
    public void IccTables_WithinTheBudget()
    {
        var profiles = Enumerable.Range(0, 3).Select(i => System.Text.Encoding.Latin1.GetString(CmykProfile(9, i))).ToArray();
        const string content = "/C0 cs 0.2 0.3 0.4 0.1 sc 0 0 10 10 re f /C1 cs 0.2 0.3 0.4 0.1 sc 20 0 10 10 re f /C2 cs 0.2 0.3 0.4 0.1 sc 40 0 10 10 re f";
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 20] /Resources << /ColorSpace << /C0 [/ICCBased 5 0 R] /C1 [/ICCBased 6 0 R] /C2 [/ICCBased 7 0 R] >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /N 4 /Alternate /DeviceCMYK /Length {profiles[0].Length} >>\nstream\n{profiles[0]}\nendstream",
            $"<< /N 4 /Alternate /DeviceCMYK /Length {profiles[1].Length} >>\nstream\n{profiles[1]}\nendstream",
            $"<< /N 4 /Alternate /DeviceCMYK /Length {profiles[2].Length} >>\nstream\n{profiles[2]}\nendstream");
        using var doc = PdfDocument.Open(file);
        doc.IccTableBudget = 2L * 9 * 9 * 9 * 9 * 3;
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        bool Grey(int x) => bitmap.GetPixel(x, 15) is var c && Math.Abs(c.Red - c.Blue) < 8 && c.Red < 200;
        Assert.True(Grey(5));
        Assert.True(Grey(25));
        Assert.False(Grey(45));   // the third: its /Alternate, DeviceCMYK — not the grey of the table
    }

    /// <summary>
    /// 8.5.3.2: a subpath that is a single point is stroked only with round caps (a dot); with butt and projecting square caps nothing is
    /// painted, nor for a trailing m. Before, the projecting square cap painted a square (docs/FEHLERGEDAECHTNIS.md, No. 758). A line with
    /// projecting square caps keeps them.
    /// </summary>
    [Fact]
    public void Stroke_SinglePointSubpath_OnlyRoundCapsPaint()
    {
        using var bitmap = Rendered("0 0 1 RG 20 w 1 J 50 150 m 50 150 l S 0 J 100 150 m 100 150 l S 2 J 150 150 m 150 150 l S " +
                                    "1 J 50 50 m 50 50 l h S 2 J 100 50 m 150 50 l S 1 J 180 50 m S");
        Assert.True(Blue(bitmap, 50, 50));      // round cap: a dot
        Assert.False(Blue(bitmap, 100, 50));    // butt cap: nothing
        Assert.False(Blue(bitmap, 150, 50));    // projecting square cap: nothing (before: a square)
        Assert.True(Blue(bitmap, 50, 150));     // closed single point, round cap: a dot
        Assert.True(Blue(bitmap, 95, 150));     // the line with projecting caps reaches beyond its end points
        Assert.True(Blue(bitmap, 155, 150));
        Assert.False(Blue(bitmap, 180, 150));   // trailing m: nothing
    }

    // ---- Nachweise added in the Nacharbeit (12.11.2026) for rows that stood as „umgesetzt“ without one

    /// <summary>
    /// 8.10.4: a reference XObject (/Ref) is shown by its proxy — the form's own content — as by any reader that does not import the other
    /// file; PDF-Knead never opens the file it names (M6 row 55).
    /// </summary>
    [Fact]
    public void ReferenceXObject_ShowsItsProxy()
    {
        const string content = "/Fm0 Do", proxy = "0 0 1 rg 50 50 100 100 re f";
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Fm0 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Ref << /F (fremd.pdf) /Page 0 >> /Length {proxy.Length} >>\nstream\n{proxy}\nendstream");
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        Assert.True(Blue(bitmap, 100, 100));
        Assert.True(White(bitmap, 20, 20));
    }

    /// <summary>As <see cref="Rendered"/>, with the extended graphics state /G of the given entries.</summary>
    private static SKBitmap RenderedWithState(string content, string state)
    {
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /ExtGState << /G << /Type /ExtGState {state} >> >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        return SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
    }

    /// <summary>
    /// 8.4.4: the operators of the graphics state — w (width), J (caps), j and M (joins and the miter limit), d (dash), ri (intent), i (flatness,
    /// device dependent: read and left aside) and gs (/LW here) — each change the stroke as 8.4.3 describes. A horizontal line at y 100 from x 50
    /// to 150 (M6 row 5).
    /// </summary>
    [Fact]
    public void GraphicsStateOperators_ChangeTheStroke()
    {
        const string line = "50 100 m 150 100 l S";
        using (var wide = Rendered($"0 0 1 RG 20 w {line}"))
        {
            Assert.True(Blue(wide, 100, 91) && Blue(wide, 100, 109));
            Assert.True(White(wide, 100, 113) && White(wide, 45, 100));   // butt caps end at the end points
        }
        using (var projecting = Rendered($"0 0 1 RG 20 w 2 J {line}"))
            Assert.True(Blue(projecting, 45, 100));
        using (var dashed = Rendered($"0 0 1 RG 20 w [10 10] 0 d {line}"))
            Assert.True(Blue(dashed, 55, 100) && White(dashed, 65, 100) && Blue(dashed, 75, 100));
        // a peak at (100, 150): the miter (ratio 2.24 below the limit 10) reaches up to y 172, a bevel (2 j, or the limit 1) ends at y 154.5
        const string peak = "50 50 m 100 150 l 150 50 l S";
        using (var miter = Rendered($"0 0 1 RG 20 w 0 j 10 M {peak}"))
            Assert.True(Blue(miter, 100, 35));
        using (var bevel = Rendered($"0 0 1 RG 20 w 2 j {peak}"))
            Assert.True(White(bevel, 100, 35) && Blue(bevel, 100, 48));
        using (var limited = Rendered($"0 0 1 RG 20 w 0 j 1 M {peak}"))
            Assert.True(White(limited, 100, 35));
        using (var state = RenderedWithState($"0 0 1 RG 20 w /G gs {line}", "/LW 4"))
            Assert.True(Blue(state, 100, 101) && White(state, 100, 105));
        // ri and i change nothing on the screen (DeviceRGB; flatness is left to the drawing library), also with an absurd flatness
        using var plain = Rendered($"0 0 1 RG 20 w {line}");
        using var intent = Rendered($"0 0 1 RG 20 w /Saturation ri 1000000 i {line}");
        Assert.Equal(plain.Bytes, intent.Bytes);
    }
}
