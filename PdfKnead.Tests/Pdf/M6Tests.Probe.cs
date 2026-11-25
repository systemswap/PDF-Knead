using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M6 (ISO 32000-2, chapter 8; ICC.1:2022): further proofs — each test pins one sentence or step more exactly than the proofs of the other
/// M6Tests files (helpers of this file: prefix Pr_; the helpers of the other M6Tests files are used as they are).
/// </summary>
public sealed partial class M6NormTests
{
    // ------------------------------------------------------------ helpers

    /// <summary>
    /// The places where a content stream breaks the order of text objects (8.2, Figure 9): „nested:BT“ — BT inside a text object; „intext:op“ —
    /// an operator that Figure 9 does not allow inside a text object (q, Q, cm, path construction and painting, clipping, Do, sh, inline images,
    /// d0/d1); „outside:op“ — text showing or positioning, or ET, outside a text object; „open“ — a text object never ended.
    /// </summary>
    private static List<string> Pr_TextViolations(byte[] content)
    {
        var found = new List<string>();
        var parser = new ContentParser(content);
        var inText = false;
        while (parser.Next() is { } op)
        {
            switch (op)
            {
                case "BT":
                    if (inText) found.Add("nested:BT");
                    inText = true;
                    break;
                case "ET":
                    if (!inText) found.Add("outside:ET");
                    inText = false;
                    break;
                // text showing and positioning: only inside
                case "Tj" or "TJ" or "'" or "\"" or "Td" or "TD" or "Tm" or "T*":
                    if (!inText) found.Add("outside:" + op);
                    break;
                // allowed on both levels: general graphics state, colour, text state, marked content, compatibility sections
                case "w" or "J" or "j" or "M" or "d" or "ri" or "i" or "gs"
                    or "CS" or "cs" or "SC" or "SCN" or "sc" or "scn" or "G" or "g" or "RG" or "rg" or "K" or "k"
                    or "Tc" or "Tw" or "Tz" or "TL" or "Tf" or "Tr" or "Ts"
                    or "BMC" or "BDC" or "EMC" or "MP" or "DP" or "BX" or "EX":
                    break;
                default:
                    if (inText) found.Add("intext:" + op);
                    break;
            }
        }
        if (inText) found.Add("open");
        return found;
    }

    /// <summary>A file of one page of 200 × 200 with the image XObject /Im (ASCII hex data) drawn by <paramref name="matrix"/>.</summary>
    private static byte[] Pr_ImagePage(string matrix, int width, int height, int bpc, string hex, string extra = "") =>
        B84_File($"q {matrix} cm /Im Do Q", "/XObject << /Im 5 0 R >>",
            $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent {bpc} {extra} " +
            $"/Filter /ASCIIHexDecode /Length {hex.Length + 1} >>\nstream\n{hex}>\nendstream");

    /// <summary>The first image XObject of the first page after redacting <paramref name="area"/>: its dictionary and its decoded samples.</summary>
    private static (PdfDictionary Dict, byte[] Data) Pr_RedactedImage(byte[] file, PdfRectangle area)
    {
        using var doc = PdfDocument.Open(file);
        using var redacted = PdfDocument.Open(RedactionTests.WithBars(doc, (0, area)));
        var xobjects = redacted.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!;
        var image = xobjects.Entries.Select(e => xobjects.Get(e.Key)).OfType<PdfStream>().Single();
        return (image.Dictionary, redacted.GetStreamData(image));
    }

    /// <summary>The tags of an RGB matrix profile whose red channel has the blue colorant and whose blue channel the red one (D50).</summary>
    private static (string, byte[])[] Pr_SwappedMatrixTags()
    {
        var trc = RIcc_Gamma(2.2);
        return
        [
            ("rXYZ", RIcc_XyzTag(0.1492240, 0.0632197, 0.7448387)), ("gXYZ", RIcc_XyzTag(0.2052401, 0.6256560, 0.0608902)),
            ("bXYZ", RIcc_XyzTag(0.6097559, 0.3111242, 0.0194811)), ("rTRC", trc), ("gTRC", trc), ("bTRC", trc),
            ("wtpt", RIcc_XyzTag(0.9642, 1, 0.8249)),
        ];
    }

    private static bool Pr_Bluish(Rgb c) => c.B > 150 && c.R < 100;

    private static bool Pr_Reddish(Rgb c) => c.R > 150 && c.B < 100;

    // ------------------------------------------------------------ P4: 8.6.5.5 sentences 41T–42T

    /// <summary>
    /// 8.6.5.5 sentences 41T–42T: a profile of the versions of Table 66 is processed without falling back — the same RGB matrix profile as
    /// version 2.1 and as version 4.3, in [/ICCBased] with a deliberately different /Alternate /DeviceRGB, paints the colour of the profile (its
    /// red channel carries the blue colorant: 1 0 0 is blue, the alternate would paint red).
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Pr_MatrixProfileVersion21And43_ProcessedWithoutTheAlternate(int major)
    {
        var profile = RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, Pr_SwappedMatrixTags(), (byte)major);
        Assert.Equal(major == 2 ? (byte)0x10 : (byte)0x30, profile[9]);
        Assert.NotNull(IccProfile.Parse(profile));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var space = RIcc_IccBased(doc, profile, 3, "DeviceRGB");
        Assert.NotSame(ColorSpace.DeviceRgb, space);
        var colour = space.ToRgb([1, 0, 0]);
        Assert.True(Pr_Bluish(colour), $"{colour}");
    }

    // ------------------------------------------------------------ P6: 8.2 sentences 26, 27 (text objects)

    /// <summary>
    /// 8.2 sentences 26, 27 (writing, text objects): after redacting a word in the middle of a line — once in a Tj, once in a TJ with a colour
    /// change inside the text object — the written content keeps the order of Figure 9 for text objects (no q, Q, cm, path, Do between BT and
    /// ET, no nested BT, no text operator outside) and for path objects; the words before and after stay, the word itself is gone.
    /// </summary>
    [Fact]
    public void Pr_RedactedWordMidLine_TextObjectsKeepTheOrderOfFigure9()
    {
        const string content = "BT /F1 12 Tf 100 400 Td (Abc def ghi) Tj ET q 1 0 0 rg 100 200 50 50 re f Q " +
                               "BT /F1 12 Tf 0 0 1 rg 100 300 Td [(Abc) -200 (def ghi)] TJ ET";
        using var doc = A81_Page(content);
        Assert.Empty(B84_ContentStreams(doc).SelectMany(s => Pr_TextViolations(s.Data)));   // the input itself keeps the order
        var bars = RedactionTests.WithBars(doc, (0, new PdfRectangle(125, 396, 139, 410)), (0, new PdfRectangle(126, 296, 140, 310)));
        using var redacted = PdfDocument.Open(bars);
        var text = Core.Pdf.Text.PageText.Create(PageInterpreter.BuildText(redacted, redacted.Pages[0]), 0).Text;
        Assert.DoesNotContain("def", text);
        Assert.Equal(2, text.Split("Abc").Length - 1);
        Assert.Equal(2, text.Split("ghi").Length - 1);
        var streams = B84_ContentStreams(redacted);
        Assert.Empty(streams.SelectMany(s => Pr_TextViolations(s.Data).Select(v => $"{s.Name}: {v}")));
        Assert.Empty(streams.SelectMany(s => B84_PathViolations(s.Data).Select(v => $"{s.Name}: {v}")));
    }

    // ------------------------------------------------------------ P7: 8.9.3 sentence 8 (writing)

    /// <summary>
    /// 8.9.3 sentence 8 (writing): an image the redaction writes anew keeps its units with the highest bit first — a 4-bit grey image of eight
    /// samples 1…8 (bytes 12 34 56 78), redacted over samples 1 and 2: the low half of the first byte and the high half of the second become 0
    /// (10 04 56 78), not the other halves.
    /// </summary>
    [Fact]
    public void Pr_RedactedFourBitImage_SamplesHighestBitFirst()
    {
        // eight samples of 2 × 2 units each: sample i covers x 50 + 2i … 52 + 2i (pixels of 2 units: not refined)
        var (dict, data) = Pr_RedactedImage(Pr_ImagePage("16 0 0 2 50 50", 8, 1, 4, "12345678"), new PdfRectangle(52.5, 49, 55.5, 53));
        Assert.Equal(4, dict.GetInteger(PdfName.Get("BitsPerComponent")));
        Assert.Equal(8, dict.GetInteger(PdfName.Get("Width")));
        Assert.Equal([0x10, 0x04, 0x56, 0x78], data);
    }

    /// <summary>
    /// 8.9.3 sentence 8 (writing): 16-bit units with the high byte first — a grey image of four samples 1234h, 0180h, 0080h, 0180h: (a) drawn
    /// at 2 units per sample (not refined), sample 1 redacted: bytes 12 34 00 00 00 80 01 80; (b) drawn at 10 units per sample with /Interpolate
    /// (refined and smoothed, every sample read and written anew): every sample left is between 0080h and 1234h read with the high byte first,
    /// the redacted ones 0 — read with the low byte first, the values between 0080h and 0180h would lie at 8000h and above.
    /// </summary>
    [Fact]
    public void Pr_RedactedSixteenBitImage_HighByteFirst()
    {
        var (plainDict, plain) = Pr_RedactedImage(Pr_ImagePage("8 0 0 2 50 50", 4, 1, 16, "1234018000800180"), new PdfRectangle(52.5, 49, 53.5, 53));
        Assert.Equal(16, plainDict.GetInteger(PdfName.Get("BitsPerComponent")));
        Assert.Equal([0x12, 0x34, 0x00, 0x00, 0x00, 0x80, 0x01, 0x80], plain);

        var (dict, data) = Pr_RedactedImage(Pr_ImagePage("40 0 0 10 50 50", 4, 1, 16, "0080018000800180", "/Interpolate true"),
            new PdfRectangle(71, 45, 79, 65));
        Assert.Equal(16, dict.GetInteger(PdfName.Get("BitsPerComponent")));
        var width = (int)dict.GetInteger(PdfName.Get("Width"))!;
        var height = (int)dict.GetInteger(PdfName.Get("Height"))!;
        Assert.True(width > 4 && height > 1, $"{width} × {height}");   // refined
        Assert.Equal(2 * width * height, data.Length);
        int At(int x, int y) => data[2 * (y * width + x)] << 8 | data[2 * (y * width + x) + 1];
        var between = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var v = At(x, y);
                if (v == 0) continue;
                Assert.InRange(v, 0x0080, 0x0180);
                if (v is > 0x0080 and < 0x0180) between++;
            }
        Assert.True(between > 0, "nothing interpolated");
        // the middle of the redacted area (x 75 of 50 … 90) is 0 in every row
        for (var y = 0; y < height; y++) Assert.Equal(0, At(width * 25 / 40, y));
    }

    // ------------------------------------------------------------ P8: 8.11.4.4 sentences 77–78

    /// <summary>
    /// 8.11.4.4 sentences 77–78, the order reversed: first the dictionary that gives OFF (/Category [/Language], en-US against de-DE), then /View
    /// with ViewState ON — the layer stays OFF; „the last dictionary wins“ would make it visible.
    /// </summary>
    [Fact]
    public void Pr_UsageSeveralDictionaries_OffFirstThenOn_StaysOff()
    {
        using var doc = K811_Layers("<< /OCGs [3 0 R] /D << /AS [<< /Event /View /OCGs [3 0 R] /Category [/Language] >> << /Event /View /OCGs [3 0 R] /Category [/View] >>] >> >>",
            "<< /Type /OCG /Name (Englisch) /Usage << /View << /ViewState /ON >> /Language << /Lang (en-US) >> >> >>");
        Assert.True(K811_Hidden(doc, 3));
        // the /View dictionary alone sets it ON
        using var alone = K811_Layers("<< /OCGs [3 0 R] /D << /OFF [3 0 R] /AS [<< /Event /View /OCGs [3 0 R] /Category [/View] >>] >> >>",
            "<< /Type /OCG /Name (Englisch) /Usage << /View << /ViewState /ON >> /Language << /Lang (en-US) >> >> >>");
        Assert.False(K811_Hidden(alone, 3));
    }

    // ------------------------------------------------------------ P9: ICC1 4.13 sentence 1

    /// <summary>
    /// ICC.1, 4.13 sentence 1: uInt8Number is one byte without sign — a grey lut8 to PCS Lab whose table entry for white is C0h C0h 40h: L* is
    /// 192/255 · 100 = 75.29 (with a sign it would be negative, divided by 256 it would be 75.0), a* = C0h − 80h = 64, b* = 40h − 80h = −64.
    /// </summary>
    [Fact]
    public void Pr_IccLut8_GridByteAbove7F_ReadWithoutSign()
    {
        var lut = RIcc_Lut8(1, 3, 2, i => i[0] == 1 ? [0xC0, 0xC0, 0x40] : [0, 0x80, 0x80]);
        var xyz = RIcc_ToXyz(IccProfile.Parse(RIcc_TableProfile("mntr", IccProfile.GraySpace, RIcc_LabSpace, lut))!, 1);
        var (l, a, b) = IccColor.XyzToLab(xyz[0], xyz[1], xyz[2]);
        Assert.InRange(l, 75.28f, 75.31f);
        Assert.InRange(a, 63.95f, 64.05f);
        Assert.InRange(b, -64.05f, -63.95f);
    }

    // ------------------------------------------------------------ P10: ICC1 8.7 sentence 1

    /// <summary>
    /// ICC.1, 8.7 sentence 1 (behaviour of today): a colour space profile without BToA0 — the reader needs only AToB0 and draws its colours; the
    /// PDF/A check looks at the header only and accepts it, the required tags are not checked.
    /// </summary>
    [Fact]
    public void Pr_Icc8_7_ColourSpaceProfileWithoutB2A0_ReadAndAcceptedToday()
    {
        var spac = IccO_Profile("spac", IccProfile.RgbSpace, IccO_PcsLab,
            [("desc", IccO_Text("Raum")), ("cprt", IccO_Text("frei")), ("wtpt", IccO_XyzTag(0.9642, 1, 0.8249)),
                ("A2B0", IccO_Lut16(3, 3, 2, v => IccO_LabRaw(30 + 40 * v[0], 20 * v[1] - 10, 0)))]);
        Assert.Null(IccO_TagData(spac, "B2A0"));
        Assert.NotNull(IccProfile.Parse(spac));
        foreach (var s in IccO_Samples(3))
        {
            var expected = new float[3];
            IccColor.LabToXyz(30 + 40 * s[0], 20 * s[1] - 10, 0, expected);
            var xyz = IccO_ToXyz(spac, 0, s);
            for (var c = 0; c < 3; c++) Assert.InRange(xyz[c] - expected[c], -2e-3f, 2e-3f);
        }
        Assert.True(PdfACheck.IccProfileHeaderValid(spac, 4));
        Assert.NotSame(ColorSpace.DeviceGray, IccO_Space(spac, 3, "DeviceGray"));
    }

    // ------------------------------------------------------------ P11: ICC1 10.12.6 sentences 1–5

    /// <summary>
    /// ICC.1, 10.12.6 sentences 1–5: as many B curves as outputs, one after the other with padding, each a whole curveType or
    /// parametricCurveType — a curv of three entries (18 bytes, 2 padding bytes), a para of type 0 (gamma 2) and a para of type 2 (offset c 0.1):
    /// 0.5 on each channel gives 0.75, 0.25 and 0.6, each channel its own curve.
    /// </summary>
    [Fact]
    public void Pr_Icc10_12_6_BCurvesCurvAndPara_EachChannelItsCurve()
    {
        var curves = IccS_Curves(IccO_Curv(0, 0.75, 1), RIcc_Para(0, 2), RIcc_Para(2, 1, 1, 0, 0.1));
        Assert.Equal(20 + 16 + 28, curves.Length);
        var profile = IccS_Profile(IccProfile.RgbSpace, IccS_Mab(3, 3, curves, null, null, null, null));
        var v = IccS_Values(profile, 0.5f, 0.5f, 0.5f);
        Assert.InRange(v[0], 0.7499, 0.7501);
        Assert.InRange(v[1], 0.2499, 0.2501);
        Assert.InRange(v[2], 0.5999, 0.6001);
    }

    // ------------------------------------------------------------ P12: ICC1 F.3 sentences 43–45 (writing)

    /// <summary>
    /// ICC.1, F.3 sentences 43–45 (writing): the matrix and curve profiles PDF-Knead writes carry PCS XYZ (bytes 20–23 „XYZ “) — sRGB, sRGB
    /// grey, ROMM RGB, the primaries of JPEG 2000, CalGray, CalRGB and a version-4 profile rewritten as 2.1.
    /// </summary>
    [Fact]
    public void Pr_OwnMatrixProfiles_PcsXyz()
    {
        foreach (var (name, p) in IccL_OwnProfiles().Append(("AsVersion2", CalibratedProfile.AsVersion2(PdfARuleCases.Version4Rgb())!)))
            Assert.True(Encoding.ASCII.GetString(p, 20, 4) == "XYZ ", $"{name}: {Encoding.ASCII.GetString(p, 20, 4)}");
    }

    // ------------------------------------------------------------ P13: 8.6.3 sentences 1–2

    /// <summary>
    /// 8.6.3 sentences 1–2: spaces of one family differ by their parameters — two arrays [/ICCBased …] read through ColorSpace.Create, one with an
    /// RGB matrix profile, one with the same profile whose red and blue colorants are exchanged: the same values 1 0 0 give red and blue.
    /// </summary>
    [Fact]
    public void Pr_TwoIccBasedSpaces_SameValuesOtherColours()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var plain = RIcc_IccBased(doc, RIcc_MatrixProfile(), 3);
        var swapped = RIcc_IccBased(doc, RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, Pr_SwappedMatrixTags()), 3);
        Assert.True(Pr_Reddish(plain.ToRgb([1, 0, 0])), $"{plain.ToRgb([1, 0, 0])}");
        Assert.True(Pr_Bluish(swapped.ToRgb([1, 0, 0])), $"{swapped.ToRgb([1, 0, 0])}");
    }

    // ------------------------------------------------------------ P14: 8.6.6.4 sentence 54 (writing, PDF/A)

    /// <summary>Every Separation and DeviceN array of a document (searched through all objects) with its alternate space resolved.</summary>
    private static List<(string Family, PdfObject Alternate)> Pr_ColourantSpaces(PdfDocument doc)
    {
        var found = new List<(string, PdfObject)>();
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        void Visit(PdfObject? obj, int depth)
        {
            if (obj is null || depth > 40 || !seen.Add(obj)) return;
            switch (obj)
            {
                case PdfArray array:
                    if (array.Count >= 4 && array.Get(0) is PdfName { Value: "Separation" or "DeviceN" } family)
                        found.Add((family.Value, array.Get(2)));
                    for (var i = 0; i < array.Count; i++) Visit(array.Get(i), depth + 1);
                    break;
                case PdfStream stream:
                    Visit(stream.Dictionary, depth + 1);
                    break;
                case PdfDictionary dict:
                    foreach (var (key, _) in dict.Entries)
                        if (key.Value is not ("Parent" or "P")) Visit(dict.Get(key), depth + 1);
                    break;
            }
        }
        for (var n = 1; n <= doc.HighestObjectNumber; n++)
        {
            try
            {
                Visit(doc.GetObject(n), 0);
            }
            catch (Core.Pdf.PdfException)
            {
            }
        }
        return found;
    }

    private static bool Pr_SpecialSpace(PdfObject space) =>
        (space as PdfName ?? (space as PdfArray)?.Get(0) as PdfName)?.Value is "Pattern" or "Indexed" or "I" or "Separation" or "DeviceN";

    /// <summary>
    /// 8.6.6.4 sentence 54 (writing, PDF/A): no Separation or DeviceN space the PDF/A copy writes has a special alternate space — a Separation, a
    /// DeviceN of three colourants (the copy describes them under /Colorants) and a DeviceN of 33 colourants (too many for PDF/A-2: replaced by
    /// its alternate); every such space in the copy has a device or CIE-based alternate.
    /// </summary>
    [Fact]
    public void Pr_PdfACopy_ColourantSpacesWithoutSpecialAlternate()
    {
        var names33 = string.Join(" ", Enumerable.Range(1, 33).Select(i => $"/F{i}"));
        var pops = string.Join(" ", Enumerable.Repeat("pop", 30));
        var postScript = $"{{ {pops} }}";
        var file = B84_File("/CS0 cs 1 scn 10 10 40 40 re f /CS1 cs 0.2 0.5 0.8 scn 60 10 40 40 re f /CS2 cs " +
                            string.Join(" ", Enumerable.Repeat("0.5", 33)) + " scn 110 10 40 40 re f",
            "/ColorSpace << /CS0 [/Separation /Gold /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0.8 0] /N 1 >>] " +
            "/CS1 [/DeviceN [/Gold /Silber /Kupfer] /DeviceRGB 5 0 R] /CS2 [/DeviceN [" + names33 + "] /DeviceRGB 6 0 R] >>",
            $"<< /FunctionType 4 /Domain [0 1 0 1 0 1] /Range [0 1 0 1 0 1] /Length 3 >>\nstream\n{{ }}\nendstream",
            $"<< /FunctionType 4 /Domain [{string.Join(" ", Enumerable.Repeat("0 1", 33))}] /Range [0 1 0 1 0 1] /Length {postScript.Length} >>\nstream\n{postScript}\nendstream");
        using var doc = PdfDocument.Open(file);
        var result = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b);
        using var copy = PdfDocument.Open(result.Bytes);
        var spaces = Pr_ColourantSpaces(copy);
        Assert.True(result.Conforms, string.Join(" / ", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
        // the Separation, the DeviceN of three colourants with a Separation per colourant under /Colorants; no DeviceN of 33 left
        Assert.Single(spaces, s => s.Family == "DeviceN");
        Assert.True(spaces.Count(s => s.Family == "Separation") >= 4, string.Join(" ; ", spaces.Select(s => $"{s.Family} {s.Alternate}")));
        Assert.All(spaces, s => Assert.False(Pr_SpecialSpace(s.Alternate), $"{s.Family} mit {s.Alternate}"));
    }

    /// <summary>
    /// 8.6.6.4 sentence 54 (writing, PDF/A): a Separation space of the input with the special alternate space /Pattern is not written as such into
    /// the PDF/A copy: the alternate becomes the device space of as many components as the tint transform gives (here DeviceGray). Before, the
    /// copy kept [/Separation /X /Pattern …] unchanged (docs/FEHLERGEDAECHTNIS.md, No. 1128).
    /// </summary>
    [Fact]
    public void Pr_PdfACopy_InputSeparationWithPatternAlternate_NotWrittenAsSuch()
    {
        var file = B84_File("/CS0 cs 1 scn 10 10 40 40 re f",
            "/ColorSpace << /CS0 [/Separation /X /Pattern << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>] >>");
        using var doc = PdfDocument.Open(file);
        var result = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b);
        using var copy = PdfDocument.Open(result.Bytes);
        Assert.All(Pr_ColourantSpaces(copy), s => Assert.False(Pr_SpecialSpace(s.Alternate), $"{s.Family} mit {s.Alternate}"));
    }

    // ------------------------------------------------------------ P15: 8.7.4.1 sentences 1–4

    /// <summary>
    /// 8.7.4.1 sentences 1–4: a shading pattern has /PatternType 2 — the same pattern with /PatternType 3 is not used as a shading: the path is
    /// painted exactly as with a broken pattern (one without /Shading), the same colour at both ends, while /PatternType 2 runs from red to blue.
    /// </summary>
    [Fact]
    public void Pr_ShadingPattern_PatternType3_NotUsed()
    {
        const string shading = "/Shading << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] " +
                               "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> /Extend [true true] >>";
        const string content = "/Pattern cs /P scn 0 0 200 100 re f";
        using var two = B84_Render(content, $"/Pattern << /P << /PatternType 2 {shading} >> >>");
        Assert.True(B84_At(two, 5, 50) is { Red: > 200, Blue: < 60 }, $"{B84_At(two, 5, 50)}");
        Assert.True(B84_At(two, 195, 50) is { Blue: > 200, Red: < 60 }, $"{B84_At(two, 195, 50)}");
        using var three = B84_Render(content, $"/Pattern << /P << /PatternType 3 {shading} >> >>");
        using var broken = B84_Render(content, "/Pattern << /P << /PatternType 2 >> >>");
        Assert.True(B84_Same(three, broken));
        Assert.Equal(B84_At(three, 5, 50), B84_At(three, 195, 50));
    }

    // ------------------------------------------------------------ P16: 8.7.4.5.8 sentences 55–62 (and 8.7.4.5.7, flag 2)

    /// <summary>
    /// Two patches: the first (70…130) with its edge from point 7 to point 10 curved unevenly (the inner points of that edge at (150, 112) and
    /// (138, 84)), the second beyond that edge — with flag 2 (<paramref name="takeOver"/>) or with flag 0 and the four points and two colours
    /// written out in the order 8.7.4.5.7 and 8.7.4.5.8 give for flag 2.
    /// </summary>
    private static byte[] Pr_CurvedEdgePatches(bool tensor, bool takeOver)
    {
        (int X, int Y)[] first = [(70, 70), (70, 90), (70, 110), (70, 130), (90, 130), (110, 130), (130, 130), (150, 112), (138, 84), (130, 70), (110, 70), (90, 70)];
        (int X, int Y)[] firstInner = [(90, 90), (90, 110), (110, 110), (110, 90)];
        (int X, int Y)[] rest = [(150, 70), (170, 70), (190, 70), (190, 90), (190, 110), (190, 130), (170, 130), (150, 130)];
        (int X, int Y)[] restInner = [(150, 105), (150, 90), (170, 90), (170, 105)];
        var data = new H8745_Bits().Bytes(0);
        foreach (var (x, y) in first) data.Bytes(x, y);
        if (tensor) foreach (var (x, y) in firstInner) data.Bytes(x, y);
        data.Bytes(255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0);
        data.Bytes(takeOver ? 2 : 0);
        if (!takeOver) foreach (var (x, y) in first[6..10]) data.Bytes(x, y);
        foreach (var (x, y) in rest) data.Bytes(x, y);
        if (tensor) foreach (var (x, y) in restInner) data.Bytes(x, y);
        if (!takeOver) data.Bytes(0, 0, 255, 255, 255, 0);   // c3 and c4 of the first patch
        data.Bytes(0, 0, 0, 255, 255, 255);
        return data.ToArray();
    }

    /// <summary>
    /// 8.7.4.5.8 sentences 55–62 (type 7) and 8.7.4.5.7 sentences 79–86 (type 6): with flag 2 the first four points of a patch are points 7, 8, 9,
    /// 10 of the previous one in this order and its first two colours the previous c3 and c4 — over a curved, uneven common edge the mesh is the
    /// same as with these points and colours written out (exchanged inner points of the edge would give another curve).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pr_Patches_Flag2_CurvedCommonEdge(bool tensor)
    {
        var type = tensor ? 7 : 6;
        var taken = H8745_MeshOf(H8745_Sh(_ => new PdfStream(H8745_MeshDict(type, 8, 8, H8745_ByteRgb), Pr_CurvedEdgePatches(tensor, true))));
        var written = H8745_MeshOf(H8745_Sh(_ => new PdfStream(H8745_MeshDict(type, 8, 8, H8745_ByteRgb), Pr_CurvedEdgePatches(tensor, false))));
        Assert.Equal(written.Points.Length, taken.Points.Length);
        for (var i = 0; i < written.Points.Length; i++)
            Assert.True(Math.Abs(written.Points[i] - taken.Points[i]) < 1e-6, $"{i}: {written.Points[i]} / {taken.Points[i]}");
        Assert.Equal(written.Colors, taken.Colors);
        // the middle of the curved edge, (130 + 3 · 150 + 3 · 138 + 130) / 8 and (130 + 3 · 112 + 3 · 84 + 70) / 8, is a point of the mesh
        H8745_HasVertex(taken, (140.5, 98.5));
    }

    // ------------------------------------------------------------ P17: 8.10.3 sentences 1–3

    private const string Pr_TwoRedSquares = "1 0 0 rg 0 0 120 120 re f 80 80 120 120 re f";

    /// <summary>
    /// 8.10.3 sentences 1–3: a group XObject is recognised by a dictionary under /Group — a form with /Group /Transparency (a name) painted with ca
    /// 0.5 is no group, each square on its own (the overlap darker, about 255 64 64); with /Group &lt;&lt; /S /Transparency &gt;&gt; the overlap
    /// is as light as the rest (255 128 128).
    /// </summary>
    [Fact]
    public void Pr_GroupAsName_NoGroup()
    {
        const string resources = "/XObject << /Fm 5 0 R >> /ExtGState << /H << /ca 0.5 >> >>";
        using (var name = B84_Render("/H gs /Fm Do", resources, [B84_Form(Pr_TwoRedSquares, "/Group /Transparency")]))
        {
            var overlap = B84_At(name, 100, 100);
            Assert.True(overlap.Red > 245 && overlap.Green is >= 54 and <= 74, $"{overlap}");
            Assert.True(B84_At(name, 50, 50) is { Red: > 245, Green: >= 120 and <= 136 }, $"{B84_At(name, 50, 50)}");
        }
        using var group = B84_Render("/H gs /Fm Do", resources, [B84_Form(Pr_TwoRedSquares, "/Group << /S /Transparency >>")]);
        Assert.True(B84_At(group, 100, 100) is { Red: > 245, Green: >= 120 and <= 136 }, $"{B84_At(group, 100, 100)}");
    }

    // ------------------------------------------------------------ P18: 8.11.4.3 sentences 48–58 (writing)

    /// <summary>A one-page file with layers of the given names, all in /Order in this order (behind <paramref name="orderPrefix"/>), and /D /OFF.</summary>
    private static byte[] Pr_LayerFile(string[] names, string orderPrefix = "", string off = "")
    {
        var layers = string.Join(" ", Enumerable.Range(0, names.Length).Select(i => $"{5 + i} 0 R"));
        return K811_File($"/OCProperties << /OCGs [{layers}] /D << /Order [{orderPrefix}{layers}] {off} >> >>", "", "0 0 1 rg 10 10 20 20 re f", "",
            [.. names.Select(n => $"<< /Type /OCG /Name ({n}) >>")]);
    }

    /// <summary>The names of the layers in /D /Order (nested arrays flattened, text labels as „(label)“).</summary>
    private static List<string> Pr_OrderNames(PdfDocument doc)
    {
        var names = new List<string>();
        void Walk(PdfArray order, int depth)
        {
            for (var i = 0; i < order.Count && depth < 10; i++)
                switch (order.Get(i))
                {
                    case PdfDictionary layer:
                        names.Add(layer.GetString(PdfName.Name)!.ToText());
                        break;
                    case PdfArray inner:
                        Walk(inner, depth + 1);
                        break;
                    case PdfString label:
                        names.Add($"({label.ToText()})");
                        break;
                    case var other:
                        names.Add($"<{other}>");
                        break;
                }
        }
        var d = doc.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!.GetDictionary(PdfName.Get("D"))!;
        Walk(d.GetArray(PdfName.Get("Order"))!, 0);
        return names;
    }

    /// <summary>
    /// 8.11.4.3 sentences 48–58 (writing): merging two files with /Order — the order of the first file stays and that of the inserted one is
    /// appended (with its text label), each entry naming a layer of the merged file; sanitising away a layer never shown takes it out of /Order
    /// without leaving a null entry.
    /// </summary>
    [Fact]
    public void Pr_MergedFiles_OrderAppended_RemovedLayerLeavesOrder()
    {
        using var first = PdfDocument.Open(Pr_LayerFile(["Grundriss", "Maße"]));
        using var second = PdfDocument.Open(Pr_LayerFile(["Elektro", "Sanitär"], "(Haustechnik) "));
        using (var output = new MemoryStream())
        {
            PdfWriter.Write(first, [.. PageItem.From(first), .. PageItem.From(second)], output);
            using var merged = PdfDocument.Open(output.ToArray());
            Assert.Equal(["Grundriss", "Maße", "(Haustechnik)", "Elektro", "Sanitär"], Pr_OrderNames(merged));
            var ocgs = merged.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!.GetArray(PdfName.Get("OCGs"))!;
            var listed = Enumerable.Range(0, ocgs.Count).Select(i => ocgs.Get(i)).ToHashSet(ReferenceEqualityComparer.Instance);
            var order = merged.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!.GetDictionary(PdfName.Get("D"))!.GetArray(PdfName.Get("Order"))!;
            Assert.All(Enumerable.Range(0, order.Count).Select(i => order.Get(i)).OfType<PdfDictionary>(), l => Assert.Contains(l, listed));
        }

        // „Maße“ off on screen and in print: removed by the sanitising, and with it from /Order
        using var hidden = PdfDocument.Open(Pr_LayerFile(["Grundriss", "Maße"], off: "/OFF [6 0 R]"));
        using var cleaned = new MemoryStream();
        PdfWriter.Write(hidden, cleaned, new PdfSaveOptions { Cleanup = CleanupItems.HiddenLayers });
        using var saved = PdfDocument.Open(cleaned.ToArray());
        Assert.Equal(["Grundriss"], Pr_OrderNames(saved));
        var savedOrder = saved.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!.GetDictionary(PdfName.Get("D"))!.GetArray(PdfName.Get("Order"))!;
        Assert.IsType<PdfDictionary>(Assert.Single(Enumerable.Range(0, savedOrder.Count).Select(i => savedOrder.Get(i))));
    }

    // ------------------------------------------------------------ P20: ICC1 6.2.1 sentences 8–9

    /// <summary>
    /// ICC.1, 6.2.1 sentences 8–9: the intents other than absolute colorimetric are relative to the PCS illuminant — an RGB profile with an AToB0
    /// to PCS XYZ whose white is D50 without any adaptation (and a media white point of D65): white gives exactly D50 and sRGB white, the media
    /// white point changes nothing (the same XYZ as with wtpt D50).
    /// </summary>
    [Fact]
    public void Pr_A2B0WithXyzPcs_WhiteIsD50WithoutAdaptation()
    {
        double[] d50 = [IccProfile.WhiteX, IccProfile.WhiteY, IccProfile.WhiteZ];
        var lut = RIcc_Lut16(3, 3, 2, i => [.. d50.Select(w => (int)Math.Round(w * 32768 * (i[0] + i[1] + i[2]) / 3.0))]);
        var d65 = RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, [("wtpt", RIcc_XyzTag(0.9505, 1, 1.089)), ("A2B0", lut)]);
        var withD50 = RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, [("wtpt", RIcc_XyzTag(0.9642, 1, 0.8249)), ("A2B0", lut)]);
        var white = RIcc_ToXyz(IccProfile.Parse(d65)!, 1, 1, 1);
        for (var c = 0; c < 3; c++) Assert.InRange(white[c], d50[c] - 1e-4, d50[c] + 1e-4);
        RIcc_AssertSameColours(withD50, d65);
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        Assert.Equal(new Rgb(255, 255, 255), RIcc_IccBased(doc, d65, 3, "DeviceGray").ToRgb([1, 1, 1]));
    }

    // ------------------------------------------------------------ P22: ICC1 10.10 sentences 108–112

    /// <summary>
    /// ICC.1, 10.10 sentences 108–112: the table of a lut16 has 2 · o · g^i bytes, the first input varies slowest — a CMYK lut16 built here (g 2,
    /// legacy Lab) dark only at the corner C = 1, M = Y = K = 0 gives (1, 0, 0, 0) dark and (0, 0, 0, 1), (0, 1, 0, 0) light; the same table
    /// shortened by two bytes (its tag no longer holds 2 · o · g^i bytes, the white point follows it) is unreadable and /Alternate applies.
    /// </summary>
    [Fact]
    public void Pr_Lut16_FirstInputSlowest_ShortTableUnreadable()
    {
        var lut = RIcc_Lut16(4, 3, 2, i => [i[0] == 1 && i[1] + i[2] + i[3] == 0 ? 0 : 0xFF00, 0x8000, 0x8000]);
        Assert.Equal(52 + 2 * 2 * 4 + 2 * 3 * 16 + 2 * 2 * 3, lut.Length);
        var profile = IccProfile.Parse(RIcc_TableProfile("prtr", IccProfile.CmykSpace, RIcc_LabSpace, lut))!;
        Assert.True(RIcc_ToXyz(profile, 1, 0, 0, 0)[1] < 0.01f);
        Assert.True(RIcc_ToXyz(profile, 0, 0, 0, 1)[1] > 0.99f);
        Assert.True(RIcc_ToXyz(profile, 0, 1, 0, 0)[1] > 0.99f);

        var tableEnd = 52 + 2 * 2 * 4 + 2 * 3 * 16;
        var shortened = lut[..(tableEnd - 2)];
        var broken = RIcc_Profile("prtr", IccProfile.CmykSpace, RIcc_LabSpace, [("A2B0", shortened), ("wtpt", RIcc_XyzTag(0.9642, 1, 0.8249))]);
        Assert.Null(IccProfile.Parse(broken));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        Assert.Same(ColorSpace.DeviceGray, RIcc_IccBased(doc, broken, 4, "DeviceGray"));
    }
}
