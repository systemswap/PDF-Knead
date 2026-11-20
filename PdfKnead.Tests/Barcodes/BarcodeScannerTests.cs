using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Barcodes.DataMatrix;
using PdfKnead.Core.Barcodes.Linear;
using PdfKnead.Core.Barcodes.Qr;
using Xunit;

namespace PdfKnead.Tests.Barcodes;

/// <summary>Finding codes in rendered images: every kind, turned in any direction, smoothed edges, noise, a click anywhere on the code.</summary>
public class BarcodeScannerTests
{
    /// <summary>
    /// Draws modules (columns × rows; bar codes as one row stretched to <paramref name="barHeight"/> modules) turned by
    /// <paramref name="angle"/> degrees, with 3 × 3 supersampling and optional noise, centred in a white square.
    /// </summary>
    internal static GrayImage Draw(bool[,] grid, double module, double angle, double barHeight = 0, int noise = 0, int seed = 1)
    {
        int columns = grid.GetLength(0), rows = grid.GetLength(1);
        double gw = columns, gh = rows == 1 ? barHeight : rows;
        var size = (int)(Math.Sqrt(gw * gw + gh * gh) * module + 40 * module);
        var pixels = new byte[size * size];
        double rad = angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad), centre = size / 2.0;
        var random = new Random(seed);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dark = 0;
                for (var sy = 0; sy < 3; sy++)
                    for (var sx = 0; sx < 3; sx++)
                    {
                        double px = x + (sx + 0.5) / 3 - centre, py = y + (sy + 0.5) / 3 - centre;
                        double u = (px * cos + py * sin) / module + gw / 2, v = (-px * sin + py * cos) / module + gh / 2;
                        if (u >= 0 && v >= 0 && u < gw && v < gh && grid[(int)u, rows == 1 ? 0 : (int)v]) dark++;
                    }
                var g = 255 - dark * 255 / 9 + (noise == 0 ? 0 : random.Next(-noise, noise + 1));
                pixels[y * size + x] = (byte)Math.Clamp(g, 0, 255);
            }
        return new GrayImage(size, size, pixels);
    }

    internal static bool[,] Grid(BitMatrix m)
    {
        var g = new bool[m.Width, m.Height];
        for (var y = 0; y < m.Height; y++)
            for (var x = 0; x < m.Width; x++) g[x, y] = m[x, y];
        return g;
    }

    internal static bool[,] Grid(bool[] modules)
    {
        var g = new bool[modules.Length, 1];
        for (var i = 0; i < modules.Length; i++) g[i, 0] = modules[i];
        return g;
    }

    public static TheoryData<string, double> Angles() => new()
    {
        { "qr", 0 }, { "qr", 90 }, { "qr", 33 }, { "qr", 200 },
        { "dm", 0 }, { "dm", 270 }, { "dm", 17 }, { "dm-rect", 125 },
        { "ean", 0 }, { "ean", 180 }, { "ean", 71 },
        { "c128", 0 }, { "c128", 90 }, { "c128", 312 },
        { "c39", 0 }, { "c39", 45 },
        { "itf", 0 }, { "itf", 160 },
    };

    [Theory]
    [MemberData(nameof(Angles))]
    public void FindsAndReads_InAnyDirection(string kind, double angle)
    {
        var (grid, expected, format, height) = kind switch
        {
            "qr" => (Grid(QrEncoder.Encode("https://example.org/rechnung?nr=4711", QrErrorCorrection.Q)), "https://example.org/rechnung?nr=4711", BarcodeFormat.Qr, 0.0),
            "dm" => (Grid(DataMatrixCodec.Encode("Charge 2026-17 / Los 42")), "Charge 2026-17 / Los 42", BarcodeFormat.DataMatrix, 0.0),
            "dm-rect" => (Grid(DataMatrixCodec.Encode("PDF-Knead", DataMatrixShape.Rectangular)), "PDF-Knead", BarcodeFormat.DataMatrix, 0.0),
            "ean" => (Grid(LinearCodes.Encode(LinearSymbology.Ean13, "4006381333931")), "4006381333931", BarcodeFormat.Ean13, 30.0),
            "c128" => (Grid(LinearCodes.Encode(LinearSymbology.Code128, "Rechnung RE-2026-0815")), "Rechnung RE-2026-0815", BarcodeFormat.Code128, 25.0),
            "c39" => (Grid(LinearCodes.Encode(LinearSymbology.Code39, "PDF-KNEAD 42")), "PDF-KNEAD 42", BarcodeFormat.Code39, 30.0),
            _ => (Grid(LinearCodes.Encode(LinearSymbology.Itf, "12345678901231")), "12345678901231", BarcodeFormat.Itf, 25.0),
        };
        var image = Draw(grid, 6, angle, height, noise: 30, seed: (int)angle + kind.Length);
        // a click off the centre of the code
        var found = BarcodeScanner.ReadAt(image, image.Width * 0.47, image.Height * 0.52)!;
        Assert.Equal(format, found.Format);
        Assert.Equal(expected, found.Text);
        Assert.InRange(found.Module, 5, 7);
        Assert.False(found.TouchesEdge);
    }

    [Fact]
    public void NoCode_Nothing()
    {
        var empty = new GrayImage(200, 200, Enumerable.Repeat((byte)255, 200 * 200).ToArray());
        Assert.Null(BarcodeScanner.ReadAt(empty, 100, 100));
        // a filled black rectangle is no code either
        var block = new bool[20, 20];
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 20; x++) block[x, y] = true;
        Assert.Null(BarcodeScanner.ReadAt(Draw(block, 5, 0), 100, 100));
    }

    [Fact]
    public void QuietZoneMeasured()
    {
        var image = Draw(Grid(QrEncoder.Encode("Ruhezone")), 5, 0);
        var found = BarcodeScanner.ReadAt(image, image.Width / 2.0, image.Height / 2.0)!;
        Assert.True(found.QuietZone >= 4, $"Ruhezone {found.QuietZone}");
    }

    [Fact]
    public void FindAll_CoarseAndFineCodesBesideText()
    {
        // a coarse QR code (24 pixels per module, its light modules wider than any bridged gap), a fine one and text-like strokes around
        const int size = 2400;
        var pixels = Enumerable.Repeat((byte)255, size * size).ToArray();
        void Paste(bool[,] grid, int x0, int y0, int module)
        {
            for (var r = 0; r < grid.GetLength(1); r++)
                for (var c = 0; c < grid.GetLength(0); c++)
                    if (grid[c, r])
                        for (var y = 0; y < module; y++)
                            for (var x = 0; x < module; x++) pixels[(y0 + r * module + y) * size + x0 + c * module + x] = 0;
        }
        Paste(Grid(QrEncoder.Encode("Plakat 2026")), 100, 100, 24);
        Paste(Grid(QrEncoder.Encode("klein")), 1500, 1500, 5);
        var random = new Random(3);
        for (var line = 0; line < 30; line++)
            for (var x = 1000; x < 2300; x += random.Next(6, 14))
                for (var y = 0; y < 18; y++)
                    if (random.Next(3) > 0) pixels[(200 + line * 40 + y) * size + x] = 0;
        var found = BarcodeScanner.FindAll(new GrayImage(size, size, pixels));
        Assert.Equal(["Plakat 2026", "klein"], found.Select(f => f.Text).OrderByDescending(t => t.Length));
    }

    [Theory]
    [InlineData("010401234567890117261231", true, 3)]
    [InlineData("010401234567890117261231", true, 2)]
    [InlineData("Charge 2026/17", false, 3)]
    public void FindAll_RectangularDataMatrix(string text, bool gs1, int module)
    {
        // a flat symbol (12 or 16 rows) has few changes of light and dark down its columns; the page search must find it all the same
        var symbol = DataMatrixCodec.Encode(text, DataMatrixShape.Rectangular, gs1);
        var image = Draw(Grid(symbol), module, 0);
        Assert.Equal(text, Assert.Single(BarcodeScanner.FindAll(image)).Text);
    }

    [Fact]
    public void Ean8_ReadBackwards_NeverAnotherNumber()
    {
        // read from its end, the right half of an EAN-8 is mirrored; matched against the left digits only, it can give other digits with a
        // valid check digit. Whatever the size and direction: the right number, or nothing
        var wrong = new List<string>();
        foreach (var text in new[] { "96385074", "52364228", "76011108", "12345670" })
        {
            var grid = Grid(LinearCodes.Encode(LinearSymbology.Ean8, text));
            for (var module = 2.5; module <= 5.01; module += 0.25)
                foreach (var angle in new[] { 0.0, 90, 180, 270 })
                    foreach (var found in BarcodeScanner.FindAll(Draw(grid, module, angle, barHeight: 30)))
                        if (found.Text != text) wrong.Add($"{text} {module} {angle}: {found.Text}");
        }
        Assert.Empty(wrong);
    }
}
