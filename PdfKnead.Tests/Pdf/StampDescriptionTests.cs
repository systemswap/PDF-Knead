using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// What a new annotation says to screen readers (/Contents; docs/ABLAUFPLAN.md, 6.4 step 3, J3; ISO 14289-1, 7.18.1; ISO 32000-2, 12.5.6.12):
/// every kind says what it is, in the document's language, without names of persons; an image stamp says so from 2.0 on (/IT /StampImage).
/// Before (docs/FEHLERGEDAECHTNIS.md, No. 631): image, shape and redaction mark without a reason had no /Contents — a stamp is then announced
/// by its default name „Draft“.
/// </summary>
public sealed class StampDescriptionTests
{
    private static Core.Imaging.RgbaImage Pixels()
    {
        var pixels = Core.Imaging.RgbaImage.Blank(8, 8);
        for (var i = 0; i < 64; i++) ((byte[])[(byte)(i * 4), 80, 160, 255]).CopyTo(pixels.Pixels, i * 4);
        return pixels;
    }

    /// <summary>Saves the elements on the first page of a one-page file and returns the annotations of the result.</summary>
    private static List<PdfDictionary> Annotations(string language, string version, params PageElement[] elements)
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        using var output = new MemoryStream();
        PdfWriter.Write(original, [PageItem.From(original)[0].WithElements(elements)], output,
            new PdfSaveOptions { DocumentLanguage = language, Version = version });
        using var saved = PdfDocument.Open(output.ToArray());
        var annots = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        return [.. Enumerable.Range(0, annots.Count).Select(i => (PdfDictionary)annots.Get(i))];
    }

    private static string? Contents(PdfDictionary annot) => annot.GetString(PdfName.Contents)?.ToText();

    /// <summary>An image stamp: its alternative text, else „Bild“ in a German document and „Image“ in any other; /IT /StampImage only in 2.0.</summary>
    [Fact]
    public void ImageStamp_SaysWhatItShows()
    {
        var image = new ImageElement { Rect = new PdfRectangle(100, 600, 200, 700), Image = Pixels() };
        var described = image with { Rect = new PdfRectangle(300, 600, 400, 700), AltText = "Firmenlogo der Mustermann GmbH" };
        var german = Annotations("de-DE", "1.7", image, described);
        Assert.Equal(["Bild", "Firmenlogo der Mustermann GmbH"], german.Select(Contents));
        Assert.All(german, a => Assert.False(a.ContainsKey(PdfName.Get("IT"))));
        var english = Annotations("en", "2.0", image);
        Assert.Equal("Image", Contents(english[0]));
        Assert.Equal("StampImage", english[0].GetName(PdfName.Get("IT"))!.Value);
    }

    /// <summary>A shape says its kind.</summary>
    [Fact]
    public void Shape_SaysItsKind()
    {
        var arrow = new ShapeElement { Rect = new PdfRectangle(100, 600, 200, 700), Kind = ShapeKind.Arrow };
        var bubble = new ShapeElement { Rect = new PdfRectangle(300, 600, 400, 700), Kind = ShapeKind.Callout };
        Assert.Equal(["Pfeil", "Sprechblase"], Annotations("de", "1.7", arrow, bubble).Select(Contents));
        Assert.Equal(["Arrow", "Speech bubble"], Annotations("fr", "1.7", arrow, bubble).Select(Contents));
    }

    /// <summary>A redaction mark says so, with its reason after a dash — also without a reason.</summary>
    [Fact]
    public void RedactionMark_SaysMarkedAndWhy()
    {
        var bare = new RedactionElement { Rect = new PdfRectangle(100, 600, 200, 620) };
        var reasoned = new RedactionElement { Rect = new PdfRectangle(100, 500, 200, 520), Reason = "Schutz personenbezogener Daten", ReasonCode = "Art. 6" };
        Assert.Equal(["Zum Schwärzen markiert", "Zum Schwärzen markiert — Art. 6 — Schutz personenbezogener Daten"],
            Annotations("de-DE", "1.7", bare, reasoned).Select(Contents));
    }

    /// <summary>The alternative text of an image is kept with the element and comes back on opening (it is an element again).</summary>
    [Fact]
    public void ImageAltText_StaysWithTheElement()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var image = new ImageElement { Rect = new PdfRectangle(100, 600, 200, 700), Image = Pixels(), AltText = "Unterschrift von Erika Mustermann" };
        using var output = new MemoryStream();
        PdfWriter.Write(original, [PageItem.From(original)[0].WithElements([image])], output);
        using var saved = PdfDocument.Open(output.ToArray());
        var back = Assert.IsType<ImageElement>(Assert.Single(PageItem.From(saved)[0].Elements));
        Assert.Equal("Unterschrift von Erika Mustermann", back.AltText);
    }
}
