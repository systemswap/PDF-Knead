using System.Buffers.Binary;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Redaction of a font whose glyphs exist only as images — no glyf with loca, no 'CFF ' or CFF2, but EBLC/EBDT bitmaps, CBLC/CBDT colour
/// bitmaps or sbix images: the image tables are written anew with the images of the glyphs still shown only (and of the glyphs these need: the
/// components of composite bitmaps, the targets of sbix 'dupe' records), at the same glyph indices. Before, such a font stayed as it was after
/// redacting, with the images of the redacted characters (docs/FEHLERGEDAECHTNIS.md, No. 1169).
/// </summary>
/// <remarks>
/// The image data are copied byte for byte, found as the readers find them (<see cref="BitmapLocation"/>: the first index subtable record whose
/// range holds a glyph decides); per strike and per index subtable of the font the kept images get index format 1 (offsets, the glyphs between
/// them empty), 4 (pairs, for few glyphs over a wide range) or — where all images share size and metrics (formats 2 and 5) — 5. The work is
/// bounded (<see cref="MaxWork"/>, <see cref="MaxOutput"/>); a prepared font beyond it fails with <see cref="FontFormatException"/> and stays as it
/// is, as every scrubber leaves a program it cannot rewrite faithfully.
/// </remarks>
internal static class BitmapScrubber
{
    /// <summary>Records and glyph entries read at most.</summary>
    private const long MaxWork = 50_000_000;

    /// <summary>Bytes written at most per table.</summary>
    private const long MaxOutput = 256L << 20;

    /// <summary>The tables written anew.</summary>
    internal static readonly string[] Rewritten = ["EBLC", "EBDT", "CBLC", "CBDT", "sbix"];

    /// <summary>Whether a font has glyph images but no outlines (then it is scrubbed here).</summary>
    internal static bool ImagesOnly(OpenTypeFont font) =>
        !EmbeddedBitmaps.HasOutlines(font)
        && (font.HasTable("EBLC") && font.HasTable("EBDT") || font.HasTable("CBLC") && font.HasTable("CBDT") || font.HasTable("sbix"));

    /// <summary>
    /// The glyphs to keep: glyph 0, the glyphs <paramref name="glyphs"/>, and what their images need — components of EBDT composites (13.2.8,
    /// 13.2.9) and targets of sbix 'dupe' records (34.3) — in any strike.
    /// </summary>
    internal static HashSet<int> Kept(OpenTypeFont font, IEnumerable<int> glyphs)
    {
        var count = font.GlyphCount;
        var keep = new HashSet<int> { 0 };
        var pending = new Stack<int>(glyphs.Where(g => g >= 0 && g < count).Append(0));
        var composites = Pairs(font, "EBLC", "EBDT") is { } eb ? (eb.Tables, Strikes: BitmapLocation.Strikes(font.FileData, eb.Location, _ => true) ?? []) : default;
        var sbix = SbixStrikes(font);
        long work = 0;
        while (pending.Count > 0)
        {
            var g = pending.Pop();
            keep.Add(g);
            foreach (var strike in composites.Strikes ?? [])
            {
                if ((work += strike.SubTables) > MaxWork) throw new FontFormatException("Bitmap-Schrift zu aufwendig");
                if (BitmapLocation.Find(composites.Tables, strike, g) is not { ImageFormat: 8 or 9 } at) continue;
                var d = font.FileData;
                var list = at.At + (at.ImageFormat == 8 ? 6 : 8);
                if (list + 2 > at.At + at.Length) continue;
                var n = (d[list] << 8) | d[list + 1];
                for (var c = 0; c < n && list + 2 + c * 4 + 4 <= at.At + at.Length; c++)
                    if (((d[list + 2 + c * 4] << 8) | d[list + 3 + c * 4]) is var component && component < count && !keep.Contains(component))
                        pending.Push(component);
            }
            foreach (var (start, end) in sbix?.Strikes ?? [])
                if (SbixRecord(font.FileData, start, end, g) is var (at, length) && length >= 10 && Tag(font.FileData, at + 4) == "dupe"
                    && ((font.FileData[at + 8] << 8) | font.FileData[at + 9]) is var target && target < count && !keep.Contains(target))
                    pending.Push(target);
        }
        return keep;
    }

    /// <summary>The image tables written anew with the images of <paramref name="keep"/> only; an image table it cannot read is left out.</summary>
    internal static Dictionary<string, byte[]> Rebuild(OpenTypeFont font, HashSet<int> keep)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (location, data) in Pairs())
            if (Pairs(font, location, data) is { } pair && Location(font, pair, keep) is var (newLocation, newData))
                (result[location], result[data]) = (newLocation, newData);
        if (font.HasTable("sbix") && Sbix(font, keep) is { } sbix) result["sbix"] = sbix;
        return result;
    }

    /// <summary>
    /// Whether every glyph of <paramref name="kept"/> finds the same images in <paramref name="after"/> as in <paramref name="before"/> — the same
    /// strikes in order, the same image format, data and metrics, the same sbix records — and no image where it had none.
    /// </summary>
    internal static bool SameImages(OpenTypeFont before, OpenTypeFont after, IEnumerable<int> kept)
    {
        foreach (var g in kept.Order())
        {
            if (g < 0 || g >= before.GlyphCount) continue;
            if (!Images(before, g).SequenceEqual(Images(after, g), StringComparer.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>The images of a glyph as comparable strings (strike, format, data, metrics), in the order the readers find them.</summary>
    private static IEnumerable<string> Images(OpenTypeFont font, int glyph)
    {
        var d = font.FileData;
        foreach (var (location, data) in Pairs())
        {
            if (Pairs(font, location, data) is not { } pair) continue;
            foreach (var s in BitmapLocation.Strikes(d, pair.Location, _ => true) ?? [])
                if (BitmapLocation.Find(pair.Tables, s, glyph) is { } at)
                    yield return $"{location} {s.Ppem} {s.PpemX} {s.BitDepth} {s.Flags} {at.ImageFormat} {Convert.ToHexString(d, at.At, at.Length)} "
                                 + (at.Metrics >= 0 ? Convert.ToHexString(d, at.Metrics, 8) : "");
        }
        foreach (var (start, end) in SbixStrikes(font)?.Strikes ?? [])
            if (SbixRecord(d, start, end, glyph) is var (at, length) && length > 0)
                yield return $"sbix {(d[start] << 8) | d[start + 1]} {Convert.ToHexString(d, at, length)}";
    }

    // ------------------------------------------------------------ EBLC/EBDT and CBLC/CBDT

    /// <summary>The location tables with their data tables.</summary>
    private static (string Location, string Data)[] Pairs() => [("EBLC", "EBDT"), ("CBLC", "CBDT")];

    private static (BitmapLocation.Tables Tables, (int Offset, int Length) Location, (int Offset, int Length) Data)? Pairs(OpenTypeFont font, string location, string data) =>
        font.Table(location) is { Length: >= 8 } l && font.Table(data) is { Length: >= 4 } b
            ? (new BitmapLocation.Tables(font.FileData, l.Offset, l.Offset + l.Length, b.Offset, b.Offset + b.Length), l, b)
            : null;

    /// <summary>A location table and its data table written anew; null if the location table cannot be read (both are then left out).</summary>
    private static (byte[] Location, byte[] Data)? Location(OpenTypeFont font,
        (BitmapLocation.Tables Tables, (int Offset, int Length) Location, (int Offset, int Length) Data) pair, HashSet<int> keep)
    {
        var d = font.FileData;
        if (BitmapLocation.Strikes(d, pair.Location, _ => true) is not { } strikes) return null;
        var sorted = keep.Where(g => g >= 0 && g <= ushort.MaxValue).Order().ToArray();
        var data = new Writer();
        data.Bytes(d, pair.Data.Offset, 4);   // version of the data table
        var written = new List<(byte[] Record, byte[] List)>();
        long work = 0;
        foreach (var strike in strikes)
        {
            // each kept glyph belongs to the first record whose range holds it, whether that one has an image of it or not (14.2)
            var next = Enumerable.Range(0, sorted.Length + 1).ToArray();   // next unclaimed position at or after an index (path halving)
            int Next(int i)
            {
                while (next[i] != i) i = next[i] = next[next[i]];
                return i;
            }
            var subtables = new List<(int First, int Last, byte[] Bytes)>();
            for (var r = 0; r < strike.SubTables; r++)
            {
                if ((work += 1) > MaxWork) throw new FontFormatException("Bitmap-Schrift zu aufwendig");
                var entry = strike.Array + r * 8;
                int first = U16(d, entry), last = U16(d, entry + 2);
                var header = strike.Array + (long)(uint)U32(d, entry + 4);
                var images = new List<(int Glyph, int At, int Length)>();
                var lookup = header + 8 <= pair.Tables.LocationEnd ? Lookup(pair.Tables, (int)header, first, ref work) : null;
                for (var i = Next(LowerBound(sorted, first)); i < sorted.Length && sorted[i] <= last; i = Next(i + 1))
                {
                    next[i] = i + 1;   // claimed
                    if (lookup?.Invoke(sorted[i]) is { } at) images.Add((sorted[i], at.At, at.Length));
                }
                if (images.Count == 0 || lookup is null) continue;
                subtables.Add(Subtable(pair.Tables, (int)header, images, data));
                if (data.Length > MaxOutput) throw new FontFormatException("Bitmap-Tabelle zu groß");
            }
            if (subtables.Count == 0) continue;   // a strike without kept images is left out
            var list = new Writer();
            var offset = subtables.Count * 8;
            foreach (var (first, last, bytes) in subtables)
            {
                list.U16(first).U16(last).U32(offset);
                offset += bytes.Length;
            }
            foreach (var (_, _, bytes) in subtables) list.Bytes(bytes, 0, bytes.Length);
            if (list.Length > MaxOutput) throw new FontFormatException("Bitmap-Tabelle zu groß");
            var record = d.AsSpan(strike.Record, BitmapLocation.BitmapSizeLength).ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4), (uint)list.Length);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(8), (uint)subtables.Count);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(40), (ushort)subtables.Min(s => s.First));
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(42), (ushort)subtables.Max(s => s.Last));
            written.Add((record, list.ToArray()));
        }
        var location = new Writer();
        location.Bytes(d, pair.Location.Offset, 4).U32(written.Count);   // version, numSizes
        var listAt = 8 + written.Count * BitmapLocation.BitmapSizeLength;
        foreach (var (record, list) in written)
        {
            BinaryPrimitives.WriteUInt32BigEndian(record, (uint)listAt);
            location.Bytes(record, 0, record.Length);
            listAt += list.Length;
        }
        foreach (var (_, list) in written) location.Bytes(list, 0, list.Length);
        return (location.ToArray(), data.ToArray());
    }

    /// <summary>
    /// The image of a glyph through one index subtable (null if none); formats 4 and 5 read their glyph list once, so that many kept glyphs do
    /// not search it each time. Null for an index subtable not read.
    /// </summary>
    private static Func<int, BitmapLocation.Located?>? Lookup(BitmapLocation.Tables tables, int header, int first, ref long work)
    {
        var d = tables.Data;
        var format = U16(d, header);
        if (format is 1 or 2 or 3) return g => BitmapLocation.InSubtable(tables, header, g - first, g);
        if (format is not (4 or 5)) return null;
        var countAt = format == 4 ? header + 8 : header + 20;
        if (!tables.Inside(countAt, 4)) return null;
        var count = (uint)U32(d, countAt);
        if ((work += count) > MaxWork) throw new FontFormatException("Bitmap-Schrift zu aufwendig");
        var index = new Dictionary<int, int>();
        var step = format == 4 ? 4 : 2;
        for (var k = 0; k < count && tables.Inside(countAt + 4 + (long)k * step, 2); k++) index.TryAdd(U16(d, countAt + 4 + k * step), k);
        return g => index.TryGetValue(g, out var k) ? BitmapLocation.InSubtable(tables, header, g - first, g, k) : null;
    }

    /// <summary>One index subtable with the kept images (sorted by glyph), their data appended to <paramref name="data"/>.</summary>
    private static (int First, int Last, byte[] Bytes) Subtable(BitmapLocation.Tables tables, int header, List<(int Glyph, int At, int Length)> images, Writer data)
    {
        var d = tables.Data;
        int first = images[0].Glyph, last = images[^1].Glyph;
        var imageFormat = U16(d, header + 2);
        var imageDataOffset = data.Length;   // from the start of the data table
        var sub = new Writer();
        if (U16(d, header) is 2 or 5)
        {
            // one size and one set of big metrics for all (format 5: sparse glyph list)
            sub.U16(5).U16(imageFormat).U32(imageDataOffset).U32(images[0].Length).Bytes(d, header + 12, 8).U32(images.Count);
            foreach (var (glyph, at, length) in images)
            {
                sub.U16(glyph);
                data.Bytes(d, at, length);
            }
        }
        else if (last - first + 1 > 2 * images.Count + 8 && images.Sum(i => (long)i.Length) <= ushort.MaxValue)
        {
            // few glyphs over a wide range: pairs of glyph and offset (format 4), one more for the length of the last
            sub.U16(4).U16(imageFormat).U32(imageDataOffset).U32(images.Count);
            var offset = 0;
            foreach (var (glyph, at, length) in images)
            {
                sub.U16(glyph).U16(offset);
                data.Bytes(d, at, length);
                offset += length;
            }
            sub.U16(0).U16(offset);
        }
        else
        {
            // offsets for every glyph of the range (format 1); a glyph not kept has length 0
            sub.U16(1).U16(imageFormat).U32(imageDataOffset);
            var k = 0;
            long offset = 0;
            for (var g = first; g <= last; g++)
            {
                sub.U32(offset);
                if (k < images.Count && images[k].Glyph == g)
                {
                    data.Bytes(d, images[k].At, images[k].Length);
                    offset += images[k].Length;
                    k++;
                }
            }
            sub.U32(offset);
        }
        sub.Pad(4);
        return (first, last, sub.ToArray());
    }

    private static int LowerBound(int[] sorted, int value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // ------------------------------------------------------------ sbix

    /// <summary>The strikes of an sbix table of version 1 (start of each strike and end of the table); null if it cannot be read.</summary>
    private static (List<(int Start, int End)> Strikes, int Flags)? SbixStrikes(OpenTypeFont font)
    {
        if (font.Table("sbix") is not { Length: >= 8 } table) return null;
        var d = font.FileData;
        if (U16(d, table.Offset) != 1) return null;
        var count = (uint)U32(d, table.Offset + 4);
        var end = table.Offset + table.Length;
        var strikes = new List<(int, int)>();
        var glyphs = font.GlyphCount;
        for (var i = 0; i < count && i < 256 && table.Offset + 8 + i * 4 + 4 <= end; i++)
        {
            var at = (uint)U32(d, table.Offset + 8 + i * 4);
            // the strike header and its offsets (numGlyphs + 1) inside the table
            if (at < 8 || table.Offset + (long)at + 4 + (glyphs + 1) * 4L > end) continue;
            strikes.Add((table.Offset + (int)at, end));
        }
        return (strikes, U16(d, table.Offset + 2));
    }

    /// <summary>The glyph data record of a glyph in an sbix strike (at least 8 bytes, inside the table); null if it has none.</summary>
    private static (int At, int Length)? SbixRecord(byte[] d, int strike, int end, int glyph)
    {
        long start = (uint)U32(d, strike + 4 + glyph * 4), stop = (uint)U32(d, strike + 4 + (glyph + 1) * 4);
        if (stop - start < 8 || strike + stop > end) return null;
        return (strike + (int)start, (int)(stop - start));
    }

    /// <summary>The sbix table with the glyph data records of <paramref name="keep"/> only (the others empty); null if it cannot be read.</summary>
    private static byte[]? Sbix(OpenTypeFont font, HashSet<int> keep)
    {
        if (SbixStrikes(font) is not var (strikes, flags)) return null;
        var d = font.FileData;
        var glyphs = font.GlyphCount;
        var parts = new List<byte[]>();
        foreach (var (start, end) in strikes)
        {
            var strike = new Writer();
            strike.Bytes(d, start, 4);   // ppem, ppi
            var records = new Writer();
            for (var g = 0; g < glyphs; g++)
            {
                strike.U32(4 + (glyphs + 1) * 4L + records.Length);
                if (keep.Contains(g) && SbixRecord(d, start, end, g) is var (at, length)) records.Bytes(d, at, length);
            }
            strike.U32(4 + (glyphs + 1) * 4L + records.Length);
            if (records.Length > MaxOutput) throw new FontFormatException("sbix zu groß");
            strike.Bytes(records.ToArray(), 0, records.Length);
            parts.Add(strike.ToArray());
        }
        var table = new Writer();
        table.U16(1).U16(flags).U32(parts.Count);
        long offset = 8 + parts.Count * 4L;
        foreach (var part in parts)
        {
            table.U32(offset);
            offset += part.Length;
        }
        foreach (var part in parts) table.Bytes(part, 0, part.Length);
        return table.ToArray();
    }

    // ------------------------------------------------------------ helpers

    private static int U16(byte[] d, long at) => at >= 0 && at + 2 <= d.Length ? (d[at] << 8) | d[at + 1] : 0;

    private static int U32(byte[] d, long at) => at >= 0 && at + 4 <= d.Length ? (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3] : 0;

    private static string Tag(byte[] d, int at) => at >= 0 && at + 4 <= d.Length ? System.Text.Encoding.Latin1.GetString(d, at, 4) : "";

    /// <summary>Big-endian bytes, written in order.</summary>
    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();

        public int Length => (int)_stream.Length;

        public Writer U16(int v)
        {
            _stream.WriteByte((byte)(v >> 8));
            _stream.WriteByte((byte)v);
            return this;
        }

        public Writer U32(long v) => U16((int)(v >> 16) & 0xFFFF).U16((int)v & 0xFFFF);

        public Writer Bytes(byte[] data, int at, int length)
        {
            _stream.Write(data, at, length);
            return this;
        }

        public Writer Pad(int multiple)
        {
            while (_stream.Length % multiple != 0) _stream.WriteByte(0);
            return this;
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
