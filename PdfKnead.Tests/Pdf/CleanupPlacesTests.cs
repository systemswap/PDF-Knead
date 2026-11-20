using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Dokument bereinigen“ → page button: <see cref="DocumentCleanup.Places"/> says where a finding lies on the page, so that it can be framed
/// there — hidden text with what it says, a field with its stored value; what lies beside the page has no place.
/// </summary>
public sealed class CleanupPlacesTests
{
    private const string Font = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>";

    internal static PdfDocument Page(string content, string annots = "", params (int, object)[] more)
    {
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = Font,
            [4] = $"<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources<</Font<</F1 3 0 R>>>>{annots}>>",
            [7] = ("", content),
        };
        foreach (var (number, value) in more) objects[number] = value;
        return PdfDocument.Open(CraftedFileTests.Build(objects));
    }

    private static List<CleanupPlace> Places(PdfDocument doc, CleanupItems what) => DocumentCleanup.Places(PageItem.From(doc)[0], what);

    [Fact]
    public void WhiteOnWhiteText_PlaceWithItsText_OnlyForThatItem()
    {
        using var doc = Page("BT /F1 12 Tf 72 780 Td (Sichtbar) Tj ET 1 g BT /F1 12 Tf 72 600 Td (Kontonummer 12345) Tj ET");
        var place = Assert.Single(Places(doc, CleanupItems.SuspiciousText));
        Assert.Contains("Kontonummer 12345", place.Text);
        Assert.InRange(place.Bounds.Bottom, 590, 605);
        Assert.Null(place.Value);
        Assert.Empty(Places(doc, CleanupItems.InvisibleText));
        Assert.Empty(Places(doc, CleanupItems.Metadata));   // a finding of the whole document has no places
    }

    [Fact]
    public void InvisibleText_OnThePage_Placed_BesideThePage_NotPlaced()
    {
        using var doc = Page("BT /F1 12 Tf 3 Tr 72 700 Td (Erkannter Text) Tj ET BT /F1 12 Tf 3 Tr 900 700 Td (Daneben) Tj ET");
        var place = Assert.Single(Places(doc, CleanupItems.InvisibleText));
        Assert.Contains("Erkannter Text", place.Text);
        Assert.DoesNotContain("Daneben", place.Text);
    }

    [Fact]
    public void HiddenAnnotation_PlacedWithItsRectangle()
    {
        using var doc = Page("BT /F1 12 Tf 72 780 Td (Text) Tj ET", "/Annots[10 0 R]",
            (10, "<</Type/Annot/Subtype/Text/Rect[100 500 120 520]/F 2/Contents(geheime Notiz)>>"));
        var place = Assert.Single(Places(doc, CleanupItems.HiddenContent));
        Assert.Equal(new PdfRectangle(100, 500, 120, 520), place.Bounds);
    }
}

/// <summary>
/// „Dokument bereinigen“ with places kept (the „x“ on a frame, KONZEPT.md, section 5.18): the item is cleaned up everywhere except in the
/// places kept — hidden text, an unseen annotation, a mismatched field each stay there and only there.
/// </summary>
public sealed class CleanupKeepTests
{
    private static byte[] Save(PdfDocument doc, CleanupItems items, params CleanupKeep[] keep)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Cleanup = items, KeepPlaces = keep });
        return output.ToArray();
    }

    private static string Text(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.BuildText(doc, doc.Pages[0]), 0).Text;
    }

    [Fact]
    public void InvisibleText_OnePlaceKept_OnlyThatOneStays()
    {
        using var doc = CleanupPlacesTests.Page("q BT /F1 12 Tf 3 Tr 72 700 Td (Behalten) Tj ET Q q BT /F1 12 Tf 3 Tr 72 500 Td (Entfernen) Tj ET Q");
        var places = DocumentCleanup.Places(PageItem.From(doc)[0], CleanupItems.InvisibleText);
        var kept = Assert.Single(places, p => p.Text.Contains("Behalten"));
        Assert.Equal("", Text(Save(doc, CleanupItems.InvisibleText)));
        Assert.Equal("Behalten", Text(Save(doc, CleanupItems.InvisibleText, new CleanupKeep(CleanupItems.InvisibleText, 0, kept.Bounds))));
        // a place of another item, or of another page, keeps nothing of it
        Assert.Equal("", Text(Save(doc, CleanupItems.InvisibleText, new CleanupKeep(CleanupItems.SuspiciousText, 0, kept.Bounds))));
        Assert.Equal("", Text(Save(doc, CleanupItems.InvisibleText, new CleanupKeep(CleanupItems.InvisibleText, 1, kept.Bounds))));
    }

    [Fact]
    public void WhiteOnWhite_KeptPlaceStays()
    {
        using var doc = CleanupPlacesTests.Page("BT /F1 12 Tf 72 780 Td (Sichtbar) Tj ET 1 g BT /F1 12 Tf 72 600 Td (Weiss) Tj ET");
        var place = Assert.Single(DocumentCleanup.Places(PageItem.From(doc)[0], CleanupItems.SuspiciousText));
        Assert.Equal("Sichtbar", Text(Save(doc, CleanupItems.SuspiciousText)));
        Assert.Equal("Sichtbar\nWeiss", Text(Save(doc, CleanupItems.SuspiciousText, new CleanupKeep(CleanupItems.SuspiciousText, 0, place.Bounds))));
    }

    [Fact]
    public void HiddenAnnotation_KeptPlaceStays()
    {
        using var doc = CleanupPlacesTests.Page("BT /F1 12 Tf 72 780 Td (Text) Tj ET", "/Annots[10 0 R 11 0 R]",
            (10, "<</Type/Annot/Subtype/Text/Rect[100 500 120 520]/F 2/Contents(behalten)>>"),
            (11, "<</Type/Annot/Subtype/Text/Rect[300 500 320 520]/F 2/Contents(entfernen)>>"));
        var keep = new CleanupKeep(CleanupItems.HiddenContent, 0, new PdfRectangle(100, 500, 120, 520));
        using var saved = PdfDocument.Open(Save(doc, CleanupItems.HiddenContent, keep));
        var annots = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"));
        var left = Assert.Single(Enumerable.Range(0, annots?.Count ?? 0).Select(i => annots!.Get(i)).OfType<Core.Pdf.Objects.PdfDictionary>());
        Assert.Equal("behalten", left.GetString(PdfName.Get("Contents"))!.ToText());
    }
}

