using System.Text;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics.Icc;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ITU-T T.801 M.11 head, M.11.1 to M.11.6 and M.11.8 to M.11.10 sentence by sentence: the boxes of a JPX file the reader passes over
/// (Table M.13), data references and fragment lists, cross-references in a codestream header, the place of the JP2 Header box, the image
/// header against the codestream, the Codestream Header boxes and the Composition box. The codestreams come from small files of the public
/// test collection; the boxes around them are built here, each checked against what the codestream alone gives.
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>Box builders of the tests on the boxes of M.11 (own names: other partial files of the class have helpers of their own).</summary>
    private static class M11Boxes
    {
        internal static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

        internal static byte[] Signature => Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]);

        internal static byte[] FileType => Box("ftyp", Ascii("jpx "), U32(0), Ascii("jpx jp2 jpxb"));

        /// <summary>A Fragment List box (Table M.17): NF, then per fragment OFF (8 bytes), LEN (4 bytes) and DR (2 bytes).</summary>
        internal static byte[] Flst(params (long Offset, uint Length, ushort Reference)[] fragments) =>
            Box("flst", [U16(fragments.Length),
                .. fragments.Select(f => (byte[])[.. U32((uint)(f.Offset >> 32)), .. U32((uint)f.Offset), .. U32(f.Length), .. U16(f.Reference)])]);

        /// <summary>An Image Header box (Table M.21): HEIGHT, WIDTH, NC, BPC, C, UnkC 0, IPR 0.</summary>
        internal static byte[] Ihdr(uint height, uint width, int components, byte depth, byte compression = 7) =>
            Box("ihdr", U32(height), U32(width), U16(components), [depth, compression, 0, 0]);

        /// <summary>A Data Entry URL box (T.800 I.7.3.2): version 0, flags 0, the location with its terminating zero.</summary>
        internal static byte[] Url(string location) => Box("url ", [0, 0, 0, 0], Ascii(location), [0]);

        /// <summary>A palette of one entry of three 8-bit columns: 10, 20, 30 (NE 1, NPC 3, B 7, 7, 7).</summary>
        internal static byte[] Palette => [0, 1, 3, 7, 7, 7, 10, 20, 30];

        /// <summary>Three channels from component 0 through palette columns 0, 1, 2 (CMP, MTYP 1, PCOL).</summary>
        internal static byte[] Mapping => [0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 2];

        /// <summary>A Channel Definition box naming the three channels the colours 3, 2, 1: blue, green, red.</summary>
        internal static byte[] Reversed => Box("cdef", [0, 3, 0, 0, 0, 0, 0, 3, 0, 1, 0, 0, 0, 2, 0, 2, 0, 0, 0, 1]);

        /// <summary>
        /// An Instruction Set box (Table M.39) with one instruction: Ityp, REPT, TICK, then the parameters Ityp names in the order of Figure
        /// M.27 — XO, YO (bit 0); WIDTH, HEIGHT (bit 1); PERSIST with LIFE, NEXT-USE (bit 3); XC, YC, WC, HC (bit 5); ROT (bit 6).
        /// </summary>
        internal static byte[] Inst(int ityp, int rept, uint tick, uint life, uint rot)
        {
            var parameters = new List<byte>();
            if ((ityp & 0x01) != 0) parameters.AddRange([.. U32(5), .. U32(3)]);
            if ((ityp & 0x02) != 0) parameters.AddRange([.. U32(80), .. U32(54)]);
            if ((ityp & 0x08) != 0) parameters.AddRange([.. U32(0x80000000u | life), .. U32(1)]);
            if ((ityp & 0x20) != 0) parameters.AddRange([.. U32(4), .. U32(2), .. U32(20), .. U32(10)]);
            if ((ityp & 0x40) != 0) parameters.AddRange(U32(rot));
            return Box("inst", U16(ityp), U16(rept), U32(tick), [.. parameters]);
        }
    }

    /// <summary>
    /// T.801 M.11, Table M.13: the boxes the JPX baseline does not use — the Codestream Registration box of the first compositing layer, the
    /// Data Reference box, the Composition box, the Binary Filter box, the Desired Reproductions box with a Graphics Technology Standard Output
    /// box, the ROI Description box, the Digital Signature box, a Compositing Layer Extensions box (its layer comes after the layers of the top
    /// level, here sYCC), a Multiple Codestream box (its Codestream Header box belongs to a later codestream, here with the channels reversed),
    /// the Decomposed XML box and the XML Header box (by the names of Table M.13 and by the codes M.11.26 and M.11.27 give for them) — are
    /// passed over: the image is the one of the sRGB codestream alone.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("creg")]
    [InlineData("dtbl")]
    [InlineData("comp")]
    [InlineData("bfil")]
    [InlineData("drep")]
    [InlineData("roid")]
    [InlineData("chck")]
    [InlineData("jclx")]
    [InlineData("j2cx")]
    [InlineData("dxml")]
    [InlineData("hxml")]
    [InlineData("xmld")]
    [InlineData("xmlh")]
    public void BoxesTheBaselineDoesNotUse_PassedOver_ImageUnchanged(string type)
    {
        var cs = Rgb.Value;
        var filter = new byte[16];
        filter[0] = 0x42;
        var box = type switch
        {
            // XS, YS 2; codestream 0 with XR, YR 2 and XO, YO 1 (Table M.35)
            "creg" => Box("jplh", Box("cgrp", Enumerated(16)), Box("creg", U16(2), U16(2), U16(0), [2, 2, 1, 1])),
            "dtbl" => Box("dtbl", U16(1), M11Boxes.Url("https://example.org/teil.j2c")),
            "comp" => Box("comp", Box("copt", U32(27), U32(40), [0]), M11Boxes.Inst(0x03, 0, 0, 0, 0)),
            "bfil" => Box("bfil", filter, [1, 2, 3, 4]),
            "drep" => Box("drep", Box("gtso", CalibratedProfile.Srgb())),
            // Nroi 1: in the codestream, rectangular, priority 1, at 0, 0, 8 × 8
            "roid" => Box("roid", [1, 1, 0, 1], U32(0), U32(0), U32(8), U32(8)),
            // Styp 0, Ptyp 1 (no offset and length), 16 bytes of data
            "chck" => Box("chck", [0, 1], new byte[16]),
            // Mjclx 1, Cjclx 0, Ljclx 1, Tjclx 0; one compositing layer with sYCC
            "jclx" => Box("jclx", Box("jlxi", U32(1), U32(0), U32(1), U32(0)), Box("jplh", Box("cgrp", Enumerated(18)))),
            "j2cx" => Box("j2cx", Box("j2ci", U32(1), U32(0)), Box("jpch", M11Boxes.Reversed)),
            _ => Box(type, U16(4), M11Boxes.Ascii("doc1"), M11Boxes.Ascii("<Bild/>")),
        };
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(16)], box), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Assert.Equal(Plain(cs), image.Samples);
    }

    /// <summary>
    /// T.801 M.11.2, M.11.3.1 (field DR): a Data Reference box with one URL box is passed over; a fragment list whose second fragment names
    /// that entry (DR 1, a file elsewhere) gives no codestream — the reader does not follow it (JPX baseline, M.9.2.5) —, with DR 0 (this file)
    /// the two fragments make up the codestream.
    /// </summary>
    [TestPdfsFact]
    public void DataReferenceBox_PassedOver_AFragmentInAnotherFileNotFollowed()
    {
        var cs = Rgb.Value;
        var half = cs.Length / 2;
        byte[] Build(ushort reference)
        {
            byte[] head =
            [
                .. M11Boxes.Signature, .. M11Boxes.FileType, .. Box("jp2h", Enumerated(16)),
                .. Box("dtbl", U16(1), M11Boxes.Url("https://example.org/teil.j2c")),
            ];
            var first = head.Length + 8;
            var second = first + half + 8;
            return
            [
                .. head, .. Box("mdat", cs[..half]), .. Box("mdat", cs[half..]),
                .. Box("ftbl", M11Boxes.Flst((first, (uint)half, 0), (second, (uint)(cs.Length - half), reference))),
            ];
        }
        Assert.Equal(Plain(cs), JpxDecoder.Decode(Build(0), long.MaxValue).Samples);
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Build(1), long.MaxValue));
    }

    /// <summary>
    /// T.801 M.11.3.1, Table M.17 (NF 0 to 65 535, LEN 0 to 2^32 − 1): a fragment list without fragments (NF 0) gives no codestream, and that
    /// first codestream counts — a Contiguous Codestream box behind it does not take its place (M.9.2.2); a fragment of length 0 between the
    /// two halves of the codestream adds nothing and the codestream is put together as without it.
    /// </summary>
    [TestPdfsFact]
    public void FragmentList_NoFragmentsOrAnEmptyOne()
    {
        var cs = Rgb.Value;
        var half = cs.Length / 2;
        byte[] head = [.. M11Boxes.Signature, .. M11Boxes.FileType, .. Box("jp2h", Enumerated(16))];
        Assert.Throws<JpxException>(() => JpxDecoder.Decode([.. head, .. Box("ftbl", M11Boxes.Flst()), .. Box("jp2c", cs)], long.MaxValue));

        var first = head.Length + 8;
        var second = first + half + 8;
        byte[] file =
        [
            .. head, .. Box("mdat", cs[..half]), .. Box("mdat", cs[half..]),
            .. Box("ftbl", M11Boxes.Flst((first, (uint)half, 0), (12, 0, 0), (second, (uint)(cs.Length - half), 0))),
        ];
        Assert.Equal(Plain(cs), JpxDecoder.Decode(file, long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 M.11.4: a Cross-Reference box in the Codestream Header box of the first codestream stands for the box its fragments make up —
    /// here the palette (Rtyp „pclr“), its contents kept in a Media Data box; with the Component Mapping box beside it every pixel of the grey
    /// codestream (all 0) takes the colour of entry 0.
    /// </summary>
    [TestPdfsFact]
    public void CrossReference_PaletteOfTheCodestreamHeader()
    {
        var cs = Gray.Value;
        var palette = M11Boxes.Palette;
        byte[] With(long offset) => Jpx(cs, [Enumerated(16)], Box("mdat", palette),
            Box("jpch", Box("cref", M11Boxes.Ascii("pclr"), M11Boxes.Flst((offset, (uint)palette.Length, 0))), Box("cmap", M11Boxes.Mapping)));
        var file = With(With(0).AsSpan().IndexOf("mdat"u8) + 4);
        var image = JpxDecoder.Decode(file, long.MaxValue);
        Assert.Equal(3, image.Channels);
        for (var i = 0; i < image.Samples.Length; i += 3) Assert.Equal(new byte[] { 10, 20, 30 }, image.Samples[i..(i + 3)]);
    }

    /// <summary>
    /// T.801 M.11.5: the JP2 Header box may stand anywhere at the top level — behind the Contiguous Codestream box its sYCC counts as in front
    /// of it.
    /// </summary>
    [TestPdfsFact]
    public void Jp2HeaderBox_BehindTheCodestream_CountsTheSame()
    {
        var cs = Rgb.Value;
        var front = JpxDecoder.Decode(Jpx(cs, [Enumerated(18)]), long.MaxValue).Samples;
        Assert.NotEqual(Plain(cs), front);
        byte[] behind = [.. M11Boxes.Signature, .. M11Boxes.FileType, .. Box("jp2c", cs), .. Box("jp2h", Enumerated(18))];
        Assert.Equal(front, JpxDecoder.Decode(behind, long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 M.11.5.1 (HEIGHT, WIDTH, NC as in JP2): an image header that gives other sizes and another number of components than the SIZ of
    /// the codestream (5 × 7, one component of 16 bits) — the image is the codestream's: 40 × 27, three channels, its samples.
    /// </summary>
    [TestPdfsFact]
    public void ImageHeader_OtherSizesThanTheCodestream_TheCodestreamCounts()
    {
        var cs = Rgb.Value;
        var image = JpxDecoder.Decode(Jpx(cs, [M11Boxes.Ihdr(5, 7, 1, 15), Enumerated(16)]), long.MaxValue);
        Assert.Equal((40, 27, 3), (image.Width, image.Height, image.Channels));
        Assert.Equal(Plain(cs), image.Samples);
    }

    /// <summary>
    /// T.801 M.11.5.1, Table M.19: the compression type C names the coding of the codestreams; the reader decodes JPEG 2000 (7) only. A file with
    /// C = 5 (JPEG) whose Contiguous Codestream box holds JPEG data gives no image — also where a JPEG 2000 codestream lies in a comment of the
    /// JPEG data: it is not looked for.
    /// </summary>
    [TestPdfsFact]
    public void ImageHeader_CompressionJpeg_NoImage()
    {
        var hidden = Gray.Value;
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xFE, .. U16(hidden.Length + 2), .. hidden, 0xFF, 0xD9];
        var file = Jpx(jpeg, [M11Boxes.Ihdr(64, 128, 1, 7, compression: 5), Enumerated(17)]);
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(file, long.MaxValue));
    }

    /// <summary>
    /// T.801 M.11.6: the Codestream Header boxes of the top level belong to the codestreams in their order — the first to codestream 0, the one
    /// the reader shows. A palette in the second one does not apply (grey, one channel), the palette in the first one does (its colours).
    /// </summary>
    [TestPdfsFact]
    public void CodestreamHeaders_TheFirstBelongsToTheFirstCodestream()
    {
        var cs = Gray.Value;
        var withPalette = Box("jpch", Box("pclr", M11Boxes.Palette), Box("cmap", M11Boxes.Mapping));
        var label = Box("jpch", Box("lbl ", M11Boxes.Ascii("Bild")));
        var grey = JpxDecoder.Decode(Jpx(cs, [Enumerated(17)], label, withPalette), long.MaxValue);
        Assert.Equal(1, grey.Channels);
        Assert.Equal(Plain(cs), grey.Samples);
        var coloured = JpxDecoder.Decode(Jpx(cs, [Enumerated(16)], withPalette, label), long.MaxValue);
        Assert.Equal(3, coloured.Channels);
        for (var i = 0; i < coloured.Samples.Length; i += 3) Assert.Equal(new byte[] { 10, 20, 30 }, coloured.Samples[i..(i + 3)]);
    }

    /// <summary>
    /// T.801 M.11.6 with M.11.21: a Codestream Header box inside a Compositing Layer Extensions box stands for headers of codestreams after
    /// those of the top level — its palette does not apply to codestream 0, the image stays grey.
    /// </summary>
    [TestPdfsFact]
    public void CodestreamHeaderInALayerExtension_NotTheFirstCodestream()
    {
        var cs = Gray.Value;
        // Mjclx 1, Cjclx 1, Ljclx 0, Tjclx 0
        var extension = Box("jclx", Box("jlxi", U32(1), U32(1), U32(0), U32(0)), Box("jpch", Box("pclr", M11Boxes.Palette), Box("cmap", M11Boxes.Mapping)));
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(17)], extension), long.MaxValue);
        Assert.Equal(1, image.Channels);
        Assert.Equal(Plain(cs), image.Samples);
    }

    /// <summary>
    /// T.801 M.11.6 (contents of the Codestream Header box): a label, an image header with other sizes (5 × 7, one component), a Bits Per
    /// Component box and an ROI Description box in the header of the first codestream — sizes and depths come from the codestream, label and
    /// ROI description are metadata: the image is the one without them.
    /// </summary>
    [TestPdfsFact]
    public void CodestreamHeader_LabelSizesDepthsAndRoi_ImageUnchanged()
    {
        var cs = Rgb.Value;
        var header = Box("jpch", Box("lbl ", M11Boxes.Ascii("Erster Datenstrom")), M11Boxes.Ihdr(5, 7, 1, 15), Box("bpcc", [15]),
            Box("roid", [1, 0, 0, 255], U32(2), U32(2), U32(10), U32(10)));
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(16)], header), long.MaxValue);
        Assert.Equal((40, 27, 3), (image.Width, image.Height, image.Channels));
        Assert.Equal(Plain(cs), image.Samples);
    }

    /// <summary>
    /// T.801 M.11.10 to M.11.10.2.1, Tables M.36 to M.40bis: a Composition box with its options (HEIGHT, WIDTH, GREPT 0, 1 or 255) and an
    /// Instruction Set box — each bit of Ityp (location, size, animation with PERSIST, LIFE up to 2^31 − 1 and NEXT-USE, crop, rotation),
    /// REPT up to 65 535 („without end“), TICK, every value of ROT of Table M.40bis and a reserved one. The JPX baseline shows the first
    /// compositing layer (M.9.2.2): the image is the codestream's, at once, without a loop or a wait.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(0x01, 0, 0u, 0, 0u, 0u)]
    [InlineData(0x02, 0, 0u, 0, 0u, 0u)]
    [InlineData(0x08, 0, 100u, 0, 0u, 0u)]
    [InlineData(0x08, 65535, 100u, 255, 0x7FFFFFFFu, 0u)]
    [InlineData(0x20, 0, 0u, 1, 0u, 0u)]
    [InlineData(0x40, 0, 0u, 0, 0u, 0x00u)]
    [InlineData(0x40, 0, 0u, 0, 0u, 0x01u)]
    [InlineData(0x40, 0, 0u, 0, 0u, 0x02u)]
    [InlineData(0x40, 0, 0u, 0, 0u, 0x03u)]
    [InlineData(0x40, 0, 0u, 0, 0u, 0x04u)]
    [InlineData(0x40, 0, 0u, 0, 0u, 0x11u)]
    [InlineData(0x40, 0, 0u, 0, 0u, 0x14u)]
    [InlineData(0x40, 0, 0u, 0, 0u, 0x20u)]
    [InlineData(0x6B, 3, 40u, 1, 25u, 0x12u)]
    public void CompositionBox_PassedOver_FirstLayerShown(int ityp, int rept, uint tick, byte grept, uint life, uint rot)
    {
        var cs = Rgb.Value;
        var composition = Box("comp", Box("copt", U32(54), U32(80), [grept]), M11Boxes.Inst(ityp, rept, tick, life, rot), M11Boxes.Inst(0x01, 0, 0, 0, 0));
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(16)], Box("jplh", Box("cgrp", Enumerated(16))), composition), long.MaxValue);
        Assert.Equal((40, 27), (image.Width, image.Height));
        Assert.Equal(Plain(cs), image.Samples);
    }
}
