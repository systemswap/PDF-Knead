using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

// M6 draft f-867-868-88 (ISO 32000-2, 8.6.7 overprint, 8.6.8 colour operators, 8.8.1 external objects): the rows whose evidence was missing.
public sealed partial class M6NormTests
{
    // ------------------------------------------------------------ helpers (package f)

    /// <summary>A stream object as text: its dictionary entries and data (Latin-1, one byte per character).</summary>
    private static string F_Stream(string dict, string data) => $"<< {dict} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static string F_Stream(string dict, byte[] data) => F_Stream(dict, Encoding.Latin1.GetString(data));

    /// <summary>A file with one page of 200 × 200: its content and the entries of its resources; further objects are numbered from 5.</summary>
    private static byte[] F_File(string content, string resources = "", params string[] objects) =>
        DocumentStructureNormTests.FileOf([
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << {resources} >> /Contents 4 0 R >>",
            F_Stream("", content), .. objects]);

    /// <summary>The paint (ARGB) of the first entry that fills, strokes or draws a glyph.</summary>
    private static uint F_Paint(string content, string resources = "", bool stroke = false, params string[] objects)
    {
        using var doc = PdfDocument.Open(F_File(content, resources, objects));
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        var item = list.Items.ToArray().First(i => stroke ? i.Kind is DisplayItemKind.Stroke or DisplayItemKind.FillStroke
            : i.Kind is DisplayItemKind.Fill or DisplayItemKind.FillStroke);
        return stroke ? list.Paints[item.Paint].Stroke : list.Paints[item.Paint].Fill;
    }

    private static uint F_Fill(string content, string resources = "", params string[] objects) => F_Paint(content, resources, false, objects);

    private static uint F_Stroke(string content, string resources = "", params string[] objects) => F_Paint(content, resources, true, objects);

    private static SKColor F_Pixel(byte[] file, int x, int y, bool overprintPreview = false)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache(), overprintPreview: overprintPreview);
        return bitmap.GetPixel(x, bitmap.Height - 1 - y);
    }

    private static bool F_White(SKColor c) => c is { Red: > 250, Green: > 250, Blue: > 250 };

    private static bool F_Yellow(SKColor c) => c is { Red: > 200, Green: > 200, Blue: < 60 };

    private static bool F_Greenish(SKColor c) => c is { Green: > 120, Red: < 60, Blue: < 100 };

    private static bool F_Blue(SKColor c) => c is { Blue: > 200, Red: < 60, Green: < 60 };

    /// <summary>Whether the whole rendered page is white.</summary>
    private static bool F_Blank(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (!F_White(bitmap.GetPixel(x, y))) return false;
        return true;
    }

    /// <summary>Graphics states for overprint: OP1 (/OP, /op, /OPM 1), OPOnly (/OP, /op without /OPM).</summary>
    private const string F_States = "/ExtGState << /OP1 << /OP true /op true /OPM 1 >> /OPOnly << /OP true /op true >> >>";

    private static readonly Lazy<byte[]> F_NaiveCmyk = new(() => CmykProfile.Bake((c, m, y, k) => ((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k))));

    /// <summary>A grey profile (mntr, GRAY, PCS XYZ) with a linear tone curve.</summary>
    private static byte[] F_GreyProfile()
    {
        var curve = new byte[14];
        BinaryPrimitives.WriteUInt32BigEndian(curve, 0x63757276);
        BinaryPrimitives.WriteUInt32BigEndian(curve.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12), 0x0100);
        var d = new byte[128 + 4 + 12 + curve.Length];
        BinaryPrimitives.WriteUInt32BigEndian(d, (uint)d.Length);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), 0x02100000);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(12), 0x6D6E7472);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), IccProfile.GraySpace);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), 0x58595A20);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(36), 0x61637370);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(128), 1);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(132), 0x6B545243);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(136), 144);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(140), (uint)curve.Length);
        curve.CopyTo(d, 144);
        return d;
    }

    /// <summary>An RGB matrix profile with the AdobeRGB primaries (D50) and gamma 2.2, version 2.1, mntr.</summary>
    private static byte[] F_AdobeProfile()
    {
        float[] columns = [0.6097559f, 0.2052401f, 0.1492240f, 0.3111242f, 0.6256560f, 0.0632197f, 0.0194811f, 0.0608902f, 0.7448387f];
        var tags = new List<(uint Sig, byte[] Data)>();
        uint[] xyzSigs = [0x7258595A, 0x6758595A, 0x6258595A];
        uint[] trcSigs = [0x72545243, 0x67545243, 0x62545243];
        for (var c = 0; c < 3; c++)
        {
            var xyz = new byte[20];
            BinaryPrimitives.WriteUInt32BigEndian(xyz, 0x58595A20);
            for (var r = 0; r < 3; r++) BinaryPrimitives.WriteInt32BigEndian(xyz.AsSpan(8 + 4 * r), (int)Math.Round(columns[r * 3 + c] * 65536));
            tags.Add((xyzSigs[c], xyz));
        }
        for (var c = 0; c < 3; c++)
        {
            var curve = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(curve, 0x63757276);
            BinaryPrimitives.WriteUInt32BigEndian(curve.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12), (ushort)Math.Round(2.2 * 256));
            tags.Add((trcSigs[c], curve));
        }
        var size = 128 + 4 + 12 * tags.Count + tags.Sum(t => t.Data.Length);
        var d = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(d, (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), 0x02100000);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(12), 0x6D6E7472);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), IccProfile.RgbSpace);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), 0x58595A20);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(36), 0x61637370);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(128), (uint)tags.Count);
        var at = 128 + 4 + 12 * tags.Count;
        for (var i = 0; i < tags.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(132 + 12 * i), tags[i].Sig);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(136 + 12 * i), (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(140 + 12 * i), (uint)tags[i].Data.Length);
            tags[i].Data.CopyTo(d, at);
            at += tags[i].Data.Length;
        }
        return d;
    }

    /// <summary>Separation towards cyan in DeviceCMYK.</summary>
    private const string F_Cyan = "[/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>]";

    /// <summary>
    /// A page drawing the letter „a“ of a Type 3 font (object 5) at 100 pt from (50, 50) — its glyph a d1 procedure (object 6) over the square 0…1000
    /// of glyph space, so the page square 50…150 —, after <paramref name="before"/> (the text colour); <paramref name="fontResources"/> are the
    /// resources of the font, further objects numbered from 7.
    /// </summary>
    private static byte[] F_Glyph(string procedure, string fontResources = "", string before = "0 0 1 rg", string pageResources = "", params string[] objects) =>
        F_File($"{before} BT /F3 100 Tf 50 50 Td (a) Tj ET", $"/Font << /F3 5 0 R >> {pageResources}", [
            "<< /Type /Font /Subtype /Type3 /FontMatrix [0.001 0 0 0.001 0 0] /FontBBox [0 0 1000 1000] /CharProcs << /g 6 0 R >> " +
            $"/Encoding << /Differences [97 /g] >> /FirstChar 97 /LastChar 97 /Widths [1000] /Resources << {fontResources} >> >>",
            F_Stream("", "1000 0 0 0 1000 1000 d1 " + procedure), .. objects]);

    // ------------------------------------------------------------ 8.6.7 overprint

    /// <summary>
    /// 8.6.7 sentences 7–9: overprint is false by default — without any graphics state, yellow „0 0 1 0 k“ over cyan covers in the overprint
    /// preview: what was painted last applies.
    /// </summary>
    [Fact]
    public void Overprint_FalseByDefault_LastPaintCovers() =>
        Assert.True(F_Yellow(F_Pixel(F_File("1 0 0 0 k 0 0 120 200 re f 0 0 1 0 k 80 0 120 200 re f"), 100, 100, overprintPreview: true)));

    /// <summary>
    /// 8.6.7 sentence 18: whether a tone is 0 is decided by the value in the file — with /OP, /op, /OPM 1 in the preview, „0 0 1 0 k“ over cyan mixes
    /// to green, „0.0001 0.0001 1 0.0001 k“ (no component 0) covers: yellow.
    /// </summary>
    [Fact]
    public void OverprintMode1_ZeroAsWrittenInTheFile()
    {
        Assert.True(F_Greenish(F_Pixel(F_File("1 0 0 0 k 0 0 120 200 re f /OP1 gs 0 0 1 0 k 80 0 120 200 re f", F_States), 100, 100, true)));
        var almost = F_Pixel(F_File("1 0 0 0 k 0 0 120 200 re f /OP1 gs 0.0001 0.0001 1 0.0001 k 80 0 120 200 re f", F_States), 100, 100, true);
        Assert.True(F_Yellow(almost), $"Gelb erwartet: {almost}");
    }

    /// <summary>
    /// 8.6.7 sentences 19–20: the overprint mode is 0 by default — a graphics state with only /OP and /op true, „0 0 1 0 k“ over cyan in the preview:
    /// yellow (every component replaces the previous one).
    /// </summary>
    [Fact]
    public void OverprintMode_ZeroByDefault() =>
        Assert.True(F_Yellow(F_Pixel(F_File("1 0 0 0 k 0 0 120 200 re f /OPOnly gs 0 0 1 0 k 80 0 120 200 re f", F_States), 100, 100, true)));

    /// <summary>
    /// 8.6.7 sentence 31: mode 1 applies only where the current space is DeviceCMYK — in an ICCBased CMYK space „0 0 1 0 sc“ over cyan with /OP,
    /// /op, /OPM 1 in the preview covers: yellow.
    /// </summary>
    [Fact]
    public void OverprintMode1_NotForAnIccCmykSpace()
    {
        var c = F_Pixel(F_File("1 0 0 0 k 0 0 120 200 re f /OP1 gs /CS0 cs 0 0 1 0 sc 80 0 120 200 re f", $"{F_States} /ColorSpace << /CS0 [/ICCBased 5 0 R] >>",
            F_Stream("/N 4", F_NaiveCmyk.Value)), 100, 100, true);
        Assert.True(F_Yellow(c), $"Gelb erwartet: {c}");
    }

    /// <summary>
    /// 8.6.7 sentence 32: mode 1 does not apply to shadings — a DeviceCMYK shading (yellow) painted with „sh“ after /OP, /op, /OPM 1 and „0 0 0 1 k“,
    /// over cyan in the preview, covers: yellow.
    /// </summary>
    [Fact]
    public void OverprintMode1_NotForShadings()
    {
        var c = F_Pixel(F_File("1 0 0 0 k 0 0 120 200 re f /OP1 gs 0 0 0 1 k q 80 0 120 200 re W n /Sh sh Q",
            $"{F_States} /Shading << /Sh << /ShadingType 2 /ColorSpace /DeviceCMYK /Coords [0 0 200 0] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 1 0] /C1 [0 0 1 0] /N 1 >> /Extend [true true] >> >>"), 100, 100, true);
        Assert.True(F_Yellow(c), $"Gelb erwartet: {c}");
    }

    // ------------------------------------------------------------ 8.6.8 colour operators

    /// <summary>
    /// 8.6.8 sentences 3T–10T (CS): the device spaces may be named directly — „/DeviceRGB CS 1 0 0 SC“ strokes red, „/DeviceCMYK CS 0 0 1 0 SC“ as
    /// „0 0 1 0 K“.
    /// </summary>
    [Fact]
    public void CS_DeviceSpacesNamedDirectly()
    {
        Assert.Equal(0xFFFF0000u, F_Stroke("/DeviceRGB CS 1 0 0 SC 0 0 m 10 0 l S"));
        Assert.Equal(F_Stroke("0 0 1 0 K 0 0 m 10 0 l S"), F_Stroke("/DeviceCMYK CS 0 0 1 0 SC 0 0 m 10 0 l S"));
    }

    /// <summary>
    /// 8.6.8 sentences 11T–15T: the name DeviceRGB always means the device space, never an entry of the resources — with /ColorSpace &lt;&lt;
    /// /DeviceRGB [/Separation …] &gt;&gt; „/DeviceRGB cs 1 0 0 sc“ fills red, not in the separation's colour.
    /// </summary>
    [Fact]
    public void DeviceSpaceNames_NeverEntriesOfTheResources() =>
        Assert.Equal(0xFFFF0000u, F_Fill("/DeviceRGB cs 1 0 0 sc 0 0 10 10 re f",
            "/ColorSpace << /DeviceRGB [/Separation /X /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 1 0] /N 1 >>] >>"));

    /// <summary>
    /// 8.6.8 sentences 11T–15T (beyond the draft): any other name is a name of the resources — also /I, /G, /RGB, /CMYK, which only inline images
    /// use as abbreviations: listed in /ColorSpace as an Indexed space whose entry 0 is red, „/name cs 0 sc“ fills red.
    /// </summary>
    [Theory]
    [InlineData("I")]
    [InlineData("G")]
    [InlineData("RGB")]
    [InlineData("CMYK")]
    public void AbbreviationsAsResourceNames_MeanTheResource(string name) =>
        Assert.Equal(0xFFFF0000u, F_Fill($"/{name} cs 0 sc 0 0 10 10 re f", $"/ColorSpace << /{name} [/Indexed /DeviceRGB 0 <FF0000>] >>"));

    /// <summary>
    /// 8.6.8 sentences 16T–18T: setting the space sets the initial colour — DeviceRGB and CalRGB 0 (black), DeviceCMYK 0 0 0 1 (as „0 0 0 1 k“).
    /// </summary>
    [Fact]
    public void InitialColour_DeviceAndCalibratedSpaces()
    {
        Assert.Equal(0xFF000000u, F_Fill("/DeviceRGB cs 0 0 10 10 re f"));
        Assert.Equal(0xFF000000u, F_Fill("/CS0 cs 0 0 10 10 re f", "/ColorSpace << /CS0 [/CalRGB << /WhitePoint [0.9505 1 1.089] >>] >>"));
        Assert.Equal(F_Fill("0 0 0 1 k 0 0 10 10 re f"), F_Fill("/DeviceCMYK cs 0 0 10 10 re f"));
    }

    /// <summary>
    /// 8.6.8 sentences 16T–18T: DeviceCMYK starts at 0 0 0 1 also where the resources give a /DefaultCMYK (here ICC, the simple formula) — the fill
    /// after „/DeviceCMYK cs“ is that of „0 0 0 1 k“, black.
    /// </summary>
    [Fact]
    public void InitialColour_DeviceCmykUnderDefaultCmyk_IsK1()
    {
        const string resources = "/ColorSpace << /DefaultCMYK [/ICCBased 5 0 R] >>";
        var profile = F_Stream("/N 4", F_NaiveCmyk.Value);
        Assert.Equal(0xFF000000u, F_Fill("0 0 0 1 k 0 0 10 10 re f", resources, profile));
        Assert.Equal(0xFF000000u, F_Fill("/DeviceCMYK cs 0 0 10 10 re f", resources, profile));
    }

    /// <summary>
    /// 8.6.8 sentence 19T (Lab): the initial colour is 0 per component, or the nearest value of /Range — Lab with /Range [10 100 10 100] fills as
    /// L* 0, a* 10, b* 10.
    /// </summary>
    [Fact]
    public void InitialColour_LabNearestInRange()
    {
        const string resources = "/ColorSpace << /L [/Lab << /WhitePoint [0.9642 1 0.8249] /Range [10 100 10 100] >>] >>";
        Assert.Equal(F_Fill("/L cs 0 10 10 sc 0 0 10 10 re f", resources), F_Fill("/L cs 0 0 10 10 re f", resources));
    }

    /// <summary>
    /// 8.6.8 sentence 19T (ICCBased): where 0 lies outside /Range the initial colour is the nearest valid value — a grey ICC space with /Range
    /// [0.5 1] fills as „0.5 sc“ (docs/FEHLERGEDAECHTNIS.md, No. 1126).
    /// </summary>
    [Fact]
    public void InitialColour_IccBasedNearestInRange()
    {
        const string resources = "/ColorSpace << /G1 [/ICCBased 5 0 R] >>";
        var profile = F_Stream("/N 1 /Range [0.5 1]", F_GreyProfile());
        // a value below the range is clamped to it as well („0 sc“ as „0.5 sc“); before, the initial colour was 0 — black
        Assert.NotEqual(0xFF000000u, F_Fill("/G1 cs 0 0 10 10 re f", resources, profile));
        Assert.Equal(F_Fill("/G1 cs 0.5 sc 0 0 10 10 re f", resources, profile), F_Fill("/G1 cs 0 0 10 10 re f", resources, profile));
    }

    /// <summary>8.6.8 sentence 20T: the initial colour of Indexed is index 0 — with the table red, blue a fill after „cs“ is red.</summary>
    [Fact]
    public void InitialColour_IndexedIsIndexZero() =>
        Assert.Equal(0xFFFF0000u, F_Fill("/Ix cs 0 0 10 10 re f", "/ColorSpace << /Ix [/Indexed /DeviceRGB 1 <FF0000 0000FF>] >>"));

    /// <summary>8.6.8 sentence 21T: the initial tone of Separation is 1.0 — towards cyan, a fill after „cs“ is full cyan (as „1 0 0 0 k“).</summary>
    [Fact]
    public void InitialColour_SeparationIsToneOne() =>
        Assert.Equal(F_Fill("1 0 0 0 k 0 0 10 10 re f"), F_Fill("/Spot cs 0 0 10 10 re f", $"/ColorSpace << /Spot {F_Cyan} >>"));

    /// <summary>
    /// 8.6.8 sentences 25T–32T (SC): the number of operands follows the space — „/DeviceRGB CS 0 0 1 SC“ strokes blue, „/DeviceCMYK CS 0 0 1 0 SC“
    /// as „0 0 1 0 K“, Indexed „1 SC“ the second entry; too few operands („/DeviceRGB CS 1 SC“) give black, without an error.
    /// </summary>
    [Fact]
    public void SC_OperandsByTheSpace()
    {
        Assert.Equal(0xFF0000FFu, F_Stroke("/DeviceRGB CS 0 0 1 SC 0 0 m 10 0 l S"));
        Assert.Equal(F_Stroke("0 0 1 0 K 0 0 m 10 0 l S"), F_Stroke("/DeviceCMYK CS 0 0 1 0 SC 0 0 m 10 0 l S"));
        Assert.Equal(0xFF0000FFu, F_Stroke("/Ix CS 1 SC 0 0 m 10 0 l S", "/ColorSpace << /Ix [/Indexed /DeviceRGB 1 <FF0000 0000FF>] >>"));
        Assert.Equal(0xFF000000u, F_Stroke("/DeviceRGB CS 1 SC 0 0 m 10 0 l S"));
    }

    /// <summary>
    /// 8.6.8 sentences 33T–40T (SCN): also for Separation and ICCBased, with numbers as operands — „/Spot CS 0.5 SCN“ (towards cyan) strokes as
    /// „0.5 0 0 0 K“; an ICC RGB space (AdobeRGB primaries) „0 0 1 SCN“ blue.
    /// </summary>
    [Fact]
    public void SCN_SeparationAndIccBased()
    {
        Assert.Equal(F_Stroke("0.5 0 0 0 K 0 0 m 10 0 l S"), F_Stroke("/Spot CS 0.5 SCN 0 0 m 10 0 l S", $"/ColorSpace << /Spot {F_Cyan} >>"));
        var blue = F_Stroke("/CS0 CS 0 0 1 SCN 0 0 m 10 0 l S", "/ColorSpace << /CS0 [/ICCBased 5 0 R] >>", F_Stream("/N 3", F_AdobeProfile()));
        Assert.True((blue & 0xFF) > 200 && (blue >> 16 & 0xFF) < 60, $"{blue:X8}");
    }

    /// <summary>The appearance of a note (Text annotation) with the colour /C: its content.</summary>
    private static string F_NoteContent(params double[] colour)
    {
        var note = TestPdf.Dict(("Subtype", TestPdf.N("Text")), ("C", new PdfArray(colour.Select(v => (PdfObject)new PdfReal(v)))));
        return Encoding.Latin1.GetString(AnnotationAppearance.Build(note, new PdfRectangle(0, 0, 20, 20))!.RawData!);
    }

    /// <summary>
    /// 8.6.8 sentences 51T–56T (G, g): DeviceGray (or /DefaultGray) and a grey from 0 to 1 — „0.5 G“ strokes and „0.5 g“ fills grey 128; under a
    /// /DefaultGray CalGray (gamma 2.2) „0.1 g“ fills as „0.1 sc“ in that space (not the grey 26 of DeviceGray); „1.5 g“ white. Writer: a note with
    /// /C [1.5] is drawn with „1 g“.
    /// </summary>
    [Fact]
    public void G_g_GreyFromZeroToOne()
    {
        Assert.Equal(0xFF808080u, F_Stroke("0.5 G 0 0 m 10 0 l S"));
        Assert.Equal(0xFF808080u, F_Fill("0.5 g 0 0 10 10 re f"));
        const string calGray = "[/CalGray << /WhitePoint [0.9642 1 0.8249] /Gamma 2.2 >>]";
        var defaultGrey = F_Fill("0.1 g 0 0 10 10 re f", $"/ColorSpace << /DefaultGray {calGray} >>");
        Assert.Equal(F_Fill("/CS0 cs 0.1 sc 0 0 10 10 re f", $"/ColorSpace << /CS0 {calGray} >>"), defaultGrey);
        Assert.NotEqual(0xFF1A1A1Au, defaultGrey);
        Assert.Equal(0xFFFFFFFFu, F_Fill("1.5 g 0 0 10 10 re f"));
        Assert.Contains("1 g", F_NoteContent(1.5));
    }

    /// <summary>
    /// 8.6.8 sentences 57T–64T (RG, rg): DeviceRGB (or /DefaultRGB), each component 0 to 1 — under a /DefaultRGB (ICC, AdobeRGB primaries) „0 1 0
    /// rg“ fills as „0 1 0 sc“ in that space; „2 0 0 rg“ as „1 0 0 rg“. Writer: a note with /C [2 0 0] is drawn with „1 0 0 rg“.
    /// </summary>
    [Fact]
    public void RG_rg_ComponentsFromZeroToOne()
    {
        var profile = F_Stream("/N 3", F_AdobeProfile());
        Assert.Equal(F_Fill("/CS0 cs 0 1 0 sc 0 0 10 10 re f", "/ColorSpace << /CS0 [/ICCBased 5 0 R] >>", profile),
            F_Fill("0 1 0 rg 0 0 10 10 re f", "/ColorSpace << /DefaultRGB [/ICCBased 5 0 R] >>", profile));
        Assert.NotEqual(F_Fill("0.3 0.6 0.2 rg 0 0 10 10 re f"), F_Fill("0.3 0.6 0.2 rg 0 0 10 10 re f", "/ColorSpace << /DefaultRGB [/ICCBased 5 0 R] >>", profile));
        Assert.Equal(F_Fill("1 0 0 rg 0 0 10 10 re f"), F_Fill("2 0 0 rg 0 0 10 10 re f"));
        Assert.Contains("1 0 0 rg", F_NoteContent(2, 0, 0));
    }

    /// <summary>
    /// 8.6.8 sentences 65T–74T (K, k): each component 0 (none) to 1 (full) — „0 0 0 2 k“ fills as „0 0 0 1 k“. Writer: a note with /C [0 0 0 2] is
    /// drawn with „0 0 0 1 k“.
    /// </summary>
    [Fact]
    public void K_k_ComponentsFromZeroToOne()
    {
        Assert.Equal(F_Fill("0 0 0 1 k 0 0 10 10 re f"), F_Fill("0 0 0 2 k 0 0 10 10 re f"));
        Assert.Contains("0 0 0 1 k", F_NoteContent(0, 0, 0, 2));
    }

    /// <summary>
    /// 8.6.8 sentences 75T–79T: in a d1 glyph the restriction holds for every content stream called from there — a form the glyph draws with „1 0 0
    /// rg“ comes out in the blue of the text.
    /// </summary>
    [Fact]
    public void D1Glyph_FormCalledFromIt_KeepsTheTextColour()
    {
        var file = F_Glyph("/Fm Do", "/XObject << /Fm 7 0 R >>", objects:
            [F_Stream("/Type /XObject /Subtype /Form /BBox [0 0 1000 1000]", "1 0 0 rg 0 0 1000 1000 re f")]);
        Assert.True(F_Blue(F_Pixel(file, 100, 100)), $"{F_Pixel(file, 100, 100)}");
    }

    /// <summary>
    /// 8.6.8 sentences 95T–102T: in a d1 glyph the rendering parameters of a graphics state are passed over — „/G0 gs“ with an inverting /TR leaves
    /// the glyph in the blue of the text.
    /// </summary>
    [Fact]
    public void D1Glyph_TransferFunctionOfGs_PassedOver()
    {
        var file = F_Glyph("/G0 gs 0 0 1000 1000 re f", "/ExtGState << /G0 << /TR << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >> >> >>");
        Assert.True(F_Blue(F_Pixel(file, 100, 100)), $"{F_Pixel(file, 100, 100)}");
    }

    /// <summary>
    /// 8.6.8 sentence 104T (d1 glyph, only the image mask; other images await a design decision on d1 glyphs): an image mask a d1 glyph draws
    /// over its right half paints in the blue of the text.
    /// </summary>
    [Fact]
    public void D1Glyph_ImageMask_PaintsInTheTextColour()
    {
        var mask = F_Glyph("q 500 0 0 1000 500 0 cm /Mk Do Q", "/XObject << /Mk 7 0 R >>", objects:
            [F_Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ImageMask true /BitsPerComponent 1", "\u0000")]);
        Assert.True(F_Blue(F_Pixel(mask, 125, 100)), $"Maske {F_Pixel(mask, 125, 100)}");
        Assert.True(F_White(F_Pixel(mask, 75, 100)), $"links {F_Pixel(mask, 75, 100)}");
    }

    /// <summary>
    /// 8.6.8 sentences 108–110: colour operators may stand inside a text object — „BT /F1 20 Tf 10 10 Td 1 0 0 rg (A) Tj ET“ draws the glyph red.
    /// </summary>
    [Fact]
    public void ColourOperatorInsideATextObject()
    {
        using var doc = PdfDocument.Open(F_File("BT /F1 20 Tf 10 10 Td 1 0 0 rg (A) Tj ET",
            "/Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>"));
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        var glyph = Assert.Single(list.Items.ToArray(), i => i.Glyph);
        Assert.Equal(0xFFFF0000u, list.Paints[glyph.Paint].Fill);
    }

    // ------------------------------------------------------------ 8.8.1 external objects

    /// <summary>8.8.1 sentences 1–4: there are image and form XObjects only — an XObject of /Subtype /PS drawn with Do paints nothing, without an error.</summary>
    [Fact]
    public void XObjectOfSubtypePS_PaintsNothing() =>
        Assert.True(F_Blank(F_File("/X Do", "/XObject << /X 5 0 R >>", F_Stream("/Type /XObject /Subtype /PS", "0 0 moveto"))));

    /// <summary>
    /// 8.8.1 sentences 8–12 and 14T–17T (reader): Do takes a name of the /XObject resources whose value is a stream — a name not listed there (also
    /// one listed only under /Pattern or /Font), a reference into the void and a dictionary instead of a stream paint nothing, without an error.
    /// </summary>
    [Theory]
    [InlineData("/Fehlt Do", "/XObject << >>")]
    [InlineData("/Name Do", "/Pattern << /Name 5 0 R >> /Font << /Name 5 0 R >>")]
    [InlineData("/Void Do", "/XObject << /Void 99 0 R >>")]
    [InlineData("/Dict Do", "/XObject << /Dict << /Type /XObject /Subtype /Form /BBox [0 0 200 200] >> >>")]
    public void Do_NameNotAStreamOfTheXObjects_PaintsNothing(string content, string resources) =>
        Assert.True(F_Blank(F_File(content, resources, F_Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8", "\u00FF\u0000\u0000"))));

    /// <summary>
    /// 8.8.1 sentences 16T–17T (reader, today's tolerance): the effect follows /Subtype — a stream with /Type /Pattern but /Subtype /Image is drawn
    /// as an image.
    /// </summary>
    [Fact]
    public void Do_StreamOfAnotherType_DrawnByItsSubtype()
    {
        var file = F_File("q 200 0 0 200 0 0 cm /Im Do Q", "/XObject << /Im 5 0 R >>",
            F_Stream("/Type /Pattern /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8", "\u00FF\u0000\u0000"));
        Assert.True(F_Pixel(file, 100, 100) is { Red: > 240, Green: < 20, Blue: < 20 }, $"{F_Pixel(file, 100, 100)}");
    }

    /// <summary>
    /// 8.8.1 sentences 8–12 and 14T–17T (writer): a page of an image document names its image in the content („/Im0 Do“) and lists it under /XObject
    /// as a stream of /Type /XObject, /Subtype /Image.
    /// </summary>
    [Fact]
    public void ImagePage_DrawsItsImageByName()
    {
        using var raster = new MemoryStream();
        var builder = new RasterPdfBuilder(raster);
        var pixels = new MemoryStream();
        using (var z = new ZLibStream(pixels, CompressionLevel.Fastest, leaveOpen: true)) z.Write(new byte[2 * 2 * 3]);
        builder.AddPage(100, 100, pixels.ToArray(), "FlateDecode", 2, 2);
        builder.Finish();
        using var doc = PdfDocument.Open(raster.ToArray());
        var page = doc.Pages[0];
        var content = Encoding.Latin1.GetString(doc.GetStreamData(page.Dictionary.GetStream(PdfName.Get("Contents"))!));
        Assert.Contains("/Im0 Do", content);
        var image = Assert.IsType<PdfStream>(page.Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im0")));
        Assert.Equal("XObject", image.Dictionary.GetName(PdfName.Type)?.Value);
        Assert.Equal("Image", image.Dictionary.GetName(PdfName.Subtype)?.Value);
    }
}
