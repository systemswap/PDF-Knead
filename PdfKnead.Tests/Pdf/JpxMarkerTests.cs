using System.Buffers.Binary;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The marker segments of ITU-T T.800 Annex A one by one (Konformitätsabgleich M2, rows of T.800 A.4–A.9 in docs/normabgleich/M2-filter.md):
/// segments the decoder needs no data from are passed over and leave the image as it is; capabilities it does not implement are refused
/// rather than drawn wrongly. The codestream is the 40 × 27 RGB image of a small file of the public test collection; the segments are put into
/// it here after the standard, so each rule is checked against the image the codestream alone gives.
/// </summary>
public sealed partial class JpxMarkerTests
{
    private static readonly Lazy<byte[]> Rgb = new(() => JpxColourTests.Codestream("jp2k-resetprob.pdf"));

    private static byte[] Plain(byte[] codestream) => JpxDecoder.Decode(codestream, long.MaxValue).Samples;

    private static byte[] Segment(int marker, params byte[] body) =>
        [(byte)(marker >> 8), (byte)marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];

    /// <summary>End of SIZ: the place behind which the main header may hold the other segments (A.5).</summary>
    private static int AfterSiz(byte[] cs) => 4 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(4));

    /// <summary>The first SOT.</summary>
    private static int FirstSot(byte[] cs)
    {
        var p = 2;
        while (BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p)) != 0xFF90) p += 2 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p + 2));
        return p;
    }

    private static byte[] Insert(byte[] cs, int at, params byte[][] segments) => [.. cs[..at], .. segments.SelectMany(s => s), .. cs[at..]];

    private static byte[] WithRsiz(byte[] cs, int rsiz)
    {
        var copy = (byte[])cs.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(6), (ushort)rsiz);   // SOC, SIZ, Lsiz, Rsiz
        return copy;
    }

    /// <summary>The codestream with the code-block style of COD changed by <paramref name="or"/> and Scod by <paramref name="scodOr"/>.</summary>
    private static byte[] WithCod(byte[] cs, int or, int scodOr = 0)
    {
        var copy = (byte[])cs.Clone();
        var p = 2;
        while (BinaryPrimitives.ReadUInt16BigEndian(copy.AsSpan(p)) != 0xFF52) p += 2 + BinaryPrimitives.ReadUInt16BigEndian(copy.AsSpan(p + 2));
        copy[p + 4] |= (byte)scodOr;
        copy[p + 4 + 5 + 3] |= (byte)or;   // Scod, SGcod (4 bytes), SPcod: levels, width, height, style
        return copy;
    }

    /// <summary>
    /// A.4.2: only the last tile-part may give 0 for Psot — it then reaches up to EOC. The image is the same as with the length written.
    /// </summary>
    [TestPdfsFact]
    public void LastTilePart_WithoutLength_ReachesUpToTheEnd()
    {
        var cs = Rgb.Value;
        var sot = FirstSot(cs);
        Assert.Equal(cs.Length - sot - 2, (int)BinaryPrimitives.ReadUInt32BigEndian(cs.AsSpan(sot + 6)));   // one tile-part, then EOC
        var zero = (byte[])cs.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(zero.AsSpan(sot + 6), 0);
        Assert.Equal(Plain(cs), Plain(zero));
    }

    /// <summary>
    /// A.7.1–A.7.3 and A.9: the lengths of tile-parts and packets (TLM, PLM in the main header, PLT in the tile-part header), the component
    /// registration (CRG) and comments (COM) give the decoder nothing it needs; they are passed over and the image stays the same. PLT stands
    /// before the packets it describes, the tile-part grows by its length (A.7.3).
    /// </summary>
    [TestPdfsFact]
    public void LengthsRegistrationAndComments_PassedOver()
    {
        var cs = Rgb.Value;
        var tlm = Segment(0xFF55, 0x00, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);   // Ztlm 0, ST 2, SP 1: one tile-part, index and length
        var plm = Segment(0xFF57, 0x00, 0x02, 0x81, 0x00);                        // Zplm 0, Nplm 2, one packet length
        var crg = Segment(0xFF63, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00);   // three components
        var com = Segment(0xFF64, [0x00, 0x01, .. "Probe"u8.ToArray()]);
        var main = Insert(cs, FirstSot(cs), tlm, plm, crg, com);
        var sot = FirstSot(main);
        var plt = Segment(0xFF58, 0x00, 0x05, 0x03);
        var withPlt = Insert(main, sot + 12, plt);
        BinaryPrimitives.WriteUInt32BigEndian(withPlt.AsSpan(sot + 6), BinaryPrimitives.ReadUInt32BigEndian(main.AsSpan(sot + 6)) + (uint)plt.Length);
        Assert.Equal(Plain(cs), Plain(withPlt));
    }

    /// <summary>
    /// A.5.2, A.5.3: CAP and PRF in the main header behind SIZ are passed over as long as no bit counts that a decoder may otherwise ignore;
    /// without CAP those bits may be ignored (Tables A.13 and A.19) — the image stays the same.
    /// </summary>
    [TestPdfsFact]
    public void CapAndProfile_WithoutExtendedBits_PassedOver()
    {
        var cs = Rgb.Value;
        var cap = Segment(0xFF50, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00);   // Pcap: bit of part 15, one Ccap
        var prf = Segment(0xFF56, 0x10, 0x00);                             // a profile number above 4095
        var plain = Plain(cs);
        Assert.Equal(plain, Plain(Insert(WithRsiz(cs, 0x4000), AfterSiz(cs), cap, prf)));
        Assert.Equal(plain, Plain(WithCod(cs, 0x40, 0x80)));   // without CAP: may be ignored
    }

    /// <summary>
    /// A.5.2 with A.6.1: with CAP and the second most significant bit of Rsiz, the upper bits of Scod and of the code-block style mean what
    /// another part of the standard gives them (e.g. the block coder of ISO/IEC 15444-15). The decoder does not know it; before, it decoded the
    /// data with its own block coder and drew a wrong image without notice — now refused (docs/FEHLERGEDAECHTNIS.md, No. 967). Also when CAP
    /// stands behind COD.
    /// </summary>
    [TestPdfsFact]
    public void ExtendedCapabilities_TheirBitsSet_Refused()
    {
        var cs = Rgb.Value;
        var cap = Segment(0xFF50, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00);
        var style = WithRsiz(WithCod(cs, 0x40), 0x4000);
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Insert(style, AfterSiz(style), cap), long.MaxValue));
        var scod = WithRsiz(WithCod(cs, 0, 0x20), 0x4000);
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Insert(scod, AfterSiz(scod), cap), long.MaxValue));
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Insert(style, FirstSot(style), cap), long.MaxValue));
    }

    /// <summary>
    /// ITU-T T.801, Table A.2 and Table A.5: a capability of part 2 that is required to decode and not implemented (here the variable DC offset
    /// and the arbitrary transformation kernels), and the offsets of the code-block anchor in Scod, are refused instead of decoded as part 1
    /// (docs/FEHLERGEDAECHTNIS.md, No. 967). The capabilities the decoder implements or may pass over (multiple component transformation,
    /// non-linearity, visual masking) keep the image.
    /// </summary>
    [TestPdfsFact]
    public void Part2_CapabilitiesNotImplemented_Refused()
    {
        var cs = Rgb.Value;
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(WithRsiz(cs, 0x8001), long.MaxValue));
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(WithRsiz(cs, 0x8040), long.MaxValue));
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(WithRsiz(WithCod(cs, 0, 0x08), 0x8000), long.MaxValue));
        Assert.Equal(Plain(cs), Plain(WithRsiz(cs, 0x8008)));   // visual masking: useful, not required
    }

    // ---- rows of T.800 A.2–A.6, I.2–I.6 split on 20.11.2026 (the check for collective rows): each rule its own test

    /// <summary>The codestream without the main header segment <paramref name="marker"/>.</summary>
    private static byte[] Without(byte[] cs, int marker)
    {
        var p = 2;
        while (BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p)) != marker)
        {
            Assert.NotEqual(0xFF90, BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p)));
            p += 2 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p + 2));
        }
        return [.. cs[..p], .. cs[(p + 2 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p + 2)))..]];
    }

    /// <summary>Whether the data are refused: an error, or an image marked as truncated (a fault inside a tile ends that tile).</summary>
    private static bool Refused(byte[] cs)
    {
        try
        {
            return JpxDecoder.Decode(cs, long.MaxValue).Truncated;
        }
        catch (JpxException)
        {
            return true;
        }
    }

    /// <summary>
    /// A.1.3, A.2: a marker segment with a code T.800 does not define (here 0xFF6A, with its length) is passed over in the main header and in a
    /// tile-part header; the image stays the same (Bericht M2, Zeile 250).
    /// </summary>
    [TestPdfsFact]
    public void UnknownMarkerSegment_PassedOver()
    {
        var cs = Rgb.Value;
        var unknown = Segment(0xFF6A, 0x01, 0x02, 0x03);
        var main = Insert(cs, FirstSot(cs), unknown);
        Assert.Equal(Plain(cs), Plain(main));
        var sot = FirstSot(cs);
        var tile = Insert(cs, sot + 12, unknown);
        BinaryPrimitives.WriteUInt32BigEndian(tile.AsSpan(sot + 6), BinaryPrimitives.ReadUInt32BigEndian(cs.AsSpan(sot + 6)) + (uint)unknown.Length);
        Assert.Equal(Plain(cs), Plain(tile));
    }

    /// <summary>A.5, A.6.1, A.6.4: COD and QCD are required in the main header — a codestream without one of them is refused (Zeile 470).</summary>
    [TestPdfsFact]
    public void WithoutCodOrQcd_Refused()
    {
        var cs = Rgb.Value;
        Assert.True(Refused(Without(cs, 0xFF52)));
        Assert.True(Refused(Without(cs, 0xFF5C)));
    }

    /// <summary>
    /// A.5.1, Table A.10: the profiles of digital cinema, long-term storage, broadcast and IMF in Rsiz only restrict the codestream — the decoder
    /// reads it as it is, the image is the same as with Rsiz 0 (Zeilen 473–477).
    /// </summary>
    [TestPdfsFact]
    public void ProfilesInRsiz_SameImage()
    {
        var cs = Rgb.Value;
        foreach (var rsiz in new[] { 0x0003, 0x0004, 0x0005, 0x0006, 0x0007, 0x0101, 0x0203, 0x0306, 0x0401, 0x0517, 0x0612, 0x0701, 0x0801, 0x0901 })
            Assert.Equal(Plain(cs), Plain(WithRsiz(cs, rsiz)));
    }

    /// <summary>
    /// A.5.1, B.3: the first tile must reach into the image (XTOsiz + XTsiz greater than XOsiz, likewise for y) — a SIZ where the first tile ends
    /// at the image origin is refused (Zeile 478).
    /// </summary>
    [TestPdfsFact]
    public void FirstTileOutsideTheImage_Refused()
    {
        var cs = (byte[])Rgb.Value.Clone();
        var width = BinaryPrimitives.ReadUInt32BigEndian(cs.AsSpan(8)) - BinaryPrimitives.ReadUInt32BigEndian(cs.AsSpan(16));
        BinaryPrimitives.WriteUInt32BigEndian(cs.AsSpan(8), width + 10);    // Xsiz
        BinaryPrimitives.WriteUInt32BigEndian(cs.AsSpan(16), 10);           // XOsiz
        BinaryPrimitives.WriteUInt32BigEndian(cs.AsSpan(24), 10);           // XTsiz
        BinaryPrimitives.WriteUInt32BigEndian(cs.AsSpan(32), 0);            // XTOsiz: first tile 0 … 10, the image from 10
        Assert.True(Refused(cs));
    }

    /// <summary>
    /// A.6.1, Table A.21, B.6: a precinct exponent of 0 is allowed only at resolution level 0 — user-defined precincts with PPx 0 at a higher
    /// level are refused (Zeile 292).
    /// </summary>
    [TestPdfsFact]
    public void PrecinctExponentZeroAboveLevelZero_Refused()
    {
        var cs = Rgb.Value;
        var p = 2;
        while (BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p)) != 0xFF52) p += 2 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p + 2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p + 2));
        var levels = cs[p + 9];
        var cod = cs[p..(p + 2 + length)];
        cod[4] |= 1;                                                        // Scod: precincts given
        byte[] precincts = [0xFF, .. Enumerable.Repeat((byte)0xF0, levels)];   // level 0: 15/15; above: PPx 0
        byte[] withPrecincts = [.. cod, .. precincts];
        BinaryPrimitives.WriteUInt16BigEndian(withPrecincts.AsSpan(2), (ushort)(length + precincts.Length));
        Assert.True(Refused([.. cs[..p], .. withPrecincts, .. cs[(p + 2 + length)..]]));
    }

    /// <summary>
    /// A.6.6, B.12.1: a progression order change whose last entry reaches beyond the layers, levels and components there are (here all of them
    /// in the order of COD) is read without error; the packets come in the same order, the image stays the same (Zeile 557).
    /// </summary>
    /// <summary>
    /// B.12 (probe of 23.11.2026, B7): a progression order change of 1 024 entries, the 1 023 after the first in RPCL over the area the first
    /// one finished — the precincts already read are left out and every one looked at counts as a step; the image stays the same.
    /// </summary>
    [TestPdfsFact]
    public void ProgressionOrderChange_ManyEntriesOverAFinishedArea_SameImage()
    {
        var cs = Rgb.Value;
        var p = 2;
        while (BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p)) != 0xFF52) p += 2 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p + 2));
        // the first entry reads everything in the order of COD, the other 1 023 in RPCL find only finished precincts
        byte[] first = [0, 0, 0xFF, 0xFF, 33, 255, cs[p + 5]], again = [0, 0, 0xFF, 0xFF, 33, 255, 2];
        var body = first.Concat(Enumerable.Range(0, 1023).SelectMany(_ => again)).ToArray();
        var poc = Segment(0xFF5F, body);
        Assert.Equal(Plain(cs), Plain(Insert(cs, FirstSot(cs), poc)));
    }

    [TestPdfsFact]
    public void ProgressionOrderChange_BeyondWhatThereIs_SameImage()
    {
        var cs = Rgb.Value;
        var p = 2;
        while (BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p)) != 0xFF52) p += 2 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p + 2));
        var order = cs[p + 5];
        var poc = Segment(0xFF5F, 0, 0, 0xFF, 0xFF, 33, 255, order);       // RSpoc 0, CSpoc 0, LYEpoc 65535, REpoc 33, CEpoc 255, Ppoc
        Assert.Equal(Plain(cs), Plain(Insert(cs, FirstSot(cs), poc)));
    }
}
