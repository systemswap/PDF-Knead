using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Saving: rewriting completely, reordering pages, merging files.</summary>
public sealed class WriterTests
{
    // TestPdf.Simple(3): catalog 1, /Pages 2, font 3, pages 5, 7, 9 (each preceded by its content), Info 10
    private static int PageNumber(int index) => 5 + 2 * index;

    /// <summary>
    /// Three pages with one bookmark per page, a text field "Name" on page 2 and a link on
    /// page 1 that points to page 3 via a named destination ("ziel3").
    /// </summary>
    private static byte[] Rich(XRefStyle style = XRefStyle.Table)
    {
        var pdf = TestPdf.Simple();
        var outlines = pdf.Add(new PdfDictionary());
        var items = new List<int>();
        for (var i = 0; i < 3; i++)
            items.Add(pdf.Add(TestPdf.Dict(("Title", new PdfString(Encoding.ASCII.GetBytes($"Kapitel {i + 1}"))),
                ("Parent", TestPdf.Ref(outlines)),
                ("Dest", new PdfArray([TestPdf.Ref(PageNumber(i)), TestPdf.N("XYZ"), PdfInteger.Of(0), PdfInteger.Of(800), PdfInteger.Of(0)])))));
        for (var i = 0; i < 3; i++)
        {
            var item = (PdfDictionary)pdf.Get(items[i]);
            if (i > 0) item.Set(PdfName.Get("Prev"), TestPdf.Ref(items[i - 1]));
            if (i < 2) item.Set(PdfName.Get("Next"), TestPdf.Ref(items[i + 1]));
        }
        pdf.Replace(outlines, TestPdf.Dict(("Type", TestPdf.N("Outlines")), ("First", TestPdf.Ref(items[0])), ("Last", TestPdf.Ref(items[2])),
            ("Count", PdfInteger.Of(3))));

        var field = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Tx")), ("T", new PdfString("Name"u8.ToArray())), ("Type", TestPdf.N("Annot")),
            ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(100, 100, 300, 120)), ("P", TestPdf.Ref(PageNumber(1))),
            ("V", new PdfString("Max Mustermann"u8.ToArray()))));
        ((PdfDictionary)pdf.Get(PageNumber(1))).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(field)]));

        var link = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Link")), ("Rect", TestPdf.Box(0, 0, 50, 50)),
            ("Dest", new PdfString("ziel3"u8.ToArray()))));
        ((PdfDictionary)pdf.Get(PageNumber(0))).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(link)]));

        var names = TestPdf.Dict(("Dests", TestPdf.Dict(("Names", new PdfArray([new PdfString("ziel3"u8.ToArray()),
            new PdfArray([TestPdf.Ref(PageNumber(2)), TestPdf.N("Fit")])])))));
        pdf.Replace(1, TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(2)), ("Outlines", TestPdf.Ref(outlines)),
            ("AcroForm", TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(field)])))), ("Names", names),
            ("PageLabels", TestPdf.Dict(("Nums", new PdfArray([PdfInteger.Of(0), TestPdf.Dict(("S", TestPdf.N("r")))])))),
            ("OpenAction", new PdfArray([TestPdf.Ref(PageNumber(2)), TestPdf.N("Fit")]))));
        return pdf.Build(style);
    }

    private static PdfDocument Save(PdfDocument main, IReadOnlyList<PageItem> pages, PdfSaveOptions? options = null, string? password = null)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(main, pages, output, options);
        var doc = PdfDocument.Open(output.ToArray(), password);
        Assert.False(doc.WasRepaired);
        return doc;
    }

    /// <summary>
    /// Guard for a deliberate simplification (docs/ABLAUFPLAN.md, 5.3 h, B-10): the writer numbers its objects without gaps, so the only free
    /// entry of a written table is object 0 — the free entries need not be chained (ISO 32000-2, 7.5.4). Should a gap ever appear, this fails.
    /// </summary>
    [Fact]
    public void SavedFile_HasNoFreeEntriesBesideZero()
    {
        using var main = PdfDocument.Open(Rich());
        var pages = PageItem.From(main);
        using var output = new MemoryStream();
        PdfWriter.Write(main, [pages[1], pages[0], .. pages.Skip(2)], output, new PdfSaveOptions { Version = "1.4" });
        var text = System.Text.Encoding.Latin1.GetString(output.ToArray());
        var table = text[(text.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 6)..text.LastIndexOf("trailer", StringComparison.Ordinal)];
        var free = System.Text.RegularExpressions.Regex.Matches(table, @"^\d{10} \d{5} f", System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.Equal(["0000000000 65535 f"], free.Select(m => m.Value));
    }

    private static string FieldName(PdfDocument doc, int index) =>
        doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!.Get(index).AsDictionary()!.GetString(PdfName.Get("T"))!.ToText();

    [Theory]
    [InlineData("1.4", XRefStyle.Table)]
    [InlineData("1.7", XRefStyle.Table)]
    [InlineData("1.7", XRefStyle.Stream)]
    [InlineData("2.0", XRefStyle.Stream)]
    public void Save_KeepsEverything(string version, XRefStyle style)
    {
        using var original = PdfDocument.Open(Rich(style));
        using var copy = Save(original, PageItem.From(original), new PdfSaveOptions { Version = version });
        Assert.Equal(version, copy.HeaderVersion);
        Assert.Equal(3, copy.PageCount);
        Assert.Equal("Testdokument", copy.Info.Title);
        Assert.StartsWith("PDF-Knead", copy.Info.Producer);
        for (var i = 0; i < 3; i++)
        {
            // Same content; inherited size and fonts are written on the page itself in the copy
            Assert.Equal(TestPdf.PageContent(i + 1), copy.GetStreamData(copy.Pages[i].Dictionary.GetStream(PdfName.Contents)!));
            Assert.NotNull(copy.Pages[i].Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Font")));
            Assert.Equal(original.Pages[i].Width, copy.Pages[i].Width, 3);
            Assert.Equal(original.Pages[i].Rotate, copy.Pages[i].Rotate);
        }
        // Unchanged: everything stays, including page labels and named destinations
        Assert.Equal(["Kapitel 1", "Kapitel 2", "Kapitel 3"], PdfOutline.Read(copy).Select(o => o.Title));
        Assert.Equal([0, 1, 2], PdfOutline.Read(copy).Select(o => o.PageIndex ?? -1));
        Assert.NotNull(copy.Catalog!.GetDictionary(PdfName.Get("PageLabels")));
        Assert.Equal("Name", FieldName(copy, 0));
        var link = copy.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0).AsDictionary()!;
        Assert.Equal(2, PdfOutline.ResolveDestination(copy, link.Get(PdfName.Get("Dest"))));
    }

    [Fact]
    public void Reorder_TrimsOutlineAndForm()
    {
        using var original = PdfDocument.Open(Rich());
        var all = PageItem.From(original);
        using var copy = Save(original, [all[2], all[0]]);   // page 2 (with the field) deleted, 3 before 1

        Assert.Equal(TestPdf.PageContent(3), copy.GetStreamData(copy.Pages[0].Dictionary.GetStream(PdfName.Contents)!));
        var outline = PdfOutline.Read(copy);
        Assert.Equal(["Kapitel 1", "Kapitel 3"], outline.Select(o => o.Title));
        Assert.Equal([1, 0], outline.Select(o => o.PageIndex ?? -1));
        Assert.Empty(copy.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!);
        // the page labels follow their pages (docs/FEHLERGEDAECHTNIS.md, No. 494; before they were dropped)
        Assert.Equal([PageLabels.Of(original)![2], PageLabels.Of(original)![0]], PageLabels.Of(copy)!);
        Assert.Equal(0, PdfOutline.ResolveDestination(copy, copy.Catalog.Get(PdfName.Get("OpenAction"))));
    }

    [Fact]
    public void Merge_RenamesFieldsAndAddsOutlineBranch()
    {
        using var a = PdfDocument.Open(Rich());
        using var b = PdfDocument.Open(Rich(XRefStyle.Stream));
        using var copy = Save(a, [.. PageItem.From(a), .. PageItem.From(b)]);

        Assert.Equal(6, copy.PageCount);
        Assert.Equal("Name", FieldName(copy, 0));
        Assert.Equal("Name_2", FieldName(copy, 1));
        var outline = PdfOutline.Read(copy);
        Assert.Equal(4, outline.Count);
        Assert.Equal("Eingefügte Seiten", outline[3].Title);
        Assert.Equal(3, outline[3].PageIndex);
        Assert.Equal([3, 4, 5], outline[3].Children.Select(o => o.PageIndex ?? -1));
        // The named destination of the inserted file is replaced by the destination itself
        var link = copy.Pages[3].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0).AsDictionary()!;
        Assert.IsType<PdfArray>(link.Get(PdfName.Get("Dest")));
        Assert.Equal(5, PdfOutline.ResolveDestination(copy, link.Get(PdfName.Get("Dest"))));
        // Field value and widget of the second file are attached to its page
        var widget = copy.Pages[4].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0).AsDictionary()!;
        Assert.Equal("Name_2", widget.GetString(PdfName.Get("T"))!.ToText());
        Assert.Same(copy.Pages[4].Dictionary, widget.GetDictionary(PdfName.Get("P")));
    }

    [Theory]
    [InlineData(PdfCipher.Rc4)]
    [InlineData(PdfCipher.Aes128)]
    [InlineData(PdfCipher.Aes256)]
    public void Save_KeepsEncryption(PdfCipher cipher)
    {
        var bytes = TestPdf.Simple().Build(XRefStyle.Stream, cipher, user: "geheim", owner: "chef", permissions: PdfPermissions.Print);
        using var original = PdfDocument.Open(bytes, "geheim");
        using var output = new MemoryStream();
        var result = PdfWriter.Write(original, output, new PdfSaveOptions { Version = "1.4" });
        Assert.True(result.VersionRaised == (cipher != PdfCipher.Rc4));   // AES needs 1.6 or 1.7

        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(output.ToArray()));
        using var copy = PdfDocument.Open(output.ToArray(), "geheim");
        Assert.Equal(PdfPermissions.Print | PdfPermissions.Accessibility, copy.Security!.Permissions);
        Assert.Equal("Testdokument", copy.Info.Title);
        Assert.Equal(TestPdf.PageContent(2), copy.GetStreamData(copy.Pages[1].Dictionary.GetStream(PdfName.Contents)!));
        using var owner = PdfDocument.Open(output.ToArray(), "chef");
        Assert.True(owner.Security!.IsOwner);
    }

    /// <summary>
    /// An encrypted file without /ID (its key derived with an empty identifier): the kept encryption keeps that identifier, so the copy opens
    /// as the original does — with a new one it needed a password nobody has (docs/FEHLERGEDAECHTNIS.md, No. 345).
    /// </summary>
    [Theory]
    [InlineData(PdfCipher.Rc4)]
    [InlineData(PdfCipher.Aes128)]
    public void Save_EncryptedWithoutId_StaysReadable(PdfCipher cipher)
    {
        var bytes = TestPdf.Simple().Build(cipher: cipher, user: "", owner: "chef", withoutId: true);
        using var original = PdfDocument.Open(bytes);
        using var copy = Save(original, PageItem.From(original));
        Assert.True(copy.IsEncrypted);
        Assert.Equal(TestPdf.PageContent(2), copy.GetStreamData(copy.Pages[1].Dictionary.GetStream(PdfName.Contents)!));
    }

    [Fact]
    public void Save_WithoutEncryption_WhenAskedTo()
    {
        var bytes = TestPdf.Simple().Build(cipher: PdfCipher.Aes256, user: "geheim", owner: "chef");
        using var original = PdfDocument.Open(bytes, "chef");
        using var copy = Save(original, PageItem.From(original), new PdfSaveOptions { KeepEncryption = false });
        Assert.False(copy.IsEncrypted);
        Assert.Equal(TestPdf.PageContent(3), copy.GetStreamData(copy.Pages[2].Dictionary.GetStream(PdfName.Contents)!));
    }

    [Fact]
    public void Rotation_IsSaved()
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var items = PageItem.From(original);
        items[0] = items[0].Rotated(90);
        items[1] = items[1].Rotated(-90);   // was 90
        using var copy = Save(original, items);
        Assert.Equal(90, copy.Pages[0].Rotate);
        Assert.Equal(0, copy.Pages[1].Rotate);
        Assert.Null(copy.Pages[1].Dictionary.GetRaw(PdfName.Rotate));
    }

    [Fact]
    public void ManyPages_BalancedTree_KeepsOrder()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(300, null).Build(XRefStyle.Stream));
        using var copy = Save(original, PageItem.From(original));
        Assert.Equal(300, copy.PageCount);
        Assert.True(copy.Catalog!.GetDictionary(PdfName.Pages)!.GetArray(PdfName.Kids)!.Count <= 64);
        foreach (var i in new[] { 0, 63, 64, 150, 299 })
            Assert.Equal(TestPdf.PageContent(i + 1), copy.GetStreamData(copy.Pages[i].Dictionary.GetStream(PdfName.Contents)!));
    }

    [Fact]
    public void InheritedDirectResources_AreWrittenOnce()
    {
        // TestPdf.Simple: /Resources is stored directly on the /Pages node
        using var original = PdfDocument.Open(TestPdf.Simple(200, null).Build());
        using var output = new MemoryStream();
        PdfWriter.Write(original, output);
        using var copy = PdfDocument.Open(output.ToArray());
        var numbers = copy.Pages.Select(p => Assert.IsType<PdfReference>(p.Dictionary.GetRaw(PdfName.Resources)).Id.Number).Distinct();
        Assert.Single(numbers);
        Assert.NotNull(copy.Pages[150].Resources?.GetDictionary(PdfName.Get("Font")));
    }

    [Fact]
    public void SamePageTwice_AndBlankPage()
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var items = PageItem.From(original);
        var blank = BlankPage.Create(595.28, 841.89);
        using var copy = Save(original, [items[0], blank, items[0]]);
        Assert.Equal(3, copy.PageCount);
        Assert.NotSame(copy.Pages[0].Dictionary, copy.Pages[2].Dictionary);
        Assert.Equal(TestPdf.PageContent(1), copy.GetStreamData(copy.Pages[2].Dictionary.GetStream(PdfName.Contents)!));
        Assert.Equal(("A4", false), PageFormat.Detect(copy.Pages[1].Width, copy.Pages[1].Height));
        blank.Source.Dispose();
    }

    [Fact]
    public void Reals_KeepTheirExactValue()
    {
        Assert.Equal("0.123456789", PdfSerializer.FormatReal(0.123456789));
        Assert.Equal("0.0000001", PdfSerializer.FormatReal(1e-7));
        Assert.Equal("-12.5", PdfSerializer.FormatReal(-12.5));
        Assert.Equal("0", PdfSerializer.FormatReal(-0.0));
    }

    [Fact]
    public void EmptyPageList_IsRefused()
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        Assert.Throws<PdfException>(() => PdfWriter.Write(original, [], new MemoryStream()));
    }
}

public sealed class PageEditingTests
{
    private static (PdfDocument Doc, PageHistory History) Five()
    {
        var doc = PdfDocument.Open(TestPdf.Simple(5).Build());
        return (doc, new PageHistory(PageItem.From(doc)));
    }

    private static int[] Order(PageHistory h) => [.. h.Pages.Select(p => p.Page.Index + 1)];

    [Fact]
    public void Move_ToTheEnd_AndBack()
    {
        var (doc, h) = Five();
        using (doc)
        {
            Assert.Equal(3, h.Move([0, 1], 5));   // the first two to the end
            Assert.Equal([3, 4, 5, 1, 2], Order(h));
            Assert.Equal(0, h.Move([3], 0));
            Assert.Equal([1, 3, 4, 5, 2], Order(h));
            Assert.True(h.IsModified);
            h.Undo();
            h.Undo();
            Assert.Equal([1, 2, 3, 4, 5], Order(h));
            Assert.False(h.IsModified);
            h.Redo();
            Assert.Equal([3, 4, 5, 1, 2], Order(h));
            Assert.Equal(-1, h.Move([], 2));
        }
    }

    [Fact]
    public void Move_BeforeOwnPosition_ChangesNothing()
    {
        var (doc, h) = Five();
        using (doc)
        {
            h.Move([2], 2);
            h.Move([2], 3);
            Assert.False(h.CanUndo);
        }
    }

    [Fact]
    public void Delete_KeepsAtLeastOnePage()
    {
        var (doc, h) = Five();
        using (doc)
        {
            Assert.False(h.Delete([0, 1, 2, 3, 4]));
            Assert.True(h.Delete([1, 3]));
            Assert.Equal([1, 3, 5], Order(h));
            h.Rotate([0], 90);
            Assert.Equal(90, h.Pages[0].Page.Rotate);
            Assert.Equal(Tr.T("Nach rechts drehen"), h.UndoDescription);
            h.Undo();
            Assert.Equal(0, h.Pages[0].Page.Rotate);
            h.Insert(1, [BlankPage.Create(100, 100)], "Leerseite");
            Assert.Equal(4, h.Count);
            Assert.False(h.CanRedo);
            Assert.Equal(2, h.Sources().Count);
            foreach (var source in h.Sources()) if (!ReferenceEquals(source, doc)) source.Dispose();
        }
    }

    [Theory]
    [InlineData("1-3, 7", 10, new[] { 0, 1, 2, 6 })]
    [InlineData("8-", 10, new[] { 7, 8, 9 })]
    [InlineData("-2;5", 10, new[] { 0, 1, 4 })]
    [InlineData("3-1", 10, new[] { 2, 1, 0 })]
    [InlineData("2, 2, 2", 10, new[] { 1 })]
    [InlineData("", 10, new int[0])]
    public void PageRange_Parses(string text, int count, int[] expected) => Assert.Equal(expected, PageRange.Parse(text, count));

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("a-b")]
    [InlineData("1-2-3")]
    public void PageRange_RejectsNonsense(string text) => Assert.Null(PageRange.Parse(text, 10));

    [Fact]
    public void PageRange_Formats() => Assert.Equal("1-3, 7, 9-10", PageRange.Format([0, 1, 2, 6, 8, 9]));

    /// <summary>
    /// Filter names abbreviated as in inline images (/AHx, /A85, …) and /DP in streams of the source: readers take them, the standard keeps
    /// them to inline images (ISO 32000-2, 8.9.7, Table 92) — the copy writes the full names and /DecodeParms, the data as it was; the page
    /// looks the same (docs/FEHLERGEDAECHTNIS.md, No. 879).
    /// </summary>
    [Fact]
    public void AbbreviatedFilterNamesInStreams_WrittenInFull()
    {
        const string content = "q 100 0 0 50 10 10 cm /Im0 Do Q 0 0 1 rg 200 200 50 50 re f";
        var file = PdfARuleCases.Raw([
            PdfARuleCases.Obj(1, "<</Type/Catalog/Pages 2 0 R>>"),
            PdfARuleCases.Obj(2, "<</Type/Pages/Kids[3 0 R]/Count 1>>"),
            PdfARuleCases.Obj(3, "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</XObject<</Im0 5 0 R>>>>>>"),
            PdfARuleCases.Stream(4, "/Filter/AHx", Convert.ToHexString(Encoding.ASCII.GetBytes(content)) + ">"),
            PdfARuleCases.Stream(5, "/Type/XObject/Subtype/Image/Width 2/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8/Filter[/AHx]/DP[null]", "20E0>"),
        ]);
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions());
        using var saved = PdfDocument.Open(output.ToArray());
        var page = saved.Pages[0].Dictionary;
        var text = page.GetStream(PdfName.Contents)!;
        Assert.Equal("ASCIIHexDecode", text.Dictionary.GetName(PdfName.Filter)?.Value);
        Assert.Equal(content, Encoding.ASCII.GetString(saved.GetStreamData(text)));
        var image = page.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!.GetStream(PdfName.Get("Im0"))!;
        Assert.Equal(["ASCIIHexDecode"], image.Dictionary.GetArray(PdfName.Filter)!.Select(f => ((PdfName)f).Value).ToArray());
        Assert.False(image.Dictionary.ContainsKey(PdfName.DP));
        Assert.NotNull(image.Dictionary.GetArray(PdfName.DecodeParms));
        Assert.Equal(new byte[] { 0x20, 0xE0 }, saved.GetStreamData(image));
    }

    /// <summary>
    /// A key of another program naming the root of the page tree: the copy writes the tree anew, and the key names its root — it stands for
    /// all pages as the old one did. It went (a reference to a node of the old tree is dropped; docs/FEHLERGEDAECHTNIS.md, No. 881; found by the
    /// data-keeping round trip). A node below the root has no counterpart and still goes.
    /// </summary>
    [Fact]
    public void ForeignKeyNamingThePageTreeRoot_NamesTheRootOfTheCopy()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/XYZ_Alle 2 0 R/XYZ_Teil 3 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Pages/Parent 2 0 R/Kids[4 0 R]/Count 1>>",
            [4] = "<</Type/Page/Parent 3 0 R/Contents 6 0 R/Resources<<>>>>",
            [6] = ("", "0 0 m 100 100 l S"),
        });
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output);
        using var saved = PdfDocument.Open(output.ToArray());
        var catalog = saved.Catalog!;
        var root = Assert.IsType<PdfReference>(catalog.GetRaw(PdfName.Pages));
        Assert.Equal(root.Id, Assert.IsType<PdfReference>(catalog.GetRaw(PdfName.Get("XYZ_Alle"))).Id);
        Assert.Null(catalog.Get(PdfName.Get("XYZ_Teil")) as PdfDictionary);
    }
}
