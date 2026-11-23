using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

public sealed partial class JpxRobustTests
{
    /// <summary>
    /// Since a header keeps up to 9 361 progression order changes (docs/FEHLERGEDAECHTNIS.md, No. 1053): 16 384 components, 32 levels, a POC of
    /// 7 281 entries (16-bit indices) over all components and the 32 lowest resolutions — all without precincts, as the image is one sample
    /// wide at x = 1 — would have the packet loop look at 7 281 · 32 · 16 384 ≈ 3.8 · 10^9 pairs without finding a packet. Every pair counts as a
    /// step, so the loop ends at its limit, in time and with little memory — in the loop of LRCP and RLCP and in the one by position (CPRL).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ManyProgressionOrderChanges_OverManyComponents_Bounded(int order)
    {
        var cs = Codestream(1, 2, 2, 16384, 1, 32, 1, (byte)order);
        byte[] entry = [0, 0, 0, 0, 1, 32, 0x40, 0x00, (byte)order];   // RSpoc 0, CSpoc 0, LYEpoc 1, REpoc 32, CEpoc 16 384, Ppoc
        var body = Enumerable.Range(0, (65535 - 2) / 9).SelectMany(_ => entry).ToArray();
        byte[] poc = [0xFF, 0x5F, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];
        var at = 4 + (cs[4] << 8 | cs[5]);
        byte[] file = [.. cs[..at], .. poc, .. cs[at..]];
        Assert.Equal(7281, JpxCodestream.Parse(file, 0, file.Length).Main.Progressions!.Count);
        Bounded(() => JpxDecoder.DecodePlanes(file, long.MaxValue), seconds: 10, maxBytes: 2L << 30);
    }
}
