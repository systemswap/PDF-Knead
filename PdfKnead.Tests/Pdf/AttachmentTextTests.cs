using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;

namespace PdfKnead.Tests.Pdf;

/// <summary>Searching attachments and XMP for redacted text as what they are (docs/FEHLERGEDAECHTNIS.md, No. 443).</summary>
public sealed class AttachmentTextTests
{
    private static bool Has(string? text) => text is not null && text.Contains("Geheim 4711", StringComparison.OrdinalIgnoreCase);

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
                using (var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open())
                    stream.Write(data);
        return output.ToArray();
    }

    /// <summary>A word-processor file as its source: a ZIP with XML inside, the sentence split into two runs.</summary>
    internal static byte[] Docx() => Zip(
        ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"u8.ToArray()),
        ("word/document.xml", Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:body><w:p><w:r><w:t>Konto: Geheim</w:t></w:r><w:r><w:t xml:space=\"preserve\"> 4711</w:t></w:r></w:p></w:body></w:document>")));

    /// <summary>A PDF with the text in packed (Flate) page content.</summary>
    internal static byte[] InnerPdf()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 12 Tf 72 700 Td (Geheim 4711) Tj ET"u8.ToArray()));
        return pdf.Build();
    }

    /// <summary>
    /// The leak search of the redaction checks (`lecksuche`, `schwaerzpruefung`) finds a word in the content of attachments as the question
    /// before redacting does (docs/FEHLERGEDAECHTNIS.md, No. 699; open point of 6.9): an office file whose sentence is split into runs and
    /// a PDF with packed content, each embedded without /Type /EmbeddedFile (it is optional) — before, only the raw bytes were searched and
    /// both stayed unfound; an attachment of unknown binary data is named as not searchable.
    /// </summary>
    [Fact]
    public void LeakSearch_FindsTheWordInAttachments()
    {
        var bytes = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Names<</EmbeddedFiles<</Names[(a.docx) 5 0 R (b.pdf) 7 0 R (c.bin) 9 0 R]>>>>>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>>",
            [4] = ("", "0 0 1 rg 10 10 20 20 re f"),
            [5] = "<</Type/Filespec/F(a.docx)/EF<</F 6 0 R>>>>",
            [6] = ("", Encoding.Latin1.GetString(Docx())),
            [7] = "<</Type/Filespec/F(b.pdf)/EF<</F 8 0 R>>>>",
            [8] = ("", Encoding.Latin1.GetString(InnerPdf())),
            [9] = "<</Type/Filespec/F(c.bin)/EF<</F 10 0 R>>>>",
            [10] = ("", "\u0001\u0002\u00ff\u00fe\u0000\u0007\u0003\u0004\u0005\u0006\u0008\u000e\u000f\u0010\u0011"),
        });
        var hits = PdfKnead.Cli.LeakSearch.Find(bytes, ["Geheim 4711"], null, out var report);
        Assert.Contains(hits, h => h.Contains("Anhangs (Objekt 6)", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Contains("Anhangs (Objekt 8)", StringComparison.Ordinal));
        Assert.Contains("Anhänge nicht durchsuchbar: 10", report);
    }

    [Fact]
    public void PackedOfficeFile_RunsJoined_Found()
    {
        Assert.False(Has(Encoding.UTF8.GetString(Docx())));   // as raw bytes: never
        Assert.Equal(AttachmentSearch.Hit, AttachmentText.Search(Docx(), Has));
    }

    [Fact]
    public void EmbeddedPdf_PackedContent_Found()
    {
        Assert.Equal(AttachmentSearch.Hit, AttachmentText.Search(InnerPdf(), Has));
        var other = TestPdf.Simple(1).Build();
        Assert.Equal(AttachmentSearch.NoHit, AttachmentText.Search(other, Has));
    }

    [Fact]
    public void EncryptedPdf_NotSearchable()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 12 Tf 72 700 Td (Geheim 4711) Tj ET"u8.ToArray()));
        Assert.Equal(AttachmentSearch.NotSearchable, AttachmentText.Search(pdf.Build(cipher: PdfCipher.Aes256, user: "geheim"), Has));
    }

    [Fact]
    public void Utf16Table_WithAndWithoutByteOrderMark_Found()
    {
        var text = Encoding.Unicode.GetBytes("Name\tKonto\r\nMax Mustermann\tGeheim 4711\r\n");
        Assert.Equal(AttachmentSearch.Hit, AttachmentText.Search([0xFF, 0xFE, .. text], Has));
        Assert.Equal(AttachmentSearch.Hit, AttachmentText.Search(text, Has));
        Assert.Equal(AttachmentSearch.Hit, AttachmentText.Search([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("Geheim 4711")], Has));
    }

    [Fact]
    public void PlainText_NoHit_IsSearched()
    {
        Assert.Equal(AttachmentSearch.NoHit, AttachmentText.Search("Max Mustermann, Musterstraße 1"u8.ToArray(), Has));
        Assert.Equal(AttachmentSearch.Hit, AttachmentText.Search("Konto Geheim 4711"u8.ToArray(), Has));
    }

    [Fact]
    public void UnknownBinary_NotSearchable()
    {
        var data = new byte[1024];
        new Random(4711).NextBytes(data);
        Assert.Equal(AttachmentSearch.NotSearchable, AttachmentText.Search(data, Has));
    }

    /// <summary>An archive entry that unpacks to far more than it is packed (a bomb) is not unpacked; the archive counts as not searchable.</summary>
    [Fact]
    public void ArchiveBomb_NotSearchable()
    {
        var bomb = Zip(("word/document.xml", new byte[8 << 20]));
        Assert.Equal(AttachmentSearch.NotSearchable, AttachmentText.Search(bomb, Has));
    }

    /// <summary>An archive inside an archive (an embedded table in a document) is searched one level deeper.</summary>
    [Fact]
    public void NestedArchive_Found()
    {
        var outer = Zip(("word/document.xml", "<w:document xmlns:w=\"urn:w\"/>"u8.ToArray()), ("word/embeddings/tabelle.xlsx", Docx()));
        Assert.Equal(AttachmentSearch.Hit, AttachmentText.Search(outer, Has));
    }

    private const string Packet =
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
        "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"" +
        " xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" pdf:Keywords=\"Akte Mustermann\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Mustermann &amp; S&#xF6;hne</rdf:li>" +
        "</rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    /// <summary>XMP is XML: entities resolved, attribute values searched, tag names not (docs/FEHLERGEDAECHTNIS.md, No. 443).</summary>
    [Fact]
    public void Xmp_EntitiesAndAttributes_TagNamesNot()
    {
        var data = Encoding.UTF8.GetBytes(Packet);
        Assert.True(AttachmentText.XmpHas(data, t => t?.Contains("Mustermann & Söhne") == true));
        Assert.True(AttachmentText.XmpHas(data, t => t?.Contains("Akte Mustermann") == true));
        Assert.False(AttachmentText.XmpHas(data, t => t?.Contains("title", StringComparison.OrdinalIgnoreCase) == true));
        // UTF-16 with a byte order mark
        Assert.True(AttachmentText.XmpHas([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Packet)], t => t?.Contains("Mustermann & Söhne") == true));
        // not well-formed: the raw text as before
        Assert.True(AttachmentText.XmpHas("<x:xmpmeta>Mustermann Söhne"u8.ToArray(), t => t?.Contains("Mustermann Söhne") == true));
    }
}
