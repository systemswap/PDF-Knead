using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Replies and popups of a redacted annotation on other pages (OFFENE-PUNKTE 8s; ISO 32000-2:2020, 12.5.6.2 /IRT, 12.5.6.14 /Parent): a reply
/// shows the note it answers, so it goes with it wherever it lies — also a reply to the reply; annotations of other pages that do not refer to
/// it stay.
/// </summary>
public sealed class RedactionRepliesTests
{
    private static PdfDictionary Note(TestPdf pdf, string text, double x, double y, params (string, PdfObject)[] more)
    {
        var appearance = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 20, 20))),
            "0 0.7 0 rg 0 0 20 20 re f"u8.ToArray()));
        var entries = new List<(string, PdfObject)>
        {
            ("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Text")), ("Rect", TestPdf.Box(x, y, x + 20, y + 20)),
            ("Contents", new PdfString(System.Text.Encoding.ASCII.GetBytes(text))), ("AP", TestPdf.Dict(("N", TestPdf.Ref(appearance)))),
        };
        entries.AddRange(more);
        return TestPdf.Dict([.. entries]);
    }

    [Fact]
    public void RedactedNote_RepliesAndPopupsOnOtherPagesGo_OthersStay()
    {
        var pdf = TestPdf.Simple(2);
        var pages = Enumerable.Range(1, pdf.Count).Where(n => pdf.Get(n) is PdfDictionary d && d.GetName(PdfName.Type)?.Value == "Page").ToList();
        var note = pdf.Add(Note(pdf, "Geheime Notiz", 300, 700));
        var popup = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Popup")), ("Rect", TestPdf.Box(330, 650, 480, 720)),
            ("Parent", TestPdf.Ref(note))));
        var reply = pdf.Add(Note(pdf, "Antwort auf Seite 2", 100, 100, ("IRT", TestPdf.Ref(note))));
        var second = pdf.Add(Note(pdf, "Antwort auf die Antwort", 140, 100, ("IRT", TestPdf.Ref(reply))));
        var other = pdf.Add(Note(pdf, "Andere Notiz", 200, 100));
        ((PdfDictionary)pdf.Get(pages[0])).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(note)]));
        ((PdfDictionary)pdf.Get(pages[1])).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(popup), TestPdf.Ref(reply), TestPdf.Ref(second), TestPdf.Ref(other)]));

        using var doc = PdfDocument.Open(pdf.Build());
        var items = PageItem.From(doc).ToArray();
        items[0] = items[0].WithElements([.. items[0].Elements, new RedactionElement { Rect = new PdfRectangle(290, 690, 330, 730) }]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true });
        using var saved = PdfDocument.Open(output.ToArray());
        string[] Texts(int page) => saved.Pages[page].Dictionary.GetArray(PdfName.Get("Annots")) is { } a
            ? [.. Enumerable.Range(0, a.Count).Select(i => a.Get(i)).OfType<PdfDictionary>().Select(d => d.GetString(PdfName.Contents)?.ToText() ?? d.GetName(PdfName.Subtype)!.Value)]
            : [];
        Assert.DoesNotContain("Geheime Notiz", Texts(0));
        Assert.Equal(["Andere Notiz"], Texts(1));
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(output.ToArray(), ["Antwort", "Geheime"], null, out _));
    }
}
