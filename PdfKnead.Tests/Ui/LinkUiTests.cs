using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Localization;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Links in the view (decision J of 3.11.2026): in „Text markieren“ only Ctrl+click follows, with the hand tool a plain click; internal
/// destinations without a question, addresses always with it, other actions never (docs/ABLAUFPLAN.md, 7.3 d; docs/FEHLERGEDAECHTNIS.md,
/// No. 566).
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class LinkUiTests
{
    /// <summary>Two A4 pages; on page 1 three links: to page 2 (y 700–720), a web address (y 600–620), a program start (y 500–520).</summary>
    private static string Linked() => TestFiles.Write(CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Annots[10 0 R 11 0 R 12 0 R]>>",
        [4] = "<</Type/Page/Parent 2 0 R>>",
        [10] = "<</Type/Annot/Subtype/Link/Rect[100 700 300 720]/Dest[4 0 R/Fit]>>",
        [11] = "<</Type/Annot/Subtype/Link/Rect[100 600 300 620]/A<</S/URI/URI(https://example.org/max-mustermann)>>>>",
        [12] = "<</Type/Annot/Subtype/Link/Rect[100 500 300 520]/A<</S/Launch/F(programm.exe)>>>>",
    }), "links.pdf");

    private static void Run(Func<MainViewModel, MainWindow, DocumentViewModel, Func<double, double, Point>, Task> test)
    {
        var path = Linked();
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([path]);
                var doc = vm.SelectedDocument!;
                vm.FitPageCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                await test(vm, window, doc, At);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    private static void Click(MainWindow window, Point p, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseDown(p, MouseButton.Left, modifiers);
        window.MouseUp(p, MouseButton.Left, modifiers);
        UiSession.Capture(window);
    }

    [Fact]
    public void CtrlClickOnInternalLink_InSelectText_ScrollsWithoutQuestion() => Run((vm, window, doc, at) =>
    {
        var asked = 0;
        vm.ConfirmOpenAddress = (_, _) => { asked++; return Task.FromResult(false); };
        doc.Tool = ViewTool.SelectText;
        Click(window, at(200, 710), RawInputModifiers.Control);
        Assert.Equal(1, doc.CurrentPage?.Index);
        Assert.Equal(0, asked);
        return Task.CompletedTask;
    });

    /// <summary>A plain click in „Text markieren“ does not follow (it stays text selection). Before and after: no jump.</summary>
    [Fact]
    public void PlainClickOnLink_InSelectText_DoesNotFollow() => Run((_, window, doc, at) =>
    {
        doc.Tool = ViewTool.SelectText;
        Click(window, at(200, 710));
        Assert.Equal(0, doc.CurrentPage?.Index);
        return Task.CompletedTask;
    });

    [Fact]
    public void ClickOnInternalLink_InPanTool_Scrolls() => Run((_, window, doc, at) =>
    {
        doc.Tool = ViewTool.Pan;
        Click(window, at(200, 710));
        Assert.Equal(1, doc.CurrentPage?.Index);
        return Task.CompletedTask;
    });

    /// <summary>A web address: always asked first, with its target; declined — nothing opened.</summary>
    [Fact]
    public void ClickOnWebLink_AlwaysAsksFirst() => Run((vm, window, doc, at) =>
    {
        var asked = new List<string>();
        var opened = new List<string>();
        vm.ConfirmOpenAddress = (target, _) => { asked.Add(target); return Task.FromResult(false); };
        vm.OpenAddress = target => { opened.Add(target); return Task.CompletedTask; };
        doc.Tool = ViewTool.Pan;
        Click(window, at(200, 610));
        Assert.Equal(["https://example.org/max-mustermann"], asked);
        Assert.Empty(opened);
        vm.ConfirmOpenAddress = (target, _) => { asked.Add(target); return Task.FromResult(true); };
        Click(window, at(200, 610));
        Assert.Equal(["https://example.org/max-mustermann"], opened);
        return Task.CompletedTask;
    });

    /// <summary>A program start is never carried out, only named.</summary>
    [Fact]
    public void LaunchAction_NotExecuted_ShowsNotice() => Run((vm, window, doc, at) =>
    {
        var opened = 0;
        vm.OpenAddress = _ => { opened++; return Task.CompletedTask; };
        doc.Tool = ViewTool.Pan;
        Click(window, at(200, 510));
        Assert.Contains(Tr.T("Wird aus Sicherheitsgründen nicht ausgeführt: {0}", "")[..20], vm.Message ?? "");
        Assert.Equal(0, opened);
        return Task.CompletedTask;
    });
}
