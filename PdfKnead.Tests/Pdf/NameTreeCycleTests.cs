using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Name trees whose /Kids name the node itself: every walk visits a node once and ends (ISO 32000-2, 7.9.6;
/// docs/FEHLERGEDAECHTNIS.md, No. 1264).
/// </summary>
public sealed class NameTreeCycleTests
{
    private static byte[] Document(string tree) => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/Names<</EmbeddedFiles 7 0 R>>>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [4] = "<</Type/Page/Parent 2 0 R/Resources<<>>>>",
        [7] = tree,
        [8] = "<</Type/Filespec/F(a.txt)>>",
    });

    /// <summary>
    /// The tree of embedded files names itself eight times as its kid: the accessibility check ends and reports what it reports for the
    /// same tree without the kids — the one file once.
    /// </summary>
    [Fact]
    public void AccessibilityCheck_EmbeddedFilesTreeThatNamesItself_EndsWithTheSameFindings()
    {
        using var plain = PdfDocument.Open(Document("<</Names[(a.txt) 8 0 R]>>"));
        var expected = AccessibilityCheck.Check(plain, AccessibilityProfile.PdfUa1).Findings.Select(f => f.Id).Order().ToList();

        var kids = string.Join(" ", Enumerable.Repeat("7 0 R", 8));
        using var crafted = PdfDocument.Open(Document($"<</Names[(a.txt) 8 0 R]/Kids[{kids}]>>"));
        var check = Task.Run(() => AccessibilityCheck.Check(crafted, AccessibilityProfile.PdfUa1));
        Assert.True(check.Wait(TimeSpan.FromSeconds(30)), "The accessibility check did not end within 30 s.");
        Assert.Equal(expected, check.Result.Findings.Select(f => f.Id).Order().ToList());
    }

    /// <summary>
    /// The one walker: a node naming itself as its kid is visited once, a kid beside it is still read, and a pair whose key is neither a
    /// string nor a name is given with its value (a check of the values must not pass it over).
    /// </summary>
    [Fact]
    public void Entries_NodeNamingItself_EachNodeOnce_AllPairs()
    {
        var kid = new Core.Pdf.Objects.PdfDictionary();
        kid.Set(Core.Pdf.Objects.PdfName.Get("Names"), new Core.Pdf.Objects.PdfArray([new Core.Pdf.Objects.PdfString("b"u8.ToArray()), Core.Pdf.Objects.PdfInteger.Of(2)]));
        var root = new Core.Pdf.Objects.PdfDictionary();
        root.Set(Core.Pdf.Objects.PdfName.Get("Names"), new Core.Pdf.Objects.PdfArray([Core.Pdf.Objects.PdfInteger.Of(7), Core.Pdf.Objects.PdfInteger.Of(1)]));
        var kids = new Core.Pdf.Objects.PdfArray([kid]);
        for (var i = 0; i < 8; i++) kids.Add(root);
        root.Set(Core.Pdf.Objects.PdfName.Kids, kids);

        var entries = NameTree.Entries(root).ToList();
        Assert.Equal([1L, 2L], entries.Select(e => ((Core.Pdf.Objects.PdfInteger)e.Value!).Value));
        Assert.IsType<Core.Pdf.Objects.PdfInteger>(entries[0].Key);
        // Pairs keeps to keys that are strings or names, as before
        Assert.Equal(["b"], NameTree.Pairs(root).Select(p => System.Text.Encoding.Latin1.GetString(p.Key)));
    }
}
