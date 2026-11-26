using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Konformitätsabgleich M7, package iso-02 (ISO 32000-2, 9.5, 9.6, 9.7.1, 9.7.2: font dictionaries, simple fonts, Type 3, encodings): the
/// tests the sentence-by-sentence drafts named as missing.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package iso-02)

    private static readonly byte[] LiberationIso02 = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");

    /// <summary>A page with one font /F (made by <paramref name="font"/>); returns the document, its display list and the font as read.</summary>
    private static (PdfDocument Doc, DisplayList List, PdfFont Font) PageIso02(string content, Func<TestPdf, PdfObject> font)
    {
        var (doc, list) = InterpreterTests.Page(content, pdf => TestPdf.Dict(("Font", TestPdf.Dict(("F", font(pdf))))));
        var dict = (PdfDictionary)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F"))!;
        return (doc, list, PdfFont.Get(dict, doc));
    }

    private static PdfDictionary DescriptorIso02(TestPdf pdf, string key, byte[] program, int flags = 32, params (string Key, PdfObject Value)[] extra)
    {
        var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Test")), ("Flags", PdfInteger.Of(flags)),
            ("FontBBox", TestPdf.Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)),
            ("Descent", PdfInteger.Of(-200)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)),
            (key, TestPdf.Ref(pdf.Add(TestPdf.FlateStream(program)))));
        foreach (var (k, v) in extra) descriptor.Set(PdfName.Get(k), v);
        return descriptor;
    }

    private static PdfArray DifferencesIso02(params (int Code, string Name)[] names)
    {
        var differences = new PdfArray();
        foreach (var (code, name) in names)
        {
            differences.Add(PdfInteger.Of(code));
            differences.Add(TestPdf.N(name));
        }
        return differences;
    }

    private static PdfDictionary EncodingIso02(params (int Code, string Name)[] names) =>
        TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", DifferencesIso02(names)));

    /// <summary>A Type 3 font (glyph space 1000 units by default) with its widths, names and procedures.</summary>
    private static PdfDictionary Type3Iso02(TestPdf pdf, int first, double[] widths, (int Code, string Name)[] names, (string Name, string Proc)[] procs,
        double[]? matrix = null, double[]? box = null)
    {
        var charProcs = new PdfDictionary();
        foreach (var (name, proc) in procs) charProcs.Set(PdfName.Get(name), TestPdf.Ref(pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes(proc)))));
        return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("FontMatrix", TestPdf.Box(matrix ?? [0.001, 0, 0, 0.001, 0, 0])),
            ("FontBBox", TestPdf.Box(box ?? [0, 0, 1000, 1000])), ("CharProcs", charProcs), ("Encoding", EncodingIso02(names)),
            ("FirstChar", PdfInteger.Of(first)), ("LastChar", PdfInteger.Of(first + widths.Length - 1)), ("Widths", TestPdf.Box(widths)));
    }

    private const string SquareD0Iso02 = "1000 0 d0 0 0 1000 1000 re f";

    /// <summary>One table of an sfnt file (null if absent).</summary>
    private static byte[]? TableIso02(byte[] font, string tag)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            var record = font.AsSpan(12 + 16 * i);
            if (Encoding.ASCII.GetString(record[..4]) != tag) continue;
            return font.AsSpan((int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]), (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..])).ToArray();
        }
        return null;
    }

    /// <summary>A cmap table with the given subtables (version 0).</summary>
    private static byte[] CmapIso02(params (int Platform, int Encoding, byte[] Subtable)[] subtables)
    {
        var data = new List<byte>();
        void U16(int v) { data.Add((byte)(v >> 8)); data.Add((byte)v); }
        void U32(int v) { U16(v >> 16); U16(v & 0xFFFF); }
        U16(0);
        U16(subtables.Length);
        var offset = 4 + 8 * subtables.Length;
        foreach (var (platform, encoding, subtable) in subtables)
        {
            U16(platform);
            U16(encoding);
            U32(offset);
            offset += subtable.Length;
        }
        foreach (var (_, _, subtable) in subtables) data.AddRange(subtable);
        return [.. data];
    }

    /// <summary>A cmap subtable of format 6 (trimmed table) from <paramref name="first"/>.</summary>
    private static byte[] Format6Iso02(int first, int[] glyphs)
    {
        var data = new byte[10 + 2 * glyphs.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data, 6);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6), (ushort)first);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), (ushort)glyphs.Length);
        for (var i = 0; i < glyphs.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10 + 2 * i), (ushort)glyphs[i]);
        return data;
    }

    /// <summary>A cmap subtable of format 4 mapping each code to its glyph (one segment per code, then the closing segment).</summary>
    private static byte[] Format4Iso02(params (int Code, int Glyph)[] map)
    {
        var codes = map.OrderBy(m => m.Code).Append((Code: 0xFFFF, Glyph: 0)).ToArray();
        var segments = codes.Length;
        var data = new byte[16 + 8 * segments];
        BinaryPrimitives.WriteUInt16BigEndian(data, 4);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6), (ushort)(2 * segments));
        for (var i = 0; i < segments; i++)
        {
            var (code, glyph) = codes[i];
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(14 + 2 * i), (ushort)code);                       // endCode
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16 + 2 * segments + 2 * i), (ushort)code);        // startCode
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16 + 4 * segments + 2 * i), (ushort)(code == 0xFFFF ? 1 : glyph - code));   // idDelta
        }
        return data;
    }

    private static PdfDictionary TrueTypeIso02(TestPdf pdf, byte[] program, PdfObject encoding, int first, double[] widths,
        params (string Key, PdfObject Value)[] descriptorExtra) =>
        TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("ABCDEF+Test")), ("Encoding", encoding),
            ("FirstChar", PdfInteger.Of(first)), ("LastChar", PdfInteger.Of(first + widths.Length - 1)), ("Widths", TestPdf.Box(widths)),
            ("FontDescriptor", DescriptorIso02(pdf, "FontFile2", program, 32, descriptorExtra)));

    // ------------------------------------------------------------ 9.5

    /// <summary>
    /// 9.5 sentence 8: a CIDFont is never used directly, only as the descendant of a Type 0 font — /Font naming a CIDFontType2 itself: the
    /// PDF/A check reports the subtype, the display draws the page without an exception.
    /// </summary>
    [Fact]
    public void Iso02_CidFontNamedDirectly_CheckReportsItDisplayDraws()
    {
        var (doc, list, _) = PageIso02("BT /F 12 Tf 10 10 Td (A) Tj ET", pdf => TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")),
            ("BaseFont", TestPdf.N("ABCDEF+Test")), ("CIDToGIDMap", TestPdf.N("Identity")),
            ("CIDSystemInfo", TestPdf.Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())),
                ("Supplement", PdfInteger.Of(0)))),
            ("FontDescriptor", DescriptorIso02(pdf, "FontFile2", LiberationIso02))));
        using (doc)
        {
            Assert.NotNull(list);
            var id = PdfARules.FontSubtype.Id(PdfAConformance.A2b);
            Assert.Contains(PdfACheck.Check(doc, PdfAConformance.A2b), v => v.Id == id);
        }
    }

    // ------------------------------------------------------------ 9.6.2

    /// <summary>
    /// 9.6.2.1 sentence 24: a code outside FirstChar to LastChar takes /MissingWidth of the descriptor — embedded TrueType with /FirstChar 65,
    /// /LastChar 65, /Widths [500] and /MissingWidth 250: code 66 is 0.25 wide.
    /// </summary>
    [Fact]
    public void Iso02_CodeOutsideFirstLast_TakesMissingWidth()
    {
        var (doc, _, font) = PageIso02("BT /F 10 Tf 10 10 Td (AB) Tj ET", pdf =>
            TrueTypeIso02(pdf, LiberationIso02, TestPdf.N("WinAnsiEncoding"), 65, [500], ("MissingWidth", PdfInteger.Of(250))));
        using (doc)
        {
            Assert.Equal(0.5, font.Glyph(65, 1).Width, 6);
            Assert.Equal(0.25, font.Glyph(66, 1).Width, 6);
        }
    }

    /// <summary>
    /// 9.6.2.1 sentence 31: for the standard 14 fonts FirstChar, LastChar, Widths and FontDescriptor stand all or none — Helvetica with a
    /// /FontDescriptor but without /Widths, saved as 2.0 and as 1.7: the copy has all four.
    /// </summary>
    [Fact]
    public void Iso02_StandardFontWithDescriptorButNoWidths_SavedWithAllFour()
    {
        var pdf = TestPdf.Simple(1);
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Helvetica")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(-166, -225, 1000, 931)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(718)),
            ("Descent", PdfInteger.Of(-207)), ("CapHeight", PdfInteger.Of(718)), ("StemV", PdfInteger.Of(88))));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")),
            ("Encoding", TestPdf.N("WinAnsiEncoding")), ("FontDescriptor", TestPdf.Ref(descriptor))));
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 24 Tf 72 700 Td (Seite) Tj ET"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font))))));
        var file = pdf.Build();
        foreach (var version in (ReadOnlySpan<string>)["2.0", "1.7"])
        {
            using var saved = DocumentStructureNormTests.Saved(file, new PdfSaveOptions { Version = version });
            var written = (PdfDictionary)saved.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1"))!;
            foreach (var key in (ReadOnlySpan<string>)["FirstChar", "LastChar", "Widths", "FontDescriptor"])
                Assert.True(written.ContainsKey(PdfName.Get(key)), $"{version}: /{key} fehlt");
        }
    }

    // ------------------------------------------------------------ 9.6.4 Type 3

    /// <summary>
    /// 9.6.4 sentences 11–17: /FontBBox with all four values 0 makes no claim about the glyphs' size — a Type 3 font with [0 0 0 0] and no d1
    /// boxes has the usual height (ascent 0.8, descent −0.2), not one taken from the box.
    /// </summary>
    [Fact]
    public void Iso02_Type3ZeroFontBBox_NoClaimAboutGlyphSizes()
    {
        var (doc, _, font) = PageIso02("BT /F 10 Tf 10 10 Td (a) Tj ET", pdf =>
            Type3Iso02(pdf, 97, [1000], [(97, "a")], [("a", SquareD0Iso02)], box: [0, 0, 0, 0]));
        using (doc)
        {
            Assert.Equal(0.8, font.Ascent, 6);
            Assert.Equal(-0.2, font.Descent, 6);
        }
    }

    /// <summary>9.6.4 sentence 34: a code outside FirstChar to LastChar has width 0 — /FirstChar 97 /LastChar 97, code 98 with a glyph: width 0.</summary>
    [Fact]
    public void Iso02_Type3CodeOutsideFirstLast_WidthZero()
    {
        var (doc, _, font) = PageIso02("BT /F 10 Tf 10 10 Td (ab) Tj ET", pdf =>
            Type3Iso02(pdf, 97, [1000], [(97, "a"), (98, "b")], [("a", SquareD0Iso02), ("b", SquareD0Iso02)]));
        using (doc)
        {
            Assert.Equal(1, font.Glyph(97, 1).Width, 6);
            Assert.Equal(0, font.Glyph(98, 1).Width, 6);
        }
    }

    /// <summary>
    /// 9.6.4 sentences 35–37: the widths are in glyph space and go through /FontMatrix; only the horizontal part counts — [0.002 0 0 0.002 0 0]
    /// with /Widths [500]: width 1.0; a matrix turned by 45° with scale 0.001 and /Widths [1000]: width 0.7071, and the second glyph stands at
    /// the same height (no vertical advance).
    /// </summary>
    [Fact]
    public void Iso02_Type3Widths_ThroughTheFontMatrix_HorizontalOnly()
    {
        var (doc, _, font) = PageIso02("BT /F 10 Tf 10 10 Td (a) Tj ET", pdf =>
            Type3Iso02(pdf, 97, [500], [(97, "a")], [("a", "500 0 d0 0 0 500 500 re f")], matrix: [0.002, 0, 0, 0.002, 0, 0]));
        using (doc) Assert.Equal(1.0, font.Glyph(97, 1).Width, 6);

        var r = Math.Sqrt(0.5) * 0.001;
        var (turnedDoc, list, turned) = PageIso02("BT /F 10 Tf 20 100 Td (aa) Tj ET", pdf =>
            Type3Iso02(pdf, 97, [1000], [(97, "a")], [("a", SquareD0Iso02)], matrix: [r, r, -r, r, 0, 0]));
        using (turnedDoc)
        {
            Assert.Equal(0.7071, turned.Glyph(97, 1).Width, 4);
            Assert.Equal(2, list.Text.Length);
            Assert.Equal(list.Text[0].X0 + 7.071, list.Text[1].X0, 2);
            Assert.Equal(list.Text[0].Y0, list.Text[1].Y0, 4);
        }
    }

    /// <summary>9.6.4 sentences 46–48: /ToUnicode of a Type 3 font gives the text — glyph „g1“ under code 61h, /ToUnicode 61 → „Z“: the text is „Z“.</summary>
    [Fact]
    public void Iso02_Type3ToUnicode_GivesTheText()
    {
        var (doc, list, _) = PageIso02("BT /F 10 Tf 10 10 Td (a) Tj ET", pdf =>
        {
            var font = Type3Iso02(pdf, 97, [1000], [(97, "g1")], [("g1", SquareD0Iso02)]);
            const string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CMapName /KneadUni def /CMapType 2 def\n" +
                                "1 begincodespacerange <00> <FF> endcodespacerange\n1 beginbfchar <61> <005A> endbfchar\n" +
                                "endcmap CMapName currentdict /CMap defineresource pop end end\n";
            font.Set(PdfName.Get("ToUnicode"), TestPdf.Ref(pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes(cmap)))));
            return font;
        });
        using (doc) Assert.Equal("Z", string.Concat(list.Text.Select(t => t.Text)));
    }

    /// <summary>
    /// 9.6.4 sentences 49–51: per code the name from /Encoding, the stream from /CharProcs; a name without a stream paints nothing — (ba) with
    /// „b“ missing in /CharProcs: white where the b stands, the a one width (from /Widths) further on.
    /// </summary>
    [Fact]
    public void Iso02_Type3NameWithoutProcedure_PaintsNothingButAdvances()
    {
        var (doc, list, _) = PageIso02("BT /F 50 Tf 50 50 Td (ba) Tj ET", pdf =>
            Type3Iso02(pdf, 97, [1000, 1000], [(97, "a"), (98, "b")], [("a", SquareD0Iso02)]));
        using (doc)
        {
            using var bitmap = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
            var b = bitmap.GetPixel(75, bitmap.Height - 1 - 75);
            var a = bitmap.GetPixel(125, bitmap.Height - 1 - 75);
            Assert.True(b.Red > 240 && b.Green > 240 && b.Blue > 240, $"b: {b}");
            Assert.True(a.Red < 40 && a.Green < 40 && a.Blue < 40, $"a: {a}");
        }
    }

    /// <summary>
    /// 9.6.4 sentences 52, 53: the graphics state is saved before a glyph procedure and restored after it — a d0 glyph setting 1 0 0 rg and
    /// 20 w: the rectangle filled after the text is blue as before it, the line stroked after it 3 wide as before it.
    /// </summary>
    [Fact]
    public void Iso02_Type3Glyph_GraphicsStateRestoredAfterIt()
    {
        var (doc, list, _) = PageIso02("0 0 1 rg 3 w BT /F 50 Tf 20 20 Td (a) Tj ET 120 120 50 50 re f 120 20 m 180 20 l S", pdf =>
            Type3Iso02(pdf, 97, [1000], [(97, "a")], [("a", "1000 0 d0 1 0 0 rg 20 w 0 0 1000 1000 re f")]));
        using (doc)
        {
            var items = list.Items.ToArray();
            Assert.Equal(0xFFFF0000u, list.Paints[items[0].Paint].Fill);   // inside the glyph: its own red (d0)
            Assert.Equal(DisplayItemKind.Fill, items[^2].Kind);
            Assert.Equal(0xFF0000FFu, list.Paints[items[^2].Paint].Fill);
            Assert.Equal(DisplayItemKind.Stroke, items[^1].Kind);
            Assert.Equal(3, list.Paints[items[^1].Paint].LineWidth, 3);
        }
    }

    /// <summary>
    /// 9.6.4 sentences 52–53: a glyph procedure finds named resources in the font's /Resources, without them in those of the page — the glyph
    /// paints the form X1; with /Resources in the font its X1 (red) is painted, without them the page's X1 (blue).
    /// </summary>
    [Fact]
    public void Iso02_Type3Glyph_NamedResourcesFromTheFontFirstThenThePage()
    {
        PdfStream Form(string colour)
        {
            var form = TestPdf.FlateStream(Encoding.ASCII.GetBytes(colour + " rg 0 0 1000 1000 re f"));
            form.Dictionary.Set(PdfName.Type, TestPdf.N("XObject"));
            form.Dictionary.Set(PdfName.Subtype, TestPdf.N("Form"));
            form.Dictionary.Set(PdfName.Get("BBox"), TestPdf.Box(0, 0, 1000, 1000));
            return form;
        }
        uint FillOf(bool fontResources)
        {
            var (doc, list) = InterpreterTests.Page("BT /F 50 Tf 20 20 Td (a) Tj ET", pdf =>
            {
                var font = Type3Iso02(pdf, 97, [1000], [(97, "a")], [("a", "1000 0 d0 /X1 Do")]);
                if (fontResources) font.Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("X1", TestPdf.Ref(pdf.Add(Form("1 0 0"))))))));
                return TestPdf.Dict(("Font", TestPdf.Dict(("F", font))), ("XObject", TestPdf.Dict(("X1", TestPdf.Ref(pdf.Add(Form("0 0 1")))))));
            });
            using (doc) return list.Paints[list.Items.Span[0].Paint].Fill;
        }
        Assert.Equal(0xFFFF0000u, FillOf(true));
        Assert.Equal(0xFF0000FFu, FillOf(false));
    }

    // ------------------------------------------------------------ 9.6.5 encodings

    /// <summary>
    /// 9.6.5.1 sentence 42: the ranges of /Differences may stand in any order but must not overlap — the PDF/A copy of a damaged Type 3 font
    /// (Differences [97 /a], the glyph of 97 with another width than /Widths) names code 97 only once.
    /// </summary>
    [Fact]
    public void Iso02_PdfACopyOfDamagedType3_DifferencesWithoutDuplicateCode()
    {
        var pdf = TestPdf.Simple(1);
        var glyph = pdf.Add(TestPdf.FlateStream("300 0 d0 0 0 300 700 re f"u8.ToArray()));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("FontMatrix", TestPdf.Box(0.001, 0, 0, 0.001, 0, 0)),
            ("FontBBox", TestPdf.Box(0, 0, 500, 700)), ("FirstChar", PdfInteger.Of(97)), ("LastChar", PdfInteger.Of(97)), ("Widths", TestPdf.Box(500)),
            ("Encoding", EncodingIso02((97, "a"))), ("CharProcs", TestPdf.Dict(("a", TestPdf.Ref(glyph)))),
            ("Resources", new PdfDictionary())));
        pdf.Replace(4, TestPdf.FlateStream("BT /F0 20 Tf 72 700 Td (a) Tj ET"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F0", TestPdf.Ref(font))))));
        var result = PdfATests.Write(pdf.Build(), PdfAConformance.A2b);
        using var doc = PdfDocument.Open(result.Bytes);
        var written = doc.Pages[0].Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F0"))!;
        var differences = written.GetDictionary(PdfName.Get("Encoding"))!.GetArray(PdfName.Get("Differences"))!;
        var codes = new List<int>();
        var code = 0;
        for (var i = 0; i < differences.Count; i++)
            if (differences.Get(i).AsNumber() is { } n) code = (int)n;
            else if (differences.Get(i) is PdfName) codes.Add(code++);
        Assert.Equal(codes.Distinct().Count(), codes.Count);
    }

    private static Func<TestPdf, PdfObject> Type1Iso02(params (int Code, string Name)[] differences) => pdf =>
        TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("KneadTest")),
            ("Encoding", EncodingIso02(differences)), ("FontDescriptor", DescriptorIso02(pdf, "FontFile", Fonts.Type1Builder.Build())));

    /// <summary>
    /// 9.6.5.2 sentences 6, 7: /Encoding of the font dictionary replaces the program's own, and /Differences names glyphs of the program — an
    /// embedded Type 1 font (A at 65 in its built-in encoding) with /Differences [66 /A]: code 66 shows the A.
    /// </summary>
    [Fact]
    public void Iso02_EmbeddedType1_DifferencesNameGlyphsOfTheProgram()
    {
        var (doc, _, font) = PageIso02("BT /F 10 Tf 10 10 Td (AB) Tj ET", Type1Iso02((66, "A")));
        using (doc)
        {
            var a = font.Glyph(65, 1).Outline;
            var b = font.Glyph(66, 1).Outline;
            Assert.False(b.IsEmpty);
            Assert.Equal(a.Bounds, b.Bounds);
            Assert.Equal(0.6, font.Glyph(66, 1).Width, 6);
        }
    }

    /// <summary>
    /// 9.6.5.2 sentences 8–10: every Type 1 program has a .notdef, and a name the program lacks is replaced by it — /Differences names
    /// „fehlt“ for 65 while the built-in encoding has A there: code 65 shows the .notdef (empty, 500 wide), not the A.
    /// </summary>
    [Fact]
    public void Iso02_EmbeddedType1_NameMissingInTheProgram_ShowsNotdef()
    {
        var (doc, _, font) = PageIso02("BT /F 10 Tf 10 10 Td (A) Tj ET", Type1Iso02((65, "fehlt")));
        using (doc)
        {
            var glyph = font.Glyph(65, 1);
            Assert.True(glyph.Outline.IsEmpty, "statt .notdef die Glyphe der eingebauten Kodierung");
            Assert.Equal(0.5, glyph.Width, 6);
        }
    }

    /// <summary>
    /// 9.6.5.4 sentences 29, 30, 32 and 49: without a (3,1) cmap but with (1,0), a name goes to its code in Mac OS Roman, where Euro replaces
    /// currency at DBh — an embedded TrueType with only a (1,0) cmap (DBh → the Euro glyph) and no glyph names, /Differences [65 /Euro]: code
    /// 65 shows the Euro glyph.
    /// </summary>
    [Fact]
    public void Iso02_TrueTypeWithMacCmap_EuroAtDB()
    {
        var original = OpenTypeFont.Parse(LiberationIso02);
        var euro = original.GlyphForUnicode(0x20AC);
        Assert.True(euro > 0);
        var mac = new int[256];
        mac[0xDB] = euro;
        var post = TableIso02(LiberationIso02, "post")![..32];
        post[0] = 0; post[1] = 3; post[2] = 0; post[3] = 0;   // version 3: no glyph names
        var program = Fonts.ColorFontBuilder.WithTables(LiberationIso02, new Dictionary<string, byte[]?>
        {
            ["cmap"] = CmapIso02((1, 0, Format6Iso02(0, mac))),
            ["post"] = post,
        });
        var (doc, _, font) = PageIso02("BT /F 10 Tf 10 10 Td (A) Tj ET", pdf => TrueTypeIso02(pdf, program, EncodingIso02((65, "Euro")), 65, [556]));
        using (doc)
        {
            var glyph = font.Glyph(65, 1).Outline;
            Assert.False(glyph.IsEmpty);
            Assert.Equal(original.Outline(euro).Bounds, glyph.Bounds);
        }
    }

    /// <summary>
    /// 9.6.5.4 sentence 31: a name that cannot be mapped through the cmap is looked up in the post table — a nonsymbolic TrueType whose (3,1)
    /// cmap maps only the space, /Differences [66 /A]: code 66 shows the glyph named A in post.
    /// </summary>
    [Fact]
    public void Iso02_TrueTypeNameOnlyInPost_GlyphFromPost()
    {
        var original = OpenTypeFont.Parse(LiberationIso02);
        var a = original.GlyphForUnicode('A');
        var program = Fonts.ColorFontBuilder.WithTables(LiberationIso02, new Dictionary<string, byte[]?>
        {
            ["cmap"] = CmapIso02((3, 1, Format4Iso02((0x20, original.GlyphForUnicode(' '))))),
        });
        Assert.Equal(0, OpenTypeFont.Parse(program).GlyphForUnicode('A'));
        var (doc, _, font) = PageIso02("BT /F 10 Tf 10 10 Td (B) Tj ET", pdf => TrueTypeIso02(pdf, program, EncodingIso02((66, "A")), 66, [667]));
        using (doc)
        {
            var glyph = font.Glyph(66, 1).Outline;
            Assert.False(glyph.IsEmpty);
            Assert.Equal(original.Outline(a).Bounds, glyph.Bounds);
        }
    }

    /// <summary>
    /// 9.6.5.4 sentences 33–48, 50 (Table 113): the fifteen additions of Mac OS Roman stand at their codes in the table the (1,0) lookup uses.
    /// </summary>
    [Fact]
    public void Iso02_MacOsRomanAdditions_AtTheirCodes()
    {
        var expected = new (int Code, string Name)[]
        {
            (0xAD, "notequal"), (0xB0, "infinity"), (0xB2, "lessequal"), (0xB3, "greaterequal"), (0xB6, "partialdiff"), (0xB7, "summation"),
            (0xB8, "product"), (0xB9, "pi"), (0xBA, "integral"), (0xBD, "Omega"), (0xC3, "radical"), (0xC5, "approxequal"), (0xC6, "Delta"),
            (0xD7, "lozenge"), (0xF0, "apple"),
        };
        foreach (var (code, name) in expected) Assert.Equal(name, FontTables.MacRomanEncoding[code]);
    }
}
