using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>The zoom buttons and Ctrl+Plus/Minus step to the next multiple of 5 % of the zoom as shown (decided on 13.11.2026).</summary>
[Collection(nameof(UiCollection))]   // every class of this namespace (UiCollectionTests), though it opens no window
public sealed class ZoomStepTests
{
    [Theory]
    [InlineData(0.74, 1, 0.75)]
    [InlineData(0.75, 1, 0.80)]
    [InlineData(0.74, -1, 0.70)]
    [InlineData(0.70, -1, 0.65)]
    [InlineData(0.7496, 1, 0.80)]    // shown as 75 %: the next step is 80 %, not 75 % again
    [InlineData(0.7449, -1, 0.70)]   // shown as 74 %
    [InlineData(1.0, 1, 1.05)]
    [InlineData(0.05, -1, 0.05)]     // the smallest zoom stays
    [InlineData(64.0, 1, 64.0)]      // the largest as well
    public void Step_NextMultipleOfFive(double zoom, int direction, double expected) =>
        Assert.Equal(expected, DocumentViewModel.ZoomStep(zoom, direction), 9);
}
