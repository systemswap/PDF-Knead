using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Saving over an existing file (docs/VOR-DEM-VERKAUF.md, §7a; docs/FEHLERGEDAECHTNIS.md, No. 750): the original is replaced only by a file
/// that was written completely and opens as it should — an error while writing or a faulty result leaves it untouched, and no temporary file
/// stays behind.
/// </summary>
public sealed class SafeSavingTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("pdfknead-safe-");

    public void Dispose() => _folder.Delete(recursive: true);

    /// <summary>A small valid PDF with one page.</summary>
    private static byte[] Original()
    {
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (var body in new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                     "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>" })
        {
            offsets.Add(text.Length);
            text.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        var xref = text.Length;
        text.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (var o in offsets) text.Append($"{o:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private string Target()
    {
        var path = Path.Combine(_folder.FullName, "Original.pdf");
        File.WriteAllBytes(path, Original());
        return path;
    }

    private void AssertOnlyTheOriginal(string path)
    {
        Assert.Equal(Original(), File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(_folder.FullName));
    }

    /// <summary>What was written is no readable PDF: the original stays as it was. Before, it was replaced by the broken file.</summary>
    [Fact]
    public void FaultyResult_DoesNotReplaceTheOriginal()
    {
        var path = Target();
        var e = Assert.Throws<PdfException>(() => PdfFileSaver.Save(path, output =>
        {
            output.Write("%PDF-1.7\nnothing usable\n"u8);
            return new PdfSaveResult("1.7", output.Position, 0, false);
        }));
        Assert.Contains("Original", e.Message, StringComparison.Ordinal);
        AssertOnlyTheOriginal(path);
    }

    /// <summary>A file whose cross-references point to the wrong places (it would only open repaired) does not replace the original either.</summary>
    [Fact]
    public void ResultNeedingRepair_DoesNotReplaceTheOriginal()
    {
        var path = Target();
        var broken = Original();
        var text = Encoding.ASCII.GetString(broken);
        var at = text.IndexOf("0000000009 00000 n", StringComparison.Ordinal);
        Encoding.ASCII.GetBytes("0000000002").CopyTo(broken, at);   // object 1 listed at a wrong place
        Assert.Throws<PdfException>(() => PdfFileSaver.Save(path, output =>
        {
            output.Write(broken);
            return new PdfSaveResult("1.7", output.Position, 0, false);
        }));
        AssertOnlyTheOriginal(path);
    }

    /// <summary>The usual save of pages: written, checked, in place of the original; nothing else left in the folder.</summary>
    [Fact]
    public void Pages_SavedOverTheOriginal()
    {
        var path = Target();
        using (var doc = PdfDocument.Open(Original()))
            PdfFileSaver.Save(doc, PageItem.From(doc), path);
        using var saved = PdfDocument.Open(File.ReadAllBytes(path));
        Assert.Equal(1, saved.PageCount);
        Assert.Equal([path], Directory.GetFiles(_folder.FullName));
    }

    /// <summary>Other files (images, word lists, reports, certificates): an error while writing leaves the existing file and no temporary one.</summary>
    [Fact]
    public void OtherFile_ErrorWhileWriting_LeavesTheOriginal()
    {
        var path = Target();
        Assert.Throws<IOException>(() => PdfFileSaver.SaveFile(path, output =>
        {
            output.Write("half"u8);
            throw new IOException("disk full");
        }));
        AssertOnlyTheOriginal(path);
        PdfFileSaver.SaveFile(path, output => output.Write("new"u8));
        Assert.Equal("new"u8.ToArray(), File.ReadAllBytes(path));
    }
}
