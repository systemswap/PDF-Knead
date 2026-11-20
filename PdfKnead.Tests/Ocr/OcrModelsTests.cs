using System.Runtime.CompilerServices;
using PdfKnead.Core.Ocr;

namespace PdfKnead.Tests.Ocr;

/// <summary>
/// The models come with the program (decided on 13.11.2026): small and medium of the text recognition and the field detector,
/// small the default; a remembered „Tiny“ of earlier versions becomes small without losing the other settings; the checksums the publish
/// scripts check (Modelle/ocr/SHA256SUMS) are those in the code.
/// </summary>
public sealed class OcrModelsTests
{
    private static string RepoRoot([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void TwoSizes_SmallTheDefault_FieldDetectorAlongside()
    {
        Assert.Equal([OcrModelSize.Small, OcrModelSize.Medium], Enum.GetValues<OcrModelSize>());
        Assert.Equal(OcrModelSize.Small, new AppSettings().OcrSize);
        Assert.Equal(9, OcrModels.Everything.Count);
        Assert.Contains(OcrModels.FieldDetector, OcrModels.Everything);
        Assert.All(OcrModels.All, f => Assert.Matches("^PP-OCRv6_(small|medium)_(det|rec)_onnx$", f.Folder));
    }

    [Theory]
    [InlineData("\"Tiny\"", OcrModelSize.Small)]
    [InlineData("\"tiny\"", OcrModelSize.Small)]
    [InlineData("0", OcrModelSize.Small)]
    [InlineData("\"7\"", OcrModelSize.Small)]
    [InlineData("\"Groß\"", OcrModelSize.Small)]
    [InlineData("{\"a\":1}", OcrModelSize.Small)]
    [InlineData("\"Medium\"", OcrModelSize.Medium)]
    [InlineData("\"Small\"", OcrModelSize.Small)]
    public void RememberedSize_ReadLeniently_OtherSettingsStay(string value, OcrModelSize expected)
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-settings-").FullName, "settings.json");
        try
        {
            File.WriteAllText(file, $"{{ \"Theme\": \"Dark\", \"OcrSize\": {value}, \"RibbonCollapsed\": true }}");
            var loaded = AppSettings.Load(file);
            Assert.Equal(expected, loaded.OcrSize);
            Assert.Equal(ThemeChoice.Dark, loaded.Theme);   // an unknown size must not cost the other settings
            Assert.True(loaded.RibbonCollapsed);
            loaded.Save(file);
            Assert.Equal(expected, AppSettings.Load(file).OcrSize);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    /// <summary>The checksum list the publish scripts check is the one in the code (only where the local store „Modelle“ is there).</summary>
    [Fact]
    public void StoreChecksums_AreThoseInTheCode()
    {
        var sums = Path.Combine(RepoRoot(), "Modelle", "ocr", "SHA256SUMS");
        if (!File.Exists(sums)) return;
        var listed = File.ReadAllLines(sums).Where(l => l.Trim().Length > 0)
            .Select(l => l.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)).Select(p => (Hash: p[0], Path: p[1].TrimStart('*'))).ToList();
        Assert.Equal(OcrModels.Everything.Select(f => (f.Sha256, $"{f.Folder}/{f.Name}")).Order(), listed.Select(l => (l.Hash, l.Path)).Order());
    }
}
