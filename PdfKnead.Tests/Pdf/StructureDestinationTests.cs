using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Structure destinations (/SD of a GoTo action, PDF 2.0; ISO 32000-2, 12.3.2.3, Table 202; ISO 14289-2, 8.8) in bookmarks and links when
/// pages are left out or files merged (docs/ABLAUFPLAN.md, 6.5 e; docs/FEHLERGEDAECHTNIS.md, No. 643). Before: every GoTo bookmark became a
/// plain /Dest (its structure destination lost), a link kept an /SD naming null after pruning, the branch of a merged file had only a page.
/// </summary>
public sealed class StructureDestinationTests
{
    /// <summary>
    /// A tagged file of three pages (MergeStructureTests.Tagged: pages 20, 30, 40, their paragraphs 22, 32, 42) with a bookmark „Teil 3“
    /// to page 3 and its paragraph, and on page 3 a link to page 1 and its paragraph.
    /// </summary>
    private static PdfDocument ThreePages() => PdfDocument.Open(MergeStructureTests.Tagged("Teil", 3, extra: o =>
    {
        o[1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 6 0 R/MarkInfo<</Marked true>>/Lang(de-DE)/Outlines 50 0 R>>";
        o[50] = "<</Type/Outlines/First 51 0 R/Last 51 0 R/Count 1>>";
        o[51] = "<</Title(Teil 3)/Parent 50 0 R/A<</S/GoTo/D[40 0 R/Fit]/SD[42 0 R/Fit]>>>>";
        o[40] = "<</Type/Page/Parent 2 0 R/Contents 41 0 R/Resources<</Font<</F1 5 0 R>>>>/StructParents 2/Annots[52 0 R]>>";
        o[52] = "<</Type/Annot/Subtype/Link/Rect[72 600 200 620]/Border[0 0 0]/A<</S/GoTo/D[20 0 R/Fit]/SD[22 0 R/Fit]>>>>";
    }));

    private static PdfDocument Save(PdfDocument doc, IReadOnlyList<PageItem> pages, string version)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, pages, output, new PdfSaveOptions { Version = version });
        return PdfDocument.Open(output.ToArray());
    }

    private static PdfDictionary FirstBookmark(PdfDocument doc) => doc.Catalog!.GetDictionary(PdfName.Outlines)!.GetDictionary(PdfName.First)!;

    private static string TextOf(PdfDocument doc, PdfDictionary paragraph)
    {
        var page = doc.Pages.First(p => ReferenceEquals(p.Dictionary, paragraph.Get(PdfName.Get("Pg"))));
        return Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.Build(doc, page).Text).Text.Trim();
    }

    /// <summary>
    /// The first page deleted, saved as 2.0: the bookmark stays a GoTo — /D on the page now second, /SD on the paragraph of the former third
    /// page. Before: only /Dest.
    /// </summary>
    [Fact]
    public void Pdf20_BookmarkWithStructureDestination_SurvivesPageDeletion()
    {
        using var doc = ThreePages();
        var items = PageItem.From(doc);
        using var saved = Save(doc, [items[1], items[2]], "2.0");
        var action = FirstBookmark(saved).GetDictionary(PdfName.Get("A"))!;
        Assert.Equal("GoTo", action.GetName(PdfName.Get("S"))!.Value);
        Assert.Same(saved.Pages[1].Dictionary, action.GetArray(PdfName.Get("D"))!.Get(0));
        var target = (PdfDictionary)action.GetArray(PdfName.Get("SD"))!.Get(0)!;
        Assert.Equal("Teil Seite 3", TextOf(saved, target));
        Assert.False(FirstBookmark(saved).ContainsKey(PdfName.Get("Dest")));
    }

    /// <summary>
    /// Saved as 1.7 with the pages as they are (the bookmarks copied, not built anew): no /SD anywhere — the bookmark's GoTo keeps its page,
    /// the link's action too.
    /// </summary>
    [Fact]
    public void Pdf17_NoStructureDestinations()
    {
        using var doc = ThreePages();
        using var saved = Save(doc, PageItem.From(doc), "1.7");
        var action = FirstBookmark(saved).GetDictionary(PdfName.Get("A"))!;
        Assert.False(action.ContainsKey(PdfName.Get("SD")));
        Assert.Same(saved.Pages[2].Dictionary, action.GetArray(PdfName.Get("D"))!.Get(0));
        var link = (PdfDictionary)saved.Pages[2].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!;
        Assert.False(link.GetDictionary(PdfName.Get("A"))!.ContainsKey(PdfName.Get("SD")));
    }

    /// <summary>
    /// The first page deleted, saved as 2.0: the link on the former third page loses its /SD (its paragraph is gone with the page) — and
    /// since its /D names the deleted page as well, the whole GoTo goes (docs/FEHLERGEDAECHTNIS.md, No. 692: a destination names a page,
    /// ISO 32000-2, 12.3.2.2). Before: /SD named null, and later /D kept [null /Fit].
    /// </summary>
    [Fact]
    public void Link_DestinationsToADeletedPage_ActionGoes()
    {
        using var doc = ThreePages();
        var items = PageItem.From(doc);
        using var saved = Save(doc, [items[1], items[2]], "2.0");
        var link = (PdfDictionary)saved.Pages[1].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!;
        Assert.Equal("Link", link.GetName(PdfName.Subtype)!.Value);
        Assert.Null(link.GetDictionary(PdfName.Get("A")));
        Assert.Null(link.Get(PdfName.Get("Dest")) as PdfArray);
    }

    /// <summary>
    /// A file with bookmarks merged into a tagged one, saved as 2.0: its branch goes to its first page and, as the structure destination, to
    /// the part its tree hangs in.
    /// </summary>
    [Fact]
    public void Merge_Pdf20_BranchHasStructureDestination()
    {
        using var a = PdfDocument.Open(MergeStructureTests.Tagged("Erste Datei", 1));
        using var b = ThreePages();
        var items = PageItem.From(b);
        using var saved = Save(a, [.. PageItem.From(a), items[0], items[1], items[2]], "2.0");
        var bookmarks = saved.Catalog!.GetDictionary(PdfName.Outlines)!;
        var branch = bookmarks.GetDictionary(PdfName.Get("Last"))!;
        var action = branch.GetDictionary(PdfName.Get("A"))!;
        Assert.Same(saved.Pages[1].Dictionary, action.GetArray(PdfName.Get("D"))!.Get(0));
        var part = (PdfDictionary)action.GetArray(PdfName.Get("SD"))!.Get(0)!;
        Assert.Equal("Part", part.GetName(PdfName.Get("S"))!.Value);
        // the merged file's own bookmark keeps its structure destination too
        var own = branch.GetDictionary(PdfName.First)!.GetDictionary(PdfName.Get("A"))!;
        Assert.Equal("Teil Seite 3", TextOf(saved, (PdfDictionary)own.GetArray(PdfName.Get("SD"))!.Get(0)!));
    }
}
