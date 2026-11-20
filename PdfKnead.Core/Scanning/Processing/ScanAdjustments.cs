namespace PdfKnead.Core.Scanning.Processing;

/// <summary>
/// The processing of one scanned page (KONZEPT §5.25, „Nachbearbeitung“): a recipe applied to the raw scan, which stays — every value can be
/// changed or taken back later. For every slider 0 means unchanged.
/// </summary>
/// <param name="AutoEnhance">„Automatisch verbessern“: the paper becomes white (also in shadows and at the spine), the print black; off by
/// default (the user starts from the raw scan).</param>
/// <param name="Brightness">Helligkeit −100 … +100 (gamma on the midtones; black and white stay).</param>
/// <param name="Luminance">Luminanz −100 … +100 (like more or less light: everything in the same ratio, white runs out softly); suits colour
/// photos best, for text the brightness is usually the better choice — both work on every page.</param>
/// <param name="Contrast">Kontrast −100 … +100 (S-curve around the middle).</param>
/// <param name="Sharpen">Schärfen 0 … 100 (unsharp mask; flat areas and noise below a small difference stay).</param>
/// <param name="ShowThrough">Durchscheinen verhindern 0 … 100 (pale, colourless tones close to the paper are lifted to it).</param>
/// <param name="BlackWhite">Schwarzweiß: the page becomes black and white (small files, sharp text).</param>
/// <param name="Threshold">Schwelle for black and white 0 … 100: a point darker than this share of white becomes black (higher: more black).</param>
/// <param name="Saturation">Sättigung −100 … +100 (all colours stronger or paler; −100 is grey).</param>
/// <param name="Vibrance">Dynamik −100 … +100 (pale colours much stronger, strong ones hardly, skin tones spared).</param>
/// <param name="Grayscale">Graustufen: the colour is taken out; the page is handed over with one channel (smaller files).</param>
/// <param name="Straighten">The key of the straightening method (<see cref="Straighteners"/>): „Schräglage ausgleichen“ by default.</param>
/// <param name="CropBorders">„Ränder abschneiden“: dark borders (edge of the glass, lid) are cut away.</param>
public sealed record ScanAdjustments(bool AutoEnhance = false, double Brightness = 0, double Luminance = 0, double Contrast = 0,
    double Sharpen = 0, double ShowThrough = 0, bool BlackWhite = false, double Threshold = 50, double Saturation = 0, double Vibrance = 0,
    bool Grayscale = false, string Straighten = Straighteners.Skew, bool CropBorders = true)
{
    /// <summary>Nothing changed: the raw scan as it came.</summary>
    public static readonly ScanAdjustments None = new(AutoEnhance: false, Straighten: Straighteners.None, CropBorders: false);

    /// <summary>Whether the light and colour stay as scanned (the straightening aside).</summary>
    public bool LightIsNone => (this with { Straighten = Straighteners.None, CropBorders = false }).IsNone;

    /// <summary>Whether the page stays as scanned.</summary>
    public bool IsNone => !AutoEnhance && Brightness == 0 && Luminance == 0 && Contrast == 0 && Sharpen == 0 && ShowThrough == 0 && !BlackWhite
        && Saturation == 0 && Vibrance == 0 && !Grayscale && Straighteners.For(Straighten).Key == Straighteners.None && !CropBorders;

    /// <summary>The values inside their ranges (a value from a file is never trusted as it is).</summary>
    /// <returns>The checked values.</returns>
    public ScanAdjustments Normalized() => this with
    {
        Brightness = Clamp(Brightness, -100, 100),
        Luminance = Clamp(Luminance, -100, 100),
        Contrast = Clamp(Contrast, -100, 100),
        Sharpen = Clamp(Sharpen, 0, 100),
        ShowThrough = Clamp(ShowThrough, 0, 100),
        Saturation = Clamp(Saturation, -100, 100),
        Vibrance = Clamp(Vibrance, -100, 100),
        Threshold = double.IsFinite(Threshold) ? Math.Clamp(Threshold, 0, 100) : 50,
        Straighten = Straighteners.For(Straighten).Key,
    };

    private static double Clamp(double value, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : 0;
}
