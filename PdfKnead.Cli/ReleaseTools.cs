using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PdfKnead.Core.Updates;

namespace PdfKnead.Cli;

/// <summary>
/// The release side of the updates (KONZEPT.md §5.26): the key pair, once, and the signed version file for the packages of a release. The
/// secret key stays with the user — never in the repository, never online.
/// </summary>
internal static class ReleaseTools
{
    /// <summary>
    /// <c>pdf-knead release-schluessel &lt;ordner&gt;</c>: writes <c>release-schluessel-geheim.pem</c> and <c>release-schluessel-oeffentlich.txt</c>
    /// into the folder (never over an existing secret key). The public key goes into <see cref="UpdateSignature.ReleaseKey"/>.
    /// </summary>
    public static int CreateKey(string folder)
    {
        Directory.CreateDirectory(folder);
        var secret = Path.Combine(folder, "release-schluessel-geheim.pem");
        if (File.Exists(secret))
        {
            Console.Error.WriteLine($"Es gibt schon einen geheimen Schlüssel: {secret} — er wird nicht überschrieben.");
            return 1;
        }
        var (privatePem, publicBase64) = UpdateSignature.CreateKey();
        File.WriteAllText(secret, privatePem);
        File.WriteAllText(Path.Combine(folder, "release-schluessel-oeffentlich.txt"), publicBase64 + "\n");
        Console.WriteLine($"Geheimer Schlüssel: {secret}");
        Console.WriteLine("  Nie ins Repository, nie online; getrennt vom Rechner sichern, mit einer Sicherungskopie. Ohne ihn nimmt das Programm keine Updates mehr an.");
        Console.WriteLine($"Öffentlicher Schlüssel (für UpdateSignature.ReleaseKey): {publicBase64}");
        return 0;
    }

    private static readonly Regex PackageName = new(@"^PDF-Knead-(?<version>[0-9][0-9A-Za-z.\-]*)-(?<platform>(windows|linux|macos)-(x64|arm64))\.(zip|tar\.gz)$");

    /// <summary>
    /// <c>pdf-knead release-signieren &lt;paketordner&gt; --schluessel &lt;geheim.pem&gt; [--hinweise &lt;datei&gt;]</c>: writes
    /// <c>version.json</c> and <c>version.json.sig</c> for the packages in the folder (PDF-Knead-&lt;version&gt;-&lt;plattform&gt;.zip/.tar.gz, all of one
    /// version), checks the signature with the public key of the program, and says what to upload where.
    /// </summary>
    public static int Sign(string folder, string? keyFile, string? notesFile)
    {
        if (keyFile is null || !File.Exists(keyFile))
        {
            Console.Error.WriteLine("Aufruf: pdf-knead release-signieren <paketordner> --schluessel <geheim.pem> [--hinweise <datei>]");
            return 2;
        }
        var packages = new List<UpdatePackage>();
        UpdateVersion? version = null;
        foreach (var file in Directory.EnumerateFiles(folder).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (PackageName.Match(name) is not { Success: true } m) continue;
            if (UpdateVersion.Parse(m.Groups["version"].Value) is not { } v || (version is { } other && other.CompareTo(v) != 0))
            {
                Console.Error.WriteLine($"Pakete verschiedener Versionen im Ordner: {name}");
                return 1;
            }
            version = v;
            using var stream = File.OpenRead(file);
            var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
            packages.Add(new UpdatePackage(m.Groups["platform"].Value, name, $"{UpdateManifest.ReleasePrefix}v{v}/{name}", new FileInfo(file).Length, sha));
        }
        if (version is not { } found || packages.Count == 0)
        {
            Console.Error.WriteLine("Keine Pakete PDF-Knead-<Version>-<Plattform>.zip/.tar.gz im Ordner.");
            return 1;
        }
        var notes = notesFile is not null && File.Exists(notesFile) ? File.ReadAllText(notesFile).Trim() : "";
        var manifest = new UpdateManifest(found, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), notes, packages);
        var json = manifest.ToJson();
        if (UpdateManifest.Parse(json) is null)
        {
            Console.Error.WriteLine("Die Versionsdatei wäre ungültig (Dateiname, Größe oder Adresse) — nichts geschrieben.");
            return 1;
        }
        byte[] signature;
        try
        {
            signature = UpdateSignature.Sign(json, File.ReadAllText(keyFile));
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            Console.Error.WriteLine($"{keyFile} ist kein geheimer P-256-Schlüssel (PEM) — nichts geschrieben.");
            return 1;
        }
        if (UpdateSignature.ReleaseKey.Length > 0 && !UpdateSignature.Verify(json, signature))
        {
            Console.Error.WriteLine("Die Signatur passt nicht zum öffentlichen Schlüssel im Programm — falscher geheimer Schlüssel? Nichts geschrieben.");
            return 1;
        }
        File.WriteAllBytes(Path.Combine(folder, "version.json"), json);
        File.WriteAllBytes(Path.Combine(folder, "version.json.sig"), signature);
        Console.WriteLine($"version.json und version.json.sig für {found} ({string.Join(", ", packages.Select(p => p.Platform))}) geschrieben.");
        Console.WriteLine($"Hochladen: die Pakete in den Release v{found}, version.json und version.json.sig in den Release „aktuell“ (ersetzen).");
        return 0;
    }
}
