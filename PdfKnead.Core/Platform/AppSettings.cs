using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfKnead.Core.Platform;

/// <summary>Light or dark appearance; <see cref="System"/> follows the operating system.</summary>
public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Persistent user settings in ~/.local/share/PDF-Knead/settings.json or
/// %LOCALAPPDATA%\PDF-Knead\settings.json. If the file is missing or damaged, the
/// defaults apply.
/// </summary>
/// <param name="Language">Language code; null = language of the system.</param>
/// <param name="Theme">Light, dark or as the system.</param>
/// <param name="RibbonCollapsed">The ribbon shows only the tabs until one is clicked.</param>
/// <param name="SidebarVisible">The sidebar is shown.</param>
/// <param name="RecentFiles">Recently opened files, newest first.</param>
/// <param name="EnhanceThinLines">Draw lines thinner than one pixel as a full hairline.</param>
/// <param name="SignatureName">Name for signatures (last used).</param>
/// <param name="SignatureFont">Handwriting font of the signature (file name).</param>
/// <param name="SignatureColor">Colour of the signature as 0xRRGGBB.</param>
/// <param name="SignatureOrigin">Add the origin line below the signature (name, date, device code).</param>
/// <param name="SignatureTextured">Signatures with ballpoint-pen texture (otherwise smooth vectors).</param>
/// <param name="SignatureInkStrength">Ink strength of the ballpoint-pen texture (1 = like the reference ink sample, default 1.6).</param>
/// <param name="OverprintPreview">Overprint preview: overprinted colours mix as in print.</param>
/// <param name="FieldLooks">Last chosen appearance per field type; new fields receive it.</param>
/// <param name="NewPage">Last chosen page format for File → New and blank pages (without the count).</param>
/// <param name="Print">Last used print settings, per printer.</param>
/// <param name="Compression">Last chosen compression (for “Custom”, the custom values).</param>
/// <param name="CompressWindow">Size of the compress window.</param>
/// <param name="SignatureDigital">Also sign signatures digitally (last choice in the signature window).</param>
/// <param name="SigningCertificate">Certificate for digital signatures; null = the own one.</param>
/// <param name="CertificateOffered">The first start has offered to create the own certificate (asked once, whatever the answer).</param>
/// <param name="MergeWindow">Size of the window „Seiten zusammenführen“.</param>
/// <param name="OcrSize">Size of the text recognition last chosen (KONZEPT.md, section 5.9); small unless chosen otherwise (design decision
/// 13.11.2026). Read leniently: „Tiny“ of earlier versions becomes small, the other settings stay.</param>
/// <param name="RedactionReasons">Own reasons for redactions entered before, newest first (KONZEPT.md, section 5.6, B-14).</param>
/// <param name="RedactionName">Name or initials for the redaction protocol; null: none (can be cleared in the window).</param>
/// <param name="SignatureTimestamps">Digital signatures get a time stamp from <paramref name="TimestampServer"/> (PAdES B-T; needs a connection;
/// off unless switched on, KONZEPT.md, section 5.17, „Zeitstempel“).</param>
/// <param name="TimestampServer">Address of the time-stamping authority the user entered; null: none.</param>
/// <param name="SignatureArchive">After signing, a document time stamp follows the validation data (PAdES B-LTA; only with time stamps;
/// off unless switched on, KONZEPT.md, section 5.17, „Archiv-Zeitstempel“).</param>
/// <param name="TimestampUser">User name at the time-stamping authority (services that want a login); null: none. The password is asked
/// once per session and never stored.</param>
/// <param name="TimestampCertificate">Customer certificate file for the time-stamping authority; null: none.</param>
/// <param name="SignatureLongTerm">After signing, the revocation status of the certificates is fetched from their certification authorities
/// and embedded (PAdES B-LT; needs a connection; off unless switched on, KONZEPT.md, section 5.17, „Langzeitprüfung“).</param>
/// <param name="RedactionCodeSet">The set of redaction codes last chosen („de“, „eu“, „at“, „ch“ or an own file); null: after the language
/// (<see cref="Pdf.Editing.RedactionCodes.DefaultFor"/>; wish of 4.11.2026).</param>
/// <param name="RedactionCodeInBar">„Code im Balken anzeigen“ as last chosen.</param>
/// <param name="AskUnsavedMeasurements">When „Messen“ is switched off with measurements that are not in the PDF, ask whether to insert them
/// as dimensions („Nicht mehr fragen“ in the question; on again in the area „Messen“; decided on 8.11.2026).</param>
/// <param name="AskCropOverhang">When an image is placed or changed so that it reaches beyond the edge of the page, ask whether to cut that part
/// off (it would stay in the file; „Nicht mehr fragen“ in the question, on again in the area of the image; decided on 8.11.2026).</param>
/// <param name="CertificateFiles">The certificate files added by the user (<see cref="Files"/>).</param>
/// <param name="Scan">The scan settings last used (device, source, resolution, colour, format and the rest of the scan window's left side); null: none yet.</param>
/// <param name="ScanProfileName">The scan profile last chosen; null: the settings last used.</param>
/// <param name="ScanWindow">The size of the scan window as last left.</param>
/// <param name="ScanTilesShare">The share of the tiles in the height of the scan window's right side (the rest is the preview), as last left.</param>
/// <param name="ScanGroupsClosed">The groups of the scan window's left side that were folded up („scanner“, „processing“).</param>
/// <param name="PageImages">How pages were last saved as images (format, quality, one TIFF file, resolution); null: the defaults.</param>
/// <param name="HiddenModules">The keys of the module tabs the user hid in the settings (the tab of a module is shown unless listed here).</param>
/// <param name="OpenInRunningWindow">„Dateien öffnen“: a file opened from outside (double click, „Öffnen mit“) goes to the window used last as a
/// new tab instead of a window of its own (<see cref="FileHandoff"/>; decided on 16.11.2026; off: each file in its own window, as before).</param>
/// <param name="ScanNetwork">The own eSCL access: on or off, networks and addresses of scanners, confirmed scanner certificates
/// (KONZEPT §5.25, S9); null = the defaults (on, nothing entered).</param>
/// <param name="UpdateCheck">„Automatisch nach Updates suchen“ (KONZEPT §5.26; decided on 18.11.2026: off at first, asked once at the
/// first start).</param>
/// <param name="UpdateLastCheck">The day of the last automatic check (yyyy-MM-dd): at most one a day.</param>
/// <param name="UpdatesAsked">Whether the question at the first start was asked (whatever the answer).</param>
/// <param name="ScanHidden">Scanners the user removed from the list of the scan window with „×“ (their ids, every way of a device;
/// docs/FEHLERGEDAECHTNIS.md, No. 926): the system and the multicast search report them again and again — a device installed in the system
/// stays there after it is gone — so they are left out of the list until „Ausgeblendete wieder zeigen“.</param>
public sealed record AppSettings(
    string? Language = null,
    ThemeChoice Theme = ThemeChoice.System,
    bool RibbonCollapsed = false,
    bool SidebarVisible = true,
    IReadOnlyList<string>? RecentFiles = null,
    bool EnhanceThinLines = true,
    string? SignatureName = null,
    string? SignatureFont = null,
    int SignatureColor = 0x1B3A8C,
    bool SignatureOrigin = true,
    bool SignatureTextured = true,
    double SignatureInkStrength = Imaging.InkColor.DefaultStrength,
    bool OverprintPreview = false,
    IReadOnlyList<Pdf.Editing.FieldLook>? FieldLooks = null,
    Pdf.Editing.PageSetup? NewPage = null,
    Printing.PrintPreferences? Print = null,
    Pdf.Writing.CompressionSettings? Compression = null,
    WindowSize? CompressWindow = null,
    bool SignatureDigital = false,
    SigningCertificateChoice? SigningCertificate = null,
    bool CertificateOffered = false,
    WindowSize? MergeWindow = null,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(Ocr.OcrModelSizeJsonConverter))] Ocr.OcrModelSize OcrSize = Ocr.OcrModelSize.Small,
    IReadOnlyList<string>? RedactionReasons = null,
    string? RedactionName = null,
    bool SignatureTimestamps = false,
    string? TimestampServer = null,
    bool SignatureLongTerm = false,
    string? TimestampUser = null,
    string? TimestampCertificate = null,
    bool SignatureArchive = false,
    string? RedactionCodeSet = null,
    bool RedactionCodeInBar = true,
    bool AskUnsavedMeasurements = true,
    bool AskCropOverhang = true,
    IReadOnlyList<SigningCertificateChoice>? CertificateFiles = null,
    Scanning.ScanProfile? Scan = null,
    string? ScanProfileName = null,
    WindowSize? ScanWindow = null,
    double ScanTilesShare = 0.4,
    IReadOnlyList<string>? ScanGroupsClosed = null,
    Imaging.PageImageSettings? PageImages = null,
    IReadOnlyList<string>? HiddenModules = null,
    bool OpenInRunningWindow = false,
    Scanning.Escl.EsclSettings? ScanNetwork = null,
    IReadOnlyList<string>? ScanHidden = null,
    bool UpdateCheck = false,
    string? UpdateLastCheck = null,
    bool UpdatesAsked = false)
{
    /// <summary>The most scanners hidden — also from a settings file edited by hand.</summary>
    public const int MaxScanHidden = 64;

    /// <summary>The ids of the hidden scanners that count (at most <see cref="MaxScanHidden"/>, none empty or longer than 512 characters).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlySet<string> ScanHiddenIds =>
        (ScanHidden ?? []).TakeLast(MaxScanHidden).Where(i => i is { Length: > 0 and <= 512 }).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The certificate files the user added (path and fingerprint, never a password; docs/ABLAUFPLAN.md, 9.4.0) — listed in „Zertifikat“ so that
    /// it is clear which one is the default; never null.
    /// </summary>
    public IReadOnlyList<SigningCertificateChoice> Files => CertificateFiles ?? [];

    /// <summary>Remembers a certificate file (once per path, the newest data kept).</summary>
    /// <param name="file">The file with its fingerprint.</param>
    /// <returns>A copy of the settings with the file in the list.</returns>
    public AppSettings WithCertificateFile(SigningCertificateChoice file)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return this with { CertificateFiles = [.. Files.Where(f => !comparison.Equals(f.Path, file.Path)), file with { Source = SigningCertificateSource.File }] };
    }

    /// <summary>Forgets a certificate file (the file itself is not touched).</summary>
    /// <param name="path">Its path.</param>
    /// <returns>A copy of the settings without it.</returns>
    public AppSettings WithoutCertificateFile(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return this with { CertificateFiles = [.. Files.Where(f => !comparison.Equals(f.Path, path))] };
    }

    /// <summary>Maximum number of entries in the list of recently opened files.</summary>
    public const int MaxRecentFiles = 25;

    /// <summary>Gets the default path of the settings file (settings.json in <see cref="AppLocations.UserDataFolder"/>).</summary>
    public static string DefaultFile => Path.Combine(AppLocations.UserDataFolder, "settings.json");

    /// <summary>Gets the recently opened files, newest first; never null.</summary>
    public IReadOnlyList<string> Recent => RecentFiles ?? [];

    /// <summary>Removes an entry from the history (the file itself is not touched).</summary>
    /// <param name="file">Path to remove; compared case-insensitively on Windows, case-sensitively elsewhere.</param>
    /// <returns>A copy of the settings without the entry.</returns>
    public AppSettings WithoutRecent(string file)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return this with { RecentFiles = Recent.Where(f => !comparison.Equals(f, file)).ToList() };
    }

    /// <summary>Puts a file at the front of the “Recently opened” list, without duplicates, at most <see cref="MaxRecentFiles"/> entries.</summary>
    /// <param name="file">Path of the opened file; compared case-insensitively on Windows, case-sensitively elsewhere.</param>
    /// <returns>A copy of the settings with the updated list.</returns>
    public AppSettings WithRecent(string file)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var list = Recent.Where(f => !comparison.Equals(f, file)).Prepend(file).Take(MaxRecentFiles).ToList();
        return this with { RecentFiles = list };
    }

    /// <summary>
    /// The settings of the file as they are now; null if it is missing, unreadable or invalid — never the defaults, so that a change made on
    /// top of it cannot wipe all settings when another window is just writing the file (FG 867).
    /// </summary>
    /// <param name="file">Path of the settings file.</param>
    public static AppSettings? TryLoad(string file)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), AppSettingsJson.Default.AppSettings);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Loads the settings from a file.</summary>
    /// <param name="file">Path of the settings file; null = <see cref="DefaultFile"/>.</param>
    /// <returns>The loaded settings, or the defaults if the file is missing, unreadable or invalid.</returns>
    public static AppSettings Load(string? file = null)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file ?? DefaultFile), AppSettingsJson.Default.AppSettings) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new();
        }
    }

    /// <summary>Saves the settings to a file, creating its folder if necessary.</summary>
    /// <param name="file">Path of the settings file; null = <see cref="DefaultFile"/>.</param>
    /// <remarks>If the file cannot be written (read-only …), the error is ignored and the settings apply only until the program ends.</remarks>
    public void Save(string? file = null)
    {
        file ??= DefaultFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            // Write next to it first, then replace: a crash or a full disk in the middle of writing leaves the old file
            // intact (otherwise all settings would be lost). The temporary name carries the process: two windows writing at once
            // do not write into the same one (FG 867).
            var temp = $"{file}.{Environment.ProcessId}.neu";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, AppSettingsJson.Default.AppSettings));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot be saved (read-only …): the settings then apply only until the program ends.
        }
    }
}

/// <summary>Remembered window size.</summary>
/// <param name="Width">Width in user interface units.</param>
/// <param name="Height">Height in user interface units.</param>
public sealed record WindowSize(double Width, double Height);

/// <summary>Generated JSON code instead of reflection (fast start-up, Native AOT).</summary>
/// <summary>
/// Which certificate signs: the own one, a PKCS#12 file (its path; the password is asked when signing and never stored) or a
/// certificate of the Windows store (its SHA-256 fingerprint).
/// </summary>
/// <param name="Source">Where it comes from.</param>
/// <param name="Path">Path of the file for <see cref="SigningCertificateSource.File"/>.</param>
/// <param name="Fingerprint">Fingerprint for <see cref="SigningCertificateSource.Store"/>.</param>
/// <summary>A certificate for digital signatures: its source, for a file its path, for the store and cards its fingerprint.</summary>
/// <param name="Name">The holder, as shown in the list (a file is opened only with its password, when signing).</param>
/// <param name="ValidUntil">The end of validity, as shown in the list.</param>
/// <param name="Level">The level its signature can reach, as shown in the list.</param>
public sealed record SigningCertificateChoice(SigningCertificateSource Source, string? Path = null, string? Fingerprint = null, string? Name = null,
    DateTimeOffset? ValidUntil = null, Pdf.Signatures.SignatureLevel? Level = null);

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;
