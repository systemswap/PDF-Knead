using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// What PDF 2.0 asks of a file beyond earlier versions, for files saved as 2.0 (ISO 32000-2; checked against the reference model of the
/// standard, docs/PRUEFUNGEN.md, section 5c).
/// </summary>
public sealed class Pdf20Tests
{
    private static PdfDocument Saved(byte[] file, string version)
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = version });
        return PdfDocument.Open(output.ToArray());
    }

    private static PdfDictionary Annotation(PdfDocument doc) => (PdfDictionary)doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0);

    /// <summary>
    /// Every annotation of some area has an appearance in PDF 2.0 (ISO 32000-2, 12.5.5): one without gets the one the display draws — markup
    /// from its properties, free text and a field with its text, a check box an empty state — and the form no longer asks readers to draw
    /// fields anew (/NeedAppearances is deprecated). As PDF 1.7 the annotation stays as it is (docs/FEHLERGEDAECHTNIS.md, No. 373).
    /// </summary>
    [Theory]
    [InlineData("<</Type/Annot/Subtype/Highlight/F 4/Rect[100 600 200 620]/QuadPoints[100 620 200 620 100 600 200 600]/C[1 1 0]>>", false)]
    [InlineData("<</Type/Annot/Subtype/Square/F 4/Rect[100 600 200 650]/C[1 0 0]>>", false)]
    [InlineData("<</Type/Annot/Subtype/FreeText/F 4/Rect[100 600 300 650]/Contents(Hinweis von Max Mustermann)/DA(/Helv 12 Tf 0 g)>>", true)]
    [InlineData("<</Type/Annot/Subtype/Widget/FT/Tx/T(Ort)/V(K\\366ln)/DA(/Helv 12 Tf 0 g)/F 4/Rect[100 600 300 620]/P 3 0 R>>", true)]
    [InlineData("<</Type/Annot/Subtype/Widget/FT/Btn/T(Ja)/F 4/Rect[100 600 120 620]/P 3 0 R/AS/Off>>", false)]
    public void AnnotationWithoutAppearance_GetsOne(string annotation, bool text)
    {
        var form = annotation.Contains("/Widget") ? "/AcroForm<</Fields[5 0 R]/NeedAppearances true/DR<</Font<</Helv 9 0 R>>>>>>" : "";
        var file = PdfARuleCases.WithAnnotation(annotation, form, "", PdfARuleCases.Obj(9, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>"))();
        using (var old = Saved(file, "1.7")) Assert.False(Annotation(old).ContainsKey(PdfName.Get("AP")));
        using var doc = Saved(file, "2.0");
        var normal = Annotation(doc).GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N"));
        var looks = normal switch
        {
            PdfStream one => [one],
            PdfDictionary states => states.Entries.Select(e => states.Get(e.Key)).OfType<PdfStream>().ToList(),
            _ => new List<PdfStream>(),
        };
        Assert.NotEmpty(looks);
        if (text) Assert.Matches(@"BT[\s\S]* Tf[\s\S]*T[jJ]", System.Text.Encoding.Latin1.GetString(doc.GetStreamData(looks[0])));
        Assert.NotEqual(true, doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))?.GetBoolean(PdfName.Get("NeedAppearances")));
    }

    /// <summary>
    /// A text field without a value and without an appearance, whose /MK asks for a red border and a white background (12.5.6.19, Table 192):
    /// saved as 2.0 — with and without /NeedAppearances — its appearance draws both, as readers draw it from /MK, and the display draws the same.
    /// Before, the copy got an empty appearance and the display drew nothing (test file issue12750; docs/FEHLERGEDAECHTNIS.md, No. 888).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyTextFieldWithBorderAndBackground_SavedAs20_AppearanceDrawsTheFrame(bool needAppearances)
    {
        var form = $"/AcroForm<</Fields[5 0 R]{(needAppearances ? "/NeedAppearances true" : "")}/DR<</Font<</Helv 9 0 R>>>>>>";
        var file = PdfARuleCases.WithAnnotation("<</Type/Annot/Subtype/Widget/FT/Tx/T(Ort)/V()/DA(/Helv 10 Tf 0 g)/MK<</BC[1 0 0]/BG[1 1 1]>>/F 4/Rect[100 600 200 614]/P 3 0 R>>",
            form, "", PdfARuleCases.Obj(9, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>"))();
        using (var source = PdfDocument.Open(file))
            Assert.NotNull(FieldAppearance.TextAppearance(source, Annotation(source)));   // the display draws it
        using var doc = Saved(file, "2.0");
        var normal = Assert.IsType<PdfStream>(Annotation(doc).GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")));
        var content = System.Text.Encoding.Latin1.GetString(doc.GetStreamData(normal));
        Assert.Contains("1 1 1 rg", content);
        Assert.Matches(@"1 0 0 RG[\s\S]* re S", content);
    }

    /// <summary>
    /// A check box in its on state and a push button with a caption (/MK /CA), both without an appearance: saved as 2.0 the check box gets its
    /// states, named after its state, with the check mark in the on state and on shown, the push button an appearance with its caption — as
    /// readers draw them; the display draws the same. Before, both got an empty appearance (test file checkbox_no_appearance, resetform_parent;
    /// docs/FEHLERGEDAECHTNIS.md, No. 889).
    /// </summary>
    [Fact]
    public void CheckBoxAndPushButtonWithoutAppearance_SavedAs20_DrawTheirStateAndCaption()
    {
        var box = "<</Type/Annot/Subtype/Widget/FT/Btn/T(Ja)/V/Yes/AS/Yes/F 4/Rect[100 600 120 620]/P 3 0 R>>";
        var file = PdfARuleCases.WithAnnotation(box, "/AcroForm<</Fields[5 0 R]/DR<</Font<</Helv 9 0 R>>>>/DA(/Helv 0 Tf 0 g)>>", "",
            PdfARuleCases.Obj(9, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>"))();
        using (var source = PdfDocument.Open(file)) Assert.NotNull(FieldAppearance.TextAppearance(source, Annotation(source)));
        using (var doc = Saved(file, "2.0"))
        {
            var widget = Annotation(doc);
            var states = Assert.IsType<PdfDictionary>(widget.GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")));
            Assert.Equal("Yes", widget.GetName(PdfName.Get("AS"))?.Value);
            var on = Assert.IsType<PdfStream>(states.Get(PdfName.Get("Yes")));
            Assert.IsType<PdfStream>(states.Get(PdfName.Get("Off")));
            Assert.NotEmpty(doc.GetStreamData(on));
        }

        var button = "<</Type/Annot/Subtype/Widget/FT/Btn/Ff 65536/T(Zurueck)/MK<</CA(Neu beginnen)>>/DA(/Helv 10 Tf 0 g)/F 4/Rect[100 600 200 630]/P 3 0 R>>";
        file = PdfARuleCases.WithAnnotation(button, "/AcroForm<</Fields[5 0 R]/DR<</Font<</Helv 9 0 R>>>>>>", "",
            PdfARuleCases.Obj(9, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>"))();
        using (var source = PdfDocument.Open(file)) Assert.NotNull(FieldAppearance.TextAppearance(source, Annotation(source)));
        using (var doc = Saved(file, "2.0"))
        {
            var normal = Assert.IsType<PdfStream>(Annotation(doc).GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")));
            Assert.Matches(@"BT[\s\S]* Tf[\s\S]*T[jJ]", System.Text.Encoding.Latin1.GetString(doc.GetStreamData(normal)));
        }
    }

    /// <summary>
    /// The push button with a caption and without an appearance, written as PDF/A: its appearance is a set of states (ISO 19005-2, 6.3.3) whose
    /// state shows the caption — the copy conforms and draws what the display draws. Before, the appearance the writer built (No. 889) went in
    /// as a stream and broke 6.3.3; given as a state, the PDF/A step took it for missing and put an empty one in its place (test file
    /// resetform_parent; docs/FEHLERGEDAECHTNIS.md, No. 919).
    /// </summary>
    [Fact]
    public void PushButtonWithoutAppearance_SavedAsPdfA_StateWithItsCaption()
    {
        var button = "<</Type/Annot/Subtype/Widget/FT/Btn/Ff 65536/T(Zurueck)/MK<</CA(Neu beginnen)>>/DA(/Helv 10 Tf 0 g)/F 4/Rect[100 600 200 630]/P 3 0 R>>";
        var file = PdfARuleCases.WithAnnotation(button, "/AcroForm<</Fields[5 0 R]/DR<</Font<</Helv 9 0 R>>>>>>", "",
            PdfARuleCases.Obj(9, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>"))();
        var result = PdfATests.Write(file);
        Assert.True(result.Conforms, PdfATests.Describe(result.Violations));
        using var doc = PdfDocument.Open(result.Bytes);
        var widget = Annotation(doc);
        var states = Assert.IsType<PdfDictionary>(widget.GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")));
        var shown = widget.GetName(PdfName.Get("AS"))!.Value;
        var look = Assert.IsType<PdfStream>(states.Get(PdfName.Get(shown)));
        Assert.Matches(@"BT[\s\S]* Tf[\s\S]*T[jJ]", System.Text.Encoding.Latin1.GetString(doc.GetStreamData(look)));
        Assert.Contains("Neu beginnen", string.Concat(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text)));
    }

    /// <summary>
    /// A free text in Arabic without an appearance: the display reads its letters also in their joined forms and the lam-alef ligature — the
    /// display font of formatted text has no ToUnicode, and the cmap knows only the base forms (Core.Fonts.Layout.GlyphTexts). Before, the
    /// display read a few scattered letters (223 of 985 characters in test file freetext_no_appearance; docs/FEHLERGEDAECHTNIS.md, No. 920).
    /// </summary>
    [Fact]
    public void FreeTextWithoutAppearance_Arabic_DisplayReadsTheJoinedLetters()
    {
        const string text = "\u0639\u0644\u0645 \u0633\u0644\u0627\u0645";
        var hex = "<FEFF" + string.Concat(text.Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture))) + ">";
        var note = $"<</Type/Annot/Subtype/FreeText/Contents{hex}/DA(/Helv 12 Tf 0 0 0 rg)/F 4/Rect[100 600 300 630]/P 3 0 R>>";
        using var doc = PdfDocument.Open(PdfARuleCases.WithAnnotation(note, "", "")());
        var read = string.Concat(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text));
        // every letter of the text (none of them shares its glyph with another letter); the order is visual (a limit of the own text reading)
        foreach (var letter in text.Where(c => c != ' ').GroupBy(c => c))
            Assert.True(read.Count(c => c == letter.Key) >= letter.Count(), $"U+{(int)letter.Key:X4} gelesen {read.Count(c => c == letter.Key)}×: {read}");
    }

    /// <summary>
    /// A check box without an appearance whose value says off while its state says on, and a radio group whose value names the button with an
    /// appearance while another button without one is also on: saved as 2.0 or PDF/A the buttons without one get their appearance — the
    /// values of the fields stay as read. Before, the state shown was written as the value: the check box became on, the group named the
    /// other button (qpdf button-set-broken-out; docs/FEHLERGEDAECHTNIS.md, No. 916).
    /// </summary>
    [Theory]
    [InlineData("2.0")]
    [InlineData("PDF/A-2b")]
    public void ButtonsWithoutAppearance_Settled_KeepTheValueAsRead(string target)
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[5 0 R 6 0 R]>>>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Annots[5 0 R 7 0 R 8 0 R]>>",
            [4] = ("", "q Q"),
            [5] = "<</Type/Annot/Subtype/Widget/FT/Btn/T(Ja)/V/Off/AS/Yes/F 4/Rect[100 700 120 720]/P 3 0 R>>",
            [6] = "<</FT/Btn/Ff 49152/T(Wahl)/V/2/Kids[7 0 R 8 0 R]>>",
            [7] = "<</Type/Annot/Subtype/Widget/Parent 6 0 R/AS/1/F 4/Rect[100 600 120 620]/P 3 0 R>>",
            [8] = "<</Type/Annot/Subtype/Widget/Parent 6 0 R/AS/2/F 4/Rect[100 560 120 580]/P 3 0 R/AP<</N<</2 9 0 R/Off 10 0 R>>>>>>",
            [9] = ("/Type/XObject/Subtype/Form/BBox[0 0 20 20]", "0 g 4 4 12 12 re f"),
            [10] = ("/Type/XObject/Subtype/Form/BBox[0 0 20 20]", ""),
        });
        using var doc = target == "2.0" ? Saved(file, "2.0") : PdfDocument.Open(PdfATests.Write(file).Bytes);
        var annots = doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        var box = (PdfDictionary)annots.Get(0)!;
        var off = (PdfDictionary)annots.Get(1)!;
        Assert.Equal("Off", box.GetName(PdfName.Get("V"))?.Value);
        Assert.Equal("Yes", box.GetName(PdfName.Get("AS"))?.Value);
        Assert.IsType<PdfDictionary>(box.GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")));
        Assert.Equal("1", off.GetName(PdfName.Get("AS"))?.Value);
        Assert.IsType<PdfDictionary>(off.GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")));
        Assert.Equal("2", off.GetDictionary(PdfName.Parent)!.GetName(PdfName.Get("V"))?.Value);
    }

    /// <summary>
    /// A file attachment without an appearance: saved as 2.0 its appearance draws the push pin (the default icon, 12.5.6.15 Table 187) — two of
    /// the three reference readers draw one; the display the same. Before, an empty appearance (test file example_041;
    /// docs/FEHLERGEDAECHTNIS.md, No. 893).
    /// </summary>
    [Fact]
    public void FileAttachmentWithoutAppearance_SavedAs20_DrawsTheIcon()
    {
        var file = PdfARuleCases.WithAnnotation("<</Type/Annot/Subtype/FileAttachment/FS(Anhang.txt)/Contents(Anhang)/F 4/Rect[100 600 114 614]/P 3 0 R>>", "", "")();
        using var doc = Saved(file, "2.0");
        var normal = Assert.IsType<PdfStream>(Annotation(doc).GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")));
        Assert.Matches(@" re b[\s\S]* re b", System.Text.Encoding.Latin1.GetString(doc.GetStreamData(normal)));
    }

    /// <summary>
    /// Free text without an appearance, with a colour (/C) and a border of width 1 (/Border): saved as 2.0 its appearance draws the box the
    /// reference readers draw — the background in that colour, the border in the colour of the text — under the text; the display the same.
    /// Before, only the text (test file annotation_freetext; docs/FEHLERGEDAECHTNIS.md, No. 892).
    /// </summary>
    [Fact]
    public void FreeTextWithoutAppearance_ColourAndBorder_SavedAs20_DrawsTheBox()
    {
        var note = "<</Type/Annot/Subtype/FreeText/Contents(Hinweis zur Lieferung)/DA(/Helv 9 Tf 0 0 1 rg)/C[1 1 0]/Border[0 0 1]/F 4" +
                   "/Rect[100 600 260 620]/P 3 0 R>>";
        var file = PdfARuleCases.WithAnnotation(note, "", "")();
        using (var source = PdfDocument.Open(file))
        {
            var shown = FieldAppearance.TextAppearance(source, Annotation(source));
            Assert.NotNull(shown);
            Assert.Contains("1 1 0 rg", System.Text.Encoding.Latin1.GetString(shown.Value.Appearance.Form.RawData ?? []));
        }
        using var doc = Saved(file, "2.0");
        var normal = Assert.IsType<PdfStream>(Annotation(doc).GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")));
        var content = System.Text.Encoding.Latin1.GetString(doc.GetStreamData(normal));
        Assert.Contains("1 1 0 rg", content);
        Assert.Matches(@"0 0 1 RG 1 w [\d. ]+ re S", content);
        Assert.Matches(@"BT[\s\S]*T[jJ]", content);
    }

    /// <summary>
    /// Free text on Arabic without an appearance, its /DA naming Helvetica (which has no Arabic letters): the appearance is set with a font per
    /// character — the Arabic word in Noto Sans Arabic, the digits in Liberation Sans —, the glyphs written in the order of the text; saved as
    /// 2.0 it is in the copy. Before, nothing was drawn (better nothing than boxes; test file freetext_no_appearance; docs/FEHLERGEDAECHTNIS.md,
    /// No. 910).
    /// </summary>
    [Fact]
    public void FreeTextWithoutAppearance_Arabic_SetWithAnArabicFont()
    {
        const string text = "سلام 12";
        var hex = "<FEFF" + string.Concat(text.Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture))) + ">";
        var note = $"<</Type/Annot/Subtype/FreeText/Contents{hex}/DA(/Helv 12 Tf 0 0 0 rg)/F 4/Rect[100 600 260 630]/P 3 0 R>>";
        var file = PdfARuleCases.WithAnnotation(note, "", "")();
        using (var source = PdfDocument.Open(file))
        {
            var shown = FieldAppearance.TextAppearance(source, Annotation(source));
            Assert.NotNull(shown);
            var fonts = shown.Value.Appearance.Form.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!;
            var names = fonts.Entries.Select(e => fonts.GetDictionary(e.Key)?.GetName(PdfName.Get("BaseFont"))?.Value ?? "").ToList();
            Assert.Contains(names, n => n.Contains("NotoSansArabic", StringComparison.Ordinal));
            Assert.Contains(names, n => n.Contains("LiberationSans", StringComparison.Ordinal));
        }
        using var doc = Saved(file, "2.0");
        var normal = Assert.IsType<PdfStream>(Annotation(doc).GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")));
        var content = System.Text.Encoding.Latin1.GetString(doc.GetStreamData(normal));
        Assert.Matches(@"BT[\s\S]*T[jJ]", content);
        // every glyph drawn is in the saved subset — the dots the font splits off a letter too (they have no text of their own)
        var subsetFonts = normal.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!;
        var widths = new HashSet<long>();
        foreach (var (key, _) in subsetFonts.Entries)
        {
            var w = subsetFonts.GetDictionary(key)!.GetArray(PdfName.Get("DescendantFonts"))!.Get(0)!.AsDictionary()!.GetArray(PdfName.Get("W"))!;
            for (var i = 0; i + 1 < w.Count; i += 2)
                if (w.Get(i) is PdfInteger first && w.Get(i + 1) is PdfArray list)
                    for (var k = 0; k < list.Count; k++) widths.Add(first.Value + k);
        }
        var drawn = System.Text.RegularExpressions.Regex.Matches(content, @"\[([^\]]*)\] TJ").SelectMany(m =>
            System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, "<([0-9A-F]+)>").SelectMany(h =>
                Enumerable.Range(0, h.Groups[1].Value.Length / 4).Select(j => Convert.ToInt64(h.Groups[1].Value.Substring(4 * j, 4), 16)))).ToList();
        Assert.NotEmpty(drawn);
        Assert.All(drawn, g => Assert.Contains(g, widths));
    }

    /// <summary>
    /// A check box whose appearance states cannot be drawn — "off" is a name instead of a stream, "on" a stream without /BBox (12.5.5 Table 170,
    /// 8.10.2 Table 93): it is drawn from its properties like a check box without an appearance; saved as 2.0 it gets both states as form
    /// XObjects with their box, on shown. Before, the display drew nothing and the copy kept the broken states (test file checkbox-bad-appearance;
    /// docs/FEHLERGEDAECHTNIS.md, No. 891).
    /// </summary>
    [Fact]
    public void CheckBoxWithBrokenAppearanceStates_SavedAs20_DrawnFromItsProperties()
    {
        var box = "<</Type/Annot/Subtype/Widget/FT/Btn/T(Zustimmung)/V/Ja/AS/Ja/MK<</BC[0 0 0.4]/BG[0.9 0.9 0.9]>>/F 4/Rect[100 600 112 612]/P 3 0 R" +
                  "/AP<</N<</Off/Off/Ja 6 0 R>>>>>>";
        var file = PdfARuleCases.WithAnnotation(box, "/AcroForm<</Fields[5 0 R]/DR<</Font<</ZaDb 9 0 R>>>>>>", "",
            PdfARuleCases.Stream(6, "/Resources<</Font<</ZaDb 9 0 R>>>>", "q 0 0 0.4 rg BT /ZaDb 10 Tf 0 0 Td (4) Tj ET Q"),
            PdfARuleCases.Obj(9, "<</Type/Font/Subtype/Type1/BaseFont/ZapfDingbats>>"))();
        using (var source = PdfDocument.Open(file)) Assert.NotNull(FieldAppearance.TextAppearance(source, Annotation(source)));   // the display draws it
        using var doc = Saved(file, "2.0");
        var widget = Annotation(doc);
        var states = Assert.IsType<PdfDictionary>(widget.GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")));
        Assert.Equal("Ja", widget.GetName(PdfName.Get("AS"))?.Value);
        foreach (var name in new[] { "Ja", "Off" })
            Assert.IsType<PdfArray>(Assert.IsType<PdfStream>(states.Get(PdfName.Get(name))).Dictionary.Get(PdfName.Get("BBox")));
        Assert.NotEmpty(doc.GetStreamData((PdfStream)states.Get(PdfName.Get("Ja"))));
    }

    /// <summary>
    /// A form with /NeedAppearances whose text fields have stored appearances that are stale: one shows an old text in its /Tx section while
    /// its value is another, one without a value draws a grey area there. Readers construct both from their values (12.7.3 Table 224,
    /// 12.7.4.3); saved as 2.0 (the flag dropped) the appearances are the stored ones updated — the /Tx section with the value, the old text
    /// and the grey area gone — and the display draws that instead of the stored one. Before, both kept their stored appearance in display
    /// and copy (test file bug1796741, text_field_own_canvas_calc; docs/FEHLERGEDAECHTNIS.md, No. 890).
    /// </summary>
    [Theory]
    [InlineData("/V(Neuer Wert)", "/Tx BMC q BT /Helv 12 Tf 0 g 2 6 Td (Alter Text) Tj ET Q EMC", "Alter Text")]
    [InlineData("", "/Tx BMC q 0.85 0.85 0.85 rg 0 0 200 20 re f Q EMC", "0.85")]
    public void StaleFieldAppearance_FormNeedsAppearances_SavedAs20_ConstructedFromTheValue(string value, string stored, string stale)
    {
        var field = $"<</Type/Annot/Subtype/Widget/FT/Tx/T(Feld){value}/DA(/Helv 12 Tf 0 g)/F 4/Rect[100 600 300 620]/P 3 0 R/AP<</N 6 0 R>>>>";
        var file = PdfARuleCases.WithAnnotation(field, "/AcroForm<</Fields[5 0 R]/NeedAppearances true/DR<</Font<</Helv 9 0 R>>>>>>", "",
            PdfARuleCases.Stream(6, "/Type/XObject/Subtype/Form/BBox[0 0 200 20]/Resources<</Font<</Helv 9 0 R>>>>", stored),
            PdfARuleCases.Obj(9, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>"))();
        using (var source = PdfDocument.Open(file))
        {
            var laidOut = FieldAppearance.TextAppearance(source, Annotation(source));
            Assert.NotNull(laidOut);
            Assert.False(laidOut.Value.OverAppearance);   // drawn instead of the stored one
        }
        using var doc = Saved(file, "2.0");
        var normal = Assert.IsType<PdfStream>(Annotation(doc).GetDictionary(PdfName.Get("AP"))?.Get(PdfName.Get("N")));
        var content = System.Text.Encoding.Latin1.GetString(doc.GetStreamData(normal));
        Assert.DoesNotContain(stale, content);
        if (value.Length > 0) Assert.Matches(@"BT[\s\S]* Tf[\s\S]*T[jJ]", content);
        Assert.False(doc.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.ContainsKey(PdfName.Get("NeedAppearances")));
    }

    /// <summary>
    /// An annotation whose appearance dictionary has no normal appearance, only the down and rollover ones: saved as 2.0 it gets the normal one
    /// the display draws (12.5.5) and keeps /D and /R (Table 170) — they went with the old dictionary (docs/FEHLERGEDAECHTNIS.md, No. 882;
    /// found by the data-keeping round trip on a test file of the PDF/A suites).
    /// </summary>
    [Fact]
    public void AppearanceWithoutNormal_SavedAs20_KeepsDownAndRollover()
    {
        var file = PdfARuleCases.WithAnnotation("<</Type/Annot/Subtype/Square/F 4/Rect[100 600 200 650]/C[1 0 0]/AP<</D 6 0 R/R 7 0 R>>>>", "", "",
            PdfARuleCases.Stream(6, "/Type/XObject/Subtype/Form/BBox[0 0 100 50]", "0 0 1 rg 0 0 100 50 re f"),
            PdfARuleCases.Stream(7, "/Type/XObject/Subtype/Form/BBox[0 0 100 50]", "0 1 0 rg 0 0 100 50 re f"))();
        using var doc = Saved(file, "2.0");
        var appearance = Annotation(doc).GetDictionary(PdfName.Get("AP"))!;
        Assert.IsType<PdfStream>(appearance.Get(PdfName.Get("N")));
        Assert.Equal("0 0 1 rg 0 0 100 50 re f", System.Text.Encoding.Latin1.GetString(doc.GetStreamData((PdfStream)appearance.Get(PdfName.Get("D")))));
        Assert.Equal("0 1 0 rg 0 0 100 50 re f", System.Text.Encoding.Latin1.GetString(doc.GetStreamData((PdfStream)appearance.Get(PdfName.Get("R")))));
    }

    /// <summary>
    /// 2.0 asked for, but written as 1.7 because the file keeps its RC4 encryption (No. 371): the form is treated as in a 1.7 file — the field
    /// keeps having no appearance, the form keeps /NeedAppearances, /DR gets no further fonts. It was settled by the version asked for
    /// (docs/FEHLERGEDAECHTNIS.md, No. 878).
    /// </summary>
    [Fact]
    public void KeptOldEncryption_AskedFor20_FormTreatedAsWritten17()
    {
        var file = PdfARuleCases.WithAnnotation("<</Type/Annot/Subtype/Widget/FT/Tx/T(Ort)/V(K\\366ln)/DA(/Helv 12 Tf 0 g)/F 4/Rect[100 600 300 620]/P 3 0 R>>",
            "/AcroForm<</Fields[5 0 R]/NeedAppearances true/DR<</Font<</Helv 9 0 R>>>>>>", "",
            PdfARuleCases.Obj(9, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>"))();
        byte[] locked;
        using (var plain = PdfDocument.Open(file))
        using (var output = new MemoryStream())
        {
            PdfWriter.Write(plain, PageItem.From(plain), output,
                new PdfSaveOptions { Version = "1.4", Protection = new PdfProtection("geheim", "chef", PdfPermissions.All) });
            locked = output.ToArray();
        }
        using var doc = PdfDocument.Open(locked, "geheim");
        Assert.Equal(PdfCipher.Rc4, doc.Security!.StreamCipher);
        using var copy = new MemoryStream();
        var result = PdfWriter.Write(doc, PageItem.From(doc), copy, new PdfSaveOptions { Version = "2.0" });
        Assert.Equal("1.7", result.Version);
        using var saved = PdfDocument.Open(copy.ToArray(), "geheim");
        Assert.False(Annotation(saved).ContainsKey(PdfName.Get("AP")));
        var form = saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!;
        Assert.True(form.GetBoolean(PdfName.Get("NeedAppearances")));
        Assert.Equal(["Helv"], form.GetDictionary(PdfName.Get("DR"))!.GetDictionary(PdfName.Get("Font"))!.Entries.Select(e => e.Key.Value).ToArray());
    }

    /// <summary>
    /// A standard font held directly in the font map of an appearance (as many programs write free text) is described in PDF 2.0 as well as one
    /// that is an object of its own (ISO 32000-2, 9.6.2.1, Table 109; docs/FEHLERGEDAECHTNIS.md, No. 372).
    /// </summary>
    [Fact]
    public void StandardFontInTheFontMapOfAnAppearance_IsDescribed()
    {
        var file = PdfARuleCases.WithAnnotation("<</Type/Annot/Subtype/FreeText/F 4/Rect[100 600 300 650]/Contents(Hinweis)/DA(/Helv 12 Tf 0 g)/AP<</N 6 0 R>>>>", "", "",
            PdfARuleCases.Stream(6, "/Type/XObject/Subtype/Form/BBox[0 0 200 50]/Resources<</Font<</Helv<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>>>>>",
                "BT /Helv 12 Tf 2 20 Td (Hinweis) Tj ET"))();
        using var doc = Saved(file, "2.0");
        var look = (PdfStream)Annotation(doc).GetDictionary(PdfName.Get("AP"))!.Get(PdfName.Get("N"));
        var font = look.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("Helv"))!;
        Assert.IsType<PdfReference>(font.GetRaw(PdfName.Get("FontDescriptor")));
        Assert.Equal("Helvetica", font.GetDictionary(PdfName.Get("FontDescriptor"))!.GetName(PdfName.Get("FontName"))?.Value);
        Assert.NotNull(font.GetArray(PdfName.Get("Widths")));
    }

    /// <summary>
    /// An embedded file names its kind in PDF 2.0 — required where it is an associated file (ISO 32000-2, 7.11.4, Table 44): a PDF as
    /// application/pdf, anything else as unknown data; a kind it has stays (docs/FEHLERGEDAECHTNIS.md, No. 375).
    /// </summary>
    [Theory]
    [InlineData("%PDF-1.7 Anlage", "", "application/pdf")]
    [InlineData("Rechnungsdaten", "", "application/octet-stream")]
    [InlineData("Rechnungsdaten", "/Subtype/text#2Fxml", "text/xml")]
    public void EmbeddedFile_NamesItsKind(string data, string kind, string expected)
    {
        var file = PdfARuleCases.WithAnnotation("<</Type/Annot/Subtype/Square/F 4/Rect[100 600 200 650]/C[1 0 0]>>",
            "/AF[7 0 R]/Names<</EmbeddedFiles<</Names[(daten)7 0 R]>>>>", "",
            PdfARuleCases.Obj(7, "<</Type/Filespec/F(daten)/UF(daten)/AFRelationship/Data/EF<</F 9 0 R>>>>"),
            PdfARuleCases.Stream(9, "/Type/EmbeddedFile" + kind, data))();
        using var doc = Saved(file, "2.0");
        var spec = (PdfDictionary)doc.Catalog!.GetArray(PdfName.Get("AF"))!.Get(0);
        var stream = (PdfStream)spec.GetDictionary(PdfName.Get("EF"))!.Get(PdfName.Get("F"));
        Assert.Equal(expected, stream.Dictionary.GetName(PdfName.Subtype)?.Value);
    }

    /// <summary>
    /// Dates in PDF 2.0 without the closing apostrophe (ISO 32000-2, 7.9.4: D:YYYYMMDDHHmmSS+HH'mm), as PDF 1.7 with it (the form of
    /// ISO 32000-1) — the date of the last change and a note made before the version was known (the note of a redaction reason)
    /// (docs/FEHLERGEDAECHTNIS.md, No. 487).
    /// </summary>
    [Theory]
    [InlineData("2.0", "D:20261020170509+02'00")]
    [InlineData("1.7", "D:20261020170509+02'00'")]
    public void Dates_InTheFormOfTheVersion(string version, string expected)
    {
        var time = new DateTimeOffset(2026, 10, 20, 17, 5, 9, TimeSpan.FromHours(2));
        var note = new PdfDictionary();
        note.Set(PdfName.Type, PdfName.Get("Annot"));
        note.Set(PdfName.Subtype, PdfName.Get("Text"));
        note.Set(PdfName.Get("Rect"), new PdfArray([PdfInteger.Of(100), PdfInteger.Of(700), PdfInteger.Of(112), PdfInteger.Of(712)]));
        note.Set(PdfName.Get("M"), new PdfString(System.Text.Encoding.ASCII.GetBytes(PdfDate.Format(time))));
        using var blank = BlankPage.CreateDocument(595, 842, 1);
        using var output = new MemoryStream();
        PdfWriter.Write(blank, PageItem.From(blank), output, new PdfSaveOptions
        {
            Version = version,
            ModificationTime = time,
            AddedAnnotations = new Dictionary<int, IReadOnlyList<PdfDictionary>> { [0] = [note] },
        });
        using var doc = PdfDocument.Open(output.ToArray());
        Assert.Equal(expected, doc.Trailer.GetDictionary(PdfName.Info)!.GetString(PdfName.Get("ModDate"))!.ToText());
        Assert.Equal(expected, Annotation(doc).GetString(PdfName.Get("M"))!.ToText());
        Assert.Equal(time, PdfDate.Parse(expected));
    }

    /// <summary>
    /// A Type 2 CIDFont with an embedded program but without /CIDToGIDMap (in PDF 1.7 optional, the identity by default): saved as 2.0 it names
    /// /Identity, which 2.0 requires (ISO 32000-2, Table 115) — the CIDFont as an object of its own and held directly in /DescendantFonts
    /// (docs/FEHLERGEDAECHTNIS.md, No. 524; found with the reference model of ISO 32000-2).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmbeddedCidFontType2_GetsItsGlyphMapInPdf20(bool ownObject)
    {
        var cid = "<</Type/Font/Subtype/CIDFontType2/BaseFont/Muster/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/FontDescriptor 7 0 R/DW 500>>";
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            [4] = ("", "BT /F1 12 Tf 72 700 Td <0001> Tj ET"),
            [5] = "<</Type/Font/Subtype/Type0/BaseFont/Muster/Encoding/Identity-H/DescendantFonts[" + (ownObject ? "6 0 R" : cid) + "]>>",
            [6] = cid,
            [7] = "<</Type/FontDescriptor/FontName/Muster/Flags 4/FontBBox[0 0 500 700]/ItalicAngle 0/Ascent 700/Descent 0/CapHeight 700/StemV 80/FontFile2 8 0 R>>",
            [8] = ("", "kein echtes Programm"),
        });
        using var doc = Saved(file, "2.0");
        var type0 = doc.Pages[0].Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
        var descendant = (PdfDictionary)type0.GetArray(PdfName.Get("DescendantFonts"))!.Get(0)!;
        Assert.Equal("Identity", descendant.GetName(PdfName.Get("CIDToGIDMap"))?.Value);
    }

    // ---- 5.3 b: forms, patterns and Type 3 fonts get the resources they borrow from the page (ISO 32000-2, 7.8.3; docs/FEHLERGEDAECHTNIS.md, No. 549)

    private const string Helvetica = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>";

    /// <summary>
    /// A page drawing form 5 (without resources of its own, as older files do) that shows text with the page's font F1;
    /// <paramref name="pageResources"/> adds entries to the page's resources.
    /// </summary>
    private static byte[] FormWithoutResources(string pageResources = "") => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = $"<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 6 0 R>>/XObject<</Fm1 5 0 R>>{pageResources}>>>>",
        [4] = ("", "q /Fm1 Do Q"),
        [5] = ("/Type/XObject/Subtype/Form/BBox[0 0 595 842]", "BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET"),
        [6] = Helvetica,
    });

    private static PdfStream Form(PdfDocument doc, int page = 0, string name = "Fm1") =>
        (PdfStream)doc.Pages[page].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get(name));

    private static string Text(PdfDocument doc, int page = 0) =>
        string.Concat(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[page]).Text.Select(t => t.Text));

    /// <summary>
    /// Saved as 2.0, a form without resources of its own gets those of the page it is drawn on — 2.0 no longer lets it inherit them; the
    /// text stays. Before, it was written as it was.
    /// </summary>
    [Fact]
    public void FormWithoutResources_GetsThoseOfItsPage()
    {
        using var doc = Saved(FormWithoutResources(), "2.0");
        var resources = Form(doc).Dictionary.GetDictionary(PdfName.Resources);
        Assert.NotNull(resources);
        Assert.Equal("Helvetica", resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!.GetName(PdfName.Get("BaseFont"))?.Value);
        Assert.Contains("Max Mustermann", Text(doc));
    }

    /// <summary>
    /// As PDF 1.7 the form gets the resources it is drawn with as well: inheriting them is deprecated there and writers should not use it
    /// (ISO 32000-1 and -2, 7.8.3; S-B B07). Before, it was written as it was (docs/FEHLERGEDAECHTNIS.md, No. 782; No. 366 still holds — never
    /// an empty /Resources on a form of the file).
    /// </summary>
    [Fact]
    public void FormWithoutResources_Pdf17_GetsThemToo()
    {
        using var doc = Saved(FormWithoutResources(), "1.7");
        var resources = Form(doc).Dictionary.GetDictionary(PdfName.Resources);
        Assert.Equal("Helvetica", resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!.GetName(PdfName.Get("BaseFont"))?.Value);
        Assert.Contains("Max Mustermann", Text(doc));
    }

    /// <summary>
    /// The resources lent to the form are written as the version has them, like everything else: as 2.0 without the page's /ProcSet, which
    /// 2.0 deprecates (ISO 32000-2, 14.2) and the writer leaves out everywhere else; as 1.7 it stays. Before, the lent copy kept it in 2.0 —
    /// found by counting the messages of the second model checker per rule (docs/FEHLERGEDAECHTNIS.md, No. 814).
    /// </summary>
    [Theory]
    [InlineData("2.0", false)]
    [InlineData("1.7", true)]
    public void FormWithoutResources_LentResourcesWithoutWhatTheVersionDropsOf(string version, bool procSet)
    {
        using var doc = Saved(FormWithoutResources("/ProcSet[/PDF/Text]"), version);
        var resources = Form(doc).Dictionary.GetDictionary(PdfName.Resources)!;
        Assert.Equal(procSet, resources.ContainsKey(PdfName.Get("ProcSet")));
        Assert.Equal(procSet, doc.Pages[0].Resources!.ContainsKey(PdfName.Get("ProcSet")));
        Assert.NotNull(resources.GetDictionary(PdfName.Get("Font"))?.GetDictionary(PdfName.Get("F1")));
        Assert.Contains("Max Mustermann", Text(doc));
    }

    /// <summary>
    /// One form drawn on two pages whose resources give one name different fonts: saved as 1.7 and as 2.0 it is written once per set of
    /// resources — the first page's form with Helvetica, the second page's name standing for a copy with Courier —, so each page shows its text
    /// with its own font, as before, and no form inherits (ISO 32000-1 and -2, 7.8.3). Before, a 1.7 copy left it inheriting, a 2.0 copy gave
    /// it the first page's font on both pages (docs/FEHLERGEDAECHTNIS.md, No. 782, 895).
    /// </summary>
    [Theory]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void FormWithoutResources_CollidingNames_OneCopyPerResources(string version)
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R 7 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 6 0 R>>/XObject<</Fm1 5 0 R>>>>>>",
            [4] = ("", "q /Fm1 Do Q"),
            [5] = ("/Type/XObject/Subtype/Form/BBox[0 0 595 842]", "BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET"),
            [6] = Helvetica,
            [7] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 8 0 R>>/XObject<</Fm1 5 0 R>>>>>>",
            [8] = "<</Type/Font/Subtype/Type1/BaseFont/Courier/Encoding/WinAnsiEncoding>>",
        });
        using var doc = Saved(file, version);
        // the width of the line tells the font: Courier gives every character 600/1000 — 14 × 7.2 = 100.8 pt at 12 pt; Helvetica is narrower
        static double Width(PdfDocument doc, int page)
        {
            var text = Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[page]).Text;
            return text.Max(t => Math.Max(t.X1, t.X2)) - text.Min(t => Math.Min(t.X0, t.X3));
        }
        Assert.True(Width(doc, 0) < 95);
        Assert.Equal(100.8, Width(doc, 1), 1);
        static string FormFont(PdfDocument doc, int page) =>
            ((PdfStream)doc.Pages[page].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Fm1"))!).Dictionary
                .GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!.GetName(PdfName.Get("BaseFont"))!.Value;
        Assert.Equal("Helvetica", FormFont(doc, 0));
        Assert.Equal("Courier", FormFont(doc, 1));
    }

    /// <summary>
    /// A Type 3 font without resources whose glyph draws with the page's F1, on two pages with different F1, the second page with a font
    /// nothing uses: its resources are written with the used names only — and its name of the Type 3 font must still stand for the copy with
    /// the second page's F1. The kept dictionary lost the names of the copies, the second page drew the glyph with the first page's
    /// resources, where its font was missing (test file pr4922, 14 pixels; docs/FEHLERGEDAECHTNIS.md, No. 915).
    /// </summary>
    [Theory]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void Type3WithoutResources_PageWithUnusedResources_KeepsItsCopy(string version)
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R 7 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</T3 5 0 R/F1 6 0 R>>>>>>",
            [4] = ("", "BT /T3 12 Tf 72 700 Td (a) Tj ET"),
            [5] = "<</Type/Font/Subtype/Type3/FontBBox[0 0 1000 1000]/FontMatrix[0.001 0 0 0.001 0 0]/CharProcs<</a 9 0 R>>" +
                  "/Encoding<</Type/Encoding/Differences[97/a]>>/FirstChar 97/LastChar 97/Widths[1000]>>",
            [6] = Helvetica,
            [7] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</T3 5 0 R/F1 8 0 R/F9 6 0 R>>>>>>",
            [8] = "<</Type/Font/Subtype/Type1/BaseFont/Courier/Encoding/WinAnsiEncoding>>",
            [9] = ("", "1000 0 d0 BT /F1 500 Tf 0 0 Td (M) Tj ET"),
        });
        using var doc = Saved(file, version);
        static string GlyphFont(PdfDocument doc, int page) =>
            doc.Pages[page].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("T3"))!
                .GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!.GetName(PdfName.Get("BaseFont"))!.Value;
        Assert.False(doc.Pages[1].Resources!.GetDictionary(PdfName.Get("Font"))!.ContainsKey(PdfName.Get("F9")));   // the unused font went
        Assert.Equal("Helvetica", GlyphFont(doc, 0));
        Assert.Equal("Courier", GlyphFont(doc, 1));
    }

    /// <summary>
    /// One form drawn on two pages whose resources each lack a name the form uses: each page's form keeps exactly what that page gives — the
    /// first page's F1, the second page's copy F2 — and nothing of the other page, so every page reads the same as before. Before, both got
    /// both, and a page showed text with a font it does not have (as test file pr4922 with a Type 3 font; docs/FEHLERGEDAECHTNIS.md, No. 895).
    /// </summary>
    [Theory]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void FormWithoutResources_SharedByTwoPagesWithOtherResources_EachKeepsItsOwn(string version)
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R 7 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 6 0 R>>/XObject<</Fm1 5 0 R>>>>>>",
            [4] = ("", "q /Fm1 Do Q"),
            [5] = ("/Type/XObject/Subtype/Form/BBox[0 0 595 842]", "BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET BT /F2 12 Tf 72 680 Td (Erika Mustermann) Tj ET"),
            [6] = Helvetica,
            [7] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F2 8 0 R>>/XObject<</Fm1 5 0 R>>>>>>",
            [8] = "<</Type/Font/Subtype/Type1/BaseFont/Courier/Encoding/WinAnsiEncoding>>",
        });
        using var source = PdfDocument.Open(file);
        using var doc = Saved(file, version);
        static PdfDictionary Fonts(PdfDocument doc, int page) =>
            ((PdfStream)doc.Pages[page].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Fm1"))!).Dictionary
                .GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!;
        Assert.Equal("Helvetica", Fonts(doc, 0).GetDictionary(PdfName.Get("F1"))!.GetName(PdfName.Get("BaseFont"))?.Value);
        Assert.False(Fonts(doc, 0).ContainsKey(PdfName.Get("F2")));
        Assert.Equal("Courier", Fonts(doc, 1).GetDictionary(PdfName.Get("F2"))!.GetName(PdfName.Get("BaseFont"))?.Value);
        Assert.False(Fonts(doc, 1).ContainsKey(PdfName.Get("F1")));
        for (var page = 0; page < 2; page++) Assert.Equal(Text(source, page), Text(doc, page));
    }

    /// <summary>A Type 3 font whose glyphs draw with the page's resources (an image here) gets them in its dictionary, as 2.0 asks.</summary>
    [Fact]
    public void Type3GlyphsWithoutResources_FontGetsThem()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</T3 5 0 R>>/XObject<</Im1 7 0 R>>>>>>",
            [4] = ("", "BT /T3 12 Tf 72 700 Td (a) Tj ET"),
            [5] = "<</Type/Font/Subtype/Type3/FontBBox[0 0 1000 1000]/FontMatrix[0.001 0 0 0.001 0 0]/CharProcs<</a 6 0 R>>"
                  + "/Encoding<</Type/Encoding/Differences[97/a]>>/FirstChar 97/LastChar 97/Widths[1000]>>",
            [6] = ("", "1000 0 0 0 1000 1000 d1 q 1000 0 0 1000 0 0 cm /Im1 Do Q"),
            [7] = ("/Type/XObject/Subtype/Image/Width 1/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8", "\0"),
        });
        using var doc = Saved(file, "2.0");
        var font = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("T3"))!;
        Assert.NotNull(font.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("XObject"))?.Get(PdfName.Get("Im1")) as PdfStream);
        // as 1.7 as well (inheriting deprecated; No. 782)
        using var old = Saved(file, "1.7");
        Assert.NotNull(old.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("T3"))!.GetDictionary(PdfName.Resources));
    }

    /// <summary>
    /// S-A A67: a form whose /Resources names nothing (a reference into the void) draws with those of its page, as the display reads it —
    /// saved, it gets them; before, the entry counted as present and the form kept naming nothing (docs/FEHLERGEDAECHTNIS.md, No. 782).
    /// </summary>
    [Fact]
    public void FormWithResourcesIntoTheVoid_GetsThoseOfItsPage()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 6 0 R>>/XObject<</Fm1 5 0 R>>>>>>",
            [4] = ("", "q /Fm1 Do Q"),
            [5] = ("/Type/XObject/Subtype/Form/BBox[0 0 595 842]/Resources 99 0 R", "BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET"),
            [6] = Helvetica,
        });
        foreach (var version in (string[])["2.0", "1.7"])
        {
            using var doc = Saved(file, version);
            Assert.Equal("Helvetica", Form(doc).Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Font"))?.GetDictionary(PdfName.Get("F1"))?.GetName(PdfName.Get("BaseFont"))?.Value);
            Assert.Contains("Max Mustermann", Text(doc));
        }
    }

    /// <summary>
    /// A form without resources of its own on a redacted page, filled with a tiling pattern of the page: the resources it borrows are those
    /// of the page as redacted — built in memory, the pattern a stream without a number. Lent as they were, the pattern went out as a
    /// dictionary without its data and the form drew grey (found by `schwaerzpruefung erzeugt 300 --struktur`, 118 cases; in 2.0 since
    /// No. 549, in 1.x since No. 782). Mapped to the copy, the pattern is a stream of its own with its cell (docs/FEHLERGEDAECHTNIS.md, No. 787).
    /// </summary>
    [Fact]
    public void FormOnRedactedPage_BorrowedPatternKeepsItsData()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 6 0 R>>/XObject<</Fm1 5 0 R>>/Pattern<</P1 7 0 R>>>>>>",
            [4] = ("", "BT /F1 12 Tf 72 700 Td (geheim bleibt) Tj ET q 1 0 0 1 300 300 cm /Fm1 Do Q"),
            [5] = ("/Type/XObject/Subtype/Form/BBox[0 0 100 100]", "/Pattern cs /P1 scn 0 0 60 60 re f"),
            [6] = Helvetica,
            [7] = ("/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 4 4]/XStep 4/YStep 4/Resources<<>>", "0 0 1 rg 0 0 2 2 re f"),
        });
        foreach (var version in (string[])["2.0", "1.7"])
        {
            using var doc = PdfDocument.Open(file);
            var text = Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Text);
            var q = text.Quads(Assert.Single(text.Find("geheim", matchCase: true)));
            var items = PageItem.From(doc);
            items[0] = items[0].WithElements([new RedactionElement { Rect = new PdfRectangle(q.Min(x => x.X0) - 1, q.Min(x => x.Y0) - 1, q.Max(x => x.X1) + 1, q.Max(x => x.Y2) + 1) }]);
            using var output = new MemoryStream();
            PdfWriter.Write(doc, items, output, new PdfSaveOptions { Version = version, ApplyRedactions = true });
            using var saved = PdfDocument.Open(output.ToArray());
            var form = Form(saved);
            var pattern = form.Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Pattern"))?.Get(PdfName.Get("P1"));
            var stream = Assert.IsType<PdfStream>(pattern);
            Assert.Contains("re f", System.Text.Encoding.Latin1.GetString(saved.GetStreamData(stream)));
        }
    }

    /// <summary>
    /// 7.8.3, sentence 18 (S-A A90 changed to the wording of the standard, decided on 12.11.2026): a form without resources of its own
    /// inherits those of the page it is used on — also when a form with resources of its own invokes it. The page names /F1 Helvetica, the
    /// outer form /F1 Courier; the inner form, without resources, shows its text in Helvetica (before: Courier, the caller's). Saved as 2.0, the
    /// inner form gets the page's resources written in (docs/FEHLERGEDAECHTNIS.md, No. 791).
    /// </summary>
    [Fact]
    public void FormWithoutResourcesInsideAFormWithThem_InheritsFromThePage()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 6 0 R>>/XObject<</Outer 5 0 R>>>>>>",
            [4] = ("", "q /Outer Do Q"),
            [5] = ("/Type/XObject/Subtype/Form/BBox[0 0 595 842]/Resources<</Font<</F1 8 0 R>>/XObject<</Inner 7 0 R>>>>", "/Inner Do"),
            [6] = Helvetica,
            [7] = ("/Type/XObject/Subtype/Form/BBox[0 0 595 842]", "BT /F1 12 Tf 72 700 Td (Max Mustermann) Tj ET"),
            [8] = "<</Type/Font/Subtype/Type1/BaseFont/Courier/Encoding/WinAnsiEncoding>>",
        });
        // the width of the line tells the font: Courier gives every character 600/1000 — 14 × 7.2 = 100.8 pt at 12 pt; Helvetica is narrower
        static double Width(PdfDocument doc)
        {
            var text = Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Text;
            return text.Max(t => Math.Max(t.X1, t.X2)) - text.Min(t => Math.Min(t.X0, t.X3));
        }
        using (var doc = PdfDocument.Open(file)) Assert.True(Width(doc) < 95, $"Breite {Width(doc)}: nicht Helvetica");
        using var saved = Saved(file, "2.0");
        var inner = (PdfStream)((PdfStream)saved.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Outer"))!)
            .Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Inner"))!;
        Assert.Equal("Helvetica", inner.Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("Font"))?.GetDictionary(PdfName.Get("F1"))?.GetName(PdfName.Get("BaseFont"))?.Value);
        Assert.True(Width(saved) < 95);
    }

    /// <summary>
    /// A Type 3 font written directly into the page's /Font dictionary (not an object of its own) gets the resources its glyphs borrow as
    /// well — before only one of its own did (docs/FEHLERGEDAECHTNIS.md, No. 549: left out; No. 782).
    /// </summary>
    [Fact]
    public void Type3FontDirect_GetsItsResources()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</T3 <</Type/Font/Subtype/Type3/FontBBox[0 0 1000 1000]/FontMatrix[0.001 0 0 0.001 0 0]/CharProcs<</a 6 0 R>>"
                  + "/Encoding<</Type/Encoding/Differences[97/a]>>/FirstChar 97/LastChar 97/Widths[1000]>>>>/XObject<</Im1 7 0 R>>>>>>",
            [4] = ("", "BT /T3 12 Tf 72 700 Td (a) Tj ET"),
            [6] = ("", "1000 0 0 0 1000 1000 d1 q 1000 0 0 1000 0 0 cm /Im1 Do Q"),
            [7] = ("/Type/XObject/Subtype/Image/Width 1/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8", "\0"),
        });
        foreach (var version in (string[])["2.0", "1.7"])
        {
            using var doc = Saved(file, version);
            var font = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("T3"))!;
            Assert.NotNull(font.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("XObject"))?.Get(PdfName.Get("Im1")) as PdfStream);
        }
    }

    // ---- 5.3 d: the XMP follows the save in 2.0 — date of change and program as in /Info (ISO 32000-2, 14.3.4; docs/FEHLERGEDAECHTNIS.md, No. 552)

    private static readonly DateTimeOffset SavedAt = new(2026, 10, 20, 17, 5, 9, TimeSpan.FromHours(2));

    private const string Namespaces = "xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"";

    /// <summary>A one-page file with the given rdf:Description content as the document's XMP.</summary>
    private static byte[] WithXmp(string description) => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R/Metadata 5 0 R>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R>>",
        [4] = ("", "0 0 m 10 10 l S"),
        [5] = ("/Type/Metadata/Subtype/XML", "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">"
            + "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" + description + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>"),
    });

    private static (PdfDocument Doc, PdfSaveResult Result) SavedAt20(byte[] file, string version = "2.0")
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = version, ModificationTime = SavedAt });
        return (PdfDocument.Open(output.ToArray()), result);
    }

    private static System.Xml.XmlDocument XmpOf(PdfDocument doc) =>
        PdfAXmp.Read(doc.GetStreamData((PdfStream)doc.Catalog!.Get(PdfName.Metadata)))!;

    private static string[] Values(System.Xml.XmlDocument xml, string name, string ns) =>
        [.. xml.GetElementsByTagName(name, ns).OfType<System.Xml.XmlElement>().Select(e => e.InnerText)];

    private const string XmpNs = "http://ns.adobe.com/xap/1.0/", PdfNs = "http://ns.adobe.com/pdf/1.3/";

    /// <summary>
    /// Saved as 2.0, the XMP names the date of this save and the program, as /ModDate and /Producer do — before it kept the old date and
    /// producer while the deprecated /Info named the new. As 1.7 just the same since phase 6.1 (docs/FEHLERGEDAECHTNIS.md, No. 613).
    /// </summary>
    [Fact]
    public void SavedAsPdf20_XmpModifyDateFollowsTheSave()
    {
        var file = WithXmp($"<rdf:Description rdf:about=\"\" {Namespaces}><xmp:ModifyDate>2020-01-02T03:04:05+01:00</xmp:ModifyDate>"
            + "<xmp:MetadataDate>2020-01-02T03:04:05+01:00</xmp:MetadataDate><pdf:Producer>Alter Erzeuger</pdf:Producer>"
            + "<dc:creator><rdf:Seq><rdf:li>Max Mustermann</rdf:li></rdf:Seq></dc:creator></rdf:Description>");
        var (doc, result) = SavedAt20(file);
        using (doc)
        {
            Assert.False(result.XmpNotUpdated);
            var xml = XmpOf(doc);
            Assert.Equal(["2026-10-20T17:05:09+02:00"], Values(xml, "ModifyDate", XmpNs));
            Assert.Equal(["2026-10-20T17:05:09+02:00"], Values(xml, "MetadataDate", XmpNs));
            var info = doc.Trailer.GetDictionary(PdfName.Info)!;
            // the program only in the XMP: /Producer is deprecated in 2.0 and the source had none (No. 810)
            Assert.StartsWith("PDF-Knead ", Assert.Single(Values(xml, "Producer", PdfNs)));
            Assert.False(info.ContainsKey(PdfName.Get("Producer")));
            Assert.Equal(SavedAt, PdfDate.Parse(info.GetString(PdfName.Get("ModDate"))!.ToText()));
            Assert.Equal(["Max Mustermann"], Values(xml, "creator", "http://purl.org/dc/elements/1.1/"));
        }
        var (old, _) = SavedAt20(file, "1.7");
        using (old) Assert.Equal(["2026-10-20T17:05:09+02:00"], Values(XmpOf(old), "ModifyDate", XmpNs));
    }

    /// <summary>
    /// /Producer is deprecated in PDF 2.0 (ISO 32000-2, 14.3.3): a 2.0 copy writes none of its own into /Info — before, every one did
    /// (docs/FEHLERGEDAECHTNIS.md, No. 810; external checkers over the save test, 509 of 987 files). One the source has is kept current, so that
    /// /Info and XMP say the same; below 2.0 the program is named as before.
    /// </summary>
    [Theory]
    [InlineData("2.0", false, false)]
    [InlineData("2.0", true, true)]
    [InlineData("1.7", false, true)]
    public void Producer_In20OnlyWhereTheSourceHasIt(string version, bool sourceHasProducer, bool expected)
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R>>",
            [4] = sourceHasProducer ? "<</Title(Muster)/Producer(Alter Erzeuger)>>" : "<</Title(Muster)>>",
        }, "/Info 4 0 R");
        var (doc, _) = SavedAt20(file, version);
        using (doc)
        {
            var info = doc.Trailer.GetDictionary(PdfName.Info)!;
            Assert.Equal(expected, info.ContainsKey(PdfName.Get("Producer")));
            if (expected) Assert.StartsWith("PDF-Knead ", info.GetString(PdfName.Get("Producer"))!.ToText());
            Assert.Equal("Muster", info.GetString(PdfName.Get("Title"))!.ToText());
            Assert.Equal(SavedAt, PdfDate.Parse(info.GetString(PdfName.Get("ModDate"))!.ToText()));
        }
    }

    /// <summary>
    /// The padding of the packet (white space before „&lt;?xpacket end“, for changes in place) stays when the XMP is updated — reading as XML
    /// dropped it (quality round of phase 5: some 700 KB less over a public test collection saved as 2.0).
    /// </summary>
    [Fact]
    public void SavedAsPdf20_XmpPaddingKept()
    {
        var padded = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Metadata 5 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R>>",
            [4] = ("", "0 0 m 10 10 l S"),
            [5] = ("/Type/Metadata/Subtype/XML", "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">"
                + "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\"/></rdf:RDF></x:xmpmeta>"
                + new string(' ', 2000) + "\n<?xpacket end=\"w\"?>"),
        });
        var (doc, _) = SavedAt20(padded);
        using (doc)
        {
            var packet = System.Text.Encoding.UTF8.GetString(doc.GetStreamData((PdfStream)doc.Catalog!.Get(PdfName.Metadata)));
            var end = packet.LastIndexOf("<?xpacket end", StringComparison.Ordinal);
            Assert.True(end > 2000);
            Assert.True(string.IsNullOrWhiteSpace(packet[(end - 2001)..end]));
            Assert.Contains("ModifyDate", packet, StringComparison.Ordinal);
        }
    }

    /// <summary>The values in attribute form (as many programs write them): replaced, not doubled.</summary>
    [Fact]
    public void SavedAsPdf20_XmpAttributeForm_Updated()
    {
        var file = WithXmp($"<rdf:Description rdf:about=\"\" {Namespaces} xmp:ModifyDate=\"2020-01-02T03:04:05+01:00\" pdf:Producer=\"Alter Erzeuger\"/>");
        var (doc, _) = SavedAt20(file);
        using (doc)
        {
            var xml = XmpOf(doc);
            Assert.Equal(["2026-10-20T17:05:09+02:00"], Values(xml, "ModifyDate", XmpNs));
            Assert.DoesNotContain(xml.GetElementsByTagName("Description", "http://www.w3.org/1999/02/22-rdf-syntax-ns#").OfType<System.Xml.XmlElement>(),
                d => d.HasAttribute("ModifyDate", XmpNs) || d.HasAttribute("Producer", PdfNs));
            Assert.Single(Values(xml, "Producer", PdfNs));
        }
    }

    /// <summary>A schema PDF-Knead does not know stays as it was; unreadable XMP stays as it is, and the result says so.</summary>
    [Fact]
    public void SavedAsPdf20_UnknownSchemaKept_UnreadableSaid()
    {
        var file = WithXmp("<rdf:Description rdf:about=\"\" xmlns:muster=\"http://example.org/muster/\"><muster:Ablage>Akte von Erika Mustermann</muster:Ablage></rdf:Description>");
        var (doc, _) = SavedAt20(file);
        using (doc)
        {
            var xml = XmpOf(doc);
            Assert.Equal(["Akte von Erika Mustermann"], Values(xml, "Ablage", "http://example.org/muster/"));
            Assert.Equal(["2026-10-20T17:05:09+02:00"], Values(xml, "ModifyDate", XmpNs));
        }
        var broken = WithXmp("<rdf:Description rdf:about=\"\"><nicht-geschlossen></rdf:Description>");
        var (same, result) = SavedAt20(broken);
        using (same)
        {
            Assert.True(result.XmpNotUpdated);
            Assert.Contains("nicht-geschlossen", System.Text.Encoding.UTF8.GetString(same.GetStreamData((PdfStream)same.Catalog!.Get(PdfName.Metadata))));
        }
    }

    // ---- 5.3 e: the version the content needs, when 1.4 is chosen (decision F; ISO 32000-2, 7.5.2 and 7.7.2; docs/FEHLERGEDAECHTNIS.md, No. 553)

    private static (byte[] Bytes, PdfSaveResult Result) SavedAs(byte[] file, string version)
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = version });
        return (output.ToArray(), result);
    }

    private static string Header(byte[] file) => System.Text.Encoding.ASCII.GetString(file, 0, 8);

    /// <summary>
    /// A file with layers (from 1.5) saved as 1.4: the header stays 1.4, the catalogue names 1.5 (it counts where higher), and the result
    /// says why. Before, the file claimed to be 1.4.
    /// </summary>
    [Fact]
    public void SavedAs14_WithLayers_DeclaresVersion15()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/OCProperties<</OCGs[5 0 R]/D<</Order[5 0 R]>>>>>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Properties<</L1 5 0 R>>>>>>",
            [4] = ("", "/OC /L1 BDC 0 0 m 100 100 l S EMC"),
            [5] = "<</Type/OCG/Name(Ebene von Max Mustermann)>>",
        });
        var (bytes, result) = SavedAs(file, "1.4");
        Assert.Equal("%PDF-1.4", Header(bytes));
        using var doc = PdfDocument.Open(bytes);
        Assert.Equal("1.5", doc.Catalog!.GetName(PdfName.Version)?.Value);
        Assert.Equal("1.5", doc.Version);
        Assert.True(result.VersionRaised);
        Assert.Equal("1.5", result.Version);
        Assert.Equal(Core.Localization.Tr.T("Ebenen"), result.RaisedFor);
    }

    /// <summary>An image in JPEG 2000 (from 1.5) saved as 1.4: the catalogue names 1.5.</summary>
    [Fact]
    public void SavedAs14_WithJpx_DeclaresVersion15()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</XObject<</Im1 5 0 R>>>>>>",
            [4] = ("", "q 100 0 0 100 0 0 cm /Im1 Do Q"),
            [5] = ("/Type/XObject/Subtype/Image/Width 1/Height 1/Filter/JPXDecode", "kein echtes JPEG 2000"),
        });
        var (bytes, result) = SavedAs(file, "1.4");
        using var doc = PdfDocument.Open(bytes);
        Assert.Equal("1.5", doc.Version);
        Assert.Equal(Core.Localization.Tr.T("JPEG 2000"), result.RaisedFor);
    }

    /// <summary>
    /// An image of 16 bits per value (from 1.5: ISO 32000-2, 8.9.5.1, and in the predictor 7.4.4.3, Table 8) saved as 1.4: the catalogue names
    /// 1.5 (docs/FEHLERGEDAECHTNIS.md, No. 943; before the copy stayed 1.4).
    /// </summary>
    [Fact]
    public void SavedAs14_With16BitImage_DeclaresVersion15()
    {
        var file = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</XObject<</Im1 5 0 R>>>>>>",
            [4] = ("", "q 100 0 0 100 0 0 cm /Im1 Do Q"),
            [5] = ("/Type/XObject/Subtype/Image/Width 2/Height 1/ColorSpace/DeviceGray/BitsPerComponent 16", "AAAA"),
        });
        var (bytes, result) = SavedAs(file, "1.4");
        using var doc = PdfDocument.Open(bytes);
        Assert.Equal("1.5", doc.Version);
        Assert.Equal(Core.Localization.Tr.T("16-Bit-Bilder"), result.RaisedFor);
    }

    /// <summary>A file with nothing of a later version stays 1.4, without /Version; a 2.0 file saved as 1.7 stays 1.7 (never raised to 2.0).</summary>
    [Fact]
    public void SavedAs14_PlainFile_Stays14_Pdf20SavedAs17_Stays17()
    {
        var (plain, result) = SavedAs(TestPdf.Simple(1).Build(), "1.4");
        using (var doc = PdfDocument.Open(plain))
        {
            Assert.Equal("1.4", doc.Version);
            Assert.False(doc.Catalog!.ContainsKey(PdfName.Version));
            Assert.False(result.VersionRaised);
        }
        var (lower, lowered) = SavedAs(TestPdf.Simple(1).Build(version: "2.0"), "1.7");
        using var copy = PdfDocument.Open(lower);
        Assert.Equal("1.7", copy.Version);
        Assert.False(lowered.VersionRaised);
    }
}
