using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfKnead.Localization;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The tooltips of the kinds of measurement (decided on 8.11.2026): each a heading, one sentence what it measures, then the keys
/// on the left and what they do on the right, groups apart — the options at a glance instead of one long paragraph.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class MeasureTooltipUiTests
{
    /// <summary>The keys of a structured tooltip (left column) in order.</summary>
    private static List<string> Keys(StackPanel tip) =>
        [.. tip.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<TextBlock>().Where(t => t.Classes.Contains("tipKey"))).Select(t => t.Text ?? "")];

    [Fact]
    public void EveryKind_HasAStructuredTooltip_PolylineAndAreaNameTheirKeys()
    {
        var pdf = TestFiles.Write(Pdf.TestPdf.Simple(1).Build(), "messen-tooltips.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                var radios = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "MeasureKind").ToDictionary(r => r.Name!);
                Assert.Equal(5, radios.Count);
                foreach (var (name, radio) in radios)
                {
                    var tip = Assert.IsType<StackPanel>(ToolTip.GetTip(radio));
                    Assert.Contains("tip", tip.Classes);
                    var title = Assert.IsType<TextBlock>(tip.Children[0]);
                    Assert.Equal(radio.Content as string, title.Text);
                    Assert.NotEmpty(Keys(tip));
                    Assert.True(tip.Children.OfType<Grid>().Any(), name);
                }
                // left what comes of it, right how (as the user drafted it); continuing for the polyline only (an area cannot be continued)
                Assert.Equal([Tr.T("Gerade"), Tr.T("Bogen"), Tr.T("Bogen tangential"), Tr.T("Bogen tangential kippen"), Tr.T("Beenden"), Tr.T("Fortsetzen")],
                    Keys((StackPanel)ToolTip.GetTip(radios["MeasurePolyline"])!));
                Assert.Equal([Tr.T("Ecke"), Tr.T("Bogen"), Tr.T("Bogen tangential"), Tr.T("Bogen tangential kippen"), Tr.T("Schließen")],
                    Keys((StackPanel)ToolTip.GetTip(radios["MeasureArea"])!));
                // one table per tooltip: the columns line up across the groups
                Assert.Single(((StackPanel)ToolTip.GetTip(radios["MeasurePolyline"])!).Children.OfType<Grid>());

                // the look, for the eye: the tooltips of polyline and area in a tooltip's frame
                if (UiSession.ScreenshotFolder() is { } folder)
                    foreach (var (name, file) in new[] { ("MeasurePolyline", "97-messen-tooltip-linienzug.png"), ("MeasureArea", "97-messen-tooltip-flaeche.png") })
                    {
                        var tip = (StackPanel)ToolTip.GetTip(radios[name])!;
                        ToolTip.SetTip(radios[name], null);
                        // (a ToolTip draws only inside its popup: a frame like it)
                        var frame = new Window
                        {
                            Width = 340, Height = 320,
                            Content = new Border
                            {
                                Background = Avalonia.Media.Brushes.White, BorderBrush = Avalonia.Media.Brushes.Gray, BorderThickness = new Avalonia.Thickness(1),
                                Padding = new Avalonia.Thickness(10, 8), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Child = tip,
                            },
                        };
                        frame.Show();
                        UiSession.Capture(frame).Save(Path.Combine(folder, file), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                        frame.Close();
                    }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
