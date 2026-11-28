namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-58 (OpenType specification, the TrueType instruction set: reading and measuring, moving points, outline flags,
/// interpolation): PDF-Knead has no interpreter of TrueType instructions — the bytes are skipped when a glyph is drawn and go unchanged into
/// a subset and a scrubbed font. The check is the one of package ot-57 (<c>Ot57CheckInstructionBytes</c>).
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// OT 79.7.1–79.8.19 (reader and writer; one case per row, the bytes of the case pushing their arguments first): the instructions that
    /// read coordinates, distances, ppem and point size, flip on/off-curve flags, shift, move, align, intersect and interpolate points are
    /// never executed — the glyph H (FLIPRGON: the glyph O with off-curve points) with these bytes draws exactly as without instructions
    /// (point 0 stays where it is and on the curve, the curves of O stay curves, the lines of H stay lines); invalid cases (points the glyph
    /// does not have, rp1 = rp2, two parallel stems of H for ISECT, IUP in zone 0, IUP twice, the obsolete AA) change nothing and throw
    /// nothing; a subset and a scrubbed font keep the instruction bytes of the glyph and „cvt “, fpgm and prep byte for byte (79.8.10, 79.8.12:
    /// with cvt; 79.8.11 sentences 14–29: the distance flags of MDRP are left as they are).
    /// </summary>
    [Theory]
    [InlineData("GC 79.7.1", "B0 00 46 B0 00 47", 'H', "glyph")]
    [InlineData("SCFS 79.7.2", "B1 00 40 48", 'H', "glyph")]
    [InlineData("MD 79.7.3", "B1 00 01 49 B1 00 01 4A", 'H', "glyph")]
    [InlineData("MPPEM with branch 79.7.4", "4B B0 0C 52 58 B0 00 2F 59", 'H', "glyph")]
    [InlineData("MPS 79.7.5", "4C", 'H', "glyph")]
    [InlineData("FLIPPT 79.8.1", "B0 00 80", 'H', "glyph")]
    [InlineData("FLIPRGON 79.8.2", "B1 00 05 81", 'O', "glyph")]
    [InlineData("FLIPRGOFF 79.8.3", "B1 00 05 82", 'H', "glyph")]
    [InlineData("SHP 79.8.4", "B0 01 32 B0 01 33", 'H', "glyph")]
    [InlineData("SHC 79.8.5", "B0 00 34 B0 00 35", 'H', "glyph")]
    [InlineData("SHZ 79.8.6", "B0 01 36 B0 01 37", 'H', "glyph")]
    [InlineData("SHPIX with loop 3 79.8.7", "B0 03 17 B3 1B 1C 1D 50 38", 'H', "glyph")]
    [InlineData("MSIRP 79.8.8", "B1 01 40 3A B1 01 40 3B", 'H', "glyph")]
    [InlineData("MDAP 79.8.9", "B0 00 2E B0 00 2F", 'H', "glyph")]
    [InlineData("MIAP 79.8.10", "B1 00 00 3E B1 00 00 3F", 'H', "glyph")]
    [InlineData("MDRP 79.8.11", "B0 01 C0 B0 01 DF", 'H', "glyph")]
    [InlineData("MDRP distance types 0, 1, 2 79.8.11", "B0 01 C4 B0 01 C5 B0 01 C6", 'H', "glyph")]
    [InlineData("MIRP 79.8.12", "B1 01 00 E0 B1 01 00 FF", 'H', "glyph")]
    [InlineData("ROFF then MIRP[c=1] 79.8.12", "7A B1 01 00 E4", 'H', "glyph")]
    [InlineData("ALIGNRP 79.8.13", "B0 01 3C", 'H', "glyph")]
    [InlineData("AA 79.8.14", "B0 00 7F", 'H', "glyph")]
    [InlineData("ISECT 79.8.15", "B4 07 04 05 09 0A 0F", 'H', "glyph")]
    // points 4–5 (left edge of the left stem) and 10–11 (right edge of the right stem) of H are parallel
    [InlineData("ISECT with parallel stems 79.8.15", "B4 07 04 05 0A 0B 0F", 'H', "glyph")]
    [InlineData("ALIGNPTS 79.8.16", "B1 03 07 27", 'H', "glyph")]
    [InlineData("IP with rp1 = rp2 79.8.17", "B0 00 11 B0 00 12 B0 01 39", 'H', "glyph")]
    [InlineData("SRP1 SRP2 IP 79.8.17", "B0 00 11 B0 05 12 B0 02 39", 'H', "glyph")]
    [InlineData("UTP 79.8.18", "B0 02 29", 'H', "glyph")]
    [InlineData("IUP also in zone 0 79.8.19", "30 31 B0 00 15 30", 'H', "glyph")]
    [InlineData("IUP twice 79.8.19", "31 31", 'H', "glyph")]
    public void Ot79_PointInstructionBytes_SkippedWhenDrawn_KeptBySubsetAndScrub(string instruction, string hex, char character, string where)
    {
        Assert.NotEmpty(instruction);
        Ot57CheckInstructionBytes(hex, character, where);
    }
}
