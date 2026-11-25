using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package cff-02 (Adobe TN 5176, sections 16–19 and Appendices A–C, H): the missing evidence of the sentence-by-sentence draft as tests.
/// Uses the helpers of package cff-01 (M7Tests.Cff01.cs).
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>A local or global subroutine that draws a line to (k / 256, k % 256) from where it is called, so each index shows itself.</summary>
    private static byte[] Cff02Marker(int k) => Cff01Cs(k / 256, k % 256, "rlineto", "return");

    /// <summary>Where the line drawn by a subroutine called with <paramref name="operand"/> ends, read and after flattening; null without a line.</summary>
    private static (string Read, string? Flattened) Cff02Call(int count, int operand, bool global)
    {
        var subrs = Enumerable.Range(0, count).Select(Cff02Marker).ToArray();
        var glyph = Cff01Cs(0, 0, "rmoveto", operand, global ? "callgsubr" : "callsubr", "endchar");
        var cff = global ? Cff01Glyphs([glyph], globalSubrs: subrs) : Cff01Glyphs([glyph], subrs: subrs);
        string? flattened;
        try
        {
            flattened = Cff01Path(CffFont.Parse(CffSubsetter.Subset(cff, [1])).Outline(1));
        }
        catch (FontFormatException)
        {
            flattened = null;
        }
        return (Cff01Path(CffFont.Parse(cff).Outline(1)), flattened);
    }

    /// <summary>
    /// TN 5176, 16 sentences 1–2 and 6–7 (b): a glyph whose crossbar is in a local subroutine is drawn with both subpaths; the subset and the
    /// scrub write it without subroutines (no callsubr in the charstring, no Subrs in the Private DICT) with the same points. The rebuild
    /// writes no Subrs either.
    /// </summary>
    [Fact]
    public void Cff02_LocalSubroutine_ExecutedAndInlinedByTheWriters()
    {
        var cff = Cff01Glyphs([Cff01Cs(0, 0, "rmoveto", 100, 0, "rlineto", -107, "callsubr", "endchar")],
            subrs: [Cff01Cs(0, 50, "rmoveto", 100, 0, "rlineto", "return")]);
        var path = Cff01Path(CffFont.Parse(cff).Outline(1));
        Assert.Equal("M 0 0 | L 100 0 | Z | M 100 50 | L 200 50 | Z", path);
        foreach (var output in new[] { CffSubsetter.Subset(cff, [1]), CffScrubber.Scrub(cff, [1]).Data })
        {
            Assert.Equal(path, Cff01Path(CffFont.Parse(output).Outline(1)));
            Assert.DoesNotContain(10, Cff01CsOps(Cff01CharStrings(output)[1]));
            Assert.DoesNotContain(Cff01FontDictOf(output, 1).Private, e => e.Op == 19);
        }
        var built = CffBuilder.Build("Probe", Cff01Thousandth, [("g1", Cff01Square(500), 600)]);
        Assert.DoesNotContain(Cff01FontDictOf(built, 1).Private, e => e.Op == 19);
    }

    /// <summary>
    /// TN 5176, 16 sentences 3–5: callsubr takes the local subroutines of the Font DICT of the glyph, callgsubr the global ones. Two Font DICTs
    /// whose local subroutine 0 draws differently, and a global subroutine: the glyph of Font DICT 1 draws its own local one and the global
    /// one; the subset draws the same points without subroutines.
    /// </summary>
    [Fact]
    public void Cff02_CidFont_LocalSubroutinesOfTheGlyphsFontDict()
    {
        var glyph = Cff01Cs(0, 0, "rmoveto", -107, "callsubr", -107, "callgsubr", "endchar");
        var cff = Cff01Cid([[14], glyph, glyph], [3, 0, 2, 0, 0, 0, 0, 2, 1, 0, 3],   // glyphs 0–1 → Font DICT 0, glyph 2 → 1
            [new Cff01Fd([], [Cff01Cs(10, 0, "rlineto", "return")]), new Cff01Fd([], [Cff01Cs(0, 20, "rlineto", "return")])],
            globalSubrs: [Cff01Cs(5, 5, "rlineto", "return")]);
        var font = CffFont.Parse(cff);
        Assert.Equal("M 0 0 | L 10 0 | L 15 5 | Z", Cff01Path(font.Outline(1)));
        Assert.Equal("M 0 0 | L 0 20 | L 5 25 | Z", Cff01Path(font.Outline(2)));
        var subset = CffFont.Parse(CffSubsetter.Subset(cff, [2]));
        Assert.Equal(Cff01Path(font.Outline(2)), Cff01Path(subset.Outline(subset.GlyphByCid(2))));
    }

    /// <summary>
    /// TN 5176, 16 sentences 6–7 (a): the Subrs offset counts from the start of the Private DICT — a Private DICT at offset 1 000 with Subrs 30
    /// reads the INDEX at 1 030, not at 30.
    /// </summary>
    [Fact]
    public void Cff02_SubrsOffset_RelativeToThePrivateDict()
    {
        var cff = Cff01Build(new Cff01Spec
        {
            CharStrings = [[14], Cff01Cs(0, 0, "rmoveto", -107, "callsubr", "endchar")],
            LocalSubrs = [Cff01Cs(10, 0, "rlineto", "return")],
            PrivateAt = 1000,
            SubrsGap = 24,   // Private DICT of 6 bytes, then 24 zeros: Subrs at 30
        });
        var at = Cff01Top(cff).Get(18)!;
        Assert.Equal(1000, at[1]);
        Assert.Equal(30, CffFont.Dict.Parse(cff.AsSpan(1000, (int)at[0])).Int(19));
        Assert.Equal("M 0 0 | L 10 0 | Z", Cff01Path(CffFont.Parse(cff).Outline(1)));
    }

    /// <summary>
    /// TN 5176, 16 sentences 8–9: the Global Subrs INDEX follows the String INDEX — the writers put 00 00 there; a reader finds a global
    /// subroutine there, called with −107 callgsubr.
    /// </summary>
    [Fact]
    public void Cff02_GlobalSubrsIndex_FollowsTheStringIndex()
    {
        foreach (var (writer, cff) in Cff01Writers())
        {
            var at = Cff01Parts(cff).GlobalAt;
            Assert.True(cff[at] == 0 && cff[at + 1] == 0, writer);
            Assert.Equal(0, Cff01Parts(cff).GlobalCount);
        }
        var withGlobal = Cff01Glyphs([Cff01Cs(0, 0, "rmoveto", -107, "callgsubr", "endchar")], globalSubrs: [Cff01Cs(7, 7, "rlineto", "return")]);
        Assert.Equal(1, Cff01Parts(withGlobal).GlobalCount);
        Assert.Equal("M 0 0 | L 7 7 | Z", Cff01Path(CffFont.Parse(withGlobal).Outline(1)));
    }

    /// <summary>
    /// TN 5176, 16 sentences 10–11, 13, 15: the subroutine number is the operand plus the bias. With one local subroutine −107 calls number 0
    /// and 0 calls none (the reader draws without it, flattening is a format error); with 1 240 global subroutines −1 131 calls number 0.
    /// </summary>
    [Fact]
    public void Cff02_SubroutineNumber_IsOperandPlusBias()
    {
        Assert.Equal(("M 0 0 | L 0 0 | Z", "M 0 0 | L 0 0 | Z"), Cff02Call(1, -107, global: false));
        Assert.Equal(("", null), Cff02Call(1, 0, global: false));
        Assert.Equal("M 0 0 | L 0 0 | Z", Cff02Call(1240, -1131, global: true).Read);
        Assert.Equal("M 0 0 | L 0 1 | Z", Cff02Call(1240, -1130, global: true).Read);
    }

    /// <summary>TN 5176, 16 sentence 12 (TN 5177, 2.3 sentence 9 and 4.7 sentences 1–5): the bias is 107 below 1 240 subroutines, 1 131 below 33 900, else 32 768.</summary>
    [Fact]
    public void Cff02_SubroutineBias_ThreeSteps()
    {
        Assert.Equal((107, 107, 1131, 1131, 32768, 32768),
            (CffFont.Bias(0), CffFont.Bias(1239), CffFont.Bias(1240), CffFont.Bias(33899), CffFont.Bias(33900), CffFont.Bias(65535)));
    }

    /// <summary>
    /// TN 5176, 16 sentences 18–70: subroutine numbers across the three bias steps, read and flattened — each subroutine draws its own point.
    /// (The draft named +32 767 → 65 535 for 33 900 and 65 535 subroutines; that index cannot exist in an INDEX of at most 65 535 entries, so
    /// the last index of each count is checked instead, and +32 767 with 65 535 subroutines calls none.)
    /// </summary>
    [Theory]
    [InlineData(1239, -107, 0)]
    [InlineData(1239, 107, 214)]
    [InlineData(1239, 108, 215)]
    [InlineData(1239, 1131, 1238)]
    [InlineData(1240, -1131, 0)]
    [InlineData(1240, -108, 1023)]
    [InlineData(1240, -107, 1024)]
    [InlineData(1240, 107, 1238)]
    [InlineData(1240, 108, 1239)]
    [InlineData(33899, 32767, 33898)]
    [InlineData(33900, -32768, 0)]
    [InlineData(33900, -107, 32661)]
    [InlineData(33900, 107, 32875)]
    [InlineData(33900, 1131, 33899)]
    [InlineData(65535, -32768, 0)]
    [InlineData(65535, 32766, 65534)]
    [InlineData(65535, 32767, -1)]
    public void Cff02_SubroutineNumbers_ReachTheirIndex(int count, int operand, int index)
    {
        var expected = index < 0 ? "" : $"M 0 0 | L {index / 256} {index % 256} | Z";
        var (read, flattened) = Cff02Call(count, operand, global: false);
        Assert.Equal(expected, read);
        Assert.Equal(index < 0 ? null : expected, flattened);
    }

    /// <summary>
    /// TN 5176, 17 sentences 1–7: a FontSet whose first font is synthetic (FullName, FontMatrix, SyntheticBase 1, no CharStrings) — PDF-Knead
    /// reads only the first font, so the program is a format error and the PDF font is substituted (no program).
    /// </summary>
    [Fact]
    public void Cff02_SyntheticFirstFont_IsAFormatError()
    {
        // first font: FullName, FontMatrix, SyntheticBase 1 and no CharStrings; second font: a real one
        var synthetic = Cff01Dict((2, [391]), (1207, [1, 0, 0, 1, 0, 0]), (1220, [1]));
        var combined = Cff01Build(new Cff01Spec
        {
            Names = ["Synthetisch"u8.ToArray(), "Probe"u8.ToArray()],
            FirstTop = synthetic,
            Strings = ["Synthetisch Kursiv"],
            CharStrings = [[14], Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar")],
        });
        Assert.Equal(2, Cff01Parts(combined).Tops.Length);
        Assert.True(CffFont.Dict.Parse(Cff01Parts(combined).Tops[1]).Has(17));
        Assert.Throws<FontFormatException>(() => CffFont.Parse(combined));
        Assert.Null(FontProgram.FromBytes(combined));
    }

    /// <summary>
    /// TN 5176, 18 sentences 7–9: each Font DICT has its own Private DICT. Two Font DICTs with nominalWidthX 100 and 300, FDSelect glyph 1 → 0,
    /// glyphs 0 and 2 → 1: the same charstring has width 150 and 350; a subset of glyph 2 has one Font DICT with nominalWidthX 300.
    /// </summary>
    [Fact]
    public void Cff02_FontDicts_EachWithItsPrivateDict()
    {
        var glyph = Cff01Cs(50, 0, 0, "rmoveto", 10, 0, "rlineto", "endchar");
        var cff = Cff01Cid([[14], glyph, glyph], [0, 1, 0, 1],   // format 0
            [new Cff01Fd(Cff01Dict((21, [100])), []), new Cff01Fd(Cff01Dict((21, [300])), [])]);
        var font = CffFont.Parse(cff);
        Assert.Equal((150.0, 350.0), (font.AdvanceWidth(1), font.AdvanceWidth(2)));
        var subsetData = CffSubsetter.Subset(cff, [2]);
        var data = new ArraySegment<byte>(subsetData);
        var r = new FontReader(data) { Position = Cff01Top(subsetData).Int(1236)!.Value };
        Assert.Equal(1, CffFont.CffIndex.Read(data, ref r).Count);
        Assert.Equal(300, Cff01FontDictOf(subsetData, 1).Private.Single(e => e.Op == 21).Operands[0]);
        var subset = CffFont.Parse(subsetData);
        Assert.Equal(350, subset.AdvanceWidth(subset.GlyphByCid(2)));
    }

    /// <summary>
    /// TN 5176, 18 sentences 13–14: a CIDFont has no encoding (256 zeros, no glyph by name); a charset offset 0 is read as the identity
    /// (lenient); the subset and the CID rebuild write no Encoding operator.
    /// </summary>
    [Fact]
    public void Cff02_CidFont_NoEncodingAndIdentityCharset()
    {
        var cff = Cff01Cid([[14], [14], [14]], [0, 0, 0, 0], [new Cff01Fd([], [])]);
        var font = CffFont.Parse(cff);
        Assert.All(font.Encoding, code => Assert.Equal(0, code));
        Assert.Equal(-1, font.GlyphByName("A"));
        Assert.Equal(-1, font.GlyphByName(".notdef"));
        Assert.Equal((0, 1, 2), (font.GlyphByCid(0), font.GlyphByCid(1), font.GlyphByCid(2)));
        Assert.False(Cff01Top(CffSubsetter.Subset(cff, [1])).Has(16));
        Assert.False(Cff01Top(CffBuilder.BuildCid("Probe", Cff01Thousandth, "Adobe", "Identity", 0, [(1, Cff01Square(500), 600)])).Has(16));
    }

    /// <summary>
    /// TN 5176, 19 sentences 15–23 (and 24–30 for the writer): FDSelect format 3 — 03 0002 0000 00 0002 01 0004 for four glyphs puts glyphs 0–1
    /// in Font DICT 0 and 2–3 in Font DICT 1 (seen in the widths); the CID rebuild with three glyphs writes 03 0001 0000 00 0004.
    /// </summary>
    [Fact]
    public void Cff02_FdSelectFormat3_RangesReadAndWritten()
    {
        var glyph = Cff01Cs(50, "endchar");
        var cff = Cff01Cid([glyph, glyph, glyph, glyph], [3, 0, 2, 0, 0, 0, 0, 2, 1, 0, 4],
            [new Cff01Fd(Cff01Dict((21, [100])), []), new Cff01Fd(Cff01Dict((21, [300])), [])]);
        var font = CffFont.Parse(cff);
        Assert.Equal(new[] { 150.0, 150, 350, 350 }, Enumerable.Range(0, 4).Select(g => font.AdvanceWidth(g)));
        var built = CffBuilder.BuildCid("Probe", Cff01Thousandth, "Adobe", "Identity", 0,
            [(1, Cff01Square(500), 600), (2, Cff01Square(400), 500), (3, Cff01Square(300), 400)]);
        var at = Cff01Top(built).Int(1237)!.Value;
        Assert.Equal(new byte[] { 3, 0, 1, 0, 0, 0, 0, 4 }, built[at..(at + 8)]);
    }

    /// <summary>
    /// TN 5176, 19 sentences 24–30: the reader tolerates FDSelect ranges that break the rules — a first range at glyph 1 leaves glyph 0 with
    /// Font DICT 0 without an error, a sentinel 9 for four glyphs reads nothing beyond glyph 3.
    /// </summary>
    [Fact]
    public void Cff02_FdSelectFormat3_ToleratesAFirstRangeAboveZeroAndALargeSentinel()
    {
        Assert.Equal(new byte[] { 0, 1, 1, 1 }, CffFont.ReadFdSelect(new byte[] { 3, 0, 1, 0, 1, 1, 0, 4 }, 0, 4));
        Assert.Equal(new byte[] { 1, 1, 1, 1 }, CffFont.ReadFdSelect(new byte[] { 3, 0, 1, 0, 0, 1, 0, 9 }, 0, 4));
        var glyph = Cff01Cs(50, "endchar");
        var cff = Cff01Cid([glyph, glyph, glyph, glyph], [3, 0, 1, 0, 1, 1, 0, 9],
            [new Cff01Fd(Cff01Dict((21, [100])), []), new Cff01Fd(Cff01Dict((21, [300])), [])]);
        var font = CffFont.Parse(cff);
        Assert.Equal(new[] { 150.0, 350, 350, 350 }, Enumerable.Range(0, 4).Select(g => font.AdvanceWidth(g)));
    }

    /// <summary>
    /// TN 5176, Appendix A sentences 2–16: the 391 standard strings — the bounds of their groups by SID, and a charset SID 34 names the glyph
    /// "A".
    /// </summary>
    [Fact]
    public void Cff02_StandardStrings_391WithTheirSids()
    {
        var s = FontTables.CffStandardStrings;
        Assert.Equal(391, s.Length);
        Assert.Equal((".notdef", "space", "asciitilde", "exclamdown", "germandbls", "zcaron"), (s[0], s[1], s[95], s[96], s[149], s[228]));
        Assert.Equal(("exclamsmall", "Ydieresissmall"), (s[229], s[378]));
        Assert.Equal(new[] { "001.000", "001.001", "001.002", "001.003" }, s[379..383]);
        Assert.Equal(new[] { "Black", "Bold", "Book", "Light", "Medium", "Regular", "Roman", "Semibold" }, s[383..391]);
        Assert.Equal(s.Length, s.Distinct().Count());
        var font = CffFont.Parse(Cff01Build(new Cff01Spec { Charset = [0, 0, 34], CharStrings = [[14], [14]] }));
        Assert.Equal("A", font.GlyphName(1));
    }

    /// <summary>
    /// TN 5176, Appendix B sentences 2–17: Standard Encoding has names at 32–126 (all) and within 161–251, none elsewhere; a CFF without
    /// operator 16 maps code 65 to its glyph "A".
    /// </summary>
    [Fact]
    public void Cff02_StandardEncoding_CodesAndDefault()
    {
        var e = FontTables.StandardEncoding;
        Assert.Equal(256, e.Length);
        for (var c = 0; c < 256; c++)
            if (c is >= 32 and <= 126) Assert.NotEqual("", e[c]);
            else if (c is < 161 or > 251) Assert.Equal("", e[c]);
        Assert.Equal(("space", "A", "quoteright", "quoteleft", "asciitilde", "exclamdown", "acute", "germandbls"),
            (e[32], e[65], e[0x27], e[0x60], e[126], e[161], e[194], e[251]));
        var font = CffFont.Parse(Cff01Build(new Cff01Spec { Charset = [0, 0, 34], CharStrings = [[14], [14]] }));
        Assert.False(Cff01Top(Cff01Build(new Cff01Spec { Charset = [0, 0, 34], CharStrings = [[14], [14]] })).Has(16));
        Assert.Equal(1, font.Encoding[65]);
    }

    /// <summary>
    /// TN 5176, Appendix H sentences 4–17 and 39–45: every one-byte operator 0–21 (12 escapes) and every two-byte operator 12 0 … 12 38 is read
    /// as its key with its operand.
    /// </summary>
    [Fact]
    public void Cff02_DictOperators_ReadAsTheirKeys()
    {
        for (var n = 0; n <= 21; n++)
            if (n != 12) Assert.Equal(new double[] { 1 }, CffFont.Dict.Parse([140, (byte)n]).Get(n));
        for (var n = 0; n <= 38; n++)
            Assert.Equal(new double[] { 1 }, CffFont.Dict.Parse([140, 12, (byte)n]).Get(1200 + n));
    }

    /// <summary>
    /// TN 5176, Appendix H sentences 4–17 and 43–45: after scrubbing the string operands still name their strings although the custom strings
    /// are numbered anew — FullName (2), PostScript (12 21), BaseFontName (12 22), FontName (12 38) and the registry and ordering of ROS.
    /// </summary>
    [Fact]
    public void Cff02_ScrubbedStringOperators_NameTheSameStrings()
    {
        string[] strings = ["weg", "Probe-Register", "Probe-Ordnung", "Voller Name", "/Probe true def", "Basis", "Schriftname"];
        var cff = Cff01Cid([[14], Cff01Cs("endchar"), Cff01Cs("endchar")], [0, 0, 0, 0],
            [new Cff01Fd([], [], Cff01Dict((1238, [397])))],
            charset: [0, 0x01, 0x87, 0, 2],   // glyph 1 "CID" 391 — not a string, so only the strings named by operators decide
            strings: strings, topExtra: Cff01Dict((2, [394]), (1221, [395]), (1222, [396])), ros: [392, 393, 0]);
        var scrubbed = CffScrubber.Scrub(cff, [2]).Data;
        var top = Cff01Top(scrubbed);
        Assert.Equal("Voller Name", Cff01String(scrubbed, top.Int(2)!.Value));
        Assert.Equal("/Probe true def", Cff01String(scrubbed, top.Int(1221)!.Value));
        Assert.Equal("Basis", Cff01String(scrubbed, top.Int(1222)!.Value));
        var ros = top.Get(1230)!;
        Assert.Equal(("Probe-Register", "Probe-Ordnung"), (Cff01String(scrubbed, (int)ros[0]), Cff01String(scrubbed, (int)ros[1])));
        var fontName = Cff01FontDictOf(scrubbed, 1).Font.Single(e => e.Op == 1238).Operands[0];
        Assert.Equal("Schriftname", Cff01String(scrubbed, (int)fontName));
        Assert.DoesNotContain("weg", Cff01Parts(scrubbed).Strings.Select(x => System.Text.Encoding.Latin1.GetString(x)));
    }
}
