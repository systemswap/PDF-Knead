using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Title, language and „show the title“ when saving, and the title kept when cleaning up (docs/ABLAUFPLAN.md, 3.3 a; ISO 14289-1, 7.1 and
/// 7.2; docs/FEHLERGEDAECHTNIS.md, No. 492).
/// </summary>
public sealed class SaveOptionsTests
{
    private static PdfDocument Saved(byte[] file, PdfSaveOptions options)
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, options);
        return PdfDocument.Open(output.ToArray());
    }

    private static string? Xmp(PdfDocument doc) =>
        doc.Catalog!.Get(PdfName.Get("Metadata")) is PdfStream xmp ? Encoding.UTF8.GetString(doc.GetStreamData(xmp)) : null;

    private static string? InfoTitle(PdfDocument doc) => doc.Trailer.GetDictionary(PdfName.Info)?.GetString(PdfName.Get("Title"))?.ToText();

    /// <summary>A file with author and title and, if given, an XMP packet.</summary>
    private static byte[] File(string? xmp = null)
    {
        var pdf = Simple(1, title: null);
        pdf.InfoNumber = pdf.Add(Dict(("Title", new PdfString(PdfTextEncoding.Encode("Bescheid"))), ("Author", new PdfString(PdfTextEncoding.Encode("Max Mustermann")))));
        if (xmp is not null)
            ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("Metadata"),
                Ref(pdf.Add(new PdfStream(Dict(("Type", N("Metadata")), ("Subtype", N("XML"))), Encoding.UTF8.GetBytes(xmp)))));
        return pdf.Build();
    }

    [Fact]
    public void TitleLanguageDisplayDocTitle_Written()
    {
        using var doc = Saved(File(), new PdfSaveOptions { DocumentTitle = "Antrag auf Erstattung", DocumentLanguage = "de_DE", DisplayDocTitle = true });
        Assert.Equal("Antrag auf Erstattung", InfoTitle(doc));
        Assert.Contains("<rdf:li xml:lang=\"x-default\">Antrag auf Erstattung</rdf:li>", Xmp(doc));
        Assert.Equal("de-DE", doc.Catalog!.GetString(PdfName.Get("Lang"))!.ToText());
        Assert.True(doc.Catalog.GetDictionary(PdfName.Get("ViewerPreferences"))!.GetBoolean(PdfName.Get("DisplayDocTitle")));
    }

    /// <summary>In PDF 2.0 the title goes into the XMP only (/Info is deprecated there but for the dates, ISO 32000-2, 14.3.3).</summary>
    [Fact]
    public void Pdf20_TitleOnlyInXmp()
    {
        using var doc = Saved(File(), new PdfSaveOptions { Version = "2.0", DocumentTitle = "Antrag auf Erstattung" });
        Assert.Null(InfoTitle(doc));
        Assert.Contains("Antrag auf Erstattung", Xmp(doc));
    }

    /// <summary>An existing packet keeps its other properties; the title in it is replaced (one x-default entry).</summary>
    [Fact]
    public void ExistingXmp_TitleReplaced_RestKept()
    {
        const string packet = "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Alt</rdf:li>" +
            "<rdf:li xml:lang=\"en\">Old</rdf:li></rdf:Alt></dc:title><xmp:CreatorTool>Textprogramm</xmp:CreatorTool></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        using var doc = Saved(File(packet), new PdfSaveOptions { DocumentTitle = "Neu" });
        var xmp = Xmp(doc)!;
        Assert.Contains("x-default\">Neu</rdf:li>", xmp);
        Assert.DoesNotContain("Alt<", xmp);
        Assert.DoesNotContain("Old", xmp);
        Assert.Contains("Textprogramm", xmp);
    }

    /// <summary>
    /// Cleaning up the metadata keeps the title (design decision, box „Titel behalten“, default on): in /Info and as dc:title of a new
    /// slim packet; the author goes. Without the box the title goes as before.
    /// </summary>
    [Fact]
    public void Cleanup_Metadata_KeepTitle_OnlyTitleRemains()
    {
        using (var kept = Saved(File(), new PdfSaveOptions { Cleanup = CleanupItems.Metadata }))
        {
            Assert.Equal("Bescheid", InfoTitle(kept));
            Assert.Null(kept.Trailer.GetDictionary(PdfName.Info)!.GetString(PdfName.Get("Author")));
            Assert.Contains("Bescheid", Xmp(kept));
            Assert.DoesNotContain("Mustermann", Xmp(kept));
        }
        using var gone = Saved(File(), new PdfSaveOptions { Cleanup = CleanupItems.Metadata, KeepTitle = false });
        Assert.Null(InfoTitle(gone));
        Assert.Null(Xmp(gone));
    }

    /// <summary>A new document with a language and a title.</summary>
    [Fact]
    public void BlankPage_WithLanguageAndTitle()
    {
        using var doc = BlankPage.CreateDocument(595.28, 841.89, 1, "1.7", "Notizen von Erika Mustermann", "de-AT");
        Assert.Equal("de-AT", doc.Catalog!.GetString(PdfName.Get("Lang"))!.ToText());
        Assert.Equal("Notizen von Erika Mustermann", InfoTitle(doc));
    }
}
