using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Danach nicht mehr da“ in the window „Seiten zusammenführen“ (KONZEPT.md, section 5.2): what an inserted file loses — protection,
/// restrictions, field names (renamed exactly as the writer does), document attachments, scripts, metadata, bookmarks if left out.
/// </summary>
public sealed class MergeNotesTests
{
    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    private static byte[] Write(PdfDocument doc, IReadOnlyList<PageItem> pages, PdfSaveOptions? options = null)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, pages, output, options ?? new PdfSaveOptions());
        return output.ToArray();
    }

    /// <summary>A one-page file with a text field „<paramref name="field"/>“ and a bookmark „<paramref name="bookmark"/>“.</summary>
    private static byte[] WithFieldAndBookmark(string field, string bookmark)
    {
        var pdf = TestPdf.Simple(1);
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        var page = (PdfDictionary)pdf.Get(5);
        var appearance = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 150, 20))),
            Encoding.ASCII.GetBytes(""));
        var widget = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Tx")), ("T", S(field)),
            ("Rect", TestPdf.Box(100, 500, 250, 520)), ("F", PdfInteger.Of(4)), ("P", TestPdf.Ref(5)), ("AP", TestPdf.Dict(("N", appearance)))));
        page.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(widget)]));
        catalog.Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(widget)]))));
        var root = pdf.Add(new PdfDictionary());
        var item = pdf.Add(TestPdf.Dict(("Title", S(bookmark)), ("Parent", TestPdf.Ref(root)), ("Dest", new PdfArray([TestPdf.Ref(5), TestPdf.N("Fit")]))));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("Outlines")), ("First", TestPdf.Ref(item)), ("Last", TestPdf.Ref(item)), ("Count", PdfInteger.Of(1))));
        catalog.Set(PdfName.Get("Outlines"), TestPdf.Ref(root));
        return pdf.Build();
    }

    [Fact]
    public void ProtectedFile_LosesPasswordAndRestrictions()
    {
        using var plain = PdfDocument.Open(TestPdf.Simple(2).Build());
        using var output = new MemoryStream();
        PdfWriter.Write(plain, output, new PdfSaveOptions { Protection = new PdfProtection("geheim", "Besitzer-1", PdfPermissions.Copy | PdfPermissions.Modify) });
        using var source = PdfDocument.Open(output.ToArray(), "geheim");
        var notes = MergeNotes.For(source, openedWithPassword: true, [source.Pages[0]], keepBookmarks: true, new HashSet<string>());
        Assert.Contains(Tr.T("Passwort zum Öffnen → entfällt"), notes);
        Assert.Contains(Tr.T("Drucken gesperrt → gilt nicht mehr"), notes);
        Assert.Contains(Tr.T("Kommentieren gesperrt → gilt nicht mehr"), notes);
        Assert.Contains(Tr.T("Formulare ausfüllen gesperrt → gilt nicht mehr"), notes);
        Assert.DoesNotContain(Tr.T("Kopieren gesperrt → gilt nicht mehr"), notes);
        Assert.DoesNotContain(Tr.T("Ändern gesperrt → gilt nicht mehr"), notes);

        // with the permissions password the file is the owner's: nothing is locked
        using var owned = PdfDocument.Open(output.ToArray(), "Besitzer-1");
        Assert.DoesNotContain(Tr.T("Drucken gesperrt → gilt nicht mehr"), MergeNotes.For(owned, true, [owned.Pages[0]], true, new HashSet<string>()));
    }

    [Fact]
    public void SameFieldName_RenamedAsTheWriterDoes_BookmarksAsChosen()
    {
        using var target = PdfDocument.Open(WithFieldAndBookmark("Name", "Vertrag"));
        using var source = PdfDocument.Open(WithFieldAndBookmark("Name", "Anlage"));
        var taken = MergeNotes.FieldNames(target);
        var notes = MergeNotes.For(source, false, [source.Pages[0]], keepBookmarks: true, taken);
        Assert.Contains(Tr.T("Formularfeld „{0}“ → „{1}“ (der Name kam schon vor)", "Name", "Name_2"), notes);
        Assert.DoesNotContain(Tr.T("Lesezeichen kommen nicht mit"), notes);
        Assert.Contains(Tr.T("Lesezeichen kommen nicht mit"), MergeNotes.For(source, false, [source.Pages[0]], keepBookmarks: false, MergeNotes.FieldNames(target)));

        // the writer renames the same way, and leaves the bookmarks of the file out when asked
        PageItem[] pages = [.. PageItem.From(target), .. PageItem.From(source)];
        using (var merged = PdfDocument.Open(Write(target, pages)))
        {
            Assert.Equal(["Name", "Name_2"], MergeNotes.FieldNames(merged).Order());
            Assert.Equal(["Vertrag", "source"], PdfOutline.Read(merged).Select(o => o.Title == "Vertrag" ? "Vertrag" : o.Children.Single().Title == "Anlage" ? "source" : "?"));
        }
        using (var without = PdfDocument.Open(Write(target, pages, new PdfSaveOptions { OutlinesLeftOut = new HashSet<PdfDocument>([source]) })))
            Assert.Equal("Vertrag", Assert.Single(PdfOutline.Read(without)).Title);
    }

    [Fact]
    public void DocumentLevelThings_StayBehind()
    {
        var pdf = TestPdf.Simple(1, title: "Geheimer Titel");
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), "Anhang"u8.ToArray()));
        var spec = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", S("a.txt")), ("EF", TestPdf.Dict(("F", TestPdf.Ref(file))))));
        var script = pdf.Add(TestPdf.Dict(("S", TestPdf.N("JavaScript")), ("JS", S("app.alert(1)"))));
        catalog.Set(PdfName.Get("Names"), TestPdf.Dict(("EmbeddedFiles", TestPdf.Dict(("Names", new PdfArray([S("a.txt"), TestPdf.Ref(spec)]))))));
        catalog.Set(PdfName.Get("OpenAction"), TestPdf.Ref(script));
        catalog.Set(PdfName.Get("OutputIntents"), new PdfArray([TestPdf.Dict(("Type", TestPdf.N("OutputIntent")), ("S", TestPdf.N("GTS_PDFX")))]));
        using var source = PdfDocument.Open(pdf.Build());
        var notes = MergeNotes.For(source, false, [source.Pages[0]], true, new HashSet<string>());
        Assert.Contains(Tr.T("Dateianhänge kommen nicht mit"), notes);
        Assert.Contains(Tr.T("Skripte und Aktionen beim Öffnen kommen nicht mit"), notes);
        Assert.Contains(Tr.T("Titel und Metadaten kommen nicht mit"), notes);
        Assert.Contains(Tr.T("Eigenes Farbprofil (Ausgabeprofil) kommt nicht mit — CMYK-Farben können sich leicht ändern"), notes);

        // a plain file loses nothing
        using var plain = PdfDocument.Open(TestPdf.Simple(1, title: null).Build());
        Assert.Empty(MergeNotes.For(plain, false, [plain.Pages[0]], true, new HashSet<string>()));
    }

    /// <summary>
    /// The structure (docs/ABLAUFPLAN.md, 6.5 step 16; No. 640, 646): an untagged file into a tagged document makes the result inaccessible; a
    /// tagged file — with namespaces too, since 6.5 g — into a tagged document, or an untagged one into a new file, loses nothing of it.
    /// </summary>
    [Fact]
    public void Structure_UntaggedIntoTagged_Noted_NamespacesComeAlong()
    {
        using var tagged = PdfDocument.Open(MergeStructureTests.Tagged("Ziel", 1));
        using var other = PdfDocument.Open(MergeStructureTests.Tagged("Andere", 1));
        using var plain = PdfDocument.Open(TestPdf.Simple(1).Build());
        using var spaced = PdfDocument.Open(MergeStructureTests.Tagged("Mit Namensraum", 1, extra: o =>
        {
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/Namespaces[16 0 R]>>";
            o[16] = "<</Type/Namespace/NS(http://iso.org/pdf2/ssn)>>";
        }));
        const string inaccessible = "Nicht getaggt → das Ergebnis ist nicht mehr barrierefrei";
        Assert.Contains(inaccessible, MergeNotes.For(plain, false, [plain.Pages[0]], true, new HashSet<string>(), tagged));
        Assert.Empty(MergeNotes.For(spaced, false, [spaced.Pages[0]], true, new HashSet<string>(), tagged));
        Assert.Empty(MergeNotes.For(other, false, [other.Pages[0]], true, new HashSet<string>(), tagged));
        Assert.DoesNotContain(inaccessible, MergeNotes.For(plain, false, [plain.Pages[0]], true, new HashSet<string>()));
    }
}
