using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace PdfKnead.Core.Scanning.Wia;

// WIA 2.0 as COM interfaces generated at build time (Native AOT has no built-in COM interop). GUIDs and the order of the methods are taken
// from wia_lh.h of the Windows SDK 10.0.26100.0 (the order of the vtable decides which method is called); constants from WiaDef.h, Sti.h,
// winerror.h and objidl.h of the same SDK. Methods after the last one used are left out (they would only extend the vtable); unused ones
// before it are declared with plain pointers to keep the slots. Every method keeps its HRESULT ([PreserveSig]): errors are mapped by
// WiaErrors, never thrown by the marshaller. Only 64-bit processes (the layouts of PROPVARIANT, PROPSPEC and STATSTG below are those of
// 64-bit Windows; the program is built for win-x64 only).

/// <summary>Constants of WIA 2.0 (Windows SDK 10.0.26100.0: WiaDef.h, Sti.h, winerror.h, objidl.h).</summary>
internal static class Wia
{
    // ---- device information properties (WiaDef.h)
    public const int DipDevId = 2, DipVendDesc = 3, DipDevDesc = 4, DipDevType = 5, DipPortName = 6, DipDevName = 7, DipServerName = 8,
        DipWiaVersion = 14, DipPnpId = 16;

    // ---- device and item properties (WiaDef.h)
    public const int DpsHorizontalBedSize = 3074, DpsVerticalBedSize = 3075, DpsHorizontalSheetFeedSize = 3076, DpsVerticalSheetFeedSize = 3077,
        DpsDocumentHandlingCapabilities = 3086, DpsDocumentHandlingStatus = 3087, IpsDocumentHandlingSelect = 3088, IpsOpticalXres = 3090,
        IpsOpticalYres = 3091, IpsPages = 3096, IpsPageSize = 3097, IpsPageWidth = 3098, IpsPageHeight = 3099, IpsPreview = 3100;

    public const int IpaItemName = 4098, IpaFullItemName = 4099, IpaItemFlags = 4101, IpaDatatype = 4103, IpaDepth = 4104,
        IpaPreferredFormat = 4105, IpaFormat = 4106, IpaCompression = 4107, IpaTymed = 4108, IpaChannelsPerPixel = 4109, IpaBitsPerChannel = 4110,
        IpaPixelsPerLine = 4112, IpaBytesPerLine = 4113, IpaNumberOfLines = 4114, IpaBufferSize = 4118, IpaItemCategory = 4125;

    public const int IpsXres = 6147, IpsYres = 6148, IpsXpos = 6149, IpsYpos = 6150, IpsXextent = 6151, IpsYextent = 6152,
        IpsMaxHorizontalSize = 6165, IpsMaxVerticalSize = 6166, IpsMinHorizontalSize = 6167, IpsMinVerticalSize = 6168;

    // ---- property attributes (WiaDef.h)
    public const int PropWrite = 0x02, PropRange = 0x10, PropList = 0x20, PropFlag = 0x40;
    public const int RangeMin = 0, RangeNom = 1, RangeMax = 2, RangeStep = 3;
    public const int ListCount = 0, ListValues = 2;
    public const int FlagValues = 1;

    // ---- values (WiaDef.h)
    public const int DataThreshold = 0, DataDither = 1, DataGrayscale = 2, DataColor = 3;
    /// <summary>Document handling select and capabilities: feeder, flatbed, both sides (FEEDER/FLATBED/DUPLEX, FEED/FLAT/DUP), front only.</summary>
    public const int Feeder = 0x001, Flatbed = 0x002, Duplex = 0x004, FrontOnly = 0x020;
    /// <summary>Where the feeder aligns narrower paper (WIA_IPS_SHEET_FEEDER_REGISTRATION: LEFT_JUSTIFIED, CENTERED, RIGHT_JUSTIFIED).</summary>
    public const int IpsSheetFeederRegistration = 3078, LeftJustified = 0, Centered = 1, RightJustified = 2;
    /// <summary>Document handling status: feeder ready, paper jam (FEED_READY, PAPER_JAM; DEVICE_ATTENTION 0x400 is ignored, see WiaErrors).</summary>
    public const int FeedReady = 0x01, PaperJam = 0x20;
    public const int AllPages = 0;
    public const int TymedFile = 2;
    public const int TransferMsgStatus = 1, TransferMsgEndOfStream = 2, TransferMsgEndOfTransfer = 3, TransferMsgDeviceStatus = 5, TransferMsgNewPage = 6;
    public const int DevinfoEnumLocal = 0x10;
    public const int ItemTypeFolder = 0x04, ItemTypeTransfer = 0x2000;

    /// <summary>The high word of WIA_DIP_DEV_TYPE (GET_STIDEVICE_TYPE, Sti.h): 1 scanner, 2 camera, 3 video.</summary>
    public const int StiScanner = 1, StiCamera = 2, StiVideo = 3;

    // ---- categories of items (WiaDef.h)
    public static readonly Guid CategoryFlatbed = new(0xfb607b1f, 0x43f3, 0x488b, 0x85, 0x5b, 0xfb, 0x70, 0x3e, 0xc3, 0x42, 0xa6);
    public static readonly Guid CategoryFeeder = new(0xfe131934, 0xf84c, 0x42ad, 0x8d, 0xa4, 0x61, 0x29, 0xcd, 0xdd, 0x72, 0x88);
    public static readonly Guid CategoryFeederFront = new(0x4823175c, 0x3b28, 0x487b, 0xa7, 0xe6, 0xee, 0xbc, 0x17, 0x61, 0x4f, 0xd1);
    public static readonly Guid CategoryFeederBack = new(0x61ca74d4, 0x39db, 0x42aa, 0x89, 0xb1, 0x8c, 0x19, 0xc9, 0xcd, 0x4c, 0x23);
    public static readonly Guid CategoryFilm = new(0xfcf65be7, 0x3ce3, 0x4473, 0xaf, 0x85, 0xf5, 0xd3, 0x7d, 0x21, 0xb6, 0x8a);
    public static readonly Guid CategoryAuto = new(0xdefe5fd8, 0x6c97, 0x4dde, 0xb1, 0x1e, 0xcb, 0x50, 0x9b, 0x27, 0x0e, 0x11);
    public static readonly Guid CategoryRoot = new(0xf193526f, 0x59b8, 0x4a26, 0x98, 0x88, 0xe1, 0x6e, 0x4f, 0x97, 0xce, 0x10);

    /// <summary>The image format BMP (WiaImgFmt_BMP): a complete file with its header, one per page.</summary>
    public static readonly Guid FormatBmp = new(0xb96b3cab, 0x0728, 0x11d3, 0x9d, 0x7b, 0x00, 0x00, 0xf8, 0x1e, 0xf3, 0x2e);

    /// <summary>The image format of WIA 1.0 band transfers without file header (WiaImgFmt_MEMORYBMP).</summary>
    public static readonly Guid FormatMemoryBmp = new(0xb96b3caa, 0x0728, 0x11d3, 0x9d, 0x7b, 0x00, 0x00, 0xf8, 0x1e, 0xf3, 0x2e);

    /// <summary>The WIA device manager 2.0 (CLSID_WiaDevMgr2, wia_lh.h).</summary>
    public static readonly Guid ClsidDevMgr2 = new("B6C292BC-7C88-41ee-8B54-8EC92617E599");

    /// <summary>The WIA device manager 1.0 (CLSID_WiaDevMgr, wia_lh.h).</summary>
    public static readonly Guid ClsidDevMgr = new("a1f4e726-8cf1-11d1-bf92-0060081ed811");

    /// <summary>The medium of a WIA 1.0 transfer: bands to a callback (TYMED_CALLBACK, WiaDef.h).</summary>
    public const int TymedCallback = 128;

    /// <summary>Messages of IWiaDataCallback (WiaDef.h).</summary>
    public const int ItMsgDataHeader = 1, ItMsgData = 2, ItMsgStatus = 3, ItMsgTermination = 4, ItMsgNewPage = 5;

    // ---- HRESULTs: MAKE_HRESULT(severity, FACILITY_WIA = 33, code) (WiaDef.h, winerror.h)
    public const int ErrorGeneral = unchecked((int)0x80210001), ErrorPaperJam = unchecked((int)0x80210002), ErrorPaperEmpty = unchecked((int)0x80210003),
        ErrorPaperProblem = unchecked((int)0x80210004), ErrorOffline = unchecked((int)0x80210005), ErrorBusy = unchecked((int)0x80210006),
        ErrorWarmingUp = unchecked((int)0x80210007), ErrorUserIntervention = unchecked((int)0x80210008), ErrorItemDeleted = unchecked((int)0x80210009),
        ErrorDeviceCommunication = unchecked((int)0x8021000A), ErrorInvalidCommand = unchecked((int)0x8021000B),
        ErrorIncorrectHardwareSetting = unchecked((int)0x8021000C), ErrorDeviceLocked = unchecked((int)0x8021000D),
        ErrorExceptionInDriver = unchecked((int)0x8021000E), ErrorInvalidDriverResponse = unchecked((int)0x8021000F),
        ErrorCoverOpen = unchecked((int)0x80210010), ErrorLampOff = unchecked((int)0x80210011), ErrorDestination = unchecked((int)0x80210012),
        ErrorNetworkReservationFailed = unchecked((int)0x80210013), ErrorMultiFeed = unchecked((int)0x80210014),
        /// <summary>WIA_S_NO_DEVICE_AVAILABLE — despite the name an error, with the same value as WIA_ERROR_MAXIMUM_PRINTER_ENDORSER_COUNTER.</summary>
        NoDeviceAvailable = unchecked((int)0x80210015);

    /// <summary>
    /// HTTP answers the class driver for eSCL passes on unchanged (FACILITY_HTTP, winerror.h): 409 Conflict comes when the feeder is empty
    /// (seen after the last sheet and with an empty feeder), 404 when there is no further document, 503 when the device is busy.
    /// </summary>
    public const int HttpNotFound = unchecked((int)0x80190194), HttpRequestTimeout = unchecked((int)0x80190198), HttpConflict = unchecked((int)0x80190199),
        HttpServiceUnavailable = unchecked((int)0x801901F7), HttpGatewayTimeout = unchecked((int)0x801901F8);

    /// <summary>WIA_STATUS_END_OF_MEDIA: a success code — the feeder has run empty after at least one page.</summary>
    public const int StatusEndOfMedia = 0x00210001;

    public const int SOk = 0, SFalse = 1;
    public const int EAbort = unchecked((int)0x80004004), ENotImpl = unchecked((int)0x80004001), EInvalidArg = unchecked((int)0x80070057),
        EOutOfMemory = unchecked((int)0x8007000E), EFail = unchecked((int)0x80004005);
    /// <summary>The operation was cancelled (HRESULT_FROM_WIN32(ERROR_CANCELLED)).</summary>
    public const int ECancelled = unchecked((int)0x800704C7);
    /// <summary>A property does not exist on the item (STG_E_INVALIDPARAMETER and „not found“ answers of property storages).</summary>
    public const int StgEInvalidParameter = unchecked((int)0x80030057);
    /// <summary>The service is not running (HRESULT_FROM_WIN32(ERROR_SERVICE_NOT_ACTIVE)) and RPC unavailable: „Windows Image Acquisition“ stopped.</summary>
    public const int EServiceNotActive = unchecked((int)0x80070426), RpcServerUnavailable = unchecked((int)0x800706BA);

    // ---- PROPVARIANT types (wtypes.h)
    public const ushort VtEmpty = 0, VtI2 = 2, VtI4 = 3, VtR4 = 4, VtR8 = 5, VtBstr = 8, VtUi2 = 18, VtUi4 = 19, VtI8 = 20, VtUi8 = 21, VtInt = 22,
        VtUint = 23, VtLpwstr = 31, VtClsid = 72, VtVector = 0x1000;
}

/// <summary>PROPSPEC on 64-bit Windows: the kind (1 = by id) and, 8-byte aligned, the id.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct PropSpec
{
    [FieldOffset(0)] public uint Kind;
    [FieldOffset(8)] public uint PropId;

    public static PropSpec Of(int id) => new() { Kind = 1, PropId = (uint)id };
}

/// <summary>PROPVARIANT on 64-bit Windows: the type and a 16-byte union (a value, a pointer, or a count with a pointer).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public int I4;
    [FieldOffset(8)] public long I8;
    [FieldOffset(8)] public double R8;
    [FieldOffset(8)] public float R4;
    [FieldOffset(8)] public nint Pointer;
    /// <summary>The number of elements of a vector (VT_VECTOR | type).</summary>
    [FieldOffset(8)] public uint Count;
    /// <summary>The elements of a vector.</summary>
    [FieldOffset(16)] public nint Elements;
}

/// <summary>STATSTG on 64-bit Windows (objidl.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StatStg
{
    public nint Name;
    public uint Type;
    public ulong Size;
    public long Modified, Created, Accessed;
    public uint Mode, LocksSupported;
    public Guid Clsid;
    public uint StateBits, Reserved;
}

/// <summary>WiaTransferParams (wia_lh.h): what the driver reports during a transfer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WiaTransferParams
{
    public int Message;
    public int PercentComplete;
    public ulong TransferredBytes;
    public int ErrorStatus;
}

[GeneratedComInterface]
[Guid("79C07CF1-CBDD-41ee-8EC3-F00080CADA7A")]
internal partial interface IWiaDevMgr2
{
    [PreserveSig] int EnumDeviceInfo(int flags, out IEnumWiaDevInfo? devices);

    [PreserveSig] int CreateDevice(int flags, [MarshalUsing(typeof(BStrStringMarshaller))] string deviceId, out IWiaItem2? root);
}

[GeneratedComInterface]
[Guid("5e38b83c-8cf1-11d1-bf92-0060081ed811")]
internal partial interface IEnumWiaDevInfo
{
    /// <summary>Next with one element at a time: the array of the interface is then a single out pointer.</summary>
    [PreserveSig] int Next(uint count, out IWiaPropertyStorage? device, out uint fetched);
}

[GeneratedComInterface]
[Guid("98B5E8A0-29CC-491a-AAC0-E6DB4FDCCEB6")]
internal unsafe partial interface IWiaPropertyStorage
{
    [PreserveSig] int ReadMultiple(uint count, PropSpec* specs, PropVariant* values);

    [PreserveSig] int WriteMultiple(uint count, PropSpec* specs, PropVariant* values, uint firstName);

    [PreserveSig] int DeleteMultiple(uint count, PropSpec* specs);

    [PreserveSig] int ReadPropertyNames(uint count, uint* ids, nint* names);

    [PreserveSig] int WritePropertyNames(uint count, uint* ids, nint* names);

    [PreserveSig] int DeletePropertyNames(uint count, uint* ids);

    [PreserveSig] int Commit(uint flags);

    [PreserveSig] int Revert();

    [PreserveSig] int Enum(nint* enumerator);

    [PreserveSig] int SetTimes(long* created, long* accessed, long* modified);

    [PreserveSig] int SetClass(Guid* clsid);

    [PreserveSig] int Stat(nint stat);

    /// <summary>The access flags and the allowed values (range, list or flags in a vector) of properties.</summary>
    [PreserveSig] int GetPropertyAttributes(uint count, PropSpec* specs, uint* flags, PropVariant* values);
}

[GeneratedComInterface]
[Guid("6CBA0075-1287-407d-9B77-CF0E030435CC")]
internal unsafe partial interface IWiaItem2
{
    [PreserveSig] int CreateChildItem(int itemFlags, int creationFlags, nint name, nint* item);

    [PreserveSig] int DeleteItem(int flags);

    [PreserveSig] int EnumChildItems(Guid* category, out IEnumWiaItem2? items);

    [PreserveSig] int FindItemByName(int flags, nint fullName, nint* item);

    [PreserveSig] int GetItemCategory(out Guid category);

    [PreserveSig] int GetItemType(out int type);
}

[GeneratedComInterface]
[Guid("59970AF4-CD0D-44d9-AB24-52295630E582")]
internal partial interface IEnumWiaItem2
{
    [PreserveSig] int Next(uint count, out IWiaItem2? item, out uint fetched);
}

[GeneratedComInterface]
[Guid("c39d6942-2f4e-4d04-92fe-4ef4d3a1de5a")]
internal partial interface IWiaTransfer
{
    [PreserveSig] int Download(int flags, IWiaTransferCallback callback);

    [PreserveSig] int Upload(int flags, nint source, nint callback);

    [PreserveSig] int Cancel();
}

/// <summary>Implemented by the program: the driver reports progress and asks for a stream for every page.</summary>
[GeneratedComInterface]
[Guid("27d4eaaf-28a6-4ca5-9aab-e678168b9527")]
internal unsafe partial interface IWiaTransferCallback
{
    [PreserveSig] int TransferCallback(int flags, WiaTransferParams* parameters);

    /// <summary>The destination must be handed out as IStream (the driver seeks in it), not as its base ISequentialStream.</summary>
    [PreserveSig] int GetNextStream(int flags, nint itemName, nint fullItemName, out IStream? destination);
}

/// <summary>ISequentialStream (objidl.h), the base of IStream.</summary>
[GeneratedComInterface]
[Guid("0c733a30-2a1c-11ce-ade5-00aa0044773d")]
internal unsafe partial interface ISequentialStream
{
    [PreserveSig] int Read(byte* buffer, uint count, uint* read);

    [PreserveSig] int Write(byte* buffer, uint count, uint* written);
}

/// <summary>IStream (objidl.h): the driver writes a page into it and may seek back to finish the header.</summary>
[GeneratedComInterface]
[Guid("0000000c-0000-0000-C000-000000000046")]
internal unsafe partial interface IStream : ISequentialStream
{
    [PreserveSig] int Seek(long move, int origin, ulong* position);

    [PreserveSig] int SetSize(ulong size);

    [PreserveSig] int CopyTo(nint target, ulong count, ulong* read, ulong* written);

    [PreserveSig] int Commit(int flags);

    [PreserveSig] int Revert();

    [PreserveSig] int LockRegion(ulong offset, ulong count, int type);

    [PreserveSig] int UnlockRegion(ulong offset, ulong count, int type);

    [PreserveSig] int Stat(StatStg* stat, int flags);

    [PreserveSig] int Clone(nint* stream);
}

// ---- WIA 1.0 (wia_lh.h): Windows' class driver for eSCL is reached only through it — the WIA 2.0 device manager neither lists nor
// opens such a device (WIA_S_NO_DEVICE_AVAILABLE, seen on Windows 11 with a network device). Same property storage, own items and transfer.

[GeneratedComInterface]
[Guid("5eb2502a-8cf1-11d1-bf92-0060081ed811")]
internal partial interface IWiaDevMgr
{
    [PreserveSig] int EnumDeviceInfo(int flags, out IEnumWiaDevInfo? devices);

    [PreserveSig] int CreateDevice([MarshalUsing(typeof(BStrStringMarshaller))] string deviceId, out IWiaItem? root);
}

[GeneratedComInterface]
[Guid("4db1ad10-3391-11d2-9a33-00c04fa36145")]
internal partial interface IWiaItem
{
    [PreserveSig] int GetItemType(out int type);

    [PreserveSig] int AnalyzeItem(int flags);

    [PreserveSig] int EnumChildItems(out IEnumWiaItem? items);
}

[GeneratedComInterface]
[Guid("5e8383fc-3391-11d2-9a33-00c04fa36145")]
internal partial interface IEnumWiaItem
{
    [PreserveSig] int Next(uint count, out IWiaItem? item, out uint fetched);
}

/// <summary>The transfer of WIA 1.0: the image in bands to a callback (idtGetBandedData), one page per call.</summary>
[GeneratedComInterface]
[Guid("a6cef998-a5b0-11d2-a08f-00c04f72dc3c")]
internal unsafe partial interface IWiaDataTransfer
{
    [PreserveSig] int GetData(nint medium, nint callback);

    [PreserveSig] int GetBandedData(WiaDataTransferInfo* info, IWiaDataCallback callback);
}

/// <summary>Implemented by the program: the bands of the image, progress, and the end of a page or of the transfer.</summary>
[GeneratedComInterface]
[Guid("a558a866-a5b0-11d2-a08f-00c04f72dc3c")]
internal unsafe partial interface IWiaDataCallback
{
    [PreserveSig] int BandedDataCallback(int message, int status, int percent, int offset, int length, int reserved, int resLength, byte* buffer);
}

/// <summary>WIA_DATA_TRANSFER_INFO (wia_lh.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WiaDataTransferInfo
{
    public uint Size, Section, BufferSize;
    public int DoubleBuffer;
    public uint Reserved1, Reserved2, Reserved3;
}

/// <summary>The few functions of COM itself.</summary>
[SupportedOSPlatform("windows")]
internal static partial class Com
{
    public const uint ClsctxLocalServer = 0x4;

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("ole32.dll")]
    public static unsafe partial int PropVariantClear(PropVariant* value);

    [LibraryImport("oleaut32.dll")]
    public static partial void SysFreeString(nint bstr);
}
