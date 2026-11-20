using System.Text.RegularExpressions;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Platform;

namespace PdfKnead.Services;

/// <summary>
/// Errors when reading, saving or exporting, formatted for the message bar. A crafted or damaged file must not terminate the
/// program (all other open documents would be lost with it): anything not reported as a <see cref="PdfException"/> or a
/// file error counts as invalid data, with a short message and the details in the log.
/// </summary>
public static partial class Failure
{
    /// <summary>Determines whether an exception is handled as a failure: everything except cancellation (cancelling is intended, not an error).</summary>
    /// <param name="ex">The caught exception.</param>
    /// <returns><see langword="false"/> for <see cref="OperationCanceledException"/>, otherwise <see langword="true"/>.</returns>
    public static bool Handled(Exception ex) => ex is not OperationCanceledException;

    /// <summary>Returns the text for the message bar; unexpected errors are written to the log with their stack trace.</summary>
    /// <param name="ex">The caught exception.</param>
    /// <returns>The message of a <see cref="PdfException"/>, a translated file error, or a generic translated message for invalid data.</returns>
    public static string Text(Exception ex)
    {
        if (ex is PdfException) return ex.Message;
        if (ex is IOException or UnauthorizedAccessException) return FileText(ex);
        AppLog.Write("Ungültige Daten (abgefangen)", ex.ToString());
        return ex is OutOfMemoryException or InsufficientExecutionStackException
            ? Tr.T("Die Datei ist zu groß oder zu tief verschachtelt.")
            : Tr.T("Die Datei enthält ungültige Daten.");
    }

    /// <summary>
    /// Makes file errors understandable: the common ones as translated texts instead of the English .NET message, and without the name
    /// of the temporary file (“Example.pdf.1a2b3c4d.pdfknead-tmp”), because the user only knows their own file.
    /// </summary>
    private static string FileText(Exception ex)
    {
        const int DiskFull = unchecked((int)0x80070070), HandleDiskFull = unchecked((int)0x80070027), SharingViolation = unchecked((int)0x80070020),
            LockViolation = unchecked((int)0x80070021), UnixNoSpace = 28;
        var name = ex is FileNotFoundException { FileName: { } f } ? Path.GetFileName(OwnName(f)) : null;
        return ex switch
        {
            IOException { HResult: DiskFull or HandleDiskFull or UnixNoSpace } => Tr.T("Auf dem Datenträger ist nicht genug Platz."),
            IOException { HResult: SharingViolation or LockViolation } =>
                Tr.T("Die Datei wird gerade von einem anderen Programm benutzt (etwa OneDrive oder ein PDF-Leser) — später noch einmal versuchen."),
            FileNotFoundException when name is not null => Tr.T("Die Datei {0} wurde nicht gefunden.", name),
            DirectoryNotFoundException => Tr.T("Der Ordner wurde nicht gefunden."),
            UnauthorizedAccessException when ex.Message == Tr.T("Die Datei ist schreibgeschützt.") => ex.Message,
            UnauthorizedAccessException => Tr.T("Kein Zugriff auf die Datei oder den Ordner (Rechte, Schreibschutz)."),
            _ => OwnName(ex.Message),
        };
    }

    /// <summary>Replaces the names of the program's own temporary files with the actual file name (removes the temporary suffix).</summary>
    private static string OwnName(string text) => TemporaryName().Replace(text, "");

    [GeneratedRegex(@"\.[0-9a-f]{8}\.pdfknead-(tmp|alt)", RegexOptions.IgnoreCase)]
    private static partial Regex TemporaryName();
}
