using Avalonia;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The floating panels on the right do not cover the page (KONZEPT.md, section 4.4): a page with room to its left stays where it is, one
/// that would reach underneath moves left, a wider one scrolls clear; „Page width“ fits beside a tool panel, but not beside the panel of a
/// selection.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class RightPanelUiTests
{
    private static void Settle(MainWindow window)
    {
        for (var i = 0; i < 3; i++) UiSession.Capture(window);
    }

    /// <summary>The page's right edge stands beside the panel (to within a pixel).</summary>
    private static void Beside(DocumentView view, Rect page) =>
        Assert.True(page.Right <= view.Bounds.Width - view.RightInset + 1, $"Seite bis {page.Right:0}, Feld ab {view.Bounds.Width - view.RightInset:0}");

    [Fact]
    public void ToolPanel_PageWidthFitsBesideIt()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 2).Build(), "rand-werkzeug.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var view = window.GetVisualDescendants().OfType<DocumentView>().Single();
                doc.Fit(ZoomMode.FitWidth);
                Settle(window);
                var zoom = doc.Zoom;
                Assert.Equal(0, view.RightInset);

                // „Bearbeiten“ stays on: page width fits into the room left of its panel
                vm.SetToolCommand.Execute(ViewTool.Edit);
                Settle(window);
                Assert.True(view.RightInset > 300);
                Assert.True(view.FitBesideInset);
                Assert.True(doc.Zoom < zoom - 0.01, $"Zoom {doc.Zoom:0.000} statt kleiner als {zoom:0.000}");
                Beside(view, view.PageInView(0));
                Assert.Equal(ZoomMode.FitWidth, doc.ZoomMode);

                // „Text einfügen“ counts as a tool: its format panel stays open while the tool is on
                vm.SetToolCommand.Execute(ViewTool.InsertText);
                Settle(window);
                Assert.True(vm.ShowTextProperties);
                Assert.True(view.FitBesideInset);
                Beside(view, view.PageInView(0));

                var beside = doc.Zoom;

                // a text placed: the tool returns to „Text markieren“, the new text stays selected — the page keeps its size
                doc.AddElement(0, new TextElement { Rect = new PdfRectangle(100, 700, 300, 720), Text = "Neu" }, "Test");
                vm.Tool = ViewTool.SelectText;
                Settle(window);
                Assert.True(vm.ShowTextProperties);
                Assert.True(view.FitBesideInset);
                Assert.Equal(beside, doc.Zoom, 6);

                // the panel closes (nothing selected): the page is as wide as before
                doc.SelectedElement = null;
                Settle(window);
                Assert.Equal(0, view.RightInset);
                Assert.False(view.FitBesideInset);
                Assert.Equal(zoom, doc.Zoom, 3);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void SelectionPanel_KeepsTheZoom_PageStillNotCovered()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "rand-auswahl.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var view = window.GetVisualDescendants().OfType<DocumentView>().Single();

                // page width: wider than the room left of the panel — the zoom stays, but the page scrolls clear of the panel
                doc.Fit(ZoomMode.FitWidth);
                Settle(window);
                var zoom = doc.Zoom;
                doc.AddElement(0, new ShapeElement { Rect = new PdfRectangle(100, 600, 200, 680) }, "Test");
                Settle(window);
                Assert.True(vm.ElementProperties.IsVisible);
                Assert.True(view.RightInset > 300);
                Assert.False(view.FitBesideInset);
                Assert.Equal(zoom, doc.Zoom, 6);
                view.Offset = new Vector(double.MaxValue, view.Offset.Y);
                Settle(window);
                Beside(view, view.PageInView(0));

                // a selection does not move the page (it would jump with every click): it stays, and scrolls clear of the panel
                doc.SelectedElement = null;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 0.7;
                view.Offset = default;
                Settle(window);
                var before = view.PageInView(0);
                doc.SelectedElement = new ElementRef(0, Assert.Single(doc.ElementsOn(0)));
                Settle(window);
                Assert.Equal(before.Left, view.PageInView(0).Left, 1);
                view.Offset = new Vector(double.MaxValue, view.Offset.Y);
                Settle(window);
                Beside(view, view.PageInView(0));

                // beside the panel of a tool the same page moves left just far enough
                doc.SelectedElement = null;
                view.Offset = default;
                doc.Zoom = 0.4;
                Settle(window);
                var free = view.PageInView(0);
                Assert.True(free.Width + 2 * DocumentView.PageMargin < view.Bounds.Width - 350, "Seite passt nicht neben das Feld");
                vm.SetToolCommand.Execute(ViewTool.Edit);
                Settle(window);
                var moved = view.PageInView(0);
                Beside(view, moved);
                Assert.True(moved.Left >= DocumentView.PageMargin - 1);
                vm.SetToolCommand.Execute(ViewTool.Edit);
                Settle(window);

                // in a wide window nothing moves when a selection opens the panel
                doc.SelectedElement = null;
                window.Width = 2000;
                Settle(window);
                var wide = view.PageInView(0);
                doc.SelectedElement = new ElementRef(0, Assert.Single(doc.ElementsOn(0)));
                Settle(window);
                Assert.Equal(wide.Left, view.PageInView(0).Left, 1);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The wider areas that stay open until closed — here „Barrierefreiheit“ and the area of a module — cover as much as they are wide and
    /// count like a tool's panel: „Seitenbreite“ fits beside them (docs/FEHLERGEDAECHTNIS.md, No. 864: they lay over the page; wish of the
    /// user 15.11.2026 for the area of the e-invoice).
    /// </summary>
    [Fact]
    public void OpenAreas_PageWidthFitsBesideThem()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 2).Build(), "rand-bereiche.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var view = window.GetVisualDescendants().OfType<DocumentView>().Single();
                doc.Fit(ZoomMode.FitWidth);
                Settle(window);
                var zoom = doc.Zoom;
                Assert.Equal(0, view.RightInset);

                foreach (var (name, open, close) in new (string, Action, Action)[]
                         {
                             ("AccessibilityHost", () => vm.ToggleAccessibilityCommand.Execute(null), doc.CloseAccessibility),
                             ("ModulePanelHost", () => vm.ShowModulePanel(doc, new Avalonia.Controls.TextBlock { Text = "Baustein" }), () => vm.ShowModulePanel(doc, null)),
                         })
                {
                    open();
                    Settle(window);
                    var host = window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Single(b => b.Name == name);
                    Assert.True(host.IsVisible, name);
                    Assert.Equal(host.Width + host.Margin.Right + 8, view.RightInset, 1);
                    Assert.True(view.FitBesideInset, name);
                    Assert.True(doc.Zoom < zoom - 0.01, $"{name}: Zoom {doc.Zoom:0.000} statt kleiner als {zoom:0.000}");
                    Beside(view, view.PageInView(0));
                    close();
                    Settle(window);
                    Assert.Equal(0, view.RightInset);
                    Assert.Equal(zoom, doc.Zoom, 3);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
