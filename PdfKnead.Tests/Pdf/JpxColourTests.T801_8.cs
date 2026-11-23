using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics.Icc;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The further boxes of a JPX file that carry nothing the JPX baseline reader needs (ISO/IEC 15444-2, M.11.11 to M.11.27): number lists,
/// labels, binary filters, desired reproductions, ROI descriptions, digital signatures, compositing layer extensions, multiple codestreams and
/// decomposed XML. Each box is built after its structure, with the values of its tables, the limits of its fields and damaged contents, in the
/// places the standard allows and in some it does not. The image must stay exactly what the file gives without the box, and no box may make
/// it fail; where the box holds something that would change the image if it were read, a check beside it shows that it would.
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>Builders for the boxes of M.11.11 to M.11.27 and the places they are put in.</summary>
    private static class M11OtherBoxes
    {
        /// <summary>The colour specification sYCC with precedence 5: it would win over the sRGB of the JP2 Header box if it were read.</summary>
        public static byte[] StrongYcc => Enumerated(18, 5, 1);

        /// <summary>The image of the plain file: the RGB codestream with sRGB in the JP2 Header box and nothing else.</summary>
        public static JpxImage Reference(byte[]? colour = null) => JpxDecoder.Decode(Jpx(Rgb.Value, [colour ?? Enumerated(16)]), long.MaxValue);

        /// <summary>The file decodes to exactly the image <paramref name="expected"/>: size, channels, samples, opacity, colour, profile, complete.</summary>
        public static void AssertSameImage(JpxImage expected, byte[] file, string because)
        {
            var image = JpxDecoder.Decode(file, long.MaxValue);
            Assert.True(expected.Width == image.Width && expected.Height == image.Height, because);
            Assert.True(expected.Channels == image.Channels, because);
            Assert.True(expected.Samples.AsSpan().SequenceEqual(image.Samples), because);
            Assert.True((expected.Alpha ?? []).AsSpan().SequenceEqual(image.Alpha ?? []) && (expected.Alpha is null) == (image.Alpha is null), because);
            Assert.True(expected.PremultipliedAlpha == image.PremultipliedAlpha, because);
            Assert.True(expected.Color == image.Color, because);
            Assert.True((expected.IccProfile ?? []).AsSpan().SequenceEqual(image.IccProfile ?? []) && (expected.IccProfile is null) == (image.IccProfile is null),
                because);
            Assert.False(image.Truncated, because);
        }

        public static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

        public static byte[] U64(ulong v) => [.. U32((uint)(v >> 32)), .. U32((uint)v)];

        /// <summary>
        /// The box in one of its places: at the top level (between the JP2 Header box and the codestream), inside the JP2 Header box, the
        /// Codestream Header box of the first codestream, the first Compositing Layer Header box, an Association box (first box a number list
        /// naming codestream 0) at the top level or inside one of the header boxes, or twice at the top level.
        /// </summary>
        public static byte[] Placed(string place, byte[] box, byte[]? colour = null)
        {
            var cs = Rgb.Value;
            var header = colour ?? Enumerated(16);
            byte[] Associated() => Association(NumberList(0x01000000), box);
            return place switch
            {
                "top" => Jpx(cs, [header], box),
                "twice" => Jpx(cs, [header], box, box),
                "jp2h" => Jpx(cs, [header, box]),
                "jpch" => Jpx(cs, [header], Box("jpch", box)),
                "jplh" => Jpx(cs, [header], Box("jplh", box)),
                "asoc" => Jpx(cs, [header], Associated()),
                "asoc in jp2h" => Jpx(cs, [header, Associated()]),
                "asoc in jpch" => Jpx(cs, [header], Box("jpch", Associated())),
                "behind the codestream" => [.. Jpx(cs, [header]), .. box],
                _ => throw new ArgumentException(place),
            };
        }

        public static byte[] Association(params byte[][] boxes) => Box("asoc", boxes);

        public static byte[] NumberList(params uint[] numbers) => Box("nlst", [.. numbers.SelectMany(U32)]);

        public static byte[] Label(string text) => Box("lbl ", Encoding.UTF8.GetBytes(text));

        public static byte[] Gzip(byte[] data)
        {
            using var output = new MemoryStream();
            using (var zip = new GZipStream(output, CompressionLevel.Optimal)) zip.Write(data);
            return output.ToArray();
        }

        /// <summary>The two filter types of Table M.44 (UUIDs as data).</summary>
        public static byte[] GzipFilter => Hex("EC340B04 74C511D4 A729879E A3548F0E");

        public static byte[] DesFilter => Hex("EC340B04 74C511D4 A729879E A3548F0F");

        /// <summary>A UUID of no filter the standard defines.</summary>
        public static byte[] UnknownFilter => Hex("6D757374 65726D61 6E6E2D66 696C7465");

        /// <summary>An output ICC profile: the ROMM RGB profile with the device class of an output device ('prtr').</summary>
        public static byte[] OutputProfile()
        {
            var p = CalibratedProfile.RommRgb();
            "prtr"u8.CopyTo(p.AsSpan(12));
            return p;
        }

        /// <summary>One region of an ROI Description box: Ri, Rtyp, Rcp, Rlcx, Rlcy, Rwdt, Rhth (Table M.49, 19 bytes).</summary>
        public static byte[] Region(byte ri, byte type, byte priority, uint x, uint y, uint width, uint height) =>
            [ri, type, priority, .. U32(x), .. U32(y), .. U32(width), .. U32(height)];

        public static byte[] Roi(byte count, params byte[][] regions) => Box("roid", [[count], .. regions]);

        /// <summary>A rectangle 20 × 10 at (4, 2) and an ellipse around (20, 13) extending 10 and 6: the simple regions refinements follow.</summary>
        public static byte[] Rectangle => Region(1, 0, 128, 4, 2, 20, 10);

        public static byte[] Ellipse => Region(1, 1, 128, 20, 13, 10, 6);

        /// <summary>A quadrilateral refinement of <see cref="Rectangle"/>: X = 4, 6, 19, 23 and Y = 2, 4, 8, 11 are in order.</summary>
        public static byte[] Quadrilateral(byte type) => Region(1, type, 128, 6, 4, 14, 5);

        /// <summary>An oriented elliptical refinement of <see cref="Ellipse"/>: touching the top edge at x 23, the left edge at y 11 (inside 10…30, 7…19).</summary>
        public static byte[] Oriented => Region(1, 3, 128, 23, 11, 1, 1);

        /// <summary>A Digital Signature box: Styp, Ptyp, OFF and LEN when given, DATA.</summary>
        public static byte[] Signature(byte type, byte pointer, ulong? offset, ulong? length, byte[] data) =>
            Box("chck", [type, pointer], offset is { } o ? U64(o) : [], length is { } l ? U64(l) : [], data);

        /// <summary>A Compositing Layer Extensions Info box: Mjclx, Cjclx, Ljclx, Tjclx, Fjclx and LIFE-START when given.</summary>
        public static byte[] ExtensionsInfo(uint repetition, uint codestreams, uint layers, uint threads, uint frames, uint? lifeStart) =>
            Box("jlxi", U32(repetition), U32(codestreams), U32(layers), U32(threads), U32(frames), lifeStart is { } t ? U32(t) : []);

        /// <summary>A palette of 256 entries in three columns of 8 bits (255 − i, i, 128) and the mapping of component 0 through it.</summary>
        public static byte[][] PaletteAndMapping =>
        [
            Box("pclr", U16(256), [3, 7, 7, 7], [.. Enumerable.Range(0, 256).SelectMany(i => (byte[])[(byte)(255 - i), (byte)i, 128])]),
            Box("cmap", [0, 0, 1, 0], [0, 0, 1, 1], [0, 0, 1, 2]),
        ];

        /// <summary>An Instruction Set box: Ityp 4 (LIFE and NEXT-USE given), REPT 0, TICK 1, one instruction.</summary>
        public static byte[] Instructions => Box("inst", U16(4), U16(0), U32(1), U32(1), U32(0));

        /// <summary>A Composition box: the options (27 × 40, no loop) and one instruction set.</summary>
        public static byte[] Composition => Box("comp", Box("copt", U32(27), U32(40), [0]), Instructions);

        /// <summary>A Multiple Codestream Info box: Ncs and Ltbl.</summary>
        public static byte[] MultipleInfo(uint count, uint table) => Box("j2ci", U32(count), U32(table));

        /// <summary>A Decomposed XML or XML Header box: LID, ID, then the front matter or the opening tag.</summary>
        public static byte[] XmlPart(string type, int idLength, string id, string text) =>
            Box(type, U16(idLength), Encoding.UTF8.GetBytes(id), Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// T.801 M.11.12: a Number List box names entities by ANi — the high byte the kind, the lower three the number (Table M.42: 0 the composited
    /// image, 0x01 a codestream, 0x02 a compositing layer), each a 32-bit field from 0 to 2^32 − 1 (Table M.43). Every kind of the table, the
    /// highest numbers, the ends of the field's range, several entries, none and one cut short, inside an Association box with a label at the
    /// top level, in the JP2 Header box and in the Codestream Header box of the first codestream: the image stays what it is without them.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("00000000")]                              // the composited image
    [InlineData("01000000")]                              // codestream 0
    [InlineData("01FFFFFF")]                              // the highest codestream number
    [InlineData("02000000")]                              // compositing layer 0
    [InlineData("02FFFFFF")]                              // the highest compositing layer number
    [InlineData("FFFFFFFF")]                              // the top of the field's range: a kind outside Table M.42
    [InlineData("00000000 01000000 02000005 FFFFFFFF")]   // several entities in one list
    [InlineData("")]                                      // no entity
    [InlineData("010000")]                                // an entry cut short
    public void NumberList_EveryEntityKindAndTheFieldRange_PassedOver(string numbers)
    {
        var expected = M11OtherBoxes.Reference();
        var association = M11OtherBoxes.Association(Box("nlst", M11OtherBoxes.Hex(numbers)), M11OtherBoxes.Label("Bild"));
        foreach (var place in new[] { "top", "jp2h", "jpch", "twice" })
            M11OtherBoxes.AssertSameImage(expected, M11OtherBoxes.Placed(place, association), $"{numbers} {place}");
    }

    /// <summary>
    /// T.801 M.11.13: the label is UTF-8 without the control characters U+0000–U+001F and U+007F–U+009F and without / ; ? : #, never ended by a
    /// null or padded. Labels breaking each of these rules, one that is no UTF-8 at all and an empty one — in the Codestream Header box of the
    /// first codestream, the first Compositing Layer Header box and in Association boxes: the label is not read, the image stays as it is.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("42696C64")]       // a well-formed label
    [InlineData("00")]             // U+0000
    [InlineData("1F")]             // U+001F
    [InlineData("7F")]             // U+007F
    [InlineData("C285")]           // U+0085
    [InlineData("C29F")]           // U+009F
    [InlineData("42696C642F78")]   // '/'
    [InlineData("3B")]             // ';'
    [InlineData("3F")]             // '?'
    [InlineData("3A")]             // ':'
    [InlineData("23")]             // '#'
    [InlineData("42696C6400")]     // ended by a null
    [InlineData("42696C640000")]   // padded with nulls
    [InlineData("FFFE")]           // no UTF-8
    [InlineData("")]               // empty
    public void Label_ForbiddenCharactersNullOrPadding_PassedOver(string text)
    {
        var expected = M11OtherBoxes.Reference();
        var label = Box("lbl ", M11OtherBoxes.Hex(text));
        foreach (var place in new[] { "jpch", "jplh", "asoc", "asoc in jp2h", "asoc in jpch" })
            M11OtherBoxes.AssertSameImage(expected, M11OtherBoxes.Placed(place, label), $"{text} {place}");
    }

    /// <summary>
    /// T.801 M.11.14: a Binary Filter box holds the filter type F (a UUID, Table M.45: 128 bits) and the transformed data DATA; a conforming
    /// reader need not process it. Each filter of Table M.44 and an unknown one, the data holding a colour specification that would win over the
    /// one of the JP2 Header box (gzip-compressed, encrypted, unfiltered), damaged data, data cut short, a box of only F, a shorter F and an empty
    /// box, at the top level (also twice), in the JP2 Header box and in the Codestream Header box of the first codestream: never unfolded, the
    /// image stays sRGB as without the box.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("gzip")]
    [InlineData("encrypted")]
    [InlineData("unknown filter, plain data")]
    [InlineData("unknown filter, gzip data")]
    [InlineData("damaged data")]
    [InlineData("data cut short")]
    [InlineData("only F")]
    [InlineData("F shorter than 16 bytes")]
    [InlineData("empty")]
    public void BinaryFilter_NeverUnfolded_WhateverTheFilterAndData(string kind)
    {
        var expected = M11OtherBoxes.Reference();
        var hidden = M11OtherBoxes.StrongYcc;
        var gzip = M11OtherBoxes.Gzip(hidden);
        byte[] contents = kind switch
        {
            "gzip" => [.. M11OtherBoxes.GzipFilter, .. gzip],
            // an initialisation vector of 8 bytes, then the colour box made unreadable, padded to whole blocks of 8
            "encrypted" => [.. M11OtherBoxes.DesFilter, 1, 2, 3, 4, 5, 6, 7, 8, .. hidden.Select(b => (byte)(b ^ 0xA5)), .. new byte[8 - hidden.Length % 8]],
            "unknown filter, plain data" => [.. M11OtherBoxes.UnknownFilter, .. hidden],
            "unknown filter, gzip data" => [.. M11OtherBoxes.UnknownFilter, .. gzip],
            "damaged data" => [.. M11OtherBoxes.GzipFilter, .. "kein gzip"u8],
            "data cut short" => [.. M11OtherBoxes.GzipFilter, .. gzip[..(gzip.Length / 2)]],
            "only F" => M11OtherBoxes.GzipFilter,
            "F shorter than 16 bytes" => M11OtherBoxes.GzipFilter[..10],
            _ => [],
        };
        var filter = Box("bfil", contents);
        foreach (var place in new[] { "top", "twice", "jp2h", "jpch" })
            M11OtherBoxes.AssertSameImage(expected, M11OtherBoxes.Placed(place, filter), $"{kind} {place}");
        // what the filtered data hold would change the image if it were read
        Assert.NotEqual(expected.Samples, JpxDecoder.Decode(M11OtherBoxes.Placed("jp2h", hidden), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 M.11.15 and M.11.15.1: the Desired Reproductions box (a superbox) holds the Graphics Technology Standard Output box with an output
    /// ICC profile OUTP (Table M.46, its version in bytes 8–11) and possibly boxes a reader does not know; a reader need not apply it. The box
    /// with that profile and an unknown box, with an empty and a damaged OUTP, empty, and the output box on its own outside of it, at the top
    /// level and in the JP2 Header box: samples, colour and ICC profile of the image stay those of the image's own colour specification.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("output profile and an unknown box")]
    [InlineData("OUTP empty")]
    [InlineData("OUTP damaged")]
    [InlineData("empty")]
    [InlineData("output box outside the reproductions box")]
    public void DesiredReproductions_OutputProfileNeverApplied(string kind)
    {
        var srgb = Icc(CalibratedProfile.Srgb());
        var expected = M11OtherBoxes.Reference(srgb);
        var output = M11OtherBoxes.OutputProfile();
        Assert.Equal("prtr"u8.ToArray(), output[12..16]);
        Assert.NotEqual(0, output[8]);   // the version of the profile, bytes 8–11
        var box = kind switch
        {
            "output profile and an unknown box" => Box("drep", Box("gtso", output), Box("musr", "Erika Musterfrau"u8.ToArray())),
            "OUTP empty" => Box("drep", Box("gtso")),
            "OUTP damaged" => Box("drep", Box("gtso", output[..100])),
            "empty" => Box("drep"),
            _ => Box("gtso", output),
        };
        foreach (var place in new[] { "top", "jp2h", "behind the codestream" })
            M11OtherBoxes.AssertSameImage(expected, M11OtherBoxes.Placed(place, box, srgb), $"{kind} {place}");
        Assert.Equal(CalibratedProfile.Srgb(), expected.IccProfile);
        // the output profile as a colour specification of the image would change it
        Assert.NotEqual(expected.IccProfile, JpxDecoder.Decode(Jpx(Rgb.Value, [srgb, Icc(output, 5, 1)]), long.MaxValue).IccProfile);
    }

    /// <summary>
    /// T.801 M.11.16: ROI Description boxes describe parts of the image, need not be coded as ROIs, may be several, and mean the codestream
    /// (in its Codestream Header box), all codestreams (in the JP2 Header box) or the rendered result (at the top level). A box with a rectangle
    /// and its quadrilateral refinement and an ellipse and its oriented refinement in each of these places, inside Association boxes and twice:
    /// the reader passes them over, the image stays as it is.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("top")]
    [InlineData("twice")]
    [InlineData("jp2h")]
    [InlineData("jpch")]
    [InlineData("asoc")]
    [InlineData("asoc in jp2h")]
    [InlineData("asoc in jpch")]
    public void RoiDescription_EveryPlace_PassedOver(string place)
    {
        var roi = M11OtherBoxes.Roi(4, M11OtherBoxes.Rectangle, M11OtherBoxes.Quadrilateral(0x02), M11OtherBoxes.Ellipse, M11OtherBoxes.Oriented);
        M11OtherBoxes.AssertSameImage(M11OtherBoxes.Reference(), M11OtherBoxes.Placed(place, roi), place);
    }

    /// <summary>
    /// T.801 M.11.16, Tables M.47 to M.49bis: Nroi and per region Ri, Rtyp, Rcp (8 bits each), Rlcx, Rlcy, Rwdt, Rhth (32 bits each). Every value
    /// of Ri, every type of Rtyp (rectangle 0, ellipse 1, quadrilateral refinement AABB CDD0 with D = 01, 10, 11 and the largest A, B, C, the
    /// oriented refinement 3) and reserved ones (D = 00, B = 3, an odd value, 0xFF), refinements without the simple region before them and with
    /// coordinates out of order or outside the bounds, every field at the ends of its range, a count larger than the regions, an entry cut
    /// short and an empty box — at the top level, in the JP2 Header box and in the Codestream Header box: no region is computed, the image stays
    /// as it is.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("no region")]
    [InlineData("rectangle, coded")]
    [InlineData("rectangle, not coded")]
    [InlineData("Ri outside Table M.47")]
    [InlineData("ellipse")]
    [InlineData("quadrilateral D=01")]
    [InlineData("quadrilateral D=10")]
    [InlineData("quadrilateral D=11")]
    [InlineData("quadrilateral A=3 B=2 C=1 D=11")]
    [InlineData("quadrilateral D=00 (reserved)")]
    [InlineData("quadrilateral B=3 (reserved)")]
    [InlineData("quadrilateral out of order")]
    [InlineData("quadrilateral without rectangle")]
    [InlineData("oriented ellipse")]
    [InlineData("oriented ellipse outside the bounds")]
    [InlineData("oriented ellipse behind a rectangle")]
    [InlineData("reserved odd type")]
    [InlineData("reserved type 0xFF")]
    [InlineData("priority 0")]
    [InlineData("priority 255")]
    [InlineData("location 0")]
    [InlineData("location 2^32-1")]
    [InlineData("size 1")]
    [InlineData("size 2^32-1")]
    [InlineData("size 0")]
    [InlineData("all fields lowest")]
    [InlineData("all fields highest, 255 regions")]
    [InlineData("count 255, one region")]
    [InlineData("count 1, no region")]
    [InlineData("entry cut short")]
    [InlineData("empty box")]
    public void RoiDescription_EveryTableValueAndDamagedEntries_PassedOver(string kind)
    {
        const uint max = uint.MaxValue;
        var rectangle = M11OtherBoxes.Rectangle;
        var ellipse = M11OtherBoxes.Ellipse;
        var roi = kind switch
        {
            "no region" => M11OtherBoxes.Roi(0),
            "rectangle, coded" => M11OtherBoxes.Roi(1, rectangle),
            "rectangle, not coded" => M11OtherBoxes.Roi(1, M11OtherBoxes.Region(0, 0, 128, 4, 2, 20, 10)),
            "Ri outside Table M.47" => M11OtherBoxes.Roi(1, M11OtherBoxes.Region(2, 0, 128, 4, 2, 20, 10)),
            "ellipse" => M11OtherBoxes.Roi(1, ellipse),
            "quadrilateral D=01" => M11OtherBoxes.Roi(2, rectangle, M11OtherBoxes.Quadrilateral(0b0000_0010)),
            "quadrilateral D=10" => M11OtherBoxes.Roi(2, rectangle, M11OtherBoxes.Quadrilateral(0b0000_0100)),
            "quadrilateral D=11" => M11OtherBoxes.Roi(2, rectangle, M11OtherBoxes.Quadrilateral(0b0000_0110)),
            "quadrilateral A=3 B=2 C=1 D=11" => M11OtherBoxes.Roi(2, rectangle, M11OtherBoxes.Quadrilateral(0b1110_1110)),
            "quadrilateral D=00 (reserved)" => M11OtherBoxes.Roi(2, rectangle, M11OtherBoxes.Quadrilateral(0b1001_1000)),
            "quadrilateral B=3 (reserved)" => M11OtherBoxes.Roi(2, rectangle, M11OtherBoxes.Quadrilateral(0b0011_0010)),
            // X[1] = 30 beyond X[3] = 23, Y[1] = 1 before Y[0] = 2
            "quadrilateral out of order" => M11OtherBoxes.Roi(2, rectangle, M11OtherBoxes.Region(1, 0b0000_0010, 128, 30, 1, 2, 50)),
            "quadrilateral without rectangle" => M11OtherBoxes.Roi(1, M11OtherBoxes.Quadrilateral(0b0000_0010)),
            "oriented ellipse" => M11OtherBoxes.Roi(2, ellipse, M11OtherBoxes.Oriented),
            // the touching points far outside XC ± WO and YC ± HO, width and height not 1
            "oriented ellipse outside the bounds" => M11OtherBoxes.Roi(2, ellipse, M11OtherBoxes.Region(1, 3, 128, max, max, 7, 0)),
            "oriented ellipse behind a rectangle" => M11OtherBoxes.Roi(2, rectangle, M11OtherBoxes.Oriented),
            "reserved odd type" => M11OtherBoxes.Roi(1, M11OtherBoxes.Region(1, 0x05, 128, 4, 2, 20, 10)),
            "reserved type 0xFF" => M11OtherBoxes.Roi(1, M11OtherBoxes.Region(1, 0xFF, 128, 4, 2, 20, 10)),
            "priority 0" => M11OtherBoxes.Roi(1, M11OtherBoxes.Region(1, 0, 0, 4, 2, 20, 10)),
            "priority 255" => M11OtherBoxes.Roi(1, M11OtherBoxes.Region(1, 0, 255, 4, 2, 20, 10)),
            "location 0" => M11OtherBoxes.Roi(2, M11OtherBoxes.Region(1, 0, 128, 0, 0, 20, 10), M11OtherBoxes.Region(1, 1, 128, 0, 0, 10, 6)),
            "location 2^32-1" => M11OtherBoxes.Roi(2, M11OtherBoxes.Region(1, 0, 128, max, max, 20, 10), M11OtherBoxes.Region(1, 1, 128, max, max, 10, 6)),
            "size 1" => M11OtherBoxes.Roi(2, M11OtherBoxes.Region(1, 0, 128, 4, 2, 1, 1), M11OtherBoxes.Region(1, 1, 128, 20, 13, 1, 1)),
            "size 2^32-1" => M11OtherBoxes.Roi(2, M11OtherBoxes.Region(1, 0, 128, 4, 2, max, max), M11OtherBoxes.Region(1, 1, 128, 20, 13, max, max)),
            "size 0" => M11OtherBoxes.Roi(2, M11OtherBoxes.Region(1, 0, 128, 4, 2, 0, 0), M11OtherBoxes.Region(1, 1, 128, 20, 13, 0, 0)),
            "all fields lowest" => M11OtherBoxes.Roi(1, M11OtherBoxes.Region(0, 0, 0, 0, 0, 1, 1)),
            "all fields highest, 255 regions" => M11OtherBoxes.Roi(255, Enumerable.Repeat(M11OtherBoxes.Region(255, 255, 255, max, max, max, max), 255).ToArray()),
            "count 255, one region" => M11OtherBoxes.Roi(255, rectangle),
            "count 1, no region" => M11OtherBoxes.Roi(1),
            "entry cut short" => M11OtherBoxes.Roi(2, rectangle, rectangle[..9]),
            _ => Box("roid"),
        };
        var expected = M11OtherBoxes.Reference();
        foreach (var place in new[] { "top", "jp2h", "jpch" })
            M11OtherBoxes.AssertSameImage(expected, M11OtherBoxes.Placed(place, roi), $"{kind} {place}");
    }

    /// <summary>
    /// T.801 M.11.17, Tables M.50 to M.52: Styp (8 bits: 0 to 5, the rest reserved), Ptyp (8 bits: 0 a range by OFF and LEN, 1 without them), OFF
    /// and LEN (64 bits each, LEN 0 up to the end of the file), DATA as Styp says. Signatures of every type over the codestream that do not
    /// match it, LEN 0, no range, a Ptyp outside the table, a range beyond the file, a box of only Styp, OFF cut short and an empty box —
    /// behind and before the codestream and in the JP2 Header box: nothing is checked, no range is read, the image stays complete as it is.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("type 0")]
    [InlineData("type 1")]
    [InlineData("type 2")]
    [InlineData("type 3")]
    [InlineData("type 4")]
    [InlineData("type 5")]
    [InlineData("type 6 (reserved)")]
    [InlineData("type 255 (reserved)")]
    [InlineData("LEN 0: up to the end")]
    [InlineData("Ptyp 1: no range")]
    [InlineData("Ptyp outside Table M.51")]
    [InlineData("range beyond the file")]
    [InlineData("offset beyond the file")]
    [InlineData("only Styp")]
    [InlineData("OFF cut short")]
    [InlineData("empty")]
    public void DigitalSignature_NeverChecked_EveryTypeAndRange(string kind)
    {
        var cs = Rgb.Value;
        var plain = Jpx(cs, [Enumerated(16)]);
        ulong offset = (ulong)(plain.Length - cs.Length), length = (ulong)cs.Length;
        var wrong = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        // a ContentInfo of the CMS (SEQUENCE { OID 1.2.840.113549.1.7.5 digestedData }) with nothing behind it
        byte[] contentInfo = [0x30, 0x0B, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x05];
        var signature = kind switch
        {
            "type 0" => M11OtherBoxes.Signature(0, 0, offset, length, wrong[..16]),
            "type 1" => M11OtherBoxes.Signature(1, 0, offset, length, wrong[..20]),
            "type 2" => M11OtherBoxes.Signature(2, 0, offset, length, wrong),
            "type 3" => M11OtherBoxes.Signature(3, 0, offset, length, wrong),
            "type 4" => M11OtherBoxes.Signature(4, 0, offset, length, wrong),
            "type 5" => M11OtherBoxes.Signature(5, 0, offset, length, contentInfo),
            "type 6 (reserved)" => M11OtherBoxes.Signature(6, 0, offset, length, wrong),
            "type 255 (reserved)" => M11OtherBoxes.Signature(255, 0, offset, length, wrong),
            "LEN 0: up to the end" => M11OtherBoxes.Signature(0, 0, offset, 0, wrong[..16]),
            "Ptyp 1: no range" => M11OtherBoxes.Signature(0, 1, null, null, wrong[..16]),
            "Ptyp outside Table M.51" => M11OtherBoxes.Signature(0, 2, offset, length, wrong[..16]),
            "range beyond the file" => M11OtherBoxes.Signature(0, 0, ulong.MaxValue, ulong.MaxValue, wrong[..16]),
            "offset beyond the file" => M11OtherBoxes.Signature(0, 0, (ulong)plain.Length + 1000, 10, wrong[..16]),
            "only Styp" => Box("chck", [0]),
            "OFF cut short" => Box("chck", [0, 0, 0, 0, 0, 1]),
            _ => Box("chck"),
        };
        var expected = M11OtherBoxes.Reference();
        foreach (var place in new[] { "behind the codestream", "top", "jp2h" })
            M11OtherBoxes.AssertSameImage(expected, M11OtherBoxes.Placed(place, signature), $"{kind} {place}");
    }

    /// <summary>
    /// T.801 M.11.21 and M.11.22: a Compositing Layer Extensions box (a superbox) holds its Info box (Mjclx, Cjclx, Ljclx, Tjclx, Fjclx, LIFE-START),
    /// codestream headers, groups of compositing layer headers with their instruction sets, and metadata; it multiplies them Mjclx times into
    /// further codestreams, layers and presentation threads. Extensions with a codestream header holding a palette and a layer header holding
    /// sYCC — one of each, counts that disagree with the contents, the last box with Mjclx 0, Fjclx 0, two threads and the highest LIFE-START, a
    /// codestream registration and a number list, an Info box cut short, none, an empty box — both alone behind the JP2 Header box and behind
    /// the composition and the top-level headers as the standard asks: the image stays the first compositing layer's, without palette, sRGB.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("one of each")]
    [InlineData("counts disagree")]
    [InlineData("last box, Mjclx 0, two threads")]
    [InlineData("registration and number list")]
    [InlineData("Info box cut short")]
    [InlineData("no Info box")]
    [InlineData("empty")]
    public void CompositingLayerExtensions_NotRead_FirstLayerStays(string kind)
    {
        var cs = Rgb.Value;
        var codestreamHeader = Box("jpch", M11OtherBoxes.PaletteAndMapping);
        var layerHeader = Box("jplh", Box("cgrp", Enumerated(18)));
        var registration = Box("creg", U16(1), U16(1), U16(0), [1, 1, 0, 0]);
        var extensions = kind switch
        {
            "one of each" => Box("jclx", M11OtherBoxes.ExtensionsInfo(1, 1, 1, 1, 1, 0), codestreamHeader, layerHeader, M11OtherBoxes.Instructions,
                M11OtherBoxes.Label("Ablauf")),
            "counts disagree" => Box("jclx", M11OtherBoxes.ExtensionsInfo(3, 5, 7, 2, 9, 0), codestreamHeader, layerHeader, M11OtherBoxes.Instructions),
            "last box, Mjclx 0, two threads" => Box("jclx", M11OtherBoxes.ExtensionsInfo(0, 1, 2, 2, 0, uint.MaxValue), codestreamHeader,
                layerHeader, M11OtherBoxes.Instructions, layerHeader, M11OtherBoxes.Instructions),
            "registration and number list" => Box("jclx", M11OtherBoxes.ExtensionsInfo(1, 1, 1, 1, 1, 0), codestreamHeader,
                Box("jplh", registration, Box("cgrp", Enumerated(18))), M11OtherBoxes.Instructions,
                M11OtherBoxes.Association(M11OtherBoxes.NumberList(0x02000001, 0x01000001), M11OtherBoxes.Label("Teil"))),
            "Info box cut short" => Box("jclx", Box("jlxi", [0, 0, 0, 1, 0, 0]), codestreamHeader, layerHeader),
            "no Info box" => Box("jclx", codestreamHeader, layerHeader),
            _ => Box("jclx"),
        };
        // alone: behind the JP2 Header box, no other compositing layer, no composition
        M11OtherBoxes.AssertSameImage(M11OtherBoxes.Reference(), Jpx(cs, [Enumerated(16)], extensions), $"{kind} alone");
        // as M.11.21 asks: the composition and at least one top-level codestream header and layer header come first
        byte[][] before = [M11OtherBoxes.Composition, Box("jpch"), Box("jplh", Box("cgrp", Enumerated(16)))];
        var withoutExtensions = JpxDecoder.Decode(Jpx(cs, [Enumerated(16)], before), long.MaxValue);
        M11OtherBoxes.AssertSameImage(withoutExtensions, Jpx(cs, [Enumerated(16)], [.. before, extensions]), $"{kind} as required");
        // what the extensions hold would change the image if they were the first headers
        var expected = M11OtherBoxes.Reference();
        Assert.NotEqual(expected.Samples, JpxDecoder.Decode(Jpx(cs, [Enumerated(16)], codestreamHeader), long.MaxValue).Samples);
        Assert.NotEqual(expected.Samples, JpxDecoder.Decode(Jpx(cs, [Enumerated(16)], layerHeader), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 M.11.23: a Multiple Codestream box holds its Info box first, then Contiguous Codestream, Fragment Table or further Multiple Codestream
    /// boxes, then Media Data and Free boxes (Table M.53). Bundled greyscale codestreams — contiguous, nested, in fragments with their media data,
    /// with a Free box, an Info box cut short — behind the top-level codestream as the standard asks and before it (not as it asks): the image is
    /// always the top-level codestream's.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("contiguous")]
    [InlineData("nested")]
    [InlineData("fragments and media data")]
    [InlineData("free box behind")]
    [InlineData("Info box cut short")]
    public void MultipleCodestreams_BundledOnesNeverShown(string kind)
    {
        var cs = Rgb.Value;
        var gray = Gray.Value;
        var expected = M11OtherBoxes.Reference();
        byte[] Bundle(int at) => kind switch
        {
            "contiguous" => Box("j2cx", M11OtherBoxes.MultipleInfo(1, 0), Box("jp2c", gray)),
            "nested" => Box("j2cx", M11OtherBoxes.MultipleInfo(2, 0), Box("j2cx", M11OtherBoxes.MultipleInfo(1, 0), Box("jp2c", gray)), Box("jp2c", gray)),
            // j2cx header 8, Info box 16, Fragment Table box 8 + list 8 + 2 + 14, Media Data box header 8: then the greyscale codestream
            "fragments and media data" => Box("j2cx", M11OtherBoxes.MultipleInfo(1, 0),
                Box("ftbl", Box("flst", U16(1), M11OtherBoxes.U64((ulong)(at + 8 + 16 + 32 + 8)), U32((uint)gray.Length), U16(0))), Box("mdat", gray)),
            "free box behind" => Box("j2cx", M11OtherBoxes.MultipleInfo(1, 0), Box("jp2c", gray), Box("free", new byte[100])),
            _ => Box("j2cx", Box("j2ci", [0, 0, 1]), Box("jp2c", gray)),
        };
        var plain = Jpx(cs, [Enumerated(16)]);
        M11OtherBoxes.AssertSameImage(expected, [.. plain, .. Bundle(plain.Length)], $"{kind} behind");
        var head = Jpx(cs, [Enumerated(16)])[..^(cs.Length + 8)];
        M11OtherBoxes.AssertSameImage(expected, Jpx(cs, [Enumerated(16)], Bundle(head.Length)), $"{kind} before");
        // the bundled codestream on its own would give another image
        Assert.Equal(1, JpxDecoder.Decode(Jpx(gray, [Enumerated(17)]), long.MaxValue).Channels);
        // with the fragments the bundled codestream is complete: the same table at the top level gives the greyscale image
        if (kind == "fragments and media data")
        {
            var bundle = Bundle(head.Length);
            var table = bundle[(8 + 16)..(8 + 16 + 32)];
            var shown = JpxDecoder.Decode([.. head, .. bundle, .. table], long.MaxValue);
            Assert.Equal(1, shown.Channels);
        }
    }

    /// <summary>
    /// T.801 M.11.23 sentence 8: at least one codestream lies outside every Multiple Codestream box. A file whose only codestream is bundled shows
    /// no image — the baseline reader takes the codestream of the top level only.
    /// </summary>
    [TestPdfsFact]
    public void MultipleCodestreams_NoCodestreamOutside_NoImage()
    {
        var cs = Rgb.Value;
        var head = Jpx(cs, [Enumerated(16)])[..^(cs.Length + 8)];
        Assert.Throws<JpxException>(() => JpxDecoder.Decode([.. head, .. Box("j2cx", M11OtherBoxes.MultipleInfo(1, 0), Box("jp2c", cs))], long.MaxValue));
    }

    /// <summary>
    /// T.801 M.11.24, Table M.54: Ncs (32 bits, 1 to 2^32 − 1) and Ltbl (32 bits, 0 to 2^32 − 1; R · 2^26 + L). The ends of both ranges, R 63
    /// with a small L, and Ncs 0 outside the table, behind and before the top-level codestream: no count is trusted, nothing is allocated by
    /// it, the image is the top-level codestream's.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(1u, 0u)]
    [InlineData(uint.MaxValue, 0u)]
    [InlineData(1u, uint.MaxValue)]
    [InlineData(uint.MaxValue, uint.MaxValue)]
    [InlineData(2u, (63u << 26) + 100)]
    [InlineData(0u, 0u)]
    public void MultipleCodestreamInfo_TableRange_PassedOver(uint count, uint table)
    {
        var cs = Rgb.Value;
        var bundle = Box("j2cx", M11OtherBoxes.MultipleInfo(count, table), Box("jp2c", Gray.Value), Box("jp2c", Gray.Value));
        var expected = M11OtherBoxes.Reference();
        M11OtherBoxes.AssertSameImage(expected, [.. Jpx(cs, [Enumerated(16)]), .. bundle], $"{count} {table} behind");
        M11OtherBoxes.AssertSameImage(expected, Jpx(cs, [Enumerated(16)], bundle), $"{count} {table} before");
    }

    /// <summary>
    /// T.801 M.11.26 and M.11.27: an XML document decomposed through Association boxes — the Decomposed XML box (LID, ID, front matter) first, then
    /// Association boxes led by an XML Header box (LID, ID, opening tag) and XML boxes. The decomposition with both spellings of the box types
    /// (the four letters and the hexadecimal values of the standard differ), the two boxes on their own outside an association, LID larger
    /// than the box, empty boxes, and a decomposition nested 5 000 deep — at the top level, in the JP2 Header box and in the Codestream Header
    /// box: never put together, the image stays as it is, no depth is followed.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("decomposed")]
    [InlineData("decomposed, hexadecimal types")]
    [InlineData("front matter alone")]
    [InlineData("opening tag alone")]
    [InlineData("LID larger than the box")]
    [InlineData("empty boxes")]
    [InlineData("nested 5000 deep")]
    public void DecomposedXml_NeverPutTogether(string kind)
    {
        const string front = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";
        byte[] Decomposed(string d, string h) => M11OtherBoxes.Association(
            M11OtherBoxes.XmlPart(d, 3, "doc", front),
            M11OtherBoxes.Association(M11OtherBoxes.XmlPart(h, 2, "e1", "<Bild autor=\"Max Mustermann\">"), Box("xml ", "<Teil/>"u8.ToArray())),
            Box("xml ", "<Rest/>"u8.ToArray()));
        byte[] Nested(int depth)
        {
            var inner = Box("xml ", "<Kern/>"u8.ToArray());
            var tag = M11OtherBoxes.XmlPart("hxml", 1, "e", "<e>");
            for (var i = 0; i < depth; i++) inner = [.. U32((uint)(8 + tag.Length + inner.Length)), .. "asoc"u8, .. tag, .. inner];
            return M11OtherBoxes.Association(M11OtherBoxes.XmlPart("dxml", 1, "d", front), inner);
        }
        var box = kind switch
        {
            "decomposed" => Decomposed("dxml", "hxml"),
            "decomposed, hexadecimal types" => Decomposed("xmld", "xmlh"),
            "front matter alone" => M11OtherBoxes.XmlPart("dxml", 3, "doc", front),
            "opening tag alone" => M11OtherBoxes.XmlPart("hxml", 2, "e1", "<Bild>"),
            "LID larger than the box" => M11OtherBoxes.Association(M11OtherBoxes.XmlPart("dxml", 0xFFFF, "d", front),
                M11OtherBoxes.Association(M11OtherBoxes.XmlPart("hxml", 0xFFFF, "e", "<e>"), Box("xml ", "<x/>"u8.ToArray()))),
            "empty boxes" => M11OtherBoxes.Association(Box("dxml"), M11OtherBoxes.Association(Box("hxml"))),
            _ => Nested(5000),
        };
        var expected = M11OtherBoxes.Reference();
        foreach (var place in new[] { "top", "jp2h", "jpch" })
            M11OtherBoxes.AssertSameImage(expected, M11OtherBoxes.Placed(place, box), $"{kind} {place}");
    }
}
