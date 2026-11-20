using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Large files stay open while the document is (KONZEPT.md, „Speicher“; docs/FEHLERGEDAECHTNIS.md, No. 240): another program may write
/// them meanwhile. Shortened in place, reading must fail with an ordinary error instead of ending the process; replaced under its name or
/// deleted, the document keeps reading the file it opened.
/// </summary>
public sealed class LargeFileTests
{
    /// <summary>A file over 4 MiB (not read completely): a small PDF followed by blanks.</summary>
    private static string LargeFile(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", "gross-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var pdf = TestPdf.Simple(3).Build();
        var data = new byte[pdf.Length + 6_000_000];
        pdf.CopyTo(data, 0);
        Array.Fill(data, (byte)' ', pdf.Length, data.Length - pdf.Length);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static int WrittenPages(PdfDocument doc)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output);
        using var back = PdfDocument.Open(output.ToArray());
        return back.PageCount;
    }

    [Fact]
    public void ShortenedInPlace_ReadingFailsWithAnError_NotTheProcess()
    {
        if (OperatingSystem.IsWindows()) return;   // a mapped file cannot be shortened there
        var path = LargeFile("gekuerzt.pdf");
        using var doc = PdfDocument.Open(path);
        Assert.Equal(true, doc.IsUnchangedOnDisk());
        File.WriteAllBytes(path, new byte[100]);   // another program writes it anew in place
        Assert.Equal(false, doc.IsUnchangedOnDisk());
        // what was read before stays (the blocks read so far); the lost part fails with an error instead of ending the process
        Assert.Throws<PdfException>(() => doc.FileBytes(5_000_000, 16).ToArray());
        Assert.Throws<PdfException>(() => doc.FileBytes(doc.FileSize - 70_000, 140_000).ToArray());
    }

    [Fact]
    public void ReplacedOrDeleted_TheOpenedFileStaysReadable()
    {
        var path = LargeFile("ersetzt.pdf");
        using var doc = PdfDocument.Open(path);
        // replaced under its name, as most programs save (written anew, renamed). Windows refuses to overwrite or delete a mapped file
        // but allows renaming it — programs there move the old one aside first (as PdfFileSaver does)
        var other = path + ".neu";
        File.WriteAllBytes(other, TestPdf.Simple(1).Build());
        if (OperatingSystem.IsWindows())
        {
            File.Move(path, path + ".alt");
            File.Move(other, path);
        }
        else
        {
            File.Move(other, path, overwrite: true);
        }
        Assert.Equal(true, doc.IsUnchangedOnDisk());
        Assert.Equal(3, WrittenPages(doc));
        if (OperatingSystem.IsWindows()) return;   // a mapped file cannot be deleted there
        // deleted
        File.Delete(path);
        Assert.Equal(true, doc.IsUnchangedOnDisk());
        Assert.Equal(3, WrittenPages(doc));
    }

    [Fact]
    public void SmallFile_IsInMemory()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdf-knead-tests");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"klein-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, TestPdf.Simple(2).Build());
        using var doc = PdfDocument.Open(path);
        File.WriteAllBytes(path, new byte[10]);
        Assert.Null(doc.IsUnchangedOnDisk());
        Assert.Equal(2, WrittenPages(doc));
    }
}
