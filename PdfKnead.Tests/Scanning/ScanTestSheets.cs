using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Barcodes.Qr;
using PdfKnead.Core.Printing;
using PdfKnead.Core.Scanning;

namespace PdfKnead.Tests.Scanning;

/// <summary>
/// Test sheets for scanning with a real device (PRUEFUNGEN §6z): A4, each with a large page number, a frame 15 mm from the edge and two QR
/// codes „PDF-Knead Scantest n/count oben links“ and „… unten rechts“. Printed once (by hand, never from the tests), they are read back from
/// the scans: the order, that every page came once, and how it lies. As an <see cref="ISheetSource"/> the program's own printing prints
/// them; <see cref="Identify"/> reads them back.
/// </summary>
internal sealed class ScanTestSheets(int count, IReadOnlyCollection<int>? blankPages = null) : ISheetSource
{
    /// <summary>The pages (1-based) left white: for both sides by turning with „leere Seiten weglassen“.</summary>
    private readonly HashSet<int> _blank = [.. blankPages ?? []];

    private const double A4Width = 210, A4Height = 297, Frame = 15, Line = 0.6, QrSize = 40, QrInset = 25;
    private const double PointsPerMm = 72 / 25.4;

    /// <summary>The text of a code: the page, the count and where on the page it is.</summary>
    public static string Text(int page, int count, bool topLeft) => $"PDF-Knead Scantest {page}/{count} {(topLeft ? "oben links" : "unten rechts")}";

    private readonly BitMatrix[] _topLeft = [.. Enumerable.Range(1, count).Select(p => QrEncoder.Encode(Text(p, count, true)))];
    private readonly BitMatrix[] _bottomRight = [.. Enumerable.Range(1, count).Select(p => QrEncoder.Encode(Text(p, count, false)))];

    public int SheetCount => count;

    public (double Width, double Height) SheetSize(int sheet) => (A4Width * PointsPerMm, A4Height * PointsPerMm);

    /// <summary>Whether the point (in millimetres from the top left) of a sheet is black.</summary>
    public bool Black(int sheet, double x, double y)
    {
        if (_blank.Contains(sheet + 1)) return false;   // a blank page: nothing on it at all
        // the frame
        bool Near(double v, double at) => Math.Abs(v - at) < Line / 2;
        if ((Near(x, Frame) || Near(x, A4Width - Frame)) && y >= Frame && y <= A4Height - Frame) return true;
        if ((Near(y, Frame) || Near(y, A4Height - Frame)) && x >= Frame && x <= A4Width - Frame) return true;
        // the codes (their light border is the white paper around them)
        if (InQr(_topLeft[sheet], x - QrInset, y - QrInset) is { } a) return a;
        if (InQr(_bottomRight[sheet], x - (A4Width - QrInset - QrSize), y - (A4Height - QrInset - QrSize)) is { } b) return b;
        // the page number in a block font, 80 mm high, in the middle
        return Digit(sheet + 1, x, y);
    }

    private static bool? InQr(BitMatrix code, double x, double y)
    {
        if (x < 0 || y < 0 || x >= QrSize || y >= QrSize) return null;
        var module = QrSize / code.Width;
        return code[(int)(x / module), (int)(y / module)];
    }

    // digits 0–9 in 5 × 7 cells, one row per string
    private static readonly string[][] Font =
    [
        ["01110", "10001", "10011", "10101", "11001", "10001", "01110"], ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ["01110", "10001", "00001", "00010", "00100", "01000", "11111"], ["11111", "00010", "00100", "00010", "00001", "10001", "01110"],
        ["00010", "00110", "01010", "10010", "11111", "00010", "00010"], ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
        ["00110", "01000", "10000", "11110", "10001", "10001", "01110"], ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ["01110", "10001", "10001", "01110", "10001", "10001", "01110"], ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
    ];

    private static bool Digit(int number, double x, double y)
    {
        var text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        const double Cell = 80.0 / 7, Gap = Cell;
        var width = text.Length * 5 * Cell + (text.Length - 1) * Gap;
        var left = (A4Width - width) / 2;
        var top = (A4Height - 7 * Cell) / 2;
        if (y < top || y >= top + 7 * Cell || x < left || x >= left + width) return false;
        var column = (int)((x - left) / (5 * Cell + Gap));
        var inDigit = x - left - column * (5 * Cell + Gap);
        if (inDigit >= 5 * Cell) return false;
        return Font[text[column] - '0'][(int)((y - top) / Cell)][(int)(inDigit / Cell)] == '1';
    }

    public void RenderBand(int sheet, double pixelsPerPoint, int top, int width, int rows, Span<byte> pixels, int stride, bool gray, CancellationToken cancel)
    {
        var mmPerPixel = 1 / (pixelsPerPoint * PointsPerMm);
        for (var r = 0; r < rows; r++)
        {
            cancel.ThrowIfCancellationRequested();
            var y = (top + rows - 1 - r) * mmPerPixel;   // bottom row of the band first, as in a Windows DIB
            var row = pixels.Slice(r * stride, stride);
            for (var x = 0; x < width; x++)
            {
                var value = Black(sheet, x * mmPerPixel, y) ? (byte)0 : (byte)255;
                if (gray) row[x] = value;
                else row.Slice(x * 3, 3).Fill(value);
            }
        }
    }

    public void WritePdf(IReadOnlyList<int> sheets, Stream output, CancellationToken cancel) => throw new NotSupportedException("printed under Windows only");

    public void WriteRasterPdf(IReadOnlyList<int> sheets, int dpi, Stream output, CancellationToken cancel) => throw new NotSupportedException("printed under Windows only");

    /// <summary>A sheet as a grey image at <paramref name="dpi"/>, rows from the top (for the self-check without a printer).</summary>
    public GrayImage Render(int sheet, int dpi)
    {
        var ppp = dpi / 72.0;
        var (w, h) = ((int)(A4Width * PointsPerMm * ppp), (int)(A4Height * PointsPerMm * ppp));
        var bottomFirst = new byte[w * h];
        RenderBand(sheet, ppp, 0, w, h, bottomFirst, w, true, CancellationToken.None);
        var pixels = new byte[w * h];
        for (var r = 0; r < h; r++) bottomFirst.AsSpan((h - 1 - r) * w, w).CopyTo(pixels.AsSpan(r * w));
        return new GrayImage(w, h, pixels);
    }

    /// <summary>
    /// Reads a scanned test sheet: its number (from the code „oben links“) and whether it lies upright (that code in the upper left half of
    /// the scan); null if no code of a test sheet is found.
    /// </summary>
    public static (int Page, int Count, bool Upright)? Identify(ScannedImage scan)
    {
        var gray = scan.Channels == 1 ? scan.Pixels : Luma(scan);
        foreach (var code in BarcodeScanner.FindAll(new GrayImage(scan.Width, scan.Height, gray)))
        {
            var parts = code.Text.Split(' ');
            if (!code.Text.StartsWith("PDF-Knead Scantest ", StringComparison.Ordinal) || !code.Text.EndsWith(" oben links", StringComparison.Ordinal)) continue;
            var numbers = parts[2].Split('/');
            var (cx, cy) = (code.Corners.Average(c => c.X), code.Corners.Average(c => c.Y));
            return (int.Parse(numbers[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(numbers[1], System.Globalization.CultureInfo.InvariantCulture),
                cx < scan.Width / 2.0 && cy < scan.Height / 2.0);
        }
        return null;
    }

    /// <summary>Where the codes of a scanned test sheet lie (their centres in pixels, and their texts) — for finding out how a page came.</summary>
    public static IEnumerable<string> Describe(ScannedImage scan)
    {
        var gray = scan.Channels == 1 ? scan.Pixels : Luma(scan);
        foreach (var code in BarcodeScanner.FindAll(new GrayImage(scan.Width, scan.Height, gray)))
            yield return $"„{code.Text}“ at ({code.Corners.Average(c => c.X):0}, {code.Corners.Average(c => c.Y):0}) of {scan.Width} × {scan.Height}, " +
                $"corners {string.Join(" ", code.Corners.Select(c => $"({c.X:0},{c.Y:0})"))}";
    }

    private static byte[] Luma(ScannedImage scan)
    {
        var result = new byte[scan.Width * scan.Height];
        for (var i = 0; i < result.Length; i++)
            result[i] = (byte)((scan.Pixels[i * 3] * 299 + scan.Pixels[i * 3 + 1] * 587 + scan.Pixels[i * 3 + 2] * 114) / 1000);
        return result;
    }
}

/// <summary>The test sheets read themselves back without printer or scanner: the codes and the page number as rendered.</summary>
public sealed class ScanTestSheetsTests
{
    [Fact]
    public void Rendered_CodesReadBack_AndUpright()
    {
        var sheets = new ScanTestSheets(4);
        var image = sheets.Render(2, 150);
        var codes = BarcodeScanner.FindAll(image).Select(c => c.Text).Order().ToList();
        Assert.Equal([ScanTestSheets.Text(3, 4, true), ScanTestSheets.Text(3, 4, false)], codes);
        var scan = new ScannedImage(image.Width, image.Height, 1, 150, image.Pixels);
        Assert.Equal((3, 4, true), ScanTestSheets.Identify(scan));
        // turned by half a turn (fed upside down): the code „oben links“ is now at the bottom right
        Assert.Equal((3, 4, false), ScanTestSheets.Identify(ScanImageOps.Rotate(scan, 2)));
        // the big number in the middle is black there
        Assert.True(sheets.Black(2, 105, 148.5) || sheets.Black(2, 95, 148.5) || sheets.Black(2, 115, 148.5));
    }

    [Fact]
    public void BlankPages_WhiteAndCountedAsBlank()
    {
        // pages 2 and 7 of 8 white (both sides by turning with „leere Seiten weglassen“): nothing on them, the program's test calls them blank
        var sheets = new ScanTestSheets(8, [2, 7]);
        var blank = sheets.Render(1, 75);
        Assert.All(blank.Pixels, p => Assert.Equal(255, p));
        Assert.True(BlankPages.IsBlank(new ScannedImage(blank.Width, blank.Height, 1, 75, blank.Pixels)));
        var page = sheets.Render(2, 150);
        Assert.False(BlankPages.IsBlank(new ScannedImage(page.Width, page.Height, 1, 150, page.Pixels)));
        Assert.Equal((3, 8, true), ScanTestSheets.Identify(new ScannedImage(page.Width, page.Height, 1, 150, page.Pixels)));
    }
}
