using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>"Redact first": applying redactions in the open document (<see cref="InDocumentRedaction"/>) and the matching history.</summary>
public sealed class InDocumentRedactionTests
{
    private const string Line = "Max Mustermann, Musterstrasse 12, 12345 Musterstadt";

    private static string TextOf(PdfDocument doc, int page, bool withoutOwnElements = false) =>
        string.Concat(PageInterpreter.Build(doc, doc.Pages[page], withoutOwnElements: withoutOwnElements).Text.Select(t => t.Text));

    /// <summary>Creates a redaction over a word on the first page.</summary>
    private static RedactionElement Mark(PdfDocument doc, string word)
    {
        var text = PageText.Create(PageInterpreter.Build(doc, doc.Pages[0]).Text);
        var areas = text.Quads(Assert.Single(text.Find(word))).Select(q => q.Bounds)
            .Select(b => new PdfRectangle(b.Left - 0.5, b.Top - 0.5, b.Right + 0.5, b.Bottom + 0.5)).ToList();
        return RedactionElement.FromPageAreas(areas);
    }

    /// <summary>
    /// Encrypted file opened with the user password (without the copy right): the redacted state is encrypted the same way and
    /// grants the same rights; otherwise content that was locked before could be copied after redacting.
    /// </summary>
    [Fact]
    public void Encrypted_KeepsProtectionAndRights()
    {
        using var plain = PdfDocument.Open(RedactionTests.TextPage(Line, "Zweite Zeile bleibt"));
        using var output = new MemoryStream();
        var rights = PdfPermissions.Print | PdfPermissions.Modify;
        PdfWriter.Write(plain, output, new PdfSaveOptions { Protection = new PdfProtection("geheim", "Besitzer-1", rights) });
        using var original = PdfDocument.Open(output.ToArray(), "geheim");
        Assert.False(original.Security!.IsOwner);
        var item = PageItem.From(original)[0];
        item = item.WithElements([.. item.Elements, Mark(original, "Musterstrasse")]);

        var (doc, pages) = InDocumentRedaction.Apply(original, [item], new PdfSaveOptions(), "geheim");
        using (doc)
        {
            Assert.NotNull(doc.Security);
            Assert.False(doc.Security!.IsOwner);
            Assert.Equal(original.Security.Permissions, doc.Security.Permissions);
            var text = TextOf(doc, 0);
            Assert.DoesNotContain("Musterstrasse", text);
            Assert.Contains("Max Mustermann,", text);
            Assert.Contains("Zweite Zeile bleibt", text);
            Assert.Empty(Assert.Single(pages).Elements);
        }
    }

    /// <summary>
    /// Own elements are not burnt in (otherwise they would become fixed) but are attached to their page again afterwards, except the one
    /// under the redaction. A page without a redaction keeps its identifier (its tiles stay valid); the redacted page gets a new one.
    /// </summary>
    [Fact]
    public void OwnElements_StayEditable_ExceptUnderTheRedaction()
    {
        using var original = PdfDocument.Open(RedactionTests.TextPage(Line, "Zweite Zeile bleibt"));
        using var other = PdfDocument.Open(RedactionTests.TextPage("Andere Seite"));
        var mark = Mark(original, "Musterstrasse");
        var kept = new TextElement { Rect = new PdfRectangle(72, 300, 220, 330), Text = "Notiz bleibt" };
        var under = new TextElement { Rect = mark.Rect, Text = "Liegt darunter" };
        var first = PageItem.From(original)[0];
        first = first.WithElements([.. first.Elements, kept, under, mark]);
        var second = PageItem.From(other)[0];

        var (doc, pages) = InDocumentRedaction.Apply(original, [first, second], new PdfSaveOptions(), null);
        using (doc)
        {
            Assert.Equal(2, doc.PageCount);
            Assert.Same(kept, Assert.Single(pages[0].Elements));
            Assert.DoesNotContain("Notiz", TextOf(doc, 0));   // not burnt in
            Assert.DoesNotContain("Liegt darunter", TextOf(doc, 0));
            Assert.NotEqual(first.Id, pages[0].Id);
            Assert.Equal(second.Id, pages[1].Id);
            Assert.Same(doc, pages[1].Source);
            Assert.Contains("Andere Seite", TextOf(doc, 1));
        }
    }

    /// <summary>
    /// Measurement (not part of the normal test run; PDFKNEAD_MESSEN=schwaerzen, Release build): redaction in the document for large
    /// files, one area on page 1, twice each: time, allocated memory, size of the redacted state in memory.
    /// </summary>
    [Fact]
    public void Measure()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") != "schwaerzen") return;
        var root = Root();
        var files = new List<(string, byte[])>
        {
            ("Buch 352 S. (freeculture)", File.ReadAllBytes(Path.Combine(root, "TestPDFs", "pdfjs", "freeculture.pdf"))),
            ("Scan 60 S. 300 dpi grau", CompressTiming.Scans(60)),
        };
        foreach (var plan in TestPdfs.Private().Take(1))
            files.Add(("A0-Plan", File.ReadAllBytes(plan)));
        foreach (var (name, data) in files)
        {
            using var original = PdfDocument.Open(data);
            var items = PageItem.From(original);
            items[0] = items[0].WithElements([.. items[0].Elements, new RedactionElement { Rect = new PdfRectangle(50, 50, 300, 300) }]);
            for (var run = 1; run <= 2; run++)
            {
                GC.Collect();
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                var before = process.PrivateMemorySize64;
                var allocated = GC.GetTotalAllocatedBytes(true);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var (doc, _) = InDocumentRedaction.Apply(original, items, new PdfSaveOptions { ImageCodec = Rendering.SkiaImageCodec.Instance }, null);
                watch.Stop();
                using (doc)
                {
                    process.Refresh();
                    Console.WriteLine($"MESS {name} Lauf {run}: {watch.ElapsedMilliseconds} ms, belegt {(GC.GetTotalAllocatedBytes(true) - allocated) / 1e6:0} MB, " +
                                      $"Datei {data.Length / 1e6:0.0} MB, Prozess privat +{(process.PrivateMemorySize64 - before) / 1e6:0} MB");
                }
            }
        }
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }

    /// <summary>The history remembers the main document per state: undoing across the redaction brings back the old one.</summary>
    [Fact]
    public void History_ReplaceDocument_UndoRedoSwitchMain()
    {
        using var original = PdfDocument.Open(RedactionTests.TextPage(Line));
        using var redacted = PdfDocument.Open(RedactionTests.TextPage("Max Mustermann"));
        var history = new PageHistory(PageItem.From(original), original);
        history.Rotate([0], 90);
        history.ReplaceDocument("Schwärzungen anwenden", redacted, PageItem.From(redacted));
        Assert.Same(redacted, history.Main);
        Assert.Contains(original, history.Sources());
        Assert.True(history.UsesMain(original));

        history.Undo();
        Assert.Same(original, history.Main);
        Assert.Same(original, history.Pages[0].Source);
        Assert.Equal(90, history.Pages[0].Page.Rotate);
        history.Redo();
        Assert.Same(redacted, history.Main);

        // Undo, then a new change: the redacted state drops out of the history
        history.Undo();
        history.Rotate([0], 90);
        Assert.False(history.UsesMain(redacted));
        Assert.DoesNotContain(redacted, history.Sources());
    }
}
