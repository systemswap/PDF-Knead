using System.Text;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The check „barrierefrei?“, stage E (docs/ABLAUFPLAN.md, 6.7 step 3; docs/FEHLERGEDAECHTNIS.md, No. 663): the structure tree — role map,
/// figures and formulas, headings, notes, the root, TOCI, lists, MathML, the language of descriptions. Each case breaks one rule; the tree
/// without the break has no finding of stage E. Before stage E nothing of it was checked.
/// </summary>
public sealed class AccessibilityStructureTests
{
    private static PdfString S(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>What a case builds: elements under the Document (type, entries, kids), the role map, the namespaces.</summary>
    private sealed class Tree
    {
        public readonly List<PdfDictionary> Top = [];
        public PdfDictionary? RoleMap;
        public bool CatalogLang = true;
        public bool Pdf20;
        public PdfDictionary? Ns20, DocumentNs;
        public PdfObject? Ns20Ref;
        public bool ExtraRootKid;

        public PdfDictionary El(string type, params object[] parts)
        {
            var e = Dict(("Type", N("StructElem")), ("S", N(type)));
            var kids = new PdfArray();
            foreach (var part in parts)
                switch (part)
                {
                    case PdfDictionary kid:
                        kids.Add(kid);
                        break;
                    case (string key, PdfObject value):
                        e.Set(PdfName.Get(key), value);
                        break;
                }
            if (kids.Count > 0) e.Set(PdfName.Get("K"), kids);
            if (Pdf20 && Ns20Ref is not null && !e.ContainsKey(PdfName.Get("NS"))) e.Set(PdfName.Get("NS"), Ns20Ref);
            return e;
        }
    }

    private static PdfDocument File(Action<Tree> build, bool pdf20 = false)
    {
        var pdf = new TestPdf();
        var pagesNumber = pdf.Add(new PdfDictionary());
        var pageNumber = pdf.Add(new PdfDictionary());
        var rootNumber = pdf.Add(new PdfDictionary());
        var tree = new Tree { Pdf20 = pdf20 };
        if (pdf20)
        {
            // an object of its own: its role map may name it
            tree.Ns20 = Dict(("Type", N("Namespace")), ("NS", S("http://iso.org/pdf2/ssn")));
            tree.Ns20Ref = Ref(pdf.Add(tree.Ns20));
        }
        build(tree);
        var document = Dict(("Type", N("StructElem")), ("S", N("Document")), ("K", new PdfArray([.. tree.Top])));
        if (pdf20) document.Set(PdfName.Get("NS"), tree.DocumentNs ?? tree.Ns20Ref!);
        // every element an object of its own (direct dictionaries naming each other through /P would be a circle), /P and /K as references
        var numbers = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        void Number(PdfDictionary element)
        {
            numbers[element] = pdf.Add(new PdfDictionary());
            if (element.Get(PdfName.Get("K")) is PdfArray kids)
                for (var i = 0; i < kids.Count; i++)
                    if (kids.Get(i) is PdfDictionary kid && kid.ContainsKey(PdfName.Get("S"))) Number(kid);
        }
        Number(document);
        void Link(PdfDictionary element, PdfObject parent)
        {
            element.Set(PdfName.Get("P"), parent);
            if (element.Get(PdfName.Get("K")) is PdfArray kids)
            {
                var refs = new PdfArray();
                for (var i = 0; i < kids.Count; i++)
                {
                    if (kids.Get(i) is PdfDictionary kid && kid.ContainsKey(PdfName.Get("S")))
                    {
                        Link(kid, Ref(numbers[element]));
                        refs.Add(Ref(numbers[kid]));
                    }
                    else refs.Add(kids[i]);
                }
                element.Set(PdfName.Get("K"), refs);
            }
            pdf.Replace(numbers[element], element);
        }
        Link(document, Ref(rootNumber));
        var rootKids = new PdfArray([Ref(numbers[document])]);
        if (tree.ExtraRootKid) rootKids.Add(Ref(pdf.Add(Dict(("Type", N("StructElem")), ("S", N("P")), ("P", Ref(rootNumber))))));
        var root = Dict(("Type", N("StructTreeRoot")), ("K", rootKids));
        if (tree.RoleMap is not null) root.Set(PdfName.Get("RoleMap"), tree.RoleMap);
        if (pdf20) root.Set(PdfName.Get("Namespaces"), new PdfArray([tree.Ns20Ref!]));
        pdf.Replace(rootNumber, root);
        pdf.Replace(pageNumber, Dict(("Type", N("Page")), ("Parent", Ref(pagesNumber)), ("MediaBox", Box(0, 0, 595, 842)), ("Resources", new PdfDictionary()),
            ("Contents", Ref(pdf.Add(FlateStream("/Artifact BMC 0 0 m 100 100 l S EMC"u8.ToArray()))))));
        pdf.Replace(pagesNumber, Dict(("Type", N("Pages")), ("Kids", new PdfArray { Ref(pageNumber) }), ("Count", PdfInteger.Of(1))));
        var catalog = Dict(("Type", N("Catalog")), ("Pages", Ref(pagesNumber)), ("MarkInfo", Dict(("Marked", PdfBoolean.Of(true)))), ("StructTreeRoot", Ref(rootNumber)));
        if (tree.CatalogLang) catalog.Set(PdfName.Get("Lang"), S("de"));
        pdf.RootNumber = pdf.Add(catalog);
        var bytes = pdf.Build();
        if (pdf20) bytes = [.. "%PDF-2.0"u8.ToArray(), .. bytes.AsSpan(8)];
        return PdfDocument.Open(bytes);
    }

    private static List<string> Ids(PdfDocument doc, AccessibilityProfile profile) => [.. AccessibilityCheck.Check(doc, profile).Findings.Select(f => f.Id)];

    public static TheoryData<string, string> Ua1Cases => new()
    {
        { "eigene Art ohne Abbildung", "7.1-5" }, { "Kreis", "7.1-6" }, { "Standardart umgedeutet", "7.1-7" },
        { "ActualText ohne Sprache", "7.2-21" }, { "Alt ohne Sprache", "7.2-22" }, { "E ohne Sprache", "7.2-23" },
        { "Figure ohne Alt", "7.3-1" }, { "Figure mit leerem Alt", "7.3-1" }, { "H1 dann H3", "7.4.2-1" }, { "erste H2", "7.4.2-1" },
        { "zwei H in einem Element", "7.4.4-1" }, { "H und H1", "7.4.4-2" }, { "H1 und H", "7.4.4-3" }, { "Formula ohne Alt", "7.7-1" },
        { "Note ohne ID", "7.9-1" }, { "Note mit gleicher ID", "7.9-2" },
    };

    private static void Ua1(Tree t, string name)
    {
        switch (name)
        {
            case "eigene Art ohne Abbildung": t.Top.Add(t.El("Absatz")); break;
            case "Kreis":
                t.RoleMap = Dict(("Absatz", N("Text")), ("Text", N("Absatz")));
                t.Top.Add(t.El("Absatz"));
                break;
            case "Standardart umgedeutet":
                t.RoleMap = Dict(("P", N("Span")));
                t.Top.Add(t.El("P"));
                break;
            case "ActualText ohne Sprache": t.CatalogLang = false; t.Top.Add(t.El("Span", ("ActualText", S("Max Mustermann")))); break;
            case "Alt ohne Sprache": t.CatalogLang = false; t.Top.Add(t.El("Figure", ("Alt", S("Foto")))); break;
            case "E ohne Sprache": t.CatalogLang = false; t.Top.Add(t.El("Span", ("E", S("zum Beispiel")))); break;
            case "Figure ohne Alt": t.Top.Add(t.El("Figure")); break;
            case "Figure mit leerem Alt": t.Top.Add(t.El("Figure", ("Alt", S("")))); break;
            case "H1 dann H3": t.Top.AddRange([t.El("H1"), t.El("H3")]); break;
            case "erste H2": t.Top.Add(t.El("H2")); break;
            case "zwei H in einem Element": t.Top.Add(t.El("Sect", t.El("H"), t.El("H"))); break;
            case "H und H1":
            case "H1 und H": t.Top.AddRange([t.El("H1"), t.El("Sect", t.El("H"))]); break;
            case "Formula ohne Alt": t.Top.Add(t.El("Formula")); break;
            case "Note ohne ID": t.Top.Add(t.El("Note")); break;
            case "Note mit gleicher ID": t.Top.AddRange([t.El("Note", ("ID", S("n1"))), t.El("Note", ("ID", S("n1")))]); break;
            case "ohne Bruch":
                t.RoleMap = Dict(("Absatz", N("P")));
                t.Top.AddRange([t.El("H1"), t.El("H2"), t.El("Absatz"), t.El("Figure", ("Alt", S("Foto"))), t.El("Formula", ("ActualText", S("a+b"))),
                    t.El("Note", ("ID", S("n1"))), t.El("Note", ("ID", S("n2"))), t.El("H2"), t.El("H3"), t.El("H1")]);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    [Theory]
    [MemberData(nameof(Ua1Cases))]
    public void Ua1_EachStructureRule_Found(string name, string id)
    {
        using var doc = File(t => Ua1(t, name));
        Assert.Contains(id, Ids(doc, AccessibilityProfile.PdfUa1));
    }

    private static bool StageE(string id) =>
        id is "7.1-5" or "7.1-6" or "7.1-7" or "7.2-21" or "7.2-22" or "7.2-23" or "7.3-1" or "7.7-1" or "7.9-1" or "7.9-2" ||
        id.StartsWith("7.4.", StringComparison.Ordinal) || id.StartsWith("8.2.4", StringComparison.Ordinal) || id.StartsWith("8.2.5", StringComparison.Ordinal);

    /// <summary>A tree meeting the rules (mapped type, headings in order with a step back, described figure and formula, notes with own IDs): nothing of stage E.</summary>
    [Fact]
    public void Ua1_Conforming_NothingOfStageE()
    {
        using var doc = File(t => Ua1(t, "ohne Bruch"));
        Assert.DoesNotContain(Ids(doc, AccessibilityProfile.PdfUa1), StageE);
    }

    public static TheoryData<string, string> Ua2Cases => new()
    {
        { "eigene Art ohne Abbildung", "8.2.4-1" }, { "Kreis im Namensraum", "8.2.4-2" }, { "Abbildung im selben Namensraum", "8.2.4-3" },
        { "Standardart umgedeutet", "8.2.4-4" }, { "zweites Kind der Wurzel", "8.2.5.2-1" }, { "Document im Namensraum 1.7", "8.2.5.2-2" },
        { "TOCI ohne Ref", "8.2.5.8-1" }, { "H", "8.2.5.12-1" }, { "Note", "8.2.5.14-1" }, { "FENote mit falscher Art", "8.2.5.14-4" },
        { "Liste mit Lbl ohne Nummerierung", "8.2.5.25-1" }, { "Figure ohne Alt", "8.2.5.28.2-1" }, { "math ohne Formula", "8.2.5.29-1" },
    };

    private static void Ua2(Tree t, string name)
    {
        switch (name)
        {
            case "eigene Art ohne Abbildung": t.Top.Add(t.El("Absatz")); break;
            case "Kreis im Namensraum":
                t.Ns20!.Set(PdfName.Get("RoleMapNS"), Dict(("Absatz", new PdfArray([N("Absatz"), t.Ns20Ref!]))));
                t.Top.Add(t.El("Absatz"));
                break;
            case "Abbildung im selben Namensraum":
            {
                var xhtml = Dict(("Type", N("Namespace")), ("NS", S("http://www.w3.org/1999/xhtml")), ("RoleMapNS", Dict(("T", new PdfArray([N("P"), t.Ns20Ref!])))));
                t.Ns20!.Set(PdfName.Get("RoleMapNS"), Dict(("Absatz", new PdfArray([N("T"), xhtml]))));
                t.Top.Add(t.El("Absatz"));
                break;
            }
            case "Standardart umgedeutet":
                t.Ns20!.Set(PdfName.Get("RoleMapNS"), Dict(("P", new PdfArray([N("Span"), t.Ns20Ref!]))));
                t.Top.Add(t.El("P"));
                break;
            case "zweites Kind der Wurzel": t.ExtraRootKid = true; t.Top.Add(t.El("P")); break;
            case "Document im Namensraum 1.7": t.DocumentNs = Dict(("Type", N("Namespace")), ("NS", S("http://iso.org/pdf/ssn"))); t.Top.Add(t.El("P")); break;
            case "TOCI ohne Ref": t.Top.Add(t.El("TOC", ("NS", Dict(("Type", N("Namespace")), ("NS", S("http://iso.org/pdf/ssn")))), t.El("TOCI", ("NS", Dict(("Type", N("Namespace")), ("NS", S("http://iso.org/pdf/ssn"))))))); break;
            case "H": t.Top.Add(t.El("H")); break;
            case "Note": t.Top.Add(t.El("Note", ("NS", Dict(("Type", N("Namespace")), ("NS", S("http://iso.org/pdf/ssn")))))); break;
            case "FENote mit falscher Art": t.Top.Add(t.El("FENote", ("A", Dict(("O", N("Layout")), ("NoteType", N("Randnotiz")))))); break;
            case "Liste mit Lbl ohne Nummerierung": t.Top.Add(t.El("L", t.El("LI", t.El("Lbl"), t.El("LBody")))); break;
            case "Figure ohne Alt": t.Top.Add(t.El("Figure")); break;
            case "math ohne Formula":
                t.Top.Add(t.El("Div", t.El("math", ("NS", Dict(("Type", N("Namespace")), ("NS", S("http://www.w3.org/1998/Math/MathML")))))));
                break;
            case "ohne Bruch":
            {
                var mathNs = Dict(("Type", N("Namespace")), ("NS", S("http://www.w3.org/1998/Math/MathML")));
                t.Top.AddRange([t.El("H1"), t.El("P"), t.El("Figure", ("Alt", S(""))), t.El("FENote", ("A", Dict(("O", N("Layout")), ("NoteType", N("Footnote"))))),
                    t.El("L", ("A", Dict(("O", N("List")), ("ListNumbering", N("Disc")))), t.El("LI", t.El("Lbl"), t.El("LBody"))),
                    t.El("Formula", ("Alt", S("a+b")), t.El("math", ("NS", mathNs), t.El("mi", ("NS", mathNs)))),
                    t.El("Aside", t.El("Title"))]);
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    [Theory]
    [MemberData(nameof(Ua2Cases))]
    public void Ua2_EachStructureRule_Found(string name, string id)
    {
        using var doc = File(t => Ua2(t, name), pdf20: true);
        Assert.Contains(id, Ids(doc, AccessibilityProfile.PdfUa2));
    }

    /// <summary>A 2.0 tree meeting the rules (Figure with an empty /Alt counts in PDF/UA-2, a numbered list, MathML in a formula): nothing of stage E.</summary>
    [Fact]
    public void Ua2_Conforming_NothingOfStageE()
    {
        using var doc = File(t => Ua2(t, "ohne Bruch"), pdf20: true);
        Assert.DoesNotContain(Ids(doc, AccessibilityProfile.PdfUa2), StageE);
    }
}
