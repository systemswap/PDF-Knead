using System.Diagnostics;
using PdfKnead.Core.Fonts;

namespace PdfKnead.Tests.Fonts;

/// <summary>
/// FDSelect format 3 of a CID-keyed CFF (Adobe TN 5176, 19; docs/FEHLERGEDAECHTNIS.md, No. 1170): the ranges of a crafted font may run up and
/// down — each one going up refilled every glyph again, up to some 2 000 million steps for 65 535 ranges. The work is bounded now.
/// </summary>
public sealed class CffFdSelectBudgetTests
{
    /// <summary>65 535 ranges alternately from glyph 0 to 65 535 and back: read within bounded time; the first range is filled.</summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void RangesRunningUpAndDown_ReadInBoundedTime()
    {
        const int ranges = 65_535, glyphs = 65_535;
        var data = new byte[1 + 2 + 2 + 3 * ranges + 2];
        data[0] = 3;
        data[1] = 0xFF;
        data[2] = 0xFF;
        // first = 0
        for (var i = 0; i < ranges; i++)
        {
            var at = 5 + 3 * i;
            data[at] = 1;                                  // Font DICT 1
            var next = i % 2 == 0 ? glyphs : 0;            // up to the last glyph, back to 0, up again …
            data[at + 1] = (byte)(next >> 8);
            data[at + 2] = (byte)next;
        }
        var watch = Stopwatch.StartNew();
        var fd = CffFont.ReadFdSelect(data, 0, glyphs);
        Assert.True(watch.ElapsedMilliseconds < 300 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(glyphs, fd.Length);
        Assert.All(fd, b => Assert.Equal(1, b));
    }
}
