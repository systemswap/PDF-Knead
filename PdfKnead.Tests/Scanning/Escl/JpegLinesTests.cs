using System.Diagnostics;
using PdfKnead.Core.Pdf.Filters.Jpeg;

namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>Counting the rows of a JPEG while it arrives (the progress of a page a network scanner streams while it scans, S9e).</summary>
public sealed class JpegLinesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Prefixes_RowsGrowToTheHeight(bool color)
    {
        var jpeg = FakeScanner.Image(300, 1000, color);
        Assert.Equal((1000, 1000), JpegLines.Count(jpeg, jpeg.Length));
        var last = 0;
        var seen = new HashSet<int>();
        for (var length = 0; length <= jpeg.Length; length += Math.Max(1, jpeg.Length / 60))
        {
            var count = JpegLines.Count(jpeg, length);
            var rows = count?.Rows ?? 0;
            Assert.True(rows >= last, $"Zeilen fallen bei {length}: {rows} < {last}");
            Assert.True(rows <= 1000);
            if (count is { } c) Assert.Equal(1000, c.Height);
            last = rows;
            seen.Add(rows / 100);
        }
        Assert.True(seen.Count >= 8, "der Anteil wächst in vielen Stufen");
        // a cut that ends inside an MCU never counts that MCU
        Assert.True(JpegLines.Count(jpeg, jpeg.Length / 2)!.Value.Rows < 1000);
    }

    [Fact]
    public void OtherKinds_AndHostileHeaders_NoCount()
    {
        var jpeg = FakeScanner.Image(64, 64, true);
        Assert.Null(JpegLines.Count([], 0));
        Assert.Null(JpegLines.Count(jpeg, 3));
        Assert.Null(JpegLines.Count("%PDF-1.7"u8.ToArray(), 8));
        // progressive (SOF2) has no rows in scan order
        var progressive = (byte[])jpeg.Clone();
        var sof = progressive.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        progressive[sof + 1] = 0xC2;
        Assert.Null(JpegLines.Count(progressive, progressive.Length));
        // a header claiming 65 535 × 65 535 pixels: refused at once, nothing allocated for it
        var huge = (byte[])jpeg.Clone();
        huge[sof + 5] = huge[sof + 6] = huge[sof + 7] = huge[sof + 8] = 0xFF;
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(JpegLines.Count(huge, huge.Length));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
    }

    [Fact]
    public void Fuzz_NeverThrowsNeverSlow()
    {
        var random = new Random(957);
        var seeds = new[] { FakeScanner.Image(200, 300, true), FakeScanner.Image(200, 300, false) };
        var slowest = TimeSpan.Zero;
        for (var round = 0; round < 3000; round++)
        {
            var data = (byte[])seeds[round % 2].Clone();
            for (var k = random.Next(1, 6); k > 0; k--) data[random.Next(data.Length)] = (byte)random.Next(256);
            var clock = Stopwatch.StartNew();
            if (JpegLines.Count(data, random.Next(data.Length + 1)) is { } c) Assert.InRange(c.Rows, 0, c.Height);
            if (clock.Elapsed > slowest) slowest = clock.Elapsed;
        }
        Assert.True(slowest < JpegLines.MaxTime + TimeSpan.FromMilliseconds(100), $"langsamste Zählung {slowest.TotalMilliseconds:0} ms");
    }
}
