using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PdfKnead.Core.Platform;

/// <summary>
/// A file with a secret of the user (a private key): on Windows bound to the logged-on user with DPAPI, otherwise created with
/// permissions for the user only. Shared by the device identity and the signing certificate, so both are protected the same way.
/// </summary>
internal static partial class UserSecretFile
{
    /// <summary>Reads and unprotects the file.</summary>
    /// <param name="file">Path of the file.</param>
    /// <param name="entropy">Additional entropy for DPAPI; must be the same as when saving.</param>
    /// <returns>The secret, or null if the file does not exist.</returns>
    /// <exception cref="IOException">The file cannot be read at the moment.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    /// <exception cref="CryptographicException">The file cannot be unprotected (other user, damaged).</exception>
    public static byte[]? Read(string file, byte[] entropy) => File.Exists(file) ? Unprotect(File.ReadAllBytes(file), entropy) : null;

    /// <summary>Protects and saves the secret; if that fails, nothing is thrown (the secret then applies until the program ends).</summary>
    /// <param name="file">Path of the file.</param>
    /// <param name="secret">The secret.</param>
    /// <param name="entropy">Additional entropy for DPAPI.</param>
    /// <returns><see langword="true"/> if saved.</returns>
    public static bool TrySave(string file, byte[] secret, byte[] entropy)
    {
        // Written to a new file first (on Linux created with permissions for the user only) and then moved over the old one: an
        // interrupted save leaves no half-written secret, and an existing file with wider permissions is replaced, not reused
        var temp = $"{file}.neu-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var data = Protector(secret, entropy);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, file, overwrite: true);
            return true;
        }
        // DPAPI can fail too (user profile not loaded, for example under a service account): then the key is kept for this
        // session instead of ending the start of the program
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
            }
            return false;
        }
    }

    /// <summary>Protects the secret before saving; replaceable only by tests (to simulate a failing DPAPI).</summary>
    internal static Func<byte[], byte[], byte[]> Protector { get; set; } = Protect;

    /// <summary>Moves an unreadable file aside: “….unlesbar-date-time”, with a counter if that name exists; never overwrites.</summary>
    /// <param name="file">Path of the file.</param>
    /// <returns><see langword="true"/> if moved.</returns>
    public static bool TryBackUp(string file)
    {
        var stem = $"{file}.unlesbar-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (var n = 1; n < 100; n++)
        {
            var target = n == 1 ? stem : $"{stem}-{n}";
            if (File.Exists(target)) continue;
            try
            {
                File.Move(file, target, overwrite: false);
                // a key set aside stays readable for the user only, whatever the old file allowed
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return false;
    }

    private static byte[] Protect(byte[] data, byte[] entropy) => OperatingSystem.IsWindows() ? Dpapi(data, entropy, protect: true) : data;

    private static byte[] Unprotect(byte[] data, byte[] entropy) => OperatingSystem.IsWindows() ? Dpapi(data, entropy, protect: false) : data;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int UiForbidden = 0x1;

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    /// <summary>Binds data to the logged-on Windows user (or releases it again).</summary>
    private static unsafe byte[] Dpapi(byte[] data, byte[] entropy, bool protect)
    {
        fixed (byte* input = data)
        fixed (byte* extra = entropy)
        {
            var inBlob = new DataBlob { Size = data.Length, Data = (IntPtr)input };
            var entropyBlob = new DataBlob { Size = entropy.Length, Data = (IntPtr)extra };
            var ok = protect
                ? CryptProtectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob);
            if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
    }
}
