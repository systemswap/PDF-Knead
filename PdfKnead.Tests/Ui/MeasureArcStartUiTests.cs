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
/// Arcs right after the first point and the place of the magnifier (decided on 9.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 664): with
/// Ctrl+Shift the point becomes an arc point — the preview stays straight with a small arc beside the mouse (No. 675; the half circle of 664
/// looked odd); Ctrl+Alt without a direction yet sets an arc point on a click and takes the direction of a drag; the magnifier keeps out of
/// where the mouse is heading and off the lines drawn.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class MeasureArcStartUiTests
{
    private static void Run(Func<MainViewModel, MainWindow, DocumentViewModel, DocumentView, Task> test)
    {
        var pdf = TestFiles.Write(MeasureUiTests.Plan(), "plan-bogenstart.pdf");
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
                doc.MeasureKind = MeasureKind.Polyline;
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
    /// After the first point, Ctrl+Shift over the page: the preview is a straight piece to the mouse, and a small arc shows beside the mouse
    /// (No. 675) — without Ctrl+Shift none; after an arc point (whose end is the next click) none either.
    /// </summary>
    [Fact]
    public void CtrlShift_AfterFirstPoint_StraightPreviewWithArcSign()
    {
        Run((_, window, doc, _) =>
        {
            doc.MeasureAt(0, 100, 400);
            doc.MeasureHoverAt(0, 200, 500, SnapKind.None, arc: true);
            var drawn = doc.CurrentMeasurement(drawing: true)!;
            Assert.Empty(drawn.ArcPoints);
            Assert.Equal(2, drawn.Points.Count);
            Assert.True(doc.MeasureHoverMakesArc);
            var shot = UiSession.Capture(window);
            if (UiSession.ScreenshotFolder() is { } folder)
                shot.Save(Path.Combine(folder, "messen-bogenzeichen.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            doc.MeasureHoverAt(0, 200, 500, SnapKind.None);
            Assert.False(doc.MeasureHoverMakesArc);
            doc.MeasureAt(0, 200, 500, arc: true);
            doc.MeasureHoverAt(0, 300, 400, SnapKind.None, arc: true);
            Assert.False(doc.MeasureHoverMakesArc);
            return Task.CompletedTask;
        });
    }

    /// <summary>Ctrl+Alt+click after the first point (no direction yet): the point is an arc point, the next click ends the arc.</summary>
    [Fact]
    public void CtrlAltClick_AfterFirstPoint_IsAnArcPoint()
    {
        Run((_, _, doc, _) =>
        {
            doc.MeasureAt(0, 100, 400);
            Assert.True(doc.MeasureNeedsDirection);
            doc.MeasureAt(0, 200, 500, tangent: true);
            Assert.Equal([1], doc.MeasureArcPoints);
            doc.MeasureAt(0, 300, 400);
            Assert.True(doc.FinishMeasure());
            Assert.True(doc.Measurements[^1].Measurement.Outline!.Pieces[0].IsArc);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Ctrl+Alt pressed at the first point, dragged to the right, let go above it: an arc that leaves the first point to the right (its start
    /// tangent) and ends where the mouse was let go.
    /// </summary>
    [Fact]
    public void CtrlAltDrag_AfterFirstPoint_GivesTheDirection()
    {
        Run((_, window, doc, view) =>
        {
            Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
            window.MouseMove(At(100, 400));
            window.MouseDown(At(100, 400), MouseButton.Left);
            window.MouseUp(At(100, 400), MouseButton.Left);
            UiSession.Capture(window);
            Assert.Single(doc.MeasurePoints);
            Thread.Sleep(700);   // no double-click
            const RawInputModifiers ctrlAlt = RawInputModifiers.Control | RawInputModifiers.Alt;
            window.MouseMove(At(100, 400), ctrlAlt);
            window.MouseDown(At(100, 400), MouseButton.Left, ctrlAlt);
            window.MouseMove(At(130, 400), ctrlAlt);   // the direction: to the right
            window.MouseMove(At(200, 500), ctrlAlt);
            window.MouseUp(At(200, 500), MouseButton.Left, ctrlAlt);
            UiSession.Capture(window);
            Assert.Equal(3, doc.MeasurePoints.Count);
            Assert.Equal([1], doc.MeasureArcPoints);
            Assert.Equal(200, doc.MeasurePoints[^1].X, 1);
            Assert.Equal(500, doc.MeasurePoints[^1].Y, 1);
            var outline = doc.CurrentMeasurement()!.Outline!;
            var start = outline.Pieces[0].StartDirection;
            Assert.True(start.X > 0.99 && Math.Abs(start.Y) < 0.05, $"{start}");
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The first arc dragged out to the right, the space bar pressed during the drag (decided on 10.11.2026: the arc came out below,
    /// wanted above): it flips to the other side of its chord — its start tangent is the dragged one mirrored at the chord, here downwards
    /// along the page's y axis instead of to the right; the end stays where the mouse was let go.
    /// </summary>
    [Fact]
    public void CtrlAltDrag_SpaceBar_FlipsTheArc()
    {
        Run((_, window, doc, view) =>
        {
            Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
            window.MouseMove(At(100, 400));
            window.MouseDown(At(100, 400), MouseButton.Left);
            window.MouseUp(At(100, 400), MouseButton.Left);
            Thread.Sleep(700);   // no double-click
            const RawInputModifiers ctrlAlt = RawInputModifiers.Control | RawInputModifiers.Alt;
            window.MouseMove(At(100, 400), ctrlAlt);
            window.MouseDown(At(100, 400), MouseButton.Left, ctrlAlt);
            window.MouseMove(At(130, 400), ctrlAlt);   // the direction: to the right
            window.MouseMove(At(200, 500), ctrlAlt);
            var before = doc.CurrentMeasurement()!.Outline!.Pieces[0].StartDirection;
            window.KeyPress(Key.Space, ctrlAlt, PhysicalKey.Space, " ");
            Assert.True(doc.MeasureArcFlipped);
            window.MouseUp(At(200, 500), MouseButton.Left, ctrlAlt);
            UiSession.Capture(window);
            Assert.Equal(3, doc.MeasurePoints.Count);
            Assert.Equal(200, doc.MeasurePoints[^1].X, 1);
            Assert.Equal(500, doc.MeasurePoints[^1].Y, 1);
            var start = doc.CurrentMeasurement()!.Outline!.Pieces[0].StartDirection;
            Assert.True(before.X > 0.99, $"{before}");
            Assert.True(Math.Abs(start.X) < 0.05 && start.Y > 0.99, $"{start}");   // (1, 0) mirrored at the chord (1, 1)
            Assert.False(doc.MeasureArcFlipped);   // the next arc starts unflipped
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// A tangent arc after a straight piece (Ctrl+Alt+click): flipped with the space bar, its point halfway is the unflipped one mirrored at the
    /// chord — the preview shows it at once, the click sets it; without Ctrl+Alt the space bar flips nothing (it pages on as ever).
    /// </summary>
    [Fact]
    public void TangentArc_SpaceBar_MirrorsItAtTheChord()
    {
        Run((_, window, doc, view) =>
        {
            doc.MeasureAt(0, 100, 400);
            doc.MeasureAt(0, 200, 400);
            doc.MeasureHoverAt(0, 300, 500, SnapKind.None, tangent: true);
            var plain = doc.CurrentMeasurement()!.Points[^2];
            Assert.True(doc.FlipMeasureArc());
            var flipped = doc.CurrentMeasurement()!.Points[^2];
            var mirrored = DocumentViewModel.Mirrored(plain, (200, 400), (300, 500));
            Assert.Equal(mirrored.X, flipped.X, 6);
            Assert.Equal(mirrored.Y, flipped.Y, 6);
            Assert.NotEqual(plain.X, flipped.X, 1);
            doc.MeasureAt(0, 300, 500, tangent: true);
            Assert.Equal(flipped.X, doc.MeasurePoints[^2].X, 6);
            Assert.False(doc.MeasureArcFlipped);
            // the key without Ctrl+Alt: nothing flips
            Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
            window.MouseMove(At(350, 450));
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.False(doc.MeasureArcFlipped);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Drawing to the right along a line, with the magnifier on: it does not sit in front of the mouse (to the right). Over the lines it may lie
    /// (design decision, No. 684: fewer moves, calmer); before 664 it sat to the upper left, also in front of a mouse moving that way.
    /// </summary>
    [Fact]
    public void Loupe_NotAheadOfTheMouse()
    {
        Run((_, window, doc, view) =>
        {
            doc.MeasureLoupe = true;
            doc.MeasureKind = MeasureKind.Polyline;
            // a line coming from the upper left to the point, then the mouse moving on to the right
            doc.MeasureAt(0, 150, 500);
            doc.MeasureAt(0, 250, 400);
            Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
            for (var x = 255; x <= 290; x += 5)
            {
                window.MouseMove(At(x, 400));
                UiSession.Capture(window);
            }
            var mouse = view.PagePointToView(0, 290, 400);
            var centre = Assert.NotNull(view.LoupeCentreTarget);   // where it goes (it glides there, No. 684)
            Assert.True(centre.X <= mouse.X + 1, $"vor der Maus: {centre} bei {mouse}");
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The mouse turning back (drawing to the right, then to the left): the magnifier gives way — not at once, it glides round the point
    /// (design decision, docs/FEHLERGEDAECHTNIS.md, No. 684): at every step of 16 ms at the same distance from the point, no step more than
    /// a quarter of the way, several steps until it arrives. Before it jumped to the other side between two frames.
    /// </summary>
    [Fact]
    public void Loupe_ChangingSides_GlidesRoundThePoint()
    {
        Run((_, window, doc, view) =>
        {
            view.LoupeGlideByHand = true;   // the glide by the times given here, not by the clock
            doc.MeasureLoupe = true;
            doc.MeasureKind = MeasureKind.Polyline;
            doc.MeasureAt(0, 150, 400);
            Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
            for (var x = 160; x <= 300; x += 10)
            {
                window.MouseMove(At(x, 400));
                UiSession.Capture(window);
            }
            // arrived (the glide of the first frames done)
            var tick = System.Diagnostics.Stopwatch.Frequency * 16 / 1000;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var i = 0; i < 100; i++) view.AdvanceLoupe(now += tick);
            var before = Assert.NotNull(view.LoupeCentreShown);
            var mouse = view.PagePointToView(0, 300, 400);
            Assert.True(before.X <= mouse.X + 1, $"vor der Maus: {before}");
            // the point set, the mouse turning back to the left: the magnifier is to change sides (the target follows the mouse; no frame
            // drawn before it is measured — the time between frames here is real time, it would arrive meanwhile)
            doc.MeasureAt(0, 300, 400);
            for (var x = 290; x >= 270; x -= 10) window.MouseMove(At(x, 400));
            var target = Assert.NotNull(view.LoupeCentreTarget);
            var hover = Assert.NotNull(doc.MeasureHover);   // the point the magnifier looks at (the mouse, snapped)
            var point = view.PagePointToView(0, hover.X, hover.Y);
            Assert.True(target.X >= point.X - 1, $"Ziel vor der Maus: {target}");
            // while the mouse goes on that way it stays there (calm: no further move)
            window.MouseMove(At(265, 400));
            var moved = Assert.NotNull(doc.MeasureHover);
            var still = Assert.NotNull(view.LoupeCentreTarget) - view.PagePointToView(0, moved.X, moved.Y);
            Assert.Equal(target.X - point.X, still.X, 1);
            Assert.Equal(target.Y - point.Y, still.Y, 1);
            point = view.PagePointToView(0, moved.X, moved.Y);
            target = point + still;
            var shown = Assert.NotNull(view.LoupeCentreShown);
            var way = Math.Sqrt(Math.Pow(target.X - shown.X, 2) + Math.Pow(target.Y - shown.Y, 2));
            Assert.True(way > 60, $"schon angekommen: {way:0}");
            var steps = 0;
            for (now = System.Diagnostics.Stopwatch.GetTimestamp(); steps < 200; steps++)
            {
                view.AdvanceLoupe(now += tick);
                var next = Assert.NotNull(view.LoupeCentreShown);
                var step = Math.Sqrt(Math.Pow(next.X - shown.X, 2) + Math.Pow(next.Y - shown.Y, 2));
                Assert.True(step <= way / 4 + 1, $"Sprung {step:0} von {way:0}");
                var radius = Math.Sqrt(Math.Pow(next.X - point.X, 2) + Math.Pow(next.Y - point.Y, 2));
                Assert.InRange(radius, (62 + 26) * 1.38 - 1, (62 + 26) * 1.38 + 1);
                shown = next;
                if (Math.Abs(next.X - target.X) < 0.5 && Math.Abs(next.Y - target.Y) < 0.5) break;
            }
            Assert.InRange(steps, 5, 150);
            return Task.CompletedTask;
        });
    }
}
