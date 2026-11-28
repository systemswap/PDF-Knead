using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package E27: symmetric swapping after U+206A (question 73), and tests for sentences whose evidence was missing or weak in the second
/// sample of the report (docs/FEHLERGEDAECHTNIS.md, No. 1215 ff.).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ question 73: U+206A, U+206B

    /// <summary>
    /// UNI 23.3.1 sentences 9–18 (question 73): after U+206A INHIBIT SYMMETRIC SWAPPING the characters with Bidi_Mirrored keep their own glyph
    /// also at an odd level, up to U+206B ACTIVATE SYMMETRIC SWAPPING; the two do not nest, the default state is activated, and a paragraph
    /// separator ends the state as it ends the other formatting characters of bidirectional text. The levels do not change, nor does the text
    /// (each glyph still stands for the character stored). Before, U+206A was not evaluated and the brackets after it were mirrored.
    /// </summary>
    [Fact]
    public void E27_SymmetricSwapping_InhibitedFromU206AUpToU206B()
    {
        // „א U+206A (ב) U+206B (ג)“: the first pair as stored, the second mirrored (L4)
        const string text = "\u05D0\u206A(\u05D1)\u206B(\u05D2)";
        var bidi = Bidi.Analyze(text);
        Assert.Equal(1, bidi.Level(2) % 2);
        Assert.Equal(1, bidi.Level(6) % 2);
        Assert.Equal("\u05D0\u206A(\u05D1)\u206B)\u05D2(", Bidi.MirrorOdd(text, bidi.Level, _ => true));
        Assert.False(bidi.SwappingInhibitedBefore(1));
        Assert.True(bidi.SwappingInhibitedBefore(2));
        Assert.True(bidi.SwappingInhibitedBefore(5));
        Assert.False(bidi.SwappingInhibitedBefore(6));
        // no nesting: one U+206B after two U+206A activates again
        const string twice = "\u05D0\u206A\u206A(\u206B(";
        Assert.Equal("\u05D0\u206A\u206A(\u206B)", Bidi.MirrorOdd(twice, Bidi.Analyze(twice).Level, _ => true));
        // the end of the paragraph ends the state
        const string paragraphs = "\u05D0\u206A(\n\u05D1(";
        Assert.Equal("\u05D0\u206A(\n\u05D1)", Bidi.MirrorOdd(paragraphs, Bidi.Analyze(paragraphs).Level, _ => true));
        // a part of a longer text takes the state at its start
        Assert.Equal("(", Bidi.MirrorOdd("(", _ => 1, _ => true, inhibitedAtStart: true));
        Assert.Equal(")", Bidi.MirrorOdd("(", _ => 1, _ => true));
        // the levels are those without the format character
        Assert.Equal(Bidi.Levels("\u05D0(\u05D1)"), Bidi.Levels("\u05D0\u206A(\u05D1)").Where((_, i) => i != 1));
    }

    /// <summary>
    /// UNI 23.3.1 sentences 9–18 (question 73), drawn: formatted text and plain text with „א U+206A (ב)“ draw the brackets with their own glyphs from
    /// the left as „)“ ב „(“ א — not mirrored — and the glyph of the stored „)“ still stands for „)“; without U+206A both are mirrored as before.
    /// rtlm, too, leaves a mirrored character alone where swapping is inhibited (∂ under hebr rtlm → X: „א∂“ takes X, „א U+206A ∂“ does not), also
    /// for a run that starts behind the U+206A.
    /// </summary>
    [Fact]
    public void E27_SymmetricSwapping_Inhibited_DrawnUnmirrored_TextKept()
    {
        var face = ElementFace.For(ElementFont.Sans);
        int[] invisible = [0, face.Glyph(' '), face.Glyph(0x206A), face.Glyph(0x206B)];
        List<int> Visible(ElementAppearance appearance) => [.. Uax9Drawn(appearance).Where(g => !invisible.Contains(g))];
        var inhibited = new RichTextElement { Rect = new PdfRectangle(0, 0, 300, 50), Content = new RichText("\u05D0\u206A(\u05D1)", Uax9Style) };
        var appearance = ElementAppearance.For(inhibited);
        Assert.Equal(Uax9Glyphs(")\u05D1(\u05D0"), Visible(appearance));
        var map = appearance.FaceUses.Single().Glyphs;
        Assert.Equal(")", map[Uax9Glyphs(")")[0]]);
        Assert.Equal("(", map[Uax9Glyphs("(")[0]]);
        var activated = new RichTextElement { Rect = new PdfRectangle(0, 0, 300, 50), Content = new RichText("\u05D0\u206B(\u05D1)", Uax9Style) };
        Assert.Equal(Uax9Glyphs("(\u05D1)\u05D0"), Visible(ElementAppearance.For(activated)));

        var plain = new TextElement { Rect = new PdfRectangle(0, 0, 300, 40), Text = "\u05D0\u206A(\u05D1)" };
        var line = Assert.Single(ElementAppearance.Layout(plain, face));
        Assert.Equal(Uax9Glyphs(")\u05D1(\u05D0"), line.Glyphs.Where(g => !invisible.Contains(g)));

        int partial = Ot02G('\u2202'), alef = Ot02G('\u05D0'), x = Ot02G('X');
        var shaper = Ot02Shaper(E11Layout("hebr", ("rtlm", E11Sub((partial, x)))));
        Assert.Equal([alef, x], Ot02Shape(shaper, "\u05D0\u2202"));
        Assert.Contains(partial, Ot02Shape(shaper, "\u05D0\u206A\u2202"));
        Assert.DoesNotContain(x, Ot02Shape(shaper, "\u05D0\u206A\u2202"));
        Assert.Equal([alef, x], shaper.Shape("\u05D0\u2202", null, null, true).Select(g => g.Glyph));
        Assert.Equal([alef, partial], shaper.Shape("\u05D0\u2202", null, null, true, swappingInhibited: true).Select(g => g.Glyph));
    }

    // ------------------------------------------------------------ 9.8.2: ForceBold

    /// <summary>
    /// 9.8.2 sentences 28, 40, 41 (reader): ForceBold (bit 19) of a font that is not embedded asks for the bold substitute only where the name
    /// does not name a face of the standard 14 or Arial — measured against the three reference readers at 6–9 points and 72, 100, 150 dpi: all
    /// three draw Helvetica, Arial, ArialMT, Times-Roman, TimesNewRoman, Courier and CourierNew regular with the bit set, two of three take a bold
    /// substitute for any other name (Verdana, a made-up name). Before, the bit made every one of them bold. An embedded font draws the same
    /// pixels with and without the bit (ForceBold_SmallEmbeddedGlyphs_SamePixels; none of the three thickens small glyphs).
    /// </summary>
    [Theory]
    [InlineData("Helvetica", "LiberationSans-Regular.ttf")]
    [InlineData("Arial", "LiberationSans-Regular.ttf")]
    [InlineData("ABCDEF+ArialMT", "LiberationSans-Regular.ttf")]
    [InlineData("Times-Roman", "LiberationSerif-Regular.ttf")]
    [InlineData("TimesNewRoman", "LiberationSerif-Regular.ttf")]
    [InlineData("Courier", "LiberationMono-Regular.ttf")]
    [InlineData("CourierNew", "LiberationMono-Regular.ttf")]
    [InlineData("Helvetica-Bold", "LiberationSans-Bold.ttf")]
    [InlineData("Verdana", "LiberationSans-Bold.ttf")]
    [InlineData("MusterSchrift", "LiberationSans-Bold.ttf")]
    public void E27_ForceBold_BoldSubstituteOnlyForOtherNames(string name, string file)
    {
        var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N(name)), ("Flags", PdfInteger.Of(32 | 1 << 18)),
            ("ItalicAngle", PdfInteger.Of(0)));
        Assert.Equal(file, StandardFonts.Choose(name, descriptor).File);
        descriptor.Set(PdfName.Get("Flags"), PdfInteger.Of(32));
        Assert.Equal(name.Contains("Bold", StringComparison.Ordinal) ? file : file.Replace("Bold", "Regular", StringComparison.Ordinal),
            StandardFonts.Choose(name, descriptor).File);
    }

    // ------------------------------------------------------------ UAX #14: SP NBSP, LB8a

    /// <summary>
    /// UAX14 5.1 sentences 466–470: NBSP keeps the characters on both sides together, but after a SPACE the line breaks after the SPACE and the
    /// NBSP goes visibly to the next line — „aaaa NBSP bbbb“ in a width for seven letters: „aaaa “ | „NBSP bbbb“; without the space no break.
    /// </summary>
    [Fact]
    public void E27_LineBreak_SpaceBeforeNoBreakSpace_BreaksAfterTheSpace()
    {
        var breaks = LineBreaking.Analyze("aaaa \u00A0bbbb");
        Assert.False(breaks.Allowed(4));
        Assert.True(breaks.Allowed(5));
        Assert.False(breaks.Allowed(6));
        var glued = LineBreaking.Analyze("aaaa\u00A0bbbb");
        Assert.False(glued.Allowed(4));
        Assert.False(glued.Allowed(5));
        var lines = E20LineTexts(E20Layout("aaaa \u00A0bbbb", E20Width("aaaabbb") + 0.5));
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("aaaa", lines[0]);
        Assert.Equal("\u00A0bbbb", lines[1]);
    }

    /// <summary>
    /// UAX14 5.1 sentences 839–845 and 6.2 (LB8a): no break after ZWJ, also between ideographs, which may break anywhere else — „中 ZWJ 文“ has no
    /// opportunity, „中文“ one; two such pairs in a width for three ideographs break between the pairs only.
    /// </summary>
    [Fact]
    public void E27_LineBreak_NoBreakAfterZwj_BetweenIdeographs()
    {
        var joined = LineBreaking.Analyze("中\u200D文");
        Assert.False(joined.Allowed(1));
        Assert.False(joined.Allowed(2));
        Assert.True(LineBreaking.Analyze("中文").Allowed(1));
        var width = E20Width("中文中") + 0.5;
        Assert.Equal(["中\u200D文", "中\u200D文"], E20LineTexts(E20Layout("中\u200D文中\u200D文", width)));
        Assert.Equal(["中文中", "文"], E20LineTexts(E20Layout("中文中文", width)));
    }

    // ------------------------------------------------------------ OT 27: kern

    /// <summary>A kern table with one format 0 subtable of the coverage given and one pair; <paramref name="apple"/>: the version 1.0 header.</summary>
    private static byte[] E27Kern(bool apple, int coverage, int left, int right, int value)
    {
        var b = new List<byte>();
        void U16(int v)
        {
            b.Add((byte)(v >> 8));
            b.Add((byte)v);
        }
        if (apple)
        {
            U16(1);
            U16(0);
            U16(0);
            U16(1);
        }
        else
        {
            U16(0);
            U16(1);
        }
        U16(0);
        U16(6 + 8 + 6);
        U16(coverage);
        U16(1);
        U16(6);
        U16(0);
        U16(0);
        U16(left);
        U16(right);
        U16(value);
        return [.. b];
    }

    /// <summary>
    /// OT 27 sentences 1–4 and 23–25: the kern table applies in its Microsoft version 0 with horizontal data (coverage bit 0 set): „AV“ −100; a
    /// subtable with vertical data (bit 0 clear) and a table in the extended Apple version (1.0) are passed over.
    /// </summary>
    [Fact]
    public void E27_KernTable_HorizontalVersion0Only()
    {
        int a = Ot02G('A'), v = Ot02G('V');
        double Advance(byte[]? kern) => TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(ShaperLimitTests.Liberation,
            new Dictionary<string, byte[]?> { ["GSUB"] = null, ["GPOS"] = null, ["GDEF"] = null, ["kern"] = kern }))).Shape("AV")[0].XAdvance;
        var plain = Advance(null);
        Assert.Equal(plain - 100, Advance(E27Kern(false, 0x0001, a, v, -100)));
        Assert.Equal(plain, Advance(E27Kern(false, 0x0000, a, v, -100)));
        Assert.Equal(plain, Advance(E27Kern(true, 0x0001, a, v, -100)));
    }

    // ------------------------------------------------------------ OT 6: CBLC index formats 2, 3, 5

    private static byte[] E27Png(int width, int height)
    {
        using var output = new MemoryStream();
        Core.Imaging.PngEncoder.Encode(width, height, 8, Core.Imaging.PngColorType.Rgba, (_, row) => row.Fill(200), output,
            level: System.IO.Compression.CompressionLevel.Fastest);
        return output.ToArray();
    }

    /// <summary>
    /// Liberation Sans with a CBLC of one 32-bit strike at 96 ppem for the glyphs „M“ and the one after it, through one index subtable whose bytes
    /// after the header (index format, image format, image data at 4) are <paramref name="subtable"/>, and a CBDT with <paramref name="images"/>.
    /// </summary>
    private static OpenTypeFont E27Cbdt(int indexFormat, int imageFormat, byte[] subtable, byte[] images)
    {
        int first = Ot02G('M'), last = first + 1;
        var b = new List<byte>();
        void U16(int v)
        {
            b.Add((byte)(v >> 8));
            b.Add((byte)v);
        }
        void U32(long v)
        {
            U16((int)(v >> 16));
            U16((int)v);
        }
        U32(0x00030000);
        U32(1);
        U32(56);
        U32(8 + 8 + subtable.Length);
        U32(1);
        U32(0);
        b.AddRange(new byte[24]);
        U16(first);
        U16(last);
        b.AddRange([96, 96, 32, 1]);
        U16(first);
        U16(last);
        U32(8);
        U16(indexFormat);
        U16(imageFormat);
        U32(4);
        b.AddRange(subtable);
        byte[] cbdt = [0, 3, 0, 0, .. images];
        return OpenTypeFont.Parse(ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?> { ["CBLC"] = [.. b], ["CBDT"] = cbdt }));
    }

    private static byte[] E27U32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    /// <summary>
    /// OT 6 sentences 28–31 (index subtables of CBLC): format 3 (16-bit offsets per glyph, image format 17 with small metrics), format 2 (one size
    /// for all images, the big metrics in the index subtable, image format 19) and format 5 (the same with a list of glyphs: only the glyph listed
    /// has an image) each give the image of 12 × 10 pixels at its place.
    /// </summary>
    [Fact]
    public void E27_CblcIndexFormats2_3_5_GiveTheImages()
    {
        int first = Ot02G('M');
        var png = E27Png(12, 10);
        // format 3: two records of small metrics (height 10, width 12, bearing 1/9, advance 12) and the PNG
        byte[] record = [10, 12, 1, 9, 12, .. E27U32(png.Length), .. png];
        var three = ColorGlyphs.For(E27Cbdt(3, 17, [0, 0, (byte)(record.Length >> 8), (byte)record.Length, (byte)(2 * record.Length >> 8), (byte)(2 * record.Length), 0, 0],
            [.. record, .. record]))!;
        foreach (var glyph in new[] { first, first + 1 })
        {
            var image = three.Glyph(glyph);
            Assert.NotNull(image);
            Assert.Equal((1, -1, 12, 10), (image.Value.X, image.Value.Y, image.Value.Image.Width, image.Value.Image.Height));
        }
        // formats 2 and 5: image size and big metrics (height 10, width 12, bearing 2/8, advance 12, vertical 0/0/0) in the index subtable
        byte[] image19 = [.. E27U32(png.Length), .. png];
        byte[] big = [10, 12, 2, 8, 12, 0, 0, 0];
        var two = ColorGlyphs.For(E27Cbdt(2, 19, [.. E27U32(image19.Length), .. big], [.. image19, .. image19]))!;
        var second = two.Glyph(first + 1)!.Value;
        Assert.Equal((2, -2, 12, 10), (second.X, second.Y, second.Image.Width, second.Image.Height));
        Assert.NotNull(two.Glyph(first));
        var five = ColorGlyphs.For(E27Cbdt(5, 19, [.. E27U32(image19.Length), .. big, .. E27U32(1), (byte)((first + 1) >> 8), (byte)(first + 1), 0, 0], image19))!;
        Assert.Null(five.Glyph(first));
        var listed = five.Glyph(first + 1)!.Value;
        Assert.Equal((2, -2, 12, 10), (listed.X, listed.Y, listed.Image.Width, listed.Image.Height));
    }

    // ------------------------------------------------------------ 9.7.4.2: the program of a CIDFontType0 in PDF/A

    /// <summary>
    /// 9.7.4.2 sentence 6 (checker): the CFF program of a CIDFontType0 stands in FontFile3 with /Subtype /CIDFontType0C or /OpenType — the bare CFF
    /// under /CIDFontType0C passes; under /Type1C (the subtype of a Type 1 program) it counts as not embedded in the form prescribed; an OpenType
    /// program passes in PDF/A-2 and is an unknown subtype in PDF/A-1 (PDF 1.4 knows none).
    /// </summary>
    [Theory]
    [InlineData("CIDFontType0C", false, false, null)]
    [InlineData("Type1C", false, false, "FontEmbedded")]
    [InlineData("OpenType", true, false, null)]
    [InlineData("OpenType", true, true, "FontFileSubtype")]
    public void E27_PdfAChecker_ProgramOfACidFontType0(string subtype, bool openType, bool partOne, string? fault)
    {
        var cff = Iso03NotoJp.Value.Cff!;
        int[] order = [0, cff.GlyphByCid(100)];
        byte[] subset = [.. CffSubsetter.Subset(Iso03NotoCff(), order)];
        var data = openType ? TrueTypeSubsetter.CffAsOpenType(Iso03NotoJp.Value, subset, order) : subset;
        var file = PdfARuleCases.WithCidTrueType(
            cid: c => c.Replace("/Subtype/CIDFontType2", "/Subtype/CIDFontType0", StringComparison.Ordinal).Replace("/CIDToGIDMap/Identity", "", StringComparison.Ordinal),
            descriptor: d => d.Replace("/FontFile2 7 0 R", "/FontFile3 10 0 R", StringComparison.Ordinal), shown: "<0064>",
            more: PdfARuleCases.Stream(10, $"/Subtype/{subtype}", System.Text.Encoding.Latin1.GetString(data)))();
        using var doc = PdfDocument.Open(file);
        var part = partOne ? PdfAConformance.A1b : PdfAConformance.A2b;
        var found = PdfACheck.Check(doc, part);
        var embedded = found.Where(v => v.Id == PdfARules.FontEmbedded.Id(part)).ToList();
        var unknown = found.Where(v => v.Id == PdfARules.FontFileSubtype.Id(part)).ToList();
        if (fault == "FontEmbedded") Assert.NotEmpty(embedded);
        else Assert.Empty(embedded);
        if (fault == "FontFileSubtype") Assert.NotEmpty(unknown);
        else Assert.Empty(unknown);
    }

    // ------------------------------------------------------------ CFF 5: INDEX

    /// <summary>
    /// CFF 5 sentences 1–13 (reader): an INDEX of three entries of 1, 3 and 6 bytes, offsets of 1 and of 2 bytes — each entry from its offset to
    /// the next one, the extra last offset gives the length of the last; the reader stands behind the data afterwards.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void E27_CffIndex_EntriesOfDifferentLengths(int offSize)
    {
        var bytes = new List<byte> { 0, 3, (byte)offSize };
        foreach (var offset in new[] { 1, 2, 5, 11 })
        {
            if (offSize == 2) bytes.Add((byte)(offset >> 8));
            bytes.Add((byte)offset);
        }
        bytes.AddRange("abcdefghij"u8.ToArray());
        bytes.Add(0xFF);
        var data = new ArraySegment<byte>([.. bytes]);
        var reader = new FontReader(data);
        var index = CffFont.CffIndex.Read(data, ref reader);
        Assert.Equal(3, index.Count);
        Assert.Equal("a", System.Text.Encoding.ASCII.GetString(index.Item(data, 0)));
        Assert.Equal("bcd", System.Text.Encoding.ASCII.GetString(index.Item(data, 1)));
        Assert.Equal("efghij", System.Text.Encoding.ASCII.GetString(index.Item(data, 2)));
        Assert.Equal(0xFF, data[reader.Position]);
    }

    // ------------------------------------------------------------ OT 62: language tags

    /// <summary>
    /// OT 62 sentences 597–616: language tags with three-letter codes of ISO 639 (as /Lang may carry them) give the tags of the registry —
    /// str STR, suk SUK, suq SUR, swe SVE, szl SZL, tab TAB, tgk TAJ, syr and syc SYR, aii SWA and SYR, tru SYR and TUA; the script variants
    /// SYRE, SYRJ and SYRN by the script subtag the registry names as their equivalent (Syre, Syrj, Syrn), each before SYR.
    /// </summary>
    [Fact]
    public void E27_LanguageTags_ThreeLetterCodes_AndSyriacVariants()
    {
        Assert.Equal(["STR "], UnicodeScripts.OpenTypeLanguages("str"));
        Assert.Equal(["SUK "], UnicodeScripts.OpenTypeLanguages("suk"));
        Assert.Equal(["SUR "], UnicodeScripts.OpenTypeLanguages("suq"));
        Assert.Equal(["SVE "], UnicodeScripts.OpenTypeLanguages("swe-SE"));
        Assert.Equal(["SZL "], UnicodeScripts.OpenTypeLanguages("szl"));
        Assert.Equal(["TAB "], UnicodeScripts.OpenTypeLanguages("tab"));
        Assert.Equal(["TAJ "], UnicodeScripts.OpenTypeLanguages("tgk"));
        Assert.Equal(["SYR "], UnicodeScripts.OpenTypeLanguages("syr"));
        Assert.Equal(["SYR "], UnicodeScripts.OpenTypeLanguages("syc"));
        Assert.Equal(["SWA ", "SYR "], UnicodeScripts.OpenTypeLanguages("aii"));
        Assert.Equal(["SYR ", "TUA "], UnicodeScripts.OpenTypeLanguages("tru"));
        Assert.Equal(["SYRE", "SYR "], UnicodeScripts.OpenTypeLanguages("syr-Syre"));
        Assert.Equal(["SYRJ", "SYR "], UnicodeScripts.OpenTypeLanguages("syc-Syrj"));
        Assert.Equal(["SYRN", "SYR "], UnicodeScripts.OpenTypeLanguages("syr-Syrn-IQ"));
        Assert.Equal(["SWA ", "SYR "], UnicodeScripts.OpenTypeLanguages("aii-Syrn"));
    }

    // ------------------------------------------------------------ UAX #9: the paragraph level of a drawn line (A221)

    /// <summary>
    /// UAX9 2, 3.3 and 4 (HL1), reader: a line of a foreign page drawn in visual order is read back with the paragraph level estimated from its
    /// ends — both ends right to left: level 1 (drawn „ג ab בא“ reads „אב ab ג“); beginning right to left and ending left to right with more
    /// right-to-left letters: level 1 (drawn „גבא ab“ reads „ab אבג“); as many of each: level 0 (drawn „בא ab“ reads „אב ab“).
    /// </summary>
    [Theory]
    [InlineData("03060405060201", "\u05D0\u05D1 ab \u05D2")]
    [InlineData("030201060405", "ab \u05D0\u05D1\u05D2")]
    [InlineData("0201060405", "\u05D0\u05D1 ab")]
    public void E27_ExtractedLine_ParagraphLevelFromItsEnds(string codes, string read)
    {
        using var doc = PdfDocument.Open(MisreadTextTests.File(["\u05D0", "\u05D1", "\u05D2", "a", "b", " "], codes));
        var text = Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.BuildText(doc, doc.Pages[0]));
        Assert.Equal(read, text.Text.Split('\n')[0]);
    }

    // ------------------------------------------------------------ Kannada, Malayalam

    /// <summary>
    /// UNI 12.8.3 sentences 3–8 (knd2): ZWNJ right after the dead consonant keeps it out of the conjunct — ka virama ssa forms the font's
    /// conjunct (pres), ka virama ZWNJ ssa keeps the visible virama; the same as the shaping library.
    /// </summary>
    [Fact]
    public void E27_Kannada_ZwnjAfterTheVirama_KeepsItVisible()
    {
        int ka = E19G(Kn, 0x0C95), virama = E19G(Kn, 0x0CCD), ssa = E19G(Kn, 0x0CB7);
        var font = E19Indic(Kn, ("pres", E12Lig(([ka, virama, ssa], E19F(1)))));
        E19Expect(font, "\u0C95\u0CCD\u0CB7", E19F(1));
        E19Expect(font, "\u0C95\u0CCD\u200C\u0CB7", ka, virama, ssa);
    }

    /// <summary>UNI 12.9.1 sentences 6–14 (table 12-32): each of the five Malayalam sequences not to use gets the dotted circle before its second part.</summary>
    [Theory]
    [InlineData(0x0D07, 0x0D57)]
    [InlineData(0x0D09, 0x0D57)]
    [InlineData(0x0D0E, 0x0D46)]
    [InlineData(0x0D12, 0x0D3E)]
    [InlineData(0x0D12, 0x0D57)]
    public void E27_Malayalam_EverySequenceNotToUse_GetsTheCircle(int first, int second)
    {
        var font = E19Font([(Ml, Ml + 0x7F, E19Base)], E11Layout("mlm2", ("nukt", E11Sub((E19G(Ml, 0x0D15), E19G(Ml, 0x0D15))))));
        E19Circle(font, char.ConvertFromUtf32(first) + char.ConvertFromUtf32(second));
    }

    /// <summary>
    /// UNI 12.9.3 sentence 94: the digraph rra virama rra with a vowel sign acts as one unit — where the font stacks the digraph (its second rra a
    /// below-base form, blwf), the left part of the vowel sign goes to the left of the whole digraph and the right part to its right (rra virama
    /// rra E: E, rra, the stacked rra; with O: its E part first, its AA part last); a font without the stacked form shows the side-by-side form,
    /// the vowel sign before the second rra. One syllable, the text as typed; the same as the shaping library.
    /// </summary>
    [Fact]
    public void E27_Malayalam_RraDigraph_VowelSignAroundTheWhole()
    {
        int rra = E19G(Ml, 0x0D31), virama = E19G(Ml, 0x0D4D), e = E19G(Ml, 0x0D46), aa = E19G(Ml, 0x0D3E);
        var font = E19Indic(Ml, ("blwf", E12Lig(([virama, rra], E19F(1)))));
        E19Expect(font, "\u0D31\u0D4D\u0D31\u0D46", e, rra, E19F(1));
        E19Expect(font, "\u0D31\u0D4D\u0D31\u0D4A", e, rra, E19F(1), aa);
        E19OneSyllable(font, "\u0D31\u0D4D\u0D31\u0D4A");
        var sideBySide = E19Indic(Ml);
        E19Expect(sideBySide, "\u0D31\u0D4D\u0D31\u0D46", rra, virama, e, rra);
    }

    // ------------------------------------------------------------ Sunuwar in the editor

    /// <summary>
    /// UNI 13.21.1 sentence 9: words of Sunuwar are separated by spaces — its letters and digits belong to a word for the editor (word steps and
    /// selection by double click), the space does not.
    /// </summary>
    [Fact]
    public void E27_Sunuwar_LettersAndDigitsAreWordCharacters()
    {
        Assert.True(EditUnits.IsWordAt("\U00011BC0", 0));
        Assert.True(EditUnits.IsWordAt("\U00011BE0", 0));
        Assert.True(EditUnits.IsWordAt("\U00011BF0", 0));
        Assert.False(EditUnits.IsWordAt("\U00011BC0 \U00011BC1", 2));
    }

    // ------------------------------------------------------------ CFF 4: arrays in a DICT

    /// <summary>
    /// CFF 4 sentence 52 (reader): the length of an array is the number of operands before its operator — a FontMatrix of six operands
    /// (0.0005 0 0 0.0005 0 0, two of them real numbers) scales the glyphs; one of five operands is no matrix and the default 0.001 applies.
    /// </summary>
    [Fact]
    public void E27_CffDict_ArrayLength_IsTheNumberOfOperands()
    {
        byte[] half = [30, 0x0A, 0x00, 0x05, 0xFF];
        byte[] six = [.. half, 139, 139, .. half, 139, 139, 12, 7];
        byte[] five = [.. half, 139, 139, .. half, 139, 12, 7];
        var matrix = CffFont.Parse(Cff01Build(new Cff01Spec { TopExtra = six, CharStrings = [[14], [14]] })).FontMatrix;
        Assert.Equal((0.0005, 0.0, 0.0, 0.0005, 0.0, 0.0), (matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F));
        Assert.Equal(0.001, CffFont.Parse(Cff01Build(new Cff01Spec { TopExtra = five, CharStrings = [[14], [14]] })).FontMatrix.A, 6);
    }
}
