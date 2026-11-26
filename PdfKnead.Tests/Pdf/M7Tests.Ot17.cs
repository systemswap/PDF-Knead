using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-17 (OpenType, COLR 10.4 and 10.5, CPAL 11, cvt 12, EBDT 13): the drawing of COLR version 1 glyphs and their variations,
/// palettes, the control value table and embedded bitmap data. PDF-Knead reads neither COLR/CPAL (question 3) nor EBDT/EBLC (question 4)
/// nor cvt: the tests check today's behaviour — fonts parse, no colour glyphs, the glyphs from their outlines; subsets keep cvt, fpgm and prep
/// byte for byte and leave the bitmap tables out.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-17)

    private static readonly Lazy<byte[]> Ot17HandFile = new(() => EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf"));

    /// <summary>COLR version 0: „A“ of Liberation Sans as one layer of itself in palette entry <paramref name="entry"/>.</summary>
    private static byte[] Ot17ColrV0(int entry = 0) => Ot14Colr(version: 0, baseGlyphs: [(Ot14Gid('A'), 0, 1)], layerRecords: [(Ot14Gid('A'), entry)]);

    /// <summary>The name table of Liberation Sans with further Windows (3, 1, 0x409) names appended.</summary>
    private static byte[] Ot17NameWith(params (int Id, string Text)[] names)
    {
        var name = Ot14Sans.Value.TableData("name").ToArray();
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(name));
        var count = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(2));
        var storage = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(4));
        var strings = name.AsSpan(storage).ToArray().ToList();
        var b = new List<byte>();
        Ot14Put(b, 2, 0);
        Ot14Put(b, 2, count + names.Length);
        Ot14Put(b, 2, storage + 12 * names.Length);
        b.AddRange(name.AsSpan(6, 12 * count).ToArray());
        foreach (var (id, text) in names)
        {
            var bytes = Encoding.BigEndianUnicode.GetBytes(text);
            foreach (var v in (int[])[3, 1, 0x409, id, bytes.Length, strings.Count]) Ot14Put(b, 2, v);
            strings.AddRange(bytes);
        }
        b.AddRange(strings);
        return [.. b];
    }

    /// <summary>The CBDT table of a test colour font with every advance in the glyph metrics set to <paramref name="advance"/>.</summary>
    private static byte[] Ot17CbdtWithAdvance(byte[] font, bool big, int advance)
    {
        var cbdt = OpenTypeFont.Parse(font).TableData("CBDT").ToArray();
        var metrics = big ? 8 : 5;
        for (var at = 4; at + metrics + 4 <= cbdt.Length;)
        {
            cbdt[at + 4] = (byte)advance;
            if (big) cbdt[at + 7] = (byte)advance;
            at += metrics + 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(cbdt.AsSpan(at + metrics));
        }
        return cbdt;
    }

    /// <summary>Glyph metrics of an 8 × 8 bitmap: small (5 bytes) or big (8 bytes).</summary>
    private static byte[] Ot17Metrics(bool big) => big ? [8, 8, 0, 8, 9, 0, 0, 9] : [8, 8, 0, 8, 9];

    /// <summary>The bitmap data of one glyph in an EBDT image format (8 × 8 pixels; components are glyph IDs with offset 0, 0).</summary>
    private static byte[] Ot17GlyphData(int format, int bitDepth = 1, params int[] components)
    {
        var image = Enumerable.Repeat((byte)0xAA, 8 * bitDepth).ToArray();
        var parts = new List<byte>();
        void Components()
        {
            Ot14Put(parts, 2, components.Length);
            foreach (var c in components) parts.AddRange([(byte)(c >> 8), (byte)c, 0, 0]);
        }
        switch (format)
        {
            case 1 or 2: parts.AddRange(Ot17Metrics(false)); parts.AddRange(image); break;
            case 5: parts.AddRange(image); break;
            case 6 or 7: parts.AddRange(Ot17Metrics(true)); parts.AddRange(image); break;
            case 8: parts.AddRange(Ot17Metrics(false)); parts.Add(0); Components(); break;
            case 9: parts.AddRange(Ot17Metrics(true)); Components(); break;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
        return [.. parts];
    }

    /// <summary>
    /// EBLC and EBDT (version 2.0) with one strike of <paramref name="ppem"/> and <paramref name="bitDepth"/>: one index subtable per glyph
    /// (index format 1, or 2 with big metrics for image format 5) and the glyph data in EBDT.
    /// </summary>
    private static (byte[] Eblc, byte[] Ebdt) Ot17Bitmaps(int ppem, int bitDepth, params (int Glyph, int Format, byte[] Data)[] glyphs)
    {
        var sorted = glyphs.OrderBy(g => g.Glyph).ToArray();
        var ebdt = new List<byte>();
        Ot14Put(ebdt, 4, 0x00020000);
        var subTables = new List<byte>();
        var array = new List<byte>();
        foreach (var (glyph, format, data) in sorted)
        {
            Ot14Put(array, 2, glyph);
            Ot14Put(array, 2, glyph);
            Ot14Put(array, 4, 8 * sorted.Length + subTables.Count);
            Ot14Put(subTables, 2, format == 5 ? 2 : 1);
            Ot14Put(subTables, 2, format);
            Ot14Put(subTables, 4, ebdt.Count);
            if (format == 5)
            {
                Ot14Put(subTables, 4, data.Length);
                subTables.AddRange(Ot17Metrics(true));
            }
            else
            {
                Ot14Put(subTables, 4, 0);
                Ot14Put(subTables, 4, data.Length);
            }
            ebdt.AddRange(data);
        }
        array.AddRange(subTables);
        var eblc = new List<byte>();
        Ot14Put(eblc, 4, 0x00020000);
        Ot14Put(eblc, 4, 1);
        Ot14Put(eblc, 4, 8 + 48);
        Ot14Put(eblc, 4, array.Count);
        Ot14Put(eblc, 4, sorted.Length);
        Ot14Put(eblc, 4, 0);
        for (var m = 0; m < 2; m++) eblc.AddRange([(byte)(ppem - 2), unchecked((byte)-2), 8, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Ot14Put(eblc, 2, sorted[0].Glyph);
        Ot14Put(eblc, 2, sorted[^1].Glyph);
        eblc.AddRange([(byte)ppem, (byte)ppem, (byte)bitDepth, 1]);
        eblc.AddRange(array);
        return ([.. eblc], [.. ebdt]);
    }

    /// <summary>Today's behaviour with bitmap tables: the glyphs from glyf, no colour glyphs; subsets and scrubbed fonts without EBDT, EBLC and EBSC.</summary>
    private static void Ot17BitmapsIgnored((byte[] Eblc, byte[] Ebdt) bitmaps, bool withEbsc = false)
    {
        var tables = new Dictionary<string, byte[]?> { ["EBLC"] = bitmaps.Eblc, ["EBDT"] = bitmaps.Ebdt };
        if (withEbsc) tables["EBSC"] = [0, 2, 0, 0, 0, 0, 0, 0];
        Ot14TablesIgnored(Ot14SansFile.Value, tables);
        var file = ColorFontBuilder.WithTables(Ot14SansFile.Value, tables);
        foreach (var result in (byte[][])[TrueTypeSubsetter.Subset(file, [Ot14Gid('A')]), TrueTypeSubsetter.Scrub(file, [Ot14Gid('A')])])
        {
            var font = OpenTypeFont.Parse(result);
            foreach (var tag in (string[])["EBDT", "EBLC", "EBSC"]) Assert.False(font.HasTable(tag), tag);
        }
    }

    // ------------------------------------------------------------ 10.4 drawing COLR version 1

    /// <summary>
    /// OT 10.4 sentences 4–7, 8–10, 11–13 (today's behaviour, question 3): the handwriting font Patrick Hand with COLR version 1 („A“ as PaintGlyph →
    /// PaintSolid palette entry 0) and CPAL (one palette, red); the same with a ClipBox (0, 0, 500, 700) for „A“; with a PaintColrLayers over
    /// „O“ in blue and „A“ in red and a cycle through PaintColrGlyph — no colour glyphs (the signature takes its image from the outline), the
    /// outline of „A“ the one without COLR, no endless loop.
    /// </summary>
    [Fact]
    public void Ot17_HandwritingWithColrV1_NotRead_GlyphsKeepTheirOutlines()
    {
        var hand = OpenTypeFont.Parse(Ot17HandFile.Value);
        var a = hand.GlyphForUnicode('A');
        var o = hand.GlyphForUnicode('O');
        var b = hand.GlyphForUnicode('B');
        var red = Ot14Cpal(1, [0], [Ot14RedColour]);
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Solid(0)))]), red, Ot17HandFile.Value);
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Solid(0)))], clips: [(a, a, Ot14ClipBox(0, 0, 500, 700))]), red, Ot17HandFile.Value);
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Layers(3, 0)), (b, Ot14ColrGlyph(a))], layers: [Ot14Glyph(o, Ot14Solid(2)), Ot14Glyph(a, Ot14Solid(0)), Ot14ColrGlyph(b)]),
            source: Ot17HandFile.Value);
    }

    // ------------------------------------------------------------ 10.5 COLR and variations

    /// <summary>
    /// OT 10.5 sentences 1–7, 8–18, 19–26, 31–34 (today's behaviour, question 3; PDF-Knead reads no variations): variable fonts (fvar) with
    /// COLR version 1 — PaintVarSolid with an alpha delta; PaintVarTransform (VarAffine2x3, varIndexBase 0) with a DeltaSetIndexMap of six
    /// entries; a map of two entries with varIndexBase 5; varIndexBase 0xFFFFFFFF; an ItemVariationStore without map (index 0x00010002);
    /// a DeltaSetIndexMap without ItemVariationStore; PaintVarSolid alpha 0x2000 with a delta of +0x1000 — no colour glyphs, the default instance.
    /// </summary>
    [Fact]
    public void Ot17_ColrV1Variations_NotRead_DefaultInstance()
    {
        var a = Ot14Gid('A');
        Ot14Node Fill() => Ot14Glyph(a, Ot14Solid(0));
        var fvar = ("fvar", Ot14Fvar());
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14VarSolid(0, 1, 0)))], varIndexMap: Ot14DeltaSetIndexMap((0, 0)), varStore: Ot14VariationStore(-0x2000)), more: fvar);
        var affine = new Ot14Node(Ot14Fixed(1), Ot14Fixed(0), Ot14Fixed(0), Ot14Fixed(1), Ot14Fixed(0), Ot14Fixed(0), Ot14U32(0));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Paint(13, Fill(), affine))], varIndexMap: Ot14DeltaSetIndexMap((0, 0), (0, 1), (0, 2), (0, 3), (0, 4), (0, 5)),
            varStore: Ot14VariationStore(1, 2, 3, 4, 5, 6)), more: fvar);
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Paint(15, Fill(), Ot14I16(0), Ot14I16(0), Ot14U32(5)))], varIndexMap: Ot14DeltaSetIndexMap((0, 0), (0, 1)),
            varStore: Ot14VariationStore(10, 20)), more: fvar);
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Paint(15, Fill(), Ot14I16(0), Ot14I16(0), Ot14U32(0xFFFFFFFF)))], varIndexMap: Ot14DeltaSetIndexMap((0, 0)),
            varStore: Ot14VariationStore(10)), more: fvar);
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Paint(15, Fill(), Ot14I16(0), Ot14I16(0), Ot14U32(0x00010002)))], varStore: Ot14VariationStore(10, 20, 30)), more: fvar);
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Paint(15, Fill(), Ot14I16(0), Ot14I16(0), Ot14U32(0)))], varIndexMap: Ot14DeltaSetIndexMap((0, 0))), more: fvar);
        var half = Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14VarSolid(0, 0.5, 0)))], varIndexMap: Ot14DeltaSetIndexMap((0, 0)), varStore: Ot14VariationStore(0x1000));
        Assert.True(Ot14Contains(half, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00));
        Ot14ColrIgnored(half, more: fvar);
    }

    // ------------------------------------------------------------ 11 CPAL

    /// <summary>
    /// OT 11 sentences 1–2, 3–6, 7–13, 14–19; 11.1 sentences 1–2; 11.1.1 sentences 1–9; 11.1.2 sentences 1–15; 11.2 sentences 1–3, 4–14;
    /// 11.3 sentences 1–10; 11.4 sentences 1–5; 11.5 sentences 1–6 (today's behaviour, question 3): COLR version 0 for „A“ with CPAL tables —
    /// two palettes of three entries; one entry of the bytes 00 00 FF FF; two palettes over four overlapping colour records; a palette whose
    /// entries run past the colour records (too short); version 2; version 0 with its colour records at byte 14; version 1 with all three
    /// offsets 0; colorRecordIndices [0, 2] and a layer naming entry 1; a colour record 10 20 30 FF; version 1 with paletteTypes [2, 1]; with
    /// paletteLabels [256] and a name 256; with paletteEntryLabels [257, 0xFFFF] and a name 257 — each parses without an exception, no colour
    /// glyphs, the glyphs from their outlines, the family name unchanged.
    /// </summary>
    [Fact]
    public void Ot17_CpalVariants_NotRead_GlyphsFromOutlines()
    {
        var colours = new[] { Ot14RedColour, Ot14GreenColour, Ot14BlueColour, Ot14GreyColour, Ot14RedColour, Ot14GreenColour };
        Ot14ColrIgnored(Ot17ColrV0(2), Ot14Cpal(3, [0, 3], colours));
        var single = Ot14Cpal(1, [0], [Ot14RedColour]);
        Assert.Equal(new byte[] { 0, 0, 0xFF, 0xFF }, single[^4..]);
        Ot14ColrIgnored(Ot17ColrV0(), single);
        Ot14ColrIgnored(Ot17ColrV0(2), Ot14Cpal(3, [0, 1], colours[..4]));
        Ot14ColrIgnored(Ot17ColrV0(2), Ot14Cpal(3, [1], colours[..2]));
        Ot14ColrIgnored(Ot17ColrV0(), Ot14Cpal(1, [0], [Ot14RedColour], version: 2));
        var v0 = Ot14Cpal(2, [0], [Ot14RedColour, Ot14BlueColour]);
        Assert.Equal(new byte[] { 0, 0, 0, 2, 0, 1, 0, 2, 0, 0, 0, 14, 0, 0 }, v0[..14]);
        Ot14ColrIgnored(Ot17ColrV0(1), v0);
        Ot14ColrIgnored(Ot17ColrV0(1), Ot14Cpal(2, [0], [Ot14RedColour, Ot14BlueColour], version: 1));
        Ot14ColrIgnored(Ot17ColrV0(1), Ot14Cpal(2, [0, 2], colours[..4]));
        Ot14ColrIgnored(Ot17ColrV0(), Ot14Cpal(1, [0], [new Ot14Bgra(0x10, 0x20, 0x30, 0xFF)]));
        Ot14ColrIgnored(Ot17ColrV0(), Ot14Cpal(1, [0, 1], colours[..2], version: 1, types: [2, 1]));
        var family = Ot14Sans.Value.FamilyName;
        var labelled = Ot14ColrIgnored(Ot17ColrV0(), Ot14Cpal(1, [0], [Ot14RedColour], version: 1, labels: [256]), more: ("name", Ot17NameWith((256, "Probe Palette"))));
        Assert.Equal(family, labelled.FamilyName);
        labelled = Ot14ColrIgnored(Ot17ColrV0(), Ot14Cpal(2, [0], [Ot14RedColour, Ot14BlueColour], version: 1, entryLabels: [257, 0xFFFF]),
            more: ("name", Ot17NameWith((257, "Probe Eintrag"))));
        Assert.Equal(family, labelled.FamilyName);
    }

    /// <summary>
    /// OT 11.6.3 sentences 1–6, 7–14, 15–19, 20–25, 26–30 (today's behaviour, question 3): COLR version 1 with PaintLinearGradient from 0.5 black
    /// to 0.9 white; a PaintSolid with the CPAL entry red 0x80; a gradient from sRGB 0x000000 to 0xFFFFFF; one from opaque red to transparent blue;
    /// one over 1 000 units from 0x000000 to 0x101010 — no colour glyphs, no gradient, the glyphs from their outlines.
    /// </summary>
    [Fact]
    public void Ot17_ColrV1GradientInterpolation_NotRead_GlyphsFromOutlines()
    {
        var cpal = Ot14Cpal(6, [0], [new Ot14Bgra(0, 0, 0, 255), new Ot14Bgra(255, 255, 255, 255), new Ot14Bgra(0, 0, 0x80, 255), Ot14RedColour,
            new Ot14Bgra(255, 0, 0, 0), new Ot14Bgra(0x10, 0x10, 0x10, 255)]);
        Ot14Node Linear(int width, params (double Stop, int Palette, double Alpha)[] stops) => Ot14Linear(Ot14Line(0, stops), 0, 0, width, 0, 0, width);
        Ot14ColrIgnored(Ot14Colr(paints:
        [
            (Ot14Gid('A'), Ot14Glyph(Ot14Gid('A'), Linear(1000, (0.5, 0, 1), (0.9, 1, 1)))),
            (Ot14Gid('B'), Ot14Glyph(Ot14Gid('B'), Ot14Solid(2))),
            (Ot14Gid('C'), Ot14Glyph(Ot14Gid('C'), Linear(1000, (0, 0, 1), (1, 1, 1)))),
            (Ot14Gid('D'), Ot14Glyph(Ot14Gid('D'), Linear(1000, (0, 3, 1), (1, 4, 1)))),
            (Ot14Gid('E'), Ot14Glyph(Ot14Gid('E'), Linear(1000, (0, 0, 1), (1, 5, 1)))),
        ]), cpal);
    }

    // ------------------------------------------------------------ 12 cvt

    /// <summary>
    /// OT 12 sentences 1–2, 3–5 (writer and reader): the control value table is copied, not interpreted — a subset of Liberation Sans with „A“
    /// keeps „cvt “ (648 bytes), fpgm and prep byte for byte; with every cvt value set to 0 the outline of „A“ is the same; a cvt of 5 bytes
    /// (not a whole number of FWORDs) is taken over unchanged without an exception.
    /// </summary>
    [Fact]
    public void Ot17_Cvt_CopiedUnchangedIntoSubsets_NotInterpreted()
    {
        var a = Ot14Gid('A');
        var sans = Ot14Sans.Value;
        Assert.Equal(648, sans.TableData("cvt ").Length);
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(Ot14SansFile.Value, [a]));
        foreach (var tag in (string[])["cvt ", "fpgm", "prep"]) Assert.Equal(sans.TableData(tag).ToArray(), subset.TableData(tag).ToArray());

        var zeroed = OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot14SansFile.Value, new Dictionary<string, byte[]?> { ["cvt "] = new byte[648] }));
        Assert.True(sans.Outline(a).Verbs.SequenceEqual(zeroed.Outline(a).Verbs) && sans.Outline(a).Points.SequenceEqual(zeroed.Outline(a).Points));

        byte[] odd = [1, 2, 3, 4, 5];
        var oddFile = ColorFontBuilder.WithTables(Ot14SansFile.Value, new Dictionary<string, byte[]?> { ["cvt "] = odd });
        Assert.Equal(odd, OpenTypeFont.Parse(TrueTypeSubsetter.Subset(oddFile, [a])).TableData("cvt ").ToArray());
    }

    // ------------------------------------------------------------ 13 EBDT

    /// <summary>
    /// OT 13 sentences 1–8; 13.1 sentences 1–10; 13.2.1 sentences 1–7 (today's behaviour, question 4): Liberation Sans with EBLC, EBDT (version 2.0)
    /// and EBSC — one strike of 12 ppem, 1 bit, image format 1 for „A“ — parses without an exception, „A“ keeps its outline from glyf, no colour
    /// glyphs; a subset and a scrubbed font have none of EBDT, EBLC and EBSC.
    /// </summary>
    [Fact]
    public void Ot17_EbdtStrike_NotRead_GlyphFromGlyfAndNotInSubsets()
    {
        var bitmaps = Ot17Bitmaps(12, 1, (Ot14Gid('A'), 1, Ot17GlyphData(1)));
        Assert.Equal(new byte[] { 0, 2, 0, 0 }, bitmaps.Ebdt[..4]);
        Ot17BitmapsIgnored(bitmaps, withEbsc: true);
    }

    /// <summary>
    /// OT 13.2.1 sentences 1–7; 13.2.2 sentences 1–8; 13.2.5 sentences 1–8; 13.2.6 sentences 1–5; 13.2.7 sentences 1–5; 13.2.8 sentences 1–8;
    /// 13.2.9 sentences 1–7 (today's behaviour, question 4): EBDT glyph bitmap data in the image formats 1, 2, 5 (with EBLC index format 2),
    /// 6, 7, and 8 and 9 made of two components — „A“ is drawn from glyf, no bitmap, subsets without the bitmap tables.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Ot17_EbdtImageFormat_NotRead_GlyphFromGlyf(int format)
    {
        var a = Ot14Gid('A');
        var o = Ot14Gid('O');
        var i = Ot14Gid('I');
        Ot17BitmapsIgnored(format is 8 or 9
            ? Ot17Bitmaps(12, 1, (a, format, Ot17GlyphData(format, 1, o, i)), (o, 1, Ot17GlyphData(1)), (i, 1, Ot17GlyphData(1)))
            : Ot17Bitmaps(12, 1, (a, format, Ot17GlyphData(format))));
    }

    /// <summary>
    /// OT 13.2 sentences 1–5 (today's behaviour, question 4): EBLC with bitDepth 8 and EBDT image format 1 — „A“ from glyf; a subset has no EBDT.
    /// </summary>
    [Fact]
    public void Ot17_EbdtBitDepth8_NotRead_SubsetWithoutBitmaps() =>
        Ot17BitmapsIgnored(Ot17Bitmaps(12, 8, (Ot14Gid('A'), 1, Ot17GlyphData(1, 8))));

    /// <summary>
    /// OT 13.1 sentences 33–43 (today's behaviour, question 4): EBDT image format 8 whose component is the glyph itself — parsing and the outline
    /// of „A“ without an exception or endless loop (the bitmaps are not read).
    /// </summary>
    [Fact]
    public void Ot17_EbdtComponentReferringToItself_NoLoop()
    {
        var a = Ot14Gid('A');
        Ot17BitmapsIgnored(Ot17Bitmaps(12, 1, (a, 8, Ot17GlyphData(8, 1, a))));
    }

    /// <summary>
    /// OT 13.1 sentences 22–25, 32 (today's behaviour): the advances in the glyph metrics of CBDT images are not used — a CBDT font with big
    /// metrics (image format 18, horiAdvance and vertAdvance 0) and one with small metrics (image format 17, advance 0) compose „Max“ as wide as
    /// with the right values: the advance comes from hmtx.
    /// </summary>
    [Theory]
    [InlineData(ColorFontBuilder.Kind.CbdtSparse)]
    [InlineData(ColorFontBuilder.Kind.Cbdt)]
    public void Ot17_CbdtMetricsAdvance_NotUsed_AdvanceFromHmtx(ColorFontBuilder.Kind kind)
    {
        var original = ColorFontBuilder.Build(kind, 32);
        var zero = ColorFontBuilder.WithTables(original, new Dictionary<string, byte[]?> { ["CBDT"] = Ot17CbdtWithAdvance(original, kind == ColorFontBuilder.Kind.CbdtSparse, 0) });
        Assert.NotEqual(OpenTypeFont.Parse(original).TableData("CBDT").ToArray(), OpenTypeFont.Parse(zero).TableData("CBDT").ToArray());
        int Width(byte[] file)
        {
            var font = OpenTypeFont.Parse(file);
            var image = ColorGlyphs.For(font)!.Compose(TextShaper.For(font).Shape("Max"), font.UnitsPerEm);
            Assert.NotNull(image);
            return image.Width;
        }
        Assert.Equal(Width(original), Width(zero));
    }
}
