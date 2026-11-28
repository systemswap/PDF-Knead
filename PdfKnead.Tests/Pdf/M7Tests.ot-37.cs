using System.Buffers.Binary;
using PdfKnead.Core.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-37 (OpenType specification, common formats of font variations 42–42.3.5): the tests the sentence-by-sentence drafts named as
/// missing. PDF-Knead reads no tuple variation store (gvar, cvar) and of an ItemVariationStore only what blend in CFF2 needs: the number of
/// regions of each ItemVariationData. The region list, wordDeltaCount and the delta sets are passed over. The CFF2 builders are in the files of
/// the packages ot-08 and ot-11.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-37)

    /// <summary>The start of the ItemVariationStore in a CFF2 table (after its uint16 length; the Top DICT names it with operator 24).</summary>
    private static int StoreOt37(byte[] cff2)
    {
        int headerSize = cff2[2], topSize = cff2[3] << 8 | cff2[4];
        return (int)DictEntriesOt08(cff2.AsSpan(headerSize, topSize)).Single(e => e.Op == 24).Operands[^1] + 2;
    }

    /// <summary>
    /// The probes of ot-37: CFF2 tables with an fvar of one axis whose ItemVariationStore breaks a rule PDF-Knead does not need in the default
    /// instance, and what each glyph draws.
    /// </summary>
    private static Cff2ProbeOt08 ProbeOt37(string name)
    {
        var fvar = new Dictionary<string, byte[]> { ["fvar"] = FvarOt08() };
        switch (name)
        {
            case "RegionListAxisCount3RegionCount0x8001":
            {
                // the example table, its VariationRegionList saying 3 axes (fvar: 1) and 0x8001 regions (high bit set, far more than stored)
                var cff2 = ExampleCff2Ot11();
                var store = StoreOt37(cff2);
                var regionList = store + (int)BinaryPrimitives.ReadUInt32BigEndian(cff2.AsSpan(store + 2));
                BinaryPrimitives.WriteUInt16BigEndian(cff2.AsSpan(regionList), 3);
                BinaryPrimitives.WriteUInt16BigEndian(cff2.AsSpan(regionList + 2), 0x8001);
                return new(Cff2FontOt08(cff2, 2, extra: fvar), [ExampleRectOt11], ExampleRectOt11);
            }
            case "StartAbovePeakAndAcrossZero":
                // one region starting at 0.8 above its peak 0.5, one from −1 to +1 peaking at 0.5
                return new(Cff2FontOt08(ExampleCff2Ot11(new([(0.8, 0.5, 1), (-1, 0.5, 1)], [[0, 1]])), 2, extra: fvar), [ExampleRectOt11], ExampleRectOt11);
            case "LongWordsWordDeltaCount0x8005":
            {
                // one region, one ItemVariationData with itemCount 1 and regionIndexCount 1 whose wordDeltaCount is 0x8005 (LONG_WORDS, five long
                // deltas — more than the one region): blend takes k = 1 all the same
                var cff2 = Cff2Ot08(new([[], CsOt08("0 0 rmoveto 10 5 1 blend 0 rlineto 0 10 rlineto")], Variations: OneRegionOt08 with { ItemCount = 1 }));
                var store = StoreOt37(cff2);
                var data = store + (int)BinaryPrimitives.ReadUInt32BigEndian(cff2.AsSpan(store + 8));
                Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(cff2.AsSpan(data + 4)));     // regionIndexCount
                BinaryPrimitives.WriteUInt16BigEndian(cff2.AsSpan(data + 2), 0x8005);
                return new(Cff2FontOt08(cff2, 2, extra: fvar), ["0 0 rmoveto 10 0 rlineto 0 10 rlineto"]);
            }
            default:
                throw new ArgumentException(name);
        }
    }

    /// <summary>
    /// A cvar for one axis with one tuple: private point numbers (CVT entry 0) and a delta of 50; with <paramref name="embeddedPeak"/> the tuple
    /// index has 0x8000 (EMBEDDED_PEAK_TUPLE) and the header carries the peak 1.0, otherwise it names shared tuple 0 (cvar has none).
    /// </summary>
    private static byte[] CvarOt37(bool embeddedPeak)
    {
        byte[] data = [1, 0, 0, 0, 50];                              // one point: run of one byte-sized number, point 0; one byte delta, 50
        byte[] header = embeddedPeak ? [0, (byte)data.Length, 0xA0, 0x00, 0x40, 0x00] : [0, (byte)data.Length, 0x20, 0x00];
        var dataOffset = 8 + header.Length;
        return [0, 1, 0, 0, 0, 1, (byte)(dataOffset >> 8), (byte)dataOffset, .. header, .. data];
    }

    // ------------------------------------------------------------ 42.2.3 TupleVariationHeader

    /// <summary>
    /// OT 42.2.3 sentences 26–38 (reader): the flags of tupleIndex — in cvar the peak is always embedded (0x8000), the low 12 bits then do not
    /// count. PDF-Knead reads no cvar: Liberation Sans with fvar and a cvar whose tuple has 0x8000 and private point numbers, and one whose tuple
    /// lacks 0x8000 and names a shared tuple cvar does not have, parse without an exception and draw „A“ with the outline and advance of the
    /// font without cvar.
    /// </summary>
    [Fact]
    public void Cvar_WithAndWithoutEmbeddedPeakFlag_NotRead_OutlineUnchanged()
    {
        var plain = Ot27Sans.Value;
        var a = Ot27Glyph('A');
        foreach (var embedded in (bool[])[true, false])
        {
            var font = OpenTypeFont.Parse(Ot27SansWith(("fvar", Ot27Fvar()), ("cvar", CvarOt37(embedded))));
            Assert.False(font.Broken);
            Assert.True(font.HasTable("cvar"));
            Assert.True(Ot27SameOutline(plain.Outline(a), font.Outline(a)), $"0x8000 {embedded}");
            Assert.Equal(plain.AdvanceWidth(a), font.AdvanceWidth(a));
        }
    }

    // ------------------------------------------------------------ 42.3.3 regions, 42.3.4 ItemVariationStore

    /// <summary>
    /// OT 42.3.3 sentences 1–14 and 15–26, OT 42.3.4 sentences 21–31 (reader): the parts of an ItemVariationStore the default instance does not
    /// need are passed over — a VariationRegionList with axisCount 3 beside an fvar of one axis and regionCount 0x8001 (high bit set); regions
    /// starting above their peak (0.8 > 0.5) or reaching from −1 to +1; an ItemVariationData with wordDeltaCount 0x8005 (LONG_WORDS, five long
    /// deltas) at regionIndexCount 1. Each CFF2 table draws the default values without an exception (blend takes k from regionIndexCount), and
    /// the redaction writes it as a static CFF2 table.
    /// </summary>
    [Theory]
    [InlineData("RegionListAxisCount3RegionCount0x8001")]   // 42.3.3 1–14
    [InlineData("StartAbovePeakAndAcrossZero")]             // 42.3.3 15–26
    [InlineData("LongWordsWordDeltaCount0x8005")]           // 42.3.4 21–31
    public void Cff2ItemVariationStore_UnusedFields_DefaultInstanceDrawn(string probe) => AssertCff2ReadOt08(ProbeOt37(probe));
}
