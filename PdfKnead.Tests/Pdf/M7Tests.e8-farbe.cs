using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Fonts;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, decisions of package E8 (docs/FEHLERGEDAECHTNIS.md, No. 1168): COLR version 0 for text and signatures the program sets with installed
/// colour fonts (version 1 on purpose not), the colour tables left out of compressed fonts, and fonts with CFF outlines embedded as OpenType
/// with their OS/2 table.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (E8)

    /// <summary>
    /// Makes <paramref name="font"/> the font of the family <paramref name="family"/> for formatted text, as if it were installed: regular, and the
    /// bold italic style faked on it (the choice of <see cref="RichFonts"/> remembered for both).
    /// </summary>
    private static void E8UseFamily(string family, OpenTypeFont font)
    {
        var key = "test:" + family;
        ElementFace.For(key, () => font, false, false, false);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var cache = (System.Collections.IDictionary)typeof(RichFonts).GetField("Cache", flags)!.GetValue(null)!;
        var gate = (Lock)typeof(RichFonts).GetField("Gate", flags)!.GetValue(null)!;
        using (gate.EnterScope())
        {
            cache[(family, false, false)] = (key, false, false, true);
            cache[(family, true, true)] = (key, true, true, true);
        }
    }

    /// <summary>
    /// Liberation Sans with COLR version 0 and CPAL: „A“ has the layers „O“ (palette entry 0, red), „I“ (0xFFFF, the text colour) and „-“ (entry 1,
    /// green with alpha 128).
    /// </summary>
    private static byte[] E8ColrFont()
    {
        var colr = Ot14Colr(version: 0, baseGlyphs: [(Ot14Gid('A'), 0, 3)], layerRecords: [(Ot14Gid('O'), 0), (Ot14Gid('I'), 0xFFFF), (Ot14Gid('-'), 1)]);
        var cpal = Ot14Cpal(2, [0], [Ot14RedColour, new Ot14Bgra(0, 255, 0, 128)]);
        return ColorFontBuilder.WithTables(Ot14SansFile.Value, new Dictionary<string, byte[]?> { ["COLR"] = colr, ["CPAL"] = cpal });
    }

    /// <summary>The fill colour (r g b rg) set last before <paramref name="at"/> in a content stream.</summary>
    private static string E8ColourBefore(string content, int at)
    {
        var end = content.LastIndexOf(" rg", at, StringComparison.Ordinal);
        var start = content.LastIndexOf("Tf ", end, StringComparison.Ordinal) + 3;
        return content[start..end];
    }

    /// <summary>A formatted text element with <paramref name="text"/> in blue in the family given.</summary>
    private static RichTextElement E8Text(string family, string text, bool boldItalic = false) => new()
    {
        Rect = new PdfRectangle(10, 10, 300, 80),
        Content = new RichText(text, new RichStyle { Family = family, Size = 40, Color = 0x0000FF, Bold = boldItalic, Italic = boldItalic }),
    };

    // ------------------------------------------------------------ question 3: COLR version 0 for the program's own text

    /// <summary>
    /// OT 10.1 sentences 1–6, 11–13, 22; 10.2.1 sentences 9–13; 10.2.2 sentences 1–9; 10.2.5 sentences 1–8; 10.3.3 sentences 1–25; 11.2 sentences 1–17;
    /// 11.6.1 sentences 1–4 (writer, own text; question 3): typed text in an installed font with COLR version 0 draws the colour glyph „A“ as its
    /// layers, bottom first, at the place of „A“ — „O“ in the red of palette entry 0, „I“ in the text colour (entry 0xFFFF, blue), „-“ in the green
    /// of entry 1 with its alpha 128 as constant alpha (and back to 1 after it); „A“ itself is shown invisibly (3 Tr) below them, so that its text
    /// and place are found through ToUnicode, the whole is wrapped in /ActualText „A“, and the saved subset holds „A“ and the layer glyphs. „B“ (no
    /// colour glyph) draws as before. Saved and opened again, the page reads „AB“ and shows red and blue.
    /// </summary>
    [Fact]
    public void E8_ColrV0OwnText_LayersInPaletteColoursWithActualText()
    {
        E8UseFamily("E8 Farbprobe", OpenTypeFont.Parse(E8ColrFont()));
        var element = E8Text("E8 Farbprobe", "AB");
        var appearance = ElementAppearance.For(element);
        var content = Encoding.ASCII.GetString(appearance.Form.RawData!);
        int a = Ot14Gid('A'), b = Ot14Gid('B'), o = Ot14Gid('O'), i = Ot14Gid('I'), hyphen = Ot14Gid('-');
        var atA = content.IndexOf($"[<{a:X4}>] TJ", StringComparison.Ordinal);
        Assert.True(atA > 0 && content.LastIndexOf(" 3 Tr ", atA, StringComparison.Ordinal) > content.LastIndexOf(" Tf ", atA, StringComparison.Ordinal), content);
        int atO = content.IndexOf($"[<{o:X4}>] TJ", StringComparison.Ordinal), atI = content.IndexOf($"[<{i:X4}>] TJ", StringComparison.Ordinal);
        int atHyphen = content.IndexOf($"[<{hyphen:X4}>] TJ", StringComparison.Ordinal), atB = content.IndexOf($"[<{b:X4}>] TJ", StringComparison.Ordinal);
        Assert.True(atO > atA && atI > atO && atHyphen > atI && atB > atHyphen, content);
        Assert.Equal("1 0 0", E8ColourBefore(content, atO));
        Assert.Equal("0 0 1", E8ColourBefore(content, atI));
        Assert.Equal("0 1 0", E8ColourBefore(content, atHyphen));
        Assert.Equal("0 0 1", E8ColourBefore(content, atB));
        var span = content.IndexOf("/Span <</ActualText <FEFF0041>>> BDC", StringComparison.Ordinal);
        Assert.True(span >= 0 && span < atA && content.IndexOf("EMC", atHyphen, StringComparison.Ordinal) < atB, content);
        // the alpha of entry 1 as constant alpha for the green layer only
        var states = appearance.Form.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("ExtGState"))!;
        var alphas = states.Keys.ToDictionary(k => k.Value, k => states.GetDictionary(k)!.GetNumber(PdfName.Get("ca"))!.Value);
        Assert.Contains(alphas, s => Math.Abs(s.Value - 128 / 255.0) < 0.002 && content.LastIndexOf($"/{s.Key} gs", atHyphen, StringComparison.Ordinal) > atI);
        Assert.Contains(alphas, s => s.Value == 1 && content.IndexOf($"/{s.Key} gs", atHyphen, StringComparison.Ordinal) is > 0 and var reset && reset < atB);
        var glyphs = appearance.FaceUses.Single().Glyphs;
        Assert.True(glyphs.ContainsKey(o) && glyphs.ContainsKey(i) && glyphs.ContainsKey(hyphen) && glyphs.ContainsKey(b) && glyphs[a] == "A" && glyphs[o].Length == 0);

        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        using var output = new MemoryStream();
        PdfWriter.Write(original, [PageItem.From(original)[0].WithElements([element])], output);
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.EndsWith("\nAB", PageText.Create(PageInterpreter.BuildText(saved, saved.Pages[0])).Text.Trim());
        using var bitmap = ContentImageTests.Render(saved, saved.Pages[0]);
        int red = 0, blue = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                if (ContentImageTests.Red(p)) red++;
                if (p.Blue > 180 && p.Red < 80 && p.Green < 80) blue++;
            }
        Assert.True(red > 50 && blue > 50, $"rot {red}, blau {blue}");
    }

    /// <summary>
    /// OT 10.1 sentences 24–26 (own text; question 3): faked bold and italic are not applied to colour glyphs — in a bold italic style faked on the
    /// colour font the layers of „A“ are filled (0 Tr) without slant, while „B“ is stroked (2 Tr) and slanted.
    /// </summary>
    [Fact]
    public void E8_ColrV0OwnText_NoFakeBoldOrItalicOnColourGlyphs()
    {
        E8UseFamily("E8 Farbprobe fett", OpenTypeFont.Parse(E8ColrFont()));
        var content = Encoding.ASCII.GetString(ElementAppearance.For(E8Text("E8 Farbprobe fett", "AB", boldItalic: true)).Form.RawData!);
        var runs = content.Split(" Tf ")[1..];
        var o = Ot14Gid('O');
        var layer = Assert.Single(runs, r => r.Contains($"[<{o:X4}>] TJ", StringComparison.Ordinal));
        Assert.Contains(" 0 Tr ", layer);
        Assert.Contains("\n1 0 0 1 ", layer);
        var plain = Assert.Single(runs, r => r.Contains($"[<{Ot14Gid('B'):X4}>] TJ", StringComparison.Ordinal));
        Assert.Contains(" 2 Tr ", plain);
        Assert.DoesNotContain("\n1 0 0 1 ", plain);
    }

    /// <summary>
    /// OT 10.1 sentence 23; 10.3.2 sentences 1–9; 10.3.3 sentences 10–15, 21–24; 11 sentences 14–19; 11.1 (limits, own text and signatures;
    /// question 3): a colour glyph is used only where the tables allow — next to the valid „A“, „B“ names a layer glyph beyond the glyph count,
    /// „C“ a palette entry beyond the palette, „D“ 513 layers (more than the limit of 512), „E“ no layer, „F“ a slice beyond the layer records: they
    /// keep their outline (no image, drawn as themselves). Counts that claim more records than the table holds are cut to those it holds; without
    /// CPAL, with CPAL whose palette runs past its colour records, and with COLR version 2 the font has no colour glyphs at all.
    /// </summary>
    [Fact]
    public void E8_ColrV0_InvalidRecordsAndLimits_GlyphKeepsItsOutline()
    {
        int a = Ot14Gid('A'), o = Ot14Gid('O');
        var layers = new List<(int Glyph, int Palette)> { (o, 0), (60000, 0), (o, 5) };
        layers.AddRange(Enumerable.Repeat((o, 0), 513));
        (int, int, int)[] bases =
        [
            (a, 0, 1), (Ot14Gid('B'), 1, 1), (Ot14Gid('C'), 2, 1), (Ot14Gid('D'), 3, 513), (Ot14Gid('E'), 0, 0), (Ot14Gid('F'), 600, 2),
        ];
        var colr = Ot14Colr(version: 0, baseGlyphs: bases.OrderBy(r => r.Item1), layerRecords: layers);
        var font = OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot14SansFile.Value, new Dictionary<string, byte[]?> { ["COLR"] = colr, ["CPAL"] = Ot14Palette() }));
        var colour = ColorGlyphs.For(font)!;
        Assert.NotNull(colour.Glyph(a));
        foreach (var c in "BCDEF") Assert.Null(colour.Glyph(Ot14Gid(c)));
        E8UseFamily("E8 Grenzen", font);
        var content = Encoding.ASCII.GetString(ElementAppearance.For(E8Text("E8 Grenzen", "ABCDEF")).Form.RawData!);
        Assert.Contains($"[<{o:X4}>] TJ", content);
        Assert.Contains("3 Tr ", content);
        foreach (var c in "BCDEF") Assert.Contains($"{Ot14Gid(c):X4}", content);

        // a count of 1 000 base glyph records and of 60 000 layer records in a table that holds one of each: cut to the one
        var claiming = Ot14Colr(version: 0, baseGlyphs: [(a, 0, 1)], layerRecords: [(o, 0)]);
        claiming[2] = 0x03;
        claiming[3] = 0xE8;
        claiming[12] = 0xEA;
        claiming[13] = 0x60;
        Assert.NotNull(ColorGlyphs.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot14SansFile.Value,
            new Dictionary<string, byte[]?> { ["COLR"] = claiming, ["CPAL"] = Ot14Palette() })))!.Glyph(a));
        var valid = Ot14Colr(version: 0, baseGlyphs: [(a, 0, 1)], layerRecords: [(o, 0)]);
        var version2 = (byte[])valid.Clone();
        version2[1] = 2;
        foreach (var (c, p) in new (byte[], byte[]?)[] { (valid, null), (valid, Ot14Cpal(4, [1], [Ot14RedColour, Ot14GreenColour, Ot14BlueColour, Ot14GreyColour])), (version2, Ot14Palette()) })
            Assert.Null(ColorGlyphs.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot14SansFile.Value, new Dictionary<string, byte[]?> { ["COLR"] = c, ["CPAL"] = p }))));
    }

    // ------------------------------------------------------------ question 3: COLR version 0 for signatures

    /// <summary>
    /// OT 10.2.5 sentences 1–8; 10.3.3 sentences 13–14, 25 (signatures; question 3): a signature in a colour font without images but with COLR
    /// version 0 takes the image path of colour fonts — the layers of „A“ filled into one image of 160 pixels per em, bottom first, „O“ red, „I“
    /// in black (the text colour the ink image starts from), „-“ green half transparent; a glyph without layers is drawn with its outline when a
    /// name is composed. A font that also has images keeps them (the true look of a font made from ink): with sbix the strike is used.
    /// </summary>
    [Fact]
    public void E8_ColrV0Signature_LayersFilledIntoTheImage()
    {
        var font = OpenTypeFont.Parse(E8ColrFont());
        var colour = ColorGlyphs.For(font)!;
        Assert.Equal(160, colour.PixelsPerEm);
        var (_, _, image) = colour.Glyph(Ot14Gid('A'))!.Value;
        int red = 0, black = 0, green = 0;
        for (var p = 0; p < image.Pixels.Length; p += 4)
        {
            var (r, g, bl, alpha) = (image.Pixels[p], image.Pixels[p + 1], image.Pixels[p + 2], image.Pixels[p + 3]);
            if (alpha > 250 && r > 200 && g < 60 && bl < 60) red++;
            if (alpha > 250 && r < 40 && g < 40 && bl < 40) black++;
            if (alpha is > 100 and < 160 && g > 200 && r < 60) green++;
        }
        Assert.True(red > 100 && black > 100 && green > 20, $"rot {red}, schwarz {black}, grün {green}");
        Assert.Null(colour.Glyph(Ot14Gid('B')));
        Assert.NotNull(colour.Compose(Core.Fonts.Layout.TextShaper.For(font).Shape("AB"), font.UnitsPerEm));

        var both = ColorFontBuilder.WithTables(E8ColrFont(), new Dictionary<string, byte[]?>
        {
            ["sbix"] = Ot33Sbix(1, new Ot33Strike(72, 72, Ot33Png((Ot14Gid('A'), 40)))),
        });
        Assert.Equal(72, ColorGlyphs.For(OpenTypeFont.Parse(both))!.PixelsPerEm);
    }

    // ------------------------------------------------------------ question 3, B82: compressing leaves the colour tables out

    /// <summary>
    /// OT 10.3.3 sentences 21–22; 10.3.6.1; 34 (sbix); 35 (SVG); 6–7 (CBDT/CBLC) (writer, compressing; question 3, finding B82): a compressed font
    /// (subset keeping all tables) has neither COLR, CPAL, sbix, CBDT, CBLC nor SVG — their layer glyphs and images would tell of glyphs emptied,
    /// and a PDF reader draws the outlines; the other tables (cmap, name, post, GSUB, GPOS) stay.
    /// </summary>
    [Fact]
    public void E8_CompressedSubset_LeavesTheColourTablesOut()
    {
        var a = Ot14Gid('A');
        var tables = new Dictionary<string, byte[]?>
        {
            ["COLR"] = Ot14Colr(version: 0, baseGlyphs: [(a, 0, 1)], layerRecords: [(Ot14Gid('O'), 0)]),
            ["CPAL"] = Ot14Palette(),
            ["sbix"] = Ot33Sbix(1, new Ot33Strike(72, 72, Ot33Png((a, 20)))),
            ["SVG "] = Ot33Svg((a, a, Ot33Doc(Ot33Image(16)))),
            ["CBDT"] = [0, 3, 0, 0],
            ["CBLC"] = [0, 3, 0, 0, 0, 0, 0, 0],
        };
        var file = ColorFontBuilder.WithTables(Ot14SansFile.Value, tables);
        var compressed = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(file, [a], keepAllTables: true));
        foreach (var tag in tables.Keys) Assert.False(compressed.HasTable(tag), tag);
        foreach (var tag in (string[])["cmap", "name", "post", "GSUB", "GPOS"])
            Assert.Equal(Ot14Sans.Value.HasTable(tag), compressed.HasTable(tag));
        Assert.False(compressed.Outline(a).IsEmpty);
    }

    // ------------------------------------------------------------ question 29: CFF outlines embedded as OpenType

    /// <summary>
    /// OT 31.2.5 sentence 24; ISO 32000-2, 9.9.1 (Table 124, FontFile3 /OpenType) (writer; question 29): typed text in an installed font with CFF
    /// outlines and fsType 0x0004 is saved as an OpenType file — FontFile3 /OpenType under a CIDFontType0, the file starting with „OTTO“, its OS/2
    /// with fsType 0x0004 unchanged, a cmap besides the CID-keyed 'CFF ' table (9.9.1: required), the glyphs of the text found by their CID through
    /// the charset; saved in a 1.4 file the version is raised to 1.6, and the page reads and draws the text. Saved as PDF/A-1 (which knows no
    /// OpenType font programs) the font stays the bare CFF (/CIDFontType0C).
    /// </summary>
    [Fact]
    public void Ot30_CffFontSaved_AsOpenTypeWithFsType()
    {
        var tables = new Dictionary<string, byte[]?> { ["OS/2"] = Ot30WithFsTypeTable(Ot05NotoExcerpt.Value, 0x0004) };
        var font = OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot05NotoExcerpt.Value, tables));
        var face = ElementFace.For($"probe-m7-ot30-cff-{Guid.NewGuid():N}", () => font, false, false, false);
        var (glyphA, glyphKan) = (face.Glyph('A'), face.Glyph('漢'));
        var descriptor = Iso04Descriptor(face.SavedFont(new Dictionary<int, string> { [glyphA] = "A", [glyphKan] = "漢" }));
        var program = descriptor.GetStream(PdfName.Get("FontFile3"))!;
        Assert.Equal("OpenType", program.Dictionary.GetName(PdfName.Subtype)?.Value);
        var data = program.RawData!;
        Assert.Equal("OTTO"u8.ToArray(), data[..4]);
        var embedded = OpenTypeFont.Parse(data);
        Assert.Equal(0x0004, Ot30U16(embedded.TableData("OS/2").ToArray(), 8));
        Assert.True(embedded.HasTable("cmap") && embedded.HasTable("head") && embedded.HasTable("hhea") && embedded.HasTable("hmtx") &&
                    embedded.HasTable("maxp") && embedded.HasTable("name") && embedded.HasTable("post"));
        var cff = embedded.Cff!;
        Assert.True(cff.IsCidKeyed);
        Assert.Equal(3, embedded.GlyphCount);
        Assert.True(Iso03SameOutline(font.Outline(glyphKan), cff.Outline(cff.GlyphByCid(glyphKan))));
        Assert.Equal(cff.GlyphByCid(glyphKan), embedded.GlyphForUnicode('漢'));
        Assert.Equal(font.AdvanceWidth(glyphA), embedded.AdvanceWidth(cff.GlyphByCid(glyphA)));

        E8UseFamily("E8 CFF-Probe", font);
        var element = E8Text("E8 CFF-Probe", "A漢");
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        using var output = new MemoryStream();
        var result = PdfWriter.Write(original, [PageItem.From(original)[0].WithElements([element])], output, new PdfSaveOptions { Version = "1.4" });
        Assert.Equal("1.6", result.Version);
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.EndsWith("\nA漢", PageText.Create(PageInterpreter.BuildText(saved, saved.Pages[0])).Text.Trim());
        var size = (int)(saved.Trailer.GetInteger(PdfName.Size) ?? 0);
        Assert.Contains(Enumerable.Range(1, size - 1).Select(saved.GetObject).OfType<PdfStream>(), s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "OpenType");
        using var bitmap = ContentImageTests.Render(saved, saved.Pages[0]);
        var ink = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Blue > 180 && bitmap.GetPixel(x, y).Red < 80) ink++;
        Assert.True(ink > 100, $"gezeichnet: {ink} Pixel");

        var pdfa = PdfATests.Write(TestPdf.Simple(1).Build(), PdfAConformance.A1b);
        using var archive = PdfDocument.Open(pdfa.Bytes);
        using var archived = new MemoryStream();
        PdfWriter.Write(archive, [PageItem.From(archive)[0].WithElements([element])], archived);
        using var reopened = PdfDocument.Open(archived.ToArray());
        var streams = Enumerable.Range(1, (int)(reopened.Trailer.GetInteger(PdfName.Size) ?? 0) - 1).Select(reopened.GetObject).OfType<PdfStream>().ToList();
        Assert.Contains(streams, s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "CIDFontType0C");
        Assert.DoesNotContain(streams, s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "OpenType");
    }
}
