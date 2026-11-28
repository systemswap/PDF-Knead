using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The PDF/UA identification and the accessibility declarations of a file go when a save breaks its accessibility, and stay when nothing does
/// (docs/ABLAUFPLAN.md, 6.1; decision G: removed without asking, said afterwards).
/// </summary>
public sealed class PdfUaClaimTests
{
    private const string Wtpdf = "https://pdfa.org/declarations/wtpdf/#accessibility1.0";

    /// <summary>
    /// A tagged one-page file (a paragraph with MCID 0 under a structure tree) whose XMP holds <paramref name="claims"/> in a description;
    /// <paramref name="tagged"/> false leaves out the structure tree.
    /// </summary>
    private static PdfDocument Tagged(string claims, bool tagged = true, string version = "1.7")
    {
        var pdf = new TestPdf();
        var font = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")), ("Encoding", TestPdf.N("WinAnsiEncoding")));
        var content = pdf.Add(TestPdf.FlateStream("/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann wohnt hier.) Tj ET EMC"u8.ToArray()));
        var pagesNumber = pdf.Add(new PdfDictionary());
        var pageNumber = pdf.Add(new PdfDictionary());
        var xmp = "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                  claims + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        var metadata = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))), Encoding.UTF8.GetBytes(xmp)));
        var catalog = TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pagesNumber)), ("Metadata", TestPdf.Ref(metadata)), ("Lang", new PdfString("de"u8.ToArray())));
        if (tagged)
        {
            var root = pdf.Add(new PdfDictionary());
            var paragraph = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("P")), ("P", TestPdf.Ref(root)), ("Pg", TestPdf.Ref(pageNumber)), ("K", PdfInteger.Of(0))));
            var parentTree = TestPdf.Dict(("Nums", new PdfArray { PdfInteger.Of(0), new PdfArray { TestPdf.Ref(paragraph) } }));
            pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", TestPdf.Ref(paragraph)), ("ParentTree", parentTree), ("ParentTreeNextKey", PdfInteger.Of(1))));
            catalog.Set(PdfName.Get("StructTreeRoot"), TestPdf.Ref(root));
            catalog.Set(PdfName.Get("MarkInfo"), TestPdf.Dict(("Marked", PdfBoolean.Of(true))));
        }
        pdf.Replace(pageNumber, TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNumber)), ("Contents", TestPdf.Ref(content)),
            ("MediaBox", TestPdf.Box(0, 0, 595, 842)), ("StructParents", PdfInteger.Of(0)), ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", font)))))));
        pdf.Replace(pagesNumber, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray { TestPdf.Ref(pageNumber) }), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(catalog);
        _ = version;
        return PdfDocument.Open(pdf.Build());
    }

    private static string Description(string inner, string attributes = "") =>
        $"<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\" xmlns:pdfd=\"http://pdfa.org/declarations/\" {attributes}>{inner}</rdf:Description>";

    private static string Declarations(params string[] uris) =>
        "<pdfd:declarations><rdf:Bag>" + string.Concat(uris.Select(u => $"<rdf:li rdf:parseType=\"Resource\"><pdfd:conformsTo>{u}</pdfd:conformsTo></rdf:li>")) + "</rdf:Bag></pdfd:declarations>";

    /// <summary>Saves with a bar over the paragraph (<paramref name="redact"/>) or as it is.</summary>
    private static (PdfSaveResult Result, PdfDocument Saved) Save(PdfDocument doc, bool redact, string version = "1.7")
    {
        var items = PageItem.From(doc);
        if (redact) items[0] = items[0].WithElements([new RedactionElement { Rect = new PdfRectangle(60, 690, 300, 720) }]);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, Version = version });
        return (result, PdfDocument.Open(output.ToArray()));
    }

    private static string Xmp(PdfDocument doc)
    {
        var xmp = (PdfStream)doc.Catalog!.Get(PdfName.Get("Metadata"))!;
        var text = Encoding.UTF8.GetString(doc.GetStreamData(xmp));
        Assert.NotNull(PdfAXmp.Read(doc.GetStreamData(xmp)));   // well-formed
        return text;
    }

    /// <summary>
    /// A bar over all the tagged content of a PDF/UA file: no tagged content is left, the identification goes, the result says it and why
    /// (before 6.2a it stayed — a false claim).
    /// </summary>
    [Fact]
    public void Redacted_PdfUaClaimRemoved()
    {
        using var doc = Tagged(Description("<pdfuaid:part>1</pdfuaid:part>"));
        var (result, saved) = Save(doc, redact: true);
        using (saved)
        {
            Assert.DoesNotContain("pdfuaid", Xmp(saved));
            Assert.True(result.AccessibilityClaimRemoved);
            Assert.Contains("kein getaggter Inhalt mehr", result.AccessibilityBreaks);
            Assert.DoesNotContain("Schwärzen", result.AccessibilityBreaks);
        }
    }

    /// <summary>
    /// A bar over one word of the paragraph: the bar is an artifact, the paragraph keeps the rest of its text — nothing breaks, the
    /// identification and the declarations stay (docs/ABLAUFPLAN.md, 6.3 step 7; docs/FEHLERGEDAECHTNIS.md, No. 660). Before: every bar
    /// removed them („Schwärzen“).
    /// </summary>
    [Fact]
    public void RedactedOneWord_PdfUaClaimKept()
    {
        using var doc = Tagged(Description("<pdfuaid:part>1</pdfuaid:part>" + Declarations(Wtpdf)));
        var text = Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Text);
        var bounds = Assert.Single(text.Quads(Assert.Single(text.Find("Mustermann")))).Bounds;
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new RedactionElement { Rect = new PdfRectangle(bounds.Left - 0.5, bounds.Top - 0.5, bounds.Right + 0.5, bounds.Bottom + 0.5) }]);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true });
        using var saved = PdfDocument.Open(output.ToArray());
        var xmp = Xmp(saved);
        Assert.Contains("pdfuaid:part", xmp);
        Assert.Contains("wtpdf", xmp);
        Assert.False(result.AccessibilityClaimRemoved);
        Assert.Empty(result.AccessibilityBreaks);
        Assert.DoesNotContain("Mustermann", Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.Build(saved, saved.Pages[0]).Text).Text);
    }

    /// <summary>The identification in attribute form (pdfuaid:part="1") goes as well; the packet stays well-formed.</summary>
    [Fact]
    public void AttributeForm_Removed()
    {
        using var doc = Tagged(Description("", "pdfuaid:part=\"1\""));
        var (result, saved) = Save(doc, redact: true);
        using (saved)
        {
            Assert.DoesNotContain("pdfuaid:part", Xmp(saved));
            Assert.True(result.AccessibilityClaimRemoved);
        }
    }

    /// <summary>PDF/UA-2: part and rev go together.</summary>
    [Fact]
    public void PdfUa2Claim_PartAndRevRemoved()
    {
        using var doc = Tagged(Description("<pdfuaid:part>2</pdfuaid:part><pdfuaid:rev>2024</pdfuaid:rev>"));
        var (_, saved) = Save(doc, redact: true, version: "2.0");
        using (saved)
        {
            var xmp = Xmp(saved);
            Assert.DoesNotContain("pdfuaid:part", xmp);
            Assert.DoesNotContain("pdfuaid:rev", xmp);
        }
    }

    /// <summary>A PDF/UA-2 file written as 1.7 is no PDF/UA-2 file (it needs PDF 2.0): the identification goes even without other changes.</summary>
    [Fact]
    public void Ua2File_SavedAs17_ClaimRemoved()
    {
        using var doc = Tagged(Description("<pdfuaid:part>2</pdfuaid:part><pdfuaid:rev>2024</pdfuaid:rev>"));
        var (result, saved) = Save(doc, redact: false, version: "1.7");
        using (saved)
        {
            Assert.DoesNotContain("pdfuaid", Xmp(saved));
            Assert.True(result.AccessibilityClaimRemoved);
            Assert.Contains(result.AccessibilityBreaks, b => b.Contains("PDF/UA-2", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The declarations that claim accessibility (WTPDF, WCAG — also written without the slash before „#“, with a line break and indent) go;
    /// another declaration stays; the bag stays with it.
    /// </summary>
    [Fact]
    public void Redacted_AccessibilityDeclarationsRemoved_OtherDeclarationKept()
    {
        using var doc = Tagged(Description(Declarations(Wtpdf, "\n      http://pdfa.org/declarations/wtpdf#reuse1.0\n    ", "https://example.org/wcag21", "http://pdfa.org/declarations/#hipaa")));
        var (result, saved) = Save(doc, redact: true);
        using (saved)
        {
            var xmp = Xmp(saved);
            Assert.DoesNotContain("wtpdf", xmp);
            Assert.DoesNotContain("wcag", xmp);
            Assert.Contains("#hipaa", xmp);
            Assert.True(result.AccessibilityClaimRemoved);
        }
    }

    /// <summary>Only accessibility declarations: the property goes entirely.</summary>
    [Fact]
    public void OnlyAccessibilityDeclarations_PropertyGone()
    {
        using var doc = Tagged(Description(Declarations(Wtpdf)));
        var (_, saved) = Save(doc, redact: true);
        using (saved) Assert.DoesNotContain("declarations>", Xmp(saved));
    }

    /// <summary>Saved without a change that breaks anything: identification and declarations stay (protection).</summary>
    [Fact]
    public void Unchanged_PdfUaClaimKept()
    {
        using var doc = Tagged(Description("<pdfuaid:part>1</pdfuaid:part>" + Declarations(Wtpdf)));
        var (result, saved) = Save(doc, redact: false);
        using (saved)
        {
            var xmp = Xmp(saved);
            Assert.Contains("pdfuaid:part", xmp);
            Assert.Contains("wtpdf", xmp);
            Assert.False(result.AccessibilityClaimRemoved);
            Assert.Empty(result.AccessibilityBreaks);
        }
    }

    /// <summary>
    /// The repair „Kennung entfernen“ of the check „barrierefrei?“ (docs/FEHLERGEDAECHTNIS.md, No. 667): without any other change the
    /// identification and the accessibility declarations go, with the reason said; other declarations stay — in an untagged file as well.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RemoveAccessibilityClaim_ClaimAndDeclarationsGone_ReasonSaid(bool tagged)
    {
        using var doc = Tagged(Description("<pdfuaid:part>1</pdfuaid:part>" + Declarations(Wtpdf, "http://pdfa.org/declarations/#hipaa")), tagged);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { RemoveAccessibilityClaim = true });
        using var saved = PdfDocument.Open(output.ToArray());
        var xmp = Xmp(saved);
        Assert.DoesNotContain("pdfuaid:part", xmp);
        Assert.DoesNotContain("wtpdf", xmp);
        Assert.Contains("#hipaa", xmp);
        Assert.True(result.AccessibilityClaimRemoved);
        Assert.Equal(["Kennung auf Wunsch entfernt"], result.AccessibilityBreaks);
    }

    /// <summary>A PDF/UA-1 file written as 2.0 is no PDF/UA-1 file (ISO 14289-1, 6.1: PDF 1.0 to 1.7): the identification goes (No. 614).</summary>
    [Fact]
    public void Ua1File_SavedAs20_ClaimRemoved()
    {
        using var doc = Tagged(Description("<pdfuaid:part>1</pdfuaid:part>"));
        var (result, saved) = Save(doc, redact: false, version: "2.0");
        using (saved)
        {
            Assert.DoesNotContain("pdfuaid", Xmp(saved));
            Assert.True(result.AccessibilityClaimRemoved);
            Assert.Contains(result.AccessibilityBreaks, b => b.Contains("PDF/UA-1", StringComparison.Ordinal));
        }
    }

    // ---- 6.1 c: a PDF/A copy keeps what the source's XMP says (docs/ABLAUFPLAN.md, 6.1 step 7; docs/FEHLERGEDAECHTNIS.md, No. 614)

    private static (PdfAResult Result, string Xmp) PdfA(PdfDocument doc, PdfAConformance part)
    {
        var result = PdfAExport.Write(doc, PageItem.From(doc), part);
        using var copy = PdfDocument.Open(result.Bytes);
        return (result, Xmp(copy));
    }

    /// <summary>
    /// A file without /Lang in the catalogue whose XMP title has a version in en-US besides x-default (docs/FEHLERGEDAECHTNIS.md, No. 677;
    /// found by the round trip of 6.8): the PDF/A copy keeps that version — it is where the file names its language (ISO 14289-1, 7.2); before,
    /// the packet built anew kept x-default alone and the copy had no language left.
    /// </summary>
    [Fact]
    public void PdfA_KeepsTheLanguageVersionsOfTheTitle()
    {
        var xmp = "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                  "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\"><pdfuaid:part>1</pdfuaid:part>" +
                  "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Mustermappe</rdf:li><rdf:li xml:lang=\"en-US\">Mustermappe</rdf:li></rdf:Alt></dc:title>" +
                  "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        var bytes = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Metadata 7 0 R/MarkInfo<</Marked true>>/StructTreeRoot 8 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>/StructParents 0>>",
            [6] = ("", "/P <</MCID 0>> BDC 0 0 m 100 100 l S EMC"),
            [7] = ("/Type/Metadata/Subtype/XML", xmp),
            [8] = "<</Type/StructTreeRoot/K 9 0 R/ParentTree<</Nums[0[9 0 R]]>>>>",
            [9] = "<</Type/StructElem/S/P/P 8 0 R/Pg 4 0 R/K 0>>",
            [10] = "<</Title(Mustermappe)>>",
        }, "/Info 10 0 R");
        using var doc = PdfDocument.Open(bytes);
        Assert.DoesNotContain(Core.Pdf.Accessibility.AccessibilityCheck.Check(doc, Core.Pdf.Accessibility.AccessibilityProfile.PdfUa1).Findings, f => f.Id == "7.2-33");
        var result = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b);
        using var copy = PdfDocument.Open(result.Bytes);
        Assert.Contains("xml:lang=\"en-US\"", Xmp(copy));
        Assert.DoesNotContain(Core.Pdf.Accessibility.AccessibilityCheck.Check(copy, Core.Pdf.Accessibility.AccessibilityProfile.PdfUa1).Findings, f => f.Id == "7.2-33");
        Assert.True(result.Conforms, string.Join(" | ", result.Violations.Select(v => v.Rule + " " + v.Text)));
    }

    /// <summary>
    /// Properties of a schema the source declares itself in a correct extension schema stay in the PDF/A copy, with that declaration — PDF/A
    /// allows them (ISO 19005-2, 6.6.2.3.1), and other software reads them (docs/FEHLERGEDAECHTNIS.md, No. 863: the invoice data of hybrid
    /// invoices were lost). A declaration missing a field carries nothing (the copy would break 6.6.2.3.3); another standard's identification
    /// (here PDF/X) is never carried, even when declared. Only where the copy keeps attachments (PDF/A-3): such data often describe them, and
    /// PDF/A-2 drops them.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PdfA_KeepsPropertiesOfADeclaredSchema_NotWithAFaultyDeclaration(bool correct)
    {
        const string ns = "http://example.org/ns/muster/1.0/", pdfx = "http://www.npes.org/pdfx/ns/id/";
        static string Property(string name, string category, bool described) =>
            $"<rdf:li rdf:parseType=\"Resource\"><pdfaProperty:name>{name}</pdfaProperty:name><pdfaProperty:valueType>Text</pdfaProperty:valueType>" +
            $"<pdfaProperty:category>{category}</pdfaProperty:category>{(described ? "<pdfaProperty:description>-</pdfaProperty:description>" : "")}</rdf:li>";
        static string Schema(string uri, string prefix, string property) =>
            $"<rdf:li rdf:parseType=\"Resource\"><pdfaSchema:schema>{prefix}</pdfaSchema:schema><pdfaSchema:namespaceURI>{uri}</pdfaSchema:namespaceURI>" +
            $"<pdfaSchema:prefix>{prefix}</pdfaSchema:prefix><pdfaSchema:property><rdf:Seq>{property}</rdf:Seq></pdfaSchema:property></rdf:li>";
        var xmp = "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                  "<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" " +
                  "xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\"><pdfaExtension:schemas><rdf:Bag>" +
                  Schema(ns, "mu", Property("Ablage", "external", correct)) + Schema(pdfx, "pdfxid", Property("GTS_PDFXVersion", "internal", true)) +
                  "</rdf:Bag></pdfaExtension:schemas></rdf:Description>" +
                  $"<rdf:Description rdf:about=\"\" xmlns:mu=\"{ns}\" xmlns:pdfxid=\"{pdfx}\"><mu:Ablage>Ordner 7</mu:Ablage>" +
                  "<pdfxid:GTS_PDFXVersion>PDF/X-4</pdfxid:GTS_PDFXVersion></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        var bytes = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Metadata 7 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>>>",
            [6] = ("", "0 0 m 100 100 l S"),
            [7] = ("/Type/Metadata/Subtype/XML", xmp),
            [10] = "<</Title(Mustermappe)>>",
        }, "/Info 10 0 R");
        using var doc = PdfDocument.Open(bytes);
        var (result, copied) = PdfA(doc, PdfAConformance.A3b);
        Assert.True(result.Conforms, string.Join(" | ", result.Violations.Select(v => v.Rule + " " + v.Text)));
        Assert.DoesNotContain("Ordner 7", PdfA(doc, PdfAConformance.A2b).Xmp, StringComparison.Ordinal);
        Assert.Equal(correct, copied.Contains("Ordner 7", StringComparison.Ordinal));
        Assert.Equal(correct, copied.Contains($"<pdfaSchema:namespaceURI>{ns}</pdfaSchema:namespaceURI>", StringComparison.Ordinal));
        Assert.DoesNotContain("GTS_PDFXVersion", copied, StringComparison.Ordinal);
    }

    /// <summary>
    /// A PDF/UA-1 file with a table head (Table, THead, TR, TH) as PDF/A-1a (docs/FEHLERGEDAECHTNIS.md, No. 683; found by the run of all ways to
    /// write, 6.8 step 8): PDF/A-1 knows no THead and maps it to another type, which PDF/UA-1 forbids for a standard type (ISO 14289-1, 7.1) —
    /// the identification goes, and that is said. Before, it stayed over 7.1-7, 7.2-3 and 7.2-4. As PDF/A-2a the table stays as it is, and so does
    /// the identification.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A1a, true)]
    [InlineData(PdfAConformance.A2a, false)]
    public void PdfA1a_OfAPdfUa1FileWithATableHead_ClaimGoes(PdfAConformance part, bool goes)
    {
        var xmp = "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                  Description("<pdfuaid:part>1</pdfuaid:part>") + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        var bytes = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Metadata 7 0 R/MarkInfo<</Marked true>>/StructTreeRoot 8 0 R/Lang(de)>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 5 0 R>>>>/StructParents 0>>",
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [6] = ("", "/TH <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Name) Tj ET EMC"),
            [7] = ("/Type/Metadata/Subtype/XML", xmp),
            [8] = "<</Type/StructTreeRoot/K 9 0 R/ParentTree<</Nums[0[13 0 R]]>>/ParentTreeNextKey 1>>",
            [9] = "<</Type/StructElem/S/Document/P 8 0 R/K 10 0 R>>",
            [10] = "<</Type/StructElem/S/Table/P 9 0 R/K 11 0 R>>",
            [11] = "<</Type/StructElem/S/THead/P 10 0 R/K 12 0 R>>",
            [12] = "<</Type/StructElem/S/TR/P 11 0 R/K 13 0 R>>",
            [13] = "<</Type/StructElem/S/TH/P 12 0 R/Pg 4 0 R/K 0/A<</O/Table/Scope/Column>>>>",
            [14] = "<</Title(Mustermappe)>>",
        }, "/Info 14 0 R");
        using var doc = PdfDocument.Open(bytes);
        var (result, written) = PdfA(doc, part);
        Assert.Equal(goes, result.AccessibilityClaimRemoved);
        Assert.Equal(goes, !written.Contains("pdfuaid:part", StringComparison.Ordinal));
        if (goes) Assert.Contains(result.AccessibilityBreaks, b => b.Contains("THead", StringComparison.Ordinal));
    }

    /// <summary>
    /// A PDF/UA-1 file as PDF/A-1b, -2b, -2a, -3b: the identification stays, declared in its extension schema, and the copy meets PDF/A — before,
    /// the packet was built from the information dictionary alone and the identification of a correct PDF/UA file was lost.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformance.A1b)]
    [InlineData(PdfAConformance.A2b)]
    [InlineData(PdfAConformance.A2a)]
    [InlineData(PdfAConformance.A3b)]
    public void PdfA_OfAPdfUa1File_KeepsTheClaimWithItsExtensionSchema(PdfAConformance part)
    {
        using var doc = Tagged(Description("<pdfuaid:part>1</pdfuaid:part>"));
        var (result, xmp) = PdfA(doc, part);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        Assert.False(result.AccessibilityClaimRemoved);
        using var copy = PdfDocument.Open(result.Bytes);
        Assert.Equal(1, AccessibilityClaims.Read(Encoding.UTF8.GetBytes(xmp))!.PdfUaPart);
        Assert.Contains("<pdfaSchema:namespaceURI>http://www.aiim.org/pdfua/ns/id/</pdfaSchema:namespaceURI>", xmp);
    }

    /// <summary>A PDF/UA-2 file as PDF/A-4 (PDF 2.0): identification and declarations stay, without an extension schema (PDF/A-4 needs none).</summary>
    [Fact]
    public void PdfA4_OfAPdfUa2File_KeepsClaimAndDeclarations()
    {
        using var doc = Tagged(Description("<pdfuaid:part>2</pdfuaid:part><pdfuaid:rev>2024</pdfuaid:rev>" + Declarations(Wtpdf)));
        var (result, xmp) = PdfA(doc, PdfAConformance.A4);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        Assert.Equal(2, AccessibilityClaims.Read(Encoding.UTF8.GetBytes(xmp))!.PdfUaPart);
        Assert.Contains("pdfuaid:rev", xmp);
        Assert.Contains(Wtpdf, xmp);
        Assert.DoesNotContain("pdfaExtension", xmp);
        Assert.Empty(result.DeclarationsLeftOut);
    }

    /// <summary>A PDF/UA-1 file as PDF/A-4 (PDF 2.0): no PDF/UA-1 file any more — the identification is left out, and that is said.</summary>
    [Fact]
    public void PdfA4_OfAPdfUa1File_ClaimLeftOut()
    {
        using var doc = Tagged(Description("<pdfuaid:part>1</pdfuaid:part>"));
        var (result, xmp) = PdfA(doc, PdfAConformance.A4);
        Assert.DoesNotContain("pdfuaid", xmp);
        Assert.True(result.AccessibilityClaimRemoved);
        Assert.Contains(result.AccessibilityBreaks, b => b.Contains("PDF/UA-1", StringComparison.Ordinal));
    }

    /// <summary>PDF/A-2b cannot carry declarations without an extension schema for their structures: left out and said; the identification stays.</summary>
    [Fact]
    public void PdfA2b_DeclarationsLeftOut_Said()
    {
        using var doc = Tagged(Description("<pdfuaid:part>1</pdfuaid:part>" + Declarations(Wtpdf, "http://pdfa.org/declarations/#hipaa")));
        var (result, xmp) = PdfA(doc, PdfAConformance.A2b);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        Assert.DoesNotContain("declarations", xmp);
        Assert.Equal([Wtpdf, "http://pdfa.org/declarations/#hipaa"], result.DeclarationsLeftOut);
        Assert.Contains("pdfuaid:part", xmp);
    }

    /// <summary>
    /// A title only in the XMP (as PDF 2.0 writes it) and the rights stay in the PDF/A copy — before, a copy of a file without /Title had no
    /// title at all (PDF/UA requires one, ISO 14289-1, 7.1). A property of an undeclared schema is not carried (it would break PDF/A-2).
    /// </summary>
    [Fact]
    public void PdfA2b_XmpTitleAndRights_Kept_UnknownSchemaNot()
    {
        using var doc = Tagged(Description("<dc:title xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><rdf:Alt><rdf:li xml:lang=\"x-default\">Nur im XMP</rdf:li></rdf:Alt></dc:title>" +
                                           "<xmpRights:Marked xmlns:xmpRights=\"http://ns.adobe.com/xap/1.0/rights/\">True</xmpRights:Marked>" +
                                           "<test:Ablage xmlns:test=\"http://example.org/ns/ablage/\">Ordner 7</test:Ablage>"));
        var (result, xmp) = PdfA(doc, PdfAConformance.A2b);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        Assert.Contains("Nur im XMP", xmp);
        Assert.Contains("xmpRights:Marked", xmp);
        Assert.DoesNotContain("Ordner 7", xmp);
    }

    /// <summary>A redacted PDF/UA file as PDF/A: the identification is left out (the bar has no structure), and that is said.</summary>
    [Fact]
    public void PdfA_OfARedactedPdfUaFile_ClaimLeftOut()
    {
        using var doc = Tagged(Description("<pdfuaid:part>1</pdfuaid:part>"));
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new RedactionElement { Rect = new PdfRectangle(60, 690, 300, 720) }]);
        var result = PdfAExport.Write(doc, items, PdfAConformance.A2b, new PdfSaveOptions { ApplyRedactions = true });
        using var copy = PdfDocument.Open(result.Bytes);
        Assert.DoesNotContain("pdfuaid:part", Xmp(copy));
        Assert.True(result.AccessibilityClaimRemoved);
    }

    /// <summary>
    /// „Kennzeichnungen“ in „Eigenschaften“ (6.1 d): PDF/A, PDF/UA and the declarations in one line — known ones by name, the others counted; a
    /// file without claims has no line.
    /// </summary>
    [Fact]
    public void Claims_SummaryNamesWhatTheFileSays()
    {
        using var doc = Tagged(Description("<pdfuaid:part>1</pdfuaid:part>" + Declarations(Wtpdf, "http://pdfa.org/declarations#wcag22aa", "http://pdfa.org/declarations/#hipaa")));
        var claims = ConformanceClaims.Read(doc)!;
        Assert.Equal(1, claims.PdfUaPart);
        Assert.Null(claims.PdfA);
        Assert.Equal($"PDF/UA-1 · {Tr.T("WTPDF 1.0 barrierefrei")} · WCAG 2.2 AA · {Tr.T("{0} weitere", 1)}", claims.Summary);
        var pdfA = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b);
        using (var copy = PdfDocument.Open(pdfA.Bytes)) Assert.Equal("PDF/A-2b · PDF/UA-1", ConformanceClaims.Read(copy)!.Summary);
        using var plain = Tagged(Description("<pdfuaid:amd>kein Teil</pdfuaid:amd>"));
        Assert.Null(ConformanceClaims.Read(plain));
    }

    /// <summary>The reader of the claims: part and declarations as written (trimmed); accessibility told tolerantly.</summary>
    [Fact]
    public void Read_ClaimsAndAccessibilityDeclarations()
    {
        var packet = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            Description("<pdfuaid:part> 1 </pdfuaid:part>" + Declarations(Wtpdf, " http://pdfa.org/declarations/#hipaa ")) + "</rdf:RDF></x:xmpmeta>");
        var found = AccessibilityClaims.Read(packet)!;
        Assert.Equal(1, found.PdfUaPart);
        Assert.Equal([Wtpdf, "http://pdfa.org/declarations/#hipaa"], found.ConformsTo);
        Assert.True(AccessibilityClaims.IsAccessibility("HTTP://pdfa.org/declarations/wtpdf#accessibility1.0"));
        Assert.True(AccessibilityClaims.IsAccessibility("https://pdfa.org/declarations#iso32005"));
        Assert.False(AccessibilityClaims.IsAccessibility("http://pdfa.org/declarations/#hipaa"));
    }
}
