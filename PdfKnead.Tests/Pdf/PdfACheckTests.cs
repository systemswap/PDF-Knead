using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The rules of the own PDF/A check that a reference validator found missing (KONZEPT.md, section 5.21; docs/PRUEFUNGEN.md, section 6p): the
/// glyphs and widths of shown text against the font programs, the TrueType and CIDFont rules, the syntax of content streams, overprint
/// mode 1 per painting, spot colours, the order of layers, JPEG 2000 without a JP2 wrapper, buttons without an image, signatures. Each rule
/// once broken and once kept, as the validator judges.
/// </summary>
public sealed class PdfACheckTests
{
    /// <summary>One page with the given content and resources (the rest of PDF/A is not of interest here).</summary>
    internal static (TestPdf Pdf, PdfDictionary Page) Page(string content, PdfDictionary resources)
    {
        var pdf = Simple(1);
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, resources);
        page.Set(PdfName.MediaBox, Box(0, 0, 595, 842));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.Latin1.GetBytes(content)));
        return (pdf, page);
    }

    private static IReadOnlyList<PdfAViolation> Check(TestPdf pdf)
    {
        using var doc = PdfDocument.Open(pdf.Build());
        return PdfACheck.Check(doc);
    }

    private static bool Has(IReadOnlyList<PdfAViolation> found, string rule) => found.Any(v => v.Rule == rule);

    [Fact]
    public void TransferFunction_InAGraphicsState_NotTheTableRowOfARoleMap()
    {
        // a role map maps the structure type „TR“ (table row) to a name: no transfer function — the check took it for one in every file with
        // a tagged table (Fehlergedächtnis 862); /TR /Identity in a graphics state stays forbidden
        var (tagged, _) = Page("0 0 10 10 re f", new PdfDictionary());
        // where it stands in a tagged file: the catalog's structure tree, its role map written inline
        var tree = tagged.Add(Dict(("Type", N("StructTreeRoot")), ("RoleMap", Dict(("TR", N("TR")), ("TD", N("TD")), ("Th", N("TH"))))));
        ((PdfDictionary)tagged.Get(tagged.RootNumber)).Set(PdfName.Get("StructTreeRoot"), Ref(tree));
        Assert.DoesNotContain(CheckAs(tagged), v => v.Id == "6.2.5-1");

        var (painted, page) = Page("/GS0 gs 0 0 10 10 re f", new PdfDictionary());
        var state = painted.Add(Dict(("Type", N("ExtGState")), ("TR", N("Identity"))));
        page.Set(PdfName.Resources, Dict(("ExtGState", Dict(("GS0", Ref(state))))));
        Assert.Contains(CheckAs(painted), v => v.Id == "6.2.5-1");
    }

    private static IReadOnlyList<PdfAViolation> CheckAs(TestPdf pdf)
    {
        using var doc = PdfDocument.Open(pdf.Build());
        return PdfACheck.Check(doc, PdfAConformance.A3b);
    }

    // ------------------------------------------------------------ fonts

    /// <summary>Liberation Sans embedded as a simple TrueType font showing „AB“.</summary>
    internal static TestPdf TrueType(int flags = 32, PdfObject? encoding = null, double widthA = 667)
    {
        var program = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var (pdf, page) = Page("BT /F1 12 Tf 72 700 Td (AB) Tj ET", new PdfDictionary());
        var file = pdf.Add(new PdfStream(Dict(("Length1", PdfInteger.Of(program.Length))), program));
        var descriptor = pdf.Add(Dict(("Type", N("FontDescriptor")), ("FontName", N("LiberationSans")), ("Flags", PdfInteger.Of(flags)),
            ("FontBBox", Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", Ref(file))));
        var font = Dict(("Type", N("Font")), ("Subtype", N("TrueType")), ("BaseFont", N("LiberationSans")), ("FirstChar", PdfInteger.Of(65)),
            ("LastChar", PdfInteger.Of(66)), ("Widths", Box(widthA, 667)), ("FontDescriptor", Ref(descriptor)));
        if (encoding is not null) font.Set(PdfName.Get("Encoding"), encoding);
        page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(pdf.Add(font)))))));
        return pdf;
    }

    [Fact]
    public void TrueType_WidthsAgainstTheProgram_NotdefByName_SymbolicWithoutEncoding()
    {
        var fine = Check(TrueType(encoding: N("WinAnsiEncoding")));
        Assert.False(Has(fine, "6.2.11.5") || Has(fine, "6.2.11.4.1") || Has(fine, "6.2.11.6") || Has(fine, "6.2.11.8"), string.Join("\n", fine));
        // the file gives „A“ another width than the program: readers place the text by the file, the program says otherwise
        Assert.True(Has(Check(TrueType(encoding: N("WinAnsiEncoding"), widthA: 700)), "6.2.11.5"));
        // a code the encoding names .notdef
        var notdef = Dict(("BaseEncoding", N("WinAnsiEncoding")), ("Differences", new PdfArray([PdfInteger.Of(65), N(".notdef")])));
        Assert.True(Has(Check(TrueType(encoding: notdef)), "6.2.11.8"));
        // non-symbolic without WinAnsi or MacRoman; symbolic with an encoding
        Assert.True(Has(Check(TrueType()), "6.2.11.6"));
        Assert.True(Has(Check(TrueType(flags: 4, encoding: N("WinAnsiEncoding"))), "6.2.11.6"));
    }

    [Fact]
    public void Type1AsPfb_IsNotInThePrescribedForm()
    {
        var (pdf, page) = Page("BT /F1 12 Tf 72 700 Td (A) Tj ET", new PdfDictionary());
        var file = pdf.Add(new PdfStream(Dict(("Length1", PdfInteger.Of(10)), ("Length2", PdfInteger.Of(10)), ("Length3", PdfInteger.Of(0))),
            [0x80, 0x01, 10, 0, 0, 0, (byte)'%', (byte)'!', (byte)'P', (byte)'S']));
        var descriptor = pdf.Add(Dict(("Type", N("FontDescriptor")), ("FontName", N("Erfunden")), ("Flags", PdfInteger.Of(32)), ("FontFile", Ref(file))));
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Erfunden")), ("FirstChar", PdfInteger.Of(65)),
            ("LastChar", PdfInteger.Of(65)), ("Widths", Box(600)), ("FontDescriptor", Ref(descriptor))));
        page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(font))))));
        var found = Check(pdf);
        Assert.Contains(found, v => v.Rule == "6.2.11.4.1" && v.Text.Contains("vorgeschriebenen Form"));
    }

    [Fact]
    public void CidFontType2_WithoutCidToGidMap()
    {
        var program = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var (pdf, page) = Page("BT /F1 12 Tf 72 700 Td <0024> Tj ET", new PdfDictionary());
        var file = pdf.Add(new PdfStream(new PdfDictionary(), program));
        var descriptor = pdf.Add(Dict(("Type", N("FontDescriptor")), ("FontName", N("LiberationSans")), ("Flags", PdfInteger.Of(32)), ("FontFile2", Ref(file))));
        var cid = Dict(("Type", N("Font")), ("Subtype", N("CIDFontType2")), ("BaseFont", N("LiberationSans")), ("FontDescriptor", Ref(descriptor)),
            ("CIDSystemInfo", Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())), ("Supplement", PdfInteger.Of(0)))),
            ("DW", PdfInteger.Of(667)));
        var font = Dict(("Type", N("Font")), ("Subtype", N("Type0")), ("BaseFont", N("LiberationSans")), ("Encoding", N("Identity-H")),
            ("DescendantFonts", new PdfArray([cid])));
        page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(pdf.Add(font)))))));
        Assert.True(Has(Check(pdf), "6.2.11.3.2"));
        cid.Set(PdfName.Get("CIDToGIDMap"), N("Identity"));
        var found = Check(pdf);
        Assert.False(Has(found, "6.2.11.3.2") || Has(found, "6.2.11.5"), string.Join("\n", found));
    }

    // ------------------------------------------------------------ content streams

    /// <summary>Reals up to ±3.403·10³⁸ are allowed: only integers end at 2³¹ (as the reference validator holds it).</summary>
    [Fact]
    public void LargeReal_IsAllowed()
    {
        var data = Encoding.Latin1.GetBytes("99999999999.5 0 m 1 0.5 l S");
        Assert.Equal(ContentFaults.None, ContentConformance.Scan(data));
    }

    [Theory]
    [InlineData("BX /X 1 foo EX 0 0 1 rg", "6.2.2")]                // an unknown operator, also between BX and EX
    [InlineData("0 0 10 10 re% comment\nf", "6.2.2")]               // a comment right after the operator
    [InlineData("BT /F1 12 Tf <414> Tj ET", "6.1.6")]               // an odd number of hexadecimal digits
    [InlineData("BT /F1 12 Tf [(A) - (B)] TJ ET", "6.1.13")]        // a sign without digits
    [InlineData("99999999999 0 0 1 0 0 cm", "6.1.13")]             // beyond the integer limit
    [InlineData("72 600 . 100 re f", "6.1.13")]                     // a point without digits (strict readers: a real out of range)
    public void ContentSyntax_IsFound_AndWrittenAnewWithout(string content, string rule)
    {
        var (pdf, _) = Page(content, Dict(("Font", Dict(("F1", Ref(3))))));
        Assert.True(Has(Check(pdf), rule));
        var data = Encoding.Latin1.GetBytes(content);
        Assert.NotEqual(ContentFaults.None, ContentConformance.Scan(data));
        var rewritten = ContentConformance.Rewrite(data);
        Assert.Equal(ContentFaults.None, ContentConformance.Scan(rewritten));
        // the same operators as the display reads them, the unknown ones left out
        Assert.Equal(Operators(data).Where(ContentParser.Operators.Contains), Operators(rewritten));
    }

    private static List<string> Operators(byte[] data)
    {
        var parser = new ContentParser(data);
        var ops = new List<string>();
        while (parser.Next() is { } op) ops.Add(op);
        return ops;
    }

    [Fact]
    public void ContentWithoutFaults_IsLeftAlone()
    {
        Assert.Equal(ContentFaults.None, ContentConformance.Scan("q /Name-1 gs 1 0 0 1 -5.5 .5 cm (a-b) Tj [(x) -20 (y)] TJ <4142> Tj Q"u8.ToArray()));
    }

    [Fact]
    public void FormWithoutResources_UsingSome()
    {
        var (pdf, page) = Page("/Fm1 Do", new PdfDictionary());
        var form = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 100, 100))),
            "BT /F1 12 Tf (A) Tj ET"u8.ToArray()));
        page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(3)))), ("XObject", Dict(("Fm1", Ref(form))))));
        Assert.True(Has(Check(pdf), "6.2.2"));
    }

    // ------------------------------------------------------------ colour

    [Fact]
    public void OverprintMode1_OnlyWhereTheIccCmykPaintingOverprints()
    {
        PdfDictionary Resources(TestPdf pdf, PdfDictionary gs)
        {
            var profile = pdf.Add(new PdfStream(Dict(("N", PdfInteger.Of(4))), new byte[200]));
            return Dict(("ColorSpace", Dict(("CS0", new PdfArray([N("ICCBased"), Ref(profile)])))), ("ExtGState", Dict(("GS0", gs))));
        }
        var (fill, fillPage) = Page("/GS0 gs /CS0 cs 0 0 0 1 scn 0 0 10 10 re f", new PdfDictionary());
        fillPage.Set(PdfName.Resources, Resources(fill, Dict(("OPM", PdfInteger.Of(1)), ("op", PdfBoolean.True))));
        Assert.Contains(Check(fill), v => v.Id == "6.2.4.2-2");
        // overprint for stroking only, the ICC-based CMYK fills: allowed
        var (stroke, strokePage) = Page("/GS0 gs /CS0 cs 0 0 0 1 scn 0 0 10 10 re f", new PdfDictionary());
        strokePage.Set(PdfName.Resources, Resources(stroke, Dict(("OPM", PdfInteger.Of(1)), ("OP", PdfBoolean.True), ("op", PdfBoolean.False))));
        Assert.DoesNotContain(Check(stroke), v => v.Id == "6.2.4.2-2");
    }

    [Fact]
    public void DeviceN_SpotColoursNeedColorants_ProcessColoursDoNot()
    {
        var tint = Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", Box(0, 1)), ("C0", Box(0, 0, 0, 0)), ("C1", Box(0, 0, 0, 1)), ("N", PdfInteger.Of(1)));
        PdfArray Space(string colourant) => new([N("DeviceN"), new PdfArray([N(colourant)]), N("DeviceCMYK"), tint]);
        Assert.True(Has(Check(Page("/CS0 cs 1 scn 0 0 10 10 re f", Dict(("ColorSpace", Dict(("CS0", Space("Gold")))))).Pdf), "6.2.4.4"));
        Assert.False(Has(Check(Page("/CS0 cs 1 scn 0 0 10 10 re f", Dict(("ColorSpace", Dict(("CS0", Space("Cyan")))))).Pdf), "6.2.4.4"));
    }

    // ------------------------------------------------------------ document

    [Fact]
    public void LayerOrder_NamesEveryLayer_ByName()
    {
        var (pdf, _) = Page("0 0 10 10 re f", new PdfDictionary());
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        var a = pdf.Add(Dict(("Type", N("OCG")), ("Name", new PdfString("Ebene"u8.ToArray()))));
        var b = pdf.Add(Dict(("Type", N("OCG")), ("Name", new PdfString("Zweite"u8.ToArray()))));
        catalog.Set(PdfName.Get("OCProperties"), Dict(("OCGs", new PdfArray([Ref(a), Ref(b)])),
            ("D", Dict(("Name", new PdfString("Standard"u8.ToArray())), ("Order", new PdfArray([Ref(a)]))))));
        Assert.True(Has(Check(pdf), "6.9"));
        // two layers of one name: the order names it (validators compare names)
        pdf.Replace(b, Dict(("Type", N("OCG")), ("Name", new PdfString("Ebene"u8.ToArray()))));
        Assert.False(Has(Check(pdf), "6.9"));
    }

    [Fact]
    public void Jpeg2000_WithoutJp2Wrapper()
    {
        // a bare codestream: SOC, SIZ with one component of 8 bits
        var codestream = new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0, 41, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 7, 1, 1 };
        Assert.NotNull(PdfAJpeg2000.Fault(codestream));
        var (pdf, page) = Page("q 10 0 0 10 0 0 cm /Im1 Do Q", new PdfDictionary());
        var image = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(1)), ("Height", PdfInteger.Of(1)),
            ("ColorSpace", N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", N("JPXDecode"))), codestream));
        page.Set(PdfName.Resources, Dict(("XObject", Dict(("Im1", Ref(image))))));
        Assert.True(Has(Check(pdf), "6.2.8.3"));
    }

    [Fact]
    public void Button_WithoutAnyStateImage()
    {
        var (pdf, page) = Page("0 0 10 10 re f", new PdfDictionary());
        var look = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 10, 10))), "0 0 10 10 re S"u8.ToArray()));
        PdfDictionary Widget(PdfObject on) => Dict(("Type", N("Annot")), ("Subtype", N("Widget")), ("FT", N("Btn")), ("Rect", Box(72, 72, 82, 82)),
            ("F", PdfInteger.Of(4)), ("AS", N("Off")), ("AP", Dict(("N", Dict(("Ja", on), ("Off", N("Off")))))));
        var none = pdf.Add(Widget(N("null")));
        page.Set(PdfName.Get("Annots"), new PdfArray([Ref(none)]));
        Assert.True(Has(Check(pdf), "6.3.3"));
        // one state with an image is enough
        pdf.Replace(none, Widget(Ref(look)));
        Assert.False(Has(Check(pdf), "6.3.3"));
    }

    [Fact]
    public void Signature_ThatDoesNotCoverTheFile()
    {
        var (pdf, _) = Page("0 0 10 10 re f", new PdfDictionary());
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        var signature = Dict(("Type", N("Sig")), ("ByteRange", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(10), PdfInteger.Of(20), PdfInteger.Of(5)])),
            ("Contents", new PdfString(new byte[8])));
        var field = pdf.Add(Dict(("FT", N("Sig")), ("T", new PdfString("Unterschrift"u8.ToArray())), ("V", signature)));
        catalog.Set(PdfName.Get("AcroForm"), Dict(("Fields", new PdfArray([Ref(field)]))));
        Assert.True(Has(Check(pdf), "6.4.3"));
    }
}
