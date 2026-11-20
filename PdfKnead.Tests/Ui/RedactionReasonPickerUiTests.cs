using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The block „Schwärzungsgrund“ (wish of 4.11.2026, docs/OFFENE-PUNKTE.md, „Schwärzungscodes — Ablauf festgelegt“): the codes of the chosen
/// country with their meaning shown, an own reason that replaces the code (codes greyed), „Code im Balken“, the warning where the code does
/// not fit into a bar and where an own reason names what is redacted, a choice shown from outside, and a code per kind of hit.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class RedactionReasonPickerUiTests
{
    private static T Named<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [Fact]
    public void CodeWithItsMeaning_OwnReasonReplacesIt_InBar_Warnings()
    {
        UiSession.Run(() =>
        {
            var picker = new RedactionReasonPicker();
            var window = new Window { Content = picker, Width = 480, Height = 520 };
            window.Show();
            try
            {
                UiSession.Capture(window);
                var sets = Named<ComboBox>(picker, "SetBox");
                var codes = Named<ComboBox>(picker, "CodeBox");
                var meaning = Named<TextBlock>(picker, "MeaningText");
                sets.SelectedItem = picker.Sets.Single(s => s.Id == "de");
                Assert.Null(picker.Choice);   // „(kein Grund)“ at first
                Assert.Equal(Tr.T("(kein Grund)"), codes.SelectedItem!.ToString());
                // a code: chosen with its basis, its meaning shown beneath
                codes.SelectedItem = codes.Items.OfType<RedactionReasonCode>().Single(c => c.Code == "Art. 9 DSGVO");
                Assert.Equal(("Art. 9 DSGVO", "Datenschutz-Grundverordnung (EU) 2016/679, Art. 9", true),
                    (picker.Choice!.Code, picker.Choice.Basis, picker.Choice.InBar));
                Assert.True(meaning.IsVisible);
                Assert.Contains("Gesundheit", meaning.Text);
                // not into the bar
                Named<CheckBox>(picker, "InBarBox").IsChecked = false;
                Assert.False(picker.Choice!.InBar);
                Named<CheckBox>(picker, "InBarBox").IsChecked = true;
                // where the code does not fit: warned about, counted
                picker.Bars = [new PdfRectangle(100, 700, 300, 714), new PdfRectangle(100, 600, 108, 604), new PdfRectangle(100, 500, 106, 503)];
                var fit = Named<TextBlock>(picker, "FitWarning");
                Assert.True(fit.IsVisible);
                Assert.Equal(Tr.T("Passt in {0} von {1} Balken nicht — dort wird er weggelassen, im Schwärzungsprotokoll steht er.", 2, 3), fit.Text);
                picker.Bars = [new PdfRectangle(100, 700, 300, 714)];
                Assert.False(fit.IsVisible);
                // an own reason replaces the code: the codes greyed, no meaning; naming what is redacted is warned about
                picker.Redacted = ["Max Mustermann", "DE89 3704"];
                var own = Named<TextBox>(picker, "OwnBox");
                own.Text = "Akte Mustermann";
                Assert.Equal(new ReasonChoice("Akte Mustermann"), picker.Choice);
                Assert.Equal(0.5, codes.Opacity);
                Assert.False(meaning.IsVisible);
                Assert.True(Named<TextBlock>(picker, "RevealWarning").IsVisible);
                own.Text = "";
                Assert.Equal("Art. 9 DSGVO", picker.Choice!.Code);
                Assert.False(Named<TextBlock>(picker, "RevealWarning").IsVisible);
                // shown from outside: a code of another country switches the country; an unknown code shows as own reason
                picker.Choice = new ReasonChoice("…", "§ 6 Z 7c IFG", "…");
                Assert.Equal("at", ((RedactionCodeSet)sets.SelectedItem!).Id);
                Assert.Equal("§ 6 Z 7c IFG", ((RedactionReasonCode)codes.SelectedItem!).Code);
                picker.Choice = new ReasonChoice("Schutz Dritter");
                Assert.Equal("Schutz Dritter", own.Text);
                picker.Choice = null;
                Assert.Equal("", own.Text);
                Assert.Equal(Tr.T("(kein Grund)"), codes.SelectedItem!.ToString());
                // a code per kind of hit: only offered where asked for, off until ticked
                Assert.False(Named<CheckBox>(picker, "AutoBox").IsVisible);
                picker.OffersAuto = true;
                Assert.True(Named<CheckBox>(picker, "AutoBox").IsVisible);
                Assert.Null(picker.Auto);
                Named<CheckBox>(picker, "AutoBox").IsChecked = true;
                Assert.Equal("at", picker.Auto!.Id);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// „Code je Fundart“: a hit of a pattern gets its kind's code (an IBAN in Germany „Art. 6 DSGVO“), a word searched for the code chosen
    /// for all; without it every hit gets the code chosen.
    /// </summary>
    [Fact]
    public void CodePerKindOfHit_PatternsTheirOwn_WordsTheChosenOne()
    {
        var de = RedactionCodes.Load(Path.Combine(Path.GetTempPath(), "pdfknead-keine-codes")).Single(s => s.Id == "de");
        var chosen = ReasonChoice.Of(de.Codes.Single(c => c.Code == "§ 5 IFG"), inBar: false);
        var iban = new RedactionHit(0, new TextRange(0, 4), "DE89", "") { Kind = RedactionFinds.Iban };
        var word = new RedactionHit(0, new TextRange(5, 4), "Name", "");
        var auto = new MainViewModel.RedactionPick([iban, word], chosen, de);
        Assert.Equal(("Art. 6 DSGVO", false), (auto.For(iban)!.Code, auto.For(iban)!.InBar));
        Assert.Equal("§ 5 IFG", auto.For(word)!.Code);
        var plain = new MainViewModel.RedactionPick([iban, word], chosen);
        Assert.Equal("§ 5 IFG", plain.For(iban)!.Code);
        Assert.Null(new MainViewModel.RedactionPick([iban], null).For(word));
    }

    /// <summary>
    /// „Suchen und schwärzen“ a second time: the hits marked the first time are offered as „schon markiert“, not ticked, cannot be ticked,
    /// and are not marked again — they were marked twice (report of 4.11.2026); a hit not yet marked is marked as usual.
    /// </summary>
    [Fact]
    public void SearchRedactTwice_MarkedHitsNotMarkedAgain()
    {
        var pdf = TestFiles.Write(Pdf.RedactionTests.TextPage("Kunde Max Mustermann", "IBAN DE89 3704 0044 0532 0130 00", "Tel. +49 30 1234567"),
            "zweimal-suchen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                SearchRedactWindow? second = null;
                async Task<MainViewModel.RedactionPick?> Search(Func<IReadOnlyList<string>, RedactionPatterns, bool, Task<List<RedactionHit>>> find, bool phone)
                {
                    var dialog = second = new SearchRedactWindow(find, barsOf: hits => doc.BarsOfAsync(hits));
                    dialog.Show();
                    Named<TextBox>(dialog, "WordsBox").Text = "Max Mustermann";
                    Named<CheckBox>(dialog, "IbanBox").IsChecked = true;
                    Named<CheckBox>(dialog, "PhoneBox").IsChecked = phone;
                    await dialog.SearchAsync();
                    var pick = dialog.Chosen();
                    dialog.Close();
                    return pick;
                }
                vm.PickRedactionHits = find => Search(find, phone: false);
                await vm.SearchRedactCommand.ExecuteAsync(null);
                var first = doc.ElementsOn(0).OfType<RedactionElement>().Count();
                Assert.Equal(2, first);
                // again, now with phone numbers: name and IBAN already marked, only the phone number is new
                vm.PickRedactionHits = find => Search(find, phone: true);
                await vm.SearchRedactCommand.ExecuteAsync(null);
                var choices = second!.Choices;
                Assert.Equal(2, choices.Count(c => c.Hit.Marked));
                Assert.All(choices.Where(c => c.Hit.Marked), c => Assert.False(c.Chosen || c.CanChoose));
                Assert.Contains(choices, c => c.Title.EndsWith(Tr.T("schon markiert")));
                Assert.True(Assert.Single(choices, c => !c.Hit.Marked).Chosen);
                Assert.Equal(first + 1, doc.ElementsOn(0).OfType<RedactionElement>().Count());
                // a third time without anything new: nothing marked, nothing to apply
                vm.PickRedactionHits = find => Search(find, phone: true);
                await vm.SearchRedactCommand.ExecuteAsync(null);
                Assert.All(second.Choices, c => Assert.True(c.Hit.Marked));
                Assert.False(Named<Button>(second, "ApplyButton").IsEnabled);
                Assert.Equal(first + 1, doc.ElementsOn(0).OfType<RedactionElement>().Count());
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// An IBAN in which the phone pattern also finds a run of digits is still an IBAN — the merged hit had no kind and got no code (report
    /// of 4.11.2026 with an offer: none of its six IBANs had one); a phone number stays one, an e-mail address one.
    /// </summary>
    [Fact]
    public void IbanWithDigitsThePhonePatternFinds_KeepsItsKind()
    {
        var pdf = TestFiles.Write(Pdf.RedactionTests.TextPage("IBAN DE89 3704 0044 0532 0130 00", "Tel. +49 30 1234567", "max.mustermann@example.org"),
            "iban-und-telefon.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([pdf]);
            var hits = await vm.SelectedDocument!.FindForRedactionAsync([],
                RedactionPatterns.Email | RedactionPatterns.Phone | RedactionPatterns.Iban | RedactionPatterns.Card | RedactionPatterns.Date, false);
            Assert.Equal(RedactionFinds.Iban, Assert.Single(hits, h => h.Found.StartsWith("DE89")).Kind);
            Assert.Equal(RedactionFinds.Phone, Assert.Single(hits, h => h.Found.Contains("1234567")).Kind);
            Assert.Equal(RedactionFinds.Email, Assert.Single(hits, h => h.Found.Contains('@')).Kind);
            vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
        });
        // the rule: the longest part decides; at the same length a pattern before a word searched for, the more specific pattern first
        var iban = new TextRange(0, 27);
        var digits = new TextRange(10, 14);
        var word = new TextRange(0, 27);
        Assert.Equal(RedactionFinds.Iban, DocumentViewModel.KindOf([iban, digits], new Dictionary<TextRange, RedactionFinds> { [iban] = RedactionFinds.Iban, [digits] = RedactionFinds.Phone }));
        Assert.Equal(RedactionFinds.None, DocumentViewModel.KindOf([new TextRange(0, 30), digits], new Dictionary<TextRange, RedactionFinds> { [digits] = RedactionFinds.Phone }));
        Assert.Equal(RedactionFinds.Iban, DocumentViewModel.KindOf([word, iban], new Dictionary<TextRange, RedactionFinds> { [iban] = RedactionFinds.Iban }));
    }

    /// <summary>
    /// „Einstellungen“ → „Schwärzungscodes“: the set offered first is remembered; a built-in one is copied for editing and then replaces it;
    /// a file of a colleague is added; the chosen set is exported; the own version is put aside as .bak (nothing lost).
    /// </summary>
    [Fact]
    public void Settings_ChooseEditAddExportPutAside()
    {
        UiSession.Run(() =>
        {
            var settings = new SettingsViewModel();
            var folder = RedactionCodes.Folder;
            settings.SelectedCodeSet = settings.CodeSets.Single(c => c.Id == "at");
            Assert.Equal("at", Services.UserSettings.Current.RedactionCodeSet);
            Assert.False(settings.CanRemoveOwnCodes);
            // edited: copied into the own folder, then it is the own version
            var path = settings.CodesFileToEdit();
            Assert.Equal(Path.Combine(folder, "at.txt"), path);
            File.WriteAllText(path!, File.ReadAllText(path!).Replace("Land\tÖsterreich", "Land\tÖsterreich (Amt Y)"));
            settings.ReloadCodeSets();
            Assert.Equal(("Österreich (Amt Y)", true), (settings.SelectedCodeSet!.Country, settings.SelectedCodeSet.Own));
            Assert.True(settings.CanRemoveOwnCodes);
            // exported as it is now
            var exported = Path.Combine(Path.GetTempPath(), "pdfknead-export-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                settings.ExportCodes(exported);
                Assert.Contains("Amt Y", File.ReadAllText(exported));
                // a colleague's file added as a set of its own; one without a code refused
                var colleague = Path.Combine(Path.GetDirectoryName(exported)!, "firma-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
                File.WriteAllText(colleague, "Land\tFirma\nK1\tIntern\tNur für den Dienstgebrauch\tHausordnung\n");
                settings.ImportCodes(colleague);
                Assert.Contains(settings.CodeSets, c => c.Country == "Firma" && c.Own);
                File.WriteAllText(colleague, "# nichts\n");
                settings.ImportCodes(colleague);
                Assert.Contains(Tr.T("Sie enthält keinen Code."), settings.CodesMessage);
                File.Delete(colleague);
            }
            finally
            {
                File.Delete(exported);
            }
            // put aside: renamed, the built-in codes apply again
            settings.RemoveOwnCodes();
            Assert.True(File.Exists(Path.Combine(folder, "at.txt.bak")));
            Assert.False(File.Exists(Path.Combine(folder, "at.txt")));
            Assert.Equal(("Österreich", false), (settings.SelectedCodeSet!.Country, settings.SelectedCodeSet.Own));
        });
    }

    /// <summary>The panel of a selected mark: the block shows its code, a code chosen there goes onto the mark (one undo step).</summary>
    [Fact]
    public void Panel_ShowsTheMarksCode_AndSetsIt()
    {
        var pdf = TestFiles.Write(Pdf.RedactionTests.TextPage("Kunde Max Mustermann zahlt"), "grund-im-feld.pdf");
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
                doc.AddElement(0, new ReasonChoice("…", "Art. 4(1)(b)", "Verordnung (EG) Nr. 1049/2001, Art. 4 Abs. 1 Buchst. b").On(
                    RedactionElement.FromPageAreas(text.RedactionAreas(Assert.Single(text.Find("Mustermann", matchCase: true))))), "Test");
                doc.MarkPicked = true;
                UiSession.Capture(window);
                var picker = window.GetVisualDescendants().OfType<ElementPropertiesPanel>().Single().GetVisualDescendants().OfType<RedactionReasonPicker>().Single();
                Assert.Equal("eu", ((RedactionCodeSet)Named<ComboBox>(picker, "SetBox").SelectedItem!).Id);
                Assert.Equal("Art. 4(1)(b)", ((RedactionReasonCode)Named<ComboBox>(picker, "CodeBox").SelectedItem!).Code);
                Assert.Single(picker.Bars!);
                var codes = Named<ComboBox>(picker, "CodeBox");
                codes.SelectedItem = codes.Items.OfType<RedactionReasonCode>().Single(c => c.Code == "Art. 4(2) Anstr. 1");
                var mark = Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal("Art. 4(2) Anstr. 1", mark.ReasonCode);
                Assert.StartsWith("Geschäftliche Interessen", mark.Reason);
                vm.UndoCommand.Execute(null);
                Assert.Equal("Art. 4(1)(b)", Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0))).ReasonCode);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The rest of a longer number is no phone number: an offer number „2026-05015352“ and a file number „12/0471123456“ are not found, a phone
    /// number beside them is (report of 5.11.2026 with an offer: the part after „2026-“ was marked as a phone number;
    /// docs/FEHLERGEDAECHTNIS.md, No. 531).
    /// </summary>
    [Fact]
    public void NumberAfterAHyphen_NoPhoneNumber()
    {
        var pdf = TestFiles.Write(Pdf.RedactionTests.TextPage("Angebot 2026-05015352", "Akte 12/0471123456", "Tel. 0711 123456-78"),
            "angebotsnummer.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([pdf]);
            var hits = await vm.SelectedDocument!.FindForRedactionAsync([], RedactionPatterns.Phone, false);
            Assert.Equal("0711 123456-78", Assert.Single(hits).Found);
            vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
        });
    }
}
