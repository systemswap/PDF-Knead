using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Pdf;

namespace PdfKnead.Tests.Fonts;

/// <summary>
/// Font cases from the comparison with three reference readers: files of the test collection whose text was missing
/// or showed wrong characters in PDF Knead. Each case checks the mechanism (which glyph is used for which code).
/// See docs/FEHLERGEDAECHTNIS.md, No. 63.
/// </summary>
[Collection(CjkFontsCollection.Name)]
public sealed class ReferenceFontTests
{
    [TestPdfsFact]
    public void BrokenHead_FallsBackToSubstitute()
    {
        // bug1050040: the stream lacks one byte, so unitsPerEm reads as 0; taken at face value, the glyphs become 64 times too large and nothing is visible
        using var doc = Open("bug1050040.pdf");
        var font = PageFont(doc, "F1");
        var glyph = font.Glyph('B', 1);
        Assert.False(glyph.Outline.IsEmpty);
        var height = glyph.Outline.Bounds.Height * glyph.ToText.D;
        Assert.InRange(height, 0.3, 1.2);   // in em, not 64 times too large
    }

    [TestPdfsFact]
    public void Type1AsCidFont_UsesCharStringOrder()
    {
        // issue11740: a Type 1 font program under CIDFontType0; the CIDs follow the CharStrings order
        using var doc = Open("issue11740_reduced.pdf");
        var font = PageFont(doc, "F1");
        for (uint cid = 1; cid <= 8; cid++) Assert.False(font.Glyph(cid, 2).Outline.IsEmpty);
    }

    [TestPdfsFact]
    public void IndexToLocFormat_NonZeroIsLong()
    {
        // issue2537r: indexToLocFormat 256 is read as long, as common font engines do; reading it as short gives boxes
        using var doc = Open("issue2537r.pdf");
        var font = PageFont(doc, "F1");
        Assert.False(font.Glyph(0x2F, 2).Outline.IsEmpty);   // „L“
        Assert.NotEqual(font.Glyph(0x2F, 2).Outline.Bounds, font.Glyph(0x31, 2).Outline.Bounds);
    }

    [TestPdfsFact]
    public void CffUnderCidFontType2_FollowsCidToGidMap()
    {
        // bug1186827: according to the CIDToGIDMap, CID 0x20 is the space; glyph 0x20 of the CFF itself is a letter
        using var doc = Open("bug1186827.pdf");
        var font = PageFont(doc, "F1");
        Assert.True(font.Glyph(0x20, 2).Outline.IsEmpty);
        Assert.False(font.Glyph(0x54, 2).Outline.IsEmpty);
    }

    [TestPdfsFact]
    public void NameKeyedCffUnderCidFontType0_FollowsCidToGidMap()
    {
        // issue7901: a name-keyed OpenType CFF under CIDFontType0 with a CIDToGIDMap. Two of the reference readers follow the map; taking the CID
        // as the glyph number instead produces garbled text
        using var doc = Open("issue7901.pdf");
        var cff = OpenTypeFont.Parse(doc.DecodeStream((PdfStream)doc.GetObject(1)).Data).Cff!;
        var map = doc.DecodeStream((PdfStream)doc.GetObject(4)).Data;
        var font = PageFont(doc, "F1");
        var checkedOnes = 0;
        for (var cid = 1; 2 * cid + 1 < map.Length; cid++)
        {
            var gid = map[2 * cid] << 8 | map[2 * cid + 1];
            if (gid == cid || gid == 0 || cff.Outline(gid).IsEmpty) continue;
            Assert.Equal(cff.Outline(gid).Bounds, font.Glyph((uint)cid, 2).Outline.Bounds);
            checkedOnes++;
        }
        Assert.True(checkedOnes > 10, $"nur {checkedOnes} Zeichen mit eigener Zuordnung");
    }

    [TestPdfsFact]
    public void CidKeyedCffUnderCidFontType2_MapGivesCids()
    {
        // issue15559 (SourceHanSans as CFF in FontFile2): if the CFF is CID-keyed, the CIDToGIDMap yields a CID that only the charset
        // of the CFF turns into a glyph (as in common font engines). Treating that value as a glyph number shows wrong characters or none instead
        // of the Chinese text. Font taken from bug1146106: CIDs 60, 62 and 310 are glyphs 1, 2 and 3
        using var source = Open("bug1146106.pdf");
        var cffData = source.DecodeStream((PdfStream)source.GetObject(3)).Data;
        var cff = CffFont.Parse(cffData);
        byte[] map = [0, 0, 0, 60, 0, 62, 1, 54];   // Code 1 → CID 60, 2 → 62, 3 → 310
        var (doc, _) = InterpreterTests.Page("", pdf => TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Dict(("Type", TestPdf.N("Font")),
            ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("ABCDEF+Test")), ("Encoding", TestPdf.N("Identity-H")),
            ("DescendantFonts", new PdfArray([TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")),
                ("BaseFont", TestPdf.N("ABCDEF+Test")), ("CIDToGIDMap", TestPdf.Ref(pdf.Add(TestPdf.FlateStream(map)))),
                ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ABCDEF+Test")), ("Flags", PdfInteger.Of(4)),
                    ("FontFile2", TestPdf.Ref(pdf.Add(TestPdf.FlateStream(cffData)))))))]))))))));
        using (doc)
        {
            var font = PageFont(doc, "F1");
            for (uint code = 1; code <= 3; code++)
            {
                var glyph = font.Glyph(code, 2);
                Assert.False(glyph.Outline.IsEmpty, $"Code {code}");
                Assert.Equal(cff.Outline((int)code).Bounds, glyph.Outline.Bounds);
            }
        }
    }

    [TestPdfsFact]
    public void CidKeyedCff_UsesTheFontMatrixOfItsFontDict()
    {
        // text_clip_cff_cid: the FontMatrix is in the Font DICT of the sub-font. Using only the one in the Top DICT makes „ABC123“
        // too large, so it extends beyond the clip
        using var doc = Open("text_clip_cff_cid.pdf");
        var glyph = PageFont(doc, "F2").Glyph(0x0025, 2);   // „B“
        Assert.False(glyph.Outline.IsEmpty);
        Assert.InRange(glyph.Outline.Bounds.Height * Math.Abs(glyph.ToText.D), 0.5, 0.8);   // cap height in em
    }

    [TestPdfsFact]
    public void SymbolicTrueType_CodeMappedToGlyphZero_IsDrawn()
    {
        // issue8234: the (3,0) cmap maps code 32 to glyph 0, which carries the „ä“ here; the code has a width.
        // The reference readers draw glyph 0, so it must not be treated as "not found" and skipped
        using var doc = Open("issue8234.pdf");
        var font = PageFont(doc, "ttf2");
        Assert.False(font.Glyph(32, 1).Outline.IsEmpty);
        Assert.NotEqual(font.Glyph(32, 1).Outline.Bounds, font.Glyph(33, 1).Outline.Bounds);   // „ä“ is not „ö“
    }

    [TestPdfsFact]
    public void SymbolicTrueType_OnlyUnicodePlatformCmap_UsesTheRawCode()
    {
        // issue5501: the only cmap is (0,0) and the codes are shifted („;LEXW“ = „What“). Querying only (3,0), (1,0) and (3,1) finds
        // nothing, which shows as boxes because glyph 0 is drawn (see the previous test). As in common font engines, every cmap present is tried
        // with the raw code
        using var doc = Open("issue5501.pdf");
        var font = PageFont(doc, "F1");
        var w = font.Glyph(';', 1);
        Assert.False(w.Outline.IsEmpty);
        Assert.NotEqual(w.Outline.Bounds, font.Glyph('L', 1).Outline.Bounds);
    }

    [TestPdfsFact]
    public void CompositeGlyphContainingItself_IsEmpty()
    {
        // recursiveCompositGlyf: composite glyphs that contain themselves. Common font engines discard them entirely; drawing whatever accumulates
        // up to the depth limit produces red strokes through the text clip
        using var doc = Open("recursiveCompositGlyf.pdf");
        var font = PageFont(doc, "F1");
        foreach (var c in "hello world") Assert.True(font.Glyph(c, 1).Outline.IsEmpty, $"„{c}“");
    }

    [TestPdfsFact]
    public void EncodingCMapInBfSyntax_GivesCids()
    {
        // bug920426: an encoding CMap written with bfchar instead of cidchar; ignoring it gives CID 0 (boxes) for everything
        using var doc = Open("bug920426.pdf");
        var font = PageFont(doc, "F1");
        Assert.True(font.Glyph(0x20, 2).Outline.IsEmpty);    // space
        Assert.False(font.Glyph(0x43, 2).Outline.IsEmpty);   // „C“
        Assert.NotEqual(font.Glyph(0x43, 2).Outline.Bounds, font.Glyph(0x68, 2).Outline.Bounds);
    }

    [TestPdfsFact]
    public void UnsortedLoca_KeepsTheGlyphs()
    {
        // issue11131: loca is not in ascending order; this must not drop every second glyph
        using var doc = Open("issue11131_reduced.pdf");
        var font = PageFont(doc, "C2_0");
        foreach (var code in (uint[])[0x0D, 0x07, 0x1C, 0x1D, 0x08, 0x1A, 0x2D, 0x09, 0x2F])
            Assert.False(font.Glyph(code, 2).Outline.IsEmpty);
    }

    [TestPdfsFact]
    public void SymbolicTrueType_UsesTheCodeBeforeDifferenceNames()
    {
        // canvas: /Differences names code 6 „space“, and the (1,0) cmap maps raw codes; looking up the name instead draws a „|“
        using var doc = Open("canvas.pdf");
        var font = PageFont(doc, "R10", page: 1);
        var space = Array.IndexOf(Differences(doc, font: "R10", page: 1), "space");
        Assert.True(space > 0);
        Assert.True(font.Glyph((uint)space, 1).Outline.IsEmpty);
    }

    [TestPdfsFact]
    public void Type1WithTwoSubrsArrays_KeepsTheFirst()
    {
        // issue18548: a second /Subrs array with only empty subroutines; using it makes the bar disappear
        using var doc = Open("issue18548_reduced.pdf");
        Assert.False(PageFont(doc, "F1").Glyph('A', 1).Outline.IsEmpty);
    }

    [TestPdfsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonEmbeddedCidTrueType_GlyphNumbersBecomeText(bool withoutSystemFonts)
    {
        // issue11242: Verdana not embedded, Identity-H, no ToUnicode; the codes are glyph numbers of the Windows font.
        // With Verdana installed they are used directly, otherwise they are resolved through the known glyph order (then Liberation)
        SystemFonts.DisabledForTests = withoutSystemFonts;
        try
        {
            using var doc = Open("issue11242_reduced.pdf");
            var font = PageFont(doc, "F1");
            var v = font.Glyph(57, 2);       // glyph 57 = „V“ in the glyph order of the Microsoft fonts
            Assert.False(v.Outline.IsEmpty);
            Assert.Equal("V", v.Text);
            Assert.True(font.Glyph(3, 2).Outline.IsEmpty);   // glyph 3 = space
        }
        finally
        {
            SystemFonts.DisabledForTests = false;
        }
    }

    [Fact]
    public void SystemFonts_StayFindable_AfterManyOthers()
    {
        // The system font cache uses a byte budget with eviction. A fixed limit of 16 loaded system fonts made every further font
        // unfindable, so the rendering depended on what had been opened before
        string[] names = ["Arial", "TimesNewRoman", "CourierNew", "Verdana", "Tahoma", "Georgia", "Calibri", "Cambria", "Candara", "Consolas",
            "Constantia", "Corbel", "SegoeUI", "TrebuchetMS", "ComicSansMS", "Impact", "LucidaConsole", "PalatinoLinotype", "SitkaText", "Ebrima"];
        var found = names.Where(SystemFonts.IsInstalled).ToList();   // installed, checked without loading
        if (found.Count <= 16) return;   // cannot be checked without enough Windows fonts (Linux)
        foreach (var name in found) Assert.NotNull(SystemFonts.Find(name, false, false));
        Assert.NotNull(SystemFonts.Find(found[0], true, false));
    }

    [Fact]
    public void SystemFontKeys_MatchPdfNames()
    {
        Assert.Equal("timesnewromanbold", SystemFonts.Key("TimesNewRomanPS-BoldMT"));
        Assert.Equal("arial", SystemFonts.Key("ABCDEF+ArialMT"));
        Assert.Equal("arialbolditalic", SystemFonts.Key("Arial,BoldItalic"));
        Assert.Equal("timesnewroman", SystemFonts.FamilyKey("Times New Roman,Bold"));
        Assert.Equal("calibri", SystemFonts.FamilyKey("Calibri-Bold"));
        Assert.NotNull(GlyphOrder.For("Calibri"));
        Assert.NotNull(GlyphOrder.For("TimesNewRomanPS-BoldMT"));
    }

    [Fact]
    public void PredefinedCMaps_MapCodesToCids_AndCidsToText()
    {
        // 90ms-RKSJ-H: one byte „(“ → half-width CID 239 (231 + 8), two bytes 0x9776 → „要“; a predefined CMap name must not be treated as Identity
        var rksj = PredefinedCMaps.Get("90ms-RKSJ-H")!;
        Assert.Equal(1, rksj.ReadCode([0x28], 0, out var paren));
        Assert.Equal(239, rksj.ToCid(paren, 1));
        Assert.Equal(2, rksj.ReadCode([0x97, 0x76], 0, out var kanji));
        Assert.Equal("要", PredefinedCMaps.Text("Japan1", rksj.ToCid(kanji, 2)));
        Assert.Equal("(", PredefinedCMaps.Text("Japan1", 239));
        // UniGB-UCS2-H (often used with an embedded subset): „中“ has its own CID in GB1, not 0x4E2D
        var gb = PredefinedCMaps.Get("UniGB-UCS2-H")!;
        var cid = gb.ToCid(0x4E2D, 2);
        Assert.NotEqual(0x4E2D, cid);
        Assert.Equal("中", PredefinedCMaps.Text("GB1", cid));
        // 90ms-RKSJ-V includes 90ms-RKSJ-H (usecmap) and overrides the codes of the vertical forms: „ー“ (0x815B) is the vertical CID 7891,
        // not the horizontal 660 of the included CMap (ISO 32000-2, 9.7.5.3; docs/FEHLERGEDAECHTNIS.md, No. 513)
        Assert.Equal(660, rksj.ToCid(0x815B, 2));
        Assert.Equal(7891, PredefinedCMaps.Get("90ms-RKSJ-V")!.ToCid(0x815B, 2));
        Assert.Equal(rksj.ToCid(kanji, 2), PredefinedCMaps.Get("90ms-RKSJ-V")!.ToCid(kanji, 2));   // (the rest as included)
        Assert.True(PredefinedCMaps.Get("Identity-V")!.WMode == 1);
        Assert.Null(PredefinedCMaps.Get("GibtEsNicht-H"));
    }

    [TestPdfsFact]
    public void NonEmbeddedCjk_UsesNotoOrReportsTheMissingFolder()
    {
        // issue13343: Ryumin-Light with 90ms-RKSJ-H, not embedded. Without the Schriften-CJK folder the document reports the missing fonts
        var empty = Directory.CreateTempSubdirectory("pdfknead-cjk-").FullName;
        try
        {
            CjkFonts.FolderForTests = empty;
            if (Environment.GetEnvironmentVariable("PDFKNEAD_CJK") is null && !Directory.Exists(Path.Combine(AppContext.BaseDirectory, CjkFonts.FolderName)))
            {
                using var doc = Open("issue13343.pdf");
                PageFont(doc, "C0_1");
                Assert.True(doc.MissingCjkFonts);
            }
            // With the fonts (in the cache, fetched by tools/schriften/cjk-holen.ps1 or .sh): glyph from Noto, text from Adobe-Japan1-UCS2
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pdf-knead-build", "cjk", CjkFonts.FolderName);
            if (!File.Exists(Path.Combine(cache, "NotoSansCJK-Regular.ttc"))) return;
            CjkFonts.FolderForTests = cache;
            using var withFonts = Open("issue13343.pdf");
            var glyph = PageFont(withFonts, "C0_1").Glyph(0x9776, 2);
            Assert.False(glyph.Outline.IsEmpty);
            Assert.Equal("要", glyph.Text);
            Assert.False(withFonts.MissingCjkFonts);
        }
        finally
        {
            CjkFonts.FolderForTests = null;
            Directory.Delete(empty);
        }
    }

    private static PdfDocument Open(string name) => PdfDocument.Open(File.ReadAllBytes(Path.Combine(Root(), "TestPDFs", "pdfjs", name)));

    private static PdfDictionary FontDict(PdfDocument doc, string name, int page) =>
        doc.Pages[page].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get(name)) as PdfDictionary
        ?? throw new InvalidOperationException(name);

    private static PdfFont PageFont(PdfDocument doc, string name, int page = 0) => PdfFont.Get(FontDict(doc, name, page), doc);

    private static string?[] Differences(PdfDocument doc, string font, int page)
    {
        var names = new string?[256];
        var code = 0;
        var encoding = FontDict(doc, font, page).Get(PdfName.Get("Encoding")) as PdfDictionary;
        foreach (var item in encoding!.GetArray(PdfName.Get("Differences"))!)
            if (item.AsNumber() is { } n) code = (int)n;
            else if (item is PdfName glyph) names[code++] = glyph.Value;
        return names;
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
}
