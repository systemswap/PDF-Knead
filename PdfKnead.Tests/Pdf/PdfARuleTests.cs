using System.Reflection;
using System.Xml;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The catalogue of PDF/A rules (KONZEPT.md, section 5.21, „Umbau vom Regelwerk her“): complete against the machine-readable rule list of the
/// reference validator, and every rule in the table docs/PDFA-REGELN.md.
/// </summary>
public sealed partial class PdfARuleTests
{
    /// <summary>Every rule of the catalogue with its name.</summary>
    internal static IReadOnlyList<(string Name, PdfARule Rule)> Catalogue { get; } = typeof(PdfARules)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(PdfARule))
        .Select(f => (f.Name, (PdfARule)f.GetValue(null)!))
        .ToArray();

    private static readonly (string File, PdfAConformance Part)[] Profiles =
        [("PDFA-1B.xml", PdfAConformance.A1b), ("PDFA-2B.xml", PdfAConformance.A2b), ("PDFA-2U.xml", PdfAConformance.A2u), ("PDFA-3B.xml", PdfAConformance.A3b),
         ("PDFA-3U.xml", PdfAConformance.A3u), ("PDFA-4.xml", PdfAConformance.A4), ("PDFA-4F.xml", PdfAConformance.A4f),
         ("PDFA-4E.xml", PdfAConformance.A4e),
         ("PDFA-2A.xml", PdfAConformance.A2a), ("PDFA-3A.xml", PdfAConformance.A3a), ("PDFA-1A.xml", PdfAConformance.A1a)];

    /// <summary>The ids of a part in the catalogue, with those its rules cover besides.</summary>
    private static HashSet<string> Ids(PdfAConformance part)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, rule) in Catalogue)
            if (rule.Id(part) is { } id)
            {
                ids.Add(id);
                ids.UnionWith(rule.Also);
            }
        return ids;
    }

    /// <summary>The ids of a rule list file (clause and test number of each rule).</summary>
    private static HashSet<string> ListIds(string path)
    {
        var xml = new XmlDocument();
        xml.Load(path);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (XmlElement id in xml.GetElementsByTagName("id", "*"))
            ids.Add($"{id.GetAttribute("clause")}-{id.GetAttribute("testNumber")}");
        return ids;
    }

    /// <summary>
    /// Only with the rule lists at hand: PDFKNEAD_PDFA_REGELN names the folder with the validation profiles of the reference validator, else
    /// the folder of the reference validator in the project (<c>tools/pruefer/pdfa</c>, kept locally only) is searched for them
    /// (docs/PRUEFUNGEN.md, section 6p). Every rule there is in the catalogue for that part, and the catalogue has none the list lacks.
    /// </summary>
    [Fact]
    public void Catalogue_CoversTheRuleListOfEveryPart()
    {
        if (RuleLists() is not { } folder) return;
        foreach (var (file, part) in Profiles)
        {
            var list = ListIds(Path.Combine(folder, file));
            var ours = Ids(part);
            Assert.True(list.IsSubsetOf(ours), $"{part.Name()}: im Katalog fehlen {string.Join(", ", list.Except(ours).Order())}");
            var mains = Catalogue.Select(c => c.Rule.Id(part)).OfType<string>().ToHashSet();
            Assert.True(mains.IsSubsetOf(list), $"{part.Name()}: der Katalog nennt {string.Join(", ", mains.Except(list).Order())}, die die Liste nicht kennt");
        }
    }

    /// <summary>
    /// The folder with the validation profiles: PDFKNEAD_PDFA_REGELN, else the rule lists the catalogue follows
    /// (<c>tools/pruefer/veraPDF-validation-profiles/PDF_A</c>, the state of 29.10.2026), else the ones of the reference validator in the
    /// project; null if none. The validator's own profiles (1.30.3) are older than the catalogue (6.2.5-6 still in part 4, 6.1.9-2,
    /// 6.2.3-4, 6.2.4.6-1 not yet): searched first they failed the test on the Linux computer, where they are at hand (publish of 1.1.542).
    /// </summary>
    private static string? RuleLists([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_PDFA_REGELN") is { Length: > 0 } given) return Directory.Exists(given) ? given : null;
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
        {
            if (Path.Combine(dir.FullName, "tools", "pruefer", "veraPDF-validation-profiles", "PDF_A") is var current && Directory.Exists(current)
                && Directory.EnumerateFiles(current, "PDFA-2B.xml", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault() is { } found)
                return found;
            if (Path.Combine(dir.FullName, "tools", "pruefer", "pdfa", "profile") is var profiles && Directory.Exists(profiles))
                return Directory.EnumerateFiles(profiles, "PDFA-2B.xml", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault();
        }
        return null;
    }

    [Fact]
    public void EveryRule_HasAnIdInSomePart_AndEveryIdIsUniqueInItsPart()
    {
        foreach (var (name, rule) in Catalogue)
            Assert.True(rule.Two is not null || rule.Four is not null || rule.One is not null || rule.Three is not null and not "-", name);
        foreach (var (_, part) in Profiles)
        {
            // part 4 has one rule (6.6.3-1) for the triggers of the catalogue, pages and annotations, which part 2 tells apart
            var twice = Catalogue.Select(c => c.Rule.Id(part)).OfType<string>().GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(twice.All(id => part.Part() == 4 && id == "6.6.3-1"), $"{part.Name()}: doppelt {string.Join(", ", twice)}");
        }
    }

    /// <summary>
    /// Rules whose area of the table is not yet rebuilt (docs/PDFA-REGELN.md, column „Testfall“ empty): the list shrinks with each area and
    /// is empty when the rebuild is done.
    /// </summary>
    private static readonly HashSet<string> NotYet = [];

    [Fact]
    public void EveryRule_HasACase()
    {
        var names = Catalogue.Select(c => c.Name).ToHashSet();
        Assert.DoesNotContain(PdfARuleCases.All.Keys.Concat(PdfARuleCases.Uncraftable.Keys).Concat(NotYet), n => !names.Contains(n));
        var missing = names.Where(n => !PdfARuleCases.All.ContainsKey(n) && !PdfARuleCases.Uncraftable.ContainsKey(n) && !NotYet.Contains(n)).ToList();
        Assert.True(missing.Count == 0, "Ohne Testfall: " + string.Join(", ", missing));
        Assert.DoesNotContain(PdfARuleCases.All.Keys, NotYet.Contains);
    }

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var (name, cases) in PdfARuleCases.All)
            for (var i = 0; i < cases.Length; i++) data.Add(name, i);
        return data;
    }

    /// <summary>
    /// ISO 32000-2, F.3.3 (ISO 32000-1, F.2.2): the linearization dictionary lies wholly within the first 1 024 bytes — the file of the case
    /// „FileIdLinearized“ with a comment of 1 100 bytes before it is not linearized, so the /ID of the first page's trailer is not compared with
    /// the last one. Before a dictionary further on still counted (M3, Annex F, 23.11.2026). Without the comment the rule is found.
    /// </summary>
    [Fact]
    public void LinearizationDictionaryBeyond1024Bytes_NotReadAsLinearized()
    {
        var id = Catalogue.Single(c => c.Name == "FileIdLinearized").Rule.Id(PdfAConformance.A1b);
        using (var near = Core.Pdf.Document.PdfDocument.Open(PdfARuleCases.LinearizedAfter("")))
            Assert.Contains(PdfACheck.Check(near, PdfAConformance.A1b), v => v.Id == id);
        using var far = Core.Pdf.Document.PdfDocument.Open(PdfARuleCases.LinearizedAfter("%" + new string('x', 1100) + "\n"));
        Assert.DoesNotContain(PdfACheck.Check(far, PdfAConformance.A1b), v => v.Id == id);
    }

    /// <summary>
    /// The check finds the rule in the crafted file; the copy meets it and conforms, or is refused for exactly this rule. With
    /// PDFKNEAD_PDFA_FAELLE set to a folder, the originals go to its subfolder „original“ and the conforming copies to „kopien-&lt;part&gt;“, for the
    /// counter-check by the reference validator (docs/PRUEFUNGEN.md, section 6p).
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Case_TheCheckFindsIt_TheCopyMeetsIt(string name, int index)
    {
        var rule = Catalogue.Single(c => c.Name == name).Rule;
        var c = PdfARuleCases.All[name][index];
        var id = c.Id ?? rule.Id(c.Part);
        Assert.NotNull(id);
        using var doc = Core.Pdf.Document.PdfDocument.Open(c.File());
        var found = PdfACheck.Check(doc, c.Part);
        Assert.True(found.Any(v => v.Id == id), $"{name} ({c.Note}): die Prüfung findet {id} nicht — gefunden: {PdfATests.Describe(found)}");
        var copy = PdfAExport.Write(doc, Core.Pdf.Editing.PageItem.From(doc), c.Part);
        if (Environment.GetEnvironmentVariable("PDFKNEAD_PDFA_FAELLE") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(Path.Combine(folder, "original"));
            File.WriteAllBytes(Path.Combine(folder, "original", $"{name}-{index}.pdf"), c.File());
            if (copy.Conforms)
            {
                var copies = Path.Combine(folder, "kopien-" + c.Part.Name().Replace("PDF/A-", "", StringComparison.Ordinal));
                Directory.CreateDirectory(copies);
                File.WriteAllBytes(Path.Combine(copies, $"{name}-{index}.pdf"), copy.Bytes);
            }
        }
        if (c.Outcome == RuleOutcome.Met)
            Assert.True(copy.Conforms, $"{name} ({c.Note}): die Kopie erfüllt PDF/A nicht — {PdfATests.Describe(copy.Violations)}");
        else
            Assert.True(!copy.Conforms && copy.Violations.Any(v => v.Id == id), $"{name} ({c.Note}): die Kopie wird nicht wegen {id} abgelehnt — {PdfATests.Describe(copy.Violations)}");
    }

    /// <summary>
    /// The copy shows what the display shows of the original (the principle of the writer: what PDF-Knead can show it writes anew in
    /// conformance): every page of a case met renders to the same pixels.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Case_TheCopyLooksAsTheOriginalIsShown(string name, int index)
    {
        var c = PdfARuleCases.All[name][index];
        if (c.Outcome != RuleOutcome.Met || c.Look is not null) return;
        using var doc = Core.Pdf.Document.PdfDocument.Open(c.File());
        var copy = PdfAExport.Write(doc, Core.Pdf.Editing.PageItem.From(doc), c.Part);
        using var written = Core.Pdf.Document.PdfDocument.Open(copy.Bytes);
        Assert.Equal(doc.PageCount, written.PageCount);
        for (var i = 0; i < doc.PageCount; i++)
        {
            using var before = ContentImageTests.Render(doc, doc.Pages[i]);
            using var after = ContentImageTests.Render(written, written.Pages[i]);
            Assert.Equal((before.Width, before.Height), (after.Width, after.Height));
            var different = 0;
            for (var y = 0; y < before.Height; y++)
                for (var x = 0; x < before.Width; x++)
                    if (before.GetPixel(x, y) != after.GetPixel(x, y)) different++;
            Assert.True(different == 0, $"{name} ({c.Note}), Seite {i + 1}: {different} Bildpunkte anders");
        }
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    [Fact]
    public void EveryRule_IsInTheTable()
    {
        var table = File.ReadAllText(Path.Combine(Root(), "docs", "PDFA-REGELN.md"));
        var missing = Catalogue.Where(c => !table.Contains($"`{c.Name}`", StringComparison.Ordinal)).Select(c => c.Name).ToList();
        Assert.True(missing.Count == 0, "In docs/PDFA-REGELN.md fehlen: " + string.Join(", ", missing));
    }
}
