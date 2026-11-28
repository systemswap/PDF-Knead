using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Encryption (ISO 32000-2, 7.6; Konformitätsabgleich M4, docs/normabgleich/M4-verschluesselung.md). Nachweise „EN.“ in the report.
/// </summary>
public sealed partial class EncryptionNormTests
{
    /// <summary>A file with one page whose catalogue also names object 4 (<paramref name="extra"/>, a stream).</summary>
    private static byte[] FileWith(string extra)
    {
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (var body in new[] { "<< /Type /Catalog /Pages 2 0 R /Extra 4 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                     "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>", extra })
        {
            offsets.Add(text.Length);
            text.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        var xref = text.Length;
        text.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) text.Append($"{o:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    /// <summary>The test collection folder (TestPDFs), found upwards from this source file.</summary>
    private static string TestPdf(string path, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return Path.Combine(dir.FullName, "TestPDFs", path);
        throw new DirectoryNotFoundException();
    }

    /// <summary>The text of the first page, as the program reads it.</summary>
    private static string FirstPageText(PdfDocument doc) =>
        PdfKnead.Core.Pdf.Text.PageText.Create(PdfKnead.Core.Pdf.Content.PageInterpreter.BuildText(doc, doc.Pages[0])).Text;

    /// <summary>
    /// Revision 4 with an RC4 key of 5 bytes whose data are encrypted with the key padded with zero bytes to 16 bytes (Algorithm 1 then takes
    /// n = 16), /U computed with the key of the norm (test file issue19484_1, _2): the norm does not describe this, a widely used reader opens such
    /// files. PDF-Knead reads them with the padded key — only because a stream decodes with it and not with the key of the norm — says so, and
    /// writes the copy with the key of the norm: opened again it reads with that key, the content the same. Before, nothing of the content
    /// could be read (docs/FEHLERGEDAECHTNIS.md, No. 894; sonderregeln.md A105).
    /// </summary>
    [Theory]
    [InlineData("pdfjs/issue19484_1.pdf")]
    [InlineData("pdfjs/issue19484_2.pdf")]
    public void ShortRc4KeyPaddedByTheWriter_ReadWithThePaddedKey_CopyAsTheNormAsks(string path)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(TestPdf(path)));
        Assert.Contains(doc.Problems, p => p.Contains("16 Byte", StringComparison.Ordinal));
        var content = FirstPageText(doc);
        Assert.Contains("This PDF demonstrates", content);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = "1.7" });
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.NotNull(saved.Security);
        Assert.DoesNotContain(saved.Problems, p => p.Contains("16 Byte", StringComparison.Ordinal));   // encrypted as the norm asks
        Assert.Equal(content, FirstPageText(saved));
    }

    /// <summary>
    /// SASLprep (RFC 4013 with the tables of RFC 3454) against the tables of the RFC itself, code point by code point: mapped to nothing (B.1),
    /// non-ASCII spaces (C.1.2), prohibited (C.2.1 to C.9), unassigned in Unicode 3.2 (A.1, refused for stored strings), right-to-left (D.1) and
    /// left-to-right (D.2); the case foldings B.2 and B.3 SASLprep does not use (the password keeps its case). Local only: rfc3454.txt in
    /// Normen-ISO/grundlagen/rfc (or the folder PDFKNEAD_RFC names); without it the test is skipped — before, it passed without checking
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1095). Konformitätsabgleich M4.
    /// </summary>
    [Rfc3454Fact]
    public void SaslPrep_AgainstTheTablesOfRfc3454()
    {
        var tables = new Dictionary<string, HashSet<int>>();
        HashSet<int>? current = null;
        foreach (var line in File.ReadLines(Rfc3454FactAttribute.Path()!))
        {
            var start = System.Text.RegularExpressions.Regex.Match(line, @"----- Start Table ([A-D][.0-9]+) -----");
            if (start.Success)
            {
                tables[start.Groups[1].Value] = current = [];
                continue;
            }
            if (line.Contains("----- End Table", StringComparison.Ordinal))
            {
                current = null;
                continue;
            }
            var entry = System.Text.RegularExpressions.Regex.Match(line, @"^\s+([0-9A-F]{4,6})(?:-([0-9A-F]{4,6}))?\s*(;|$)");
            if (current is null || !entry.Success) continue;
            var from = Convert.ToInt32(entry.Groups[1].Value, 16);
            var to = entry.Groups[2].Success ? Convert.ToInt32(entry.Groups[2].Value, 16) : from;
            for (var c = from; c <= to; c++) current.Add(c);
        }
        string[] prohibitedTables = ["C.2.1", "C.2.2", "C.3", "C.4", "C.5", "C.6", "C.7", "C.8", "C.9"];
        var prohibited = prohibitedTables.SelectMany(t => tables[t]).ToHashSet();
        var mismatches = new Dictionary<string, List<int>>();
        void Mismatch(string kind, int c) => (mismatches.TryGetValue(kind, out var list) ? list : mismatches[kind] = []).Add(c);

        for (var c = 0; c <= 0x10FFFF; c++)
        {
            if (c is >= 0xD800 and <= 0xDFFF) continue;   // surrogates: no character on their own
            var s = char.ConvertFromUtf32(c);
            var prepared = StandardSecurityHandler.PrepareAesPassword(s);
            // RFC 4013 maps C.1.2 to a space (2.1) before B.1 to nothing (2.2): U+200B stands in both
            if ((tables["B.1"].Contains(c) && !tables["C.1.2"].Contains(c)) != (prepared.Length == 0)) Mismatch("B.1 auf nichts abgebildet", c);
            if (tables["C.1.2"].Contains(c) && prepared is not [0x20]) Mismatch("C.1.2 Leerzeichen", c);
            // unassigned in Unicode 3.2 (A.1): refused whatever NFKC of the runtime makes of it (No. 1095: U+1D2C passed as „A“)
            if (tables["A.1"].Contains(c) && StandardSecurityHandler.SaslRefusal(s) is null) Mismatch("A.1 nicht zugewiesen, nicht abgelehnt", c);
            // B.2 and B.3 (case folding) are not part of SASLprep: the prepared password is NFKC of the character, its case kept
            if ((tables["B.2"].Contains(c) || tables["B.3"].Contains(c)) && !tables["B.1"].Contains(c) && !tables["C.1.2"].Contains(c)
                && !prepared.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(s.Normalize(NormalizationForm.FormKC))))
                Mismatch("B.2/B.3 gefaltet", c);
            string stable;
            try
            {
                stable = s.Normalize(System.Text.NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (stable != s || tables["B.1"].Contains(c) || tables["C.1.2"].Contains(c)) continue;   // the remaining checks: characters that stay
            var expectRefused = prohibited.Contains(c) || tables["A.1"].Contains(c);
            var refused = StandardSecurityHandler.SaslRefusal(s) is not null;
            if (expectRefused && !refused) Mismatch(tables["A.1"].Contains(c) ? "A.1 nicht zugewiesen, nicht abgelehnt" : "C verboten, nicht abgelehnt", c);
            if (!expectRefused && refused) Mismatch("erlaubt, aber abgelehnt", c);
            if (expectRefused || refused) continue;
            // bidirectional: beside a left-to-right letter only characters that are not right-to-left (D.1); between right-to-left ones no
            // left-to-right (D.2)
            if (tables["D.1"].Contains(c) != (StandardSecurityHandler.SaslRefusal("a" + s) is not null)) Mismatch("D.1 rechts nach links", c);
            if (!tables["D.1"].Contains(c) && tables["D.2"].Contains(c) != (StandardSecurityHandler.SaslRefusal("א" + s + "א") is not null))
                Mismatch("D.2 links nach rechts", c);
        }
        var report = string.Join("\n", mismatches.Select(m => $"{m.Key}: {m.Value.Count} (z. B. {string.Join(", ", m.Value.Take(8).Select(c => $"U+{c:X4}"))})"));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pdfknead-saslprep.txt"), report);
        Assert.True(mismatches.Count == 0, report);
    }

    /// <summary>
    /// SASLprep as RFC 4013 and 3454 define it (docs/FEHLERGEDAECHTNIS.md, No. 752): characters unassigned in Unicode 3.2 are refused in a
    /// password being set (another program could normalise them differently); the direction check follows the tables D.1 and D.2 (a Hebrew
    /// accent beside Latin letters and a modifier letter between Hebrew ones are allowed, a Latin letter between Hebrew ones not); U+200B is a
    /// space. Before: the emoji was taken, the two allowed passwords refused, U+200B removed.
    /// </summary>
    [Fact]
    public void SaslPrep_Rfc3454Tables()
    {
        Assert.NotNull(StandardSecurityHandler.SaslRefusal("Passwort\U0001F600"));
        Assert.Null(StandardSecurityHandler.SaslRefusal("a\u0591b"));
        Assert.Null(StandardSecurityHandler.SaslRefusal("אʹא"));
        Assert.NotNull(StandardSecurityHandler.SaslRefusal("אaא"));
        Assert.Equal("a b"u8.ToArray(), StandardSecurityHandler.PrepareAesPassword("a\u200Bb"));
        Assert.Equal("ab"u8.ToArray(), StandardSecurityHandler.PrepareAesPassword("a\u00ADb"));
    }

    /// <summary>
    /// AES key wrap as PDF-Knead implements it for the MAC of ISO/TS 32004 (RFC 3394, 2.2.1 and 2.2.2), with the test vectors of RFC 3394,
    /// 4.1, 4.3, 4.5 and 4.6 — and a changed byte is not unwrapped.
    /// </summary>
    [Theory]
    [InlineData("000102030405060708090A0B0C0D0E0F", "00112233445566778899AABBCCDDEEFF", "1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5")]
    [InlineData("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F", "00112233445566778899AABBCCDDEEFF",
        "64E8C3F9CE0F5BA263E9777905818A2A93C8191E7D6E8AE7")]
    [InlineData("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F", "00112233445566778899AABBCCDDEEFF0001020304050607",
        "A8F9BC1612C68B3FF6E6F4FBE30E71E4769C8B80A32CB8958CD5D17D6B254DA1")]
    [InlineData("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F", "00112233445566778899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F",
        "28C9F404C4B810F4CBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43BFB988B9B7A02DD21")]
    public void KeyWrap_Rfc3394TestVectors(string kek, string key, string wrapped)
    {
        var k = Convert.FromHexString(kek);
        Assert.Equal(wrapped, Convert.ToHexString(PdfMac.Wrap(k, Convert.FromHexString(key))));
        Assert.Equal(key, Convert.ToHexString(PdfMac.Unwrap(k, Convert.FromHexString(wrapped))!));
        var changed = Convert.FromHexString(wrapped);
        changed[^1] ^= 1;
        Assert.Null(PdfMac.Unwrap(k, changed));
    }

    /// <summary>
    /// 7.6.6, Table 25: the standard security handler counts the /Length of the crypt filter in bytes in every version (32 for AESV3; the
    /// resolution of pdf-issues 184, approved by ISO, keeps 128/256 for public-key handlers) — No. 753 had written 256 in 2.0
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1096). The file opens either way.
    /// </summary>
    [Theory]
    [InlineData("2.0", 32)]
    [InlineData("1.7", 32)]
    public void CryptFilterLength_AsTheVersionCounts(string version, int length)
    {
        using var doc = PdfDocument.Open(FileWith("<< /Length 3 >>\nstream\nABC\nendstream"));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output,
            new PdfSaveOptions { Version = version, Protection = new PdfProtection("geheim", "Besitzer", PdfPermissions.All) });
        using var saved = PdfDocument.Open(output.ToArray(), "geheim");
        var std = saved.EncryptDictionary!.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!;
        Assert.Equal("AESV3", std.GetName(PdfName.Get("CFM"))!.Value);
        Assert.Equal(length, std.GetInteger(PdfName.Length));
        Assert.Equal("ABC"u8.ToArray(), saved.DecodeStream(Assert.IsType<PdfStream>(saved.Catalog!.Get(PdfName.Get("Extra")))).Data);
    }

    /// <summary>
    /// A kept encryption whose crypt filter of the standard handler gives its /Length in bits (256 for AESV3, as No. 753 wrote it in 2.0) is
    /// written with 32 bytes (Table 25 with pdf-issues 184); the source's dictionary stays as it is, the copy opens with its password
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1096).
    /// </summary>
    [Theory]
    [InlineData("2.0")]
    [InlineData("1.7")]
    public void CryptFilterLength_InBits_WrittenInBytesWhenKept(string version)
    {
        using var doc = PdfDocument.Open(PdfKnead.Tests.Pdf.TestPdf.Simple(1).Build(cipher: PdfCipher.Aes256, user: "geheim", owner: "Besitzer"), "geheim");
        var std = doc.EncryptDictionary!.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!;
        std.Set(PdfName.Length, PdfInteger.Of(256));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Version = version, KeepEncryption = true });
        using var saved = PdfDocument.Open(output.ToArray(), "geheim");
        Assert.Equal(32, saved.EncryptDictionary!.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!.GetInteger(PdfName.Length));
        Assert.Equal(256, std.GetInteger(PdfName.Length));
    }

    /// <summary>
    /// 7.5.8.2 with the correction the model of ISO 32000-2 follows (pdf-issues 246): behind a cross-reference stream the encryption dictionary
    /// stands directly in it — before, an indirect one did, which the model refuses from PDF 2.0 (docs/FEHLERGEDAECHTNIS.md, No. 811). Behind a
    /// table it stays an object of its own. The file opens with its password either way, its strings stay plain (the stream is not
    /// encrypted), and an incremental update carries /Encrypt on as it is.
    /// </summary>
    [Theory]
    [InlineData("2.0", true)]
    [InlineData("1.7", true)]
    [InlineData("1.7", false)]
    public void EncryptDictionary_DirectInCrossReferenceStream(string version, bool objectStreams)
    {
        using var doc = PdfDocument.Open(FileWith("<< /Length 3 >>\nstream\nABC\nendstream"));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output,
            new PdfSaveOptions { Version = version, ObjectStreams = objectStreams, Protection = new PdfProtection("geheim", "Besitzer", PdfPermissions.All) });
        using var saved = PdfDocument.Open(output.ToArray(), "geheim");
        var raw = saved.Trailer.GetRaw(PdfName.Encrypt);
        if (objectStreams) Assert.IsType<PdfDictionary>(raw);
        else Assert.IsType<PdfReference>(raw);
        Assert.Equal(saved.Trailer.GetRaw(PdfName.Get("Type")) is PdfName { Value: "XRef" }, objectStreams);
        Assert.Equal("ABC"u8.ToArray(), saved.DecodeStream(Assert.IsType<PdfStream>(saved.Catalog!.Get(PdfName.Get("Extra")))).Data);
        Assert.True(IncrementalUpdate.CanAppend(saved));
        var update = new IncrementalUpdate(saved);
        update.Add(new PdfDictionary());
        using var appended = new MemoryStream();
        update.WriteTo(appended);
        using var again = PdfDocument.Open(appended.ToArray(), "geheim");
        Assert.IsType(raw.GetType(), again.Trailer.GetRaw(PdfName.Encrypt));
        Assert.Equal("ABC"u8.ToArray(), again.DecodeStream(Assert.IsType<PdfStream>(again.Catalog!.Get(PdfName.Get("Extra")))).Data);
    }

    /// <summary>
    /// 7.6.2: the contents of a stream that refers to an external file (/F) are not encrypted — they are not part of the PDF file. Before, the
    /// bytes in the file (here „ABC“) were encrypted like any stream (docs/FEHLERGEDAECHTNIS.md, No. 751).
    /// </summary>
    [Fact]
    public void ExternalFileStream_NotEncrypted()
    {
        using var doc = PdfDocument.Open(FileWith("<< /F (extern.txt) /Length 3 >>\nstream\nABC\nendstream"));
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output,
            new PdfSaveOptions { Version = "1.7", ObjectStreams = false, Protection = new PdfProtection("geheim", "Besitzer", PdfPermissions.All) });
        using var saved = PdfDocument.Open(output.ToArray(), "geheim");
        Assert.NotNull(saved.Security);
        var stream = Assert.IsType<PdfStream>(saved.Catalog!.Get(PdfName.Get("Extra")));
        Assert.Equal("ABC"u8.ToArray(), saved.FileBytes(stream.DataOffset, stream.DataLength).ToArray());
        Assert.Equal("ABC"u8.ToArray(), saved.GetRawStreamData(stream));
    }

    // ---- Nachweise added in the Nacharbeit (12.11.2026) for rows that stood without one

    /// <summary>
    /// 7.6.5: a file encrypted for recipients (Adobe.PubSec, adbe.pkcs7.s5) whose recipient object no certificate at hand opens (here not even
    /// CMS; the recipients stand in the dictionary instead of the crypt filter, read with a note): refused with the handler's name — the
    /// caller may ask for a certificate (docs/FEHLERGEDAECHTNIS.md, No. 1117; reading and writing: PublicKeyEncryptionTests).
    /// </summary>
    [Fact]
    public void PublicKeyHandler_RefusedWithItsName()
    {
        var text = Encoding.Latin1.GetString(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s5 /V 4 /Length 128 /Recipients [<3000>] >>"));
        const string id = "<00112233445566778899AABBCCDDEEFF>";
        var file = Encoding.Latin1.GetBytes(text.Replace("/Root 1 0 R >>", $"/Root 1 0 R /Encrypt 3 0 R /ID [{id} {id}] >>"));
        var error = Assert.ThrowsAny<Core.Pdf.PdfException>(() => PdfDocument.Open(file).Dispose());
        Assert.Contains("Adobe.PubSec", error.Message);
    }

    /// <summary>
    /// 7.6.7: a file with a collection showing an encrypted payload as attachment, but without /AF naming it as /EncryptedPayload, is no wrapper
    /// in the sense of 7.6.7 — it opens as any file, its page is shown and the payload is one of its attachments. A real wrapper (with /AF) is
    /// recognised and its payload offered: EncryptedPayloadUiTests (docs/FEHLERGEDAECHTNIS.md, No. 1115).
    /// </summary>
    [Fact]
    public void EncryptedPayload_WrapperOpensAsAFile_PayloadIsAnAttachment()
    {
        const string content = "BT /F1 12 Tf 20 100 Td (Verschluesselter Inhalt im Anhang) Tj ET";
        using var doc = PdfDocument.Open(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(geheim.pdf) 5 0 R] >> >> /Collection << /D (geheim.pdf) /View /H >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 7 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Filespec /F (geheim.pdf) /UF (geheim.pdf) /EF << /F 6 0 R >> /AFRelationship /EncryptedPayload " +
            "/EP << /Type /EncryptedPayload /Subtype /Beispiel /Version (1) >> >>",
            "<< /Type /EmbeddedFile /Subtype /application#2Fpdf /Length 3 >>\nstream\nABC\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        Assert.Equal(1, doc.PageCount);
        Assert.Contains("Anhang", string.Concat(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text)));
        Assert.Equal(1, DocumentCleanup.DocumentAttachments(doc));
    }
}
