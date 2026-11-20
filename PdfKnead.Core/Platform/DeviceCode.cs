using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PdfKnead.Core.Platform;

/// <summary>
/// Two-part code for signatures, “7F3A9C-12C4D7A8B9”: first the PC (6 characters), then the account
/// (10 characters). Neither the computer name nor the user name appears in it. The PC part can only be traced back with the device ID;
/// on Windows in a domain, the user part can be found by trial (domain SID + sequential account number, the key is embedded in the
/// program). This is deliberate, so that the same account looks the same on every PC (File → Info explains this):
/// <list type="bullet">
/// <item>PC part: hash (HMAC-SHA256) of the random device ID of the operating system (Windows
/// <c>MachineGuid</c>, Linux <c>/etc/machine-id</c>); the same for all users of the PC, and unchanged when the program
/// is reinstalled.</item>
/// <item>User part: hash of the account. On Windows the SID, which for organisational accounts (domain, Entra) is
/// the same on all PCs, so the same account can be recognised on another PC; local accounts have their own SID
/// on each PC anyway. On Linux the uid, keyed with the device ID (uid 1000 exists everywhere).</item>
/// </list>
/// The same code with different names means that the same account signed for someone else.
/// </summary>
/// <param name="Pc">PC part: 6 hexadecimal characters.</param>
/// <param name="User">Account part: 10 hexadecimal characters.</param>
public sealed record DeviceCode(string Pc, string User)
{
    private static readonly byte[] PcKey = "PDF-Knead PC-Kennung 1"u8.ToArray();
    private static readonly byte[] UserKey = "PDF-Knead Konto-Kennung 1"u8.ToArray();
    private static DeviceCode? _current;

    /// <summary>Returns the code in the form “PC-account”, e.g. “7F3A9C-12C4D7A8B9”.</summary>
    /// <returns>The two parts joined with a hyphen.</returns>
    public override string ToString() => $"{Pc}-{User}";

    /// <summary>Gets the code of this user on this PC (computed once).</summary>
    /// <remarks>Reads the device ID from the registry or <c>/etc/machine-id</c> on first access; may create a fallback ID file in the user data folder.</remarks>
    public static DeviceCode Current => _current ??= Compute(MachineId(), UserId(), userIsGlobal: OperatingSystem.IsWindows());

    /// <summary>
    /// Computes the code from the device ID and the account (HMAC-SHA256: 3 bytes for the PC, 5 for the account, hexadecimal).
    /// </summary>
    /// <param name="machineId">Device ID of the operating system; trimmed and lower-cased before hashing.</param>
    /// <param name="userId">Account identifier (Windows SID or “uid:…”).</param>
    /// <param name="userIsGlobal">The account identifier is unique across PCs (Windows SID); otherwise it is keyed with the device ID.</param>
    /// <returns>The code; the same inputs always give the same code.</returns>
    public static DeviceCode Compute(string machineId, string userId, bool userIsGlobal)
    {
        var machine = Encoding.UTF8.GetBytes(machineId.Trim().ToLowerInvariant());
        var pc = HMACSHA256.HashData(PcKey, machine);
        var user = HMACSHA256.HashData(userIsGlobal ? UserKey : machine, Encoding.UTF8.GetBytes("PDF-Knead Benutzer-Kennung 1\n" + userId));
        return new DeviceCode(Convert.ToHexString(pc, 0, 3), Convert.ToHexString(user, 0, 5));
    }

    /// <summary>
    /// Device ID of the operating system. If it is missing (rare, e.g. macOS), a random one stored in the user
    /// folder is used; the PC part is then only the same per user.
    /// </summary>
    private static string MachineId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                if (key?.GetValue("MachineGuid") is string guid && guid.Length > 0) return guid;
            }
            else
            {
                foreach (var file in (ReadOnlySpan<string>)["/etc/machine-id", "/var/lib/dbus/machine-id"])
                    if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } id) return id;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return FallbackId();
    }

    private static string FallbackId()
    {
        var file = Path.Combine(AppLocations.UserDataFolder, "geraet.id");
        try
        {
            if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } stored) return stored;
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, id);
            return id;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "unbekannt";
        }
    }

    /// <summary>Account identifier: the Windows SID (unchanged when the account is renamed), otherwise the uid.</summary>
    private static string UserId()
    {
        if (OperatingSystem.IsWindows())
            return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        try
        {
            return "uid:" + UnixNative.GetUid();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return Environment.UserName;
        }
    }

}

internal static partial class UnixNative
{
    [LibraryImport("libc", EntryPoint = "getuid")]
    internal static partial uint GetUid();

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    internal static partial int FSync(int descriptor);

    [LibraryImport("libc", EntryPoint = "close")]
    internal static partial int Close(int descriptor);

    /// <summary>
    /// Linux: writes the folder entry to disk, so that a rename into it survives a power cut (the data of the file itself is flushed before).
    /// Does nothing elsewhere or if the folder cannot be opened — saving does not fail because of it.
    /// </summary>
    /// <param name="folder">The folder.</param>
    internal static void FlushFolder(string folder)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            var descriptor = Open(folder, 0);   // O_RDONLY
            if (descriptor < 0) return;
            try
            {
                FSync(descriptor);
            }
            finally
            {
                Close(descriptor);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }
}
