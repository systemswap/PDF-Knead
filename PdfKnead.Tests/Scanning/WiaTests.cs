using System.Buffers.Binary;
using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Wia;
using Xunit.Abstractions;

namespace PdfKnead.Tests.Scanning;

/// <summary>
/// Scanning under Windows through WIA 2.0 (KONZEPT §5.25, S8) without a device: the mapping of devices, sources, capabilities and settings
/// over tables of properties shaped like what Windows' class drivers for eSCL and WSD reported on a real network device; the HRESULTs; the
/// own BMP reader with prepared headers; the page stream and the callback of the transfer. The COM path itself: <see cref="WiaDeviceTests"/>.
/// </summary>
public sealed class WiaTests
{
    /// <summary>Properties as a table: values and what is allowed; every write is recorded.</summary>
    private sealed class Table : IWiaProperties
    {
        public Dictionary<int, object> Values { get; } = [];
        public Dictionary<int, WiaAllowed> Rules { get; } = [];
        public List<(int Id, object Value)> Writes { get; } = [];
        public Dictionary<int, int> Refuse { get; } = [];

        public int? Int(int id) => Values.TryGetValue(id, out var v) && v is int i ? i : null;
        public string? Text(int id) => Values.TryGetValue(id, out var v) ? v as string : null;
        public Guid? Guid(int id) => Values.TryGetValue(id, out var v) && v is Guid g ? g : null;
        public WiaAllowed? Allowed(int id) => Rules.TryGetValue(id, out var r) ? r : Values.ContainsKey(id) ? new WiaAllowed(WiaAllowedKind.None, false, []) : null;

        public int Set(int id, int value)
        {
            Writes.Add((id, value));
            if (Refuse.TryGetValue(id, out var hr)) return hr;
            Values[id] = value;
            return 0;
        }

        public int Set(int id, Guid value)
        {
            Writes.Add((id, value));
            Values[id] = value;
            return 0;
        }

        public Table With(int id, object value, WiaAllowed? rule = null)
        {
            Values[id] = value;
            if (rule is not null) Rules[id] = rule;
            return this;
        }
    }

    private static WiaAllowed List(params int[] values) => new(WiaAllowedKind.List, true, values);
    private static WiaAllowed Range(int min, int max, int step = 1) => new(WiaAllowedKind.Range, true, [], min, max, step);
    private static WiaAllowed Flags(int bits) => new(WiaAllowedKind.Flags, true, [bits]);

    private const string EsclPnp = @"\\?\swd#escl#12345678-9abc-4def-8123-456789abcdef#{6bdd1fc6-810f-11d0-bec7-08002be2092f}";
    private const string WsdPnp = @"\\?\swd#dafwsdprovider#urn:uuid:12345678-9abc-4def-8123-456789abcdef#uri:0f0e0d0c-0b0a-4908-8706-050403020100#scanner1#{6bdd1fc6-810f-11d0-bec7-08002be2092f}";

    private static Table Info(string id, string name, int type, string pnp, string vendor = "Microsoft", string? description = null) => new Table()
        .With(Wia.DipDevId, id).With(Wia.DipDevName, name).With(Wia.DipDevType, type).With(Wia.DipPnpId, pnp).With(Wia.DipVendDesc, vendor)
        .With(Wia.DipDevDesc, description ?? name);

    [Fact]
    public void Devices_EsclTypeKept_CamerasLeftOut_MicrosoftIsNoVendor()
    {
        // Windows' eSCL class driver reports 0xFFFF0001 as the type, not a scanner (high word 1): a filter on „scanner“ would lose it
        var escl = WiaMapping.DeviceOf(Info(@"SWD\Escl\12345678-9abc-4def-8123-456789abcdef", "Muster MFP 100 (A1B2C3)", unchecked((int)0xFFFF0001), EsclPnp), out var uuid);
        Assert.NotNull(escl);
        Assert.Equal("12345678-9abc-4def-8123-456789abcdef", uuid);
        Assert.Equal(("", "Muster MFP 100 (A1B2C3)", "eSCL"), (escl.Vendor, escl.Model, escl.Via));
        Assert.Equal("Muster MFP 100 (A1B2C3) (eSCL)", escl.DisplayName);
        var wsd = WiaMapping.DeviceOf(Info(@"{6BDD1FC6-810F-11D0-BEC7-08002BE2092F}\0000", "MFP 100 (Muster MFP 100)", 0x00010001, WsdPnp, description: "WSD-Scandienst"), out var wsdUuid);
        Assert.Equal(("WSD", "WSD-Scandienst"), (wsd!.Via, wsd.Kind));
        Assert.Equal(uuid, wsdUuid);
        // a camera (2) and a video device (3) are no scanners; a vendor's own driver keeps its name and has no way
        Assert.Null(WiaMapping.DeviceOf(Info("cam", "Kamera", 0x00020001, ""), out _));
        Assert.Null(WiaMapping.DeviceOf(Info("vid", "Video", 0x00030000, ""), out _));
        var usb = WiaMapping.DeviceOf(Info("usb", "Muster Scan 1", 0x00010000, @"\\?\usb#vid_04a9&pid_1234#5&1", vendor: "Muster"), out var none);
        Assert.Equal(("Muster", null, null), (usb!.Vendor, usb.Via, none));
        Assert.Equal("Muster Scan 1", usb.DisplayName);
        // no id: nothing to open it by
        Assert.Null(WiaMapping.DeviceOf(new Table().With(Wia.DipDevName, "x"), out _));
    }

    [Fact]
    public void Devices_SameDeviceTogether_EsclFirst()
    {
        var wsd = new ScanDevice("wsd", "", "MFP 100 (Muster MFP 100)", "", "WSD");
        var other = new ScanDevice("usb", "Muster", "Scan 1", "");
        var escl = new ScanDevice("escl", "", "Muster MFP 100 (A1B2C3)", "", "eSCL");
        const string Same = "12345678-9abc-4def-8123-456789abcdef";
        // every way as an entry of its own (tests that try one way): the same device together, eSCL first
        Assert.Equal([escl, wsd, other], WiaMapping.Order([(wsd, Same), (other, null), (escl, Same)]));
        Assert.Equal("eSCL", WiaMapping.WayOf(EsclPnp));
        Assert.Equal("WSD", WiaMapping.WayOf(WsdPnp));
        Assert.Null(WiaMapping.WayOf(@"\\?\usb#vid_04a9"));
        Assert.Null(WiaMapping.UuidOf("keine"));
    }

    [Fact]
    public void Devices_OneEntryPerDevice_BestWayGivesIt_OthersToFallBackOn()
    {
        // the program chooses the driver: one entry for the device, eSCL opens it, WSD only when eSCL cannot reach it; no way in the name
        var wsd = new ScanDevice("wsd", "", "MFP 100 (Muster MFP 100)", "WSD-Scandienst", "WSD");
        var other = new ScanDevice("usb", "Muster", "Scan 1", "");
        var escl = new ScanDevice("escl", "", "Muster MFP 100 (A1B2C3)", "", "eSCL");
        const string Same = "12345678-9abc-4def-8123-456789abcdef";
        var devices = WiaMapping.Combine([(wsd, Same), (other, null), (escl, Same)]);
        Assert.Equal(2, devices.Count);
        Assert.Equal(escl with { Via = null, Alternates = "wsd" }, devices[0]);
        Assert.Equal(["escl", "wsd"], devices[0].AllIds);
        Assert.Equal("Muster MFP 100 (A1B2C3)", devices[0].DisplayName);
        Assert.True(devices[0].Has("wsd") && !devices[0].Has("usb") && !devices[0].Has(null));
        Assert.Same(other, devices[1]);
        // a vendor's driver before WSD; two devices of the same model (other UUIDs) stay two
        var vendor = new ScanDevice("hersteller", "Muster", "MFP 100", "");
        Assert.Equal(["hersteller", "wsd"], WiaMapping.Combine([(wsd, Same), (vendor, Same)])[0].AllIds);
        var second = escl with { Id = "escl2" };
        Assert.Equal(2, WiaMapping.Combine([(escl, Same), (second, "87654321-9abc-4def-8123-456789abcdef")]).Count);
        // no ids in the text that separates them: two equal lists are equal entries (profiles, the helper process)
        Assert.Equal(WiaMapping.Combine([(wsd, Same), (escl, Same)])[0], WiaMapping.Combine([(escl, Same), (wsd, Same)])[0]);
    }

    [Fact]
    public void OpenFirst_NextWayOnlyWhenTheDeviceIsNotReached()
    {
        var tried = new List<string>();
        (string? Opened, int Hr) Way(string id, int hr)
        {
            tried.Add(id);
            return hr == 0 ? (id, 0) : (null, hr);
        }
        // the best way cannot reach the device: the next opens it
        var hrs = new Dictionary<string, int> { ["escl"] = Wia.ErrorOffline, ["wsd"] = 0 };
        Assert.Equal(("wsd", 0), WiaMapping.OpenFirst(["escl", "wsd"], id => Way(id, hrs[id])));
        Assert.Equal(["escl", "wsd"], tried);
        // the best way opens it: the others are never tried
        tried.Clear();
        hrs["escl"] = 0;
        Assert.Equal(("escl", 0), WiaMapping.OpenFirst(["escl", "wsd"], id => Way(id, hrs[id])));
        Assert.Equal(["escl"], tried);
        // a state of the device (busy) is the answer at once: every way would meet it
        tried.Clear();
        hrs["escl"] = Wia.ErrorBusy;
        Assert.Equal((null, Wia.ErrorBusy), WiaMapping.OpenFirst(["escl", "wsd"], id => Way(id, hrs[id])));
        Assert.Equal(["escl"], tried);
        // no way reaches it: the answer of the best way; no way at all: no device
        hrs["escl"] = Wia.NoDeviceAvailable;
        hrs["wsd"] = Wia.ErrorOffline;
        Assert.Equal((null, Wia.NoDeviceAvailable), WiaMapping.OpenFirst(["escl", "wsd"], id => Way(id, hrs[id])));
        Assert.Equal((null, Wia.NoDeviceAvailable), WiaMapping.OpenFirst<string>([], id => Way(id, 0)));
    }

    [Fact]
    public void Sources_ByCategoryElseName_FeederSidesAreNoSources()
    {
        Assert.Equal(ScanSourceKind.Flatbed, WiaMapping.SourceKindOf(Wia.CategoryFlatbed, "Irgendwas"));
        Assert.Equal(ScanSourceKind.Feeder, WiaMapping.SourceKindOf(Wia.CategoryFeeder, null));
        Assert.Null(WiaMapping.SourceKindOf(Wia.CategoryFeederFront, "Front"));
        Assert.Null(WiaMapping.SourceKindOf(Wia.CategoryFeederBack, "Back"));
        Assert.Null(WiaMapping.SourceKindOf(Wia.CategoryAuto, "Auto"));
        Assert.Equal(ScanSourceKind.Flatbed, WiaMapping.SourceKindOf(null, "Flatbed"));
        Assert.Equal(ScanSourceKind.Feeder, WiaMapping.SourceKindOf(Guid.NewGuid(), "Feeder"));
        // the names the session gives are recognised again (profiles store them)
        Assert.Equal(ScanSourceKind.FeederDuplex, ScanSource.FromName(WiaMapping.FeederDuplexName).Kind);
        Assert.Equal(ScanSourceKind.Feeder, ScanSource.FromName(WiaMapping.FeederName).Kind);
        Assert.Equal(ScanSourceKind.Flatbed, ScanSource.FromName(WiaMapping.FlatbedName).Kind);
    }

    [Fact]
    public void Duplex_FromTheFeederElseTheDevice()
    {
        Assert.True(WiaMapping.FeederHasDuplex(new Table().With(Wia.IpsDocumentHandlingSelect, Wia.FrontOnly, Flags(Wia.FrontOnly | Wia.Duplex)), new Table()));
        Assert.False(WiaMapping.FeederHasDuplex(new Table().With(Wia.IpsDocumentHandlingSelect, Wia.FrontOnly, Flags(Wia.FrontOnly)), new Table()));
        // capabilities 0x8003 (feeder, flatbed, automatic source) as on the test device: no duplex
        Assert.False(WiaMapping.FeederHasDuplex(new Table(), new Table().With(Wia.DpsDocumentHandlingCapabilities, 0x8003)));
        Assert.True(WiaMapping.FeederHasDuplex(new Table(), new Table().With(Wia.DpsDocumentHandlingCapabilities, 0x8007)));
    }

    private static Table EsclFlatbed() => new Table()
        .With(Wia.IpsXres, 200, List(75, 150, 200, 300, 600, 1200)).With(Wia.IpsYres, 200, List(75, 150, 200, 300, 600, 1200))
        .With(Wia.IpaDatatype, Wia.DataColor, List(Wia.DataColor, Wia.DataGrayscale))
        .With(Wia.IpsMaxHorizontalSize, 8500).With(Wia.IpsMaxVerticalSize, 11693)
        .With(Wia.IpsXpos, 0, Range(0, 1316)).With(Wia.IpsYpos, 0, Range(0, 1954))
        .With(Wia.IpsXextent, 1700, Range(384, 1700)).With(Wia.IpsYextent, 2338, Range(384, 2338))
        .With(Wia.IpaFormat, Wia.FormatBmp, new WiaAllowed(WiaAllowedKind.List, true, [], Guids: [Wia.FormatBmp]))
        .With(Wia.IpaCompression, 0, List(0, 5)).With(Wia.IpsPages, 1, Range(0, int.MaxValue));

    [Fact]
    public void Capabilities_ListsAndSizes()
    {
        var caps = WiaMapping.CapabilitiesOf(EsclFlatbed(), new Table(), ScanSourceKind.Flatbed, [], null);
        Assert.Equal([75, 150, 200, 300, 600, 1200], caps.Resolutions);
        // no threshold type: black and white is left to the processing (S3), never offered as a mode the device lacks
        Assert.Equal([ScanColorMode.Color, ScanColorMode.Gray], caps.ColorModes);
        Assert.Equal(215.9, caps.MaxWidthMm, 1);
        Assert.Equal(297.0, caps.MaxHeightMm, 1);
        // a range of resolutions, dithering left out, the sizes of the device where the item has none
        var range = new Table().With(Wia.IpsXres, 300, Range(50, 600, 1)).With(Wia.IpaDatatype, 0, List(0, 1, 2));
        var root = new Table().With(Wia.DpsHorizontalSheetFeedSize, 8500).With(Wia.DpsVerticalSheetFeedSize, 14000);
        var feeder = WiaMapping.CapabilitiesOf(range, root, ScanSourceKind.Feeder, [], null);
        Assert.Equal([50, 75, 100, 150, 200, 240, 300, 400, 600], feeder.Resolutions);
        Assert.Equal([ScanColorMode.Gray, ScanColorMode.BlackWhite], feeder.ColorModes);
        Assert.Equal(355.6, feeder.MaxHeightMm, 1);
    }

    [Fact]
    public void Apply_OrderAndValues_AreaInPixelsOfTheResolution()
    {
        var item = EsclFlatbed();
        var caps = WiaMapping.CapabilitiesOf(item, new Table(), ScanSourceKind.Flatbed, [], null);
        var notes = new List<string>();
        // 250 dpi is not offered: 300 (the closer, ties upward); A4 at 300 dpi: 2480 × 3507 pixels, cut, not rounded
        item.Rules[Wia.IpsXextent] = Range(384, 2550);
        item.Rules[Wia.IpsYextent] = Range(384, 3507);
        var dpi = WiaMapping.Apply(item, new Table(), ScanSourceKind.Flatbed, new ScanSettings(null, 250, ScanColorMode.Gray, 210, 297), caps, notes);
        Assert.Equal(300, dpi);
        Assert.Equal((Wia.IpsPages, (object)1), item.Writes[0]);
        Assert.Equal(Wia.DataGrayscale, item.Int(Wia.IpaDatatype));
        Assert.Equal((2480, 3507), (item.Int(Wia.IpsXextent), item.Int(Wia.IpsYextent)));
        Assert.Equal(0, item.Int(Wia.IpsXpos));
        // the resolution before the area, the bit depth never
        var order = item.Writes.Select(w => w.Id).ToList();
        Assert.True(order.IndexOf(Wia.IpsXres) < order.IndexOf(Wia.IpsXextent));
        Assert.DoesNotContain(Wia.IpaDepth, order);
        Assert.Empty(notes);
    }

    [Fact]
    public void Apply_Feeder_FrontOnlyAllPages_CentredPaper_RefusedPropertyNotFatal()
    {
        var item = EsclFlatbed()
            .With(Wia.IpsDocumentHandlingSelect, Wia.FrontOnly, Flags(Wia.FrontOnly | Wia.Duplex))
            .With(Wia.IpsSheetFeederRegistration, Wia.Centered);
        item.Rules[Wia.IpsXextent] = Range(384, 2550);
        item.Rules[Wia.IpsXpos] = Range(0, 2000);
        item.Rules[Wia.IpsYextent] = Range(384, 4200);
        item.Refuse[Wia.IpsYres] = unchecked((int)0x80070057);
        var caps = WiaMapping.CapabilitiesOf(item, new Table(), ScanSourceKind.Feeder, [], null);
        var notes = new List<string>();
        // black and white asked, the device has no threshold type: grey (the closest), converted later by the processing if wanted
        WiaMapping.Apply(item, new Table(), ScanSourceKind.Feeder, new ScanSettings(null, 300, ScanColorMode.BlackWhite, 148, 210), caps, notes);
        Assert.Equal(Wia.FrontOnly, item.Int(Wia.IpsDocumentHandlingSelect));
        Assert.Equal(0, item.Int(Wia.IpsPages));
        Assert.Equal(Wia.DataGrayscale, item.Int(Wia.IpaDatatype));
        // A5 (1748 px) centred in the feeder's 2550: offset (2550 − 1748) / 2
        Assert.Equal((1748, (2550 - 1748) / 2), (item.Int(Wia.IpsXextent), item.Int(Wia.IpsXpos)));
        Assert.Contains(notes, n => n.Contains("0x80070057"));
        // both sides: the duplex bit
        var both = EsclFlatbed().With(Wia.IpsDocumentHandlingSelect, Wia.FrontOnly, Flags(Wia.FrontOnly | Wia.Duplex));
        WiaMapping.Apply(both, new Table(), ScanSourceKind.FeederDuplex, new ScanSettings(null, 300, ScanColorMode.Color), caps, []);
        Assert.Equal(Wia.Duplex, both.Int(Wia.IpsDocumentHandlingSelect));
    }

    [Fact]
    public void Errors_EndOfFeederAndMessages()
    {
        // the end of the feeder: END_OF_MEDIA always, PAPER_EMPTY only after pages — before the first page it is the error
        Assert.True(WiaMapping.IsEndOfFeeder(Wia.StatusEndOfMedia, true, 0));
        Assert.True(WiaMapping.IsEndOfFeeder(Wia.ErrorPaperEmpty, true, 3));
        Assert.False(WiaMapping.IsEndOfFeeder(Wia.ErrorPaperEmpty, true, 0));
        Assert.False(WiaMapping.IsEndOfFeeder(Wia.ErrorPaperEmpty, false, 1));
        Assert.Equal(ScanErrorKind.NoPaper, WiaMapping.ErrorOf(Wia.ErrorPaperEmpty).Kind);
        Assert.Equal(ScanErrorKind.Jammed, WiaMapping.ErrorOf(Wia.ErrorPaperJam).Kind);
        Assert.Equal(ScanErrorKind.Jammed, WiaMapping.ErrorOf(Wia.ErrorMultiFeed).Kind);
        Assert.Equal(ScanErrorKind.CoverOpen, WiaMapping.ErrorOf(Wia.ErrorCoverOpen).Kind);
        Assert.Equal(ScanErrorKind.Busy, WiaMapping.ErrorOf(Wia.ErrorBusy).Kind);
        Assert.Equal(ScanErrorKind.Busy, WiaMapping.ErrorOf(Wia.ErrorWarmingUp).Kind);
        // 0x80210015 is WIA_S_NO_DEVICE_AVAILABLE and also the endorser counter: read as „not reachable“
        Assert.Equal(unchecked((int)0x80210015), Wia.NoDeviceAvailable);
        Assert.Equal(ScanErrorKind.Offline, WiaMapping.ErrorOf(Wia.NoDeviceAvailable).Kind);
        Assert.Equal(ScanErrorKind.InterfaceMissing, WiaMapping.ErrorOf(Wia.EServiceNotActive).Kind);
        Assert.Contains("0x8021000E", WiaMapping.ErrorOf(Wia.ErrorExceptionInDriver).Message);
        // a transfer refused before any data: E_FAIL (the WSD class driver while still busy with the last scan), busy, warming up — tried
        // again four times, 15 s in all; an empty feeder, a jam or a refused setting never (Fehlergedächtnis 853)
        Assert.Equal([2.0, 3.0, 5.0, 5.0], Enumerable.Range(0, 4).Select(a => WiaMapping.RetryAfter(Wia.EFail, a)!.Value.TotalSeconds));
        Assert.Null(WiaMapping.RetryAfter(Wia.EFail, 4));
        Assert.NotNull(WiaMapping.RetryAfter(Wia.ErrorBusy, 0));
        Assert.NotNull(WiaMapping.RetryAfter(Wia.ErrorWarmingUp, 0));
        Assert.Null(WiaMapping.RetryAfter(Wia.ErrorPaperEmpty, 0));
        Assert.Null(WiaMapping.RetryAfter(Wia.ErrorPaperJam, 0));
        Assert.Null(WiaMapping.RetryAfter(Wia.EInvalidArg, 0));
        // the class driver for eSCL passes HTTP answers on: 409 after the last sheet (and with an empty feeder) is the end of the feeder,
        // not an error and not tried again; at the flatbed 409 and 503 mean busy (Fehlergedächtnis 854)
        Assert.True(WiaMapping.IsEndOfFeeder(Wia.HttpConflict, true, 4));
        Assert.True(WiaMapping.IsEndOfFeeder(Wia.HttpNotFound, true, 1));
        Assert.False(WiaMapping.IsEndOfFeeder(Wia.HttpConflict, true, 0));
        Assert.False(WiaMapping.IsEndOfFeeder(Wia.HttpConflict, false, 1));
        Assert.Equal(ScanErrorKind.NoPaper, WiaMapping.ErrorOf(Wia.HttpConflict, feeder: true).Kind);
        Assert.Equal(ScanErrorKind.Busy, WiaMapping.ErrorOf(Wia.HttpConflict, feeder: false).Kind);
        Assert.Equal(ScanErrorKind.Busy, WiaMapping.ErrorOf(Wia.HttpServiceUnavailable).Kind);
        Assert.Null(WiaMapping.RetryAfter(Wia.HttpConflict, 0, feeder: true));
        Assert.NotNull(WiaMapping.RetryAfter(Wia.HttpConflict, 0, feeder: false));
        Assert.NotNull(WiaMapping.RetryAfter(Wia.HttpServiceUnavailable, 0, feeder: true));
        // a driver refusing the transfer of WIA 2.0 as „invalid argument“ before any image: once more through WIA 1.0; after an image never
        Assert.True(WiaMapping.FallsBackToLegacy(Wia.EInvalidArg, started: false));
        Assert.False(WiaMapping.FallsBackToLegacy(Wia.EInvalidArg, started: true));
        Assert.False(WiaMapping.FallsBackToLegacy(Wia.EFail, started: false));
        Assert.Contains("0x80001234", WiaMapping.ErrorOf(unchecked((int)0x80001234)).Message);
    }

    // ---- BMP

    /// <summary>A BMP as drivers write it: file header, 40-byte info header, palette, rows padded to four bytes.</summary>
    private static byte[] Bmp(int width, int height, int bits, Func<int, int, byte[]> pixel, byte[]? palette = null, bool fileHeader = true,
        int? claimedHeight = null, int dropRows = 0, uint compression = 0)
    {
        var stride = (width * bits + 31) / 32 * 4;
        var rows = Math.Abs(height) - dropRows;
        var paletteBytes = palette?.Length ?? 0;
        var start = (fileHeader ? 14 : 0) + 40 + paletteBytes;
        var data = new byte[start + stride * rows];
        if (fileHeader)
        {
            data[0] = (byte)'B';
            data[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), start);
        }
        var info = data.AsSpan(fileHeader ? 14 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(info, 40);
        BinaryPrimitives.WriteInt32LittleEndian(info[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(info[8..], claimedHeight ?? height);
        BinaryPrimitives.WriteInt16LittleEndian(info[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(info[14..], (short)bits);
        BinaryPrimitives.WriteUInt32LittleEndian(info[16..], compression);
        BinaryPrimitives.WriteInt32LittleEndian(info[32..], paletteBytes / 4);
        palette?.CopyTo(data, (fileHeader ? 14 : 0) + 40);
        var topDown = height < 0;
        for (var stored = 0; stored < rows; stored++)
        {
            var y = topDown ? stored : Math.Abs(height) - 1 - stored;
            for (var x = 0; x < width; x++)
            {
                var value = pixel(x, y);
                if (bits >= 8) value.CopyTo(data, start + stored * stride + x * bits / 8);
                else if (value[0] != 0) data[start + stored * stride + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }
        return data;
    }

    private static ScannedImage Decode(byte[] data, bool gray = false) => WiaImage.Decode(new ArrayPageData(data), 300, gray);

    [Fact]
    public void Bmp_24Bit_BottomUp_Padded_SizeFromTheHeader()
    {
        // width 3: rows of 9 bytes padded to 12; bottom-up rows; BGR to RGB — 2479 wide as the WSD driver delivered for an extent of 2480
        var image = Decode(Bmp(3, 2, 24, (x, y) => [(byte)(x * 10), (byte)(y * 20), 200]));
        Assert.Equal((3, 2, 3, 300), (image.Width, image.Height, image.Channels, image.Dpi));
        Assert.Equal(new byte[] { 200, 0, 0, 200, 0, 10, 200, 0, 20 }, image.Pixels[..9]);
        Assert.Equal(new byte[] { 200, 20, 0 }, image.Pixels[9..12]);
        var wsd = Decode(Bmp(2479, 2, 24, (_, _) => [1, 2, 3]));
        Assert.Equal(2479, wsd.Width);
        // top-down rows (negative height); grey asked: one channel
        var top = Decode(Bmp(2, -2, 24, (x, y) => [(byte)(y * 100), (byte)(y * 100), (byte)(y * 100)]), gray: true);
        Assert.Equal(1, top.Channels);
        Assert.Equal(new byte[] { 0, 0, 100, 100 }, top.Pixels);
    }

    [Fact]
    public void Bmp_Palette_GrayAndBlackWhite_WithoutFileHeader()
    {
        var grayPalette = Enumerable.Range(0, 256).SelectMany(i => new[] { (byte)i, (byte)i, (byte)i, (byte)0 }).ToArray();
        var gray = Decode(Bmp(5, 1, 8, (x, _) => [(byte)(x * 50)], grayPalette));
        Assert.Equal(1, gray.Channels);
        Assert.Equal(new byte[] { 0, 50, 100, 150, 200 }, gray.Pixels);
        // 1 bit with a black and white palette (index 1 = white): black and white, 0 and 255
        var bw = Decode(Bmp(10, 1, 1, (x, _) => [(byte)(x % 2)], [0, 0, 0, 0, 255, 255, 255, 0]));
        Assert.True(bw.BlackWhite);
        Assert.Equal(new byte[] { 0, 255, 0, 255, 0, 255, 0, 255, 0, 255 }, bw.Pixels);
        // a memory bitmap: the info header first, no file header
        var memory = Decode(Bmp(2, 1, 8, (x, _) => [(byte)(x * 7)], grayPalette, fileHeader: false));
        Assert.Equal(new byte[] { 0, 7 }, memory.Pixels);
        // a coloured palette: three channels
        var colored = Decode(Bmp(1, 1, 8, (_, _) => [1], [0, 0, 0, 0, 10, 20, 30, 0]));
        Assert.Equal(3, colored.Channels);
        Assert.Equal(new byte[] { 30, 20, 10 }, colored.Pixels);
    }

    [Fact]
    public void Bmp_FewerRowsThanAnnounced_OrHeightZero_TheRowsThatCame()
    {
        // the feeder measured shorter paper: the rows that arrived (of a bottom-up page its lower part)
        var shorter = Decode(Bmp(2, 4, 24, (_, y) => [(byte)y, 0, 0], dropRows: 1));
        Assert.Equal(3, shorter.Height);
        // a height of 0 (older drivers, paper of unknown length): the rows of the data
        var zero = Decode(Bmp(2, 3, 24, (_, _) => [9, 9, 9], claimedHeight: 0));
        Assert.Equal(3, zero.Height);
    }

    [Theory]
    [InlineData(0, 10, 24, 0u)]            // no width
    [InlineData(-5, 10, 24, 0u)]           // negative width
    [InlineData(2_000_000, 1, 24, 0u)]     // wider than any page
    [InlineData(10, 10, 16, 0u)]           // 16 bits: not written by scanners
    [InlineData(10, 10, 8, 1u)]            // run-length coded
    public void Bmp_PreparedHeaders_RefusedBeforeAllocating(int width, int height, int bits, uint compression)
    {
        var data = Bmp(1, 1, 24, (_, _) => [0, 0, 0]);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(28), (short)bits);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(30), compression);
        Assert.Throws<ScanException>(() => Decode(data));
    }

    [Fact]
    public void Bmp_HugeClaims_NoHugeArrays_AndIndexPastThePalette()
    {
        // 1 000 000 × 1 000 000 announced, a few bytes delivered: the page has the rows that came, nothing of the announced size is allocated
        var data = Bmp(4, 1, 24, (_, _) => [1, 2, 3]);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), 1_000_000);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var image = Decode(data);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
        Assert.Equal(1, image.Height);
        // wider than the data: no row at all is complete
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), 1_000_000);
        Assert.Throws<ScanException>(() => Decode(data));
        // pixel offset past the end
        var offset = Bmp(1, 1, 24, (_, _) => [0, 0, 0]);
        BinaryPrimitives.WriteInt32LittleEndian(offset.AsSpan(10), 1_000_000);
        Assert.Throws<ScanException>(() => Decode(offset));
        // an index past a two-entry palette: the last entry, never a read past it
        var past = Decode(Bmp(1, 1, 8, (_, _) => [200], [0, 0, 0, 0, 255, 255, 255, 0]));
        Assert.Equal(255, past.Pixels[0]);
        // no image at all
        Assert.Throws<ScanException>(() => Decode("GIF89a"u8.ToArray()));
        Assert.Throws<ScanException>(() => Decode([]));
    }

    [Fact]
    public void Bmp_32BitWithMasks_AndPng()
    {
        var data = Bmp(1, 1, 32, (_, _) => [30, 20, 10, 0], compression: 3);
        // masks after the 40-byte header: blue in the low byte, as written by drivers; insert them
        var withMasks = new byte[data.Length + 12];
        data.AsSpan(0, 54).CopyTo(withMasks);
        BinaryPrimitives.WriteUInt32LittleEndian(withMasks.AsSpan(54), 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(withMasks.AsSpan(58), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(withMasks.AsSpan(62), 0x000000FF);
        data.AsSpan(54).CopyTo(withMasks.AsSpan(66));
        BinaryPrimitives.WriteInt32LittleEndian(withMasks.AsSpan(10), 66);
        Assert.Equal(new byte[] { 10, 20, 30 }, Decode(withMasks).Pixels);
        // PNG from drivers that refuse BMP: grey content in one channel
        using var png = new MemoryStream();
        PdfKnead.Core.Imaging.PngEncoder.Encode(2, 1, 8, PdfKnead.Core.Imaging.PngColorType.Gray, (_, row) => { row[0] = 10; row[1] = 20; }, png);
        var image = Decode(png.ToArray());
        Assert.Equal((2, 1, 1), (image.Width, image.Height, image.Channels));
        Assert.Equal(new byte[] { 10, 20 }, image.Pixels);
    }

    // ---- page stream and callback

    private static unsafe void Write(PageStream stream, byte[] bytes)
    {
        uint written;
        fixed (byte* p = bytes) Assert.Equal(0, stream.Write(p, (uint)bytes.Length, &written));
        Assert.Equal((uint)bytes.Length, written);
    }

    private static unsafe void SeekTo(PageStream stream, long position)
    {
        ulong at;
        Assert.Equal(0, stream.Seek(position, 0, &at));
        Assert.Equal((ulong)position, at);
    }

    [Fact]
    public unsafe void PageStream_SeekBackToFinishTheHeader_GapsAreZero_Limit()
    {
        var stream = new PageStream();
        Write(stream, [1, 2, 3, 4]);
        SeekTo(stream, 1);
        Write(stream, [9]);                       // the header finished afterwards
        SeekTo(stream, PageStream.ChunkSize + 2);   // past a piece boundary, a gap
        Write(stream, [7, 8]);
        Assert.Equal(PageStream.ChunkSize + 4, stream.Length);
        var all = new byte[stream.Length];
        stream.Read(0, all);
        Assert.Equal(new byte[] { 1, 9, 3, 4, 0 }, all[..5]);
        Assert.Equal(new byte[] { 0, 7, 8 }, all[(PageStream.ChunkSize + 1)..]);
        // shrinking and growing again: the cut part reads as zeros
        Assert.Equal(0, stream.SetSize(2));
        Assert.Equal(0, stream.SetSize(4));
        var four = new byte[4];
        stream.Read(0, four);
        Assert.Equal(new byte[] { 1, 9, 0, 0 }, four);
        StatStg stat;
        Assert.Equal(0, stream.Stat(&stat, 1));
        Assert.Equal(4ul, stat.Size);
        // more than the limit: refused with „medium full“, the page marked
        var small = new PageStream { Limit = 8 };
        Write(small, [1, 2, 3, 4, 5, 6, 7, 8]);
        uint written;
        byte one = 1;
        Assert.Equal(unchecked((int)0x80030070), small.Write(&one, 1, &written));
        Assert.True(small.Overflowed);
        Assert.Equal(unchecked((int)0x80030001), small.Seek(9, 0, null));
    }

    [Fact]
    public unsafe void Callback_PageOnEndOfStream_EmptyStreamsSkipped_ErrorsKeptNotThrown_CancelBySFalse()
    {
        var pages = new List<(int Number, long Length)>();
        var callback = new PageCallback((page, number) => pages.Add((number, page.Length)), null);
        Assert.Equal(0, callback.GetNextStream(0, 0, 0, out var first));
        Write((PageStream)first!, [1, 2, 3]);
        var end = new WiaTransferParams { Message = Wia.TransferMsgEndOfStream };
        Assert.Equal(0, callback.TransferCallback(0, &end));
        Assert.Equal([(1, 3L)], pages);
        // an empty stream (some drivers end the feeder so) is no page
        Assert.Equal(0, callback.GetNextStream(0, 0, 0, out _));
        Assert.Equal(0, callback.TransferCallback(0, &end));
        Assert.Single(pages);
        // a page whose stream the driver did not end: complete after a successful Download
        Assert.Equal(0, callback.GetNextStream(0, 0, 0, out var last));
        Write((PageStream)last!, [4]);
        callback.Complete();
        Assert.Equal(2, callback.Pages);

        // a failing page stops the transfer (S_FALSE), the exception is kept for after Download
        var failing = new PageCallback((_, _) => throw new ScanException("kaputt"), null);
        failing.GetNextStream(0, 0, 0, out var bad);
        Write((PageStream)bad!, [1]);
        Assert.Equal(1, failing.TransferCallback(0, &end));
        Assert.IsType<ScanException>(failing.Failure);
        Assert.Equal(1, failing.GetNextStream(0, 0, 0, out var none));
        Assert.Null(none);

        // cancelling: S_FALSE at the next callback and for the next stream
        var cancelled = new PageCallback((_, _) => { }, null);
        var status = new WiaTransferParams { Message = Wia.TransferMsgStatus, PercentComplete = 40 };
        Assert.Equal(0, cancelled.TransferCallback(0, &status));
        cancelled.Cancel();
        Assert.Equal(1, cancelled.TransferCallback(0, &status));
        Assert.Equal(1, cancelled.GetNextStream(0, 0, 0, out _));
    }

    [Fact]
    public unsafe void BandCallback_BandsAppended_PageOnNewPageOrEnd_CancelBySFalse()
    {
        // WIA 1.0: the bands of a page (info header first) appended; a page ends with „new page“ or the end of the transfer
        var pages = new List<byte[]>();
        var callback = new BandCallback((page, _) => { var b = new byte[page.Length]; page.Read(0, b); pages.Add(b); }, null);
        byte[] first = [1, 2, 3], second = [4, 5];
        fixed (byte* a = first) Assert.Equal(0, callback.BandedDataCallback(Wia.ItMsgData, 1, 10, 0, first.Length, 0, 0, a));
        fixed (byte* b = second) Assert.Equal(0, callback.BandedDataCallback(Wia.ItMsgData, 1, 20, 3, second.Length, 0, 0, b));
        Assert.Equal(0, callback.BandedDataCallback(Wia.ItMsgNewPage, 0, 100, 0, 0, 0, 0, null));
        fixed (byte* a = first) callback.BandedDataCallback(Wia.ItMsgData, 1, 10, 0, 1, 0, 0, a);
        callback.BandedDataCallback(Wia.ItMsgTermination, 0, 100, 0, 0, 0, 0, null);
        Assert.Equal(2, callback.Pages);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, pages[0]);
        Assert.Equal(new byte[] { 1 }, pages[1]);
        // bands out of order, each at its offset: the driver writes a bottom-up BMP from its end as it scans from the top, the compatibility
        // layer passes on where — in the order they come the page would be mirrored top to bottom (Fehlergedächtnis 855)
        byte[] header = [9, 9], low = [1, 1], high = [2, 2];
        fixed (byte* h = header) callback.BandedDataCallback(Wia.ItMsgData, 1, 0, 0, 2, 0, 0, h);
        fixed (byte* b = high) callback.BandedDataCallback(Wia.ItMsgData, 1, 50, 4, 2, 0, 0, b);   // the top row, stored last
        fixed (byte* a = low) callback.BandedDataCallback(Wia.ItMsgData, 1, 100, 2, 2, 0, 0, a);   // the bottom row, stored first
        callback.BandedDataCallback(Wia.ItMsgTermination, 0, 100, 0, 0, 0, 0, null);
        Assert.Equal(new byte[] { 9, 9, 1, 1, 2, 2 }, pages[2]);
        // a transfer that failed: the begun page is dropped, never handed on
        fixed (byte* a = first) callback.BandedDataCallback(Wia.ItMsgData, 1, 10, 0, 3, 0, 0, a);
        callback.Drop();
        callback.Complete();
        Assert.Equal(3, callback.Pages);
        // cancelled: S_FALSE at the next band
        callback.Cancel();
        fixed (byte* a = first) Assert.Equal(1, callback.BandedDataCallback(Wia.ItMsgData, 1, 10, 0, 3, 0, 0, a));
    }

    [Fact]
    public void Legacy_SourcesFromTheDevice_SelectAtTheDevice()
    {
        // capabilities 0x8003 (feeder, flatbed, automatic source): flatbed and feeder, no both sides
        Assert.Equal([ScanSourceKind.Flatbed, ScanSourceKind.Feeder],
            WiaMapping.LegacySourcesOf(new Table().With(Wia.DpsDocumentHandlingCapabilities, 0x8003)).Select(s => s.Kind));
        Assert.Equal([ScanSourceKind.Flatbed, ScanSourceKind.Feeder, ScanSourceKind.FeederDuplex],
            WiaMapping.LegacySourcesOf(new Table().With(Wia.DpsDocumentHandlingCapabilities, 0x7)).Select(s => s.Kind));
        Assert.Equal([ScanSourceKind.Flatbed], WiaMapping.LegacySourcesOf(new Table()).Select(s => s.Kind));
        // the source and one page per transfer at the device, the image in bands to the program
        var root = new Table().With(Wia.IpsDocumentHandlingSelect, Wia.Flatbed, Flags(Wia.Feeder | Wia.Flatbed | Wia.FrontOnly))
            .With(Wia.IpsPages, 1, Range(0, int.MaxValue));
        var item = EsclFlatbed().With(Wia.IpaTymed, 2, List(128, 2, 256));
        var caps = WiaMapping.CapabilitiesOf(item, root, ScanSourceKind.Feeder, [], null);
        WiaMapping.Apply(item, root, ScanSourceKind.Feeder, new ScanSettings(null, 300, ScanColorMode.Color), caps, [], legacy: true);
        // the feeder: all pages in one transfer (WIA_DPS_PAGES 0, divided by „new page“)
        Assert.Equal((Wia.Feeder, Wia.AllPages), (root.Int(Wia.IpsDocumentHandlingSelect), root.Int(Wia.IpsPages)));
        Assert.Equal(Wia.TymedCallback, item.Int(Wia.IpaTymed));
        Assert.DoesNotContain(item.Writes, w => w.Id == Wia.IpsDocumentHandlingSelect || w.Id == Wia.IpsPages);
    }

    [Fact]
    public void HelperJob_ClosingTheJobEndsItsProcesses()
    {
        if (!OperatingSystem.IsWindows()) return;
        // what Windows does when the program ends: the job is closed, the helper in it ends — even one that waits in a driver call
        var job = PdfKnead.Core.Scanning.Helper.HelperJob.Create();
        Assert.NotEqual(0, job);
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping", "-n 60 127.0.0.1")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        try
        {
            Assert.True(PdfKnead.Core.Scanning.Helper.HelperJob.Assign(job, child.Handle));
            Assert.False(child.WaitForExit(300));
            Assert.True(PdfKnead.Core.Scanning.Helper.HelperJob.Close(job));
            Assert.True(child.WaitForExit(5000), "the process in the job did not end with the job");
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }

    [Fact]
    public void Twain_OnlyRealDataSources_NeverLoaded()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Directory.CreateTempSubdirectory("pdf-knead-twain-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "wiatwain.ds"), "");   // TWAIN's bridge to WIA devices: no TWAIN-only driver
            Assert.Null(WiaBackend.TwainHint([folder]));
            Directory.CreateDirectory(Path.Combine(folder, "Muster"));
            File.WriteAllText(Path.Combine(folder, "Muster", "muster.ds"), "kein Code wird geladen");
            Assert.Contains("Muster", WiaBackend.TwainHint([folder, Path.Combine(folder, "fehlt")]));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

/// <summary>The device tests switch <see cref="WiaSession.ForceLegacyForTests"/> and use the one scanner: they run alone.</summary>
[CollectionDefinition(nameof(WiaDeviceCollection), DisableParallelization = true)]
public sealed class WiaDeviceCollection;

/// <summary>
/// The COM path of WIA on this computer: the device manager, the device information and its properties through the generated interfaces.
/// Searching never opens a device and runs everywhere under Windows (with or without scanners). Opening and scanning need a real device and
/// paper: only with PDFKNEAD_WIA_GERAET set (the name, or a part of it, of the device to use; PRUEFUNGEN §6z).
/// </summary>
[Collection(nameof(WiaDeviceCollection))]
public sealed class WiaDeviceTests
{
    private static readonly string? Wanted = Environment.GetEnvironmentVariable("PDFKNEAD_WIA_GERAET");

    [Fact]
    public async Task FindDevices_ThroughCom_NeverOpensADevice()
    {
        if (!OperatingSystem.IsWindows()) return;
        var backend = new WiaBackend();
        Assert.True(backend.IsAvailable(out _));
        IReadOnlyList<ScanDevice> devices;
        try
        {
            devices = await backend.FindDevicesAsync();
        }
        catch (ScanException e) when (e.Kind == ScanErrorKind.InterfaceMissing)
        {
            return;   // a Windows without the scanner service (server editions): nothing to search
        }
        foreach (var d in devices) _output.WriteLine($"{d.DisplayName} | {d.Kind} | {d.Via}");
        Assert.All(devices, d => Assert.False(string.IsNullOrEmpty(d.Id)));
        // Windows' class drivers are the driver, not the device: never shown as the vendor (the order of pairs: WiaTests)
        Assert.All(devices, d => Assert.NotEqual("Microsoft", d.Vendor));
    }

    private readonly ITestOutputHelper _output;

    public WiaDeviceTests(ITestOutputHelper output) => _output = output;

    /// <summary>Which real scan to run: „flachbett“, „einzug“ (several sheets in the feeder), „leer“ (an empty feeder), „abbrechen“.</summary>
    private static readonly string? Scan = Environment.GetEnvironmentVariable("PDFKNEAD_WIA_SCANNEN");

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<(ScanDevice, IScanSession)> OpenWanted()
    {
        // one way of the device (every way is an entry of its own here): „(eSCL)“ or „(WSD)“ in the name picks it
        var backend = new WiaBackend();
        var device = (await Task.Run(() => WiaBackend.Enumerate(combine: false)))
            .First(d => d.DisplayName.Contains(Wanted!, StringComparison.OrdinalIgnoreCase));
        return (device, await backend.OpenAsync(device));
    }

    [Fact]
    public async Task RealDevice_OneEntry_OpenedByTheBestWay()
    {
        // the device as the scan window lists it: one entry, no way in its name, opened by the best way (eSCL before WSD)
        if (!OperatingSystem.IsWindows() || Wanted is not { Length: > 0 }) return;
        var backend = new WiaBackend();
        var name = Wanted.Replace("(eSCL)", "", StringComparison.OrdinalIgnoreCase).Replace("(WSD)", "", StringComparison.OrdinalIgnoreCase).Trim();
        var devices = await backend.FindDevicesAsync();
        foreach (var d in devices) _output.WriteLine($"{d.DisplayName} | ways {d.AllIds.Count}");
        var device = devices.First(d => d.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase));
        Assert.Null(device.Via);
        await using var session = await backend.OpenAsync(device);
        Assert.Equal(device, session.Device);
        Assert.NotEmpty(session.Capabilities.Sources);
    }

    [Fact]
    public async Task RealDevice_OpenAndCapabilitiesPerSource()
    {
        if (!OperatingSystem.IsWindows() || Wanted is not { Length: > 0 }) return;
        var (device, session) = await OpenWanted();
        await using var _ = session;
        _output.WriteLine($"{device.DisplayName}: {string.Join(", ", session.Capabilities.Sources.Select(s => $"{s.Name} ({s.Kind})"))}");
        Assert.NotEmpty(session.Capabilities.Sources);
        foreach (var source in session.Capabilities.Sources)
        {
            var caps = await session.SelectSourceAsync(source.Name);
            _output.WriteLine($"  {source.Name}: dpi {string.Join("/", caps.Resolutions)}; {string.Join("/", caps.ColorModes)}; {caps.MaxWidthMm:0.#} × {caps.MaxHeightMm:0.#} mm");
            Assert.NotEmpty(caps.Resolutions);
            Assert.NotEmpty(caps.ColorModes);
            Assert.True(caps.MaxWidthMm > 50 && caps.MaxHeightMm > 50);
        }
    }

    private async Task<List<ScannedImage>> ScanWith(IScanSession session, ScanSourceKind kind, int dpi, ScanColorMode mode, double? width = 210,
        double? height = 297, CancellationToken cancel = default)
    {
        var source = session.Capabilities.Sources.First(s => s.Kind == kind);
        await session.SelectSourceAsync(source.Name, cancel);
        var pages = new List<ScannedImage>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var page in session.ScanAsync(new ScanSettings(source.Name, dpi, mode, width, height), null, cancel))
        {
            pages.Add(page);
            _output.WriteLine($"  {kind} {mode} {dpi} dpi: page {pages.Count} {page.Width} × {page.Height} × {page.Channels}, {watch.ElapsedMilliseconds} ms");
            // only for the printed test sheets: where their codes lie, and the scan as PNG into a folder of one's own (never private paper)
            if (Environment.GetEnvironmentVariable("PDFKNEAD_WIA_TESTSEITEN") == "1")
                foreach (var line in ScanTestSheets.Describe(page)) _output.WriteLine($"    {line}");
            if (Environment.GetEnvironmentVariable("PDFKNEAD_WIA_BILDER") is { Length: > 0 } folder && page.Channels == 1)
            {
                Directory.CreateDirectory(folder);
                using var file = File.Create(Path.Combine(folder, $"{Wanted?.Trim('(', ')')}-{kind}-{pages.Count}.png"));
                PdfKnead.Core.Imaging.PngEncoder.Encode(page.Width, page.Height, 8, PdfKnead.Core.Imaging.PngColorType.Gray,
                    (y, row) => page.Pixels.AsSpan(y * page.Width, page.Width).CopyTo(row), file);
            }
        }
        return pages;
    }

    /// <summary>A4 at <paramref name="dpi"/> in pixels, cut; the driver may take a pixel off.</summary>
    private static void AssertA4(ScannedImage page, int dpi, int channels)
    {
        Assert.Equal((channels, dpi), (page.Channels, page.Dpi));
        // a glass a hair under 297 mm (11 690 thousandths of an inch) gives 3 rows less at 1200 dpi: a few pixels per 300 dpi
        var slack = Math.Max(2, dpi / 300);
        Assert.InRange(page.Width, (int)(210 / 25.4 * dpi) - slack, (int)(210 / 25.4 * dpi));
        Assert.InRange(page.Height, (int)(297 / 25.4 * dpi) - slack, (int)(297 / 25.4 * dpi));
    }

    [Fact]
    public async Task RealDevice_Flatbed_ColourGrayAndResolutions()
    {
        if (!OperatingSystem.IsWindows() || Wanted is not { Length: > 0 } || Scan != "flachbett") return;
        var (_, session) = await OpenWanted();
        await using var __ = session;
        AssertA4(Assert.Single(await ScanWith(session, ScanSourceKind.Flatbed, 150, ScanColorMode.Gray)), 150, 1);
        AssertA4(Assert.Single(await ScanWith(session, ScanSourceKind.Flatbed, 300, ScanColorMode.Color)), 300, 3);
        // the highest resolution the device offers, grey (in colour a page of A4 at 1200 dpi has 420 MB)
        var highest = session.Capabilities.Resolutions.Max();
        AssertA4(Assert.Single(await ScanWith(session, ScanSourceKind.Flatbed, highest, ScanColorMode.Gray)), highest, 1);
    }

    private static string DuplexFronts => Path.Combine(Path.GetTempPath(), "pdf-knead-wia-beidseitig-vorderseiten.txt");

    [Fact]
    public async Task RealDevice_ManualDuplex_1_Fronts()
    {
        // both sides by turning (ScanTestSheets(6) printed on both sides): first the fronts, their codes kept for the second step
        if (!OperatingSystem.IsWindows() || Wanted is not { Length: > 0 } || Scan != "beidseitig-vorne") return;
        var (_, session) = await OpenWanted();
        await using var __ = session;
        var read = (await ScanWith(session, ScanSourceKind.Feeder, 150, ScanColorMode.Gray)).Select(ScanTestSheets.Identify).ToList();
        foreach (var r in read) _output.WriteLine($"  front: test page {r?.Page}/{r?.Count}, upright {r?.Upright}");
        Assert.All(read, r => Assert.NotNull(r));
        File.WriteAllLines(DuplexFronts, read.Select(r => $"{r!.Value.Page};{r.Value.Upright}"));
    }

    [Fact]
    public async Task RealDevice_ManualDuplex_2_BacksInOrder()
    {
        // the stack turned over: the backs, put between the fronts as the scan window does (ManualDuplexOrder) — every page in order, upright
        if (!OperatingSystem.IsWindows() || Wanted is not { Length: > 0 } || Scan != "beidseitig-hinten") return;
        var fronts = File.ReadAllLines(DuplexFronts).Select(l => l.Split(';')).Select(p => (Page: int.Parse(p[0]), Upright: bool.Parse(p[1]))).ToList();
        var (_, session) = await OpenWanted();
        await using var __ = session;
        var read = (await ScanWith(session, ScanSourceKind.Feeder, 150, ScanColorMode.Gray)).Select(ScanTestSheets.Identify).ToList();
        foreach (var r in read) _output.WriteLine($"  back: test page {r?.Page}/{r?.Count}, upright {r?.Upright}");
        Assert.All(read, r => Assert.NotNull(r));
        var backs = read.Select(r => (r!.Value.Page, r.Value.Upright)).ToList();
        var order = ManualDuplexOrder.Interleave(fronts, backs);
        _output.WriteLine($"  order: {string.Join(" ", order.Select(p => p.Page + (p.Upright ? "" : "↓")))}");
        Assert.Equal(Enumerable.Range(1, fronts.Count + backs.Count), order.Select(p => p.Page));
        Assert.All(order, p => Assert.True(p.Upright, $"page {p.Page} came upside down"));
    }

    [Fact]
    public async Task RealDevice_Wia2Refused_TheSameDeviceThroughWia1()
    {
        // a driver that refuses the transfer of WIA 2.0 (E_INVALIDARG, taken as refused here): the session scans through WIA 1.0, also the
        // next scan; with a device of the WIA 2.0 device manager (WSD), the glass may be empty
        if (!OperatingSystem.IsWindows() || Wanted is not { Length: > 0 } || Scan != "rueckfall") return;
        WiaSession.ForceLegacyForTests = true;
        try
        {
            var (_, session) = await OpenWanted();
            await using var __ = session;
            var dpi = session.Capabilities.ClosestResolution(150);
            AssertA4(Assert.Single(await ScanWith(session, ScanSourceKind.Flatbed, 150, ScanColorMode.Gray)), dpi, 1);
            AssertA4(Assert.Single(await ScanWith(session, ScanSourceKind.Flatbed, 150, ScanColorMode.Gray)), dpi, 1);
        }
        finally
        {
            WiaSession.ForceLegacyForTests = false;
        }
    }

    [Fact]
    public async Task RealDevice_Flatbed_TestSheetUpright()
    {
        // a printed test sheet (ScanTestSheets) on the glass, face down at the corner mark: read back upright, not mirrored
        if (!OperatingSystem.IsWindows() || Wanted is not { Length: > 0 } || Scan != "testseite") return;
        var (_, session) = await OpenWanted();
        await using var __ = session;
        var page = Assert.Single(await ScanWith(session, ScanSourceKind.Flatbed, 200, ScanColorMode.Gray));
        foreach (var line in ScanTestSheets.Describe(page)) _output.WriteLine($"    {line}");
        var read = ScanTestSheets.Identify(page);
        Assert.NotNull(read);
        Assert.True(read.Value.Upright, "the code „oben links“ is not at the top left: turned or mirrored");
    }

    [Fact]
    public async Task RealDevice_Feeder_EveryPage_ThenEmpty()
    {
        if (!OperatingSystem.IsWindows() || Wanted is not { Length: > 0 } || Scan != "einzug") return;
        var (_, session) = await OpenWanted();
        await using var __ = session;
        var pages = await ScanWith(session, ScanSourceKind.Feeder, 200, ScanColorMode.Gray);
        Assert.True(pages.Count >= 2, $"{pages.Count} pages — put at least two sheets into the feeder");
        // at the offered resolution closest to 200 dpi (WSD on the test device: 75/150/300 — 150)
        var dpi = session.Capabilities.ClosestResolution(200);
        Assert.All(pages, p => AssertA4(p, dpi, 1));
        // the printed test sheets (ScanTestSheets, page 1 on top, face up): every page once, in order, upright
        if (Environment.GetEnvironmentVariable("PDFKNEAD_WIA_TESTSEITEN") == "1")
        {
            var read = pages.Select(ScanTestSheets.Identify).ToList();
            foreach (var r in read) _output.WriteLine($"  test sheet {r?.Page}/{r?.Count}, upright {r?.Upright}");
            Assert.All(read, r => Assert.NotNull(r));
            Assert.Equal(Enumerable.Range(1, read[0]!.Value.Count), read.Select(r => r!.Value.Page));
            Assert.All(read, r => Assert.True(r!.Value.Upright));
        }
        // the feeder is empty now: „no paper“, not a page and not a hang
        var empty = await Assert.ThrowsAsync<ScanException>(() => ScanWith(session, ScanSourceKind.Feeder, 200, ScanColorMode.Gray));
        Assert.Equal(ScanErrorKind.NoPaper, empty.Kind);
    }

    [Fact]
    public async Task RealDevice_Feeder_Empty()
    {
        if (!OperatingSystem.IsWindows() || Wanted is not { Length: > 0 } || Scan != "leer") return;
        var (_, session) = await OpenWanted();
        await using var __ = session;
        var empty = await Assert.ThrowsAsync<ScanException>(() => ScanWith(session, ScanSourceKind.Feeder, 150, ScanColorMode.Gray));
        Assert.Equal(ScanErrorKind.NoPaper, empty.Kind);
    }

    [Fact]
    public async Task RealDevice_Flatbed_CancelInTheMiddle_SessionStillWorks()
    {
        if (!OperatingSystem.IsWindows() || Wanted is not { Length: > 0 } || Scan != "abbrechen") return;
        var (_, session) = await OpenWanted();
        await using var __ = session;
        // 600 dpi in colour takes long enough over the network to be stopped in the middle
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScanWith(session, ScanSourceKind.Flatbed, 600, ScanColorMode.Color, cancel: stop.Token));
        _output.WriteLine($"  stopped after {watch.ElapsedMilliseconds} ms");
        // WIA 2.0 (WSD) stops within seconds; Windows' eSCL driver under WIA 1.0 calls back only when the device has scanned the page (31 s
        // here) — in the program the helper is ended 15 s after a cancel that does not stop (ScanHelperBackend.CancelLimit), not in this test
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
        // the device is free again: the next scan works (at the offered resolution closest to 100 dpi)
        var dpi = session.Capabilities.ClosestResolution(100);
        AssertA4(Assert.Single(await ScanWith(session, ScanSourceKind.Flatbed, 100, ScanColorMode.Gray)), dpi, 1);
    }
}
