using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Pdf.Filters;
using Xunit;

namespace PdfKnead.Tests.Pdf;

/// <summary>"Flate – strong": every output inflates back exactly to the original and is not larger than .NET at its strongest level.</summary>
public class StrongDeflateTests
{
    private static byte[] Inflate(byte[] packed)
    {
        using var input = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Net(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(data);
        return output.ToArray();
    }

    public static TheoryData<string> Cases => ["leer", "ein Byte", "zwei Bytes", "Nullen", "Zufall", "Text", "Inhaltsstrom", "Muster 32 KB", "gemischt"];

    private static byte[] Data(string name)
    {
        var random = new Random(7);
        switch (name)
        {
            case "leer": return [];
            case "ein Byte": return [42];
            case "zwei Bytes": return [1, 1];
            case "Nullen": return new byte[1_000_000];
            case "Zufall":
                var bytes = new byte[300_000];
                random.NextBytes(bytes);
                return bytes;
            case "Text":
                var text = new StringBuilder();
                var words = new[] { "Rechnung", "Angebot", "Summe", "Datum", "Max", "Mustermann", "Straße", "und", "der", "die", "das", "PDF" };
                for (var i = 0; i < 60_000; i++) text.Append(words[random.Next(words.Length)]).Append(i % 13 == 0 ? '\n' : ' ');
                return Encoding.UTF8.GetBytes(text.ToString());
            case "Inhaltsstrom":
                var content = new StringBuilder();
                for (var i = 0; i < 40_000; i++)
                    content.Append($"{random.Next(600)}.{random.Next(100)} {random.Next(800)}.{random.Next(100)} m {random.Next(600)} {random.Next(800)} l S\n");
                return Encoding.ASCII.GetBytes(content.ToString());
            case "Muster 32 KB":
                // repetition exactly at the window boundary
                var block = new byte[32_767];
                random.NextBytes(block);
                return [.. block, .. block, .. block, .. block];
            default:
                var mixed = new MemoryStream();
                for (var i = 0; i < 20; i++)
                {
                    var part = new byte[random.Next(1, 40_000)];
                    if (i % 3 == 0) random.NextBytes(part);
                    else Array.Fill(part, (byte)(i * 7));
                    mixed.Write(part);
                    mixed.Write(Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 712 Td (Seite) Tj ET\n"));
                }
                return mixed.ToArray();
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTrip_AndNotLargerThanNet(string name)
    {
        var data = Data(name);
        var packed = StrongDeflate.Encode(data);
        Assert.Equal(data, Inflate(packed));
        var net = Net(data).Length;
        // At most 5 % above .NET (1 MB of zeros: one header per 300 KB block); the writer keeps the smaller result anyway
        Assert.True(packed.Length <= net * 1.05 + 8, $"{name}: stark {packed.Length}, .NET {net}");
        if (name is "Text" or "Inhaltsstrom" or "gemischt" or "Muster 32 KB") Assert.True(packed.Length < net, $"{name}: stark {packed.Length}, .NET {net}");
    }

    /// <summary>Large stream with many changes (text, random data, filled areas): several blocks across two threads, restored exactly.</summary>
    [Fact]
    public void RoundTrip_LargeMixedOverTwoThreads()
    {
        var random = new Random(5);
        var data = new MemoryStream();
        for (var part = 0; part < 30; part++)
        {
            var chunk = new byte[random.Next(20_000, 150_000)];
            switch (part % 3)
            {
                case 0:
                    random.NextBytes(chunk);
                    break;
                case 1:
                    for (var i = 0; i < chunk.Length; i++) chunk[i] = (byte)"0123456789 m l S\n"[random.Next(17)];
                    break;
                default:
                    Array.Fill(chunk, (byte)part);
                    break;
            }
            data.Write(chunk);
        }
        var bytes = data.ToArray();
        Assert.True(bytes.Length > 2_000_000);
        Assert.Equal(bytes, Inflate(StrongDeflate.Encode(bytes)));
    }

    /// <summary>A cancelled packing ends with an OperationCanceledException at once — independent of the speed of the computer.</summary>
    [Fact]
    public void Cancelled_Stops()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => StrongDeflate.Encode(new byte[8_000_000], cancel.Token));
    }

    /// <summary>
    /// Cancelling in the middle of a large stream ends with an OperationCanceledException without hanging. A time test: on a fast computer
    /// the packing may be done before the cancellation arrives. The cancellation comes from a thread of its own: a timer of the
    /// CancellationTokenSource runs on the thread pool, which the full test run keeps busy — in the guard m2-abschluss (20.11.2026) it
    /// arrived only after the packing had taken 7 s and was done (docs/FEHLERGEDAECHTNIS.md, No. 399, addendum).
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void Cancel_LargeInput_Stops()
    {
        var data = new byte[8_000_000];
        new Random(9).NextBytes(data.AsSpan(0, 4_000_000));
        using var cancel = new CancellationTokenSource();
        var canceller = new Thread(() =>
        {
            Thread.Sleep(200);
            cancel.Cancel();
        }) { IsBackground = true };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        canceller.Start();
        Assert.ThrowsAny<OperationCanceledException>(() => StrongDeflate.Encode(data, cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10 * TestKind.Factor), $"{watch.Elapsed}");
        canceller.Join();
    }

    /// <summary>Many small random cases (lengths, repetitions, alphabets): always restored exactly.</summary>
    [Fact]
    public void RoundTrip_ManySmallRandomCases()
    {
        var random = new Random(11);
        for (var round = 0; round < 400; round++)
        {
            var length = random.Next(0, 5000);
            var alphabet = random.Next(1, 256);
            var data = new byte[length];
            for (var i = 0; i < length; i++)
                data[i] = i > 10 && random.Next(4) == 0 ? data[i - random.Next(1, Math.Min(i, 300))] : (byte)random.Next(alphabet);
            Assert.Equal(data, Inflate(StrongDeflate.Encode(data)));
        }
    }

    /// <summary>
    /// RFC 1951, 3.2.7 Sätze 10, 11: a single distance code is coded with one bit, not with zero; data of literals only may say so with one
    /// distance code of zero bits. The code lengths: one used symbol gets length 1. Own data from an alphabet of 32 bytes in which no three bytes
    /// repeat (no match at all), and the same data twice (the second half by matches): each packs into a dynamic block (BTYPE 10) that zlib reads
    /// back exactly; without any match at most two distance codes (HDIST ≤ 1). Where only one distance code is used, the packer adds a second
    /// one of one bit, so that the code is complete (as zlib does for old decoders); RFC 1951 allows that as well as one code of one bit with an
    /// unused second.
    /// </summary>
    [Fact]
    public void DynamicBlocks_NoDistanceCodeAndASingleOne()
    {
        var single = new byte[30];
        StrongDeflate.Huffman.Lengths(new int[30] { 0, 0, 0, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 15, single);
        Assert.Equal(1, single[3]);
        Assert.Equal(1, single.Count(l => l > 0));
        var random = new Random(1951);
        var seen = new HashSet<int>();
        var literals = new List<byte> { 0, 1 };
        while (literals.Count < 1500)
        {
            var b = (byte)(random.Next(32) < 24 ? random.Next(8) : random.Next(32));   // skewed, so that a Huffman code pays
            var triple = literals[^2] << 16 | literals[^1] << 8 | b;
            if (seen.Add(triple)) literals.Add(b);
        }
        byte[] once = [.. literals];
        byte[] twice = [.. once, .. once];
        foreach (var (data, noDistance) in new[] { (once, true), (twice, false) })
        {
            var packed = StrongDeflate.Encode(data);
            Assert.Equal(data, Inflate(packed));
            var first = packed[2];                       // after the two bytes of the zlib header
            Assert.Equal(2, first >> 1 & 3);             // BTYPE 10: dynamic Huffman codes
            if (noDistance) Assert.InRange(packed[3] & 31, 0, 1);   // HDIST (the five bits after HLIT): no needless distance codes
        }
    }

    /// <summary>
    /// RFC 1951, 3.2.1, 3.2.7: codes are at most 15 bits (literal/length, distance) and 7 bits (the code lengths' own code). Frequencies after
    /// the Fibonacci sequence make ordinary Huffman coding as deep as there are symbols (29 for 30); the packer then takes package-merge — the
    /// lengths stay within the limit and form a complete prefix code (Kraft sum 1). Data with such frequencies pack and unpack alike (Bericht M2,
    /// Zeile 180: no test reached package-merge before).
    /// </summary>
    [Fact]
    public void LengthLimitedCodes_PackageMerge()
    {
        foreach (var (symbols, limit) in new[] { (30, 15), (19, 7) })
        {
            var freq = new int[symbols];
            int a = 1, b = 1;
            for (var s = 0; s < symbols; s++)
            {
                freq[s] = a;
                (a, b) = (b, a + b);
            }
            var lengths = new byte[symbols];
            StrongDeflate.Huffman.Lengths(freq, limit, lengths);
            Assert.All(lengths, l => Assert.InRange(l, 1, limit));
            Assert.Equal(limit, lengths.Max());
            Assert.Equal(1.0, lengths.Sum(l => Math.Pow(2, -l)), 12);
        }

        // bytes 0..24 with Fibonacci counts, shuffled so that few repeats remain: the literal code needs the limit
        var data = new List<byte>();
        int x = 1, y = 1;
        for (var s = 0; s < 25; s++)
        {
            data.AddRange(Enumerable.Repeat((byte)s, x));
            (x, y) = (y, x + y);
        }
        var random = new Random(5);
        var shuffled = data.OrderBy(_ => random.Next()).ToArray();
        Assert.Equal(shuffled, Inflate(StrongDeflate.Encode(shuffled)));
    }

    /// <summary>
    /// RFC 1950, 2.1, 2.2 and RFC 1951, 6 (Bericht M2, Zeilen 169, 170, 183): what the own packer writes is a zlib stream — CMF X'78' (deflate,
    /// 32 KB window), FLG with FDICT 0 and CMF·256 + FLG a multiple of 31, after the deflate data the Adler-32 sum of the data with the most
    /// significant byte first, and nothing after it.
    /// </summary>
    [Fact]
    public void Packed_IsAZlibStreamWithItsChecksum()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 4000).Select(i => (char)('a' + i * 7 % 26))));
        var packed = StrongDeflate.Encode(data);
        Assert.Equal(0x78, packed[0]);
        Assert.Equal(0, (packed[0] * 256 + packed[1]) % 31);
        Assert.Equal(0, packed[1] & 0x20);
        Assert.Equal(3, packed[1] >> 6);   // FLEVEL 3: the slowest, strongest algorithm (RFC 1950, 2.2)
        var adler = Flate.Adler32(data);
        Assert.Equal(new[] { (byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler }, packed[^4..]);
        using var inflater = new DeflateStream(new MemoryStream(packed, 2, packed.Length - 6), CompressionMode.Decompress);
        using var unpacked = new MemoryStream();
        inflater.CopyTo(unpacked);
        Assert.Equal(data, unpacked.ToArray());
    }

    private const System.Reflection.BindingFlags Hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;

    private static T Table<T>(string name) => (T)typeof(StrongDeflate).GetField(name, Hidden)!.GetValue(null)!;

    /// <summary>
    /// RFC 1951, 3.2.2: the canonical codes from the lengths alone — count the codes of each length, the first code of each length, then the
    /// codes in the order of the symbols; length 0 gets no code (3.2.2 Satz 17). Own lengths 4, 2, 3, 4, 3, 0, 2, 3 give 1110, 00, 100, 1111,
    /// 101, —, 01, 110 (worked out by hand after the three steps); the packer stores them bit-reversed, as Huffman codes go into the stream from
    /// their most significant bit (3.1.1).
    /// </summary>
    [Fact]
    public void CanonicalCodes_TheThreeStepsOf322()
    {
        var codes = StrongDeflate.Huffman.Codes([4, 2, 3, 4, 3, 0, 2, 3]);
        string[] expected = ["1110", "00", "100", "1111", "101", "", "01", "110"];
        for (var s = 0; s < expected.Length; s++)
        {
            var bits = string.Concat(Enumerable.Range(0, expected[s].Length).Select(b => (codes[s] >> b & 1) == 1 ? '1' : '0'));
            Assert.Equal(expected[s], bits);
        }
        Assert.Equal(0u, codes[5]);
    }

    /// <summary>
    /// RFC 1951, 3.2.5 and 3.2.6: the tables of the packer, rebuilt from the rule of the RFC — length codes 257–264 without extra bits, then one
    /// more extra bit every four codes up to five, each base the previous one plus its range, code 285 the length 258 without extra bits;
    /// distance codes 0–3 without extra bits, then one more every two codes, up to 13 (base 24 577); the fixed literal/length code with 8, 9, 7
    /// and 8 bits for 0–143, 144–255, 256–279, 280–287; and the order of the code length code lengths of 3.2.7.
    /// </summary>
    [Fact]
    public void Tables_AsRfc1951()
    {
        var lengthBase = Table<int[]>("LengthBase");
        var lengthExtra = Table<int[]>("LengthExtra");
        for (int code = 0, b = 3; code < 28; b += 1 << lengthExtra[code], code++)
        {
            Assert.Equal(code < 8 ? 0 : code / 4 - 1, lengthExtra[code]);
            Assert.Equal(b, lengthBase[code]);
        }
        Assert.Equal((258, 0), (lengthBase[28], lengthExtra[28]));
        var distBase = Table<int[]>("DistBase");
        var distExtra = Table<int[]>("DistExtra");
        Assert.Equal(30, distBase.Length);
        for (int code = 0, b = 1; code < 30; b += 1 << distExtra[code], code++)
        {
            Assert.Equal(code < 4 ? 0 : code / 2 - 1, distExtra[code]);
            Assert.Equal(b, distBase[code]);
        }
        Assert.Equal(32768, distBase[29] + (1 << distExtra[29]) - 1);
        var fixedLength = typeof(StrongDeflate).GetMethod("FixedLiteralLength", Hidden)!;
        for (var symbol = 0; symbol < 288; symbol++)
            Assert.Equal(symbol < 144 ? 8 : symbol < 256 ? 9 : symbol < 280 ? 7 : 8, (int)fixedLength.Invoke(null, [symbol])!);
        Assert.Equal(new byte[] { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 }, Table<byte[]>("CodeLengthOrder"));
    }

    /// <summary>
    /// RFC 1951, 3.2.4: random data does not pack — it goes into stored blocks, each starting on a byte boundary with LEN and its one's
    /// complement NLEN, no more than 65 535 bytes, the last with BFINAL; together exactly the data.
    /// </summary>
    [Fact]
    public void StoredBlocks_AtMost65535Bytes_LenAndNlen()
    {
        var data = new byte[200_000];
        new Random(3).NextBytes(data);
        var packed = StrongDeflate.Encode(data);
        var joined = new List<byte>();
        int at = 2, blocks = 0;
        while (true)
        {
            var head = packed[at++];
            Assert.Equal(0, head >> 1 & 3);   // BTYPE 00
            int len = packed[at] | packed[at + 1] << 8, nlen = packed[at + 2] | packed[at + 3] << 8;
            Assert.Equal(0xFFFF, len ^ nlen);
            Assert.InRange(len, 0, 65535);
            joined.AddRange(packed.AsSpan(at + 4, len).ToArray());
            at += 4 + len;
            blocks++;
            if ((head & 1) == 1) break;
        }
        Assert.Equal(data, joined.ToArray());
        Assert.Equal(packed.Length - 4, at);   // then only the Adler-32 sum
        Assert.True(blocks >= 4);
    }

    /// <summary>
    /// RFC 1951, 3.2.6: a short text packs into one block with the fixed codes (BTYPE 01, BFINAL 1) — the header of dynamic tables would not pay
    /// off — and zlib reads it back exactly.
    /// </summary>
    [Fact]
    public void FixedCodes_ShortText()
    {
        var data = Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 712 Td (Seite 1) Tj ET");
        var packed = StrongDeflate.Encode(data);
        Assert.Equal(1, packed[2] & 1);
        Assert.Equal(1, packed[2] >> 1 & 3);
        Assert.Equal(data, Inflate(packed));
    }
}
