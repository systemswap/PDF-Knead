using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Ui;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package uni-16 (Unicode chapter 18: ideographic description sequences, kana, Hangul): the rows of the draft that still asked for a test
/// — the caret after every character of a description sequence, and the lists of field names in the order of the platform (in
/// <see cref="Uni16FieldNameOrderUiTests"/>).
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// UNI 18.2.1 sentences 78–82 (editor): an ideographic description sequence counts as three characters — „⿱井蛙“ has a caret stop before
    /// and after every character, and the arrow keys go one character at a time (the three glyphs with their own clusters are tested in
    /// <c>E20CjkFallbackTests.E20_TypedCompatibilityIdeographsAndDescriptionSequence_AsTyped</c>).
    /// </summary>
    [Fact]
    public void Uni16_DescriptionSequence_CaretAfterEveryCharacter()
    {
        const string sequence = "\u2FF1\u4E95\u86D9";
        Assert.All(Enumerable.Range(0, sequence.Length + 1), i => Assert.True(EditUnits.IsStop(sequence, i)));
        Assert.Equal(1, EditUnits.Next(sequence, 0));
        Assert.Equal(2, EditUnits.Next(sequence, 1));
        Assert.Equal(2, EditUnits.Previous(sequence, 3));
    }
}

/// <summary>
/// M7, packages uni-15 and uni-16: the lists of field names in the properties panel of a calculated field (the fields of a formula and the
/// fields a calculation works over) are sorted by the platform, PDF-Knead has no ordering of its own for ideographs, kana or Hangul.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class Uni16FieldNameOrderUiTests
{
    /// <summary>
    /// UNI 18.1.7 sentences 20–21, 18.4.5 sentence 9, 18.6.5 sentences 39–46: fields named with ideographs of the basic block and of extension
    /// B, the kana U+1B00E, U+1B001, U+1B00F and the Hangul „각“, „가“ and „가“ with the final jamo U+11A8 — the lists of a formula field give
    /// them in exactly the order of <see cref="StringComparer.CurrentCulture"/>; the two equivalent spellings of „각“ stand next to each other.
    /// </summary>
    [Fact]
    public void Uni16_FieldNameLists_InTheOrderOfThePlatform()
    {
        var path = TestFiles.Write(TestPdf.Simple(1).Build(), "uni16-feldnamen.pdf");
        UiSession.Run(async () =>
        {
            var doc = new DocumentViewModel(path, PdfDocument.Open(path));
            try
            {
                string[] names = ["\u4E2D", "\u4E00", "\U0002000B", "\u4E01", "\U00020000", "\U0001B00E", "\U0001B001", "\U0001B00F", "\uAC01", "\uAC00", "\uAC00\u11A8"];
                var y = 800.0;
                foreach (var name in names)
                {
                    doc.AddElement(0, new FieldElement { Rect = new PdfRectangle(50, y - 14, 150, y), Spec = new FieldSpec { Name = name }, Value = "" }, "Test");
                    y -= 20;
                }
                var sum = new FieldElement
                {
                    Rect = new PdfRectangle(300, 786, 400, 800),
                    Spec = new FieldSpec { Name = "Summe", Calculation = new FieldCalculation(CalcOperation.Formula, []) },
                    Value = "",
                };
                doc.AddElement(0, sum, "Test");
                doc.Tool = ViewTool.DesignForm;
                doc.SelectFields([new ElementRef(0, sum)]);
                var properties = new FieldPropertiesViewModel();
                properties.Refresh(doc);
                Assert.True(properties.IsFormula);
                var expected = names.Order(StringComparer.CurrentCulture).ToArray();
                Assert.Equal(expected, properties.CalculationSources.Select(s => s.Name));
                Assert.Equal(expected.Select(FieldFormula.Escape), properties.FormulaFields);
                Assert.Equal(1, Math.Abs(Array.IndexOf(expected, "\uAC01") - Array.IndexOf(expected, "\uAC00\u11A8")));
            }
            finally
            {
                await doc.Renderer.WaitUntilUnused([doc.Document]).WaitAsync(TimeSpan.FromSeconds(10));
                doc.Dispose();
            }
        });
    }
}
