using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Text fields with "shrink text automatically": they start at the start size and become smaller only when the text does not fit
/// (single-line and multi-line); stored in the file as automatic size (0 Tf) with the start size as a separate entry, and read back
/// the same way on opening.
/// </summary>
public sealed class FieldShrinkTests
{
    private static FieldElement Field(string value, bool multiline = false, double height = 22) => new()
    {
        Rect = new PdfRectangle(100, 600, 260, 600 + height),
        Spec = new FieldSpec { Name = "Name", FontSize = 14, ShrinkToFit = true, Multiline = multiline, BorderColor = 0 },
        Value = value,
    };

    [Fact]
    public void StartSize_ShrinksOnlyWhenTheTextDoesNotFit()
    {
        var spec = Field("").Spec;
        Assert.Equal(14, ElementAppearance.FieldText(Field("Max"), spec, "Max").FontSize, 2);
        var crowded = ElementAppearance.FieldText(Field(""), spec, "Max Mustermann aus Musterstadt am Musterbach");
        Assert.InRange(crowded.FontSize, 4, 9);
        // Without the switch the fixed size stays (the text overflows)
        Assert.Equal(14, ElementAppearance.FieldText(Field(""), spec with { ShrinkToFit = false }, "Max Mustermann aus Musterstadt am Musterbach").FontSize, 2);
        // Multi-line: wrap first, then shrink until everything fits
        var tall = Field("", multiline: true, height: 60);
        var few = ElementAppearance.FieldText(tall, tall.Spec, "Zwei Zeilen Text");
        var many = ElementAppearance.FieldText(tall, tall.Spec, string.Join(' ', Enumerable.Repeat("viel Text im Feld", 12)));
        Assert.Equal(14, few.FontSize, 2);
        Assert.True(many.FontSize < 10 && ElementAppearance.NeededHeight(many) <= 60, many.FontSize.ToString());
    }

    [Fact]
    public void Saved_AsAuto_WithStartSize_ReadBackTheSame()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var page = PageItem.From(original)[0].WithElements([Field("Max Mustermann aus Musterstadt am Musterbach")]);
        using var output = new MemoryStream();
        PdfWriter.Write(original, [page], output);
        using var saved = PdfDocument.Open(output.ToArray());
        var widget = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0).AsDictionary()!;
        Assert.Contains(" 0 Tf", ((PdfString)widget.Get(PdfName.Get("DA"))).ToText());   // other readers: automatic size
        Assert.Equal(14, widget.GetNumber(FieldSpec.StartSizeKey));

        var foreign = Assert.IsType<ForeignAnnotation>(Assert.Single(PageItem.From(saved)[0].Elements));
        var spec = FieldSpec.Of(foreign)!;
        Assert.Equal((14.0, true), (spec.FontSize, spec.ShrinkToFit));
        // Filled in in PDF Knead without changed properties: starts from the start size
        var field = FormField.Of(foreign)!;
        Assert.Equal(14, ElementAppearance.FieldText(foreign, field, "Max").Text.FontSize, 2);
        Assert.True(ElementAppearance.FieldText(foreign, field, "Max Mustermann aus Musterstadt am Musterbach").Text.FontSize < 10);
    }
}
