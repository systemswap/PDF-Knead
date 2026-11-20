using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The label of a measurement's value moved with the mouse (decided on 10.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 703): within its rules,
/// back to its own place when let go near it, and with a double-click.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class MeasureLabelUiTests
{
    private static void Run(Func<MainViewModel, MainWindow, DocumentViewModel, DocumentView, Task> test)
    {
        var pdf = TestFiles.Write(MeasureUiTests.Plan(), "plan-beschriftung.pdf");
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
                var view = window.GetVisualDescendants().OfType<DocumentView>().Single();
                var target = view.PagePointToView(0, 250, 400);
                view.Offset += new Vector(target.X - view.Bounds.Width / 2, target.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                vm.MeasureCommand.Execute(null);
                doc.MeasureSnap = false;
                await test(vm, window, doc, view);
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
    /// An area 200–300 × 350–450: its label (in its middle) dragged far to the right stops with its middle on the right edge; dragged back near its
    /// middle it goes home; moved again and double-clicked, it goes home as well.
    /// </summary>
    [Fact]
    public void AreaLabel_DraggedWithinItsRules_SnapsHome_DoubleClickResets()
    {
        Run((_, window, doc, view) =>
        {
            doc.MeasureKind = MeasureKind.Area;
            foreach (var (x, y) in new[] { (200.0, 350.0), (300.0, 350.0), (300.0, 450.0), (200.0, 450.0) }) doc.MeasureAt(0, x, y);
            Assert.True(doc.FinishMeasure());
            var row = doc.Measurements[^1];
            doc.SelectedMeasurement = null;
            Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
            UiSession.Capture(window);

            // dragged far to the right: its middle on the edge
            window.MouseMove(At(250, 400));
            window.MouseDown(At(250, 400), MouseButton.Left);
            window.MouseMove(At(320, 400));
            window.MouseMove(At(420, 400));
            window.MouseUp(At(420, 400), MouseButton.Left);
            UiSession.Capture(window);
            Assert.NotNull(row.LabelAt);
            Assert.Equal(300, row.LabelAt!.Value.X, 0);
            Assert.Equal(400, row.LabelAt!.Value.Y, 0);
            Thread.Sleep(700);   // no double-click

            // dragged back near its middle: home
            window.MouseMove(At(300, 400));
            window.MouseDown(At(300, 400), MouseButton.Left);
            window.MouseMove(At(270, 400));
            window.MouseMove(At(252, 401));
            window.MouseUp(At(252, 401), MouseButton.Left);
            UiSession.Capture(window);
            Assert.Null(row.LabelAt);
            Thread.Sleep(700);

            // moved again, then a double-click on it: home
            window.MouseMove(At(250, 400));
            window.MouseDown(At(250, 400), MouseButton.Left);
            window.MouseMove(At(230, 380));
            window.MouseMove(At(220, 370));
            window.MouseUp(At(220, 370), MouseButton.Left);
            UiSession.Capture(window);
            Assert.NotNull(row.LabelAt);
            Thread.Sleep(700);
            window.MouseDown(At(220, 370), MouseButton.Left);
            window.MouseUp(At(220, 370), MouseButton.Left);
            window.MouseDown(At(220, 370), MouseButton.Left);
            window.MouseUp(At(220, 370), MouseButton.Left);
            UiSession.Capture(window);
            Assert.Null(row.LabelAt);
            return Task.CompletedTask;
        });
    }
}
