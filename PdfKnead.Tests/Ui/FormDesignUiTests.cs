using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Platform;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>UI tests for form design: Form tab, inserting, selecting and arranging fields, properties panel, saving.</summary>
[Collection(nameof(UiCollection))]
public sealed class FormDesignUiTests
{
    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void Click(Window window, Point at, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseDown(at, MouseButton.Left, modifiers);
        window.MouseUp(at, MouseButton.Left, modifiers);
    }

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
    }

    [Fact]
    public void Form_InsertSelectArrangeEditAndSave()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 2).Build(), "formular erstellen.pdf");
        UiSession.Run(async () =>
        {
            PdfKnead.Services.UserSettings.Update(s => s with { FieldLooks = null });   // do not take over the remembered look from other tests
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Form;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                List<PageElement> Fields() => [.. doc.ElementsOn(0).Where(DocumentViewModel.IsFormField)];

                // draw a text field, click a date and two number fields: the tool stays on, and every field gets an unused name
                vm.InsertFormFieldCommand.Execute(FieldPreset.Text);
                Assert.True(vm.IsFormDesign);
                // the properties panel appears with the mode: refit the page before clicking
                vm.FitWidthCommand.Execute(null);
                UiSession.Capture(window);
                Drag(window, At(72, 720), At(272, 700));
                vm.InsertFormFieldCommand.Execute(FieldPreset.Date);
                Click(window, At(72, 680));
                vm.InsertFormFieldCommand.Execute(FieldPreset.Number);
                Click(window, At(72, 650));
                Click(window, At(72, 620));
                Assert.Equal(ViewTool.InsertFormField, vm.Tool);
                var fields = Fields().Cast<FieldElement>().ToList();
                Assert.Equal(4, fields.Count);
                Assert.Equal(200, fields[0].Rect.Width, 1);
                Assert.Equal(FieldFormatKind.Date, fields[1].Spec.Format.Kind);
                Assert.Equal(FieldFormatKind.Number, fields[2].Spec.Format.Kind);
                Assert.Equal(4, fields.Select(f => f.Spec.Name).Distinct().Count());

                // radio buttons placed in one tool run form one group with different export values; a new run starts a new group
                vm.InsertFormFieldCommand.Execute(FieldPreset.Radio);
                Click(window, At(300, 680));
                Click(window, At(420, 680));   // beside the label of the first one (a click on the label would select it)
                vm.InsertFormFieldCommand.Execute(FieldPreset.CheckBox);
                vm.InsertFormFieldCommand.Execute(FieldPreset.Radio);
                Click(window, At(300, 640));
                var radios = Fields().OfType<FieldElement>().Where(f => f.Spec.Kind == FormFieldKind.Radio).ToList();
                Assert.Equal(3, radios.Count);
                Assert.Equal(radios[0].Spec.Name, radios[1].Spec.Name);
                Assert.NotEqual(radios[0].Spec.ExportValue, radios[1].Spec.ExportValue);
                Assert.NotEqual(radios[0].Spec.Name, radios[2].Spec.Name);
                vm.InsertFormFieldCommand.Execute(FieldPreset.ComboBox);
                Click(window, At(300, 600));
                Shoot(window, "60-formular-felder-eingefuegt");

                // Esc ends inserting, form design mode stays on; a click selects (does not fill in)
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.Equal(ViewTool.DesignForm, vm.Tool);
                Click(window, At(150, 710));
                Assert.Same(fields[0], doc.SelectedElement?.Element);
                Assert.Null(doc.FillingField);
                Assert.True(vm.FieldProperties.IsVisible);
                Assert.True(vm.FieldProperties.IsText);

                // multiple selection with Ctrl, then same width as the field selected last
                Click(window, At(80, 680), RawInputModifiers.Control);
                Click(window, At(80, 650), RawInputModifiers.Control);
                Assert.Equal(3, doc.FieldSelection.Count);
                vm.ArrangeFieldsCommand.Execute(FieldArrange.SameWidth);
                Assert.All(doc.FieldSelection, r => Assert.Equal(doc.FieldSelection[^1].Element.Rect.Width, r.Element.Rect.Width, 3));
                Assert.Equal(Tr.T("Felder anordnen"), doc.History.UndoDescription);
                Shoot(window, "61-formular-mehrfachauswahl");

                // properties panel: border colour for all three in one step
                vm.FieldProperties.BorderColor = FieldPropertiesViewModel.Colors.First(c => c.Rgb == 0xC00000);
                Assert.All(doc.FieldSelection, r => Assert.Equal(0xC00000, doc.SpecOf(r.Element)!.BorderColor));
                vm.UndoCommand.Execute(null);
                // undo: no border again (text, date and number fields start without one)
                Assert.All(doc.ElementsOn(0).OfType<FieldElement>().Take(4), f => Assert.Null(f.Spec.BorderColor));
                vm.RedoCommand.Execute(null);

                // one field: name, required, currency format; combo box: entries
                Click(window, At(150, 710));
                vm.FieldProperties.Name = "Vorname";
                vm.FieldProperties.Required = true;
                Click(window, At(80, 650));
                vm.FieldProperties.Currency = " €";
                Click(window, At(330, 600));
                Assert.True(vm.FieldProperties.IsCombo);
                vm.FieldProperties.OptionsText = "Deutschland|de\nÖsterreich|at\nSchweiz";
                Shoot(window, "62-formular-eigenschaften");

                // calculation: sum of the two number fields in a new field
                vm.InsertFormFieldCommand.Execute(FieldPreset.Number);
                Click(window, At(72, 560));
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Click(window, At(80, 560));
                var numbers = Fields().OfType<FieldElement>().Where(f => f.Spec.Format.Kind == FieldFormatKind.Number).Select(f => f.Spec.Name).ToList();
                vm.FieldProperties.Operation = FieldPropertiesViewModel.Operations.First(o => o.Value == CalcOperation.Sum);
                foreach (var source in vm.FieldProperties.CalculationSources.Where(s => numbers.Take(2).Contains(s.Name))) source.Included = true;
                var total = (FieldElement)doc.SelectedElement!.Value.Element;
                Assert.Equal(numbers.Take(2), total.Spec.Calculation!.Fields);
                Assert.True(total.Spec.ReadOnly);

                // field list in the sidebar
                vm.ShowSidebarTabCommand.Execute(SidebarTab.Fields);
                Assert.Equal(Fields().Count, vm.FieldList.Count);
                Shoot(window, "63-formular-feldliste");

                // save and reopen: ordinary form fields with their properties
                using var output = new MemoryStream();
                Core.Pdf.Writing.PdfWriter.Write(doc.Document, doc.History.Pages, output);
                using var saved = PdfDocument.Open(output.ToArray());
                var acroForm = saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"));
                var widgets = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().Select(f => FormField.Of(f)!).ToList();
                Assert.Equal(Fields().Count, widgets.Count);
                var vorname = FieldSpec.Of(widgets.First(w => w.Name == "Vorname"), acroForm);
                Assert.True(vorname.Required);
                Assert.Equal(0xC00000, vorname.BorderColor);
                Assert.Equal(["de", "at", "Schweiz"], FieldSpec.Of(widgets.First(w => w.Kind == FormFieldKind.ComboBox), acroForm).Options.Select(o => o.Export));
                Assert.Single(acroForm!.GetArray(PdfName.Get("CO"))!);

                // form design mode off: clicks fill in again
                vm.ToggleFormDesignCommand.Execute(null);
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                Assert.False(vm.FieldProperties.IsVisible);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Builds a form from another program (as exported from an office program): a text field and a check box, both read-only (/Ff 1).</summary>
    private static byte[] ReadOnlyForm()
    {
        var pdf = TestPdf.Simple(1);
        PdfString S(string text) => new(PdfTextEncoding.Encode(text));
        int Look(string content) => pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 14, 14)))));
        var text = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(72, 700, 272, 720)),
            ("F", PdfInteger.Of(4)), ("FT", TestPdf.N("Tx")), ("T", S("Name")), ("Ff", PdfInteger.Of(1)), ("DA", S("/Helv 10 Tf 0 g"))));
        var box = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(72, 660, 86, 674)),
            ("F", PdfInteger.Of(4)), ("FT", TestPdf.N("Btn")), ("T", S("Ok")), ("Ff", PdfInteger.Of(1)), ("V", TestPdf.N("Off")), ("AS", TestPdf.N("Off")),
            ("AP", TestPdf.Dict(("N", TestPdf.Dict(("Yes", TestPdf.Ref(Look("0 g 2 2 10 10 re f"))), ("Off", TestPdf.Ref(Look("0 G 0.5 0.5 13 13 re S")))))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(text), TestPdf.Ref(box)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(text), TestPdf.Ref(box)]))));
        return pdf.Build();
    }

    /// <summary>
    /// A form from another program with read-only fields: in normal mode the fields can be neither dragged, nor moved with the arrow
    /// keys, nor deleted with Del; once "Read-only" is cleared in the properties, they can be filled in at once, without saving first.
    /// </summary>
    [Fact]
    public void Form_ReadOnlyForeignFields_StayAndBecomeFillable()
    {
        var pdf = TestFiles.Write(ReadOnlyForm(), "schreibgeschuetzt.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                PageElement Field(string name) => doc.ElementsOn(0).First(e => DocumentViewModel.IsFormField(e) && doc.FieldNameOf(e) == name);
                var (textRect, boxRect) = (Field("Name").Rect, Field("Ok").Rect);
                Assert.False(DocumentViewModel.IsFillable(Field("Name")));
                Assert.False(DocumentViewModel.IsFillable(Field("Ok")));

                // normal mode: dragging, arrow keys, Del: everything stays
                Drag(window, At(150, 710), At(300, 500));
                view.Focus();
                doc.SelectedElement = new ElementRef(0, Field("Name"));
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                Assert.Equal(textRect, Field("Name").Rect);
                Drag(window, At(79, 667), At(300, 400));
                Assert.Equal(boxRect, Field("Ok").Rect);
                Assert.Equal(2, doc.ElementsOn(0).Count(DocumentViewModel.IsFormField));
                doc.SelectedElement = new ElementRef(0, Field("Ok"));
                Assert.False(vm.DeleteElementCommand.CanExecute(null));   // nor does the Delete element button

                Assert.False(vm.ModeBannerVisible);
                // „Bearbeiten“ on the Start tab (KONZEPT.md, section 5.22): read-only fields can then be dragged, moved with the arrow keys,
                // resized and deleted, the field panel shows on the right; Esc first clears the selection, then ends the mode
                var button = window.GetVisualDescendants().OfType<RibbonToggleButton>().Single(b => b.Label == Tr.T("Bearbeiten") && b.IsEffectivelyVisible);
                Thread.Sleep(800);
                Click(window, button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value);
                Assert.True(vm.IsEditTool);
                Assert.True(button.IsChecked);
                Assert.True(vm.ModeBannerVisible);   // banner at the top shows that fields can be moved
                UiSession.Capture(window);   // the banner pushes the page down
                Drag(window, At(150, 710), At(150, 690));
                Assert.Equal(textRect.Bottom - 20, Field("Name").Rect.Bottom, 0.5);
                Assert.True(vm.FieldProperties.IsFloating);   // floating: the page does not move under the mouse
                view.Focus();
                window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                Assert.Equal(textRect.Bottom - 19, Field("Name").Rect.Bottom, 0.5);
                // … resize: drag the bottom-right corner
                var moved = Field("Name").Rect;
                Drag(window, At(moved.Right, moved.Bottom), At(moved.Right + 50, moved.Bottom));
                Assert.Equal(moved.Width + 50, Field("Name").Rect.Width, 1);
                // … drag the check box and delete it
                Drag(window, At(79, 667), At(79, 647));
                Assert.Equal(boxRect.Bottom - 20, Field("Ok").Rect.Bottom, 0.5);
                UiSession.Capture(window);
                Shoot(window, "62-felder-bearbeiten-start");
                view.Focus();
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                Assert.Equal(1, doc.ElementsOn(0).Count(DocumentViewModel.IsFormField));
                for (var i = 0; i < 5; i++) vm.UndoCommand.Execute(null);
                Assert.Equal(textRect, Field("Name").Rect);
                Assert.Equal(boxRect, Field("Ok").Rect);
                doc.SelectFields([new ElementRef(0, Field("Name"))]);
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.True(vm.IsEditTool);   // first only the selection is cleared
                Assert.Empty(doc.FieldSelection);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(vm.IsEditTool);
                Assert.False(vm.ModeBannerVisible);
                Assert.False(button.IsChecked);
                // Esc also works when the button has the focus (right after the click)
                Click(window, button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value);
                Assert.True(vm.IsEditTool);
                button.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(vm.IsEditTool);

                // form design: clear read-only, end the mode; fillable at once, without saving
                vm.ToggleFormDesignCommand.Execute(null);
                doc.SelectFields([new ElementRef(0, Field("Name"))]);
                Assert.True(vm.ModeBannerVisible);
                UiSession.Capture(window);
                Shoot(window, "63-modus-formular-bearbeiten");
                vm.FieldProperties.ReadOnly = false;
                doc.SelectFields([new ElementRef(0, Field("Ok"))]);
                vm.FieldProperties.ReadOnly = false;
                vm.EndModeCommand.Execute(null);   // End button in the banner
                Assert.False(vm.IsFormDesign);
                Assert.False(vm.ModeBannerVisible);
                Assert.True(DocumentViewModel.IsFillable(Field("Name")));
                Assert.True(DocumentViewModel.IsFillable(Field("Ok")));
                Assert.True(doc.SetFieldValue(new ElementRef(0, Field("Name")), "Max Mustermann"));
                Assert.Equal("Max Mustermann", DocumentViewModel.ValueOf(Field("Name")));
                UiSession.Capture(window);
                Click(window, At(79, 667));
                Assert.NotEqual("Off", DocumentViewModel.ValueOf(Field("Ok")));
                Assert.Equal(boxRect, Field("Ok").Rect);   // the click checks the box and does not move it
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Builds a form as common PDF editors write it, with price and quantity (numbers) and total = sum … (AFSimple_Calculate in /AA /C, /CO), plus a field
    /// with its own calculation script, which never runs.
    /// </summary>
    private static byte[] Calculated()
    {
        var pdf = TestPdf.Simple(1);
        PdfString S(string text) => new(PdfTextEncoding.Encode(text));
        PdfDictionary Js(string script) => TestPdf.Dict(("S", TestPdf.N("JavaScript")), ("JS", S(script)));
        int Field(string name, double bottom, PdfDictionary? aa = null)
        {
            var entries = new List<(string, PdfObject)>
            {
                ("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(72, bottom, 222, bottom + 20)), ("F", PdfInteger.Of(4)),
                ("FT", TestPdf.N("Tx")), ("T", S(name)), ("DA", S("/Helv 10 Tf 0 g")),
            };
            if (aa is not null) entries.Add(("AA", aa));
            return pdf.Add(TestPdf.Dict([.. entries]));
        }
        var number = TestPdf.Dict(("F", Js("AFNumber_Format(2, 2, 0, 0, \" €\", false);")));
        var a = Field("Preis", 700, number);
        var b = Field("Menge", 670);
        var total = Field("Gesamt", 640, TestPdf.Dict(("F", Js("AFNumber_Format(2, 2, 0, 0, \" €\", false);")),
            ("C", Js("AFSimple_Calculate(\"SUM\", new Array (\"Preis\", \"Menge\"));"))));
        var own = Field("Eigen", 610, TestPdf.Dict(("C", Js("event.value = 42;"))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(a), TestPdf.Ref(b), TestPdf.Ref(total), TestPdf.Ref(own)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(
            ("Fields", new PdfArray([TestPdf.Ref(a), TestPdf.Ref(b), TestPdf.Ref(total), TestPdf.Ref(own)])),
            ("CO", new PdfArray([TestPdf.Ref(total), TestPdf.Ref(own)]))));
        return pdf.Build();
    }

    /// <summary>
    /// Simple calculations are recomputed while filling in (in the same undo step), in forms from other PDF editors and in new fields before saving;
    /// custom scripts never run. New fields can be filled in right away (check box, radio group).
    /// </summary>
    [Fact]
    public void Form_CalculationsAndFillingNewFields()
    {
        var pdf = TestFiles.Write(Calculated(), "berechnet.pdf");
        UiSession.Run(async () =>
        {
            PdfKnead.Services.UserSettings.Update(s => s with { FieldLooks = null });   // do not take over the remembered look from other tests
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                UiSession.Capture(window);
                ElementRef Ref(string name) => new(0, doc.ElementsOn(0).First(e => DocumentViewModel.IsFormField(e) && doc.FieldNameOf(e) == name));
                string Value(string name) => DocumentViewModel.ValueOf(Ref(name).Element);

                Assert.True(doc.SetFieldValue(Ref("Preis"), "19.5"));
                Assert.Equal("19.5", Value("Gesamt"));
                Assert.True(doc.SetFieldValue(Ref("Menge"), "3"));
                Assert.Equal("22.5", Value("Gesamt"));
                Assert.Equal("", Value("Eigen"));   // custom script: never runs
                vm.UndoCommand.Execute(null);
                Assert.Equal(("", "19.5"), (Value("Menge"), Value("Gesamt")));   // value and result are one step
                vm.RedoCommand.Execute(null);

                // new fields: check box, radio group, calculation over a field of the original form; fillable before saving
                vm.RibbonTab = RibbonTab.Form;
                vm.InsertFormFieldCommand.Execute(FieldPreset.CheckBox);
                doc.CreateField(0, new Core.Pdf.Document.PdfRectangle(300, 700, 314, 714), 0);
                vm.InsertFormFieldCommand.Execute(FieldPreset.Radio);
                doc.CreateField(0, new Core.Pdf.Document.PdfRectangle(300, 670, 314, 684), 0);
                doc.CreateField(0, new Core.Pdf.Document.PdfRectangle(330, 670, 344, 684), 0);
                vm.InsertFormFieldCommand.Execute(FieldPreset.Number);
                var doubled = (FieldElement)doc.CreateField(0, new Core.Pdf.Document.PdfRectangle(300, 640, 400, 660), 0)!;
                doc.SelectFields([Ref(doubled.Spec.Name)]);
                vm.FieldProperties.Operation = FieldPropertiesViewModel.Operations.First(o => o.Value == CalcOperation.Sum);
                foreach (var source in vm.FieldProperties.CalculationSources.Where(s => s.Name is "Gesamt" or "Preis")) source.Included = true;
                // sum over a radio group: the export value of the chosen option counts (it must not yield 0)
                var second = doc.ElementsOn(0).OfType<FieldElement>().Where(f => f.Spec.Kind == FormFieldKind.Radio).ToList()[1];
                doc.SelectFields([new ElementRef(0, second)]);
                vm.FieldProperties.ExportValue = "2";
                vm.InsertFormFieldCommand.Execute(FieldPreset.Number);
                var fromRadio = (FieldElement)doc.CreateField(0, new Core.Pdf.Document.PdfRectangle(300, 610, 400, 630), 0)!;
                doc.SelectFields([Ref(fromRadio.Spec.Name)]);
                vm.FieldProperties.Operation = FieldPropertiesViewModel.Operations.First(o => o.Value == CalcOperation.Sum);
                foreach (var source in vm.FieldProperties.CalculationSources.Where(s => s.Name == second.Spec.Name)) source.Included = true;
                vm.ToggleFormDesignCommand.Execute(null);   // off: fill-in mode

                var check = doc.ElementsOn(0).OfType<FieldElement>().First(f => f.Spec.Kind == FormFieldKind.CheckBox);
                Assert.True(DocumentViewModel.IsFillable(check));
                Assert.True(doc.ToggleField(new ElementRef(0, check)));
                Assert.Equal(check.Spec.ExportValue, DocumentViewModel.ValueOf(doc.ElementsOn(0).OfType<FieldElement>().First(f => f.Spec.Kind == FormFieldKind.CheckBox)));
                var radios = doc.ElementsOn(0).OfType<FieldElement>().Where(f => f.Spec.Kind == FormFieldKind.Radio).ToList();
                Assert.True(doc.ToggleField(new ElementRef(0, radios[1])));
                radios = [.. doc.ElementsOn(0).OfType<FieldElement>().Where(f => f.Spec.Kind == FormFieldKind.Radio)];
                Assert.Equal(["Off", radios[1].Spec.ExportValue], radios.Select(r => r.Value));
                Assert.Equal("2", Value(fromRadio.Spec.Name));
                Assert.True(doc.ToggleField(new ElementRef(0, radios[0])));
                radios = [.. doc.ElementsOn(0).OfType<FieldElement>().Where(f => f.Spec.Kind == FormFieldKind.Radio)];
                Assert.Equal([radios[0].Spec.ExportValue, "Off"], radios.Select(r => r.Value));

                // the new sum is recomputed on the next fill-in (Preis 10 + Gesamt 13 = 23)
                Assert.True(doc.SetFieldValue(Ref("Preis"), "10"));
                Assert.Equal("13", Value("Gesamt"));
                Assert.Equal("23", Value(doubled.Spec.Name));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Tab order by clicking (saved as the order in /Annots, /Tabs is dropped), renaming updates calculations, and the reset button
    /// restores the default values (without a script).
    /// </summary>
    [Fact]
    public void Form_TabOrderRenameAndReset()
    {
        var pdf = TestFiles.Write(Calculated(), "tab-reihenfolge.pdf");
        UiSession.Run(async () =>
        {
            PdfKnead.Services.UserSettings.Update(s => s with { FieldLooks = null });   // do not take over the remembered look from other tests
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Form;
                vm.ToggleFormDesignCommand.Execute(null);
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                List<string> Order() => [.. doc.ElementsOn(0).Where(DocumentViewModel.IsFormField).Select(doc.FieldNameOf)];
                ElementRef Ref(string name) => new(0, doc.ElementsOn(0).First(e => DocumentViewModel.IsFormField(e) && doc.FieldNameOf(e) == name));

                // tab order: Gesamt first, then Menge; one step per click, Esc ends
                Assert.Equal(["Preis", "Menge", "Gesamt", "Eigen"], Order());
                vm.ToggleTabOrderCommand.Execute(null);
                Assert.True(vm.ShowTabOrder);
                UiSession.Capture(window);   // the message below the ribbon shifts the page
                Click(window, At(150, 650));
                Click(window, At(150, 680));
                Assert.Equal(["Gesamt", "Menge", "Preis", "Eigen"], Order());
                Shoot(window, "65-tab-reihenfolge");
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(vm.ShowTabOrder);
                Assert.False(vm.HasMessage);   // the hint disappears as well

                // rename: the calculation in „Gesamt“ uses the new name
                doc.SelectFields([Ref("Preis")]);
                vm.FieldProperties.Name = "Einzelpreis";
                Assert.Equal(["Einzelpreis", "Menge"], doc.SpecOf(Ref("Gesamt").Element)!.Calculation!.Fields);
                Assert.Equal(Tr.T("Feldname ändern"), doc.History.UndoDescription);

                // default value and a reset button (new field), then fill in and reset
                vm.FieldProperties.DefaultValue = "5";
                vm.InsertFormFieldCommand.Execute(FieldPreset.ResetButton);
                var button = doc.CreateField(0, new Core.Pdf.Document.PdfRectangle(300, 700, 410, 724), 0)!;
                vm.TestFormCommand.Execute(null);
                Assert.True(doc.SetFieldValue(Ref("Einzelpreis"), "7"));
                Assert.True(doc.SetFieldValue(Ref("Menge"), "2"));
                Assert.Equal("9", DocumentViewModel.ValueOf(Ref("Gesamt").Element));
                Assert.True(doc.ResetForm(button));
                Assert.Equal(("5", "", "5"), (DocumentViewModel.ValueOf(Ref("Einzelpreis").Element), DocumentViewModel.ValueOf(Ref("Menge").Element),
                    DocumentViewModel.ValueOf(Ref("Gesamt").Element)));

                // saved: /Annots in the new order, without /Tabs
                using var output = new MemoryStream();
                Core.Pdf.Writing.PdfWriter.Write(doc.Document, doc.History.Pages, output);
                using var saved = PdfDocument.Open(output.ToArray());
                var names = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().Select(f => FormField.Of(f)!.Name).ToList();
                Assert.Equal(["Gesamt", "Menge", "Einzelpreis", "Eigen"], names.Take(4));
                Assert.False(saved.Pages[0].Dictionary.ContainsKey(PdfName.Get("Tabs")));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Labels: radio buttons and check boxes get text to their right; it moves along, can switch sides, can only be moved horizontally,
    /// stays attached to its box after renaming and saving; a click on it checks the box.
    /// </summary>
    [Fact]
    public void Form_LabelsBesideBoxes()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "beschriftungen.pdf");
        UiSession.Run(async () =>
        {
            PdfKnead.Services.UserSettings.Update(s => s with { FieldLooks = null });   // do not take over the remembered look from other tests
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Form;
                vm.InsertFormFieldCommand.Execute(FieldPreset.Radio);
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                Click(window, At(307, 607));
                Click(window, At(307, 577));
                vm.InsertFormFieldCommand.Execute(FieldPreset.CheckBox);
                Click(window, At(307, 547));
                var labels = doc.ElementsOn(0).OfType<TextElement>().Where(DocumentViewModel.IsFieldLabel).ToList();
                Assert.Equal([Tr.T("Option {0}", 1), Tr.T("Option {0}", 2), Tr.T("Beschriftung")], labels.Select(l => l.Text));
                var boxes = doc.ElementsOn(0).OfType<FieldElement>().ToList();
                Assert.All(labels.Zip(boxes), p =>
                {
                    Assert.Equal(p.Second.Rect.Right + 4, p.First.Rect.Left, 1);   // to the right
                    Assert.Equal((p.Second.Rect.Bottom + p.Second.Rect.Top) / 2, (p.First.Rect.Bottom + p.First.Rect.Top) / 2, 1);   // vertically centred
                });
                Shoot(window, "68-beschriftungen");

                // tool still on, nothing selected: Ctrl+click on existing boxes selects them (places no new one)
                doc.SelectFields([]);
                var before = doc.ElementsOn(0).Count;
                Click(window, At(307, 607), RawInputModifiers.Control);
                Click(window, At(307, 577), RawInputModifiers.Control);
                Assert.Equal(ViewTool.InsertFormField, vm.Tool);
                Assert.Equal(before, doc.ElementsOn(0).Count);
                Assert.Equal(2, doc.FieldSelection.Count);
                UiSession.Capture(window);
                Shoot(window, "70-eigenschaften-modern");

                // drag a box: the label moves along
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Drag(window, At(307, 547), At(357, 517));
                var check = doc.ElementsOn(0).OfType<FieldElement>().Single(f => f.Spec.Kind == FormFieldKind.CheckBox);
                var checkLabel = doc.LabelsOf(new ElementRef(0, check)).Single().Element;
                Assert.Equal(check.Rect.Right + 4, checkLabel.Rect.Left, 1);
                Assert.Equal((check.Rect.Bottom + check.Rect.Top) / 2, (checkLabel.Rect.Bottom + checkLabel.Rect.Top) / 2, 1);

                // to the left; the label can only be moved horizontally
                vm.FieldProperties.LabelLeft = true;
                checkLabel = doc.LabelsOf(new ElementRef(0, check)).Single().Element;
                Assert.Equal(check.Rect.Left - 4, checkLabel.Rect.Right, 1);
                doc.SelectFields([new ElementRef(0, checkLabel)]);
                doc.MoveSelectedFields(-20, 30, "x");
                var moved = doc.LabelsOf(new ElementRef(0, check)).Single().Element;
                Assert.Equal(checkLabel.Rect.Left - 20, moved.Rect.Left, 1);
                Assert.Equal(checkLabel.Rect.Bottom, moved.Rect.Bottom, 1);

                // change the text and rename: still attached to the box
                doc.SelectFields([new ElementRef(0, check)]);
                vm.FieldProperties.LabelText = "Ich stimme zu";
                vm.FieldProperties.Name = "Zustimmung";
                check = doc.ElementsOn(0).OfType<FieldElement>().Single(f => f.Spec.Kind == FormFieldKind.CheckBox);
                Assert.Equal("Ich stimme zu", ((TextElement)doc.LabelsOf(new ElementRef(0, check)).Single().Element).Text);

                // fill-in: a click on the label checks the box
                vm.TestFormCommand.Execute(null);
                UiSession.Capture(window);
                var label = (TextElement)doc.LabelsOf(new ElementRef(0, check)).Single().Element;
                var center = At((label.Rect.Left + label.Rect.Right) / 2, (label.Rect.Bottom + label.Rect.Top) / 2);
                Click(window, center);
                check = doc.ElementsOn(0).OfType<FieldElement>().Single(f => f.Spec.Kind == FormFieldKind.CheckBox);
                Assert.Equal(check.Spec.ExportValue, check.Value);

                // saved and reopened: the label knows its box
                using var output = new MemoryStream();
                Core.Pdf.Writing.PdfWriter.Write(doc.Document, doc.History.Pages, output);
                using var saved = PdfDocument.Open(output.ToArray());
                var reread = PageItem.From(saved)[0].Elements.OfType<TextElement>().Where(DocumentViewModel.IsFieldLabel).ToList();
                Assert.Equal(3, reread.Count);
                Assert.Contains(reread, l => l.LabelFor == "Zustimmung" && l.Text == "Ich stimme zu");
                Assert.Equal(2, reread.Count(l => l.LabelState.Length > 0));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>A tooltip typed in the properties panel and saved right away (focus still in the input box) is included in the file.</summary>
    [Fact]
    public void Form_PanelInputSavedWithoutLeavingTheBox()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "bereich speichern.pdf");
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
                var field = (FieldElement)doc.CreateField(0, new PdfRectangle(72, 700, 272, 720), 0)!;
                UiSession.Capture(window);
                var box = window.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Beschriftung (für Bildschirmleser)" && t.IsEffectivelyVisible);
                box.Focus();
                window.KeyTextInput("Name des Kunden");
                Assert.Equal("Name des Kunden", box.Text);
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.False(doc.IsModified);
                using (var saved = PdfDocument.Open(File.ReadAllBytes(pdf)))
                {
                    var widget = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().Single();
                    Assert.Equal("Name des Kunden", FieldSpec.Of(widget)!.Tooltip);
                }

                // two fields selected, focus in the tooltip box (showing the primary field's tooltip), save: the other field keeps its own
                var second = (FieldElement)doc.CreateField(0, new PdfRectangle(72, 600, 272, 620), 0)!;
                vm.FieldProperties.Tooltip = "Zweites";
                ElementRef Ref(string tip) => new(0, doc.ElementsOn(0).First(e => DocumentViewModel.IsFormField(e) && doc.SpecOf(e)?.Tooltip == tip));
                doc.SelectFields([Ref("Zweites"), Ref("Name des Kunden")]);
                UiSession.Capture(window);
                box = window.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Beschriftung (für Bildschirmleser)" && t.IsEffectivelyVisible);
                box.Focus();
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Equal(["Name des Kunden", "Zweites"], doc.ElementsOn(0).Where(DocumentViewModel.IsFormField).Select(e => doc.SpecOf(e)!.Tooltip).Order());
            }
            finally { window.Close(); }
        });
    }

    /// <summary>
    /// Calculation as a formula in the properties panel: errors are explained and not applied; filling in recomputes with operator
    /// precedence and parentheses; renaming updates the formula; saved as common PDF editors do.
    /// </summary>
    [Fact]
    public void Form_FormulaCalculation()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "formel.pdf");
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
                vm.InsertFormFieldCommand.Execute(FieldPreset.Number);
                ElementRef Ref(string name) => new(0, doc.ElementsOn(0).First(e => DocumentViewModel.IsFormField(e) && doc.FieldNameOf(e) == name));
                foreach (var (name, y) in new[] { ("Menge", 700), ("Preis", 670), ("Rabatt", 640), ("Gesamt", 610) })
                {
                    var created = doc.CreateField(0, new PdfRectangle(72, y, 182, y + 20), 0)!;
                    doc.SelectFields([new ElementRef(0, created)]);
                    vm.FieldProperties.Name = name;
                }
                doc.SelectFields([Ref("Gesamt")]);
                vm.FieldProperties.Operation = FieldPropertiesViewModel.Operations.First(o => o.Value == CalcOperation.Formula);
                Assert.True(vm.FieldProperties.IsFormula);
                Assert.False(vm.FieldProperties.IsListCalculation);
                Assert.Contains("Menge", vm.FieldProperties.FormulaFields);

                // error: explained, not applied, the text stays in the box
                vm.FieldProperties.FormulaText = "(Menge * Preis";
                Assert.True(vm.FieldProperties.HasFormulaError);
                void PanelToEnd()
                {
                    UiSession.Capture(window);
                    window.GetVisualDescendants().OfType<FieldPropertiesPanel>().Single(p => p.IsEffectivelyVisible).GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
                }
                PanelToEnd();
                Shoot(window, "71-formel-fehler");
                Assert.Equal("(Menge * Preis", vm.FieldProperties.FormulaText);
                Assert.Null(doc.SpecOf(Ref("Gesamt").Element)!.Calculation!.Formula);
                vm.FieldProperties.FormulaText = "Menge * Stückpreis";
                Assert.Contains("Stückpreis", vm.FieldProperties.FormulaError);
                vm.FieldProperties.FormulaText = "Gesamt + 1";
                Assert.True(vm.FieldProperties.HasFormulaError);

                vm.FieldProperties.FormulaText = "(Menge * Preis - Rabatt) * 1,19";
                Assert.False(vm.FieldProperties.HasFormulaError);
                Assert.Equal("(Menge * Preis - Rabatt) * 1.19", doc.SpecOf(Ref("Gesamt").Element)!.Calculation!.Formula!.Notation);
                Assert.True(doc.SpecOf(Ref("Gesamt").Element)!.ReadOnly);
                PanelToEnd();
                Shoot(window, "72-formel");

                // fill-in: operator precedence, parentheses
                vm.ToggleFormDesignCommand.Execute(null);
                Assert.True(doc.SetFieldValue(Ref("Menge"), "3"));
                Assert.True(doc.SetFieldValue(Ref("Preis"), "10"));
                Assert.True(doc.SetFieldValue(Ref("Rabatt"), "5"));
                Assert.Equal("29.75", DocumentViewModel.ValueOf(Ref("Gesamt").Element));

                // rename: the formula uses the new name and keeps working
                vm.ToggleFormDesignCommand.Execute(null);
                doc.SelectFields([Ref("Menge")]);
                vm.FieldProperties.Name = "Anzahl";
                Assert.Equal("(Anzahl * Preis - Rabatt) * 1.19", doc.SpecOf(Ref("Gesamt").Element)!.Calculation!.Formula!.Notation);

                // saved as common PDF editors do, read back as a formula when opened
                await vm.SaveCommand.ExecuteAsync(null);
                using var saved = PdfDocument.Open(File.ReadAllBytes(pdf));
                var total = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().First(w => FormField.Of(w)!.Name == "Gesamt");
                Assert.Equal("(Anzahl * Preis - Rabatt) * 1.19", FieldSpec.Of(total)!.Calculation!.Formula!.Notation);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Ctrl+D with several fields gives every copy its own name; a duplicate name is asked about first, duplicate export values are rejected.</summary>
    [Fact]
    public void Form_NamesStayUnique()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "eindeutige namen.pdf");
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
                var a = doc.CreateField(0, new PdfRectangle(72, 700, 272, 720), 0)!;
                var b = doc.CreateField(0, new PdfRectangle(72, 650, 272, 670), 0)!;
                List<string> Names() => [.. doc.ElementsOn(0).Where(DocumentViewModel.IsFormField).Select(doc.FieldNameOf)];
                doc.SelectFields([new ElementRef(0, a), new ElementRef(0, b)]);
                Assert.True(doc.DuplicateSelectedFields());
                Assert.Equal(4, Names().Distinct().Count());

                // renaming to an existing name links the fields (KONZEPT §5.4a) — asked first; „Anderen Namen wählen“ leaves it as it is
                var first = doc.ElementsOn(0).First(DocumentViewModel.IsFormField);
                var last = doc.ElementsOn(0).Last(DocumentViewModel.IsFormField);
                doc.SelectFields([new ElementRef(0, last)]);
                vm.AskChoice = (_, _, _, _) => Task.FromResult(2);
                vm.FieldProperties.Name = doc.FieldNameOf(first);
                Assert.Equal(4, Names().Distinct().Count());
                vm.AskChoice = null;

                // radio group: second option set to the export value of the first: rejected
                vm.InsertFormFieldCommand.Execute(FieldPreset.Radio);
                var r1 = (FieldElement)doc.CreateField(0, new PdfRectangle(72, 500, 86, 514), 0)!;
                var r2 = (FieldElement)doc.CreateField(0, new PdfRectangle(172, 500, 186, 514), 0)!;
                doc.SelectFields([new ElementRef(0, r2)]);
                vm.FieldProperties.ExportValue = r1.Spec.ExportValue;
                var radios = doc.ElementsOn(0).OfType<FieldElement>().Where(f => f.Spec.Kind == FormFieldKind.Radio).ToList();
                Assert.Equal(2, radios.Select(r => r.Spec.ExportValue).Distinct().Count());

                // label selected, text changed: the selection points to the new label, not to the old instance
                bool SelectionOnPage() => doc.SelectedElement is { } sel && doc.ElementsOn(0).Any(e => ReferenceEquals(e, sel.Element));
                vm.InsertFormFieldCommand.Execute(FieldPreset.CheckBox);
                var box = doc.CreateField(0, new PdfRectangle(72, 400, 86, 414), 0)!;
                var label = doc.LabelsOf(new ElementRef(0, box)).Single();
                doc.SelectFields([label]);
                Assert.True(doc.SetLabelText("Neuer Text"));
                Assert.True(SelectionOnPage());
                Assert.True(doc.SetLabelSide(true));
                Assert.True(SelectionOnPage());
                Assert.True(doc.SetLabelText(""));   // label removed: the box stays selected
                Assert.True(SelectionOnPage());
                Assert.IsType<FieldElement>(doc.SelectedElement!.Value.Element);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void Form_RubberBandWithMouse()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "rahmen aufziehen.pdf");
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
                vm.InsertFormFieldCommand.Execute(FieldPreset.Number);
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                Click(window, At(72, 520));
                Click(window, At(72, 490));
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.Equal(ViewTool.DesignForm, vm.Tool);
                UiSession.Capture(window);

                // rubber band from empty space over both fields, from top right to bottom left (must not give a rectangle with negative width that selects nothing)
                Drag(window, At(330, 537), At(60, 476));
                Assert.Equal(2, doc.FieldSelection.Count);
                // a click into empty space clears the selection; from bottom left to top right only the lower field is touched
                Click(window, At(400, 600));
                Assert.Empty(doc.FieldSelection);
                Drag(window, At(60, 470), At(330, 495));
                Assert.Single(doc.FieldSelection);
                // field drawn towards the top left: correct size
                vm.InsertFormFieldCommand.Execute(FieldPreset.Text);
                Drag(window, At(300, 700), At(100, 720));
                var created = doc.ElementsOn(0).OfType<FieldElement>().Last();
                Assert.Equal(200, created.Rect.Width, 1);
                Assert.Equal(100, created.Rect.Left, 1);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>
    /// Arranging: centred, middle, horizontal distribution (evenly and with a gap; boxes including labels), switching the label off and
    /// on again, label font, size and colour, also when the label itself is selected.
    /// </summary>
    [Fact]
    public void Form_ArrangeDistributeAndLabelStyle()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "anordnen.pdf");
        UiSession.Run(async () =>
        {
            PdfKnead.Services.UserSettings.Update(s => s with { FieldLooks = null });   // do not take over the remembered look from other tests
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Form;
                PdfRectangle R(double l, double b, double w, double h) => new(l, b, l + w, b + h);
                vm.InsertFormFieldCommand.Execute(FieldPreset.CheckBox);
                var a = doc.CreateField(0, R(100, 300, 14, 14), 0)!;
                var b = doc.CreateField(0, R(180, 310, 14, 14), 0)!;
                var c = doc.CreateField(0, R(320, 296, 14, 14), 0)!;
                FieldElement Box(PageElement original) => doc.ElementsOn(0).OfType<FieldElement>().Single(f => f.Spec.Name == ((FieldElement)original).Spec.Name);
                PageElement LabelOf(PageElement box) => doc.LabelsOf(new ElementRef(0, Box(box))).Single().Element;
                void Select(params PageElement[] boxes) => doc.SelectFields([.. boxes.Select(x => new ElementRef(0, Box(x)))]);

                // middle (on one line) with the field selected last
                Select(a, b, c);
                vm.ArrangeFieldsCommand.Execute(FieldArrange.Middle);
                Assert.All(new[] { a, b, c }, x => Assert.Equal(303, (Box(x).Rect.Bottom + Box(x).Rect.Top) / 2, 2));
                Assert.All(new[] { a, b, c }, x => Assert.Equal(303, (LabelOf(x).Rect.Bottom + LabelOf(x).Rect.Top) / 2, 2));   // labels move along

                // distribute horizontally with a 12 pt gap: 12 pt between a label and the next box
                Select(a, b, c);
                vm.DistributeGap = "12";
                vm.DistributeFieldsCommand.Execute(FieldArrange.DistributeHorizontally);
                Assert.Equal(100, Box(a).Rect.Left, 2);
                Assert.Equal(LabelOf(a).Rect.Right + 12, Box(b).Rect.Left, 2);
                Assert.Equal(LabelOf(b).Rect.Right + 12, Box(c).Rect.Left, 2);
                // evenly: first and last stay, equal gaps
                Select(c);
                doc.MoveSelectedFields(200, 0, "x");
                Select(a, b, c);
                vm.DistributeGap = Tr.T("Gleichmäßig");
                vm.DistributeFieldsCommand.Execute(FieldArrange.DistributeHorizontally);
                var gap1 = Box(b).Rect.Left - LabelOf(a).Rect.Right;
                var gap2 = Box(c).Rect.Left - LabelOf(b).Rect.Right;
                Assert.Equal(gap1, gap2, 2);
                Assert.True(gap1 > 12);

                // centred one above the other
                Select(a, c);
                vm.ArrangeFieldsCommand.Execute(FieldArrange.Center);
                Assert.Equal((Box(c).Rect.Left + Box(c).Rect.Right) / 2, (Box(a).Rect.Left + Box(a).Rect.Right) / 2, 2);

                // label: font, size, colour, set through the selected label itself
                doc.SelectFields([new ElementRef(0, LabelOf(b))]);
                Assert.True(vm.FieldProperties.IsCheck);   // shows the properties of the box
                vm.FieldProperties.LabelFont = MainViewModel.FontOptions[1];
                vm.FieldProperties.LabelSize = 14;
                vm.FieldProperties.LabelColor = MainViewModel.ColorOptions.First(o => o.Rgb == 0xC00000);
                var styled = (TextElement)LabelOf(b);
                Assert.Equal((ElementFont.Serif, 14.0, 0xC00000), (styled.Font, styled.FontSize, styled.Color));
                Assert.Equal(Box(b).Rect.Right + 4, styled.Rect.Left, 1);   // gap stays

                // off and on again: the same text
                Select(b);
                vm.FieldProperties.LabelText = "Mit Zucker";
                vm.FieldProperties.ShowLabel = false;
                Assert.Empty(doc.LabelsOf(new ElementRef(0, Box(b))));
                vm.FieldProperties.ShowLabel = true;
                Assert.Equal("Mit Zucker", ((TextElement)LabelOf(b)).Text);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// New fields take over the look last chosen for their kind (stored in the settings); the label gap is adjustable; a label that
    /// was switched off stays off for new boxes.
    /// </summary>
    [Fact]
    public void Form_RemembersLookAndLabelGap()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "aussehen merken.pdf");
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
                PdfRectangle R(double l, double b, double w, double h) => new(l, b, l + w, b + h);

                // text field: no border at first; red border and 14 pt → the next text field too, a date field not
                vm.InsertFormFieldCommand.Execute(FieldPreset.Text);
                var first = (FieldElement)doc.CreateField(0, R(72, 700, 180, 20), 0)!;
                Assert.Null(first.Spec.BorderColor);
                vm.FieldProperties.BorderColor = FieldPropertiesViewModel.Colors.First(c => c.Rgb == 0xC00000);
                vm.FieldProperties.FontSize = FieldPropertiesViewModel.FontSizes.First(s => s.Value == 14);
                var second = (FieldElement)doc.CreateField(0, R(72, 660, 180, 20), 0)!;
                Assert.Equal((0xC00000, 14.0), (second.Spec.BorderColor, second.Spec.FontSize));
                vm.InsertFormFieldCommand.Execute(FieldPreset.Date);
                Assert.Null(((FieldElement)doc.CreateField(0, R(72, 620, 110, 20), 0)!).Spec.BorderColor);
                Assert.Contains(AppSettings.Load(PdfKnead.Services.UserSettings.File).FieldLooks!, l => l.Kind == "Text" && l.BorderColor == 0xC00000);

                // multiple selection: one property changes for all, the others stay per field
                first = doc.ElementsOn(0).OfType<FieldElement>().First(f => f.Spec.Name == first.Spec.Name);   // changed in the meantime: take the current version
                doc.SelectFields([new ElementRef(0, first)]);
                vm.FieldProperties.FontSize = FieldPropertiesViewModel.FontSizes.First(s => s.Value == 9);
                first = doc.ElementsOn(0).OfType<FieldElement>().First(f => f.Spec.Name == first.Spec.Name);
                Assert.Equal(9.0, first.Spec.FontSize);
                second = doc.ElementsOn(0).OfType<FieldElement>().First(f => f.Spec.Name == second.Spec.Name);
                doc.SelectFields([new ElementRef(0, first), new ElementRef(0, second)]);
                vm.FieldProperties.FillColor = FieldPropertiesViewModel.Colors.First(c => c.Rgb == 0xFFF6CC);
                first = doc.ElementsOn(0).OfType<FieldElement>().First(f => f.Spec.Name == first.Spec.Name);
                second = doc.ElementsOn(0).OfType<FieldElement>().First(f => f.Spec.Name == second.Spec.Name);
                Assert.Equal((0xFFF6CC, 0xFFF6CC), (first.Spec.FillColor, second.Spec.FillColor));
                Assert.Equal((9.0, 14.0), (first.Spec.FontSize, second.Spec.FontSize));
                // mixed with a check box: only common properties (no font, no format, no label)
                vm.InsertFormFieldCommand.Execute(FieldPreset.CheckBox);
                var mixedBox = (FieldElement)doc.CreateField(0, R(300, 580, 14, 14), 0)!;
                doc.SelectFields([new ElementRef(0, first), new ElementRef(0, mixedBox)]);
                Assert.True(vm.FieldProperties.IsMixed);
                Assert.False(vm.FieldProperties.IsText || vm.FieldProperties.IsCheck || vm.FieldProperties.HasTextStyle);
                Assert.True(vm.FieldProperties.HasAppearance);
                // Ctrl+click removes the box again: only text fields, all text sections are back
                doc.ToggleFieldSelection(new ElementRef(0, mixedBox));
                Assert.False(vm.FieldProperties.IsMixed);
                Assert.True(vm.FieldProperties.IsText && vm.FieldProperties.HasTextStyle);
                vm.InsertFormFieldCommand.Execute(FieldPreset.Text);
                doc.SelectFields([]);

                // check box: gap 10 pt, label on the left → the next box the same
                vm.InsertFormFieldCommand.Execute(FieldPreset.CheckBox);
                var box = (FieldElement)doc.CreateField(0, R(300, 700, 14, 14), 0)!;
                vm.FieldProperties.LabelGap = 10;
                vm.FieldProperties.LabelLeft = true;
                Assert.Equal(10, (double)vm.FieldProperties.LabelGap!.Value, 1);
                var next = (FieldElement)doc.CreateField(0, R(300, 660, 14, 14), 0)!;
                var label = doc.LabelsOf(new ElementRef(0, next)).Single().Element;
                Assert.Equal(next.Rect.Left - 10, label.Rect.Right, 1);
                // label off → new boxes without one
                vm.FieldProperties.ShowLabel = false;
                var bare = (FieldElement)doc.CreateField(0, R(300, 620, 14, 14), 0)!;
                Assert.Empty(doc.LabelsOf(new ElementRef(0, bare)));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
                PdfKnead.Services.UserSettings.Update(s => s with { FieldLooks = null });
            }
        });
    }

    /// <summary>Choice list of a combo box while filling in: click on the field, the list opens, choose an entry (the calendar of a date field:
    /// <see cref="Calendar_TodayWeeksAndMonthView"/>).</summary>
    [Fact]
    public void Form_ChoiceListFlyout()
    {
        var pdf = TestFiles.Write(FormFillTests.AllKinds(), "auswahl.pdf");
        UiSession.Run(async () =>
        {
            PdfKnead.Services.UserSettings.Update(s => s with { FieldLooks = null });   // do not take over the remembered look from other tests
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                Click(window, At(150, 440));   // „Land“
                UiSession.Capture(window);
                Shoot(window, "69-auswahlliste");
                var items = window.GetVisualDescendants().Concat(TopLevel.GetTopLevel(window)!.GetVisualDescendants())
                    .OfType<Button>().Where(b => b.Classes.Contains("choiceItem")).ToList();
                if (items.Count == 0)
                    items = [.. Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants().OfType<Button>()
                        .Where(b => b.Classes.Contains("choiceItem")) ?? []];
                var austria = items.FirstOrDefault(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Österreich"));
                Assert.NotNull(austria);   // the list has opened
                austria.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("at", DocumentViewModel.ValueOf(doc.ElementsOn(0).First(e => doc.FieldNameOf(e) == "Land")));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>The built-in calendar: week numbers on the left, a Today button, month overview through the title.</summary>
    [Fact]
    public void Calendar_TodayWeeksAndMonthView()
    {
        UiSession.Run(() =>
        {
            var calendar = new FieldCalendar(new DateTime(2026, 10, 18));
            var window = new Window { Content = calendar, Width = 330, Height = 460 };
            window.Show();
            try
            {
                DateTime? chosen = null;
                calendar.DateChosen += d => chosen = d;
                UiSession.Capture(window);
                Shoot(window, "66-kalender");
                var texts = calendar.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains("42", texts);   // calendar week 42: 12–18 October 2026
                Assert.Contains("44", texts);
                var buttons = calendar.GetVisualDescendants().OfType<Button>().ToList();
                buttons.First(b => b.Content as string == Tr.T("Heute")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(DateTime.Today, chosen);
                // title: month overview, then choose a month
                var title = buttons.First(b => (b.Content as string)?.Contains("2026") == true);
                title.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                UiSession.Capture(window);
                Shoot(window, "67-kalender-monate");
                Assert.Contains(calendar.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "2026");
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>Fields from another program: selecting, resizing and changing properties in form design mode.</summary>
    [Fact]
    public void Form_ForeignFieldResizedAndChanged()
    {
        var pdf = TestFiles.Write(FormFillTests.AllKinds(), "fremdes formular.pdf");
        UiSession.Run(async () =>
        {
            PdfKnead.Services.UserSettings.Update(s => s with { FieldLooks = null });   // do not take over the remembered look from other tests
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Form;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                ForeignAnnotation Name() => doc.ElementsOn(0).OfType<ForeignAnnotation>().First(f => FormField.Of(f)!.Name == "Name");

                vm.ToggleFormDesignCommand.Execute(null);
                vm.FitWidthCommand.Execute(null);
                UiSession.Capture(window);
                Click(window, At(150, 710));
                Assert.Same(Name(), doc.SelectedElement?.Element);
                Assert.Null(doc.FillingField);
                // right middle handle: 50 pt wider; the appearance is redrawn (properties carried over)
                Drag(window, At(272, 710), At(322, 710));
                Assert.Equal(250, Name().Rect.Width, 1);
                Assert.NotNull(Name().Spec);
                vm.FieldProperties.Tooltip = "Vor- und Nachname";
                Assert.Equal("Vor- und Nachname", Name().Spec!.Tooltip);
                // radio group: a field property applies to both widgets
                Click(window, At(80, 488));
                vm.FieldProperties.Required = true;
                Assert.All(doc.ElementsOn(0).OfType<ForeignAnnotation>().Where(f => FormField.Of(f)!.Name == "Zahlung"),
                    f => Assert.True(f.Spec!.Required));
                Shoot(window, "64-fremdes-formular-bearbeiten");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Tab order with an outdated field (a reference from before an undo): before, on a page without fields it divided by zero
    /// (the click ended in an exception), and on a page with fields the old field was put in while the last one dropped out.
    /// Now nothing changes; a current field is still placed.
    /// </summary>
    [Fact]
    public void TabOrder_OutdatedField_ChangesNothing()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 2).Build(), "tab-reihenfolge.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var first = new FieldElement { Rect = new PdfRectangle(100, 300, 250, 320), Spec = new FieldSpec { Name = "Erstes" } };
                var second = new FieldElement { Rect = new PdfRectangle(100, 400, 250, 420), Spec = new FieldSpec { Name = "Zweites" } };
                doc.AddElement(0, first, "Feld");
                doc.AddElement(0, second, "Feld");
                vm.ToggleFormDesignCommand.Execute(null);
                doc.SetShowTabOrder(true);
                var outdated = first with { Spec = new FieldSpec { Name = "Früher" } };

                Assert.False(doc.PlaceInTabOrder(new ElementRef(1, outdated)));   // page without fields
                Assert.False(doc.PlaceInTabOrder(new ElementRef(0, outdated)));
                Assert.Equal([first, second], doc.ElementsOn(0));

                Assert.True(doc.PlaceInTabOrder(new ElementRef(0, second)));
                Assert.Equal([second, first], doc.ElementsOn(0));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
            }
        });
    }

    // ---- 7.4 E-31: calculations in the order of /CO (ISO 32000-2, 12.7.3, Table 224, 12.6.3; docs/FEHLERGEDAECHTNIS.md, No. 578)

    /// <summary>
    /// Fields a (input), x = sum of y, y = sum of a, z = sum of a; /CO lists x before y, z not at all (a file that breaks the rule that every
    /// field with a calculation is in /CO).
    /// </summary>
    private static byte[] Ordered()
    {
        var pdf = TestPdf.Simple(1);
        PdfString S(string text) => new(PdfTextEncoding.Encode(text));
        PdfDictionary Sum(string of) => TestPdf.Dict(("C", TestPdf.Dict(("S", TestPdf.N("JavaScript")),
            ("JS", S($"AFSimple_Calculate(\"SUM\", new Array (\"{of}\"));")))));
        int Field(string name, double bottom, PdfDictionary? aa = null)
        {
            var entries = new List<(string, PdfObject)>
            {
                ("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(72, bottom, 222, bottom + 20)), ("F", PdfInteger.Of(4)),
                ("FT", TestPdf.N("Tx")), ("T", S(name)), ("DA", S("/Helv 10 Tf 0 g")),
            };
            if (aa is not null) entries.Add(("AA", aa));
            return pdf.Add(TestPdf.Dict([.. entries]));
        }
        var a = Field("a", 700);
        var y = Field("y", 670, Sum("a"));   // first on the page …
        var x = Field("x", 640, Sum("y"));   // … but /CO calculates x first
        var z = Field("z", 610, Sum("a"));
        PdfArray Refs(params int[] n) => new([.. n.Select(i => (PdfObject)TestPdf.Ref(i))]);
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), Refs(a, y, x, z));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", Refs(a, y, x, z)), ("CO", Refs(x, y))));
        return pdf.Build();
    }

    /// <summary>
    /// One pass in /CO order: changing a calculates x (from the y before) and then y — x lags one step behind, as in readers that follow /CO.
    /// Before: passes up to a fixed point gave x = y. z, outside /CO, is not calculated, and that is said once.
    /// </summary>
    [Fact]
    public void CalculationsFollowTheCalculationOrder_OutsideCoNotCalculated()
    {
        var pdf = TestFiles.Write(Ordered(), "berechnet-reihenfolge.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var messages = new List<string>();
                doc.MessageRequested += messages.Add;
                ElementRef Ref(string name) => new(0, doc.ElementsOn(0).First(e => DocumentViewModel.IsFormField(e) && doc.FieldNameOf(e) == name));
                string Value(string name) => DocumentViewModel.ValueOf(Ref(name).Element);

                Assert.True(doc.SetFieldValue(Ref("a"), "5"));
                Assert.Equal(("0", "5"), (Value("x"), Value("y")));
                Assert.True(doc.SetFieldValue(Ref("a"), "7"));
                Assert.Equal(("5", "7"), (Value("x"), Value("y")));
                Assert.Equal("", Value("z"));
                Assert.Single(messages, m => m.Contains("„z“", StringComparison.Ordinal));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// New fields with calculations, made in the "wrong" order (the sum of a sum first): saved, /CO lists them after the file's order and each
    /// after the field it reads; z, a calculation of the file outside /CO, stays out (the file is not changed in that).
    /// </summary>
    [Fact]
    public void OwnCalculatedFields_WrittenInCoInDependencyOrder()
    {
        var pdf = TestFiles.Write(Ordered(), "berechnet-eigene.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "berechnet-eigene gespeichert.pdf");
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
                ElementRef Ref(string name) => new(0, doc.ElementsOn(0).First(e => DocumentViewModel.IsFormField(e) && doc.FieldNameOf(e) == name));
                vm.RibbonTab = RibbonTab.Form;
                FieldElement Number(double bottom)
                {
                    vm.InsertFormFieldCommand.Execute(FieldPreset.Number);
                    return (FieldElement)doc.CreateField(0, new Core.Pdf.Document.PdfRectangle(300, bottom, 400, bottom + 20), 0)!;
                }
                void SumOf(FieldElement field, string source)
                {
                    doc.SelectFields([Ref(field.Spec.Name)]);
                    vm.FieldProperties.Operation = FieldPropertiesViewModel.Operations.First(o => o.Value == CalcOperation.Sum);
                    foreach (var s in vm.FieldProperties.CalculationSources.Where(s => s.Name == source)) s.Included = true;
                }
                var outer = Number(700);
                var inner = Number(670);
                SumOf(outer, inner.Spec.Name);   // made first, but reads the second
                SumOf(inner, "a");
                vm.ToggleFormDesignCommand.Execute(null);
                Assert.True(doc.SetFieldValue(Ref("a"), "4"));
                Assert.Equal("4", DocumentViewModel.ValueOf(Ref(outer.Spec.Name).Element));   // in the same step, own fields in dependency order

                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.SaveAsCommand.ExecuteAsync(null);
                using var saved = Core.Pdf.Document.PdfDocument.Open(target);
                var co = saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("CO"))!;
                var names = Enumerable.Range(0, co.Count).Select(i => (co.Get(i) as PdfDictionary)?.Get(PdfName.Get("T")) is PdfString t ? t.ToText() : "?").ToList();
                Assert.Equal(["x", "y", inner.Spec.Name, outer.Spec.Name], names);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The order calculated is the order saved (docs/FEHLERGEDAECHTNIS.md, No. 588). A: x (/CO [x, y]) gets another calculation over y — in
    /// the session it is calculated after y, and the saved /CO is [y, x] (before: it stayed [x, y], so after reopening x lagged one step behind
    /// y). B: z, a calculation of the file outside /CO, only becomes required — it stays outside and is not calculated (before: every changed
    /// property counted as „set here“, so the session calculated z, but the saved file did not).
    /// </summary>
    [Fact]
    public void ChangedCalculation_LeavesItsPlace_OtherChangesKeepIt()
    {
        var pdf = TestFiles.Write(Ordered(), "berechnet-geaendert.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "berechnet-geaendert gespeichert.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                ElementRef Ref(string name) => new(0, doc.ElementsOn(0).First(e => DocumentViewModel.IsFormField(e) && doc.FieldNameOf(e) == name));
                string Value(string name) => DocumentViewModel.ValueOf(Ref(name).Element);
                vm.RibbonTab = RibbonTab.Form;
                if (!doc.IsFieldDesign) vm.ToggleFormDesignCommand.Execute(null);
                doc.SelectFields([Ref("x")]);
                vm.FieldProperties.Operation = FieldPropertiesViewModel.Operations.First(o => o.Value == CalcOperation.Maximum);   // max of y
                doc.SelectFields([Ref("z")]);
                vm.FieldProperties.Required = true;
                vm.ToggleFormDesignCommand.Execute(null);

                Assert.True(doc.SetFieldValue(Ref("a"), "5"));
                Assert.Equal(("5", "5"), (Value("x"), Value("y")));   // x after y: no lag
                Assert.Equal("", Value("z"));                          // still outside /CO

                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.SaveAsCommand.ExecuteAsync(null);
                using var saved = Core.Pdf.Document.PdfDocument.Open(target);
                var co = saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("CO"))!;
                var names = Enumerable.Range(0, co.Count).Select(i => (co.Get(i) as PdfDictionary)?.Get(PdfName.Get("T")) is PdfString t ? t.ToText() : "?").ToList();
                Assert.Equal(["y", "x"], names);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
