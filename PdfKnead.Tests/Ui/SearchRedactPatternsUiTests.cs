using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Suchen und schwärzen“ with check digits, dates and a word list (KONZEPT.md, section 5.6): a valid IBAN is ticked, one with a wrong check
/// digit offered unticked; a card number counts only with a valid check digit, never the digits of an IBAN; dates in their usual forms; words
/// from a loaded list are found.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class SearchRedactPatternsUiTests
{
    [Fact]
    public void Patterns_CheckDigits_Dates_WordList()
    {
        var content = string.Join(" ", new[]
        {
            "BT /F1 11 Tf 72 760 Td (Konto DE89 3704 0044 0532 0130 00 gueltig) Tj ET",
            "BT /F1 11 Tf 72 740 Td (Konto DE89 3704 0044 0532 0130 01 vertippt) Tj ET",
            "BT /F1 11 Tf 72 720 Td (Karte 4111 1111 1111 1111 und Bestellung 1234 5678 9012 3456) Tj ET",
            "BT /F1 11 Tf 72 700 Td (Termin 24.12.2026, Frist 2026-01-31, am 3. Maerz 2026, Version 1.2.3) Tj ET",
            "BT /F1 11 Tf 72 680 Td (Projekt Kranich bei Erika Musterfrau) Tj ET",
        });
        var file = TestFiles.Write(FakeRedactionTests.Page(content), "muster.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                var hits = await doc.FindForRedactionAsync([], RedactionPatterns.Iban | RedactionPatterns.Card | RedactionPatterns.Date, false);
                var found = hits.Select(h => (h.Found, h.Doubt)).ToList();
                Assert.Contains(("DE89 3704 0044 0532 0130 00", (string?)null), found);
                Assert.Contains(found, f => f.Found == "DE89 3704 0044 0532 0130 01" && f.Doubt is not null);
                Assert.Contains(found, f => f.Found == "4111 1111 1111 1111");
                Assert.DoesNotContain(found, f => f.Found.Contains("1234 5678"));                            // fails the Luhn check
                Assert.DoesNotContain(found, f => f.Found.StartsWith("3704") || f.Found.StartsWith("89 "));  // no card in an IBAN
                Assert.Contains(found, f => f.Found == "24.12.2026");
                Assert.Contains(found, f => f.Found == "2026-01-31");
                Assert.Contains(found, f => f.Found == "3. Maerz 2026");
                Assert.DoesNotContain(found, f => f.Found.Contains("1.2.3"));

                // in the window: the doubtful hit unticked; words from a loaded list
                var dialog = new SearchRedactWindow((w, p, c) => doc.FindForRedactionAsync(w, p, c));
                dialog.Show();
                Assert.Equal(2, dialog.AddWords(Core.Pdf.Text.WordList.Parse("Projekt Kranich\nErika Musterfrau\nProjekt Kranich")));
                await dialog.SearchAsync();
                Assert.Equal(2, dialog.Choices.Count);
                Assert.All(dialog.Choices, c => Assert.True(c.Chosen));
                dialog.Close();
                var choices = hits.Select(h => new RedactionChoice(h)).ToList();
                Assert.False(choices.Single(c => c.Hit.Found.EndsWith("01")).Chosen);
                Assert.Contains("Prüfziffer", choices.Single(c => c.Hit.Found.EndsWith("01")).Title);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
