using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Merging tagged files (docs/ABLAUFPLAN.md, 6.5 a; ISO 32000-2, 14.7.2, 14.7.5.4; ISO 14289-2, 8.2.5.2; docs/FEHLERGEDAECHTNIS.md, No. 638):
/// the tree of a file merged into a tagged copy comes along — its keys in a range of their own, its elements under a Part below the copy's
/// Document, one Part per run of its pages, in the reading order of the pages. Before (No. 435): its keys went, its content belonged to no
/// element, and the save took the PDF/UA identification for „Seiten aus einer anderen Datei“.
/// </summary>
public sealed class MergeStructureTests
{
    /// <summary>
    /// A tagged file: per page a paragraph (MCID 0) under a Document, keys 0 … n−1, the text „{name} Seite {i}“; <paramref name="extra"/> changes objects.
    /// Objects: catalog 1, pages 2, root 6, Document 7, parent tree 15; page i at 20 + 10 i, its content 21 + 10 i, its paragraph 22 + 10 i.
    /// </summary>
    internal static byte[] Tagged(string name, int pages, string lang = "de-DE", Action<Dictionary<int, object>>? extra = null, string? title = null)
    {
        var objects = new Dictionary<int, object>
        {
            [1] = $"<</Type/Catalog/Pages 2 0 R/StructTreeRoot 6 0 R/MarkInfo<</Marked true>>/Lang({lang})>>",
            [2] = $"<</Type/Pages/Kids[{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{20 + 10 * i} 0 R"))}]/Count {pages}/MediaBox[0 0 595 842]>>",
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [6] = $"<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey {pages}>>",
            [7] = $"<</Type/StructElem/S/Document/P 6 0 R/K[{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{22 + 10 * i} 0 R"))}]>>",
            [15] = $"<</Nums[{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{i}[{22 + 10 * i} 0 R]"))}]>>",
        };
        for (var i = 0; i < pages; i++)
        {
            objects[20 + 10 * i] = $"<</Type/Page/Parent 2 0 R/Contents {21 + 10 * i} 0 R/Resources<</Font<</F1 5 0 R>>>>/StructParents {i}>>";
            objects[21 + 10 * i] = ("", $"/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td ({name} Seite {i + 1}) Tj ET EMC");
            objects[22 + 10 * i] = $"<</Type/StructElem/S/P/P 7 0 R/Pg {20 + 10 * i} 0 R/K 0>>";
        }
        if (title is not null)
        {
            objects[3] = $"<</Title({title})>>";
            objects[1] = ((string)objects[1])[..^2] + "/ViewerPreferences<</DisplayDocTitle true>>>>";
        }
        extra?.Invoke(objects);
        return CraftedFileTests.Build(objects, title is null ? "" : "/Info 3 0 R");
    }

    /// <summary>Writes the pages (file, page index) in that order; the first file is the main one.</summary>
    private static (PdfDocument Saved, PdfSaveResult Result) Merge(PdfDocument main, PdfDocument other, params (int File, int Page)[] order)
    {
        var items = new[] { PageItem.From(main), PageItem.From(other) };
        using var output = new MemoryStream();
        var result = PdfWriter.Write(main, [.. order.Select(o => items[o.File][o.Page])], output);
        return (PdfDocument.Open(output.ToArray()), result);
    }

    private static PdfDictionary Document(PdfDocument doc) => Kids(doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!)[0];

    private static List<PdfDictionary> Kids(PdfDictionary element) => element.Get(PdfName.Get("K")) switch
    {
        PdfArray a => [.. Enumerable.Range(0, a.Count).Select(a.Get).OfType<PdfDictionary>()],
        PdfDictionary one => [one],
        _ => [],
    };

    private static string TextOf(PdfDocument doc, PdfDictionary paragraph)
    {
        var page = doc.Pages.First(p => ReferenceEquals(p.Dictionary, paragraph.Get(PdfName.Get("Pg"))));
        return Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.Build(doc, page).Text).Text.Trim();
    }

    /// <summary>
    /// Two tagged files one after the other: the second page keeps a key (1, shifted into the second file's range), the parent tree names
    /// its paragraph, which hangs in a Part after the first file's paragraph under the one Document; no reason „Seiten aus einer anderen Datei“.
    /// </summary>
    [Fact]
    public void TwoTaggedFiles_SecondTreeInAPartUnderTheDocument_KeysShifted()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1));
        using var b = PdfDocument.Open(Tagged("Zweite Datei", 1));
        var (saved, result) = Merge(a, b, (0, 0), (1, 0));
        using (saved)
        {
            Assert.Equal(1, saved.Pages[1].Dictionary.GetInteger(PdfName.Get("StructParents")));
            var top = Kids(Document(saved));
            Assert.Equal(["P", "Part"], top.Select(e => e.GetName(PdfName.Get("S"))!.Value));
            var paragraph = Assert.Single(Kids(top[1]));
            Assert.Equal("Zweite Datei Seite 1", TextOf(saved, paragraph));
            Assert.Same(top[1], paragraph.Get(PdfName.Get("P")));
            Assert.DoesNotContain("Seiten aus einer anderen Datei", result.AccessibilityBreaks);
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>Inserted between the main file's two pages: the Part stands between their paragraphs — the reading order follows the pages.</summary>
    [Fact]
    public void InsertedInTheMiddle_PartBetweenTheParagraphs()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 2));
        using var b = PdfDocument.Open(Tagged("Zweite Datei", 1));
        var (saved, _) = Merge(a, b, (0, 0), (1, 0), (0, 1));
        using (saved)
        {
            Assert.Equal(["P", "Part", "P"], Kids(Document(saved)).Select(e => e.GetName(PdfName.Get("S"))!.Value));
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>The two pages of the other file at two places: one Part per run, each at the place of its pages.</summary>
    [Fact]
    public void PagesAtTwoPlaces_OnePartPerRun()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 2));
        using var b = PdfDocument.Open(Tagged("Zweite Datei", 2));
        var (saved, _) = Merge(a, b, (1, 0), (0, 0), (1, 1), (0, 1));
        using (saved)
        {
            var top = Kids(Document(saved));
            Assert.Equal(["Part", "P", "Part", "P"], top.Select(e => e.GetName(PdfName.Get("S"))!.Value));
            Assert.Equal("Zweite Datei Seite 2", TextOf(saved, Assert.Single(Kids(top[2]))));
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>
    /// An annotation and a form of the other file keep their keys, moved into its range; the other file's language stands on its Part.
    /// </summary>
    [Fact]
    public void AnnotationAndFormKeysShifted_LanguageOnThePart()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1));
        using var b = PdfDocument.Open(Tagged("Second file", 1, "en-US", o =>
        {
            o[20] = "<</Type/Page/Parent 2 0 R/Contents 21 0 R/Resources<</Font<</F1 5 0 R>>/XObject<</X1 25 0 R>>>>/StructParents 0/Annots[24 0 R]>>";
            o[21] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Second file) Tj ET EMC /X1 Do");
            o[23] = "<</Type/StructElem/S/Annot/P 7 0 R/Pg 20 0 R/K<</Type/OBJR/Obj 24 0 R/Pg 20 0 R>>>>";
            o[24] = "<</Type/Annot/Subtype/Square/Rect[100 100 200 200]/F 4/StructParent 1/Contents(Box)/AP<</N 26 0 R>>>>";
            o[25] = ("/Type/XObject/Subtype/Form/BBox[0 0 100 100]/StructParents 2", "/Figure <</MCID 0>> BDC 0 0 m 50 50 l S EMC");
            o[26] = ("/Type/XObject/Subtype/Form/BBox[0 0 100 100]", "0 0 100 100 re S");
            o[27] = "<</Type/StructElem/S/Figure/P 7 0 R/Pg 20 0 R/Alt(Linie)/K<</Type/MCR/MCID 0/Stm 25 0 R/Pg 20 0 R>>>>";
            o[7] = "<</Type/StructElem/S/Document/P 6 0 R/K[22 0 R 23 0 R 27 0 R]>>";
            o[15] = "<</Nums[0[22 0 R] 1 23 0 R 2[27 0 R]]>>";
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 3>>";
        }));
        var (saved, _) = Merge(a, b, (0, 0), (1, 0));
        using (saved)
        {
            var page = saved.Pages[1].Dictionary;
            var annot = (PdfDictionary)page.GetArray(PdfName.Get("Annots"))!.Get(0);
            var form = (PdfStream)page.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("X1"))!;
            Assert.Equal(1, page.GetInteger(PdfName.Get("StructParents")));
            Assert.Equal(2, annot.GetInteger(PdfName.Get("StructParent")));
            Assert.Equal(3, form.Dictionary.GetInteger(PdfName.Get("StructParents")));
            var part = Kids(Document(saved))[1];
            Assert.Equal("en-US", part.GetString(PdfName.Get("Lang"))!.ToText());
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>The other file names its language „English“ (no tag): its Part gets the tag meant, „en“ — never the name.</summary>
    [Fact]
    public void LanguageName_OnThePartAsTheTagMeant()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1));
        using var b = PdfDocument.Open(Tagged("Second file", 1, "English"));
        var (saved, _) = Merge(a, b, (0, 0), (1, 0));
        using (saved) Assert.Equal("en", Kids(Document(saved))[1].GetString(PdfName.Get("Lang"))!.ToText());
    }

    /// <summary>
    /// A file the steps built so far cannot take — the copy maps the standard type P to H1 (against ISO 14289-1, 7.1), the other file's
    /// paragraphs would become headings and no new name escapes the role map without namespaces: as before, its keys go, the save says so.
    /// </summary>
    [Fact]
    public void CopyRemapsAStandardType_FallsBack_KeysGoReasonGiven()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1, extra: o =>
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/RoleMap<</P/H1>>>>"));
        using var b = PdfDocument.Open(Tagged("Zweite Datei", 1));
        var (saved, result) = Merge(a, b, (0, 0), (1, 0));
        using (saved)
        {
            Assert.False(saved.Pages[1].Dictionary.ContainsKey(PdfName.Get("StructParents")));
            Assert.Contains("Seiten aus einer anderen Datei", result.AccessibilityBreaks);
            Assert.Empty(Core.Pdf.Accessibility.TaggedContent.ParentTree(saved));
            // nor does its content keep its marked-content ids (No. 689: a page with them needs /StructParents, ISO 32000-2, Table 31); the tag
            // stays, and so does the text
            using var content = Core.Pdf.Content.PageInterpreter.PageContent(saved, saved.Pages[1].Dictionary);
            var text = System.Text.Encoding.Latin1.GetString(content.Span);
            Assert.DoesNotContain("MCID", text);
            Assert.Contains("BDC", text);
            Assert.Contains("Zweite Datei Seite 1", Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.Build(saved, saved.Pages[1]).Text).Text);
        }
    }

    /// <summary>
    /// The copy maps Document to a type of its own (as a reference file does): the other file's Document is unwrapped, its paragraph means P —
    /// nothing of it takes the copy's meaning, its tree comes. (The first draft of 6.5 b fell back here, found with the measurement.)
    /// </summary>
    [Fact]
    public void CopyRemapsATypeTheOtherDoesNotKeep_Merged()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1, extra: o =>
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/RoleMap<</Document/Buch>>>>"));
        using var b = PdfDocument.Open(Tagged("Zweite Datei", 1));
        var (saved, result) = Merge(a, b, (0, 0), (1, 0));
        using (saved)
        {
            Assert.Equal(["P", "Part"], Kids(Document(saved)).Select(e => e.GetName(PdfName.Get("S"))!.Value));
            Assert.DoesNotContain("Seiten aus einer anderen Datei", result.AccessibilityBreaks);
            StructureAssert.Clean(saved);
        }
    }

    // ------------------------------------------------------------ 6.5 b: names that clash (No. 639)

    private static PdfDictionary Root(PdfDocument doc) => doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!;

    private static PdfDictionary RoleMap(PdfDocument doc) => Root(doc).GetDictionary(PdfName.Get("RoleMap"))!;

    /// <summary>
    /// Both files have their own type „Absatz“ — the copy maps it to P, the other file to H1: the other file's element becomes „Absatz_2“
    /// mapped to H1, the copy's entry stays. Before: the other file's tree did not come (its keys went).
    /// </summary>
    [Fact]
    public void RoleMapConflict_ForeignTypeRenamed()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1, extra: o =>
        {
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/RoleMap<</Absatz/P>>>>";
            o[22] = "<</Type/StructElem/S/Absatz/P 7 0 R/Pg 20 0 R/K 0>>";
        }));
        using var b = PdfDocument.Open(Tagged("Zweite Datei", 1, extra: o =>
        {
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/RoleMap<</Absatz/H1/Kasten/Absatz>>>>";
            o[22] = "<</Type/StructElem/S/Absatz/P 7 0 R/Pg 20 0 R/K 0>>";
        }));
        var (saved, result) = Merge(a, b, (0, 0), (1, 0));
        using (saved)
        {
            var top = Kids(Document(saved));
            Assert.Equal("Absatz", top[0].GetName(PdfName.Get("S"))!.Value);
            Assert.Equal("Absatz_2", Assert.Single(Kids(top[1])).GetName(PdfName.Get("S"))!.Value);
            var roles = RoleMap(saved);
            Assert.Equal("P", roles.GetName(PdfName.Get("Absatz"))!.Value);
            Assert.Equal("H1", roles.GetName(PdfName.Get("Absatz_2"))!.Value);
            Assert.Equal("Absatz_2", roles.GetName(PdfName.Get("Kasten"))!.Value);   // an entry naming the renamed type follows
            Assert.DoesNotContain("Seiten aus einer anderen Datei", result.AccessibilityBreaks);
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>
    /// The other file maps the standard type P to Span: taken over as it is, the entry would make every paragraph of the copy a Span — its
    /// elements become „P_2“ mapped to Span, the copy's role map gets no entry for P.
    /// </summary>
    [Fact]
    public void StandardTypeMappedByTheOther_RenamedThere_CopyUntouched()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1));
        using var b = PdfDocument.Open(Tagged("Zweite Datei", 1, extra: o =>
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/RoleMap<</P/Span>>>>"));
        var (saved, _) = Merge(a, b, (0, 0), (1, 0));
        using (saved)
        {
            var top = Kids(Document(saved));
            Assert.Equal("P", top[0].GetName(PdfName.Get("S"))!.Value);
            Assert.Equal("P_2", Assert.Single(Kids(top[1])).GetName(PdfName.Get("S"))!.Value);
            Assert.False(RoleMap(saved).ContainsKey(PdfName.Get("P")));
            Assert.Equal("Span", RoleMap(saved).GetName(PdfName.Get("P_2"))!.Value);
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>
    /// Both files have an element with the ID „P1“: the other file's header cell becomes „P1_2“, the /Headers of its data cell follow, its
    /// /Ref follows by reference; the /IDTree of the copy names both. Before: a file with an /IDTree did not come.
    /// </summary>
    [Fact]
    public void DuplicateId_Renamed_HeadersFollow_RefFollows()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1, extra: o =>
        {
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/IDTree 16 0 R>>";
            o[16] = "<</Names[(P1) 22 0 R]>>";
            o[22] = "<</Type/StructElem/S/P/P 7 0 R/Pg 20 0 R/K 0/ID(P1)>>";
        }));
        using var b = PdfDocument.Open(Tagged("Zweite Datei", 1, extra: o =>
        {
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/IDTree 16 0 R>>";
            o[7] = "<</Type/StructElem/S/Document/P 6 0 R/K[40 0 R]>>";
            o[15] = "<</Nums[0[22 0 R 23 0 R]]>>";
            o[16] = "<</Names[(P1) 22 0 R]>>";
            o[21] = ("", "/TH <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Name) Tj ET EMC /TD <</MCID 1>> BDC BT /F1 12 Tf 72 680 Td (Erika Mustermann) Tj ET EMC");
            o[22] = "<</Type/StructElem/S/TH/P 41 0 R/Pg 20 0 R/K 0/ID(P1)>>";
            o[23] = "<</Type/StructElem/S/TD/P 41 0 R/Pg 20 0 R/K 1/A<</O/Table/Headers[(P1)]>>/Ref[22 0 R]>>";
            o[40] = "<</Type/StructElem/S/Table/P 7 0 R/K[41 0 R]>>";
            o[41] = "<</Type/StructElem/S/TR/P 40 0 R/K[22 0 R 23 0 R]>>";
        }));
        var (saved, result) = Merge(a, b, (0, 0), (1, 0));
        using (saved)
        {
            var table = Assert.Single(Kids(Kids(Document(saved))[1]));
            var row = Kids(Assert.Single(Kids(table)));
            var (header, cell) = (row[0], row[1]);
            Assert.Equal("P1_2", header.GetString(PdfName.Get("ID"))!.ToText());
            var headers = cell.GetDictionary(PdfName.Get("A"))!.GetArray(PdfName.Get("Headers"))!;
            Assert.Equal("P1_2", ((PdfString)headers.Get(0)).ToText());
            Assert.Same(header, cell.GetArray(PdfName.Get("Ref"))!.Get(0));
            var ids = NameTree.Pairs(Root(saved).GetDictionary(PdfName.Get("IDTree"))!)
                .ToDictionary(p => System.Text.Encoding.ASCII.GetString(p.Key), p => saved.Resolve(((PdfReference)p.Value).Id));
            Assert.Equal(["P1", "P1_2"], ids.Keys.Order());
            Assert.Same(Kids(Document(saved))[0], ids["P1"]);
            Assert.Same(header, ids["P1_2"]);
            Assert.DoesNotContain("Seiten aus einer anderen Datei", result.AccessibilityBreaks);
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>
    /// Both files have a class „Rot“ with other attributes, and a class „Gleich“ with the same: the other file's element names „Rot_2“, the
    /// class map of the copy has both red classes and one „Gleich“. Before: a file with a /ClassMap did not come.
    /// </summary>
    [Fact]
    public void ClassMapConflict_ForeignClassRenamed_SameClassShared()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1, extra: o =>
        {
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/ClassMap<</Rot<</O/Layout/Color[1 0 0]>>/Gleich<</O/Layout/TextAlign/Center>>>>>>";
            o[22] = "<</Type/StructElem/S/P/P 7 0 R/Pg 20 0 R/K 0/C/Rot>>";
        }));
        using var b = PdfDocument.Open(Tagged("Zweite Datei", 1, extra: o =>
        {
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/ClassMap<</Rot<</O/Layout/Color[0 0 1]>>/Gleich<</O/Layout/TextAlign/Center>>>>>>";
            o[22] = "<</Type/StructElem/S/P/P 7 0 R/Pg 20 0 R/K 0/C[/Rot 0 /Gleich]>>";
        }));
        var (saved, _) = Merge(a, b, (0, 0), (1, 0));
        using (saved)
        {
            var paragraph = Assert.Single(Kids(Kids(Document(saved))[1]));
            var classes = paragraph.GetArray(PdfName.Get("C"))!;
            Assert.Equal("Rot_2", ((PdfName)classes.Get(0)).Value);
            Assert.Equal(0, ((PdfInteger)classes.Get(1)).Value);
            Assert.Equal("Gleich", ((PdfName)classes.Get(2)).Value);
            var map = Root(saved).GetDictionary(PdfName.Get("ClassMap"))!;
            Assert.Equal(["Gleich", "Rot", "Rot_2"], map.Entries.Select(e => e.Key.Value).Order());
            Assert.Equal(1, ((PdfInteger)((PdfDictionary)map.Get(PdfName.Get("Rot"))!).GetArray(PdfName.Get("Color"))!.Get(0)).Value);
            Assert.Equal(0, ((PdfInteger)((PdfDictionary)map.Get(PdfName.Get("Rot_2"))!).GetArray(PdfName.Get("Color"))!.Get(0)).Value);
            StructureAssert.Clean(saved);
        }
    }

    // ------------------------------------------------------------ 6.5 c: a new file of tagged files (No. 640)

    /// <summary>All pages of the files given, in that order, into a new file on the empty document underneath (as „als neue Datei“ does).</summary>
    private static (PdfDocument Saved, PdfSaveResult Result) MergeAsNewFile(params PdfDocument[] files)
    {
        using var blank = BlankPage.CreateDocument(595.28, 841.89, 1);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(blank, [.. files.SelectMany(PageItem.From)], output);
        return (PdfDocument.Open(output.ToArray()), result);
    }

    /// <summary>
    /// Two tagged files merged as a new file: the result is tagged — a root with one Document holding a Part per file, /MarkInfo, the language,
    /// the title and „show the title“ of the first; the second's other language on its Part. Before (D-N5): untagged, without all of it.
    /// </summary>
    [Fact]
    public void NewFileOfTwoTaggedFiles_Tagged_TitleAndLanguageOfTheFirst()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1, title: "Bericht"));
        using var b = PdfDocument.Open(Tagged("Second file", 1, "en-US"));
        var (saved, _) = MergeAsNewFile(a, b);
        using (saved)
        {
            var catalog = saved.Catalog!;
            Assert.True(catalog.GetDictionary(PdfName.Get("MarkInfo"))!.GetBoolean(PdfName.Get("Marked")));
            Assert.Equal("de-DE", catalog.GetString(PdfName.Get("Lang"))!.ToText());
            Assert.True(catalog.GetDictionary(PdfName.Get("ViewerPreferences"))!.GetBoolean(PdfName.Get("DisplayDocTitle")));
            Assert.Equal("Bericht", saved.Info.Title);
            var root = Root(saved);
            var document = Assert.Single(Kids(root));
            Assert.Equal("Document", document.GetName(PdfName.Get("S"))!.Value);
            var parts = Kids(document);
            Assert.Equal(["Part", "Part"], parts.Select(e => e.GetName(PdfName.Get("S"))!.Value));
            Assert.Null(parts[0].GetString(PdfName.Get("Lang")));
            Assert.Equal("en-US", parts[1].GetString(PdfName.Get("Lang"))!.ToText());
            Assert.Equal("Second file Seite 1", TextOf(saved, Assert.Single(Kids(parts[1]))));
            Assert.Equal([0, 1], saved.Pages.Select(p => (int)p.Dictionary.GetInteger(PdfName.Get("StructParents"))!));
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>
    /// An untagged file first, a tagged one after it: the tree is built for the tagged one (decision I), the untagged page stays
    /// untagged; title and language come from the first tagged file.
    /// </summary>
    [Fact]
    public void NewFile_UntaggedFirst_TreeOfTheTaggedOne_ItsTitle()
    {
        using var plain = PdfDocument.Open(TestPdf.Simple(1).Build());
        using var b = PdfDocument.Open(Tagged("Zweite Datei", 1, title: "Anhang"));
        var (saved, _) = MergeAsNewFile(plain, b);
        using (saved)
        {
            Assert.Equal("Anhang", saved.Info.Title);
            Assert.Equal("de-DE", saved.Catalog!.GetString(PdfName.Get("Lang"))!.ToText());
            Assert.False(saved.Pages[0].Dictionary.ContainsKey(PdfName.Get("StructParents")));
            var part = Assert.Single(Kids(Document(saved)));
            Assert.Equal("Zweite Datei Seite 1", TextOf(saved, Assert.Single(Kids(part))));
            StructureAssert.Clean(saved, content: false);   // the untagged page holds content of no element — tagged in part
        }
    }

    /// <summary>
    /// A tagged file merged into an untagged document that keeps its own pages: the tree comes (a Part under a new Document), the document's
    /// own title and language stay as they are (none) — they are taken over only for a new file; a key its page kept from a tree it no longer
    /// has goes (it would name the Part's content).
    /// </summary>
    [Fact]
    public void TaggedIntoUntaggedDocument_TreeComes_OwnMetadataStay_StaleKeyGoes()
    {
        var pdf = TestPdf.Simple(1);
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        using var main = PdfDocument.Open(pdf.Build());
        using var b = PdfDocument.Open(Tagged("Zweite Datei", 1, title: "Anhang"));
        var (saved, _) = Merge(main, b, (0, 0), (1, 0));
        using (saved)
        {
            Assert.Equal("Testdokument", saved.Info.Title);   // its own
            Assert.Null(saved.Catalog!.GetString(PdfName.Get("Lang")));
            Assert.False(saved.Pages[0].Dictionary.ContainsKey(PdfName.Get("StructParents")));
            Assert.Equal(0, saved.Pages[1].Dictionary.GetInteger(PdfName.Get("StructParents")));
            Assert.Equal("Part", Assert.Single(Kids(Document(saved))).GetName(PdfName.Get("S"))!.Value);
            Assert.Empty(Core.Pdf.Accessibility.TaggedContent.ParentTree(saved));
        }
    }

    /// <summary>
    /// A new file whose only tagged file cannot come along (a file of PDF 2.0 with an Artifact element into the new file of 1.7; namespaces as
    /// such come along since 6.5 g): no tree, no /MarkInfo — the result claims nothing; its language and title still come from it.
    /// </summary>
    [Fact]
    public void NewFile_TreeCannotCome_NoRootNoMarkInfo()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1, title: "Bericht", extra: o =>
        {
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/Namespaces[16 0 R]>>";
            o[16] = "<</Type/Namespace/NS(http://iso.org/pdf2/ssn)>>";
            o[7] = "<</Type/StructElem/S/Document/NS 16 0 R/P 6 0 R/K[22 0 R]>>";
            o[22] = "<</Type/StructElem/S/Artifact/NS 16 0 R/P 7 0 R/Pg 20 0 R/K 0>>";
        }));
        var (saved, _) = MergeAsNewFile(a);
        using (saved)
        {
            Assert.False(saved.Catalog!.ContainsKey(PdfName.Get("StructTreeRoot")));
            Assert.False(saved.Catalog.ContainsKey(PdfName.Get("MarkInfo")));
            Assert.False(saved.Pages[0].Dictionary.ContainsKey(PdfName.Get("StructParents")));
            Assert.Equal("Bericht", saved.Info.Title);
        }
    }

    /// <summary>„Bereinigen: Metadaten“ on a new file: no title of another file (privacy), the tree comes all the same.</summary>
    [Fact]
    public void NewFile_MetadataCleanedUp_NoTitleOfTheOther()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1, title: "Bericht"));
        using var blank = BlankPage.CreateDocument(595.28, 841.89, 1);
        using var output = new MemoryStream();
        PdfWriter.Write(blank, [.. PageItem.From(a)], output, new PdfSaveOptions { Cleanup = CleanupItems.Metadata });
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.Null(saved.Info.Title);
        Assert.Equal("Part", Assert.Single(Kids(Document(saved))).GetName(PdfName.Get("S"))!.Value);
    }

    /// <summary>
    /// The first tagged file has its title only in its XMP (dc:title, as files made for PDF/UA often do): the new file takes it from there.
    /// Found with the measurement — the first draft read only /Info /Title, the result had no dc:title (ISO 14289-1, 7.1).
    /// </summary>
    [Fact]
    public void NewFile_TitleOnlyInTheXmp_Taken()
    {
        const string xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF " +
            "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Nur im XMP</rdf:li></rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta>" +
            "<?xpacket end=\"w\"?>";
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1, extra: o =>
        {
            o[1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 6 0 R/MarkInfo<</Marked true>>/Lang(de-DE)/Metadata 4 0 R>>";
            o[4] = ("/Type/Metadata/Subtype/XML", xmp);
        }));
        var (saved, _) = MergeAsNewFile(a);
        using (saved) Assert.Equal("Nur im XMP", saved.Info.Title);
    }

    // ------------------------------------------------------------ 6.5 d: a page taken more than once (No. 642)

    /// <summary>Writes the pages of one file in the order given (indices).</summary>
    private static (PdfDocument Saved, PdfSaveResult Result) Pages(PdfDocument doc, params int[] order)
    {
        var items = PageItem.From(doc);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, [.. order.Select(i => items[i])], output);
        return (PdfDocument.Open(output.ToArray()), result);
    }

    /// <summary>The element the parent tree gives a page's MCID.</summary>
    private static PdfDictionary ElementOfMcid(PdfDocument doc, int page, int mcid)
    {
        var key = doc.Pages[page].Dictionary.GetInteger(PdfName.Get("StructParents"))!.Value;
        var array = (PdfArray)NumberTree.Value(Root(doc).GetDictionary(PdfName.Get("ParentTree"))!, key)!;
        return (PdfDictionary)array.Get(mcid)!;
    }

    /// <summary>
    /// The first page of a file taken again at the end: the copy has a key of its own, its paragraph is a copy with /Pg on the copy, under a
    /// part at the end; the first copy keeps its own; no reason „dieselbe Seite mehrfach“. Before: both pages named the same paragraph,
    /// whose /Pg was the first, and the save took the identification.
    /// </summary>
    [Fact]
    public void SamePageTwice_SecondCopyHasElementsOfItsOwn()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 2));
        var (saved, result) = Pages(a, 0, 1, 0);
        using (saved)
        {
            var first = ElementOfMcid(saved, 0, 0);
            var copy = ElementOfMcid(saved, 2, 0);
            Assert.NotSame(first, copy);
            Assert.NotEqual(saved.Pages[0].Dictionary.GetInteger(PdfName.Get("StructParents")), saved.Pages[2].Dictionary.GetInteger(PdfName.Get("StructParents")));
            Assert.Same(saved.Pages[2].Dictionary, copy.Get(PdfName.Get("Pg")));
            Assert.Equal("Erste Datei Seite 1", TextOf(saved, copy));
            var top = Kids(Document(saved));
            Assert.Equal(["P", "P", "Part"], top.Select(e => e.GetName(PdfName.Get("S"))!.Value));
            Assert.Same(copy, Assert.Single(Kids(top[2])));
            Assert.DoesNotContain("dieselbe Seite mehrfach", result.AccessibilityBreaks);
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>
    /// A table cell on a page taken twice: the copy keeps its row and table (ancestors copied up to below the Document), without the cell's
    /// /ID (IDs are unique); the /IDTree names only the original.
    /// </summary>
    [Fact]
    public void SamePageTwice_CellCopiedWithRowAndTable_WithoutId()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1, extra: o =>
        {
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/IDTree 16 0 R>>";
            o[7] = "<</Type/StructElem/S/Document/P 6 0 R/K[40 0 R]>>";
            o[16] = "<</Names[(Z1) 22 0 R]>>";
            o[21] = ("", "/TD <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Zelle) Tj ET EMC");
            o[22] = "<</Type/StructElem/S/TD/P 41 0 R/Pg 20 0 R/K 0/ID(Z1)>>";
            o[40] = "<</Type/StructElem/S/Table/P 7 0 R/K[41 0 R]>>";
            o[41] = "<</Type/StructElem/S/TR/P 40 0 R/K[22 0 R]>>";
        }));
        var (saved, result) = Pages(a, 0, 0);
        using (saved)
        {
            var cell = ElementOfMcid(saved, 1, 0);
            Assert.Equal("TD", cell.GetName(PdfName.Get("S"))!.Value);
            Assert.False(cell.ContainsKey(PdfName.Get("ID")));
            var row = (PdfDictionary)cell.Get(PdfName.Get("P"))!;
            var table = (PdfDictionary)row.Get(PdfName.Get("P"))!;
            Assert.Equal(["TR", "Table", "Part"], new[] { row, table, (PdfDictionary)table.Get(PdfName.Get("P"))! }.Select(e => e.GetName(PdfName.Get("S"))!.Value));
            Assert.Equal("Z1", ((PdfString)ElementOfMcid(saved, 0, 0).Get(PdfName.Get("ID"))!).ToText());
            Assert.DoesNotContain("dieselbe Seite mehrfach", result.AccessibilityBreaks);
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>
    /// A page of a merged file taken twice: its tree comes (before: the whole file fell back), the further copy gets copies of its elements
    /// in a part of their own at its place, in the merged file's language.
    /// </summary>
    [Fact]
    public void MergedPageTwice_TreeComes_SecondCopyHasElementsOfItsOwn()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1));
        using var b = PdfDocument.Open(Tagged("Second file", 1, "en-US"));
        var (saved, result) = Merge(a, b, (1, 0), (0, 0), (1, 0));
        using (saved)
        {
            var top = Kids(Document(saved));
            Assert.Equal(["Part", "P", "Part"], top.Select(e => e.GetName(PdfName.Get("S"))!.Value));
            Assert.Equal("en-US", top[2].GetString(PdfName.Get("Lang"))!.ToText());
            var copy = Assert.Single(Kids(top[2]));
            Assert.Same(copy, ElementOfMcid(saved, 2, 0));
            Assert.Equal("Second file Seite 1", TextOf(saved, copy));
            Assert.NotSame(ElementOfMcid(saved, 0, 0), copy);
            Assert.DoesNotContain("Seiten aus einer anderen Datei", result.AccessibilityBreaks);
            Assert.DoesNotContain("dieselbe Seite mehrfach", result.AccessibilityBreaks);
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>
    /// A page drawing a form with keys of its own (/StructParents), taken twice: the further copy keeps no structure (its form would name the
    /// first copy's elements) — no key, the save says why.
    /// </summary>
    [Fact]
    public void PageWithFormKeys_Twice_CopyWithoutStructure_ReasonGiven()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1, extra: o =>
        {
            o[20] = "<</Type/Page/Parent 2 0 R/Contents 21 0 R/Resources<</Font<</F1 5 0 R>>/XObject<</X1 25 0 R>>>>/StructParents 0>>";
            o[21] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Erste Datei) Tj ET EMC /X1 Do");
            o[25] = ("/Type/XObject/Subtype/Form/BBox[0 0 100 100]/StructParents 1", "/Figure <</MCID 0>> BDC 0 0 m 50 50 l S EMC");
            o[27] = "<</Type/StructElem/S/Figure/P 7 0 R/Pg 20 0 R/Alt(Linie)/K<</Type/MCR/MCID 0/Stm 25 0 R/Pg 20 0 R>>>>";
            o[7] = "<</Type/StructElem/S/Document/P 6 0 R/K[22 0 R 27 0 R]>>";
            o[15] = "<</Nums[0[22 0 R] 1[27 0 R]]>>";
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 2>>";
        }));
        var (saved, result) = Pages(a, 0, 0);
        using (saved)
        {
            Assert.Contains("dieselbe Seite mehrfach", result.AccessibilityBreaks);
            Assert.Equal(saved.Pages[0].Dictionary.GetInteger(PdfName.Get("StructParents")), saved.Pages[1].Dictionary.GetInteger(PdfName.Get("StructParents")));
        }
    }

    // ------------------------------------------------------------ 6.5 g: namespaces (No. 646)

    /// <summary>
    /// A tagged file whose elements are in the namespace for PDF 2.0 (object 16, with <paramref name="nsExtra"/> in it); <paramref name="extra"/>
    /// changes objects after (a /S of its own; elements keep the /NS).
    /// </summary>
    private static byte[] Spaced(string name, int pages, string lang = "de-DE", string nsExtra = "", Action<Dictionary<int, object>>? extra = null) =>
        Tagged(name, pages, lang, o =>
        {
            foreach (var key in o.Keys.ToList())
                if (o[key] is string text && text.Contains("/Type/StructElem")) o[key] = text.Replace("/Type/StructElem", "/Type/StructElem/NS 16 0 R");
            o[6] = ((string)o[6])[..^2] + "/Namespaces[16 0 R]>>";
            o[16] = $"<</Type/Namespace/NS(http://iso.org/pdf2/ssn){nsExtra}>>";
            extra?.Invoke(o);
        });

    private static (PdfDocument Saved, PdfSaveResult Result) MergeAs(string version, PdfDocument main, PdfDocument other, params (int File, int Page)[] order)
    {
        var items = new[] { PageItem.From(main), PageItem.From(other) };
        using var output = new MemoryStream();
        var result = PdfWriter.Write(main, [.. order.Select(o => items[o.File][o.Page])], output, new PdfSaveOptions { Version = version });
        return (PdfDocument.Open(output.ToArray()), result);
    }

    private static PdfArray NamespacesOf(PdfDocument doc) => Root(doc).GetArray(PdfName.Get("Namespaces"))!;

    /// <summary>
    /// Two files of PDF 2.0 with the same namespace, written as 2.0: one namespace in the copy, the merged elements name it; the whole file a
    /// Document inside the Document (ISO 14289-2, 8.2.5.2). Before: a tree with namespaces did not come.
    /// </summary>
    [Fact]
    public void TwoPdf20Files_SameNamespace_SharedInTheCopy()
    {
        using var a = PdfDocument.Open(Spaced("Erste Datei", 1));
        using var b = PdfDocument.Open(Spaced("Zweite Datei", 1));
        var (saved, result) = MergeAs("2.0", a, b, (0, 0), (1, 0));
        using (saved)
        {
            var space = Assert.Single(Enumerable.Range(0, NamespacesOf(saved).Count).Select(NamespacesOf(saved).Get));
            var top = Kids(Document(saved));
            Assert.Equal(["P", "Document"], top.Select(e => e.GetName(PdfName.Get("S"))!.Value));
            var paragraph = Assert.Single(Kids(top[1]));
            Assert.Same(space, paragraph.Get(PdfName.Get("NS")));
            Assert.Same(space, top[1].Get(PdfName.Get("NS")));
            Assert.DoesNotContain("Seiten aus einer anderen Datei", result.AccessibilityBreaks);
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>The same address, other entries (here a schema): two namespaces in the copy — never joined by the address alone.</summary>
    [Fact]
    public void SameAddressOtherEntries_BothNamespacesKept()
    {
        using var a = PdfDocument.Open(Spaced("Erste Datei", 1));
        using var b = PdfDocument.Open(Spaced("Zweite Datei", 1, nsExtra: "/Schema(Muster)"));
        var (saved, _) = MergeAs("2.0", a, b, (0, 0), (1, 0));
        using (saved)
        {
            Assert.Equal(2, NamespacesOf(saved).Count);
            var paragraph = Assert.Single(Kids(Kids(Document(saved))[1]));
            Assert.Same(NamespacesOf(saved).Get(1), paragraph.Get(PdfName.Get("NS")));
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>A file of PDF 1.7 into one with the 2.0 namespace, written as 2.0: its common types get that namespace, a type only 1.7 has (Note) not.</summary>
    [Fact]
    public void Pdf17FileInto20_CommonTypesGetThe20Namespace()
    {
        using var a = PdfDocument.Open(Spaced("Erste Datei", 1));
        using var b = PdfDocument.Open(Tagged("Zweite Datei", 1, extra: o =>
        {
            o[7] = "<</Type/StructElem/S/Document/P 6 0 R/K[22 0 R 23 0 R]>>";
            o[21] = ("", "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Zweite Datei) Tj ET EMC /Note <</MCID 1>> BDC BT /F1 12 Tf 72 680 Td (Anmerkung) Tj ET EMC");
            o[23] = "<</Type/StructElem/S/Note/P 7 0 R/Pg 20 0 R/K 1/ID(N1)>>";
            o[15] = "<</Nums[0[22 0 R 23 0 R]]>>";
            o[6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 1/IDTree 24 0 R>>";
            o[24] = "<</Names[(N1) 23 0 R]>>";
        }));
        var (saved, _) = MergeAs("2.0", a, b, (0, 0), (1, 0));
        using (saved)
        {
            var space = NamespacesOf(saved).Get(0);
            var kids = Kids(Kids(Document(saved))[1]);
            Assert.Same(space, kids[0].Get(PdfName.Get("NS")));
            Assert.Equal("Note", kids[1].GetName(PdfName.Get("S"))!.Value);
            Assert.False(kids[1].ContainsKey(PdfName.Get("NS")));
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>
    /// A file of PDF 2.0 into one of 1.7, written as 1.7: its elements become types of 1.7 (Title → P, PDF-Knead's own mapping), without
    /// /NS, the copy without /Namespaces. Before: its tree did not come.
    /// </summary>
    [Fact]
    public void Pdf20FileInto17_ElementsWrittenAs17()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1));
        using var b = PdfDocument.Open(Spaced("Zweite Datei", 1, extra: o => o[22] = ((string)o[22]).Replace("/S/P", "/S/Title")));
        var (saved, result) = MergeAs("1.7", a, b, (0, 0), (1, 0));
        using (saved)
        {
            Assert.False(Root(saved).ContainsKey(PdfName.Get("Namespaces")));
            var part = Kids(Document(saved))[1];
            Assert.Equal("Part", part.GetName(PdfName.Get("S"))!.Value);
            var title = Assert.Single(Kids(part));
            Assert.Equal("P", title.GetName(PdfName.Get("S"))!.Value);
            Assert.False(title.ContainsKey(PdfName.Get("NS")));
            Assert.Equal("Zweite Datei Seite 1", TextOf(saved, title));
            Assert.DoesNotContain("Seiten aus einer anderen Datei", result.AccessibilityBreaks);
            StructureAssert.Clean(saved);
        }
    }

    /// <summary>
    /// A file of PDF 2.0 with an Artifact element into one of 1.7: PDF 1.7 cannot name it, and the save does not rewrite the merged page's
    /// content — its tree keeps the state before (its keys go, the save says so).
    /// </summary>
    [Fact]
    public void Pdf20FileWithArtifactInto17_FallsBack_ReasonGiven()
    {
        using var a = PdfDocument.Open(Tagged("Erste Datei", 1));
        using var b = PdfDocument.Open(Spaced("Zweite Datei", 1, extra: o => o[22] = ((string)o[22]).Replace("/S/P", "/S/Artifact")));
        var (saved, result) = MergeAs("1.7", a, b, (0, 0), (1, 0));
        using (saved)
        {
            Assert.False(saved.Pages[1].Dictionary.ContainsKey(PdfName.Get("StructParents")));
            Assert.Contains("Seiten aus einer anderen Datei", result.AccessibilityBreaks);
        }
    }
}
