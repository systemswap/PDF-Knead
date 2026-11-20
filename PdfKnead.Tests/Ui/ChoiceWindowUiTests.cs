using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Localization;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The question window with any number of answers, its own way back and a box to tick (<see cref="ChoiceRequest"/>; docs/FEHLERGEDAECHTNIS.md,
/// No. 619): the questions of „Messen“ show every button completely, the default answer is the rightmost and Enter's; each button gives its
/// number, the way back 0, the box is reported.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class ChoiceWindowUiTests
{
    private static ChoiceRequest EscQuestion => new(Tr.T("Messung abschließen?"),
        Tr.T("Die Messung ist noch nicht abgeschlossen und nicht in der PDF. Abgeschlossen bleibt sie sichtbar, solange „Messen“ an ist; in die PDF kommt sie als Bemaßung, die mit der Datei gespeichert wird."),
        [Tr.T("Abschließen"), Tr.T("Abschließen und in die PDF einfügen"), Tr.T("Verwerfen")], Tr.T("Weiter messen"), Icon: "Icon.Ruler");

    private static ChoiceRequest LeaveQuestion => new(Tr.T("Messungen in die PDF übernehmen?"),
        Tr.T("In „{0}“ sind {1} Messungen nicht in der PDF — ohne Einfügen sind sie fort, sobald „Messen“ aus ist. Als Bemaßungen eingefügt werden sie mit der Datei gespeichert; andere Programme messen dasselbe.", "Grundriss Erdgeschoss Mustermann.pdf", 3),
        [Tr.T("Alle in die PDF einfügen"), Tr.T("Verwerfen")], Tr.T("Zurück zum Messen"), Tr.T("Nicht mehr fragen (wieder einschaltbar im Bereich „Messen“)"), "Icon.Ruler");

    [Fact]
    public void MeasureQuestions_EveryButtonComplete_DefaultRightmost()
    {
        UiSession.Run(() =>
        {
            foreach (var (request, name) in new[] { (EscQuestion, "83-rueckfrage-esc"), (LeaveQuestion, "84-rueckfrage-ausschalten") })
            {
                var window = new ChoiceWindow(request);
                window.Show();
                try
                {
                    var frame = UiSession.Capture(window);
                    if (UiSession.ScreenshotFolder() is { } folder) frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
                    var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b is not CheckBox).ToList();   // a check box is a button too
                    Assert.Equal(request.Answers.Count + 1, buttons.Count);
                    foreach (var button in buttons)
                    {
                        var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X;
                        Assert.True(right <= window.ClientSize.Width - 8, $"„{button.Content}“ endet bei {right:0}, Fenster {window.ClientSize.Width:0}");
                        Assert.True(button.DesiredSize.Width <= button.Bounds.Width + 0.5, $"„{button.Content}“ abgeschnitten");
                    }
                    // the way back leftmost, the default answer rightmost and Enter's
                    var ordered = buttons.OrderBy(b => b.TranslatePoint(default, window)!.Value.X).ToList();
                    Assert.Equal(request.Cancel, ordered[0].Content);
                    Assert.True(ordered[0].IsCancel);
                    Assert.Equal(request.Answers[0], ordered[^1].Content);
                    Assert.True(ordered[^1].IsDefault);
                    Assert.Equal(request.Check is not null, window.GetVisualDescendants().OfType<CheckBox>().Single().IsVisible);
                }
                finally
                {
                    window.Close();
                }
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>Each answer gives its number, the way back 0; the box is reported with either.</summary>
    [Fact]
    public void MeasureQuestions_AnswersAndBox()
    {
        UiSession.Run(async () =>
        {
            var owner = new Window { Width = 800, Height = 600 };
            owner.Show();
            try
            {
                async Task<ChoiceAnswer> Answer(ChoiceRequest request, Func<Button, bool> which, bool tick)
                {
                    var window = new ChoiceWindow(request);
                    var result = window.ShowDialog<ChoiceAnswer>(owner);
                    UiSession.Capture(window);
                    if (tick) window.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
                    window.GetVisualDescendants().OfType<Button>().Where(b => b is not CheckBox).Single(which).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return await result;
                }
                Assert.Equal(new ChoiceAnswer(1, false), await Answer(EscQuestion, b => (string?)b.Content == Tr.T("Abschließen"), false));
                Assert.Equal(new ChoiceAnswer(2, false), await Answer(EscQuestion, b => (string?)b.Content == Tr.T("Abschließen und in die PDF einfügen"), false));
                Assert.Equal(new ChoiceAnswer(3, false), await Answer(EscQuestion, b => (string?)b.Content == Tr.T("Verwerfen"), false));
                Assert.Equal(new ChoiceAnswer(0, false), await Answer(EscQuestion, b => (string?)b.Content == Tr.T("Weiter messen"), false));
                Assert.Equal(new ChoiceAnswer(1, true), await Answer(LeaveQuestion, b => (string?)b.Content == Tr.T("Alle in die PDF einfügen"), true));
                Assert.Equal(new ChoiceAnswer(0, true), await Answer(LeaveQuestion, b => (string?)b.Content == Tr.T("Zurück zum Messen"), true));
                // the older form still gives the number alone
                var older = new ChoiceWindow("Frage", "…", "Erste", "Zweite");
                var number = older.ShowDialog<int>(owner);
                UiSession.Capture(older);
                older.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "Zweite").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, await number);
            }
            finally
            {
                owner.Close();
            }
        });
    }
}
