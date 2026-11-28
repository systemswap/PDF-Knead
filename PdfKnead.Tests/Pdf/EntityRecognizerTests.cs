using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Pdf.Text.Entities;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The recognition of persons, companies and addresses without AI (KONZEPT.md, section 5.23): the lexicon file, the rules on an invented
/// letter, the building rules of the patent check (docs/PATENTE.md, B1–B8), foreign input, and lower bounds on the own test sentences
/// (<c>tools/ner-messung/pruefsaetze.txt</c>) so no change makes the recognition worse unnoticed. Only invented names.
/// </summary>
public sealed class EntityRecognizerTests
{
    private static List<(string Text, EntityKind Kind, EntityDoubt Doubt)> Find(string text, EntityOptions? options = null) =>
        [.. new EntityRecognizer(Lexicon.Default!, options).Find(text).Select(h => (text.Substring(h.Start, h.Length), h.Kind, h.Doubt))];

    private static string RepoRoot([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    [Fact]
    public void Lexicon_BuiltIn_ListsAskedOneByOne()
    {
        var lexicon = Lexicon.Default;
        Assert.NotNull(lexicon);
        Assert.True(lexicon.Count(LexiconList.GivenNames) > 100_000);
        Assert.True(lexicon.Count(LexiconList.FamilyNames) > 500_000);
        Assert.True(lexicon.Contains(LexiconList.GivenNames, "Erika"));
        Assert.False(lexicon.Contains(LexiconList.GivenNames, "erika"));   // as written
        Assert.True(lexicon.Contains(LexiconList.BigPlaces, "Berlin"));
        Assert.True(lexicon.Contains(LexiconList.Countries, "Deutschland"));
        Assert.True(lexicon.Contains(LexiconList.CommonLower, "und"));
        // B1: the common words are single words only
        Assert.DoesNotContain(lexicon.Entries(LexiconList.CommonLower).Take(50_000), w => w.Contains(' '));
        // the sources are named
        Assert.Contains(lexicon.Entries(LexiconList.Sources), s => s.Contains("GeoNames") && s.Contains("CC BY 4.0"));
    }

    [Fact]
    public void Lexicon_WrittenAndRead_SortedOnceEach_DamageRefused()
    {
        var names = Enumerable.Range(0, 100).Select(i => $"Muster{i:000}").Concat(["Max", "Erika", "Max", "", "Ärger"]).ToList();
        var data = Lexicon.Write(new Dictionary<LexiconList, IEnumerable<string>> { [LexiconList.GivenNames] = names, [LexiconList.Titles] = ["Dr."] });
        var lexicon = Lexicon.Read(data);
        Assert.Equal(103, lexicon.Count(LexiconList.GivenNames));
        Assert.All(names.Where(n => n.Length > 0), n => Assert.True(lexicon.Contains(LexiconList.GivenNames, n)));
        Assert.False(lexicon.Contains(LexiconList.GivenNames, "Maxi"));
        Assert.False(lexicon.Contains(LexiconList.GivenNames, "Muster100"));
        Assert.False(lexicon.Contains(LexiconList.FamilyNames, "Max"));   // a list it does not have
        var entries = lexicon.Entries(LexiconList.GivenNames);
        Assert.Equal(entries.OrderBy(e => Encoding.UTF8.GetBytes(e), Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))), entries);
        Assert.Equal(["Dr."], lexicon.Entries(LexiconList.Titles));
        // packed as built into the program
        Assert.Equal(data, Unpack(Lexicon.Pack(data)));
        // truncated, another version, every single byte changed: refused or still consistent — never a read past the data
        Assert.Throws<InvalidDataException>(() => Lexicon.Read(data[..(data.Length - 3)]));
        Assert.Throws<InvalidDataException>(() => Lexicon.Read([.. "PKLX"u8, 9, 0]));
        for (var i = 4; i < data.Length; i++)
        {
            var bad = (byte[])data.Clone();
            bad[i] ^= 0x5A;
            try
            {
                var damaged = Lexicon.Read(bad);
                foreach (var list in damaged.Lists)
                    foreach (var e in damaged.Entries(list)) _ = damaged.Contains(list, e);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    private static byte[] Unpack(byte[] packed)
    {
        using var brotli = new System.IO.Compression.BrotliStream(new MemoryStream(packed), System.IO.Compression.CompressionMode.Decompress);
        using var memory = new MemoryStream();
        brotli.CopyTo(memory);
        return memory.ToArray();
    }

    [Fact]
    public void Letter_EveryPartOfTheAddressAHitOfItsOwn()
    {
        var hits = Find("Herrn\nMax Mustermann\nMusterstraße 12\n12345 Musterstadt\nDeutschland\n\nSehr geehrter Herr Mustermann,");
        Assert.Contains(("Herrn\nMax Mustermann", EntityKind.Person, EntityDoubt.None), hits);
        Assert.Contains(("Musterstraße", EntityKind.Street, EntityDoubt.None), hits);
        Assert.Contains(("12", EntityKind.HouseNumber, EntityDoubt.None), hits);
        Assert.Contains(("12345", EntityKind.PostalCode, EntityDoubt.None), hits);
        Assert.Contains(("Musterstadt", EntityKind.Place, EntityDoubt.None), hits);
        Assert.Contains(("Deutschland", EntityKind.Country, EntityDoubt.None), hits);
        Assert.Contains(("Herr Mustermann", EntityKind.Person, EntityDoubt.None), hits);
    }

    [Fact]
    public void Persons_SureAndSuggested()
    {
        var hits = Find("Kontakt: Erika Musterfrau. Gestern hat Thomas angerufen. Der Müller kam auch, mit dem Bauer.");
        Assert.Contains(("Erika Musterfrau", EntityKind.Person, EntityDoubt.None), hits);
        Assert.Contains(("Thomas", EntityKind.Person, EntityDoubt.SingleName), hits);   // only a given name: offered unticked
        Assert.DoesNotContain(hits, h => h.Text.Contains("Müller") || h.Text.Contains("Bauer"));   // after an article
        // a name found with certainty is found again, also the family name alone
        var again = Find("Max Mustermann hat angerufen. Später schrieb Mustermann zurück.");
        Assert.Equal(2, again.Count(h => h.Kind == EntityKind.Person && h.Doubt == EntityDoubt.None));
    }

    [Fact]
    public void Companies_PersonInsideBelongsToTheCompany()
    {
        // B2: what is found as a whole is not looked at again word by word
        var hits = Find("Die Mustermann-Milch GmbH liefert, die Muster & Partner KG zahlt.");
        Assert.Contains(("Mustermann-Milch GmbH", EntityKind.Organisation, EntityDoubt.None), hits);
        Assert.Contains(("Muster & Partner KG", EntityKind.Organisation, EntityDoubt.None), hits);
        Assert.DoesNotContain(hits, h => h.Kind == EntityKind.Person);
    }

    [Fact]
    public void HouseNumber_LetterOnlyAsAWordOfItsOwn()
    {
        var hits = Find("Kontakt: Erika Musterfrau, 14 Bradley Road. Musterweg 7a und Beispielallee 3 b.");
        Assert.Contains(("14", EntityKind.HouseNumber, EntityDoubt.None), hits);   // not „14 B“
        Assert.Contains(("Bradley Road", EntityKind.Street, EntityDoubt.None), hits);
        Assert.Contains(("7a", EntityKind.HouseNumber, EntityDoubt.None), hits);
        Assert.Contains(("3 b", EntityKind.HouseNumber, EntityDoubt.None), hits);
    }

    [Fact]
    public void TitleThatIsAWord_NeverSureAlone()
    {
        // B5: titles that are also common words stay in the list and are judged where they stand
        var hits = Find("Saint Lucia ist eine Insel.");
        Assert.DoesNotContain(hits, h => h.Kind == EntityKind.Person && h.Doubt == EntityDoubt.None);
    }

    [Fact]
    public void Thorough_UnknownNamesOnlyWhenAsked()
    {
        const string text = "Wir trafen Dixonchester gestern am Bahnhof.";
        Assert.DoesNotContain(Find(text), h => h.Text == "Dixonchester");
        Assert.Contains(("Dixonchester", EntityKind.Person, EntityDoubt.Unknown), Find(text, new EntityOptions(Thorough: true)));
    }

    [Fact]
    public void DecomposedText_PositionsStayExact()
    {
        var text = "Herr Ju\u0308rgen Mustermann wohnt in Musterstadt.";
        var hit = new EntityRecognizer(Lexicon.Default!).Find(text).First(h => h.Kind == EntityKind.Person);
        Assert.Equal("Herr Ju\u0308rgen Mustermann", text.Substring(hit.Start, hit.Length));
    }

    [Fact]
    public void ForeignInput_Finishes()
    {
        // page text is foreign input: long runs that make patterns try much, unpaired surrogates, control characters
        var recognizer = new EntityRecognizer(Lexicon.Default!, new EntityOptions(true, true));
        var texts = new[]
        {
            new string('A', 100_000),
            string.Concat(Enumerable.Repeat("Aa-Bb ", 20_000)),
            string.Concat(Enumerable.Repeat("1 ", 50_000)),
            string.Concat(Enumerable.Repeat("Herr ", 20_000)),
            string.Concat(Enumerable.Repeat("Muster GmbH ", 10_000)),
            "Herr \ud800 Max \u0000 Mustermann \udfff",
        };
        var watch = Stopwatch.StartNew();
        foreach (var text in texts) _ = recognizer.Find(text);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"{watch.Elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>
    /// The own test sentences (21 languages, invented names) as measured on 13.11.2026 — equal to the prototype after the building rules of the
    /// patent check: lower bounds for what is found, upper bounds for false hits.
    /// </summary>
    [Fact]
    public void OwnSentences_NotWorseThanMeasured()
    {
        var rows = EntityMeasurement.ReadSentences(File.ReadLines(Path.Combine(RepoRoot(), "tools", "ner-messung", "pruefsaetze.txt"), Encoding.UTF8));
        var scores = EntityMeasurement.Score(new EntityRecognizer(Lexicon.Default!), rows);
        void Bound(char type, int found, int falseHits)
        {
            var c = scores.Types[type];
            Assert.True(c.Found >= found, $"{type}: {c.Found} gefunden, mindestens {found} erwartet — verpasst: {string.Join("; ", scores.Misses.Where(m => m.Contains($" {type}:")))}");
            Assert.True(c.False <= falseHits, $"{type}: {c.False} Fehltreffer, höchstens {falseHits} erwartet — {string.Join("; ", scores.Falses.Where(m => m.Contains($" {type} ")))}");
        }
        Bound('P', 38, 4);
        Bound('O', 26, 0);
        Bound('A', 16, 0);
        Bound('Z', 15, 0);
        Bound('L', 18, 0);
        Assert.True(scores.Types['P'].PerWord >= 0.84);
    }
}
