namespace PdfKnead.Core.Scanning.Escl;

/// <summary>
/// An error that points to a problem of the eSCL access with this device rather than to the device's state: an answer that cannot be
/// understood, a broken exchange, a damaged page, a refused request. After such an error the program takes the device's other way — SANE
/// or WIA — for the rest of the run (KONZEPT.md §5.25, S9, „SANE und WIA bleiben“). Paper jam, empty feeder, open cover, a busy or
/// switched-off device and a job cancelled at the device are ordinary <see cref="ScanException"/>s: the other way would meet them too.
/// </summary>
/// <param name="message">What to tell the user (translated).</param>
/// <param name="kind">What happened.</param>
public sealed class EsclProtocolException(string message, ScanErrorKind kind = ScanErrorKind.Other) : ScanException(message, kind);
