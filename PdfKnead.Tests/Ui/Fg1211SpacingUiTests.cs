using System.Globalization;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// M7, FG 1211: the line spacing in the properties panel of formatted text — the list offers multiples of the natural line height of the fonts;
/// a block of an earlier version shows its factor of the font size as such, and a value chosen from the list gives it the new kind.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class Fg1211SpacingUiTests
{
    /// <summary>
    /// A block of an earlier version (1.15 times the font size) shows „1,15 × Schriftgröße“ after the four values of the list, not „1,15“;
    /// choosing „1,5“ makes it a block spaced by 1.5 times the line height of its fonts, which then shows „1,5“; a new block shows
    /// „Einfach (1,0)“. Before, both showed „1,15“ and the value meant the font size.
    /// </summary>
    [Fact]
    public void Panel_OldBlockShowsFactorOfTheSize_ChoosingAValueGivesTheNewKind()
    {
        var path = TestFiles.Write(TestPdf.Simple(1).Build(), "fg1211-spacing.pdf");
        UiSession.Run(async () =>
        {
            var doc = new DocumentViewModel(path, PdfDocument.Open(path));
            try
            {
                var style = new RichStyle { Family = "Liberation Sans", Size = 12 };
                var old = new RichTextElement
                {
                    Rect = new PdfRectangle(50, 600, 300, 700), Content = new RichText("Alt\nZweite Zeile", style), LineGap = 1.15, FontLineHeight = false,
                };
                var fresh = new RichTextElement { Rect = new PdfRectangle(50, 400, 300, 500), Content = new RichText("Neu", style) };
                doc.AddElement(0, old, "Test");
                doc.AddElement(0, fresh, "Test");
                var panel = new RichTextPropertiesViewModel();

                doc.SelectedElement = new ElementRef(0, old);
                panel.Refresh(doc);
                var shown = Assert.IsType<SpacingChoice>(panel.Spacing);
                Assert.True(shown.OfFontSize);
                Assert.Equal(Tr.T("{0} × Schriftgröße", 1.15.ToString("0.##", CultureInfo.CurrentCulture)), shown.Label);
                Assert.Equal(["Einfach (1,0)", "1,15", "1,5", "Doppelt (2,0)"], RichTextPropertiesViewModel.Spacings.Take(4).Select(s => s.Label));
                Assert.All(RichTextPropertiesViewModel.Spacings.Take(4), s => Assert.False(s.OfFontSize));

                panel.Spacing = RichTextPropertiesViewModel.Spacings.First(s => s.Value == 1.5 && !s.OfFontSize);
                var changed = Assert.Single(doc.ElementsOn(0).OfType<RichTextElement>(), e => e.Content.Text.StartsWith("Alt", StringComparison.Ordinal));
                Assert.True(changed.FontLineHeight);
                Assert.Equal(1.5, changed.LineGap);
                Assert.Equal("1,5", panel.Spacing!.Label);

                doc.SelectedElement = new ElementRef(0, Assert.Single(doc.ElementsOn(0).OfType<RichTextElement>(), e => e.Content.Text == "Neu"));
                panel.Refresh(doc);
                Assert.Equal("Einfach (1,0)", panel.Spacing!.Label);
            }
            finally
            {
                await doc.Renderer.WaitUntilUnused([doc.Document]).WaitAsync(TimeSpan.FromSeconds(10));
                doc.Dispose();
            }
        });
    }

    /// <summary>
    /// A label the form design adds to a new check box is new text (no form field): it is spaced by the natural line height of its font, as
    /// inserted text, and keeps the mark after saving and opening. Before, every new label took 1.15 times the font size.
    /// </summary>
    [Fact]
    public void FormDesign_NewLabel_SpacedByTheLineHeightOfItsFont()
    {
        var path = TestFiles.Write(TestPdf.Simple(1).Build(), "fg1211-label.pdf");
        UiSession.Run(async () =>
        {
            var doc = new DocumentViewModel(path, PdfDocument.Open(path));
            try
            {
                doc.FieldPreset = FieldPreset.CheckBox;
                Assert.NotNull(doc.CreateField(0, new PdfRectangle(100, 600, 112, 612), 0));
                var label = Assert.Single(doc.ElementsOn(0).OfType<TextElement>(), DocumentViewModel.IsFieldLabel);
                Assert.True(label.FontLineHeight);
                var face = ElementFace.For(label.Font);
                Assert.Equal(face.LineHeight, ElementAppearance.LineStep(label, face), 9);
                Assert.NotEqual(ElementAppearance.LineSpacing, ElementAppearance.LineStep(label, face), 9);

                using var blank = PdfDocument.Open(TestPdf.Simple(1).Build());
                using var output = new MemoryStream();
                PdfWriter.Write(blank, [PageItem.From(blank)[0].WithElements([label])], output);
                using var saved = PdfDocument.Open(output.ToArray());
                Assert.True(Assert.Single(PageItem.From(saved)[0].Elements.OfType<TextElement>()).FontLineHeight);
            }
            finally
            {
                await doc.Renderer.WaitUntilUnused([doc.Document]).WaitAsync(TimeSpan.FromSeconds(10));
                doc.Dispose();
            }
        });
    }
}
