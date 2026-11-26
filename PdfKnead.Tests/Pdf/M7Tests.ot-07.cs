namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-07 (OpenType specification, CFF2: header, TopDICT, FontDICTINDEX, FontDICTSelect, FontDICT): PDF-Knead reads CFF2 in the
/// default instance (question 1, docs/FEHLERGEDAECHTNIS.md, No. 1158) — the font built after the section, read: its squares drawn, the matrix of
/// the Top DICT or from head, FontDICTSelect choosing the Private DICT. The CFF2 tables come from <c>Ot06Cff2</c> (package ot-06).
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// CFF2 header, TopDICT and FontDICTs as the sections describe them, each variant read (question 1): no 'CFF ', not intact (a strict PDF reader
    /// knows no CFF2), every glyph after .notdef the square, the matrix of the Top DICT (without one from head, unitsPerEm; an uneven matrix with
    /// a shift is taken as written, as common font engines do), tolerant where the table breaks a rule (no FontDICTINDEX or an empty one, a Private
    /// DICT beyond the table: no local subroutines), and the redaction writes a static CFF2 table.
    /// <list type="bullet">
    /// <item>OT 8.6 sentences 8–11: header size 7 (two bytes before the TopDICT).</item>
    /// <item>OT 8.7 sentences 1–4: TopDICT with FontMatrix 0.0005, head unitsPerEm 2000.</item>
    /// <item>OT 8.7.1 sentences 1–7: TopDICT with the operators 17, 12 36 and 12 7.</item>
    /// <item>OT 8.7.2 sentences 1–7: CharStringINDEX of three glyphs at the offset of operator 17, maxp 3.</item>
    /// <item>OT 8.7.2 sentences 8–13: a variable font (fvar, gvar) with a VariationStore (operator 24).</item>
    /// <item>OT 8.7.2 sentences 14–19: TopDICT without operator 12 36.</item>
    /// <item>OT 8.7.2 sentences 20–26: one FontDICT and still operator 12 37 with a FontDICTSelect.</item>
    /// <item>OT 8.7.2 sentences 27–40: FontMatrix 1/2048 with unitsPerEm 2048, and the uneven matrix 0.001 0 0 0.002 0 5.</item>
    /// <item>OT 8.8.1 sentences 1–6: two FontDICTs, each with its PrivateDICT and its local subroutines, and a FontDICTSelect.</item>
    /// <item>OT 8.8.2 sentences 1–7: an empty FontDICTINDEX; two FontDICTs without FontDICTSelect.</item>
    /// <item>OT 8.8.3 sentences 1–9: FontDICTSelect of the unknown format 5.</item>
    /// <item>OT 8.8.3 sentences 10–15: format 0 with the indices 0 1 0 1 for four glyphs, and with an index beyond the FontDICTs.</item>
    /// <item>OT 8.8.3 sentences 16–35: format 3 with two ranges (0–1 → 0, 2–3 → 1), and with a first range at 1, falling ranges, a sentinel
    /// other than numGlyphs.</item>
    /// <item>OT 8.8.3 sentences 36–47: format 4 with numRanges 0xFFFFFFFF in a short table, and with two valid ranges.</item>
    /// <item>OT 8.8.4 sentences 1–10: FontDICT with PrivateDICTOffset 0 0, and with a PrivateDICT reaching beyond the table.</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("Kopfgröße 7")]
    [InlineData("FontMatrix 1/2000")]
    [InlineData("Operatoren 17, 12 36, 12 7")]
    [InlineData("drei Glyphen")]
    [InlineData("variabel")]
    [InlineData("ohne 12 36")]
    [InlineData("eine FontDICT mit FontDICTSelect")]
    [InlineData("FontMatrix 1/2048")]
    [InlineData("FontMatrix ungleichmäßig")]
    [InlineData("zwei FontDICTs mit FontDICTSelect")]
    [InlineData("leere FontDICTINDEX")]
    [InlineData("zwei FontDICTs ohne FontDICTSelect")]
    [InlineData("FontDICTSelect Format 5")]
    [InlineData("Format 0: 0 1 0 1")]
    [InlineData("Format 0: Index zu groß")]
    [InlineData("Format 3")]
    [InlineData("Format 3: erster Bereich bei 1")]
    [InlineData("Format 3: fallende Bereiche")]
    [InlineData("Format 3: Endmarke falsch")]
    [InlineData("Format 4: numRanges 0xFFFFFFFF")]
    [InlineData("Format 4: zwei Bereiche")]
    [InlineData("Private 0 0")]
    [InlineData("Private außerhalb")]
    public void Cff2TopDictAndFontDicts_AreRead(string variant)
    {
        const double em2048 = 1.0 / 2048;
        var unitsPerEm = variant == "FontMatrix 1/2000" ? 2000 : 2048;
        var o = variant switch
        {
            "Kopfgröße 7" => new Ot06Cff2Options { HeaderSize = 7 },
            "FontMatrix 1/2000" => new Ot06Cff2Options { FontMatrix = [0.0005, 0, 0, 0.0005, 0, 0], UnitsPerEm = 2000 },
            "Operatoren 17, 12 36, 12 7" or "FontMatrix 1/2048" => new Ot06Cff2Options { FontMatrix = [em2048, 0, 0, em2048, 0, 0] },
            "drei Glyphen" => new Ot06Cff2Options { Glyphs = 3 },
            "variabel" => new Ot06Cff2Options { VariationStore = true },
            "ohne 12 36" => new Ot06Cff2Options { FontDictIndexOffset = false },
            "eine FontDICT mit FontDICTSelect" => new Ot06Cff2Options { FdSelect = [0, 0, 0] },
            "FontMatrix ungleichmäßig" => new Ot06Cff2Options { FontMatrix = [0.001, 0, 0, 0.002, 0, 5] },
            "zwei FontDICTs mit FontDICTSelect" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [3, 0, 2, 0, 0, 0, 0, 2, 1, 0, 4] },
            "leere FontDICTINDEX" => new Ot06Cff2Options { FontDicts = 0 },
            "zwei FontDICTs ohne FontDICTSelect" => new Ot06Cff2Options { FontDicts = 2 },
            "FontDICTSelect Format 5" => new Ot06Cff2Options { FontDicts = 2, FdSelect = [5, 0, 0] },
            "Format 0: 0 1 0 1" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [0, 0, 1, 0, 1] },
            "Format 0: Index zu groß" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [0, 0, 5, 0, 1] },
            "Format 3" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [3, 0, 2, 0, 0, 0, 0, 2, 1, 0, 4] },
            "Format 3: erster Bereich bei 1" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [3, 0, 2, 0, 1, 0, 0, 2, 1, 0, 4] },
            "Format 3: fallende Bereiche" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [3, 0, 2, 0, 2, 0, 0, 0, 1, 0, 4] },
            "Format 3: Endmarke falsch" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [3, 0, 2, 0, 0, 0, 0, 2, 1, 0, 9] },
            "Format 4: numRanges 0xFFFFFFFF" => new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = [4, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0] },
            "Format 4: zwei Bereiche" => new Ot06Cff2Options
            {
                Glyphs = 4, FontDicts = 2, FdSelect = [4, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 0, 0, 4],
            },
            "Private 0 0" => new Ot06Cff2Options { EmptyPrivate = true },
            "Private außerhalb" => new Ot06Cff2Options { PrivateOutside = true },
            _ => throw new ArgumentException(variant),
        };
        var cff2 = Ot06Cff2(o);
        var file = variant == "variabel"
            ? Ot06Cff2Font(cff2, o.Glyphs, unitsPerEm, ("fvar", Ot05VariationTables(o.Glyphs).Fvar), ("gvar", Ot05VariationTables(o.Glyphs).Gvar))
            : Ot06Cff2Font(cff2, o.Glyphs, unitsPerEm);
        Assert.Equal(o.HeaderSize, cff2[2]);
        // the matrix as written (the builder writes at most ten decimals)
        static double Written(double v) => double.Parse(v.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        var matrix = o.FontMatrix is { } m ? new PdfKnead.Core.Pdf.Graphics.Matrix(Written(m[0]), Written(m[1]), Written(m[2]), Written(m[3]), Written(m[4]), Written(m[5])) : (PdfKnead.Core.Pdf.Graphics.Matrix?)null;
        Ot06AssertCff2Read(file, unitsPerEm, matrix);
        Assert.Equal(o.Glyphs, PdfKnead.Core.Fonts.OpenTypeFont.Parse(file).GlyphCount);
    }

    /// <summary>
    /// OT 8.8.1 sentences 1–6, 8.8.2 sentences 1–7, 8.8.3 (reader): FontDICTSelect chooses the Private DICT and with it the local subroutines of
    /// each glyph — each glyph draws the square and then calls local subroutine 0 of its Font DICT (Font DICT 0: a line up, 1: a line to the
    /// right). Format 0 (0 1 0 1), format 3 and format 4 with two ranges (0–1 → 0, 2–3 → 1), a first range at 1 (glyph 0 then keeps Font DICT 0),
    /// falling ranges (each glyph takes the first range that covers it), a sentinel beyond numGlyphs, an index beyond the Font DICTs (Font DICT 0),
    /// the unknown format 5 and no FontDICTSelect (all Font DICT 0); format 4 with numRanges 0xFFFFFFFF in a short table draws without hanging.
    /// </summary>
    [Theory]
    [InlineData("Format 0: 0 1 0 1", 1, 0, 1)]
    [InlineData("Format 3", 0, 1, 1)]
    [InlineData("Format 4: zwei Bereiche", 0, 1, 1)]
    [InlineData("Format 3: erster Bereich bei 1", 0, 1, 1)]
    [InlineData("Format 3: fallende Bereiche", 1, 1, 1)]
    [InlineData("Format 3: Endmarke falsch", 0, 1, 1)]
    [InlineData("Format 0: Index zu groß", 0, 0, 1)]
    [InlineData("FontDICTSelect Format 5", 0, 0, 0)]
    [InlineData("zwei FontDICTs ohne FontDICTSelect", 0, 0, 0)]
    [InlineData("Format 4: numRanges 0xFFFFFFFF", -1, -1, -1)]
    public void Cff2FontDictSelect_ChoosesThePrivateDict(string variant, int glyph1, int glyph2, int glyph3)
    {
        byte[]? fdSelect = variant switch
        {
            "Format 0: 0 1 0 1" => [0, 0, 1, 0, 1],
            "Format 3" => [3, 0, 2, 0, 0, 0, 0, 2, 1, 0, 4],
            "Format 4: zwei Bereiche" => [4, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 0, 0, 4],
            "Format 3: erster Bereich bei 1" => [3, 0, 2, 0, 1, 0, 0, 2, 1, 0, 4],
            "Format 3: fallende Bereiche" => [3, 0, 2, 0, 2, 0, 0, 0, 1, 0, 4],
            "Format 3: Endmarke falsch" => [3, 0, 2, 0, 0, 0, 0, 2, 1, 0, 9],
            "Format 0: Index zu groß" => [0, 0, 5, 0, 1],
            "FontDICTSelect Format 5" => [5, 0, 0],
            "zwei FontDICTs ohne FontDICTSelect" => null,
            "Format 4: numRanges 0xFFFFFFFF" => [4, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0],
            _ => throw new ArgumentException(variant),
        };
        var file = Ot06Cff2Font(Ot06Cff2(new Ot06Cff2Options { Glyphs = 4, FontDicts = 2, FdSelect = fdSelect, CallLocalSubr = true }), 4);
        var font = PdfKnead.Core.Fonts.OpenTypeFont.Parse(file);
        int[] expected = [glyph1, glyph2, glyph3];
        for (var g = 1; g <= 3; g++)
        {
            if (expected[g - 1] < 0)
            {
                var bounds = font.Outline(g).Bounds;
                Assert.True(bounds.Left <= 50 && bounds.Right >= 150, $"Glyphe {g}");
                continue;
            }
            SameOutlineOt08(ReferenceOt08(Ot06Square + (expected[g - 1] == 0 ? " 0 100 rlineto" : " 100 0 rlineto")), font.Outline(g), $"Glyphe {g}");
        }
    }
}
