using System.Text;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// T.800 I.7 and Annex M: removing the vendor boxes when the metadata are cleaned, and the
/// access units of broadcast elementary streams, which are not data of JPXDecode. The codestream is the one built in
/// <see cref="JpxMarkerTests.BuiltCodestream"/>, so these tests need no file of the test collection.
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>
    /// The boxes of the JP2 file of these tests, each its own entry: signature, file type, JP2 Header box (image header, sRGB), codestream; other
    /// boxes before the JP2 Header box, behind it and at the end where given.
    /// </summary>
    private static byte[][] Jp2Boxes(byte[] codestream, byte[]? beforeHeader = null, byte[]? afterHeader = null, byte[]? atTheEnd = null) =>
    [
        Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
        Box("ftyp", Encoding.ASCII.GetBytes("jp2 "), U32(0), Encoding.ASCII.GetBytes("jp2 ")),
        beforeHeader ?? [],
        Box("jp2h", Box("ihdr", U32(4), U32(8), [0, 3, 7, 7, 0, 0]), Enumerated(16)),
        afterHeader ?? [],
        Box("jp2c", codestream),
        atTheEnd ?? [],
    ];

    private static byte[] VendorBox(string type) => type switch
    {
        "xml " => Box("xml ", Encoding.UTF8.GetBytes("<Hersteller><Name>Max Mustermann</Name></Hersteller>")),
        "uuid" => Box("uuid", [0x4D, 0x75, 0x73, 0x74, 0x65, 0x72, 0x2D, 0x49, 0x44, 0x2D, 0x30, 0x30, 0x30, 0x31, 0x2D, 0x41], Encoding.ASCII.GetBytes("Herstellerdaten")),
        _ => Box("uinf",
            Box("ulst", [0, 2], [.. Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i))], [.. Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i))]),
            Box("url ", [0, 0, 0, 0], Encoding.UTF8.GetBytes("https://hersteller.example/uuid"), [0])),
    };

    /// <summary>
    /// T.800 I.7.1 (sentence 5: type 'xml '; sentences 14–21: the UUID box with ID and data; sentences 33–41: the UUID info box with a UUID list
    /// and a data entry URL box): cleaning the metadata removes each of these boxes at the top level of a JP2 file — before the JP2 Header box,
    /// behind it and behind the codestream — and nothing else: the result is byte for byte the file without them, and its image is the same.
    /// </summary>
    [Theory]
    [InlineData("xml ")]
    [InlineData("uuid")]
    [InlineData("uinf")]
    public void MetadataRemoved_VendorBox_GoneEverythingElseKept(string type)
    {
        var cs = JpxMarkerTests.BuiltCodestream();
        var box = VendorBox(type);
        var without = Jp2Boxes(cs).SelectMany(b => b).ToArray();
        var file = Jp2Boxes(cs, box, box, box).SelectMany(b => b).ToArray();
        var cleaned = ObjectMetadata.Jpx(file);
        Assert.NotNull(cleaned);
        Assert.Equal(without, cleaned);
        Assert.Equal(JpxDecoder.Decode(file, long.MaxValue).Samples, JpxDecoder.Decode(cleaned, long.MaxValue).Samples);
        Assert.Equal(Plain(cs), JpxDecoder.Decode(cleaned, long.MaxValue).Samples);
    }

    /// <summary>
    /// T.800 I.7.1 (sentences 5, 14–21, 33–41): the three vendor boxes together, an XML box before the JP2 Header box, a UUID box behind it and a
    /// UUID info box behind the codestream, all go; the rest of the file stays as it was. A file without any of them, and a bare codestream,
    /// give nothing to clean (null).
    /// </summary>
    [Fact]
    public void MetadataRemoved_AllThreeVendorBoxes_NoneWithoutThem()
    {
        var cs = JpxMarkerTests.BuiltCodestream();
        var without = Jp2Boxes(cs).SelectMany(b => b).ToArray();
        var file = Jp2Boxes(cs, VendorBox("xml "), VendorBox("uuid"), VendorBox("uinf")).SelectMany(b => b).ToArray();
        Assert.Equal(without, ObjectMetadata.Jpx(file));
        Assert.Equal(Plain(cs), JpxDecoder.Decode(without, long.MaxValue).Samples);
        Assert.Null(ObjectMetadata.Jpx(without));
        Assert.Null(ObjectMetadata.Jpx(cs));
    }

    /// <summary>A broadcast access unit (T.800 M.3, M.4): the elementary stream header superbox, then one or two contiguous codestream boxes.</summary>
    private static byte[] AccessUnit(byte[] codestream, byte colour, int fields, bool withMasteringDisplay)
    {
        byte[] Be16(int v) => [(byte)(v >> 8), (byte)v];
        var frat = Box("frat", Be16(1001), Be16(25000));                                       // DEM, NUM (M.4.1)
        var brat = Box("brat", U32(150_000_000), U32((uint)codestream.Length + 8), U32(fields == 2 ? (uint)codestream.Length + 8 : 0));   // MaxBr, AUF1, AUF2
        var fiel = fields == 2 ? Box("fiel", [2, 1]) : [];                                      // Fic, Fio (M.4.3)
        var tcod = Box("tcod", [13, 37, 42, 7]);                                                // HH MM SS FF (M.4.4)
        var bcol = Box("bcol", [colour]);                                                       // ColrC (M.4.5, Table M.2)
        // Xc0 … Yc2, Xwp, Ywp (2 bytes each, at most 50 000), Lmin below Lmax (4 bytes each)
        byte[] dmon = withMasteringDisplay
            ? Box("dmon", Be16(34000), Be16(16000), Be16(13250), Be16(34500), Be16(7500), Be16(3000), Be16(15635), Be16(16450), U32(500), U32(10_000_000))
            : [];
        return [.. Box("elsm", frat, brat, fiel, tcod, bcol, dmon), .. Enumerable.Repeat(Box("jp2c", codestream), fields).SelectMany(b => b)];
    }

    /// <summary>
    /// T.800 Annex M (M.1–M.4.6, Tables M.1–M.6): an access unit of a broadcast elementary stream — the superbox 'elsm' with frame rate, maximum
    /// bit rate, field coding (with two fields), time code, broadcast colour specification (each code of Table M.2 and a reserved one) and
    /// mastering display, then one or two contiguous codestreams — is no data of JPXDecode (ISO 32000-2, 7.4.9: a JPX file or a codestream):
    /// no image, as without the signature and file type boxes. The same codestream in a JP2 file is read.
    /// </summary>
    [Theory]
    [InlineData((byte)0x00, 1, false)]
    [InlineData((byte)0x01, 1, true)]
    [InlineData((byte)0x02, 2, false)]
    [InlineData((byte)0x03, 2, true)]
    [InlineData((byte)0x04, 1, true)]
    [InlineData((byte)0x05, 1, true)]
    [InlineData((byte)0x06, 2, true)]
    [InlineData((byte)0x07, 1, false)]
    public void BroadcastAccessUnit_AsJpxDecodeData_NoImage(byte colour, int fields, bool withMasteringDisplay)
    {
        var cs = JpxMarkerTests.BuiltCodestream();
        Assert.Equal(Plain(cs), JpxDecoder.Decode(Jp2Boxes(cs).SelectMany(b => b).ToArray(), long.MaxValue).Samples);
        var unit = AccessUnit(cs, colour, fields, withMasteringDisplay);
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(unit, long.MaxValue));
    }

    /// <summary>
    /// T.800 M.3, Table M.1 (sentences 13, 15–18): in a JP2 file the seven box types of the elementary stream — 'elsm' with its boxes, and
    /// 'frat', 'brat', 'fiel', 'tcod', 'bcol', 'dmon' each on its own at the top level — are boxes the reader does not know: passed over, the
    /// image is the same as without them.
    /// </summary>
    [Fact]
    public void BroadcastBoxTypes_InAJp2File_PassedOver()
    {
        var cs = JpxMarkerTests.BuiltCodestream();
        var unit = AccessUnit(cs, 0x03, 2, true);
        var header = unit[..(int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(unit)];
        byte[] singles = [.. header[8..]];   // the contents of 'elsm': its six boxes one after the other
        var file = Jp2Boxes(cs, header, singles, singles).SelectMany(b => b).ToArray();
        Assert.Equal(Plain(cs), JpxDecoder.Decode(file, long.MaxValue).Samples);
    }
}
