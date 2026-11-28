using System.Runtime.CompilerServices;
using PdfKnead.Core.Fonts.Layout;

namespace PdfKnead.Core.Fonts;

/// <summary>One layer of a colour glyph of COLR version 0: a glyph of the font filled with one colour.</summary>
/// <param name="Glyph">The glyph whose outline is the layer's shape.</param>
/// <param name="Rgb">The colour as 0xRRGGBB from the default palette of CPAL; null for the text colour (palette entry 0xFFFF).</param>
/// <param name="Alpha">The alpha of the palette entry (0 transparent … 255 opaque); 255 for the text colour.</param>
public readonly record struct ColrLayer(int Glyph, int? Rgb, byte Alpha);

/// <summary>
/// Colour glyphs of the OpenType table 'COLR' in version 0 (OpenType 10.2.1, 10.2.5, 10.3.3): a base glyph drawn as a stack of other glyphs of
/// the font, bottom layer first, each filled with a colour of the default palette of 'CPAL' (OpenType 11, 11.2) or with the text colour. Used for
/// text and signatures the program sets itself with installed colour fonts (docs/FEHLERGEDAECHTNIS.md, No. 1168). The version 1 formats (the
/// paint graph with gradients, transformations and composition) are not read on purpose — a large new attack surface; a version 1 table is read
/// for the version 0 records it carries for applications without version 1 (10.3.2.1). Fonts embedded in PDF files draw from their outlines in the
/// colour of the text state, as the PDF imaging model prescribes; this class is not used for them.
/// </summary>
/// <remarks>
/// Every count from the file is checked against the length of its table, and a colour glyph has at most <see cref="MaxLayers"/> layers. A glyph
/// whose record names a layer glyph or a palette entry the font does not have is drawn with its own outline. Instances are shared per font and
/// immutable.
/// </remarks>
public sealed class ColrLayers
{
    private static readonly ConditionalWeakTable<OpenTypeFont, ColrLayers?> All = new();

    /// <summary>
    /// Layers of one colour glyph at most: the work per glyph stays bounded as for the other colour glyphs (the colour emoji of the system use up
    /// to 120); a glyph with more is drawn with its own outline.
    /// </summary>
    public const int MaxLayers = 512;

    private readonly TableData _data;
    private readonly int _bases, _baseCount, _layers, _layerCount, _glyphCount;
    private readonly int _colours, _entries;

    private ColrLayers(TableData data, int bases, int baseCount, int layers, int layerCount, int glyphCount, int colours, int entries)
    {
        (_data, _bases, _baseCount, _layers, _layerCount, _glyphCount, _colours, _entries) =
            (data, bases, baseCount, layers, layerCount, glyphCount, colours, entries);
    }

    /// <summary>Returns the COLR version 0 colour glyphs of a font.</summary>
    /// <param name="font">The font.</param>
    /// <returns>The shared instance, or null if the font has no usable COLR table with version 0 records, or no usable CPAL table.</returns>
    public static ColrLayers? For(OpenTypeFont font) => All.GetValue(font, Create);

    private static ColrLayers? Create(OpenTypeFont font)
    {
        if (font.Table("COLR") is not { Length: >= 14 } colr || font.Table("CPAL") is not { Length: >= 14 } cpal) return null;
        var t = new TableData(font.FileData);
        // versions 0 and 1 (10.3.2); a reader does not use a version it does not know (1.3; docs/FEHLERGEDAECHTNIS.md, No. 1149)
        if (t.U16(colr.Offset) > 1) return null;
        int baseCount = t.U16(colr.Offset + 2), baseAt = t.U32(colr.Offset + 4), layerAt = t.U32(colr.Offset + 8), layerCount = t.U16(colr.Offset + 12);
        // the records only as far as they lie inside the table (an Offset32 of 2³¹ or more reads as negative)
        baseCount = Fit(baseAt, baseCount, 6, colr.Length);
        layerCount = Fit(layerAt, layerCount, 4, colr.Length);
        if (baseCount == 0 || layerCount == 0) return null;   // a version 1 table without version 0 records
        // CPAL: without a usable table COLR is ignored (10.1 sentence 23); version 0 or 1 (11.1), at least one palette with one entry (11
        // sentences 14–15), and the colour records of the default palette inside the array and the table (11 sentence 19, 11.2 sentence 3)
        if (t.U16(cpal.Offset) > 1) return null;
        int entries = t.U16(cpal.Offset + 2), palettes = t.U16(cpal.Offset + 4), records = t.U16(cpal.Offset + 6), recordsAt = t.U32(cpal.Offset + 8);
        if (entries == 0 || palettes == 0 || 12 + 2 * palettes > cpal.Length) return null;
        var first = t.U16(cpal.Offset + 12);
        if (first + entries > records || recordsAt < 0 || (long)recordsAt + 4L * (first + entries) > cpal.Length) return null;
        return new ColrLayers(t, colr.Offset + baseAt, baseCount, colr.Offset + layerAt, layerCount, font.GlyphCount,
            cpal.Offset + recordsAt + 4 * first, entries);
    }

    /// <summary>How many records of <paramref name="size"/> bytes from <paramref name="offset"/> lie inside a table of <paramref name="length"/> bytes.</summary>
    private static int Fit(int offset, int count, int size, int length) =>
        offset <= 0 || offset >= length ? 0 : (int)Math.Min(count, (length - (long)offset) / size);

    /// <summary>Returns the layers of a glyph, bottom layer first.</summary>
    /// <param name="glyph">The base glyph.</param>
    /// <returns>
    /// The layers; null if the glyph is no base glyph of a version 0 record, has no layers or more than <see cref="MaxLayers"/>, or names a layer glyph
    /// or palette entry the font lacks (10.3.3 sentences 10, 21, 24) — such a glyph is drawn with its own outline.
    /// </returns>
    public IReadOnlyList<ColrLayer>? Layers(int glyph)
    {
        if (glyph < 0 || glyph >= _glyphCount) return null;
        // the records are sorted by glyph (10.3.3 sentences 11–12): a binary search
        int lo = 0, hi = _baseCount - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var at = _bases + mid * 6;
            var id = _data.U16(at);
            if (id < glyph) lo = mid + 1;
            else if (id > glyph) hi = mid - 1;
            else return Slice(_data.U16(at + 2), _data.U16(at + 4));
        }
        return null;
    }

    /// <summary>The layers <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> − 1 of the layer records (10.3.3 sentences 13–15).</summary>
    private ColrLayer[]? Slice(int first, int count)
    {
        if (count == 0 || count > MaxLayers || first + count > _layerCount) return null;
        var result = new ColrLayer[count];
        for (var i = 0; i < count; i++)
        {
            var at = _layers + (first + i) * 4;
            int layer = _data.U16(at), entry = _data.U16(at + 2);
            if (layer >= _glyphCount) return null;
            if (entry == 0xFFFF)
            {
                // the text colour of the application (10.3.3 sentence 25, 11.6.1)
                result[i] = new ColrLayer(layer, null, 255);
                continue;
            }
            if (entry >= _entries) return null;
            var colour = _colours + entry * 4;   // blue, green, red, alpha (11.2)
            result[i] = new ColrLayer(layer, (_data.Bytes[colour + 2] << 16) | (_data.Bytes[colour + 1] << 8) | _data.Bytes[colour], _data.Bytes[colour + 3]);
        }
        return result;
    }
}
