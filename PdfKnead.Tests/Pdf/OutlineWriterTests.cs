using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Bookmarks when saving (docs/FEHLERGEDAECHTNIS.md, No. 679; found by the round trip of 6.8): an entry without a target — a heading of the
/// outline, allowed by ISO 32000-2, 12.3.3 — stays; one whose page is not taken over goes. Before, every save that builds the bookmarks anew
/// (pages taken out or moved, files merged) dropped the entries without a target; a save without changes takes them over as they are.
/// </summary>
public sealed class OutlineWriterTests
{
    /// <summary>Two pages; bookmarks „Überschrift“ (no target), „Seite 1“ (page 1), „Seite 2“ (page 2).</summary>
    private static byte[] File() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/Outlines 10 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R 5 0 R]/Count 2/MediaBox[0 0 595 842]>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>>>",
        [5] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>>>",
        [6] = ("", "0 0 m 100 100 l S"),
        [10] = "<</Type/Outlines/First 11 0 R/Last 13 0 R/Count 3>>",
        [11] = "<</Title(Überschrift)/Parent 10 0 R/Next 12 0 R>>",
        [12] = "<</Title(Seite 1)/Parent 10 0 R/Prev 11 0 R/Next 13 0 R/Dest[4 0 R/Fit]>>",
        [13] = "<</Title(Seite 2)/Parent 10 0 R/Prev 12 0 R/Dest[5 0 R/Fit]>>",
    });

    private static List<string> Titles(byte[] data)
    {
        using var doc = PdfDocument.Open(data);
        return [.. PdfOutline.Read(doc).Select(o => o.Title)];
    }

    [Fact]
    public void Saved_EntryWithoutTargetStays()
    {
        using var doc = PdfDocument.Open(File());
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output);
        Assert.Equal(["Überschrift", "Seite 1", "Seite 2"], Titles(output.ToArray()));
    }

    [Fact]
    public void PageTakenOut_ItsEntryGoes_TheOneWithoutTargetStays()
    {
        using var doc = PdfDocument.Open(File());
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [PageItem.From(doc)[0]], output);
        Assert.Equal(["Überschrift", "Seite 1"], Titles(output.ToArray()));
    }

    /// <summary>
    /// A structure tree without /MarkInfo /Marked (not tagged in the sense of ISO 14289-1, 7.1): the copy carries the tree along as it is, so a
    /// bookmark keeps its structure element (/SE, ISO 32000-2, Table 151), saved as it is or after taking out the other page — it went although
    /// the element was written. A key of another program stays as well where the bookmarks are built anew (page taken out): it went with it
    /// (docs/FEHLERGEDAECHTNIS.md, No. 880; found by the data-keeping round trip).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UntaggedTreeCarriedAlong_BookmarkKeepsItsStructureElement(bool pageTakenOut)
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Outlines 10 0 R/StructTreeRoot 20 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R 5 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>>>",
            [5] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>>>",
            [6] = ("", "0 0 m 100 100 l S"),
            [10] = "<</Type/Outlines/First 11 0 R/Last 11 0 R/Count 1>>",
            [11] = "<</Title(Artikel)/Parent 10 0 R/Dest[4 0 R/Fit]/SE 21 0 R/XYZ_Hinweis(privat)>>",
            [20] = "<</Type/StructTreeRoot/K 21 0 R>>",
            [21] = "<</Type/StructElem/S/Art/P 20 0 R/Pg 4 0 R>>",
        });
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, pageTakenOut ? [PageItem.From(doc)[0]] : PageItem.From(doc), output);
        using var saved = PdfDocument.Open(output.ToArray());
        var bookmark = saved.Catalog!.GetDictionary(Core.Pdf.Objects.PdfName.Get("Outlines"))!.GetDictionary(Core.Pdf.Objects.PdfName.Get("First"))!;
        var element = bookmark.GetDictionary(Core.Pdf.Objects.PdfName.Get("SE"));
        Assert.NotNull(element);
        Assert.Equal("Art", element.GetName(Core.Pdf.Objects.PdfName.Get("S"))?.Value);
        Assert.Equal("privat", (bookmark.Get(Core.Pdf.Objects.PdfName.Get("XYZ_Hinweis")) as Core.Pdf.Objects.PdfString)?.ToText());
    }

    /// <summary>
    /// A PDF/UA-1 file without /Lang in the catalogue, a file with bookmarks inserted (docs/FEHLERGEDAECHTNIS.md, No. 680; found by the round
    /// trip of 6.8): PDF/UA-1 tells the language of bookmarks by the catalogue alone, so the copy has bookmarks without a language — the save
    /// names the break and the identification goes. Before it stayed (7.2-2).
    /// </summary>
    [Fact]
    public void MergedBookmarks_MainWithoutLanguage_ClaimGoesWithReason()
    {
        const string xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                           "<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\"><pdfuaid:part>1</pdfuaid:part></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        using var main = PdfDocument.Open(CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Metadata 7 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>>>",
            [6] = ("", "/Artifact BMC 0 0 m 100 100 l S EMC"),
            [7] = ("/Type/Metadata/Subtype/XML", xmp),
        }));
        using var inserted = PdfDocument.Open(File());
        using var output = new MemoryStream();
        var result = PdfWriter.Write(main, [.. PageItem.From(main), .. PageItem.From(inserted)], output);
        Assert.Contains("Lesezeichen oder Felder einer eingefügten Datei ohne Sprache (die Datei nennt keine)", result.AccessibilityBreaks);
        Assert.True(result.AccessibilityClaimRemoved);
        // the inserted file's bookmarks under the branch of the inserted pages, the one without a target among them
        using var merged = PdfDocument.Open(output.ToArray());
        Assert.Equal(["Überschrift", "Seite 1", "Seite 2"], PdfOutline.Read(merged).Single().Children.Select(c => c.Title));
    }
}
