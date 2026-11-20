using System.Globalization;
using System.Security;
using System.Text;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>
/// What is asked of the device for one job, chosen from the user's settings and what the device offers (KONZEPT.md §5.25, S9,
/// „Fähigkeiten“): only values the device names, the closest where the wish is not among them.
/// </summary>
/// <param name="Input">The input.</param>
/// <param name="ColorMode">The colour mode as the device names it.</param>
/// <param name="Dpi">The resolution.</param>
/// <param name="Format">The transfer format (media type).</param>
/// <param name="Width">The width of the area in three-hundredths of an inch, or 0 to leave it to the device.</param>
/// <param name="Height">The height of the area in three-hundredths of an inch, or 0 to leave it to the device.</param>
/// <param name="Version">The protocol version written into the request.</param>
/// <param name="Pages">How many sheets the feeder draws in, 0 for all; asked of the device only where it can draw a set number.</param>
public sealed record EsclScanRequest(EsclInput Input, string ColorMode, int Dpi, string Format, int Width, int Height, string Version, int Pages = 0)
{
    /// <summary>Chooses the request.</summary>
    /// <param name="caps">What the device offers.</param>
    /// <param name="settings">The user's settings.</param>
    /// <returns>The request.</returns>
    /// <exception cref="ScanException">The device has no input, no colour mode or no format the program reads.</exception>
    public static EsclScanRequest Choose(EsclCapabilities caps, ScanSettings settings)
    {
        var input = EsclCapabilities.InputOf(settings.Source) is { } wanted && caps.Inputs.ContainsKey(wanted) ? wanted
            : caps.Inputs.Keys.Order().Cast<EsclInput?>().FirstOrDefault()
              ?? throw new EsclProtocolException(Tr.T("Der Scanner bietet keine Quelle zum Scannen an."));
        var ic = caps.Inputs[input];
        var mode = ChooseMode(ic, settings.Mode) ?? throw new EsclProtocolException(Tr.T("Der Scanner kann das nicht."));
        var sets = ic.ResolutionsFor(mode);
        var choices = sets.SelectMany(s => s.Choices).Distinct().ToList();
        // a wish outside any sensible value (none of the program's choices would give it) becomes 300 dpi
        var wished = settings.Dpi is > 0 and <= ResolutionChoices.Max ? settings.Dpi : 300;
        var dpi = sets.Any(s => s.Allows(wished)) || choices.Count == 0 ? wished
            : choices.OrderBy(r => Math.Abs(r - wished)).ThenByDescending(r => r).First();
        var width = Area(settings.WidthMm, ic.MinWidth, ic.MaxWidth);
        var height = Area(settings.HeightMm, ic.MinHeight, ic.MaxHeight);
        var format = ChooseFormat(ic, mode, dpi, width, height) ?? throw new EsclProtocolException(Tr.T("Der Scanner liefert ein unbekanntes Bildformat."));
        return new EsclScanRequest(input, mode, dpi, format, width, height, RequestVersion(caps.Version));
    }

    /// <summary>The device's name for a colour mode: 8 bits per sample preferred; black and white only with a lossless format, else grey.</summary>
    private static string? ChooseMode(EsclInputCaps caps, ScanColorMode wanted)
    {
        string? Find(params string[] names) =>
            names.Select(n => caps.ColorModes.FirstOrDefault(m => m.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(m => m is not null);
        string? Any(ScanColorMode mode) => caps.ColorModes.FirstOrDefault(m => EsclCapabilities.ModeOf(m) == mode);
        var lossless = caps.Formats.Any(f => EsclCapabilities.LosslessFormats.Contains(f));
        return wanted switch
        {
            ScanColorMode.BlackWhite when lossless && Find("BlackAndWhite1") is { } bw => bw,
            ScanColorMode.BlackWhite or ScanColorMode.Gray => Find("Grayscale8") ?? Any(ScanColorMode.Gray) ?? Find("RGB24") ?? Any(ScanColorMode.Color),
            _ => Find("RGB24") ?? Any(ScanColorMode.Color) ?? Find("Grayscale8") ?? Any(ScanColorMode.Gray),
        } ?? (caps.ColorModes.Count > 0 ? null : wanted == ScanColorMode.Color ? "RGB24" : "Grayscale8");
    }

    /// <summary>
    /// The transfer format: PNG for black and white; PNG for grey where offered and the page fits the PNG reader; otherwise JPEG, which
    /// every device offers.
    /// </summary>
    private static string? ChooseFormat(EsclInputCaps caps, string mode, int dpi, int width, int height)
    {
        var png = caps.Formats.Contains("image/png");
        var jpeg = caps.Formats.Contains("image/jpeg") || caps.Formats.Contains("image/jpg") || caps.Formats.Count == 0;
        var w = (width > 0 ? width : caps.MaxWidth) / 300.0 * dpi;
        var h = (height > 0 ? height : caps.MaxHeight) / 300.0 * dpi;
        var fits = w * h <= Imaging.PngDecoder.MaxPixels;
        return EsclCapabilities.ModeOf(mode) switch
        {
            ScanColorMode.BlackWhite when png && fits => "image/png",
            ScanColorMode.Gray when png && fits => "image/png",
            _ when jpeg => "image/jpeg",
            _ when png && fits => "image/png",
            _ => null,
        };
    }

    private static int Area(double? mm, int min, int max)
    {
        var wanted = mm is > 0 and < 100_000 ? (int)Math.Round(mm.Value / 25.4 * 300) : 0;
        if (max <= 0) return wanted;
        if (wanted == 0) return max;
        // a device may name a smallest size larger than its largest (Fehlergedächtnis 958): then the largest counts
        return Math.Clamp(wanted, Math.Min(Math.Max(1, min), max), max);
    }

    // the device's own version where it is a 2.x one, else 2.0, the lowest the protocol allows in a request
    private static string RequestVersion(string? device) =>
        decimal.TryParse(device, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v >= 2 && v < 3
            ? v.ToString("0.0#", CultureInfo.InvariantCulture) : "2.0";

    /// <summary>
    /// The next request to try when the device refuses this one: JPEG instead of PNG, and grey instead of black and white with it (one bit
    /// per pixel needs a lossless format). Null when there is nothing left to give way on.
    /// </summary>
    /// <param name="caps">What the device offers.</param>
    public EsclScanRequest? Fallback(EsclCapabilities caps)
    {
        if (Format != "image/png") return null;
        var mode = EsclCapabilities.ModeOf(ColorMode) == ScanColorMode.BlackWhite
            ? caps.Inputs[Input].ColorModes.FirstOrDefault(m => EsclCapabilities.ModeOf(m) == ScanColorMode.Gray) ?? "Grayscale8"
            : ColorMode;
        return this with { Format = "image/jpeg", ColorMode = mode };
    }

    /// <summary>The request as the device expects it.</summary>
    public byte[] ToXml()
    {
        static string E(string text) => SecurityElement.Escape(text);
        var b = new StringBuilder();
        b.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        b.Append($"<scan:ScanSettings xmlns:scan=\"{Scan}\" xmlns:pwg=\"{Pwg}\">\n");
        b.Append($"  <pwg:Version>{E(Version)}</pwg:Version>\n");
        if (Width > 0 && Height > 0)
            b.Append("  <pwg:ScanRegions>\n    <pwg:ScanRegion>\n")
                .Append($"      <pwg:Height>{Height.ToString(CultureInfo.InvariantCulture)}</pwg:Height>\n")
                .Append("      <pwg:ContentRegionUnits>escl:ThreeHundredthsOfInches</pwg:ContentRegionUnits>\n")
                .Append($"      <pwg:Width>{Width.ToString(CultureInfo.InvariantCulture)}</pwg:Width>\n")
                .Append("      <pwg:XOffset>0</pwg:XOffset>\n      <pwg:YOffset>0</pwg:YOffset>\n")
                .Append("    </pwg:ScanRegion>\n  </pwg:ScanRegions>\n");
        b.Append($"  <pwg:InputSource>{(Input == EsclInput.Platen ? "Platen" : "Feeder")}</pwg:InputSource>\n");
        if (Input != EsclInput.Platen) b.Append($"  <scan:Duplex>{(Input == EsclInput.FeederDuplex ? "true" : "false")}</scan:Duplex>\n");
        if (Input != EsclInput.Platen && Pages > 0) b.Append($"  <scan:NumberOfPages>{Pages.ToString(CultureInfo.InvariantCulture)}</scan:NumberOfPages>\n");
        b.Append($"  <scan:ColorMode>{E(ColorMode)}</scan:ColorMode>\n");
        b.Append($"  <scan:XResolution>{Dpi.ToString(CultureInfo.InvariantCulture)}</scan:XResolution>\n");
        b.Append($"  <scan:YResolution>{Dpi.ToString(CultureInfo.InvariantCulture)}</scan:YResolution>\n");
        // both elements: older devices know only the first, newer ones expect the second
        b.Append($"  <pwg:DocumentFormat>{E(Format)}</pwg:DocumentFormat>\n");
        b.Append($"  <scan:DocumentFormatExt>{E(Format)}</scan:DocumentFormatExt>\n");
        b.Append("</scan:ScanSettings>\n");
        return Encoding.UTF8.GetBytes(b.ToString());
    }

    /// <summary>The namespace of the scan elements.</summary>
    public const string Scan = "http://schemas.hp.com/imaging/escl/2011/05/03";

    /// <summary>The namespace of the elements from the semantic model of the printer working group.</summary>
    public const string Pwg = "http://www.pwg.org/schemas/2010/12/sm";
}
