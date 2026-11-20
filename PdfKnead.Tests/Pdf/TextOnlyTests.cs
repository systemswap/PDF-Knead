using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Tests.Pdf;

/// <summary>The text-only pass (used by search) yields the same characters as the full display list.</summary>
public sealed class TextOnlyTests
{
    private static string PdfJs(string name, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return Path.Combine(dir.FullName, "TestPDFs", "pdfjs", name);
        throw new DirectoryNotFoundException();
    }

    [TestPdfsTheory]
    [InlineData("ContentStreamCycleType3insideType3.pdf")]   // Type 3 procedures with their own text
    [InlineData("ContentStreamNoCycleType3insideType3.pdf")]
    [InlineData("TAMReview.pdf")]
    [InlineData("alphatrans.pdf")]
    [InlineData("annotation-text-widget.pdf")]
    [InlineData("issue1002.pdf")]
    public void BuildText_MatchesTheDisplayList(string name)
    {
        using var doc = PdfDocument.Open(PdfJs(name));
        foreach (var page in doc.Pages.Take(5))
            Assert.Equal(PageInterpreter.Build(doc, page, withoutOwnElements: true).Text, PageInterpreter.BuildText(doc, page, withoutOwnElements: true));
    }
}
