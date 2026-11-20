using System.Diagnostics;
using System.Text;
using PdfKnead.EInvoice;
using PdfKnead.Tests;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The hardened reader of invoice XML (KONZEPT.md, section 5.24, „Bauvorgaben Sicherheit“; research on security 13.11.2026): every known
/// attack on XML readers is refused with a reason and never followed (external entities, entity expansion, DTD fetching, XInclude,
/// schemaLocation, stylesheets), every count and length stays within its limit, deep nesting never overflows the stack, many pieces of text
/// stay linear, attachments are decoded in blocks, and only UTF-8 is read whatever the declaration says. Only invented content.
/// </summary>
public sealed class InvoiceXmlTests
{
    private static InvoiceXmlDocument Read(string xml, InvoiceXmlLimits? limits = null) => InvoiceXml.Read(Encoding.UTF8.GetBytes(xml), limits);

    private static InvoiceXmlRejection Refused(string xml, InvoiceXmlLimits? limits = null) => Refused(Encoding.UTF8.GetBytes(xml), limits);

    private static InvoiceXmlRejection Refused(byte[] data, InvoiceXmlLimits? limits = null) =>
        Assert.Throws<InvoiceXmlException>(() => InvoiceXml.Read(data, limits)).Reason;

    // ------------------------------------------------------------ attacks over the DTD

    [Fact]
    public void ExternalEntity_Refused_FileNeverRead()
    {
        // a file with a marker: if the reader ever resolved the entity, the marker would end up in the text
        var secret = TestFiles.Write("GEHEIMER-INHALT-4711"u8.ToArray(), "geheim.txt");
        var uri = new Uri(secret).AbsoluteUri;
        var xml = $"<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"{uri}\">]><r>&x;</r>";
        var e = Assert.Throws<InvoiceXmlException>(() => InvoiceXml.Read(Encoding.UTF8.GetBytes(xml)));
        Assert.Equal(InvoiceXmlRejection.Doctype, e.Reason);
        Assert.DoesNotContain("GEHEIM", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void EntityExpansion_BillionLaughs_RefusedAtOnce()
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\">");
        for (var i = 1; i <= 9; i++)
            sb.Append($"<!ENTITY lol{i} \"").Append(string.Concat(Enumerable.Repeat($"&lol{(i == 1 ? "" : (i - 1).ToString())};", 10))).Append("\">");
        sb.Append("]><lolz>&lol9;</lolz>");
        var watch = Stopwatch.StartNew();
        Assert.Equal(InvoiceXmlRejection.Doctype, Refused(sb.ToString()));
        Assert.True(watch.ElapsedMilliseconds < 5000 * TestKind.Factor);
    }

    [Theory]
    [InlineData("<!DOCTYPE r SYSTEM \"http://127.0.0.1:1/r.dtd\"><r/>")]                                  // external DTD: never fetched
    [InlineData("<!DOCTYPE r PUBLIC \"-//Beispiel//DTD Rechnung//DE\" \"rechnung.dtd\"><r/>")]
    [InlineData("<!DOCTYPE r [<!ENTITY % p SYSTEM \"http://127.0.0.1:1/p.dtd\"> %p;]><r/>")]               // parameter entity
    [InlineData("<!DOCTYPE r [<!ELEMENT r ANY>]><r/>")]                                                       // even a harmless DTD
    [InlineData("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE r><r/>")]
    [InlineData("<?xml version=\"1.0\"?><!-- Kommentar --><?pi x?>\n<!DOCTYPE r><r/>")]                     // after a comment and a PI
    public void Doctype_AnyForm_Refused(string xml) => Assert.Equal(InvoiceXmlRejection.Doctype, Refused(xml));

    [Fact]
    public void UndeclaredEntity_Refused() => Assert.Equal(InvoiceXmlRejection.Malformed, Refused("<r>&extern;</r>"));

    [Fact]
    public void PredefinedAndCharacterReferences_Read()
    {
        // the limit on characters from entities must not refuse the five predefined entities and character references, however many
        var xml = Read("<r>&amp;&lt;&gt;&quot;&apos;&#228;&#xF6;&#x1F600; Mustermann &amp; Söhne &amp; Töchter &#38; Co.</r>");
        Assert.Equal("&<>\"'äö\U0001F600 Mustermann & Söhne & Töchter & Co.", xml.Root.Text);
    }

    // ------------------------------------------------------------ things that would fetch or run, only noted

    [Fact]
    public void XInclude_Noted_NothingFetched()
    {
        var secret = TestFiles.Write("GEHEIMER-INHALT-4711"u8.ToArray(), "geheim.txt");
        var xml = Read($"<r xmlns:xi=\"http://www.w3.org/2001/XInclude\"><xi:include href=\"{new Uri(secret).AbsoluteUri}\" parse=\"text\"/></r>");
        Assert.True(xml.Notes.XInclude);
        var include = Assert.Single(xml.Root.Children);
        Assert.Null(include.Text);
        Assert.Empty(include.Children);
    }

    [Fact]
    public void SchemaLocation_Stylesheet_XmlBase_Noted_NothingLoaded()
    {
        var xml = Read("<?xml version=\"1.0\"?><?xml-stylesheet type=\"text/xsl\" href=\"http://127.0.0.1:1/r.xsl\"?>"
            + "<r xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:schemaLocation=\"urn:beispiel http://127.0.0.1:1/r.xsd\" "
            + "xml:base=\"http://127.0.0.1:1/\"><!-- Anmerkung --><a xsi:noNamespaceSchemaLocation=\"r.xsd\">1</a></r>");
        Assert.True(xml.Notes.SchemaLocation);
        Assert.True(xml.Notes.ProcessingInstructions);
        Assert.True(xml.Notes.XmlBase);
        Assert.True(xml.Notes.Comments);
        Assert.Equal("1", xml.Root.Children[0].Text);
    }

    // ------------------------------------------------------------ limits

    [Fact]
    public void Nesting_AMillionDeep_RefusedWithoutStackOverflow()
    {
        var xml = string.Concat(Enumerable.Repeat("<a>", 1_000_000));
        Assert.Equal(InvoiceXmlRejection.TooDeep, Refused(xml));
    }

    [Fact]
    public void Nesting_AtTheLimit_Read_OneMore_Refused()
    {
        static string Deep(int n) => string.Concat(Enumerable.Repeat("<a>", n)) + string.Concat(Enumerable.Repeat("</a>", n));
        Assert.Equal(64, Read(Deep(64)).MaxDepthSeen);
        Assert.Equal(InvoiceXmlRejection.TooDeep, Refused(Deep(65)));
    }

    [Fact]
    public void Elements_OverTheLimit_Refused()
    {
        var limits = new InvoiceXmlLimits { MaxElements = 10 };
        Assert.Equal(10, Read("<r>" + string.Concat(Enumerable.Repeat("<a/>", 9)) + "</r>", limits).Elements);
        Assert.Equal(InvoiceXmlRejection.TooManyElements, Refused("<r>" + string.Concat(Enumerable.Repeat("<a/>", 10)) + "</r>", limits));
    }

    [Fact]
    public void Attributes_AtTheLimit_Read_OneMore_Refused()
    {
        static string Element(int n) => "<r " + string.Join(" ", Enumerable.Range(0, n).Select(i => $"a{i}=\"1\"")) + "/>";
        Assert.Equal(32, Read(Element(32)).Root.Attributes.Count);
        Assert.Equal(InvoiceXmlRejection.TooManyAttributes, Refused(Element(33)));
        Assert.Equal(InvoiceXmlRejection.TooManyAttributes, Refused(Element(200_000)));
    }

    [Fact]
    public void NamespaceDeclarations_NotCountedAsAttributes_ButBounded()
    {
        // an invoice declares its namespaces on the root: they are not attributes of the invoice
        var some = "<r " + string.Join(" ", Enumerable.Range(0, 10).Select(i => $"xmlns:n{i}=\"urn:beispiel:{i}\"")) + " a=\"1\"/>";
        Assert.Single(Read(some).Root.Attributes);
        var flood = "<r " + string.Join(" ", Enumerable.Range(0, 100_000).Select(i => $"xmlns:n{i}=\"urn:beispiel:{i}\"")) + "/>";
        Assert.Equal(InvoiceXmlRejection.TooManyAttributes, Refused(flood));
    }

    [Fact]
    public void AttributeValue_OverTheLimit_Refused()
    {
        Assert.Equal(4096, Read($"<r a=\"{new string('x', 4096)}\"/>").Root.Attribute("a")!.Length);
        Assert.Equal(InvoiceXmlRejection.AttributeTooLong, Refused($"<r a=\"{new string('x', 4097)}\"/>"));
    }

    [Fact]
    public void NamespaceDeclaration_OverTheLimit_Refused()
    {
        // FG 829: a declaration is no attribute of the invoice, but its value is the namespace of every element using it — a namespace of
        // megabytes was hashed for each of 17 000 elements (40 s); the limit of attribute values holds for it too
        var at = "urn:" + new string('t', 4092);
        Assert.Equal(at, Read($"<p:r xmlns:p=\"{at}\"><p:a/></p:r>").Root.Children[0].Namespace);
        Assert.Equal(InvoiceXmlRejection.AttributeTooLong, Refused($"<p:r xmlns:p=\"{at}x\"><p:a/></p:r>"));
        Assert.Equal(InvoiceXmlRejection.AttributeTooLong, Refused($"<r xmlns=\"{at}x\"/>"));   // the default namespace as well
    }

    [Fact]
    public void Name_OverTheLimit_Refused()
    {
        Assert.Equal(InvoiceXmlRejection.NameTooLong, Refused($"<r><{new string('n', 257)}/></r>"));
        Assert.Equal(InvoiceXmlRejection.NameTooLong, Refused($"<r {new string('n', 257)}=\"1\"/>"));
        Assert.Single(Read($"<r><{new string('n', 256)}/></r>").Root.Children);
    }

    [Fact]
    public void DistinctNames_OverTheLimit_Refused()
    {
        var xml = "<r>" + string.Concat(Enumerable.Range(0, 5000).Select(i => $"<e{i}/>")) + "</r>";
        Assert.Equal(InvoiceXmlRejection.TooManyNames, Refused(xml));
        // the same names again and again are no new names
        Assert.Equal(5001, Read("<r>" + string.Concat(Enumerable.Repeat("<e/>", 5000)) + "</r>").Elements);
    }

    [Fact]
    public void Namespaces_OverTheLimit_Refused()
    {
        var xml = "<r>" + string.Concat(Enumerable.Range(0, 65).Select(i => $"<x:e xmlns:x=\"urn:beispiel:{i}\"/>")) + "</r>";
        Assert.Equal(InvoiceXmlRejection.TooManyNamespaces, Refused(xml));
    }

    [Fact]
    public void Text_OverTheLimit_Refused_AlsoInPieces()
    {
        Assert.Equal(1 << 20, Read($"<r>{new string('x', 1 << 20)}</r>").Root.Text!.Length);
        Assert.Equal(InvoiceXmlRejection.TextTooLong, Refused($"<r>{new string('x', (1 << 20) + 1)}</r>"));
        // CDATA pieces count together
        Assert.Equal(InvoiceXmlRejection.TextTooLong, Refused($"<r><![CDATA[{new string('x', 600_000)}]]><![CDATA[{new string('x', 600_000)}]]></r>"));
        // pieces beside child elements count together
        var mixed = "<r>" + string.Concat(Enumerable.Repeat(new string('x', 1000) + "<b/>", 1100)) + "</r>";
        Assert.Equal(InvoiceXmlRejection.TextTooLong, Refused(mixed));
    }

    [Fact]
    public void Data_OverTheLimit_Refused() =>
        Assert.Equal(InvoiceXmlRejection.TooLarge, Refused("<r>" + new string('x', 200) + "</r>", new InvoiceXmlLimits { MaxBytes = 100 }));

    // ------------------------------------------------------------ text and attachments

    [Fact]
    public void ElementFlood_TreeStaysLean()
    {
        // the finding of the fuzz run 13.11.2026: 563 000 empty elements in 2.2 MB took 41 times their size (a list object and its spare room
        // per element); the tree of exact arrays takes about 26 times — the element limit bounds it as a whole
        var data = Encoding.UTF8.GetBytes("<r>" + string.Concat(Enumerable.Repeat("<x/>", 563_000)) + "</r>");
        InvoiceXml.Read(data);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var xml = InvoiceXml.Read(data);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(563_000, xml.Root.Children.Count);
        Assert.True(allocated < 32L * data.Length, $"{allocated >> 20} MB für {data.Length >> 10} KB");
    }

    [Fact]
    public void ManyPiecesOfText_JoinedLinearly()
    {
        // before, every piece joined the whole text again: 300 000 pieces took minutes
        var xml = Read("<r>" + string.Concat(Enumerable.Repeat("x<b/>", 300_000)) + "</r>");
        Assert.Equal(300_000, xml.Root.Text!.Length);
        Assert.Equal(300_000, xml.Root.Children.Count);
    }

    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void ManyPiecesOfText_Time()
    {
        var data = Encoding.UTF8.GetBytes("<r>" + string.Concat(Enumerable.Repeat("x<b/>", 300_000)) + "\n" + string.Concat(Enumerable.Repeat("<c/>\n   ", 300_000)) + "</r>");
        InvoiceXml.Read(data);
        var watch = Stopwatch.StartNew();
        InvoiceXml.Read(data);
        Assert.True(watch.Elapsed.TotalMilliseconds < 400 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void WhiteSpace_BetweenChildren_Dropped_InLeaf_Kept()
    {
        var xml = Read("<r>\n  <a>  </a>\n  <b>Rechnung 1</b>\n</r>");
        Assert.Null(xml.Root.Text);
        Assert.Equal("  ", xml.Root.Children[0].Text);
        Assert.Equal("Rechnung 1", xml.Root.Children[1].Text);
    }

    [Fact]
    public void Attachment_DecodedInBlocks_NextSiblingNotLost()
    {
        var payload = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray();
        var b64 = Convert.ToBase64String(payload);
        // written without any white space between the elements: the node after the attachment must not be skipped
        var xml = Read($"<r><AttachmentBinaryObject mimeCode=\"application/pdf\" filename=\"lieferschein.pdf\">{b64}</AttachmentBinaryObject><Weiter>1</Weiter><Ende/></r>");
        Assert.Equal(3, xml.Root.Children.Count);
        Assert.Equal(payload, xml.Root.Children[0].Binary);
        Assert.Equal("lieferschein.pdf", xml.Root.Children[0].Attribute("filename"));
        Assert.Equal("1", xml.Root.Children[1].Text);
        Assert.Equal("Ende", xml.Root.Children[2].Local);
        Assert.Equal(4, xml.Elements);
    }

    [Fact]
    public void Attachment_AsLastNode_And_AsRoot_Read()
    {
        Assert.Equal(new byte[] { 1, 2, 3 }, Read("<r><EmbeddedDocumentBinaryObject>AQID</EmbeddedDocumentBinaryObject></r>").Root.Children[0].Binary);
        Assert.Equal(new byte[] { 1, 2, 3 }, Read("<AttachmentBinaryObject>AQID</AttachmentBinaryObject>").Root.Binary);
    }

    [Fact]
    public void ManySmallAttachments_EachItsOwnBytes_LittleMemory()
    {
        // FG 829: each attachment began with a block of 64 KB of its own — 1 000 attachments of a few bytes took over 60 MB
        var sb = new StringBuilder("<r>");
        for (var i = 0; i < 1000; i++) sb.Append($"<AttachmentBinaryObject>{Convert.ToBase64String([(byte)i, (byte)(i >> 8), 7])}</AttachmentBinaryObject>");
        sb.Append("<AttachmentBinaryObject>").Append(Convert.ToBase64String(new byte[200_000])).Append("</AttachmentBinaryObject></r>");
        var data = Encoding.UTF8.GetBytes(sb.ToString());
        var before = GC.GetAllocatedBytesForCurrentThread();
        var xml = InvoiceXml.Read(data);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8L << 20, $"{allocated >> 20} MB");
        Assert.Equal(new byte[] { 0xE7, 0x03, 7 }, xml.Root.Children[999].Binary);   // each attachment its own, not the shared block
        Assert.Equal(new byte[] { 0, 0, 7 }, xml.Root.Children[0].Binary);
        Assert.Equal(200_000, xml.Root.Children[1000].Binary!.Length);           // a long one past the first block, whole
    }

    [Fact]
    public void Attachment_OverTheLimit_Or_Damaged_Refused()
    {
        var b64 = Convert.ToBase64String(new byte[2000]);
        Assert.Equal(InvoiceXmlRejection.BinaryTooLarge,
            Refused($"<r><AttachmentBinaryObject>{b64}</AttachmentBinaryObject></r>", new InvoiceXmlLimits { MaxBinaryBytes = 1000 }));
        Assert.Equal(InvoiceXmlRejection.Malformed, Refused("<r><AttachmentBinaryObject>A*B?C</AttachmentBinaryObject></r>"));
        Assert.Equal(InvoiceXmlRejection.Malformed, Refused("<r><AttachmentBinaryObject>AQID<x/></AttachmentBinaryObject></r>"));
    }

    // ------------------------------------------------------------ encoding

    [Fact]
    public void Utf16_And_BrokenUtf8_Refused()
    {
        Assert.Equal(InvoiceXmlRejection.NotUtf8, Refused([0xFF, 0xFE, (byte)'<', 0, (byte)'r', 0, (byte)'/', 0, (byte)'>', 0]));
        Assert.Equal(InvoiceXmlRejection.NotUtf8, Refused([0xFE, 0xFF, 0, (byte)'<', 0, (byte)'r', 0, (byte)'/', 0, (byte)'>']));
        Assert.Equal(InvoiceXmlRejection.NotUtf8, Refused([.. "<r>"u8, 0xC3, 0x28, .. "</r>"u8]));          // broken sequence
        Assert.Equal(InvoiceXmlRejection.NotUtf8, Refused([.. "<r>"u8, 0xC0, 0xAF, .. "</r>"u8]));          // overlong form
        Assert.Equal(InvoiceXmlRejection.NotUtf8, Refused([.. "<r>"u8, 0xED, 0xA0, 0x80, .. "</r>"u8]));    // surrogate
    }

    [Fact]
    public void Utf8_WithByteOrderMark_Read() => Assert.Equal("ä", InvoiceXml.Read([0xEF, 0xBB, 0xBF, .. "<r>ä</r>"u8]).Root.Text);

    [Theory]
    [InlineData("ISO-8859-1")]
    [InlineData("UTF-16")]
    [InlineData("windows-1252")]
    public void DeclaredEncoding_Noted_DataStillReadAsUtf8(string declared)
    {
        var xml = Read($"<?xml version=\"1.0\" encoding=\"{declared}\"?><r>Mustermann &amp; Söhne</r>");
        Assert.Equal("Mustermann & Söhne", xml.Root.Text);
        Assert.Equal(declared, xml.Notes.DeclaredEncoding);
    }

    [Fact]
    public void ForbiddenCharacters_Refused()
    {
        Assert.Equal(InvoiceXmlRejection.Malformed, Refused("<r>&#0;</r>"));
        Assert.Equal(InvoiceXmlRejection.Malformed, Refused([.. "<r>"u8, 0x01, .. "</r>"u8]));
    }

    // ------------------------------------------------------------ damaged and the rest

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<r>")]
    [InlineData("<r></s>")]
    [InlineData("<a/><b/>")]
    [InlineData("Text<r/>")]
    [InlineData("<r a=\"1\" a=\"2\"/>")]
    [InlineData("<x:r/>")]
    [InlineData("<DTD></x>")]                                                                                  // broken, but no DTD
    [InlineData("<!-- DOCTYPE --><r>")]
    public void Damaged_Refused(string xml) => Assert.Equal(InvoiceXmlRejection.Malformed, Refused(xml));

    [Fact]
    public void Refusal_NeverQuotesTheContent()
    {
        var e = Assert.Throws<InvoiceXmlException>(() => InvoiceXml.Read("<r>VERTRAULICH-0815 <unbekannt"u8.ToArray()));
        Assert.DoesNotContain("VERTRAULICH", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unbekannt", e.Message, StringComparison.Ordinal);
        Assert.Null(e.InnerException);
    }

    [Fact]
    public void Cancelled_Refused()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var e = Assert.Throws<InvoiceXmlException>(() => InvoiceXml.Read("<r/>"u8.ToArray(), null, cancel.Token));
        Assert.Equal(InvoiceXmlRejection.Cancelled, e.Reason);
    }

    [Fact]
    public void Path_And_Lookups()
    {
        var xml = Read("<r xmlns=\"urn:beispiel\" xmlns:b=\"urn:b\"><a><b:c x=\"1\">Wert</b:c><b:c>2</b:c></a></r>");
        var c = xml.Root.Child("urn:beispiel", "a")!.Child("urn:b", "c")!;
        Assert.Equal("/r/a/c", c.Path);
        Assert.Equal("1", c.Attribute("x"));
        Assert.Null(c.Attribute("y"));
        Assert.Equal(2, xml.Root.Children[0].ChildrenNamed("urn:b", "c").Count());
        Assert.Null(xml.Root.Child("urn:anders", "a"));
    }

    [Theory]
    [InlineData(new byte[] { (byte)'<', (byte)'r' }, true)]
    [InlineData(new byte[] { (byte)' ', (byte)'\n', (byte)'\t', (byte)'\r', (byte)'<' }, true)]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'<' }, true)]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'<', 0 }, true)]
    [InlineData(new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F' }, false)]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, false)]
    [InlineData(new byte[] { (byte)'P', (byte)'K', 3, 4 }, false)]
    [InlineData(new byte[] { (byte)' ', (byte)' ' }, false)]
    [InlineData(new byte[0], false)]
    public void LooksLikeXml_ByTheFirstSign(byte[] data, bool expected) => Assert.Equal(expected, InvoiceXml.LooksLikeXml(data));
}
