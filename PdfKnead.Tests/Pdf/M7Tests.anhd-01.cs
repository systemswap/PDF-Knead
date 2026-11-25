using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Character sets and encodings (ISO 32000-2, Annex D.1 and D.2; Konformitätsabgleich M7, draft anhd-01): the predefined encodings by
/// name, the Latin character set of the standard fonts, the codes of every character in StandardEncoding, MacRomanEncoding, WinAnsiEncoding
/// and PDFDocEncoding, and the footnotes of Table D.2.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>The 229 glyph names of Table D.2, separated by spaces.</summary>
    private const string AnhdD_LatinNames =
        "A a Aacute aacute Acircumflex acircumflex acute Adieresis adieresis AE ae Agrave agrave ampersand Aring aring "
        + "asciicircum asciitilde asterisk at Atilde atilde B b backslash bar braceleft braceright bracketleft bracketright breve "
        + "brokenbar bullet C c caron Ccedilla ccedilla cedilla cent circumflex colon comma copyright currency D d dagger daggerdbl "
        + "degree dieresis divide dollar dotaccent dotlessi E e Eacute eacute Ecircumflex ecircumflex Edieresis edieresis Egrave "
        + "egrave eight ellipsis emdash endash equal Eth eth Euro exclam exclamdown F f fi five fl florin four fraction G g "
        + "germandbls grave greater guillemotleft guillemotright guilsinglleft guilsinglright H h hungarumlaut hyphen I i Iacute "
        + "iacute Icircumflex icircumflex Idieresis idieresis Igrave igrave J j K k L l less logicalnot Lslash lslash M m macron "
        + "minus mu multiply N n nine Ntilde ntilde numbersign O o Oacute oacute Ocircumflex ocircumflex Odieresis odieresis OE oe "
        + "ogonek Ograve ograve one onehalf onequarter onesuperior ordfeminine ordmasculine Oslash oslash Otilde otilde P p "
        + "paragraph parenleft parenright percent period periodcentered perthousand plus plusminus Q q question questiondown "
        + "quotedbl quotedblbase quotedblleft quotedblright quoteleft quoteright quotesinglbase quotesingle R r registered ring S s "
        + "Scaron scaron section semicolon seven six slash space sterling T t Thorn thorn three threequarters threesuperior tilde "
        + "trademark two twosuperior U u Uacute uacute Ucircumflex ucircumflex Udieresis udieresis Ugrave ugrave underscore V v W w "
        + "X x Y y Yacute yacute Ydieresis ydieresis yen Z z Zcaron zcaron zero";

    /// <summary>Opens a one-page file with the font dictionary <paramref name="font"/> builds (it may add further objects) and reads the font.</summary>
    private static (PdfDocument Doc, PdfFont Font) AnhdD_Open(Func<TestPdf, PdfDictionary> font)
    {
        var pdf = TestPdf.Simple(1);
        var number = pdf.Add(font(pdf));
        var doc = PdfDocument.Open(pdf.Build());
        return (doc, PdfFont.Get((PdfDictionary)doc.GetObject(number), doc));
    }

    /// <summary>A simple font that is not embedded, with the given /Encoding (a name, a dictionary, or none).</summary>
    private static PdfDictionary AnhdD_Standard(string subtype, string baseFont, PdfObject? encoding)
    {
        var dict = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N(subtype)), ("BaseFont", TestPdf.N(baseFont)));
        if (encoding is not null) dict.Set(PdfName.Get("Encoding"), encoding);
        return dict;
    }

    /// <summary>An encoding dictionary with an optional /BaseEncoding and /Differences of single codes.</summary>
    private static PdfDictionary AnhdD_Differences(string? baseEncoding, params (int Code, string Name)[] entries)
    {
        var differences = new PdfArray();
        foreach (var (code, name) in entries)
        {
            differences.Add(PdfInteger.Of(code));
            differences.Add(PdfName.Get(name));
        }
        var dict = TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", differences));
        if (baseEncoding is not null) dict.Set(PdfName.Get("BaseEncoding"), PdfName.Get(baseEncoding));
        return dict;
    }

    /// <summary>The text a code of a font reads as.</summary>
    private static string? AnhdD_Text(PdfFont font, int code) => font.Glyph((uint)code, 1).Text;

    /// <summary>
    /// A Type 1 program (eexec-encrypted) with the given glyphs — glyph <c>i</c> a rectangle 100 wide and 20 + 3 i high, so each glyph has bounds
    /// of its own — and the given built-in encoding (all other codes .notdef).
    /// </summary>
    internal static (byte[] Data, int Clear, int Encrypted) AnhdD_Type1(IReadOnlyList<string> glyphs, IReadOnlyDictionary<int, string> builtIn)
    {
        static byte[] Num(int v) => v is >= -107 and <= 107 ? [(byte)(v + 139)]
            : v is >= 108 and <= 1131 ? [(byte)((v - 108) / 256 + 247), (byte)((v - 108) % 256)]
            : [(byte)((-v - 108) / 256 + 251), (byte)((-v - 108) % 256)];
        static byte[] Code(params object[] parts) => [.. parts.SelectMany(p => p is int i ? Num(i) : (byte[])p)];
        byte[] hsbw = [13], rmoveto = [21], rlineto = [5], closepath = [9], endchar = [14], ret = [11];
        var priv = new MemoryStream();
        void Text(string t) => priv.Write(System.Text.Encoding.ASCII.GetBytes(t));
        void Binary(byte[] plain)
        {
            var encrypted = Type1Sample.Encrypt([0, 0, 0, 0, .. plain], 4330);
            Text($"{encrypted.Length} RD ");
            priv.Write(encrypted);
        }
        Text("dup /Private 8 dict dup begin\n/RD{string currentfile exch readstring pop}executeonly def\n/ND{noaccess def}executeonly def\n" +
             "/NP{noaccess put}executeonly def\n/lenIV 4 def\n/Subrs 1 array\ndup 0 ");
        Binary(Code(ret));
        Text(" NP\nND\n");
        Text($"2 index /CharStrings {glyphs.Count + 1} dict dup begin\n/.notdef ");
        Binary(Code(0, 250, hsbw, endchar));
        Text(" ND\n");
        for (var i = 0; i < glyphs.Count; i++)
        {
            Text($"/{glyphs[i]} ");
            Binary(Code(0, 600, hsbw, 50, 0, rmoveto, 100, 0, rlineto, 0, 20 + 3 * i, rlineto, -100, 0, rlineto, closepath, endchar));
            Text(" ND\n");
        }
        Text("end\nend\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");
        var encoding = string.Concat(builtIn.Select(e => $"dup {e.Key} /{e.Value} put\n"));
        var clear = System.Text.Encoding.ASCII.GetBytes("%!PS-AdobeFont-1.0: Musterprobe 001\n12 dict begin\n/FontName /Musterprobe def\n/FontType 1 def\n" +
            "/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n/FontBBox {0 0 1000 1000} readonly def\n/Encoding 256 array\n" +
            "0 1 255 {1 index exch /.notdef put} for\n" + encoding + "readonly def\ncurrentdict end\ncurrentfile eexec\n");
        var encryptedPart = Type1Sample.Encrypt([0, 0, 0, 0, .. priv.ToArray()], 55665);
        var trailer = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(new string('0', 64) + "\n", 8)) + "cleartomark\n");
        return ([.. clear, .. encryptedPart, .. trailer], clear.Length, encryptedPart.Length);
    }

    /// <summary>A Type 1 font dictionary with the program embedded (FontFile) and the given /Encoding (or none).</summary>
    internal static PdfDictionary AnhdD_EmbeddedType1(TestPdf pdf, (byte[] Data, int Clear, int Encrypted) program, PdfObject? encoding, int flags = 32)
    {
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Length1", PdfInteger.Of(program.Clear)), ("Length2", PdfInteger.Of(program.Encrypted)),
            ("Length3", PdfInteger.Of(program.Data.Length - program.Clear - program.Encrypted))), program.Data));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Musterprobe")), ("Flags", PdfInteger.Of(flags)),
            ("FontBBox", TestPdf.Box(0, 0, 1000, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(800)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile", TestPdf.Ref(file))));
        var dict = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Musterprobe")),
            ("FontDescriptor", TestPdf.Ref(descriptor)));
        if (encoding is not null) dict.Set(PdfName.Get("Encoding"), encoding);
        return dict;
    }

    /// <summary>The bundled LiberationSans as an embedded non-symbolic TrueType font with the given /Encoding.</summary>
    private static PdfDictionary AnhdD_EmbeddedTrueType(TestPdf pdf, PdfObject encoding)
    {
        var program = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Length1", PdfInteger.Of(program.Length))), program));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Musterschrift")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(0, -200, 1000, 900)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file))));
        return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("Musterschrift")),
            ("FontDescriptor", TestPdf.Ref(descriptor)), ("Encoding", encoding));
    }

    /// <summary>
    /// Checks rows of Table D.2, given as „name std/mac/win/pdf; …“ with octal codes and „-“ for a dash: in each of the three font encodings the
    /// set of codes that carry the name is exactly the one of the table (empty for a dash), plus only the second codes of footnote 3 (bullet in
    /// WinAnsiEncoding), footnote 5 (hyphen at ADh in WinAnsiEncoding) and footnote 6 (space at CAh in MacRomanEncoding, A0h in WinAnsiEncoding);
    /// the PDFDocEncoding code decodes to the character of the name (Adobe Glyph List) and that character encodes back to exactly that byte.
    /// </summary>
    private static void AnhdD2_Check(string rows)
    {
        (string Label, string[] Table)[] tables =
            [("StandardEncoding", FontTables.StandardEncoding), ("MacRomanEncoding", FontTables.MacRomanEncoding), ("WinAnsiEncoding", FontTables.WinAnsiEncoding)];
        var secondCodes = new Dictionary<(int Table, string Name), int[]>
        {
            [(2, "bullet")] = [0x7F, 0x81, 0x8D, 0x8F, 0x90, 0x9D],
            [(2, "hyphen")] = [0xAD],
            [(2, "space")] = [0xA0],
            [(1, "space")] = [0xCA],
        };
        foreach (var row in rows.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = row.Split(' ');
            var name = parts[0];
            var codes = parts[1].Split('/');
            Assert.Equal(4, codes.Length);
            for (var t = 0; t < tables.Length; t++)
            {
                var expected = new SortedSet<int>();
                if (codes[t] != "-") expected.Add(Convert.ToInt32(codes[t], 8));
                if (secondCodes.TryGetValue((t, name), out var extra)) expected.UnionWith(extra);
                var actual = Enumerable.Range(0, 256).Where(c => tables[t].Table[c] == name).ToList();
                Assert.True(expected.SetEquals(actual),
                    $"{name} in {tables[t].Label}: expected {string.Join(",", expected.Select(c => c.ToString("X2")))}, found {string.Join(",", actual.Select(c => c.ToString("X2")))}");
            }
            var character = GlyphNames.ToUnicode(name);
            Assert.NotNull(character);
            var pdfCode = Convert.ToByte(codes[3], 8);
            Assert.Equal(character, PdfTextEncoding.Decode([pdfCode]));
            Assert.Equal([pdfCode], PdfTextEncoding.Encode(character));
            Assert.Equal([pdfCode], PdfTextEncoding.EncodeDocEncoding(character)!);
        }
    }

    /// <summary>
    /// D.1 sentence 1: the encodings every PDF processor has predefined are known by name — MacRomanEncoding, WinAnsiEncoding and
    /// MacExpertEncoding give their tables; PDFDocEncoding is no font encoding (no table, the font keeps its own).
    /// </summary>
    [Fact]
    public void AnnexD1_Sentence1_PredefinedEncodings_KnownByName()
    {
        Assert.Same(FontTables.MacRomanEncoding, PdfFont.EncodingTable("MacRomanEncoding"));
        Assert.Same(FontTables.WinAnsiEncoding, PdfFont.EncodingTable("WinAnsiEncoding"));
        Assert.Same(FontTables.MacExpertEncoding, PdfFont.EncodingTable("MacExpertEncoding"));
        Assert.Null(PdfFont.EncodingTable("PDFDocEncoding"));
    }

    /// <summary>
    /// D.1 sentence 8: every character of Table D.2 is available in the families Times, Helvetica and Courier — all four styles of each, not
    /// embedded, with /Differences naming all 229 characters: each code draws a glyph with an outline (the space an empty one).
    /// </summary>
    [Fact]
    public void AnnexD1_Sentence8_TimesHelveticaCourier_DrawEveryCharacterOfD2()
    {
        var names = AnhdD_LatinNames.Split(' ');
        Assert.Equal(229, names.Length);
        string[] fonts = ["Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique", "Times-Roman", "Times-Bold", "Times-Italic",
            "Times-BoldItalic", "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique"];
        foreach (var baseFont in fonts)
        {
            var (doc, font) = AnhdD_Open(_ => AnhdD_Standard("Type1", baseFont, AnhdD_Differences(null, [.. names.Select((n, i) => (i, n))])));
            using (doc)
                for (var c = 0; c < names.Length; c++)
                    Assert.True(font.Glyph((uint)c, 1).Outline.IsEmpty == (names[c] == "space"), $"{baseFont}: {names[c]}");
        }
    }

    /// <summary>
    /// D.1 sentence 18 (Table D.1), today's behaviour: the table says a PDF processor has no predefined encoding named StandardEncoding. PDF-Knead
    /// reads the name as the StandardEncoding table all the same — as /Encoding of an embedded Type 1 font whose built-in encoding differs (code 65
    /// draws A, not the built-in X) and as /BaseEncoding, and for a TrueType font (0x27 reads ’, WinAnsi would give '). Measured: the three
    /// reference readers read it the same way (finding B29, special rule noted).
    /// </summary>
    [Fact]
    public void AnnexD1_Sentence18_StandardEncodingName_TodayReadAsTheTable_AsTheReferenceReaders()
    {
        var program = AnhdD_Type1(["A", "X"], new Dictionary<int, string> { [65] = "X" });
        var parsed = Type1Font.Parse(program.Data);
        foreach (PdfObject encoding in new PdfObject[] { PdfName.Get("StandardEncoding"), AnhdD_Differences("StandardEncoding") })
        {
            var (doc, font) = AnhdD_Open(pdf => AnhdD_EmbeddedType1(pdf, program, encoding));
            using (doc) Assert.Equal(parsed.Outline("A").Bounds, font.Glyph(65, 1).Outline.Bounds);
        }
        foreach (PdfObject encoding in new PdfObject[] { PdfName.Get("StandardEncoding"), AnhdD_Differences("StandardEncoding") })
        {
            var (doc, font) = AnhdD_Open(pdf => AnhdD_EmbeddedTrueType(pdf, encoding));
            using (doc) Assert.Equal("’", AnhdD_Text(font, 0x27));
        }
        var (winDoc, win) = AnhdD_Open(pdf => AnhdD_EmbeddedTrueType(pdf, PdfName.Get("WinAnsiEncoding")));
        using (winDoc) Assert.Equal("'", AnhdD_Text(win, 0x27));
    }

    /// <summary>
    /// D.1 sentence 19 (Table D.1): the built-in encoding of a font can be the base over which an encoding dictionary gives differences — an
    /// embedded Type 1 font whose built-in encoding puts X at 65 and Y at 67 (StandardEncoding has A and C there), with /Differences [66 /A] and
    /// no /BaseEncoding: code 66 draws A, codes 65 and 67 the glyphs of the built-in encoding.
    /// </summary>
    [Fact]
    public void AnnexD1_Sentence19_DifferencesOverTheBuiltInEncoding_OfAnEmbeddedType1Font()
    {
        var program = AnhdD_Type1(["A", "X", "Y"], new Dictionary<int, string> { [65] = "X", [67] = "Y" });
        var parsed = Type1Font.Parse(program.Data);
        var (doc, font) = AnhdD_Open(pdf => AnhdD_EmbeddedType1(pdf, program, AnhdD_Differences(null, (66, "A"))));
        using (doc)
        {
            Assert.Equal(parsed.Outline("A").Bounds, font.Glyph(66, 1).Outline.Bounds);
            Assert.Equal(parsed.Outline("X").Bounds, font.Glyph(65, 1).Outline.Bounds);
            Assert.Equal(parsed.Outline("Y").Bounds, font.Glyph(67, 1).Outline.Bounds);
        }
    }

    /// <summary>
    /// D.1 sentences 20T–22T (Table D.1, MacRomanEncoding): predefined and usable with Type 1 and TrueType fonts alike — a Type 1 and a TrueType
    /// font with /Encoding /MacRomanEncoding read 0x8A as „ä“ (WinAnsi would give „Š“) and 0xDB as „¤“.
    /// </summary>
    [Fact]
    public void AnnexD1_Sentences20To22_MacRomanEncoding_Type1AndTrueType()
    {
        foreach (var (subtype, baseFont) in new[] { ("Type1", "Helvetica"), ("TrueType", "Arial") })
        {
            var (doc, font) = AnhdD_Open(_ => AnhdD_Standard(subtype, baseFont, PdfName.Get("MacRomanEncoding")));
            using (doc)
            {
                Assert.Equal("ä", AnhdD_Text(font, 0x8A));
                Assert.Equal("¤", AnhdD_Text(font, 0xDB));
            }
        }
    }

    /// <summary>
    /// D.1 sentences 20T–22T with D.2: MacRomanEncoding as PDF defines it has no character at the 15 codes the Mac OS encoding adds (ADh
    /// notequal, B0h infinity, D7h lozenge …). PDF-Knead reads them as the Mac OS encoding does — a read tolerance measured on 26.11.2026
    /// (one reference reader shows the same characters, one nothing, one a single other glyph; Probe K9 of
    /// tools/normabgleich/proben/lesetoleranzen_m7.py): the test holds today's behaviour.
    /// </summary>
    [Fact]
    public void AnnexD1_Sentences20To22_MacRomanEncoding_MacOsAdditionsReadAsMacOs()
    {
        Assert.Equal("notequal", FontTables.MacRomanEncoding[0xAD]);
        var (doc, font) = AnhdD_Open(_ => AnhdD_Standard("Type1", "Helvetica", PdfName.Get("MacRomanEncoding")));
        using (doc) Assert.Equal("≠", AnhdD_Text(font, 0xAD));
    }

    /// <summary>
    /// D.1 sentences 23T–26T (Table D.1, WinAnsiEncoding): predefined and usable with Type 1 and TrueType fonts alike — a Type 1 and a TrueType
    /// font with /Encoding /WinAnsiEncoding read 0x80 as „€“ and 0x8A as „Š“ (MacRoman would give „ä“).
    /// </summary>
    [Fact]
    public void AnnexD1_Sentences23To26_WinAnsiEncoding_Type1AndTrueType()
    {
        foreach (var (subtype, baseFont) in new[] { ("Type1", "Helvetica"), ("TrueType", "Arial") })
        {
            var (doc, font) = AnhdD_Open(_ => AnhdD_Standard(subtype, baseFont, PdfName.Get("WinAnsiEncoding")));
            using (doc)
            {
                Assert.Equal("€", AnhdD_Text(font, 0x80));
                Assert.Equal("Š", AnhdD_Text(font, 0x8A));
            }
        }
    }

    /// <summary>
    /// D.1 sentence 29T (Table D.1): there is no predefined font encoding named PDFDocEncoding — a Type 1 font that is not embedded with
    /// /Encoding /PDFDocEncoding keeps its standard encoding: 0x27 reads „’“ as in StandardEncoding (not „'“), and 0x80 is no „•“.
    /// </summary>
    [Fact]
    public void AnnexD1_Sentence29_PdfDocEncodingIsNoFontEncoding()
    {
        var (doc, font) = AnhdD_Open(_ => AnhdD_Standard("Type1", "Helvetica", PdfName.Get("PDFDocEncoding")));
        using (doc)
        {
            Assert.Equal("’", AnhdD_Text(font, 0x27));
            Assert.NotEqual("•", AnhdD_Text(font, 0x80));
        }
    }

    /// <summary>
    /// D.1 sentences 30T–33T (Table D.1, MacExpertEncoding): predefined — the table names ff at 0x56, Asmall at 0x61 and onehalf at 0x48, and a
    /// font with /Encoding /MacExpertEncoding reads 0x56 as „ﬀ“ and 0x48 as „½“.
    /// </summary>
    [Fact]
    public void AnnexD1_Sentences30To33_MacExpertEncoding_Predefined()
    {
        Assert.Equal("ff", FontTables.MacExpertEncoding[0x56]);
        Assert.Equal("Asmall", FontTables.MacExpertEncoding[0x61]);
        Assert.Equal("onehalf", FontTables.MacExpertEncoding[0x48]);
        var (doc, font) = AnhdD_Open(_ => AnhdD_Standard("Type1", "Helvetica", PdfName.Get("MacExpertEncoding")));
        using (doc)
        {
            Assert.Equal("ﬀ", AnhdD_Text(font, 0x56));
            Assert.Equal("½", AnhdD_Text(font, 0x48));
        }
    }

    /// <summary>
    /// D.2 sentences 605T–607T (footnote 1): MacRomanEncoding keeps the currency sign at DBh — a MacRoman font reads „¤“ there; with /BaseEncoding
    /// /MacRomanEncoding and /Differences [219 /Euro] the code reads „€“.
    /// </summary>
    [Fact]
    public void AnnexD2_Footnote1_MacRomanCurrency_EuroOnlyThroughDifferences()
    {
        var (doc, font) = AnhdD_Open(_ => AnhdD_Standard("Type1", "Helvetica", PdfName.Get("MacRomanEncoding")));
        using (doc) Assert.Equal("¤", AnhdD_Text(font, 0xDB));
        var (euroDoc, euro) = AnhdD_Open(_ => AnhdD_Standard("Type1", "Helvetica", AnhdD_Differences("MacRomanEncoding", (219, "Euro"))));
        using (euroDoc) Assert.Equal("€", AnhdD_Text(euro, 0xDB));
    }

    /// <summary>D.2 sentence 608T (footnote 2): Zcaron and zcaron stand at 8Eh and 9Eh of WinAnsiEncoding — a WinAnsi font reads „Ž“ and „ž“.</summary>
    [Fact]
    public void AnnexD2_Footnote2_WinAnsiZcaronAt8EAnd9E()
    {
        var (doc, font) = AnhdD_Open(_ => AnhdD_Standard("Type1", "Helvetica", PdfName.Get("WinAnsiEncoding")));
        using (doc)
        {
            Assert.Equal("Ž", AnhdD_Text(font, 0x8E));
            Assert.Equal("ž", AnhdD_Text(font, 0x9E));
        }
    }

    /// <summary>
    /// D.2 sentences 609T–611T (footnote 3): in WinAnsiEncoding the bullet stands at 95h and at the otherwise unused codes above 40 octal (7Fh, 81h,
    /// 8Dh, 8Fh, 90h, 9Dh) — the table names bullet there, and a WinAnsi font reads each of these codes as „•“.
    /// </summary>
    [Fact]
    public void AnnexD2_Footnote3_WinAnsiUnusedCodesAreBullet()
    {
        int[] codes = [0x95, 0x7F, 0x81, 0x8D, 0x8F, 0x90, 0x9D];
        Assert.All(codes, c => Assert.Equal("bullet", FontTables.WinAnsiEncoding[c]));
        var (doc, font) = AnhdD_Open(_ => AnhdD_Standard("Type1", "Helvetica", PdfName.Get("WinAnsiEncoding")));
        using (doc) Assert.All(codes, c => Assert.Equal("•", AnhdD_Text(font, c)));
    }

    /// <summary>
    /// D.2 sentences 612T–614T (footnote 4): the names guillemotleft and guillemotright, misspelt but in use, stand at ABh/BBh of StandardEncoding
    /// and WinAnsiEncoding and at C7h/C8h of MacRomanEncoding, and the glyph list reads them as „«“ and „»“.
    /// </summary>
    [Fact]
    public void AnnexD2_Footnote4_GuillemotNames()
    {
        Assert.Equal(("guillemotleft", "guillemotright"), (FontTables.StandardEncoding[0xAB], FontTables.StandardEncoding[0xBB]));
        Assert.Equal(("guillemotleft", "guillemotright"), (FontTables.WinAnsiEncoding[0xAB], FontTables.WinAnsiEncoding[0xBB]));
        Assert.Equal(("guillemotleft", "guillemotright"), (FontTables.MacRomanEncoding[0xC7], FontTables.MacRomanEncoding[0xC8]));
        Assert.Equal("«", GlyphNames.ToUnicode("guillemotleft"));
        Assert.Equal("»", GlyphNames.ToUnicode("guillemotright"));
    }

    /// <summary>
    /// D.2 sentences 615T–617T (footnote 5): hyphen stands also at ADh of WinAnsiEncoding — a WinAnsi font reads it as „-“ (U+002D); who means
    /// the soft hyphen names softhyphen with /Differences [173 /softhyphen], and the code reads U+00AD.
    /// </summary>
    [Fact]
    public void AnnexD2_Footnote5_WinAnsiHyphenAtAD_SofthyphenThroughDifferences()
    {
        var (doc, font) = AnhdD_Open(_ => AnhdD_Standard("Type1", "Helvetica", PdfName.Get("WinAnsiEncoding")));
        using (doc) Assert.Equal("-", AnhdD_Text(font, 0xAD));
        var (softDoc, soft) = AnhdD_Open(_ => AnhdD_Standard("Type1", "Helvetica", AnhdD_Differences("WinAnsiEncoding", (173, "softhyphen"))));
        using (softDoc) Assert.Equal("­", AnhdD_Text(soft, 0xAD));
    }

    /// <summary>
    /// D.2 sentences 618T–620T (footnote 6): space stands also at CAh of MacRomanEncoding and at A0h of WinAnsiEncoding — both read as „ “
    /// (U+0020); who means the no-break space names nonbreakingspace with /Differences [160 /nonbreakingspace], and the code reads U+00A0.
    /// </summary>
    [Fact]
    public void AnnexD2_Footnote6_SpaceSecondCodes_NonbreakingspaceThroughDifferences()
    {
        var (winDoc, win) = AnhdD_Open(_ => AnhdD_Standard("Type1", "Helvetica", PdfName.Get("WinAnsiEncoding")));
        using (winDoc) Assert.Equal(" ", AnhdD_Text(win, 0xA0));
        var (macDoc, mac) = AnhdD_Open(_ => AnhdD_Standard("Type1", "Helvetica", PdfName.Get("MacRomanEncoding")));
        using (macDoc) Assert.Equal(" ", AnhdD_Text(mac, 0xCA));
        var (nbspDoc, nbsp) = AnhdD_Open(_ => AnhdD_Standard("Type1", "Helvetica", AnhdD_Differences("WinAnsiEncoding", (160, "nonbreakingspace"))));
        using (nbspDoc) Assert.Equal(" ", AnhdD_Text(nbsp, 0xA0));
    }

    /// <summary>D.2 sentences 1–17: the characters A, Aacute, Acircumflex, Adieresis, AE, Agrave, Aring, Atilde — each name stands at its
    /// codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of
    /// footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name
    /// and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences1To17_AToAtilde_NamesAtTheirCodes() =>
        AnhdD2_Check("A 101/101/101/101; Aacute -/347/301/301; Acircumflex -/345/302/302; Adieresis -/200/304/304; "
            + "AE 341/256/306/306; Agrave -/313/300/300; Aring -/201/305/305; Atilde -/314/303/303");

    /// <summary>D.2 sentences 18–29: the characters B, C, Ccedilla, D, E, Eacute — each name stands at its codes in StandardEncoding,
    /// MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of footnotes 3, 5 and 6), where
    /// the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name and is written back as that
    /// one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences18To29_BToEacute_NamesAtTheirCodes() =>
        AnhdD2_Check("B 102/102/102/102; C 103/103/103/103; Ccedilla -/202/307/307; D 104/104/104/104; E 105/105/105/105; "
            + "Eacute -/203/311/311");

    /// <summary>D.2 sentences 30T–47T: the characters A, Aacute, Acircumflex, Adieresis, AE, Agrave, Aring, Atilde, B, C, Ccedilla, D, E,
    /// Eacute, Oacute, Ocircumflex, Odieresis, OE, Ograve — each name stands at its codes in StandardEncoding, MacRomanEncoding and
    /// WinAnsiEncoding and at no other code of these tables (except the second codes of footnotes 3, 5 and 6), where the table has a dash
    /// no code carries it, and the PDFDocEncoding code reads as the character of the name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences30To47_AToOgrave_NamesAtTheirCodes() =>
        AnhdD2_Check("A 101/101/101/101; Aacute -/347/301/301; Acircumflex -/345/302/302; Adieresis -/200/304/304; "
            + "AE 341/256/306/306; Agrave -/313/300/300; Aring -/201/305/305; Atilde -/314/303/303; B 102/102/102/102; "
            + "C 103/103/103/103; Ccedilla -/202/307/307; D 104/104/104/104; E 105/105/105/105; Eacute -/203/311/311; "
            + "Oacute -/356/323/323; Ocircumflex -/357/324/324; Odieresis -/205/326/326; OE 352/316/214/226; "
            + "Ograve -/361/322/322");

    /// <summary>D.2 sentences 48T–65T: the characters Oslash, Otilde, P, Q, R, S — each name stands at its codes in StandardEncoding,
    /// MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of footnotes 3, 5 and 6), where
    /// the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name and is written back as that
    /// one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences48To65_OslashToS_NamesAtTheirCodes() =>
        AnhdD2_Check("Oslash 351/257/330/330; Otilde -/315/325/325; P 120/120/120/120; Q 121/121/121/121; R 122/122/122/122; "
            + "S 123/123/123/123");

    /// <summary>D.2 sentences 66T–83T: the characters Ecircumflex, Edieresis, Egrave, Eth, Scaron, T, Thorn — each name stands at its codes
    /// in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of footnotes
    /// 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name and is
    /// written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences66To83_EcircumflexToThorn_NamesAtTheirCodes() =>
        AnhdD2_Check("Ecircumflex -/346/312/312; Edieresis -/350/313/313; Egrave -/351/310/310; Eth -/-/320/320; Scaron -/-/212/227; "
            + "T 124/124/124/124; Thorn -/-/336/336");

    /// <summary>D.2 sentences 84T–101T: the characters Euro, F, G, H, I, Iacute, Icircumflex, Idieresis, Igrave — each name stands at its
    /// codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of
    /// footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name
    /// and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences84To101_EuroToIgrave_NamesAtTheirCodes() =>
        AnhdD2_Check("Euro -/-/200/240; F 106/106/106/106; G 107/107/107/107; H 110/110/110/110; I 111/111/111/111; "
            + "Iacute -/352/315/315; Icircumflex -/353/316/316; Idieresis -/354/317/317; Igrave -/355/314/314");

    /// <summary>D.2 sentences 102T–119T: the characters aring, J, K, L, Lslash, M, N, Ntilde, O — each name stands at its codes in
    /// StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of footnotes 3,
    /// 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name and is
    /// written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences102To119_AringToO_NamesAtTheirCodes() =>
        AnhdD2_Check("aring -/214/345/345; J 112/112/112/112; K 113/113/113/113; L 114/114/114/114; Lslash 350/-/-/225; "
            + "M 115/115/115/115; N 116/116/116/116; Ntilde -/204/321/321; O 117/117/117/117");

    /// <summary>D.2 sentences 120T–139T: the characters asciicircum, asciitilde, asterisk, at, atilde, Ecircumflex, Edieresis, Egrave, Eth,
    /// Euro, F, G, H, I — each name stands at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of
    /// these tables (except the second codes of footnotes 3, 5 and 6), where the table has a dash no code carries it, and the
    /// PDFDocEncoding code reads as the character of the name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences120To139_AsciicircumToI_NamesAtTheirCodes() =>
        AnhdD2_Check("asciicircum 136/136/136/136; asciitilde 176/176/176/176; asterisk 052/052/052/052; at 100/100/100/100; "
            + "atilde -/213/343/343; Ecircumflex -/346/312/312; Edieresis -/350/313/313; Egrave -/351/310/310; "
            + "Eth -/-/320/320; Euro -/-/200/240; F 106/106/106/106; G 107/107/107/107; H 110/110/110/110; I 111/111/111/111");

    /// <summary>D.2 sentences 140T–157T: the characters aring, asciicircum, asciitilde, asterisk, at, atilde, Iacute, Icircumflex,
    /// Idieresis, Igrave, J, K, L, Lslash, M, N, Ntilde, O — each name stands at its codes in StandardEncoding, MacRomanEncoding and
    /// WinAnsiEncoding and at no other code of these tables (except the second codes of footnotes 3, 5 and 6), where the table has a dash
    /// no code carries it, and the PDFDocEncoding code reads as the character of the name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences140To157_AringToO_NamesAtTheirCodes() =>
        AnhdD2_Check("aring -/214/345/345; asciicircum 136/136/136/136; asciitilde 176/176/176/176; asterisk 052/052/052/052; "
            + "at 100/100/100/100; atilde -/213/343/343; Iacute -/352/315/315; Icircumflex -/353/316/316; "
            + "Idieresis -/354/317/317; Igrave -/355/314/314; J 112/112/112/112; K 113/113/113/113; L 114/114/114/114; "
            + "Lslash 350/-/-/225; M 115/115/115/115; N 116/116/116/116; Ntilde -/204/321/321; O 117/117/117/117");

    /// <summary>D.2 sentences 158T–174T: the characters U, Uacute, Ucircumflex, Udieresis, Ugrave, V, W, X, Y — each name stands at its
    /// codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of
    /// footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name
    /// and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences158To174_UToY_NamesAtTheirCodes() =>
        AnhdD2_Check("U 125/125/125/125; Uacute -/362/332/332; Ucircumflex -/363/333/333; Udieresis -/206/334/334; "
            + "Ugrave -/364/331/331; V 126/126/126/126; W 127/127/127/127; X 130/130/130/130; Y 131/131/131/131");

    /// <summary>D.2 sentences 175T–192T: the characters a, aacute, acircumflex, acute, Y, Yacute, Ydieresis, Z, Zcaron — each name stands
    /// at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes
    /// of footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the
    /// name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences175To192_AToZcaron_NamesAtTheirCodes() =>
        AnhdD2_Check("a 141/141/141/141; aacute -/207/341/341; acircumflex -/211/342/342; acute 302/253/264/264; Y 131/131/131/131; "
            + "Yacute -/-/335/335; Ydieresis -/331/237/230; Z 132/132/132/132; Zcaron -/-/216/231");

    /// <summary>D.2 sentences 193T–209T: the characters adieresis, ae, agrave, ampersand, eth, exclam, exclamdown, f, fi — each name stands
    /// at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes
    /// of footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the
    /// name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences193To209_AdieresisToFi_NamesAtTheirCodes() =>
        AnhdD2_Check("adieresis -/212/344/344; ae 361/276/346/346; agrave -/210/340/340; ampersand 046/046/046/046; eth -/-/360/360; "
            + "exclam 041/041/041/041; exclamdown 241/301/241/241; f 146/146/146/146; fi 256/336/-/223");

    /// <summary>D.2 sentences 210T–227T: the characters a, aacute, acircumflex, acute, adieresis, ae, agrave, ampersand, b, backslash, bar,
    /// braceleft, braceright, bracketleft, bracketright, eth, exclam, exclamdown, f, fi, five, U, Uacute, Ucircumflex, Udieresis, Ugrave,
    /// V, W, X, Y, Yacute, Ydieresis, Z, Zcaron — each name stands at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding
    /// and at no other code of these tables (except the second codes of footnotes 3, 5 and 6), where the table has a dash no code carries
    /// it, and the PDFDocEncoding code reads as the character of the name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences210To227_AToZcaron_NamesAtTheirCodes() =>
        AnhdD2_Check("a 141/141/141/141; aacute -/207/341/341; acircumflex -/211/342/342; acute 302/253/264/264; "
            + "adieresis -/212/344/344; ae 361/276/346/346; agrave -/210/340/340; ampersand 046/046/046/046; "
            + "b 142/142/142/142; backslash 134/134/134/134; bar 174/174/174/174; braceleft 173/173/173/173; "
            + "braceright 175/175/175/175; bracketleft 133/133/133/133; bracketright 135/135/135/135; eth -/-/360/360; "
            + "exclam 041/041/041/041; exclamdown 241/301/241/241; f 146/146/146/146; fi 256/336/-/223; five 065/065/065/065; "
            + "U 125/125/125/125; Uacute -/362/332/332; Ucircumflex -/363/333/333; Udieresis -/206/334/334; "
            + "Ugrave -/364/331/331; V 126/126/126/126; W 127/127/127/127; X 130/130/130/130; Y 131/131/131/131; "
            + "Yacute -/-/335/335; Ydieresis -/331/237/230; Z 132/132/132/132; Zcaron -/-/216/231");

    /// <summary>D.2 sentences 228T–245T: the characters breve, brokenbar, bullet, caron, ccedilla, cedilla, cent, circumflex, colon — each
    /// name stands at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the
    /// second codes of footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the
    /// character of the name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences228To245_BreveToColon_NamesAtTheirCodes() =>
        AnhdD2_Check("breve 306/371/-/030; brokenbar -/-/246/246; bullet 267/245/225/200; caron 317/377/-/031; "
            + "ccedilla -/215/347/347; cedilla 313/374/270/270; cent 242/242/242/242; circumflex 303/366/210/032; "
            + "colon 072/072/072/072");

    /// <summary>D.2 sentences 246T–263T: the characters comma, copyright, currency, d, dagger, daggerdbl, degree, dieresis, divide — each
    /// name stands at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the
    /// second codes of footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the
    /// character of the name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences246To263_CommaToDivide_NamesAtTheirCodes() =>
        AnhdD2_Check("comma 054/054/054/054; copyright -/251/251/251; currency 250/333/244/244; d 144/144/144/144; "
            + "dagger 262/240/206/201; daggerdbl 263/340/207/202; degree -/241/260/260; dieresis 310/254/250/250; "
            + "divide -/326/367/367");

    /// <summary>D.2 sentences 264T–283T: the characters b, backslash, bar, braceleft, braceright, bracketleft, bracketright, breve,
    /// brokenbar, bullet, c, caron, ccedilla, cedilla, cent, circumflex, colon, dollar — each name stands at its codes in StandardEncoding,
    /// MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of footnotes 3, 5 and 6), where
    /// the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name and is written back as that
    /// one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences264To283_BToDollar_NamesAtTheirCodes() =>
        AnhdD2_Check("b 142/142/142/142; backslash 134/134/134/134; bar 174/174/174/174; braceleft 173/173/173/173; "
            + "braceright 175/175/175/175; bracketleft 133/133/133/133; bracketright 135/135/135/135; breve 306/371/-/030; "
            + "brokenbar -/-/246/246; bullet 267/245/225/200; c 143/143/143/143; caron 317/377/-/031; ccedilla -/215/347/347; "
            + "cedilla 313/374/270/270; cent 242/242/242/242; circumflex 303/366/210/032; colon 072/072/072/072; "
            + "dollar 044/044/044/044");

    /// <summary>D.2 sentences 284T–303T: the characters comma, copyright, currency, d, dagger, daggerdbl, degree, dieresis, divide, dollar,
    /// fl, florin, four — each name stands at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of
    /// these tables (except the second codes of footnotes 3, 5 and 6), where the table has a dash no code carries it, and the
    /// PDFDocEncoding code reads as the character of the name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences284To303_CommaToFour_NamesAtTheirCodes() =>
        AnhdD2_Check("comma 054/054/054/054; copyright -/251/251/251; currency 250/333/244/244; d 144/144/144/144; "
            + "dagger 262/240/206/201; daggerdbl 263/340/207/202; degree -/241/260/260; dieresis 310/254/250/250; "
            + "divide -/326/367/367; dollar 044/044/044/044; fl 257/337/-/224; florin 246/304/203/206; four 064/064/064/064");

    /// <summary>D.2 sentences 304T–321T: the characters fraction, g, germandbls, grave, greater, guillemotleft, guillemotright — each name
    /// stands at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the
    /// second codes of footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the
    /// character of the name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences304To321_FractionToGuillemotright_NamesAtTheirCodes() =>
        AnhdD2_Check("fraction 244/332/-/207; g 147/147/147/147; germandbls 373/247/337/337; grave 301/140/140/140; "
            + "greater 076/076/076/076; guillemotleft 253/307/253/253; guillemotright 273/310/273/273");

    /// <summary>D.2 sentences 322T–339T: the characters guilsinglleft, guilsinglright, h, hungarumlaut, hyphen, i, iacute, icircumflex —
    /// each name stands at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except
    /// the second codes of footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the
    /// character of the name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences322To339_GuilsinglleftToIcircumflex_NamesAtTheirCodes() =>
        AnhdD2_Check("guilsinglleft 254/334/213/210; guilsinglright 255/335/233/211; h 150/150/150/150; hungarumlaut 315/375/-/034; "
            + "hyphen 055/055/055/055; i 151/151/151/151; iacute -/222/355/355; icircumflex -/224/356/356");

    /// <summary>D.2 sentences 340T–358T: the characters idieresis, igrave, j, k, l, less, logicalnot — each name stands at its codes in
    /// StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of footnotes 3,
    /// 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name and is
    /// written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences340To358_IdieresisToLogicalnot_NamesAtTheirCodes() =>
        AnhdD2_Check("idieresis -/225/357/357; igrave -/223/354/354; j 152/152/152/152; k 153/153/153/153; l 154/154/154/154; "
            + "less 074/074/074/074; logicalnot -/302/254/254");

    /// <summary>D.2 sentences 359T–375T: the characters dotaccent, dotlessi, e, lslash, m — each name stands at its codes in
    /// StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of footnotes 3,
    /// 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name and is
    /// written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences359To375_DotaccentToM_NamesAtTheirCodes() =>
        AnhdD2_Check("dotaccent 307/372/-/033; dotlessi 365/365/-/232; e 145/145/145/145; lslash 370/-/-/233; m 155/155/155/155");

    /// <summary>D.2 sentences 376T–394T: the characters eacute, ecircumflex, edieresis, egrave, eight, ellipsis, emdash — each name stands
    /// at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes
    /// of footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the
    /// name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences376To394_EacuteToEmdash_NamesAtTheirCodes() =>
        AnhdD2_Check("eacute -/216/351/351; ecircumflex -/220/352/352; edieresis -/221/353/353; egrave -/217/350/350; "
            + "eight 070/070/070/070; ellipsis 274/311/205/203; emdash 320/321/227/204");

    /// <summary>D.2 sentences 395T–411T: the characters endash, equal, oe, ogonek, ograve, one — each name stands at its codes in
    /// StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of footnotes 3,
    /// 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name and is
    /// written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences395To411_EndashToOne_NamesAtTheirCodes() =>
        AnhdD2_Check("endash 261/320/226/205; equal 075/075/075/075; oe 372/317/234/234; ogonek 316/376/-/035; ograve -/230/362/362; "
            + "one 061/061/061/061");

    /// <summary>D.2 sentences 412T–428T: the characters onehalf, onequarter, onesuperior, ordfeminine, ordmasculine, oslash, otilde — each
    /// name stands at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the
    /// second codes of footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the
    /// character of the name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences412To428_OnehalfToOtilde_NamesAtTheirCodes() =>
        AnhdD2_Check("onehalf -/-/275/275; onequarter -/-/274/274; onesuperior -/-/271/271; ordfeminine 343/273/252/252; "
            + "ordmasculine 353/274/272/272; oslash 371/277/370/370; otilde -/233/365/365");

    /// <summary>D.2 sentences 429T–445T: the characters macron, p, paragraph, parenleft, parenright — each name stands at its codes in
    /// StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of footnotes 3,
    /// 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name and is
    /// written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences429To445_MacronToParenright_NamesAtTheirCodes() =>
        AnhdD2_Check("macron 305/370/257/257; p 160/160/160/160; paragraph 266/246/266/266; parenleft 050/050/050/050; "
            + "parenright 051/051/051/051");

    /// <summary>D.2 sentences 446T–462T: the characters minus, mu, multiply, n, nine, ntilde — each name stands at its codes in
    /// StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of footnotes 3,
    /// 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name and is
    /// written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences446To462_MinusToNtilde_NamesAtTheirCodes() =>
        AnhdD2_Check("minus -/-/-/212; mu -/265/265/265; multiply -/-/327/327; n 156/156/156/156; nine 071/071/071/071; "
            + "ntilde -/226/361/361");

    /// <summary>D.2 sentences 463T–481T: the characters numbersign, o, oacute, ocircumflex, odieresis, s, scaron — each name stands at its
    /// codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of
    /// footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name
    /// and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences463To481_NumbersignToScaron_NamesAtTheirCodes() =>
        AnhdD2_Check("numbersign 043/043/043/043; o 157/157/157/157; oacute -/227/363/363; ocircumflex -/231/364/364; "
            + "odieresis -/232/366/366; s 163/163/163/163; scaron -/-/232/235");

    /// <summary>D.2 sentences 482T–499T: the characters section, semicolon, seven, six, slash, space, sterling — each name stands at its
    /// codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of
    /// footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name
    /// and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences482To499_SectionToSterling_NamesAtTheirCodes() =>
        AnhdD2_Check("section 247/244/247/247; semicolon 073/073/073/073; seven 067/067/067/067; six 066/066/066/066; "
            + "slash 057/057/057/057; space 040/040/040/040; sterling 243/243/243/243");

    /// <summary>D.2 sentences 500T–516T: the characters t, thorn, three, threequarters, threesuperior, tilde — each name stands at its
    /// codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of
    /// footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name
    /// and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences500To516_TToTilde_NamesAtTheirCodes() =>
        AnhdD2_Check("t 164/164/164/164; thorn -/-/376/376; three 063/063/063/063; threequarters -/-/276/276; "
            + "threesuperior -/-/263/263; tilde 304/367/230/037");

    /// <summary>D.2 sentences 517T–534T: the characters percent, period, periodcentered, perthousand, plus, plusminus, q — each name stands
    /// at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes
    /// of footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the
    /// name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences517To534_PercentToQ_NamesAtTheirCodes() =>
        AnhdD2_Check("percent 045/045/045/045; period 056/056/056/056; periodcentered 264/341/267/267; perthousand 275/344/211/213; "
            + "plus 053/053/053/053; plusminus -/261/261/261; q 161/161/161/161");

    /// <summary>D.2 sentences 535T–551T: the characters question, questiondown, quotedbl, quotedblbase, quotedblleft, quotedblright,
    /// quoteleft — each name stands at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these
    /// tables (except the second codes of footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code
    /// reads as the character of the name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences535To551_QuestionToQuoteleft_NamesAtTheirCodes() =>
        AnhdD2_Check("question 077/077/077/077; questiondown 277/300/277/277; quotedbl 042/042/042/042; quotedblbase 271/343/204/214; "
            + "quotedblleft 252/322/223/215; quotedblright 272/323/224/216; quoteleft 140/324/221/217");

    /// <summary>D.2 sentences 552T–568T: the characters quoteright, quotesinglbase, quotesingle, r, registered, ring — each name stands at
    /// its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables (except the second codes of
    /// footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads as the character of the name
    /// and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences552To568_QuoterightToRing_NamesAtTheirCodes() =>
        AnhdD2_Check("quoteright 047/325/222/220; quotesinglbase 270/342/202/221; quotesingle 251/047/047/047; r 162/162/162/162; "
            + "registered -/250/256/256; ring 312/373/-/036");

    /// <summary>D.2 sentences 569T–585T: the characters trademark, two, twosuperior, u, uacute, ucircumflex, udieresis, ugrave, underscore
    /// — each name stands at its codes in StandardEncoding, MacRomanEncoding and WinAnsiEncoding and at no other code of these tables
    /// (except the second codes of footnotes 3, 5 and 6), where the table has a dash no code carries it, and the PDFDocEncoding code reads
    /// as the character of the name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences569To585_TrademarkToUnderscore_NamesAtTheirCodes() =>
        AnhdD2_Check("trademark -/252/231/222; two 062/062/062/062; twosuperior -/-/262/262; u 165/165/165/165; uacute -/234/372/372; "
            + "ucircumflex -/236/373/373; udieresis -/237/374/374; ugrave -/235/371/371; underscore 137/137/137/137");

    /// <summary>D.2 sentences 586T–601T: the characters trademark, two, twosuperior, u, uacute, ucircumflex, udieresis, ugrave, underscore,
    /// v, w, x, y, yacute, ydieresis, yen, z, zcaron, zero — each name stands at its codes in StandardEncoding, MacRomanEncoding and
    /// WinAnsiEncoding and at no other code of these tables (except the second codes of footnotes 3, 5 and 6), where the table has a dash
    /// no code carries it, and the PDFDocEncoding code reads as the character of the name and is written back as that one byte.</summary>
    [Fact]
    public void AnnexD2_Sentences586To601_TrademarkToZero_NamesAtTheirCodes() =>
        AnhdD2_Check("trademark -/252/231/222; two 062/062/062/062; twosuperior -/-/262/262; u 165/165/165/165; uacute -/234/372/372; "
            + "ucircumflex -/236/373/373; udieresis -/237/374/374; ugrave -/235/371/371; underscore 137/137/137/137; "
            + "v 166/166/166/166; w 167/167/167/167; x 170/170/170/170; y 171/171/171/171; yacute -/-/375/375; "
            + "ydieresis -/330/377/377; yen 245/264/245/245; z 172/172/172/172; zcaron -/-/236/236; zero 060/060/060/060");

}
