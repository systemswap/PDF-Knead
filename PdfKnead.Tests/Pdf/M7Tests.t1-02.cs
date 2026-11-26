using System.Diagnostics;
using System.Text.RegularExpressions;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Tests.Pdf;

/// <summary>Konformitätsabgleich M7 (text and fonts), package t1-02: Type 1 Font Format, chapters 4–6 (prefix T1).</summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// T1 4.1 sentences 1–2: a half circle as a single rrcurveto without an end point at its extreme stays one cubic curve with the points of the
    /// charstring; the bounds are the smallest rectangle around the curve, its top at 0.75 × 133 (ISO 32000-2, 9.2.4; FG 1153 — before the
    /// control points).
    /// </summary>
    [Fact]
    public void Type1_HalfCircleAsOneCurve_KeepsItsPoints()
    {
        var font = T1Paket.WithGlyph("0 600 hsbw 100 0 rmoveto 0 133 200 0 0 -133 rrcurveto closepath endchar");
        var outline = font.Outline("A");
        Assert.Equal([PathVerb.Move, PathVerb.Cubic, PathVerb.Close], outline.Verbs);
        Assert.Equal([(100f, 0f), (100f, 133f), (300f, 133f), (300f, 0f)], T1Paket.Points(outline));
        Assert.Equal(99.75, outline.Bounds.Bottom, 3);
    }

    /// <summary>
    /// T1 4.2 sentences 3–4: a join from a line to a curve whose control point lies two units off the line keeps its points exactly as written —
    /// the reader smooths nothing.
    /// </summary>
    [Fact]
    public void Type1_KinkedJoin_KeepsThePointsAsWritten()
    {
        var font = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 100 0 rlineto 50 2 50 98 0 100 rrcurveto closepath endchar");
        Assert.Equal([(0f, 0f), (100f, 0f), (150f, 2f), (200f, 100f), (200f, 200f)], T1Paket.Points(font.Outline("A")));
    }

    /// <summary>
    /// T1 5.1 sentences 6–7, 5.2–5.11 and 5.14, Appendix 1 sentences 4–12, 14–33, 37–38, 44 and 47–51: the font-wide hints of the private part
    /// (BlueValues, OtherBlues, FamilyBlues, FamilyOtherBlues, BlueScale, BlueShift, BlueFuzz, StdHW, StdVW, StemSnapH, StemSnapV, ForceBold,
    /// LanguageGroup, RndStemUp, ExpansionFactor, MinFeature, password — also against their rules) are read over: the glyph has the same points,
    /// verbs and width as without them; cleaning copies them unchanged.
    /// </summary>
    [Theory]
    [InlineData("/BlueValues [-15 0 700 715] def\n", "0 50 hstem 0 50 vstem")]
    [InlineData("/BlueValues [-15 0 700 715] def\n/OtherBlues [-250 -240] def\n", "")]
    [InlineData("/BlueValues [0 -15 700 701 702 703] def\n", "")]
    [InlineData("/OtherBlues [-250 -240 -20 -10] def\n", "")]
    [InlineData("/FamilyBlues [-15 0 700 715] def\n/FamilyOtherBlues [-250 -240] def\n", "")]
    [InlineData("/BlueScale .04379 def\n/BlueValues [-15 0 700 723] def\n", "")]
    [InlineData("/BlueShift 7 def\n", "")]
    [InlineData("/BlueFuzz 0 def\n", "")]
    [InlineData("/StdHW [32] def\n/StdVW [85] def\n/StemSnapH [41 32] def\n", "")]
    [InlineData("/ForceBold true def\n", "")]
    [InlineData("/LanguageGroup 1 def\n/RndStemUp false def\n", "")]
    [InlineData("/LanguageGroup 1 def\n/ExpansionFactor 0.5 def\n", "")]
    [InlineData("/BlueValues [-15 0 700 715] def\n/BlueScale 0.039 def\n/BlueShift 7 def\n/BlueFuzz 1 def\n/ExpansionFactor 0.06 def\n", "")]
    [InlineData("/FamilyBlues [-15 0 700 715] def\n/FamilyOtherBlues [-250 -240] def\n/ForceBold true def\n/LanguageGroup 1 def\n", "")]
    [InlineData("/MinFeature {16 16} def\n", "")]
    [InlineData("/password 5839 def\n/RndStemUp true def\n/StdHW [32] def\n/StdVW [85] def\n/StemSnapH [32 36] def\n/StemSnapV [85 90] def\n", "")]
    public void Type1_PrivateHintEntries_AreReadOverAndKeptByCleaning(string entries, string hints)
    {
        var b = ("B", "0 600 hsbw 50 50 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        var plain = T1Paket.Parse(new T1Paket.Spec { Glyphs = [(".notdef", T1Paket.NotDef), ("A", T1Paket.Triangle), b] });
        var program = T1Paket.Build(new T1Paket.Spec
        {
            PrivateExtra = entries,
            Glyphs = [(".notdef", T1Paket.NotDef), ("A", $"50 600 hsbw {hints} 0 0 rmoveto 250 700 rlineto 250 -700 rlineto closepath endchar"), b],
        });
        T1Paket.SameGlyph(plain, Type1Font.Parse(program.Data), "A");
        var (data, clear, encrypted, _) = Type1Scrubber.Scrub(program.Data, new HashSet<string> { "A" }, program.Clear, program.Encrypted);
        Assert.Contains(entries, T1Paket.Private(data, clear, encrypted));
        T1Paket.SameGlyph(plain, Type1Font.Parse(data), "A");
    }

    /// <summary>
    /// T1 5.12 sentences 2–4, 7.3 sentences 1–5, Appendix 1 sentences 13, 34–35: lenIV — without an entry four leading bytes, otherwise as given
    /// (0, 1, 2, 4); the glyphs read the same, and cleaning writes the emptied subroutine with that many leading bytes plus "return" and reads the
    /// same afterwards.
    /// </summary>
    [Theory]
    [InlineData(null, 5)]
    [InlineData(4, 5)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    public void Type1_LenIV_DefaultAndOtherValues(int? lenIV, int emptyLength)
    {
        var reference = T1Paket.Parse();
        var program = T1Paket.Build(new T1Paket.Spec { LenIV = lenIV });
        Assert.Equal(lenIV is not null, T1Paket.Private(program.Data, program.Clear, program.Encrypted).Contains("/lenIV"));
        var font = Type1Font.Parse(program.Data);
        foreach (var name in (string[])["A", "B", "C"]) T1Paket.SameGlyph(reference, font, name);
        var (data, clear, encrypted, _) = Type1Scrubber.Scrub(program.Data, new HashSet<string> { "B" }, program.Clear, program.Encrypted);
        Assert.Contains($"dup 4 {emptyLength} RD ", T1Paket.Private(data, clear, encrypted));
        T1Paket.SameGlyph(reference, Type1Font.Parse(data), "B");
    }

    /// <summary>
    /// T1 6.1 sentence 6: the stack holds 24 numbers in the specification, 64 in the reader — with 30 numbers before rlineto the bottom two are dx
    /// and dy; with 70 there is no error, the numbers beyond 64 are dropped.
    /// </summary>
    [Fact]
    public void Type1_OverfullStack_KeepsTheBottomNumbers()
    {
        foreach (var count in (int[])[30, 70])
        {
            var font = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 10 20 " + string.Concat(Enumerable.Repeat("1 ", count - 2)) +
                "rlineto 0 100 rlineto closepath endchar");
            Assert.Equal([(0f, 0f), (10f, 20f), (10f, 120f)], T1Paket.Points(font.Outline("A")));
        }
    }

    /// <summary>T1 6.2 sentence 6: bytes 251–254 with the next byte w give −(v − 251) × 256 − w − 108 — "-108 -1131 rmoveto" starts at (−108, −1131).</summary>
    [Fact]
    public void Type1_TwoByteNegativeNumbers()
    {
        Assert.Equal([251, 0, 254, 255], T1Paket.Charstring("-108 -1131"));
        var font = T1Paket.WithGlyph("0 600 hsbw -108 -1131 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        Assert.Equal((-108f, -1131f), T1Paket.Points(font.Outline("A"))[0]);
    }

    /// <summary>
    /// T1 6.2 sentences 7–9: byte 255 is followed by a signed 32-bit number, most significant byte first — a width of 600 and a dx of −2000 written so
    /// give width 600 and a first point at x = −2000.
    /// </summary>
    [Fact]
    public void Type1_FiveByteNumbers()
    {
        Assert.Equal([255, 0, 0, 2, 88, 255, 255, 255, 248, 48], T1Paket.Charstring("L600 L-2000"));
        var font = T1Paket.WithGlyph("0 L600 hsbw L-2000 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        Assert.Equal(600, font.AdvanceWidth("A"));
        Assert.Equal((-2000f, 0f), T1Paket.Points(font.Outline("A"))[0]);
    }

    /// <summary>
    /// T1 6.2 sentence 10: numbers above 32 000 bind the program to a following div; the reader takes them as they are — "0 40000 hsbw" gives
    /// width 40 000, "64000 2 div 0 rmoveto" a first point at x = 32 000.
    /// </summary>
    [Fact]
    public void Type1_LargeNumbersWithAndWithoutDiv()
    {
        Assert.Equal(40000, T1Paket.WithGlyph("0 40000 hsbw 0 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar").AdvanceWidth("A"));
        var font = T1Paket.WithGlyph("0 600 hsbw 64000 2 div 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        Assert.Equal((32000f, 0f), T1Paket.Points(font.Outline("A"))[0]);
    }

    /// <summary>
    /// T1 6.3 sentences 3–5: command values not listed (here the reserved 2 and the escape 12 20) are passed over and clear the stack — the
    /// numbers before them do not reach the next command, the outline is as without them.
    /// </summary>
    [Fact]
    public void Type1_ReservedCommands_AreSkippedAndClearTheStack()
    {
        var plain = T1Paket.WithGlyph("0 600 hsbw 100 0 rmoveto 50 0 rlineto 0 50 rlineto closepath endchar");
        var reserved = T1Paket.WithGlyph("0 600 hsbw 100 0 rmoveto 7 7 #2 50 0 rlineto 7 7 #12 #20 0 50 rlineto closepath endchar");
        T1Paket.SameGlyph(plain, reserved, "A");
    }

    /// <summary>
    /// T1 6.3 sentences 6–8: byte 12 introduces a two-byte command whose value is in the next byte — "20 30 500 0 sbw" (12 7) and "145 10 div"
    /// (12 12) give width 500 and a first point at (34.5, 30).
    /// </summary>
    [Fact]
    public void Type1_EscapeByte_IntroducesTwoByteCommands()
    {
        Assert.Equal([12, 7], T1Paket.Charstring("sbw"));
        var font = T1Paket.WithGlyph("20 30 500 0 sbw 145 10 div 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        Assert.Equal(500, font.AdvanceWidth("A"));
        Assert.Equal((34.5f, 30f), T1Paket.Points(font.Outline("A"))[0]);
    }

    /// <summary>
    /// T1 6.4 sentences 5–7: a command clears the stack, its operands serve only it — "100 200 rmoveto 50 hlineto" draws to (150, 200); a
    /// subroutine call passes the stack on — "100 0 5 callsubr" with subroutine 5 = "rmoveto return" starts at (100, 0).
    /// </summary>
    [Fact]
    public void Type1_CommandsClearTheStackButCallsubrPassesIt()
    {
        var font = T1Paket.WithGlyph("0 600 hsbw 100 200 rmoveto 50 hlineto 0 50 rlineto closepath endchar");
        Assert.Equal([(100f, 200f), (150f, 200f), (150f, 250f)], T1Paket.Points(font.Outline("A")));
        var called = T1Paket.WithGlyph("0 600 hsbw 100 0 5 callsubr 0 50 rlineto 50 0 rlineto closepath endchar", [.. T1Paket.DefaultSubrs, "rmoveto return"]);
        Assert.Equal((100f, 0f), T1Paket.Points(called.Outline("A"))[0]);
    }

    /// <summary>
    /// T1 6.4 sentences 8–10: endchar ends the outline — nothing after it is drawn, and an open subpath before it is closed.
    /// </summary>
    [Fact]
    public void Type1_Endchar_ClosesAndEndsTheCharstring()
    {
        var font = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar 100 0 rlineto");
        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Close], font.Outline("A").Verbs);
        Assert.Equal(3, T1Paket.Points(font.Outline("A")).Length);
        var open = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 100 0 rlineto 0 100 rlineto endchar");
        Assert.Equal(PathVerb.Close, open.Outline("A").Verbs[^1]);
    }

    /// <summary>
    /// T1 6.4 sentences 21–22: hsbw once and first binds the program; the reader is tolerant — without hsbw the width is 0 and the outline begins at
    /// (0, 0); a second "10 700 hsbw" in the middle sets the width to 700.
    /// </summary>
    [Fact]
    public void Type1_WithoutOrWithASecondHsbw_IsReadTolerantly()
    {
        var none = T1Paket.WithGlyph("0 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        Assert.Equal(0, none.AdvanceWidth("A"));
        Assert.Equal((0f, 0f), T1Paket.Points(none.Outline("A"))[0]);
        var twice = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 100 0 rlineto 10 700 hsbw 0 100 rlineto closepath endchar");
        Assert.Equal(700, twice.AdvanceWidth("A"));
        Assert.False(twice.Outline("A").IsEmpty);
    }

    /// <summary>
    /// T1 6.4 sentence 23: a character that draws nothing ("30 250 hsbw endchar", a space) has an empty outline and width 250; it paints nothing and
    /// the text after it moves on by 250 units.
    /// </summary>
    [Fact]
    public void Type1_EmptyGlyph_AdvancesWithoutDrawing()
    {
        var program = T1Paket.Build(new T1Paket.Spec
        {
            Encoding = "256 array\n0 1 255 {1 index exch /.notdef put} for\ndup 32 /space put\ndup 65 /A put\nreadonly def",
            Glyphs = [(".notdef", T1Paket.NotDef), ("space", "30 250 hsbw endchar"), ("A", T1Paket.Triangle)],
        });
        var font = Type1Font.Parse(program.Data);
        Assert.True(font.Outline("space").IsEmpty);
        Assert.Equal(250, font.AdvanceWidth("space"));
        using var doc = PdfDocument.Open(T1Paket.Pdf(program, "BT /F1 100 Tf 10 50 Td ( A) Tj ET"));
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        Assert.Single(T1Paket.Fills(list));
        var a = Assert.Single(list.Text, t => t.Text == "A");
        Assert.Equal(35, a.X0, 2);
    }

    /// <summary>
    /// T1 6.4 sentences 24, 27–28 and 30–31: seac builds an accented character from two characters of the program, found by their StandardEncoding
    /// codes — Aacute has the verbs of A and of acute; cleaning down to Aacute keeps A and acute and removes the rest.
    /// </summary>
    [Fact]
    public void Type1_Seac_CombinesBaseAndAccentAndCleaningKeepsBoth()
    {
        var program = T1Paket.Build(new T1Paket.Spec { Glyphs = T1PaketSeacGlyphs() });
        var font = Type1Font.Parse(program.Data);
        Assert.Equal(font.Outline("A").Verbs.Concat(font.Outline("acute").Verbs), font.Outline("Aacute").Verbs);
        var (data, _, _, _) = Type1Scrubber.Scrub(program.Data, new HashSet<string> { "Aacute" }, program.Clear, program.Encrypted);
        var cleaned = Type1Font.Parse(data);
        Assert.True(cleaned.HasGlyph("Aacute"));
        Assert.True(cleaned.HasGlyph("A"));
        Assert.True(cleaned.HasGlyph("acute"));
        Assert.False(cleaned.HasGlyph("B"));
        T1Paket.SameGlyph(font, cleaned, "Aacute");
    }

    /// <summary>
    /// T1 6.4 sentences 25–26, today's behaviour (open question 19 of FRAGEN.md): the accent is moved by adx − asb, and its outline keeps its own
    /// sidebearing sbx — its first point lies at adx − asb + sbx: 200 for acute with sbx = asb = 30 (seac 30 200 150), 220 for an acute with sbx 50.
    /// </summary>
    [Fact]
    public void Type1_Seac_AccentAtAdxMinusAsbPlusItsSidebearing()
    {
        var same = T1Paket.Parse(new T1Paket.Spec { Glyphs = T1PaketSeacGlyphs() });
        Assert.Equal((200f, 900f), T1Paket.Points(same.Outline("Aacute"))[3]);
        var other = T1Paket.Parse(new T1Paket.Spec { Glyphs = T1PaketSeacGlyphs(acuteHsbw: "50 300 hsbw") });
        Assert.Equal((220f, 900f), T1Paket.Points(other.Outline("Aacute"))[3]);
    }

    /// <summary>
    /// T1 6.4 sentence 29: the parts of seac are found by the StandardEncoding, not by the encoding of the program — with A at code 66 in the
    /// program's own encoding, bchar 65 still draws A.
    /// </summary>
    [Fact]
    public void Type1_Seac_FindsPartsThroughStandardEncoding()
    {
        var font = T1Paket.Parse(new T1Paket.Spec
        {
            Encoding = "256 array\n0 1 255 {1 index exch /.notdef put} for\ndup 66 /A put\nreadonly def",
            Glyphs = T1PaketSeacGlyphs(),
        });
        Assert.Equal("A", font.Encoding[66]);
        Assert.Equal("", font.Encoding[65]);
        Assert.Equal(T1Paket.Points(font.Outline("A")), T1Paket.Points(font.Outline("Aacute"))[..3]);
    }

    /// <summary>
    /// T1 6.4 sentence 33: the accented character has the width of its own hsbw (600, as A) and its base stands where A stands alone.
    /// </summary>
    [Fact]
    public void Type1_Seac_WidthOfTheAccentedCharacterAndBaseInPlace()
    {
        var font = T1Paket.Parse(new T1Paket.Spec { Glyphs = T1PaketSeacGlyphs() });
        Assert.Equal(600, font.AdvanceWidth("Aacute"));
        Assert.Equal(T1Paket.Points(font.Outline("A")), T1Paket.Points(font.Outline("Aacute"))[..3]);
    }

    /// <summary>T1 6.4 sentences 34–35: seac is the last command — a rlineto after it draws nothing, the outline is A and acute only.</summary>
    [Fact]
    public void Type1_Seac_EndsTheCharstring()
    {
        var plain = T1Paket.Parse(new T1Paket.Spec { Glyphs = T1PaketSeacGlyphs() });
        var trailing = T1Paket.Parse(new T1Paket.Spec { Glyphs = T1PaketSeacGlyphs("50 600 hsbw 30 200 150 65 194 seac 100 0 rlineto") });
        T1Paket.SameGlyph(plain, trailing, "Aacute");
    }

    /// <summary>
    /// T1 6.4 sentences 36–39: sbw sets the sidebearing point (sbx, sby) and the width wx without a point in the path; the first rmoveto starts
    /// there — "20 30 500 0 sbw 0 0 rmoveto" begins at (20, 30), width 500.
    /// </summary>
    [Fact]
    public void Type1_Sbw_SetsSidebearingPointAndWidth()
    {
        var font = T1Paket.WithGlyph("20 30 500 0 sbw 0 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        Assert.Equal(500, font.AdvanceWidth("A"));
        Assert.Equal([(20f, 30f), (120f, 30f), (120f, 130f)], T1Paket.Points(font.Outline("A")));
    }

    /// <summary>
    /// T1 6.4 sentences 40–42: "20 0 500 0 sbw" gives the same points and width as "20 500 hsbw"; a second sbw in the middle (which binds the
    /// program) sets point and width anew in the reader.
    /// </summary>
    [Fact]
    public void Type1_SbwWithZeroYAndRepeated_IsReadTolerantly()
    {
        var hsbw = T1Paket.WithGlyph("20 500 hsbw 0 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        var sbw = T1Paket.WithGlyph("20 0 500 0 sbw 0 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        T1Paket.SameGlyph(hsbw, sbw, "A");
        var twice = T1Paket.WithGlyph("0 0 600 0 sbw 0 0 rmoveto 100 0 rlineto 0 100 rlineto closepath 20 30 500 0 sbw 0 0 rmoveto 50 0 rlineto " +
            "0 50 rlineto closepath endchar");
        Assert.Equal(500, twice.AdvanceWidth("A"));
        Assert.Equal((20f, 30f), T1Paket.Points(twice.Outline("A"))[3]);
    }

    /// <summary>
    /// T1 6.4 sentences 46, 49–50: subpaths without closepath are closed by the reader — two open subpaths end with a close verb each and give the
    /// same outline as with closepath.
    /// </summary>
    [Fact]
    public void Type1_OpenSubpaths_AreClosed()
    {
        var open = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 100 0 rlineto 0 100 rlineto 200 0 rmoveto 100 0 rlineto 0 100 rlineto endchar");
        var closed = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 100 0 rlineto 0 100 rlineto closepath 200 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Close, PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Close],
            open.Outline("A").Verbs);
        T1Paket.SameGlyph(closed, open, "A");
    }

    /// <summary>
    /// T1 6.4 sentences 51–58: hmoveto is "dx 0 rmoveto", hlineto "dx 0 rlineto", hvcurveto "dx1 0 dx2 dy2 0 dy3 rrcurveto" — points (100, 0),
    /// (300, 0), a curve over (350, 0) and (400, 50) to (400, 150).
    /// </summary>
    [Fact]
    public void Type1_HorizontalShortcuts()
    {
        var font = T1Paket.WithGlyph("0 600 hsbw 100 hmoveto 200 hlineto 50 50 50 100 hvcurveto closepath endchar");
        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Cubic, PathVerb.Close], font.Outline("A").Verbs);
        Assert.Equal([(100f, 0f), (300f, 0f), (350f, 0f), (400f, 50f), (400f, 150f)], T1Paket.Points(font.Outline("A")));
    }

    /// <summary>
    /// T1 6.4 sentences 61–64: rrcurveto takes its arguments relative to each other — "0 0 rmoveto 10 20 30 40 50 60 rrcurveto" is a curve over
    /// (10, 20) and (40, 60) to (90, 120).
    /// </summary>
    [Fact]
    public void Type1_Rrcurveto_IsRelative()
    {
        var font = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 10 20 30 40 50 60 rrcurveto closepath endchar");
        Assert.Equal([(0f, 0f), (10f, 20f), (40f, 60f), (90f, 120f)], T1Paket.Points(font.Outline("A")));
    }

    /// <summary>
    /// T1 6.4 sentences 65–72: vmoveto is "0 dy rmoveto", vlineto "0 dy rlineto", vhcurveto "0 dy1 dx2 dy2 dx3 0 rrcurveto" — points (0, 100),
    /// (0, 300), a curve over (0, 350) and (50, 400) to (150, 400).
    /// </summary>
    [Fact]
    public void Type1_VerticalShortcuts()
    {
        var font = T1Paket.WithGlyph("0 600 hsbw 100 vmoveto 200 vlineto 50 50 50 100 vhcurveto closepath endchar");
        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Cubic, PathVerb.Close], font.Outline("A").Verbs);
        Assert.Equal([(0f, 100f), (0f, 300f), (0f, 350f), (50f, 400f), (150f, 400f)], T1Paket.Points(font.Outline("A")));
    }

    /// <summary>
    /// T1 6.4 sentences 110–118 and 126–128, T1 8 sentences 21–33: callothersubr with a number the reader does not know puts its arguments on the
    /// PostScript stack so that the first pop gives arg1 and the second arg2 ("7 8 2 27 callothersubr pop pop rmoveto" starts at (7, 8); a third
    /// argument stays behind); the OtherSubrs procedures of the font are not run; pop without a callothersubr gives 0.
    /// </summary>
    [Fact]
    public void Type1_UnknownOtherSubr_ReturnsItsArgumentsToPop()
    {
        var font = T1Paket.WithGlyph("0 600 hsbw 7 8 2 27 callothersubr pop pop rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        Assert.Equal((7f, 8f), T1Paket.Points(font.Outline("A"))[0]);
        // OtherSubrs that would need dictionaries and the PostScript interpreter: never run
        var withProcedures = T1Paket.Parse(new T1Paket.Spec
        {
            PrivateExtra = "/OtherSubrs [{}{}{}{} {systemdict begin currentdict begin end end}] def\n",
            Glyphs = [(".notdef", T1Paket.NotDef), ("A", "0 600 hsbw 7 8 2 27 callothersubr pop pop rmoveto 100 0 rlineto 0 100 rlineto closepath endchar")],
        });
        T1Paket.SameGlyph(font, withProcedures, "A");
        var line = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 5 7 2 27 callothersubr pop pop rlineto 0 100 rlineto closepath endchar");
        Assert.Equal((5f, 7f), T1Paket.Points(line.Outline("A"))[1]);
        var three = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 5 7 9 3 27 callothersubr pop pop rlineto 0 100 rlineto closepath endchar");
        Assert.Equal((5f, 7f), T1Paket.Points(three.Outline("A"))[1]);
        var popOnly = T1Paket.WithGlyph("0 600 hsbw 50 pop rmoveto 100 0 rlineto 0 100 rlineto closepath endchar");
        Assert.Equal((50f, 0f), T1Paket.Points(popOnly.Outline("A"))[0]);
    }

    /// <summary>The subroutines of a chain: 4 … 14 each call the next, 15 draws a line; 0–3 as the conventions have them.</summary>
    private static string[] T1PaketChain() =>
        [.. T1Paket.DefaultSubrs.Take(4), .. Enumerable.Range(4, 11).Select(k => $"{k + 1} callsubr return"), "0 300 rlineto return"];

    /// <summary>
    /// T1 6.4 sentence 124 and T1 8 sentence 12: subroutine calls may nest ten deep — a chain over ten levels (6 → … → 15) draws its line, over
    /// eleven (5 → … → 15) the glyph is empty without an exception; cleaning keeps the whole chain of the kept glyph.
    /// </summary>
    [Fact]
    public void Type1_SubroutineDepth_TenLevelsDrawnElevenEmpty()
    {
        var program = T1Paket.Build(new T1Paket.Spec
        {
            Subrs = T1PaketChain(),
            Glyphs = [(".notdef", T1Paket.NotDef), ("X", "0 600 hsbw 0 0 rmoveto 6 callsubr 100 0 rlineto closepath endchar"),
                ("Y", "0 600 hsbw 0 0 rmoveto 5 callsubr 100 0 rlineto closepath endchar")],
        });
        var font = Type1Font.Parse(program.Data);
        Assert.Equal([(0f, 0f), (0f, 300f), (100f, 300f)], T1Paket.Points(font.Outline("X")));
        Assert.True(font.Outline("Y").IsEmpty);

        var before = T1Paket.Private(program.Data, program.Clear, program.Encrypted);
        var (data, clear, encrypted, _) = Type1Scrubber.Scrub(program.Data, new HashSet<string> { "X" }, program.Clear, program.Encrypted);
        var after = T1Paket.Private(data, clear, encrypted);
        for (var k = 6; k <= 15; k++)
            Assert.Equal(Regex.Match(before, $@"dup {k} (\d+) RD ").Groups[1].Value, Regex.Match(after, $@"dup {k} (\d+) RD ").Groups[1].Value);
        Assert.Contains("dup 4 5 RD ", after);
        Assert.Contains("dup 5 5 RD ", after);
        T1Paket.SameGlyph(font, Type1Font.Parse(data), "X");
    }

    /// <summary>T1 6.4 sentence 124 and T1 8 sentence 12: a subroutine that calls itself ("4 callsubr") gives an empty glyph, quickly.</summary>
    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void Type1_SelfCallingSubroutine_EmptyAndQuick()
    {
        var watch = Stopwatch.StartNew();
        var font = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 4 callsubr 100 0 rlineto closepath endchar", [.. T1Paket.DefaultSubrs.Take(4), "4 callsubr"]);
        Assert.True(font.Outline("A").IsEmpty);
        Assert.True(watch.ElapsedMilliseconds < 1000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// T1 6.4 sentences 131–134: setcurrentpoint sets the current point without a moveto, with the results of an OtherSubrs call —
    /// "300 400 2 27 callothersubr pop pop setcurrentpoint 10 0 rlineto" ends at (310, 400) without another move.
    /// </summary>
    [Fact]
    public void Type1_Setcurrentpoint_MovesWithoutAMove()
    {
        var font = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 100 0 rlineto 300 400 2 27 callothersubr pop pop setcurrentpoint 10 0 rlineto closepath endchar");
        var outline = font.Outline("A");
        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Close], outline.Verbs);
        Assert.Equal((310f, 400f), T1Paket.Points(outline)[^1]);
    }
}
