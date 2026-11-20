using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>UI tests for privacy, security and stability: background work, closing, file permissions and redaction leftovers.</summary>
[Collection(nameof(UiCollection))]
public sealed class CrossRoundUiTests
{
    /// <summary>
    /// While "Save as" writes in the background, editing and undo change nothing, "Save" is disabled, and closing waits until saving
    /// has finished; otherwise changes are lost and the file is released while the writer is still using it.
    /// </summary>
    [Fact]
    public void Work_LocksEditing_CloseWaits()
    {
        var pdf = TestFiles.Write(CompressTiming.Scans(12), "arbeit.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "arbeit-kopie.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.AddElement(0, new TextElement { Rect = new PdfRectangle(100, 100, 300, 130), Text = "vorher" }, "Text");
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                var saving = vm.SaveAsCommand.ExecuteAsync(null);
                Assert.True(doc.IsWorking);
                Assert.False(vm.SaveCommand.CanExecute(null));
                Assert.False(vm.UndoCommand.CanExecute(null));
                // editing while writing changes nothing (the save run then takes over its own state)
                doc.AddElement(0, new TextElement { Rect = new PdfRectangle(100, 200, 300, 230), Text = "währenddessen" }, "Text");
                Assert.Single(doc.ElementsOn(0));
                // closing waits for the save
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                var closing = vm.CloseDocumentCommand.ExecuteAsync(doc);
                await saving;
                await closing;
                Assert.Empty(vm.Documents);
                using var saved = PdfDocument.Open(target);
                Assert.Equal(12, saved.PageCount);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Inserting pages from a protected file requires the copy permission. The assemble permission alone is not enough, because the
    /// content could then be copied through this document and saved without a password. With an open password, a hint says that the
    /// protection does not apply here.
    /// </summary>
    [Fact]
    public void InsertFromProtected_NeedsCopyRight_WarnsAboutPassword()
    {
        var target = TestFiles.Write(TestPdf.Simple(1).Build(), "ziel.pdf");
        var assembleOnly = TestFiles.Write(TestPdf.Simple(2).Build(cipher: Core.Pdf.Security.PdfCipher.Aes256, user: "lesen", owner: "besitzer",
            permissions: Core.Pdf.Security.PdfPermissions.Assemble), "nur-zusammenstellen.pdf");
        var copyAllowed = TestFiles.Write(TestPdf.Simple(2).Build(cipher: Core.Pdf.Security.PdfCipher.Aes256, user: "lesen", owner: "besitzer",
            permissions: Core.Pdf.Security.PdfPermissions.Copy | Core.Pdf.Security.PdfPermissions.Print), "kopieren-erlaubt.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([target]);
                var doc = vm.SelectedDocument!;
                vm.AskPassword = (_, _) => Task.FromResult<string?>("lesen");
                vm.ChoosePages = (source, _) => Task.FromResult<IReadOnlyList<int>?>([.. Enumerable.Range(0, source.PageCount)]);
                await vm.InsertFilesAsync([assembleOnly]);
                Assert.Equal(1, doc.PageCount);
                Assert.Contains(Tr.T("{0}: Die Datei erlaubt kein Übernehmen ihrer Seiten (Rechte der Datei).", "nur-zusammenstellen.pdf"), vm.Message);
                await vm.InsertFilesAsync([copyAllowed]);
                Assert.Equal(3, doc.PageCount);
                Assert.Contains("kopieren-erlaubt.pdf", vm.Message);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Print → save as PDF from a protected file (opened with the owner password): the dialog states that the PDF is created without
    /// a password, and it refuses to save over an open file.
    /// </summary>
    [Fact]
    public void PrintAsPdf_WarnsAboutProtection_RefusesOpenFile()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(2).Build(cipher: Core.Pdf.Security.PdfCipher.Aes256, user: "lesen", owner: "besitzer"), "geschuetzt-druck.pdf");
        var length = new FileInfo(pdf).Length;
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Print = new Core.Printing.PrintPreferences(Printer: Core.Printing.PrintPreferences.PdfTarget) });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                vm.AskPassword = (_, _) => Task.FromResult<string?>("besitzer");
                await vm.OpenAsync([pdf]);
                PrintViewModel? print = null;
                PrintWindow? dialog = null;
                Task<string?>? result = null;
                vm.ShowPrint = p =>
                {
                    print = p;
                    dialog = new PrintWindow(p);
                    result = dialog.ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.PrintCommand.ExecuteAsync(null);
                var until = DateTime.UtcNow.AddSeconds(30);
                while (!(print is { IsReady: true } && print.PrintCommand.CanExecute(null)) && DateTime.UtcNow < until)
                {
                    UiSession.Capture(window);
                    Thread.Sleep(20);
                }
                Assert.True(print!.IsPdf);
                Assert.True(print.PdfWithoutProtection);
                print.PickSavePath = (_, _, _) => Task.FromResult<string?>(pdf);
                await print.PrintCommand.ExecuteAsync(null);
                Assert.Contains("geschuetzt-druck.pdf", print.Problem);
                Assert.Equal(length, new FileInfo(pdf).Length);
                dialog!.Close();
                await result!;
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Copying also omits a character that lies only 40 % under the bar, because redaction removes it as well; judging by the centre
    /// of the character alone would put it on the clipboard.
    /// </summary>
    [Fact]
    public void Copy_PartlyCoveredCharacter_Omitted()
    {
        var pdf = TestFiles.Write(RedactionTests.TextPage("Max Mustermann wohnt hier"), "teilweise.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var text = await doc.GetTextAsync(0);
                var word = Assert.Single(text.Find("Muster"));
                var next = text.Quads(new Core.Pdf.Text.TextRange(word.End, 1))[0].Bounds;   // the „m“ that follows
                var area = DocumentViewModel.AreasOf(text, word)[0];
                // bar reaching 40 % into the next character
                var mark = RedactionElement.FromPageAreas([new PdfRectangle(area.Left, area.Bottom, next.Left + next.Width * 0.4, area.Top)]);
                doc.AddElement(0, mark, "Schwärzen");
                string? copied = null;
                vm.SetClipboardText = value =>
                {
                    copied = value;
                    return Task.CompletedTask;
                };
                doc.Select(0, Assert.Single(text.Find("Max Mustermann wohnt hier")));
                await vm.CopyTextCommand.ExecuteAsync(null);
                Assert.NotNull(copied);
                Assert.DoesNotContain("Mustermann", copied);
                Assert.DoesNotContain("mann", copied);   // the partly covered „m“ is omitted too
                Assert.Contains("ann wohnt hier", copied);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// When the opened file drops out of the history after redaction (more than <see cref="Core.Pdf.Editing.PageHistory.Limit"/> steps),
    /// it is released; otherwise it stays open until the program exits, and its old version, renamed when overwriting, cannot be deleted.
    /// </summary>
    [Fact]
    public void Original_ReleasedWhenNoStepNeedsIt()
    {
        var pdf = TestFiles.Write(RedactionTests.TextPage("Max Mustermann", "Zweite Zeile"), "ausgelaufen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var original = doc.Document;
                var content = (Core.Pdf.Objects.PdfStream)original.Pages[0].Dictionary.Get(Core.Pdf.Objects.PdfName.Contents)!;
                doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(60, 690, 200, 715) }, "Schwärzen");
                vm.ConfirmRedaction = _ => Task.FromResult(new RedactionReply(RedactionAnswer.Apply));
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.NotSame(original, doc.Document);
                for (var i = 0; i < Core.Pdf.Editing.PageHistory.Limit + 2; i++) doc.History.Rotate([0], 90);
                Assert.DoesNotContain(original, doc.History.Sources());
                var until = DateTime.UtcNow.AddSeconds(10);
                var released = false;
                while (!released && DateTime.UtcNow < until)
                {
                    UiSession.Capture(window);
                    try
                    {
                        original.GetRawStreamData(content);
                    }
                    catch (ObjectDisposedException)
                    {
                        released = true;
                    }
                    Thread.Sleep(20);
                }
                Assert.True(released);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Every save into a new file passes through the „Schwärzen zuerst“ gate — a new document, and „Als neue Datei“ after redacting —
    /// not only „Speichern unter“; otherwise the marked text leaves the document in a new file, merely covered by a mark
    /// (docs/FEHLERGEDAECHTNIS.md, No. 129).
    /// </summary>
    [Fact]
    public void NewFile_AlwaysThroughTheRedactionGate()
    {
        var pdf = TestFiles.Write(RedactionTests.TextPage("Max Mustermann", "Zweite Zeile"), "neue-datei.pdf");
        var folder = Path.GetDirectoryName(pdf)!;
        static string Text(string path)
        {
            using var file = PdfDocument.Open(path);
            return string.Concat(Core.Pdf.Content.PageInterpreter.Build(file, file.Pages[0], withoutOwnElements: false).Text.Select(t => t.Text));
        }
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                var questions = new List<RedactionQuestion>();
                vm.ConfirmRedaction = question =>
                {
                    questions.Add(question);
                    return Task.FromResult(new RedactionReply(RedactionAnswer.Apply));
                };
                var target = "";
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);

                // 1. a new document: an own text under a mark
                var created = vm.CreateDocument(new PageSetup());
                created.AddElement(0, new TextElement { Rect = new PdfRectangle(100, 600, 300, 630), Text = "Geheimvermerk" }, "Text");
                created.AddElement(0, new TextElement { Rect = new PdfRectangle(100, 500, 300, 530), Text = "Sichtbar" }, "Text");
                created.AddElement(0, new RedactionElement { Rect = new PdfRectangle(90, 590, 310, 640) }, "Schwärzen");
                target = Path.Combine(folder, "neu-gespeichert.pdf");
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Single(questions);
                Assert.DoesNotContain("Geheimvermerk", Text(target));
                Assert.Contains("Sichtbar", Text(target));

                // 2. redacted, marked again, Ctrl+S → „Als neue Datei“: the new mark is applied too
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.Select(0, Assert.Single((await doc.GetTextAsync(0)).Find("Mustermann")));
                doc.RedactSelection();
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                doc.Select(0, Assert.Single((await doc.GetTextAsync(0)).Find("Zweite")));
                doc.RedactSelection();
                vm.ConfirmRedactedSave = _ => Task.FromResult(RedactedSaveAnswer.SaveAsNew);
                target = Path.Combine(folder, "neue-datei (geschwärzt).pdf");
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Equal(3, questions.Count);
                Assert.DoesNotContain("Mustermann", Text(target));
                Assert.DoesNotContain("Zweite", Text(target));
                Assert.Contains("Zeile", Text(target));
                Assert.Contains("Zweite", Text(pdf));   // the original untouched
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The search for redacted text elsewhere in the document also finds alternate texts in the structure tree, the content of file
    /// attachments, layer names and field options.
    /// </summary>
    [Fact]
    public void FindElsewhere_StructureAttachmentsLayersOptions()
    {
        static Core.Pdf.Objects.PdfString S(string text) => new(Core.Pdf.Objects.PdfTextEncoding.Encode(text));
        var pdf = TestPdf.Simple(1);
        var catalog = (Core.Pdf.Objects.PdfDictionary)pdf.Get(1);
        var page = (Core.Pdf.Objects.PdfDictionary)pdf.Get(5);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 12 Tf 72 700 Td (Erika Musterfrau zahlt) Tj ET"u8.ToArray()));
        var root = pdf.Add(new Core.Pdf.Objects.PdfDictionary());
        var figure = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Figure")), ("P", TestPdf.Ref(root)), ("Alt", S("Foto von Erika Musterfrau"))));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", new Core.Pdf.Objects.PdfArray([TestPdf.Ref(figure)]))));
        catalog.Set(Core.Pdf.Objects.PdfName.Get("StructTreeRoot"), TestPdf.Ref(root));
        var file = pdf.Add(new Core.Pdf.Objects.PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), "Kontakt: Erika Musterfrau"u8.ToArray()));
        var spec = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", S("kontakt.txt")), ("EF", TestPdf.Dict(("F", TestPdf.Ref(file))))));
        catalog.Set(Core.Pdf.Objects.PdfName.Get("Names"), TestPdf.Dict(("EmbeddedFiles", TestPdf.Dict(("Names", new Core.Pdf.Objects.PdfArray([S("kontakt.txt"), TestPdf.Ref(spec)]))))));
        // a file associated with the page only (/AF, not in the name tree): the data behind a chart (docs/FEHLERGEDAECHTNIS.md, No. 434)
        var data = pdf.Add(new Core.Pdf.Objects.PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), "Name;Betrag\nErika Musterfrau;12"u8.ToArray()));
        var dataSpec = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", S("daten.csv")), ("AFRelationship", TestPdf.N("Data")), ("EF", TestPdf.Dict(("F", TestPdf.Ref(data))))));
        page.Set(Core.Pdf.Objects.PdfName.Get("AF"), new Core.Pdf.Objects.PdfArray([TestPdf.Ref(dataSpec)]));
        var layer = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", S("Notizen Musterfrau"))));
        catalog.Set(Core.Pdf.Objects.PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new Core.Pdf.Objects.PdfArray([TestPdf.Ref(layer)])), ("D", new Core.Pdf.Objects.PdfDictionary())));
        var field = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Ch")), ("T", S("Ansprechpartner")),
            ("Opt", new Core.Pdf.Objects.PdfArray([S("Max Muster"), S("Erika Musterfrau")])), ("Rect", TestPdf.Box(300, 500, 450, 520)), ("P", TestPdf.Ref(5))));
        page.Set(Core.Pdf.Objects.PdfName.Get("Annots"), new Core.Pdf.Objects.PdfArray([TestPdf.Ref(field)]));
        catalog.Set(Core.Pdf.Objects.PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new Core.Pdf.Objects.PdfArray([TestPdf.Ref(field)]))));
        var path = TestFiles.Write(pdf.Build(), "anderswo.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([path]);
                var doc = vm.SelectedDocument!;
                await doc.RedactHitsAsync(await doc.FindForRedactionAsync(["Erika Musterfrau"], RedactionPatterns.None, false));
                var places = (await doc.FindElsewhereAsync()).Select(e => e.Where).ToList();
                Assert.Contains(Tr.T("Strukturbaum (Ersatztexte)"), places);
                Assert.Contains(Tr.T("Dateianhänge"), places);
                Assert.Contains(Tr.T("zugeordnete Dateien"), places);
                Assert.Contains(Tr.T("Ebenennamen"), places);
                Assert.Contains(Tr.T("Formularfelder"), places);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    private static Core.Pdf.Objects.PdfString PdfText(string text) => new(Core.Pdf.Objects.PdfTextEncoding.Encode(text));

    /// <summary>A one-page file with <paramref name="line"/> on the page, in WinAnsi (catalogue 1, font 3, content 4, page 5).</summary>
    private static TestPdf Page(string line)
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(System.Text.Encoding.Latin1.GetBytes($"BT /F1 12 Tf 72 700 Td ({line}) Tj ET")));
        ((Core.Pdf.Objects.PdfDictionary)pdf.Get(3)).Set(Core.Pdf.Objects.PdfName.Get("Encoding"), TestPdf.N("WinAnsiEncoding"));
        return pdf;
    }

    private static int FileSpec(TestPdf pdf, string name, byte[] data, string? relationship = null)
    {
        var file = pdf.Add(new Core.Pdf.Objects.PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), data));
        var spec = TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", PdfText(name)), ("UF", PdfText(name)), ("EF", TestPdf.Dict(("F", TestPdf.Ref(file)))));
        if (relationship is not null) spec.Set(Core.Pdf.Objects.PdfName.Get("AFRelationship"), TestPdf.N(relationship));
        return pdf.Add(spec);
    }

    private static void InNameTree(TestPdf pdf, string name, int spec) =>
        ((Core.Pdf.Objects.PdfDictionary)pdf.Get(1)).Set(Core.Pdf.Objects.PdfName.Get("Names"),
            TestPdf.Dict(("EmbeddedFiles", TestPdf.Dict(("Names", new Core.Pdf.Objects.PdfArray([PdfText(name), TestPdf.Ref(spec)]))))));

    /// <summary>Redacts <paramref name="redact"/> on the page and returns the places the question before redacting names.</summary>
    private static List<string> Elsewhere(TestPdf pdf, string redact)
    {
        var path = TestFiles.Write(pdf.Build(), "anderswo-anhang.pdf");
        var places = new List<string>();
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([path]);
                var doc = vm.SelectedDocument!;
                var hits = await doc.FindForRedactionAsync([redact], RedactionPatterns.None, false);
                Assert.NotEmpty(hits);
                await doc.RedactHitsAsync(hits);
                places.AddRange((await doc.FindElsewhereAsync()).Select(e => e.Where));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
        return places;
    }

    /// <summary>
    /// The source of the whole document as a word-processor file (a ZIP, the sentence split into runs), associated with the catalogue
    /// only (/AF /Source, not in the name tree): the question names it. It searched the raw bytes, and the catalogue's /AF not at all
    /// (docs/FEHLERGEDAECHTNIS.md, No. 443, Analyse K-01 and K-02).
    /// </summary>
    [Fact]
    public void FindElsewhere_PackedSourceInTheCatalogue_IsReported()
    {
        var pdf = Page("Konto Geheim 4711");
        var spec = FileSpec(pdf, "vertrag.docx", AttachmentTextTests.Docx(), "Source");
        ((Core.Pdf.Objects.PdfDictionary)pdf.Get(1)).Set(Core.Pdf.Objects.PdfName.Get("AF"), new Core.Pdf.Objects.PdfArray([TestPdf.Ref(spec)]));
        Assert.Contains(Tr.T("zugeordnete Dateien"), Elsewhere(pdf, "Geheim 4711"));
    }

    /// <summary>
    /// The redacted word in the prefix of a page label, in the information of an article, in a table summary of the structure and in a script of
    /// the document (docs/FEHLERGEDAECHTNIS.md, No. 695; found by the overall check of redaction, 6.9): the question names each — before it
    /// searched none of them, and the bars do not reach them.
    /// </summary>
    [Theory]
    [InlineData("label")]
    [InlineData("thread")]
    [InlineData("summary")]
    [InlineData("script")]
    public void FindElsewhere_LabelsArticlesSummariesScripts_AreReported(string place)
    {
        static Core.Pdf.Objects.PdfString S(string text) => new(Core.Pdf.Objects.PdfTextEncoding.Encode(text));
        var pdf = Page("Konto Geheim 4711");
        var catalog = (Core.Pdf.Objects.PdfDictionary)pdf.Get(1);
        string expected;
        switch (place)
        {
            case "label":
                catalog.Set(Core.Pdf.Objects.PdfName.Get("PageLabels"), TestPdf.Dict(("Nums", new Core.Pdf.Objects.PdfArray([Core.Pdf.Objects.PdfInteger.Of(0),
                    TestPdf.Dict(("S", TestPdf.N("D")), ("P", S("Geheim 4711 – ")))]))));
                expected = Tr.T("Seitenbeschriftungen");
                break;
            case "thread":
                var thread = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Thread")), ("I", TestPdf.Dict(("Title", S("Bericht Geheim 4711"))))));
                catalog.Set(Core.Pdf.Objects.PdfName.Get("Threads"), new Core.Pdf.Objects.PdfArray([TestPdf.Ref(thread)]));
                expected = Tr.T("Artikel");
                break;
            case "summary":
                var root = pdf.Add(new Core.Pdf.Objects.PdfDictionary());
                var table = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("Table")), ("P", TestPdf.Ref(root)),
                    ("A", TestPdf.Dict(("O", TestPdf.N("Table")), ("Summary", S("Konten mit Geheim 4711"))))));
                pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", new Core.Pdf.Objects.PdfArray([TestPdf.Ref(table)]))));
                catalog.Set(Core.Pdf.Objects.PdfName.Get("StructTreeRoot"), TestPdf.Ref(root));
                expected = Tr.T("Strukturbaum (Zusammenfassungen von Tabellen)");
                break;
            default:
                var js = pdf.Add(TestPdf.Dict(("S", TestPdf.N("JavaScript")), ("JS", S("var konto = 'Geheim 4711';"))));
                catalog.Set(Core.Pdf.Objects.PdfName.Get("Names"), TestPdf.Dict(("JavaScript", TestPdf.Dict(("Names", new Core.Pdf.Objects.PdfArray([S("start"), TestPdf.Ref(js)]))))));
                expected = Tr.T("Skripte");
                break;
        }
        Assert.Contains(expected, Elsewhere(pdf, "Geheim 4711"));
    }

    /// <summary>An original PDF as an attachment (packed page content): the question names it (No. 443).</summary>
    [Fact]
    public void FindElsewhere_EmbeddedPdf_IsReported()
    {
        var pdf = Page("Konto Geheim 4711");
        InNameTree(pdf, "original.pdf", FileSpec(pdf, "original.pdf", AttachmentTextTests.InnerPdf()));
        Assert.Contains(Tr.T("Dateianhänge"), Elsewhere(pdf, "Geheim 4711"));
    }

    /// <summary>A table exported as UTF-16 text: the question names it (No. 443).</summary>
    [Fact]
    public void FindElsewhere_Utf16Table_IsReported()
    {
        var pdf = Page("Konto Geheim 4711");
        InNameTree(pdf, "liste.txt", FileSpec(pdf, "liste.txt", [0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes("Max Mustermann\tGeheim 4711\r\n")]));
        Assert.Contains(Tr.T("Dateianhänge"), Elsewhere(pdf, "Geheim 4711"));
    }

    /// <summary>
    /// An attachment that cannot be searched (binary of an unknown kind) is reported with its name, not counted as clean (decision of
    /// the user, 3.11.2026; No. 443).
    /// </summary>
    [Fact]
    public void FindElsewhere_UnknownBinary_ReportedAsNotSearchable()
    {
        var pdf = Page("Konto Geheim 4711");
        var data = new byte[1024];
        new Random(4711).NextBytes(data);
        InNameTree(pdf, "anlage.bin", FileSpec(pdf, "anlage.bin", data));
        var places = Elsewhere(pdf, "Geheim 4711");
        Assert.Contains(Tr.T("Dateianhänge, die nicht durchsucht werden konnten: {0}", "anlage.bin"), places);
        Assert.DoesNotContain(Tr.T("Dateianhänge"), places);
    }

    /// <summary>A file associated with marked content only (/AF /MC0 BDC, the files in /Properties): the question names it (K-02, No. 443).</summary>
    [Fact]
    public void FindElsewhere_AssociatedFileOfMarkedContent_IsReported()
    {
        var pdf = Page("Konto Geheim 4711");
        var spec = FileSpec(pdf, "daten.csv", "Name;Konto\nMax Mustermann;Geheim 4711"u8.ToArray(), "Data");
        pdf.Replace(4, TestPdf.FlateStream("/AF /MC0 BDC BT /F1 12 Tf 72 700 Td (Konto Geheim 4711) Tj ET EMC"u8.ToArray()));
        var page = (Core.Pdf.Objects.PdfDictionary)pdf.Get(5);
        page.Set(Core.Pdf.Objects.PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))),
            ("Properties", TestPdf.Dict(("MC0", new Core.Pdf.Objects.PdfArray([TestPdf.Ref(spec)]))))));
        Assert.Contains(Tr.T("zugeordnete Dateien"), Elsewhere(pdf, "Geheim 4711"));
    }

    /// <summary>The XMP of an annotation holds the name: the question names it (K-02, No. 443).</summary>
    [Fact]
    public void FindElsewhere_XmpOfAnAnnotation_IsReported()
    {
        var pdf = Page("Konto Geheim 4711");
        var xmp = pdf.Add(new Core.Pdf.Objects.PdfStream(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))),
            System.Text.Encoding.UTF8.GetBytes(XmpPacket("Akte Geheim 4711"))));
        var note = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Square")), ("Rect", TestPdf.Box(300, 300, 400, 400)),
            ("Metadata", TestPdf.Ref(xmp))));
        ((Core.Pdf.Objects.PdfDictionary)pdf.Get(5)).Set(Core.Pdf.Objects.PdfName.Get("Annots"), new Core.Pdf.Objects.PdfArray([TestPdf.Ref(note)]));
        Assert.Contains(Tr.T("Metadaten an Objekten"), Elsewhere(pdf, "Geheim 4711"));
    }

    /// <summary>
    /// The document's XMP is read as XML: a name written with entities is found, a tag name is no hit (K-03, No. 443). As raw text the
    /// first was missed and the second a false alarm.
    /// </summary>
    [Fact]
    public void FindElsewhere_XmpAsXml_EntitiesFound_TagNamesNot()
    {
        var pdf = Page("M\u00fcller zahlt");
        var xmp = pdf.Add(new Core.Pdf.Objects.PdfStream(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))),
            System.Text.Encoding.UTF8.GetBytes(XmpPacket("M&#xFC;ller &amp; S&#xF6;hne"))));
        ((Core.Pdf.Objects.PdfDictionary)pdf.Get(1)).Set(Core.Pdf.Objects.PdfName.Get("Metadata"), TestPdf.Ref(xmp));
        Assert.Contains(Tr.T("Dokumenteigenschaften"), Elsewhere(pdf, "M\u00fcller"));

        var other = Page("Konto title");
        var plain = other.Add(new Core.Pdf.Objects.PdfStream(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))),
            System.Text.Encoding.UTF8.GetBytes(XmpPacket("Bericht"))));
        ((Core.Pdf.Objects.PdfDictionary)other.Get(1)).Set(Core.Pdf.Objects.PdfName.Get("Metadata"), TestPdf.Ref(plain));
        Assert.DoesNotContain(Tr.T("Dokumenteigenschaften"), Elsewhere(other, "title"));
    }

    private static string XmpPacket(string title) =>
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
        "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
        $"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
}
