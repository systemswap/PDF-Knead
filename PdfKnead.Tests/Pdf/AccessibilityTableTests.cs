using System.Text;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The check „barrierefrei?“, stage G (docs/ABLAUFPLAN.md, 6.7 step 3; docs/FEHLERGEDAECHTNIS.md, No. 666): containment and tables — where
/// table, list and contents elements stand and what they hold, the table grid (overlapping cells, rows and columns of different reach), headers
/// of data cells, Ruby and Warichu, content of list items, captions, the references of notes, the containment rules of ISO/TS 32005. Each case
/// breaks one rule; the trees without a break have no finding of stage G. Before stage G nothing of it was checked.
/// </summary>
public sealed class AccessibilityTableTests
{
    private static PdfString S(string text) => new(Encoding.UTF8.GetBytes(text));

    private static int _mcid;

    /// <summary>An element: parts are kids (elements), marked content (<see cref="C"/>) or entries (key, value).</summary>
    private static PdfDictionary El(string type, params object[] parts)
    {
        var e = Dict(("Type", N("StructElem")), ("S", N(type)));
        var kids = new PdfArray();
        foreach (var part in parts)
            switch (part)
            {
                case PdfObject kid:
                    kids.Add(kid);
                    break;
                case (string key, PdfObject value):
                    e.Set(PdfName.Get(key), value);
                    break;
            }
        if (kids.Count > 0) e.Set(PdfName.Get("K"), kids);
        return e;
    }

    /// <summary>A content item (a marked-content number).</summary>
    private static PdfInteger C() => PdfInteger.Of(_mcid++);

    /// <summary>Table attributes: RowSpan, ColSpan, Scope, Headers.</summary>
    private static (string, PdfObject) Table(int rowSpan = 1, int colSpan = 1, string? scope = null, params string[] headers)
    {
        var a = Dict(("O", N("Table")));
        if (rowSpan != 1) a.Set(PdfName.Get("RowSpan"), PdfInteger.Of(rowSpan));
        if (colSpan != 1) a.Set(PdfName.Get("ColSpan"), PdfInteger.Of(colSpan));
        if (scope is not null) a.Set(PdfName.Get("Scope"), N(scope));
        if (headers.Length > 0) a.Set(PdfName.Get("Headers"), new PdfArray([.. headers.Select(S)]));
        return ("A", a);
    }

    private static PdfDictionary Td(params object[] parts) => El("TD", [C(), .. parts]);
    private static PdfDictionary Th(string? scope = "Column", params object[] parts) => El("TH", [C(), .. (scope is null ? parts : [Table(scope: scope), .. parts])]);

    /// <summary>A file with the elements under one Document; every element an object of its own, /K, /P and /Ref as references.</summary>
    private static PdfDocument File(bool pdf20, params PdfDictionary[] top)
    {
        var pdf = new TestPdf();
        var pagesNumber = pdf.Add(new PdfDictionary());
        var pageNumber = pdf.Add(new PdfDictionary());
        var rootNumber = pdf.Add(new PdfDictionary());
        PdfObject? ns = pdf20 ? Ref(pdf.Add(Dict(("Type", N("Namespace")), ("NS", S("http://iso.org/pdf2/ssn"))))) : null;
        var document = El("Document", [.. top]);
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
            if (ns is not null) element.Set(PdfName.Get("NS"), ns);
            if (element.Get(PdfName.Get("Ref")) is PdfArray targets)
                element.Set(PdfName.Get("Ref"), new PdfArray([.. Enumerable.Range(0, targets.Count).Select(i => (PdfObject)Ref(numbers[(PdfDictionary)targets.Get(i)!]))]));
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
            if (element.ContainsKey(PdfName.Get("K"))) element.Set(PdfName.Get("Pg"), Ref(pageNumber));
            pdf.Replace(numbers[element], element);
        }
        Link(document, Ref(rootNumber));
        var root = Dict(("Type", N("StructTreeRoot")), ("K", new PdfArray([Ref(numbers[document])])));
        if (ns is not null) root.Set(PdfName.Get("Namespaces"), new PdfArray([ns]));
        pdf.Replace(rootNumber, root);
        pdf.Replace(pageNumber, Dict(("Type", N("Page")), ("Parent", Ref(pagesNumber)), ("MediaBox", Box(0, 0, 595, 842)), ("Resources", new PdfDictionary()),
            ("Contents", Ref(pdf.Add(FlateStream("/Artifact BMC 0 0 m 100 100 l S EMC"u8.ToArray()))))));
        pdf.Replace(pagesNumber, Dict(("Type", N("Pages")), ("Kids", new PdfArray { Ref(pageNumber) }), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(Dict(("Type", N("Catalog")), ("Pages", Ref(pagesNumber)), ("Lang", S("de")), ("MarkInfo", Dict(("Marked", PdfBoolean.Of(true)))),
            ("StructTreeRoot", Ref(rootNumber))));
        var bytes = pdf.Build();
        if (pdf20) bytes = [.. "%PDF-2.0"u8.ToArray(), .. bytes.AsSpan(8)];
        return PdfDocument.Open(bytes);
    }

    private static List<string> Ids(PdfDocument doc, AccessibilityProfile profile) => [.. AccessibilityCheck.Check(doc, profile).Findings.Select(f => f.Id)];

    private static PdfDictionary Row(params PdfDictionary[] cells) => El("TR", [.. cells]);

    public static TheoryData<string, string> Ua1Cases => new()
    {
        { "Table mit P", "7.2-3" }, { "TR frei", "7.2-4" }, { "THead frei", "7.2-5" }, { "TBody frei", "7.2-6" }, { "TFoot frei", "7.2-7" },
        { "TH frei", "7.2-8" }, { "TD frei", "7.2-9" }, { "TR mit P", "7.2-10" }, { "zwei THead", "7.2-11" }, { "zwei TFoot", "7.2-12" },
        { "TFoot ohne TBody", "7.2-13" }, { "THead ohne TBody", "7.2-14" }, { "Zellen überdecken sich", "7.2-15" }, { "Caption in der Mitte", "7.2-16" },
        { "LI frei", "7.2-17" }, { "LBody frei", "7.2-18" }, { "L mit P", "7.2-19" }, { "LI mit P", "7.2-20" }, { "TOCI frei", "7.2-26" },
        { "TOC mit P", "7.2-27" }, { "TOC Caption hinten", "7.2-28" }, { "TOC zwei Caption", "7.2-28" }, { "THead mit P", "7.2-36" },
        { "TBody mit P", "7.2-37" }, { "TFoot mit P", "7.2-38" }, { "zwei Caption", "7.2-39" }, { "L Caption hinten", "7.2-40" },
        { "Spalte zu lang", "7.2-41" }, { "Zeile zu breit", "7.2-42" }, { "Zeile zu schmal", "7.2-43" }, { "TH ohne Scope", "7.5-1" },
        { "Headers unbekannt", "7.5-2" },
    };

    /// <summary>The cases of both parts (the tables) and of PDF/UA-1.</summary>
    private static PdfDictionary[] Case(string name) => name switch
    {
        "Table mit P" => [El("Table", Row(Td()), El("P", C()))],
        "TR frei" => [Row(Td())],
        "THead frei" => [El("THead", Row(Td()))],
        "TBody frei" => [El("TBody", Row(Td()))],
        "TFoot frei" => [El("TFoot", Row(Td()))],
        "TH frei" => [Th()],
        "TD frei" => [Td()],
        "TR mit P" => [El("Table", El("TR", Td(), El("P", C())))],
        "zwei THead" => [El("Table", El("THead", Row(Td())), El("THead", Row(Td())), El("TBody", Row(Td())))],
        "zwei TFoot" => [El("Table", El("TBody", Row(Td())), El("TFoot", Row(Td())), El("TFoot", Row(Td())))],
        "TFoot ohne TBody" => [El("Table", Row(Td()), El("TFoot", Row(Td())))],
        "THead ohne TBody" => [El("Table", El("THead", Row(Td())), Row(Td()))],
        // row 1: TD, TD over two rows; row 2: a TD over two columns lands on the second
        "Zellen überdecken sich" => [El("Table", Row(Td(), Td(Table(rowSpan: 2))), Row(Td(Table(colSpan: 2))))],
        "Caption in der Mitte" => [El("Table", Row(Td()), El("Caption", C()), Row(Td()))],
        "LI frei" => [El("LI", El("LBody", C()))],
        "LBody frei" => [El("LBody", C())],
        "L mit P" => [El("L", El("LI", El("LBody", C())), El("P", C()))],
        "LI mit P" => [El("L", El("LI", El("P", C())))],
        "TOCI frei" => [El("TOCI", C())],
        "TOC mit P" => [El("TOC", El("TOCI", C()), El("P", C()))],
        "TOC Caption hinten" => [El("TOC", El("TOCI", C()), El("Caption", C()))],
        "TOC zwei Caption" => [El("TOC", El("Caption", C()), El("TOCI", C()), El("Caption", C()))],
        "THead mit P" => [El("Table", El("THead", Row(Td()), El("P", C())), El("TBody", Row(Td())))],
        "TBody mit P" => [El("Table", El("TBody", Row(Td()), El("P", C())))],
        "TFoot mit P" => [El("Table", El("TBody", Row(Td())), El("TFoot", Row(Td()), El("P", C())))],
        "zwei Caption" => [El("Table", El("Caption", C()), Row(Td()), El("Caption", C()))],
        "L Caption hinten" => [El("L", El("LI", El("LBody", C())), El("Caption", C()))],
        // the first column reaches over three rows, the table has two
        "Spalte zu lang" => [El("Table", Row(Td(Table(rowSpan: 3)), Td()), Row(Td()))],
        "Zeile zu breit" => [El("Table", Row(Td(), Td()), Row(Td(), Td(), Td()))],
        "Zeile zu schmal" => [El("Table", Row(Td(), Td()), Row(Td()))],
        "TH ohne Scope" => [El("Table", Row(Th(null), Th(null)), Row(Td(), Td()))],
        "Headers unbekannt" => [El("Table", Row(Th(null), Th(null)), Row(Td(Table(headers: "fehlt")), Td(Table(headers: "fehlt"))))],
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Ua1Cases))]
    public void Ua1_EachRule_Found(string name, string id)
    {
        using var doc = File(false, Case(name));
        Assert.Contains(id, Ids(doc, AccessibilityProfile.PdfUa1));
    }

    /// <summary>
    /// Tables, lists and contents meeting the rules: head, body, foot, caption first, headers by /Scope and by /Headers, an empty corner without a
    /// header, a cell over two rows and one over two columns.
    /// </summary>
    private static PdfDictionary[] Conforming()
    {
        var name = Th(null, ("ID", S("name")));
        var age = Th(null, ("ID", S("alter")));
        return
        [
            El("Table", El("Caption", C()),
                El("THead", Row(El("TD"), Th(), Th())),
                El("TBody", Row(Th("Row", Table(rowSpan: 2, scope: "Row")), Td(), Td()), Row(Td(Table(colSpan: 2)))),
                El("TFoot", Row(Th("Row"), Td(), Td()))),
            El("Table", Row(name, age), Row(Td(Table(headers: "name")), Td(Table(headers: "alter")))),
            El("L", El("Caption", C()), El("LI", El("Lbl", C()), El("LBody", C())), El("L", El("LI", El("LBody", C())))),
            // TOCI holds no content directly (ISO/TS 32005, Table 5)
            El("TOC", El("Caption", C()), El("TOCI", El("P", C())), El("TOC", El("TOCI", El("P", C())))),
        ];
    }

    private static bool StageG(string id) =>
        id.StartsWith("7.5-", StringComparison.Ordinal) || id == "32005-T5" || id.StartsWith("8.2.5.26-", StringComparison.Ordinal) ||
        id is "8.2.5.14-2" or "8.2.5.14-3" or "8.2.5.23-1" or "8.2.5.24-1" or "8.2.5.25-2" or "8.2.5.27-1" ||
        id.StartsWith("7.2-", StringComparison.Ordinal) && int.Parse(id[4..]) is >= 3 and <= 20 or >= 26 and <= 28 or >= 36 and <= 43;

    [Fact]
    public void Ua1_Conforming_NothingOfStageG()
    {
        using var doc = File(false, Conforming());
        Assert.DoesNotContain(Ids(doc, AccessibilityProfile.PdfUa1), StageG);
    }

    public static TheoryData<string, string> Ua2Cases => new()
    {
        { "Zellen überdecken sich", "8.2.5.26-1" }, { "Spalte zu lang", "8.2.5.26-2" }, { "Zelle über die Gruppe", "8.2.5.26-2" },
        { "Zeile zu breit", "8.2.5.26-3" }, { "Zeile zu schmal", "8.2.5.26-4" }, { "TH ohne Scope", "8.2.5.26-5" }, { "Headers unbekannt", "8.2.5.26-6" },
        { "Ruby ohne RT", "8.2.5.23-1" }, { "Warichu ohne zweites WP", "8.2.5.24-1" }, { "LI mit Inhalt", "8.2.5.25-2" },
        { "Caption in der Mitte", "8.2.5.27-1" }, { "Fußnote nennt Verweis nicht", "8.2.5.14-2" }, { "Fußnote nennt fremde Stelle", "8.2.5.14-3" },
        { "Table mit P", "32005-T5" },
    };

    private static PdfDictionary[] Ua2Case(string name)
    {
        switch (name)
        {
            // a cell in the head over two rows reaches into the body
            case "Zelle über die Gruppe":
                return [El("Table", El("THead", Row(Th(null, Table(rowSpan: 2, scope: "Column")), Th())), El("TBody", Row(Td())))];
            case "Ruby ohne RT": return [El("P", El("Ruby", El("RB", C())))];
            case "Warichu ohne zweites WP": return [El("P", El("Warichu", El("WP", C()), El("WT", C())))];
            case "LI mit Inhalt": return [El("L", El("LI", C(), El("LBody", C())), ("A", Dict(("O", N("List")), ("ListNumbering", N("None")))))];
            case "Caption in der Mitte": return [El("Figure", El("P", C()), El("Caption", C()), El("P", C()), ("Alt", S("Bild")))];
            case "Fußnote nennt Verweis nicht":
            {
                var note = El("FENote", El("P", C()));
                return [El("P", C(), ("Ref", new PdfArray([note]))), note];
            }
            case "Fußnote nennt fremde Stelle":
            {
                var citing = El("P", C());
                return [citing, El("FENote", El("P", C()), ("Ref", new PdfArray([citing])))];
            }
            default: return Case(name);
        }
    }

    [Theory]
    [MemberData(nameof(Ua2Cases))]
    public void Ua2_EachRule_Found(string name, string id)
    {
        using var doc = File(true, Ua2Case(name));
        Assert.Contains(id, Ids(doc, AccessibilityProfile.PdfUa2));
    }

    /// <summary>The conforming tables and lists, Ruby and Warichu complete, a caption last, a note and its citation naming each other.</summary>
    [Fact]
    public void Ua2_Conforming_NothingOfStageG()
    {
        var citing = El("Span", C());
        var note = El("FENote", El("P", C()), ("Ref", new PdfArray([citing])));
        citing.Set(PdfName.Get("Ref"), new PdfArray([note]));
        using var doc = File(true,
        [
            .. Conforming(),
            El("P", El("Ruby", El("RB", C()), El("RT", C())), El("Warichu", El("WP", C()), El("WT", C()), El("WP", C())), citing),
            note,
            El("Figure", El("P", C()), El("Caption", C()), ("Alt", S("Bild"))),
        ]);
        Assert.DoesNotContain(Ids(doc, AccessibilityProfile.PdfUa2), StageG);
    }
}
