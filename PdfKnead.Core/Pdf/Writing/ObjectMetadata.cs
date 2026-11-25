using System.Buffers.Binary;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Metadata that objects carry inside their own data (docs/RECHERCHE-VERSTECKEN.md, B-3), removed with „Metadaten“ when sanitising, without
/// changing what the objects show:
/// <list type="bullet">
/// <item>JPEG: EXIF (with a preview of its own — a cropped photo can show whole in it —, GPS, camera, serial number), XMP, Photoshop/IPTC,
/// comments, JFXX previews and the segments of other programs; cut out losslessly. JFIF (without the preview it may carry), the colour
/// profile (cleaned as below) and the Adobe segment stay: they decide the colours.</item>
/// <item>JPEG 2000: the boxes XML, UUID (XMP, GeoJP2, camera data) and UUID info; the colour profile of a colour box cleaned as below.</item>
/// <item>Colour profiles (ICC), as streams, in JPEG and in JPEG 2000: date of creation, device maker, model and creator in the header,
/// descriptions of the device maker and model, profile sequences, the metadata tag, private tags (<see cref="Icc"/>); the profile ID (a
/// checksum) is computed afresh.</item>
/// </list>
/// Copyright and licence notices in fonts and profiles stay: many licences require them, and they tell nothing about the document.
/// </summary>
internal static class ObjectMetadata
{
    /// <summary>What kinds of object metadata a document carries, counted for the window.</summary>
    /// <param name="Jpeg">JPEG images with metadata segments.</param>
    /// <param name="Jpx">JPEG 2000 images with metadata boxes.</param>
    /// <param name="Icc">Colour profiles (streams) with device or origin data to clean (<see cref="ObjectMetadata.Icc"/>).</param>
    /// <param name="FontNotices">Font programs with copyright, licence or other free texts (<see cref="CleanupItems.Notices"/>).</param>
    /// <param name="IccNotices">Colour profiles with description or copyright text (<see cref="CleanupItems.Notices"/>).</param>
    internal readonly record struct Counts(int Jpeg, int Jpx, int Icc, int FontNotices, int IccNotices);

    /// <summary>
    /// At most this many objects are looked at for the window; profile streams larger than the largest profile the display reads are not decoded
    /// (32 MB, as <see cref="IccProfile"/>; before 4 MB, and a larger profile the display used kept its device data — No. 1133).
    /// </summary>
    private const int MaxObjects = 200_000;
    private const int MaxProfile = 32 << 20;

    /// <summary>The stream without the metadata in its data, or null if it has none (or is not such a stream).</summary>
    /// <param name="stream">The stream as it is written.</param>
    /// <param name="doc">The document it comes from.</param>
    /// <param name="items">The chosen items: <see cref="CleanupItems.Metadata"/> and/or <see cref="CleanupItems.Notices"/>.</param>
    /// <param name="number">Its object number in <paramref name="doc"/> (0: unknown) — a profile is also found by its use (<see cref="IsProfile"/>).</param>
    /// <returns>A replacement with the same dictionary (and filter), or null.</returns>
    public static PdfStream? Clean(PdfStream stream, PdfDocument doc, CleanupItems items, int number = 0)
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
                case null or "FlateDecode" or "LZWDecode" or "ASCIIHexDecode" or "ASCII85Decode" when (metadata || notices) && IsProfile(stream, doc, number):
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
                    case null or "FlateDecode" or "LZWDecode" or "ASCIIHexDecode" or "ASCII85Decode" when IsProfile(stream, doc, n):
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

    /// <summary>
    /// An ICC profile stream is not an image or form and has /N (1, 3 or 4) — or, whatever its /N, is named as a profile: by an ICCBased space
    /// or as the /DestOutputProfile of an output intent (<see cref="NamedProfiles"/>). Before, a profile without /N was not looked at, and its
    /// device data stayed (docs/FEHLERGEDAECHTNIS.md, No. 1133).
    /// </summary>
    private static bool IsProfile(PdfStream stream, PdfDocument doc, int number) =>
        stream.Dictionary.GetName(PdfName.Subtype) is null && stream.DataLength <= MaxProfile &&
        (stream.Dictionary.GetInteger(PdfName.N) is 1 or 3 or 4 || number > 0 && Named(doc).Contains(number));

    /// <summary>Per document the numbers of the streams named as profiles, collected once.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfDocument, HashSet<int>> NamedProfiles = new();

    private static HashSet<int> Named(PdfDocument doc) => NamedProfiles.GetValue(doc, CollectNamed);

    /// <summary>
    /// The numbers of the streams that an [/ICCBased n 0 R] names or a /DestOutputProfile refers to, anywhere in the objects of the document —
    /// colour space resources and their bases and alternates (Indexed, Separation, DeviceN), default spaces, group /CS, images, shadings,
    /// output intents: every place keeps the space as an array in an object, so a walk through the objects finds them without decoding any
    /// content stream. At most <see cref="MaxObjects"/> objects, 32 levels deep, 10 000 elements per array.
    /// </summary>
    private static HashSet<int> CollectNamed(PdfDocument doc)
    {
        var found = new HashSet<int>();
        for (var n = 1; n <= Math.Min(doc.ObjectCount, MaxObjects); n++)
        {
            try
            {
                var obj = doc.GetObject(n);
                Walk(obj is PdfStream stream ? stream.Dictionary : obj, found, 0);
            }
            catch (Exception e) when (e is PdfException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
            }
        }
        return found;

        static void Walk(PdfObject? obj, HashSet<int> found, int depth)
        {
            if (depth > 32) return;
            switch (obj)
            {
                case PdfArray array:
                    if (array.Count >= 2 && array[0] is PdfName { Value: "ICCBased" } && array[1] is PdfReference profile) found.Add(profile.Id.Number);
                    for (var i = 0; i < Math.Min(array.Count, 10_000); i++)
                        if (array[i] is PdfArray or PdfDictionary) Walk(array[i], found, depth + 1);
                    break;
                case PdfDictionary dict:
                    foreach (var (key, value) in dict.Entries)
                    {
                        if (key.Value == "DestOutputProfile" && value is PdfReference intent) found.Add(intent.Id.Number);
                        else if (value is PdfArray or PdfDictionary) Walk(value, found, depth + 1);
                    }
                    break;
            }
        }
    }

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
        var profileParts = new List<(int Start, int End, int Number, int Count)>();
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
            else if (marker == 0xE2 && body.Length >= 14) profileParts.Add((at, at + 2 + length, body[12], body[13]));
            at += 2 + length;
        }
        JpegProfile(data, profileParts, drop);
        if (drop.Count == 0) return null;
        drop.Sort((a, b) => a.Start.CompareTo(b.Start));
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

    /// <summary>The most profile bytes an APP2 segment carries: a length field of 65 535 less itself, „ICC_PROFILE\0“, number and count.</summary>
    private const int ProfileChunk = 65535 - 2 - 14;

    /// <summary>
    /// The colour profile of a JPEG cleaned (<see cref="Icc"/>): the segments put together in the order of their numbers (ICC.1, B.4; T.872,
    /// 6.5.1), cleaned, and written in place of the first one as numbered segments again, the others dropped — before, the segments stayed as
    /// they were (No. 1133). Segments whose numbers do not make one profile (a number twice or missing, counts that differ), or whose bytes
    /// together are no profile, give no reader a profile — PDF-Knead's decoder does not read them, the image is drawn in the colour space of the
    /// PDF —, so they can only carry data: they are removed (before, they stayed as they were).
    /// </summary>
    private static void JpegProfile(ReadOnlySpan<byte> data, List<(int Start, int End, int Number, int Count)> parts,
        List<(int Start, int End, byte[] Replacement)> drop)
    {
        if (parts.Count == 0) return;
        if (parts.Any(p => p.Count != parts.Count) || !parts.Select(p => p.Number).Order().SequenceEqual(Enumerable.Range(1, parts.Count)))
        {
            foreach (var (start, end, _, _) in parts) drop.Add((start, end, []));
            return;
        }
        var profile = new List<byte>();
        foreach (var (start, end, _, _) in parts.OrderBy(p => p.Number)) profile.AddRange(data[(start + 18)..end]);
        byte[] whole = [.. profile];
        if (whole.Length < 132 || !whole.AsSpan(36, 4).SequenceEqual("acsp"u8))
        {
            foreach (var (start, end, _, _) in parts) drop.Add((start, end, []));
            return;
        }
        if (Icc(whole) is not { } clean) return;
        var count = (clean.Length + ProfileChunk - 1) / ProfileChunk;
        if (count > 255) return;
        var segments = new List<byte>();
        for (var i = 0; i < count; i++)
        {
            var part = clean.AsSpan(i * ProfileChunk, Math.Min(ProfileChunk, clean.Length - i * ProfileChunk));
            var length = part.Length + 16;
            segments.AddRange([0xFF, 0xE2, (byte)(length >> 8), (byte)length, .. "ICC_PROFILE\0"u8, (byte)(i + 1), (byte)count, .. part]);
        }
        var first = parts.MinBy(p => p.Start);
        foreach (var (start, end, _, _) in parts) drop.Add((start, end, start == first.Start ? [.. segments] : []));
    }

    // ------------------------------------------------------------ JPEG 2000

    /// <summary>
    /// A JP2/JPX file or a bare codestream without its metadata, or null if it has none: the boxes XML, UUID, UUID info and intellectual
    /// property rights at any level of the box structure — the top level and the superboxes —, and the comment segments of a contiguous
    /// codestream (main header and tile-part headers). The lengths of the boxes around a removed part, the Psot of its tile-part and its entry
    /// in TLM shrink with it. Before only the top level was cleaned; rights, comments and metadata inside
    /// superboxes stayed (decided on 23.11.2026, L45 brought forward). The colour profile of a colour box is cleaned (<see cref="Icc"/>), its box
    /// and the boxes around it shrink with it (No. 1133).
    /// </summary>
    internal static byte[]? Jpx(ReadOnlySpan<byte> data)
    {
        var drop = new List<(int Start, int End)>();
        var replace = new List<(int Start, int End, byte[] With)>();
        var fields = new List<(int Position, int Size, int Start, int End)>();
        // a bare codestream (ISO 32000-2, 7.4.9 allows one in a JPXDecode stream): its comment segments all the same — before only a JP2/JPX
        // file was cleaned, and the comments of a bare codestream stayed (23.11.2026)
        var bare = data.Length >= 2 && data[0] == 0xFF && data[1] == 0x4F;
        if (bare) CodestreamComments(data, 0, data.Length, drop, fields);
        else if (data.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(data) != 12 || !data.Slice(4, 4).SequenceEqual("jP  "u8)
                 || !MetadataParts(data, 0, data.Length, 0, drop, replace, fields)) return null;
        if (drop.Count + replace.Count == 0) return null;
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
                replace.RemoveAll(d => offset < (ulong)d.End && end > (ulong)d.Start);
            }
        // removed parts and replaced ones (a cleaned colour profile, No. 1133) together, in order; each shortens the file by what it gives up
        var cuts = drop.Select(d => (d.Start, d.End, With: Array.Empty<byte>())).Concat(replace).OrderBy(d => d.Start).ToList();
        if (cuts.Count == 0) return null;
        static long Removed((int Start, int End, byte[] With) d) => d.End - d.Start - d.With.Length;
        var result = new byte[data.Length - cuts.Sum(Removed)];
        var written = 0;
        var from = 0;
        foreach (var (start, end, with) in cuts)
        {
            data[from..start].CopyTo(result.AsSpan(written));
            written += start - from;
            with.CopyTo(result, written);
            written += with.Length;
            from = end;
        }
        data[from..].CopyTo(result.AsSpan(written));
        long Before(long position) => cuts.Where(d => d.End <= position).Sum(Removed);
        // the length of every box (and the Psot of every tile-part) around a removed part shrinks by what was removed inside it
        foreach (var (position, size, start, end) in fields)
        {
            var inside = cuts.Where(d => d.Start >= start && d.End <= end).Sum(Removed);
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
    /// a contiguous codestream), the colour profiles of colour boxes with their cleaned replacement (method 2 or 3, ISO/IEC 15444-1 I.5.3.3,
    /// 15444-2 M.11.7.2; <see cref="Icc"/>), and the length fields of the boxes walked into; false if the structure is damaged.
    /// </summary>
    private static bool MetadataParts(ReadOnlySpan<byte> data, int s, int e, int depth, List<(int Start, int End)> drop,
        List<(int Start, int End, byte[] With)> replace, List<(int Position, int Size, int Start, int End)> fields)
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
                if (!MetadataParts(data, cs, ce, depth + 1, drop, replace, fields)) return false;
            }
            // the profile of a colour box (method, precedence, approximation, then the profile to the end of the box): cleaned like a profile
            // stream, the box shrinks with it — before, it stayed as it was (No. 1133)
            else if (type == "colr" && ce - cs > 3 + 132 && data[cs] is 2 or 3 && Icc(data[(cs + 3)..ce].ToArray()) is { } clean)
            {
                if (field is { } f) fields.Add((f.Position, f.Size, at, ce));
                replace.Add((cs + 3, ce, clean));
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

    // A profile tells where it comes from in its header and in tags that do not take part in converting colours. Cleaning removes all of it,
    // wherever the profile stands (ICCBased and output intent streams, JPEG APP2 segments, JPEG 2000 colour boxes), and keeps what converts
    // colours: the colours stay exactly the same. Before, only the tags dmnd, dmdd and meta went, and only from profile streams up to 4 MB
    // (docs/FEHLERGEDAECHTNIS.md, No. 1133).

    /// <summary>
    /// Tags removed from every profile: device maker and model description (ICC.1:2022, 9.2.23, 9.2.24), the metadata dictionary (9.2.35), the
    /// profile sequence with the device descriptions of the profiles it was made of (9.2.44, 10.19) and their identifiers (9.2.45, 10.20), the
    /// date of calibration (9.2.13, a date of the device like the date of creation).
    /// </summary>
    private static readonly HashSet<string> IccMetadataTags = ["dmnd", "dmdd", "meta", "pseq", "psid", "calt"];

    /// <summary>
    /// The tag signatures ICC.1:2022 registers (9.2), and of earlier versions those that take part in converting colours: the media black point
    /// (black point compensation of version-2 profiles) and the named colours of version 2. Every other signature is private (7.3.1) — the
    /// localised description of a platform, a video card table, the device settings and PostScript data of version 2 — and goes from profiles of
    /// versions 2 to 4.
    /// </summary>
    private static readonly HashSet<string> IccRegisteredTags =
    [
        "A2B0", "A2B1", "A2B2", "bXYZ", "bTRC", "B2A0", "B2A1", "B2A2", "B2D0", "B2D1", "B2D2", "B2D3", "calt", "targ", "chad", "chrm", "cicp",
        "clro", "clrt", "clot", "ciis", "cprt", "dmnd", "dmdd", "D2B0", "D2B1", "D2B2", "D2B3", "gamt", "kTRC", "gXYZ", "gTRC", "lumi", "meas",
        "meta", "wtpt", "ncl2", "resp", "rig0", "pre0", "pre1", "pre2", "desc", "pseq", "psid", "rXYZ", "rTRC", "rig2", "tech", "vued", "view",
        "bkpt", "ncol",
    ];

    /// <summary>Tag types of ICC.1 (clause 10, and textDescriptionType and namedColorType of version 2): their bytes 4–7 are reserved and zero.</summary>
    private static readonly HashSet<string> IccKnownTypes =
    [
        "chrm", "cicp", "clro", "clrt", "curv", "data", "dtim", "dict", "mft2", "mft1", "mAB ", "mBA ", "meas", "mluc", "mpet", "ncl2", "para",
        "pseq", "psid", "rcs2", "sf32", "sig ", "text", "uf32", "ui16", "ui32", "ui64", "ui08", "view", "XYZ ", "desc", "ncol",
    ];

    /// <summary>
    /// An ICC profile cleaned of where it comes from; null if there is nothing to clean or it is no profile (no „acsp“). In the header the date
    /// of creation becomes the fixed date of the own profiles (a valid date stays, ICC.1 7.2.8), device maker, model and creator become zero
    /// (7.2.12, 7.2.13, 7.2.17), of the device attributes the vendor bits 32–63 go while the bits that describe the medium stay (7.2.14: they
    /// say what the profile was made for — reflective or transparent, glossy or matte —, not which device), the reserved bytes 100–127 become
    /// zero (7.2.19). The tags of <see cref="IccMetadataTags"/> and all private tags go, the data of the others are packed anew (shared data stay
    /// shared), bytes 4–7 of a tag of a known type become zero (10.1). A profile of version 4 or later gets its profile ID computed afresh
    /// (7.2.18, <see cref="IccProfile.ComputedId"/>), one of version 2 zeros there (it has no ID). Of a profile of another layout than ICC.1
    /// (versions from 5 on) only the header fields and the tags of <see cref="IccMetadataTags"/> are cleaned; of a profile whose tag table is
    /// damaged, only the header.
    /// </summary>
    internal static byte[]? Icc(byte[] profile)
    {
        if (profile.Length < 132 || !profile.AsSpan(36, 4).SequenceEqual("acsp"u8)) return null;
        var layout = profile[8] is >= 2 and <= 4;
        var result = IccTags(profile, layout) ?? (byte[])profile.Clone();
        CalibratedProfile.WriteCreated(result);
        result.AsSpan(48, 12).Clear();   // device maker, model, vendor attributes
        result.AsSpan(80, 4).Clear();    // creator
        if (layout) result.AsSpan(100, 28).Clear();
        WriteProfileId(result);
        return result.AsSpan().SequenceEqual(profile) ? null : result;
    }

    /// <summary>The profile ID of a changed profile: computed afresh from version 4 on (ICC.1 7.2.18), zero before (a reserved field there).</summary>
    private static void WriteProfileId(byte[] profile)
    {
        profile.AsSpan(84, 16).Clear();
        if (profile[8] >= 4) IccProfile.ComputedId(profile).CopyTo(profile, 84);
    }

    /// <summary>The tag entries of a profile; null if the table does not fit the data.</summary>
    private static List<(string Signature, int Offset, int Size)>? IccEntries(byte[] profile)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128));
        if (count > 1000 || 132 + count * 12 > profile.Length) return null;
        var tags = new List<(string Signature, int Offset, int Size)>((int)count);
        for (var i = 0; i < count; i++)
        {
            var entry = profile.AsSpan(132 + i * 12);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            if (offset > profile.Length || size > profile.Length - offset) return null;
            tags.Add((System.Text.Encoding.ASCII.GetString(entry[..4]), (int)offset, (int)size));
        }
        return tags;
    }

    /// <summary>A copy of the profile without the tags to remove, the reserved bytes of the kept tags zero; null if the tag table is damaged.</summary>
    private static byte[]? IccTags(byte[] profile, bool layout)
    {
        if (IccEntries(profile) is not { } tags) return null;
        var kept = tags.Where(t => !IccMetadataTags.Contains(t.Signature) && (!layout || IccRegisteredTags.Contains(t.Signature))).ToList();
        byte[] result;
        if (kept.Count == tags.Count) result = (byte[])profile.Clone();
        // tags whose data overlap in part (ICC.1, 7.3.1 forbids it) would be copied once each: up to 1 000 times the profile (a crafted profile of
        // 4 MB gave 4 GB). Where packing anew would make the profile larger than it is, it stays as it is — only the entries of the removed tags
        // go and their bytes are cleared where no kept tag uses them (docs/FEHLERGEDAECHTNIS.md, No. 1122)
        else if (kept.DistinctBy(t => (t.Offset, t.Size)).Sum(t => (long)t.Size + 3) + 132 + 12L * kept.Count > profile.Length)
            result = IccWithoutTagsInPlace(profile, tags, kept);
        else
            result = IccPacked(profile, kept);
        if (IccEntries(result) is { } written) ClearReservedBytes(result, written);
        return result;
    }

    /// <summary>The profile with the header of <paramref name="profile"/> and the kept tags packed anew, each data once, at multiples of four.</summary>
    private static byte[] IccPacked(byte[] profile, List<(string Signature, int Offset, int Size)> kept)
    {
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
        WriteTable(result, table, kept.Count);
        return result;
    }

    /// <summary>
    /// The profile as it is, without the entries of the removed tags; their bytes cleared where no kept tag lies on them (the kept ranges merged
    /// first: one pass over the data, whatever the number of overlapping entries — before, every byte was compared with every kept tag).
    /// </summary>
    private static byte[] IccWithoutTagsInPlace(byte[] profile, List<(string Signature, int Offset, int Size)> tags,
        List<(string Signature, int Offset, int Size)> kept)
    {
        var result = (byte[])profile.Clone();
        var cover = new bool[profile.Length + 1];
        foreach (var (start, end) in Merged(kept.Select(k => (k.Offset, k.Offset + k.Size)))) cover.AsSpan(start, end - start).Fill(true);
        // never the header and the tag table, even where a crafted entry points into them
        var table = 132 + 12 * tags.Count;
        foreach (var (start, end) in Merged(tags.Except(kept).Select(t => (t.Offset, t.Offset + t.Size))))
            for (var at = Math.Max(start, table); at < end; at++)
                if (!cover[at]) result[at] = 0;
        WriteTable(result, kept, tags.Count);
        return result;
    }

    /// <summary>Ranges [start, end) merged where they overlap or touch, in order.</summary>
    private static List<(int Start, int End)> Merged(IEnumerable<(int Start, int End)> ranges)
    {
        var merged = new List<(int Start, int End)>();
        foreach (var (start, end) in ranges.Where(r => r.End > r.Start).OrderBy(r => r.Start))
            if (merged.Count > 0 && start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            else merged.Add((start, end));
        return merged;
    }

    /// <summary>The tag table: count and entries, the room of <paramref name="before"/> entries cleared first.</summary>
    private static void WriteTable(byte[] result, List<(string Signature, int Offset, int Size)> table, int before)
    {
        result.AsSpan(128, 4 + 12 * before).Clear();
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(128), (uint)table.Count);
        for (var i = 0; i < table.Count; i++)
        {
            var entry = result.AsSpan(132 + i * 12);
            System.Text.Encoding.ASCII.GetBytes(table[i].Signature, entry[..4]);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)table[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)table[i].Size);
        }
    }

    /// <summary>
    /// Bytes 4–7 of the tags of a known type as zero (ICC.1, 10.1: reserved) — not where another tag's data, starting elsewhere, lie on them
    /// (overlapping tags of a crafted profile: their data stay as they are).
    /// </summary>
    private static void ClearReservedBytes(byte[] profile, List<(string Signature, int Offset, int Size)> tags)
    {
        var starts = tags.Where(t => t.Size >= 8).Select(t => (t.Offset, t.Size)).Distinct().OrderBy(t => t.Offset).ToList();
        foreach (var (offset, _) in starts)
        {
            if (!IccKnownTypes.Contains(System.Text.Encoding.ASCII.GetString(profile, offset, 4))) continue;
            // another tag starting before and reaching into bytes 4–7, or starting inside them
            if (!starts.Any(t => t.Offset != offset && t.Offset < offset + 8 && t.Offset + t.Size > offset + 4)) profile.AsSpan(offset + 4, 4).Clear();
        }
    }

    // ------------------------------------------------------------ Copyright and licence texts (CleanupItems.Notices)

    // Free texts can carry anything, and without reading them nobody can tell (docs/RECHERCHE-VERSTECKEN.md, B-3). They are overwritten
    // with spaces of the same length: every length, offset and count stays valid, the font and the profile work as before, only the
    // text is gone. Not chosen in advance: many font licences require their notices to stay.

    /// <summary>
    /// Profile tags with free text: description, copyright, the description of the viewing conditions (ICC.1, 9.2.50; before, it stayed —
    /// No. 1133), Apple's localised description.
    /// </summary>
    private static readonly HashSet<string> IccNoticeTags = ["desc", "cprt", "vued", "dscm"];

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
                    // in long: a record size near 2^31 overflowed, the exception dropped the cleaning of the whole profile (No. 1122)
                    for (var r = 0; r < records && 16L + (long)r * recordSize + 12 <= tag.Length; r++)
                    {
                        var record = tag[(int)(16L + (long)r * recordSize)..];
                        var length = BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
                        var at = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
                        if (at <= tag.Length && length <= tag.Length - at) changed |= BlankUtf16(tag.Slice((int)at, (int)length));
                    }
                    break;
            }
        }
        if (!changed) return null;
        WriteProfileId(result);   // a checksum of the profile: computed afresh (version 4), before set to 0 (No. 1133)
        return result;
    }

    /// <summary>
    /// Names a font program keeps (family, style, full name, PostScript name, PostScript CID findfont name, typographic family and style,
    /// WWS names, variations). ID 20 must hold only the characters 33–126 (OpenType name, 30.4), spaces broke it (No. 1143).
    /// </summary>
    private static readonly HashSet<int> KeptNames = [1, 2, 4, 6, 16, 17, 20, 21, 22, 25];

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
        var records = new List<(int Platform, int Name, int Start, int Length)>();
        for (var i = 0; i < count && 6 + i * 12 + 12 <= table.Length; i++)
        {
            var record = table[(6 + i * 12)..];
            int start = strings + BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            if (start + length <= table.Length)
                records.Add((BinaryPrimitives.ReadUInt16BigEndian(record), BinaryPrimitives.ReadUInt16BigEndian(record[6..]), start, length));
        }
        // records may share their text: a kept name stays whole even where a cleaned one names the same bytes — before, a family name
        // stored with the copyright became spaces with it (No. 1143)
        var kept = new bool[table.Length];
        foreach (var r in records.Where(r => KeptNames.Contains(r.Name)))
            Array.Fill(kept, true, r.Start, r.Length);
        var changed = false;
        foreach (var (platform, name, start, length) in records)
        {
            if (KeptNames.Contains(name)) continue;
            var wide = platform is 0 or 3;
            // ID 5 begins with „Version <number>.<number>“ (OpenType name, 30.4): that stays, the rest becomes spaces
            var from = name == 5 ? VersionPrefix(table.Slice(start, length), wide) : 0;
            for (var i = from; i < length; i += wide ? 2 : 1)
            {
                var at = start + i;
                if (kept[at] || wide && (at + 1 >= table.Length || kept[at + 1])) continue;
                if (wide)
                {
                    if (table[at] == 0 && table[at + 1] is 0 or (byte)' ') continue;
                    (table[at], table[at + 1]) = (0, (byte)' ');
                }
                else
                {
                    if (table[at] is 0 or (byte)' ') continue;
                    table[at] = (byte)' ';
                }
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Bytes of „Version“, a space and „number.number“ at the start of a name (0 if it does not begin so).</summary>
    private static int VersionPrefix(ReadOnlySpan<byte> text, bool wide)
    {
        var step = wide ? 2 : 1;
        var p = 0;
        foreach (var c in "Version ")
        {
            if (At(text, p, wide) != c) return 0;
            p += step;
        }
        var digits = 0;
        while (char.IsAsciiDigit(At(text, p, wide))) (p, digits) = (p + step, digits + 1);
        if (digits == 0 || At(text, p, wide) != '.') return 0;
        p += step;
        digits = 0;
        while (char.IsAsciiDigit(At(text, p, wide))) (p, digits) = (p + step, digits + 1);
        return digits == 0 ? 0 : p;
    }

    private static char At(ReadOnlySpan<byte> text, int i, bool wide) =>
        wide ? (i + 1 < text.Length && text[i] == 0 ? (char)text[i + 1] : ' ') : i < text.Length ? (char)text[i] : ' ';

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
