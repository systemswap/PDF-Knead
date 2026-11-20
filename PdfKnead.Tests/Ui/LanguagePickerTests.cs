using Avalonia.Controls;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The language of a document chosen from a list (decided on 5.11.2026): a tag in the list shows its entry, a choice sets the tag, any
/// other tag goes into the box behind „Andere Sprache …“.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class LanguagePickerTests
{
    [Fact]
    public void Picker_ShowsAndSetsTheTag()
    {
        UiSession.Run(() =>
        {
            var picker = new LanguagePicker { Value = "de" };
            var window = new Window { Content = picker, Width = 500, Height = 200 };
            window.Show();
            try
            {
                var box = picker.FindControl<ComboBox>("Box")!;
                var own = picker.FindControl<TextBox>("OwnBox")!;
                Assert.Equal("Deutsch – de", ((DocumentLanguage)box.SelectedItem!).Display);
                Assert.False(own.IsVisible);

                box.SelectedItem = LanguagePicker.Languages.Single(l => l.Language.Code == "en-GB");
                Assert.Equal("en-GB", picker.Value);

                // a tag the list does not have: „Andere Sprache …“ and the tag in the box; typing there sets it
                picker.Value = "sr-Latn";
                Assert.True(((DocumentLanguage)box.SelectedItem!).IsOther);
                Assert.True(own.IsVisible);
                Assert.Equal("sr-Latn", own.Text);
                own.Text = "gsw";
                Assert.Equal("gsw", picker.Value);

                // the case of a tag as written in a file does not matter
                picker.Value = "FR";
                Assert.Equal("fr", ((DocumentLanguage)box.SelectedItem!).Language.Code);
                Assert.False(own.IsVisible);
                picker.Value = "";
                Assert.Null(box.SelectedItem);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }
}
