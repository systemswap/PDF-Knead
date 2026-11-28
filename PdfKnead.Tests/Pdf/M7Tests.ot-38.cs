using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-38 (OpenType: the table BASE, baselines and extents per script), the test that needs the CJK substitute fonts: it switches
/// the shared font folder and therefore runs in the collection of the CJK fonts, one after another. BASE is not read and is left out when writing
/// (M7NormTests.Fg1172_Compression_LeavesOutTablesThatDependOnGlyphs, M7NormTests.Ot01_BaseJstfMath_NotUsedAndLeftOut).
/// </summary>
[Collection(CjkFontsCollection.Name)]
public sealed class M7NormTestsOt38Cjk
{
    private static string Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>The font <paramref name="index"/> of a collection as a single font file, without the tables <paramref name="drop"/>.</summary>
    private static byte[] Ot38FromCollection(byte[] collection, int index, params string[] drop)
    {
        var member = (int)BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan(12 + 4 * index));
        var count = BinaryPrimitives.ReadUInt16BigEndian(collection.AsSpan(member + 4));
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var record = collection.AsSpan(member + 12 + 16 * i);
            var tag = Encoding.ASCII.GetString(record[..4]);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            if (!drop.Contains(tag)) tables[tag] = collection.AsSpan(offset, length).ToArray();
        }
        // the remaining tables in a table directory of their own; the helper that replaces tables then sets checksums and search fields
        var single = new byte[12 + 16 * tables.Count + tables.Values.Sum(t => (t.Length + 3) & ~3)];
        collection.AsSpan(member, 4).CopyTo(single);
        BinaryPrimitives.WriteUInt16BigEndian(single.AsSpan(4), (ushort)tables.Count);
        var at = 12 + 16 * tables.Count;
        var r = 12;
        foreach (var (tag, data) in tables)
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(single, r);
            BinaryPrimitives.WriteUInt32BigEndian(single.AsSpan(r + 8), (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(single.AsSpan(r + 12), (uint)data.Length);
            data.CopyTo(single, at);
            at += (data.Length + 3) & ~3;
            r += 16;
        }
        return ColorFontBuilder.WithTables(single, new Dictionary<string, byte[]?>());
    }

    /// <summary>
    /// The baseline coordinates of the horizontal axis of a BASE table for one script (BaseTagList tag → BaseCoord coordinate); empty if the
    /// script has no BaseValues.
    /// </summary>
    private static Dictionary<string, int> Ot38Baselines(ReadOnlySpan<byte> table, string script)
    {
        int U16(ReadOnlySpan<byte> t, int at) => BinaryPrimitives.ReadUInt16BigEndian(t[at..]);
        var result = new Dictionary<string, int>();
        var axis = U16(table, 4);
        if (axis == 0) return result;
        var tagList = axis + U16(table, axis);
        var scriptList = axis + U16(table, axis + 2);
        var tags = new List<string>();
        for (var i = 0; i < U16(table, tagList); i++) tags.Add(Encoding.ASCII.GetString(table.Slice(tagList + 2 + 4 * i, 4)));
        for (var i = 0; i < U16(table, scriptList); i++)
        {
            if (Encoding.ASCII.GetString(table.Slice(scriptList + 2 + 6 * i, 4)) != script) continue;
            var baseScript = scriptList + U16(table, scriptList + 2 + 6 * i + 4);
            if (U16(table, baseScript) is var valuesOffset && valuesOffset == 0) return result;
            var values = baseScript + valuesOffset;
            for (var k = 0; k < U16(table, values + 2) && k < tags.Count; k++)
            {
                var coord = values + U16(table, values + 4 + 2 * k);
                result[tags[k]] = BinaryPrimitives.ReadInt16BigEndian(table[(coord + 2)..]);
            }
        }
        return result;
    }

    /// <summary>
    /// OT 43.1.1 sentences 1 and 5–6 (the model of a dominant run whose baseline the others refer to; the values are recommendations, an application
    /// may place the baselines itself): PDF-Knead assumes no dominant run and puts every font on its own baseline 0 — formatted text „Max 日本“ in
    /// Liberation Sans, the CJK characters from the substitute font Noto Sans CJK, which carries a BASE whose ideographic baseline lies below the
    /// Roman one: all glyphs of the one line lie on the same baseline (no vertical offset, the CJK run is not moved by the difference of the
    /// baselines), and glyphs, advances and line heights are those of a copy of the substitute font without BASE.
    /// </summary>
    [Fact]
    public void Ot38_FallbackFontWithBase_SameBaselineAsWithout()
    {
        var folder = Path.Combine(Repo(), "Schriften", "CJK");
        CjkFonts.FolderForTests = folder;
        RichFonts.SystemFontsOff = true;
        try
        {
            var noto = CjkFonts.Get("Japan1", false)!;
            Assert.True(noto.HasTable("BASE"));
            var baselines = Ot38Baselines(noto.TableData("BASE"), "hani");
            Assert.True(baselines["ideo"] < baselines["romn"], "ideo unter romn");

            const double size = 20;
            var layout = RichLayout.Layout(new RichText("Max 日本", new RichStyle { Family = "Liberation Sans", Size = size }), 1000,
                BlockAlignment.Left, 1.2, null, 0);
            var line = Assert.Single(layout.Lines);
            Assert.All(line.Glyphs, g => Assert.Equal(0, g.YOffset));
            var cjk = line.Glyphs.Where(g => g.Text is "日" or "本").ToList();
            Assert.Equal(2, cjk.Count);
            Assert.All(cjk, g => Assert.Same(noto, g.Face!.Font));

            // the same font without BASE: shaping and line metrics as with it
            var plain = OpenTypeFont.Parse(Ot38FromCollection(File.ReadAllBytes(Path.Combine(folder, "NotoSansCJK-Regular.ttc")), 0, "BASE"));
            Assert.False(plain.HasTable("BASE"));
            Assert.Equal((noto.LineAscent, noto.LineDescent, noto.UnitsPerEm), (plain.LineAscent, plain.LineDescent, plain.UnitsPerEm));
            var shaped = TextShaper.For(plain).Shape("日本");
            Assert.Equal(TextShaper.For(noto).Shape("日本").Select(g => (g.Glyph, g.XAdvance, g.XOffset, g.YOffset)),
                shaped.Select(g => (g.Glyph, g.XAdvance, g.XOffset, g.YOffset)));
            var scale = size / plain.UnitsPerEm;
            Assert.Equal(shaped.Select(g => (g.Glyph, g.XAdvance * scale)), cjk.Select(g => (g.Glyph, g.Advance)));
            var latin = line.Glyphs.First(g => g.Text == "M").Face!;
            Assert.Equal(Math.Max(latin.Ascent, (double)plain.LineAscent / plain.UnitsPerEm) * size, line.Ascent, 9);
            Assert.Equal(Math.Max(-latin.Descent, -(double)plain.LineDescent / plain.UnitsPerEm) * size, line.Descent, 9);
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
            CjkFonts.FolderForTests = null;
        }
    }
}
