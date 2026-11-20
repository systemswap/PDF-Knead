using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PdfKnead.Tests;

/// <summary>Tests that change state of the whole process (the working folder) and so run alone.</summary>
[CollectionDefinition(nameof(ProcessStateCollection), DisableParallelization = true)]
public sealed class ProcessStateCollection;

/// <summary>The working folder belongs to the whole process: changing it while other tests run could send any relative path elsewhere.</summary>
[Collection(nameof(ProcessStateCollection))]
public sealed class WorkingFolderTests
{
    /// <summary>System tools (lp, lpstat) only from the system folders: a file of the same name in the working folder is never started.</summary>
    [Fact]
    public void PrinterTools_OnlyFromSystemFolders()
    {
        var dir = Directory.CreateTempSubdirectory("pdf-knead-werkzeug-").FullName;
        File.WriteAllText(Path.Combine(dir, "pdfknead-werkzeug"), "#!/bin/sh\necho gestartet\n");
        var before = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = dir;
            Assert.Throws<System.ComponentModel.Win32Exception>(() => Core.Printing.Printers.ToolPath("pdfknead-werkzeug"));
        }
        finally
        {
            Environment.CurrentDirectory = before;
        }
        var full = Path.Combine(dir, "pdfknead-werkzeug");
        Assert.Equal(full, Core.Printing.Printers.ToolPath(full));
    }
}

/// <summary>
/// A test that switches state shared by the whole process — a static setting of the program (…ForTests, limits, the list of leftovers), the
/// working folder — must run alone: a test running at the same time works with the switched state, and the result depends on timing. Twice such
/// a race made a test fail now and then (the list of leftovers, docs/FEHLERGEDAECHTNIS.md, M6; the folder of the CJK fonts, No. 774). This
/// test reads the sources of the tests and fails for every such assignment in a class that is not in a collection running alone. Settings
/// marked [ThreadStatic] in the program belong to one thread and are allowed anywhere.
/// </summary>
public sealed class TestIsolationTests
{
    private static string Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    /// <summary>Assignments to state of the whole process: Type.Member = … for the settings below and every …ForTests.</summary>
    private static readonly Regex Shared = new(
        @"(?<![\w.])(?:Core\.)?(?:[\w]+\.)*(?<type>[A-Z]\w*)\.(?<member>\w*ForTests|MaxInterned|LeftoverList|Protector|Max\w+Bytes|CurrentDirectory)\s*=(?!=)",
        RegexOptions.Compiled);

    [Fact]
    public void SharedSwitches_OnlyInCollectionsThatRunAlone()
    {
        var repo = Repo();
        var tests = Directory.EnumerateFiles(Path.Combine(repo, "PdfKnead.Tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => f, File.ReadAllText);
        // collections that run alone
        var alone = tests.Values.SelectMany(t => Regex.Matches(t, @"\[CollectionDefinition\((?:nameof\()?(\w+(?:\.\w+)?)\)?\s*,\s*DisableParallelization\s*=\s*true\)\]\s*(?:public\s+)?(?:sealed\s+)?(?:partial\s+)?class\s+(\w+)"))
            .SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }).ToHashSet();
        // a collection named by a constant (Collection(CjkFontsCollection.Name)) counts by its class; nameof(Pdf.LimitCollection) by its last part
        // members marked [ThreadStatic] in the program
        var program = Directory.EnumerateFiles(repo, "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}PdfKnead.Core{Path.DirectorySeparatorChar}") || f.Contains($"{Path.DirectorySeparatorChar}PdfKnead.Ui{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        var threadStatic = program.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\[ThreadStatic\][^;{=]*?\b(\w+)\s*(?:;|=)").Select(m => m.Groups[1].Value)).ToHashSet();
        var faults = new List<string>();
        foreach (var (file, text) in tests)
        {
            foreach (Match m in Shared.Matches(text))
            {
                var member = m.Groups["member"].Value;
                if (threadStatic.Contains(member)) continue;
                if (InModuleInitializer(text, m.Index)) continue;
                var collection = CollectionOfClassAt(text, m.Index);
                var runsAlone = collection is not null && collection.Split('.').Any(alone.Contains);
                if (!runsAlone)
                    faults.Add($"{Path.GetFileName(file)}:{text[..m.Index].Count(c => c == '\n') + 1}: {m.Groups["type"].Value}.{member} in {ClassAt(text, m.Index)} (Sammlung: {collection ?? "keine"})");
            }
        }
        Assert.True(faults.Count == 0, "Prozessweite Schalter in Tests, die gleichzeitig mit anderen laufen:\n" + string.Join("\n", faults));
    }

    /// <summary>The top-level class (declared at the start of a line) the position lies in, and the attribute lines just above it.</summary>
    private static (string Name, string Attributes)? ClassAround(string text, int at)
    {
        var declaration = Regex.Matches(text[..at], @"(?m)^(?:(?:public|internal|sealed|static|partial|file|abstract)\s+)*class\s+(\w+)").LastOrDefault();
        if (declaration is null) return null;
        var attributes = new List<string>();
        var lines = text[..declaration.Index].TrimEnd('\r', '\n').Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[')) attributes.Add(line);
            else if (!line.StartsWith("///")) break;
        }
        return (declaration.Groups[1].Value, string.Join("\n", attributes));
    }

    private static string ClassAt(string text, int at) => ClassAround(text, at)?.Name ?? "?";

    private static string? CollectionOfClassAt(string text, int at)
    {
        var m = Regex.Match(ClassAround(text, at)?.Attributes ?? "", @"\[Collection\((?:nameof\()?([\w.]+)\)?\)\]");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Set once in a [ModuleInitializer], before any test runs.</summary>
    private static bool InModuleInitializer(string text, int at)
    {
        var method = Regex.Matches(text[..at], @"(\[[^\]\n]*\]\s*)*(?:internal|public|private)\s+static\s+void\s+\w+\(\)").LastOrDefault();
        return method is not null && method.Value.Contains("ModuleInitializer");
    }
}
