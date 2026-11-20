using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfKnead.Core.Scanning;

/// <summary>
/// The settings of the left side of the scan window under a name (KONZEPT §5.25, „Profile“); without a name, the settings last used.
/// Unknown entries of a newer version are ignored when read, missing ones take their defaults.
/// </summary>
/// <param name="Name">The name the user gave; empty for the settings last used.</param>
/// <param name="DeviceId">The id of the device.</param>
/// <param name="DeviceName">The name of the device: finds it again when its id has changed (another computer, another address).</param>
/// <param name="Source">The source name as the driver names it.</param>
/// <param name="Dpi">The resolution.</param>
/// <param name="Mode">The colour mode.</param>
/// <param name="Format">The key of the paper format.</param>
/// <param name="ManualDuplex">Both sides by turning the stack: fronts first, then the backs (feeder without duplex).</param>
/// <param name="SkipBlank">Whether blank pages are left out.</param>
/// <param name="BlankLimit">Up to which share of the page in percent a page counts as blank.</param>
/// <param name="Adjustments">The processing new pages get („Nachbearbeitung“); null: the default (straighten, cut dark borders, light as scanned).</param>
/// <param name="BacksTurned">Both sides by turning: the backs come upside down and are turned by half a turn (the stack turned over the
/// short edge, or a feeder that delivers them so) — „Lage-Korrektur“ of the scan window, remembered.</param>
/// <param name="BacksInFeedOrder">Both sides by turning: the backs come in the order of the sheets (2, 4, 6), not the last first — devices
/// whose paper path lays the sheets face up in the tray.</param>
public sealed record ScanProfile(string Name = "", string? DeviceId = null, string? DeviceName = null, string? Source = null, int Dpi = 300,
    ScanColorMode Mode = ScanColorMode.Color, string Format = "A4", bool ManualDuplex = false, bool SkipBlank = false,
    double BlankLimit = BlankPages.DefaultLimit, Processing.ScanAdjustments? Adjustments = null, bool BacksTurned = false,
    bool BacksInFeedOrder = false)
{
    /// <summary>The version of the profile format this program writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The longest name.</summary>
    public const int MaxName = 80;

    /// <summary>The version of the format the profile was written in.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>
    /// The profile with every value inside its limits: the name without control characters and at most <see cref="MaxName"/> characters,
    /// the texts shortened, the numbers in their ranges — a profile from a file is never trusted as it is.
    /// </summary>
    /// <returns>The checked profile.</returns>
    public ScanProfile Normalized() => this with
    {
        Name = CleanName(Name),
        DeviceId = Cut(DeviceId, 512),
        DeviceName = Cut(DeviceName, 256),
        Source = Cut(Source, 128),
        Dpi = Math.Clamp(Dpi, 25, 19200),
        Mode = Enum.IsDefined(Mode) ? Mode : ScanColorMode.Color,
        Format = Cut(Format, 32) is { Length: > 0 } format ? format : "A4",
        BlankLimit = double.IsFinite(BlankLimit) ? Math.Clamp(BlankLimit, BlankPages.MinLimit, BlankPages.MaxLimit) : BlankPages.DefaultLimit,
        Adjustments = Adjustments?.Normalized(),
        Version = CurrentVersion,
    };

    /// <summary>The device of the profile among <paramref name="devices"/>: the same id, else the same name reached the same way, else the same name.</summary>
    /// <param name="devices">The devices found.</param>
    /// <returns>The device, or null if it is not there.</returns>
    public ScanDevice? FindDevice(IReadOnlyList<ScanDevice> devices)
    {
        // any way of the device counts: a profile saved with one way finds the device when the program chose another as the best
        if (devices.FirstOrDefault(d => d.Has(DeviceId)) is { } same) return same;
        if (DeviceName is not { Length: > 0 } name) return null;
        var named = devices.Where(d => string.Equals(d.BaseName, name, StringComparison.OrdinalIgnoreCase)).ToList();
        var prefix = DeviceId?.Split(':')[0];
        return named.FirstOrDefault(d => d.AllIds.Any(id => id.Split(':')[0] == prefix)) ?? named.FirstOrDefault();
    }

    /// <summary>The name without control characters and surrounding spaces, at most <see cref="MaxName"/> characters.</summary>
    /// <param name="name">The name as typed or read.</param>
    /// <returns>The cleaned name (may be empty).</returns>
    public static string CleanName(string? name)
    {
        // control characters, line separators and direction marks (which could make a name read differently from what it is) are left out
        var text = new string((name ?? "").Where(c => char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.Control
            or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator)).ToArray()).Trim();
        return text.Length <= MaxName ? text : text[..MaxName].TrimEnd();
    }

    private static string? Cut(string? text, int length)
    {
        if (text is null) return null;
        text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        return text.Length <= length ? text : text[..length];
    }
}

/// <summary>
/// The scan profiles of the user: one file per profile in a folder of the program's settings, written through a temporary file. Export and
/// import use one file for any number of profiles; what is read is checked (size, count, values) before it is used.
/// </summary>
/// <param name="folder">The folder of the profiles; created on the first save.</param>
public sealed class ScanProfileStore(string folder)
{
    /// <summary>The marker of an export file.</summary>
    public const string ExportFormat = "PDF-Knead-Scanprofile";

    /// <summary>The largest export file read (a profile has some hundred bytes).</summary>
    public const long MaxFileBytes = 1 << 20;

    /// <summary>The most profiles in one export file and in the folder.</summary>
    public const int MaxProfiles = 200;

    private const long MaxProfileBytes = 64 << 10;

    /// <summary>The folder of the profiles.</summary>
    public string Folder { get; } = folder;

    /// <summary>The store beside a settings file: the folder „Scanprofile“ next to it.</summary>
    /// <param name="settingsFile">The path of the settings file.</param>
    /// <returns>The store.</returns>
    public static ScanProfileStore Beside(string settingsFile) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsFile)) ?? ".", "Scanprofile"));

    /// <summary>All profiles, sorted by name; files that cannot be read are left out.</summary>
    /// <returns>The profiles.</returns>
    public IReadOnlyList<ScanProfile> Load()
    {
        if (!Directory.Exists(Folder)) return [];
        var profiles = new List<ScanProfile>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Take(MaxProfiles * 2))
        {
            try
            {
                if (new FileInfo(file).Length > MaxProfileBytes) continue;
                if (JsonSerializer.Deserialize(File.ReadAllBytes(file), ScanProfileJson.Default.ScanProfile)?.Normalized() is { Name.Length: > 0 } profile
                    && !profiles.Any(p => p.Name == profile.Name))
                    profiles.Add(profile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // a broken or foreign file: the other profiles count
            }
            if (profiles.Count == MaxProfiles) break;
        }
        return [.. profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Whether a profile of this name exists.</summary>
    public bool Exists(string name) => File.Exists(PathOf(ScanProfile.CleanName(name)));

    /// <summary>Saves a profile; one of the same name is replaced.</summary>
    /// <param name="profile">The profile; its name must not be empty.</param>
    /// <returns>The profile as saved (checked).</returns>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    public ScanProfile Save(ScanProfile profile)
    {
        profile = profile.Normalized();
        if (profile.Name.Length == 0) throw new ArgumentException("A profile needs a name.", nameof(profile));
        Directory.CreateDirectory(Folder);
        var path = PathOf(profile.Name);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(profile, ScanProfileJson.Default.ScanProfile));
        File.Move(temp, path, overwrite: true);
        return profile;
    }

    /// <summary>Deletes a profile.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Whether there was one.</returns>
    public bool Delete(string name)
    {
        var path = PathOf(ScanProfile.CleanName(name));
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Writes profiles into one export file.</summary>
    /// <param name="profiles">The profiles.</param>
    /// <param name="target">The stream of the file.</param>
    public static void Export(IEnumerable<ScanProfile> profiles, Stream target) =>
        JsonSerializer.Serialize(target, new ScanProfileExport(ExportFormat, ScanProfile.CurrentVersion, [.. profiles.Select(p => p.Normalized())]),
            ScanProfileJson.Default.ScanProfileExport);

    /// <summary>Reads the profiles of an export file, checked; profiles without a name are left out.</summary>
    /// <param name="source">The stream of the file.</param>
    /// <returns>The profiles.</returns>
    /// <exception cref="InvalidDataException">Not an export file of scan profiles, too large, too many profiles or from a newer version.</exception>
    public static IReadOnlyList<ScanProfile> Import(Stream source)
    {
        var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            if (bytes.Length + read > MaxFileBytes) throw new InvalidDataException(Tr.T("Die Datei ist zu groß für Scanprofile."));
            bytes.Write(buffer, 0, read);
        }
        ScanProfileExport? file;
        try
        {
            file = JsonSerializer.Deserialize(bytes.ToArray(), ScanProfileJson.Default.ScanProfileExport);
        }
        catch (JsonException)
        {
            file = null;
        }
        if (file is null || file.Format != ExportFormat) throw new InvalidDataException(Tr.T("Die Datei enthält keine Scanprofile von PDF-Knead."));
        if (file.Version > ScanProfile.CurrentVersion)
            throw new InvalidDataException(Tr.T("Die Scanprofile stammen aus einer neueren Version von PDF-Knead — bitte das Programm aktualisieren."));
        var profiles = file.Profiles ?? [];
        if (profiles.Count > MaxProfiles) throw new InvalidDataException(Tr.T("Die Datei enthält zu viele Scanprofile (höchstens {0}).", MaxProfiles));
        return [.. profiles.OfType<ScanProfile>().Select(p => p.Normalized()).Where(p => p.Name.Length > 0)];
    }

    /// <summary>
    /// What to save of imported profiles: the same profile as one already there is left out (also when an earlier import gave it a number);
    /// another profile under a name already there gets a free name („Büro (2)“) — nothing of the user's is replaced without asking.
    /// </summary>
    /// <param name="existing">The profiles there.</param>
    /// <param name="imported">The imported profiles.</param>
    /// <returns>The profiles to save, with their names.</returns>
    public static IReadOnlyList<ScanProfile> Merge(IReadOnlyList<ScanProfile> existing, IReadOnlyList<ScanProfile> imported)
    {
        var names = new HashSet<string>(existing.Select(p => p.Name), StringComparer.CurrentCultureIgnoreCase);
        var result = new List<ScanProfile>();
        foreach (var profile in imported)
        {
            var stem = Stem(profile.Name, out var number);
            if (existing.Concat(result).Any(p => p with { Name = "" } == profile with { Name = "" } && Stem(p.Name, out _) == stem)) continue;
            var name = profile.Name;
            // a name that already ends in a number counts on from it („Büro (2)“ → „Büro (3)“, not „Büro (2) (2)“)
            for (var n = number + 1; names.Contains(name); n++)
            {
                var suffix = $" ({n})";
                name = (stem.Length + suffix.Length > ScanProfile.MaxName ? stem[..(ScanProfile.MaxName - suffix.Length)] : stem) + suffix;
            }
            names.Add(name);
            result.Add(profile with { Name = name });
        }
        return result;
    }

    /// <summary>The name without a number at its end („Büro (2)“ → „Büro“, number 2); a name without one has the number 1.</summary>
    private static string Stem(string name, out int number)
    {
        var numbered = System.Text.RegularExpressions.Regex.Match(name, @"^(.*\S) \((\d{1,4})\)$");
        number = numbered.Success ? int.Parse(numbered.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
        return numbered.Success ? numbered.Groups[1].Value : name;
    }

    /// <summary>
    /// The file of a profile: its name with characters a file system may refuse replaced, and a short checksum of the exact name — two names
    /// that differ only in such characters or in case do not share a file.
    /// </summary>
    private string PathOf(string name)
    {
        var safe = new StringBuilder();
        foreach (var c in name.Take(40)) safe.Append(char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_');
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
        return Path.Combine(Folder, $"{safe.ToString().Trim()}-{hash}.json");
    }
}

/// <summary>The content of an export file.</summary>
/// <param name="Format">Always <see cref="ScanProfileStore.ExportFormat"/>.</param>
/// <param name="Version">The version of the profile format.</param>
/// <param name="Profiles">The profiles.</param>
public sealed record ScanProfileExport(string Format, int Version, List<ScanProfile>? Profiles);

/// <summary>Generated JSON code (Native AOT).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(ScanProfile))]
[JsonSerializable(typeof(ScanProfileExport))]
internal sealed partial class ScanProfileJson : JsonSerializerContext;
