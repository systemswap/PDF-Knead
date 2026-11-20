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
using PdfKnead.Localization;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>The tool „Messen“ (Ablaufplan 7.6 c; docs/FEHLERGEDAECHTNIS.md, No. 581).</summary>
[Collection(nameof(UiCollection))]
public sealed class MeasureUiTests
{
    /// <summary>A plan page with a scale of 0.01 m per point and the lines (100,100) → (400,100) → (400,400).</summary>
    internal static byte[] Plan()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("2 w 100 100 m 400 100 l 400 400 l S"u8.ToArray()));
        PdfString S(string text) => new(PdfTextEncoding.Encode(text));
        PdfDictionary Unit(string label, double factor) => TestPdf.Dict(("U", S(label)), ("C", new PdfReal(factor)));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("VP"), new PdfArray([TestPdf.Dict(("BBox", TestPdf.Box(0, 0, 595.28, 841.89)),
            ("Measure", TestPdf.Dict(("Subtype", TestPdf.N("RL")), ("R", S("1:100")), ("X", new PdfArray([Unit("m", 0.01)])),
                ("D", new PdfArray([Unit("m", 1)])), ("A", new PdfArray([Unit("m²", 1)])))))]));
        return pdf.Build();
    }

    /// <summary>
    /// The drawing of a page built and kept by the document while „Messen“ is on — as moving the mouse over the page does
    /// (docs/FEHLERGEDAECHTNIS.md, No. 625): the points snap to it from the first click and hover, also under the load of a full test run,
    /// when the shared cache drops lists. Off, only built (as before).
    /// </summary>
    internal static async Task KeepDrawing(DocumentViewModel doc, int page)
    {
        var key = doc.Pages[page].Key;
        if (await doc.Renderer.GetDisplayList(key) is { } list) doc.KeepSnapList(key, list);
    }

    [Fact]
    public void Measure_SnappedLength_FreePoint_AreaWithEnter_EscAndToolOff()
    {
        var pdf = TestFiles.Write(Plan(), "plan-messen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                var target = view.PagePointToView(0, 250, 250);
                view.Offset += new Vector(target.X - view.Bounds.Width / 2, target.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                await doc.Renderer.GetDisplayList(doc.Pages[0].Key);   // snapping needs the drawing of the page
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                void Click(double x, double y, RawInputModifiers modifiers = RawInputModifiers.None)
                {
                    window.MouseMove(At(x, y), modifiers);
                    window.MouseDown(At(x, y), MouseButton.Left, modifiers);
                    window.MouseUp(At(x, y), MouseButton.Left, modifiers);
                    UiSession.Capture(window);
                    Thread.Sleep(50);
                }

                vm.MeasureCommand.Execute(null);
                await KeepDrawing(doc, 0);   // the drawing kept by the document, as moving the mouse over the page does (No. 625)
                UiSession.Capture(window);
                Assert.True(doc.IsMeasuring);
                Assert.True(window.FindControl<Border>("MeasureHost")!.IsVisible);

                // length: two clicks beside the corners snap onto them — exactly 3 m
                Click(102, 98);
                Click(397, 103);
                var length = Assert.Single(doc.Measurements);
                Assert.Equal("3 m", length.Measurement.Text);
                Assert.Equal("1:100", length.Measurement.Scale);
                Assert.Equal("3 m", doc.MeasureText);
                Assert.Equal(Tr.T("Maßstab: {0}", "1:100"), doc.MeasureScaleText);

                // with Alt where clicked: no longer exactly 3 m
                Click(102, 98, RawInputModifiers.Alt);
                Click(397, 103, RawInputModifiers.Alt);
                Assert.Equal(2, doc.Measurements.Count);
                Assert.NotEqual("3 m", doc.Measurements[1].Measurement.Text);

                // area: three corners, Enter closes — a triangle of 4.5 m² with 10.24 m perimeter
                doc.MeasureKind = MeasureKind.Area;
                UiSession.Capture(window);
                Click(101, 101, RawInputModifiers.Control);   // on the corner where the lengths begin: Ctrl begins a new measurement (7.6 l)
                Click(399, 101);
                Click(399, 399);
                Assert.Equal(3, doc.MeasurePoints.Count);
                view.Focus();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                UiSession.Capture(window);
                Assert.Equal("4.5 m²; 10.24 m", doc.Measurements[^1].Measurement.Text);
                window.MouseMove(At(250, 330));   // a measurement going on is drawn with the point under the mouse
                if (UiSession.ScreenshotFolder() is { } folder)
                {
                    Click(150, 330);
                    window.MouseMove(At(330, 395));
                    UiSession.Capture(window).Save(Path.Combine(folder, "71-messen.png"), PngBitmapEncoderOptions.Default);
                    view.Focus();
                    window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                }

                // Esc drops a measurement going on that cannot be ended yet (one point) without asking; switching the tool off asks about
                // the measurements that are not in the PDF (No. 619) — „Verwerfen“ removes them
                var asked = new List<ChoiceRequest>();
                vm.AskChoices = request =>
                {
                    asked.Add(request);
                    return Task.FromResult(new ChoiceAnswer(2, false));
                };
                Click(101, 101, RawInputModifiers.Control);   // on measurements already there: with Ctrl a new one (7.6 l)
                Assert.Single(doc.MeasurePoints);
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                UiSession.Capture(window);
                Assert.Empty(doc.MeasurePoints);
                Assert.Empty(asked);
                Assert.Equal(3, doc.Measurements.Count);
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                Assert.Equal(Tr.T("Messungen in die PDF übernehmen?"), Assert.Single(asked).Heading);
                Assert.False(doc.IsMeasuring);
                Assert.Empty(doc.Measurements);
                Assert.False(window.FindControl<Border>("MeasureHost")!.IsVisible);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Nothing drawn is lost without a question (decided on 8.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 619). Esc with a polyline that
    /// could be ended asks: „Weiter messen“ keeps the points, „Abschließen“ ends it, „Verwerfen“ drops it, „Abschließen und in die PDF
    /// einfügen“ makes it a dimension on the page. Switching the tool off with measurements not in the PDF asks: „Zurück zum Messen“ brings
    /// tool and measurements back, „Verwerfen“ lets them go (a dimension stays), „In die PDF einfügen“ inserts them; „Nicht mehr fragen“ is
    /// saved, and then switching off clears without a question. Before: Esc dropped the polyline and switching off cleared, silently.
    /// </summary>
    [Fact]
    public void EscAndToolOff_AskBeforeMeasurementsAreLost()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-rueckfrage.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                var asked = new List<ChoiceRequest>();
                var answer = new ChoiceAnswer(0, false);
                vm.AskChoices = request =>
                {
                    asked.Add(request);
                    return Task.FromResult(answer);
                };
                void Esc()
                {
                    view.Focus();
                    window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                    UiSession.Capture(window);
                }
                void Polyline()
                {
                    doc.MeasureKind = MeasureKind.Polyline;
                    foreach (var m in new[] { (6.0, 4.0), (8.0, 4.0), (8.0, 5.5) })
                    {
                        var (x, y) = MeasurePlanTests.At(m, 100, 1);
                        doc.MeasureAt(0, x, y);
                    }
                }
                int Dimensions() => doc.History.Pages[0].Elements.OfType<DimensionElement>().Count();

                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                Polyline();

                // „Weiter messen“: the points stay, the tool stays on
                Esc();
                var question = Assert.Single(asked);
                Assert.Equal(Tr.T("Messung abschließen?"), question.Heading);
                Assert.Equal([Tr.T("Abschließen"), Tr.T("Abschließen und in die PDF einfügen"), Tr.T("Verwerfen")], question.Answers);
                Assert.Equal(Tr.T("Weiter messen"), question.Cancel);
                Assert.Equal(3, doc.MeasurePoints.Count);
                Assert.True(doc.IsMeasuring);

                // „Abschließen“: a measurement of the session
                answer = new ChoiceAnswer(1, false);
                Esc();
                Assert.Empty(doc.MeasurePoints);
                var finished = Assert.Single(doc.Measurements);
                Assert.True(finished.IsSession);
                Assert.Equal("3.5 m", finished.Measurement.Text);

                // „Verwerfen“: gone
                Polyline();
                answer = new ChoiceAnswer(3, false);
                Esc();
                Assert.Empty(doc.MeasurePoints);
                Assert.Single(doc.Measurements);

                // „Abschließen und in die PDF einfügen“: a dimension on the page
                Polyline();
                answer = new ChoiceAnswer(2, false);
                Esc();
                Assert.Equal(1, Dimensions());
                Assert.Equal(2, doc.Measurements.Count);
                Assert.Single(doc.Measurements, r => r.IsSaved);
                Assert.Equal(4, asked.Count);

                // the tool off: „Zurück zum Messen“ — tool and measurement back
                answer = new ChoiceAnswer(0, false);
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                question = asked[^1];
                Assert.Equal(Tr.T("Messung in die PDF übernehmen?"), question.Heading);
                Assert.Equal([Tr.T("In die PDF einfügen"), Tr.T("Verwerfen")], question.Answers);
                Assert.Equal(Tr.T("Zurück zum Messen"), question.Cancel);
                Assert.NotNull(question.Check);
                Assert.True(doc.IsMeasuring);
                Assert.Equal(ViewTool.Measure, vm.Tool);
                Assert.Same(finished, Assert.Single(doc.Measurements, r => r.IsSession));

                // „Verwerfen“: the measurement goes, the dimension stays on the page
                answer = new ChoiceAnswer(2, false);
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                Assert.False(doc.IsMeasuring);
                Assert.Empty(doc.Measurements);
                Assert.Equal(1, Dimensions());

                // a polyline going on when the tool goes off counts as drawn; „In die PDF einfügen“ with „Nicht mehr fragen“
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                Polyline();
                answer = new ChoiceAnswer(1, true);
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                Assert.Equal(2, Dimensions());
                Assert.False(doc.MeasureAskOnLeave);
                Assert.False(AppSettings.Load(PdfKnead.Services.UserSettings.File).AskUnsavedMeasurements);

                // not asked any more: switching off clears
                var count = asked.Count;
                vm.MeasureCommand.Execute(null);
                Polyline();
                doc.FinishMeasure();
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                Assert.Equal(count, asked.Count);
                Assert.Empty(doc.Measurements);
                Assert.Equal(2, Dimensions());
            }
            finally
            {
                PdfKnead.Services.UserSettings.Update(s => s with { AskUnsavedMeasurements = true });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// A plan without a scale: the paper is measured (300 pt = 105.8 mm). Calibrating with the last distance (3 m) writes a scale into the
    /// page — the measurement becomes 3 m —; 1:50 replaces it (one viewport, not two); saved and opened again, the file measures the same;
    /// undone, the page has no scale again, redone the scale is back (Ablaufplan 7.6 d; docs/FEHLERGEDAECHTNIS.md, No. 582).
    /// </summary>
    [Fact]
    public void Calibrate_WritesTheScaleIntoThePage_SavedAndUndone()
    {
        var plan = TestPdf.Simple(1);
        plan.Replace(4, TestPdf.FlateStream("2 w 100 100 m 400 100 l 400 400 l S"u8.ToArray()));
        var pdf = TestFiles.Write(plan.Build(), "plan-kalibrieren.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "plan-kalibrieren gespeichert.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                var middle = view.PagePointToView(0, 250, 250);
                view.Offset += new Vector(middle.X - view.Bounds.Width / 2, middle.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                await doc.Renderer.GetDisplayList(doc.Pages[0].Key);
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                void Click(double x, double y)
                {
                    window.MouseMove(At(x, y));
                    window.MouseDown(At(x, y), MouseButton.Left);
                    window.MouseUp(At(x, y), MouseButton.Left);
                    UiSession.Capture(window);
                    Thread.Sleep(50);
                }
                vm.MeasureCommand.Execute(null);
                await KeepDrawing(doc, 0);   // the drawing kept by the document, as moving the mouse over the page does (No. 625)
                Click(102, 98);
                Click(397, 103);
                Assert.Equal("105.8 mm", Assert.Single(doc.Measurements).Measurement.Text);
                Assert.Null(Assert.Single(doc.Measurements).Measurement.Scale);

                // calibrate: the same line as start and end, 3 m in reality
                doc.StartCalibrationCommand.Execute(null);
                Assert.True(doc.IsCalibrating);
                Click(102, 98);
                Click(397, 103);
                Assert.False(doc.IsCalibrating);
                Assert.True(doc.HasCalibrationLine);
                doc.ScaleUnit = "m";
                doc.CalibrationLength = "3";
                Assert.Equal("28.35", doc.ScaleRatio);   // 105.8 mm of paper are 3 m
                doc.ApplyScaleCommand.Execute(null);
                Assert.False(doc.HasCalibrationLine);
                Assert.Equal("3 m", Assert.Single(doc.Measurements).Measurement.Text);
                Assert.Single(PageScales.Of(doc.History.Pages[0].Page.Dictionary));

                doc.ScaleRatio = "1:50";
                doc.ApplyScaleCommand.Execute(null);
                Assert.Equal("5.29 m", Assert.Single(doc.Measurements).Measurement.Text);
                var viewport = Assert.Single(PageScales.Of(doc.History.Pages[0].Page.Dictionary));   // replaced, not added
                Assert.Equal("1:50", viewport.Measure!.Ratio);

                // each scale is one undo step
                vm.UndoCommand.Execute(null);
                vm.UndoCommand.Execute(null);
                Assert.Empty(PageScales.Of(doc.History.Pages[0].Page.Dictionary));
                vm.RedoCommand.Execute(null);
                vm.RedoCommand.Execute(null);
                Assert.Equal("1:50", Assert.Single(PageScales.Of(doc.History.Pages[0].Page.Dictionary)).Measure!.Ratio);

                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.SaveAsCommand.ExecuteAsync(null);
                using var saved = PdfDocument.Open(target);
                Assert.Equal("5.29 m", Measuring.Measure(MeasureKind.Length, [(100, 100), (400, 100)], PageScales.Of(saved.Pages[0].Dictionary)).Text);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The test plan through the view (docs/FEHLERGEDAECHTNIS.md, No. 583): on the rotated page (1:50) clicks beside the corners measure the
    /// diagonal of the rectangle — 5 m; on the page with /UserUnit 2 and no scale the width is 40 mm of paper (before: 20 mm), and „1:100“
    /// makes it 4 m (before: 2 m).
    /// </summary>
    [Fact]
    public void TestPlan_RotatedPage_AndUserUnit_MeasureTheRealSize()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-messen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                vm.MeasureCommand.Execute(null);

                async Task Measure(int page, MeasureKind kind, (double X, double Y)[] metres)
                {
                    var plan = MeasurePlanTests.Pages[page];
                    var clicks = MeasurePlanTests.Clicks(metres, plan.Ratio, plan.UserUnit);
                    var middle = view.PagePointToView(page, clicks[0].X, clicks[0].Y);
                    view.Offset += new Vector(middle.X - view.Bounds.Width / 2, middle.Y - view.Bounds.Height / 2);
                    UiSession.Capture(window);
                    await KeepDrawing(doc, page);
                    doc.MeasureKind = kind;
                    foreach (var (x, y) in clicks)
                    {
                        var at = view.TranslatePoint(view.PagePointToView(page, x, y), window)!.Value;
                        window.MouseMove(at);
                        window.MouseDown(at, MouseButton.Left);
                        window.MouseUp(at, MouseButton.Left);
                        UiSession.Capture(window);
                        Thread.Sleep(50);
                    }
                }

                await Measure(1, MeasureKind.Length, [(0, 0), (4, 3)]);
                Assert.Equal("5 m", doc.Measurements[^1].Measurement.Text);
                Assert.Equal(1, doc.Measurements[^1].Page);

                await Measure(3, MeasureKind.Length, [(0, 0), (4, 0)]);
                Assert.Equal("40 mm", doc.Measurements[^1].Measurement.Text);
                doc.ScaleUnit = "m";
                doc.ScaleRatio = "100";
                doc.ApplyScaleCommand.Execute(null);
                Assert.Equal("4 m", doc.Measurements[^1].Measurement.Text);
                Assert.Equal("1:100", PageScales.Of(doc.History.Pages[3].Page.Dictionary)[^1].Measure!.Ratio);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// „Kreis/Bogen“ through the view (Ablaufplan 7.6 e; docs/FEHLERGEDAECHTNIS.md, No. 584): on the test plan (1:100) the right end of the
    /// circle, a point on the curve at 45° (snapped onto it, not to a corner), the top and the left end — three points do not end it any more;
    /// Enter does: the upper half circle, r 2.5 m, arc 7.85 m, 180°.
    /// </summary>
    [Fact]
    public void Arc_ThroughFourPoints_OneOnTheCurve_EndedWithEnter()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-bogen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                vm.MeasureCommand.Execute(null);
                doc.MeasureKind = MeasureKind.Radius;
                var at45 = (2 + 2.5 / Math.Sqrt(2), 6.5 + 2.5 / Math.Sqrt(2));
                var clicks = MeasurePlanTests.Clicks([(4.5, 6.5), at45, (2, 9), (-0.5, 6.5)], 100, 1);
                var middle = view.PagePointToView(0, clicks[1].X, clicks[1].Y);
                view.Offset += new Vector(middle.X - view.Bounds.Width / 2, middle.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                await KeepDrawing(doc, 0);
                foreach (var (x, y) in clicks)
                {
                    var at = view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                    window.MouseMove(at);
                    window.MouseDown(at, MouseButton.Left);
                    window.MouseUp(at, MouseButton.Left);
                    UiSession.Capture(window);
                    Thread.Sleep(50);
                }
                Assert.Empty(doc.Measurements);
                Assert.Equal(4, doc.MeasurePoints.Count);
                view.Focus();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                UiSession.Capture(window);
                var arc = Assert.Single(doc.Measurements).Measurement;
                Assert.Equal("r 2.5 m; ⌀ 5 m", arc.Text);
                Assert.Equal("7.85 m", arc.Arc!.LengthText);
                Assert.Equal("180°", arc.Arc.SweepText);
                Assert.Equal(DocumentViewModel.Detail(arc), doc.MeasureDetail);
                Assert.True(arc.Arc.Deviation < 0.002);   // the point on the curve lies on the circle (to 0.03 % of the radius)
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The cache of display lists is shared by all open documents and drops the least used lists when memory gets tight. A list dropped
    /// between showing the page and the clicks let the points stay where they were clicked — beside the corners, unsnapped (r 2.63 m
    /// instead of 2.5 m; in full test runs now and then, docs/FEHLERGEDAECHTNIS.md, No. 610). Now a click builds the list again and snaps.
    /// </summary>
    [Fact]
    public void Arc_ListDroppedFromTheSharedCache_ClicksStillSnap()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-bogen-verdraengt.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.True(doc.Pages.Count >= 2);
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                vm.MeasureCommand.Execute(null);
                doc.MeasureKind = MeasureKind.Radius;
                var at45 = (2 + 2.5 / Math.Sqrt(2), 6.5 + 2.5 / Math.Sqrt(2));
                var clicks = MeasurePlanTests.Clicks([(4.5, 6.5), at45, (2, 9), (-0.5, 6.5)], 100, 1);
                var middle = view.PagePointToView(0, clicks[1].X, clicks[1].Y);
                view.Offset += new Vector(middle.X - view.Bounds.Width / 2, middle.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                await doc.Renderer.GetDisplayList(doc.Pages[0].Key);
                // another page used last, then memory tight: the list of page 1 is dropped (the one used last stays)
                await doc.Renderer.GetDisplayList(doc.Pages[1].Key);
                doc.Renderer.Free(PdfKnead.Rendering.CachePool.Lists, long.MaxValue);
                Assert.Null(doc.Renderer.TryGetDisplayList(doc.Pages[0].Key));
                foreach (var (x, y) in clicks)
                {
                    var at = view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                    window.MouseDown(at, MouseButton.Left);
                    window.MouseUp(at, MouseButton.Left);
                    UiSession.Capture(window);
                }
                view.Focus();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                UiSession.Capture(window);
                Assert.Equal("r 2.5 m; ⌀ 5 m", Assert.Single(doc.Measurements).Measurement.Text);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The look of the measurements (Ablaufplan 7.6 e2; docs/FEHLERGEDAECHTNIS.md, No. 585): each measurement keeps the colour chosen when
    /// it was taken; the main switches turn the lengths of the pieces and the angles at the bends on and off at all measurements and are taken
    /// by the next ones; each measurement can be switched by itself. With PDFKNEAD_SCREENSHOTS the plan with them is saved (72-messen-…).
    /// </summary>
    [Fact]
    public void Look_ColourPerMeasurement_MainSwitchesAndEachByItself()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-darstellung.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1.25;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                // the middle of the plan in the middle of what is visible beside the area on the right
                var centre = MeasurePlanTests.Clicks([(5, 4.5)], 100, 1)[0];
                var middle = view.PagePointToView(0, centre.X, centre.Y);
                var panel = window.FindControl<Border>("MeasureHost")!;
                var visible = Math.Min(view.Bounds.Width, panel.TranslatePoint(new Point(0, 0), view)!.Value.X);
                view.Offset += new Vector(middle.X - visible / 2, middle.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                await KeepDrawing(doc, 0);

                void Measure(MeasureKind kind, (double X, double Y)[] metres, bool enter)
                {
                    doc.MeasureKind = kind;
                    foreach (var (x, y) in MeasurePlanTests.Clicks(metres, 100, 1))
                    {
                        var at = view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                        window.MouseMove(at);
                        window.MouseDown(at, MouseButton.Left);
                        window.MouseUp(at, MouseButton.Left);
                        UiSession.Capture(window);
                        Thread.Sleep(50);
                    }
                    if (!enter) return;
                    view.Focus();
                    window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                    UiSession.Capture(window);
                }

                Measure(MeasureKind.Length, [(0, 0), (4, 3)], false);
                doc.MeasureColor = DocumentViewModel.MeasureColors[2];
                Measure(MeasureKind.Polyline, [(6, 4), (8, 4), (8, 5.5), (11, 5.5)], true);
                doc.MeasureShowSegments = true;
                doc.MeasureColor = DocumentViewModel.MeasureColors[3];
                Measure(MeasureKind.Area, [(6, 0), (9, 0), (9, 3 * Math.Tan(Math.PI / 6))], true);
                doc.MeasureColor = DocumentViewModel.MeasureColors[4];
                Measure(MeasureKind.Radius, [(4.5, 6.5), (2, 9), (-0.5, 6.5), (2, 4)], true);
                Assert.Equal(4, doc.Measurements.Count);

                // each keeps its colour; the main switch for the lengths was on for the area and the arc, and switched all on
                Assert.Equal([0, 2, 3, 4], doc.Measurements.Select(r => DocumentViewModel.MeasureColors.ToList().IndexOf(r.Color!)));
                Assert.All(doc.Measurements, r => Assert.True(r.ShowSegments));
                Assert.Equal("6.5 m", doc.Measurements[1].Measurement.Text);
                Assert.Equal(["2 m", "1.5 m", "3 m"], doc.Measurements[1].Measurement.Segments);
                Assert.True(doc.Measurements[1].HasPieces);
                Assert.False(doc.Measurements[0].HasPieces);

                // angles: on for all with the main switch, then off at the polyline by itself
                doc.MeasureShowAngles = true;
                Assert.All(doc.Measurements, r => Assert.True(r.ShowAngles));
                doc.Measurements[1].ShowAngles = false;
                Assert.False(doc.Measurements[1].ShowAngles);
                Assert.True(doc.Measurements[2].ShowAngles);
                Assert.Equal(["30°", "90°", "60°"], doc.Measurements[2].Measurement.Corners);
                // a change of a measurement is drawn again
                var redrawn = 0;
                doc.MeasureChanged += () => redrawn++;
                doc.Measurements[0].Color = DocumentViewModel.MeasureColors[1];
                Assert.True(redrawn > 0);
                if (UiSession.ScreenshotFolder() is { } folder)
                {
                    window.MouseMove(new Point(5, 5));
                    UiSession.Capture(window).Save(Path.Combine(folder, "72-messen-darstellung.png"), PngBitmapEncoderOptions.Default);
                }

                // the main switch off: off everywhere
                doc.MeasureShowSegments = false;
                Assert.All(doc.Measurements, r => Assert.False(r.ShowSegments));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Choosing and changing a measurement (Ablaufplan 7.6 e3; docs/FEHLERGEDAECHTNIS.md, No. 586): a click on its value chooses it — the bar
    /// floats above it; its end dragged to the opposite corner snaps there (4 m becomes 5 m); colour from the bar; two decimals „5.00 m“; a
    /// polyline as an arc and back; Delete removes the chosen one; with snapping off a click beside a corner is not the corner, with Alt it is;
    /// a scale for all pages in one undo step.
    /// </summary>
    [Fact]
    public void Choose_DragAPoint_Bar_Decimals_Arc_Delete_Snap_AllPages()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-auswahl.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                var centre = MeasurePlanTests.Clicks([(5, 4.5)], 100, 1)[0];
                var middle = view.PagePointToView(0, centre.X, centre.Y);
                view.Offset += new Vector(middle.X - view.Bounds.Width / 2, middle.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                await KeepDrawing(doc, 0);
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                void Click(Point at, RawInputModifiers modifiers = RawInputModifiers.None)
                {
                    window.MouseMove(at, modifiers);
                    window.MouseDown(at, MouseButton.Left, modifiers);
                    window.MouseUp(at, MouseButton.Left, modifiers);
                    UiSession.Capture(window);
                    Thread.Sleep(50);
                }
                void Measure(MeasureKind kind, (double X, double Y)[] metres, RawInputModifiers modifiers = RawInputModifiers.None)
                {
                    doc.MeasureKind = kind;
                    // the first click with Ctrl: a new measurement begins there even where another one lies (Ablaufplan 7.6 l)
                    var first = true;
                    foreach (var (x, y) in MeasurePlanTests.Clicks(metres, 100, 1))
                    {
                        Click(At(x, y), first ? modifiers | RawInputModifiers.Control : modifiers);
                        first = false;
                    }
                    if (kind is MeasureKind.Length) return;
                    view.Focus();
                    window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                    UiSession.Capture(window);
                }

                // the width of the rectangle; a click on its value chooses it, the bar floats above it
                Measure(MeasureKind.Length, [(0, 0), (4, 0)]);
                var width = Assert.Single(doc.Measurements);
                Assert.Equal("4 m", width.Measurement.Text);
                var label = view.TranslatePoint(view.MeasureLabelCentre(width)!.Value, window)!.Value;
                Click(label);
                Assert.Same(width, doc.SelectedMeasurement);
                Assert.Single(doc.Measurements);                      // choosing measures nothing
                Thread.Sleep(100);
                UiSession.Capture(window);
                var bar = window.FindControl<Border>("MeasureBar")!;
                Assert.True(bar.IsVisible);
                var barBottom = bar.TranslatePoint(new Point(0, bar.Bounds.Height), window)!.Value.Y;
                Assert.True(barBottom < At(4, 0).Y, $"bar {barBottom} above the measurement {At(4, 0).Y}");

                // its end dragged onto the opposite corner: it snaps there — the diagonal, 5 m
                var handle = view.TranslatePoint(view.MeasureHandle(width, 1), window)!.Value;
                var target = At(MeasurePlanTests.Clicks([(4, 3)], 100, 1)[0].X, MeasurePlanTests.Clicks([(4, 3)], 100, 1)[0].Y);
                window.MouseMove(handle);
                window.MouseDown(handle, MouseButton.Left);
                window.MouseMove(new Point((handle.X + target.X) / 2, (handle.Y + target.Y) / 2));
                window.MouseMove(target);
                window.MouseUp(target, MouseButton.Left);
                UiSession.Capture(window);
                Assert.Equal("5 m", width.Measurement.Text);
                Assert.Single(doc.Measurements);
                Assert.Equal("5 m", doc.MeasureText);
                if (UiSession.ScreenshotFolder() is { } folder)
                {
                    Thread.Sleep(100);
                    UiSession.Capture(window);
                    UiSession.Capture(window).Save(Path.Combine(folder, "73-messen-auswahl.png"), PngBitmapEncoderOptions.Default);
                }

                // colour from the bar; two decimals for all
                doc.SetSelectedMeasureColorCommand.Execute(DocumentViewModel.MeasureColors[3]);
                Assert.Same(DocumentViewModel.MeasureColors[3], width.Color);
                doc.MeasureDecimals = DocumentViewModel.MeasureDecimalChoices[3];
                Assert.Equal("5.00 m", width.Measurement.Text);
                doc.MeasureDecimals = DocumentViewModel.MeasureDecimalChoices[0];
                Assert.Equal("5 m", width.Measurement.Text);

                // Esc ends the choice (the bar floats where the next points go); a first point of a new measurement ends it as well
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                UiSession.Capture(window);
                Assert.Null(doc.SelectedMeasurement);
                Assert.True(doc.IsMeasuring);
                doc.SelectedMeasurement = width;
                doc.MeasureAt(0, 10, 10);
                Assert.Null(doc.SelectedMeasurement);
                doc.CancelMeasure();
                UiSession.Capture(window);

                // a polyline as an arc and back
                Measure(MeasureKind.Polyline, [(6, 4), (8, 4), (8, 5.5), (11, 5.5)]);
                var line = doc.Measurements[^1];
                doc.SelectedMeasurement = line;                       // chosen in the list
                Assert.True(line.CanSwitchArc);
                doc.SwitchSelectedArcCommand.Execute(null);
                Assert.Equal(MeasureKind.Radius, line.Measurement.Kind);
                Assert.NotNull(line.Measurement.Arc);
                doc.SwitchSelectedArcCommand.Execute(null);
                Assert.Equal("6.5 m", line.Measurement.Text);

                // Delete removes the chosen one
                view.Focus();
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                UiSession.Capture(window);
                Assert.Same(width, Assert.Single(doc.Measurements));
                Assert.Null(doc.SelectedMeasurement);

                // snapping off: beside the corners is not 4 m; Alt snaps
                doc.MeasureSnap = false;
                Measure(MeasureKind.Length, [(0, 0), (4, 0)]);
                Assert.NotEqual("4 m", doc.Measurements[^1].Measurement.Text);
                Measure(MeasureKind.Length, [(0, 0), (4, 0)], RawInputModifiers.Alt);
                Assert.Equal("4 m", doc.Measurements[^1].Measurement.Text);

                // a scale for all pages, one undo step
                var before = doc.History.Pages.Select(p => PageScales.Of(p.Page.Dictionary).Count).ToList();
                doc.ScaleAllPages = true;
                doc.ScaleUnit = "m";
                doc.ScaleRatio = "50";
                doc.ApplyScaleCommand.Execute(null);
                Assert.All(doc.History.Pages, p => Assert.Equal("1:50", PageScales.Of(p.Page.Dictionary)[^1].Measure!.Ratio));
                Assert.Equal("2 m", doc.Measurements[^1].Measurement.Text);   // drawn at 1:100: 40 mm of paper, at 1:50 2 m
                vm.UndoCommand.Execute(null);
                Assert.Equal(before, doc.History.Pages.Select(p => PageScales.Of(p.Page.Dictionary).Count).ToList());
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Measurements as dimensions (Ablaufplan 7.6 f; docs/FEHLERGEDAECHTNIS.md, No. 587): the chosen one from its bar — a dimension element on
    /// the page, one undo step, in the list as a dimension now (7.6 h, No. 605); the others with „Alle als Bemaßung einfügen“ in one step; saved
    /// and opened again they are dimensions of PDF Knead with their values, and the line dimension measures 5 m with its own /Measure.
    /// </summary>
    [Fact]
    public void InsertAsDimensions_OneAndAll_UndoAndSaved()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-bemassen.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "pruefplan-bemassen gespeichert.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                (double X, double Y) At((double X, double Y) m) => MeasurePlanTests.At(m, 100, 1);
                void Measure(MeasureKind kind, params (double X, double Y)[] metres)
                {
                    doc.MeasureKind = kind;
                    foreach (var m in metres)
                    {
                        var (x, y) = At(m);
                        doc.MeasureAt(0, x, y);
                    }
                    doc.FinishMeasure();
                }
                Measure(MeasureKind.Length, (0, 0), (4, 3));
                Measure(MeasureKind.Area, (0, 0), (4, 0), (4, 3), (0, 3));
                Measure(MeasureKind.Polyline, (6, 4), (8, 4), (8, 5.5), (11, 5.5));
                Assert.Equal(3, doc.Measurements.Count);
                int Dimensions() => doc.History.Pages[0].Elements.OfType<DimensionElement>().Count();

                // the chosen one from the bar
                doc.SelectedMeasurement = doc.Measurements[0];
                UiSession.Capture(window);
                Assert.True(window.FindControl<Button>("MeasureBarInsert")!.IsVisible);
                doc.InsertSelectedDimensionCommand.Execute(null);
                Assert.Equal(1, Dimensions());
                Assert.Equal(3, doc.Measurements.Count);
                Assert.Equal(1, doc.Measurements.Count(r => r.IsSaved));
                Assert.True(doc.SelectedMeasurement?.IsSaved);
                var line = doc.History.Pages[0].Elements.OfType<DimensionElement>().Single();
                Assert.Equal("5 m", line.Text);
                Assert.Equal(MeasureKind.Length, line.Kind);
                vm.UndoCommand.Execute(null);
                Assert.Equal(0, Dimensions());
                vm.RedoCommand.Execute(null);
                Assert.Equal(1, Dimensions());

                // all others in one step
                doc.InsertAllDimensionsCommand.Execute(null);
                Assert.Equal(3, Dimensions());
                Assert.Equal(3, doc.Measurements.Count);
                Assert.All(doc.Measurements, r => Assert.True(r.IsSaved));
                vm.UndoCommand.Execute(null);
                Assert.Equal(1, Dimensions());
                vm.RedoCommand.Execute(null);
                Assert.Equal(3, Dimensions());
                UiSession.Capture(window);

                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.SaveAsCommand.ExecuteAsync(null);
                using var saved = PdfDocument.Open(target);
                var own = ElementStore.For(saved, saved.Pages[0]).Elements.OfType<DimensionElement>().ToList();
                Assert.Equal(["12 m²; 14 m", "5 m", "6.5 m"], own.Select(d => d.Text).Order(StringComparer.Ordinal));
                var annots = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
                var written = Enumerable.Range(0, annots.Count).Select(i => (PdfDictionary)annots.Get(i)).Single(a => a.GetName(PdfName.Get("IT"))?.Value == "LineDimension");
                var measure = RectilinearMeasure.Read(written.GetDictionary(PdfName.Get("Measure")))!;
                var l = written.GetArray(PdfName.Get("L"))!;
                Assert.Equal("5 m", measure.Distance.Format(measure.Length([(l.GetNumber(0)!.Value, l.GetNumber(1)!.Value), (l.GetNumber(2)!.Value, l.GetNumber(3)!.Value)])!.Value));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Saved dimensions are measurements (Ablaufplan 7.6 h; docs/FEHLERGEDAECHTNIS.md, No. 605; decided on 6.11.2026): on the test
    /// plan a line dimension of another program (4 m, red) is in the list with „Messen“ on. A length measured and inserted stays in the list as a
    /// dimension, chosen; its end dragged to (8, 6) m — one undo step, the dimension on the page measures 10 m, undo and redo keep the row
    /// chosen; a colour from the bar is saved. The point of the foreign one moved: a dimension of PDF Knead in its place, red as before, 5 m.
    /// A scale of 1 : 50 set for the page measures both anew in the same undo step.
    /// The view leaves bound dimensions out („Messen“ draws them). Delete removes it from the page (undo brings it back); the tool off, the
    /// dimensions stay on the page; saved and opened, the annotation of the other program is gone and both are dimensions of PDF Knead.
    /// Before, dimensions could only be moved and deleted, a foreign one not even that from „Messen“.
    /// </summary>
    [Fact]
    public void SavedDimensions_AreMeasurements_ChangedDeletedSaved_ForeignToo()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(foreignDimension: true), "pruefplan-bemassungen-aendern.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "pruefplan-bemassungen-aendern gespeichert.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                (double X, double Y) At((double X, double Y) m) => MeasurePlanTests.At(m, 100, 1);
                IReadOnlyList<PageElement> Elements() => doc.History.Pages[0].Elements;

                // the dimension of another program is a measurement, as the file measures it
                var foreign = Assert.Single(doc.Measurements);
                Assert.True(foreign.IsSaved);
                Assert.IsType<ForeignAnnotation>(foreign.Element);
                Assert.Equal("4 m", foreign.Measurement.Text);
                Assert.Equal(0xFF0000, foreign.Rgb);
                Assert.True(doc.IsBoundDimension(foreign.Element!));

                // a length measured and inserted: in the list as a dimension, chosen, the bar without „Als Bemaßung einfügen“
                doc.MeasureKind = MeasureKind.Length;
                foreach (var m in new[] { (0.0, 0.0), (4.0, 3.0) })
                {
                    var (x, y) = At(m);
                    doc.MeasureAt(0, x, y);
                }
                doc.SelectedMeasurement = doc.Measurements[^1];
                doc.InsertSelectedDimensionCommand.Execute(null);
                Assert.Equal(2, doc.Measurements.Count);
                var length = doc.SelectedMeasurement!;
                Assert.True(length.IsSaved);
                Assert.IsType<DimensionElement>(length.Element);
                Assert.Equal("5 m", length.Measurement.Text);
                UiSession.Capture(window);
                Assert.True(window.FindControl<Border>("MeasureBar")!.IsVisible);
                Assert.False(window.FindControl<Button>("MeasureBarInsert")!.IsVisible);

                // its end dragged to (8, 6) m — saved when let go, one undo step
                var before = doc.History.UndoDescription;
                foreach (var m in new[] { (6.0, 4.5), (8.0, 6.0) })
                {
                    var (x, y) = At(m);
                    doc.MoveMeasurePoint(length, 1, x, y);
                }
                Assert.Equal("5 m", Elements().OfType<DimensionElement>().Single().Text);   // not yet: only when let go
                doc.EndMeasureDrag(length);
                Assert.Equal("10 m", Elements().OfType<DimensionElement>().Single().Text);
                Assert.Equal(Tr.T("Bemaßung ändern"), doc.History.UndoDescription);
                Assert.Same(length, doc.SelectedMeasurement);
                Assert.Equal("10 m", length.Measurement.Text);
                vm.UndoCommand.Execute(null);
                Assert.Equal("5 m", Elements().OfType<DimensionElement>().Single().Text);
                Assert.Equal("5 m", length.Measurement.Text);
                Assert.Same(length, doc.SelectedMeasurement);
                Assert.Equal(before, doc.History.UndoDescription);
                vm.RedoCommand.Execute(null);
                Assert.Equal("10 m", length.Measurement.Text);

                // a colour from the bar: saved
                doc.SetSelectedMeasureColorCommand.Execute(DocumentViewModel.MeasureColors[2]);
                Assert.Equal(0x1F4E9A, Elements().OfType<DimensionElement>().Single().Color);
                Assert.Same(length, doc.SelectedMeasurement);

                // the foreign one: a point moved — a dimension of PDF Knead in its place, red as before
                doc.SelectedMeasurement = foreign;
                var (fx, fy) = At((4, 0));
                doc.MoveMeasurePoint(foreign, 1, fx, fy);
                doc.EndMeasureDrag(foreign);
                Assert.DoesNotContain(Elements(), e => e is ForeignAnnotation);
                var converted = Assert.Single(Elements().OfType<DimensionElement>(), d => d.Color == 0xFF0000);
                Assert.Equal("5 m", converted.Text);
                Assert.Same(foreign, doc.SelectedMeasurement);
                Assert.Same(converted, foreign.Element);

                // a scale of 1 : 50 for the page: the dimensions on it measure anew, in the same undo step
                doc.ScaleRatio = "50";
                doc.ApplyScaleCommand.Execute(null);
                Assert.Equal(["2.5 m", "5 m"], Elements().OfType<DimensionElement>().Select(d => d.Text).Order(StringComparer.Ordinal));
                Assert.Equal("2.5 m", foreign.Measurement.Text);
                Assert.Equal(Tr.T("Maßstab festlegen"), doc.History.UndoDescription);
                vm.UndoCommand.Execute(null);
                Assert.Equal(["10 m", "5 m"], Elements().OfType<DimensionElement>().Select(d => d.Text).Order(StringComparer.Ordinal));
                Assert.Equal("5 m", foreign.Measurement.Text);
                Assert.Same(foreign, doc.SelectedMeasurement);

                // the view leaves them out: „Messen“ draws them
                Assert.All(Elements().OfType<DimensionElement>(), d => Assert.True(doc.IsBoundDimension(d)));
                UiSession.Capture(window);
                if (UiSession.ScreenshotFolder() is { } folder) UiSession.Capture(window).Save(Path.Combine(folder, "78-bemassung-bearbeiten.png"), PngBitmapEncoderOptions.Default);

                // deleted from the bar: gone from the page, undo brings it back
                doc.DeleteSelectedMeasurementCommand.Execute(null);
                Assert.Single(Elements().OfType<DimensionElement>());
                Assert.Single(doc.Measurements);
                Assert.Null(doc.SelectedMeasurement);
                vm.UndoCommand.Execute(null);
                Assert.Equal(2, Elements().OfType<DimensionElement>().Count());
                Assert.Equal(2, doc.Measurements.Count);

                // the tool off: the dimensions stay on the page and are drawn by the view again
                vm.MeasureCommand.Execute(null);
                Assert.Empty(doc.Measurements);
                Assert.Equal(2, Elements().OfType<DimensionElement>().Count());
                Assert.All(Elements(), e => Assert.False(doc.IsBoundDimension(e)));

                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.SaveAsCommand.ExecuteAsync(null);
                using var saved = PdfDocument.Open(target);
                var annots = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
                var lines = Enumerable.Range(0, annots.Count).Select(i => (PdfDictionary)annots.Get(i)).Where(a => a.GetName(PdfName.Subtype)?.Value == "Line").ToList();
                Assert.Equal(2, lines.Count);
                Assert.All(lines, a => Assert.NotNull(ElementStore.DataOf(a)));
                Assert.Equal(["10 m", "5 m"], ElementStore.For(saved, saved.Pages[0]).Elements.OfType<DimensionElement>().Select(d => d.Text).Order(StringComparer.Ordinal));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Measurements follow undo, redo and pages rearranged (docs/FEHLERGEDAECHTNIS.md, No. 598): on the page without a scale 40 mm; „1 : 100“
    /// makes it 4 m, undo 40 mm again, redo 4 m; a page deleted before it — the measurement follows its page (index 3 → 2); its page deleted —
    /// the measurement goes. Before, undo left 4 m (and a dimension inserted then carried the scale undone).
    /// </summary>
    [Fact]
    public void Measurements_FollowUndoRedoAndPages()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-rueckgaengig.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                UiSession.Capture(window);
                vm.MeasureCommand.Execute(null);
                doc.MeasureKind = MeasureKind.Length;
                var (ax, ay) = MeasurePlanTests.At((0, 0), 100, 1);
                var (bx, by) = MeasurePlanTests.At((4, 0), 100, 1);
                doc.MeasureAt(2, ax, ay);
                doc.MeasureAt(2, bx, by);
                var row = Assert.Single(doc.Measurements);
                Assert.Equal("40 mm", row.Measurement.Text);
                doc.ScaleUnit = "m";
                doc.ScaleRatio = "100";
                doc.ApplyScaleCommand.Execute(null);
                Assert.Equal("4 m", row.Measurement.Text);
                vm.UndoCommand.Execute(null);
                Assert.Equal("40 mm", row.Measurement.Text);
                Assert.Equal("40 mm", doc.MeasureText);
                vm.RedoCommand.Execute(null);
                Assert.Equal("4 m", row.Measurement.Text);

                Assert.True(doc.History.Delete([0]));
                Assert.Equal(1, row.Page);
                Assert.Equal("4 m", row.Measurement.Text);
                Assert.True(doc.History.Delete([1]));
                Assert.Empty(doc.Measurements);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// „Messen“ is on the Start tab among the tools, with its own ruler icon (decided on 6.11.2026): a click there switches the tool on,
    /// the panel appears; another click switches it off.
    /// </summary>
    [Fact]
    public void MeasureButton_OnTheStartTab_AmongTheTools()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-knopf.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                vm.RibbonTab = RibbonTab.Start;
                UiSession.Capture(window);
                var button = window.GetVisualDescendants().OfType<RibbonToggleButton>().Single(b => b.Label == Tr.T("Messen") && b.IsEffectivelyVisible);
                Assert.Equal("MeasureToolButton", button.Name);
                void Press()
                {
                    UiSession.Capture(window);
                    var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(at);
                    window.MouseDown(at, MouseButton.Left);
                    window.MouseUp(at, MouseButton.Left);
                    UiSession.Capture(window);
                }
                Press();
                Assert.True(vm.SelectedDocument!.IsMeasuring);
                Assert.True(window.FindControl<Border>("MeasureHost")!.IsVisible);
                Press();
                Assert.False(vm.SelectedDocument!.IsMeasuring);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Arcs in polylines and areas (Ablaufplan 7.6 j; docs/FEHLERGEDAECHTNIS.md, No. 620; decided on 8.11.2026). On the plan (lines
    /// (100,100) → (400,100) → (400,400), 0.01 m per point): a click, Ctrl+Shift+click above the middle, a click on the corner (400,400) — a
    /// half circle of 1.5 m (π · 1.5 m) arriving straight down; let go, the next click is straight on down (3 m, the bend 180°). Backspace takes an arc point back with its
    /// mark; Ctrl+Shift with „Länge“ sets no arc point. An area closed by an arc: a square of 3 m with a half circle below. With
    /// PDFKNEAD_SCREENSHOTS the page is saved (72-messen-boegen.png).
    /// </summary>
    [Fact]
    public void Arcs_CtrlShiftClick_PolylineAndArea()
    {
        var pdf = TestFiles.Write(Plan(), "plan-boegen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                var target = view.PagePointToView(0, 250, 300);
                view.Offset += new Vector(target.X - view.Bounds.Width / 2, target.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                await doc.Renderer.GetDisplayList(doc.Pages[0].Key);
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                void Click(double x, double y, RawInputModifiers modifiers = RawInputModifiers.None)
                {
                    window.MouseMove(At(x, y), modifiers);
                    window.MouseDown(At(x, y), MouseButton.Left, modifiers);
                    window.MouseUp(At(x, y), MouseButton.Left, modifiers);
                    UiSession.Capture(window);
                    Thread.Sleep(50);
                }
                const RawInputModifiers arc = RawInputModifiers.Control | RawInputModifiers.Shift;

                vm.MeasureCommand.Execute(null);
                await KeepDrawing(doc, 0);   // the drawing kept by the document, as moving the mouse over the page does (No. 625)
                UiSession.Capture(window);
                doc.MeasureShowSegments = true;
                doc.MeasureShowAngles = true;

                // Ctrl+Shift with „Länge“: no arc point
                Click(100, 400);
                Click(250, 550, arc);
                Assert.Empty(doc.Measurements[^1].Measurement.ArcPoints);
                Assert.Equal(MeasureKind.Length, doc.Measurements[^1].Measurement.Kind);

                // a polyline: a point (where the length begins: with Ctrl, 7.6 l), an arc point above the middle, the corner — then straight down
                doc.MeasureKind = MeasureKind.Polyline;
                Click(100, 400, RawInputModifiers.Control);
                Click(250, 550, arc);
                Assert.Equal([1], doc.MeasureArcPoints);
                view.Focus();
                window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
                Assert.Empty(doc.MeasureArcPoints);
                Assert.Single(doc.MeasurePoints);
                Thread.Sleep(700);   // the same spot again at once would be a double-click
                Click(250, 550, arc);
                Click(400, 400, arc);   // still held: the end of the arc, no arc point of its own
                Assert.Equal([1], doc.MeasureArcPoints);
                Click(400, 100);
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                UiSession.Capture(window);
                var polyline = doc.Measurements[^1].Measurement;
                Assert.Equal(MeasureKind.Polyline, polyline.Kind);
                Assert.Equal([1], polyline.ArcPoints);
                Assert.Equal(3 + 1.5 * Math.PI, polyline.Value!.Value, 6);
                Assert.Equal(2, polyline.Outline!.Pieces.Count);
                Assert.Equal("180°", polyline.Corners[2]);   // the half circle arrives straight down, the line goes on straight down
                if (UiSession.ScreenshotFolder() is { } folder) UiSession.Capture(window).Save(Path.Combine(folder, "72-messen-boegen.png"), PngBitmapEncoderOptions.Default);

                // an area closed by an arc: a square of 3 m, from its lower left corner an arc below back to the start
                doc.MeasureKind = MeasureKind.Area;
                foreach (var (x, y, isArc) in new[] { (100.0, 500.0, false), (100.0, 800.0, false), (400.0, 800.0, false), (400.0, 500.0, false), (250.0, 350.0, true) })
                    doc.MeasureAt(0, x, y, isArc);
                Assert.True(doc.FinishMeasure());
                var area = doc.Measurements[^1].Measurement;
                Assert.Equal([4], area.ArcPoints);
                Assert.Equal(9 + Math.PI * 1.5 * 1.5 / 2, area.Value!.Value, 6);
                Assert.Equal(9 + 1.5 * Math.PI, area.Outline!.Length, 6);
            }
            finally
            {
                vm.AskChoices = _ => Task.FromResult(new ChoiceAnswer(2, false));
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Editing the points of a finished measurement and tangent arcs (Ablaufplan 7.6 k; docs/FEHLERGEDAECHTNIS.md, No. 621; wishes of the
    /// user, 8.11.2026). On the plan (0.01 m per point): a polyline (100,400) → (400,400) → (400,100), chosen; Ctrl+click on its first piece puts
    /// a point there — the value stays 6 m, the new point is chosen; Del takes it out again; a click on the corner's handle and Del join its
    /// neighbours straight (4.24 m); a polyline of two points keeps them (a note). As a dimension, a point put in is one undo step. Then a
    /// polyline whose third click is Ctrl+Alt: the quarter circle that goes on without a bend (2 + π/2 m, the bend 180°).
    /// </summary>
    [Fact]
    public void EditPoints_CtrlClickInsert_ClickAndDel_TangentArc()
    {
        var pdf = TestFiles.Write(Plan(), "plan-punkte.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                var target = view.PagePointToView(0, 250, 450);
                view.Offset += new Vector(target.X - view.Bounds.Width / 2, target.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                await doc.Renderer.GetDisplayList(doc.Pages[0].Key);
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                void Click(double x, double y, RawInputModifiers modifiers = RawInputModifiers.None)
                {
                    window.MouseMove(At(x, y), modifiers);
                    window.MouseDown(At(x, y), MouseButton.Left, modifiers);
                    window.MouseUp(At(x, y), MouseButton.Left, modifiers);
                    UiSession.Capture(window);
                    Thread.Sleep(600);   // apart from the click before: no double-click
                }
                void Key(Key key, PhysicalKey physical)
                {
                    view.Focus();
                    window.KeyPress(key, RawInputModifiers.None, physical, null);
                    UiSession.Capture(window);
                }

                vm.MeasureCommand.Execute(null);
                await KeepDrawing(doc, 0);   // the drawing kept by the document, as moving the mouse over the page does (No. 625)
                UiSession.Capture(window);
                doc.MeasureKind = MeasureKind.Polyline;
                foreach (var (x, y) in new[] { (100.0, 400.0), (400.0, 400.0), (400.0, 100.0) }) doc.MeasureAt(0, x, y);
                Assert.True(doc.FinishMeasure());
                var row = doc.Measurements[^1];
                doc.SelectedMeasurement = row;
                UiSession.Capture(window);

                // Ctrl+click on the first piece (a little beside it): a point on the line, the value the same, the new point chosen
                Click(250, 402, RawInputModifiers.Control);
                Assert.Equal(4, row.Measurement.Points.Count);
                Assert.Equal((250.0, 400.0), (Math.Round(row.Measurement.Points[1].X, 6), Math.Round(row.Measurement.Points[1].Y, 6)));
                Assert.Equal("6 m", row.Measurement.Text);
                Assert.Equal(1, doc.SelectedMeasurePoint);
                Key(Avalonia.Input.Key.Delete, PhysicalKey.Delete);
                Assert.Equal(3, row.Measurement.Points.Count);
                Assert.Same(row, doc.SelectedMeasurement);   // the point went, not the measurement

                // the corner chosen with a click on its handle, Del: its neighbours joined straight
                Click(400, 400);
                Assert.Equal(1, doc.SelectedMeasurePoint);
                Key(Avalonia.Input.Key.Delete, PhysicalKey.Delete);
                Assert.Equal(2, row.Measurement.Points.Count);
                Assert.Equal(3 * Math.Sqrt(2), row.Measurement.Value!.Value, 6);
                // two points are the least: a note, both stay
                Click(100, 400);
                Key(Avalonia.Input.Key.Delete, PhysicalKey.Delete);
                Assert.Equal(2, row.Measurement.Points.Count);
                Assert.Equal(Tr.T("Ein Linienzug braucht mindestens zwei Punkte."), vm.Message);

                // as a dimension: a point put in is one undo step
                doc.InsertSelectedDimensionCommand.Execute(null);
                var saved = Assert.Single(doc.Measurements, r => r.IsSaved);
                doc.SelectedMeasurement = saved;
                UiSession.Capture(window);
                Click(250, 250, RawInputModifiers.Control);
                Assert.Equal(3, saved.Measurement.Points.Count);
                Assert.Equal(3, doc.History.Pages[0].Elements.OfType<DimensionElement>().Single().Points.Count);
                vm.UndoCommand.Execute(null);
                Assert.Equal(2, doc.History.Pages[0].Elements.OfType<DimensionElement>().Single().Points.Count);

                // a tangent arc: straight to the right, then Ctrl+Alt+click — the quarter circle round (300, 700) going on without a bend
                doc.SelectedMeasurement = null;
                Click(100, 600);
                Click(300, 600);
                Click(400, 700, RawInputModifiers.Control | RawInputModifiers.Alt);
                Key(Avalonia.Input.Key.Enter, PhysicalKey.Enter);
                var tangent = doc.Measurements[^1].Measurement;
                Assert.Equal([2], tangent.ArcPoints);
                Assert.Equal(2 + Math.PI / 2, tangent.Value!.Value, 6);
                Assert.Equal("180°", tangent.Corners[1]);
            }
            finally
            {
                vm.AskChoices = _ => Task.FromResult(new ChoiceAnswer(2, false));
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Choosing by the line, dragging a point at once, going on with a polyline (Ablaufplan 7.6 l; docs/FEHLERGEDAECHTNIS.md, No. 623; wishes of
    /// the user, 8.11.2026). A finished polyline (100,400) → (400,400) → (400,100): a click on its line chooses it (no new measurement; with Ctrl a
    /// new one begins there); a press on
    /// a point of it, not chosen, drags the point; a double-click on its line continues it — a click near its end goes on there; „Fortsetzen“
    /// in the bar and a click near its start goes on before the start; „Verwerfen“ in the question of Esc leaves it as it was; as a dimension,
    /// continuing is one undo step. Before: only a click on the value chose it, and a polyline could not be continued.
    /// </summary>
    [Fact]
    public void ChooseByLine_DragAPointAtOnce_ContinueAtEndAndStart()
    {
        var pdf = TestFiles.Write(Plan(), "plan-fortsetzen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                var target = view.PagePointToView(0, 330, 360);
                view.Offset += new Vector(target.X - view.Bounds.Width / 2, target.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                await doc.Renderer.GetDisplayList(doc.Pages[0].Key);
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                void Click(double x, double y, int times = 1)
                {
                    window.MouseMove(At(x, y));
                    for (var k = 0; k < times; k++)
                    {
                        window.MouseDown(At(x, y), MouseButton.Left);
                        window.MouseUp(At(x, y), MouseButton.Left);
                    }
                    UiSession.Capture(window);
                    Thread.Sleep(600);   // apart from the next click: no double-click by chance
                }
                void Key(Key key, PhysicalKey physical)
                {
                    view.Focus();
                    window.KeyPress(key, RawInputModifiers.None, physical, null);
                    UiSession.Capture(window);
                }
                var asked = new List<ChoiceRequest>();
                var answer = new ChoiceAnswer(3, false);   // „Verwerfen“
                vm.AskChoices = request =>
                {
                    asked.Add(request);
                    return Task.FromResult(answer);
                };

                vm.MeasureCommand.Execute(null);
                await KeepDrawing(doc, 0);   // the drawing kept by the document, as moving the mouse over the page does (No. 625)
                UiSession.Capture(window);
                doc.MeasureKind = MeasureKind.Polyline;
                foreach (var (x, y) in new[] { (100.0, 400.0), (400.0, 400.0), (400.0, 100.0) }) doc.MeasureAt(0, x, y);
                Assert.True(doc.FinishMeasure());
                var row = Assert.Single(doc.Measurements);

                // a click on its line chooses it — no new measurement begins there
                Click(250, 401);
                Assert.Same(row, doc.SelectedMeasurement);
                Assert.Empty(doc.MeasurePoints);
                Key(Avalonia.Input.Key.Escape, PhysicalKey.Escape);
                Assert.Null(doc.SelectedMeasurement);
                // with Ctrl a new measurement begins there instead (design decision)
                window.MouseMove(At(250, 401), RawInputModifiers.Control);
                window.MouseDown(At(250, 401), MouseButton.Left, RawInputModifiers.Control);
                window.MouseUp(At(250, 401), MouseButton.Left, RawInputModifiers.Control);
                UiSession.Capture(window);
                Thread.Sleep(600);
                Assert.Single(doc.MeasurePoints);
                Assert.Null(doc.SelectedMeasurement);
                doc.CancelMeasure();

                // a press on a point of it (not chosen) drags the point at once
                window.MouseMove(At(100, 400));
                window.MouseDown(At(100, 400), MouseButton.Left);
                window.MouseMove(At(100, 450));
                window.MouseMove(At(100, 500));
                window.MouseUp(At(100, 500), MouseButton.Left);
                UiSession.Capture(window);
                Thread.Sleep(600);
                Assert.Same(row, doc.SelectedMeasurement);
                Assert.Equal((100.0, 500.0), (Math.Round(row.Measurement.Points[0].X, 6), Math.Round(row.Measurement.Points[0].Y, 6)));
                Key(Avalonia.Input.Key.Escape, PhysicalKey.Escape);

                // a double-click on its line continues it; a click near its end goes on there
                Click(400, 250, times: 2);
                Assert.True(doc.IsContinuing(row));
                Assert.Equal(3, doc.MeasurePoints.Count);
                Click(550, 200);
                Key(Avalonia.Input.Key.Enter, PhysicalKey.Enter);
                Assert.False(doc.IsContinuing(row));
                Assert.Same(row, Assert.Single(doc.Measurements));
                Assert.Equal(4, row.Measurement.Points.Count);
                Assert.Equal((550.0, 200.0), (Math.Round(row.Measurement.Points[^1].X, 6), Math.Round(row.Measurement.Points[^1].Y, 6)));

                // „Fortsetzen“ in the bar, a click near its start: before the start
                doc.SelectedMeasurement = row;
                UiSession.Capture(window);
                Assert.True(window.FindControl<Avalonia.Controls.Button>("MeasureBarContinue")!.IsVisible);
                doc.ContinueSelectedMeasurementCommand.Execute(null);
                Click(100, 560);
                Key(Avalonia.Input.Key.Enter, PhysicalKey.Enter);
                Assert.Equal(5, row.Measurement.Points.Count);
                Assert.Equal((100.0, 560.0), (Math.Round(row.Measurement.Points[0].X, 6), Math.Round(row.Measurement.Points[0].Y, 6)));
                Assert.Equal((550.0, 200.0), (Math.Round(row.Measurement.Points[^1].X, 6), Math.Round(row.Measurement.Points[^1].Y, 6)));

                // Esc → „Verwerfen“: the continuing goes, the polyline stays as it was
                var before = row.Measurement;
                doc.ContinueMeasurement(row);
                Click(560, 260);
                Key(Avalonia.Input.Key.Escape, PhysicalKey.Escape);
                Assert.Equal(Tr.T("Messung abschließen?"), asked[^1].Heading);
                Assert.False(doc.IsContinuing(row));
                Assert.Same(before, row.Measurement);

                // as a dimension: continuing is one undo step (no „in die PDF einfügen“ in the question: it is there already)
                doc.SelectedMeasurement = row;
                doc.InsertSelectedDimensionCommand.Execute(null);
                var saved = Assert.Single(doc.Measurements);
                Assert.True(saved.IsSaved);
                doc.ContinueMeasurement(saved);
                Click(560, 260);
                Key(Avalonia.Input.Key.Enter, PhysicalKey.Enter);
                Assert.Equal(6, doc.History.Pages[0].Elements.OfType<DimensionElement>().Single().Points.Count);
                vm.UndoCommand.Execute(null);
                Assert.Equal(5, doc.History.Pages[0].Elements.OfType<DimensionElement>().Single().Points.Count);
                doc.ContinueMeasurement(doc.Measurements.Single());
                Click(560, 260);
                answer = new ChoiceAnswer(0, false);
                Key(Avalonia.Input.Key.Escape, PhysicalKey.Escape);
                Assert.Equal([Tr.T("Abschließen"), Tr.T("Verwerfen")], asked[^1].Answers);
                doc.CancelMeasure();
            }
            finally
            {
                vm.AskChoices = _ => Task.FromResult(new ChoiceAnswer(2, false));
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// While „Messen“ is on, the drawing of a page measured on is kept by the document (docs/FEHLERGEDAECHTNIS.md, No. 625): the mouse moved
    /// over the page once, then memory tight — the shared cache drops its list —, the clicks still snap at once, without the list being built
    /// again (it stays out of the cache). Before, each dropped list had to be built again for a click, which waited a second at most: under the
    /// load of a full test run the measuring tests failed now and then, unsnapped.
    /// </summary>
    [Fact]
    public void KeptDrawing_ClicksSnapWithoutBuildingAgain()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-gehalten.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                vm.MeasureCommand.Execute(null);
                doc.MeasureKind = MeasureKind.Radius;
                var at45 = (2 + 2.5 / Math.Sqrt(2), 6.5 + 2.5 / Math.Sqrt(2));
                var clicks = MeasurePlanTests.Clicks([(4.5, 6.5), at45, (2, 9), (-0.5, 6.5)], 100, 1);
                var middle = view.PagePointToView(0, clicks[1].X, clicks[1].Y);
                view.Offset += new Vector(middle.X - view.Bounds.Width / 2, middle.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                await doc.Renderer.GetDisplayList(doc.Pages[0].Key);
                Point At((double X, double Y) p) => view.TranslatePoint(view.PagePointToView(0, p.X, p.Y), window)!.Value;
                // the mouse over the page: its drawing is kept
                window.MouseMove(At(clicks[0]));
                UiSession.Capture(window);
                Assert.NotNull(doc.KeptSnapList(doc.Pages[0].Key));
                // another page used last, then memory tight: the shared cache drops the list of page 1 (the one used last stays)
                await doc.Renderer.GetDisplayList(doc.Pages[1].Key);
                doc.Renderer.Free(PdfKnead.Rendering.CachePool.Lists, long.MaxValue);
                Assert.Null(doc.Renderer.TryGetDisplayList(doc.Pages[0].Key));
                foreach (var click in clicks)
                {
                    window.MouseMove(At(click));
                    window.MouseDown(At(click), MouseButton.Left);
                    window.MouseUp(At(click), MouseButton.Left);
                    UiSession.Capture(window);
                }
                view.Focus();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                UiSession.Capture(window);
                Assert.Equal("r 2.5 m; ⌀ 5 m", Assert.Single(doc.Measurements).Measurement.Text);
                Assert.Null(doc.Renderer.TryGetDisplayList(doc.Pages[0].Key));   // not built again: the kept one did
                // the tool off: the drawing goes back to the cache
                vm.AskChoices = _ => Task.FromResult(new ChoiceAnswer(2, false));
                vm.MeasureCommand.Execute(null);
                Assert.Null(doc.KeptSnapList(doc.Pages[0].Key));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Calibrating with a line (Ablaufplan 7.6 g; docs/FEHLERGEDAECHTNIS.md, No. 601): on the page without a scale „Kalibrieren“, the
    /// magnifier follows the mouse; start and end on the width of the rectangle (snapped); then the cursor is in the length box, „4“ m gives
    /// 1 : 100, „Maßstab festlegen“ writes it — the width measures 4 m. Esc while setting the points drops the line. With PDFKNEAD_SCREENSHOTS
    /// the panel and the magnifier are saved (74-messen-kalibrieren.png).
    /// </summary>
    [Fact]
    public void Calibrate_WithMagnifier_LineLengthAndUnit()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-kalibrieren-lupe.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1.25;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                const int page = 2;
                var centre = MeasurePlanTests.At((2, 1.5), 100, 1);
                var middle = view.PagePointToView(page, centre.X, centre.Y);
                var panel = window.FindControl<Border>("MeasureHost")!;
                var visible = Math.Min(view.Bounds.Width, panel.TranslatePoint(new Point(0, 0), view)!.Value.X);
                view.Offset += new Vector(middle.X - visible / 2, middle.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                await KeepDrawing(doc, page);
                Point At((double X, double Y) metres)
                {
                    var c = MeasurePlanTests.Clicks([metres], 100, 1)[0];
                    return view.TranslatePoint(view.PagePointToView(page, c.X, c.Y), window)!.Value;
                }

                // Esc while setting the points drops it
                doc.StartCalibrationCommand.Execute(null);
                Assert.True(doc.IsCalibrating);
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(doc.IsCalibrating);
                Assert.True(doc.IsMeasuring);

                window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CalibrateButton").Command!.Execute(null);
                Assert.True(doc.IsCalibrating);
                window.MouseMove(At((0, 0)));
                UiSession.Capture(window);
                Assert.True(view.LoupeShown);
                window.MouseDown(At((0, 0)), MouseButton.Left);
                window.MouseUp(At((0, 0)), MouseButton.Left);
                Thread.Sleep(50);
                window.MouseMove(At((3.6, 0.3)));
                window.MouseMove(At((4, 0)));
                UiSession.Capture(window);
                if (UiSession.ScreenshotFolder() is { } folder) UiSession.Capture(window).Save(Path.Combine(folder, "74-messen-kalibrieren.png"), PngBitmapEncoderOptions.Default);
                window.MouseDown(At((4, 0)), MouseButton.Left);
                window.MouseUp(At((4, 0)), MouseButton.Left);
                UiSession.Capture(window);
                Assert.False(doc.IsCalibrating);
                Assert.True(doc.HasCalibrationLine);
                Assert.Equal(page, doc.CalibrationPage);
                Assert.Contains("40 mm", doc.CalibrationLineText);
                for (var i = 0; i < 5 && window.FocusManager?.GetFocusedElement() is not TextBox { Name: "CalibrationLengthBox" }; i++)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    UiSession.Capture(window);
                }
                Assert.Equal("CalibrationLengthBox", (window.FocusManager?.GetFocusedElement() as TextBox)?.Name);

                doc.ScaleUnit = "m";
                doc.CalibrationLength = "4";
                Assert.Equal("100", doc.ScaleRatio);
                UiSession.Capture(window);
                if (UiSession.ScreenshotFolder() is { } again) UiSession.Capture(window).Save(Path.Combine(again, "75-messen-kalibriert.png"), PngBitmapEncoderOptions.Default);
                doc.ApplyScaleCommand.Execute(null);
                Assert.False(doc.HasCalibrationLine);
                Assert.Equal("1:100", PageScales.Of(doc.History.Pages[page].Page.Dictionary)[^1].Measure!.Ratio);
                var (ax, ay) = MeasurePlanTests.At((0, 0), 100, 1);
                var (bx, by) = MeasurePlanTests.At((4, 0), 100, 1);
                doc.MeasureKind = MeasureKind.Length;
                doc.MeasureAt(page, ax, ay);
                doc.MeasureAt(page, bx, by);
                Assert.Equal("4 m", doc.Measurements[^1].Measurement.Text);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Straight lines while measuring (Ablaufplan 7.6 g; docs/FEHLERGEDAECHTNIS.md, No. 602): a length started in the rectangle, the mouse
    /// a little above the horizontal — the point is exactly at the height of the start, with a guide; with Shift the angle is free, Shift let
    /// go without moving straightens again; 8° off it is not; at the right edge of the rectangle the point is where the straight line meets
    /// it. Switched off in the panel, Shift straightens. The direction beside the value: anticlockwise from the right, no „-0°“.
    /// </summary>
    [Fact]
    public void Lines_SnapStraight_ShiftFreesTheAngle()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-gerade.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1.25;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                const int page = 2;
                var centre = MeasurePlanTests.At((2, 1.5), 100, 1);
                var middle = view.PagePointToView(page, centre.X, centre.Y);
                var panel = window.FindControl<Border>("MeasureHost")!;
                var visible = Math.Min(view.Bounds.Width, panel.TranslatePoint(new Point(0, 0), view)!.Value.X);
                view.Offset += new Vector(middle.X - visible / 2, middle.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                await KeepDrawing(doc, page);
                Point At((double X, double Y) metres)
                {
                    var c = MeasurePlanTests.Clicks([metres], 100, 1)[0];
                    return view.TranslatePoint(view.PagePointToView(page, c.X, c.Y), window)!.Value;
                }
                (int Page, double X, double Y, SnapKind Snap) Hover() => doc.MeasureHover!.Value;

                Assert.Equal("0°", DocumentView.DirectionText(new Point(0, 0), new Point(10, 0)));
                Assert.Equal("0°", DocumentView.DirectionText(new Point(0, 0), new Point(100, 0.01)));
                Assert.Equal("90°", DocumentView.DirectionText(new Point(0, 0), new Point(0, -10)));
                Assert.Equal("180°", DocumentView.DirectionText(new Point(0, 0), new Point(-10, 0)));
                Assert.Equal("315°", DocumentView.DirectionText(new Point(0, 0), new Point(10, 10)));
                Assert.Equal(1.1.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + "°", DocumentView.DirectionText(new Point(0, 0), new Point(100, -1.92)));

                doc.MeasureKind = MeasureKind.Length;
                view.Focus();
                window.MouseMove(At((0.5, 1.5)));
                window.MouseDown(At((0.5, 1.5)), MouseButton.Left);
                window.MouseUp(At((0.5, 1.5)), MouseButton.Left);
                Thread.Sleep(50);
                var start = doc.MeasurePoints.Single();

                window.MouseMove(At((3, 1.55)));
                UiSession.Capture(window);
                if (UiSession.ScreenshotFolder() is { } folder) UiSession.Capture(window).Save(Path.Combine(folder, "76-messen-gerade.png"), PngBitmapEncoderOptions.Default);
                Assert.Equal(SnapKind.Straight, Hover().Snap);
                Assert.Equal(start.Y, Hover().Y);
                Assert.True(view.StraightGuideShown);

                window.MouseMove(At((3, 1.55)), RawInputModifiers.Shift);
                Assert.Equal(SnapKind.None, Hover().Snap);
                Assert.NotEqual(start.Y, Hover().Y);
                Assert.False(view.StraightGuideShown);
                window.KeyRelease(Key.LeftShift, RawInputModifiers.None, PhysicalKey.ShiftLeft, null);
                Assert.Equal(SnapKind.Straight, Hover().Snap);

                window.MouseMove(At((3, 1.85)));
                Assert.Equal(SnapKind.None, Hover().Snap);

                window.MouseMove(At((3.97, 1.56)));
                Assert.Equal(SnapKind.OnLine | SnapKind.Straight, Hover().Snap);
                Assert.Equal(start.Y, Hover().Y);
                Assert.Equal(MeasurePlanTests.At((4, 0), 100, 1).X, Hover().X, 4);   // the drawing is kept in float
                window.MouseDown(At((3.97, 1.56)), MouseButton.Left);
                window.MouseUp(At((3.97, 1.56)), MouseButton.Left);
                var line = doc.Measurements[^1].Measurement.Points;
                Assert.Equal(line[0].Y, line[1].Y);

                doc.MeasureStraight = false;
                Thread.Sleep(50);
                window.MouseMove(At((0.5, 1.2)));
                window.MouseDown(At((0.5, 1.2)), MouseButton.Left);
                window.MouseUp(At((0.5, 1.2)), MouseButton.Left);
                window.MouseMove(At((3, 1.25)));
                Assert.Equal(SnapKind.None, Hover().Snap);
                window.MouseMove(At((3, 1.25)), RawInputModifiers.Shift);
                Assert.Equal(SnapKind.Straight, Hover().Snap);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The magnifier shows what is put on the page (docs/FEHLERGEDAECHTNIS.md, No. 603): a picture put in (a floor plan as an image) is an
    /// element the view draws above the page — the magnifier drew only the page and stayed white there. Now its middle shows the red picture,
    /// and at the corner of the rectangle of the page still its line.
    /// </summary>
    [Fact]
    public void Magnifier_ShowsAPicturePutIn()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-lupe-bild.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                vm.MeasureCommand.Execute(null);
                const int page = 2;
                await KeepDrawing(doc, page);
                var (px, py) = MeasurePlanTests.At((5, 1.5), 100, 1);
                var pixels = new byte[32 * 32 * 4];
                for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (0xD0, 0x20, 0x20, 0xFF);
                doc.AddElement(page, new Core.Pdf.Editing.ImageElement
                {
                    Rect = new Core.Pdf.Document.PdfRectangle(px - 20, py - 20, px + 20, py + 20),
                    Image = new Core.Imaging.RgbaImage(32, 32, pixels),
                }, "Bild einfügen");
                UiSession.Capture(window);

                using (var image = view.LoupeImage(page, px, py, 1)!)
                using (var bitmap = SkiaSharp.SKBitmap.FromImage(image))
                {
                    var c = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
                    Assert.True(c.Red > 180 && c.Green < 80 && c.Blue < 80, $"Mitte der Lupe: {c}");
                }
                var (qx, qy) = MeasurePlanTests.At((0, 0), 100, 1);
                using (var image = view.LoupeImage(page, qx, qy, 1)!)
                using (var bitmap = SkiaSharp.SKBitmap.FromImage(image))
                {
                    var c = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
                    Assert.True(c.Red < 150, $"Ecke des Rechtecks: {c}");
                }
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Magnifier while measuring and the order of the panel (Ablaufplan 7.6 g; docs/FEHLERGEDAECHTNIS.md, No. 604; decided at the
    /// acceptance of 1.1.593): the scale with „Kalibrieren“ is the first card, the kind of measurement the second; „Alle als Bemaßung
    /// einfügen“ is blue. With „Messen“ on the magnifier follows the mouse over the page — before the first point and while drawing; switched
    /// off it is gone, while calibrating it stays.
    /// </summary>
    [Fact]
    public void Magnifier_WhileMeasuring_ScaleOnTop()
    {
        var pdf = TestFiles.Write(MeasurePlanTests.Build(), "pruefplan-messen-lupe.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1.25;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                const int page = 2;
                var centre = MeasurePlanTests.At((2, 1.5), 100, 1);
                var middle = view.PagePointToView(page, centre.X, centre.Y);
                var panel = window.FindControl<Border>("MeasureHost")!;
                var visible = Math.Min(view.Bounds.Width, panel.TranslatePoint(new Point(0, 0), view)!.Value.X);
                view.Offset += new Vector(middle.X - visible / 2, middle.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                await KeepDrawing(doc, page);
                Point At((double X, double Y) metres)
                {
                    var c = MeasurePlanTests.Clicks([metres], 100, 1)[0];
                    return view.TranslatePoint(view.PagePointToView(page, c.X, c.Y), window)!.Value;
                }

                var cards = panel.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("propCard")).ToList();
                Assert.Contains(cards[0].GetVisualDescendants().OfType<Button>(), b => b.Name == "CalibrateButton");
                // „Kalibrieren“ stands out (design decision, No. 686): in the accent colour at rest, full accent while calibrating — before it
                // looked like any other button (the class it had while calibrating has no style of its own here)
                var calibrate = cards[0].GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CalibrateButton");
                Assert.Contains("soft", calibrate.Classes);
                Assert.DoesNotContain("primary", calibrate.Classes);
                doc.StartCalibrationCommand.Execute(null);
                Assert.Contains("primary", calibrate.Classes);
                Assert.DoesNotContain("soft", calibrate.Classes);
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(doc.IsCalibrating);
                Assert.Contains("soft", calibrate.Classes);
                Assert.Contains(cards[1].GetVisualDescendants().OfType<RadioButton>(), b => b.Name == "MeasureLength");
                Assert.Contains("accent", panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "InsertAllDimensionsButton").Classes);

                doc.MeasureKind = MeasureKind.Length;
                view.Focus();
                window.MouseMove(At((0.5, 1.5)));
                Assert.True(view.LoupeShown);
                window.MouseDown(At((0.5, 1.5)), MouseButton.Left);
                window.MouseUp(At((0.5, 1.5)), MouseButton.Left);
                Thread.Sleep(50);
                window.MouseMove(At((3, 1.55)));
                Assert.True(view.LoupeShown);
                UiSession.Capture(window);
                if (UiSession.ScreenshotFolder() is { } folder) UiSession.Capture(window).Save(Path.Combine(folder, "77-messen-lupe.png"), PngBitmapEncoderOptions.Default);

                doc.MeasureLoupe = false;
                window.MouseMove(At((3, 1.6)));
                Assert.False(view.LoupeShown);
                doc.CancelMeasure();
                doc.StartCalibrationCommand.Execute(null);
                window.MouseMove(At((3, 1.5)));
                Assert.True(view.LoupeShown);
                doc.CancelCalibration();
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
