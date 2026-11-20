using System.Text.RegularExpressions;
using PdfKnead.Services;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>The window „Über PDF-Knead“: the own license first, the overview, and every license text the overview names.</summary>
[Collection(nameof(UiCollection))]
public sealed class AboutTests
{
    [Fact]
    public void LicenseTexts_OwnFirst_EveryNamedTextBuiltIn()
    {
        var entries = LicenseTexts.All();
        Assert.Equal(Tr.T("Lizenz von PDF-Knead"), entries[0].Title);
        Assert.NotNull(entries[0].Resource);   // the file LICENSE is built in
        Assert.StartsWith("PDF-Knead-Lizenz", LicenseTexts.Text(entries[0]));
        var notices = LicenseTexts.Text(entries[1]);
        Assert.Contains("Fremde Bestandteile", notices);

        // every „licenses/…“ of the overview (with * for the variants of the runtime notices) is in the list, with content
        var titles = entries.Skip(2).Select(e => e.Title).ToList();
        var named = Regex.Matches(notices, @"licenses/([A-Za-z0-9.*\-]+)\.txt").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(named);
        foreach (var name in named)
        {
            var pattern = new Regex("^" + Regex.Escape(name).Replace(@"\*", ".+") + "$");
            Assert.True(titles.Any(pattern.IsMatch), $"fehlt im Fenster: {name}");
        }
        Assert.All(entries.Skip(1), e => Assert.False(string.IsNullOrWhiteSpace(LicenseTexts.Text(e)), e.Title));
    }

    [Fact]
    public void Window_ShowsTheSelectedText()
    {
        UiSession.Run(() =>
        {
            var model = new AboutViewModel();
            var window = new AboutWindow(model);
            window.Show();
            try
            {
                Assert.StartsWith("PDF-Knead ", model.Title);
                Assert.Equal(LicenseTexts.Text(model.Entries[0]), model.Text);
                MainWindowTests.Shoot(window, "05e-ueber");
                model.Selected = model.Entries.First(e => e.Title.StartsWith("Liberation", StringComparison.Ordinal));
                Assert.Contains("SIL OPEN FONT LICENSE", model.Text, StringComparison.OrdinalIgnoreCase);
                MainWindowTests.Shoot(window, "05f-ueber-lizenz");
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }
}
