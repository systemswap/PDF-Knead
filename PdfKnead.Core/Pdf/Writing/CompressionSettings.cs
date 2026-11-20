using System.Text.Json.Serialization;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>Presets for File → Compress. See KONZEPT.md, section 5.8.</summary>
public enum CompressionPreset
{
    /// <summary>E-mail and screen: images at 120 dpi, JPEG at medium quality.</summary>
    Screen,

    /// <summary>Reading on a tablet, still printable: 150 dpi, good quality.</summary>
    EBook,

    /// <summary>Printing in good quality: 300 dpi, high quality.</summary>
    Print,

    /// <summary>Nothing changes in appearance: lossless steps only.</summary>
    Lossless,

    /// <summary>Everything set by the user.</summary>
    Custom,
}

/// <summary>
/// How to compress. Always lossless: merging duplicates, removing ballast (thumbnails, private data of other
/// applications), the strongest Flate level, object streams. Lossy steps only if <see cref="ImageDpi"/> is set.
/// </summary>
/// <param name="Preset">The preset these settings came from.</param>
/// <param name="ImageDpi">Downsample images (colour and grey) above 1.5 times this resolution to it; null = do not downsample
/// and use no lossy steps.</param>
/// <param name="JpegQuality">Quality when repacking as JPEG (30 … 98).</param>
/// <param name="BilevelScans">Store scans that are really only black and white as 1-bit images (<see cref="BilevelCodec"/>);
/// lossless only for pure black and white.</param>
/// <param name="SubsetFonts">Subset embedded TrueType fonts to the characters used.</param>
/// <param name="Brotli">Pack with Brotli instead of Flate (smaller, but many readers cannot open it yet).</param>
/// <param name="Graphics">Graphics and screenshots (images with few colours, not JPEG): Flate with predictor, or palette.</param>
/// <param name="PaletteColors">Palette: at most this many colours (2 … 256).</param>
/// <param name="PaletteDither">Palette: dithering (Floyd–Steinberg) in per cent, 0 = off. Prevents banding in gradients but makes
/// the file considerably larger (screenshot with a gradient: 1.9 KB without, 5.7 KB with 50 %), hence off by default.</param>
/// <param name="PaletteQuality">Palette: minimum quality 0 … 100; if it is not reached, the image stays lossless (<see cref="MinimumPsnr"/>).</param>
/// <param name="Scans">Colour and greyscale scans: like photos, or MRC.</param>
/// <param name="MrcTextDpi">MRC: resolution of the text masks; a sharper scan is downsampled to it. Down to half of it, the masks
/// are computed at double resolution (300 dpi scan at 600: smooth edges).</param>
/// <param name="MrcBackgroundDpi">MRC: resolution of the background.</param>
/// <param name="MrcBackgroundQuality">MRC: JPEG quality of the background.</param>
/// <param name="MrcSensitivity">MRC: text detection 0 … 100 (higher finds paler text but more readily picks up specks).</param>
/// <param name="MrcColors">MRC: at most this many text colours (1 = all text in one colour).</param>
/// <param name="BilevelCodec">Black-and-white images and MRC masks: CCITT G4 or JBIG2 (lossless, about one third smaller).</param>
/// <param name="MrcFlattenPaper">MRC: flatten the paper. Whatever is close to the paper tone (noise, show-through from the
/// back) becomes a uniform paper tone; stamps and tinted fields stay. On by default.</param>
/// <param name="StrongFlate">"Flate – strong (slower)": own, more strongly packing Deflate (<see cref="Filters.StrongDeflate"/>, at the
/// level of the procedure it follows). About 10 % smaller Flate data, packing takes about five times as long; every reader can
/// read it. No effect together with Brotli.</param>
/// <remarks>Ranges are enforced by <see cref="Checked"/>.</remarks>
public sealed record CompressionSettings(
    CompressionPreset Preset = CompressionPreset.EBook,
    int? ImageDpi = 150,
    int JpegQuality = 80,
    bool BilevelScans = true,
    bool SubsetFonts = true,
    bool Brotli = false,
    GraphicsMethod Graphics = GraphicsMethod.Palette,
    int PaletteColors = 256,
    int PaletteDither = 0,
    int PaletteQuality = 80,
    ScanMethod Scans = ScanMethod.Mrc,
    int MrcTextDpi = 600,
    int MrcBackgroundDpi = 100,
    int MrcBackgroundQuality = 60,
    int MrcSensitivity = 50,
    int MrcColors = 4,
    BilevelMethod BilevelCodec = BilevelMethod.Jbig2,
    bool MrcFlattenPaper = true,
    bool StrongFlate = false)
{
    /// <summary>Minimum PSNR for a palette with colour reduction.</summary>
    /// <param name="quality">The palette quality 0 … 100; clamped.</param>
    /// <returns>The PSNR in dB: quality 0 → 26 dB, 80 → 40.4 dB, 100 → 44 dB.</returns>
    public static double MinimumPsnr(int quality) => 26 + 0.18 * Math.Clamp(quality, 0, 100);

    /// <summary>Images are downsampled only when they have 1.5 times the target resolution (as in common PDF editors; small excesses are not worth it).</summary>
    public const double DownsampleThreshold = 1.5;

    /// <summary>True if lossy steps are allowed (<see cref="ImageDpi"/> is set).</summary>
    [JsonIgnore]
    public bool Lossy => ImageDpi is not null;

    /// <summary>Packing kind for cache keys: 0 = Flate, 1 = strong Flate, 2 = Brotli.</summary>
    [JsonIgnore]
    public int PackKind => Brotli ? 2 : StrongFlate ? 1 : 0;

    /// <summary>The default (e-book): on first opening and for "Reset".</summary>
    public static CompressionSettings Default => For(CompressionPreset.EBook);

    /// <summary>Returns the settings of a preset.</summary>
    /// <param name="preset">The preset; unknown values give the e-book preset.</param>
    /// <returns>New settings.</returns>
    public static CompressionSettings For(CompressionPreset preset) => preset switch
    {
        CompressionPreset.Screen => new(preset, 120, 65, PaletteQuality: 70, MrcBackgroundDpi: 75, MrcBackgroundQuality: 50),
        CompressionPreset.Print => new(preset, 300, 90, Graphics: GraphicsMethod.Flate, Scans: ScanMethod.AsPhotos),
        CompressionPreset.Lossless => new(preset, null, 90, BilevelScans: true, Graphics: GraphicsMethod.Flate, Scans: ScanMethod.AsPhotos),
        CompressionPreset.Custom => new(preset),
        _ => new(CompressionPreset.EBook, 150, 80),
    };

    /// <summary>Clamps nonsensical values (old or corrupted settings) to valid ranges.</summary>
    /// <returns>A copy with valid values.</returns>
    public CompressionSettings Checked() => this with
    {
        Preset = Enum.IsDefined(Preset) ? Preset : CompressionPreset.EBook,
        ImageDpi = ImageDpi is { } dpi ? Math.Clamp(dpi, 36, 1200) : null,
        JpegQuality = Math.Clamp(JpegQuality, 30, 98),
        Graphics = Enum.IsDefined(Graphics) ? Graphics : GraphicsMethod.Flate,
        PaletteColors = Math.Clamp(PaletteColors, 2, 256),
        PaletteDither = Math.Clamp(PaletteDither, 0, 100),
        PaletteQuality = Math.Clamp(PaletteQuality, 0, 100),
        Scans = Enum.IsDefined(Scans) ? Scans : ScanMethod.AsPhotos,
        MrcTextDpi = Math.Clamp(MrcTextDpi, 150, 600),
        MrcBackgroundDpi = Math.Clamp(MrcBackgroundDpi, 50, 300),
        MrcBackgroundQuality = Math.Clamp(MrcBackgroundQuality, 30, 95),
        MrcSensitivity = Math.Clamp(MrcSensitivity, 0, 100),
        MrcColors = Math.Clamp(MrcColors, 1, 8),
        BilevelCodec = Enum.IsDefined(BilevelCodec) ? BilevelCodec : BilevelMethod.G4,
    };
}

/// <summary>Method for graphics and screenshots. See KONZEPT.md, section 5.12.</summary>
public enum GraphicsMethod
{
    /// <summary>Lossless: Flate with PNG predictor; images with at most 256 colours as an exact palette.</summary>
    Flate,

    /// <summary>Palette with colour reduction: more colours are also reduced to at most <see cref="CompressionSettings.PaletteColors"/>, with a minimum quality.</summary>
    Palette,
}

/// <summary>Method for black-and-white images and MRC masks; both are lossless. See KONZEPT.md, section 5.12.</summary>
public enum BilevelMethod
{
    /// <summary>CCITT Group 4 (the fax method).</summary>
    G4,

    /// <summary>JBIG2, generic region (arithmetic coding): the same bits, about one third smaller than G4.</summary>
    Jbig2,
}

/// <summary>Method for colour and greyscale scans. See KONZEPT.md, section 5.12.</summary>
public enum ScanMethod
{
    /// <summary>Like photos: downsampled and stored as JPEG.</summary>
    AsPhotos,

    /// <summary>MRC: text as sharp 1-bit masks per colour, the paper at low resolution as JPEG; only for detected text scans.</summary>
    Mrc,
}
