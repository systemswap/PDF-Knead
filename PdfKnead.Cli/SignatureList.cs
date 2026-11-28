using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;
using PdfKnead.Core.Security;

namespace PdfKnead.Cli;

/// <summary>
/// <c>pdf-knead signaturen &lt;file|folder&gt;</c>: checks the digital signatures of a file, or of every PDF in a folder (for
/// comparisons with a reference reader; also in its subfolders), and prints one block per signature; for a folder a summary at the end:
/// how many signatures got which outcome and which later changes (the counts before and after a phase, docs/ABLAUFPLAN.md, 2.7).
/// </summary>
internal static class SignatureList
{
    /// <summary>
    /// The validation report of one file (<c>pdf-knead signaturen &lt;datei&gt; --bericht text|html</c>; docs/ABLAUFPLAN.md, 9.3.5, step 4), as the
    /// window „Signaturen“ saves it.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="password">Password of an encrypted file.</param>
    /// <param name="html">HTML instead of text.</param>
    internal static string Report(string path, string? password, bool html)
    {
        using var doc = PdfDocument.Open(path, password);
        var checks = SignatureVerifier.Verify(doc, password);
        var (name, now) = (Path.GetFileName(path), DateTimeOffset.Now);
        return html ? ValidationReport.ToHtml(checks, name, AppVersion.Display, now) : ValidationReport.ToText(checks, name, AppVersion.Display, now);
    }

    /// <summary>Runs the check.</summary>
    /// <param name="path">A file or a folder.</param>
    /// <param name="password">Password of an encrypted file.</param>
    /// <returns>0 if all signatures are valid, 3 if one is not, 1 on a read error.</returns>
    public static int Run(string path, string? password)
    {
        // the names from the file (field, signer, issuer, authority, the notes that name them) with their directional formatting characters
        // shown (Unicode 9, UTR #36; docs/FEHLERGEDAECHTNIS.md, No. 1217)
        static void Say(string line) => Console.WriteLine(DisplayNames.Safe(line));
        var folder = Directory.Exists(path);
        var files = folder ? Directory.GetFiles(path, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray() : [path];
        var result = 0;
        var outcomes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var laterCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var indications = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int signed = 0, unreadable = 0;
        foreach (var file in files)
        {
            try
            {
                using var doc = PdfDocument.Open(file, password);
                var checks = SignatureVerifier.Verify(doc, password);
                if (files.Length > 1 && checks.Count == 0) continue;
                Say($"== {(folder ? Path.GetRelativePath(path, file) : Path.GetFileName(file))}");
                if (checks.Count > 0) signed++;
                if (checks.Count == 0) Say(Tr.T("Keine digitalen Signaturen."));
                foreach (var check in checks)
                {
                    Say($"   {check.FieldName}: {SignatureTexts.Outcome(check)}" + (check.IsDocumentTimestamp ? " · " + Tr.T("Dokument-Zeitstempel") : ""));
                    if (check.Problem is { } problem) Say($"      {problem}");
                    if (check.UnconfirmedReason is { } reason) Say($"      {reason}");
                    Say($"      EN 319 102-1: {SignatureTexts.Indication(check)}");
                    foreach (var warning in check.Warnings) Say($"      ! {warning}");
                    Say($"      {check.SignerName} · {check.SigningTime:yyyy-MM-dd HH:mm:ss zzz} · {check.Method}" +
                                      $" · {(check.CoversWholeFile ? Tr.T("ganze Datei") : Tr.T("frühere Fassung"))}");
                    if (check.Timestamp is { } stamp)
                        Say($"      Zeitstempel {stamp.Time:yyyy-MM-dd HH:mm:ss zzz} von {stamp.Authority} · {(stamp.Valid ? "passt" : "PASST NICHT")}{(stamp.Trusted ? ", Dienst vertraut" : "")}");
                    if (SignatureTexts.TrustList(check) is { } trustList) Say($"      {Tr.T("EU-Vertrauensliste")}: {trustList}");
                    if (check.TimestampService is { Qualified: true } qualified)
                        Say($"      {Tr.T("qualifizierter Zeitstempel ({0})", qualified.Service.Territory)}: {qualified.Service.Provider}");
                    if (SignatureTexts.LongTerm(check) is { } longTerm) Say($"      {Tr.T("Langzeitprüfung")}: {longTerm}");
                    if (SignatureTexts.PadesLevel(check) is { } pades) Say($"      {Tr.T("PAdES-Stufe")}: {pades}");
                    if (check.Commitment is { } commitment) Say($"      {Tr.T("Zweck")}: {commitment}");
                    if (SignatureTexts.RenewBefore(check) is { } renew) Say($"      {Tr.T("Schutz erneuern bis spätestens")}: {renew}");
                    if (check.Certificate is { } certificate)
                    {
                        Say($"      {Tr.T("Ausgestellt von {0}", certificate.Issuer)}{(certificate.SelfSigned ? " (" + Tr.T("selbst ausgestellt") + ")" : "")} · {certificate.Fingerprint}");
                        Say($"      {Tr.T("Stufe")}: {SignatureTexts.Level(check)}");
                        foreach (var note in SignatureTexts.NatureNotes(check)) Say($"      · {note}");
                    }
                    if (check.Certification is { } level) Say($"      {Tr.T("Zertifiziert")}: {Tr.T("danach erlaubt: {0}", SignatureTexts.Allowed(level))}");
                    if (check.Lock is { } fieldLock) Say($"      {Tr.T("Sperrt")}: {SignatureTexts.Locked(fieldLock)}");
                    if (SignatureTexts.Later(check) is { } later) Say($"      {later}");
                    foreach (var detail in check.LaterDetails) Say($"        – {detail}");
                    if (!SignatureTexts.IsValid(check)) result = 3;
                    var outcome = SignatureTexts.Outcome(check) + (check.IsDocumentTimestamp ? " (Zeitstempel)" : "");
                    outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                    laterCounts[check.Later.ToString()] = laterCounts.GetValueOrDefault(check.Later.ToString()) + 1;
                    var indication = SignatureTexts.Indication(check).ToString();
                    indications[indication] = indications.GetValueOrDefault(indication) + 1;
                }
            }
            catch (Exception e) when (e is PdfException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Say($"== {(folder ? Path.GetRelativePath(path, file) : Path.GetFileName(file))}: {e.Message}");
                unreadable++;
                result = result == 0 ? 1 : result;
            }
        }
        if (folder)
        {
            Console.WriteLine();
            Console.WriteLine($"Dateien: {files.Length}, mit Signaturen: {signed}, nicht lesbar: {unreadable}, Signaturen: {outcomes.Values.Sum()}");
            foreach (var (outcome, count) in outcomes) Console.WriteLine($"  {count,5}  {outcome}");
            Console.WriteLine("Spätere Fassungen:");
            foreach (var (kind, count) in laterCounts) Console.WriteLine($"  {count,5}  {kind}");
            Console.WriteLine("EN 319 102-1:");
            foreach (var (indication, count) in indications) Console.WriteLine($"  {count,5}  {indication}");
        }
        return result;
    }
}
