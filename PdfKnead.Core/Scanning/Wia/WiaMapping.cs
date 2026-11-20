using System.Text.RegularExpressions;

namespace PdfKnead.Core.Scanning.Wia;

/// <summary>
/// WIA 2.0 mapped onto the scanner interface, without COM (tested with property tables): devices, sources, capabilities, applying the
/// settings, and the HRESULTs. Values from the Windows SDK (see <see cref="Wia"/>); what drivers really report was read from the class
/// drivers of Windows for eSCL and WSD (KONZEPT §5.25, S8).
/// </summary>
internal static partial class WiaMapping
{
    /// <summary>The way a device is reached, from its PnP id: Windows' class drivers for eSCL and WSD, else null (a vendor's own driver).</summary>
    /// <param name="pnpId">WIA_DIP_PNP_ID.</param>
    /// <returns>„eSCL“, „WSD“ or null.</returns>
    public static string? WayOf(string? pnpId)
    {
        var id = pnpId?.ToLowerInvariant() ?? "";
        if (id.Contains("#escl#") || id.Contains("\\escl\\")) return "eSCL";
        if (id.Contains("dafwsdprovider") || id.Contains("#wsd#")) return "WSD";
        return null;
    }

    /// <summary>The first UUID in a PnP id: the same physical network device has it under both class drivers.</summary>
    public static string? UuidOf(string? pnpId) =>
        pnpId is not null && Uuid().Match(pnpId) is { Success: true } m ? m.Value.ToLowerInvariant() : null;

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Uuid();

    /// <summary>
    /// A device from its device information, or null for cameras and video devices. The type is in the high word of WIA_DIP_DEV_TYPE
    /// (GET_STIDEVICE_TYPE, Sti.h); Windows' eSCL class driver reports 0xFFFF there, not 1 — so only cameras and video are left out, never
    /// „not exactly a scanner“. Windows' class drivers report „Microsoft“ as the vendor: that is the driver, not the device, and is left out.
    /// </summary>
    /// <param name="info">The device information.</param>
    /// <param name="uuid">The UUID from the PnP id, for finding the same device under two drivers.</param>
    /// <returns>The device, or null.</returns>
    public static ScanDevice? DeviceOf(IWiaProperties info, out string? uuid)
    {
        uuid = null;
        if (info.Text(Wia.DipDevId) is not { Length: > 0 } id) return null;
        var type = (info.Int(Wia.DipDevType) ?? 0) >>> 16;
        if (type is Wia.StiCamera or Wia.StiVideo) return null;
        var pnp = info.Text(Wia.DipPnpId);
        uuid = UuidOf(pnp);
        var name = info.Text(Wia.DipDevName)?.Trim() is { Length: > 0 } n ? n : info.Text(Wia.DipDevDesc)?.Trim() ?? "";
        var vendor = info.Text(Wia.DipVendDesc)?.Trim() ?? "";
        if (vendor.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) vendor = "";
        var description = info.Text(Wia.DipDevDesc)?.Trim() ?? "";
        return new ScanDevice(id, vendor, name, description.Equals(name, StringComparison.OrdinalIgnoreCase) ? "" : description, WayOf(pnp));
    }

    /// <summary>
    /// The devices in the order shown: the same physical device (same UUID) together, reached through eSCL first (it reports what the device
    /// offers, on a test device up to 1200 dpi), then a vendor's driver, then WSD (on the same device only up to 300 dpi); otherwise as found.
    /// </summary>
    public static List<ScanDevice> Order(IEnumerable<(ScanDevice Device, string? Uuid)> found)
    {
        var list = found.ToList();
        int Group(int i) => list[i].Uuid is { } u ? list.FindIndex(x => x.Uuid == u) : i;
        return [.. list.Select((x, i) => (x.Device, Group: Group(i), Index: i)).OrderBy(x => x.Group).ThenBy(x => RankOf(x.Device)).ThenBy(x => x.Index)
            .Select(x => x.Device)];
    }

    /// <summary>
    /// One entry per physical device (the same UUID in the PnP id): the best way gives it — eSCL (reports what the device offers, on a test
    /// device 1200 instead of 300 dpi), then a vendor's driver, then WSD (after a scan busy for seconds, Fehlergedächtnis 853) — and the other
    /// ways become the ones to fall back on when it cannot be opened (Festlegung: the program chooses the driver itself).
    /// </summary>
    public static List<ScanDevice> Combine(IEnumerable<(ScanDevice Device, string? Uuid)> found) =>
        ScanDevice.Combine(found.Select(x => (x.Device, x.Uuid, RankOf(x.Device))));

    private static int RankOf(ScanDevice device) => device.Via switch { "eSCL" => 0, null => 1, _ => 2 };

    /// <summary>
    /// Opens a device by its ways in turn, the best first: the next way only when the one before could not reach the device. A state of the
    /// device (busy, cover open, jam …) is the answer at once — every way would meet it.
    /// </summary>
    /// <typeparam name="T">What opening gives.</typeparam>
    /// <param name="ids">The ways, best first.</param>
    /// <param name="tryOpen">Opens one way: what it gives, or null with the HRESULT.</param>
    /// <returns>What was opened, or null with the HRESULT of the best way (or of the state met).</returns>
    public static (T? Opened, int Hr) OpenFirst<T>(IEnumerable<string> ids, Func<string, (T? Opened, int Hr)> tryOpen) where T : class
    {
        int? first = null;
        foreach (var id in ids)
        {
            var (opened, hr) = tryOpen(id);
            if (opened is not null) return (opened, 0);
            if (ErrorOf(hr) is { Kind: not (ScanErrorKind.Other or ScanErrorKind.Offline) }) return (null, hr);
            first ??= hr;
        }
        return (null, first ?? Wia.NoDeviceAvailable);
    }

    /// <summary>The kind of source an item stands for: by its category (WIA_IPA_ITEM_CATEGORY), else by its name; null for items that are not
    /// a source of their own (the front and back of a feeder, „automatic“, the root).</summary>
    public static ScanSourceKind? SourceKindOf(Guid? category, string? name)
    {
        if (category == Wia.CategoryFlatbed) return ScanSourceKind.Flatbed;
        if (category == Wia.CategoryFeeder) return ScanSourceKind.Feeder;
        if (category == Wia.CategoryFilm) return ScanSourceKind.Other;
        if (category == Wia.CategoryFeederFront || category == Wia.CategoryFeederBack || category == Wia.CategoryAuto || category == Wia.CategoryRoot)
            return null;
        // no or an unknown category: the name, as drivers usually call their items „Flatbed“ and „Feeder“
        var kind = ScanSource.FromName(name ?? "").Kind;
        return kind == ScanSourceKind.FeederDuplex ? ScanSourceKind.Feeder : kind;
    }

    /// <summary>The names the sources get: what <see cref="ScanSource.FromName"/> recognises.</summary>
    public const string FlatbedName = "Flatbed", FeederName = "Feeder", FeederDuplexName = "Feeder Duplex";

    /// <summary>Whether the feeder can scan both sides: the allowed bits of its document handling select, else the capabilities of the device.</summary>
    public static bool FeederHasDuplex(IWiaProperties feeder, IWiaProperties root) =>
        feeder.Allowed(Wia.IpsDocumentHandlingSelect) is { Kind: WiaAllowedKind.Flags } select
            ? (select.Bits & Wia.Duplex) != 0
            : ((feeder.Int(Wia.DpsDocumentHandlingCapabilities) ?? root.Int(Wia.DpsDocumentHandlingCapabilities) ?? 0) & Wia.Duplex) != 0;

    /// <summary>What an item offers: resolutions, colour modes and the largest area.</summary>
    /// <param name="item">The item (flatbed or feeder).</param>
    /// <param name="root">The device, for the sizes older drivers report there only.</param>
    /// <param name="kind">The kind of source.</param>
    /// <param name="sources">All sources of the device.</param>
    /// <param name="source">The selected one.</param>
    public static ScanCapabilities CapabilitiesOf(IWiaProperties item, IWiaProperties root, ScanSourceKind kind, IReadOnlyList<ScanSource> sources,
        ScanSource? source)
    {
        var resolutions = item.Allowed(Wia.IpsXres) switch
        {
            { Kind: WiaAllowedKind.List } list => ResolutionChoices.FromList(list.Values),
            { Kind: WiaAllowedKind.Range } range => ResolutionChoices.FromRange(range.Min, range.Max, range.Step),
            _ => item.Int(Wia.IpsXres) is { } current ? ResolutionChoices.FromList([current]) : [],
        };
        var types = item.Allowed(Wia.IpaDatatype) is { Kind: WiaAllowedKind.List } t ? t.Values : item.Int(Wia.IpaDatatype) is { } d ? [d] : [];
        var modes = types.Select(ModeOf).OfType<ScanColorMode>().Distinct().Order().ToList();
        var (width, height) = MaxSizeOf(item, root, kind);
        return new ScanCapabilities(sources, source, resolutions, modes, width * 25.4 / 1000, height * 25.4 / 1000);
    }

    /// <summary>The colour mode of a WIA data type: threshold is black and white; dithering imitates grey with dots and is left out.</summary>
    public static ScanColorMode? ModeOf(int dataType) => dataType switch
    {
        Wia.DataColor => ScanColorMode.Color,
        Wia.DataGrayscale => ScanColorMode.Gray,
        Wia.DataThreshold => ScanColorMode.BlackWhite,
        _ => null,
    };

    private static int DataTypeOf(ScanColorMode mode) => mode switch
    {
        ScanColorMode.Color => Wia.DataColor,
        ScanColorMode.Gray => Wia.DataGrayscale,
        _ => Wia.DataThreshold,
    };

    /// <summary>The largest area in thousandths of an inch: the item's maximum size (WIA 2.0), else the bed or sheet feed size of the device.</summary>
    public static (int Width, int Height) MaxSizeOf(IWiaProperties item, IWiaProperties root, ScanSourceKind kind)
    {
        var feeder = kind is ScanSourceKind.Feeder or ScanSourceKind.FeederDuplex;
        var width = item.Int(Wia.IpsMaxHorizontalSize) is > 0 and var w ? w
            : root.Int(feeder ? Wia.DpsHorizontalSheetFeedSize : Wia.DpsHorizontalBedSize) ?? 0;
        var height = item.Int(Wia.IpsMaxVerticalSize) is > 0 and var h ? h
            : root.Int(feeder ? Wia.DpsVerticalSheetFeedSize : Wia.DpsVerticalBedSize) ?? 0;
        return (Math.Max(0, width), Math.Max(0, height));
    }

    /// <summary>
    /// Applies the settings to an item, in the order the properties depend on each other: document handling and pages, data type, resolution
    /// (read back), format, then the area in pixels of the resolution set. Every property alone: one the driver lacks or refuses is noted in
    /// <paramref name="notes"/> and skipped, never fatal. The bit depth is not set (the data type implies it; drivers refuse other depths).
    /// </summary>
    /// <param name="item">The item to scan with.</param>
    /// <param name="root">The device.</param>
    /// <param name="kind">The kind of source (flatbed, feeder, feeder both sides).</param>
    /// <param name="settings">What to scan with.</param>
    /// <param name="capabilities">What the item offers (for the closest resolution).</param>
    /// <param name="notes">Receives what was refused or adjusted.</param>
    /// <param name="legacy">WIA 1.0: the source and the pages are set at the device (one page per transfer), the image comes in bands.</param>
    /// <returns>The resolution the item uses now.</returns>
    public static int Apply(IWiaProperties item, IWiaProperties root, ScanSourceKind kind, ScanSettings settings, ScanCapabilities capabilities,
        ICollection<string> notes, bool legacy = false)
    {
        void SetOn(IWiaProperties target, int id, int value)
        {
            if (target.Allowed(id) is not { } allowed) return;   // the item does not have it
            if (!allowed.Writable) return;
            if (!allowed.Allows(value))
            {
                notes.Add($"WIA {id}: {value} not allowed");
                return;
            }
            if (target.Set(id, value) is var hr and < 0) notes.Add($"WIA {id} = {value}: 0x{hr:X8}");
        }
        void Set(int id, int value) => SetOn(item, id, value);

        var feeder = kind is ScanSourceKind.Feeder or ScanSourceKind.FeederDuplex;
        if (legacy)
        {
            // WIA 1.0: the source at the device (feeder, with both sides, or flatbed); the feeder all pages in one transfer, divided by „new
            // page“ (WIA_DPS_PAGES 0, as the driver documentation describes it) — drivers that give one page per transfer are read again
            // until the feeder reports no more paper
            SetOn(root, Wia.IpsDocumentHandlingSelect, SelectOf(kind));
            SetOn(root, Wia.IpsPages, feeder ? Wia.AllPages : 1);
        }
        else if (feeder)
        {
            // both sides, or the front only — said explicitly, as drivers keep the last value
            Set(Wia.IpsDocumentHandlingSelect, kind == ScanSourceKind.FeederDuplex ? Wia.Duplex : Wia.FrontOnly);
            Set(Wia.IpsPages, Wia.AllPages);
        }
        else
        {
            Set(Wia.IpsPages, 1);
        }

        var dataType = DataTypeOf(settings.Mode);
        if (item.Allowed(Wia.IpaDatatype) is { Kind: WiaAllowedKind.List } types && !types.Values.Contains(dataType))
            dataType = types.Values.Contains(Wia.DataGrayscale) ? Wia.DataGrayscale : types.Values.FirstOrDefault(v => ModeOf(v) is not null, dataType);
        Set(Wia.IpaDatatype, dataType);

        var dpi = capabilities.ClosestResolution(settings.Dpi);
        Set(Wia.IpsXres, dpi);
        Set(Wia.IpsYres, dpi);
        var actual = item.Int(Wia.IpsXres) is > 0 and var x ? x : dpi;
        if (actual != dpi) notes.Add($"WIA resolution {dpi} -> {actual}");

        // WIA 1.0: the image in bands to the program (the formats allowed depend on the medium, so it comes first)
        if (legacy) Set(Wia.IpaTymed, Wia.TymedCallback);
        // BMP without compression, where the driver allows choosing (in bands also the memory bitmap); otherwise the image is recognised by
        // its first bytes
        if (item.Allowed(Wia.IpaFormat) is { Writable: true, Guids: { } formats }
            && (formats.Contains(Wia.FormatBmp) ? Wia.FormatBmp : legacy && formats.Contains(Wia.FormatMemoryBmp) ? Wia.FormatMemoryBmp : (Guid?)null) is { } format
            && item.Guid(Wia.IpaFormat) != format && item.Set(Wia.IpaFormat, format) is var fhr and < 0)
            notes.Add($"WIA format {format}: 0x{fhr:X8}");
        if (item.Int(Wia.IpaCompression) is { } compression && compression != 0) Set(Wia.IpaCompression, 0);

        ApplyArea(item, root, kind, settings, actual, notes);
        return actual;
    }

    /// <summary>
    /// The area in pixels at <paramref name="dpi"/>: cut, not rounded, and kept inside the item's limits. Narrower paper in the feeder is
    /// placed as the feeder aligns it (WIA_IPS_SHEET_FEEDER_REGISTRATION: left, centred or right); the flatbed always from the corner.
    /// The extent is set before the offset (with the offset at 0 every extent is allowed), the offset after it.
    /// </summary>
    private static void ApplyArea(IWiaProperties item, IWiaProperties root, ScanSourceKind kind, ScanSettings settings, int dpi, ICollection<string> notes)
    {
        int Write(int id, int value)
        {
            if (item.Allowed(id) is not { Writable: true } allowed) return item.Int(id) ?? value;
            var clamped = allowed.Kind == WiaAllowedKind.Range ? Math.Clamp(value, allowed.Min, allowed.Max) : value;
            if (item.Set(id, clamped) is var hr and < 0) notes.Add($"WIA {id} = {clamped}: 0x{hr:X8}");
            return item.Int(id) ?? clamped;
        }

        var (maxWidth, maxHeight) = MaxSizeOf(item, root, kind);
        Write(Wia.IpsXpos, 0);
        Write(Wia.IpsYpos, 0);
        int Pixels(double mm) => (int)(mm / 25.4 * dpi);
        int Limit(int id, int thousandths) =>
            item.Allowed(id) is { Kind: WiaAllowedKind.Range, Max: > 0 } range ? range.Max : (int)((long)thousandths * dpi / 1000);
        var fullWidth = Limit(Wia.IpsXextent, maxWidth);
        var fullHeight = Limit(Wia.IpsYextent, maxHeight);
        var width = settings.WidthMm is { } w && fullWidth > 0 ? Math.Min(Pixels(w), fullWidth) : fullWidth;
        var height = settings.HeightMm is { } h && fullHeight > 0 ? Math.Min(Pixels(h), fullHeight) : fullHeight;
        if (width > 0)
        {
            var setWidth = Write(Wia.IpsXextent, width);
            var feeder = kind is ScanSourceKind.Feeder or ScanSourceKind.FeederDuplex;
            var offset = !feeder ? 0 : (item.Int(Wia.IpsSheetFeederRegistration) ?? root.Int(Wia.IpsSheetFeederRegistration)) switch
            {
                Wia.Centered => (fullWidth - setWidth) / 2,
                Wia.RightJustified => fullWidth - setWidth,
                _ => 0,
            };
            if (offset > 0)
            {
                Write(Wia.IpsXpos, offset);
                // some drivers read the extent as the right edge and shorten it when the offset moves: noted, so it shows in the log
                if (item.Int(Wia.IpsXextent) is { } after && after != setWidth) notes.Add($"WIA extent {setWidth} -> {after} after offset {offset}");
            }
        }
        if (height > 0) Write(Wia.IpsYextent, height);
    }

    /// <summary>The document handling select of WIA 1.0 for a source, set at the device: feeder (with both sides) or flatbed.</summary>
    public static int SelectOf(ScanSourceKind kind) => kind switch
    {
        ScanSourceKind.FeederDuplex => Wia.Feeder | Wia.Duplex,
        ScanSourceKind.Feeder => Wia.Feeder,
        _ => Wia.Flatbed,
    };

    /// <summary>
    /// The sources of a WIA 1.0 device, from its document handling capabilities (FEED, FLAT, DUP): it has one item to scan with, the source
    /// is chosen at the device. Without the property: the flatbed.
    /// </summary>
    public static List<ScanSource> LegacySourcesOf(IWiaProperties root)
    {
        var capabilities = root.Int(Wia.DpsDocumentHandlingCapabilities) ?? Wia.Flatbed;
        var sources = new List<ScanSource>();
        if ((capabilities & Wia.Flatbed) != 0 || (capabilities & Wia.Feeder) == 0) sources.Add(new ScanSource(FlatbedName, ScanSourceKind.Flatbed));
        if ((capabilities & Wia.Feeder) != 0)
        {
            sources.Add(new ScanSource(FeederName, ScanSourceKind.Feeder));
            if ((capabilities & Wia.Duplex) != 0) sources.Add(new ScanSource(FeederDuplexName, ScanSourceKind.FeederDuplex));
        }
        return sources;
    }

    /// <summary>The waits before a new attempt at a transfer that brought nothing: 15 seconds in all.</summary>
    internal static readonly TimeSpan[] RetryWaits = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)];

    /// <summary>
    /// How long to wait before trying a transfer again that failed before any data came, or null to give up. Windows' class driver for WSD
    /// answers E_FAIL instead of „busy“ for some seconds after a scan (the device is still finishing, Fehlergedächtnis 853); busy and warming
    /// up are tried again the same way. Only before any data: then nothing was scanned and nothing is lost or scanned twice. An empty feeder
    /// reports „no paper“ and is never tried again.
    /// </summary>
    /// <param name="hr">The HRESULT of the transfer.</param>
    /// <param name="attempt">The attempts so far (0 after the first).</param>
    /// <param name="feeder">Whether the feeder was scanned: there an HTTP 409 means „empty“ and is not tried again.</param>
    /// <returns>The wait, or null.</returns>
    public static TimeSpan? RetryAfter(int hr, int attempt, bool feeder = false) =>
        (hr is Wia.EFail or Wia.ErrorBusy or Wia.ErrorWarmingUp or Wia.HttpServiceUnavailable || hr == Wia.HttpConflict && !feeder)
        && attempt < RetryWaits.Length ? RetryWaits[attempt] : null;

    /// <summary>
    /// Whether a WIA 2.0 transfer is to be done again through WIA 1.0: the driver refused it as „invalid argument“ before any image came —
    /// drivers that do not get on with the transfer of WIA 2.0.
    /// </summary>
    public static bool FallsBackToLegacy(int hr, bool started) => hr == Wia.EInvalidArg && !started;

    /// <summary>
    /// Whether the HRESULT of a finished transfer means the feeder ran empty after pages had come — the normal end, not an error: END_OF_MEDIA,
    /// PAPER_EMPTY, and what the class driver for eSCL passes on instead (HTTP 409 Conflict or 404 Not Found for the next document,
    /// Fehlergedächtnis 854).
    /// </summary>
    public static bool IsEndOfFeeder(int hr, bool feeder, int pages) =>
        hr == Wia.StatusEndOfMedia || feeder && pages > 0 && hr is Wia.ErrorPaperEmpty or Wia.HttpConflict or Wia.HttpNotFound;

    /// <summary>The error for the user from an HRESULT, knowing the source: an HTTP 409 or 404 of the feeder is an empty feeder.</summary>
    public static ScanException ErrorOf(int hr, bool feeder) =>
        feeder && hr is Wia.HttpConflict or Wia.HttpNotFound ? ErrorOf(Wia.ErrorPaperEmpty) : ErrorOf(hr);

    /// <summary>The error for the user from an HRESULT of WIA (WiaDef.h) or COM.</summary>
    public static ScanException ErrorOf(int hr) => hr switch
    {
        Wia.HttpConflict or Wia.HttpServiceUnavailable =>
            new ScanException(Tr.T("Der Scanner ist gerade belegt — vielleicht von einem anderen Programm."), ScanErrorKind.Busy),
        Wia.HttpRequestTimeout or Wia.HttpGatewayTimeout => new ScanException(Tr.T("Die Verbindung zum Scanner ist gestört.")),
        Wia.ErrorPaperEmpty => new ScanException(Tr.T("Im Einzug liegt kein Papier."), ScanErrorKind.NoPaper),
        Wia.ErrorPaperJam or Wia.ErrorMultiFeed => new ScanException(Tr.T("Papierstau im Scanner."), ScanErrorKind.Jammed),
        Wia.ErrorPaperProblem => new ScanException(Tr.T("Mit dem Papier im Scanner stimmt etwas nicht — bitte nachsehen."), ScanErrorKind.Jammed),
        Wia.ErrorCoverOpen => new ScanException(Tr.T("Eine Abdeckung des Scanners ist offen."), ScanErrorKind.CoverOpen),
        Wia.ErrorBusy or Wia.ErrorDeviceLocked => new ScanException(Tr.T("Der Scanner ist gerade belegt — vielleicht von einem anderen Programm."), ScanErrorKind.Busy),
        Wia.ErrorOffline or Wia.NoDeviceAvailable or Wia.ErrorItemDeleted =>
            new ScanException(Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline),
        Wia.ErrorWarmingUp or Wia.ErrorLampOff => new ScanException(Tr.T("Der Scanner wärmt sich noch auf — bitte gleich noch einmal scannen."), ScanErrorKind.Busy),
        Wia.ErrorUserIntervention => new ScanException(Tr.T("Der Scanner braucht eine Handlung am Gerät — bitte dort nachsehen."), ScanErrorKind.Other),
        Wia.ErrorDeviceCommunication or Wia.RpcServerUnavailable => new ScanException(Tr.T("Die Verbindung zum Scanner ist gestört.")),
        Wia.ErrorIncorrectHardwareSetting or Wia.ErrorInvalidCommand or Wia.EInvalidArg => new ScanException(Tr.T("Der Scanner lehnt die Einstellung ab.")),
        Wia.ErrorExceptionInDriver or Wia.ErrorInvalidDriverResponse =>
            new ScanException(Tr.T("Der Scanner-Treiber meldet einen inneren Fehler ({0}).", $"0x{hr:X8}")),
        Wia.EOutOfMemory => new ScanException(Tr.T("Dem Scanner-Treiber fehlt Arbeitsspeicher.")),
        Wia.EServiceNotActive => new ScanException(ServiceHint, ScanErrorKind.InterfaceMissing),
        _ => new ScanException(Tr.T("Der Scanner meldet einen Fehler ({0}).", $"0x{hr:X8}")),
    };

    /// <summary>What to tell the user when the scanner service of Windows does not run.</summary>
    public static string ServiceHint =>
        Tr.T("Der Windows-Dienst „Windows-Bilderfassung (WIA)“ läuft nicht. Bitte in den Diensten von Windows starten und auf „Automatisch“ stellen.");
}
