using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Labels of new form fields for screen readers (docs/ABLAUFPLAN.md, 3.3 d; ISO 14289-1, 7.18.1; ISO 14289-2, 8.10.2.3; docs/FEHLERGEDAECHTNIS.md,
/// No. 493): /TU from the tooltip or a meaningful name, never a technical one; /Contents at every widget, the option at each radio button.
/// </summary>
public sealed class FieldLabelTests
{
    private static PdfRectangle R(double l, double b, double w, double h) => new(l, b, l + w, b + h);

    /// <summary>Saves a page with these elements and returns the widgets of the saved page by their /T (of the field or the parent).</summary>
    private static List<PdfDictionary> Widgets(params PageElement[] elements)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements(elements);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output);
        var saved = PdfDocument.Open(output.ToArray());
        var annots = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        return [.. Enumerable.Range(0, annots.Count).Select(i => (PdfDictionary)annots.Get(i)).Where(a => a.GetName(PdfName.Subtype)?.Value == "Widget")];
    }

    private static string? Text(PdfDictionary dict, string key) => dict.GetString(PdfName.Get(key))?.ToText();

    /// <summary>A radio group „Zahlungsart“ with „Überweisung“ and „Lastschrift“: each button says its option (before: none).</summary>
    [Fact]
    public void RadioGroup_EachWidgetHasContents()
    {
        var radio = new FieldSpec { Kind = FormFieldKind.Radio, Name = "Zahlungsart", BorderColor = 0, FillColor = 0xFFFFFF };
        var widgets = Widgets(
            new FieldElement { Rect = R(72, 620, 14, 14), Value = "Off", Spec = radio with { ExportValue = "Überweisung" } },
            new FieldElement { Rect = R(172, 620, 14, 14), Value = "Off", Spec = radio with { ExportValue = "Lastschrift" } });
        Assert.Equal(["Überweisung", "Lastschrift"], widgets.Select(w => Text(w, "Contents")));
        Assert.Equal("Zahlungsart", Text(widgets[0].GetDictionary(PdfName.Get("Parent"))!, "TU"));
    }

    /// <summary>
    /// A text field with a meaningful name gets it as /TU and /Contents; one with a technical name („Text1“) or none gets no label — never
    /// the name (before: no /TU at all); a tooltip always wins.
    /// </summary>
    [Fact]
    public void NewTextField_LabelFromNameOrTooltip_NeverTechnical()
    {
        var widgets = Widgets(
            new FieldElement { Rect = R(72, 700, 150, 20), Spec = new FieldSpec { Name = "Vorname" } },
            new FieldElement { Rect = R(72, 660, 150, 20), Spec = new FieldSpec { Name = "Text1" } },
            new FieldElement { Rect = R(72, 620, 150, 20), Spec = new FieldSpec { Name = "Feld_7", Tooltip = "Postleitzahl" } });
        Assert.Equal(("Vorname", "Vorname"), (Text(widgets[0], "TU"), Text(widgets[0], "Contents")));
        Assert.Null(Text(widgets[1], "TU"));
        Assert.Null(Text(widgets[1], "Contents"));
        Assert.Equal(("Postleitzahl", "Postleitzahl"), (Text(widgets[2], "TU"), Text(widgets[2], "Contents")));
    }

    /// <summary>A new signature field says „Unterschrift“, without a name (before: no /TU).</summary>
    [Fact]
    public void NewSignatureField_HasNeutralTu()
    {
        var widget = Assert.Single(Widgets(new SignatureFieldElement { Rect = R(60, 200, 200, 50), FieldName = "Unterschrift Max Mustermann" }));
        Assert.Equal(Tr.T("Unterschrift"), Text(widget, "TU"));
    }

    [Theory]
    [InlineData("Text1", true)]
    [InlineData("Textfeld12", true)]
    [InlineData("form1[0].Name[0]", true)]
    [InlineData("Feld_3", true)]
    [InlineData("Vorname", false)]
    [InlineData("Straße und Hausnummer", false)]
    [InlineData("Seite 2", false)]
    public void TechnicalName_Recognised(string name, bool technical) => Assert.Equal(technical, FieldLabels.TechnicalName(name));
}
