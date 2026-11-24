using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The files of the read tolerances of M4 (PLAN §6 point 8 b; tools/normabgleich/lesetoleranz.py): each breaks one rule of ISO 32000-2, 7.6,
/// ISO/TS 32003 or 32004 that PDF-Knead tolerates when reading, opens without a password (an empty user password — the readers are given
/// none) and shows text. Written only with PDFKNEAD_M4_PROBEN (the folder), with the list liste.tsv.
/// </summary>
public sealed partial class EncryptionNormTests
{
    /// <summary>Writes the probe files and liste.tsv (tolerance, file, page, what it breaks) — only with PDFKNEAD_M4_PROBEN.</summary>
    [Fact]
    public void ReadTolerances_ProbeFiles()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_M4_PROBEN") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var list = new List<string>();
        void Probe(string name, string what, byte[] file)
        {
            File.WriteAllBytes(Path.Combine(folder, name + ".pdf"), file);
            list.Add($"{name}\t{name}.pdf\t1\t{what}");
        }
        static byte[] Build(PdfCipher cipher, Action<PdfDictionary>? patch = null, string version = "1.7") =>
            Pdf.TestPdf.Simple(1).Build(cipher: cipher, user: "", owner: "Besitzer", version: version, patchEncrypt: patch);

        Probe("V3", "/V 3 (in ISO 32000-2 nicht vorgesehen), RC4 wie V 2 (S-A A82)", Build(PdfCipher.Rc4, e => e.Set(PdfName.Get("V"), PdfInteger.Of(3))));
        // RC4 data (the key of revision 3 equals that of revision 4 with encrypted metadata) under V 4, the filter without /CFM: read as RC4
        Probe("ohne-CFM", "V 4, Kryptfilter ohne /CFM (Vorgabe None), Daten mit RC4 (S-A A16)", Build(PdfCipher.Rc4, e =>
        {
            e.Set(PdfName.Get("V"), PdfInteger.Of(4));
            e.Set(PdfName.Get("R"), PdfInteger.Of(4));
            var std = new PdfDictionary();
            std.Set(PdfName.Length, PdfInteger.Of(16));
            var cf = new PdfDictionary();
            cf.Set(PdfName.Get("StdCF"), std);
            e.Set(PdfName.Get("CF"), cf);
            e.Set(PdfName.Get("StmF"), PdfName.Get("StdCF"));
            e.Set(PdfName.Get("StrF"), PdfName.Get("StdCF"));
        }));
        Probe("anderer-Filtername", "Kryptfilter unter einem anderen Namen als StdCF (S-A A83)", Build(PdfCipher.Aes128, e =>
        {
            var cf = e.GetDictionary(PdfName.Get("CF"))!;
            var std = cf.GetDictionary(PdfName.Get("StdCF"))!;
            cf.Remove(PdfName.Get("StdCF"));
            cf.Set(PdfName.Get("MeinFilter"), std);
            e.Set(PdfName.Get("StmF"), PdfName.Get("MeinFilter"));
            e.Set(PdfName.Get("StrF"), PdfName.Get("MeinFilter"));
        }));
        Probe("ohne-P", "Revision 6 ohne /P (Pflichteintrag)", Build(PdfCipher.Aes256, e => e.Remove(PdfName.Get("P")), "2.0"));
        Probe("U-O-laenger", "Revision 6 mit /U und /O länger als 48 Byte", Build(PdfCipher.Aes256, e =>
        {
            foreach (var key in new[] { "U", "O" })
                e.Set(PdfName.Get(key), new PdfString([.. e.GetString(PdfName.Get(key))!.Bytes, 0, 0]));
        }, "2.0"));
        Probe("Filter-ohne-Type", "Kryptfilter ohne /Type und mit /Length 128 statt 16", Build(PdfCipher.Aes128, e =>
        {
            var std = e.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!;
            std.Remove(PdfName.Type);
            std.Set(PdfName.Length, PdfInteger.Of(128));
        }));
        Probe("EFF-unbekannt", "/EFF nennt einen Filter, den /CF nicht hat", Build(PdfCipher.Aes128, e => e.Set(PdfName.Get("EFF"), PdfName.Get("Fehlt"))));
        Probe("V6-R6", "V 6 (AES-GCM) mit Revision 6 statt 7", Build(PdfCipher.Aes256Gcm, e => e.Set(PdfName.Get("R"), PdfInteger.Of(6)), "2.0"));
        Probe("V4-ohne-Length", "V 4 ohne /Length (Schlüssellänge aus dem Kryptfilter)", Build(PdfCipher.Aes128, e => e.Remove(PdfName.Length)));
        Probe("R5", "Revision 5 (nicht in ISO 32000-2), gelesen", Revision5File(userPassword: ""));
        Probe("AESV2-unter-V5", "Revision 6 (V 5) mit einem Kryptfilter AESV2, Daten mit AES-128 je Objekt (S-A A163)", ProbeFile(PdfCipher.Aes256, "AESV2", damagedPadding: false));
        Probe("Auffuellung-beschaedigt", "AES-128, die Auffüllung des Seiteninhalts beschädigt (S-A A162)", ProbeFile(PdfCipher.Aes128, null, damagedPadding: true));
        File.WriteAllLines(Path.Combine(folder, "liste.tsv"), list);
    }
    /// <summary>
    /// The files of the reader check of the writer (PLAN §6 point 8 b, „Leser-Verträglichkeit“): a new protection as PDF-Knead writes it in
    /// 1.4 (RC4), 1.6 (AES-128, no /Length since No. 1108), 1.7 (AES-256 with the ADBE declaration) and 2.0 (AES-256, the crypt filter's
    /// /Length in bytes since No. 1096), and a revision-6 encryption sealed anew without the MAC requirement (No. 1105) — each with an empty
    /// user password, so the reference readers open it. Written only with PDFKNEAD_M4_SCHREIBER (the folder), with liste.tsv.
    /// </summary>
    [Fact]
    public void ReaderCheck_WrittenFiles()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_M4_SCHREIBER") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var list = new List<string>();
        using var source = Core.Pdf.Document.PdfDocument.Open(Pdf.TestPdf.Simple(2).Build());
        foreach (var version in new[] { "1.4", "1.6", "1.7", "2.0" })
        {
            using var output = new MemoryStream();
            Core.Pdf.Writing.PdfWriter.Write(source, Core.Pdf.Editing.PageItem.From(source), output, new Core.Pdf.Writing.PdfSaveOptions
            {
                Version = version, Protection = new Core.Pdf.Writing.PdfProtection("", "Besitzer", PdfPermissions.All),
            });
            var name = $"neu-{version}";
            File.WriteAllBytes(Path.Combine(folder, name + ".pdf"), output.ToArray());
            list.Add($"{name}\t{name}.pdf\t1\tneuer Schutz in {version}");
        }
        var macFile = Pdf.TestPdf.Simple(2).Build(cipher: PdfCipher.Aes256, user: "", owner: "Besitzer", version: "2.0", macRequired: true);
        using (var mac = Core.Pdf.Document.PdfDocument.Open(macFile))
        {
            using var output = new MemoryStream();
            Core.Pdf.Writing.PdfWriter.Write(mac, Core.Pdf.Editing.PageItem.From(mac), output,
                new Core.Pdf.Writing.PdfSaveOptions { Version = "2.0", KeepEncryption = true });
            File.WriteAllBytes(Path.Combine(folder, "neuversiegelt-2.0.pdf"), output.ToArray());
            list.Add("neuversiegelt\tneuversiegelt-2.0.pdf\t1\tRevision 6 ohne MAC-Pflicht neu versiegelt");
        }
        File.WriteAllLines(Path.Combine(folder, "liste.tsv"), list);
    }
    /// <summary>
    /// A one-page probe built here: the encryption dictionary of a new protection (empty user password), its crypt filter set to
    /// <paramref name="method"/> where given, and the page content encrypted by the test with AES-128 and the key per object of algorithm 1
    /// (MD5 of the file key, object and generation number low byte first, „sAlT“) — with the padding of RFC 8018 or, with
    /// <paramref name="damagedPadding"/>, a last block whose padding bytes disagree.
    /// </summary>
    private static byte[] ProbeFile(PdfCipher cipher, string? method, bool damagedPadding)
    {
        byte[] firstId = [.. Enumerable.Range(1, 16).Select(i => (byte)(i * 5))];
        var (encrypt, handler) = StandardSecurityHandler.Create(cipher, "", "Besitzer", PdfPermissions.All, firstId);
        var fileKey = (byte[])typeof(StandardSecurityHandler).GetField("_key", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(handler)!;
        if (method is not null) encrypt.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!.Set(PdfName.Get("CFM"), PdfName.Get(method));
        if (method == "AESV2") encrypt.GetDictionary(PdfName.Get("CF"))!.GetDictionary(PdfName.Get("StdCF"))!.Set(PdfName.Length, PdfInteger.Of(16));
        var plain = System.Text.Encoding.ASCII.GetBytes("BT /F1 24 Tf 72 700 Td (Probe der Verschluesselung) Tj ET");
        var objectKey = System.Security.Cryptography.MD5.HashData([.. fileKey, 4, 0, 0, 0, 0, .. "sAlT"u8]);
        byte[] padded;
        if (damagedPadding)
        {
            var pad = 16 - plain.Length % 16;
            padded = [.. plain, .. Enumerable.Repeat((byte)pad, pad)];
            padded[^2] = (byte)(pad + 1);   // the padding bytes disagree
        }
        else padded = [];
        byte[] content;
        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = objectKey;
            var iv = Enumerable.Range(0, 16).Select(i => (byte)(30 + i)).ToArray();
            content = damagedPadding ? [.. iv, .. aes.EncryptCbc(padded, iv, System.Security.Cryptography.PaddingMode.None)]
                : [.. iv, .. aes.EncryptCbc(plain, iv, System.Security.Cryptography.PaddingMode.PKCS7)];
        }
        var output = new MemoryStream();
        void Write(string s) => output.Write(System.Text.Encoding.Latin1.GetBytes(s));
        var offsets = new List<long>();
        Write("%PDF-1.7\n");
        void Object(string body)
        {
            offsets.Add(output.Position);
            Write($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
        offsets.Add(output.Position);
        Write($"4 0 obj\n<< /Length {content.Length} >>\nstream\n");
        output.Write(content);
        Write("\nendstream\nendobj\n");
        offsets.Add(output.Position);
        Write("5 0 obj\n");
        Core.Pdf.Writing.PdfSerializer.Write(encrypt, output);
        Write("\nendobj\n");
        var xref = output.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) Write($"{off:D10} 00000 n \n");
        var id = Convert.ToHexString(firstId);
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Encrypt 5 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
}
