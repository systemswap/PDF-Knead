using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The count of large decoded buffers across the process until the collector takes them (<see cref="PdfLimits.MaxDecodedBytesHeld"/>,
/// <see cref="DecodedMemory"/>, docs/FEHLERGEDAECHTNIS.md, No. 1003): before, only each stream was bounded, and what was decoded at once —
/// nested forms, page parts being put together, several documents — added up without a bound.
/// </summary>
[Collection(nameof(LimitCollection))]
public sealed class DecodedHeldTests
{
    /// <summary>
    /// A page drawing form 0, which draws form 1, … form <paramref name="depth"/> − 1; each form's content is <paramref name="size"/> NUL bytes
    /// (white space), then the call of the next form (the last draws a square).
    /// </summary>
    private static byte[] NestedForms(int depth, int size)
    {
        var output = new MemoryStream();
        void Text(string t) => output.Write(Encoding.Latin1.GetBytes(t));
        Text("%PDF-1.7\n");
        var offsets = new List<long>();
        void Object(string body)
        {
            offsets.Add(output.Position);
            Text($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /F 5 0 R >> >> /Contents 4 0 R >>");
        Object("<< /Length 5 >>\nstream\n/F Do\nendstream");
        for (var i = 0; i < depth; i++)
        {
            using var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, true))
            {
                z.Write(new byte[size]);
                z.Write(Encoding.Latin1.GetBytes(i < depth - 1 ? "\n/F Do\n" : "\n0 0 10 10 re f\n"));
            }
            var next = i < depth - 1 ? $"/Resources << /XObject << /F {6 + i} 0 R >> >> " : "";
            offsets.Add(output.Position);
            Text($"{offsets.Count} 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] {next}/Filter /FlateDecode /Length {packed.Length} >>\nstream\n");
            output.Write(packed.ToArray());
            Text("\nendstream\nendobj\n");
        }
        var xref = output.Position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Text($"{o:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>The bytes still counted after a collection: only what is held.</summary>
    private static long HeldAfterCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return DecodedMemory.Held;
    }

    /// <summary>
    /// Forms nested in forms, each under the limit of one stream: what is held at once is bounded as well. Here a stream may decode to 4 MB
    /// and 8 MB may be held; each of four forms decodes to 3 MB in a buffer of 4 MB, all held while the innermost runs — the page is refused,
    /// as one stream above the limit is (here already at the second: its buffer grows to 4 MB while the first holds 4 MB, also after the
    /// collection that frees the smaller buffers). One such form is within the bound and drawn. Before, any depth was decoded and held at once.
    /// </summary>
    [Fact]
    public void NestedForms_HeldTogether_Bounded()
    {
        PdfLimits.MaxDecodedStreamBytes = 4 << 20;
        PdfLimits.MaxDecodedBytesHeld = 8 << 20;
        try
        {
            var start = HeldAfterCollection();
            using (var deep = PdfDocument.Open(NestedForms(4, 3 << 20)))
                Assert.Throws<PdfLimitException>(() => PageInterpreter.Build(deep, deep.Pages[0]));
            Assert.Equal(start, HeldAfterCollection());   // nothing held after the refusal
            using var flat = PdfDocument.Open(NestedForms(1, 3 << 20));
            Assert.True(PageInterpreter.Build(flat, flat.Pages[0]).Count > 0);
            Assert.Equal(start, HeldAfterCollection());
        }
        finally
        {
            PdfLimits.DynamicHeldLimit();
            PdfLimits.DynamicStreamLimit();
        }
    }

    /// <summary>
    /// Every way through the decoders is counted while held and no longer once collected: pooled page content, a decoding that grows and is
    /// trimmed, a stream above its limit (before, its rented buffer was not even given back to the pool), a holder that never disposes. And a
    /// new buffer that would pass the bound first has the collector find what is no longer held: page after page of large content is drawn,
    /// though their buffers together pass the bound many times.
    /// </summary>
    [Fact]
    public void Count_GivenBack_OnEveryWay()
    {
        PdfLimits.MaxDecodedStreamBytes = 4 << 20;
        PdfLimits.MaxDecodedBytesHeld = 16 << 20;
        try
        {
            var start = HeldAfterCollection();
            using var doc = PdfDocument.Open(DecodedMemoryTests.PageOfWhiteSpace(3 << 20));
            HoldContent(doc, start);
            Assert.Equal(start, HeldAfterCollection());

            DecodeOnce(doc);
            Assert.Equal(start, HeldAfterCollection());

            using var bomb = PdfDocument.Open(DecodedMemoryTests.PageOfWhiteSpace(8 << 20));
            Assert.Throws<PdfLimitException>(() => PageInterpreter.PageContent(bomb, bomb.Pages[0].Dictionary).Dispose());
            Assert.Equal(start, HeldAfterCollection());

            Forget(doc);
            Assert.Equal(start, HeldAfterCollection());

            for (var i = 0; i < 20; i++) DrawOnce(doc);   // 20 times about 7 MB decoded, the bound 16 MB
            Assert.Equal(start, HeldAfterCollection());
        }
        finally
        {
            PdfLimits.DynamicHeldLimit();
            PdfLimits.DynamicStreamLimit();
        }
    }

    // each case in a frame of its own: the first stage of the just-in-time compiler keeps every local of a method alive to its end

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void HoldContent(PdfDocument doc, long start)
    {
        using var content = PageInterpreter.PageContent(doc, doc.Pages[0].Dictionary);
        Assert.True(DecodedMemory.Held > start);
        Assert.True(HeldAfterCollection() >= start + content.Length);   // held while in use
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void DecodeOnce(PdfDocument doc)
    {
        var stream = (PdfKnead.Core.Pdf.Objects.PdfStream)doc.Pages[0].Dictionary.Get(PdfKnead.Core.Pdf.Objects.PdfName.Contents)!;
        Assert.True(doc.DecodeStream(stream).Data.Length > 3 << 20);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void DrawOnce(PdfDocument doc)
    {
        using var content = PageInterpreter.PageContent(doc, doc.Pages[0].Dictionary);
        Assert.True(content.Length > 3 << 20);
    }

    // decoded and never disposed (its buffer then stays out of the pool)
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Forget(PdfDocument doc) => _ = PageInterpreter.PageContent(doc, doc.Pages[0].Dictionary).Length;

    /// <summary>
    /// Before the first collection the runtime reports no memory: then it is asked again after one collection of the youngest generation.
    /// Before, 8 GB were assumed — a file opened right after the start got the limits of a large machine, also in a process limited to 3.5 GB.
    /// </summary>
    [Fact]
    public void Memory_NotReportedYet_AskedAgainAfterOneCollection()
    {
        var collections = 0;
        Assert.Equal(3L << 30, PdfLimits.Ask(() => collections == 0 ? 0 : 3L << 30, () => collections++));
        Assert.Equal(1, collections);
        Assert.Equal(5L << 30, PdfLimits.Ask(() => 5L << 30, () => collections++));
        Assert.Equal(1, collections);   // reported: no collection
    }

    /// <summary>The bound follows the memory: half of what the runtime gives the program, never below twice the limit of one stream.</summary>
    [Fact]
    public void HeldLimit_HalfTheMemory_AtLeastTwoStreams()
    {
        Assert.True(PdfLimits.MaxDecodedBytesHeld >= 2 * PdfLimits.MaxDecodedStreamBytes);
        Assert.True(PdfLimits.MaxDecodedBytesHeld >= GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2 - 1);
        PdfLimits.MaxDecodedStreamBytes = 64 << 20;
        try
        {
            Assert.True(PdfLimits.MaxDecodedBytesHeld >= 128 << 20);
        }
        finally
        {
            PdfLimits.DynamicStreamLimit();
        }
    }
}
