using System.Text;
using PdfKnead.Core.Pdf.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// CMaps as M7 reads them (ISO 32000-2 9.7.5 and 9.7.6, Adobe TN 5014 5.2): which mapping applies where they overlap, the notdef
/// mappings (docs/FEHLERGEDAECHTNIS.md, No. 1141). Measured against the reference readers with tools/normabgleich/proben/lesetoleranzen_m7.py.
/// </summary>
public sealed class CMapNormTests
{
    private static CMap Parse(string body, string codespace = "<00> <FF>") =>
        CMap.Parse(Encoding.ASCII.GetBytes($"/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n1 begincodespacerange {codespace} endcodespacerange\n{body}\nendcmap end end"));

    /// <summary>
    /// TN 5014 5.2: where mappings overlap, the later one applies — a range after a single mapping, a single mapping after a range, the
    /// later of two ranges (the reference readers: all three for two ranges, two of three for a range after a single mapping). Before, a
    /// single mapping always won and of the ranges the first.
    /// </summary>
    [Fact]
    public void OverlappingMappings_TheLaterApplies()
    {
        Assert.Equal(66, Parse("1 begincidchar <41> 100 endcidchar\n1 begincidrange <00> <FF> 1 endcidrange").ToCid(0x41, 1));
        Assert.Equal(100, Parse("1 begincidrange <00> <FF> 1 endcidrange\n1 begincidchar <41> 100 endcidchar").ToCid(0x41, 1));
        var ranges = Parse("2 begincidrange <00> <FF> 1 <41> <41> 100 endcidrange");
        Assert.Equal(100, ranges.ToCid(0x41, 1));
        Assert.Equal(0x42 + 1, ranges.ToCid(0x42, 1));
        Assert.Equal(200, Parse("2 begincidchar <41> 100 <41> 200 endcidchar").ToCid(0x41, 1));
    }

    /// <summary>
    /// ISO 32000-2 9.7.5.4, 9.7.6.3: a code that no cidchar or cidrange maps takes the CID of its notdef mapping (a range gives all its codes
    /// the same CID), else CID 0. Before, notdef mappings were not read (58 of the predefined CMaps have them).
    /// </summary>
    [Fact]
    public void NotdefMappings_GiveCodesWithoutAMappingTheirCid()
    {
        var cmap = Parse("1 beginnotdefrange <00> <1F> 7 endnotdefrange\n1 beginnotdefchar <80> 9 endnotdefchar\n1 begincidrange <20> <7E> 1 endcidrange");
        Assert.Equal(7, cmap.ToCid(0x05, 1));
        Assert.Equal(7, cmap.ToCid(0x1F, 1));
        Assert.Equal(9, cmap.ToCid(0x80, 1));
        Assert.Equal(0x41 - 0x20 + 1, cmap.ToCid(0x41, 1));
        Assert.Equal(0, cmap.ToCid(0x90, 1));
        // after the ranges as before them
        Assert.Equal(7, Parse("1 begincidrange <20> <7E> 1 endcidrange\n1 beginnotdefrange <00> <1F> 7 endnotdefrange").ToCid(0x05, 1));
    }

    /// <summary>
    /// TN 5014 5.2 allows any number of codespace ranges; the reader reads 64. The encoding CMap of the PDF/A copy made one range per run
    /// of first bytes — 100 two-byte codes with scattered first bytes gave 100 ranges, and the copy read its last codes split into single
    /// bytes. Runs are joined across their smallest gaps now, never over a first byte of another length.
    /// </summary>
    [Fact]
    public void WrittenCodespace_StaysWithinWhatTheReaderReads()
    {
        var entries = Enumerable.Range(0, 100).Select(i => ((uint)(i * 2) << 8 | 0x41, 2, i + 1)).ToList();
        entries.Add((0x05, 1, 500));   // a one-byte code inside a gap: no two-byte range may begin with 05
        var ranges = PdfKnead.Core.Pdf.Writing.PdfACMaps.Codespace(entries.Select(e => (e.Item1, e.Item2)));
        Assert.InRange(ranges.Count, 1, 64);   // CMap.MaxCodespaceRanges
        Assert.DoesNotContain(ranges, r => r.Bytes == 2 && (r.Low >> 8) <= 0x05 && (r.High >> 8) >= 0x05);
        var stream = PdfKnead.Core.Pdf.Writing.PdfACMaps.Encoding("Probe", "Adobe", "Identity", 0, 0, entries);
        var cmap = CMap.Parse(stream.RawData!);
        foreach (var (code, bytes, cid) in entries)
        {
            var data = bytes == 2 ? new[] { (byte)(code >> 8), (byte)code } : new[] { (byte)code };
            Assert.Equal(bytes, cmap.ReadCode(data, 0, out var read));
            Assert.Equal(cid, cmap.ToCid(read, bytes));
        }
    }
}
