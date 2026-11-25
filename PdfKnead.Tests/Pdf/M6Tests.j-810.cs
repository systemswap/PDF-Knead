using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>The Nachweise of the M6 draft j-810 (8.10 form XObjects) that had none.</summary>
public sealed partial class M6NormTests
{
    // ---- helpers of draft j-810 (8.10)

    /// <summary>A one-page file of 200 × 200: 3 the page with <paramref name="resources"/>, 4 the content, 5 … the objects given.</summary>
    private static byte[] J810_File(string resources, string content, params string[] more) => DocumentStructureNormTests.FileOf([
        "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << {resources} >> /Contents 4 0 R >>",
        $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", .. more]);

    /// <summary>A form XObject with the entries given and its content.</summary>
    private static string J810_Form(string entries, string content) =>
        $"<< /Type /XObject /Subtype /Form {entries} /Length {content.Length} >>\nstream\n{content}\nendstream";

    private static SKBitmap J810_Render(byte[] file, bool forPrint = false)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        return SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page, forPrint: forPrint), doc, page, 1, new ImageCache());
    }

    /// <summary>The pixel at (<paramref name="x"/>, <paramref name="y"/>) in page units, y counted from the bottom.</summary>
    private static SKColor J810_At(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, bitmap.Height - 1 - y);

    private static bool J810_Red(SKColor c) => c is { Red: > 200, Green: < 60, Blue: < 60 };

    private static bool J810_White(SKColor c) => c is { Red: > 245, Green: > 245, Blue: > 245 };

    /// <summary>The XObjects in the resources of the first page.</summary>
    private static List<PdfStream> J810_XObjects(PdfDocument doc, string subtype) =>
        doc.Pages[0].Resources?.GetDictionary(PdfName.Get("XObject")) is { } x
            ? [.. x.Keys.Select(k => x.Get(k)).OfType<PdfStream>().Where(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == subtype)]
            : [];

    // ---- 8.10.1, 8.10.2

    /// <summary>
    /// 8.10.1 sentences 21–24: /BBox and the content of a form lie in form space; /Matrix maps form space to the user space at the „Do“ and is
    /// joined with the CTM. A form with /Matrix [2 0 0 2 30 30], /BBox [0 0 40 40] and a red square 0 0 60 60, called under
    /// „1 0 0 1 10 10 cm“: red at (60, 60) and (115, 115), white at (125, 125) — the box mapped by the matrix clips — and at (35, 35)
    /// (<c>PageInterpreter.Form</c>).
    /// </summary>
    [Fact]
    public void Form_MatrixJoinedWithTheCtm_BoxInFormSpace()
    {
        using var bitmap = J810_Render(J810_File("/XObject << /Fm0 5 0 R >>", "q 1 0 0 1 10 10 cm /Fm0 Do Q",
            J810_Form("/BBox [0 0 40 40] /Matrix [2 0 0 2 30 30] /Resources << >>", "1 0 0 rg 0 0 60 60 re f")));
        Assert.True(J810_Red(J810_At(bitmap, 60, 60)));
        Assert.True(J810_Red(J810_At(bitmap, 115, 115)));
        Assert.True(J810_White(J810_At(bitmap, 125, 125)));
        Assert.True(J810_White(J810_At(bitmap, 35, 35)));
    }

    /// <summary>
    /// 8.10.1 sentences 26–27: a „Do“ of a form saves the graphics state like q and restores it like Q afterwards. A form sets
    /// „0 0 1 rg 5 w 2 0 0 2 0 0 cm“ and paints nothing; the square „0 0 10 10 re f“ after it on the page is black, 10 × 10 and upright
    /// (<c>PageInterpreter.Form</c>).
    /// </summary>
    [Fact]
    public void Form_StateRestoredAfterDo()
    {
        using var bitmap = J810_Render(J810_File("/XObject << /Fm0 5 0 R >>", "/Fm0 Do 0 0 10 10 re f",
            J810_Form("/BBox [0 0 200 200] /Resources << >>", "0 0 1 rg 5 w 2 0 0 2 0 0 cm")));
        var inside = J810_At(bitmap, 5, 5);
        Assert.True(inside is { Red: < 10, Green: < 10, Blue: < 10 }, inside.ToString());
        Assert.True(J810_White(J810_At(bitmap, 15, 15)));
    }

    /// <summary>
    /// 8.10.2 sentences 4–6: /Type of a form is XObject if given. The forms PDF-Knead writes for its own elements carry /Type /XObject
    /// (<c>ElementAppearance.FormStream</c>); a foreign form with /Type /Pattern is saved with /Type /XObject — the display never reads /Type of a
    /// form, the meaning stays (<c>VersionKeys.Filter</c>; before, kept as it was — docs/FEHLERGEDAECHTNIS.md, No. 1131).
    /// </summary>
    [Fact]
    public void Form_Type_OwnFormsXObject_ForeignCorrected()
    {
        var own = ElementAppearance.For(new ShapeElement { Kind = ShapeKind.Rectangle, Rect = new PdfRectangle(10, 10, 60, 40), FillColor = 0xC00000 }).Form;
        Assert.Equal("XObject", own.Dictionary.GetName(PdfName.Type)?.Value);
        Assert.Equal("Form", own.Dictionary.GetName(PdfName.Subtype)?.Value);

        using var saved = DocumentStructureNormTests.Saved(J810_File("/XObject << /Fm0 5 0 R >>", "/Fm0 Do",
            "<< /Type /Pattern /Subtype /Form /BBox [0 0 50 50] /Resources << >> /Length 21 >>\nstream\n1 0 0 rg 0 0 50 50 re f\nendstream"));
        Assert.Equal("XObject", Assert.Single(J810_XObjects(saved, "Form")).Dictionary.GetName(PdfName.Type)?.Value);
    }

    /// <summary>
    /// 8.10.2 sentences 19–22: /Matrix is six numbers, by default the identity. A form whose /Matrix [1 0 0] cannot be read draws as without
    /// one — its red square 0 0 50 50 at (25, 25), nothing at (75, 75) (<c>Matrix.FromArray</c> in <c>PageInterpreter.Form</c>).
    /// </summary>
    [Fact]
    public void Form_UnreadableMatrix_AsTheIdentity()
    {
        using var bitmap = J810_Render(J810_File("/XObject << /Fm0 5 0 R >>", "/Fm0 Do",
            J810_Form("/BBox [0 0 50 50] /Matrix [1 0 0] /Resources << >>", "1 0 0 rg 0 0 50 50 re f")));
        Assert.True(J810_Red(J810_At(bitmap, 25, 25)));
        Assert.True(J810_White(J810_At(bitmap, 75, 75)));
    }

    /// <summary>
    /// 8.10.2 sentence 32: the resources of an independent form are not raised into those of the stream that calls it. A form with its own
    /// /Font /F9, saved: the page's resources have no /F9, the form keeps it.
    /// </summary>
    [Fact]
    public void Form_OwnResources_NotRaisedIntoThePage()
    {
        using var saved = DocumentStructureNormTests.Saved(J810_File("/XObject << /Fm0 5 0 R >>", "/Fm0 Do",
            J810_Form("/BBox [0 0 200 200] /Resources << /Font << /F9 6 0 R >> >>", "BT /F9 12 Tf 20 100 Td (Formular) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        Assert.Null(saved.Pages[0].Resources?.GetDictionary(PdfName.Get("Font"))?.GetRaw(PdfName.Get("F9")));
        var form = Assert.Single(J810_XObjects(saved, "Form"));
        Assert.NotNull(form.Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Font"))?.GetRaw(PdfName.Get("F9")));
    }

    /// <summary>
    /// 8.10.2 sentences 43–46 (→ M11, metadata streams 14.3.2), today's behaviour: the /Metadata of a form stays when saved; where a bar
    /// redacts part of the form and its content is written anew, the metadata (XMP „Geheim“) leaves and the word is nowhere in the copy
    /// (<c>Redactor.RewriteForm</c>, <c>Redactor.DescribesOldContent</c>).
    /// </summary>
    [Fact]
    public void Form_Metadata_StaysOnSaving_LeavesUnderABar_TodaysBehaviour()
    {
        byte[] File()
        {
            var pdf = TestPdf.Simple(1);
            var xmp = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))), "<x:xmpmeta>Geheim</x:xmpmeta>"u8.ToArray()));
            var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 612, 792)),
                ("Resources", new PdfDictionary()), ("Metadata", TestPdf.Ref(xmp))), "0 0 1 rg 100 600 100 50 re f 100 300 100 50 re f"u8.ToArray()));
            ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Fm0", TestPdf.Ref(form))))));
            pdf.Replace(4, TestPdf.FlateStream("/Fm0 Do"u8.ToArray()));
            return pdf.Build();
        }
        using (var saved = DocumentStructureNormTests.Saved(File()))
            Assert.NotNull(Assert.Single(J810_XObjects(saved, "Form")).Dictionary.Get(PdfName.Metadata) as PdfStream);
        using var doc = PdfDocument.Open(File());
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(90, 590, 220, 660))));
        Assert.All(J810_XObjects(after, "Form"), f => Assert.Null(f.Dictionary.GetRaw(PdfName.Metadata)));
        Assert.DoesNotContain("Geheim", RedactionTests.Everything(after));
    }

    /// <summary>
    /// 8.10.2 sentences 76–78: /Name of a form is deprecated in PDF 2.0 — saved as 2.0 the form goes without it, saved as 1.7 with it
    /// (<c>VersionKeys.Filter</c>).
    /// </summary>
    [Fact]
    public void Form_Name_GoesAs20_StaysAs17()
    {
        var file = J810_File("/XObject << /Fm0 5 0 R >>", "/Fm0 Do", J810_Form("/Name /Fm0 /BBox [0 0 50 50] /Resources << >>", "1 0 0 rg 0 0 50 50 re f"));
        using (var as20 = DocumentStructureNormTests.Saved(file, new PdfSaveOptions { Version = "2.0" }))
            Assert.Null(Assert.Single(J810_XObjects(as20, "Form")).Dictionary.GetRaw(PdfName.Get("Name")));
        using var as17 = DocumentStructureNormTests.Saved(file, new PdfSaveOptions { Version = "1.7" });
        Assert.Equal("Fm0", Assert.Single(J810_XObjects(as17, "Form")).Dictionary.GetName(PdfName.Get("Name"))?.Value);
    }

    /// <summary>A scanned letter of 1 700 × 1 100 pixels: slightly yellowish paper with noise and black lines of „letters“ (as in
    /// <see cref="MrcTests"/>).</summary>
    private static byte[] J810_Letter()
    {
        const int w = 1700, h = 1100;
        var rgb = new byte[w * h * 3];
        var random = new Random(7);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 3;
                var noise = random.Next(-6, 7);
                (int r, int g, int b) = (246 + noise, 242 + noise, 230 + noise);
                var line = (y - 120) % 60;
                if (y is > 120 and < 800 && line is > 10 and < 34 && x is > 120 and < 1500 && x % 22 < 13 && (x / 22 + y / 60) % 7 != 0) (r, g, b) = (25, 25, 30);
                (rgb[o], rgb[o + 1], rgb[o + 2]) = ((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
            }
        return rgb;
    }

    /// <summary>
    /// 8.10.2 sentences 85–87 and 88–90 (→ M10, 12.9 measuring and 12.10.5 point data), today's behaviour, and sentences 4–6 for the MRC form:
    /// a text scan with /Measure or /PtData, written as layers (MRC), becomes a form that carries /Type /XObject and the entry of the image
    /// (<c>PdfWriter.MrcForm</c>); a form with the entry keeps it when saved.
    /// </summary>
    [Theory]
    [InlineData("Measure", "<< /Type /Measure /Subtype /RL /R (1 in = 1 ft) /X [<< /U (ft) /C 0.0138 >>] /D [<< /U (ft) /C 1 >>] /A [<< /U (sq ft) /C 1 >>] >>")]
    [InlineData("PtData", "<< /Type /PtData /Subtype /Cloud /Names [/LAT /LON /ALT] /XPTS [[48.1 11.5 520]] >>")]
    public void Form_MeasureAndPointData_TakenIntoTheMrcFormAndKept_TodaysBehaviour(string key, string value)
    {
        const int w = 1700, h = 1100;
        var rgb = J810_Letter();
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], 255);
        var jpeg = SkiaImageCodec.Instance.EncodeJpeg(w, h, 4, rgba, 90)!;
        var pdf = TestPdf.Simple(1, null);
        var image = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(w)),
            ("Height", PdfInteger.Of(h)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N("DCTDecode"))), jpeg);
        using (var parse = PdfDocument.Open(DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>", value)))
            image.Dictionary.Set(PdfName.Get(key), parse.GetObject(3)!);
        var imageNumber = pdf.Add(image);
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(pdf.Add(TestPdf.FlateStream("q 612 0 0 396 0 300 cm /Im0 Do Q"u8.ToArray()))));
        page.Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(imageNumber))))));
        var file = pdf.Build();

        using var original = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(original, PageItem.From(original), output,
            new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.EBook), ImageCodec = SkiaImageCodec.Instance });
        using var mrc = PdfDocument.Open(output.ToArray());
        var form = (PdfStream)mrc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im0"))!;
        Assert.Equal("Form", form.Dictionary.GetName(PdfName.Subtype)?.Value);
        Assert.Equal("XObject", form.Dictionary.GetName(PdfName.Type)?.Value);
        Assert.NotNull(form.Dictionary.GetDictionary(PdfName.Get(key)));

        using var saved = DocumentStructureNormTests.Saved(J810_File("/XObject << /Fm0 5 0 R >>", "/Fm0 Do",
            J810_Form($"/BBox [0 0 50 50] /Resources << >> /{key} {value}", "1 0 0 rg 0 0 50 50 re f")), new PdfSaveOptions { Version = "2.0" });
        Assert.NotNull(Assert.Single(J810_XObjects(saved, "Form")).Dictionary.GetDictionary(PdfName.Get(key)));
    }

    // ---- 8.10.3 group XObjects

    /// <summary>
    /// 8.10.3 sentences 7–9: /Type of a group attributes dictionary is Group if given. The knockout group PDF-Knead writes when a bar cuts a
    /// half-transparent path filled and stroked with B carries /Type /Group (<c>Redactor.WriteCut</c>).
    /// </summary>
    [Fact]
    public void Group_WrittenByARedactionCut_TypeGroup()
    {
        var pdf = TestPdf.Simple(1);
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("ExtGState", TestPdf.Dict(("GS0", TestPdf.Dict(("ca", new PdfReal(0.5)), ("CA", new PdfReal(0.5))))))));
        pdf.Replace(4, TestPdf.FlateStream("q /GS0 gs 1 0 0 rg 0 0 1 RG 12 w 100 300 200 200 re B Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var saved = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(170, 250, 230, 560))));
        var group = J810_XObjects(saved, "Form").Select(f => f.Dictionary.GetDictionary(PdfName.Get("Group"))).Single(g => g is not null)!;
        Assert.Equal("Group", group.GetName(PdfName.Type)?.Value);
        Assert.Equal("Transparency", group.GetName(PdfName.Get("S"))?.Value);
    }

    /// <summary>
    /// 8.10.3 sentences 10–18: /S names the kind of group; only Transparency is defined. A form with /Group &lt;&lt; /S /Erfunden &gt;&gt; or
    /// with /Group without /S, under /ca 0.5: no transparency group — each square on its own, the overlap darker (<c>BeginTransparencyGroup</c>).
    /// </summary>
    [Theory]
    [InlineData("/Group << /S /Erfunden >>")]
    [InlineData("/Group << /I true >>")]
    public void Group_OtherOrMissingSubtype_NoTransparencyGroup(string group)
    {
        using var bitmap = J810_Render(J810_File("/ExtGState << /H << /ca 0.5 >> >> /XObject << /Fm0 5 0 R >>", "/H gs /Fm0 Do",
            J810_Form($"/BBox [0 0 200 200] /Resources << >> {group}", "1 0 0 rg 0 0 120 120 re f 80 80 120 120 re f")));
        var single = J810_At(bitmap, 50, 50);
        var overlap = J810_At(bitmap, 100, 100);
        Assert.True(Math.Abs(single.Green - 128) <= 8, single.ToString());
        Assert.True(Math.Abs(overlap.Green - 64) <= 10, overlap.ToString());
    }

    // ---- 8.10.4 reference XObjects

    /// <summary>
    /// 8.10.4.2 sentences 1–2: when printing, the imported content or the proxy may be output — PDF-Knead prints the proxy: the reference
    /// XObject of <see cref="GraphicsNormTests.ReferenceXObject_ShowsItsProxy"/> built for printing shows the blue proxy at (100, 100), white at
    /// (20, 20) (<c>PageInterpreter.Build</c> with <c>forPrint</c>).
    /// </summary>
    [Fact]
    public void ReferenceXObject_PrintedAsItsProxy()
    {
        using var bitmap = J810_Render(J810_File("/XObject << /Fm0 5 0 R >>", "/Fm0 Do",
            J810_Form("/BBox [0 0 200 200] /Ref << /F (fremd.pdf) /Page 0 >>", "0 0 1 rg 50 50 100 100 re f")), forPrint: true);
        var proxy = J810_At(bitmap, 100, 100);
        Assert.True(proxy is { Blue: > 200, Red: < 60 }, proxy.ToString());
        Assert.True(J810_White(J810_At(bitmap, 20, 20)));
    }
}
