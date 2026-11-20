using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The check helpers for tagged content (<see cref="TaggedContent"/>; docs/ABLAUFPLAN.md, 6.2 step 8; docs/FEHLERGEDAECHTNIS.md, No. 629): the
/// parent tree and the references of the tree, the content of the pages against it, the containment rules.
/// </summary>
public sealed class TaggedContentTests
{
    /// <summary>
    /// Two pages (3, 11), a paragraph on each (8, 9) under the Document (7); the parent tree pairs 0 and 1, /ParentTreeNextKey 2, the /IDTree
    /// naming both. <paramref name="change"/> breaks one thing.
    /// </summary>
    internal static PdfDocument Tagged(Action<Dictionary<int, object>>? change = null)
    {
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/StructTreeRoot 6 0 R/MarkInfo<</Marked true>>/Lang(de)>>",
            [2] = "<</Type/Pages/Kids[3 0 R 11 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>/StructParents 0>>",
            [4] = ("", "/P <</MCID 0>> BDC 0 0 m 100 100 l S EMC"),
            [6] = "<</Type/StructTreeRoot/K 7 0 R/ParentTree 15 0 R/ParentTreeNextKey 2/IDTree 16 0 R>>",
            [7] = "<</Type/StructElem/S/Document/P 6 0 R/K[8 0 R 9 0 R]>>",
            [8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K 0/ID(P1)>>",
            [9] = "<</Type/StructElem/S/P/P 7 0 R/Pg 11 0 R/K 0/ID(P2)>>",
            [11] = "<</Type/Page/Parent 2 0 R/Contents 12 0 R/Resources<<>>/StructParents 1>>",
            [12] = ("", "/P <</MCID 0>> BDC 0 0 m 100 100 l S EMC"),
            [15] = "<</Nums[0[8 0 R] 1[9 0 R]]>>",
            [16] = "<</Names[(P1) 8 0 R (P2) 9 0 R]>>",
        };
        change?.Invoke(objects);
        return PdfDocument.Open(CraftedFileTests.Build(objects));
    }

    /// <summary>The file as made: nothing found by any of the three.</summary>
    [Fact]
    public void Conforming_NothingFound()
    {
        using var doc = Tagged();
        Assert.Empty(TaggedContent.ParentTree(doc));
        Assert.Empty(TaggedContent.Nesting(doc));
        var tagging = TaggedContent.Analyse(doc);
        Assert.All(tagging.Pages, p => Assert.Equal(0, p.Untagged + p.McidsWithoutElement + p.DuplicateMcids + p.ArtifactInTagged + p.TaggedInArtifact));
        Assert.Equal(0, tagging.FormsWithMcidsDrawnTwice);
    }

    public static TheoryData<string, string> TreeCases => new()
    {
        { "Schlüssel doppelt", nameof(StructureIssueKind.DuplicateKey) },
        { "Schlüssel über NextKey", nameof(StructureIssueKind.KeyNotBelowNextKey) },
        { "Paar null", nameof(StructureIssueKind.PairWithoutTarget) },
        { "Feld nur null", nameof(StructureIssueKind.PairWithoutTarget) },
        { "Pg keine Seite", nameof(StructureIssueKind.PageNotInDocument) },
        { "MCR mit Stm null", nameof(StructureIssueKind.StreamMissing) },
        { "OBJR ohne Obj", nameof(StructureIssueKind.ObjectMissing) },
        { "Element ohne P", nameof(StructureIssueKind.ParentMissing) },
        { "ID ohne Element", nameof(StructureIssueKind.IdWithoutElement) },
    };

    /// <summary>Each kind the tree check knows, from a file breaking it alone (new code: the check did not exist).</summary>
    [Theory]
    [MemberData(nameof(TreeCases))]
    public void ParentTree_EachKindFound(string name, string kind)
    {
        using var doc = Tagged(o =>
        {
            switch (name)
            {
                case "Schlüssel doppelt": o[15] = "<</Nums[0[8 0 R] 1[9 0 R] 1[9 0 R]]>>"; break;
                case "Schlüssel über NextKey": o[6] = ((string)o[6]).Replace("/ParentTreeNextKey 2", "/ParentTreeNextKey 1", StringComparison.Ordinal); break;
                case "Paar null": o[15] = "<</Nums[0[8 0 R] 1[9 0 R] 2 null]>>"; o[6] = ((string)o[6]).Replace("NextKey 2", "NextKey 3", StringComparison.Ordinal); break;
                case "Feld nur null": o[15] = "<</Nums[0[8 0 R] 1[null null]]>>"; break;
                case "Pg keine Seite": o[9] = ((string)o[9]).Replace("/Pg 11 0 R", "/Pg 4 0 R", StringComparison.Ordinal); break;
                case "MCR mit Stm null": o[9] = "<</Type/StructElem/S/P/P 7 0 R/Pg 11 0 R/K<</Type/MCR/MCID 0/Stm 30 0 R>>/ID(P2)>>"; break;
                case "OBJR ohne Obj": o[9] = "<</Type/StructElem/S/Link/P 7 0 R/Pg 11 0 R/K[0 <</Type/OBJR/Obj 30 0 R>>]/ID(P2)>>"; break;
                case "Element ohne P": o[9] = ((string)o[9]).Replace("/P 7 0 R", "", StringComparison.Ordinal); break;
                case "ID ohne Element": o[16] = "<</Names[(P1) 8 0 R (P2) null]>>"; break;
                default: throw new ArgumentOutOfRangeException(nameof(name), name);
            }
        });
        Assert.Contains(TaggedContent.ParentTree(doc), i => i.Kind.ToString() == kind);
    }

    /// <summary>The content against the tree: what counts as untagged, MCIDs the tree does not name, MCIDs twice, artifacts in tagged content and back.</summary>
    [Fact]
    public void Analyse_CountsWhatIsWrong()
    {
        using var doc = Tagged(o =>
        {
            o[4] = ("", "0 0 m 1 1 l S /P <</MCID 0>> BDC 0 0 m 1 1 l S /Artifact BMC 0 0 m 1 1 l S EMC EMC /P <</MCID 0>> BDC EMC");
            o[12] = ("", "/Artifact BMC /P <</MCID 0>> BDC EMC EMC /P <</MCID 4>> BDC 0 0 m 1 1 l S EMC BI /W 1 /H 1 /CS /G /BPC 8 ID \u0080 EI");
        });
        var pages = TaggedContent.Analyse(doc).Pages;
        Assert.Equal(1, pages[0].Untagged);
        Assert.Equal(1, pages[0].ArtifactInTagged);
        Assert.Equal(1, pages[0].DuplicateMcids);
        Assert.Equal(1, pages[1].TaggedInArtifact);
        Assert.Equal(1, pages[1].McidsWithoutElement);
        Assert.Equal(2, pages[1].Untagged);   // the line in MCID 4 (no element) and the inline image
    }

    /// <summary>The containment rules through the shared building block: a paragraph in a paragraph.</summary>
    [Fact]
    public void Nesting_ParagraphInParagraph()
    {
        using var doc = Tagged(o =>
        {
            o[7] = "<</Type/StructElem/S/Document/P 6 0 R/K 8 0 R>>";
            o[8] = "<</Type/StructElem/S/P/P 7 0 R/Pg 3 0 R/K[0 9 0 R]/ID(P1)>>";
            o[9] = ((string)o[9]).Replace("/P 7 0 R", "/P 8 0 R", StringComparison.Ordinal);
        });
        Assert.Contains("P > P", TaggedContent.Nesting(doc));
    }
}
