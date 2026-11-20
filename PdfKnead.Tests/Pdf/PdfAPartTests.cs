using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The parts and levels besides PDF/A-2b (KONZEPT.md, section 5.21): level u (every character with Unicode), PDF/A-4 on PDF 2.0 without an
/// information dictionary, PDF/A-4f with attachments; the check reads the part a file declares.
/// </summary>
public sealed class PdfAPartTests
{
    private static string Xmp(PdfDocument doc) => Encoding.UTF8.GetString(doc.GetStreamData((PdfStream)doc.Catalog!.Get(PdfName.Metadata)));

    [Theory]
    [InlineData("2b", PdfAConformance.A2b)]
    [InlineData("PDF/A-3U", PdfAConformance.A3u)]
    [InlineData(" 4 ", PdfAConformance.A4)]
    [InlineData("4f", PdfAConformance.A4f)]
    [InlineData("4E", PdfAConformance.A4e)]
    [InlineData("1b", PdfAConformance.A1b)]
    [InlineData("1a", PdfAConformance.A1a)]
    [InlineData("2A", PdfAConformance.A2a)]
    [InlineData("1u", null)]
    [InlineData(null, null)]
    public void FromName_ShortNames(string? name, PdfAConformance? expected) => Assert.Equal(expected, PdfAParts.FromName(name));

    [Theory]
    [InlineData(PdfAConformance.A2u)]
    [InlineData(PdfAConformance.A3u)]
    [InlineData(PdfAConformance.A4)]
    public void TextAndPreparedFile_Conform_AndDeclareThePart(PdfAConformance part)
    {
        // visible text in a standard font that is not embedded, and the file with everything the writer brings in line
        foreach (var data in (byte[][])[Simple(2).Build(), PdfATests.Prepared()])
        {
            var result = PdfATests.Write(data, part);
            Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
            using var doc = PdfDocument.Open(result.Bytes);
            Assert.Equal(part, PdfACheck.Declared(doc));
        }
    }

    /// <summary>
    /// PDF/A-1b (ISO 19005-1): PDF 1.4 with a cross-reference table (6.1.4), the identification part 1 level B (6.7.11), the information
    /// dictionary repeated in the metadata, unpacked (6.7.2, 6.7.3) — a line end in the title as well (an XML reader would make a line feed
    /// of a carriage return written as it is).
    /// </summary>
    [Fact]
    public void PdfA1b_Pdf14_CrossReferenceTable_InfoAsInXmp()
    {
        var result = PdfATests.Write(Simple(1, title: "Bericht\r\nTeil 2").Build(), PdfAConformance.A1b);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        var text = Encoding.Latin1.GetString(result.Bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("\nxref\n0 ", text);
        Assert.DoesNotContain("/XRef", text);
        Assert.DoesNotContain("/ObjStm", text);
        using var doc = PdfDocument.Open(result.Bytes);
        var xmp = Xmp(doc);
        Assert.Contains("<pdfaid:part>1</pdfaid:part>", xmp);
        Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", xmp);
        Assert.Contains("Bericht&#xD;", xmp);
        Assert.False(((PdfStream)doc.Catalog!.Get(PdfName.Metadata)).Dictionary.ContainsKey(PdfName.Filter));
        Assert.Equal(PdfAConformance.A1b, PdfACheck.Declared(doc));
        // the file with everything the writer brings in line: in PDF/A-1 its translucent graphics state is flattened (an image over the page)
        var prepared = PdfATests.Write(PdfATests.Prepared(), PdfAConformance.A1b);
        Assert.True(prepared.Conforms, PdfATests.Describe(prepared.Violations));
        Assert.Equal([0], prepared.FlattenedPages);
    }

    [Fact]
    public void PdfA4_Pdf20_WithoutInformationDictionary_TheTitleInXmp()
    {
        var result = PdfATests.Write(Simple(1).Build(), PdfAConformance.A4);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        Assert.StartsWith("%PDF-2.0", Encoding.ASCII.GetString(result.Bytes, 0, 8));
        using var doc = PdfDocument.Open(result.Bytes);
        Assert.False(doc.Trailer.ContainsKey(PdfName.Info));
        var xmp = Xmp(doc);
        Assert.Contains("<pdfaid:part>4</pdfaid:part>", xmp);
        Assert.Contains("<pdfaid:rev>2020</pdfaid:rev>", xmp);
        Assert.DoesNotContain("pdfaid:conformance", xmp);
        Assert.Contains("Testdokument", xmp);
    }

    [Fact]
    public void PdfA4_InformationDictionaryAndOldVersion_AreReported()
    {
        // a file that declares PDF/A-4 but keeps an information dictionary, a PDF 1.7 header and a catalogue version 1.7
        var copy = PdfATests.Write(Simple(1).Build(), PdfAConformance.A4);
        using var four = PdfDocument.Open(copy.Bytes);
        var pdf = Simple(1);
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("Version"), N("1.7"));
        catalog.Set(PdfName.Metadata, Ref(pdf.Add(new PdfStream(Dict(("Type", N("Metadata")), ("Subtype", N("XML"))), four.GetStreamData((PdfStream)four.Catalog!.Get(PdfName.Metadata))))));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Equal(PdfAConformance.A4, PdfACheck.Declared(doc));
        var rules = PdfACheck.Check(doc).Select(v => v.Text).ToList();
        Assert.Contains(Tr.T("Die Datei beginnt nicht mit der Kennung einer PDF-Version 2.x."), rules);
        Assert.Contains(Tr.T("Die Datei hat ein Dokumentinfo-Wörterbuch (/Info), das PDF/A-4 nicht erlaubt."), rules);
        Assert.Contains(Tr.T("Der Katalog nennt eine PDF-Version vor 2.0 (/Version)."), rules);
    }

    /// <summary>A Type 3 font with a glyph name no list knows under a control code, shown visibly and invisibly: the display reads no text
    /// for code 1 (for a printable code it would read the code's character).</summary>
    private static byte[] UnknownGlyphNames()
    {
        var pdf = Simple(1);
        var square = pdf.Add(new PdfStream(new PdfDictionary(), "1000 0 0 0 1000 1000 d1 0 0 1000 1000 re f"u8.ToArray()));
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type3")), ("FontBBox", Box(0, 0, 1000, 1000)),
            ("FontMatrix", new PdfArray([new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0), new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0)])),
            ("CharProcs", Dict(("g1", Ref(square)), ("A", Ref(square)))), ("Encoding", Dict(("Differences", new PdfArray([PdfInteger.Of(1), N("g1"), PdfInteger.Of(65), N("A")])))),
            ("FirstChar", PdfInteger.Of(1)), ("LastChar", PdfInteger.Of(65)), ("Widths", new PdfArray([.. Enumerable.Repeat<PdfObject>(PdfInteger.Of(1000), 65)]))));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, Dict(("Font", Dict(("T3", Ref(font))))));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "BT /T3 12 Tf 72 700 Td <4101> Tj 3 Tr <01> Tj ET"u8.ToArray()));
        return pdf.Build();
    }

    [Fact]
    public void LevelU_EveryCodeMapsToUnicode_AnUnknownGlyphToThePrivateUseArea()
    {
        var result = PdfATests.Write(UnknownGlyphNames(), PdfAConformance.A2u);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var font = PdfFont.Get((PdfDictionary)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("T3")), doc);
        Assert.Equal("A", font.StrictText(65, 1));
        Assert.Equal("\uE001", font.StrictText(1, 1));

        // the same file as PDF/A-2b keeps code 1 without Unicode
        var plain = PdfATests.Write(UnknownGlyphNames());
        Assert.True(plain.Conforms, PdfATests.Describe(plain.Violations));
        using var two = PdfDocument.Open(plain.Bytes);
        var twoFont = PdfFont.Get((PdfDictionary)two.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("T3")), two);
        Assert.Null(twoFont.StrictText(1, 1));
    }

    [Fact]
    public void PdfAText_KeepsText_ReplacesWhatIsMissingOrForbidden()
    {
        // the forbidden values go, the rest of the text stays; with nothing left, only level u gets a value
        Assert.Equal("AB", PdfWriter.PdfAText("A\uFEFF\0B", 7, 1, false));
        Assert.Null(PdfWriter.PdfAText("\uFFFE", 7, 1, false));
        Assert.Null(PdfWriter.PdfAText(null, 7, 1, false));
        Assert.Equal("ä", PdfWriter.PdfAText("ä", 7, 1, true));
        Assert.Equal("\uE007", PdfWriter.PdfAText(null, 7, 1, true));
        Assert.Equal("\uE0FF", PdfWriter.PdfAText("\uFEFF", 255, 1, true));
        Assert.Equal(char.ConvertFromUtf32(0xF0000 + 0x1234), PdfWriter.PdfAText("", 0x1234, 2, true));
        // never one of plane 15's two non-characters
        Assert.NotEqual(char.ConvertFromUtf32(0xFFFFE), PdfWriter.PdfAText(null, 0xFFFE, 2, true));
        Assert.NotEqual(char.ConvertFromUtf32(0xFFFFF), PdfWriter.PdfAText(null, 0xFFFF, 2, true));
    }

    [Fact]
    public void PdfA4f_KeepsAttachments_WithoutOneItIsReported()
    {
        var with = PdfATests.Write(PdfATests.WithAttachment(), PdfAConformance.A4f);
        Assert.True(with.Conforms, PdfATests.Describe(with.Violations));
        using (var doc = PdfDocument.Open(with.Bytes))
        {
            Assert.NotNull(doc.Catalog!.GetDictionary(PdfName.Get("Names"))?.GetDictionary(PdfName.Get("EmbeddedFiles")));
            Assert.Contains("<pdfaid:conformance>F</pdfaid:conformance>", Xmp(doc));
            Assert.Equal(PdfAConformance.A4f, PdfACheck.Declared(doc));
        }
        var without = PdfATests.Write(Simple(1).Build(), PdfAConformance.A4f);
        Assert.False(without.Conforms);
        Assert.Contains(without.Violations, v => v.Text == Tr.T("PDF/A-4f verlangt mindestens einen Anhang (/EmbeddedFiles)."));

        // PDF/A-4 drops a file that is not PDF/A from the list of attachments
        var four = PdfATests.Write(PdfATests.WithAttachment(), PdfAConformance.A4);
        Assert.True(four.Conforms, PdfATests.Describe(four.Violations));
        using var fourDoc = PdfDocument.Open(four.Bytes);
        Assert.Empty(fourDoc.Catalog!.GetDictionary(PdfName.Get("Names"))?.GetDictionary(PdfName.Get("EmbeddedFiles"))?.GetArray(PdfName.Get("Names")) ?? []);
    }

    /// <summary>
    /// Parts with attachments (PDF/A-3b, -4e, -4f) of a file without one (docs/FEHLERGEDAECHTNIS.md, No. 688; found by the run of all ways to
    /// write): the catalogue has no /AF — before an empty array, which ISO 32000-2, Table 29 does not allow (one or more). With an attachment
    /// /AF names its file specification.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A3b)]
    [InlineData(PdfAConformance.A4e)]
    [InlineData(PdfAConformance.A4f)]
    public void PartWithAttachments_NoneThere_NoEmptyAF(PdfAConformance part)
    {
        var without = PdfATests.Write(Simple(1).Build(), part);
        using (var doc = PdfDocument.Open(without.Bytes)) Assert.Null(doc.Catalog!.Get(PdfName.Get("AF")) as PdfArray);
        var with = PdfATests.Write(PdfATests.WithAttachment(), part);
        using var withDoc = PdfDocument.Open(with.Bytes);
        var af = Assert.IsType<PdfArray>(withDoc.Catalog!.Get(PdfName.Get("AF")));
        Assert.Single(Enumerable.Range(0, af.Count), i => af.Get(i) is PdfDictionary spec && PdfACheck.IsFileSpec(spec));
    }

    /// <summary>
    /// A one-page file for engineering: a 3D annotation (data of the given kind, an appearance), a rich media annotation with a model as its
    /// asset (no appearance) and a link that switches a layer.
    /// </summary>
    internal static byte[] Engineering(string threeDKind = "U3D")
    {
        static PdfString Text(string s) => new(Encoding.ASCII.GetBytes(s));
        var pdf = Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 1 rg 72 600 200 100 re f"u8.ToArray()));
        var data = pdf.Add(new PdfStream(Dict(("Type", N("3D")), ("Subtype", N(threeDKind))), "Beispieldaten eines Modells"u8.ToArray()));
        var look = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 200, 150))), "0.5 g 0 0 200 150 re f"u8.ToArray()));
        var threeD = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("3D")), ("Rect", Box(72, 300, 272, 450)), ("F", PdfInteger.Of(4)), ("3DD", Ref(data)),
            ("AP", Dict(("N", Ref(look))))));
        var asset = pdf.Add(new PdfStream(Dict(("Type", N("EmbeddedFile"))), "Beispielmodell"u8.ToArray()));
        var spec = pdf.Add(Dict(("Type", N("Filespec")), ("F", Text("modell.prc")), ("UF", Text("modell.prc")), ("EF", Dict(("F", Ref(asset))))));
        var media = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("RichMedia")), ("Rect", Box(300, 300, 500, 450)), ("F", PdfInteger.Of(4)),
            ("RichMediaContent", Dict(("Assets", Dict(("Names", new PdfArray([Text("modell.prc"), Ref(spec)]))))))));
        var layer = pdf.Add(Dict(("Type", N("OCG")), ("Name", Text("Ebene"))));
        var link = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Link")), ("Rect", Box(72, 200, 172, 220)), ("F", PdfInteger.Of(4)), ("Border", Box(0, 0, 0)),
            ("A", Dict(("S", N("SetOCGState")), ("State", new PdfArray([N("Toggle"), Ref(layer)]))))));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("OCProperties"),
            Dict(("OCGs", new PdfArray([Ref(layer)])), ("D", Dict(("Name", Text("Ansicht")), ("Order", new PdfArray([Ref(layer)]))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([Ref(threeD), Ref(media), Ref(link)]));
        return pdf.Build();
    }

    private static List<string> Annotations(PdfDocument doc) =>
        doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots")) is { } annots
            ? [.. Enumerable.Range(0, annots.Count).Select(i => (annots.Get(i) as PdfDictionary)?.GetName(PdfName.Subtype)?.Value ?? "?")]
            : [];

    /// <summary>
    /// PDF/A-4e (ISO 19005-4, Annex B): 3D and rich media annotations stay (one without an appearance gets an empty one), so do the action of
    /// a layer and the model as an embedded file (with its MIME type and relationship); the identification names level E. PDF/A-4 takes them
    /// off the page (what they draw goes into the content), and 3D data of another kind than U3D or PRC is not allowed in PDF/A-4e either.
    /// </summary>
    [Fact]
    public void PdfA4e_Keeps3DRichMediaAndLayerActions()
    {
        var engineering = PdfATests.Write(Engineering(), PdfAConformance.A4e);
        Assert.True(engineering.Conforms, PdfATests.Describe(engineering.Violations));
        using (var doc = PdfDocument.Open(engineering.Bytes))
        {
            Assert.Equal(["3D", "RichMedia", "Link"], Annotations(doc));
            Assert.Contains("<pdfaid:conformance>E</pdfaid:conformance>", Xmp(doc));
            Assert.Equal(PdfAConformance.A4e, PdfACheck.Declared(doc));
            var annots = doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
            var media = (PdfDictionary)annots.Get(1);
            Assert.NotNull(media.GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")) as PdfStream);
            var spec = (PdfDictionary)media.GetDictionary(PdfName.Get("RichMediaContent"))!.GetDictionary(PdfName.Get("Assets"))!.GetArray(PdfName.Get("Names"))!.Get(1);
            Assert.NotNull(spec.GetName(PdfName.Get("AFRelationship")));
            Assert.NotNull(((PdfStream)spec.GetDictionary(PdfName.Get("EF"))!.Get(PdfName.Get("F"))).Dictionary.GetName(PdfName.Subtype));
            Assert.Equal("SetOCGState", ((PdfDictionary)annots.Get(2)).GetDictionary(PdfName.Get("A"))?.GetName(PdfName.Get("S"))?.Value);
        }

        var four = PdfATests.Write(Engineering(), PdfAConformance.A4);
        Assert.True(four.Conforms, PdfATests.Describe(four.Violations));
        using (var doc = PdfDocument.Open(four.Bytes))
        {
            Assert.DoesNotContain("3D", Annotations(doc));
            Assert.DoesNotContain("RichMedia", Annotations(doc));
        }

        var other = PdfATests.Write(Engineering("VRML"), PdfAConformance.A4e);
        Assert.True(other.Conforms, PdfATests.Describe(other.Violations));
        using (var doc = PdfDocument.Open(other.Bytes)) Assert.Equal(["RichMedia", "Link"], Annotations(doc));
    }

    /// <summary>
    /// Level a (ISO 19005-2, 6.7): an untagged page gets a structure from its text blocks — the large line a heading, the text a paragraph, the
    /// picture a figure with the text given for it, in reading order —, marked content in its content, /MarkInfo, the language; without an
    /// answer for the picture the copy is refused; as decoration the picture is an artifact.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A2a)]
    [InlineData(PdfAConformance.A1a)]
    public void LevelA_TagsAnUntaggedPage_AndAsksForThePicture(PdfAConformance part)
    {
        var pdf = Simple(1);
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("Encoding", N("WinAnsiEncoding"))));
        var image = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(2)),
            ("ColorSpace", N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8))), [200, 30, 30, 30, 200, 30, 30, 30, 200, 200, 200, 30]));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(
            "BT /T1 24 Tf 72 760 Td (Bericht zum Jahr) Tj ET BT /T1 11 Tf 72 720 Td (Dies ist der erste Absatz, der etwas erklaert.) Tj 0 -14 Td " +
            "(Er hat eine zweite Zeile.) Tj ET q 100 0 0 80 72 560 cm /Bild Do Q BT /T1 11 Tf 72 520 Td (Und ein Absatz unter dem Bild.) Tj ET")));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, Dict(("Font", Dict(("T1", Ref(font)))), ("XObject", Dict(("Bild", Ref(image))))));
        var data = pdf.Build();

        var unanswered = PdfATests.Write(data, part);
        Assert.Contains(unanswered.Violations, v => v.Text == Tr.T("Ein Bild braucht einen Alternativtext oder die Angabe „nur Schmuck“.") && v.Page == 1);

        using var original = PdfDocument.Open(data);
        var figure = Assert.Single(PdfATagging.Figures([(original, original.Pages[0])]));
        var result = PdfAExport.Write(original, Core.Pdf.Editing.PageItem.From(original), part,
            accessibility: new PdfAAccessibility { Language = "de", Figures = new Dictionary<string, string?> { [figure.Key] = "Drei farbige Felder" } });
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        Assert.Equal(part, PdfACheck.Declared(doc));
        Assert.True(doc.Catalog!.GetDictionary(PdfName.Get("MarkInfo"))?.GetBoolean(PdfName.Get("Marked")));
        Assert.Equal("de", doc.Catalog.GetString(PdfName.Get("Lang"))?.ToText());
        var kids = doc.Catalog.GetDictionary(PdfName.Get("StructTreeRoot"))!.GetDictionary(PdfName.Get("K"))!.GetArray(PdfName.Get("K"))!;
        var types = Enumerable.Range(0, kids.Count).Select(i => ((PdfDictionary)kids.Get(i)).GetName(PdfName.Get("S"))!.Value).ToList();
        Assert.Equal(["H1", "P", "Figure", "P"], types);
        Assert.Equal("Drei farbige Felder", ((PdfDictionary)kids.Get(2)).GetString(PdfName.Get("Alt"))?.ToText());
        Assert.Equal(0, doc.Pages[0].Dictionary.GetInteger(PdfName.Get("StructParents")));
        var content = Encoding.Latin1.GetString(doc.GetStreamData((PdfStream)doc.Pages[0].Dictionary.Get(PdfName.Contents)));
        Assert.Contains("/Figure <</MCID 2>> BDC", content);

        // as decoration: an artifact, no figure
        var plain = PdfAExport.Write(original, Core.Pdf.Editing.PageItem.From(original), part, accessibility: new PdfAAccessibility { AllDecorative = true });
        Assert.True(plain.Conforms, PdfATests.Describe(plain.Violations));
        using var plainDoc = PdfDocument.Open(plain.Bytes);
        Assert.Contains("/Artifact BMC", Encoding.Latin1.GetString(plainDoc.GetStreamData((PdfStream)plainDoc.Pages[0].Dictionary.Get(PdfName.Contents))));
        Assert.Equal("de", plainDoc.Catalog!.GetString(PdfName.Get("Lang"))?.ToText());
    }

    /// <summary>
    /// A standard type the source maps elsewhere (<c>/RoleMap &lt;&lt; /Table /Div &gt;&gt;</c>, a box holding paragraphs): parts 2 and 3 forbid the
    /// mapping, and before the entry was simply removed — the element became a table holding paragraphs. Now it is renamed and the new name maps
    /// to Div (ISO 19005-2, 6.7.3.4; ISO 32000-2, Annex L; docs/FEHLERGEDAECHTNIS.md, No. 502).
    /// </summary>
    [Fact]
    public void LevelA_RemappedStandardType_ElementRenamedNotReinterpreted()
    {
        var pdf = Simple(1);
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("Encoding", N("WinAnsiEncoding"))));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(
            "/P <</MCID 0>> BDC BT /T1 11 Tf 72 720 Td (Erster Absatz im Kasten.) Tj ET EMC /P <</MCID 1>> BDC BT /T1 11 Tf 72 700 Td (Zweiter Absatz.) Tj ET EMC")));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, Dict(("Font", Dict(("T1", Ref(font))))));
        page.Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        var root = pdf.Add(new PdfDictionary());
        var box = pdf.Add(new PdfDictionary());
        var first = pdf.Add(Dict(("Type", N("StructElem")), ("S", N("P")), ("P", Ref(box)), ("Pg", Ref(5)), ("K", PdfInteger.Of(0))));
        var second = pdf.Add(Dict(("Type", N("StructElem")), ("S", N("P")), ("P", Ref(box)), ("Pg", Ref(5)), ("K", PdfInteger.Of(1))));
        pdf.Replace(box, Dict(("Type", N("StructElem")), ("S", N("Table")), ("P", Ref(root)), ("K", new PdfArray([Ref(first), Ref(second)]))));
        var parents = pdf.Add(Dict(("Nums", new PdfArray([PdfInteger.Of(0), new PdfArray([Ref(first), Ref(second)])]))));
        pdf.Replace(root, Dict(("Type", N("StructTreeRoot")), ("K", Ref(box)), ("ParentTree", Ref(parents)), ("ParentTreeNextKey", PdfInteger.Of(1)),
            ("RoleMap", Dict(("Table", N("Div"))))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("StructTreeRoot"), Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), Dict(("Marked", PdfBoolean.True)));
        catalog.Set(PdfName.Get("Lang"), new PdfString("de"u8.ToArray()));

        var result = PdfATests.Write(pdf.Build(), PdfAConformance.A2a);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var tree = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        var roles = tree.GetDictionary(PdfName.Get("RoleMap"))!;
        Assert.DoesNotContain(roles.Entries, e => PdfACheck.StandardStructureType(e.Key.Value, 2));
        var element = tree.GetDictionary(PdfName.Get("K"))!;
        var type = element.GetName(PdfName.Get("S"))!.Value;
        Assert.NotEqual("Table", type);
        Assert.Equal("Div", roles.GetName(PdfName.Get(type))?.Value);
    }

    /// <summary>
    /// Own types that reach no standard type: before each was mapped by its name alone — a „Body“ holding paragraphs became P (a paragraph in a
    /// paragraph), a „TableRow“ and a „TableCell“ became Table (a table in a table), two „Heading“ under the document two H. Now the name gives
    /// candidates and the containment rules of the standard types decide (ISO/TS 32005, Table 5; docs/FEHLERGEDAECHTNIS.md, No. 507).
    /// </summary>
    [Fact]
    public void LevelA_UnmappedCustomTypes_MappedWithoutBreakingNesting()
    {
        var pdf = Simple(1);
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("Encoding", N("WinAnsiEncoding"))));
        var text = new StringBuilder();
        for (var i = 0; i < 5; i++) text.Append($"/Span <</MCID {i}>> BDC BT /T1 11 Tf 72 {720 - 20 * i} Td (Zeile {i}) Tj ET EMC ");
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(text.ToString())));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, Dict(("Font", Dict(("T1", Ref(font))))));
        page.Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        var root = pdf.Add(new PdfDictionary());
        var document = pdf.Add(new PdfDictionary());
        var body = pdf.Add(new PdfDictionary());
        var table = pdf.Add(new PdfDictionary());
        var row = pdf.Add(new PdfDictionary());
        int Leaf(string type, int parent, int mcid) =>
            pdf.Add(Dict(("Type", N("StructElem")), ("S", N(type)), ("P", Ref(parent)), ("Pg", Ref(5)), ("K", PdfInteger.Of(mcid))));
        var first = Leaf("P", body, 0);
        var second = Leaf("P", body, 1);
        var cell = Leaf("TableCell", row, 2);
        var heading = Leaf("Heading", document, 3);
        var heading2 = Leaf("Heading", document, 4);
        pdf.Replace(body, Dict(("Type", N("StructElem")), ("S", N("Body")), ("P", Ref(document)), ("K", new PdfArray([Ref(first), Ref(second)]))));
        pdf.Replace(row, Dict(("Type", N("StructElem")), ("S", N("TableRow")), ("P", Ref(table)), ("K", new PdfArray([Ref(cell)]))));
        pdf.Replace(table, Dict(("Type", N("StructElem")), ("S", N("Table")), ("P", Ref(document)), ("K", new PdfArray([Ref(row)]))));
        pdf.Replace(document, Dict(("Type", N("StructElem")), ("S", N("Document")), ("P", Ref(root)),
            ("K", new PdfArray([Ref(heading), Ref(body), Ref(heading2), Ref(table)]))));
        var parents = pdf.Add(Dict(("Nums", new PdfArray([PdfInteger.Of(0), new PdfArray([Ref(first), Ref(second), Ref(cell), Ref(heading), Ref(heading2)])]))));
        pdf.Replace(root, Dict(("Type", N("StructTreeRoot")), ("K", Ref(document)), ("ParentTree", Ref(parents)), ("ParentTreeNextKey", PdfInteger.Of(1))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("StructTreeRoot"), Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), Dict(("Marked", PdfBoolean.True)));
        catalog.Set(PdfName.Get("Lang"), new PdfString("de"u8.ToArray()));

        var result = PdfATests.Write(pdf.Build(), PdfAConformance.A2a);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var tree = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        var roles = tree.GetDictionary(PdfName.Get("RoleMap"))!;
        Assert.Equal("TR", roles.GetName(PdfName.Get("TableRow"))?.Value);
        Assert.Equal("TD", roles.GetName(PdfName.Get("TableCell"))?.Value);
        Assert.NotEqual("P", roles.GetName(PdfName.Get("Body"))?.Value);
        Assert.NotEqual("H", roles.GetName(PdfName.Get("Heading"))?.Value);
        Assert.Empty(Core.Pdf.Accessibility.Containment.Violations(tree));
    }

    /// <summary>
    /// A creation date of year 0000 (<c>D:00000000000000+00'00'</c>, a placeholder of some producers) is no date: the copy leaves it out
    /// instead of writing the first of January of year 1 into the information dictionary and XMP — the reference validator read the XMP date of
    /// year 1 in the Julian calendar and found the two unequal (ISO 19005-1, 6.7.3; docs/FEHLERGEDAECHTNIS.md, No. 508).
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A1b)]
    [InlineData(PdfAConformance.A2b)]
    public void PlaceholderDate_LeftOutNotWrittenAsYearOne(PdfAConformance part)
    {
        var pdf = Simple(1);
        pdf.InfoNumber = pdf.Add(Dict(("CreationDate", new PdfString("D:00000000000000+00'00'"u8.ToArray())),
            ("ModDate", new PdfString("D:20150314150605+08'00'"u8.ToArray()))));
        var result = PdfATests.Write(pdf.Build(), part);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var info = doc.Trailer.GetDictionary(PdfName.Info)!;
        Assert.False(info.ContainsKey(PdfName.Get("CreationDate")));
        Assert.True(info.ContainsKey(PdfName.Get("ModDate")));
        var xmp = Encoding.UTF8.GetString(doc.GetStreamData((PdfStream)doc.Catalog!.Get(PdfName.Metadata)!));
        Assert.DoesNotContain("CreateDate", xmp);
        Assert.DoesNotContain("0001-", xmp);
    }

    /// <summary>
    /// A page with CMYK and RGB colours whose /DefaultRGB is /DeviceRGB itself: the copy has a CMYK output intent, and the default does not
    /// make the RGB colours device-independent — before both the check and the writer took any /DefaultRGB as set, and the reference validator
    /// rejected the copy (ISO 19005-2, 6.2.4.3; docs/FEHLERGEDAECHTNIS.md, No. 509). Now the writer puts its own sRGB profile there.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A1b)]
    [InlineData(PdfAConformance.A2b)]
    public void DeviceDefaultSpace_ReplacedByAnIndependentOne(PdfAConformance part)
    {
        var pdf = Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("0 0 0 1 k 72 600 200 100 re f 1 0 0 rg 72 400 200 100 re f")));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, Dict(("ColorSpace", Dict(("DefaultRGB", N("DeviceRGB"))))));
        var result = PdfATests.Write(pdf.Build(), part);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var spaces = doc.Pages[0].Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("ColorSpace"))!;
        var space = Assert.IsType<PdfArray>(spaces.Get(PdfName.Get("DefaultRGB")));
        Assert.Equal("ICCBased", (space.Get(0) as PdfName)?.Value);
    }

    /// <summary>
    /// A graphics state whose /SMask refers to an object that does not exist: by ISO 32000-2, 7.3.10 no mask — the copy leaves the key out
    /// instead of writing the dangling reference, which a reference validator read as a mask (PDF/A-1, 6.4; docs/FEHLERGEDAECHTNIS.md, No. 511).
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A1b)]
    [InlineData(PdfAConformance.A2b)]
    public void SoftMaskToNothing_LeftOut(PdfAConformance part)
    {
        var pdf = Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("/GS1 gs 0 0 0 rg 72 600 200 100 re f")));
        var state = Dict(("Type", N("ExtGState")), ("SMask", new PdfReference(new PdfObjectId(999, 0))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, Dict(("ExtGState", Dict(("GS1", state)))));
        var result = PdfATests.Write(pdf.Build(), part);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var gs = doc.Pages[0].Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("ExtGState"))!.GetDictionary(PdfName.Get("GS1"))!;
        Assert.False(gs.ContainsKey(PdfName.Get("SMask")));
    }

    /// <summary>
    /// A structure in a circle (a paragraph with its own parent, the document element, as a child): the copy writes a tree — the child reached
    /// before is left out; a reference validator overflowed its stack on the copy, and every reader that follows /K runs in a circle
    /// (ISO 32000-2, 14.7.2; docs/FEHLERGEDAECHTNIS.md, No. 512).
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A2b)]
    [InlineData(PdfAConformance.A3b)]
    [InlineData(PdfAConformance.A2a)]
    public void StructureInACircle_WrittenAsATree(PdfAConformance part)
    {
        var pdf = Simple(1);
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("Encoding", N("WinAnsiEncoding"))));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("/P <</MCID 0>> BDC BT /T1 11 Tf 72 720 Td (Ein Absatz.) Tj ET EMC")));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, Dict(("Font", Dict(("T1", Ref(font))))));
        page.Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        var root = pdf.Add(new PdfDictionary());
        var document = pdf.Add(new PdfDictionary());
        var paragraph = pdf.Add(Dict(("Type", N("StructElem")), ("S", N("P")), ("P", Ref(document)), ("Pg", Ref(5)),
            ("K", new PdfArray([PdfInteger.Of(0), Ref(document)]))));
        pdf.Replace(document, Dict(("Type", N("StructElem")), ("S", N("Document")), ("P", Ref(root)), ("K", new PdfArray([Ref(paragraph)]))));
        var parents = pdf.Add(Dict(("Nums", new PdfArray([PdfInteger.Of(0), new PdfArray([Ref(paragraph)])]))));
        pdf.Replace(root, Dict(("Type", N("StructTreeRoot")), ("K", Ref(document)), ("ParentTree", Ref(parents)), ("ParentTreeNextKey", PdfInteger.Of(1))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("StructTreeRoot"), Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), Dict(("Marked", PdfBoolean.True)));
        catalog.Set(PdfName.Get("Lang"), new PdfString("de"u8.ToArray()));

        var result = PdfATests.Write(pdf.Build(), part);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var tree = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!;
        // every element reached once, the paragraph keeps its content
        var reached = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfObject?>([tree.Get(PdfName.Get("K"))]);
        var elements = 0;
        while (stack.TryPop(out var k) && elements < 100)
            switch (k)
            {
                case PdfArray array:
                    for (var i = 0; i < array.Count; i++) stack.Push(array.Get(i));
                    break;
                case PdfDictionary element when element.ContainsKey(PdfName.Get("S")):
                    Assert.True(reached.Add(element), "ein Element wird zweimal erreicht");
                    elements++;
                    stack.Push(element.Get(PdfName.Get("K")));
                    break;
            }
        Assert.Equal(2, elements);
    }

    /// <summary>
    /// A field tree in a circle — a widget its own /Parent, or a field its own child in /Kids (two signature test files of a public collection):
    /// the copy writes a tree, each widget with the field it hangs on as /Parent, the top field without one; a reference validator gave up on
    /// the copy with „loop in field tree“ (ISO 32000-2, 12.7.4; docs/FEHLERGEDAECHTNIS.md, No. 514).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FieldTreeInACircle_WrittenAsATree(bool ownChild)
    {
        var pdf = Simple(1);
        var field = pdf.Add(new PdfDictionary());
        var widget = pdf.Add(new PdfDictionary());
        pdf.Replace(widget, Dict(("Type", N("Annot")), ("Subtype", N("Widget")), ("Rect", new PdfArray([PdfInteger.Of(72), PdfInteger.Of(700), PdfInteger.Of(272), PdfInteger.Of(720)])),
            ("F", PdfInteger.Of(4)), ("P", Ref(5)), ("Parent", Ref(ownChild ? field : widget))));
        pdf.Replace(field, Dict(("FT", N("Tx")), ("T", new PdfString("Name"u8.ToArray())),
            ("Kids", new PdfArray(ownChild ? [Ref(field), Ref(widget)] : [Ref(widget)]))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([Ref(widget)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), Dict(("Fields", new PdfArray([Ref(field)]))));

        var result = PdfATests.Write(pdf.Build(), PdfAConformance.A2b);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var top = (PdfDictionary)doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!.Get(0)!;
        Assert.False(top.ContainsKey(PdfName.Get("Parent")));
        var kids = top.GetArray(PdfName.Get("Kids"))!;
        var only = (PdfDictionary)Assert.Single(Enumerable.Range(0, kids.Count).Select(kids.Get))!;
        Assert.NotSame(top, only);
        Assert.Same(top, only.GetDictionary(PdfName.Get("Parent")));
    }

    /// <summary>
    /// A Type 3 font with a null in its bounding box whose resources name the font itself — its own, or (a rendering test file of a public
    /// collection) none, so that the copy gave it those of the page it is drawn on: the copy writes the resources without the font and the box
    /// in numbers — a reference validator followed the circle until its stack overflowed (ISO 32000-2, 9.6.4 and Table 110;
    /// docs/FEHLERGEDAECHTNIS.md, No. 515).
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A2b, true)]
    [InlineData(PdfAConformance.A3b, true)]
    [InlineData(PdfAConformance.A3b, false)]
    public void Type3FontInItsOwnResources_LeftOut(PdfAConformance part, bool ownResources)
    {
        var pdf = Simple(1);
        var glyph = pdf.Add(new PdfStream(new PdfDictionary(), "500 0 0 0 500 700 d1 /G0 gs 0 0 500 700 re f"u8.ToArray()));
        var font = pdf.Add(new PdfDictionary());
        var resources = Dict(("Font", Dict(("F0", Ref(font)))), ("ExtGState", Dict(("G0", Dict(("Type", N("ExtGState")), ("LW", PdfInteger.Of(1)))))));
        pdf.Replace(font, Dict(("Type", N("Font")), ("Subtype", N("Type3")), ("FontMatrix", new PdfArray([new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0), new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0)])),
            ("FontBBox", new PdfArray([PdfInteger.Of(0), PdfNull.Instance, PdfInteger.Of(500), PdfInteger.Of(700)])), ("FirstChar", PdfInteger.Of(65)), ("LastChar", PdfInteger.Of(65)),
            ("Widths", new PdfArray([PdfInteger.Of(500)])), ("Encoding", Dict(("Type", N("Encoding")), ("Differences", new PdfArray([PdfInteger.Of(65), N("a")])))),
            ("CharProcs", Dict(("a", Ref(glyph))))));
        if (ownResources) ((PdfDictionary)pdf.Get(font)).Set(PdfName.Resources, resources);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "BT /F0 20 Tf 72 700 Td (A) Tj ET"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, resources);
        var result = PdfATests.Write(pdf.Build(), part);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var pageFonts = doc.Pages[0].Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!;
        var written = pageFonts.GetDictionary(PdfName.Get("F0"))!;
        var number = Assert.IsType<PdfReference>(pageFonts.GetRaw(PdfName.Get("F0"))).Id.Number;
        Assert.NotNull(written.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("ExtGState")));   // (what its glyphs use stays)
        var own = written.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Font"));
        Assert.True(own is null || own.Entries.All(e => own.GetRaw(e.Key) is not PdfReference r || r.Id.Number != number), "die Schrift nennt sich selbst");
        var box = written.GetArray(PdfName.Get("FontBBox"))!;
        Assert.All(Enumerable.Range(0, box.Count), i => Assert.True(box.Get(i) is PdfInteger or PdfReal));
    }

    /// <summary>
    /// A page draws an „XObject“ that is a metadata stream (a signature test file where a later revision put metadata in place of the image):
    /// it draws nothing, and the copy no longer names it — before the copy wrote the Do and a reference validator found the name undefined
    /// (ISO 32000-2, 8.8; ISO 19005-4, 6.2.2; docs/FEHLERGEDAECHTNIS.md, No. 516).
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A2b)]
    [InlineData(PdfAConformance.A4)]
    public void XObjectThatIsNone_NotDrawn(PdfAConformance part)
    {
        var pdf = Simple(1);
        var packet = "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF " +
                     "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        var metadata = pdf.Add(new PdfStream(Dict(("Type", N("Metadata")), ("Subtype", N("XML"))), Encoding.UTF8.GetBytes(packet)));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "0 0 0 rg 72 700 100 20 re f q 100 0 0 100 72 500 cm /I1 Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, Dict(("XObject", Dict(("I1", Ref(metadata))))));
        var result = PdfATests.Write(pdf.Build(), part);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var content = Encoding.Latin1.GetString(doc.GetStreamData((PdfStream)doc.Pages[0].Dictionary.Get(PdfName.Contents)!));
        Assert.DoesNotContain("/I1 Do", content);
        Assert.Contains("re", content);   // (the rest stays)
    }

    /// <summary>
    /// A Type 3 font without /CharProcs (a damaged copy of a public test file, found by fuzzing) whose resources name fonts: the copy is
    /// written — before the leaving out of unused fonts enumerated the missing procedures and the program stopped with an exception
    /// (docs/FEHLERGEDAECHTNIS.md, No. 522).
    /// </summary>
    [Fact]
    public void Type3FontWithoutProcedures_Written()
    {
        var pdf = Simple(1);
        var other = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica"))));
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type3")), ("FontMatrix", new PdfArray([new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0), new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0)])),
            ("FontBBox", new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(500), PdfInteger.Of(700)])), ("FirstChar", PdfInteger.Of(65)), ("LastChar", PdfInteger.Of(65)),
            ("Widths", new PdfArray([PdfInteger.Of(500)])), ("Encoding", Dict(("Type", N("Encoding")), ("Differences", new PdfArray([PdfInteger.Of(65), N("a")])))),
            ("Resources", Dict(("Font", Dict(("F1", Ref(other))))))));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "BT /F0 20 Tf 72 700 Td (A) Tj ET"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, Dict(("Font", Dict(("F0", Ref(font))))));
        var result = PdfATests.Write(pdf.Build(), PdfAConformance.A2b);
        Assert.NotEmpty(result.Bytes);
        foreach (var _ in default(PdfDictionary.EntryList)) Assert.Fail("ein leerer Eintragsbereich zählt nichts auf");
    }

    /// <summary>
    /// A CIDFont whose name holds a space, rebuilt for the copy (its new name without the space): the descriptor's /FontName is the new name
    /// too — before it kept the old one and differed from /BaseFont (ISO 32000-2, Table 120; docs/FEHLERGEDAECHTNIS.md, No. 525; found with
    /// the reference model of ISO 32000-2 on a public test file).
    /// </summary>
    [TestPdfsFact]
    public void RebuiltCidFont_DescriptorNamedAsTheFont()
    {
        static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
        var result = PdfATests.Write(File.ReadAllBytes(Path.Combine(Root(), "TestPDFs", "pdfjs", "bug2004951.pdf")), PdfAConformance.A4);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var checkedFonts = 0;
        for (var n = 1; n <= doc.HighestObjectNumber; n++)
        {
            if (!doc.HasObject(n) || doc.GetObject(n) is not PdfDictionary type0 || type0.GetName(PdfName.Subtype)?.Value != "Type0") continue;
            var font = (PdfDictionary)type0.GetArray(PdfName.Get("DescendantFonts"))!.Get(0)!;
            var descriptor = font.GetDictionary(PdfName.Get("FontDescriptor"))!;
            Assert.Equal(font.GetName(PdfName.Get("BaseFont"))?.Value, descriptor.GetName(PdfName.Get("FontName"))?.Value);
            checkedFonts++;
        }
        Assert.True(checkedFonts > 0);
    }

    // ---- 5.3 g: a PDF/A file saved the ordinary way with what surely breaks PDF/A loses its identification (docs/FEHLERGEDAECHTNIS.md, No. 554)

    private static (PdfDocument Doc, PdfSaveResult Result) SavedFromPdfA(PdfSaveOptions options, string password = "")
    {
        var pdfa = PdfATests.Write(Simple(1).Build(), PdfAConformance.A2b);
        Assert.True(pdfa.Conforms, PdfATests.Describe(pdfa.Violations));
        using var doc = PdfDocument.Open(pdfa.Bytes);
        Assert.Equal(PdfAConformance.A2b, PdfACheck.Declared(doc));
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, Core.Pdf.Editing.PageItem.From(doc), output, options);
        return (PdfDocument.Open(output.ToArray(), password), result);
    }

    /// <summary>
    /// A PDF/A-2b file saved with a password: encryption is forbidden in PDF/A, the copy no longer claims to be one (pdfaid removed, the
    /// rest of the XMP stays) and the result says why. Before, the encrypted copy still said „PDF/A-2b“.
    /// </summary>
    [Fact]
    public void PdfA2bSource_SavedWithPassword_LosesTheClaim()
    {
        var (doc, result) = SavedFromPdfA(new PdfSaveOptions { Protection = new PdfProtection("geheim", "chef", Core.Pdf.Security.PdfPermissions.Print) }, "geheim");
        using (doc)
        {
            Assert.Null(PdfACheck.Declared(doc));
            Assert.DoesNotContain("pdfaid", Xmp(doc), StringComparison.Ordinal);
            Assert.Contains("rdf:RDF", Xmp(doc), StringComparison.Ordinal);
            Assert.Equal(Core.Localization.Tr.T("Passwortschutz"), result.PdfAClaimRemovedFor);
        }
    }

    /// <summary>Brotli (not part of ISO 32000-2) and PDF 2.0 (outside parts 2 and 3) break it as well.</summary>
    [Fact]
    public void PdfA2bSource_SavedWithBrotliOr20_LosesTheClaim()
    {
        var (brotli, packed) = SavedFromPdfA(new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Lossless) with { Brotli = true } });
        using (brotli)
        {
            Assert.Null(PdfACheck.Declared(brotli));
            Assert.Equal(Core.Localization.Tr.T("Brotli-Packverfahren"), packed.PdfAClaimRemovedFor);
        }
        var (pdf20, raised) = SavedFromPdfA(new PdfSaveOptions { Version = "2.0" });
        using (pdf20)
        {
            Assert.Null(PdfACheck.Declared(pdf20));
            Assert.Equal(Core.Localization.Tr.T("PDF {0}", "2.0"), raised.PdfAClaimRemovedFor);
        }
    }

    /// <summary>Saved plainly (1.7, no password, Flate) it keeps the claim — whether the copy still conforms is the check's to say.</summary>
    [Fact]
    public void PdfA2bSource_SavedPlain_KeepsTheClaim()
    {
        var (doc, result) = SavedFromPdfA(new PdfSaveOptions { Version = "1.7" });
        using (doc)
        {
            Assert.Equal(PdfAConformance.A2b, PdfACheck.Declared(doc));
            Assert.Null(result.PdfAClaimRemovedFor);
        }
    }

    // ---- phase 9.1: a PDF/A file saved the ordinary way (also compressed) keeps a claim only if it is true (docs/FEHLERGEDAECHTNIS.md, No. 707–709)

    private static (PdfDocument Doc, PdfSaveResult Result, byte[] Bytes) Saved(PdfAConformance part, PdfSaveOptions options)
    {
        var pdfa = PdfATests.Write(Simple(1).Build(), part);
        Assert.True(pdfa.Conforms, PdfATests.Describe(pdfa.Violations));
        using var doc = PdfDocument.Open(pdfa.Bytes);
        Assert.Equal(part, PdfACheck.Declared(doc));
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, Core.Pdf.Editing.PageItem.From(doc), output, options);
        return (PdfDocument.Open(output.ToArray()), result, output.ToArray());
    }

    /// <summary>
    /// A PDF/A-4 file saved the ordinary way as 2.0 keeps its identification, so the copy must not get an information dictionary
    /// (ISO 19005-4, 6.1.3). Before, every one got /Info with producer and date — all 144 conforming files of the part-4 collection lost
    /// their conformance on a plain save.
    /// </summary>
    [Fact]
    public void PdfA4Source_SavedPlain_StaysConforming()
    {
        var (doc, result, _) = Saved(PdfAConformance.A4, new PdfSaveOptions { Version = "2.0" });
        using (doc)
        {
            Assert.Equal(PdfAConformance.A4, PdfACheck.Declared(doc));
            Assert.Null(result.PdfAClaimRemovedFor);
            Assert.Null(doc.Trailer.GetRaw(PdfName.Info));
            var violations = PdfACheck.Check(doc);
            Assert.True(violations.Count == 0, PdfATests.Describe(violations));
        }
    }

    /// <summary>
    /// A PDF/A-1b file compressed as 1.4: object streams would need 1.5, which PDF/A-1 does not allow — before, the copy was raised to 1.5
    /// and lost its identification on every compression; now it stays 1.4 without object streams and conforms.
    /// </summary>
    [Theory]
    [InlineData(CompressionPreset.Lossless)]
    [InlineData(CompressionPreset.Screen)]
    public void PdfA1bSource_Compressed_StaysPdfA1(CompressionPreset preset)
    {
        var (doc, result, bytes) = Saved(PdfAConformance.A1b, new PdfSaveOptions { Version = "1.4", Compression = CompressionSettings.For(preset) });
        using (doc)
        {
            Assert.Equal("%PDF-1.4", System.Text.Encoding.ASCII.GetString(bytes, 0, 8));
            Assert.Equal(PdfAConformance.A1b, PdfACheck.Declared(doc));
            Assert.Null(result.PdfAClaimRemovedFor);
            var violations = PdfACheck.Check(doc);
            Assert.True(violations.Count == 0, PdfATests.Describe(violations));
        }
    }

    /// <summary>
    /// A signed PDF/A-2b file saved the ordinary way: written anew, the byte ranges of its signature no longer cover the file (6.4.3), so
    /// the identification goes and the result says why. Before, the copy still claimed PDF/A-2b (one file of the part-2 collection).
    /// </summary>
    [Fact]
    public void SignedPdfA2bSource_SavedPlain_LosesTheClaim()
    {
        const string xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF " +
                           "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" " +
                           "xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"><pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>" +
                           "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        byte[] source;
        using (var signed = PdfDocument.Open(SignedTestPdf.Build(xmp: xmp)))
        {
            Assert.Equal(PdfAConformance.A2b, PdfACheck.Declared(signed));
            Assert.NotEmpty(Core.Pdf.Signatures.DigitalSignatures.Find(signed));
            using var output = new MemoryStream();
            var result = PdfWriter.Write(signed, Core.Pdf.Editing.PageItem.From(signed), output, new PdfSaveOptions { Version = "1.7" });
            Assert.Equal(Core.Localization.Tr.T("einer ungültig gewordenen digitalen Signatur"), result.PdfAClaimRemovedFor);
            source = output.ToArray();
        }
        using var copy = PdfDocument.Open(source);
        Assert.Null(PdfACheck.Declared(copy));
    }
}
