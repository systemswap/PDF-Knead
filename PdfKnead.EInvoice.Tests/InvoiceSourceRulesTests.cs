using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The building rules of security for the e-invoice checker, checked in its source (KONZEPT.md, section 5.24, „Bauvorgaben Sicherheit“ 1 and 4):
/// invoice XML is read only through <c>InvoiceXml.Read</c> — no other XML reader is created in the checker's code, no DOM loader (they allow a
/// DTD or resolve on their own), no XSLT, no XPath document, and no regular expression without a time limit.
/// </summary>
public sealed class InvoiceSourceRulesTests
{
    internal static string Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    /// <summary>The checker's source: every project of the module (PdfKnead.EInvoice, later its user interface), without build output.</summary>
    internal static IEnumerable<string> CheckerSources()
    {
        var repo = Repo();
        foreach (var project in Directory.EnumerateDirectories(repo, "PdfKnead.EInvoice*"))
        {
            if (project.EndsWith(".Tests", StringComparison.Ordinal)) continue;
            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
                if (!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    yield return file;
        }
    }

    private static readonly (Regex Pattern, string Why)[] Forbidden =
    [
        (new(@"\bXDocument\s*\.\s*(Load|Parse)\b"), "XDocument lässt intern eine DTD zu"),
        (new(@"\bXElement\s*\.\s*(Load|Parse)\b"), "XElement liest ohne die Grenzen"),
        (new(@"\bnew\s+XmlDocument\b|\bXmlDocument\s*\.\s*Load"), "XmlDocument löst selbst auf"),
        (new(@"\bXPathDocument\b"), "XPathDocument liest ohne die Grenzen"),
        (new(@"\bXslCompiledTransform\b|\bXslTransform\b"), "kein XSLT"),
        (new(@"\bXmlTextReader\b"), "der alte Leser löst DTDs auf"),
        (new(@"\bXmlSerializer\b|\bDataContractSerializer\b"), "Serialisierer lesen ohne die Grenzen"),
    ];

    [Fact]
    public void OnlyTheHardenedReader_ReadsInvoiceXml()
    {
        var sources = CheckerSources().ToList();
        Assert.Contains(sources, f => f.EndsWith("InvoiceXml.cs", StringComparison.Ordinal));
        var breaches = new List<string>();
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (var (pattern, why) in Forbidden)
                if (pattern.IsMatch(text)) breaches.Add($"{Path.GetFileName(file)}: {why}");
            // the one reader is created in InvoiceXml.cs, with its explicit settings
            if (!file.EndsWith("InvoiceXml.cs", StringComparison.Ordinal) && Regex.IsMatch(text, @"\bXmlReader\s*\.\s*Create\b"))
                breaches.Add($"{Path.GetFileName(file)}: ein zweiter XmlReader neben InvoiceXml.Read");
        }
        Assert.Empty(breaches);
    }

    [Fact]
    public void Sources_NoInvisibleCharacters()
    {
        // „Trojan Source“: a bidi control or a zero-width character in the source makes code read otherwise than it runs. Test values with such
        // characters are written as escapes („\u202E“) — once a tool wrote them as the characters themselves (14.11.2026, before this test).
        var repo = Repo();
        var files = Directory.EnumerateDirectories(repo, "PdfKnead.EInvoice*")
            .SelectMany(p => Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetExtension(f) is ".cs" or ".py" or ".tsv" or ".csproj")
            .Concat([Path.Combine(repo, "tools", "fuzz", "EInvoiceFuzz.cs"), Path.Combine(repo, "tools", "fuzz", "EInvoiceSaveFuzz.cs"),
                Path.Combine(repo, "tools", "erechnung-entfernen.py")])
            .ToList();
        Assert.True(files.Count > 30, files.Count.ToString());
        var breaches = new List<string>();
        foreach (var file in files)
        {
            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                foreach (var r in line.EnumerateRunes())
                    if (InvoiceFormat.Hidden(r))
                        breaches.Add($"{Path.GetRelativePath(repo, file)}:{number}: U+{r.Value:X4}");
            }
        }
        Assert.Empty(breaches);
    }

    [Fact]
    public void RegularExpressions_OnlyWithTimeLimit()
    {
        var breaches = new List<string>();
        foreach (var file in CheckerSources())
        {
            var text = File.ReadAllText(file);
            // a pattern built at run time needs a time limit; generated patterns ([GeneratedRegex]) carry one as their third argument
            foreach (Match m in Regex.Matches(text, @"new\s+Regex\s*\(([^;]*)\)"))
                if (!m.Groups[1].Value.Contains("TimeSpan", StringComparison.Ordinal)) breaches.Add($"{Path.GetFileName(file)}: {m.Value}");
            foreach (Match m in Regex.Matches(text, @"\bRegex\s*\.\s*(IsMatch|Match|Matches|Replace|Split)\s*\(([^;]*)\)"))
                if (!m.Groups[2].Value.Contains("TimeSpan", StringComparison.Ordinal)) breaches.Add($"{Path.GetFileName(file)}: {m.Value}");
            foreach (Match m in Regex.Matches(text, @"\[GeneratedRegex\s*\(([^\]]*)\)\]"))
                if (m.Groups[1].Value.Split(',').Length < 3) breaches.Add($"{Path.GetFileName(file)}: {m.Value} ohne Zeitgrenze");
        }
        Assert.Empty(breaches);
    }
}
