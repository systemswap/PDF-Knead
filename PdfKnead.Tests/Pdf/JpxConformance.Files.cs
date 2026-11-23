using System.Buffers.Binary;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics.Icc;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The file format part of the probe run: the JP2 files of ITU-T T.803, Annex G (decoded, converted to sRGB, the peak error against the
/// reference at most 4, Table G.1), the JPX files of Annex H.3 (float.jpx with half precision channels through the Pixel Format box,
/// multi_img.jpx, reboxing.jpx with fragment tables and cross-references) against their references with the same bound, and the extended
/// codestreams of Annex H.2 (multiple component transformation, non-linearity) component by component. Before 20.11.2026 the JP2 files were
/// decoded but never compared: their references are TIFF images, the run looked for PNM files only.
/// </summary>
public sealed partial class JpxConformance
{
    /// <summary>Peak error a file format reader may show against the sRGB reference (T.803, Table G.1; used for Annex H.3 as well).</summary>
    private const int FilePeak = 4;

    /// <summary>
    /// A JP2 or JPX test file against its reference: the image as PDF-Knead decodes it, in sRGB (an ICC profile of the file through the own
    /// colour management, grey spread to three channels), compared channel by channel with the 8-bit reference (a floating point reference
    /// clamped to 0 to 1 first).
    /// </summary>
    private static string CompareFile(string name, byte[] data, Func<string, string?> reference, List<string> failures)
    {
        var refName = name.StartsWith("file", StringComparison.Ordinal) ? $"jp2_{name[4..]}.tif" : $"{name}.tif";
        var path = reference(refName) ?? reference($"{name}-0.tif");
        var image = JpxDecoder.Decode(data, long.MaxValue);
        var text = $"{image.Width}×{image.Height}×{image.Channels} Farbe {image.Color}{(image.Alpha is null ? "" : " +Deckkraft")}";
        if (path is null)
        {
            failures.Add($"{name}: Referenzbild fehlt");
            return text + " | Referenz fehlt";
        }
        var (w, h, channels, values) = ReadTiff(path);
        if (w != image.Width || h != image.Height)
        {
            failures.Add($"{name}: Größe");
            return text + $" | Referenz {w}×{h}";
        }
        var srgb = ToSrgb(image);
        long peak = 0;
        double sum = 0;
        var count = (long)w * h;
        for (var i = 0; i < count; i++)
        for (var k = 0; k < 3; k++)
        {
            var d = Math.Abs(srgb[i * 3 + k] - values[i * channels + Math.Min(k, channels - 1)]);
            peak = Math.Max(peak, (long)Math.Round(d));
            sum += d * d;
        }
        var ok = peak <= FilePeak;
        if (!ok) failures.Add($"{name}: Spitze {peak} über {FilePeak}");
        return text + $" | sRGB gegen Referenz: Spitze {peak} MSE {sum / (count * 3):0.###}{(ok ? "" : " ZU GROSS")}";
    }

    /// <summary>The decoded image as sRGB, three bytes per pixel.</summary>
    private static byte[] ToSrgb(JpxImage image)
    {
        var count = image.Width * image.Height;
        var n = image.Channels;
        var output = new byte[count * 3];
        var transform = image.Color == JpxColor.Icc && image.IccProfile is { } icc && IccProfile.Parse(icc) is { } profile ? new IccTransform(profile) : null;
        Span<float> device = stackalloc float[Math.Max(1, n)];
        Span<float> rgb = stackalloc float[3];
        for (var i = 0; i < count; i++)
        {
            if (transform is not null)
            {
                // an image in the wide working space from its colours at 16 bits, as the display draws it (docs/FEHLERGEDAECHTNIS.md, No. 1092)
                for (var k = 0; k < n; k++)
                    device[k] = image.Fine is { } fine ? BinaryPrimitives.ReadUInt16BigEndian(fine.AsSpan((i * n + k) * 2)) / 65535f : image.Samples[i * n + k] / 255f;
                transform.ToSrgb(device[..n], rgb);
                for (var k = 0; k < 3; k++) output[i * 3 + k] = (byte)Math.Clamp(Math.Round(rgb[k] * 255), 0, 255);
            }
            else
            {
                for (var k = 0; k < 3; k++) output[i * 3 + k] = image.Samples[i * n + Math.Min(k, n - 1)];
            }
        }
        return output;
    }

    /// <summary>
    /// A TIFF image as the references of T.803 have it: one image, chunky, 8 bits (uncompressed or LZW, with or without the horizontal
    /// predictor) or 32-bit floating point (clamped to 0 to 1); the values on the 8-bit scale.
    /// </summary>
    internal static (int W, int H, int Channels, double[] Values) ReadTiff(string path)
    {
        var d = File.ReadAllBytes(path);
        var little = d[0] == 'I';
        uint U32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at));
        int U16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at));
        var ifd = (int)U32(4);
        var tags = new Dictionary<int, uint[]>();
        for (var i = 0; i < U16(ifd); i++)
        {
            var at = ifd + 2 + 12 * i;
            int tag = U16(at), type = U16(at + 2);
            // only the tags of the image data (others, such as XMP or an ICC profile, are bytes of any length)
            if (tag is not (256 or 257 or 258 or 259 or 273 or 277 or 278 or 279 or 317 or 339) || type is not (3 or 4)) continue;
            var count = (int)U32(at + 4);
            var size = type == 3 ? 2 : 4;
            var start = count * size <= 4 ? at + 8 : (int)U32(at + 8);
            tags[tag] = [.. Enumerable.Range(0, count).Select(k => size == 2 ? (uint)U16(start + 2 * k) : U32(start + 4 * k))];
        }
        int w = (int)tags[256][0], h = (int)tags[257][0], channels = (int)tags[277][0], bits = (int)tags[258][0];
        var compression = tags.TryGetValue(259, out var c) ? (int)c[0] : 1;
        var predictor = tags.TryGetValue(317, out var p) ? (int)p[0] : 1;
        var floating = tags.TryGetValue(339, out var f) && f[0] == 3;
        var rows = tags.TryGetValue(278, out var r) ? (int)r[0] : h;
        var bytesPerPixel = channels * bits / 8;
        var raw = new byte[(long)w * h * bytesPerPixel];
        var written = 0;
        for (var s = 0; s < tags[273].Length; s++)
        {
            var strip = d.AsSpan((int)tags[273][s], (int)tags[279][s]).ToArray();
            var plain = compression == 5 ? Lzw(strip) : strip;
            var rowsHere = Math.Min(rows, h - s * rows);
            var length = Math.Min(plain.Length, rowsHere * w * bytesPerPixel);
            if (predictor == 2 && bits == 8)
                for (var y = 0; y < rowsHere; y++)
                for (var x = bytesPerPixel; x < w * bytesPerPixel; x++)
                {
                    var at = y * w * bytesPerPixel + x;
                    if (at < length) plain[at] = (byte)(plain[at] + plain[at - bytesPerPixel]);
                }
            Array.Copy(plain, 0, raw, written, length);
            written += length;
        }
        var values = new double[(long)w * h * channels];
        for (var i = 0; i < values.Length; i++)
            values[i] = floating ? Math.Clamp(BitConverter.Int32BitsToSingle((int)(little ? BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4))
                    : BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(i * 4)))), 0, 1) * 255
                : bits == 16 ? (little ? BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2)) : BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(i * 2))) / 257.0
                : raw[i];
        return (w, h, channels, values);
    }

    /// <summary>LZW of TIFF 6.0, section 13 (codes of 9 to 12 bits, the earlier switch of the code length).</summary>
    private static byte[] Lzw(byte[] data)
    {
        var output = new List<byte>(data.Length * 3);
        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (var i = 0; i < 256; i++) table.Add([(byte)i]);
            table.Add([]);
            table.Add([]);
        }
        Reset();
        int bitPos = 0, width = 9;
        byte[]? previous = null;
        while (true)
        {
            if ((bitPos + width + 7) / 8 > data.Length) break;
            var code = 0;
            for (var b = 0; b < width; b++, bitPos++) code = code << 1 | (data[bitPos >> 3] >> (7 - (bitPos & 7)) & 1);
            if (code == 257) break;
            if (code == 256)
            {
                Reset();
                width = 9;
                previous = null;
                continue;
            }
            byte[] entry;
            if (code < table.Count) entry = table[code];
            else if (previous is not null) entry = [.. previous, previous[0]];
            else break;
            output.AddRange(entry);
            if (previous is not null) table.Add([.. previous, entry[0]]);
            previous = entry;
            width = table.Count >= 4095 ? 12 : table.Count >= 2047 ? 12 : table.Count >= 1023 ? 11 : table.Count >= 511 ? 10 : 9;
        }
        return [.. output];
    }
}
