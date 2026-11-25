using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

// M6 draft d-861-865 (ISO 32000-2, 8.6.1 to 8.6.5): the rows whose evidence was missing.
public sealed partial class M6NormTests
{
    // ------------------------------------------------------------ helpers (package d)

    /// <summary>A stream object as text: its dictionary entries and data (Latin-1, one byte per character).</summary>
    private static string D_Stream(string dict, string data) => $"<< {dict} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static string D_Stream(string dict, byte[] data) => D_Stream(dict, Encoding.Latin1.GetString(data));

    /// <summary>
    /// A file with one page of 200 × 200: its content, the entries of its resources and of its catalogue; further objects are numbered from 5.
    /// </summary>
    private static byte[] D_File(string content, string resources = "", string catalog = "", params string[] objects) =>
        DocumentStructureNormTests.FileOf([
            $"<< /Type /Catalog /Pages 2 0 R {catalog} >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << {resources} >> /Contents 4 0 R >>",
            D_Stream("", content), .. objects]);

    private static (PdfDocument Doc, DisplayList List) D_Page(string content, string resources = "", string catalog = "", params string[] objects)
    {
        var doc = PdfDocument.Open(D_File(content, resources, catalog, objects));
        return (doc, PageInterpreter.Build(doc, doc.Pages[0]));
    }

    /// <summary>The paint of the first entry of the display list that fills (or strokes, <paramref name="stroke"/>), as ARGB.</summary>
    private static uint D_Paint(string content, string resources = "", bool stroke = false, string catalog = "", params string[] objects)
    {
        var (doc, list) = D_Page(content, resources, catalog, objects);
        using (doc)
        {
            var item = list.Items.ToArray().First(i => stroke ? i.Kind is DisplayItemKind.Stroke or DisplayItemKind.FillStroke
                : i.Kind is DisplayItemKind.Fill or DisplayItemKind.FillStroke);
            var paint = list.Paints[item.Paint];
            return stroke ? paint.Stroke : paint.Fill;
        }
    }

    private static uint D_Fill(string content, string resources = "", string catalog = "", params string[] objects) =>
        D_Paint(content, resources, false, catalog, objects);

    private static uint D_Stroke(string content, string resources = "") => D_Paint(content, resources, stroke: true);

    private static (byte R, byte G, byte B) D_Rgb(uint argb) => ((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>The fill of „/CS0 cs <paramref name="values"/> sc“ in the colour space <paramref name="space"/> (written as text).</summary>
    private static (byte R, byte G, byte B) D_In(string space, string values, params string[] objects) =>
        D_Rgb(D_Fill($"/CS0 cs {values} sc 0 0 10 10 re f", $"/ColorSpace << /CS0 {space} >>", "", objects));

    /// <summary>The page rendered at 72 dpi; the pixel at page coordinates (x, y).</summary>
    private static SKColor D_Pixel(byte[] file, int x, int y, bool overprintPreview = false)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache(), overprintPreview: overprintPreview);
        return bitmap.GetPixel(x, bitmap.Height - 1 - y);
    }

    private static void D_Grey((byte R, byte G, byte B) c, int low, int high)
    {
        Assert.True(c.R >= low && c.R <= high && Math.Abs(c.R - c.G) <= 1 && Math.Abs(c.G - c.B) <= 1, $"kein Grau {low}–{high}: {c}");
    }

    private const string D_D50 = "/WhitePoint [0.9642 1 0.8249]";
    private const string D_D65 = "/WhitePoint [0.9505 1 1.089]";

    /// <summary>CMYK profile of the simple formula (R = (1 − C)(1 − K) …).</summary>
    private static readonly Lazy<byte[]> D_NaiveCmyk = new(() => CmykProfile.Bake((c, m, y, k) => ((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k))));

    /// <summary>CMYK profile with cyan and magenta swapped.</summary>
    private static readonly Lazy<byte[]> D_SwappedCmyk = new(() => CmykProfile.Bake((c, m, y, k) => ((1 - m) * (1 - k), (1 - c) * (1 - k), (1 - y) * (1 - k))));

    /// <summary>A copy of an ICC profile with one tag more (appended at the end; the tag table grows by one entry).</summary>
    private static byte[] D_WithTag(byte[] profile, uint signature, byte[] data)
    {
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128));
        var tableEnd = 132 + 12 * count;
        var d = new byte[profile.Length + 12 + data.Length];
        profile.AsSpan(0, tableEnd).CopyTo(d);
        profile.AsSpan(tableEnd).CopyTo(d.AsSpan(tableEnd + 12));
        void W(int at, int v) => BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(at), (uint)v);
        W(0, d.Length);
        W(128, count + 1);
        for (var i = 0; i < count; i++) W(136 + 12 * i, (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(136 + 12 * i)) + 12);
        W(tableEnd, (int)signature);
        W(tableEnd + 4, profile.Length + 12);
        W(tableEnd + 8, data.Length);
        data.CopyTo(d, profile.Length + 12);
        return d;
    }

    /// <summary>The bytes of a tag of an ICC profile.</summary>
    private static byte[] D_Tag(byte[] profile, uint signature)
    {
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128));
        for (var i = 0; i < count; i++)
            if (BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(132 + 12 * i)) == signature)
                return profile.AsSpan((int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(136 + 12 * i)),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(140 + 12 * i))).ToArray();
        throw new InvalidOperationException("tag");
    }

    /// <summary>A CMYK profile with two tables: A2B0 (perceptual) cyan and magenta swapped, A2B1 (relative colorimetric) the simple formula.</summary>
    private static readonly Lazy<byte[]> D_SwappedAndPlain = new(() =>
        D_WithTag(D_SwappedCmyk.Value, 0x41324231, D_Tag(D_NaiveCmyk.Value, 0x41324230)));

    /// <summary>An RGB matrix profile (version 2.1, mntr): primaries (XYZ D50, one per column) and one gamma for all three channels.</summary>
    private static byte[] D_MatrixProfile(float[] columns, float gamma)
    {
        var tags = new (uint Sig, byte[] Data)[6];
        uint[] xyzSigs = [0x7258595A, 0x6758595A, 0x6258595A];
        uint[] trcSigs = [0x72545243, 0x67545243, 0x62545243];
        for (var c = 0; c < 3; c++)
        {
            var xyz = new byte[20];
            BinaryPrimitives.WriteUInt32BigEndian(xyz, 0x58595A20);
            for (var r = 0; r < 3; r++) BinaryPrimitives.WriteInt32BigEndian(xyz.AsSpan(8 + 4 * r), (int)Math.Round(columns[r * 3 + c] * 65536));
            tags[c] = (xyzSigs[c], xyz);
            var curve = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(curve, 0x63757276);
            BinaryPrimitives.WriteUInt32BigEndian(curve.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12), (ushort)Math.Round(gamma * 256));
            tags[3 + c] = (trcSigs[c], curve);
        }
        return D_Profile(0x6D6E7472, IccProfile.RgbSpace, 0x58595A20, tags);
    }

    /// <summary>An ICC profile (version 2.1) of the given class, data colour space and PCS with the given tags.</summary>
    private static byte[] D_Profile(uint deviceClass, uint space, uint pcs, (uint Sig, byte[] Data)[] tags)
    {
        var size = 128 + 4 + 12 * tags.Length + tags.Sum(t => t.Data.Length);
        var d = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(d, (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), 0x02100000);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(12), deviceClass);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), space);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), pcs);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(36), 0x61637370);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(128), (uint)tags.Length);
        var at = 128 + 4 + 12 * tags.Length;
        for (var i = 0; i < tags.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(132 + 12 * i), tags[i].Sig);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(136 + 12 * i), (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(140 + 12 * i), (uint)tags[i].Data.Length);
            tags[i].Data.CopyTo(d, at);
            at += tags[i].Data.Length;
        }
        return d;
    }

    // AdobeRGB primaries, Bradford-adapted to D50
    private static readonly float[] D_AdobeColumns = [0.6097559f, 0.2052401f, 0.1492240f, 0.3111242f, 0.6256560f, 0.0632197f, 0.0194811f, 0.0608902f, 0.7448387f];

    /// <summary>
    /// A colour space profile with data colour space Lab and PCS Lab (class spac): A2B0 a lut16 that passes the values through (input 0…1 per
    /// channel is L* 0…100, a* and b* −128…127 in the encoding of version 2).
    /// </summary>
    private static byte[] D_LabProfile()
    {
        var lut = new byte[52 + 3 * 2 * 2 + 8 * 3 * 2 + 3 * 2 * 2];
        BinaryPrimitives.WriteUInt32BigEndian(lut, 0x6D667432);   // mft2
        lut[8] = 3;
        lut[9] = 3;
        lut[10] = 2;
        foreach (var at in new[] { 12, 28, 44 }) BinaryPrimitives.WriteInt32BigEndian(lut.AsSpan(at), 65536);
        BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(50), 2);
        var p = 52;
        for (var i = 0; i < 3; i++, p += 4) BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(p + 2), 65535);
        for (var i = 0; i < 8; i++)
            for (var o = 0; o < 3; o++, p += 2)
                BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(p), (ushort)((i >> (2 - o) & 1) * 0xFF00));
        for (var i = 0; i < 3; i++, p += 4) BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(p + 2), 65535);
        return D_Profile(0x73706163, IccProfile.LabSpace, IccProfile.LabSpace, [(0x41324230, lut)]);
    }

    private static PdfAResult D_PdfA(byte[] file, PdfAConformance part)
    {
        using var doc = PdfDocument.Open(file);
        return PdfAExport.Write(doc, PageItem.From(doc), part);
    }

    // ------------------------------------------------------------ 8.6.1

    /// <summary>
    /// 8.6.1 sentences 9–10: the appearances PDF-Knead draws itself for annotations put no rendering parameters into the page description — their
    /// graphics states (opacity, blend mode) hold none of /TR, /TR2, /HT, /BG, /BG2, /UCR, /UCR2.
    /// </summary>
    [Fact]
    public void OwnAppearances_WriteNoRenderingParameters()
    {
        var rect = new PdfRectangle(0, 0, 100, 50);
        foreach (var (kind, key, value) in new (string, string, PdfObject)[]
                 {
                     ("Square", "IC", TestPdf.Box(0, 0, 1)), ("Line", "L", TestPdf.Box(0, 0, 100, 50)),
                     ("Ink", "InkList", new PdfArray([TestPdf.Box(0, 0, 10, 10, 20, 0)])),
                     ("Highlight", "QuadPoints", TestPdf.Box(0, 50, 100, 50, 0, 0, 100, 0)), ("Text", "Name", TestPdf.N("Note")),
                 })
        {
            var annot = TestPdf.Dict(("Subtype", TestPdf.N(kind)), ("C", TestPdf.Box(1, 0, 0)), ("CA", new PdfReal(0.5)), (key, value));
            var form = AnnotationAppearance.Build(annot, rect);
            Assert.NotNull(form);
            var states = form!.Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("ExtGState"));
            if (states is not null)
                foreach (var (name, _) in states.Entries)
                    foreach (var parameter in new[] { "TR", "TR2", "HT", "BG", "BG2", "UCR", "UCR2" })
                        Assert.False(states.GetDictionary(name)!.ContainsKey(PdfName.Get(parameter)), $"{kind}: /{parameter}");
            var content = Encoding.Latin1.GetString(form.RawData!);
            Assert.DoesNotContain("/TR", content);
        }
    }

    /// <summary>
    /// 8.6.1 sentences 16–17 (today's behaviour; the effect belongs to M8, 10.4–10.6): a graphics state with a transfer function (/TR, here one
    /// that inverts) does not change how „0.2 g“ fills — the fill is the same as without the state.
    /// </summary>
    [Fact]
    public void TransferFunctionInExtGState_DoesNotChangeTheFill()
    {
        const string states = "/ExtGState << /T << /TR << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >> >> >>";
        Assert.Equal(D_Fill("0.2 g 0 0 10 10 re f", states), D_Fill("/T gs 0.2 g 0 0 10 10 re f", states));
    }

    // ------------------------------------------------------------ 8.6.2, 8.6.3, 8.6.4

    /// <summary>
    /// 8.6.2 sentences 2–3 and 8.6.4.2 sentences 1–2: a grey value is one number from 0 (black) to 1 (white) — „0 g“, „0.5 g“, „1 g“ fill black,
    /// middle grey and white; „1.5 g“ like „1 g“.
    /// </summary>
    [Theory]
    [InlineData("0", 0xFF000000u)]
    [InlineData("0.5", 0xFF808080u)]
    [InlineData("1", 0xFFFFFFFFu)]
    [InlineData("1.5", 0xFFFFFFFFu)]
    public void DeviceGray_ZeroIsBlackOneIsWhite(string value, uint argb) =>
        Assert.Equal(argb, D_Fill($"{value} g 0 0 10 10 re f"));

    /// <summary>
    /// 8.6.3 sentences 14T–23T (Table 61): the families CalGray and DeviceGray are recognised — a fill in [/CalGray …] (D50, gamma 1) at 0.5 is the
    /// neutral grey of Y 0.5, one in DeviceGray at 0.25 the grey 64.
    /// </summary>
    [Fact]
    public void CalGrayAndDeviceGray_FillWithTheirColour()
    {
        D_Grey(D_In($"[/CalGray << {D_D50} >>]", "0.5"), 187, 189);
        Assert.Equal(0xFF404040u, D_Fill("/DeviceGray cs 0.25 sc 0 0 10 10 re f"));
    }

    /// <summary>
    /// 8.6.3 sentences 31T–33T (writer): the image a PDF/A-1 copy draws in place of translucent content gives its colour space as an array of its
    /// own (ICCBased), not as a name of the resources.
    /// </summary>
    [Fact]
    public void PdfA1Flattened_ImageGivesItsSpaceDirectly()
    {
        var result = D_PdfA(D_File("/H gs 1 0 0 rg 20 20 100 100 re f", "/ExtGState << /H << /ca 0.5 >> >>"), PdfAConformance.A1b);
        using var doc = PdfDocument.Open(result.Bytes);
        var images = new List<PdfStream>();
        void Collect(PdfDictionary? resources, int depth)
        {
            if (resources?.GetDictionary(PdfName.Get("XObject")) is not { } xobjects || depth > 4) return;
            foreach (var (key, _) in xobjects.Entries)
                if (xobjects.Get(key) is PdfStream s)
                {
                    if (s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image") images.Add(s);
                    else Collect(s.Dictionary.GetDictionary(PdfName.Resources), depth + 1);
                }
        }
        Collect(doc.Pages[0].Resources, 0);
        Assert.NotEmpty(images);
        foreach (var image in images)
        {
            var space = Assert.IsType<PdfArray>(image.Dictionary.Get(PdfName.Get("ColorSpace")));
            Assert.Equal("ICCBased", ((PdfName)space.Get(0)).Value);
        }
    }

    /// <summary>
    /// 8.6.3 sentences 31T–33T (reader, today's lenient reading): an image XObject naming its colour space (/CS0) is looked up in the resources of
    /// the page, and a colour space array written into the content („[/DeviceRGB] cs“) is used as well.
    /// </summary>
    [Fact]
    public void ColourSpaceByNameForAnImage_OrAsArrayInTheContent_ReadLeniently()
    {
        var file = D_File("q 100 0 0 100 0 100 cm /Im Do Q [/DeviceRGB] cs 0 1 0 sc 0 0 100 100 re f",
            "/ColorSpace << /CS0 /DeviceRGB >> /XObject << /Im 5 0 R >>", "",
            D_Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /CS0 /BitsPerComponent 8", "\u00FF\u0000\u0000"));
        Assert.True(D_Pixel(file, 50, 150) is { Red: > 240, Green: < 20, Blue: < 20 }, "Bild nicht rot");
        Assert.True(D_Pixel(file, 50, 50) is { Green: > 240, Red: < 20, Blue: < 20 }, "Fläche nicht grün");
    }

    /// <summary>
    /// 8.6.4.2 sentences 8–10: setting DeviceGray with cs gives the colour 0 (black) until sc; „0.5 G“ strokes grey; „/DeviceGray CS 0.5 SC“
    /// strokes the same as „0.5 G“.
    /// </summary>
    [Fact]
    public void DeviceGray_SettingTheSpaceGivesBlack_SeparateAndCombinedOperators()
    {
        Assert.Equal(0xFF000000u, D_Fill("/DeviceGray cs 0 0 10 10 re f"));
        Assert.Equal(0xFF808080u, D_Stroke("0.5 G 0 0 m 10 0 l S"));
        Assert.Equal(D_Stroke("0.5 G 0 0 m 10 0 l S"), D_Stroke("/DeviceGray CS 0.5 SC 0 0 m 10 0 l S"));
    }

    /// <summary>
    /// 8.6.4.3 sentences 8–11: setting DeviceRGB with cs gives red, green, blue 0 (black) until sc; „/DeviceRGB CS 0 1 0 SC“ strokes the same as
    /// „0 1 0 RG“.
    /// </summary>
    [Fact]
    public void DeviceRgb_SettingTheSpaceGivesBlack_SeparateAndCombinedOperators()
    {
        Assert.Equal(0xFF000000u, D_Fill("/DeviceRGB cs 0 0 10 10 re f"));
        Assert.Equal(0xFF00FF00u, D_Stroke("/DeviceRGB CS 0 1 0 SC 0 0 m 10 0 l S"));
        Assert.Equal(D_Stroke("0 1 0 RG 0 0 m 10 0 l S"), D_Stroke("/DeviceRGB CS 0 1 0 SC 0 0 m 10 0 l S"));
    }

    /// <summary>
    /// 8.6.4.4 sentences 8–12: setting DeviceCMYK with cs gives C, M, Y 0 and K 1 — the fill before any sc is that of „0 0 0 1 k“; the same in a
    /// document with a CMYK output profile (DeviceCMYK through that profile).
    /// </summary>
    [Fact]
    public void DeviceCmyk_SettingTheSpaceGivesTheBlackOfK1()
    {
        Assert.Equal(D_Fill("0 0 0 1 k 0 0 10 10 re f"), D_Fill("/DeviceCMYK cs 0 0 10 10 re f"));
        const string intent = "/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (Probe) /DestOutputProfile 5 0 R >>]";
        var profile = D_Stream("/N 4", D_NaiveCmyk.Value);
        var black = D_Fill("0 0 0 1 k 0 0 10 10 re f", "", intent, profile);
        Assert.Equal(0xFF000000u, black);   // the profile's own black, not the bundled one
        Assert.Equal(black, D_Fill("/DeviceCMYK cs 0 0 10 10 re f", "", intent, profile));
    }

    // ------------------------------------------------------------ 8.6.5.1 to 8.6.5.4

    /// <summary>
    /// 8.6.5.1 sentence 27: setting a CIE-based space gives 0 per component, the nearest valid value where 0 lies outside the range — Lab with
    /// /Range [10 20 10 20] fills as „0 10 10 sc“, CalRGB as black.
    /// </summary>
    [Fact]
    public void CieSpaces_InitialColourZeroOrTheNearestValid()
    {
        const string lab = $"/ColorSpace << /L [/Lab << {D_D50} /Range [10 20 10 20] >>] /C [/CalRGB << {D_D65} >>] >>";
        Assert.Equal(D_Fill("/L cs 0 10 10 sc 0 0 10 10 re f", lab), D_Fill("/L cs 0 0 10 10 re f", lab));
        Assert.Equal(0xFF000000u, D_Fill("/C cs 0 0 10 10 re f", lab));
    }

    /// <summary>
    /// 8.6.5.2 sentences 6–14 and 28T–31T: CalGray decodes A by its gamma, then scales by the white point — A 0.5 with gamma 2.2 under D65 is
    /// Y = 0.5^2.2 = 0.2176, a neutral grey (X and Z in the ratio of the white point) of sRGB 127–129; A 1.5 as A 1, A −0.2 as A 0 (clamped
    /// without notice).
    /// </summary>
    [Fact]
    public void CalGray_GammaThenWhitePoint_ClampedToZeroOne()
    {
        const string space = $"[/CalGray << {D_D65} /Gamma 2.2 >>]";
        D_Grey(D_In(space, "0.5"), 127, 129);
        Assert.Equal(D_In(space, "1"), D_In(space, "1.5"));
        Assert.Equal(D_In(space, "0"), D_In(space, "-0.2"));
        Assert.Equal(((byte)0, (byte)0, (byte)0), D_In(space, "-0.2"));
    }

    /// <summary>
    /// 8.6.5.2 sentences 15T–19T: the white point is the diffuse white — CalGray under D65 at A 1 is white (255, 255, 255); without /WhitePoint
    /// or with YW 0 it is read as D50 (today's lenient reading).
    /// </summary>
    [Fact]
    public void CalGray_WhitePoint_D65WhiteIsWhite_MissingOrYZeroAsD50()
    {
        Assert.Equal(((byte)255, (byte)255, (byte)255), D_In($"[/CalGray << {D_D65} >>]", "1"));
        var d50 = D_In($"[/CalGray << {D_D50} >>]", "0.5");
        Assert.Equal(d50, D_In("[/CalGray << /Gamma 1 >>]", "0.5"));
        Assert.Equal(d50, D_In("[/CalGray << /WhitePoint [0.9505 0 1.089] >>]", "0.5"));
    }

    /// <summary>
    /// 8.6.5.2 sentences 20T–23T (today's behaviour, /BlackPoint: design decision pending): CalGray with /BlackPoint [0.1 0.1 0.1] gives the same
    /// colours as without — A 0 black, A 0.5 the same grey.
    /// </summary>
    [Fact]
    public void CalGray_BlackPoint_TodayWithoutEffect()
    {
        const string with = $"[/CalGray << {D_D50} /BlackPoint [0.1 0.1 0.1] >>]";
        Assert.Equal(((byte)0, (byte)0, (byte)0), D_In(with, "0"));
        Assert.Equal(D_In($"[/CalGray << {D_D50} >>]", "0.5"), D_In(with, "0.5"));
    }

    /// <summary>8.6.5.2 sentences 24T–27T: without /Gamma the gamma is 1 — CalGray (D50) at A 0.5 is Y 0.5, sRGB 187–189.</summary>
    [Fact]
    public void CalGray_WithoutGamma_IsLinear() => D_Grey(D_In($"[/CalGray << {D_D50} >>]", "0.5"), 187, 189);

    /// <summary>8.6.5.3 sentences 1–6: CalRGB clamps A, B, C to 0–1 without notice — „1.5 −0.2 0.5 sc“ fills as „1 0 0.5 sc“.</summary>
    [Fact]
    public void CalRgb_ValuesClampedToZeroOne()
    {
        const string space = $"[/CalRGB << {D_D65} /Gamma [2.2 2.2 2.2] /Matrix [0.4124 0.2126 0.0193 0.3576 0.7152 0.1192 0.1805 0.0722 0.9505] >>]";
        Assert.Equal(D_In(space, "1 0 0.5"), D_In(space, "1.5 -0.2 0.5"));
    }

    /// <summary>
    /// 8.6.5.3 sentences 12T–14T and 22T–28T (today's behaviour, /BlackPoint: design decision pending): CalRGB with /BlackPoint [0.05 0.05 0.05]
    /// gives the same colours as without — the black point is not mapped.
    /// </summary>
    [Fact]
    public void CalRgb_BlackPoint_TodayWithoutEffect()
    {
        const string matrix = "/Matrix [0.4124 0.2126 0.0193 0.3576 0.7152 0.1192 0.1805 0.0722 0.9505]";
        const string with = $"[/CalRGB << {D_D65} {matrix} /BlackPoint [0.05 0.05 0.05] >>]";
        const string without = $"[/CalRGB << {D_D65} {matrix} >>]";
        foreach (var values in new[] { "0 0 0", "0.2 0.4 0.6" })
            Assert.Equal(D_In(without, values), D_In(with, values));
        Assert.Equal(((byte)0, (byte)0, (byte)0), D_In(with, "0 0 0"));
    }

    /// <summary>
    /// 8.6.5.3 sentences 15T–17T: without /Gamma the gammas are [1 1 1] — CalRGB with an sRGB-like matrix (D65) at 0.5 0.5 0.5 is linear 0.5,
    /// sRGB grey 187–189.
    /// </summary>
    [Fact]
    public void CalRgb_WithoutGamma_IsLinear() =>
        D_Grey(D_In($"[/CalRGB << {D_D65} /Matrix [0.4124 0.2126 0.0193 0.3576 0.7152 0.1192 0.1805 0.0722 0.9505] >>]", "0.5 0.5 0.5"), 187, 189);

    /// <summary>
    /// 8.6.5.3 sentences 18T–21T: without /Matrix the matrix is the identity — CalRGB (D50) at 1 0 0 (XYZ 1 0 0) and at 0.3 0.6 0.2 fills as with
    /// [1 0 0 0 1 0 0 0 1].
    /// </summary>
    [Fact]
    public void CalRgb_WithoutMatrix_IsTheIdentity()
    {
        foreach (var values in new[] { "1 0 0", "0.3 0.6 0.2" })
            Assert.Equal(D_In($"[/CalRGB << {D_D50} /Matrix [1 0 0 0 1 0 0 0 1] >>]", values), D_In($"[/CalRGB << {D_D50} >>]", values));
    }

    /// <summary>
    /// 8.6.5.4 sentences 1–4: Lab clamps L* to 0–100 and a*, b* to /Range without notice — (D50, range ±100) „150 0 0“ as „100 0 0“, „50 200 0“
    /// as „50 100 0“.
    /// </summary>
    [Fact]
    public void Lab_ValuesClampedToTheirRanges()
    {
        const string space = $"[/Lab << {D_D50} /Range [-100 100 -100 100] >>]";
        Assert.Equal(D_In(space, "100 0 0"), D_In(space, "150 0 0"));
        Assert.Equal(D_In(space, "50 100 0"), D_In(space, "50 200 0"));
    }

    /// <summary>
    /// 8.6.5.4 sentences 15T–22T: below 6/29 the function g is the straight line 108/841 · (x − 4/29) — L* 5, a* = b* = 0 under D50 is
    /// Y ≈ 0.0055, sRGB 17–19.
    /// </summary>
    [Fact]
    public void Lab_LowerBranchOfG() => D_Grey(D_In($"[/Lab << {D_D50} >>]", "5 0 0"), 17, 19);

    /// <summary>
    /// 8.6.5.4 sentences 27T–28T (today's lenient reading): Lab without /WhitePoint, or with YW 0, is read as D65 — the same colours as with
    /// [0.9505 1 1.089].
    /// </summary>
    [Fact]
    public void Lab_WhitePointMissingOrYZero_AsD65()
    {
        var d65 = D_In($"[/Lab << {D_D65} >>]", "50 20 -20");
        Assert.Equal(d65, D_In("[/Lab << >>]", "50 20 -20"));
        Assert.Equal(d65, D_In("[/Lab << /WhitePoint [0.9505 0 1.089] >>]", "50 20 -20"));
    }

    /// <summary>
    /// 8.6.5.4 sentences 29T–31T (today's behaviour, /BlackPoint: design decision pending): Lab (D50) with /BlackPoint [0.05 0.05 0.05] — „0 0 0“
    /// is black as without.
    /// </summary>
    [Fact]
    public void Lab_BlackPoint_TodayWithoutEffect()
    {
        Assert.Equal(((byte)0, (byte)0, (byte)0), D_In($"[/Lab << {D_D50} /BlackPoint [0.05 0.05 0.05] >>]", "0 0 0"));
        Assert.Equal(D_In($"[/Lab << {D_D50} >>]", "40 10 10"), D_In($"[/Lab << {D_D50} /BlackPoint [0.05 0.05 0.05] >>]", "40 10 10"));
    }

    // ------------------------------------------------------------ 8.6.5.5 ICCBased

    /// <summary>
    /// 8.6.5.5 sentences 11T–13T (reader): /N must match the profile — a CMYK profile with /N 3 and /Alternate /DeviceRGB draws as DeviceRGB;
    /// /N 2 without /Alternate today as DeviceRGB.
    /// </summary>
    [Fact]
    public void IccBased_NNotMatchingTheProfile_TakesTheAlternate()
    {
        Assert.Equal(((byte)255, (byte)0, (byte)0), D_In("[/ICCBased 5 0 R]", "1 0 0", D_Stream("/N 3 /Alternate /DeviceRGB", D_NaiveCmyk.Value)));
        Assert.Equal(((byte)0, (byte)255, (byte)0), D_In("[/ICCBased 5 0 R]", "0 1 0", D_Stream("/N 2", D_NaiveCmyk.Value)));
    }

    /// <summary>
    /// 8.6.5.5 sentences 11T–13T (writer): the output profile of a PDF/A copy carries the /N of its profile — a CMYK output profile stated with
    /// /N 3 is written with /N 4.
    /// </summary>
    [Fact]
    public void PdfA_OutputProfileWithWrongN_WrittenWithTheNOfTheProfile()
    {
        var file = D_File("0 0 0 1 k 0 0 10 10 re f", "",
            "/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (Probe) /DestOutputProfile 5 0 R >>]",
            D_Stream("/N 3", D_NaiveCmyk.Value));
        var result = D_PdfA(file, PdfAConformance.A2b);
        using var doc = PdfDocument.Open(result.Bytes);
        var intent = (PdfDictionary)doc.Catalog!.GetArray(PdfName.Get("OutputIntents"))!.Get(0);
        Assert.Equal(4, intent.GetStream(PdfName.Get("DestOutputProfile"))!.Dictionary.GetInteger(PdfName.N));
    }

    /// <summary>
    /// 8.6.5.5 sentences 19T–20T: with an unreadable profile the alternate space applies, and values outside its range become the nearest valid
    /// ones without notice — „1.5 −0.2 0.5 sc“ in /Alternate /DeviceRGB fills (255, 0, 128).
    /// </summary>
    [Fact]
    public void IccBased_UnreadableProfile_AlternateClampsTheValues() =>
        Assert.Equal(((byte)255, (byte)0, (byte)128), D_In("[/ICCBased 5 0 R]", "1.5 -0.2 0.5", D_Stream("/N 3 /Alternate /DeviceRGB", "nicht lesbar")));

    /// <summary>
    /// 8.6.5.5 sentences 21T–23T and 58T–59T: /Range gives each component its range; for a profile of data colour space Lab it is L* 0–100, a* and
    /// b* −128–127 — „50 0 0 sc“ in such a space (/Range [0 100 −128 127 −128 127]) is a neutral middle grey. (The profile itself turns its
    /// encoded values 0.5, 0.5, 0.5 into that grey.)
    /// </summary>
    [Fact]
    public void IccBasedLab_RangeGivesTheValues()
    {
        var profile = D_LabProfile();
        var transform = new IccTransform(IccProfile.Parse(profile)!, compensateBlack: false);
        Span<float> rgb = stackalloc float[3];
        transform.ToSrgb([0.5f, 128 / 255f, 128 / 255f], rgb);
        Assert.True(Math.Abs(rgb[0] - rgb[2]) < 0.03 && rgb[0] is > 0.4f and < 0.53f, $"Profil: {rgb[0]} {rgb[1]} {rgb[2]}");
        D_Grey(D_In("[/ICCBased 5 0 R]", "50 0 0", D_Stream("/N 3 /Range [0 100 -128 127 -128 127]", profile)), 112, 126);
    }

    /// <summary>
    /// 8.6.5.5 sentences 24T–25T (today's behaviour; metadata streams belong to M11, removing them to M16): an ICC stream's /Metadata is written
    /// when saving and removed by the clean-up of metadata.
    /// </summary>
    [Fact]
    public void IccStreamMetadata_KeptOnSaving_RemovedByTheCleanUp()
    {
        var file = D_File("/CS0 cs 0 1 0 0 sc 0 0 10 10 re f", "/ColorSpace << /CS0 [/ICCBased 5 0 R] >>", "",
            D_Stream("/N 4 /Metadata 6 0 R", D_NaiveCmyk.Value),
            D_Stream("/Type /Metadata /Subtype /XML", "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"));
        PdfStream Profile(PdfDocument doc) =>
            (PdfStream)((PdfArray)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("ColorSpace"))!.Get(PdfName.Get("CS0"))).Get(1);
        using (var kept = DocumentStructureNormTests.Saved(file))
            Assert.IsType<PdfStream>(Profile(kept).Dictionary.Get(PdfName.Metadata));
        using var cleaned = DocumentStructureNormTests.Saved(file, new PdfSaveOptions { Cleanup = CleanupItems.Metadata });
        Assert.False(Profile(cleaned).Dictionary.ContainsKey(PdfName.Metadata));
    }

    /// <summary>
    /// 8.6.5.5 sentences 43T–47T and 78T (writer, Table 67): the profiles PDF-Knead writes are of the classes and colour spaces the table allows —
    /// sRGB, ROMM and calibrated RGB: display (mntr), RGB; grey: display, GRAY; CMYK: output (prtr), CMYK.
    /// </summary>
    [Fact]
    public void OwnProfiles_ClassAndColourSpaceOfTable67()
    {
        static (uint Class, uint Space) Head(byte[] p) => (BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12)), BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(16)));
        const uint mntr = 0x6D6E7472, prtr = 0x70727472;
        foreach (var rgb in new[] { CalibratedProfile.Srgb(), CalibratedProfile.RommRgb(), CalibratedProfile.Rgb(null) })
            Assert.Equal((mntr, IccProfile.RgbSpace), Head(rgb));
        foreach (var grey in new[] { CalibratedProfile.SrgbGray(), CalibratedProfile.Gray(null) })
            Assert.Equal((mntr, IccProfile.GraySpace), Head(grey));
        Assert.Equal((prtr, IccProfile.CmykSpace), Head(CmykProfile.Bytes()));
    }

    /// <summary>
    /// 8.6.5.5 sentence 78T (reader, today's lenient reading): a profile of the class DeviceLink (link) or Abstract (abst) is read like the others —
    /// its table gives the colour (magenta for 0 1 0 0).
    /// </summary>
    [Theory]
    [InlineData(0x6C696E6Bu)]
    [InlineData(0x61627374u)]
    public void ProfileOfClassLinkOrAbstract_ReadToday(uint deviceClass)
    {
        var profile = (byte[])D_SwappedCmyk.Value.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(12), deviceClass);
        Assert.NotNull(IccProfile.Parse(profile));
        var c = D_In("[/ICCBased 5 0 R]", "1 0 0 0", D_Stream("/N 4", profile));   // the swapped table: cyan comes out magenta
        Assert.True(c is { R: > 230, G: < 25, B: > 230 }, $"{c}");
    }

    /// <summary>
    /// 8.6.5.5 sentences 48T–57T (Table 68): an ICC RGB space (AdobeRGB-like) starts at 0 per component — the fill before any sc is that of
    /// „0 0 0 sc“ —, and its range is 0–1: „1.2 0.5 0.5 sc“ fills as „1 0.5 0.5 sc“.
    /// </summary>
    [Fact]
    public void IccBasedRgb_InitialZero_ValuesWithinZeroOne()
    {
        var profile = D_Stream("/N 3", D_MatrixProfile(D_AdobeColumns, 2.2f));
        const string resources = "/ColorSpace << /CS0 [/ICCBased 5 0 R] >>";
        Assert.Equal(D_Fill("/CS0 cs 0 0 0 sc 0 0 10 10 re f", resources, "", profile), D_Fill("/CS0 cs 0 0 10 10 re f", resources, "", profile));
        Assert.Equal(D_In("[/ICCBased 5 0 R]", "1 0.5 0.5", profile), D_In("[/ICCBased 5 0 R]", "1.2 0.5 0.5", profile));
    }

    /// <summary>
    /// 8.6.5.5 sentences 60T–61T: as a source only the AToB tables count — a profile with A2B0 and a different B2A0 (here the table that swaps cyan
    /// and magenta) gives the colours of A2B0 alone.
    /// </summary>
    [Fact]
    public void IccBased_OnlyAToBCounts_BToAPassedOver()
    {
        var plain = D_In("[/ICCBased 5 0 R]", "0 1 0 0", D_Stream("/N 4", D_NaiveCmyk.Value));
        var withB2A = D_WithTag(D_NaiveCmyk.Value, 0x42324130, D_Tag(D_SwappedCmyk.Value, 0x41324230));
        Assert.True(plain is { R: > 230, G: < 25, B: > 230 }, $"{plain}");
        Assert.Equal(plain, D_In("[/ICCBased 5 0 R]", "0 1 0 0", D_Stream("/N 4", withB2A)));
    }

    // ------------------------------------------------------------ 8.6.5.6 default colour spaces

    /// <summary>A CalRGB space (D50, linear) whose first component is the sRGB blue primary and whose third is the red: red and blue swapped.</summary>
    private const string D_RedBlueSwapped =
        $"[/CalRGB << {D_D50} /Matrix [0.1431 0.0606 0.7141 0.3851 0.7169 0.0971 0.4361 0.2225 0.0139] >>]";

    /// <summary>
    /// 8.6.5.6 sentences 5–9: the default space replaces the device space for every object — also for a shading in /DeviceRGB painted with „sh“ and
    /// an inline image in /RGB: under a /DefaultRGB that swaps red and blue both come out blue. A device space given as an array ([/DeviceRGB])
    /// is today not replaced (red).
    /// </summary>
    [Fact]
    public void DefaultRgb_AppliesToShadingAndInlineImage_NotToAnArray()
    {
        var file = D_File(
            "q 0 0 100 100 re W n /Sh sh Q q 100 0 0 100 100 0 cm BI /W 1 /H 1 /CS /RGB /BPC 8 ID \u00FF\u0000\u0000 EI Q /A cs 1 0 0 sc 0 100 100 100 re f",
            $"/ColorSpace << /DefaultRGB {D_RedBlueSwapped} /A [/DeviceRGB] >> /Shading << /Sh << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [1 0 0] /N 1 >> /Extend [true true] >> >>");
        Assert.True(D_Pixel(file, 50, 50) is { Blue: > 200, Red: < 60 }, $"sh: {D_Pixel(file, 50, 50)}");
        Assert.True(D_Pixel(file, 150, 50) is { Blue: > 200, Red: < 60 }, $"Inline-Bild: {D_Pixel(file, 150, 50)}");
        Assert.True(D_Pixel(file, 50, 150) is { Red: > 200, Blue: < 60 }, $"[/DeviceRGB]: {D_Pixel(file, 50, 150)}");
    }

    /// <summary>
    /// 8.6.5.6 sentences 12–15: a default space must have as many components as the device space and must not be Pattern or Indexed — a
    /// /DefaultRGB of four components (ICC CMYK) or an Indexed space leaves DeviceRGB: „1 0 0 rg“ stays red.
    /// </summary>
    [Fact]
    public void DefaultRgb_OtherComponentsOrIndexed_DeviceRgbStays()
    {
        var cmyk = D_Stream("/N 4", D_NaiveCmyk.Value);
        Assert.Equal(0xFFFF0000u, D_Fill("1 0 0 rg 0 0 10 10 re f", "/ColorSpace << /DefaultRGB [/ICCBased 5 0 R] >>", "", cmyk));
        Assert.Equal(0xFFFF0000u, D_Fill("1 0 0 rg 0 0 10 10 re f", "/ColorSpace << /DefaultRGB [/Indexed /DeviceRGB 0 <0000FF>] >>"));
    }

    /// <summary>
    /// 8.6.5.6 sentences 16–18: the default space stands in for a device space inside special spaces — the base of Indexed ([/Indexed /DeviceRGB 0
    /// &lt;FF0000&gt;] under a /DefaultRGB swapping red and blue: blue), the alternate space of Separation ([/Separation /X /DeviceCMYK …] towards
    /// cyan under a /DefaultCMYK swapping cyan and magenta: magenta) and the base of Pattern (an uncoloured pattern in [/Pattern /DeviceRGB] at
    /// 1 0 0: blue).
    /// </summary>
    [Fact]
    public void DefaultSpaces_StandInForTheBaseOfSpecialSpaces()
    {
        var indexed = D_Rgb(D_Fill("/Ix cs 0 sc 0 0 10 10 re f", $"/ColorSpace << /DefaultRGB {D_RedBlueSwapped} /Ix [/Indexed /DeviceRGB 0 <FF0000>] >>"));
        Assert.True(indexed is { B: > 200, R: < 60 }, $"Indexed: {indexed}");
        var separation = D_Rgb(D_Fill("/S cs 1 sc 0 0 10 10 re f",
            "/ColorSpace << /DefaultCMYK [/ICCBased 5 0 R] /S [/Separation /X /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>] >>",
            "", D_Stream("/N 4", D_SwappedCmyk.Value)));
        Assert.True(separation is { R: > 230, G: < 25, B: > 230 }, $"Separation: {separation}");
        var file = D_File("/P cs 1 0 0 /P0 scn 0 0 200 200 re f",
            $"/ColorSpace << /DefaultRGB {D_RedBlueSwapped} /P [/Pattern /DeviceRGB] >> /Pattern << /P0 5 0 R >>", "",
            D_Stream("/Type /Pattern /PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources << >>", "0 0 20 20 re f"));
        Assert.True(D_Pixel(file, 105, 105) is { Blue: > 200, Red: < 60 }, $"Muster: {D_Pixel(file, 105, 105)}");
    }

    /// <summary>
    /// 8.6.5.6 sentences 19–22 (reader, today's lenient reading): a /DefaultRGB of the family Lab is taken — „50 0 0 rg“ fills the grey of L* 50
    /// instead of the red of DeviceRGB.
    /// </summary>
    [Fact]
    public void DefaultRgb_Lab_TakenToday()
    {
        var c = D_Rgb(D_Fill("50 0 0 rg 0 0 10 10 re f", $"/ColorSpace << /DefaultRGB [/Lab << {D_D50} >>] >>"));
        D_Grey(c, 110, 130);
    }

    // ------------------------------------------------------------ 8.6.5.7, 8.6.5.8

    /// <summary>
    /// 8.6.5.7 sentences 13–17 (today's behaviour): an ICCBased CMYK space with the very profile of the output intent is no DeviceCMYK — with /OP,
    /// /op and /OPM 1 and the overprint preview on, yellow „0 0 1 0 sc“ over cyan covers instead of mixing to green.
    /// </summary>
    [Fact]
    public void IccCmykSameAsTheOutputProfile_Opm1_DoesNotMixToday()
    {
        var file = D_File("1 0 0 0 k 0 0 120 200 re f /OP1 gs /CS0 cs 0 0 1 0 sc 80 0 120 200 re f",
            "/ColorSpace << /CS0 [/ICCBased 5 0 R] >> /ExtGState << /OP1 << /OP true /op true /OPM 1 >> >>",
            "/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (Probe) /DestOutputProfile 5 0 R >>]",
            D_Stream("/N 4", D_NaiveCmyk.Value));
        var c = D_Pixel(file, 100, 100, overprintPreview: true);
        Assert.True(c is { Red: > 200, Green: > 200, Blue: < 60 }, $"Gelb erwartet: {c}");
    }

    /// <summary>
    /// 8.6.5.8 sentence 33T: an unknown rendering intent is relative colorimetric — in an ICC CMYK space whose perceptual table swaps cyan and
    /// magenta, „/Foo ri“ (also after „/Perceptual ri“) and /RI 5 in a graphics state leave magenta „0 1 0 0“ magenta.
    /// </summary>
    [Theory]
    [InlineData("/Foo ri")]
    [InlineData("/Perceptual ri /Foo ri")]
    [InlineData("/R5 gs")]
    public void UnknownRenderingIntent_IsRelativeColorimetric(string intent)
    {
        var c = D_Rgb(D_Fill($"{intent} /CS0 cs 0 1 0 0 sc 0 0 10 10 re f", "/ColorSpace << /CS0 [/ICCBased 5 0 R] >> /ExtGState << /R5 << /RI 5 >> >>", "",
            D_Stream("/N 4", D_SwappedAndPlain.Value)));
        Assert.True(c is { R: > 230, G: < 25, B: > 230 }, $"nicht Magenta: {c}");
    }
}
