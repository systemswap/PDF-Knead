using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Dokument bereinigen“ → form fields that show something other than their stored value (docs/RECHERCHE-VERSTECKEN.md, B-7): found, named
/// and drawn anew from the value; fields that show their value — also formatted, as a comb, as a password — give no finding.
/// </summary>
public sealed class FieldMismatchTests
{
    private const string Font = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>";

    /// <summary>A page with the given widgets (objects 10, 11 …) and their appearances (objects 30, 31 …).</summary>
    private static PdfDocument Form(params (string Widget, string? Shown)[] fields)
    {
        var objects = new Dictionary<int, object>
        {
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = Font,
            [7] = ("", "BT /F1 12 Tf 72 780 Td (Formular) Tj ET"),
        };
        var refs = new List<string>();
        for (var i = 0; i < fields.Length; i++)
        {
            var (widget, shown) = fields[i];
            var ap = shown is null ? "" : $"/AP<</N {30 + i} 0 R>>";
            objects[10 + i] = $"<</Type/Annot/Subtype/Widget/Rect[70 {700 - 40 * i} 300 {725 - 40 * i}]/P 4 0 R{ap}{widget}>>";
            if (shown is not null)
                objects[30 + i] = ("/Type/XObject/Subtype/Form/BBox[0 0 230 25]/Resources<</Font<</F1 3 0 R>>>>",
                    $"/Tx BMC BT /F1 12 Tf 2 8 Td ({shown}) Tj ET EMC");
            refs.Add($"{10 + i} 0 R");
        }
        objects[1] = $"<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[{string.Join(' ', refs)}]/DA(/Helv 0 Tf 0 g)>>>>";
        objects[4] = $"<</Type/Page/Parent 2 0 R/Contents 7 0 R/Annots[{string.Join(' ', refs)}]/Resources<</Font<</F1 3 0 R>>>>>>";
        return PdfDocument.Open(CraftedFileTests.Build(objects));
    }

    private static CleanupFinding? Finding(PdfDocument doc) =>
        DocumentCleanup.Inspect(doc, PageItem.From(doc)).FirstOrDefault(f => f.Item == CleanupItems.FieldValueMismatch);

    private static PdfDocument Cleaned(PdfDocument doc)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Cleanup = CleanupItems.FieldValueMismatch });
        return PdfDocument.Open(output.ToArray());
    }

    /// <summary>What the widget's appearance shows, as text.</summary>
    private static string Shown(PdfDocument doc, int index)
    {
        var page = doc.Pages[0];
        var widget = (PdfDictionary)page.Dictionary.GetArray(PdfName.Get("Annots"))!.Get(index)!;
        var list = PageInterpreter.BuildAnnotations(doc, page, [widget]);
        return PageText.Create(list.Text, list.UnreadableGlyphs).Text;
    }

    [Fact]
    public void TextField_ShowsAnotherAmount_FoundNamedAndShownAnewFromTheValue()
    {
        using var doc = Form(("/FT/Tx/T(betrag)/V(1000 EUR)", "100 EUR"), ("/FT/Tx/T(ort)/V(Musterstadt)", "Musterstadt"));
        var finding = Finding(doc);
        Assert.NotNull(finding);
        Assert.Equal(1, finding.Count);
        Assert.Equal("betrag", finding.Detail);

        using var cleaned = Cleaned(doc);
        Assert.Null(Finding(cleaned));
        Assert.Contains("1000 EUR", Shown(cleaned, 0));
        Assert.Equal("Musterstadt", Shown(cleaned, 1).Trim());
        var widget = (PdfDictionary)cleaned.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!;
        Assert.Equal("1000 EUR", FormField.Of(widget)!.StoredValue);   // the value stays
    }

    [Fact]
    public void Places_TheMismatchedFieldWithItsStoredValue()
    {
        using var doc = Form(("/FT/Tx/T(betrag)/V(1000 EUR)", "100 EUR"), ("/FT/Tx/T(ort)/V(Musterstadt)", "Musterstadt"));
        var place = Assert.Single(DocumentCleanup.Places(PageItem.From(doc)[0], CleanupItems.FieldValueMismatch));
        Assert.Equal(new PdfRectangle(70, 700, 300, 725), place.Bounds);
        Assert.Equal("1000 EUR", place.Value);
    }

    [Fact]
    public void FieldsThatShowTheirValue_NoFinding()
    {
        var number = "AFNumber_Format(2, 2, 0, 0, \"\", false);";
        var formatted = FieldFormat.Parse(number).Display("1234.5");
        Assert.NotEqual("1234.5", formatted);   // the format changes what is shown
        using var doc = Form(
            ("/FT/Tx/T(name)/V(Max Mustermann)", "Max  Mustermann"),
            ($"/FT/Tx/T(summe)/V(1234.5)/AA<</F<</S/JavaScript/JS({number})>>>>", formatted),
            ("/FT/Tx/T(kennwort)/Ff 8192/V(geheim)", "******"),
            ("/FT/Tx/T(plz)/Ff 16777216/MaxLen 5/V(12345)", "1 2 3 4 5"),
            ("/FT/Tx/T(eigen)/V(1234.5)/AA<</F<</S/JavaScript/JS(eigenesFormat\\(\\);)>>>>", "eins zwei"),   // a script the program does not know
            ("/FT/Ch/Ff 131072/T(land)/Opt[[(de)(Deutschland)]]/V(de)", "Deutschland"),
            ("/FT/Tx/T(leer)", ""),
            ("/FT/Btn/Ff 32768/T(wahl)/AS/a/AP<</N<</a 30 0 R/Off 31 0 R>>>>", null));   // a radio button without a value: its state is all there is
        Assert.Null(Finding(doc));
    }

    [Fact]
    public void CheckBox_ShowsATickTheValueHasNot_FoundAndSwitchedToTheValue()
    {
        using var doc = Form(("/FT/Btn/T(zustimmung)/V/Off/AS/Yes/AP<</N<</Yes 30 0 R/Off 31 0 R>>>>", null),
                             ("/FT/Btn/T(ok)/V/Yes/AS/Yes/AP<</N<</Yes 30 0 R/Off 31 0 R>>>>", null));
        Assert.Equal(1, Finding(doc)?.Count);
        using var cleaned = Cleaned(doc);
        Assert.Null(Finding(cleaned));
        var annots = cleaned.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        Assert.Equal("Off", FormField.Of((PdfDictionary)annots.Get(0)!)!.State);
        Assert.Equal("Yes", FormField.Of((PdfDictionary)annots.Get(1)!)!.State);
    }
}
