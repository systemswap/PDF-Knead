namespace PdfKnead.Core.Fonts;

/// <summary>
/// The part that the bitmap location tables 'EBLC' and 'CBLC' share (OpenType 1.9, "EBLC — Embedded Bitmap Location Table" 14, and
/// "CBLC — Color Bitmap Location Table" 6, which takes over its structure): the BitmapSize records of the strikes, their index subtable
/// lists and the five index subtable formats that locate the image data of a glyph in 'EBDT' or 'CBDT'. Used by the colour images
/// (<see cref="CbdtImages"/>), the monochrome and grey bitmaps (<see cref="EmbeddedBitmaps"/>) and the scrubber of bitmap fonts
/// (<see cref="BitmapScrubber"/>), so that all three find the same image for a glyph.
/// </summary>
/// <remarks>Every offset is checked against its table: a damaged or prepared font only loses images and never reads outside the file.</remarks>
internal static class BitmapLocation
{
    /// <summary>Length of a BitmapSize record (14.1).</summary>
    internal const int BitmapSizeLength = 48;

    /// <summary>Strikes read at most (numSizes is a uint32).</summary>
    internal const int MaxStrikes = 256;

    /// <summary>Index subtables per strike at most (numberOfIndexSubtables is a uint32).</summary>
    internal const int MaxIndexSubTables = 65536;

    /// <summary>A strike: its BitmapSize record and what is read from it.</summary>
    /// <param name="Record">Absolute position of the BitmapSize record.</param>
    /// <param name="Array">Absolute position of the IndexSubtableList (indexSubtableListOffset from the table start).</param>
    /// <param name="SubTables">numberOfIndexSubtables.</param>
    /// <param name="Ppem">ppemY.</param>
    /// <param name="PpemX">ppemX (ppemY when 0).</param>
    /// <param name="BitDepth">bitDepth.</param>
    /// <param name="Flags">flags (0x01 horizontal, 0x02 vertical small glyph metrics).</param>
    internal readonly record struct Strike(int Record, int Array, int SubTables, int Ppem, int PpemX, int BitDepth, int Flags);

    /// <summary>Where the image data of a glyph lie.</summary>
    /// <param name="IndexFormat">Format of the index subtable.</param>
    /// <param name="ImageFormat">Format of the image data.</param>
    /// <param name="At">Absolute position of the image data (inside the data table).</param>
    /// <param name="Length">Their length (above 0).</param>
    /// <param name="Metrics">Absolute position of the bigGlyphMetrics of the index subtable (formats 2 and 5); -1 for the others.</param>
    internal readonly record struct Located(int IndexFormat, int ImageFormat, int At, int Length, int Metrics);

    /// <summary>The bounds of a location table and its data table in the font file.</summary>
    internal readonly record struct Tables(byte[] Data, int LocationStart, int LocationEnd, int DataStart, int DataEnd)
    {
        public bool Inside(long at, int length) => at >= LocationStart && at + length <= LocationEnd;
    }

    /// <summary>
    /// Reads the BitmapSize records of a location table of major version 2 or 3 (EBLC 2.0, CBLC 3.0; the first versions): the strikes
    /// whose bit depth <paramref name="bitDepth"/> accepts, with ppemY above 0 and an index subtable list inside the table, in the order of
    /// the table.
    /// </summary>
    internal static List<Strike>? Strikes(byte[] data, (int Offset, int Length) location, Func<int, bool> bitDepth)
    {
        if (location.Length < 8) return null;
        var t = new Layout.TableData(data);
        var start = location.Offset;
        if (t.U16(start) is not (2 or 3)) return null;
        var sizes = Math.Min((uint)t.U32(start + 4), MaxStrikes);
        var found = new List<Strike>();
        for (var i = 0; i < sizes; i++)
        {
            var record = start + 8 + i * BitmapSizeLength;
            if (record + BitmapSizeLength > start + location.Length) break;
            var array = (uint)t.U32(record);
            var count = (uint)t.U32(record + 8);
            var ppem = data[record + 45];
            if (!bitDepth(data[record + 46]) || ppem == 0 || count == 0 || count > MaxIndexSubTables || array + 8L * count > location.Length) continue;
            // ppemX (+44) and the flags (+47) too: pixels need not be square, and small metrics may be vertical ones (OpenType 14.1; No. 1160)
            found.Add(new Strike(record, start + (int)array, (int)count, ppem, data[record + 44] == 0 ? ppem : data[record + 44], data[record + 46], data[record + 47]));
        }
        return found;
    }

    /// <summary>
    /// The image data of <paramref name="glyph"/> in a strike: the first IndexSubtableRecord whose range holds it decides (14.2; a later one
    /// is not searched when it has no image); null if no record holds it or its index subtable gives no image data inside the data table.
    /// </summary>
    internal static Located? Find(in Tables tables, in Strike strike, int glyph)
    {
        var t = new Layout.TableData(tables.Data);
        for (var i = 0; i < strike.SubTables; i++)
        {
            var entry = strike.Array + i * 8;
            if (glyph < t.U16(entry) || glyph > t.U16(entry + 2)) continue;
            var header = strike.Array + (long)(uint)t.U32(entry + 4);
            return header + 8 > tables.LocationEnd ? null : InSubtable(tables, (int)header, glyph - t.U16(entry), glyph);
        }
        return null;
    }

    /// <summary>
    /// The image data of a glyph through one index subtable (14.3): <paramref name="index"/> is its place in the range of the record
    /// (glyph − firstGlyphIndex), used by formats 1–3; formats 4 and 5 search <paramref name="glyph"/> in their list, starting at
    /// <paramref name="known"/> when it is the glyph's place there (the scrubber reads each list once).
    /// </summary>
    internal static Located? InSubtable(in Tables tables, int header, int index, int glyph, int known = -1)
    {
        var t = new Layout.TableData(tables.Data);
        var indexFormat = t.U16(header);
        var imageFormat = t.U16(header + 2);
        var imageData = (uint)t.U32(header + 4);
        long offset, length;
        var metrics = -1;   // bigGlyphMetrics in the index subtable (formats 2 and 5)
        switch (indexFormat)
        {
            case 1 when tables.Inside(header + 8L + (index + 1) * 4L, 4):
                offset = (uint)t.U32(header + 8 + index * 4);
                length = (uint)t.U32(header + 8 + (index + 1) * 4) - offset;
                break;
            case 3 when tables.Inside(header + 8L + (index + 1) * 2L, 2):
                offset = t.U16(header + 8 + index * 2);
                length = t.U16(header + 8 + (index + 1) * 2) - offset;
                break;
            case 2 when tables.Inside(header + 12, 8):
                length = (uint)t.U32(header + 8);
                offset = index * length;
                metrics = header + 12;
                break;
            case 4 when tables.Inside(header + 8, 4):
            {
                var count = (uint)t.U32(header + 8);
                if (!tables.Inside(header + 12 + count * 4L, 4)) return null;
                var k = known is >= 0 && known < count && t.U16(header + 12 + known * 4) == glyph ? known : 0;
                while (k < count && t.U16(header + 12 + k * 4) != glyph) k++;
                if (k == count) return null;
                offset = t.U16(header + 12 + k * 4 + 2);
                length = t.U16(header + 12 + (k + 1) * 4 + 2) - offset;
                break;
            }
            case 5 when tables.Inside(header + 20, 4):
            {
                var count = (uint)t.U32(header + 20);
                if (count == 0 || !tables.Inside(header + 24 + (count - 1) * 2L, 2)) return null;
                var k = known is >= 0 && known < count && t.U16(header + 24 + known * 2) == glyph ? known : 0;
                while (k < count && t.U16(header + 24 + k * 2) != glyph) k++;
                if (k == count) return null;
                length = (uint)t.U32(header + 8);
                offset = k * length;
                metrics = header + 12;
                break;
            }
            default:
                return null;
        }
        var at = tables.DataStart + (long)imageData + offset;
        if (length <= 0 || at < tables.DataStart || at + length > tables.DataEnd) return null;
        return new Located(indexFormat, imageFormat, (int)at, (int)length, metrics);
    }
}
