using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The check „barrierefrei?“, PDF/UA-2 8.8 (docs/FEHLERGEDAECHTNIS.md, No. 668): destinations within the document are structure destinations —
/// /Dest of outline items and links, named destinations, /OpenAction begin with a structure element; GoTo actions carry /SD. Before, nothing
/// of it was checked.
/// </summary>
public sealed class AccessibilityDestinationTests
{
    /// <summary>A tagged 2.0 file: object 5 is the page, 9 a structure element (P); <paramref name="extra"/> adds to the catalog.</summary>
    private static PdfDocument File(string catalogExtra, string pageExtra = "", params (int Number, string Body)[] more)
    {
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Lang(de)/MarkInfo<</Marked true>>/StructTreeRoot 7 0 R" + catalogExtra + ">>",
            [2] = "<</Type/Pages/Kids[5 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [5] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>/StructParents 0" + pageExtra + ">>",
            [6] = ("", "/P <</MCID 0>> BDC EMC"),
            [7] = "<</Type/StructTreeRoot/K 8 0 R/ParentTree<</Nums[0[9 0 R]]>>>>",
            [8] = "<</Type/StructElem/S/Document/P 7 0 R/K 9 0 R>>",
            [9] = "<</Type/StructElem/S/P/P 8 0 R/Pg 5 0 R/K 0>>",
        };
        foreach (var (number, body) in more) objects[number] = body;
        var bytes = CraftedFileTests.Build(objects);
        return PdfDocument.Open([.. "%PDF-2.0"u8.ToArray(), .. bytes.AsSpan(8)]);
    }

    private static List<string> Ids(PdfDocument doc) => [.. AccessibilityCheck.Check(doc, AccessibilityProfile.PdfUa2).Findings.Select(f => f.Id)];

    public static TheoryData<string, string, string, string?> Cases => new()
    {
        // name, catalog addition, page addition, expected (null: no finding of 8.8)
        { "Lesezeichen auf Seite", "/Outlines 10 0 R", "", "8.8-1" },
        { "Lesezeichen auf Element", "/Outlines 11 0 R", "", null },
        { "OpenAction auf Seite", "/OpenAction[5 0 R/Fit]", "", "8.8-1" },
        { "benanntes Ziel auf Seite", "/Outlines 13 0 R/Names<</Dests<</Names[(ziel)[5 0 R/Fit]]>>>>", "", "8.8-1" },
        { "Link mit GoTo ohne SD", "", "/Annots[14 0 R]", "8.8-2" },
        { "Link mit GoTo und SD", "", "/Annots[15 0 R]", null },
        { "Kette über Next", "/OpenAction<</S/URI/URI(https://example.org)/Next<</S/GoTo/D[5 0 R/Fit]>>>>", "", "8.8-2" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Destinations(string name, string catalog, string page, string? expected)
    {
        _ = name;
        using var doc = File(catalog, page,
            (10, "<</First 12 0 R/Last 12 0 R/Count 1>>"),
            (11, "<</First 16 0 R/Last 16 0 R/Count 1>>"),
            (12, "<</Title(Seite)/Parent 10 0 R/Dest[5 0 R/Fit]>>"),
            (13, "<</First 17 0 R/Last 17 0 R/Count 1>>"),
            (14, "<</Type/Annot/Subtype/Link/Rect[0 0 10 10]/A<</S/GoTo/D[5 0 R/Fit]>>>>"),
            (15, "<</Type/Annot/Subtype/Link/Rect[0 0 10 10]/A<</S/GoTo/D[5 0 R/Fit]/SD[9 0 R/Fit]>>>>"),
            (16, "<</Title(Element)/Parent 11 0 R/Dest[9 0 R/Fit]>>"),
            (17, "<</Title(Benannt)/Parent 13 0 R/Dest(ziel)>>"));
        var ids = Ids(doc);
        if (expected is null) Assert.DoesNotContain(ids, id => id.StartsWith("8.8-", StringComparison.Ordinal));
        else Assert.Contains(expected, ids);
    }

    /// <summary>PDF/UA-1 knows no structure destinations: nothing of 8.8.</summary>
    [Fact]
    public void Ua1_NotChecked()
    {
        using var doc = File("/Outlines 10 0 R", "", (10, "<</First 12 0 R/Last 12 0 R/Count 1>>"), (12, "<</Title(Seite)/Parent 10 0 R/Dest[5 0 R/Fit]>>"));
        Assert.DoesNotContain(AccessibilityCheck.Check(doc, AccessibilityProfile.PdfUa1).Findings, f => f.Id.StartsWith("8.8-", StringComparison.Ordinal));
    }
}
