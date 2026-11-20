using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ISO 32000-2, 12.7.4.3, sentences 46–47: an existing appearance of a field with variable text is updated — only its <c>/Tx BMC … EMC</c>
/// section gets the new value (without one, it is appended); what it draws around the text stays (docs/FEHLERGEDAECHTNIS.md, No. 815). Found by
/// the save test on bug1883609 (PDF 2.0: entries 810 → 808): the yellow background of a choice field went when the form was settled.
/// </summary>
public sealed class FieldAppearanceUpdateTests
{
    private const string Helvetica = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>";

    /// <summary>A page with one field (object 5) whose appearance (object 6) is <paramref name="appearance"/>; the form as <paramref name="form"/>.</summary>
    private static byte[] FormFile(string widget, string appearance, string form = "") => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = $"<</Type/Catalog/Pages 2 0 R/AcroForm<</Fields[5 0 R]/DR<</Font<</Helv 7 0 R>>>>/DA(/Helv 0 Tf 0 g){form}>>>>",
        [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Annots[5 0 R]>>",
        [4] = ("", ""),
        [5] = $"<</Type/Annot/Subtype/Widget/Rect[100 700 250 714]/F 4/P 3 0 R/DA(/Helv 11 Tf 0 g)/AP<</N 6 0 R>>{widget}>>",
        [6] = ("/Type/XObject/Subtype/Form/BBox[0 0 150 14]/Resources<</Font<</Helv 7 0 R>>>>", appearance),
        [7] = Helvetica,
    });

    private static byte[] Saved(byte[] file, string version, Func<PageElement, PageElement>? edit = null)
    {
        using var doc = PdfDocument.Open(file);
        var items = PageItem.From(doc).ToArray();
        if (edit is not null) items[0] = items[0].WithElements([.. items[0].Elements.Select(edit)]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { Version = version });
        return output.ToArray();
    }

    private static PdfStream Normal(PdfDocument doc) =>
        (PdfStream)((PdfDictionary)doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0)!).GetDictionary(PdfName.Get("AP"))!.Get(PdfName.N)!;

    /// <summary>What stays of the old appearance, as the new one draws it (<c>ElementAppearance.FrameName</c>, spelled out so that the test runs
    /// on the comparison build too).</summary>
    private static PdfStream? Frame(PdfDocument doc) =>
        Normal(doc).Dictionary.GetDictionary(PdfName.Resources)?.GetDictionary(PdfName.Get("XObject"))?.Get(PdfName.Get("Frame")) as PdfStream;

    private static string Content(PdfDocument doc, PdfStream stream) => Encoding.Latin1.GetString(doc.GetStreamData(stream));

    private static string Text(PdfDocument doc) => string.Concat(PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text));

    private static int Entries(PdfDocument doc) => PageInterpreter.Build(doc, doc.Pages[0]).Items.Length;

    /// <summary>
    /// A form whose readers draw the fields anew (/NeedAppearances), saved as 2.0, where the flag is deprecated and the writer settles the
    /// appearances itself: the choice field keeps the yellow background its appearance draws outside the <c>/Tx</c> section, and the copy
    /// shows the page as the original was shown — the same entries. Before, the new appearance was the text alone.
    /// </summary>
    [Fact]
    public void Settled_ChoiceFieldKeepsTheBackgroundOfItsAppearance()
    {
        var file = FormFile("/FT/Ch/Ff 131072/T(Farbe)/Opt[(Rot)(Gelb)]/V(Gelb)", "1 1 0.6 rg 0 0 150 14 re f\n/Tx BMC\nEMC\n", "/NeedAppearances true");
        using var original = PdfDocument.Open(file);
        using var copy = PdfDocument.Open(Saved(file, "2.0"));
        Assert.NotEqual(true, copy.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetBoolean(PdfName.Get("NeedAppearances")));
        var normal = Normal(copy);
        var frame = Frame(copy);
        Assert.NotNull(frame);
        Assert.Contains("1 1 0.6 rg", Content(copy, frame!));
        Assert.DoesNotContain("/Tx", Content(copy, frame));
        Assert.Contains("/Tx BMC", Content(copy, normal));
        Assert.Contains("Gelb", Text(copy));
        Assert.Equal(Entries(original), Entries(copy));
    }

    /// <summary>
    /// A field of another program filled in: its appearance draws a blue border outside the <c>/Tx</c> section and the old value inside it —
    /// the copy keeps the border and shows only the new value. Before, the appearance was built anew from /MK, which this field does not have:
    /// the border went.
    /// </summary>
    [Fact]
    public void FilledIn_BorderStays_OldValueGoes()
    {
        var file = FormFile("/FT/Tx/T(Name)/V(Alt)", "0 0 1 RG 1 w 0.5 0.5 149 13 re S\n/Tx BMC q BT /Helv 11 Tf 0 g 2 3 Td (Alt) Tj ET Q EMC\n");
        using var copy = PdfDocument.Open(Saved(file, "1.7", e => e is ForeignAnnotation f && FormField.Of(f)?.Name == "Name" ? f with { Value = "Neu" } : e));
        var frame = Frame(copy);
        Assert.NotNull(frame);
        Assert.Contains("re S", Content(copy, frame!));
        Assert.DoesNotContain("(Alt)", Content(copy, frame));
        var text = Text(copy);
        Assert.Contains("Neu", text);
        Assert.DoesNotContain("Alt", text);
    }

    /// <summary>
    /// Without a <c>/Tx</c> section the new text is appended to the appearance (sentence 47): a frame of its own without text stays whole.
    /// </summary>
    [Fact]
    public void FilledIn_NoSection_FrameStaysWhole()
    {
        var file = FormFile("/FT/Tx/T(Name)", "0.9 g 0 0 150 14 re f\n");
        using var copy = PdfDocument.Open(Saved(file, "1.7", e => e is ForeignAnnotation f && FormField.Of(f)?.Name == "Name" ? f with { Value = "Neu" } : e));
        var frame = Frame(copy);
        Assert.NotNull(frame);
        Assert.Contains("0.9 g", Content(copy, frame!));
        Assert.Contains("Neu", Text(copy));
    }

    /// <summary>
    /// An appearance that shows its old value without a <c>/Tx</c> section is built anew (appended, the new value would stand over the old
    /// one — decided on 13.11.2026, docs/ENTSCHEIDUNGEN.md): no frame, only the new value.
    /// </summary>
    [Fact]
    public void FilledIn_OldTextWithoutSection_BuiltAnew()
    {
        var file = FormFile("/FT/Tx/T(Name)/V(Alt)", "BT /Helv 11 Tf 0 g 2 3 Td (Alt) Tj ET\n");
        using var copy = PdfDocument.Open(Saved(file, "1.7", e => e is ForeignAnnotation f && FormField.Of(f)?.Name == "Name" ? f with { Value = "Neu" } : e));
        Assert.Null(Frame(copy));
        var text = Text(copy);
        Assert.Contains("Neu", text);
        Assert.DoesNotContain("Alt", text);
    }
}
