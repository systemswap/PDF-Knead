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
        File.WriteAllLines(Path.Combine(folder, "liste.tsv"), list);
    }
}
