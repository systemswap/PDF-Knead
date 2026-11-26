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
    private static string Ot31Decoration(RichStyle style)
    {
        var sb = new StringBuilder();
        typeof(ElementAppearance).GetMethod("AppendDecoration", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [sb, style, 0.0, 0.0, 10.0]);
        return sb.ToString();
    }

    private static int Ot31MacStyle(byte[] font) => Ot30U16(OpenTypeFont.Parse(font).TableData("head"), 44);

    // ------------------------------------------------------------ 31.2.14, 31.2.15 strikeout

    /// <summary>
    /// OT 31.2.14 sentences 1–4, 31.2.15 sentences 1–5 (and ot-32: 32.2 sentences 6–8 for reading, 32.7 sentences 1–6) — today's behaviour,
    /// question 14 open: strikeout and underline of typed text are fixed fractions of the size, not yStrikeout*, post or MVAR of the font (the
    /// drawing does not see the font at all) — at 10 points 0.5 thick, the strikeout from 2.55 above the baseline, the underline from 1.45 below;
    /// at 4 points the least thickness 0.4.
    /// </summary>
    [Fact]
    public void Ot31_StrikeoutAndUnderline_FixedFractionsOfTheSize()
    {
        var style = new RichStyle { Family = "Liberation Sans", Size = 10 };
        Assert.Contains("0 2.55 10 0.5 re f", Ot31Decoration(style with { Strikeout = true }), StringComparison.Ordinal);
        Assert.Contains("0 -1.45 10 0.5 re f", Ot31Decoration(style with { Underline = true }), StringComparison.Ordinal);
        Assert.Contains("0 0.92 10 0.4 re f", Ot31Decoration(style with { Size = 4, Strikeout = true }), StringComparison.Ordinal);
        Assert.Equal(Ot31Decoration(style with { Strikeout = true }), Ot31Decoration(style with { Family = "Liberation Serif", Strikeout = true }));
    }

    /// <summary>
    /// OT 31.2.14 sentences 5–6 (today's behaviour, question 27 open): the own font writes yStrikeoutSize 0 (offset 26), its post an underline
    /// thickness of 50.
    /// </summary>
    [Fact]
    public void Ot31_GlyphlessOs2_StrikeoutSizeZeroToday()
    {
        Assert.Equal(0, Ot30U16(Ot30Glyphless, 26));
        Assert.Equal(50, Ot30U16(Ot05GlyphlessTable("post"), 10));
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
    /// 31.2.22 sentences 4–5, 31.2.28 sentences 1–42): the fields are never computed anew — a subset, a scrubbed copy and a rebuilt copy of
    /// Liberation Sans, Liberation Serif and Noto Sans Arabic (usLastCharIndex 0xFFFF, Arabic range bits 13, 63, 67) carry the OS/2 of the source
    /// byte for byte: strikeout (26–29), family class (30–31), PANOSE (32–41), Unicode ranges (42–57), vendor (58–61), code pages (78–85).
    /// </summary>
    [Theory]
    [InlineData("Schriften/Standard/LiberationSans-Regular.ttf")]
    [InlineData("Schriften/Standard/LiberationSerif-Regular.ttf")]
    [InlineData("Schriften/Arabisch/NotoSansArabic-Regular.ttf")]
    public void Ot31_SubsetScrubRebuild_KeepOs2ByteForByte(string file)
    {
        var font = File.ReadAllBytes(Path.Combine(Ot05Repo(), file));
        var parsed = OpenTypeFont.Parse(font);
        var glyphs = new[] { 'A', 'e', 'ب' }.Select(c => parsed.GlyphForUnicode(c)).Where(g => g > 0).ToArray();
        Assert.NotEmpty(glyphs);
        var source = Ot30Os2Of(font);
        Assert.Equal(source, Ot30Os2Of(TrueTypeSubsetter.Subset(font, glyphs)));
        Assert.Equal(source, Ot30Os2Of(TrueTypeSubsetter.Scrub(font, glyphs)));
        Assert.Equal(source, Ot30Os2Of(TrueTypeSubsetter.Rebuild(font, glyphs.ToDictionary(g => g, _ => 1000), [], null)));
        if (file.Contains("Arabic", StringComparison.Ordinal))
        {
            Assert.Equal(0xFFFF, Ot30U16(source, 66));
            Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(42)) & (1u << 13));
        }
    }

    /// <summary>
    /// OT 31.2.18 sentences 61–100: the Unicode ranges of the CJK font stay as they were — the OpenType excerpt of Noto Sans CJK scrubbed to
    /// „漢字A“ has the OS/2 of the source byte for byte.
    /// </summary>
    [Fact]
    public void Ot31_ScrubbedCjkFont_KeepsItsUnicodeRanges()
    {
        Assert.Equal(Ot30Os2Of(Ot05NotoExcerpt.Value), Ot30Os2Of(Ot05NotoScrubbed.Value));
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
    /// OT 31.2.20 sentences 11, 27–30 and ot-32 31.2.25 sentences 1–4, 8–10 (today's behaviour, question 14 open): the line spacing of typed
    /// text is the factor times the size — in Liberation Sans 10 pt the baselines are 10 or 12 points apart for the factors 1.0 and 1.2, not
    /// (sTypoAscender − sTypoDescender + sTypoLineGap) / unitsPerEm × 10 = 10.88; the own font leaves bit 7 clear.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.2)]
    public void Ot31_LineSpacing_FactorTimesSize_NotTypoMetrics(double factor)
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            var layout = RichLayout.Layout(new RichText("Eins\nZwei", new RichStyle { Family = "Liberation Sans", Size = 10 }), 500, BlockAlignment.Left, factor, null, 0);
            Assert.Equal(factor * 10, layout.Lines[1].Baseline - layout.Lines[0].Baseline, 3);
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
        Assert.Equal(0, Ot05Glyphless.Value.Selection & 0x80);
    }

    /// <summary>
    /// OT 31.2.20 sentences 12–13, 31–43: families are grouped by name 16 (else 1), not by the WWS names 21/22 — a font with bit 8 clear and names
    /// 21/22 is offered under name 16; the own font leaves bit 8 clear.
    /// </summary>
    [Fact]
    public void Ot31_WwsNames_NotUsedForTheFamily()
    {
        var font = Ot05SansWith(("name", Ot05Name((3, 1, 0x409, 1, "Muster Kondensiert"), (3, 1, 0x409, 2, "Regular"), (3, 1, 0x409, 6, "Muster-Kondensiert"),
            (3, 1, 0x409, 16, "Muster"), (3, 1, 0x409, 17, "Kondensiert"), (3, 1, 0x409, 21, "Muster Kondensiert WWS"), (3, 1, 0x409, 22, "Regular"))),
            ("OS/2", Ot31Os2Table(o => Ot30Set(o, 62, 0x0040))));
        Assert.Equal("Muster", Ot05SystemFontNames(font).Family);
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
    /// (the family from name 16: SystemFont_TypographicFamilyNameGoesFirst).
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
