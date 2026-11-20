using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Ocr;

/// <summary>
/// The two sizes of the text recognition PP-OCRv6 that come with the program (KONZEPT.md, section 5.9; decided on 13.11.2026:
/// small and medium in the package, small the default, tiny no longer): the larger, the more exact and the slower.
/// </summary>
public enum OcrModelSize
{
    /// <summary>7.7 million parameters: balanced (the default).</summary>
    Small,

    /// <summary>34.5 million parameters: the most exact, slower.</summary>
    Medium,
}

/// <summary>
/// Reads a remembered size leniently: a value this version does not know — „Tiny“ of earlier versions, a number, anything else — becomes
/// <see cref="OcrModelSize.Small"/>, so the other settings are not lost (an unknown enum value would make the whole settings file unreadable).
/// </summary>
public sealed class OcrModelSizeJsonConverter : JsonConverter<OcrModelSize>
{
    public override OcrModelSize Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<OcrModelSize>(reader.GetString(), ignoreCase: true, out var size) &&
            Enum.IsDefined(size) && !int.TryParse(reader.GetString(), out _))
            return size;
        reader.Skip();
        return OcrModelSize.Small;
    }

    public override void Write(Utf8JsonWriter writer, OcrModelSize value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

/// <summary>A file of a model: where it lies in the folder of the models, how large it is and its SHA-256 checksum.</summary>
/// <param name="Folder">The folder of the model (as published, e.g. "PP-OCRv6_medium_det_onnx"); locally its subfolder.</param>
/// <param name="Name">The file name.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Sha256">The checksum, lower-case hexadecimal.</param>
public sealed record OcrModelFile(string Folder, string Name, long Size, string Sha256);

/// <summary>
/// The models of the text recognition and the field detector (KONZEPT.md, sections 5.9 and 5.9a). They come with the program, in the folder
/// „ocr“ next to it (decided on 13.11.2026: nothing is loaded from the network any more; the publish scripts copy them from the
/// local store „Modelle/ocr“ after checking every checksum). Without them everything else works as before.
/// </summary>
public static class OcrModels
{
    /// <summary>Name of the folder next to the program.</summary>
    public const string FolderName = "ocr";

    /// <summary>For tests: a folder used instead.</summary>
    internal static string? FolderForTests;

    /// <summary>The folder next to the program.</summary>
    public static string ProgramFolder => FolderForTests ?? Path.Combine(AppLocations.ProgramFolder, FolderName);

    /// <summary>All files of both sizes, as published on 30.10.2026 (sizes and checksums of that state).</summary>
    public static IReadOnlyList<OcrModelFile> All { get; } =
    [
        new("PP-OCRv6_small_det_onnx", "inference.onnx", 9_880_512, "d73e0058b7a8086bbd57f3d10b8bcd4ff95363f67e06e2762b5e814fe9c9410e"),
        new("PP-OCRv6_small_det_onnx", "inference.yml", 885, "193f435274bf9f0b5f71a929bbfbcf148282df7e633b34e7c373e8f44741b516"),
        new("PP-OCRv6_small_rec_onnx", "inference.onnx", 21_159_378, "5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634"),
        new("PP-OCRv6_small_rec_onnx", "inference.yml", 150_579, "ab078671bb49f06228eadccd34f1bb501e157f7a047095ffb943ba81512c77d1"),
        new("PP-OCRv6_medium_det_onnx", "inference.onnx", 62_032_837, "eb13b44b25bb36f89528b68720af8a61d9cf381176107f465db1757b65d086e1"),
        new("PP-OCRv6_medium_det_onnx", "inference.yml", 886, "7298d5ead546584af2504d03355f881ac7a7bc0eb1e282d3e159277c1d0af871"),
        new("PP-OCRv6_medium_rec_onnx", "inference.onnx", 76_554_979, "9c09abf0957f7968c7586464b7397b84ad2387a0497a351af40e9acc71b673ba"),
        new("PP-OCRv6_medium_rec_onnx", "inference.yml", 150_580, "991b700facf5b50a7de193468207d5f4255b538dde0d312ae3b7c7a9b6873129"),
    ];

    /// <summary>
    /// The field detector FFDetr for „Felder erkennen“ (KONZEPT.md, section 5.9a): an RF-DETR network trained on the CommonForms data, as ONNX
    /// at 1024 × 1024 (Apache-2.0, model and data by Joe Barrow, converted by DocSpring), as published on 31.10.2026.
    /// </summary>
    public static OcrModelFile FieldDetector { get; } =
        new("ffdetr-onnx", "ffdetr-1024.onnx", 125_093_295, "4b98b3e25341f64a289034a8f7a42d945c7e528aae23fa912eaa2ba5f610c6e2");

    /// <summary>Everything that comes with the program: both sizes of the text recognition and the field detector.</summary>
    public static IReadOnlyList<OcrModelFile> Everything { get; } = [.. All, FieldDetector];

    /// <summary>Where the field detector lies; null if it is not there.</summary>
    public static string? FieldDetectorPath => Find(FieldDetector);

    /// <summary>The name of a size in the folder names ("small", "medium").</summary>
    public static string Key(OcrModelSize size) => size.ToString().ToLowerInvariant();

    /// <summary>The files of one size: detection (where text lies) and recognition (what it says), each the network and its description.</summary>
    public static IEnumerable<OcrModelFile> FilesOf(OcrModelSize size) => All.Where(f => f.Folder.StartsWith($"PP-OCRv6_{Key(size)}_", StringComparison.Ordinal));

    /// <summary>Where a file lies if it is there and has its size; null otherwise.</summary>
    /// <param name="file">The file.</param>
    /// <returns>The path, or null.</returns>
    public static string? Find(OcrModelFile file)
    {
        var path = Path.Combine(ProgramFolder, file.Folder, file.Name);
        return File.Exists(path) && new FileInfo(path).Length == file.Size ? path : null;
    }

    /// <summary>Whether all files of a size are there (by size; the checksums were checked when the package was built).</summary>
    public static bool IsInstalled(OcrModelSize size) => FilesOf(size).All(f => Find(f) is not null);

    /// <summary>The sizes that are there.</summary>
    public static IReadOnlyList<OcrModelSize> Installed => [.. Enum.GetValues<OcrModelSize>().Where(IsInstalled)];

    /// <summary>The size to use when the one wished is not there: small first, then medium; the wish if none is there.</summary>
    public static OcrModelSize Available(OcrModelSize wished) =>
        IsInstalled(wished) ? wished : IsInstalled(OcrModelSize.Small) ? OcrModelSize.Small : IsInstalled(OcrModelSize.Medium) ? OcrModelSize.Medium : wished;

    /// <summary>The folder of a model of a size (detection or recognition), where its files lie; null if they are not all there.</summary>
    /// <param name="size">The size.</param>
    /// <param name="recognition">The recognition model (what text says) instead of the detection model (where text lies).</param>
    /// <returns>The folder, or null.</returns>
    public static string? FolderOf(OcrModelSize size, bool recognition)
    {
        var files = FilesOf(size).Where(f => f.Folder.EndsWith(recognition ? "_rec_onnx" : "_det_onnx", StringComparison.Ordinal)).ToList();
        var paths = files.Select(Find).ToList();
        return paths.All(p => p is not null) ? Path.GetDirectoryName(paths[0]) : null;
    }

    /// <summary>Whether a file has the size and checksum it must have (the publish scripts and „pdf-knead modelle“ check every file).</summary>
    /// <param name="file">The file.</param>
    /// <param name="data">Its content.</param>
    /// <returns>True if size and SHA-256 match.</returns>
    public static bool Matches(OcrModelFile file, Stream data)
    {
        if (data.CanSeek && data.Length != file.Size) return false;
        return Convert.ToHexStringLower(SHA256.HashData(data)) == file.Sha256;
    }
}
