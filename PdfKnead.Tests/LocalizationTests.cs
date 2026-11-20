using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PdfKnead.Tests.Ui;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests;

/// <summary>
/// Tests for localisation: English must know every text, and language files from the
/// languages folder are read. The tests switch the language, so they run in the same
/// collection as the UI tests, never in parallel with them.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed partial class LocalizationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdf-knead-lang-").FullName;

    public void Dispose()
    {
        Tr.Use(Language.German);
        Directory.Delete(_dir, recursive: true);
    }

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    [GeneratedRegex(@"\{l:T '([^']*)'\}")]
    private static partial Regex XamlAttribute();

    [GeneratedRegex(@"<l:T Text=""([^""]*)""")]
    private static partial Regex XamlElement();

    [GeneratedRegex(@"Tr\.T\(""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CsCall();

    /// <summary>Collects all German texts that the user interface shows translated.</summary>
    /// <returns>The texts in ordinal order, without texts that contain no letters.</returns>
    internal static SortedSet<string> AllTexts()
    {
        var texts = new SortedSet<string>(StringComparer.Ordinal);
        var root = RepoRoot();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "PdfKnead.Ui"), "*.axaml", SearchOption.AllDirectories))
        {
            var xaml = File.ReadAllText(file);
            foreach (Match m in XamlAttribute().Matches(xaml)) texts.Add(WebUtility.HtmlDecode(m.Groups[1].Value));
            foreach (Match m in XamlElement().Matches(xaml)) texts.Add(WebUtility.HtmlDecode(m.Groups[1].Value));
        }

        foreach (var project in new[] { "PdfKnead.Ui", "PdfKnead.Core", "PdfKnead.Rendering", "PdfKnead.Cli" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in CsCall().Matches(File.ReadAllText(file)))
                texts.Add(Regex.Unescape(m.Groups[1].Value));
        }

        // lists whose values are German texts that are shown translated
        foreach (var t in SettingsViewModel.ThemeOptions) texts.Add(t.Label);
        foreach (var v in VersionOption.All) texts.Add(v.Label);
        foreach (var c in CompressionOption.All) texts.Add(c.Label);
        foreach (var f in MainViewModel.FontOptions) texts.Add(f.Label);
        foreach (var c in MainViewModel.ColorOptions) texts.Add(c.Label);
        foreach (var c in DocumentViewModel.MeasureColors) texts.Add(c.Label);
        foreach (var c in DocumentViewModel.MeasureDecimalChoices) texts.Add(c.Label);
        texts.Add("Eigene Farbe");
        texts.UnionWith(DocumentViewModel.PropertyLabels);
        texts.UnionWith(FieldPropertiesViewModel.AllLabels);
        texts.UnionWith(ElementPropertiesViewModel.AllLabels);
        texts.UnionWith(CodePropertiesViewModel.AllLabels);
        texts.UnionWith(RichTextPropertiesViewModel.AllLabels);
        texts.UnionWith(PdfKnead.Core.Fonts.SignatureFonts.Errors);

        // texts without letters (symbols, numbers) need no translation
        texts.RemoveWhere(t => !t.Any(char.IsLetter));
        return texts;
    }

    [Fact]
    public void English_CoversEveryText()
    {
        var missing = AllTexts().Where(t => !English.Texts.ContainsKey(t)).ToList();
        Assert.True(missing.Count == 0,
            $"{missing.Count} Texte ohne englische Übersetzung (in English.cs ergänzen):\n" +
            string.Join("\n", missing.Select(t => $"[{Quote(t)}] = {Quote(t)},")));
    }

    /// <summary>
    /// No German text may appear twice in English.cs: the table is filled through the indexer, so a second
    /// entry silently overwrites the first.
    /// </summary>
    [Fact]
    public void English_HasNoDuplicateKeys()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "PdfKnead.Core", "Localization", "English.cs"));
        var keys = Regex.Matches(source, @"^\s*\[(""(?:[^""\\]|\\.)*"")\]\s*=", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        Assert.True(keys.Count > 50, $"nur {keys.Count} Einträge gefunden — Muster passt nicht mehr");
        var duplicates = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Doppelt in English.cs: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void English_HasNoLeftovers()
    {
        var used = AllTexts();
        var stale = English.Texts.Keys.Where(k => !used.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Nicht mehr benutzt:\n" + string.Join("\n", stale));
    }

    [GeneratedRegex(@"\{\d+(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    [Fact]
    public void English_KeepsPlaceholders()
    {
        foreach (var (german, english) in English.Texts)
        {
            var a = Placeholder().Matches(german).Select(m => m.Value).Order().ToList();
            var b = Placeholder().Matches(english).Select(m => m.Value).Order().ToList();
            Assert.True(a.SequenceEqual(b), $"Platzhalter weichen ab:\n{german}\n{english}");
        }
    }

    [Fact]
    public void Switching_ChangesTexts_AndFallsBackToEnglish()
    {
        File.WriteAllText(Path.Combine(_dir, "fr.json"), """
            {
              "code": "fr",
              "name": "Français",
              "flag": { "stripes": "vertical", "colors": ["#002395", "#FFFFFF", "#ED2939"] },
              "texts": { "Öffnen": "Ouvrir" }
            }
            """);
        var all = Languages.All(out var problems, _dir);
        var fr = all.Single(l => l.Code == "fr");

        Assert.Empty(problems);
        Assert.Equal(["de", "en", "fr"], all.Select(l => l.Code));
        Assert.True(fr.Flag.Vertical);

        Tr.Use(fr);
        Assert.Equal("Ouvrir", Tr.T("Öffnen"));
        Assert.Equal("Print", Tr.T("Drucken"));                     // missing in fr.json → English
        Assert.Equal("Unbekannter Text", Tr.T("Unbekannter Text"));  // missing everywhere → German

        Tr.Use(Language.German);
        Assert.Equal("Öffnen", Tr.T("Öffnen"));
    }

    [Fact]
    public void BrokenFiles_AreReported_NotLoaded()
    {
        File.WriteAllText(Path.Combine(_dir, "kaputt.json"), "{ \"code\": \"xx\", \"texts\": {");
        File.WriteAllText(Path.Combine(_dir, "de.json"), "{ \"code\": \"de\", \"name\": \"Deutsch 2\" }");

        var all = Languages.All(out var problems, _dir);

        Assert.Equal(["de", "en"], all.Select(l => l.Code));
        Assert.Equal(2, problems.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Template_FromGermanOrEnglish_ContainsEveryText_AndLoads(bool fromGerman)
    {
        var path = Languages.WriteTemplate(fromGerman, Path.Combine(_dir, Languages.TemplateFileName(fromGerman)));

        // The template itself is not loaded as a language (underscore prefix) …
        Assert.DoesNotContain(Languages.All(out _, _dir), l => l.Code == "xx");

        // … but a renamed copy is, with all texts in the chosen source language
        File.Copy(path, Path.Combine(_dir, "xx.json"));
        var xx = Languages.All(out var problems, _dir).Single(l => l.Code == "xx");
        Assert.Empty(problems);
        Assert.Equal(English.Texts.Count, xx.Texts.Count);
        Assert.Equal(fromGerman ? "Drucken" : "Print", xx.Texts["Drucken"]);
    }

    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData("en-US", "en")]
    [InlineData("fr-FR", "en")]   // no French file → English
    public void FirstStart_FollowsTheSystem(string culture, string expected)
    {
        var all = Languages.All(out _, _dir);
        Assert.Equal(expected, Languages.ForSystem(all, new System.Globalization.CultureInfo(culture)).Code);
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
}
