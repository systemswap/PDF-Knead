using System.Diagnostics;
using System.Text;
using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Barcodes.DataMatrix;
using PdfKnead.Core.Barcodes.Linear;
using PdfKnead.Core.Barcodes.Qr;
using Xunit;

namespace PdfKnead.Tests.Barcodes;

/// <summary>
/// Crafted and damaged codes (KONZEPT.md, section 5.19, security): the readers never throw, never run long, never return more than the
/// symbol can hold, and the display stays bounded — whatever the modules, codewords, widths or contents are.
/// </summary>
[Collection(nameof(Pdf.LimitCollection))]   // measures time: in parallel with the other tests a run took 8 s instead of 0.8 s
[Trait(TestKind.Name, TestKind.Time)]
public class BarcodeRobustnessTests
{
    private static void Quick(Action action, int milliseconds = 5000)
    {
        var watch = Stopwatch.StartNew();
        action();
        Assert.True(watch.ElapsedMilliseconds < milliseconds * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Qr_RandomAndDamagedModules_NeverThrow()
    {
        var random = new Random(41);
        Quick(() =>
        {
            for (var v = 1; v <= 40; v += 3)
            {
                var n = 17 + 4 * v;
                for (var t = 0; t < 20; t++)
                {
                    var m = new BitMatrix(n, n);
                    for (var y = 0; y < n; y++)
                        for (var x = 0; x < n; x++) m[x, y] = random.Next(2) == 0;
                    _ = QrDecoder.Decode(m);
                }
                // a real symbol with more and more modules flipped: repaired, or refused — never a crash
                var real = QrEncoder.Encode(new string('A', QrVersion.Get(v).DataCodewords(QrErrorCorrection.H)), QrErrorCorrection.H, v);
                for (var share = 0.02; share < 0.5; share += 0.06)
                {
                    var damaged = BitMatrix.Parse(real.ToString());
                    for (var k = 0; k < n * n * share; k++)
                    {
                        int x = random.Next(n), y = random.Next(n);
                        damaged[x, y] = !damaged[x, y];
                    }
                    var content = QrDecoder.Decode(damaged);
                    if (content is not null) Assert.True(content.Text.Length <= 7089);
                }
            }
            // sizes that are no QR size
            Assert.Null(QrDecoder.Decode(new BitMatrix(22, 22)));
            Assert.Null(QrDecoder.Decode(new BitMatrix(21, 25)));
        });
    }

    [Fact]
    public void Qr_CraftedCodewords_WithValidErrorCorrection_NeverThrow()
    {
        // valid check codewords around any data: only the segment parser stands between the data and the program
        var random = new Random(43);
        Quick(() =>
        {
            for (var t = 0; t < 600; t++)
            {
                var version = QrVersion.Get(1 + random.Next(40));
                var level = (QrErrorCorrection)random.Next(4);
                var data = new byte[version.DataCodewords(level)];
                random.NextBytes(data);
                // half of them start with a plausible mode indicator (bytes, ECI, Kanji, structured append) and huge counts
                if (t % 2 == 0) data[0] = (byte)(new[] { 0x4F, 0x7F, 0x8F, 0x3F, 0x1F, 0x2F, 0x5F, 0x9F }[t / 2 % 8]);
                var content = QrDecoder.Decode(QrEncoder.Place(data, version, level, random.Next(8)));
                if (content is not null) Assert.True(content.Text.Length <= data.Length * 8);
            }
        });
        // a byte segment that claims 65 535 bytes in a version-1 symbol: refused, not read past the end
        var claim = new byte[QrVersion.Get(10).DataCodewords(QrErrorCorrection.L)];
        claim[0] = 0x4F;
        claim[1] = 0xFF;
        claim[2] = 0xF0;
        Assert.Null(QrDecoder.Decode(QrEncoder.Place(claim, QrVersion.Get(10), QrErrorCorrection.L)));
    }

    [Fact]
    public void DataMatrix_CraftedCodewords_NeverThrow()
    {
        var random = new Random(47);
        Quick(() =>
        {
            for (var t = 0; t < 800; t++)
            {
                var symbol = DataMatrixSymbol.All[random.Next(DataMatrixSymbol.All.Length)];
                var data = new byte[symbol.DataCodewords];
                random.NextBytes(data);
                // the scheme latches and special codewords that read lengths and counts
                if (t % 2 == 0) data[0] = new byte[] { 230, 231, 233, 238, 239, 240, 241, 235, 236 }[t / 2 % 9];
                var content = DataMatrixCodec.Decode(DataMatrixCodec.Place(data, symbol));
                if (content is not null) Assert.True(content.Text.Length <= data.Length * 3 + 16);
            }
        });
        // Base 256 claiming 30 000 bytes in a small symbol: refused
        var small = DataMatrixSymbol.All.First(s => s.Rows == 16 && s.Columns == 16);
        var claim = new byte[small.DataCodewords];
        claim[0] = 231;
        claim[1] = (byte)((255 + 149 * 2 % 255 + 1) % 256);   // randomised 255: a two-codeword length follows
        claim[2] = 200;
        Assert.Null(DataMatrixCodec.Decode(DataMatrixCodec.Place(claim, small)));
    }

    [Fact]
    public void BarCodes_RandomAndHugeWidths_NeverThrow_AndStayFast()
    {
        var random = new Random(53);
        Quick(() =>
        {
            for (var t = 0; t < 2000; t++)
            {
                var runs = new double[random.Next(1, 400)];
                for (var i = 0; i < runs.Length; i++) runs[i] = random.Next(4) == 0 ? random.NextDouble() * 40 : 1 + random.Next(4);
                _ = LinearCodes.Decode(runs);
            }
            // a scan line of 100 000 elements, and degenerate widths
            var many = Enumerable.Range(0, 100_000).Select(i => 1.0 + i % 3).ToArray();
            _ = LinearCodes.Decode(many);
            _ = LinearCodes.Decode([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            _ = LinearCodes.Decode([double.MaxValue, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, double.MaxValue]);
            _ = LinearCodes.Decode([double.NaN, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, double.NaN]);
        });
    }

    [Fact]
    public void Images_Noise_Patterns_Extremes_NeverThrow()
    {
        var random = new Random(59);
        Quick(() =>
        {
            foreach (var kind in new[] { "noise", "black", "white", "checker", "stripes", "blocks" })
            {
                const int size = 400;
                var pixels = new byte[size * size];
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                        pixels[y * size + x] = kind switch
                        {
                            "noise" => (byte)random.Next(256),
                            "black" => 0,
                            "white" => 255,
                            "checker" => (byte)((x + y) % 2 == 0 ? 0 : 255),
                            "stripes" => (byte)(x % 3 == 0 ? 0 : 255),
                            _ => (byte)((x / 7 + y / 5) % 3 == 0 ? 0 : 255),
                        };
                var image = new GrayImage(size, size, pixels);
                for (var k = 0; k < 5; k++) _ = BarcodeScanner.ReadAt(image, random.Next(size), random.Next(size));
                _ = BarcodeScanner.FindAll(image);
            }
            // tiny images and points outside
            _ = BarcodeScanner.ReadAt(new GrayImage(1, 1, [0]), 0, 0);
            _ = BarcodeScanner.ReadAt(new GrayImage(3, 2, [0, 255, 0, 255, 0, 255]), -50, 1e9);
            _ = BarcodeScanner.FindAll(new GrayImage(2, 2, [0, 0, 0, 0]));
        }, 20000);
    }

    /// <summary>
    /// A large, almost black area (as damaged CCITT data of a few kilobytes give, found by the fuzzing run of M2): reading and locating at
    /// a point keep their memory near that of the image. Before, every dark pixel went into a list and onto a stack — at the 24 megapixels
    /// of a closer look several hundred megabytes (docs/FEHLERGEDAECHTNIS.md, No. 946).
    /// </summary>
    [Fact]
    public void Images_LargeDarkArea_BoundedMemory()
    {
        const int width = 4000, height = 3000;
        var pixels = new byte[width * height];
        // light only in a band at the top and in a few thin lines: one connected dark area over the rest
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = (byte)(y < 300 || y % 250 == 0 ? 255 : 0);
        var image = new GrayImage(width, height, pixels);
        Quick(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = BarcodeScanner.ReadAt(image, width / 2.0, height / 2.0);
            _ = BarcodeScanner.Locate(image, width / 2.0, height / 2.0);
            var perPixel = (double)(GC.GetAllocatedBytesForCurrentThread() - before) / (width * height);
            // the masks of the image (dark, grown, seen) take a few bytes per pixel and look; a list of the pixels took sixteen and more
            Assert.True(perPixel < 24, $"{perPixel:0.0} bytes per pixel");
        }, 20000);
    }

    [Fact]
    public void Contents_AnyText_InterpretedSafely()
    {
        var random = new Random(61);
        string[] starts = ["BCD\n", "SPC\n", "WIFI:", "BEGIN:VCARD\n", "MECARD:", "BEGIN:VEVENT\n", "MATMSG:", "mailto:", "tel:", "SMSTO:", "sms:", "geo:", "URLTO:", "https://", "http://", "www.", ""];
        Quick(() =>
        {
            for (var t = 0; t < 3000; t++)
            {
                var b = new StringBuilder(starts[t % starts.Length]);
                var length = random.Next(0, t % 50 == 0 ? 20000 : 300);
                for (var i = 0; i < length; i++)
                    b.Append(random.Next(10) switch
                    {
                        0 => '\n', 1 => ';', 2 => ':', 3 => '\\', 4 => (char)random.Next(0, 32), 5 => (char)random.Next(0xD800, 0xE000),   // lone surrogates
                        6 => '\u202E', 7 => '%', _ => (char)random.Next(32, 0x3000),
                    });
                var text = b.ToString();
                foreach (var format in new[] { BarcodeFormat.Qr, BarcodeFormat.DataMatrix, BarcodeFormat.Code128 })
                {
                    var m = BarcodeInterpreter.Interpret(format, text, gs1: t % 7 == 0);
                    Assert.All(m.Rows, r => Assert.True(r.Value.Length <= 2100));
                    Assert.All(m.Rows, r => Assert.DoesNotContain('\u202E', r.Value));
                    if (m.Action is { } a)
                        Assert.True(a.Target.StartsWith("http://", StringComparison.Ordinal) || a.Target.StartsWith("https://", StringComparison.Ordinal)
                                    || a.Target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));
                }
            }
            // article numbers of any digits
            for (var t = 0; t < 500; t++)
            {
                var digits = string.Concat(Enumerable.Range(0, 13).Select(_ => (char)('0' + random.Next(10))));
                _ = BarcodeInterpreter.Interpret(BarcodeFormat.Ean13, digits);
                _ = BarcodeInterpreter.Interpret(BarcodeFormat.Ean8, digits[..8]);
                _ = BarcodeInterpreter.Interpret(BarcodeFormat.UpcE, "0" + digits[..7]);
            }
        }, 15000);
    }
}
