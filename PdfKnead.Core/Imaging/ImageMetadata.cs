using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;

namespace PdfKnead.Core.Imaging;

/// <summary>
/// What the header of an image file says (image info): for JPEG the coding (progressive, chroma subsampling,
/// estimated quality), stored resolution (JFIF, EXIF), EXIF data (device, software, date, orientation, GPS), XMP and the name
/// of the colour profile; for JPEG 2000 the colour space and stored resolution. Only header data is read, nothing is decoded. The data
/// is untrusted: every length is checked and texts are limited.
/// </summary>
/// <remarks>Null properties mean that the file does not contain the information.</remarks>
public sealed record ImageMetadata
{
    /// <summary>JPEG: width and height in px according to the frame header (SOF). The PDF file states its own; if they differ, the image info shows both.</summary>
    public (int Width, int Height)? Size { get; init; }

    /// <summary>JPEG: progressive coding.</summary>
    public bool Progressive { get; init; }

    /// <summary>JPEG: arithmetic instead of Huffman coding (rare; some readers cannot decode it).</summary>
    public bool Arithmetic { get; init; }

    /// <summary>JPEG: chroma subsampling ("4:2:0" …); null for a single channel or an unusual combination.</summary>
    public string? Subsampling { get; init; }

    /// <summary>JPEG: quality 1 … 100, estimated from the luminance quantisation table (assuming the scaling of the IJG library).</summary>
    public int? Quality { get; init; }

    /// <summary>Stored resolution (JFIF, EXIF or JPEG 2000) in dpi; null if there is none, only an aspect ratio, or an implausible value.</summary>
    public (double X, double Y)? StoredDpi { get; init; }

    /// <summary>Device manufacturer (EXIF Make, otherwise XMP).</summary>
    public string? Make { get; init; }

    /// <summary>Device model (EXIF Model, otherwise XMP).</summary>
    public string? Model { get; init; }

    /// <summary>Software that wrote the image (EXIF Software, otherwise XMP CreatorTool).</summary>
    public string? Software { get; init; }

    /// <summary>Capture or creation date as stored in the image (no time zone conversion for EXIF).</summary>
    public DateTime? Taken { get; init; }

    /// <summary>EXIF orientation 2 … 8 (1 = normal is not reported).</summary>
    public int? Orientation { get; init; }

    /// <summary>EXIF Artist.</summary>
    public string? Artist { get; init; }

    /// <summary>EXIF Copyright.</summary>
    public string? Copyright { get; init; }

    /// <summary>Location according to GPS in degrees (south and west negative).</summary>
    public (double Latitude, double Longitude)? Gps { get; init; }

    /// <summary>Name of the embedded colour profile.</summary>
    public string? IccProfile { get; init; }

    /// <summary>JPEG 2000: enumerated colour space from the file (values of the standard: 16 sRGB, 17 greyscale, 18 sYCC, 12 CMYK, 14 Lab).</summary>
    public int? JpxColor { get; init; }

    /// <summary>JPEG 2000: what the Reader Requirements box asks for to understand the file completely and PDF-Knead lacks (codes of
    /// ISO/IEC 15444-2, Table M.14, see <c>JpxRequirements.NotUnderstood</c>); null without such a box or when nothing is missing.</summary>
    public int[]? JpxNotUnderstood { get; init; }

    private const int MaxText = 120;
    private const int MaxXmp = 1 << 20;
    private const int MaxIcc = 4 << 20;

    // ------------------------------------------------------------ JPEG

    /// <summary>Reads the header of a JPEG file up to the start of the image data.</summary>
    /// <param name="data">The JPEG data.</param>
    /// <returns>The metadata, or null if the data does not start with a JPEG SOI marker.</returns>
    public static ImageMetadata? FromJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        var result = new ImageMetadata();
        (double, double)? jfif = null;
        byte[]? exif = null, xmp = null;
        var icc = new SortedDictionary<int, byte[]>();
        long iccBytes = 0;
        int[]? luminance = null;
        for (var at = 2; at + 4 <= data.Length;)
        {
            if (data[at] != 0xFF) break;
            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                at += 2;
                continue;
            }
            if (marker is 0xD9 or 0xDA) break;   // end of image or start of the image data
            var length = data[at + 2] << 8 | data[at + 3];
            if (length < 2 || at + 2 + length > data.Length) break;
            var body = data.Slice(at + 4, length - 2);
            switch (marker)
            {
                case 0xE0 when body.StartsWith("JFIF\0"u8) && body.Length >= 12:
                    var unit = body[7];
                    double x = body[8] << 8 | body[9], y = body[10] << 8 | body[11];
                    if (unit is 1 or 2 && x > 0 && y > 0) jfif = unit == 1 ? (x, y) : (x * 2.54, y * 2.54);
                    break;
                case 0xE1 when body.StartsWith("Exif\0\0"u8) && exif is null:
                    exif = body[6..].ToArray();
                    break;
                case 0xE1 when body.StartsWith("http://ns.adobe.com/xap/1.0/\0"u8) && xmp is null && body.Length <= MaxXmp:
                    xmp = body[29..].ToArray();
                    break;
                case 0xE2 when body.StartsWith("ICC_PROFILE\0"u8) && body.Length > 14 && iccBytes + body.Length <= MaxIcc:
                    icc.TryAdd(body[12], body[14..].ToArray());
                    iccBytes += body.Length;
                    break;
                case 0xDB:
                    luminance ??= LuminanceTable(body);
                    break;
                case >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC):
                    result = result with
                    {
                        Progressive = marker is 0xC2 or 0xC6 or 0xCA or 0xCE,
                        Arithmetic = marker >= 0xC9,
                        Subsampling = SubsamplingOf(body),
                        Size = body.Length >= 5 ? (body[3] << 8 | body[4], body[1] << 8 | body[2]) : null,
                    };
                    break;
            }
            at += 2 + length;
        }

        if (luminance is not null) result = result with { Quality = EstimateQuality(luminance) };
        if (exif is not null) result = Exif(result, exif);
        if (jfif is { } dpi && result.StoredDpi is null) result = result with { StoredDpi = dpi };
        result = result with { StoredDpi = Plausible(result.StoredDpi) };
        if (xmp is not null) result = Xmp(result, xmp);
        if (icc.Count > 0) result = result with { IccProfile = IccName(icc.Values.SelectMany(b => b).ToArray()) };
        return result;
    }

    /// <summary>Chroma subsampling from the sampling factors of the first two of three components.</summary>
    private static string? SubsamplingOf(ReadOnlySpan<byte> sof)
    {
        if (sof.Length < 6 || sof[5] != 3 || sof.Length < 6 + 9) return null;
        int h0 = sof[7] >> 4, v0 = sof[7] & 15, h1 = sof[10] >> 4, v1 = sof[10] & 15;
        if (h1 == 0 || v1 == 0) return null;
        return (h0 / h1, v0 / v1) switch
        {
            (1, 1) => "4:4:4",
            (2, 1) => "4:2:2",
            (2, 2) => "4:2:0",
            (4, 1) => "4:1:1",
            (1, 2) => "4:4:0",
            _ => null,
        };
    }

    /// <summary>Quantisation table number 0 (luminance) from a DQT segment.</summary>
    private static int[]? LuminanceTable(ReadOnlySpan<byte> body)
    {
        for (var at = 0; at < body.Length;)
        {
            var precision = body[at] >> 4;
            var id = body[at] & 15;
            var size = precision == 0 ? 64 : 128;
            if (at + 1 + size > body.Length) return null;
            if (id == 0)
            {
                var table = new int[64];
                for (var i = 0; i < 64; i++)
                    table[i] = precision == 0 ? body[at + 1 + i] : body[at + 1 + 2 * i] << 8 | body[at + 2 + 2 * i];
                return table;
            }
            at += 1 + size;
        }
        return null;
    }

    // Sum of the luminance table from Annex K of the JPEG standard (ITU-T T.81); IJG scales it with the quality
    private const int StandardLuminanceSum = 3688;

    /// <summary>Quality as in the IJG library: scaling = 5000 / Q below 50, otherwise 200 − 2 Q (percent of the standard table).</summary>
    private static int? EstimateQuality(int[] table)
    {
        var sum = table.Sum();
        if (sum <= 0) return null;
        if (table.All(q => q == 1)) return 100;
        var scale = sum * 100.0 / StandardLuminanceSum;
        var quality = scale <= 100 ? (200 - scale) / 2 : 5000 / scale;
        return (int)Math.Clamp(Math.Round(quality), 1, 100);
    }

    /// <summary>Keeps the stored resolution only if it makes sense, 1 … 100 000 dpi (crafted files: 10^127 pixels per metre, 0, infinity).</summary>
    private static (double X, double Y)? Plausible((double X, double Y)? dpi) =>
        dpi is { } d && double.IsFinite(d.X) && double.IsFinite(d.Y) && d.X is >= 1 and <= 100_000 && d.Y is >= 1 and <= 100_000 ? d : null;

    // ------------------------------------------------------------ EXIF

    private static ImageMetadata Exif(ImageMetadata result, byte[] tiff)
    {
        if (tiff.Length < 8) return result;
        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I') little = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') little = false;
        else return result;
        var reader = new TiffReader(tiff, little);
        if (reader.U16(2) != 42) return result;
        var tags = new Dictionary<int, (int Type, int Count, int Value)>();
        reader.ReadIfd((int)reader.U32(4), tags, 0);

        string? Text(int tag) => tags.TryGetValue(tag, out var t) ? reader.Ascii(t) : null;
        double? Number(int tag) => tags.TryGetValue(tag, out var t) ? reader.Number(t, 0) : null;

        var unit = Number(0x0128) ?? 2;   // resolution unit: 2 = inch, 3 = centimetre
        (double, double)? dpi = null;
        if (Number(0x011A) is > 0 and var x && Number(0x011B) is > 0 and var y && unit is 2 or 3)
            dpi = unit == 2 ? (x, y) : (x * 2.54, y * 2.54);
        var orientation = (int?)Number(0x0112);
        (double, double)? gps = null;
        if (tags.TryGetValue(0x10002, out var lat) && tags.TryGetValue(0x10004, out var lon))
        {
            double Degrees((int Type, int Count, int Value) t) =>
                (reader.Number(t, 0) ?? 0) + (reader.Number(t, 1) ?? 0) / 60 + (reader.Number(t, 2) ?? 0) / 3600;
            var la = Degrees(lat) * (Text(0x10001) is "S" ? -1 : 1);
            var lo = Degrees(lon) * (Text(0x10003) is "W" ? -1 : 1);
            if (la is >= -90 and <= 90 && lo is >= -180 and <= 180 && (la != 0 || lo != 0)) gps = (la, lo);
        }
        return result with
        {
            Make = Text(0x010F),
            Model = Text(0x0110),
            Software = Text(0x0131),
            Taken = Date(Text(0x9003)) ?? Date(Text(0x0132)),
            Orientation = orientation is >= 2 and <= 8 ? orientation : null,
            Artist = Text(0x013B),
            Copyright = Text(0x8298),
            StoredDpi = dpi,
            Gps = gps,
        };
    }

    /// <summary>Parses an EXIF date "YYYY:MM:DD hh:mm:ss" (also with hyphens).</summary>
    private static DateTime? Date(string? text) =>
        text is { Length: >= 19 } && DateTime.TryParseExact(text[..19].Replace('-', ':'), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    /// <summary>Reads TIFF directories (IFD0 with the EXIF and GPS IFDs in it; GPS tags shifted by 0x10000), at most four directories.</summary>
    private sealed class TiffReader(byte[] data, bool little)
    {
        private const int MaxEntries = 512;
        private readonly HashSet<int> _seen = [];

        public int U16(int at) => at < 0 || at + 2 > data.Length ? -1
            : little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));

        public long U32(int at) => at < 0 || at + 4 > data.Length ? -1
            : little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));

        public void ReadIfd(int offset, Dictionary<int, (int, int, int)> tags, int shift)
        {
            if (offset < 8 || offset >= data.Length || !_seen.Add(offset) || _seen.Count > 4) return;
            var count = U16(offset);
            if (count <= 0) return;
            for (var i = 0; i < Math.Min(count, MaxEntries); i++)
            {
                var entry = offset + 2 + 12 * i;
                if (entry + 12 > data.Length) break;
                var tag = U16(entry);
                var type = U16(entry + 2);
                var n = U32(entry + 4);
                if (n is < 0 or > 1 << 20) continue;
                var size = TypeSize(type) * n;
                // Small values are stored in the entry itself, larger ones at an offset
                var value = size <= 4 ? entry + 8 : (int)Math.Min(U32(entry + 8), int.MaxValue);
                if (value < 0 || value + size > data.Length) continue;
                if (tag == 0x8769 && shift == 0) ReadIfd((int)U32(entry + 8), tags, 0);          // EXIF
                else if (tag == 0x8825 && shift == 0) ReadIfd((int)U32(entry + 8), tags, 0x10000);  // GPS
                else tags.TryAdd(tag + shift, (type, (int)n, value));
            }
        }

        private static int TypeSize(int type) => type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 => 4,
            5 or 10 or 12 => 8,
            _ => 0,
        };

        public string? Ascii((int Type, int Count, int Value) t)
        {
            if (t.Type != 2) return null;
            var span = data.AsSpan(t.Value, t.Count);
            if (span.IndexOf((byte)0) is >= 0 and var end) span = span[..end];
            return Clean(Encoding.Latin1.GetString(span));
        }

        /// <summary>
        /// Returns value number <paramref name="index"/> of an entry: BYTE, SHORT or LONG — the types TIFF 6.0, section 2 asks readers to
        /// accept for unsigned integers (BYTE before not, FG 989) — or RATIONAL.
        /// </summary>
        public double? Number((int Type, int Count, int Value) t, int index)
        {
            if (index >= t.Count) return null;
            switch (t.Type)
            {
                case 1:
                    return t.Value + index < data.Length ? data[t.Value + index] : null;
                case 3:
                    return U16(t.Value + 2 * index);
                case 4:
                    return U32(t.Value + 4 * index);
                case 5:
                    var numerator = U32(t.Value + 8 * index);
                    var denominator = U32(t.Value + 8 * index + 4);
                    return denominator > 0 && numerator >= 0 ? (double)numerator / denominator : null;
                default:
                    return null;
            }
        }
    }

    // ------------------------------------------------------------ XMP

    /// <summary>Adds a few fields from XMP (tool, device, date) to the metadata, without DTD processing, without external resources, size-limited.</summary>
    /// <param name="result">The metadata so far; values already present are kept.</param>
    /// <param name="xml">The XMP packet; only the first 1 MB is read.</param>
    /// <returns>The metadata with the XMP values added.</returns>
    public static ImageMetadata Xmp(ImageMetadata result, byte[] xml)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxXmp * 2L,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(new MemoryStream(xml, 0, Math.Min(xml.Length, MaxXmp)), settings);
            string? element = null;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    element = reader.LocalName;
                    for (var i = 0; i < reader.AttributeCount; i++)
                    {
                        reader.MoveToAttribute(i);
                        Keep(reader.LocalName, reader.Value);
                    }
                    reader.MoveToElement();
                }
                else if (reader.NodeType == XmlNodeType.Text && element is not null)
                {
                    Keep(element, reader.Value);
                }
            }
        }
        catch (Exception e) when (e is XmlException or InvalidOperationException or ArgumentException or IOException)
        {
            // damaged XMP: keep what was read up to that point
        }

        void Keep(string name, string value)
        {
            if (name is "CreatorTool" or "Make" or "Model" or "CreateDate" or "DateTimeOriginal" && Clean(value) is { } text) found.TryAdd(name, text);
        }

        DateTime? date = null;
        foreach (var key in (ReadOnlySpan<string>)["DateTimeOriginal", "CreateDate"])
            if (date is null && found.TryGetValue(key, out var text) &&
                DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                date = parsed;
        return result with
        {
            Make = result.Make ?? found.GetValueOrDefault("Make"),
            Model = result.Model ?? found.GetValueOrDefault("Model"),
            Software = result.Software ?? found.GetValueOrDefault("CreatorTool"),
            Taken = result.Taken ?? date,
        };
    }

    // ------------------------------------------------------------ ICC

    /// <summary>Reads the description of an ICC profile (tag "desc": as text in version 2, as "mluc" in version 4).</summary>
    /// <param name="profile">The ICC profile data.</param>
    /// <param name="language">The language of a multilingual description, as „de“ or „de-CH“; null = the program language.</param>
    /// <returns>The cleaned-up description (see <see cref="Clean"/>), or null if there is none.</returns>
    public static string? IccName(ReadOnlySpan<byte> profile, string? language = null)
    {
        if (profile.Length < 132) return null;
        var count = BinaryPrimitives.ReadUInt32BigEndian(profile[128..]);
        for (var i = 0; i < Math.Min(count, 256u); i++)
        {
            var entry = 132 + 12 * (int)i;
            if (entry + 12 > profile.Length) return null;
            if (!profile.Slice(entry, 4).SequenceEqual("desc"u8)) continue;
            var offset = BinaryPrimitives.ReadUInt32BigEndian(profile[(entry + 4)..]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(profile[(entry + 8)..]);
            if (offset + (long)size > profile.Length || size < 12) return null;
            var tag = profile.Slice((int)offset, (int)size);
            if (tag.StartsWith("desc"u8))
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
                return length > 0 && 12 + length <= tag.Length ? Clean(Encoding.Latin1.GetString(tag.Slice(12, (int)length)).TrimEnd('\0')) : null;
            }
            // only with at least one record: the count comes first (ICC.1 10.15; before, the bytes after the header were read as a record
            // even with none — docs/FEHLERGEDAECHTNIS.md, No. 1126)
            // the record of the program language, else of the same language without the region, else the first (ICC.1 10.15 sentences 15–16;
            // before, always the first — No. 1133)
            if (tag.StartsWith("mluc"u8) && tag.Length >= 28 && BinaryPrimitives.ReadUInt32BigEndian(tag[8..]) > 0)
                return Pdf.Graphics.Icc.CalibratedProfile.MlucRecord(tag, language) is var (start, length) && length > 0
                    ? Clean(Encoding.BigEndianUnicode.GetString(tag.Slice(start, length & ~1)).TrimEnd('\0'))
                    : null;
            return null;
        }
        return null;
    }

    // ------------------------------------------------------------ JPEG 2000

    /// <summary>Reads the header of a JPEG 2000 file (with boxes): colour space, colour profile and stored resolution.</summary>
    /// <param name="data">The JPEG 2000 data.</param>
    /// <returns>The metadata, or null for a raw codestream without the JP2 signature box.</returns>
    public static ImageMetadata? FromJpx(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data.Slice(4, 4).SequenceEqual("jP  "u8)) return null;
        var result = new ImageMetadata();
        Boxes(data, 0, ref result);
        return result with { StoredDpi = Plausible(result.StoredDpi) };
    }

    private static void Boxes(ReadOnlySpan<byte> data, int depth, ref ImageMetadata result)
    {
        for (var at = 0; at + 8 <= data.Length && depth < 3;)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            var type = Encoding.ASCII.GetString(data.Slice(at + 4, 4));
            var header = 8;
            if (length == 1 && at + 16 <= data.Length)
            {
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(at + 8)..]);
                header = 16;
            }
            else if (length == 0) length = data.Length - at;
            // compared without adding: an XLBox near 2^63 made at + length run over, the position jumped back and Slice threw (M2, T.800 I.4, 23.11.2026)
            if (length < header || length > data.Length - at) return;
            var body = data.Slice(at + header, (int)length - header);
            switch (type)
            {
                case "jp2h" or "res ":
                    Boxes(body, depth + 1, ref result);
                    break;
                // the Reader Requirements box at the top level (ISO/IEC 15444-2, M.11.1): the comparison with the Fully Understand Aspects mask
                // (M.6.3; decided on 23.11.2026)
                case "rreq" when depth == 0 && result.JpxNotUnderstood is null:
                    var copy = body.ToArray();
                    if (Pdf.Filters.Jpx.JpxRequirements.NotUnderstood(copy, 0, copy.Length) is { Length: > 0 } open)
                        result = result with { JpxNotUnderstood = open };
                    break;
                // the first colour specification counts (ITU-T T.800, I.5.3.3 sentence 17); before every further enumerated one replaced it
                // (docs/FEHLERGEDAECHTNIS.md, No. 982)
                case "colr" when body.Length >= 3 && result.JpxColor is null && result.IccProfile is null:
                    if (body[0] == 1 && body.Length >= 7)
                        result = result with
                        {
                            JpxColor = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[3..]), int.MaxValue),
                        };
                    else if (body[0] is 2 or 3 && result.IccProfile is null) result = result with { IccProfile = IccName(body[3..]) };
                    break;
                case "resc" or "resd" when body.Length >= 10:
                    // Pixels per metre: numerator, denominator, power of ten for vertical and horizontal; "capture" (resc) wins over "display"
                    var vertical = (body[0] << 8 | body[1]) / (double)Math.Max(1, body[2] << 8 | body[3]) * Math.Pow(10, (sbyte)body[8]);
                    var horizontal = (body[4] << 8 | body[5]) / (double)Math.Max(1, body[6] << 8 | body[7]) * Math.Pow(10, (sbyte)body[9]);
                    if (vertical > 0 && horizontal > 0 && (type == "resc" || result.StoredDpi is null))
                        result = result with { StoredDpi = (horizontal * 0.0254, vertical * 0.0254) };
                    break;
            }
            at += (int)length;
        }
    }

    /// <summary>
    /// Prepares text from untrusted data for display: control characters become spaces; invisible format characters (direction overrides
    /// such as U+202E, which show "gpj.exe" as "exe.jpg", zero-width characters) and broken surrogate pairs are removed; the text is shortened.
    /// </summary>
    /// <param name="text">The text, or null.</param>
    /// <param name="max">Maximum length in UTF-16 code units (a surrogate pair at the limit may make it one longer).</param>
    /// <returns>The cleaned, trimmed text, or null if nothing remains.</returns>
    public static string? Clean(string? text, int max = MaxText)
    {
        if (text is null) return null;
        var builder = new StringBuilder(Math.Min(text.Length, max));
        foreach (var rune in text.EnumerateRunes())
        {
            if (builder.Length >= max) break;
            if (rune == Rune.ReplacementChar || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) continue;
            if (Rune.IsControl(rune)) builder.Append(' ');
            else builder.Append(rune.ToString());
        }
        var cleaned = builder.ToString().Trim();
        return cleaned.Length > 0 ? cleaned : null;
    }
}
