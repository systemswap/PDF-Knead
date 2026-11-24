using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Pages of several files written into one (KONZEPT.md, section 5.2, „Seiten zusammenführen“): what the writer must carry over beyond the
/// pages themselves so that they look the same afterwards.
/// </summary>
public sealed class MergeWriterTests
{
    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    /// <summary>
    /// A form whose field values have no appearance of their own: its /AcroForm asks readers to draw them (/NeedAppearances). Merged into a
    /// file without that entry, the written form keeps the target's — the values would be invisible everywhere. The writer gives such fields
    /// the appearance the display draws, so they look as in their own file.
    /// </summary>
    [Fact]
    public void FieldsOfAFormWithNeedAppearances_MergedIntoAnother_KeepTheirVisibleValues()
    {
        var pdf = TestPdf.Simple(1);
        var page = (PdfDictionary)pdf.Get(5);
        // an empty appearance, as form editors leave it when they rely on the reader
        var empty = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 200, 20))), []));
        var widget = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Tx")), ("T", S("Name")),
            ("V", S("Max Mustermann")), ("DA", S("/Helv 12 Tf 0 g")), ("Rect", TestPdf.Box(100, 500, 300, 520)), ("F", PdfInteger.Of(4)), ("P", TestPdf.Ref(5)),
            ("AP", TestPdf.Dict(("N", TestPdf.Ref(empty))))));
        page.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(widget)]));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(widget)])),
            ("NeedAppearances", PdfBoolean.True)));
        using var form = PdfDocument.Open(pdf.Build());
        Assert.Contains("Max Mustermann", PageText.Create(PageInterpreter.Build(form, form.Pages[0]).Text).Text);

        using var target = BlankPage.CreateDocument(595, 842, 1);
        using var output = new MemoryStream();
        PdfWriter.Write(target, [.. PageItem.From(target), .. PageItem.From(form)], output);
        using var merged = PdfDocument.Open(output.ToArray());
        Assert.NotEqual(true, merged.Catalog!.GetDictionary(PdfName.Get("AcroForm"))?.GetBoolean(PdfName.Get("NeedAppearances")));
        Assert.Contains("Max Mustermann", PageText.Create(PageInterpreter.Build(merged, merged.Pages[1]).Text).Text);
        var written = Assert.IsType<PdfDictionary>(merged.Pages[1].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0));
        Assert.Equal("Max Mustermann", FormField.Of(written)!.StoredValue);
    }

    /// <summary>
    /// A form whose text field takes its default appearance and alignment from the form (/DA and /Q of its /AcroForm, inherited, ISO 32000-2,
    /// 12.7.4.3), a check box beside it, merged into a file whose form has another /DA and no /Q (docs/FEHLERGEDAECHTNIS.md, No. 690): the
    /// text field carries its own form's defaults now; the check box (no variable text) gets none. Before, they went with the inserted form —
    /// the text field took the copy's size and colour, or had none at all.
    /// </summary>
    [Fact]
    public void FieldsTakingTheirFormsDefaults_MergedIntoAnother_KeepThem()
    {
        var pdf = TestPdf.Simple(1);
        var page = (PdfDictionary)pdf.Get(5);
        var text = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Tx")), ("T", S("Name")),
            ("V", S("Max Mustermann")), ("Rect", TestPdf.Box(100, 500, 300, 520)), ("F", PdfInteger.Of(4)), ("P", TestPdf.Ref(5))));
        var box = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("FT", TestPdf.N("Btn")), ("T", S("Ja")),
            ("Rect", TestPdf.Box(100, 450, 115, 465)), ("F", PdfInteger.Of(4)), ("P", TestPdf.Ref(5))));
        page.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(text), TestPdf.Ref(box)]));
        var helv = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(text), TestPdf.Ref(box)])),
            ("DA", S("/Helv 9 Tf 0 0 1 rg")), ("Q", PdfInteger.Of(1)), ("DR", TestPdf.Dict(("Font", TestPdf.Dict(("Helv", TestPdf.Ref(pdf.Add(helv)))))))));
        using var form = PdfDocument.Open(pdf.Build());

        var main = TestPdf.Simple(1);
        ((PdfDictionary)main.Get(main.RootNumber)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray()), ("DA", S("/Helv 14 Tf 1 0 0 rg"))));
        using var target = PdfDocument.Open(main.Build());
        using var output = new MemoryStream();
        PdfWriter.Write(target, [.. PageItem.From(target), .. PageItem.From(form)], output);
        using var merged = PdfDocument.Open(output.ToArray());
        var annots = merged.Pages[1].Dictionary.GetArray(PdfName.Get("Annots"))!;
        var writtenText = Enumerable.Range(0, annots.Count).Select(annots.Get).OfType<PdfDictionary>().Single(a => a.GetName(PdfName.Get("FT"))?.Value == "Tx");
        Assert.Equal("/Helv 9 Tf 0 0 1 rg", writtenText.GetString(PdfName.Get("DA"))!.ToText());
        Assert.Equal(1, writtenText.GetInteger(PdfName.Get("Q")));
        var writtenBox = Enumerable.Range(0, annots.Count).Select(annots.Get).OfType<PdfDictionary>().Single(a => a.GetName(PdfName.Get("FT"))?.Value == "Btn");
        Assert.False(writtenBox.ContainsKey(PdfName.Get("DA")));
        // the copy's own defaults stay
        Assert.Equal("/Helv 14 Tf 1 0 0 rg", merged.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetString(PdfName.Get("DA"))!.ToText());
    }

    /// <summary>
    /// A tagged file merged into another: its page and its annotation lose their keys into the parent tree (/StructParents, /StructParent),
    /// because only the main file's structure tree is written — kept, the key handed the page's content to a stranger's element (ISO 32000-2,
    /// 14.7.5.4; docs/FEHLERGEDAECHTNIS.md, No. 435).
    /// </summary>
    [Fact]
    public void TaggedFileMergedIntoAnother_LosesItsStructureKeys()
    {
        var pdf = TestPdf.Simple(1);
        var page = (PdfDictionary)pdf.Get(5);
        var root = pdf.Add(new PdfDictionary());
        var element = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("StructElem")), ("S", TestPdf.N("P")), ("P", TestPdf.Ref(root)), ("Pg", TestPdf.Ref(5)), ("K", PdfInteger.Of(0))));
        pdf.Replace(root, TestPdf.Dict(("Type", TestPdf.N("StructTreeRoot")), ("K", new PdfArray([TestPdf.Ref(element)])),
            ("ParentTree", TestPdf.Dict(("Nums", new PdfArray([PdfInteger.Of(0), new PdfArray([TestPdf.Ref(element)]), PdfInteger.Of(1), TestPdf.Ref(element)])))),
            ("ParentTreeNextKey", PdfInteger.Of(2))));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("StructTreeRoot"), TestPdf.Ref(root));
        var note = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Text")), ("Rect", TestPdf.Box(100, 500, 120, 520)),
            ("Contents", S("Notiz")), ("StructParent", PdfInteger.Of(1)), ("P", TestPdf.Ref(5))));
        page.Set(PdfName.Get("StructParents"), PdfInteger.Of(0));
        page.Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(note)]));
        pdf.Replace(4, TestPdf.FlateStream("/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Absatz) Tj ET EMC"u8.ToArray()));
        using var tagged = PdfDocument.Open(pdf.Build());

        using var target = BlankPage.CreateDocument(595, 842, 1);
        using var output = new MemoryStream();
        PdfWriter.Write(target, [.. PageItem.From(target), .. PageItem.From(tagged)], output);
        using var merged = PdfDocument.Open(output.ToArray());
        var written = merged.Pages[1].Dictionary;
        Assert.False(written.ContainsKey(PdfName.Get("StructParents")));
        var annot = Assert.IsType<PdfDictionary>(written.GetArray(PdfName.Get("Annots"))!.Get(0));
        Assert.False(annot.ContainsKey(PdfName.Get("StructParent")));
        Assert.Contains("Absatz", PageText.Create(PageInterpreter.Build(merged, merged.Pages[1]).Text).Text);
    }

    /// <summary>A file with one layer „Notizen“; its default configuration with <paramref name="name"/> (none: null).</summary>
    private static PdfDocument WithLayer(string? name)
    {
        var pdf = TestPdf.Simple(1);
        var layer = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", S("Notizen"))));
        var config = TestPdf.Dict(("Order", new PdfArray([TestPdf.Ref(layer)])), ("OFF", new PdfArray([TestPdf.Ref(layer)])));
        if (name is not null) config.Set(PdfName.Get("Name"), S(name));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(layer)])), ("D", config)));
        return PdfDocument.Open(pdf.Build());
    }

    private static PdfDictionary MergedDefaultConfiguration(PdfDocument inserted)
    {
        using var target = BlankPage.CreateDocument(595, 842, 1);
        using var output = new MemoryStream();
        PdfWriter.Write(target, [.. PageItem.From(target), .. PageItem.From(inserted)], output);
        using var merged = PdfDocument.Open(output.ToArray());
        var oc = merged.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!;
        Assert.Single(oc.GetArray(PdfName.Get("OCGs"))!);
        return oc.GetDictionary(PdfName.Get("D"))!;
    }

    /// <summary>
    /// Layers only in the inserted file, its default configuration without a name: the merged one gets „Standard“ (ISO 14289-1, 7.10;
    /// docs/FEHLERGEDAECHTNIS.md, No. 653). Before: a new /D with /OFF and /Order only.
    /// </summary>
    [Fact]
    public void LayersOnlyInTheInsertedFile_DefaultConfigurationHasAName()
    {
        using var inserted = WithLayer(null);
        var config = MergedDefaultConfiguration(inserted);
        Assert.Equal(Core.Localization.Tr.T("Standard"), config.GetString(PdfName.Get("Name"))?.ToText());
        Assert.Single(config.GetArray(PdfName.Get("OFF"))!);
    }

    /// <summary>An empty name counts as none (ISO 14289-1, 7.10): „Standard“. Before: the empty name was copied.</summary>
    [Fact]
    public void LayersOnlyInTheInsertedFile_EmptyName_BecomesStandard()
    {
        using var inserted = WithLayer("");
        Assert.Equal(Core.Localization.Tr.T("Standard"), MergedDefaultConfiguration(inserted).GetString(PdfName.Get("Name"))?.ToText());
    }

    /// <summary>The inserted file names its default configuration: the merged one takes that name. Before: none.</summary>
    [Fact]
    public void LayersOnlyInTheInsertedFile_DefaultConfigurationKeepsItsName()
    {
        using var inserted = WithLayer("Druckansicht");
        Assert.Equal("Druckansicht", MergedDefaultConfiguration(inserted).GetString(PdfName.Get("Name"))?.ToText());
    }

    /// <summary>
    /// 8.11.4.3 when merging: a layer of an inserted file keeps the state it had there — off under /BaseState /OFF (not in its /OFF list) stays
    /// off, an /ON layer of a file with /BaseState /OFF stays on; the main file's states stay. Before, only /OFF and /Order were taken over: the
    /// inserted file's hidden layers became visible after merging (M6 draft k-811, privacy; docs/FEHLERGEDAECHTNIS.md, No. 1125).
    /// </summary>
    [Fact]
    public void InsertedLayers_KeepTheirState()
    {
        var pdf = TestPdf.Simple(1);
        var hidden = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", S("Gehaltsliste"))));
        var shown = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", S("Text"))));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("OCProperties"), TestPdf.Dict(("OCGs", new PdfArray([TestPdf.Ref(hidden), TestPdf.Ref(shown)])),
            ("D", TestPdf.Dict(("BaseState", TestPdf.N("OFF")), ("ON", new PdfArray([TestPdf.Ref(shown)]))))));
        using var inserted = PdfDocument.Open(pdf.Build());
        using var target = BlankPage.CreateDocument(595, 842, 1);
        using var output = new MemoryStream();
        PdfWriter.Write(target, [.. PageItem.From(target), .. PageItem.From(inserted)], output);
        using var merged = PdfDocument.Open(output.ToArray());
        var layers = merged.Catalog!.GetDictionary(PdfName.Get("OCProperties"))!.GetArray(PdfName.Get("OCGs"))!;
        PdfObject Named(string name) => Enumerable.Range(0, layers.Count).Select(layers.Get).First(l => l.AsDictionary()!.GetString(PdfName.Get("Name"))!.ToText() == name);
        var optional = Core.Pdf.Content.OptionalContent.For(merged);
        Assert.True(optional.IsHidden(Named("Gehaltsliste"), null));
        Assert.False(optional.IsHidden(Named("Text"), null));
    }
}
