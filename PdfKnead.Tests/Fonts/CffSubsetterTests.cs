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

    /// <summary>A number as the 5-byte form of a DICT (operator 29), so that sizes are known before positions.</summary>
    private static byte[] Five(int v) => [29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    /// <summary>
    /// Adobe TN 5176, 18 and 19: a CIDFont of about one megabyte whose FDArray holds 65 535 Font DICTs, all naming the same Private DICT with
    /// 65 535 (empty) subroutines. FDSelect can name only 256 Font DICTs (Card8); reading, subsetting and cleaning keep at most those, and
    /// read the shared Private DICT once — before, each Font DICT held its own offsets of the subroutines (256 KB), some 16 GB in all
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1136). The glyphs are drawn and kept as before.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void ManyFontDictsSharingOnePrivate_StayWithinMemory()
    {
        const int fonts = 65_535;
        var name = Index("CidBombe"u8.ToArray());
        var strings = Index("Adobe"u8.ToArray(), "Identity"u8.ToArray());
        var global = Index();
        var charStrings = Index([14], [14]);   // two glyphs, endchar
        byte[] fdSelect = [3, 0, 1, 0, 0, 0, 0, 2];   // format 3, one range: glyphs 0–1 → Font DICT 0, sentinel 2
        // Private DICT: Subrs (19) at its own offset + 6; its INDEX: 65 535 empty subroutines with 1-byte offsets
        byte[] priv = [.. Five(6), 19];
        var subrs = new List<byte> { 0xFF, 0xFF, 1 };
        for (var i = 0; i <= fonts; i++) subrs.Add(1);
        // Top DICT: ROS (12 30), CharStrings (17), FDArray (12 36), FDSelect (12 37) — all offsets 5-byte numbers
        byte[] Top(int charStringsAt, int fdArrayAt, int fdSelectAt) =>
            [.. Five(391), .. Five(392), .. Five(0), 12, 30, .. Five(charStringsAt), 17, .. Five(fdArrayAt), 12, 36, .. Five(fdSelectAt), 12, 37];
        var topSize = Index(Top(0, 0, 0)).Length;
        var charStringsAt = 4 + name.Length + topSize + strings.Length + global.Length;
        var fdSelectAt = charStringsAt + charStrings.Length;
        var fdArrayAt = fdSelectAt + fdSelect.Length;
        // FDArray: 65 535 Font DICTs of 11 bytes (Private: size, offset), 3-byte offsets
        const int fdSize = 11;
        var fdArraySize = 3 + (fonts + 1) * 3 + fonts * fdSize;
        var privateAt = fdArrayAt + fdArraySize;
        byte[] fd = [.. Five(priv.Length), .. Five(privateAt), 18];
        var fdArray = new List<byte>(fdArraySize) { 0xFF, 0xFF, 3 };
        for (var i = 0; i <= fonts; i++)
        {
            var o = 1 + i * fdSize;
            fdArray.AddRange([(byte)(o >> 16), (byte)(o >> 8), (byte)o]);
        }
        for (var i = 0; i < fonts; i++) fdArray.AddRange(fd);
        byte[] cff = [1, 0, 4, 4, .. name, .. Index(Top(charStringsAt, fdArrayAt, fdSelectAt)), .. strings, .. global, .. charStrings,
                      .. fdSelect, .. fdArray, .. priv, .. subrs];
        Assert.True(cff.Length < 1_100_000, $"{cff.Length} Bytes");

        var watch = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var font = CffFont.Parse(cff);
        Assert.Equal(2, font.GlyphCount);
        Assert.NotEmpty(CffSubsetter.Subset(cff, [1]));
        var (cleaned, keep) = CffScrubber.Scrub(cff, [1]);
        Assert.NotEmpty(cleaned);
        Assert.Contains(1, keep);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 400_000_000, $"{allocated / 1_000_000} MB angelegt");
        Assert.True(watch.ElapsedMilliseconds < 5000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>A CFF with one glyph after .notdef whose charstring the caller gives (no subroutines).</summary>
    private static byte[] OneGlyph(byte[] glyph)
    {
        var name = Index("Probe"u8.ToArray());
        var strings = Index();
        var global = Index();
        var topSize = Index(new byte[6]).Length;
        var charStringsAt = 4 + name.Length + topSize + strings.Length + global.Length;
        byte[] top = [29, (byte)(charStringsAt >> 24), (byte)(charStringsAt >> 16), (byte)(charStringsAt >> 8), (byte)charStringsAt, 17];
        return [1, 0, 4, 4, .. name, .. Index(top), .. strings, .. global, .. Index([14], glyph)];
    }

    /// <summary>
    /// Adobe TN 5177, Appendix B: dotsection is deprecated and a no-op. Flattening refused it like an arithmetic operator, so the subset
    /// failed and the redaction kept the whole program with the glyphs of the redacted text (docs/FEHLERGEDAECHTNIS.md, No. 1139).
    /// </summary>
    [Fact]
    public void Dotsection_IsLeftOutWhenFlattening()
    {
        // 100 100 rmoveto, dotsection, 50 0 rlineto, 0 50 rlineto, dotsection, endchar
        byte[] glyph = [239, 239, 21, 12, 0, 189, 139, 5, 139, 189, 5, 12, 0, 14];
        var cff = OneGlyph(glyph);
        var before = CffFont.Parse(cff).Outline(1).Bounds;
        var subset = CffSubsetter.Subset(cff, [1]);
        var (cleaned, keep) = CffScrubber.Scrub(cff, [1]);
        Assert.Contains(1, keep);
        foreach (var result in new[] { subset, cleaned })
        {
            var after = CffFont.Parse(result).Outline(1).Bounds;
            Assert.Equal((before.Left, before.Top, before.Right, before.Bottom), (after.Left, after.Top, after.Right, after.Bottom));
        }
    }

    /// <summary>
    /// Adobe TN 5177, 4.3: the arithmetic of Type 2 works on 16.16 fixed values. A chain of 70 times mul 30 000 grew beyond any finite
    /// value and put infinities and NaN into the outline and the FontBBox of the PDF/A copy (docs/FEHLERGEDAECHTNIS.md, No. 1139).
    /// </summary>
    [Fact]
    public void MultiplicationChain_StaysFinite()
    {
        var glyph = new List<byte> { 28, 0x75, 0x30 };                         // 30 000
        for (var i = 0; i < 70; i++) glyph.AddRange([28, 0x75, 0x30, 12, 24]);   // 30 000 mul
        glyph.AddRange([139, 21, 149, 149, 5, 14]);                              // 0 rmoveto, 10 10 rlineto, endchar
        var bounds = CffFont.Parse(OneGlyph([.. glyph])).Outline(1).Bounds;
        Assert.True(double.IsFinite(bounds.Left) && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Top) && double.IsFinite(bounds.Bottom),
            $"{bounds.Left} {bounds.Right} {bounds.Top} {bounds.Bottom}");
    }
}
