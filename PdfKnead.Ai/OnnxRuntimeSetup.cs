using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace PdfKnead.Ai;

/// <summary>
/// Prepares ONNX Runtime once before its first use, with its telemetry off. Its official builds collect usage events and send them to the
/// publisher (Linux and macOS over HTTPS with a device identifier kept in the user's cache folder, Windows through the system's event
/// tracing), switched on by default. The program sends nothing about its use anywhere (docs/FEHLERGEDAECHTNIS.md, No. 292):
/// <list type="bullet">
/// <item>ORT_DISABLE_TELEMETRY=1 in the environment of the process before the library starts — the only switch that keeps it from creating
/// the uploader, any event and the identifier. The library reads it with the C library's getenv, which does not see what .NET sets
/// (on Linux and macOS .NET keeps its own copy of the environment): it is set there directly as well.</item>
/// <item>The library's own switch for the events it would still send.</item>
/// </list>
/// </summary>
internal static partial class OnnxRuntimeSetup
{
    /// <summary>The variable that keeps ONNX Runtime from starting its telemetry.</summary>
    internal const string TelemetryVariable = "ORT_DISABLE_TELEMETRY";

    private static readonly Lazy<bool> Done = new(() =>
    {
        SetForNativeCode(TelemetryVariable, "1");
        OrtEnv.Instance().DisableTelemetryEvents();
        return true;
    });

    /// <summary>Switches the telemetry off (only the first call does something).</summary>
    public static void Prepare() => _ = Done.Value;

    /// <summary>Sets a variable so that native code reading it with getenv sees it, too.</summary>
    internal static void SetForNativeCode(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            if (OperatingSystem.IsWindows()) _ = PutEnvWindows(name, value);
            else _ = SetEnv(name, value, 1);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // without the C library of the system there is no getenv either
        }
    }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnv(string name, string value, int overwrite);

    [LibraryImport("ucrtbase", EntryPoint = "_wputenv_s", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PutEnvWindows(string name, string value);

    /// <summary>For tests: the value native code sees.</summary>
    internal static string? NativeValue(string name) => OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name) : Marshal.PtrToStringUTF8(GetEnv(name));

    [LibraryImport("libc", EntryPoint = "getenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetEnv(string name);
}
