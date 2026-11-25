using PdfKnead.Core.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package t2-01 (Adobe TN 5177, Type 2 charstrings): the missing evidence of the sentence-by-sentence draft as tests. Uses the helpers
/// of package cff-01 (M7Tests.Cff01.cs): <c>Cff01Cs</c> assembles a charstring, <c>Cff01Glyphs</c> builds a font around it.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>The outline of glyph 1 of a font with that charstring, as text.</summary>
    private static string T201Path(byte[] glyph, byte[][]? subrs = null, byte[][]? globalSubrs = null) =>
        Cff01Path(CffFont.Parse(Cff01Glyphs([glyph], subrs, globalSubrs)).Outline(1));

    /// <summary>The flattened charstring of glyph 1 (subset: glyph 1 stays glyph 1).</summary>
    private static byte[] T201Flattened(byte[] glyph, byte[][]? subrs = null, byte[][]? globalSubrs = null) =>
        Cff01CharStrings(CffSubsetter.Subset(Cff01Glyphs([glyph], subrs, globalSubrs), [1]))[1];

    /// <summary>A path "0 0 rmoveto …" begins with "M 0 0" and ends closed.</summary>
    private static string T201FromOrigin(string segments) => $"M 0 0 | {segments} | Z";

    /// <summary>
    /// TN 5177, 2.1 sentences 1–2: hint operators are counted (for the width and the length of masks) but draw nothing — with hstem and vstem
    /// before the path the width is the extra first number and the outline is the same as without them.
    /// </summary>
    [Fact]
    public void T201_StemHints_CountedForTheWidthOnly()
    {
        var font = CffFont.Parse(Cff01Glyphs([
            Cff01Cs(100, 10, 20, 30, 40, "hstem", 50, 60, "vstem", 0, 0, "rmoveto", 100, 0, "rlineto", "endchar"),
            Cff01Cs(100, 0, 0, "rmoveto", 100, 0, "rlineto", "endchar")]));
        Assert.Equal(100, font.AdvanceWidth(1));
        Assert.Equal(Cff01Path(font.Outline(2)), Cff01Path(font.Outline(1)));
    }

    /// <summary>
    /// TN 5177, 2.1 sentences 1–2, 2.2 sentence 7 and 4.3: the rebuild writes no hints and no flex — its charstrings contain only rmoveto,
    /// rlineto, rrcurveto and endchar.
    /// </summary>
    [Fact]
    public void T201_Builder_WritesOnlyMovesLinesCurvesAndEndchar()
    {
        var cff = CffBuilder.Build("Probe", Cff01Thousandth, [("a", Cff01Square(500), 600), ("b", Cff01Square(250), 300)]);
        var ops = Cff01CharStrings(cff).SelectMany(Cff01CsOps).Distinct().Order().ToList();
        Assert.Equal(new[] { 5, 8, 14, 21 }, ops);
    }

    /// <summary>
    /// TN 5177, 2.1 sentences 3–9 and 4.3 (stem hints, edge hints, overlapping stems, mask bits for no stem): hints only steer the rasterizer
    /// with hinting — PDF-Knead draws the outline the same as without them.
    /// </summary>
    [Fact]
    public void T201_StemHintsAndHintMasks_DoNotChangeTheOutline()
    {
        var plain = T201Path(Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", 0, 10, "rlineto", "endchar"));
        var hinted = T201Path(Cff01Cs(121, -21, 400, -20, "hstem", 10, 50, -30, 50, "vstemhm", "hintmask", new byte[] { 0xFF },
            0, 0, "rmoveto", 10, 0, "rlineto", "hintmask", new byte[] { 0x30 }, 0, 10, "rlineto", "endchar"));
        Assert.Equal(T201FromOrigin("L 10 0 | L 10 10"), plain);
        Assert.Equal(plain, hinted);
    }

    /// <summary>
    /// TN 5177, 2.1 sentences 10–15: a hintmask or cntrmask is followed by one bit per stem, rounded up to bytes — with nine stems two bytes,
    /// even when the second looks like a number (0x8B); flattening keeps both bytes.
    /// </summary>
    [Theory]
    [InlineData("hintmask")]
    [InlineData("cntrmask")]
    public void T201_Masks_LengthFromTheStemCount(string mask)
    {
        var glyph = Cff01Cs(1, 2, 3, 4, 5, 6, "hstemhm", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, "vstemhm", mask, new byte[] { 0xFF, 0x8B },
            10, 20, "rmoveto", 5, 0, "rlineto", "endchar");
        Assert.Equal("M 10 20 | L 15 20 | Z", T201Path(glyph));
        var flat = T201Flattened(glyph);
        Assert.True(flat.AsSpan().IndexOf(new byte[] { Cff01CsOperators[mask][0], 0xFF, 0x8B }) >= 0);
    }

    /// <summary>TN 5177, 2.1 sentences 17, 19–20, 29–30 and 4.3 sentences 34–36: counter masks steer hinting only — two of them leave the outline as it is.</summary>
    [Fact]
    public void T201_CounterMasks_DoNotChangeTheOutline()
    {
        var plain = T201Path(Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", 0, 10, "rlineto", "endchar"));
        var masked = T201Path(Cff01Cs(10, 20, "hstem", 30, 40, "vstem", "cntrmask", new byte[] { 0xC0 }, "cntrmask", new byte[] { 0x40 },
            0, 0, "rmoveto", 10, 0, "rlineto", 0, 10, "rlineto", "endchar"));
        Assert.Equal(plain, masked);
    }

    /// <summary>
    /// TN 5177, 2.2 sentences 1–2, 4.1 sentences 9–10, 48–51: flex draws two curves with its twelve relative points; the flex depth fd (50 or
    /// 1 000) only chooses between curve and line when hinting and does not change the shape.
    /// </summary>
    [Fact]
    public void T201_Flex_AlwaysTwoCurves()
    {
        const string expected = "M 0 0 | C 10 20 40 60 90 120 | C 160 200 250 300 360 420 | Z";
        foreach (var fd in new[] { 50, 1000 })
            Assert.Equal(expected, T201Path(Cff01Cs(0, 0, "rmoveto", 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, fd, "flex", "endchar")));
    }

    /// <summary>
    /// TN 5177, 2.2 sentences 3–4 and 4.1 sentences 62–65: hflex keeps both curves horizontal at their ends (the second goes down by dy2
    /// again), hflex1 ends on the height of its start.
    /// </summary>
    [Fact]
    public void T201_HorizontalFlex_EndsOnTheStartHeight()
    {
        Assert.Equal("M 0 0 | C 10 0 30 5 60 5 | C 100 5 150 0 210 0 | Z",
            T201Path(Cff01Cs(0, 0, "rmoveto", 10, 20, 5, 30, 40, 50, 60, "hflex", "endchar")));
        Assert.Equal("M 0 0 | C 10 5 30 15 60 15 | C 100 15 95 10 145 0 | Z",
            T201Path(Cff01Cs(0, 0, "rmoveto", 10, 5, 20, 10, 30, 40, -5, -5, 50, "hflex1", "endchar")));
    }

    /// <summary>
    /// TN 5177, 2.2 sentences 3–4 and 4.1 sentences 66–70: flex1 takes its last value along the dominant direction of the summed steps and
    /// returns to the start in the other — horizontally (end y = start y) and vertically (end x = start x).
    /// </summary>
    [Fact]
    public void T201_Flex1_LastValueAlongTheDominantDirection()
    {
        Assert.Equal("M 0 0 | C 10 1 20 2 30 3 | C 40 2 50 1 70 0 | Z",
            T201Path(Cff01Cs(0, 0, "rmoveto", 10, 1, 10, 1, 10, 1, 10, -1, 10, -1, 20, "flex1", "endchar")));
        Assert.Equal("M 0 0 | C 1 10 2 20 3 30 | C 2 40 1 50 0 70 | Z",
            T201Path(Cff01Cs(0, 0, "rmoveto", 1, 10, 1, 10, 1, 10, -1, 10, -1, 10, 20, "flex1", "endchar")));
    }

    /// <summary>
    /// TN 5177, 2.3 sentence 10, 4.7 sentence 12 and Appendix B: subroutines nest at most ten levels — a chain of ten (local or global) with a
    /// line in the innermost is drawn and flattened; with eleven the glyph is empty with defaultWidthX and flattening is a format error.
    /// </summary>
    [Fact]
    public void T201_SubroutineNesting_AtMostTenLevels()
    {
        foreach (var global in new[] { false, true })
        {
            var call = global ? "callgsubr" : "callsubr";
            byte[] Font(int levels)
            {
                var subrs = Enumerable.Range(0, levels)
                    .Select(k => k < levels - 1 ? Cff01Cs(k + 1 - 107, call, "return") : Cff01Cs(10, 0, "rlineto", "return")).ToArray();
                var glyph = Cff01Cs(0, 0, "rmoveto", -107, call, "endchar");
                return global ? Cff01Glyphs([glyph], globalSubrs: subrs, defaultWidth: 333) : Cff01Glyphs([glyph], subrs: subrs, defaultWidth: 333);
            }
            Assert.Equal("M 0 0 | L 10 0 | Z", Cff01Path(CffFont.Parse(Font(10)).Outline(1)));
            Assert.Equal("M 0 0 | L 10 0 | Z", Cff01Path(CffFont.Parse(CffSubsetter.Subset(Font(10), [1])).Outline(1)));
            var deep = CffFont.Parse(Font(11));
            Assert.True(deep.Outline(1).IsEmpty);
            Assert.Equal(333, deep.AdvanceWidth(1));
            Assert.Throws<FontFormatException>(() => CffSubsetter.Subset(Font(11), [1]));
        }
    }

    /// <summary>
    /// TN 5177, 2.3 sentences 11–12 and 4.2 sentence 5: a subroutine ends with return or endchar; endchar in a subroutine ends the glyph —
    /// what follows the call is not drawn, and the flattened charstring ends with that endchar.
    /// </summary>
    [Fact]
    public void T201_EndcharInASubroutine_EndsTheGlyph()
    {
        var glyph = Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", -107, "callsubr", 99, 99, "rlineto");
        byte[][] subrs = [Cff01Cs(0, 10, "rlineto", "endchar")];
        Assert.Equal(T201FromOrigin("L 10 0 | L 10 10"), T201Path(glyph, subrs));
        var flat = T201Flattened(glyph, subrs);
        Assert.Equal(new[] { 21, 5, 5, 14 }, Cff01CsOps(flat));
    }

    /// <summary>
    /// TN 5177, 3 sentences 4–9, 4 sentences 6–8 and Appendix B: operators take their arguments from the bottom of the stack, the stack
    /// survives calls and returns, path operators clear it; it holds 48 numbers — a 49th or 60th number is dropped without an error.
    /// </summary>
    [Fact]
    public void T201_ArgumentStack_FromTheBottomThroughCallsAtMost48()
    {
        Assert.Equal(T201FromOrigin("L 5 7"), T201Path(Cff01Cs(0, 0, "rmoveto", 5, 7, -107, "callsubr", "endchar"), [Cff01Cs("rlineto", "return")]));
        Assert.Equal("M 7 0 | L 8 0 | Z", T201Path(Cff01Cs(3, 4, "add", 0, "rmoveto", 1, 0, "rlineto", "endchar")));
        Assert.Equal(T201FromOrigin("L 10 0"), T201Path(Cff01Cs(0, 0, "rmoveto", 10, "hlineto", "endchar")));
        Assert.Equal(T201FromOrigin("L 10 0 | L 10 10 | L 0 10"), T201Path(Cff01Cs(0, 0, "rmoveto", 10, 0, 0, 10, -10, 0, "rlineto", "endchar")));
        foreach (var count in new[] { 49, 60 })
        {
            var outline = CffFont.Parse(Cff01Glyphs([Cff01Cs([0, 0, "rmoveto", .. Enumerable.Repeat<object>(1, count), "hlineto", "endchar"])])).Outline(1);
            Assert.Equal(48, outline.Verbs.Count(v => v == Core.Pdf.Graphics.PathVerb.Line));
            Assert.Equal((24f, 24f), (outline.Points[^2], outline.Points[^1]));
        }
    }

    /// <summary>
    /// TN 5177, 3.1 sentences 1–3 and TN 5176, Appendix H sentences 18–23: the width is nominalWidthX plus the first number, without one
    /// defaultWidthX — 400/500: 100 → 600, none → 400; 500/600: none → 500, 10 → 610.
    /// </summary>
    [Fact]
    public void T201_Width_NominalPlusFirstNumberElseDefault()
    {
        var first = CffFont.Parse(Cff01Glyphs([Cff01Cs(100, 0, 0, "rmoveto", 10, 0, "rlineto", "endchar"), Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar")],
            defaultWidth: 400, nominalWidth: 500));
        Assert.Equal((600.0, 400.0), (first.AdvanceWidth(1), first.AdvanceWidth(2)));
        var second = CffFont.Parse(Cff01Glyphs([Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar"), Cff01Cs(10, 0, 0, "rmoveto", 10, 0, "rlineto", "endchar")],
            defaultWidth: 500, nominalWidth: 600));
        Assert.Equal((500.0, 610.0), (second.AdvanceWidth(1), second.AdvanceWidth(2)));
    }

    /// <summary>
    /// TN 5177, 3.1 sentences 8–9: a number or a mask must not run past the end of its charstring — a subroutine ending in the middle of a
    /// number, or right after hintmask without its mask bytes: flattening refuses it, the reader draws without failing.
    /// </summary>
    [Fact]
    public void T201_TruncatedNumberOrMask_RefusedWhenFlattened()
    {
        var number = Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", -107, "callsubr", "endchar");
        byte[][] cut = [[247]];
        Assert.Equal(T201FromOrigin("L 10 0"), T201Path(number, cut));
        Assert.Throws<FontFormatException>(() => T201Flattened(number, cut));
        var mask = Cff01Cs(0, 10, "hstem", -107, "callsubr", 0, 0, "rmoveto", 10, 0, "rlineto", "endchar");
        byte[][] bareMask = [[19]];
        Assert.Equal(T201FromOrigin("L 10 0"), T201Path(mask, bareMask));
        Assert.Throws<FontFormatException>(() => T201Flattened(mask, bareMask));
    }

    /// <summary>
    /// TN 5177, 3.3 sentences 1–6 and Appendix A: a reserved operator does nothing but clear the stack — "0 0 rmoveto 5 5 [reserved] 10 0
    /// rlineto" draws one line to (10, 0); flattening copies a reserved one-byte operator and refuses a reserved two-byte one.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 2 })]
    [InlineData(new byte[] { 9 })]
    [InlineData(new byte[] { 13 })]
    [InlineData(new byte[] { 15 })]
    [InlineData(new byte[] { 16 })]
    [InlineData(new byte[] { 17 })]
    [InlineData(new byte[] { 12, 1 })]
    [InlineData(new byte[] { 12, 2 })]
    [InlineData(new byte[] { 12, 6 })]
    [InlineData(new byte[] { 12, 13 })]
    [InlineData(new byte[] { 12, 19 })]
    [InlineData(new byte[] { 12, 25 })]
    [InlineData(new byte[] { 12, 33 })]
    [InlineData(new byte[] { 12, 38 })]
    [InlineData(new byte[] { 12, 255 })]
    public void T201_ReservedOperators_ClearTheStackOnly(byte[] reserved)
    {
        var glyph = Cff01Cs(0, 0, "rmoveto", 5, 5, reserved, 10, 0, "rlineto", "endchar");
        Assert.Equal(T201FromOrigin("L 10 0"), T201Path(glyph));
        if (reserved.Length == 1)
            Assert.Equal(T201FromOrigin("L 10 0"), Cff01Path(CffFont.Parse(CffSubsetter.Subset(Cff01Glyphs([glyph]), [1])).Outline(1)));
        else
            Assert.Throws<FontFormatException>(() => T201Flattened(glyph));
    }

    /// <summary>TN 5177, 4.1 sentences 5–6, 8, 23–33: line operators draw one segment per set of arguments; hlineto and vlineto alternate direction.</summary>
    [Fact]
    public void T201_LineOperators_OneSegmentPerArgumentSet()
    {
        Assert.Equal(T201FromOrigin("L 10 0 | L 10 10 | L 0 10"), T201Path(Cff01Cs(0, 0, "rmoveto", 10, 0, 0, 10, -10, 0, "rlineto", "endchar")));
        Assert.Equal(T201FromOrigin("L 10 0 | L 10 20 | L 40 20"), T201Path(Cff01Cs(0, 0, "rmoveto", 10, 20, 30, "hlineto", "endchar")));
        Assert.Equal(T201FromOrigin("L 0 10 | L 20 10 | L 20 40 | L 60 40"), T201Path(Cff01Cs(0, 0, "rmoveto", 10, 20, 30, 40, "vlineto", "endchar")));
    }

    /// <summary>
    /// TN 5177, 4.1 sentences 5–6, 8, 34–49: curve operators draw one curve per set of arguments; the short forms put their missing values to 0
    /// (control points level with or above their neighbours), the optional first or last value applies to the first or last curve only;
    /// rcurveline ends with a line, rlinecurve with a curve.
    /// </summary>
    [Fact]
    public void T201_CurveOperators_ShortFormsAndSequences()
    {
        (object[] Args, string Op, string Expected)[] cases =
        [
            ([10, 0, 10, 10, 0, 10, 10, 0, 10, 10, 0, 10], "rrcurveto", "C 10 0 20 10 20 20 | C 30 20 40 30 40 40"),
            ([5, 10, 20, 30, 40], "hhcurveto", "C 10 5 30 35 70 35"),
            ([5, 10, 20, 30, 40, 10, 20, 30, 40], "hhcurveto", "C 10 5 30 35 70 35 | C 80 35 100 65 140 65"),
            ([10, 20, 30, 40, 10, 20, 30, 40], "hhcurveto", "C 10 0 30 30 70 30 | C 80 30 100 60 140 60"),
            ([5, 10, 20, 30, 40], "vvcurveto", "C 5 10 25 40 25 80"),
            ([10, 20, 30, 40, 10, 20, 30, 40], "vvcurveto", "C 0 10 20 40 20 80 | C 20 90 40 120 40 160"),
            ([10, 20, 30, 40], "hvcurveto", "C 10 0 30 30 30 70"),
            ([10, 20, 30, 40, 50, 60, 70, 80], "hvcurveto", "C 10 0 30 30 30 70 | C 30 120 90 190 170 190"),
            ([10, 20, 30, 40, 50], "hvcurveto", "C 10 0 30 30 80 70"),
            ([10, 20, 30, 40], "vhcurveto", "C 0 10 20 40 60 40"),
            ([10, 0, 10, 10, 0, 10, 5, 5], "rcurveline", "C 10 0 20 10 20 20 | L 25 25"),
            ([5, 5, 10, 0, 10, 10, 0, 10], "rlinecurve", "L 5 5 | C 15 5 25 15 25 25"),
        ];
        foreach (var (args, op, expected) in cases)
            Assert.Equal(T201FromOrigin(expected), T201Path(Cff01Cs([0, 0, "rmoveto", .. args, op, "endchar"])));
    }

    /// <summary>TN 5177, 4.1 sentence 14: a moveto closes the open subpath; the current point stays the last point, so the next move is relative to it.</summary>
    [Fact]
    public void T201_MoveTo_ClosesTheOpenSubpath()
    {
        Assert.Equal("M 0 0 | L 100 0 | L 100 100 | Z | M 110 110 | L 160 110 | Z",
            T201Path(Cff01Cs(0, 0, "rmoveto", 100, 0, "rlineto", 0, 100, "rlineto", 10, 10, "rmoveto", 50, 0, "rlineto", "endchar")));
    }

    /// <summary>
    /// TN 5177, 4.1 sentences 17–18, 20, 22: the width may precede the first stack-clearing operator — hstem (odd count), hintmask, rmoveto,
    /// hmoveto, vmoveto, endchar; it gives nominalWidthX + 100 = 600 and is no coordinate; without it defaultWidthX (300).
    /// </summary>
    [Fact]
    public void T201_Width_BeforeEachFirstOperator()
    {
        (byte[] Glyph, double Width, string Path)[] cases =
        [
            (Cff01Cs(100, 10, 20, "hstem", 0, 0, "rmoveto", 5, 0, "rlineto", "endchar"), 600, "M 0 0 | L 5 0 | Z"),
            (Cff01Cs(100, 10, 20, "hintmask", new byte[] { 0x80 }, 10, 20, "rmoveto", 5, 0, "rlineto", "endchar"), 600, "M 10 20 | L 15 20 | Z"),
            (Cff01Cs(100, 10, 20, "rmoveto", 5, 0, "rlineto", "endchar"), 600, "M 10 20 | L 15 20 | Z"),
            (Cff01Cs(100, 30, "hmoveto", 5, 0, "rlineto", "endchar"), 600, "M 30 0 | L 35 0 | Z"),
            (Cff01Cs(100, 40, "vmoveto", 5, 0, "rlineto", "endchar"), 600, "M 0 40 | L 5 40 | Z"),
            (Cff01Cs(100, "endchar"), 600, ""),
            (Cff01Cs(10, 20, "rmoveto", 5, 0, "rlineto", "endchar"), 300, "M 10 20 | L 15 20 | Z"),
        ];
        var font = CffFont.Parse(Cff01Glyphs([.. cases.Select(c => c.Glyph)], defaultWidth: 300, nominalWidth: 500));
        for (var i = 0; i < cases.Length; i++)
        {
            Assert.Equal(cases[i].Width, font.AdvanceWidth(i + 1));
            Assert.Equal(cases[i].Path, Cff01Path(font.Outline(i + 1)));
        }
    }

    /// <summary>TN 5177, 4.1 sentences 19, 21: hmoveto moves horizontally, vmoveto vertically, each from the current point.</summary>
    [Fact]
    public void T201_HorizontalAndVerticalMoveTo()
    {
        Assert.Equal("M 30 0 | L 40 0 | Z | M 40 40 | L 40 50 | Z",
            T201Path(Cff01Cs(30, "hmoveto", 10, 0, "rlineto", 40, "vmoveto", 0, 10, "rlineto", "endchar")));
    }

    /// <summary>
    /// TN 5177, 4.2 sentences 7–9: endchar may carry the width — the rebuild writes an empty glyph of advance 278 as "278 endchar" and one of
    /// advance 0 as endchar alone; at nominalWidthX 500 "−222 endchar" has width 278.
    /// </summary>
    [Fact]
    public void T201_Endchar_CarriesTheWidth()
    {
        var built = CffBuilder.Build("Probe", Cff01Thousandth, [("e", GlyphOutline.Empty, 278), ("z", GlyphOutline.Empty, 0)]);
        var charStrings = Cff01CharStrings(built);
        Assert.Equal(new byte[] { 247, 170, 14 }, charStrings[1]);
        Assert.Equal(new byte[] { 14 }, charStrings[2]);
        var font = CffFont.Parse(built);
        Assert.Equal((278.0, 0.0), (font.AdvanceWidth(1), font.AdvanceWidth(2)));
        Assert.True(font.Outline(1).IsEmpty);
        Assert.Equal(278, CffFont.Parse(Cff01Glyphs([Cff01Cs(-222, "endchar")], nominalWidth: 500)).AdvanceWidth(1));
    }

    /// <summary>
    /// TN 5177, 4.3 (hstemhm/vstemhm and the stems implied before hintmask): "280 100 −70 40 hstemhm 400 50 hintmask" makes three stems and
    /// one mask byte; with hstem the same — the outline after the mask begins at (10, 20).
    /// </summary>
    [Fact]
    public void T201_StemsBeforeHintMask_CountedForTheMask()
    {
        foreach (var stem in new[] { "hstemhm", "hstem" })
        {
            var glyph = Cff01Cs(280, 100, -70, 40, stem, 400, 50, "hintmask", new byte[] { 0xA0 }, 10, 20, "rmoveto", 5, 0, "rlineto", "endchar");
            Assert.Equal("M 10 20 | L 15 20 | Z", T201Path(glyph));
            Assert.True(T201Flattened(glyph).AsSpan().IndexOf(new byte[] { 19, 0xA0, 149, 159 }) >= 0);
        }
    }

    /// <summary>
    /// TN 5177, 4.4 sentences 1–9 and Appendix A: abs, add, sub, div, neg, mul, sqrt, drop, exch and dup compute on the stack; a chain of
    /// "dup mul" stays finite. (random, index, roll and the conditional operators are not tested: open question of the user.)
    /// </summary>
    [Fact]
    public void T201_ArithmeticOperators_ComputeOnTheStack()
    {
        (object[] Code, string Start)[] cases =
        [
            ([10, 3, "sub", 0], "M 7 0"), ([3, 4, "add", 0], "M 7 0"), ([3, 4, "mul", 0], "M 12 0"), ([12, 4, "div", 0], "M 3 0"),
            ([5, "neg", 0], "M -5 0"), ([-5, "abs", 0], "M 5 0"), ([16, "sqrt", 0], "M 4 0"), ([7, 99, "drop", 0], "M 7 0"),
            ([1, 2, "exch"], "M 2 1"), ([7, "dup", "add", 0], "M 14 0"),
        ];
        foreach (var (code, start) in cases)
            Assert.StartsWith(start + " | L", T201Path(Cff01Cs([.. code, "rmoveto", 1, 0, "rlineto", "endchar"])));
        var chain = new List<object> { 2 };
        for (var i = 0; i < 12; i++) chain.AddRange(["dup", "mul"]);
        var bounds = CffFont.Parse(Cff01Glyphs([Cff01Cs([.. chain, 0, "rmoveto", 1, 0, "rlineto", "endchar"])])).Outline(1).Bounds;
        Assert.True(double.IsFinite(bounds.Left) && double.IsFinite(bounds.Right) && bounds.Right <= 32769, $"{bounds.Left} {bounds.Right}");
    }

    /// <summary>
    /// TN 5177, 4.5 sentences 1–6 and Appendix B: the transient array has 32 entries, new for each glyph and 0 at first — put then get gives the
    /// value back, the next glyph reads 0; an index outside 0–31 changes nothing and is no error.
    /// </summary>
    [Fact]
    public void T201_TransientArray_PerGlyph()
    {
        var font = CffFont.Parse(Cff01Glyphs([
            Cff01Cs(25, 3, "put", 3, "get", 0, "rmoveto", 1, 0, "rlineto", "endchar"),
            Cff01Cs(3, "get", 0, "rmoveto", 1, 0, "rlineto", "endchar"),
            Cff01Cs(5, 40, "put", 5, 32, "put", 32, "get", 0, "rmoveto", 1, 0, "rlineto", "endchar")]));
        Assert.Equal("M 25 0 | L 26 0 | Z", Cff01Path(font.Outline(1)));
        Assert.Equal("M 0 0 | L 1 0 | Z", Cff01Path(font.Outline(2)));
        Assert.Equal("M 0 0 | L 1 0 | Z", Cff01Path(font.Outline(3)));
    }

    /// <summary>TN 5177, 4.7 sentence 10: a call of a subroutine that does not exist — the reader passes over it and draws the rest, flattening refuses it.</summary>
    [Fact]
    public void T201_MissingSubroutine_SkippedByTheReaderRefusedWhenFlattened()
    {
        var glyph = Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", 50, "callsubr", 0, 10, "rlineto", "endchar");
        byte[][] subrs = [Cff01Cs(0, 99, "rlineto", "return")];   // only number 0; 50 + 107 = 157 is missing
        Assert.Equal(T201FromOrigin("L 10 0 | L 10 10"), T201Path(glyph, subrs));
        Assert.Throws<FontFormatException>(() => T201Flattened(glyph, subrs));
    }

    /// <summary>TN 5177, 4.7 sentence 12: callgsubr runs a global subroutine and return goes back to the caller.</summary>
    [Fact]
    public void T201_GlobalSubroutine_CalledAndReturns()
    {
        Assert.Equal(T201FromOrigin("L 10 0 | L 10 10"),
            T201Path(Cff01Cs(0, 0, "rmoveto", -107, "callgsubr", 0, 10, "rlineto", "endchar"), globalSubrs: [Cff01Cs(10, 0, "rlineto", "return")]));
    }

    /// <summary>TN 5177, Appendix B: a charstring has at most 65 535 bytes — the rebuild refuses an outline that would need more.</summary>
    [Fact]
    public void T201_BuiltCharstring_AtMost65535Bytes()
    {
        var outline = new GlyphOutline();
        outline.MoveTo(0.5f, 0.5f);
        // steps with fractions in both directions: each a 5-byte number, some 77 000 bytes in all
        for (var i = 1; i <= 7000; i++) outline.LineTo(i * 1.37f, (i % 3) * 0.41f);
        outline.Finish();
        Assert.ThrowsAny<Exception>(() => CffBuilder.Build("Probe", Cff01Thousandth, [("lang", outline, 500)]));
    }

    /// <summary>TN 5177, Appendix C sentences 3–4: dotsection is deprecated and does nothing — the outline is the same as without it.</summary>
    [Fact]
    public void T201_Dotsection_DoesNothing()
    {
        Assert.Equal(T201Path(Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", "endchar")),
            T201Path(Cff01Cs(0, 0, "rmoveto", "dotsection", 10, 0, "rlineto", "endchar")));
    }

    /// <summary>A font with "A" (glyph 1), "acute" (glyph 2) and a third glyph; a custom encoding puts code 65 on glyph 3, 66 on A, 67 on acute.</summary>
    private static byte[] T201SeacFont(byte[] third, byte[]? first = null)
    {
        var acute = Array.IndexOf(FontTables.CffStandardStrings, "acute");
        var aacute = Array.IndexOf(FontTables.CffStandardStrings, "Aacute");
        return Cff01Build(new Cff01Spec
        {
            Charset = [0, 0, 34, (byte)(acute >> 8), (byte)acute, (byte)(aacute >> 8), (byte)aacute],
            Encoding = [0, 3, 66, 67, 65],
            CharStrings = [[14], first ?? Cff01Cs(0, 0, "rmoveto", 100, 0, "rlineto", 0, 100, "rlineto", "endchar"),
                           Cff01Cs(0, 0, "rmoveto", 10, 0, "rlineto", 0, 10, "rlineto", "endchar"), third],
        });
    }

    /// <summary>
    /// TN 5177, Appendix C sentences 3–10: endchar with four arguments builds an accented character from the glyphs that Standard Encoding
    /// names for the two codes (not the font's own encoding): "A" at the origin and "acute" moved by (adx, ady); scrubbing keeps both parts.
    /// </summary>
    [Fact]
    public void T201_Seac_ComponentsByStandardEncoding()
    {
        var plain = T201SeacFont(Cff01Cs(0, 0, 65, 194, "endchar"));
        var font = CffFont.Parse(plain);
        Assert.Equal(3, font.Encoding[65]);
        Assert.Equal("M 0 0 | L 100 0 | L 100 100 | Z | M 0 0 | L 10 0 | L 10 10 | Z", Cff01Path(font.Outline(3)));
        var moved = T201SeacFont(Cff01Cs(100, 0, 65, 194, "endchar"));
        Assert.Equal("M 0 0 | L 100 0 | L 100 100 | Z | M 100 0 | L 110 0 | L 110 10 | Z", Cff01Path(CffFont.Parse(moved).Outline(3)));
        var (data, keep) = CffScrubber.Scrub(moved, [3]);
        Assert.Superset(new HashSet<int> { 1, 2, 3 }, keep);
        var scrubbed = CffFont.Parse(data);
        Assert.False(scrubbed.Outline(1).IsEmpty || scrubbed.Outline(2).IsEmpty);
        Assert.Equal(Cff01Path(CffFont.Parse(moved).Outline(3)), Cff01Path(scrubbed.Outline(3)));
    }

    private const string T201Acute = "M 0 0 | L 10 0 | L 10 10 | Z";

    /// <summary>
    /// TN 5177, Appendix C sentence 11: the parts of an accented character must not use seac themselves. PDF-Knead reads such fonts leniently
    /// but bounded: a base glyph that is itself built with seac (from two accents) is drawn with its parts, and a base glyph that names itself
    /// as its base, together with a glyph built on it, finishes in finite time.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public async Task T201_Seac_NestedAndSelfReferring_Bounded()
    {
        // "A" built from acute + acute, glyph 3 from "A" + acute: three accents
        var nested = CffFont.Parse(T201SeacFont(Cff01Cs(0, 0, 65, 194, "endchar"), first: Cff01Cs(0, 0, 194, 194, "endchar")));
        Assert.Equal(string.Join(" | ", Enumerable.Repeat(T201Acute, 3)), Cff01Path(nested.Outline(3)));
        var cff = T201SeacFont(Cff01Cs(0, 0, 65, 194, "endchar"), first: Cff01Cs(0, 0, 65, 194, "endchar"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var task = Task.Run(() =>
        {
            var font = CffFont.Parse(cff);
            return (Cff01Path(font.Outline(1)), Cff01Path(font.Outline(3)));
        });
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2 * TestKind.Factor))));
        var (_, onSelf) = await task;
        Assert.Contains(T201Acute, onSelf);
        Assert.True(watch.ElapsedMilliseconds < 2000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// TN 5177, Appendix C sentence 11 (lenient reading as the draft describes it): a glyph that names itself as its base is drawn up to the
    /// nesting limit — at least its accent. Today it is empty: the innermost call, cut off at the limit, stores its empty outline in the glyph
    /// cache first, and every outer level then returns that stored outline instead of its own.
    /// </summary>
    [Fact]
    public void T201_Seac_SelfReferring_DrawnUpToTheLimit()
    {
        var font = CffFont.Parse(T201SeacFont(Cff01Cs(0, 0, 65, 194, "endchar"), first: Cff01Cs(0, 0, 65, 194, "endchar")));
        Assert.Contains(T201Acute, Cff01Path(font.Outline(1)));
    }
}
