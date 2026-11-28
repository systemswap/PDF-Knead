using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core;
using PdfKnead.Core.Pdf;

namespace PdfKnead.ViewModels;

/// <summary>
/// Documents encrypted for recipients (a public-key handler, ISO 32000-2, 7.6.5): the user's certificate store is tried first; where no
/// certificate of it fits, a certificate file (.p12, .pfx) with its private key is asked for. Its private key is held only in memory, never
/// put into a key store (design decision, 24.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 1117).
/// </summary>
public partial class MainViewModel
{
    /// <summary>Picks a certificate file (.p12, .pfx) for a document encrypted for recipients (set by the view): the document's name → the file
    /// or null.</summary>
    public Func<string, Task<string?>>? PickCertificateFile { get; set; }

    /// <summary>
    /// Asks for a certificate that opens a document encrypted for recipients: says why, lets the user pick a certificate file and asks for
    /// its password until it opens; null if the user cancels. Without a view (tests) the refusal stands.
    /// </summary>
    /// <param name="name">The document's name.</param>
    /// <param name="refusal">The refusal of the reader (it names the handler).</param>
    /// <param name="again">A certificate file was given already and did not fit.</param>
    internal async Task<X509Certificate2?> AskRecipientCertificateAsync(string name, PdfRecipientException refusal, bool again)
    {
        if (AskChoice is null || PickCertificateFile is null || AskSessionPassword is null) throw refusal;
        var answer = await AskChoice(Tr.T("Für Empfänger verschlüsselt"),
            again
                ? Tr.T("Auch dieses Zertifikat öffnet „{0}“ nicht. Eine andere Zertifikatsdatei wählen?", name)
                : Tr.T("„{0}“ ist für bestimmte Empfänger verschlüsselt ({1}). In Ihrem Zertifikatsspeicher fand PDF-Knead kein passendes Zertifikat. Eine Zertifikatsdatei (.p12, .pfx) mit privatem Schlüssel wählen?",
                    name, Core.Security.DisplayNames.Safe(refusal.Filter)),
            Tr.T("Zertifikatsdatei wählen …"), null);
        if (answer != 1 || await PickCertificateFile(name) is not { } file) return null;
        var wrong = false;
        while (true)
        {
            var password = await AskSessionPassword(Tr.T("Zertifikatsdatei"), Tr.T("Passwort der Zertifikatsdatei {0}", Path.GetFileName(file)),
                Tr.T("Öffnen"), wrong);
            if (password is null) return null;
            try
            {
                var certificate = X509CertificateLoader.LoadPkcs12FromFile(file, password, X509KeyStorageFlags.EphemeralKeySet);
                if (certificate.HasPrivateKey) return certificate;
                certificate.Dispose();
                Message = Tr.T("{0} enthält keinen privaten Schlüssel.", Path.GetFileName(file));
                return null;
            }
            catch (CryptographicException)
            {
                wrong = true;
            }
        }
    }
}
