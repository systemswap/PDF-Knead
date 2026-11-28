using System.Buffers.Binary;

namespace PdfKnead.Core.Fonts;

public static partial class TrueTypeSubsetter
{
    /// <summary>
    /// A subset of an OpenType font with CFF or CFF2 outlines as an OpenType file around its subset 'CFF ' table (ISO 32000-2, 9.9.1, Table 124:
    /// FontFile3 /OpenType under a CIDFontType0 — the font program shall include the 'cmap' table besides 'CFF '). The OS/2 table stays with its
    /// embedding rights (OpenType 31.2.5 sentence 24: fsType is not changed when embedding) — before such a font was embedded as the bare CFF and
    /// OS/2 with fsType was lost (docs/FEHLERGEDAECHTNIS.md, No. 1168). Besides 'CFF ' and 'cmap' the file has head, hhea, hmtx, maxp, OS/2, name and
    /// post (OpenType 3.6, the tables every OpenType font needs); the tables of layout, variations and vertical metrics stay out (a PDF reader
    /// positions every glyph itself, vertical metrics come from /W2).
    /// </summary>
    /// <param name="font">The source font.</param>
    /// <param name="cff">The CID-keyed 'CFF ' table of the subset, its glyph i the glyph <paramref name="order"/>[i] of the source (the CID).</param>
    /// <param name="order">The source glyphs in the order of the subset, glyph 0 first.</param>
    /// <returns>The font file ("OTTO").</returns>
    /// <exception cref="FontFormatException">The source font has no head table or it is too short.</exception>
    internal static byte[] CffAsOpenType(OpenTypeFont font, byte[] cff, IReadOnlyList<int> order)
    {
        var count = order.Count;
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["CFF "] = cff };
        if (font.TableData("head") is { Length: >= 54 } head) data["head"] = head.ToArray();
        // maxp version 0.5: only the number of glyphs (OpenType maxp, for CFF outlines)
        var maxp = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00005000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)count);
        data["maxp"] = maxp;
        // hmtx: a full record per glyph of the subset, the advance and left side bearing of its source glyph
        var hmtx = new byte[count * 4];
        var sourceHmtx = font.TableData("hmtx");
        var sourceHhea = font.TableData("hhea");
        var full = sourceHhea.Length >= 36 ? Math.Min(BinaryPrimitives.ReadUInt16BigEndian(sourceHhea[34..]), sourceHmtx.Length / 4) : 0;
        for (var i = 0; i < count; i++)
        {
            var g = order[i];
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(i * 4), (ushort)Math.Clamp(font.AdvanceWidth(g), 0, 0xFFFF));
            var at = g < full ? g * 4 + 2 : full * 4 + (g - full) * 2;
            if (full > 0 && at + 2 <= sourceHmtx.Length) sourceHmtx.Slice(at, 2).CopyTo(hmtx.AsSpan(i * 4 + 2));
        }
        data["hmtx"] = hmtx;
        if (sourceHhea.Length >= 36)
        {
            var hhea = sourceHhea[..36].ToArray();
            BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)count);
            data["hhea"] = hhea;
        }
        if (font.TableData("OS/2") is { Length: >= 2 } os2) data["OS/2"] = os2.ToArray();
        if (font.TableData("name") is { Length: > 0 } name) data["name"] = name.ToArray();
        // post version 3.0: the header of the source without glyph names (OpenType 32.2.4)
        var post = new byte[32];
        if (font.TableData("post") is { Length: >= 32 } sourcePost) sourcePost[..32].CopyTo(post);
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00030000);
        data["post"] = post;
        data["cmap"] = CffSubsetCmap(font, order, out var first, out var last);
        SetCharRange(data, first, last);
        FitMetrics(data, count, i => font.Outline(order[i]) is { IsEmpty: false } o
            ? ((int)Math.Floor(o.Bounds.Left), (int)Math.Floor(o.Bounds.Top), (int)Math.Ceiling(o.Bounds.Right), (int)Math.Ceiling(o.Bounds.Bottom))
            : null);
        FitAverageWidth(data, count);
        return Finish(data);
    }

    /// <summary>
    /// The cmap of a subset in new glyph order: the codes of the source's Unicode subtable (3,10 before 0,4/0,6 before 3,1 before the other Unicode
    /// ones) — or of its symbol subtable (3,0) — that map to a glyph of the subset, as (3,1) format 4 and, with codes above 0xFFFF, also (3,10)
    /// format 12; a symbol font as (3,0) format 4. Without such a subtable a (3,1) table that maps nothing. <paramref name="first"/> and
    /// <paramref name="last"/>: the smallest and largest code mapped, −1 for none.
    /// </summary>
    private static byte[] CffSubsetCmap(OpenTypeFont font, IReadOnlyList<int> order, out int first, out int last)
    {
        var newGlyph = new Dictionary<int, int>(order.Count);
        for (var i = 0; i < order.Count; i++) newGlyph.TryAdd(order[i], i);
        (int Platform, int Encoding)[] preferred = [(3, 10), (0, 6), (0, 4), (3, 1), (0, 3), (0, 2), (0, 1), (0, 0), (3, 0)];
        var source = preferred.FirstOrDefault(p => font.CmapSubtables.Contains(p), (Platform: -1, Encoding: -1));
        var budget = MaxCmapCodes;
        var map = new List<(int Code, int Glyph)>();
        if (source.Platform >= 0)
            foreach (var (code, glyph) in font.Mappings(source.Platform, source.Encoding))
            {
                if (--budget < 0) break;
                if (glyph > 0 && newGlyph.TryGetValue(glyph, out var mapped)) map.Add((code, mapped));
            }
        map = [.. map.DistinctBy(m => m.Code).OrderBy(m => m.Code)];
        (first, last) = map.Count > 0 ? (map[0].Code, map[^1].Code) : (-1, -1);
        var symbol = source == (3, 0);
        List<(int Code, int Glyph)> bmpMap = [.. map.Where(m => m.Code <= 0xFFFF)];
        var bmp = Runs(bmpMap);
        // format 4 holds at most 64 KB (about 8 000 runs), beyond that with a glyph array, as in the cmap of compressed fonts
        var bmpBody = bmp.Count <= 8000 ? Format4Of(bmp) : Format4WithArray(bmpMap, 0) ?? Format12Of(bmp);
        var tables = new List<(int Platform, int Encoding, byte[] Body)> { (3, symbol ? 0 : 1, bmpBody) };
        if (!symbol && map.Any(m => m.Code > 0xFFFF)) tables.Add((3, 10, Format12Of(Runs(map))));
        var header = new byte[4 + 8 * tables.Count];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)tables.Count);
        var offset = header.Length;
        for (var i = 0; i < tables.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4 + i * 8), (ushort)tables[i].Platform);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6 + i * 8), (ushort)tables[i].Encoding);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8 + i * 8), (uint)offset);
            offset += tables[i].Body.Length;
        }
        return [.. header, .. tables.SelectMany(t => t.Body)];
    }
}
