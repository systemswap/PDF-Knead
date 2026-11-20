using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Finding hidden data and removing it on saving.</summary>
public sealed class CleanupTests
{
    private static PdfString S(string text) => new(System.Text.Encoding.ASCII.GetBytes(text));

    private static PdfStream Plain(string text, params (string, PdfObject)[] entries) =>
        new(TestPdf.Dict(entries), System.Text.Encoding.ASCII.GetBytes(text));

    /// <summary>Builds a file with everything the cleanup knows; page 1 has comments, links and an image with XMP.</summary>
    internal static byte[] Laden()
    {
        var pdf = TestPdf.Simple(2);
        var catalog = (PdfDictionary)pdf.Get(1);
        var page = (PdfDictionary)pdf.Get(5);
        pdf.InfoNumber = pdf.Add(TestPdf.Dict(("Author", S("Max Mustermann")), ("Creator", S("Textprogramm 7")), ("Title", S("Vertrag"))));
        var xmp = pdf.Add(Plain("<x:xmpmeta>MAX-MUSTERMANN-XMP</x:xmpmeta>", ("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))));
        catalog.Set(PdfName.Get("Metadata"), TestPdf.Ref(xmp));
        var file = pdf.Add(Plain("GEHEIMER-ANHANG-INHALT", ("Type", TestPdf.N("EmbeddedFile"))));
        var spec = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", S("anhang.txt")), ("EF", TestPdf.Dict(("F", TestPdf.Ref(file))))));
        var script = pdf.Add(TestPdf.Dict(("S", TestPdf.N("JavaScript")), ("JS", S("app.alert('GEHEIMES-SKRIPT')"))));
        catalog.Set(PdfName.Get("Names"), TestPdf.Dict(
            ("EmbeddedFiles", TestPdf.Dict(("Names", new PdfArray([S("anhang.txt"), TestPdf.Ref(spec)])))),
            ("JavaScript", TestPdf.Dict(("Names", new PdfArray([S("start"), TestPdf.Ref(script)]))))));
        catalog.Set(PdfName.Get("OpenAction"), TestPdf.Ref(script));
        catalog.Set(PdfName.Get("PieceInfo"), TestPdf.Dict(("Illustrator", TestPdf.Dict(("Private", S("GEHEIME-ZEICHNUNG"))))));
        var structRoot = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot"))));
        catalog.Set(PdfName.Get("StructTreeRoot"), TestPdf.Ref(structRoot));
        var outlineRoot = pdf.Add(new PdfDictionary());
        var outline = pdf.Add(TestPdf.Dict(("Title", S("Kapitel Mustermann")), ("Parent", TestPdf.Ref(outlineRoot)), ("Dest", new PdfArray([TestPdf.Ref(5), TestPdf.N("Fit")]))));
        pdf.Replace(outlineRoot, TestPdf.Dict(("Type", TestPdf.N("Outlines")), ("First", TestPdf.Ref(outline)), ("Last", TestPdf.Ref(outline)), ("Count", PdfInteger.Of(1))));
        catalog.Set(PdfName.Get("Outlines"), TestPdf.Ref(outlineRoot));

        var thumb = pdf.Add(Plain("VORSCHAUBILD", ("Width", PdfInteger.Of(1)), ("Height", PdfInteger.Of(1))));
        page.Set(PdfName.Get("Thumb"), TestPdf.Ref(thumb));
        page.Set(PdfName.Get("AA"), TestPdf.Dict(("O", TestPdf.Ref(script))));
        var popup = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Popup")), ("Rect", TestPdf.Box(300, 300, 400, 400))));
        var note = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Text")), ("Rect", TestPdf.Box(10, 10, 30, 30)),
            ("Contents", S("GEHEIMER-KOMMENTAR")), ("Popup", TestPdf.Ref(popup))));
        var attachment = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("FileAttachment")), ("Rect", TestPdf.Box(40, 10, 60, 30)), ("FS", TestPdf.Ref(spec))));
        var jsLink = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Link")), ("Rect", TestPdf.Box(70, 10, 90, 30)), ("A", TestPdf.Ref(script))));
        var webLink = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Link")), ("Rect", TestPdf.Box(100, 10, 120, 30)),
            ("A", TestPdf.Dict(("S", TestPdf.N("URI")), ("URI", S("https://example.org"))))));
        page.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(note), TestPdf.Ref(popup), TestPdf.Ref(attachment), TestPdf.Ref(jsLink), TestPdf.Ref(webLink)]));
        return pdf.Build();
    }

    /// <summary>
    /// A form that embeds another PDF page (as typesetting programs write it) carries the path and the document properties of that file
    /// (/PTEX.FileName, /PTEX.InfoDict): found as private data and removed with it (docs/FEHLERGEDAECHTNIS.md, No. 137).
    /// </summary>
    [Fact]
    public void EmbeddedPageInfo_FoundAndRemoved()
    {
        var pdf = TestPdf.Simple(1);
        var info = pdf.Add(TestPdf.Dict(("Title", S("Eingebetteter Geheimtitel")), ("Author", S("Erika Musterfrau"))));
        var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 100, 100)),
            ("PTEX.FileName", S("/home/user/Dokumente/geheimplan.pdf")), ("PTEX.PageNumber", PdfInteger.Of(1)), ("PTEX.InfoDict", TestPdf.Ref(info))),
            "0 0 50 50 re f"u8.ToArray()));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(PdfName.Get("XObject"), TestPdf.Dict(("Fm", TestPdf.Ref(form))));
        pdf.Replace(4, TestPdf.FlateStream("q /Fm Do Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Contains(DocumentCleanup.Inspect(doc, PageItem.From(doc)), f => f.Item == CleanupItems.PrivateData);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Cleanup = CleanupItems.PrivateData });
        var data = output.ToArray();
        foreach (var word in new[] { "Geheimtitel", "Musterfrau", "geheimplan" })
            Assert.Empty(PdfKnead.Cli.LeakSearch.Find(data, [word], null, out _));
    }

    [Fact]
    public void Inspect_FindsEverything()
    {
        using var doc = PdfDocument.Open(Laden());
        var found = DocumentCleanup.Inspect(doc, PageItem.From(doc)).ToDictionary(f => f.Item);
        Assert.Contains($"{Tr.T("Autor")}: Max Mustermann", found[CleanupItems.Metadata].Detail);
        Assert.Contains("XMP", found[CleanupItems.Metadata].Detail);
        Assert.Equal(2, found[CleanupItems.Attachments].Count);   // name tree + file attachment annotation
        Assert.True(found[CleanupItems.Scripts].Count >= 3);      // document JavaScript, open action, page action, link
        Assert.Equal(1, found[CleanupItems.Thumbnails].Count);
        Assert.Equal(1, found[CleanupItems.PrivateData].Count);
        Assert.Equal(1, found[CleanupItems.Comments].Count);      // the note (the popup does not count separately)
        Assert.Equal(1, found[CleanupItems.Bookmarks].Count);
        Assert.True(found.ContainsKey(CleanupItems.Structure));
    }

    [Fact]
    public void Cleanup_RemovesEverything_LinksAndContentStay()
    {
        using var doc = PdfDocument.Open(Laden());
        var all = Enum.GetValues<CleanupItems>().Aggregate(CleanupItems.None, (a, b) => a | b);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Cleanup = all, CompressStreams = false, ObjectStreams = false });
        var bytes = output.ToArray();
        var raw = System.Text.Encoding.Latin1.GetString(bytes);
        foreach (var secret in (ReadOnlySpan<string>)["Max Mustermann", "MAX-MUSTERMANN-XMP", "GEHEIMER-ANHANG-INHALT", "GEHEIMES-SKRIPT",
                     "GEHEIME-ZEICHNUNG", "VORSCHAUBILD", "GEHEIMER-KOMMENTAR", "Kapitel Mustermann", "Textprogramm", "PDF-Knead"])
            Assert.DoesNotContain(secret, raw);

        using var clean = PdfDocument.Open(bytes);
        Assert.Empty(DocumentCleanup.Inspect(clean, PageItem.From(clean)));
        var annots = clean.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        Assert.Equal(2, annots.Count);   // both links stay …
        Assert.Null(annots.Get(0).AsDictionary()!.Get(PdfName.Get("A")).AsDictionary());   // … the one with the script without its action
        Assert.Equal("URI", annots.Get(1).AsDictionary()!.GetDictionary(PdfName.Get("A"))!.GetName(PdfName.Get("S"))!.Value);
        Assert.Contains("Seite 1", string.Concat(Core.Pdf.Content.PageInterpreter.Build(clean, clean.Pages[0]).Text.Select(t => t.Text)));
    }

    [Fact]
    public void Cleanup_OnlyChosenItems()
    {
        using var doc = PdfDocument.Open(Laden());
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Cleanup = CleanupItems.Scripts | CleanupItems.Attachments });
        using var result = PdfDocument.Open(output.ToArray());
        var left = DocumentCleanup.Inspect(result, PageItem.From(result)).Select(f => f.Item).ToHashSet();
        Assert.DoesNotContain(CleanupItems.Scripts, left);
        Assert.DoesNotContain(CleanupItems.Attachments, left);
        Assert.Contains(CleanupItems.Metadata, left);
        Assert.Contains(CleanupItems.Comments, left);
        Assert.Contains(CleanupItems.Bookmarks, left);
    }

    /// <summary>
    /// Page with a hidden layer (text in BDC /OC, plus a form XObject with its own /OC), invisible text (3 Tr) and
    /// visible text.
    /// </summary>
    internal static byte[] HiddenContent()
    {
        var pdf = TestPdf.Simple(1);
        var layer = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Interne Kalkulation"u8.ToArray()))));
        var visibleLayer = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Grundriss"u8.ToArray()))));
        var form = pdf.Add(TestPdf.FlateStream("BT /F1 12 Tf 0 0 Td (Formular verborgen) Tj ET 0 0 50 10 re f"u8.ToArray(), TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 300, 50)), ("OC", TestPdf.Ref(layer)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))))))));
        pdf.Replace(4, TestPdf.FlateStream((
            "BT /F1 12 Tf 72 700 Td (Sichtbar bleibt) Tj ET " +
            "/OC /L1 BDC BT /F1 12 Tf 72 680 Td (Marge 38 Prozent) Tj ET 72 600 100 20 re f EMC " +
            "/OC /L2 BDC BT /F1 12 Tf 72 660 Td (Grundriss sichtbar) Tj ET EMC " +
            "q 1 0 0 1 72 500 cm /Fm0 Do Q " +
            "BT 3 Tr /F1 12 Tf 72 640 Td (Erkannter Scantext) Tj ET").Select(c => (byte)c).ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))),
            ("XObject", TestPdf.Dict(("Fm0", TestPdf.Ref(form)))),
            ("Properties", TestPdf.Dict(("L1", TestPdf.Ref(layer)), ("L2", TestPdf.Ref(visibleLayer))))));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(layer), TestPdf.Ref(visibleLayer)])),
            ("D", TestPdf.Dict(("OFF", new PdfArray([TestPdf.Ref(layer)]))))));
        return pdf.Build();
    }

    [Fact]
    public void HiddenLayersAndInvisibleText_FoundAndRemovedSeparately()
    {
        using var doc = PdfDocument.Open(HiddenContent());
        var found = DocumentCleanup.Inspect(doc, PageItem.From(doc));
        var layers = Assert.Single(found, f => f.Item == CleanupItems.HiddenLayers);
        Assert.Equal(1, layers.Count);
        Assert.Equal("Interne Kalkulation", layers.Detail);
        Assert.Equal(1, Assert.Single(found, f => f.Item == CleanupItems.InvisibleText).Count);

        // Layers only: the hidden text (also in the form XObject) is gone, the invisible scan text stays
        using (var output = new MemoryStream())
        {
            PdfWriter.Write(doc, output, new PdfSaveOptions { Cleanup = CleanupItems.HiddenLayers });
            using var saved = PdfDocument.Open(output.ToArray());
            var all = RedactionTests.Everything(saved);
            Assert.DoesNotContain("Marge 38 Prozent", all);
            Assert.DoesNotContain("Formular verborgen", all);
            Assert.DoesNotContain("72 600 100 20 re", all);
            Assert.Contains("Sichtbar bleibt", all);
            Assert.Contains("Grundriss sichtbar", all);
            Assert.Contains("Erkannter Scantext", all);
        }
        // Invisible text only
        using (var output = new MemoryStream())
        {
            PdfWriter.Write(doc, output, new PdfSaveOptions { Cleanup = CleanupItems.InvisibleText });
            using var saved = PdfDocument.Open(output.ToArray());
            var all = RedactionTests.Everything(saved);
            Assert.DoesNotContain("Erkannter Scantext", all);
            Assert.Contains("Marge 38 Prozent", all);
            Assert.Contains("Sichtbar bleibt", all);
            Assert.DoesNotContain(DocumentCleanup.Inspect(saved, PageItem.From(saved)), f => f.Item == CleanupItems.InvisibleText);
        }
    }

    /// <summary>
    /// Redacting with „nur auf Seiten mit Schwärzung“: the page items of the cleanup apply only to the chosen pages (<see cref="CleanupScope"/>)
    /// — there the hidden layer's content, the invisible text and the hidden annotation go; the other page keeps all of it, and the layer
    /// stays, because that page still uses it.
    /// </summary>
    [Fact]
    public void ScopedCleanup_OnlyOnTheChosenPages_LayerStays()
    {
        const string Page = "BT /F1 12 Tf 72 700 Td (Sichtbar{0}) Tj ET /OC /L1 BDC BT /F1 12 Tf 72 680 Td (Ebene{0}) Tj ET EMC " +
                            "BT 3 Tr /F1 12 Tf 72 660 Td (Scantext{0}) Tj ET";
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/OCProperties<</OCGs[9 0 R]/D<</OFF[9 0 R]>>>>>>",
            [2] = "<</Type/Pages/Kids[4 0 R 5 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Annots[10 0 R]/Resources<</Font<</F1 3 0 R>>/Properties<</L1 9 0 R>>>>>>",
            [5] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Annots[11 0 R]/Resources<</Font<</F1 3 0 R>>/Properties<</L1 9 0 R>>>>>>",
            [6] = ("", string.Format(Page, "Eins")),
            [7] = ("", string.Format(Page, "Zwei")),
            [9] = "<</Type/OCG/Name(Intern)>>",
            [10] = "<</Type/Annot/Subtype/Text/Rect[100 500 120 520]/F 2/Contents(NotizEins)>>",
            [11] = "<</Type/Annot/Subtype/Text/Rect[100 500 120 520]/F 2/Contents(NotizZwei)>>",
        };
        using var doc = PdfDocument.Open(CraftedFileTests.Build(objects));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions
        {
            ScopedCleanup = new CleanupScope(CleanupItems.HiddenLayers | CleanupItems.InvisibleText | CleanupItems.HiddenContent | CleanupItems.Metadata,
                new HashSet<int> { 1 }),
        });
        using var saved = PdfDocument.Open(output.ToArray());
        var all = RedactionTests.Everything(saved);
        foreach (var gone in new[] { "EbeneZwei", "ScantextZwei", "NotizZwei" }) Assert.DoesNotContain(gone, all);
        foreach (var kept in new[] { "SichtbarEins", "SichtbarZwei", "EbeneEins", "ScantextEins", "NotizEins", "Intern" }) Assert.Contains(kept, all);
        Assert.Single(DocumentCleanup.HiddenLayerNames(saved));   // the layer is still there, still off
        Assert.Contains(DocumentCleanup.Inspect(saved, PageItem.From(saved)), f => f.Item == CleanupItems.InvisibleText && f.Pages is [0]);
    }

    /// <summary>
    /// A layer that is off on screen but on when printed (/AS with /Event /Print, /Usage /Print /PrintState /ON — a watermark „KOPIE“, a
    /// note for the paper) is not hidden: it shows on paper. „Inhalte ausgeblendeter Ebenen“ keeps its content, its annotation and the layer
    /// itself with its state — dropping only the layer would have made its content show on screen as well.
    /// </summary>
    [Fact]
    public void LayerShownOnlyWhenPrinted_StaysWithItsStateAndAnnotation()
    {
        var pdf = TestPdf.Simple(1);
        var print = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Druckvermerk"u8.ToArray())),
            ("Usage", TestPdf.Dict(("Print", TestPdf.Dict(("PrintState", TestPdf.N("ON"))))))));
        var hidden = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Interne Kalkulation"u8.ToArray()))));
        var note = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Square")), ("Rect", TestPdf.Box(300, 300, 340, 340)),
            ("OC", TestPdf.Ref(print)), ("Contents", new PdfString("Nur auf Papier"u8.ToArray()))));
        pdf.Replace(4, TestPdf.FlateStream((
            "BT /F1 12 Tf 72 700 Td (Sichtbar bleibt) Tj ET " +
            "/OC /P1 BDC BT /F1 12 Tf 72 680 Td (KOPIE fuer die Akte) Tj ET EMC " +
            "/OC /H1 BDC BT /F1 12 Tf 72 660 Td (Marge 38 Prozent) Tj ET EMC").Select(c => (byte)c).ToArray()));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))),
            ("Properties", TestPdf.Dict(("P1", TestPdf.Ref(print)), ("H1", TestPdf.Ref(hidden))))));
        page.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(note)]));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(print), TestPdf.Ref(hidden)])),
            ("D", TestPdf.Dict(("OFF", new PdfArray([TestPdf.Ref(print), TestPdf.Ref(hidden)])),
                ("AS", new PdfArray([TestPdf.Dict(("Event", TestPdf.N("Print")), ("Category", new PdfArray([TestPdf.N("Print")])),
                    ("OCGs", new PdfArray([TestPdf.Ref(print)])))]))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var layers = Assert.Single(DocumentCleanup.Inspect(doc, PageItem.From(doc)), f => f.Item == CleanupItems.HiddenLayers);
        Assert.Equal("Interne Kalkulation", layers.Detail);   // the print layer is not named

        using var output = new MemoryStream();
        PdfWriter.Write(doc, output, new PdfSaveOptions { Cleanup = CleanupItems.HiddenLayers });
        using var saved = PdfDocument.Open(output.ToArray());
        var all = RedactionTests.Everything(saved);
        Assert.DoesNotContain("Marge 38 Prozent", all);
        Assert.Contains("KOPIE fuer die Akte", all);
        Assert.Contains("Nur auf Papier", all);
        // the layer is still there, off on screen and on in print: on screen the note for the paper stays hidden
        var groups = saved.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!.GetArray(PdfName.Get("OCGs"))!;
        var kept = (PdfDictionary)Assert.Single(Enumerable.Range(0, groups.Count).Select(i => groups.Get(i)))!;
        Assert.Equal("Druckvermerk", kept.GetString(PdfName.Name)!.ToText());
        Assert.True(OptionalContent.For(saved).IsHidden(kept, null));
        Assert.False(OptionalContent.For(saved, print: true).IsHidden(kept, null));
        Assert.DoesNotContain("KOPIE", PageText.Create(PageInterpreter.BuildText(saved, saved.Pages[0]), 0).Text);
        Assert.DoesNotContain(DocumentCleanup.Inspect(saved, PageItem.From(saved)), f => f.Item == CleanupItems.HiddenLayers);
    }

    /// <summary>
    /// Data no page shows (docs/RECHERCHE-VERSTECKEN.md, B-1 and B-2): a page template outside the page tree (/Names /Templates) with
    /// its own text, the origin of a web capture (/SpiderInfo, /URLS), an article thread with its title and its bead on the page,
    /// a portfolio view and a further layer configuration. Before, all of it was written unchecked: neither search nor redaction
    /// reached the template, and no item of the sanitising named or removed the rest. Now the template counts under „Skripte“ (only a
    /// script shows it), the rest as „Weitere Dokumentdaten“; removed, none of it is left in the file.
    /// </summary>
    [Fact]
    public void HiddenTemplatePagesAndOtherCatalogData_FoundAndRemoved()
    {
        var pdf = TestPdf.Simple(1);
        var templateContent = pdf.Add(TestPdf.FlateStream("BT /F1 12 Tf 72 700 Td (Geheime Vorlage) Tj ET"u8.ToArray()));
        var template = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("MediaBox", TestPdf.Box(0, 0, 595, 842)), ("Contents", TestPdf.Ref(templateContent)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))))));
        var thread = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Thread")), ("I", TestPdf.Dict(("Title", new PdfString("Artikeltitel Geheim"u8.ToArray()))))));
        var bead = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Bead")), ("T", TestPdf.Ref(thread)), ("P", TestPdf.Ref(5)), ("R", TestPdf.Box(72, 600, 300, 700))));
        ((PdfDictionary)pdf.Get(bead)).Set(PdfName.Get("N"), TestPdf.Ref(bead));
        ((PdfDictionary)pdf.Get(bead)).Set(PdfName.Get("V"), TestPdf.Ref(bead));
        ((PdfDictionary)pdf.Get(thread)).Set(PdfName.Get("F"), TestPdf.Ref(bead));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("B"), new PdfArray([TestPdf.Ref(bead)]));
        var catalog = (PdfDictionary)pdf.Get(pdf.RootNumber);
        catalog.Set(PdfName.Get("Names"), TestPdf.Dict(("Templates", TestPdf.Dict(("Names", new PdfArray([new PdfString("Vorlage1"u8.ToArray()), TestPdf.Ref(template)])))),
            ("URLS", TestPdf.Dict(("Names", new PdfArray([new PdfString("https://intern.example.org/geheim"u8.ToArray()), TestPdf.Dict()]))))));
        catalog.Set(PdfName.Get("SpiderInfo"), TestPdf.Dict(("V", new PdfReal(1.0))));
        catalog.Set(PdfName.Get("Threads"), new PdfArray([TestPdf.Ref(thread)]));
        catalog.Set(PdfName.Get("Collection"), TestPdf.Dict(("Type", TestPdf.N("Collection")), ("View", TestPdf.N("D"))));
        var layer = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString("Ebene"u8.ToArray()))));
        catalog.Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(layer)])), ("D", TestPdf.Dict()),
            ("Configs", new PdfArray([TestPdf.Dict(("Name", new PdfString("Konfiguration Geheim"u8.ToArray())), ("OFF", new PdfArray([TestPdf.Ref(layer)])))]))));
        using var doc = PdfDocument.Open(pdf.Build());

        var found = DocumentCleanup.Inspect(doc, PageItem.From(doc));
        var scripts = Assert.Single(found, f => f.Item == CleanupItems.Scripts);
        Assert.Contains("versteckte Seite", scripts.Detail);
        var other = Assert.Single(found, f => f.Item == CleanupItems.OtherData);
        foreach (var part in new[] { "Herkunft aus dem Web", "Artikel", "Portfolio-Ansicht", "weitere Ebenen-Einstellungen" }) Assert.Contains(part, other.Detail);
        // the search of redacted text elsewhere reads the template
        var templates = DocumentCleanup.TemplatePages(doc);
        Assert.Contains("Geheime Vorlage", Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.BuildText(doc, Assert.Single(templates))).Text);

        using (var kept = new MemoryStream())
        {
            PdfWriter.Write(doc, kept, new PdfSaveOptions { CompressStreams = false });
            var all = RedactionTests.Everything(PdfDocument.Open(kept.ToArray()));
            Assert.Contains("Geheime Vorlage", all);   // without the items everything stays, as before
        }
        using var output = new MemoryStream();
        PdfWriter.Write(doc, output, new PdfSaveOptions { Cleanup = CleanupItems.Scripts | CleanupItems.OtherData, CompressStreams = false });
        using var saved = PdfDocument.Open(output.ToArray());
        var everything = RedactionTests.Everything(saved);
        foreach (var secret in new[] { "Geheime Vorlage", "Artikeltitel Geheim", "intern.example.org", "Konfiguration Geheim" }) Assert.False(everything.Contains(secret, StringComparison.Ordinal), secret);
        foreach (var key in new[] { "SpiderInfo", "Threads", "Collection" }) Assert.False(saved.Catalog!.ContainsKey(PdfName.Get(key)), key);
        Assert.False(saved.Pages[0].Dictionary.ContainsKey(PdfName.Get("B")));
        Assert.DoesNotContain(DocumentCleanup.Inspect(saved, PageItem.From(saved)), f => f.Item is CleanupItems.OtherData or CleanupItems.Scripts);
        Assert.Equal(1, saved.PageCount);
    }

    /// <summary>
    /// The preview in the document's XMP (xmp:Thumbnails) shows the first page as it was (docs/RECHERCHE-VERSTECKEN.md, B-4). Before, it
    /// stayed after redaction unless „Metadaten“ was chosen — like /Thumb of the page, which the redaction drops. Now it goes whenever marks
    /// are applied; the rest of the metadata stays. Without redaction it stays too.
    /// </summary>
    [Fact]
    public void Redaction_RemovesTheXmpPreview_KeepsTheRest()
    {
        const string xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF " +
                           "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
                           "xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:xmpGImg=\"http://ns.adobe.com/xap/1.0/g/img/\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">" +
                           "Titel bleibt</rdf:li></rdf:Alt></dc:title><xmp:Thumbnails><rdf:Alt><rdf:li rdf:parseType=\"Resource\"><xmpGImg:width>64</xmpGImg:width>" +
                           "<xmpGImg:image>VORSCHAUVORHER</xmpGImg:image></rdf:li></rdf:Alt></xmp:Thumbnails></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        var pdf = TestPdf.Simple(1);
        var meta = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))), System.Text.Encoding.UTF8.GetBytes(xmp)));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("Metadata"), TestPdf.Ref(meta));
        using var doc = PdfDocument.Open(pdf.Build());

        string Xmp(byte[] data)
        {
            using var saved = PdfDocument.Open(data);
            return System.Text.Encoding.UTF8.GetString(saved.GetStreamData(saved.Catalog!.GetStream(PdfName.Get("Metadata"))!));
        }
        byte[] Save(bool redact)
        {
            var items = PageItem.From(doc);
            if (redact) items[0] = items[0].WithElements([RedactionElement.FromPageAreas([new PdfRectangle(60, 690, 200, 720)])]);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, CompressStreams = false });
            return output.ToArray();
        }

        Assert.Contains("VORSCHAUVORHER", Xmp(Save(redact: false)));
        var after = Xmp(Save(redact: true));
        Assert.DoesNotContain("VORSCHAUVORHER", after);
        Assert.DoesNotContain("Thumbnails", after);
        Assert.Contains("Titel bleibt", after);
        Assert.Contains("</x:xmpmeta>", after);
    }

    private const string XmpWithPreview =
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF " +
        "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:xmpGImg=\"http://ns.adobe.com/xap/1.0/g/img/\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">" +
        "Titel bleibt</rdf:li></rdf:Alt></dc:title><xmp:Thumbnails><rdf:Alt><rdf:li rdf:parseType=\"Resource\"><xmpGImg:width>64</xmpGImg:width>" +
        "<xmpGImg:image>VORSCHAUVORHER</xmpGImg:image></rdf:li></rdf:Alt></xmp:Thumbnails></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    private static PdfDocument WithPreview(int pages)
    {
        var pdf = TestPdf.Simple(pages);
        var meta = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))), System.Text.Encoding.UTF8.GetBytes(XmpWithPreview)));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("Metadata"), TestPdf.Ref(meta));
        return PdfDocument.Open(pdf.Build());
    }

    private static string SavedXmp(PdfDocument doc, IReadOnlyList<PageItem> items, PdfSaveOptions? options = null)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, options ?? new PdfSaveOptions { CompressStreams = false });
        using var saved = PdfDocument.Open(output.ToArray());
        return System.Text.Encoding.UTF8.GetString(saved.GetStreamData(saved.Catalog!.GetStream(PdfName.Get("Metadata"))!));
    }

    /// <summary>
    /// The preview in the XMP shows the first page as it was: deleting it (or any change of the page order) removes the preview, the rest of
    /// the metadata stays; saving without a change keeps it (decided on 3.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 446).
    /// </summary>
    [Fact]
    public void DeletedFirstPage_RemovesTheXmpPreview()
    {
        using var doc = WithPreview(2);
        Assert.Contains("VORSCHAUVORHER", SavedXmp(doc, PageItem.From(doc)));
        var after = SavedXmp(doc, PageItem.From(doc).Skip(1).ToList());
        Assert.DoesNotContain("VORSCHAUVORHER", after);
        Assert.Contains("Titel bleibt", after);
    }

    /// <summary>Something added to a page — the preview no longer shows it as it is (No. 446).</summary>
    [Fact]
    public void EditedPage_RemovesTheXmpPreview()
    {
        using var doc = WithPreview(1);
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new TextElement { Rect = new PdfRectangle(100, 100, 300, 130), Text = "neu" }]);
        Assert.DoesNotContain("VORSCHAUVORHER", SavedXmp(doc, items));
        var rotated = PageItem.From(doc);
        rotated[0] = rotated[0].Rotated(90);
        Assert.DoesNotContain("VORSCHAUVORHER", SavedXmp(doc, rotated));
    }

    /// <summary>„Vorschaubilder“ in „Dokument bereinigen“ removes the preview in the XMP too, and offers it (No. 446).</summary>
    [Fact]
    public void ThumbnailsItem_RemovesTheXmpPreview_AndOffersIt()
    {
        using var doc = WithPreview(1);
        var finding = Assert.Single(DocumentCleanup.Inspect(doc, PageItem.From(doc)), f => f.Item == CleanupItems.Thumbnails);
        Assert.Equal(Tr.T("Vorschaubild in den Metadaten"), finding.Detail);
        var after = SavedXmp(doc, PageItem.From(doc), new PdfSaveOptions { CompressStreams = false, Cleanup = CleanupItems.Thumbnails });
        Assert.DoesNotContain("VORSCHAUVORHER", after);
        Assert.Contains("Titel bleibt", after);
    }

    /// <summary>
    /// A file associated only with an image (/AF, not in the name tree): „Dokument bereinigen“ offers „Dateianhänge“ — and so does the
    /// question before signing, which uses the same findings (docs/FEHLERGEDAECHTNIS.md, No. 446, Analyse K-08).
    /// </summary>
    [Fact]
    public void Inspect_AssociatedFileOnlyOnAnImage_IsOffered()
    {
        var pdf = TestPdf.Simple(1, title: null);
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), "Max Mustermann;4711"u8.ToArray()));
        var spec = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", new PdfString("daten.csv"u8.ToArray())), ("EF", TestPdf.Dict(("F", TestPdf.Ref(file))))));
        var image = pdf.Add(TestPdf.FlateStream([220, 40, 40], TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(1)),
            ("Height", PdfInteger.Of(1)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)), ("AF", new PdfArray([TestPdf.Ref(spec)])))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        pdf.Replace(4, TestPdf.FlateStream("q 100 0 0 100 100 600 cm /Im0 Do Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var finding = Assert.Single(DocumentCleanup.Inspect(doc, PageItem.From(doc)), f => f.Item == CleanupItems.Attachments);
        Assert.Equal(1, finding.Count);
        Assert.Equal(Tr.T("davon {0} zugeordnete Dateien (/AF)", 1), finding.Detail);
    }

    /// <summary>Only a page carries XMP (no /Info, no XMP of the document): „Metadaten“ is offered (No. 446, Analyse K-08).</summary>
    [Fact]
    public void Inspect_PageXmpOnly_IsOfferedAsMetadata()
    {
        var pdf = TestPdf.Simple(1, title: null);
        var xmp = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))), "<x:xmpmeta>Max Mustermann</x:xmpmeta>"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Metadata"), TestPdf.Ref(xmp));
        using var doc = PdfDocument.Open(pdf.Build());
        var finding = Assert.Single(DocumentCleanup.Inspect(doc, PageItem.From(doc)), f => f.Item == CleanupItems.Metadata);
        Assert.Contains(Tr.T("XMP an {0} Objekten (Seiten, Bilder, Kommentare …)", 1), finding.Detail);
    }

    /// <summary>
    /// Invisible text in the appearance stream of an annotation (a stamp whose /AP shows a frame, a visible line and, in mode 3, a
    /// sentence, plus a form with invisible text of its own): before, „Bereinigen“ with „Unsichtbarer Text“ left it there — only the
    /// page content and its forms were cleaned. Now the appearance loses the invisible text, also in the form it calls; frame and
    /// visible line stay, the annotation stays, and the finding counts the page.
    /// </summary>
    /// <summary>
    /// A damaged file whose page tree points at a JavaScript action (the public test file issue15590 has this form): read as a page, its
    /// script went into the cleaned file with it (docs/FEHLERGEDAECHTNIS.md, No. 246). „Skripte“ removes the script from any dictionary.
    /// </summary>
    [Fact]
    public void ScriptInADictionaryReadAsPage_Removed()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\ntrailer\n<</Root 1 0 R>>\n1 0 obj\n<</Type/Catalog/Pages 2 0 R/OpenAction 2 0 R>>\nendobj\n" +
            "2 0 obj\n<</S/JavaScript/JS(app.alert\\('SKRIPT-IN-DER-SEITE'\\))>>\nendobj\n%%EOF\n");
        using var doc = PdfDocument.Open(data);
        Assert.True(PdfKnead.Cli.LeakSearch.Find(data, ["SKRIPT-IN-DER-SEITE"], null, out _).Count > 0);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Cleanup = CleanupItems.Scripts });
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(output.ToArray(), ["SKRIPT-IN-DER-SEITE"], null, out _));
        using var cleaned = PdfDocument.Open(output.ToArray());
        Assert.Equal(doc.PageCount, cleaned.PageCount);
        Assert.False(cleaned.Pages[0].Dictionary.ContainsKey(PdfName.Get("S")));
    }

    [Fact]
    public void InvisibleTextInAnnotationAppearance_Removed()
    {
        var pdf = TestPdf.Simple(1);
        var inner = pdf.Add(TestPdf.FlateStream("BT 3 Tr /F1 10 Tf 2 2 Td (Innen versteckt) Tj ET"u8.ToArray(), TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 200, 40)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))))))));
        var look = pdf.Add(TestPdf.FlateStream(
            "0 0 1 RG 1 1 198 38 re S BT /F1 12 Tf 5 20 Td (Stempel sichtbar) Tj 3 Tr 0 -12 Td (Geheime Notiz) Tj ET /Fm0 Do"u8.ToArray(),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 200, 40)),
                ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))), ("XObject", TestPdf.Dict(("Fm0", TestPdf.Ref(inner)))))))));
        var stamp = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Stamp")), ("Rect", TestPdf.Box(72, 600, 272, 640)),
            ("AP", TestPdf.Dict(("N", TestPdf.Ref(look))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(stamp)]));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Equal(1, Assert.Single(DocumentCleanup.Inspect(doc, PageItem.From(doc)), f => f.Item == CleanupItems.InvisibleText).Count);

        using var output = new MemoryStream();
        PdfWriter.Write(doc, output, new PdfSaveOptions { Cleanup = CleanupItems.InvisibleText });
        using var saved = PdfDocument.Open(output.ToArray());
        var all = RedactionTests.Everything(saved);
        Assert.DoesNotContain("Geheime Notiz", all);
        Assert.DoesNotContain("Innen versteckt", all);
        Assert.Contains("Stempel sichtbar", all);
        Assert.Contains("198 38 re", all);
        Assert.Single(saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!);
        Assert.DoesNotContain(DocumentCleanup.Inspect(saved, PageItem.From(saved)), f => f.Item == CleanupItems.InvisibleText);
    }

    /// <summary>
    /// Text rendering mode as the display reads it: Q restores the 3 that q saved, and 3.5 counts as 3 — the check before cleaning
    /// followed Tr without q/Q and compared the number exactly, so neither the page nor the stamp counted as having invisible text and
    /// the stamp's text stayed in the file (docs/FEHLERGEDAECHTNIS.md, No. 425).
    /// </summary>
    [Fact]
    public void InvisibleTextAfterQOrWithFractionalMode_Found()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("3 Tr q 0 Tr Q BT /F1 12 Tf 72 700 Td (Seite versteckt) Tj ET"u8.ToArray()));
        var look = pdf.Add(TestPdf.FlateStream("BT 3.5 Tr /F1 12 Tf 5 20 Td (Stempel versteckt) Tj ET"u8.ToArray(),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 200, 40)),
                ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))))))));
        var stamp = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Stamp")), ("Rect", TestPdf.Box(72, 600, 272, 640)),
            ("AP", TestPdf.Dict(("N", TestPdf.Ref(look))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(stamp)]));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Equal([0], DocumentCleanup.PagesWithInvisibleText(PageItem.From(doc)));
        Assert.Single(DocumentCleanup.InvisibleTextAppearances(doc, doc.Pages[0]));

        using var output = new MemoryStream();
        PdfWriter.Write(doc, output, new PdfSaveOptions { Cleanup = CleanupItems.InvisibleText });
        using var saved = PdfDocument.Open(output.ToArray());
        var all = RedactionTests.Everything(saved);
        Assert.DoesNotContain("Seite versteckt", all);
        Assert.DoesNotContain("Stempel versteckt", all);
        Assert.Empty(DocumentCleanup.PagesWithInvisibleText(PageItem.From(saved)));
    }
}
