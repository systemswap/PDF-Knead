namespace PdfKnead.Core.Scanning;

/// <summary>A paper format for the scan area.</summary>
/// <param name="Key">The stored name („whole“, „A4“ …); never translated.</param>
/// <param name="WidthMm">The width in millimetres; null for the whole area of the device.</param>
/// <param name="HeightMm">The height in millimetres; null for the whole area of the device.</param>
public sealed record ScanFormat(string Key, double? WidthMm, double? HeightMm)
{
    /// <summary>The name for the user.</summary>
    public string DisplayName => Key == ScanFormats.WholeKey ? Tr.T("Ganze Fläche") : Key;
}

/// <summary>The paper formats offered for scanning (ISO 216 A sizes and the North American Letter and Legal).</summary>
public static class ScanFormats
{
    /// <summary>The key of „the whole area of the device“.</summary>
    public const string WholeKey = "whole";

    /// <summary>All formats, the whole area first.</summary>
    public static readonly IReadOnlyList<ScanFormat> All =
    [
        new(WholeKey, null, null),
        new("A4", 210, 297),
        new("A5", 148, 210),
        new("A6", 105, 148),
        new("Letter", 215.9, 279.4),
        new("Legal", 215.9, 355.6),
        new("A3", 297, 420),
    ];

    /// <summary>The formats that fit the device's area (a millimetre of tolerance: devices report 215.9 for a width meant for A4 and Letter).</summary>
    /// <param name="maxWidthMm">The widest area; 0 = unknown (then all are offered).</param>
    /// <param name="maxHeightMm">The tallest area; 0 = unknown.</param>
    /// <returns>The formats, the whole area always included.</returns>
    public static IReadOnlyList<ScanFormat> Fitting(double maxWidthMm, double maxHeightMm) =>
        [.. All.Where(f => f.WidthMm is null
            || ((maxWidthMm <= 0 || f.WidthMm <= maxWidthMm + 1) && (maxHeightMm <= 0 || f.HeightMm <= maxHeightMm + 1)))];
}

/// <summary>Turning scanned pages by quarter turns (the samples are moved, nothing is resampled).</summary>
public static class ScanImageOps
{
    /// <summary>Turns a page by quarter turns clockwise.</summary>
    /// <param name="page">The page.</param>
    /// <param name="quarterTurns">Quarter turns clockwise; negative turns counter-clockwise.</param>
    /// <returns>The turned page (the page itself for a multiple of four).</returns>
    public static ScannedImage Rotate(ScannedImage page, int quarterTurns)
    {
        var turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0) return page;
        var (w, h, c) = (page.Width, page.Height, page.Channels);
        var (nw, nh) = turns == 2 ? (w, h) : (h, w);
        var result = new byte[page.Pixels.LongLength];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (nx, ny) = turns switch
                {
                    1 => (h - 1 - y, x),
                    2 => (w - 1 - x, h - 1 - y),
                    _ => (y, w - 1 - x),
                };
                var from = ((long)y * w + x) * c;
                var to = ((long)ny * nw + nx) * c;
                for (var k = 0; k < c; k++) result[to + k] = page.Pixels[from + k];
            }
        return page with { Width = nw, Height = nh, Pixels = result };
    }
}
