using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Platform;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// UI tests for File → New with ISO A formats, and for inserting blank pages in the chosen format.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class NewDocumentUiTests
{
    private static void WaitUntil(Window window, Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            UiSession.Capture(window);
            Thread.Sleep(20);
        }
        Assert.True(condition(), "Zeitüberschreitung");
    }

    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static (string Name, bool Landscape)? Format(PageViewModel page) => PageFormat.Detect(page.Page.Width, page.Page.Height);

    private static void Apply(Window dialog) =>
        dialog.FindControl<Button>("ApplyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void NewDocument_ChooseFormat_InsertPages_SaveAndReopen()
    {
        var target = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-test-").FullName, "neu.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                Services.UserSettings.Update(s => s with { NewPage = null });
                // Ctrl+N: dialog with A4 portrait preselected; choose A3 landscape, three pages
                vm.PickPageSetup = async (setup, insert) =>
                {
                    Assert.False(insert);
                    Assert.Equal(new PageSetup(), setup);
                    var dialog = new NewDocumentWindow(setup, insert);
                    var result = dialog.ShowDialog<BlankPagesChoice?>(window);
                    UiSession.Capture(dialog);
                    dialog.FindControl<ComboBox>("FormatChoice")!.SelectedIndex = PageSetup.Formats.ToList().IndexOf("A3");
                    dialog.FindControl<RadioButton>("LandscapeChoice")!.IsChecked = true;
                    dialog.FindControl<NumericUpDown>("CountBox")!.Value = 3;
                    Assert.False(dialog.FindControl<StackPanel>("SizePanel")!.IsVisible);
                    Assert.False(dialog.FindControl<StackPanel>("PositionPanel")!.IsVisible);
                    var preview = dialog.FindControl<Border>("PreviewPage")!;
                    Assert.True(preview.Width > preview.Height);
                    Shoot(dialog, "73-neues-dokument");
                    Apply(dialog);
                    return await result;
                };
                window.KeyPress(Key.N, RawInputModifiers.Control, PhysicalKey.N, null);
                WaitUntil(window, () => vm.SelectedDocument is not null);
                var doc = vm.SelectedDocument!;
                Assert.True(doc.IsUntitled);
                Assert.Equal(Tr.T("Unbenannt {0}", 1) + ".pdf", doc.FileName);
                Assert.False(doc.IsModified);
                Assert.Equal("", doc.Folder);
                Assert.Equal(3, doc.PageCount);
                Assert.All(doc.Pages, p => Assert.Equal(("A3", true), Format(p)));
                Assert.Equal(("A3", true), AppSettings.Load(Services.UserSettings.File).NewPage is { } remembered ? (remembered.Format, remembered.Landscape) : default);
                Shoot(window, "74-neues-dokument-a3-quer");

                // blank page from the menu: A4 portrait after the current (first) page
                vm.InsertBlankPageAsCommand.Execute("A4");
                Assert.Equal(4, doc.PageCount);
                Assert.Equal(("A4", false), Format(doc.Pages[1]));
                Assert.Same(doc.Pages[1], doc.CurrentPage);

                // "More …": custom size, two pages, at the end; the dialog suggests the format chosen last
                vm.PickPageSetup = async (setup, insert) =>
                {
                    Assert.True(insert);
                    Assert.Equal(("A3", true, 1), (setup.Format, setup.Landscape, setup.Count));
                    var dialog = new NewDocumentWindow(setup, insert);
                    var result = dialog.ShowDialog<BlankPagesChoice?>(window);
                    UiSession.Capture(dialog);
                    dialog.FindControl<ComboBox>("FormatChoice")!.SelectedIndex = PageSetup.Formats.Count;   // custom size
                    Assert.True(dialog.FindControl<StackPanel>("SizePanel")!.IsVisible);
                    Assert.True(dialog.FindControl<StackPanel>("PositionPanel")!.IsVisible);
                    // takes over the dimensions of A3 landscape; switching to portrait swaps width and height
                    Assert.Equal(420m, dialog.FindControl<NumericUpDown>("WidthBox")!.Value);
                    dialog.FindControl<RadioButton>("PortraitChoice")!.IsChecked = true;
                    Assert.Equal(297m, dialog.FindControl<NumericUpDown>("WidthBox")!.Value);
                    dialog.FindControl<NumericUpDown>("WidthBox")!.Value = 100;
                    dialog.FindControl<NumericUpDown>("HeightBox")!.Value = 150;
                    dialog.FindControl<NumericUpDown>("CountBox")!.Value = 2;
                    dialog.FindControl<RadioButton>("EndChoice")!.IsChecked = true;
                    Shoot(dialog, "75-leere-seiten-einfuegen");
                    Apply(dialog);
                    return await result;
                };
                await vm.InsertBlankPagesCommand.ExecuteAsync(null);
                Assert.Equal(6, doc.PageCount);
                Assert.Equal(Tr.T("Leerseiten einfügen"), doc.History.UndoDescription);
                foreach (var page in doc.Pages.Skip(4))
                {
                    Assert.Equal(100, page.Page.Width / PageFormat.PointsPerMillimeter, 3);
                    Assert.Equal(150, page.Page.Height / PageFormat.PointsPerMillimeter, 3);
                }
                Assert.True(doc.IsModified);

                // Ctrl+S on a new document asks for the location (starting in the Documents folder)
                string? suggested = null, folder = null;
                vm.PickSavePath = (_, name, start) =>
                {
                    (suggested, folder) = (name, start);
                    return Task.FromResult<string?>(target);
                };
                window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
                WaitUntil(window, () => !doc.IsUntitled && !doc.IsModified && vm.Busy is null);
                Assert.Equal(Tr.T("Unbenannt {0}", 1) + ".pdf", suggested);
                Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), folder);
                Assert.Equal(target, doc.Path);
                Assert.Equal(Path.GetDirectoryName(target), doc.Folder);
                Assert.Contains(target, vm.RecentFiles.Select(r => r.Path));
                using (var saved = PdfDocument.Open(File.ReadAllBytes(target)))
                {
                    Assert.False(saved.WasRepaired);
                    Assert.Equal(6, saved.PageCount);
                    Assert.Equal(("A3", true), PageFormat.Detect(saved.Pages[0].Width, saved.Pages[0].Height));
                    Assert.Equal(("A4", false), PageFormat.Detect(saved.Pages[1].Width, saved.Pages[1].Height));
                }

                // A second new document is again called "Untitled 1" (the first one is now „neu.pdf“), a third one "Untitled 2";
                // closing an unchanged document does not ask
                vm.PickPageSetup = (setup, _) => Task.FromResult<BlankPagesChoice?>(new BlankPagesChoice(setup, BlankPagePosition.After));
                await vm.NewDocumentCommand.ExecuteAsync(null);
                var second = vm.SelectedDocument!;
                await vm.NewDocumentCommand.ExecuteAsync(null);
                var third = vm.SelectedDocument!;
                Assert.Equal(Tr.T("Unbenannt {0}", 1) + ".pdf", second.FileName);
                Assert.Equal(Tr.T("Unbenannt {0}", 2) + ".pdf", third.FileName);
                Assert.Equal(100, third.Pages[0].Page.Width / PageFormat.PointsPerMillimeter, 3);   // chosen last: the custom size
                vm.AskSaveChanges = _ => throw new InvalidOperationException("darf nicht fragen");
                await vm.CloseDocumentCommand.ExecuteAsync(third);
                Assert.DoesNotContain(third, vm.Documents);

                // closing a changed document with "Save": asks for the location
                vm.InsertBlankPageCommand.Execute(null);
                Assert.Same(second, vm.SelectedDocument);
                Assert.True(second.IsModified);
                var secondTarget = Path.Combine(Path.GetDirectoryName(target)!, "zweites.pdf");
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Save);
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(secondTarget);
                await vm.CloseDocumentCommand.ExecuteAsync(second);
                Assert.DoesNotContain(second, vm.Documents);
                using (var saved = PdfDocument.Open(File.ReadAllBytes(secondTarget))) Assert.Equal(2, saved.PageCount);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Cancelling the dialog creates no document and inserts no pages.</summary>
    [Fact]
    public void Cancel_ChangesNothing()
    {
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                vm.PickPageSetup = (_, _) => Task.FromResult<BlankPagesChoice?>(null);
                await vm.NewDocumentCommand.ExecuteAsync(null);
                Assert.Empty(vm.Documents);
                var doc = vm.CreateDocument(new PageSetup("A5"));
                await vm.InsertBlankPagesCommand.ExecuteAsync(null);
                Assert.Equal(1, doc.PageCount);
                Assert.False(doc.IsModified);
                Assert.Equal(("A5", false), Format(doc.Pages[0]));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// „Eigenschaften“: title, language and „Titel statt Dateiname anzeigen“ edited and applied count as a change; saving writes them
    /// (docs/FEHLERGEDAECHTNIS.md, No. 492). A new document gets the language of the program.
    /// </summary>
    [Fact]
    public void Properties_TitleAndLanguage_Saved()
    {
        var pdf = TestFiles.Write(Pdf.TestPdf.Simple(pages: 1).Build(), "eigenschaften.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.Equal("Testdokument", doc.EditTitle);
                doc.EditTitle = "Antrag von Erika Mustermann";
                doc.EditLanguage = "de-DE";
                doc.EditDisplayTitle = true;
                doc.ApplyDescriptionCommand.Execute(null);
                Assert.True(doc.IsModified);
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.False(doc.IsModified);
                using (var saved = PdfDocument.Open(pdf))
                {
                    Assert.Equal("Antrag von Erika Mustermann", saved.Info.Title);
                    Assert.Equal("de-DE", saved.Catalog!.GetString(Core.Pdf.Objects.PdfName.Get("Lang"))!.ToText());
                }
                Assert.Equal("Antrag von Erika Mustermann", doc.EditTitle);

                var created = vm.CreateDocument(new PageSetup());
                Assert.False(string.IsNullOrEmpty(created.Document.Catalog!.GetString(Core.Pdf.Objects.PdfName.Get("Lang"))?.ToText()));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
