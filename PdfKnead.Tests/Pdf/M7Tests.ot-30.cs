using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-30 (OpenType specification, OS/2 table: versions 0 to 5, xAvgCharWidth, usWeightClass, usWidthClass, fsType and the sub- and
/// superscript fields): the tests the drafts of the sentence-by-sentence comparison asked for — which OS/2 fields are read, how the licence bits
/// decide embedding, and what the own font of the text recognition writes.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-30, also used by ot-31 and ot-32)

    private const string Ot30Regular = "LiberationSans-Regular.ttf";

    private static byte[] Ot30File(string file = Ot30Regular) => EmbeddedFonts.LoadStandard(file);

    private static byte[] Ot30Os2Of(byte[] font) => OpenTypeFont.Parse(font).TableData("OS/2").ToArray();

    private static void Ot30Set(byte[] table, int at, int value) => BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(at), (ushort)value);

    private static int Ot30U16(ReadOnlySpan<byte> table, int at) => BinaryPrimitives.ReadUInt16BigEndian(table[at..]);

    /// <summary>
    /// A Liberation font whose OS/2 is changed: cut or padded with zeros to <paramref name="length"/>, the version set to <paramref name="version"/>,
    /// then <paramref name="change"/> applied.
    /// </summary>
    private static byte[] Ot30WithOs2(Action<byte[]>? change = null, int? version = null, int? length = null, string file = Ot30Regular)
    {
        var source = Ot30File(file);
        var os2 = Ot30Os2Of(source);
        if (length is { } n) Array.Resize(ref os2, n);
        if (version is { } v && os2.Length >= 2) Ot30Set(os2, 0, v);
        change?.Invoke(os2);
        return ColorFontBuilder.WithTables(source, new Dictionary<string, byte[]?> { ["OS/2"] = os2 });
    }

    /// <summary>The fsType of a Liberation Sans copy set to <paramref name="fsType"/> (and the OS/2 version to <paramref name="version"/>).</summary>
    private static byte[] Ot30WithFsType(int fsType, int? version = null) => Ot30WithOs2(o => Ot30Set(o, 8, fsType), version);

    /// <summary>The style RichFonts reads from a system font (private RichFonts.StyleOf): bold and italic.</summary>
    private static (bool Bold, bool Italic) Ot30StyleOf(byte[] font) =>
        ((bool, bool))typeof(RichFonts).GetMethod("StyleOf", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [OpenTypeFont.Parse(font)])!;

    /// <summary>
    /// What RichFonts.System makes of a system font with this style when <paramref name="bold"/> and <paramref name="italic"/> are asked for:
    /// a missing bold or italic is faked.
    /// </summary>
    private static (bool FakeBold, bool FakeItalic) Ot30Fakes(byte[] font, bool bold, bool italic)
    {
        var (fontBold, fontItalic) = Ot30StyleOf(font);
        return (bold && !fontBold, italic && !fontItalic);
    }

    /// <summary>The descriptor StandardFontEmbedding writes from a font's own tables (private Descriptor).</summary>
    private static PdfDictionary Ot30StandardDescriptor(byte[] font, string file = Ot30Regular) =>
        (PdfDictionary)typeof(StandardFontEmbedding).GetMethod("Descriptor", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [OpenTypeFont.Parse(font), PdfName.Get("Probe"), file])!;

    /// <summary>A font as typed text uses it (ElementFace under a fresh key).</summary>
    private static ElementFace Ot30Face(byte[] font, bool italic = false) =>
        ElementFace.For($"probe-m7-ot30-{Guid.NewGuid():N}", () => OpenTypeFont.Parse(font), false, false, italic);

    /// <summary>The descriptor of typed text („Probe“) saved in the font given.</summary>
    private static PdfDictionary Ot30SavedDescriptor(byte[] font, bool italic = false) => Iso04Descriptor(Iso03Saved(Ot30Face(font, italic), "Probe"));

    /// <summary>The OS/2 of the font of the text recognition.</summary>
    private static byte[] Ot30Glyphless => Ot05GlyphlessTable("OS/2");

    /// <summary>The program behind a font of a document (the FontProgram field of the PdfFont).</summary>
    private static FontProgram Ot30Program(PdfFont font)
    {
        for (var type = font.GetType(); type is not null; type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                if (field.FieldType == typeof(FontProgram) && field.GetValue(font) is FontProgram program) return program;
        throw new InvalidOperationException("kein Schriftprogramm");
    }

    // ------------------------------------------------------------ 31, 31.1

    /// <summary>
    /// OT 31 sentence 1: the own font of the text recognition has an OS/2 of 96 bytes, a subset of Liberation Sans keeps the OS/2 of the source,
    /// and a font without OS/2 is read on (weight 400, embeddable).
    /// </summary>
    [Fact]
    public void Ot30_Os2_InOwnFontAndSubset_FontWithoutOs2ReadTolerantly()
    {
        Assert.True(Ot05Glyphless.Value.HasTable("OS/2"));
        Assert.Equal(96, Ot30Glyphless.Length);
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(Ot30File(), Ot05Glyphs("AB")));
        Assert.True(subset.HasTable("OS/2"));
        var without = OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot30File(), new Dictionary<string, byte[]?> { ["OS/2"] = null }));
        Assert.False(without.HasTable("OS/2"));
        Assert.Equal(400, without.WeightClass);
        Assert.True(RichFonts.Embeddable(without));
    }

    /// <summary>
    /// OT 31.1 sentences 1–2: all six versions are valid, version 4 or higher recommended — the own font writes version 4; Liberation Sans with
    /// its OS/2 set to version 0, 1, 3 and 5 is read and embeddable.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Ot30_EveryVersionRead_OwnFontVersion4(int version)
    {
        Assert.Equal(4, Ot30U16(Ot30Glyphless, 0));
        var font = OpenTypeFont.Parse(Ot30WithOs2(version: version, length: version == 5 ? 100 : null));
        Assert.Equal(400, font.WeightClass);
        Assert.True(RichFonts.Embeddable(font));
    }

    /// <summary>
    /// OT 31.1.1 sentences 1–17: version 5 (100 bytes) — fsType 0x0002 at offset 8 makes the font not embeddable, fsType 0 embeddable;
    /// usWeightClass at offset 4 is read (400).
    /// </summary>
    [Fact]
    public void Ot30_Version5_FsTypeAndWeightRead()
    {
        var restricted = OpenTypeFont.Parse(Ot30WithOs2(o => Ot30Set(o, 8, 0x0002), 5, 100));
        Assert.False(RichFonts.Embeddable(restricted));
        Assert.Equal(400, restricted.WeightClass);
        Assert.True(RichFonts.Embeddable(OpenTypeFont.Parse(Ot30WithOs2(o => Ot30Set(o, 8, 0), 5, 100))));
    }

    /// <summary>
    /// OT 31.1.1 sentences 18–27: fsSelection at offset 62 of a version-5 table — Liberation Sans Italic (bit 0) reads as italic; the same table
    /// with fsSelection 0x0040 does not.
    /// </summary>
    [Fact]
    public void Ot30_Version5_FsSelectionGivesItalic()
    {
        Assert.True(Ot30StyleOf(Ot30WithOs2(version: 5, length: 100, file: "LiberationSans-Italic.ttf")).Italic);
        Assert.False(Ot30StyleOf(Ot30WithOs2(o => Ot30Set(o, 62, 0x0040), 5, 100, "LiberationSans-Italic.ttf")).Italic);
    }

    /// <summary>
    /// OT 31.1.1 sentences 28–41, 31.1.2 sentences 31–42, 31.1.3–31.1.5, 31.1.6 sentences 29–36, 31.2.1 sentences 1–5 (and ot-32: 31.2.28
    /// sentences 43–47, 31.2.29–31.2.35 on the versions, 31.2.30 sentences 1–3): sCapHeight at offset 88 is read only from version 2 on and only
    /// where the table holds it — then /CapHeight is 1409 × 1000 / 2048 = 688 (StandardFontEmbedding and typed text alike); a table of version 0
    /// or 1 (also one of 96 or 102 bytes) or one too short gives /CapHeight from the ascender of hhea. A table cut after usLastCharIndex (68 bytes)
    /// is read and the font stays embeddable.
    /// </summary>
    [Theory]
    [InlineData(5, 100, true)]
    [InlineData(4, 96, true)]
    [InlineData(3, 96, true)]
    [InlineData(2, 96, true)]
    [InlineData(2, 88, false)]
    [InlineData(1, 96, false)]
    [InlineData(1, 86, false)]
    [InlineData(1, 102, false)]
    [InlineData(0, 96, false)]
    [InlineData(0, 102, false)]
    [InlineData(0, 78, false)]
    [InlineData(0, 68, false)]
    public void Ot30_CapHeight_FromOs2OnlyFromVersion2(int version, int length, bool fromOs2)
    {
        var font = Ot30WithOs2(o =>
        {
            if (o.Length >= 90) Ot30Set(o, 88, 1409);
        }, version, length);
        var parsed = OpenTypeFont.Parse(font);
        Assert.True(RichFonts.Embeddable(parsed));
        var standard = Ot30StandardDescriptor(font).GetNumber(PdfName.Get("CapHeight"));
        var typed = Ot30SavedDescriptor(font).GetNumber(PdfName.Get("CapHeight"));
        if (fromOs2)
        {
            Assert.Equal(688, standard);
            Assert.Equal(688, typed);
        }
        else
        {
            Assert.Equal(Math.Round(parsed.Ascent * 1000.0 / parsed.UnitsPerEm), standard);
            Assert.Equal(Math.Round(parsed.Ascent * 1000.0 / parsed.UnitsPerEm * 0.7), typed);
        }
    }

    /// <summary>
    /// OT 31.1.2 sentences 1–42 (and 31.2.3, 31.2.4, 31.2.5 sentences 1–6, 12 and 19, 31.2.8, 31.2.12): the own font writes OS/2 version 4 of 96
    /// bytes — version 4, xAvgCharWidth 500, usWeightClass 400, usWidthClass 5, fsType 0 (no reserved bit), the sub- and superscript offsets 0,
    /// fsSelection 0x0040, usFirstCharIndex 0x20, usLastCharIndex 0xFFFF, sTypoAscender 1000, usWinAscent 1000, sCapHeight 1000, usBreakChar 0x20.
    /// </summary>
    [Fact]
    public void Ot30_GlyphlessOs2_Version4FieldsAtTheirOffsets()
    {
        var os2 = Ot30Glyphless;
        Assert.Equal(96, os2.Length);
        Assert.Equal(4, Ot30U16(os2, 0));
        Assert.Equal(500, Ot30U16(os2, 2));
        Assert.Equal(400, Ot30U16(os2, 4));
        Assert.Equal(5, Ot30U16(os2, 6));
        Assert.Equal(0, Ot30U16(os2, 8));
        Assert.Equal(0, Ot30U16(os2, 8) & 0xFCF0);
        Assert.Equal(0, Ot30U16(os2, 14));
        Assert.Equal(0, Ot30U16(os2, 22));
        Assert.Equal(0x0040, Ot30U16(os2, 62));
        Assert.Equal(0x0020, Ot30U16(os2, 64));
        Assert.Equal(0xFFFF, Ot30U16(os2, 66));
        Assert.Equal(1000, Ot30U16(os2, 68));
        Assert.Equal(1000, Ot30U16(os2, 74));
        Assert.Equal(1000, Ot30U16(os2, 88));
        Assert.Equal(0x0020, Ot30U16(os2, 92));
    }

    /// <summary>
    /// OT 31.1.5 sentences 1–19: version 1 (86 bytes) — fsType 0x0002 makes the font not embeddable; usWeightClass 700 reads as bold.
    /// </summary>
    [Fact]
    public void Ot30_Version1_FsTypeAndWeightRead()
    {
        Assert.False(RichFonts.Embeddable(OpenTypeFont.Parse(Ot30WithOs2(o => Ot30Set(o, 8, 0x0002), 1, 86))));
        Assert.True(Ot30StyleOf(Ot30WithOs2(o => Ot30Set(o, 4, 700), 1, 86)).Bold);
    }

    /// <summary>OT 31.1.5 sentences 20–29: fsSelection in a version-1 table (86 bytes) — Liberation Sans Italic reads as italic.</summary>
    [Fact]
    public void Ot30_Version1_FsSelectionGivesItalic()
    {
        Assert.True(Ot30StyleOf(Ot30WithOs2(version: 1, length: 86, file: "LiberationSans-Italic.ttf")).Italic);
    }

    /// <summary>
    /// OT 31.1.6 sentences 1–18: version 0 cut to 10 bytes — fsType 0x0002 is still read (not embeddable); cut to 8 bytes fsType is missing and
    /// the font counts as embeddable.
    /// </summary>
    [Fact]
    public void Ot30_Version0_FsTypeReadOnlyWhereTheTableHoldsIt()
    {
        Assert.False(RichFonts.Embeddable(OpenTypeFont.Parse(Ot30WithOs2(o => Ot30Set(o, 8, 0x0002), 0, 10))));
        var cut = Ot30WithOs2(version: 0, length: 8);
        Assert.True(RichFonts.Embeddable(OpenTypeFont.Parse(cut)));
    }

    /// <summary>
    /// OT 31.1.6 sentences 19–28: Liberation Sans Bold with its OS/2 (version 0) cut to 62 bytes — fsSelection is not read beyond the table: the
    /// style is regular and OpenTypeFont.Selection is 0.
    /// </summary>
    [Fact]
    public void Ot30_Version0CutBeforeFsSelection_ReadsRegular()
    {
        var font = Ot30WithOs2(version: 0, length: 62, file: "LiberationSans-Bold.ttf");
        Assert.Equal((false, false), Ot30StyleOf(font));
        Assert.Equal(0, OpenTypeFont.Parse(font).Selection);
    }

    // ------------------------------------------------------------ 31.2.2 xAvgCharWidth

    /// <summary>
    /// OT 31.2.2 sentences 1–5 (today's behaviour, question 7 open): scrubbing Liberation Sans down to the glyphs of „Text“ clears the advances
    /// of the others, but xAvgCharWidth stays 1187 from the source.
    /// </summary>
    [Fact]
    public void Ot30_Scrub_KeepsXAvgCharWidthOfTheSource()
    {
        var scrubbed = TrueTypeSubsetter.Scrub(Ot30File(), Ot05Glyphs("Text"));
        Assert.Equal(1187, Ot30U16(Ot30Os2Of(scrubbed), 2));
    }

    /// <summary>
    /// OT 31.2.2 sentences 11–25 (and ot-31: 31.2.18 sentences 185–189): xAvgCharWidth is never computed after the old weights — a subset of
    /// Liberation Sans with OS/2 version 1 (bit 8 of ulUnicodeRange1 set) keeps the OS/2 byte for byte.
    /// </summary>
    [Fact]
    public void Ot30_SubsetOfVersion1_KeepsOs2ByteForByte()
    {
        var font = Ot30WithOs2(o => o[44] |= 0x01, 1, 86);
        var source = Ot30Os2Of(font);
        Assert.Equal(source, Ot30Os2Of(TrueTypeSubsetter.Subset(font, Ot05Glyphs("AB"))));
    }

    // ------------------------------------------------------------ 31.2.3 usWeightClass

    /// <summary>
    /// OT 31.2.3 sentences 1–4: usWeightClass decides bold — Liberation Sans Bold (700) asked for bold needs no faked bold, Liberation Sans
    /// Regular (400) does.
    /// </summary>
    [Fact]
    public void Ot30_WeightClass700_NoFakedBold()
    {
        Assert.False(Ot30Fakes(Ot30File("LiberationSans-Bold.ttf"), true, false).FakeBold);
        Assert.True(Ot30Fakes(Ot30File(), true, false).FakeBold);
    }

    /// <summary>OT 31.2.3 sentences 5–14: with fsSelection 0x0040, weight 500 (Medium) reads regular, 600 (Semi-bold) and 900 (Black) bold.</summary>
    [Theory]
    [InlineData(500, false)]
    [InlineData(600, true)]
    [InlineData(900, true)]
    public void Ot30_WeightClass_BoldFrom600(int weight, bool bold)
    {
        Assert.Equal(bold, Ot30StyleOf(Ot30WithOs2(o =>
        {
            Ot30Set(o, 4, weight);
            Ot30Set(o, 62, 0x0040);
        })).Bold);
    }

    // ------------------------------------------------------------ 31.2.5 fsType

    /// <summary>
    /// OT 31.2.5 sentences 1–6, 12–19, 23, 26–35: a system font is embedded and offered for new text unless its fsType restricts it — 0x0002
    /// (restricted licence), bit 8 (no subsetting), bit 9 (bitmaps only, also with 0x0008) exclude it; 0, 4 and 8, the reserved bits (0xFCF0),
    /// several of bits 0–3 together (0x000C, 0x0006: the least restrictive applies) and the obsolete bit 0 (0x0001, 0x0003) do not.
    /// RichFonts.Embeddable and the font index (SystemFonts.Names) agree.
    /// </summary>
    [Theory]
    [InlineData(0x0000, true)]
    [InlineData(0x0002, false)]
    [InlineData(0x0004, true)]
    [InlineData(0x0008, true)]
    [InlineData(0xFCF0, true)]
    [InlineData(0x0100, false)]
    [InlineData(0x0200, false)]
    [InlineData(0x0208, false)]
    [InlineData(0x000C, true)]
    [InlineData(0x0006, true)]
    [InlineData(0x0001, true)]
    [InlineData(0x0003, true)]
    public void Ot30_FsType_DecidesEmbedding(int fsType, bool embeddable)
    {
        var font = Ot30WithFsType(fsType);
        Assert.Equal(embeddable, RichFonts.Embeddable(OpenTypeFont.Parse(font)));
        Assert.Equal(embeddable, Ot05SystemFontNames(font).Usable);
    }

    /// <summary>
    /// OT 31.2.5 sentences 38–42: in versions 0–2 several bits meant the least restrictive — version 2 with 0x000C and version 3 with 0x0006 are
    /// embeddable.
    /// </summary>
    [Theory]
    [InlineData(2, 0x000C)]
    [InlineData(3, 0x0006)]
    public void Ot30_FsTypeSeveralBits_LeastRestrictive(int version, int fsType)
    {
        Assert.True(RichFonts.Embeddable(OpenTypeFont.Parse(Ot30WithFsType(fsType, version))));
    }

    /// <summary>
    /// OT 31.2.5 sentences 36–37: versions 0 and 1 know only bits 0–3, bits 4–15 are to be ignored there — Liberation Sans with OS/2 version 1 and
    /// fsType 0x0100 should be embeddable.
    /// </summary>
    [Fact]
    public void Ot30_FsTypeBit8_IgnoredInVersion1()
    {
        var font = Ot30WithFsType(0x0100, 1);
        Assert.True(RichFonts.Embeddable(OpenTypeFont.Parse(font)));
        Assert.True(Ot05SystemFontNames(font).Usable);
    }

    /// <summary>
    /// OT 31.2.5 sentences 7, 10–11 and 24: an editable font (0x0008) is embedded with new text — typed text saved in it carries the subset as
    /// FontFile2 with fsType 0x0008 unchanged; subset, scrub and rebuild of such a font keep 0x0008.
    /// </summary>
    [Fact]
    public void Ot30_EditableFont_EmbeddedWithItsFsType()
    {
        var font = Ot30WithFsType(0x0008);
        Assert.True(RichFonts.Embeddable(OpenTypeFont.Parse(font)));
        var program = Ot30SavedDescriptor(font).GetStream(PdfName.Get("FontFile2"));
        Assert.NotNull(program);
        Assert.Equal(0x0008, Ot30U16(Ot30Os2Of(program!.RawData!), 8));
        var glyphs = Ot05Glyphs("AB");
        Assert.Equal(0x0008, Ot30U16(Ot30Os2Of(TrueTypeSubsetter.Subset(font, glyphs)), 8));
        Assert.Equal(0x0008, Ot30U16(Ot30Os2Of(TrueTypeSubsetter.Scrub(font, glyphs)), 8));
        Assert.Equal(0x0008, Ot30U16(Ot30Os2Of(TrueTypeSubsetter.Rebuild(font, glyphs.ToDictionary(g => g, _ => 1000), [], null)), 8));
    }

    /// <summary>
    /// OT 31.2.5 sentence 24 (today's behaviour, question 29 open): an installed font with CFF outlines is saved as a bare CFF — typed text in an
    /// OpenType font with CFF outlines and fsType 0x0004 becomes FontFile3 /CIDFontType0C without OS/2, so without fsType.
    /// </summary>
    [Fact]
    public void Ot30_CffFontSaved_AsBareCffWithoutFsType()
    {
        var tables = new Dictionary<string, byte[]?> { ["OS/2"] = Ot30WithFsTypeTable(Ot05NotoExcerpt.Value, 0x0004) };
        var font = ColorFontBuilder.WithTables(Ot05NotoExcerpt.Value, tables);
        var face = ElementFace.For($"probe-m7-ot30-cff-{Guid.NewGuid():N}", () => OpenTypeFont.Parse(font), false, false, false);
        var descriptor = Iso04Descriptor(face.SavedFont(new Dictionary<int, string> { [face.Glyph('A')] = "A" }));
        var program = descriptor.GetStream(PdfName.Get("FontFile3"));
        Assert.NotNull(program);
        Assert.Equal("CIDFontType0C", program!.Dictionary.GetName(PdfName.Subtype)?.Value);
        Assert.Equal(1, program.RawData![0]);   // a CFF header, not an OpenType file with an OS/2
    }

    private static byte[] Ot30WithFsTypeTable(byte[] font, int fsType)
    {
        var os2 = Ot30Os2Of(font);
        Ot30Set(os2, 8, fsType);
        return os2;
    }

    /// <summary>
    /// OT 31.2.5 sentence 8 (today's behaviour, question 8 open): an embedded TrueType font with fsType 0x0002 is still subset when compressing
    /// losslessly — the saved program differs from the source and the outline of „C“ (not shown) is empty.
    /// </summary>
    [Fact]
    public void Ot30_RestrictedEmbeddedFont_SubsetWhenCompressing()
    {
        var (source, saved) = Ot30CompressedProgram(0x0002);
        Assert.NotEqual(source, saved);
        var c = OpenTypeFont.Parse(source).GlyphForUnicode('C');
        Assert.False(OpenTypeFont.Parse(source).Outline(c).IsEmpty);
        Assert.True(OpenTypeFont.Parse(saved).Outline(c).IsEmpty);
    }

    /// <summary>
    /// OT 31.2.5 sentences 13–14 (today's behaviour, question 8 open): an embedded TrueType font with fsType bit 8 (no subsetting) is subset
    /// when compressing losslessly — the outline of „C“ (not shown) is empty afterwards. A system font with bit 8 is not offered for new text.
    /// </summary>
    [Fact]
    public void Ot30_NoSubsettingEmbeddedFont_SubsetWhenCompressing()
    {
        var (source, saved) = Ot30CompressedProgram(0x0100);
        var c = OpenTypeFont.Parse(source).GlyphForUnicode('C');
        Assert.True(OpenTypeFont.Parse(saved).Outline(c).IsEmpty);
        Assert.Equal(0x0100, Ot30U16(Ot30Os2Of(saved), 8));
        Assert.False(RichFonts.Embeddable(OpenTypeFont.Parse(source)));
        Assert.False(Ot05SystemFontNames(source).Usable);
    }

    /// <summary>„AB“ in an embedded Liberation Sans with the fsType given, saved with lossless compression: the program before and after.</summary>
    private static (byte[] Source, byte[] Saved) Ot30CompressedProgram(int fsType)
    {
        var program = Ot30WithFsType(fsType);
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(program: () => program)());
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Lossless) });
        using var saved = PdfDocument.Open(output.ToArray());
        var stream = Iso04F1(saved).GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile2"))!;
        return (program, saved.GetStreamData(stream));
    }

    /// <summary>
    /// OT 31.2.5 sentence 9 (today's behaviour, question 26 open): a document whose embedded font has fsType 0x0004 (preview and print) opens
    /// editable — its text is edited with the font of the file and saved like any other.
    /// </summary>
    [Fact]
    public void Ot30_PreviewAndPrintFont_DocumentStaysEditable()
    {
        var program = Ot30WithFsType(0x0004);
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(program: () => program)());
        var block = Assert.Single(TextBlocks.Find(doc, doc.Pages[0]));
        var element = EditedText.FromBlock(doc, block);
        Assert.NotNull(Assert.Single(element.Content.Spans).Style.Original);
        var edited = element with { Content = element.Content.Insert(element.Content.Length, "BA") };
        var page = PageItem.From(doc)[0];
        var removed = Redactor.RemoveGlyphs(doc, page.Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [page.WithEditedPage(removed, [edited])], output);
        using var saved = PdfDocument.Open(output.ToArray());
        // the block is as wide as „AB“ and wraps
        var text = PageText.Create(PdfKnead.Core.Pdf.Content.PageInterpreter.BuildText(saved, saved.Pages[0])).Text;
        Assert.Contains("ABBA", string.Concat(text.Where(c => !char.IsWhiteSpace(c))), StringComparison.Ordinal);
    }

    /// <summary>
    /// OT 31.2.5 sentence 25: an embedded font loaded for display lives only as long as its document — after opening a file with an embedded
    /// TrueType font, drawing its page and closing it, the parsed font is collected.
    /// </summary>
    [Fact]
    public void Ot30_EmbeddedFont_GoneWithTheDocument()
    {
        var weak = Ot30OpenDrawClose(PdfARuleCases.WithTrueType()());
        for (var i = 0; i < 3 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "die eingebettete Schrift lebt nach dem Schließen weiter");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Ot30OpenDrawClose(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        using (ContentImageTests.Render(doc, doc.Pages[0]))
        {
        }
        var program = Ot30Program(PdfFont.Get(Iso04F1(doc), doc));
        Assert.True(program.IsEmbedded);
        return new WeakReference(program.OpenType!);
    }

    // ------------------------------------------------------------ 31.2.6–31.2.13 sub- and superscript

    /// <summary>
    /// OT 31.2.6, 31.2.7, 31.2.10, 31.2.11 sentences 1–5 (today's behaviour, question 27 open): the own font writes ySubscriptXSize,
    /// ySubscriptYSize, ySuperscriptXSize and ySuperscriptYSize as 0 (offsets 10, 12, 18, 20), and the offsets 14, 16, 22 and 24 as 0.
    /// </summary>
    [Fact]
    public void Ot30_GlyphlessOs2_SubAndSuperscriptFieldsZeroToday()
    {
        foreach (var at in (int[])[10, 12, 14, 16, 18, 20, 22, 24]) Assert.Equal(0, Ot30U16(Ot30Glyphless, at));
    }

    /// <summary>
    /// OT 31.2.6–31.2.13 (sub- and superscript, sentences on simulated sub- and superscripts): no sub- or superscript is simulated — a „2“ set
    /// 3 points low or 4 points high in existing text keeps its rise when edited, and typed text with a rise is written with Ts and the same
    /// size and position as without (nothing scaled or moved after OS/2).
    /// </summary>
    [Theory]
    [InlineData(-3)]
    [InlineData(4)]
    public void Ot30_Rise_TakenAsIs_NotScaledFromOs2(int rise)
    {
        using var doc = TextBlockTests.Page($"BT /F1 10 Tf 72 700 Td (Wasser H) Tj {rise} Ts (2) Tj 0 Ts (O) Tj ET");
        var block = Assert.Single(TextBlocks.Find(doc, doc.Pages[0]));
        var span = Assert.Single(EditedText.FromBlock(doc, block).Content.Spans, s => s.Text == "2");
        Assert.Equal(rise, span.Style.Rise, 3);
        Assert.Equal(10, span.Style.Size, 3);

        var plain = new RichStyle { Family = "Liberation Sans", Size = 10 };
        string Content(double r)
        {
            // the „2“ in its own colour, so that its run stays apart also without a rise
            var element = new RichTextElement
            {
                Rect = new PdfRectangle(72, 650, 300, 700),
                Content = new RichText([new RichSpan("H", plain), new RichSpan("2", plain with { Color = 0x000001, Rise = r }), new RichSpan("O", plain)]),
            };
            return Encoding.ASCII.GetString(ElementAppearance.For(element).Form.RawData!);
        }
        static List<string> Starts(string content) =>
            [.. System.Text.RegularExpressions.Regex.Matches(content, @"1 0 (\S+) 1 (\S+) \S+ Tm").Select(m => m.Groups[1].Value + " " + m.Groups[2].Value)];
        static List<string> Sizes(string content) => [.. System.Text.RegularExpressions.Regex.Matches(content, @"(\S+) Tf ").Select(m => m.Groups[1].Value)];
        var raised = Content(rise);
        var flat = Content(0);
        Assert.Contains($" {rise} Ts", raised, StringComparison.Ordinal);
        Assert.Equal(3, Starts(raised).Count);
        Assert.Equal(Starts(flat), Starts(raised));
        Assert.All(Sizes(raised), s => Assert.Equal("10", s));
    }
}
