using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>Findings of the probe of the M2 report (T.801 M.6, M.11.1, M.11.7.8; 23.11.2026), fixed and tested here.</summary>
public sealed partial class JpxColourTests
{
    // ---- T.801 Table M.14: every value against what the decoder does

    /// <summary>The words <see cref="JpxRequirements.Describe"/> gives per group of Table M.14.</summary>
    private const string NotUnderstoodFile = "eine nicht vollständig verstandene Datei", JpegStream = "ein JPEG-Codestream",
        Elsewhere = "Teile in anderen Dateien oder im Netz", Compositing = "das Zusammensetzen mehrerer Ebenen oder Codestreams",
        Animation = "Animation", Metadata = "Metadaten", Signatures = "Signaturen oder Prüfsummen", Prepress = "eine Wiedergabe für die Druckvorstufe",
        Colour = "ein Farbraum, den PDF-Knead nicht zeichnet", Extensions = "Erweiterungen des Codestreams", JpegXr = "ein JPEG-XR-Codestream",
        Unknown = "unbekannte Merkmale";

    /// <summary>
    /// Every value of T.801 Table M.14 (0 to 86) and two beyond it, classified here apart from the decoder's list: null where the decoder
    /// provides the feature for the image as PDF draws it (JpxDecoder: the first layer and codestream, the colour spaces of Choose, the Pixel
    /// Format box) or the value is deprecated; otherwise the words of its group.
    /// </summary>
    public static TheoryData<int, string?> TableM14 => new()
    {
        { 0, NotUnderstoodFile }, { 1, null }, { 2, null }, { 3, null }, { 4, null }, { 5, null }, { 6, null }, { 7, JpegStream }, { 8, null },
        { 9, null }, { 10, null }, { 11, null }, { 12, null }, { 13, null }, { 14, null }, { 15, Elsewhere }, { 16, Elsewhere },
        { 17, Compositing }, { 18, null }, { 19, null }, { 20, null }, { 21, Compositing }, { 22, null }, { 23, Compositing }, { 24, null },
        { 25, Animation }, { 26, Animation }, { 27, null }, { 28, Animation }, { 29, null }, { 30, Animation }, { 31, null },
        { 32, Compositing }, { 33, Compositing }, { 34, Metadata }, { 35, Metadata }, { 36, Metadata }, { 37, Metadata }, { 38, Metadata },
        { 39, Signatures }, { 40, Signatures }, { 41, Prepress }, { 42, null }, { 43, null }, { 44, null }, { 45, null }, { 46, null },
        { 47, null }, { 48, null }, { 49, null }, { 50, null }, { 51, null }, { 52, Colour }, { 53, null }, { 54, null }, { 55, null },
        { 56, null }, { 57, null }, { 58, Colour }, { 59, Colour }, { 60, null }, { 61, null }, { 62, null }, { 63, null }, { 64, null },
        { 65, null }, { 66, null }, { 67, Metadata }, { 68, Extensions }, { 69, Extensions }, { 70, null }, { 71, null }, { 72, null },
        { 73, null }, { 74, null }, { 75, JpegXr }, { 76, JpegXr }, { 77, JpegXr }, { 78, JpegXr }, { 79, JpegXr }, { 80, null },
        { 81, null }, { 82, null }, { 83, Colour }, { 84, Extensions }, { 85, Colour }, { 86, Extensions }, { 87, Unknown }, { 65535, Unknown },
    };

    /// <summary>
    /// T.801 M.11.1, Table M.14: each value alone in a column of both masks — what the decoder provides or what is deprecated leaves nothing
    /// open, every other value is missing for the display and for understanding the file, named by its group. Before YPbPr(1125/60) (73) and
    /// YPbPr(1250/50) (74), which the decoder draws (No. 1071, No. 1078), were reported as a colour space PDF-Knead does not draw. scRGB (83)
    /// counts as missing here, where nothing says the image is drawn as scRGB (see <see cref="ReaderRequirements_ScRgb_ProvidedWhereDrawn"/>).
    /// </summary>
    [Theory]
    [MemberData(nameof(TableM14))]
    public void ReaderRequirements_EveryValueOfTableM14(int code, string? group)
    {
        var box = Rreq(1, 0x80, 0x80, [(code, 0x80)]);
        int[] expected = group is null ? [] : [code];
        Assert.Equal(expected, JpxRequirements.Missing(box, 0, box.Length));
        Assert.Equal(expected, JpxRequirements.NotUnderstood(box, 0, box.Length));
        if (group is not null) Assert.Equal(group, JpxRequirements.Describe([code]));
    }

    /// <summary>
    /// T.801 Table M.14, values 73 and 74 in a file: a JPX file in YPbPr(1250/50) (the codes of YPbPr1250_PrimariesAndCurveOfBt709_LumaOfItem42)
    /// whose Reader Requirements box asks for 74 (or 73) to display — drawn as RGB, nothing missing; the same box asking for PhotoYCC (52), which the decoder does not read, still names it.
    /// </summary>
    [Theory]
    [InlineData(73)]
    [InlineData(74)]
    public void ReaderRequirements_YPbPr_Provided(int code)
    {
        int[] y = [145, 81, 41, 210, 170, 106, 125, 125], pb = [54, 90, 240, 16, 166, 202, 128, 91], pr = [34, 240, 110, 146, 16, 222, 128, 176];
        var cs = JpxTests.AnnexG.Lossless(4, 2, [(8, false), (8, false), (8, false)], [y, pb, pr]);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(23)], Box("rreq", Rreq(1, 0xC0, 0xC0, [(5, 0x80), (code, 0x40)]))), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Assert.Empty(image.MissingFeatures);
        var photoYcc = JpxDecoder.Decode(Jpx(cs, [Enumerated(23)], Box("rreq", Rreq(1, 0xC0, 0xC0, [(5, 0x80), (52, 0x40)]))), long.MaxValue);
        Assert.Equal([52], photoYcc.MissingFeatures);
    }

    /// <summary>
    /// T.801 Table M.14, value 83 (scRGB): provided where the decoder draws the image as scRGB — 16-bit unsigned components (and so fixed or
    /// floating point ones, JpxDecoder.ScRgbFits): nothing missing. 8-bit components, for which the decoder passes scRGB over and takes the
    /// sRGB behind it: 83 is missing, named as a colour space PDF-Knead does not draw. The box alone (no image) counts 83 as missing; with
    /// <c>scRgbDrawn</c> as provided, for both masks.
    /// </summary>
    [Fact]
    public void ReaderRequirements_ScRgb_ProvidedWhereDrawn()
    {
        var rreq = Box("rreq", Rreq(1, 0xC0, 0xC0, [(5, 0x80), (83, 0x40)]));
        int[] r = [12288, 4096, 8192, 6000], g = [12288, 4096, 5000, 9000], b = [12288, 4096, 4500, 11000];
        var sixteen = JpxTests.AnnexG.Lossless(2, 2, [(16, false), (16, false), (16, false)], [r, g, b]);
        var drawn = JpxDecoder.Decode(Jpx(sixteen, [Enumerated(25)], rreq), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, drawn.Color);
        Assert.Empty(drawn.MissingFeatures);

        var eight = JpxTests.AnnexG.Lossless(2, 2, [(8, false), (8, false), (8, false)], [[255, 0, 128, 60], [255, 0, 90, 30], [255, 0, 20, 200]]);
        var passedOver = JpxDecoder.Decode(Jpx(eight, [Enumerated(25, 1, 1), Enumerated(16, 0, 1)], rreq), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, passedOver.Color);
        Assert.Equal([83], passedOver.MissingFeatures);
        Assert.Equal(Colour, JpxRequirements.Describe(passedOver.MissingFeatures));

        var box = Rreq(1, 0xE0, 0xC0, [(5, 0x80), (83, 0x40), (34, 0x20)]);
        Assert.Equal([83], JpxRequirements.Missing(box, 0, box.Length));
        Assert.Empty(JpxRequirements.Missing(box, 0, box.Length, scRgbDrawn: true));
        Assert.Equal([34, 83], JpxRequirements.NotUnderstood(box, 0, box.Length));
        Assert.Equal([34], JpxRequirements.NotUnderstood(box, 0, box.Length, scRgbDrawn: true));
    }

    // ---- T.801 M.6.2.3, M.6.3: boxes laid out as Tables M.5, M.7 and M.9, with values of their own

    /// <summary>
    /// T.801 M.6.2.3, Table M.5 (layout: ML 1, both masks of four columns in one byte, five standard features each with a one-byte mask, no
    /// vendor feature), values of this test: the columns CMYK (55), a Part 1 profile 1 codestream (4) or a JPEG codestream (7), premultiplied
    /// opacity (10) — the display — and IPR metadata (35), only to understand the file. The decoder provides 55, 4 and 10: nothing missing for
    /// the display; for understanding, the metadata column is open.
    /// </summary>
    [Fact]
    public void ReaderRequirements_LaidOutAsTableM5()
    {
        var box = Rreq(1, 0xF0, 0xE0, [(55, 0x80), (4, 0x40), (7, 0x40), (10, 0x20), (35, 0x10)]);
        Assert.Equal(1 + 1 + 1 + 2 + 5 * 3 + 2, box.Length);
        Assert.Empty(JpxRequirements.Missing(box, 0, box.Length));
        Assert.Equal([35], JpxRequirements.NotUnderstood(box, 0, box.Length));
        Assert.Equal(Metadata, JpxRequirements.Describe(JpxRequirements.NotUnderstood(box, 0, box.Length)));
    }

    /// <summary>
    /// T.801 M.6.2.3, Table M.7 (layout: ML 1, four standard features and two vendor features, each UUID with a one-byte mask), values of this
    /// test: the display needs CMYK (55) or the first vendor feature, Any ICC (44), and the second vendor feature alone; understanding needs
    /// content metadata (36) besides, and a Part 1 codestream (5) sits in no column. The decoder provides 55, 44 and 5 but no vendor feature:
    /// the third column stays open for both masks — „Herstellermerkmale“ —, the metadata column for understanding.
    /// </summary>
    [Fact]
    public void ReaderRequirements_LaidOutAsTableM7()
    {
        var box = Rreq(1, 0xF0, 0xE0, [(55, 0x80), (44, 0x40), (5, 0x00), (36, 0x10)], 0x80, 0x20);
        Assert.Equal(1 + 1 + 1 + 2 + 4 * 3 + 2 + 2 * 17, box.Length);
        Assert.Equal([JpxRequirements.Vendor], JpxRequirements.Missing(box, 0, box.Length));
        Assert.Equal([JpxRequirements.Vendor, 36], JpxRequirements.NotUnderstood(box, 0, box.Length));
        Assert.Equal("Herstellermerkmale", JpxRequirements.Describe(JpxRequirements.Missing(box, 0, box.Length)));
        Assert.Equal("Herstellermerkmale, Metadaten", JpxRequirements.Describe(JpxRequirements.NotUnderstood(box, 0, box.Length)));
    }

    /// <summary>
    /// T.801 M.6.3, Table M.9 (layout as Table M.5) and the comparison worked out below it, values of this test: the display needs CIEJab (58),
    /// several layers (2) or compositing (17), and a Part 1 codestream (5) or animation (25); understanding needs JPX signatures (39) besides.
    /// The OR of the masks of what the decoder provides (2, 5) is 0110 0000: the first display column is open — CIEJab, which PDF excludes —,
    /// and for understanding the signature column too.
    /// </summary>
    [Fact]
    public void ReaderRequirements_LaidOutAsTableM9()
    {
        var box = Rreq(1, 0xF0, 0xE0, [(58, 0x80), (2, 0x40), (17, 0x40), (5, 0x20), (25, 0x20), (39, 0x10)]);
        Assert.Equal([58], JpxRequirements.Missing(box, 0, box.Length));
        Assert.Equal([39, 58], JpxRequirements.NotUnderstood(box, 0, box.Length));
        Assert.Equal(Colour, JpxRequirements.Describe(JpxRequirements.Missing(box, 0, box.Length)));
        Assert.Equal($"{Signatures}, {Colour}", JpxRequirements.Describe(JpxRequirements.NotUnderstood(box, 0, box.Length)));
    }

    // ---- T.801 M.11.7.8: floating point with a wide exponent field

    /// <summary>
    /// T.801 M.11.7.8 (Table M.35bis): a floating point format on a 38-bit channel with 4 mantissa bits has an exponent field of 33 bits, with
    /// 0 mantissa bits one of 37: the bias 2^(e−1) − 1, the all-ones exponent for infinity and not a number, computed in 64 bits. Before the
    /// shifts ran in 32 bits and wrapped (bias 0 for e = 33), so 1.0 came out as a huge number.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(20)]
    public void PixelFormat_FloatingPoint_WideExponentField(int m)
    {
        const int bits = 38;
        var e = bits - 1 - m;
        var format = 0x4000 | m;
        var bias = (1L << (e - 1)) - 1;
        long Pattern(bool negative, long exponent, long mantissa) => (negative ? 1L << (bits - 1) : 0) | exponent << m | mantissa;
        Assert.True(JpxPixelFormat.Fits(format, bits));
        Assert.Equal(1.0, JpxPixelFormat.Value(Pattern(false, bias, 0), bits, format));
        Assert.Equal(-2.0, JpxPixelFormat.Value(Pattern(true, bias + 1, 0), bits, format));
        Assert.Equal(0.25, JpxPixelFormat.Value(Pattern(false, bias - 2, 0), bits, format));
        Assert.Equal(double.PositiveInfinity, JpxPixelFormat.Value(Pattern(false, (1L << e) - 1, 0), bits, format));
        Assert.Equal(double.NegativeInfinity, JpxPixelFormat.Value(Pattern(true, (1L << e) - 1, 0), bits, format));
        if (m > 0)
        {
            Assert.True(double.IsNaN(JpxPixelFormat.Value(Pattern(false, (1L << e) - 1, 1), bits, format)));
            Assert.Equal(1.5, JpxPixelFormat.Value(Pattern(false, bias, 1L << (m - 1)), bits, format));
        }
        Assert.Equal(0.0, JpxPixelFormat.Value(0, bits, format));
    }

    /// <summary>
    /// ISO 32000-2, 7.4.9: where the PDF gives the colour space of a JPEG 2000 image (/ColorSpace, the decoder called with
    /// <c>ownColour</c> false), the colour specification of the file does not count — neither do the colour spaces its Reader Requirements box
    /// names: PhotoYCC (52) is not reported as missing then, a feature beyond colour (metadata, 34) is. With the colour of the file both are
    /// missing. Before the colour space was reported in either case (probe of 23.11.2026).
    /// </summary>
    [Fact]
    public void ReaderRequirements_ColourSpaceOfThePdf_ColourOfTheFileNotMissing()
    {
        var cs = JpxTests.AnnexG.Lossless(2, 2, [(8, false), (8, false), (8, false)], [[255, 0, 128, 60], [255, 0, 90, 30], [255, 0, 20, 200]]);
        var file = Jpx(cs, [Enumerated(16)], Box("rreq", Rreq(1, 0xC0, 0xC0, [(5, 0x80), (52, 0x40), (34, 0x40)])));
        Assert.Equal([34, 52], JpxDecoder.Decode(file, long.MaxValue).MissingFeatures.Order());
        Assert.Equal([34], JpxDecoder.Decode(file, long.MaxValue, ownColour: _ => false).MissingFeatures);
        Assert.Equal([34], JpxDecoder.Decode(file, long.MaxValue, applyPalette: false).MissingFeatures);
    }
}
