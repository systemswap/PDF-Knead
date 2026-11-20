using PdfKnead.Core.Platform;

namespace PdfKnead.Services;

/// <summary>Device identity of this user (loaded or created once). Tests redirect <see cref="File"/>.</summary>
public static class Device
{
    private static DeviceIdentity? _current;

    public static string File { get; set; } = DeviceIdentity.DefaultFile;

    public static DeviceIdentity Current => _current ??= DeviceIdentity.LoadOrCreate(File);

    /// <summary>
    /// Builds the origin line, e.g. “Name · 16.10.2026 14:32 · ID 7F3A9C-12C4D7A8B9” (the name is omitted when empty).
    /// It contains neither a computer name nor a user name; the code only shows whether it was the same PC and user.
    /// </summary>
    /// <param name="name">Name entered by the user; trimmed, omitted when blank.</param>
    /// <param name="when">Date and time, formatted with the current culture (short date and time).</param>
    /// <returns>The parts joined with “ · ”.</returns>
    public static string Origin(string name, DateTime when)
    {
        var parts = new List<string>();
        if (name.Trim().Length > 0) parts.Add(name.Trim());
        parts.Add(when.ToString("g", System.Globalization.CultureInfo.CurrentCulture));
        parts.Add($"{Tr.T("Kennung")} {DeviceCode.Current}");
        return string.Join(" · ", parts);
    }
}
