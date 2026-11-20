using Avalonia.Controls;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Suchen und schwärzen“ with persons, companies and addresses (KONZEPT.md, section 5.23): every part of an address is a hit of its own with
/// its kind beside it; only the parts ticked are looked for; a suggestion of the recognition is offered unticked with its reason; „Adresse“
/// ticks all parts and shows the third state when only some are ticked. Only invented names.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class SearchRedactEntitiesUiTests
{
    [Fact]
    public void PersonsAndAddress_EachPartItsOwnHit()
    {
        var content = string.Join(" ", new[]
        {
            "BT /F1 11 Tf 72 760 Td (Herrn Max Mustermann) Tj ET",
            "BT /F1 11 Tf 72 745 Td (Musterweg 12) Tj ET",
            "BT /F1 11 Tf 72 730 Td (12345 Musterstadt) Tj ET",
            "BT /F1 11 Tf 72 700 Td (Die Mustermann-Milch GmbH liefert. Gestern hat Thomas angerufen.) Tj ET",
        });
        var file = TestFiles.Write(FakeRedactionTests.Page(content), "anschrift.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                var hits = await doc.FindForRedactionAsync([], RedactionPatterns.Person | RedactionPatterns.Organisation | RedactionPatterns.Address, false);
                var found = hits.Select(h => (h.Found, h.Entity, Sure: h.Doubt is null)).ToList();
                Assert.Contains(("Herrn Max Mustermann", Core.Pdf.Text.Entities.EntityKind.Person, true), found);
                Assert.Contains(("Musterweg", Core.Pdf.Text.Entities.EntityKind.Street, true), found);
                Assert.Contains(("12", Core.Pdf.Text.Entities.EntityKind.HouseNumber, true), found);
                Assert.Contains(("12345", Core.Pdf.Text.Entities.EntityKind.PostalCode, true), found);
                Assert.Contains(("Musterstadt", Core.Pdf.Text.Entities.EntityKind.Place, true), found);
                Assert.Contains(("Mustermann-Milch GmbH", Core.Pdf.Text.Entities.EntityKind.Organisation, true), found);
                Assert.Contains(("Thomas", Core.Pdf.Text.Entities.EntityKind.Person, false), found);
                Assert.Equal(Core.Pdf.Editing.RedactionFinds.Address, hits.Single(h => h.Found == "12345").Kind);   // its code per kind

                // only the parts ticked: the house number alone
                var numbers = await doc.FindForRedactionAsync([], RedactionPatterns.HouseNumber, false);
                Assert.Equal(["12"], numbers.Select(h => h.Found));

                // in the list: the kind beside the hit, a suggestion unticked with its reason
                var choices = hits.Select(h => new RedactionChoice(h)).ToList();
                var thomas = choices.Single(c => c.Hit.Found == "Thomas");
                Assert.False(thomas.Chosen);
                Assert.Contains("(Person)", thomas.Title);
                Assert.Contains("Liste", thomas.Title);
                Assert.True(choices.Single(c => c.Hit.Found == "Musterweg").Chosen);
                Assert.Contains("(Straße)", choices.Single(c => c.Hit.Found == "Musterweg").Title);

                // the window: „Adresse“ ticks all parts; one part off — the third state
                var dialog = new SearchRedactWindow((w, p, c) => doc.FindForRedactionAsync(w, p, c));
                dialog.Show();
                dialog.AddressBox.IsChecked = true;
                Assert.Equal(RedactionPatterns.Address, dialog.EntityPatterns);
                dialog.StreetBox.IsChecked = false;
                Assert.Null(dialog.AddressBox.IsChecked);
                Assert.Equal(RedactionPatterns.Address & ~RedactionPatterns.Street, dialog.EntityPatterns);
                dialog.AddressBox.IsChecked = false;
                Assert.Equal(RedactionPatterns.None, dialog.EntityPatterns);
                dialog.PersonBox.IsChecked = true;
                await dialog.SearchAsync();
                Assert.Contains(dialog.Choices, c => c.Hit.Found == "Herrn Max Mustermann" && c.Chosen);
                Assert.DoesNotContain(dialog.Choices, c => c.Hit.Entity == Core.Pdf.Text.Entities.EntityKind.Street);
                dialog.Close();
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The head of the window (decided on 13.11.2026): the introduction in one line, below it the note that the search is a help without
    /// guarantee, in the colour of warnings and also in one line — at the window's own width, in German and in English.
    /// </summary>
    [Fact]
    public void Head_IntroductionAndNoGuarantee_EachOneLine()
    {
        UiSession.Run(() =>
        {
            foreach (var language in new[] { Language.German, PdfKnead.Core.Localization.English.Language })
            {
                Tr.Use(language);
                try
                {
                    var dialog = new SearchRedactWindow((_, _, _) => Task.FromResult(new List<RedactionHit>()));
                    dialog.Show();
                    dialog.UpdateLayout();
                    Assert.Single(dialog.IntroText.TextLayout.TextLines);
                    Assert.Single(dialog.NoGuaranteeText.TextLayout.TextLines);
                    Assert.Contains(language.IsGerman ? "ohne Gewähr" : "without guarantee", dialog.NoGuaranteeText.Text);
                    Assert.True(dialog.TryFindResource("WarnBrush", dialog.ActualThemeVariant, out var warn));
                    Assert.Equal(warn, dialog.NoGuaranteeText.Foreground);
                    dialog.Close();
                }
                finally
                {
                    Tr.Use(Language.German);
                }
            }
        });
    }
}
