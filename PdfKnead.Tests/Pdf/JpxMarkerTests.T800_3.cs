using System.Buffers.Binary;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// T.800 A.8–A.10: the markers in the bit stream (SOP, EPH), comments in main and
/// tile-part headers, the profiles and levels in Rsiz and PRF, the order of the data in the cinema profiles. The codestream is built here
/// packet by packet, so every packet header and every packet body is known to the byte and the segments can be put exactly where the
/// standard places them.
/// </summary>
public sealed partial class JpxMarkerTests
{
    // ---- a small codestream built here: 8 × 4 samples, three components of 8 bits, 5-3, one layer, one precinct and one code-block per
    // sub-band; every code-block has ten coding passes over arbitrary data (without 0xFF), so the image is fixed but not flat

    /// <summary>One packet: its header (the bits of B.10) and its body (the data of its code-blocks).</summary>
    internal readonly record struct BuiltPacket(int Component, int Resolution, byte[] Header, byte[] Body);

    /// <summary>The bits of a packet header (B.10.1): after a byte 0xFF the next one carries only seven bits.</summary>
    private sealed class HeaderBits
    {
        private readonly List<byte> _bytes = [];
        private int _value, _count;

        private int Capacity => _bytes.Count > 0 && _bytes[^1] == 0xFF ? 7 : 8;

        public void Bits(int value, int n)
        {
            for (var i = n - 1; i >= 0; i--)
            {
                _value = _value << 1 | (value >> i & 1);
                if (++_count == Capacity) Flush();
            }
        }

        private void Flush()
        {
            _bytes.Add((byte)_value);
            (_value, _count) = (0, 0);
        }

        public byte[] Finish()
        {
            if (_count > 0)
            {
                _value <<= Capacity - _count;
                Flush();
            }
            if (_bytes.Count > 0 && _bytes[^1] == 0xFF) _bytes.Add(0);
            return [.. _bytes];
        }
    }

    private const int BuiltWidth = 8, BuiltHeight = 4, BuiltPasses = 10, BuiltZeroPlanes = 2;

    /// <summary>The data of one code-block: 20 to 45 bytes, never 0xFF.</summary>
    private static byte[] BlockData(int c, int r, int band) =>
        [.. Enumerable.Range(0, 20 + 7 * c + 5 * r + 3 * band).Select(i => (byte)((i * 37 + c * 101 + r * 53 + band * 29 + 11) % 255))];

    /// <summary>The packet of component <paramref name="c"/>, resolution <paramref name="r"/> (layer 0, the only precinct).</summary>
    private static BuiltPacket Packet(int c, int r)
    {
        var bits = new HeaderBits();
        bits.Bits(1, 1);                                   // not empty
        var body = new List<byte>();
        for (var band = 0; band < (r == 0 ? 1 : 3); band++)
        {
            var data = BlockData(c, r, band);
            bits.Bits(1, 1);                               // inclusion tag tree of one node: included in layer 0
            bits.Bits(1, BuiltZeroPlanes + 1);             // zero bit-planes tag tree: 0, 0, 1 — two missing bit-planes
            bits.Bits(0b1111, 4);                          // number of coding passes 6 to 36: 1111 and five bits (Table B.4)
            bits.Bits(BuiltPasses - 6, 5);
            bits.Bits(0, 1);                               // Lblock stays 3
            bits.Bits(data.Length, 3 + 3);                 // Lblock + floor(log2(10)) bits of length
            body.AddRange(data);
        }
        return new BuiltPacket(c, r, bits.Finish(), [.. body]);
    }

    /// <summary>The packets of one layer in the order resolution, component (LRCP and RLCP alike with one layer and one precinct).</summary>
    private static List<BuiltPacket> ByResolution(int components, int levels) =>
        [.. from r in Enumerable.Range(0, levels + 1) from c in Enumerable.Range(0, components) select Packet(c, r)];

    /// <summary>The main header: SIZ, COD, QCD, then <paramref name="extra"/> segments.</summary>
    private static byte[] MainHeader(int components = 3, int levels = 1, int scod = 0, int order = 0, int rsiz = 0, int xcb = 6, int ycb = 6,
        params byte[][] extra)
    {
        var siz = new byte[36 + 3 * components];
        BinaryPrimitives.WriteUInt16BigEndian(siz, (ushort)rsiz);
        BinaryPrimitives.WriteUInt32BigEndian(siz.AsSpan(2), BuiltWidth);     // Xsiz
        BinaryPrimitives.WriteUInt32BigEndian(siz.AsSpan(6), BuiltHeight);    // Ysiz
        BinaryPrimitives.WriteUInt32BigEndian(siz.AsSpan(18), BuiltWidth);    // XTsiz: one tile
        BinaryPrimitives.WriteUInt32BigEndian(siz.AsSpan(22), BuiltHeight);   // YTsiz
        BinaryPrimitives.WriteUInt16BigEndian(siz.AsSpan(34), (ushort)components);
        for (var c = 0; c < components; c++) { siz[36 + 3 * c] = 7; siz[37 + 3 * c] = 1; siz[38 + 3 * c] = 1; }   // 8 bits, no subsampling
        // Scod, progression order, one layer, no component transformation; levels, code-block size, style 0, 5-3
        var cod = Segment(0xFF52, (byte)scod, (byte)order, 0, 1, 0, (byte)levels, (byte)(xcb - 2), (byte)(ycb - 2), 0, 1);
        // no quantisation, two guard bits; exponents LL 8, then HL 9, LH 9, HH 10 per level
        byte[] exponents = [8 << 3, .. Enumerable.Range(0, levels).SelectMany(_ => new byte[] { 9 << 3, 9 << 3, 10 << 3 })];
        var qcd = Segment(0xFF5C, [0x40, .. exponents]);
        return [0xFF, 0x4F, .. Segment(0xFF51, siz), .. cod, .. qcd, .. extra.SelectMany(e => e)];
    }

    /// <summary>A tile-part of tile 0: SOT, the <paramref name="header"/> segments, SOD, <paramref name="data"/>; Psot counts all of it.</summary>
    private static byte[] TilePart(byte[] data, int part = 0, int parts = 1, params byte[][] header)
    {
        var head = header.SelectMany(h => h).ToArray();
        var psot = 12 + head.Length + 2 + data.Length;
        return [0xFF, 0x90, 0, 10, 0, 0, (byte)(psot >> 24), (byte)(psot >> 16), (byte)(psot >> 8), (byte)psot, (byte)part, (byte)parts,
            .. head, 0xFF, 0x93, .. data];
    }

    private static byte[] Built(byte[] main, params byte[][] tileParts) => [.. main, .. tileParts.SelectMany(t => t), 0xFF, 0xD9];

    private static byte[] Sop(int nsop) => [0xFF, 0x91, 0x00, 0x04, (byte)(nsop >> 8), (byte)nsop];

    private static readonly byte[] Eph = [0xFF, 0x92];

    /// <summary>The packets in the bit stream: before each the SOP <paramref name="sop"/> gives (or none), behind each header <paramref name="eph"/>.</summary>
    private static byte[] InStream(IEnumerable<BuiltPacket> packets, Func<int, byte[]?>? sop = null, byte[]? eph = null) =>
        [.. packets.SelectMany((p, k) => (byte[])[.. sop?.Invoke(k) ?? [], .. p.Header, .. eph ?? [], .. p.Body])];

    /// <summary>The headers of the packets put together for PPM or PPT, each followed by <paramref name="eph"/>.</summary>
    private static byte[] Headers(IEnumerable<BuiltPacket> packets, byte[]? eph = null) => [.. packets.SelectMany(p => (byte[])[.. p.Header, .. eph ?? []])];

    /// <summary>The bodies of the packets alone (packet headers in PPM or PPT), each preceded by the SOP <paramref name="sop"/> gives.</summary>
    private static byte[] Bodies(IEnumerable<BuiltPacket> packets, Func<int, byte[]?>? sop = null) =>
        [.. packets.SelectMany((p, k) => (byte[])[.. sop?.Invoke(k) ?? [], .. p.Body])];

    /// <summary>The codestream built here as it is: packets in the bit stream, one tile-part, no SOP and no EPH.</summary>
    internal static byte[] BuiltCodestream() => Built(MainHeader(), TilePart(InStream(ByResolution(3, 1))));

    /// <summary>The image of the codestream built here, checked once: decoded whole, not flat.</summary>
    private static byte[] BuiltImage()
    {
        var image = JpxDecoder.Decode(BuiltCodestream(), long.MaxValue);
        Assert.False(image.Truncated);
        Assert.Equal(BuiltWidth * BuiltHeight * 3, image.Samples.Length);
        Assert.True(image.Samples.Distinct().Count() > 8, "the image is too flat to show a difference");
        return image.Samples;
    }

    private static byte[] Decoded(byte[] cs)
    {
        var image = JpxDecoder.Decode(cs, long.MaxValue);
        Assert.False(image.Truncated);
        return image.Samples;
    }

    // ---- A.8, A.8.2: EPH

    /// <summary>
    /// T.800 A.8 and A.8.2 (sentences 1–4, 7): EPH stands right behind each packet header and only ends it — in the bit stream, and with the
    /// headers moved to PPT (tile-part header) or PPM (main header) behind each header there; the packet data begin only after it. With the EPH
    /// bit in Scod the image is the same as without EPH.
    /// </summary>
    [Theory]
    [InlineData("bit stream")]
    [InlineData("PPT")]
    [InlineData("PPM")]
    public void EndOfPacketHeader_BehindEveryHeader_SameImage(string where)
    {
        var plain = BuiltImage();
        var packets = ByResolution(3, 1);
        var cs = where switch
        {
            "bit stream" => Built(MainHeader(scod: 4), TilePart(InStream(packets, eph: Eph))),
            "PPT" => Built(MainHeader(scod: 4), TilePart(Bodies(packets), header: [Segment(0xFF61, [0, .. Headers(packets, Eph)])])),
            _ => Built(MainHeader(scod: 4, extra: [Segment(0xFF60, [0, .. U32Of(Headers(packets, Eph).Length), .. Headers(packets, Eph)])]),
                TilePart(Bodies(packets))),
        };
        Assert.Equal(plain, Decoded(cs));
        // the same headers without EPH read the same: the marker only ended them
        var without = where switch
        {
            "bit stream" => BuiltCodestream(),
            "PPT" => Built(MainHeader(), TilePart(Bodies(packets), header: [Segment(0xFF61, [0, .. Headers(packets)])])),
            _ => Built(MainHeader(extra: [Segment(0xFF60, [0, .. U32Of(Headers(packets).Length), .. Headers(packets)])]), TilePart(Bodies(packets))),
        };
        Assert.Equal(plain, Decoded(without));
    }

    private static byte[] U32Of(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    /// <summary>
    /// T.800 A.8.2, Table A.41 (sentences 10–15): EPH has a fixed length of 16 bits, the marker 0xFF92 alone. Only these two bytes behind a
    /// packet header are taken as EPH: 0xFF91 or 0xFF93 there are not passed over, they are read as packet data and the image changes.
    /// </summary>
    [Theory]
    [InlineData(0x92, true)]
    [InlineData(0x91, false)]
    [InlineData(0x93, false)]
    public void EndOfPacketHeader_OnlyTheMarkerOfTableA41_PassedOver(int second, bool passedOver)
    {
        var plain = BuiltImage();
        var cs = Built(MainHeader(scod: 4), TilePart(InStream(ByResolution(3, 1), eph: [0xFF, (byte)second])));
        byte[] samples;
        try
        {
            samples = JpxDecoder.Decode(cs, long.MaxValue).Samples;
        }
        catch (JpxException)
        {
            samples = [];
        }
        if (passedOver) Assert.Equal(plain, samples);
        else Assert.NotEqual(plain, samples);
    }

    // ---- A.8.1: SOP

    /// <summary>
    /// T.800 A.8.1 (sentences 1, 5): SOP marks the start of a packet and is optional — before every packet, before only one, with the SOP bit in
    /// Scod and without it, and in front of the packet bodies when the headers are in PPT: the image is the same as without SOP.
    /// </summary>
    [Theory]
    [InlineData(true, "every", false)]
    [InlineData(true, "second", false)]
    [InlineData(false, "every", false)]
    [InlineData(true, "every", true)]
    [InlineData(true, "second", true)]
    public void StartOfPacket_Optional_SameImage(bool allowed, string which, bool ppt)
    {
        var plain = BuiltImage();
        var packets = ByResolution(3, 1);
        byte[]? Sops(int k) => which == "every" || k == 1 ? Sop(k) : null;
        var main = MainHeader(scod: allowed ? 2 : 0);
        var cs = ppt
            ? Built(main, TilePart(Bodies(packets, Sops), header: [Segment(0xFF61, [0, .. Headers(packets)])]))
            : Built(main, TilePart(InStream(packets, Sops)));
        Assert.Equal(plain, Decoded(cs));
    }

    /// <summary>
    /// T.800 A.8.1, Table A.40 (sentences 12–18, 22–24): SOP has a fixed length — the marker 0xFF91, Lsop 4 (16 bits, without the marker) and
    /// Nsop (16 bits, the packet number counted from 0 in the tile). Six bytes in front of each of the six packets are passed over; the image
    /// stays the same.
    /// </summary>
    [Fact]
    public void StartOfPacket_SegmentOfTableA40_PassedOver()
    {
        var plain = BuiltImage();
        var packets = ByResolution(3, 1);
        Assert.Equal(6, packets.Count);
        Assert.Equal(new byte[] { 0xFF, 0x91, 0x00, 0x04, 0x00, 0x05 }, Sop(5));
        Assert.Equal(plain, Decoded(Built(MainHeader(scod: 2), TilePart(InStream(packets, Sop)))));
    }

    /// <summary>
    /// T.800 A.8.1 (sentences 10, 19–21): Nsop counts the packets of a tile from 0 and rolls over to 0 after 65 535. The decoder does not use the
    /// number (no resynchronisation by it): a sequence that starts at 1 or at 65 535 (and rolls over behind it) gives the same image as the one
    /// that starts at 0.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    public void StartOfPacket_SequenceNumber_NotEvaluated(int first)
    {
        var plain = BuiltImage();
        var cs = Built(MainHeader(scod: 2), TilePart(InStream(ByResolution(3, 1), k => Sop((first + k) & 0xFFFF))));
        Assert.Equal(plain, Decoded(cs));
    }

    // ---- A.9.2: COM

    /// <summary>
    /// T.800 A.9.2 (sentences 1–4): comments may stand in the main header and in tile-part headers, as many as desired, and have no effect on
    /// decoding — three COM in the main header, two in the header of the first tile-part and one in the second (Psot counts them): the image
    /// stays the same.
    /// </summary>
    [Fact]
    public void Comments_InMainAndTilePartHeaders_SameImage()
    {
        var plain = BuiltImage();
        var packets = ByResolution(3, 1);
        byte[] Com(string text) => Segment(0xFF64, [0x00, 0x01, .. System.Text.Encoding.Latin1.GetBytes(text)]);
        var main = MainHeader(extra: [Com("eins"), Com("zwei"), Com("drei")]);
        var first = TilePart(InStream(packets.Take(3)), 0, 2, Com("vier"), Com("fünf"));
        var second = TilePart(InStream(packets.Skip(3)), 1, 2, Com("sechs"));
        Assert.Equal(plain, Decoded(Built(main, first, second)));
    }

    /// <summary>
    /// T.800 A.9.2, Table A.44 (sentences 20, 21): Rcom 0 is binary data, 1 text of ISO/IEC 8859-15, every other value reserved. The decoder does
    /// not read it: a comment with any of these values, in the main header and in the tile-part header, leaves the image as it is.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0xFFFF)]
    public void Comment_RegistrationValue_NotEvaluated(int rcom)
    {
        var plain = BuiltImage();
        var com = Segment(0xFF64, (byte)(rcom >> 8), (byte)rcom, 0x00, 0x7F, 0x80, 0xFE);
        Assert.Equal(plain, Decoded(Built(MainHeader(extra: [com]), TilePart(InStream(ByResolution(3, 1)), header: [com]))));
    }

    // ---- A.10, A.10.1: profiles and levels

    /// <summary>
    /// T.800 A.10, Table A.45 (sentences 5–7, 12, 17, 19): Profile-0 (Rsiz 1) and Profile-1 (Rsiz 2) restrict the codestream; the decoder reads it
    /// as it is. A codestream that breaks both — code-blocks of 1024 × 4 (Profile-0 asks for 32 × 32 or 64 × 64, Profile-1 for at most 64 wide
    /// and high) — gives the same image with Rsiz 1 or 2 as with Rsiz 0; since 23.11.2026 it is reported (read and report;
    /// docs/FEHLERGEDAECHTNIS.md, No. 1077).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ProfileZeroOrOne_LimitsBroken_SameImageAsRsizZero(int rsiz)
    {
        var data = TilePart(InStream(ByResolution(3, 1)));
        var reference = Decoded(Built(MainHeader(xcb: 10, ycb: 2), data));
        Assert.Equal(BuiltImage(), reference);
        var named = JpxDecoder.Decode(Built(MainHeader(rsiz: rsiz, xcb: 10, ycb: 2), data), long.MaxValue);
        Assert.Equal(reference, named.Samples);
        Assert.True(named.Truncated);
    }

    /// <summary>
    /// T.800 A.10.1, Table A.49 (sentences 208–214): the main levels 0 to 11 of the broadcast contribution profiles (single tile 0x010w,
    /// multi-tile 0x020w, the level in the lower four bits) only bound sampling rate and bit rate of a stream; the decoder does not evaluate them —
    /// every level gives the image of Rsiz 0.
    /// </summary>
    [Theory]
    [InlineData(0x0100)] [InlineData(0x0101)] [InlineData(0x0102)] [InlineData(0x0103)] [InlineData(0x0104)] [InlineData(0x0105)]
    [InlineData(0x0106)] [InlineData(0x0107)] [InlineData(0x0108)] [InlineData(0x0109)] [InlineData(0x010A)] [InlineData(0x010B)]
    [InlineData(0x0200)] [InlineData(0x0201)] [InlineData(0x0202)] [InlineData(0x0203)] [InlineData(0x0204)] [InlineData(0x0205)]
    [InlineData(0x0206)] [InlineData(0x0207)] [InlineData(0x0208)] [InlineData(0x0209)] [InlineData(0x020A)] [InlineData(0x020B)]
    public void BroadcastMainLevel_NotEvaluated(int rsiz) => Assert.Equal(BuiltImage(), Decoded(WithRsiz(BuiltCodestream(), rsiz)));

    /// <summary>
    /// T.800 A.10.1, Table A.50 (sentences 215–221): the reversible broadcast profile has main levels 6 (Rsiz 0x0306) and 7 (0x0307); the decoder
    /// does not evaluate them — the image of Rsiz 0.
    /// </summary>
    [Theory]
    [InlineData(0x0306)]
    [InlineData(0x0307)]
    public void BroadcastReversibleMainLevel_NotEvaluated(int rsiz) => Assert.Equal(BuiltImage(), Decoded(WithRsiz(BuiltCodestream(), rsiz)));

    /// <summary>
    /// T.800 A.10.1, Table A.53 (sentences 292–295): the main levels 0 to 11 of the IMF profiles in the lower four bits of Rsiz (here of the 2k
    /// lossy single tile profile 0x04yw with sublevel 0, allowed at every main level); the decoder does not evaluate them — the image of Rsiz 0.
    /// </summary>
    [Theory]
    [InlineData(0x0400)] [InlineData(0x0401)] [InlineData(0x0402)] [InlineData(0x0403)] [InlineData(0x0404)] [InlineData(0x0405)]
    [InlineData(0x0406)] [InlineData(0x0407)] [InlineData(0x0408)] [InlineData(0x0409)] [InlineData(0x040A)] [InlineData(0x040B)]
    public void ImfMainLevel_NotEvaluated(int rsiz) => Assert.Equal(BuiltImage(), Decoded(WithRsiz(BuiltCodestream(), rsiz)));

    /// <summary>
    /// T.800 A.10.1, Table A.54 (sentences 300, 301): the sublevels 0 to 9 of the IMF profiles in bits 4 to 7 of Rsiz (here with main level 11,
    /// which allows all of them, Table A.53); the decoder does not evaluate them — the image of Rsiz 0.
    /// </summary>
    [Theory]
    [InlineData(0x040B)] [InlineData(0x041B)] [InlineData(0x042B)] [InlineData(0x043B)] [InlineData(0x044B)]
    [InlineData(0x045B)] [InlineData(0x046B)] [InlineData(0x047B)] [InlineData(0x048B)] [InlineData(0x049B)]
    public void ImfSublevel_NotEvaluated(int rsiz) => Assert.Equal(BuiltImage(), Decoded(WithRsiz(BuiltCodestream(), rsiz)));

    /// <summary>
    /// T.800 A.10.1, Table A.55 (sentence 302) with A.5.3: PRFnum is 4095 plus Pprf; 0 to 4095 are not allowed. A PRF with Pprf 0 (PRFnum 4095)
    /// behind SIZ with Rsiz 0x0FFF (profile in PRF) is passed over like one with an allowed number (Pprf 1, PRFnum 4096): the image of the
    /// codestream without PRF.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ProfileNumber_NotAllowedOrAllowed_PassedOver(int pprf)
    {
        var cs = WithRsiz(BuiltCodestream(), 0x0FFF);
        Assert.Equal(BuiltImage(), Decoded(Insert(cs, AfterSiz(cs), Segment(0xFF56, (byte)(pprf >> 8), (byte)pprf))));
    }

    /// <summary>
    /// T.800 A.10.1 (sentences 130–142, Figures A.25 and A.26): the order of the data of the 4K cinema profile — one tile, the 2K part of each
    /// colour component (resolutions 0 to NL − 1) in a tile-part of its own, then the 4K part (resolution NL) of each, six tile-parts in all;
    /// CPRL in COD, POC with one progression for the 2K and one for the 4K resolutions; TLM in the main header. The decoder puts the tile-parts
    /// together and reads the packets in the order of POC: the same image as the same packets in one tile-part, and as without POC in LRCP.
    /// </summary>
    [Fact]
    public void CinemaOrder_SixTilePartsTwoProgressions_SameImage()
    {
        var plain = BuiltImage();
        // 2K_0, 2K_1, 2K_2, then 4K_0, 4K_1, 4K_2
        List<BuiltPacket> packets = [.. from r in new[] { 0, 1 } from c in new[] { 0, 1, 2 } select Packet(c, r)];
        var poc = Segment(0xFF5F, 0, 0, 0, 1, 1, 3, 4, 1, 0, 0, 1, 2, 3, 4);   // RSpoc, CSpoc, LYEpoc, REpoc, CEpoc, Ppoc (CPRL) twice
        var parts = packets.Select((p, k) => TilePart(InStream([p]), k, 6)).ToArray();
        byte[] tlm = [0x00, 0x50, .. parts.SelectMany(t => (byte[])[0, .. U32Of(t.Length)])];   // Ztlm 0, Ttlm 8 bits, Ptlm 32 bits
        var cinema = Built(MainHeader(order: 4, rsiz: 4, extra: [Segment(0xFF55, tlm), poc]), parts);
        Assert.Equal(plain, Decoded(cinema));
        Assert.Equal(plain, Decoded(Built(MainHeader(order: 4, rsiz: 4, extra: [poc]), TilePart(InStream(packets)))));
    }
}
