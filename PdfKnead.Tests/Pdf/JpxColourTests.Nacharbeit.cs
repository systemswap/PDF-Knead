using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>Findings of the sentence-by-sentence assignment of T.801 (23.11.2026), fixed and tested here.</summary>
public sealed partial class JpxColourTests
{
    /// <summary>Colours of six pixels (R, G, B in 0 … 255) that reach the corners and the middle of the RGB cube.</summary>
    private static readonly int[][] RgbPixels = [[255, 0, 0], [0, 255, 0], [0, 0, 255], [255, 255, 255], [0, 0, 0], [200, 120, 40]];

    /// <summary>R′G′B′ → E′y, E′cb, E′cr with the matrix of ITU-R BT.601 (E′y in 0 … 1, E′cb and E′cr in −0.5 … 0.5).</summary>
    private static (double Y, double Cb, double Cr) Bt601(int[] rgb)
    {
        double r = rgb[0] / 255.0, g = rgb[1] / 255.0, b = rgb[2] / 255.0;
        var y = 0.299 * r + 0.587 * g + 0.114 * b;
        return (y, (b - y) / 1.772, (r - y) / 1.402);
    }

    /// <summary>The components of the six pixels, written by <paramref name="encode"/> from E′y, E′cb, E′cr.</summary>
    private static int[][] Components(Func<(double Y, double Cb, double Cr), int[]> encode)
    {
        var coded = RgbPixels.Select(p => encode(Bt601(p))).ToArray();
        return [.. Enumerable.Range(0, 3).Select(c => coded.Select(p => p[c]).ToArray())];
    }

    /// <summary>
    /// T.801 Table M.25, YCbCr(2) (EnumCS 3): full range — Y in 0 … 255, Cb and Cr in −128 … 127 stored with the offset 128 —, the matrix of
    /// BT.601. Six colours written so are read back as RGB (to one step). Before 23.11.2026 the space was passed over and the image left as Y,
    /// Cb, Cr.
    /// </summary>
    [Fact]
    public void EnumeratedYcbcr2_FullRange_ReadAsRgb()
    {
        var components = Components(e => [(int)Math.Round(255 * e.Y), (int)Math.Round(128 + 255 * e.Cb), (int)Math.Round(128 + 255 * e.Cr)]);
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(JpxTests.AnnexG.Lossless8(3, 2, components), [Enumerated(3)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Near([.. RgbPixels.SelectMany(p => p.Select(v => (byte)v))], image.Samples);
    }

    /// <summary>
    /// T.801 Table M.25, YCbCr(1) and YCbCr(3) (EnumCS 1 and 4): the limited range of BT.601 — at 8 bits Y = 16 + 219·E′y, Cb and Cr =
    /// 128 + 224·E′c; at 10 bits four times that —, the matrix of BT.601. Six colours written so are read back as RGB (to two steps: the
    /// limited range has fewer steps); black is Y 16, not 0. Before 23.11.2026 both spaces were passed over.
    /// </summary>
    [Theory]
    [InlineData(1u, 8)]
    [InlineData(4u, 8)]
    [InlineData(1u, 10)]
    [InlineData(4u, 10)]
    public void EnumeratedYcbcrLimitedRange_ReadAsRgb(uint space, int bits)
    {
        var scale = 1 << (bits - 8);
        var components = Components(e => [(int)Math.Round(scale * (16 + 219 * e.Y)), (int)Math.Round(scale * (128 + 224 * e.Cb)),
            (int)Math.Round(scale * (128 + 224 * e.Cr))]);
        Assert.Equal(16 * scale, components[0][4]);   // black
        var cs = JpxTests.AnnexG.Lossless(3, 2, [(bits, false), (bits, false), (bits, false)], components);
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Enumerated(space)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Near([.. RgbPixels.SelectMany(p => p.Select(v => (byte)v))], image.Samples, 2);
    }

    /// <summary>
    /// T.801 Table M.25, CMY (EnumCS 11): 0 is no ink, 2^BPS − 1 full ink — drawn as RGB, each component inverted. Before 23.11.2026 the space
    /// was passed over and the ink amounts drawn as light.
    /// </summary>
    [Fact]
    public void EnumeratedCmy_Inverted()
    {
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(Jp2HeaderBoxes.Three(), [Enumerated(11)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Assert.Equal([.. AnnexI.Interleaved(AnnexI.Ycc).Select(v => (byte)(255 - v))], image.Samples);
    }

    /// <summary>
    /// T.801 Table M.25, YCCK (EnumCS 13): CMYK data with R = 255 − C, G = 255 − M, B = 255 − Y, turned into YCbCr(2), K unchanged. Six CMYK
    /// colours written so are read back as CMYK (to one step), K exactly. Before 23.11.2026 the space was passed over.
    /// </summary>
    [Fact]
    public void EnumeratedYcck_ReadAsCmyk()
    {
        int[] black = [0, 30, 0, 255, 128, 77];
        var cmy = RgbPixels.Select(p => p.Select(v => 255 - v).ToArray()).ToArray();
        var ycc = Components(e => [(int)Math.Round(255 * e.Y), (int)Math.Round(128 + 255 * e.Cb), (int)Math.Round(128 + 255 * e.Cr)]);
        var cs = JpxTests.AnnexG.Lossless8(3, 2, [.. ycc, black]);
        var image = JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Enumerated(13)]), long.MaxValue);
        Assert.Equal(JpxColor.Cmyk, image.Color);
        Assert.Equal(4, image.Channels);
        Near([.. Enumerable.Range(0, 6).SelectMany(i => new[] { (byte)cmy[i][0], (byte)cmy[i][1], (byte)cmy[i][2], (byte)black[i] })], image.Samples);
        for (var i = 0; i < 6; i++) Assert.Equal(black[i], image.Samples[4 * i + 3]);
    }

    // ---- M.6, M.11.1: Reader Requirements box

    /// <summary>The contents of a Reader Requirements box (Table M.15): ML, FUAM, DCM, the standard features with their masks, then
    /// <paramref name="vendorMasks"/> vendor features (UUIDs of zeros) with theirs; masks big-endian in ML bytes.</summary>
    private static byte[] Rreq(int ml, ulong fuam, ulong dcm, (int Code, ulong Mask)[] standard, params ulong[] vendorMasks)
    {
        byte[] Mask(ulong m) => [.. Enumerable.Range(0, ml).Select(i => (byte)(m >> (8 * (ml - 1 - i))))];
        return [(byte)ml, .. Mask(fuam), .. Mask(dcm), (byte)(standard.Length >> 8), (byte)standard.Length,
            .. standard.SelectMany(f => new[] { (byte)(f.Code >> 8), (byte)f.Code }.Concat(Mask(f.Mask))),
            (byte)(vendorMasks.Length >> 8), (byte)vendorMasks.Length, .. vendorMasks.SelectMany(m => new byte[16].Concat(Mask(m)))];
    }

    private static int[] MissingOf(byte[] contents) => JpxRequirements.Missing(contents, 0, contents.Length);

    /// <summary>
    /// T.801 M.6.3: the reader ORs the masks of the features it provides and compares the result with the Display Contents mask. CMYK (55) and a
    /// Part 1 codestream (5) are provided, compositing (17) is not: the column of compositing stays open — 17 is reported; the metadata
    /// column (34) lies only in the Fully Understand Aspects mask and is not asked for. Without the compositing column nothing is missing.
    /// </summary>
    [Fact]
    public void ReaderRequirements_OpenColumnOfTheDisplayMask_Reported()
    {
        (int, ulong)[] features = [(55, 0x80), (5, 0x40), (17, 0x20), (34, 0x10)];
        Assert.Equal([17], MissingOf(Rreq(1, 0xF0, 0xE0, features)));
        Assert.Empty(MissingOf(Rreq(1, 0xF0, 0xC0, features)));
    }

    /// <summary>
    /// T.801 M.6.2.1: the features of one column are alternatives — compositing (17) beside several layers (2), which PDF-Knead provides,
    /// leaves nothing open; two unprovided ones in the column (17, 25) are both reported, in ascending order; one unprovided in each of two
    /// columns (25 and 7) likewise.
    /// </summary>
    [Fact]
    public void ReaderRequirements_FeaturesOfAColumnAreAlternatives()
    {
        Assert.Empty(MissingOf(Rreq(1, 0x80, 0x80, [(17, 0x80), (2, 0x80)])));
        Assert.Equal([17, 25], MissingOf(Rreq(1, 0x80, 0x80, [(25, 0x80), (17, 0x80)])));
        Assert.Equal([7, 25], MissingOf(Rreq(1, 0xC0, 0xC0, [(25, 0x80), (7, 0x40), (5, 0x00)])));
    }

    /// <summary>
    /// T.801 M.11.1: readers ignore the contribution of deprecated features — a column with only feature 18 or 3 is not counted as missing.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(18)]
    [InlineData(70)]
    public void ReaderRequirements_DeprecatedFeatures_NeverMissing(int code) =>
        Assert.Empty(MissingOf(Rreq(1, 0x80, 0x80, [(code, 0x80)])));

    /// <summary>
    /// T.801 M.11.1: a vendor feature (VF, VM) alone in a column of the display mask is reported as such; one in a column beside a provided
    /// standard feature is not. A column of the display mask no feature covers is reported as unlisted.
    /// </summary>
    [Fact]
    public void ReaderRequirements_VendorFeatureAndUnlistedColumn()
    {
        Assert.Equal([JpxRequirements.Vendor], MissingOf(Rreq(1, 0xC0, 0xC0, [(5, 0x40)], 0x80)));
        Assert.Empty(MissingOf(Rreq(1, 0x80, 0x80, [(5, 0x80)], 0x80)));
        Assert.Equal([JpxRequirements.Unlisted], MissingOf(Rreq(1, 0xC0, 0xC0, [(5, 0x80)])));
    }

    /// <summary>
    /// T.801 Table M.15: ML 1, 2, 4 or 8 bytes per mask — the open column in the most significant byte of each width is found; ML 3 or 0
    /// (reserved) and a box shorter than its counts say give no evaluation.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void ReaderRequirements_MaskLengths(int ml)
    {
        var top = 1UL << (8 * ml - 1);
        Assert.Equal([25], MissingOf(Rreq(ml, top | 1, top | 1, [(25, top), (5, 1)])));
        Assert.Empty(MissingOf(Rreq(ml, 1, 1, [(25, top), (5, 1)])));
    }

    [Fact]
    public void ReaderRequirements_ReservedMaskLengthOrShortBox_NotEvaluated()
    {
        // well-formed with masks of three and of sixteen bytes (reserved lengths): not evaluated
        Assert.Empty(MissingOf(Rreq(3, 0x800000, 0x800000, [(25, 0x800000)])));
        Assert.Empty(MissingOf([16, .. new byte[32], 0, 1, 0, 25, .. new byte[16], 0, 0]));
        Assert.Empty(MissingOf([0, 0, 0, 0, 0, 0]));
        var full = Rreq(1, 0x80, 0x80, [(25, 0x80)]);
        Assert.Equal([25], MissingOf(full));
        for (var cut = 1; cut < full.Length - 2; cut++) Assert.Empty(JpxRequirements.Missing(full, 0, cut));
        // NSF 65 535 in a box of a few bytes: stops at the end without reading past it
        Assert.Empty(MissingOf([1, 0x80, 0x80, 0xFF, 0xFF, 0, 25, 0x80]));
    }

    /// <summary>
    /// T.801 M.6, M.11.1 in a file: a JPX file whose Reader Requirements box asks for animation (25) to display — the image is decoded all
    /// the same (its first codestream, as ISO 32000-2, 7.4.9 has it) and names the missing feature; the description groups it. The box
    /// PDF-Knead writes itself (CMYK with a Part 1 codestream, a Part 2 codestream) asks for nothing it lacks.
    /// </summary>
    [Fact]
    public void ReaderRequirements_InAFile_ImageShown_FeatureNamed()
    {
        var cs = Jp2HeaderBoxes.Three();
        byte[] file = [.. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]), .. Box("ftyp", [.. "jpx "u8, 0, 0, 0, 0, .. "jpx "u8]),
            .. Box("rreq", Rreq(1, 0xC0, 0xC0, [(5, 0x80), (25, 0x40)])), .. Box("jp2h", Enumerated(16)), .. Box("jp2c", cs)];
        var image = JpxDecoder.Decode(file, long.MaxValue);
        Assert.Equal(AnnexI.Interleaved(AnnexI.Ycc), image.Samples);
        Assert.Equal([25], image.MissingFeatures);
        Assert.Equal("Animation", JpxRequirements.Describe(image.MissingFeatures));
        Assert.Equal("Animation, ein JPEG-Codestream, unbekannte Merkmale", JpxRequirements.Describe([25, 28, 7, JpxRequirements.Unlisted]));
        Assert.Empty(JpxDecoder.Decode(Jp2HeaderBoxes.File(cs, [Enumerated(16)]), long.MaxValue).MissingFeatures);
        // T.801 M.7.2.1: a feature code no edition defines (200) and a vendor feature, each alone in a column of the display mask — named
        // („unbekannte Merkmale“, „Herstellermerkmale“), the samples those of the same file without the box
        foreach (var (rreq, missing, words) in new[]
        {
            (Rreq(1, 0xC0, 0xC0, [(5, 0x80), (200, 0x40)]), new[] { 200 }, "unbekannte Merkmale"),
            (Rreq(1, 0xC0, 0xC0, [(5, 0x80)], 0x40), new[] { JpxRequirements.Vendor }, "Herstellermerkmale"),
        })
        {
            byte[] other = [.. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]), .. Box("ftyp", [.. "jpx "u8, 0, 0, 0, 0, .. "jpx "u8]),
                .. Box("rreq", rreq), .. Box("jp2h", Enumerated(16)), .. Box("jp2c", cs)];
            var read = JpxDecoder.Decode(other, long.MaxValue);
            Assert.Equal(missing, read.MissingFeatures);
            Assert.Equal(words, JpxRequirements.Describe(read.MissingFeatures));
            Assert.Equal(AnnexI.Interleaved(AnnexI.Ycc), read.Samples);
        }

        var cmyk = JpxRobustTests.Codestream(0, 64, 64, 4, 1, 0, 1, 0);
        Assert.Empty(JpxDecoder.Decode(Core.Pdf.Writing.PdfAJpeg2000.Wrap(cmyk)!, 16_000_000).MissingFeatures);
        var part2 = JpxRobustTests.Codestream(0, 64, 64, 3, 1, 0, 1, 0);
        part2[6] = 0x80;
        Assert.Empty(JpxDecoder.Decode(Core.Pdf.Writing.PdfAJpeg2000.Wrap(part2)!, 16_000_000).MissingFeatures);
    }

    /// <summary>
    /// T.801 M.6.3: the reader compares the OR of its masks with both expressions (decided on 23.11.2026). Metadata (34) only in the Fully
    /// Understand Aspects mask: nothing missing for the display, „not completely understood: metadata“ for the file; a vendor feature in a
    /// column of both masks counts for both; deprecated features for neither. A damaged box gives neither.
    /// </summary>
    [Fact]
    public void ReaderRequirements_FullyUnderstandMask_ComparedToo()
    {
        var box = Rreq(1, 0xE0, 0xC0, [(5, 0x80), (25, 0x40), (34, 0x20)]);
        Assert.Equal([25], JpxRequirements.Missing(box, 0, box.Length));
        Assert.Equal([25, 34], JpxRequirements.NotUnderstood(box, 0, box.Length));
        var onlyMetadata = Rreq(1, 0xA0, 0x80, [(5, 0x80), (34, 0x20)]);
        Assert.Empty(JpxRequirements.Missing(onlyMetadata, 0, onlyMetadata.Length));
        Assert.Equal([34], JpxRequirements.NotUnderstood(onlyMetadata, 0, onlyMetadata.Length));
        var vendor = Rreq(1, 0xC0, 0xC0, [(5, 0x80)], 0x40);
        Assert.Equal([JpxRequirements.Vendor], JpxRequirements.NotUnderstood(vendor, 0, vendor.Length));
        var deprecated = Rreq(1, 0xC0, 0x80, [(5, 0x80), (18, 0x40)]);
        Assert.Empty(JpxRequirements.NotUnderstood(deprecated, 0, deprecated.Length));
        Assert.Empty(JpxRequirements.NotUnderstood(onlyMetadata, 0, onlyMetadata.Length - 1));
    }

    /// <summary>
    /// The comparison with the Fully Understand Aspects mask reaches the image information, not the problem list: a JPX file whose box asks
    /// for metadata (34) only to understand it — „Nicht vollständig verstanden: Metadaten“ among what the image stores, no missing feature for
    /// the display; without the box no such row.
    /// </summary>
    [Fact]
    public void ReaderRequirements_NotUnderstood_InTheImageInformation()
    {
        var cs = Jp2HeaderBoxes.Three();
        byte[] With(bool rreq) => [.. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]), .. Box("ftyp", [.. "jpx "u8, 0, 0, 0, 0, .. "jpx "u8]),
            .. (rreq ? Box("rreq", Rreq(1, 0xA0, 0x80, [(5, 0x80), (34, 0x20)])) : []), .. Box("jp2h", Enumerated(16)), .. Box("jp2c", cs)];
        var file = With(true);
        Assert.Empty(JpxDecoder.Decode(file, long.MaxValue).MissingFeatures);
        var metadata = Core.Imaging.ImageMetadata.FromJpx(file)!;
        Assert.Equal([34], metadata.JpxNotUnderstood!);
        var text = Core.Pdf.Graphics.ImageInspector.AsText(Core.Pdf.Graphics.ImageInspector.Sections(new Core.Pdf.Graphics.ImageInfo { Embedded = metadata }));
        Assert.Contains("Nicht vollständig verstanden", text);
        Assert.Contains("Metadaten", text);
        Assert.Null(Core.Imaging.ImageMetadata.FromJpx(With(false))!.JpxNotUnderstood);
    }

    /// <summary>
    /// The same file as the image of a page: drawn, and the document names what is missing in its problem list — once, however often the
    /// image is drawn; the same image without the Reader Requirements box adds no problem.
    /// </summary>
    [Fact]
    public void ReaderRequirements_InADocument_ProblemNamed()
    {
        var cs = JpxTests.AnnexG.Lossless8(4, 1, [0, 64, 128, 255]);
        byte[] With(bool rreq) => [.. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]), .. Box("ftyp", [.. "jpx "u8, 0, 0, 0, 0, .. "jpx "u8]),
            .. (rreq ? Box("rreq", Rreq(1, 0xC0, 0xC0, [(5, 0x80), (17, 0x40)])) : []), .. Box("jp2h", Enumerated(17)), .. Box("jp2c", cs)];
        foreach (var rreq in new[] { true, false })
        {
            var pdf = new TestPdf();
            var image = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(4)),
                ("Height", PdfInteger.Of(1)), ("Filter", TestPdf.N("JPXDecode"))), With(rreq)));
            pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Pages")),
                ("Kids", new PdfArray()), ("Count", PdfInteger.Of(0))))))));
            using var doc = PdfDocument.Open(pdf.Build());
            Assert.Equal(new byte[] { 0, 64, 128, 255 }, GrayDrawn(doc, (PdfStream)doc.GetObject(image)));
            GrayDrawn(doc, (PdfStream)doc.GetObject(image));
            var named = doc.Problems.Where(p => p.Contains("JPEG-2000-Bild verlangt", StringComparison.Ordinal)).ToList();
            if (!rreq)
            {
                Assert.Empty(named);
                continue;
            }
            Assert.Equal(["Ein JPEG-2000-Bild verlangt zum Anzeigen etwas, das PDF-Knead nicht kann (das Zusammensetzen mehrerer Ebenen oder Codestreams); "
                + "es wird gezeigt, so weit es sich lesen lässt."], named);
        }
    }
}
