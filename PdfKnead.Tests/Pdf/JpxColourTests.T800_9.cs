using System.Text;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

public sealed partial class JpxColourTests
{
    /// <summary>The boxes of a JP2 file one by one (ITU-T T.800 I.5), around the codestreams of <see cref="JpxTests.AnnexG"/>.</summary>
    internal static class Jp2HeaderBoxes
    {
        /// <summary>Signature box, file type box with brand „jp2 “ and <paramref name="compatibility"/>, JP2 Header box with
        /// <paramref name="header"/>, Contiguous Codestream box.</summary>
        internal static byte[] File(byte[] codestream, byte[][] header, string compatibility = "jp2 ") =>
        [
            .. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
            .. Box("ftyp", Encoding.ASCII.GetBytes("jp2 "), U32(0), Encoding.ASCII.GetBytes(compatibility)),
            .. Box("jp2h", header),
            .. Box("jp2c", codestream),
        ];

        /// <summary>An Image Header box (I.5.3.1, Table I.5): HEIGHT, WIDTH, NC, BPC, C, UnkC, IPR.</summary>
        internal static byte[] Ihdr(uint height, uint width, int components, int bpc, int compression = 7, int unknown = 0, int ipr = 0) =>
            Box("ihdr", U32(height), U32(width), U16(components), [(byte)bpc, (byte)compression, (byte)unknown, (byte)ipr]);

        /// <summary>A Capture or Display Resolution box (I.5.3.7.1, I.5.3.7.2): VR N, VR D, HR N, HR D, VR E, HR E.</summary>
        internal static byte[] Resolution(string type, int vn, int vd, int ve, int hn, int hd, int he) =>
            Box(type, U16(vn), U16(vd), U16(hn), U16(hd), [(byte)(sbyte)ve, (byte)(sbyte)he]);

        /// <summary>The three-component test image of <see cref="AnnexI.Ycc"/> (3 × 2 pixels, 8 bits).</summary>
        internal static byte[] Three() => JpxTests.AnnexG.Lossless8(3, 2, AnnexI.Ycc);

        /// <summary>A JP2 file around a codestream with the given minimal header, for the image information (only the boxes matter).</summary>
        internal static byte[] WithResolution(params byte[][] resolution) =>
            File(Three(), [Ihdr(2, 3, 3, 7), Enumerated(16), Box("res ", resolution)]);
    }

    // ---- ITU-T T.800 I.5.1 signature box

    /// <summary>
    /// T.800 I.5.1 sentences 7 to 10: the twelve bytes of the signature box find damage in transmission — CR LF turned into LF, an LF turned
    /// into CR LF (the first or the last one), bit 7 of 0x87 cleared. Each of them gives no image („keine JP2-Datei“); the file as it was gives
    /// the image of its codestream.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("crlf-to-lf")]
    [InlineData("first-lf-to-crlf")]
    [InlineData("last-lf-to-crlf")]
    [InlineData("bit7-cleared")]
    public void SignatureBox_TransmissionDamage_NoImage(string damage)
    {
        var cs = JpxTests.AnnexG.Lossless8(2, 1, [10, 200]);
        var file = Jp2HeaderBoxes.File(cs, [Enumerated(17)]);
        Assert.Equal(new byte[] { 0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A }, file[..12]);
        byte[] damaged = damage switch
        {
            "crlf-to-lf" => [.. file[..8], 0x0A, .. file[10..]],
            "first-lf-to-crlf" => [.. file[..9], 0x0D, 0x0A, .. file[10..]],
            "last-lf-to-crlf" => [.. file[..11], 0x0D, 0x0A, .. file[12..]],
            "bit7-cleared" => [.. file[..10], 0x07, .. file[11..]],
            _ => file,
        };
        if (damage == "none")
        {
            Assert.Equal(new byte[] { 10, 200 }, JpxDecoder.Decode(damaged, long.MaxValue).Samples);
            return;
        }
        Assert.Contains("keine JP2-Datei", Assert.Throws<JpxException>(() => JpxDecoder.Decode(damaged, long.MaxValue)).Message);
    }

    // ---- I.5.2 file type box

    /// <summary>
    /// T.800 I.5.2 sentences 32 and 33: „J2P0“ or „J2P1“ in the compatibility list say that the first codestream keeps profile 0 or 1 — the
    /// reader decodes every codestream the same way: the list with the profile codes gives the image the list „jp2 “ gives.
    /// </summary>
    [Theory]
    [InlineData("jp2 J2P0")]
    [InlineData("jp2 J2P1")]
    [InlineData("J2P1jp2 ")]
    public void FileType_ProfileCodesInTheCompatibilityList_SameImage(string list)
    {
        var cs = Jp2HeaderBoxes.Three();
        var plain = JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Enumerated(16)]), long.MaxValue).Samples;
        Assert.Equal(AnnexI.Interleaved(AnnexI.Ycc), plain);
        Assert.Equal(plain, JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Enumerated(16)], list), long.MaxValue).Samples);
    }

    // ---- I.5.3.1 image header box

    /// <summary>
    /// T.800 I.5.3.1 sentences 7, 49, 56 and 61 with sentence 8: an image header that contradicts the first codestream — other height, width,
    /// number of components, bit depth — or carries reserved values (C other than 7, UnkC or IPR other than 0 and 1) makes a file that does not
    /// conform; the reader takes size, components and depth from the codestream: always the image of 3 × 2 pixels and three channels it holds.
    /// </summary>
    [Theory]
    [InlineData(2u, 3u, 3, 7, 7, 0, 0)]
    [InlineData(99u, 3u, 3, 7, 7, 0, 0)]
    [InlineData(2u, 1000u, 3, 7, 7, 0, 0)]
    [InlineData(2u, 3u, 4, 7, 7, 0, 0)]
    [InlineData(2u, 3u, 1, 7, 7, 0, 0)]
    [InlineData(2u, 3u, 3, 15, 7, 0, 0)]
    [InlineData(2u, 3u, 3, 0x87, 7, 0, 0)]
    [InlineData(2u, 3u, 3, 255, 7, 0, 0)]
    [InlineData(2u, 3u, 3, 7, 0, 0, 0)]
    [InlineData(2u, 3u, 3, 7, 7, 2, 0)]
    [InlineData(2u, 3u, 3, 7, 7, 0, 2)]
    [InlineData(2u, 3u, 3, 7, 7, 0, 1)]
    public void ImageHeader_ContradictingOrReserved_TheCodestreamCounts(uint height, uint width, int components, int bpc, int compression, int unknown, int ipr)
    {
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(Jp2HeaderBoxes.Three(), [Jp2HeaderBoxes.Ihdr(height, width, components, bpc, compression, unknown, ipr), Enumerated(16)]),
            long.MaxValue);
        Assert.Equal((3, 2, 3), (image.Width, image.Height, image.Channels));
        Assert.Equal(AnnexI.Interleaved(AnnexI.Ycc), image.Samples);
    }

    /// <summary>
    /// T.800 I.5.3.1 sentences 50 to 54: UnkC 1 says the colour space is not known for certain — the colour specification still applies
    /// (sentence 55): with sYCC the colours are converted to RGB just as with UnkC 0.
    /// </summary>
    [Fact]
    public void ImageHeader_ColourSpaceUnknownFlag_TheSpecificationStillApplies()
    {
        var cs = Jp2HeaderBoxes.Three();
        var known = JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Jp2HeaderBoxes.Ihdr(2, 3, 3, 7), Enumerated(18)]), long.MaxValue);
        var unknown = JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Jp2HeaderBoxes.Ihdr(2, 3, 3, 7, unknown: 1), Enumerated(18)]), long.MaxValue);
        NearSycc(AnnexI.Interleaved(AnnexI.Ycc), known);
        Assert.Equal(known.Color, unknown.Color);
        Assert.Equal(known.Samples, unknown.Samples);
    }

    /// <summary>
    /// T.800 I.5.3.1 sentences 19 and 29, I.5.3.7.2 sentences 1 and 2: grid points need not be square, the Resolution box gives their aspect
    /// and the size meant for display — in PDF the matrix sets the size (ISO 32000-2, 8.9.4), so a Resolution box with different vertical and
    /// horizontal values (capture, display or both) leaves width, height and samples as they are.
    /// </summary>
    [Theory]
    [InlineData("resc")]
    [InlineData("resd")]
    [InlineData("both")]
    public void ResolutionBox_NotSquare_ImageUnchanged(string which)
    {
        var cs = Jp2HeaderBoxes.Three();
        var capture = Jp2HeaderBoxes.Resolution("resc", 1000, 1, 0, 2000, 1, 0);
        var display = Jp2HeaderBoxes.Resolution("resd", 3000, 1, 0, 1500, 1, 0);
        byte[][] inner = which switch { "resc" => [capture], "resd" => [display], _ => [capture, display] };
        var plain = JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Enumerated(16)]), long.MaxValue);
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Enumerated(16), Box("res ", inner)]), long.MaxValue);
        Assert.Equal((plain.Width, plain.Height, plain.Channels), (image.Width, image.Height, image.Channels));
        Assert.Equal(plain.Samples, image.Samples);
    }

    // ---- I.5.3.2 bits per component box

    /// <summary>
    /// T.800 I.5.3.2 sentences 1, 3 and 4: the box gives the depth of each component in the order of the codestream; the reader takes the
    /// depths from SIZ — a box that contradicts it (16 bits, 8 bits signed, 4 bits for three components of 8 bits unsigned) leaves the image.
    /// </summary>
    [Fact]
    public void BitsPerComponentBox_ContradictingTheCodestream_TheCodestreamCounts()
    {
        var cs = Jp2HeaderBoxes.Three();
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Jp2HeaderBoxes.Ihdr(2, 3, 3, 255), Box("bpcc", [15, 0x87, 3]), Enumerated(16)]), long.MaxValue);
        Assert.Equal(3, image.Channels);
        Assert.Equal(AnnexI.Interleaved(AnnexI.Ycc), image.Samples);
    }

    /// <summary>
    /// T.800 I.5.3.2 sentences 23 to 30 (Table I.8): the values of the box — 1 and 38 bits, with the sign bit, and the reserved values 38 to 127
    /// with and without it — are not used by the reader; each leaves the image.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(37)]
    [InlineData(0x80)]
    [InlineData(0x87)]
    [InlineData(0xA5)]
    [InlineData(38)]
    [InlineData(127)]
    [InlineData(0xFF)]
    public void BitsPerComponentBox_EveryValueOfTableI8_ImageUnchanged(int value)
    {
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(Jp2HeaderBoxes.Three(), [Jp2HeaderBoxes.Ihdr(2, 3, 3, 255), Box("bpcc", [(byte)value, (byte)value, (byte)value]),
            Enumerated(16)]), long.MaxValue);
        Assert.Equal(AnnexI.Interleaved(AnnexI.Ycc), image.Samples);
    }

    // ---- I.5.3.3 colour specification box

    /// <summary>
    /// T.800 I.5.3.3 sentences 13 and 14: a signed channel maps to d = Lzero + (Lmax − Lzero)·x / (2^(BPC AND 0x7F) − 1), Lzero the value of
    /// no radiance — for sRGB 0, so x = 0 is black, 127 is 255, negative values lie below the range. Before the decoder showed signed values
    /// moved by half their range, 0 as 128 (docs/FEHLERGEDAECHTNIS.md, No. 1066).
    /// </summary>
    [Fact]
    public void SignedChannels_EnumeratedSpace_FromLzero()
    {
        int[][] samples = [[-20, 0, 127, 64], [127, -128, 10, 0], [0, 50, -1, 127]];
        var cs = JpxTests.AnnexG.Lossless(4, 1, [(8, true), (8, true), (8, true)], samples);
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Enumerated(16)]), long.MaxValue);
        byte[] expected = [.. Enumerable.Range(0, 4).SelectMany(i => samples.Select(c => (byte)Math.Clamp(Math.Round(255.0 * c[i] / 127), 0, 255)))];
        Near(expected, image.Samples);
    }

    /// <summary>
    /// T.800 I.5.3.3 sentences 44, 45 and 47: PREC is a signed byte — 0xFF is −1, below 0: the specification with precedence 0 (sRGB) counts
    /// before the one with 0xFF (sYCC) that stands first, as the JPX baseline orders them (ISO 32000-2, 7.4.9). Read as 255 the sYCC would
    /// count; with precedence 1 it does.
    /// </summary>
    [Fact]
    public void Precedence_ASignedByte_InTheJp2Header()
    {
        var cs = Jp2HeaderBoxes.Three();
        var plain = AnnexI.Interleaved(AnnexI.Ycc);
        Assert.Equal(plain, JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Enumerated(18, -1), Enumerated(16, 0)]), long.MaxValue).Samples);
        NearSycc(plain, JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Enumerated(18, 1), Enumerated(16, 0)]), long.MaxValue));
    }

    /// <summary>
    /// T.800 I.5.3.3 sentences 62 to 69 and 71 (Table I.10, value 17): greyscale with the sRGB curve, d = x / (2^(BPC + 1) − 1) between
    /// Lmin 0 and Lmax 1 — the samples of 1, 4, 8, 12 and 16 bits brought linearly to 8 bits (0 to 255; within one step, the decoder keeps the
    /// upper eight bits), the curve staying in the values as in sRGB.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    public void Greyscale_LinearToEightBits(int depth)
    {
        var top = (1 << depth) - 1;
        int[] samples = [0, top, top / 3, top * 2 / 3, Math.Max(0, top - 1), 1];
        var cs = JpxTests.AnnexG.Lossless(samples.Length, 1, [(depth, false)], [samples]);
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Enumerated(17)]), long.MaxValue);
        Assert.Equal(JpxColor.Gray, image.Color);
        Near([.. samples.Select(x => (byte)Math.Round(255.0 * x / top))], image.Samples);
        Assert.Equal(0, image.Samples[0]);
        Assert.Equal(255, image.Samples[1]);
    }

    /// <summary>
    /// T.800 I.5.3.3 sentences 70 and 77: other values of EnumCS are reserved — one the reader does not know is never chosen, the image is
    /// read as without a colour specification (by the number of channels, unconverted). 2 is reserved in ISO/IEC 15444-2 (Table M.25) as
    /// well; the values that Part defines (YCbCr, CMY, YCCK) are applied in a JP2 file too.
    /// </summary>
    [Theory]
    [InlineData(99u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(2u)]
    public void ReservedEnumCs_AsWithoutColourSpecification(uint space)
    {
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(Jp2HeaderBoxes.Three(), [Enumerated(space)]), long.MaxValue);
        Assert.Equal(JpxColor.Unknown, image.Color);
        Assert.Equal(AnnexI.Interleaved(AnnexI.Ycc), image.Samples);
    }

    // ---- I.5.3.4 palette box

    /// <summary>
    /// T.800 I.5.3.4 sentences 29 to 31 (Table I.12) at the limits of the valid values: NE 1024 (a 10-bit index reaches the last entry,
    /// entry j holding (37·j + 11) mod 256), NE 1, and NPC 255 (three of the 255 columns mapped).
    /// </summary>
    [Fact]
    public void Palette_EntriesAndColumns_AtTheLimitsOfTableI12()
    {
        int[] index = [0, 1, 512, 1022, 1023];
        var tenBits = JpxTests.AnnexG.Lossless(index.Length, 1, [(10, false)], [index]);
        static byte Entry(int j) => (byte)((37 * j + 11) % 256);
        byte[] many = [4, 0, 1, 7, .. Enumerable.Range(0, 1024).Select(Entry)];
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(tenBits, [Enumerated(17), Box("pclr", many), Box("cmap", [0, 0, 1, 0])]), long.MaxValue);
        Assert.Equal([.. index.Select(Entry)], image.Samples);

        var eightBits = JpxTests.AnnexG.Lossless8(2, 1, [0, 0]);
        image = JpxDecoder.Decode(Jp2HeaderBoxes.File(eightBits, [Enumerated(16), Box("pclr", [0, 1, 3, 7, 7, 7, 9, 99, 199]),
            Box("cmap", [0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 2])]), long.MaxValue);
        Assert.Equal(new byte[] { 9, 99, 199, 9, 99, 199 }, image.Samples);

        var index01 = JpxTests.AnnexG.Lossless8(2, 1, [0, 1]);
        byte[] wide = [0, 2, 255, .. Enumerable.Repeat((byte)7, 255), .. Enumerable.Range(0, 255).Select(k => (byte)k), .. Enumerable.Range(0, 255).Select(k => (byte)(255 - k))];
        image = JpxDecoder.Decode(Jp2HeaderBoxes.File(index01, [Enumerated(16), Box("pclr", wide), Box("cmap", [0, 0, 1, 0, 0, 0, 1, 127, 0, 0, 1, 254])]),
            long.MaxValue);
        Assert.Equal(new byte[] { 0, 127, 254, 255, 128, 1 }, image.Samples);
    }

    /// <summary>
    /// T.800 I.5.3.4 sentences 29 to 31 (Table I.12) beyond the valid values: the reader goes as far as the data allow — NE 1025 is read as
    /// 1024 entries (a 10-bit index reaches entry 1023 as with NE 1024), NE 0 or NPC 0 is no palette: the index component is shown as grey.
    /// </summary>
    [Fact]
    public void Palette_OutsideTableI12_ReadAsFarAsItGoes()
    {
        int[] index = [0, 1, 1023];
        var tenBits = JpxTests.AnnexG.Lossless(index.Length, 1, [(10, false)], [index]);
        static byte Entry(int j) => (byte)((37 * j + 11) % 256);
        byte[] more = [4, 1, 1, 7, .. Enumerable.Range(0, 1025).Select(Entry)];
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(tenBits, [Enumerated(17), Box("pclr", more), Box("cmap", [0, 0, 1, 0])]), long.MaxValue);
        Assert.Equal([.. index.Select(Entry)], image.Samples);

        var eightBits = JpxTests.AnnexG.Lossless8(3, 1, [0, 100, 255]);
        foreach (var none in new[] { new byte[] { 0, 0, 1, 7 }, new byte[] { 0, 2, 0 } })
        {
            var grey = JpxDecoder.Decode(Jp2HeaderBoxes.File(eightBits, [Enumerated(17), Box("pclr", none), Box("cmap", [0, 0, 1, 0])]), long.MaxValue);
            Assert.Equal(new byte[] { 0, 100, 255 }, grey.Samples);
        }
    }

    /// <summary>
    /// T.800 I.5.3.4 sentences 35 to 41 (Table I.13): a column of 1 bit (Bi 0, one byte per entry) and one of 38 bits (Bi 37, five bytes per
    /// entry, the upper 16 bits kept) — 0 and 1 become 0 and 255, 0, 2^37 and 2^38 − 1 become 0, 128 and 255.
    /// </summary>
    [Fact]
    public void Palette_ColumnsOfOneAnd38Bits()
    {
        var cs = JpxTests.AnnexG.Lossless8(3, 1, [0, 1, 2]);
        byte[] palette =
        [
            0, 3, 2, 0, 37,
            0, 0, 0, 0, 0, 0,
            1, 0x3F, 0xFF, 0xFF, 0xFF, 0xFF,
            1, 0x20, 0, 0, 0, 0,
        ];
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Box("pclr", palette), Box("cmap", [0, 0, 1, 0, 0, 0, 1, 1])]), long.MaxValue);
        Assert.Equal(2, image.Channels);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 128 }, image.Samples);
    }

    // ---- I.5.3.5 component mapping box

    /// <summary>
    /// T.800 I.5.3.5 sentences 11 and 12: CMP names the component a channel comes from — here component 1 of three, through the palette:
    /// its values 2, 0, 1 pick the entries, components 0 and 2 play no part.
    /// </summary>
    [Fact]
    public void ComponentMapping_TheComponentNamedByCmp()
    {
        var cs = JpxTests.AnnexG.Lossless8(3, 1, [0, 0, 0], [2, 0, 1], [1, 1, 1]);
        byte[] palette = [0, 3, 3, 7, 7, 7, 10, 20, 30, 40, 50, 60, 70, 80, 90];
        byte[] mapping = [0, 1, 1, 0, 0, 1, 1, 1, 0, 1, 1, 2];
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Enumerated(16), Box("pclr", palette), Box("cmap", mapping)]), long.MaxValue);
        Assert.Equal(new byte[] { 70, 80, 90, 10, 20, 30, 40, 50, 60 }, image.Samples);
    }

    /// <summary>
    /// T.800 I.5.3.5 sentences 13 to 26 (Table I.14): MTYP 0 maps a component directly to its channel, MTYP 1 through the palette column
    /// PCOL — here channel 0 is component 1 as it is, channels 1 and 2 are columns 0 and 1 for the index in component 0.
    /// </summary>
    [Fact]
    public void ComponentMapping_DirectBesideThePalette()
    {
        var cs = JpxTests.AnnexG.Lossless8(3, 1, [0, 1, 2], [200, 100, 7]);
        byte[] palette = [0, 3, 2, 7, 7, 10, 20, 30, 40, 50, 60];
        byte[] mapping = [0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1];
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Enumerated(16), Box("pclr", palette), Box("cmap", mapping)]), long.MaxValue);
        Assert.Equal(new byte[] { 200, 10, 20, 100, 30, 40, 7, 50, 60 }, image.Samples);
    }

    // ---- I.5.3.6 channel definition box

    /// <summary>
    /// T.800 I.5.3.6 sentences 31 to 38 (Equation I-5): type 2 is premultiplied opacity, Sp = S·α / max — the image says so, and drawn the
    /// colour is Sp·255/α: 64, 32, 100 at α 128 become 127, 63, 199; α 255 leaves the colour, α 0 is transparent. With type 1 the colour is
    /// drawn as it is.
    /// </summary>
    [Fact]
    public void ChannelDefinition_PremultipliedOpacity_ColourDividedWhenDrawn()
    {
        var cs = JpxTests.AnnexG.Lossless8(3, 1, [64, 255, 0], [32, 100, 0], [100, 50, 0], [128, 255, 0]);
        byte[] Definition(int type) => [0, 4, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 2, 0, 2, 0, 0, 0, 3, 0, 3, 0, (byte)type, 0, 0];
        var data = Jp2HeaderBoxes.File(cs, [Enumerated(16), Box("cdef", Definition(2))]);
        var image = JpxDecoder.Decode(data, long.MaxValue);
        Assert.True(image.PremultipliedAlpha);
        Assert.Equal(new byte[] { 64, 32, 100, 255, 100, 50, 0, 0, 0 }, image.Samples);
        Assert.Equal(new byte[] { 128, 255, 0 }, image.Alpha);

        byte[] Drawn(byte[] jpx)
        {
            var pdf = new TestPdf();
            var number = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(3)),
                ("Height", PdfInteger.Of(1)), ("Filter", TestPdf.N("JPXDecode")), ("SMaskInData", PdfInteger.Of(1))), jpx));
            pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Pages")),
                ("Kids", new PdfArray()), ("Count", PdfInteger.Of(0))))))));
            using var doc = PdfDocument.Open(pdf.Build());
            var decoded = PdfImageDecoder.Decode(new ImageSource((PdfStream)doc.GetObject(number), null, null, false, 0), doc)!;
            Assert.Equal(ImagePixels.Rgba, decoded.Format);
            return decoded.Pixels[..12];
        }
        Assert.Equal(new byte[] { 127, 63, 199, 128, 255, 100, 50, 255, 0, 0, 0, 0 }, Drawn(data));
        Assert.Equal(new byte[] { 64, 32, 100, 128, 255, 100, 50, 255, 0, 0, 0, 0 }, Drawn(Jp2HeaderBoxes.File(cs, [Enumerated(16), Box("cdef", Definition(1))])));
    }

    /// <summary>
    /// T.800 I.5.3.6 sentences 39 and 40 (Table I.16): types 3 to 2^16 − 2 are reserved, 2^16 − 1 says the type is not given — such a channel
    /// is neither colour nor opacity: of three channels with colours 1 and 2 on the first two, the image has those two colour channels and no
    /// opacity.
    /// </summary>
    [Theory]
    [InlineData(65535)]
    [InlineData(3)]
    [InlineData(65534)]
    public void ChannelDefinition_UnspecifiedOrReservedType_NeitherColourNorOpacity(int type)
    {
        var (a, b, c) = (AnnexI.Ycc[0], AnnexI.Ycc[1], AnnexI.Ycc[2]);
        byte[] definition = [0, 3, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 2, 0, 2, (byte)(type >> 8), (byte)type, 0, 0];
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(Jp2HeaderBoxes.Three(), [Box("cdef", definition)]), long.MaxValue);
        Assert.Equal(2, image.Channels);
        Assert.Null(image.Alpha);
        Assert.Equal(AnnexI.Interleaved(a, b), image.Samples);
        Assert.NotEqual(AnnexI.Interleaved(a, c), image.Samples);
    }

    /// <summary>
    /// T.800 I.5.3.6 sentences 69 to 72 (Table I.18, YCbCr): Y is colour 1, Cb 2, Cr 3 — stored in another order with a channel definition
    /// that names them, sYCC gives the same image as Y, Cb, Cr in order without one.
    /// </summary>
    [Theory]
    [InlineData(3, 2, 1)]
    [InlineData(2, 3, 1)]
    [InlineData(1, 3, 2)]
    public void ChannelDefinition_Ycc_ByAssociation(int first, int second, int third)
    {
        int[] associations = [first, second, third];
        int[][] stored = [.. associations.Select(a => AnnexI.Ycc[a - 1])];
        byte[] definition = [0, 3, .. Enumerable.Range(0, 3).SelectMany(i => (byte[])[0, (byte)i, 0, 0, 0, (byte)associations[i]])];
        var inOrder = JpxDecoder.Decode(Jp2HeaderBoxes.File(Jp2HeaderBoxes.Three(), [Enumerated(18)]), long.MaxValue);
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(JpxTests.AnnexG.Lossless8(3, 2, stored), [Enumerated(18), Box("cdef", definition)]), long.MaxValue);
        Assert.Equal(inOrder.Samples, image.Samples);
    }

    /// <summary>
    /// T.800 I.5.3.6 sentences 78 to 81 and 100 to 104 (Table I.18, Lab and CMYK — the spaces CIELab and CMYK of the JPX baseline): stored as
    /// b, a, L or K, Y, M, C with a channel definition giving their colour numbers, the image is the one of L, a, b or C, M, Y, K in order.
    /// </summary>
    [Fact]
    public void ChannelDefinition_LabAndCmyk_ByAssociation()
    {
        var (l, a, b) = (AnnexI.Ycc[0], AnnexI.Ycc[1], AnnexI.Ycc[2]);
        byte[] reversed3 = [0, 3, 0, 0, 0, 0, 0, 3, 0, 1, 0, 0, 0, 2, 0, 2, 0, 0, 0, 1];
        var lab = JpxDecoder.Decode(Jp2HeaderBoxes.File(JpxTests.AnnexG.Lossless8(3, 2, l, a, b), [Enumerated(14)]), long.MaxValue);
        var labReversed = JpxDecoder.Decode(Jp2HeaderBoxes.File(JpxTests.AnnexG.Lossless8(3, 2, b, a, l), [Enumerated(14), Box("cdef", reversed3)]), long.MaxValue);
        Assert.Equal(JpxColor.Lab, labReversed.Color);
        Assert.Equal(lab.Samples, labReversed.Samples);

        int[] k = [0, 50, 100, 150, 200, 250];
        byte[] reversed4 = [0, 4, 0, 0, 0, 0, 0, 4, 0, 1, 0, 0, 0, 3, 0, 2, 0, 0, 0, 2, 0, 3, 0, 0, 0, 1];
        var cmyk = JpxDecoder.Decode(Jp2HeaderBoxes.File(JpxTests.AnnexG.Lossless8(3, 2, l, a, b, k), [Enumerated(12)]), long.MaxValue);
        var cmykReversed = JpxDecoder.Decode(Jp2HeaderBoxes.File(JpxTests.AnnexG.Lossless8(3, 2, k, b, a, l), [Enumerated(12), Box("cdef", reversed4)]), long.MaxValue);
        Assert.Equal(JpxColor.Cmyk, cmykReversed.Color);
        Assert.Equal(AnnexI.Interleaved(l, a, b, k), cmykReversed.Samples);
        Assert.Equal(cmyk.Samples, cmykReversed.Samples);
    }

    /// <summary>
    /// T.800 I.5.3.6 sentences 73 to 77 and 82 to 88 (Table I.18, classes listed for future use): an enumerated space the decoder does not know
    /// (EnumCS 9, PhotoYCC) is not applied — the image goes by the number of channels — but the channels still follow the channel definition:
    /// colour 1 first.
    /// </summary>
    [Fact]
    public void ChannelDefinition_SpaceNotKnown_ChannelsStillByAssociation()
    {
        var (a, b, c) = (AnnexI.Ycc[0], AnnexI.Ycc[1], AnnexI.Ycc[2]);
        byte[] definition = [0, 3, 0, 0, 0, 0, 0, 2, 0, 1, 0, 0, 0, 3, 0, 2, 0, 0, 0, 1];
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(Jp2HeaderBoxes.Three(), [Enumerated(9), Box("cdef", definition)]), long.MaxValue);
        Assert.Equal(JpxColor.Unknown, image.Color);
        Assert.Equal(AnnexI.Interleaved(c, a, b), image.Samples);
    }

    /// <summary>
    /// T.800 I.5.3.6 sentences 89 to 99 and 105 to 113 (Table I.18, further classes listed for future use): CIEJab (19, which ISO 32000-2,
    /// 7.4.9 leaves out) is not applied — the image is the one without a colour specification. CMY (11), defined in ISO/IEC 15444-2, is
    /// applied since 23.11.2026 (JpxColourTests.EnumeratedCmy_Inverted).
    /// </summary>
    [Theory]
    [InlineData(19u)]
    public void FutureClasses_Jab_AsWithoutColourSpecification(uint space)
    {
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(Jp2HeaderBoxes.Three(), [Enumerated(space)]), long.MaxValue);
        Assert.Equal(JpxColor.Unknown, image.Color);
        Assert.Equal(AnnexI.Interleaved(AnnexI.Ycc), image.Samples);
    }

    // ---- I.5.3.7 resolution boxes, as the image information reads them

    /// <summary>
    /// T.800 I.5.3.7.1 sentences 12 to 14, 18 and 19 (Equations I-4, I-5) and I.5.3.7.2 sentences 20 to 26 (I-6, I-7): the resolution is
    /// N / D · 10^E grid points per metre, vertically and horizontally — denominators and exponents other than 1 and 0, an exponent of −1
    /// (0xFF), and the two directions different; shown in dots per inch (· 0.0254). The display resolution counts only without a capture
    /// resolution.
    /// </summary>
    [Theory]
    [InlineData("resc", 11811, 10, 1, 59055, 1, -1, 149.9997, 299.9994)]
    [InlineData("resd", 11811, 10, 1, 59055, 1, -1, 149.9997, 299.9994)]
    [InlineData("resc", 59055, 1, -1, 11811, 10, 1, 299.9994, 149.9997)]
    [InlineData("resd", 59055, 1, -1, 11811, 10, 1, 299.9994, 149.9997)]
    [InlineData("resc", 300, 3, 2, 2, 1, 3, 50.8, 254)]
    [InlineData("resd", 300, 3, 2, 2, 1, 3, 50.8, 254)]
    public void Resolution_FractionTimesAPowerOfTen(string type, int vn, int vd, int ve, int hn, int hd, int he, double x, double y)
    {
        var dpi = ImageMetadata.FromJpx(Jp2HeaderBoxes.WithResolution(Jp2HeaderBoxes.Resolution(type, vn, vd, ve, hn, hd, he)))!.StoredDpi!.Value;
        Assert.Equal(x, dpi.X, 0.001);
        Assert.Equal(y, dpi.Y, 0.001);
    }

    /// <summary>
    /// T.800 I.5.3.7.1 sentences 23 to 42 (Figure I.15) and I.5.3.7.2 sentences 30 to 53 (Figure I.16): the fields in their order VR N, VR D,
    /// HR N, HR D (two bytes each), VR E, HR E (one signed byte each) — each with another value, so that any two swapped give another result:
    /// vertically 1000 / 2 · 10^1 = 5000 per metre (127 dpi), horizontally 3000 / 5 · 10^−1 = 60 per metre (1.524 dpi).
    /// </summary>
    [Theory]
    [InlineData("resc")]
    [InlineData("resd")]
    public void Resolution_EachFieldInItsPlace(string type)
    {
        var dpi = ImageMetadata.FromJpx(Jp2HeaderBoxes.WithResolution(Jp2HeaderBoxes.Resolution(type, 1000, 2, 1, 3000, 5, -1)))!.StoredDpi!.Value;
        Assert.Equal(1.524, dpi.X, 0.0001);
        Assert.Equal(127, dpi.Y, 0.0001);
    }

    /// <summary>
    /// T.800 I.5.3.7.1 sentences 43 to 45 (Table I.20) and I.5.3.7.2 sentences 57 to 59 (Table I.21): numerators and denominators 1 to 65 535,
    /// exponents −128 to 127 — at the limits no exception; 65 535 / 65 535 · 10^2 is 100 per metre (2.54 dpi), 65 535 / 1 is 1664.589 dpi, and
    /// what lies outside any plausible resolution (10^127, 10^−128, 1 / 65 535 per metre) is left out of the image information.
    /// </summary>
    [Theory]
    [InlineData("resc", 65535, 65535, 2, 2.54)]
    [InlineData("resd", 65535, 65535, 2, 2.54)]
    [InlineData("resc", 65535, 1, 0, 1664.589)]
    [InlineData("resd", 65535, 1, 0, 1664.589)]
    [InlineData("resc", 1, 1, 127, double.NaN)]
    [InlineData("resd", 1, 1, 127, double.NaN)]
    [InlineData("resc", 65535, 1, -128, double.NaN)]
    [InlineData("resd", 65535, 1, -128, double.NaN)]
    [InlineData("resc", 1, 65535, 0, double.NaN)]
    public void Resolution_LimitsOfTheFields(string type, int n, int d, int e, double expected)
    {
        var meta = ImageMetadata.FromJpx(Jp2HeaderBoxes.WithResolution(Jp2HeaderBoxes.Resolution(type, n, d, e, n, d, e)));
        Assert.NotNull(meta);
        if (double.IsNaN(expected))
        {
            Assert.Null(meta.StoredDpi);
            return;
        }
        Assert.Equal(expected, meta.StoredDpi!.Value.X, 0.001);
        Assert.Equal(expected, meta.StoredDpi!.Value.Y, 0.001);
    }
}
