using System.Reflection;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-29 (OpenType specification: name): the tests the drafts of the sentence-by-sentence comparison asked for — which names
/// the font reader and the index of system fonts take (platforms, encodings, languages, name IDs), what cleaning and scrubbing leave of a
/// name table, and the name table of the font of the text recognition.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-29)

    /// <summary>A name record: platform, encoding, language, name ID and the bytes of its text.</summary>
    private readonly record struct Ot29Record(int Platform, int Encoding, int Language, int Id, byte[] Text);

    /// <summary>A record whose text is encoded as the platform has it (platform 1 one byte per character, otherwise UTF-16BE).</summary>
    private static Ot29Record Ot29R(int platform, int encoding, int language, int id, string text) =>
        new(platform, encoding, language, id, platform == 1 ? Encoding.Latin1.GetBytes(text) : Encoding.BigEndianUnicode.GetBytes(text));

    /// <summary>
    /// A name table of the records given, in the order given; with <paramref name="langTags"/> version 1 with these language tags (UTF-16BE,
    /// stored after the texts of the records), otherwise version 0.
    /// </summary>
    private static byte[] Ot29Name(Ot29Record[] records, string[]? langTags = null)
    {
        var table = new List<byte>();
        var strings = new List<byte>();
        void U16(int v) => table.AddRange([(byte)(v >> 8), (byte)v]);
        U16(langTags is null ? 0 : 1);
        U16(records.Length);
        U16(6 + 12 * records.Length + (langTags is null ? 0 : 2 + 4 * langTags.Length));
        foreach (var r in records)
        {
            U16(r.Platform); U16(r.Encoding); U16(r.Language); U16(r.Id); U16(r.Text.Length); U16(strings.Count);
            strings.AddRange(r.Text);
        }
        if (langTags is not null)
        {
            U16(langTags.Length);
            foreach (var tag in langTags)
            {
                var bytes = Encoding.BigEndianUnicode.GetBytes(tag);
                U16(bytes.Length); U16(strings.Count);
                strings.AddRange(bytes);
            }
        }
        table.AddRange(strings);
        return [.. table];
    }

    private static byte[] Ot29Font(params Ot29Record[] records) => Ot27SansWith(("name", Ot29Name(records)));

    private static OpenTypeFont Ot29Parsed(params Ot29Record[] records) => OpenTypeFont.Parse(Ot29Font(records));

    /// <summary>The bytes of the text of the first record of a platform and name ID in a font file; null if there is none.</summary>
    private static byte[]? Ot29Text(byte[] font, int platform, int id)
    {
        var name = Ot27Table(font, "name");
        var strings = Ot27U16(name, 4);
        for (var i = 0; i < Ot27U16(name, 2); i++)
        {
            var r = 6 + 12 * i;
            if (Ot27U16(name, r) == platform && Ot27U16(name, r + 6) == id)
                return name.AsSpan(strings + Ot27U16(name, r + 10), Ot27U16(name, r + 8)).ToArray();
        }
        return null;
    }

    /// <summary>The keys, family name and usability <c>SystemFonts</c> reads from a font file (its private method, by reflection).</summary>
    private static (List<string> Keys, string? Family, bool Usable) Ot29SystemFontNames(byte[] font)
    {
        var file = Path.Combine(Path.GetTempPath(), $"m7-ot29-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(file, font);
        try
        {
            var names = typeof(SystemFonts).GetMethod("Names", BindingFlags.NonPublic | BindingFlags.Static)!;
            return ((List<string>, string?, bool))names.Invoke(null, [file])!;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static readonly byte[] Ot29Copyright = Encoding.BigEndianUnicode.GetBytes("Copyright Muster");

    private static bool Ot29AllUtf16Spaces(byte[] text) =>
        text.Length % 2 == 0 && Enumerable.Range(0, text.Length / 2).All(i => text[2 * i] == 0 && text[2 * i + 1] == ' ');

    // ------------------------------------------------------------ OT 30 overview

    /// <summary>
    /// OT 30 sentence 6 (reader, today's behaviour; question 18): the language ID is passed over, the first matching record counts — a Chinese family
    /// name (3,1,0x0404) before „Probe Sans“ (3,1,0x0409): the family name and the family of the system font index are the Chinese one.
    /// </summary>
    [Fact]
    public void Ot29_FirstRecordInTableOrder_TodayWhateverItsLanguage()
    {
        var font = Ot29Font(Ot29R(3, 1, 0x0404, 1, "中文名"), Ot29R(3, 1, 0x0409, 1, "Probe Sans"));
        Assert.Equal("中文名", OpenTypeFont.Parse(font).FamilyName);
        Assert.Equal("中文名", Ot29SystemFontNames(font).Family);
    }

    /// <summary>
    /// OT 30 sentences 8–9 (reader): without Windows or Unicode records the Macintosh ones count — a font with only (1,0,0) records for IDs 1 and 6:
    /// family and PostScript name, and the system font index finds it by them.
    /// </summary>
    [Fact]
    public void Ot29_MacOnlyNames_ReadAndFoundInTheSystemFontIndex()
    {
        var file = Ot29Font(Ot29R(1, 0, 0, 1, "Probe Mac"), Ot29R(1, 0, 0, 6, "ProbeMac-Regular"));
        var font = OpenTypeFont.Parse(file);
        Assert.Equal(("Probe Mac", "ProbeMac-Regular"), (font.FamilyName, font.PostScriptName));
        var (keys, family, _) = Ot29SystemFontNames(file);
        Assert.Equal("Probe Mac", family);
        Assert.Contains("probemacregular", keys);
        Assert.Contains("probemac", keys);
    }

    // ------------------------------------------------------------ OT 30.1 versions

    /// <summary>
    /// OT 30.1 sentences 1–4 and OT 30.1.2 sentences 1–9 (reader and cleaning): a name table of version 1 is read like version 0 — one language tag
    /// „en“ and the record (3,1,0x8000,1) „Probe Sans“: that is the family name; cleaning the notices leaves the tag record and its text byte for byte.
    /// </summary>
    [Fact]
    public void Ot29_NameVersion1_ReadLikeVersion0_CleaningLeavesTheLangTag()
    {
        var name = Ot29Name([new(3, 1, 0x0409, 0, Ot29Copyright), Ot29R(3, 1, 0x8000, 1, "Probe Sans")], ["en"]);
        var file = Ot27SansWith(("name", name));
        Assert.Equal("Probe Sans", OpenTypeFont.Parse(file).FamilyName);
        var clean = Assert.IsType<byte[]>(ObjectMetadata.FontNotices(file));
        var cleaned = Ot27Table(clean, "name");
        var tagRecord = 6 + 12 * 2;
        Assert.Equal(name[tagRecord..(tagRecord + 6)], cleaned[tagRecord..(tagRecord + 6)]);
        var tagText = Ot27U16(name, 4) + Ot27U16(name, tagRecord + 4);
        Assert.Equal("en", Encoding.BigEndianUnicode.GetString(cleaned, tagText, 4));
        Assert.True(Ot29AllUtf16Spaces(Ot29Text(clean, 3, 0)!));
    }

    /// <summary>
    /// OT 30.1.1 sentences 1–7, OT 30.2 sentences 1–10 and OT 30.5.4.1 sentences 15 and 18 (writer): the name table of the text recognition font —
    /// version 0, three records, storage at 42, the records (1,0,0,6,15,0) in Roman, (3,1,0x0409,1,30,15) and (3,1,0x0409,6,30,45) in UTF-16BE, all
    /// „PDFKneadOcrText“ (the Macintosh record since FG 1151, OT 3.19.1).
    /// </summary>
    [Fact]
    public void Ot29_GlyphlessFont_NameTableLayout()
    {
        var name = Ot27Table(OcrTextLayer.Glyphless, "name");
        Assert.Equal((0, 3, 42), (Ot27U16(name, 0), Ot27U16(name, 2), Ot27U16(name, 4)));
        int[] Record(int i) => [.. Enumerable.Range(0, 6).Select(k => Ot27U16(name, 6 + 12 * i + 2 * k))];
        Assert.Equal(new[] { 1, 0, 0, 6, 15, 0 }, Record(0));
        Assert.Equal(new[] { 3, 1, 0x0409, 1, 30, 15 }, Record(1));
        Assert.Equal(new[] { 3, 1, 0x0409, 6, 30, 45 }, Record(2));
        Assert.Equal(117, name.Length);
        Assert.Equal("PDFKneadOcrText", Encoding.ASCII.GetString(name, 42, 15));
        Assert.Equal("PDFKneadOcrText", Encoding.BigEndianUnicode.GetString(name, 57, 30));
        Assert.Equal("PDFKneadOcrText", Encoding.BigEndianUnicode.GetString(name, 87, 30));
    }

    /// <summary>
    /// OT 30.1.2 sentences 10–15 (reader, today's behaviour; question 18): language tags are not read — version 1 with (3,1,0x0409,1) „A“ and
    /// (3,1,0x8000,1) „B“ (tag „de“): the family name is „A“, the first record.
    /// </summary>
    [Fact]
    public void Ot29_NameVersion1_TodayTheFirstRecordWhateverItsTag()
    {
        var name = Ot29Name([Ot29R(3, 1, 0x0409, 1, "A"), Ot29R(3, 1, 0x8000, 1, "B")], ["de"]);
        Assert.Equal("A", OpenTypeFont.Parse(Ot27SansWith(("name", name))).FamilyName);
    }

    /// <summary>
    /// OT 30.1.2 sentences 16–22 (writer): language tags are passed on as they are — Liberation Sans with a name table of version 1 (tag „en“):
    /// scrubbing keeps the name table byte for byte, cleaning the notices keeps langTagCount, the tag record and its text.
    /// </summary>
    [Fact]
    public void Ot29_NameVersion1_ScrubAndCleaningPassTheTagsOn()
    {
        var name = Ot29Name([new(3, 1, 0x0409, 0, Ot29Copyright), Ot29R(3, 1, 0x0409, 1, "Probe"), Ot29R(3, 1, 0x8000, 1, "Probe EN")], ["en"]);
        var file = Ot27SansWith(("name", name));
        Assert.Equal(name, Ot27Table(TrueTypeSubsetter.Scrub(file, [Ot27Glyph('A')]), "name"));
        var cleaned = Ot27Table(Assert.IsType<byte[]>(ObjectMetadata.FontNotices(file)), "name");
        var tags = 6 + 12 * 3;
        Assert.Equal(name[tags..(tags + 6)], cleaned[tags..(tags + 6)]);
        var text = Ot27U16(name, 4) + Ot27U16(name, tags + 4);
        Assert.Equal(name[text..(text + 4)], cleaned[text..(text + 4)]);
    }

    /// <summary>
    /// OT 30.1.2 sentences 23–26 (reader, today's behaviour; question 18): a language ID beyond the tags is not passed over — version 1 with one tag
    /// and the only record (3,1,0x8005,1) „X“: the family name is „X“.
    /// </summary>
    [Fact]
    public void Ot29_LanguageIdBeyondTheTags_TodayTaken()
    {
        var name = Ot29Name([Ot29R(3, 1, 0x8005, 1, "X")], ["en"]);
        Assert.Equal("X", OpenTypeFont.Parse(Ot27SansWith(("name", name))).FamilyName);
    }

    // ------------------------------------------------------------ OT 30.2, 30.3 records and languages

    /// <summary>
    /// OT 30.2 sentence 14 (reader): records need not be sorted to be found — records in descending order (ID 6 before ID 1, Windows before
    /// Macintosh): family and PostScript name are read.
    /// </summary>
    [Fact]
    public void Ot29_RecordsSortedDescending_NamesStillFound()
    {
        var font = Ot29Parsed(Ot29R(3, 1, 0x0409, 6, "Probe-Regular"), Ot29R(3, 1, 0x0409, 1, "Probe"), Ot29R(1, 0, 0, 1, "Probe Mac"));
        Assert.Equal(("Probe", "Probe-Regular"), (font.FamilyName, font.PostScriptName));
    }

    /// <summary>
    /// OT 30.3 sentences 6–7 (reader, today's behaviour; question 18): the language ID has no influence — (3,1,0x0407,1) „Probe DE“ before
    /// (3,1,0x0409,1) „Probe EN“: the family name is „Probe DE“.
    /// </summary>
    [Fact]
    public void Ot29_GermanBeforeEnglish_TodayTheFirst()
    {
        Assert.Equal("Probe DE", Ot29Parsed(Ot29R(3, 1, 0x0407, 1, "Probe DE"), Ot29R(3, 1, 0x0409, 1, "Probe EN")).FamilyName);
    }

    /// <summary>
    /// OT 30.3 sentences 8–9 (reader): a version 0 table whose record has a language ID of 0x8000 (a tag reference) is still read — family name
    /// „Probe“. (The writer side: <c>Ot29_GlyphlessFont_NameTableLayout</c> and the existing <c>MacOnlyNames_GiveTheFamily_GlyphlessNamesInUsEnglish</c>.)
    /// </summary>
    [Fact]
    public void Ot29_Version0WithLanguageId0x8000_NameRead()
    {
        Assert.Equal("Probe", Ot29Parsed(Ot29R(3, 1, 0x8000, 1, "Probe")).FamilyName);
    }

    // ------------------------------------------------------------ OT 30.4 name IDs

    /// <summary>
    /// OT 30.4 sentences 7–10 (writer): the text recognition font is a family of one regular face — fsSelection without bit 0 and 5, with bit 6, and
    /// the family name (ID 1) „PDFKneadOcrText“.
    /// </summary>
    [Fact]
    public void Ot29_GlyphlessFont_OneRegularFace()
    {
        var selection = Ot27U16(Ot27Table(OcrTextLayer.Glyphless, "OS/2"), 62);
        Assert.Equal((0, 0x40), (selection & 0x21, selection & 0x40));
        Assert.Equal("PDFKneadOcrText", OpenTypeFont.Parse(OcrTextLayer.Glyphless).FamilyName);
    }

    /// <summary>
    /// OT 30.4 sentences 15–17, 25–28 and 34–37 (writer): names of other fonts stay as they are — the subset with cmap and the scrubbed font of
    /// Liberation Sans have its name table byte for byte.
    /// </summary>
    [Fact]
    public void Ot29_SubsetWithCmapAndScrub_NameTableUnchanged()
    {
        var name = Ot27Table(Ot27SansFile.Value, "name");
        var glyphs = new[] { Ot27Glyph('A') };
        Assert.Equal(name, Ot27Table(TrueTypeSubsetter.SubsetWithCmap(Ot27SansFile.Value, glyphs), "name"));
        Assert.Equal(name, Ot27Table(TrueTypeSubsetter.Scrub(Ot27SansFile.Value, glyphs), "name"));
    }

    /// <summary>
    /// OT 30.4 sentences 18–24 (reader and cleaning): the style of ID 2 is part of the system font key, „Regular“ is no style — ID 1 „Probe“ with
    /// ID 2 „Bold“ gives „probebold“, with „Regular“ „probe“; cleaning the notices leaves ID 2.
    /// </summary>
    [Fact]
    public void Ot29_StyleFromId2_RegularIsNoStyle_CleaningKeepsId2()
    {
        var bold = Ot29Font(new(3, 1, 0x0409, 0, Ot29Copyright), Ot29R(3, 1, 0x0409, 1, "Probe"), Ot29R(3, 1, 0x0409, 2, "Bold"));
        Assert.Contains("probebold", Ot29SystemFontNames(bold).Keys);
        Assert.Contains("probe", Ot29SystemFontNames(Ot29Font(Ot29R(3, 1, 0x0409, 1, "Probe"), Ot29R(3, 1, 0x0409, 2, "Regular"))).Keys);
        var clean = Assert.IsType<byte[]>(ObjectMetadata.FontNotices(bold));
        Assert.Equal(Ot29Text(bold, 3, 2), Ot29Text(clean, 3, 2));
    }

    /// <summary>
    /// OT 30.4 sentences 29 and 125 (writer, today's behaviour; question 27): the text recognition font has only the names 1 and 6 — no style name
    /// (ID 2) and no full name (ID 4).
    /// </summary>
    [Fact]
    public void Ot29_GlyphlessFont_TodayOnlyNameIds1And6()
    {
        var name = Ot27Table(OcrTextLayer.Glyphless, "name");
        var ids = Enumerable.Range(0, Ot27U16(name, 2)).Select(i => Ot27U16(name, 6 + 12 * i + 6)).Distinct().Order().ToArray();
        Assert.Equal(new[] { 1, 6 }, ids);
    }

    /// <summary>
    /// OT 30.4 sentences 31–33 (reader): without ID 1 the full name stands in — a font with only ID 4 „Probe Sans Bold“: family name „Probe Sans
    /// Bold“, system font key „probesansbold“.
    /// </summary>
    [Fact]
    public void Ot29_WithoutId1_TheFullNameStandsIn()
    {
        var file = Ot29Font(Ot29R(3, 1, 0x0409, 4, "Probe Sans Bold"));
        Assert.Equal("Probe Sans Bold", OpenTypeFont.Parse(file).FamilyName);
        Assert.Contains("probesansbold", Ot29SystemFontNames(file).Keys);
    }

    /// <summary>
    /// OT 30.4 sentences 45–47 (writer): the PostScript name of the text recognition font has at most 63 characters, only codes 33–126 and none of
    /// [](){}&lt;&gt;/% .
    /// </summary>
    [Fact]
    public void Ot29_GlyphlessFont_PostScriptNameOfAllowedCharacters()
    {
        var ps = OpenTypeFont.Parse(OcrTextLayer.Glyphless).PostScriptName;
        Assert.InRange(ps.Length, 1, 63);
        Assert.All(ps, c => Assert.True(c is >= '!' and <= '~' && "[](){}<>/%".IndexOf(c) < 0, $"Zeichen {c}"));
    }

    /// <summary>
    /// OT 30.4 sentences 73–78, 80–81 and 123–124 (reader and cleaning): typographic names go first — ID 16 „Probe“ and 17 „Kursiv“ beside ID 1
    /// „Probe Alt“ and 2 „Regular“: system font key „probekursiv“ and family „Probe“; without 16 and 17 the key comes from 1 and 2 („probealt“);
    /// cleaning the notices leaves 16 and 17.
    /// </summary>
    [Fact]
    public void Ot29_TypographicNames_KeyFrom16And17_Otherwise1And2_CleaningKeepsThem()
    {
        Ot29Record[] common = [new(3, 1, 0x0409, 0, Ot29Copyright), Ot29R(3, 1, 0x0409, 1, "Probe Alt"), Ot29R(3, 1, 0x0409, 2, "Regular"),
            Ot29R(3, 1, 0x0409, 6, "PrbX")];
        var typographic = Ot29Font([.. common, Ot29R(3, 1, 0x0409, 16, "Probe"), Ot29R(3, 1, 0x0409, 17, "Kursiv")]);
        var (keys, family, _) = Ot29SystemFontNames(typographic);
        Assert.Contains("probekursiv", keys);
        Assert.DoesNotContain("probealt", keys);
        Assert.Equal("Probe", family);
        Assert.Contains("probealt", Ot29SystemFontNames(Ot29Font(common)).Keys);
        var clean = Assert.IsType<byte[]>(ObjectMetadata.FontNotices(typographic));
        Assert.Equal(Ot29Text(typographic, 3, 16), Ot29Text(clean, 3, 16));
        Assert.Equal(Ot29Text(typographic, 3, 17), Ot29Text(clean, 3, 17));
    }

    /// <summary>
    /// OT 30.4 sentences 94–104 and 117–120 (cleaning and writer): WWS names stay — cleaning the notices leaves IDs 21 and 22; the text recognition
    /// font has neither and fsSelection bit 8 clear.
    /// </summary>
    [Fact]
    public void Ot29_WwsNames_CleaningKeepsThem_GlyphlessFontHasNone()
    {
        var file = Ot29Font(new(3, 1, 0x0409, 0, Ot29Copyright), Ot29R(3, 1, 0x0409, 1, "Probe"), Ot29R(3, 1, 0x0409, 21, "Probe WWS"),
            Ot29R(3, 1, 0x0409, 22, "Halbfett"));
        var clean = Assert.IsType<byte[]>(ObjectMetadata.FontNotices(file));
        Assert.Equal(Ot29Text(file, 3, 21), Ot29Text(clean, 3, 21));
        Assert.Equal(Ot29Text(file, 3, 22), Ot29Text(clean, 3, 22));
        Assert.Null(Ot29Text(OcrTextLayer.Glyphless, 3, 21));
        Assert.Null(Ot29Text(OcrTextLayer.Glyphless, 3, 22));
        Assert.Equal(0, Ot27U16(Ot27Table(OcrTextLayer.Glyphless, "OS/2"), 62) & 0x100);
    }

    /// <summary>
    /// OT 30.4 sentences 105–110 (cleaning): palette names are not kept — (3,1,0x0409,23) „Hell“ becomes spaces of the same length, and CPAL stays
    /// byte for byte.
    /// </summary>
    [Fact]
    public void Ot29_PaletteName_CleaningMakesSpaces_CpalUnchanged()
    {
        byte[] cpal = [0, 0, 0, 1, 0, 1, 0, 1, 0, 0, 0, 14, 0, 0, 0, 0xFF, 0xFF, 0xFF];
        var file = Ot27SansWith(("name", Ot29Name([Ot29R(3, 1, 0x0409, 1, "Probe"), Ot29R(3, 1, 0x0409, 23, "Hell")])), ("CPAL", cpal));
        var clean = Assert.IsType<byte[]>(ObjectMetadata.FontNotices(file));
        var text = Ot29Text(clean, 3, 23)!;
        Assert.Equal(8, text.Length);
        Assert.True(Ot29AllUtf16Spaces(text));
        Assert.Equal(cpal, Ot27Table(clean, "CPAL"));
    }

    /// <summary>
    /// OT 30.4 sentences 111–116 (cleaning and scrubbing): the variations PostScript name prefix stays — ID 25 „ProbeVF“ is left byte for byte by
    /// cleaning the notices and by scrubbing.
    /// </summary>
    [Fact]
    public void Ot29_VariationsPrefix_CleaningAndScrubKeepIt()
    {
        var file = Ot29Font(new(3, 1, 0x0409, 0, Ot29Copyright), Ot29R(3, 1, 0x0409, 1, "Probe"), Ot29R(3, 1, 0x0409, 25, "ProbeVF"));
        var expected = Encoding.BigEndianUnicode.GetBytes("ProbeVF");
        Assert.Equal(expected, Ot29Text(Assert.IsType<byte[]>(ObjectMetadata.FontNotices(file)), 3, 25));
        Assert.Equal(expected, Ot29Text(TrueTypeSubsetter.Scrub(file, [Ot27Glyph('A')]), 3, 25));
    }

    // ------------------------------------------------------------ OT 30.5 platforms and encodings

    /// <summary>
    /// OT 30.5.1 sentences 1–6 (reader): Unicode records are read, records of platform 2 are not — only (0,3,0) for IDs 1 and 6: both names; a
    /// font with (1,0,0,1) „Probe Mac“ and (2,0,0,1) „Probe ISO“: „Probe Mac“; with only (2,0,0,1): no family name.
    /// (Platform 1 alone: the existing <c>MacOnlyNames_GiveTheFamily_GlyphlessNamesInUsEnglish</c>.)
    /// </summary>
    [Fact]
    public void Ot29_UnicodePlatformRead_Platform2PassedOver()
    {
        var unicode = Ot29Parsed(Ot29R(0, 3, 0, 1, "Probe Uni"), Ot29R(0, 3, 0, 6, "ProbeUni-Regular"));
        Assert.Equal(("Probe Uni", "ProbeUni-Regular"), (unicode.FamilyName, unicode.PostScriptName));
        Assert.Equal("Probe Mac", Ot29Parsed(Ot29R(1, 0, 0, 1, "Probe Mac"), new(2, 0, 0, 1, Encoding.ASCII.GetBytes("Probe ISO"))).FamilyName);
        Assert.Equal("", Ot29Parsed(new Ot29Record(2, 0, 0, 1, Encoding.ASCII.GetBytes("Probe ISO"))).FamilyName);
    }

    /// <summary>
    /// OT 30.5.2 sentences 1–12 (reader): every Unicode encoding is UTF-16BE — only (0,4,0,1) „Probe 𝔸“ (a surrogate pair) and only (0,0,0,6): the
    /// family name with the character beyond the BMP, and the PostScript name.
    /// </summary>
    [Fact]
    public void Ot29_UnicodeFullRepertoireName_KeepsTheSupplementaryCharacter()
    {
        var font = Ot29Parsed(Ot29R(0, 4, 0, 1, "Probe 𝔸"), Ot29R(0, 0, 0, 6, "Probe-A"));
        Assert.Equal(("Probe 𝔸", "Probe-A"), (font.FamilyName, font.PostScriptName));
    }

    /// <summary>OT 30.5.2 sentences 13–16 (reader): (0,3,0,1) „Probe“ and (0,3,0x8000,6) „Probe-Regular“ — both names are read.</summary>
    [Fact]
    public void Ot29_UnicodeNamesWithLanguage0x8000_Read()
    {
        var font = Ot29Parsed(Ot29R(0, 3, 0, 1, "Probe"), Ot29R(0, 3, 0x8000, 6, "Probe-Regular"));
        Assert.Equal(("Probe", "Probe-Regular"), (font.FamilyName, font.PostScriptName));
    }

    /// <summary>
    /// OT 30.5.3.1 sentences 1–20 (reader, today's behaviour; question 18): of the Macintosh encodings only Roman is read, and as Latin-1 — only
    /// (1,0,0,1) with the bytes 43 61 66 8E: „Caf“ and U+008E (Mac Roman would give „Café“); only (1,1,11,1): no family name.
    /// </summary>
    [Fact]
    public void Ot29_MacRomanName_TodayReadAsLatin1_OtherMacScriptsPassedOver()
    {
        Assert.Equal("Caf\u008E", Ot29Parsed(new Ot29Record(1, 0, 0, 1, [0x43, 0x61, 0x66, 0x8E])).FamilyName);
        Assert.Equal("", Ot29Parsed(new Ot29Record(1, 1, 11, 1, [0x43, 0x61, 0x66, 0x8E])).FamilyName);
    }

    /// <summary>
    /// OT 30.5.4.1 sentences 1–4 and 13 (reader): Windows symbol and full-repertoire records are read — only (3,0,0x0409,1) or only (3,10,0x0409,1):
    /// the family name; the system font index passes (3,10) over today (no family, no key).
    /// </summary>
    [Fact]
    public void Ot29_WindowsSymbolAndFullRepertoireNames_Read_SystemIndexSkipsFullRepertoire()
    {
        Assert.Equal("Probe Sym", Ot29Parsed(Ot29R(3, 0, 0x0409, 1, "Probe Sym")).FamilyName);
        var full = Ot29Font(Ot29R(3, 10, 0x0409, 1, "Probe Voll"));
        Assert.Equal("Probe Voll", OpenTypeFont.Parse(full).FamilyName);
        var (keys, family, _) = Ot29SystemFontNames(full);
        Assert.Null(family);
        Assert.Empty(keys);
    }

    /// <summary>
    /// OT 30.5.4.1 sentences 5–12, 17 and 19–21 (reader, today's behaviour; question 18): the old code page encodings are not decoded — only
    /// (3,4,0x0404,1) with the Big5 bytes of „中文“ (A4 A4 A4 E5): the family name is these bytes read as UTF-16BE.
    /// </summary>
    [Fact]
    public void Ot29_WindowsBig5Name_TodayReadAsUtf16()
    {
        byte[] big5 = [0xA4, 0xA4, 0xA4, 0xE5];
        var family = Ot29Parsed(new Ot29Record(3, 4, 0x0404, 1, big5)).FamilyName;
        Assert.Equal(Encoding.BigEndianUnicode.GetString(big5), family);
        Assert.NotEqual("中文", family);
    }

    /// <summary>
    /// OT 30.5.4.1 sentences 15 and 18 (cleaning): cleaned Windows texts stay valid UTF-16BE — the copyright (3,1) of Liberation Sans becomes 00 20
    /// pairs of its length.
    /// </summary>
    [Fact]
    public void Ot29_CleanedWindowsTexts_AreUtf16Spaces()
    {
        var before = Ot29Text(Ot27SansFile.Value, 3, 0)!;
        var after = Ot29Text(Assert.IsType<byte[]>(ObjectMetadata.FontNotices(Ot27SansFile.Value)), 3, 0)!;
        Assert.Equal(before.Length, after.Length);
        Assert.True(Ot29AllUtf16Spaces(after));
    }

    /// <summary>
    /// OT 30.5.4.1 sentences 14 and 16 (writer): cmap and name agree — the text recognition font has only a (3,1) cmap and its Windows names are
    /// (3,1) (beside them the Macintosh PostScript name, FG 1151). (The symbolic rebuild: the existing
    /// <c>FontScrubTests.RebuiltAsSymbolic_NamesPanoseAndRangeFollow</c>, FG 1146.)
    /// </summary>
    [Fact]
    public void Ot29_GlyphlessFont_CmapAndNamesBothWindowsUnicode()
    {
        var font = OpenTypeFont.Parse(OcrTextLayer.Glyphless);
        Assert.Equal(new[] { (3, 1) }, font.CmapSubtables.ToArray());
        var name = Ot27Table(OcrTextLayer.Glyphless, "name");
        var records = Enumerable.Range(0, Ot27U16(name, 2)).Select(i => (Ot27U16(name, 6 + 12 * i), Ot27U16(name, 8 + 12 * i))).ToList();
        Assert.All(records.Where(r => r.Item1 == 3), r => Assert.Equal((3, 1), r));
        Assert.All(records.Where(r => r.Item1 != 3), r => Assert.Equal((1, 0), r));
    }
}
