using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// PDF/A level a (docs/ABLAUFPLAN.md, 6.6 e; docs/FEHLERGEDAECHTNIS.md, No. 652): of an untagged file, what paints outside the text and figures
/// becomes an artifact, heading levels follow their numbering or start at H1 without jumps; of a tagged file, a figure answered as decoration
/// leaves the tree and its content becomes an artifact.
/// </summary>
public sealed class PdfALevelAArtifactTests
{
    /// <summary>An untagged page with Helvetica as T1 and <paramref name="content"/>.</summary>
    private static byte[] Untagged(string content)
    {
        var pdf = Simple(1);
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("Encoding", N("WinAnsiEncoding"))));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content)));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, Dict(("Font", Dict(("T1", Ref(font))))));
        return pdf.Build();
    }

    private static PdfDocument Export(byte[] data, PdfAAccessibility? answers = null)
    {
        using var doc = PdfDocument.Open(data);
        var result = PdfAExport.Write(doc, Core.Pdf.Editing.PageItem.From(doc), PdfAConformance.A2a, accessibility: answers ?? new PdfAAccessibility { Language = "de" });
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        return PdfDocument.Open(result.Bytes);
    }

    private static List<string> TopTypes(PdfDocument doc)
    {
        var kids = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.GetDictionary(PdfName.Get("K"))!.GetArray(PdfName.Get("K"))!;
        return [.. Enumerable.Range(0, kids.Count).Select(i => ((PdfDictionary)kids.Get(i)).GetName(PdfName.Get("S"))!.Value)];
    }

    private static string Content(PdfDocument doc)
    {
        using var data = Core.Pdf.Content.PageInterpreter.PageContent(doc, doc.Pages[0].Dictionary);
        return Encoding.Latin1.GetString(data.Span);
    }

    /// <summary>A filled rectangle beside the text: an artifact of its own, around its construction and painting. Before: real content without structure.</summary>
    [Fact]
    public void LevelA_PathsBecomeArtifacts_NothingUntagged()
    {
        using var doc = Export(Untagged("BT /T1 11 Tf 72 720 Td (Ein Absatz mit Text.) Tj ET 0 0 1 rg 72 600 200 50 re f"));
        Assert.Matches(@"/Artifact\s*<<\s*/Type\s*/Layout\s*>>\s*BDC\s+72 600 200 50 re\s+f\s+EMC", Content(doc));
        var page = Assert.Single(Core.Pdf.Accessibility.TaggedContent.Analyse(doc).Pages);
        Assert.Equal(0, page.Untagged);
    }

    /// <summary>The first heading in the second size, a later one in the first: both H1 — no document starts with H2. Before: H2, then H1.</summary>
    [Fact]
    public void LevelA_FirstHeadingIsH1()
    {
        using var doc = Export(Untagged(
            "BT /T1 18 Tf 72 760 Td (Einleitung) Tj ET BT /T1 11 Tf 72 730 Td (Der erste Absatz erklaert etwas.) Tj ET " +
            "BT /T1 24 Tf 72 680 Td (Hauptteil) Tj ET BT /T1 11 Tf 72 650 Td (Der zweite Absatz erklaert mehr.) Tj ET"));
        Assert.Equal(["H1", "P", "H1", "P"], TopTypes(doc));
    }

    /// <summary>A heading numbered „2.3“: H2, even as the only heading size (by its size alone it was H1).</summary>
    [Fact]
    public void LevelA_NumberedHeadingKeepsItsLevel()
    {
        using var doc = Export(Untagged(
            "BT /T1 18 Tf 72 760 Td (2.3 Ergebnisse) Tj ET BT /T1 11 Tf 72 730 Td (Der Absatz nennt die Ergebnisse.) Tj ET"));
        Assert.Equal(["H2", "P"], TopTypes(doc));
    }

    /// <summary>
    /// A tagged page with a Figure without text, answered as decoration: it leaves the tree, its picture is an artifact. Before: NonStruct, its
    /// content real content without meaning.
    /// </summary>
    [Fact]
    public void LevelA_DecorativeFigureOfOwnTree_IsArtifact()
    {
        var pdf = Simple(1);
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("Encoding", N("WinAnsiEncoding"))));
        var image = pdf.Add(new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(2)),
            ("ColorSpace", N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8))), [200, 30, 30, 30, 200, 30, 30, 30, 200, 200, 200, 30]));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(
            "/P <</MCID 0>> BDC BT /T1 11 Tf 72 720 Td (Ein Absatz.) Tj ET EMC /Figure <</MCID 1>> BDC q 100 0 0 80 72 560 cm /Bild Do Q EMC")));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, Dict(("Font", Dict(("T1", Ref(font)))), ("XObject", Dict(("Bild", Ref(image))))));
        page.Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        var root = pdf.Add(new PdfDictionary());
        var document = pdf.Add(new PdfDictionary());
        var paragraph = pdf.Add(Dict(("Type", N("StructElem")), ("S", N("P")), ("P", Ref(document)), ("Pg", Ref(5)), ("K", PdfInteger.Of(0))));
        var figure = pdf.Add(Dict(("Type", N("StructElem")), ("S", N("Figure")), ("P", Ref(document)), ("Pg", Ref(5)), ("K", PdfInteger.Of(1))));
        pdf.Replace(document, Dict(("Type", N("StructElem")), ("S", N("Document")), ("P", Ref(root)), ("K", new PdfArray([Ref(paragraph), Ref(figure)]))));
        pdf.Replace(root, Dict(("Type", N("StructTreeRoot")), ("K", Ref(document)),
            ("ParentTree", Dict(("Nums", new PdfArray([PdfInteger.Of(0), new PdfArray([Ref(paragraph), Ref(figure)])])))), ("ParentTreeNextKey", PdfInteger.Of(1))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("StructTreeRoot"), Ref(root));
        catalog.Set(PdfName.Get("MarkInfo"), Dict(("Marked", PdfBoolean.True)));
        catalog.Set(PdfName.Get("Lang"), new PdfString("de"u8.ToArray()));

        using var doc = Export(pdf.Build(), new PdfAAccessibility
        {
            Language = "de",
            Figures = new Dictionary<string, string?> { ["e" + figure.ToString(System.Globalization.CultureInfo.InvariantCulture)] = null },
        });
        Assert.Equal(["P"], TopTypes(doc));
        var content = Content(doc);
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(content, @"/Artifact BMC\s+q\s+100 0 0 80 72 560 cm"), content);
    }
}
