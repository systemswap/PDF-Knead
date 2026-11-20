using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Leftovers after redaction, cleanup and deletion, checked with the leak search
/// (<c>pdf-knead lecksuche</c>) on files that contain exactly that case.
/// </summary>
public sealed class CrossRoundTests
{
    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    private static List<string> Leaks(byte[] data, params string[] words) => PdfKnead.Cli.LeakSearch.Find(data, words, null, out _);

    private static byte[] Write(PdfDocument doc, IReadOnlyList<PageItem> pages, PdfSaveOptions options)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, pages, output, options);
        return output.ToArray();
    }

    /// <summary>Redacts the whole page.</summary>
    private static PageItem Blackened(PageItem item) =>
        item.WithElements([.. item.Elements, new RedactionElement { Rect = new PdfRectangle(0, 0, 595, 842) }]);

    /// <summary>Text in the group of a soft mask (graphics state /SMask /G) lies under the redaction: it must be removed as well.</summary>
    [Fact]
    public void SoftMaskGroup_UnderRedaction_Removed()
    {
        var pdf = TestPdf.Simple(1);
        var group = pdf.Add(TestPdf.FlateStream("BT /F1 30 Tf 1 g 72 600 Td (MASKENGEHEIM) Tj ET"u8.ToArray(), TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 595, 842)),
            ("Group", TestPdf.Dict(("S", TestPdf.N("Transparency")), ("CS", TestPdf.N("DeviceGray")))),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))))))));
        pdf.Replace(4, TestPdf.FlateStream("q /GS1 gs 0 0 1 rg 0 0 595 842 re f Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("ExtGState", TestPdf.Dict(("GS1", TestPdf.Dict(
            ("SMask", TestPdf.Dict(("S", TestPdf.N("Luminosity")), ("G", TestPdf.Ref(group))))))))));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.NotEmpty(Leaks(pdf.Build(), "MASKENGEHEIM"));   // check: the search finds it beforehand
        var data = Write(doc, [Blackened(PageItem.From(doc)[0])], new PdfSaveOptions { ApplyRedactions = true });
        Assert.Empty(Leaks(data, "MASKENGEHEIM"));
    }

    /// <summary>Cross-check: if the redaction lies elsewhere, the mask stays as it is (same graphics state, text inside).</summary>
    [Fact]
    public void SoftMaskGroup_OutsideRedaction_Stays()
    {
        var pdf = TestPdf.Simple(1);
        var group = pdf.Add(TestPdf.FlateStream("BT /F1 30 Tf 1 g 72 600 Td (MASKENTEXT) Tj ET"u8.ToArray(), TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 595, 842)),
            ("Group", TestPdf.Dict(("S", TestPdf.N("Transparency")), ("CS", TestPdf.N("DeviceGray")))),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))))))));
        pdf.Replace(4, TestPdf.FlateStream("q /GS1 gs 0 0 1 rg 0 0 595 842 re f Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("ExtGState", TestPdf.Dict(("GS1", TestPdf.Dict(
            ("SMask", TestPdf.Dict(("S", TestPdf.N("Luminosity")), ("G", TestPdf.Ref(group))))))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0].WithElements([new RedactionElement { Rect = new PdfRectangle(60, 50, 200, 100) }]);
        var data = Write(doc, [item], new PdfSaveOptions { ApplyRedactions = true, CompressStreams = false });
        Assert.NotEmpty(Leaks(data, "MASKENTEXT"));
        Assert.Contains("/GS1 gs", System.Text.Encoding.Latin1.GetString(data));
    }



    /// <summary>A form XObject with private data of another program (/PieceInfo) is redacted: the copy must not keep that data.</summary>
    [Fact]
    public void RedactedForm_KeepsNoPieceInfo()
    {
        var pdf = TestPdf.Simple(1);
        var form = pdf.Add(TestPdf.FlateStream("BT /F1 20 Tf 0 0 Td (Sichtbar) Tj ET 0 0 200 50 re S"u8.ToArray(), TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 300, 60)),
            ("PieceInfo", TestPdf.Dict(("Zeichner", TestPdf.Dict(("Private", S("FORMPRIVAT")))))),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))))))));
        pdf.Replace(4, TestPdf.FlateStream("q 1 0 0 1 72 600 cm /Fm0 Do Q"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))),
            ("XObject", TestPdf.Dict(("Fm0", TestPdf.Ref(form))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        item = item.WithElements([new RedactionElement { Rect = new PdfRectangle(60, 590, 120, 640) }]);
        var data = Write(doc, [item], new PdfSaveOptions { ApplyRedactions = true, Cleanup = CleanupItems.PrivateData });
        Assert.Empty(Leaks(data, "FORMPRIVAT"));
    }

    /// <summary>Page 2 deleted: its structure element (alternative text) must not come along via the structure tree.</summary>
    [Fact]
    public void DeletedPage_StructureTextGone()
    {
        var pdf = TestPdf.Simple(2);
        var catalog = (PdfDictionary)pdf.Get(1);
        var root = pdf.Add(new PdfDictionary());
        var element = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Figure")), ("P", TestPdf.Ref(root)),
            ("Pg", TestPdf.Ref(7)), ("Alt", S("STRUKTURGEHEIM")), ("K", PdfInteger.Of(0))));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", new PdfArray([TestPdf.Ref(element)]))));
        catalog.Set(PdfName.Get("StructTreeRoot"), TestPdf.Ref(root));
        using var doc = PdfDocument.Open(pdf.Build());
        var data = Write(doc, [PageItem.From(doc)[0]], new PdfSaveOptions());
        Assert.Empty(Leaks(data, "STRUKTURGEHEIM"));
    }

    /// <summary>The calculation order (/CO) of a form names a field of the deleted page: its value must not come along.</summary>
    [Fact]
    public void DeletedPage_CalculatedFieldGone()
    {
        var pdf = TestPdf.Simple(2);
        var field = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Tx")), ("T", S("Summe")),
            ("V", S("FELDGEHEIM")), ("Rect", TestPdf.Box(100, 100, 200, 120)), ("P", TestPdf.Ref(7))));
        ((PdfDictionary)pdf.Get(7)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(field)]));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(field)])),
            ("CO", new PdfArray([TestPdf.Ref(field)]))));
        using var doc = PdfDocument.Open(pdf.Build());
        var data = Write(doc, [PageItem.From(doc)[0]], new PdfSaveOptions());
        Assert.Empty(Leaks(data, "FELDGEHEIM"));
    }

    /// <summary>A signature with a provenance record is redacted: name, line and identifier must not remain in the record.</summary>
    [Fact]
    public void RedactedSignature_ProvenanceGone()
    {
        var key = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-schluessel-").FullName, "geraet.key");
        using var device = Core.Platform.DeviceIdentity.LoadOrCreate(key);
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var signature = new SignatureElement { Rect = new PdfRectangle(100, 600, 300, 660), Name = "Unterschriftgeheim", FontFile = "PatrickHand-Regular.ttf" };
        signature = signature with { Seal = SignatureSeal.Create(signature, $"Unterschriftgeheim · 16.10.2026 12:00 · Kennung {device.Code}", device) };
        using var signed = PdfDocument.Open(Write(original, [PageItem.From(original)[0].WithElements([signature])], new PdfSaveOptions()));
        var item = PageItem.From(signed)[0];
        item = item.WithElements([.. item.Elements, new RedactionElement { Rect = new PdfRectangle(80, 560, 320, 680) }]);
        var data = Write(signed, [item], new PdfSaveOptions { ApplyRedactions = true });
        Assert.Empty(Leaks(data, "Unterschriftgeheim"));
    }

    /// <summary>Cross-check: a redaction elsewhere on the page leaves the signature, line and record in place.</summary>
    [Fact]
    public void RedactedElsewhere_ProvenanceStays()
    {
        var key = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-schluessel-").FullName, "geraet.key");
        using var device = Core.Platform.DeviceIdentity.LoadOrCreate(key);
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var signature = new SignatureElement { Rect = new PdfRectangle(100, 600, 300, 660), Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" };
        signature = signature with { Seal = SignatureSeal.Create(signature, $"Max Mustermann · 16.10.2026 12:00 · Kennung {device.Code}", device) };
        using var signed = PdfDocument.Open(Write(original, [PageItem.From(original)[0].WithElements([signature])], new PdfSaveOptions()));
        var item = PageItem.From(signed)[0];
        item = item.WithElements([.. item.Elements, new RedactionElement { Rect = new PdfRectangle(60, 740, 300, 800) }]);   // "Seite 1" at the top
        using var result = PdfDocument.Open(Write(signed, [item], new PdfSaveOptions { ApplyRedactions = true }));
        Assert.Single(SealRecord.Read(result));
    }

    /// <summary>Page labels (i, ii, 1 …) survive the redaction of a page.</summary>
    [Fact]
    public void PageLabels_SurviveRedaction()
    {
        var pdf = TestPdf.Simple(2);
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("PageLabels"), TestPdf.Dict(("Nums", new PdfArray([PdfInteger.Of(0), TestPdf.Dict(("S", TestPdf.N("r")))]))));
        using var doc = PdfDocument.Open(pdf.Build());
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new RedactionElement { Rect = new PdfRectangle(60, 750, 200, 800) }]);
        using var copy = PdfDocument.Open(Write(doc, items, new PdfSaveOptions { ApplyRedactions = true }));
        Assert.NotNull(copy.Catalog!.Get(PdfName.Get("PageLabels")).AsDictionary());
    }

    /// <summary>Redacted copy: the file identifier /ID must not link it to the original.</summary>
    [Fact]
    public void RedactedCopy_GetsNewFileId()
    {
        // With an identifier: written once (the writer assigns one)
        using var plain = PdfDocument.Open(RedactionTests.TextPage("Max Mustermann"));
        using var doc = PdfDocument.Open(Write(plain, PageItem.From(plain), new PdfSaveOptions()));
        var data = Write(doc, [Blackened(PageItem.From(doc)[0])], new PdfSaveOptions { ApplyRedactions = true });
        using var copy = PdfDocument.Open(data);
        static string? First(PdfDocument d) => d.Trailer.Get(PdfName.Get("ID")) is PdfArray { Count: >= 1 } ids && ids[0] is PdfString s ? Convert.ToHexString(s.Bytes) : null;
        Assert.NotNull(First(copy));
        Assert.NotNull(First(doc));
        Assert.NotEqual(First(doc), First(copy));
        // Without redaction it stays (the same file, written again)
        using (var same = PdfDocument.Open(Write(doc, PageItem.From(doc), new PdfSaveOptions())))
            Assert.Equal(First(doc), First(same));
    }




    /// <summary>
    /// Broken TrueType fonts during subsetting (compression): truncated → rejected cleanly; glyph offsets outside the table → these
    /// glyphs stay empty and the rest is subset (otherwise ArgumentOutOfRange; fuzzing hit this even with an unmodified test file).
    /// </summary>
    [Fact]
    public void TrueTypeSubset_BrokenFonts_RejectedCleanly()
    {
        var ttf = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        Assert.Throws<Core.Fonts.FontFormatException>(() => Core.Fonts.TrueTypeSubsetter.Subset(ttf[..30], [1, 2]));
        // Find loca and make the first offsets nonsensical (long or short, depending on head)
        int tables = (ttf[4] << 8) | ttf[5], locaOffset = -1;
        for (var i = 0; i < tables; i++)
            if (System.Text.Encoding.ASCII.GetString(ttf, 12 + i * 16, 4) == "loca")
                locaOffset = (ttf[12 + i * 16 + 8] << 24) | (ttf[12 + i * 16 + 9] << 16) | (ttf[12 + i * 16 + 10] << 8) | ttf[12 + i * 16 + 11];
        Assert.True(locaOffset > 0);
        var broken = (byte[])ttf.Clone();
        for (var k = 4; k < 16; k++) broken[locaOffset + k] = 0xFF;
        var subset = Core.Fonts.TrueTypeSubsetter.Subset(broken, Enumerable.Range(0, 60));
        Assert.True(subset.Length > 0);
    }

    /// <summary>Splitting by bookmarks: device names become ordinary names, long names are not cut in the middle of a character.</summary>
    [Fact]
    public void SplitFileNames_DeviceNamesAndLongNames()
    {
        Assert.Equal("_CON", ViewModels.SplitViewModel.FileNameOf("CON", 1));
        Assert.Equal("_nul.txt", ViewModels.SplitViewModel.FileNameOf("nul.txt", 1));
        Assert.Equal("_COM1", ViewModels.SplitViewModel.FileNameOf("COM1", 1));
        Assert.Equal("Console", ViewModels.SplitViewModel.FileNameOf("Console", 1));
        Assert.Equal("_.._Ausbruch_x", ViewModels.SplitViewModel.FileNameOf(@"..\..\Ausbruch:x", 1));
        var name = ViewModels.SplitViewModel.FileNameOf(new string('a', 119) + "😀" + "b", 1);
        Assert.Equal(119, name.Length);
        Assert.False(char.IsHighSurrogate(name[^1]));
    }

    /// <summary>File errors in plain words: disk full, file in use, not found, and without the name of the temporary file.</summary>
    [Fact]
    public void FileErrors_Readable_WithoutTemporaryName()
    {
        Assert.Equal(Tr.T("Auf dem Datenträger ist nicht genug Platz."),
            Services.Failure.Text(new IOException("There is not enough space on the disk.", unchecked((int)0x80070070))));
        Assert.StartsWith(Tr.T("Die Datei wird gerade von einem anderen Programm benutzt (etwa OneDrive oder ein PDF-Leser) — später noch einmal versuchen.")[..20],
            Services.Failure.Text(new IOException("The process cannot access the file", unchecked((int)0x80070020))));
        Assert.Equal(Tr.T("Die Datei {0} wurde nicht gefunden.", "Vertrag.pdf"),
            // path of the running system: under Linux "\" is not a separator
            Services.Failure.Text(new FileNotFoundException("Could not find file", Path.Combine(Path.GetTempPath(), "Ablage", "Vertrag.pdf.1a2b3c4d.pdfknead-tmp"))));
        Assert.Equal(@"Fehler bei C:\Ablage\Vertrag.pdf", Services.Failure.Text(new IOException(@"Fehler bei C:\Ablage\Vertrag.pdf.1a2b3c4d.pdfknead-tmp")));
    }

    /// <summary>Cleanup "scripts and actions": links to smb: and javascript: lose their action like file:, web and mail links stay.</summary>
    [Fact]
    public void Cleanup_NonWebLinks_Removed_WebLinksStay()
    {
        var pdf = TestPdf.Simple(1);
        var annots = new PdfArray();
        foreach (var (uri, x) in new[] { ("smb://server/freigabe", 10), ("javascript:alert(1)", 40), ("https://example.org", 70), ("mailto:max@example.org", 100) })
            annots.Add(TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Link")), ("Rect", TestPdf.Box(x, 10, x + 20, 30)),
                ("A", TestPdf.Dict(("S", TestPdf.N("URI")), ("URI", new PdfString(System.Text.Encoding.ASCII.GetBytes(uri)))))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), annots);
        using var doc = PdfDocument.Open(pdf.Build());
        using var clean = PdfDocument.Open(Write(doc, PageItem.From(doc), new PdfSaveOptions { Cleanup = CleanupItems.Scripts }));
        var links = clean.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        var targets = Enumerable.Range(0, links.Count)
            .Select(i => links.Get(i).AsDictionary()?.GetDictionary(PdfName.Get("A"))?.GetString(PdfName.Get("URI"))?.ToText()).ToList();
        Assert.Equal([null, null, "https://example.org", "mailto:max@example.org"], targets);
    }
}

/// <summary>
/// The list of leftovers is a static setting of <see cref="PdfFileSaver"/>: while a test points it to a file of its own, a save in a test running
/// at the same time recorded its old version there too (the list existed again after the cleanup). Not run in parallel with other tests.
/// </summary>
[Collection(nameof(SaverStateCollection))]
public sealed class LeftoverListTests
{
    /// <summary>
    /// The leftovers of earlier runs are deleted while this instance saves (in the tests: another test runs the clean-up at the same time):
    /// the temporary file of the save in progress stays, and the save completes (docs/FEHLERGEDAECHTNIS.md, No. 265).
    /// </summary>
    [Fact]
    public void Leftovers_OfEarlierRuns_DeletedDuringASave_TheSaveCompletes()
    {
        var dir = Directory.CreateTempSubdirectory("pdf-knead-reste-").FullName;
        var crashed = Path.Combine(dir, "Angebot.pdf.4567cdef.pdfknead-alt");
        File.WriteAllText(crashed, "x");
        var list = Path.Combine(dir, "aufraeumen.txt");
        File.WriteAllLines(list, [$"999999999:1\t{crashed}"]);
        var target = Path.Combine(dir, "neu.pdf");
        var before = PdfFileSaver.LeftoverList;
        try
        {
            PdfFileSaver.LeftoverList = list;
            using var doc = PdfDocument.Open(TestFiles.MinimalPdf());
            PdfFileSaver.Save(target, output =>
            {
                PdfFileSaver.DeleteLeftoversOfEarlierRuns();
                return PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions());
            });
            Assert.False(File.Exists(crashed));
            Assert.True(new FileInfo(target).Length > 0);
        }
        finally
        {
            PdfFileSaver.LeftoverList = before;
        }
    }

    /// <summary>
    /// A second instance starts while the first is saving: before, it deleted the first one's temporary file from the shared
    /// list — under Linux that works on an open file, and the save failed at the end. Now entries of an instance that still
    /// runs stay (on disk and in the list, also when this instance writes the list itself); those of an instance that has
    /// ended, or whose process number now belongs to another program, are deleted.
    /// </summary>
    [Fact]
    public void Leftovers_OfAnotherRunningInstance_Kept()
    {
        var dir = Directory.CreateTempSubdirectory("pdf-knead-reste-").FullName;
        var saving = Path.Combine(dir, "Vertrag.pdf.0123abcd.pdfknead-tmp");
        var crashed = Path.Combine(dir, "Angebot.pdf.4567cdef.pdfknead-alt");
        var reused = Path.Combine(dir, "Rechnung.pdf.89abcdef.pdfknead-alt");
        foreach (var file in new[] { saving, crashed, reused }) File.WriteAllText(file, "x");
        using var other = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "ping" : "sleep",
            OperatingSystem.IsWindows() ? "-n 120 127.0.0.1" : "120") { CreateNoWindow = true, RedirectStandardOutput = true })!;
        var list = Path.Combine(dir, "aufraeumen.txt");
        var running = $"{other.Id}:{other.StartTime.ToUniversalTime().Ticks}";
        File.WriteAllLines(list, [$"{running}\t{saving}", $"999999999:1\t{crashed}", $"{other.Id}:1\t{reused}"]);
        var before = PdfFileSaver.LeftoverList;
        try
        {
            PdfFileSaver.LeftoverList = list;
            PdfFileSaver.DeleteLeftoversOfEarlierRuns();
            Assert.True(File.Exists(saving));
            Assert.False(File.Exists(crashed));
            Assert.False(File.Exists(reused));

            // This instance saves (its own temporary file goes into the list and out again): the other one's line stays.
            // (the class runs alone: no other test saves meanwhile)
            using (var doc = PdfDocument.Open(TestFiles.MinimalPdf()))
                PdfFileSaver.Save(doc, PageItem.From(doc), Path.Combine(dir, "neu.pdf"));
            var lines = File.ReadAllLines(list);
            Assert.Contains($"{running}\t{saving}", lines);
            Assert.DoesNotContain(lines, l => l.Contains("pdfknead-alt", StringComparison.Ordinal) || l.Contains("neu.pdf", StringComparison.Ordinal));
        }
        finally
        {
            PdfFileSaver.LeftoverList = before;
            other.Kill();
        }
    }

    /// <summary>
    /// Leftovers of earlier runs: a left-behind old version (such as the unredacted original) is deleted at startup, but a foreign
    /// file in the list is not (only the own name pattern counts).
    /// </summary>
    [Fact]
    public void Leftovers_OfEarlierRuns_Deleted_OthersKept()
    {
        var dir = Directory.CreateTempSubdirectory("pdf-knead-reste-").FullName;
        var leftover = Path.Combine(dir, "Vertrag.pdf.0123abcd.pdfknead-alt");
        var foreign = Path.Combine(dir, "wichtig.pdf");
        File.WriteAllText(leftover, "ungeschwärzt");
        File.WriteAllText(foreign, "bleibt");
        var list = Path.Combine(dir, "aufraeumen.txt");
        File.WriteAllLines(list, [leftover, foreign, "relativ.pdf.0123abcd.pdfknead-alt"]);
        var before = PdfFileSaver.LeftoverList;
        try
        {
            PdfFileSaver.LeftoverList = list;
            PdfFileSaver.DeleteLeftoversOfEarlierRuns();
            Assert.False(File.Exists(leftover));
            Assert.True(File.Exists(foreign));
            Assert.False(File.Exists(list));   // nothing left pending
        }
        finally
        {
            PdfFileSaver.LeftoverList = before;
        }
    }
}

[CollectionDefinition(nameof(SaverStateCollection), DisableParallelization = true)]
public sealed class SaverStateCollection;
