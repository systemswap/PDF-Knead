using System.Text.RegularExpressions;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The e-invoice checker is a removable module (decided on 13.11.2026: „falls ich es nicht nutzen möchte, dass es auch einfach und
/// sauber wieder entfernt werden kann“; KONZEPT.md, section 5.24, „Aufbau und Entfernen“): outside its own projects only docking lines
/// marked with „Andockstelle E-Rechnung“ may name it — one line each, which tools/erechnung-entfernen.py deletes —, and its texts live in the
/// module. If one of these tests fails, a dependency crept in that removing the module would break: move it into the module, or make it a
/// marked docking line on purpose (and add it to the list in KONZEPT).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed partial class EInvoiceModuleTests
{
    /// <summary>The mark every docking line carries; tools/erechnung-entfernen.py removes exactly the lines with it.</summary>
    internal const string Marker = "Andockstelle E-Rechnung";

    /// <summary>Files outside the module projects that belong to the module as a whole (the script deletes them) or that it edits otherwise.</summary>
    internal static readonly HashSet<string> ModuleFilesOutside = new(StringComparer.OrdinalIgnoreCase)
    {
        "PdfKnead.sln",                 // the two projects; removed with „dotnet sln remove“
        "tools/fuzz/EInvoiceFuzz.cs",   // the module's fuzzer, next to the general mutator it uses
        "tools/fuzz/EInvoiceSaveFuzz.cs",   // its part for the rules of the PDF side and the save paths (analysis run 15.11.2026)
        "tools/erechnung-entfernen.py", // the removal itself
    };

    [GeneratedRegex(@"\bEInvoice\w*|PdfKnead\.EInvoice")]
    private static partial Regex ModuleName();

    [GeneratedRegex(@"Tr\.T\(""((?:[^""\\]|\\.)*)""")]
    private static partial Regex TranslatedText();

    private static readonly string[] Extensions = [".cs", ".csproj", ".axaml", ".sln", ".props", ".targets", ".ps1", ".sh", ".py"];

    private static readonly string[] SkippedFolders =
        ["obj", "bin", ".git", "TestPDFs", "Normen-ISO", "Patente", "Modelle", "published", "pruefer", "node_modules", "Arbeitsunterlagen"];

    [Fact]
    public void OutsideTheModule_OnlyMarkedDockingLines()
    {
        var repo = InvoiceSourceRulesTests.Repo();
        var breaches = new List<string>();
        var docking = 0;
        var pending = new Stack<string>();
        pending.Push(repo);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (dir == repo && name.StartsWith("PdfKnead.EInvoice", StringComparison.Ordinal)) continue;   // the module itself
                pending.Push(sub);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                var rel = Path.GetRelativePath(repo, file).Replace('\\', '/');
                if (ModuleFilesOutside.Contains(rel)) continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(Marker, StringComparison.Ordinal)) docking++;
                    // every line that names the module is a docking line with the mark: the removal deletes it, nothing else depends on it
                    else if (ModuleName().IsMatch(lines[i])) breaches.Add($"{rel}:{i + 1} nennt den Baustein ohne „{Marker}“");
                }
            }
        }
        Assert.Empty(breaches);
        Assert.True(docking >= 4, $"nur {docking} Andockzeilen gefunden");   // command line (2), fuzzing tool (3) — the list in KONZEPT
    }

    // the texts of the views: {l:T '…'} and <l:T Text="…" />
    [GeneratedRegex(@"\{l:T '((?:[^'\\]|\\.)*)'\}|<l:T Text=""([^""]*)""")]
    private static partial Regex ViewText();

    [Fact]
    public void Texts_AllTranslated_NoneLeftOver_NoneInTheCore()
    {
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in InvoiceSourceRulesTests.CheckerSources())
            foreach (Match m in TranslatedText().Matches(File.ReadAllText(file)))
                used.Add(Regex.Unescape(m.Groups[1].Value));
        // the views of the interface (PdfKnead.EInvoice.Ui)
        var repo = InvoiceSourceRulesTests.Repo();
        foreach (var dir in Directory.EnumerateDirectories(repo, "PdfKnead.EInvoice*").Where(d => !d.EndsWith(".Tests", StringComparison.Ordinal)))
            foreach (var view in Directory.EnumerateFiles(dir, "*.axaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
                foreach (Match m in ViewText().Matches(File.ReadAllText(view)))
                    used.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        Assert.NotEmpty(used);
        var tables = new[] { ("EInvoiceTexts.cs", EInvoiceTexts.Table), ("EInvoiceUiTexts.cs", Ui.EInvoiceUiTexts.Table) };
        // a text the core translates already (a general word like „{0} KB“) needs no second entry in the module
        var missing = used.Where(t => !tables.Any(x => x.Item2.ContainsKey(t)) && !English.Texts.ContainsKey(t)).ToList();
        Assert.True(missing.Count == 0, "ohne englische Übersetzung (in EInvoiceTexts.cs oder EInvoiceUiTexts.cs ergänzen):\n" + string.Join("\n", missing));
        foreach (var (name, table) in tables)
        {
            var leftOver = table.Keys.Where(t => !used.Contains(t)).ToList();
            Assert.True(leftOver.Count == 0, $"übrig in {name}:\n" + string.Join("\n", leftOver));
            // the module's texts are not in the core's table: removing the module leaves nothing there
            var inCore = table.Keys.Where(English.Texts.ContainsKey).ToList();
            Assert.True(inCore.Count == 0, $"{name}, auch in English.cs des Kerns:\n" + string.Join("\n", inCore));
        }
        // and not in both tables of the module
        Assert.Empty(EInvoiceTexts.Table.Keys.Intersect(Ui.EInvoiceUiTexts.Table.Keys));
    }

    [Fact]
    public void InterfaceTests_OneAfterTheOther()
    {
        // two classes running the interface at once share one session and one set of settings (15.11.2026): all of them in the collection
        var tests = Path.Combine(InvoiceSourceRulesTests.Repo(), "PdfKnead.EInvoice.Tests");
        var stray = Directory.EnumerateFiles(tests, "*.cs")
            .Where(f => File.ReadAllText(f) is var text && text.Contains("UiSession" + ".Run(", StringComparison.Ordinal)
                && !text.Contains("[Collection(UiCollection.Name)]", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        Assert.Empty(stray);
    }

    [Fact]
    public void LanguageSwitchingTests_RunAlone()
    {
        // a test that switches the language beside one that expects German texts fails now and then (14.11.2026): all of them in the collection
        var tests = Path.Combine(InvoiceSourceRulesTests.Repo(), "PdfKnead.EInvoice.Tests");
        var stray = Directory.EnumerateFiles(tests, "*.cs")
            .Where(f => File.ReadAllText(f) is var text && text.Contains("Tr.Use(", StringComparison.Ordinal)
                && !text.Contains("[Collection(LanguageCollection.Name)]", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        Assert.Empty(stray);
    }

    [Fact]
    public void Texts_RegisteredWhenTheModuleLoads()
    {
        // the module initializer has run: the translation knows the module's texts without the host doing anything
        Assert.True(Tr.TryEnglish("zu tief verschachtelt", out var english));
        Assert.Equal("nested too deeply", english);
        var before = Tr.Current;
        try
        {
            Tr.Use(Language.German);
            Assert.Equal("zu tief verschachtelt", Tr.T("zu tief verschachtelt"));
        }
        finally
        {
            Tr.Use(before);
        }
    }
}
