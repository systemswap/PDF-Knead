using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Memory of large decoded content (docs/FEHLERGEDAECHTNIS.md, No. 1003): a file of 1 MB with one stream at the limit took up to 8.5 GB while
/// drawn, printed and saved. These tests use only what the state before the fix has as well (counter-check against the comparison build);
/// the count across the process is tested in <see cref="DecodedHeldTests"/>.
/// </summary>
[Collection(nameof(LimitCollection))]
public sealed class DecodedMemoryTests
{
    /// <summary>A one-page file whose content is one Flate stream: <paramref name="size"/> NUL bytes (white space), then a line.</summary>
    internal static byte[] PageOfWhiteSpace(int size)
    {
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, true))
        {
            z.Write(new byte[size]);
            z.Write("\n0 0 m 10 10 l S\n"u8);
        }
        var data = packed.ToArray();
        var output = new MemoryStream();
        void Text(string t) => output.Write(Encoding.Latin1.GetBytes(t));
        Text("%PDF-1.7\n");
        var offsets = new List<long>();
        foreach (var body in new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                     "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 4 0 R >>" })
        {
            offsets.Add(output.Position);
            Text($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        offsets.Add(output.Position);
        Text($"4 0 obj\n<< /Filter /FlateDecode /Length {data.Length} >>\nstream\n");
        output.Write(data);
        Text("\nendstream\nendobj\n");
        var xref = output.Position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Text($"{o:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>
    /// A large decoded page content does not go back into the shared array pool when the page is done: the pool keeps what it gets back, per
    /// processor and size, and big buffers stayed after every page — drawing, printing and saving one stream at the limit held several
    /// gigabytes afterwards. Before, the same buffer came out of the pool again.
    /// </summary>
    [Fact]
    public void LargeContent_NotKeptInTheSharedPool()
    {
        using var doc = PdfDocument.Open(PageOfWhiteSpace(24 << 20));
        byte[] seen;
        using (var content = PageInterpreter.PageContent(doc, doc.Pages[0].Dictionary))
        {
            Assert.True(content.Length > 24 << 20);
            seen = content.Array;
        }
        var again = System.Buffers.ArrayPool<byte>.Shared.Rent(seen.Length);
        try
        {
            Assert.NotSame(seen, again);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(again);
        }
    }

    /// <summary>
    /// Which resources a page uses, checked when saving: the content is decoded once into a pooled buffer and read where it lies. Before, it
    /// was decoded into an array, copied into a growing list (which doubled for the one line end after it) and copied out again — about seven
    /// times the content allocated, four times held at once.
    /// </summary>
    [Fact]
    public void UsedNames_LargeContent_DecodedOnceNotCopied()
    {
        const int size = 24 << 20;
        using var doc = PdfDocument.Open(PageOfWhiteSpace(size));
        var page = doc.Pages[0];
        ResourceUsage.UsedNames(doc, page);   // once for what is set up the first time (pool, tables)
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.NotNull(ResourceUsage.UsedNames(doc, page));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 3L * size, $"{allocated >> 20} MB allocated for {size >> 20} MB of content");
    }
}
