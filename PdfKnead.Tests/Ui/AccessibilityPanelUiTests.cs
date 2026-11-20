using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Barrierefreiheit“ as an area on the right (docs/ABLAUFPLAN.md, 6.7 steps 6, 7, 10; docs/FEHLERGEDAECHTNIS.md, No. 667): the check of the file
/// as it would be saved, the headline in words, the findings in groups; title, language and „Titel statt Dateiname“ repaired from the area,
/// then checked again — and written when saving. One area at a time on the right.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class AccessibilityPanelUiTests
{
    private static byte[] File() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
        [6] = ("", "BT /F1 12 Tf 72 780 Td (Ein kurzer deutscher Text mit einigen Wörtern.) Tj ET"),
    });

    /// <summary>A file whose XMP claims PDF/UA-1 under a foreign prefix (ISO 14289-1, 5: the prefix is pdfuaid).</summary>
    private static byte[] ClaimingFile() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/Metadata 7 0 R/Lang(de)>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>>>",
        [6] = ("", "0 0 m 100 100 l S"),
        [7] = ("/Type/Metadata/Subtype/XML", "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                                             "<rdf:Description rdf:about=\"\" xmlns:ua=\"http://www.aiim.org/pdfua/ns/id/\"><ua:part>1</ua:part></rdf:Description>" +
                                             "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>"),
    });

    /// <summary>A faulty identification: „Kennung entfernen“ takes it out of the copy checked and of the saved file.</summary>
    [Fact]
    public void FaultyClaim_Removed_CheckedAgainAndSaved()
    {
        var pdf = TestFiles.Write(ClaimingFile(), "barrierefrei-kennung.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ChosenAccessibilityProfile = DocumentViewModel.AccessibilityProfiles.Single(p => p.Profile == AccessibilityProfile.PdfUa1);
                await vm.ToggleAccessibilityCommand.ExecuteAsync(null);
                var part = doc.AccessibilityGroups.SelectMany(g => g.Rows).Single(r => r.Finding.Id == "5-3");
                Assert.Equal(AccessibilityRepair.RemoveClaim, part.Finding.Repair);
                await doc.RepairAccessibilityCommand.ExecuteAsync(part);
                Assert.True(doc.RemoveClaimOnSave);
                Assert.True(doc.IsModified);
                Assert.DoesNotContain("5-3", Ids(doc));
                Assert.Contains("5-1", Ids(doc));   // no identification now — PDF-Knead does not write one by itself
                var missing = doc.AccessibilityGroups.SelectMany(g => g.Rows).Single(r => r.Finding.Id == "5-1");
                Assert.False(missing.HasRepair);
                Assert.True(missing.HasGuidance);

                var copy = Path.Combine(Path.GetDirectoryName(pdf)!, "barrierefrei-kennung-kopie.pdf");
                await doc.SaveAsync(copy, doc.SaveOptions("1.7"));
                Assert.False(doc.RemoveClaimOnSave);
                using var saved = PdfDocument.Open(System.IO.File.ReadAllBytes(copy));
                Assert.Null(AccessibilityClaims.Read(saved.GetStreamData((Core.Pdf.Objects.PdfStream)saved.Catalog!.Get(Core.Pdf.Objects.PdfName.Metadata)!))?.PdfUaPart);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The helps M1, M2, M6 on the page (No. 670): switched on at their question, one at a time, worked out in the background; closing the area
    /// ends them.
    /// </summary>
    [Fact]
    public void Helps_ReadingOrderFiguresArtifacts_OnThePage()
    {
        var pdf = TestFiles.Write(ReadingOrderTests.Bytes(), "barrierefrei-hilfen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                await vm.ToggleAccessibilityCommand.ExecuteAsync(null);
                var rows = doc.AccessibilityQuestionRows;
                Assert.Equal(["M1", "M2", "M6"], rows.Where(r => r.HasHelp).Select(r => r.Question.Id));
                async Task<PageReadingOrder> Shown()
                {
                    for (var i = 0; i < 200 && doc.AccessibilityHelpFor(0) is null; i++)
                    {
                        await Task.Delay(10);
                        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    }
                    return doc.AccessibilityHelpFor(0)!;
                }
                void Shot(string name)
                {
                    var bitmap = UiSession.Capture(window);
                    if (UiSession.ScreenshotFolder() is { } folder) bitmap.Save(Path.Combine(folder, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }

                doc.ToggleAccessibilityHelpCommand.Execute(rows[0]);
                Assert.Equal(AccessibilityHelp.ReadingOrder, doc.ShownAccessibilityHelp);
                Assert.True(rows[0].HelpShown);
                Assert.Contains("ausblenden", rows[0].HelpLabel);
                Assert.Equal(3, (await Shown()).Order.Count);
                Shot("barrierefrei-m1-reihenfolge.png");

                // one at a time: M2 takes the place of M1
                doc.ToggleAccessibilityHelpCommand.Execute(rows[1]);
                Assert.Equal(AccessibilityHelp.Figures, doc.ShownAccessibilityHelp);
                Assert.False(rows[0].HelpShown);
                Assert.Equal("Ein Quadrat", (await Shown()).Order.Single(b => b.Type == "Figure").Alt);
                Shot("barrierefrei-m2-abbildungen.png");

                doc.ToggleAccessibilityHelpCommand.Execute(rows[5]);
                Assert.Single((await Shown()).Artifacts);
                Shot("barrierefrei-m6-artefakte.png");
                // the same button again hides it
                doc.ToggleAccessibilityHelpCommand.Execute(rows[5]);
                Assert.Equal(AccessibilityHelp.None, doc.ShownAccessibilityHelp);
                Assert.Null(doc.AccessibilityHelpFor(0));

                doc.ToggleAccessibilityHelpCommand.Execute(rows[0]);
                await vm.ToggleAccessibilityCommand.ExecuteAsync(null);   // closing the area ends it
                Assert.Equal(AccessibilityHelp.None, doc.ShownAccessibilityHelp);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    private static List<string> Ids(DocumentViewModel doc) => [.. doc.AccessibilityGroups.SelectMany(g => g.Rows).Select(r => r.Finding.Id)];

    [Fact]
    public void Check_RepairTitleLanguageDisplay_CheckedAgainAndSaved()
    {
        var pdf = TestFiles.Write(File(), "barrierefrei-bereich.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                // the default profile says hints only; chosen: PDF/UA-1
                Assert.Equal(AccessibilityProfile.HintsOnly, doc.ChosenAccessibilityProfile.Profile);
                await vm.ToggleAccessibilityCommand.ExecuteAsync(null);
                Assert.True(doc.IsAccessibilityOpen);
                Assert.True(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "AccessibilityHost").IsVisible);
                Assert.All(doc.AccessibilityGroups.SelectMany(g => g.Rows), r => Assert.Equal(AccessibilitySeverity.Hint, r.Finding.Severity));
                doc.ChosenAccessibilityProfile = DocumentViewModel.AccessibilityProfiles.Single(p => p.Profile == AccessibilityProfile.PdfUa1);
                await doc.WhenIdle();
                await doc.CheckAccessibilityAsync();
                var shot = UiSession.Capture(window);
                if (UiSession.ScreenshotFolder() is { } folder) shot.Save(Path.Combine(folder, "barrierefrei-bereich.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                Assert.StartsWith("Nicht barrierefrei nach PDF/UA-1", doc.AccessibilityHeadline);
                Assert.StartsWith("Muss behoben werden", doc.AccessibilityGroups[0].Title);
                Assert.Contains("7.1-9", Ids(doc));   // no title
                Assert.Contains("7.1-10", Ids(doc));   // title not shown
                Assert.Contains("ISO 14289-1, 7.1-9 · Matterhorn 06-003", doc.AccessibilityGroups.SelectMany(g => g.Rows).Single(r => r.Finding.Id == "7.1-9").Rule);

                // a repair without a title typed asks for one
                var title = doc.AccessibilityGroups.SelectMany(g => g.Rows).Single(r => r.Finding.Id == "7.1-9");
                Assert.True(title.HasRepair);
                doc.AccessibilityTitle = "";
                await doc.RepairAccessibilityCommand.ExecuteAsync(title);
                Assert.Contains("Titel", doc.AccessibilityProblem);
                Assert.Contains("7.1-9", Ids(doc));
                doc.AccessibilityTitle = "Mustermappe";
                await doc.RepairAccessibilityCommand.ExecuteAsync(title);
                Assert.DoesNotContain("7.1-9", Ids(doc));
                Assert.Equal("Mustermappe", doc.DescriptionOnSave?.Title);
                Assert.True(doc.IsModified);

                await doc.RepairAccessibilityCommand.ExecuteAsync(doc.AccessibilityGroups.SelectMany(g => g.Rows).Single(r => r.Finding.Id == "7.1-10"));
                Assert.DoesNotContain("7.1-10", Ids(doc));

                // the language: proposed from the text, typed as a name it becomes the tag
                if (doc.AccessibilityGroups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Finding.Repair == AccessibilityRepair.Language) is { } language)
                {
                    doc.AccessibilityLanguage = "Deutsch";
                    await doc.RepairAccessibilityCommand.ExecuteAsync(language);
                    Assert.Equal("de", doc.DescriptionOnSave?.Language);
                    Assert.Equal("de", doc.AccessibilityLanguage);
                    Assert.DoesNotContain(doc.AccessibilityGroups.SelectMany(g => g.Rows), r => r.Finding.Repair == AccessibilityRepair.Language);
                }
                UiSession.Capture(window);

                // the questions: answers move the headline; a change of the document lets them lapse, saving does not
                var rows = doc.AccessibilityQuestionRows;
                Assert.Equal(12, rows.Count);
                Assert.Contains("12 von 12 offen", doc.AccessibilityQuestionsTitle);
                rows[0].IsYes = true;
                rows[1].IsNo = true;
                Assert.Contains("10 von 12 offen", doc.AccessibilityQuestionsTitle);
                Assert.StartsWith("Nicht barrierefrei", doc.AccessibilityHeadline);
                Assert.True(window.GetVisualDescendants().OfType<RadioButton>().Count() >= 24);
                if (UiSession.ScreenshotFolder() is { } questionsFolder)
                {
                    var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "FindingsScroll");
                    scroller.ScrollToEnd();
                    UiSession.Capture(window);
                    UiSession.Capture(window).Save(Path.Combine(questionsFolder, "barrierefrei-fragen.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }

                // the report for the record, through the button's command
                var reportPath = Path.Combine(Path.GetDirectoryName(pdf)!, "barrierefrei-bericht.html");
                vm.PickReportSavePath = (_, name, _) =>
                {
                    Assert.EndsWith(".html", name);
                    return Task.FromResult<string?>(reportPath);
                };
                Assert.True(doc.HasAccessibilityReport);
                await vm.SaveAccessibilityReportCommand.ExecuteAsync(null);
                var html = System.IO.File.ReadAllText(reportPath);
                Assert.Contains(System.Net.WebUtility.HtmlEncode(doc.AccessibilityHeadline), html);
                Assert.Contains("barrierefrei-bereich.pdf", html);

                Assert.Contains("M2 Beschreiben die Ersatztexte die Abbildungen treffend? <strong>Nein</strong>", html);

                // saved: the file has what the area repaired; the answers stay (the same content)
                var copy = Path.Combine(Path.GetDirectoryName(pdf)!, "barrierefrei-bereich-kopie.pdf");
                await doc.SaveAsync(copy, doc.SaveOptions("1.7"));
                Assert.Equal(AccessibilityAnswer.Yes, rows[0].Answer);
                Assert.Equal("", doc.AccessibilityAnswersLapsed);
                // a change of the document: they lapse, and the area says so
                doc.AccessibilityTitle = "Mustermappe 2";
                await doc.RepairAccessibilityCommand.ExecuteAsync(new AccessibilityRow(new AccessibilityFinding("7.1-9", null, AccessibilitySeverity.Error, "", null, AccessibilityRepair.Title), 1));
                Assert.All(rows, r => Assert.Equal(AccessibilityAnswer.Open, r.Answer));
                Assert.NotEqual("", doc.AccessibilityAnswersLapsed);
                using (var saved = PdfDocument.Open(System.IO.File.ReadAllBytes(copy)))
                {
                    Assert.Equal("Mustermappe", saved.Info.Title);
                    var ids = AccessibilityCheck.Check(saved, AccessibilityProfile.PdfUa1).Findings.Select(f => f.Id).ToList();
                    Assert.DoesNotContain("7.1-9", ids);
                    Assert.DoesNotContain("7.1-10", ids);
                }

                // the area stays open through saving; the ribbon button closes and opens it; one area at a time: the notes take its place
                Assert.True(doc.IsAccessibilityOpen);
                await vm.ToggleAccessibilityCommand.ExecuteAsync(null);
                Assert.False(doc.IsAccessibilityOpen);
                await vm.ToggleAccessibilityCommand.ExecuteAsync(null);
                Assert.True(doc.IsAccessibilityOpen);
                vm.ToggleNotesCommand.Execute(null);
                Assert.True(doc.IsNotesOpen);
                Assert.False(doc.IsAccessibilityOpen);
                await vm.ToggleAccessibilityCommand.ExecuteAsync(null);
                Assert.False(doc.IsNotesOpen);
                Assert.True(doc.IsAccessibilityOpen);
                await vm.ToggleAccessibilityCommand.ExecuteAsync(null);
                Assert.False(doc.IsAccessibilityOpen);
                Assert.Empty(doc.AccessibilityGroups);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
