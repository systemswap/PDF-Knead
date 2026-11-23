using System.Buffers.Binary;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Metadata that objects carry inside their own data (docs/RECHERCHE-VERSTECKEN.md, B-3), removed with „Metadaten“ when sanitising, without
/// changing what the objects show:
/// <list type="bullet">
/// <item>JPEG: EXIF (with a preview of its own — a cropped photo can show whole in it —, GPS, camera, serial number), XMP, Photoshop/IPTC,
/// comments, JFXX previews and the segments of other programs; cut out losslessly. JFIF (without the preview it may carry), the colour
/// profile and the Adobe segment stay: they decide the colours.</item>
/// <item>JPEG 2000: the boxes XML, UUID (XMP, GeoJP2, camera data) and UUID info.</item>
/// <item>Colour profiles (ICC): descriptions of the device maker and model, the metadata tag; the profile ID (a checksum) becomes 0.</item>
/// </list>
/// Copyright and licence notices in fonts and profiles stay: many licences require them, and they tell nothing about the document.
/// </summary>
internal static class ObjectMetadata
{
    /// <summary>What kinds of object metadata a document carries, counted for the window.</summary>
    /// <param name="Jpeg">JPEG images with metadata segments.</param>
    /// <param name="Jpx">JPEG 2000 images with metadata boxes.</param>
    /// <param name="Icc">Colour profiles with device descriptions or metadata.</param>
    /// <param name="FontNotices">Font programs with copyright, licence or other free texts (<see cref="CleanupItems.Notices"/>).</param>
    /// <param name="IccNotices">Colour profiles with description or copyright text (<see cref="CleanupItems.Notices"/>).</param>
    internal readonly record struct Counts(int Jpeg, int Jpx, int Icc, int FontNotices, int IccNotices);

    /// <summary>At most this many objects are looked at for the window; streams larger than this are not decoded (profiles).</summary>
    private const int MaxObjects = 200_000;
    private const int MaxProfile = 4 << 20;

    /// <summary>The stream without the metadata in its data, or null if it has none (or is not such a stream).</summary>
    /// <param name="stream">The stream as it is written.</param>
    /// <param name="doc">The document it comes from.</param>
    /// <param name="items">The chosen items: <see cref="CleanupItems.Metadata"/> and/or <see cref="CleanupItems.Notices"/>.</param>
    /// <returns>A replacement with the same dictionary (and filter), or null.</returns>
    public static PdfStream? Clean(PdfStream stream, PdfDocument doc, CleanupItems items)
    {
        var metadata = items.HasFlag(CleanupItems.Metadata);
        var notices = items.HasFlag(CleanupItems.Notices);
        try
        {
            switch (OnlyFilter(stream.Dictionary))
            {
                case "DCTDecode" when metadata && Jpeg(doc.GetRawStreamData(stream)) is { } jpeg:
                    return WithRaw(stream, jpeg);
                case "JPXDecode" when metadata && Jpx(doc.GetRawStreamData(stream)) is { } jpx:
                    return WithRaw(stream, jpx);
                case null or "FlateDecode" or "LZWDecode" or "ASCIIHexDecode" or "ASCII85Decode" when MaybeProfile(stream) && (metadata || notices):
                    var data = doc.GetStreamData(stream);
                    var profile = metadata ? Icc(data) ?? data : data;
                    if (notices && IccNotices(profile) is { } blank) profile = blank;
                    return ReferenceEquals(profile, data) ? null : Decoded(stream, profile);
                case null or "FlateDecode" or "LZWDecode" or "ASCIIHexDecode" or "ASCII85Decode" when notices && MaybeFont(stream):
                    return FontNotices(doc.GetStreamData(stream)) is { } font ? Decoded(stream, font) : null;
                default:
                    return null;
            }
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return null;   // unreadable: stays as it is (it would not be written differently anyway)
        }
    }

    /// <summary>Counts the objects with such metadata (for „Dokumenteigenschaften und Metadaten“ in the window).</summary>
    public static Counts Count(PdfDocument doc)
    {
        int jpeg = 0, jpx = 0, icc = 0, fonts = 0, iccNotices = 0;
        for (var n = 1; n <= Math.Min(doc.ObjectCount, MaxObjects); n++)
        {
            try
            {
                if (doc.GetObject(n) is not PdfStream stream) continue;
                switch (OnlyFilter(stream.Dictionary))
                {
                    case "DCTDecode" when stream.DataLength < 256 << 20 && Jpeg(doc.GetRawStreamData(stream)) is not null:
                        jpeg++;
                        break;
                    case "JPXDecode" when stream.DataLength < 256 << 20 && Jpx(doc.GetRawStreamData(stream)) is not null:
                        jpx++;
                        break;
                    case null or "FlateDecode" or "LZWDecode" or "ASCIIHexDecode" or "ASCII85Decode" when MaybeProfile(stream):
                        var profile = doc.GetStreamData(stream);
                        if (Icc(profile) is not null) icc++;
                        if (IccNotices(profile) is not null) iccNotices++;
                        break;
                    case null or "FlateDecode" or "LZWDecode" or "ASCIIHexDecode" or "ASCII85Decode" when MaybeFont(stream) && stream.DataLength < 64 << 20:
                        if (FontNotices(doc.GetStreamData(stream)) is not null) fonts++;
                        break;
                }
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
            }
        }
        return new Counts(jpeg, jpx, icc, fonts, iccNotices);
    }

    /// <summary>The filter of a stream if it has exactly one (null without a filter, "" with several).</summary>
    private static string? OnlyFilter(PdfDictionary dict) => dict.Get(PdfName.Filter) switch
    {
        PdfName name => name.Value,
        PdfArray { Count: 1 } one when one.Get(0) is PdfName name => name.Value,
        PdfArray { Count: > 1 } => "",
        _ => null,
    };

    /// <summary>An ICC profile stream has /N (1, 3 or 4) and is not an image or form.</summary>
    private static bool MaybeProfile(PdfStream stream) =>
        stream.Dictionary.GetInteger(PdfName.N) is 1 or 3 or 4 && stream.Dictionary.GetName(PdfName.Subtype) is null && stream.DataLength <= MaxProfile;

    /// <summary>A font program (FontFile, FontFile2, FontFile3): it has /Length1 or a font subtype.</summary>
    private static bool MaybeFont(PdfStream stream) =>
        stream.Dictionary.ContainsKey(PdfName.Get("Length1")) || stream.Dictionary.GetName(PdfName.Subtype)?.Value is "Type1C" or "CIDFontType0C" or "OpenType";

    /// <summary>The stream with new, unfiltered data (the writer packs it again); /Length1 … stay valid, the length is the same.</summary>
    private static PdfStream Decoded(PdfStream stream, byte[] data)
    {
        var dict = Copy(stream.Dictionary);
        dict.Remove(PdfName.Filter);
        dict.Remove(PdfName.DecodeParms);
        dict.Remove(PdfName.Length);
        return new PdfStream(dict, data);
    }

    private static PdfStream WithRaw(PdfStream stream, byte[] data)
    {
        var dict = Copy(stream.Dictionary);
        dict.Remove(PdfName.Length);
        return new PdfStream(dict, data);
    }

    private static PdfDictionary Copy(PdfDictionary source)
    {
        var dict = new PdfDictionary(source.Count) { Resolver = source.Resolver };
        foreach (var (key, value) in source.Entries) dict.Set(key, value);
        return dict;
    }

    // ------------------------------------------------------------ JPEG

    /// <summary>
    /// A JPEG without its metadata segments (up to the image data), or null if it has none: APP1 (EXIF, XMP), APP3–APP13 (among them
    /// Photoshop/IPTC), APP15, COM, APP0 other than JFIF (JFXX previews), APP2 other than the colour profile — and the preview a JFIF segment
    /// carries itself.
    /// </summary>
    internal static byte[]? Jpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        var drop = new List<(int Start, int End, byte[] Replacement)>();
        var at = 2;
        while (at + 4 <= data.Length)
        {
            if (data[at] != 0xFF) return null;   // not a marker where one belongs: leave the file as it is
            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                at += 2;
                continue;
            }
            if (marker is 0xD9 or 0xDA) break;   // image data from here on
            var length = data[at + 2] << 8 | data[at + 3];
            if (length < 2 || at + 2 + length > data.Length) return null;
            var body = data.Slice(at + 4, length - 2);
            var metadata = marker switch
            {
                0xE0 => !body.StartsWith("JFIF\0"u8),
                0xE2 => !body.StartsWith("ICC_PROFILE\0"u8),
                0xEE => false,                                   // Adobe: colour transform
                >= 0xE1 and <= 0xEF => true,
                0xFE => true,                                    // comment
                _ => false,
            };
            if (metadata) drop.Add((at, at + 2 + length, []));
            // JFIF stays, but not the preview it may carry itself (Xthumbnail × Ythumbnail RGB after the 14 bytes of its fields, T.871, 6.3):
            // a cropped photo can show whole in it — the segment keeps its fields with a preview of 0 × 0 (docs/FEHLERGEDAECHTNIS.md, No. 960)
            else if (marker == 0xE0 && body.Length > 14)
                drop.Add((at, at + 2 + length, [0xFF, 0xE0, 0x00, 0x10, .. body[..12], 0x00, 0x00]));
            at += 2 + length;
        }
        if (drop.Count == 0) return null;
        var result = new byte[data.Length - drop.Sum(d => d.End - d.Start - d.Replacement.Length)];
        var written = 0;
        var from = 0;
        foreach (var (start, end, replacement) in drop)
        {
            data[from..start].CopyTo(result.AsSpan(written));
            written += start - from;
            replacement.CopyTo(result, written);
            written += replacement.Length;
            from = end;
        }
        data[from..].CopyTo(result.AsSpan(written));
        return result;
    }

    // ------------------------------------------------------------ JPEG 2000

    /// <summary>
    /// A JP2/JPX file or a bare codestream without its metadata, or null if it has none: the boxes XML, UUID, UUID info and intellectual
    /// property rights at any level of the box structure — the top level and the superboxes —, and the comment segments of a contiguous
    /// codestream (main header and tile-part headers). The lengths of the boxes around a removed part, the Psot of its tile-part and its entry
    /// in TLM shrink with it. Before only the top level was cleaned; rights, comments and metadata inside
    /// superboxes stayed (decided on 23.11.2026, L45 brought forward).
    /// </summary>
    internal static byte[]? Jpx(ReadOnlySpan<byte> data)
    {
        var drop = new List<(int Start, int End)>();
        var fields = new List<(int Position, int Size, int Start, int End)>();
        // a bare codestream (ISO 32000-2, 7.4.9 allows one in a JPXDecode stream): its comment segments all the same — before only a JP2/JPX
        // file was cleaned, and the comments of a bare codestream stayed (23.11.2026)
        var bare = data.Length >= 2 && data[0] == 0xFF && data[1] == 0x4F;
        if (bare) CodestreamComments(data, 0, data.Length, drop, fields);
        else if (data.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(data) != 12 || !data.Slice(4, 4).SequenceEqual("jP  "u8)
                 || !MetadataParts(data, 0, data.Length, 0, drop, fields)) return null;
        if (drop.Count == 0) return null;
        // the fragments of a Fragment Table or Cross-Reference box lie at offsets from the start of the file (ISO/IEC 15444-2, M.11.3, M.11.4):
        // a part a fragment points into stays, and every offset behind a removed part moves back by its length — before the boxes were cut out
        // and the fragments pointed at the wrong bytes, the image was destroyed (M2, T.800 I.7, 23.11.2026)
        var lists = new List<int>();
        if (!bare) FragmentLists(data, 0, data.Length, 0, lists);
        foreach (var p in lists)
            for (var i = 0; i < BinaryPrimitives.ReadUInt16BigEndian(data[p..]); i++)
            {
                var entry = p + 2 + 14 * i;
                var offset = BinaryPrimitives.ReadUInt64BigEndian(data[entry..]);
                var end = offset + BinaryPrimitives.ReadUInt32BigEndian(data[(entry + 8)..]);
                drop.RemoveAll(d => offset < (ulong)d.End && end > (ulong)d.Start);
            }
        if (drop.Count == 0) return null;
        drop.Sort();
        var result = new byte[data.Length - drop.Sum(d => d.End - d.Start)];
        var written = 0;
        var from = 0;
        foreach (var (start, end) in drop)
        {
            data[from..start].CopyTo(result.AsSpan(written));
            written += start - from;
            from = end;
        }
        data[from..].CopyTo(result.AsSpan(written));
        long Before(long position) => drop.Where(d => d.End <= position).Sum(d => (long)(d.End - d.Start));
        // the length of every box (and the Psot of every tile-part) around a removed part shrinks by what was removed inside it
        foreach (var (position, size, start, end) in fields)
        {
            var inside = drop.Where(d => d.Start >= start && d.End <= end).Sum(d => (long)(d.End - d.Start));
            if (inside == 0) continue;
            var at = (int)(position - Before(position));
            if (size == 8) BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(at), BinaryPrimitives.ReadUInt64BigEndian(data[position..]) - (ulong)inside);
            else if (size == 2) BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(at), (ushort)(BinaryPrimitives.ReadUInt16BigEndian(data[position..]) - inside));
            else BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(at), BinaryPrimitives.ReadUInt32BigEndian(data[position..]) - (uint)inside);
        }
        foreach (var p in lists)
            for (var i = 0; i < BinaryPrimitives.ReadUInt16BigEndian(data[p..]); i++)
            {
                var entry = p + 2 + 14 * i;
                var offset = BinaryPrimitives.ReadUInt64BigEndian(data[entry..]);
                if (BinaryPrimitives.ReadUInt16BigEndian(data[(entry + 12)..]) != 0 || offset > (ulong)data.Length) continue;   // another file, or no place
                BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan((int)(entry - Before(entry))), offset - (ulong)Before((long)offset));
            }
        return result;
    }

    /// <summary>Boxes that carry metadata only: XML, UUID, UUID info, intellectual property rights.</summary>
    private static readonly string[] MetadataBoxes = ["xml ", "uuid", "uinf", "jp2i"];

    /// <summary>
    /// The metadata parts of the boxes from <paramref name="s"/> to <paramref name="e"/> (through the superboxes, at most eight deep, and into
    /// a contiguous codestream), and the length fields of the boxes walked into; false if the structure is damaged.
    /// </summary>
    private static bool MetadataParts(ReadOnlySpan<byte> data, int s, int e, int depth, List<(int Start, int End)> drop,
        List<(int Position, int Size, int Start, int End)> fields)
    {
        for (var at = s; at + 8 <= e;)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            var header = 8;
            (int Position, int Size)? field = (at, 4);
            if (length == 1)
            {
                if (at + 16 > e) return false;
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(at + 8)..]);
                header = 16;
                field = (at + 8, 8);
            }
            else if (length == 0)
            {
                length = e - at;   // to the end: no length to change
                field = null;
            }
            // compared without adding: an XLBox near 2^63 ran the sum over (as FG 1030)
            if (length < header || length > e - at) return false;
            var type = System.Text.Encoding.ASCII.GetString(data.Slice(at + 4, 4));
            int cs = at + header, ce = at + (int)length;
            if (MetadataBoxes.Contains(type)) drop.Add((at, ce));
            else if ((Superboxes.Contains(type) || type == "res ") && depth < 8)
            {
                if (field is { } f) fields.Add((f.Position, f.Size, at, ce));
                if (!MetadataParts(data, cs, ce, depth + 1, drop, fields)) return false;
            }
            else if (type == "jp2c")
            {
                if (field is { } f) fields.Add((f.Position, f.Size, at, ce));
                CodestreamComments(data, cs, ce, drop, fields);
            }
            at = ce;
        }
        return true;
    }

    /// <summary>
    /// The comment segments (COM) of a codestream: those of the main header, and those of the tile-part headers with the Psot of their
    /// tile-part. Where TLM segments list the tile-part lengths (T.800 A.7.1: Ztlm orders the segments, the entries follow the tile-parts in
    /// the order of the codestream), the entry of the tile-part shrinks with it — before the comments of the tile-parts stayed there, and a
    /// name in them went along in spite of „remove metadata“ (probe of 23.11.2026, L45). TLM segments that do not fit the codestream (another
    /// count of entries than of tile-parts, a length that does not divide into entries) leave the tile-parts as they are. The last tile-part
    /// may have Psot 0 (it runs to the end of the codestream, A.4.2): its comments go too, its Psot stays 0.
    /// </summary>
    private static void CodestreamComments(ReadOnlySpan<byte> data, int s, int e, List<(int Start, int End)> drop,
        List<(int Position, int Size, int Start, int End)> fields)
    {
        if (e - s < 4 || BinaryPrimitives.ReadUInt16BigEndian(data[s..]) != 0xFF4F) return;
        var comments = new List<(int, int)>();
        var lengthsListed = false;
        var tlmFits = true;
        var tlm = new List<(int Index, List<(int Position, int Size)> Entries)>();
        var p = s + 2;
        while (p + 4 <= e)
        {
            var marker = BinaryPrimitives.ReadUInt16BigEndian(data[p..]);
            if (marker == 0xFF90) break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(p + 2)..]);
            if (length < 2 || length > e - p - 2) return;
            if (marker == 0xFF64) comments.Add((p, p + 2 + length));
            if (marker == 0xFF55)
            {
                lengthsListed = true;
                // Ltlm, Ztlm, Stlm (ST: Ttlm of 0, 1 or 2 bytes; SP: Ptlm of 2 or 4 bytes), then Ttlm and Ptlm per tile-part
                if (length < 4) { tlmFits = false; p += 2 + length; continue; }
                var st = (data[p + 5] >> 4) & 3;
                var size = (data[p + 5] & 0x40) != 0 ? 4 : 2;
                var step = st + size;
                int index = data[p + 4];
                if (st == 3 || (length - 4) % step != 0 || tlm.Any(t => t.Index == index)) tlmFits = false;
                else
                {
                    var entries = new List<(int, int)>();
                    for (var at = p + 6; at + step <= p + 2 + length; at += step) entries.Add((at + st, size));
                    tlm.Add((index, entries));
                }
            }
            p += 2 + length;
        }
        drop.AddRange(comments);
        var listed = tlm.OrderBy(t => t.Index).SelectMany(t => t.Entries).ToList();
        var parts = new List<(int Sot, int End, bool Last, List<(int, int)> Comments)>();
        var complete = false;
        while (p + 12 <= e && BinaryPrimitives.ReadUInt16BigEndian(data[p..]) == 0xFF90)
        {
            var psot = BinaryPrimitives.ReadUInt32BigEndian(data[(p + 6)..]);
            if (psot > (uint)(e - p)) break;
            var last = psot == 0;
            var partEnd = last ? e : p + (int)psot;
            var q = p + 12;
            var inPart = new List<(int, int)>();
            while (q + 4 <= partEnd)
            {
                var marker = BinaryPrimitives.ReadUInt16BigEndian(data[q..]);
                if (marker == 0xFF93) break;
                int length = BinaryPrimitives.ReadUInt16BigEndian(data[(q + 2)..]);
                if (length < 2 || length > partEnd - q - 2) break;
                if (marker == 0xFF64) inPart.Add((q, q + 2 + length));
                q += 2 + length;
            }
            parts.Add((p, partEnd, last, inPart));
            p = partEnd;
            if (last || p + 2 > e || BinaryPrimitives.ReadUInt16BigEndian(data[p..]) == 0xFFD9) { complete = true; break; }
        }
        if (lengthsListed && !(tlmFits && complete && listed.Count == parts.Count)) return;
        for (var i = 0; i < parts.Count; i++)
        {
            var (sot, end, last, inPart) = parts[i];
            if (inPart.Count == 0) continue;
            drop.AddRange(inPart);
            if (!last) fields.Add((sot + 6, 4, sot, end));
            if (lengthsListed && (last ? ReadLength(data, listed[i]) != 0 : true)) fields.Add((listed[i].Position, listed[i].Size, sot, end));
        }
    }

    /// <summary>The value of a big-endian length field of 2 or 4 bytes.</summary>
    private static long ReadLength(ReadOnlySpan<byte> data, (int Position, int Size) field) =>
        field.Size == 2 ? BinaryPrimitives.ReadUInt16BigEndian(data[field.Position..]) : BinaryPrimitives.ReadUInt32BigEndian(data[field.Position..]);

    /// <summary>Superboxes that may hold a Fragment Table or Cross-Reference box (ISO/IEC 15444-2, M.11): searched for Fragment List boxes.</summary>
    private static readonly string[] Superboxes = ["jp2h", "jpch", "jplh", "cgrp", "ftbl", "asoc", "jclx", "j2cx", "drep", "comp", "grp "];

    /// <summary>The positions of the contents of all complete Fragment List boxes (NF and its entries) in the boxes from <paramref name="s"/> to
    /// <paramref name="e"/>, through the superboxes (at most eight deep) and the Cross-Reference boxes.</summary>
    private static void FragmentLists(ReadOnlySpan<byte> data, int s, int e, int depth, List<int> found)
    {
        for (var at = s; at + 8 <= e && depth < 8;)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            var header = 8;
            if (length == 1)
            {
                if (at + 16 > e) return;
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(at + 8)..]);
                header = 16;
            }
            else if (length == 0) length = e - at;
            if (length < header || length > e - at) return;
            var type = System.Text.Encoding.ASCII.GetString(data.Slice(at + 4, 4));
            int cs = at + header, ce = at + (int)length;
            if (type == "flst" && ce - cs >= 2 && ce - cs >= 2 + 14L * BinaryPrimitives.ReadUInt16BigEndian(data[cs..])) found.Add(cs);
            else if (type == "cref" && ce - cs > 4) FragmentLists(data, cs + 4, ce, depth + 1, found);
            else if (Superboxes.Contains(type)) FragmentLists(data, cs, ce, depth + 1, found);
            at = ce;
        }
    }

    // ------------------------------------------------------------ ICC

    /// <summary>Tags that describe the device or carry free metadata: device maker and model description, the metadata dictionary.</summary>
    private static readonly HashSet<string> IccMetadataTags = ["dmnd", "dmdd", "meta"];

    /// <summary>
    /// An ICC profile without the tags in <see cref="IccMetadataTags"/>, the data of the others packed anew (shared data stays shared), the
    /// profile ID set to 0 (it is a checksum of the old profile); null if the profile has none of these tags or is not readable.
    /// </summary>
    internal static byte[]? Icc(byte[] profile)
    {
        if (profile.Length < 132 || !profile.AsSpan(36, 4).SequenceEqual("acsp"u8)) return null;
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128));
        if (count is < 0 or > 1000 || 132 + count * 12 > profile.Length) return null;
        var tags = new List<(string Signature, int Offset, int Size)>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = profile.AsSpan(132 + i * 12);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            if (offset > profile.Length || size > profile.Length - offset) return null;
            tags.Add((System.Text.Encoding.ASCII.GetString(entry[..4]), (int)offset, (int)size));
        }
        if (!tags.Any(t => IccMetadataTags.Contains(t.Signature))) return null;
        var kept = tags.Where(t => !IccMetadataTags.Contains(t.Signature)).ToList();
        var output = new MemoryStream();
        output.Write(profile.AsSpan(0, 128));
        output.Write(new byte[4 + kept.Count * 12]);
        var placed = new Dictionary<(int, int), int>();   // the same data for several tags is written once
        var table = new List<(string Signature, int Offset, int Size)>();
        foreach (var (signature, offset, size) in kept)
        {
            if (!placed.TryGetValue((offset, size), out var at))
            {
                while (output.Length % 4 != 0) output.WriteByte(0);
                at = (int)output.Length;
                output.Write(profile.AsSpan(offset, size));
                placed[(offset, size)] = at;
            }
            table.Add((signature, at, size));
        }
        while (output.Length % 4 != 0) output.WriteByte(0);
        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        result.AsSpan(84, 16).Clear();   // profile ID
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(128), (uint)table.Count);
        for (var i = 0; i < table.Count; i++)
        {
            var entry = result.AsSpan(132 + i * 12);
            System.Text.Encoding.ASCII.GetBytes(table[i].Signature, entry[..4]);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)table[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)table[i].Size);
        }
        return result;
    }

    // ------------------------------------------------------------ Copyright and licence texts (CleanupItems.Notices)

    // Free texts can carry anything, and without reading them nobody can tell (docs/RECHERCHE-VERSTECKEN.md, B-3). They are overwritten
    // with spaces of the same length: every length, offset and count stays valid, the font and the profile work as before, only the
    // text is gone. Not chosen in advance: many font licences require their notices to stay.

    /// <summary>Profile tags with free text: description, copyright, Apple's localised description.</summary>
    private static readonly HashSet<string> IccNoticeTags = ["desc", "cprt", "dscm"];

    /// <summary>The profile with the texts of <see cref="IccNoticeTags"/> as spaces; null if there is no such text (or it is spaces already).</summary>
    internal static byte[]? IccNotices(byte[] profile)
    {
        if (profile.Length < 132 || !profile.AsSpan(36, 4).SequenceEqual("acsp"u8)) return null;
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128));
        if (count is < 0 or > 1000 || 132 + count * 12 > profile.Length) return null;
        var result = (byte[])profile.Clone();
        var changed = false;
        for (var i = 0; i < count; i++)
        {
            var entry = profile.AsSpan(132 + i * 12);
            if (!IccNoticeTags.Contains(System.Text.Encoding.ASCII.GetString(entry[..4]))) continue;
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            if (offset > profile.Length || size > profile.Length - offset || size < 12) continue;
            var tag = result.AsSpan((int)offset, (int)size);
            switch (System.Text.Encoding.ASCII.GetString(tag[..4]))
            {
                case "text":
                    changed |= BlankAscii(tag[8..]);
                    break;
                case "desc":
                    // ASCII (count with the final 0), Unicode (language, count, UTF-16), ScriptCode (code, count, 67 bytes)
                    var ascii = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(tag[8..]), (uint)(tag.Length - 12));
                    changed |= BlankAscii(tag.Slice(12, ascii));
                    var unicode = 12 + ascii;
                    if (unicode + 8 <= tag.Length)
                    {
                        var characters = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(tag[(unicode + 4)..]), (uint)(tag.Length - unicode - 8) / 2);
                        changed |= BlankUtf16(tag.Slice(unicode + 8, characters * 2));
                        var script = unicode + 8 + characters * 2;
                        if (script + 3 <= tag.Length) changed |= BlankAscii(tag.Slice(script + 3, Math.Min(tag[script + 2], tag.Length - script - 3)));
                    }
                    break;
                case "mluc" when tag.Length >= 16:
                    var records = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(tag[8..]), 1000u);
                    var recordSize = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[12..]);
                    if (recordSize < 12) break;
                    for (var r = 0; r < records && 16 + r * recordSize + 12 <= tag.Length; r++)
                    {
                        var record = tag[(16 + r * recordSize)..];
                        var length = BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
                        var at = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
                        if (at <= tag.Length && length <= tag.Length - at) changed |= BlankUtf16(tag.Slice((int)at, (int)length));
                    }
                    break;
            }
        }
        if (!changed) return null;
        result.AsSpan(84, 16).Clear();   // profile ID: a checksum of the old profile
        return result;
    }

    /// <summary>Names a font program keeps (family, style, full name, PostScript name, typographic family and style, WWS names, variations).</summary>
    private static readonly HashSet<int> KeptNames = [1, 2, 4, 6, 16, 17, 21, 22, 25];

    /// <summary>
    /// The font program with its free texts as spaces — TrueType/OpenType: the name table except <see cref="KeptNames"/> (copyright,
    /// unique ID, version, trademark, maker, designer, description, addresses, licence, sample text …) and Notice/Copyright of a CFF table;
    /// a bare CFF: Notice/Copyright; Type 1: /Notice and /Copyright. Null if there is no such text.
    /// </summary>
    internal static byte[]? FontNotices(byte[] font)
    {
        if (font.Length < 12) return null;
        var result = (byte[])font.Clone();
        var version = BinaryPrimitives.ReadUInt32BigEndian(font);
        bool changed;
        if (version is 0x00010000 or 0x74727565 or 0x4F54544F)   // 1.0, "true", "OTTO"
            changed = SfntNotices(result);
        else if (font[0] == 1 && font[2] >= 4)
            changed = CffNotices(result.AsSpan());
        else if (font[0] == '%' || font[0] == 0x80)
            changed = Type1Notices(result);
        else
            return null;
        return changed ? result : null;
    }

    private static bool SfntNotices(byte[] font)
    {
        int tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        if (12 + tables * 16 > font.Length) return false;
        var changed = false;
        var head = -1;
        for (var i = 0; i < tables; i++)
        {
            var entry = 12 + i * 16;
            var tag = System.Text.Encoding.ASCII.GetString(font, entry, 4);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(entry + 8));
            var length = BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(entry + 12));
            if (offset > font.Length || length > font.Length - offset) continue;
            var table = font.AsSpan((int)offset, (int)length);
            var tableChanged = tag switch
            {
                "name" => NameNotices(table),
                "CFF " => CffNotices(table),
                _ => false,
            };
            if (tag == "head") head = entry;
            if (!tableChanged) continue;
            changed = true;
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(entry + 4), Checksum(table));
        }
        // the font's checksum adjustment in head, over the whole file
        if (changed && head >= 0 && BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(head + 8)) is var headOffset && headOffset + 12 <= font.Length)
        {
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan((int)headOffset + 8), 0);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan((int)headOffset + 8), 0xB1B0AFBA - Checksum(font));
        }
        return changed;
    }

    private static bool NameNotices(Span<byte> table)
    {
        if (table.Length < 6) return false;
        int count = BinaryPrimitives.ReadUInt16BigEndian(table[2..]);
        int strings = BinaryPrimitives.ReadUInt16BigEndian(table[4..]);
        var changed = false;
        for (var i = 0; i < count && 6 + i * 12 + 12 <= table.Length; i++)
        {
            var record = table[(6 + i * 12)..];
            int platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            int name = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            int offset = BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            if (KeptNames.Contains(name) || strings + offset + length > table.Length) continue;
            var text = table.Slice(strings + offset, length);
            changed |= platform is 0 or 3 ? BlankUtf16(text) : BlankAscii(text);
        }
        return changed;
    }

    /// <summary>Notice and Copyright of every top DICT of a CFF font, in its string INDEX.</summary>
    private static bool CffNotices(Span<byte> cff)
    {
        if (cff.Length < 4) return false;
        var names = cff[2];
        if (Index(cff, names) is not { } nameIndex || Index(cff, nameIndex.End) is not { } topIndex || Index(cff, topIndex.End) is not { } stringIndex)
            return false;
        var changed = false;
        for (var i = 0; i < topIndex.Count; i++)
        {
            var (start, end) = topIndex.Item(cff, i);
            foreach (var sid in NoticeSids(cff[start..end]))
            {
                var index = sid - 391;   // the first 391 strings are standard strings
                if (index < 0 || index >= stringIndex.Count) continue;
                var (s, e) = stringIndex.Item(cff, index);
                changed |= BlankAscii(cff[s..e]);
            }
        }
        return changed;
    }

    /// <summary>An INDEX of a CFF font: count, size of the offsets, where its data starts, where it ends.</summary>
    private readonly record struct CffIndex(int Start, int Count, int OffSize, int DataStart, int End)
    {
        public (int Start, int End) Item(ReadOnlySpan<byte> cff, int i) =>
            (DataStart + Offset(cff, Start + 3 + i * OffSize) - 1, DataStart + Offset(cff, Start + 3 + (i + 1) * OffSize) - 1);

        private int Offset(ReadOnlySpan<byte> cff, int at)
        {
            var value = 0;
            for (var k = 0; k < OffSize; k++) value = value << 8 | cff[at + k];
            return value;
        }
    }

    private static CffIndex? Index(ReadOnlySpan<byte> cff, int at)
    {
        if (at + 2 > cff.Length) return null;
        int count = BinaryPrimitives.ReadUInt16BigEndian(cff[at..]);
        if (count == 0) return new CffIndex(at, 0, 1, at + 2, at + 2);
        if (at + 3 > cff.Length) return null;
        int offSize = cff[at + 2];
        if (offSize is < 1 or > 4 || at + 3 + (count + 1) * offSize > cff.Length) return null;
        var index = new CffIndex(at, count, offSize, at + 3 + (count + 1) * offSize - 1 + 1, 0);
        var last = index.Item(cff, count - 1).End;
        if (last > cff.Length) return null;
        for (var i = 0; i < count; i++)
            if (index.Item(cff, i) is var (s, e) && (s < index.DataStart || e < s || e > cff.Length)) return null;
        return index with { End = last };
    }

    /// <summary>The string IDs of Notice (operator 1) and Copyright (12 0) in a top DICT.</summary>
    private static List<int> NoticeSids(ReadOnlySpan<byte> dict)
    {
        var sids = new List<int>();
        var operand = int.MinValue;
        for (var p = 0; p < dict.Length;)
        {
            var b = dict[p];
            if (b <= 21)
            {
                var op = b == 12 && p + 1 < dict.Length ? 1200 + dict[++p] : b;
                p++;
                if (op is 1 or 1200 && operand >= 0) sids.Add(operand);
                operand = int.MinValue;
                continue;
            }
            switch (b)
            {
                case 28 when p + 3 <= dict.Length:
                    operand = (short)(dict[p + 1] << 8 | dict[p + 2]);
                    p += 3;
                    break;
                case 29 when p + 5 <= dict.Length:
                    operand = dict[p + 1] << 24 | dict[p + 2] << 16 | dict[p + 3] << 8 | dict[p + 4];
                    p += 5;
                    break;
                case 30:
                    // a real number: nibbles up to 0xf
                    p++;
                    while (p < dict.Length && (dict[p] & 0x0F) != 0x0F && (dict[p] & 0xF0) != 0xF0) p++;
                    p++;
                    operand = int.MinValue;
                    break;
                case >= 32 and <= 246:
                    operand = b - 139;
                    p++;
                    break;
                case >= 247 and <= 250 when p + 2 <= dict.Length:
                    operand = (b - 247) * 256 + dict[p + 1] + 108;
                    p += 2;
                    break;
                case >= 251 and <= 254 when p + 2 <= dict.Length:
                    operand = -(b - 251) * 256 - dict[p + 1] - 108;
                    p += 2;
                    break;
                default:
                    return sids;   // broken: what was read so far
            }
        }
        return sids;
    }

    /// <summary>/Notice (…) and /Copyright (…) in the clear text of a Type 1 font (before eexec).</summary>
    private static bool Type1Notices(byte[] font)
    {
        var end = font.AsSpan().IndexOf("eexec"u8);
        if (end < 0) end = font.Length;
        var changed = false;
        foreach (var key in new[] { "/Notice"u8.ToArray(), "/Copyright"u8.ToArray() })
            for (var at = font.AsSpan(0, end).IndexOf(key); at >= 0;)
            {
                var p = at + key.Length;
                while (p < end && font[p] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') p++;
                if (p < end && font[p] == (byte)'(')
                {
                    // the string up to its closing parenthesis (nested ones and \-escapes count), everything inside becomes spaces
                    var depth = 1;
                    var q = p + 1;
                    for (; q < end && depth > 0; q++)
                    {
                        if (font[q] == (byte)'\\') q++;
                        else if (font[q] == (byte)'(') depth++;
                        else if (font[q] == (byte)')') depth--;
                    }
                    if (depth == 0) changed |= BlankAscii(font.AsSpan(p + 1, q - 1 - (p + 1)));
                }
                var next = font.AsSpan(at + key.Length, end - at - key.Length).IndexOf(key);
                at = next < 0 ? -1 : at + key.Length + next;
            }
        return changed;
    }

    /// <summary>Single-byte text as spaces (a final 0 stays); true if something changed.</summary>
    private static bool BlankAscii(Span<byte> text)
    {
        var changed = false;
        for (var i = 0; i < text.Length; i++)
            if (text[i] is not (0 or (byte)' '))
            {
                text[i] = (byte)' ';
                changed = true;
            }
        return changed;
    }

    /// <summary>UTF-16 text (big-endian) as spaces; true if something changed.</summary>
    private static bool BlankUtf16(Span<byte> text)
    {
        var changed = false;
        for (var i = 0; i + 1 < text.Length; i += 2)
            if (!(text[i] == 0 && text[i + 1] is 0 or (byte)' '))
            {
                text[i] = 0;
                text[i + 1] = (byte)' ';
                changed = true;
            }
        return changed;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var k = 0; k < 4; k++) word = (word << 8) | (i + k < data.Length ? data[i + k] : (byte)0);
            sum += word;
        }
        return sum;
    }
}
