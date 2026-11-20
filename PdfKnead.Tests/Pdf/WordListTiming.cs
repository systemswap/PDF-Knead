using System.Diagnostics;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Text;
using Xunit.Abstractions;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Measurement (not part of the normal test run): „Suchen und schwärzen“ with a word list (KONZEPT.md, section 5.6) — 200 and 20 000 terms
/// (the most a list may have) over a book of the public test set, once with terms of varied beginnings and once with names that share their
/// first three letters (the worst case of the search, which groups the terms by them). Enable with PDFKNEAD_MESSEN=wortliste, use a
/// release build, and show the output with --logger "console;verbosity=detailed" (lines starting with "MESS").
/// </summary>
public sealed class WordListTiming(ITestOutputHelper output)
{
    [Fact]
    public void ManyTermsOverABook()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") != "wortliste") return;
        using var doc = PdfDocument.Open(Path.Combine(Root(), "TestPDFs", "pdfjs", "freeculture.pdf"));
        var texts = doc.Pages.Select(p => PageText.Create(PageInterpreter.BuildText(doc, p))).ToList();
        var characters = texts.Sum(t => t.Text.Length);
        var random = new Random(7);
        string[] syllables = ["ma", "mu", "ster", "mann", "er", "ka", "jür", "gen", "mül", "ler", "schmidt", "ner", "bach", "berg", "hof", "lin", "ko", "wal"];
        string Word(int parts) => string.Concat(Enumerable.Range(0, parts).Select(_ => syllables[random.Next(syllables.Length)]));
        List<string> Varied(int count) => [.. Enumerable.Range(0, count).Select(i => PageText.NormalizeQuery(Word(2 + i % 3) + " " + Word(2)))];
        List<string> SamePrefix(int count) => [.. Enumerable.Range(0, count).Select(i => PageText.NormalizeQuery("Mus" + Word(2) + i))];
        foreach (var (name, terms) in new[] { ("200 Begriffe", Varied(200)), ("20 000 Begriffe", Varied(20_000)), ("20 000 mit gleichem Anfang", SamePrefix(20_000)) })
        {
            var watch = Stopwatch.StartNew();
            var hits = 0;
            foreach (var text in texts) hits += text.FindAll(terms).Sum(h => h.Count);
            output.WriteLine($"MESS {name}: {texts.Count} Seiten, {characters} Zeichen, {watch.ElapsedMilliseconds} ms, {hits} Treffer");
        }
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
}
