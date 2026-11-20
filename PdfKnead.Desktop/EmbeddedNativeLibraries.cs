using System.Runtime.InteropServices;
using PdfKnead.Core.Platform;

namespace PdfKnead.Desktop;

/// <summary>
/// AOT binary as a single file: the native libraries (Skia, HarfBuzz, ONNX Runtime, on Windows ANGLE) are
/// embedded in the program as resources “native/…”. On the first start of a version they are
/// extracted to the user folder (…/PDF-Knead/bibliotheken/&lt;version&gt;) and loaded before Avalonia,
/// as .NET itself does for the .NET build with IncludeNativeLibrariesForSelfExtract. The
/// .NET build has no such resources; then nothing happens here.
/// </summary>
internal static class EmbeddedNativeLibraries
{
    private const string Prefix = "native/";

    /// <summary>Loaded libraries by name without extension (“libSkiaSharp”).</summary>
    private static readonly Dictionary<string, IntPtr> Loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extracted libraries loaded only when first asked for, by name without extension.</summary>
    private static readonly Dictionary<string, string> OnDemand = new(StringComparer.OrdinalIgnoreCase);

    public static void Load()
    {
        var assembly = typeof(EmbeddedNativeLibraries).Assembly;
        var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
        if (names.Count == 0) return;

        var root = Path.Combine(AppLocations.UserDataFolder, "bibliotheken");
        var folder = Path.Combine(root, AppVersion.Raw);
        Directory.CreateDirectory(folder);
        foreach (var name in names)
        {
            var file = Path.Combine(folder, name[Prefix.Length..]);
            using (var stream = assembly.GetManifestResourceStream(name)!)
                if (!File.Exists(file) || new FileInfo(file).Length != stream.Length) Extract(stream, file);
            // ONNX Runtime (text recognition, 17–29 MB) is needed only when text is recognised: loaded then, not at every start
            if (file.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase)) OnDemand[Path.GetFileNameWithoutExtension(file)] = file;
            else Loaded[Path.GetFileNameWithoutExtension(file)] = NativeLibrary.Load(file);
        }

        // Redirect DllImport("libSkiaSharp") etc. to the loaded files (on Windows LoadLibrary would
        // already find them by name, on Linux dlopen not necessarily).
        DllImportResolver resolve = (library, _, _) => Resolve(StripExtension(library));
        foreach (var user in new[] { typeof(SkiaSharp.SKObject).Assembly, typeof(HarfBuzzSharp.Blob).Assembly, typeof(Microsoft.ML.OnnxRuntime.OrtEnv).Assembly })
        {
            try { NativeLibrary.SetDllImportResolver(user, resolve); }
            catch (InvalidOperationException) { }   // already has a resolver of its own
        }

        RemoveOtherVersions(root, folder);
    }

    /// <summary>The handle of a library, loading it on first use if it is one of <see cref="OnDemand"/>; zero for others.</summary>
    private static IntPtr Resolve(string name)
    {
        lock (Loaded)
        {
            if (Loaded.TryGetValue(name, out var handle)) return handle;
            // the managed side of ONNX Runtime asks for "onnxruntime", the file is "libonnxruntime" on Linux
            var file = OnDemand.GetValueOrDefault(name) ?? OnDemand.GetValueOrDefault("lib" + name);
            return file is null ? IntPtr.Zero : Loaded[name] = NativeLibrary.Load(file);
        }
    }

    private static void Extract(Stream stream, string file)
    {
        // Write under a name of its own first, then rename, so that two instances started at the same
        // time do not interfere with each other.
        var temp = $"{file}.{Environment.ProcessId}.neu";
        using (var output = File.Create(temp)) stream.CopyTo(output);
        try { File.Move(temp, file, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The other instance was faster and has already loaded the file.
            File.Delete(temp);
            if (!File.Exists(file)) throw;
        }
    }

    private static string StripExtension(string library) =>
        library.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || library.EndsWith(".so", StringComparison.Ordinal)
            ? Path.GetFileNameWithoutExtension(library)
            : library;

    /// <summary>Removes the libraries of other versions (folders in use remain until the next start).</summary>
    private static void RemoveOtherVersions(string root, string current)
    {
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
