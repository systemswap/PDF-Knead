using Avalonia;
using PdfKnead.Controls;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Linked fields in the window (KONZEPT.md, section 5.4a): renaming a field to the name of another of its kind links them after a question,
/// they share their value, the panel shows the others and separates them; a name of another kind is refused.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class FieldLinksUiTests
{
    private static PdfRectangle R(double l, double b, double w, double h) => new(l, b, l + w, b + h);

    [Fact]
    public void RenamedToAnotherName_Linked_ShareTheValue_Separated()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 2).Build(), "verbundene felder.pdf");
        UiSession.Run(async () =>
        {
            PdfKnead.Services.UserSettings.Update(s => s with { FieldLooks = null });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Form;
                vm.InsertFormFieldCommand.Execute(FieldPreset.Text);
                var a = doc.CreateField(0, R(72, 700, 200, 20), 0)!;
                var b = doc.CreateField(1, R(72, 500, 200, 20), 0)!;
                doc.SetFieldValue(new ElementRef(0, doc.ElementsOn(0).First(DocumentViewModel.IsFormField)), "Köln");
                vm.InsertFormFieldCommand.Execute(FieldPreset.CheckBox);
                var box = doc.CreateField(1, R(72, 400, 14, 14), 0)!;
                var nameA = doc.FieldNameOf(a);
                PageElement A() => doc.ElementsOn(0).First(DocumentViewModel.IsFormField);
                PageElement B() => doc.ElementsOn(1).First(e => DocumentViewModel.IsFormField(e) && doc.SpecOf(e)!.Kind == FormFieldKind.Text);

                // a check box cannot take the name of a text field
                doc.SelectFields([new ElementRef(1, box)]);
                vm.FieldProperties.Name = nameA;
                Assert.True(vm.HasMessage);
                Assert.NotEqual(nameA, doc.FieldNameOf(doc.ElementsOn(1).First(e => doc.SpecOf(e)?.Kind == FormFieldKind.CheckBox)));

                // the second text field: asked, linked, it takes over the value
                string? asked = null;
                vm.AskChoice = (heading, _, _, _) =>
                {
                    asked = heading;
                    return Task.FromResult(1);
                };
                doc.SelectFields([new ElementRef(1, B())]);
                vm.FieldProperties.Name = nameA;
                Assert.Contains(nameA, asked);
                Assert.Equal(nameA, doc.FieldNameOf(B()));
                Assert.Equal("Köln", DocumentViewModel.ValueOf(B()));
                Assert.True(vm.FieldProperties.IsLinked);
                Assert.True(vm.FieldProperties.CanSeparate);
                Assert.Single(vm.FieldProperties.LinkedFields);
                Assert.StartsWith("Seite 1", vm.FieldProperties.LinkedFields[0].Text);
                Assert.True(doc.IsLinkedField(A()) && doc.IsLinkedField(B()));
                // the two buttons are recognisable as buttons: filled in the accent colour of the theme (flat, they looked like text)
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                foreach (var label in new[] { "Von den anderen trennen", "Alle trennen" })
                {
                    var button = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, label));
                    Assert.True(button.IsEffectivelyVisible, label);
                    var fill = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(button.Background);
                    Assert.True(fill.Color.A > 0, $"„{label}“ ohne Hintergrund");
                }
                if (UiSession.ScreenshotFolder() is { } folder) UiSession.Capture(window).Save(Path.Combine(folder, "felder-verbunden.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

                // what is typed into one is in the other
                doc.SetFieldValue(new ElementRef(0, A()), "Bonn");
                Assert.Equal("Bonn", DocumentViewModel.ValueOf(B()));

                // saved: one field with two widgets
                var linkedPath = Path.Combine(Path.GetDirectoryName(pdf)!, "verbundene felder gespeichert.pdf");
                Assert.True(await vm.SaveDocumentAsync(doc, linkedPath, doc.SaveVersion));
                using (var saved = PdfDocument.Open(File.ReadAllBytes(linkedPath)))
                {
                    var widgets = PageItem.From(saved).SelectMany(p => p.Elements.OfType<ForeignAnnotation>()).Select(w => FormField.Of(w)!)
                        .Where(f => f.Name == nameA).ToList();
                    Assert.Equal(2, widgets.Count);
                    Assert.Same(widgets[0].Field, widgets[1].Field);
                }

                // after saving still linked (now one field of the file); a click on the row jumps to the other field and selects it
                doc.SelectFields([new ElementRef(1, B())]);
                vm.FieldProperties.Refresh(doc);
                Assert.Single(vm.FieldProperties.LinkedFields);
                vm.FieldProperties.GoToLinked(vm.FieldProperties.LinkedFields[0]);
                Assert.Same(A(), doc.FieldSelection[^1].Element);

                // separated: a name of its own, the value stays; then filled in on its own; undone in one step
                doc.SelectFields([new ElementRef(1, B())]);
                vm.FieldProperties.SeparateOneCommand.Execute(null);
                Assert.Equal(nameA + "_2", doc.FieldNameOf(B()));
                Assert.Equal("Bonn", DocumentViewModel.ValueOf(B()));
                Assert.False(vm.FieldProperties.IsLinked);
                doc.SetFieldValue(new ElementRef(0, A()), "Ulm");
                Assert.Equal("Bonn", DocumentViewModel.ValueOf(B()));
                vm.UndoCommand.Execute(null);
                vm.UndoCommand.Execute(null);
                Assert.Equal(nameA, doc.FieldNameOf(B()));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void FieldOfTheDocumentWithTwoWidgets_ShownLinked_SeparatedInTheFile()
    {
        // a field with two widgets, as other programs write it
        using var first = PdfDocument.Open(TestPdf.Simple(1).Build());
        var spec = new FieldSpec { Name = "Ort" };
        using var output = new MemoryStream();
        PdfWriter.Write(first, [PageItem.From(first)[0].WithElements(
            [new FieldElement { Rect = R(72, 700, 150, 20), Value = "Bonn", Spec = spec }, new FieldElement { Rect = R(72, 600, 150, 20), Value = "Bonn", Spec = spec }])], output);
        var pdf = TestFiles.Write(output.ToArray(), "feld mit zwei widgets.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Form;
                vm.Tool = ViewTool.DesignForm;
                var widgets = doc.ElementsOn(0).Where(DocumentViewModel.IsFormField).ToList();
                Assert.Equal(2, widgets.Count);
                doc.SelectFields([new ElementRef(0, widgets[1])]);
                vm.FieldProperties.Refresh(doc);
                Assert.True(vm.FieldProperties.IsLinked);
                vm.FieldProperties.SeparateOneCommand.Execute(null);
                Assert.Equal(["Ort", "Ort_2"], doc.ElementsOn(0).Where(DocumentViewModel.IsFormField).Select(doc.FieldNameOf).Order().ToList());
                var path = Path.Combine(Path.GetDirectoryName(pdf)!, "feld mit zwei widgets getrennt.pdf");
                Assert.True(await vm.SaveDocumentAsync(doc, path, doc.SaveVersion));
                using var saved = PdfDocument.Open(File.ReadAllBytes(path));
                var fields = PageItem.From(saved).SelectMany(p => p.Elements.OfType<ForeignAnnotation>()).Select(w => FormField.Of(w)!).ToList();
                Assert.Equal(["Ort", "Ort_2"], fields.Select(f => f.Name).Order().ToList());
                Assert.NotSame(fields[0].Field, fields[1].Field);
                Assert.All(fields, f => Assert.Equal("Bonn", f.StoredValue));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void RecognizedHeadOnEveryPage_ProposedLinked_TakenOverLinked()
    {
        var pdf = TestFiles.Write(CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R 5 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
            [5] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
            [6] = ("", "BT /F1 10 Tf 72 780 Td (Aktenzeichen:) Tj ET 0.7 w 150 778 m 300 778 l S"),
        }), "kopf auf jeder seite.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                await vm.RecognizeFieldsCommand.ExecuteAsync(null);
                Assert.Equal(["Aktenzeichen", "Aktenzeichen"], doc.FieldProposals.Select(r => r.Name));
                Assert.All(doc.FieldProposals, r => Assert.True(r.IsLinked));
                vm.AcceptFieldProposalsCommand.Execute(null);
                var fields = new[] { doc.ElementsOn(0).Single(DocumentViewModel.IsFormField), doc.ElementsOn(1).Single(DocumentViewModel.IsFormField) };
                Assert.All(fields, f => Assert.Equal("Aktenzeichen", doc.FieldNameOf(f)));
                Assert.All(fields, f => Assert.True(doc.IsLinkedField(f)));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
    /// <summary>
    /// The question „Mit … verbinden?“ comes when the name box loses focus — by a click on the page, which changes the selection before the
    /// answer: the field being renamed is selected again, and „Verbinden“ links it all the same. Enter in the name box takes the name over
    /// (docs/FEHLERGEDAECHTNIS.md, No. 360).
    /// </summary>
    [Fact]
    public void SelectionChangesWhileAsking_StillLinked_EnterTakesTheNameOver()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "verbinden-waehrend-frage.pdf");
        UiSession.Run(async () =>
        {
            PdfKnead.Services.UserSettings.Update(s => s with { FieldLooks = null });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Form;
                vm.InsertFormFieldCommand.Execute(FieldPreset.Text);
                var a = doc.CreateField(0, R(72, 700, 200, 20), 0)!;
                var b = doc.CreateField(0, R(72, 600, 200, 20), 0)!;
                var nameA = doc.FieldNameOf(a);
                PageElement A() => doc.ElementsOn(0).First(e => e.Rect.Bottom > 650 && DocumentViewModel.IsFormField(e));
                PageElement B() => doc.ElementsOn(0).First(e => e.Rect.Bottom < 650 && DocumentViewModel.IsFormField(e));

                // while the question is open, a click on the page selects the other field (or nothing): the answer still links B
                vm.AskChoice = (_, _, _, _) =>
                {
                    doc.SelectFields([new ElementRef(0, A())]);
                    return Task.FromResult(1);
                };
                doc.SelectFields([new ElementRef(0, B())]);
                vm.FieldProperties.Name = nameA;
                UiSession.Capture(window);
                Assert.Equal(nameA, doc.FieldNameOf(B()));
                Assert.True(doc.IsLinkedField(A()) && doc.IsLinkedField(B()));

                // Enter in the name box takes a new name over (the box binds on losing focus); a linked field is renamed as a whole
                doc.SelectFields([new ElementRef(0, B())]);
                UiSession.Capture(window);
                var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NameBox" && t.IsEffectivelyVisible);
                box.Focus();
                box.Text = "Rechnungsort";
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                UiSession.Capture(window);
                Assert.Equal("Rechnungsort", doc.FieldNameOf(B()));

                // the click on the page that makes the box lose its focus starts a selection frame, and the question swallows the release:
                // the frame must not follow the mouse after the answer (docs/FEHLERGEDAECHTNIS.md, No. 397)
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                var asked = new TaskCompletionSource<int>();
                vm.AskChoice = (_, _, _, _) => asked.Task;
                var c = doc.CreateField(0, R(72, 500, 200, 20), 0)!;
                doc.SelectFields([new ElementRef(0, c)]);
                UiSession.Capture(window);
                box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NameBox" && t.IsEffectivelyVisible);
                box.Focus();
                box.Text = "Rechnungsort";
                var free = view.TranslatePoint(view.PagePointToView(0, 400, 300), window)!.Value;
                window.MouseDown(free, MouseButton.Left);   // leaves the box: the question opens, the release goes to it
                UiSession.Capture(window);
                asked.SetResult(2);
                UiSession.Capture(window);
                window.MouseMove(new Point(free.X + 80, free.Y + 60));
                UiSession.Capture(window);
                Assert.False(view.IsDragging);
                doc.SelectFields([new ElementRef(0, B())]);
                UiSession.Capture(window);
                box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NameBox" && t.IsEffectivelyVisible);
                box.Focus();
                box.Text = "Lieferort";
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                UiSession.Capture(window);
                Assert.Equal("Lieferort", doc.FieldNameOf(B()));
                Assert.Equal("Lieferort", doc.FieldNameOf(A()));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
