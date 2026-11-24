using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Tests.Pdf;

public sealed partial class EncryptionNormTests
{
    /// <summary>
    /// ISO 32000-2, 7.6.6, Table 25: /AuthEvent of the attachments' crypt filter decides when the password is asked. EFOpen: when an attachment
    /// is opened — the document opens without it, the attachments locked. DocOpen stated explicitly: when the document opens, though strings
    /// and streams are not encrypted. Left out (its default is DocOpen): opened like EFOpen, as most reference readers do (S-A A170) — design
    /// decision, 24.11.2026 (docs/FEHLERGEDAECHTNIS.md, No. 1114). The entry is rewritten in the bytes with the same length, the offsets stay.
    /// </summary>
    [Theory]
    [InlineData("EFOpen")]
    [InlineData("DocOpen")]
    [InlineData("fehlt")]
    public void AttachmentsOnly_AuthEvent_DecidesWhenThePasswordIsAsked(string authEvent)
    {
        var bytes = CryptFilterTests.WithLockedAttachment(Encoding.ASCII.GetBytes("Anlage von Max Mustermann"));
        var text = Encoding.Latin1.GetString(bytes);
        // the filter as the test file writes it: /AuthEvent/EFOpen/CFM/AESV2/Length 16 — DocOpen is one letter longer, /Length (not evaluated)
        // gives way
        var entry = Regex.Match(text, @"/AuthEvent\s*/EFOpen(\s*/CFM\s*/AESV2)\s*/Length\s*16");
        Assert.True(entry.Success);
        var replacement = authEvent switch
        {
            "DocOpen" => ("/AuthEvent/DocOpen" + entry.Groups[1].Value).PadRight(entry.Length),
            "fehlt" => entry.Groups[1].Value.PadRight(entry.Length),
            _ => entry.Value,
        };
        Assert.Equal(entry.Length, replacement.Length);
        Encoding.ASCII.GetBytes(replacement).CopyTo(bytes, entry.Index);
        if (authEvent == "DocOpen")
        {
            Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes));
            using var doc = PdfDocument.Open(bytes, "Anhang");
            Assert.False(doc.Security!.AttachmentsLocked);
        }
        else
        {
            using var doc = PdfDocument.Open(bytes);
            Assert.True(doc.Security!.AttachmentsLocked);
        }
    }
}
