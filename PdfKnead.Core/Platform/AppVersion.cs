using System.Reflection;

namespace PdfKnead.Core.Platform;

/// <summary>
/// Program version from <c>Directory.Build.props</c> (applies to all projects of the solution).
/// </summary>
public static class AppVersion
{
    /// <summary>Gets the version as written in the file, e.g. “0.1.5-beta”, without the build suffix “+…”.</summary>
    /// <remarks>Taken from the informational version attribute; falls back to the assembly version (three parts) and then to “0.0.0”.</remarks>
    public static string Raw { get; } = Read();

    /// <summary>Gets the version for the window title, e.g. “0.1.5 beta” (hyphens replaced by spaces).</summary>
    public static string Display => ForDisplay(Raw);

    internal static string ForDisplay(string raw) => raw.Replace('-', ' ');

    private static string Read()
    {
        var info = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
                   ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus < 0 ? info : info[..plus];
    }
}
