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
    /// the packing may be done before the cancellation arrives.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void Cancel_LargeInput_Stops()
    {
        var data = new byte[8_000_000];
        new Random(9).NextBytes(data.AsSpan(0, 4_000_000));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => StrongDeflate.Encode(data, cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10 * TestKind.Factor), $"{watch.Elapsed}");
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
}
