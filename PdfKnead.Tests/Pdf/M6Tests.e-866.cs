using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

// M6 draft e-866 (ISO 32000-2, 8.6.6: Pattern, Indexed, Separation, DeviceN): the rows whose evidence was missing.
public sealed partial class M6NormTests
{
    // ------------------------------------------------------------ helpers (package e)

    /// <summary>A stream object as text: its dictionary entries and data (Latin-1, one byte per character).</summary>
    private static string E_Stream(string dict, string data) => $"<< {dict} /Length {data.Length} >>\nstream\n{data}\nendstream";

    /// <summary>A file with one page of 200 × 200: its content and the entries of its resources; further objects are numbered from 5.</summary>
    private static byte[] E_File(string content, string resources = "", params string[] objects) =>
        DocumentStructureNormTests.FileOf([
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << {resources} >> /Contents 4 0 R >>",
            E_Stream("", content), .. objects]);

    /// <summary>The paint of the first entry that fills (or strokes) as (R, G, B) and the index of its pattern (−1: a colour).</summary>
    private static ((byte R, byte G, byte B) Rgb, int Pattern) E_Paint(string content, string resources = "", bool stroke = false, params string[] objects)
    {
        using var doc = PdfDocument.Open(E_File(content, resources, objects));
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        var item = list.Items.ToArray().First(i => stroke ? i.Kind is DisplayItemKind.Stroke or DisplayItemKind.FillStroke
            : i.Kind is DisplayItemKind.Fill or DisplayItemKind.FillStroke);
        var paint = list.Paints[item.Paint];
        var argb = stroke ? paint.Stroke : paint.Fill;
        return (((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb), stroke ? paint.StrokeShading : paint.FillShading);
    }

    /// <summary>The fill colour of „/CS0 cs <paramref name="values"/> scn“ in the colour space <paramref name="space"/> (written as text).</summary>
    private static (byte R, byte G, byte B) E_In(string space, string values, params string[] objects) =>
        E_Paint($"/CS0 cs {values} scn 0 0 10 10 re f", $"/ColorSpace << /CS0 {space} >>", false, objects).Rgb;

    /// <summary>The page rendered at 72 dpi; the pixel at page coordinates (x, y).</summary>
    private static SKColor E_Pixel(byte[] file, int x, int y)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        return bitmap.GetPixel(x, bitmap.Height - 1 - y);
    }

    private static bool E_White(SKColor c) => c is { Red: > 250, Green: > 250, Blue: > 250 };

    /// <summary>The first pixel of the rendered page that is not white, null if there is none.</summary>
    private static (int X, int Y, SKColor C)? E_Marked(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (!E_White(bitmap.GetPixel(x, y))) return (x, bitmap.Height - 1 - y, bitmap.GetPixel(x, y));
        return null;
    }

    private static readonly (byte, byte, byte) E_Red = (255, 0, 0), E_Green = (0, 255, 0), E_Blue = (0, 0, 255), E_White3 = (255, 255, 255),
        E_Black = (0, 0, 0), E_LightGrey = (200, 200, 200);

    /// <summary>One tone towards red in DeviceRGB: 0 white, 1 red.</summary>
    private const string E_ToRed = "<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>";

    /// <summary>One tone towards green in DeviceRGB: 0 white, 1 green.</summary>
    private const string E_ToGreen = "<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 1 0] /N 1 >>";

    /// <summary>Two tones (A, B) → RGB (A, B, 0), a PostScript function (object 5).</summary>
    private static string E_TwoToRg() => E_Stream("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", "{ 0 }");

    private static byte[] E_Flate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    // ------------------------------------------------------------ 8.6.6.2 Pattern

    /// <summary>
    /// 8.6.6.2 sentences 1–2: a pattern is a tiling pattern (type 1, a stream) or a shading pattern (type 2) — a pattern of type 3, and a type 1
    /// given as a dictionary without a stream, paint neither tiles nor a shading but the light grey stand-in (200, 200, 200).
    /// </summary>
    [Theory]
    [InlineData("<< /PatternType 3 >>")]
    [InlineData("<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >> >>")]
    public void PatternOfAnotherType_PaintsTheLightGrey(string pattern)
    {
        var (rgb, index) = E_Paint("/Pattern cs /P0 scn 0 0 200 200 re f", $"/Pattern << /P0 {pattern} >>");
        Assert.Equal(E_LightGrey, rgb);
        Assert.Equal(-1, index);
    }

    // ------------------------------------------------------------ 8.6.6.3 Indexed

    /// <summary>8.6.6.3 sentences 4–7 (lenient reading): an Indexed array of three elements [/Indexed /DeviceRGB 1] is read as DeviceGray, without an error.</summary>
    [Fact]
    public void Indexed_ArrayOfThreeElements_ReadAsDeviceGray() =>
        Assert.Equal(((byte)128, (byte)128, (byte)128), E_In("[/Indexed /DeviceRGB 1]", "0.5"));

    /// <summary>
    /// 8.6.6.3 sentence 8: setting an Indexed space sets the colour to index 0 — with the table red, blue a fill after „cs“ without „sc“ is red,
    /// as is a stroke after „CS“.
    /// </summary>
    [Fact]
    public void Indexed_SettingTheSpaceGivesIndexZero()
    {
        const string resources = "/ColorSpace << /Ix [/Indexed /DeviceRGB 1 <FF0000 0000FF>] >>";
        Assert.Equal(E_Red, E_Paint("/Ix cs 0 0 50 50 re f", resources).Rgb);
        Assert.Equal(E_Red, E_Paint("/Ix CS 0 0 m 50 0 l S", resources, stroke: true).Rgb);
    }

    /// <summary>
    /// 8.6.6.3 sentences 9–10 (lenient reading beyond Pattern and Indexed): a base of Separation or DeviceN gives its tones through the tint
    /// transformation (entry 0 tone 0 white, entry 1 tone 1 red); a base of Pattern paints the light grey, without an error; a base that is itself
    /// Indexed gives the colour of the inner entry (blue).
    /// </summary>
    [Fact]
    public void Indexed_BaseSeparationDeviceNPatternOrIndexed()
    {
        foreach (var colorant in new[] { $"[/Separation /Rot /DeviceRGB {E_ToRed}]", $"[/DeviceN [/Rot] /DeviceRGB {E_ToRed}]" })
        {
            Assert.Equal(E_White3, E_In($"[/Indexed {colorant} 1 <00FF>]", "0"));
            Assert.Equal(E_Red, E_In($"[/Indexed {colorant} 1 <00FF>]", "1"));
        }
        Assert.Equal(E_LightGrey, E_In("[/Indexed /Pattern 1 <00FF>]", "1"));
        Assert.Equal(E_Blue, E_In("[/Indexed [/Indexed /DeviceRGB 1 <FF0000 0000FF>] 1 <0001>]", "1"));
    }

    /// <summary>
    /// 8.6.6.3 sentences 12–13 (reader): hival is at most 255 — with hival 300 and a table of 301 entries (255 blue, 300 red) „300 sc“ takes the
    /// last valid index 255: blue.
    /// </summary>
    [Fact]
    public void Indexed_HivalAbove255_CountsAs255()
    {
        var table = new StringBuilder();
        for (var i = 0; i <= 300; i++) table.Append(i == 255 ? "0000FF" : i == 300 ? "FF0000" : "000000");
        Assert.Equal(E_Blue, E_In($"[/Indexed /DeviceRGB 300 <{table}>]", "300"));
    }

    /// <summary>8.6.6.3 sentences 14–15: the table may be a stream as well as a string — plain or with /FlateDecode it gives the same colours.</summary>
    [Fact]
    public void Indexed_TableAsAStream_AlsoCompressed()
    {
        byte[] table = [255, 0, 0, 0, 0, 255, 0, 255, 0];
        foreach (var stream in new[]
                 {
                     E_Stream("", Encoding.Latin1.GetString(table)), E_Stream("/Filter /FlateDecode", Encoding.Latin1.GetString(E_Flate(table))),
                 })
            for (var index = 0; index < 3; index++)
                Assert.Equal(E_In("[/Indexed /DeviceRGB 2 <FF0000 0000FF 00FF00>]", $"{index}"), E_In("[/Indexed /DeviceRGB 2 5 0 R]", $"{index}", stream));
    }

    /// <summary>
    /// 8.6.6.3 sentence 16 (reader, lenient): the table holds m × (hival + 1) bytes — of one too short (&lt;FF0000 00FF&gt; for three entries) the
    /// missing bytes count as the minimum: entry 1 green (blue 0), entry 2 black; no error.
    /// </summary>
    [Fact]
    public void Indexed_TableTooShort_MissingBytesAreTheMinimum()
    {
        Assert.Equal(E_Green, E_In("[/Indexed /DeviceRGB 2 <FF0000 00FF>]", "1"));
        Assert.Equal(E_Black, E_In("[/Indexed /DeviceRGB 2 <FF0000 00FF>]", "2"));
    }

    // ------------------------------------------------------------ 8.6.6.4 Separation

    /// <summary>
    /// 8.6.6.4 sentences 29–30: the initial tone of a Separation space is 1.0, scn sets the tone — towards red: after „cs“ without „scn“ red, „0
    /// scn“ white, „0.5 scn“ pink (255, 128, 128); a stroke after „CS“ red.
    /// </summary>
    [Fact]
    public void Separation_InitialToneOne_ScnSetsTheTone()
    {
        var resources = $"/ColorSpace << /Sp [/Separation /Rot /DeviceRGB {E_ToRed}] >>";
        Assert.Equal(E_Red, E_Paint("/Sp cs 0 0 10 10 re f", resources).Rgb);
        Assert.Equal(E_White3, E_Paint("/Sp cs 0 scn 0 0 10 10 re f", resources).Rgb);
        Assert.Equal(((byte)255, (byte)128, (byte)128), E_Paint("/Sp cs 0.5 scn 0 0 10 10 re f", resources).Rgb);
        Assert.Equal(E_Red, E_Paint("/Sp CS 0 0 m 10 0 l S", resources, stroke: true).Rgb);
    }

    /// <summary>8.6.6.4 sentence 31: an image of one component may give the tones — 2 × 1 pixels, 8 bits, values 0 and 255 towards red: white, red.</summary>
    [Fact]
    public void Separation_ImageOfOneComponentGivesTheTones()
    {
        var file = E_File("q 200 0 0 100 0 50 cm /Im Do Q", "/XObject << /Im 5 0 R >>",
            E_Stream($"/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace [/Separation /Rot /DeviceRGB {E_ToRed}]", "\u0000\u00FF"));
        Assert.True(E_White(E_Pixel(file, 50, 100)), $"links {E_Pixel(file, 50, 100)}");
        Assert.True(E_Pixel(file, 150, 100) is { Red: > 240, Green: < 20, Blue: < 20 }, $"rechts {E_Pixel(file, 150, 100)}");
    }

    /// <summary>
    /// 8.6.6.4 sentences 35–37: All marks every colorant; on the screen the tone is subtracted from 1 — „1 scn“ black, „0.25 scn“ grey 191, „0
    /// scn“ white; the tint transformation (towards red) is not used.
    /// </summary>
    [Fact]
    public void Separation_All_PaintsOneMinusTheTone()
    {
        var all = $"[/Separation /All /DeviceRGB {E_ToRed}]";
        Assert.Equal(E_Black, E_In(all, "1"));
        Assert.Equal(((byte)191, (byte)191, (byte)191), E_In(all, "0.25"));
        Assert.Equal(E_White3, E_In(all, "0"));
    }

    /// <summary>
    /// 8.6.6.4 sentences 40–41 (paths; images, „sh“ and shading patterns in GraphicsNormTests.SeparationNone_ImagesAndShadingsMarkNothing): a
    /// path filled and stroked in the colorant /None leaves the page white.
    /// </summary>
    [Fact]
    public void SeparationNone_PathFilledAndStroked_MarksNothing()
    {
        var file = E_File("/N0 cs /N0 CS 1 scn 1 SCN 20 w 20 20 160 160 re B",
            $"/ColorSpace << /N0 [/Separation /None /DeviceRGB {E_ToRed}] >>");
        Assert.Null(E_Marked(file));
    }

    /// <summary>
    /// 8.6.6.4 sentences 42–43: All and None are supported whatever the alternate space and tint transformation — with an invalid function (a
    /// number instead of a function) None marks nothing and All paints 1 − tone.
    /// </summary>
    [Fact]
    public void SeparationNoneAndAll_WithAnInvalidFunction()
    {
        Assert.Null(E_Marked(E_File("/N0 cs 1 scn 20 20 160 160 re f", "/ColorSpace << /N0 [/Separation /None /DeviceRGB 0] >>")));
        Assert.Equal(((byte)191, (byte)191, (byte)191), E_In("[/Separation /All /DeviceRGB 0]", "0.25"));
    }

    /// <summary>
    /// 8.6.6.4 sentences 46–48, 50: the screen never puts a process colorant on directly — /Cyan and /Red with a function towards green paint
    /// green; the name has no effect.
    /// </summary>
    [Theory]
    [InlineData("Cyan")]
    [InlineData("Red")]
    public void Separation_ProcessColourNames_UseTheTintTransform(string name) =>
        Assert.Equal(E_Green, E_In($"[/Separation /{name} /DeviceRGB {E_ToGreen}]", "1"));

    /// <summary>
    /// 8.6.6.4 sentence 54 (lenient reading): a special alternate space — Pattern paints the light grey; Indexed gives the entry the function's
    /// value picks (1: blue); a Separation naming itself as its alternate ends after eight levels in DeviceGray, without an error.
    /// </summary>
    [Fact]
    public void Separation_SpecialAlternate_ReadLeniently()
    {
        const string toOne = "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>";
        Assert.Equal(E_LightGrey, E_In($"[/Separation /X /Pattern {toOne}]", "1"));
        Assert.Equal(E_Blue, E_In($"[/Separation /X [/Indexed /DeviceRGB 1 <FF0000 0000FF>] {toOne}]", "1"));
        var self = E_Paint("/CS0 cs 1 scn 0 0 10 10 re f", "/ColorSpace << /CS0 5 0 R >>", false, $"[/Separation /X 5 0 R {toOne}]");
        Assert.Equal(-1, self.Pattern);
    }

    // ------------------------------------------------------------ 8.6.6.5 DeviceN

    /// <summary>
    /// 8.6.6.5 sentences 17–23, 42, 50, 60–68, 81–85, 101–102, 111–138: the attributes of DeviceN — /Subtype /NChannel or /DeviceN, /Colorants,
    /// /Process, /MixingHints with /Solidities, /PrintingOrder, /DotGain, also for Cyan — are not used: the space paints exactly as without its
    /// attribute dictionary, through the tint transformation.
    /// </summary>
    [Theory]
    [InlineData("<< /Subtype /NChannel /Colorants << /Gold [/Separation /Gold /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0.8 0] /N 1 >>] >> " +
                "/Process << /ColorSpace /DeviceCMYK /Components [/Cyan /Magenta /Yellow /Black] >> /MixingHints << /Solidities << /Gold 1 >> /PrintingOrder [/Cyan /Gold] >> >>")]
    [InlineData("<< /Subtype /DeviceN >>")]
    [InlineData("<< /Colorants << /Gold [/Separation /Gold /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 0 1] /N 1 >>] >> >>")]
    [InlineData("<< /Process << /ColorSpace /DeviceRGB /Components [/Cyan /Gold] >> >>")]
    [InlineData("<< /MixingHints << /Solidities << /Gold 1 /Default 0.5 >> /PrintingOrder [/Cyan /Gold] >> >>")]
    [InlineData("<< /MixingHints << /PrintingOrder [/Gold /Cyan /Extra] >> >>")]
    [InlineData("<< /MixingHints << /DotGain << /Gold << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [0.2] /N 1 >> /Default << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 0.5 >> >> >> >>")]
    [InlineData("<< /MixingHints << /Solidities << /Cyan 0.2 >> /PrintingOrder [/Cyan /Gold] /DotGain << /Cyan << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 2 >> >> >> >>")]
    public void DeviceN_Attributes_PaintAsWithout(string attributes)
    {
        const string resources = "/ColorSpace << /CS0 [/DeviceN [/Cyan /Gold] /DeviceRGB 5 0 R] /CS1 [/DeviceN [/Cyan /Gold] /DeviceRGB 5 0 R {0}] >>";
        foreach (var values in new[] { "1 0", "0.3 0.7", "0 1" })
        {
            var plain = E_Paint($"/CS0 cs {values} scn 0 0 10 10 re f", string.Format(resources, attributes), false, E_TwoToRg()).Rgb;
            var with = E_Paint($"/CS1 cs {values} scn 0 0 10 10 re f", string.Format(resources, attributes), false, E_TwoToRg()).Rgb;
            Assert.Equal(plain, with);
        }
        Assert.Equal(E_Red, E_Paint("/CS1 cs 1 0 scn 0 0 10 10 re f", string.Format(resources, attributes), false, E_TwoToRg()).Rgb);
    }

    /// <summary>
    /// 8.6.6.5 sentences 32, 34–35 (reader, lenient): the names should differ and not be All — [/A /A] and [/All /B] paint through the tint
    /// transformation (1 0: red), without an error; a second element that is no array gives DeviceGray.
    /// </summary>
    [Fact]
    public void DeviceN_RepeatedOrAllNames_ReadLeniently()
    {
        Assert.Equal(E_Red, E_In("[/DeviceN [/A /A] /DeviceRGB 5 0 R]", "1 0", E_TwoToRg()));
        Assert.Equal(E_Red, E_In("[/DeviceN [/All /B] /DeviceRGB 5 0 R]", "1 0", E_TwoToRg()));
        Assert.Equal(((byte)128, (byte)128, (byte)128), E_In("[/DeviceN /A /DeviceRGB 5 0 R]", "0.5", E_TwoToRg()));
    }

    /// <summary>
    /// 8.6.6.5 sentence 43: setting a DeviceN space gives every component the tone 1.0 — with a function (A, B) → (1 − A, 1 − B, 1) a fill after
    /// „cs“ without „scn“ is blue.
    /// </summary>
    [Fact]
    public void DeviceN_SettingTheSpaceGivesToneOne() =>
        Assert.Equal(E_Blue, E_Paint("/Dn cs 0 0 10 10 re f", "/ColorSpace << /Dn [/DeviceN [/A /B] /DeviceRGB 5 0 R] >>", false,
            E_Stream("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", "{ 1 exch sub exch 1 exch sub exch 1 }")).Rgb);

    /// <summary>
    /// 8.6.6.5 sentences 44–45: the operands of scn and the values of an image come in the order of the names — with (A, B) → (A, B, 0) „0 1 scn“
    /// is green, „1 0 scn“ red; an image of two components with the pixels (255, 0) and (0, 255) is red, then green.
    /// </summary>
    [Fact]
    public void DeviceN_OperandsAndImageValuesInTheOrderOfTheNames()
    {
        Assert.Equal(E_Green, E_In("[/DeviceN [/A /B] /DeviceRGB 5 0 R]", "0 1", E_TwoToRg()));
        Assert.Equal(E_Red, E_In("[/DeviceN [/A /B] /DeviceRGB 5 0 R]", "1 0", E_TwoToRg()));
        var file = E_File("q 200 0 0 100 0 50 cm /Im Do Q", "/XObject << /Im 6 0 R >>", E_TwoToRg(),
            E_Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace [/DeviceN [/A /B] /DeviceRGB 5 0 R]",
                "\u00FF\u0000\u0000\u00FF"));
        Assert.True(E_Pixel(file, 50, 100) is { Red: > 240, Green: < 20, Blue: < 20 }, $"links {E_Pixel(file, 50, 100)}");
        Assert.True(E_Pixel(file, 150, 100) is { Green: > 240, Red: < 20, Blue: < 20 }, $"rechts {E_Pixel(file, 150, 100)}");
    }

    /// <summary>8.6.6.5 sentences 46–47 (lenient reading): a DeviceN space with the alternate space Pattern paints the light grey, without an error.</summary>
    [Fact]
    public void DeviceN_PatternAlternate_PaintsTheLightGrey() =>
        Assert.Equal(E_LightGrey, E_In("[/DeviceN [/A /B] /Pattern 5 0 R]", "1 0", E_TwoToRg()));

    /// <summary>
    /// 8.6.6.5 sentences 55–59: a component None is never marked — DeviceN [/None /None] leaves a filled path, text, an image and a shading
    /// painted with „sh“ unmarked; in [/A /None] the tone of None goes to the tint transformation (here (A, N) → (N, 0, 0): „0 1 scn“ red, „0 0
    /// scn“ black).
    /// </summary>
    [Fact]
    public void DeviceN_None_NeverMarked()
    {
        var file = E_File(
            "/NN cs 1 1 scn 0 0 100 100 re f BT /F1 80 Tf 110 20 Td (H) Tj ET q 100 0 0 100 0 100 cm /Im Do Q q 100 100 100 100 re W n /Sh sh Q",
            "/ColorSpace << /NN [/DeviceN [/None /None] /DeviceRGB 5 0 R] >> /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> " +
            "/XObject << /Im 6 0 R >> /Shading << /Sh << /ShadingType 2 /ColorSpace [/DeviceN [/None /None] /DeviceRGB 5 0 R] /Coords [0 0 200 0] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 1] /C1 [1 1] /N 1 >> /Extend [true true] >> >>",
            E_TwoToRg(),
            E_Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace [/DeviceN [/None /None] /DeviceRGB 5 0 R]",
                "\u00FF\u00FF"));
        Assert.Null(E_Marked(file));
        var noneTone = E_Stream("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", "{ exch pop 0 0 }");
        Assert.Equal(E_Red, E_In("[/DeviceN [/A /None] /DeviceRGB 5 0 R]", "0 1", noneTone));
        Assert.Equal(E_Black, E_In("[/DeviceN [/A /None] /DeviceRGB 5 0 R]", "0 0", noneTone));
    }

    // ------------------------------------------------------------ 8.6.6.5 writer and checker (PDF/A)

    private static PdfAResult E_PdfA(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        return PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b);
    }

    private static PdfArray E_WrittenSpace(PdfDocument doc, int page = 0) =>
        (PdfArray)doc.Pages[page].Resources!.GetDictionary(PdfName.Get("ColorSpace"))!.Get(PdfName.Get("CS0"));

    private const string E_GoldTint = "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0.2 0.4 0 0.1] /N 1 >>";

    /// <summary>
    /// 8.6.6.5 sentence 24: PDF-Knead creates no NChannel spaces — the PDF/A repair of a DeviceN space without attributes adds /Colorants but no
    /// /Subtype; an NChannel space that brings its attributes stays as it is.
    /// </summary>
    [Fact]
    public void PdfA_DeviceNGetsColorantsWithoutSubtype_NChannelKept()
    {
        using (var repaired = PdfDocument.Open(E_PdfA(E_File("/CS0 cs 1 scn 72 72 50 50 re f",
                   $"/ColorSpace << /CS0 [/DeviceN [/Gold] /DeviceCMYK {E_GoldTint}] >>")).Bytes))
        {
            var attributes = (PdfDictionary)E_WrittenSpace(repaired).Get(4);
            Assert.NotNull(attributes.GetDictionary(PdfName.Get("Colorants")));
            Assert.False(attributes.ContainsKey(PdfName.Get("Subtype")));
        }
        var nchannel = $"[/DeviceN [/Gold] /DeviceCMYK {E_GoldTint} << /Subtype /NChannel /Colorants << /Gold [/Separation /Gold /DeviceCMYK {E_GoldTint}] >> >>]";
        using var kept = PdfDocument.Open(E_PdfA(E_File("/CS0 cs 1 scn 72 72 50 50 re f", $"/ColorSpace << /CS0 {nchannel} >>")).Bytes);
        var keptAttributes = (PdfDictionary)E_WrittenSpace(kept).Get(4);
        Assert.Equal("NChannel", keptAttributes.GetName(PdfName.Get("Subtype"))?.Value);
        var gold = (PdfArray)keptAttributes.GetDictionary(PdfName.Get("Colorants"))!.Get(PdfName.Get("Gold"));
        Assert.Equal("Gold", ((PdfName)gold.Get(1)).Value);
        Assert.Equal(1, keptAttributes.GetDictionary(PdfName.Get("Colorants"))!.Count);
    }

    /// <summary>
    /// 8.6.6.5 sentences 69T–76T (writer): the key of an entry of /Colorants is the colorant name of its Separation space — after the repair the
    /// Separation under the key Gold names /Gold; after a second description of the same name was renamed, the one under the key „X 2“ names
    /// „X 2“.
    /// </summary>
    [Fact]
    public void PdfA_ColorantsKeyIsTheNameInItsSeparation()
    {
        using (var doc = PdfDocument.Open(E_PdfA(E_File("/CS0 cs 1 scn 72 72 50 50 re f",
                   $"/ColorSpace << /CS0 [/DeviceN [/Gold] /DeviceCMYK {E_GoldTint}] >>")).Bytes))
        {
            var gold = (PdfArray)((PdfDictionary)E_WrittenSpace(doc).Get(4)).GetDictionary(PdfName.Get("Colorants"))!.Get(PdfName.Get("Gold"));
            Assert.Equal("Separation", ((PdfName)gold.Get(0)).Value);
            Assert.Equal("Gold", ((PdfName)gold.Get(1)).Value);
        }
        // two pages with the name X described differently: the second becomes „X 2“
        static string Spot(string c1) => $"[/DeviceN [/X] /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [{c1}] /N 1 >>]";
        var file = DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /ColorSpace << /CS0 {Spot("0.2 0.4 0 0.1")} >> >> /Contents 4 0 R >>",
            E_Stream("", "/CS0 cs 1 scn 72 72 50 50 re f"),
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /ColorSpace << /CS0 {Spot("0 0 1 0")} >> >> /Contents 6 0 R >>",
            E_Stream("", "/CS0 cs 1 scn 72 72 50 50 re f"));
        var result = E_PdfA(file);
        Assert.True(result.Conforms, string.Join("\n", result.Violations.Select(v => $"{v.Rule} {v.Text}")));
        using var renamed = PdfDocument.Open(result.Bytes);
        var second = E_WrittenSpace(renamed, 1);
        Assert.Equal("X 2", ((PdfName)((PdfArray)second.Get(1)).Get(0)).Value);
        var colorants = ((PdfDictionary)second.Get(4)).GetDictionary(PdfName.Get("Colorants"))!;
        var separation = (PdfArray)colorants.Get(PdfName.Get("X 2"));
        Assert.Equal("X 2", ((PdfName)separation.Get(1)).Value);
    }

    private static IReadOnlyList<PdfAViolation> E_Check(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        return PdfACheck.Check(doc);
    }

    /// <summary>
    /// 8.6.6.5 sentences 77T–80T: the components of /Process are process colours — the check finds no missing description (6.2.4.4) for DeviceN
    /// [/PRot /Gold] whose /Process names PRot (DeviceRGB, components PRot, PGruen, PBlau) and whose /Colorants describe Gold only; the display
    /// paints the space as without /Process.
    /// </summary>
    [Fact]
    public void PdfACheck_ProcessComponentsAreNoSpotColours()
    {
        const string goldSeparation = "[/Separation /Gold /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0.8 0] /N 1 >>]";
        var attributes = $"<< /Process << /ColorSpace /DeviceRGB /Components [/PRot /PGruen /PBlau] >> /Colorants << /Gold {goldSeparation} >> >>";
        var with = $"[/DeviceN [/PRot /Gold] /DeviceRGB 5 0 R {attributes}]";
        Assert.DoesNotContain(E_Check(E_File("/CS0 cs 1 0 scn 0 0 10 10 re f", $"/ColorSpace << /CS0 {with} >>", E_TwoToRg())), v => v.Rule == "6.2.4.4");
        Assert.Contains(E_Check(E_File("/CS0 cs 1 0 scn 0 0 10 10 re f", "/ColorSpace << /CS0 [/DeviceN [/PRot /Gold] /DeviceRGB 5 0 R] >>", E_TwoToRg())),
            v => v.Rule == "6.2.4.4");
        Assert.Equal(E_In("[/DeviceN [/PRot /Gold] /DeviceRGB 5 0 R]", "0.4 0.6", E_TwoToRg()), E_In(with, "0.4 0.6", E_TwoToRg()));
    }

    /// <summary>
    /// 8.6.6.5 sentences 103T–110T: the process colour space of /Process is a colour space in use — an [/ICCBased …] there with an invalid profile is
    /// reported (6.2.4.2) as any ICC space; the names of /Components are no spot colours (no 6.2.4.4).
    /// </summary>
    [Fact]
    public void PdfACheck_ProcessColourSpaceCheckedAsInUse()
    {
        var found = E_Check(E_File("/CS0 cs 1 1 scn 0 0 10 10 re f",
            "/ColorSpace << /CS0 [/DeviceN [/PRot /PGruen] /DeviceRGB 5 0 R << /Subtype /NChannel /Process << /ColorSpace [/ICCBased 6 0 R] /Components [/PRot /PGruen /PBlau] >> >>] >>",
            E_TwoToRg(), E_Stream("/N 3", "kein Profil")));
        Assert.Contains(found, v => v.Rule == "6.2.4.2");
        Assert.DoesNotContain(found, v => v.Rule == "6.2.4.4");
    }
}
