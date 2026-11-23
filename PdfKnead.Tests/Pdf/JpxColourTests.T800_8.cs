using System.Text;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

public sealed partial class JpxColourTests
{
    /// <summary>Files of the JP2 format (ITU-T T.800 I.2 to I.4) around the codestreams of <see cref="JpxTests.AnnexG"/>.</summary>
    internal static class AnnexI
    {
        /// <summary>Signature box, file type box (brand „jp2 “, minor version 0, compatibility „jp2 “), then <paramref name="boxes"/> in order.</summary>
        internal static byte[] Jp2(params byte[][] boxes) =>
        [
            .. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
            .. Box("ftyp", Encoding.ASCII.GetBytes("jp2 "), U32(0), Encoding.ASCII.GetBytes("jp2 ")),
            .. boxes.SelectMany(b => b),
        ];

        /// <summary>A box with LBox 1 and the given XLBox.</summary>
        internal static byte[] Extended(string type, ulong length, byte[] content) =>
            [0, 0, 0, 1, .. Encoding.ASCII.GetBytes(type), .. U32((uint)(length >> 32)), .. U32((uint)length), .. content];

        /// <summary>The samples of a three-component test image (3 × 2 pixels): Y, Cb, Cr values that sYCC turns into clearly other colours.</summary>
        internal static readonly int[][] Ycc =
        [
            [30, 128, 200, 90, 255, 0],
            [128, 60, 200, 100, 128, 240],
            [128, 200, 50, 160, 30, 128],
        ];

        /// <summary>The samples interleaved per pixel, as the decoder gives them.</summary>
        internal static byte[] Interleaved(params int[][] components) =>
            [.. Enumerable.Range(0, components[0].Length).SelectMany(i => components.Select(c => (byte)c[i]))];
    }

    /// <summary>
    /// T.800 I.2.2 sentence 15: the order of the boxes is not fixed in general — a JP2 Header box behind the Contiguous Codestream box (here with
    /// the colour specification sYCC) counts as one in front of it: the colours converted to RGB.
    /// </summary>
    [Fact]
    public void JpHeaderBoxBehindTheCodestream_CountsAllTheSame()
    {
        var cs = JpxTests.AnnexG.Lossless8(3, 2, AnnexI.Ycc);
        var before = JpxDecoder.Decode(AnnexI.Jp2(Box("jp2h", Enumerated(18)), Box("jp2c", cs)), long.MaxValue);
        var behind = JpxDecoder.Decode(AnnexI.Jp2(Box("jp2c", cs), Box("jp2h", Enumerated(18))), long.MaxValue);
        NearSycc(AnnexI.Interleaved(AnnexI.Ycc), before);
        Assert.Equal(before.Color, behind.Color);
        Assert.Equal(before.Samples, behind.Samples);
        Assert.NotEqual(AnnexI.Interleaved(AnnexI.Ycc), behind.Samples);
    }

    /// <summary>
    /// T.800 I.2.3 sentences 10 and 11: the palette is applied before the colour space is interpreted, and the colour specification describes
    /// the values of the palette — an index component through a palette of three sYCC entries gives the RGB of those entries, not the entries.
    /// </summary>
    [Fact]
    public void Palette_AppliedBeforeTheColourSpace()
    {
        int[] index = [0, 1, 2, 1, 0, 2];
        int[][] entries = [[30, 128, 128], [128, 60, 200], [200, 200, 50]];
        var cs = JpxTests.AnnexG.Lossless8(3, 2, index);
        byte[] palette = [0, 3, 3, 7, 7, 7, .. entries.SelectMany(e => e.Select(v => (byte)v))];
        byte[] mapping = [0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 2];
        var image = JpxDecoder.Decode(AnnexI.Jp2(Box("jp2h", Enumerated(18), Box("pclr", palette), Box("cmap", mapping)), Box("jp2c", cs)), long.MaxValue);
        byte[] looked = [.. index.SelectMany(i => entries[i].Select(v => (byte)v))];
        NearSycc(looked, image);
        Assert.NotEqual(looked, image.Samples);
    }

    /// <summary>
    /// T.800 I.2.4 sentence 4: the components are first associated with the colour channels (Channel Definition box), then converted — the
    /// codestream holds Cr, Y, Cb, the channel definition names them colours 3, 1, 2 of sYCC: the result is the RGB of Y, Cb, Cr. Converted
    /// before the association it would be the RGB of Cr, Y, Cb put in another order.
    /// </summary>
    [Fact]
    public void ChannelDefinition_AppliedBeforeTheColourSpace()
    {
        var (y, cb, cr) = (AnnexI.Ycc[0], AnnexI.Ycc[1], AnnexI.Ycc[2]);
        var cs = JpxTests.AnnexG.Lossless8(3, 2, cr, y, cb);
        byte[] definition = [0, 3, 0, 0, 0, 0, 0, 3, 0, 1, 0, 0, 0, 1, 0, 2, 0, 0, 0, 2];
        var image = JpxDecoder.Decode(AnnexI.Jp2(Box("jp2h", Enumerated(18), Box("cdef", definition)), Box("jp2c", cs)), long.MaxValue);
        NearSycc(AnnexI.Interleaved(y, cb, cr), image);
        var wrongOrder = YccToRgb(AnnexI.Interleaved(cr, y, cb));
        byte[] reordered = [.. Enumerable.Range(0, 6).SelectMany(i => new[] { wrongOrder[3 * i + 1], wrongOrder[3 * i + 2], wrongOrder[3 * i] })];
        Assert.NotEqual(reordered, image.Samples);
    }

    /// <summary>
    /// T.800 I.3.2 sentences 8 to 12: a restricted ICC profile may be monochrome — only a grey tone curve. A grey JP2 file with a linear grey
    /// profile (method 2) keeps the profile, and drawn the grey values go through its curve: the same as the samples drawn uncompressed in
    /// /ICCBased with that profile (ISO 32000-2, 8.6.5.5), and close to the sRGB encoding of the linear values, 0, 64, 128, 255 becoming
    /// about 0, 137, 188, 255 — not the samples themselves, as the enumerated greyscale (17) draws them.
    /// </summary>
    [Fact]
    public void MonochromeIccProfile_GreyThroughItsCurve()
    {
        int[] grey = [0, 64, 128, 255];
        var cs = JpxTests.AnnexG.Lossless8(4, 1, grey);
        var profile = CalibratedProfile.Gray(null);
        var data = AnnexI.Jp2(Box("jp2h", Icc(profile)), Box("jp2c", cs));
        var image = JpxDecoder.Decode(data, long.MaxValue);
        Assert.Equal(JpxColor.Icc, image.Color);
        Assert.Equal(profile, image.IccProfile);

        var pdf = new TestPdf();
        PdfStream Xobject(PdfDictionary extra, byte[] content)
        {
            var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(4)), ("Height", PdfInteger.Of(1)));
            foreach (var key in extra.Keys) dict.Set(key, extra.Get(key)!);
            return new PdfStream(dict, content);
        }
        var iccStream = pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(1))), profile));
        var jpx = pdf.Add(Xobject(TestPdf.Dict(("Filter", TestPdf.N("JPXDecode"))), data));
        var greyJpx = pdf.Add(Xobject(TestPdf.Dict(("Filter", TestPdf.N("JPXDecode"))), AnnexI.Jp2(Box("jp2h", Enumerated(17)), Box("jp2c", cs))));
        var raw = pdf.Add(Xobject(TestPdf.Dict(("BitsPerComponent", PdfInteger.Of(8)),
            ("ColorSpace", new PdfArray([TestPdf.N("ICCBased"), TestPdf.Ref(iccStream)]))), [.. grey.Select(v => (byte)v)]));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Pages")),
            ("Kids", new PdfArray()), ("Count", PdfInteger.Of(0))))))));
        using var doc = PdfDocument.Open(pdf.Build());
        byte[] Drawn(int number) => GrayDrawn(doc, (PdfStream)doc.GetObject(number));
        var drawn = Drawn(jpx);
        Assert.Equal(Drawn(raw), drawn);
        Assert.Equal([.. grey.Select(v => (byte)v)], Drawn(greyJpx));
        static double Srgb(double linear) => linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        for (var i = 0; i < grey.Length; i++)
            Assert.True(Math.Abs(Srgb(grey[i] / 255.0) * 255 - drawn[i]) <= 2, $"grey {grey[i]}: drawn {drawn[i]}, expected {Srgb(grey[i] / 255.0) * 255:F1}");
    }

    /// <summary>
    /// T.800 I.4, Table I.1, LBox 8: the smallest box, header only — an empty box at the top level and one inside the JP2 Header box leave
    /// the image as it is, for the decoder, the PDF/A check (it still finds the image header and the three channels behind the empty box) and
    /// the image information (the colour specification behind it).
    /// </summary>
    [Fact]
    public void BoxLengthEight_AnEmptyBox()
    {
        var cs = JpxTests.AnnexG.Lossless8(3, 2, AnnexI.Ycc);
        var plain = Plain(cs);
        var empty = Box("xml ");
        Assert.Equal(8, empty.Length);
        byte[] ihdr = [0, 0, 0, 2, 0, 0, 0, 3, 0, 3, 7, 7, 0, 0];
        var file = AnnexI.Jp2(empty, Box("jp2h", empty, Box("ihdr", ihdr), empty, Enumerated(16)), empty, Box("jp2c", cs));
        Assert.Equal(plain, JpxDecoder.Decode(file, long.MaxValue).Samples);
        Assert.Equal("DeviceRGB", PdfAJpeg2000.DeviceSpace(file));
        Assert.Null(PdfAJpeg2000.Fault(file));
        Assert.Equal(16, ImageMetadata.FromJpx(file)!.JpxColor);
    }

    /// <summary>
    /// T.800 I.4, Table I.1: the length of a box beyond the end of the file — an XLBox (LBox 1) or an LBox larger than what is left — is cut to
    /// the end (a damaged file): the Contiguous Codestream box still gives the image. An XLBox of exactly the box gives it as well.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    public void BoxLength_BeyondTheEndOfTheFile_CutToIt(int beyond)
    {
        var cs = JpxTests.AnnexG.Lossless8(3, 2, AnnexI.Ycc);
        var plain = Plain(cs);
        var header = Box("jp2h", Enumerated(16));
        Assert.Equal(plain, JpxDecoder.Decode(AnnexI.Jp2(header, AnnexI.Extended("jp2c", (ulong)(16 + cs.Length + beyond), cs)), long.MaxValue).Samples);
        byte[] longer = [.. U32((uint)(8 + cs.Length + beyond)), .. "jp2c"u8, .. cs];
        Assert.Equal(plain, JpxDecoder.Decode(AnnexI.Jp2(header, longer), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.800 I.4, Table I.1: XLBox is 16 or more (the box with its 16 bytes of header) — a smaller XLBox is no length a box can have; the
    /// decoder takes the box as damaged, running to the end of the file. In front of the Contiguous Codestream box it covers it: no image.
    /// </summary>
    [Theory]
    [InlineData(15)]
    [InlineData(8)]
    [InlineData(0)]
    public void ExtendedLength_BelowSixteen_TheBoxRunsToTheEnd(int length)
    {
        var cs = JpxTests.AnnexG.Lossless8(3, 2, AnnexI.Ycc);
        var header = Box("jp2h", Enumerated(16));
        Assert.Equal(Plain(cs), JpxDecoder.Decode(AnnexI.Jp2(header, AnnexI.Extended("xml ", 16, []), Box("jp2c", cs)), long.MaxValue).Samples);
        var damaged = AnnexI.Jp2(header, AnnexI.Extended("xml ", (ulong)length, []), Box("jp2c", cs));
        Assert.Contains("Datenstrom", Assert.Throws<JpxException>(() => JpxDecoder.Decode(damaged, long.MaxValue)).Message);
    }
}
