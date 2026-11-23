using System.Buffers.Binary;
using PdfKnead.Core.Pdf.Filters.Jbig2;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Findings of the sentence-by-sentence assignment of T.800 and T.801 (23.11.2026), fixed and tested here.</summary>
public sealed partial class JpxMarkerTests
{
    /// <summary>
    /// T.800 B.12.2 and A.6.6: the decoder keeps as many entries per header as one POC segment can hold — (65 535 − 2) / 7 = 9 361 (Lpoc has 16
    /// bits; one POC segment per header). A segment of 1 025 or of 9 361 entries is read whole (before at most 1 024 were kept); a second
    /// segment in the same header (not allowed) that brings the count to 9 362 is cut, and the image counts as incomplete.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(1025, 0, false)]
    [InlineData(9361, 0, false)]
    [InlineData(9361, 1, true)]
    public void ProgressionOrderChange_BeyondTheLimit_Incomplete(int entries, int second, bool incomplete)
    {
        var cs = Rgb.Value;
        byte[] entry = [0, 0, 0xFF, 0xFF, 33, 255, 0];
        var poc = Segment(0xFF5F, [.. Enumerable.Range(0, entries).SelectMany(_ => entry)]);
        if (second > 0) poc = [.. poc, .. Segment(0xFF5F, [.. Enumerable.Range(0, second).SelectMany(_ => entry)])];
        var file = Insert(cs, AfterSiz(cs), poc);
        var parsed = JpxCodestream.Parse(file, 0, file.Length);
        Assert.Equal(incomplete, parsed.Truncated);
        Assert.Equal(Math.Min(entries + second, 9361), parsed.Main.Progressions!.Count);
    }

    /// <summary>
    /// T.800 A.6.6 (B.12.2 sentence 20): at most one POC segment in a header. A second one in the main header or in the same tile-part
    /// header is read all the same (its entries count) and reported (decided on 23.11.2026: read and report); one in the main header and one
    /// in a tile-part header are allowed and not reported.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("main+main", true)]
    [InlineData("part+part", true)]
    [InlineData("main+part", false)]
    [InlineData("main", false)]
    public void ProgressionOrderChange_SecondInTheSameHeader_Reported(string where, bool reported)
    {
        var cs = Rgb.Value;
        byte[] entry = [0, 0, 0xFF, 0xFF, 33, 255, 0];
        var poc = Segment(0xFF5F, [.. entry, .. entry]);
        var file = where.StartsWith("main") ? Insert(cs, AfterSiz(cs), poc) : cs;
        if (where == "main+main") file = Insert(file, AfterSiz(file), poc);
        var inPart = where switch { "part+part" => 2, "main+part" => 1, _ => 0 };
        if (inPart > 0)
        {
            var sot = FirstSot(file);
            var added = Enumerable.Repeat(poc, inPart).SelectMany(p => p).ToArray();
            file = Insert(file, sot + 12, added);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(sot + 6), BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(sot + 6)) + (uint)added.Length);
        }
        var parsed = JpxCodestream.Parse(file, 0, file.Length);
        Assert.Equal(reported, parsed.Truncated);
        if (where == "main+main") Assert.Equal(4, parsed.Main.Progressions!.Count);
    }

    /// <summary>
    /// T.800 D.5: with segmentation symbols (code-block style 32) each cleanup pass ends with 1010 in the UNIFORM context; another value shows an
    /// error in that bit-plane — the block is drawn as decoded and counts as damaged (better than the standard asks: before the value was passed
    /// over; M2, 23.11.2026). A 4 × 4 block of one cleanup pass: four run-length decisions „all four stay insignificant“, then the symbol.
    /// </summary>
    [Theory]
    [InlineData(0b1010, false)]
    [InlineData(0b1111, true)]
    [InlineData(0b0000, true)]
    public void SegmentationSymbol_OtherThan1010_BlockDamaged(int symbol, bool damaged)
    {
        const int runLength = 17, uniform = 18;   // contexts of Table D.7
        var cx = JpxBlockDecoder.InitialContexts();
        var encoder = new MqEncoder();
        for (var column = 0; column < 4; column++) encoder.Encode(cx, runLength, 0);
        for (var bit = 3; bit >= 0; bit--) encoder.Encode(cx, uniform, symbol >> bit & 1);
        var decoder = new JpxBlockDecoder();
        decoder.Decode(4, 4, [(encoder.Finish(), 1)], 1, JpxBlockDecoder.SegmentationSymbols, 0);
        Assert.Equal(damaged, decoder.Damaged);
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++) Assert.Equal(0, decoder.At(x, y));
    }

    /// <summary>
    /// T.801, Table M.17: a fragment begins at offset 12 at the earliest — behind the signature box. A fragment table whose first fragment (of
    /// length 0) points into the signature box gives no codestream; before it was taken and the codestream of the second fragment shown (M2,
    /// 23.11.2026). The same fragment at offset 12 stays allowed.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(8u, false)]
    [InlineData(4u, false)]
    [InlineData(12u, true)]
    public void FragmentIntoTheSignatureBox_Refused(uint first, bool readable)
    {
        var cs = Rgb.Value;
        static byte[] Box(string type, byte[] body) =>
            [.. BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(8 + body.Length)), .. System.Text.Encoding.ASCII.GetBytes(type), .. body];
        static byte[] Entry(uint offset, int length) =>
            [0, 0, 0, 0, .. BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(offset)), .. BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(length)), 0, 0];
        byte[] head = [.. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]), .. Box("ftyp", [.. "jpx \0\0\0\0jpx jpxb"u8]), .. Box("mdat", cs)];
        var at = (uint)head.AsSpan().IndexOf("mdat"u8) + 4;
        byte[] file = [.. head, .. Box("ftbl", Box("flst", [0, 2, .. Entry(first, 0), .. Entry(at, cs.Length)]))];
        if (readable) Assert.Equal(Plain(cs), Plain(file));
        else Assert.Contains("kein Datenstrom", Assert.Throws<JpxException>(() => JpxDecoder.Decode(file, long.MaxValue)).Message);
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [.. BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(8 + body.Length)), .. System.Text.Encoding.ASCII.GetBytes(type), .. body];
    }

    /// <summary>A JP2 file around a codestream: signature, file type, a JP2 Header box with <paramref name="header"/>, <paramref name="before"/>
    /// boxes, the codestream box.</summary>
    private static byte[] Jp2(byte[] codestream, byte[][] header, params byte[][] before)
    {
        var siz = 2;
        var width = BinaryPrimitives.ReadUInt32BigEndian(codestream.AsSpan(siz + 6)) - BinaryPrimitives.ReadUInt32BigEndian(codestream.AsSpan(siz + 14));
        var height = BinaryPrimitives.ReadUInt32BigEndian(codestream.AsSpan(siz + 10)) - BinaryPrimitives.ReadUInt32BigEndian(codestream.AsSpan(siz + 18));
        byte[] ihdr = [.. BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(height)), .. BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(width)),
            0, 3, 7, 7, 0, 0];
        return [.. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]), .. Box("ftyp", [.. "jp2 "u8, 0, 0, 0, 0, .. "jp2 "u8]),
            .. Box("jp2h", [Box("ihdr", ihdr), Box("colr", [1, 0, 0, 0, 0, 0, 16]), .. header]), .. before.SelectMany(b => b), .. Box("jp2c", codestream)];
    }

    /// <summary>
    /// Cleaning the metadata of a JPEG 2000 image (decided on 23.11.2026, L45 brought forward): the rights box (jp2i) at the top level, an XML
    /// box inside an association with a label, a UUID box inside the JP2 Header box — all gone, the boxes around them shorter by as much, the
    /// image the same. Before only the top level was cleaned, and rights were kept everywhere.
    /// </summary>
    [TestPdfsFact]
    public void MetadataRemoved_RightsAndBoxesInSuperboxes_Gone()
    {
        var cs = Rgb.Value;
        var file = Jp2(cs, [Box("uuid", [.. new byte[16], .. "Max Mustermann"u8])],
            Box("jp2i", [.. "Rechte: Erika Musterfrau"u8]), Box("asoc", Box("lbl ", [.. "Bild"u8]), Box("xml ", [.. "<x>Max Mustermann</x>"u8])));
        var cleaned = ObjectMetadata.Jpx(file)!;
        Assert.Equal(-1, cleaned.AsSpan().IndexOf("Mustermann"u8));
        Assert.Equal(-1, cleaned.AsSpan().IndexOf("Musterfrau"u8));
        Assert.NotEqual(-1, cleaned.AsSpan().IndexOf("lbl Bild"u8));
        Assert.Equal(Plain(cs), Plain(cleaned));
        // every box still runs exactly to the next one
        for (var at = 0; at < cleaned.Length;) at += (int)BinaryPrimitives.ReadUInt32BigEndian(cleaned.AsSpan(at));
    }

    /// <summary>
    /// Cleaning the comment segments of the codestream (decided on 23.11.2026): a COM in the main header and one in the first tile-part header
    /// go, the codestream box and the tile-part (Psot) shorter by as much, the image the same and complete. A TLM segment that does not fit the
    /// codestream (here its length does not divide into entries) leaves the comment of the tile-part as it is; the one of the main header goes all
    /// the same. A TLM that fits shrinks with it: <see cref="MetadataRemoved_TileLengthsListed_EntryShrinks"/>.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataRemoved_CommentSegments_GoneWithTheirLengths(bool tileLengthsListed)
    {
        var cs = Rgb.Value;
        var withMain = Insert(cs, AfterSiz(cs), Segment(0xFF64, [0, 1, .. "Kommentar Max Mustermann"u8]));
        if (tileLengthsListed) withMain = Insert(withMain, AfterSiz(withMain), Segment(0xFF55, [0, 0x00, 0]));
        var sot = FirstSot(withMain);
        var tileComment = Segment(0xFF64, [0, 1, .. "Kachel Erika Musterfrau"u8]);
        var both = Insert(withMain, sot + 12, tileComment);
        BinaryPrimitives.WriteUInt32BigEndian(both.AsSpan(sot + 6), BinaryPrimitives.ReadUInt32BigEndian(both.AsSpan(sot + 6)) + (uint)tileComment.Length);
        var file = Jp2(both, []);
        var cleaned = ObjectMetadata.Jpx(file)!;
        Assert.Equal(-1, cleaned.AsSpan().IndexOf("Mustermann"u8));
        Assert.Equal(tileLengthsListed, cleaned.AsSpan().IndexOf("Musterfrau"u8) >= 0);
        var image = JpxDecoder.Decode(cleaned, long.MaxValue);
        Assert.False(image.Truncated);
        Assert.Equal(Plain(cs), image.Samples);
        var jp2c = cleaned.AsSpan().IndexOf("jp2c"u8) - 4;
        Assert.Equal(cleaned.Length - jp2c, (int)BinaryPrimitives.ReadUInt32BigEndian(cleaned.AsSpan(jp2c)));
    }

    /// <summary>
    /// T.800 A.7.1: where TLM segments list the tile-part lengths, the comment of a tile-part goes all the same and its entry in TLM shrinks with
    /// Psot — Ptlm of 2 and of 4 bytes, Ttlm of 0 and 1 byte, the entries of four tile-parts split over two TLM segments given out of the order
    /// of Ztlm, the comment in the third tile-part. Before the comment stayed (a name in it went along in spite of „remove metadata“; probe of
    /// 23.11.2026, L45). The image stays the same and complete, and every entry equals the Psot of its tile-part again.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(2, 0)]
    [InlineData(4, 0)]
    [InlineData(4, 1)]
    [InlineData(2, 1)]
    public void MetadataRemoved_TileLengthsListed_EntryShrinks(int ptlm, int ttlm)
    {
        var cs = FourTileParts();
        var plain = JpxDecoder.Decode(cs, long.MaxValue);
        Assert.False(plain.Truncated);
        var sot = FirstSot(cs) + 2 * 15;   // the third tile-part
        var tileComment = Segment(0xFF64, [0, 1, .. "Kachel Erika Musterfrau"u8]);
        var withComment = Insert(cs, sot + 12, tileComment);
        BinaryPrimitives.WriteUInt32BigEndian(withComment.AsSpan(sot + 6), BinaryPrimitives.ReadUInt32BigEndian(withComment.AsSpan(sot + 6)) + (uint)tileComment.Length);
        var parts = TileParts(withComment);
        Assert.Equal(4, parts.Count);
        // the entries in two TLM segments, the second one (Ztlm 1) placed first
        byte[] Entries(IEnumerable<(int Tile, int Length)> list) => list.SelectMany(e =>
            (ttlm == 1 ? new[] { (byte)e.Tile } : []).Concat(ptlm == 2 ? new[] { (byte)(e.Length >> 8), (byte)e.Length }
                : new[] { (byte)(e.Length >> 24), (byte)(e.Length >> 16), (byte)(e.Length >> 8), (byte)e.Length })).ToArray();
        var stlm = (byte)((ttlm << 4) | (ptlm == 4 ? 0x40 : 0));
        var half = (parts.Count + 1) / 2;
        var first = Segment(0xFF55, [0, stlm, .. Entries(parts.Take(half).Select(t => (t.Tile, t.Psot)))]);
        var second = Segment(0xFF55, [1, stlm, .. Entries(parts.Skip(half).Select(t => (t.Tile, t.Psot)))]);
        var listed = Insert(withComment, AfterSiz(withComment), parts.Count > half ? [.. second, .. first] : first);
        var cleaned = ObjectMetadata.Jpx(Jp2(listed, []))!;
        Assert.Equal(-1, cleaned.AsSpan().IndexOf("Musterfrau"u8));
        var image = JpxDecoder.Decode(cleaned, long.MaxValue);
        Assert.False(image.Truncated);
        Assert.Equal(plain.Samples, image.Samples);
        // every entry of TLM (ordered by Ztlm) is again the Psot of its tile-part
        var code = cleaned.AsSpan(cleaned.AsSpan().IndexOf("jp2c"u8) + 4).ToArray();
        var after = TileParts(code);
        var lengths = new List<long>();
        foreach (var z in new[] { 0, 1 })
            for (var at = 2; at + 4 <= code.Length && BinaryPrimitives.ReadUInt16BigEndian(code.AsSpan(at)) != 0xFF90;
                 at += 2 + BinaryPrimitives.ReadUInt16BigEndian(code.AsSpan(at + 2)))
            {
                if (BinaryPrimitives.ReadUInt16BigEndian(code.AsSpan(at)) != 0xFF55 || code[at + 4] != z) continue;
                var end = at + 2 + BinaryPrimitives.ReadUInt16BigEndian(code.AsSpan(at + 2));
                for (var e = at + 6 + ttlm; e < end; e += ttlm + ptlm)
                    lengths.Add(ptlm == 2 ? BinaryPrimitives.ReadUInt16BigEndian(code.AsSpan(e)) : BinaryPrimitives.ReadUInt32BigEndian(code.AsSpan(e)));
            }
        Assert.Equal(after.Select(t => (long)t.Psot), lengths);
        Assert.Equal(parts[2].Psot - tileComment.Length, after[2].Psot);
        Assert.Equal(15, after[2].Psot);
    }

    /// <summary>A codestream of 64 × 64 grey samples in four tiles of 32 × 32, each in one tile-part of 15 bytes with an empty packet.</summary>
    private static byte[] FourTileParts()
    {
        var b = new List<byte> { 0xFF, 0x4F, 0xFF, 0x51, 0x00, 41, 0x00, 0x00 };
        foreach (var v in new uint[] { 64, 64, 0, 0, 32, 32, 0, 0 }) b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        b.AddRange([0x00, 0x01, 0x07, 0x01, 0x01]);
        b.AddRange([0xFF, 0x52, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x04, 0x04, 0x00, 0x01]);
        b.AddRange([0xFF, 0x5C, 0x00, 0x04, 0x40, 0x40]);
        for (var t = 0; t < 4; t++) b.AddRange([0xFF, 0x90, 0x00, 0x0A, 0x00, (byte)t, 0x00, 0x00, 0x00, 15, 0x00, 0x01, 0xFF, 0x93, 0x00]);
        b.AddRange([0xFF, 0xD9]);
        return [.. b];
    }

    /// <summary>The tile-parts of a codestream in their order: tile index (Isot) and length (Psot).</summary>
    private static List<(int Tile, int Psot)> TileParts(byte[] cs)
    {
        var list = new List<(int, int)>();
        for (var at = FirstSot(cs); at + 12 <= cs.Length && BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(at)) == 0xFF90;)
        {
            var psot = (int)BinaryPrimitives.ReadUInt32BigEndian(cs.AsSpan(at + 6));
            list.Add((BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(at + 4)), psot));
            if (psot == 0) break;
            at += psot;
        }
        return list;
    }

    /// <summary>
    /// A bare codestream (a JPXDecode stream may be one, ISO 32000-2, 7.4.9) is cleaned the same: the COM of the main header and of the first
    /// tile-part header go, Psot shrinks, the image stays the same and complete. Before only JP2/JPX files were cleaned (23.11.2026).
    /// </summary>
    [TestPdfsFact]
    public void MetadataRemoved_BareCodestream_CommentsGone()
    {
        var cs = Rgb.Value;
        var withMain = Insert(cs, AfterSiz(cs), Segment(0xFF64, [0, 1, .. "Kommentar Max Mustermann"u8]));
        var sot = FirstSot(withMain);
        var tileComment = Segment(0xFF64, [0, 1, .. "Kachel Erika Musterfrau"u8]);
        var both = Insert(withMain, sot + 12, tileComment);
        BinaryPrimitives.WriteUInt32BigEndian(both.AsSpan(sot + 6), BinaryPrimitives.ReadUInt32BigEndian(both.AsSpan(sot + 6)) + (uint)tileComment.Length);
        var cleaned = ObjectMetadata.Jpx(both)!;
        Assert.Equal(-1, cleaned.AsSpan().IndexOf("Mustermann"u8));
        Assert.Equal(-1, cleaned.AsSpan().IndexOf("Musterfrau"u8));
        // the test codestream brings a comment of its own: the result is the codestream cleaned of it, no COM left before the first SOT
        Assert.Equal(ObjectMetadata.Jpx(cs), cleaned);
        Assert.Equal(-1, cleaned.AsSpan(0, FirstSot(cleaned)).IndexOf(new byte[] { 0xFF, 0x64 }));
        var image = JpxDecoder.Decode(cleaned, long.MaxValue);
        Assert.False(image.Truncated);
        Assert.Equal(Plain(cs), image.Samples);
        Assert.Null(ObjectMetadata.Jpx(cleaned));   // nothing left to clean
    }

    /// <summary>
    /// T.800 A.4.2: the last tile-part may have Psot 0 (it runs to the end of the codestream) — a COM in its header goes too, Psot stays 0, the
    /// image stays the same. Before such a tile-part was left as it was.
    /// </summary>
    [Fact]
    public void MetadataRemoved_LastTilePartOfUnknownLength_CommentGone()
    {
        var cs = JpxRobustTests.Codestream(0, 64, 64, 3, 1, 0, 1, 0);
        var sot = FirstSot(cs);
        BinaryPrimitives.WriteUInt32BigEndian(cs.AsSpan(sot + 6), 0);   // Psot 0: to the end
        var plain = JpxDecoder.Decode(cs, long.MaxValue).Samples;
        var withComment = Insert(cs, sot + 12, Segment(0xFF64, [0, 1, .. "Kachel Erika Musterfrau"u8]));
        var cleaned = ObjectMetadata.Jpx(withComment)!;
        Assert.Equal(-1, cleaned.AsSpan().IndexOf("Musterfrau"u8));
        Assert.Equal(cs, cleaned);
        Assert.Equal(plain, JpxDecoder.Decode(cleaned, long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801, Table M.20 (and T.800, I.5.3.1): the bit depth in the image header is 0 to 37 (1 to 38 bits); a codestream with a reserved depth,
    /// or with an image area that would be negative (XOsiz above Xsiz), is not wrapped — before the reserved depth was copied into the header and
    /// the width ran over (M2, 23.11.2026).
    /// </summary>
    [Theory]
    [InlineData(0x07, 0u, true)]
    [InlineData(0x25, 0u, true)]
    [InlineData(0x26, 0u, false)]
    [InlineData(0x7F, 0u, false)]
    [InlineData(0x07, 65u, false)]
    public void Wrap_ReservedDepthOrNegativeArea_NotWrapped(byte depth, uint xo, bool wrapped)
    {
        var cs = JpxRobustTests.Codestream(0, 64, 64, 3, 1, 0, 1, 0);
        for (var c = 0; c < 3; c++) cs[42 + 3 * c] = depth;
        BinaryPrimitives.WriteUInt32BigEndian(cs.AsSpan(16), xo);   // XOsiz
        Assert.Equal(wrapped, PdfAJpeg2000.Wrap(cs) is not null);
    }
}
