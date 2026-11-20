using System.Text;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The check „barrierefrei?“, stages A and B (docs/ABLAUFPLAN.md, 6.7; docs/FEHLERGEDAECHTNIS.md, No. 615): one crafted file meets every rule
/// built; each case breaks exactly one, the check must name it — before there was no check at all.
/// </summary>
public sealed class AccessibilityCheckTests
{
    /// <summary>What a case changes in the file that meets everything.</summary>
    private sealed class Parts
    {
        public required TestPdf Pdf;
        public required PdfDictionary Catalog, Page, Root, Element;
        public string Xmp = Description("<pdfuaid:part>1</pdfuaid:part><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Rechnung</rdf:li></rdf:Alt></dc:title>");
        public string Content = "/Artifact BMC 0 0 m 100 100 l S EMC";
        public string Version = "1.7";
    }

    private static string Description(string inner, string attributes = "") =>
        $"<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" {attributes}>{inner}</rdf:Description>";

    /// <summary>A one-page tagged file (Document element, language, title, title shown, identification), changed by <paramref name="change"/>.</summary>
    private static PdfDocument File(Action<Parts>? change = null)
    {
        var pdf = new TestPdf();
        var pagesNumber = pdf.Add(new PdfDictionary());
        var pageNumber = pdf.Add(new PdfDictionary());
        var rootNumber = pdf.Add(new PdfDictionary());
        var element = Dict(("Type", N("StructElem")), ("S", N("Document")), ("P", Ref(rootNumber)));
        var elementNumber = pdf.Add(element);
        var root = Dict(("Type", N("StructTreeRoot")), ("K", Ref(elementNumber)));
        var catalog = Dict(("Type", N("Catalog")), ("Pages", Ref(pagesNumber)), ("Lang", new PdfString("de"u8.ToArray())),
            ("MarkInfo", Dict(("Marked", PdfBoolean.Of(true)))), ("StructTreeRoot", Ref(rootNumber)),
            ("ViewerPreferences", Dict(("DisplayDocTitle", PdfBoolean.Of(true)))));
        var page = Dict(("Type", N("Page")), ("Parent", Ref(pagesNumber)), ("MediaBox", Box(0, 0, 595, 842)), ("Resources", new PdfDictionary()));
        var parts = new Parts { Pdf = pdf, Catalog = catalog, Page = page, Root = root, Element = element };
        change?.Invoke(parts);
        page.Set(PdfName.Contents, Ref(pdf.Add(FlateStream(Encoding.Latin1.GetBytes(parts.Content)))));
        if (parts.Xmp.Length > 0)
        {
            var xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                      parts.Xmp + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
            catalog.Set(PdfName.Metadata, Ref(pdf.Add(new PdfStream(Dict(("Type", N("Metadata")), ("Subtype", N("XML"))), Encoding.UTF8.GetBytes(xmp)))));
        }
        pdf.Replace(rootNumber, root);
        pdf.Replace(pageNumber, page);
        pdf.Replace(pagesNumber, Dict(("Type", N("Pages")), ("Kids", new PdfArray { Ref(pageNumber) }), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(catalog);
        var bytes = pdf.Build();
        if (parts.Version != "1.7") bytes = [.. Encoding.ASCII.GetBytes("%PDF-" + parts.Version), .. bytes.AsSpan(8)];
        return PdfDocument.Open(bytes);
    }

    private static AccessibilityReport Check(PdfDocument doc, AccessibilityProfile? profile = null) => AccessibilityCheck.Check(doc, profile);

    /// <summary>The file that meets everything: no finding in PDF/UA-1 (protection against false alarms).</summary>
    [Fact]
    public void Conforming_NoFinding()
    {
        using var doc = File();
        var report = Check(doc);
        Assert.Empty(report.Findings);
        Assert.Equal(1, report.Part);
        Assert.Equal(Tr.T("Maschinell geprüft (Stufen {0}): keine Fehler", AccessibilityCheck.Stages), report.Headline);
    }

    public static TheoryData<string, string> Ua1Cases => new()
    {
        { "keine Kennung", "5-1" }, { "Teil 2", "5-2" }, { "anderes Präfix", "5-3" }, { "Version 2.0", "6.1-1" }, { "ohne Marked", "6.2-1" },
        { "Suspects", "7.1-4" }, { "ohne XMP", "7.1-8" }, { "ohne Titel", "7.1-9" }, { "ohne DisplayDocTitle", "7.1-10" }, { "ohne Struktur", "7.1-11" },
        { "Lesezeichen ohne Sprache", "7.2-2" }, { "Anmerkung ohne Sprache", "7.2-24" }, { "Feld ohne Sprache", "7.2-25" },
        { "Sprache falsch geformt", "7.2-29" }, { "Sprache leer am Element", "7.2-29" }, { "Sprache im Inhalt falsch", "7.2-29" },
        { "Metadaten ohne Sprache", "7.2-33" }, { "Text ohne Sprache", "7.2-34" }, { "Ebene ohne Namen", "7.10-1" }, { "Ebene mit AS", "7.10-2" },
        { "Anhang ohne UF", "7.11-1" }, { "Dateianhang-Anmerkung ohne UF", "7.18.7-1" }, { "dynamisches XFA", "7.15-1" },
    };

    /// <summary>Each rule of stage A in PDF/UA-1: a file breaking it alone is reported under its number (before: no check, nothing found).</summary>
    [Theory]
    [MemberData(nameof(Ua1Cases))]
    public void Ua1_EachRule_Found(string name, string id)
    {
        using var doc = File(p => Break(p, name));
        var report = Check(doc, AccessibilityProfile.PdfUa1);
        Assert.Contains(report.Findings, f => f.Id == id && f.Severity == AccessibilitySeverity.Error);
    }

    private static void Break(Parts p, string name)
    {
        var noLang = () => p.Catalog.Remove(PdfName.Get("Lang"));
        switch (name)
        {
            case "keine Kennung": p.Xmp = Description("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Rechnung</rdf:li></rdf:Alt></dc:title>"); break;
            case "Teil 2": p.Xmp = p.Xmp.Replace("<pdfuaid:part>1<", "<pdfuaid:part>2<", StringComparison.Ordinal); break;
            case "anderes Präfix": p.Xmp = p.Xmp.Replace("<pdfuaid:part>1</pdfuaid:part>", "<ua:part xmlns:ua=\"http://www.aiim.org/pdfua/ns/id/\">1</ua:part>", StringComparison.Ordinal); break;
            case "Version 2.0": p.Version = "2.0"; break;
            case "ohne Marked": p.Catalog.Remove(PdfName.Get("MarkInfo")); break;
            case "Suspects": p.Catalog.Set(PdfName.Get("MarkInfo"), Dict(("Marked", PdfBoolean.Of(true)), ("Suspects", PdfBoolean.Of(true)))); break;
            case "ohne XMP": p.Xmp = ""; break;
            case "ohne Titel": p.Xmp = Description("<pdfuaid:part>1</pdfuaid:part>"); break;
            case "ohne DisplayDocTitle": p.Catalog.Remove(PdfName.Get("ViewerPreferences")); break;
            case "ohne Struktur": p.Catalog.Remove(PdfName.Get("StructTreeRoot")); break;
            case "Lesezeichen ohne Sprache":
                noLang();
                var outlines = p.Pdf.Add(new PdfDictionary());
                var item = p.Pdf.Add(Dict(("Title", new PdfString("Kapitel"u8.ToArray())), ("Parent", Ref(outlines))));
                p.Pdf.Replace(outlines, Dict(("Type", N("Outlines")), ("First", Ref(item)), ("Last", Ref(item)), ("Count", PdfInteger.Of(1))));
                p.Catalog.Set(PdfName.Get("Outlines"), Ref(outlines));
                break;
            case "Anmerkung ohne Sprache":
                noLang();
                p.Page.Set(PdfName.Get("Annots"), new PdfArray { Dict(("Type", N("Annot")), ("Subtype", N("Text")), ("Rect", Box(10, 10, 30, 30)),
                    ("Contents", new PdfString("Notiz von Max Mustermann"u8.ToArray()))) });
                break;
            case "Feld ohne Sprache":
                noLang();
                p.Page.Set(PdfName.Get("Annots"), new PdfArray { Dict(("Type", N("Annot")), ("Subtype", N("Widget")), ("FT", N("Tx")), ("T", new PdfString("name"u8.ToArray())),
                    ("TU", new PdfString("Vorname"u8.ToArray())), ("Rect", Box(10, 10, 100, 30))) });
                break;
            case "Sprache falsch geformt": p.Catalog.Set(PdfName.Get("Lang"), new PdfString("1-pt"u8.ToArray())); break;
            case "Sprache leer am Element": p.Element.Set(PdfName.Get("Lang"), new PdfString([])); break;
            case "Sprache im Inhalt falsch": p.Content = "/Span <</Lang (-pt)>> BDC 0 0 m 10 10 l S EMC"; break;
            case "Metadaten ohne Sprache": noLang(); break;
            case "Text ohne Sprache":
                noLang();
                p.Page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica"))))))));
                p.Content = "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET EMC";
                break;
            case "Ebene ohne Namen":
            case "Ebene mit AS":
                var layer = p.Pdf.Add(Dict(("Type", N("OCG")), ("Name", new PdfString("Ebene"u8.ToArray()))));
                var config = name == "Ebene ohne Namen" ? new PdfDictionary()
                    : Dict(("Name", new PdfString("Standard"u8.ToArray())), ("AS", new PdfArray { Dict(("Event", N("View")), ("Category", new PdfArray { N("View") })) }));
                p.Catalog.Set(PdfName.Get("OCProperties"), Dict(("OCGs", new PdfArray { Ref(layer) }), ("D", config)));
                break;
            case "Anhang ohne UF":
                var spec = p.Pdf.Add(Dict(("Type", N("Filespec")), ("F", new PdfString("daten.xml"u8.ToArray()))));
                p.Catalog.Set(PdfName.Get("Names"), Dict(("EmbeddedFiles", Dict(("Names", new PdfArray { new PdfString("daten.xml"u8.ToArray()), Ref(spec) })))));
                break;
            case "Dateianhang-Anmerkung ohne UF":
                p.Page.Set(PdfName.Get("Annots"), new PdfArray { Dict(("Type", N("Annot")), ("Subtype", N("FileAttachment")), ("Rect", Box(10, 10, 30, 30)),
                    ("FS", Dict(("Type", N("Filespec")), ("F", new PdfString("daten.xml"u8.ToArray()))))) });
                break;
            case "dynamisches XFA":
                var config2 = p.Pdf.Add(FlateStream("<config><present><pdf><dynamicRender>required</dynamicRender></pdf></present></config>"u8.ToArray()));
                p.Catalog.Set(PdfName.Get("AcroForm"), Dict(("Fields", new PdfArray()), ("XFA", new PdfArray { new PdfString("config"u8.ToArray()), Ref(config2) })));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(name), name);
        }
    }

    /// <summary>PDF/UA-2 needs PDF 2.0, part 2 with the revision 2024, a language in the catalogue; no XFA at all; descriptions of attachments.</summary>
    [Fact]
    public void Ua2_DocumentRules()
    {
        using var old = File();
        var ids = Check(old, AccessibilityProfile.PdfUa2).Findings.Select(f => f.Id).ToList();
        Assert.Contains("6.1-1", ids);      // 1.7
        Assert.Contains("5-2", ids);        // part 1
        Assert.Contains("5-5", ids);        // no rev 2024
        using var two = File(p =>
        {
            p.Version = "2.0";
            p.Xmp = p.Xmp.Replace("<pdfuaid:part>1</pdfuaid:part>", "<pdfuaid:part>2</pdfuaid:part><pdfuaid:rev>2024</pdfuaid:rev>", StringComparison.Ordinal);
            // the Document at the root in the namespace for PDF 2.0 (ISO 14289-2, 8.2.5.2; checked since stage E)
            var ns = p.Pdf.Add(Dict(("Type", N("Namespace")), ("NS", new PdfString("http://iso.org/pdf2/ssn"u8.ToArray()))));
            p.Element.Set(PdfName.Get("NS"), Ref(ns));
            p.Root.Set(PdfName.Get("Namespaces"), new PdfArray { Ref(ns) });
        });
        Assert.Empty(Check(two).Findings);   // PDF 2.0: profile PDF/UA-2 by itself
        using var noLang = File(p =>
        {
            p.Version = "2.0";
            p.Xmp = p.Xmp.Replace("<pdfuaid:part>1</pdfuaid:part>", "<pdfuaid:part>2</pdfuaid:part><pdfuaid:rev>2024</pdfuaid:rev>", StringComparison.Ordinal);
            p.Catalog.Remove(PdfName.Get("Lang"));
            var spec = p.Pdf.Add(Dict(("Type", N("Filespec")), ("F", new PdfString("daten.xml"u8.ToArray())), ("UF", new PdfString("daten.xml"u8.ToArray()))));
            p.Catalog.Set(PdfName.Get("Names"), Dict(("EmbeddedFiles", Dict(("Names", new PdfArray { new PdfString("daten.xml"u8.ToArray()), Ref(spec) })))));
            p.Catalog.Set(PdfName.Get("AcroForm"), Dict(("Fields", new PdfArray()), ("XFA", p.Pdf.Add(FlateStream("<xdp/>"u8.ToArray())) is var x ? Ref(x) : null!)));
        });
        var found = Check(noLang).Findings.Select(f => f.Id).ToList();
        Assert.Contains("8.4.4-1", found);
        Assert.Contains("8.14.1-1", found);
        Assert.Contains("8.10.1-3", found);
    }

    /// <summary>
    /// The name of a language instead of a tag („English“, the finding of the second reference checker in a reference file): of the right form,
    /// so no error of 7.2-29 — but „Bitte prüfen“ with the tag meant.
    /// </summary>
    [Fact]
    public void LanguageName_IsACheck_WithTheTagMeant()
    {
        using var doc = File(p => p.Catalog.Set(PdfName.Get("Lang"), new PdfString("English"u8.ToArray())));
        var finding = Assert.Single(Check(doc).Findings);
        Assert.Equal("H11", finding.Id);
        Assert.Equal(AccessibilitySeverity.Check, finding.Severity);
        Assert.Equal("en", finding.Suggestion);
        Assert.Equal(AccessibilityRepair.Language, finding.Repair);
    }

    /// <summary>Stage B: a font that is not embedded is reported under PDF/UA's number (7.21.4.1-1, PDF/UA-2 8.4.5.5.1-1).</summary>
    [Fact]
    public void FontNotEmbedded_Found()
    {
        using var doc = File(p =>
        {
            var descriptor = Dict(("Type", N("FontDescriptor")), ("FontName", N("Unbekannt")), ("Flags", PdfInteger.Of(32)), ("FontBBox", Box(0, 0, 1000, 1000)),
                ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)), ("CapHeight", PdfInteger.Of(700)),
                ("StemV", PdfInteger.Of(80)));
            var font = Dict(("Type", N("Font")), ("Subtype", N("TrueType")), ("BaseFont", N("Unbekannt")), ("FirstChar", PdfInteger.Of(65)),
                ("LastChar", PdfInteger.Of(65)), ("Widths", new PdfArray { PdfInteger.Of(600) }), ("Encoding", N("WinAnsiEncoding")), ("FontDescriptor", descriptor));
            p.Page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", font)))));
            p.Content = "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (A) Tj ET EMC";
        });
        var finding = Assert.Single(Check(doc, AccessibilityProfile.PdfUa1).Findings, f => f.Id.StartsWith("7.21", StringComparison.Ordinal));
        Assert.Equal("7.21.4.1-1", finding.Id);
        Assert.Equal("31-009", finding.Matterhorn);
    }

    /// <summary>Stage B, PDF/UA-2: characters of the private use areas in /Alt, /ActualText and in text strings.</summary>
    [Fact]
    public void Ua2_PrivateUse_Found()
    {
        using var doc = File(p =>
        {
            p.Version = "2.0";
            p.Element.Set(PdfName.Get("Alt"), new PdfString(PdfTextEncoding.Encode("")));
            p.Element.Set(PdfName.Get("ActualText"), new PdfString(PdfTextEncoding.Encode("a")));
            p.Element.Set(PdfName.Get("T"), new PdfString(PdfTextEncoding.Encode("")));
        });
        var ids = Check(doc, AccessibilityProfile.PdfUa2).Findings.Select(f => f.Id).ToList();
        Assert.Contains("8.4.3-2", ids);
        Assert.Contains("8.4.3-3", ids);
        Assert.Contains("8.6-1", ids);
    }

    /// <summary>Profile „Nur Hinweise“: the same findings, none an error.</summary>
    [Fact]
    public void HintsOnly_NoErrors()
    {
        using var doc = File(p => p.Catalog.Remove(PdfName.Get("ViewerPreferences")));
        var report = Check(doc, AccessibilityProfile.HintsOnly);
        Assert.Contains(report.Findings, f => f.Id == "7.1-10" && f.Severity == AccessibilitySeverity.Hint);
        Assert.Equal(0, report.Errors);
    }

    [Theory]
    [InlineData("de", true)]
    [InlineData("de-DE", true)]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("gsw", true)]
    [InlineData("x-klingon", true)]
    [InlineData("x-klingonisch", false)]
    [InlineData("", true)]
    [InlineData("English", false)]
    [InlineData("portugue", false)]
    [InlineData("qq", false)]
    [InlineData("de_DE", false)]
    public void LanguageTags_Valid(string tag, bool valid) => Assert.Equal(valid, LanguageTags.Valid(tag));

    [Theory]
    [InlineData("English", "en")]
    [InlineData("Deutsch", "de")]
    [InlineData("de_DE", "de-DE")]
    [InlineData("portugue", null)]
    public void LanguageTags_Proposal(string tag, string? proposal) => Assert.Equal(proposal, LanguageTags.Proposal(tag));

    // ------------------------------------------------------------ stage C: content (docs/FEHLERGEDAECHTNIS.md, No. 629)

    /// <summary>
    /// The file tagged: a paragraph (MCID 0 of the page) under the Document, the parent tree giving it the MCID; <paramref name="paragraph"/>
    /// changes the paragraph element. The content draws a line in the paragraph unless a case says otherwise.
    /// </summary>
    private static PdfDictionary Tag(Parts p, Action<PdfDictionary>? paragraph = null)
    {
        var element = Dict(("Type", N("StructElem")), ("S", N("P")), ("P", p.Root.GetRaw(PdfName.Get("K"))!), ("K", PdfInteger.Of(0)));
        paragraph?.Invoke(element);
        var number = p.Pdf.Add(element);
        p.Element.Set(PdfName.Get("K"), Ref(number));
        p.Root.Set(PdfName.Get("ParentTree"), Dict(("Nums", new PdfArray { PdfInteger.Of(0), new PdfArray { Ref(number) } })));
        p.Root.Set(PdfName.Get("ParentTreeNextKey"), PdfInteger.Of(1));
        p.Page.Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        p.Content = "/P <</MCID 0>> BDC 0 0 m 100 100 l S EMC";
        return element;
    }

    /// <summary>A form XObject named X1 in the page's resources.</summary>
    private static void Form(Parts p, string content, params (string Key, PdfObject Value)[] entries)
    {
        var dict = Dict([("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 100, 100)), .. entries]);
        var form = p.Pdf.Add(FlateStream(Encoding.Latin1.GetBytes(content), dict));
        p.Page.Set(PdfName.Resources, Dict(("XObject", Dict(("X1", Ref(form))))));
    }

    /// <summary>The tagged file with its content in the paragraph and nothing else: no finding of any stage (protection against false alarms).</summary>
    [Fact]
    public void TaggedContent_Conforming_NoFinding()
    {
        using var doc = File(p => Tag(p));
        Assert.Empty(Check(doc, AccessibilityProfile.PdfUa1).Findings);
    }

    public static TheoryData<string, string> ContentCases => new()
    {
        { "Inhalt ungetaggt", "7.1-3" }, { "MCID ohne Element", "7.1-3" }, { "Artefakt in getaggtem Inhalt", "7.1-1" },
        { "Getaggtes in einem Artefakt", "7.1-2" }, { "ActualText ohne Sprache", "7.2-30" }, { "Alt ohne Sprache", "7.2-31" },
        { "E ohne Sprache", "7.2-32" }, { "Referenz-XObject", "7.20-1" }, { "Form mit MCID zweimal", "7.20-2" }, { "Element ohne P", "7.1-12" },
        { "Form ungetaggt", "7.1-3" },
    };

    /// <summary>Each rule of stage C in PDF/UA-1: a tagged file breaking it alone is reported under its number (before: stage C was not built).</summary>
    [Theory]
    [MemberData(nameof(ContentCases))]
    public void Ua1_ContentRule_Found(string name, string id)
    {
        using var doc = File(p =>
        {
            var paragraph = Tag(p);
            switch (name)
            {
                case "Inhalt ungetaggt": p.Content += " 0 0 m 50 50 l S"; break;
                case "MCID ohne Element": p.Content = "/P <</MCID 3>> BDC 0 0 m 100 100 l S EMC"; break;
                case "Artefakt in getaggtem Inhalt": p.Content = "/P <</MCID 0>> BDC /Artifact BMC 0 0 m 1 1 l S EMC EMC"; break;
                case "Getaggtes in einem Artefakt": p.Content = "/Artifact BMC /P <</MCID 0>> BDC 0 0 m 1 1 l S EMC EMC"; break;
                case "ActualText ohne Sprache" or "Alt ohne Sprache" or "E ohne Sprache":
                    p.Catalog.Remove(PdfName.Get("Lang"));
                    p.Content = $"/P <</MCID 0/{name.Split(' ')[0]} (Max Mustermann)>> BDC 0 0 m 1 1 l S EMC";
                    break;
                case "Referenz-XObject":
                    Form(p, "0 0 m 1 1 l S", ("Ref", Dict(("F", new PdfString("andere.pdf"u8.ToArray())), ("Page", PdfInteger.Of(0)))));
                    p.Content = "/Artifact BMC /X1 Do EMC";
                    break;
                case "Form mit MCID zweimal":
                    Form(p, "/P <</MCID 0>> BDC 0 0 m 1 1 l S EMC");
                    p.Content = "/X1 Do /X1 Do";
                    break;
                case "Form ungetaggt":
                    Form(p, "0 0 m 1 1 l S");
                    p.Content += " /X1 Do";
                    break;
                case "Element ohne P": paragraph.Remove(PdfName.Get("P")); break;
                default: throw new ArgumentOutOfRangeException(nameof(name), name);
            }
        });
        var report = Check(doc, AccessibilityProfile.PdfUa1);
        Assert.Contains(report.Findings, f => f.Id == id && f.Severity == AccessibilitySeverity.Error);
    }

    /// <summary>
    /// What stage C must not report: a form drawn in tagged content and in an artifact, a language on the marked content or at the element for
    /// its replacement text, a form with MCIDs drawn once.
    /// </summary>
    [Fact]
    public void Ua1_ContentRules_NotReportedWhereMet()
    {
        using (var doc = File(p =>
               {
                   Tag(p);
                   Form(p, "0 0 m 1 1 l S");
                   p.Content = "/P <</MCID 0>> BDC /X1 Do EMC /Artifact BMC /X1 Do EMC";
               }))
            Assert.Empty(Check(doc, AccessibilityProfile.PdfUa1).Findings);
        using (var doc = File(p =>
               {
                   Tag(p, e => e.Set(PdfName.Get("Lang"), new PdfString("de"u8.ToArray())));
                   p.Catalog.Remove(PdfName.Get("Lang"));
                   p.Content = "/P <</MCID 0/ActualText (Max Mustermann)>> BDC 0 0 m 1 1 l S EMC /Artifact BMC /Span <</Lang (en)/Alt (Erika Mustermann)>> BDC 0 0 m 1 1 l S EMC EMC";
               }))
            Assert.DoesNotContain(Check(doc, AccessibilityProfile.PdfUa1).Findings, f => f.Id is "7.2-30" or "7.2-31" or "7.1-1" or "7.1-2" or "7.1-3");
        using (var doc = File(p =>
               {
                   Tag(p);
                   Form(p, "/P <</MCID 0>> BDC 0 0 m 1 1 l S EMC");
                   p.Content = "/X1 Do";
               }))
            Assert.Empty(Check(doc, AccessibilityProfile.PdfUa1).Findings);
    }

    /// <summary>PDF/UA-2: untagged content under 8.2.2-1; characters of the private use areas without a replacement text under 8.4.3-1, none with one.</summary>
    [Fact]
    public void Ua2_ContentRules()
    {
        static void Ua2(Parts p)
        {
            p.Version = "2.0";
            p.Xmp = p.Xmp.Replace("<pdfuaid:part>1</pdfuaid:part>", "<pdfuaid:part>2</pdfuaid:part><pdfuaid:rev>2024</pdfuaid:rev>", StringComparison.Ordinal);
        }
        using (var doc = File(p =>
               {
                   Ua2(p);
                   Tag(p);
                   p.Content += " 0 0 m 50 50 l S";
               }))
            Assert.Contains(Check(doc).Findings, f => f.Id == "8.2.2-1");
        static void PrivateUse(Parts p, string? actualText)
        {
            Ua2(p);
            Tag(p, e =>
            {
                if (actualText is not null) e.Set(PdfName.Get("ActualText"), new PdfString(PdfTextEncoding.Encode(actualText)));
            });
            var map = "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CMapName /T def\n/CMapType 2 def\n1 begincodespacerange\n<00> <FF>\n" +
                      "endcodespacerange\n1 beginbfchar\n<41> <E000>\nendbfchar\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";
            var font = Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("ToUnicode", Ref(p.Pdf.Add(FlateStream(Encoding.ASCII.GetBytes(map))))));
            p.Page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", font)))));
            p.Content = "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (A) Tj ET EMC";
        }
        using (var doc = File(p => PrivateUse(p, null)))
            Assert.Contains(Check(doc).Findings, f => f.Id == "8.4.3-1");
        using (var doc = File(p => PrivateUse(p, "Haken")))
            Assert.DoesNotContain(Check(doc).Findings, f => f.Id == "8.4.3-1");
    }
}
