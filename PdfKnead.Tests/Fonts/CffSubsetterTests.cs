using System.Diagnostics;
using PdfKnead.Core.Fonts;

namespace PdfKnead.Tests.Fonts;

/// <summary>Tests for CFF subsetting (<see cref="CffSubsetter"/>): limits against crafted subroutines.</summary>
public sealed class CffSubsetterTests
{
    /// <summary>Builds a CFF INDEX with 4-byte offsets (Adobe Technical Note #5176, section 5).</summary>
    private static byte[] Index(params byte[][] items)
    {
        if (items.Length == 0) return [0, 0];   // empty: count only
        var result = new List<byte> { (byte)(items.Length >> 8), (byte)items.Length, 4 };
        var offset = 1;
        void Offset(int v) => result.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        Offset(offset);
        foreach (var item in items)
        {
            offset += item.Length;
            Offset(offset);
        }
        foreach (var item in items) result.AddRange(item);
        return [.. result];
    }

    /// <summary>
    /// Ten global subroutines, each calling the next one twenty times and drawing nothing: flattened, that would be 20⁹ calls. The
    /// subsetter must abort quickly. The file is built deliberately because random corruption hardly ever produces this case.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void SubroutineFanOut_IsAbortedQuickly()
    {
        // Subroutine k calls k+1: number (k+1) − 107 (bias for fewer than 1240 subroutines) as a one-byte number (+139), then callgsubr (29)
        var subrs = new byte[10][];
        for (var k = 0; k < 10; k++)
        {
            var body = new List<byte>();
            if (k < 9)
                for (var i = 0; i < 20; i++) body.AddRange([(byte)(k + 1 - 107 + 139), 29]);
            body.Add(11);   // return
            subrs[k] = [.. body];
        }
        byte[] glyph0 = [14];                               // endchar
        byte[] glyph1 = [(byte)(0 - 107 + 139), 29, 14];   // subroutine 0, endchar
        var name = Index("Bombe"u8.ToArray());
        var strings = Index();
        var global = Index(subrs);
        // Top DICT: CharStrings offset as a 5-byte number (29), operator 17, so its size is known in advance
        var topSize = Index(new byte[6]).Length;
        var charStringsAt = 4 + name.Length + topSize + strings.Length + global.Length;
        byte[] top = [29, (byte)(charStringsAt >> 24), (byte)(charStringsAt >> 16), (byte)(charStringsAt >> 8), (byte)charStringsAt, 17];
        byte[] cff = [1, 0, 4, 4, .. name, .. Index(top), .. strings, .. global, .. Index(glyph0, glyph1)];

        var watch = Stopwatch.StartNew();
        Assert.Throws<FontFormatException>(() => CffSubsetter.Subset(cff, [1]));
        Assert.True(watch.ElapsedMilliseconds < 2000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
        // Without the bomb the subset succeeds: glyph 0 only
        Assert.NotEmpty(CffSubsetter.Subset(cff, [0]));
    }
}
