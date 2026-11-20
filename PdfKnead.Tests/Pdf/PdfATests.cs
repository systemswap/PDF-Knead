using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// PDF/A (KONZEPT.md, section 5.21): what the writer brings in line, what it reports instead, the own check against the veraPDF test files,
/// PDF/A-3 with attachments, and the own sRGB profile.
/// </summary>
public sealed class PdfATests
{
    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    /// <summary>LZW with literal codes only (9 bits each between a clear code and the end code) — valid, if not small.</summary>
    private static byte[] Lzw(byte[] data)
    {
        var bits = new List<bool>();
        void Code(int code)
        {
            for (var b = 8; b >= 0; b--) bits.Add(((code >> b) & 1) != 0);
        }
        Code(256);
        foreach (var d in data) Code(d);
        Code(257);
        var result = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i]) result[i / 8] |= (byte)(0x80 >> (i % 8));
        return result;
    }

    /// <summary>
    /// A page with what PDF/A does not allow but the writer can bring in line: DeviceCMYK, an LZW content stream, an image to be smoothed, a
    /// transfer function and an unknown rendering intent, a script on opening, a hidden note, a film, a link running a script, a square without
    /// appearance, a check box with one appearance, layers without names. Text only invisible (Tr 3), so the font need not be embedded.
    /// </summary>
    internal static byte[] Prepared()
    {
        var pdf = Simple(1);
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        var page = (PdfDictionary)pdf.Get(5);
        var image = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(1)),
            ("ColorSpace", N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)), ("Interpolate", PdfBoolean.True)), [255, 0, 0, 0, 0, 255]));
        var mask = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 600, 850)),
            ("Group", Dict(("S", N("Transparency")), ("CS", N("DeviceGray"))))), "0.5 g 0 0 600 850 re f"u8.ToArray()));
        var content = "0 0.5 1 0 k 72 600 200 100 re f 1 0 0 rg 72 450 200 100 re f /GS0 gs /GS1 gs 72 300 100 50 re f q 100 0 0 50 300 300 cm /Im1 Do Q " +
                      "BT 3 Tr /F1 12 Tf 72 200 Td (unsichtbar) Tj ET";
        pdf.Replace(4, new PdfStream(Dict(("Filter", N("LZWDecode"))), Lzw(Encoding.ASCII.GetBytes(content))));
        var gs0 = Dict(("TR", N("Identity")), ("RI", N("Erfunden")), ("BM", N("Erfunden")));
        var gs1 = Dict(("SMask", Dict(("S", N("Luminosity")), ("G", Ref(mask)), ("TR", N("Identity")))));
        page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(3)))), ("XObject", Dict(("Im1", Ref(image)))),
            ("ExtGState", Dict(("GS0", gs0), ("GS1", gs1)))));
        page.Set(PdfName.MediaBox, Box(0, 0, 595, 842));

        var checkLook = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 12, 12))), "0 0 0 RG 1 1 10 10 re S"u8.ToArray()));
        var square = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Square")), ("Rect", Box(300, 600, 400, 700)), ("C", Box(1, 0, 0)), ("P", Ref(5))));
        var hidden = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Text")), ("Rect", Box(50, 50, 70, 70)), ("F", PdfInteger.Of(2)), ("Contents", S("verborgen"))));
        var film = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Screen")), ("Rect", Box(400, 50, 500, 150)), ("F", PdfInteger.Of(4))));
        var link = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Link")), ("Rect", Box(72, 100, 200, 120)), ("F", PdfInteger.Of(4)),
            ("A", Dict(("S", N("JavaScript")), ("JS", S("app.alert(1)"))))));
        var check = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Widget")), ("FT", N("Btn")), ("T", S("Haken")), ("Rect", Box(72, 50, 84, 62)),
            ("F", PdfInteger.Of(4)), ("AP", Dict(("N", Ref(checkLook)), ("D", Ref(checkLook))))));
        page.Set(PdfName.Get("Annots"), new PdfArray([Ref(square), Ref(hidden), Ref(film), Ref(link), Ref(check)]));
        catalog.Set(PdfName.Get("AcroForm"), Dict(("Fields", new PdfArray([Ref(check)])), ("NeedAppearances", PdfBoolean.False)));
        catalog.Set(PdfName.Get("OpenAction"), Dict(("S", N("JavaScript")), ("JS", S("app.alert(2)"))));
        catalog.Set(PdfName.Get("AA"), Dict(("WC", Dict(("S", N("JavaScript")), ("JS", S("x"))))));
        var layer = pdf.Add(Dict(("Type", N("OCG")), ("Name", S("Ebene"))));
        catalog.Set(PdfName.Get("OCProperties"), Dict(("OCGs", new PdfArray([Ref(layer)])),
            ("D", Dict(("Order", new PdfArray([Ref(layer)])), ("AS", new PdfArray())))));
        return pdf.Build();
    }

    internal static PdfAResult Write(byte[] data, PdfAConformance part = PdfAConformance.A2b)
    {
        using var doc = PdfDocument.Open(data);
        return PdfAExport.Write(doc, PageItem.From(doc), part);
    }

    internal static string Describe(IEnumerable<PdfAViolation> violations) => string.Join("\n", violations.Select(v => $"{v.Id} {v.Text}"));

    [Fact]
    public void Prepared_IsBroughtInLine_AndConforms()
    {
        var result = Write(Prepared());
        Assert.True(result.Conforms, Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        Assert.Equal("1.7", doc.HeaderVersion);
        Assert.False(doc.IsEncrypted);
        var catalog = doc.Catalog!;
        var intent = Assert.IsType<PdfDictionary>(Assert.Single(catalog.GetArray(PdfName.Get("OutputIntents"))!.Select((_, i) => catalog.GetArray(PdfName.Get("OutputIntents"))!.Get(i))));
        Assert.Equal("GTS_PDFA1", intent.GetName(PdfName.Get("S"))?.Value);
        // the page draws with DeviceCMYK: the bundled CMYK profile is the output intent (DeviceRGB gets an sRGB default space)
        Assert.Equal(CmykProfile.Name, intent.GetString(PdfName.Get("Info"))!.ToText());
        Assert.Equal(4, ((PdfStream)intent.Get(PdfName.Get("DestOutputProfile"))).Dictionary.GetInteger(PdfName.N));
        var xmp = Encoding.UTF8.GetString(doc.GetStreamData((PdfStream)catalog.Get(PdfName.Metadata)));
        Assert.Contains("<pdfaid:part>2</pdfaid:part>", xmp);
        Assert.Contains("<rdf:li xml:lang=\"x-default\">Testdokument</rdf:li>", xmp);
        Assert.False(catalog.ContainsKey(PdfName.Get("OpenAction")));
        Assert.False(catalog.ContainsKey(PdfName.Get("AA")));
        Assert.Equal("Standard", catalog.GetDictionary(PdfName.Get("OCProperties"))!.GetDictionary(PdfName.Get("D"))!.GetString(PdfName.Get("Name"))!.ToText());

        var page = doc.Pages[0];
        // DeviceCMYK stands under the CMYK intent, DeviceRGB gets a default space
        Assert.False(page.Resources!.GetDictionary(PdfName.Get("ColorSpace"))!.ContainsKey(PdfName.Get("DefaultCMYK")));
        Assert.True(page.Resources.GetDictionary(PdfName.Get("ColorSpace"))!.ContainsKey(PdfName.Get("DefaultRGB")));
        var image = (PdfStream)page.Resources.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im1"));
        Assert.False(image.Dictionary.ContainsKey(PdfName.Get("Interpolate")));
        var states = page.Resources.GetDictionary(PdfName.Get("ExtGState"))!;
        var gs0 = states.GetDictionary(PdfName.Get("GS0"))!;
        Assert.False(gs0.ContainsKey(PdfName.Get("TR")));
        Assert.Equal("RelativeColorimetric", gs0.GetName(PdfName.Get("RI"))?.Value);
        Assert.Equal("Normal", gs0.GetName(PdfName.Get("BM"))?.Value);
        // the transfer function of a soft mask is something else, and allowed
        Assert.True(states.GetDictionary(PdfName.Get("GS1"))!.GetDictionary(PdfName.Get("SMask"))!.ContainsKey(PdfName.Get("TR")));
        Assert.DoesNotContain("LZW", Encoding.Latin1.GetString(result.Bytes));

        var annots = page.Dictionary.GetArray(PdfName.Get("Annots"))!;
        var kinds = Enumerable.Range(0, annots.Count).Select(i => (PdfDictionary)annots.Get(i)).ToList();
        Assert.Equal(["Square", "Link", "Widget"], kinds.Select(a => a.GetName(PdfName.Subtype)!.Value));
        Assert.All(kinds, a => Assert.Equal(4, a.GetInteger(PdfName.Get("F"))));
        Assert.IsType<PdfStream>(kinds[0].GetDictionary(PdfName.Get("AP"))!.Get(PdfName.Get("N")));
        Assert.False(kinds[1].ContainsKey(PdfName.Get("A")));
        var checkLook = kinds[2].GetDictionary(PdfName.Get("AP"))!;
        Assert.Equal(["N"], checkLook.Entries.Select(e => e.Key.Value));
        Assert.IsType<PdfDictionary>(checkLook.Get(PdfName.Get("N")));
        Assert.Equal("On", kinds[2].GetName(PdfName.Get("AS"))?.Value);
    }

    /// <summary>
    /// A standard font without /Widths, embedded for PDF/A: the copy's /Widths are the widths the display sets the codes with — the standard
    /// metrics in whole units (No. 884) —, so the text stands where it stood. Before, the embedded font took the substitute's exact widths
    /// (333.007813 for 333), and the text of the copy crept along each line: 55 files of test file a few pixels off (docs/FEHLERGEDAECHTNIS.md,
    /// No. 918).
    /// </summary>
    [Theory]
    [InlineData("Times-Roman")]
    [InlineData("Helvetica-Bold")]
    [InlineData("Courier")]
    public void StandardFontEmbedded_WidthsAsTheDisplaySetsThem(string baseFont)
    {
        const string text = "Waveform quality 0123 (fj)";
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            [4] = ("", $"BT /F1 14 Tf 72 700 Td ({text}) Tj ET"),
            [5] = $"<</Type/Font/Subtype/Type1/BaseFont/{baseFont}/Encoding/WinAnsiEncoding>>",
        });
        var result = Write(file);
        Assert.True(result.Conforms, Describe(result.Violations));
        using var source = PdfDocument.Open(file);
        var shown = Core.Pdf.Fonts.PdfFont.Get(source.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!, source);
        using var copy = PdfDocument.Open(result.Bytes);
        var font = copy.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
        Assert.NotNull(font.GetDictionary(PdfName.Get("FontDescriptor")));
        var first = (int)font.GetInteger(PdfName.Get("FirstChar"))!.Value;
        var widths = font.GetArray(PdfName.Get("Widths"))!;
        foreach (var c in text.Distinct())
        {
            var expected = shown.Glyph(c, 1).Width * 1000;
            Assert.Equal(Math.Round(expected), expected, 9);   // the standard metrics: whole units
            Assert.Equal(expected, widths.GetNumber(c - first)!.Value, 9);
        }
    }

    /// <summary>
    /// PDF/A-4 of a font with /Name that is not embedded: the copy embeds it — a standard font as the bundled one, a symbolic one as the
    /// substitute the display draws it with. Before, the keys of 2.0 were taken out first (the font's /Name, deprecated), the PDF/A step got
    /// another object than the one it had measured, and the font stayed without a program (6.2.10.4.1; docs/FEHLERGEDAECHTNIS.md, No. 922;
    /// runs of all ways to write, M5: test file „PDF 2.0 image with BPC“). PDF/A-2b was not affected (1.7 keeps /Name).
    /// </summary>
    [Theory]
    [InlineData("/Encoding/WinAnsiEncoding", "")]
    [InlineData("", "/FontDescriptor 6 0 R")]
    public void FontWithName_AsPdfA4_IsEmbedded(string encoding, string descriptor)
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            [4] = ("", "BT /F1 14 Tf 72 700 Td (Erika Musterfrau) Tj ET"),
            [5] = $"<</Type/Font/Subtype/Type1/Name/F1/BaseFont/Helvetica{encoding}{descriptor}>>",
            [6] = "<</Type/FontDescriptor/FontName/Helvetica/Flags 4/FontBBox[-166 -225 1000 931]/ItalicAngle 0/Ascent 718/Descent -207/CapHeight 718/StemV 88>>",
        });
        var result = Write(file, PdfAConformance.A4);
        Assert.True(result.Conforms, Describe(result.Violations));
        using var copy = PdfDocument.Open(result.Bytes);
        var font = copy.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
        var written = font.GetDictionary(PdfName.Get("FontDescriptor"))!;
        Assert.True(written.ContainsKey(PdfName.Get("FontFile2")) || written.ContainsKey(PdfName.Get("FontFile3")));
        Assert.False(font.ContainsKey(PdfName.Get("Name")));   // deprecated in 2.0, still taken out
    }

    [Fact]
    public void Check_NamesWhatTheOriginalLacks()
    {
        using var doc = PdfDocument.Open(Prepared());
        var rules = PdfACheck.Check(doc).Select(v => v.Rule).ToHashSet();
        foreach (var rule in (string[])["6.6.2.1", "6.2.4.3", "6.2.8", "6.1.7.2", "6.2.5", "6.2.10", "6.5.1", "6.5.2", "6.3.1", "6.3.2", "6.3.3", "6.9"])
            Assert.Contains(rule, rules);
    }

    private static string Text(byte[] data)
    {
        using var doc = PdfDocument.Open(data);
        return Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.BuildText(doc, doc.Pages[0]), 0).Text;
    }

    /// <summary>One line of text in a font of the file that is not embedded; <paramref name="wideG"/>: the file gives „G“ a width of its own.</summary>
    private static byte[] WithFont(string font, bool wideG = false, string text = "Grüße aus dem Archiv")
    {
        var pdf = Simple(1);
        var dict = Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N(font)));
        if (font != "ZapfDingbats") dict.Set(PdfName.Get("Encoding"), N("WinAnsiEncoding"));   // ZapfDingbats with its own (as form fields use it)
        if (wideG)
        {
            dict.Set(PdfName.Get("FirstChar"), PdfInteger.Of(71));
            dict.Set(PdfName.Get("LastChar"), PdfInteger.Of(71));
            dict.Set(PdfName.Get("Widths"), new PdfArray([PdfInteger.Of(900)]));
        }
        pdf.Replace(3, dict);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.Latin1.GetBytes($"BT /F1 14 Tf 72 700 Td ({text}) Tj ET")));
        return pdf.Build();
    }

    [Fact]
    public void StandardFont_EmbeddedAsTheLiberationFontTheDisplayDrawsItWith()
    {
        var original = WithFont("Helvetica-Bold");
        var result = Write(original);
        Assert.True(result.Conforms, Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var font = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
        Assert.Equal("TrueType", font.GetName(PdfName.Subtype)?.Value);
        Assert.EndsWith("+LiberationSans-Bold", font.GetName(PdfName.Get("BaseFont"))!.Value);
        Assert.Equal("WinAnsiEncoding", font.GetName(PdfName.Get("Encoding"))?.Value);
        var program = (PdfStream)font.GetDictionary(PdfName.Get("FontDescriptor"))!.Get(PdfName.Get("FontFile2"));
        // a subset: far smaller than the whole font
        var whole = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Bold.ttf").Length;
        Assert.True(doc.GetStreamData(program).Length < whole / 2, $"{doc.GetStreamData(program).Length} von {whole}");
        Assert.Equal(Text(original), Text(result.Bytes));
    }

    [Fact]
    public void ZapfDingbats_EmbeddedWithItsNames_TextReadsTheSame()
    {
        var original = WithFont("ZapfDingbats", text: "4n");
        var result = Write(original);
        Assert.True(result.Conforms, Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var font = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
        Assert.Equal("ZapfDingbats", font.GetName(PdfName.Get("BaseFont"))?.Value);
        Assert.IsType<PdfStream>(font.GetDictionary(PdfName.Get("FontDescriptor"))!.Get(PdfName.Get("FontFile3")));
        Assert.Equal(["a20", "a73"], font.GetDictionary(PdfName.Get("Encoding"))!.GetArray(PdfName.Get("Differences"))!.OfType<PdfName>().Select(n => n.Value));
        Assert.Equal(Text(original), Text(result.Bytes));
    }

    [Fact]
    public void FontWithoutABundledEquivalent_OrOtherWidths_EmbeddedAsTheSubstitute_AndNamed()
    {
        var calibri = Write(WithFont("Calibri"));
        Assert.True(calibri.Conforms, Describe(calibri.Violations));
        Assert.Equal(["Calibri"], calibri.SubstitutedFonts);
        // Helvetica, but the file sets „G“ (code 71) far wider: the bundled font would move the text — the substitute as the display stretches it
        var wide = Write(WithFont("Helvetica", wideG: true));
        Assert.True(wide.Conforms, Describe(wide.Violations));
        Assert.Equal(["Helvetica"], wide.SubstitutedFonts);
        // a standard font the bundled one matches is embedded as such, and is no substitute to name
        Assert.Empty(Write(WithFont("Helvetica")).SubstitutedFonts);
        // a font no text shows needs no embedding
        var unused = Simple(1);
        unused.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg 72 600 200 100 re f"u8.ToArray()));
        var plain = Write(unused.Build());
        Assert.True(plain.Conforms, Describe(plain.Violations));
        Assert.DoesNotContain("Liberation", Encoding.Latin1.GetString(plain.Bytes));
    }

    /// <summary>A file with only shapes and an attachment of the document (/Names /EmbeddedFiles).</summary>
    internal static byte[] WithAttachment() => WithAttachment("Rechnungsdaten"u8.ToArray(), "daten.xml");

    /// <summary>A one-page file with one attachment of the given data and name in /Names /EmbeddedFiles.</summary>
    internal static byte[] WithAttachment(byte[] data, string name)
    {
        var pdf = Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg 72 600 200 100 re f"u8.ToArray()));
        var file = pdf.Add(new PdfStream(Dict(("Type", N("EmbeddedFile"))), data));
        var spec = pdf.Add(Dict(("Type", N("Filespec")), ("F", S(name)), ("EF", Dict(("F", Ref(file))))));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("Names"),
            Dict(("EmbeddedFiles", Dict(("Names", new PdfArray([S(name), Ref(spec)]))))));
        return pdf.Build();
    }

    [Theory]
    [InlineData(PdfAConformance.A2b)]
    [InlineData(PdfAConformance.A4)]
    public void AttachmentThatIsPdfA_Stays(PdfAConformance part)
    {
        // PDF/A-2 and PDF/A-4 allow attachments that are PDF/A themselves (6.8; ISO 19005-4, 6.9): one the check confirms stays, as a PDF
        var attachment = Write(Simple(1).Build(), PdfAConformance.A2b);
        Assert.True(attachment.Conforms, Describe(attachment.Violations));
        Assert.True(PdfACheck.AllowedAttachment(attachment.Bytes, part));
        var copy = Write(WithAttachment(attachment.Bytes, "anlage.pdf"), part);
        Assert.True(copy.Conforms, Describe(copy.Violations));
        using var doc = PdfDocument.Open(copy.Bytes);
        var names = doc.Catalog!.GetDictionary(PdfName.Get("Names"))!.GetDictionary(PdfName.Get("EmbeddedFiles"))!.GetArray(PdfName.Get("Names"))!;
        var spec = Assert.IsType<PdfDictionary>(names.Get(1));
        var file = Assert.IsType<PdfStream>(spec.GetDictionary(PdfName.Get("EF"))!.Get(PdfName.Get("F")));
        Assert.Equal(attachment.Bytes, doc.GetStreamData(file));
        Assert.Equal("anlage.pdf", spec.GetString(PdfName.Get("UF"))?.ToText());
    }

    [Fact]
    public void PdfA3_KeepsAttachments_WithTheirRelationship_PdfA2_DropsThem()
    {
        var three = Write(WithAttachment(), PdfAConformance.A3b);
        Assert.True(three.Conforms, Describe(three.Violations));
        using (var doc = PdfDocument.Open(three.Bytes))
        {
            var spec = Assert.IsType<PdfDictionary>(doc.Catalog!.GetArray(PdfName.Get("AF"))!.Get(0));
            Assert.Equal("Unspecified", spec.GetName(PdfName.Get("AFRelationship"))?.Value);
            Assert.Equal("daten.xml", spec.GetString(PdfName.Get("UF"))!.ToText());
            var stream = (PdfStream)spec.GetDictionary(PdfName.Get("EF"))!.Get(PdfName.Get("F"));
            Assert.Equal("application/octet-stream", stream.Dictionary.GetName(PdfName.Subtype)?.Value);
            Assert.True(stream.Dictionary.GetDictionary(PdfName.Get("Params"))!.ContainsKey(PdfName.Get("ModDate")));
            Assert.Contains("<pdfaid:part>3</pdfaid:part>", Encoding.UTF8.GetString(doc.GetStreamData((PdfStream)doc.Catalog.Get(PdfName.Metadata))));
            Assert.Equal(PdfAConformance.A3b, PdfACheck.Declared(doc));
        }
        var two = Write(WithAttachment());
        Assert.True(two.Conforms, Describe(two.Violations));
        using var doc2 = PdfDocument.Open(two.Bytes);
        // a file that is not PDF/A leaves the list of attachments (6.8)
        Assert.Empty(doc2.Catalog!.GetDictionary(PdfName.Get("Names"))?.GetDictionary(PdfName.Get("EmbeddedFiles"))?.GetArray(PdfName.Get("Names")) ?? []);
    }

    /// <summary>
    /// A link into an attachment the copy drops (a GoToE to a child of the document) loses its action — otherwise it
    /// named a file the copy no longer has (ISO 32000-2, 12.6.4.4; docs/FEHLERGEDAECHTNIS.md, No. 369).
    /// </summary>
    [Fact]
    public void LinkIntoAnAttachmentThatGoes_LosesItsAction()
    {
        var pdf = Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg 72 600 200 100 re f"u8.ToArray()));
        var file = pdf.Add(new PdfStream(Dict(("Type", N("EmbeddedFile"))), "Rechnungsdaten"u8.ToArray()));
        var spec = pdf.Add(Dict(("Type", N("Filespec")), ("F", S("daten.xml")), ("EF", Dict(("F", Ref(file))))));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("Names"), Dict(("EmbeddedFiles", Dict(("Names", new PdfArray([S("daten.xml"), Ref(spec)]))))));
        var name = "daten.xml"u8.ToArray();
        var link = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Link")), ("Rect", Box(72, 700, 200, 720)), ("F", PdfInteger.Of(4)),
            ("A", Dict(("S", N("GoToE")), ("T", Dict(("R", N("C")), ("N", new PdfString(name))))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([Ref(link)]));
        var copy = Write(pdf.Build());
        Assert.True(copy.Conforms, Describe(copy.Violations));
        using var doc = PdfDocument.Open(copy.Bytes);
        var annot = (PdfDictionary)doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0);
        Assert.False(annot.ContainsKey(PdfName.Get("A")));
    }

    /// <summary>
    /// An attachment PDF/A-4 does not allow goes; its specification then also leaves the associated files (/AF) of the page — there a
    /// specification must carry its file (ISO 32000-2, 14.13.2; docs/FEHLERGEDAECHTNIS.md, No. 368).
    /// </summary>
    [Fact]
    public void PdfA4_AttachmentThatGoes_LeavesTheAssociatedFiles()
    {
        var pdf = Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg 72 600 200 100 re f"u8.ToArray()));
        var file = pdf.Add(new PdfStream(Dict(("Type", N("EmbeddedFile"))), "<daten/>"u8.ToArray()));
        var spec = pdf.Add(Dict(("Type", N("Filespec")), ("F", S("daten.xml")), ("UF", S("daten.xml")), ("EF", Dict(("F", Ref(file)))),
            ("AFRelationship", N("Source"))));
        // the source of a formula, associated with its structure element (as typesetting programs write it)
        var root = pdf.Add(Dict(("Type", N("StructTreeRoot"))));
        var element = pdf.Add(Dict(("Type", N("StructElem")), ("S", N("Formula")), ("P", Ref(root)), ("Pg", Ref(5)), ("AF", new PdfArray([Ref(spec)]))));
        ((PdfDictionary)pdf.Get(root)).Set(PdfName.Get("K"), Ref(element));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("StructTreeRoot"), Ref(root));
        var result = Write(pdf.Build(), PdfAConformance.A4);
        Assert.True(result.Conforms, Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var written = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.GetDictionary(PdfName.Get("K"))!;
        Assert.Equal("Formula", written.GetName(PdfName.Get("S"))?.Value);
        Assert.False(written.ContainsKey(PdfName.Get("AF")));
    }

    /// <summary>
    /// A PDF/A-4 copy whose catalogue keeps private data (/PieceInfo) has an information dictionary with nothing but the date of the last change:
    /// ISO 32000-2, 14.5 needs that date to tell whether the private data is current, and ISO 19005-4, 6.1.3 allows the dictionary for it
    /// (docs/FEHLERGEDAECHTNIS.md, No. 367).
    /// </summary>
    [Fact]
    public void PdfA4_WithPrivateData_HasTheDateOfTheLastChange()
    {
        var pdf = Simple(1, title: "Titel");
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg 72 600 200 100 re f"u8.ToArray()));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("PieceInfo"),
            Dict(("Programm", Dict(("LastModified", S("D:20260101000000Z")), ("Private", S("Daten"))))));
        var result = Write(pdf.Build(), PdfAConformance.A4);
        Assert.True(result.Conforms, Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        Assert.True(doc.Catalog!.ContainsKey(PdfName.Get("PieceInfo")));
        var info = Assert.IsType<PdfDictionary>(doc.Trailer.Get(PdfName.Info));
        Assert.Equal(["ModDate"], info.Entries.Select(e => e.Key.Value));

        // without private data no information dictionary, as before
        var plain = Simple(1, title: "Titel");
        plain.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg 72 600 200 100 re f"u8.ToArray()));
        using var without = PdfDocument.Open(Write(plain.Build(), PdfAConformance.A4).Bytes);
        Assert.False(without.Trailer.ContainsKey(PdfName.Info));
    }

    [Fact]
    public void Info_And_Xmp_SayTheSame_ControlCharactersLeftOut()
    {
        var pdf = Simple(1, title: "Titel\u0001 mit <Zeichen> & Umlauten äö");
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg 72 600 200 100 re f"u8.ToArray()));
        var result = Write(pdf.Build());
        Assert.True(result.Conforms, Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        Assert.Equal("Titel mit <Zeichen> & Umlauten äö", doc.Info.Title);
        var xmp = Encoding.UTF8.GetString(doc.GetStreamData((PdfStream)doc.Catalog!.Get(PdfName.Metadata)));
        Assert.Contains("Titel mit &lt;Zeichen&gt; &amp; Umlauten äö", xmp);
        Assert.Contains($"<pdf:Producer>{doc.Info.Producer}</pdf:Producer>", xmp);
        Assert.Contains($"<xmp:ModifyDate>{PdfAMetadata.XmpDate(doc.Info.Modified!.Value)}</xmp:ModifyDate>", xmp);
        Assert.DoesNotContain("encoding=", xmp);
    }

    [Fact]
    public void ShapesOnly_InRgb_NeedNoCmykProfile()
    {
        var pdf = Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg 72 600 200 100 re f"u8.ToArray()));
        var result = Write(pdf.Build());
        Assert.True(result.Conforms, Describe(result.Violations));
        // the output intent is sRGB: neither default space nor the large CMYK profile is written
        Assert.True(result.Bytes.Length < 30_000, result.Bytes.Length.ToString());
        using var doc = PdfDocument.Open(result.Bytes);
        Assert.Null(doc.Pages[0].Resources?.GetDictionary(PdfName.Get("ColorSpace")));
    }

    [Fact]
    public void Srgb_Profile_IsReadable_AndKeepsSrgbColours()
    {
        var profile = IccProfile.Parse(CalibratedProfile.Srgb());
        Assert.NotNull(profile);
        Assert.Equal((uint?)IccProfile.RgbSpace, PdfACheck.UsableProfile(CalibratedProfile.Srgb(), 4));
        var transform = new IccTransform(profile!, compensateBlack: false);
        Span<float> rgb = stackalloc float[3];
        foreach (var (r, g, b) in (ReadOnlySpan<(float, float, float)>)[(0.2f, 0.5f, 0.8f), (1, 1, 1), (0, 0, 0), (0.9f, 0.1f, 0.3f)])
        {
            transform.ToSrgb([r, g, b], rgb);
            Assert.Equal(r, rgb[0], 0.01f);
            Assert.Equal(g, rgb[1], 0.01f);
            Assert.Equal(b, rgb[2], 0.01f);
        }
    }

    /// <summary>
    /// Against the veraPDF test files for PDF/A-2b (in the repository): the check rejects none of the valid files. Which invalid ones it misses
    /// is measured with <c>pdf-knead pdfa-pruefen</c> (docs/PRUEFUNGEN.md, section 6p).
    /// </summary>
    [TestPdfsFact]
    public void VeraPdfFiles_NoValidFileRejected()
    {
        var folder = Path.Combine(Root(), "TestPDFs", "verapdf", "PDF_A-2b");
        if (!Directory.Exists(folder)) return;
        var rejected = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*pass*.pdf", SearchOption.AllDirectories))
        {
            using var doc = PdfDocument.Open(file);
            if (PdfACheck.Check(doc) is { Count: > 0 } found) rejected.Add($"{Path.GetFileName(file)}: {Describe(found)}");
        }
        Assert.True(rejected.Count == 0, string.Join("\n", rejected));
    }

    private static string Root([CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        return "";
    }
}
