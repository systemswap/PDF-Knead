using System.Text;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The check „barrierefrei?“, stage D (docs/ABLAUFPLAN.md, 6.7 step 3; docs/FEHLERGEDAECHTNIS.md, No. 661): what annotations say of themselves
/// — tab order, types not allowed, descriptions, media clips, rich text and rich values, attachments and pop-ups. Each case breaks one rule;
/// the same page without the break has no finding of stage D. Before stage D nothing of it was checked.
/// </summary>
public sealed class AccessibilityAnnotationTests
{
    private static PdfString S(string text) => new(Encoding.ASCII.GetBytes(text));

    /// <summary>
    /// A one-page tagged file with <paramref name="annotations"/> on its page (/Tabs as given, null: none), written as <paramref name="version"/>;
    /// <paramref name="fields"/> go into /AcroForm.
    /// </summary>
    private static PdfDocument File(string version, string? tabs, PdfDictionary[] annotations, PdfDictionary[]? fields = null)
    {
        var pdf = new TestPdf();
        var pagesNumber = pdf.Add(new PdfDictionary());
        var pageNumber = pdf.Add(new PdfDictionary());
        var rootNumber = pdf.Add(new PdfDictionary());
        var document = pdf.Add(Dict(("Type", N("StructElem")), ("S", N("Document")), ("P", Ref(rootNumber))));
        pdf.Replace(rootNumber, Dict(("Type", N("StructTreeRoot")), ("K", Ref(document))));
        var page = Dict(("Type", N("Page")), ("Parent", Ref(pagesNumber)), ("MediaBox", Box(0, 0, 595, 842)), ("Resources", new PdfDictionary()),
            ("Contents", Ref(pdf.Add(FlateStream("/Artifact BMC 0 0 m 100 100 l S EMC"u8.ToArray())))));
        if (tabs is not null) page.Set(PdfName.Get("Tabs"), N(tabs));
        var annots = new PdfArray();
        foreach (var annot in annotations)
        {
            annot.Set(PdfName.Get("P"), Ref(pageNumber));
            annots.Add(Ref(pdf.Add(annot)));
        }
        if (annots.Count > 0) page.Set(PdfName.Get("Annots"), annots);
        pdf.Replace(pageNumber, page);
        pdf.Replace(pagesNumber, Dict(("Type", N("Pages")), ("Kids", new PdfArray { Ref(pageNumber) }), ("Count", PdfInteger.Of(1))));
        var catalog = Dict(("Type", N("Catalog")), ("Pages", Ref(pagesNumber)), ("Lang", new PdfString("de"u8.ToArray())),
            ("MarkInfo", Dict(("Marked", PdfBoolean.Of(true)))), ("StructTreeRoot", Ref(rootNumber)));
        if (fields is not null) catalog.Set(PdfName.Get("AcroForm"), Dict(("Fields", new PdfArray([.. fields.Select(f => (PdfObject)Ref(pdf.Add(f)))]))));
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

    private static List<string> Ids(PdfDocument doc, AccessibilityProfile profile) => [.. AccessibilityCheck.Check(doc, profile).Findings.Select(f => f.Id)];

    public static TheoryData<string, string> Ua1Cases => new()
    {
        { "ohne Tabs", "7.18.3-1" }, { "Tabs R", "7.18.3-1" }, { "TrapNet", "7.18.2-1" }, { "Link ohne Contents", "7.18.5-2" },
        { "Anmerkung ohne Beschreibung", "7.18.1-2" }, { "Medienclip ohne CT", "7.18.6.2-1" }, { "Medienclip mit leerem Alt", "7.18.6.2-2" },
    };

    private static (PdfDictionary[] Annots, string? Tabs) Ua1(string name) => name switch
    {
        "ohne Tabs" => ([Annot("Text", ("Contents", S("Notiz")))], null),
        "Tabs R" => ([Annot("Text", ("Contents", S("Notiz")))], "R"),
        "TrapNet" => ([Annot("TrapNet", ("Contents", S("x")))], "S"),
        "Link ohne Contents" => ([Annot("Link")], "S"),
        "Anmerkung ohne Beschreibung" => ([Annot("Square")], "S"),
        "Medienclip ohne CT" => ([Annot("Screen", ("Contents", S("Film")), ("A", Clip(withType: false, alt: "Film über das Amt")))], "S"),
        "Medienclip mit leerem Alt" => ([Annot("Screen", ("Contents", S("Film")), ("A", Clip(withType: true, alt: "")))], "S"),
        "ohne Bruch" => ([Annot("Text", ("Contents", S("Notiz"))), Annot("Link", ("Contents", S("Zur Startseite"))),
            Annot("Screen", ("Contents", S("Film")), ("A", Clip(withType: true, alt: "Film über das Amt")))], "S"),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>A rendition action playing a media clip (/S /MCD) with or without /CT and with the given description.</summary>
    private static PdfDictionary Clip(bool withType, string alt)
    {
        var clip = Dict(("Type", N("MediaClip")), ("S", N("MCD")), ("Alt", new PdfArray([S(""), S(alt)])));
        if (withType) clip.Set(PdfName.Get("CT"), S("video/mp4"));
        return Dict(("Type", N("Action")), ("S", N("Rendition")), ("OP", PdfInteger.Of(0)),
            ("R", Dict(("Type", N("Rendition")), ("S", N("MR")), ("C", clip))));
    }

    [Theory]
    [MemberData(nameof(Ua1Cases))]
    public void Ua1_EachAnnotationRule_Found(string name, string id)
    {
        var (annots, tabs) = Ua1(name);
        using var doc = File("1.7", tabs, annots);
        Assert.Contains(id, Ids(doc, AccessibilityProfile.PdfUa1));
    }

    /// <summary>
    /// The rules of stage F (the annotation's place in the structure tree, <see cref="AccessibilityAnnotationTreeTests"/>): these files have no
    /// structure tree, so stage F rightly finds annotations outside it.
    /// </summary>
    private static readonly HashSet<string> StageF =
    [
        "7.18.1-1", "7.18.1-3", "7.18.4-1", "7.18.4-2", "7.18.5-1", "7.18.8-1", "8.9.2.1-1", "8.9.2.2-1", "8.9.2.2-2", "8.9.2.3-1", "8.9.2.4.13-1",
        "8.9.2.4.14-1", "8.9.2.4.16-1", "8.9.4.2-1", "8.10.1-1", "8.10.1-2", "8.10.2.3-1",
    ];

    /// <summary>The same annotations meeting the rules, and one hidden and one outside the crop box without a description: nothing of stage D.</summary>
    [Fact]
    public void Ua1_Conforming_HiddenAndOutside_NothingOfStageD()
    {
        var (annots, tabs) = Ua1("ohne Bruch");
        using var doc = File("1.7", tabs, [.. annots, Annot("Square", ("F", PdfInteger.Of(2))), Annot("Square", ("Rect", new PdfArray([PdfInteger.Of(800), PdfInteger.Of(800), PdfInteger.Of(800), PdfInteger.Of(800)])))]);
        Assert.DoesNotContain(Ids(doc, AccessibilityProfile.PdfUa1), id => id.StartsWith("7.18", StringComparison.Ordinal) && !StageF.Contains(id));
    }

    public static TheoryData<string, string> Ua2Cases => new()
    {
        { "ohne Tabs", "8.9.3.3-1" }, { "Tabs R", "8.9.3.3-1" }, { "TrapNet", "8.9.2.4.15-1" }, { "Sound", "8.9.2.4.11-1" }, { "Movie", "8.9.2.4.11-2" },
        { "Stempel ohne Name", "8.9.2.4.7-1" }, { "Freihand ohne Contents", "8.9.2.4.8-1" }, { "Screen ohne Contents", "8.9.2.4.12-1" },
        { "3D ohne Contents", "8.9.2.4.19-1" }, { "RichMedia ohne Contents", "8.9.2.4.19-2" }, { "Pop-up im Baum", "8.9.2.4.9-1" },
        { "Dateianhang ohne Beziehung", "8.9.2.4.10-1" }, { "RC anders als Contents", "8.9.2.3-2" },
    };

    private static (PdfDictionary[] Annots, string? Tabs) Ua2(string name) => name switch
    {
        "ohne Tabs" => ([Annot("Text", ("Contents", S("Notiz")))], null),
        "Tabs R" => ([Annot("Text", ("Contents", S("Notiz")))], "R"),
        "TrapNet" => ([Annot("TrapNet")], "S"),
        "Sound" => ([Annot("Sound", ("Contents", S("Ton")))], "S"),
        "Movie" => ([Annot("Movie", ("Contents", S("Film")))], "S"),
        "Stempel ohne Name" => ([Annot("Stamp")], "S"),
        "Freihand ohne Contents" => ([Annot("Ink")], "S"),
        "Screen ohne Contents" => ([Annot("Screen")], "S"),
        "3D ohne Contents" => ([Annot("3D")], "S"),
        "RichMedia ohne Contents" => ([Annot("RichMedia")], "S"),
        "Pop-up im Baum" => ([Annot("Popup", ("StructParent", PdfInteger.Of(0)))], "S"),
        "Dateianhang ohne Beziehung" => ([Annot("FileAttachment", ("Contents", S("Anlage")),
            ("FS", Dict(("Type", N("Filespec")), ("F", S("anlage.txt")), ("UF", S("anlage.txt")))))], "S"),
        "RC anders als Contents" => ([Annot("Text", ("Contents", S("Bitte bis Freitag")),
            ("RC", S("<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\"><p>Bitte bis Montag</p></body>")))], "S"),
        "ohne Bruch" => ([Annot("Text", ("Contents", S("Bitte bis Freitag")),
                ("RC", S("<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\"><p>Bitte <b>bis</b> Freitag</p></body>"))),
            Annot("Stamp", ("Name", N("Approved"))), Annot("Ink", ("Contents", S("Unterstrichen"))),
            Annot("FileAttachment", ("Contents", S("Anlage")), ("FS", Dict(("Type", N("Filespec")), ("F", S("a.txt")), ("UF", S("a.txt")), ("AFRelationship", N("Supplement")))))], "W"),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Ua2Cases))]
    public void Ua2_EachAnnotationRule_Found(string name, string id)
    {
        var (annots, tabs) = Ua2(name);
        using var doc = File("2.0", tabs, annots);
        Assert.Contains(id, Ids(doc, AccessibilityProfile.PdfUa2));
    }

    /// <summary>The same rules met (rich text with markup saying the same, /Tabs /W): nothing of stage D in PDF/UA-2.</summary>
    [Fact]
    public void Ua2_Conforming_NothingOfStageD()
    {
        var (annots, tabs) = Ua2("ohne Bruch");
        using var doc = File("2.0", tabs, annots);
        Assert.DoesNotContain(Ids(doc, AccessibilityProfile.PdfUa2), id => (id.StartsWith("8.9", StringComparison.Ordinal) || id.StartsWith("8.10", StringComparison.Ordinal)) && !StageF.Contains(id));
    }

    /// <summary>A text field with a rich value whose plain value says something else: 8.10.3.3; the same text in both: nothing.</summary>
    [Theory]
    [InlineData("Max Mustermann", false)]
    [InlineData("Erika Mustermann", true)]
    public void Ua2_RichValueOfATextField(string value, bool found)
    {
        var field = Dict(("FT", N("Tx")), ("T", S("name")), ("V", S(value)),
            ("RV", S("<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\"><p>Max <i>Mustermann</i></p></body>")));
        using var doc = File("2.0", null, [], [field]);
        Assert.Equal(found, Ids(doc, AccessibilityProfile.PdfUa2).Contains("8.10.3.3-1"));
    }

    /// <summary>
    /// A text field with a plain value and no rich value: nothing of 8.10.3.3 — there is no rich value to compare. Before, a missing /RV read as
    /// present (the null object) and every such field was reported (docs/FEHLERGEDAECHTNIS.md, No. 1004).
    /// </summary>
    [Fact]
    public void Ua2_TextFieldWithoutRichValue_NotReported()
    {
        var field = Dict(("FT", N("Tx")), ("T", S("name")), ("V", S("Erika Mustermann")));
        using var doc = File("2.0", null, [], [field]);
        Assert.DoesNotContain("8.10.3.3-1", Ids(doc, AccessibilityProfile.PdfUa2));
    }
}
