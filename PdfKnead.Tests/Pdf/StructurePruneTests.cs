using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The structure tree when pages are not written (deleted, extracted, split; docs/ABLAUFPLAN.md, 6.2 step 5, D-B14, L4 L6;
/// docs/FEHLERGEDAECHTNIS.md, No. 626): the pair of the parent tree of a page that is not written goes as a whole (ISO 32000-2, 14.7.5.4: one
/// key per content stream), the /IDTree loses the entries of removed elements (14.7.2), the /Limits of the nodes are drawn in to the keys
/// left (7.9.6, 7.9.7), and with no tagged content left the file no longer claims to be tagged (no root, no /MarkInfo, no /StructParents;
/// a PDF/UA identification goes with its reason). Before: the pair stayed with null entries, the /IDTree named null, the empty root and
/// /Marked true stayed.
/// </summary>
public sealed class StructurePruneTests
{
    /// <summary>
    /// Three pages (objects 3, 11, 13), each with a paragraph (MCID 0; elements 8, 9, 10 with /ID P1, P2, P3) under a Document (7); the parent
    /// tree pairs 0, 1, 2 with the arrays of the pages; /IDTree with the three IDs. <paramref name="taggedPages"/>: which pages carry their
    /// paragraph (the others have no /StructParents and no element). <paramref name="treeInKids"/>: both trees as a root with one kid that has
    /// /Limits (as larger files write them), the arrays of the parent tree as objects of their own (20–22, as 7.9.6 sentence 5 recommends — a
    /// tree with direct arrays is built anew on writing, docs/FEHLERGEDAECHTNIS.md, No. 779); <paramref name="xmp"/>: an XMP packet for the
    /// catalogue.
    /// </summary>
    private static byte[] ThreePages(bool[] taggedPages, bool treeInKids = false, string? xmp = null)
    {
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 6 0 R/MarkInfo<</Marked true>>/Lang(de-DE)>>",
            [2] = "<</Type/Pages/Kids[3 0 R 11 0 R 13 0 R]/Count 3/MediaBox[0 0 595 842]>>",
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
        };
        int[] pages = [3, 11, 13], contents = [4, 12, 14], elements = [8, 9, 10];
        var kids = new List<string>();
        var nums = new List<string>();
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var tagged = taggedPages[i];
            objects[pages[i]] = $"<</Type/Page/Parent 2 0 R/Contents {contents[i]} 0 R/Resources<</Font<</F1 5 0 R>>>>{(tagged ? $"/StructParents {i}" : "")}>>";
            objects[contents[i]] = ("", tagged ? $"/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Seite {i + 1}) Tj ET EMC" : $"BT /F1 12 Tf 72 700 Td (Seite {i + 1}) Tj ET");
            if (!tagged) continue;
            objects[elements[i]] = $"<</Type/StructElem/S/P/P 7 0 R/Pg {pages[i]} 0 R/K 0/ID(P{i + 1})>>";
            kids.Add($"{elements[i]} 0 R");
            if (treeInKids)
            {
                objects[20 + i] = $"[{elements[i]} 0 R]";
                nums.Add($"{i} {20 + i} 0 R");
            }
            else nums.Add($"{i}[{elements[i]} 0 R]");
            ids.Add($"(P{i + 1}) {elements[i]} 0 R");
        }
        objects[6] = $"<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 3/IDTree 16 0 R>>";
        objects[7] = $"<</Type/StructElem/S/Document/P 6 0 R/K[{string.Join(" ", kids)}]>>";
        var first = Array.IndexOf(taggedPages, true);
        var last = Array.LastIndexOf(taggedPages, true);
        if (treeInKids)
        {
            objects[15] = "<</Kids[17 0 R]>>";
            objects[17] = $"<</Limits[{first} {last}]/Nums[{string.Join(" ", nums)}]>>";
            objects[16] = "<</Kids[18 0 R]>>";
            objects[18] = $"<</Limits[(P{first + 1}) (P{last + 1})]/Names[{string.Join(" ", ids)}]>>";
        }
        else
        {
            objects[15] = $"<</Nums[{string.Join(" ", nums)}]>>";
            objects[16] = $"<</Names[{string.Join(" ", ids)}]>>";
        }
        if (xmp is not null)
        {
            objects[19] = ("/Type/Metadata/Subtype/XML", xmp);
            objects[1] = ((string)objects[1]).Replace("/Lang(de-DE)", "/Lang(de-DE)/Metadata 19 0 R", StringComparison.Ordinal);
        }
        return CraftedFileTests.Build(objects);
    }

    /// <summary>Writes the pages kept (by index) and opens the result.</summary>
    private static PdfDocument Keep(byte[] file, params int[] kept) => Save(file, kept).Saved;

    /// <summary>Writes the pages kept (by index): the result of saving and the file opened.</summary>
    private static (PdfSaveResult Result, PdfDocument Saved) Save(byte[] file, params int[] kept)
    {
        using var doc = PdfDocument.Open(file);
        var items = PageItem.From(doc);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, [.. kept.Select(i => items[i])], output, new PdfSaveOptions());
        return (result, PdfDocument.Open(output.ToArray()));
    }

    private static PdfDictionary Root(PdfDocument doc) => doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!;

    /// <summary>The pairs of a flat number tree as (key, value).</summary>
    private static List<(long Key, PdfObject? Value)> Pairs(PdfDictionary tree)
    {
        var nums = tree.GetArray(PdfName.Get("Nums"))!;
        return [.. Enumerable.Range(0, nums.Count / 2).Select(i => (((PdfInteger)nums.Get(2 * i)!).Value, nums.Get(2 * i + 1)))];
    }

    /// <summary>The middle page deleted: its pair of the parent tree goes as a whole; the others stay with their elements, none null.</summary>
    [Fact]
    public void DeletedPage_ItsParentTreePairGoes()
    {
        using var saved = Keep(ThreePages([true, true, true]), 0, 2);
        var pairs = Pairs(Root(saved).GetDictionary(PdfName.Get("ParentTree"))!);
        Assert.Equal([0L, 2L], pairs.Select(p => p.Key));
        Assert.All(pairs, p => Assert.All(Enumerable.Range(0, ((PdfArray)p.Value!).Count), i => Assert.IsType<PdfDictionary>(((PdfArray)p.Value!).Get(i))));
        Assert.True(Root(saved).GetInteger(PdfName.Get("ParentTreeNextKey")) > 2);
        StructureAssert.Clean(saved);
    }

    /// <summary>The middle page deleted: the /IDTree no longer names its paragraph (before: P2 → null).</summary>
    [Fact]
    public void DeletedPage_ItsIdTreeEntryGoes()
    {
        using var saved = Keep(ThreePages([true, true, true]), 0, 2);
        var names = Root(saved).GetDictionary(PdfName.Get("IDTree"))!.GetArray(PdfName.Get("Names"))!;
        var entries = Enumerable.Range(0, names.Count / 2).Select(i => (((PdfString)names.Get(2 * i)!).ToText(), names.Get(2 * i + 1))).ToList();
        Assert.Equal(["P1", "P3"], entries.Select(e => e.Item1));
        Assert.All(entries, e => Assert.IsType<PdfDictionary>(e.Item2));
        StructureAssert.Clean(saved);
    }

    /// <summary>
    /// Only the deleted page carried tagged content: nothing tagged is left — the file has no structure tree and no /MarkInfo any more (before:
    /// a root without kids and /Marked true, a tagged file without tagged content).
    /// </summary>
    [Fact]
    public void AllTaggedContentDeleted_NoTreeNoMarkInfo()
    {
        using var saved = Keep(ThreePages([false, true, false]), 0, 2);
        Assert.False(saved.Catalog!.ContainsKey(PdfName.Get("StructTreeRoot")));
        Assert.False(saved.Catalog.ContainsKey(PdfName.Get("MarkInfo")));
        Assert.All(saved.Pages, p => Assert.False(p.Dictionary.ContainsKey(PdfName.Get("StructParents"))));
        // and with tagged content left, both stay
        using var kept = Keep(ThreePages([true, true, false]), 0, 2);
        Assert.NotNull(kept.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot")));
        Assert.Equal(true, kept.Catalog.GetDictionary(PdfName.Get("MarkInfo"))!.GetBoolean(PdfName.Get("Marked")));
        StructureAssert.Clean(kept, content: false);   // the third page is untagged on purpose
    }

    /// <summary>
    /// Both trees in kids with /Limits, the last page deleted: the kids lose its pair and its ID, and their /Limits are drawn in to the keys
    /// left (before: the parent tree kept [0 2] for keys 0 and 1, the /IDTree was not pruned at all).
    /// </summary>
    [Fact]
    public void TreesInKids_LimitsDrawnIn()
    {
        using var saved = Keep(ThreePages([true, true, true], treeInKids: true), 0, 1);
        var numsKid = Root(saved).GetDictionary(PdfName.Get("ParentTree"))!.GetArray(PdfName.Get("Kids"))!.Get(0) as PdfDictionary;
        Assert.Equal([0L, 1L], Pairs(numsKid!).Select(p => p.Key));
        var limits = numsKid!.GetArray(PdfName.Get("Limits"))!;
        Assert.Equal([0L, 1L], new[] { ((PdfInteger)limits.Get(0)!).Value, ((PdfInteger)limits.Get(1)!).Value });
        var namesKid = Root(saved).GetDictionary(PdfName.Get("IDTree"))!.GetArray(PdfName.Get("Kids"))!.Get(0) as PdfDictionary;
        var nameLimits = namesKid!.GetArray(PdfName.Get("Limits"))!;
        Assert.Equal(["P1", "P2"], new[] { ((PdfString)nameLimits.Get(0)!).ToText(), ((PdfString)nameLimits.Get(1)!).ToText() });
        Assert.Equal(4, namesKid.GetArray(PdfName.Get("Names"))!.Count);
        StructureAssert.Clean(saved);
    }

    /// <summary>
    /// A PDF/UA file whose only tagged page is deleted: no longer tagged, so its identification goes and the result says why (before: the
    /// identification stayed on a file that does not claim to be tagged any more — or, before No. 626, on a root without content).
    /// </summary>
    [Fact]
    public void PdfUaFile_AllTaggedContentDeleted_ClaimGoesWithReason()
    {
        const string xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                           "<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\"><pdfuaid:part>1</pdfuaid:part></rdf:Description>" +
                           "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        var (result, saved) = Save(ThreePages([false, true, false], xmp: xmp), 0, 2);
        using (saved)
        {
            Assert.Contains("kein getaggter Inhalt mehr", result.AccessibilityBreaks);
            Assert.True(result.AccessibilityClaimRemoved);
            var packet = System.Text.Encoding.UTF8.GetString(saved.GetStreamData((PdfStream)saved.Catalog!.Get(PdfName.Get("Metadata"))!));
            Assert.DoesNotContain("pdfuaid:part", packet);
        }
        // tagged content left: nothing breaks, the identification stays
        var (kept, keptFile) = Save(ThreePages([true, true, false], xmp: xmp), 0, 2);
        using (keptFile) Assert.Empty(kept.AccessibilityBreaks);
    }
}
