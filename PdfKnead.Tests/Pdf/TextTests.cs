using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Text: fonts, positions, text rendering modes, ToUnicode, Type 3, embedded fonts.</summary>
public sealed class TextTests
{
    private static PdfDictionary Helvetica() =>
        TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")),
            ("Encoding", TestPdf.N("WinAnsiEncoding")));

    private static Func<TestPdf, PdfDictionary> Fonts(params (string Name, Func<TestPdf, PdfObject> Font)[] fonts) => pdf =>
        TestPdf.Dict(("Font", TestPdf.Dict(fonts.Select(f => (f.Name, f.Font(pdf))).ToArray())));

    private static string TextOf(DisplayList list) => string.Concat(list.Text.Select(t => t.Text));

    [Fact]
    public void StandardFont_IsSubstituted_WithHelveticaWidths()
    {
        var (doc, list) = InterpreterTests.Page("BT /F1 20 Tf 10 100 Td (Hallo) Tj ET", Fonts(("F1", _ => Helvetica())));
        using (doc)
        {
            Assert.Equal("Hallo", TextOf(list));
            Assert.Equal(5, list.Count);                                   // five filled glyphs
            Assert.Equal(10, list.Text[0].X0, 2);
            Assert.Equal(10 + 0.722 * 20, list.Text[1].X0, 1);            // H is 722/1000 wide
            Assert.True(list.Text[0].Y3 > 100 && list.Text[0].Y0 < 100);  // quadrilateral around the baseline
        }
    }

    [Fact]
    public void FontInExtGState_ActsLikeTf()
    {
        // Test file NegativeFontSize: /Font [font size] in the ExtGState sets font and size like Tf;
        // otherwise the fallback font without a size would remain
        var (doc, list) = InterpreterTests.Page("BT /GS1 gs 10 100 Td (Hallo) Tj ET", pdf =>
        {
            var helvetica = TestPdf.Ref(pdf.Add(Helvetica()));
            return TestPdf.Dict(("Font", TestPdf.Dict(("F1", helvetica))),
                ("ExtGState", TestPdf.Dict(("GS1", TestPdf.Dict(("Font", new PdfArray([helvetica, PdfInteger.Of(20)])))))));
        });
        using (doc)
        {
            Assert.Equal("Hallo", TextOf(list));
            Assert.Equal(20, list.Text[0].Size, 2);
            Assert.Equal(10 + 0.722 * 20, list.Text[1].X0, 1);            // Helvetica widths
        }
    }

    [Fact]
    public void Positioning_TjAdjustment_CharAndWordSpacing()
    {
        // A, then 1000/1000 em to the right, B; character spacing 2, word spacing 5 at the space
        var (doc, list) = InterpreterTests.Page("BT /F1 10 Tf 2 Tc 5 Tw 0 0 Td [(A) -1000 (B)] TJ ( C) Tj ET", Fonts(("F1", _ => Helvetica())));
        using (doc)
        {
            Assert.Equal("AB C", TextOf(list));
            var a = 0.667 * 10 + 2;                                        // A + Tc
            Assert.Equal(a + 10, list.Text[1].X0, 1);                      // −1000 → +10 points
            var b = a + 10 + 0.667 * 10 + 2;
            Assert.Equal(b + 0.278 * 10 + 2 + 5, list.Text[3].X0, 1);      // space with Tc and Tw
        }
    }

    [Fact]
    public void InvisibleText_IsExtractable_ButNotDrawn()
    {
        var (doc, list) = InterpreterTests.Page("BT 3 Tr /F1 12 Tf 10 10 Td (OCR) Tj ET", Fonts(("F1", _ => Helvetica())));
        using (doc)
        {
            Assert.Equal(0, list.Count);
            Assert.Equal("OCR", TextOf(list));
            Assert.All(list.Text, t => Assert.False(t.Visible));
        }
    }

    [Fact]
    public void ClipText_RestrictsLaterPainting()
    {
        // Large "I" as a clip (mode 7), then fill everything red: red only in the stem of the I
        var (doc, list) = InterpreterTests.Page("BT 7 Tr /F1 150 Tf 80 20 Td (I) Tj ET 1 0 0 rg 0 0 200 200 re f", Fonts(("F1", _ => Helvetica())));
        using (doc)
        {
            using var bitmap = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
            Color(bitmap, 80 + 0.278 * 150 / 2, 80, SKColors.Red);    // middle of the stem
            Color(bitmap, 20, 80, SKColors.White);                     // beside it
        }
    }

    /// <summary>
    /// Text in clip mode on a hidden layer still clips: a clip acts on hidden content as everywhere (ISO 32000-2, 8.11.3.1) — the red fill
    /// after it shows only in the stem of the I. The display left the outlines of hidden characters out of the clip, and the red covered the
    /// page (docs/FEHLERGEDAECHTNIS.md, No. 441).
    /// </summary>
    [Fact]
    public void ClipTextOnAHiddenLayer_StillClips()
    {
        var ocg = 0;
        var (doc, list) = InterpreterTests.Page("/OC /Aus BDC BT 7 Tr /F1 150 Tf 80 20 Td (I) Tj ET EMC 1 0 0 rg 0 0 200 200 re f",
            pdf =>
            {
                var resources = Fonts(("F1", _ => Helvetica()))(pdf);
                ocg = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Ebene"u8.ToArray()))));
                resources.Set(PdfName.Get("Properties"), TestPdf.Dict(("Aus", TestPdf.Ref(ocg))));
                return resources;
            },
            (pdf, catalog) => catalog.Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(ocg)])),
                ("D", TestPdf.Dict(("OFF", new PdfArray([TestPdf.Ref(ocg)])))))));
        using (doc)
        {
            using var bitmap = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
            Color(bitmap, 80 + 0.278 * 150 / 2, 80, SKColors.Red);    // middle of the stem
            Color(bitmap, 20, 80, SKColors.White);                     // beside it
        }
    }

    /// <summary>
    /// A text object in clip mode that leaves no outline (only a space, or no glyph at all): the clip stays as it is and the red fill after
    /// it covers the page (ISO 32000-2, 9.3.6). An empty clip hid everything that followed (docs/FEHLERGEDAECHTNIS.md, No. 433).
    /// </summary>
    [Theory]
    [InlineData("BT 7 Tr /F1 150 Tf 80 20 Td ( ) Tj ET 1 0 0 rg 0 0 200 200 re f")]
    [InlineData("BT 7 Tr /F1 150 Tf 80 20 Td ET 1 0 0 rg 0 0 200 200 re f")]
    public void ClipTextWithoutOutlines_ClipsNothing(string content)
    {
        var (doc, list) = InterpreterTests.Page(content, Fonts(("F1", _ => Helvetica())));
        using (doc)
        {
            using var bitmap = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
            Color(bitmap, 20, 80, SKColors.Red);
            Color(bitmap, 150, 150, SKColors.Red);
        }
    }

    [Fact]
    public void ToUnicodeCMap_SinglesRangesAndArrays()
    {
        var cmap = CMap.Parse(Encoding.ASCII.GetBytes("""
            /CIDInit /ProcSet findresource begin 12 dict begin begincmap
            1 begincodespacerange <0000> <FFFF> endcodespacerange
            2 beginbfchar <0001> <0048> <0002> <00660069> endbfchar
            2 beginbfrange <0010> <0012> <0061> <0020> <0021> [<00E4> <D83DDE00>] endbfrange
            endcmap CMapName currentdict /CMap defineresource pop end end
            """));
        Assert.Equal("H", cmap.ToUnicode(1, 2));
        Assert.Equal("fi", cmap.ToUnicode(2, 2));
        Assert.Equal("c", cmap.ToUnicode(0x12, 2));
        Assert.Equal("ä", cmap.ToUnicode(0x20, 2));
        Assert.Equal("😀", cmap.ToUnicode(0x21, 2));
        Assert.Equal(2, cmap.ReadCode([0x00, 0x12], 0, out var code));
        Assert.Equal(0x12u, code);
    }

    /// <summary>
    /// An unterminated hex string as target (fuzzing, S7): the string ran over the following 190 KB, every character became 16,000
    /// letters, and editing one text block took 3 GB. Targets over 512 bytes are dropped; the code keeps no mapping.
    /// </summary>
    [Fact]
    public void ToUnicodeCMap_OverlongTargetsDropped()
    {
        var huge = string.Concat(Enumerable.Repeat("0041", 300));   // 600 bytes
        var fine = string.Concat(Enumerable.Repeat("0042", 256));   // 512 bytes
        var cmap = CMap.Parse(Encoding.ASCII.GetBytes($"""
            begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange
            2 beginbfchar <0001> <{huge}> <0002> <{fine}> endbfchar
            2 beginbfrange <0010> <0012> <{huge}> <0020> <0021> [<{huge}> <0043>] endbfrange
            endcmap
            """));
        Assert.Null(cmap.ToUnicode(1, 2));
        Assert.Equal(new string('B', 256), cmap.ToUnicode(2, 2));
        Assert.Null(cmap.ToUnicode(0x11, 2));
        Assert.Equal("", cmap.ToUnicode(0x20, 2));
        Assert.Equal("C", cmap.ToUnicode(0x21, 2));
    }

    [Fact]
    public void Type3Font_DrawsGlyphProcedure_InFillColor()
    {
        var (doc, list) = InterpreterTests.Page("0 0 1 rg BT /T3 100 Tf 50 50 Td (a) Tj ET", Fonts(("T3", pdf =>
        {
            // Glyph: square 0..1000 in glyph space (d1: without its own colour)
            var proc = pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes("1000 0 0 0 1000 1000 d1 1 0 0 rg 0 0 1000 1000 re f")));
            return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")),
                ("FontMatrix", TestPdf.Box(0.001, 0, 0, 0.001, 0, 0)), ("FontBBox", TestPdf.Box(0, 0, 1000, 1000)),
                ("CharProcs", TestPdf.Dict(("square", TestPdf.Ref(proc)))),
                ("Encoding", TestPdf.Dict(("Differences", new PdfArray([PdfInteger.Of(97), TestPdf.N("square")])))),
                ("FirstChar", PdfInteger.Of(97)), ("LastChar", PdfInteger.Of(97)), ("Widths", new PdfArray([PdfInteger.Of(1000)])));
        })));
        using (doc)
        {
            using var bitmap = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
            Color(bitmap, 100, 100, SKColors.Blue);                    // d1: the colour of the page, not the red of the procedure
            Color(bitmap, 160, 100, SKColors.White);
            Assert.Equal("a", TextOf(list));
        }
    }

    [Fact]
    public void EmbeddedTrueType_AndType0IdentityH_DrawTheSameGlyph()
    {
        var ttf = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var font = OpenTypeFont.Parse(ttf);
        var gid = font.GlyphForUnicode('I');
        var content = $"BT /TT 150 Tf 30 20 Td (I) Tj /CID 150 Tf 110 20 Td <{gid:X4}> Tj ET";
        var (doc, list) = InterpreterTests.Page(content, Fonts(
            ("TT", pdf => TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("ABCDEF+Test")),
                ("Encoding", TestPdf.N("WinAnsiEncoding")), ("FontDescriptor", Descriptor(pdf, ttf)))),
            ("CID", pdf => TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("ABCDEF+Test")),
                ("Encoding", TestPdf.N("Identity-H")),
                ("DescendantFonts", new PdfArray([TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")),
                    ("BaseFont", TestPdf.N("ABCDEF+Test")), ("CIDToGIDMap", TestPdf.N("Identity")), ("DW", PdfInteger.Of(278)),
                    ("FontDescriptor", Descriptor(pdf, ttf)))]))))));
        using (doc)
        {
            Assert.Equal("II", TextOf(list));                          // Type0 without ToUnicode: via the cmap of the font
            using var bitmap = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
            var stem = 0.278 * 150 / 2;
            Color(bitmap, 30 + stem, 80, SKColors.Black);
            Color(bitmap, 30 + 110 + stem, 80, SKColors.Black);
            Color(bitmap, 30 + 110 / 2, 80, SKColors.White);
        }
    }

    private static PdfObject Descriptor(TestPdf pdf, byte[] ttf)
    {
        var file = pdf.Add(TestPdf.FlateStream(ttf));
        return TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Test")), ("Flags", PdfInteger.Of(32)),
            ("FontFile2", TestPdf.Ref(file)));
    }

    /// <summary>Checks a pixel in PDF coordinates (y upwards).</summary>
    private static void Color(SKBitmap b, double x, double pdfY, SKColor expected)
    {
        var actual = b.GetPixel((int)x, b.Height - 1 - (int)pdfY);
        Assert.True(Math.Abs(actual.Red - expected.Red) < 40 && Math.Abs(actual.Green - expected.Green) < 40 && Math.Abs(actual.Blue - expected.Blue) < 40,
            $"bei ({x:0}, {pdfY:0}): erwartet {expected}, gefunden {actual}");
    }

    // ---- 7.4 E-23: Symbolic and Nonsymbolic both set — the Symbolic bit decides (ISO 32000-2, 9.8.2; docs/FEHLERGEDAECHTNIS.md, No. 571)

    /// <summary>A cmap format 4 subtable mapping each code to its glyph (one segment per code).</summary>
    private static byte[] Format4(params (int Code, int Glyph)[] map)
    {
        var codes = map.OrderBy(m => m.Code).Append((Code: 0xFFFF, Glyph: 0)).ToArray();
        var segments = codes.Length;
        var data = new byte[16 + 8 * segments];
        void U16(int at, int v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), (ushort)v);
        var power = 1;
        var log = 0;
        while (power * 2 <= segments) { power *= 2; log++; }
        U16(0, 4); U16(2, data.Length); U16(6, 2 * segments); U16(8, 2 * power); U16(10, log); U16(12, 2 * segments - 2 * power);
        for (var i = 0; i < segments; i++)
        {
            var (code, glyph) = codes[i];
            U16(14 + 2 * i, code);                                       // endCode
            U16(16 + 2 * segments + 2 * i, code);                        // startCode
            U16(16 + 4 * segments + 2 * i, code == 0xFFFF ? 1 : glyph - code);   // idDelta
        }
        return data;
    }

    /// <summary>
    /// Liberation Sans with glyphs „A“ and „B“ and two cmaps: (3,1) Unicode „A“ → A, „B“ → B, and (3,0) symbol 0xF041 → B, 0xF042 → A —
    /// so the two ways to find a glyph give different glyphs. Returns the font file and the glyph numbers of A and B.
    /// </summary>
    private static (byte[] Font, int A, int B) TwoCmapFont()
    {
        var original = OpenTypeFont.Parse(EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"));
        int a = original.GlyphForUnicode('A'), b = original.GlyphForUnicode('B');
        return (TwoCmaps([(0xF041, b), (0xF042, a)], [('A', a), ('B', b)]), a, b);
    }

    /// <summary>Liberation Sans with only the glyphs named in the two cmaps, a (3,0) symbol cmap and a (3,1) Unicode cmap.</summary>
    internal static byte[] TwoCmaps((int Code, int Glyph)[] symbolMap, (int Code, int Glyph)[] unicodeMap)
    {
        var source = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var advances = symbolMap.Concat(unicodeMap).Select(m => m.Glyph).Distinct().ToDictionary(g => g, _ => 1366);
        var rebuilt = TrueTypeSubsetter.Rebuild(source, advances, [], symbolMap);
        var tables = new Dictionary<string, byte[]>();
        int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(rebuilt.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            var record = rebuilt.AsSpan(12 + 16 * i);
            var tag = Encoding.ASCII.GetString(record[..4]);
            var offset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            tables[tag] = rebuilt.AsSpan(offset, length).ToArray();
        }
        var symbol = Format4(symbolMap);
        var unicode = Format4(unicodeMap);
        var cmap = new byte[4 + 16 + symbol.Length + unicode.Length];
        void U16(int at, int v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(at), (ushort)v);
        void U32(int at, int v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(at), (uint)v);
        U16(2, 2);
        U16(4, 3); U16(6, 0); U32(8, 20);
        U16(12, 3); U16(14, 1); U32(16, 20 + symbol.Length);
        symbol.CopyTo(cmap, 20);
        unicode.CopyTo(cmap, 20 + symbol.Length);
        tables["cmap"] = cmap;
        return TrueTypeSubsetter.Assemble(tables);
    }

    /// <summary>
    /// Embedded TrueType without /Encoding, the code „A“: with the Symbolic bit (4, and 36 = Symbolic and Nonsymbolic) the (3,0) cmap
    /// decides — glyph B; with only Nonsymbolic (32) the name „A“ through the (3,1) cmap — glyph A. Before: 36 counted as non-symbolic (A).
    /// Both set and an /Encoding given: the names, as before (a symbolic TrueType font has no /Encoding; test file issue20232).
    /// </summary>
    [Theory]
    [InlineData(36, false, 'B')]
    [InlineData(4, false, 'B')]
    [InlineData(32, false, 'A')]
    [InlineData(36, true, 'A')]
    public void SymbolicAndNonsymbolicBoth_SymbolicDecides(int flags, bool withEncoding, char expected)
    {
        var (font, a, b) = TwoCmapFont();
        var (doc, _) = InterpreterTests.Page("BT /F1 12 Tf 10 10 Td (A) Tj ET", pdf =>
        {
            var file = pdf.Add(new PdfStream(TestPdf.Dict(("Length1", PdfInteger.Of(font.Length))), font));
            var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Musterschrift")), ("Flags", PdfInteger.Of(flags)),
                ("FontBBox", TestPdf.Box(0, -200, 1000, 900)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)),
                ("Descent", PdfInteger.Of(-200)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file)));
            var dict = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("Musterschrift")),
                ("FirstChar", PdfInteger.Of(65)), ("LastChar", PdfInteger.Of(66)), ("Widths", TestPdf.Box(667, 667)), ("FontDescriptor", descriptor));
            if (withEncoding)
                dict.Set(TestPdf.N("Encoding"), TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", new PdfArray([PdfInteger.Of(65), TestPdf.N("A")]))));
            return TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(pdf.Add(dict))))));
        });
        using (doc)
        {
            var dict = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
            var program = OpenTypeFont.Parse(font);
            var glyph = PdfFont.Get(dict, doc).Glyph('A', 1);
            Assert.Equal(program.Outline(expected == 'A' ? a : b).Bounds, glyph.Outline.Bounds);
        }
    }

    /// <summary>
    /// E-11, deliberately kept (docs/FEHLERGEDAECHTNIS.md, No. 571): a non-symbolic TrueType font with an /Encoding dictionary without
    /// /BaseEncoding takes WinAnsi as its base, as widely used readers do — ISO 32000-2, Table 112 names StandardEncoding. Code 0x80 is „€“
    /// (in StandardEncoding it has no character). Font cleaning when redacting uses the same base; a change must meet both.
    /// </summary>
    [Fact]
    public void NonSymbolicTrueTypeWithoutBaseEncoding_UsesWinAnsi()
    {
        var font = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var (doc, list) = InterpreterTests.Page("BT /F1 12 Tf 10 10 Td <80> Tj ET", pdf =>
        {
            var file = pdf.Add(new PdfStream(TestPdf.Dict(("Length1", PdfInteger.Of(font.Length))), font));
            var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Musterschrift")), ("Flags", PdfInteger.Of(32)),
                ("FontBBox", TestPdf.Box(0, -200, 1000, 900)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)),
                ("Descent", PdfInteger.Of(-200)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file)));
            var dict = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("Musterschrift")),
                ("FirstChar", PdfInteger.Of(128)), ("LastChar", PdfInteger.Of(128)), ("Widths", TestPdf.Box(556)), ("FontDescriptor", descriptor),
                ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", new PdfArray()))));
            return TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(pdf.Add(dict))))));
        });
        using (doc) Assert.Equal("€", TextOf(list));
    }
}
