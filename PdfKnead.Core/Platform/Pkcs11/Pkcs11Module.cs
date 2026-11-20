using System.Runtime.InteropServices;
using System.Text;

namespace PdfKnead.Core.Platform.Pkcs11;

/// <summary>A PKCS#11 call failed; <see cref="Code"/> is the return value (CKR_…).</summary>
/// <param name="message">What to tell the user (translated).</param>
/// <param name="code">The return value of the module.</param>
public sealed class Pkcs11Exception(string message, ulong code = 0) : Exception(message)
{
    /// <summary>The return value of the module (CKR_…); 0 if the error is not from a call.</summary>
    public ulong Code { get; } = code;

    /// <summary>The PIN was wrong (CKR_PIN_INCORRECT).</summary>
    public bool WrongPin => Code == 0xA0;

    /// <summary>The PIN is locked (CKR_PIN_LOCKED).</summary>
    public bool PinLocked => Code == 0xA4;
}

/// <summary>
/// A PKCS#11 module (OASIS PKCS #11 Cryptographic Token Interface Base Specification v2.40): the driver library of a smart card or token,
/// loaded into the program and called through its function list. Only what signing needs: slots and tokens, certificates and private keys,
/// login, sign. Unix only — there CK_ULONG is <c>unsigned long</c> (pointer-sized) and the structures have natural alignment; under Windows,
/// cards are reached through the certificate store.
/// </summary>
/// <remarks>
/// Loading a module runs its code: only modules from the known system places or a file the user chose. Calls into one module are
/// serialised; the module is initialised once (with „the library may use OS locking“) and stays loaded for the session.
/// </remarks>
internal sealed unsafe class Pkcs11Module
{
    // function list indexes (PKCS #11 v2.40, 5.x: order of CK_FUNCTION_LIST)
    private const int CInitialize = 0, CGetSlotList = 4, CGetTokenInfo = 6, COpenSession = 12, CCloseSession = 13, CLogin = 18, CLogout = 19,
        CGetAttributeValue = 24, CFindObjectsInit = 26, CFindObjects = 27, CFindObjectsFinal = 28, CSignInit = 42, CSign = 43;

    internal const ulong CkaClass = 0x0, CkaLabel = 0x3, CkaValue = 0x11, CkaCertificateType = 0x80, CkaKeyType = 0x100, CkaId = 0x102,
        CkaAlwaysAuthenticate = 0x202;
    internal const ulong CkoCertificate = 1, CkoPrivateKey = 3, CkcX509 = 0, CkkRsa = 0, CkkEc = 3;
    internal const ulong CkmRsaPkcs = 0x1, CkmEcdsa = 0x1041;
    // CKM_RSA_PKCS_PSS, CKM_SHA256/384/512 (values of pkcs11t.h); CKG_MGF1_SHA256/384/512 (PKCS #11 3.1, 2.1.7)
    internal const ulong CkmRsaPkcsPss = 0xD, CkmSha256 = 0x250, CkmSha384 = 0x260, CkmSha512 = 0x270, CkgMgf1Sha256 = 0x2, CkgMgf1Sha384 = 0x3,
        CkgMgf1Sha512 = 0x4;
    internal const ulong CkrMechanismInvalid = 0x70;

    /// <summary>CK_RSA_PKCS_PSS_PARAMS: the hash, the mask generation function, the salt length (PKCS #11 3.1, 2.1.11).</summary>
    internal readonly record struct PssParameters(ulong Hash, ulong Mgf, ulong SaltLength)
    {
        /// <summary>For a hash: MGF1 with it, salt as long as it.</summary>
        public static PssParameters For(System.Security.Cryptography.HashAlgorithmName hash) =>
            hash == System.Security.Cryptography.HashAlgorithmName.SHA512 ? new(CkmSha512, CkgMgf1Sha512, 64)
            : hash == System.Security.Cryptography.HashAlgorithmName.SHA384 ? new(CkmSha384, CkgMgf1Sha384, 48)
            : hash == System.Security.Cryptography.HashAlgorithmName.SHA256 ? new(CkmSha256, CkgMgf1Sha256, 32)
            : throw new System.Security.Cryptography.CryptographicException(Tr.T("Dieses Signaturverfahren unterstützt die Karte hier nicht."));
    }
    internal const ulong CkuUser = 1, CkuContextSpecific = 2;
    private const ulong CkfSerialSession = 0x4, CkfOsLockingOk = 0x2;
    internal const ulong CkfLoginRequired = 0x4, CkfProtectedAuthenticationPath = 0x100, CkfTokenInitialized = 0x400, CkfUserPinLocked = 0x40000;
    private const ulong CkrOk = 0, CkrCryptokiAlreadyInitialized = 0x191, CkrUserAlreadyLoggedIn = 0x100, CkrBufferTooSmall = 0x150;
    private const ulong Unavailable = ulong.MaxValue;
    private const int MaxObjects = 256, MaxAttribute = 1 << 20;

    private static readonly Dictionary<string, Pkcs11Module> Loaded = new(StringComparer.Ordinal);
    private static readonly Lock LoadGate = new();

    private readonly nint _functions;
    private readonly int _first;

    /// <summary>Serialises the calls into this module.</summary>
    internal Lock Gate { get; } = new();

    private Pkcs11Module(string path, nint functions)
    {
        Path = path;
        _functions = functions;
        // CK_VERSION (two bytes) comes first; the function pointers follow at the next pointer boundary
        _first = IntPtr.Size;
    }

    /// <summary>The library file.</summary>
    public string Path { get; }

    /// <summary>Loads and initialises a module once per session.</summary>
    /// <param name="path">The library file.</param>
    /// <returns>The module.</returns>
    /// <exception cref="Pkcs11Exception">It cannot be loaded or is no PKCS#11 module.</exception>
    public static Pkcs11Module Open(string path)
    {
        if (OperatingSystem.IsWindows()) throw new Pkcs11Exception(Tr.T("Signaturkarten werden unter Windows über den Zertifikatsspeicher angesprochen."));
        lock (LoadGate)
        {
            if (Loaded.TryGetValue(path, out var known)) return known;
            if (!NativeLibrary.TryLoad(path, out var library)) throw new Pkcs11Exception(Tr.T("Der Kartentreiber lässt sich nicht laden: {0}", path));
            if (!NativeLibrary.TryGetExport(library, "C_GetFunctionList", out var export))
                throw new Pkcs11Exception(Tr.T("Die Datei ist kein Kartentreiber (PKCS#11): {0}", path));
            nint list = 0;
            var rv = ((delegate* unmanaged<nint*, nuint>)export)(&list);
            if (rv != CkrOk || list == 0) throw new Pkcs11Exception(Tr.T("Der Kartentreiber antwortet nicht ({0}).", Hex(rv)), rv);
            var module = new Pkcs11Module(path, list);
            try
            {
                module.Initialize();
            }
            catch (Pkcs11Exception e)
            {
                // the most common cause: SoftHSM (a token in software for tests) cannot read its configuration or token folder, which only
                // the group „softhsm“ may
                throw new Pkcs11Exception(Tr.T("Der Kartentreiber lässt sich nicht starten ({0}).", Hex((nuint)e.Code))
                    + (path.Contains("softhsm", StringComparison.OrdinalIgnoreCase)
                        ? " " + Tr.T("SoftHSM ist ein Testwerkzeug, keine Karte; es braucht Zugriff auf seinen Token-Ordner (Gruppe „softhsm“).")
                        : ""), e.Code);
            }
            Loaded[path] = module;
            return module;
        }
    }

    private nint Function(int index) => Marshal.ReadIntPtr(_functions, _first + index * IntPtr.Size);

    private static string Hex(nuint rv) => "0x" + ((ulong)rv).ToString("X");

    private static void Check(nuint rv, string what)
    {
        if (rv == CkrOk) return;
        var code = (ulong)rv;
        throw code switch
        {
            0xA0 => new Pkcs11Exception(Tr.T("Die PIN ist falsch."), code),
            0xA4 => new Pkcs11Exception(Tr.T("Die PIN ist gesperrt."), code),
            0xE0 => new Pkcs11Exception(Tr.T("Keine Karte im Leser."), code),
            0xB3 => new Pkcs11Exception(Tr.T("Die Karte wurde entfernt."), code),
            0x101 => new Pkcs11Exception(Tr.T("Die Karte verlangt eine Anmeldung."), code),
            _ => new Pkcs11Exception(Tr.T("Die Karte meldet einen Fehler bei {0} ({1}).", what, Hex(rv)), code),
        };
    }

    /// <summary>C_Initialize with CK_C_INITIALIZE_ARGS { no mutex callbacks, CKF_OS_LOCKING_OK }.</summary>
    private void Initialize()
    {
        var args = stackalloc nint[6];
        for (var i = 0; i < 6; i++) args[i] = 0;
        args[4] = (nint)CkfOsLockingOk;
        var rv = ((delegate* unmanaged<nint*, nuint>)Function(CInitialize))(args);
        if ((ulong)rv != CkrCryptokiAlreadyInitialized) Check(rv, "C_Initialize");
    }

    /// <summary>The slots (readers) with a token (card) in them; with <paramref name="withToken"/> false all slots.</summary>
    public List<ulong> Slots(bool withToken = true)
    {
        lock (Gate)
        {
            var get = (delegate* unmanaged<byte, nuint*, nuint*, nuint>)Function(CGetSlotList);
            var present = (byte)(withToken ? 1 : 0);
            nuint count = 0;
            Check(get(present, null, &count), "C_GetSlotList");
            if (count == 0) return [];
            var slots = new nuint[Math.Min((int)count, 64)];
            count = (nuint)slots.Length;
            fixed (nuint* p = slots) Check(get(present, p, &count), "C_GetSlotList");
            return [.. slots.Take((int)count).Select(s => (ulong)s)];
        }
    }

    /// <summary>Label, serial number and flags of the token in a slot (CK_TOKEN_INFO).</summary>
    public (string Label, string Serial, ulong Flags) TokenInfo(ulong slot)
    {
        lock (Gate)
        {
            // CK_TOKEN_INFO: label[32], manufacturerID[32], model[16], serialNumber[16], flags (CK_ULONG), …; 512 bytes are more than enough
            var info = stackalloc byte[512];
            Check(((delegate* unmanaged<nuint, byte*, nuint>)Function(CGetTokenInfo))((nuint)slot, info), "C_GetTokenInfo");
            var label = Encoding.UTF8.GetString(info, 32).TrimEnd(' ', '\0');
            var serial = Encoding.ASCII.GetString(info + 80, 16).TrimEnd(' ', '\0');
            var flags = (ulong)*(nuint*)(info + 96);
            return (label, serial, flags);
        }
    }

    /// <summary>Opens a read-only session on a slot.</summary>
    public ulong OpenSession(ulong slot)
    {
        lock (Gate)
        {
            nuint session = 0;
            Check(((delegate* unmanaged<nuint, nuint, nint, nint, nuint*, nuint>)Function(COpenSession))((nuint)slot, (nuint)CkfSerialSession, 0, 0, &session),
                "C_OpenSession");
            return session;
        }
    }

    /// <summary>Closes a session (errors ignored: the card may be gone).</summary>
    public void CloseSession(ulong session)
    {
        lock (Gate) ((delegate* unmanaged<nuint, nuint>)Function(CCloseSession))((nuint)session);
    }

    /// <summary>Logs in as user or, for a key that asks for it with every use, context-specifically; null PIN for a PIN pad of the reader.</summary>
    public void Login(ulong session, string? pin, ulong user = CkuUser)
    {
        lock (Gate)
        {
            var bytes = pin is null ? null : Encoding.UTF8.GetBytes(pin);
            try
            {
                fixed (byte* p = bytes)
                {
                    var rv = ((delegate* unmanaged<nuint, nuint, byte*, nuint, nuint>)Function(CLogin))((nuint)session, (nuint)user, p, (nuint)(bytes?.Length ?? 0));
                    if ((ulong)rv != CkrUserAlreadyLoggedIn) Check(rv, "C_Login");
                }
            }
            finally
            {
                if (bytes is not null) Array.Clear(bytes);
            }
        }
    }

    /// <summary>Logs out (errors ignored).</summary>
    public void Logout(ulong session)
    {
        lock (Gate) ((delegate* unmanaged<nuint, nuint>)Function(CLogout))((nuint)session);
    }

    /// <summary>The objects matching a template of whole-number and byte attributes.</summary>
    public List<ulong> Find(ulong session, params (ulong Type, object Value)[] template)
    {
        lock (Gate)
        {
            var handles = new List<GCHandle>();
            var attributes = new nint[template.Length * 3];
            try
            {
                for (var i = 0; i < template.Length; i++)
                {
                    var data = template[i].Value switch
                    {
                        ulong number => IntPtr.Size == 8 ? BitConverter.GetBytes(number) : BitConverter.GetBytes((uint)number),
                        byte[] raw => raw,
                        _ => throw new ArgumentException("ulong or byte[]"),
                    };
                    var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
                    handles.Add(handle);
                    attributes[i * 3] = (nint)template[i].Type;
                    attributes[i * 3 + 1] = handle.AddrOfPinnedObject();
                    attributes[i * 3 + 2] = data.Length;
                }
                fixed (nint* t = attributes)
                    Check(((delegate* unmanaged<nuint, nint*, nuint, nuint>)Function(CFindObjectsInit))((nuint)session, t, (nuint)template.Length), "C_FindObjectsInit");
            }
            finally
            {
                foreach (var handle in handles) handle.Free();
            }
            var found = new List<ulong>();
            try
            {
                var batch = stackalloc nuint[32];
                while (found.Count < MaxObjects)
                {
                    nuint count = 0;
                    Check(((delegate* unmanaged<nuint, nuint*, nuint, nuint*, nuint>)Function(CFindObjects))((nuint)session, batch, 32, &count), "C_FindObjects");
                    if (count == 0) break;
                    for (var i = 0; i < (int)count; i++) found.Add(batch[i]);
                }
            }
            finally
            {
                ((delegate* unmanaged<nuint, nuint>)Function(CFindObjectsFinal))((nuint)session);
            }
            return found;
        }
    }

    /// <summary>The value of one attribute of an object; null if it has none or it is not readable.</summary>
    public byte[]? Attribute(ulong session, ulong handle, ulong type)
    {
        lock (Gate)
        {
            var get = (delegate* unmanaged<nuint, nuint, nint*, nuint, nuint>)Function(CGetAttributeValue);
            var attribute = stackalloc nint[3];
            attribute[0] = (nint)type;
            attribute[1] = 0;
            attribute[2] = 0;
            if (get((nuint)session, (nuint)handle, attribute, 1) != CkrOk || (ulong)attribute[2] == Unavailable || attribute[2] > MaxAttribute) return null;
            var value = new byte[attribute[2]];
            fixed (byte* p = value)
            {
                attribute[1] = (nint)p;
                if (get((nuint)session, (nuint)handle, attribute, 1) != CkrOk) return null;
            }
            return value;
        }
    }

    /// <summary>A whole-number attribute (CK_ULONG); null if missing.</summary>
    public ulong? Number(ulong session, ulong handle, ulong type) =>
        Attribute(session, handle, type) is { Length: 8 } eight ? BitConverter.ToUInt64(eight) : null;

    /// <summary>Signs data with a private key and a mechanism without parameters (CKM_RSA_PKCS with a DigestInfo, CKM_ECDSA with a digest).</summary>
    /// <param name="session">The session (logged in).</param>
    /// <param name="key">The private key.</param>
    /// <param name="mechanism">The mechanism.</param>
    /// <param name="data">What is signed.</param>
    /// <param name="contextPin">For a key that wants the PIN for every use (CKA_ALWAYS_AUTHENTICATE): the PIN; otherwise null.</param>
    /// <param name="alwaysAuthenticate">Whether the key wants that.</param>
    /// <returns>The signature (RSA: the block; ECDSA: r‖s).</returns>
    /// <param name="pss">The parameters of CKM_RSA_PKCS_PSS; null for a mechanism without.</param>
    public byte[] Sign(ulong session, ulong key, ulong mechanism, byte[] data, bool alwaysAuthenticate, string? contextPin, PssParameters? pss = null)
    {
        lock (Gate)
        {
            // CK_RSA_PKCS_PSS_PARAMS: three CK_ULONG (pointer-sized on Unix)
            var pssValues = stackalloc nuint[3];
            if (pss is { } p)
            {
                pssValues[0] = (nuint)p.Hash;
                pssValues[1] = (nuint)p.Mgf;
                pssValues[2] = (nuint)p.SaltLength;
            }
            var parameters = stackalloc nint[3];
            parameters[0] = (nint)mechanism;
            parameters[1] = pss is null ? 0 : (nint)pssValues;
            parameters[2] = pss is null ? 0 : 3 * sizeof(nuint);
            Check(((delegate* unmanaged<nuint, nint*, nuint, nuint>)Function(CSignInit))((nuint)session, parameters, (nuint)key), "C_SignInit");
        }
        if (alwaysAuthenticate) Login(session, contextPin, CkuContextSpecific);
        lock (Gate)
        {
            var sign = (delegate* unmanaged<nuint, byte*, nuint, byte*, nuint*, nuint>)Function(CSign);
            fixed (byte* d = data)
            {
                nuint length = 0;
                Check(sign((nuint)session, d, (nuint)data.Length, null, &length), "C_Sign");
                if (length == 0 || length > 4096) throw new Pkcs11Exception(Tr.T("Die Karte liefert keine Signatur."));
                var signature = new byte[length];
                fixed (byte* s = signature)
                {
                    var rv = sign((nuint)session, d, (nuint)data.Length, s, &length);
                    if ((ulong)rv == CkrBufferTooSmall) throw new Pkcs11Exception(Tr.T("Die Karte liefert keine Signatur."), (ulong)rv);
                    Check(rv, "C_Sign");
                }
                return signature.AsSpan(0, (int)length).ToArray();
            }
        }
    }
}
