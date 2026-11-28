using System.Text;

using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using PdfKnead.Tests.Fonts;
using PdfKnead.Tests.Ui;
using PdfKnead.ViewModels;
using PdfKnead.Views;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, reading after the measurements against the reference readers (docs/FEHLERGEDAECHTNIS.md, No. 1165): d1 outside a Type 3 glyph and the
/// transparency values after d1 (ISO 32000-2, 9.6.4), the text knockout flag /TK (9.3.8), the read tolerances of text, CFF, CMap and cmap that
/// either stay (special rules, measured) or became strict where the readers are, and the notice for fonts whose licence forbids editing.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (E7)

    private static string E7Stream(string dict, string data) => $"<< {dict} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static string E7Stream(string dict, byte[] data) => E7Stream(dict, Encoding.Latin1.GetString(data));

    /// <summary>A file with one page of 200 × 200: its content and the entries of its resources; further objects are numbered from 5.</summary>
    private static byte[] E7File(string content, string resources = "", params string[] objects) =>
        DocumentStructureNormTests.FileOf([
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << {resources} >> /Contents 4 0 R >>",
            E7Stream("", content), .. objects]);

    private static SKBitmap E7Render(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        return SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
    }

    private static SKColor E7Pixel(byte[] file, int x, int y)
    {
        using var bitmap = E7Render(file);
        return bitmap.GetPixel(x, bitmap.Height - 1 - y);
    }

    private static int E7Ink(byte[] file)
    {
        using var bitmap = E7Render(file);
        var ink = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y) is var c && Math.Max(255 - c.Red, Math.Max(255 - c.Green, 255 - c.Blue)) > 24) ink++;
        return ink;
    }

    private const string E7Helvetica = "/Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>";

    /// <summary>A page with a Type 3 font /F3 whose glyph „a“ (1000 × 1000 units) runs <paramref name="procedure"/>; drawn at 100 pt at (50, 50).</summary>
    private static byte[] E7Type3(string procedure, string fontResources = "", string before = "0 0 1 rg", params string[] objects) =>
        E7File($"{before} BT /F3 100 Tf 50 50 Td (a) Tj ET", "/Font << /F3 5 0 R >>", [
            "<< /Type /Font /Subtype /Type3 /FontMatrix [0.001 0 0 0.001 0 0] /FontBBox [0 0 1000 1000] /CharProcs << /g 6 0 R >> " +
            $"/Encoding << /Differences [97 /g] >> /FirstChar 97 /LastChar 97 /Widths [1000] /Resources << {fontResources} >> >>",
            E7Stream("", procedure), .. objects]);

    /// <summary>The darkest red level inside the left part of the page (where the glyphs of the knockout probes lie).</summary>
    private static int E7Darkest(byte[] file)
    {
        using var bitmap = E7Render(file);
        var least = 255;
        for (var y = 30; y < 190; y++)
            for (var x = 30; x < 150; x++)
                least = Math.Min(least, bitmap.GetPixel(x, y).Red);
        return least;
    }

    /// <summary>A Type 2 charstring: numbers and operator bytes.</summary>
    private static byte[] E7Cs(params object[] parts)
    {
        var d = new List<byte>();
        foreach (var p in parts)
            if (p is int v)
            {
                if (v is >= -107 and <= 107) d.Add((byte)(v + 139));
                else if (v is >= 108 and <= 1131) d.AddRange([(byte)(((v - 108) >> 8) + 247), (byte)((v - 108) & 0xFF)]);
                else if (v is >= -1131 and <= -108) d.AddRange([(byte)(((-v - 108) >> 8) + 251), (byte)((-v - 108) & 0xFF)]);
                else d.AddRange([28, (byte)(v >> 8), (byte)v]);
            }
            else d.AddRange((byte[])p);
        return [.. d];
    }

    private static readonly byte[] E7Square = E7Cs(100, 0, new byte[] { 21 }, 600, new byte[] { 6 }, 700, new byte[] { 7 }, -600, new byte[] { 6 }, new byte[] { 14 });
    private static readonly byte[] E7Tall = E7Cs(300, 0, new byte[] { 21 }, 150, new byte[] { 6 }, 700, new byte[] { 7 }, -150, new byte[] { 6 }, new byte[] { 14 });
    private static readonly byte[] E7Empty = [14];

    /// <summary>
    /// A CID-keyed CFF with the Top DICT entries in the given order: „ros“, „count“ (CIDCount), „charset“, „charstrings“, „fdarray“,
    /// „fdselect“; one Font DICT (defaultWidthX 0), FDSelect format 0 with Font DICT 0 for every glyph; charset format 0 from <paramref name="cids"/>.
    /// </summary>
    private static byte[] E7CidCff(byte[][] glyphs, int[]? cids, string[] order, int cidCount = 8720)
    {
        var name = Cff01Index("Probe"u8.ToArray());
        var strings = Cff01Index("Adobe"u8.ToArray(), "Identity"u8.ToArray());
        var global = Cff01Index();
        byte[] Top(int charsetAt, int charStringsAt, int fdArrayAt, int fdSelectAt)
        {
            var d = new List<byte>();
            foreach (var entry in order)
                d.AddRange(entry switch
                {
                    "ros" => [.. Cff01Five(391), .. Cff01Five(392), .. Cff01Five(0), 12, 30],
                    "count" => [.. Cff01Five(cidCount), 12, 34],
                    "charset" when cids is not null => [.. Cff01Five(charsetAt), 15],
                    "charstrings" => [.. Cff01Five(charStringsAt), 17],
                    "fdarray" => [.. Cff01Five(fdArrayAt), 12, 36],
                    "fdselect" => [.. Cff01Five(fdSelectAt), 12, 37],
                    _ => [],
                });
            return [.. d];
        }
        var charset = cids is null ? [] : new List<byte> { 0 }.Concat(cids.SelectMany(c => new[] { (byte)(c >> 8), (byte)c })).ToArray();
        byte[] fdSelect = [0, .. new byte[glyphs.Length]];
        var charStrings = Cff01Index(glyphs);
        byte[] priv = [139, 20];
        var topSize = Cff01Index(Top(0, 0, 0, 0)).Length;
        var charsetAt = 4 + name.Length + topSize + strings.Length + global.Length;
        var fdSelectAt = charsetAt + charset.Length;
        var charStringsAt = fdSelectAt + fdSelect.Length;
        var fdArrayAt = charStringsAt + charStrings.Length;
        var privateAt = fdArrayAt + Cff01Index([.. Cff01Five(0), .. Cff01Five(0), 18]).Length;
        return [1, 0, 4, 4, .. name, .. Cff01Index(Top(charsetAt, charStringsAt, fdArrayAt, fdSelectAt)), .. strings, .. global, .. charset,
                .. fdSelect, .. charStrings, .. Cff01Index([.. Cff01Five(priv.Length), .. Cff01Five(privateAt), 18]), .. priv];
    }

    private static readonly string[] E7RosFirst = ["ros", "charset", "charstrings", "fdarray", "fdselect"];

    // ------------------------------------------------------------ TrueType cmap helpers (E7)

    private static readonly byte[] E7Liberation = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
    private static readonly OpenTypeFont E7LiberationFont = OpenTypeFont.Parse(E7Liberation);

    /// <summary>cmap format 4 with one segment per code and the final 0xFFFF segment.</summary>
    private static byte[] E7Format4(IReadOnlyDictionary<int, int> map)
    {
        var codes = map.Keys.Order().Append(0xFFFF).ToArray();
        var seg = codes.Length;
        var log = (int)Math.Log2(seg);
        var d = new List<byte>();
        void U16(int v) => d.AddRange([(byte)(v >> 8), (byte)v]);
        U16(4); U16(16 + 8 * seg); U16(0); U16(2 * seg); U16(2 << log); U16(log); U16(2 * seg - (2 << log));
        foreach (var c in codes) U16(c);
        U16(0);
        foreach (var c in codes) U16(c);
        foreach (var c in codes) U16(c == 0xFFFF ? 1 : (map[c] - c) & 0xFFFF);
        foreach (var _ in codes) U16(0);
        return [.. d];
    }

    private static byte[] E7Format0(IReadOnlyDictionary<int, int> map)
    {
        var d = new byte[262];
        d[1] = 0; d[3] = 6; d[2] = 1;   // format 0, length 262, language 0
        foreach (var (c, g) in map) d[6 + c] = (byte)g;
        return d;
    }

    private static byte[] E7CmapTable(params ((int Platform, int Encoding) Id, byte[] Data)[] subtables)
    {
        var d = new List<byte> { 0, 0, 0, (byte)subtables.Length };
        var at = 4 + 8 * subtables.Length;
        foreach (var ((p, e), data) in subtables)
        {
            d.AddRange([0, (byte)p, 0, (byte)e, (byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at]);
            at += data.Length;
        }
        foreach (var (_, data) in subtables) d.AddRange(data);
        return [.. d];
    }

    /// <summary>Liberation Sans with another cmap; <paramref name="withoutNames"/> writes post version 3 (no glyph names).</summary>
    private static byte[] E7WithCmap(bool withoutNames, params ((int, int), byte[])[] subtables)
    {
        var changed = new Dictionary<string, byte[]?> { ["cmap"] = E7CmapTable(subtables) };
        if (withoutNames)
        {
            var post = E7LiberationFont.TableData("post")[..32].ToArray();
            post[0] = 0; post[1] = 3; post[2] = 0; post[3] = 0;
            changed["post"] = post;
        }
        return ColorFontBuilder.WithTables(E7Liberation, changed);
    }

    /// <summary>The outline bounds the display draws for <paramref name="code"/> with the font program embedded as a simple TrueType font.</summary>
    private static RectD E7TrueTypeGlyph(byte[] program, int flags, string? encoding, int code)
    {
        var enc = encoding is null ? "" : $"/Encoding {encoding} ";
        using var doc = PdfDocument.Open(E7File("BT /F2 12 Tf (AB) Tj ET", "/Font << /F2 5 0 R >>",
            $"<< /Type /Font /Subtype /TrueType /BaseFont /Probe {enc}/FirstChar 65 /LastChar 66 /Widths [667 667] /FontDescriptor 6 0 R >>",
            $"<< /Type /FontDescriptor /FontName /Probe /Flags {flags} /FontBBox [-544 -303 1302 980] /ItalicAngle 0 /Ascent 905 /Descent -212 " +
            "/CapHeight 729 /StemV 80 /FontFile2 7 0 R >>",
            E7Stream("", program)));
        var dict = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F2"))!;
        return PdfFont.Get(dict, doc).Glyph((uint)code, 1).Outline.Bounds;
    }

    private static RectD E7GlyphBounds(char c) => E7LiberationFont.Outline(E7LiberationFont.GlyphForUnicode(c)).Bounds;

    // ------------------------------------------------------------ question 22: d1 (ISO 32000-2, 9.6.4)

    /// <summary>
    /// 9.6.4 sentence 89: d1 belongs only in the glyph descriptions of a Type 3 font. A d1 in the page content is passed over, as two of three
    /// reference readers do (probe D1a): the red square after it stays red. Before, it switched the colour operators off for the rest of the
    /// page and the square came out black.
    /// </summary>
    [Fact]
    public void E7_D1InPageContent_PassedOver_TheColourStays()
    {
        var c = E7Pixel(E7File("1000 0 0 0 10 10 d1 1 0 0 rg 50 50 100 100 re f"), 100, 100);
        Assert.True(c is { Red: > 200, Green: < 60, Blue: < 60 }, $"{c}");
    }

    /// <summary>
    /// 9.6.4 sentences 85–88: after d1 the glyph takes its colour from the text, transparency settings of a graphics state included — a blend
    /// mode set inside the d1 glyph is passed over (as one of three readers; the other two leave the glyph out entirely, probe D1d). The blue glyph
    /// over yellow stays blue. Before, it was multiplied to black.
    /// </summary>
    [Fact]
    public void E7_D1Glyph_BlendModeOfGs_PassedOver()
    {
        var file = E7Type3("1000 0 0 0 1000 1000 d1 /M gs 0 0 1000 1000 re f", "/ExtGState << /M << /BM /Multiply >> >>", "1 1 0 rg 0 0 200 200 re f 0 0 1 rg");
        var c = E7Pixel(file, 100, 100);
        Assert.True(c is { Blue: > 200, Red: < 60, Green: < 60 }, $"{c}");
    }

    /// <summary>
    /// Special rule (9.6.4 sentences 85–88, measured with the real file issue19634 and probes SM1–SM8, D1f): a soft mask set inside a d1 glyph
    /// applies — the right half the mask hides stays unpainted. one reference reader masks in every probe, one where the mask shows an image (as in the
    /// real file; with filled areas it leaves the glyph out), the third passes the mask over: two of three mask the real file
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1201). Before, the mask was passed over and the whole glyph painted.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void E7_D1Glyph_SoftMaskOfGs_Applies_AsTwoOfThreeReaders(bool image)
    {
        var mask = image
            ? E7Stream("/Type /XObject /Subtype /Form /BBox [0 0 1000 1000] /Group << /S /Transparency /I true /CS /DeviceGray >> " +
                       "/Resources << /XObject << /I 8 0 R >> >>", "q 1000 0 0 1000 0 0 cm /I Do Q")
            : E7Stream("/Type /XObject /Subtype /Form /BBox [0 0 1000 1000] /Group << /S /Transparency /CS /DeviceGray >>",
                "1 g 0 0 500 1000 re f 0 g 500 0 500 1000 re f");
        var halves = new byte[16 * 16];
        for (var i = 0; i < halves.Length; i++) halves[i] = (byte)(i % 16 < 8 ? 255 : 0);
        var file = E7Type3("1000 0 0 0 1000 1000 d1 /S gs 0 0 1000 1000 re f", "/ExtGState << /S << /SMask << /S /Luminosity /G 7 0 R >> >> >>", "0 0 1 rg",
            mask, E7Stream("/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceGray /BitsPerComponent 8", halves));
        var left = E7Pixel(file, 75, 100);
        Assert.True(left is { Blue: > 200, Red: < 60, Green: < 60 }, $"{left}");
        var right = E7Pixel(file, 125, 100);
        Assert.True(right is { Blue: > 200, Red: > 200, Green: > 200 }, $"{right}");
    }

    /// <summary>
    /// Special rule (9.6.4 sentences 85–88, measured with probe D1b): the constant opacity of a graphics state set inside a d1 glyph still applies,
    /// as two of three reference readers draw it — the glyph at /ca 0.3 is a light blue, not the full blue of the text.
    /// </summary>
    [Fact]
    public void E7_D1Glyph_ConstantOpacityOfGs_StillApplies_AsTwoOfThreeReaders()
    {
        var file = E7Type3("1000 0 0 0 1000 1000 d1 /A gs 0 0 1000 1000 re f", "/ExtGState << /A << /ca 0.3 >> >>");
        var c = E7Pixel(file, 100, 100);
        Assert.True(c is { Blue: > 240, Red: > 150, Green: > 150 }, $"{c}");
    }

    // ------------------------------------------------------------ question 23: /TK (ISO 32000-2, 9.3.8; 9.6.4 sentence 61)

    private const string E7Overlap = "BT /F1 100 Tf 50 50 Td [(l) 222 (l)] TJ ET";   // the second glyph on the first
    private const string E7Single = "BT /F1 100 Tf 50 50 Td (l) Tj ET";

    /// <summary>
    /// 9.3.8 sentences 2–11, 17: with /TK true a text object acts as a non-isolated knockout group — a later glyph replaces an earlier one where
    /// they overlap, so two glyphs drawn on top of each other under /ca 0.5 are no darker than one (as a reference reader draws it; probes TK1, TK3). Both on the page
    /// and inside a transparency group. Before, they were darker where they overlapped.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void E7_TextKnockout_True_OverlappingGlyphsOfOneTextObject_CoverOnce(bool inGroup)
    {
        byte[] File(string text)
        {
            const string gs = "/ExtGState << /G << /TK true /ca 0.5 >> >>";
            if (!inGroup) return E7File($"/G gs 0 g {text}", $"{gs} {E7Helvetica}");
            return E7File("/X Do", "/XObject << /X 5 0 R >>",
                E7Stream($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /K false >> /Resources << {gs} {E7Helvetica} >>",
                    $"/G gs 0 g {text}"));
        }
        var once = E7Darkest(File(E7Single));
        Assert.InRange(once, 100, 160);
        Assert.InRange(E7Darkest(File(E7Overlap)), once - 8, once + 8);
    }

    /// <summary>9.3.8 sentence 8: the initial value of /TK is true — without /TK the overlapping glyphs under /ca 0.5 are knocked out as well.</summary>
    [Fact]
    public void E7_TextKnockout_InitiallyTrue()
    {
        var resources = $"/ExtGState << /G << /ca 0.5 >> >> {E7Helvetica}";
        var once = E7Darkest(E7File($"/G gs 0 g {E7Single}", resources));
        Assert.InRange(E7Darkest(E7File($"/G gs 0 g {E7Overlap}", resources)), once - 8, once + 8);
    }

    /// <summary>9.3.8 sentences 9–10: with /TK false every glyph is an object of its own — the overlap under /ca 0.5 is darker than one glyph.</summary>
    [Fact]
    public void E7_TextKnockout_False_OverlapsBlend()
    {
        var resources = $"/ExtGState << /G << /TK false /ca 0.5 >> >> {E7Helvetica}";
        var once = E7Darkest(E7File($"/G gs 0 g {E7Single}", resources));
        Assert.True(E7Darkest(E7File($"/G gs 0 g {E7Overlap}", resources)) < once - 30);
    }

    /// <summary>9.3.8 sentence 5: the knockout covers one text object — glyphs of two text objects on top of each other still blend (probe TK6).</summary>
    [Fact]
    public void E7_TextKnockout_TwoTextObjects_BlendWithEachOther()
    {
        var resources = $"/ExtGState << /G << /TK true /ca 0.5 >> >> {E7Helvetica}";
        var once = E7Darkest(E7File($"/G gs 0 g {E7Single}", resources));
        var twice = E7Darkest(E7File($"/G gs 0 g {E7Single} BT /F1 100 Tf 50 50 Td (l) Tj ET", resources));
        Assert.True(twice < once - 30, $"{twice} gegen {once}");
    }

    /// <summary>
    /// 9.3.8 sentences 3–4: /TK set by a graphics state between BT and ET is passed over — the object keeps the knockout it began with (the
    /// initial true), so the overlap is no darker.
    /// </summary>
    [Fact]
    public void E7_TextKnockout_SetInsideTheTextObject_PassedOver()
    {
        var resources = $"/ExtGState << /G << /ca 0.5 >> /F << /TK false >> >> {E7Helvetica}";
        var once = E7Darkest(E7File($"/G gs 0 g {E7Single}", resources));
        Assert.InRange(E7Darkest(E7File($"/G gs 0 g BT /F gs /F1 100 Tf 50 50 Td [(l) 222 (l)] TJ ET", resources)), once - 8, once + 8);
    }

    /// <summary>9.6.4 sentence 61: /TK covers the glyphs of Type 3 fonts as well — two square glyphs overlapping under /ca 0.5 are no darker than one.</summary>
    [Fact]
    public void E7_TextKnockout_Type3Glyphs()
    {
        byte[] File(string text) => E7File($"/G gs 0 g {text}", "/ExtGState << /G << /ca 0.5 >> >> /Font << /F3 5 0 R >>",
            "<< /Type /Font /Subtype /Type3 /FontMatrix [0.001 0 0 0.001 0 0] /FontBBox [0 0 1000 1000] /CharProcs << /g 6 0 R >> " +
            "/Encoding << /Differences [97 /g] >> /FirstChar 97 /LastChar 97 /Widths [1000] >>",
            E7Stream("", "1000 0 d0 0 0 1000 1000 re f"));
        var once = E7Darkest(File("BT /F3 60 Tf 40 40 Td (a) Tj ET"));
        Assert.InRange(E7Darkest(File("BT /F3 60 Tf 40 40 Td [(a) 500 (a)] TJ ET")), once - 8, once + 8);
    }

    /// <summary>
    /// The knockout group of a text object costs a layer only where it can be seen: opaque text with the blend mode Normal (nearly all text) gets
    /// no layer of its own.
    /// </summary>
    [Fact]
    public void E7_TextKnockout_OpaqueText_NoLayer()
    {
        using var doc = PdfDocument.Open(E7File($"0 g {E7Overlap}", E7Helvetica));
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        Assert.DoesNotContain(list.Groups.ToArray(), g => g.NeedsLayer);
    }

    // ------------------------------------------------------------ question 24: text (B12)

    /// <summary>
    /// 9.3.1 sentences 38–41 (Tr takes an integer of 0–7, Table 104): a mode outside 0–7 is passed over and the mode before it stays, as one
    /// reference reader does; the other two agree with that on what can be seen (probes B12a, B12b). „0 Tr 9 Tr“ fills the text; „3 Tr -1 Tr“
    /// leaves it invisible. Before, 9 became 7 (a clip: nothing visible) and −1 became 0 (visible), as no reader shows it.
    /// </summary>
    [Fact]
    public void E7_TextRenderingModeOutsideZeroToSeven_PassedOver()
    {
        Assert.True(E7Ink(E7File("BT /F1 60 Tf 0 Tr 9 Tr 20 100 Td (Hello) Tj ET 0 0 1 rg 0 0 200 50 re f", E7Helvetica)) > 200 * 50 + 500);
        Assert.Equal(200 * 50, E7Ink(E7File("BT /F1 60 Tf 3 Tr -1 Tr 20 100 Td (Hello) Tj ET 0 0 1 rg 0 0 200 50 re f", E7Helvetica)));
    }

    /// <summary>
    /// 9.3.1 sentence 36 (font and size have no initial value): text shown before any Tf is drawn by none of the three reference readers
    /// (probe B12d); the display draws nothing either. It is still read as text in size 1 (search, copying, redaction find it, as invisible text).
    /// Before, it was drawn with the substitute font.
    /// </summary>
    [Fact]
    public void E7_TextWithoutTf_NotDrawn_ButReadAsText()
    {
        var file = E7File("BT 60 0 0 60 20 100 Tm (Hello) Tj ET", E7Helvetica);
        Assert.Equal(0, E7Ink(file));
        using var doc = PdfDocument.Open(file);
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        Assert.Equal("Hello", string.Concat(list.Text.Select(t => t.Text)));
    }

    /// <summary>
    /// Special rule (9.2.2 sentences 9–13, V14; probe B12e): Tf naming a font the resources do not have — the text is drawn with the substitute font
    /// in the size of Tf, as two of three reference readers draw it.
    /// </summary>
    [Fact]
    public void E7_TfNamingAMissingFont_DrawnWithTheSubstitute_AsTwoOfThreeReaders()
    {
        Assert.True(E7Ink(E7File("BT /F9 60 Tf 20 100 Td (Hello) Tj ET", E7Helvetica)) > 1000);
    }

    /// <summary>Special rule (9.4.1: text operators only between BT and ET; probe B12c): Tf, Td and Tj without BT are drawn, as by all three readers.</summary>
    [Fact]
    public void E7_TextOperatorsOutsideBtEt_Drawn_AsTheReferenceReaders()
    {
        Assert.True(E7Ink(E7File("/F1 60 Tf 20 100 Td (Hello) Tj", E7Helvetica)) > 1000);
    }

    // ------------------------------------------------------------ question 24: CFF (B36, B72)

    /// <summary>
    /// TN 5176, 13 and Appendix C: the predefined charsets name 229 (ISOAdobe), 166 (Expert) and 87 (ExpertSubset) glyphs. A font with more glyphs
    /// than its predefined charset names is not read, as all three reference readers reject it and draw the substitute (probes B36d–B36i, B36b2);
    /// for CID-keyed fonts without a charset the same. Exactly as many glyphs are read as before. Before, the glyphs beyond took the following SIDs.
    /// </summary>
    [Fact]
    public void E7_Cff_PredefinedCharsetShorterThanTheGlyphs_NotRead()
    {
        CffFont Font(int? charset, int glyphs) =>
            CffFont.Parse(Cff01Build(new Cff01Spec { CharsetId = charset, CharStrings = [.. Enumerable.Repeat(new byte[] { 14 }, glyphs)] }));
        Assert.Equal("zcaron", Font(null, 229).GlyphName(228));
        Assert.Throws<FontFormatException>(() => Font(null, 232));
        Assert.Equal("Ydieresissmall", Font(1, 166).GlyphName(165));
        Assert.Throws<FontFormatException>(() => Font(1, 170));
        Assert.Equal("commainferior", Font(2, 87).GlyphName(86));
        Assert.Throws<FontFormatException>(() => Font(2, 88));
        Assert.Equal(228, CffFont.Parse(E7CidCff([.. Enumerable.Repeat(E7Empty, 229)], null, E7RosFirst)).GlyphByCid(228));
        Assert.Throws<FontFormatException>(() => CffFont.Parse(E7CidCff([.. Enumerable.Repeat(E7Empty, 240)], null, E7RosFirst)));
    }

    /// <summary>
    /// TN 5176, 19 (FDSelect names a Font DICT of the FDArray) and CID 3.2 sentence 210: a glyph whose FDSelect value names no Font DICT draws
    /// nothing, as two of three reference readers (probes B36c, B36c2: the other glyphs of the font are drawn); its width stays that of Font DICT 0. Cleaning
    /// the font keeps its FDSelect value, so it stays empty there too (every reader shows the cleaned font as before). Before, it was drawn with
    /// Font DICT 0.
    /// </summary>
    [Fact]
    public void E7_Cff_GlyphWhoseFdSelectNamesNoFontDict_DrawsNothing()
    {
        var data = Cid01CidCff([1, 2], defaultWidths: [700, 300], fdSelect: [0, 1, 5]);
        var font = CffFont.Parse(data);
        Assert.False(font.Outline(1).IsEmpty);
        Assert.True(font.Outline(2).IsEmpty);
        Assert.Equal(700, font.AdvanceWidth(2));
        var scrubbed = CffFont.Parse(CffScrubber.Scrub(data, [1, 2]).Data);
        Assert.False(scrubbed.Outline(1).IsEmpty);
        Assert.True(scrubbed.Outline(2).IsEmpty);
    }

    /// <summary>Special rule (TN 5176, 18: ROS first in the Top DICT of a CIDFont; probe B36a): ROS later in the Top DICT still makes the font CID-keyed, as for all three readers.</summary>
    [Fact]
    public void E7_Cff_RosNotFirst_StillCidKeyed_AsTheReferenceReaders()
    {
        var font = CffFont.Parse(E7CidCff([E7Empty, E7Square, E7Empty, E7Empty, E7Empty, E7Tall], [5, 6, 7, 8, 9],
            ["charstrings", "charset", "ros", "fdarray", "fdselect"]));
        Assert.True(font.IsCidKeyed);
        Assert.Equal(1, font.GlyphByCid(5));
    }

    /// <summary>Special rule (CID 4.2 sentences 5–8, CIDCount; probe B72): a CID at or above CIDCount still finds its glyph, as for all three readers.</summary>
    [Fact]
    public void E7_Cff_CidAboveCidCount_StillDrawn_AsTheReferenceReaders()
    {
        var font = CffFont.Parse(E7CidCff([E7Empty, E7Square], [5], ["ros", "count", "charset", "charstrings", "fdarray", "fdselect"], cidCount: 3));
        Assert.Equal(1, font.GlyphByCid(5));
        Assert.False(font.Outline(1).IsEmpty);
    }

    /// <summary>Special rule (TN 5176, 13: charset offset 0 means ISOAdobe; probe B36b): a CID-keyed font without a charset maps CID n to glyph n, as all three readers.</summary>
    [Fact]
    public void E7_Cff_CidKeyedWithoutCharset_CidIsGlyph_AsTheReferenceReaders()
    {
        var font = CffFont.Parse(E7CidCff([E7Empty, E7Tall, E7Tall, E7Square], null, E7RosFirst));
        Assert.Equal(3, font.GlyphByCid(3));
    }

    // ------------------------------------------------------------ question 24: ZapfDingbats and CMaps (B45, B61, B63)

    /// <summary>
    /// Special rule (Annex D.6 lists no character at 128–141): ZapfDingbats not embedded draws the bracket ornaments a89–a96 at codes 128–141 (octal
    /// 200–215), as the two readers that have the font (probe B45; the third draws neither these nor the codes of D.6).
    /// </summary>
    [Fact]
    public void E7_ZapfDingbats_Codes128To141_DrawTheBracketOrnaments_AsTheReferenceReaders()
    {
        using var doc = PdfDocument.Open(E7File("BT /F2 20 Tf (\x80) Tj ET", "/Font << /F2 << /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >> >>"));
        var font = PdfFont.Get(doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F2"))!, doc);
        for (uint code = 0x80; code <= 0x8D; code++) Assert.False(font.Glyph(code, 1).Outline.IsEmpty, $"Code {code}");
    }

    private static CMap E7CMap(string body, string codespace = "1 begincodespacerange <00> <FF> endcodespacerange\n", Func<string, CMap?>? use = null) =>
        CMap.Parse(Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Probe def /CMapType 1 def\n" +
                                           codespace + body + "\nendcmap CMapName currentdict /CMap defineresource pop end end\n"), use);

    /// <summary>
    /// Special rules (CID 7.4 sentence 111 and 5.4 sentence 25; probes B61, B63b): the CMap's own mappings go before those of the CMap it uses, wherever
    /// its usecmap stands — „<41>“ takes the own CID, „<42>“ the one of the base, as two of three readers (the third draws nothing for B61, all four
    /// alike for B63b).
    /// </summary>
    [Fact]
    public void E7_CMap_OwnMappingsBeforeTheUsedCMap_WhereverUsecmapStands()
    {
        var baseMap = E7CMap("2 begincidchar <41> 61 <42> 59 endcidchar");
        CMap? Use(string name) => name == "Base" ? baseMap : null;
        foreach (var body in (string[])["/Base usecmap\n1 begincidchar <41> 36 endcidchar", "1 begincidchar <41> 36 endcidchar\n/Base usecmap"])
        {
            var cmap = E7CMap(body, "", Use);
            Assert.Equal(36, cmap.ToCid(0x41, 1));
            Assert.Equal(59, cmap.ToCid(0x42, 1));
        }
    }

    /// <summary>
    /// Special rules (CID 5.2, 7.3; probes B63a, B63c): mappings before the code space and a block of more than 100 entries are read, as by all
    /// three readers.
    /// </summary>
    [Fact]
    public void E7_CMap_MappingsBeforeTheCodespace_AndBlocksOverAHundred_Read()
    {
        var before = CMap.Parse(Encoding.ASCII.GetBytes("begincmap 1 begincidchar <41> 36 endcidchar\n1 begincodespacerange <00> <FF> endcodespacerange endcmap"));
        Assert.Equal(36, before.ToCid(0x41, 1));
        var entries = new StringBuilder("150 begincidchar\n");
        for (var i = 0; i < 120; i++) entries.Append($"<{0x80 + i:X2}> 61\n");
        entries.Append("<41> 36\n");
        for (var i = 0; i < 29; i++) entries.Append($"<{0x10 + i:X2}> 61\n");
        Assert.Equal(36, E7CMap(entries.Append("endcidchar").ToString()).ToCid(0x41, 1));
    }

    /// <summary>
    /// Special rule (CID 5.2 sentences 95–98: code space ranges must not overlap; probe B63d): with a one-byte range over all bytes and a two-byte
    /// range for 41xx, „41 42“ is read as two one-byte codes — the shortest range that matches, as ISO 32000-2, 9.7.6.2 reads codes and as one
    /// reference reader does (the other two take the two-byte code).
    /// </summary>
    [Fact]
    public void E7_CMap_OverlappingCodespaces_ShortestMatchFirst()
    {
        var cmap = E7CMap("2 begincidchar <41> 36 <42> 59 endcidchar\n1 begincidchar <4142> 61 endcidchar",
            "2 begincodespacerange <00> <FF> <4100> <41FF> endcodespacerange\n");
        Assert.Equal(1, cmap.ReadCode([0x41, 0x42], 0, out var code));
        Assert.Equal(0x41u, code);
    }

    // ------------------------------------------------------------ question 24: cmap subtables (OT 9.3 sentence 11, ISO 32000-2 9.6.5.4)

    /// <summary>
    /// ISO 32000-2, 9.6.5.4 sentences 27–31 and OpenType 9.3 sentence 11: a non-symbolic TrueType font with a (3,1) subtable takes the glyph from
    /// it, else by the glyph name from post — not from (1,0) or another Unicode subtable. „B“ missing in (3,1) but mapped to „Z“ in (1,0) or (0,3):
    /// without names glyph 0 (it has a width), with names the „B“ of post, as all three reference readers (probes CM31, CM31p, CM31u; one draws
    /// nothing instead of glyph 0). Before, the „Z“.
    /// </summary>
    [Fact]
    public void E7_NonSymbolicTrueType_With31Cmap_NoOtherSubtable()
    {
        int a = E7LiberationFont.GlyphForUnicode('A'), z = E7LiberationFont.GlyphForUnicode('Z');
        var mac = E7Format0(new Dictionary<int, int> { [65] = a, [66] = z });
        var unicode = E7Format4(new Dictionary<int, int> { [0x41] = a });
        var notdef = E7LiberationFont.Outline(0).Bounds;
        Assert.Equal(notdef, E7TrueTypeGlyph(E7WithCmap(true, ((1, 0), mac), ((3, 1), unicode)), 32, "/WinAnsiEncoding", 66));
        Assert.Equal(E7GlyphBounds('B'), E7TrueTypeGlyph(E7WithCmap(false, ((1, 0), mac), ((3, 1), unicode)), 32, "/WinAnsiEncoding", 66));
        var otherUnicode = E7Format4(new Dictionary<int, int> { [0x41] = a, [0x42] = z });
        Assert.Equal(notdef, E7TrueTypeGlyph(E7WithCmap(true, ((0, 3), otherUnicode), ((3, 1), unicode)), 32, "/WinAnsiEncoding", 66));
        Assert.Equal(E7GlyphBounds('A'), E7TrueTypeGlyph(E7WithCmap(true, ((1, 0), mac), ((3, 1), unicode)), 32, "/WinAnsiEncoding", 65));
    }

    /// <summary>
    /// ISO 32000-2, 9.6.5.4 sentences 51–53: a symbolic TrueType font with a (3,0) subtable takes the code from it only — a code (3,0) lacks draws
    /// glyph 0, not the „Z“ (1,0) maps it to (probes CM30, CM30p: two of three reference readers glyph 0, the third the post name). Before, the „Z“.
    /// </summary>
    [Fact]
    public void E7_SymbolicTrueType_With30Cmap_NoOtherSubtable()
    {
        int a = E7LiberationFont.GlyphForUnicode('A'), z = E7LiberationFont.GlyphForUnicode('Z');
        var program = E7WithCmap(true, ((1, 0), E7Format0(new Dictionary<int, int> { [65] = a, [66] = z })), ((3, 0), E7Format4(new Dictionary<int, int> { [0xF041] = a })));
        Assert.Equal(E7LiberationFont.Outline(0).Bounds, E7TrueTypeGlyph(program, 4, null, 66));
        Assert.Equal(E7GlyphBounds('A'), E7TrueTypeGlyph(program, 4, null, 65));
    }

    /// <summary>ISO 32000-2, 9.6.5.4 sentences 29–30: without (3,1) the non-symbolic font takes (1,0) — „B“ there is the „Z“ (unchanged).</summary>
    [Fact]
    public void E7_NonSymbolicTrueType_Without31Cmap_Takes10()
    {
        int a = E7LiberationFont.GlyphForUnicode('A'), z = E7LiberationFont.GlyphForUnicode('Z');
        var program = E7WithCmap(true, ((1, 0), E7Format0(new Dictionary<int, int> { [65] = a, [66] = z })));
        Assert.Equal(E7GlyphBounds('Z'), E7TrueTypeGlyph(program, 32, "/WinAnsiEncoding", 66));
    }

    // ------------------------------------------------------------ question 26: licence of embedded fonts (9.9.1 sentences 8–10, OT 31.2.5)

    /// <summary>A file whose text uses an embedded Liberation Sans with the given fsType (for the notice when editing).</summary>
    internal static byte[] E7FileWithFsType(int fsType) => PdfARuleCases.WithTrueType(program: () => Ot30WithFsType(fsType))();

    /// <summary>
    /// OT 31.2.5 (usage bits 0–3; of several the least restrictive applies; bits 8 and 9 are about embedding, not editing) and ISO 32000-2, 9.9.1
    /// sentences 8–10: the licence forbids new or changed text only with „restricted“ (2) or „preview and print“ (4) without „editable“ (8).
    /// </summary>
    [Theory]
    [InlineData(0x0000, false)]
    [InlineData(0x0002, true)]
    [InlineData(0x0004, true)]
    [InlineData(0x0006, true)]
    [InlineData(0x0008, false)]
    [InlineData(0x000C, false)]
    [InlineData(0x0304, true)]
    [InlineData(0x0300, false)]
    public void E7_FontLicence_OnlyRestrictedOrPreviewAndPrintForbidEditing(int fsType, bool forbids)
    {
        using var doc = PdfDocument.Open(E7FileWithFsType(fsType));
        var font = PdfFont.Get(doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!, doc);
        Assert.Equal(forbids, FontLicence.ForbidsEditing(font));
    }
}

/// <summary>
/// The notice when editing text whose embedded font the licence allows only for viewing and printing (ISO 32000-2, 9.9.1 sentences 8–10; OpenType
/// OS/2 fsType; docs/FEHLERGEDAECHTNIS.md, No. 1165): shown once per font when its block is opened for editing; editing stays possible.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class E7LicenceNoticeUiTests
{
    /// <summary>
    /// fsType 4 (preview and print): opening the text block for editing says that the licence of its font does not allow new or changed text with
    /// it; the block is still edited. fsType 8 (editable) says nothing.
    /// </summary>
    [Theory]
    [InlineData(0x0004, true)]
    [InlineData(0x0008, false)]
    public void EditingTextWithAPreviewAndPrintFont_GivesTheNotice(int fsType, bool notice)
    {
        var file = TestFiles.Write(M7NormTests.E7FileWithFsType(fsType), $"lizenz {fsType}.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                vm.ToggleEditCommand.Execute(null);
                IReadOnlyList<Core.Pdf.Text.PageTextBlock>? blocks = null;
                var until = DateTime.UtcNow.AddSeconds(30);
                while ((blocks = doc.BlocksOn(0)) is null && DateTime.UtcNow < until) await Task.Delay(20);
                Assert.NotNull(blocks);
                vm.Message = "";
                var editor = await doc.EditBlockAsync(0, Assert.Single(blocks));
                Assert.NotNull(editor);
                var hint = Tr.T("Die Schrift „{0}“ ist nach ihrer Lizenz nur zum Ansehen und Drucken eingebettet — neuen oder geänderten Text mit ihr erlaubt die Lizenz nicht.", "LiberationSans");
                if (notice) Assert.Equal(hint, vm.Message);
                else Assert.NotEqual(hint, vm.Message);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
