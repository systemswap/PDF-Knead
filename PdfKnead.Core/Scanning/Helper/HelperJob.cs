using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PdfKnead.Core.Scanning.Helper;

/// <summary>
/// Under Windows, the scan helper belongs to a job object of the program that ends its processes when the job is closed — which Windows
/// does when the program ends, also when it crashes. A helper caught in a driver call (it no longer reads its pipe and so never sees the
/// program go) is then ended all the same. Layout of JOBOBJECT_EXTENDED_LIMIT_INFORMATION and the values from winnt.h (Windows SDK
/// 10.0.26100.0), 64 bit.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class HelperJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    /// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION on 64-bit Windows: the basic limits (64 bytes), the I/O counters (48), four sizes (32).</summary>
    private const int ExtendedLimitSize = 144;

    /// <summary>Where LimitFlags lies: after the two time limits (LARGE_INTEGER each).</summary>
    private const int LimitFlagsOffset = 16;

    // one job for the run of the program: its handle is never closed on purpose — Windows closes it when the program ends
    private static readonly Lazy<nint> Job = new(Create);

    /// <summary>Puts a started helper into the job; false if that failed (the helper then still ends with its pipe).</summary>
    public static bool Assign(nint process) => Job.Value != 0 && AssignProcessToJobObject(Job.Value, process);

    /// <summary>Puts a process into a given job (tests).</summary>
    internal static bool Assign(nint job, nint process) => AssignProcessToJobObject(job, process);

    /// <summary>Closes a job made by <see cref="Create"/> (tests: what Windows does when the program ends).</summary>
    internal static bool Close(nint job) => CloseHandle(job);

    /// <summary>A new job that ends its processes when it is closed; 0 if Windows refused.</summary>
    internal static unsafe nint Create()
    {
        if (!Environment.Is64BitProcess) return 0;
        var job = CreateJobObjectW(0, 0);
        if (job == 0) return 0;
        var info = stackalloc byte[ExtendedLimitSize];
        new Span<byte>(info, ExtendedLimitSize).Clear();
        *(uint*)(info + LimitFlagsOffset) = KillOnJobClose;
        return SetInformationJobObject(job, JobObjectExtendedLimitInformation, info, ExtendedLimitSize) ? job : 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateJobObjectW(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
