using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package E25: three findings left from E21 and E24, decided after measuring against the three reference readers and the shaping library
/// (docs/FEHLERGEDAECHTNIS.md, No. 1202–1205): the installed font of a PostScript name with a style after a comma, the substitute family by the
/// name before the flags, the language systems of a script without a default one, and the text of a CID that selects no glyph.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ No. 1202: a style after a comma (ISO 32000-2, 9.6.3, 9.7.4.1)

    /// <summary>
    /// ISO 32000-2, 9.6.3 with 9.7.4.1 (/BaseFont of a CIDFontType2 as of a TrueType font, a style such as „,Italic“ after the name): the key of
    /// a name with a style is the key of the name before the comma with the style — „Arial-ItalicMT,Italic“ is the key of Arial Italic, its
    /// family Arial (also for the glyph order). Before, the whole name was one key, „arialitalicmtitalic“, and no installed font was found.
    /// </summary>
    [Fact]
    public void E25_NameWithStyleAfterAComma_KeyOfTheNameBeforeIt()
    {
        Assert.Equal("arialitalic", SystemFonts.Key("Arial-ItalicMT,Italic"));
        Assert.Equal("arialitalic", SystemFonts.Key("Arial,Italic"));
        Assert.Equal("arialbold", SystemFonts.Key("Arial-BoldMT,Bold"));
        Assert.Equal("arialbold", SystemFonts.Key("ArialMT,Bold"));
        Assert.Equal("arialbolditalic", SystemFonts.Key("Arial-BoldItalicMT,BoldItalic"));
        Assert.Equal("arialbolditalic", SystemFonts.Key("Arial-ItalicMT,Bold"));
        Assert.Equal("arialbolditalic", SystemFonts.Key("ABCDEF+Arial-BoldMT,Italic"));
        Assert.Equal("timesnewromanitalic", SystemFonts.Key("TimesNewRomanPS-ItalicMT,Italic"));
        Assert.Equal("couriernewbold", SystemFonts.Key("CourierNewPS-BoldMT,Bold"));
        Assert.Equal("arial", SystemFonts.Key("ArialMT,Regular"));
        Assert.Equal("arial", SystemFonts.FamilyKey("Arial-ItalicMT,Italic"));
        Assert.Equal("timesnewroman", SystemFonts.FamilyKey("TimesNewRomanPS-BoldMT,Bold"));
        Assert.NotNull(GlyphOrder.For("Arial-ItalicMT,Italic"));
    }

    /// <summary>
    /// A composite font that is not embedded (CIDFontType2, Identity-H, /ToUnicode /Identity-H as a name, CIDs = Unicode) with the given name and
    /// descriptor flags, as the real file issue11915 has it.
    /// </summary>
    private static PdfDictionary E25CidFont(string name, int flags, int angle, bool toUnicode = true, string registry = "Adobe", string ordering = "Identity")
    {
        var cid = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N(name)),
            ("CIDSystemInfo", Iso03Collection(registry, ordering, 0)), ("DW", PdfInteger.Of(600)),
            ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N(name)), ("Flags", PdfInteger.Of(flags)),
                ("ItalicAngle", PdfInteger.Of(angle)))));
        var font = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N(name)),
            ("Encoding", TestPdf.N("Identity-H")), ("DescendantFonts", new PdfArray([cid])));
        if (toUnicode) font.Set(PdfName.Get("ToUnicode"), TestPdf.N("Identity-H"));
        return font;
    }

    /// <summary>
    /// 9.6.3 and 9.7.4.1 with the installed fonts (real file issue11915, line „Arial Italic“): a non-embedded composite font „Arial-ItalicMT,Italic“
    /// with /Flags 97 (FixedPitch by mistake) draws the glyphs of the installed Arial Italic. Before, the name found no installed font, and the
    /// substitute chosen by the flags was the monospaced one — narrow glyphs with gaps.
    /// </summary>
    [Fact]
    public void E25_NonEmbeddedCidFont_PostScriptNameWithStyle_DrawsTheInstalledFace()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "ariali.ttf");
        if (!OperatingSystem.IsWindows() || !File.Exists(file)) return;
        var italic = OpenTypeFont.Parse(File.ReadAllBytes(file));
        var (doc, _, font) = Iso03PageFont("", _ => E25CidFont("Arial-ItalicMT,Italic", 97, -12));
        using (doc)
            foreach (var c in "Aai")
                Assert.True(Iso03SameOutline(italic.Outline(italic.GlyphForUnicode(c)), font.Glyph(c, 2).Outline), $"{c}: nicht Arial Italic");
    }

    // ------------------------------------------------------------ No. 1203: the name of a standard family before the flags (9.8.2)

    /// <summary>
    /// ISO 32000-2, 9.8.1 sentence 2 and 9.8.2 (the flags help to choose a substitute) with what the reference readers do: a name of the standard
    /// families decides the family of the substitute before FixedPitch and Serif — Arial and Helvetica sans serif, Times serif, Courier
    /// monospaced, whatever the flags say (all three readers so, probes B1b, B3b, D1–D11; real file issue11915). A name without a standard
    /// family keeps the flags first, also one that only begins with one (D12, D13 and „ArialUnicodeMS“ in the real file issue20489: two of three
    /// readers). Before, FixedPitch made every font monospaced and Serif every font but one with „sans“ in its name serif.
    /// </summary>
    [Theory]
    [InlineData("Arial-ItalicMT,Italic", 97, -12, "LiberationSans-Italic.ttf")]
    [InlineData("Arial,Italic", 97, -12, "LiberationSans-Italic.ttf")]
    [InlineData("Arial", 33, 0, "LiberationSans-Regular.ttf")]
    [InlineData("Arial", 34, 0, "LiberationSans-Regular.ttf")]
    [InlineData("ABCDEF+ArialMT", 42, 0, "LiberationSans-Regular.ttf")]
    [InlineData("ArialUnicodeMS", 6, 0, "LiberationSerif-Regular.ttf")]
    [InlineData("Helvetica", 33, 0, "LiberationSans-Regular.ttf")]
    [InlineData("Helvetica-BoldOblique", 35, -12, "LiberationSans-BoldItalic.ttf")]
    [InlineData("Times-Roman", 33, 0, "LiberationSerif-Regular.ttf")]
    [InlineData("TimesNewRoman", 33, 0, "LiberationSerif-Regular.ttf")]
    [InlineData("TimesNewRoman,Italic", 97, -12, "LiberationSerif-Italic.ttf")]
    [InlineData("TimesNewRomanPSMT", 32, 0, "LiberationSerif-Regular.ttf")]
    [InlineData("CourierNew", 34, 0, "LiberationMono-Regular.ttf")]
    [InlineData("Courier", 32, 0, "LiberationMono-Regular.ttf")]
    [InlineData("Calibri", 33, 0, "LiberationMono-Regular.ttf")]
    [InlineData("Verdana", 34, 0, "LiberationSerif-Regular.ttf")]
    public void E25_StandardFamilyName_DecidesBeforeTheFlags(string name, int flags, int angle, string file)
    {
        var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N(name)), ("Flags", PdfInteger.Of(flags)),
            ("ItalicAngle", PdfInteger.Of(angle)));
        Assert.Equal(file, StandardFonts.Choose(name, descriptor).File);
    }

    /// <summary>
    /// 9.8.2 with No. 1202 and 1203, without installed fonts (as on a computer without Arial): the composite font of issue11915 draws with the
    /// sans serif italic substitute, not with the monospaced one.
    /// </summary>
    [Fact]
    public void E25_NonEmbeddedCidFont_WithoutInstalledFont_SubstituteOfTheNamedFamily()
    {
        var sans = EmbeddedFonts.StandardOpenType("LiberationSans-Italic.ttf");
        SystemFonts.DisabledForTests = true;
        try
        {
            var (doc, _, font) = Iso03PageFont("", _ => E25CidFont("Arial-ItalicMT,Italic", 97, -12));
            using (doc)
                Assert.True(Iso03SameOutline(sans.Outline(sans.GlyphForUnicode('a')), font.Glyph('a', 2).Outline), "nicht Liberation Sans Italic");
        }
        finally
        {
            SystemFonts.DisabledForTests = false;
        }
    }

    // ------------------------------------------------------------ No. 1204: a script without a default language system

    /// <summary>
    /// OpenType 2.4.1 and 2.4.2 (defaultLangSysOffset may be NULL; a language system holds the conventions of its language) measured against the
    /// shaping library: Courier New, whose script „latn“ has only the language systems „LTH “ and „ROM “, draws í (U+00ED, also typed as i with
    /// U+0301) with its glyph from the cmap, as the library; Lithuanian text takes the form of „LTH “ (locl: í with the dot kept). Before, the first
    /// language system „LTH “ applied to every Latin text (glyph 3150 instead of 116).
    /// </summary>
    [Fact]
    public void E25_CourierNew_IAcute_NoLithuanianFormForOtherText()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "cour.ttf");
        if (!OperatingSystem.IsWindows() || !File.Exists(file)) return;
        var font = OpenTypeFont.Parse(File.ReadAllBytes(file));
        var shaper = TextShaper.For(font);
        var plain = font.GlyphForUnicode(0xED);
        foreach (var typed in new[] { "\u00ED", "i\u0301", "v\u00EDa" })
        {
            Assert.Contains(plain, shaper.Shape(typed).Select(g => g.Glyph));
            Assert.Equal(ShaperTests.HarfBuzz(font.FileData, font.UnitsPerEm, typed, []).Glyphs.Order(), shaper.Shape(typed).Select(g => g.Glyph).Order());
            Assert.Contains(plain, shaper.Shape(typed, null, "de").Select(g => g.Glyph));
        }
        Assert.DoesNotContain(plain, shaper.Shape("\u00ED", null, "lt").Select(g => g.Glyph));
    }

    // ------------------------------------------------------------ No. 1205: the text of a CID that selects no glyph

    /// <summary>
    /// ISO 32000-2, 9.10.2 sentence 11 (without a way of the norm the reader chooses) measured: a CID TrueType font that is not embedded, with the
    /// collection Adobe-Identity or Adobe-UCS, Identity-H and no /ToUnicode, whose name is neither installed nor has a glyph order, reads its CIDs
    /// as Unicode — codes 0041 0042 are „AB“, as two of three reference readers read them and one draws them (probes T13, T15, T17); drawn are the
    /// glyphs of the standard Macintosh order (No. 1245, tests in package E33). The search finds the text and „Suchen und schwärzen“ removes both codes, so another reader shows nothing of them. Another
    /// registry keeps no text (T16: only one reference reader reads it); a font whose CIDs draw glyphs keeps the text of those glyphs (glyph order of
    /// Arial: „^_“, the display). Before, the text was empty and the search found nothing.
    /// </summary>
    [Fact]
    public void E25_NonEmbeddedCidFont_NoGlyph_TextIsTheCidAsUnicode()
    {
        SystemFonts.DisabledForTests = true;
        try
        {
            foreach (var ordering in new[] { "Identity", "UCS" })
            {
                var (doc, list, font) = Iso03PageFont("BT /F1 20 Tf 20 100 Td <00410042> Tj ET",
                    _ => E25CidFont("ProbeSans", 32, 0, toUnicode: false, ordering: ordering));
                using (doc)
                {
                    Assert.Equal("AB", string.Concat(list.Text.Select(t => t.Text)));
                    Assert.False(font.Glyph(0x41, 2).Outline.IsEmpty);   // the glyph of the standard Macintosh order (No. 1245)
                    var page = doc.Pages[0];
                    var text = PageText.Create(PageInterpreter.BuildText(doc, page));
                    var hit = Assert.Single(text.Find("AB"));
                    var redacted = Redactor.Apply(doc, page, text.RedactionAreas(hit));
                    Assert.Equal(2, redacted.RemovedGlyphs);
                    Assert.Equal("AB", redacted.RemovedText);
                }
            }
            var (otherDoc, otherList, _) = Iso03PageFont("BT /F1 20 Tf 20 100 Td <00410042> Tj ET",
                _ => E25CidFont("ProbeSans", 32, 0, toUnicode: false, registry: "Probe", ordering: "Probe"));
            using (otherDoc)
                Assert.Equal("", string.Concat(otherList.Text.Select(t => t.Text)));
            var (arialDoc, arialList, _) = Iso03PageFont("BT /F1 20 Tf 20 100 Td <00410042> Tj ET",
                _ => E25CidFont("Arial", 32, 0, toUnicode: false));
            using (arialDoc)
                Assert.Equal("^_", string.Concat(arialList.Text.Select(t => t.Text)));
        }
        finally
        {
            SystemFonts.DisabledForTests = false;
        }
    }
}
