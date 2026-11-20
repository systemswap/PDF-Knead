using System.Xml.Linq;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>The inputs of an eSCL scanner.</summary>
public enum EsclInput
{
    /// <summary>The glass plate.</summary>
    Platen,
    /// <summary>The feeder, front sides.</summary>
    Feeder,
    /// <summary>The feeder, both sides in one pass (only where the device describes this input of its own).</summary>
    FeederDuplex,
}

/// <summary>A range of resolutions in dpi.</summary>
/// <param name="Min">The lowest.</param>
/// <param name="Max">The highest.</param>
/// <param name="Step">The step between allowed values; 0 or 1 = any.</param>
public sealed record EsclResolutionRange(int Min, int Max, int Step);

/// <summary>The resolutions a device names for an input — for one colour mode or, without one, for all.</summary>
/// <param name="ColorMode">The colour mode as the device names it, or null for all.</param>
/// <param name="Values">Single resolutions in dpi (same in both directions).</param>
/// <param name="Ranges">Ranges.</param>
public sealed record EsclResolutionSet(string? ColorMode, IReadOnlyList<int> Values, IReadOnlyList<EsclResolutionRange> Ranges)
{
    /// <summary>The choices for the user (<see cref="ResolutionChoices"/>).</summary>
    public IEnumerable<int> Choices => ResolutionChoices.FromList(Values).Concat(Ranges.SelectMany(r => ResolutionChoices.FromRange(r.Min, r.Max, r.Step)));

    /// <summary>Whether <paramref name="dpi"/> is allowed.</summary>
    public bool Allows(int dpi) => Values.Contains(dpi) || Ranges.Any(r => dpi >= r.Min && dpi <= r.Max && (r.Step <= 1 || (dpi - r.Min) % r.Step == 0));
}

/// <summary>What a device offers for one input; sizes in three-hundredths of an inch, 0 where the device does not say.</summary>
/// <param name="MinWidth">The narrowest area.</param>
/// <param name="MaxWidth">The widest area.</param>
/// <param name="MinHeight">The shortest area.</param>
/// <param name="MaxHeight">The longest area.</param>
/// <param name="ColorModes">The colour modes as the device names them (e.g. „RGB24“).</param>
/// <param name="Formats">The transfer formats as media types in lower case (e.g. „image/jpeg“).</param>
/// <param name="Resolutions">The resolutions, per colour mode where the device distinguishes.</param>
public sealed record EsclInputCaps(int MinWidth, int MaxWidth, int MinHeight, int MaxHeight,
    IReadOnlyList<string> ColorModes, IReadOnlyList<string> Formats, IReadOnlyList<EsclResolutionSet> Resolutions)
{
    /// <summary>The resolution sets that apply to a colour mode (those for exactly this mode, else those for all).</summary>
    /// <param name="colorMode">The colour mode as the device names it.</param>
    public IReadOnlyList<EsclResolutionSet> ResolutionsFor(string colorMode)
    {
        var own = Resolutions.Where(r => string.Equals(r.ColorMode, colorMode, StringComparison.OrdinalIgnoreCase)).ToList();
        return own.Count > 0 ? own : [.. Resolutions.Where(r => r.ColorMode is null)];
    }
}

/// <summary>
/// The capabilities of an eSCL scanner as read from its answer (KONZEPT.md §5.25, S9b). Every field is optional; what cannot be read is left
/// out and named in <see cref="Unknown"/> for the diagnosis log.
/// </summary>
/// <param name="Version">The protocol version the device names.</param>
/// <param name="MakeAndModel">Manufacturer and model.</param>
/// <param name="SerialNumber">The serial number.</param>
/// <param name="Uuid">The device's UUID (normalised as in the search).</param>
/// <param name="Inputs">The inputs with what they offer.</param>
/// <param name="FeederCapacity">How many sheets the feeder holds, 0 if not said.</param>
/// <param name="FeederOptions">What the feeder can do, as the device names it (e.g. „DetectPaperLoaded“).</param>
/// <param name="Unknown">Values that were not understood or not resolvable.</param>
public sealed record EsclCapabilities(string? Version, string? MakeAndModel, string? SerialNumber, string? Uuid,
    IReadOnlyDictionary<EsclInput, EsclInputCaps> Inputs, int FeederCapacity, IReadOnlyList<string> FeederOptions, IReadOnlyList<string> Unknown)
{
    /// <summary>The largest answer accepted (1 MB): real devices send a few to some tens of kilobytes.</summary>
    public const int MaxBytes = 1 << 20;

    private const int MaxProfiles = 64, MaxValues = 256, MaxDimension = 300 * 1000;   // 1000 inches is far beyond any scanner

    /// <summary>The source names the scanner session uses for the inputs (<see cref="ScanSource.Name"/>).</summary>
    public static string SourceName(EsclInput input) => input switch
    {
        EsclInput.Platen => "Platen",
        EsclInput.Feeder => "Feeder",
        _ => "Feeder" + ScanSource.DuplexMarker,
    };

    /// <summary>The input for a source name, or null if it is not one of <see cref="SourceName"/>.</summary>
    public static EsclInput? InputOf(string? source) =>
        Enum.GetValues<EsclInput>().Cast<EsclInput?>().FirstOrDefault(i => SourceName(i!.Value) == source);

    /// <summary>The program's colour mode for a device's mode name, or null if unknown.</summary>
    public static ScanColorMode? ModeOf(string name) =>
        name.StartsWith("RGB", StringComparison.OrdinalIgnoreCase) ? ScanColorMode.Color
        : name.StartsWith("Grayscale", StringComparison.OrdinalIgnoreCase) ? ScanColorMode.Gray
        : name.Equals("BlackAndWhite1", StringComparison.OrdinalIgnoreCase) ? ScanColorMode.BlackWhite
        : null;

    /// <summary>
    /// The formats in which the program reads one bit per pixel without loss. Black and white straight from the device is offered only with
    /// one of them; otherwise the device scans grey and the program sets the threshold (S3).
    /// </summary>
    public static readonly IReadOnlyList<string> LosslessFormats = ["image/png"];

    /// <summary>
    /// What the program offers for an input: its sources, the resolutions over all colour modes, colour, grey and — where it can be read
    /// without loss — black and white, the largest area in millimetres (0 where the device does not say).
    /// </summary>
    /// <param name="input">The selected input; the first one the device has if it lacks this one.</param>
    /// <returns>The capabilities.</returns>
    public ScanCapabilities ToScanCapabilities(EsclInput? input)
    {
        var sources = Inputs.Keys.Order().Select(i => new ScanSource(SourceName(i), i switch
        {
            EsclInput.Platen => ScanSourceKind.Flatbed,
            EsclInput.Feeder => ScanSourceKind.Feeder,
            _ => ScanSourceKind.FeederDuplex,
        })).ToList();
        if (sources.Count == 0) return new ScanCapabilities([], null, [], [], 0, 0);
        var chosen = input is { } i0 && Inputs.ContainsKey(i0) ? i0 : Inputs.Keys.Order().First();
        var caps = Inputs[chosen];
        var modes = new List<ScanColorMode>();
        foreach (var m in caps.ColorModes)
            if (ModeOf(m) is { } mode && !modes.Contains(mode)
                && (mode != ScanColorMode.BlackWhite || caps.Formats.Any(f => LosslessFormats.Contains(f))))
                modes.Add(mode);
        return new ScanCapabilities(sources, sources.First(s => s.Name == SourceName(chosen)),
            [.. caps.Resolutions.SelectMany(r => r.Choices).Distinct().Order()], [.. modes.Order()],
            caps.MaxWidth * 25.4 / 300, caps.MaxHeight * 25.4 / 300);
    }

    /// <summary>Reads the capabilities from a device's answer.</summary>
    /// <param name="data">The answer as received.</param>
    /// <returns>The capabilities, or null if the answer is too large, not XML, or not a capabilities document.</returns>
    public static EsclCapabilities? Read(ReadOnlySpan<byte> data) => EsclXml.Parse(data, MaxBytes) is { } root ? Read(root) : null;

    /// <summary>Reads the capabilities from the root element.</summary>
    public static EsclCapabilities? Read(XElement root)
    {
        if (!root.Is("ScannerCapabilities")) return null;
        var unknown = new List<string>();
        // profiles defined once and referenced by name from the inputs
        var shared = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var p in root.Child("SettingProfiles").Children("SettingProfile").Take(MaxProfiles))
            if (p.AttributeValue("name") is { Length: > 0 } name) shared.TryAdd(name, p);
        var inputs = new Dictionary<EsclInput, EsclInputCaps>();
        void Add(EsclInput input, XElement? element)
        {
            if (element is not null && ReadInput(element, shared, unknown) is { } caps) inputs[input] = caps;
        }
        Add(EsclInput.Platen, root.Child("Platen").Child("PlatenInputCaps") ?? root.Child("PlatenInputCaps"));
        var adf = root.Child("Adf");
        Add(EsclInput.Feeder, adf.Child("AdfSimplexInputCaps"));
        Add(EsclInput.FeederDuplex, adf.Child("AdfDuplexInputCaps"));
        if (inputs.ContainsKey(EsclInput.FeederDuplex) && !inputs.ContainsKey(EsclInput.Feeder))
        {
            unknown.Add("Einzug beidseitig ohne einseitigen Einzug");
            inputs[EsclInput.Feeder] = inputs[EsclInput.FeederDuplex];
        }
        var options = adf.Child("AdfOptions").Children("AdfOption").Select(o => o.Value.Trim()).Where(o => o.Length is > 0 and <= 64)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
        return new EsclCapabilities(Short(root.Text("Version")), root.Text("MakeAndModel") is { } model ? EsclXml.Clean(model) : null, Short(root.Text("SerialNumber")),
            root.Text("UUID") is { } uuid ? EsclSearch.NormalizeUuid(uuid) : null, inputs,
            Math.Clamp(adf.Int("FeederCapacity") ?? 0, 0, 100_000), options, unknown);
    }

    private static string? Short(string? text) => text is null ? null : text.Length <= 256 ? text : text[..256];

    private static EsclInputCaps? ReadInput(XElement element, Dictionary<string, XElement> shared, List<string> unknown)
    {
        var modes = new List<string>();
        var formats = new List<string>();
        var sets = new List<EsclResolutionSet>();
        // a profile inside the list or, at some devices, directly in the input
        foreach (var p in element.Child("SettingProfiles").Children("SettingProfile").Concat(element.Children("SettingProfile")).Take(MaxProfiles))
        {
            var profile = p;
            if (p.AttributeValue("ref") is { Length: > 0 } reference)
            {
                if (!shared.TryGetValue(reference, out var found))
                {
                    unknown.Add($"Profil „{Short(reference)}“ nicht gefunden");
                    continue;
                }
                profile = found;
            }
            foreach (var m in profile.Child("ColorModes").Children("ColorMode").Select(m => m.Value.Trim()))
            {
                if (m.Length is 0 or > 64 || modes.Count >= 32 || modes.Contains(m, StringComparer.OrdinalIgnoreCase)) continue;
                modes.Add(m);
                if (ModeOf(m) is null) unknown.Add($"Farbart „{m}“");
            }
            foreach (var f in (profile.Child("DocumentFormats")?.Elements() ?? []).Where(e => e.Is("DocumentFormat") || e.Is("DocumentFormatExt"))
                         .Select(f => f.Value.Trim().ToLowerInvariant()))
                if (f.Length is > 0 and <= 128 && formats.Count < 32 && !formats.Contains(f)) formats.Add(f);
            // resolutions inside their wrapper — several of them for different colour modes — or, at some devices, without it
            var wrappers = profile.Children("SupportedResolutions").ToList();
            if (wrappers.Count == 0) wrappers.Add(profile);
            foreach (var w in wrappers.Take(32))
                if (ReadResolutions(w) is { } set) sets.Add(set);
        }
        if (modes.Count == 0 && sets.Count == 0 && element.Int("MaxWidth") is null) return null;
        return new EsclInputCaps(Dimension(element.Int("MinWidth")), Dimension(element.Int("MaxWidth")),
            Dimension(element.Int("MinHeight")), Dimension(element.Int("MaxHeight")), modes, formats, sets);
    }

    private static int Dimension(int? value) => value is > 0 and <= MaxDimension ? value.Value : 0;

    private static EsclResolutionSet? ReadResolutions(XElement element)
    {
        var values = new List<int>();
        foreach (var d in element.Child("DiscreteResolutions").Children("DiscreteResolution").Take(MaxValues))
        {
            // the program scans with the same resolution in both directions: only those values count
            var x = d.Int("XResolution");
            var y = d.Int("YResolution") ?? x;
            if (x is > 0 and <= ResolutionChoices.Max && x == y && !values.Contains(x.Value)) values.Add(x.Value);
        }
        var ranges = new List<EsclResolutionRange>();
        if (element.Child("ResolutionRange") is { } range && Range(range.Child("XResolutionRange")) is { } xr)
        {
            var yr = Range(range.Child("YResolutionRange")) ?? xr;
            var r = new EsclResolutionRange(Math.Max(xr.Min, yr.Min), Math.Min(xr.Max, yr.Max), Math.Max(xr.Step, yr.Step));
            if (r.Min <= r.Max) ranges.Add(r);
        }
        if (values.Count == 0 && ranges.Count == 0) return null;
        var mode = element.Text("ColorMode") is { Length: <= 64 } m ? m : null;
        return new EsclResolutionSet(mode, values, ranges);
    }

    private static EsclResolutionRange? Range(XElement? element)
    {
        var min = element.Int("Min");
        var max = element.Int("Max");
        if (min is not > 0 || max is not > 0) return null;
        var step = Math.Clamp(element.Int("Step") ?? 0, 0, ResolutionChoices.Max);
        return new EsclResolutionRange(Math.Min(min.Value, max.Value), Math.Min(Math.Max(min.Value, max.Value), 100_000), step);
    }
}
