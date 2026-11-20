using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Redactions that are none (KONZEPT.md, section 5.6, „Vorgetäuschte Schwärzung“): text under a dark bar, black on black, under a dark
/// annotation, and unapplied redaction marks of other programs are found with the text that can still be read out; visible text, white on
/// white, OCR text and light covers are not reported, nor PDF-Knead's own marks (they are applied when saving).
/// </summary>
public sealed class FakeRedactionTests
{
    private static List<FakeRedactionSpot> Find(byte[] data)
    {
        using var doc = PdfDocument.Open(data);
        return FakeRedactions.Find(doc, doc.Pages[0], new ImageCache());
    }

    internal static byte[] Page(string content, params PdfDictionary[] annotations)
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content)));
        if (annotations.Length > 0)
        {
            var page = Enumerable.Range(1, pdf.Count).Select(pdf.Get).OfType<PdfDictionary>().First(d => d.GetName(PdfName.Type)?.Value == "Page");
            var list = new PdfArray();
            foreach (var annotation in annotations)
            {
                // a stream must be an object of its own: the appearance goes in by reference
                if (annotation.GetDictionary(PdfName.Get("AP")) is { } ap && ap.Get(PdfName.N) is PdfStream appearance) ap.Set(PdfName.N, TestPdf.Ref(pdf.Add(appearance)));
                list.Add(TestPdf.Ref(pdf.Add(annotation)));
            }
            page.Set(PdfName.Get("Annots"), list);
        }
        return pdf.Build();
    }

    [Fact]
    public void DarkCovers_Found_WithTheText()
    {
        var spots = Find(Page(
            "BT /F1 12 Tf 72 700 Td (Max Mustermann 4711) Tj ET 0 g 66 694 160 22 re f " +           // black bar over text
            "BT /F1 12 Tf 72 600 Td (Kontonummer 1234) Tj ET 0.2 g 66 594 140 22 re f 0 g " +           // dark grey bar
            "0 g 66 494 160 22 re f BT /F1 12 Tf 72 500 Td (Schwarz auf schwarz) Tj ET"));              // black text on a black ground
        Assert.Equal(3, spots.Count);
        Assert.All(spots, s => Assert.Equal(FakeRedactionKind.Covered, s.Kind));
        Assert.Contains(spots, s => s.Text.Contains("Mustermann"));
        Assert.Contains(spots, s => s.Text.Contains("Kontonummer"));
        Assert.Contains(spots, s => s.Text.Contains("Schwarz"));
    }

    [Fact]
    public void VisibleWhiteHiddenOrLightlyCovered_NotReported()
    {
        Assert.Empty(Find(Page(
            "BT /F1 12 Tf 72 760 Td (Sichtbarer Text) Tj ET " +
            "0 g 66 694 160 22 re f 1 g BT /F1 12 Tf 72 700 Td (Weiss auf Schwarz) Tj ET 0 g " +       // white on a black bar: visible
            "1 g BT /F1 12 Tf 72 600 Td (Weiss auf Weiss) Tj ET 0 g " +                                 // hidden, but not dark: „Verborgenes schwärzen“
            "BT 3 Tr /F1 12 Tf 72 500 Td (OCR Text) Tj ET " +                                           // invisible text (OCR)
            "BT /F1 12 Tf 72 400 Td (Unter Hellgrau) Tj ET 0.85 g 66 394 160 22 re f 0 g")));          // a light cover does not look redacted
    }

    private static PdfDictionary Annotation(string subtype, double x0, double y0, double x1, double y1, bool own = false)
    {
        var appearance = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, x1 - x0, y1 - y0))),
            Encoding.ASCII.GetBytes($"0 g 0 0 {x1 - x0} {y1 - y0} re f"));
        var annotation = TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N(subtype)), ("Rect", TestPdf.Box(x0, y0, x1, y1)),
            ("AP", TestPdf.Dict(("N", appearance))));
        if (own) annotation.Set(ElementStore.Key, new PdfDictionary());
        return annotation;
    }

    [Fact]
    public void DarkAnnotation_And_UnappliedForeignMark_Found_OwnMarkNot()
    {
        var spots = Find(Page(
            "BT /F1 12 Tf 72 700 Td (Unter Kaestchen) Tj ET BT /F1 12 Tf 72 600 Td (Unter Marke) Tj ET BT /F1 12 Tf 72 500 Td (Unter eigener Marke) Tj ET",
            Annotation("Square", 66, 694, 200, 716),
            Annotation("Redact", 66, 594, 180, 616) is var redact && redact.Remove(PdfName.Get("AP")) ? redact : redact,
            Annotation("Redact", 66, 494, 220, 516, own: true)));
        Assert.Contains(spots, s => s.Kind == FakeRedactionKind.Covered && s.Text.Contains("Kaestchen"));
        var mark = Assert.Single(spots, s => s.Kind == FakeRedactionKind.UnappliedMark);
        Assert.Contains("Marke", mark.Text);
        Assert.DoesNotContain(spots, s => s.Text.Contains("eigener"));
    }
}
