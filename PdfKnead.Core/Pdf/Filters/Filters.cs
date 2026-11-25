using System.IO.Compression;
using System.Runtime.Intrinsics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Filters;

/// <summary>
/// Decodes stream data according to /Filter and /DecodeParms (ISO 32000-2, 7.4). Image formats are decoded elsewhere
/// (DCT and JPX by the renderer, JBIG2 by the image decoder with /JBIG2Globals); the chain stops before them here
/// and <see cref="DecodeResult.ImageFilter"/> names the rest.
/// </summary>
/// <remarks>Stateless and thread-safe. Decoded output is limited to <c>PdfLimits.MaxDecodedStreamBytes</c> against decompression bombs.</remarks>
public static class StreamFilters
{
    /// <summary>Result: the decoded data and, if the chain ends with an image format, its name and parameters.</summary>
    /// <param name="Data">The decoded data (still encoded in the image format if <paramref name="ImageFilter"/> is set).</param>
    /// <param name="ImageFilter">The image filter at which decoding stopped, or null.</param>
    /// <param name="ImageParms">The /DecodeParms of that image filter.</param>
    /// <param name="Truncated">The data was damaged or ended early; <paramref name="Data"/> holds what could be decoded.</param>
    public sealed record DecodeResult(byte[] Data, PdfName? ImageFilter = null, PdfDictionary? ImageParms = null, bool Truncated = false);

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        // Abbreviations from inline images (ISO 32000-2, 8.9.7)
        ["AHx"] = "ASCIIHexDecode",
        ["A85"] = "ASCII85Decode",
        ["LZW"] = "LZWDecode",
        ["Fl"] = "FlateDecode",
        ["RL"] = "RunLengthDecode",
        ["CCF"] = "CCITTFaxDecode",
        ["DCT"] = "DCTDecode",
    };

    /// <summary>The full name of a filter name abbreviated as in inline images (ISO 32000-2, 8.9.7, Table 92), or null.</summary>
    public static string? FullName(string name) => Aliases.GetValueOrDefault(name);

    private static readonly HashSet<string> ImageFilters = ["DCTDecode", "JPXDecode", "JBIG2Decode"];

    /// <summary>Returns the filter names and parameters of a stream dictionary in order.</summary>
    /// <param name="dict">The stream dictionary.</param>
    /// <returns>The chain with abbreviated names expanded; empty if there is no /Filter.</returns>
    public static List<(string Name, PdfDictionary? Parms)> Chain(PdfDictionary dict)
    {
        var result = new List<(string, PdfDictionary?)>();
        var filter = dict.Get(PdfName.Filter);
        // the abbreviation of inline images (8.9.7) in a stream dictionary: /F with a name or an array of names is read as /Filter, as /DP
        // and the abbreviated names are below, as by two reference readers; an /F that is a file specification (a string or dictionary, 7.3.8.2)
        // names no filter (docs/normabgleich/sonderregeln.md S-A A181; docs/FEHLERGEDAECHTNIS.md, No. 1134)
        if (filter is PdfNull && dict.Get(PdfName.Get("F")) is var abbreviated and (PdfName or PdfArray)) filter = abbreviated;
        // an entry whose reference leads nowhere counts as absent (7.3.9; S-A A67, docs/FEHLERGEDAECHTNIS.md, No. 948)
        var parms = dict.Get(PdfName.DecodeParms) is not PdfNull and var full ? full : dict.Get(PdfName.DP);
        switch (filter)
        {
            case PdfName name:
                result.Add((Normalize(name.Value), parms.AsDictionary()));
                break;
            case PdfArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array.Get(i) is not PdfName n) continue;
                    var p = parms is PdfArray pa ? pa.Get(i).AsDictionary() : null;
                    result.Add((Normalize(n.Value), p));
                }
                break;
        }
        return result;
    }

    private static string Normalize(string name) => Aliases.GetValueOrDefault(name, name);

    /// <summary>
    /// Runs data through the filter chain. Damaged data is decoded as far as
    /// possible (<see cref="DecodeResult.Truncated"/>).
    /// </summary>
    /// <param name="data">The encoded (already decrypted) stream data.</param>
    /// <param name="dict">The stream dictionary with /Filter and /DecodeParms.</param>
    /// <param name="sizeHint">Expected size of the decoded data in bytes if known (images); 0 = unknown.</param>
    /// <returns>The decoded data.</returns>
    /// <exception cref="PdfException">The chain contains an unknown filter.</exception>
    /// <exception cref="PdfLimitException">The decoded data would exceed the size limit.</exception>
    public static DecodeResult Decode(byte[] data, PdfDictionary dict, int sizeHint = 0)
    {
        var truncated = false;
        var chain = Chain(dict);
        for (var i = 0; i < chain.Count; i++)
        {
            var (name, parms) = chain[i];
            if (ImageFilters.Contains(name))
            {
                // the data of the image filter as stored: the own JPEG decoder reads /ColorTransform and the number of lines itself (FG 805);
                // only a JPEG written to a file of its own gets them put in (JpegHeader, image export)
                return new DecodeResult(data, PdfName.Get(name), parms, truncated);
            }

            // The hint only applies to the last filter before the raw data
            var hint = i == chain.Count - 1 ? Predictor.InputSize(sizeHint, parms) : 0;
            data = name switch
            {
                "FlateDecode" => Predictor.Apply(Flate.Decode(data, ref truncated, hint), parms, ref truncated),
                // Extension of the PDF Association (2026); always read, written only when explicitly chosen
                "BrotliDecode" => Predictor.Apply(Brotli.Decode(data, ref truncated, hint), parms, ref truncated),
                "LZWDecode" => Predictor.Apply(Lzw.Decode(data, (int)(parms?.GetInteger(PdfName.Get("EarlyChange")) ?? 1), ref truncated), parms, ref truncated),
                "ASCIIHexDecode" => AsciiHex.Decode(data, ref truncated),
                "ASCII85Decode" => Ascii85.Decode(data, ref truncated),
                "RunLengthDecode" => RunLength.Decode(data, ref truncated),
                "CCITTFaxDecode" => CcittFax.Decode(data, parms, ref truncated, hint),
                "Crypt" => data,   // Identity; real crypt filters are handled by decryption beforehand
                _ => throw new PdfException(Tr.T("Unbekannter Filter /{0}", name)),
            };
        }
        return new DecodeResult(data, Truncated: truncated);
    }

    /// <summary>
    /// Decodes into a rented buffer (content streams, which are only needed while the display list is built).
    /// For Flate without a predictor, the usual case, the buffer comes from the array pool; otherwise this works like
    /// <see cref="Decode"/>. Call <see cref="PooledBytes.Dispose"/> after use.
    /// </summary>
    /// <param name="data">The encoded stream data.</param>
    /// <param name="dict">The stream dictionary.</param>
    /// <param name="truncated">Set to true if the data was damaged or ended early.</param>
    /// <returns>The decoded data; image filters at the end of the chain are not decoded.</returns>
    /// <exception cref="PdfException">The chain contains an unknown filter.</exception>
    public static PooledBytes DecodePooled(byte[] data, PdfDictionary dict, out bool truncated)
    {
        truncated = false;
        var chain = Chain(dict);
        if (chain.Count == 1 && chain[0].Name == "FlateDecode" && Predictor.Of(chain[0].Parms) is (1, true))
            return Flate.DecodePooled(data, ref truncated);
        // Brotli content as well: before it went into a new array per stream (docs/FEHLERGEDAECHTNIS.md, No. 936, Nachtrag)
        if (chain.Count == 1 && chain[0].Name == "BrotliDecode" && Predictor.Of(chain[0].Parms) is (1, true))
            return Brotli.DecodePooled(data, ref truncated);
        var result = Decode(data, dict);
        truncated = result.Truncated;
        return new PooledBytes(result.Data, result.Data.Length, pooled: false);
    }

    /// <summary>
    /// Reads a decompressing stream to its end into a rented buffer, doubled while needed within the size limit; damaged data keep what came
    /// before (S-A A09). Shared by Flate and Brotli.
    /// </summary>
    /// <param name="decoder">The decompressing stream.</param>
    /// <param name="estimate">The first size of the buffer.</param>
    /// <param name="truncated">Set where the data are damaged.</param>
    /// <param name="operationIsDamage">Whether an <see cref="InvalidOperationException"/> means damaged data (Brotli reports some damage so).</param>
    /// <returns>The rented buffer with the data.</returns>
    internal static PooledBytes ReadPooled(Stream decoder, int estimate, ref bool truncated, bool operationIsDamage = false)
    {
        // large buffers are allocated and counted, not rented (docs/FEHLERGEDAECHTNIS.md, No. 1003)
        var buffer = DecodedMemory.Rent(Math.Clamp(estimate, 4096, 64 << 20), out var pooled);
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    CheckSize((long)buffer.Length * 2);
                    var bigger = DecodedMemory.Rent((int)Math.Min((long)buffer.Length * 2, Array.MaxLength), out var biggerPooled);
                    buffer.AsSpan(0, length).CopyTo(bigger);
                    DecodedMemory.Return(buffer, pooled);
                    (buffer, pooled) = (bigger, biggerPooled);
                }
                var read = decoder.Read(buffer, length, buffer.Length - length);
                if (read == 0) break;
                length += read;
            }
        }
        catch (Exception e) when (e is InvalidDataException || operationIsDamage && e is InvalidOperationException)
        {
            truncated = true;
        }
        catch
        {
            DecodedMemory.Return(buffer, pooled);   // above a limit: before, the rented buffer was not given back
            throw;
        }
        return new PooledBytes(buffer, length, pooled);
    }

    /// <summary>Throws if the output grows beyond the limit; then it is almost certainly a decompression bomb.</summary>
    internal static void CheckSize(long length)
    {
        if (length > PdfLimits.MaxDecodedStreamBytes)
            throw new PdfLimitException(Tr.T("Ein Stream wird entpackt größer als {0} MB — abgebrochen.", PdfLimits.MaxDecodedStreamBytes >> 20));
    }
}

/// <summary>FlateDecode (zlib/Deflate) via the .NET base class library.</summary>
internal static class Flate
{
    /// <param name="data">The compressed data.</param>
    /// <param name="truncated">Set to true if the data is damaged or ends early.</param>
    /// <param name="sizeHint">Expected size (images: rows × row bytes); if it is right, exactly one array is allocated, without a copy.</param>
    public static byte[] Decode(byte[] data, ref bool truncated, int sizeHint = 0)
    {
        var start = DataStart(data, out var unusual);
        if (unusual) truncated = true;
        if (start < 0) return [];
        var (result, complete) = Inflate(data, start, sizeHint);
        if (!complete || start > 0 && !Checked(data, result)) truncated = true;
        return result;
    }

    /// <summary>
    /// Where the Deflate data of a zlib stream begin: after its two header bytes (RFC 1950, 2.2). -1 where the header is not one RFC 1950
    /// allows — a compression method other than 8, a wrong FCHECK, or a preset dictionary (FDICT), which PDF never supplies (2.3: the
    /// decompressor must then report an error): no data then, and the stream counts as damaged, as RFC 1950 asks and the three reference
    /// readers do; before, raw Deflate was tried from byte 0 and from byte 2, and PDF-Knead showed content no other reader showed
    /// (docs/FEHLERGEDAECHTNIS.md, No. 937). A window above 32 KB (CINFO above 7) is read — one reference reader shows such data, and
    /// PDF-Knead sees at least what the readers see — and counts as damaged. An empty stream holds nothing and no damage.
    /// </summary>
    /// <param name="data">The stream data.</param>
    /// <param name="damaged">Set where the header is not one RFC 1950 allows.</param>
    /// <returns>2, 0 for an empty stream, or -1.</returns>
    internal static int DataStart(ReadOnlySpan<byte> data, out bool damaged)
    {
        damaged = false;
        if (data.IsEmpty) return 0;
        if (data.Length < 2 || (data[0] & 0x0F) != 8 || ((data[0] << 8) | data[1]) % 31 != 0 || (data[1] & 0x20) != 0)
        {
            damaged = true;
            return -1;
        }
        damaged = data[0] >> 4 > 7;
        return 2;
    }

    /// <summary>
    /// Inflates directly into an array that is doubled when needed and only trimmed at the end if
    /// the estimate was wrong. A MemoryStream with growth steps, an intermediate buffer
    /// and a final copy would allocate three to four times the data.
    /// </summary>
    private static (byte[] Data, bool Complete) Inflate(byte[] data, int skip, int sizeHint)
    {
        // Deflate cannot expand by more than a factor of 1032: this limits wrong size hints from damaged files
        var estimate = sizeHint > 0
            ? (int)Math.Min(Math.Min(sizeHint, (long)(data.Length - skip) * 1032 + 1024), 64L << 20)
            : (int)Math.Clamp((long)(data.Length - skip) * 4, 256, 64L << 20);
        // large buffers counted until collected (docs/FEHLERGEDAECHTNIS.md, No. 1003): the old one while it is copied, the one handed out as
        // long as its receiver keeps it
        var buffer = DecodedMemory.Allocate(estimate);
        var length = 0;
        var complete = true;
        try
        {
            using var input = new MemoryStream(data, skip, data.Length - skip, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            while (true)
            {
                if (length == buffer.Length)
                {
                    // Full: check whether more data follows before doubling (an exact estimate means no copying)
                    var probe = deflate.ReadByte();
                    if (probe < 0) break;
                    StreamFilters.CheckSize((long)buffer.Length * 2);
                    buffer = DecodedMemory.Resize(buffer, length, (int)Math.Min((long)buffer.Length * 2, Array.MaxLength));
                    buffer[length++] = (byte)probe;
                }
                var read = deflate.Read(buffer, length, buffer.Length - length);
                if (read == 0) break;
                length += read;
            }
        }
        catch (InvalidDataException)
        {
            // Damaged or truncated: keep what was decoded so far (as common PDF readers do)
            complete = false;
        }
        if (length != buffer.Length) buffer = DecodedMemory.Resize(buffer, length, length);
        return (buffer, complete);
    }

    /// <summary>Like <see cref="Decode"/>, but into rented buffers (ArrayPool); when a buffer becomes too small, a larger one is rented.</summary>
    public static PooledBytes DecodePooled(byte[] data, ref bool truncated)
    {
        var skip = DataStart(data, out var unusual);
        if (unusual) truncated = true;
        if (skip < 0) return new PooledBytes([], 0, pooled: false);
        using var input = new MemoryStream(data, skip, data.Length - skip, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        var result = StreamFilters.ReadPooled(deflate, (int)Math.Clamp((long)(data.Length - skip) * 4, 4096, 64L << 20), ref truncated);
        if (skip > 0 && !Checked(data, result.Span)) truncated = true;
        return result;
    }

    /// <summary>
    /// A Flate stream written anew as RFC 1950 asks, where that loses nothing: the decoder read it to its end, and only its header (a window
    /// above 32 KB, S-A A08d) or its Adler-32 sum (missing — cut at the end, or ending at a flush point, S-A A108 — or wrong, S-A A71) is not as
    /// the RFC asks; the same data packed anew with a valid header and sum (the predictor stays in /DecodeParms, the data are those before it).
    /// Null where the stream is as the RFC asks, where the decoder stopped at damage with bytes behind it (they stay as stored: nothing is lost,
    /// removing them is sanitising, M16), and where it has no valid header (no data at all; Verzeichnis L, L53) — design decision
    /// 19.11.2026 (docs/FEHLERGEDAECHTNIS.md, No. 963).
    /// </summary>
    internal static unsafe byte[]? Repaired(ReadOnlySpan<byte> stream)
    {
        var start = DataStart(stream, out var unusual);
        if (start < 0 || stream.Length - start < 1) return null;
        // every Flate stream of a file passes here when it is saved: read in place and into rented buffers, no copy of the stream
        fixed (byte* p = stream)
        {
            try
            {
                using var input = new UnmanagedMemoryStream(p + start, stream.Length - start);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                var stopped = false;
                using var result = StreamFilters.ReadPooled(deflate, (int)Math.Clamp((long)(stream.Length - start) * 4, 4096, 64L << 20), ref stopped);
                if (stopped) return null;
                Span<byte> sum = stackalloc byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sum, Adler32(result.Span));
                if (!unusual && stream[2..].LastIndexOf(sum) >= 0) return null;
                return Encode(result.Span);
            }
            catch (PdfLimitException)
            {
                return null;   // larger unpacked than the limit (a decompression bomb): stays as stored, as before — saving never fails on it
            }
        }
    }

    /// <summary>
    /// Whether the Adler-32 sum of the unpacked data stands in the stream after its header (RFC 1950, 2.2, 2.3: the decompressor checks it
    /// and reports an error otherwise). It is missing where the data were cut — the Deflate decoder of .NET ends there without an error — and
    /// where it is wrong; the data are shown either way, as the reference readers show them (S-A A09, A71), but the stream counts as damaged:
    /// it is not written anew, and a cross-reference stream is rebuilt from the file (docs/FEHLERGEDAECHTNIS.md, No. 937). Searched in all of
    /// the stream, not only at its end: one Flate stream in six of the collections has bytes after the sum (up to 11 KB). A stream that ends
    /// at a flush point (the empty stored block 00 00 FF FF, then only white space) without the sum counts as whole: its producer flushed all
    /// it had and did not finish the stream — a widely used writer writes incremental updates so, and the data are complete (a cross-reference stream of
    /// a test file file decodes to exactly the entries its /Index names); before this exception such files were rebuilt as repaired.
    /// </summary>
    internal static bool Checked(ReadOnlySpan<byte> stream, ReadOnlySpan<byte> unpacked)
    {
        Span<byte> sum = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sum, Adler32(unpacked));
        if (stream.Length > 2 && stream[2..].LastIndexOf(sum) >= 0) return true;
        var end = stream.TrimEnd(" \t\r\n\f\0"u8);
        return end.Length > 6 && end.EndsWith((ReadOnlySpan<byte>)[0, 0, 0xFF, 0xFF]);
    }

    /// <summary>
    /// Adler-32 (RFC 1950, 8.2), in blocks of 5552 bytes so that the sums cannot overflow before they are reduced. Sixteen bytes at a time
    /// where the processor has vectors: byte by byte the check of every Flate stream (No. 937) made pages with many images measurably slower.
    /// </summary>
    internal static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        while (!data.IsEmpty)
        {
            var block = data[..Math.Min(data.Length, 5552)];
            var rest = block;
            if (Vector128.IsHardwareAccelerated && rest.Length >= 16)
            {
                // per chunk of sixteen bytes x0 … x15: a grows by their sum, b by 16 × a before the chunk plus x0 × 16 + x1 × 15 + … + x15 × 1;
                // the lanes hold partial sums (at most a few hundred million in a block, far from overflowing)
                var chunks = rest.Length / 16;
                Vector128<uint> sums = Vector128<uint>.Zero, before = Vector128<uint>.Zero, weighted = Vector128<uint>.Zero;
                var weightsLow = Vector128.Create((ushort)16, 15, 14, 13, 12, 11, 10, 9);
                var weightsHigh = Vector128.Create((ushort)8, 7, 6, 5, 4, 3, 2, 1);
                for (var c = 0; c < chunks; c++)
                {
                    before += sums;
                    var (low, high) = Vector128.Widen(Vector128.Create(rest.Slice(c * 16, 16)));
                    var (s0, s1) = Vector128.Widen(low + high);
                    sums += s0 + s1;
                    var (w0, w1) = Vector128.Widen(low * weightsLow + high * weightsHigh);
                    weighted += w0 + w1;
                }
                b = (uint)((b + (ulong)chunks * 16 * a + 16UL * Vector128.Sum(before) + Vector128.Sum(weighted)) % Mod);
                a = (uint)((a + (ulong)Vector128.Sum(sums)) % Mod);
                rest = rest[(chunks * 16)..];
            }
            foreach (var x in rest)
            {
                a += x;
                b += a;
            }
            a %= Mod;
            b %= Mod;
            data = data[block.Length..];
        }
        return b << 16 | a;
    }

    /// <summary>Compresses data in zlib format.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.SmallestSize)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, level, leaveOpen: true)) z.Write(data);
        return output.ToArray();
    }
}

/// <summary>LZWDecode (ISO 32000-2, 7.4.4): codes of 9–12 bits, table reset with 256, end with 257.</summary>
internal static class Lzw
{
    public static byte[] Decode(byte[] data, int earlyChange, ref bool truncated)
    {
        // counted while the output grows (docs/FEHLERGEDAECHTNIS.md, No. 1003)
        var output = new DecodedWriter(data.Length * 3, PdfLimits.MaxDecodedStreamBytes);
        var prefix = new int[4096];
        var suffix = new byte[4096];
        var length = new int[4096];
        for (var i = 0; i < 256; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            length[i] = 1;
        }
        var next = 258;
        var codeLength = 9;
        var previous = -1;
        var stack = new byte[4096];

        long bitBuffer = 0;
        var bitCount = 0;
        var pos = 0;
        while (true)
        {
            while (bitCount < codeLength)
            {
                if (pos >= data.Length)
                {
                    // End of data without an end code (257): counts as complete, because this is common
                    return output.ToArray();
                }
                bitBuffer = (bitBuffer << 8) | data[pos++];
                bitCount += 8;
            }
            var code = (int)((bitBuffer >> (bitCount - codeLength)) & ((1 << codeLength) - 1));
            bitCount -= codeLength;

            if (code == 256)
            {
                next = 258;
                codeLength = 9;
                previous = -1;
                continue;
            }
            if (code == 257) break;

            int entry;
            if (code < next && (code < 256 || code >= 258))
            {
                entry = code;
            }
            else if (code == next && previous >= 0)
            {
                // KwKwK case: entry = previous + its first character
                entry = -1;
            }
            else
            {
                truncated = true;   // invalid code
                break;
            }

            byte first;
            if (entry >= 0)
            {
                first = WriteEntry(entry, prefix, suffix, length, stack, output);
            }
            else
            {
                first = WriteEntry(previous, prefix, suffix, length, stack, output);
                output.WriteByte(first);
            }

            if (previous >= 0 && next < 4096)
            {
                prefix[next] = previous;
                suffix[next] = first;
                length[next] = length[previous] + 1;
                next++;
            }
            previous = code;
            StreamFilters.CheckSize(output.Length);

            if (next + earlyChange >= (1 << codeLength) && codeLength < 12) codeLength++;
        }
        return output.ToArray();
    }

    private static byte WriteEntry(int code, int[] prefix, byte[] suffix, int[] length, byte[] stack, DecodedWriter output)
    {
        var n = length[code];
        for (var i = n - 1; i >= 0; i--)
        {
            stack[i] = suffix[code];
            code = prefix[code];
        }
        output.Write(stack, 0, n);
        return stack[0];
    }
}

/// <summary>
/// Whether a byte is white space (ISO 32000-2, 7.2.3, Table 1): the filters of 7.4.2 and 7.4.3 ignore it; any other character outside
/// their alphabet is an error — read past as before, but reported as damage (docs/FEHLERGEDAECHTNIS.md, No. 797).
/// </summary>
internal static class FilterWhiteSpace
{
    public static bool Is(byte c) => c is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;
}

/// <summary>ASCIIHexDecode (ISO 32000-2, 7.4.2): a character other than a hex digit or white space is damage (No. 797).</summary>
internal static class AsciiHex
{
    public static byte[] Decode(byte[] data, ref bool truncated)
    {
        var output = new List<byte>(data.Length / 2);
        var high = -1;
        foreach (var c in data)
        {
            if (c == '>') break;
            var v = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                _ => -1,
            };
            if (v < 0)
            {
                if (!FilterWhiteSpace.Is(c)) truncated = true;
                continue;
            }
            if (high < 0) high = v;
            else
            {
                output.Add((byte)(high * 16 + v));
                high = -1;
            }
        }
        if (high >= 0) output.Add((byte)(high * 16));
        return [.. output];
    }
}

/// <summary>ASCII85Decode (ISO 32000-2, 7.4.3).</summary>
internal static class Ascii85
{
    /// <summary>
    /// Decodes; damage (No. 797) is a character outside „!“ to „u“ that is neither white space, „z“ at the start of a group nor the end
    /// „~&gt;“, a „~“ not followed by „&gt;“, a „z“ inside a group, a last group of one character, or a group above 2³² − 1 (7.4.3).
    /// A character outside the alphabet ends the data there, the whole groups before it stay — as the three reference readers have it; a
    /// leading „&lt;~“ is no exception (it reads as a digit and a „~“ without „&gt;“: nothing). Before, both were read past
    /// (docs/FEHLERGEDAECHTNIS.md, No. 962; S-A A70).
    /// </summary>
    public static byte[] Decode(byte[] data, ref bool truncated)
    {
        var output = new List<byte>(data.Length * 4 / 5 + 4);
        Span<int> group = stackalloc int[5];
        var count = 0;
        for (var i = 0; i < data.Length; i++)
        {
            var c = data[i];
            if (c == '~')
            {
                // the end is the two characters „~>“ (7.4.3; white space between is ignored as anywhere in the data): a „~“ followed by
                // anything else is damage — decoding stops there all the same (docs/FEHLERGEDAECHTNIS.md, No. 899)
                var next = i + 1;
                while (next < data.Length && FilterWhiteSpace.Is(data[next])) next++;
                if (next >= data.Length || data[next] != '>') truncated = true;
                break;
            }
            if (c == 'z')
            {
                if (count == 0) output.AddRange([0, 0, 0, 0]);
                else truncated = true;   // „z“ only stands for a whole group
                continue;
            }
            if (c is < (byte)'!' or > (byte)'u')
            {
                if (FilterWhiteSpace.Is(c)) continue;   // white space is skipped
                // anything else is damage and ends the data: the group begun before it is not completed
                truncated = true;
                return [.. output];
            }
            group[count++] = c - '!';
            if (count == 5)
            {
                if (!Emit(group, 4, output)) truncated = true;
                count = 0;
            }
        }
        if (count == 1) truncated = true;   // a last group of one character is not valid
        if (count > 1)
        {
            for (var i = count; i < 5; i++) group[i] = 'u' - '!';
            if (!Emit(group, count - 1, output)) truncated = true;
        }
        return [.. output];
    }

    /// <returns>False if the group stands for more than 2³² − 1 (then its low 32 bits are taken, as before).</returns>
    private static bool Emit(Span<int> group, int bytes, List<byte> output)
    {
        ulong value = 0;
        foreach (var g in group) value = value * 85 + (uint)g;
        for (var i = 0; i < bytes; i++) output.Add((byte)(value >> (24 - 8 * i)));
        return value <= uint.MaxValue;
    }
}

/// <summary>RunLengthDecode (ISO 32000-2, 7.4.5).</summary>
internal static class RunLength
{
    /// <summary>Decodes; a run that reaches beyond the data is damage (No. 797); the end without the code 128 is common and no damage.</summary>
    public static byte[] Decode(byte[] data, ref bool truncated)
    {
        // counted while the output grows (docs/FEHLERGEDAECHTNIS.md, No. 1003)
        var output = new DecodedWriter(data.Length * 2, PdfLimits.MaxDecodedStreamBytes);
        var i = 0;
        while (i < data.Length)
        {
            int n = data[i++];
            if (n == 128) break;
            if (n < 128)
            {
                var count = Math.Min(n + 1, data.Length - i);
                if (count < n + 1) truncated = true;
                output.Write(data, i, count);
                i += count;
            }
            else if (i < data.Length)
            {
                var b = data[i++];
                for (var k = 0; k < 257 - n; k++) output.WriteByte(b);
            }
            else truncated = true;   // a repeat without its byte
            StreamFilters.CheckSize(output.Length);
        }
        return output.ToArray();
    }
}

/// <summary>Predictors for Flate and LZW (ISO 32000-2, 7.4.4.4): TIFF 2 and PNG 10–15.</summary>
internal static class Predictor
{
    /// <summary>Size before the predictor for the expected size after it (PNG: one type byte per row).</summary>
    public static int InputSize(int outputSize, PdfDictionary? parms)
    {
        if (outputSize <= 0) return 0;
        if (Of(parms).Kind != 10 || Parameters(parms) is not { Valid: true } p) return outputSize;
        var (colors, bits, columns) = (p.Colors, p.Bits, p.Columns);
        var rowLength = (int)(((long)colors * bits * columns + 7) / 8);
        var rows = outputSize / Math.Max(1, rowLength);
        return (int)Math.Min(int.MaxValue / 2, (long)rows * (rowLength + 1));
    }

    /// <summary>
    /// The predictor /DecodeParms names, as ISO 32000-2, 7.4.4.4, Table 8 assigns the values: 1 none, 2 TIFF, 10 to 15 PNG (each row names
    /// its own filter, so all six read alike — returned as 10). Any other value (0, 3 to 9, above 15) is not assigned: the data are read
    /// as they are, as the reference readers read them, and the stream counts as damaged. One rule for every reader of the data — before,
    /// the whole stream read 3 to 9 as PNG, the image rows of the display as none (docs/FEHLERGEDAECHTNIS.md, No. 938).
    /// </summary>
    /// <param name="parms">The decode parameters of the filter.</param>
    /// <returns>The kind (1, 2 or 10) and whether the value is one Table 8 assigns.</returns>
    public static (int Kind, bool Valid) Of(PdfDictionary? parms) =>
        (parms?.GetInteger(PdfName.Get("Predictor")) ?? 1) switch
        {
            1 => (1, true),
            2 => (2, true),
            >= 10 and <= 15 => (10, true),
            _ => (1, false),
        };

    /// <summary>The largest /Colors read (a bound against malicious files: the pixel of a predictor holds colors × bits bits).</summary>
    internal const int MaxColors = 4096;

    /// <summary>
    /// /Colors, /BitsPerComponent and /Columns of a predictor, as ISO 32000-2, 7.4.4.3, Table 8 gives them: Colors 1 or greater (since PDF 1.3;
    /// before, 1 to 4), default 1; BitsPerComponent 1, 2, 4, 8 or 16, default 8; Columns 1 or greater, default 1. Valid only within those
    /// ranges (and Colors up to <see cref="MaxColors"/>, Columns up to 2^24). Before, the values were clamped without a note: Colors to 32
    /// — a valid stream of 33 or more colours decoded wrongly — and BitsPerComponent to 1–16, so 3 or 12 passed (docs/FEHLERGEDAECHTNIS.md,
    /// No. 1007; the reference readers: only one decodes 33 colours, none reads 3 or 12 bits, and they disagree on Colors 0 — S-A A117).
    /// </summary>
    internal static (int Colors, int Bits, int Columns, bool Valid) Parameters(PdfDictionary? parms)
    {
        var colors = parms?.GetInteger(PdfName.Get("Colors")) ?? 1;
        var bits = parms?.GetInteger(PdfName.Get("BitsPerComponent")) ?? 8;
        var columns = parms?.GetInteger(PdfName.Get("Columns")) ?? 1;
        var valid = colors is >= 1 and <= MaxColors && bits is 1 or 2 or 4 or 8 or 16 && columns is >= 1 and <= 1 << 24;
        return valid ? ((int)colors, (int)bits, (int)columns, true) : (1, 8, 1, false);
    }

    public static byte[] Apply(byte[] data, PdfDictionary? parms, ref bool truncated)
    {
        var (predictor, valid) = Of(parms);
        if (!valid) truncated = true;
        if (predictor == 1) return data;
        // a value of Table 8 out of its range: the data as they are, damaged — the rule of /Predictor (No. 938) for the other entries too
        if (Parameters(parms) is not { Valid: true } p)
        {
            truncated = true;
            return data;
        }

        var (colors, bits, columns) = (p.Colors, p.Bits, p.Columns);
        var bytesPerPixel = Math.Max(1, (colors * bits + 7) / 8);
        var rowLength = (int)(((long)colors * bits * columns + 7) / 8);

        return predictor == 2 ? Tiff(data, rowLength, colors, bits, columns) : Png(data, rowLength, bytesPerPixel, ref truncated);
    }

    private static byte[] Png(byte[] data, int rowLength, int bpp, ref bool truncated)
    {
        var rows = data.Length / (rowLength + 1);
        // Not a single whole row: allocate nothing (otherwise /Columns from the file could request row buffers of up to 1 GB)
        if (rows == 0) return [];
        var output = DecodedMemory.Allocate(rows * rowLength);   // counted (No. 1003); every byte is written below
        var previous = new byte[rowLength];
        for (var r = 0; r < rows; r++)
        {
            var src = r * (rowLength + 1);
            var row = output.AsSpan(r * rowLength, rowLength);
            data.AsSpan(src + 1, rowLength).CopyTo(row);
            if (!PngRow(data[src], row, previous, bpp)) truncated = true;
            row.CopyTo(previous);
        }
        return output;
    }

    /// <summary>
    /// Reverses the filter of one PNG-filtered row (also used for reading large images row by row). Table 9 (ISO 32000-2, 7.4.4.4) and RFC 2083
    /// know the tags 0 to 4; another tag leaves the row as it is, as two of the reference readers read it, and is reported as damage (before,
    /// read as None without a note — the rule of No. 938 and 1007 for every value the tables do not assign; docs/FEHLERGEDAECHTNIS.md,
    /// No. 1009, S-A A118).
    /// </summary>
    /// <returns>Whether the tag is one of the five.</returns>
    public static bool PngRow(int type, Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
    {
        var rowLength = row.Length;
        switch (type)
        {
            case 1:   // Sub
                for (var i = bpp; i < rowLength; i++) row[i] += row[i - bpp];
                break;
            case 2:   // Up
                for (var i = 0; i < rowLength; i++) row[i] += previous[i];
                break;
            case 3:   // Average
                for (var i = 0; i < rowLength; i++)
                    row[i] += (byte)(((i >= bpp ? row[i - bpp] : 0) + previous[i]) / 2);
                break;
            case 4:   // Paeth
                for (var i = 0; i < rowLength; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                break;
        }
        return type is >= 0 and <= 4;
    }

    /// <summary>
    /// TIFF predictor 2: every component is the difference to the same component of the pixel to its left, modulo 2^bits — at every depth
    /// (ISO 32000-2, 7.4.4.4). With 1, 2 and 4 bits the rows were left as they were, and such an image showed stripes and noise
    /// (docs/FEHLERGEDAECHTNIS.md, No. 456, Analyse E-24).
    /// </summary>
    private static byte[] Tiff(byte[] data, int rowLength, int colors, int bits, int columns)
    {
        var output = (byte[])data.Clone();
        if (bits == 8)
        {
            for (var start = 0; start + rowLength <= output.Length; start += rowLength)
                for (var i = colors; i < rowLength; i++)
                    output[start + i] += output[start + i - colors];
        }
        else if (bits == 16)
        {
            for (var start = 0; start + rowLength <= output.Length; start += rowLength)
                for (var i = 2 * colors; i + 1 < rowLength; i += 2)
                {
                    var v = ((output[start + i] << 8) | output[start + i + 1]) + ((output[start + i - 2 * colors] << 8) | output[start + i - 2 * colors + 1]);
                    output[start + i] = (byte)(v >> 8);
                    output[start + i + 1] = (byte)v;
                }
        }
        else if (bits is 1 or 2 or 4)
        {
            var mask = (1 << bits) - 1;
            var samples = (long)colors * columns;
            for (var start = 0; start + rowLength <= output.Length; start += rowLength)
                for (var s = (long)colors; s < samples; s++)
                {
                    var at = s * bits;
                    var shift = 8 - bits - (int)(at & 7);
                    var index = start + (int)(at >> 3);
                    var left = (s - colors) * bits;
                    var sum = ((output[index] >> shift) + (output[start + (int)(left >> 3)] >> (8 - bits - (int)(left & 7)))) & mask;
                    output[index] = (byte)(output[index] & ~(mask << shift) | sum << shift);
                }
        }
        return output;
    }
}

/// <summary>Decoded data in a buffer that may be rented from the array pool; only the first <see cref="Length"/> bytes are valid.</summary>
/// <param name="array">The buffer.</param>
/// <param name="length">Number of valid bytes.</param>
/// <param name="pooled">Whether the buffer is rented from <see cref="System.Buffers.ArrayPool{T}.Shared"/> and must be returned.</param>
public readonly struct PooledBytes(byte[] array, int length, bool pooled) : IDisposable
{
    /// <summary>The buffer; it may be longer than <see cref="Length"/>.</summary>
    public byte[] Array { get; } = array;

    /// <summary>Number of valid bytes.</summary>
    public int Length { get; } = length;

    /// <summary>The valid bytes.</summary>
    public ReadOnlySpan<byte> Span => Array.AsSpan(0, Length);

    /// <summary>Returns the buffer to the pool (call only once; do not read the data afterwards).</summary>
    public void Dispose()
    {
        if (pooled) System.Buffers.ArrayPool<byte>.Shared.Return(Array);
    }
}

/// <summary>
/// Large decoded buffers, counted across the process against <see cref="PdfLimits.MaxDecodedBytesHeld"/> from their allocation until the
/// collector has taken them (docs/FEHLERGEDAECHTNIS.md, No. 1003). Before, only each stream was bounded: the decoded content of nested forms
/// (a reader of forms holds every level while it reads the next), of page parts put together, of the buffers a decoder doubles and of several
/// documents at once added up without a bound, and large buffers went back into the shared array pool, which kept them after the page — a file
/// of 1 MB with one stream at the limit took up to 8.5 GB while drawn, printed and saved, and a process limited to 3.5 GB ran out of memory.
/// Counted until collected, not until a holder lets go: that is the memory they take. Where the count would pass the bound, one compacting
/// collection finds what is no longer held (and closes the gaps between large buffers); what is still held then refuses the new one. Where the
/// runtime finds no room although the count allows it (other memory of the program, gaps), the same: one compacting collection, then the
/// refusal instead of running out of memory.
/// </summary>
internal static class DecodedMemory
{
    private static readonly Lock Gate = new();
    private static readonly List<(WeakReference<byte[]> Buffer, long Bytes)> Tracked = [];
    private static long _counted;
    private static int _added;

    /// <summary>From this size on, a buffer is counted, and allocated on its own instead of rented: the pool would keep it after use.</summary>
    internal static long Threshold => Math.Min(16L << 20, PdfLimits.MaxDecodedBytesHeld / 8);

    /// <summary>Bytes in large decoded buffers not yet found collected.</summary>
    internal static long Held
    {
        get
        {
            lock (Gate)
            {
                Prune();
                return _counted;
            }
        }
    }

    /// <summary>A new array of <paramref name="size"/> bytes for decoded data; from <see cref="Threshold"/> on counted (contents undefined).</summary>
    /// <exception cref="PdfLimitException">Even after a collection, more would be held than <see cref="PdfLimits.MaxDecodedBytesHeld"/>, or the
    /// runtime has no room for it.</exception>
    internal static byte[] Allocate(int size)
    {
        if (size < Threshold) return new byte[size];
        var limit = PdfLimits.MaxDecodedBytesHeld;
        for (var collected = false; ; collected = true)
        {
            lock (Gate)
            {
                if (_counted + size > limit) Prune();
                if (_counted + size <= limit)
                {
                    try
                    {
                        var buffer = GC.AllocateUninitializedArray<byte>(size);
                        Tracked.Add((new WeakReference<byte[]>(buffer), size));
                        _counted += size;
                        if (++_added >= 64) Prune();
                        return buffer;
                    }
                    catch (OutOfMemoryException) when (!collected)
                    {
                        // no room, although the count allows it: collected below, then once more
                    }
                    catch (OutOfMemoryException)
                    {
                        throw new PdfLimitException(Tr.T("Zu viele entpackte Daten auf einmal (über {0} MB) — abgebrochen.", limit >> 20));
                    }
                }
            }
            if (collected) throw new PdfLimitException(Tr.T("Zu viele entpackte Daten auf einmal (über {0} MB) — abgebrochen.", limit >> 20));
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }

    /// <summary>The first <paramref name="length"/> bytes of <paramref name="buffer"/> in a new array of <paramref name="size"/> bytes.</summary>
    internal static byte[] Resize(byte[] buffer, int length, int size)
    {
        var bigger = Allocate(size);
        buffer.AsSpan(0, length).CopyTo(bigger);
        return bigger;
    }

    /// <summary>A buffer of at least <paramref name="size"/> bytes: rented below <see cref="Threshold"/>, else allocated and counted.</summary>
    internal static byte[] Rent(int size, out bool pooled)
    {
        pooled = size < Threshold;
        return pooled ? System.Buffers.ArrayPool<byte>.Shared.Rent(size) : Allocate(size);
    }

    /// <summary>Gives a buffer from <see cref="Rent"/> back to the pool where it came from there.</summary>
    internal static void Return(byte[] buffer, bool pooled)
    {
        if (pooled) System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
    }

    private static void Prune()
    {
        _added = 0;
        Tracked.RemoveAll(entry =>
        {
            if (entry.Buffer.TryGetTarget(out _)) return false;
            _counted -= entry.Bytes;
            return true;
        });
    }
}

/// <summary>Decoded bytes written one after another into a buffer that grows; its large steps are counted (<see cref="DecodedMemory"/>).</summary>
/// <param name="capacity">The first size (at most 64 MB).</param>
/// <param name="bound">The most the writer has to hold before its user refuses (a limit checked after each write): the buffer grows by doubling,
/// but not beyond this and one byte — a page content bounded to 743 MB doubled from 512 MB to 1 GB before.</param>
internal sealed class DecodedWriter(long capacity, long bound)
{
    private byte[] _buffer = DecodedMemory.Allocate((int)Math.Clamp(capacity, 256, 64L << 20));

    /// <summary>Number of bytes written.</summary>
    public int Length { get; private set; }

    /// <summary>Writes bytes.</summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(Length));
        Length += bytes.Length;
    }

    /// <summary>Writes <paramref name="count"/> bytes of <paramref name="data"/> from <paramref name="offset"/>.</summary>
    public void Write(byte[] data, int offset, int count) => Write(data.AsSpan(offset, count));

    /// <summary>Writes one byte.</summary>
    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[Length++] = value;
    }

    private void Ensure(int more)
    {
        if ((long)Length + more <= _buffer.Length) return;
        var size = Math.Min(Math.Max(Math.Min((long)_buffer.Length * 2, bound + 1), (long)Length + more), Array.MaxLength);
        if (size < (long)Length + more) throw new PdfLimitException(Tr.T("Ein Stream wird entpackt größer als {0} MB — abgebrochen.", PdfLimits.MaxDecodedStreamBytes >> 20));
        _buffer = DecodedMemory.Resize(_buffer, Length, (int)size);
    }

    /// <summary>The bytes written, in an array of their length.</summary>
    public byte[] ToArray() => Length == _buffer.Length ? _buffer : DecodedMemory.Resize(_buffer, Length, Length);
}

/// <summary>
/// BrotliDecode, an extension of the PDF Association (August 2026), via the .NET base class library. Like <see cref="Flate"/>: damaged
/// data is decoded as far as possible, and the size is limited (decompression bombs).
/// </summary>
internal static class Brotli
{
    /// <summary>Decodes into a rented buffer (content streams; as <see cref="Flate.DecodePooled"/>).</summary>
    public static PooledBytes DecodePooled(byte[] data, ref bool truncated)
    {
        using var input = new MemoryStream(data, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        return StreamFilters.ReadPooled(brotli, (int)Math.Clamp((long)data.Length * 4, 4096, 64L << 20), ref truncated, operationIsDamage: true);
    }

    public static byte[] Decode(byte[] data, ref bool truncated, int sizeHint = 0)
    {
        // counted until collected, as Flate (docs/FEHLERGEDAECHTNIS.md, No. 1003)
        var buffer = DecodedMemory.Allocate(sizeHint > 0 ? Math.Min(sizeHint, 64 << 20) : (int)Math.Clamp((long)data.Length * 4, 256, 64L << 20));
        var length = 0;
        try
        {
            using var input = new MemoryStream(data, writable: false);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            while (true)
            {
                if (length == buffer.Length)
                {
                    var probe = brotli.ReadByte();
                    if (probe < 0) break;
                    StreamFilters.CheckSize((long)buffer.Length * 2);
                    buffer = DecodedMemory.Resize(buffer, length, (int)Math.Min((long)buffer.Length * 2, Array.MaxLength));
                    buffer[length++] = (byte)probe;
                }
                var read = brotli.Read(buffer, length, buffer.Length - length);
                if (read == 0) break;
                length += read;
            }
        }
        // Damaged data: depending on where the damage is, BrotliStream reports it as InvalidData or as InvalidOperation ("decoder error")
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            truncated = true;
        }
        if (length != buffer.Length) buffer = DecodedMemory.Resize(buffer, length, length);
        return buffer;
    }

    /// <summary>Input per encoder call: quality 11 manages well under a megabyte per second, so cancelling takes effect within a fraction of a second.</summary>
    private const int Slice = 256 << 10;

    /// <summary>Bytes given to <see cref="Encode"/> on this thread (tests: each stream is packed only once).</summary>
    [ThreadStatic] internal static long EncodedForTests;

    /// <summary>
    /// Compresses with the highest quality (11) and a large window (2^22), fed in slices so that <paramref name="cancel"/> is honoured
    /// between them (in one call a large image blocked cancelling for a minute and more); returns a copy of the data if compression fails.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> data, CancellationToken cancel = default)
    {
        EncodedForTests += data.Length;
        using var encoder = new BrotliEncoder(quality: 11, window: 22);
        var output = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        var (position, written) = (0, 0);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var length = Math.Min(Slice, data.Length - position);
            var final = position + length == data.Length;
            var status = encoder.Compress(data.Slice(position, length), output.AsSpan(written), out var consumed, out var produced, final);
            (position, written) = (position + consumed, written + produced);
            if (status == System.Buffers.OperationStatus.Done && final && position == data.Length) return output[..written];
            // the bound holds for the whole output, so a full buffer (or invalid data) means the encoder failed
            if (status == System.Buffers.OperationStatus.InvalidData || written == output.Length) return [.. data];
        }
    }
}
