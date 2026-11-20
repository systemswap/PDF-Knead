using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// File → Export → „Als PDF/A speichern …“ (KONZEPT.md, section 5.21): the window checks the copy; a conforming one is saved under a new name
/// and the tab keeps the opened file, one that does not conform is not saved and the window names the reasons with the page.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class PdfAUiTests
{
    private static void WaitUntil(Window window, Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            UiSession.Capture(window);
            Thread.Sleep(20);
        }
        Assert.True(condition(), "Zeitüberschreitung");
    }

    private static void Press(Window window, Button button)
    {
        var at = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
    }

    /// <summary>Shapes and invisible text only: nothing that needs an embedded font.</summary>
    private static byte[] Shapes() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
        [6] = ("", "0 0 1 rg 72 600 200 100 re f 0 0.5 1 0 k 72 400 200 100 re f BT 3 Tr /F1 12 Tf 72 300 Td (Unsichtbar) Tj ET"),
    });

    /// <summary>Visible text in a font that is not embedded and has no bundled equivalent.</summary>
    private static byte[] Text() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Font/Subtype/TrueType/BaseFont/Calibri/Encoding/WinAnsiEncoding/FirstChar 32/LastChar 32/Widths[226]>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
        [6] = ("", "BT /F1 12 Tf 72 780 Td (Sichtbar) Tj ET"),
    });

    [Fact]
    public void Conforming_SavedUnderANewName_TheTabKeepsTheOpenedFile()
    {
        var pdf = TestFiles.Write(Shapes(), "pdfa-formen.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "pdfa-formen (PDF-A).pdf");
        File.Delete(target);
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                string? suggested = null;
                vm.PickSavePath = (_, name, _) =>
                {
                    suggested = name;
                    return Task.FromResult<string?>(target);
                };
                var running = vm.ExportPdfACommand.ExecuteAsync(null);
                WaitUntil(window, () => window.OwnedWindows.OfType<PdfAWindow>().Any());
                var dialog = window.OwnedWindows.OfType<PdfAWindow>().Single();
                // the probe proposes the fitting part: shapes and invisible text in a standard font — PDF/A-2u
                var proposing = (PdfAViewModel)dialog.DataContext!;
                WaitUntil(dialog, () => !proposing.Probing && proposing.Recommended is not null, 60);
                Assert.Equal(PdfAConformance.A2u, proposing.Recommended);
                Assert.Equal(PdfAConformance.A2u, proposing.Part);
                UiSession.Capture(dialog);
                var check = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Tr.T("Prüfen und speichern …")));
                Press(dialog, check);
                await running.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.Empty(window.OwnedWindows.OfType<PdfAWindow>());
                Assert.Equal("pdfa-formen (PDF-A).pdf", suggested);
                Assert.True(File.Exists(target));
                using (var copy = PdfDocument.Open(target))
                {
                    Assert.Empty(PdfACheck.Check(copy));
                    Assert.Equal(PdfAConformance.A2u, PdfACheck.Declared(copy));
                }
                Assert.Equal(Path.GetFullPath(pdf), doc.Path);
                Assert.False(doc.IsModified);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("PDF/A-4 — neueste Fassung (PDF 2.0)", PdfAConformance.A4)]
    [InlineData("PDF/A-2u — Text durchsuchbar", PdfAConformance.A2u)]
    public void OtherPart_ChosenWithItsButton_TheCopyDeclaresIt(string button, PdfAConformance part)
    {
        var pdf = TestFiles.Write(Shapes(), $"pdfa-teil-{part}.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, $"pdfa-teil-{part} (PDF-A).pdf");
        File.Delete(target);
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                var running = vm.ExportPdfACommand.ExecuteAsync(null);
                WaitUntil(window, () => window.OwnedWindows.OfType<PdfAWindow>().Any());
                var dialog = window.OwnedWindows.OfType<PdfAWindow>().Single();
                var model = (PdfAViewModel)dialog.DataContext!;
                WaitUntil(dialog, () => !model.Probing, 60);
                Assert.Equal(model.Recommended, model.Part);
                UiSession.Capture(dialog);
                Press(dialog, dialog.GetVisualDescendants().OfType<RadioButton>().Single(r => Equals(r.Content, Tr.T(button))));
                Assert.Equal(part, model.Part);
                Assert.Single(dialog.GetVisualDescendants().OfType<RadioButton>(), r => r.IsChecked == true);
                Press(dialog, dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Tr.T("Prüfen und speichern …"))));
                await running.WaitAsync(TimeSpan.FromSeconds(60));
                using var copy = PdfDocument.Open(target);
                Assert.Empty(PdfACheck.Check(copy));
                Assert.Equal(part, PdfACheck.Declared(copy));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Protected_WithoutTheRightToChangeIt_NoCopy()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(1).Build(cipher: Core.Pdf.Security.PdfCipher.Aes256, user: "geheim", owner: "rechte",
            permissions: Core.Pdf.Security.PdfPermissions.Print), "pdfa-geschuetzt.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                vm.AskPassword = (_, _) => Task.FromResult<string?>("geheim");
                await vm.OpenAsync([pdf]);
                Assert.False(vm.SelectedDocument!.CanChangeProtection);
                var asked = false;
                vm.PickPdfA = _ =>
                {
                    asked = true;
                    return Task.FromResult<PdfAResult?>(null);
                };
                await vm.ExportPdfACommand.ExecuteAsync(null);
                Assert.False(asked);
                Assert.Contains("PDF/A", vm.Message);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The opened file chosen as the target: nothing is written, and the message asking for another name is seen although the file area is still
    /// open — the bar of the window lies under it (docs/FEHLERGEDAECHTNIS.md, No. 340).
    /// </summary>
    [Fact]
    public void OpenedFileAsTarget_MessageSeenOverTheFileArea()
    {
        var pdf = TestFiles.Write(Shapes(), "pdfa-ziel-offen.pdf");
        var before = File.ReadAllBytes(pdf);
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                vm.ShowBackstageCommand.Execute(BackstagePage.Export);
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(pdf);
                var running = vm.ExportPdfACommand.ExecuteAsync(null);
                WaitUntil(window, () => window.OwnedWindows.OfType<PdfAWindow>().Any());
                var model = (PdfAViewModel)window.OwnedWindows.OfType<PdfAWindow>().Single().DataContext!;
                await model.CheckCommand.ExecuteAsync(null);
                await running.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Contains("ist geöffnet", vm.Message);
                Assert.True(vm.BackstageOpen);
                // the topmost element at the middle of a message is the message or its bar (once the layout has taken the message in)
                bool Seen() => window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == vm.Message && t.IsEffectivelyVisible).Any(t =>
                    t.Bounds.Width > 0 && t.TranslatePoint(new Point(t.Bounds.Width / 2, t.Bounds.Height / 2), window) is { } at &&
                    window.GetVisualsAt(at).FirstOrDefault() is { } top &&
                    (ReferenceEquals(top, t) || top.GetVisualAncestors().Contains(t) || t.GetVisualAncestors().Take(3).Contains(top)));
                var until = DateTime.UtcNow.AddSeconds(10);
                while (!Seen() && DateTime.UtcNow < until)
                {
                    UiSession.Capture(window);
                    Thread.Sleep(20);
                }
                Assert.True(Seen(), "die Meldung liegt unter dem Datei-Bereich");
                Assert.Equal(before, File.ReadAllBytes(pdf));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A font that is not in the file is embedded as the substitute the display draws it with, and the message after saving names it.</summary>
    [Fact]
    public void MissingFont_IsEmbeddedAsTheSubstitute_AndNamed()
    {
        var pdf = TestFiles.Write(Text(), "pdfa-ersatzschrift.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "pdfa-ersatzschrift (PDF-A).pdf");
        File.Delete(target);
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                var running = vm.ExportPdfACommand.ExecuteAsync(null);
                WaitUntil(window, () => window.OwnedWindows.OfType<PdfAWindow>().Any());
                var dialog = window.OwnedWindows.OfType<PdfAWindow>().Single();
                var model = (PdfAViewModel)dialog.DataContext!;
                PdfAResult? ready = null;
                model.Ready += r => ready = r;
                await model.CheckCommand.ExecuteAsync(null);
                Assert.False(model.Failed);
                Assert.NotNull(ready);
                Assert.True(ready!.Conforms);
                Assert.Contains("Calibri", ready.SubstitutedFonts);
                await running.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(File.Exists(target));
                Assert.Contains("Calibri", vm.Notice);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>A page that carries a string of 40 000 bytes: no copy can keep it within the limits of PDF/A without losing it.</summary>
    private static byte[] LongString() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>/Eigen(" + new string('x', 40_000) + ")>>",
        [6] = ("", "0 0 1 rg 72 600 200 100 re f"),
    });

    [Fact]
    public void NotConforming_NothingSaved_TheWindowNamesTheReasons()
    {
        var pdf = TestFiles.Write(LongString(), "pdfa-lang.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var asked = false;
                vm.PickSavePath = (_, _, _) =>
                {
                    asked = true;
                    return Task.FromResult<string?>(null);
                };
                var running = vm.ExportPdfACommand.ExecuteAsync(null);
                WaitUntil(window, () => window.OwnedWindows.OfType<PdfAWindow>().Any());
                var dialog = window.OwnedWindows.OfType<PdfAWindow>().Single();
                var model = (PdfAViewModel)dialog.DataContext!;
                await model.CheckCommand.ExecuteAsync(null);
                UiSession.Capture(dialog);
                Assert.True(model.Failed);
                var reason = Assert.Single(model.Reasons);
                Assert.Contains("6.1.13", reason);
                Assert.Contains(dialog.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.IsEffectivelyVisible && t.Text == reason);
                // still open: the user reads the reasons and closes it
                Assert.Contains(dialog, window.OwnedWindows);
                dialog.Close();
                await running.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.False(asked);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
