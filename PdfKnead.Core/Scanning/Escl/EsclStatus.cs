using System.Xml.Linq;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>The overall state of an eSCL scanner.</summary>
public enum EsclScannerState
{
    /// <summary>Not said or not understood.</summary>
    Unknown,
    /// <summary>Ready.</summary>
    Idle,
    /// <summary>Busy with a job.</summary>
    Processing,
    /// <summary>Calibrating or warming up.</summary>
    Testing,
    /// <summary>Stopped by an error.</summary>
    Stopped,
    /// <summary>Not available.</summary>
    Down,
}

/// <summary>The state of the feeder.</summary>
public enum EsclFeederState
{
    /// <summary>The device names no feeder state (no feeder, or not reported).</summary>
    None,
    /// <summary>Named, but not understood — never blocks a scan.</summary>
    Unknown,
    /// <summary>Feeding — the normal state during a job.</summary>
    Processing,
    /// <summary>Paper is loaded.</summary>
    Loaded,
    /// <summary>No paper.</summary>
    Empty,
    /// <summary>Paper jam.</summary>
    Jam,
    /// <summary>A sheet could not be drawn in.</summary>
    Mispick,
    /// <summary>The cover is open.</summary>
    HatchOpen,
    /// <summary>A sheet is too short for both sides.</summary>
    DuplexPageTooShort,
    /// <summary>A sheet is too long for both sides.</summary>
    DuplexPageTooLong,
    /// <summary>Several sheets were drawn in at once.</summary>
    MultipickDetected,
    /// <summary>The input tray failed.</summary>
    InputTrayFailed,
    /// <summary>More sheets than the tray holds.</summary>
    InputTrayOverloaded,
}

/// <summary>The state of a scan job.</summary>
public enum EsclJobState
{
    /// <summary>Not said or not understood.</summary>
    Unknown,
    /// <summary>Created, the device prepares.</summary>
    Pending,
    /// <summary>Scanning or handing out pages.</summary>
    Processing,
    /// <summary>Finished.</summary>
    Completed,
    /// <summary>Cancelled by the program or at the device.</summary>
    Canceled,
    /// <summary>Ended by an error.</summary>
    Aborted,
}

/// <summary>One job as the device lists it.</summary>
/// <param name="Uri">The job's address as the device names it.</param>
/// <param name="Uuid">The job's UUID.</param>
/// <param name="Age">Seconds since the entry last changed.</param>
/// <param name="ImagesCompleted">Pages scanned so far.</param>
/// <param name="ImagesToTransfer">Pages scanned but not yet fetched.</param>
/// <param name="State">The state.</param>
/// <param name="Reasons">The reasons the device gives (e.g. „JobCanceledByUser“).</param>
public sealed record EsclJobInfo(string? Uri, string? Uuid, int? Age, int? ImagesCompleted, int? ImagesToTransfer, EsclJobState State, IReadOnlyList<string> Reasons)
{
    /// <summary>Whether this entry is the job at <paramref name="location"/> (the path the device returned on creation).</summary>
    /// <remarks>Compared by the last path segment: devices name the job with or without their resource path, or as a full address.</remarks>
    public bool Is(string location)
    {
        var mine = LastSegment(Uri);
        var other = LastSegment(location);
        return other.Length > 0 && (string.Equals(mine, other, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Uuid, other, StringComparison.OrdinalIgnoreCase));
    }

    private static string LastSegment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var path = System.Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.AbsolutePath : text.Trim();
        return path.TrimEnd('/').Split('/')[^1];
    }
}

/// <summary>The state of an eSCL scanner as read from its answer (KONZEPT.md §5.25, S9b). Values not understood are named in <see cref="Unknown"/>.</summary>
/// <param name="State">The overall state.</param>
/// <param name="Feeder">The state of the feeder.</param>
/// <param name="Reasons">The reasons the device gives for its state.</param>
/// <param name="Jobs">The jobs the device lists, as listed.</param>
/// <param name="Unknown">Values that were not understood.</param>
public sealed record EsclStatus(EsclScannerState State, EsclFeederState Feeder, IReadOnlyList<string> Reasons, IReadOnlyList<EsclJobInfo> Jobs,
    IReadOnlyList<string> Unknown)
{
    /// <summary>The largest answer accepted (256 KB).</summary>
    public const int MaxBytes = 256 << 10;

    private const int MaxJobs = 64, MaxReasons = 32;

    /// <summary>The entry of the job at <paramref name="location"/>, or null.</summary>
    public EsclJobInfo? Job(string location) => Jobs.FirstOrDefault(j => j.Is(location));

    /// <summary>Reads the state from a device's answer.</summary>
    /// <returns>The state, or null if the answer is too large, not XML, or not a status document.</returns>
    public static EsclStatus? Read(ReadOnlySpan<byte> data) => EsclXml.Parse(data, MaxBytes) is { } root ? Read(root) : null;

    /// <summary>Reads the state from the root element.</summary>
    public static EsclStatus? Read(XElement root)
    {
        if (!root.Is("ScannerStatus")) return null;
        var unknown = new List<string>();
        var state = root.Text("State") is { } s ? Parse<EsclScannerState>(s, "", unknown, "Zustand") : EsclScannerState.Unknown;
        var feeder = root.Text("AdfState") is { } a ? Parse(a, "ScannerAdf", unknown, "Einzug", EsclFeederState.Unknown) : EsclFeederState.None;
        var jobs = new List<EsclJobInfo>();
        foreach (var j in root.Child("Jobs").Children("JobInfo").Take(MaxJobs))
            jobs.Add(new EsclJobInfo(Short(j.Text("JobUri")), Short(j.Text("JobUuid")), NonNegative(j.Int("Age")), NonNegative(j.Int("ImagesCompleted")),
                NonNegative(j.Int("ImagesToTransfer")),
                j.Text("JobState") is { } js ? Parse<EsclJobState>(js, "", unknown, "Auftrag") : EsclJobState.Unknown,
                ReadReasons(j.Child("JobStateReasons"), "JobStateReason")));
        return new EsclStatus(state, feeder, ReadReasons(root.Child("StateReasons"), "StateReason"), jobs, unknown);
    }

    private static IReadOnlyList<string> ReadReasons(XElement? list, string name) =>
        [.. list.Children(name).Select(r => r.Value.Trim()).Where(r => r.Length is > 0 and <= 128).Take(MaxReasons)];

    private static int? NonNegative(int? value) => value is >= 0 ? value : null;

    private static string? Short(string? text) => text is null ? null : text.Length <= 512 ? text : text[..512];

    private static T Parse<T>(string text, string prefix, List<string> unknown, string what, T fallback = default) where T : struct, Enum
    {
        var name = prefix.Length > 0 && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..] : text;
        // single names only — a number or a list („Loaded,Empty“) would otherwise be taken as a value of the enumeration
        if (name.Length > 0 && name.All(char.IsAsciiLetter) && Enum.TryParse<T>(name, ignoreCase: true, out var value) && Enum.IsDefined(value)
            && !value.Equals(default(T)) && !(typeof(T) == typeof(EsclFeederState) && name.Equals("None", StringComparison.OrdinalIgnoreCase)))
            return value;
        unknown.Add($"{what} „{Short(text)}“");
        return fallback;
    }
}
