using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>Konformitätsabgleich M7 (text and fonts), package t1-03: Type 1 Font Format, chapters 7–10 and appendices 1–3 (prefix T1).</summary>
public sealed partial class M7NormTests
{
    /// <summary>A stand-in for the OtherSubrs of a font with flex and hint replacement (own PostScript, not the code of the specification).</summary>
    private const string T1PaketOtherSubrsFull = "/OtherSubrs\n[{3 {pop} repeat}{}{}{pop 3}] def\n";

    /// <summary>A stand-in for the OtherSubrs of a font with hint replacement only (own PostScript).</summary>
    private const string T1PaketOtherSubrsHints = "/OtherSubrs\n[{}{}{}{pop 3}] def\n";

    /// <summary>
    /// A flex glyph after the example of T1 8.3: start (100, −10), "1 callsubr", the reference point and six control points each with rmoveto and
    /// "2 callsubr", then control value, end point and "0 callsubr"; the joining point lies <paramref name="height"/> above the start. Then a
    /// line "0 10 rlineto".
    /// </summary>
    private static string T1PaketFlex(int height, int control) =>
        $"0 600 hsbw 100 -10 rmoveto 1 callsubr 50 0 rmoveto 2 callsubr -35 0 rmoveto 2 callsubr 10 {height} rmoveto 2 callsubr " +
        $"25 0 rmoveto 2 callsubr 25 0 rmoveto 2 callsubr 10 {-height} rmoveto 2 callsubr 15 0 rmoveto 2 callsubr {control} 200 -10 0 callsubr " +
        "0 10 rlineto closepath endchar";

    /// <summary>
    /// T1 8 sentences 2 and 15–19, 8.3 sentences 7–8, 15, 21–25, 32–48, 66–69 and 71, 8.4 sentences 5–10, Appendix 1 sentences 42–43: flex through
    /// Subrs and OtherSubrs 0–2 — the reader has its own code per number and draws the two curves as defined: one move at (100, −10), then the
    /// curves over (115, −10) (125, y) to (150, y) and over (175, y) (185, −10) to (200, −10), no further move; the current point after
    /// "0 callsubr" is (200, −10), so "0 10 rlineto" ends at (200, 0). The same with a flex height of 30 (beyond the limit), with /BlueShift 3,
    /// with the control values 50 and 5000 and with or without an OtherSubrs array in the font.
    /// </summary>
    [Theory]
    [InlineData(10, 50, "")]
    [InlineData(30, 50, "")]
    [InlineData(10, 50, "/BlueShift 3 def\n")]
    [InlineData(10, 5000, "")]
    [InlineData(10, 50, T1PaketOtherSubrsFull)]
    [InlineData(10, 50, T1PaketOtherSubrsHints)]
    public void Type1_Flex_DrawsTheTwoCurves(int height, int control, string privateExtra)
    {
        var font = T1Paket.Parse(new T1Paket.Spec { PrivateExtra = privateExtra, Glyphs = [(".notdef", T1Paket.NotDef), ("A", T1PaketFlex(height, control))] });
        var outline = font.Outline("A");
        var y = -10f + height;
        Assert.Equal(new[] { PathVerb.Move, PathVerb.Cubic, PathVerb.Cubic, PathVerb.Line, PathVerb.Close }, outline.Verbs);
        Assert.Equal(new (float, float)[] { (100, -10), (115, -10), (125, y), (150, y), (175, y), (185, -10), (200, -10), (200, 0) }, T1Paket.Points(outline));
    }

    /// <summary>
    /// T1 8.3 sentences 1–6: flex is meant to hide small bumps at small sizes; PDF-Knead draws the curves as defined at any size — text of size 4
    /// and of size 400 fills the same path of two curves.
    /// </summary>
    [Fact]
    public void Type1_Flex_SameAtSmallAndLargeSizes()
    {
        var program = T1Paket.Build(new T1Paket.Spec { Glyphs = [(".notdef", T1Paket.NotDef), ("A", T1PaketFlex(10, 50))] });
        foreach (var size in (int[])[4, 400])
        {
            using var doc = PdfDocument.Open(T1Paket.Pdf(program, $"BT /F1 {size} Tf 20 60 Td (A) Tj ET"));
            var fill = Assert.Single(T1Paket.Fills(PageInterpreter.Build(doc, doc.Pages[0])));
            Assert.Equal(new[] { PathVerb.Move, PathVerb.Cubic, PathVerb.Cubic, PathVerb.Line, PathVerb.Close }, fill);
        }
    }

    /// <summary>T1 8 sentences 13–14: subroutines are optional — glyph B of the sample has none and draws (50, 50) … (150, 150) with width 600.</summary>
    [Fact]
    public void Type1_GlyphWithoutSubroutine_Draws()
    {
        var font = Type1Font.Parse(Type1Sample.Build().Data);
        Assert.Equal(new (float, float)[] { (50, 50), (150, 50), (150, 150) }, T1Paket.Points(font.Outline("B")));
        Assert.Equal(RectD.FromLtrb(50, 50, 150, 150), font.Outline("B").Bounds);
        Assert.Equal(600, font.AdvanceWidth("B"));
    }

    /// <summary>
    /// T1 8 sentence 20, Appendix 1 sentences 42–43, Appendix 3 sentences 10–15 and 24–27: the OtherSubrs in the private part (both kinds: for flex
    /// and hint replacement, and for hint replacement only) are not run — the glyphs read as without them — and cleaning keeps them byte for byte.
    /// </summary>
    [Theory]
    [InlineData(T1PaketOtherSubrsFull)]
    [InlineData(T1PaketOtherSubrsHints)]
    public void Type1_OtherSubrsBlock_ReadOverAndKeptByCleaning(string block)
    {
        var plain = T1Paket.Parse();
        var program = T1Paket.Build(new T1Paket.Spec { PrivateExtra = block });
        var font = Type1Font.Parse(program.Data);
        foreach (var name in (string[])["A", "B", "C"]) T1Paket.SameGlyph(plain, font, name);
        var (data, clear, encrypted, _) = Type1Scrubber.Scrub(program.Data, new HashSet<string> { "B" }, program.Clear, program.Encrypted);
        Assert.Contains(block, T1Paket.Private(data, clear, encrypted));
    }

    /// <summary>
    /// T1 8.1 sentences 4–7: new hints stand in a subroutine of their own ("5 1 3 callothersubr pop callsubr", subroutine 5 = "0 20 hstem return");
    /// cleaning keeps it with its length when its glyph stays, and empties it when its glyph goes.
    /// </summary>
    [Fact]
    public void Type1_HintReplacementSubroutine_StaysWithItsGlyph()
    {
        var program = T1Paket.Build(new T1Paket.Spec
        {
            Subrs = [.. T1Paket.DefaultSubrs, "0 20 hstem return"],
            Glyphs = [.. T1Paket.DefaultGlyphs.Where(g => g.Name != "A"), ("A", "0 600 hsbw 5 1 3 callothersubr pop callsubr 100 100 rmoveto 4 callsubr closepath endchar")],
        });
        var length = Regex.Match(T1Paket.Private(program.Data, program.Clear, program.Encrypted), @"dup 5 (\d+) RD ").Groups[1].Value;
        Assert.NotEqual("5", length);
        var keepA = Type1Scrubber.Scrub(program.Data, new HashSet<string> { "A" }, program.Clear, program.Encrypted);
        Assert.Contains($"dup 5 {length} RD ", T1Paket.Private(keepA.Data, keepA.Clear, keepA.Encrypted));
        var keepB = Type1Scrubber.Scrub(program.Data, new HashSet<string> { "B" }, program.Clear, program.Encrypted);
        Assert.Contains("dup 5 5 RD ", T1Paket.Private(keepB.Data, keepB.Clear, keepB.Encrypted));
    }

    /// <summary>
    /// T1 8.1 sentences 8–19: the hint replacement enc(subr#) 1 3 callothersubr pop callsubr — OtherSubrs 3 gives 3 as in an interpreter without
    /// hint replacement, pop takes it and callsubr calls subroutine 3, which does nothing; subroutine 5 (here a line, standing in for the new
    /// hints) is not run: the outline is as without the sequence.
    /// </summary>
    [Fact]
    public void Type1_HintReplacement_DoesNotRunTheNewHints()
    {
        Assert.Equal(new byte[] { 144, 140, 142, 12, 16, 12, 17, 10 }, T1Paket.Charstring("5 1 3 callothersubr pop callsubr"));
        string[] subrs = [.. T1Paket.DefaultSubrs, "0 300 rlineto return"];
        var plain = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar", subrs);
        var replaced = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 100 0 rlineto 5 1 3 callothersubr pop callsubr 0 100 rlineto closepath endchar", subrs);
        T1Paket.SameGlyph(plain, replaced, "A");
    }

    /// <summary>
    /// T1 8.4 sentences 1 and 3–4: Subrs 0–3 are fixed by the conventions — cleaned down to nothing, subroutine 0 (the crossbar of the builder's
    /// A) keeps its length and is not emptied.
    /// </summary>
    [Fact]
    public void Type1_FixedSubroutines_StayWhenCleaning()
    {
        var font = Type1Builder.Build();
        var clear = font.AsSpan().IndexOf("currentfile eexec\n"u8) + "currentfile eexec\n".Length;
        var before = Regex.Match(Encoding.Latin1.GetString(Type1Sample.Decrypt(font.AsSpan(clear), 55665, 4)), @"dup 0 (\d+) RD ").Groups[1].Value;
        Assert.NotEqual("5", before);
        var (data, newClear, encrypted, _) = Type1Scrubber.Scrub(font, new HashSet<string>());
        Assert.Contains($"dup 0 {before} RD ", T1Paket.Private(data, newClear, encrypted));
    }

    /// <summary>
    /// A synthetic font (T1 9.1, own wording of its structure): clear text with its own name and a slanted matrix; in the eexec part a clone of
    /// the private dictionary, the test whether the base font is loaded (FontDirectory after /Private) and the complete base program with its
    /// own eexec part; the CharStrings come from the base font.
    /// </summary>
    private static byte[] T1PaketSynthetic()
    {
        var baseProgram = Encoding.Latin1.GetString(T1Paket.Build().Data);
        var priv = "dup /Private 8 dict dup begin\n/RD{string currentfile exch readstring pop}executeonly def\n/ND{noaccess def}executeonly def\n" +
                   "/NP{noaccess put}executeonly def\n/lenIV 4 def\n/UniqueID 4000002 def\nend readonly put\n" +
                   "FontDirectory /KneadTest known {/KneadTest findfont /UniqueID get 4000001 eq} {false} ifelse\n{save true} {false} ifelse\n" +
                   baseProgram +
                   "\ndup /CharStrings FontDirectory /KneadTest get /CharStrings get put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n";
        var clear = "%!PS-AdobeFont-1.0: KneadTestOblique 001\n12 dict begin\n/FontName /KneadTestOblique def\n/FontType 1 def\n" +
                    "/FontMatrix [0.001 0 0.0002 0.001 0 0] readonly def\n/FontBBox {0 0 1200 1000} readonly def\n/UniqueID 4000002 def\n" +
                    "/Encoding StandardEncoding def\ncurrentdict end\ncurrentfile eexec\n";
        return [.. Encoding.Latin1.GetBytes(clear), .. Type1Sample.Encrypt([0, 0, 0, 0, .. Encoding.Latin1.GetBytes(priv)], 55665), .. Encoding.ASCII.GetBytes(T1Paket.Zeros)];
    }

    /// <summary>
    /// T1 9 sentences 1–4, 9.1 sentences 5–18 (reading) and 10.3 sentence 17: a synthetic font shares the CharStrings of its base program, whose
    /// copy it contains; the reader recognises the form (FontDirectory after /Private), reads A from the copy and keeps the slanted matrix of the
    /// synthetic font.
    /// </summary>
    [Fact(Skip = "M7-Befund: synthetische Type-1-Schrift nicht erkannt, Parse wirft „Type 1 ohne CharStrings“ (B53)")]
    public void Type1_SyntheticFont_ReadsTheBaseProgramCopy()
    {
        var font = Type1Font.Parse(T1PaketSynthetic());
        Assert.Equal("KneadTestOblique", font.Name);
        Assert.Equal(0.0002, font.FontMatrix.C, 9);
        T1Paket.SameGlyph(T1Paket.Parse(), font, "A");
    }

    /// <summary>
    /// T1 9 sentences 1–4, 9.2 sentences 1–12 and 10.3 sentence 17: a hybrid font ("hires" before the first /Subrs) has two sets of Subrs and
    /// CharStrings, one per resolution; the reader takes the charstring and its subroutines from the same set — never the subroutines of one with
    /// the charstrings of the other.
    /// </summary>
    [Fact(Skip = "M7-Befund: hybride Type-1-Schrift — Subrs der ersten, CharStrings der zweiten Gruppe gemischt (S8)")]
    public void Type1_HybridFont_TakesSubrsAndCharStringsFromOneSet()
    {
        string Set(string subr4, string glyphA)
        {
            var subrs = new[] { T1Paket.DefaultSubrs[0], T1Paket.DefaultSubrs[1], T1Paket.DefaultSubrs[2], T1Paket.DefaultSubrs[3], subr4 };
            var text = new StringBuilder($"/Subrs {subrs.Length} array\n");
            for (var i = 0; i < subrs.Length; i++)
            {
                var bytes = Type1Sample.Encrypt([0, 0, 0, 0, .. T1Paket.Charstring(subrs[i])], 4330);
                text.Append($"dup {i} {bytes.Length} RD ").Append(Encoding.Latin1.GetString(bytes)).Append(" NP\n");
            }
            text.Append("ND\n2 index /CharStrings 2 dict dup begin\n");
            foreach (var (name, code) in new[] { (".notdef", T1Paket.NotDef), ("A", glyphA) })
            {
                var bytes = Type1Sample.Encrypt([0, 0, 0, 0, .. T1Paket.Charstring(code)], 4330);
                text.Append($"/{name} {bytes.Length} RD ").Append(Encoding.Latin1.GetString(bytes)).Append(" ND\n");
            }
            return text.Append("end\n").ToString();
        }
        var low = Set("100 0 rlineto return", "0 600 hsbw 0 0 rmoveto 4 callsubr 50 50 rlineto closepath endchar");
        var high = Set("0 100 rlineto return", "0 600 hsbw 0 0 rmoveto 4 callsubr 60 60 rlineto closepath endchar");
        var priv = "dup /Private 8 dict dup begin\n/RD{string currentfile exch readstring pop}executeonly def\n/ND{noaccess def}executeonly def\n" +
                   "/NP{noaccess put}executeonly def\n/lenIV 4 def\n/hires 0 def\n" + low + "save hires {restore} if\n" + high +
                   "end\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n";
        var clear = "%!PS-AdobeFont-1.0: KneadTest 001\n12 dict begin\n/FontName /KneadTest def\n/FontType 1 def\n/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n" +
                    "/FontBBox {0 0 1000 1000} readonly def\n/Encoding StandardEncoding def\ncurrentdict end\ncurrentfile eexec\n";
        byte[] data = [.. Encoding.Latin1.GetBytes(clear), .. Type1Sample.Encrypt([0, 0, 0, 0, .. Encoding.Latin1.GetBytes(priv)], 55665), .. Encoding.ASCII.GetBytes(T1Paket.Zeros)];
        var points = T1Paket.Points(Type1Font.Parse(data).Outline("A"));
        var lowSet = new (float, float)[] { (0, 0), (100, 0), (150, 50) };
        var highSet = new (float, float)[] { (0, 0), (0, 100), (60, 160) };
        Assert.True(points.SequenceEqual(lowSet) || points.SequenceEqual(highSet), string.Join(" ", points));
    }

    /// <summary>
    /// T1 10.2 sentences 1–3: an array follows its name directly, in brackets or braces, each number a token of its own — /FontMatrix
    /// {0.002 0 0 0.002 0 0} is read; a computed entry [1 1000 div 0 0 0.001 0 0] is not six numbers and leaves the default 0.001.
    /// </summary>
    [Fact]
    public void Type1_ArraysInBracesAndComputedEntries()
    {
        Assert.Equal(0.002, T1Paket.Parse(new T1Paket.Spec { FontMatrix = "{0.002 0 0 0.002 0 0}" }).FontMatrix.A, 9);
        var computed = T1Paket.Parse(new T1Paket.Spec { FontMatrix = "[1 1000 div 0 0 0.001 0 0]" }).FontMatrix;
        Assert.Equal(0.001, computed.A, 9);
        Assert.Equal(0.001, computed.D, 9);
    }

    /// <summary>T1 10.3 sentence 1: "/Encoding StandardEncoding def" gives the standard encoding — 65 A, 66 B, 39 quoteright.</summary>
    [Fact]
    public void Type1_StandardEncodingByName()
    {
        var font = T1Paket.Parse(new T1Paket.Spec { Encoding = "StandardEncoding def" });
        Assert.Equal("A", font.Encoding[65]);
        Assert.Equal("B", font.Encoding[66]);
        Assert.Equal("quoteright", font.Encoding[39]);
    }

    /// <summary>
    /// T1 10.3 sentence 4: the number after /Subrs is the number of its entries — cleaned down to B, the sample still has "/Subrs 5 array" with
    /// exactly the entries 0 … 4 (none removed, unused ones emptied).
    /// </summary>
    [Fact]
    public void Type1_Cleaning_KeepsTheSubrsCountAndNumbers()
    {
        var sample = Type1Sample.Build();
        var (data, clear, encrypted, _) = Type1Scrubber.Scrub(sample.Data, new HashSet<string> { "B" }, sample.Clear, sample.Encrypted);
        var priv = T1Paket.Private(data, clear, encrypted);
        Assert.Contains("/Subrs 5 array", priv);
        Assert.Equal(new[] { "0", "1", "2", "3", "4" }, Regex.Matches(priv, @"dup (\d+) \d+ RD ").Select(m => m.Groups[1].Value));
    }

    /// <summary>T1 10.3 sentence 9: the number after /CharStrings is at least the number of its entries — cleaned down to B it is 2 (B and .notdef).</summary>
    [Fact]
    public void Type1_Cleaning_CountsTheKeptCharStrings()
    {
        var sample = Type1Sample.Build();
        var (data, clear, encrypted, _) = Type1Scrubber.Scrub(sample.Data, new HashSet<string> { "B" }, sample.Clear, sample.Encrypted);
        Assert.Contains("/CharStrings 2 dict", T1Paket.Private(data, clear, encrypted));
    }

    /// <summary>T1 10.3 sentences 15–16: the CharStrings end with "end" — an entry "/Z 5 RD …" after it is no glyph.</summary>
    [Fact]
    public void Type1_EntryAfterTheCharStringsEnd_IsNoGlyph()
    {
        var font = T1Paket.Parse(new T1Paket.Spec { AfterCharStrings = "/Z 5 RD abcde ND\n" });
        Assert.False(font.HasGlyph("Z"));
        Assert.True(font.HasGlyph("A"));
        Assert.False(font.Outline("A").IsEmpty);
    }

    /// <summary>
    /// T1 Appendix 1 sentences 52–54: Subrs is optional — without it a glyph without callsubr draws, one with "0 callsubr" draws the rest of its
    /// outline without an exception, and cleaning works.
    /// </summary>
    [Fact]
    public void Type1_WithoutSubrs_DrawsAndCleans()
    {
        var program = T1Paket.Build(new T1Paket.Spec
        {
            Subrs = null,
            Glyphs = [(".notdef", T1Paket.NotDef), ("A", T1Paket.Triangle), ("G", "0 600 hsbw 0 0 rmoveto 0 callsubr 100 0 rlineto 0 100 rlineto closepath endchar")],
        });
        Assert.DoesNotContain("/Subrs", T1Paket.Private(program.Data, program.Clear, program.Encrypted));
        var font = Type1Font.Parse(program.Data);
        Assert.False(font.Outline("A").IsEmpty);
        Assert.Equal(new (float, float)[] { (0, 0), (100, 0), (100, 100) }, T1Paket.Points(font.Outline("G")));
        var (data, _, _, _) = Type1Scrubber.Scrub(program.Data, new HashSet<string> { "G" }, program.Clear, program.Encrypted);
        var cleaned = Type1Font.Parse(data);
        Assert.False(cleaned.HasGlyph("A"));
        T1Paket.SameGlyph(font, cleaned, "G");
    }

    /// <summary>
    /// T1 Appendix 2 sentences 2–3: the one-byte commands as listed — vmoveto, hlineto, vlineto, rrcurveto, hmoveto, vhcurveto and hvcurveto give the
    /// expected points and verbs (cubic curves for 8, 30 and 31); hstem and vstem have no effect on the outline.
    /// </summary>
    [Fact]
    public void Type1_OneByteCommands_AsListed()
    {
        var font = T1Paket.WithGlyph("0 600 hsbw 0 50 hstem 0 50 vstem 100 vmoveto 200 hlineto 100 vlineto 10 20 30 40 50 60 rrcurveto 50 hmoveto " +
            "10 20 30 40 vhcurveto 10 20 30 40 hvcurveto closepath endchar");
        var outline = font.Outline("A");
        Assert.Equal(new[] { PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Cubic, PathVerb.Close, PathVerb.Move, PathVerb.Cubic, PathVerb.Cubic, PathVerb.Close },
            outline.Verbs);
        Assert.Equal(new (float, float)[]
        {
            (0, 100), (200, 100), (200, 200), (210, 220), (240, 260), (290, 320),
            (340, 320), (340, 330), (360, 360), (400, 360), (410, 360), (430, 390), (430, 430),
        }, T1Paket.Points(outline));
    }

    /// <summary>
    /// T1 Appendix 2 sentences 4–5: the escape commands as listed — sbw (width from wx), div ("1200 2 div" as the width of hsbw gives 600), seac
    /// (both parts, the accent moved by adx − asb), setcurrentpoint before a rlineto; dotsection, vstem3 and hstem3 without effect (see
    /// Type1_HintsInCharstrings_LeaveOutlineAndWidthUnchanged), callothersubr and pop (see Type1_UnknownOtherSubr_ReturnsItsArgumentsToPop).
    /// </summary>
    [Fact]
    public void Type1_EscapeCommands_AsListed()
    {
        Assert.Equal(500, T1Paket.WithGlyph("20 30 500 0 sbw 0 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar").AdvanceWidth("A"));
        Assert.Equal(600, T1Paket.WithGlyph("0 1200 2 div hsbw 0 0 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar").AdvanceWidth("A"));
        var seac = T1Paket.Parse(new T1Paket.Spec { Glyphs = T1PaketSeacGlyphs() }).Outline("Aacute");
        Assert.Equal(2, seac.Verbs.Count(v => v == PathVerb.Move));
        Assert.Equal((200f, 900f), T1Paket.Points(seac)[3]);   // acute (sbx 30) moved by 200 − 30
        var point = T1Paket.WithGlyph("0 600 hsbw 0 0 rmoveto 100 0 rlineto 300 400 setcurrentpoint 10 0 rlineto closepath endchar");
        Assert.Equal((310f, 400f), T1Paket.Points(point.Outline("A"))[^1]);
    }
}
