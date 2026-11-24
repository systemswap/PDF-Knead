namespace PdfKnead.Core.Pdf;

/// <summary>
/// Thrown when a file is not a PDF or is so damaged that even the repair finds nothing usable.
/// The message is localised and can be shown to the user as it is.
/// </summary>
public class PdfException : Exception
{
    /// <summary>Creates the exception with a localised message.</summary>
    /// <param name="message">The localised message.</param>
    public PdfException(string message) : base(message) { }

    /// <summary>Creates the exception with a localised message and the exception that caused it.</summary>
    /// <param name="message">The localised message.</param>
    /// <param name="inner">The underlying exception.</param>
    public PdfException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when the file is encrypted and needs a password, or the given password is wrong.</summary>
/// <param name="message">The localised message.</param>
/// <param name="wrongPassword">True if a password was given but does not match.</param>
public sealed class PdfPasswordException(string message, bool wrongPassword) : PdfException(message)
{
    /// <summary>True if a password was given but does not match; false if none was given.</summary>
    public bool WrongPassword { get; } = wrongPassword;
}

/// <summary>
/// Thrown when an attachment is encrypted with a password of its own (only the attachments are encrypted, /EFF with AuthEvent EFOpen) and
/// cannot be carried over without it: the caller may ask for that password (<see cref="Document.PdfDocument.UnlockAttachments"/>) and try
/// again (decided on 4.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 459).
/// </summary>
/// <param name="message">The localised message.</param>
public sealed class PdfAttachmentsLockedException(string message) : PdfException(message);

/// <summary>
/// Thrown when a document is encrypted for recipients (a public-key handler, ISO 32000-2, 7.6.5) and no certificate at hand — in the user's
/// certificate store or given — opens it: the caller may ask for a certificate file with its private key and try again
/// (docs/FEHLERGEDAECHTNIS.md, No. 1117).
/// </summary>
/// <param name="filter">The name of the handler (/Filter).</param>
public sealed class PdfRecipientException(string filter)
    : PdfException(Localization.Tr.T("Diese Datei ist für bestimmte Empfänger verschlüsselt ({0}); ein passendes Zertifikat mit privatem Schlüssel wurde nicht gefunden.", filter))
{
    /// <summary>The name of the handler (/Filter).</summary>
    public string Filter { get; } = filter;
}

/// <summary>Thrown when a protective limit is exceeded (nesting depth, decoded size, …).</summary>
/// <param name="message">The localised message.</param>
public sealed class PdfLimitException(string message) : PdfException(message);

/// <summary>Limits that protect against damaged or malicious files.</summary>
public static class PdfLimits
{
    /// <summary>Maximum nesting depth of arrays and dictionaries.</summary>
    public const int MaxNesting = 256;

    private static long? _maxDecodedStreamBytes;

    /// <summary>Maximum size of a stream after decoding, in bytes (protection against decompression bombs).</summary>
    /// <remarks>
    /// <see cref="Budget"/> with the ceiling 1 GiB and at least 256 MiB (docs/FEHLERGEDAECHTNIS.md, No. 743); setting it fixes the value (tests).
    /// Exceeding it throws <see cref="PdfLimitException"/>.
    /// </remarks>
    public static long MaxDecodedStreamBytes
    {
        get => _maxDecodedStreamBytes ?? Budget(1L << 30, 256L << 20);
        set => _maxDecodedStreamBytes = value;
    }

    /// <summary>Makes <see cref="MaxDecodedStreamBytes"/> follow <see cref="Budget"/> again after a fixed value (tests).</summary>
    internal static void DynamicStreamLimit() => _maxDecodedStreamBytes = null;

    private static long? _maxDecodedBytesHeld;

    /// <summary>
    /// The most decoded data the program holds in large buffers at once, across all documents and threads: half of what the runtime gives the
    /// program, and at least twice <see cref="MaxDecodedStreamBytes"/> — a stream at its limit is decoded into a buffer that is doubled while
    /// needed, and the parts of /Contents are put together into one more (docs/FEHLERGEDAECHTNIS.md, No. 1003). Setting it fixes the value
    /// (tests). Exceeding it throws <see cref="PdfLimitException"/>, as a single stream above its limit does.
    /// </summary>
    public static long MaxDecodedBytesHeld
    {
        get => _maxDecodedBytesHeld ?? Math.Max(2 * MaxDecodedStreamBytes, Available() / 2);
        set => _maxDecodedBytesHeld = value;
    }

    /// <summary>Makes <see cref="MaxDecodedBytesHeld"/> follow the memory again after a fixed value (tests).</summary>
    internal static void DynamicHeldLimit() => _maxDecodedBytesHeld = null;

    /// <summary>
    /// The memory one large piece of work may take — an image at full size, a decoded stream, a stage of a transformation: a quarter of what the
    /// runtime gives the program (the memory of the machine, or a limit set for the process), between <paramref name="floor"/> and the fixed
    /// <paramref name="ceiling"/> of the purpose (decided on 11.11.2026: dynamic with a fixed ceiling; docs/FEHLERGEDAECHTNIS.md,
    /// No. 743). These are budgets for real, large data; what a crafted file claims is caught before by limits of the format.
    /// </summary>
    /// <param name="ceiling">The most, whatever the machine.</param>
    /// <param name="floor">The least, also on a small machine (not above the ceiling).</param>
    public static long Budget(long ceiling, long floor = 64L << 20) => Budget(ceiling, floor, Available());

    internal static long Budget(long ceiling, long floor, long available) => Math.Clamp(available / 4, Math.Min(floor, ceiling), ceiling);

    // what the runtime gives the program does not change while it runs (the machine's memory, or the limit the process was started with):
    // asked once — the budgets are read in loops, and asking each time made decoding a large stream several times slower. Before the first
    // collection the runtime does not report it: one collection of the youngest generation makes it known (about a millisecond, once). Before,
    // 8 GB were assumed until then — a file opened right after the start got the limits of a large machine, also on a small one or in a process
    // limited to 3.5 GB, which then ran out of memory (docs/FEHLERGEDAECHTNIS.md, No. 1003). 8 GB stay only where the runtime never tells
    private static long _available;

    private static long Available()
    {
        if (Interlocked.Read(ref _available) is > 0 and var known) return known;
        var available = Ask(() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, () => GC.Collect(0, GCCollectionMode.Forced, blocking: true));
        if (available <= 0) return 8L << 30;
        Interlocked.Exchange(ref _available, available);
        return available;
    }

    /// <summary>What the runtime reports; where it reports nothing yet, again after one collection.</summary>
    internal static long Ask(Func<long> report, Action collect)
    {
        var available = report();
        if (available > 0) return available;
        collect();
        return report();
    }

    /// <summary>Maximum length of a chain of /Prev references (incremental updates).</summary>
    public const int MaxXRefSections = 4096;

    /// <summary>Highest object number an appended update may use (ISO 32000-2:2020, Annex C: at most 8,388,607 indirect objects).</summary>
    public const int MaxObjectNumber = 8_388_607;

    /// <summary>Maximum depth of the page tree.</summary>
    public const int MaxPageTreeDepth = 128;

    /// <summary>
    /// Maximum number of objects that depend on each other while being read (the /Length of a stream points
    /// to a stream whose /Length …). Without a limit, a chain of a few thousand objects would be enough for a
    /// stack overflow, which terminates the process.
    /// </summary>
    public const int MaxResolveDepth = 32;

    /// <summary>Maximum number of colour components (DeviceN) and of function inputs and outputs; the buffers
    /// for them are allocated on the stack.</summary>
    public const int MaxComponents = 32;
}
