using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// What the PDF/A writer brings in line after the counter-check with a reference validator (KONZEPT.md, section 5.21; docs/FEHLERGEDAECHTNIS.md,
/// No. 298): fonts rebuilt from what the display draws (TrueType symbolic with its own cmap and the widths of the file, Type 1/CFF as CFF from the
/// outlines, Type 3 with procedures of the file's widths), content written anew without faulty syntax, and the rules on dictionaries — each
/// time the copy conforms, and the page looks and reads the same.
/// </summary>
public sealed class PdfARepairTests
{
    private static PdfAResult Write(byte[] data)
    {
        using var doc = PdfDocument.Open(data);
        return PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b);
    }

    /// <summary>The copy conforms, its first page looks exactly like the original's and reads the same.</summary>
    private static PdfDocument Same(byte[] original, PdfAResult result)
    {
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
        using var before = PdfDocument.Open(original);
        var after = PdfDocument.Open(result.Bytes);
        using var a = ContentImageTests.Render(before, before.Pages[0]);
        using var b = ContentImageTests.Render(after, after.Pages[0]);
        Assert.True(a.Bytes.AsSpan().SequenceEqual(b.Bytes), "die Seite sieht anders aus");
        Assert.Equal(Text(before), Text(after));
        return after;
    }

    private static string Text(PdfDocument doc) => Core.Pdf.Text.PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]), 0).Text;

    private static PdfDictionary FirstFont(PdfDocument doc) =>
        doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;

    [Fact]
    public void TrueType_WithOtherWidthsAndWithoutEncoding_RebuiltSymbolic()
    {
        // „A“ 700 wide in the file, 667 in the program; no /Encoding for a non-symbolic font
        var original = PdfACheckTests.TrueType(widthA: 700).Build();
        using var doc = Same(original, Write(original));
        var font = FirstFont(doc);
        Assert.False(font.ContainsKey(PdfName.Get("Encoding")));
        var descriptor = font.GetDictionary(PdfName.Get("FontDescriptor"))!;
        Assert.Equal(4, (int)descriptor.GetInteger(PdfName.Get("Flags"))! & 36);
        var program = OpenTypeFont.Parse(doc.GetStreamData((PdfStream)descriptor.Get(PdfName.Get("FontFile2"))))!;
        Assert.Equal([(3, 0)], program.CmapSubtables);
        var a = program.Lookup(3, 0, 0xF000 + 'A');
        Assert.Equal(700, program.AdvanceWidth(a) * 1000 / program.UnitsPerEm, 0.6);
    }

    [Fact]
    public void Cff_WithOtherWidths_RebuiltFromTheOutlines()
    {
        var (pdf, page) = PdfACheckTests.Page("BT /F1 30 Tf 72 700 Td (AB) Tj ET", new PdfDictionary());
        var file = pdf.Add(new PdfStream(Dict(("Subtype", N("Type1C"))), EmbeddedFonts.LoadStandard("FoxitSymbol.cff")));
        var descriptor = pdf.Add(Dict(("Type", N("FontDescriptor")), ("FontName", N("Erfunden")), ("Flags", PdfInteger.Of(4)),
            ("FontBBox", Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile3", Ref(file))));
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Erfunden")), ("FirstChar", PdfInteger.Of(65)),
            ("LastChar", PdfInteger.Of(66)), ("Widths", Box(800, 667)), ("FontDescriptor", Ref(descriptor)),
            ("Encoding", Dict(("Differences", new PdfArray([PdfInteger.Of(65), N("Alpha"), N("Beta")]))))));
        page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(font))))));
        var original = pdf.Build();
        using var doc = Same(original, Write(original));
        var written = FirstFont(doc);
        var program = written.GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile3"))!;
        Assert.Equal("Type1C", program.Dictionary.GetName(PdfName.Subtype)?.Value);
        Assert.Equal(["g65", "g66"], written.GetDictionary(PdfName.Get("Encoding"))!.GetArray(PdfName.Get("Differences"))!.OfType<PdfName>().Select(n => n.Value));
    }

    [Fact]
    public void Type3_ProcedureWidthAndMissingGlyph_Completed()
    {
        // „a“ sets 500 in its procedure, the file 1000; „b“ has no procedure
        var (pdf, page) = PdfACheckTests.Page("BT /F1 1 Tf 72 700 Td (ab) Tj ET", new PdfDictionary());
        var square = pdf.Add(new PdfStream(new PdfDictionary(), "500 0 0 0 1000 1000 d1 0 0 1000 1000 re f"u8.ToArray()));
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type3")), ("FontBBox", Box(0, 0, 1000, 1000)), ("FontMatrix", Box(0.02, 0, 0, 0.02, 0, 0)),
            ("CharProcs", Dict(("square", Ref(square)))), ("Encoding", Dict(("Differences", new PdfArray([PdfInteger.Of(97), N("square"), N("nothing")])))),
            ("FirstChar", PdfInteger.Of(97)), ("LastChar", PdfInteger.Of(98)), ("Widths", Box(1000, 1000))));
        page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(font))))));
        var original = pdf.Build();
        Assert.Contains(PdfACheck.Check(PdfDocument.Open(original)), v => v.Rule == "6.2.11.5");
        using var doc = Same(original, Write(original));
        var procs = FirstFont(doc).GetDictionary(PdfName.Get("CharProcs"))!;
        Assert.StartsWith("1000 0 0 0 1000 1000 d1", Encoding.ASCII.GetString(doc.GetStreamData(procs.GetStream(PdfName.Get("PDFKneadGlyph97"))!)));
        Assert.NotNull(procs.GetStream(PdfName.Get("PDFKneadGlyph98")));
    }

    [Fact]
    public void ContentWithUnknownOperatorsAndOddHexStrings_WrittenAnew()
    {
        var (pdf, _) = PdfACheckTests.Page("1 0 0 rg BX /X 1 foo EX 72 600 200 100 re% Kommentar\nf BT /F1 12 Tf 72 500 Td <41424> Tj ET",
            Dict(("Font", Dict(("F1", Ref(3))))));
        var original = pdf.Build();
        using var doc = Same(original, Write(original));
        Assert.Equal(ContentFaults.None, ContentConformance.Scan(doc.GetStreamData((PdfStream)doc.Pages[0].Dictionary.Get(PdfName.Contents))));
    }

    [Fact]
    public void LayersOverprintSignaturesBoxesAndShadingPatterns_BroughtInLine()
    {
        var (pdf, page) = PdfACheckTests.Page("/GS0 gs 0 0 0 1 k 72 600 100 100 re f /Pattern cs /P0 scn 72 400 300 100 re f", new PdfDictionary());
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        // a layer missing in the order
        var a = pdf.Add(Dict(("Type", N("OCG")), ("Name", new PdfString("Ebene"u8.ToArray()))));
        var b = pdf.Add(Dict(("Type", N("OCG")), ("Name", new PdfString("Zweite"u8.ToArray()))));
        catalog.Set(PdfName.Get("OCProperties"), Dict(("OCGs", new PdfArray([Ref(a), Ref(b)])),
            ("D", Dict(("Name", new PdfString("Standard"u8.ToArray())), ("Order", new PdfArray([Ref(a)]))))));
        // a signature over bytes the copy no longer has
        var signature = Dict(("Type", N("Sig")), ("ByteRange", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(10), PdfInteger.Of(20), PdfInteger.Of(5)])),
            ("Contents", new PdfString(new byte[8])));
        var field = pdf.Add(Dict(("FT", N("Sig")), ("T", new PdfString("Unterschrift"u8.ToArray())), ("V", signature)));
        catalog.Set(PdfName.Get("AcroForm"), Dict(("Fields", new PdfArray([Ref(field)]))));
        // the media box as references, a bleed box of nothing
        var left = pdf.Add(PdfInteger.Of(0));
        page.Set(PdfName.MediaBox, new PdfArray([Ref(left), PdfInteger.Of(0), PdfInteger.Of(595), PdfInteger.Of(842)]));
        page.Set(PdfName.Get("BleedBox"), Box(0, 0, 0, 0));
        // overprint mode 1 with DeviceCMYK (it becomes ICC-based), a shading pattern in DeviceCMYK
        var shading = Dict(("ShadingType", PdfInteger.Of(2)), ("ColorSpace", N("DeviceCMYK")), ("Coords", Box(72, 0, 372, 0)),
            ("Function", Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", Box(0, 1)), ("C0", Box(1, 0, 0, 0)), ("C1", Box(0, 0, 1, 0)), ("N", PdfInteger.Of(1)))));
        page.Set(PdfName.Resources, Dict(("ExtGState", Dict(("GS0", Dict(("OPM", PdfInteger.Of(1)), ("op", PdfBoolean.True))))),
            ("Pattern", Dict(("P0", Dict(("PatternType", PdfInteger.Of(2)), ("Shading", shading)))))));
        var original = pdf.Build();
        using var doc = Same(original, Write(original));
        var order = doc.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!.GetDictionary(PdfName.Get("D"))!.GetArray(PdfName.Get("Order"))!;
        Assert.Equal(2, order.Count);
        Assert.False(((PdfDictionary)doc.Catalog.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!.Get(0)).ContainsKey(PdfName.Get("V")));
        Assert.False(doc.Pages[0].Dictionary.ContainsKey(PdfName.Get("BleedBox")));
    }

    [Fact]
    public void StandardFontShownWithEmptyTextOrCodesWithoutGlyph_Embedded()
    {
        // code 0 has no name in WinAnsi: written symbolic, finding an empty glyph by its code
        var (pdf, _) = PdfACheckTests.Page("BT /F1 12 Tf 72 700 Td () Tj 0 -20 Td <00414200> Tj ET", Dict(("Font", Dict(("F1", Ref(3))))));
        pdf.Replace(3, Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("Encoding", N("WinAnsiEncoding"))));
        var original = pdf.Build();
        using var doc = Same(original, Write(original));
        Assert.NotNull(FirstFont(doc).GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile2")));
    }

    [Fact]
    public void SpotColoursOfDeviceN_GetColorants()
    {
        var tint = Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", Box(0, 1)), ("C0", Box(0, 0, 0, 0)), ("C1", Box(0.2, 0.4, 0, 0.1)), ("N", PdfInteger.Of(1)));
        var space = new PdfArray([N("DeviceN"), new PdfArray([N("Gold")]), N("DeviceCMYK"), tint]);
        var (pdf, _) = PdfACheckTests.Page("/CS0 cs 1 scn 72 600 200 100 re f", Dict(("ColorSpace", Dict(("CS0", space)))));
        var original = pdf.Build();
        using var doc = Same(original, Write(original));
        var written = (PdfArray)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("ColorSpace"))!.Get(PdfName.Get("CS0"));
        var separation = (PdfArray)((PdfDictionary)written.Get(4)).GetDictionary(PdfName.Get("Colorants"))!.Get(PdfName.Get("Gold"));
        Assert.Equal("Separation", ((PdfName)separation.Get(0)).Value);
    }

    private static PdfArray Spot(string name, double c, double m, double y, double k) => new([N("DeviceN"), new PdfArray([N(name)]), N("DeviceCMYK"),
        Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", Box(0, 1)), ("C0", Box(0, 0, 0, 0)), ("C1", Box(c, m, y, k)), ("N", PdfInteger.Of(1)))]);

    private static PdfArray Colorants(PdfDocument doc, int page) =>
        (PdfArray)((PdfArray)doc.Pages[page].Resources!.GetDictionary(PdfName.Get("ColorSpace"))!.Get(PdfName.Get("CS0"))).Get(1);

    /// <summary>
    /// The same spot colour name in DeviceN spaces of several pages, described differently (without /Colorants, as merged files bring them): the
    /// descriptions the writer adds must be alike per name in the whole file (6.2.4.4) — a different one gets a name of its own, an alike one
    /// keeps the name (docs/FEHLERGEDAECHTNIS.md, No. 337: three pages of a merged file failed).
    /// </summary>
    [Fact]
    public void SpotColourOfOneNameDescribedDifferently_SecondRenamed_AlikeKept()
    {
        var pdf = Simple(3, title: null);
        var spaces = new[] { Spot("X", 0.2, 0.4, 0, 0.1), Spot("X", 0, 0, 1, 0), Spot("X", 0.2, 0.4, 0, 0.1) };
        for (var i = 0; i < 3; i++)
        {
            ((PdfDictionary)pdf.Get(5 + 2 * i)).Set(PdfName.Resources, Dict(("ColorSpace", Dict(("CS0", spaces[i])))));
            pdf.Replace(4 + 2 * i, new PdfStream(new PdfDictionary(), "/CS0 cs 1 scn 72 600 200 100 re f"u8.ToArray()));
        }
        var result = Write(pdf.Build());
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
        using var doc = PdfDocument.Open(result.Bytes);
        Assert.Equal("X", ((PdfName)Colorants(doc, 0).Get(0)).Value);
        Assert.Equal("X 2", ((PdfName)Colorants(doc, 1).Get(0)).Value);
        Assert.Equal("X", ((PdfName)Colorants(doc, 2).Get(0)).Value);
    }

    /// <summary>
    /// A page whose resources are an object of their own with a key of no resource category, drawing in RGB, in a file whose output intent is
    /// CMYK (another page draws in CMYK): its resources still get the default RGB space (6.2.4.3; docs/FEHLERGEDAECHTNIS.md, No. 337).
    /// </summary>
    [Fact]
    public void PageResourcesWithOddKey_GetDefaultSpaces()
    {
        var pdf = Simple(2, title: null);
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, new PdfDictionary());
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 0 1 k 72 600 200 100 re f"u8.ToArray()));
        var odd = pdf.Add(Dict(("Pi", new PdfReal(3.141593))));
        ((PdfDictionary)pdf.Get(7)).Set(PdfName.Resources, Ref(odd));
        pdf.Replace(6, new PdfStream(new PdfDictionary(), "0 1 0 rg 72 600 200 100 re f"u8.ToArray()));
        var result = Write(pdf.Build());
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
    }

    /// <summary>
    /// Two check boxes of the same name on two pages, each its own field (as merged files bring them): linked into one field, each widget
    /// keeps its states as a button must (6.3.3) — the link put /Parent into the new file, where the field type could not be followed
    /// (docs/FEHLERGEDAECHTNIS.md, No. 337).
    /// </summary>
    [Fact]
    public void SameNamedCheckBoxesOnTwoPages_Linked_KeepTheirStates()
    {
        var pdf = Simple(2, title: null);
        var fields = new PdfArray();
        for (var i = 0; i < 2; i++)
        {
            int Look(string content) => pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 20, 20))),
                Encoding.ASCII.GetBytes(content)));
            var states = Dict(("Off", Ref(Look("0 G 1 1 18 18 re S"))), ("Yes", Ref(Look("0 g 4 4 12 12 re f"))));
            var widget = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Widget")), ("Rect", Box(72, 700, 92, 720)), ("F", PdfInteger.Of(4)),
                ("FT", N("Btn")), ("T", new PdfString("Check Box1"u8.ToArray())), ("AS", N(i == 0 ? "Yes" : "Off")), ("AP", Dict(("N", states))),
                ("P", Ref(5 + 2 * i))));
            ((PdfDictionary)pdf.Get(5 + 2 * i)).Set(PdfName.Get("Annots"), new PdfArray([Ref(widget)]));
            fields.Add(Ref(widget));
        }
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), Dict(("Fields", fields)));
        var result = Write(pdf.Build());
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
        using var doc = PdfDocument.Open(result.Bytes);
        for (var i = 0; i < 2; i++)
        {
            var widget = doc.Pages[i].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0) as PdfDictionary;
            Assert.IsType<PdfDictionary>(widget!.GetDictionary(PdfName.Get("AP"))!.Get(PdfName.N));
        }
    }

    /// <summary>
    /// A page with more annotations than a PDF/A-1 list may hold (8 191, ISO 19005-1, 6.1.12): the comments beyond go into the content as drawn,
    /// the field stays an annotation, and the page looks the same (docs/FEHLERGEDAECHTNIS.md, No. 337: a test file with 32 768).
    /// </summary>
    [Fact]
    public void MoreAnnotationsThanAListMayHold_PdfA1_RestDrawnIntoTheContent()
    {
        var pdf = Simple(1, title: null);
        var look = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 4, 4))), "1 0 0 rg 0 0 4 4 re f"u8.ToArray()));
        var annots = new PdfArray();
        for (var i = 0; i < 8200; i++)
        {
            var (x, y) = (20 + i % 100 * 5, 40 + i / 100 * 5);
            annots.Add(Ref(pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Square")), ("Rect", Box(x, y, x + 4, y + 4)), ("F", PdfInteger.Of(4)),
                ("AP", Dict(("N", Ref(look))))))));
        }
        var field = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Widget")), ("Rect", Box(400, 780, 500, 800)), ("F", PdfInteger.Of(4)), ("FT", N("Tx")),
            ("T", new PdfString("Name"u8.ToArray())), ("AP", Dict(("N", Ref(look))))));
        annots.Add(Ref(field));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), annots);
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), Dict(("Fields", new PdfArray([Ref(field)]))));
        var original = pdf.Build();
        using var doc = PdfDocument.Open(original);
        var result = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A1b);
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
        using var written = PdfDocument.Open(result.Bytes);
        var kept = written.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        Assert.Equal(8191, kept.Count);
        Assert.Contains(Enumerable.Range(0, kept.Count), i => kept.Get(i) is PdfDictionary a && a.GetName(PdfName.Subtype)?.Value == "Widget");
        using var a = ContentImageTests.Render(doc, doc.Pages[0]);
        using var b = ContentImageTests.Render(written, written.Pages[0]);
        Assert.True(a.Bytes.AsSpan().SequenceEqual(b.Bytes), "die Seite sieht anders aus");
    }

    /// <summary>
    /// More links on a page than a PDF/A-1 list may hold (a test file of the public collection has 32 768): links draw nothing and cannot go
    /// into the content — the first 8 191 stay, the field too, the rest are left out; the page looks the same.
    /// </summary>
    [Fact]
    public void MoreLinksThanAListMayHold_PdfA1_FirstKept()
    {
        var pdf = Simple(1, title: null);
        var annots = new PdfArray();
        for (var i = 0; i < 8300; i++)
            annots.Add(Ref(pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Link")), ("Rect", Box(i % 500, i / 500, i % 500 + 1, i / 500 + 1)),
                ("F", PdfInteger.Of(4)), ("Border", Box(0, 0, 0)), ("A", Dict(("S", N("URI")), ("URI", new PdfString("https://example.com/"u8.ToArray()))))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), annots);
        using var doc = PdfDocument.Open(pdf.Build());
        var result = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A1b);
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
        using var written = PdfDocument.Open(result.Bytes);
        Assert.Equal(8191, written.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Count);
    }

    /// <summary>
    /// A form without resources of its own (drawn by a form that has some) painting in RGB, the output intent CMYK: strict readers take the
    /// default RGB space only from the form's own resources (6.2.4.3), so the copy gives it some — and the check counts inherited ones no more
    /// (docs/FEHLERGEDAECHTNIS.md, No. 337: the reference validator rejected a page of a merged file the own check had passed).
    /// </summary>
    [Fact]
    public void FormWithoutResourcesInRgb_CmykIntent_GetsOwnDefaultSpaces()
    {
        var pdf = Simple(2, title: null);
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, new PdfDictionary());
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 0 1 k 72 600 200 100 re f"u8.ToArray()));
        var inner = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 20, 20))), "0 1 0 rg 0 0 20 20 re f"u8.ToArray()));
        var outer = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 200, 50)),
            ("Resources", Dict(("XObject", Dict(("Im", Ref(inner)))))) ), "q 2 0 0 2 10 10 cm /Im Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(7)).Set(PdfName.Resources, Dict(("XObject", Dict(("Fm", Ref(outer))))));
        pdf.Replace(6, new PdfStream(new PdfDictionary(), "/Fm Do"u8.ToArray()));
        var result = Write(pdf.Build());
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));

        // the check: with the inner form's resources taken away again, the outer form's default RGB space does not count for it
        using var written = PdfDocument.Open(result.Bytes);
        Assert.DoesNotContain(PdfACheck.Check(written), v => v.Id == PdfARules.DeviceRgb.Id(PdfAConformance.A2b));
        var outerForm = (PdfStream)written.Pages[1].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Fm"));
        Assert.NotNull(outerForm.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("ColorSpace"))?.Get(PdfName.Get("DefaultRGB")));
        var innerRef = (PdfReference)outerForm.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!.GetRaw(PdfName.Get("Im"))!;
        var innerForm = (PdfStream)written.Resolve(innerRef.Id)!;
        var bare = new PdfDictionary();
        foreach (var (key, value) in innerForm.Dictionary.Entries)
            if (key != PdfName.Resources && key != PdfName.Filter && key != PdfName.Get("Length")) bare.Set(key, value);
        var update = new IncrementalUpdate(written);
        update.Replace(innerRef.Id, new PdfStream(bare, written.GetStreamData(innerForm)));
        using var output = new MemoryStream();
        update.WriteTo(output);
        using var stripped = PdfDocument.Open(output.ToArray());
        Assert.Contains(PdfACheck.Check(stripped), v => v.Id == PdfARules.DeviceRgb.Id(PdfAConformance.A2b));
    }

    /// <summary>
    /// A content stream packed with a filter of a name no reader knows (a damaged name), and a stream nothing refers to with the same: the copy
    /// has no such filter anywhere — the unreadable data written empty —, and the check reads unreferenced objects too (6.1.7.2;
    /// docs/FEHLERGEDAECHTNIS.md, No. 340: the reference validator rejected a copy the own check had passed).
    /// </summary>
    [Fact]
    public void UnknownFilterName_ContentAndUnreferencedStream_NoneLeft()
    {
        var pdf = Simple(1, title: null);
        pdf.Replace(4, new PdfStream(Dict(("Filter", N("FlateDekode"))), [0x78, 0x9C, 0x01, 0x02]));
        pdf.Add(new PdfStream(Dict(("Filter", N("FlateDekode"))), [1, 2, 3]));
        var result = Write(pdf.Build());
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
        using (var copy = PdfDocument.Open(result.Bytes))
        {
            var size = (int)(copy.Trailer.GetInteger(PdfName.Size) ?? 0);
            Assert.DoesNotContain(Enumerable.Range(1, size - 1), n => copy.HasObject(n) && copy.GetObject(n) is PdfStream st && st.Dictionary.GetName(PdfName.Filter)?.Value == "FlateDekode");
        }

        // the check: an unreferenced stream with such a filter in a copy that meets every other rule
        using var written = PdfDocument.Open(result.Bytes);
        var update = new IncrementalUpdate(written);
        update.Add(new PdfStream(Dict(("Filter", N("FlateDekode"))), [1, 2, 3]));
        using var output = new MemoryStream();
        update.WriteTo(output);
        using var withOrphan = PdfDocument.Open(output.ToArray());
        Assert.Contains(PdfACheck.Check(withOrphan), v => v.Id == PdfARules.StandardFilters.Id(PdfAConformance.A2b));
    }

    /// <summary>
    /// A text operator without its text (`/F1 14 Tf Tj`, its string lost) with a font that is not embedded: the display draws nothing, strict
    /// readers count the font as used — the check says so, and the copy leaves the operator out (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    [Theory]
    [InlineData("Tj")]
    [InlineData("'")]
    [InlineData("1 2 \"")]
    [InlineData("TJ")]
    public void TextOperatorWithoutText_CheckedAsUsed_LeftOut(string op)
    {
        var pdf = Simple(1, title: null);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes($"BT 10 20 Td /F1 14 Tf {op} ET 0 0 1 rg 72 600 200 100 re f")));
        var original = pdf.Build();
        using (var doc = PdfDocument.Open(original))
            Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A2b), v => v.Id == PdfARules.FontEmbedded.Id(PdfAConformance.A2b));
        var result = Write(original);
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
        using var written = PdfDocument.Open(result.Bytes);
        Assert.DoesNotContain(" " + op.Split(' ')[^1] + "\n", "\n" + Encoding.Latin1.GetString(written.GetStreamData((PdfStream)written.Pages[0].Dictionary.Get(PdfName.Contents)!)));
    }

    /// <summary>
    /// A TrueType program a strict reader cannot read — cut short (its tables beyond the end of the data), or with fewer offsets in loca than
    /// maxp counts glyphs: the check says it cannot be read, the copy embeds the substitute from what the display draws, and the page looks and
    /// reads the same (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    [Theory]
    [InlineData("abgeschnitten")]
    [InlineData("loca")]
    public void TrueTypeProgramAStrictReaderRefuses_EmbeddedAsTheSubstitute(string damage)
    {
        var program = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf").ToArray();
        if (damage == "abgeschnitten") program = program[..(program.Length * 2 / 3)];
        else
        {
            // maxp counts more glyphs than loca has offsets for
            var tables = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(4));
            for (var i = 0; i < tables; i++)
                if (Encoding.ASCII.GetString(program, 12 + 16 * i, 4) == "maxp")
                {
                    var offset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(program.AsSpan(12 + 16 * i + 8));
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(program.AsSpan(offset + 4), 60000);
                }
        }
        var pdf = Simple(1, title: null);
        var file = pdf.Add(FlateStream(program, Dict(("Length1", PdfInteger.Of(program.Length)))));
        var descriptor = pdf.Add(Dict(("Type", N("FontDescriptor")), ("FontName", N("Muster")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", Ref(file))));
        var widths = new PdfArray();
        for (var c = 32; c <= 126; c++) widths.Add(PdfInteger.Of(556));
        pdf.Replace(3, Dict(("Type", N("Font")), ("Subtype", N("TrueType")), ("BaseFont", N("Muster")), ("Encoding", N("WinAnsiEncoding")),
            ("FirstChar", PdfInteger.Of(32)), ("LastChar", PdfInteger.Of(126)), ("Widths", widths), ("FontDescriptor", Ref(descriptor))));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "BT /F1 24 Tf 72 700 Td (Archiv 2026) Tj ET"u8.ToArray()));
        var original = pdf.Build();
        using (var doc = PdfDocument.Open(original))
            Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A2b), v => v.Id == PdfARules.FontEmbedded.Id(PdfAConformance.A2b));
        var result = Write(original);
        Assert.Equal(["Muster"], result.SubstitutedFonts);
        using var _ = Same(original, result);
    }

    /// <summary>
    /// „ID“ and „EI“ standing alone in a content stream (belonging to no inline image): no operator ISO 32000 defines — the check says so, the
    /// copy leaves them out, the page looks the same (docs/FEHLERGEDAECHTNIS.md, No. 340: in a glyph procedure of a Type 3 font).
    /// </summary>
    [Fact]
    public void StrayInlineImageOperators_CheckedAndLeftOut()
    {
        var pdf = Simple(1, title: null);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg ID 72 600 200 100 re f EI"u8.ToArray()));
        var original = pdf.Build();
        using (var doc = PdfDocument.Open(original))
            Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A1b), v => v.Id == PdfARules.KnownOperators.Id(PdfAConformance.A1b));
        var result = Write(original);
        using var written = Same(original, result);
        var content = Encoding.Latin1.GetString(written.GetStreamData((PdfStream)written.Pages[0].Dictionary.Get(PdfName.Contents)!));
        Assert.DoesNotContain("ID", content.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("EI", content.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// The action of an outline item and of a link without a type (/S lost): an action of no allowed type — the check says so, the copy leaves it
    /// out (it does nothing); a structure element's /A (its attributes) is no action (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    [Fact]
    public void ActionsWithoutType_CheckedAndLeftOut()
    {
        var pdf = Simple(1, title: null);
        var outlines = pdf.Add(new PdfDictionary());
        var item = pdf.Add(Dict(("Title", new PdfString("Kapitel"u8.ToArray())), ("Parent", Ref(outlines)), ("A", Dict(("URI", new PdfString("https://example.com/"u8.ToArray())))),
            ("Dest", new PdfArray([Ref(5), N("Fit")]))));
        pdf.Replace(outlines, Dict(("Type", N("Outlines")), ("First", Ref(item)), ("Last", Ref(item)), ("Count", PdfInteger.Of(1))));
        var link = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Link")), ("Rect", Box(72, 700, 200, 720)), ("F", PdfInteger.Of(4)), ("Border", Box(0, 0, 0)),
            ("A", new PdfDictionary())));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([Ref(link)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("Outlines"), Ref(outlines));
        var original = pdf.Build();
        using (var doc = PdfDocument.Open(original))
            Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A2b), v => v.Id == PdfARules.Actions.Id(PdfAConformance.A2b));
        var result = Write(original);
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
        using var written = PdfDocument.Open(result.Bytes);
        var first = written.Catalog!.GetDictionary(PdfName.Get("Outlines"))!.GetDictionary(PdfName.Get("First"))!;
        Assert.False(first.ContainsKey(PdfName.Get("A")));
        Assert.NotNull(first.Get(PdfName.Get("Dest")));   // the destination stays
    }

    /// <summary>
    /// A link whose normal appearance refers to an object that is not there (a broken reference): no appearance — the check says so, the copy
    /// leaves the appearance out, the link stays (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A1b)]
    [InlineData(PdfAConformance.A2b)]
    public void LinkAppearanceReferringToNothing_CheckedAndLeftOut(PdfAConformance part)
    {
        var pdf = Simple(1, title: null);
        var missing = pdf.Add(PdfNull.Instance);
        var link = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Link")), ("Rect", Box(72, 700, 200, 720)), ("F", PdfInteger.Of(4)), ("Border", Box(0, 0, 0)),
            ("A", Dict(("S", N("URI")), ("URI", new PdfString("https://example.com/"u8.ToArray())))), ("AP", Dict(("N", Ref(missing)), ("D", Ref(missing))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([Ref(link)]));
        var original = pdf.Build();
        using (var doc = PdfDocument.Open(original))
            Assert.Contains(PdfACheck.Check(doc, part), v => v.Id == PdfARules.AppearanceStream.Id(part));
        using var source = PdfDocument.Open(original);
        var result = PdfAExport.Write(source, PageItem.From(source), part);
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
        using var written = PdfDocument.Open(result.Bytes);
        var kept = (PdfDictionary)written.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!;
        Assert.Equal("Link", kept.GetName(PdfName.Subtype)?.Value);
        Assert.False(kept.ContainsKey(PdfName.Get("AP")));
    }

    /// <summary>
    /// A font resource of the wrong kind (an empty string where the font should be): no resource — the check says so, and the copy is the one of
    /// a page whose font is missing altogether, drawn as the display draws it: the substitute's widths written exactly (docs/FEHLERGEDAECHTNIS.md,
    /// No. 340).
    /// </summary>
    [Fact]
    public void FontResourceOfTheWrongKind_TreatedAsMissing()
    {
        byte[] Page(PdfObject? font)
        {
            var pdf = Simple(1, title: null);
            ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, font is null ? Dict(("Font", Dict())) : Dict(("Font", Dict(("F1", font)))));
            pdf.Replace(4, new PdfStream(new PdfDictionary(), "BT /F1 16 Tf 50 700 Td (A = 0.05) Tj ET"u8.ToArray()));
            return pdf.Build();
        }
        var original = Page(new PdfString([]));
        using (var doc = PdfDocument.Open(original))
            Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A4), v => v.Id == PdfARules.ResourcesDefined.Id(PdfAConformance.A4));
        var copy = Write(original);
        using var _ = Same(original, copy);
        var missing = Write(Page(null));
        using (var a = PdfDocument.Open(copy.Bytes))
        using (var b = PdfDocument.Open(missing.Bytes))
        {
            using var drawn = ContentImageTests.Render(a, a.Pages[0]);
            using var drawnMissing = ContentImageTests.Render(b, b.Pages[0]);
            Assert.True(drawn.Bytes.AsSpan().SequenceEqual(drawnMissing.Bytes), "die Seite sieht anders aus als mit fehlender Schrift");
            Assert.Equal("A = 0.05", Text(a).Trim());
        }
        using var source = PdfDocument.Open(original);
        var four = PdfAExport.Write(source, PageItem.From(source), PdfAConformance.A4);
        Assert.True(four.Conforms, string.Join("\n", four.Violations.Select(v => $"{v.Rule} {v.Text}")));
    }

    [Fact]
    public void Jpeg2000Codestream_WrappedInJp2_Unchanged()
    {
        // a bare codestream of one grey component (only its header matters for the wrapper)
        var codestream = new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0, 41, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 7, 1, 1 };
        var wrapped = PdfAJpeg2000.Wrap(codestream)!;
        Assert.Null(PdfAJpeg2000.Fault(wrapped));
        Assert.True(wrapped.AsSpan(wrapped.Length - codestream.Length).SequenceEqual(codestream));
        // three components of different depths cannot be wrapped as they are
        var mixed = (byte[])codestream.Clone();
        Array.Resize(ref mixed, mixed.Length + 6);
        mixed[41] = 3;
        mixed[42] = 7;
        mixed[45] = 15;
        mixed[48] = 7;
        Assert.Null(PdfAJpeg2000.Wrap(mixed));
    }

    [Fact]
    public void SmoothedInlineImage_AndFontNameInGbk_BroughtInLine()
    {
        var program = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var (pdf, page) = PdfACheckTests.Page("q 100 0 0 100 72 600 cm BI /W 2 /H 1 /CS /G /BPC 8 /I true ID \u0000\u00ff EI Q BT /F1 12 Tf 72 500 Td (A) Tj ET",
            new PdfDictionary());
        var file = pdf.Add(new PdfStream(Dict(("Length1", PdfInteger.Of(program.Length))), program));
        var gbk = PdfName.Get(Encoding.Latin1.GetString([0xCB, 0xCE, 0xCC, 0xE5]));
        var descriptor = pdf.Add(Dict(("Type", N("FontDescriptor")), ("FontName", gbk), ("Flags", PdfInteger.Of(32)), ("FontFile2", Ref(file))));
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("TrueType")), ("BaseFont", gbk), ("FirstChar", PdfInteger.Of(65)), ("LastChar", PdfInteger.Of(65)),
            ("Widths", Box(667)), ("Encoding", N("WinAnsiEncoding")), ("FontDescriptor", Ref(descriptor))));
        page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(font))))));
        var result = Write(pdf.Build());
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
        using var doc = PdfDocument.Open(result.Bytes);
        Assert.Equal("____", FirstFont(doc).GetName(PdfName.Get("BaseFont"))!.Value);
        Assert.DoesNotContain("/I true", Encoding.Latin1.GetString(doc.GetStreamData((PdfStream)doc.Pages[0].Dictionary.Get(PdfName.Contents))));
    }

    [Fact]
    public void FormDrawnOnTwoPagesWithOtherResources_GetsBoth()
    {
        var pdf = Simple(2);
        // one form without resources of its own, drawing /F1 on page 1 and /X1 on page 2 — each page gives only its own
        var form = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 600, 800))),
            "BT /F1 12 Tf 72 700 Td (A) Tj ET q 10 0 0 10 72 600 cm /X1 Do Q"u8.ToArray()));
        var image = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(1)), ("Height", PdfInteger.Of(1)),
            ("ColorSpace", N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8))), [0]));
        var pages = new[] { (PdfDictionary)pdf.Get(5), (PdfDictionary)pdf.Get(7) };
        pages[0].Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(3)))), ("XObject", Dict(("Fm", Ref(form))))));
        pages[1].Set(PdfName.Resources, Dict(("XObject", Dict(("Fm", Ref(form)), ("X1", Ref(image))))));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "/Fm Do"u8.ToArray()));
        pdf.Replace(6, new PdfStream(new PdfDictionary(), "/Fm Do"u8.ToArray()));
        var result = Write(pdf.Build());
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
    }

    [Fact]
    public void EncodingCMapInTheFormOfAToUnicodeMap_WrittenAsCidCMap()
    {
        // the CMap maps codes with bfchar (a /ToUnicode form): the display takes them as CIDs, strict readers find none
        var program = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var (pdf, page) = PdfACheckTests.Page("BT /F1 20 Tf 72 700 Td <00410042> Tj ET", new PdfDictionary());
        var file = pdf.Add(new PdfStream(new PdfDictionary(), program));
        var descriptor = pdf.Add(Dict(("Type", N("FontDescriptor")), ("FontName", N("LiberationSans")), ("Flags", PdfInteger.Of(32)), ("FontFile2", Ref(file)),
            ("FontBBox", Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80))));
        var identity = Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())), ("Supplement", PdfInteger.Of(0)));
        var cid = Dict(("Type", N("Font")), ("Subtype", N("CIDFontType2")), ("BaseFont", N("LiberationSans")), ("FontDescriptor", Ref(descriptor)),
            ("CIDSystemInfo", identity), ("CIDToGIDMap", N("Identity")), ("DW", PdfInteger.Of(667)));
        var cmap = pdf.Add(new PdfStream(Dict(("Type", N("CMap")), ("CMapName", N("Seltsam")), ("CIDSystemInfo", identity)), Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Seltsam def 1 begincodespacerange <0000> <FFFF> endcodespacerange " +
            "2 beginbfchar <0041> <0024> <0042> <0025> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end")));
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type0")), ("BaseFont", N("LiberationSans")), ("Encoding", Ref(cmap)),
            ("DescendantFonts", new PdfArray([cid]))));
        page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(font))))));
        var original = pdf.Build();
        Assert.Contains(PdfACheck.Check(PdfDocument.Open(original)), v => v.Rule == "6.2.11.3.3");
        using var doc = Same(original, Write(original));
        var written = (PdfStream)FirstFont(doc).Get(PdfName.Get("Encoding"));
        var text = Encoding.ASCII.GetString(doc.GetStreamData(written));
        Assert.Contains("<0041> 36", text);
        Assert.Contains("begincidchar", text);
    }

    [Fact]
    public void FontNotEmbedded_EmbeddedAsTheSubstitute_StretchedAsDrawn()
    {
        // a font with no bundled equivalent, its „A“ wider than the substitute's: the display stretches the substitute
        var (pdf, _) = PdfACheckTests.Page("BT /F1 24 Tf 72 700 Td (AAB) Tj ET", Dict(("Font", Dict(("F1", Ref(3))))));
        pdf.Replace(3, Dict(("Type", N("Font")), ("Subtype", N("TrueType")), ("BaseFont", N("Erfunden")), ("Encoding", N("WinAnsiEncoding")),
            ("FirstChar", PdfInteger.Of(65)), ("LastChar", PdfInteger.Of(66)), ("Widths", Box(800, 667))));
        var original = pdf.Build();
        var copy = Write(original);
        Assert.Contains("Erfunden", copy.SubstitutedFonts);
        using var doc = Same(original, copy);
        Assert.Equal("Type1C", FirstFont(doc).GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile3"))!.Dictionary.GetName(PdfName.Subtype)?.Value);
    }

    [Fact]
    public void CompositeFontNotEmbedded_EmbeddedAsCidCff()
    {
        var (pdf, page) = PdfACheckTests.Page("BT /F1 24 Tf 72 700 Td <00010002> Tj ET", new PdfDictionary());
        var toUnicode = pdf.Add(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /U def 1 begincodespacerange <0000> <FFFF> endcodespacerange " +
            "2 beginbfchar <0001> <0041> <0002> <0042> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end")));
        var descriptor = pdf.Add(Dict(("Type", N("FontDescriptor")), ("FontName", N("Erfunden")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80))));
        var cid = Dict(("Type", N("Font")), ("Subtype", N("CIDFontType2")), ("BaseFont", N("Erfunden")), ("FontDescriptor", Ref(descriptor)),
            ("CIDSystemInfo", Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())), ("Supplement", PdfInteger.Of(0)))),
            ("DW", PdfInteger.Of(667)));
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type0")), ("BaseFont", N("Erfunden")), ("Encoding", N("Identity-H")),
            ("DescendantFonts", new PdfArray([cid])), ("ToUnicode", Ref(toUnicode))));
        page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(font))))));
        var original = pdf.Build();
        var copy = Write(original);
        Assert.Contains("Erfunden", copy.SubstitutedFonts);
        using var doc = Same(original, copy);
        var written = (PdfDictionary)FirstFont(doc).GetArray(PdfName.Get("DescendantFonts"))!.Get(0);
        Assert.Equal("CIDFontType0", written.GetName(PdfName.Subtype)?.Value);
    }

    /// <summary>
    /// A string of a two-byte font that ends in the middle of a code: readers take that byte differently — the check says so, and the copy leaves
    /// it out and keeps what follows in place (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    [Fact]
    public void StringEndingInTheMiddleOfACode_CheckedAndLeftOut()
    {
        var program = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var (pdf, page) = PdfACheckTests.Page("BT /F1 20 Tf 72 700 Td [<002400> <0025>] TJ ET", new PdfDictionary());
        var toUnicode = pdf.Add(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /U def 1 begincodespacerange <0000> <FFFF> endcodespacerange " +
            "2 beginbfchar <0024> <0041> <0025> <0042> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end")));
        var file = pdf.Add(new PdfStream(new PdfDictionary(), program));
        var descriptor = pdf.Add(Dict(("Type", N("FontDescriptor")), ("FontName", N("LiberationSans")), ("Flags", PdfInteger.Of(32)), ("FontFile2", Ref(file))));
        var cid = Dict(("Type", N("Font")), ("Subtype", N("CIDFontType2")), ("BaseFont", N("LiberationSans")), ("FontDescriptor", Ref(descriptor)),
            ("CIDSystemInfo", Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())), ("Supplement", PdfInteger.Of(0)))),
            ("CIDToGIDMap", N("Identity")), ("DW", PdfInteger.Of(667)));
        var font = Dict(("Type", N("Font")), ("Subtype", N("Type0")), ("BaseFont", N("LiberationSans")), ("Encoding", N("Identity-H")),
            ("DescendantFonts", new PdfArray([cid])), ("ToUnicode", Ref(toUnicode)));
        page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(pdf.Add(font)))))));
        var original = pdf.Build();
        using var before = PdfDocument.Open(original);
        Assert.Contains(PdfACheck.Check(before, PdfAConformance.A2b), v => v.Text.Contains("Kodierung nicht kennt"));
        var copy = Write(original);
        Assert.True(copy.Conforms, string.Join("\n", copy.Violations.Select(v => $"{v.Rule} {v.Text}")));
        using var after = PdfDocument.Open(copy.Bytes);
        var read = PageInterpreter.BuildText(before, before.Pages[0]);
        var written = PageInterpreter.BuildText(after, after.Pages[0]);
        Assert.Equal("B", written[^1].Text);
        Assert.Equal(read.Single(g => g.Text == "B").X0, written[^1].X0, 3);
    }

    /// <summary>
    /// A Type 3 font damaged where strict readers take it otherwise than the display: a procedure in a filter nobody knows, a d1 with a name among
    /// its numbers, a procedure that is no stream, /LastChar not matching /Widths, no procedures at all, a /Type that names no font — the check
    /// says so, and the copy conforms and looks the same (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    [Theory]
    [InlineData("filter", "6.2.11.5")]
    [InlineData("d1", "6.2.11.5")]
    [InlineData("keinstrom", "6.2.11.4.1")]
    [InlineData("breiten", "6.2.11.2")]
    [InlineData("ohneprozeduren", "6.2.11.4.1")]
    [InlineData("typ", "6.2.11.2")]
    public void DamagedType3Font_CheckedAndBroughtInLine(string damage, string rule)
    {
        var (pdf, page) = PdfACheckTests.Page("BT /F1 1 Tf 72 700 Td (ab) Tj ET", new PdfDictionary());
        var square = pdf.Add(new PdfStream(new PdfDictionary(), "1000 0 0 0 1000 1000 d1 0 0 1000 1000 re f"u8.ToArray()));
        PdfObject bar = damage switch
        {
            "filter" => Ref(pdf.Add(new PdfStream(Dict(("Filter", N("Unbekannt"))), "1000 0 0 0 1000 500 d1 0 0 1000 500 re f"u8.ToArray()))),
            "d1" => Ref(pdf.Add(new PdfStream(new PdfDictionary(), "1000 /X 0 0 1000 500 d1 0 0 1000 500 re f"u8.ToArray()))),
            "keinstrom" => Ref(pdf.Add(PdfNull.Instance)),
            _ => Ref(pdf.Add(new PdfStream(new PdfDictionary(), "1000 0 0 0 1000 500 d1 0 0 1000 500 re f"u8.ToArray()))),
        };
        var font = Dict(("Type", N(damage == "typ" ? "FontDescriptor" : "Font")), ("Subtype", N("Type3")), ("FontBBox", Box(0, 0, 1000, 1000)),
            ("FontMatrix", Box(0.02, 0, 0, 0.02, 0, 0)), ("Encoding", Dict(("Differences", new PdfArray([PdfInteger.Of(97), N("square"), N("bar")])))),
            ("FirstChar", PdfInteger.Of(97)), ("LastChar", PdfInteger.Of(damage == "breiten" ? 120 : 98)), ("Widths", Box(1000, 1000)));
        if (damage != "ohneprozeduren") font.Set(PdfName.Get("CharProcs"), Dict(("square", Ref(square)), ("bar", bar)));
        page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(pdf.Add(font)))))));
        var original = pdf.Build();
        using (var doc = PdfDocument.Open(original))
            Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A2b), v => v.Rule == rule);
        using var written = Same(original, Write(original));
        var copy = FirstFont(written);
        Assert.Equal("Font", copy.GetName(PdfName.Type)?.Value);
        var procs = copy.GetDictionary(PdfName.Get("CharProcs"))!;
        Assert.All(procs.Entries, e => Assert.IsType<PdfStream>(procs.Get(e.Key)));
        Assert.Equal(copy.GetInteger(PdfName.Get("LastChar")) - copy.GetInteger(PdfName.Get("FirstChar")) + 1, copy.GetArray(PdfName.Get("Widths"))!.Count);
    }

    /// <summary>
    /// An inline image whose data hold "EI" followed by white space: strict readers end the image there and read the rest as operators — the check
    /// says so, and the copy writes the data as hexadecimal text, which holds no "EI" (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    [Fact]
    public void InlineImageWithEiInItsData_WrittenAsHexadecimalText()
    {
        // four grey samples „E“, „I“, „ “, „x“: the display reads them by the size of the image
        var (pdf, _) = PdfACheckTests.Page("q 100 0 0 100 72 600 cm BI /W 4 /H 1 /CS /G /BPC 8 ID EI x EI Q", new PdfDictionary());
        var original = pdf.Build();
        using (var doc = PdfDocument.Open(original))
            Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A2b), v => v.Rule == "6.2.2");
        using var written = Same(original, Write(original));
        var content = Encoding.Latin1.GetString(written.GetStreamData((PdfStream)written.Pages[0].Dictionary.Get(PdfName.Contents)!));
        Assert.Contains("/AHx", content);
        Assert.Contains("45492078>", content);
    }

    /// <summary>
    /// A Type 3 font with the resources of its page, itself among their fonts: strict readers follow that circle without end. The copy leaves the
    /// fonts no procedure chooses out of the font's resources and looks the same (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    [Fact]
    public void Type3FontAmongItsOwnResources_CircleLeftOut()
    {
        var (pdf, page) = PdfACheckTests.Page("BT /F1 20 Tf 72 700 Td (ab) Tj ET", new PdfDictionary());
        var square = pdf.Add(new PdfStream(new PdfDictionary(), "1000 0 0 0 1000 1000 d1 0 0 800 800 re f"u8.ToArray()));
        var resources = pdf.Add(new PdfDictionary());
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type3")), ("FontBBox", Box(0, 0, 1000, 1000)), ("FontMatrix", Box(0.001, 0, 0, 0.001, 0, 0)),
            ("CharProcs", Dict(("a", Ref(square)), ("b", Ref(square)))), ("Encoding", Dict(("Differences", new PdfArray([PdfInteger.Of(97), N("a"), N("b")])))),
            ("FirstChar", PdfInteger.Of(97)), ("LastChar", PdfInteger.Of(98)), ("Widths", Box(1000, 1000)), ("Resources", Ref(resources))));
        pdf.Replace(resources, Dict(("Font", Dict(("F1", Ref(font))))));
        page.Set(PdfName.Resources, Ref(resources));
        var original = pdf.Build();
        using var written = Same(original, Write(original));
        var copied = FirstFont(written);
        Assert.Equal(0, copied.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Font"))?.Count ?? 0);
    }

    /// <summary>
    /// Fields whose appearance the writer draws itself (their /N refers into the new file): they keep it — only an /N that refers to nothing goes
    /// (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    [TestPdfsTheory]
    [InlineData("annotation-tx.pdf")]
    [InlineData("crypt_filters.pdf")]
    public void FieldAppearanceTheWriterDraws_Kept(string name)
    {
        using var doc = PdfDocument.Open(Path.Combine(Root(), "TestPDFs", "pdfjs", name));
        var result = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b);
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        return "";
    }
}
