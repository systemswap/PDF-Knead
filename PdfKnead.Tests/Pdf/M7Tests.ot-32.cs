using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-32 (OpenType specification, OS/2 from usFirstCharIndex to the end, variable fonts, and the post table): the tests the drafts of
/// the sentence-by-sentence comparison asked for — line metrics from hhea, the fields read only from version 2 on, the own font of the text
/// recognition, and glyph names from post.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-32)

    private static byte[] Ot32Post(byte[] font) => OpenTypeFont.Parse(font).TableData("post").ToArray();

    /// <summary>A post table of version 2.0 for Liberation Sans: every glyph index 0 except those given, then the strings.</summary>
    private static byte[] Ot32Post2(IReadOnlyDictionary<int, int> indices, params string[] strings)
    {
        var count = Ot05Sans.Value.GlyphCount;
        var post = new List<byte>(Ot32Post(Ot05SansFile.Value)[..32]);
        BinaryPrimitives.WriteUInt32BigEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(post), 0x00020000);
        post.AddRange([(byte)(count >> 8), (byte)count]);
        for (var g = 0; g < count; g++)
        {
            var index = indices.GetValueOrDefault(g);
            post.AddRange([(byte)(index >> 8), (byte)index]);
        }
        foreach (var s in strings)
        {
            post.Add((byte)s.Length);
            post.AddRange(Encoding.ASCII.GetBytes(s));
        }
        return [.. post];
    }

    /// <summary>A minimal MVAR (version 1.0, one value record for <paramref name="tag"/>, no item variation store).</summary>
    private static byte[] Ot32Mvar(string tag) => [0, 1, 0, 0, 0, 0, 0, 8, 0, 1, 0, 0, .. Encoding.ASCII.GetBytes(tag), 0, 0, 0, 0];

    // ------------------------------------------------------------ 31.2.21, 31.2.22 first and last character

    /// <summary>
    /// OT 31.2.21 sentences 1–5 and 31.2.22 sentences 1–3 (today's behaviour, question 7 open): scrubbing Liberation Sans down to the glyphs of
    /// „Text“ builds a new cmap but keeps usFirstCharIndex 0x20 and usLastCharIndex 0xFFFC of the source (a font rebuilt as symbolic takes the
    /// codes of its cmap: FontScrubTests.RebuiltAsSymbolic_NamesPanoseAndRangeFollow).
    /// </summary>
    [Fact]
    public void Ot32_Scrub_KeepsFirstAndLastCharIndexOfTheSource()
    {
        var os2 = Ot30Os2Of(TrueTypeSubsetter.Scrub(Ot30File(), Ot05Glyphs("Text")));
        Assert.Equal(0x0020, Ot30U16(os2, 64));
        Assert.Equal(0xFFFC, Ot30U16(os2, 66));
    }

    // ------------------------------------------------------------ 31.2.23–31.2.27 line metrics

    /// <summary>
    /// OT 31.2.23 sentences 1–4, 8–10, 31.2.24 sentences 1–4, 8–10, 31.2.26 sentences 12–20, 31.2.27 sentences 14–22 (and ot-31: 31.2.20
    /// sentences 11, 27–30) — today's behaviour, question 14 open: ascender and descender of typed text come from hhea, also when fsSelection
    /// bit 7 (USE_TYPO_METRICS) is set and sTypo* and usWin* differ (1500/−500, 2000/600): Liberation Sans keeps 1854/2048 and −434/2048.
    /// </summary>
    [Fact]
    public void Ot32_UseTypoMetrics_AscentAndDescentStillFromHhea()
    {
        var font = Ot30WithOs2(o =>
        {
            Ot30Set(o, 62, 0x00C0);
            Ot30Set(o, 68, 1500);
            Ot30Set(o, 70, -500);
            Ot30Set(o, 74, 2000);
            Ot30Set(o, 76, 600);
        }, 4);
        var face = Ot30Face(font);
        Assert.Equal(1854.0 / 2048, face.Ascent, 9);
        Assert.Equal(-434.0 / 2048, face.Descent, 9);
    }

    /// <summary>
    /// OT 31.2.23 sentence 12, 31.2.24 sentence 12, 31.2.26 sentences 5–11, 21–22, 31.2.27 sentences 5–13, 23–24, 31.2.28 sentences 1–42,
    /// 31.2.31 sentences 1–4, 31.2.32 sentences 1–5, 31.2.33 sentences 1–6, 31.2.34 sentences 8–14, 31.2.35 sentences 8–15 (writer): the own font
    /// fits its empty glyphs (0 … 1000) — sTypoAscender/Descender/LineGap 1000/0/0 as hhea, usWinAscent 1000 ≥ yMax, usWinDescent 0 = −yMin, no
    /// code page bit, usDefaultChar 0, usBreakChar 0x20, usMaxContext 0, version 4 of 96 bytes (without the optical sizes).
    /// </summary>
    [Fact]
    public void Ot32_GlyphlessOs2_MetricsFitTheEmptyGlyphs()
    {
        var os2 = Ot30Glyphless;
        var hhea = Ot05GlyphlessTable("hhea");
        var head = Ot05GlyphlessTable("head");
        Assert.Equal((1000, 0, 0), (Ot30U16(os2, 68), Ot30U16(os2, 70), Ot30U16(os2, 72)));
        Assert.Equal((1000, 0, 0), (Ot30U16(hhea, 4), Ot30U16(hhea, 6), Ot30U16(hhea, 8)));
        Assert.True(Ot30U16(os2, 74) >= BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(42)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(38)));
        Assert.Equal(0, Ot30U16(os2, 76));
        Assert.All(os2[78..86], b => Assert.Equal(0, b));
        Assert.Equal(0, Ot30U16(os2, 90));
        Assert.Equal(0x0020, Ot30U16(os2, 92));
        Assert.Equal(0, Ot30U16(os2, 94));
        Assert.Equal(4, Ot30U16(os2, 0));
        Assert.Equal(96, os2.Length);
    }

    /// <summary>
    /// OT 31.2.26 sentences 1–4 and 31.2.27 sentences 1–4: glyphs are not clipped to usWinAscent/usWinDescent — with usWinAscent 200, usWinDescent
    /// 20 and /Ascent 300, /Descent −50 in the descriptor, the cell of „Å“ (for selecting and redacting) reaches its whole outline, and so does the
    /// cell of every glyph with a descender.
    /// </summary>
    [Fact]
    public void Ot32_GlyphCell_TakesTheWholeOutline_NotClippedToWinMetrics()
    {
        var program = Ot30WithOs2(o =>
        {
            Ot30Set(o, 74, 200);
            Ot30Set(o, 76, 20);
        });
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(descriptor: d => d.Replace("/Ascent 905/Descent -212", "/Ascent 300/Descent -50", StringComparison.Ordinal),
            program: () => program)());
        var font = PdfFont.Get(Iso04F1(doc), doc);
        var cellOf = typeof(PageInterpreter).GetMethod("GlyphCell", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var code in "ÅgjpqyÇ")
        {
            var glyph = font.Glyph(code, 1);
            Assert.False(glyph.Outline.IsEmpty);
            var (cell, ink) = ((RectD, RectD))cellOf.Invoke(null, [font, glyph])!;
            Assert.True(cell.Top <= ink.Top && cell.Bottom >= ink.Bottom, $"Zelle von {code}");
            if (code == 'Å') Assert.True(ink.Bottom > 0.3 && cell.Bottom >= ink.Bottom);
        }
    }

    // ------------------------------------------------------------ 31.2.29, 31.2.30 x and cap height of the own font

    /// <summary>
    /// OT 31.2.29 sentences 4–5 and 31.2.30 sentences 4–5 (today's behaviour, question 27 open): the own font has no glyph for x or H, yet writes
    /// sxHeight 500 and sCapHeight 1000.
    /// </summary>
    [Fact]
    public void Ot32_GlyphlessOs2_XHeightAndCapHeightSetToday()
    {
        Assert.Equal(0, Ot05Glyphless.Value.GlyphForUnicode('x'));
        Assert.Equal(0, Ot05Glyphless.Value.GlyphForUnicode('H'));
        Assert.Equal(500, Ot30U16(Ot30Glyphless, 86));
        Assert.Equal(1000, Ot30U16(Ot30Glyphless, 88));
    }

    // ------------------------------------------------------------ 31.2.33 usMaxContext, 31.2.34/35 optical sizes

    /// <summary>
    /// OT 31.2.33 sentences 1–6: a subset of Liberation Sans has neither GSUB nor GPOS, so its usMaxContext (offset 94) should be 0 instead of
    /// the 44 of the source.
    /// </summary>
    [Fact(Skip = "M7-Befund: Teilschrift und bereinigte Schrift ohne GSUB/GPOS behalten usMaxContext der Quelle (44) (TrueTypeSubsetter.Subset/Scrub)")]
    public void Ot32_SubsetWithoutLayout_UsMaxContextZero()
    {
        var subset = TrueTypeSubsetter.Subset(Ot30File(), Ot05Glyphs("AB"));
        Assert.False(OpenTypeFont.Parse(subset).HasTable("GSUB"));
        Assert.Equal(0, Ot30U16(Ot30Os2Of(subset), 94));
    }

    /// <summary>
    /// OT 31.2.34 sentences 8–14 and 31.2.35 sentences 8–15: a subset of a font with OS/2 version 5 (no optical sizes: 0 and 0xFFFF) keeps both
    /// fields unchanged.
    /// </summary>
    [Fact]
    public void Ot32_SubsetOfVersion5_KeepsOpticalSizes()
    {
        var font = Ot30WithOs2(o => Ot30Set(o, 98, 0xFFFF), 5, 100);
        var os2 = Ot30Os2Of(TrueTypeSubsetter.Subset(font, Ot05Glyphs("AB")));
        Assert.Equal(Ot30Os2Of(font), os2);
        Assert.Equal((0, 0xFFFF), (Ot30U16(os2, 96), Ot30U16(os2, 98)));
    }

    // ------------------------------------------------------------ 31.3 variable fonts

    /// <summary>
    /// OT 31.3 sentences 1–3 and 33–36: no variable font is written — a subset of Liberation Sans with fvar, gvar and MVAR has none of them, its
    /// OS/2 and hhea are those of the source (default instance).
    /// </summary>
    [Fact]
    public void Ot32_SubsetOfVariableFont_DropsFvarGvarMvar_KeepsOs2AndHhea()
    {
        var (fvar, gvar) = Ot05VariationTables(Ot05Sans.Value.GlyphCount);
        var variable = Ot05SansWith(("fvar", fvar), ("gvar", gvar), ("MVAR", Ot32Mvar("hasc")));
        var subset = TrueTypeSubsetter.Subset(variable, Ot05Glyphs("AB"));
        var parsed = OpenTypeFont.Parse(subset);
        foreach (var tag in (string[])["fvar", "gvar", "MVAR"]) Assert.False(parsed.HasTable(tag), tag);
        Assert.Equal(Ot30Os2Of(variable), Ot30Os2Of(subset));
        Assert.Equal(OpenTypeFont.Parse(variable).TableData("hhea").ToArray(), parsed.TableData("hhea").ToArray());
    }

    /// <summary>
    /// OT 31.3 sentences 7–24: MVAR is not read — with an MVAR record for cpht the descriptor still has /CapHeight 688 from sCapHeight of the
    /// default instance (StandardFontEmbedding and typed text).
    /// </summary>
    [Fact]
    public void Ot32_MvarCapHeight_NotApplied()
    {
        var font = Ot05SansWith(("MVAR", Ot32Mvar("cpht")));
        Assert.Equal(688, Ot30StandardDescriptor(font).GetNumber(PdfName.Get("CapHeight")));
        Assert.Equal(688, Ot30SavedDescriptor(font).GetNumber(PdfName.Get("CapHeight")));
    }

    // ------------------------------------------------------------ 32.2 header

    /// <summary>
    /// OT 32.2 sentences 1–3, 10–13, 16–25: a rebuilt font (PDF/A) and the own font carry post version 3.0 of 32 bytes, the memory fields
    /// (offsets 16–31) 0.
    /// </summary>
    [Fact]
    public void Ot32_RebuiltAndOwnFont_PostVersion3Of32Bytes()
    {
        var rebuilt = Ot32Post(TrueTypeSubsetter.Rebuild(Ot30File(), Ot05Glyphs("AB").ToDictionary(g => g, _ => 1000), [], null));
        foreach (var post in (byte[][])[rebuilt, Ot05GlyphlessTable("post")])
        {
            Assert.Equal(32, post.Length);
            Assert.Equal(0x00030000u, BinaryPrimitives.ReadUInt32BigEndian(post));
            Assert.All(post[16..32], b => Assert.Equal(0, b));
        }
    }

    /// <summary>
    /// OT 32.2 sentences 4–5: italicAngle for the standard fonts — StandardFontEmbedding writes /ItalicAngle −12 and flag 64 for Liberation Sans
    /// Italic, 0 without flag 64 for Liberation Sans Regular.
    /// </summary>
    [Fact]
    public void Ot32_ItalicAngle_FromPostForStandardFonts()
    {
        var italic = Ot30StandardDescriptor(Ot30File("LiberationSans-Italic.ttf"), "LiberationSans-Italic.ttf");
        Assert.Equal(-12, italic.GetNumber(PdfName.Get("ItalicAngle")));
        Assert.NotEqual(0, italic.GetInteger(PdfName.Get("Flags"))!.Value & 64);
        var regular = Ot30StandardDescriptor(Ot30File());
        Assert.Equal(0, regular.GetNumber(PdfName.Get("ItalicAngle")));
        Assert.Equal(0, regular.GetInteger(PdfName.Get("Flags"))!.Value & 64);
    }

    /// <summary>
    /// OT 32.2 sentences 4–5 (today's behaviour, question 14 open): typed text in an italic system font whose post says −9.5° gets /ItalicAngle
    /// −12, not the value of post.
    /// </summary>
    [Fact]
    public void Ot32_TypedTextInItalicFont_ItalicAngleMinus12Today()
    {
        var post = Ot32Post(Ot05SansFile.Value);
        BinaryPrimitives.WriteInt32BigEndian(post.AsSpan(4), (int)(-9.5 * 65536));
        var font = Ot05SansWith(("post", post));
        Assert.Equal(-12, Ot30SavedDescriptor(font, italic: true).GetNumber(PdfName.Get("ItalicAngle")));
    }

    /// <summary>OT 32.2 sentence 9: the own font is fixed pitch — isFixedPitch (offset 12) is 1 and both glyphs are 500 wide.</summary>
    [Fact]
    public void Ot32_GlyphlessPost_FixedPitch_EqualAdvances()
    {
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(Ot05GlyphlessTable("post").AsSpan(12)));
        Assert.Equal(500, Ot05Glyphless.Value.AdvanceWidth(0));
        Assert.Equal(500, Ot05Glyphless.Value.AdvanceWidth(1));
    }

    // ------------------------------------------------------------ 32.3–32.6 glyph names

    /// <summary>OT 32.3 sentences 1–2: post version 1.0 names the glyphs in the Macintosh order — glyph 36 is „A“, and „A“ is glyph 36.</summary>
    [Fact]
    public void Ot32_PostVersion1_MacintoshNames()
    {
        var post = Ot32Post(Ot05SansFile.Value)[..32];
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00010000);
        var font = OpenTypeFont.Parse(Ot05SansWith(("post", post)));
        Assert.Equal("A", font.GlyphName(36));
        Assert.Equal(36, font.GlyphByName("A"));
    }

    /// <summary>
    /// OT 32.4 sentences 1–9: numGlyphs of post equals that of maxp — also after scrubbing Liberation Sans (2620 glyphs).
    /// </summary>
    [Fact]
    public void Ot32_Scrub_PostGlyphCountIsMaxp()
    {
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(Ot30File(), Ot05Glyphs("Text")));
        var post = scrubbed.TableData("post");
        Assert.Equal(0x00020000u, BinaryPrimitives.ReadUInt32BigEndian(post));
        Assert.Equal(Ot30U16(scrubbed.TableData("maxp"), 4), Ot30U16(post, 32));
        Assert.Equal(Ot05Sans.Value.GlyphCount, Ot30U16(post, 32));
    }

    /// <summary>
    /// OT 32.4 sentences 10–15: an index below 258 gives the Macintosh name, from 258 on the string number index − 258 — in Liberation Sans the
    /// glyph of index 258 is named by the first string, the Euro (an index above 258) „Euro“.
    /// </summary>
    [Fact]
    public void Ot32_PostVersion2_MacNamesBelow258_StringsFrom258()
    {
        var sans = Ot05Sans.Value;
        var post = sans.TableData("post").ToArray();
        var count = Ot30U16(post, 32);
        var strings = 34 + 2 * count;
        var first = Encoding.ASCII.GetString(post, strings + 1, post[strings]);
        var glyph = Enumerable.Range(0, count).First(g => Ot30U16(post, 34 + 2 * g) == 258);
        Assert.Equal(first, sans.GlyphName(glyph));
        var euro = sans.GlyphForUnicode(0x20AC);
        Assert.True(Ot30U16(post, 34 + 2 * euro) > 258);
        Assert.Equal("Euro", sans.GlyphName(euro));
        Assert.Equal("A", sans.GlyphName(sans.GlyphForUnicode('A')));
    }

    /// <summary>
    /// OT 32.4 sentences 16–21: scrubbing keeps the strings of the glyphs kept — after scrubbing to „€T“ the Euro is still named „Euro“, and the
    /// name of a removed glyph with its own string („Ā“) is no longer in the table.
    /// </summary>
    [Fact]
    public void Ot32_Scrub_KeepsTheStringsOfKeptGlyphsOnly()
    {
        var sans = Ot05Sans.Value;
        var euro = sans.GlyphForUnicode(0x20AC);
        var removed = sans.GlyphName(sans.GlyphForUnicode(0x0100));
        Assert.False(string.IsNullOrEmpty(removed));
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(Ot30File(), [euro, sans.GlyphForUnicode('T')]));
        Assert.Equal("Euro", scrubbed.GlyphName(euro));
        Assert.DoesNotContain(removed, Encoding.Latin1.GetString(scrubbed.TableData("post")), StringComparison.Ordinal);
    }

    /// <summary>OT 32.4 sentence 22 (reader, lenient): a name of 100 characters (longer than the 63 allowed) is still found.</summary>
    [Fact]
    public void Ot32_PostNameOf100Characters_FoundAnyway()
    {
        var name = new string('L', 100);
        var font = OpenTypeFont.Parse(Ot05SansWith(("post", Ot32Post2(new Dictionary<int, int> { [5] = 258 }, name))));
        Assert.Equal(5, font.GlyphByName(name));
    }

    /// <summary>OT 32.4 sentence 23: glyphs without a name have index 0 — after scrubbing to „Text“ the glyphs of G, Q, ß and h are named „.notdef“.</summary>
    [Fact]
    public void Ot32_Scrub_RemovedGlyphsNamedNotdef()
    {
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(Ot30File(), Ot05Glyphs("Text")));
        foreach (var g in Ot05Glyphs("GQßh")) Assert.Equal(".notdef", scrubbed.GlyphName(g));
    }

    /// <summary>
    /// OT 32.5 sentences 1–9: post version 2.5 gives each glyph an offset into the Macintosh order — glyphs 1–3 with offset +35 should be „A“, „B“,
    /// „C“.
    /// </summary>
    [Fact(Skip = "M7-Befund: post 2.5 (veraltet) wird nicht gelesen, die Glyphen bleiben ohne Namen (OpenTypeFont.PostNames)")]
    public void Ot32_PostVersion25_GivesMacintoshNames()
    {
        var count = Ot05Sans.Value.GlyphCount;
        var post = new byte[34 + count];
        Ot32Post(Ot05SansFile.Value).AsSpan(0, 32).CopyTo(post);
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00025000);
        BinaryPrimitives.WriteUInt16BigEndian(post.AsSpan(32), (ushort)count);
        for (var g = 1; g <= 3; g++) post[34 + g] = 35;
        var font = OpenTypeFont.Parse(Ot05SansWith(("post", post)));
        Assert.Equal("A", font.GlyphName(1));
        Assert.Equal("C", font.GlyphName(3));
    }

    /// <summary>OT 32.6 sentences 1–6: post version 3.0 has no glyph names — the own font is read without error and its glyphs have no name.</summary>
    [Fact]
    public void Ot32_PostVersion3_NoGlyphNames()
    {
        Assert.Null(Ot05Glyphless.Value.GlyphName(0));
        Assert.Null(Ot05Glyphless.Value.GlyphName(1));
        Assert.Equal(-1, Ot05Glyphless.Value.GlyphByName(".notdef"));
    }
}
