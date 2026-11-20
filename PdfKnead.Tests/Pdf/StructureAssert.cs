using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The three check helpers of the core over a saved file (docs/ABLAUFPLAN.md, 6.2 step 8: every structure test of phase 6 runs all three and
/// expects nothing): the parent tree and the references of the tree, the containment rules, the content of the pages against the tree.
/// </summary>
internal static class StructureAssert
{
    /// <param name="doc">The saved file.</param>
    /// <param name="content">Whether all content of the pages must be tagged or an artifact (false for files whose pages are partly untagged on purpose).</param>
    public static void Clean(PdfDocument doc, bool content = true)
    {
        Assert.Empty(TaggedContent.ParentTree(doc));
        Assert.Empty(TaggedContent.Nesting(doc));
        if (!content) return;
        foreach (var page in TaggedContent.Analyse(doc).Pages)
        {
            Assert.True(page.Untagged == 0, $"Seite {page.Page + 1}: {page.Untagged} ungetaggt");
            Assert.True(page.McidsWithoutElement == 0, $"Seite {page.Page + 1}: {page.McidsWithoutElement} MCIDs ohne Element");
            Assert.True(page.DuplicateMcids + page.ArtifactInTagged + page.TaggedInArtifact == 0, $"Seite {page.Page + 1}: Marken verschachtelt oder doppelt");
        }
    }
}
