using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// An area measured with many arcs (reported on 9.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 662): while „Messen“ is on, the view draws its
/// course as the screen can show it — simplified to within a third of a pixel and kept while the view stays — instead of the 15 000 points of
/// its arcs in steps of half a degree in every frame. Before: about 190 ms per frame for the measurement alone, and dragging an image on the
/// page stalled.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class MeasureCourseUiTests
{
    /// <summary>A circle of 1 440 points (radius 300): the simplified line keeps a few dozen, and none of the circle lies further from it than the tolerance.</summary>
    [Fact]
    public void Simplified_Circle_FewPoints_WithinTolerance()
    {
        var circle = Enumerable.Range(0, 1441).Select(k => new Point(300 * Math.Cos(k * Math.PI / 720), 300 * Math.Sin(k * Math.PI / 720))).ToList();
        var simple = DocumentView.Simplified(circle, 0.3);
        Assert.InRange(simple.Count, 20, 200);
        Assert.Equal(circle[0], simple[0]);
        Assert.Equal(circle[^1], simple[^1]);
        foreach (var p in circle)
            Assert.True(Enumerable.Range(0, simple.Count - 1).Min(i => Distance(p, simple[i], simple[i + 1])) <= 0.3 + 1e-9);
    }

    private static double Distance(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
        var t = length < 1e-12 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>
    /// An area of 80 points, every second an arc point (40 arcs, about 15 000 points of path): drawn with at most a tenth of them; the same
    /// course again while the view stays (kept), a new one after zooming.
    /// </summary>
    [Fact]
    public void AreaWithManyArcs_DrawnWithFewPoints_KeptWhileTheViewStays()
    {
        var pdf = TestFiles.Write(MeasureUiTests.Plan(), "plan-boegen.pdf");
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
                doc.MeasureShowSegments = true;
                doc.MeasureShowAngles = true;
                doc.MeasureKind = MeasureKind.Area;
                const int n = 80;
                for (var k = 0; k < n; k++)
                {
                    var a = 2 * Math.PI * k / n;
                    var r = k % 2 == 0 ? 250 : 270;
                    doc.MeasureAt(0, 300 + r * Math.Cos(a), 420 + r * Math.Sin(a), k % 2 == 1);
                }
                Assert.True(doc.FinishMeasure());
                UiSession.Capture(window);
                var row = doc.Measurements[^1];
                var path = row.Measurement.Outline!.Path.Count;
                Assert.True(path > 10_000, $"{path}");
                var view = window.GetVisualDescendants().OfType<DocumentView>().Single();
                var course = view.MeasureCourseInView(row)!;
                Assert.True(course.Count * 10 < path, $"{course.Count} von {path}");
                Assert.Same(course, view.MeasureCourseInView(row));
                doc.Zoom = 1.5;
                UiSession.Capture(window);
                Assert.NotSame(course, view.MeasureCourseInView(row));
            }
            finally
            {
                vm.AskChoices = _ => Task.FromResult(new ChoiceAnswer(2, false));
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
