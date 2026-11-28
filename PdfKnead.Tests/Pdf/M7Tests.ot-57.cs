using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-57 (OpenType specification, the TrueType instruction set: instructions of the graphics state): PDF-Knead has no interpreter
/// of TrueType instructions — the bytes are skipped when a glyph is drawn and go unchanged into a subset and a scrubbed font.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-57, also used by ot-58)

    private static readonly Lazy<byte[]> Ot57SansFile = new(() => EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"));

    private static readonly Lazy<OpenTypeFont> Ot57Sans = new(() => OpenTypeFont.Parse(Ot57SansFile.Value));

    private static int Ot57U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    private static uint Ot57U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data[at..]);

    /// <summary>One table of a font file (empty when the font has none).</summary>
    private static byte[] Ot57Table(byte[] font, string tag)
    {
        for (var i = 0; i < Ot57U16(font, 4); i++)
        {
            var at = 12 + 16 * i;
            if (Encoding.ASCII.GetString(font, at, 4) == tag)
                return font.AsSpan((int)Ot57U32(font, at + 8), (int)Ot57U32(font, at + 12)).ToArray();
        }
        return [];
    }

    /// <summary>The bytes of one glyph of a font file, taken by its loca range (short or long loca after head).</summary>
    private static byte[] Ot57GlyphData(byte[] font, int glyph)
    {
        var head = Ot57Table(font, "head");
        var loca = Ot57Table(font, "loca");
        var glyf = Ot57Table(font, "glyf");
        var longLoca = BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)) != 0;
        int Offset(int g) => longLoca ? (int)Ot57U32(loca, g * 4) : Ot57U16(loca, g * 2) * 2;
        return glyf.AsSpan(Offset(glyph), Offset(glyph + 1) - Offset(glyph)).ToArray();
    }

    /// <summary>The instruction bytes of a simple glyph of a font file.</summary>
    private static byte[] Ot57GlyphInstructions(byte[] font, int glyph)
    {
        var data = Ot57GlyphData(font, glyph);
        var contours = BinaryPrimitives.ReadInt16BigEndian(data);
        Assert.True(contours > 0, "keine einfache Glyphe");
        var at = 10 + 2 * contours;
        return data.AsSpan(at + 2, Ot57U16(data, at)).ToArray();
    }

    /// <summary>
    /// Liberation Sans with the instructions of one simple glyph replaced (glyf rewritten with long loca, every glyph aligned to 4 bytes);
    /// modelled on the helper of package ot-05.
    /// </summary>
    private static byte[] Ot57WithInstructions(int glyph, byte[] instructions)
    {
        var font = Ot57SansFile.Value;
        var head = Ot57Table(font, "head");
        var count = Ot57Sans.Value.GlyphCount;
        var newGlyf = new List<byte>();
        var newLoca = new byte[(count + 1) * 4];
        for (var g = 0; g < count; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(g * 4), (uint)newGlyf.Count);
            var data = Ot57GlyphData(font, g);
            if (g == glyph)
            {
                var contours = BinaryPrimitives.ReadInt16BigEndian(data);
                Assert.True(contours > 0, "keine einfache Glyphe");
                var at = 10 + 2 * contours;
                var old = Ot57U16(data, at);
                newGlyf.AddRange(data[..at]);
                newGlyf.AddRange([(byte)(instructions.Length >> 8), (byte)instructions.Length]);
                newGlyf.AddRange(instructions);
                newGlyf.AddRange(data[(at + 2 + old)..]);
            }
            else newGlyf.AddRange(data);
            while (newGlyf.Count % 4 != 0) newGlyf.Add(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(count * 4), (uint)newGlyf.Count);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        return ColorFontBuilder.WithTables(font, new Dictionary<string, byte[]?> { ["glyf"] = [.. newGlyf], ["loca"] = newLoca, ["head"] = head });
    }

    private static byte[] Ot57Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    private static bool Ot57SameOutline(GlyphOutline a, GlyphOutline b) => a.Verbs.SequenceEqual(b.Verbs) && a.Points.SequenceEqual(b.Points);

    /// <summary>
    /// The common check of the instruction bytes (packages ot-57 and ot-58): the bytes go into the glyph <paramref name="character"/> of
    /// Liberation Sans, as its prep table, or both (<paramref name="where"/>: "glyph", "prep", "glyph+prep"). Reading: no exception, and the
    /// glyph has the same outline as without instructions and as in the font as shipped. Writing: <see cref="TrueTypeSubsetter.Subset(byte[], IEnumerable{int}, bool)"/>
    /// and <see cref="TrueTypeSubsetter.Scrub"/> with the glyph keep its instruction bytes, and „cvt “, fpgm and prep, byte for byte.
    /// </summary>
    private static void Ot57CheckInstructionBytes(string hex, char character, string where)
    {
        var bytes = Ot57Bytes(hex);
        var glyph = Ot57Sans.Value.GlyphForUnicode(character);
        Assert.True(glyph > 0);
        var inGlyph = where.Contains("glyph", StringComparison.Ordinal);
        var asPrep = where.Contains("prep", StringComparison.Ordinal);
        var font = inGlyph ? Ot57WithInstructions(glyph, bytes) : Ot57SansFile.Value;
        if (asPrep) font = ColorFontBuilder.WithTables(font, new Dictionary<string, byte[]?> { ["prep"] = bytes });

        // reading: the instructions are skipped, never executed
        var parsed = OpenTypeFont.Parse(font);
        Assert.False(parsed.Broken);
        var outline = parsed.Outline(glyph);
        var without = OpenTypeFont.Parse(Ot57WithInstructions(glyph, [])).Outline(glyph);
        Assert.False(without.IsEmpty);
        Assert.True(Ot57SameOutline(without, outline), "Umriss anders als ohne Befehle");
        Assert.True(Ot57SameOutline(Ot57Sans.Value.Outline(glyph), outline), "Umriss anders als in der Schrift");
        if (inGlyph) Assert.Equal(bytes, Ot57GlyphInstructions(font, glyph));
        if (asPrep) Assert.Equal(bytes, Ot57Table(font, "prep"));

        // writing: subset and scrubber copy the glyph and the hinting tables unchanged
        foreach (var (name, written) in new[] { ("Subset", TrueTypeSubsetter.Subset(font, [glyph])), ("Scrub", TrueTypeSubsetter.Scrub(font, [glyph])) })
        {
            Assert.Equal(Ot57GlyphInstructions(font, glyph), Ot57GlyphInstructions(written, glyph));
            foreach (var tag in new[] { "cvt ", "fpgm", "prep" })
            {
                Assert.NotEmpty(Ot57Table(font, tag));
                Assert.True(Ot57Table(font, tag).AsSpan().SequenceEqual(Ot57Table(written, tag)), $"{name}: {tag} geändert");
            }
            Assert.True(Ot57SameOutline(outline, OpenTypeFont.Parse(written).Outline(glyph)), $"{name}: Umriss geändert");
        }
    }

    // ------------------------------------------------------------ 79.6 instructions of the graphics state

    /// <summary>
    /// OT 79.6.3–79.6.44 (reader and writer; one case per row, the bytes of the case pushing their arguments first): the instructions that
    /// set or read the graphics state (vectors, reference points, zone pointers, rounding state, loop, minimum distance, instruction control,
    /// scan type, cut-in values, auto flip, delta base and shift) are never executed — the glyph H (or prep) with these bytes draws exactly
    /// as without instructions, invalid or reserved values (zone 7, SROUND period 3, a loop of 32767) change nothing and throw nothing; a
    /// subset and a scrubbed font keep the instruction bytes of the glyph and „cvt “, fpgm and prep byte for byte (79.6.10 sentences 8–15,
    /// 79.6.11 sentences 9–16, 79.6.12 sentences 8–14, 79.6.13 sentences 9–15: the values of the instructions are left as they are).
    /// </summary>
    [Theory]
    [InlineData("SVTCA 79.6.3", "00 01", 'H', "glyph")]
    [InlineData("SPVTCA 79.6.4", "02 03", 'H', "glyph")]
    [InlineData("SFVTCA 79.6.5", "04 05", 'H', "glyph")]
    [InlineData("SPVTL 79.6.6", "B1 00 01 06 B1 00 01 07", 'H', "glyph")]
    [InlineData("SFVTL 79.6.7", "B1 00 01 08 B1 00 01 09", 'H', "glyph")]
    [InlineData("SFVTPV 79.6.8", "0E", 'H', "glyph")]
    [InlineData("SDPVTL 79.6.9", "B1 00 01 86 B1 00 01 87", 'H', "glyph")]
    [InlineData("SPVFS 79.6.10", "B9 00 00 40 00 0A", 'H', "glyph")]
    [InlineData("SFVFS 79.6.11", "B9 40 00 00 00 0B", 'H', "glyph")]
    [InlineData("GPV 79.6.12", "0C", 'H', "glyph")]
    [InlineData("GFV 79.6.13", "0D", 'H', "glyph")]
    [InlineData("SRP0 79.6.14", "B0 00 10", 'H', "glyph")]
    [InlineData("SRP1 79.6.15", "B0 00 11", 'H', "glyph")]
    [InlineData("SRP2 79.6.16", "B0 00 12", 'H', "glyph")]
    [InlineData("SZP0 valid and invalid zone 79.6.17", "B0 01 13 B0 07 13", 'H', "glyph")]
    [InlineData("SZP1 79.6.18", "B0 01 14 B0 07 14", 'H', "glyph")]
    [InlineData("SZP2 79.6.19", "B0 01 15 B0 07 15", 'H', "glyph")]
    [InlineData("SZPS 79.6.20", "B0 00 16 B0 07 16", 'H', "glyph")]
    [InlineData("RTHG 79.6.21", "19", 'H', "glyph")]
    [InlineData("RTG 79.6.22", "18", 'H', "glyph")]
    [InlineData("RTDG 79.6.23", "3D", 'H', "glyph")]
    [InlineData("RDTG 79.6.24", "7D", 'H', "glyph")]
    [InlineData("RUTG 79.6.25", "7C", 'H', "glyph")]
    [InlineData("ROFF 79.6.26", "7A", 'H', "glyph")]
    [InlineData("SROUND 79.6.27", "B0 58 76", 'H', "glyph")]
    [InlineData("SROUND period 3 79.6.28", "B0 C8 76", 'H', "glyph")]
    [InlineData("SROUND phase 3 79.6.29", "B0 78 76", 'H', "glyph")]
    [InlineData("SROUND threshold 0 and 15 79.6.30", "B0 40 76 B0 4F 76", 'H', "glyph")]
    [InlineData("SROUND then MDAP[1] 79.6.30", "B0 58 76 B0 00 2F", 'H', "glyph")]
    [InlineData("SROUND threshold 0 79.6.30", "B0 40 76", 'H', "glyph")]
    [InlineData("S45ROUND 79.6.31", "B0 58 77", 'H', "glyph")]
    [InlineData("SLOOP 32767 79.6.32", "B8 7F FF 17", 'H', "glyph")]
    [InlineData("SMD 79.6.33", "B0 40 1A", 'H', "glyph")]
    [InlineData("INSTCTRL in glyph and prep 79.6.34", "B1 01 01 8E", 'H', "glyph+prep")]
    [InlineData("INSTCTRL selectors 2 and 3 79.6.34", "B1 02 02 8E B1 04 03 8E", 'H', "prep")]
    [InlineData("SCANTYPE 79.6.36", "B0 04 8D", 'H', "glyph")]
    [InlineData("SCANTYPE 0 and 5 79.6.36", "B0 00 8D B0 05 8D", 'H', "glyph")]
    [InlineData("SCANTYPE 1 then 5 79.6.36", "B0 01 8D B0 05 8D", 'H', "prep")]
    [InlineData("SCVTCI 79.6.37", "B0 44 1D", 'H', "glyph")]
    [InlineData("SSWCI 79.6.38", "B0 20 1E", 'H', "glyph")]
    [InlineData("SSW 79.6.39", "B0 50 1F", 'H', "glyph")]
    [InlineData("FLIPON 79.6.40", "4D", 'H', "glyph")]
    [InlineData("FLIPOFF 79.6.41", "4E", 'H', "glyph")]
    [InlineData("SANGW 79.6.42", "B0 00 7E", 'H', "glyph")]
    [InlineData("SDB 79.6.43", "B0 0C 5E", 'H', "glyph")]
    [InlineData("SDS 79.6.44", "B0 02 5F", 'H', "glyph")]
    public void Ot79_InstructionBytes_SkippedWhenDrawn_KeptBySubsetAndScrub(string instruction, string hex, char character, string where)
    {
        Assert.NotEmpty(instruction);
        Ot57CheckInstructionBytes(hex, character, where);
    }
}
