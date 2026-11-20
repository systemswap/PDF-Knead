using System.Text;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The check „barrierefrei?“, stage F (docs/ABLAUFPLAN.md, 6.7 step 3; docs/FEHLERGEDAECHTNIS.md, No. 665): annotations and their place in the
/// structure tree. Each case breaks one rule; the conforming page has no finding of stage F. Before stage F nothing of it was checked.
/// </summary>
public sealed class AccessibilityAnnotationTreeTests
{
    private static PdfString S(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>An annotation and the element type it hangs in (null: not in the tree), the element's own entries, the element's kids beyond it.</summary>
    private sealed record Placed(PdfDictionary Annot, string? Element, (string Key, PdfObject Value)[]? ElementEntries = null, string? Group = null);

    /// <summary>
    /// A tagged page (/Tabs /S) with the annotations placed; annotations of the same <see cref="Placed.Group"/> share one element; fields go into
    /// /AcroForm.
    /// </summary>
    private static PdfDocument File(string version, params Placed[] placed)
    {
        var pdf = new TestPdf();
        var pagesNumber = pdf.Add(new PdfDictionary());
        var pageNumber = pdf.Add(new PdfDictionary());
        var rootNumber = pdf.Add(new PdfDictionary());
        var documentNumber = pdf.Add(new PdfDictionary());
        var ns = version == "2.0" ? Ref(pdf.Add(Dict(("Type", N("Namespace")), ("NS", S("http://iso.org/pdf2/ssn"))))) : null;
        var annots = new PdfArray();
        var documentKids = new PdfArray();
        var nums = new PdfArray();
        var groups = new Dictionary<string, (int Number, PdfDictionary Element)>();
        var fields = new PdfArray();
        var key = 0;
        // streams nested in an annotation (appearances, their forms and images) as objects of their own, as PDF requires
        void Lift(PdfDictionary dict, int depth)
        {
            foreach (var (key, value) in dict.Entries.ToList())
                switch (value)
                {
                    case PdfStream stream when depth < 8:
                        Lift(stream.Dictionary, depth + 1);
                        dict.Set(key, Ref(pdf.Add(stream)));
                        break;
                    case PdfDictionary inner when depth < 8:
                        Lift(inner, depth + 1);
                        break;
                }
        }
        foreach (var p in placed)
        {
            Lift(p.Annot, 0);
            var annotNumber = pdf.Add(p.Annot);
            p.Annot.Set(PdfName.Get("P"), Ref(pageNumber));
            annots.Add(Ref(annotNumber));
            if (p.Annot.ContainsKey(PdfName.Get("FT"))) fields.Add(Ref(annotNumber));
            if (p.Element is null) continue;
            var objr = Dict(("Type", N("OBJR")), ("Obj", Ref(annotNumber)), ("Pg", Ref(pageNumber)));
            if (p.Group is not null && groups.TryGetValue(p.Group, out var shared))
            {
                ((PdfArray)shared.Element.Get(PdfName.Get("K"))!).Add(objr);
                p.Annot.Set(PdfName.Get("StructParent"), PdfInteger.Of(key));
                nums.Add(PdfInteger.Of(key++));
                nums.Add(Ref(shared.Number));
                continue;
            }
            var element = Dict(("Type", N("StructElem")), ("S", N(p.Element)), ("P", Ref(documentNumber)), ("Pg", Ref(pageNumber)), ("K", new PdfArray([objr])));
            if (ns is not null) element.Set(PdfName.Get("NS"), ns);
            foreach (var (k, v) in p.ElementEntries ?? []) element.Set(PdfName.Get(k), v);
            var elementNumber = pdf.Add(element);
            if (p.Group is not null) groups[p.Group] = (elementNumber, element);
            documentKids.Add(Ref(elementNumber));
            p.Annot.Set(PdfName.Get("StructParent"), PdfInteger.Of(key));
            nums.Add(PdfInteger.Of(key++));
            nums.Add(Ref(elementNumber));
        }
        var document = Dict(("Type", N("StructElem")), ("S", N("Document")), ("P", Ref(rootNumber)), ("K", documentKids));
        if (ns is not null) document.Set(PdfName.Get("NS"), ns);
        pdf.Replace(documentNumber, document);
        var root = Dict(("Type", N("StructTreeRoot")), ("K", Ref(documentNumber)), ("ParentTree", Dict(("Nums", nums))), ("ParentTreeNextKey", PdfInteger.Of(key)));
        if (ns is not null) root.Set(PdfName.Get("Namespaces"), new PdfArray([ns]));
        pdf.Replace(rootNumber, root);
        pdf.Replace(pageNumber, Dict(("Type", N("Page")), ("Parent", Ref(pagesNumber)), ("MediaBox", Box(0, 0, 595, 842)), ("Resources", new PdfDictionary()),
            ("Tabs", N("S")), ("Annots", annots), ("Contents", Ref(pdf.Add(FlateStream("/Artifact BMC 0 0 m 100 100 l S EMC"u8.ToArray()))))));
        pdf.Replace(pagesNumber, Dict(("Type", N("Pages")), ("Kids", new PdfArray { Ref(pageNumber) }), ("Count", PdfInteger.Of(1))));
        var catalog = Dict(("Type", N("Catalog")), ("Pages", Ref(pagesNumber)), ("Lang", S("de")), ("MarkInfo", Dict(("Marked", PdfBoolean.Of(true)))),
            ("StructTreeRoot", Ref(rootNumber)));
        if (fields.Count > 0) catalog.Set(PdfName.Get("AcroForm"), Dict(("Fields", fields)));
        pdf.RootNumber = pdf.Add(catalog);
        var bytes = pdf.Build();
        if (version != "1.7") bytes = [.. Encoding.ASCII.GetBytes("%PDF-" + version), .. bytes.AsSpan(8)];
        return PdfDocument.Open(bytes);
    }

    private static PdfDictionary Annot(string subtype, params (string Key, PdfObject Value)[] entries)
    {
        var annot = Dict(("Type", N("Annot")), ("Subtype", N(subtype)), ("Rect", Box(100, 100, 200, 130)), ("F", PdfInteger.Of(4)));
        foreach (var (key, value) in entries) annot.Set(PdfName.Get(key), value);
        return annot;
    }

    private static PdfDictionary Field(params (string Key, PdfObject Value)[] entries) =>
        Annot("Widget", [("FT", N("Tx")), ("T", S("name")), .. entries]);

    private static List<string> Ids(PdfDocument doc, AccessibilityProfile profile) => [.. AccessibilityCheck.Check(doc, profile).Findings.Select(f => f.Id)];

    public static TheoryData<string, string> Ua1Cases => new()
    {
        { "Markierung in P", "7.18.1-1" }, { "Feld ohne TU und Alt", "7.18.1-3" }, { "TU nur am Optionsfeld", "7.18.1-3" }, { "Feld in P", "7.18.4-1" },
        { "Form mit zwei Feldern", "7.18.4-2" }, { "Link in P", "7.18.5-1" }, { "Druckmarke im Baum", "7.18.8-1" },
    };

    private static Placed[] Ua1(string name)
    {
        switch (name)
        {
            case "Markierung in P": return [new(Annot("Highlight", ("Contents", S("wichtig"))), "P")];
            case "Feld ohne TU und Alt": return [new(Field(), "Form")];
            case "TU nur am Optionsfeld":
            {
                // a radio group: the field without /TU, its widget (no field of its own) with one — not the group's tooltip
                var group = Dict(("FT", N("Btn")), ("Ff", PdfInteger.Of(49152)), ("T", S("auswahl")));
                var widget = Annot("Widget", ("Parent", group), ("TU", S("Ja")));
                return [new(widget, "Form")];
            }
            case "Feld in P": return [new(Field(("TU", S("Name"))), "P")];
            case "Form mit zwei Feldern":
                return [new(Field(("TU", S("Vorname"))), "Form", Group: "f"), new(Field(("TU", S("Nachname")), ("T", S("nach"))), "Form", Group: "f")];
            case "Link in P": return [new(Annot("Link", ("Contents", S("Startseite"))), "P")];
            case "Druckmarke im Baum": return [new(Annot("PrinterMark", ("Contents", S("Passkreuz"))), "Annot")];
            case "ohne Bruch":
                return [new(Annot("Highlight", ("Contents", S("wichtig"))), "Annot"), new(Field(("TU", S("Name"))), "Form"),
                    new(Annot("Link", ("Contents", S("Startseite"))), "Link"), new(Annot("PrinterMark", ("Contents", S("Passkreuz"))), null)];
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    [Theory]
    [MemberData(nameof(Ua1Cases))]
    public void Ua1_EachRule_Found(string name, string id)
    {
        using var doc = File("1.7", Ua1(name));
        Assert.Contains(id, Ids(doc, AccessibilityProfile.PdfUa1));
    }

    [Fact]
    public void Ua1_Conforming_NothingOfStageF()
    {
        using var doc = File("1.7", Ua1("ohne Bruch"));
        Assert.DoesNotContain(Ids(doc, AccessibilityProfile.PdfUa1), id => id is "7.18.1-1" or "7.18.1-3" or "7.18.4-1" or "7.18.4-2" or "7.18.5-1" or "7.18.8-1");
    }

    public static TheoryData<string, string> Ua2Cases => new()
    {
        { "unsichtbar im Baum", "8.9.2.2-1" }, { "NoView im Baum", "8.9.2.2-2" }, { "Kommentar in P", "8.9.2.3-1" }, { "Feld ohne Fläche", "8.9.2.4.13-1" },
        { "Druckmarke als Inhalt", "8.9.2.4.14-1" }, { "Wasserzeichen in P", "8.9.2.4.16-1" }, { "Contents anders als Alt", "8.9.4.2-1" },
        { "Feld in P", "8.10.1-1" }, { "Form mit zwei Feldern", "8.10.1-2" }, { "Feld ohne Lbl und Contents", "8.10.2.3-1" }, { "Link in P", "8.2.5.20-1" },
        { "Link mit zwei Zielen", "8.2.5.20-2" }, { "Anmerkung nicht im Baum", "8.9.2.1-1" },
    };

    private static Placed[] Ua2(string name)
    {
        switch (name)
        {
            case "unsichtbar im Baum": return [new(Annot("Text", ("F", PdfInteger.Of(5)), ("Contents", S("x"))), "Annot")];
            case "NoView im Baum": return [new(Annot("Text", ("F", PdfInteger.Of(36)), ("Contents", S("x"))), "Annot")];
            case "Kommentar in P": return [new(Annot("Text", ("Contents", S("Notiz"))), "P")];
            case "Feld ohne Fläche": return [new(Field(("Rect", Box(100, 100, 100, 100)), ("Contents", S("Name"))), "Form")];
            case "Druckmarke als Inhalt": return [new(Annot("PrinterMark"), "Annot")];
            case "Wasserzeichen in P": return [new(Annot("Watermark"), "P")];
            case "Contents anders als Alt": return [new(Annot("Text", ("Contents", S("Notiz"))), "Annot", [("Alt", S("Anmerkung"))])];
            case "Feld in P": return [new(Field(("Contents", S("Name"))), "P")];
            case "Form mit zwei Feldern":
                return [new(Field(("Contents", S("Vorname"))), "Form", Group: "f"), new(Field(("Contents", S("Nachname")), ("T", S("nach"))), "Form", Group: "f")];
            case "Feld ohne Lbl und Contents": return [new(Field(), "Form")];
            case "Link in P": return [new(Annot("Link", ("Contents", S("Startseite"))), "P")];
            case "Link mit zwei Zielen":
                return [new(Annot("Link", ("Contents", S("a")), ("A", Dict(("S", N("URI")), ("URI", S("https://example.org/a"))))), "Link", Group: "l"),
                    new(Annot("Link", ("Contents", S("b")), ("A", Dict(("S", N("URI")), ("URI", S("https://example.org/b"))))), "Link", Group: "l")];
            case "Anmerkung nicht im Baum": return [new(Annot("Text", ("Contents", S("Notiz"))), null)];
            case "ohne Bruch":
                return [new(Annot("Text", ("Contents", S("Notiz"))), "Annot", [("Alt", S("Notiz"))]), new(Field(("Contents", S("Name"))), "Form"),
                    new(Annot("Link", ("Contents", S("a")), ("A", Dict(("S", N("URI")), ("URI", S("https://example.org/a"))))), "Link", Group: "l"),
                    new(Annot("Link", ("Contents", S("a")), ("A", Dict(("S", N("URI")), ("URI", S("https://example.org/a"))))), "Link", Group: "l"),
                    new(Annot("Popup"), null), new(Annot("Text", ("F", PdfInteger.Of(5)), ("Contents", S("x"))), null)];
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    [Theory]
    [MemberData(nameof(Ua2Cases))]
    public void Ua2_EachRule_Found(string name, string id)
    {
        using var doc = File("2.0", Ua2(name));
        Assert.Contains(id, Ids(doc, AccessibilityProfile.PdfUa2));
    }

    /// <summary>The conforming page (pop-up and invisible annotation outside the tree, two links to the same target in one Link): nothing of stage F.</summary>
    [Fact]
    public void Ua2_Conforming_NothingOfStageF()
    {
        using var doc = File("2.0", Ua2("ohne Bruch"));
        Assert.DoesNotContain(Ids(doc, AccessibilityProfile.PdfUa2), id => id.StartsWith("8.9.2.", StringComparison.Ordinal) || id.StartsWith("8.9.4", StringComparison.Ordinal) ||
                                                                         id.StartsWith("8.10.1", StringComparison.Ordinal) || id == "8.10.2.3-1" || id.StartsWith("8.2.5.20", StringComparison.Ordinal));
    }

    /// <summary>A visible signature whose appearance shows a picture (an image XObject drawn by a form in the appearance).</summary>
    private static PdfDictionary SignatureWithPicture(params (string Key, PdfObject Value)[] entries)
    {
        var image = FlateStream([0, 0, 0], Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(1)), ("Height", PdfInteger.Of(1)),
            ("ColorSpace", N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8))));
        var inner = FlateStream("/Im0 Do"u8.ToArray(), Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 100, 30)),
            ("Resources", Dict(("XObject", Dict(("Im0", image)))))));
        var appearance = FlateStream("/Fm0 Do"u8.ToArray(), Dict(("Type", N("XObject")), ("Subtype", N("Form")), ("BBox", Box(0, 0, 100, 30)),
            ("Resources", Dict(("XObject", Dict(("Fm0", inner)))))));
        return Annot("Widget", [("FT", N("Sig")), ("T", S("unterschrift")), ("AP", Dict(("N", appearance))), .. entries]);
    }

    /// <summary>
    /// PDF/UA-2, 8.10.3.5 (docs/FEHLERGEDAECHTNIS.md, No. 671): the picture of a visible signature without /Alt and /Contents is a sign to check
    /// („Bitte prüfen“); either description, or a signature without a picture, is none.
    /// </summary>
    [Fact]
    public void Ua2_SignaturePictureWithoutDescription_ASignToCheck()
    {
        using (var doc = File("2.0", new Placed(SignatureWithPicture(), "Form")))
        {
            var finding = Assert.Single(AccessibilityCheck.Check(doc, AccessibilityProfile.PdfUa2).Findings, f => f.Id == "8.10.3.5-1");
            Assert.Equal(AccessibilitySeverity.Check, finding.Severity);
        }
        using (var doc = File("2.0", new Placed(SignatureWithPicture(("Contents", S("Digitale Signatur"))), "Form")))
            Assert.DoesNotContain("8.10.3.5-1", Ids(doc, AccessibilityProfile.PdfUa2));
        using (var doc = File("2.0", new Placed(SignatureWithPicture(), "Form", [("Alt", S("Unterschrift von Erika Mustermann"))])))
            Assert.DoesNotContain("8.10.3.5-1", Ids(doc, AccessibilityProfile.PdfUa2));
        using (var doc = File("2.0", new Placed(Annot("Widget", ("FT", N("Sig")), ("T", S("leer"))), "Form")))
            Assert.DoesNotContain("8.10.3.5-1", Ids(doc, AccessibilityProfile.PdfUa2));
        // PDF/UA-1 has no such rule
        using (var doc = File("1.7", new Placed(SignatureWithPicture(), "Form")))
            Assert.DoesNotContain(AccessibilityCheck.Check(doc, AccessibilityProfile.PdfUa1).Findings, f => f.Id.StartsWith("8.10.3.5", StringComparison.Ordinal));
    }
}
