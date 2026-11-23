using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PdfKnead.Core.Updates;

/// <summary>
/// A program version as the releases name it: „1.2.171-beta“ — numbers separated by dots, optionally a pre-release after a hyphen
/// (KONZEPT.md §5.26). A version without a pre-release is higher than the same numbers with one („1.3.0“ after „1.3.0-beta“).
/// </summary>
public readonly record struct UpdateVersion(IReadOnlyList<int> Numbers, string PreRelease) : IComparable<UpdateVersion>
{
    /// <summary>Reads a version; null if the text is none (at most 4 numbers of at most 9 digits, a pre-release of letters, digits and dots).</summary>
    public static UpdateVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return null;
        var t = text.Trim();
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        var hyphen = t.IndexOf('-');
        var pre = hyphen < 0 ? "" : t[(hyphen + 1)..];
        var parts = (hyphen < 0 ? t : t[..hyphen]).Split('.');
        if (parts.Length is < 1 or > 4 || (hyphen >= 0 && (pre.Length == 0 || !pre.All(c => char.IsAsciiLetterOrDigit(c) || c == '.'))))
            return null;
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length is 0 or > 9 || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        return new UpdateVersion(numbers, pre);
    }

    /// <inheritdoc/>
    public int CompareTo(UpdateVersion other)
    {
        for (var i = 0; i < Math.Max(Numbers.Count, other.Numbers.Count); i++)
        {
            var a = i < Numbers.Count ? Numbers[i] : 0;
            var b = i < other.Numbers.Count ? other.Numbers[i] : 0;
            if (a != b) return a.CompareTo(b);
        }
        if (PreRelease.Length == 0 || other.PreRelease.Length == 0) return (PreRelease.Length == 0).CompareTo(other.PreRelease.Length == 0);
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    /// <summary>Whether this version is higher than <paramref name="other"/>.</summary>
    public bool IsNewerThan(UpdateVersion other) => CompareTo(other) > 0;

    /// <inheritdoc/>
    public override string ToString() => string.Join('.', Numbers) + (PreRelease.Length > 0 ? "-" + PreRelease : "");
}

/// <summary>One package of a release: for one platform, the file, where it lies, how large it is and its SHA-256 (lower-case hex).</summary>
public sealed record UpdatePackage(string Platform, string File, string Url, long Size, string Sha256);

/// <summary>
/// The version file of a release (<c>version.json</c>, KONZEPT.md §5.26): version, date, what changed, the packages per platform. It comes
/// from the network: read only after its signature was checked, with limits, and every package address must point into the releases of the
/// repository.
/// </summary>
public sealed record UpdateManifest(UpdateVersion Version, string Date, string Notes, IReadOnlyList<UpdatePackage> Packages)
{
    /// <summary>The largest version file accepted.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The largest package accepted.</summary>
    public const long MaxPackage = 1L << 30;

    /// <summary>Where every package must lie: the release downloads of the repository.</summary>
    public const string ReleasePrefix = "https://github.com/systemswap/PDF-Knead/releases/download/";

    /// <summary>The platform of this program: „windows-x64“, „linux-x64“ …</summary>
    public static string ThisPlatform =>
        (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other") + "-" +
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    /// <summary>The package for a platform, or null.</summary>
    public UpdatePackage? PackageFor(string platform) => Packages.FirstOrDefault(p => p.Platform == platform);

    /// <summary>Reads a version file; null for anything that is not one as written by <see cref="ToJson"/>.</summary>
    public static UpdateManifest? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (UpdateVersion.Parse(Text(root, "version")) is not { } version) return null;
            var packages = new List<UpdatePackage>();
            if (!root.TryGetProperty("packages", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 16) return null;
            foreach (var p in list.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) return null;
                var platform = Text(p, "platform");
                var file = Text(p, "file");
                var url = Text(p, "url");
                var sha = Text(p, "sha256");
                if (platform is null || file is null || url is null || sha is null || !p.TryGetProperty("size", out var size)
                    || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes) || bytes is <= 0 or > MaxPackage)
                    return null;
                if (!IsPlainName(file) || !url.StartsWith(ReleasePrefix, StringComparison.Ordinal) || !url.EndsWith("/" + file, StringComparison.Ordinal)
                    || url.Contains("..", StringComparison.Ordinal) || sha.Length != 64 || !sha.All(char.IsAsciiHexDigitLower))
                    return null;
                packages.Add(new UpdatePackage(platform, file, url, bytes, sha));
            }
            // „introductionUntil“ of older version files (the end of an introductory period, until 21.11.2026) is passed over
            return new UpdateManifest(version, Text(root, "date") ?? "", Text(root, "notes") ?? "", packages);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The version file as written for a release (UTF-8, the order of the fields fixed).</summary>
    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("version", Version.ToString());
            w.WriteString("date", Date);
            w.WriteString("notes", Notes);
            w.WriteStartArray("packages");
            foreach (var p in Packages)
            {
                w.WriteStartObject();
                w.WriteString("platform", p.Platform);
                w.WriteString("file", p.File);
                w.WriteString("url", p.Url);
                w.WriteNumber("size", p.Size);
                w.WriteString("sha256", p.Sha256);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: <= 8000 } s ? s : null;

    /// <summary>A file name without a path: letters, digits and „-_.“, not starting with a dot.</summary>
    public static bool IsPlainName(string name) =>
        name.Length is > 0 and <= 128 && name[0] != '.' && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>The notes cleaned for the window: no control or direction characters, at most 4 000 characters.</summary>
    public static string CleanNotes(string notes)
    {
        var b = new StringBuilder(Math.Min(notes.Length, 4000));
        foreach (var c in notes)
        {
            if (b.Length >= 4000) break;
            if (c == '\n' || (!char.IsControl(c) && c is not ('\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\uFEFF')))
                b.Append(c);
        }
        return b.ToString();
    }
}
