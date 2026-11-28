using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-31 (OpenType specification, OS/2 table: yStrikeoutSize, yStrikeoutPosition, sFamilyClass, panose, ulUnicodeRange1–4,
/// achVendID, fsSelection): the tests the drafts of the sentence-by-sentence comparison asked for — the fields are carried through unchanged,
/// fonts are chosen by name and cmap, fsSelection decides bold and italic, and what the font of the text recognition writes.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-31)

    /// <summary>The decoration RichText draws for a span (private ElementAppearance.AppendDecoration) at the origin, 10 points wide.</summary>
    private static int Ot31MacStyle(byte[] font) => Ot30U16(OpenTypeFont.Parse(font).TableData("head"), 44);

    // ------------------------------------------------------------ 31.2.14, 31.2.15 strikeout

    /// <summary>
    /// OT 31.2.14 sentences 1–4, 31.2.15 sentences 1–5 (and ot-32: 32.2 sentences 4–8; docs/FEHLERGEDAECHTNIS.md, No. 1162): underline and
    /// strikeout of typed text come from the font — in Liberation Sans at 10 points the underline from post (top underlinePosition, thickness
    /// underlineThickness), the strikeout from OS/2 (top yStrikeoutPosition, thickness yStrikeoutSize), each scaled to the size and measured from
    /// the baseline of the line. Before, both were fixed shares of the size (0.5 thick at 10 points). MVAR is not read (question 2).
    /// </summary>
    [Fact]
    public void Ot31_StrikeoutAndUnderline_FromTheFont()
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            var element = new RichTextElement
            {
                Rect = new Core.Pdf.Document.PdfRectangle(0, 0, 200, 30),
                Content = new RichText("Probe", new RichStyle { Family = "Liberation Sans", Size = 10, Underline = true, Strikeout = true }),
            };
            var font = Ot20Lib;
            var post = font.TableData("post");
            var os2 = font.TableData("OS/2");
            double S(int v) => v * 10.0 / font.UnitsPerEm;
            var (underTop, underThick) = (S(BinaryPrimitives.ReadInt16BigEndian(post[8..])), S(BinaryPrimitives.ReadInt16BigEndian(post[10..])));
            var (strikeThick, strikeTop) = (S(BinaryPrimitives.ReadInt16BigEndian(os2[26..])), S(BinaryPrimitives.ReadInt16BigEndian(os2[28..])));
            var baseline = element.UprightSize.Height - RichLayout.Of(element).Lines[0].Baseline;
            var content = Encoding.ASCII.GetString(ElementAppearance.For(element).Form.RawData!);
            var rects = content.Split('\n').Where(l => l.EndsWith(" re f", StringComparison.Ordinal))
                .Select(l => l.Split(' ').Take(4).Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToList();
            Assert.Equal(2, rects.Count);
            Assert.Equal(baseline + underTop - underThick, rects[0][1], 2);
            Assert.Equal(underThick, rects[0][3], 2);
            Assert.Equal(baseline + strikeTop - strikeThick, rects[1][1], 2);
            Assert.Equal(strikeThick, rects[1][3], 2);
            Assert.NotEqual(0.5, underThick, 2);
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }

    /// <summary>
    /// OT 31.2.14 sentences 5–6 and OT 32.2 sentences 6–8 (writer, FG 1166): the own font writes a yStrikeoutSize (offset 26) as thick as the
    /// underline thickness of its post table (51 of 1024 units).
    /// </summary>
    [Fact]
    public void Ot31_GlyphlessOs2_StrikeoutSizeEqualsTheUnderline()
    {
        Assert.Equal(51, Ot30U16(Ot30Glyphless, 26));
        Assert.Equal(51, Ot30U16(Ot05GlyphlessTable("post"), 10));
    }

    /// <summary>
    /// OT 31.2.15 sentences 6–7: the own font has no visible glyphs — yStrikeoutPosition is 0 (offset 28) and both glyphs have no outline.
    /// </summary>
    [Fact]
    public void Ot31_GlyphlessOs2_StrikeoutPositionZero_GlyphsEmpty()
    {
        Assert.Equal(0, Ot30U16(Ot30Glyphless, 28));
        Assert.True(Ot05Glyphless.Value.Outline(0).IsEmpty);
        Assert.True(Ot05Glyphless.Value.Outline(1).IsEmpty);
    }

    // ------------------------------------------------------------ OS/2 carried through

    /// <summary>
    /// OT 31.2.14 sentences 1–4, 31.2.15 sentences 1–5, 31.2.16, 31.2.17 sentences 1–17, 31.2.18 sentences 1–60, 101–180, 31.2.19 (and ot-32:
    /// 31.2.22 sentences 4–5, 31.2.28 sentences 1–42): the fields that describe the design are never computed anew — a subset, a scrubbed copy
    /// and a rebuilt copy of Liberation Sans, Liberation Serif and Noto Sans Arabic (usLastCharIndex 0xFFFF, Arabic range bits 13, 63, 67) carry
    /// the OS/2 of the source byte for byte: weight, width, fsType, sub- and superscript, strikeout (26–29), family class (30–31), PANOSE
    /// (32–41), Unicode ranges (42–57), vendor (58–61), fsSelection, the typographic metrics, code pages (78–85), x-height and cap height. Only
    /// what follows from the glyphs kept is computed anew (FG 1159): xAvgCharWidth (2–3), usFirstCharIndex and usLastCharIndex (64–67) and
    /// usMaxContext (94–95).
    /// </summary>
    [Theory]
    [InlineData("Schriften/Standard/LiberationSans-Regular.ttf")]
    [InlineData("Schriften/Standard/LiberationSerif-Regular.ttf")]
    [InlineData("Schriften/Arabisch/NotoSansArabic-Regular.ttf")]
    public void Ot31_SubsetScrubRebuild_KeepTheDescriptiveOs2Fields(string file)
    {
        var font = File.ReadAllBytes(Path.Combine(Ot05Repo(), file));
        var parsed = OpenTypeFont.Parse(font);
        var glyphs = new[] { 'A', 'e', 'ب' }.Select(c => parsed.GlyphForUnicode(c)).Where(g => g > 0).ToArray();
        Assert.NotEmpty(glyphs);
        static byte[] Descriptive(byte[] os2)
        {
            var copy = os2.ToArray();
            foreach (var (from, to) in (ReadOnlySpan<(int, int)>)[(2, 4), (64, 68), (94, 96)])
                if (to <= copy.Length) copy.AsSpan(from, to - from).Clear();
            return copy;
        }
        var source = Ot30Os2Of(font);
        Assert.Equal(Descriptive(source), Descriptive(Ot30Os2Of(TrueTypeSubsetter.Subset(font, glyphs))));
        Assert.Equal(Descriptive(source), Descriptive(Ot30Os2Of(TrueTypeSubsetter.Scrub(font, glyphs))));
        Assert.Equal(Descriptive(source), Descriptive(Ot30Os2Of(TrueTypeSubsetter.Rebuild(font, glyphs.ToDictionary(g => g, _ => 1000), [], null))));
        if (file.Contains("Arabic", StringComparison.Ordinal))
        {
            Assert.Equal(0xFFFF, Ot30U16(source, 66));
            Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(42)) & (1u << 13));
        }
    }

    /// <summary>
    /// OT 31.2.18 sentences 61–100: the Unicode ranges of the CJK font stay as they were — the OpenType excerpt of Noto Sans CJK scrubbed to
    /// „漢字A“ has the ulUnicodeRange fields (42–57) of the source byte for byte.
    /// </summary>
    [Fact]
    public void Ot31_ScrubbedCjkFont_KeepsItsUnicodeRanges()
    {
        Assert.Equal(Ot30Os2Of(Ot05NotoExcerpt.Value)[42..58], Ot30Os2Of(Ot05NotoScrubbed.Value)[42..58]);
    }

    /// <summary>
    /// OT 31.2.16–31.2.19 and 31.2.20 sentences 5–8, 10, 12–13, 15–16, 20, 22–26, 53–63, 65–66 (writer): the own font leaves the selection fields
    /// empty — family class, PANOSE, Unicode ranges (also bit 57 and the reserved bits 123–127) and vendor are 0, the version is 4 and fsSelection
    /// is 0x0040 (regular; bits 0–5 and 7–15 clear), which OpenTypeFont.Selection reads.
    /// </summary>
    [Fact]
    public void Ot31_GlyphlessOs2_SelectionFieldsEmpty_Regular()
    {
        var os2 = Ot30Glyphless;
        Assert.Equal(4, Ot30U16(os2, 0));
        Assert.All(os2[30..62], b => Assert.Equal(0, b));
        Assert.Equal(0x0040, Ot30U16(os2, 62));
        Assert.Equal(0x0040, Ot05Glyphless.Value.Selection);
    }

    // ------------------------------------------------------------ 31.2.16, 31.2.17 choosing fonts

    /// <summary>
    /// OT 31.2.16 sentences 1–10 and 31.2.17 sentences 18–21: fallbacks and kinds come from the family name, not from sFamilyClass or PANOSE —
    /// „Times New Roman“ falls back to Liberation Serif, „Courier New“ to Liberation Mono; Consolas is fixed pitch, Georgia serif, Verdana neither.
    /// </summary>
    [Fact]
    public void Ot31_FallbackAndKind_ByFamilyName()
    {
        Assert.Equal("Liberation Serif", RichFonts.Fallback("Times New Roman"));
        Assert.Equal("Liberation Mono", RichFonts.Fallback("Courier New"));
        Assert.Equal((false, true), RichFonts.Kind("Consolas"));
        Assert.Equal((true, false), RichFonts.Kind("Georgia"));
        Assert.Equal((false, false), RichFonts.Kind("Verdana"));
    }

    /// <summary>
    /// OT 31.2.17 sentences 22–23: a variable font is drawn in its default instance and its PANOSE is not touched — Liberation Sans with fvar and
    /// gvar draws „A“ as without them, and a subset keeps bytes 32–41 of OS/2.
    /// </summary>
    [Fact]
    public void Ot31_VariableFont_DefaultInstance_PanoseUnchanged()
    {
        var (fvar, gvar) = Ot05VariationTables(Ot05Sans.Value.GlyphCount);
        var variable = Ot05SansWith(("fvar", fvar), ("gvar", gvar));
        var a = Ot05Glyphs("A");
        Assert.True(Ot05SameOutline(Ot05Sans.Value.Outline(a[0]), OpenTypeFont.Parse(variable).Outline(a[0])));
        Assert.Equal(Ot30Os2Of(variable)[32..42], Ot30Os2Of(TrueTypeSubsetter.Subset(variable, a))[32..42]);
    }

    /// <summary>OT 31.2.18 sentences 181–184: the meta table is not read and a subset leaves it out (scrubbing too: Ot01_Scrub_DropsDeviceMetricsAndMeta).</summary>
    [Fact]
    public void Ot31_Subset_LeavesOutMeta()
    {
        var font = Ot05SansWith(("meta", [0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 16, 0, 0, 0, 0]));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(font, Ot05Glyphs("A"))).HasTable("meta"));
    }

    // ------------------------------------------------------------ 31.2.20 fsSelection

    /// <summary>OT 31.2.20 sentences 1–2: OpenTypeFont.Selection reads fsSelection — 0x0040 for the own font, bit 5 for Liberation Sans Bold.</summary>
    [Fact]
    public void Ot31_Selection_ReadFromOs2()
    {
        Assert.Equal(0x0040, Ot05Glyphless.Value.Selection);
        Assert.NotEqual(0, OpenTypeFont.Parse(Ot30File("LiberationSans-Bold.ttf")).Selection & 0x20);
    }

    /// <summary>
    /// OT 31.2.20 sentences 3–4, 9, 19, 21: bit 5 BOLD and bit 0 ITALIC — a system font with the bit needs no faked bold or italic, without it it
    /// does; bits 5 and 0 agree with head.macStyle bits 0 and 1 in Liberation Sans Bold, Italic and Regular, in their subsets and in the own font.
    /// </summary>
    [Fact]
    public void Ot31_BoldAndItalicBits_DecideFaking_AgreeWithMacStyle()
    {
        Assert.False(Ot30Fakes(Ot30WithOs2(o => { Ot30Set(o, 4, 400); Ot30Set(o, 62, 0x0020); }), true, false).FakeBold);
        Assert.True(Ot30Fakes(Ot30WithOs2(o => { Ot30Set(o, 4, 400); Ot30Set(o, 62, 0x0040); }), true, false).FakeBold);
        Assert.False(Ot30Fakes(Ot30WithOs2(o => Ot30Set(o, 62, 0x0001)), false, true).FakeItalic);
        Assert.True(Ot30Fakes(Ot30WithOs2(o => Ot30Set(o, 62, 0x0040)), false, true).FakeItalic);
        foreach (var file in (string[])["LiberationSans-Bold.ttf", "LiberationSans-Italic.ttf", "LiberationSans-Regular.ttf"])
        {
            var font = Ot30File(file);
            foreach (var probe in (byte[][])[font, TrueTypeSubsetter.Subset(font, Ot05Glyphs("AB"))])
            {
                var selection = OpenTypeFont.Parse(probe).Selection;
                var mac = Ot31MacStyle(probe);
                Assert.Equal((selection >> 5) & 1, mac & 1);
                Assert.Equal(selection & 1, (mac >> 1) & 1);
            }
        }
        Assert.Equal(0, Ot05Glyphless.Value.Selection & 0x21);
        Assert.Equal(0, Ot31MacStyle(Ot31GlyphlessFile()) & 0x03);
    }

    private static byte[] Ot31GlyphlessFile() => PdfKnead.Core.Ocr.OcrTextLayer.Glyphless;

    /// <summary>
    /// OT 31.2.20 sentences 5–8, 20: bits 1–4 (underscore, negative, outlined, strikeout) are not evaluated — the own font leaves them clear, and a
    /// font with bit 3 reads as the same style as without.
    /// </summary>
    [Fact]
    public void Ot31_DecorationBits_NotEvaluated()
    {
        Assert.Equal(0, Ot05Glyphless.Value.Selection & 0x1E);
        Assert.Equal(Ot30StyleOf(Ot30WithOs2(o => Ot30Set(o, 62, 0x0040))), Ot30StyleOf(Ot30WithOs2(o => Ot30Set(o, 62, 0x0048))));
    }

    /// <summary>
    /// OT 31.2.20 sentences 11, 27–30 and ot-32 31.2.25 sentences 1–4, 8–10 (docs/FEHLERGEDAECHTNIS.md, No. 1162, 1211): the line spacing of new
    /// text is the factor times the natural line height of the font — in Liberation Sans 10 pt (USE_TYPO_METRICS not set: hhea 1854 − (−434) + 67
    /// of 2048) 11.499 or 13.799 points for the factors 1.0 and 1.2, not (sTypoAscender − sTypoDescender + sTypoLineGap) / unitsPerEm × 10 =
    /// 10.88; a block of the earlier kind keeps the factor times the size (10 or 12 points). Before, every block took the factor times the size;
    /// the own font leaves bit 7 clear.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.2)]
    public void Ot31_LineSpacing_FactorTimesLineHeightOfTheFont(double factor)
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            RichLayout Layout(bool fontLineHeight) => RichLayout.Layout(new RichText("Eins\nZwei", new RichStyle { Family = "Liberation Sans", Size = 10 }), 500,
                BlockAlignment.Left, factor, null, 0, fontLineHeight: fontLineHeight);
            Assert.Equal(factor * 10 * (1854 + 434 + 67) / 2048.0, Layout(true).Lines[1].Baseline - Layout(true).Lines[0].Baseline, 6);
            Assert.Equal(factor * 10, Layout(false).Lines[1].Baseline - Layout(false).Lines[0].Baseline, 6);
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
        Assert.Equal(0, Ot05Glyphless.Value.Selection & 0x80);
    }

    /// <summary>
    /// OT 31.2.20 sentences 12–13, 31–43: families are not grouped by the WWS names 21/22 — a font with bit 8 clear and names 21/22 is offered
    /// under name 1, the family of the model regular/bold/italic/bold italic of the font list (OT 57.2; before under name 16, FG 1176); the own
    /// font leaves bit 8 clear.
    /// </summary>
    [Fact]
    public void Ot31_WwsNames_NotUsedForTheFamily()
    {
        var font = Ot05SansWith(("name", Ot05Name((3, 1, 0x409, 1, "Muster Kondensiert"), (3, 1, 0x409, 2, "Regular"), (3, 1, 0x409, 6, "Muster-Kondensiert"),
            (3, 1, 0x409, 16, "Muster"), (3, 1, 0x409, 17, "Kondensiert"), (3, 1, 0x409, 21, "Muster Kondensiert WWS"), (3, 1, 0x409, 22, "Regular"))),
            ("OS/2", Ot31Os2Table(o => Ot30Set(o, 62, 0x0040))));
        Assert.Equal("Muster Kondensiert", Ot05SystemFontNames(font).Family);
        Assert.Equal(0, Ot05Glyphless.Value.Selection & 0x100);
    }

    private static byte[] Ot31Os2Table(Action<byte[]> change)
    {
        var os2 = Ot30Os2Of(Ot30File());
        change(os2);
        return os2;
    }

    /// <summary>OT 31.2.20 sentences 44–47: STAT is not read and a subset leaves it out.</summary>
    [Fact]
    public void Ot31_Subset_LeavesOutStat()
    {
        byte[] stat = [0, 1, 0, 2, 0, 8, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2];
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(Ot05SansWith(("STAT", stat)), Ot05Glyphs("A"))).HasTable("STAT"));
    }

    /// <summary>
    /// OT 31.2.20 sentences 48–51: weight is usWeightClass, slope fsSelection bit 0 or bit 9 — usWeightClass 700 reads bold, bit 9 alone italic
    /// (the keys from names 16 and 17, the family of the font list from name 1: SystemFont_KeysFromTheTypographicNames_FamilyFromId1).
    /// </summary>
    [Fact]
    public void Ot31_WeightAndSlope_AsDefined()
    {
        Assert.True(Ot30StyleOf(Ot30WithOs2(o => { Ot30Set(o, 4, 700); Ot30Set(o, 62, 0); })).Bold);
        Assert.True(Ot30StyleOf(Ot30WithOs2(o => Ot30Set(o, 62, 0x0200), 4)).Italic);
    }

    /// <summary>
    /// OT 31.2.20 sentences 14, 52–58: bit 9 OBLIQUE in a version-4 table — asked for italic, a system font with only bit 9 needs no faked italic,
    /// the same font without bit 9 does.
    /// </summary>
    [Fact]
    public void Ot31_ObliqueBitInVersion4_NoFakedItalic()
    {
        Assert.False(Ot30Fakes(Ot30WithOs2(o => Ot30Set(o, 62, 0x0200), 4), false, true).FakeItalic);
        Assert.True(Ot30Fakes(Ot30WithOs2(o => Ot30Set(o, 62, 0x0000), 4), false, true).FakeItalic);
    }

    /// <summary>
    /// OT 31.2.20 sentence 64: in versions 0–3 bits 7–15 are to be ignored — a system font with OS/2 version 3 and only bit 9 should need a faked
    /// italic.
    /// </summary>
    [Fact]
    public void Ot31_ObliqueBitInVersion3_Ignored()
    {
        Assert.True(Ot30Fakes(Ot30WithOs2(o => Ot30Set(o, 62, 0x0200), 3), false, true).FakeItalic);
    }

    /// <summary>
    /// OT 31.2.20 sentence 67: bits 10–15 are ignored in versions 4 and 5 — with fsSelection 0xFC00 bold and italic are faked as without these
    /// bits.
    /// </summary>
    [Fact]
    public void Ot31_ReservedSelectionBits_Ignored()
    {
        Assert.Equal((true, true), Ot30Fakes(Ot30WithOs2(o => { Ot30Set(o, 4, 400); Ot30Set(o, 62, 0xFC00); }, 4), true, true));
    }
}
