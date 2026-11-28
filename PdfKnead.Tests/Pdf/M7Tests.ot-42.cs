using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-42 (OpenType specification, the tables MERG 51, meta 52 and MVAR 53): the tests the sentence-by-sentence drafts named as
/// missing. PDF-Knead reads neither meta nor MVAR: fonts are chosen by their cmap, and the metrics of a variable font are those of its default
/// instance in hhea, OS/2 and post.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-42)

    /// <summary>A meta table (version 1, flags 0) with the data maps given, each a tag and its text as ASCII.</summary>
    private static byte[] MetaOt42(params (string Tag, string Text)[] maps)
    {
        var header = new byte[16 + 12 * maps.Length];
        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)maps.Length);
        var data = new List<byte>();
        for (var i = 0; i < maps.Length; i++)
        {
            var text = Encoding.ASCII.GetBytes(maps[i].Text);
            Encoding.ASCII.GetBytes(maps[i].Tag).CopyTo(header, 16 + 12 * i);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16 + 12 * i + 4), (uint)(header.Length + data.Count));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16 + 12 * i + 8), (uint)text.Length);
            data.AddRange(text);
        }
        return [.. header, .. data];
    }

    /// <summary>
    /// An MVAR (version 1.0) with one value record per tag, sorted by tag, each 12 bytes (valueRecordSize 12: four further bytes 0xEE after the
    /// three fields, as a later minor version could add), delta set (0, i); and an ItemVariationStore with one region (0 … 1 peaking at 1) and
    /// one ItemVariationData giving every item a delta of +100.
    /// </summary>
    private static byte[] MvarOt42(params string[] tags)
    {
        var sorted = tags.OrderBy(t => t, StringComparer.Ordinal).ToArray();
        const int recordSize = 12;
        var storeAt = 12 + recordSize * sorted.Length;
        var mvar = new List<byte>();
        void U16(int v) => mvar.AddRange([(byte)(v >> 8), (byte)v]);
        void U32(int v) => mvar.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        U16(1);
        U16(0);
        U16(0);
        U16(recordSize);
        U16(sorted.Length);
        U16(storeAt);
        for (var i = 0; i < sorted.Length; i++)
        {
            mvar.AddRange(Encoding.ASCII.GetBytes(sorted[i]));
            U16(0);
            U16(i);
            mvar.AddRange([0xEE, 0xEE, 0xEE, 0xEE]);
        }
        // ItemVariationStore: format 1, region list at 12, one ItemVariationData at 22
        U16(1);
        U32(12);
        U16(1);
        U32(22);
        U16(1);                                                       // axisCount
        U16(1);                                                       // regionCount
        U16(0);
        U16(0x4000);
        U16(0x4000);                                                  // start 0, peak 1, end 1
        U16(sorted.Length);                                           // itemCount
        U16(0);                                                       // wordDeltaCount: all deltas int8
        U16(1);                                                       // regionIndexCount
        U16(0);
        mvar.AddRange(Enumerable.Repeat((byte)100, sorted.Length));
        return [.. mvar];
    }

    /// <summary>The metrics PDF-Knead takes from a font's own tables.</summary>
    private static (int, int, int, int, int, int, int, int, int, int, double?, int) MetricsOt42(OpenTypeFont f) =>
        (f.Ascent, f.Descent, f.LineAscent, f.LineDescent, f.LineGap, f.StrikeoutSize, f.StrikeoutPosition, f.UnderlinePosition,
            f.UnderlineThickness, f.CapHeight, f.ItalicAngle, f.WeightClass);

    // ------------------------------------------------------------ 52 meta

    /// <summary>
    /// OT 52.1 sentences 18–21, 52.2 sentences 8–22, 52.3 sentences 1–14 and 21–24, 52.3.1 sentences 6–24 (reader): PDF-Knead does not evaluate
    /// meta — fonts are chosen by their cmap. Liberation Sans with a meta naming the design languages „Cyrl“ (dlng), the supported ones
    /// „Cyrl, Deva“ (slng, a comma list with a space) and a second slng „Cyrl“ (only one counts), and a dlng with a tag „en-Latn-IN“ and one
    /// that is not well-formed („x_bad“): „Ж“ maps to the same glyph as without meta, the face of typed text covers „Ж“ and — though slng claims
    /// Devanagari — not „क“, exactly as without meta (the glyph test is the one the fallback of formatted text asks), and the shaper gives the
    /// same glyphs and advances for „Жук“.
    /// </summary>
    [Fact]
    public void Meta_DesignAndSupportedLanguages_NotUsed_CoverageAndShapingAsWithout()
    {
        var plainData = LiberationOt11;
        var data = ColorFontBuilder.WithTables(plainData, new Dictionary<string, byte[]?>
        {
            ["meta"] = MetaOt42(("dlng", "Cyrl"), ("slng", "Cyrl, Deva"), ("slng", "Cyrl"), ("dlng", "en-Latn-IN,x_bad")),
        });
        var plain = OpenTypeFont.Parse(plainData);
        var font = OpenTypeFont.Parse(data);
        Assert.False(font.Broken);
        Assert.True(font.HasTable("meta"));
        Assert.True(plain.GlyphForUnicode('Ж') > 0);
        Assert.Equal(plain.GlyphForUnicode('Ж'), font.GlyphForUnicode('Ж'));
        Assert.Equal(0, font.GlyphForUnicode('क'));

        ElementFace Face(byte[] bytes) => ElementFace.For("M7-ot-42-meta-" + Guid.NewGuid().ToString("N"), () => OpenTypeFont.Parse(bytes), false, false, false);
        var (withMeta, without) = (Face(data), Face(plainData));
        Assert.True(withMeta.Covers("Ж") && without.Covers("Ж"));
        Assert.False(withMeta.Covers("क"));
        Assert.False(without.Covers("क"));
        Assert.Equal(without.Glyph('Ж'), withMeta.Glyph('Ж'));
        Assert.Equal(0, withMeta.Glyph('क'));

        var shapedWith = TextShaper.For(font).Shape("Жук");
        var shapedWithout = TextShaper.For(plain).Shape("Жук");
        Assert.Equal(shapedWithout.Select(g => (g.Glyph, g.XAdvance, g.XOffset, g.YOffset)), shapedWith.Select(g => (g.Glyph, g.XAdvance, g.XOffset, g.YOffset)));
    }

    // ------------------------------------------------------------ 53 MVAR

    /// <summary>
    /// OT 53.1 sentences 1–6, 13–16 and 17–25, 53.2 sentences 1–7, 53.3 sentences 1–8, 9–12, 13–29, 30–43 and 44–53 (reader and writer):
    /// PDF-Knead neither reads nor writes MVAR. Liberation Sans with fvar and an MVAR of 12-byte value records (larger than the three fields)
    /// for the line metrics and caret (hasc, hdsc, hlgp, hcla, hcld, hcrs, hcrn, hcof, vasc), x-height and cap height (xhgt, cpht), sub- and
    /// superscript (sbxs … spyo), strikeout (strs, stro), underline (unds, undo), gasp (gsp0) and a private tag „PRVT“, each with a delta of +100
    /// in its ItemVariationStore: every metric of the font (ascent, descent, line gap, strikeout, underline, cap height, italic angle, weight)
    /// and the outline and advance of „A“ are those without MVAR, and so are /Ascent, /Descent and /CapHeight of the descriptor typed text
    /// saves. Subset, compression, scrubbing and the PDF/A rebuild write no MVAR.
    /// </summary>
    [Fact]
    public void Mvar_ValueRecords_NotApplied_MetricsOfTheDefaultInstance_NotWritten()
    {
        string[] tags =
        [
            "hasc", "hdsc", "hlgp", "hcla", "hcld", "hcrs", "hcrn", "hcof", "vasc", "xhgt", "cpht", "sbxs", "sbys", "sbxo", "sbyo", "spxs", "spys",
            "spxo", "spyo", "strs", "stro", "unds", "undo", "gsp0", "PRVT",
        ];
        var plainData = Ot27SansFile.Value;
        var data = Ot27SansWith(("fvar", Ot27Fvar()), ("MVAR", MvarOt42(tags)));
        var plain = Ot27Sans.Value;
        var font = OpenTypeFont.Parse(data);
        Assert.False(font.Broken);
        Assert.True(font.HasTable("MVAR"));
        Assert.Equal(MetricsOt42(plain), MetricsOt42(font));
        var a = Ot27Glyph('A');
        Assert.True(Ot27SameOutline(plain.Outline(a), font.Outline(a)));
        Assert.Equal(plain.AdvanceWidth(a), font.AdvanceWidth(a));

        var (savedPlain, saved) = (Ot30SavedDescriptor(plainData), Ot30SavedDescriptor(data));
        foreach (var key in (string[])["Ascent", "Descent", "CapHeight"])
            Assert.Equal(savedPlain.GetNumber(PdfName.Get(key)), saved.GetNumber(PdfName.Get(key)));

        var written = new Dictionary<string, byte[]>
        {
            ["Teilschrift"] = TrueTypeSubsetter.Subset(data, [a]),
            ["Verkleinern"] = TrueTypeSubsetter.Subset(data, [a], keepAllTables: true),
            ["Bereinigen"] = TrueTypeSubsetter.Scrub(data, [a]),
            ["Neubau"] = TrueTypeSubsetter.Rebuild(data, new Dictionary<int, int> { [a] = 1366 }, [], null),
        };
        foreach (var (writer, file) in written) Assert.False(OpenTypeFont.Parse(file).HasTable("MVAR"), writer);
    }
}
