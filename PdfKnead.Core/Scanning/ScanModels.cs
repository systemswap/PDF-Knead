namespace PdfKnead.Core.Scanning;

/// <summary>A scanner as the system's scanner interface reports it.</summary>
/// <param name="Id">The name the interface opens it by (e.g. a SANE device name); stable between sessions as far as the driver keeps it.</param>
/// <param name="Vendor">The manufacturer as reported (may be empty).</param>
/// <param name="Model">The model as reported (may be empty).</param>
/// <param name="Kind">The device type as reported, e.g. „flatbed scanner“ (may be empty).</param>
/// <param name="Via">How the device is reached, shown when the same device appears more than once and the entries could not be combined.</param>
/// <param name="Alternates">
/// Further ways to the same device after <paramref name="Id"/>, the better first (ids, separated by <see cref="Separator"/>): the program
/// chose the best way itself and falls back on these silently (Festlegung: no one needs to know the drivers). A text, not a list,
/// so that two descriptions of the same device are equal.
/// </param>
/// <param name="Host">The short host name of a network scanner (without the domain), shown before the type: „PRN-2OG-01 – Beispiel Büro 1000“
/// (design decision, docs/FEHLERGEDAECHTNIS.md, No. 926); null if unknown. Not part of <see cref="BaseName"/>, which profiles and the joining
/// of the ways of a device compare.</param>
public sealed record ScanDevice(string Id, string Vendor, string Model, string Kind, string? Via = null, string? Alternates = null, string? Host = null)
{
    /// <summary>Between the ids in <see cref="Alternates"/> (a control character, never part of an id).</summary>
    public const char Separator = '\u001F';

    /// <summary>The further ways to the device, best first.</summary>
    public IReadOnlyList<string> AlternateIds => Alternates is { Length: > 0 } a ? a.Split(Separator) : [];

    /// <summary>Every way to the device, the best first.</summary>
    public IReadOnlyList<string> AllIds => [Id, .. AlternateIds];

    /// <summary>Whether <paramref name="id"/> is one of the ways to this device.</summary>
    public bool Has(string? id) => id is not null && (Id == id || AlternateIds.Contains(id));

    /// <summary>
    /// Combines entries that are one physical device: entries with the same key become one, the best (lowest rank) gives the id, name and
    /// type, the others become its <see cref="Alternates"/>; entries without a key stay as they are. The groups keep the order in which their
    /// first entry was found.
    /// </summary>
    /// <param name="found">The entries with the key of their device (null: cannot be combined) and the rank of their way.</param>
    /// <returns>One entry per device.</returns>
    public static List<ScanDevice> Combine(IEnumerable<(ScanDevice Device, string? Key, int Rank)> found)
    {
        var list = found.ToList();
        var done = new bool[list.Count];
        var result = new List<ScanDevice>();
        for (var i = 0; i < list.Count; i++)
        {
            if (done[i]) continue;
            var key = list[i].Key;
            var group = key is null ? [i] : Enumerable.Range(i, list.Count - i).Where(k => !done[k] && string.Equals(list[k].Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in group) done[k] = true;
            var ordered = group.OrderBy(k => list[k].Rank).ThenBy(k => k).Select(k => list[k].Device).ToList();
            result.Add(ordered.Count == 1 ? ordered[0]
                : ordered[0] with { Via = null, Alternates = string.Join(Separator, ordered.Skip(1).SelectMany(d => d.AllIds)) });
        }
        return result;
    }

    /// <summary>The name of the device: manufacturer and model, else the id.</summary>
    public string BaseName
    {
        get
        {
            // network back ends report the protocol as the vendor („eSCL“, „WSD“) and the full name as the model
            var vendor = Vendor.Trim() is var v && (v.Equals("eSCL", StringComparison.OrdinalIgnoreCase) || v.Equals("WSD", StringComparison.OrdinalIgnoreCase)) ? "" : v;
            var model = Model.Trim();
            var name = vendor.Length > 0 && model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? model   // „HP“ + „HP Color …“
                : string.Join(' ', new[] { vendor, model }.Where(s => s.Length > 0));
            return name.Length > 0 ? name : Id;
        }
    }

    /// <summary>
    /// The name for the user: <see cref="BaseName"/>, the host name before it if known, with the way it is reached if the device appears more
    /// than once.
    /// </summary>
    public string DisplayName
    {
        get
        {
            var name = Host is { Length: > 0 } host && !string.Equals(host, BaseName, StringComparison.OrdinalIgnoreCase) ? $"{host} – {BaseName}" : BaseName;
            return Via is { Length: > 0 } via ? $"{name} ({via})" : name;
        }
    }

    /// <summary>
    /// Leaves out network scanners whose address belongs to this computer: a scanner that this computer shares in the network (its own eSCL
    /// service for a USB scanner) would otherwise appear twice, once directly and once through the network.
    /// </summary>
    /// <param name="devices">The devices as found.</param>
    /// <param name="ownAddresses">The addresses of this computer.</param>
    /// <returns>The devices without the computer's own shares.</returns>
    public static List<ScanDevice> WithoutOwnShares(IEnumerable<ScanDevice> devices, IReadOnlyCollection<System.Net.IPAddress> ownAddresses) =>
        [.. devices.Where(d => !Addresses(d).Any(a => ownAddresses.Any(own => own.Equals(a))))];

    /// <summary>The IP addresses a network back end names in a device's id or type („escl:https://192.0.2.1:443“, „ip=192.0.2.1“).</summary>
    internal static IEnumerable<System.Net.IPAddress> Addresses(ScanDevice device)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(device.Id + " " + device.Kind,
                     @"(?<![\d.])(\d{1,3}(?:\.\d{1,3}){3})(?![\d.])|\[([0-9a-fA-F:]+)\]"))
            if (System.Net.IPAddress.TryParse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, out var address)) yield return address;
    }

    /// <summary>
    /// SANE: one entry for a device that comes through several back ends (the part of the id before the colon), the best way first: of the two
    /// back ends for network scanners, „airscan“ reports the resolutions of the feeder as the device does, „escl“ (as of sane-backends 1.3)
    /// offers those of the flatbed for the feeder too. A name is combined only when no back end lists it twice — two devices of the same model
    /// stay apart, then marked with their back end (<see cref="Via"/>).
    /// </summary>
    /// <param name="devices">The devices as found.</param>
    /// <returns>One entry per device where it is certain, the others distinguishable.</returns>
    public static List<ScanDevice> CombineBackEnds(IEnumerable<ScanDevice> devices)
    {
        var list = devices.ToList();
        static string BackEnd(ScanDevice d) => d.Id.Split(':')[0];
        static int Rank(ScanDevice d) => BackEnd(d) switch { "airscan" => 0, "escl" => 2, _ => 1 };
        var combinable = list.GroupBy(d => d.BaseName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1 && g.GroupBy(BackEnd).All(b => b.Count() == 1)).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var combined = Combine(list.Select(d => (d, combinable.Contains(d.BaseName) ? d.BaseName : null, Rank(d))));
        var repeated = combined.GroupBy(d => d.BaseName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. combined.Select(d => repeated.Contains(d.BaseName) ? d with { Via = BackEnd(d) } : d)];
    }
}

/// <summary>Where the paper comes from.</summary>
public enum ScanSourceKind
{
    /// <summary>The glass plate.</summary>
    Flatbed,
    /// <summary>The automatic document feeder, front sides only.</summary>
    Feeder,
    /// <summary>The automatic document feeder, both sides in one pass.</summary>
    FeederDuplex,
    /// <summary>Anything else the driver offers (film, transparency unit …).</summary>
    Other,
}

/// <summary>A source as the driver names it, with what kind it is.</summary>
/// <param name="Name">The value the driver expects (e.g. „ADF Duplex“); never translated.</param>
/// <param name="Kind">The kind, recognised from the name.</param>
public sealed record ScanSource(string Name, ScanSourceKind Kind)
{
    /// <summary>
    /// Appended to the feeder's name for „both sides“ where the driver switches it with an option instead of offering a source of its own;
    /// the scanner interface sets the feeder and the option then. A control character, so it never collides with a driver's name.
    /// </summary>
    public const string DuplexMarker = "\u001Fduplex";

    /// <summary>Recognises the kind from a driver's source name (SANE leaves the names to each back end).</summary>
    /// <param name="name">The source name.</param>
    /// <returns>The source.</returns>
    public static ScanSource FromName(string name)
    {
        // back ends name their sources freely and some translate them: English, German, French, Italian and Spanish words count
        var n = name.ToLowerInvariant();
        var feeder = n.Contains("adf") || n.Contains("feeder") || n.Contains("einzug") || n.Contains("automatic document") || n.Contains("chargeur")
            || n.Contains("alimentat") || n.Contains("alimentador") || n.Contains("caricatore");
        var duplex = n.Contains("duplex") || n.Contains("both") || n.Contains("beidseitig") || n.Contains("recto verso") || n.Contains("recto-verso")
            || n.Contains("fronte-retro") || n.Contains("doble cara");
        var kind = feeder && duplex ? ScanSourceKind.FeederDuplex
            : feeder ? ScanSourceKind.Feeder
            : n.Contains("flatbed") || n.Contains("platen") || n.Contains("glass") || n.Contains("flachbett") || n.Contains("normal") || n.Contains("vitre")
              || n.Contains("à plat") || n.Contains("piano") || n.Contains("cristal") || n.Contains("plana") ? ScanSourceKind.Flatbed
            : ScanSourceKind.Other;
        return new ScanSource(name, kind);
    }

    /// <summary>The name for the user.</summary>
    public string DisplayName => Kind switch
    {
        ScanSourceKind.Flatbed => Tr.T("Flachbett"),
        ScanSourceKind.Feeder => Tr.T("Einzug"),
        ScanSourceKind.FeederDuplex => Tr.T("Einzug beidseitig"),
        _ => Name,
    };
}

/// <summary>How the scanner reads the colours.</summary>
public enum ScanColorMode
{
    /// <summary>Colour.</summary>
    Color,
    /// <summary>Greyscale.</summary>
    Gray,
    /// <summary>Black and white (one bit per pixel at the scanner).</summary>
    BlackWhite,
}

/// <summary>What the opened scanner offers for the selected source (sources change resolutions, colours and area).</summary>
/// <param name="Sources">All sources of the device; empty if the driver offers no choice.</param>
/// <param name="Source">The selected source; null if the driver offers no choice.</param>
/// <param name="Resolutions">The resolutions in dpi, ascending, as the device offers them for this source.</param>
/// <param name="ColorModes">The colour modes the device offers.</param>
/// <param name="MaxWidthMm">The widest area in millimetres.</param>
/// <param name="MaxHeightMm">The tallest area in millimetres (a feeder often allows longer paper than the glass).</param>
public sealed record ScanCapabilities(
    IReadOnlyList<ScanSource> Sources,
    ScanSource? Source,
    IReadOnlyList<int> Resolutions,
    IReadOnlyList<ScanColorMode> ColorModes,
    double MaxWidthMm,
    double MaxHeightMm)
{
    /// <summary>The resolution that is allowed and closest to <paramref name="wanted"/> (ties go to the higher one).</summary>
    /// <param name="wanted">The wished resolution in dpi.</param>
    /// <returns>The resolution, or <paramref name="wanted"/> if the device names none.</returns>
    public int ClosestResolution(int wanted) =>
        Resolutions.Count == 0 ? wanted : Resolutions.OrderBy(r => Math.Abs(r - wanted)).ThenByDescending(r => r).First();
}

/// <summary>What to scan with.</summary>
/// <param name="Source">The source name as the driver expects it; null keeps the device's choice.</param>
/// <param name="Dpi">The resolution in dpi.</param>
/// <param name="Mode">The colour mode.</param>
/// <param name="WidthMm">The width of the area from the left edge in millimetres; null for the whole width.</param>
/// <param name="HeightMm">The height of the area from the top edge in millimetres; null for the whole height.</param>
public sealed record ScanSettings(string? Source, int Dpi, ScanColorMode Mode, double? WidthMm = null, double? HeightMm = null);

/// <summary>
/// One scanned page in 8 bits per sample: greyscale (one byte per pixel) or RGB (three bytes per pixel), rows without padding. Black and
/// white from the scanner arrives as greyscale with 0 and 255; deeper samples are reduced to 8 bits.
/// </summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="Channels">1 (grey) or 3 (RGB).</param>
/// <param name="Dpi">The resolution the scanner used.</param>
/// <param name="Pixels">The samples, <c>Width × Height × Channels</c> bytes.</param>
/// <param name="BlackWhite">Whether the scanner delivered one bit per pixel (only 0 and 255 occur).</param>
public sealed record ScannedImage(int Width, int Height, int Channels, int Dpi, byte[] Pixels, bool BlackWhite = false)
{
    /// <summary>The page width in PDF points.</summary>
    public double WidthPoints => Width * 72.0 / Math.Max(1, Dpi);

    /// <summary>The page height in PDF points.</summary>
    public double HeightPoints => Height * 72.0 / Math.Max(1, Dpi);
}

/// <summary>What a scan job is doing, for the user (network scanners say little about a sheet while they read it).</summary>
public enum ScanPhase
{
    /// <summary>The scanner reads the sheet.</summary>
    Reading,
    /// <summary>The scanner is still busy (with a job just ended or another program's); the job waits for it.</summary>
    Waiting,
    /// <summary>The page is on its way to the program.</summary>
    Transferring,
    /// <summary>The page has arrived and is unpacked.</summary>
    Processing,
    /// <summary>All pages are in; the scanner closes the job.</summary>
    Finishing,
}

/// <summary>How far a scan job is.</summary>
/// <param name="Page">The page being read (1-based).</param>
/// <param name="Fraction">How much of this page has arrived, 0 … 1; negative if the driver does not know the length.</param>
/// <param name="Phase">What the job is doing.</param>
/// <param name="Bytes">How many bytes of the page have arrived (while it is transferred), 0 if not told.</param>
public readonly record struct ScanProgress(int Page, double Fraction, ScanPhase Phase = ScanPhase.Reading, long Bytes = 0);

/// <summary>A scanner could not be reached or reported an error; the message is for the user (translated).</summary>
/// <param name="message">What to tell the user.</param>
/// <param name="kind">What happened, for decisions in the program.</param>
public class ScanException(string message, ScanErrorKind kind = ScanErrorKind.Other) : Exception(message)
{
    /// <summary>What happened.</summary>
    public ScanErrorKind Kind { get; } = kind;
}

/// <summary>Kinds of scan errors the program reacts to.</summary>
public enum ScanErrorKind
{
    /// <summary>Any other error.</summary>
    Other,
    /// <summary>The feeder is empty.</summary>
    NoPaper,
    /// <summary>Paper jam.</summary>
    Jammed,
    /// <summary>A cover is open.</summary>
    CoverOpen,
    /// <summary>The device is used by another program.</summary>
    Busy,
    /// <summary>The scanner interface of the system is missing.</summary>
    InterfaceMissing,
    /// <summary>The device cannot be reached (switched off, gone from the network, or known under a new id).</summary>
    Offline,
    /// <summary>The helper process with the driver has crashed or hung and was ended.</summary>
    DriverCrashed,
}
