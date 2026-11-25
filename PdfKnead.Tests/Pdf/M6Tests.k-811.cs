using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>The Nachweise of the M6 draft k-811 (8.11 optional content) that had none.</summary>
public sealed partial class M6NormTests
{
    // ---- helpers of draft k-811 (8.11)

    /// <summary>A one-page file of 200 × 200: the catalogue with <paramref name="catalog"/>, 3 the page with <paramref name="resources"/>
    /// (and <paramref name="page"/>), 4 the content, 5 … the objects given.</summary>
    private static byte[] K811_File(string catalog, string resources, string content, string page = "", params string[] more) => DocumentStructureNormTests.FileOf([
        $"<< /Type /Catalog /Pages 2 0 R {catalog} >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << {resources} >> /Contents 4 0 R {page} >>",
        $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", .. more]);

    /// <summary>A stream object with the entries given and its content.</summary>
    private static string K811_Stream(string entries, string content) => $"<< {entries} /Length {content.Length} >>\nstream\n{content}\nendstream";

    private static SKBitmap K811_Render(byte[] file, bool forPrint = false)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        return SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page, forPrint: forPrint), doc, page, 1, new ImageCache());
    }

    private static SKColor K811_At(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, bitmap.Height - 1 - y);

    private static bool K811_Painted(SKBitmap bitmap, int x, int y) => K811_At(bitmap, x, y) is { Red: < 60 };

    private static bool K811_White(SKBitmap bitmap, int x, int y) => K811_At(bitmap, x, y) is { Red: > 245, Green: > 245, Blue: > 245 };

    /// <summary>A document without pages whose /OCProperties are <paramref name="properties"/>; objects 3 … as given.</summary>
    private static PdfDocument K811_Layers(string properties, params string[] objects) => PdfDocument.Open(DocumentStructureNormTests.FileOf([
        $"<< /Type /Catalog /Pages 2 0 R /OCProperties {properties} >>", "<< /Type /Pages /Kids [] /Count 0 >>", .. objects]));

    private static bool K811_Hidden(PdfDocument doc, int number, OptionalContentUse use = OptionalContentUse.View, string language = "de-DE") =>
        OptionalContent.For(doc, use, language).IsHidden(doc.GetObject(number), null);

    // ---- 8.11.2 optional content groups and membership dictionaries

    /// <summary>
    /// 8.11.2.1 sentences 27–29: nested optional content is visible only if its own and every outer layer are; a hidden outer layer hides all
    /// inner ones. Outer off and inner on, outer on and inner off, and a form without /OC called in a hidden section whose own section is on:
    /// each time only the square after the sections is painted (<c>PageInterpreter.Operator</c>, <c>_hiddenDepth</c>).
    /// </summary>
    [Theory]
    [InlineData("/OC /Aus BDC /OC /An BDC 0 0 1 rg 0 0 50 50 re f EMC EMC")]
    [InlineData("/OC /An BDC /OC /Aus BDC 0 0 1 rg 0 0 50 50 re f EMC EMC")]
    [InlineData("/OC /Aus BDC /Fm0 Do EMC")]
    public void Layers_Nested_VisibleOnlyIfAllAreOn(string sections)
    {
        using var bitmap = K811_Render(K811_File("/OCProperties << /OCGs [5 0 R 6 0 R] /D << /OFF [5 0 R] >> >>",
            "/Properties << /Aus 5 0 R /An 6 0 R >> /XObject << /Fm0 7 0 R >>", sections + " 0 0 1 rg 100 100 50 50 re f", "",
            "<< /Type /OCG /Name (Aus) >>", "<< /Type /OCG /Name (An) >>",
            K811_Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /Properties << /An 6 0 R >> >>", "/OC /An BDC 0 0 1 rg 0 0 50 50 re f EMC")));
        Assert.True(K811_White(bitmap, 25, 25));
        Assert.True(K811_Painted(bitmap, 125, 125));
    }

    /// <summary>
    /// 8.11.2.2 sentences 6–9: /OCGs of a membership dictionary; null and deleted entries are passed over; without layers /P has no effect.
    /// „/OCGs [null 99 0 R] /P /AllOff“ (99 not there) and „/P /AllOff“ without /OCGs: visible; „/OCGs [null 3 0 R] /P /AllOn“ with 3 off:
    /// hidden — the null does not count (<c>OptionalContent.MembershipVisible</c>).
    /// </summary>
    [Fact]
    public void Membership_NullsAndMissingLayersPassedOver()
    {
        using var doc = K811_Layers("<< /OCGs [3 0 R] /D << /OFF [3 0 R] >> >>",
            "<< /Type /OCG /Name (Aus) >>",
            "<< /Type /OCMD /OCGs [null 99 0 R] /P /AllOff >>",
            "<< /Type /OCMD /P /AllOff >>",
            "<< /Type /OCMD /OCGs [null 3 0 R] /P /AllOn >>");
        Assert.False(K811_Hidden(doc, 4));
        Assert.False(K811_Hidden(doc, 5));
        Assert.True(K811_Hidden(doc, 6));
    }

    /// <summary>
    /// 8.11.2.2 sentences 10–12, 15–16 and 21: /P AnyOn — visible if any of the layers is on — is the default. Of two layers one on: AnyOn
    /// visible, without /P visible, with the unknown /P /Foo as AnyOn visible; both off: hidden.
    /// </summary>
    [Fact]
    public void Membership_AnyOn_TheDefault()
    {
        using var doc = K811_Layers("<< /OCGs [3 0 R 4 0 R 5 0 R] /D << /OFF [4 0 R 5 0 R] >> >>",
            "<< /Type /OCG /Name (An) >>", "<< /Type /OCG /Name (Aus) >>", "<< /Type /OCG /Name (Auch aus) >>",
            "<< /Type /OCMD /OCGs [3 0 R 4 0 R] /P /AnyOn >>",
            "<< /Type /OCMD /OCGs [3 0 R 4 0 R] >>",
            "<< /Type /OCMD /OCGs [3 0 R 4 0 R] /P /Foo >>",
            "<< /Type /OCMD /OCGs [4 0 R 5 0 R] >>");
        Assert.False(K811_Hidden(doc, 6));
        Assert.False(K811_Hidden(doc, 7));
        Assert.False(K811_Hidden(doc, 8));
        Assert.True(K811_Hidden(doc, 9));
    }

    /// <summary>8.11.2.2 sentences 13–14: /P AllOn — visible only if every layer is on: one off hidden, both on visible.</summary>
    [Fact]
    public void Membership_AllOn()
    {
        using var doc = K811_Layers("<< /OCGs [3 0 R 4 0 R 5 0 R] /D << /OFF [4 0 R] >> >>",
            "<< /Type /OCG /Name (A) >>", "<< /Type /OCG /Name (B) >>", "<< /Type /OCG /Name (C) >>",
            "<< /Type /OCMD /OCGs [3 0 R 4 0 R] /P /AllOn >>",
            "<< /Type /OCMD /OCGs [3 0 R 5 0 R] /P /AllOn >>");
        Assert.True(K811_Hidden(doc, 6));
        Assert.False(K811_Hidden(doc, 7));
    }

    /// <summary>8.11.2.2 sentences 17–18: /P AnyOff — visible if any layer is off: one off visible, both on hidden.</summary>
    [Fact]
    public void Membership_AnyOff()
    {
        using var doc = K811_Layers("<< /OCGs [3 0 R 4 0 R 5 0 R] /D << /OFF [4 0 R] >> >>",
            "<< /Type /OCG /Name (A) >>", "<< /Type /OCG /Name (B) >>", "<< /Type /OCG /Name (C) >>",
            "<< /Type /OCMD /OCGs [3 0 R 4 0 R] /P /AnyOff >>",
            "<< /Type /OCMD /OCGs [3 0 R 5 0 R] /P /AnyOff >>");
        Assert.False(K811_Hidden(doc, 6));
        Assert.True(K811_Hidden(doc, 7));
    }

    /// <summary>
    /// 8.11.2.2 sentence 28 (writer, today's behaviour): PDF-Knead creates no membership dictionaries; one with /VE, /OCGs and /P stays with all
    /// three entries when saved — also when „Inhalte ausgeblendeter Ebenen“ is cleaned up and a layer of the expression is hidden
    /// (<c>PdfWriter.DropHiddenLayers</c> keeps layers of expressions).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Membership_VisibilityExpressionWithOcgsAndP_KeptOnSaving(bool cleanup)
    {
        var file = K811_File("/OCProperties << /OCGs [5 0 R 6 0 R] /D << /OFF [6 0 R] >> >>", "/Properties << /M 7 0 R >>",
            "/OC /M BDC 0 0 1 rg 0 0 50 50 re f EMC", "",
            "<< /Type /OCG /Name (An) >>", "<< /Type /OCG /Name (Aus) >>",
            "<< /Type /OCMD /VE [/Or 5 0 R 6 0 R] /OCGs [5 0 R] /P /AnyOn >>");
        using var saved = DocumentStructureNormTests.Saved(file, new PdfSaveOptions { Cleanup = cleanup ? CleanupItems.HiddenLayers : CleanupItems.None });
        var membership = saved.Pages[0].Resources!.GetDictionary(PdfName.Get("Properties"))!.GetDictionary(PdfName.Get("M"))!;
        Assert.Equal(3, membership.GetArray(PdfName.Get("VE"))?.Count);
        Assert.Equal(1, membership.GetArray(PdfName.Get("OCGs"))?.Count);
        Assert.Equal("AnyOn", membership.GetName(PdfName.Get("P"))?.Value);
    }

    /// <summary>
    /// 8.11.2.2 sentences 29–32: And and Or need at least one operand. [/And] and [/Or] without operands are invalid — /OCGs and /P apply
    /// (here AllOff over a layer that is on: hidden) (<c>OptionalContent.Evaluate</c>).
    /// </summary>
    [Fact]
    public void VisibilityExpression_AndOrWithoutOperands_FallBackToOcgsAndP()
    {
        using var doc = K811_Layers("<< /OCGs [3 0 R] /D << >> >>",
            "<< /Type /OCG /Name (An) >>",
            "<< /Type /OCMD /VE [/And] /OCGs [3 0 R] /P /AllOff >>",
            "<< /Type /OCMD /VE [/Or] /OCGs [3 0 R] /P /AllOff >>",
            "<< /Type /OCMD /VE [/Or 3 0 R] /OCGs [3 0 R] /P /AllOff >>");
        Assert.True(K811_Hidden(doc, 4));
        Assert.True(K811_Hidden(doc, 5));
        Assert.False(K811_Hidden(doc, 6));   // a valid expression takes precedence
    }

    // ---- 8.11.3 making graphical content optional

    /// <summary>
    /// 8.11.3.1 sentences 6 and 8: changes of the graphics state in hidden content still take effect. „/OC /Aus BDC 1 0 0 rg 2 0 0 2 0 0 cm
    /// EMC 0 0 10 10 re f“ with the layer off: a red square 0…20 (colour and matrix stay) (<c>PageInterpreter.Operator</c>).
    /// </summary>
    [Fact]
    public void Layers_HiddenSection_StateChangesStillApply()
    {
        using var bitmap = K811_Render(K811_File("/OCProperties << /OCGs [5 0 R] /D << /OFF [5 0 R] >> >>", "/Properties << /Aus 5 0 R >>",
            "/OC /Aus BDC 1 0 0 rg 2 0 0 2 0 0 cm EMC 0 0 10 10 re f", "", "<< /Type /OCG /Name (Aus) >>"));
        Assert.True(K811_At(bitmap, 15, 15) is { Red: > 200, Green: < 60, Blue: < 60 });
        Assert.True(K811_White(bitmap, 25, 25));
    }

    /// <summary>
    /// 8.11.3.1 sentence 7: side effects of painting operators take effect — the text position moves on also for hidden text. In
    /// „(AAAA) Tj“ on a layer that is off, followed by „(B) Tj“: the B stands where it stands with the layer on (<c>PageInterpreter.ShowText</c>).
    /// </summary>
    [Fact]
    public void Layers_HiddenText_StillAdvancesTheTextPosition()
    {
        float BAt(string off)
        {
            var file = K811_File($"/OCProperties << /OCGs [5 0 R] /D << /OFF [{off}] >> >>", "/Properties << /L 5 0 R >> /Font << /F1 6 0 R >>",
                "BT /F1 20 Tf 20 100 Td /OC /L BDC (AAAA) Tj EMC (B) Tj ET", "",
                "<< /Type /OCG /Name (Text) >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            using var doc = PdfDocument.Open(file);
            return PageInterpreter.Build(doc, doc.Pages[0]).Text.Single(g => g.Text == "B").X0;
        }
        var shown = BAt("");
        Assert.True(shown > 60, shown.ToString());
        Assert.Equal(shown, BAt("5 0 R"), 0.01f);
    }

    /// <summary>
    /// 8.11.3.1 sentence 10: also state outside the graphics state (BX, EX) takes effect in hidden content. „/OC /Aus BDC BX EMC /Unbekannt 1 2
    /// xyz EX 0 0 50 50 re f“ with the layer off: the page as with the layer on — the square is painted, no problem reported.
    /// </summary>
    [Fact]
    public void Layers_HiddenSection_CompatibilitySectionStillApplies()
    {
        var file = K811_File("/OCProperties << /OCGs [5 0 R] /D << /OFF [5 0 R] >> >>", "/Properties << /Aus 5 0 R >>",
            "/OC /Aus BDC BX EMC /Unbekannt 1 2 xyz EX 0 0 50 50 re f", "", "<< /Type /OCG /Name (Aus) >>");
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        Assert.True(K811_Painted(bitmap, 25, 25));
        Assert.Empty(doc.Problems);
    }

    /// <summary>
    /// 8.11.3.2 sentences 1–2: optional sections may stand in forms, Type 3 glyphs and appearances of annotations; DP may name a layer without
    /// content. Each of the three with a section on a layer that is off draws nothing there; with the layer on it draws; „/OC /L DP“ before a
    /// square changes nothing (<c>PageInterpreter.Form</c>, <c>Type3Glyph</c>, <c>DrawAnnotation</c>).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Layers_SectionsInFormsGlyphsAndAppearances(bool off)
    {
        const string section = "/OC /L BDC 0 0 1 rg 0 0 50 50 re f EMC";
        const string glyph = "1000 0 d0 /OC /L BDC 0 0 1000 1000 re f EMC";
        using var bitmap = K811_Render(K811_File($"/OCProperties << /OCGs [5 0 R] /D << /OFF [{(off ? "5 0 R" : "")}] >> >>",
            "/Properties << /L 5 0 R >> /XObject << /Fm0 6 0 R >> /Font << /T3 7 0 R >>",
            "/Fm0 Do BT /T3 50 Tf 100 100 Td (a) Tj ET /OC /L DP 0 g 0 100 50 50 re f", "/Annots [9 0 R]",
            "<< /Type /OCG /Name (Ebene) >>",
            K811_Stream("/Type /XObject /Subtype /Form /BBox [0 0 50 50] /Resources << /Properties << /L 5 0 R >> >>", section),
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a 8 0 R >> " +
            "/Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1000] /Resources << /Properties << /L 5 0 R >> >> >>",
            K811_Stream("", glyph),
            "<< /Type /Annot /Subtype /Square /Rect [100 0 150 50] /F 4 /AP << /N 10 0 R >> >>",
            K811_Stream("/Type /XObject /Subtype /Form /BBox [0 0 50 50] /Resources << /Properties << /L 5 0 R >> >>", section)));
        Assert.Equal(!off, K811_Painted(bitmap, 25, 25));     // form
        Assert.Equal(!off, K811_Painted(bitmap, 125, 125));   // Type 3 glyph
        Assert.Equal(!off, K811_Painted(bitmap, 125, 25));    // appearance of the annotation
        Assert.True(K811_Painted(bitmap, 25, 125));           // after DP
    }

    /// <summary>
    /// 8.11.3.2 sentences 3–5: the property list of „/OC … BDC“ is a named resource under /Properties; only the tag OC makes a section
    /// optional. „/OC /Fehlt BDC“ without an entry in /Properties: painted; „/Span /L BDC“ with a layer that is off under /L: painted.
    /// </summary>
    [Fact]
    public void Layers_OnlyTagOcWithANamedLayerHides()
    {
        using var bitmap = K811_Render(K811_File("/OCProperties << /OCGs [5 0 R] /D << /OFF [5 0 R] >> >>", "/Properties << /L 5 0 R >>",
            "/OC /Fehlt BDC 0 0 50 50 re f EMC /Span /L BDC 100 100 50 50 re f EMC /OC /L BDC 100 0 50 50 re f EMC", "",
            "<< /Type /OCG /Name (Aus) >>"));
        Assert.True(K811_Painted(bitmap, 25, 25));
        Assert.True(K811_Painted(bitmap, 125, 125));
        Assert.True(K811_White(bitmap, 125, 25));   // the same layer with the tag OC hides
    }

    /// <summary>
    /// 8.11.3.2 sentences 7–9 (writer, today's behaviour): PDF-Knead creates no optional sections; a section nested in a marked-content
    /// section stays nested as it was after another part of the page is redacted (<c>Redactor</c>).
    /// </summary>
    [Fact]
    public void Layers_NestingOfSections_KeptAfterRedaction()
    {
        var pdf = TestPdf.Simple(1);
        var layer = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Ebene"u8.ToArray()))));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(layer)])), ("D", new PdfDictionary())));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Properties", TestPdf.Dict(("L", TestPdf.Ref(layer))))));
        pdf.Replace(4, TestPdf.FlateStream("/P <</MCID 0>> BDC /OC /L BDC 0 0 1 rg 100 600 50 50 re f EMC EMC 1 0 0 rg 300 300 50 50 re f"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(290, 290, 360, 360))));
        var content = I89_Content(after);
        Assert.Matches(new Regex(@"/P\s*<<\s*/MCID\s+0\s*>>\s*BDC\s*/OC\s*/L\s*BDC.*?EMC\s*EMC", RegexOptions.Singleline), content);
    }

    /// <summary>
    /// 8.11.3.2 sentences 23–26: sections with the same membership rule are equally visible; the same layers with /P AnyOn (none) and
    /// AllOff are exactly opposite. Memberships „/OCGs [A]“ and „/OCGs [A] /P /AllOff“: with A on only the first visible, with A off only
    /// the second; a second membership like the first behaves like it (<c>OptionalContent.MembershipVisible</c>).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Membership_SameRuleSameVisibility_AllOffOpposite(bool on)
    {
        using var doc = K811_Layers($"<< /OCGs [3 0 R] /D << /OFF [{(on ? "" : "3 0 R")}] >> >>",
            "<< /Type /OCG /Name (A) >>",
            "<< /Type /OCMD /OCGs [3 0 R] >>",
            "<< /Type /OCMD /OCGs [3 0 R] /P /AllOff >>",
            "<< /Type /OCMD /OCGs [3 0 R] >>");
        Assert.Equal(!on, K811_Hidden(doc, 4));
        Assert.Equal(on, K811_Hidden(doc, 5));
        Assert.Equal(K811_Hidden(doc, 4), K811_Hidden(doc, 6));
    }

    /// <summary>
    /// 8.11.3.3 sentences 1–2: the visibility of a form or an image also depends on the visibility where it is called. A form and an image
    /// without /OC, called in a section on a layer that is off: nothing drawn; with the layer on both are drawn (<c>PageInterpreter.XObject</c>).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Layers_FormAndImageInAHiddenSection_NotDrawn(bool off)
    {
        using var bitmap = K811_Render(K811_File($"/OCProperties << /OCGs [5 0 R] /D << /OFF [{(off ? "5 0 R" : "")}] >> >>",
            "/Properties << /L 5 0 R >> /XObject << /Fm0 6 0 R /Im0 7 0 R >>",
            "/OC /L BDC q 100 0 0 100 0 0 cm /Im0 Do Q /Fm0 Do EMC", "",
            "<< /Type /OCG /Name (Ebene) >>",
            K811_Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << >>", "0 0 1 rg 100 100 100 100 re f"),
            K811_Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", "\x00")));
        Assert.Equal(!off, K811_Painted(bitmap, 50, 50));
        Assert.Equal(!off, K811_Painted(bitmap, 150, 150));
    }

    /// <summary>
    /// 8.11.3.3 sentences 3–4: an annotation with /OC is visible on the screen or on paper only if its flags allow it and its layer is visible.
    /// Left: a layer that is off — drawn neither on the screen nor printed. Bottom right: Print flag, a layer that is on only when printed
    /// (/AS with /Print) — printed, not on the screen. Top right: no Print flag, a layer that is on — on the screen, not printed
    /// (<c>PageInterpreter.DrawAnnotation</c>).
    /// </summary>
    [Fact]
    public void Layers_AnnotationsWithOc_FlagsAndLayerTogether()
    {
        const string appearance = "/Type /XObject /Subtype /Form /BBox [0 0 50 50]";
        var file = K811_File("/OCProperties << /OCGs [8 0 R 9 0 R 10 0 R] /D << /OFF [8 0 R 9 0 R] /AS [<< /Event /Print /OCGs [9 0 R] /Category [/Print] >>] >> >>",
            "", "", "/Annots [5 0 R 6 0 R 7 0 R]",
            "<< /Type /Annot /Subtype /Square /Rect [0 0 50 50] /F 4 /OC 8 0 R /AP << /N 11 0 R >> >>",
            "<< /Type /Annot /Subtype /Square /Rect [100 0 150 50] /F 4 /OC 9 0 R /AP << /N 11 0 R >> >>",
            "<< /Type /Annot /Subtype /Square /Rect [100 100 150 150] /F 0 /OC 10 0 R /AP << /N 11 0 R >> >>",
            "<< /Type /OCG /Name (Aus) >>",
            "<< /Type /OCG /Name (Nur Papier) /Usage << /Print << /PrintState /ON >> >> >>",
            "<< /Type /OCG /Name (An) >>",
            K811_Stream(appearance, "0 0 1 rg 0 0 50 50 re f"));
        using (var screen = K811_Render(file))
        {
            Assert.False(K811_Painted(screen, 25, 25));
            Assert.False(K811_Painted(screen, 125, 25));
            Assert.True(K811_Painted(screen, 125, 125));
        }
        using var printed = K811_Render(file, forPrint: true);
        Assert.False(K811_Painted(printed, 25, 25));
        Assert.True(K811_Painted(printed, 125, 25));
        Assert.False(K811_Painted(printed, 125, 125));
    }

    // ---- 8.11.4 configuring optional content

    /// <summary>
    /// 8.11.4.2 sentence 2: without /OCProperties every structure of optional content is passed over — a section on a layer is painted when
    /// the catalogue has no /OCProperties, and when its /OCProperties have no /D (<c>OptionalContent.For</c>).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("/OCProperties << /OCGs [5 0 R] >>")]
    public void Layers_WithoutPropertiesOrDefaultConfiguration_AllVisible(string catalog)
    {
        using var bitmap = K811_Render(K811_File(catalog, "/Properties << /L 5 0 R >>", "/OC /L BDC 0 0 50 50 re f EMC", "",
            "<< /Type /OCG /Name (Ebene) >>"));
        Assert.True(K811_Painted(bitmap, 25, 25));
    }

    /// <summary>
    /// 8.11.4.3 sentences 10–23 and 25–28, 8.11.4.5 sentences 8–10: /BaseState sets every layer first, then the /ON list (or /OFF) overrides
    /// it. /BaseState /OFF with /ON [A]: A visible, B hidden; /BaseState /Unchanged on opening: as ON (<c>OptionalContent.GroupOn</c>).
    /// </summary>
    [Fact]
    public void Layers_BaseState_ThenTheOnList()
    {
        using (var off = K811_Layers("<< /OCGs [3 0 R 4 0 R] /D << /BaseState /OFF /ON [3 0 R] >> >>", "<< /Type /OCG /Name (A) >>", "<< /Type /OCG /Name (B) >>"))
        {
            Assert.False(K811_Hidden(off, 3));
            Assert.True(K811_Hidden(off, 4));
        }
        using var unchanged = K811_Layers("<< /OCGs [3 0 R 4 0 R] /D << /BaseState /Unchanged /OFF [4 0 R] >> >>", "<< /Type /OCG /Name (A) >>", "<< /Type /OCG /Name (B) >>");
        Assert.False(K811_Hidden(unchanged, 3));
        Assert.True(K811_Hidden(unchanged, 4));
    }

    /// <summary>
    /// 8.11.4.3 sentence 24: in the default configuration /BaseState, if given, is ON. The configurations PDF-Knead writes have none — the
    /// print PDF (<c>OptionalContent.ForPrintConfig</c>) and a merged file whose layers come only from an inserted file with /BaseState /OFF
    /// (<c>PdfWriter.OptionalContentMerged</c>); a read file with /D /BaseState /OFF is applied (today's behaviour: the layers not on are hidden).
    /// </summary>
    [Fact]
    public void Layers_DefaultConfigurationWritten_WithoutBaseState()
    {
        byte[] OffFile()
        {
            var pdf = TestPdf.Simple(1);
            var hidden = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Versteckt"u8.ToArray()))));
            var shown = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Sichtbar"u8.ToArray()))));
            ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(hidden), TestPdf.Ref(shown)])),
                ("D", TestPdf.Dict(("BaseState", TestPdf.N("OFF")), ("ON", new PdfArray([TestPdf.Ref(shown)]))))));
            return pdf.Build();
        }
        using var source = PdfDocument.Open(OffFile());
        var print = OptionalContent.ForPrintConfig(source)!;
        Assert.Null(print.GetDictionary(PdfName.Get("D"))!.GetRaw(PdfName.Get("BaseState")));

        using var target = BlankPage.CreateDocument(595, 842, 1);
        using var output = new MemoryStream();
        PdfWriter.Write(target, [.. PageItem.From(target), .. PageItem.From(source)], output);
        using var merged = PdfDocument.Open(output.ToArray());
        Assert.Null(merged.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!.GetDictionary(PdfName.Get("D"))!.GetRaw(PdfName.Get("BaseState")));

        var layers = source.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!.GetArray(PdfName.Get("OCGs"))!;
        var reader = OptionalContent.For(source);
        Assert.True(reader.IsHidden(layers.Get(0), null));
        Assert.False(reader.IsHidden(layers.Get(1), null));
    }

    /// <summary>
    /// 8.11.4.4 sentences 5–15 (today's behaviour): /CreatorInfo describes the layer for the program that made it and gives no recommendation.
    /// A layer with only /CreatorInfo in its /Usage under /AS naming the categories whose missing entries leave the state alone (CreatorInfo,
    /// View, Print, Export, User, PageElement) keeps its state (on: visible, in /OFF: hidden); saved, its /CreatorInfo stays
    /// (<c>OptionalContent.ApplyUsages</c>). Language and Zoom are left out: by 8.11.4.4 sentences 71–76 they recommend a state also for a
    /// layer without such an entry (Language: OFF).
    /// </summary>
    [Fact]
    public void Usage_CreatorInfo_NoEffect_KeptOnSaving()
    {
        const string usage = "/Usage << /CreatorInfo << /Creator (Zeichenprogramm) /Subtype /Technical >> >>";
        const string all = "/Category [/CreatorInfo /Export /Print /View /User /PageElement]";
        using (var doc = K811_Layers($"<< /OCGs [3 0 R 4 0 R] /D << /OFF [4 0 R] /AS [<< /Event /View /OCGs [3 0 R 4 0 R] {all} >>] >> >>",
                   $"<< /Type /OCG /Name (An) {usage} >>", $"<< /Type /OCG /Name (Aus) {usage} >>"))
        {
            Assert.False(K811_Hidden(doc, 3));
            Assert.True(K811_Hidden(doc, 4));
        }
        using var saved = DocumentStructureNormTests.Saved(K811_File("/OCProperties << /OCGs [5 0 R] /D << >> >>", "/Properties << /L 5 0 R >>",
            "/OC /L BDC 0 0 50 50 re f EMC", "", $"<< /Type /OCG /Name (An) {usage} >>"));
        var layer = saved.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!.GetArray(PdfName.Get("OCGs"))!.Get(0)!.AsDictionary()!;
        Assert.Equal("Zeichenprogramm", layer.GetDictionary(PdfName.Get("Usage"))?.GetDictionary(PdfName.Get("CreatorInfo"))?.GetString(PdfName.Get("Creator"))?.ToText());
    }

    /// <summary>
    /// 8.11.4.4 sentences 28–31: /Export with /ExportState gives the state for a format without optional content. A layer off in /D but on for
    /// exporting (/AS with /Export, /ExportState /ON): its square is in the PNG written by <c>RasterExport.WriteImages</c>, not on the screen.
    /// </summary>
    [Fact]
    public void Usage_ExportState_AppliesToPageImages()
    {
        var file = K811_File("/OCProperties << /OCGs [5 0 R] /D << /OFF [5 0 R] /AS [<< /Event /Export /OCGs [5 0 R] /Category [/Export] >>] >> >>",
            "/Properties << /L 5 0 R >>", "/OC /L BDC 0 0 1 rg 0 0 200 200 re f EMC", "",
            "<< /Type /OCG /Name (Nur Export) /Usage << /Export << /ExportState /ON >> >> >>");
        using (var screen = K811_Render(file)) Assert.True(K811_White(screen, 100, 100));
        using var doc = PdfDocument.Open(file);
        var folder = Directory.CreateTempSubdirectory("pdf-knead-m6-export-").FullName;
        try
        {
            var written = Assert.Single(RasterExport.WriteImages(PageItem.From(doc), folder, "Seite", new PageImageSettings(PageImageFormat.Png, Dpi: 72)));
            using var image = SKBitmap.Decode(written);
            var c = image.GetPixel(image.Width / 2, image.Height / 2);
            Assert.True(c is { Blue: > 200, Red: < 60 }, c.ToString());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// 8.11.4.4 sentences 47–51: /User names the users a layer is meant for (/Type Ind, Ttl or Org, /Name); PDF-Knead does not know the reader,
    /// so it gives no recommendation (S-A A84): under /AS with /Category [/User] a layer stays visible when on and hidden when in /OFF.
    /// </summary>
    [Fact]
    public void Usage_User_NoRecommendation()
    {
        const string usage = "/Usage << /User << /Type /Ind /Name (Erika Beispiel) >> >>";
        using var doc = K811_Layers("<< /OCGs [3 0 R 4 0 R] /D << /OFF [4 0 R] /AS [<< /Event /View /OCGs [3 0 R 4 0 R] /Category [/User] >>] >> >>",
            $"<< /Type /OCG /Name (Für eine Person) {usage} >>", $"<< /Type /OCG /Name (Für eine Person, aus) {usage} >>");
        Assert.False(K811_Hidden(doc, 3));
        Assert.True(K811_Hidden(doc, 4));
    }

    /// <summary>
    /// 8.11.4.4 sentences 52–53: /PageElement says the layer holds a page element (HF, FG, BG, L) and changes nothing: under /AS with
    /// /Category [/PageElement] the layer keeps its state, its square is drawn (<c>OptionalContent.ApplyUsages</c>).
    /// </summary>
    [Fact]
    public void Usage_PageElement_NoEffect()
    {
        const string catalog = "/OCProperties << /OCGs [5 0 R] /D << /AS [<< /Event /View /OCGs [5 0 R] /Category [/PageElement] >>] >> >>";
        const string layer = "<< /Type /OCG /Name (Kopfzeile) /Usage << /PageElement << /Subtype /HF >> >> >>";
        using (var doc = K811_Layers("<< /OCGs [3 0 R] /D << /AS [<< /Event /View /OCGs [3 0 R] /Category [/PageElement] >>] >> >>", layer))
            Assert.False(K811_Hidden(doc, 3));
        using var bitmap = K811_Render(K811_File(catalog, "/Properties << /L 5 0 R >>", "/OC /L BDC 0 0 50 50 re f EMC", "", layer));
        Assert.True(K811_Painted(bitmap, 25, 25));
    }

    /// <summary>
    /// 8.11.4.4 sentences 54–58: without usage application dictionaries (/AS) nothing is set automatically — a layer with /Usage /View
    /// /ViewState /OFF but no /AS stays visible (<c>OptionalContent</c> constructor).
    /// </summary>
    [Fact]
    public void Usage_WithoutApplicationDictionaries_NoAutomaticState()
    {
        using var doc = K811_Layers("<< /OCGs [3 0 R] /D << >> >>", "<< /Type /OCG /Name (A) /Usage << /View << /ViewState /OFF >> >> >>");
        Assert.False(K811_Hidden(doc, 3));
    }

    /// <summary>
    /// 8.11.4.4 sentences 77–78: a layer in several dictionaries of the same event is ON only if every category of all of them is ON. In one
    /// /View dictionary with /Category [/View] (ViewState ON) and one with /Category [/Language] (en-US, the program reads de-DE): OFF.
    /// </summary>
    [Fact]
    public void Usage_SeveralDictionariesOfOneEvent_AllMustBeOn()
    {
        using var doc = K811_Layers("<< /OCGs [3 0 R] /D << /AS [<< /Event /View /OCGs [3 0 R] /Category [/View] >> << /Event /View /OCGs [3 0 R] /Category [/Language] >>] >> >>",
            "<< /Type /OCG /Name (Englisch) /Usage << /View << /ViewState /ON >> /Language << /Lang (en-US) >> >> >>");
        Assert.True(K811_Hidden(doc, 3));
    }

    /// <summary>
    /// 8.11.4.4 sentences 111–112: /Event (View, Print or Export) says when an application dictionary applies. One without /Event and one with
    /// /Event /Design, both /Category [/View] over a layer with /ViewState /OFF: without effect on the screen, in print and in export.
    /// </summary>
    [Fact]
    public void Usage_EventMissingOrUnknown_NoEffect()
    {
        using var doc = K811_Layers("<< /OCGs [3 0 R] /D << /AS [<< /OCGs [3 0 R] /Category [/View] >> << /Event /Design /OCGs [3 0 R] /Category [/View] >>] >> >>",
            "<< /Type /OCG /Name (A) /Usage << /View << /ViewState /OFF >> >> >>");
        Assert.False(K811_Hidden(doc, 3, OptionalContentUse.View));
        Assert.False(K811_Hidden(doc, 3, OptionalContentUse.Print));
        Assert.False(K811_Hidden(doc, 3, OptionalContentUse.Export));
    }

    /// <summary>
    /// 8.11.4.4 sentences 113–114: /OCGs of an application dictionary lists the layers it sets, by default none. „/Event /View /Category
    /// [/View]“ without /OCGs and with /OCGs [] over a layer with /ViewState /OFF: the layer stays visible.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("/OCGs []")]
    public void Usage_ApplicationWithoutLayers_SetsNone(string ocgs)
    {
        using var doc = K811_Layers($"<< /OCGs [3 0 R] /D << /AS [<< /Event /View {ocgs} /Category [/View] >>] >> >>",
            "<< /Type /OCG /Name (A) /Usage << /View << /ViewState /OFF >> >> >>");
        Assert.False(K811_Hidden(doc, 3));
    }

    /// <summary>
    /// 8.11.4.4 sentences 115–116: /Category names the entries of the usage that count. Without /Category a dictionary over a layer with
    /// /ViewState /OFF has no effect (visible); /Category [/View /Print] on the screen with ViewState ON and PrintState OFF gives OFF.
    /// </summary>
    [Fact]
    public void Usage_Category_MissingOrSeveral()
    {
        using (var none = K811_Layers("<< /OCGs [3 0 R] /D << /AS [<< /Event /View /OCGs [3 0 R] >>] >> >>",
                   "<< /Type /OCG /Name (A) /Usage << /View << /ViewState /OFF >> >> >>"))
            Assert.False(K811_Hidden(none, 3));
        using var both = K811_Layers("<< /OCGs [3 0 R] /D << /AS [<< /Event /View /OCGs [3 0 R] /Category [/View /Print] >>] >> >>",
            "<< /Type /OCG /Name (A) /Usage << /View << /ViewState /ON >> /Print << /PrintState /OFF >> >> >>");
        Assert.True(K811_Hidden(both, 3));
    }

    /// <summary>
    /// 8.11.4.5 sentences 18–20 (→ M10, Set-OCG-State actions 12.6.4.13), today's behaviour: PDF-Knead changes no layer state by hand or by
    /// action — a link with /A &lt;&lt; /S /SetOCGState /State [/OFF A] &gt;&gt; is shown as a foreign action that is never carried out
    /// (<c>PageLinks</c>), and layer A stays visible.
    /// </summary>
    [Fact]
    public void Layers_SetOcgStateLink_NotCarriedOut_TodaysBehaviour()
    {
        var file = K811_File("/OCProperties << /OCGs [5 0 R] /D << >> >>", "/Properties << /A 5 0 R >>", "/OC /A BDC 0 0 50 50 re f EMC", "/Annots [6 0 R]",
            "<< /Type /OCG /Name (A) >>",
            "<< /Type /Annot /Subtype /Link /Rect [100 100 150 150] /Border [0 0 0] /A << /S /SetOCGState /State [/OFF 5 0 R] >> >>");
        using var doc = PdfDocument.Open(file);
        var link = Assert.Single(PageLinks.Of(doc, 0));
        Assert.Equal("SetOCGState", Assert.IsType<OtherLink>(link.Target).Kind);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        Assert.True(K811_Painted(bitmap, 25, 25));
    }

    /// <summary>
    /// 8.11.4.5 sentences 21 and 23: printing applies the /Print application dictionaries over the current states, exporting the /Export ones.
    /// PDF-Knead changes no layer state by hand (no layer panel, no Set-OCG-State action), so the current state is the one shown: /D with the
    /// /View application dictionaries of /AS. Layer A, on in /D and turned off by a /View dictionary without a /Print or /Export dictionary,
    /// stays off on the screen, in print, in the print PDF (<c>OptionalContent.ForPrintConfig</c>: in its /OFF) and in a page image; layer B,
    /// turned off by /View as well but on by a /Print dictionary (/PrintState /ON), is printed (<c>OptionalContent</c> constructor).
    /// </summary>
    [Fact]
    public void Layers_PrintAndExportStartFromTheStateShown()
    {
        var file = K811_File("/OCProperties << /OCGs [5 0 R 6 0 R] /D << /AS [<< /Event /View /OCGs [5 0 R 6 0 R] /Category [/View] >> " +
                             "<< /Event /Print /OCGs [6 0 R] /Category [/Print] >>] >> >>",
            "/Properties << /A 5 0 R /B 6 0 R >>", "/OC /A BDC 0 0 50 50 re f EMC /OC /B BDC 100 100 50 50 re f EMC", "",
            "<< /Type /OCG /Name (Nur Bildschirm aus) /Usage << /View << /ViewState /OFF >> >> >>",
            "<< /Type /OCG /Name (Nur Papier) /Usage << /View << /ViewState /OFF >> /Print << /PrintState /ON >> >> >>");
        using (var screen = K811_Render(file))
        {
            Assert.False(K811_Painted(screen, 25, 25));
            Assert.False(K811_Painted(screen, 125, 125));
        }
        using (var printed = K811_Render(file, forPrint: true))
        {
            Assert.False(K811_Painted(printed, 25, 25));
            Assert.True(K811_Painted(printed, 125, 125));
        }
        using var doc = PdfDocument.Open(file);
        Assert.True(K811_Hidden(doc, 5, OptionalContentUse.Export));
        var off = OptionalContent.ForPrintConfig(doc)!.GetDictionary(PdfName.Get("D"))!.GetArray(PdfName.Get("OFF"))!;
        Assert.Equal(5, Assert.IsType<PdfReference>(Assert.Single(off)).Id.Number);
        var folder = Directory.CreateTempSubdirectory("pdf-knead-m6-export-").FullName;
        try
        {
            var written = Assert.Single(RasterExport.WriteImages(PageItem.From(doc), folder, "Seite", new PageImageSettings(PageImageFormat.Png, Dpi: 72)));
            using var image = SKBitmap.Decode(written);
            Assert.True(image.GetPixel(25, image.Height - 1 - 25) is { Red: > 245 });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- layers outside /OCGs (8.11.2.1 sentences 5–8, 8.11.3.2 sentence 6)

    /// <summary>
    /// 8.11.3.2 sentence 6, 8.11.2.1 sentences 5–8: content is optional only through a layer listed in /OCProperties /OCGs that is one (/Type
    /// /OCG), or through a membership dictionary; the layers a membership dictionary names count only as such layers too. Under /BaseState
    /// /OFF: the listed layer 3 is hidden; layer 4 (/Type /OCG, not listed) and dictionary 5 (listed, without /Type) are visible; a membership
    /// with /P /AllOff over 4 only, and one with the expression [/Not 4], have no effect (visible); one with /AnyOn over 3 and 4 counts 3 only
    /// (hidden) (<c>OptionalContent.GroupState</c>).
    /// </summary>
    [Fact]
    public void Layers_OnlyListedGroupsWithTypeCount()
    {
        using var doc = K811_Layers("<< /OCGs [3 0 R 5 0 R] /D << /BaseState /OFF >> >>",
            "<< /Type /OCG /Name (Gelistet) >>", "<< /Type /OCG /Name (Nicht gelistet) >>", "<< /Name (Ohne Typ) >>",
            "<< /Type /OCMD /OCGs [4 0 R] /P /AllOff >>", "<< /Type /OCMD /VE [/Not 4 0 R] >>", "<< /Type /OCMD /OCGs [3 0 R 4 0 R] /P /AnyOn >>");
        Assert.True(K811_Hidden(doc, 3));
        Assert.False(K811_Hidden(doc, 4));
        Assert.False(K811_Hidden(doc, 5));
        Assert.False(K811_Hidden(doc, 6));
        Assert.False(K811_Hidden(doc, 7));
        Assert.True(K811_Hidden(doc, 8));
        Assert.False(K811_Hidden(doc, 4, OptionalContentUse.Print));
    }

    /// <summary>A page whose text and square lie on layer 6 (/Type /OCG, not in /OCGs) under /D /BaseState /OFF, with listed layer 5.</summary>
    private static byte[] K811_UnlistedLayerFile(string layer = "<< /Type /OCG /Name (Ausserhalb) >>", string config = "/BaseState /OFF", string listed = "5 0 R") =>
        K811_File($"/OCProperties << /OCGs [{listed}] /D << {config} >> >>",
            "/Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> /Properties << /G 5 0 R /X 6 0 R >>",
            "/OC /X BDC 0 0 50 50 re f BT /F1 12 Tf 60 20 Td (Kontostand 4711) Tj ET EMC", "",
            "<< /Type /OCG /Name (Gelistet) >>", layer);

    /// <summary>
    /// 8.11.3.2 sentence 6: the content of a layer outside /OCGs is not optional, so it is visible everywhere alike — on the screen, in print,
    /// in the text read out, and „Dokument bereinigen“ names only the listed layer under „Inhalte ausgeblendeter Ebenen“; removing hidden
    /// content (also as a copy without layers, <c>screenLayers</c>) leaves it. Before, /BaseState /OFF hid it, and both removed it.
    /// </summary>
    [Fact]
    public void Layers_UnlistedLayer_ShownEverywhereAlike()
    {
        var file = K811_UnlistedLayerFile();
        using (var screen = K811_Render(file)) Assert.True(K811_Painted(screen, 25, 25));
        using (var printed = K811_Render(file, forPrint: true)) Assert.True(K811_Painted(printed, 25, 25));
        using var doc = PdfDocument.Open(file);
        Assert.Contains("Kontostand 4711", PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]), 0).Text);
        Assert.Equal("Gelistet", Assert.Single(DocumentCleanup.Inspect(doc, PageItem.From(doc)), f => f.Item == CleanupItems.HiddenLayers).Detail);
        Assert.Null(Redactor.RemoveHidden(doc, doc.Pages[0], layers: true, invisibleText: false));
        Assert.Null(Redactor.RemoveHidden(doc, doc.Pages[0], layers: true, invisibleText: false, screenLayers: true));
    }

    /// <summary>
    /// 8.11.3.2 sentence 6, 8.11.2.1 sentences 5–8, 8.11.4.3: saved — also with „Inhalte ausgeblendeter Ebenen“ removed — the content of a
    /// layer outside /OCGs stays and does not lie hidden in the file either: the layer is entered in /OCGs, and on in the default configuration
    /// (in /ON under /BaseState /OFF, out of /OFF and of the application dictionaries of /AS), so that every reader shows it as PDF-Knead does —
    /// as 2.0, where only listed layers count, and as 1.7, whose readers may count unlisted ones. A dictionary without /Type gets /Type /OCG,
    /// one without /Name a name (<c>PdfWriter</c>, unlisted layers).
    /// </summary>
    [Theory]
    [InlineData("2.0", "<< /Type /OCG /Name (Ausserhalb) >>", "/BaseState /OFF")]
    [InlineData("1.7", "<< /Type /OCG /Name (Ausserhalb) >>", "/BaseState /OFF")]
    [InlineData("2.0", "<< /Type /OCG /Name (Ausserhalb) >>", "/OFF [6 0 R] /AS [<< /Event /View /OCGs [6 0 R] /Category [/View] >>]")]
    [InlineData("2.0", "<< /Type /OCG >>", "/BaseState /OFF")]
    [InlineData("2.0", "<< /Name (Ohne Typ) >>", "/BaseState /OFF", "5 0 R 6 0 R")]
    [InlineData("1.7", "<< /Name (Ohne Typ) >>", "/OFF [6 0 R]", "5 0 R 6 0 R")]
    public void Layers_UnlistedLayer_SavedListedAndOn(string version, string layer, string config, string listed = "5 0 R")
    {
        var file = K811_UnlistedLayerFile(layer.Replace(">>", "/Usage << /View << /ViewState /OFF >> >> >>"), config, listed);
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, output, new PdfSaveOptions { Version = version, Cleanup = CleanupItems.HiddenLayers });
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.Contains("Kontostand 4711", RedactionTests.Everything(saved));
        Assert.Contains("Kontostand 4711", PageText.Create(PageInterpreter.BuildText(saved, saved.Pages[0]), 0).Text);
        var properties = saved.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!;
        var x = saved.Pages[0].Resources!.GetDictionary(PdfName.Get("Properties"))!.GetDictionary(PdfName.Get("X"))!;
        var groups = properties.GetArray(PdfName.Get("OCGs"))!;
        Assert.Contains(Enumerable.Range(0, groups.Count), i => ReferenceEquals(groups.Get(i), x));
        Assert.Equal("OCG", x.GetName(PdfName.Type)?.Value);
        Assert.False(string.IsNullOrEmpty(x.GetString(PdfName.Get("Name"))?.ToText()));
        var standard = properties.GetDictionary(PdfName.Get("D"))!;
        bool In(PdfArray? list) => list is not null && Enumerable.Range(0, list.Count).Any(i => ReferenceEquals(list.Get(i), x));
        Assert.False(In(standard.GetArray(PdfName.Get("OFF"))));
        if (standard.GetName(PdfName.Get("BaseState"))?.Value == "OFF") Assert.True(In(standard.GetArray(PdfName.Get("ON"))));
        if (standard.GetArray(PdfName.Get("AS")) is { } usages)
            for (var i = 0; i < usages.Count; i++) Assert.False(In(usages.Get(i)?.AsDictionary()?.GetArray(PdfName.Get("OCGs"))));
        Assert.False(OptionalContent.For(saved).IsHidden(x, null));
        Assert.False(OptionalContent.For(saved, print: true).IsHidden(x, null));
        using var screen = K811_Render(output.ToArray());
        Assert.True(K811_Painted(screen, 25, 25));
    }

    /// <summary>
    /// 8.11.3.2 sentence 6, 8.11.4.3: merged into another file, a layer outside the /OCGs of the inserted file is entered in the merged /OCGs
    /// as well and is not put in /OFF; its square stays visible (<c>PdfWriter.EnterUnlistedLayers</c> for inserted files).
    /// </summary>
    [Fact]
    public void Layers_UnlistedLayer_MergedListed()
    {
        using var source = PdfDocument.Open(K811_UnlistedLayerFile());
        using var target = BlankPage.CreateDocument(200, 200, 1);
        using var output = new MemoryStream();
        PdfWriter.Write(target, [.. PageItem.From(target), .. PageItem.From(source)], output);
        using var merged = PdfDocument.Open(output.ToArray());
        var x = merged.Pages[1].Resources!.GetDictionary(PdfName.Get("Properties"))!.GetDictionary(PdfName.Get("X"))!;
        var groups = merged.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!.GetArray(PdfName.Get("OCGs"))!;
        Assert.Contains(Enumerable.Range(0, groups.Count), i => ReferenceEquals(groups.Get(i), x));
        Assert.False(OptionalContent.For(merged).IsHidden(x, null));
        var page = merged.Pages[1];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(merged, page), merged, page, 1, new ImageCache());
        Assert.True(K811_Painted(bitmap, 25, 25));
    }

    /// <summary>
    /// 8.11.3.2 sentence 6: what the content of a layer outside /OCGs looks like does not change on saving, also where it is named in a
    /// membership dictionary — such a layer is not entered (in /OCGs and on it would count for the membership and change what it shows):
    /// the image with /OC of the membership [/Not X] stays visible; the dictionary is written as it was.
    /// </summary>
    [Fact]
    public void Layers_UnlistedLayerInMembership_NotEntered()
    {
        var file = K811_File("/OCProperties << /OCGs [5 0 R] /D << /BaseState /OFF >> >>", "/Properties << /M 7 0 R >>",
            "/OC /M BDC 0 0 50 50 re f EMC", "", "<< /Type /OCG /Name (Gelistet) >>", "<< /Type /OCG /Name (Ausserhalb) >>",
            "<< /Type /OCMD /OCGs [6 0 R] /P /AllOff >>");
        using var saved = DocumentStructureNormTests.Saved(file, new PdfSaveOptions { Version = "2.0" });
        var groups = saved.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!.GetArray(PdfName.Get("OCGs"))!;
        Assert.Equal(1, groups.Count);
        var page = saved.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(saved, page), saved, page, 1, new ImageCache());
        Assert.True(K811_Painted(bitmap, 25, 25));
    }

    // ---- /Language after the language of the program (8.11.4.4 sentences 16–27, 68–76)

    /// <summary>
    /// 8.11.4.4 sentences 16–27: /Language compares with the language of the program (BCP 47 with region). PDF-Knead's language has no region:
    /// where the system's language is the same, its region is taken (de with de-AT → de-AT), otherwise the usual region of the language
    /// (en with a German system → en-US, de with an English system → de-DE) (<c>OptionalContent.ProgramLanguage</c>).
    /// </summary>
    [Theory]
    [InlineData("de", "de-AT", "de-AT")]
    [InlineData("de", "en-US", "de-DE")]
    [InlineData("en", "de-DE", "en-US")]
    [InlineData("en", "en-GB", "en-GB")]
    [InlineData("fr", "fr", "fr-FR")]
    public void Usage_Language_ProgramLanguageWithRegion(string program, string system, string expected) =>
        Assert.Equal(expected, OptionalContent.ProgramLanguage(program, CultureInfo.GetCultureInfo(system)));
}

/// <summary>
/// 8.11.4.4 sentences 16–27 and 68–76: without a language given, /Language is evaluated after the language chosen in PDF-Knead, not after
/// the system's (they switch the language of the program, hence in the collection of the UI tests).
/// </summary>
[Collection(nameof(Ui.UiCollection))]
public sealed class M6LayerLanguageTests : IDisposable
{
    public void Dispose() => Tr.Use(Language.German);

    /// <summary>
    /// Two layers under /AS /View /Language: de-DE and en-US. With PDF-Knead in English on a German system the English layer is shown; in German
    /// on an English system the German one (<c>OptionalContent.For</c> without a language). Before, the system's language decided.
    /// </summary>
    [Theory]
    [InlineData("en", "de-DE", false)]
    [InlineData("de", "en-US", true)]
    public void Language_FollowsTheProgram(string program, string system, bool germanShown)
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            Tr.Use(program == "en" ? Language.EnglishLanguage : Language.German);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(system);
            using var doc = PdfDocument.Open(DocumentStructureNormTests.FileOf([
                "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [3 0 R 4 0 R] /D << /AS [<< /Event /View /OCGs [3 0 R 4 0 R] /Category [/Language] >>] >> >> >>",
                "<< /Type /Pages /Kids [] /Count 0 >>",
                "<< /Type /OCG /Name (Deutsch) /Usage << /Language << /Lang (de-DE) >> >> >>",
                "<< /Type /OCG /Name (Englisch) /Usage << /Language << /Lang (en-US) >> >> >>"]));
            var layers = OptionalContent.For(doc);
            Assert.Equal(germanShown, !layers.IsHidden(doc.GetObject(3), null));
            Assert.Equal(!germanShown, !layers.IsHidden(doc.GetObject(4), null));
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }
}
